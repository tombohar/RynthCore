// ============================================================================
//  RynthCore.Engine - Compatibility/InventoryAttributeFilter.cs
//  The Inventory panel's attribute filter: rules on an item's attributes
//  ("Armor Level >= 300", "Spells contain Legendary", "Fits slot Neck"),
//  matched all or any. Pure: no AC reads. The panel supplies each item's
//  values through IInventoryFilterFacts (InventoryFilterFacts.cs), so the host
//  tests link this file alone.
//
//  Two kinds of attribute:
//    public     what every item sends (value, burden, material, slots...)
//    appraised  what only an identify returns (armor level, ratings, spells...).
//               An item never identified can't be judged on these: Unknown,
//               which the panel hides and counts ("N not identified").
//  An appraised attribute the item doesn't have counts as 0 / no / "" (a
//  weapon's armor level is 0), so "Level to wield <= 150" keeps items with no
//  requirement.
//
//  Threads: the panel's (render) thread only; the rules are edited and read
//  there. Saved as one line in inventory_settings.txt (Serialize / Parse).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RynthCore.Engine.Compatibility;

/// <summary>How a rule compares the attribute.</summary>
internal enum FilterAttrKind : byte { Number, Flag, Text, Choice }

/// <summary>Where the value comes from and how it is turned into what the rule shows.</summary>
internal enum FilterSource : byte
{
    /// <summary>From the item's public description (the panel's InventoryItem).</summary>
    Public,
    /// <summary>An appraised int property (0 when absent).</summary>
    Int,
    /// <summary>An appraised float property, shown as is.</summary>
    Float,
    /// <summary>An appraised float multiplier shown as a bonus percent: 1.15 -> 15.</summary>
    FloatModPercent,
    /// <summary>An appraised float fraction shown as a percent: 0.05 -> 5.</summary>
    FloatPercent,
    /// <summary>An appraised int that counts as "yes" when not 0.</summary>
    IntNonZero,
    /// <summary>An appraised bool property.</summary>
    Bool,
    /// <summary>Worked out by the facts provider (level to wield, set name, spells).</summary>
    Special,
}

/// <summary>Every attribute a rule can test. The persisted key is <see cref="FilterAttrInfo.Key"/>, not the number.</summary>
internal enum FilterAttr : byte
{
    // Public
    Name, Value, Burden, Workmanship, Stack, Material, Slot, Equipped, CastsSpell,
    // Appraised
    ArmorLevel, MaxDamage, Speed, AttackBonus, MeleeDefense, MissileDefense, MagicDefense,
    ElementalDamage, ManaConversion, DamageRating, DamageResistRating, CritRating, CritResistRating,
    CritDamageRating, CritDamageResistRating, HealingBoostRating, VitalityRating, WieldLevel,
    ArcaneLore, Spellcraft, MaxMana, Tinkers, MaxItemLevel, SetName, Spells, Imbued, Bonded,
    Attuned, Retained,
}

/// <summary>One attribute's description: its key, label, kind and where its value lives.</summary>
internal sealed class FilterAttrInfo
{
    public FilterAttr Id;
    public string Key = string.Empty;
    public string Label = string.Empty;
    public FilterAttrKind Kind;
    public FilterSource Source;
    /// <summary>The appraisal property id (STypeInt / STypeFloat / STypeBool); 0 for public and special.</summary>
    public uint Stype;
    public string Tip = string.Empty;
    /// <summary>Needs an identify (any source but Public).</summary>
    public bool NeedsId => Source != FilterSource.Public;
}

/// <summary>How a rule tests its attribute. The persisted key is in <see cref="InventoryAttributeFilter.OpKey"/>.</summary>
internal enum FilterOp : byte { AtLeast, AtMost, Equal, NotEqual, Yes, No, Contains, NotContains, Is, IsNot }

/// <summary>What one rule (or the whole filter) says about an item.</summary>
internal enum FilterVerdict : byte { Match, NoMatch, Unknown }

