using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using RynthCore.Engine.Hooking;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// DIAGNOSTIC (branch diag/logoff-origin, 2026-09-27): pins down which client code path
/// asks the server to log the character off. Lucy logged off by herself mid-fight at
/// 14:28:20, 15:36:52 and 16:43:30; the ExecuteLogOff caller was the server's
/// CharacterLogOff (0xF653) handler, and ACE only sends that in reply to a logoff the
/// client requested. This probe watches the request side. It changes no behaviour.
///
/// Hooks (all log caller frames via <see cref="LogoutLifecycleHooks.LogCaller"/>):
///   - CPlayerSystem::LogOffCharacter(bool immediate)  every client logoff entry point
///   - CPlayerSystem::RequestLogOff()                  prints "Logging off...", sends
///   - Proto_UI::LogOffCharacter(uint gid)             builds + sends the 0xF653 request
///   - CPlayerSystem::SetLogOffStarted()               ~3 s after the request (timeline)
///   - ClientNet::LogOffServer()                       transport-level disconnect
///
/// Context, dumped once when a logoff is requested (or when one completes with no request):
///   - client input idle time vs InactiveTimeBeforeLogout (the retail client logs itself
///     off from ClientUISystem::UseTime when Timer::cur_time minus CInputManager's
///     last-input time exceeds it; 1200 s on the reference client)
///   - game-window foreground/focus state and when it last changed
///   - ring buffers: the last window messages the WndProc subclass saw (keys, clicks,
///     focus, close, syscommand) and the last chat lines through the engine's chat paths
///   - an acclient-only stack sweep (return addresses above the detour; may be stale)
///
/// Idle milestones are also logged as the idle time climbs toward the threshold, and the
/// heartbeat line carries idle=Ns.
///
/// CommandInterpreter::HandleLogOff (0x006B4270) is NOT hooked: it is a two-instruction
/// thunk to CommandInterpreter::Disable (vtable +0x84) that runs as a consequence of a
/// logoff, not a requester.
/// </summary>
internal static unsafe class LogoffOriginProbe
{
    // Fallback VAs on the reference retail client (4,841,472 bytes, SizeOfImage 0x56D000).
    // The patterns below are the source of truth (cut + verified with tools/pe_pattern.py).
    private const int ProbeLogOffCharacterVa  = 0x005642C0; // CPlayerSystem::LogOffCharacter(bool)
    private const int ProbeRequestLogOffVa    = 0x00563B70; // CPlayerSystem::RequestLogOff()
    private const int ProbeProtoLogOffVa      = 0x005475E0; // Proto_UI::LogOffCharacter(uint gid), cdecl
    private const int ProbeSetLogOffStartedVa = 0x0055E4F0; // CPlayerSystem::SetLogOffStarted()
    private const int ProbeLogOffServerVa     = 0x00544AB0; // ClientNet::LogOffServer()
    private const int ProbeTimerCurTimeVa     = 0x008379A8; // Timer::cur_time (double, seconds)
    private const int ProbeInputManagerVa     = 0x00837FF4; // ICIDM::s_cidm (CInputManager*)
    private const int ProbeIdleLimitVa        = 0x007CEB70; // InactiveTimeBeforeLogout (double, 1200.0)

    private const int ReferenceImageSize = 0x0056D000;

    // push esi; mov esi,ecx; push 0; lea ecx,[esi+0x30]; call; mov al,[esp+8]; test al,al
    private static readonly byte?[] LogOffCharacterPattern = [ 0x56, 0x8B, 0xF1, 0x6A, 0x00, 0x8D, 0x4E, 0x30, 0xE8, null, null, null, null, 0x8A, 0x44, 0x24, 0x08, 0x84, 0xC0 ];
    // push ecx; push esi; push edi; push "Logging off...\n"; mov esi,ecx; push 0; lea ecx,[esp+0x10]; call
    private static readonly byte?[] RequestLogOffPattern = [ 0x51, 0x56, 0x57, 0x68, null, null, null, null, 0x8B, 0xF1, 0x6A, 0x00, 0x8D, 0x4C, 0x24, 0x10, 0xE8 ];
    // push 8; call alloc; mov ecx,[esp+8]; push 8; mov dword ptr [eax],0xF653
    private static readonly byte?[] ProtoLogOffPattern = [ 0x6A, 0x08, 0xE8, null, null, null, null, 0x8B, 0x4C, 0x24, 0x08, 0x6A, 0x08, 0xC7, 0x00, 0x53, 0xF6, 0x00, 0x00 ];
    // mov byte ptr [ecx+0x221],0; ret
    private static readonly byte?[] SetLogOffStartedPattern = [ 0xC6, 0x81, 0x21, 0x02, 0x00, 0x00, 0x00, 0xC3 ];
    // push ebp; push esi; push edi; mov ebp,ecx; mov esi,[ebp+0x8820]; push 0x1C; call
    private static readonly byte?[] LogOffServerPattern = [ 0x55, 0x56, 0x57, 0x8B, 0xE9, 0x8B, 0xB5, 0x20, 0x88, 0x00, 0x00, 0x6A, 0x1C, 0xE8 ];

