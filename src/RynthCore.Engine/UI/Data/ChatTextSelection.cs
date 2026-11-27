// ============================================================================
//  RynthCore.Engine - UI/Data/ChatTextSelection.cs
//  The pure half of selecting text in RynthChat's scrollback (2026-11-26 request:
//  "mouse selection, ctrl + C to copy", "full item id on one text line"). The chat
//  view (ImGui/Panels/ChatView.cs) finds which line and character the mouse is on;
//  this file turns two such ends into the text that is copied, finds the character
//  under an x position in one row, and joins an item's info lines into one line.
//  No ImGui, no engine state: tools/ChatTellTests compiles it in.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text;

namespace RynthCore.Engine.UI.Data;

internal static class ChatTextSelection
{
    /// <summary>Puts the two ends of a selection in reading order (a before b).</summary>
    public static void Order(ref int aLine, ref int aChar, ref int bLine, ref int bChar)
    {
        if (aLine < bLine || (aLine == bLine && aChar <= bChar)) return;
        (aLine, bLine) = (bLine, aLine);
        (aChar, bChar) = (bChar, aChar);
    }

    /// <summary>
    /// <paramref name="index"/> kept inside <paramref name="text"/> and never between the two
    /// halves of a surrogate pair (it moves to the pair's start).
    /// </summary>
    public static int Clamp(string text, int index)
    {
        index = Math.Clamp(index, 0, text.Length);
        if (index > 0 && index < text.Length && char.IsLowSurrogate(text[index]) && char.IsHighSurrogate(text[index - 1]))
            index--;
        return index;
    }

    /// <summary>
    /// The selected part [from, to) of one line. A part that starts at the line's start takes
    /// its timestamp too (<paramref name="timestamp"/>, null when timestamps are off), as a
    /// whole copied line always has ("12:01:33 text").
    /// </summary>
    public static string Part(string text, string? timestamp, int from, int to)
    {
        from = Clamp(text, from);
        to = Clamp(text, to);
        if (to < from) (from, to) = (to, from);
        string part = text.Substring(from, to - from);
        return from == 0 && !string.IsNullOrEmpty(timestamp) ? timestamp + " " + part : part;
    }

    /// <summary>
    /// The character boundary nearest <paramref name="x"/> in the row [start, end) of
    /// <paramref name="text"/>, x measured from the row's left edge. <paramref name="advance"/>
    /// is the drawn width of the character (or surrogate pair) at an index.
    /// </summary>
    public static int CharAtX(string text, int start, int end, float x, Func<int, float> advance)
    {
        end = Math.Min(end, text.Length);
        if (x <= 0f || start >= end) return Math.Clamp(start, 0, text.Length);
        float acc = 0f;
        int k = start;
        while (k < end)
        {
            int step = char.IsHighSurrogate(text[k]) && k + 1 < end && char.IsLowSurrogate(text[k + 1]) ? 2 : 1;
            float w = advance(k);
            if (x < acc + w * 0.5f) return k;
            acc += w;
            k += step;
        }
        return end;
    }

    // ── An item's info lines (RynthAi's item info, the Items tab) ───────────

    /// <summary>RynthChat files the lines that start with this under Items (its ChatClassifier).</summary>
    public const string ItemPrefix = "[RynthAi Item] ";

    /// <summary>The longest run of one item's lines looked at (the pet layout writes up to 5).</summary>
    private const int MaxItemLines = 12;

    public static bool IsItemLine(string text) => text.StartsWith(ItemPrefix, StringComparison.Ordinal);

    /// <summary>One of an item's later lines: indented under its first ("[RynthAi Item]   ...").</summary>
    public static bool IsItemDetail(string text) =>
        IsItemLine(text) && text.Length > ItemPrefix.Length + 1
        && text[ItemPrefix.Length] == ' ' && text[ItemPrefix.Length + 1] == ' ';

    /// <summary>
    /// The lines of the item <paramref name="index"/> belongs to: its first line (the name) and
    /// the indented lines under it. False when that line isn't item info.
    /// </summary>
    public static bool ItemRun(IReadOnlyList<string> texts, int index, out int first, out int last)
    {
        first = last = index;
        if (index < 0 || index >= texts.Count || !IsItemLine(texts[index])) return false;
        while (first > 0 && index - first < MaxItemLines && IsItemDetail(texts[first]) && IsItemLine(texts[first - 1]))
            first--;
        while (last + 1 < texts.Count && last - first < MaxItemLines && IsItemDetail(texts[last + 1]))
            last++;
        return true;
    }

    /// <summary>
    /// An item's info lines as one line, without the "[RynthAi Item]" tags: the parts joined
    /// with ", " (the two spaces the layout puts between parts of a line too).
    /// </summary>
    public static string ItemOneLine(IReadOnlyList<string> texts, int first, int last)
    {
        var sb = new StringBuilder(256);
        for (int i = Math.Max(0, first); i <= last && i < texts.Count; i++)
        {
            string body = texts[i];
            if (IsItemLine(body)) body = body.Substring(ItemPrefix.Length);
            body = body.Trim();
            if (body.Length == 0) continue;
            if (sb.Length > 0) sb.Append(", ");
            AppendCollapsingGaps(sb, body);
        }
        return sb.ToString();
    }

    // "+5% Melee D  Wield 300" -> "+5% Melee D, Wield 300": a run of 2+ spaces is the layout's gap between parts.
    private static void AppendCollapsingGaps(StringBuilder sb, string body)
    {
        int i = 0;
        while (i < body.Length)
        {
            if (body[i] == ' ' && i + 1 < body.Length && body[i + 1] == ' ')
            {
                while (i < body.Length && body[i] == ' ') i++;
                // A part that already ends in a comma keeps just the one.
                if (sb.Length > 0 && sb[^1] == ',') sb.Append(' ');
                else sb.Append(", ");
                continue;
            }
            sb.Append(body[i++]);
        }
    }
}
