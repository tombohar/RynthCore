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

    private static IntPtr _smartboxStaticAddr;
    private static IntPtr _boundCmdInterp;
    private static CommenceJumpDelegate? _commenceJump;
    private static DoJumpDelegate? _doJump;
    private static SetAutoRunDelegate? _setAutoRun;
    private static SetMotionDelegate? _setMotion;
    private static StopCompletelyDelegate? _stopCompletely;
    private static TurnToHeadingDelegate? _turnToHeading;
    private static PlayerTeleportedDelegate? _playerTeleported;
    private static TakeControlFromServerDelegate? _takeControlFromServer;
    private static ClearAllCommandsDelegate? _clearAllCommands;
    private static string _statusMessage = "Not probed yet.";

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

    public static bool SetAutoRun(bool enabled)
    {
        if (!TryBindDelegates())
            return false;

        try
        {
            _setAutoRun!(_boundCmdInterp, enabled ? 1 : 0, 1);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool SetMotion(uint motion, bool enabled)
    {
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
        if (!TryBindDelegates() || _turnToHeading == null)
            return false;

        try
        {
            _turnToHeading(_boundCmdInterp, headingDegrees);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool PlayerTeleported()
    {
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

    public static bool TapJump()
    {
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
        IntPtr setMotionPtr = ResolveSetMotionPointer(commenceJumpPtr, doJumpPtr);
        IntPtr stopCompletelyPtr = AddOffset(setAutoRunPtr, StopCompletelyOffsetFromSetAutoRun);
        IntPtr turnToHeadingPtr = AddOffset(setAutoRunPtr, TurnToHeadingOffsetFromSetAutoRun);
        IntPtr playerTeleportedPtr = AddOffset(setAutoRunPtr, PlayerTeleportedOffsetFromSetAutoRun);
        IntPtr takeControlPtr = AddOffset(setAutoRunPtr, TakeControlOffsetFromSetAutoRun);
        IntPtr clearAllCommandsPtr = AddOffset(setAutoRunPtr, ClearAllCommandsOffsetFromSetAutoRun);

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
        _commenceJump = null;
        _doJump = null;
        _setAutoRun = null;
        _setMotion = null;
        _stopCompletely = null;
        _turnToHeading = null;
        _playerTeleported = null;
        _takeControlFromServer = null;
        _clearAllCommands = null;
        IsInitialized = false;
        _statusMessage = "Not probed yet.";
    }
}
