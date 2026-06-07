using System;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.Compatibility;

internal static class DeleteObjectHooks
{
    private const int DeleteObjectVa = 0x00558330;
    // Verified unique + lands at 0x00558330 offline (tools/pe_pattern.py).
    private static readonly byte?[] DeleteObjectPattern =
    [
        0x56, 0x57, 0x8B, 0xF9, 0xE8, null, null, null, null, 0x85, 0xC0, 0x8B, 0x74, 0x24, 0x0C
    ];

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int DeleteObjectDelegate(IntPtr thisPtr, uint objectId);

    private static DeleteObjectDelegate? _originalDeleteObject;
    private static DeleteObjectDelegate? _deleteObjectDetour;
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

        HookResolver.ResolveResult resolved = HookResolver.Resolve(textSection, "DeleteObject.DeleteObject", DeleteObjectPattern, DeleteObjectVa);
        if (!resolved.Success)
        {
            _statusMessage = $"ACCObjectMaint::DeleteObject unresolved (VA 0x{DeleteObjectVa:X8}).";
            RynthLog.Compat($"Compat: delete-object hook failed - {_statusMessage}");
            return;
        }

        try
        {
            _targetAddress = resolved.Address;
            _deleteObjectDetour = DeleteObjectDetour;
            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_deleteObjectDetour);
            _originalDeleteObject = Marshal.GetDelegateForFunctionPointer<DeleteObjectDelegate>(MinHook.HookCreate(_targetAddress, detourPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(_targetAddress);

            IsInstalled = true;
            _statusMessage = $"Hooked ACCObjectMaint::DeleteObject @ 0x{_targetAddress.ToInt32():X8}.";
            RynthLog.Verbose($"Compat: delete-object hook ready - DeleteObject=0x{_targetAddress.ToInt32():X8}");
        }
        catch (Exception ex)
        {
            _statusMessage = ex.Message;
            RynthLog.Compat($"Compat: delete-object hook failed - {ex.Message}");
        }
    }

    private static int DeleteObjectDetour(IntPtr thisPtr, uint objectId)
    {
        RecursionGuard.Tick("DeleteObjectHooks.DeleteObject");
        if (objectId != 0)
        {
            PaletteCache.Remove(objectId);
            PluginManager.QueueDeleteObject(objectId);
            AutoIdService.Evict(objectId);
        }

        return _originalDeleteObject!(thisPtr, objectId);
    }
}
