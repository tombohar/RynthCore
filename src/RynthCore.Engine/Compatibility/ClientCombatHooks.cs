using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// Hooks into the AC client's ClientCombatSystem — the high-level combat pipeline
/// that handles turn-to-face, power bar build, and attack execution naturally.
/// Contrast with CombatActionHooks which sends raw game actions (bypassing facing).
/// </summary>
internal static class ClientCombatHooks
{
    // ── VAs from Chorizite ClientCombatSystem (confirmed from IDA .text comments) ──

    /// <summary>Static cdecl: ClientCombatSystem* GetCombatSystem()</summary>
    private const int GetCombatSystemVa = 0x0056B210;

    /// <summary>Global pointer: ClientCombatSystem* s_pCombatSystem</summary>
    private const int CombatSystemPtrVa = 0x0087166C;

    // Thiscall instance methods on ClientCombatSystem:
    private const int SetRequestedAttackHeightVa = 0x0056D640;
    private const int StartAttackRequestVa       = 0x0056CD90;
    private const int EndAttackRequestVa         = 0x0056CE30;
    private const int PlayerInReadyPositionVa    = 0x0056C570;
    private const int AutoTargetVa               = 0x0056C9D0;

    // Static cdecl CM_Combat function — notifies the client of height change (same as pressing Del/End/PgDn)
    private const int SendAttackHeightChangedVa  = 0x006AAE10;

    // ── Delegates ───────────────────────────────────────────────────────

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetCombatSystemDelegate();

    // Thiscall: first param = this pointer (passed via ECX on x86)
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void SetRequestedAttackHeightDelegate(IntPtr thisPtr, int attackHeight);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void StartAttackRequestDelegate(IntPtr thisPtr);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void EndAttackRequestDelegate(IntPtr thisPtr, int attackHeight, float power);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate byte PlayerInReadyPositionDelegate(IntPtr thisPtr, byte considerAttackingReady);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void AutoTargetDelegate(IntPtr thisPtr);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte SendAttackHeightChangedDelegate(int attackHeight);

    // ── Function pointers ───────────────────────────────────────────────

    private static GetCombatSystemDelegate? _getCombatSystem;
    private static SetRequestedAttackHeightDelegate? _setAttackHeight;
    private static StartAttackRequestDelegate? _startAttackRequest;
    private static EndAttackRequestDelegate? _endAttackRequest;
    private static PlayerInReadyPositionDelegate? _playerInReadyPosition;
    private static AutoTargetDelegate? _autoTarget;
    private static SendAttackHeightChangedDelegate? _sendAttackHeightChanged;

    // ── Pattern-resolved binding (1a hardening, 2026-06-05) ─────────────
    // The *Va consts above are now FALLBACKs. These signatures (verified unique + landing
    // exactly at the VA offline against this client via tools/pe_pattern.py) are the source
    // of truth, so binding survives AC-patch / ACE-rebuild drift. null = wildcard (rel32).
    private static readonly byte?[] PatGetCombatSystem = [ 0xA1, 0x6C, 0x16, 0x87, 0x00, 0xC3, 0x90, 0x90 ];
    private static readonly byte?[] PatSetRequestedAttackHeight = [ 0x8B, 0x51, 0x24, 0x8B, 0x44, 0x24, 0x04, 0x3B ];
    private static readonly byte?[] PatStartAttackRequest = [ 0x51, 0x56, 0x8B, 0xF1, 0xE8, null, null, null, null, 0x8A ];
    private static readonly byte?[] PatEndAttackRequest = [ 0x51, 0x56, 0x8B, 0xF1, 0x8A, 0x46, 0x3E, 0x84, 0xC0, 0x0F ];
    private static readonly byte?[] PatPlayerInReadyPosition = [ 0xA1, 0x58, 0xDA, 0x83, 0x00, 0x83, 0xEC, 0x18 ];
    private static readonly byte?[] PatAutoTarget = [ 0x83, 0xEC, 0x18, 0x53, 0x56, 0x57, 0x8D, 0x44, 0x24, 0x10 ];
    private static readonly byte?[] PatSendAttackHeightChanged = [ 0xE8, null, null, null, null, 0x8B, 0x10, 0x68, 0xFC ];

    // Phase B (1a-data): s_pCombatSystem global (0x0087166C) resolved by code-xref — the unique
    // "mov [s_pCombatSystem],esi" site (89 35 <addr>); read the address from the operand at
    // offset 2. The VA stays as the logged fallback.
    private static readonly byte?[] PatXrefCombatSystemPtr = [ 0x89, 0x35, null, null, null, null, 0x8B, 0x06 ];
    private static IntPtr _combatSystemPtrAddr = new(CombatSystemPtrVa);

