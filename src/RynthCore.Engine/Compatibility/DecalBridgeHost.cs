// ============================================================================
//  RynthCore.Engine - Compatibility/DecalBridgeHost.cs
//
//  "Bridge mode" (docs/DECAL_BRIDGE_PLAN.md): when Decal runs in the same acclient.exe
//  AND the RynthCore Decal bridge (src/RynthCore.DecalBridge, a Decal network filter)
//  is loaded, the engine leaves the AC functions Decal itself patches to Decal and
//  gets what it needed from them through the bridge instead:
//
//    AC function (Decal patches it too)       bridge mode
//    ClientSystem::AddTextToScroll 0x005649F0  chat lines      <- Decal ChatBoxMessage
//    OutgoingChat 0x005821A0                   chat-bar lines  <- Decal CommandLineText
//                                              sending lines   -> Decal InvokeChatParser
//    gmSalvageUI::OpenSalvagePanel 0x004CBF70  salvage add/go  -> Decal SalvagePanelAdd/Salvage
//
//  Every other hook stays native: Decal doesn't patch those functions (measured patch
//  map, 2026-09-29), and the native hooks run on AC's main thread with the live reads
//  the bridge can't do. RYNTHCORE_DECAL_BRIDGE_STREAMS=all brings back the spike's
//  routing (object create/delete/change and game events from Decal too) for A/B tests.
//
//  Transport: BridgeProtocol.cs (byte rings in a named mapping). Events are drained on
//  the plugin pump thread, before ProcessPendingActions, into the same queues the hooks
//  feed. Commands are queued here and run by the bridge on AC's main thread. No thread,
//  timer or callback the bridge could hold: Shutdown just unmaps the view.
//
//  Cost without Decal: Initialize makes two GetModuleHandle calls and returns; nothing
//  else here runs (every other entry point checks Active first).
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using RynthCore.DecalBridge;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.Compatibility;

internal static unsafe class DecalBridgeHost
{
    // Chat-bar prefixes the bridge eats for us. NOT /vt, /ub, /mt: RynthAi answers those
    // as compat commands, but with Decal loaded they belong to the real VTank/UB/MagTools.
    private const string EatPrefixes = "/rc;/ra;/rv;/rn;/rnav;/lua";
    // Same id DecalBridgeRegistration writes (RynthCore.App).
    private const string FilterKey = @"SOFTWARE\Decal\NetworkFilters\{5B3E0D57-2C41-4F8A-9D6E-8C1B70DECA11}";
    private const int HelloWaitMs = 20000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetModuleFileNameW(IntPtr module, char* buffer, uint size);

    private static readonly object Sync = new();
    private static readonly object CommandSync = new();
    private static BridgeChannel? _ch;
    private static DecalBridgeMode _mode;
    private static bool _allStreams;
    private static long _lastSummaryTick;
    private static readonly long[] _kindCounts = new long[16];
    private static readonly Dictionary<uint, long> _opcodeCounts = new();
    private static readonly Dictionary<uint, long> _gameEventCounts = new();
    private static long _serverBytes;
    private static int _logged;
    private static bool _sawHello;
    private static int _salvageLogged;
    private static int _commandDropLogged;

    /// <summary>Bridge mode is on for this generation (decided at init; false without Decal).</summary>
    public static bool Active { get; private set; }

    /// <summary>
    /// Init steps bridge mode leaves to Decal: the two whose AC functions Decal patches.
    /// With RYNTHCORE_DECAL_BRIDGE_STREAMS=all, also the spike's create/delete/game-event/raw sources.
    /// </summary>
    public static bool SkipsInitStep(string step) => Active && (step switch
    {
        "chat callback hooks" => true,   // AddTextToScroll + OutgoingChat: Decal patches both entries
        "salvage hooks" => true,         // gmSalvageUI::OpenSalvagePanel: Decal patches it
        "create-object hooks" or "delete-object hooks" or "smartbox hooks" or "raw packet hooks" => _allStreams,
        _ => false,
    });