    // Data globals by code-xref (operand at offset 2).
    private static readonly byte?[] CurTimeXrefPattern = [ 0xDD, 0x05, null, null, null, null, 0x56, 0xDD ];
    // ClientUISystem::UseTime: mov ebp,[ICIDM::s_cidm]; push esi; push edi; lea eax,[esp+0x10]
    private static readonly byte?[] InputManagerXrefPattern = [ 0x8B, 0x2D, null, null, null, null, 0x56, 0x57, 0x8D, 0x44, 0x24, 0x10 ];
    // ClientUISystem::UseTime: fcomp qword ptr [InactiveTimeBeforeLogout]; fnstsw ax; test ah,0x41; jne; cmp
    private static readonly byte?[] IdleLimitXrefPattern = [ 0xDC, 0x1D, null, null, null, null, 0xDF, 0xE0, 0xF6, 0xC4, 0x41, 0x75, 0x22, 0x3B ];

    private static IntPtr _origLogOffCharacter;
    private static IntPtr _origRequestLogOff;
    private static IntPtr _origProtoLogOff;
    private static IntPtr _origSetLogOffStarted;
    private static IntPtr _origLogOffServer;

    private static IntPtr _curTimeVa;
    private static IntPtr _inputMgrSlotVa;
    private static IntPtr _idleLimitVa;
    private static int _lastInputOffset = -1;
    private static bool _inputOffsetProbed;

