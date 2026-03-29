using System;
using System.Runtime.InteropServices;
using System.Threading;
using NexCore.Engine.Hooking;
using NexCore.Engine.Plugins;

namespace NexCore.Engine.Compatibility;

internal static class UpdateObjectInventoryHooks
{
    private const int UpdateObjectInventoryVa = 0x0055A190;
    private static readonly byte[] UpdateObjectInventorySignature =
    [
        0x8B, 0x44, 0x24, 0x04, 0x50, 0xE8, 0x96, 0xE7,
        0xFA, 0xFF, 0x8B, 0x4C, 0x24, 0x08, 0x51, 0x8D,
        0x48, 0x3C, 0xE8, 0x69, 0xFF, 0xFF, 0xFF, 0xC2,
        0x08, 0x00
    ];

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void UpdateObjectInventoryDelegate(IntPtr thisPtr, uint objectId, IntPtr newInventory);

    private static UpdateObjectInventoryDelegate? _originalUpdateObjectInventory;
    private static UpdateObjectInventoryDelegate? _updateObjectInventoryDetour;
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

        int funcOff = UpdateObjectInventoryVa - textSection.TextBaseVa;
        if (!PatternScanner.VerifyBytes(textSection.Bytes, funcOff, UpdateObjectInventorySignature))
        {
            _statusMessage = $"ACCObjectMaint::UpdateObjectInventory signature mismatch @ 0x{UpdateObjectInventoryVa:X8}.";
            log?.Invoke($"Compat: update-object-inventory hook failed - {_statusMessage}");
            return;
        }

        try
        {
            _targetAddress = new IntPtr(textSection.TextBaseVa + funcOff);
            _updateObjectInventoryDetour = UpdateObjectInventoryDetour;
            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_updateObjectInventoryDetour);
            IntPtr originalPtr = MinHook.Hook(_targetAddress, detourPtr);
            _originalUpdateObjectInventory = Marshal.GetDelegateForFunctionPointer<UpdateObjectInventoryDelegate>(originalPtr);

            IsInstalled = true;
            _statusMessage = $"Hooked ACCObjectMaint::UpdateObjectInventory @ 0x{_targetAddress.ToInt32():X8}.";
            log?.Invoke($"Compat: update-object-inventory hook ready - UpdateObjectInventory=0x{_targetAddress.ToInt32():X8}");
        }
        catch (Exception ex)
        {
            _statusMessage = ex.Message;
            log?.Invoke($"Compat: update-object-inventory hook failed - {ex.Message}");
        }
    }

    private static void UpdateObjectInventoryDetour(IntPtr thisPtr, uint objectId, IntPtr newInventory)
    {
        _originalUpdateObjectInventory!(thisPtr, objectId, newInventory);
        if (objectId == 0)
            return;

        int count = Interlocked.Increment(ref _dispatchCount);
        if (count <= 5)
            EntryPoint.Log($"Compat: update object inventory #{count} id=0x{objectId:X8} inv=0x{newInventory.ToInt32():X8}");

        PluginManager.QueueUpdateObjectInventory(objectId);
    }
}
