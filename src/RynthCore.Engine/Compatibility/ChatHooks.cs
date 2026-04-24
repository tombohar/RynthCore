// ============================================================================
//  RynthCore.Engine - Compatibility/ChatHooks.cs
//
//  Hooks gmMainChatUI::ListenToElementMessage purely to capture the widget's
//  'this' pointer on first UI dispatch. Per-frame visibility assertion is
//  driven from EndSceneHook (ChatHooks.TickHide), which avoids the unsafe
//  UseTime trampoline that was crashing with 0xC000001D — UseTime's prologue
//  contains a relative jump MinHook on x86 mis-relocated. ListenToElementMessage
//  is a large function with a standard MSVC prologue, safe to hook.
//
//  VA derivation (map offset + 0x00401000 = live VA):
//    000CD6F0 gmMainChatUI::ListenToElementMessage(UIElementMessageInfo const &)
//                                                           → 0x004CE6F0
//    00061390 UIElement::SetVisible(bool)                   → 0x00462390
// ============================================================================

using System;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;

namespace RynthCore.Engine.Compatibility;

internal static class ChatHooks
{
    private const int GmMainChatUIListenMsgVa = 0x004CE6F0;
    private const int UIElementSetVisibleVa   = 0x00462390;

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void ListenToElementMessageDelegate(IntPtr thisPtr, IntPtr msgInfo);

    private static ListenToElementMessageDelegate? _originalListen;
    private static ListenToElementMessageDelegate? _listenDetour; // held alive to prevent GC

    private static IntPtr _gmMainChatInstance;
    private static bool _hookInstalled;
    private static string _statusMessage = "Not initialized.";

    public static bool IsInstalled => _hookInstalled;
    public static string StatusMessage => _statusMessage;

    /// <summary>
    /// When true, TickHide() calls UIElement::SetVisible(false) on the captured
    /// chat widget each frame, hiding the retail chatbox. Re-asserted every frame.
    /// </summary>
    public static bool SuppressOriginalChat;

    /// <summary>The captured gmMainChatUI singleton. Zero until the user interacts
    /// with the chat at least once (hover, click, message dispatch).</summary>
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

        int funcOff = GmMainChatUIListenMsgVa - textSection.TextBaseVa;
        if (funcOff < 0 || funcOff >= textSection.Bytes.Length)
        {
            _statusMessage = $"ListenToElementMessage VA out of range @ 0x{GmMainChatUIListenMsgVa:X8}.";
            RynthLog.Compat($"Compat: chat hook failed - {_statusMessage}");
            return;
        }

        byte firstByte = textSection.Bytes[funcOff];
        if (firstByte is 0x00 or 0xCC or 0xC3)
        {
            _statusMessage = $"ListenToElementMessage looks invalid @ 0x{GmMainChatUIListenMsgVa:X8} (opcode 0x{firstByte:X2}).";
            RynthLog.Compat($"Compat: chat hook failed - {_statusMessage}");
            return;
        }

        try
        {
            IntPtr targetAddress = new IntPtr(textSection.TextBaseVa + funcOff);
            _listenDetour = ListenDetour;
            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_listenDetour);
            _originalListen = Marshal.GetDelegateForFunctionPointer<ListenToElementMessageDelegate>(
                MinHook.HookCreate(targetAddress, detourPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(targetAddress);

            _hookInstalled = true;
            _statusMessage = $"Hooked gmMainChatUI::ListenToElementMessage @ 0x{targetAddress.ToInt32():X8}.";
            RynthLog.Verbose($"Compat: chat hook ready @ 0x{targetAddress.ToInt32():X8}, firstByte=0x{firstByte:X2}");
        }
        catch (Exception ex)
        {
            _statusMessage = ex.Message;
            RynthLog.Compat($"Compat: chat hook failed - {ex.Message}");
        }
    }

    private static void ListenDetour(IntPtr thisPtr, IntPtr msgInfo)
    {
        if (thisPtr != IntPtr.Zero)
            _gmMainChatInstance = thisPtr;

        _originalListen!(thisPtr, msgInfo);
    }

    /// <summary>True once we've issued SetVisible(false) — used so we know to
    /// restore visibility one frame after the suppress flag flips off.</summary>
    private static bool _isHiddenAsserted;

    /// <summary>
    /// Per-frame visibility assertion — called from EndSceneHook every frame.
    /// While suppression is on, calls SetVisible(false) every frame so the game
    /// can't sneak the chat back on. When suppression flips off, calls
    /// SetVisible(true) one time to restore the retail chatbox.
    /// </summary>
    public static unsafe void TickHide()
    {
        IntPtr inst = _gmMainChatInstance;
        if (inst == IntPtr.Zero) return;

        if (SuppressOriginalChat)
        {
            try
            {
                ((delegate* unmanaged[Thiscall]<IntPtr, int, void>)UIElementSetVisibleVa)(inst, 0);
            }
            catch { /* best-effort */ }
            _isHiddenAsserted = true;
            return;
        }

        // Suppression is off — if we previously hid it, show it again now.
        if (_isHiddenAsserted)
        {
            try
            {
                ((delegate* unmanaged[Thiscall]<IntPtr, int, void>)UIElementSetVisibleVa)(inst, 1);
            }
            catch { /* best-effort */ }
            _isHiddenAsserted = false;
        }
    }
}
