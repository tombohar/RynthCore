// ============================================================================
//  RynthCore.Headless - World/PlayerState.cs
//  The player's own state: properties, attributes, vitals, skills, spellbook,
//  enchantments, the main pack order and what is equipped. Seeded by PlayerDescription,
//  then kept current by the private updates and magic/inventory events.
//
//  Vital maxima here are the BASE values (attribute contribution + init + ranks);
//  enchantment-adjusted ("buffed") values come with the plugin host (P3).
// ============================================================================

using RynthCore.Headless.Messages;

namespace RynthCore.Headless.World;

public enum Vital : uint { Health = 1, Stamina = 2, Mana = 3 }

public sealed class PlayerState
{
    public uint Id { get; internal set; }
    public bool DescriptionReceived { get; internal set; }

    internal readonly Dictionary<uint, AttributeInfo> Attributes = new();
    internal readonly Dictionary<uint, VitalInfo> Vitals = new();
    internal readonly Dictionary<uint, SkillInfo> Skills = new();
    internal readonly HashSet<uint> Spells = new();
    internal readonly List<Enchantment> Enchantments = new();
    internal readonly List<uint> MainPack = new();        // ordinary items, server order
    internal readonly List<uint> SidePacks = new();       // containers and foci, server order
    internal readonly Dictionary<uint, uint> Equipped = new();   // item -> EquipMask
    internal PlayerDescriptionData? Description;

    public IReadOnlyDictionary<uint, AttributeInfo> AttributeTable => Attributes;
    public IReadOnlyDictionary<uint, SkillInfo> SkillTable => Skills;
    public IReadOnlyCollection<uint> SpellBook => Spells;
    public IReadOnlyList<Enchantment> ActiveEnchantments => Enchantments;
    public IReadOnlyList<uint> MainPackOrder => MainPack;
    public IReadOnlyList<uint> SidePackOrder => SidePacks;
    public IReadOnlyDictionary<uint, uint> EquippedItems => Equipped;

    /// <summary>Strength 1 .. Self 6, base (init + ranks).</summary>
    public uint BaseAttribute(uint attr) => Attributes.TryGetValue(attr, out AttributeInfo a) ? a.Base : 0;

    public uint CurrentVital(Vital v) => Vitals.TryGetValue((uint)v, out VitalInfo i) ? i.Current : 0;

    /// <summary>Base maximum: Endurance/2 (health), Endurance (stamina) or Self (mana), plus init + ranks.</summary>
    public uint BaseMaxVital(Vital v)
    {
        if (!Vitals.TryGetValue((uint)v, out VitalInfo i)) return 0;
        uint attr = v switch
        {
            Vital.Health => (BaseAttribute(2) + 1) / 2,
            Vital.Stamina => BaseAttribute(2),
            _ => BaseAttribute(6),
        };
        return attr + i.Init + i.Ranks;
    }

    public bool TryGetSkill(uint id, out SkillInfo s) => Skills.TryGetValue(id, out s);

    internal void SetEnchantment(Enchantment e)
    {
        RemoveEnchantment(e.SpellId, e.Layer);
        Enchantments.Add(e);
    }

    internal bool RemoveEnchantment(ushort spell, ushort layer)
        => Enchantments.RemoveAll(x => x.SpellId == spell && x.Layer == layer) > 0;

    internal void RemoveFromPack(uint id)
    {
        MainPack.Remove(id);
        SidePacks.Remove(id);
    }
}
