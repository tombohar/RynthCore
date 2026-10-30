using RynthCore.Engine.ImGuiBackend.Panels;

namespace InventorySelectionTests;

/// <summary>
/// The Inventory panel's multi-selection (InventorySelection.cs) without AC or ImGui: the
/// grid is a list of ids in the order it shows them, like the panel's filtered list.
/// </summary>
internal static class Program
{
    private static int _checks, _failed;
    private static string _case = "";

    // The grid as shown: ids in display order (deliberately not sorted).
    private static readonly uint[] Shown = { 50, 10, 40, 20, 30, 60 };
    private static readonly Func<uint, uint> Id = static id => id;

    private static int Main()
    {
        SingleClick();
        CtrlToggle();
        ShiftRunForward();
        ShiftRunBackward();
        ShiftRunMovesWithAnchorKept();
        CtrlShiftAddsRun();
        ShiftWithoutAnchor();
        ShiftAnchorNotShown();
        SelectAllShown();
        PruneGone();
        ZeroIdsIgnored();

        Console.WriteLine();
        Console.WriteLine($"{_checks - _failed}/{_checks} checks passed.");
        Console.WriteLine(_failed == 0 ? "ALL INVENTORY SELECTION TESTS PASSED." : $"{_failed} FAILED.");
        return _failed == 0 ? 0 : 1;
    }

    private static void SingleClick()
    {
        Case("click: just that item");
        var s = new InventorySelection();
        s.Single(40);
        Check(s.Count == 1 && s.Contains(40), "one picked");
        Check(!s.IsMulti, "one is not a group");
        Check(s.Anchor == 40, "anchor is the clicked item");
        s.Toggle(20);
        s.Single(10);
        Same(s.Ids, new uint[] { 10 }, "a plain click replaces the group");
        s.Clear();
        Check(s.Count == 0 && s.Anchor == 0, "clear empties and forgets the anchor");
    }

    private static void CtrlToggle()
    {
        Case("ctrl+click: add or remove one, picked order kept");
        var s = new InventorySelection();
        s.Single(40);
        Check(s.Toggle(20), "ctrl+click adds");
        Check(s.Toggle(60), "ctrl+click adds another");
        Check(s.IsMulti, "three is a group");
        Same(s.Ids, new uint[] { 40, 20, 60 }, "picked order, not grid order");
        Check(!s.Toggle(20), "ctrl+click on a picked item removes it");
        Same(s.Ids, new uint[] { 40, 60 }, "the others stay in order");
        Check(s.Anchor == 20, "anchor follows the last ctrl+click, even a removal");
        Check(!s.Contains(20), "removed item no longer picked");
    }

    private static void ShiftRunForward()
    {
        Case("shift+click: run forward in grid order");
        var s = new InventorySelection();
        s.Single(10);                                   // index 1
        s.Range(Shown, Id, 30, additive: false);        // index 4
        Same(s.Ids, new uint[] { 10, 40, 20, 30 }, "anchor..clicked, both included, grid order");
        Check(s.Anchor == 10, "anchor stays");
    }

    private static void ShiftRunBackward()
    {
        Case("shift+click: run backward");
        var s = new InventorySelection();
        s.Single(30);                                   // index 4
        s.Range(Shown, Id, 10, additive: false);        // index 1
        Same(s.Ids, new uint[] { 30, 20, 40, 10 }, "anchor first, walking back to the clicked item");
        s.Range(Shown, Id, 30, additive: false);
        Same(s.Ids, new uint[] { 30 }, "shift+click on the anchor itself picks just it");
    }

    private static void ShiftRunMovesWithAnchorKept()
    {
        Case("shift+click again: the run is redrawn from the same anchor");
        var s = new InventorySelection();
        s.Single(40);                                   // index 2
        s.Range(Shown, Id, 60, additive: false);        // 40,20,30,60
        s.Range(Shown, Id, 50, additive: false);        // back past the anchor: 40,10,50
        Same(s.Ids, new uint[] { 40, 10, 50 }, "the old run is replaced, not extended");
        Check(s.Anchor == 40, "anchor unchanged");
    }