    /// <summary>Decides bridge mode. Top of InitWorker, before any hook step.</summary>
    public static void Initialize()
    {
        Active = false;
        InactiveReason = null;
        string? decal = FindDecalModule();
        if (decal == null)
            return;   // no Decal: nothing below ever runs

        _mode = ModeFromEnvironment() ?? EngineSettings.DecalBridgeMode;
        if (_mode == DecalBridgeMode.Off)
        {
            RynthLog.Info($"DecalBridge: Decal in the process ({decal}) and DecalBridge=Off - old coexistence mode (every hook installs).");
            NoteInactive("DecalBridge=Off (engine.json or RYNTHCORE_DECAL_BRIDGE=0)",
                "RynthCore: the Decal bridge is switched off (DecalBridge Off in engine.json), so RynthCore's overlay is off in this Decal client.");
            return;
        }

        int pid = Environment.ProcessId;
        bool mappingExists = BridgeChannel.Exists(pid);
        bool registered = IsBridgeRegistered(out string registration);
        if (_mode == DecalBridgeMode.Auto && !registered && !mappingExists)
        {
            RynthLog.Info($"DecalBridge: Decal in the process ({decal}) but the RynthCore Decal bridge isn't registered ({registration}) - old coexistence mode. " +
                "Set the account to \"Decal + RynthCore\" in the launcher to use bridge mode.");
            NoteInactive($"Decal won't load the bridge in this client (its entry, read as this client sees the registry: {registration})", NotLoadedChat);
            return;
        }

        _allStreams = string.Equals(Environment.GetEnvironmentVariable("RYNTHCORE_DECAL_BRIDGE_STREAMS"), "all", StringComparison.OrdinalIgnoreCase);
        try
        {
            var ch = BridgeChannel.OpenOrCreate(pid);
            if (!ch.Valid)
            {
                RynthLog.Warn($"DecalBridge: the bridge mapping has another protocol version ({ch.ReadInt(BridgeLayout.OffVersion)}, engine {BridgeLayout.Version}) - " +
                    "the bridge and the engine are from different builds; old coexistence mode.");
                ch.Dispose();
                NoteInactive("the Decal bridge and the engine are from different builds (protocol version)",
                    "RynthCore: the Decal bridge and the engine are from different builds - restart the client once the update has finished, or reinstall RynthCore.");
                return;
            }
            uint want = BridgeLayout.Bit(BridgeRecordKind.ChatText) | BridgeLayout.Bit(BridgeRecordKind.ChatBarEnter) |
                        BridgeLayout.Bit(BridgeRecordKind.LoginComplete) | BridgeLayout.Bit(BridgeRecordKind.Logoff) |
                        BridgeLayout.Bit(BridgeRecordKind.CommandResult);
            if (_allStreams)
                want |= BridgeLayout.Bit(BridgeRecordKind.CreateObject) | BridgeLayout.Bit(BridgeRecordKind.ReleaseObject) |
                        BridgeLayout.Bit(BridgeRecordKind.ChangeObject) | BridgeLayout.Bit(BridgeRecordKind.ServerMessage);
            ch.WriteInt(BridgeLayout.OffWantMask, unchecked((int)want));
            WritePrefixes(ch, EatPrefixes);
            ch.WriteInt(BridgeLayout.OffEngineGeneration, EntryPoint.InitCount);
            ch.WriteInt(BridgeLayout.OffEngineAttached, 1);
            if (_allStreams)
            {
                // A new generation has no objects (a hot reload, or an engine injected late):
                // ask the bridge to replay Decal's WorldFilter. Duplicates are harmless.
                ch.WriteInt(BridgeLayout.OffReplayRequest, ch.ReadInt(BridgeLayout.OffReplayRequest) + 1);
            }
            lock (Sync) _ch = ch;
            Active = true;
            RynthLog.Info($"DecalBridge: BRIDGE MODE ({_mode}; Decal: {decal}; bridge {registration}; gen {EntryPoint.InitCount}). " +
                $"Bridge state={ch.ReadInt(BridgeLayout.OffBridgeState)}, records so far={ch.ReadLong(BridgeLayout.OffRecords)}. " +
                (_allStreams
                    ? "Streams=all: chat, salvage, create/delete, game-event and raw-packet hooks are left to Decal."
                    : "The chat and salvage hooks are left to Decal; every other hook installs as usual."));
        }
        catch (Exception ex)
        {
            RynthLog.Warn($"DecalBridge: could not open the bridge mapping ({ex.GetType().Name}: {ex.Message}) - old coexistence mode.");
            NoteInactive($"could not open the bridge mapping ({ex.GetType().Name})", NotLoadedChat);
        }
    }

