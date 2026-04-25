// ============================================================================
//  RynthCore.Engine - Compatibility/PowerbarHooks.cs
//
//  Hooks gmPowerbarUI's RecvNotice_* trio so that, when SuppressOriginalDraw
//  is set, the vanilla attack/magic power bar never appears on screen. The
//  bar is ENTIRELY initialized via these notices — BeginPowerbar shows it,
//  SetPowerbarLevel updates it, FinishPowerbar hides it. By no-opping all
//  three, the bar stays invisible.
//
//  VA derivation (map offset + 0x00401000 = live VA — same as RadarHooks):
//    000DA390 gmPowerbarUI::RecvNotice_BeginPowerbar(PowerBarMode)         → 0x004DB390
//    000DA1B0 gmPowerbarUI::RecvNotice_SetPowerbarLevel(PowerBarMode,float)→ 0x004DB1B0
//    000DA1E0 gmPowerbarUI::RecvNotice_FinishPowerbar(PowerBarMode)        → 0x004DB1E0
// ============================================================================

using System;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;

namespace RynthCore.Engine.Compatibility;

internal static class PowerbarHooks
{
    private const int GmPowerbarUIBeginVa  = 0x004DB390;
    private const int GmPowerbarUILevelVa  = 0x004DB1B0;
    private const int GmPowerbarUIFinishVa = 0x004DB1E0;

    // ClientCombatSystem direct hide. Map: 0016A5F0 + 0x00401000 = 0x0056B5F0.
    // Thiscall void HidePowerBar() — call on the CCS singleton via the global
    // pointer that ClientCombatHooks already locates at 0x0087166C.
    private const int ClientCombatSystemHidePowerBarVa = 0x0056B5F0;
    private const int CombatSystemPtrVa                = 0x0087166C;

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void HidePowerBarDelegate(IntPtr thisPtr);

    private static IntPtr _gmPowerbarUIInstance;

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void NoticeBeginDelegate(IntPtr thisPtr, int powerBarMode);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void NoticeLevelDelegate(IntPtr thisPtr, int powerBarMode, float level);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void NoticeFinishDelegate(IntPtr thisPtr, int powerBarMode);

    private static NoticeBeginDelegate?  _originalBegin;
    private static NoticeBeginDelegate?  _beginDetour;
    private static NoticeLevelDelegate?  _originalLevel;
    private static NoticeLevelDelegate?  _levelDetour;
    private static NoticeFinishDelegate? _originalFinish;
    private static NoticeFinishDelegate? _finishDetour;

    private static bool _hookInstalled;
    private static string _statusMessage = "Not initialized.";

    public static bool IsInstalled => _hookInstalled;
    public static string StatusMessage => _statusMessage;

    /// <summary>
    /// When true, the detours skip the original gmPowerbarUI notices, so the
    /// retail attack/magic power bar never appears. A plugin can render its
    /// own equivalent (or just leave it hidden).
    /// </summary>
    public static bool SuppressOriginalDraw;

