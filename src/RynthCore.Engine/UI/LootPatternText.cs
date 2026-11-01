// ============================================================================
//  RynthCore.Engine - UI/LootPatternText.cs
//  The Loot Editor's words for a text condition's pattern (2026-11-01, the
//  owner: "People hate regex ... let's add a does not contain"). A VTank text
//  condition (String Value Match, name matches) holds a regex; the editor
//  shows a mode and plain words instead and writes the regex for them:
//
//    contains        Sanguine        Sanguine
//    doesn't contain Sanguine        ^(?!.*Sanguine)
//    starts with     Sanguine        ^Sanguine
//    ends with       Sanguine        Sanguine$
//    is exactly      Sanguine Robe   ^Sanguine Robe$
//    regex           (anything)      as typed
//
//  The words are escaped the way RynthCore.LootSdk's LootItemRules.EscapeText
//  does (regex metacharacters, spaces left alone), so a profile stays a plain
//  VTank .utl. Reading a pattern back (Parse) gives the mode whose shape it
//  has, else Regex. Several words: "Sanguine, Tattered" means either word
//  (contains / doesn't contain only). Matching is case-insensitive (RynthAi's
//  evaluator), so the words are too. Pure: tools\LootPatternTextTests.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text;

namespace RynthCore.Engine.UI;

internal enum LootTextMode { Contains, NotContains, StartsWith, EndsWith, Exactly, Regex }

internal static class LootPatternText
{
    public static readonly LootTextMode[] Modes =
        { LootTextMode.Contains, LootTextMode.NotContains, LootTextMode.StartsWith, LootTextMode.EndsWith, LootTextMode.Exactly, LootTextMode.Regex };

    public static readonly string[] Labels = { "contains", "doesn't contain", "starts with", "ends with", "is exactly", "regex" };

    public static string Label(LootTextMode mode) => Labels[(int)mode];

    /// <summary>What the field's hint says in <paramref name="mode"/>.</summary>
    public static string Hint(LootTextMode mode) => mode switch
    {
        LootTextMode.Contains or LootTextMode.NotContains => "word(s), commas for either",
        LootTextMode.Regex => "pattern (regex)",
        _ => "text",
    };

    /// <summary>The regex for <paramref name="words"/> in <paramref name="mode"/> (Regex: the text as typed).</summary>
    public static string Build(LootTextMode mode, string words)
    {
        if (mode == LootTextMode.Regex) return words;
        string w = words.Trim();
        if (w.Length == 0) return "";   // no words yet: an empty pattern (the editor keeps the mode)
        switch (mode)
        {
            case LootTextMode.Contains:
                return Alternatives(w, wrap: false);
            case LootTextMode.NotContains:
                return w.Length == 0 ? "" : "^(?!.*" + Alternatives(w, wrap: true) + ")";
            case LootTextMode.StartsWith:
                return "^" + Escape(w);
            case LootTextMode.EndsWith:
                return Escape(w) + "$";
            default:   // Exactly
                return "^" + Escape(w) + "$";
        }
    }

    /// <summary>
    /// The mode and words <paramref name="pattern"/> stands for: the first shape it has (doesn't contain,
    /// is exactly, starts with, ends with, contains), with its words unescaped; else Regex and the pattern.
    /// An empty pattern is Contains with no words.
    /// </summary>
    public static (LootTextMode Mode, string Words) Parse(string pattern)
    {
        pattern ??= "";
        if (pattern.Length == 0) return (LootTextMode.Contains, "");
        if (pattern.StartsWith("^(?!.*", StringComparison.Ordinal) && pattern.EndsWith(")", StringComparison.Ordinal))
        {
            string inner = pattern.Substring(6, pattern.Length - 7);
            if (inner.StartsWith("(", StringComparison.Ordinal) && inner.EndsWith(")", StringComparison.Ordinal)
                && TryWords(inner.Substring(1, inner.Length - 2), out string several) && several.Contains(','))
                return (LootTextMode.NotContains, several);
            if (TryUnescape(inner, out string one)) return (LootTextMode.NotContains, one);
        }
        bool start = pattern.StartsWith("^", StringComparison.Ordinal);
        bool end = pattern.EndsWith("$", StringComparison.Ordinal) && !pattern.EndsWith("\\$", StringComparison.Ordinal);
        string mid = pattern.Substring(start ? 1 : 0, pattern.Length - (start ? 1 : 0) - (end ? 1 : 0));
        if (start || end)
        {
            if (TryUnescape(mid, out string text) && text.Length > 0)
                return (start && end ? LootTextMode.Exactly : start ? LootTextMode.StartsWith : LootTextMode.EndsWith, text);
            return (LootTextMode.Regex, pattern);
        }
        if (TryWords(pattern, out string words)) return (LootTextMode.Contains, words);
        return (LootTextMode.Regex, pattern);
    }

    // "a, b" -> "a|b" (each escaped); wrap: "(a|b)" when there are several.
    private static string Alternatives(string words, bool wrap)
    {
        var parts = new List<string>();
        foreach (string raw in words.Split(','))
        {
            string p = raw.Trim();
            if (p.Length > 0) parts.Add(Escape(p));
        }
        if (parts.Count == 0) return "";
        string joined = string.Join("|", parts);
        return wrap && parts.Count > 1 ? "(" + joined + ")" : joined;
    }

    // "a|b" (each a plain escaped text) -> "a, b".
    private static bool TryWords(string pattern, out string words)
    {
        words = "";
        var parts = new List<string>();
        foreach (string piece in SplitTopLevelBars(pattern))
        {
            if (!TryUnescape(piece, out string t) || t.Trim().Length == 0 || t.Contains(',')) return false;
            parts.Add(t);
        }
        if (parts.Count == 0) return false;
        words = string.Join(", ", parts);
        return true;
    }

    // Splits on '|' that isn't escaped.
    private static IEnumerable<string> SplitTopLevelBars(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch == '\\' && i + 1 < s.Length) { sb.Append(ch).Append(s[++i]); continue; }
            if (ch == '|') { yield return sb.ToString(); sb.Clear(); continue; }
            sb.Append(ch);
        }
        yield return sb.ToString();
    }

    private const string Meta = "\\*+?|{}[]()^$.#";

    /// <summary>Text with the regex metacharacters escaped, spaces left alone (as LootSdk's EscapeText).</summary>
    public static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (char ch in text)
        {
            if (Meta.IndexOf(ch) >= 0) sb.Append('\\');
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>The plain text <paramref name="pattern"/> is, when it has no unescaped metacharacter.</summary>
    private static bool TryUnescape(string pattern, out string text)
    {
        var sb = new StringBuilder(pattern.Length);
        for (int i = 0; i < pattern.Length; i++)
        {
            char ch = pattern[i];
            if (ch == '\\')
            {
                if (i + 1 >= pattern.Length || Meta.IndexOf(pattern[i + 1]) < 0) { text = ""; return false; }
                sb.Append(pattern[++i]);
                continue;
            }
            if (Meta.IndexOf(ch) >= 0) { text = ""; return false; }
            sb.Append(ch);
        }
        text = sb.ToString();
        return true;
    }
}
