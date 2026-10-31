// ============================================================================
//  RynthCore.Headless - Session/ClientSession.Actions.cs
//  The player's actions (use, cast, attack, inventory moves, ...). Movement is in
//  ClientSession.Movement.cs.
// ============================================================================

using RynthCore.Headless.Messages;
using RynthCore.Headless.World;

namespace RynthCore.Headless.Session;

public sealed partial class ClientSession
{
    public void Use(uint obj) => SendAction(ActionType.Use, GameActions.Use(obj));
    public void UseOn(uint source, uint target) => SendAction(ActionType.UseWithTarget, GameActions.UseWithTarget(source, target));
    public void Identify(uint obj) => SendAction(ActionType.IdentifyObject, GameActions.Identify(obj));
    public void QueryHealth(uint obj) => SendAction(ActionType.QueryHealth, GameActions.QueryHealth(obj));
    public void SetCombatMode(uint mode) => SendAction(ActionType.ChangeCombatMode, GameActions.ChangeCombatMode(mode));

    /// <summary>Casts on a target (the player's own id for self spells), or untargeted when target is 0.</summary>
    public void Cast(uint spell, uint target)
    {
        if (target == 0) SendAction(ActionType.CastUntargeted, GameActions.CastUntargeted(spell));
        else SendAction(ActionType.CastTargeted, GameActions.CastTargeted(target, spell));
    }

    public void MeleeAttack(uint target, uint height = AttackHeight.Medium, float power = 0.5f)
        => SendAction(ActionType.MeleeAttack, GameActions.MeleeAttack(target, height, power));
    public void MissileAttack(uint target, uint height = AttackHeight.Medium, float accuracy = 0.5f)
        => SendAction(ActionType.MissileAttack, GameActions.MissileAttack(target, height, accuracy));
    public void CancelAttack() => SendAction(ActionType.CancelAttack, ReadOnlySpan<byte>.Empty);

    /// <summary>Picks an item up into the main pack (the server asks us to walk over first if needed).</summary>
    public void PickUp(uint item) => MoveItem(item, PlayerId, 0);
    public void MoveItem(uint item, uint container, int placement)
        => SendAction(ActionType.PutItemInContainer, GameActions.PutItemInContainer(item, container, placement));
    public void Wield(uint item, uint equipMask) => SendAction(ActionType.GetAndWieldItem, GameActions.GetAndWield(item, equipMask));
    public void Drop(uint item) => SendAction(ActionType.DropItem, GameActions.Drop(item));
    public void Salvage(uint tool, IReadOnlyList<uint> items) => SendAction(ActionType.Salvage, GameActions.Salvage(tool, items));
    public void Give(uint target, uint item, int amount = 1) => SendAction(ActionType.GiveObjectRequest, GameActions.Give(target, item, amount));
    public void Merge(uint from, uint to, int amount) => SendAction(ActionType.StackableMerge, GameActions.Merge(from, to, amount));
    public void Split(uint stack, uint container, int placement, int amount)
        => SendAction(ActionType.StackableSplitToContainer, GameActions.SplitToContainer(stack, container, placement, amount));
    public void StopViewing(uint container) => SendAction(ActionType.NoLongerViewingContents, GameActions.NoLongerViewing(container));
    public void RecallLifestone() => SendAction(ActionType.TeleToLifestone, ReadOnlySpan<byte>.Empty);
    public void ConfirmationResponse(uint type, uint context, bool accept)
        => SendAction(ActionType.ConfirmationResponse, GameActions.ConfirmationResponse(type, context, accept));

    /// <summary>
    /// A position moved to (x, y) in <paramref name="p"/>'s landblock frame. Outdoors the cell
    /// (and, past an edge, the landblock) is recomputed; indoors the cell is kept.
    /// </summary>
    internal static WorldPosition Relocate(WorldPosition p, float x, float y)
    {
        if ((p.Cell & 0xFFFF) >= 0x100)
            return p with { X = x, Y = y };
        int lbx = (int)(p.Cell >> 24), lby = (int)((p.Cell >> 16) & 0xFF);
        while (x < 0 && lbx > 0) { x += 192; lbx--; }
        while (x >= 192 && lbx < 254) { x -= 192; lbx++; }
        while (y < 0 && lby > 0) { y += 192; lby--; }
        while (y >= 192 && lby < 254) { y -= 192; lby++; }
        x = Math.Clamp(x, 0, 191.999f);
        y = Math.Clamp(y, 0, 191.999f);
        uint cell = (uint)((int)(x / 24) * 8 + (int)(y / 24) + 1);
        return p with { Cell = ((uint)lbx << 24) | ((uint)lby << 16) | cell, X = x, Y = y };
    }
}
