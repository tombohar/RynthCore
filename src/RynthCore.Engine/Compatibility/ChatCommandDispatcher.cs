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
    // CM_Communication::Event_* — all cdecl, all using AC1Legacy::PStringBase<char>
    private static readonly delegate* unmanaged[Cdecl]<LegacyPString*, byte> FnEventTalk =
        (delegate* unmanaged[Cdecl]<LegacyPString*, byte>)0x006A53E0;
    private static readonly delegate* unmanaged[Cdecl]<LegacyPString*, byte> FnEventEmote =
        (delegate* unmanaged[Cdecl]<LegacyPString*, byte>)0x006A4F40;
    private static readonly delegate* unmanaged[Cdecl]<LegacyPString*, byte> FnEventSoulEmote =
        (delegate* unmanaged[Cdecl]<LegacyPString*, byte>)0x006A5320;
    private static readonly delegate* unmanaged[Cdecl]<LegacyPString*, LegacyPString*, byte> FnEventTalkDirectByName =
        (delegate* unmanaged[Cdecl]<LegacyPString*, LegacyPString*, byte>)0x006A55A0;
    private static readonly delegate* unmanaged[Cdecl]<uint, LegacyPString*, byte> FnEventChannelBroadcast =
        (delegate* unmanaged[Cdecl]<uint, LegacyPString*, byte>)0x006A4E50;

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

        private static readonly IntPtr NullBufferVa = new(0x008EF11C);
        private static readonly delegate* unmanaged[Thiscall]<LegacyPString*, byte*, void> Ctor =
            (delegate* unmanaged[Thiscall]<LegacyPString*, byte*, void>)0x0048C3E0;
        private static readonly delegate* unmanaged[Thiscall]<LegacyPString*, void> ClearFn =
            (delegate* unmanaged[Thiscall]<LegacyPString*, void>)0x004AB990;

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
