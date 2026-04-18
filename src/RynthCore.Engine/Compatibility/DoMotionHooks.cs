// ============================================================================
//  RynthCore.Engine - Compatibility/DoMotionHooks.cs
//
//  Hooks CPhysicsObj::DoMotion to track per-object On/Off motion state.
//  Used by wobjectgetisdooropen[] to detect runtime door open/close state,
//  since STypeBool.OPEN in CBaseQualities reflects the default/creation state
//  and is NOT updated when a door is animated open or closed at runtime.
//
//  VA derivation (map_offset + 0x00401000 = live VA):
//    0010FAF0 CPhysicsObj::DoMotion → 0x00510AF0
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;

namespace RynthCore.Engine.Compatibility;

internal static class DoMotionHooks
{
    // CPhysicsObj::DoMotion(uint motion, MovementParameters* params, int sendEvent) — thiscall
    // Map: 0010FAF0 → live VA: 0x00510AF0
    private const int DoMotionVa = 0x00510AF0;

    // Door motion commands (AC1Legacy Command enum)
    private const uint MotionOn  = 0x4000000B; // door opens
    private const uint MotionOff = 0x4000000C; // door closes

    // CPhysicsObj::weenie_obj pointer at +0x128
    // Calculated from CPhysicsObj struct layout (confirmed against PhysicsPositionOffset = 0x48).
    private const int WeenieObjOffset = 0x128;

    // Object ID inside ACCWeenieObject (HashBaseData<UInt32>): vfptr(4) + hash_next*(4) + id(4) = offset 8
    private const int WeenieIdOffset = 8;

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate uint DoMotionDelegate(IntPtr thisPtr, uint motion, IntPtr paramsPtr, int sendEvent);

    private static DoMotionDelegate? _originalDoMotion;
    private static DoMotionDelegate? _doMotionDetour; // held alive to prevent GC
    private static bool _hookInstalled;
    private static string _statusMessage = "Not initialized.";

    // Per-object motion state: true = On (open/activated), false = Off (closed/deactivated).
    // Only populated for objects that have had an On or Off motion applied since injection.
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

        int funcOff = DoMotionVa - textSection.TextBaseVa;
        if (funcOff < 0 || funcOff >= textSection.Bytes.Length)
        {
            _statusMessage = $"CPhysicsObj::DoMotion VA out of range @ 0x{DoMotionVa:X8}.";
            RynthLog.Compat($"Compat: do-motion hook failed - {_statusMessage}");
            return;
        }

        byte firstByte = textSection.Bytes[funcOff];
        if (firstByte is 0x00 or 0xCC or 0xC3)
        {
            _statusMessage = $"CPhysicsObj::DoMotion looks invalid @ 0x{DoMotionVa:X8} (opcode 0x{firstByte:X2}).";
            RynthLog.Compat($"Compat: do-motion hook failed - {_statusMessage}");
            return;
        }

        try
        {
            IntPtr targetAddress = new IntPtr(textSection.TextBaseVa + funcOff);
            _doMotionDetour = DoMotionDetour;
            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_doMotionDetour);
            _originalDoMotion = Marshal.GetDelegateForFunctionPointer<DoMotionDelegate>(
                MinHook.HookCreate(targetAddress, detourPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(targetAddress);

            _hookInstalled = true;
            _statusMessage = $"Hooked CPhysicsObj::DoMotion @ 0x{targetAddress.ToInt32():X8}.";
            RynthLog.Verbose($"Compat: do-motion hook ready @ 0x{targetAddress.ToInt32():X8}, firstByte=0x{firstByte:X2}");
        }
        catch (Exception ex)
        {
            _statusMessage = ex.Message;
            RynthLog.Compat($"Compat: do-motion hook failed - {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the last observed On/Off motion state for the given object ID.
    /// Returns false (not found) if this object has never had an On or Off motion since injection.
    /// </summary>
    public static bool TryGetObjectMotionOn(uint objectId, out bool isOn)
    {
        return _objectOnState.TryGetValue(objectId, out isOn);
    }

    private static uint DoMotionDetour(IntPtr thisPtr, uint motion, IntPtr paramsPtr, int sendEvent)
    {
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

        return _originalDoMotion!(thisPtr, motion, paramsPtr, sendEvent);
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
