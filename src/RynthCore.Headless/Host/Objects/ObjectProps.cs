// ============================================================================
//  RynthCore.Headless - Host/Objects/ObjectProps.cs
//  An object's properties as the engine's per-key readers answer them off AC's thread
//  (Compatibility/ClientObjectHooks.cs TryGetObject*Property), from the world model:
//
//    1. the object's property tables: what the server said about it as properties -
//       the player's PlayerDescription and private updates, public UpdateProperty*
//       messages, and the tables of every identify reply (merged in by WorldState);
//    2. the CreateObject / UpdateObject weenie header (the engine's PublicWeenieDesc
//       snapshot), filed under the property ids the server took them from
//       (PropertyWire.PwdLayout): a field the header didn't carry answers nothing.
//
//  Exceptions, as in the engine: the client's own copies win for the stack size (12),
//  the wielded location (10), the container (iid 2) and the wielder (iid 3) - they follow
//  every split, merge, move and wield, where a table value is only as new as the last
//  identify; the icon data ids (8, 50, 52) answer straight from the header.
//
//  Int ids (ACE PropertyInt): 1 ItemType, 4 ClothingPriority, 5 EncumbranceVal (burden),
//  6 ItemsCapacity, 7 ContainersCapacity, 9 ValidLocations, 10 CurrentWieldedLocation,
//  11 MaxStackSize, 12 StackSize, 16 ItemUseable, 18 UiEffects, 19 Value, 50 AmmoType,
//  51 CombatUse, 91 MaxStructure, 92 Structure, 94 TargetType, 95 RadarBlipColor,
//  105 ItemWorkmanship (the header's float, rounded), 131 MaterialType,
//  133 ShowableOnRadar, 151 HookType, 152 HookItemType, 280 SharedCooldown.
//  Float: 54 UseRadius, 167 CooldownDuration, 280 workmanship (the engine's own read,
//  UtilityBelt's SalvageWorkmanship). Data id: 8 Icon, 28 Spell, 50 IconOverlay,
//  52 IconUnderlay. Instance id: 2 Container, 3 Wielder, 26 Monarch, 32 HouseOwner,
//  44 PetOwner. String: 1 Name, 20 PluralName.
// ============================================================================

using RynthCore.Engine.Compatibility;
using RynthCore.Headless.Messages;
using RynthCore.Headless.World;

namespace RynthCore.Headless.Host.Objects;

internal static class ObjectProps
{
    // ObjectDescriptionFlags (the header's bitfield).
    public const uint BfPlayer = 0x8, BfAttackable = 0x10, BfPlayerKiller = 0x20, BfPkLite = 0x2000000;

    /// <summary>The server created it (a CreateObject arrived), as opposed to a stub an inventory event made.</summary>
    public static bool Created(WorldObject o) => o.Desc.Id != 0;

    // ── ints ────────────────────────────────────────────────────────────────

    public static bool TryGetInt(WorldObject o, uint stype, out int value)
    {
        value = 0;
        switch (stype)
        {
            case 10:   // the client's own wielded location; never a stale identify value
                value = (int)o.WieldedLocation;
                return value != 0;
            case 12 when HasStack(o):
                value = (int)o.StackSize;
                return value > 0;
        }
        if (o.TryGetInt(stype, out value)) return true;
        return TryGetHeaderInt(o, stype, out value);
    }

    private static bool HasStack(WorldObject o) => o.Desc.StackSize.HasValue || o.StackSize != 1;

    /// <summary>The weenie header's int fields (zero / absent answers nothing).</summary>
    public static bool TryGetHeaderInt(WorldObject o, uint stype, out int value)
    {
        ObjectCreateData d = o.Desc;
        long v = stype switch
        {
            1 => d.ItemType,
            4 => d.Priority ?? 0,
            5 => d.Burden ?? 0,
            6 => d.ItemsCapacity ?? 0,
            7 => d.ContainersCapacity ?? 0,
            9 => d.ValidLocations ?? 0,
            10 => o.WieldedLocation,
            11 => d.MaxStackSize ?? 0,
            12 => HasStack(o) ? o.StackSize : 0,
            16 => d.Usable ?? 0,
            18 => d.UiEffects ?? 0,
            19 => o.Value,
            50 => d.AmmoType ?? 0,
            51 => d.CombatUse ?? 0,
            91 => d.MaxStructure ?? 0,
            92 => d.Structure ?? 0,
            94 => d.TargetType ?? 0,
            95 => d.RadarColor ?? 0,
            105 => d.Workmanship is float w && float.IsFinite(w) ? (long)MathF.Round(w) : 0,
            131 => d.MaterialType ?? 0,
            133 => d.RadarBehavior ?? 0,
            151 => d.HookType ?? 0,
            152 => d.HookItemTypes ?? 0,
            280 => d.CooldownId ?? 0,
            _ => 0,
        };
        value = unchecked((int)v);
        return value != 0;
    }

