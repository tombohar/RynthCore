using RynthCore.Engine.ImGuiBackend.Hud;

namespace PlateSightTests;

/// <summary>
/// PlateSightRules: does a nameplate or name label show for an object, given what AC's cells say
/// (2026-11-04, report R-20261105-020337-67aa: labels drew, and took clicks, through walls)?
/// </summary>
internal static class Program
{
    // One landblock (0xC6A9: the town of the report) with a shop and its neighbours.
    private const uint Shop = 0xC6A90161;          // the viewer's room (the report's cell): sees outside
    private const uint ShopBack = 0xC6A90162;      // another room of the shop, in its visible list
    private const uint ShopUpstairs = 0xC6A90163;  // in the visible list too
    private const uint NextDoor = 0xC6A90170;      // another building's room: not visible from the shop
    private const uint Street = 0xC6A90029;        // an outdoor land cell
    private const uint Street2 = 0xC6A9002A;
    private const uint Dungeon = 0x01D90105;       // a dungeon room: doesn't see outside
    private const uint DungeonNear = 0x01D90106;
    private const uint DungeonFar = 0x01D90140;

    private static readonly uint[] ShopVisible = { Shop, ShopBack, ShopUpstairs };
    private static readonly uint[] DungeonVisible = { Dungeon, DungeonNear };

    private static int _checks, _failed;
    private static string _case = "";

    private static int Main()
    {
        CellIds();
        Stamps();
        Rects();
        NothingKnownShows();
        OwnRoomsAlwaysShow();
        BothOutdoorsUnchanged();
        IndoorsTheCellList();
        IndoorsTheDrawStamp();
        IndoorsOutdoorObjects();
        DungeonsSeeNothingOutside();
        OutdoorsLookingIntoBuildings();
        CoarseIsTheCellListsOnly();
        TheReport();

        Console.WriteLine();
        Console.WriteLine($"{_checks - _failed}/{_checks} checks passed.");
        Console.WriteLine(_failed == 0 ? "ALL PLATE SIGHT TESTS PASSED." : $"{_failed} FAILED.");
        return _failed == 0 ? 0 : 1;
    }

    private static SightQuery InShop(uint obj, bool onScreen = true) => new()
    {
        ViewerCell = Shop, PlayerCell = Shop, ViewerSeesOutside = SightFact.Yes,
        ViewerVisible = ShopVisible, VisibleKnown = true, ObjectCell = obj, OnScreen = onScreen,
    };

    private static SightQuery InDungeon(uint obj, bool onScreen = true) => new()
    {
        ViewerCell = Dungeon, PlayerCell = Dungeon, ViewerSeesOutside = SightFact.No,
        ViewerVisible = DungeonVisible, VisibleKnown = true, ObjectCell = obj, OnScreen = onScreen,
    };

    private static SightQuery OnStreet(uint obj, bool onScreen = true) => new()
    {
        ViewerCell = Street, PlayerCell = Street, ViewerSeesOutside = SightFact.Yes,
        ViewerVisible = ReadOnlySpan<uint>.Empty, VisibleKnown = true, ObjectCell = obj, OnScreen = onScreen,
    };

    private static void CellIds()
    {
        Case("cell ids: land cells are outdoors, 0x0100 and up are EnvCells");
        Check(!PlateSightRules.IsIndoor(Street) && !PlateSightRules.IsIndoor(0xC6A90001) && !PlateSightRules.IsIndoor(0xC6A90040), "0x0001-0x0040 outdoors");
        Check(!PlateSightRules.IsIndoor(0xC6A900FF), "0x00FF outdoors");
        Check(PlateSightRules.IsIndoor(0xC6A90100) && PlateSightRules.IsIndoor(Shop) && PlateSightRules.IsIndoor(0x01D9FFFD), "0x0100.. indoors");
    }

    private static void Stamps()
    {
        Case("draw stamps: drawn since the sample, wrapping at 2^32");
        Check(PlateSightRules.DrawnSince(101, 100), "one newer");
        Check(PlateSightRules.DrawnSince(5000, 100), "many newer (several DrawCells passes a frame)");
        Check(!PlateSightRules.DrawnSince(100, 100), "the sample itself is not newer");
        Check(!PlateSightRules.DrawnSince(99, 100), "older");
        Check(!PlateSightRules.DrawnSince(0, 100), "never drawn (a new cell starts at 0)");
        Check(PlateSightRules.DrawnSince(0x10, 0xFFFFFFF0), "across the wrap");
        Check(!PlateSightRules.DrawnSince(0xFFFFFFF0, 0x10), "older across the wrap");
    }

    private static void Rects()
    {
        Case("screen rects: overlap, touching, the margin");
        Check(PlateSightRules.Overlaps(10, 10, 20, 20, 15, 15, 30, 30, 0), "overlapping");
        Check(PlateSightRules.Overlaps(10, 10, 20, 20, 20, 0, 30, 30, 0), "touching edges count");
        Check(!PlateSightRules.Overlaps(10, 10, 20, 20, 25, 10, 30, 20, 0), "apart");
        Check(PlateSightRules.Overlaps(10, 10, 20, 20, 25, 10, 30, 20, 6), "apart by less than the margin");
        Check(PlateSightRules.Overlaps(15, 0, 15, 40, 10, 10, 20, 20, 0), "a head-to-feet line through the rect");
    }

