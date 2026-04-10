using System;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.Compatibility;

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

    public static void Initialize()
    {
        if (IsInstalled)
            return;

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection textSection))
        {
            _statusMessage = "acclient.exe not available.";
            return;
        }

        int funcOff = CreateObjectVa - textSection.TextBaseVa;
        if (!PatternScanner.VerifyBytes(textSection.Bytes, funcOff, CreateObjectSignature))
        {
            _statusMessage = $"ACCObjectMaint::CreateObject signature mismatch @ 0x{CreateObjectVa:X8}.";
            RynthLog.Compat($"Compat: create-object hook failed - {_statusMessage}");
            return;
        }

        try
        {
            _targetAddress = new IntPtr(textSection.TextBaseVa + funcOff);
            _createObjectDetour = CreateObjectDetour;
            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_createObjectDetour);
            _originalCreateObject = Marshal.GetDelegateForFunctionPointer<CreateObjectDelegate>(MinHook.HookCreate(_targetAddress, detourPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(_targetAddress);

            IsInstalled = true;
            _statusMessage = $"Hooked ACCObjectMaint::CreateObject @ 0x{_targetAddress.ToInt32():X8}.";
            RynthLog.Compat($"Compat: create-object hook ready - CreateObject=0x{_targetAddress.ToInt32():X8}");
        }
        catch (Exception ex)
        {
            _statusMessage = ex.Message;
            RynthLog.Compat($"Compat: create-object hook failed - {ex.Message}");
        }
    }

    private static IntPtr CreateObjectDetour(IntPtr thisPtr, uint objectId, IntPtr visualDesc, IntPtr physicsDesc, IntPtr weenieDesc)
    {
        IntPtr result = _originalCreateObject!(thisPtr, objectId, visualDesc, physicsDesc, weenieDesc);
        if (result == IntPtr.Zero || objectId == 0)
            return result;

        int count = Interlocked.Increment(ref _dispatchCount);
        if (count <= 0)
            RynthLog.Compat($"Compat: create object #{count} id=0x{objectId:X8} ptr=0x{result.ToInt32():X8}");

        PluginManager.QueueCreateObject(objectId);
        return result;
    }
}
