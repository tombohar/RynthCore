// ============================================================================
//  RynthCore.Engine - UI/Data/SenseModel.cs
//  The Sense panel's pure part (no AC, no ImGui, no files), so tools\SenseTests
//  can compile it in:
//
//    SensePattern     one watch-list term (moved here from SenseData.cs).
//    SenseLayoutMode  how the panel looks: Full (the add row, term chips, status
//                     and the match rows), Compact (only the match rows, tighter,
//                     no frame or background, clicks pass through except on the
//                     rows; item-counter.lua's bare look) and Collapsed (one line,
//                     "Sense: N", that opens back to Compact on a click).
//    SenseFile        sense.txt read and written as text: the terms, the alerts
//                     and the look (view=full|compact|collapsed).
//    SenseLayout      the compact view's layout decisions (which columns fit,
//                     row height, which row the mouse is on) and /rc sense's
//                     look commands (compact on|off, collapse on|off, view ...)
//                     with their replies.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RynthCore.Engine.UI.Data;

/// <summary>One watch-list term. Immutable.</summary>
internal sealed class SensePattern
{
    public SensePattern(string text, bool enabled)
    {
        Text = (text ?? string.Empty).Trim();
        Enabled = enabled;
        IsGlob = Text.IndexOfAny(Wildcards) >= 0;
    }

    private static readonly char[] Wildcards = { '*', '?' };

    public string Text { get; }
    public bool Enabled { get; }
    /// <summary>True when the term has * or ?: the whole name must match it.</summary>
    public bool IsGlob { get; }

    public bool Matches(string name)
    {
        if (Text.Length == 0 || string.IsNullOrEmpty(name)) return false;
        return IsGlob ? Glob(name, Text) : name.Contains(Text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Case-insensitive whole-string match of <paramref name="s"/> against
    /// <paramref name="p"/> (* any run, ? one character). Iterative, backtracking
    /// only to the last *; no allocation.
    /// </summary>
    internal static bool Glob(string s, string p)
    {
        int si = 0, pi = 0, star = -1, mark = 0;
        while (si < s.Length)
        {
            if (pi < p.Length && p[pi] == '*') { star = pi++; mark = si; }
            else if (pi < p.Length && (p[pi] == '?' || Same(p[pi], s[si]))) { si++; pi++; }
            else if (star >= 0) { pi = star + 1; si = ++mark; }
            else return false;
        }
        while (pi < p.Length && p[pi] == '*') pi++;
        return pi == p.Length;
    }

    private static bool Same(char a, char b) => a == b || char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
}

/// <summary>How the Sense panel looks (sense.txt "view").</summary>
internal enum SenseLayoutMode : byte
{
    /// <summary>The whole panel: add row, chips, status, column header, rows. The default.</summary>
    Full,
    /// <summary>Only the match rows, tighter; no frame or background; clicks pass through except on the rows.</summary>
    Compact,
    /// <summary>One line, "Sense: N"; a click opens it to Compact.</summary>
    Collapsed,
}

/// <summary>What sense.txt holds.</summary>
internal sealed class SenseFileData
{
    public List<SensePattern> Terms = new();
    public bool AlertChat;
    public bool AlertBeep;
    public SenseLayoutMode Layout = SenseLayoutMode.Full;
}

/// <summary>sense.txt as text (the store does the file I/O). Pure.</summary>
internal static class SenseFile
{
    public const int MaxTerms = 40;
    public const int MaxTermLength = 64;

    /// <summary>
    /// Reads the lines of sense.txt: "alertChat=1", "alertBeep=0", "view=compact", "term=1|name"
    /// (1 on, 0 off; a bare "term=name" is on). Comments (#), blank lines and unknown keys are
    /// skipped; a duplicate term (any case), an empty one or one past <see cref="MaxTerms"/> is
    /// dropped; an unknown view is Full.
    /// </summary>
    public static SenseFileData Parse(IEnumerable<string> lines)
    {
        var data = new SenseFileData();
        foreach (string raw in lines)
        {
            string line = (raw ?? string.Empty).Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq].Trim();
            string val = line[(eq + 1)..];
            switch (key)
            {
                case "alertChat": data.AlertChat = val.Trim() == "1"; break;
                case "alertBeep": data.AlertBeep = val.Trim() == "1"; break;
                case "view":
                    data.Layout = SenseLayout.TryParseLayout(val, out SenseLayoutMode mode) ? mode : SenseLayoutMode.Full;
                    break;
                case "term":
                {
                    bool on = true;
                    string text = val;
                    int bar = val.IndexOf('|');
                    if (bar is 1 && (val[0] == '0' || val[0] == '1'))
                    {
                        on = val[0] == '1';
                        text = val[2..];
                    }
                    text = Clean(text);
                    if (text.Length > 0 && data.Terms.Count < MaxTerms && IndexOf(data.Terms, text) < 0)
                        data.Terms.Add(new SensePattern(text, on));
                    break;
                }
            }
        }
        return data;
    }

    /// <summary>The file's text (what <see cref="Parse"/> reads back).</summary>
    public static string Format(IReadOnlyList<SensePattern> terms, bool alertChat, bool alertBeep, SenseLayoutMode layout)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# RynthCore Sense watch list - auto-generated, hand-edits OK. /rc sense in game.");
        sb.AppendLine("# term=1|name (1 on, 0 off). No * or ?: the name contains it. With * or ?: the whole name matches.");
        sb.AppendLine("# view=full|compact|collapsed (/rc sense compact on|off, /rc sense collapse on|off).");
        sb.AppendLine(alertChat ? "alertChat=1" : "alertChat=0");
        sb.AppendLine(alertBeep ? "alertBeep=1" : "alertBeep=0");
        sb.Append("view=").AppendLine(SenseLayout.LayoutName(layout));
        foreach (SensePattern t in terms)
            sb.Append("term=").Append(t.Enabled ? '1' : '0').Append('|').AppendLine(t.Text);
        return sb.ToString();
    }

