using RynthCore.Engine.Compatibility;

namespace AcMouseOverTests;

/// <summary>
/// AcMouseOverRules: what AC's UI manager says is under the mouse, and whether a nameplate
/// drawn under AC's windows may take the click there.
/// </summary>
internal static class Program
{
    private const uint SomeElement = 0x1A2B3C40;
    private const int SmartBox = AcMouseOverRules.UIElementTypeSmartBoxWrapper;
    private const int ItemList = 0x10000031;   // the inventory's item lists (UiElementHooks)

    private static int _checks, _failed;
    private static string _case = "";

    private static int Main()
    {
        TheTypeId();
        WorldTakesTheClick();
        AcWindowsKeepTheClick();
        FailOpen();
        NothingUnderTheMouse();

        Console.WriteLine();
        Console.WriteLine($"{_checks - _failed}/{_checks} checks passed.");
        Console.WriteLine(_failed == 0 ? "ALL AC MOUSE-OVER TESTS PASSED." : $"{_failed} FAILED.");
        return _failed == 0 ? 0 : 1;
    }

    private static void TheTypeId()
    {
        Case("the 3D view's type is UIElement_SmartBoxWrapper's (GetUIElementType 0x004E5E80)");
        Check(SmartBox == 0x10000030, "0x10000030");
    }

    private static void WorldTakesTheClick()
    {
        Case("cursor on the 3D world: the plate takes the click");
        AcMouseTarget t = AcMouseOverRules.Classify(true, false, SomeElement, true, SmartBox);
        Check(t == AcMouseTarget.World, $"SmartBox wrapper under the mouse = World (got {t})");
        Check(AcMouseOverRules.UnderUiOverlayMayTakeClick(t), "may take the click");

        // 2026-10-10 client logs: over open ground AC reports gmSmartBoxUI (element 0x1000049A,
        // type 0x10000014), not the wrapper. Classed as a window, it kept every plate's click from the plate.
        Case("cursor on the 3D world as AC reports it in game: gmSmartBoxUI (0x10000014) takes the click");
        Check(AcMouseOverRules.UIElementTypeGmSmartBoxUI == 0x10000014, "gmSmartBoxUI::Register registers 0x10000014");
        AcMouseTarget g = AcMouseOverRules.Classify(true, false, 0x1000049A, true, AcMouseOverRules.UIElementTypeGmSmartBoxUI);
        Check(g == AcMouseTarget.World, $"gmSmartBoxUI under the mouse = World (got {g})");
        Check(AcMouseOverRules.UnderUiOverlayMayTakeClick(g), "gmSmartBoxUI: may take the click");
    }

    private static void AcWindowsKeepTheClick()
    {
        Case("cursor on one of AC's windows: the click stays with AC");
        // 0x10000013 / 0x10000015: neighbours of gmSmartBoxUI's type stay windows.
        foreach (int type in new[] { ItemList, 0x10000001, 0x10000032, 0, -1, SmartBox + 1, 0x10000013, 0x10000015 })
        {
            AcMouseTarget t = AcMouseOverRules.Classify(true, false, SomeElement, true, type);
            Check(t == AcMouseTarget.AcWindow, $"type 0x{type:X8} = AcWindow (got {t})");
            Check(!AcMouseOverRules.UnderUiOverlayMayTakeClick(t), $"type 0x{type:X8}: plate may not take the click");
        }
    }

    private static void FailOpen()
    {
        Case("AC's UI state unreadable: today's behaviour (the plate takes the click)");
        AcMouseTarget noMgr = AcMouseOverRules.Classify(false, false, SomeElement, true, ItemList);
        Check(noMgr == AcMouseTarget.Unknown, $"no manager / unreadable = Unknown (got {noMgr})");
        Check(AcMouseOverRules.UnderUiOverlayMayTakeClick(noMgr), "unknown manager: may take the click");
        AcMouseTarget noType = AcMouseOverRules.Classify(true, false, SomeElement, false, 0);
        Check(noType == AcMouseTarget.Unknown, $"element type unreadable = Unknown (got {noType})");
        Check(AcMouseOverRules.UnderUiOverlayMayTakeClick(noType), "unknown type: may take the click");
        Check(AcMouseOverRules.UnderUiOverlayMayTakeClick(AcMouseTarget.Unknown), "Unknown: may take the click");
    }

    private static void NothingUnderTheMouse()
    {
        Case("AC reports nothing (cursor left the client, or no element took the mouse)");
        AcMouseTarget outside = AcMouseOverRules.Classify(true, true, SomeElement, true, ItemList);
        Check(outside == AcMouseTarget.Nothing, $"+0x2D4 set = Nothing, whatever +0x244 still holds (got {outside})");
        AcMouseTarget none = AcMouseOverRules.Classify(true, false, 0, false, 0);
        Check(none == AcMouseTarget.Nothing, $"+0x244 zero = Nothing (got {none})");
        Check(AcMouseOverRules.UnderUiOverlayMayTakeClick(outside) && AcMouseOverRules.UnderUiOverlayMayTakeClick(none),
              "Nothing: may take the click");
    }

    private static void Case(string name)
    {
        _case = name;
        Console.WriteLine($"-- {name}");
    }

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (ok) return;
        _failed++;
        Console.WriteLine($"   FAIL [{_case}] {what}");
    }
}