    private static void NothingKnownShows()
    {
        Case("nothing known: today's behaviour (the plate shows)");
        var q = new SightQuery { ObjectCell = NextDoor, OnScreen = true, ObjectCellDrawn = SightFact.No };
        Check(PlateSightRules.Show(in q), "no viewer cell");
        q = InShop(0);
        Check(PlateSightRules.Show(in q), "no object cell");
        q = InShop(0x00000161);
        Check(PlateSightRules.Show(in q), "an object cell without a landblock");
        q = InShop(NextDoor);
        q.VisibleKnown = false;
        q.ViewerVisible = ReadOnlySpan<uint>.Empty;
        Check(PlateSightRules.Show(in q), "visible list unreadable, no stamp: shows");
        q = InShop(Street);
        q.ViewerSeesOutside = SightFact.Unknown;
        Check(PlateSightRules.Show(in q), "viewer's sees-outside unreadable: an outdoor object shows");
    }

    private static void OwnRoomsAlwaysShow()
    {
        Case("the viewer's and the player's own cells always show");
        SightQuery q = InShop(Shop);
        q.ObjectCellDrawn = SightFact.No;   // even a stale "not drawn"
        Check(PlateSightRules.Show(in q), "the viewer's room");
        q = InShop(NextDoor);
        q.PlayerCell = NextDoor;            // third person: the camera is in another room
        q.ObjectCellDrawn = SightFact.No;
        Check(PlateSightRules.Show(in q), "the player's room");
        q = InDungeon(Dungeon, onScreen: false);
        Check(PlateSightRules.Show(in q), "the dungeon room you stand in");
    }

    private static void BothOutdoorsUnchanged()
    {
        Case("viewer and object outdoors: no change (no terrain or building occlusion)");
        SightQuery q = OnStreet(Street2);
        Check(PlateSightRules.Show(in q), "on screen");
        q = OnStreet(Street2, onScreen: false);
        Check(PlateSightRules.Show(in q), "pinned to the edge");
        q = OnStreet(0xC7A90001);
        Check(PlateSightRules.Show(in q), "the next landblock");
    }

    private static void IndoorsTheCellList()
    {
        Case("indoors, no draw stamp: only the cells in the viewer's visible list");
        SightQuery q = InShop(ShopBack);
        Check(PlateSightRules.Show(in q), "a room in the list");
        q = InShop(ShopUpstairs, onScreen: false);
        Check(PlateSightRules.Show(in q), "a room in the list, off screen");
        q = InShop(NextDoor);
        Check(!PlateSightRules.Show(in q), "another building's room: hidden");
        q = InShop(NextDoor, onScreen: false);
        Check(!PlateSightRules.Show(in q), "another building's room, pinned to the edge: hidden");
        q = InDungeon(DungeonFar);
        Check(!PlateSightRules.Show(in q), "a dungeon room not visible from here: hidden");
        q = InDungeon(DungeonNear);
        Check(PlateSightRules.Show(in q), "a dungeon room visible from here: shows");
    }

    private static void IndoorsTheDrawStamp()
    {
        Case("on screen: AC's draw stamp on the object's room decides");
        SightQuery q = InShop(ShopBack);
        q.ObjectCellDrawn = SightFact.No;
        Check(!PlateSightRules.Show(in q), "in the list but AC didn't draw it (behind the wall): hidden");
        q.ObjectCellDrawn = SightFact.Yes;
        Check(PlateSightRules.Show(in q), "drawn: shows");
        q = InShop(NextDoor);
        q.ObjectCellDrawn = SightFact.Yes;
        Check(PlateSightRules.Show(in q), "drawn wins over the list (seen out the door and through another)");
        q = InDungeon(DungeonNear);
        q.ObjectCellDrawn = SightFact.No;
        Check(!PlateSightRules.Show(in q), "the next dungeon room around the corner: hidden");

        Case("off screen (pinned by Keep on screen): the stamp is ignored, the list decides");
        q = InShop(ShopBack, onScreen: false);
        q.ObjectCellDrawn = SightFact.No;   // AC skips rooms outside the view
        Check(PlateSightRules.Show(in q), "a visible room off to the side keeps its pinned plate");
        q = InShop(NextDoor, onScreen: false);
        q.ObjectCellDrawn = SightFact.Yes;
        Check(!PlateSightRules.Show(in q), "off screen, not in the list: hidden");
    }

