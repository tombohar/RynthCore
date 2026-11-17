using System;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// Which chat lines without a leading '/' still belong to AC's own chat parser, not to plain
/// speech (Event_Talk). AC's help text (acclient.exe, "@help emote") lists them: the text
/// emote "@emote text" and its short forms "@e", "@em", "; text" and ": text", the list
/// "@emotes", and the action emotes written between asterisks ("*wave*", "*Warm Hands*"),
/// which AC turns into an animation. Sent as speech, they came out as the words themselves
/// (2026-11-16 report: no emotes from the RynthChat window). Pure: no AC, no engine state.
/// </summary>
internal static class ChatLineRoute
{
    private static readonly string[] AtEmoteCommands = ["@emotes", "@emote", "@em", "@e"];

    /// <summary>True when <paramref name="trimmed"/> (a trimmed line, not starting with '/')
    /// is one AC's chat bar treats as an emote, so it goes to AC's parser as typed.</summary>
    public static bool IsAcEmoteLine(string trimmed)
    {
        if (string.IsNullOrEmpty(trimmed)) return false;
        char first = trimmed[0];

        if (first is ';' or ':')
            return trimmed.Length > 1;

        if (first == '*')
            return trimmed.Length > 2 && trimmed[^1] == '*' && trimmed.AsSpan(1, trimmed.Length - 2).Trim().Length > 0;

        if (first == '@')
        {
            foreach (string cmd in AtEmoteCommands)
            {
                if (!trimmed.StartsWith(cmd, StringComparison.OrdinalIgnoreCase)) continue;
                // The whole word: "@e hello" or "@emote" alone, never "@echo" or "@emu".
                if (trimmed.Length == cmd.Length || char.IsWhiteSpace(trimmed[cmd.Length]))
                    return true;
            }
        }
        return false;
    }
}