    /// <summary>One line, trimmed, at most <see cref="MaxTermLength"/> characters.</summary>
    public static string Clean(string text)
    {
        text = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length > MaxTermLength ? text[..MaxTermLength] : text;
    }

    /// <summary>The index of the term with this text (any case), or -1.</summary>
    public static int IndexOf(IReadOnlyList<SensePattern> list, string text)
    {
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i].Text, text, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}

/// <summary>The compact view's columns at a width (px): arrow, name and distance always.</summary>
internal readonly record struct SenseCompactColumns(bool Location, bool Kind, float NameWidth);

/// <summary>What one /rc sense look command did.</summary>
internal readonly record struct SenseLayoutAnswer(bool Ok, SenseLayoutMode Next, string Reply);

/// <summary>The compact view's layout and the look commands. Pure.</summary>
internal static class SenseLayout
{
    /// <summary>Row heights (px at 100%): the full view's rows and the compact view's.</summary>
    public const float FullRowHeight = 20f, CompactRowHeight = 16f;

    /// <summary>Compact column widths (px at 100%).</summary>
    public const float CompactArrowWidth = 16f, CompactDistWidth = 52f, CompactLocWidth = 104f, CompactKindWidth = 50f;

    /// <summary>The name keeps at least this much before the coordinates, then the kind, are shown.</summary>
    public const float CompactNameRoom = 110f;

    public const string Usage =
        "Usage: /rc sense [compact on|off | collapse on|off | view full|compact|collapsed] (alone: opens or closes the Sense panel)";

    /// <summary>
    /// Which compact columns fit in <paramref name="width"/> px (<paramref name="scale"/>: the text
    /// size factor the widths grow with): the coordinates when the name still keeps
    /// <see cref="CompactNameRoom"/>, then the kind on the same rule. The name gets what is left.
    /// </summary>
    public static SenseCompactColumns Compact(float width, float scale = 1f)
    {
        float k = scale > 0 ? scale : 1f;
        float fixedW = (CompactArrowWidth + CompactDistWidth) * k;
        float room = CompactNameRoom * k;
        bool loc = width - fixedW - CompactLocWidth * k >= room;
        bool kind = loc && width - fixedW - (CompactLocWidth + CompactKindWidth) * k >= room;
        float name = width - fixedW - (loc ? CompactLocWidth * k : 0) - (kind ? CompactKindWidth * k : 0);
        return new SenseCompactColumns(loc, kind, Math.Max(24f * k, name));
    }

