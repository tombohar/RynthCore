using System;
using System.Runtime.InteropServices;
using System.Threading;
using NexCore.Engine.Hooking;
using NexCore.Engine.Plugins;

namespace NexCore.Engine.Compatibility;

internal static class DeleteObjectHooks
{
    private const int DeleteObjectVa = 0x00558330;
    private static readonly byte[] DeleteObjectSignature =
    [
        0x56, 0x57, 0x8B, 0xF9, 0xE8, 0x67, 0x2D, 0x00,
        0x00, 0x85, 0xC0, 0x8B, 0x74, 0x24, 0x0C, 0x74,
        0x0C, 0xE8, 0xFA, 0x23, 0x00, 0x00, 0x8B, 0x08,
        0x56, 0x50, 0xFF, 0x51, 0x24, 0x56, 0xE8
    ];

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int DeleteObjectDelegate(IntPtr thisPtr, uint objectId);

    private static DeleteObjectDelegate? _originalDeleteObject;
    private static DeleteObjectDelegate? _deleteObjectDetour;
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

        int funcOff = DeleteObjectVa - textSection.TextBaseVa;
        if (!PatternScanner.VerifyBytes(textSection.Bytes, funcOff, DeleteObjectSignature))
        {
            _statusMessage = $"ACCObjectMaint::DeleteObject signature mismatch @ 0x{DeleteObjectVa:X8}.";
            log?.Invoke($"Compat: delete-object hook failed - {_statusMessage}");
            return;
        }

        try
        {
            _targetAddress = new IntPtr(textSection.TextBaseVa + funcOff);
            _deleteObjectDetour = DeleteObjectDetour;
            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_deleteObjectDetour);
            IntPtr originalPtr = MinHook.Hook(_targetAddress, detourPtr);
            _originalDeleteObject = Marshal.GetDelegateForFunctionPointer<DeleteObjectDelegate>(originalPtr);

            IsInstalled = true;
            _statusMessage = $"Hooked ACCObjectMaint::DeleteObject @ 0x{_targetAddress.ToInt32():X8}.";
            log?.Invoke($"Compat: delete-object hook ready - DeleteObject=0x{_targetAddress.ToInt32():X8}");
        }
        catch (Exception ex)
        {
            _statusMessage = ex.Message;
            log?.Invoke($"Compat: delete-object hook failed - {ex.Message}");
        }
    }

    private static int DeleteObjectDetour(IntPtr thisPtr, uint objectId)
    {
        if (objectId != 0)
        {
            int count = Interlocked.Increment(ref _dispatchCount);
            if (count <= 5)
                EntryPoint.Log($"Compat: delete object #{count} id=0x{objectId:X8}");

            PluginManager.QueueDeleteObject(objectId);
        }

        return _originalDeleteObject!(thisPtr, objectId);
    }
}
