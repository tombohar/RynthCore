using System;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// Dispatches chat commands by calling individual CM_Communication::Event_*
/// functions directly. All are cdecl and use AC1Legacy::PStringBase&lt;char&gt;.
/// Falls back to keyboard simulation for unrecognized slash commands.
/// </summary>
internal static unsafe class ChatCommandDispatcher
{
    // ── Pattern-resolved binding (1a hardening, 2026-06-05) ─────────────
    // The Event_*/PStringBase VAs below are now FALLBACKs; these signatures (verified unique
    // + landing exactly at the VA offline via tools/pe_pattern.py) are the source of truth.
    // Declared BEFORE the fn-ptr fields so the static field initializers see them. The Event_*
    // senders are near-identical templates, so the patterns run long to reach the distinct tail.
    private static readonly byte?[] PatEventTalk = [ 0x83, 0xEC, 0x0C, 0x53, 0x56, 0x57, 0xE8, null, null, null, null, 0x8B, 0x5C, 0x24, 0x1C, 0x89, 0x44, 0x24, 0x14, 0x6A, 0x00, 0x8D, 0x44, 0x24, 0x10, 0x50, 0x8B, 0xCB, 0xC7, 0x44, 0x24, 0x18, 0x2C, 0x2C, 0x80, 0x00, 0xC7, 0x44, 0x24, 0x14, 0x00, 0x00, 0x00, 0x00, 0xE8, null, null, null, null, 0x6A, 0x00, 0x8D, 0x4C, 0x24, 0x10, 0x51, 0x8D, 0x4C, 0x24, 0x18, 0x8B, 0xF0, 0xE8, null, null, null, null, 0x8D, 0x74, 0x06, 0x04, 0x56, 0xE8, null, null, null, null, 0x83, 0xC4, 0x04, 0x56, 0x8D, 0x54, 0x24, 0x10, 0x52, 0x8D, 0x4C, 0x24, 0x18, 0x89, 0x44, 0x24, 0x14, 0x8B, 0xF8, 0xE8, null, null, null, null, 0x8B, 0x44, 0x24, 0x0C, 0xC7, 0x00, 0x15 ];
    private static readonly byte?[] PatEventEmote = [ 0x83, 0xEC, 0x0C, 0x53, 0x56, 0x57, 0xE8, null, null, null, null, 0x8B, 0x5C, 0x24, 0x1C, 0x89, 0x44, 0x24, 0x14, 0x6A, 0x00, 0x8D, 0x44, 0x24, 0x10, 0x50, 0x8B, 0xCB, 0xC7, 0x44, 0x24, 0x18, 0x2C, 0x2C, 0x80, 0x00, 0xC7, 0x44, 0x24, 0x14, 0x00, 0x00, 0x00, 0x00, 0xE8, null, null, null, null, 0x6A, 0x00, 0x8D, 0x4C, 0x24, 0x10, 0x51, 0x8D, 0x4C, 0x24, 0x18, 0x8B, 0xF0, 0xE8, null, null, null, null, 0x8D, 0x74, 0x06, 0x04, 0x56, 0xE8, null, null, null, null, 0x83, 0xC4, 0x04, 0x56, 0x8D, 0x54, 0x24, 0x10, 0x52, 0x8D, 0x4C, 0x24, 0x18, 0x89, 0x44, 0x24, 0x14, 0x8B, 0xF8, 0xE8, null, null, null, null, 0x8B, 0x44, 0x24, 0x0C, 0xC7, 0x00, 0xDF ];
    private static readonly byte?[] PatEventSoulEmote = [ 0x83, 0xEC, 0x0C, 0x53, 0x56, 0x57, 0xE8, null, null, null, null, 0x8B, 0x5C, 0x24, 0x1C, 0x89, 0x44, 0x24, 0x14, 0x6A, 0x00, 0x8D, 0x44, 0x24, 0x10, 0x50, 0x8B, 0xCB, 0xC7, 0x44, 0x24, 0x18, 0x2C, 0x2C, 0x80, 0x00, 0xC7, 0x44, 0x24, 0x14, 0x00, 0x00, 0x00, 0x00, 0xE8, null, null, null, null, 0x6A, 0x00, 0x8D, 0x4C, 0x24, 0x10, 0x51, 0x8D, 0x4C, 0x24, 0x18, 0x8B, 0xF0, 0xE8, null, null, null, null, 0x8D, 0x74, 0x06, 0x04, 0x56, 0xE8, null, null, null, null, 0x83, 0xC4, 0x04, 0x56, 0x8D, 0x54, 0x24, 0x10, 0x52, 0x8D, 0x4C, 0x24, 0x18, 0x89, 0x44, 0x24, 0x14, 0x8B, 0xF8, 0xE8, null, null, null, null, 0x8B, 0x44, 0x24, 0x0C, 0xC7, 0x00, 0xE1 ];
    private static readonly byte?[] PatEventTalkDirectByName = [ 0x83, 0xEC, 0x0C, 0x53, 0x55, 0x56, 0x57, 0xE8, null, null, null, null, 0x8B, 0x5C, 0x24, 0x20, 0x89, 0x44, 0x24, 0x18, 0x6A, 0x00, 0x8D, 0x44, 0x24, 0x14, 0x50, 0x8B, 0xCB, 0xC7, 0x44, 0x24, 0x1C, 0x2C, 0x2C, 0x80, 0x00, 0xC7, 0x44, 0x24, 0x18, 0x00, 0x00, 0x00, 0x00, 0xE8, null, null, null, null, 0x8B, 0x6C, 0x24, 0x24, 0x6A, 0x00, 0x8D, 0x4C, 0x24, 0x14, 0x51, 0x8B, 0xCD, 0x8B, 0xF0, 0xE8, null, null, null, null, 0x6A, 0x00, 0x8D, 0x54, 0x24, 0x14, 0x52, 0x8D, 0x4C, 0x24, 0x1C, 0x03, 0xF0, 0xE8, null, null, null, null, 0x8D, 0x74, 0x06, 0x04, 0x56, 0xE8, null, null, null, null, 0x83, 0xC4, 0x04, 0x89, 0x44, 0x24, 0x10, 0x8B, 0xF8, 0x56, 0x8D, 0x44, 0x24, 0x14, 0x50, 0x8D, 0x4C, 0x24, 0x1C, 0xE8, null, null, null, null, 0x8B, 0x4C, 0x24, 0x10, 0xC7, 0x01, 0x5D ];
    private static readonly byte?[] PatEventChannelBroadcast = [ 0x83, 0xEC, 0x0C, 0x53, 0x56, 0x57, 0xE8, null, null, null, null, 0x8B, 0x5C, 0x24, 0x20, 0x89, 0x44, 0x24, 0x14, 0x6A, 0x00, 0x8D, 0x44, 0x24, 0x10, 0x50, 0x8B, 0xCB, 0xC7, 0x44, 0x24, 0x18, 0x2C, 0x2C, 0x80, 0x00, 0xC7, 0x44, 0x24, 0x14, 0x00, 0x00, 0x00, 0x00, 0xE8, null, null, null, null, 0x6A, 0x00, 0x8D, 0x4C, 0x24, 0x10, 0x51, 0x8D, 0x4C, 0x24, 0x18, 0x8B, 0xF0, 0xE8, null, null, null, null, 0x8D, 0x74, 0x06, 0x08, 0x56, 0xE8, null, null, null, null, 0x83, 0xC4, 0x04, 0x56, 0x8D, 0x54, 0x24, 0x10, 0x52, 0x8D, 0x4C, 0x24, 0x18, 0x89, 0x44, 0x24, 0x14, 0x8B, 0xF8, 0xE8, null, null, null, null, 0x8B, 0x44, 0x24, 0x0C, 0x8B, 0x4C, 0x24, 0x1C, 0xC7, 0x00, 0x47 ];
    private static readonly byte?[] PatPStringBaseCtor = [ 0x56, 0x8B, 0x74, 0x24, 0x08, 0x85, 0xF6, 0x57, 0x8B, 0xF9, 0x74, 0x35, 0x80, 0x3E, 0x00, 0x74, 0x30, 0x8B, 0xC6, 0x8D, 0x50, 0x01, 0x8A, 0x08, 0x40, 0x84, 0xC9, 0x75, 0xF9, 0x2B, 0xC2, 0x50, 0x8B, 0xCF, 0xE8, null, null, null, null, 0x8B, 0x07 ];
    private static readonly byte?[] PatPStringBaseClear = [ 0xA1, 0x1C, 0xF1, 0x8E, 0x00, 0x56, 0x57, 0x8B, 0xF9, 0x8B, 0x37, 0x3B, 0xF0, 0x74, 0x2D, 0x8D, 0x46, 0x04, 0x50, 0xFF, 0x15, 0xF8, 0x31, 0x79, 0x00, 0x85, 0xC0, 0x75, 0x0C, 0x85, 0xF6, 0x74, 0x08, 0x8B, 0x16, 0x6A, 0x01, 0x8B, 0xCE, 0xFF, 0x12, 0xA1, 0x1C, 0xF1, 0x8E, 0x00, 0x8B, 0xC8, 0x83, 0xC1, 0x04, 0x51, 0x89, 0x07, 0xFF, 0x15, 0xFC, 0x31, 0x79, 0x00, 0x5F ];

