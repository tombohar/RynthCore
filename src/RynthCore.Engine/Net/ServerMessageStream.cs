// ============================================================================
//  RynthCore.Engine - Net/ServerMessageStream.cs
//  The reassembled server-message stream for plugins (API v78).
//
//  RecvFrom detour (RawPacketHooks)          plugin pump (PluginManager.ProcessPendingActions)
//    OnDatagram: one volatile check; while      Pump: drains the ring through the
//    capturing, copies fragment datagrams  ->   FragmentReassembler; each whole message goes
//    into PacketRing (no alloc, no lock,        to the plugins that asked for it (and to the
//    never waits; full = dropped + counted)     /rc netmsg log tap and capture file).
//
//  Strictly read-only: the detour copies the client's bytes after the real recvfrom
//  returned them; it never alters, delays or drops anything the client gets.
//
//  Capturing runs only while it is wanted: a plugin with a non-empty interest, or
//  /rc netmsg log / capture. engine.json "ServerMessageStream": false (or /rc netmsg off)
//  keeps it off whatever is wanted, and the detour is then exactly what it was before.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RynthCore.Engine.Plugins;
using RynthCore.PluginSdk.Net;

namespace RynthCore.Engine.Net;

internal static unsafe class ServerMessageStream
{
    private const int RingBytes = 2 * 1024 * 1024;
    private const int MaxPacketsPerPump = 8192;
    private const long MaxCaptureBytes = 32L * 1024 * 1024;
    private const int MaxLogLinesPerKind = 5;
    private const int MaxLogLines = 300;

    private static PacketRing? _ring;            // created on first use, never freed (see PacketRing)
    private static readonly FragmentReassembler Reassembler = new();
    private static readonly Sink MessageSink = new();
    private static volatile bool _capturing;
    private static bool _sessionOff;
    private static bool _pluginsWant;

    /// <summary>
    /// The engine itself wants the stream (the Inventory panel's server-order tracker, Net/InventoryOrder),
    /// whether or not a plugin does. engine.json / /rc netmsg off still turn the stream off.
    /// </summary>
    public static volatile bool EngineWants = true;

    // Delivery counters (pump thread).
    private static long _delivered, _dispatchedToPlugins;
    private static long _pumpTicksTotal, _pumpTicksMax, _pumps;

    // /rc netmsg log: check the SDK parsers against the live server.
    private static bool _logOn;
    private static int _logLines;
    private static readonly Dictionary<string, KindStats> Kinds = new();
    private static readonly Dictionary<uint, long> Opcodes = new();
    private static readonly object LogLock = new();   // Kinds/Opcodes: pump writes, chat commands read

    // /rc netmsg capture: raw fragment datagrams for tools\NetMessageTests.
    private static volatile FileStream? _capture;     // opened by the command, written and closed by the pump
    private static volatile bool _captureStopRequested;
    private static string _capturePath = "";
    private static long _captureBytes;

    private sealed class KindStats
    {
        public long Ok, Failed, Leftover;
        public int Logged;
    }

    /// <summary>True while the detour copies datagrams.</summary>
    public static bool Capturing => _capturing;

    // ── receive hook side ───────────────────────────────────────────────────