    /// <summary>
    /// After the hook steps: waits (bounded) for the bridge to say hello. True = bridge mode
    /// stands. False = it fell back (Auto only) and the caller installs the skipped steps.
    /// </summary>
    public static bool WaitForBridge(Func<bool> aborted)
    {
        BridgeChannel? ch;
        lock (Sync) ch = _ch;
        if (!Active || ch == null)
            return true;
        long start = Environment.TickCount64;
        while (ch.ReadInt(BridgeLayout.OffBridgeState) != 1)
        {
            if (aborted() || Environment.TickCount64 - start >= HelloWaitMs)
                break;
            Thread.Sleep(100);
        }
        long waited = Environment.TickCount64 - start;
        if (ch.ReadInt(BridgeLayout.OffBridgeState) == 1)
        {
            RynthLog.Info($"DecalBridge: bridge is up (bridge pid {ch.ReadInt(BridgeLayout.OffBridgePid)}, waited {waited} ms).");
            return true;
        }
        if (_mode == DecalBridgeMode.On)
        {
            RynthLog.Warn($"DecalBridge: no hello from the bridge after {waited} ms and DecalBridge=On - staying in bridge mode; " +
                "chat, chat commands and salvage stay dark until the bridge loads.");
            NoteInactive($"registered, but Decal didn't load the bridge (no hello after {waited} ms; DecalBridge=On keeps bridge mode)", NotLoadedChat);
            return true;
        }
        RynthLog.Warn($"DecalBridge: no hello from the bridge after {waited} ms - falling back to the old coexistence mode " +
            "(the chat and salvage hooks install now). Check Logs\\DecalBridge.<pid>.log and the registration (RynthCore.Injector --decal-bridge status).");
        Detach(logSummary: false);
        NoteInactive($"registered, but Decal didn't load the bridge (no hello after {waited} ms) - see Logs\\DecalBridge.<pid>.log", NotLoadedChat);
        return false;
    }

    /// <summary>
    /// The IDirect3DDevice9* Decal renders with, as the bridge read it from Decal's public
    /// IDecalCore.GetD3DDevice on its first frame. Zero when not in bridge mode or not yet
    /// known. Never waits (read on AC's thread at login).
    /// </summary>
    public static IntPtr DecalDevice
    {
        get
        {
            BridgeChannel? ch;
            lock (Sync) ch = _ch;
            return ch == null ? IntPtr.Zero : new IntPtr(ch.ReadInt(BridgeLayout.OffD3DDevice));
        }
    }

    /// <summary>Feeds everything the bridge wrote since the last call into the plugin queues. Pump thread.</summary>
    public static int Drain()
    {
        BridgeChannel? ch;
        lock (Sync) ch = _ch;
        if (ch == null)
            return 0;
        int n = 0;
        try
        {
            n = ch.Drain(Handle, 20000);
        }
        catch (Exception ex)
        {
            RynthLog.Warn($"DecalBridge: drain threw {ex.GetType().Name}: {ex.Message}");
        }
        long now = Environment.TickCount64;
        if (now - _lastSummaryTick >= 60000)
        {
            _lastSummaryTick = now;
            LogSummary(ch);
        }
        return n;
    }

