namespace NetMessageTests;
using System.Buffers.Binary;
using RynthCore.Engine.Net;

// Net/InventoryOrder: the server's item order, read from PlayerDescription, ViewContents and
// InventoryPutObjInContainer. Messages are built the way ACE writes them
// (GameEventPlayerDescription / GameEventViewContents / GameEventItemServerSaysContainId).
internal static unsafe partial class Program
{
    private const uint Player = 0x50000001;

    private static byte[] GameEvent(uint ev, params uint[] payload)
    {
        var b = new byte[16 + payload.Length * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 0xF7B0);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), Player);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), ev);
        for (int i = 0; i < payload.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16 + i * 4), payload[i]);
        return b;
    }

    // junkWords of noise (options, tables, spells...), then the inventory list, then the worn list.
    private static byte[] PlayerDescriptionMsg(int junkWords, uint[] inventory, uint[] types, int worn)
    {
        var w = new List<uint>();
        var rnd = new Random(junkWords + 1);
        for (int i = 0; i < junkWords; i++) w.Add((uint)rnd.Next());
        w.Add((uint)inventory.Length);
        for (int i = 0; i < inventory.Length; i++) { w.Add(inventory[i]); w.Add(types[i]); }
        w.Add((uint)worn);
        for (int i = 0; i < worn; i++) { w.Add(0x80001000u + (uint)i); w.Add(0x200u); w.Add(3); }
        return GameEvent(0x0013, w.ToArray());
    }

    private static string Order(uint container, bool slots) =>
        InventoryOrder.TryGetOrder(container, slots, out uint[] ids)
            ? string.Join(",", ids.Select(i => (i & 0xFFFF).ToString("X")))
            : "unknown";

    // The order file as engines before version 2 wrote it: no player id after the list count.
    private static byte[] OldFormatFile((uint Container, bool Slot, uint[] Ids)[] lists)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(0x44524F49u); w.Write(1u); w.Write((uint)lists.Length);
        foreach ((uint c, bool slot, uint[] ids) in lists)
        {
            w.Write(c); w.Write(slot ? (byte)1 : (byte)0); w.Write((uint)ids.Length);
            foreach (uint id in ids) w.Write(id);
        }
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] MessageWithGuid(uint opcode, uint guid)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(b, opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), guid);
        return b;
    }

    private static void InventoryOrderTests()
    {
        Case("inventory order: PlayerDescription gives the main pack, items then pack-slot things");
        InventoryOrder.Reset();
        uint[] ids = { 0x80000001, 0x80000002, 0x80000003, 0x80000004, 0x80000005 };
        uint[] types = { 0, 0, 0, 1, 2 };
        InventoryOrder.Observe(PlayerDescriptionMsg(137, ids, types, 4));
        Eq(Order(Player, false), "1,2,3", "ordinary items in server order");
        Eq(Order(Player, true), "4,5", "side pack and focus, in their own list");

        Case("inventory order: the tail is found whatever noise comes before it");
        for (int junk = 0; junk < 300; junk += 7)
        {
            InventoryOrder.Reset();
            InventoryOrder.Observe(PlayerDescriptionMsg(junk, ids, types, junk % 9));
            Eq(Order(Player, false), "1,2,3", $"junk {junk}");
        }
        InventoryOrder.Reset();
        InventoryOrder.Observe(PlayerDescriptionMsg(20, Array.Empty<uint>(), Array.Empty<uint>(), 0));
        Eq(Order(Player, false), "", "an empty pack is a known, empty list");

        Case("inventory order: a move puts the item at its placement, the others shift");
        InventoryOrder.Reset();
        InventoryOrder.Observe(PlayerDescriptionMsg(50, new uint[] { 0x80000001, 0x80000002, 0x80000003, 0x80000004 }, new uint[] { 0, 0, 0, 0 }, 2));
        InventoryOrder.Observe(GameEvent(0x0022, 0x80000001, Player, 2, 0));
        Eq(Order(Player, false), "2,3,1,4", "first item dropped on the third cell lands there");
        InventoryOrder.Observe(GameEvent(0x0022, 0x80000004, Player, 0, 0));
        Eq(Order(Player, false), "4,2,3,1", "last item to the front");
        InventoryOrder.Observe(GameEvent(0x0022, 0x80000002, Player, 99, 0));
        Eq(Order(Player, false), "4,3,1,2", "a placement past the end is the end");

        Case("inventory order: between containers; an unseen container is not guessed");
        uint pack = 0x80000100;
        InventoryOrder.Observe(GameEvent(0x0196, pack, 2, 0x80000200, 0, 0x80000201, 0));
        Eq(Order(pack, false), "200,201", "ViewContents gives the open pack");
        InventoryOrder.Observe(GameEvent(0x0022, 0x80000004, pack, 1, 0));
        Eq(Order(pack, false), "200,4,201", "moved into the pack at 1");
        Eq(Order(Player, false), "3,1,2", "and gone from the main pack");
        InventoryOrder.Observe(GameEvent(0x0022, 0x80000003, 0x80000999, 0, 0));
        Eq(Order(Player, false), "1,2", "moved to a container never seen: only the removal is applied");
        Eq(Order(0x80000999, false), "unknown", "and that container stays unknown");

        Case("inventory order: removal and deletion");
        InventoryOrder.Observe(MessageWithGuid(0x0024, 0x80000001));
        Eq(Order(Player, false), "2", "InventoryRemoveObject");
        InventoryOrder.Observe(MessageWithGuid(0xF747, 0x80000002));
        Eq(Order(Player, false), "", "DeleteObject");

        Case("inventory order: opened containers are bounded, the player's own is never dropped");
        InventoryOrder.Reset();
        InventoryOrder.Observe(PlayerDescriptionMsg(30, new uint[] { 0x80000001, 0x80000002 }, new uint[] { 0, 0 }, 0));
        for (uint c = 0; c < 200; c++)
            InventoryOrder.Observe(GameEvent(0x0196, 0x90000000 + c, 1, 0xA0000000 + c, 0));
        Eq(Order(Player, false), "1,2", "the player's list survives 200 corpses");
        Eq(Order(0x90000000 + 199, false), "C7", "the newest container is kept");
        Eq(Order(0x90000000, false), "unknown", "the oldest was dropped");
        Check(InventoryOrder.Serialize().Length < 4000, "so the saved file stays small");

        Case("inventory order: saved and restored (it survives an engine reload)");
        InventoryOrder.Reset();
        InventoryOrder.Observe(PlayerDescriptionMsg(60, new uint[] { 0x80000001, 0x80000002, 0x80000003, 0x80000004 }, new uint[] { 0, 0, 0, 1 }, 1));
        InventoryOrder.Observe(GameEvent(0x0022, 0x80000003, Player, 0, 0));
        byte[] saved = InventoryOrder.Serialize();
        string before = Order(Player, false) + "|" + Order(Player, true);
        InventoryOrder.Reset();
        Eq(Order(Player, false), "unknown", "a fresh engine knows nothing");
        Check(InventoryOrder.Restore(saved), "the saved data is accepted");
        Eq(Order(Player, false) + "|" + Order(Player, true), before, "same lists back");
        Check(!InventoryOrder.Restore(new byte[] { 1, 2, 3 }), "garbage is refused");
        Check(!InventoryOrder.Restore(saved.AsSpan(0, saved.Length - 3)), "a truncated file is refused");
        Eq(Order(Player, false) + "|" + Order(Player, true), before, "and a refused file changes nothing");

        Case("inventory order: after a restore the player's lists are still never dropped for room");
        Check(InventoryOrder.Restore(saved), "restored");
        Eq(InventoryOrder.Player, Player, "the file says whose lists they are");
        for (uint c = 0; c < 200; c++)
            InventoryOrder.Observe(GameEvent(0x0196, 0x90000000 + c, 1, 0xA0000000 + c, 0));
        Eq(Order(Player, false) + "|" + Order(Player, true), before, "the main pack and the pack column survive 200 corpses");

        Case("inventory order: a version 1 file (no player in it) still keeps the player's lists");
        byte[] v1 = OldFormatFile(new[] { (Player, false, new uint[] { 0x80000001 }), (Player, true, new uint[] { 0x80000004, 0x80000005 }),
                                          (0x80000004u, false, new uint[] { 0x80000009 }), (0x80000004u, true, Array.Empty<uint>()) });
        Check(InventoryOrder.Restore(v1), "a version 1 file is accepted");
        Eq(InventoryOrder.Player, Player, "the player is the one container in the player id range with a pack column");
        for (uint c = 0; c < 200; c++)
            InventoryOrder.Observe(GameEvent(0x0196, 0x90000000 + c, 1, 0xA0000000 + c, 0));
        Eq(Order(Player, true), "4,5", "its pack column survives 200 corpses");
        Eq(InventoryOrder.PlayerOf(new[] { (Player, true), (Player + 1, true) }), 0u, "two candidates: none guessed");
        Eq(InventoryOrder.PlayerOf(new[] { (0x80000004u, true) }), 0u, "an item id is never the player");

        Case("inventory order: a saved file is this client's when written after it started, however old");
        var start = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
        Check(InventoryOrder.SavedFileIsOurs(start.AddDays(2), start), "written two days ago by this client: used");
        Check(InventoryOrder.SavedFileIsOurs(start.AddSeconds(-20), start), "clock rounding: used");
        Check(!InventoryOrder.SavedFileIsOurs(start.AddHours(-1), start), "written before this client started (a recycled pid): not used");
        Check(InventoryOrder.SavedFileIsOurs(start, null), "start time unknown: used");

        Case("inventory order: a list's hash changes when the list does");
        InventoryOrder.Reset();
        Eq(InventoryOrder.ListHash(Player, true), 0u, "unknown: 0");
        InventoryOrder.Observe(PlayerDescriptionMsg(40, new uint[] { 0x80000001, 0x80000004, 0x80000005 }, new uint[] { 0, 1, 1 }, 0));
        uint h1 = InventoryOrder.ListHash(Player, true);
        Check(h1 != 0, "known: not 0");
        InventoryOrder.Observe(GameEvent(0x0022, 0x80000005, Player, 0, 1));
        Eq(Order(Player, true), "5,4", "a pack moved to the top of the column");
        Check(InventoryOrder.ListHash(Player, true) != h1, "and the hash changed");

        Case("inventory order: side packs sorted into the pack column's order");
        uint[] packs = { Player, 0x80000002, 0x80000003, 0x80000004 };
        Check(InventoryOrder.SortByColumn(packs, 1, x => x, new uint[] { 0x80000004, 0x80000F01, 0x80000002, 0x80000003 }), "a column naming every pack (a focus between)");
        Eq(string.Join(",", packs.Select(x => (x & 0xFFFF).ToString("X"))), "1,4,2,3", "main pack stays first, the top pack next");
        uint[] packs2 = { Player, 0x80000002, 0x80000003 };
        Check(!InventoryOrder.SortByColumn(packs2, 1, x => x, new uint[] { 0x80000003 }), "a pack the column lacks: refused");
        Eq(string.Join(",", packs2.Select(x => (x & 0xFFFF).ToString("X"))), "1,2,3", "and left in id order");

        Case("inventory order: junk never throws and never invents a list");
        InventoryOrder.Reset();
        var rng = new Random(5);
        uint[] events = { 0x13, 0x22, 0x196 };
        for (int i = 0; i < 2000; i++)
        {
            var junk = new byte[rng.Next(0, 80)];
            rng.NextBytes(junk);
            if (junk.Length >= 16 && i % 2 == 0)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(junk, 0xF7B0);
                BinaryPrimitives.WriteUInt32LittleEndian(junk.AsSpan(12), events[i % 3]);
            }
            InventoryOrder.Observe(junk);
        }
        Check(true, "2000 random messages survived");
    }
}
