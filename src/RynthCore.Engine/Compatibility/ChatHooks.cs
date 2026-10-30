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
    private static IntPtr _gmMainChatVtable;     // the instance's vtable when captured
    // Set when a logoff is requested, cleared when the logout completes: AC frees
    // the chatbox during the logoff, and ListenToElementMessage can still fire on
    // the dying one, so it is not captured again in between.
    private static volatile bool _logoffInProgress;
    private static IntPtr _uiElementSetVisibleAddress;
    private static bool _hookInstalled;
    private static string _statusMessage = "Not initialized.";

    public static bool IsInstalled => _hookInstalled;
    public static string StatusMessage => _statusMessage;

    public static bool SuppressOriginalChat;   // the user's "Hide retail chat" option

    /// <summary>RynthChat's panel is on screen (set by RynthChatPanel).</summary>
    public static volatile bool ChatPanelShown;

    /// <summary>
    /// RynthChat is standing in for the retail chat: in the world, its panel is open AND
    /// "Hide retail chat" is on. Only then is the retail chatbox hidden and Enter routed to
    /// RynthChat — whichever chat is visible gets Enter. Outside the world Enter is AC's:
    /// at character select it enters the game.
    /// </summary>
    public static bool RynthChatOwnsChat =>
        SuppressOriginalChat && ChatPanelShown && LoginLifecycleHooks.HasObservedLoginComplete;

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
        if (thisPtr != IntPtr.Zero && !_logoffInProgress && thisPtr != _gmMainChatInstance)
        {
            _gmMainChatVtable = ClientObjectHooks.IsReadablePointer(thisPtr) ? Marshal.ReadIntPtr(thisPtr) : IntPtr.Zero;
            _gmMainChatInstance = thisPtr;
        }
        if (++_listenFires <= 3)
            RynthLog.Compat($"ChatHooks: Listen fired #{_listenFires} this=0x{thisPtr.ToInt32():X8}");
        try { _originalListen!(thisPtr, msgInfo); }
        catch (Exception ex) { try { RynthLog.Compat($"ChatHooks: Listen original threw {ex.GetType().Name}: {ex.Message}"); } catch { } throw; }
    }

    // Starts true so the first tick asserts the setting either way: after an engine reload
    // the previous generation may have left the chatbox hidden, and a fresh "false" here
    // would never show it again even with "Hide retail chat" off. SetVisible(true) on a
    // visible chatbox is a no-op.
    private static bool _isHiddenAsserted = true;

    public static unsafe void TickHide()
    {
        if (!LoginLifecycleHooks.HasObservedLoginComplete)
            return;

        IntPtr inst = _gmMainChatInstance;
        if (inst == IntPtr.Zero) return;
        if (_uiElementSetVisibleAddress == IntPtr.Zero) return;
        if (!StillAlive(inst))
        {
            RynthLog.Compat($"ChatHooks: chatbox 0x{inst.ToInt32():X8} is gone (freed); dropped until it is captured again.");
            _gmMainChatInstance = IntPtr.Zero;
            return;
        }

        if (RynthChatOwnsChat)
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

    /// <summary>
    /// Second guard against a freed chatbox: its memory must still be readable
    /// and still start with the vtable it had when captured. (A freed object's
    /// first bytes are overwritten by the heap; the 2026-09-28 14:41 crash called
    /// SetVisible on one and jumped to 0x0000FFFF.)
    /// </summary>
    private static bool StillAlive(IntPtr inst)
    {
        if (!ClientObjectHooks.IsReadablePointer(inst)) return false;
        IntPtr vtable = Marshal.ReadIntPtr(inst);
        if (_gmMainChatVtable == IntPtr.Zero) _gmMainChatVtable = vtable;
        return vtable == _gmMainChatVtable;
    }

    /// <summary>
    /// A logoff was requested (LogoffOriginProbe): drop the chatbox now. The logout
    /// notice (ResetCachedInstance) comes after AC has already freed it, and the
    /// per-frame SetVisible in between crashed on the freed object (2026-09-28).
    /// AC's main thread.
    /// </summary>
    public static void OnLogoffRequested()
    {
        _logoffInProgress = true;
        _gmMainChatInstance = IntPtr.Zero;
        _gmMainChatVtable = IntPtr.Zero;
        _isHiddenAsserted = true;
    }

    /// <summary>The logout completed (PluginManager's logout dispatch): the next chatbox is a new one.</summary>
    public static void ResetCachedInstance()
    {
        _gmMainChatInstance = IntPtr.Zero;
        _gmMainChatVtable = IntPtr.Zero;
        _logoffInProgress = false;
        _isHiddenAsserted = true;   // re-assert on the next instance, as at start-up
    }
}
