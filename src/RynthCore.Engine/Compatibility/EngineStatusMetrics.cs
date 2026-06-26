// ============================================================================
//  RynthCore.Engine - EngineStatusMetrics.cs
//
//  A benign, generic "here are my own metrics" accessor. Each heartbeat tick
//  caches the engine's live metrics (UpdateMetrics); BuildEngineStatusJson
//  composes them — plus the main-thread-cached player stats and session
//  identity — into a small JSON object that a plugin can pull via the
//  GetEngineStatusJson host bridge. The engine NEVER writes a file, opens a
//  socket, or networks; it only hands a plugin its own numbers on request.
//
//  (The per-client status file and phone command drain now live entirely in the
//  private RynthCore.Plugin.RynthRemote plugin, so the public engine carries no
//  remote or networking code — just this benign metrics accessor.)
// ============================================================================

using System;
using System.IO;
using System.Text.Json;

namespace RynthCore.Engine.Compatibility;

internal static class EngineStatusMetrics
{
    // Latest heartbeat metrics, cached so the parameterless BuildEngineStatusJson
    // accessor (backing the GetEngineStatusJson host bridge) can compose without
    // the heartbeat thread's locals. Updated every heartbeat tick; ~1s staleness
    // is fine for a status pull.
    private static long _mUptimeSec, _mWorkingSetMb, _mQueueDropped, _mReconciles, _mForceClears;
    private static int _mFps, _mPluginTicksPerSec;
    private static bool _mInWorld;

    /// <summary>
    /// Cache this tick's engine metrics. Called once/second from the heartbeat
    /// thread with values it already computed — a few field writes, no I/O.
    /// </summary>
    internal static void UpdateMetrics(
        long uptimeSeconds, int fps, int pluginTicksPerSec, long workingSetMb,
        bool inWorld, long queueDropped, long reconciles, long forceClears)
    {
        _mUptimeSec = uptimeSeconds; _mFps = fps; _mPluginTicksPerSec = pluginTicksPerSec;
        _mWorkingSetMb = workingSetMb; _mInWorld = inWorld; _mQueueDropped = queueDropped;
        _mReconciles = reconciles; _mForceClears = forceClears;
    }

    // Per-process session baselines for the per-hour rates. Each client is its own
    // process, so these naturally reset per launch; we also reset on an XP drop
    // (relog / char swap).
    private static long _xpBaseline = -1;
    private static long _lumBaseline = -1;
    private static int _deathsBaseline = -1;
    private static DateTime _statsSessionStartUtc = DateTime.UtcNow;

    /// <summary>
    /// Compose the engine-side status fields as a JSON object string, from the
    /// latest cached heartbeat metrics. Backs the GetEngineStatusJson host bridge.
    /// Never throws; returns "{}" on failure. Safe off the main thread — player
    /// stats come from a main-thread-cached source.
    /// </summary>
    internal static string BuildEngineStatusJson()
    {
        try
        {
            using var ms = new MemoryStream(1024);
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                WriteEngineFields(w, _mUptimeSec, _mFps, _mPluginTicksPerSec, _mWorkingSetMb,
                                  _mInWorld, _mQueueDropped, _mReconciles, _mForceClears);
                w.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return "{}";
        }
    }

