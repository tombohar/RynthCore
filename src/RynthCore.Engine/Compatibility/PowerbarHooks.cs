// ============================================================================
//  RynthCore.Engine - Compatibility/PowerbarHooks.cs
//
//  Hooks gmPowerbarUI's RecvNotice_* trio so that, when SuppressOriginalDraw
//  is set, the vanilla attack/magic power bar never appears on screen. The
//  bar is ENTIRELY initialized via these notices — BeginPowerbar shows it,
//  SetPowerbarLevel updates it, FinishPowerbar hides it. By no-opping all
//  three, the bar stays invisible.
//
//  All three target addresses are now resolved via HookResolver — pattern
//  scan first, hardcoded VA fallback (logged as RISKY), or UNAVAILABLE.
//  The recv-notice functions all share the +0x600/+0xFA08 ClientCombatSystem
//  field offsets which are unique enough to anchor patterns against.
// ============================================================================

using System;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;

namespace RynthCore.Engine.Compatibility;

internal static class PowerbarHooks
{
    // Fallback VAs for the 4,841,472-byte client. Pattern-scan is now the
    // source of truth; these are the safety net.
    private const int GmPowerbarUIBeginFallbackVa  = 0x004DB390;
    private const int GmPowerbarUILevelFallbackVa  = 0x004DB1B0;
    private const int GmPowerbarUIFinishFallbackVa = 0x004DB1E0;

    // gmPowerbarUI::RecvNotice_BeginPowerbar(PowerBarMode) — uses jump-table
    // dispatch on the mode argument; the lea-decrement and ja-of-3 are unique.
    private static readonly byte?[] BeginPattern =
    [
        0x83, 0xEC, 0x10, 0x55, 0x8B, 0x6C, 0x24, 0x18,
        0x56, 0x8D, 0x45, 0xFF, 0x83, 0xF8, 0x03, 0x57,
        0x8B, 0xF1, 0x0F, 0x87, 0xBC, 0x00, 0x00, 0x00,
        0xFF, 0x24, 0x85, null, null, null, null   // jmp [imm32 + eax*4] — table
    ];

    // gmPowerbarUI::RecvNotice_SetPowerbarLevel(PowerBarMode,float)
    // Compares first arg with [ecx+4] then dispatches via push 0x10000034
    // (UI element ID) — unique pair.
    private static readonly byte?[] LevelPattern =
    [
        0x8B, 0x44, 0x24, 0x04, 0x3B, 0x41, 0x04, 0x75,
        0x23, 0x68, 0x34, 0x00, 0x00, 0x10, 0x81, 0xC1,
        0x08, 0xFA, 0xFF, 0xFF,
        0xE8, null, null, null, null,             // call rel32
        0x85, 0xC0, 0x74, 0x0F
    ];

    // gmPowerbarUI::RecvNotice_FinishPowerbar(PowerBarMode)
    // Same +0xFA08 anchor as Level, but with `75 33` instead of `75 23` and a
    // C7 41 04 zero-write (clears m_active before re-arming with 0x10000034).
    private static readonly byte?[] FinishPattern =
    [
        0x8B, 0x44, 0x24, 0x04, 0x3B, 0x41, 0x04, 0x75,
        0x33, 0x56, 0x8D, 0xB1, 0x08, 0xFA, 0xFF, 0xFF,
        0xC7, 0x41, 0x04, 0x00, 0x00, 0x00, 0x00, 0x68,
        0x34, 0x00, 0x00, 0x10, 0x8B, 0xCE,
        0xE8, null, null, null, null              // call rel32
    ];

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

    // UIElement::SetVisible(bool) — same address as ChatHooks/RadarHooks use.
    private const int UIElementSetVisibleFallbackVa = 0x00462390;
    private static readonly byte?[] UIElementSetVisiblePattern =
    [
        0x51, 0x53, 0x56, 0x57,
        0x8B, 0x3D, null, null, null, null,   // mov edi, ds:[imm32]
        0x8B, 0xF1,
        0xE8, null, null, null, null,         // call rel32
        0x8B, 0x9E, 0xA4, 0x00, 0x00, 0x00,
        0x88, 0x44, 0x24, 0x0F
    ];

