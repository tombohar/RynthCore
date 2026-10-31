// ============================================================================
//  ObjectsTests - the objects / properties slots (HostApi.Objects.cs) against the replayed
//  session: names, positions, types, flags, every property kind with the CreateObject
//  header fallbacks, ownership, container contents and counts, live ids, identify
//  (request, appraisal data, spell book, creature vitals from a synthetic reply), door
//  motion, server age, ghost removal, the v84 record, container order, wielded items.
// ============================================================================

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using RynthCore.Headless.Host;
using RynthCore.Headless.Net;
using RynthCore.Headless.World;
using RynthCore.PluginSdk;

namespace HeadlessHostTests;

internal static unsafe class ObjectsTests
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Register() => Suites.Add("objects", Run, order: 10);

    private const uint Player = 0x50000001;

    private static Fixture F => Fixture.Shared;
    private static RynthCoreApiNative* Api => F.Api;
    private static WorldState W => F.Session.World;

    private static WorldObject First(string name) => W.Objects.Values.Where(o => o.Name == name).OrderBy(o => o.Id).First();

    // ── the slots, called through the table as plugins do ──────────────────

    private static string? Name(uint id) => Marshal.PtrToStringAnsi(((delegate* unmanaged[Cdecl]<uint, IntPtr>)Api->GetObjectNameFn)(id));

    private static bool Int(uint id, uint stype, out int v)
    {
        int x = 0;
        bool ok = ((delegate* unmanaged[Cdecl]<uint, uint, int*, int>)Api->GetObjectIntPropertyFn)(id, stype, &x) != 0;
        v = x;
        return ok;
    }

    private static int? IntOrNull(uint id, uint stype) => Int(id, stype, out int v) ? v : null;

    private static double? Dbl(uint id, uint stype)
    {
        double x = 0;
        return ((delegate* unmanaged[Cdecl]<uint, uint, double*, int>)Api->GetObjectDoublePropertyFn)(id, stype, &x) != 0 ? x : null;
    }

    private static long? Quad(uint id, uint stype)
    {
        long x = 0;
        return ((delegate* unmanaged[Cdecl]<uint, uint, long*, int>)Api->GetObjectQuadPropertyFn)(id, stype, &x) != 0 ? x : null;
    }

    private static bool? Bool(uint id, uint stype)
    {
        int x = 0;
        return ((delegate* unmanaged[Cdecl]<uint, uint, int*, int>)Api->GetObjectBoolPropertyFn)(id, stype, &x) != 0 ? x != 0 : null;
    }

    private static string? Str(uint id, uint stype) => Marshal.PtrToStringAnsi(((delegate* unmanaged[Cdecl]<uint, uint, IntPtr>)Api->GetObjectStringPropertyFn)(id, stype));

    private static uint? Did(uint id, uint stype)
    {
        uint x = 0;
        return ((delegate* unmanaged[Cdecl]<uint, uint, uint*, int>)Api->GetObjectDataIdPropertyFn)(id, stype, &x) != 0 ? x : null;
    }

    private static uint? Iid(uint id, uint stype)
    {
        uint x = 0;
        return ((delegate* unmanaged[Cdecl]<uint, uint, uint*, int>)Api->GetObjectInstanceIdPropertyFn)(id, stype, &x) != 0 ? x : null;
    }

    private static uint[] Contents(uint container, int max = 512)
    {
        uint[] buf = new uint[max];
        fixed (uint* p = buf)
        {
            int n = ((delegate* unmanaged[Cdecl]<uint, uint*, int, int>)Api->GetContainerContentsFn)(container, p, max);
            return buf.Take(n).ToArray();
        }
    }

    private static int SpellIds(uint id, uint[] buf)
    {
        fixed (uint* p = buf)
            return ((delegate* unmanaged[Cdecl]<uint, uint*, int, int>)Api->GetObjectSpellIdsFn)(id, p, buf.Length);
    }

    private static int Call1(IntPtr fn, uint id) => ((delegate* unmanaged[Cdecl]<uint, int>)fn)(id);

    private static void Run()
    {
        using var _ = F.Context.Enter();
        Identity();
        Positions();
        Properties();
        Ownership();
        Identify();
        Physics();
        Removal();
        Record();
        OrderAndWielded();
    }

    private static void Identity()
    {
        // The captured character's own name, from the server's character list (no "+" admin prefix).
        string listed = F.Session.Characters.FirstOrDefault(c => c.Id == Player).Name?.TrimStart('+') ?? "";
        T.Check(listed.Length > 0, "the character list names the player");
        T.Eq(Name(Player), listed, "GetObjectName: the player");
        WorldObject boots = First("Viamontian Laced Boots");
        T.Eq(Name(boots.Id), "Viamontian Laced Boots", "GetObjectName: an item in the pack");
        T.Check(Name(0) == null && Name(0x7FFFFFF0) == null, "GetObjectName: 0 / unknown id answer null");

        WorldObject sack = First("Sack");
        uint flags = 0;
        var itemType = (delegate* unmanaged[Cdecl]<uint, uint*, int>)Api->GetItemTypeFn;
        T.Check(itemType(sack.Id, &flags) == 1 && flags == 0x200, $"GetItemType: Sack = Container 0x200 (got 0x{flags:X})");
        T.Check(itemType(Player, &flags) == 1 && flags == 0x10, "GetItemType: the player = Creature");
        T.Eq(itemType(0x7FFFFFF0, &flags), 0, "GetItemType: unknown id");
        T.Eq(itemType(sack.Id, null), 0, "GetItemType: null out-pointer");

        var bitfield = (delegate* unmanaged[Cdecl]<uint, uint>)Api->GetObjectBitfieldFn;
        var wcid = (delegate* unmanaged[Cdecl]<uint, uint>)Api->GetObjectWcidFn;
        T.Eq(bitfield(sack.Id), 0x13u, "GetObjectBitfield: Sack (Openable|Inscribable|Requires... 0x13)");
        T.Eq(wcid(sack.Id), 166u, "GetObjectWcid: Sack");
        T.Eq(wcid(0x7FFFFFF0), 0u, "GetObjectWcid: unknown id");

        WorldObject slasher = First("Olthoi Slasher");
        WorldObject shop = First("Shopkeeper Renald the Elder");
        T.Eq(Call1(Api->ObjectIsAttackableFn, slasher.Id), 1, "ObjectIsAttackable: a monster (Attackable bit)");
        T.Eq(Call1(Api->ObjectIsAttackableFn, shop.Id), 0, "ObjectIsAttackable: a vendor NPC");
        T.Eq(Call1(Api->ObjectIsAttackableFn, Player), 0, "ObjectIsAttackable: never the player");
        T.Eq(Call1(Api->ObjectIsAttackableFn, sack.Id), 0, "ObjectIsAttackable: an item");
        T.Eq(Call1(Api->ObjectIsAttackableFn, 0x7FFFFFF0), 0, "ObjectIsAttackable: unknown id");
        T.Eq(Call1(Api->ObjectIsAttackableFn, First("Corpse of Drudge Skulker").Id), 0, "ObjectIsAttackable: a corpse");
        T.Eq(Call1(Api->ObjectIsAttackableFn, First("Trade Note (250,000)").Id), 0, "ObjectIsAttackable: an item with the Attackable bit");
        // Players: only PK vs PK (or PK-lite vs PK-lite).
        static WorldObject Pc(uint id, uint bf) => new(id) { Desc = new RynthCore.Headless.Messages.ObjectCreateData { Id = id, ItemType = 0x10, DescriptionFlags = bf } };
        T.Check(RynthCore.Headless.Host.Objects.ObjectProps.IsAttackable(Pc(2, 0x38), Pc(1, 0x38)), "attackable: PK vs PK");
        T.Check(!RynthCore.Headless.Host.Objects.ObjectProps.IsAttackable(Pc(2, 0x38), Pc(1, 0x18)), "not attackable: PK vs non-PK");
        T.Check(!RynthCore.Headless.Host.Objects.ObjectProps.IsAttackable(Pc(2, 0x18), Pc(1, 0x18)), "not attackable: non-PK players");
        T.Check(RynthCore.Headless.Host.Objects.ObjectProps.IsAttackable(Pc(2, 0x2000018), Pc(1, 0x2000018)), "attackable: PK-lite vs PK-lite");

        // Live ids: every created object, ascending; the total even when the buffer is short.
        int created = W.Objects.Values.Count(o => o.Desc.Id != 0);
        var live = (delegate* unmanaged[Cdecl]<uint*, int, int>)Api->GetLiveObjectIdsFn;
        uint[] ids = new uint[1024];
        int total;
        fixed (uint* p = ids) total = live(p, ids.Length);
        T.Eq(total, created, "GetLiveObjectIds: the count of created objects");
        T.Check(ids.Take(total).Contains(Player) && ids.Take(total).Contains(sack.Id), "GetLiveObjectIds: player and pack items listed");
        T.Check(ids.Take(total).SequenceEqual(ids.Take(total).OrderBy(x => x)), "GetLiveObjectIds: ascending");
        uint* small = stackalloc uint[3];
        T.Eq(live(small, 3), created, "GetLiveObjectIds: the total with a short buffer");
        T.Eq(live(null, 0), created, "GetLiveObjectIds: count only");
    }

    private static void Positions()
    {
        var pos = (delegate* unmanaged[Cdecl]<uint, uint*, float*, float*, float*, int>)Api->GetObjectPositionFn;
        uint cell = 0; float x = 0, y = 0, z = 0;
        WorldObject me = W.PlayerObject!;
        T.Check(pos(Player, &cell, &x, &y, &z) == 1 && cell == me.Position!.Value.Cell && x == me.Position.Value.X && z == me.Position.Value.Z,
            $"GetObjectPosition: the player (cell 0x{cell:X8} {x:F1},{y:F1},{z:F1})");
        WorldObject slasher = First("Olthoi Slasher");
        T.Check(pos(slasher.Id, &cell, &x, &y, &z) == 1 && cell == slasher.Position!.Value.Cell, "GetObjectPosition: a monster");
        cell = 0xDEAD;
        T.Check(pos(First("Viamontian Laced Boots").Id, &cell, &x, &y, &z) == 0 && cell == 0xDEAD, "GetObjectPosition: a pack item has none (out untouched)");
        T.Eq(pos(Player, null, &x, &y, &z), 0, "GetObjectPosition: null out-pointer");
        T.Eq(pos(0x7FFFFFF0, &cell, &x, &y, &z), 0, "GetObjectPosition: unknown id");
    }

    private static void Properties()
    {
        // Header fallbacks (never identified): a stack's size, value, burden, type.
        WorldObject note = First("Trade Note (250,000)");
        T.Eq(IntOrNull(note.Id, 12), 215, "int 12 StackSize: Trade Notes from CreateObject");
        T.Eq(IntOrNull(note.Id, 19), 53750000, "int 19 Value");
        T.Eq(IntOrNull(note.Id, 5), 215, "int 5 EncumbranceVal (burden)");
        T.Eq(IntOrNull(note.Id, 1), 0x40000, "int 1 ItemType");
        T.Eq(IntOrNull(note.Id, 11), (int?)note.Desc.MaxStackSize, "int 11 MaxStackSize");
        WorldObject pyreals = W.Objects.Values.First(o => o.Name == "Pyreal" && o.ContainerId != 0);
        T.Eq(IntOrNull(pyreals.Id, 12), (int)pyreals.StackSize, $"int 12: the Pyreal stack in a pack ({pyreals.StackSize})");
        WorldObject sack = First("Sack");
        T.Eq(IntOrNull(sack.Id, 6), 24, "int 6 ItemsCapacity: Sack");
        T.Eq(IntOrNull(sack.Id, 12), null, "int 12 on a non-stackable answers nothing");
        T.Eq(IntOrNull(sack.Id, 9999), null, "unknown int stype");
        WorldObject coat = First("Academy Coat");
        T.Eq(IntOrNull(coat.Id, 10), 0x1E00, "int 10 CurrentWieldedLocation: the worn Academy Coat");
        T.Eq(IntOrNull(coat.Id, 9), (int?)coat.Desc.ValidLocations, "int 9 ValidLocations");
        T.Eq(IntOrNull(First("Viamontian Laced Boots").Id, 10), null, "int 10 on an item in a pack answers nothing");

        // The player's own tables (PlayerDescription): every int, int64 and bool as the world model has it.
        WorldObject me = W.PlayerObject!;
        T.Check(me.Ints.Count > 0 && me.Ints.All(kv => IntOrNull(Player, kv.Key) == kv.Value), $"player ints from PlayerDescription ({me.Ints.Count})");
        T.Check(me.Int64s.All(kv => Quad(Player, kv.Key) == kv.Value), $"player int64s ({me.Int64s.Count})");
        T.Check(me.Bools.All(kv => Bool(Player, kv.Key) == kv.Value), $"player bools ({me.Bools.Count})");
        T.Check(me.Strings.All(kv => Str(Player, kv.Key) == kv.Value), $"player strings ({me.Strings.Count})");
        T.Eq(Quad(Player, 0x7FFF), null, "unknown quad stype");

        // Identify-merged tables (the boots' replayed identify).
        WorldObject boots = First("Viamontian Laced Boots");
        T.Check(boots.Ints.Count > 0 && boots.Ints.Where(kv => kv.Key is not (10 or 12)).All(kv => IntOrNull(boots.Id, kv.Key) == kv.Value),
            $"boots: identify ints ({boots.Ints.Count})");
        T.Check(boots.Floats.All(kv => Dbl(boots.Id, kv.Key) == kv.Value), $"boots: identify floats ({boots.Floats.Count})");
        T.Check(boots.Strings.All(kv => Str(boots.Id, kv.Key) == kv.Value), $"boots: identify strings ({boots.Strings.Count})");

        // Strings and doubles from the header.
        T.Eq(Str(sack.Id, 1), "Sack", "string 1 Name from the header");
        T.Eq(Str(sack.Id, 9999), null, "unknown string stype");
        WorldObject? withRadius = W.Objects.Values.FirstOrDefault(o => o.Desc.UseRadius is > 0f && !o.Floats.ContainsKey(54));
        if (withRadius != null) T.Eq(Dbl(withRadius.Id, 54), (double)withRadius.Desc.UseRadius!.Value, $"double 54 UseRadius ({withRadius.Name})");
        else T.Info("no object with a UseRadius in the capture");
        WorldObject? worked = W.Objects.Values.FirstOrDefault(o => o.Desc.Workmanship is > 0f);
        if (worked != null)
        {
            T.Eq(Dbl(worked.Id, 280), (double)worked.Desc.Workmanship!.Value, $"double 280 workmanship ({worked.Name})");
            if (!worked.Ints.ContainsKey(105)) T.Eq(IntOrNull(worked.Id, 105), (int)MathF.Round(worked.Desc.Workmanship.Value), "int 105 ItemWorkmanship from the header");
        }
        else T.Info("no object with a workmanship in the capture");
        T.Eq(Dbl(sack.Id, 9999), null, "unknown double stype");

        // Data ids and instance ids.
        T.Eq(Did(sack.Id, 8), sack.Desc.Icon, "data id 8 Icon from the header");
        T.Check((sack.Desc.Icon & 0xFF000000) == 0x06000000, "the icon is a full 0x06 data id");
        T.Eq(Did(sack.Id, 9999), null, "unknown data id stype");
        T.Eq(Iid(boots.Id, 2), Player, "instance id 2 Container: boots in the main pack");
        T.Eq(Iid(coat.Id, 3), Player, "instance id 3 Wielder: the worn coat");
        T.Eq(Iid(coat.Id, 2), null, "instance id 2 of a wielded item answers nothing");
        T.Eq(Iid(0x7FFFFFF0, 2), null, "instance id of an unknown object");

        // Null out-pointers.
        T.Eq(((delegate* unmanaged[Cdecl]<uint, uint, int*, int>)Api->GetObjectIntPropertyFn)(note.Id, 12, null), 0, "int: null out-pointer");
        T.Eq(((delegate* unmanaged[Cdecl]<uint, uint, double*, int>)Api->GetObjectDoublePropertyFn)(note.Id, 54, null), 0, "double: null out-pointer");
    }

    private static void Ownership()
    {
        WorldObject coat = First("Academy Coat");
        uint c = 1, w = 1, l = 1;
        var own = (delegate* unmanaged[Cdecl]<uint, uint*, uint*, uint*, int>)Api->GetObjectOwnershipInfoFn;
        T.Check(own(coat.Id, &c, &w, &l) == 1 && c == 0 && w == Player && l == 0x1E00, $"GetObjectOwnershipInfo: worn coat ({c:X8},{w:X8},0x{l:X})");
        WorldObject boots = First("Viamontian Laced Boots");
        T.Check(own(boots.Id, &c, &w, &l) == 1 && c == Player && w == 0 && l == 0, "GetObjectOwnershipInfo: boots in the main pack");
        T.Eq(own(boots.Id, null, &w, &l), 0, "GetObjectOwnershipInfo: null out-pointer");
        T.Eq(own(0x7FFFFFF0, &c, &w, &l), 0, "GetObjectOwnershipInfo: unknown id");
        var wi = (delegate* unmanaged[Cdecl]<uint, uint*, uint*, int>)Api->GetObjectWielderInfoFn;
        T.Check(wi(coat.Id, &w, &l) == 1 && w == Player && l == 0x1E00, "GetObjectWielderInfo: worn coat");

        // A pack's contents: what the world model has in it, ascending.
        WorldObject sack = First("Sack");
        uint[] inSack = Contents(sack.Id);
        uint[] expected = W.Objects.Values.Where(o => o.ContainerId == sack.Id || o.WielderId == sack.Id).Select(o => o.Id).OrderBy(x => x).ToArray();
        T.Check(inSack.Length > 0 && inSack.SequenceEqual(expected), $"GetContainerContents: Sack holds {inSack.Length}");
        // The player: main pack + side packs/foci (PlayerState) + what she wields.
        PlayerState p = W.Player;
        uint[] mine = Contents(Player);
        var fromState = p.MainPackOrder.Concat(p.SidePackOrder).Concat(p.EquippedItems.Keys).OrderBy(x => x).ToArray();
        T.Check(mine.SequenceEqual(fromState), $"GetContainerContents: the player = main pack {p.MainPackOrder.Count} + packs {p.SidePackOrder.Count} + wielded {p.EquippedItems.Count} (got {mine.Length})");
        uint[] three = Contents(Player, 3);
        T.Check(three.SequenceEqual(fromState.Take(3)), "GetContainerContents: a short buffer gets the lowest ids");
        T.Eq(Contents(0x7FFFFFF0).Length, 0, "GetContainerContents: unknown container");
        T.Eq(((delegate* unmanaged[Cdecl]<uint, uint*, int, int>)Api->GetContainerContentsFn)(Player, null, 10), 0, "GetContainerContents: null buffer");

        T.Eq(Call1(Api->GetNumContainedItemsFn, Player), p.MainPackOrder.Count, "GetNumContainedItems: the player's main pack");
        T.Eq(Call1(Api->GetNumContainedContainersFn, Player), 7, "GetNumContainedContainers: 3 sacks + 4 foci");
        int sackItems = W.Objects.Values.Count(o => o.ContainerId == sack.Id);
        T.Eq(Call1(Api->GetNumContainedItemsFn, sack.Id), sackItems, "GetNumContainedItems: a sack");
        T.Eq(Call1(Api->GetNumContainedContainersFn, sack.Id), 0, "GetNumContainedContainers: a sack holds no packs");
        T.Eq(Call1(Api->GetNumContainedItemsFn, 0x7FFFFFF0), -1, "GetNumContainedItems: unknown id");
    }

    // An identify reply (game event 0xC9) for a monster: one int, a two-spell book, a creature profile with attributes.
    private static byte[] IdentifyReply(uint id, uint level, uint[] spells, uint hp, uint hpMax, uint st, uint stMax, uint mp, uint mpMax)
    {
        var w = new WireWriter(128);
        w.U32(0xF7B0).U32(Player).U32(9999).U32(0xC9);
        w.U32(id).U32(0x0001 | 0x0010 | 0x0100).U32(1);
        w.U16(1).U16(8).U32(25).U32(level);                          // int table: Level
        w.U32((uint)spells.Length);
        foreach (uint s in spells) w.U32(s);
        w.U32(0x8).U32(hp).U32(hpMax);                               // creature profile, ShowAttributes
        for (int i = 0; i < 6; i++) w.U32(50);
        w.U32(st).U32(mp).U32(stMax).U32(mpMax);
        return w.ToArray();
    }

    private static byte[] UpdateHealth(uint id, float ratio)
        => new WireWriter(32).U32(0xF7B0).U32(Player).U32(10000).U32(0x01C0).U32(id).F32(ratio).ToArray();

    private static void Identify()
    {
        WorldObject boots = First("Viamontian Laced Boots");
        WorldObject slasher = First("Olthoi Slasher");

        // RequestId sends IdentifyObject (0xC8) with the id.
        int mark = F.Mark;
        T.Eq(Call1(Api->RequestIdFn, slasher.Id), 1, "RequestId: sent");
        var sent = F.ActionsSince(mark);
        T.Check(sent.Count == 1 && sent[0].Type == 0xC8 && BinaryPrimitives.ReadUInt32LittleEndian(sent[0].Payload) == slasher.Id,
            $"RequestId: game action 0xC8 with the id ({string.Join(",", sent.Select(a => $"0x{a.Type:X}"))})");
        T.Eq(Call1(Api->RequestIdFn, 0), 0, "RequestId: id 0 refused");

        // The replayed identify of the boots.
        T.Eq(Call1(Api->HasAppraisalDataFn, boots.Id), 1, "HasAppraisalData: the identified boots");
        T.Eq(Call1(Api->HasAppraisalDataFn, slasher.Id), 0, "HasAppraisalData: a monster not identified yet");
        var lastId = (delegate* unmanaged[Cdecl]<uint, long>)Api->GetLastIdTimeFn;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        T.Check(Math.Abs(lastId(boots.Id) - now) < 600, "GetLastIdTime: the boots (Unix seconds)");
        T.Eq(lastId(slasher.Id), 0L, "GetLastIdTime: never identified");
        uint[] buf = new uint[16];
        int n = SpellIds(boots.Id, buf);
        T.Check(n == boots.Spells.Count && n > 0 && buf.Take(n).SequenceEqual(boots.Spells), $"GetObjectSpellIds: the boots' spell book ({n}: {string.Join(",", buf.Take(Math.Max(n, 0)))})");
        uint[] one = new uint[1];
        T.Eq(SpellIds(boots.Id, one), boots.Spells.Count, "GetObjectSpellIds: the total with a short buffer");
        T.Eq(one[0], boots.Spells[0], "GetObjectSpellIds: the first id copied");
        T.Eq(SpellIds(slasher.Id, buf), -1, "GetObjectSpellIds: no identify = -1");
        T.Eq(((delegate* unmanaged[Cdecl]<uint, uint*, int, int>)Api->GetObjectSpellIdsFn)(boots.Id, null, 4), -1, "GetObjectSpellIds: null buffer = -1");

        var vitals = (delegate* unmanaged[Cdecl]<uint, uint*, uint*, uint*, uint*, uint*, uint*, int>)Api->GetTargetVitalsFn;
        uint h = 0, hm = 0, s = 0, sm = 0, m = 0, mm = 0;
        T.Eq(vitals(slasher.Id, &h, &hm, &s, &sm, &m, &mm), 0, "GetTargetVitals: nothing before an identify");

        // A synthetic identify reply with a spell book and a creature profile.
        F.Context.Events.Drain();
        F.Receive(IdentifyReply(slasher.Id, 100, new uint[] { 0x3E1, 0x3E2 }, 150, 200, 90, 100, 40, 50));
        T.Eq(Call1(Api->HasAppraisalDataFn, slasher.Id), 1, "HasAppraisalData after the reply");
        T.Check(Math.Abs(lastId(slasher.Id) - now) < 600, "GetLastIdTime after the reply");
        T.Eq(IntOrNull(slasher.Id, 25), 100, "the reply's int table merged (Level 25)");
        n = SpellIds(slasher.Id, buf);
        T.Check(n == 2 && buf[0] == 0x3E1 && buf[1] == 0x3E2, $"GetObjectSpellIds: the reply's spell book ({n})");
        T.Check(vitals(slasher.Id, &h, &hm, &s, &sm, &m, &mm) == 1 && h == 150 && hm == 200 && s == 90 && sm == 100 && m == 40 && mm == 50,
            $"GetTargetVitals: the creature profile ({h}/{hm} {s}/{sm} {m}/{mm})");
        T.Eq(vitals(slasher.Id, null, &hm, &s, &sm, &m, &mm), 0, "GetTargetVitals: null out-pointer");
        var events = F.Context.Events.Drain();
        T.Check(events.OfType<PluginEvent.UpdateHealth>().Any(e => e.Target == slasher.Id && e.CurrentHealth == 150 && e.MaxHealth == 200),
            "the reply's creature profile is reported as a health update (as the engine does)");
        T.Eq(F.Context.MaxHealthOf(slasher.Id), 200u, "MaxHealthHook: the identify's max health");
        T.Eq(F.Context.HealthOf(slasher.Id, 0.5f), (100u, 200u), "HealthOf: ratio 0.5 of 200");

        // UpdateHealth keeps the target's current health.
        F.Receive(UpdateHealth(slasher.Id, 0.25f));
        T.Check(vitals(slasher.Id, &h, &hm, &s, &sm, &m, &mm) == 1 && h == 50 && hm == 200, $"GetTargetVitals after UpdateHealth 0.25 ({h}/{hm})");
    }

    private static byte[] DoorMotion(uint id, ushort command)
        => new WireWriter(32).U32(0xF74C).U32(id).U16(1).U16(1).U16(0).U8(0).U8(0).U8(0).U8(0).U16(0x3D)
            .U32(0x2).U16(command).Align4().ToArray();

    private static void Physics()
    {
        var heading = (delegate* unmanaged[Cdecl]<uint, float*, int>)Api->GetObjectHeadingFn;
        float hd = -1;
        WorldObject me = W.PlayerObject!;
        float expect = (float)(((-2.0 * Math.Atan2(me.Position!.Value.Qz, me.Position.Value.Qw) * 180.0 / Math.PI) % 360.0 + 720.0) % 360.0);
        T.Check(heading(Player, &hd) == 1 && hd >= 0 && hd < 360 && Math.Abs(hd - expect) < 0.01f, $"GetObjectHeading: the player ({hd:F1})");
        T.Eq(RynthCore.Headless.Host.Objects.ObjectProps.Heading(1, 0), 0f, "heading: identity rotation = north");
        T.Check(Math.Abs(RynthCore.Headless.Host.Objects.ObjectProps.Heading(MathF.Cos(-MathF.PI / 4), MathF.Sin(-MathF.PI / 4)) - 90f) < 0.01f,
            "heading: a -90 degree yaw = east (clockwise)");
        T.Eq(heading(First("Viamontian Laced Boots").Id, &hd), 0, "GetObjectHeading: a pack item has none");

        var state = (delegate* unmanaged[Cdecl]<uint, uint*, int>)Api->GetObjectStateFn;
        uint st = 0;
        T.Check(state(Player, &st) == 1 && st == me.PhysicsState, $"GetObjectState: the player (0x{st:X})");
        T.Eq(state(0x7FFFFFF0, &st), 0, "GetObjectState: unknown id");

        // Door On / Off from UpdateMotion (a chest stands in for a door).
        WorldObject chest = First("Chest");
        var motionOn = (delegate* unmanaged[Cdecl]<uint, int*, int>)Api->GetObjectMotionOnFn;
        int on = -1;
        T.Eq(motionOn(chest.Id, &on), 0, "GetObjectMotionOn: nothing seen yet");
        T.Eq(Call1(Api->GetObjectServerAgeFn, chest.Id), -1, "GetObjectServerAge: nothing heard since the host started");
        F.Receive(DoorMotion(chest.Id, 0x000B));
        T.Check(motionOn(chest.Id, &on) == 1 && on == 1, "GetObjectMotionOn: On (0x4000000B)");
        F.Receive(DoorMotion(chest.Id, 0x000C));
        T.Check(motionOn(chest.Id, &on) == 1 && on == 0, "GetObjectMotionOn: Off (0x4000000C)");
        int age = Call1(Api->GetObjectServerAgeFn, chest.Id);
        T.Check(age >= 0 && age < 2000, $"GetObjectServerAge: just heard ({age} ms)");
        T.Eq(Call1(Api->GetObjectServerAgeFn, 0), -1, "GetObjectServerAge: id 0");

        T.Eq(((delegate* unmanaged[Cdecl]<uint, uint*, uint*, int, int>)Api->GetObjectPalettesFn)(chest.Id, &st, &st, 1), -1, "GetObjectPalettes: no data");
    }

    private static void Removal()
    {
        WorldObject slasher = First("Olthoi Slasher");   // heard from (UpdateHealth) a moment ago
        WorldObject ghost = W.Objects.Values.Where(o => o.Name == "Olthoi Slasher" && o.Id != slasher.Id).OrderBy(o => o.Id).First();
        T.Eq(Call1(Api->RemoveClientObjectFn, Player), 0, "RemoveClientObject: never the player");
        T.Eq(Call1(Api->RemoveClientObjectFn, First("Sack").Id), 0, "RemoveClientObject: not a creature");
        T.Eq(Call1(Api->RemoveClientObjectFn, slasher.Id), 0, "RemoveClientObject: heard from in the last 5 s");
        F.Context.Events.Drain();
        T.Eq(Call1(Api->RemoveClientObjectFn, ghost.Id), 1, "RemoveClientObject: a quiet monster is removed");
        T.Check(W.Get(ghost.Id) == null, "the ghost is gone from the world model");
        T.Check(F.Context.Events.Drain().OfType<PluginEvent.DeleteObject>().Any(d => d.Id == ghost.Id), "plugins are told (OnDeleteObject)");
        T.Eq(Call1(Api->RemoveClientObjectFn, ghost.Id), 0, "RemoveClientObject: already gone");
        T.Eq(Name(ghost.Id), null, "the ghost has no name any more");
    }

    private static byte[]? RecordOf(uint id)
    {
        var fn = (delegate* unmanaged[Cdecl]<uint, byte*, int, int>)Api->GetObjectPropertiesFn;
        int size = fn(id, null, 0);
        if (size <= 0) return null;
        byte[] buf = new byte[size];
        fixed (byte* p = buf)
            return fn(id, p, size) == size ? buf : null;
    }

    private static void Record()
    {
        var fn = (delegate* unmanaged[Cdecl]<uint, byte*, int, int>)Api->GetObjectPropertiesFn;
        WorldObject boots = First("Viamontian Laced Boots");
        byte[]? rec = RecordOf(boots.Id);
        T.Check(rec != null && ObjectProperties.TryParse(rec, out _), "GetObjectProperties: the boots' record parses");
        if (rec != null && ObjectProperties.TryParse(rec, out ObjectProperties? props) && props != null)
        {
            T.Check(props.HasIdData && (props.Flags & ObjectPropertyFlags.HasCreateData) != 0 && !props.IsPlayer, $"record flags ({props.Flags})");
            T.Check(props.LastIdTime > 0, "record: last identify time");
            T.Check(props.Spells.SequenceEqual(boots.Spells), $"record: spell book ({props.Spells.Count})");
            T.Check(props.Ints.All(kv => IntOrNull(boots.Id, kv.Key) == kv.Value), $"record ints = per-key reads ({props.Ints.Count})");
            T.Check(props.Floats.All(kv => Dbl(boots.Id, kv.Key) == kv.Value), $"record floats = per-key reads ({props.Floats.Count})");
            T.Eq(props.InstanceIds.GetValueOrDefault(2u), Player, "record: container");
            T.Eq(props.Strings.GetValueOrDefault(1u), "Viamontian Laced Boots", "record: name");
            T.Eq(props.Ints.GetValueOrDefault(19u), boots.Value, "record: value from the header");
        }
        byte[]? mine = RecordOf(Player);
        T.Check(mine != null && ObjectProperties.TryParse(mine, out ObjectProperties? pp) && pp!.IsPlayer && pp.Ints.Count >= W.PlayerObject!.Ints.Count,
            "GetObjectProperties: the player's record (IsPlayer, PlayerDescription ints)");
        WorldObject note = First("Trade Note (250,000)");
        byte[]? nrec = RecordOf(note.Id);
        T.Check(nrec != null && ObjectProperties.TryParse(nrec, out ObjectProperties? np) && np!.Ints.GetValueOrDefault(12u) == 215 && !np.HasIdData,
            "GetObjectProperties: an unidentified stack (header stack size, no id data)");
        byte small = 0;
        T.Eq(fn(boots.Id, &small, 1), rec?.Length ?? -2, "GetObjectProperties: a short buffer gets the size");
        T.Eq(fn(0x7FFFFFF0, null, 0), 0, "GetObjectProperties: unknown id = 0");
        T.Eq(fn(0, null, 0), -1, "GetObjectProperties: id 0 = -1");
        T.Eq(fn(boots.Id, null, -1), -1, "GetObjectProperties: negative size = -1");
        T.Eq(fn(boots.Id, null, 10), -1, "GetObjectProperties: null buffer with a size = -1");
    }

    private static void OrderAndWielded()
    {
        var order = (delegate* unmanaged[Cdecl]<uint, int, uint*, int, int>)Api->GetContainerOrderFn;
        PlayerState p = W.Player;
        uint[] buf = new uint[256];
        fixed (uint* b = buf)
        {
            int n = order(Player, 0, b, buf.Length);
            T.Check(n == p.MainPackOrder.Count && buf.Take(n).SequenceEqual(p.MainPackOrder), $"GetContainerOrder: the main pack's items in slot order ({n})");
            n = order(Player, 1, b, buf.Length);
            T.Check(n == 7 && buf.Take(n).SequenceEqual(p.SidePackOrder), $"GetContainerOrder: the pack column ({n})");
            T.Eq(order(Player, 0, null, 0), p.MainPackOrder.Count, "GetContainerOrder: count only");
            T.Eq(order(First("Sack").Id, 0, b, buf.Length), -1, "GetContainerOrder: a side pack is unknown (-1)");
            T.Eq(order(Player, 2, b, buf.Length), -1, "GetContainerOrder: bad slot kind");
            T.Eq(order(Player, 0, null, 5), -1, "GetContainerOrder: null buffer with a size");
        }

        var wielded = (delegate* unmanaged[Cdecl]<uint*, uint*, int, int>)Api->GetWieldedItemsFn;
        uint* ids = stackalloc uint[64];
        uint* locs = stackalloc uint[64];
        int count = wielded(ids, locs, 64);
        var got = new Dictionary<uint, uint>();
        for (int i = 0; i < Math.Max(count, 0); i++) got[ids[i]] = locs[i];
        T.Check(count == p.EquippedItems.Count && p.EquippedItems.All(kv => got.TryGetValue(kv.Key, out uint l) && l == kv.Value),
            $"GetWieldedItems: PlayerDescription's equipped list ({count})");
        T.Eq(got.GetValueOrDefault(First("Academy Coat").Id), 0x1E00u, "GetWieldedItems: the coat's location");
        T.Eq(wielded(ids, locs, 2), 2, "GetWieldedItems: at most max");
        T.Eq(wielded(null, locs, 64), -1, "GetWieldedItems: null buffer");
    }
}
