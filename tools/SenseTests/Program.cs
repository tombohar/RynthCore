using RynthCore.Engine.UI.Data;

namespace SenseTests;

/// <summary>
/// The Sense panel's pure part (UI/Data/SenseModel.cs): the compact view's layout, the look
/// commands and their replies, and sense.txt.
/// </summary>
internal static class Program
{
    private static int _checks, _failed;

    private static int Main()
    {
        CompactColumns();
        RowsAndSlots();
        MouseOnTheBarePanel();
        ViewSwitch();
        CollapsedLine();
        LookCommands();
        CommandArguments();
        FileRoundTrip();
        OlderAndBadFiles();
        TermRules();
        Matching();

        Console.WriteLine($"{_checks} checks, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (ok) return;
        _failed++;
        Console.WriteLine("FAIL: " + what);
    }

    private static bool Near(float a, float b) => Math.Abs(a - b) < 0.01f;

    // ── layout ─────────────────────────────────────────────────────────

    private static void CompactColumns()
    {
        // Arrow 16 + distance 52 always; the coordinates (104) when the name keeps 110, then the kind (50).
        SenseCompactColumns narrow = SenseLayout.Compact(200);
        Check(!narrow.Location && !narrow.Kind && Near(narrow.NameWidth, 132), $"200 px: arrow, name, distance only (name {narrow.NameWidth})");
        SenseCompactColumns justLoc = SenseLayout.Compact(281);
        Check(!justLoc.Location, "281 px: one short of the coordinates");
        SenseCompactColumns loc = SenseLayout.Compact(282);
        Check(loc.Location && !loc.Kind && Near(loc.NameWidth, 110), $"282 px: the coordinates come in, the name keeps 110 (got {loc.NameWidth})");
        SenseCompactColumns notKind = SenseLayout.Compact(331);
        Check(notKind.Location && !notKind.Kind && Near(notKind.NameWidth, 159), "331 px: coordinates, no kind yet");
        SenseCompactColumns all = SenseLayout.Compact(332);
        Check(all.Location && all.Kind && Near(all.NameWidth, 110), $"332 px: coordinates and kind (name {all.NameWidth})");
        SenseCompactColumns big = SenseLayout.Compact(564, scale: 2f);
        Check(big.Location && !big.Kind, "the widths grow with the text size: 564 px at 200% is 282 at 100%");
        SenseCompactColumns tiny = SenseLayout.Compact(50);
        Check(!tiny.Location && !tiny.Kind && Near(tiny.NameWidth, 24), "a very narrow window keeps a sliver of name");
        Check(SenseLayout.CompactRowHeight < SenseLayout.FullRowHeight, "compact rows are tighter than the full view's");
    }

    private static void RowsAndSlots()
    {
        Check(SenseLayout.CompactFit(160, 16, 5) == (5, false), "five rows in a 160 px window: all of them");
        Check(SenseLayout.CompactFit(160, 16, 12) == (9, true), "twelve rows in ten slots: nine and a +N more line");
        Check(SenseLayout.CompactFit(10, 16, 3) == (0, true), "a window shorter than a row: just the +N more line");
        Check(SenseLayout.CompactFit(100, 16, 0) == (0, false), "no rows: nothing");
        Check(SenseLayout.MoreText(3) == "+3 more" && SenseLayout.MoreText(-1) == "+0 more", "the more line's text");
        Check(SenseLayout.RowsThatFit(160, 16, 12) == 10 && SenseLayout.RowsThatFit(160, 0, 4) == 4, "rows that fit");

        Check(SenseLayout.RowAt(105, 100, 16, 3) == 0, "the first row");
        Check(SenseLayout.RowAt(131.9f, 100, 16, 3) == 1, "the second row's last pixel");
        Check(SenseLayout.RowAt(148, 100, 16, 3) == -1, "just below the last row: none");
        Check(SenseLayout.RowAt(99, 100, 16, 3) == -1 && SenseLayout.RowAt(105, 100, 16, 0) == -1, "above, or no rows: none");
    }

    private static void MouseOnTheBarePanel()
    {
        // Drew (0,0)-(200,48); the buttons beside the first line end at x 240, y 16.
        Check(SenseLayout.IsHot(100, 20, 0, 0, 200, 48, 240, 16, wasHot: false), "over a row: the panel takes the mouse");
        Check(!SenseLayout.IsHot(210, 10, 0, 0, 200, 48, 240, 16, wasHot: false), "right of the rows, coming from the game: clicks go through");
        Check(SenseLayout.IsHot(210, 10, 0, 0, 200, 48, 240, 16, wasHot: true), "from a row to the buttons: still the panel's");
        Check(SenseLayout.IsHot(210, 40, 0, 0, 200, 48, 240, 16, wasHot: true), "the strip beside the rows, from the last row toward the buttons");
        Check(!SenseLayout.IsHot(210, 50, 0, 0, 200, 48, 240, 16, wasHot: true), "below the rows: the game's");
        Check(!SenseLayout.IsHot(250, 10, 0, 0, 200, 48, 240, 16, wasHot: true), "past the buttons: the game's");
        Check(!SenseLayout.IsHot(-1, 10, 0, 0, 200, 48, 240, 16, wasHot: true), "left of the panel: the game's");
        Check(!SenseLayout.IsHot(100, 60, 0, 0, 200, 48, 240, 16, wasHot: false), "the empty rest of the window: the game's");

        // The view switch is always drawn (204..240 beside the first line), so it always takes the mouse.
        Check(SenseLayout.TakesMouse(210, 10, 0, 0, 200, 48, 204, 240, 16, wasHot: false), "on the view switch, coming from the game: the panel's");
        Check(!SenseLayout.TakesMouse(202, 10, 0, 0, 200, 48, 204, 240, 16, wasHot: false), "the gap before the switch, coming from the game: the game's");
        Check(!SenseLayout.TakesMouse(210, 20, 0, 0, 200, 48, 204, 240, 16, wasHot: false), "under the switch, coming from the game: the game's");
        Check(!SenseLayout.TakesMouse(241, 10, 0, 0, 200, 48, 204, 240, 16, wasHot: false), "past the switch: the game's");
        Check(SenseLayout.TakesMouse(100, 20, 0, 0, 200, 48, 204, 240, 16, wasHot: false), "over a row: still the panel's");
        Check(SenseLayout.TakesMouse(210, 40, 0, 0, 200, 48, 204, 240, 16, wasHot: true), "the strip toward the switch once hot: still the panel's");
        Check(!SenseLayout.TakesMouse(210, -1, 0, 0, 200, 48, 204, 240, 16, wasHot: false), "above the panel: the game's");
    }

    private static void ViewSwitch()
    {
        // Every look offers the two others, so the full / compact / collapsed switch is always one click away.
        foreach (SenseLayoutMode mode in new[] { SenseLayoutMode.Full, SenseLayoutMode.Compact, SenseLayoutMode.Collapsed })
        {
            (SenseLayoutMode a, SenseLayoutMode b) = SenseLayout.OtherViews(mode);
            Check(a != mode && b != mode && a != b, $"{SenseLayout.LayoutName(mode)}: the switch offers the other two looks");
        }
        Check(SenseLayout.OtherViews(SenseLayoutMode.Full) == (SenseLayoutMode.Compact, SenseLayoutMode.Collapsed), "full: compact, then collapsed");
        Check(SenseLayout.OtherViews(SenseLayoutMode.Compact) == (SenseLayoutMode.Collapsed, SenseLayoutMode.Full), "compact: collapse first (the caret up), then full");
        Check(SenseLayout.OtherViews(SenseLayoutMode.Collapsed) == (SenseLayoutMode.Compact, SenseLayoutMode.Full), "collapsed: open to compact first (the caret down), then full");
        // Each button's tooltip names the command that does the same, and that command is valid.
        foreach (SenseLayoutMode to in new[] { SenseLayoutMode.Full, SenseLayoutMode.Compact, SenseLayoutMode.Collapsed })
        {
            string tip = SenseLayout.SwitchTip(to);
            string cmd = "view " + SenseLayout.LayoutName(to);
            Check(tip.Contains("/rc sense " + cmd), $"the {SenseLayout.LayoutName(to)} button's tip names /rc sense {cmd}");
            Check(SenseLayout.IsValidArgs(cmd) && SenseLayout.Apply(cmd, SenseLayoutMode.Full, true).Next == to, $"/rc sense {cmd} is the button's look");
        }
    }

    private static void CollapsedLine()
    {
        Check(SenseLayout.CollapsedText(3) == "Sense: 3", "collapsed: Sense: N");
        Check(SenseLayout.CollapsedText(0) == "Sense: 0" && SenseLayout.CollapsedText(-2) == "Sense: 0", "none (never negative)");
    }

    // ── commands ───────────────────────────────────────────────────────

    private static void LookCommands()
    {
        var (full, compact, collapsed) = (SenseLayoutMode.Full, SenseLayoutMode.Compact, SenseLayoutMode.Collapsed);

        SenseLayoutAnswer a = SenseLayout.Apply("compact on", full, panelOpen: true);
        Check(a.Ok && a.Next == compact && a.Reply.StartsWith("Sense: now the compact view", StringComparison.Ordinal) && a.Reply.EndsWith("panel.", StringComparison.Ordinal),
            "compact on from full: compact, and says so: " + a.Reply);
        a = SenseLayout.Apply("compact on", compact, true);
        Check(a.Ok && a.Next == compact && a.Reply.StartsWith("Sense is already in the compact view", StringComparison.Ordinal), "compact on twice: already: " + a.Reply);
        a = SenseLayout.Apply("compact on", collapsed, true);
        Check(a.Ok && a.Next == collapsed && a.Reply.StartsWith("Sense is already in the collapsed view", StringComparison.Ordinal), "compact on while collapsed: stays collapsed (it is the small look)");
        Check(SenseLayout.Apply("compact off", compact, true).Next == full && SenseLayout.Apply("compact off", collapsed, true).Next == full,
            "compact off: the full panel, from compact or collapsed");
        a = SenseLayout.Apply("compact off", compact, true);
        Check(a.Reply.StartsWith("Sense: now the full view", StringComparison.Ordinal), "compact off says the full view: " + a.Reply);

        Check(SenseLayout.Apply("collapse on", full, true).Next == collapsed, "collapse on from full: collapsed");
        Check(SenseLayout.Apply("collapse on", compact, true).Next == collapsed, "collapse on from compact: collapsed");
        Check(SenseLayout.Apply("collapse off", collapsed, true).Next == compact, "collapse off: the matches (compact)");
        a = SenseLayout.Apply("collapse off", full, true);
        Check(a.Ok && a.Next == full && a.Reply.StartsWith("Sense is already in the full view", StringComparison.Ordinal), "collapse off on the full panel: stays full");
        Check(SenseLayout.Apply("collapsed on", full, true).Next == collapsed, "collapsed is collapse's other spelling");

        Check(SenseLayout.Apply("view full", collapsed, true).Next == full, "view full");
        Check(SenseLayout.Apply("VIEW Compact", full, true).Next == compact, "any case");
        Check(SenseLayout.Apply("view collapsed", compact, true).Next == collapsed, "view collapsed");

        foreach (string ask in new[] { "compact", "collapse", "view" })
        {
            a = SenseLayout.Apply(ask, compact, true);
            Check(a.Ok && a.Next == compact && a.Reply.StartsWith("Sense is in the compact view", StringComparison.Ordinal), $"'{ask}' alone: the look now ({a.Reply})");
        }

        a = SenseLayout.Apply("compact on", full, panelOpen: false);
        Check(a.Reply.EndsWith("The panel is closed: /rc sense opens it.", StringComparison.Ordinal), "a closed panel: the reply says how to open it");

        foreach (string bad in new[] { "compact maybe", "view huge", "frob", "collapse yes", "" })
        {
            a = SenseLayout.Apply(bad, compact, true);
            Check(!a.Ok && a.Reply == SenseLayout.Usage, $"'{bad}': the usage");
        }
        Check(SenseLayout.Usage.StartsWith("Usage: /rc sense", StringComparison.Ordinal), "the usage line");
    }

    private static void CommandArguments()
    {
        foreach (string ok in new[] { "", "compact", "compact on", "compact off", "collapse on", "COLLAPSED off", "view", "view full", "view compact", "view collapsed" })
            Check(SenseLayout.IsValidArgs(ok), $"'/rc sense {ok}' runs");
        foreach (string bad in new[] { "now", "compact maybe", "compact on off", "view huge", "view full now", "frob on" })
            Check(!SenseLayout.IsValidArgs(bad), $"'/rc sense {bad}' gets the usage");
        Check(SenseLayout.IsLayoutCommand("compact on") && SenseLayout.IsLayoutCommand("view") && !SenseLayout.IsLayoutCommand("frob"), "look commands");
    }

    // ── sense.txt ──────────────────────────────────────────────────────

    private static string[] Lines(string text) => text.Replace("\r\n", "\n").Split('\n');

    private static void FileRoundTrip()
    {
        var terms = new List<SensePattern> { new("Drake", true), new("Mattekar*", false), new("?ire Ant", true) };
        foreach (SenseLayoutMode mode in Enum.GetValues<SenseLayoutMode>())
        {
            string text = SenseFile.Format(terms, alertChat: true, alertBeep: false, mode);
            SenseFileData back = SenseFile.Parse(Lines(text));
            Check(back.Layout == mode, $"the look {mode} comes back");
            Check(back.AlertChat && !back.AlertBeep, "the alerts come back");
            Check(back.Terms.Count == 3 && back.Terms[0].Text == "Drake" && back.Terms[0].Enabled
                  && back.Terms[1].Text == "Mattekar*" && !back.Terms[1].Enabled && back.Terms[1].IsGlob
                  && back.Terms[2].Text == "?ire Ant", "the terms come back, in order, on and off");
        }
        string compact = SenseFile.Format(terms, false, true, SenseLayoutMode.Compact);
        Check(compact.Contains("\nview=compact", StringComparison.Ordinal), "written as view=compact");
        Check(SenseFile.Format(terms, false, false, SenseLayoutMode.Full).Contains("view=full", StringComparison.Ordinal), "and view=full");
        Check(SenseFile.Format(terms, false, false, SenseLayoutMode.Collapsed).Contains("view=collapsed", StringComparison.Ordinal), "and view=collapsed");
    }

    private static void OlderAndBadFiles()
    {
        // A file from before the look: no view line.
        SenseFileData old = SenseFile.Parse(new[]
        {
            "# RynthCore Sense watch list - auto-generated, hand-edits OK. /rc sense in game.",
            "alertChat=0", "alertBeep=1", "term=1|Drake", "term=0|Mattekar",
        });
        Check(old.Layout == SenseLayoutMode.Full && old.AlertBeep && old.Terms.Count == 2, "an older file: the full panel, as before");
        Check(SenseFile.Parse(new[] { "view=huge" }).Layout == SenseLayoutMode.Full, "an unknown look: full");
        Check(SenseFile.Parse(new[] { "view= Compact " }).Layout == SenseLayoutMode.Compact, "the look by hand, any case and spaces");
        Check(SenseFile.Parse(new[] { "view=compact", "view=collapsed" }).Layout == SenseLayoutMode.Collapsed, "the last view line wins");
        Check(SenseFile.Parse(Array.Empty<string>()).Layout == SenseLayoutMode.Full, "no file: full");
        SenseFileData junk = SenseFile.Parse(new[] { "", "   ", "# view=compact", "nonsense", "=x", "unknown=1", "view" });
        Check(junk.Layout == SenseLayoutMode.Full && junk.Terms.Count == 0 && !junk.AlertChat, "comments, blanks and junk are skipped");
    }

    private static void TermRules()
    {
        var lines = new List<string> { "term=drake", "term=1|DRAKE", "term=0|", "term=1|  Golem  ", "term=2|odd" };
        SenseFileData d = SenseFile.Parse(lines);
        Check(d.Terms.Count == 3, $"a duplicate (any case) and an empty term are dropped ({d.Terms.Count})");
        Check(d.Terms[0].Text == "drake" && d.Terms[0].Enabled, "a bare term=name is on");
        Check(d.Terms[1].Text == "Golem", "trimmed");
        Check(d.Terms[2].Text == "2|odd" && d.Terms[2].Enabled, "only 0| and 1| are flags");

        var many = new List<string>();
        for (int i = 0; i < 45; i++) many.Add("term=1|name" + i);
        Check(SenseFile.Parse(many).Terms.Count == SenseFile.MaxTerms, "at most 40 terms");
        string longName = new('x', 100);
        Check(SenseFile.Parse(new[] { "term=1|" + longName }).Terms[0].Text.Length == SenseFile.MaxTermLength, "a long term is cut to 64");
        Check(SenseFile.Clean(" a\r\nb ") == "a  b", "one line");
        Check(SenseFile.IndexOf(new List<SensePattern> { new("Drake", true) }, "dRaKe") == 0, "found by any case");
    }

    private static void Matching()
    {
        Check(new SensePattern("drake", true).Matches("Dragon's Isle Drake"), "contains, any case");
        Check(new SensePattern("Drake*", true).Matches("Drake Hatchling") && !new SensePattern("Drake*", true).Matches("Isle Drake"),
            "with * the whole name must match");
        Check(new SensePattern("?ire Ant", true).Matches("Fire Ant") && !new SensePattern("?ire Ant", true).Matches("Fire Ants"), "? is one letter");
        Check(!new SensePattern("", true).Matches("Anything") && !new SensePattern("x", true).Matches(""), "empty never matches");
    }
}