    /// <summary>The collapsed line: "Sense: 3", "Sense: 0".</summary>
    public static string CollapsedText(int matches) =>
        "Sense: " + Math.Max(0, matches).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The match row under the mouse at <paramref name="mouseY"/> (rows start at <paramref name="top"/>,
    /// <paramref name="rowHeight"/> apart, <paramref name="rows"/> of them shown); -1 for none.
    /// </summary>
    public static int RowAt(float mouseY, float top, float rowHeight, int rows)
    {
        if (rows <= 0 || rowHeight <= 0 || mouseY < top) return -1;
        int i = (int)((mouseY - top) / rowHeight);
        return i < rows ? i : -1;
    }

    /// <summary>
    /// Whether the bare panel takes the mouse (else clicks go to the game): over what it drew, from
    /// (<paramref name="x0"/>, <paramref name="y0"/>) to (<paramref name="drawnX1"/>, <paramref name="drawnY1"/>),
    /// or, while it was already hot, anywhere in the strip to the right of that out to the buttons'
    /// far edge (<paramref name="buttonsX1"/>), so the buttons beside the first line can be reached from any row.
    /// </summary>
    public static bool IsHot(float mx, float my, float x0, float y0, float drawnX1, float drawnY1, float buttonsX1, float buttonsY1, bool wasHot)
    {
        if (mx < x0 || my < y0) return false;
        if (mx < drawnX1 && my < drawnY1) return true;
        return wasHot && mx < buttonsX1 && my < Math.Max(drawnY1, buttonsY1);
    }

    /// <summary>
    /// The view switch every look shows (2026-10-31: always there, not only on hover): the two other
    /// looks, in the order their buttons stand. Full: compact, collapsed. Compact: collapsed (the
    /// caret up), full. Collapsed: compact (the caret down), full.
    /// </summary>
    public static (SenseLayoutMode First, SenseLayoutMode Second) OtherViews(SenseLayoutMode current) => current switch
    {
        SenseLayoutMode.Compact => (SenseLayoutMode.Collapsed, SenseLayoutMode.Full),
        SenseLayoutMode.Collapsed => (SenseLayoutMode.Compact, SenseLayoutMode.Full),
        _ => (SenseLayoutMode.Compact, SenseLayoutMode.Collapsed),
    };

    /// <summary>What the view switch's button to <paramref name="to"/> says (its tooltip) and the command it matches.</summary>
    public static string SwitchTip(SenseLayoutMode to) => to switch
    {
        SenseLayoutMode.Compact => "Compact: just the matches, no frame; clicks pass through around them (/rc sense view compact)",
        SenseLayoutMode.Collapsed => "Collapse to one line, \"Sense: N\"; click it to show the matches (/rc sense view collapsed)",
        _ => "The full Sense panel: add names, alerts, move and resize (/rc sense view full)",
    };

    /// <summary>
    /// Whether the bare panel takes the mouse: <see cref="IsHot"/>, or over the view switch beside the
    /// first line (from <paramref name="buttonsX0"/>), which is always drawn and so always clickable.
    /// </summary>
    public static bool TakesMouse(float mx, float my, float x0, float y0, float drawnX1, float drawnY1,
        float buttonsX0, float buttonsX1, float buttonsY1, bool wasHot)
    {
        if (IsHot(mx, my, x0, y0, drawnX1, drawnY1, buttonsX1, buttonsY1, wasHot)) return true;
        return mx >= buttonsX0 && mx < buttonsX1 && my >= y0 && my < buttonsY1;
    }

    /// <summary>
    /// Compact rows that fit in <paramref name="height"/> px: (rows drawn, whether the last slot says
    /// "+N more" instead). At least one slot when there are rows.
    /// </summary>
    public static (int Drawn, bool More) CompactFit(float height, float rowHeight, int rows)
    {
        int slots = RowsThatFit(height, rowHeight, rows);
        return slots < rows ? (slots - 1, true) : (slots, false);
    }

    /// <summary>"+3 more".</summary>
    public static string MoreText(int hidden) => "+" + Math.Max(0, hidden).ToString(CultureInfo.InvariantCulture) + " more";

    /// <summary>How many rows of <paramref name="rowHeight"/> fit in <paramref name="height"/> (at least one when any).</summary>
    public static int RowsThatFit(float height, float rowHeight, int rows)
    {
        if (rows <= 0) return 0;
        if (rowHeight <= 0) return rows;
        return Math.Clamp((int)(height / rowHeight), 1, rows);
    }

    public static string LayoutName(SenseLayoutMode mode) => mode switch
    {
        SenseLayoutMode.Compact => "compact",
        SenseLayoutMode.Collapsed => "collapsed",
        _ => "full",
    };

    /// <summary>"full", "compact" (or "small", "mini"), "collapsed" (or "collapse"), any case.</summary>
    public static bool TryParseLayout(string text, out SenseLayoutMode mode)
    {
        switch ((text ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "full": case "normal": mode = SenseLayoutMode.Full; return true;
            case "compact": case "small": case "mini": mode = SenseLayoutMode.Compact; return true;
            case "collapsed": case "collapse": mode = SenseLayoutMode.Collapsed; return true;
            default: mode = SenseLayoutMode.Full; return false;
        }
    }

    /// <summary>True when <paramref name="args"/> (after "/rc sense ") is a look command.</summary>
    public static bool IsLayoutCommand(string args)
    {
        string first = FirstWord(args, out _);
        return first is "compact" or "collapse" or "collapsed" or "view";
    }

    /// <summary>
    /// The check before /rc sense runs (ChatCommandReplies): nothing (open or close the panel), or a
    /// look command <see cref="Apply"/> takes: compact|collapse [on|off], view [full|compact|collapsed].
    /// </summary>
    public static bool IsValidArgs(string args)
    {
        string a = (args ?? string.Empty).Trim();
        if (a.Length == 0) return true;
        string word = FirstWord(a, out string rest);
        string value = rest.Trim().ToLowerInvariant();
        if (value.Contains(' ')) return false;
        return word switch
        {
            "compact" or "collapse" or "collapsed" => value is "" or "on" or "off",
            "view" => value is "" or "full" or "compact" or "collapsed",
            _ => false,
        };
    }

    /// <summary>
    /// /rc sense compact on|off, collapse on|off, view full|compact|collapsed, from the look now
    /// (<paramref name="current"/>). Alone (compact, collapse, view) each says the look now.
    /// compact off is Full; collapse on is Collapsed, collapse off opens it to Compact (Full stays
    /// Full). <paramref name="panelOpen"/>: the reply says how to open a closed panel.
    /// </summary>
    public static SenseLayoutAnswer Apply(string args, SenseLayoutMode current, bool panelOpen)
    {
        string word = FirstWord(args, out string rest);
        string value = rest.Trim().ToLowerInvariant();
        SenseLayoutMode next = current;
        bool changed;
        switch (word)
        {
            case "compact":
                if (value.Length == 0) return Now(current, panelOpen);
                if (!TryOnOff(value, out bool compactOn)) return Bad();
                next = compactOn ? (current == SenseLayoutMode.Collapsed ? SenseLayoutMode.Collapsed : SenseLayoutMode.Compact)
                                 : SenseLayoutMode.Full;
                break;
            case "collapse":
            case "collapsed":
                if (value.Length == 0) return Now(current, panelOpen);
                if (!TryOnOff(value, out bool collapseOn)) return Bad();
                next = collapseOn ? SenseLayoutMode.Collapsed
                                  : current == SenseLayoutMode.Collapsed ? SenseLayoutMode.Compact : current;
                break;
            case "view":
                if (value.Length == 0) return Now(current, panelOpen);
                if (value is not ("full" or "compact" or "collapsed") || !TryParseLayout(value, out next)) return Bad();
                break;
            default:
                return Bad();
        }
        changed = next != current;
        return new SenseLayoutAnswer(true, next, Describe(next, changed ? "Sense: now the " : "Sense is already in the ", panelOpen));
    }

    /// <summary>
    /// "&lt;head&gt;compact view (just the matches, ...)." plus, when the panel is closed, how to open it.
    /// Heads: "Sense: now the " (changed), "Sense is already in the " (no change), "Sense is in the " (asked).
    /// </summary>
    public static string Describe(SenseLayoutMode mode, string head, bool panelOpen)
    {
        string what = mode switch
        {
            SenseLayoutMode.Compact => "compact view (just the matches; clicks pass through around them). /rc sense compact off for the full panel",
            SenseLayoutMode.Collapsed => "collapsed view (one line, Sense: N). Click it, or /rc sense collapse off, to show the matches",
            _ => "full view. /rc sense compact on for the small one",
        };
        return head + what + (panelOpen ? "." : ". The panel is closed: /rc sense opens it.");
    }

    private static SenseLayoutAnswer Now(SenseLayoutMode current, bool panelOpen) =>
        new(true, current, Describe(current, "Sense is in the ", panelOpen));

    private static SenseLayoutAnswer Bad() => new(false, default, Usage);

    private static bool TryOnOff(string value, out bool on)
    {
        switch (value)
        {
            case "on": on = true; return true;
            case "off": on = false; return true;
            default: on = false; return false;
        }
    }

    private static string FirstWord(string s, out string rest)
    {
        s = (s ?? string.Empty).Trim();
        int sp = s.IndexOf(' ');
        if (sp < 0) { rest = string.Empty; return s.ToLowerInvariant(); }
        rest = s[(sp + 1)..].Trim();
        return s[..sp].ToLowerInvariant();
    }
}