    private static void IndoorsOutdoorObjects()
    {
        Case("from inside a building that sees outside: outdoor objects need a drawn doorway on screen");
        SightQuery q = InShop(Street);
        q.InDoorway = SightFact.Yes;
        Check(PlateSightRules.Show(in q), "seen through the door: shows");
        q.InDoorway = SightFact.No;
        Check(!PlateSightRules.Show(in q), "behind the shop's wall: hidden");
        q.InDoorway = SightFact.Unknown;
        Check(PlateSightRules.Show(in q), "doorways not worked out: shows");
        q = InShop(Street, onScreen: false);
        q.InDoorway = SightFact.No;
        Check(PlateSightRules.Show(in q), "pinned to the edge: the cell rule (sees outside) shows it");
    }

    private static void DungeonsSeeNothingOutside()
    {
        Case("from a cell that doesn't see outside, nothing outdoors shows");
        SightQuery q = InDungeon(Street);
        Check(!PlateSightRules.Show(in q), "on screen");
        q.InDoorway = SightFact.Yes;
        Check(!PlateSightRules.Show(in q), "even with a (stale) doorway answer");
        q = InDungeon(Street, onScreen: false);
        Check(!PlateSightRules.Show(in q), "pinned to the edge");
    }

    private static void OutdoorsLookingIntoBuildings()
    {
        Case("from outdoors, objects inside buildings and dungeons");
        SightQuery q = OnStreet(Shop);
        q.ObjectCellDrawn = SightFact.Yes;
        Check(PlateSightRules.Show(in q), "the shop's room drawn through its doorway: shows");
        q.ObjectCellDrawn = SightFact.No;
        Check(!PlateSightRules.Show(in q), "not drawn (door out of view, walls): hidden");
        q = OnStreet(Shop, onScreen: false);
        q.ObjectSeesOutside = SightFact.Yes;
        Check(PlateSightRules.Show(in q), "off screen, a room that sees outside: shows");
        q.ObjectSeesOutside = SightFact.No;
        Check(!PlateSightRules.Show(in q), "off screen, a sealed room / dungeon: hidden");
        q.ObjectSeesOutside = SightFact.Unknown;
        Check(PlateSightRules.Show(in q), "off screen, unknown: shows");
        q = OnStreet(Dungeon);
        q.ObjectSeesOutside = SightFact.No;
        Check(!PlateSightRules.Show(in q), "a dungeon room below the town, no stamp: hidden");
    }

    private static void CoarseIsTheCellListsOnly()
    {
        Case("the 4 Hz scan: the cell lists only (no stamp, no doorway)");
        Check(PlateSightRules.ShowCoarse(Shop, Shop, SightFact.Yes, ShopVisible, true, ShopBack), "a visible room");
        Check(!PlateSightRules.ShowCoarse(Shop, Shop, SightFact.Yes, ShopVisible, true, NextDoor), "another building's room");
        Check(PlateSightRules.ShowCoarse(Shop, Shop, SightFact.Yes, ShopVisible, true, Street), "outdoors from a shop that sees outside");
        Check(!PlateSightRules.ShowCoarse(Dungeon, Dungeon, SightFact.No, DungeonVisible, true, Street), "outdoors from a dungeon");
        Check(!PlateSightRules.ShowCoarse(Dungeon, Dungeon, SightFact.No, DungeonVisible, true, DungeonFar), "a dungeon room out of sight");
        Check(PlateSightRules.ShowCoarse(Dungeon, Dungeon, SightFact.No, ReadOnlySpan<uint>.Empty, false, DungeonFar), "list unreadable: shows");
        Check(PlateSightRules.ShowCoarse(Street, Street, SightFact.Yes, ReadOnlySpan<uint>.Empty, true, Shop), "outdoors: a building's room is left to the frame check");
        Check(PlateSightRules.ShowCoarse(0, 0, SightFact.Unknown, ReadOnlySpan<uint>.Empty, false, NextDoor), "no viewer: shows");
    }

    private static void TheReport()
    {
        // R-20261105-020337-67aa: indoors in cell 0xC6A90161 buying components; NPC and monster labels through walls.
        Case("the report: in a town shop, only what can be seen keeps a label");
        SightQuery vendor = InShop(Shop);
        Check(PlateSightRules.Show(in vendor), "the vendor in your room");
        SightQuery backRoom = InShop(ShopBack);
        backRoom.ObjectCellDrawn = SightFact.Yes;
        Check(PlateSightRules.Show(in backRoom), "an NPC in the back room, seen through the inner door");
        SightQuery nextDoor = InShop(NextDoor);
        nextDoor.ObjectCellDrawn = SightFact.No;
        Check(!PlateSightRules.Show(in nextDoor), "an NPC in the building next door");
        SightQuery behindWall = InShop(Street);
        behindWall.InDoorway = SightFact.No;
        Check(!PlateSightRules.Show(in behindWall), "a monster in the street behind the shop's wall");
        SightQuery outTheDoor = InShop(Street2);
        outTheDoor.InDoorway = SightFact.Yes;
        Check(PlateSightRules.Show(in outTheDoor), "an NPC in the street, seen out of the open door");
    }

    private static void Case(string name)
    {
        _case = name;
        Console.WriteLine($"- {name}");
    }

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (ok) return;
        _failed++;
        Console.WriteLine($"  FAIL [{_case}] {what}");
    }
}
