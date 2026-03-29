using System;
using System.Runtime.InteropServices;
using System.Threading;
using NexCore.Engine.Hooking;
using NexCore.Engine.Plugins;

namespace NexCore.Engine.Compatibility;

internal static class CreateObjectHooks
{
    private const int CreateObjectVa = 0x005594B0;
    private static readonly byte[] CreateObjectSignature =
    [
        0x55, 0x8B, 0x6C, 0x24, 0x08, 0x56, 0x8B, 0xF1,
        0x8B, 0x8E, 0x8C, 0x00, 0x00, 0x00, 0x8B, 0x96,
        0x88, 0x00, 0x00, 0x00, 0x8B, 0xC5, 0xD3, 0xE8,
        0x8B, 0x8E, 0x90, 0x00, 0x00, 0x00, 0x33, 0xC5
    ];

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate IntPtr CreateObjectDelegate(IntPtr thisPtr, uint objectId, IntPtr visualDesc, IntPtr physicsDesc, IntPtr weenieDesc);

    private static CreateObjectDelegate? _originalCreateObject;
    private static CreateObjectDelegate? _createObjectDetour;
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

        int funcOff = CreateObjectVa - textSection.TextBaseVa;
        if (!PatternScanner.VerifyBytes(textSection.Bytes, funcOff, CreateObjectSignature))
        {
            _statusMessage = $"ACCObjectMaint::CreateObject signature mismatch @ 0x{CreateObjectVa:X8}.";
            log?.Invoke($"Compat: create-object hook failed - {_statusMessage}");
            return;
        }

        try
        {
            _targetAddress = new IntPtr(textSection.TextBaseVa + funcOff);
            _createObjectDetour = CreateObjectDetour;
            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_createObjectDetour);
            IntPtr originalPtr = MinHook.Hook(_targetAddress, detourPtr);
            _originalCreateObject = Marshal.GetDelegateForFunctionPointer<CreateObjectDelegate>(originalPtr);

            IsInstalled = true;
            _statusMessage = $"Hooked ACCObjectMaint::CreateObject @ 0x{_targetAddress.ToInt32():X8}.";
            log?.Invoke($"Compat: create-object hook ready - CreateObject=0x{_targetAddress.ToInt32():X8}");
        }
        catch (Exception ex)
        {
            _statusMessage = ex.Message;
            log?.Invoke($"Compat: create-object hook failed - {ex.Message}");
        }
    }

    private static IntPtr CreateObjectDetour(IntPtr thisPtr, uint objectId, IntPtr visualDesc, IntPtr physicsDesc, IntPtr weenieDesc)
    {
        IntPtr result = _originalCreateObject!(thisPtr, objectId, visualDesc, physicsDesc, weenieDesc);
        if (result == IntPtr.Zero || objectId == 0)
            return result;

        int count = Interlocked.Increment(ref _dispatchCount);
        if (count <= 5)
            EntryPoint.Log($"Compat: create object #{count} id=0x{objectId:X8} ptr=0x{result.ToInt32():X8}");

        PluginManager.QueueCreateObject(objectId);
        return result;
    }
}
