// ============================================================================
//  RynthCore.Engine - ImGui/MonsterHud/DebuffIcon.cs
//  What one debuff icon stands for, shared by the monster plates' row
//  (PlateDebuffs: your debuffs on each monster) and your own plate's row
//  (SelfDebuffs: harmful spells on you), both painted by DebuffIconRow.
//  Plain data, no engine dependencies (the offline tests compile it in).
// ============================================================================

namespace RynthCore.Engine.ImGuiBackend.Hud;

/// <summary>What an icon on the plate stands for (one icon per kind).</summary>
internal enum DebuffKind : byte
{
    VulnFire, VulnCold, VulnLightning, VulnAcid, VulnSlash, VulnPierce, VulnBludgeon,
    Imperil, MagicYield, Defense, Attributes, Vitals, Movement, Skills, Dot, Other,
}

/// <summary>
/// One icon: the kind, when its soonest entry expires, that entry's full length, how many categories it
/// merges, and (2026-11-07, for the hover tooltip) the spell heading each merged category, up to
/// <see cref="MaxSpells"/> (Stack can be higher: the tooltip says how many more).
/// </summary>
internal struct DebuffIcon
{
    public const int MaxSpells = 4;

    public DebuffKind Kind;
    public long Expiry;         // Stopwatch ticks
    public float Duration;      // seconds, of the entry expiring first
    public int Stack;
    public uint Spell0, Spell1, Spell2, Spell3;
    public int SpellCount;      // ids held (<= MaxSpells)

    /// <summary>Adds a spell id (0 and repeats are skipped; past MaxSpells it is only counted by Stack).</summary>
    public void AddSpell(uint id)
    {
        if (id == 0 || SpellCount >= MaxSpells) return;
        for (int i = 0; i < SpellCount; i++) if (SpellAt(i) == id) return;
        switch (SpellCount)
        {
            case 0: Spell0 = id; break;
            case 1: Spell1 = id; break;
            case 2: Spell2 = id; break;
            default: Spell3 = id; break;
        }
        SpellCount++;
    }

    public readonly uint SpellAt(int i) => i switch { 0 => Spell0, 1 => Spell1, 2 => Spell2, 3 => Spell3, _ => 0 };
}
