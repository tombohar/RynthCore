// ============================================================================
//  RynthCore.Engine - UI/Data/ChatRuleMatching.cs
//  How a RynthChat rule's pattern is read (2026-10-05; idea from Silentkelpie,
//  rynth PR #1). A rule was always a case-insensitive regex. It can now also be
//  plain text: Contains, Starts with or Ends with, any case, nothing special
//  about . ( * " and the like. Plain text is escaped into a regex here, so the
//  router (ChatData.cs) runs every rule the same way, with the same timeout.
//
//  Backward compatible: a rule saved without a "mode" is a Regex rule and is
//  compiled exactly as before (the pattern untouched, the same options), and a
//  Regex rule is saved without a "mode", so its JSON doesn't change either.
//
//  No engine state here: the rule editor, the settings code and
//  tools/ChatFilterTests all use it.
// ============================================================================

using System;
using System.Text.RegularExpressions;

namespace RynthCore.Engine.UI.Data;

/// <summary>How a rule's pattern is matched against a line's text.</summary>
internal enum ChatMatchMode
{
    /// <summary>A case-insensitive regex (every rule before 2026-10-05).</summary>
    Regex,
    /// <summary>The text anywhere in the line.</summary>
    Contains,
    /// <summary>The line starts with the text.</summary>
    StartsWith,
    /// <summary>The line ends with the text (trailing spaces on either side don't count).</summary>
    EndsWith,
}

internal static class ChatRuleMatching
{
    /// <summary>The options every rule is compiled with (as before match modes).</summary>
    public const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Picker labels, in enum order.</summary>
    public static readonly string[] Labels = { "Regex", "Contains", "Starts with", "Ends with" };

    public static string Label(ChatMatchMode mode) => Labels[(int)mode];

    /// <summary>
    /// The regex a rule runs. Regex mode: the pattern as typed. Plain text: escaped, anchored
    /// for Starts with / Ends with. Null when the rule matches nothing (an empty pattern, or
    /// plain text that is only spaces).
    /// </summary>
    public static string? ToRegex(ChatMatchMode mode, string? pattern)
    {
        pattern ??= "";
        if (mode == ChatMatchMode.Regex) return pattern.Length == 0 ? null : pattern;
        if (pattern.Trim().Length == 0) return null;
        return mode switch
        {
            ChatMatchMode.StartsWith => "^" + Regex.Escape(pattern.TrimStart()),
            ChatMatchMode.EndsWith => Regex.Escape(pattern.TrimEnd()) + @"\s*$",
            _ => Regex.Escape(pattern),
        };
    }

    /// <summary>Compiles a rule; null with <paramref name="error"/> set when the regex is bad,
    /// null with no error when it matches nothing (see <see cref="ToRegex"/>).</summary>
    public static Regex? Compile(ChatMatchMode mode, string? pattern, int timeoutMs, out string? error)
    {
        error = null;
        string? expr = ToRegex(mode, pattern);
        if (expr == null) return null;
        try { return new Regex(expr, Options, TimeSpan.FromMilliseconds(timeoutMs)); }
        catch (ArgumentException ex) { error = ex.Message; return null; }
    }

    /// <summary>
    /// The router's compile (2026-11-07): the rule first as a non-backtracking regex, which runs in
    /// time linear in the line's length whatever the pattern, so it gets no match timeout and can
    /// never be "too slow" (<paramref name="linear"/> true). Every plain-text rule and most regex
    /// rules compile this way. A pattern that needs backtracking (lookarounds, backreferences,
    /// atomic groups, conditionals) compiles as <see cref="Compile"/> does, with the timeout.
    ///
    /// Why: the rules were timed by the clock. On a PC running several game clients a plain-text
    /// rule's first match after a compile, a GC pause or a descheduled pump thread ran past the 15 ms
    /// timeout, and the rule was skipped until edited - "too slow and skipped all the time" (the
    /// owner, 2026-11-07, with eight plain-text rules). Rules recompile whenever the shared chat
    /// settings file is saved by any client.
    /// </summary>
    public static Regex? CompileForRouter(ChatMatchMode mode, string? pattern, int timeoutMs, out string? error, out bool linear)
    {
        error = null;
        linear = false;
        string? expr = ToRegex(mode, pattern);
        if (expr == null) return null;
        try
        {
            var rx = new Regex(expr, Options | RegexOptions.NonBacktracking, Regex.InfiniteMatchTimeout);
            linear = true;
            return rx;
        }
        catch (NotSupportedException) { }   // needs backtracking: the timed compile below
        catch (ArgumentException) { }       // a bad pattern: the compile below reports it as before
        return Compile(mode, pattern, timeoutMs, out error);
    }

    /// <summary>The "mode" a rule's JSON carries: absent (an older file or a Regex rule) is Regex.</summary>
    public static ChatMatchMode Parse(string? json) => (json ?? "").Trim().ToLowerInvariant() switch
    {
        "contains" => ChatMatchMode.Contains,
        "startswith" or "starts" or "starts with" => ChatMatchMode.StartsWith,
        "endswith" or "ends" or "ends with" => ChatMatchMode.EndsWith,
        _ => ChatMatchMode.Regex,
    };

    /// <summary>What a rule saves as "mode": nothing for Regex, so a Regex rule's JSON is as before.</summary>
    public static string? ToJson(ChatMatchMode mode) => mode switch
    {
        ChatMatchMode.Contains => "contains",
        ChatMatchMode.StartsWith => "startswith",
        ChatMatchMode.EndsWith => "endswith",
        _ => null,
    };

    /// <summary>
    /// A rule's When: "" any line, "chan:Combat" one channel tab, "type:3" one ChatMessageType.
    /// Anything else reads as any line. Channel is null and Type -1 when not set.
    /// </summary>
    public static (string? Channel, int Type) ParseWhen(string? raw)
    {
        raw = (raw ?? "").Trim();
        if (raw.StartsWith("chan:", StringComparison.OrdinalIgnoreCase) && raw.Length > 5)
            return (raw[5..], -1);
        if (raw.StartsWith("type:", StringComparison.OrdinalIgnoreCase) && int.TryParse(raw[5..], out int t) && t >= 0)
            return (null, t);
        return (null, -1);
    }

    /// <summary>Does a line from <paramref name="channel"/> with <paramref name="chatType"/> pass the When?</summary>
    public static bool WhenMatches(string? channel, int type, string lineChannel, int chatType)
    {
        if (channel != null) return string.Equals(channel, lineChannel, StringComparison.OrdinalIgnoreCase);
        if (type >= 0) return type == chatType;
        return true;
    }
}