/// <summary>One item's values as a rule sees them. Absent values are 0 / false / "".</summary>
internal interface IInventoryFilterFacts
{
    /// <summary>The server's identify answer is cached for this item.</summary>
    bool Identified { get; }
    double Number(FilterAttrInfo attr);
    bool Flag(FilterAttrInfo attr);
    string Text(FilterAttrInfo attr);
    /// <summary>The item's ValidLocations (EquipMask) bits, for the Slot choice.</summary>
    uint SlotMask { get; }
}

/// <summary>One rule. Mutable: the panel's editor changes it in place and bumps the filter's version.</summary>
internal sealed class InventoryFilterRule
{
    public FilterAttr Attr;
    public FilterOp Op;
    public double Number;
    public string Text = string.Empty;
    /// <summary>The choice's index (Slot: into <see cref="InventoryAttributeFilter.SlotNames"/>).</summary>
    public int Choice;

    public InventoryFilterRule Clone() => new() { Attr = Attr, Op = Op, Number = Number, Text = Text, Choice = Choice };
}

/// <summary>The rule list, its match mode and on/off; evaluation, description and persistence.</summary>
internal sealed class InventoryAttributeFilter
{
    public const int MaxRules = 12;

    /// <summary>Off keeps the rules but shows every item.</summary>
    public bool Enabled;
    /// <summary>True: any rule matching keeps the item. False: every rule must.</summary>
    public bool MatchAny;
    public readonly List<InventoryFilterRule> Rules = new();
    /// <summary>Bumped on every edit, so the panel knows to rebuild its list.</summary>
    public int Version { get; private set; }

    /// <summary>On, with at least one rule.</summary>
    public bool Active => Enabled && Rules.Count > 0;

    /// <summary>Some rule needs an identify (so unknown items can exist and appraisals change matches).</summary>
    public bool NeedsIdentify
    {
        get
        {
            foreach (InventoryFilterRule r in Rules)
                if (Info(r.Attr).NeedsId) return true;
            return false;
        }
    }

    public void Touch() => Version++;

    // ── Attributes ──────────────────────────────────────────────────────

    public static readonly FilterAttrInfo[] Attributes = BuildAttributes();