    /// <summary>
    /// Starts copying datagrams as soon as the receive hook is installed (RawPacketHooks.Initialize), when the
    /// engine itself wants the stream. The pump only runs once the world is up, and the server sends the
    /// player's inventory order (PlayerDescription) while entering it: the ring holds that until the first pump.
    /// </summary>
    public static void EnableEarly()
    {
        try
        {
            if (!EngineWants || _sessionOff || !EngineSettings.ServerMessageStream || _capturing) return;
            _ring ??= new PacketRing(RingBytes);
            _capturing = true;
            RynthLog.Compat("ServerMessageStream: capture on from the receive hook (the engine's own readers).");
        }
        catch (Exception ex) { RynthLog.Compat($"ServerMessageStream: early capture failed - {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>Called by the RecvFrom detour after the real recvfrom, with the bytes it returned.</summary>
    public static void OnDatagram(byte* data, int length)
    {
        if (!_capturing)
            return;
        PacketRing? ring = _ring;
        if (ring == null || length < AcPacketWire.HeaderSize)
            return;
        uint flags = AcPacketWire.ReadU32(data + 4);
        if ((flags & (PacketFlags.BlobFragments | PacketFlags.ConnectRequest)) == 0)
            return;   // acks, pings, time sync: nothing to reassemble
        ring.TryWrite(data, length, (uint)Environment.TickCount);
    }

    // ── pump side ───────────────────────────────────────────────────────────

    /// <summary>
    /// Once per ProcessPendingActions (the single TickAll driver). <paramref name="pluginsWant"/>:
    /// some initialized plugin has a non-empty interest.
    /// </summary>
    // The server's item order survives an engine reload (Net/InventoryOrder): saved per client process.
    private static bool _orderRestored;
    private static int _orderSavedVersion = -1;
    private static long _orderSavedMs;
    private static string OrderFile => Path.Combine(Path.GetTempPath(), $"RynthCore.invorder.{Environment.ProcessId}.bin");

    private static void KeepInventoryOrder(long nowMs)
    {
        try
        {
            if (!_orderRestored)
            {
                _orderRestored = true;
                string f = OrderFile;
                // Only this client's own file, and not a stale one (a recycled process id).
                if (File.Exists(f) && DateTime.UtcNow - File.GetLastWriteTimeUtc(f) < TimeSpan.FromHours(24)
                    && InventoryOrder.Restore(File.ReadAllBytes(f)))
                    RynthLog.Compat("ServerMessageStream: restored the server's item order saved before this engine loaded.");
                _orderSavedVersion = InventoryOrder.Version;
                return;
            }
            int v = InventoryOrder.Version;
            if (v == _orderSavedVersion || nowMs - _orderSavedMs < 1500) return;
            File.WriteAllBytes(OrderFile, InventoryOrder.Serialize());
            _orderSavedVersion = v;
            _orderSavedMs = nowMs;
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"ServerMessageStream: item order file failed - {ex.GetType().Name}: {ex.Message}");
            _orderSavedVersion = InventoryOrder.Version;   // do not retry every pump
        }
    }

    public static void Pump(bool pluginsWant)
    {
        KeepInventoryOrder(Environment.TickCount64);
        _pluginsWant = pluginsWant;
        if (_captureStopRequested)
        {
            _captureStopRequested = false;
            StopCapture("stopped");
        }
        bool want = EngineSettings.ServerMessageStream && !_sessionOff && (pluginsWant || EngineWants || _logOn || _capture != null);
        if (want != _capturing)
        {
            if (want)
            {
                _ring ??= new PacketRing(RingBytes);
                _ring.Clear();
                Reassembler.Reset();
            }
            _capturing = want;
            RynthLog.Compat($"ServerMessageStream: capture {(want ? "on" : "off")} (plugins={pluginsWant} log={_logOn} file={_capture != null})");
        }

        PacketRing? ring = _ring;
        if (ring == null)
            return;
        if (!_capturing)
        {
            if (ring.Used != 0) ring.Clear();
            return;
        }

        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        long now = Environment.TickCount64;
        int n = 0;
        while (n < MaxPacketsPerPump && ring.TryPeek(out byte* data, out int len, out uint stamp, out int releaseTo))
        {
            n++;
            try
            {
                if (_capture != null) WriteCapture(data, len, stamp);
                Reassembler.ProcessDatagram(data, len, now, MessageSink);
            }
            catch (Exception ex)
            {
                RynthLog.Compat($"ServerMessageStream: datagram failed - {ex.GetType().Name}: {ex.Message}");
            }
            ring.Release(releaseTo);
        }
        if (n > 0)
        {
            long ticks = System.Diagnostics.Stopwatch.GetTimestamp() - start;
            _pumps++;
            _pumpTicksTotal += ticks;
            if (ticks > _pumpTicksMax) _pumpTicksMax = ticks;
        }
    }

    private sealed class Sink : IServerMessageSink
    {
        public void OnServerMessage(uint fragmentSequence, byte* message, int length)
        {
            _delivered++;
            uint opcode = AcPacketWire.ReadU32(message);
            uint eventType = opcode == ServerMessage.GameEventOpcode && length >= 16 ? AcPacketWire.ReadU32(message + 12) : 0;
            if (_logOn)
            {
                try { LogTap(opcode, eventType, message, length); }
                catch (Exception ex) { RynthLog.Compat($"ServerMessageStream: log tap failed - {ex.GetType().Name}: {ex.Message}"); }
            }
            if (EngineWants)
            {
                try { InventoryOrder.Observe(new ReadOnlySpan<byte>(message, length)); }
                catch (Exception ex) { RynthLog.Compat($"ServerMessageStream: inventory order failed - {ex.GetType().Name}: {ex.Message}"); }
                // The SERVER deleted an object (0xF747 ObjectDelete, its id after the opcode). AutoId
                // tells this apart from the client's own releases: only this one makes a re-create
                // under the same id due an appraisal again (AutoIdEligibility).
                if (opcode == InventoryOrder.DeleteObjectOpcode && length >= 8)
                {
                    try { Compatibility.AutoIdService.OnServerDelete(AcPacketWire.ReadU32(message + 4)); }
                    catch (Exception ex) { RynthLog.Compat($"ServerMessageStream: server delete failed - {ex.GetType().Name}: {ex.Message}"); }
                }
            }
            if (_pluginsWant)
                _dispatchedToPlugins += PluginManager.DispatchServerMessage(opcode, eventType, message + 4, length - 4);
        }
    }

    // ── /rc netmsg ──────────────────────────────────────────────────────────

    public static string[] HandleCommand(string args)
    {
        string[] parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string verb = parts.Length > 0 ? parts[0].ToLowerInvariant() : "status";
        string arg = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
        switch (verb)
        {
            case "on":
                _sessionOff = false;
                return new[] { EngineSettings.ServerMessageStream
                    ? "Server message stream allowed for this session (it runs while a plugin, the log or a capture wants it)."
                    : "engine.json has \"ServerMessageStream\": false - the stream stays off. Remove it (or set true) and restart." };
            case "off":
                _sessionOff = true;
                return new[] { "Server message stream off for this session (plugins get nothing until /rc netmsg on)." };
            case "log":
                if (arg is "on" or "")
                {
                    lock (LogLock) { _logLines = 0; Kinds.Clear(); Opcodes.Clear(); }
                    _logOn = true;
                    return new[] { "Server message log on: parsed messages go to the RynthCore log (NetMsg:), counts to /rc netmsg." };
                }
                if (arg == "off") { _logOn = false; return new[] { "Server message log off." }; }
                return new[] { "Usage: /rc netmsg log on|off" };
            case "capture":
                if (arg is "on" or "") return new[] { StartCapture() };
                if (arg == "off")
                {
                    if (_capture == null) return new[] { "Not capturing." };
                    _captureStopRequested = true;   // the pump closes it after its current write
                    return new[] { $"Capture stopping: {_capturePath}." };
                }
                return new[] { "Usage: /rc netmsg capture on|off" };
            case "status":
                return StatusLines();
            default:
                return new[] { "Usage: /rc netmsg [status|on|off|log on|off|capture on|off]" };
        }
    }

    public static string[] StatusLines()
    {
        var r = Reassembler;
        var lines = new List<string>
        {
            $"Server messages: setting {(EngineSettings.ServerMessageStream ? "on" : "OFF (engine.json)")}, session {(_sessionOff ? "off" : "on")}, " +
            $"capturing {(_capturing ? "yes" : "no")}, hook {(Compatibility.RawPacketHooks.IsInstalled ? "installed" : "NOT installed")}; " +
            $"plugins {PluginManager.ServerMessageSubscriberCount()} subscribed",
        };
        if (_ring != null)
            lines.Add($"Ring: {_ring.Written} datagrams copied, dropped {_ring.DroppedFull} full / {_ring.DroppedContended} contended / {_ring.DroppedTooLong} too long, {_ring.Used / 1024} KB queued");
        lines.Add($"Reassembly: {r.Packets} packets ({r.PacketsNoFragments} no fragments, {r.PacketsMalformed} malformed, {r.PacketsUnknownOptional} unknown header" +
                  (r.PacketsUnknownOptional > 0 ? $" last flags 0x{r.LastUnknownFlags:X8}" : "") + $"), {r.Fragments} fragments, " +
                  $"{r.Messages} messages ({r.MultiFragmentMessages} multi-fragment), dup {r.DuplicateFragments} fragments / {r.DuplicateMessages} messages, " +
                  $"bad {r.BadFragments}, oversized {r.Oversized}, evicted {r.EvictedStale} stale / {r.EvictedFull} full, {r.PartialCount} partial, {r.SessionResets} session resets");
        double tickUs = 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency;
        lines.Add($"Delivered {_delivered}, plugin calls {_dispatchedToPlugins}; drain (incl. plugin calls) avg {(_pumps > 0 ? _pumpTicksTotal * tickUs / _pumps : 0):0.0} us, max {_pumpTicksMax * tickUs:0} us over {_pumps} drains");
        lock (LogLock)
        if (_logOn || Kinds.Count > 0)
        {
            string kinds = string.Join(", ", Kinds.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value.Ok} ok" +
                (k.Value.Failed > 0 ? $"/{k.Value.Failed} FAILED" : "") + (k.Value.Leftover > 0 ? $"/{k.Value.Leftover} leftover" : "")));
            lines.Add($"Parsed ({(_logOn ? "log on" : "log off")}): {(kinds.Length > 0 ? kinds : "none yet")}");
            string top = string.Join(" ", Opcodes.OrderByDescending(o => o.Value).Take(12).Select(o => $"{FormatKey(o.Key)}x{o.Value}"));
            if (top.Length > 0) lines.Add($"Top opcodes: {top}");
        }
        if (_capture != null)
            lines.Add($"Capturing to {_capturePath} ({_captureBytes / 1024} KB)");
        return lines.ToArray();
    }

