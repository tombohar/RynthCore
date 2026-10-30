using System.Buffers.Binary;
using RynthCore.Engine.Compatibility;

namespace PropertyWireTests;

/// <summary>
/// One identify reply, update set or PublicWeenieDesc per kind of object, built the way ACE
/// writes them, through the engine's parsers; every value the engine would cache is checked.
/// </summary>
internal static class Program
{
    private static int _checks, _failed;
    private static string _case = "";

    private const uint Me = 0x50000001, Other = 0x50000002, Leggings = 0x80000101, Sword = 0x80000102,
        Scroll = 0x80000103, Drudge = 0x80000201, Door = 0x7A9B4001, Portal = 0x7A9B4002, Chest = 0x7A9B4003,
        Corpse = 0x80000301, HookObj = 0x7A9B4004;

    private static int Main()
    {
        PackItemIdentified();
        PackItemNotIdentified();
        WornWeapon();
        CreatureIdentified();
        CreatureFailedRoll();
        CreatureNotIdentified();
        OtherPlayer();
        LandscapeDoorPortalChest();
        CorpseIdentified();
        HouseHook();
        PlayerSelf();
        UpdatesEveryType();
        StringUpdateAlignment();
        TruncatedAndBadInput();

        Console.WriteLine();
        Console.WriteLine($"{_checks - _failed}/{_checks} checks passed.");
        Console.WriteLine(_failed == 0 ? "ALL PROPERTY WIRE TESTS PASSED." : $"{_failed} FAILED.");
        return _failed == 0 ? 0 : 1;
    }

    // ── object kinds ────────────────────────────────────────────────────────

    /// <summary>Studded Leather Leggings: tables, spells, armour profile + armour highlights, armour levels.</summary>
    private static void PackItemIdentified()
    {
        Case("pack item, identified (armour)");
        var a = new AppraiseInfo
        {
            Ints = { [1] = 2, [5] = 900, [19] = 1200, [28] = 120, [105] = 6, [106] = 180, [109] = 210, [158] = 7, [159] = 0, [160] = 150, [131] = 42 },
            Bools = { [69] = true },
            Strings = { [16] = "Studded Leather Leggings of Acid Protection" },
            Dids = { [37] = 0x0000002C },
            SpellBook = { 1391, 2092 },
            Armor = new ArmorProfile(1.1f, 0.9f, 0.8f, 0.6f, 0.4f, 1.3f, 0.5f, 0.7f),
            ArmorHighlight = 0x0003, ArmorColor = 0x0001,
            ArmorLevels = new uint[] { 0, 0, 120, 0, 0, 0, 120, 120, 0 },
        };
        IdentifyRecord r = Identify(a, Leggings);
        Check(r.ObjectId == Leggings && r.Success && !r.Truncated, "header");
        Check(r.HasArmorProfile && !r.HasWeaponProfile && !r.HasCreatureProfile && !r.HasHookProfile, "profile flags");
        Eq(r.SpellCount, 2, "spell count");
        // Tables as sent, plus the eight ratings as ArmorModVs* (slash 13 .. acid 18, nether 165, electric 19).
        var floats = new Dictionary<uint, double>
        {
            [13] = 1.1f, [14] = 0.9f, [15] = 0.8f, [16] = 0.6f, [17] = 0.4f, [18] = 1.3f, [165] = 0.5f, [19] = 0.7f,
        };
        ExpectBag(r.Properties, ints: a.Ints, bools: a.Bools, floats: floats, strings: a.Strings, dids: a.Dids);
    }

