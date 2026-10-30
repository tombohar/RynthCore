// ============================================================================
//  RynthCore.Engine - UI/BarLayout.cs
//  The toolbar's rows (ImGuiBar): how many rows the player wants (1-6, default
//  1) and the most icons a row may hold (0 = no limit, the default, else
//  2-30). Icons per row = ceil(icons / rows), capped at the limit, so the cap
//  can add rows beyond the chosen count. Rows fill left to right, top to bottom.
//
//  Pure (no ImGui, no files): the clamps, the per-row count, the menu's steps
//  and the barlayout= row of panel_state.txt (PanelStateStore) that keeps them.
//  A file without that row, or with a row this can't read, gives the defaults.
//  Tests: tools/BarLayoutTests. Any thread.
// ============================================================================

using System;
using System.Globalization;

namespace RynthCore.Engine.UI;

internal static class BarLayout
{
    public const int MinRows = 1, MaxRows = 6, DefaultRows = 1;
    /// <summary>Icons per row: 0 is "no limit"; a limit is MinPerRow..MaxPerRow.</summary>
    public const int NoLimit = 0, MinPerRow = 2, MaxPerRow = 30;

    public static int ClampRows(int rows) => Math.Clamp(rows, MinRows, MaxRows);

    /// <summary>0 or less = no limit; 1 becomes 2 (a one-icon row is just a column of rows); above 30 = 30.</summary>
    public static int ClampMaxPerRow(int max) => max <= NoLimit ? NoLimit : Math.Clamp(max, MinPerRow, MaxPerRow);

    /// <summary>Icons in each row (the last may hold fewer): ceil(icons / rows), capped at the limit. At least 1.</summary>
    public static int PerRow(int icons, int rows, int maxPerRow)
    {
        rows = ClampRows(rows);
        maxPerRow = ClampMaxPerRow(maxPerRow);
        int perRow = Math.Max(1, (Math.Max(0, icons) + rows - 1) / rows);
        return maxPerRow > 0 ? Math.Min(perRow, maxPerRow) : perRow;
    }

    /// <summary>The rows the icons take (at least 1: an empty bar is still its grip row).</summary>
    public static int RowCount(int icons, int perRow) =>
        icons <= 0 ? 1 : (icons + Math.Max(1, perRow) - 1) / Math.Max(1, perRow);

    /// <summary>
    /// The icons-per-row menu's - / + from <paramref name="max"/>. No limit sits above 30: + at 30
    /// is no limit, and - from no limit caps one below what a row shows now (<paramref name="shownPerRow"/>),
    /// so the first click changes the bar. - stops at 2, + stops at no limit.
    /// </summary>
    public static int StepMaxPerRow(int max, int delta, int shownPerRow)
    {
        max = ClampMaxPerRow(max);
        if (delta == 0) return max;
        if (max == NoLimit)
            return delta > 0 ? NoLimit : Math.Clamp(shownPerRow - 1, MinPerRow, MaxPerRow);
        int next = max + delta;
        if (next > MaxPerRow) return NoLimit;
        return Math.Max(MinPerRow, next);
    }

    /// <summary>Settings &gt; Display picker labels: rows "1".."6" (index = rows - 1).</summary>
    public static readonly string[] RowNames = Names(MinRows, MaxRows, null);

    /// <summary>Settings &gt; Display picker labels: "No limit", then "2".."30" (MaxPerRowToIndex).</summary>
    public static readonly string[] MaxPerRowNames = Names(MinPerRow, MaxPerRow, "No limit");

    /// <summary>The MaxPerRowNames index for a limit: 0 = no limit, 1 = 2, ... 29 = 30.</summary>
    public static int MaxPerRowToIndex(int max)
    {
        max = ClampMaxPerRow(max);
        return max == NoLimit ? 0 : max - MinPerRow + 1;
    }

    public static int IndexToMaxPerRow(int index) => index <= 0 ? NoLimit : ClampMaxPerRow(index + MinPerRow - 1);

    private static string[] Names(int from, int to, string? first)
    {
        int lead = first != null ? 1 : 0;
        var names = new string[to - from + 1 + lead];
        if (first != null) names[0] = first;
        for (int i = from; i <= to; i++) names[i - from + lead] = i.ToString(CultureInfo.InvariantCulture);
        return names;
    }

    /// <summary>"No limit" or the number.</summary>
    public static string DescribeMaxPerRow(int max)
    {
        max = ClampMaxPerRow(max);
        return max == NoLimit ? "No limit" : max.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The value of panel_state.txt's barlayout= row: "&lt;rows&gt;,&lt;maxPerRow&gt;".</summary>
    public static string Format(int rows, int maxPerRow) =>
        FormattableString.Invariant($"{ClampRows(rows)},{ClampMaxPerRow(maxPerRow)}");

    /// <summary>Reads a barlayout= value, clamped. False (and the defaults) when it isn't two integers.</summary>
    public static bool TryParse(string? value, out int rows, out int maxPerRow)
    {
        rows = DefaultRows;
        maxPerRow = NoLimit;
        if (string.IsNullOrWhiteSpace(value)) return false;
        string[] parts = value.Split(',');
        if (parts.Length != 2
            || !int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int r)
            || !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int m))
            return false;
        rows = ClampRows(r);
        maxPerRow = ClampMaxPerRow(m);
        return true;
    }
}
