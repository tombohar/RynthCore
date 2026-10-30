using System;
using System.Runtime.InteropServices;

namespace RynthCore.Engine.Compatibility;

internal static class CommandInterpreterHooks
{
    private const int VtblCommenceJumpIndex = 16;
    private const int VtblDoJumpIndex = 17;
    private const int VtblSetAutoRunIndex = 51;
    private const int ReferenceAccCmdInterpCommenceJump = 0x0058BFD0;
    private const int ReferenceAccCmdInterpDoJump = 0x0058BFF0;
    private const int ReferenceAccCmdInterpSetMotion = 0x0058C140;
    private const int ReferenceCommandInterpreterSetAutoRun = 0x006B5790;
    private const int ReferenceCommandInterpreterStopCompletely = 0x006B4A90;
    private const int ReferenceCommandInterpreterTurnToHeading = 0x006B54B0;
    private const int ReferenceCommandInterpreterPlayerTeleported = 0x006B41F0;
    private const int ReferenceCommandInterpreterTakeControlFromServer = 0x006B4210;
    private const int ReferenceCommandInterpreterClearAllCommands = 0x006B40E0;
    private const int SetMotionOffsetFromCommenceJump = ReferenceAccCmdInterpSetMotion - ReferenceAccCmdInterpCommenceJump;
    private const int SetMotionOffsetFromDoJump = ReferenceAccCmdInterpSetMotion - ReferenceAccCmdInterpDoJump;
    private const int StopCompletelyOffsetFromSetAutoRun = ReferenceCommandInterpreterStopCompletely - ReferenceCommandInterpreterSetAutoRun;
    private const int TurnToHeadingOffsetFromSetAutoRun = ReferenceCommandInterpreterTurnToHeading - ReferenceCommandInterpreterSetAutoRun;
    private const int PlayerTeleportedOffsetFromSetAutoRun = ReferenceCommandInterpreterPlayerTeleported - ReferenceCommandInterpreterSetAutoRun;
    private const int TakeControlOffsetFromSetAutoRun = ReferenceCommandInterpreterTakeControlFromServer - ReferenceCommandInterpreterSetAutoRun;
    private const int ClearAllCommandsOffsetFromSetAutoRun = ReferenceCommandInterpreterClearAllCommands - ReferenceCommandInterpreterSetAutoRun;

    // CommandInterpreter::SendMovementEvent (vtable slot 19 = +0x4C, VA 0x006B55C0, thiscall,
    // no args, plain ret): sends the current raw motion state to the server as a MoveToState
    // (0xF61C). Bound from the live vtable and accepted only when it sits at the reference
    // distance from SetAutoRun (same binary layout); otherwise it stays null and SetAutoRun
    // behaves as before (no send).
    private const int VtblSendMovementEventIndex = 19;
    private const int ReferenceCommandInterpreterSendMovementEvent = 0x006B55C0;
    private const int SendMovementEventOffsetFromSetAutoRun = ReferenceCommandInterpreterSendMovementEvent - ReferenceCommandInterpreterSetAutoRun;

    // CommandInterpreter.autoRun (int): SetAutoRun(val, ...) compares and writes [this+0x48];
    // ToggleAutoRun reads it (acclient 0x006B5790 / 0x006B4C00).
    internal const int CmdInterpAutoRunOffset = 0x48;