    private static T? Bind<T>(AcClientTextSection text, string name, byte?[] pattern, int fallbackVa) where T : Delegate
    {
        HookResolver.ResolveResult r = HookResolver.Resolve(text, name, pattern, fallbackVa);
        return r.Success ? Marshal.GetDelegateForFunctionPointer<T>(r.Address) : null;
    }

    private static bool _initialized;
    private static string _statusMessage = "Not probed yet.";

    public static bool IsInitialized => _initialized;
    public static string StatusMessage => _statusMessage;
    public static bool HasClientCombat => _startAttackRequest != null;

    // ── Probe ───────────────────────────────────────────────────────────

    public static bool Probe()
    {
        try
        {
            if (!AcClientModule.TryReadTextSection(out AcClientTextSection text))
            {
                _statusMessage = "acclient .text not readable for pattern resolve.";
                RynthLog.Compat($"Compat: ClientCombat probe failed - {_statusMessage}");
                return false;
            }

            _combatSystemPtrAddr = HookResolver.ResolveData(text, "ClientCombat.s_pCombatSystem", PatXrefCombatSystemPtr, 2, CombatSystemPtrVa).Address;

            _getCombatSystem = Bind<GetCombatSystemDelegate>(text, "ClientCombat.GetCombatSystem", PatGetCombatSystem, GetCombatSystemVa);
            _setAttackHeight = Bind<SetRequestedAttackHeightDelegate>(text, "ClientCombat.SetRequestedAttackHeight", PatSetRequestedAttackHeight, SetRequestedAttackHeightVa);
            _startAttackRequest = Bind<StartAttackRequestDelegate>(text, "ClientCombat.StartAttackRequest", PatStartAttackRequest, StartAttackRequestVa);
            _endAttackRequest = Bind<EndAttackRequestDelegate>(text, "ClientCombat.EndAttackRequest", PatEndAttackRequest, EndAttackRequestVa);
            _playerInReadyPosition = Bind<PlayerInReadyPositionDelegate>(text, "ClientCombat.PlayerInReadyPosition", PatPlayerInReadyPosition, PlayerInReadyPositionVa);
            _autoTarget = Bind<AutoTargetDelegate>(text, "ClientCombat.AutoTarget", PatAutoTarget, AutoTargetVa);
            _sendAttackHeightChanged = Bind<SendAttackHeightChangedDelegate>(text, "ClientCombat.SendAttackHeightChanged", PatSendAttackHeightChanged, SendAttackHeightChangedVa);

            // Verify the combat system singleton is accessible
            IntPtr cs = _getCombatSystem?.Invoke() ?? IntPtr.Zero;
            IntPtr globalPtr = Marshal.ReadIntPtr(_combatSystemPtrAddr);
            RynthLog.Verbose($"Compat: ClientCombat GetCombatSystem()=0x{cs:X8}, global=0x{globalPtr:X8}");
            if (cs == IntPtr.Zero && globalPtr == IntPtr.Zero)
            {
                _statusMessage = "GetCombatSystem returned null — client not ready.";
                RynthLog.Verbose($"Compat: ClientCombat probe - {_statusMessage}");
                // Don't fail — the pointer may be valid after login
            }
            else
            {
                IntPtr used = cs != IntPtr.Zero ? cs : globalPtr;
                RynthLog.Verbose($"Compat: ClientCombatSystem at 0x{used:X8}");
            }

            _initialized = true;
            _statusMessage = "Ready.";
            RynthLog.Verbose("Compat: ClientCombat hooks ready — native attack pipeline available.");
            return true;
        }
        catch (Exception ex)
        {
            Reset();
            _statusMessage = ex.Message;
            RynthLog.Compat($"Compat: ClientCombat probe failed - {ex.Message}");
            return false;
        }
    }

    // ── Public API ──────────────────────────────────────────────────────

