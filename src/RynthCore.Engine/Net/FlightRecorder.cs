// ============================================================================
//  RynthCore.Engine - Net/FlightRecorder.cs
//  Always-on flight recorder: the last minutes of client<->server traffic that matters
//  for "the character stopped doing things", plus the engine's own markers, dumped to
//  a text file on request or when a stall is detected.
//
//  Feeds (all cheap, none allocates on the hot path):
//    - RawPacketHooks: every datagram sent (sendto detour, new) and received (the
//      existing recvfrom detour) -> FlightCapture.OnDatagram;
//    - AcActionTrace.Record (every engine direct-action call) -> EngineCall markers,
//      used to tag the action that call sends as [eng];
//    - BusyCountHooks / ClientActionGates: m_cBusy changes, force-clears (plugin asks
//      included), reconciles, negative repairs, gate clears;
//    - GameTickHooks (AC's main thread, every tick) -> MainTick: a one-line summary per
//      second (link counters, m_cBusy, the item-action gates, the cast gesture), the
//      stall detectors, and starting a scheduled automatic dump.
//
//  Dumps: /rc flightdump [note] (any time), /rc flightdump auto <reason> and the plugin
//  command (SendPluginCommand("RynthCore.FlightRecorder", "dump", reason)) for automatic
//  ones - at most one per AutoDumpCooldownMs, written AutoDumpDelayMs after the trigger so
//  the aftermath is in it too. Files: Logs\flight-<pid>-<yyyyMMdd-HHmmss>.txt, written on
//  a short-lived engine thread.
//
//  engine.json "FlightRecorder": false turns it off at the next client start (no sendto
//  hook, nothing recorded); /rc flight off|on pauses it for the session.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.Net;

internal static unsafe class FlightRecorder
{
    public const int Capacity = 16384;                 // entries (152 bytes each, ~2.5 MB)
    public const long AutoDumpCooldownMs = 10 * 60 * 1000;
    public const int AutoDumpDelayMs = 3000;
    public const int PruneDays = 14;

    /// <summary>The plugin name plugins pass to SendPluginCommand to reach the recorder.</summary>
    public const string PluginCommandTarget = "RynthCore.FlightRecorder";

    private static FlightCapture? _cap;
    private static volatile bool _paused;
    private static volatile bool _autoOff;
    private static readonly FlightDumpPolicy Policy = new(AutoDumpCooldownMs);
    private static long _lastSummaryMs;
    private static int _lastSendErrors;

    private static readonly object ScheduleLock = new();
    private static string? _scheduledReason;
    private static long _scheduledAtMs, _scheduledZeroStamp;
    private static DateTime _scheduledZeroLocal;

    private static int _dumping;
    private static string _lastDump = "";
    private static int _dumpsWritten;

    public static bool Enabled => _cap != null && !_paused;

    /// <summary>An automatic dump is scheduled or a dump is being written right now.</summary>
    public static bool DumpPendingOrRunning
    {
        get
        {
            if (Volatile.Read(ref _dumping) != 0) return true;
            lock (ScheduleLock) return _scheduledReason != null;
        }
    }

    // ── setup ──────────────────────────────────────────────────────────────

    public static void Initialize()
    {
        if (_cap != null) return;
        if (!EngineSettings.FlightRecorder)
        {
            RynthLog.Compat("FlightRecorder: off (engine.json \"FlightRecorder\": false).");
            return;
        }
        _cap = new FlightCapture(Capacity, Stopwatch.GetTimestamp, Stopwatch.Frequency);
        uint gen = (uint)Math.Max(1, EntryPoint.InitCount);
        _cap.Mark(FlightMark.EngineInit, gen);
        RynthLog.Compat($"FlightRecorder: on ({Capacity} entries, {_cap.Ring.Bytes / 1024} KB); /rc flightdump writes it.");
        if (gen <= 1)
            PruneOldDumps();
    }

    public static void Shutdown()
    {
        FlightCapture? c = _cap;
        if (c == null) return;
        c.FlushEngineRepeats();
        c.Mark(FlightMark.EngineShutdown, text: EngineThreads.Stopping ? "hot reload or exit" : "");
    }

    // ── feeds ──────────────────────────────────────────────────────────────

    /// <summary>RecvFrom detour, after the real call, with the bytes the client got.</summary>
    public static void OnInbound(byte* data, int length)
    {
        FlightCapture? c = _cap;
        if (c == null || _paused) return;
        c.OnDatagram(false, data, length);
    }

