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

/// <summary>One icon: the kind, when its soonest entry expires, that entry's full length, and how many categories it merges.</summary>
internal struct DebuffIcon
{
    public DebuffKind Kind;
    public long Expiry;         // Stopwatch ticks
    public float Duration;      // seconds, of the entry expiring first
    public int Stack;
}
