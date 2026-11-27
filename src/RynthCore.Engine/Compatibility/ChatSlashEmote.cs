using System;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// "/wave" as a shortcut for AC's "*Wave*" (2026-11-26 request). AC has no /wave: its help text
/// says the standard emotes are typed between asterisks, and these are its names for them
/// (acclient.exe). A line that is exactly "/" and one of them, in any case ("/WAVE", "/warmhands"
/// or "/warm hands"), becomes the asterisk form for AC's parser. Anything after the emote
/// ("/wave hello") is not an emote. None of these words is one of AC's own client commands
/// (its command table in acclient.exe, checked 2026-11-26), and the dispatcher asks this only
/// last, after the engine's /rc, every plugin's commands (Lua's too) and its own routes have
/// had the line, so a real command always wins. Pure: no AC, no engine state.
/// </summary>
internal static class ChatSlashEmote
{
    /// <summary>AC's standard emotes, as its help text writes them.</summary>
    public static readonly string[] Names =
    [
        "ShakeFist", "Beckon", "BeSeeingYou", "BlowKiss", "BowDeep", "ClapHands", "Cry", "Laugh", "Nod",
        "Point", "Shrug", "Wave", "Akimbo", "HeartyLaugh", "Salute", "TapFoot", "WaveHigh", "WaveLow",
        "Yawn", "Stretch", "Cringe", "Kneel", "Plead", "Shiver", "Shoo", "Slouch", "Spit", "Surrender",
        "Woah", "Winded", "YMCA", "Eat", "Drink", "Teapot", "Pray", "Mock", "Cheer", "Helper",
        "Warm Hands", "Scratch Head", "Shake Head",
    ];

    /// <summary>
    /// True when <paramref name="trimmed"/> (a trimmed line) is "/" and one emote name, nothing
    /// else; <paramref name="acLine"/> is then AC's form, "*Wave*" or "*Warm Hands*". A two-word
    /// name matches with its words run together or apart ("/warmhands", "/warm hands").
    /// </summary>
    public static bool TryMatch(string? trimmed, out string acLine)
    {
        acLine = "";
        if (trimmed is not { Length: >= 2 } || trimmed[0] != '/') return false;
        ReadOnlySpan<char> word = trimmed.AsSpan(1);
        if (char.IsWhiteSpace(word[0])) return false;   // "/ wave" is not a command word

        foreach (string name in Names)
        {
            if (!SameName(word, name)) continue;
            acLine = "*" + name + "*";
            return true;
        }
        return false;
    }

    /// <summary>
    /// <paramref name="word"/> is <paramref name="name"/>, ignoring case; where the name has a
    /// space the word may have one or more spaces or none.
    /// </summary>
    private static bool SameName(ReadOnlySpan<char> word, string name)
    {
        int w = 0;
        for (int n = 0; n < name.Length; n++)
        {
            char c = name[n];
            if (c == ' ')
            {
                while (w < word.Length && char.IsWhiteSpace(word[w])) w++;
                continue;
            }
            if (w >= word.Length || char.ToUpperInvariant(word[w]) != char.ToUpperInvariant(c)) return false;
            w++;
        }
        return w == word.Length;
    }
}
