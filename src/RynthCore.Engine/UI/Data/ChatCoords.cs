// ============================================================================
//  RynthCore.Engine - UI/Data/ChatCoords.cs
//  Finds map coordinates in a chat line ("42.1N, 33.6E", "42.1N 33.6E",
//  "33.6E 42.1N", "0.5S, 101.2W"), for the chat window's right-click menu
//  ("Arrow to ..." / "Go to ..." send them to RynthNav).
//
//  Runs when a line's menu opens or a line is measured (the underlined links), never per
//  frame. No engine dependencies besides ChatSpan: tools/ChatCoordTests compiles this file.
//
//  To keep ordinary chat from turning into coordinates ("give me 5 s, 2 e"),
//  a pair counts when both letters are capitals, or both numbers have a decimal
//  point; and every number must be on the map (0 to 102).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RynthCore.Engine.UI.Data;

internal readonly record struct ChatCoord(int Start, int Length, double Ns, double Ew)
{
    /// <summary>"42.1N, 33.6E".</summary>
    public string Text => Fmt(Ns, 'N', 'S') + ", " + Fmt(Ew, 'E', 'W');

    private static string Fmt(double v, char pos, char neg) =>
        Math.Abs(v).ToString("0.0##", CultureInfo.InvariantCulture) + (v >= 0 ? pos : neg);
}

internal static class ChatCoords
{
    private const double MaxDegrees = 102.0;

    // number, optional space, letter; then a separator; then the other axis.
    private static readonly Regex NsFirst = new(
        @"(?<![\w.])(\d{1,3}(?:\.\d+)?)\s?([NnSs])(?![A-Za-z])[\s,;/]*(\d{1,3}(?:\.\d+)?)\s?([EeWw])(?![\w])",
        RegexOptions.CultureInvariant);
    private static readonly Regex EwFirst = new(
        @"(?<![\w.])(\d{1,3}(?:\.\d+)?)\s?([EeWw])(?![A-Za-z])[\s,;/]*(\d{1,3}(?:\.\d+)?)\s?([NnSs])(?![\w])",
        RegexOptions.CultureInvariant);

    /// <summary>Every coordinate pair in <paramref name="text"/>, in order, without overlaps.</summary>
    public static List<ChatCoord> Find(string? text)
    {
        var found = new List<ChatCoord>();
        if (string.IsNullOrEmpty(text) || text.Length > 4000) return found;
        Collect(NsFirst, text, nsFirst: true, found);
        Collect(EwFirst, text, nsFirst: false, found);
        found.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (int i = found.Count - 1; i > 0; i--)
            if (found[i].Start < found[i - 1].Start + found[i - 1].Length) found.RemoveAt(i);
        return found;
    }

    /// <summary>Cheap pre-check before the coordinate regex: a digit followed (soon) by a compass letter.
    /// Never false for a line <see cref="Find"/> would find something in.</summary>
    public static bool MayHaveCoords(string text)
    {
        for (int i = 0; i + 1 < text.Length; i++)
            if (char.IsDigit(text[i]))
                for (int j = i + 1; j < text.Length && j <= i + 2; j++)
                    if ("NnSsEeWw".IndexOf(text[j]) >= 0) return true;
        return false;
    }

    /// <summary>
    /// The rules' highlight spans with the coordinate links laid over them: a link keeps its own
    /// colour (<paramref name="linkArgb"/>), and any highlight it overlaps is cut around it. The result
    /// is sorted by start and never overlaps (given sorted, non-overlapping spans and coords).
    /// </summary>
    public static ChatSpan[] LinkSpans(ChatSpan[]? spans, List<ChatCoord> coords, uint linkArgb)
    {
        var result = new List<ChatSpan>();
        foreach (ChatSpan s in spans ?? Array.Empty<ChatSpan>())
        {
            int a = s.Start, b = s.Start + s.Length;
            foreach (ChatCoord c in coords)
            {
                int ca = c.Start, cb = c.Start + c.Length;
                if (cb <= a || ca >= b) continue;
                if (ca > a) result.Add(new ChatSpan(a, ca - a, s.Argb));
                a = Math.Max(a, cb);
            }
            if (b > a) result.Add(new ChatSpan(a, b - a, s.Argb));
        }
        foreach (ChatCoord c in coords) result.Add(new ChatSpan(c.Start, c.Length, linkArgb));
        result.Sort((x, y) => x.Start.CompareTo(y.Start));
        return result.ToArray();
    }

    private static void Collect(Regex rx, string text, bool nsFirst, List<ChatCoord> found)
    {
        foreach (Match m in rx.Matches(text))
        {
            string n1 = m.Groups[1].Value, l1 = m.Groups[2].Value, n2 = m.Groups[3].Value, l2 = m.Groups[4].Value;
            bool capitals = char.IsUpper(l1[0]) && char.IsUpper(l2[0]);
            bool decimals = n1.Contains('.') && n2.Contains('.');
            if (!capitals && !decimals) continue;
            if (!double.TryParse(n1, NumberStyles.Float, CultureInfo.InvariantCulture, out double a)) continue;
            if (!double.TryParse(n2, NumberStyles.Float, CultureInfo.InvariantCulture, out double b)) continue;
            if (a > MaxDegrees || b > MaxDegrees) continue;
            double v1 = "SsWw".IndexOf(l1[0]) >= 0 ? -a : a;
            double v2 = "SsWw".IndexOf(l2[0]) >= 0 ? -b : b;
            found.Add(nsFirst ? new ChatCoord(m.Index, m.Length, v1, v2) : new ChatCoord(m.Index, m.Length, v2, v1));
        }
    }
}