    /// <summary>SendTo detour, before the real call, with the bytes the client sends.</summary>
    public static void OnOutbound(byte* data, int length)
    {
        FlightCapture? c = _cap;
        if (c == null || _paused) return;
        c.OnDatagram(true, data, length);
    }

    /// <summary>An engine direct-action call (AcActionTrace.Record). <paramref name="name"/> is a literal.</summary>
    public static void EngineCall(string name, uint a, uint b)
    {
        FlightCapture? c = _cap;
        if (c == null || _paused) return;
        try { c.EngineCall(name, a, b); } catch { }
    }

    /// <summary>An engine IdentifyObject send for <paramref name="id"/>, asked for by <paramref name="who"/>
    /// (IdRequester): the 0xC8 it produces is tagged [eng:&lt;who&gt;]. Call it before the send; the
    /// token goes to EngineSendFailed when the send does not happen. -1 while the recorder is off.</summary>
    public static int EngineSend(byte who, uint id)
    {
        FlightCapture? c = _cap;
        if (c == null || _paused) return -1;
        try { return c.EngineSend(who, id); } catch { return -1; }
    }

    /// <summary>The send registered as <paramref name="token"/> did not happen: retire its entry.</summary>
    public static void EngineSendFailed(int token, uint id)
    {
        if (token < 0) return;
        FlightCapture? c = _cap;
        if (c == null) return;
        try { c.EngineSendFailed(token, id); } catch { }
    }

    /// <summary>IdentifyObject messages sent with no engine send to match (AC or Decal); -1 while the recorder is off.</summary>
    public static long ForeignIdentifyTotal
    {
        get
        {
            FlightCapture? c = _cap;
            return c == null ? -1 : c.IdentifyForeignTotal;
        }
    }

    public static void Mark(FlightMark kind, uint a = 0, uint b = 0, uint c = 0, string? text = null)
    {
        FlightCapture? cap = _cap;
        if (cap == null || _paused) return;
        try { cap.Mark(kind, a, b, c, text); } catch { }
    }

    /// <summary>AC's main thread, every game tick (GameTickHooks, post-tick).</summary>
    public static void MainTick()
    {
        FlightCapture? c = _cap;
        if (c == null) return;
        long now = Environment.TickCount64;
        if (!_paused && now - _lastSummaryMs >= 1000)
        {
            _lastSummaryMs = now;
            WriteSummary(c);
            c.Tick();
            string? trigger = c.TakeTrigger();
            if (trigger != null)
                RequestAuto(trigger, "engine");
        }
        string? reason = null;
        long zero = 0;
        DateTime zeroLocal = default;
        lock (ScheduleLock)
        {
            if (_scheduledReason != null && now >= _scheduledAtMs)
            {
                reason = _scheduledReason;
                zero = _scheduledZeroStamp;
                zeroLocal = _scheduledZeroLocal;
                _scheduledReason = null;
            }
        }
        if (reason != null)
            StartDump("auto: " + reason, zero, zeroLocal, announce: true);
    }

    private static void WriteSummary(FlightCapture c)
    {
        var s = new FlightSummary
        {
            RealBusy = FlightSummary.Unknown,
            PendingType = 0xFFFFFFFF,
            Attacking = 255,
            Gesture = 255,
        };
        try { if (BusyCountHooks.TryReadRealBusy(out int real)) s.RealBusy = real; } catch { }
        s.ShadowBusy = BusyCountHooks.ShadowBusyCount;
        try
        {
            if (ClientActionGates.TryReadPending(out uint type, out uint obj, out _)) { s.PendingType = type; s.PendingObject = obj; }
            if (ClientActionGates.TryReadAttacking(out bool atk)) s.Attacking = atk ? (byte)1 : (byte)0;
        }
        catch { }
        try { if (PlayerPhysicsHooks.TryGetCastGestureInProgress(out bool g)) s.Gesture = g ? (byte)1 : (byte)0; } catch { }
        c.TakeCounters(ref s);
        int sendErrors = RawPacketHooks.SendErrors;
        s.SendErrors = (ushort)Math.Clamp(sendErrors - _lastSendErrors, 0, ushort.MaxValue);
        _lastSendErrors = sendErrors;
        c.WriteSummary(s);
    }

    // ── dumps ──────────────────────────────────────────────────────────────