    private static FilterAttrInfo[] BuildAttributes()
    {
        var list = new FilterAttrInfo[(int)FilterAttr.Retained + 1];
        void Add(FilterAttr id, string key, string label, FilterAttrKind kind, FilterSource src, uint stype, string tip) =>
            list[(int)id] = new FilterAttrInfo { Id = id, Key = key, Label = label, Kind = kind, Source = src, Stype = stype, Tip = tip };

        Add(FilterAttr.Name, "name", "Name", FilterAttrKind.Text, FilterSource.Public, 0, "The item's name.");
        Add(FilterAttr.Value, "value", "Value", FilterAttrKind.Number, FilterSource.Public, 0, "Pyreal value.");
        Add(FilterAttr.Burden, "burden", "Burden", FilterAttrKind.Number, FilterSource.Public, 0, "Burden units (a stack's total).");
        Add(FilterAttr.Workmanship, "work", "Workmanship", FilterAttrKind.Number, FilterSource.Public, 0, "1 to 10; 0 for items without one.");
        Add(FilterAttr.Stack, "stack", "Stack size", FilterAttrKind.Number, FilterSource.Public, 0, "1 for a single item.");
        Add(FilterAttr.Material, "material", "Material", FilterAttrKind.Text, FilterSource.Public, 0, "Steel, Silk, Ruby...");
        Add(FilterAttr.Slot, "slot", "Fits slot", FilterAttrKind.Choice, FilterSource.Public, 0, "Where the item can be worn or wielded.");
        Add(FilterAttr.Equipped, "equipped", "Equipped", FilterAttrKind.Flag, FilterSource.Public, 0, "Worn or wielded now.");
        Add(FilterAttr.CastsSpell, "casts", "Casts a spell", FilterAttrKind.Flag, FilterSource.Public, 0, "Has a use spell (scrolls, gems, wands).");

        Add(FilterAttr.ArmorLevel, "al", "Armor level", FilterAttrKind.Number, FilterSource.Int, 28, "Base armor level.");
        Add(FilterAttr.MaxDamage, "dmg", "Max damage", FilterAttrKind.Number, FilterSource.Int, 44, "A weapon's maximum damage.");
        Add(FilterAttr.Speed, "speed", "Speed", FilterAttrKind.Number, FilterSource.Int, 49, "Weapon speed (lower is faster).");
        Add(FilterAttr.AttackBonus, "attack", "Attack bonus %", FilterAttrKind.Number, FilterSource.FloatModPercent, 62, "A weapon's attack skill bonus.");
        Add(FilterAttr.MeleeDefense, "meleed", "Melee defense %", FilterAttrKind.Number, FilterSource.FloatModPercent, 29, "Melee defense bonus.");
        Add(FilterAttr.MissileDefense, "missiled", "Missile defense %", FilterAttrKind.Number, FilterSource.FloatModPercent, 149, "Missile defense bonus.");
        Add(FilterAttr.MagicDefense, "magicd", "Magic defense %", FilterAttrKind.Number, FilterSource.FloatModPercent, 150, "Magic defense bonus.");
        Add(FilterAttr.ElementalDamage, "elem", "Elemental damage %", FilterAttrKind.Number, FilterSource.FloatModPercent, 152, "A caster's elemental damage bonus.");
        Add(FilterAttr.ManaConversion, "manac", "Mana conversion %", FilterAttrKind.Number, FilterSource.FloatPercent, 144, "A caster's mana conversion bonus.");
        Add(FilterAttr.DamageRating, "dr", "Damage rating", FilterAttrKind.Number, FilterSource.Int, 370, "Gear damage rating.");
        Add(FilterAttr.DamageResistRating, "drr", "Damage resist rating", FilterAttrKind.Number, FilterSource.Int, 371, "Gear damage resistance rating.");
        Add(FilterAttr.CritRating, "cr", "Crit rating", FilterAttrKind.Number, FilterSource.Int, 372, "Gear critical rating.");
        Add(FilterAttr.CritResistRating, "crr", "Crit resist rating", FilterAttrKind.Number, FilterSource.Int, 373, "Gear critical resistance rating.");
        Add(FilterAttr.CritDamageRating, "cdr", "Crit damage rating", FilterAttrKind.Number, FilterSource.Int, 374, "Gear critical damage rating.");
        Add(FilterAttr.CritDamageResistRating, "cdrr", "Crit damage resist rating", FilterAttrKind.Number, FilterSource.Int, 375, "Gear critical damage resistance rating.");
        Add(FilterAttr.HealingBoostRating, "heal", "Healing boost rating", FilterAttrKind.Number, FilterSource.Int, 376, "Gear healing boost rating.");
        Add(FilterAttr.VitalityRating, "vit", "Vitality rating", FilterAttrKind.Number, FilterSource.Int, 379, "Gear max health (vitality) rating.");
        Add(FilterAttr.WieldLevel, "wieldlvl", "Level to wield", FilterAttrKind.Number, FilterSource.Special, 0, "Character level needed; 0 when none.");
        Add(FilterAttr.ArcaneLore, "lore", "Arcane lore", FilterAttrKind.Number, FilterSource.Int, 109, "Arcane lore needed to use it.");
        Add(FilterAttr.Spellcraft, "spellcraft", "Spellcraft", FilterAttrKind.Number, FilterSource.Int, 106, "The item's spellcraft.");
        Add(FilterAttr.MaxMana, "mana", "Max mana", FilterAttrKind.Number, FilterSource.Int, 108, "The item's mana pool.");
        Add(FilterAttr.Tinkers, "tinks", "Times tinkered", FilterAttrKind.Number, FilterSource.Int, 171, "Tinkers applied so far.");
        Add(FilterAttr.MaxItemLevel, "maxlvl", "Max item level", FilterAttrKind.Number, FilterSource.Int, 319, "Aetheria / cloak / leveling items.");
        Add(FilterAttr.SetName, "set", "Armor set", FilterAttrKind.Text, FilterSource.Special, 265, "Soldier's, Adept's, Cloak War Magic...");
        Add(FilterAttr.Spells, "spells", "Spells", FilterAttrKind.Text, FilterSource.Special, 0, "Any of the item's spells contains the text.");
        Add(FilterAttr.Imbued, "imbued", "Imbued", FilterAttrKind.Flag, FilterSource.IntNonZero, 179, "Has an imbue (crushing blow, armor rend...).");
        Add(FilterAttr.Bonded, "bonded", "Bonded", FilterAttrKind.Flag, FilterSource.IntNonZero, 33, "Bonded, sticky or destroyed on drop.");
        Add(FilterAttr.Attuned, "attuned", "Attuned", FilterAttrKind.Flag, FilterSource.IntNonZero, 114, "Can't be given away.");
        Add(FilterAttr.Retained, "retained", "Retained", FilterAttrKind.Flag, FilterSource.Bool, 91, "Won't be sold or salvaged by accident.");
        return list;
    }