    private static readonly uint[] HeaderIntStypes = { 1, 4, 5, 6, 7, 9, 10, 11, 12, 16, 18, 19, 50, 51, 91, 92, 94, 95, 105, 131, 133, 151, 152, 280 };

    // ── the other kinds ────────────────────────────────────────────────────

    public static bool TryGetInt64(WorldObject o, uint stype, out long value) => o.TryGetInt64(stype, out value);

    public static bool TryGetBool(WorldObject o, uint stype, out bool value) => o.TryGetBool(stype, out value);

    public static bool TryGetFloat(WorldObject o, uint stype, out double value)
    {
        if (o.TryGetFloat(stype, out value)) return true;
        return TryGetHeaderFloat(o, stype, out value);
    }

    public static bool TryGetHeaderFloat(WorldObject o, uint stype, out double value)
    {
        ObjectCreateData d = o.Desc;
        value = stype switch
        {
            54 => d.UseRadius ?? 0,
            167 => d.CooldownDuration ?? 0,
            280 => d.Workmanship ?? 0,
            _ => 0,
        };
        return value != 0 && double.IsFinite(value);
    }

    public static bool TryGetString(WorldObject o, uint stype, out string value)
    {
        if (o.TryGetString(stype, out value) && value.Length > 0) return true;
        value = stype switch
        {
            1 => o.Desc.Name,
            20 => o.Desc.PluralName ?? "",
            _ => "",
        };
        return value.Length > 0;
    }

    public static bool TryGetDataId(WorldObject o, uint stype, out uint value)
    {
        ObjectCreateData d = o.Desc;
        // The icons straight from the header, 0 included (the engine's PWD icon read).
        if (stype is 8 or 50 or 52 && Created(o))
        {
            value = stype switch { 8 => d.Icon, 50 => d.IconOverlay ?? 0, _ => d.IconUnderlay ?? 0 };
            return true;
        }
        if (o.TryGetDataId(stype, out value)) return true;
        value = stype switch
        {
            8 => d.Icon,
            28 => d.Spell ?? 0,
            50 => d.IconOverlay ?? 0,
            52 => d.IconUnderlay ?? 0,
            _ => 0,
        };
        return value != 0;
    }

    public static bool TryGetInstanceId(WorldObject o, uint stype, out uint value)
    {
        value = 0;
        if (stype == 0) return false;
        if (stype == 2 && o.ContainerId != 0) { value = o.ContainerId; return true; }
        if (stype == 3 && o.WielderId != 0) { value = o.WielderId; return true; }
        if (o.TryGetInstanceId(stype, out value) && value != 0) return true;
        ObjectCreateData d = o.Desc;
        value = stype switch
        {
            26 => d.Monarch ?? 0,
            32 => d.HouseOwner ?? 0,
            44 => d.PetOwner ?? 0,
            _ => 0,
        };
        return value != 0;
    }

    // ── the whole set (GetObjectProperties) ─────────────────────────────────

    /// <summary>The weenie header as a property bag (PwdLayout.FillBag's counterpart).</summary>
    public static PropertyBag HeaderBag(WorldObject o)
    {
        var bag = new PropertyBag();
        foreach (uint s in HeaderIntStypes)
            if (TryGetHeaderInt(o, s, out int v)) bag.Ints[s] = v;
        foreach (uint s in new uint[] { 54, 167 })
            if (TryGetHeaderFloat(o, s, out double f)) bag.Floats[s] = f;
        ObjectCreateData d = o.Desc;
        if (d.Icon != 0) bag.DataIds[8] = d.Icon;
        if (d.Spell is ushort sp && sp != 0) bag.DataIds[28] = sp;
        if (d.IconOverlay is uint ov && ov != 0) bag.DataIds[50] = ov;
        if (d.IconUnderlay is uint un && un != 0) bag.DataIds[52] = un;
        if (o.ContainerId != 0) bag.InstanceIds[2] = o.ContainerId;
        if (o.WielderId != 0) bag.InstanceIds[3] = o.WielderId;
        if (d.Monarch is uint m && m != 0) bag.InstanceIds[26] = m;
        if (d.HouseOwner is uint h && h != 0) bag.InstanceIds[32] = h;
        if (d.PetOwner is uint p && p != 0) bag.InstanceIds[44] = p;
        if (d.Name.Length > 0) bag.Strings[1] = d.Name;
        if (!string.IsNullOrEmpty(d.PluralName)) bag.Strings[20] = d.PluralName;
        return bag;
    }