    /// <summary>An automatic dump request (detector, plugin, /rc flightdump auto). Rate-limited.</summary>
    public static string RequestAuto(string reason, string source)
    {
        FlightCapture? c = _cap;
        if (c == null) return "Flight recorder is off.";
        reason = Clean(reason, 100);
        if (_autoOff)
        {
            c.Mark(FlightMark.Trigger, text: $"{source}: {reason} (automatic dumps are off: /rc flight auto on)");
            return "Automatic flight dumps are off for this session.";
        }
        long now = Environment.TickCount64;
        if (!Policy.TryAccept(now, out long waitMs))
        {
            c.Mark(FlightMark.Trigger, text: $"{source}: {reason} (no dump: one was written {(Policy.CooldownMs - waitMs) / 1000} s ago)");
            return $"Not dumped: the last automatic dump was {(Policy.CooldownMs - waitMs) / 1000} s ago (one per {Policy.CooldownMs / 60000} min).";
        }
        c.Mark(FlightMark.Trigger, text: $"{source}: {reason}");
        lock (ScheduleLock)
        {
            _scheduledReason = $"{source}: {reason}";
            _scheduledAtMs = now + AutoDumpDelayMs;
            _scheduledZeroStamp = Stopwatch.GetTimestamp();
            _scheduledZeroLocal = DateTime.Now;
        }
        RynthLog.Compat($"FlightRecorder: automatic dump in {AutoDumpDelayMs / 1000} s ({source}: {reason}).");
        return $"Automatic flight dump in {AutoDumpDelayMs / 1000} s.";
    }

    /// <summary>A manual dump now. Returns the file it is being written to.</summary>
    public static string DumpNow(string note)
    {
        FlightCapture? c = _cap;
        if (c == null) return "Flight recorder is off (engine.json \"FlightRecorder\": false).";
        note = Clean(note, 100);
        c.Mark(FlightMark.Dump, text: note);
        string? path = StartDump(note.Length > 0 ? "manual: " + note : "manual", Stopwatch.GetTimestamp(), DateTime.Now, announce: false);
        return path == null ? "A dump is already being written; try again in a moment." : $"Flight recorder dump: {path}";
    }

