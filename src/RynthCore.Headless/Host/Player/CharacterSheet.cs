// ============================================================================
//  RynthCore.Headless - Host/Player/CharacterSheet.cs
//  The player's attributes, vitals and skills as the client computes them: base
//  (unenchanted) and buffed (enchanted) values, from what PlayerDescription and the
//  private updates told us plus the portal dat formulas (Dat/SkillTable.cs).
//
//  The rules (as ACE implements the retail ones; facts only, our own code):
//    attribute base   = init + ranks
//    attribute buffed = round(base * Π mult + Σ (int)add), at least 10 (1 if base < 10)
//    vital base max   = init + ranks + formula(base attributes)
//                       (+ enlightenment * 2 + gear max health, health only)
//    vital buffed max = round((init + ranks + formula(BUFFED attributes) [+ health extras])
//                       * Π mult * vitae + Σ add), at least 5 (1 if that total < 5)
//    skill base       = [usable] formula(base attributes) + init + ranks + aug base bonus
//    skill buffed     = round(([usable] formula(BUFFED attributes) + init + ranks + aug base)
//                       * Π mult * vitae + aug current bonus + Σ (int)add + attack/defense
//                       debuffs), at least 0
//  "usable": trained / specialized, or untrained with the skill's dat MinLevel == 1.
//  Enchantments that count: per spell category only the top layer (highest power level,
//  then the level-8 aura self spells, then the newest / for set spells the highest id),
//  among those whose StatModType has the stat-kind and the multiplicative/additive bit
//  plus SingleStat with the stat's key, or MultipleStat with key 0 (not vitae).
//  Vitae (spell 666) is its own multiplier: StatModValue, 1.0 = none.
// ============================================================================

using RynthCore.Headless.Dat;
using RynthCore.Headless.Messages;
using RynthCore.Headless.World;

namespace RynthCore.Headless.Host.Player;

internal sealed class CharacterSheet
{
    // EnchantmentTypeFlags
    public const uint Attribute = 0x1, SecondAtt = 0x2, Skill = 0x10, SingleStat = 0x1000, MultipleStat = 0x2000,
        Multiplicative = 0x4000, Additive = 0x8000, AttackSkills = 0x10000, DefenseSkills = 0x20000,
        VitaeFlag = 0x800000, Cooldown = 0x1000000;

    public const ushort VitaeSpell = 666;

    // PropertyInt
    private const uint AugSkilledMelee = 300, AugSkilledMissile = 301, AugSkilledMagic = 302, AugJackOfAllTrades = 326,
        LumAugSkilledSpec = 344, LumAugAllSkills = 365, GearMaxHealth = 379, Enlightenment = 390;

    // Blood Drinker / Defender / Heart Seeker / Spirit Drinker / Swift Killer / Hermetic Link Self VIII.
    private static readonly HashSet<uint> Level8AuraSelf = new() { 4395, 4400, 4405, 4414, 4417, 4418 };

    private static readonly HashSet<uint> MeleeSkills = new() { 45, 44, 46, 49, 41, 1, 4, 5, 9, 10, 11, 13 };
    private static readonly HashSet<uint> MissileSkills = new() { 47, 2, 3, 8, 12 };
    private static readonly HashSet<uint> MagicSkills = new() { 31, 32, 33, 43, 34 };
    private static readonly HashSet<uint> AttackSkillSet = new() { 1, 2, 3, 4, 5, 8, 9, 10, 11, 12, 13, 46, 44, 45, 47, 41, 34, 33, 43, 49 };
    private static readonly HashSet<uint> DefenseSkillSet = new() { 6, 7, 15, 48 };

    private readonly PlayerState _p;
    private readonly WorldState _world;
    private readonly SkillTable? _table;
    private readonly List<(Enchantment E, double Start)> _ench;

    /// <param name="startOf">The absolute (server-time) start of an enchantment, for the newest-layer tie-break.</param>
    public CharacterSheet(WorldState world, SkillTable? table, Func<Enchantment, double> startOf)
    {
        _world = world;
        _p = world.Player;
        _table = table;
        _ench = new List<(Enchantment, double)>(_p.Enchantments.Count);
        foreach (Enchantment e in _p.Enchantments) _ench.Add((e, startOf(e)));
    }

    // ── properties ──────────────────────────────────────────────────────────

    private int Int(uint key)
    {
        WorldObject? me = _world.PlayerObject;
        if (me != null && me.Ints.TryGetValue(key, out int v)) return v;
        return _p.Description != null && _p.Description.Ints.TryGetValue(key, out int d) ? d : 0;
    }