    private static string FormatKey(uint key) => key >= 0x10000 ? $"F7B0/{key & 0xFFFF:X4}" : $"{key:X4}";

    // ── log tap ─────────────────────────────────────────────────────────────

    private static void LogTap(uint opcode, uint eventType, byte* message, int length)
    {
        uint key = opcode == ServerMessage.GameEventOpcode ? 0x10000u | (eventType & 0xFFFF) : opcode & 0xFFFF;
        var msg = new ServerMessage(opcode, new ReadOnlySpan<byte>(message + 4, length - 4));
        NetMessageInfo info = NetMessages.Inspect(msg);
        bool interesting = !info.Parsed || info.Leftover != 0;
        bool log;
        lock (LogLock)
        {
            Opcodes[key] = Opcodes.TryGetValue(key, out long c) ? c + 1 : 1;
            if (!info.Known)
                return;
            if (!Kinds.TryGetValue(info.Kind, out KindStats? k))
                Kinds[info.Kind] = k = new KindStats();
            if (info.Parsed) k.Ok++; else k.Failed++;
            if (info.Parsed && info.Leftover != 0) k.Leftover++;
            log = _logLines < MaxLogLines && (k.Logged < MaxLogLinesPerKind || interesting);
            if (log)
            {
                _logLines++;
                k.Logged++;
            }
        }
        if (log)
        {
            string hex = interesting ? " bytes=" + Convert.ToHexString(new ReadOnlySpan<byte>(message, Math.Min(length, 96))) : "";
            RynthLog.Compat($"NetMsg: {info.Kind} len={length} {(info.Parsed ? "ok" : "FAILED")}" +
                            (info.Leftover != 0 ? $" leftover={info.Leftover}" : "") + $" {info.Summary}{hex}");
        }
    }

