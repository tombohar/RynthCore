// ============================================================================
//  RynthCore.Headless - Messages/PlayerDescription.cs
//  Game event 0x0013: everything the server tells the player about itself at login.
//
//  Order: property flags, weenie type, the property tables (int, int64, bool, double,
//  string, data id, instance id, position), vector flags + "has health", attributes
//  (6 primary x 12 bytes, 3 vitals x 16), skills (32-byte entries), spells, the
//  enchantment registry, character options (options1, shortcuts, 8 spell bars,
//  desired components, spellbook filters, options2, gameplay options), then the main
//  pack list and the equipped list.
//
//  The gameplay-options blob has no length on the wire. When it is present the two
//  lists are found from the END of the message (nothing follows them): every candidate
//  (equipped count, inventory count) whose counts and entries are consistent is
//  considered, and the one with the most entries wins. Inventory can also be rebuilt
//  from the CreateObject stream (each item carries its container/wielder), which the
//  world model does anyway.
// ============================================================================

using System.Buffers.Binary;
using RynthCore.PluginSdk.Net;

namespace RynthCore.Headless.Messages;

public readonly record struct AttributeInfo(uint Ranks, uint Init, uint Xp)
{
    public uint Base => Init + Ranks;
}

public readonly record struct VitalInfo(uint Ranks, uint Init, uint Xp, uint Current);

public readonly record struct SkillInfo(uint Id, ushort Ranks, ushort AdjustPP, uint Training, uint Xp, uint Init, uint Resistance, double LastUsed)
{
    /// <summary>0 inactive, 1 untrained, 2 trained, 3 specialized.</summary>
    public bool IsTrained => Training >= 2;
}

public readonly record struct Enchantment(
    ushort SpellId, ushort Layer, ushort Category, uint PowerLevel, double StartTime, double Duration, uint CasterId,
    float DegradeModifier, float DegradeLimit, double LastDegraded, uint StatModType, uint StatModKey, float StatModValue,
    uint SpellSetId);

public readonly record struct PackEntry(uint Id, uint ContainerType);

public readonly record struct EquippedEntry(uint Id, uint Location, uint Priority);

public sealed class PlayerDescriptionData
{
    public uint WeenieType;
    public readonly Dictionary<uint, int> Ints = new();
    public readonly Dictionary<uint, long> Int64s = new();
    public readonly Dictionary<uint, bool> Bools = new();
    public readonly Dictionary<uint, double> Floats = new();
    public readonly Dictionary<uint, string> Strings = new();
    public readonly Dictionary<uint, uint> DataIds = new();
    public readonly Dictionary<uint, uint> InstanceIds = new();
    public readonly Dictionary<uint, WorldPosition> Positions = new();

    /// <summary>Strength (1) .. Self (6).</summary>
    public readonly Dictionary<uint, AttributeInfo> Attributes = new();
    /// <summary>Health (1), Stamina (2), Mana (3).</summary>
    public readonly Dictionary<uint, VitalInfo> Vitals = new();
    public readonly Dictionary<uint, SkillInfo> Skills = new();
    public readonly HashSet<uint> Spells = new();
    public readonly List<Enchantment> Enchantments = new();

    public uint OptionFlags, SpellbookFilters;
    public int Options1, Options2;
    public readonly List<(uint Slot, uint ObjectId)> Shortcuts = new();
    public readonly List<uint>[] SpellBars = Enumerable.Range(0, 8).Select(_ => new List<uint>()).ToArray();
    public readonly Dictionary<uint, int> DesiredComponents = new();

    public readonly List<PackEntry> Inventory = new();
    public readonly List<EquippedEntry> Equipped = new();
    /// <summary>False when the two lists could not be located (the world model then relies on CreateObject).</summary>
    public bool InventoryFound;
}

public static class PlayerDescription
{
    public const uint EventType = 0x0013;