    /// <summary>1.0 = no vitae penalty, 0.95 = 5 %.</summary>
    public float Vitae
    {
        get
        {
            foreach ((Enchantment e, _) in _ench)
                if (e.SpellId == VitaeSpell) return e.StatModValue;
            return 1.0f;
        }
    }

    // ── enchantments ────────────────────────────────────────────────────────

    /// <summary>The top layer per spell category among the enchantments that apply to (type, key).</summary>
    public List<Enchantment> TopLayer(uint statModType, uint statModKey, bool handleMultiple)
    {
        uint multiple = 0;
        if (handleMultiple)
        {
            multiple = statModType | MultipleStat;
            statModType |= SingleStat;
        }
        var best = new Dictionary<ushort, (Enchantment E, double Start)>();
        foreach ((Enchantment e, double start) in _ench)
        {
            if (e.SpellId == VitaeSpell) continue;
            uint t = e.StatModType;
            bool single = (t & statModType) == statModType && e.StatModKey == statModKey;
            bool multi = handleMultiple && (t & multiple) == multiple && (t & VitaeFlag) == 0 && e.StatModKey == 0;
            if (!single && !multi) continue;
            if (!best.TryGetValue(e.Category, out var cur) || Beats(e, start, cur.E, cur.Start))
                best[e.Category] = (e, start);
        }
        return best.Values.Select(x => x.E).ToList();
    }

    private static bool Beats(Enchantment a, double aStart, Enchantment b, double bStart)
    {
        if (a.PowerLevel != b.PowerLevel) return a.PowerLevel > b.PowerLevel;
        bool a8 = Level8AuraSelf.Contains(a.SpellId), b8 = Level8AuraSelf.Contains(b.SpellId);
        if (a8 != b8) return a8;
        double ka = a.SpellSetId != 0 ? a.SpellId : aStart;
        double kb = b.SpellSetId != 0 ? b.SpellId : bStart;
        return ka > kb;
    }

    private float Multiplier(uint kind, uint key)
    {
        float m = 1.0f;
        foreach (Enchantment e in TopLayer(kind | Multiplicative, key, true)) m *= e.StatModValue;
        return m;
    }

    private int IntAdditive(uint kind, uint key)
    {
        int a = 0;
        foreach (Enchantment e in TopLayer(kind | Additive, key, true)) a += (int)e.StatModValue;
        return a;
    }

    private float FloatAdditive(uint kind, uint key)
    {
        float a = 0;
        foreach (Enchantment e in TopLayer(kind | Additive, key, true)) a += e.StatModValue;
        return a;
    }

    private static int RoundAway(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);

    // ── attributes ──────────────────────────────────────────────────────────

    public bool HasAttribute(uint a) => _p.Attributes.ContainsKey(a);

    /// <summary>Strength 1 .. Self 6: init + ranks.</summary>
    public uint AttributeBase(uint a) => _p.Attributes.TryGetValue(a, out AttributeInfo i) ? i.Init + i.Ranks : 0;

    public uint AttributeBuffed(uint a)
    {
        uint b = AttributeBase(a);
        float total = (int)b * Multiplier(Attribute, a) + IntAdditive(Attribute, a);
        int r = RoundAway(total);
        int min = b >= 10 ? 10 : 1;
        return (uint)Math.Max(min, r);
    }

    // ── vitals (1 Health, 2 Stamina, 3 Mana) ────────────────────────────────

    /// <summary>The Attribute2nd max key the enchantments use: MaxHealth 1, MaxStamina 3, MaxMana 5.</summary>
    private static uint MaxKey(uint vital) => vital * 2 - 1;

    private SkillFormula VitalFormula(uint vital)
        => _table?.VitalFormula(vital) ?? vital switch
        {
            1 => new SkillFormula(0, 1, 0, 2, 2, 0),     // Endurance / 2
            2 => new SkillFormula(0, 1, 0, 1, 2, 0),     // Endurance
            _ => new SkillFormula(0, 1, 0, 1, 6, 0),     // Self
        };

    private uint HealthExtras()
    {
        long extra = Int(Enlightenment) * 2L;
        foreach (uint item in _p.Equipped.Keys)
            if (_world.Get(item) is { } o && o.Ints.TryGetValue(GearMaxHealth, out int g)) extra += g;
        return (uint)Math.Max(0, extra);
    }