    // Writes the engine status fields into an already-open JSON object. The per-hour
    // baselines below have a single writer (the GetEngineStatusJson accessor path).
    private static void WriteEngineFields(
        Utf8JsonWriter w,
        long uptimeSeconds, int fps, int pluginTicksPerSec, long workingSetMb,
        bool inWorld, long queueDropped, long reconciles, long forceClears)
    {
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

        // Player stats, read on the main thread by PrefetchPlayerStats and served here off-thread
        // (the plugin pump can't read these live). deaths all-time; rates/session = whole-session.
        ClientObjectHooks.TryGetPlayerStats(out long xp, out long lum, out int deaths, out float vitae,
                                            out int encVal, out int encCap, out uint landcell,
                                            out float px, out float py, out float pz);
        double vitaePct = Math.Clamp((1.0 - vitae) * 100.0, 0.0, 100.0);
        // Lazy baselines; reset on an XP drop (relog / char change).
        if (xp > 0 && (_xpBaseline < 0 || xp < _xpBaseline))
        {
            _xpBaseline = xp; _deathsBaseline = deaths; _statsSessionStartUtc = DateTime.UtcNow;
        }
        if (_deathsBaseline < 0 && xp > 0) _deathsBaseline = deaths;
        if (lum > 0 && (_lumBaseline < 0 || lum < _lumBaseline)) _lumBaseline = lum;
        double hours = (DateTime.UtcNow - _statsSessionStartUtc).TotalHours;
        double xpPerHour  = (hours > 1.0 / 3600.0 && _xpBaseline  >= 0 && xp  >= _xpBaseline)  ? (xp  - _xpBaseline)  / hours : 0;
        double lumPerHour = (hours > 1.0 / 3600.0 && _lumBaseline >= 0 && lum >= _lumBaseline) ? (lum - _lumBaseline) / hours : 0;
        long xpSession = (_xpBaseline >= 0 && xp >= _xpBaseline) ? xp - _xpBaseline : 0;
        int deathsSession = (_deathsBaseline >= 0 && deaths >= _deathsBaseline) ? deaths - _deathsBaseline : 0;
        double burdenPct = encCap > 0 ? Math.Round((double)encVal / encCap * 100.0) : 0;
        w.WriteNumber("deaths", deaths);
        w.WriteNumber("deathsSession", deathsSession);
        w.WriteNumber("vitaePct", Math.Round(vitaePct, 1));
        w.WriteNumber("xpPerHour", Math.Round(xpPerHour));
        w.WriteNumber("luminancePerHour", Math.Round(lumPerHour));
        w.WriteNumber("xpSession", xpSession);
        w.WriteNumber("burdenPct", burdenPct);
        w.WriteString("area", landcell != 0 ? (landcell >> 16).ToString("X4") : "");
        // [status-export] live map-dot position. wx/wy are ABSOLUTE world coords (cell-local origin + the
        // landblock grid offset) because the baked dungeon map's bounds are absolute (DungeonLOS adds the
        // same gx/gy per vertex). The app maps these straight to map pixels.
        uint lb = landcell != 0 ? (landcell >> 16) : 0u;
        bool indoor = lb != 0 && (landcell & 0xFFFF) >= 0x0100;   // indoor EnvCells are 0x0100+; outdoor = 0
        w.WriteString("landblock", lb != 0 ? lb.ToString("X8") : "");   // matches the .bin filename {lb:X8}
        w.WriteBoolean("indoor", indoor);
        if (indoor)
        {
            double wx = px + ((lb >> 8) & 0xFF) * 192.0;
            double wy = py + (lb & 0xFF) * 192.0;
            w.WriteNumber("wx", Math.Round(wx, 2));
            w.WriteNumber("wy", Math.Round(wy, 2));
            w.WriteNumber("pz", Math.Round(pz, 2));   // raw local Z — floor-label / fallback hint
        }
        // Last engine warning/error (truncated) + age, for remote diagnosis of a stuck box.
        string? li = RynthLog.LastIssue;
        if (!string.IsNullOrEmpty(li))
        {
            w.WriteString("lastIssue", li.Length > 140 ? li.Substring(0, 140) : li);
            w.WriteNumber("lastIssueAgeSec", (long)Math.Max(0, (DateTime.UtcNow - RynthLog.LastIssueUtc).TotalSeconds));
        }
        else { w.WriteNull("lastIssue"); w.WriteNumber("lastIssueAgeSec", -1); }
    }

    private static string SafeMachineName()
    {
        try { return Environment.MachineName; }
        catch { return string.Empty; }
    }
}
