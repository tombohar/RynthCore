// ============================================================================
//  RynthCore.Engine - Compatibility/DoMotionHooks.cs
//
//  Hooks CPhysicsObj::DoMotion to track per-object On/Off motion state.
//  Used by wobjectgetisdooropen[] to detect runtime door open/close state,
//  since STypeBool.OPEN in CBaseQualities reflects the default/creation state
//  and is NOT updated when a door is animated open or closed at runtime.
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;

namespace RynthCore.Engine.Compatibility;

internal static class DoMotionHooks
{
    private const int DoMotionFallbackVa = 0x00510AF0;

    // CPhysicsObj::DoMotion(uint motion, MovementParameters* params, int sendEvent)
    // Distinctive 0x83 0xEC 0x64 (sub esp, 0x64) and the +0xC4 / +0xCC field
    // accesses; the 'mov dword ptr [esi+0xCC],1' is unique to this function's prologue.
    private static readonly byte?[] DoMotionPattern =
    [
        0x83, 0xEC, 0x64, 0x56, 0x8B, 0xF1, 0x8B, 0x8E,
        0xC4, 0x00, 0x00, 0x00, 0x33, 0xC0, 0x3B, 0xC8,
        0xC7, 0x86, 0xCC, 0x00, 0x00, 0x00, 0x01, 0x00,
        0x00, 0x00, 0x75, 0x0C
    ];

    // Door motion commands (AC1Legacy Command enum)
    private const uint MotionOn  = 0x4000000B;
    private const uint MotionOff = 0x4000000C;

    private const int WeenieObjOffset = 0x128;
    private const int WeenieIdOffset = 8;

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate uint DoMotionDelegate(IntPtr thisPtr, uint motion, IntPtr paramsPtr, int sendEvent);

    private static DoMotionDelegate? _originalDoMotion;
    private static DoMotionDelegate? _doMotionDetour;
    private static bool _hookInstalled;
    private static string _statusMessage = "Not initialized.";

    private static readonly ConcurrentDictionary<uint, bool> _objectOnState = new();

    public static bool IsInstalled => _hookInstalled;
    public static string StatusMessage => _statusMessage;

    public static void Initialize()
    {
        if (_hookInstalled)
            return;

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection textSection))
        {
            _statusMessage = "acclient.exe not available.";
            return;
        }

        var resolved = HookResolver.Resolve(textSection, "DoMotionHooks.DoMotion",
            DoMotionPattern, DoMotionFallbackVa);
        if (!resolved.Success)
        {
            _statusMessage = $"Resolve failed ({resolved.Detail}).";
            return;
        }

        try
        {
            _doMotionDetour = DoMotionDetour;
            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_doMotionDetour);
            _originalDoMotion = Marshal.GetDelegateForFunctionPointer<DoMotionDelegate>(
                MinHook.HookCreate(resolved.Address, detourPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(resolved.Address);

            _hookInstalled = true;
            _statusMessage = $"Hooked CPhysicsObj::DoMotion @ 0x{resolved.Address.ToInt32():X8}.";
            RynthLog.Compat($"DoMotionHooks: install ok.");
        }
        catch (Exception ex)
        {
            _statusMessage = ex.Message;
            RynthLog.Compat($"DoMotionHooks: install threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static bool TryGetObjectMotionOn(uint objectId, out bool isOn)
    {
        return _objectOnState.TryGetValue(objectId, out isOn);
    }

    private static int _fires;

    private static uint DoMotionDetour(IntPtr thisPtr, uint motion, IntPtr paramsPtr, int sendEvent)
    {
        RecursionGuard.Tick("DoMotionHooks.DoMotion");
        if (Interlocked.Increment(ref _fires) <= 3)
            RynthLog.Compat($"DoMotionHooks: fired #{_fires} this=0x{thisPtr.ToInt32():X8} motion=0x{motion:X8}");

        try
        {
            if (thisPtr != IntPtr.Zero && (motion == MotionOn || motion == MotionOff))
            {
                uint objectId = ReadWeenieId(thisPtr);
                if (objectId != 0)
                    _objectOnState[objectId] = motion == MotionOn;
            }
        }
        catch { }

        try { return _originalDoMotion!(thisPtr, motion, paramsPtr, sendEvent); }
        catch (Exception ex) { try { RynthLog.Compat($"DoMotionHooks: original threw {ex.GetType().Name}: {ex.Message}"); } catch { } throw; }
    }

    private static uint ReadWeenieId(IntPtr physObjPtr)
    {
        try
        {
            IntPtr weenieFieldAddr = physObjPtr + WeenieObjOffset;
            if (!ClientObjectHooks.IsReadablePointer(weenieFieldAddr))
                return 0;

            IntPtr weeniePtr = Marshal.ReadIntPtr(weenieFieldAddr);
            if (weeniePtr == IntPtr.Zero)
                return 0;

            IntPtr idAddr = weeniePtr + WeenieIdOffset;
            if (!ClientObjectHooks.IsReadablePointer(idAddr))
                return 0;

            return (uint)Marshal.ReadInt32(idAddr);
        }
        catch { return 0; }
    }
}
