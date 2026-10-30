using System.Buffers.Binary;
using RynthCore.Engine.Compatibility;
using EngineFlags = RynthCore.Engine.Compatibility.ObjectPropertyFlags;
using SdkFlags = RynthCore.PluginSdk.ObjectPropertyFlags;
using SdkProps = RynthCore.PluginSdk.ObjectProperties;

namespace ObjectPropertyTests;

internal static class Program
{
    private static int _checks, _failed;
    private static string _case = "";

    private static int Main()
    {
        Run("record: every type round-trips engine encoder -> SDK parser", RoundTrip);
        Run("record: header, flags and the sorted, de-duplicated layout", HeaderAndLayout);
        Run("record: long strings are cut on a character boundary", LongStrings);
        Run("record: an empty object, and malformed records are refused", EmptyAndMalformed);
        Run("merge: another object prefers its identify, instance ids its updates", MergeOther);
        Run("merge: the player prefers its own record, then updates, qualities, identify", MergePlayer);
        Run("merge: the client's stack size, container and wielder win when set", MergeCreateOverrides);
        Run("create data: the PublicWeenieDesc fields come out as their properties", CreateDataFill);
        Run("flags: engine and SDK bits agree", FlagParity);
        Console.WriteLine($"{_checks} check(s), {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        _case = name;
        int before = _failed;
        try { test(); }
        catch (Exception ex) { Fail($"threw {ex.GetType().Name}: {ex.Message}"); }
        Console.WriteLine($"[{(_failed == before ? "PASS" : "FAIL")}]  {name}");
    }

    private static void Check(bool ok, string what) { _checks++; if (!ok) Fail(what); }
    private static void Fail(string what) { _failed++; Console.WriteLine($"   FAIL {_case}: {what}"); }

    private static SdkProps Parse(byte[] rec)
    {
        Check(SdkProps.TryParse(rec, out SdkProps? p) && p != null, "the SDK parses the engine's record");
        return p ?? throw new InvalidOperationException("no parse");
    }

    private static PropertyBag SampleBag()
    {
        var b = new PropertyBag();
        b.Ints[1] = 2;              // ItemType Armor
        b.Ints[9015] = -7;          // a key no fixed probe range reached (ACE InventoryOrder)
        b.Ints[19] = 12345;
        b.Int64s[4] = 1_000_000_000_000L;   // ItemTotalXp
        b.Bools[22] = true;
        b.Bools[69] = false;
        b.Floats[13] = 1.25;        // ArmorModVsSlash
        b.Floats[5] = -0.0333;
        b.Strings[1] = "Olthoi Amuli Coat";
        b.Strings[16] = "Grün — été";   // non-ASCII survives
        b.Strings[7] = "";
        b.DataIds[8] = 0x06001234;  // Icon
        b.DataIds[8044] = 0xFFFFFFFF;
        b.InstanceIds[2] = 0x50000001;
        b.InstanceIds[38] = 0x80000ABC;
        return b;
    }

    private static void RoundTrip()
    {
        PropertyBag b = SampleBag();
        uint[] spells = { 2783, 4400, 6055 };
        byte[] rec = ObjectPropertyRecord.Encode(b, spells, EngineFlags.HasIdData | EngineFlags.QualitiesRead, 1_760_000_000L);
        SdkProps p = Parse(rec);
        Check(p.Ints.Count == 3 && p.Ints[1] == 2 && p.Ints[9015] == -7 && p.Ints[19] == 12345, "ints");
        Check(p.Int64s.Count == 1 && p.Int64s[4] == 1_000_000_000_000L, "int64s");
        Check(p.Bools.Count == 2 && p.Bools[22] && !p.Bools[69], "bools");
        Check(p.Floats.Count == 2 && p.Floats[13] == 1.25 && p.Floats[5] == -0.0333, "floats (exact doubles)");
        Check(p.Strings.Count == 3 && p.Strings[1] == "Olthoi Amuli Coat" && p.Strings[16] == "Grün — été" && p.Strings[7] == "", "strings (UTF-8, empty)");
        Check(p.DataIds.Count == 2 && p.DataIds[8] == 0x06001234 && p.DataIds[8044] == 0xFFFFFFFF, "data ids");
        Check(p.InstanceIds.Count == 2 && p.InstanceIds[2] == 0x50000001 && p.InstanceIds[38] == 0x80000ABC, "instance ids");
        Check(p.Spells.SequenceEqual(spells), "spells in spell book order");
        Check(p.HasIdData && !p.QualitiesPending && !p.IsPlayer && (p.Flags & SdkFlags.QualitiesRead) != 0, "flags");
        Check(p.LastIdTime == 1_760_000_000L, "last id time");
        Check(p.Count == b.Count + spells.Length, $"count {p.Count}");
    }

    private static void HeaderAndLayout()
    {
        var b = new PropertyBag();
        b.Ints[300] = 3; b.Ints[2] = 1; b.Ints[45] = 2;
        byte[] rec = ObjectPropertyRecord.Encode(b, new uint[] { 9, 0, 9, 7 }, EngineFlags.IsPlayer, 0);
        Check(BinaryPrimitives.ReadUInt32LittleEndian(rec) == SdkProps.RecordMagic && SdkProps.RecordMagic == ObjectPropertyRecord.Magic, "magic");
        Check(BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(4)) == SdkProps.RecordVersion && ObjectPropertyRecord.FormatVersion == SdkProps.RecordVersion, "version");
        Check(BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(16)) == 5, "entry count 3 ints + 2 spells (0 and the repeat dropped)");
        // Ints sorted by key: 2, 45, 300.
        uint k0 = BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(ObjectPropertyRecord.HeaderSize + 1));
        uint k1 = BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(ObjectPropertyRecord.HeaderSize + 9 + 1));
        uint k2 = BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(ObjectPropertyRecord.HeaderSize + 18 + 1));
        Check(k0 == 2 && k1 == 45 && k2 == 300, $"sorted keys {k0},{k1},{k2}");
        Check(rec.Length == ObjectPropertyRecord.HeaderSize + 3 * 9 + 2 * 5, $"size {rec.Length}");
        SdkProps p = Parse(rec);
        Check(p.IsPlayer && p.Spells.SequenceEqual(new uint[] { 9, 7 }), "player flag, spells 9 then 7");
    }

    private static void LongStrings()
    {
        var b = new PropertyBag();
        b.Strings[16] = new string('a', ObjectPropertyRecord.MaxStringBytes - 1) + "€€";   // 3-byte euro across the cut
        b.Strings[1] = new string('x', 100_000);
        SdkProps p = Parse(ObjectPropertyRecord.Encode(b, null, EngineFlags.None, 0));
        Check(p.Strings[16] == new string('a', ObjectPropertyRecord.MaxStringBytes - 1), "cut before a character that doesn't fit, never inside it");
        Check(p.Strings[1].Length == ObjectPropertyRecord.MaxStringBytes, "ASCII cut at the cap");
    }

    private static void EmptyAndMalformed()
    {
        byte[] rec = ObjectPropertyRecord.Encode(new PropertyBag(), null, EngineFlags.QualitiesPending, 0);
        Check(rec.Length == ObjectPropertyRecord.HeaderSize, "empty record = header only");
        SdkProps p = Parse(rec);
        Check(p.Count == 0 && p.QualitiesPending, "empty, pending");

        byte[] full = ObjectPropertyRecord.Encode(SampleBag(), new uint[] { 1 }, EngineFlags.None, 0);
        Check(!SdkProps.TryParse(full.AsSpan(0, full.Length - 1), out _), "truncated by one byte refused");
        Check(!SdkProps.TryParse(full.AsSpan(0, 10), out _), "shorter than the header refused");
        byte[] bad = (byte[])full.Clone(); bad[0] ^= 0xFF;
        Check(!SdkProps.TryParse(bad, out _), "wrong magic refused");
        byte[] newer = (byte[])full.Clone(); newer[4] = 2;
        Check(!SdkProps.TryParse(newer, out _), "a newer format refused");
        byte[] kind = (byte[])full.Clone(); kind[ObjectPropertyRecord.HeaderSize] = 42;
        Check(!SdkProps.TryParse(kind, out _), "unknown kind refused");
    }

    private static void MergeOther()
    {
        var create = new PropertyBag(); create.Ints[19] = 10; create.Ints[5] = 50;
        var qual = new PropertyBag(); qual.Ints[19] = 20; qual.Ints[25] = 7; qual.InstanceIds[38] = 0xA;
        var upd = new PropertyBag(); upd.Ints[19] = 30; upd.Ints[92] = 4; upd.InstanceIds[38] = 0xB;
        var id = new PropertyBag(); id.Ints[19] = 40; id.Ints[92] = 5; id.Strings[16] = "long"; id.InstanceIds[38] = 0xC;
        PropertyBag m = ObjectPropertyRecord.Combine(false, create, qual, id, upd, null);
        Check(m.Ints[19] == 40 && m.Ints[92] == 5, "identify over updates over qualities over the create copy");
        Check(m.Ints[5] == 50 && m.Ints[25] == 7, "keys only one source has are kept");
        Check(m.InstanceIds[38] == 0xB, "instance ids: updates over identify (PropertyCaches.TryGetInstanceId)");
        Check(m.Strings[16] == "long", "strings");
        PropertyBag none = ObjectPropertyRecord.Combine(false, null, null, null, null, null);
        Check(none.Count == 0, "no sources, nothing");
        Check(create.Ints[19] == 10 && id.Ints[19] == 40 && qual.Ints[19] == 20, "sources unchanged");
    }

    private static void MergePlayer()
    {
        var id = new PropertyBag(); id.Ints[25] = 1; id.Ints[1] = 1;
        var qual = new PropertyBag(); qual.Ints[25] = 2; qual.Int64s[1] = 100;
        var upd = new PropertyBag(); upd.Ints[25] = 3; upd.Int64s[1] = 200;
        var self = new PropertyBag(); self.Ints[25] = 4; self.Strings[1] = "Me";
        PropertyBag m = ObjectPropertyRecord.Combine(true, null, qual, id, upd, self);
        Check(m.Ints[25] == 4, "own record wins");
        Check(m.Int64s[1] == 200, "public updates over the qualities snapshot");
        Check(m.Ints[1] == 1 && m.Strings[1] == "Me", "the rest is kept");
        PropertyBag m2 = ObjectPropertyRecord.Combine(true, null, qual, id, null, null);
        Check(m2.Ints[25] == 2, "qualities over the player's last identify");
    }

    private static void MergeCreateOverrides()
    {
        var create = new PropertyBag(); create.Ints[12] = 25; create.InstanceIds[2] = 0x500; create.InstanceIds[3] = 0;
        var id = new PropertyBag(); id.Ints[12] = 1; id.InstanceIds[2] = 0x400; id.InstanceIds[3] = 0x600;
        PropertyBag m = ObjectPropertyRecord.Combine(false, create, null, id, null, null);
        Check(m.Ints[12] == 25, "the client's stack size wins (it follows every split and merge)");
        Check(m.InstanceIds[2] == 0x500, "the client's container wins");
        Check(m.InstanceIds[3] == 0x600, "a zero wielder in the create copy doesn't hide the identify's");
    }

    private static void CreateDataFill()
    {
        byte[] pwd = new byte[PwdLayout.Size];
        void U32(int off, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(pwd.AsSpan(off), v);
        U32(PwdLayout.Type, 0x10);           // ItemType 1
        U32(PwdLayout.Value, 500);           // Value 19
        U32(PwdLayout.Burden, 75);           // EncumbranceVal 5
        U32(PwdLayout.StackSize, 3);         // StackSize 12
        U32(PwdLayout.Icon, 0x06003355);     // Icon (DID 8)
        U32(PwdLayout.SpellId, 1234);        // Spell (DID 28)
        U32(PwdLayout.Container, 0x50000002);// Container (IID 2)
        U32(PwdLayout.PetOwner, 0x50000003); // PetOwner (IID 44)
        U32(PwdLayout.CooldownId, 9);        // SharedCooldown (int 280)
        BinaryPrimitives.WriteInt32LittleEndian(pwd.AsSpan(PwdLayout.UseRadius), BitConverter.SingleToInt32Bits(2.5f));
        BinaryPrimitives.WriteInt32LittleEndian(pwd.AsSpan(PwdLayout.Workmanship), BitConverter.SingleToInt32Bits(7.5f));
        var bag = new PropertyBag();
        PwdLayout.FillBag(pwd, bag);
        Check(bag.Ints.Count == 5 && bag.Ints[1] == 0x10 && bag.Ints[19] == 500 && bag.Ints[5] == 75 && bag.Ints[12] == 3 && bag.Ints[280] == 9, "ints (zero fields answer nothing)");
        Check(bag.DataIds.Count == 2 && bag.DataIds[8] == 0x06003355 && bag.DataIds[28] == 1234, "data ids");
        Check(bag.InstanceIds.Count == 2 && bag.InstanceIds[2] == 0x50000002 && bag.InstanceIds[44] == 0x50000003, "instance ids");
        Check(bag.Floats.Count == 1 && bag.Floats[54] == 2.5, "UseRadius; the workmanship float (no property id) left out");
        foreach (uint s in PwdLayout.IntStypes) Check(PwdLayout.IntOffset(s) >= 0, $"int {s} has a PWD field");
        foreach (uint s in PwdLayout.DataIdStypes) Check(PwdLayout.DataIdOffset(s) >= 0, $"did {s} has a PWD field");
        foreach (uint s in PwdLayout.InstanceIdStypes) Check(PwdLayout.InstanceIdOffset(s) >= 0, $"iid {s} has a PWD field");
        int mapped = 0;
        for (uint s = 0; s < 10000; s++) if (PwdLayout.IntOffset(s) >= 0) mapped++;
        Check(mapped == PwdLayout.IntStypes.Length, $"every int the PWD maps is listed ({mapped} vs {PwdLayout.IntStypes.Length})");
    }

    private static void FlagParity()
    {
        foreach (EngineFlags f in Enum.GetValues<EngineFlags>())
            Check(Enum.GetName((SdkFlags)(ushort)f) == f.ToString(), $"bit 0x{(ushort)f:X4} is {f} on both sides");
        Check(Enum.GetValues<EngineFlags>().Length == Enum.GetValues<SdkFlags>().Length, "same number of flags");
        Check((byte)PropertyKind.InstanceId == 6 && ObjectPropertyRecord.KindSpell == 7, "kind numbers");
    }
}
