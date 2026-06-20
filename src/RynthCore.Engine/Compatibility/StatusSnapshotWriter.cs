// ============================================================================
//  RynthCore.Engine - StatusSnapshotWriter.cs                  [status-export]
//
//  Opt-in, default-OFF diagnostics export. Once per second (driven from
//  HeartbeatLogger) this writes a small LOCAL JSON file —
//      C:\Games\RynthCore\Logs\status\RynthCore.<pid>.status.json
//  — carrying the same fields as the heartbeat line PLUS the RynthAi bot
//  snapshot (macro running / profile / vitals), so an out-of-process reader
//  can tell at a glance whether this client is running and actively botting.
//
//  PRIVACY / SAFETY:
//    * Gated behind EngineSettings.EnableStatusExport, which DEFAULTS TO FALSE.
//      A client that doesn't opt in gets zero new behaviour.
//    * This code NEVER opens a socket. It only writes a local file under the
//      existing Logs directory. Nothing is sent anywhere from inside the game.
//    * The optional RynthCore.StatusAgent (separate exe, on the user's own PC)
//      is what reads these files and forwards them to a user-configured
//      endpoint. The engine has no knowledge of, and no link to, any network.
//
//  REMOVAL: delete this file, the single call in HeartbeatLogger.Run, the
//  EnableStatusExport member in EngineSettings, the [status-export] region in
//  LogPaths.cs, and the LastAccount/Character/ServerName members in
//  SessionStateRegistry. Grep "[status-export]" to find every touch point.
// ============================================================================

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.Compatibility;

internal static class StatusSnapshotWriter
{
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetSnapshotJsonFn();

    private static GetSnapshotJsonFn? _getSnapshotJson;
    private static bool _bindAttempted;
    private static bool _firstWriteLogged;
    private static long _errorsLogged;

    /// <summary>
    /// Write this client's status snapshot. No-op (and never throws) when the
    /// feature is disabled. Called once/second from the heartbeat thread with
    /// values it already computed, so this adds one file write + one cheap
    /// plugin call per second.
    /// </summary>
    internal static void Write(
        long uptimeSeconds, int fps, int pluginTicksPerSec, long workingSetMb,
        bool inWorld, long queueDropped, long reconciles, long forceClears)
    {
        if (!EngineSettings.EnableStatusExport)
            return;

        try
        {
            string? botJson = TryGetBotSnapshotJson();
            byte[] payload = BuildJson(
                uptimeSeconds, fps, pluginTicksPerSec, workingSetMb,
                inWorld, queueDropped, reconciles, forceClears, botJson);
            WriteAtomic(payload);

            if (!_firstWriteLogged)
            {
                _firstWriteLogged = true;
                RynthLog.Info($"StatusSnapshotWriter: status export ON -> {LogPaths.StatusFilePath}");
            }
        }
        catch (Exception ex)
        {
            // Throttle: a persistent failure (e.g. read-only disk) must not spam
            // one ERR line/second into the per-client log.
            if (_errorsLogged < 3)
            {
                _errorsLogged++;
                RynthLog.Warn($"StatusSnapshotWriter: write failed ({ex.GetType().Name}: {ex.Message}).");
            }
        }
    }

    /// <summary>
    /// Pull the RynthAi plugin's snapshot JSON via its C export, the same way
    /// RynthAiPanel does. Returns the raw JSON string (validated parseable) or
    /// null when the plugin isn't loaded / hasn't produced a snapshot. The
    /// export is designed for off-main-thread polling (the Avalonia panel calls
    /// it ~30x/s), so calling it from the heartbeat thread is safe.
    /// </summary>
    private static string? TryGetBotSnapshotJson()
    {
        if (!EngineSettings.EnablePlugins)
            return null;

        if (!_bindAttempted || _getSnapshotJson == null)
        {
            _bindAttempted = true;
            try
            {
                foreach (LoadedPlugin plugin in PluginManager.Plugins)
                {
                    if (!plugin.DisplayName.Contains("RynthAi", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (plugin.ModuleHandle == IntPtr.Zero)
                        continue;

                    IntPtr addr = GetProcAddress(plugin.ModuleHandle, "RynthPluginGetSnapshotJson");
                    if (addr != IntPtr.Zero)
                        _getSnapshotJson = Marshal.GetDelegateForFunctionPointer<GetSnapshotJsonFn>(addr);
                    break;
                }
            }
            catch { _getSnapshotJson = null; }
        }

        if (_getSnapshotJson == null)
            return null;

        try
        {
            IntPtr ptr = _getSnapshotJson();
            if (ptr == IntPtr.Zero)
                return null;
            string json = Marshal.PtrToStringAnsi(ptr) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(json))
                return null;

            // Validate it parses before we embed it as a raw value — a malformed
            // string would otherwise throw inside Utf8JsonWriter.WriteRawValue and
            // lose the whole status file for that tick. Parsing an in-memory
            // string opens no file, so it can't recurse through DatFileShareHooks.
            using (JsonDocument.Parse(json)) { }
            return json;
        }
        catch
        {
            // Plugin unloaded mid-call / bad pointer / malformed JSON — emit the
            // status file without bot detail rather than dropping it entirely.
            _getSnapshotJson = null;
            _bindAttempted = false;   // allow a rebind on the next tick
            return null;
        }
    }

    private static byte[] BuildJson(
        long uptimeSeconds, int fps, int pluginTicksPerSec, long workingSetMb,
        bool inWorld, long queueDropped, long reconciles, long forceClears, string? botJson)
    {
        using var ms = new MemoryStream(2048);
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("schema", "rynthcore.client-status/1");
            w.WriteString("ts", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'"));
            w.WriteString("host", SafeMachineName());
            w.WriteNumber("pid", Environment.ProcessId);

            w.WriteString("account", SessionStateRegistry.LastAccountName ?? string.Empty);
            w.WriteString("character", SessionStateRegistry.LastCharacterName ?? string.Empty);
            w.WriteString("server", SessionStateRegistry.LastServerName ?? string.Empty);

            w.WriteNumber("uptimeSec", uptimeSeconds);
            w.WriteNumber("fps", fps);
            w.WriteNumber("pluginTicksPerSec", pluginTicksPerSec);
            w.WriteNumber("workingSetMB", workingSetMb);
            w.WriteBoolean("inWorld", inWorld);
            w.WriteNumber("queueDropped", queueDropped);
            w.WriteNumber("reconciles", reconciles);
            w.WriteNumber("forceClears", forceClears);

            if (botJson != null)
            {
                w.WritePropertyName("bot");
                w.WriteRawValue(botJson, skipInputValidation: false);
            }
            else
            {
                w.WriteNull("bot");
            }

            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Write to a temp file in the same directory, then atomically replace the
    /// live file — a reader (the agent) never sees a half-written snapshot.
    /// </summary>
    private static void WriteAtomic(byte[] payload)
    {
        LogPaths.EnsureStatusDirectory();
        string dest = LogPaths.StatusFilePath;
        string tmp = dest + ".tmp";
        File.WriteAllBytes(tmp, payload);
        try
        {
            File.Move(tmp, dest, overwrite: true);
        }
        catch
        {
            // Move can fail if a reader has the dest open without share-delete;
            // fall back to a direct overwrite so the snapshot still lands.
            try { File.WriteAllBytes(dest, payload); } finally { try { File.Delete(tmp); } catch { } }
        }
    }

    private static string SafeMachineName()
    {
        try { return Environment.MachineName; }
        catch { return string.Empty; }
    }
}