    public static void Shutdown() => Detach(logSummary: true);

    private static void Detach(bool logSummary)
    {
        BridgeChannel? ch;
        lock (Sync) { ch = _ch; _ch = null; }
        Active = false;
        if (ch == null)
            return;
        try
        {
            if (logSummary) LogSummary(ch);
            ch.WriteInt(BridgeLayout.OffEngineAttached, 0);
            ch.WriteInt(BridgeLayout.OffWantMask, 0);
        }
        catch { }
        lock (CommandSync) ch.Dispose();
        if (logSummary)
            RynthLog.Info("DecalBridge: detached (mapping view released; the bridge keeps the rings for the next generation).");
    }

    // ── "Decal is here but the bridge isn't" (2026-10-03) ────────────────────────
    //
    // Without the bridge a Decal client takes the old coexistence path (no overlay; it has
    // ended in crashes). The launcher's pre-launch check blocks that, but a client started
    // another way (an older launcher, the injector, a registration Decal doesn't read) still
    // gets here, so say it once: one clear log line, and one chat line after login.

    private const string NotLoadedChat =
        "RynthCore: the Decal bridge isn't loaded - open the launcher's Decal check (Recovery Actions > Check Decal bridge). RynthCore's overlay is off in this client.";
    private const string NoticeShownVar = "RYNTHCORE_DECAL_BRIDGE_NOTICE_SHOWN";   // once per client, across hot reloads
    private static int _noticeScheduled;
    private static string? _noticeText;

    /// <summary>
    /// Why bridge mode is off although Decal is in this client (null: bridge mode, or no
    /// Decal). Set at init by <see cref="Initialize"/> or the <see cref="WaitForBridge"/> fallback.
    /// </summary>
    public static string? InactiveReason { get; private set; }

    /// <summary>
    /// The engine should stand down in this client (EntryPoint.InitWorker): Decal is here, the
    /// bridge isn't active, the mode isn't an explicit DecalBridge=Off (engine.json or
    /// RYNTHCORE_DECAL_BRIDGE=0 - the old coexistence path, for tests), and engine.json
    /// DecalStandDown isn't false. Never true without Decal.
    /// </summary>
    public static bool ShouldStandDown =>
        InactiveReason != null && !Active && _mode != DecalBridgeMode.Off && EngineSettings.DecalStandDown;

    private static void NoteInactive(string reason, string chatLine)
    {
        InactiveReason = reason;
        RynthLog.Warn($"DecalBridge: NOT ACTIVE in this Decal client - {reason}. No RynthCore overlay here: the old coexistence path runs " +
            "(known to end in a crash). Fix: the launcher's Recovery Actions > Check Decal bridge (or RynthCore.Injector --decal-bridge check).");
        if (Environment.GetEnvironmentVariable(NoticeShownVar) == "1")
            return;
        if (Interlocked.Exchange(ref _noticeScheduled, 1) != 0)
            return;
        _noticeText = chatLine;
        if (LoginLifecycleHooks.HasObservedLoginComplete)
            PostNotice();
        else
            LoginLifecycleHooks.LoginComplete += OnLoginForNotice;
    }

    private static void OnLoginForNotice()
    {
        LoginLifecycleHooks.LoginComplete -= OnLoginForNotice;
        PostNotice();
    }

    // Queued for AC's main thread (drained from Client::UseTime); never calls AC from here.
    private static void PostNotice()
    {
        string? text = _noticeText;
        if (text == null) return;
        try
        {
            if (AcMainThreadQueue.EnqueueWriteToChat(text, 2))
                Environment.SetEnvironmentVariable(NoticeShownVar, "1");
        }
        catch { }
    }

    // ── commands (engine -> bridge -> Decal's public API on AC's main thread) ─────