    /// <summary>The property tables (PlayerDescription, updates, identifies) as a bag.</summary>
    public static PropertyBag TableBag(WorldObject o)
    {
        var bag = new PropertyBag();
        foreach (var kv in o.Ints) bag.Ints[kv.Key] = kv.Value;
        foreach (var kv in o.Int64s) bag.Int64s[kv.Key] = kv.Value;
        foreach (var kv in o.Bools) bag.Bools[kv.Key] = kv.Value;
        foreach (var kv in o.Floats) bag.Floats[kv.Key] = kv.Value;
        foreach (var kv in o.Strings) bag.Strings[kv.Key] = kv.Value;
        foreach (var kv in o.DataIds) bag.DataIds[kv.Key] = kv.Value;
        foreach (var kv in o.InstanceIds) bag.InstanceIds[kv.Key] = kv.Value;
        return bag;
    }

    /// <summary>
    /// The v84 record (ObjectPropertyRecord) for one object: the header under the tables, then the
    /// client's own stack size, wielded location, container and wielder over both - what the
    /// per-key reads above answer. Null when nothing is known about it.
    /// </summary>
    public static byte[]? Record(WorldObject o, bool isPlayer, bool appraised, long lastIdTime, IReadOnlyList<uint>? spells)
    {
        bool created = Created(o);
        // The tables go in as the "qualities" source: Combine lays it over the header for players and others alike.
        PropertyBag all = ObjectPropertyRecord.Combine(isPlayer, created ? HeaderBag(o) : null, TableBag(o), null, null, null);
        if (o.WieldedLocation != 0) all.Ints[10] = (int)o.WieldedLocation;
        else all.Ints.Remove(10);
        if (all.Count == 0 && (spells == null || spells.Count == 0) && !created) return null;

        ObjectPropertyFlags flags = ObjectPropertyFlags.None;
        if (appraised) flags |= ObjectPropertyFlags.HasIdData;
        if (isPlayer) flags |= ObjectPropertyFlags.IsPlayer;
        if (created) flags |= ObjectPropertyFlags.HasCreateData;
        return ObjectPropertyRecord.Encode(all, spells, flags, lastIdTime);
    }

    // ── attackable ──────────────────────────────────────────────────────────

    /// <summary>
    /// Retail's ClientCombatSystem::ObjectIsAttackable from what the client has: never the player;
    /// a creature (ItemType 0x10) whose header has the Attackable bit (0x10: ACE sets it on monsters
    /// and players, not on NPCs or vendors - but also on most items, hence the creature test); and
    /// for another player (Player bit 0x8) only when both are player killers (0x20) or both PK-lite
    /// (0x2000000).
    /// </summary>
    public static bool IsAttackable(WorldObject o, WorldObject? me)
    {
        if (!Created(o) || (me != null && o.Id == me.Id)) return false;
        if ((o.ItemType & 0x10) == 0) return false;
        uint bf = o.Desc.DescriptionFlags;
        if ((bf & BfAttackable) == 0) return false;
        if ((bf & BfPlayer) == 0) return true;
        uint mine = me?.Desc.DescriptionFlags ?? 0;
        return ((bf & BfPlayerKiller) != 0 && (mine & BfPlayerKiller) != 0)
            || ((bf & BfPkLite) != 0 && (mine & BfPkLite) != 0);
    }

    // ── heading ─────────────────────────────────────────────────────────────

    /// <summary>Compass heading 0..360 (0 = north, clockwise) from a rotation quaternion (the engine's formula).</summary>
    public static float Heading(float qw, float qz)
    {
        double physYawDeg = 2.0 * Math.Atan2(qz, qw) * (180.0 / Math.PI);
        return (float)(((-physYawDeg) % 360.0 + 720.0) % 360.0);
    }
}