    // property flags
    private const uint PInt = 0x01, PBool = 0x02, PDouble = 0x04, PDid = 0x08, PString = 0x10, PPosition = 0x20, PIid = 0x40, PInt64 = 0x80;
    // vector flags
    private const uint VAttribute = 0x001, VSkill = 0x002, VSpell = 0x100, VEnchantment = 0x200;
    // character option flags
    private const uint OShortcut = 0x1, OSquelch = 0x2, OMultiSpell = 0x4, ODesiredComps = 0x8, OExtMultiSpell = 0x10,
        OSpellbookFilters = 0x20, OOptions2 = 0x40, OTimestampFormat = 0x80, OGenericQualities = 0x100,
        OGameplayOptions = 0x200, OSpellLists8 = 0x400;

    public const int EnchantmentSize = 60;   // + 4 with a spell set id

    /// <summary>Parses the event body (after the 12-byte event header).</summary>
    public static bool TryParse(ReadOnlySpan<byte> body, out PlayerDescriptionData pd)
    {
        pd = new PlayerDescriptionData();
        return Parse(body, pd);
    }

    private static bool Parse(ReadOnlySpan<byte> body, PlayerDescriptionData pd)
    {
        var r = new NetReader(body);
        if (!r.U32(out uint pf) || !r.U32(out pd.WeenieType)) return false;

        if ((pf & PInt) != 0 && !Table(ref r, 8, (ref NetReader x, uint k) => { if (!x.I32(out int v)) return false; pd.Ints[k] = v; return true; })) return false;
        if ((pf & PInt64) != 0 && !Table(ref r, 12, (ref NetReader x, uint k) => { if (!x.U64(out ulong v)) return false; pd.Int64s[k] = (long)v; return true; })) return false;
        if ((pf & PBool) != 0 && !Table(ref r, 8, (ref NetReader x, uint k) => { if (!x.U32(out uint v)) return false; pd.Bools[k] = v != 0; return true; })) return false;
        if ((pf & PDouble) != 0 && !Table(ref r, 12, (ref NetReader x, uint k) => { if (!x.F64(out double v)) return false; pd.Floats[k] = v; return true; })) return false;
        if ((pf & PString) != 0 && !Table(ref r, 8, (ref NetReader x, uint k) => { if (!x.String16L(out string v)) return false; pd.Strings[k] = v; return true; })) return false;
        if ((pf & PDid) != 0 && !Table(ref r, 8, (ref NetReader x, uint k) => { if (!x.U32(out uint v)) return false; pd.DataIds[k] = v; return true; })) return false;
        if ((pf & PIid) != 0 && !Table(ref r, 8, (ref NetReader x, uint k) => { if (!x.U32(out uint v)) return false; pd.InstanceIds[k] = v; return true; })) return false;
        if ((pf & PPosition) != 0 && !Table(ref r, 36, (ref NetReader x, uint k) => { if (!ObjectCreate.ReadPosition(ref x, out WorldPosition p)) return false; pd.Positions[k] = p; return true; })) return false;

        if (!r.U32(out uint vf) || !r.U32(out _)) return false;

        if ((vf & VAttribute) != 0)
        {
            if (!r.U32(out uint mask)) return false;
            for (int bit = 0; bit < 9; bit++)
            {
                if ((mask & (1u << bit)) == 0) continue;
                if (!r.U32(out uint ranks) || !r.U32(out uint init) || !r.U32(out uint xp)) return false;
                if (bit < 6)
                {
                    pd.Attributes[(uint)bit + 1] = new AttributeInfo(ranks, init, xp);
                }
                else
                {
                    if (!r.U32(out uint current)) return false;
                    pd.Vitals[(uint)bit - 5] = new VitalInfo(ranks, init, xp, current);
                }
            }
        }

        if ((vf & VSkill) != 0 && !Table(ref r, 32, (ref NetReader x, uint k) =>
            {
                if (!ReadSkillBody(ref x, k, out SkillInfo s)) return false;
                pd.Skills[k] = s;
                return true;
            })) return false;

        if ((vf & VSpell) != 0 && !Table(ref r, 8, (ref NetReader x, uint k) => { if (!x.Skip(4)) return false; pd.Spells.Add(k); return true; })) return false;

        if ((vf & VEnchantment) != 0 && !ReadRegistry(ref r, pd.Enchantments)) return false;

        // Character options.
        if (!r.U32(out pd.OptionFlags) || !r.I32(out pd.Options1)) return false;
        uint of = pd.OptionFlags;
        if ((of & OShortcut) != 0)
        {
            if (!r.ListCount(out int n, 12)) return false;
            for (int i = 0; i < n; i++)
            {
                if (!r.U32(out uint slot) || !r.U32(out uint obj) || !r.Skip(4)) return false;
                pd.Shortcuts.Add((slot, obj));
            }
        }
        if ((of & OSpellLists8) != 0)
        {
            for (int bar = 0; bar < 8; bar++)
            {
                if (!r.U32List(out uint[] ids)) return false;
                pd.SpellBars[bar].AddRange(ids);
            }
        }
        else if ((of & OMultiSpell) != 0 || (of & OExtMultiSpell) != 0)
        {
            int bars = (of & OExtMultiSpell) != 0 ? 7 : 5;
            for (int bar = 0; bar < bars; bar++)
            {
                if (!r.U32List(out uint[] ids)) return false;
                if (bar < 8) pd.SpellBars[bar].AddRange(ids);
            }
        }
        else if (!r.U32List(out uint[] single))
        {
            return false;
        }
        else
        {
            pd.SpellBars[0].AddRange(single);
        }
        if ((of & ODesiredComps) != 0 && !Table(ref r, 8, (ref NetReader x, uint k) => { if (!x.I32(out int v)) return false; pd.DesiredComponents[k] = v; return true; })) return false;
        if ((of & OSpellbookFilters) != 0 && !r.U32(out pd.SpellbookFilters)) return false;
        if ((of & OOptions2) != 0 && !r.I32(out pd.Options2)) return false;
        if ((of & (OSquelch | OTimestampFormat | OGenericQualities)) != 0)
        {
            // Never sent by ACE; their layouts aren't read here, so the lists are found from the end.
            pd.InventoryFound = FindListsFromEnd(body, r.Position, pd);
            return true;
        }

        if ((of & OGameplayOptions) == 0)
        {
            pd.InventoryFound = ReadLists(ref r, pd) && r.Remaining == 0;
            if (!pd.InventoryFound) { pd.Inventory.Clear(); pd.Equipped.Clear(); }
            return true;
        }
        pd.InventoryFound = FindListsFromEnd(body, r.Position, pd);
        return true;
    }