    /// <summary>Sends a chat line as if typed, through Decal's InvokeChatParser. Any thread.</summary>
    public static bool TryInvokeChatParser(string line)
    {
        if (!Active || string.IsNullOrEmpty(line))
            return false;
        bool sent = SendCommand(BridgeRecordKind.CmdInvokeChatParser, 0, line);
        // The bridge doesn't echo our own lines back as ChatBarEnter, so a /vt start|stop
        // the engine or a plugin sends is noted here.
        if (sent)
            VTankWatch.ObserveSentLine(line);
        return sent;
    }

    /// <summary>Salvage panel add, left to Decal in bridge mode (SalvagePanelAdd). Any thread.</summary>
    public static bool TrySalvagePanelAdd(uint itemId)
    {
        if (!Active || itemId == 0)
            return false;
        NoteSalvage();
        return SendCommand(BridgeRecordKind.CmdSalvagePanelAdd, itemId, null);
    }

    /// <summary>Salvage button, left to Decal in bridge mode (SalvagePanelSalvage). Any thread.</summary>
    public static bool TrySalvagePanelSalvage()
    {
        if (!Active)
            return false;
        NoteSalvage();
        return SendCommand(BridgeRecordKind.CmdSalvagePanelSalvage, 0, null);
    }

    private static void NoteSalvage()
    {
        if (Interlocked.Exchange(ref _salvageLogged, 1) == 0)
            RynthLog.Info("DecalBridge: salvage is left to Decal in bridge mode (our OpenSalvagePanel hook is not installed): " +
                "adding items and pressing Salvage go through Decal's SalvagePanelAdd/SalvagePanelSalvage. The panel must be open (use an Ust).");
    }

    private static bool SendCommand(BridgeRecordKind kind, uint arg0, string? text)
    {
        lock (CommandSync)
        {
            BridgeChannel? ch;
            lock (Sync) ch = _ch;
            if (ch == null)
                return false;
            bool ok = ch.WriteCommand(kind, arg0, 0, text);
            if (!ok && Interlocked.Exchange(ref _commandDropLogged, 1) == 0)
                RynthLog.Warn($"DecalBridge: command ring full - {kind} dropped (is the bridge running? it drains once per frame).");
            return ok;
        }
    }

