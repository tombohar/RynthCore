// ============================================================================
//  RynthCore.Engine - UI/Data/ChatPlayerText.cs
//  Which chat lines are text from players (2026-11-22): tells both ways,
//  local says and emotes, fellowship, allegiance, the General / Trade / LFG /
//  Roleplay / Society channels, and a server's network rooms. Not combat,
//  spell words, system, loot or plugin output.
//
//  Two things use it:
//    • the Channels tab shows every player line, not only the channel ones
//      ("Channels shows all player chat" in the chat settings, on by default);
//    • the scrollback keeps player lines longer than the rest (ChatHistory.
//      MergeKeep, "Player chat history"), so a busy hunt's combat spam no
//      longer pushes the tells and fellowship lines out within the hour.
//
//  The RynthChat plugin has the same rule (ChatClassifier.IsPlayerChat in
//  RynthSuite) for its own ring, so the lines it hands the engine after a
//  reload, or after the chat was closed for a while, include them too. Keep
//  the two the same.
//
//  No engine state: tools/ChatFilterTests and tools/ChatHistoryBench compile it.
// ============================================================================

using System;

namespace RynthCore.Engine.UI.Data;

internal static class ChatPlayerText
{
    /// <summary>The tab that shows every player line (when the setting is on).</summary>
    public const string PlayerChatTab = "Channels";

    /// <summary>
    /// True for a line a player typed. <paramref name="channel"/> is the tab RynthChat filed it
    /// under (Chat, Channels, System, Combat, Rynth, Items, Other); <paramref name="text"/> the
    /// line, with or without AC's name links.
    /// </summary>
    public static bool IsPlayerChat(string channel, int chatType, string? text)
    {
        // Plugin output and item info are never chat, whatever type they were written with.
        if (channel is "Rynth" or "Items") return false;
        // Say, tells (in and out), emotes, allegiance, fellowship; every channel line.
        if (channel is "Chat" or "Channels") return true;
        // An admin's tell (AdminTell, 0x1F) lands under System.
        if (chatType == 0x1F) return true;
        // Spell words ("Bob says, \"Zojak Equin\"") are spellcasting: not chat.
        if (channel == "Combat") return false;
        // A room line that arrived as another type: "[Room] Name@world says, \"...\"" (a server's
        // network rooms), "[Patron] Bob says, \"...\"", "You say to ..., \"...\"".
        return LooksLikeSpeech(text);
    }

    /// <summary>
    /// "[Tag] Name says, \"" or "[Tag] Name tells you, \"" (the name may carry AC's link markup),
    /// or your own "You say ..., \"" / "You tell ..., \"". No regex: a few IndexOf calls.
    /// </summary>
    public static bool LooksLikeSpeech(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length < 8) return false;
        int start = 0;
        bool tagged = false;
        if (text[0] == '[')
        {
            int close = text.IndexOf(']', 1);
            if (close < 2 || close > 41 || close + 1 >= text.Length || text[close + 1] != ' ') return false;
            if (text.IndexOf('[', 1, close - 1) >= 0) return false;
            start = close + 2;
            tagged = true;
        }
        int quote = text.IndexOf(", \"", start, StringComparison.Ordinal);
        if (quote < 0 || quote - start > 220) return false;
        string who = text.Substring(start, quote - start);
        if (who.IndexOf('"') >= 0) return false;
        if (who.StartsWith("You say", StringComparison.Ordinal) || who.StartsWith("You tell ", StringComparison.Ordinal)) return true;
        return tagged && (who.Contains(" says", StringComparison.Ordinal) || who.Contains(" tells you", StringComparison.Ordinal));
    }
}
