// ============================================================================
//  RynthCore.Headless - Messages/WorldMessages.cs
//  Opcodes and readers for the messages that keep the world model current:
//  positions, motion, state, inventory moves, vitals/attributes/skills, enchantments,
//  use/attack results and combat notices. Property updates (0x02CD-0x02DA) and the
//  identify reply (event 0x00C9) use the engine's PropertyWire.
//
//  The Private/Public Update* messages start with a one-byte sequence, so their fields
//  are not 4-byte aligned.
// ============================================================================

using RynthCore.PluginSdk.Net;

namespace RynthCore.Headless.Messages;

internal static class WorldOpcode
{
    public const uint ParentEvent = 0xF749, PickupEvent = 0xF74A, SetState = 0xF74B, UpdateMotion = 0xF74C,
        VectorUpdate = 0xF74E, UpdatePosition = 0xF748, UpdateObject = 0xF7DB;
    public const uint SetStackSize = 0x0197, InventoryRemoveObject = 0x0024;
    public const uint PrivateUpdateSkill = 0x02DD, PrivateUpdateAttribute = 0x02E3, PrivateUpdateVital = 0x02E7,
        PublicUpdateVital = 0x02E8, PrivateUpdateVitalCurrent = 0x02E9;
    public const uint PrivateUpdatePosition = 0x02DB, PublicUpdatePosition = 0x02DC;
}

internal static class WorldEvent
{
    public const uint PutObjInContainer = 0x0022, WieldObject = 0x0023, CloseGroundContainer = 0x0052,
        InventoryServerSaveFailed = 0x00A0, IdentifyObjectResponse = 0x00C9, ViewContents = 0x0196,
        PutObjectIn3D = 0x019A, MagicRemoveSpell = 0x01A8, AttackDone = 0x01A7, VictimNotification = 0x01AC,
        KillerNotification = 0x01AD, AttackerNotification = 0x01B1, DefenderNotification = 0x01B2,
        EvasionAttacker = 0x01B3, EvasionDefender = 0x01B4, CombatCommenceAttack = 0x01B8, UpdateHealth = 0x01C0,
        UseDone = 0x01C7, WeenieError = 0x028A, WeenieErrorWithString = 0x028B, MagicUpdateSpell = 0x02C1,
        MagicUpdateEnchantment = 0x02C2, MagicRemoveEnchantment = 0x02C3, MagicUpdateMultipleEnchantments = 0x02C4,
        MagicRemoveMultipleEnchantments = 0x02C5, MagicPurgeEnchantments = 0x02C6, MagicDispelEnchantment = 0x02C7,
        MagicDispelMultipleEnchantments = 0x02C8, TransientString = 0x02EB, MagicPurgeBadEnchantments = 0x0312;
}

/// <summary>UpdatePosition 0xF748.</summary>
internal readonly record struct PositionUpdate(uint Id, uint Flags, WorldPosition Position, float Vx, float Vy, float Vz,
    ushort InstanceSeq, ushort PositionSeq, ushort TeleportSeq, ushort ForcePositionSeq)
{
    public bool Grounded => (Flags & 0x04) != 0;
}

/// <summary>UpdateMotion 0xF74C header plus the stance and (for an interpreted state) the forward command.</summary>
/// <summary>
/// UpdateMotion 0xF74C header, the stance, and for the move-to types (6 MoveToObject,
/// 7 MoveToPosition) the target, its origin and how close to get (DistanceToObject).
/// </summary>
public readonly record struct MotionUpdate(uint Id, ushort InstanceSeq, ushort MovementSeq, ushort ServerControlSeq,
    bool Autonomous, byte MovementType, ushort Stance, uint TargetId, WorldPosition? Origin, float DistanceToObject);

internal static class WorldMessages
{
    public static bool TryUpdatePosition(ReadOnlySpan<byte> b, out PositionUpdate u)
    {
        u = default;
        var r = new NetReader(b);
        if (!r.U32(out uint id) || !r.U32(out uint f) || !r.U32(out uint cell)
            || !r.F32(out float x) || !r.F32(out float y) || !r.F32(out float z))
            return false;
        float qw = 0, qx = 0, qy = 0, qz = 0;
        if ((f & 0x08) == 0 && !r.F32(out qw)) return false;
        if ((f & 0x10) == 0 && !r.F32(out qx)) return false;
        if ((f & 0x20) == 0 && !r.F32(out qy)) return false;
        if ((f & 0x40) == 0 && !r.F32(out qz)) return false;
        float vx = 0, vy = 0, vz = 0;
        if ((f & 0x01) != 0 && (!r.F32(out vx) || !r.F32(out vy) || !r.F32(out vz))) return false;
        if ((f & 0x02) != 0 && !r.Skip(4)) return false;
        if (!r.U16(out ushort inst) || !r.U16(out ushort pos) || !r.U16(out ushort tele) || !r.U16(out ushort force)) return false;
        u = new PositionUpdate(id, f, new WorldPosition(cell, x, y, z, qw, qx, qy, qz), vx, vy, vz, inst, pos, tele, force);
        return true;
    }

