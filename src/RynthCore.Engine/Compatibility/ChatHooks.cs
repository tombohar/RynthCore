// ============================================================================
//  RynthCore.Engine - Compatibility/ChatHooks.cs
//
//  Hooks gmMainChatUI::ListenToElementMessage purely to capture the widget's
//  'this' pointer on first UI dispatch. Per-frame visibility assertion is
//  driven from EndSceneHook (ChatHooks.TickHide) and calls UIElement::SetVisible
//  directly on the captured singleton.
//
//  Both addresses (the listen hook and the SetVisible call target) are now
//  resolved via HookResolver — pattern-scan first, fallback VA second.
// ============================================================================

using System;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;

namespace RynthCore.Engine.Compatibility;

internal static class ChatHooks
{
    // Fallback VAs (4,841,472-byte client). Pattern-scan is the source of truth.
    private const int GmMainChatUIListenMsgFallbackVa = 0x004CE6F0;
    private const int UIElementSetVisibleFallbackVa   = 0x00462390;

    // gmMainChatUI::ListenToElementMessage(UIElementMessageInfo const&)
    // Reads the message kind from [esi+8], decrements, switches on it (0x3C, 0x3E, ...).
    private static readonly byte?[] ListenToElementMessagePattern =
    [
        0x56, 0x8B, 0x74, 0x24, 0x08, 0x8B, 0x46, 0x08,
        0x48, 0x57, 0x8B, 0xF9, 0x74, 0x72, 0x83, 0xE8,
        0x06, 0x75, 0x7D, 0x8B, 0x4E, 0x04, 0x8B, 0x01,
        0x6A, 0x06, 0xFF, 0x90, 0x94, 0x00, 0x00, 0x00
    ];

    // UIElement::SetVisible(bool) — duplicated against RadarHooks deliberately;
    // each consumer resolves independently so a partial failure isolates per-file.
    private static readonly byte?[] UIElementSetVisiblePattern =
    [
        0x51, 0x53, 0x56, 0x57,
        0x8B, 0x3D, null, null, null, null,   // mov edi, ds:[imm32]
        0x8B, 0xF1,
        0xE8, null, null, null, null,         // call rel32
        0x8B, 0x9E, 0xA4, 0x00, 0x00, 0x00,
        0x88, 0x44, 0x24, 0x0F
    ];

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void ListenToElementMessageDelegate(IntPtr thisPtr, IntPtr msgInfo);

    private static ListenToElementMessageDelegate? _originalListen;
    private static ListenToElementMessageDelegate? _listenDetour;

    private static IntPtr _gmMainChatInstance;
    private static IntPtr _uiElementSetVisibleAddress;
    private static bool _hookInstalled;
    private static string _statusMessage = "Not initialized.";

    public static bool IsInstalled => _hookInstalled;
    public static string StatusMessage => _statusMessage;

    public static bool SuppressOriginalChat;

    public static IntPtr GmMainChatInstance => _gmMainChatInstance;

    public static void Initialize()
    {
        if (_hookInstalled)
            return;

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection textSection))
        {
            _statusMessage = "acclient.exe not available.";
            return;
        }

        var setVisible = HookResolver.Resolve(textSection, "ChatHooks.UIElement::SetVisible",
            UIElementSetVisiblePattern, UIElementSetVisibleFallbackVa);
        if (setVisible.Success) _uiElementSetVisibleAddress = setVisible.Address;

        var listen = HookResolver.Resolve(textSection, "ChatHooks.ListenToElementMessage",
            ListenToElementMessagePattern, GmMainChatUIListenMsgFallbackVa);
        if (!listen.Success)
        {
            _statusMessage = $"ListenToElementMessage resolve failed ({listen.Detail}).";
            return;
        }

        try
        {
            _listenDetour = ListenDetour;
            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_listenDetour);
            _originalListen = Marshal.GetDelegateForFunctionPointer<ListenToElementMessageDelegate>(
                MinHook.HookCreate(listen.Address, detourPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(listen.Address);

            _hookInstalled = true;
            _statusMessage = $"Hooked gmMainChatUI::ListenToElementMessage @ 0x{listen.Address.ToInt32():X8}.";
            RynthLog.Compat($"ChatHooks: install ok.");
        }
        catch (Exception ex)
        {
            _statusMessage = ex.Message;
            RynthLog.Compat($"ChatHooks: install threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static int _listenFires;

    private static void ListenDetour(IntPtr thisPtr, IntPtr msgInfo)
    {
        RecursionGuard.Tick("ChatHooks.Listen");
        if (thisPtr != IntPtr.Zero)
            _gmMainChatInstance = thisPtr;
        if (++_listenFires <= 3)
            RynthLog.Compat($"ChatHooks: Listen fired #{_listenFires} this=0x{thisPtr.ToInt32():X8}");
        try { _originalListen!(thisPtr, msgInfo); }
        catch (Exception ex) { try { RynthLog.Compat($"ChatHooks: Listen original threw {ex.GetType().Name}: {ex.Message}"); } catch { } throw; }
    }

    private static bool _isHiddenAsserted;

    public static unsafe void TickHide()
    {
        if (!LoginLifecycleHooks.HasObservedLoginComplete)
            return;

        IntPtr inst = _gmMainChatInstance;
        if (inst == IntPtr.Zero) return;
        if (_uiElementSetVisibleAddress == IntPtr.Zero) return;

        if (SuppressOriginalChat)
        {
            try
            {
                ((delegate* unmanaged[Thiscall]<IntPtr, int, void>)_uiElementSetVisibleAddress)(inst, 0);
            }
            catch { /* best-effort */ }
            _isHiddenAsserted = true;
            return;
        }

        if (_isHiddenAsserted)
        {
            try
            {
                ((delegate* unmanaged[Thiscall]<IntPtr, int, void>)_uiElementSetVisibleAddress)(inst, 1);
            }
            catch { /* best-effort */ }
            _isHiddenAsserted = false;
        }
    }

    public static void ResetCachedInstance()
    {
        _gmMainChatInstance = IntPtr.Zero;
        _isHiddenAsserted = false;
    }
}