    private uint VitalTotal(uint vital, bool buffedAttributes)
    {
        if (!_p.Vitals.TryGetValue(vital, out VitalInfo v)) return 0;
        uint attr = VitalFormula(vital).Apply(buffedAttributes ? AttributeBuffed : AttributeBase);
        uint total = v.Init + v.Ranks + attr;
        if (vital == 1) total += HealthExtras();
        return total;
    }

    public bool HasVital(uint vital) => _p.Vitals.ContainsKey(vital);

    public uint VitalCurrent(uint vital) => _p.Vitals.TryGetValue(vital, out VitalInfo v) ? v.Current : 0;

    /// <summary>Unbuffed maximum: no enchantments, no vitae.</summary>
    public uint VitalBaseMax(uint vital) => VitalTotal(vital, buffedAttributes: false);

    /// <summary>The maximum the client shows: buffed attributes, enchantments and vitae.</summary>
    public uint VitalBuffedMax(uint vital)
    {
        if (!_p.Vitals.ContainsKey(vital)) return 0;
        uint total = VitalTotal(vital, buffedAttributes: true);
        uint key = MaxKey(vital);
        float f = total * Multiplier(SecondAtt, key);
        float vitae = Vitae;
        if (vitae != 1.0f) f *= vitae;
        int r = RoundAway(f + FloatAdditive(SecondAtt, key));
        int min = total >= 5 ? 5 : 1;
        return (uint)Math.Max(min, r);
    }

    // ── skills ──────────────────────────────────────────────────────────────

    public bool TryGetSkill(uint id, out SkillInfo s) => _p.Skills.TryGetValue(id, out s);

    private bool IsUsable(in SkillInfo s)
    {
        if (s.Training >= 2) return true;
        if (s.Training == 1) return _table?.Get(s.Id)?.UsableUntrained ?? false;
        return false;
    }

    private uint AugBase(in SkillInfo s)
    {
        long total = Int(LumAugAllSkills);
        if (Int(AugSkilledMelee) > 0 && MeleeSkills.Contains(s.Id)) total += Int(AugSkilledMelee) * 10L;
        else if (Int(AugSkilledMissile) > 0 && MissileSkills.Contains(s.Id)) total += Int(AugSkilledMissile) * 10L;
        else if (Int(AugSkilledMagic) > 0 && MagicSkills.Contains(s.Id)) total += Int(AugSkilledMagic) * 10L;
        if (s.Training >= 2 && Int(Enlightenment) != 0) total += Int(Enlightenment);
        return (uint)Math.Max(0, total);
    }

    private uint AugCurrent(in SkillInfo s)
    {
        long total = 0;
        if (Int(AugJackOfAllTrades) != 0) total += Int(AugJackOfAllTrades) * 5L;
        if (s.Training == 3 && Int(LumAugSkilledSpec) != 0) total += Int(LumAugSkilledSpec) * 2L;
        return (uint)Math.Max(0, total);
    }

    private uint SkillFormulaPart(in SkillInfo s, bool buffedAttributes)
    {
        if (!IsUsable(s)) return 0;
        SkillFormula? f = _table?.Get(s.Id)?.Formula;
        return f?.Apply(buffedAttributes ? AttributeBuffed : AttributeBase) ?? 0;
    }

    /// <summary>The unenchanted level: formula(base attributes) + init + ranks + augmentations.</summary>
    public int SkillBase(in SkillInfo s) => (int)(SkillFormulaPart(s, false) + s.Init + s.Ranks + AugBase(s));

    /// <summary>The level the client shows: buffed attributes, enchantments, vitae, augmentations.</summary>
    public int SkillBuffed(in SkillInfo s)
    {
        uint total = SkillFormulaPart(s, true) + s.Init + s.Ranks + AugBase(s);
        float f = total * Multiplier(Skill, s.Id);
        float vitae = Vitae;
        if (vitae != 1.0f) f *= vitae;
        f += AugCurrent(s);
        int additives = IntAdditive(Skill, s.Id);
        if (DefenseSkillSet.Contains(s.Id)) additives += Debuff(Skill | Additive | DefenseSkills);
        if (AttackSkillSet.Contains(s.Id)) additives += Debuff(Skill | Additive | AttackSkills);
        return Math.Max(0, RoundAway(f + additives));
    }

    private int Debuff(uint type)
    {
        float a = 0;
        foreach (Enchantment e in TopLayer(type, 0, false)) a += e.StatModValue;
        return (int)Math.Round(a);
    }
}