    public static void Initialize()
    {
        if (_hookInstalled)
            return;

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection textSection))
        {
            _statusMessage = "acclient.exe not available.";
            return;
        }

        bool beginOk  = TryInstallBeginHook(textSection);
        bool levelOk  = TryInstallLevelHook(textSection);
        bool finishOk = TryInstallFinishHook(textSection);

        if (beginOk && levelOk && finishOk)
        {
            _hookInstalled = true;
            _statusMessage = "Hooked gmPowerbarUI::RecvNotice_(Begin/Level/Finish).";
            RynthLog.Compat($"Compat: powerbar hooks ready (Begin=0x{GmPowerbarUIBeginVa:X8}, Level=0x{GmPowerbarUILevelVa:X8}, Finish=0x{GmPowerbarUIFinishVa:X8}).");
        }
        else
        {
            _statusMessage = $"Partial install — Begin={beginOk}, Level={levelOk}, Finish={finishOk}.";
            RynthLog.Compat($"Compat: powerbar hook {_statusMessage}");
        }
    }

    private static bool TryInstallBeginHook(AcClientTextSection textSection)
    {
        int funcOff = GmPowerbarUIBeginVa - textSection.TextBaseVa;
        if (!IsValidPrologue(textSection, funcOff, "Begin", GmPowerbarUIBeginVa))
            return false;

        try
        {
            IntPtr targetAddress = new IntPtr(textSection.TextBaseVa + funcOff);
            _beginDetour = BeginDetour;
            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_beginDetour);
            _originalBegin = Marshal.GetDelegateForFunctionPointer<NoticeBeginDelegate>(
                MinHook.HookCreate(targetAddress, detourPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(targetAddress);
            return true;
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"Compat: Powerbar Begin hook failed - {ex.Message}");
            return false;
        }
    }

    private static bool TryInstallLevelHook(AcClientTextSection textSection)
    {
        int funcOff = GmPowerbarUILevelVa - textSection.TextBaseVa;
        if (!IsValidPrologue(textSection, funcOff, "Level", GmPowerbarUILevelVa))
            return false;

        try
        {
            IntPtr targetAddress = new IntPtr(textSection.TextBaseVa + funcOff);
            _levelDetour = LevelDetour;
            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_levelDetour);
            _originalLevel = Marshal.GetDelegateForFunctionPointer<NoticeLevelDelegate>(
                MinHook.HookCreate(targetAddress, detourPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(targetAddress);
            return true;
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"Compat: Powerbar Level hook failed - {ex.Message}");
            return false;
        }
    }

    private static bool TryInstallFinishHook(AcClientTextSection textSection)
    {
        int funcOff = GmPowerbarUIFinishVa - textSection.TextBaseVa;
        if (!IsValidPrologue(textSection, funcOff, "Finish", GmPowerbarUIFinishVa))
            return false;

        try
        {
            IntPtr targetAddress = new IntPtr(textSection.TextBaseVa + funcOff);
            _finishDetour = FinishDetour;
            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_finishDetour);
            _originalFinish = Marshal.GetDelegateForFunctionPointer<NoticeFinishDelegate>(
                MinHook.HookCreate(targetAddress, detourPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(targetAddress);
            return true;
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"Compat: Powerbar Finish hook failed - {ex.Message}");
            return false;
        }
    }

    private static bool IsValidPrologue(AcClientTextSection textSection, int funcOff, string label, int va)
    {
        if (funcOff < 0 || funcOff >= textSection.Bytes.Length)
        {
            RynthLog.Compat($"Compat: Powerbar {label} VA out of range @ 0x{va:X8}.");
            return false;
        }

        byte firstByte = textSection.Bytes[funcOff];
        if (firstByte is 0x00 or 0xCC or 0xC3)
        {
            RynthLog.Compat($"Compat: Powerbar {label} looks invalid @ 0x{va:X8} (opcode 0x{firstByte:X2}).");
            return false;
        }
        return true;
    }

    // Per-call diagnostic — emits the first ~5 fires of each detour so we
    // can confirm the hook is actually reached when the user expects the bar.
    private static int _beginFires, _levelFires, _finishFires;

    private static void BeginDetour(IntPtr thisPtr, int powerBarMode)
    {
        if (thisPtr != IntPtr.Zero) _gmPowerbarUIInstance = thisPtr;
        if (++_beginFires <= 5)
            RynthLog.Compat($"Powerbar Begin fired #{_beginFires} (mode={powerBarMode}, suppress={SuppressOriginalDraw})");
        if (SuppressOriginalDraw)
            return;
        _originalBegin!(thisPtr, powerBarMode);
    }

    private static void LevelDetour(IntPtr thisPtr, int powerBarMode, float level)
    {
        if (thisPtr != IntPtr.Zero) _gmPowerbarUIInstance = thisPtr;
        if (++_levelFires <= 5)
            RynthLog.Compat($"Powerbar Level fired #{_levelFires} (mode={powerBarMode}, level={level:F2}, suppress={SuppressOriginalDraw})");
        if (SuppressOriginalDraw)
            return;
        _originalLevel!(thisPtr, powerBarMode, level);
    }

    private static void FinishDetour(IntPtr thisPtr, int powerBarMode)
    {
        if (thisPtr != IntPtr.Zero) _gmPowerbarUIInstance = thisPtr;
        if (++_finishFires <= 5)
            RynthLog.Compat($"Powerbar Finish fired #{_finishFires} (mode={powerBarMode}, suppress={SuppressOriginalDraw})");
        if (SuppressOriginalDraw)
            return;
        _originalFinish!(thisPtr, powerBarMode);
    }

    // Per-frame SetVisible(false) on the captured pointer was crashing
    // (0xC0000005 null-deref) — the instance grabbed from LevelDetour isn't
    // safe to invoke UIElement::SetVisible on. Suppression is currently a
    // no-op of the notices only; we'll need a different hide strategy later.
}
