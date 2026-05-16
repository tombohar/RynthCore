using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// Reads per-character OnLogin chat commands from the launcher's
/// LaunchContextRecord (written per-PID under
/// %APPDATA%\RynthCore\launch_contexts\) and dispatches them via
/// <see cref="ChatCommandDispatcher"/> after AC's
/// <c>SendLoginCompleteNotification</c> fires.
///
/// Timing model mirrors Thwargle's:
///   1. Wait for the engine's LoginComplete signal.
///   2. Sleep <c>OnLoginWaitMs</c> (default 3000 ms — the Thwargle default).
///   3. Dispatch each command in order with a small inter-command gap so AC's
///      chat parser has time to finish processing before the next arrives.
///
/// Single-shot per process: subscribes once, fires once. Logout/relogin within
/// the same client process is not handled here yet (would require
/// re-subscribing after LoginLifecycleHooks.ResetObservation).
/// </summary>
internal static class OnLoginCommandRunner
{
    /// <summary>Inter-command delay. AC's chat parser is synchronous on the
    /// main thread but the underlying network handlers benefit from breathing
    /// room; 250 ms keeps things human-paced and avoids any chance of the
    /// parser dropping a command queued behind a still-processing one.</summary>
    private const int InterCommandGapMs = 250;

    /// <summary>Fallback wait if the launcher provides 0 / unset value.</summary>
    private const int DefaultWaitMs = 3000;

    private static int _scheduled;
    private static string _statusMessage = "Not initialized.";

    public static string StatusMessage => _statusMessage;

    public static void Initialize()
    {
        // One-shot subscription. If LoginLifecycleHooks.ResetObservation gets
        // called (logout pipeline), the next LoginComplete will still fire
        // through this handler and we'll re-arm via Interlocked.Exchange.
        LoginLifecycleHooks.LoginComplete += OnLoginComplete;
        _statusMessage = "OnLogin command runner armed.";
        RynthLog.Verbose($"Compat: {_statusMessage}");
    }

    private static void OnLoginComplete()
    {
        // Guard: only schedule once per LoginComplete edge. If we ever support
        // mid-session relogin, ResetForRelogin should clear this flag.
        if (Interlocked.Exchange(ref _scheduled, 1) != 0)
            return;

        try
        {
            (List<string> commands, int waitMs) = ReadLaunchCommands();
            if (commands.Count == 0)
            {
                RynthLog.Verbose("OnLogin: no commands queued for this PID — skipping dispatch.");
                return;
            }

            int effectiveWait = waitMs > 0 ? waitMs : DefaultWaitMs;
            RynthLog.Info($"OnLogin: {commands.Count} command(s) scheduled, first dispatch in {effectiveWait}ms.");

            // Fire-and-forget background task. ChatCommandDispatcher.Dispatch
            // is thread-safe — it calls thiscall/cdecl AC functions through
            // function-pointer delegates, which AC's chat layer tolerates from
            // any thread because the underlying CM_Communication::Event_*
            // handlers post to the network queue rather than touching UI.
            _ = Task.Run(async () => await DispatchAsync(commands, effectiveWait));
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"OnLogin: scheduling failed - {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task DispatchAsync(List<string> commands, int waitMs)
    {
        try
        {
            await Task.Delay(waitMs);

            for (int i = 0; i < commands.Count; i++)
            {
                string line = commands[i];
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // Plugin-first dispatch lives inside ChatCommandDispatcher
                // itself — see its plugin pre-dispatch step. So we just call
                // the unified entry point here.
                bool ok = ChatCommandDispatcher.Dispatch(line);
                RynthLog.Info($"OnLogin: [{i + 1}/{commands.Count}] '{line}' dispatched={ok}.");

                if (i + 1 < commands.Count)
                    await Task.Delay(InterCommandGapMs);
            }
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"OnLogin: dispatch loop failed - {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static (List<string> commands, int waitMs) ReadLaunchCommands()
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RynthCore",
            "launch_contexts",
            $"launch_context_{Environment.ProcessId}.json");

        if (!File.Exists(path))
            return (new List<string>(), 0);

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(path));
            JsonElement root = doc.RootElement;

            int waitMs = 0;
            if (root.TryGetProperty("OnLoginWaitMs", out JsonElement wEl) && wEl.ValueKind == JsonValueKind.Number)
                waitMs = wEl.GetInt32();

            var commands = new List<string>();
            if (root.TryGetProperty("OnLoginCommands", out JsonElement cmdEl) && cmdEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in cmdEl.EnumerateArray())
                {
                    string? line = entry.GetString();
                    if (!string.IsNullOrWhiteSpace(line))
                        commands.Add(line);
                }
            }

            return (commands, waitMs);
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"OnLogin: launch context parse failed - {ex.Message}");
            return (new List<string>(), 0);
        }
    }
}
