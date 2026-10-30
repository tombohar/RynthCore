using RynthCore.Engine.UI;

namespace BarLayoutTests;

// Offline tests for the toolbar's rows. See the csproj.
internal static class Program
{
    private static int _fails, _asserts;
    private static void Check(bool cond, string msg)
    {
        _asserts++;
        if (!cond) { _fails++; Console.WriteLine($"  [FAIL] {msg}"); }
    }
    private static void Eq<T>(T actual, T expected, string msg) =>
        Check(EqualityComparer<T>.Default.Equals(actual, expected), $"{msg}: expected '{expected}', got '{actual}'");

    private static int Main()
    {
        PerRow();
        RowCount();
        Clamps();
        StepMaxPerRow();
        PickerIndexes();
        ParseAndFormat();
        OpenState();
        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? $"PASS ({_asserts} checks)" : $"FAIL ({_fails} of {_asserts} checks failed)");
        return _fails == 0 ? 0 : 1;
    }

    private static void PerRow()
    {
        Console.WriteLine("PerRow");
        Eq(BarLayout.PerRow(12, 1, 0), 12, "one row, no limit: every icon on it (today's bar)");
        Eq(BarLayout.PerRow(12, 2, 0), 6, "12 icons, 2 rows");
        Eq(BarLayout.PerRow(13, 2, 0), 7, "13 icons, 2 rows: ceil");
        Eq(BarLayout.PerRow(13, 3, 0), 5, "13 icons, 3 rows: ceil(13/3)");
        Eq(BarLayout.PerRow(12, 1, 5), 5, "one row capped at 5: the cap adds rows");
        Eq(BarLayout.RowCount(12, BarLayout.PerRow(12, 1, 5)), 3, "12 icons capped at 5 take 3 rows");
        Eq(BarLayout.PerRow(12, 3, 10), 4, "a cap above ceil(icons/rows) changes nothing");
        Eq(BarLayout.PerRow(3, 6, 0), 1, "more rows than icons: one a row");
        Eq(BarLayout.PerRow(0, 2, 0), 1, "no icons: still at least 1 (no divide by zero)");
        Eq(BarLayout.PerRow(12, 0, 0), 12, "rows 0 reads as 1");
        Eq(BarLayout.PerRow(12, 99, 0), 2, "rows 99 reads as 6");
        Eq(BarLayout.PerRow(12, 1, 1), 2, "a limit of 1 reads as 2");
    }

    private static void RowCount()
    {
        Console.WriteLine("RowCount");
        Eq(BarLayout.RowCount(0, 1), 1, "an empty bar is one row");
        Eq(BarLayout.RowCount(12, 6), 2, "12 by 6");
        Eq(BarLayout.RowCount(13, 6), 3, "13 by 6: a short last row");
        Eq(BarLayout.RowCount(5, 0), 5, "perRow 0 reads as 1");
    }

    private static void Clamps()
    {
        Console.WriteLine("Clamps");
        Eq(BarLayout.ClampRows(0), 1, "rows below 1");
        Eq(BarLayout.ClampRows(7), 6, "rows above 6");
        Eq(BarLayout.ClampRows(3), 3, "rows in range");
        Eq(BarLayout.ClampMaxPerRow(0), 0, "0 is no limit");
        Eq(BarLayout.ClampMaxPerRow(-5), 0, "negative is no limit");
        Eq(BarLayout.ClampMaxPerRow(1), 2, "1 becomes 2");
        Eq(BarLayout.ClampMaxPerRow(31), 30, "above 30 becomes 30");
        Eq(BarLayout.ClampMaxPerRow(17), 17, "in range");
    }

    private static void StepMaxPerRow()
    {
        Console.WriteLine("StepMaxPerRow");
        Eq(BarLayout.StepMaxPerRow(0, 1, 12), 0, "+ at no limit stays no limit");
        Eq(BarLayout.StepMaxPerRow(0, -1, 12), 11, "- from no limit caps one below the shown row");
        Eq(BarLayout.StepMaxPerRow(0, -1, 2), 2, "- from no limit never below 2");
        Eq(BarLayout.StepMaxPerRow(0, -1, 50), 30, "- from no limit never above 30");
        Eq(BarLayout.StepMaxPerRow(30, 1, 12), 0, "+ at 30 is no limit");
        Eq(BarLayout.StepMaxPerRow(2, -1, 12), 2, "- stops at 2");
        Eq(BarLayout.StepMaxPerRow(5, 1, 12), 6, "+ one");
        Eq(BarLayout.StepMaxPerRow(5, -1, 12), 4, "- one");
        Eq(BarLayout.StepMaxPerRow(5, 0, 12), 5, "no step");
    }

    private static void PickerIndexes()
    {
        Console.WriteLine("PickerIndexes");
        Eq(BarLayout.RowNames.Length, 6, "rows picker: 1..6");
        Eq(BarLayout.RowNames[0], "1", "rows picker starts at 1");
        Eq(BarLayout.MaxPerRowNames.Length, 30, "limit picker: no limit + 2..30");
        Eq(BarLayout.MaxPerRowNames[0], "No limit", "limit picker starts with no limit");
        Eq(BarLayout.MaxPerRowNames[^1], "30", "limit picker ends at 30");
        for (int max = 0; max <= 30; max++)
        {
            if (max == 1) continue;   // not a value (clamps to 2)
            int index = BarLayout.MaxPerRowToIndex(max);
            Eq(BarLayout.IndexToMaxPerRow(index), max, $"limit {max} round-trips through the picker");
            Eq(BarLayout.MaxPerRowNames[index], BarLayout.DescribeMaxPerRow(max), $"limit {max}'s label");
        }
    }

    private static void ParseAndFormat()
    {
        Console.WriteLine("ParseAndFormat");
        Check(BarLayout.TryParse("2,8", out int r, out int m) && r == 2 && m == 8, "reads 2,8");
        Eq(BarLayout.Format(2, 8), "2,8", "writes 2,8");
        Check(BarLayout.TryParse(BarLayout.Format(6, 0), out r, out m) && r == 6 && m == 0, "round trip 6, no limit");
        Check(BarLayout.TryParse(" 3 , 1 ", out r, out m) && r == 3 && m == 2, "spaces; a limit of 1 clamps to 2");
        Check(BarLayout.TryParse("9,99", out r, out m) && r == 6 && m == 30, "out of range clamps");
        Check(!BarLayout.TryParse(null, out r, out m) && r == 1 && m == 0, "no row (an old file): defaults");
        Check(!BarLayout.TryParse("", out r, out m) && r == 1 && m == 0, "empty: defaults");
        Check(!BarLayout.TryParse("2", out r, out m) && r == 1 && m == 0, "one value: defaults");
        Check(!BarLayout.TryParse("two,8", out r, out m) && r == 1 && m == 0, "not a number: defaults");
        Check(!BarLayout.TryParse("2,8,1", out r, out m) && r == 1 && m == 0, "three values: defaults");
    }

    private static void OpenState()
    {
        Console.WriteLine("OpenState");
        // What ImGuiPanelHost would say: these panels open (case-insensitive, like its set).
        // A popped-out panel is in the set too (pop out never closes it).
        var openSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Chat", "Mini Remote", "script:rynthsort", "Guardian" };
        Func<string, bool> isOpen = openSet.Contains;

        // Which window a one-window button stands for.
        Eq(BarOpenState.KeyFor("Chat", null), "Chat", "an engine panel: its title");
        Eq(BarOpenState.KeyFor("Mini Remote", ""), "Mini Remote", "an empty panel key reads as none");
        Eq(BarOpenState.KeyFor("RynthSort", "script:rynthsort"), "script:rynthsort", "a script window: its panel key, not its title");

        // One-window buttons.
        Check(BarOpenState.IsOpen(BarOpenState.KeyFor("Chat", null), isOpen), "an open panel's button is lit");
        Check(BarOpenState.IsOpen(BarOpenState.KeyFor("Mini Remote", null), isOpen), "the remote open: its button is lit");
        Check(!BarOpenState.IsOpen(BarOpenState.KeyFor("Radar", null), isOpen), "a closed panel's button is not");
        Check(BarOpenState.IsOpen(BarOpenState.KeyFor("RynthSort", "script:rynthsort"), isOpen), "an open script window's button is lit");
        Check(!BarOpenState.IsOpen(BarOpenState.KeyFor("Oracle", "script:oracle"), isOpen), "a closed script window's button is not");
        Check(!BarOpenState.IsOpen("", isOpen), "an empty key is never open");
        Check(BarOpenState.IsOpen("chat", isOpen), "the open check's case rules apply (the host's set ignores case)");

        // Menu buttons: lit while any window they list is open.
        Check(BarOpenState.AnyOpen(new[] { "Bounties", "Guardian" }, isOpen), "ILT: Guardian open lights the ILT button");
        Check(!BarOpenState.AnyOpen(new[] { "Bounties" }, isOpen), "ILT: only Bounties, closed");
        Check(BarOpenState.AnyOpen(new[] { "script:lua/a", "script:rynthsort" }, isOpen), "Scripts: one window open is enough");
        Check(!BarOpenState.AnyOpen(new[] { "script:lua/a", "script:lua/b" }, isOpen), "Scripts: none open");
        Check(!BarOpenState.AnyOpen(Array.Empty<string>(), isOpen), "a menu with nothing listed is not lit");

        // The open check is asked once a key, and the search stops at the first open one.
        int asked = 0;
        Func<string, bool> counting = k => { asked++; return openSet.Contains(k); };
        BarOpenState.AnyOpen(new[] { "Guardian", "Bounties", "Radar" }, counting);
        Eq(asked, 1, "stops at the first open window");
        asked = 0;
        BarOpenState.AnyOpen(new[] { "Radar", "", "Bounties" }, counting);
        Eq(asked, 2, "every key asked once when none is open; an empty key not at all");
    }
}