    internal static bool ReadSkillBody(ref NetReader x, uint id, out SkillInfo s)
    {
        s = default;
        if (!x.U16(out ushort ranks) || !x.U16(out ushort adj) || !x.U32(out uint training) || !x.U32(out uint xp)
            || !x.U32(out uint init) || !x.U32(out uint res) || !x.F64(out double last))
            return false;
        s = new SkillInfo(id, ranks, adj, training, xp, init, res, last);
        return true;
    }

    private delegate bool EntryReader(ref NetReader r, uint key);

    /// <summary>A packed hash table: u16 count, u16 buckets, then entries (u32 key + the rest).</summary>
    private static bool Table(ref NetReader r, int minEntry, EntryReader entry)
    {
        if (!r.TableCount(out int n, minEntry)) return false;
        for (int i = 0; i < n; i++)
            if (!r.U32(out uint k) || !entry(ref r, k)) return false;
        return true;
    }

    public static bool ReadEnchantment(ref NetReader r, out Enchantment e)
    {
        e = default;
        if (!r.U16(out ushort spell) || !r.U16(out ushort layer) || !r.U16(out ushort cat) || !r.U16(out ushort hasSet)
            || !r.U32(out uint power) || !r.F64(out double start) || !r.F64(out double dur) || !r.U32(out uint caster)
            || !r.F32(out float dmod) || !r.F32(out float dlim) || !r.F64(out double last)
            || !r.U32(out uint smType) || !r.U32(out uint smKey) || !r.F32(out float smVal))
            return false;
        uint set = 0;
        if (hasSet != 0 && !r.U32(out set)) return false;
        e = new Enchantment(spell, layer, cat, power, start, dur, caster, dmod, dlim, last, smType, smKey, smVal, set);
        return true;
    }