    private static void* ResolveVa(string name, byte?[] pattern, int fallbackVa)
    {
        if (!AcClientModule.TryReadTextSection(out AcClientTextSection text))
            return (void*)fallbackVa;
        HookResolver.ResolveResult r = HookResolver.Resolve(text, name, pattern, fallbackVa);
        return (void*)(r.Success ? r.Address : new IntPtr(fallbackVa));
    }

    // Phase B: PStringBase<char>::s_NullBuffer (0x008EF11C) via code-xref (3B 3D <addr>), offset 2.
    private static readonly byte?[] PatXrefPStringNullBuffer = [ 0x3B, 0x3D, null, null, null, null, 0x74, 0xF1 ];
    private static IntPtr ResolveDataVa(string name, byte?[] pattern, int operandOffset, int fallbackVa)
    {
        if (!AcClientModule.TryReadTextSection(out AcClientTextSection text))
            return new IntPtr(fallbackVa);
        return HookResolver.ResolveData(text, name, pattern, operandOffset, fallbackVa).Address;
    }
    // CM_Communication::Event_* — all cdecl, all using AC1Legacy::PStringBase<char>
    private static readonly delegate* unmanaged[Cdecl]<LegacyPString*, byte> FnEventTalk =
        (delegate* unmanaged[Cdecl]<LegacyPString*, byte>)ResolveVa("ChatCmd.Event_Talk", PatEventTalk, 0x006A53E0);
    private static readonly delegate* unmanaged[Cdecl]<LegacyPString*, byte> FnEventEmote =
        (delegate* unmanaged[Cdecl]<LegacyPString*, byte>)ResolveVa("ChatCmd.Event_Emote", PatEventEmote, 0x006A4F40);
    private static readonly delegate* unmanaged[Cdecl]<LegacyPString*, byte> FnEventSoulEmote =
        (delegate* unmanaged[Cdecl]<LegacyPString*, byte>)ResolveVa("ChatCmd.Event_SoulEmote", PatEventSoulEmote, 0x006A5320);
    private static readonly delegate* unmanaged[Cdecl]<LegacyPString*, LegacyPString*, byte> FnEventTalkDirectByName =
        (delegate* unmanaged[Cdecl]<LegacyPString*, LegacyPString*, byte>)ResolveVa("ChatCmd.Event_TalkDirectByName", PatEventTalkDirectByName, 0x006A55A0);
    private static readonly delegate* unmanaged[Cdecl]<uint, LegacyPString*, byte> FnEventChannelBroadcast =
        (delegate* unmanaged[Cdecl]<uint, LegacyPString*, byte>)ResolveVa("ChatCmd.Event_ChannelBroadcast", PatEventChannelBroadcast, 0x006A4E50);

    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const uint WM_CHAR = 0x0102;
    private const int VK_RETURN = 0x0D;