    private static void CtrlShiftAddsRun()
    {
        Case("ctrl+shift+click: adds the run to what is picked");
        var s = new InventorySelection();
        s.Single(60);
        s.Toggle(10);                                   // anchor = 10 (index 1)
        s.Range(Shown, Id, 20, additive: true);         // 10,40,20
        Same(s.Ids, new uint[] { 60, 10, 40, 20 }, "earlier picks kept, no duplicates");
        Check(s.Count == 4, "count matches");
    }

    private static void ShiftWithoutAnchor()
    {
        Case("shift+click with nothing picked");
        var s = new InventorySelection();
        s.Range(Shown, Id, 20, additive: false);
        Same(s.Ids, new uint[] { 20 }, "picks just the clicked item");
        Check(s.Anchor == 20, "and it becomes the anchor");
        var t = new InventorySelection();
        t.Range(Shown, Id, 20, additive: true);
        Same(t.Ids, new uint[] { 20 }, "ctrl+shift with nothing picked adds just it");
    }

    private static void ShiftAnchorNotShown()
    {
        Case("shift+click when the anchor is not on show (other pack, new search)");
        var s = new InventorySelection();
        s.Single(999);                                  // picked in another pack
        s.Range(Shown, Id, 30, additive: false);
        Same(s.Ids, new uint[] { 30 }, "plain shift: just the clicked item");
        var t = new InventorySelection();
        t.Single(999);
        t.Range(Shown, Id, 30, additive: true);
        Same(t.Ids, new uint[] { 999, 30 }, "ctrl+shift: the other pack's pick is kept");
        Check(t.Anchor == 30, "the clicked item is the new anchor");
        var u = new InventorySelection();
        u.Single(10);
        u.Range(Shown, Id, 777, additive: false);       // clicked id not in the list (raced a rebuild)
        Same(u.Ids, new uint[] { 777 }, "clicked item not in the list: just it");
    }

    private static void SelectAllShown()
    {
        Case("select all shown");
        var s = new InventorySelection();
        s.Single(999);
        s.SelectAll(Shown, Id);
        Same(s.Ids, Shown, "every shown item, in grid order, nothing else");
        Check(s.Anchor == 50, "anchor is the first shown");
        s.SelectAll(Array.Empty<uint>(), Id);
        Check(s.Count == 0 && s.Anchor == 0, "nothing shown: nothing picked");
    }

    private static void PruneGone()
    {
        Case("prune: items that left the inventory are forgotten");
        var s = new InventorySelection();
        s.SelectAll(Shown, Id);                         // anchor 50
        var still = new HashSet<uint> { 10, 20, 60 };
        Check(s.Prune(still.Contains), "reports a change");
        Same(s.Ids, new uint[] { 10, 20, 60 }, "the rest stay, in order");
        Check(!s.Contains(50) && !s.Contains(40), "gone ones no longer picked");
        Check(s.Anchor == 0, "a gone anchor is forgotten");
        Check(!s.Prune(still.Contains), "nothing gone: no change");
        s.Single(10);
        s.Toggle(20);                                   // anchor 20
        Check(!s.Prune(still.Contains) && s.Anchor == 20, "a kept anchor stays");
        s.Toggle(60);                                   // anchor 60
        still.Remove(10);
        Check(s.Prune(still.Contains) && s.Anchor == 60, "an anchor still there survives others going");
        Same(s.Ids, new uint[] { 20, 60 }, "only the gone one left");
    }

    private static void ZeroIdsIgnored()
    {
        Case("id 0 (no object) is never picked");
        var s = new InventorySelection();
        s.Single(0);
        Check(s.Count == 0 && s.Anchor == 0, "single(0) picks nothing");
        Check(!s.Toggle(0), "toggle(0) refused");
        s.SelectAll(new uint[] { 0, 10, 0 }, Id);
        Same(s.Ids, new uint[] { 10 }, "select all skips 0");
    }

    // ── harness ─────────────────────────────────────────────────────────

    private static void Case(string name)
    {
        _case = name;
        Console.WriteLine("- " + name);
    }

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (ok) return;
        _failed++;
        Console.WriteLine($"  FAIL [{_case}] {what}");
    }

    private static void Same(IReadOnlyList<uint> got, IReadOnlyList<uint> want, string what)
    {
        bool ok = got.Count == want.Count;
        for (int i = 0; ok && i < got.Count; i++) ok = got[i] == want[i];
        Check(ok, what + $" (got [{string.Join(",", got)}], want [{string.Join(",", want)}])");
    }
}