    // Verified unique + lands at the Reference VA offline (tools/pe_pattern.py).
    // Primary resolution; the *Offset deltas below remain as fallback only.
    private static readonly byte?[] SetMotionPattern = [ 0x8B, 0x41, 0x08, 0x81, 0xEC, 0x0C, 0x01, 0x00 ];
    private static readonly byte?[] StopCompletelyPattern = [ 0x56, 0x8B, 0xF1, 0x8B, 0x46, 0x04, 0x85, 0xC0, 0x74, 0x32 ];
    private static readonly byte?[] TurnToHeadingPattern = [ 0x83, 0xEC, 0x2C, 0x56, 0x8B, 0xF1, 0x8B, 0x06 ];
    private static readonly byte?[] PlayerTeleportedPattern = [ 0x56, 0x8B, 0xF1, 0x8B, 0x06, 0x6A, 0x01, 0x6A ];
    private static readonly byte?[] TakeControlFromServerPattern = [ 0x56, 0x8B, 0xF1, 0x8B, 0x46, 0x34, 0x85, 0xC0, 0x74 ];
    private static readonly byte?[] ClearAllCommandsPattern = [ 0x56, 0x8B, 0xF1, 0x8D, 0x4E, 0x0C, 0xE8, null, null, null, null, 0x8D, 0x4E, 0x18, 0xE8, null, null, null, null, 0x8D, 0x4E, 0x24, 0x5E ];

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void CommenceJumpDelegate(IntPtr thisPtr);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void DoJumpDelegate(IntPtr thisPtr, int autonomous);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void SetAutoRunDelegate(IntPtr thisPtr, int val, int applyMovement);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void SetMotionDelegate(IntPtr thisPtr, uint motion, int enabled);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void StopCompletelyDelegate(IntPtr thisPtr);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void TurnToHeadingDelegate(IntPtr thisPtr, float headingDegrees);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void PlayerTeleportedDelegate(IntPtr thisPtr);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void TakeControlFromServerDelegate(IntPtr thisPtr);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void ClearAllCommandsDelegate(IntPtr thisPtr);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void SendMovementEventDelegate(IntPtr thisPtr);

    private static IntPtr _smartboxStaticAddr;
    private static IntPtr _boundCmdInterp;
    private static bool _absResolved;
    private static IntPtr _setMotionAbs, _stopCompletelyAbs, _turnToHeadingAbs, _playerTeleportedAbs, _takeControlAbs, _clearAllCommandsAbs;
    private static CommenceJumpDelegate? _commenceJump;
    private static DoJumpDelegate? _doJump;
    private static SetAutoRunDelegate? _setAutoRun;
    private static SetMotionDelegate? _setMotion;
    private static StopCompletelyDelegate? _stopCompletely;
    private static TurnToHeadingDelegate? _turnToHeading;
    private static PlayerTeleportedDelegate? _playerTeleported;
    private static TakeControlFromServerDelegate? _takeControlFromServer;
    private static ClearAllCommandsDelegate? _clearAllCommands;
    private static SendMovementEventDelegate? _sendMovementEvent;
    private static bool _sendMovementEventMissLogged;
    private static long _autoRunMovementSends;
    private static string _statusMessage = "Not probed yet.";

    /// <summary>MoveToStates sent after an autorun change (diagnostics).</summary>
    public static long AutoRunMovementSends => System.Threading.Interlocked.Read(ref _autoRunMovementSends);
    public static bool HasSendMovementEvent => _sendMovementEvent != null;

    public static bool IsInitialized { get; private set; }
    public static bool HasTapJump => _commenceJump != null && _doJump != null;
    public static bool HasSetAutoRun => _setAutoRun != null;
    public static bool HasSetMotion => _setMotion != null;
    public static bool HasStopCompletely => _stopCompletely != null;
    public static bool HasTurnToHeading => _turnToHeading != null;
    public static bool HasTakeControlFromServer => _takeControlFromServer != null;
    public static bool HasClearAllCommands => _clearAllCommands != null;
    public static string StatusMessage => _statusMessage;

    public static bool Probe()
    {
        Reset();

        if (!AcClientModule.TryReadTextSection(out _))
        {
            _statusMessage = "acclient.exe not available.";
            return false;
        }

        try
        {
            if (!SmartBoxLocator.Probe())
            {
                _statusMessage = SmartBoxLocator.StatusMessage;
                return false;
            }

            if (!TryBindDelegates())
            {
                if (string.IsNullOrEmpty(_statusMessage))
                    _statusMessage = "cmdinterp unavailable.";
                return false;
            }

            IsInitialized = true;
            _statusMessage = "Ready.";
            return true;
        }
        catch (Exception ex)
        {
            Reset();
            _statusMessage = ex.Message;
            RynthLog.Compat($"Compat: command interpreter probe failed - {ex.Message}");
            return false;
        }
    }

    // Rate-limited diag counters — see why patrol "creates a route but stands
    // still" by surfacing the actual movement API calls in RynthCore.log.
    private static int _setAutoRunCalls;
    private static int _turnToHeadingCalls;
    private static int _setMotionCalls;
    private static bool ShouldLogMove(int n) => n <= 3 || (n & 0x7) == 0;