    // ── capture file ────────────────────────────────────────────────────────
    // Format "RNC1": the 4 magic bytes, then per datagram [u32 stamp ms][u16 length][bytes].

    private static string StartCapture()
    {
        if (_capture != null)
            return $"Already capturing to {_capturePath}.";
        if (!EngineSettings.ServerMessageStream || _sessionOff)
            return "The server message stream is off; /rc netmsg on first (engine.json must allow it).";
        try
        {
            LogPaths.EnsureLogDirectory();
            _capturePath = Path.Combine(LogPaths.LogDirectory, $"netcapture-{Environment.ProcessId}-{DateTime.Now:yyyyMMdd-HHmmss}.rnc");
            _capture = new FileStream(_capturePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024);
            _capture.Write("RNC1"u8);
            _captureBytes = 4;
            return $"Capturing server datagrams to {_capturePath} (stops at {MaxCaptureBytes / (1024 * 1024)} MB; holds chat text, keep it local).";
        }
        catch (Exception ex)
        {
            _capture = null;
            return $"Capture failed: {ex.Message}";
        }
    }

    private static string StopCapture(string why)
    {
        if (_capture == null)
            return "Not capturing.";
        try { _capture.Dispose(); } catch { }
        _capture = null;
        string msg = $"Capture {why}: {_capturePath} ({_captureBytes / 1024} KB).";
        RynthLog.Compat("ServerMessageStream: " + msg);
        return msg;
    }

    private static void WriteCapture(byte* data, int len, uint stamp)
    {
        FileStream? f = _capture;
        if (f == null) return;
        Span<byte> head = stackalloc byte[6];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(head, stamp);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(head.Slice(4), (ushort)len);
        try
        {
            f.Write(head);
            f.Write(new ReadOnlySpan<byte>(data, len));
            _captureBytes += 6 + len;
        }
        catch (Exception ex)
        {
            StopCapture($"failed ({ex.Message})");
            return;
        }
        if (_captureBytes >= MaxCaptureBytes)
            StopCapture("reached its size cap");
    }

    /// <summary>Engine shutdown: close a capture file. The ring stays allocated (see PacketRing).</summary>
    public static void Shutdown()
    {
        _capturing = false;
        if (_capture != null) StopCapture("closed at shutdown");
    }
}
