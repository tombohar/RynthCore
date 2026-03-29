using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using NexCore.Engine.Hooking;
using NexCore.Engine.Plugins;

namespace NexCore.Engine.Compatibility;

internal static class SmartBoxHooks
{
    private const int DispatchSmartBoxEventVa = 0x0055A210;
    private const int NetBlobBufPtrOffset = 0x2C;
    private const int NetBlobBufSizeOffset = 0x30;
    private const uint PositionUpdateOpcode = 0x0000F74C;
    private const uint PlayerPositionUpdateOpcode = 0x0000F74B;
    private const uint VectorUpdateOpcode = 0x0000F74E;
    private const uint UpdateObjectOpcode = 0x0000F7DB;
    private static readonly byte[] DispatchSmartBoxEventSignature =
    [
        0x83, 0xEC, 0x08, 0x53, 0x8B, 0x5C, 0x24, 0x10,
        0x8B, 0x53, 0x30, 0x83, 0xFA, 0x04, 0x8B, 0x43,
        0x2C, 0x56, 0x8B, 0xF1, 0x89, 0x44, 0x24, 0x14,
        0x89, 0x54, 0x24, 0x08, 0x72, 0x68, 0x8B, 0x08
    ];

    private static IntPtr _originalDispatchSmartBoxEventPtr;
    private static IntPtr _targetAddress;
    private static string _statusMessage = "Not probed yet.";
    private static int _dispatchCount;

    public static bool IsInstalled { get; private set; }
    public static string StatusMessage => _statusMessage;

    public static void Initialize(Action<string>? log = null)
    {
        if (IsInstalled)
            return;

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection textSection, log))
        {
            _statusMessage = "acclient.exe not available.";
            return;
        }

        int funcOff = DispatchSmartBoxEventVa - textSection.TextBaseVa;
        if (!PatternScanner.VerifyBytes(textSection.Bytes, funcOff, DispatchSmartBoxEventSignature))
        {
            _statusMessage = $"ACSmartBox::DispatchSmartBoxEvent signature mismatch @ 0x{DispatchSmartBoxEventVa:X8}.";
            log?.Invoke($"Compat: smartbox hook failed - {_statusMessage}");
            return;
        }

        try
        {
            unsafe
            {
                _targetAddress = new IntPtr(textSection.TextBaseVa + funcOff);
                delegate* unmanaged[Thiscall]<IntPtr, IntPtr, uint> pDetour = &DispatchSmartBoxEventDetour;
                _originalDispatchSmartBoxEventPtr = MinHook.Hook(_targetAddress, (IntPtr)pDetour);
            }

            IsInstalled = true;
            _statusMessage = $"Hooked ACSmartBox::DispatchSmartBoxEvent @ 0x{_targetAddress.ToInt32():X8}.";
            log?.Invoke($"Compat: smartbox hook ready - DispatchSmartBoxEvent=0x{_targetAddress.ToInt32():X8}");
        }
        catch (Exception ex)
        {
            _statusMessage = ex.Message;
            log?.Invoke($"Compat: smartbox hook failed - {ex.Message}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvThiscall) })]
    private static unsafe uint DispatchSmartBoxEventDetour(IntPtr thisPtr, IntPtr blob)
    {
        var pOriginal = (delegate* unmanaged[Thiscall]<IntPtr, IntPtr, uint>)_originalDispatchSmartBoxEventPtr;
        if (!LoginLifecycleHooks.HasObservedLoginComplete)
            return pOriginal(thisPtr, blob);

        SmartBoxEventInfo info = ReadSmartBoxEventInfo(blob);
        uint status = pOriginal(thisPtr, blob);

        int count = Interlocked.Increment(ref _dispatchCount);
        if (count <= 24)
            EntryPoint.Log(
                $"Compat: smartbox #{count} opcode=0x{info.Opcode:X8} rawId=0x{info.RawObjectId:X8} size={info.BlobSize} status={status}");

        if (info.RawObjectId != 0 &&
            (info.Opcode == PositionUpdateOpcode ||
             info.Opcode == PlayerPositionUpdateOpcode ||
             info.Opcode == VectorUpdateOpcode ||
             info.Opcode == UpdateObjectOpcode))
        {
            PluginManager.QueueUpdateObject(info.RawObjectId);
        }

        return status;
    }

    private static SmartBoxEventInfo ReadSmartBoxEventInfo(IntPtr blob)
    {
        if (blob == IntPtr.Zero)
            return default;

        try
        {
            uint blobSize = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(blob, NetBlobBufSizeOffset)));
            if (blobSize < sizeof(uint))
                return new SmartBoxEventInfo(0, 0, blobSize);

            IntPtr payloadPtr = Marshal.ReadIntPtr(IntPtr.Add(blob, NetBlobBufPtrOffset));
            if (payloadPtr == IntPtr.Zero)
                return new SmartBoxEventInfo(0, 0, blobSize);

            uint opcode = unchecked((uint)Marshal.ReadInt32(payloadPtr));
            uint rawObjectId = blobSize >= 8
                ? unchecked((uint)Marshal.ReadInt32(IntPtr.Add(payloadPtr, sizeof(uint))))
                : 0;
            return new SmartBoxEventInfo(opcode, rawObjectId, blobSize);
        }
        catch
        {
            return default;
        }
    }

    private readonly record struct SmartBoxEventInfo(uint Opcode, uint RawObjectId, uint BlobSize);
}