    public static bool ReadEnchantmentList(ref NetReader r, List<Enchantment> into)
    {
        if (!r.ListCount(out int n, EnchantmentSize)) return false;
        for (int i = 0; i < n; i++)
        {
            if (!ReadEnchantment(ref r, out Enchantment e)) return false;
            into.Add(e);
        }
        return true;
    }

    /// <summary>Registry: mask, then multiplicative, additive, cooldown lists, then one vitae enchantment.</summary>
    private static bool ReadRegistry(ref NetReader r, List<Enchantment> into)
    {
        if (!r.U32(out uint mask)) return false;
        if ((mask & 0x1) != 0 && !ReadEnchantmentList(ref r, into)) return false;
        if ((mask & 0x2) != 0 && !ReadEnchantmentList(ref r, into)) return false;
        if ((mask & 0x8) != 0 && !ReadEnchantmentList(ref r, into)) return false;
        if ((mask & 0x4) != 0)
        {
            if (!ReadEnchantment(ref r, out Enchantment v)) return false;
            into.Add(v);
        }
        return true;
    }

    private static bool ReadLists(ref NetReader r, PlayerDescriptionData pd)
    {
        if (!r.ListCount(out int n, 8)) return false;
        for (int i = 0; i < n; i++)
        {
            if (!r.U32(out uint id) || !r.U32(out uint type)) return false;
            pd.Inventory.Add(new PackEntry(id, type));
        }
        if (!r.ListCount(out int e, 12)) return false;
        for (int i = 0; i < e; i++)
        {
            if (!r.U32(out uint id) || !r.U32(out uint loc) || !r.U32(out uint pri)) return false;
            pd.Equipped.Add(new EquippedEntry(id, loc, pri));
        }
        return true;
    }

    private static bool FindListsFromEnd(ReadOnlySpan<byte> body, int earliest, PlayerDescriptionData pd)
    {
        int bestTotal = -1, bestI = 0, bestE = 0, bestPI = 0, bestPE = 0;
        int end = body.Length;
        for (int e = 0; ; e++)
        {
            int pE = end - 4 - 12 * e;
            if (pE < earliest + 4) break;
            if (U32(body, pE) != (uint)e || !EquippedOk(body, pE + 4, e)) continue;
            for (int i = 0; ; i++)
            {
                int pI = pE - 4 - 8 * i;
                if (pI < earliest) break;
                if (U32(body, pI) != (uint)i || !InventoryOk(body, pI + 4, i)) continue;
                if (i + e > bestTotal) { bestTotal = i + e; bestI = i; bestE = e; bestPI = pI; bestPE = pE; }
            }
        }
        if (bestTotal < 0) return false;
        for (int k = 0; k < bestI; k++)
            pd.Inventory.Add(new PackEntry(U32(body, bestPI + 4 + 8 * k), U32(body, bestPI + 8 + 8 * k)));
        for (int k = 0; k < bestE; k++)
            pd.Equipped.Add(new EquippedEntry(U32(body, bestPE + 4 + 12 * k), U32(body, bestPE + 8 + 12 * k), U32(body, bestPE + 12 + 12 * k)));
        return true;
    }

    private static uint U32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(at));

    // Object ids are far above counts, masks and types.
    private const uint MinId = 0x1000;

    private static bool InventoryOk(ReadOnlySpan<byte> b, int at, int n)
    {
        bool sawPackSlot = false;
        for (int k = 0; k < n; k++)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(at + 8 * k));
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(at + 8 * k + 4));
            if (id < MinId || type > 2) return false;
            if (type != 0) sawPackSlot = true;
            else if (sawPackSlot) return false;   // ordinary items come first
        }
        return true;
    }

    private static bool EquippedOk(ReadOnlySpan<byte> b, int at, int n)
    {
        for (int k = 0; k < n; k++)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(at + 12 * k));
            uint loc = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(at + 12 * k + 4));
            if (id < MinId || loc == 0 || (loc & 0x80000000) != 0) return false;
        }
        return true;
    }
}