    // ── record handling ──────────────────────────────────────────────────────
    private static void Handle(BridgeRecordKind kind, uint arg0, uint arg1, byte* payload, int len)
    {
        int k = (int)kind;
        if (k >= 0 && k < _kindCounts.Length) _kindCounts[k]++;
        switch (kind)
        {
            case BridgeRecordKind.Hello:
                _sawHello = true;
                RynthLog.Info($"DecalBridge: hello from bridge v{arg0}: {Utf16(payload, len)}");
                break;

            case BridgeRecordKind.ChatText:
            {
                string text = Utf16(payload, len);
                FirstFew($"chat type={arg0} target={arg1} '{Trim(text)}'");
                VTankWatch.ObserveChatText(text);   // diagnostic log only
                PluginManager.QueueChatWindowText(text, arg0);
                break;
            }

            case BridgeRecordKind.ChatBarEnter:
            {
                // Runs on the pump thread (the hook ran it on AC's thread, synchronously,
                // so it could eat the line; the bridge now does the eating by prefix).
                string text = Utf16(payload, len);
                FirstFew($"chat-bar '{Trim(text)}' eatenByBridge={arg0 != 0}");
                // Every parser line, eaten or not: /vt start|stop went on to VTank, and is
                // how RynthAi learns that VTank's macro runs (one bot per client).
                VTankWatch.ObserveParserLine(text);
                // Only the lines the bridge ate (our prefixes) are ours to run. Every other
                // line already went on to Decal and AC: handing it to the plugins too would
                // run it twice (RynthAi answers /vt, /ub and /mt as compat commands, and
                // VTank, UB and MagTools already ran them).
                if (arg0 == 0)
                    break;
                // Decal hands the bridge typed lines and its plugins' InvokeChatParser lines (a VTank
                // meta's chat command) alike, so these count as the player's: every reply shows.
                if (RynthCoreChatCommands.TryHandle(text, ChatCommandOrigin.Player))
                    break;
                using (RynthCoreChatCommands.PlayerLine())
                    PluginManager.DispatchChatBarEnter(text);
                break;
            }

            case BridgeRecordKind.CommandResult:
                RynthLog.Warn($"DecalBridge: command {(BridgeRecordKind)arg0} failed in the bridge: {Utf16(payload, len)}");
                break;

            // Streams=all only (the spike's routing). Same side effects as CreateObjectHooks /
            // DeleteObjectHooks: the auto-appraisal queue is what gives RynthAi its creature
            // classification (measured in world 2026-09-29).
            case BridgeRecordKind.CreateObject:
                PluginManager.QueueCreateObject(arg0);
                AutoIdService.Enqueue(arg0);
                break;

            case BridgeRecordKind.ReleaseObject:
                PaletteCache.Remove(arg0);
                PluginManager.QueueDeleteObject(arg0);
                AutoIdService.OnClientRelease(arg0);
                break;

            case BridgeRecordKind.ChangeObject:
                PluginManager.QueueUpdateObject(arg0);
                break;

            case BridgeRecordKind.ServerMessage:
            {
                _serverBytes += len;
                _opcodeCounts[arg0] = _opcodeCounts.TryGetValue(arg0, out long c) ? c + 1 : 1;
                // Decal's Message.RawData for a game event: [F7B0][objectId][sequence][eventType][payload].
                if (arg0 == 0xF7B0 && len >= 16 && *(uint*)payload == 0xF7B0)
                {
                    uint ev = *(uint*)(payload + 12);
                    _gameEventCounts[ev] = _gameEventCounts.TryGetValue(ev, out long g) ? g + 1 : 1;
                    SmartBoxHooks.ProcessBridgeGameEvent(payload + 12, len - 12);
                }
                break;
            }

            case BridgeRecordKind.LoginComplete:
                RynthLog.Info($"DecalBridge: Decal LoginComplete (character 0x{arg0:X8}); engine login seen={LoginLifecycleHooks.HasObservedLoginComplete}.");
                break;

            case BridgeRecordKind.Logoff:
                RynthLog.Info($"DecalBridge: Decal Logoff type={arg0}.");
                VTankWatch.OnLogoff();
                break;
        }
    }

    private static void LogSummary(BridgeChannel ch)
    {
        var sb = new StringBuilder();
        sb.Append($"DecalBridge: summary - hello={_sawHello} records={ch.ReadLong(BridgeLayout.OffRecords)} dropped={ch.ReadLong(BridgeLayout.OffDropped)} ");
        sb.Append($"chat={_kindCounts[2]} chatBar={_kindCounts[3]} commands sent={ch.ReadLong(BridgeLayout.OffCmdWritePos) > 0} run={ch.ReadLong(BridgeLayout.OffCmdDone)} dropped={ch.ReadLong(BridgeLayout.OffCmdDropped)}");
        if (_allStreams)
        {
            sb.Append($" create={_kindCounts[4]} release={_kindCounts[5]} change={_kindCounts[6]} server={_kindCounts[7]} ({_serverBytes / 1024} KB); top opcodes:");
            foreach (var kv in Top(_opcodeCounts, 8)) sb.Append($" {kv.Key:X4}x{kv.Value}");
            sb.Append("; game events:");
            foreach (var kv in Top(_gameEventCounts, 12)) sb.Append($" {kv.Key:X4}x{kv.Value}");
        }
        sb.Append($"; plugins live objects={PluginManager.LiveObjectCount}");
        RynthLog.Info(sb.ToString());
    }

