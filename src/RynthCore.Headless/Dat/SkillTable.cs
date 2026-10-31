// ============================================================================
//  RynthCore.Headless - Dat/SkillTable.cs
//  The two portal-dat tables the character sheet is computed from:
//    0x0E000004 SkillTable: file id, then a packed hash table (u16 count, u16 buckets)
//      of u32 skill id + SkillBase: description and name (u16-length strings, each
//      padded to a 4-byte boundary), icon, trained cost, specialized cost, category,
//      chargen use, min level (1 = usable untrained, 2 = must be trained), the formula
//      (w, x, y, z = divisor, attr1, attr2; x == 0 = no attribute part), then upper
//      bound, lower bound and learn mod (doubles).
//    0x0E000003 SecondaryAttributeTable: file id, then the max health, max stamina and
//      max mana formulas (same 6 dwords each).
//  The retired skills (axe, bow, ... unarmed) are not in the dat; their formulas are
//  the documented retail ones.
// ============================================================================

namespace RynthCore.Headless.Dat;

/// <summary>A dat skill/vital formula: (attr1 [+ attr2]) / divisor, rounded half away from zero.</summary>
public readonly record struct SkillFormula(uint W, uint X, uint Y, uint Divisor, uint Attr1, uint Attr2)
{
    public bool HasAttributes => X != 0;

    /// <summary>The attribute contribution for the given attribute values (Strength 1 .. Self 6).</summary>
    public uint Apply(Func<uint, uint> attribute)
    {
        if (X == 0 || Attr1 == 0) return 0;
        uint total = attribute(Attr1);
        if (Attr2 != 0) total += attribute(Attr2);
        if (Divisor > 1)
            total = (uint)Math.Round((float)total / Divisor, MidpointRounding.AwayFromZero);
        return total;
    }

    public override string ToString() => X == 0 ? "none" : Attr2 != 0 ? $"(a{Attr1} + a{Attr2}) / {Divisor}" : $"a{Attr1} / {Divisor}";
}

public sealed record SkillBaseInfo(uint Id, string Name, uint Category, uint MinLevel, SkillFormula Formula)
{
    /// <summary>Untrained skills with MinLevel 1 still get the attribute part.</summary>
    public bool UsableUntrained => MinLevel == 1;
}

public sealed class SkillTable
{
    public const uint SkillFileId = 0x0E000004;
    public const uint VitalFileId = 0x0E000003;

    private readonly Dictionary<uint, SkillBaseInfo> _skills = new();
    public IReadOnlyDictionary<uint, SkillBaseInfo> Skills => _skills;

    /// <summary>Max health, max stamina, max mana formulas.</summary>
    public SkillFormula MaxHealth { get; private set; }
    public SkillFormula MaxStamina { get; private set; }
    public SkillFormula MaxMana { get; private set; }

    public SkillBaseInfo? Get(uint skill) => _skills.TryGetValue(skill, out SkillBaseInfo? s) ? s : null;

    /// <summary>The formula for a vital: 1 Health, 2 Stamina, 3 Mana.</summary>
    public SkillFormula VitalFormula(uint vital) => vital switch { 1 => MaxHealth, 2 => MaxStamina, _ => MaxMana };

    public static SkillTable? Load(string acFolder, out string why)
    {
        string portal = Path.Combine(acFolder, "client_portal.dat");
        byte[]? skills = DatFile.Read(portal, SkillFileId, out why);
        if (skills == null) return null;
        byte[]? vitals = DatFile.Read(portal, VitalFileId, out why);
        if (vitals == null) return null;
        var t = new SkillTable();
        if (!t.ParseSkills(skills)) { why = "skill table did not parse"; return null; }
        if (!t.ParseVitals(vitals)) { why = "secondary attribute table did not parse"; return null; }
        t.AddRetired();
        why = "";
        return t;
    }

    private sealed class Reader
    {
        private readonly byte[] _b;
        public int P;
        public bool Ok = true;
        public Reader(byte[] b) => _b = b;
        public uint U32() { if (P + 4 > _b.Length) { Ok = false; return 0; } uint v = BitConverter.ToUInt32(_b, P); P += 4; return v; }
        public ushort U16() { if (P + 2 > _b.Length) { Ok = false; return 0; } ushort v = BitConverter.ToUInt16(_b, P); P += 2; return v; }
        public double F64() { if (P + 8 > _b.Length) { Ok = false; return 0; } double v = BitConverter.ToDouble(_b, P); P += 8; return v; }
        public string Str()
        {
            int n = U16();
            if (!Ok || P + n > _b.Length) { Ok = false; return ""; }
            string s = System.Text.Encoding.Latin1.GetString(_b, P, n);
            P += n;
            P = (P + 3) & ~3;
            return s;
        }
        public SkillFormula Formula() => new(U32(), U32(), U32(), U32(), U32(), U32());
    }

    private bool ParseSkills(byte[] raw)
    {
        var r = new Reader(raw);
        r.U32();                 // file id
        int count = r.U16();
        r.U16();                 // buckets
        for (int i = 0; i < count && r.Ok; i++)
        {
            uint id = r.U32();
            r.Str();             // description
            string name = r.Str();
            r.U32(); r.U32(); r.U32();    // icon, trained cost, specialized cost
            uint category = r.U32();
            r.U32();             // chargen use
            uint minLevel = r.U32();
            SkillFormula f = r.Formula();
            r.F64(); r.F64(); r.F64();    // upper bound, lower bound, learn mod
            if (r.Ok) _skills[id] = new SkillBaseInfo(id, name, category, minLevel, f);
        }
        return r.Ok && _skills.Count > 0;
    }

    private bool ParseVitals(byte[] raw)
    {
        var r = new Reader(raw);
        r.U32();
        MaxHealth = r.Formula();
        MaxStamina = r.Formula();
        MaxMana = r.Formula();
        return r.Ok;
    }

    private void AddRetired()
    {
        // Strength 1, Endurance 2, Quickness 3, Coordination 4, Focus 5, Self 6.
        void Add(uint id, string name, uint a1, uint a2, uint div)
        {
            if (!_skills.ContainsKey(id))
                _skills[id] = new SkillBaseInfo(id, name, 1, 2, new SkillFormula(0, 1, 0, div, a1, a2));
        }
        Add(1, "Axe", 1, 4, 3);
        Add(2, "Bow", 4, 0, 2);
        Add(3, "Crossbow", 4, 0, 2);
        Add(4, "Dagger", 3, 4, 3);
        Add(5, "Mace", 1, 4, 3);
        Add(9, "Spear", 1, 4, 3);
        Add(10, "Staff", 1, 4, 3);
        Add(11, "Sword", 1, 4, 3);
        Add(12, "Thrown Weapons", 4, 0, 2);
        Add(13, "Unarmed Combat", 1, 4, 3);
    }

    // One table per process: the portal dat doesn't change while we run.
    private static readonly Lazy<SkillTable?> _shared = new(() =>
    {
        try
        {
            string? ac = DatFile.FindAcFolder();
            return ac != null ? Load(ac, out _) : null;
        }
        catch
        {
            return null;
        }
    });

    /// <summary>The table from the usual AC folder (null when the dats aren't there).</summary>
    public static SkillTable? Shared => _shared.Value;
}