    private static IntPtr _gmPowerbarUIInstance;
    private static IntPtr _uiElementSetVisibleAddress;
    private static bool _hookInstalled;
    private static bool _isHiddenAsserted;
    private static string _statusMessage = "Not initialized.";

    public static bool IsInstalled => _hookInstalled;
    public static string StatusMessage => _statusMessage;

    /// <summary>
    /// When true, <see cref="TickHide"/> calls UIElement::SetVisible(false) on
    /// the captured powerbar singleton each frame, hiding the retail attack/
    /// magic power bar. We do NOT skip the original Begin/Level/Finish notices
    /// — that pattern leaves AC's powerbar state machine half-initialized
    /// (Begin shows, but no Finish to hide it; or Finish without prior Begin),
    /// destabilizes its UIElement, and the next UI traversal (e.g. PopOutPanel
    /// undock) AVs on the corrupted state. Same fix the chat suppression uses.
    /// NOTE: <c>_gmPowerbarUIInstance</c> is captured from the first notice the
    /// powerbar dispatches, so SetVisible can only kick in after the player
    /// has at least once initiated combat. After that, the suppression sticks.
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

        // Resolve UIElement::SetVisible (used by TickHide). Direct call site,
        // not a hook, but treat through HookResolver so we get pattern-scan
        // resilience same as the other hooked addresses.
        var setVisible = HookResolver.Resolve(textSection, "PowerbarHooks.UIElement::SetVisible",
            UIElementSetVisiblePattern, UIElementSetVisibleFallbackVa);
        if (setVisible.Success) _uiElementSetVisibleAddress = setVisible.Address;

        bool beginOk  = TryInstallBeginHook(textSection);
        bool levelOk  = TryInstallLevelHook(textSection);
        bool finishOk = TryInstallFinishHook(textSection);