    private static IEnumerable<KeyValuePair<uint, long>> Top(Dictionary<uint, long> d, int n)
    {
        var list = new List<KeyValuePair<uint, long>>(d);
        list.Sort((a, b) => b.Value.CompareTo(a.Value));
        return list.Count > n ? list.GetRange(0, n) : list;
    }

    private static void FirstFew(string line)
    {
        if (_logged >= 20) return;
        _logged++;
        RynthLog.Info("DecalBridge: " + line);
    }

    private static string Trim(string s) => s.Length > 80 ? s.Substring(0, 80) + "..." : s.TrimEnd('\n', '\r');

    private static string Utf16(byte* p, int len) => len >= 2 ? new string((char*)p, 0, len / 2) : string.Empty;

    private static void WritePrefixes(BridgeChannel ch, string prefixes)
    {
        char* dst = (char*)(ch.Header + BridgeLayout.OffPrefixes);
        int n = Math.Min(prefixes.Length, BridgeLayout.PrefixChars - 1);
        for (int i = 0; i < n; i++) dst[i] = prefixes[i];
        dst[n] = '\0';
    }

    // ── decisions ────────────────────────────────────────────────────────────

    /// <summary>RYNTHCORE_DECAL_BRIDGE (test override): 1/on, 0/off, auto.</summary>
    private static DecalBridgeMode? ModeFromEnvironment()
    {
        string? v = Environment.GetEnvironmentVariable("RYNTHCORE_DECAL_BRIDGE");
        if (string.IsNullOrWhiteSpace(v)) return null;
        v = v.Trim();
        if (v == "1" || v.Equals("on", StringComparison.OrdinalIgnoreCase)) return DecalBridgeMode.On;
        if (v == "0" || v.Equals("off", StringComparison.OrdinalIgnoreCase)) return DecalBridgeMode.Off;
        if (v.Equals("auto", StringComparison.OrdinalIgnoreCase)) return DecalBridgeMode.Auto;
        return null;
    }

    /// <summary>
    /// Whether Decal will load the bridge in this client: its filter entry, read through
    /// this (virtualized, 32-bit) process's own view of HKLM - the view Decal reads, which
    /// includes the per-user copy the launcher writes.
    /// </summary>
    private static bool IsBridgeRegistered(out string detail)
    {
        try
        {
            using Microsoft.Win32.RegistryKey? k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(FilterKey);
            if (k == null) { detail = "not registered"; return false; }
            bool enabled = k.GetValue("Enabled") is int e && e != 0;
            string? dir = k.GetValue("Path") as string;
            string? file = k.GetValue("Assembly") as string;
            bool present = dir != null && file != null && File.Exists(Path.Combine(dir, file));
            detail = $"registered, {(enabled ? "enabled" : "DISABLED")}, {(present ? dir : $"DLL missing at {dir}")}";
            return enabled && present;
        }
        catch (Exception ex)
        {
            detail = $"registry read failed: {ex.GetType().Name}";
            return false;
        }
    }

    /// <summary>
    /// Decal in this process? Two GetModuleHandle calls (no module walk). decal.dll, or
    /// Decal's Inject.dll (the first thing a Decal launch loads) next to Decal.Adapter.dll.
    /// Deliberately not DecalDetection: that caches its first answer, and asking it this
    /// early (before decal.dll loads) would cache "no Decal" for the post-login check.
    /// </summary>
    private static string? FindDecalModule()
    {
        try
        {
            IntPtr m = GetModuleHandleW("decal.dll");
            if (m == IntPtr.Zero)
            {
                m = GetModuleHandleW("Inject.dll");
                if (m == IntPtr.Zero)
                    return null;
            }
            char* buf = stackalloc char[520];
            uint n = GetModuleFileNameW(m, buf, 520);
            string path = new string(buf, 0, (int)n);
            string? dir = Path.GetDirectoryName(path);
            return dir != null && File.Exists(Path.Combine(dir, "Decal.Adapter.dll")) ? path : null;
        }
        catch
        {
            return null;
        }
    }
}