    /// <summary>
    /// When true, all commands are dispatched via keyboard simulation
    /// rather than direct function calls.
    /// </summary>
    public static bool UseDirectChatInput { get; set; }

    /// <summary>
    /// Recursion guard for the plugin pre-dispatch step. A plugin's
    /// OnChatBarEnter handler may chain by calling Host.InvokeChatParser →
    /// PluginManager.InvokeChatParser → ClientHelperHooks.InvokeParser →
    /// ChatCommandDispatcher.Dispatch. Without this guard the plugin would
    /// re-enter its own OnChatBarEnter on the chained line and could loop.
    /// </summary>
    [ThreadStatic]
    private static bool _inPluginPreDispatch;

    /// <summary>
    /// AC1Legacy::PStringBase&lt;char&gt; for Event_* functions.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct LegacyPString
    {
        public IntPtr Buffer;

        private static readonly IntPtr NullBufferVa = ResolveDataVa("ChatCmd.PStringChar_NullBuffer", PatXrefPStringNullBuffer, 2, 0x008EF11C);
        private static readonly delegate* unmanaged[Thiscall]<LegacyPString*, byte*, void> Ctor =
            (delegate* unmanaged[Thiscall]<LegacyPString*, byte*, void>)ResolveVa("ChatCmd.PStringBaseC_ctor", PatPStringBaseCtor, 0x0048C3E0);
        private static readonly delegate* unmanaged[Thiscall]<LegacyPString*, void> ClearFn =
            (delegate* unmanaged[Thiscall]<LegacyPString*, void>)ResolveVa("ChatCmd.PStringBaseC_Clear", PatPStringBaseClear, 0x004AB990);

        public static LegacyPString Create(string text)
        {
            var value = new LegacyPString { Buffer = Marshal.ReadIntPtr(NullBufferVa) };
            byte[] bytes = new byte[text.Length + 1];
            for (int i = 0; i < text.Length; i++)
                bytes[i] = (byte)text[i];
            fixed (byte* pBytes = bytes)
                Ctor(&value, pBytes);
            return value;
        }

        public void Dispose()
        {
            fixed (LegacyPString* ptr = &this)
                ClearFn(ptr);
        }
    }