    public static bool TryUpdateMotion(ReadOnlySpan<byte> b, out MotionUpdate u)
    {
        u = default;
        var r = new NetReader(b);
        if (!r.U32(out uint id) || !r.U16(out ushort inst) || !r.U16(out ushort mov) || !r.U16(out ushort sc)
            || !r.U8(out byte auto) || !r.Skip(1) || !r.U8(out byte type) || !r.U8(out _) || !r.U16(out ushort stance))
            return false;
        uint target = 0;
        WorldPosition? origin = null;
        float dist = 0;
        if (type is 6 or 8) r.U32(out target);   // MoveToObject / TurnToObject: target guid first
        if (type is 6 or 7 && r.U32(out uint cell) && r.F32(out float x) && r.F32(out float y) && r.F32(out float z))
        {
            origin = new WorldPosition(cell, x, y, z, 1, 0, 0, 0);
            if (r.U32(out _) && r.F32(out float d)) dist = d;   // MoveToParameters: flags, distance to object, ...
        }
        u = new MotionUpdate(id, inst, mov, sc, auto != 0, type, stance, target, origin, dist);
        return true;
    }

    public static bool TrySetStackSize(ReadOnlySpan<byte> b, out uint id, out uint size, out uint value)
    {
        id = size = value = 0;
        var r = new NetReader(b);
        return r.Skip(1) && r.U32(out id) && r.U32(out size) && r.U32(out value);
    }

    /// <summary>PrivateUpdateAttribute 0x02E3: seq, attribute 1..6, ranks, init, xp.</summary>
    public static bool TryPrivateAttribute(ReadOnlySpan<byte> b, out uint attr, out AttributeInfo a)
    {
        a = default; attr = 0;
        var r = new NetReader(b);
        if (!r.Skip(1) || !r.U32(out attr) || !r.U32(out uint ranks) || !r.U32(out uint init) || !r.U32(out uint xp)) return false;
        a = new AttributeInfo(ranks, init, xp);
        return true;
    }

    /// <summary>PrivateUpdateVital 0x02E7: seq, vital (1 MaxHealth, 3 MaxStamina, 5 MaxMana), ranks, init, xp, current.</summary>
    public static bool TryPrivateVital(ReadOnlySpan<byte> b, out uint vital, out VitalInfo v)
    {
        v = default; vital = 0;
        var r = new NetReader(b);
        if (!r.Skip(1) || !r.U32(out uint id) || !r.U32(out uint ranks) || !r.U32(out uint init) || !r.U32(out uint xp)
            || !r.U32(out uint cur)) return false;
        vital = (id + 1) / 2;   // 1,3,5 -> 1,2,3
        v = new VitalInfo(ranks, init, xp, cur);
        return true;
    }

    /// <summary>PrivateUpdateAttribute2ndLevel 0x02E9: seq, vital (2 Health, 4 Stamina, 6 Mana), current.</summary>
    public static bool TryPrivateVitalCurrent(ReadOnlySpan<byte> b, out uint vital, out uint current)
    {
        vital = current = 0;
        var r = new NetReader(b);
        if (!r.Skip(1) || !r.U32(out uint id) || !r.U32(out current)) return false;
        vital = id / 2;         // 2,4,6 -> 1,2,3
        return true;
    }

    /// <summary>PrivateUpdateSkill 0x02DD: seq, then the 32-byte skill entry.</summary>
    public static bool TryPrivateSkill(ReadOnlySpan<byte> b, out SkillInfo s)
    {
        s = default;
        var r = new NetReader(b);
        return r.Skip(1) && r.U32(out uint id) && PlayerDescription.ReadSkillBody(ref r, id, out s);
    }

    public static bool TryLayeredSpell(ref NetReader r, out ushort spell, out ushort layer)
    {
        layer = 0;
        return r.U16(out spell) && r.U16(out layer);
    }

    public static bool TryU32(ReadOnlySpan<byte> b, out uint v)
    {
        var r = new NetReader(b);
        return r.U32(out v);
    }

    public static bool TryU32U32(ReadOnlySpan<byte> b, out uint a, out uint c)
    {
        c = 0;
        var r = new NetReader(b);
        return r.U32(out a) && r.U32(out c);
    }
}

/// <summary>Combat notices (attacker/defender/evasion/kill) as one simple shape.</summary>
public readonly record struct CombatNotice(uint EventType, string Name, uint DamageType, double HealthPercent, uint Damage, bool Critical);
