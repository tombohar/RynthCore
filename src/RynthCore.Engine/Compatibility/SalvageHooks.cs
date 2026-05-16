// ============================================================================
//  RynthCore.Engine - Compatibility/SalvageHooks.cs
//
//  Hooks gmSalvageUI::OpenSalvagePanel to capture the singleton 'this' pointer.
//  The captured instance is used by ClientHelperHooks for SalvagePanelAddItem
//  and SalvagePanelExecute, which call the thiscall instance methods directly.
// ============================================================================

using System;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;

namespace RynthCore.Engine.Compatibility;

internal static class SalvageHooks
{
    private const int OpenSalvagePanelFallbackVa = 0x004CBF70;

    // gmSalvageUI::OpenSalvagePanel(uint toolId)
    // Reads the salvage UI element from [esi+0x600], pushes it as 'this' to
    // call the +0x608 lookup. The two `[esi+0x600]` accesses + the two E8 calls
    // are very specific to this function.
    private static readonly byte?[] OpenSalvagePanelPattern =
    [
        0x8B, 0x44, 0x24, 0x04, 0x56, 0x8B, 0xF1, 0x8B,
        0x8E, 0x00, 0x06, 0x00, 0x00, 0x51, 0x8B, 0xCE,
        0x89, 0x86, 0x08, 0x06, 0x00, 0x00,
        0xE8, null, null, null, null,         // call rel32
        0x8B, 0x8E, 0x00, 0x06, 0x00, 0x00,
        0xE8, null, null, null, null          // call rel32
    ];

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void GmSalvageUIOpenSalvagePanelDelegate(IntPtr thisPtr, uint toolId);

    private static GmSalvageUIOpenSalvagePanelDelegate? _originalOpenSalvagePanel;
    private static GmSalvageUIOpenSalvagePanelDelegate? _openSalvagePanelDetour;
    private static IntPtr _gmSalvageUIInstance;
    private static bool _hookInstalled;
    private static string _statusMessage = "Not initialized.";

    public static bool IsInstalled => _hookInstalled;
    public static string StatusMessage => _statusMessage;

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

        var resolved = HookResolver.Resolve(textSection, "SalvageHooks.OpenSalvagePanel",
            OpenSalvagePanelPattern, OpenSalvagePanelFallbackVa);
        if (!resolved.Success)
        {
            _statusMessage = $"Resolve failed ({resolved.Detail}).";
            return;
        }

        try
        {
            _openSalvagePanelDetour = OpenSalvagePanelDetour;
            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_openSalvagePanelDetour);
            _originalOpenSalvagePanel = Marshal.GetDelegateForFunctionPointer<GmSalvageUIOpenSalvagePanelDelegate>(
                MinHook.HookCreate(resolved.Address, detourPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(resolved.Address);

            _hookInstalled = true;
            _statusMessage = $"Hooked gmSalvageUI::OpenSalvagePanel @ 0x{resolved.Address.ToInt32():X8}.";
            RynthLog.Compat($"SalvageHooks: install ok.");
        }
        catch (Exception ex)
        {
            _statusMessage = ex.Message;
            RynthLog.Compat($"SalvageHooks: install threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static int _fires;

    private static void OpenSalvagePanelDetour(IntPtr thisPtr, uint toolId)
    {
        if (thisPtr != IntPtr.Zero)
            _gmSalvageUIInstance = thisPtr;
        if (++_fires <= 3)
            RynthLog.Compat($"SalvageHooks: OpenSalvagePanel fired #{_fires} this=0x{thisPtr.ToInt32():X8} toolId=0x{toolId:X}");

        try { _originalOpenSalvagePanel!(thisPtr, toolId); }
        catch (Exception ex) { try { RynthLog.Compat($"SalvageHooks: original threw {ex.GetType().Name}: {ex.Message}"); } catch { } throw; }
    }
}