    private static string? StartDump(string why, long zeroStamp, DateTime zeroLocal, bool announce)
    {
        FlightCapture? c = _cap;
        if (c == null) return null;
        if (Interlocked.CompareExchange(ref _dumping, 1, 0) != 0)
            return null;
        string path;
        try
        {
            LogPaths.EnsureLogDirectory();
            string stem = $"flight-{Environment.ProcessId}-{DateTime.Now:yyyyMMdd-HHmmss}";
            path = Path.Combine(LogPaths.LogDirectory, stem + ".txt");
            for (int i = 2; File.Exists(path) && i < 100; i++)
                path = Path.Combine(LogPaths.LogDirectory, $"{stem}-{i}.txt");
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _dumping, 0);
            RynthLog.Compat($"FlightRecorder: dump failed - {ex.Message}");
            return null;
        }
        c.FlushEngineRepeats();
        string file = path;
        try
        {
            EngineThreads.Start("RynthCore.FlightDump", () => WriteDumpFile(c, file, why, zeroStamp, zeroLocal, announce));
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _dumping, 0);
            RynthLog.Compat($"FlightRecorder: dump thread failed - {ex.Message}");
            return null;
        }
        return path;
    }

    private static void WriteDumpFile(FlightCapture c, string path, string why, long zeroStamp, DateTime zeroLocal, bool announce)
    {
        try
        {
            FlightRecord[] records = c.Ring.Snapshot();
            var header = new List<string>
            {
                $"Reason: {why}",
                $"Requested: {zeroLocal:yyyy-MM-dd HH:mm:ss.fff} (time 0 below)",
                $"Client pid {Environment.ProcessId}, engine build {EntryPoint.BuildStamp}, generation {EntryPoint.InitCount}, player 0x{c.PlayerId:X8}",
                $"Ring: {c.Ring.Capacity} entries, {c.Ring.Total} recorded since this engine generation started.",
            };
            using (var w = new StreamWriter(path, false, new System.Text.UTF8Encoding(false), 256 * 1024))
                FlightFormat.WriteDump(w, records, zeroStamp, zeroLocal, Stopwatch.Frequency, header);
            _lastDump = path;
            Interlocked.Increment(ref _dumpsWritten);
            RynthLog.Compat($"FlightRecorder: wrote {path} ({records.Length} entries; {why}).");
            if (announce)
                AcMainThreadQueue.EnqueueWriteToChat($"[RynthCore] Flight recorder saved ({why}): {path}", 1);
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"FlightRecorder: writing {path} failed - {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _dumping, 0);
        }
    }

    private static void PruneOldDumps()
    {
        try
        {
            if (!Directory.Exists(LogPaths.LogDirectory)) return;
            DateTime cutoff = DateTime.Now.AddDays(-PruneDays);
            foreach (string f in Directory.GetFiles(LogPaths.LogDirectory, "flight-*.txt"))
            {
                try { if (File.GetLastWriteTime(f) < cutoff) File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    private static string Clean(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Trim().Replace('\r', ' ').Replace('\n', ' ');
        return s.Length > max ? s.Substring(0, max) : s;
    }

    // ── commands ───────────────────────────────────────────────────────────

    /// <summary>/rc flightdump [note] | /rc flightdump auto &lt;reason&gt;</summary>
    public static string[] HandleDumpCommand(string args)
    {
        args = args.Trim();
        if (args.StartsWith("auto", StringComparison.OrdinalIgnoreCase) && (args.Length == 4 || args[4] == ' '))
            return new[] { RequestAuto(args.Length > 4 ? args.Substring(5) : "chat command", "command") };
        return new[] { DumpNow(args) };
    }

    /// <summary>/rc flight [status|mark &lt;text&gt;|on|off|auto on|off]</summary>
    public static string[] HandleCommand(string args)
    {
        string[] parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string verb = parts.Length > 0 ? parts[0].ToLowerInvariant() : "status";
        string rest = parts.Length > 1 ? parts[1].Trim() : "";
        switch (verb)
        {
            case "mark":
            case "note":
                if (_cap == null) return new[] { "Flight recorder is off." };
                Mark(FlightMark.Note, text: Clean(rest, 100));
                return new[] { "Marked." };
            case "off":
                _paused = true;
                return new[] { "Flight recorder paused for this session (/rc flight on resumes)." };
            case "on":
                _paused = false;
                return new[] { _cap == null ? "Flight recorder is off in engine.json (\"FlightRecorder\": false); restart the client after changing it." : "Flight recorder recording." };
            case "auto":
                if (rest.Equals("off", StringComparison.OrdinalIgnoreCase)) { _autoOff = true; return new[] { "Automatic flight dumps off for this session." }; }
                if (rest.Equals("on", StringComparison.OrdinalIgnoreCase)) { _autoOff = false; return new[] { "Automatic flight dumps on." }; }
                return new[] { "Usage: /rc flight auto on|off" };
            case "status":
                return StatusLines();
            default:
                // A word it doesn't take: the usage, not the status (every command answers a typo so).
                return new[] { "Usage: /rc flight [status|mark <text>|on|off|auto on|off]" };
        }
    }

    public static string[] StatusLines()
    {
        FlightCapture? c = _cap;
        if (c == null)
            return new[] { "Flight recorder: off (engine.json \"FlightRecorder\": false)." };
        return new[]
        {
            $"Flight recorder: {(_paused ? "PAUSED" : "recording")}, {c.Ring.Total} entries recorded (ring {c.Ring.Capacity}, {c.Ring.Bytes / 1024} KB), " +
            $"send hook {(RawPacketHooks.IsSendInstalled ? "installed" : "NOT installed")}, receive hook {(RawPacketHooks.IsInstalled ? "installed" : "NOT installed")}, player 0x{c.PlayerId:X8}",
            $"Automatic dumps {(_autoOff ? "OFF" : "on")} (one per {AutoDumpCooldownMs / 60000} min, {Policy.Refused} refused by the limit); " +
            $"refused-use streak {c.RefusedStreak}; dumps written {_dumpsWritten}{(_lastDump.Length > 0 ? ", last " + _lastDump : "")}",
            "/rc flightdump [note] writes it now; /rc flight mark <text> adds a note; /rc flight off|on|auto off|on.",
        };
    }

    /// <summary>SendPluginCommand("RynthCore.FlightRecorder", action, value): "dump" (automatic,
    /// rate-limited; value = reason) or "mark" (value = note). Any thread.</summary>
    public static bool HandlePluginCommand(string? action, string? value)
    {
        if (_cap == null) return false;
        switch ((action ?? "").Trim().ToLowerInvariant())
        {
            case "dump":
                RequestAuto(value ?? "", "plugin");
                return true;
            case "mark":
            case "note":
                Mark(FlightMark.Note, text: Clean("plugin: " + value, 100));
                return true;
            default:
                return false;
        }
    }
}
