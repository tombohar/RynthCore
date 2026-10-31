// ============================================================================
//  RynthCore.Headless - Messages/GameActions.cs
//  Client-to-server game actions (inside 0xF7B1: u32 sequence, u32 type, payload).
//  Each builder returns the payload; ClientSession wraps and numbers it.
// ============================================================================

using RynthCore.Headless.Net;

namespace RynthCore.Headless.Messages;

public static class ActionType
{
    public const uint MeleeAttack = 0x0008, MissileAttack = 0x000A, Talk = 0x0015, PutItemInContainer = 0x0019,
        GetAndWieldItem = 0x001A, DropItem = 0x001B, UseWithTarget = 0x0035, Use = 0x0036, CastUntargeted = 0x0048,
        CastTargeted = 0x004A, ChangeCombatMode = 0x0053, StackableMerge = 0x0054, StackableSplitToContainer = 0x0055,
        StackableSplitTo3D = 0x0056, TeleToLifestone = 0x0063, LoginComplete = 0x00A1, IdentifyObject = 0x00C8,
        GiveObjectRequest = 0x00CD, NoLongerViewingContents = 0x0195, StackableSplitToWield = 0x019B,
        QueryHealth = 0x01BF, CancelAttack = 0x01B7, PingRequest = 0x01E9, ConfirmationResponse = 0x0275,
        Salvage = 0x027D,
        Jump = 0xF61B, MoveToState = 0xF61C, AutonomousPosition = 0xF753;
}

/// <summary>CombatMode flags.</summary>
public static class CombatMode
{
    public const uint NonCombat = 1, Melee = 2, Missile = 4, Magic = 8;
}

public static class AttackHeight
{
    public const uint High = 1, Medium = 2, Low = 3;
}

internal static class GameActions
{
    private static byte[] U32s(params uint[] v)
    {
        var w = new WireWriter(v.Length * 4);
        foreach (uint x in v) w.U32(x);
        return w.ToArray();
    }

    public static byte[] Use(uint obj) => U32s(obj);
    public static byte[] UseWithTarget(uint source, uint target) => U32s(source, target);
    public static byte[] Identify(uint obj) => U32s(obj);
    public static byte[] CastTargeted(uint target, uint spell) => U32s(target, spell);
    public static byte[] CastUntargeted(uint spell) => U32s(spell);
    public static byte[] ChangeCombatMode(uint mode) => U32s(mode);
    public static byte[] MeleeAttack(uint target, uint height, float power)
        => new WireWriter(12).U32(target).U32(height).F32(Math.Clamp(power, 0f, 1f)).ToArray();
    public static byte[] MissileAttack(uint target, uint height, float accuracy)
        => new WireWriter(12).U32(target).U32(height).F32(Math.Clamp(accuracy, 0f, 1f)).ToArray();
    public static byte[] PutItemInContainer(uint item, uint container, int placement)
        => new WireWriter(12).U32(item).U32(container).I32(placement).ToArray();
    public static byte[] GetAndWield(uint item, uint equipMask) => U32s(item, equipMask);
    public static byte[] Drop(uint item) => U32s(item);

    /// <summary>Salvage with a tool (the Ust): tool, count, then the item ids.</summary>
    public static byte[] Salvage(uint tool, IReadOnlyList<uint> items)
    {
        var w = new WireWriter(8 + 4 * items.Count).U32(tool).U32((uint)items.Count);
        foreach (uint id in items) w.U32(id);
        return w.ToArray();
    }
    public static byte[] Give(uint target, uint item, int amount) => new WireWriter(12).U32(target).U32(item).I32(amount).ToArray();
    public static byte[] Merge(uint from, uint to, int amount) => new WireWriter(12).U32(from).U32(to).I32(amount).ToArray();
    public static byte[] SplitToContainer(uint stack, uint container, int placement, int amount)
        => new WireWriter(16).U32(stack).U32(container).I32(placement).I32(amount).ToArray();
    public static byte[] SplitTo3D(uint stack, int amount) => new WireWriter(8).U32(stack).I32(amount).ToArray();
    public static byte[] NoLongerViewing(uint container) => U32s(container);
    public static byte[] QueryHealth(uint obj) => U32s(obj);
    public static byte[] ConfirmationResponse(uint type, uint context, bool accept)
        => U32s(type, context, accept ? 1u : 0u);

    /// <summary>
    /// AutonomousPosition: position (cell, x, y, z, rotation w x y z), the instance,
    /// server-control, teleport and force-position sequences, a contact byte, pad to 4.
    /// </summary>
    public static byte[] AutonomousPosition(WorldPosition p, ushort instance, ushort serverControl, ushort teleport,
        ushort forcePosition, bool onGround)
    {
        var w = new WireWriter(48);
        w.U32(p.Cell).F32(p.X).F32(p.Y).F32(p.Z).F32(p.Qw).F32(p.Qx).F32(p.Qy).F32(p.Qz);
        w.U16(instance).U16(serverControl).U16(teleport).U16(forcePosition);
        w.U8(onGround ? (byte)1 : (byte)0);
        return w.Align4().ToArray();
    }
}