    public static FilterAttrInfo Info(FilterAttr a) => Attributes[(int)a];

    public static FilterAttrInfo? InfoByKey(string key)
    {
        foreach (FilterAttrInfo i in Attributes)
            if (string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase)) return i;
        return null;
    }

    // ── Operators ───────────────────────────────────────────────────────

    private static readonly FilterOp[] NumberOps = { FilterOp.AtLeast, FilterOp.AtMost, FilterOp.Equal, FilterOp.NotEqual };
    private static readonly FilterOp[] FlagOps = { FilterOp.Yes, FilterOp.No };
    private static readonly FilterOp[] TextOps = { FilterOp.Contains, FilterOp.NotContains };
    private static readonly FilterOp[] ChoiceOps = { FilterOp.Is, FilterOp.IsNot };

    /// <summary>The operators that make sense for an attribute kind; the first is the default.</summary>
    public static FilterOp[] OpsFor(FilterAttrKind kind) => kind switch
    {
        FilterAttrKind.Number => NumberOps,
        FilterAttrKind.Flag => FlagOps,
        FilterAttrKind.Text => TextOps,
        _ => ChoiceOps,
    };

    public static string OpLabel(FilterOp op) => op switch
    {
        FilterOp.AtLeast => ">=",
        FilterOp.AtMost => "<=",
        FilterOp.Equal => "=",
        FilterOp.NotEqual => "!=",
        FilterOp.Yes => "yes",
        FilterOp.No => "no",
        FilterOp.Contains => "contains",
        FilterOp.NotContains => "doesn't contain",
        FilterOp.Is => "is",
        _ => "is not",
    };

    public static string OpKey(FilterOp op) => op switch
    {
        FilterOp.AtLeast => "ge",
        FilterOp.AtMost => "le",
        FilterOp.Equal => "eq",
        FilterOp.NotEqual => "ne",
        FilterOp.Yes => "yes",
        FilterOp.No => "no",
        FilterOp.Contains => "has",
        FilterOp.NotContains => "nothas",
        FilterOp.Is => "is",
        _ => "isnot",
    };

    private static bool TryOpFromKey(string key, out FilterOp op)
    {
        for (FilterOp o = FilterOp.AtLeast; o <= FilterOp.IsNot; o++)
            if (OpKey(o) == key) { op = o; return true; }
        op = default;
        return false;
    }

    /// <summary>A new rule on <paramref name="attr"/> with that attribute's default operator.</summary>
    public static InventoryFilterRule NewRule(FilterAttr attr) => new() { Attr = attr, Op = OpsFor(Info(attr).Kind)[0] };

    /// <summary>Points <paramref name="rule"/> at another attribute, keeping its operator when it still fits.</summary>
    public static void SetAttr(InventoryFilterRule rule, FilterAttr attr)
    {
        rule.Attr = attr;
        if (Array.IndexOf(OpsFor(Info(attr).Kind), rule.Op) < 0) rule.Op = OpsFor(Info(attr).Kind)[0];
        if (Info(attr).Kind == FilterAttrKind.Choice) rule.Choice = Math.Clamp(rule.Choice, 0, SlotNames.Length - 1);
    }

    // ── Evaluation ──────────────────────────────────────────────────────

    /// <summary>
    /// The whole filter on one item. All: any NoMatch wins, then any Unknown. Any: any Match
    /// wins, then any Unknown. Off or empty: Match.
    /// </summary>
    public FilterVerdict Evaluate(IInventoryFilterFacts facts)
    {
        if (!Active) return FilterVerdict.Match;
        bool unknown = false;
        foreach (InventoryFilterRule r in Rules)
        {
            FilterVerdict v = EvaluateRule(r, facts);
            if (v == FilterVerdict.Unknown) unknown = true;
            else if (MatchAny && v == FilterVerdict.Match) return FilterVerdict.Match;
            else if (!MatchAny && v == FilterVerdict.NoMatch) return FilterVerdict.NoMatch;
        }
        if (unknown) return FilterVerdict.Unknown;
        return MatchAny ? FilterVerdict.NoMatch : FilterVerdict.Match;
    }

    /// <summary>One rule on one item. Unknown when it needs an identify the item doesn't have.</summary>
    public static FilterVerdict EvaluateRule(InventoryFilterRule rule, IInventoryFilterFacts facts)
    {
        FilterAttrInfo info = Info(rule.Attr);
        // A text rule with no text tests nothing: it never hides an item.
        if (info.Kind == FilterAttrKind.Text && rule.Text.Length == 0) return FilterVerdict.Match;
        if (info.NeedsId && !facts.Identified) return FilterVerdict.Unknown;
        bool ok = info.Kind switch
        {
            FilterAttrKind.Number => CompareNumber(facts.Number(info), rule.Op, rule.Number),
            FilterAttrKind.Flag => facts.Flag(info) == (rule.Op == FilterOp.Yes),
            FilterAttrKind.Text => facts.Text(info).Contains(rule.Text, StringComparison.OrdinalIgnoreCase) == (rule.Op == FilterOp.Contains),
            _ => FitsSlot(facts.SlotMask, rule.Choice) == (rule.Op == FilterOp.Is),
        };
        return ok ? FilterVerdict.Match : FilterVerdict.NoMatch;
    }

    /// <summary>
    /// Number comparison. Equal allows half a hundredth either way: percents come from floats
    /// (1.15 -> 14.999...) and are typed to two places at most.
    /// </summary>
    public static bool CompareNumber(double value, FilterOp op, double target)
    {
        const double Eps = 1e-6, EqTol = 0.005;
        return op switch
        {
            FilterOp.AtLeast => value >= target - Eps,
            FilterOp.AtMost => value <= target + Eps,
            FilterOp.Equal => Math.Abs(value - target) < EqTol,
            FilterOp.NotEqual => Math.Abs(value - target) >= EqTol,
            _ => false,
        };
    }

    /// <summary>A raw appraised float as the rule shows it.</summary>
    public static double Scale(FilterSource source, double raw) => source switch
    {
        FilterSource.FloatModPercent => raw == 0 ? 0 : (raw - 1.0) * 100.0,
        FilterSource.FloatPercent => raw * 100.0,
        _ => raw,
    };

    /// <summary>
    /// The level needed to wield: WieldRequirements (158) or WieldRequirements2 (270) = 7
    /// (Level) gives WieldDifficulty (160) or WieldDifficulty2 (271). 0 when neither is a level.
    /// </summary>
    public static int WieldLevel(int req1, int diff1, int req2, int diff2) =>
        req1 == WieldRequirementLevel ? diff1 : req2 == WieldRequirementLevel ? diff2 : 0;

    public const int WieldRequirementLevel = 7;
    public const uint StypeWieldRequirements = 158, StypeWieldDifficulty = 160,
        StypeWieldRequirements2 = 270, StypeWieldDifficulty2 = 271;

    // ── Slots ───────────────────────────────────────────────────────────

    public static readonly string[] SlotNames =
    {
        "Head", "Chest", "Abdomen", "Upper arms", "Lower arms", "Hands", "Upper legs", "Lower legs", "Feet",
        "Neck", "Wrist", "Finger", "Melee weapon", "Shield", "Missile weapon", "Ammunition", "Held (wand, orb)",
        "Two-handed weapon", "Trinket", "Cloak", "Aetheria",
    };

    /// <summary>EquipMask bits per <see cref="SlotNames"/> entry (armor and underclothes share a body part).</summary>
    public static readonly uint[] SlotMasks =
    {
        0x00000001, 0x00000002 | 0x00000200, 0x00000004 | 0x00000400, 0x00000008 | 0x00000800,
        0x00000010 | 0x00001000, 0x00000020, 0x00000040 | 0x00002000, 0x00000080 | 0x00004000, 0x00000100,
        0x00008000, 0x00030000, 0x000C0000, 0x00100000, 0x00200000, 0x00400000, 0x00800000, 0x01000000,
        0x02000000, 0x04000000, 0x08000000, 0x70000000,
    };

    public static bool FitsSlot(uint validLocations, int choice) =>
        choice >= 0 && choice < SlotMasks.Length && (validLocations & SlotMasks[choice]) != 0;

    // ── Names (material, armor set) ─────────────────────────────────────

    // MaterialType 1..77 (ACE.Entity.Enum.MaterialType), in id order.
    private const string MaterialIds =
        "Ceramic,Porcelain,Cloth,Linen,Satin,Silk,Velvet,Wool,Gem,Agate,Amber,Amethyst,Aquamarine,Azurite,"
        + "BlackGarnet,BlackOpal,Bloodstone,Carnelian,Citrine,Diamond,Emerald,FireOpal,GreenGarnet,GreenJade,"
        + "Hematite,ImperialTopaz,Jet,LapisLazuli,LavenderJade,Malachite,Moonstone,Onyx,Opal,Peridot,RedGarnet,"
        + "RedJade,RoseQuartz,Ruby,Sapphire,SmokeyQuartz,Sunstone,TigerEye,Tourmaline,Turquoise,WhiteJade,"
        + "WhiteQuartz,WhiteSapphire,YellowGarnet,YellowTopaz,Zircon,Ivory,Leather,ArmoredilloHide,GromnieHide,"
        + "ReedSharkHide,Metal,Brass,Bronze,Copper,Gold,Iron,Pyreal,Silver,Steel,Stone,Alabaster,Granite,Marble,"
        + "Obsidian,Sandstone,Serpentine,Wood,Ebony,Mahogany,Oak,Pine,Teak";

    // EquipmentSet 0..140 (ACECustom ACE.Entity.Enum.EquipmentSet), in id order.
    private const string SetIds =
        "Invalid,Test,Test2,Unknown3,CarraidasBenediction,NobleRelic,AncientRelic,AlduressaRelic,Ninja,EmpyreanRings,"
        + "ArmMindHeart,ArmorPerfectLight,ArmorPerfectLight2,Soldiers,Adepts,Archers,Defenders,Tinkers,Crafters,Hearty,"
        + "Dexterous,Wise,Swift,Hardened,Reinforced,Interlocking,Flameproof,Acidproof,Coldproof,Lightningproof,"
        + "SocietyArmor,ColosseumClothing,GraveyardClothing,OlthoiClothing,NoobieArmor,AetheriaDefense,"
        + "AetheriaDestruction,AetheriaFury,AetheriaGrowth,AetheriaVigor,RareDamageResistance,RareDamageBoost,"
        + "OlthoiArmorDRed,OlthoiArmorCRat,OlthoiArmorCRed,OlthoiArmorDRat,AlduressaRelicUpgrade,AncientRelicUpgrade,"
        + "NobleRelicUpgrade,CloakAlchemy,CloakArcaneLore,CloakArmorTinkering,CloakAssessPerson,CloakLightWeapons,"
        + "CloakMissileWeapons,CloakCooking,CloakCreatureEnchantment,CloakCrossbow,CloakFinesseWeapons,CloakDeception,"
        + "CloakFletching,CloakHealing,CloakItemEnchantment,CloakItemTinkering,CloakLeadership,CloakLifeMagic,"
        + "CloakLoyalty,CloakMace,CloakMagicDefense,CloakMagicItemTinkering,CloakManaConversion,CloakMeleeDefense,"
        + "CloakMissileDefense,CloakSalvaging,CloakSpear,CloakStaff,CloakHeavyWeapons,CloakThrownWeapon,"
        + "CloakTwoHandedCombat,CloakUnarmedCombat,CloakVoidMagic,CloakWarMagic,CloakWeaponTinkering,"
        + "CloakAssessCreature,CloakDirtyFighting,CloakDualWield,CloakRecklessness,CloakShield,CloakSneakAttack,"
        + "Ninja_New,CloakSummoning,ShroudedSoul,DarkenedMind,CloudedSpirit,MinorStingingShroudedSoul,"
        + "MinorSparkingShroudedSoul,MinorSmolderingShroudedSoul,MinorShiveringShroudedSoul,MinorStingingDarkenedMind,"
        + "MinorSparkingDarkenedMind,MinorSmolderingDarkenedMind,MinorShiveringDarkenedMind,MinorStingingCloudedSpirit,"
        + "MinorSparkingCloudedSpirit,MinorSmolderingCloudedSpirit,MinorShiveringCloudedSpirit,"
        + "MajorStingingShroudedSoul,MajorSparkingShroudedSoul,MajorSmolderingShroudedSoul,MajorShiveringShroudedSoul,"
        + "MajorStingingDarkenedMind,MajorSparkingDarkenedMind,MajorSmolderingDarkenedMind,MajorShiveringDarkenedMind,"
        + "MajorStingingCloudedSpirit,MajorSparkingCloudedSpirit,MajorSmolderingCloudedSpirit,"
        + "MajorShiveringCloudedSpirit,BlackfireStingingShroudedSoul,BlackfireSparkingShroudedSoul,"
        + "BlackfireSmolderingShroudedSoul,BlackfireShiveringShroudedSoul,BlackfireStingingDarkenedMind,"
        + "BlackfireSparkingDarkenedMind,BlackfireSmolderingDarkenedMind,BlackfireShiveringDarkenedMind,"
        + "BlackfireStingingCloudedSpirit,BlackfireSparkingCloudedSpirit,BlackfireSmolderingCloudedSpirit,"
        + "BlackfireShiveringCloudedSpirit,ShimmeringShadowsSet,BrownSocietyLocket,YellowSocietyLocket,"
        + "RedSocietyBand,GreenSocietyBand,PurpleSocietyBand,BlueSocietyBand,GauntletGarb,ParagonMissile,"
        + "ParagonCaster,ParagonMelee";

    private static readonly string[] Materials = SplitNames(MaterialIds);
    private static readonly string[] Sets = SplitNames(SetIds);

    /// <summary>"Black Garnet" for MaterialType 15; "" for 0 and ids past the list.</summary>
    public static string MaterialName(int materialType) =>
        materialType >= 1 && materialType <= Materials.Length ? Materials[materialType - 1] : string.Empty;

    /// <summary>"Cloak War Magic" for EquipmentSet 81; "Set 200" for an id past the list; "" for none.</summary>
    public static string SetName(int setId) =>
        setId <= 0 ? string.Empty
        : setId < Sets.Length ? Sets[setId]
        : "Set " + setId.ToString(CultureInfo.InvariantCulture);

    private static string[] SplitNames(string ids)
    {
        string[] parts = ids.Split(',');
        for (int i = 0; i < parts.Length; i++) parts[i] = Words(parts[i]);
        return parts;
    }

    /// <summary>"CloakWarMagic" -> "Cloak War Magic"; underscores become spaces.</summary>
    public static string Words(string id)
    {
        var sb = new StringBuilder(id.Length + 6);
        for (int i = 0; i < id.Length; i++)
        {
            char c = id[i];
            if (c == '_') { sb.Append(' '); continue; }
            // A capital starts a word after a lower-case letter or digit, or ends a run of
            // capitals when a lower-case letter follows ("ArmorDRed" -> "Armor D Red").
            if (i > 0 && char.IsUpper(c))
            {
                char prev = id[i - 1];
                bool nextLower = i + 1 < id.Length && char.IsLower(id[i + 1]);
                if (char.IsLower(prev) || char.IsDigit(prev) || (char.IsUpper(prev) && nextLower)) sb.Append(' ');
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ── Description ─────────────────────────────────────────────────────

    /// <summary>"Armor level >= 300", "Spells contains Legendary", "Fits slot is Neck".</summary>
    public static string Describe(InventoryFilterRule rule)
    {
        FilterAttrInfo info = Info(rule.Attr);
        string what = info.Kind switch
        {
            FilterAttrKind.Number => " " + rule.Number.ToString("0.##", CultureInfo.InvariantCulture),
            FilterAttrKind.Text => " \"" + rule.Text + "\"",
            FilterAttrKind.Choice => " " + (rule.Choice >= 0 && rule.Choice < SlotNames.Length ? SlotNames[rule.Choice] : "?"),
            _ => string.Empty,
        };
        return info.Label + " " + OpLabel(rule.Op) + what;
    }

    /// <summary>The rules joined with " and " / " or ", for the panel's tooltip.</summary>
    public string Summary()
    {
        if (Rules.Count == 0) return "No rules.";
        var sb = new StringBuilder();
        for (int i = 0; i < Rules.Count; i++)
        {
            if (i > 0) sb.Append(MatchAny ? " or " : " and ");
            sb.Append(Describe(Rules[i]));
        }
        return sb.ToString();
    }

    // ── Persistence ─────────────────────────────────────────────────────

    /// <summary>
    /// One line: "v1;on|off;all|any;" then rules split by ';', each "key,op,number,choice,text"
    /// (text URL-escaped so ',' and ';' can't break it).
    /// </summary>
    public string Serialize()
    {
        var sb = new StringBuilder("v1;");
        sb.Append(Enabled ? "on" : "off").Append(';').Append(MatchAny ? "any" : "all");
        foreach (InventoryFilterRule r in Rules)
        {
            sb.Append(';').Append(Info(r.Attr).Key).Append(',').Append(OpKey(r.Op)).Append(',')
              .Append(r.Number.ToString("R", CultureInfo.InvariantCulture)).Append(',')
              .Append(r.Choice.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(Uri.EscapeDataString(r.Text));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Reads <see cref="Serialize"/>'s line. Unknown attributes and broken rules are skipped,
    /// so an older engine's or a hand-edited line still loads what it can.
    /// </summary>
    public static InventoryAttributeFilter Parse(string? line)
    {
        var f = new InventoryAttributeFilter();
        if (string.IsNullOrWhiteSpace(line)) return f;
        string[] parts = line.Trim().Split(';');
        if (parts.Length < 3 || parts[0] != "v1") return f;
        f.Enabled = parts[1] == "on";
        f.MatchAny = parts[2] == "any";
        for (int i = 3; i < parts.Length && f.Rules.Count < MaxRules; i++)
        {
            string[] r = parts[i].Split(',');
            if (r.Length < 5) continue;
            FilterAttrInfo? info = InfoByKey(r[0]);
            if (info == null || !TryOpFromKey(r[1], out FilterOp op)) continue;
            if (Array.IndexOf(OpsFor(info.Kind), op) < 0) continue;
            if (!double.TryParse(r[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double num) || !double.IsFinite(num)) num = 0;
            if (!int.TryParse(r[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int choice)) choice = 0;
            string text;
            try { text = Uri.UnescapeDataString(r[4]); }
            catch (UriFormatException) { text = string.Empty; }
            f.Rules.Add(new InventoryFilterRule
            {
                Attr = info.Id,
                Op = op,
                Number = num,
                Choice = info.Kind == FilterAttrKind.Choice ? Math.Clamp(choice, 0, SlotNames.Length - 1) : choice,
                Text = text,
            });
        }
        return f;
    }
}
