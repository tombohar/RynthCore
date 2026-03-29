using System;
using System.Runtime.InteropServices;
using NexCore.Engine.Hooking;
using NexCore.Engine.Plugins;

namespace NexCore.Engine.Compatibility;

internal static class SelectedTargetHooks
{
    private const int SetSelectedObjectVa = 0x0058D110;
    private const int SelectedIdVa = 0x00871E54;
    private static readonly byte[] SetSelectedObjectSignature =
    [
        0x8B, 0x4C, 0x24, 0x08, 0x85, 0xC9, 0xA1, 0x54,
        0x1E, 0x87, 0x00, 0x56, 0x8B, 0x74, 0x24, 0x08,
        0x57, 0x8B, 0xF8, 0x75, 0x04, 0x3B, 0xC6, 0x74,
        0x7A, 0x85, 0xC0, 0x74, 0x14, 0x50, 0xE8
    ];

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetSelectedObjectDelegate(uint selectedId, int reselect);

    private static SetSelectedObjectDelegate? _originalSetSelectedObject;
    private static SetSelectedObjectDelegate? _setSelectedObjectDetour;
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

        int funcOff = SetSelectedObjectVa - textSection.TextBaseVa;
        if (!PatternScanner.VerifyBytes(textSection.Bytes, funcOff, SetSelectedObjectSignature))
        {
            _statusMessage = $"ACCWeenieObject::SetSelectedObject signature mismatch @ 0x{SetSelectedObjectVa:X8}.";
            log?.Invoke($"Compat: selected-target hook failed - {_statusMessage}");
            return;
        }

        try
        {
            _targetAddress = new IntPtr(textSection.TextBaseVa + funcOff);
            _setSelectedObjectDetour = SetSelectedObjectDetour;
            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_setSelectedObjectDetour);
            IntPtr originalPtr = MinHook.Hook(_targetAddress, detourPtr);
            _originalSetSelectedObject = Marshal.GetDelegateForFunctionPointer<SetSelectedObjectDelegate>(originalPtr);

            IsInstalled = true;
            _statusMessage = $"Hooked ACCWeenieObject::SetSelectedObject @ 0x{_targetAddress.ToInt32():X8}.";
            log?.Invoke(
                $"Compat: selected-target hook ready - SetSelectedObject=0x{_targetAddress.ToInt32():X8}, selectedId=0x{SelectedIdVa:X8}");
        }
        catch (Exception ex)
        {
            _statusMessage = ex.Message;
            log?.Invoke($"Compat: selected-target hook failed - {ex.Message}");
        }
    }

    private static void SetSelectedObjectDetour(uint selectedId, int reselect)
    {
        uint previousTargetId = ReadUInt32(SelectedIdVa);

        try
        {
            _originalSetSelectedObject!(selectedId, reselect);
        }
        catch (Exception ex)
        {
            try { EntryPoint.Log($"Compat: selected-target detour error - {ex.GetType().Name}: {ex.Message}"); } catch { }
            throw;
        }

        uint currentTargetId = ReadUInt32(SelectedIdVa);
        if (currentTargetId == previousTargetId)
            return;

        int count = System.Threading.Interlocked.Increment(ref _dispatchCount);
        if (count <= 5)
        {
            EntryPoint.Log(
                $"Compat: selected target #{count} prev=0x{previousTargetId:X8} curr=0x{currentTargetId:X8} arg=0x{selectedId:X8} reselect={reselect}");
        }

        PluginManager.QueueSelectedTargetChange(currentTargetId, previousTargetId);
    }

    private static uint ReadUInt32(int address)
    {
        return unchecked((uint)Marshal.ReadInt32(new IntPtr(address)));
    }
}
