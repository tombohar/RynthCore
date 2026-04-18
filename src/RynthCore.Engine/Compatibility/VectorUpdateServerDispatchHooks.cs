using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RynthCore.Engine.Hooking;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.Compatibility;

internal static class VectorUpdateServerDispatchHooks
{
    private const int DispatchSbVectorUpdateVa = 0x006ADC80;
    private const int NetBlobBufPtrOffset = 0x2C;
    private const int NetBlobBufSizeOffset = 0x30;
    private const uint VectorUpdateOpcode = 0x0000F74E;
    private static readonly byte[] DispatchSbVectorUpdateSignature =
    [
        0x83, 0xEC, 0x20, 0x53, 0x8B, 0x5C, 0x24, 0x2C,
        0x85, 0xDB, 0x74, 0x08, 0x8B, 0x44, 0x24, 0x28,
        0x85, 0xC0, 0x75, 0x0A, 0xB8, 0x03, 0x00, 0x00,
        0x00, 0x5B, 0x83, 0xC4, 0x20, 0xC3
    ];

    private static IntPtr _originalDispatchSbVectorUpdatePtr;
    private static IntPtr _targetAddress;
    private static string _statusMessage = "Not probed yet.";

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

        int funcOff = DispatchSbVectorUpdateVa - textSection.TextBaseVa;
        if (!PatternScanner.VerifyBytes(textSection.Bytes, funcOff, DispatchSbVectorUpdateSignature))
        {
            _statusMessage = $"CM_Physics::DispatchSB_VectorUpdate signature mismatch @ 0x{DispatchSbVectorUpdateVa:X8}.";
            RynthLog.Compat($"Compat: vector-update hook failed - {_statusMessage}");
            return;
        }

        try
        {
            unsafe
            {
                _targetAddress = new IntPtr(textSection.TextBaseVa + funcOff);
                delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint> pDetour = &DispatchSbVectorUpdateDetour;
                MinHook.Hook(_targetAddress, (IntPtr)pDetour, out _originalDispatchSbVectorUpdatePtr);
            }

            IsInstalled = true;
            _statusMessage = $"Hooked CM_Physics::DispatchSB_VectorUpdate @ 0x{_targetAddress.ToInt32():X8}.";
            RynthLog.Verbose($"Compat: vector-update hook ready - DispatchSB_VectorUpdate=0x{_targetAddress.ToInt32():X8}");
        }
        catch (Exception ex)
        {
            _statusMessage = ex.Message;
            RynthLog.Compat($"Compat: vector-update hook failed - {ex.Message}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe uint DispatchSbVectorUpdateDetour(IntPtr smartBoxPtr, IntPtr blob)
    {
        var pOriginal = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint>)_originalDispatchSbVectorUpdatePtr;
        VectorUpdateInfo info = ReadVectorUpdateInfo(blob);

        uint status = pOriginal(smartBoxPtr, blob);
        if (!LoginLifecycleHooks.HasObservedLoginComplete)
            return status;

        if (info.Opcode != VectorUpdateOpcode || info.RawObjectId == 0)
            return status;

        PluginManager.QueueUpdateObject(info.RawObjectId);
        return status;
    }

    private static VectorUpdateInfo ReadVectorUpdateInfo(IntPtr blob)
    {
        if (blob == IntPtr.Zero)
            return default;

        try
        {
            uint blobSize = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(blob, NetBlobBufSizeOffset)));
            if (blobSize < 8)
                return new VectorUpdateInfo(0, 0, blobSize);

            IntPtr payloadPtr = Marshal.ReadIntPtr(IntPtr.Add(blob, NetBlobBufPtrOffset));
            if (payloadPtr == IntPtr.Zero)
                return new VectorUpdateInfo(0, 0, blobSize);

            uint opcode = unchecked((uint)Marshal.ReadInt32(payloadPtr));
            uint rawObjectId = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(payloadPtr, sizeof(uint))));
            return new VectorUpdateInfo(opcode, rawObjectId, blobSize);
        }
        catch
        {
            return default;
        }
    }

    private readonly record struct VectorUpdateInfo(uint Opcode, uint RawObjectId, uint BlobSize);
}
