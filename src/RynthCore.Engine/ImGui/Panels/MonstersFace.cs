// ============================================================================
//  RynthCore.Engine - ImGui/Panels/MonstersFace.cs
//  The basic Monsters panel's ImGui face, retired 2026-10-01 (Tom). What only
//  it could do moved to the Damage panel (DamageFace: Name rules, the
//  Offhand modes per monster) and Monster Detail (DamageDetailFace: priority,
//  damage type, extra vuln, match expression; a name rule's debuffs, off
//  hand, pet and name). Categories were dropped from the UI; Rule.Category
//  stays in the model so existing rule files keep it.
//
//  Only Register is left, because EntryPoint calls it: it registers no face
//  (so "Monsters" never shows) and moves a Monsters panel saved open onto
//  Damage. PanelRouter sends every other "Monsters" request to Damage.
// ============================================================================

using RynthCore.Engine.UI;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal static class MonstersFace
{
    public const string Title = "Monsters";

    /// <summary>Engine init (EntryPoint). Registers nothing: see the file header.</summary>
    public static void Register() => PanelRouter.MoveRetiredMonstersPanel();
}