    /// <summary>
    /// Initiate a native attack using the client's combat pipeline.
    /// Sets attack height then calls StartAttackRequest which begins
    /// auto-repeating attacks. The client handles turn-to-face naturally.
    /// Target must be selected via SelectItem first.
    /// </summary>
    /// <summary>
    /// Initiate a native attack using the client's combat pipeline.
    /// Calls StartAttackRequest to begin the power bar, then EndAttackRequest
    /// to fire at the specified height and power. The client handles turn-to-face.
    /// Target must be selected via SelectItem first.
    /// </summary>
    public static bool NativeAttack(int attackHeight, float power)
    {
        if (_startAttackRequest == null || _endAttackRequest == null || _getCombatSystem == null)
        {
            RynthLog.Compat($"ClientCombat: NativeAttack — delegates null (start={_startAttackRequest != null}, end={_endAttackRequest != null}, getCS={_getCombatSystem != null})");
            return false;
        }

        // Deep-audit finding #15 (2026-06-18): this fires 4 AC combat-state
        // mutators (height notify, SetRequestedAttackHeight, StartAttackRequest,
        // EndAttackRequest) that must land in the SAME order as the paired
        // SelectItem that precedes it. Reachable off-thread via the host API,
        // and UseNativeAttack defaults true — so unlike this file's sibling
        // MeleeAttack/MissileAttack, this ran un-marshalled every fight. Marshal
        // as ONE queue entry (not split into four) so the sequence stays atomic
        // relative to the drain loop's other actions.
        if (AcMainThreadQueue.MustQueue(teardown: false))
            return AcMainThreadQueue.EnqueueNativeAttack(attackHeight, power);

        try
        {
            IntPtr cs = _getCombatSystem();
            if (cs == IntPtr.Zero)
            {
                IntPtr globalPtr = Marshal.ReadIntPtr(_combatSystemPtrAddr);
                if (globalPtr == IntPtr.Zero) return false;
                cs = globalPtr;
            }
            if (!ClientObjectHooks.IsReadablePointer(cs)) return false;
            if (!ClientObjectHooks.IsReadableSpan(cs, AttackStateSpan)) return false;

            // Only in Melee or Missile. Elsewhere ExecuteAttack sends nothing but still sets the
            // attack-in-progress flag, which then holds every later native attack (NativeAttackRearm).
            // A queued NativeAttack can run after a stance change the plugin hadn't seen yet.
            int mode = Marshal.ReadInt32(cs + ModeOffset);
            if (!NativeAttackRearm.AttackMode(mode))
            {
                LogRefusedMode(mode);
                return false;
            }
            RearmIfStuck(cs);

            // Notify client of height change via CM_Combat (same path as keyboard Del/End/PgDn)
            _sendAttackHeightChanged?.Invoke(attackHeight);
            _setAttackHeight!(cs, attackHeight);
            _startAttackRequest(cs);
            _endAttackRequest(cs, attackHeight, power);
            _modeChangesAtLastSend = CombatModeHooks.ModeChanges;
            return true;
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"ClientCombat: NativeAttack EXCEPTION - {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // ── The client's attack state (ClientCombatSystem fields) ────────────
    // From the decompile of StartAttackRequest / EndAttackRequest / ExecuteAttack / UseTime /
    // HandleCommenceAttackEvent / HandleAttackDoneEvent (0x0056CD90, 0x0056CE30, 0x0056C8C0,
    // 0x0056CF40, 0x0056BA60, 0x0056D250); +0x3C/+0x3D are also in ClientActionGates' AttackDone
    // pattern and +0x3E in EndAttackRequest's. See NativeAttackRearm for how they wedge.
    private const int ModeOffset         = 0x1C;   // combat mode
    private const int BarStartOffset     = 0x28;   // power bar start time (a double, +0x28..+0x2F)
    private const int BarBuildingOffset  = 0x30;   // power bar filling: UseTime attacks when it is full
    private const int InProgressOffset   = 0x3D;   // attack in progress: set by ExecuteAttack, cleared by AttackDone
    private const int RequestOffset      = 0x3E;   // between StartAttackRequest and EndAttackRequest
    private const int AutoRepeatOffset   = 0x44;   // the client's auto-repeat: AttackDone attacks again
    private const int BarNoAttackOffset  = 0x45;   // 1 = the filling bar only shows, it doesn't attack
    private const int QueuedOffset       = 0x47;   // EndAttackRequest while in progress: AttackDone replays it
    private const int QueuedPowerOffset  = 0x48;   // its power (float)
    private const int AttackStateSpan    = 0x50;

    private static int _modeChangesAtLastSend = -1;   // CombatModeHooks.ModeChanges at the last native attack
    private static long _stuckSinceMs;
    private static long _rearms, _disarms;
    private static long _lastRefusedLogMs = long.MinValue / 2;

    public static long RearmCount => Interlocked.Read(ref _rearms);
    public static long DisarmCount => Interlocked.Read(ref _disarms);

    // MAIN THREAD ONLY (NativeAttack's direct part). Clears a stuck attack-in-progress flag
    // (NativeAttackRearm) so this attack goes out instead of being queued behind it.
    private static void RearmIfStuck(IntPtr cs)
    {
        bool inProgress = Marshal.ReadByte(cs + InProgressOffset) != 0;
        bool? swinging = ClientActionGates.TryReadAttacking(out bool attacking) ? attacking : null;
        bool stanceChanged = CombatModeHooks.ModeChanges != _modeChangesAtLastSend;
        NativeAttackRearm.Verdict v = NativeAttackRearm.Judge(inProgress, swinging, stanceChanged,
            Environment.TickCount64, ref _stuckSinceMs);
        if (v == NativeAttackRearm.Verdict.Leave)
            return;

        byte queued = Marshal.ReadByte(cs + QueuedOffset);
        ClearAttackState(cs, inProgressToo: true);
        long n = Interlocked.Increment(ref _rearms);
        string why = v == NativeAttackRearm.Verdict.AfterStanceChange
            ? "left from before the last stance change"
            : $"for {NativeAttackRearm.StuckMs / 1000}s with no swing";
        RynthLog.Compat($"ClientCombat: the client's attack-in-progress flag was set {why} (queued request {queued}, " +
                        $"server swinging {(swinging == null ? "unknown" : "no")}): cleared, so this native attack goes out " +
                        $"instead of waiting for an AttackDone that never comes (re-arm #{n}).");
    }

    /// <summary>
    /// AC's main thread, after the client's combat mode changed (CombatModeHooks). Out of Melee
    /// and Missile no attack can go out, so a queued request, a filling power bar or the
    /// auto-repeat left from the last stance is cleared: fired in Peace or Magic it would set the
    /// attack-in-progress flag with nothing sent (NativeAttackRearm). The flag itself is left
    /// to the server's AttackDone (a swing may still be finishing); a stuck one is cleared at the
    /// next native attack.
    /// </summary>
    internal static void DisarmOutOfAttackStance(string why)
    {
        if (!MainThreadGuard.IsOnMainThread())
            return;
        try
        {
            IntPtr cs = CombatModeHooks.ReadCombatSystemPtr();
            if (cs == IntPtr.Zero || !ClientObjectHooks.IsReadableSpan(cs, AttackStateSpan))
                return;
            int mode = Marshal.ReadInt32(cs + ModeOffset);
            if (!NativeAttackRearm.DisarmFor(mode))
                return;
            byte queued = Marshal.ReadByte(cs + QueuedOffset);
            byte bar = Marshal.ReadByte(cs + BarBuildingOffset);
            byte barAttacks = (byte)(bar != 0 && Marshal.ReadByte(cs + BarNoAttackOffset) == 0 ? 1 : 0);
            byte request = Marshal.ReadByte(cs + RequestOffset);
            byte repeat = Marshal.ReadByte(cs + AutoRepeatOffset);
            if (queued == 0 && bar == 0 && request == 0 && repeat == 0)
                return;
            ClearAttackState(cs, inProgressToo: false);
            long n = Interlocked.Increment(ref _disarms);
            // Only an attack that could have fired is worth a line (the auto-repeat and a show-only
            // bar are left after every fight with AC's auto-repeat on).
            if (queued != 0 || barAttacks != 0 || request != 0)
                RynthLog.Compat($"ClientCombat: {why}: mode {mode} with an attack still armed (queued request {queued}, " +
                                $"power bar attacking {barAttacks}, request held {request}): disarmed, so it can't fire with nothing sent (disarm #{n}).");
        }
        catch { }
    }

    // MAIN THREAD ONLY. The stores ExecuteAttack and HandleAttackDoneEvent make, all at once.
    private static void ClearAttackState(IntPtr cs, bool inProgressToo)
    {
        if (!MainThreadGuard.IsOnMainThread())
            return;
        Marshal.WriteByte(cs + QueuedOffset, 0);
        Marshal.WriteInt32(cs + QueuedPowerOffset, 0);
        Marshal.WriteByte(cs + RequestOffset, 0);
        if (Marshal.ReadByte(cs + BarBuildingOffset) != 0)
        {
            Marshal.WriteByte(cs + BarBuildingOffset, 0);
            Marshal.WriteInt32(cs + BarStartOffset, 0);
            Marshal.WriteInt32(cs + BarStartOffset + 4, 0);
        }
        Marshal.WriteByte(cs + AutoRepeatOffset, 0);
        if (inProgressToo)
            Marshal.WriteByte(cs + InProgressOffset, 0);
    }

    private static void LogRefusedMode(int mode)
    {
        long now = Environment.TickCount64;
        if (now - _lastRefusedLogMs < 10_000)
            return;
        _lastRefusedLogMs = now;
        RynthLog.Compat($"ClientCombat: NativeAttack refused: the client's combat mode is {mode}, not Melee or Missile " +
                        "(the attack would send nothing and leave the client's attack-in-progress flag set).");
    }

    /// <summary>One line for /rc actionstate: the native attack's re-arms and disarms this session.</summary>
    public static string DescribeRearms() =>
        $"native attack: re-arms {RearmCount} (a stuck attack-in-progress flag cleared), disarms {DisarmCount} (an armed attack cleared out of Melee/Missile)";

    /// <summary>
    /// Check if the player is in a ready position to begin an attack.
    /// </summary>
    public static bool IsPlayerReady()
    {
        if (_playerInReadyPosition == null || _getCombatSystem == null)
            return false;

        // Off AC's main thread (host IsPlayerReadyFn: RynthNet's login probe polls it
        // every 2 s from the plugin pump): the value sampled on the main thread
        // (<= ~200 ms old). ClientCombatSystem::PlayerInReadyPosition walks the
        // player's motion state and the combat singleton, both freed and rebuilt
        // around login / logout, exactly when this is polled.
        if (!MainThreadGuard.IsOnMainThread())
            return Volatile.Read(ref _readySnapshot) != 0;

        bool ready = ReadPlayerReadyLive();
        Volatile.Write(ref _readySnapshot, ready ? 1 : 0);
        return ready;
    }

    // 1 = the main thread last saw the player in a ready position.
    private static int _readySnapshot;
    private static long _nextReadySampleMs;
    private const int ReadySampleIntervalMs = 200;

    /// <summary>
    /// Main thread only (MainThreadSnapshots.Tick). Samples PlayerInReadyPosition for
    /// off-thread IsPlayerReady callers, about five times a second. With no player
    /// object (char-select, portal teardown) it publishes "not ready" without
    /// calling into the combat system.
    /// </summary>
    internal static void SampleReady()
    {
        if (!MainThreadGuard.IsOnMainThread() || _playerInReadyPosition == null || _getCombatSystem == null)
            return;
        long now = Environment.TickCount64;
        if (now < _nextReadySampleMs)
            return;
        _nextReadySampleMs = now + ReadySampleIntervalMs;

        bool ready = ClientHelperHooks.GetPlayerId() != 0 && ReadPlayerReadyLive();
        Volatile.Write(ref _readySnapshot, ready ? 1 : 0);
    }

    // MAIN THREAD ONLY.
    private static bool ReadPlayerReadyLive()
    {
        if (!MainThreadGuard.IsOnMainThread() || _playerInReadyPosition == null || _getCombatSystem == null)
            return false;
        try
        {
            IntPtr cs = _getCombatSystem();
            if (cs == IntPtr.Zero) return false;
            if (!ClientObjectHooks.IsReadablePointer(cs)) return false;
            return _playerInReadyPosition(cs, 1) != 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Auto-target the nearest enemy using the client's targeting system.
    /// </summary>
    public static bool AutoTarget()
    {
        if (_autoTarget == null || _getCombatSystem == null)
            return false;

        // Same mutator class as NativeAttack (audit finding #15). Not currently
        // exposed through the plugin SDK, so there's no live off-thread caller
        // today — fail closed rather than queue, since queuing an unreachable
        // path is dead-weight; revisit with an ActionKind if this is exposed.
        if (!MainThreadGuard.IsOnMainThread())
            return false;

        try
        {
            IntPtr cs = _getCombatSystem();
            if (cs == IntPtr.Zero) return false;
            if (!ClientObjectHooks.IsReadablePointer(cs)) return false;
            _autoTarget(cs);
            return true;
        }
        catch { return false; }
    }

    public static void Reset()
    {
        _getCombatSystem = null;
        _setAttackHeight = null;
        _startAttackRequest = null;
        _endAttackRequest = null;
        _playerInReadyPosition = null;
        _autoTarget = null;
        _sendAttackHeightChanged = null;
        _initialized = false;
        Volatile.Write(ref _readySnapshot, 0);
    }
}