        if (beginOk && levelOk && finishOk)
        {
            _hookInstalled = true;
            _statusMessage = "Hooked gmPowerbarUI::RecvNotice_(Begin/Level/Finish).";
            RynthLog.Compat($"PowerbarHooks: all three notices hooked.");
        }
        else
        {
            _statusMessage = $"Partial install — Begin={beginOk}, Level={levelOk}, Finish={finishOk}.";
            RynthLog.Compat($"PowerbarHooks: {_statusMessage}");
        }
    }

    private static bool TryInstallBeginHook(AcClientTextSection textSection)
    {
        var resolved = HookResolver.Resolve(textSection, "PowerbarHooks.Begin",
            BeginPattern, GmPowerbarUIBeginFallbackVa);
        if (!resolved.Success) return false;

        try
        {
            _beginDetour = BeginDetour;
            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_beginDetour);
            _originalBegin = Marshal.GetDelegateForFunctionPointer<NoticeBeginDelegate>(
                MinHook.HookCreate(resolved.Address, detourPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(resolved.Address);
            return true;
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"PowerbarHooks: Begin hook install threw {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static bool TryInstallLevelHook(AcClientTextSection textSection)
    {
        var resolved = HookResolver.Resolve(textSection, "PowerbarHooks.Level",
            LevelPattern, GmPowerbarUILevelFallbackVa);
        if (!resolved.Success) return false;

        try
        {
            _levelDetour = LevelDetour;
            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_levelDetour);
            _originalLevel = Marshal.GetDelegateForFunctionPointer<NoticeLevelDelegate>(
                MinHook.HookCreate(resolved.Address, detourPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(resolved.Address);
            return true;
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"PowerbarHooks: Level hook install threw {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static bool TryInstallFinishHook(AcClientTextSection textSection)
    {
        var resolved = HookResolver.Resolve(textSection, "PowerbarHooks.Finish",
            FinishPattern, GmPowerbarUIFinishFallbackVa);
        if (!resolved.Success) return false;

        try
        {
            _finishDetour = FinishDetour;
            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_finishDetour);
            _originalFinish = Marshal.GetDelegateForFunctionPointer<NoticeFinishDelegate>(
                MinHook.HookCreate(resolved.Address, detourPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(resolved.Address);
            return true;
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"PowerbarHooks: Finish hook install threw {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static int _beginFires, _levelFires, _finishFires;

    private static void BeginDetour(IntPtr thisPtr, int powerBarMode)
    {
        RecursionGuard.Tick("PowerbarHooks.Begin");
        if (thisPtr != IntPtr.Zero) _gmPowerbarUIInstance = thisPtr;
        if (++_beginFires <= 3)
            RynthLog.Compat($"PowerbarHooks: Begin fired #{_beginFires} mode={powerBarMode} suppress={SuppressOriginalDraw}");
        // Always call original — see SuppressOriginalDraw comment. TickHide
        // hides via SetVisible(false) per frame.
        try { _originalBegin!(thisPtr, powerBarMode); }
        catch (Exception ex) { try { RynthLog.Compat($"PowerbarHooks: Begin original threw {ex.GetType().Name}: {ex.Message}"); } catch { } throw; }
    }

    private static void LevelDetour(IntPtr thisPtr, int powerBarMode, float level)
    {
        RecursionGuard.Tick("PowerbarHooks.Level");
        if (thisPtr != IntPtr.Zero) _gmPowerbarUIInstance = thisPtr;
        if (++_levelFires <= 3)
            RynthLog.Compat($"PowerbarHooks: Level fired #{_levelFires} mode={powerBarMode} level={level:F2} suppress={SuppressOriginalDraw}");
        try { _originalLevel!(thisPtr, powerBarMode, level); }
        catch (Exception ex) { try { RynthLog.Compat($"PowerbarHooks: Level original threw {ex.GetType().Name}: {ex.Message}"); } catch { } throw; }
    }

    private static void FinishDetour(IntPtr thisPtr, int powerBarMode)
    {
        RecursionGuard.Tick("PowerbarHooks.Finish");
        if (thisPtr != IntPtr.Zero) _gmPowerbarUIInstance = thisPtr;
        if (++_finishFires <= 3)
            RynthLog.Compat($"PowerbarHooks: Finish fired #{_finishFires} mode={powerBarMode} suppress={SuppressOriginalDraw}");
        try { _originalFinish!(thisPtr, powerBarMode); }
        catch (Exception ex) { try { RynthLog.Compat($"PowerbarHooks: Finish original threw {ex.GetType().Name}: {ex.Message}"); } catch { } throw; }
    }

    /// <summary>
    /// Per-frame visibility assertion — currently a no-op.
    ///
    /// Why: <c>gmPowerbarUI</c> doesn't appear to be a UIElement subclass with
    /// the UIElement subobject at offset 0 (unlike gmMainChatUI and gmRadarUI).
    /// Captured <c>_gmPowerbarUIInstance</c> is the gmPowerbarUI's `this`,
    /// which has a different vtable layout. Passing it to UIElement::SetVisible
    /// at 0x00462390 caused a NULL function-pointer dereference inside the
    /// function (observed crash: "instruction at 0x00000000 referenced memory
    /// at 0x00000000" on first combat engage after suppression toggled on).
    ///
    /// Until we resolve the correct UIElement child to hide (likely a member
    /// inside gmPowerbarUI at some offset), the suppression flag is wired but
    /// has no visual effect. Powerbar remains visible. Detours still always
    /// call AC's original notices, so AC's UI state is consistent and stable.
    /// </summary>
    public static void TickHide()
    {
        // Intentionally empty until we find the right UIElement to SetVisible on.
    }

    public static void ResetCachedInstance()
    {
        _gmPowerbarUIInstance = IntPtr.Zero;
        _isHiddenAsserted = false;
    }
}