    public static bool Dispatch(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        // Deep-audit finding #4 (2026-06-18): this whole function is not
        // network-only — it runs arbitrary plugin OnChatBarEnter handlers,
        // constructs/destructs AC's native PStringBase<char> in AC's heap,
        // and calls Event_Talk/Event_Emote/etc. directly against AC's live
        // chat-manager. The old inline comment claiming this was "thread-safe"
        // was an inherited Decal/VTank assumption that contradicts the
        // engine's own AcMainThreadQueue design (see the documented
        // 0x00460D1D chat-buffer write-AV class). Gating here (rather than at
        // each call site) covers every off-thread caller at once:
        // OnLoginCommandRunner (ThreadPool), ChatFileDispatcher
        // (FileSystemWatcher thread), and Host.InvokeChatParser (plugin
        // export, any thread). The queued call re-invokes this same method
        // from the drain, where IsOnMainThread is satisfied and the body
        // below runs directly — so there's only one code path to keep in sync.
        if (!MainThreadGuard.IsOnMainThread())
            return AcMainThreadQueue.EnqueueChatCommand(text);

        try
        {
            string trimmed = text.Trim();
            if (trimmed.Length == 0) return false;

            // RynthCore engine commands (/rc ...) are handled before plugin
            // pre-dispatch and before any AC chat routing so they work even
            // when the overlay is hidden and no plugin can eat the line.
            if (RynthCoreChatCommands.TryHandle(trimmed))
                return true;

            // Expand shorthand: /s → /say, /t → /tell, /e → /emote
            trimmed = ExpandShorthand(trimmed);

            // Plugin pre-dispatch: matches AC's natural OutgoingChatDetour
            // behavior — when a user types in the chat bar, plugins
            // (RynthAi /ra, VTank /vt, UBService /ub, MagTools /mt, etc.) get
            // first dibs via OnChatBarEnter and can eat the line. We mirror
            // that here so callers like OnLoginCommandRunner and
            // Host.InvokeChatParser produce identical-to-typed semantics.
            //
            // Skipped when:
            //  - Already inside a plugin pre-dispatch on this thread
            //    (avoids infinite loops on chained Host.InvokeChatParser calls)
            //  - UseDirectChatInput is on (raw simulate mode bypasses this)
            if (!UseDirectChatInput && !_inPluginPreDispatch)
            {
                _inPluginPreDispatch = true;
                try
                {
                    if (PluginManager.DispatchChatBarEnter(trimmed))
                        return true;
                }
                catch (Exception ex)
                {
                    RynthLog.Compat($"ChatDispatch: plugin pre-dispatch threw on '{trimmed}' - {ex.GetType().Name}: {ex.Message}");
                }
                finally { _inPluginPreDispatch = false; }
            }

            // Direct Chat Input mode — everything goes through keyboard sim
            if (UseDirectChatInput)
                return SimulateChatInput(trimmed);

            // /say message
            if (StartsWithCmd(trimmed, "/say "))
                return DispatchSay(trimmed.Substring(5));

            // /emote message
            if (StartsWithCmd(trimmed, "/emote "))
                return DispatchEmote(trimmed.Substring(7));

            // /e message (soul emote — client-side)
            if (StartsWithCmd(trimmed, "/me "))
                return DispatchSoulEmote(trimmed.Substring(4));

            // /tell Name, message
            if (StartsWithCmd(trimmed, "/tell "))
                return DispatchTell(trimmed.Substring(6));

            // /a message (allegiance channel broadcast)
            if (StartsWithCmd(trimmed, "/a "))
                return DispatchChannelBroadcast(1, trimmed.Substring(3));

            // /f message (fellowship — no direct Event_*; use the raw chat path)
            if (StartsWithCmd(trimmed, "/f "))
                return DispatchRaw(trimmed);

            // Bare text → say
            if (!trimmed.StartsWith("/", StringComparison.Ordinal))
                return DispatchSay(trimmed);

            // Everything else (arbitrary slash commands like /smite) → raw chat path
            return DispatchRaw(trimmed);
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"ChatDispatch: error - {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static string ExpandShorthand(string text)
    {
        if (StartsWithCmd(text, "/s ")) return "/say " + text.Substring(3);
        if (StartsWithCmd(text, "/t ")) return "/tell " + text.Substring(3);
        return text;
    }

    private static bool DispatchSay(string message)
    {
        var ps = LegacyPString.Create(message);
        try
        {
            byte result = FnEventTalk(&ps);
            return result != 0;
        }
        finally { ps.Dispose(); }
    }

    private static bool DispatchEmote(string emoteText)
    {
        var ps = LegacyPString.Create(emoteText);
        try
        {
            byte result = FnEventEmote(&ps);
            return result != 0;
        }
        finally { ps.Dispose(); }
    }

    private static bool DispatchSoulEmote(string emoteText)
    {
        var ps = LegacyPString.Create(emoteText);
        try
        {
            byte result = FnEventSoulEmote(&ps);
            return result != 0;
        }
        finally { ps.Dispose(); }
    }

    /// <summary>
    /// Dispatches /tell by parsing "Name, message" and calling Event_TalkDirectByName.
    /// </summary>
    private static bool DispatchTell(string args)
    {
        // Format: "PlayerName, message text"
        int commaIdx = args.IndexOf(',');
        if (commaIdx < 1)
        {
            return DispatchRaw("/tell " + args);
        }

        string targetName = args.Substring(0, commaIdx).Trim();
        string message = args.Substring(commaIdx + 1).TrimStart();

        if (targetName.Length == 0 || message.Length == 0)
            return false;

        var psMsg = LegacyPString.Create(message);
        var psTarget = LegacyPString.Create(targetName);
        try
        {
            byte result = FnEventTalkDirectByName(&psMsg, &psTarget);
            return result != 0;
        }
        finally
        {
            psMsg.Dispose();
            psTarget.Dispose();
        }
    }

    /// <summary>
    /// Dispatches a message to a Turbine chat channel via Event_ChannelBroadcast.
    /// Channel IDs: 1=Allegiance, 2=General, 3=Trade, 4=LFG, 5=Roleplay, 6=Society
    /// </summary>
    private static bool DispatchChannelBroadcast(uint channelId, string message)
    {
        if (message.Length == 0)
            return false;

        var ps = LegacyPString.Create(message);
        try
        {
            byte result = FnEventChannelBroadcast(channelId, &ps);
            return result != 0;
        }
        finally { ps.Dispose(); }
    }

    /// <summary>
    /// Dispatches an arbitrary chat line. Prefers calling AC's outgoing-chat
    /// function directly (deterministic and repeatable — no native chat-bar state to
    /// corrupt); falls back to keystroke simulation only until the chat 'this' has
    /// been captured (typically the very first send of a session, whose successful
    /// submit seeds the capture for every send after it).
    /// </summary>
    private static bool DispatchRaw(string command)
    {
        if (ChatCallbackHooks.TryDispatchDirect(command))
            return true;
        return SimulateChatInput(command);
    }

    /// <summary>
    /// Simulates keyboard input to type and submit a chat command.
    /// Used as fallback for commands without a direct Event_* function.
    /// </summary>
    private static bool SimulateChatInput(string command)
    {
        // Deep-audit finding #18 (2026-06-18): SendToGameWndProc drives
        // CallWindowProcA directly into AC's WndProc — calling that off the
        // window-owning thread is a cross-thread WndProc re-entry. Dispatch()
        // gating (finding #4) already closes the two known off-thread paths
        // into this method, but wrap here too per the fix note ("protect
        // future callers"): RunOnGameThread inlines with zero overhead when
        // already on the game thread (the common case, post-#4) and marshals
        // synchronously otherwise.
        return ImGuiBackend.Win32Backend.RunOnGameThread(() =>
        {
            // Enter → opens chat input. The WM_CHAR '\r' is required here to complete
            // AC's chat-bar open transition.
            IntPtr enterDown = MakeKeyLParam(0x1C, false);
            IntPtr enterUp = MakeKeyLParam(0x1C, true);
            ImGuiBackend.Win32Backend.SendToGameWndProc(WM_KEYDOWN, new IntPtr(VK_RETURN), enterDown);
            ImGuiBackend.Win32Backend.SendToGameWndProc(WM_CHAR, new IntPtr('\r'), enterDown);
            ImGuiBackend.Win32Backend.SendToGameWndProc(WM_KEYUP, new IntPtr(VK_RETURN), enterUp);

            // Type each character
            foreach (char c in command)
                ImGuiBackend.Win32Backend.SendToGameWndProc(WM_CHAR, new IntPtr(c), IntPtr.Zero);

            // Enter → submit. NO trailing WM_CHAR '\r' here: the WM_KEYDOWN submits and
            // closes the bar; a '\r' arriving on the now-closed bar re-opens it (the
            // "lone '\r'" behavior the chat-capture path also guards against), leaving the
            // bar open so the NEXT command's open-Enter submits an empty bar instead and
            // the command is lost — the "works once then stops" symptom.
            ImGuiBackend.Win32Backend.SendToGameWndProc(WM_KEYDOWN, new IntPtr(VK_RETURN), enterDown);
            ImGuiBackend.Win32Backend.SendToGameWndProc(WM_KEYUP, new IntPtr(VK_RETURN), enterUp);

            return true;
        });
    }

    private static IntPtr MakeKeyLParam(byte scanCode, bool keyUp)
    {
        int lParam = 1;
        lParam |= (scanCode << 16);
        if (keyUp)
            lParam |= (1 << 30) | (1 << 31);
        return new IntPtr(lParam);
    }

    private static bool StartsWithCmd(string text, string prefix)
        => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
}
