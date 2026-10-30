// ============================================================================
//  RynthCore.Engine - ImGui/MonsterHud/PlateClass.cs
//  What a world object is to the nameplates (MonsterHud.Classify), from the
//  main-thread snapshot's ITEM_TYPE: a creature (its PublicWeenieDesc then
//  says player, pet, vendor or other), a portal (a name label over it, since
//  2026-10-28), or nothing that gets a plate or a name.
//
//  Pure code: MonsterHud feeds it the snapshot values; tools/PlateSizingTests
//  compiles it in.
// ============================================================================

namespace RynthCore.Engine.ImGuiBackend.Hud;

internal static class PlateClass
{
    // The values of MonsterHud's per-id cache (MonsterHud.Classes). Unknown: the snapshot has no type yet.
    public const byte Unknown = 0, Other = 1, Creature = 2, Vendor = 3, Player = 4, Pet = 5;
    /// <summary>A creature whose PWD isn't in the snapshot yet: never cached, asked again next scan.</summary>
    public const byte CreatureNoPwd = 6;
    /// <summary>A portal: a name label over it ("Portal names").</summary>
    public const byte Portal = 7;

    public const uint TypeCreature = 0x10;     // ITEM_TYPE TYPE_CREATURE
    public const uint TypePortal = 0x10000;    // ITEM_TYPE TYPE_PORTAL (the radar and the Sense scan read it too)

    /// <summary>
    /// What the item type alone decides: <see cref="Creature"/> (its PWD then decides which kind),
    /// <see cref="Portal"/>, or <see cref="Other"/>. A creature bit wins, so nothing that has a
    /// monster plate ever turns into a portal name.
    /// </summary>
    public static byte FromItemType(uint type) =>
        (type & TypeCreature) != 0 ? Creature : (type & TypePortal) != 0 ? Portal : Other;
}
