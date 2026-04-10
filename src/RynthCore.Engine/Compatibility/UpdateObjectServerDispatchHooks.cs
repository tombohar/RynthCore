using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.Compatibility;

internal static class UpdateObjectServerDispatchHooks
{
    private const int DispatchSbUpdateObjectVa = 0x006AD710;
    private const int NetBlobBufPtrOffset = 0x2C;
    private const int NetBlobBufSizeOffset = 0x30;
    private const uint UpdateObjectOpcode = 0x0000F7DB;
    private static readonly byte[] DispatchSbUpdateObjectSignature =
    [
        0x81, 0xEC, 0xB8, 0x01, 0x00, 0x00, 0x53, 0x8B,
        0x9C, 0x24, 0xC4, 0x01, 0x00, 0x00, 0x85, 0xDB,
        0x74, 0x0B, 0x8B, 0x84, 0x24, 0xC0, 0x01, 0x00,
        0x00, 0x85, 0xC0, 0x75, 0x0D, 0xB8, 0x03, 0x00,
        0x00, 0x00
    ];

    private static IntPtr _originalDispatchSbUpdateObjectPtr;
    private static IntPtr _targetAddress;
    private static string _statusMessage = "Not probed yet.";
    private static int _dispatchCount;

    public static bool IsInstalled { get; private set; }
    public static string StatusMessage => _statusMessage;

    public static void Initialize()
    {
        if (IsInstalled)
            return;

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection textSection))
        {
            _statusMessage = "acclient.exe not available.";
            return;
        }

        int funcOff = DispatchSbUpdateObjectVa - textSection.TextBaseVa;
        if (!PatternScanner.VerifyBytes(textSection.Bytes, funcOff, DispatchSbUpdateObjectSignature))
        {
            _statusMessage = $"CM_Physics::DispatchSB_UpdateObject signature mismatch @ 0x{DispatchSbUpdateObjectVa:X8}.";
            RynthLog.Compat($"Compat: update-object hook failed - {_statusMessage}");
            return;
        }

        try
        {
            unsafe
            {
                _targetAddress = new IntPtr(textSection.TextBaseVa + funcOff);
                delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint> pDetour = &DispatchSbUpdateObjectDetour;
                MinHook.Hook(_targetAddress, (IntPtr)pDetour, out _originalDispatchSbUpdateObjectPtr);
            }

            IsInstalled = true;
            _statusMessage = $"Hooked CM_Physics::DispatchSB_UpdateObject @ 0x{_targetAddress.ToInt32():X8}.";
            RynthLog.Compat($"Compat: update-object hook ready - DispatchSB_UpdateObject=0x{_targetAddress.ToInt32():X8}");
        }
        catch (Exception ex)
        {
            _statusMessage = ex.Message;
            RynthLog.Compat($"Compat: update-object hook failed - {ex.Message}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe uint DispatchSbUpdateObjectDetour(IntPtr smartBoxPtr, IntPtr blob)
    {
        var pOriginal = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint>)_originalDispatchSbUpdateObjectPtr;
        uint objectId = TryReadUpdatedObjectId(blob);

        uint status = pOriginal(smartBoxPtr, blob);
        if (!LoginLifecycleHooks.HasObservedLoginComplete || objectId == 0)
            return status;

        int count = Interlocked.Increment(ref _dispatchCount);
        if (count <= 8)
            RynthLog.Compat($"Compat: update object #{count} id=0x{objectId:X8} status={status}");

        PluginManager.QueueUpdateObject(objectId);
        return status;
    }

    private static uint TryReadUpdatedObjectId(IntPtr blob)
    {
        if (blob == IntPtr.Zero)
            return 0;

        try
        {
            uint blobSize = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(blob, NetBlobBufSizeOffset)));
            if (blobSize < 8)
                return 0;

            IntPtr payloadPtr = Marshal.ReadIntPtr(IntPtr.Add(blob, NetBlobBufPtrOffset));
            if (payloadPtr == IntPtr.Zero)
                return 0;

            uint opcode = unchecked((uint)Marshal.ReadInt32(payloadPtr));
            if (opcode != UpdateObjectOpcode)
                return 0;

            return unchecked((uint)Marshal.ReadInt32(IntPtr.Add(payloadPtr, sizeof(uint))));
        }
        catch
        {
            return 0;
        }
    }
}
