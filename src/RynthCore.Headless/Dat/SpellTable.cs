// ============================================================================
//  RynthCore.Headless - Dat/SpellTable.cs
//  The spell table (portal dat file 0x0E00000E): every spell's name, school,
//  category, flags, mana, power and duration. Layout as documented in the engine's
//  ImGui/MonsterHud/PortalSpellTable.cs (whose parse this follows): file id, then a
//  hash table of u32 id + SpellBase (obfuscated name/description strings, school,
//  icon, category, bitfield, base mana, range const/mod, power, economy mod, formula
//  version, component loss, meta type, meta id, [duration + degrade for enchantments |
//  lifetime for portal summons], 8 components, effects, recovery, display order,
//  target type, mana mod).
// ============================================================================

namespace RynthCore.Headless.Dat;

public sealed record SpellInfo(uint Id, string Name, uint School, uint Icon, uint Category, uint Flags, uint BaseMana,
    float RangeConstant, float RangeMod, uint Power, uint MetaType, double Duration)
{
    public bool Beneficial => (Flags & 0x4) != 0;
    public bool SelfTargeted => (Flags & 0x8) != 0;
    public bool IsEnchantment => MetaType is 1 or 12;
}

public sealed class SpellTable
{
    public const uint FileId = 0x0E00000E;
    private const uint MetaEnchantment = 1, MetaPortalSummon = 7, MetaFellowEnchantment = 12;

    private readonly Dictionary<uint, SpellInfo> _byId = new();
    public IReadOnlyDictionary<uint, SpellInfo> ById => _byId;

    public static SpellTable? Load(string acFolder, out string why)
    {
        byte[]? raw = DatFile.Read(Path.Combine(acFolder, "client_portal.dat"), FileId, out why);
        if (raw == null) return null;
        var t = new SpellTable();
        t.Parse(raw);
        return t;
    }

    public SpellInfo? Get(uint id) => _byId.TryGetValue(id, out SpellInfo? s) ? s : null;

    private void Parse(byte[] raw)
    {
        int p = 0;
        bool ok = true;
        uint U32() { if (p + 4 > raw.Length) { ok = false; return 0; } uint v = BitConverter.ToUInt32(raw, p); p += 4; return v; }
        float F32() { if (p + 4 > raw.Length) { ok = false; return 0; } float v = BitConverter.ToSingle(raw, p); p += 4; return v; }
        double F64() { if (p + 8 > raw.Length) { ok = false; return 0; } double v = BitConverter.ToDouble(raw, p); p += 8; return v; }
        ushort U16() { if (p + 2 > raw.Length) { ok = false; return 0; } ushort v = BitConverter.ToUInt16(raw, p); p += 2; return v; }
        void Skip(int n) { if (p + n > raw.Length) ok = false; else p += n; }
        string Str()
        {
            int n = U16();
            if (!ok || p + n > raw.Length) { ok = false; return ""; }
            var c = new char[n];
            for (int i = 0; i < n; i++) { byte x = raw[p + i]; c[i] = (char)(byte)((x >> 4) | (x << 4)); }
            p += n;
            p = (p + 3) & ~3;
            return new string(c);
        }

        U32();
        int total = U16();
        U16();
        for (int i = 0; i < total && ok; i++)
        {
            uint id = U32();
            string name = Str();
            Str();
            uint school = U32(), icon = U32(), category = U32(), flags = U32(), mana = U32();
            float rc = F32(), rm = F32();
            uint power = U32();
            Skip(12);
            uint meta = U32();
            U32();
            double duration = 0;
            if (meta == MetaEnchantment || meta == MetaFellowEnchantment) { duration = F64(); Skip(8); }
            else if (meta == MetaPortalSummon) Skip(8);
            Skip(8 * 4 + 12 + 8 + 4 + 12);
            if (!ok) break;
            _byId[id] = new SpellInfo(id, name, school, icon, category, flags, mana, rc, rm, power, meta, duration);
        }
    }
}
