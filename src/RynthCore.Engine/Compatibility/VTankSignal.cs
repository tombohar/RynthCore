// ============================================================================
//  RynthCore.Engine - Compatibility/VTankSignal.cs
//
//  Pure parsing for VTankWatch (no engine state, no AC, no Decal), so the offline
//  tests in tools\VTankWatchTests compile this file directly.
//
//  The signal is VTank's own DOCUMENTED chat commands, as they go through Decal's
//  chat parser (typed in the chat bar, sent by a meta's Chat Command action, by a
//  login command, or by another plugin through InvokeChatParser):
//      /vt start  - "Turns the macro on"
//      /vt stop   - "Turns the macro off"
//  (VirindiPlugins wiki, "Virindi Tank Commands", /vt commands - Actions.)
//  Nothing here reads, loads or inspects VTank itself (its licence forbids reverse
//  engineering; docs/LEGAL_COMPATIBILITY.md).
// ============================================================================

using System;

namespace RynthCore.Engine.Compatibility;

internal static class VTankSignal
{
    /// <summary>The line turns VTank's macro on.</summary>
    public const int Start = 1;
    /// <summary>The line turns VTank's macro off.</summary>
    public const int Stop = -1;
    /// <summary>Not a VTank start/stop command.</summary>
    public const int None = 0;

    /// <summary>
    /// <see cref="Start"/> for "/vt start", <see cref="Stop"/> for "/vt stop" (any case,
    /// any spacing, leading/trailing blanks ignored); <see cref="None"/> for anything else,
    /// including other /vt commands ("/vt startx", "/vt opt set ...") and lines that only
    /// mention the words ("tell X /vt start").
    /// </summary>
    public static int ParseCommand(string? line)
    {
        if (string.IsNullOrEmpty(line))
            return None;
        ReadOnlySpan<char> s = line.AsSpan().Trim();
        if (s.Length < 4 || s[0] != '/' || !s.Slice(1, 2).Equals("vt", StringComparison.OrdinalIgnoreCase))
            return None;
        s = s.Slice(3);
        // "/vt" must be a whole word: "/vtank start" is not VTank's command.
        if (s.Length == 0 || !char.IsWhiteSpace(s[0]))
            return None;
        s = s.TrimStart();
        int end = 0;
        while (end < s.Length && !char.IsWhiteSpace(s[end])) end++;
        ReadOnlySpan<char> verb = s.Slice(0, end);
        // The documented forms take no argument; tolerate trailing words anyway (VTank's own
        // parser decides), but the verb itself must match exactly.
        if (verb.Equals("start", StringComparison.OrdinalIgnoreCase)) return Start;
        if (verb.Equals("stop", StringComparison.OrdinalIgnoreCase)) return Stop;
        return None;
    }

    /// <summary>
    /// A chat-window line that looks like VTank's own output (for the diagnostic log only:
    /// it is never used to decide anything, because VTank documents no on/off chat line).
    /// Lines other players send ("X tells you, ...", channel chat) are excluded.
    /// </summary>
    public static bool LooksLikeVTankChat(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        if (text.Contains(" tells you, ", StringComparison.Ordinal) || text.Contains(" says, ", StringComparison.Ordinal))
            return false;
        return text.Contains("VTank", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Virindi Tank", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("[VT]", StringComparison.OrdinalIgnoreCase);
    }
}