    public static bool SetAutoRun(bool enabled)
    {
        // ⚠ Main thread only (2026-06-12 cross-incident forensics): these
        // CommandInterpreter calls were the last direct off-thread AC mutators
        // — dump-proven executing 15s after a main thread had wedged. They
        // rewrite the locomotion channels of the same CommandInterpreter /
        // motion graph the CSequence-AV corruption class kills. Off-thread
        // callers marshal through the action ring (drained post-tick,
        // gesture-gated) like every other mutator.
        if (AcMainThreadQueue.MustQueue(teardown: !enabled))
            return AcMainThreadQueue.EnqueueSetAutoRun(enabled);
        int n = System.Threading.Interlocked.Increment(ref _setAutoRunCalls);
        if (!TryBindDelegates())
        {
            if (ShouldLogMove(n))
                RynthLog.Compat($"Move: SetAutoRun({enabled}) #{n} — TryBindDelegates failed: {_statusMessage}");
            return false;
        }

        try
        {
            // Tell the server, like AC's own autorun key and Decal's SetAutorun (2026-10-12).
            // CommandInterpreter::SetAutoRun(val, applyMovement) only changes the LOCAL motion
            // (MovePlayer -> DoMotion/StopMotion); it never sends a MoveToState. AC's autorun key
            // (HandleKeyboardCommand 0x090000C7) is ToggleAutoRun + SendMovementEvent, and
            // Decal's IACHooks::SetAutorun (Decal.dll 0x18025C10) is SetAutoRun(on, 1) followed by
            // one more no-arg cmdinterp call. Without the send the server never learned that the
            // bot started or stopped running: the client ran on AutonomousPositions alone, and the
            // next SendMovementEvent - a SetMotion turn-off, or the cast's own
            // FreeHandsAndCastSpell -> MaybeStopCompletely -> StopCompletely - delivered a burst
            // of stops for a run the server had never seen (flight 63260, 12:27:44 / 12:35:55 /
            // 12:38:56: every bad burst had no forward command; the harmless ones began with
            // WalkForward). Sent only when AC's flag really changed: nav re-sends autorun every
            // tick and AC ignores an unchanged value, so a send each time would spam the server.
            int before = TryReadAutoRunFlag(_boundCmdInterp);
            _setAutoRun!(_boundCmdInterp, enabled ? 1 : 0, 1);
            int after = TryReadAutoRunFlag(_boundCmdInterp);
            bool sent = false;
            if (before >= 0 && after >= 0 && before != after)
            {
                if (_sendMovementEvent != null)
                {
                    _sendMovementEvent(_boundCmdInterp);
                    System.Threading.Interlocked.Increment(ref _autoRunMovementSends);
                    sent = true;
                }
                else if (!_sendMovementEventMissLogged)
                {
                    _sendMovementEventMissLogged = true;
                    RynthLog.Compat("Move: SetAutoRun changed AC's autorun but SendMovementEvent is not bound - the server is not told (old behavior).");
                }
            }
            if (ShouldLogMove(n))
                RynthLog.Compat($"Move: SetAutoRun({enabled}) #{n} -> ok (cmdInterp=0x{_boundCmdInterp.ToInt32():X8}, autorun {before}->{after}{(sent ? ", MoveToState sent" : "")})");
            return true;
        }
        catch (Exception ex)
        {
            if (ShouldLogMove(n))
                RynthLog.Compat($"Move: SetAutoRun({enabled}) #{n} threw {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // CommandInterpreter.controlled_by_server (int, +0x34): LoseControlToServer sets it to 1,
    // TakeControlFromServer to 0. MaybeStopCompletely (vtable +0xC0, 0x006B4AD0) - the first thing
    // ClientMagicSystem::FreeHandsAndCastSpell (0x00567C90) does - calls StopCompletely (and so
    // sends a MoveToState) only while it is 0, i.e. while the player has control.
    internal const int CmdInterpControlledByServerOffset = 0x34;

    /// <summary>1 the server has control (a cast sends no stop), 0 the player has (a cast sends one), -1 unknown. Main thread.</summary>
    public static int ReadControlledByServer()
    {
        if (!MainThreadGuard.IsOnMainThread() || !TryBindDelegates()) return -1;
        try { return Marshal.ReadInt32(_boundCmdInterp + CmdInterpControlledByServerOffset) != 0 ? 1 : 0; }
        catch { return -1; }
    }

    /// <summary>
    /// For the length of one cast call: makes MaybeStopCompletely see "server has control", so
    /// FreeHandsAndCastSpell sends no stop in the cast's frame. Only for a cast whose stop the
    /// engine already sent a server answer ahead (PreCastStop). Returns the command interpreter to
    /// pass to <see cref="EndCastStopSuppression"/>, or Zero when nothing was changed.
    /// </summary>
    public static IntPtr BeginCastStopSuppression(out int saved)
    {
        saved = 0;
        if (!MainThreadGuard.IsOnMainThread() || !TryBindDelegates()) return IntPtr.Zero;
        try
        {
            IntPtr ci = _boundCmdInterp;
            saved = Marshal.ReadInt32(ci + CmdInterpControlledByServerOffset);
            if (saved != 0) return IntPtr.Zero;               // already no stop: nothing to do
            Marshal.WriteInt32(ci + CmdInterpControlledByServerOffset, 1);
            return ci;
        }
        catch { return IntPtr.Zero; }
    }

    public static void EndCastStopSuppression(IntPtr cmdInterp, int saved)
    {
        if (cmdInterp == IntPtr.Zero || !MainThreadGuard.IsOnMainThread()) return;
        try { Marshal.WriteInt32(cmdInterp + CmdInterpControlledByServerOffset, saved); } catch { }
    }

    /// <summary>AC's autorun flag (0/1), or -1 when it can't be read.</summary>
    private static int TryReadAutoRunFlag(IntPtr cmdInterp)
    {
        if (cmdInterp == IntPtr.Zero) return -1;
        try { return Marshal.ReadInt32(cmdInterp + CmdInterpAutoRunOffset) != 0 ? 1 : 0; }
        catch { return -1; }
    }

    public static bool SetMotion(uint motion, bool enabled)
    {
        if (AcMainThreadQueue.MustQueue(teardown: !enabled))
            return AcMainThreadQueue.EnqueueSetMotion(motion, enabled); // same off-thread class as SetAutoRun
        if (!TryBindDelegates() || _setMotion == null)
            return false;

        try
        {
            _setMotion(_boundCmdInterp, motion, enabled ? 1 : 0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool StopCompletely()
    {
        if (AcMainThreadQueue.MustQueue(teardown: true))
            return AcMainThreadQueue.EnqueueStopCompletely(); // same off-thread class as SetAutoRun
        if (!TryBindDelegates() || _stopCompletely == null)
            return false;

        try
        {
            _stopCompletely(_boundCmdInterp);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TurnToHeading(float headingDegrees)
    {
        if (AcMainThreadQueue.MustQueue(teardown: false))
            return AcMainThreadQueue.EnqueueTurnToHeading(headingDegrees); // same off-thread class as SetAutoRun
        int n = System.Threading.Interlocked.Increment(ref _turnToHeadingCalls);
        if (!TryBindDelegates() || _turnToHeading == null)
        {
            if (ShouldLogMove(n))
                RynthLog.Compat($"Move: TurnToHeading({headingDegrees:0.0}) #{n} — bind={(_turnToHeading != null)} status='{_statusMessage}'");
            return false;
        }

        try
        {
            _turnToHeading(_boundCmdInterp, headingDegrees);
            if (ShouldLogMove(n))
                RynthLog.Compat($"Move: TurnToHeading({headingDegrees:0.0}) #{n} -> ok");
            return true;
        }
        catch (Exception ex)
        {
            if (ShouldLogMove(n))
                RynthLog.Compat($"Move: TurnToHeading({headingDegrees:0.0}) #{n} threw {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // Recovery triplet: unguarded like the jump trio, but only latent today
    // (reached solely from the main-thread-gated ForceResetBusyCount) — public
    // API surface though, so gate defensively rather than add queue plumbing
    // for a currently-unreached off-thread path (revisit with an ActionKind if
    // one of these is ever exposed to off-thread callers).

    public static bool PlayerTeleported()
    {
        if (!MainThreadGuard.IsOnMainThread())
            return false;
        if (!TryBindDelegates() || _playerTeleported == null)
            return false;

        try
        {
            _playerTeleported(_boundCmdInterp);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TakeControlFromServer()
    {
        if (!MainThreadGuard.IsOnMainThread())
            return false;
        if (!TryBindDelegates() || _takeControlFromServer == null)
            return false;

        try
        {
            _takeControlFromServer(_boundCmdInterp);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool ClearAllCommands()
    {
        if (!MainThreadGuard.IsOnMainThread())
            return false;
        if (!TryBindDelegates() || _clearAllCommands == null)
            return false;

        try
        {
            _clearAllCommands(_boundCmdInterp);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Jump trio marshalled 2026-09-02 (deep-audit finding #2): these called the
    // native CommandInterpreter through _boundCmdInterp with zero
    // MainThreadGuard gate, unlike every sibling (SetAutoRun/SetMotion/
    // StopCompletely/TurnToHeading above), live-exercised off-thread via
    // Jumper.cs's Decal-coexistence pump-thread tick.

    public static bool TapJump()
    {
        if (AcMainThreadQueue.MustQueue(teardown: false))
            return AcMainThreadQueue.EnqueueTapJump();
        if (!TryBindDelegates())
            return false;

        try
        {
            // Mirror the normal keyboard jump path: key-down starts charge,
            // key-up immediately executes an autonomous tap jump.
            _commenceJump!(_boundCmdInterp);
            _doJump!(_boundCmdInterp, 1);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool CommenceJump()
    {
        if (AcMainThreadQueue.MustQueue(teardown: false))
            return AcMainThreadQueue.EnqueueCommenceJump();
        if (!TryBindDelegates())
            return false;

        try
        {
            _commenceJump!(_boundCmdInterp);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool DoJump(bool autonomous)
    {
        if (AcMainThreadQueue.MustQueue(teardown: false))
            return AcMainThreadQueue.EnqueueDoJumpAutonomous(autonomous);
        if (!TryBindDelegates())
            return false;

        try
        {
            _doJump!(_boundCmdInterp, autonomous ? 1 : 0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Resolve the six non-vtable-slot member functions by unique .text signature (primary),
    // once and cached — they are static code addresses. On a miss they stay Zero and
    // TryBindDelegates falls back to the live-anchor + hardcoded-delta computation (old behavior).
    private static void EnsureAbsoluteResolved()
    {
        if (_absResolved) return;
        if (!AcClientModule.TryReadTextSection(out AcClientTextSection text)) return; // retry next call
        HookResolver.ResolveResult r;
        r = HookResolver.Resolve(text, "CmdInterp.SetMotion", SetMotionPattern, ReferenceAccCmdInterpSetMotion);
        _setMotionAbs = r.Success ? r.Address : IntPtr.Zero;
        r = HookResolver.Resolve(text, "CmdInterp.StopCompletely", StopCompletelyPattern, ReferenceCommandInterpreterStopCompletely);
        _stopCompletelyAbs = r.Success ? r.Address : IntPtr.Zero;
        r = HookResolver.Resolve(text, "CmdInterp.TurnToHeading", TurnToHeadingPattern, ReferenceCommandInterpreterTurnToHeading);
        _turnToHeadingAbs = r.Success ? r.Address : IntPtr.Zero;
        r = HookResolver.Resolve(text, "CmdInterp.PlayerTeleported", PlayerTeleportedPattern, ReferenceCommandInterpreterPlayerTeleported);
        _playerTeleportedAbs = r.Success ? r.Address : IntPtr.Zero;
        r = HookResolver.Resolve(text, "CmdInterp.TakeControlFromServer", TakeControlFromServerPattern, ReferenceCommandInterpreterTakeControlFromServer);
        _takeControlAbs = r.Success ? r.Address : IntPtr.Zero;
        r = HookResolver.Resolve(text, "CmdInterp.ClearAllCommands", ClearAllCommandsPattern, ReferenceCommandInterpreterClearAllCommands);
        _clearAllCommandsAbs = r.Success ? r.Address : IntPtr.Zero;
        _absResolved = true;
    }

    private static bool TryBindDelegates()
    {
        IntPtr selectedStaticAddr = IntPtr.Zero;
        IntPtr smartBox = IntPtr.Zero;
        IntPtr cmdInterp = IntPtr.Zero;
        IntPtr vtable = IntPtr.Zero;

        if (!SmartBoxLocator.TryGetSmartBox(out smartBox, out selectedStaticAddr, out string smartBoxFailure))
        {
            _statusMessage = smartBoxFailure;
            return false;
        }

        try
        {
            cmdInterp = Marshal.ReadIntPtr(smartBox + SmartBoxLocator.SmartBoxCmdInterpOffset);
            if (cmdInterp == IntPtr.Zero)
            {
                _statusMessage = "SmartBox active but CommandInterpreter not ready.";
                return false;
            }

            vtable = Marshal.ReadIntPtr(cmdInterp);
            if (!SmartBoxLocator.IsPointerInModule(vtable))
            {
                _statusMessage = $"CommandInterpreter vtable looks invalid (0x{vtable.ToInt32():X8}).";
                return false;
            }
        }
        catch (Exception ex)
        {
            _statusMessage = ex.Message;
            return false;
        }

        if (cmdInterp == _boundCmdInterp &&
            _commenceJump != null &&
            _doJump != null &&
            _setAutoRun != null &&
            _setMotion != null &&
            _stopCompletely != null &&
            _turnToHeading != null &&
            _playerTeleported != null &&
            _takeControlFromServer != null &&
            _clearAllCommands != null)
        {
            _smartboxStaticAddr = selectedStaticAddr;
            IsInitialized = true;
            _statusMessage = "Ready.";
            return true;
        }

        IntPtr commenceJumpPtr = Marshal.ReadIntPtr(vtable, VtblCommenceJumpIndex * IntPtr.Size);
        IntPtr doJumpPtr = Marshal.ReadIntPtr(vtable, VtblDoJumpIndex * IntPtr.Size);
        IntPtr setAutoRunPtr = Marshal.ReadIntPtr(vtable, VtblSetAutoRunIndex * IntPtr.Size);
        EnsureAbsoluteResolved();
        IntPtr setMotionPtr = _setMotionAbs != IntPtr.Zero ? _setMotionAbs : ResolveSetMotionPointer(commenceJumpPtr, doJumpPtr);
        IntPtr stopCompletelyPtr = _stopCompletelyAbs != IntPtr.Zero ? _stopCompletelyAbs : AddOffset(setAutoRunPtr, StopCompletelyOffsetFromSetAutoRun);
        IntPtr turnToHeadingPtr = _turnToHeadingAbs != IntPtr.Zero ? _turnToHeadingAbs : AddOffset(setAutoRunPtr, TurnToHeadingOffsetFromSetAutoRun);
        IntPtr playerTeleportedPtr = _playerTeleportedAbs != IntPtr.Zero ? _playerTeleportedAbs : AddOffset(setAutoRunPtr, PlayerTeleportedOffsetFromSetAutoRun);
        IntPtr takeControlPtr = _takeControlAbs != IntPtr.Zero ? _takeControlAbs : AddOffset(setAutoRunPtr, TakeControlOffsetFromSetAutoRun);
        IntPtr clearAllCommandsPtr = _clearAllCommandsAbs != IntPtr.Zero ? _clearAllCommandsAbs : AddOffset(setAutoRunPtr, ClearAllCommandsOffsetFromSetAutoRun);

        if (!SmartBoxLocator.IsPointerInModule(commenceJumpPtr) ||
            !SmartBoxLocator.IsPointerInModule(doJumpPtr) ||
            !SmartBoxLocator.IsPointerInModule(setAutoRunPtr) ||
            !SmartBoxLocator.IsPointerInModule(setMotionPtr) ||
            !SmartBoxLocator.IsPointerInModule(stopCompletelyPtr) ||
            !SmartBoxLocator.IsPointerInModule(turnToHeadingPtr) ||
            !SmartBoxLocator.IsPointerInModule(playerTeleportedPtr) ||
            !SmartBoxLocator.IsPointerInModule(takeControlPtr) ||
            !SmartBoxLocator.IsPointerInModule(clearAllCommandsPtr))
        {
            _statusMessage = "CommandInterpreter pointers look invalid.";
            return false;
        }

        _commenceJump = Marshal.GetDelegateForFunctionPointer<CommenceJumpDelegate>(commenceJumpPtr);
        _doJump = Marshal.GetDelegateForFunctionPointer<DoJumpDelegate>(doJumpPtr);
        _setAutoRun = Marshal.GetDelegateForFunctionPointer<SetAutoRunDelegate>(setAutoRunPtr);
        _setMotion = Marshal.GetDelegateForFunctionPointer<SetMotionDelegate>(setMotionPtr);
        _stopCompletely = Marshal.GetDelegateForFunctionPointer<StopCompletelyDelegate>(stopCompletelyPtr);
        _turnToHeading = Marshal.GetDelegateForFunctionPointer<TurnToHeadingDelegate>(turnToHeadingPtr);
        _playerTeleported = Marshal.GetDelegateForFunctionPointer<PlayerTeleportedDelegate>(playerTeleportedPtr);
        _takeControlFromServer = Marshal.GetDelegateForFunctionPointer<TakeControlFromServerDelegate>(takeControlPtr);
        _clearAllCommands = Marshal.GetDelegateForFunctionPointer<ClearAllCommandsDelegate>(clearAllCommandsPtr);

        // Optional: SendMovementEvent from the vtable, only at the reference distance from
        // SetAutoRun. A miss leaves it null (SetAutoRun then sends nothing, as before) and never
        // fails the binding of the others.
        IntPtr sendMovementPtr = Marshal.ReadIntPtr(vtable, VtblSendMovementEventIndex * IntPtr.Size);
        if (sendMovementPtr == AddOffset(setAutoRunPtr, SendMovementEventOffsetFromSetAutoRun)
            && SmartBoxLocator.IsPointerInModule(sendMovementPtr))
        {
            _sendMovementEvent = Marshal.GetDelegateForFunctionPointer<SendMovementEventDelegate>(sendMovementPtr);
        }
        else
        {
            _sendMovementEvent = null;
            RynthLog.Compat($"Compat: CommandInterpreter::SendMovementEvent not bound (vtable slot {VtblSendMovementEventIndex} = 0x{sendMovementPtr.ToInt32():X8}, expected SetAutoRun{SendMovementEventOffsetFromSetAutoRun:+0;-0}) - autorun changes are not sent to the server.");
        }

        _smartboxStaticAddr = selectedStaticAddr;
        _boundCmdInterp = cmdInterp;
        IsInitialized = true;
        _statusMessage = "Ready.";
        RynthLog.Verbose("Compat: command interpreter bound");
        return true;
    }

    private static IntPtr ResolveSetMotionPointer(IntPtr commenceJumpPtr, IntPtr doJumpPtr)
    {
        IntPtr fromCommenceJump = AddOffset(commenceJumpPtr, SetMotionOffsetFromCommenceJump);
        IntPtr fromDoJump = AddOffset(doJumpPtr, SetMotionOffsetFromDoJump);
        return fromCommenceJump == fromDoJump ? fromCommenceJump : IntPtr.Zero;
    }

    private static IntPtr AddOffset(IntPtr address, int offset)
    {
        return new IntPtr(address.ToInt64() + offset);
    }

    private static void Reset()
    {
        _smartboxStaticAddr = IntPtr.Zero;
        _boundCmdInterp = IntPtr.Zero;
        _absResolved = false;
        _setMotionAbs = _stopCompletelyAbs = _turnToHeadingAbs = _playerTeleportedAbs = _takeControlAbs = _clearAllCommandsAbs = IntPtr.Zero;
        _commenceJump = null;
        _doJump = null;
        _setAutoRun = null;
        _setMotion = null;
        _stopCompletely = null;
        _turnToHeading = null;
        _playerTeleported = null;
        _takeControlFromServer = null;
        _clearAllCommands = null;
        _sendMovementEvent = null;
        IsInitialized = false;
        _statusMessage = "Not probed yet.";
    }
}