    private static long _acBase;
    private static long _acImageSize;
    private static long _acTextLo;
    private static long _acTextHi;
    private static bool _labelsValid;
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized)
            return;
        _initialized = true;

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection text))
        {
            RynthLog.Compat("LogoffProbe: acclient.exe not available - probe not installed.");
            return;
        }

        _acBase = text.ModuleBase.ToInt64();
        _acImageSize = text.ImageSize;
        _acTextLo = text.TextBaseVa;
        _acTextHi = text.TextBaseVa + (long)text.Bytes.Length;
        _labelsValid = text.ImageSize == ReferenceImageSize;

        _curTimeVa = HookResolver.ResolveData(text, "LogoffProbe.Timer_cur_time", CurTimeXrefPattern, 2, ProbeTimerCurTimeVa).Address;
        _inputMgrSlotVa = HookResolver.ResolveData(text, "LogoffProbe.ICIDM_s_cidm", InputManagerXrefPattern, 2, ProbeInputManagerVa).Address;
        _idleLimitVa = HookResolver.ResolveData(text, "LogoffProbe.InactiveTimeBeforeLogout", IdleLimitXrefPattern, 2, ProbeIdleLimitVa).Address;

        try
        {
            double limit = _idleLimitVa != IntPtr.Zero ? *(double*)_idleLimitVa : double.NaN;
            RynthLog.Compat($"LogoffProbe: client idle auto-logoff threshold (InactiveTimeBeforeLogout @ 0x{_idleLimitVa.ToInt32():X8}) = {limit:0.#} s. ClientUISystem::UseTime calls LogOffCharacter(0) once Timer::cur_time - last input exceeds it.");
        }
        catch { }

        int hooked = 0;
        HookResolver.ResolveResult r;

        r = HookResolver.Resolve(text, "LogoffProbe.LogOffCharacter", LogOffCharacterPattern, ProbeLogOffCharacterVa);
        delegate* unmanaged[Thiscall]<IntPtr, byte, void> dLogOffCharacter = &LogOffCharacterDetour;
        if (TryHook(r, "CPlayerSystem::LogOffCharacter", (IntPtr)dLogOffCharacter, out _origLogOffCharacter)) hooked++;

        r = HookResolver.Resolve(text, "LogoffProbe.RequestLogOff", RequestLogOffPattern, ProbeRequestLogOffVa);
        delegate* unmanaged[Thiscall]<IntPtr, void> dRequestLogOff = &RequestLogOffDetour;
        if (TryHook(r, "CPlayerSystem::RequestLogOff", (IntPtr)dRequestLogOff, out _origRequestLogOff)) hooked++;

        r = HookResolver.Resolve(text, "LogoffProbe.Proto_UI_LogOffCharacter", ProtoLogOffPattern, ProbeProtoLogOffVa);
        delegate* unmanaged[Cdecl]<uint, int> dProtoLogOff = &ProtoLogOffDetour;
        if (TryHook(r, "Proto_UI::LogOffCharacter", (IntPtr)dProtoLogOff, out _origProtoLogOff)) hooked++;

        r = HookResolver.Resolve(text, "LogoffProbe.SetLogOffStarted", SetLogOffStartedPattern, ProbeSetLogOffStartedVa);
        delegate* unmanaged[Thiscall]<IntPtr, void> dSetLogOffStarted = &SetLogOffStartedDetour;
        if (TryHook(r, "CPlayerSystem::SetLogOffStarted", (IntPtr)dSetLogOffStarted, out _origSetLogOffStarted)) hooked++;

        r = HookResolver.Resolve(text, "LogoffProbe.ClientNet_LogOffServer", LogOffServerPattern, ProbeLogOffServerVa);
        delegate* unmanaged[Thiscall]<IntPtr, void> dLogOffServer = &LogOffServerDetour;
        if (TryHook(r, "ClientNet::LogOffServer", (IntPtr)dLogOffServer, out _origLogOffServer)) hooked++;

        RynthLog.Compat($"LogoffProbe: {hooked}/5 logoff-origin hooks installed (labels {(_labelsValid ? "on" : "off - not the reference client")}).");
    }

    private static bool TryHook(HookResolver.ResolveResult r, string name, IntPtr detour, out IntPtr original)
    {
        original = IntPtr.Zero;
        if (!r.Success)
        {
            RynthLog.Compat($"LogoffProbe: {name} unresolved - not hooked.");
            return false;
        }
        try
        {
            MinHook.Hook(r.Address, detour, out original);
            RynthLog.Compat($"LogoffProbe: {name} hooked @ 0x{r.Address.ToInt32():X8} ({r.Detail}).");
            return true;
        }
        catch (Exception ex)
        {
            original = IntPtr.Zero;
            RynthLog.Compat($"LogoffProbe: {name} hook failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // ── Detours (game thread; must never throw into AC) ──────────────────────

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvThiscall) })]
    private static void LogOffCharacterDetour(IntPtr thisPtr, byte immediate)
    {
        try { OnRequest("CPlayerSystem::LogOffCharacter", $"immediate={immediate}"); }
        catch { }
        ((delegate* unmanaged[Thiscall]<IntPtr, byte, void>)_origLogOffCharacter)(thisPtr, immediate);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvThiscall) })]
    private static void RequestLogOffDetour(IntPtr thisPtr)
    {
        try { OnRequest("CPlayerSystem::RequestLogOff", ""); }
        catch { }
        ((delegate* unmanaged[Thiscall]<IntPtr, void>)_origRequestLogOff)(thisPtr);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int ProtoLogOffDetour(uint gid)
    {
        try
        {
            Volatile.Write(ref _lastProtoSendMs, Environment.TickCount64);
            OnRequest("Proto_UI::LogOffCharacter", $"gid=0x{gid:X8} (sends 0xF653 to the server)");
        }
        catch { }
        return ((delegate* unmanaged[Cdecl]<uint, int>)_origProtoLogOff)(gid);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvThiscall) })]
    private static void SetLogOffStartedDetour(IntPtr thisPtr)
    {
        try
        {
            string since = _lastRequestMs != 0 ? $" {(Environment.TickCount64 - _lastRequestMs) / 1000.0:0.00}s after the request" : " (no request seen)";
            RynthLog.Info($"LogoffProbe: CPlayerSystem::SetLogOffStarted{since}.");
            LogoutLifecycleHooks.LogCaller("SetLogOffStarted", "LogoffProbe");
        }
        catch { }
        ((delegate* unmanaged[Thiscall]<IntPtr, void>)_origSetLogOffStarted)(thisPtr);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvThiscall) })]
    private static void LogOffServerDetour(IntPtr thisPtr)
    {
        try
        {
            RynthLog.Info("LogoffProbe: ClientNet::LogOffServer (transport disconnect) entered.");
            LogoutLifecycleHooks.LogCaller("ClientNet::LogOffServer", "LogoffProbe");
        }
        catch { }
        ((delegate* unmanaged[Thiscall]<IntPtr, void>)_origLogOffServer)(thisPtr);
    }

    // ── Request / completion tracking ────────────────────────────────────────

    private static long _lastRequestMs;
    private static string? _lastRequestDesc;
    private static long _lastProtoSendMs;
    private static long _lastDumpMs;

    private static void OnRequest(string fn, string args)
    {
        // First, before any logging: AC frees UI objects during the logoff.
        ChatHooks.OnLogoffRequested();
        RadarHooks.OnLogoffRequested();
        RetailVitalsHooks.OnLogoffRequested();
        RetailToolbarHooks.OnLogoffRequested();
        long now = Environment.TickCount64;
        IntPtr caller = LogoutLifecycleHooks.LogCaller(fn, "LogoffProbe");
        string callerText = DescribeAcclientAddress(caller);

        string idleText = "idle=?";
        bool idleOver = false;
        if (TryReadIdle(out double idle, out double limit, out _, out _))
        {
            idleOver = idle > limit;
            idleText = $"idle={idle:0.0}s/{limit:0.#}s{(idleOver ? " (OVER threshold: matches the client idle auto-logoff)" : "")}";
        }

        string argText = args.Length > 0 ? $"({args})" : "()";
        RynthLog.Warn($"LogoffProbe: logoff requested - {fn}{argText} caller={callerText} {idleText} {DescribeFocus()}");

        // The first request of a cycle names the path; later ones (RequestLogOff, the send)
        // are the same logoff continuing, so keep the original description.
        if (_lastRequestMs == 0 || now - _lastRequestMs > 30_000)
        {
            _lastRequestMs = now;
            _lastRequestDesc = $"{fn}{argText} from {callerText}";
        }

        if (now - _lastDumpMs > 30_000)
        {
            _lastDumpMs = now;
            DumpContext($"request via {fn}");
        }
    }

    /// <summary>
    /// Called once per logout cycle from the ExecuteLogOff / RecvNotice_Logoff detours.
    /// Says whether the client asked for this logoff, and if not, dumps the context.
    /// </summary>
    internal static void OnLogoffCompleted(string via)
    {
        try
        {
            long now = Environment.TickCount64;
            long req = _lastRequestMs;
            if (req != 0 && now - req < 120_000)
            {
                long send = Volatile.Read(ref _lastProtoSendMs);
                string sendText = send != 0 && now - send < 120_000 ? $"{(now - send) / 1000.0:0.00}s after the 0xF653 send" : "no 0xF653 send seen";
                RynthLog.Info($"LogoffProbe: logoff completed via {via} {(now - req) / 1000.0:0.00}s after the client request ({sendText}). Request: {_lastRequestDesc}.");
            }
            else
            {
                RynthLog.Warn($"LogoffProbe: logoff completed via {via} with NO client logoff request in the last 120 s - server-initiated, or a path this probe does not hook.");
                if (now - _lastDumpMs > 30_000)
                {
                    _lastDumpMs = now;
                    DumpContext($"completion via {via} without a request");
                }
            }
            _lastRequestMs = 0;
            _lastRequestDesc = null;
        }
        catch { }
    }

    // ── Idle time (client's own idle-logoff clock) ───────────────────────────

    private static long _lastIdleSampleMs;
    private static int _idleSecondsForHb = -1;
    private static double _prevIdle;
    private static int _idleMilestoneIdx;
    private static bool _idleOverLogged;
    private static bool _idleDisabledLogged;
    // Seconds before the threshold at which a milestone line is logged.
    private static readonly int[] IdleMilestonesBefore = [900, 600, 300, 120, 60, 30, 10];

    /// <summary>Client input idle seconds for the heartbeat line, or -1 when unknown.</summary>
    internal static int IdleSecondsForHeartbeat => Volatile.Read(ref _idleSecondsForHb);

    /// <summary>
    /// Called from the Client::UseTime detour (AC's game thread). Samples once a second.
    /// </summary>
    internal static void SampleIdleOnGameThread()
    {
        long now = Environment.TickCount64;
        if (now - _lastIdleSampleMs < 1000)
            return;
        _lastIdleSampleMs = now;

        try
        {
            if (!TryReadIdle(out double idle, out double limit, out _, out _))
            {
                Volatile.Write(ref _idleSecondsForHb, -1);
                return;
            }
            Volatile.Write(ref _idleSecondsForHb, idle < 0 ? 0 : (int)Math.Min(idle, int.MaxValue));

            // Input arrived since the last sample: the client reset its idle clock.
            if (idle + 1.5 < _prevIdle)
            {
                if (_idleMilestoneIdx > 0 || _idleOverLogged)
                    RynthLog.Info($"LogoffProbe: client input idle clock reset after {_prevIdle:0}s (input received). {DescribeFocus()}");
                _idleMilestoneIdx = 0;
                _idleOverLogged = false;
            }
            _prevIdle = idle;

            // ClientUISystem::UseTime only logs off an in-world player; skip the
            // milestone lines at character select.
            bool inWorld = false;
            try { inWorld = LoginLifecycleHooks.HasObservedLoginComplete; } catch { }
            if (!inWorld)
            {
                // Before LoginComplete too (2026-10-13): a relaunched client that never had any
                // input carries an idle time of years (last input 0), and AC logged Drakkon off
                // 0.7 s after he entered the world, before LoginComplete arrived, so the
                // in-world refresh below never ran. Keep the clock fresh at char-select as well.
                if (Plugins.EngineSettings.PreventIdleLogoff && idle > IdleRefreshAfterSec && TryRefreshLastInput() && !_preWorldRefreshLogged)
                {
                    _preWorldRefreshLogged = true;
                    RynthLog.Info($"LogoffProbe: reset the client's idle clock before entering the world ({idle:0}s idle) so the idle auto-logoff can't fire on arrival (PreventIdleLogoff; logged once).");
                }
                return;
            }

            if (double.IsNaN(limit) || double.IsInfinity(limit) || limit <= 60)
            {
                if (!_idleDisabledLogged)
                {
                    _idleDisabledLogged = true;
                    RynthLog.Info($"LogoffProbe: idle auto-logoff threshold is {limit} - milestones off.");
                }
                return;
            }

            // The client counts only real keyboard/mouse input, so a character a bot is
            // playing idles out after InactiveTimeBeforeLogout (1200 s) and logs itself off
            // mid-fight - Lucy, three times on 2026-09-27, each 1206 s after the window lost
            // focus. Keep the idle clock fresh while in the world (PreventIdleLogoff).
            if (Plugins.EngineSettings.PreventIdleLogoff && idle > IdleRefreshAfterSec && TryRefreshLastInput())
            {
                long nowMs = Environment.TickCount64;
                if (nowMs - _lastIdleRefreshLogMs > 600_000)
                {
                    _lastIdleRefreshLogMs = nowMs;
                    RynthLog.Info($"LogoffProbe: reset the client's idle clock at {idle:0}s idle so the {limit:0.#}s idle auto-logoff can't fire (PreventIdleLogoff; logged at most every 10 min). {DescribeFocus()}");
                }
                _prevIdle = 0;
                return;
            }

            while (_idleMilestoneIdx < IdleMilestonesBefore.Length && idle >= limit - IdleMilestonesBefore[_idleMilestoneIdx])
            {
                RynthLog.Info($"LogoffProbe: client input idle {idle:0}s - the client auto-logs-off at {limit:0.#}s ({limit - idle:0}s left). {DescribeFocus()}");
                _idleMilestoneIdx++;
            }

            if (!_idleOverLogged && idle > limit)
            {
                _idleOverLogged = true;
                RynthLog.Warn($"LogoffProbe: client input idle {idle:0.0}s is over the {limit:0.#}s threshold - ClientUISystem::UseTime should call LogOffCharacter(0) now. {DescribeFocus()}");
            }
        }
        catch { }
    }

    private const double IdleRefreshAfterSec = 120;
    private static bool _preWorldRefreshLogged;
    private static long _lastIdleRefreshLogMs = -600_000;

    /// <summary>Sets the input manager's last-input time to Timer::cur_time, as real input
    /// would. Game thread only (called from SampleIdleOnGameThread).</summary>
    private static bool TryRefreshLastInput()
    {
        if (_curTimeVa == IntPtr.Zero || _inputMgrSlotVa == IntPtr.Zero)
            return false;
        IntPtr mgr = *(IntPtr*)_inputMgrSlotVa;
        if (mgr == IntPtr.Zero)
            return false;
        int off = ResolveLastInputOffset(mgr);
        if (off < 0)
            return false;
        *(double*)((byte*)mgr + off) = *(double*)_curTimeVa;
        return true;
    }

    /// <summary>Reads the same values ClientUISystem::UseTime compares. Game thread only.</summary>
    private static bool TryReadIdle(out double idle, out double limit, out double curTime, out double lastInput)
    {
        idle = 0; limit = double.NaN; curTime = 0; lastInput = 0;
        if (_curTimeVa == IntPtr.Zero || _inputMgrSlotVa == IntPtr.Zero)
            return false;

        IntPtr mgr = *(IntPtr*)_inputMgrSlotVa;
        if (mgr == IntPtr.Zero)
            return false;

        int off = ResolveLastInputOffset(mgr);
        if (off < 0)
            return false;

        curTime = *(double*)_curTimeVa;
        lastInput = *(double*)((byte*)mgr + off);
        if (_idleLimitVa != IntPtr.Zero)
            limit = *(double*)_idleLimitVa;
        idle = curTime - lastInput;
        return true;
    }

    /// <summary>
    /// ClientUISystem::UseTime gets the last-input time from vtable slot 5 of the input
    /// manager, CInputManager::GetLastInputTimestamp, which is "fld qword [ecx+off]; ret".
    /// Read the field offset out of that body so the probe never guesses a layout.
    /// </summary>
    private static int ResolveLastInputOffset(IntPtr mgr)
    {
        if (_inputOffsetProbed)
            return _lastInputOffset;
        _inputOffsetProbed = true;

        long vtbl = (*(IntPtr*)mgr).ToInt64();
        if (vtbl < _acBase || vtbl + 0x18 > _acBase + _acImageSize)
        {
            RynthLog.Compat($"LogoffProbe: input manager vtable 0x{vtbl:X8} is outside acclient - idle probe off.");
            return -1;
        }
        long fn = (*(IntPtr*)(vtbl + 0x14)).ToInt64();
        if (fn < _acTextLo || fn + 4 > _acTextHi)
        {
            RynthLog.Compat($"LogoffProbe: GetLastInputTimestamp 0x{fn:X8} is outside acclient .text - idle probe off.");
            return -1;
        }
        byte* p = (byte*)fn;
        if (p[0] == 0xDD && p[1] == 0x41 && p[3] == 0xC3)
        {
            _lastInputOffset = p[2];
            RynthLog.Compat($"LogoffProbe: CInputManager last-input time at +0x{_lastInputOffset:X2} (GetLastInputTimestamp @ 0x{fn:X8}).");
        }
        else
        {
            RynthLog.Compat($"LogoffProbe: GetLastInputTimestamp @ 0x{fn:X8} has unexpected bytes {p[0]:X2} {p[1]:X2} {p[2]:X2} {p[3]:X2} - idle probe off.");
        }
        return _lastInputOffset;
    }

    // ── Ring buffers ─────────────────────────────────────────────────────────

    private const uint WM_DESTROY = 0x0002;
    private const uint WM_ACTIVATE = 0x0006;
    private const uint WM_SETFOCUS = 0x0007;
    private const uint WM_KILLFOCUS = 0x0008;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_QUERYENDSESSION = 0x0011;
    private const uint WM_ENDSESSION = 0x0016;
    private const uint WM_ACTIVATEAPP = 0x001C;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const uint WM_SYSKEYDOWN = 0x0104;
    private const uint WM_SYSKEYUP = 0x0105;
    private const uint WM_SYSCOMMAND = 0x0112;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_MBUTTONDOWN = 0x0207;
    private const uint WM_XBUTTONDOWN = 0x020B;

    private struct MsgEntry
    {
        public long Ms;
        public uint Msg;
        public int WParam;
        public int LParam;
        public bool Synthetic;
    }

    private struct ChatEntry
    {
        public long Ms;
        public string Source;
        public string Text;
    }

    private const int MsgRingSize = 24;
    private const int ChatRingSize = 16;
    private static readonly MsgEntry[] _msgRing = new MsgEntry[MsgRingSize];
    private static readonly ChatEntry[] _chatRing = new ChatEntry[ChatRingSize];
    private static int _msgCount;
    private static int _chatCount;
    private static readonly object _ringLock = new();

    private static long _lastMouseMoveMs;
    private static long _lastRealKeyMs;
    private static long _lastRealClickMs;
    private static long _lastKillFocusMs;
    private static long _lastSetFocusMs;

    /// <summary>
    /// Records an input/focus/close message seen by the game WndProc subclass
    /// (<paramref name="synthetic"/> = false) or sent by the engine through
    /// Win32Backend.SendToGameWndProc (true). Cheap filter first; never throws.
    /// </summary>
    internal static void RecordWndMsg(uint msg, IntPtr wParam, IntPtr lParam, bool synthetic)
    {
        try
        {
            long now;
            switch (msg)
            {
                case WM_MOUSEMOVE:
                    if (!synthetic) Volatile.Write(ref _lastMouseMoveMs, Environment.TickCount64);
                    return;
                case WM_KEYDOWN:
                case WM_SYSKEYDOWN:
                    // Skip auto-repeat (bit 30 = key was already down) so a held key can't flood the ring.
                    if (((long)lParam & 0x40000000) != 0) return;
                    now = Environment.TickCount64;
                    if (!synthetic) Volatile.Write(ref _lastRealKeyMs, now);
                    break;
                case WM_KEYUP:
                case WM_SYSKEYUP:
                    now = Environment.TickCount64;
                    if (!synthetic) Volatile.Write(ref _lastRealKeyMs, now);
                    break;
                case WM_LBUTTONDOWN:
                case WM_RBUTTONDOWN:
                case WM_MBUTTONDOWN:
                case WM_XBUTTONDOWN:
                    now = Environment.TickCount64;
                    if (!synthetic) Volatile.Write(ref _lastRealClickMs, now);
                    break;
                case WM_SETFOCUS:
                    now = Environment.TickCount64;
                    Volatile.Write(ref _lastSetFocusMs, now);
                    break;
                case WM_KILLFOCUS:
                    now = Environment.TickCount64;
                    Volatile.Write(ref _lastKillFocusMs, now);
                    break;
                case WM_DESTROY:
                case WM_ACTIVATE:
                case WM_CLOSE:
                case WM_QUERYENDSESSION:
                case WM_ENDSESSION:
                case WM_ACTIVATEAPP:
                case WM_SYSCOMMAND:
                    now = Environment.TickCount64;
                    break;
                default:
                    return;
            }

            lock (_ringLock)
            {
                ref MsgEntry e = ref _msgRing[_msgCount % MsgRingSize];
                e.Ms = now;
                e.Msg = msg;
                e.WParam = (int)(long)wParam;
                e.LParam = (int)(long)lParam;
                e.Synthetic = synthetic;
                _msgCount++;
            }
        }
        catch { }
    }

    /// <summary>Records a chat line passing through one of the engine's chat paths.</summary>
    internal static void RecordChat(string source, string? text)
    {
        try
        {
            if (text == null) return;
            string t = text.Length > 120 ? text.Substring(0, 120) + "..." : text;
            lock (_ringLock)
            {
                ref ChatEntry e = ref _chatRing[_chatCount % ChatRingSize];
                e.Ms = Environment.TickCount64;
                e.Source = source;
                e.Text = t;
                _chatCount++;
            }
        }
        catch { }
    }

    // ── Context dump ─────────────────────────────────────────────────────────

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetFocus();
    [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("kernel32.dll")] private static extern void GetCurrentThreadStackLimits(out IntPtr lowLimit, out IntPtr highLimit);

    private static string Ago(long ms, long now)
        => ms == 0 ? "never" : $"{(now - ms) / 1000.0:0.0}s ago";

    private static string DescribeFocus()
    {
        try
        {
            IntPtr game = ImGuiBackend.Win32Backend.GameHwnd;
            long now = Environment.TickCount64;
            IntPtr fg = GetForegroundWindow();
            int fgGame = game != IntPtr.Zero && fg == game ? 1 : 0;
            int focusGame = game != IntPtr.Zero && GetFocus() == game ? 1 : 0;
            int iconic = game != IntPtr.Zero && IsIconic(game) ? 1 : 0;
            return $"fg={fgGame} focus={focusGame} min={iconic} killfocus={Ago(Volatile.Read(ref _lastKillFocusMs), now)} setfocus={Ago(Volatile.Read(ref _lastSetFocusMs), now)}";
        }
        catch { return "fg=?"; }
    }

    private static void DumpContext(string trigger)
    {
        try
        {
            long now = Environment.TickCount64;
            DateTime wallNow = DateTime.Now;
            var sb = new StringBuilder();
            sb.Append($"LogoffProbe: ---- context dump ({trigger}) ----");

            if (TryReadIdle(out double idle, out double limit, out double cur, out double last))
                sb.Append($"\n  idle: Timer::cur_time={cur:0.000} lastInput={last:0.000} idle={idle:0.0}s threshold={limit:0.#}s");
            else
                sb.Append("\n  idle: unavailable");

            IntPtr game = ImGuiBackend.Win32Backend.GameHwnd;
            IntPtr fg = GetForegroundWindow();
            sb.Append($"\n  window: game=0x{game.ToInt64():X} foreground=0x{fg.ToInt64():X} active=0x{GetActiveWindow().ToInt64():X} focus=0x{GetFocus().ToInt64():X} minimized={(game != IntPtr.Zero && IsIconic(game) ? 1 : 0)}");
            sb.Append($"\n  engine-seen input: key={Ago(Volatile.Read(ref _lastRealKeyMs), now)} click={Ago(Volatile.Read(ref _lastRealClickMs), now)} mousemove={Ago(Volatile.Read(ref _lastMouseMoveMs), now)} killfocus={Ago(Volatile.Read(ref _lastKillFocusMs), now)} setfocus={Ago(Volatile.Read(ref _lastSetFocusMs), now)}");

            sb.Append("\n  acclient stack sweep (may include stale frames):");
            AppendStackSweep(sb);

            MsgEntry[] msgs;
            ChatEntry[] chats;
            int msgCount, chatCount;
            lock (_ringLock)
            {
                msgs = (MsgEntry[])_msgRing.Clone();
                chats = (ChatEntry[])_chatRing.Clone();
                msgCount = _msgCount;
                chatCount = _chatCount;
            }

            int mn = Math.Min(msgCount, MsgRingSize);
            sb.Append($"\n  last {mn} window messages (of {msgCount}):");
            for (int i = msgCount - mn; i < msgCount; i++)
            {
                MsgEntry e = msgs[i % MsgRingSize];
                sb.Append($"\n    {Clock(wallNow, now, e.Ms)} {(e.Synthetic ? "synth " : "")}{DescribeMsg(e)}");
            }

            int cn = Math.Min(chatCount, ChatRingSize);
            sb.Append($"\n  last {cn} engine chat lines (of {chatCount}):");
            for (int i = chatCount - cn; i < chatCount; i++)
            {
                ChatEntry e = chats[i % ChatRingSize];
                sb.Append($"\n    {Clock(wallNow, now, e.Ms)} [{e.Source}] '{e.Text}'");
            }

            sb.Append("\nLogoffProbe: ---- end context dump ----");
            RynthLog.Info(sb.ToString());
        }
        catch (Exception ex)
        {
            try { RynthLog.Info($"LogoffProbe: context dump failed: {ex.GetType().Name}: {ex.Message}"); } catch { }
        }
    }

    private static string Clock(DateTime wallNow, long now, long ms)
        => $"{wallNow.AddMilliseconds(-(now - ms)):HH:mm:ss.fff} ({(ms - now) / 1000.0:0.0}s)";

    private static string DescribeMsg(MsgEntry e)
    {
        switch (e.Msg)
        {
            case WM_KEYDOWN: return $"WM_KEYDOWN vk=0x{e.WParam & 0xFF:X2}{(IsExtended(e.LParam) ? " ext" : "")}";
            case WM_KEYUP: return $"WM_KEYUP vk=0x{e.WParam & 0xFF:X2}{(IsExtended(e.LParam) ? " ext" : "")}";
            case WM_SYSKEYDOWN: return $"WM_SYSKEYDOWN vk=0x{e.WParam & 0xFF:X2} alt={((e.LParam >> 29) & 1)}";
            case WM_SYSKEYUP: return $"WM_SYSKEYUP vk=0x{e.WParam & 0xFF:X2} alt={((e.LParam >> 29) & 1)}";
            case WM_LBUTTONDOWN: return $"WM_LBUTTONDOWN ({(short)(e.LParam & 0xFFFF)},{(short)((e.LParam >> 16) & 0xFFFF)})";
            case WM_RBUTTONDOWN: return $"WM_RBUTTONDOWN ({(short)(e.LParam & 0xFFFF)},{(short)((e.LParam >> 16) & 0xFFFF)})";
            case WM_MBUTTONDOWN: return "WM_MBUTTONDOWN";
            case WM_XBUTTONDOWN: return "WM_XBUTTONDOWN";
            case WM_SETFOCUS: return $"WM_SETFOCUS from=0x{e.WParam:X}";
            case WM_KILLFOCUS: return $"WM_KILLFOCUS to=0x{e.WParam:X}";
            case WM_ACTIVATE: return $"WM_ACTIVATE state={e.WParam & 0xFFFF} other=0x{e.LParam:X}";
            case WM_ACTIVATEAPP: return $"WM_ACTIVATEAPP active={e.WParam} tid={e.LParam}";
            case WM_CLOSE: return "WM_CLOSE";
            case WM_DESTROY: return "WM_DESTROY";
            case WM_QUERYENDSESSION: return "WM_QUERYENDSESSION";
            case WM_ENDSESSION: return $"WM_ENDSESSION ending={e.WParam}";
            case WM_SYSCOMMAND: return $"WM_SYSCOMMAND cmd=0x{e.WParam & 0xFFF0:X4}{((e.WParam & 0xFFF0) == 0xF060 ? " (SC_CLOSE)" : "")}";
            default: return $"msg=0x{e.Msg:X4} w=0x{e.WParam:X} l=0x{e.LParam:X}";
        }
    }

    private static bool IsExtended(int lParam) => ((lParam >> 24) & 1) != 0;

    /// <summary>
    /// Scans this thread's stack above the current frame for values that are return
    /// addresses into acclient .text (preceded by a call instruction). AC is built with
    /// frame-pointer omission, so the EBP walk in LogCaller stops at the first AC frame;
    /// this recovers the AC callers above it. Stale values from earlier calls can appear.
    /// </summary>
    private static void AppendStackSweep(StringBuilder sb)
    {
        int local = 0;
        long start = (long)(nint)(&local);
        GetCurrentThreadStackLimits(out IntPtr lowLimit, out IntPtr highLimit);
        long high = highLimit.ToInt64();
        if (start < lowLimit.ToInt64() || start >= high)
        {
            sb.Append(" <stack limits unavailable>");
            return;
        }
        long end = Math.Min(high, start + 0x3000);
        int hits = 0;
        for (long a = (start + 3) & ~3L; a + 4 <= end && hits < 16; a += 4)
        {
            long v = *(uint*)a;
            if (v < _acTextLo + 8 || v >= _acTextHi)
                continue;
            if (!LooksLikeReturnAddress((byte*)v))
                continue;
            hits++;
            sb.Append($" {DescribeAcclientAddress((IntPtr)v)}");
        }
        if (hits == 0)
            sb.Append(" <none>");
    }

    private static bool LooksLikeReturnAddress(byte* ret)
    {
        if (ret[-5] == 0xE8) return true;                                  // call rel32
        if (ret[-2] == 0xFF && ((ret[-1] >> 3) & 7) == 2 && (ret[-1] >> 6) == 3) return true; // call reg
        if (ret[-3] == 0xFF && ((ret[-2] >> 3) & 7) == 2 && (ret[-2] >> 6) == 1) return true; // call [reg+disp8]
        if (ret[-2] == 0xFF && ((ret[-1] >> 3) & 7) == 2 && (ret[-1] >> 6) == 0) return true; // call [reg]
        if (ret[-6] == 0xFF && ((ret[-5] >> 3) & 7) == 2 && ((ret[-5] >> 6) == 2 || (ret[-5] & 0xC7) == 0x05)) return true; // call [reg+disp32] / [abs]
        return false;
    }

    private static string DescribeAcclientAddress(IntPtr addr)
    {
        if (addr == IntPtr.Zero)
            return "<no acclient frame>";
        int rva = (int)(addr.ToInt64() - _acBase);
        string label = LabelForAcclientRva(rva);
        return label.Length > 0 ? $"acclient.exe+0x{rva:X}{{{label}}}" : $"acclient.exe+0x{rva:X}";
    }

    /// <summary>
    /// Names the known logoff-related call sites by return-address RVA. Reference client
    /// only (labels are off on any other build); the raw module+RVA is always logged.
    /// </summary>
    internal static string LabelForAcclientRva(int rva)
    {
        if (!_labelsValid)
            return "";
        return rva switch
        {
            // callers of CPlayerSystem::LogOffCharacter
            0x166056 => "ClientUISystem::UseTime: idle auto-logoff",
            0x0EB0E9 => "gmGamePlayUI::UseTime: logout confirmed in the UI",
            0x0EAEC8 => "gmGamePlayUI::EndSession",
            0x0EA80F => "gmEpilogueUI ctor",
            0x15AD39 => "APIManager::EndCharacterSession: plugin API",
            // callers of CPlayerSystem::RequestLogOff
            0x1642EE => "CPlayerSystem::LogOffCharacter",
            0x163FC6 => "CPlayerSystem::UseTime: deferred request",
            // caller of Proto_UI::LogOffCharacter
            0x163BC4 => "CPlayerSystem::RequestLogOff",
            // callers of CPlayerSystem::ExecuteLogOff
            0x15D676 => "server CharacterLogOff 0xF653 handler",
            0x1642DC => "CPlayerSystem::LogOffCharacter(immediate)",
            // caller of CPlayerSystem::SetLogOffStarted
            0x0D7AF8 => "gmSmartBoxUI::UseTime",
            // callers of ClientNet::LogOffServer
            0x01160C => "Client::Disconnect",
            0x012077 => "Client::CleanupNet",
            0x145E79 => "ClientNet::ProcessOptionalHeader: server disconnect",
            0x145FEC => "ClientNet::RemoveConnection",
            _ => "",
        };
    }
}
