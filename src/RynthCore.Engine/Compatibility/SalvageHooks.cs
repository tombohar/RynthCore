// ============================================================================
//  RynthCore.Engine - Compatibility/SalvageHooks.cs
//
//  Hooks gmSalvageUI::OpenSalvagePanel to capture the singleton 'this' pointer.
//  The captured instance is used by ClientHelperHooks for SalvagePanelAddItem
//  and SalvagePanelExecute, which call the thiscall instance methods directly.
//
//  VA derivation (map_offset + 0x00401000 = live VA):
//    000CAF70 gmSalvageUI::OpenSalvagePanel → 0x004CBF70
// ============================================================================

using System;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;

namespace RynthCore.Engine.Compatibility;

internal static class SalvageHooks
{
    // gmSalvageUI::OpenSalvagePanel(uint toolId) — thiscall
    // Map: 000CAF70 → live VA: 0x004CBF70
    private const int GmSalvageUIOpenSalvagePanelVa = 0x004CBF70;

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void GmSalvageUIOpenSalvagePanelDelegate(IntPtr thisPtr, uint toolId);

    private static GmSalvageUIOpenSalvagePanelDelegate? _originalOpenSalvagePanel;
    private static GmSalvageUIOpenSalvagePanelDelegate? _openSalvagePanelDetour; // held alive to prevent GC
    private static IntPtr _gmSalvageUIInstance;
    private static bool _hookInstalled;
    private static string _statusMessage = "Not initialized.";

    public static bool IsInstalled => _hookInstalled;
    public static string StatusMessage => _statusMessage;

    /// <summary>
    /// The captured gmSalvageUI singleton. Zero until the salvage panel has been
    /// opened at least once (either by the player or by SalvagePanelOpen).
    /// </summary>
    public static IntPtr GmSalvageUIInstance => _gmSalvageUIInstance;

    public static void Initialize()
    {
        if (_hookInstalled)
            return;

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection textSection))
        {
            _statusMessage = "acclient.exe not available.";
            return;
        }

        int funcOff = GmSalvageUIOpenSalvagePanelVa - textSection.TextBaseVa;
        if (funcOff < 0 || funcOff >= textSection.Bytes.Length)
        {
            _statusMessage = $"gmSalvageUI::OpenSalvagePanel VA out of range @ 0x{GmSalvageUIOpenSalvagePanelVa:X8}.";
            RynthLog.Compat($"Compat: salvage hook failed - {_statusMessage}");
            return;
        }

        byte firstByte = textSection.Bytes[funcOff];
        if (firstByte is 0x00 or 0xCC or 0xC3)
        {
            _statusMessage = $"gmSalvageUI::OpenSalvagePanel looks invalid @ 0x{GmSalvageUIOpenSalvagePanelVa:X8} (opcode 0x{firstByte:X2}).";
            RynthLog.Compat($"Compat: salvage hook failed - {_statusMessage}");
            return;
        }

        try
        {
            IntPtr targetAddress = new IntPtr(textSection.TextBaseVa + funcOff);
            _openSalvagePanelDetour = OpenSalvagePanelDetour;
            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_openSalvagePanelDetour);
            _originalOpenSalvagePanel = Marshal.GetDelegateForFunctionPointer<GmSalvageUIOpenSalvagePanelDelegate>(
                MinHook.HookCreate(targetAddress, detourPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(targetAddress);

            _hookInstalled = true;
            _statusMessage = $"Hooked gmSalvageUI::OpenSalvagePanel @ 0x{targetAddress.ToInt32():X8}.";
            RynthLog.Compat($"Compat: salvage hook ready @ 0x{targetAddress.ToInt32():X8}, firstByte=0x{firstByte:X2}");
        }
        catch (Exception ex)
        {
            _statusMessage = ex.Message;
            RynthLog.Compat($"Compat: salvage hook failed - {ex.Message}");
        }
    }

    private static void OpenSalvagePanelDetour(IntPtr thisPtr, uint toolId)
    {
        // Capture the gmSalvageUI singleton on every open so it stays fresh
        // even across hot-reloads or UI recreation.
        if (thisPtr != IntPtr.Zero)
            _gmSalvageUIInstance = thisPtr;

        _originalOpenSalvagePanel!(thisPtr, toolId);
    }
}