    /// <summary>An item never identified: the client knows only its CreateObject (PublicWeenieDesc).</summary>
    private static void PackItemNotIdentified()
    {
        Case("pack item, not identified (PublicWeenieDesc)");
        var pwd = new byte[PwdLayout.Size];
        void U32(int off, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(pwd.AsSpan(off), v);
        U32(PwdLayout.Wcid, 20630); U32(PwdLayout.Icon, 0x06001F4A); U32(PwdLayout.IconOverlay, 0x06006C0A);
        U32(PwdLayout.Container, Me); U32(PwdLayout.Priority, 0x4000); U32(PwdLayout.ValidLocations, 0x2000);
        U32(PwdLayout.Type, 0x2000); U32(PwdLayout.Value, 250); U32(PwdLayout.Useability, 0x10008);
        BinaryPrimitives.WriteSingleLittleEndian(pwd.AsSpan(PwdLayout.UseRadius), 2.5f);
        U32(PwdLayout.TargetType, 0x10); U32(PwdLayout.Effects, 0x20); U32(PwdLayout.AmmoType, 0);
        U32(PwdLayout.CombatUse, 3); U32(PwdLayout.Structure, 50); U32(PwdLayout.MaxStructure, 100);
        U32(PwdLayout.StackSize, 10); U32(PwdLayout.MaxStackSize, 100); U32(PwdLayout.BlipColor, 8);
        U32(PwdLayout.RadarEnum, 4); U32(PwdLayout.Burden, 30); U32(PwdLayout.SpellId, 1234);
        U32(PwdLayout.HouseOwner, 0x50000009); U32(PwdLayout.HookType, 2); U32(PwdLayout.HookItemTypes, 0x80);
        U32(PwdLayout.Monarch, Other); U32(PwdLayout.MaterialType, 57);
        BinaryPrimitives.WriteSingleLittleEndian(pwd.AsSpan(PwdLayout.Workmanship), 6.5f);
        U32(PwdLayout.CooldownId, 5);
        BinaryPrimitives.WriteDoubleLittleEndian(pwd.AsSpan(PwdLayout.CooldownDuration), 15.0);
        U32(PwdLayout.PetOwner, Me);

        var ints = new Dictionary<uint, int>
        {
            [1] = 0x2000, [4] = 0x4000, [5] = 30, [9] = 0x2000, [11] = 100, [12] = 10, [16] = 0x10008, [18] = 0x20,
            [19] = 250, [51] = 3, [91] = 100, [92] = 50, [94] = 0x10, [95] = 8, [131] = 57, [133] = 4, [151] = 2,
            [152] = 0x80, [280] = 5,
        };
        foreach (var kv in ints)
            Check(PwdLayout.TryGetInt(pwd, kv.Key, out int v) && v == kv.Value, $"PWD int {kv.Key} = {kv.Value}");
        foreach (uint absent in new uint[] { 6, 7, 10, 50, 25, 2 })   // zero (not sent) or not a PWD field
            Check(!PwdLayout.TryGetInt(pwd, absent, out _), $"PWD int {absent} absent");
        Check(PwdLayout.TryGetDataId(pwd, 8, out uint d) && d == 0x06001F4A, "PWD Icon");
        Check(PwdLayout.TryGetDataId(pwd, 50, out d) && d == 0x06006C0A, "PWD IconOverlay");
        Check(!PwdLayout.TryGetDataId(pwd, 52, out _), "PWD IconUnderlay absent");
        Check(PwdLayout.TryGetDataId(pwd, 28, out d) && d == 1234, "PWD Spell");
        Check(PwdLayout.TryGetInstanceId(pwd, 2, out uint i) && i == Me, "PWD Container");
        Check(!PwdLayout.TryGetInstanceId(pwd, 3, out _), "PWD Wielder absent");
        Check(PwdLayout.TryGetInstanceId(pwd, 26, out i) && i == Other, "PWD Monarch");
        Check(PwdLayout.TryGetInstanceId(pwd, 32, out i) && i == 0x50000009, "PWD HouseOwner");
        Check(PwdLayout.TryGetInstanceId(pwd, 44, out i) && i == Me, "PWD PetOwner");
        Check(PwdLayout.TryGetFloat(pwd, 54, out double f) && f == 2.5, "PWD UseRadius");
        Check(PwdLayout.TryGetFloat(pwd, 167, out f) && f == 15.0, "PWD CooldownDuration");
        Check(PwdLayout.TryGetFloat(pwd, 280, out f) && f == 6.5, "PWD workmanship (280)");
        Check(!PwdLayout.TryGetFloat(pwd, 22, out _), "PWD has no DamageVariance");
    }

    /// <summary>A wielded sword: tables, weapon profile (buffed values), weapon highlights; then a public update.</summary>
    private static void WornWeapon()
    {
        Case("worn item, identified (weapon)");
        var a = new AppraiseInfo
        {
            // DamageType (45) is an assessment property: the table's value stays, the profile's doesn't replace it.
            Ints = { [1] = 1, [5] = 800, [19] = 5000, [45] = 1, [158] = 2, [159] = 44, [160] = 250, [105] = 8 },
            Int64s = { [4] = 12_000_000_000, [5] = 1_000_000_000 },   // ItemTotalXp, ItemBaseXp
            Bools = { [22] = true },
            Floats = { [29] = 1.15, [149] = 1.05, [150] = 1.02, [152] = 0.0 },
            Strings = { [16] = "Tachi", [7] = "Made for Tester", [8] = "Tester" },
            SpellBook = { 2083, 0x80000000u | 1616 },
            Weapon = new WeaponProfile(DamageType: 3, WeaponTime: 35, WeaponSkill: 44, Damage: 38,
                DamageVariance: 0.45, DamageMod: 1.1, WeaponLength: 1.5, MaxVelocity: 1.0, WeaponOffense: 1.08, MaxVelocityEstimated: 7),
            WeaponHighlight = 0x0005, WeaponColor = 0x0005,
        };
        IdentifyRecord r = Identify(a, Sword);
        Check(r.HasWeaponProfile && !r.HasArmorProfile && !r.Truncated, "weapon profile read");
        var ints = new Dictionary<uint, int>(a.Ints) { [49] = 35, [48] = 44, [44] = 38 };   // WeaponTime, WeaponSkill, Damage
        var floats = new Dictionary<uint, double>(a.Floats) { [22] = 0.45, [63] = 1.1, [21] = 1.5, [26] = 1.0, [62] = 1.08 };
        ExpectBag(r.Properties, ints: ints, int64s: a.Int64s, bools: a.Bools, floats: floats, strings: a.Strings);

        // A caster: ACE sends no weapon profile for one, only the tables.
        var wand = new AppraiseInfo { Ints = { [1] = 0x8000 }, Floats = { [144] = 1.2, [152] = 1.1 } };
        IdentifyRecord w = Identify(wand, Sword + 1);
        Check(!w.HasWeaponProfile, "caster: no weapon profile");
        ExpectBag(w.Properties, ints: wand.Ints, floats: wand.Floats);

        // Later: the sword's structure changes and its inscription is edited (public updates).
        PropertyUpdate u = Update(Updates.PublicInt(Sword, 92, 7));
        Check(u.Kind == PropertyKind.Int && !u.IsPrivate && u.ObjectId == Sword && u.Key == 92 && u.Integer == 7, "public Structure update");
        var bag = r.Properties;
        bag.Apply(u);
        bag.Apply(Update(Updates.PublicString(Sword, 7, "Made for someone else")));
        Eq(bag.Ints[92], 7, "update folded into the identify record (int)");
        Eq(bag.Strings[7], "Made for someone else", "update folded into the identify record (string)");
    }

    /// <summary>A monster, successful assess: ints, strings, creature profile with attributes and buff highlights.</summary>
    private static void CreatureIdentified()
    {
        Case("creature, identified");
        var a = new AppraiseInfo
        {
            Ints = { [1] = 0x10, [2] = 3, [25] = 25, [307] = 10, [313] = 5 },
            Strings = { [16] = "A drudge skulker." },
            Creature = new CreatureProfile(0x8 | 0x1, 180, 200, new uint[] { 60, 70, 80, 90, 50, 40, 150, 100, 160, 110 }, 0x0003, 0x0001),
            ResistHighlight = 0x0010, ResistColor = 0x0010,
        };
        IdentifyRecord r = Identify(a, Drudge);
        Check(r.HasCreatureProfile && r.CreatureHealth == 180 && r.CreatureHealthMax == 200 && !r.Truncated, "creature profile read");
        ExpectBag(r.Properties, ints: a.Ints, strings: a.Strings);
    }

    /// <summary>A failed assess: Success = 0, the creature profile carries health only; the tables still come.</summary>
    private static void CreatureFailedRoll()
    {
        Case("creature, failed assess");
        var a = new AppraiseInfo
        {
            Success = false,
            Ints = { [1] = 0x10, [2] = 3, [25] = 25 },
            Creature = new CreatureProfile(0x0, 200, 200),
        };
        IdentifyRecord r = Identify(a, Drudge);
        Check(!r.Success && r.HasCreatureProfile && r.CreatureHealthMax == 200 && !r.Truncated, "failed roll read");
        ExpectBag(r.Properties, ints: a.Ints);
    }

    /// <summary>A monster never assessed: CreateObject fields only.</summary>
    private static void CreatureNotIdentified()
    {
        Case("creature, not identified (PublicWeenieDesc)");
        var pwd = new byte[PwdLayout.Size];
        BinaryPrimitives.WriteUInt32LittleEndian(pwd.AsSpan(PwdLayout.Type), 0x10);
        BinaryPrimitives.WriteUInt32LittleEndian(pwd.AsSpan(PwdLayout.Useability), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(pwd.AsSpan(PwdLayout.BlipColor), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(pwd.AsSpan(PwdLayout.RadarEnum), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(pwd.AsSpan(PwdLayout.PetOwner), Me);   // a summoned pet
        Check(PwdLayout.TryGetInt(pwd, 1, out int t) && t == 0x10, "ItemType Creature");
        Check(PwdLayout.TryGetInt(pwd, 16, out int use) && use == 1, "ItemUseable");
        Check(PwdLayout.TryGetInt(pwd, 95, out int blip) && blip == 3, "RadarBlipColor");
        Check(PwdLayout.TryGetInstanceId(pwd, 44, out uint owner) && owner == Me, "PetOwner");
        Check(!PwdLayout.TryGetInt(pwd, 19, out _), "no Value");
    }

    /// <summary>Another player: allegiance strings, ratings, creature profile; then public updates about them.</summary>
    private static void OtherPlayer()
    {
        Case("another player");
        var a = new AppraiseInfo
        {
            Ints = { [1] = 0x10, [25] = 275, [113] = 1, [188] = 3, [307] = 12, [218] = 9 },
            Strings = { [1] = "Someone Else", [47] = "The Crew", [21] = "High King Boss", [35] = "Lord Patron", [10] = "Hunters" },
            Creature = new CreatureProfile(0x8, 400, 400, new uint[] { 100, 100, 100, 100, 100, 100, 300, 250, 300, 250 }),
            ArmorLevels = new uint[] { 300, 310, 320, 330, 340, 350, 360, 370, 380 },
        };
        IdentifyRecord r = Identify(a, Other);
        Check(r.HasCreatureProfile && !r.Truncated, "player profile read");
        ExpectBag(r.Properties, ints: a.Ints, strings: a.Strings);

        PropertyUpdate u = Update(Updates.PublicInstanceId(Other, 26, Me));
        Check(u.Kind == PropertyKind.InstanceId && u.ObjectId == Other && u.Key == 26 && u.Integer == Me, "public Monarch update");
        u = Update(Updates.PublicBool(Other, 7, true));
        Check(u.Kind == PropertyKind.Bool && u.ObjectId == Other && u.Key == 7 && u.Integer == 1, "public PlayerKiller-style bool update");
    }

    /// <summary>A locked door (lockpick numbers), a portal (level limits), a chest.</summary>
    private static void LandscapeDoorPortalChest()
    {
        Case("landscape: door, portal, chest");
        var door = new AppraiseInfo { Ints = { [1] = 0x80, [38] = 250, [173] = 67 }, Bools = { [3] = true, [2] = false } };
        IdentifyRecord r = Identify(door, Door);
        ExpectBag(r.Properties, ints: door.Ints, bools: door.Bools);

        var portal = new AppraiseInfo
        {
            Ints = { [1] = 0x10000, [86] = 20, [87] = 80, [111] = 0x31 },
            Strings = { [14] = "Walk through to Town Network.", [16] = "A portal." },
        };
        r = Identify(portal, Portal);
        ExpectBag(r.Properties, ints: portal.Ints, strings: portal.Strings);

        var chest = new AppraiseInfo { Ints = { [1] = 0x200, [19] = 0, [38] = 0 }, Bools = { [3] = false } };
        r = Identify(chest, Chest);
        ExpectBag(r.Properties, ints: chest.Ints, bools: chest.Bools);

        // The door opens: public bool update for Open.
        PropertyUpdate u = Update(Updates.PublicBool(Door, 2, true));
        Check(u.ObjectId == Door && u.Key == 2 && u.Integer == 1, "door Open update");
    }

    /// <summary>ACE strips a corpse to EncumbranceVal, Value = 0 and LongDesc.</summary>
    private static void CorpseIdentified()
    {
        Case("corpse");
        var a = new AppraiseInfo { Ints = { [5] = 2400, [19] = 0 }, Strings = { [16] = "Killed by Tester." } };
        IdentifyRecord r = Identify(a, Corpse);
        ExpectBag(r.Properties, ints: a.Ints, strings: a.Strings);
    }

    /// <summary>A house hook holding an item: the item's tables, then the hook profile.</summary>
    private static void HouseHook()
    {
        Case("house hook");
        var a = new AppraiseInfo
        {
            Ints = { [1] = 0x100, [19] = 100 },
            Strings = { [16] = "This hook is owned by Tester. It contains: \nArrows" },
            Hook = new HookProfile(Flags: 0x1 | 0x8, ValidLocations: 0x800000, AmmoType: 1),
        };
        IdentifyRecord r = Identify(a, HookObj);
        Check(r.HasHookProfile && !r.Truncated, "hook profile read");
        var ints = new Dictionary<uint, int>(a.Ints) { [9] = 0x800000, [50] = 1 };
        ExpectBag(r.Properties, ints: ints, bools: new() { [22] = true }, strings: a.Strings);

        // A hook with an item that isn't inscribable and has no ammo type: only its locations.
        var b = new AppraiseInfo { Ints = { [1] = 0x100 }, Hook = new HookProfile(0x2, 0x1, 0) };
        r = Identify(b, HookObj);
        ExpectBag(r.Properties, ints: new() { [1] = 0x100, [9] = 0x1 });
    }

    /// <summary>The player: PlayerDescription's seven tables, then a private update of every type.</summary>
    private static void PlayerSelf()
    {
        Case("the player (PlayerDescription + private updates)");
        var pd = new PlayerDescription
        {
            Ints = { [25] = 275, [5] = 1500, [20] = 250000, [43] = 3, [96] = 4000, [307] = 10, [113] = 1 },
            Int64s = { [1] = 191_226_310_247, [2] = 1_000_000, [6] = 500_000, [7] = 1_500_000 },
            Bools = { [74] = true, [81] = false },
            Floats = { [64] = 1.0, [72] = 0.8, [98] = 1_727_000_000.5 },
            Strings = { [1] = "Tester", [5] = "Adventurer", [47] = "The Crew", [2] = "Sir" },
            Dids = { [1] = 0x02000001, [8] = 0x06001036, [23] = 0x0400007E },
            Iids = { [24] = 0x50000010, [25] = 0x50000011, [26] = 0x50000012 },
        };
        Check(PropertyWire.TryParsePlayerDescription(pd.ToMessage(), out PropertyBag bag, out bool truncated), "PlayerDescription parsed");
        Check(!truncated, "not truncated");
        ExpectBag(bag, ints: pd.Ints, int64s: pd.Int64s, bools: pd.Bools, floats: pd.Floats, strings: pd.Strings, dids: pd.Dids, iids: pd.Iids);

        // Private updates carry no guid; the engine files them in the player's record.
        byte[][] msgs =
        {
            Updates.PrivateInt(5, 1650), Updates.PrivateInt64(1, 191_300_000_000), Updates.PrivateBool(81, true),
            Updates.PrivateFloat(72, 0.75), Updates.PrivateString(2, "Lord"), Updates.PrivateDataId(8, 0x06001037),
            Updates.PrivateInstanceId(25, 0x50000013),
        };
        foreach (byte[] m in msgs)
        {
            PropertyUpdate u = Update(m);
            Check(u.IsPrivate && u.ObjectId == 0, $"private {u.Kind} has no guid");
            bag.Apply(u);
        }
        Eq(bag.Ints[5], 1650, "EncumbranceVal after update");
        Eq(bag.Int64s[1], 191_300_000_000L, "TotalExperience after update");
        Eq(bag.Bools[81], true, "bool after update");
        Eq(bag.Floats[72], 0.75, "float after update");
        Eq(bag.Strings[2], "Lord", "Title after update");
        Eq(bag.DataIds[8], 0x06001037u, "Icon after update");
        Eq(bag.InstanceIds[25], 0x50000013u, "Patron after update");

        // A description with no tables at all still parses.
        var empty = new PlayerDescription();
        Check(PropertyWire.TryParsePlayerDescription(empty.ToMessage(), out bag, out truncated) && bag.Count == 0 && !truncated, "empty description");
    }

    /// <summary>Every opcode, public and private, round-trips kind, guid, key and value.</summary>
    private static void UpdatesEveryType()
    {
        Case("UpdateProperty* (every type)");
        const uint g = 0x80000555;
        Expect(Updates.PublicInt(g, 12, -3), PropertyKind.Int, false, g, 12, -3);
        Expect(Updates.PrivateInt(12, int.MaxValue), PropertyKind.Int, true, 0, 12, int.MaxValue);
        Expect(Updates.PublicInt64(g, 4, long.MaxValue), PropertyKind.Int64, false, g, 4, long.MaxValue);
        Expect(Updates.PrivateInt64(2, -5), PropertyKind.Int64, true, 0, 2, -5);
        Expect(Updates.PublicBool(g, 19, false), PropertyKind.Bool, false, g, 19, 0);
        Expect(Updates.PrivateBool(19, true), PropertyKind.Bool, true, 0, 19, 1);
        Expect(Updates.PublicDataId(g, 8, 0x06004D2A), PropertyKind.DataId, false, g, 8, 0x06004D2A);
        Expect(Updates.PrivateDataId(28, 0xFFFFFFFF), PropertyKind.DataId, true, 0, 28, 0xFFFFFFFF);
        Expect(Updates.PublicInstanceId(g, 3, Me), PropertyKind.InstanceId, false, g, 3, Me);
        Expect(Updates.PrivateInstanceId(26, Other), PropertyKind.InstanceId, true, 0, 26, Other);

        PropertyUpdate f = Update(Updates.PublicFloat(g, 167, 12.25));
        Check(f.Kind == PropertyKind.Float && !f.IsPrivate && f.ObjectId == g && f.Key == 167 && f.Real == 12.25, "public float");
        f = Update(Updates.PrivateFloat(98, 1.5e9));
        Check(f.Kind == PropertyKind.Float && f.IsPrivate && f.Key == 98 && f.Real == 1.5e9, "private float");
        Check(!PropertyWire.TryParsePropertyUpdate(Updates.PrivateFloat(98, double.NaN), out _), "NaN float refused");

        PropertyUpdate s = Update(Updates.PublicString(g, 16, "A long description."));
        Check(s.Kind == PropertyKind.String && s.ObjectId == g && s.Key == 16 && s.Text == "A long description.", "public string (key before guid)");
        s = Update(Updates.PrivateString(5, "Template"));
        Check(s.Kind == PropertyKind.String && s.IsPrivate && s.Key == 5 && s.Text == "Template", "private string");
    }

    /// <summary>Strings of every length mod 4, and a code page 1252 character.</summary>
    private static void StringUpdateAlignment()
    {
        Case("string updates: alignment and padding");
        foreach (string text in new[] { "", "a", "ab", "abc", "abcd", "abcde", "Aelrynth Sword of Æsc" })
        {
            PropertyUpdate p = Update(Updates.PrivateString(7, text));
            Check(p.Text == text, $"private \"{text}\"");
            PropertyUpdate q = Update(Updates.PublicString(Sword, 7, text));
            Check(q.Text == text && q.ObjectId == Sword, $"public \"{text}\"");
        }
        // A string table in an identify with the same lengths.
        var a = new AppraiseInfo { Strings = { [1] = "", [2] = "a", [3] = "ab", [4] = "abc", [5] = "abcd" }, Ints = { [1] = 1 } };
        a.Dids[8] = 0x06001234;   // after the string table: proves the padding was consumed
        IdentifyRecord r = Identify(a, Scroll);
        ExpectBag(r.Properties, ints: a.Ints, strings: a.Strings, dids: a.Dids);
    }

    private static void TruncatedAndBadInput()
    {
        Case("truncated and bad messages");
        var a = new AppraiseInfo
        {
            Ints = { [1] = 2, [19] = 5 },
            Bools = { [69] = true },
            Floats = { [5] = -0.03, [29] = 1.1 },
            Strings = { [16] = "Cut short" },
        };
        byte[] full = a.ToIdentifyMessage(Scroll);
        // Cut inside the float table: ints and bools kept, the rest dropped, flagged truncated.
        int cut = 16 + 4 + 16 + 4 + 8 + 4 + 6;
        Check(PropertyWire.TryParseIdentify(full.AsSpan(0, cut), out IdentifyRecord r), "truncated identify still parsed");
        Check(r.Truncated, "flagged truncated");
        ExpectBag(r.Properties, ints: a.Ints, bools: a.Bools);

        Check(!PropertyWire.TryParseIdentify(full.AsSpan(0, 12), out _), "no header, no record");
        byte[] wrongType = (byte[])full.Clone();
        wrongType[0] = 0xC8;
        Check(!PropertyWire.TryParseIdentify(wrongType, out _), "not an identify");
        Check(!PropertyWire.TryParsePlayerDescription(full, out _, out _), "an identify is not a PlayerDescription");

        byte[] upd = Updates.PublicInt(Sword, 92, 1);
        Check(!PropertyWire.TryParsePropertyUpdate(upd.AsSpan(0, upd.Length - 1), out _), "short update refused");
        Check(!PropertyWire.TryParsePropertyUpdate(Updates.PrivateString(1, "x").AsSpan(0, 13), out _), "short string refused");
        byte[] other = (byte[])upd.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(other, 0x02DB);   // PrivateUpdatePosition
        Check(!PropertyWire.TryParsePropertyUpdate(other, out _), "position update is not a property update");
        Check(PropertyWire.IsPropertyUpdateOpcode(0x02CD) && PropertyWire.IsPropertyUpdateOpcode(0x02DA)
              && !PropertyWire.IsPropertyUpdateOpcode(0x02CC) && !PropertyWire.IsPropertyUpdateOpcode(0x02DB), "opcode range");
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static IdentifyRecord Identify(AppraiseInfo a, uint guid)
    {
        bool ok = PropertyWire.TryParseIdentify(a.ToIdentifyMessage(guid), out IdentifyRecord r);
        Check(ok, "identify parsed");
        Check(r.ObjectId == guid && r.Flags == a.Flags && r.Success == a.Success, "identify header");
        return r;
    }

    private static PropertyUpdate Update(byte[] msg)
    {
        bool ok = PropertyWire.TryParsePropertyUpdate(msg, out PropertyUpdate u);
        Check(ok, $"update 0x{BinaryPrimitives.ReadUInt32LittleEndian(msg):X4} parsed");
        return u;
    }

    private static void Expect(byte[] msg, PropertyKind kind, bool isPrivate, uint guid, uint key, long value)
    {
        PropertyUpdate u = Update(msg);
        Check(u.Kind == kind && u.IsPrivate == isPrivate && u.ObjectId == guid && u.Key == key && u.Integer == value,
            $"{(isPrivate ? "private" : "public")} {kind} key {key} = {value} (got {u.Kind} {u.IsPrivate} 0x{u.ObjectId:X8} {u.Key} {u.Integer})");
    }

    /// <summary>Every table of the bag must equal the expected one: same keys, same values.</summary>
    private static void ExpectBag(PropertyBag bag,
        Dictionary<uint, int>? ints = null, Dictionary<uint, long>? int64s = null, Dictionary<uint, bool>? bools = null,
        Dictionary<uint, double>? floats = null, Dictionary<uint, string>? strings = null, Dictionary<uint, uint>? dids = null,
        Dictionary<uint, uint>? iids = null)
    {
        Same("int", bag.Ints, ints ?? new());
        Same("int64", bag.Int64s, int64s ?? new());
        Same("bool", bag.Bools, bools ?? new());
        Same("float", bag.Floats, floats ?? new());
        Same("string", bag.Strings, strings ?? new());
        Same("did", bag.DataIds, dids ?? new());
        Same("iid", bag.InstanceIds, iids ?? new());
    }

    private static void Same<T>(string what, Dictionary<uint, T> actual, Dictionary<uint, T> expected)
    {
        Check(actual.Count == expected.Count, $"{what}: {actual.Count} values, expected {expected.Count}");
        foreach (var kv in expected)
            Check(actual.TryGetValue(kv.Key, out T? v) && EqualityComparer<T>.Default.Equals(v, kv.Value),
                $"{what}[{kv.Key}] = {kv.Value} (got {(actual.TryGetValue(kv.Key, out T? g) ? g : "nothing")})");
    }

    private static void Case(string name)
    {
        _case = name;
        Console.WriteLine($"- {name}");
    }

    private static void Eq<T>(T actual, T expected, string what) =>
        Check(EqualityComparer<T>.Default.Equals(actual, expected), $"{what}: expected {expected}, got {actual}");

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (ok) return;
        _failed++;
        Console.WriteLine($"  FAIL [{_case}] {what}");
    }
}
