using System;
using System.Runtime.InteropServices;

namespace NexCore.Engine.Compatibility;

internal static class MovementActionHooks
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool DoMovementCommandDelegate(uint motion, float speed, int holdKey);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool StopMovementCommandDelegate(uint motion, int holdKey);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool JumpNonAutonomousDelegate(float extent);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool AutonomyLevelDelegate(uint level);

    public const uint MotionWalkForward = 0x45000005;
    public const uint MotionWalkBackward = 0x45000006;
    public const uint MotionRunForward = 0x41000003;
    public const uint MotionTurnRight = 0x6500000D;
    public const uint MotionTurnLeft = 0x6500000E;
    public const uint MotionSidestepRight = 0x6500000F;
    public const uint MotionSidestepLeft = 0x65000010;

    public const int HoldKeyNone = 0;
    public const int HoldKeyRun = 1;
    public const int HoldKeyAutorun = 2;

    private static readonly byte[] MovementPrologue = [0x83, 0xEC, 0x0C, 0x53, 0x56, 0x57, 0xE8];

    private static DoMovementCommandDelegate? _doMovement;
    private static StopMovementCommandDelegate? _stopMovement;
    private static JumpNonAutonomousDelegate? _jumpNonAutonomous;
    private static AutonomyLevelDelegate? _autonomyLevel;
    private static string _statusMessage = "Not probed yet.";

    public static bool IsInitialized { get; private set; }
    public static bool HasDoMovement => _doMovement != null;
    public static bool HasStopMovement => _stopMovement != null;
    public static bool HasJumpNonAutonomous => _jumpNonAutonomous != null;
    public static bool HasAutonomyLevel => _autonomyLevel != null;
    public static string StatusMessage => _statusMessage;

    public static bool Probe(Action<string>? log = null)
    {
        Reset();

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection textSection, log))
        {
            _statusMessage = "acclient.exe not available.";
            return false;
        }

        try
        {
            byte[] text = textSection.Bytes;

            byte[] stopOpcode = [0xC7, 0x02, 0x61, 0xF6, 0x00, 0x00];
            int stopOpcodeOff = PatternScanner.FindPattern(text, stopOpcode);
            if (stopOpcodeOff < 0)
            {
                _statusMessage = "StopMovement opcode 0xF661 not found.";
                log?.Invoke("Compat: movement probe failed - StopMovement opcode 0xF661 not found.");
                return false;
            }

            int stopFuncOff = PatternScanner.FindPrologueBefore(text, stopOpcodeOff, MovementPrologue);
            if (stopFuncOff < 0)
            {
                _statusMessage = "StopMovement prologue not found.";
                log?.Invoke("Compat: movement probe failed - StopMovement prologue not found.");
                return false;
            }

            int regionStart = Math.Max(0, stopOpcodeOff - 0x700);
            int regionEnd = Math.Min(text.Length, stopOpcodeOff + 0x300);

            byte[] doOpcode = [0xC7, 0x02, 0x1E, 0xF6, 0x00, 0x00];
            int doOpcodeOff = PatternScanner.FindPatternInRegion(text, doOpcode, regionStart, regionEnd);
            int doFuncOff = doOpcodeOff >= 0
                ? PatternScanner.FindPrologueBefore(text, doOpcodeOff, MovementPrologue)
                : -1;

            byte[] jumpOpcode = [0xC7, 0x02, 0xC9, 0xF7, 0x00, 0x00];
            int jumpOpcodeOff = PatternScanner.FindPatternInRegion(text, jumpOpcode, regionStart, regionEnd);
            int jumpFuncOff = jumpOpcodeOff >= 0
                ? PatternScanner.FindPrologueBefore(text, jumpOpcodeOff, MovementPrologue)
                : -1;

            byte[] autonomyOpcode = [0xC7, 0x02, 0x52, 0xF7, 0x00, 0x00];
            int autonomyOpcodeOff = PatternScanner.FindPatternInRegion(text, autonomyOpcode, regionStart, regionEnd);
            int autonomyFuncOff = autonomyOpcodeOff >= 0
                ? PatternScanner.FindPrologueBefore(text, autonomyOpcodeOff, MovementPrologue)
                : -1;

            if (doFuncOff < 0)
            {
                _statusMessage = "DoMovement opcode 0xF61E not found.";
                log?.Invoke("Compat: movement probe incomplete - DoMovement opcode 0xF61E not found.");
                return false;
            }

            int stopVa = textSection.TextBaseVa + stopFuncOff;
            int doVa = textSection.TextBaseVa + doFuncOff;
            _stopMovement = Marshal.GetDelegateForFunctionPointer<StopMovementCommandDelegate>(new IntPtr(stopVa));
            _doMovement = Marshal.GetDelegateForFunctionPointer<DoMovementCommandDelegate>(new IntPtr(doVa));

            if (jumpFuncOff >= 0)
            {
                int jumpVa = textSection.TextBaseVa + jumpFuncOff;
                _jumpNonAutonomous = Marshal.GetDelegateForFunctionPointer<JumpNonAutonomousDelegate>(new IntPtr(jumpVa));
                log?.Invoke($"Compat: movement jump hook ready - jump=0x{jumpVa:X8}");
            }

            if (autonomyFuncOff >= 0)
            {
                int autonomyVa = textSection.TextBaseVa + autonomyFuncOff;
                _autonomyLevel = Marshal.GetDelegateForFunctionPointer<AutonomyLevelDelegate>(new IntPtr(autonomyVa));
                log?.Invoke($"Compat: movement autonomy hook ready - autonomy=0x{autonomyVa:X8}");
            }

            IsInitialized = true;
            _statusMessage = jumpFuncOff >= 0 && autonomyFuncOff >= 0
                ? "Ready."
                : $"Partial. jump={jumpFuncOff >= 0}, autonomy={autonomyFuncOff >= 0}.";

            log?.Invoke($"Compat: movement hooks ready - stop=0x{stopVa:X8}, move=0x{doVa:X8}");
            return true;
        }
        catch (Exception ex)
        {
            Reset();
            _statusMessage = ex.Message;
            log?.Invoke($"Compat: movement probe failed - {ex.Message}");
            return false;
        }
    }

    public static bool DoMovement(uint motion, float speed = 1.0f, int holdKey = HoldKeyRun)
    {
        if (_doMovement == null)
            return false;

        try
        {
            return _doMovement(motion, speed, holdKey);
        }
        catch
        {
            return false;
        }
    }

    public static bool StopMovement(uint motion, int holdKey = HoldKeyRun)
    {
        if (_stopMovement == null)
            return false;

        try
        {
            return _stopMovement(motion, holdKey);
        }
        catch
        {
            return false;
        }
    }

    public static bool JumpNonAutonomous(float extent)
    {
        if (_jumpNonAutonomous == null)
            return false;

        try
        {
            return _jumpNonAutonomous(Math.Clamp(extent, 0f, 1f));
        }
        catch
        {
            return false;
        }
    }

    public static bool SetAutonomyLevel(uint level)
    {
        if (_autonomyLevel == null)
            return false;

        try
        {
            return _autonomyLevel(level);
        }
        catch
        {
            return false;
        }
    }

    private static void Reset()
    {
        _doMovement = null;
        _stopMovement = null;
        _jumpNonAutonomous = null;
        _autonomyLevel = null;
        IsInitialized = false;
    }
}
