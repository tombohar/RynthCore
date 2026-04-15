// ============================================================================
//  RynthCore.Engine - Compatibility/SmartBoxSetStateHooks.cs
//
//  Hooks SmartBox::HandleSetState to track per-object physics state changes.
//  When the server sends a SetState blob for a world object, this function is
//  called with the object ID and new PhysicsState bitfield — giving us both
//  without any reverse-pointer lookup.
//
//  VA derivation (from Chorizite SmartBox.cs .text address):
//    .text:004533E0 SmartBox::HandleSetState → 0x004533E0
//    enum_Entrypoint: 0x00453420 (jmp stub — do NOT hook this one)
//
//  Signature (Chorizite):
//    NetBlobProcessedStatus __thiscall SmartBox::HandleSetState(
//        SmartBox *this, NetBlob *blob, unsigned int object_id,
//        unsigned int new_state, PhysicsTimestampPack *timestamps)
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;

namespace RynthCore.Engine.Compatibility;

internal static class SmartBoxSetStateHooks
{
    // .text address of SmartBox::HandleSetState
    private const int HandleSetStateVa = 0x004533E0;

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate uint HandleSetStateDelegate(
        IntPtr thisPtr, IntPtr blobPtr, uint objectId, uint newState, IntPtr timestampsPtr);

    private static HandleSetStateDelegate? _originalHandleSetState;
    private static HandleSetStateDelegate? _handleSetStateDetour;
    private static bool _hookInstalled;
    private static string _statusMessage = "Not initialized.";

    // Per-object last-known physics state.
    private static readonly ConcurrentDictionary<uint, uint> _objectState = new();

    // Debug: log first N calls
    private static int _debugLogCount;

    public static bool IsInstalled => _hookInstalled;
    public static string StatusMessage => _statusMessage;

    public static void Initialize()
    {
        // VA 0x004533E0 was found to land mid-function in this binary (firstByte=0xCF).
        // Physics state is now read directly from CPhysicsObj+0xA8 via TryGetObjectPhysicsState.
        // Hook not installed; TryGetObjectState remains available for future use.
        _statusMessage = "Disabled — using direct CPhysicsObj state read instead.";
        RynthLog.Compat("Compat: smartbox-setstate hook skipped — direct read via CPhysicsObj+0xA8 in use.");
    }

    /// <summary>
    /// Returns the last physics state received from the server for this object.
    /// Returns false if no SetState has been seen for this object since injection.
    /// </summary>
    public static bool TryGetObjectState(uint objectId, out uint state)
    {
        return _objectState.TryGetValue(objectId, out state);
    }

    private static uint HandleSetStateDetour(
        IntPtr thisPtr, IntPtr blobPtr, uint objectId, uint newState, IntPtr timestampsPtr)
    {
        try
        {
            _objectState[objectId] = newState;

            int n = Interlocked.Increment(ref _debugLogCount);
            if (n <= 60)
                RynthLog.Compat($"SetState#{n} obj=0x{objectId:X8} state=0x{newState:X8}");
        }
        catch { }

        return _originalHandleSetState!(thisPtr, blobPtr, objectId, newState, timestampsPtr);
    }
}
