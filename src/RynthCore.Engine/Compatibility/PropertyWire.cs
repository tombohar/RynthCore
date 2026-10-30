// ============================================================================
//  RynthCore.Engine - Compatibility/PropertyWire.cs
//
//  Pure parsers for every message that carries an object's properties to the
//  client, in ACE's exact wire formats (ACE.Server: AppraiseInfo.cs and the
//  Armor/Creature/Weapon/HookProfile writers, GameEventPlayerDescription.cs,
//  GameMessagePrivate/PublicUpdateProperty*.cs, WorldObject_Networking.cs for
//  the PublicWeenieDesc fields). No AC memory, no engine state: the engine feeds
//  them from UIQueueManager::ProcessNetBlobData (SmartBoxHooks.ParseGameEvent),
//  and the offline tests (tools\PropertyWireTests) compile
//  this file in and feed them hand-built messages.
//
//  ProcessNetBlobData hands its handlers the message from its type on: a game
//  event (0xF7B0) arrives as [event type][payload], a top-level message as
//  [opcode][body]. Verified against acclient.exe: the function compares [data]
//  with 0x2D5/0x2D6 (string updates, @0x55CD72) and calls
//  CM_Examine::SendNotice_SetAppraiseInfo (@0x55C862) and
//  CPlayerSystem::Handle_PlayerDescription (@0x55BE35); the CM_Qualities
//  DispatchUI_* handlers it calls read the opcode at buf+0.
// ============================================================================

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace RynthCore.Engine.Compatibility;

/// <summary>The seven AC property types (STypeInt, STypeInt64, ...).</summary>
internal enum PropertyKind : byte
{
    Int,
    Int64,
    Bool,
    Float,
    String,
    DataId,
    InstanceId,
}

/// <summary>One property update message (UpdateProperty* public or private).</summary>
internal readonly struct PropertyUpdate
{
    public PropertyUpdate(PropertyKind kind, bool isPrivate, uint objectId, uint key, long integer, double real, string? text)
    {
        Kind = kind;
        IsPrivate = isPrivate;
        ObjectId = objectId;
        Key = key;
        Integer = integer;
        Real = real;
        Text = text;
    }

    public PropertyKind Kind { get; }
    /// <summary>A private update is about the player and carries no object id.</summary>
    public bool IsPrivate { get; }
    /// <summary>The object a public update is about; 0 for a private one.</summary>
    public uint ObjectId { get; }
    public uint Key { get; }
    /// <summary>Int, Int64, Bool (0/1), DataId and InstanceId values.</summary>
    public long Integer { get; }
    /// <summary>Float values.</summary>
    public double Real { get; }
    /// <summary>String values.</summary>
    public string? Text { get; }
}

/// <summary>
/// An object's properties, one table per type. Not thread-safe: the engine's caches
/// guard every bag with their own lock.
/// </summary>
internal sealed class PropertyBag
{
    public readonly Dictionary<uint, int> Ints = new();
    public readonly Dictionary<uint, long> Int64s = new();
    public readonly Dictionary<uint, bool> Bools = new();
    public readonly Dictionary<uint, double> Floats = new();
    public readonly Dictionary<uint, string> Strings = new();
    public readonly Dictionary<uint, uint> DataIds = new();
    public readonly Dictionary<uint, uint> InstanceIds = new();

    public int Count => Ints.Count + Int64s.Count + Bools.Count + Floats.Count + Strings.Count + DataIds.Count + InstanceIds.Count;

    public void Apply(in PropertyUpdate u)
    {
        switch (u.Kind)
        {
            case PropertyKind.Int: Ints[u.Key] = unchecked((int)u.Integer); break;
            case PropertyKind.Int64: Int64s[u.Key] = u.Integer; break;
            case PropertyKind.Bool: Bools[u.Key] = u.Integer != 0; break;
            case PropertyKind.Float: Floats[u.Key] = u.Real; break;
            case PropertyKind.String: Strings[u.Key] = u.Text ?? string.Empty; break;
            case PropertyKind.DataId: DataIds[u.Key] = unchecked((uint)u.Integer); break;
            case PropertyKind.InstanceId: InstanceIds[u.Key] = unchecked((uint)u.Integer); break;
        }
    }

    /// <summary>Copies every value of <paramref name="other"/> in, replacing same-key values.</summary>
    public void MergeFrom(PropertyBag other)
    {
        foreach (var kv in other.Ints) Ints[kv.Key] = kv.Value;
        foreach (var kv in other.Int64s) Int64s[kv.Key] = kv.Value;
        foreach (var kv in other.Bools) Bools[kv.Key] = kv.Value;
        foreach (var kv in other.Floats) Floats[kv.Key] = kv.Value;
        foreach (var kv in other.Strings) Strings[kv.Key] = kv.Value;
        foreach (var kv in other.DataIds) DataIds[kv.Key] = kv.Value;
        foreach (var kv in other.InstanceIds) InstanceIds[kv.Key] = kv.Value;
    }
}

/// <summary>What one identify reply (game event 0xC9, SetAppraiseInfo) told the client.</summary>
internal sealed class IdentifyRecord
{
    public uint ObjectId;
    public uint Flags;
    public bool Success;
    /// <summary>The six tables, plus the armour/weapon/hook profile values filed under their property ids.</summary>
    public readonly PropertyBag Properties = new();
    public int SpellCount;
    public bool HasArmorProfile, HasCreatureProfile, HasWeaponProfile, HasHookProfile;
    public uint CreatureHealth, CreatureHealthMax;
    /// <summary>A flagged section ran past the end of the message; everything before it was kept.</summary>
    public bool Truncated;
}

internal static class PropertyWire
{
    // Game events (ProcessNetBlobData: [event type][payload]).
    public const uint EventPlayerDescription = 0x0013;
    public const uint EventIdentifyObjectResponse = 0x00C9;

    // Top-level messages (ACE GameMessageOpcode).
    public const uint OpPrivateUpdateInt = 0x02CD, OpPublicUpdateInt = 0x02CE;
    public const uint OpPrivateUpdateInt64 = 0x02CF, OpPublicUpdateInt64 = 0x02D0;
    public const uint OpPrivateUpdateBool = 0x02D1, OpPublicUpdateBool = 0x02D2;
    public const uint OpPrivateUpdateFloat = 0x02D3, OpPublicUpdateFloat = 0x02D4;
    public const uint OpPrivateUpdateString = 0x02D5, OpPublicUpdateString = 0x02D6;
    public const uint OpPrivateUpdateDataId = 0x02D7, OpPublicUpdateDataId = 0x02D8;
    public const uint OpPrivateUpdateInstanceId = 0x02D9, OpPublicUpdateInstanceId = 0x02DA;

    // IdentifyResponseFlags (ACE.Entity.Enum.IdentifyResponseFlags).
    public const uint IdIntStats = 0x0001, IdBoolStats = 0x0002, IdFloatStats = 0x0004, IdStringStats = 0x0008,
                      IdSpellBook = 0x0010, IdWeaponProfile = 0x0020, IdHookProfile = 0x0040, IdArmorProfile = 0x0080,
                      IdCreatureProfile = 0x0100, IdDidStats = 0x1000, IdInt64Stats = 0x2000;

    // PlayerDescription DescriptionPropertyFlag.
    public const uint PdInt = 0x0001, PdBool = 0x0002, PdDouble = 0x0004, PdDid = 0x0008, PdString = 0x0010,
                      PdIid = 0x0040, PdInt64 = 0x0080;

    // Property ids the profiles are filed under (ACE PropertyInt / PropertyFloat / PropertyBool).
    public const uint IntValidLocations = 9, IntDamage = 44, IntDamageType = 45, IntWeaponSkill = 48, IntWeaponTime = 49, IntAmmoType = 50;
    public const uint FloatWeaponLength = 21, FloatDamageVariance = 22, FloatMaximumVelocity = 26, FloatWeaponOffense = 62, FloatDamageMod = 63;
    public const uint BoolInscribable = 22;

    /// <summary>
    /// ArmorProfile floats in wire order (ACE ArmorProfile.Write: slash, pierce, bludgeon, cold,
    /// fire, acid, nether, lightning) and the PropertyFloat ids they are filed under
    /// (ArmorModVsSlash 13 .. ArmorModVsAcid 18, ArmorModVsNether 165, ArmorModVsElectric 19).
    /// </summary>
    public static readonly uint[] ArmorProfileFloatIds = { 13, 14, 15, 16, 17, 18, 165, 19 };

    public static bool IsPropertyUpdateOpcode(uint opcode) => opcode >= OpPrivateUpdateInt && opcode <= OpPublicUpdateInstanceId;

    /// <summary>
    /// Parses one UpdateProperty* message, starting at its opcode. Layouts (ACE):
    ///   private: [op][seq u8][key u32][value]
    ///   public:  [op][seq u8][guid u32][key u32][value]
    ///   strings: private [op][seq][key][align 4][str16L], public [op][seq][key][guid][align 4][str16L]
    /// (the public string update has the key BEFORE the guid, unlike every other type).
    /// Values: int i32, int64 i64, bool u32, float f64, data id u32, instance id u32.
    /// </summary>
    public static bool TryParsePropertyUpdate(ReadOnlySpan<byte> msg, out PropertyUpdate update)
    {
        update = default;
        var r = new WireReader(msg);
        if (!r.U32(out uint op) || !IsPropertyUpdateOpcode(op) || !r.Skip(1))   // sequence byte
            return false;

        bool isPrivate = (op & 1) != 0;   // every private opcode is odd, every public one even
        uint guid = 0, key;
        switch (op)
        {
            case OpPrivateUpdateString:
            case OpPublicUpdateString:
            {
                if (!r.U32(out key)) return false;
                if (!isPrivate && !r.U32(out guid)) return false;
                if (!r.Align4() || !r.String16L(out string text)) return false;
                update = new PropertyUpdate(PropertyKind.String, isPrivate, guid, key, 0, 0, text);
                return true;
            }
        }

        if (!isPrivate && !r.U32(out guid)) return false;
        if (!r.U32(out key)) return false;
        switch (op)
        {
            case OpPrivateUpdateInt:
            case OpPublicUpdateInt:
                if (!r.I32(out int i)) return false;
                update = new PropertyUpdate(PropertyKind.Int, isPrivate, guid, key, i, 0, null);
                return true;
            case OpPrivateUpdateInt64:
            case OpPublicUpdateInt64:
                if (!r.I64(out long q)) return false;
                update = new PropertyUpdate(PropertyKind.Int64, isPrivate, guid, key, q, 0, null);
                return true;
            case OpPrivateUpdateBool:
            case OpPublicUpdateBool:
                if (!r.U32(out uint b)) return false;
                update = new PropertyUpdate(PropertyKind.Bool, isPrivate, guid, key, b != 0 ? 1 : 0, 0, null);
                return true;
            case OpPrivateUpdateFloat:
            case OpPublicUpdateFloat:
                if (!r.F64(out double d) || !double.IsFinite(d)) return false;
                update = new PropertyUpdate(PropertyKind.Float, isPrivate, guid, key, 0, d, null);
                return true;
            case OpPrivateUpdateDataId:
            case OpPublicUpdateDataId:
                if (!r.U32(out uint did)) return false;
                update = new PropertyUpdate(PropertyKind.DataId, isPrivate, guid, key, did, 0, null);
                return true;
            case OpPrivateUpdateInstanceId:
            case OpPublicUpdateInstanceId:
                if (!r.U32(out uint iid)) return false;
                update = new PropertyUpdate(PropertyKind.InstanceId, isPrivate, guid, key, iid, 0, null);
                return true;
        }
        return false;
    }

    /// <summary>
    /// Parses an identify reply, starting at its event type (0xC9): [type][guid][flags][success],
    /// then in ACE's write order the int, int64, bool, float, string and data id tables, the
    /// spell book, the armour, creature, weapon and hook profiles. The tables are kept as sent;
    /// the armour profile's ratings, the weapon profile's numbers and the hook profile's
    /// locations/ammo/inscribable are added under their property ids, where the tables don't
    /// already hold them (as the retail client's appraisal panel reads them, and UtilityBelt
    /// exposes them). A section that runs past the end stops the parse; what came before is kept.
    /// </summary>
    public static bool TryParseIdentify(ReadOnlySpan<byte> msg, out IdentifyRecord record)
    {
        record = new IdentifyRecord();
        var r = new WireReader(msg);
        if (!r.U32(out uint type) || type != EventIdentifyObjectResponse)
            return false;
        if (!r.U32(out record.ObjectId) || !r.U32(out record.Flags) || !r.U32(out uint success) || record.ObjectId == 0)
            return false;
        record.Success = success != 0;
        uint f = record.Flags;
        PropertyBag bag = record.Properties;

        if (!ReadSections(ref r, record, bag, f))
            record.Truncated = true;
        return true;
    }

    private static bool ReadSections(ref WireReader r, IdentifyRecord rec, PropertyBag bag, uint f)
    {
        if ((f & IdIntStats) != 0 && !ReadIntTable(ref r, bag.Ints)) return false;
        if ((f & IdInt64Stats) != 0 && !ReadInt64Table(ref r, bag.Int64s)) return false;
        if ((f & IdBoolStats) != 0 && !ReadBoolTable(ref r, bag.Bools)) return false;
        if ((f & IdFloatStats) != 0 && !ReadFloatTable(ref r, bag.Floats)) return false;
        if ((f & IdStringStats) != 0 && !ReadStringTable(ref r, bag.Strings)) return false;
        if ((f & IdDidStats) != 0 && !ReadUIntTable(ref r, bag.DataIds)) return false;

        if ((f & IdSpellBook) != 0)
        {
            if (!r.I32(out int n) || n < 0 || n > 100_000 || !r.Skip(n * 4)) return false;
            rec.SpellCount = n;
        }

        if ((f & IdArmorProfile) != 0)
        {
            Span<float> prot = stackalloc float[8];
            for (int i = 0; i < 8; i++)
                if (!r.F32(out prot[i])) return false;
            rec.HasArmorProfile = true;
            for (int i = 0; i < 8; i++)
                if (float.IsFinite(prot[i])) bag.Floats.TryAdd(ArmorProfileFloatIds[i], prot[i]);
        }

        if ((f & IdCreatureProfile) != 0)
        {
            // cpFlags, Health, HealthMax; ShowAttributes(0x8): Str, End, Quick, Coord, Focus, Self,
            // Stamina, Mana, StaminaMax, ManaMax; HasBuffsDebuffs(0x1): u16 highlights, u16 colors.
            if (!r.U32(out uint cp) || !r.U32(out rec.CreatureHealth) || !r.U32(out rec.CreatureHealthMax)) return false;
            if ((cp & 0x8) != 0 && !r.Skip(40)) return false;
            if ((cp & 0x1) != 0 && !r.Skip(4)) return false;
            rec.HasCreatureProfile = true;
        }

        if ((f & IdWeaponProfile) != 0)
        {
            // ACE WeaponProfile.Write: DamageType, WeaponTime, WeaponSkill, Damage (u32 each),
            // DamageVariance, DamageMod, WeaponLength, MaxVelocity, WeaponOffense (f64 each),
            // MaxVelocityEstimated (u32; no property of its own).
            if (!r.U32(out uint dmgType) || !r.U32(out uint time) || !r.U32(out uint skill) || !r.U32(out uint dmg)) return false;
            if (!r.F64(out double variance) || !r.F64(out double dmgMod) || !r.F64(out double length)
                || !r.F64(out double maxVel) || !r.F64(out double offense) || !r.Skip(4)) return false;
            rec.HasWeaponProfile = true;
            bag.Ints.TryAdd(IntDamageType, unchecked((int)dmgType));
            bag.Ints.TryAdd(IntWeaponTime, unchecked((int)time));
            bag.Ints.TryAdd(IntWeaponSkill, unchecked((int)skill));
            bag.Ints.TryAdd(IntDamage, unchecked((int)dmg));
            AddFinite(bag.Floats, FloatDamageVariance, variance);
            AddFinite(bag.Floats, FloatDamageMod, dmgMod);
            AddFinite(bag.Floats, FloatWeaponLength, length);
            AddFinite(bag.Floats, FloatMaximumVelocity, maxVel);
            AddFinite(bag.Floats, FloatWeaponOffense, offense);
        }

        if ((f & IdHookProfile) != 0)
        {
            // ACE HookProfile.Write: Flags (Inscribable 1, IsHealer 2, IsFood 4, IsLockpick 8),
            // ValidLocations, AmmoType. Sent when a house hook holds an item: the tables above
            // are that item's.
            if (!r.U32(out uint hookFlags) || !r.U32(out uint locations) || !r.U32(out uint ammo)) return false;
            rec.HasHookProfile = true;
            if (locations != 0) bag.Ints.TryAdd(IntValidLocations, unchecked((int)locations));
            if (ammo != 0) bag.Ints.TryAdd(IntAmmoType, unchecked((int)ammo));
            if ((hookFlags & 0x1) != 0) bag.Bools.TryAdd(BoolInscribable, true);
        }
        // The enchantment bitfields and armour levels that may follow carry no properties.
        return true;
    }

    /// <summary>
    /// Parses the property tables at the start of the player's PlayerDescription (game event
    /// 0x13), starting at its event type: [type][property flags][weenie type], then the int,
    /// int64, bool, double, string, data id and instance id tables (ACE
    /// GameEventPlayerDescription.WriteEventBody order). Everything after them (positions,
    /// attributes, skills, spells, options, inventory) is ignored. A table that runs past the
    /// end stops the parse; the tables before it are kept (<paramref name="truncated"/>).
    /// </summary>
    public static bool TryParsePlayerDescription(ReadOnlySpan<byte> msg, out PropertyBag bag, out bool truncated)
    {
        bag = new PropertyBag();
        truncated = false;
        var r = new WireReader(msg);
        if (!r.U32(out uint type) || type != EventPlayerDescription || !r.U32(out uint f) || !r.Skip(4))
            return false;
        bool ok = ((f & PdInt) == 0 || ReadIntTable(ref r, bag.Ints))
               && ((f & PdInt64) == 0 || ReadInt64Table(ref r, bag.Int64s))
               && ((f & PdBool) == 0 || ReadBoolTable(ref r, bag.Bools))
               && ((f & PdDouble) == 0 || ReadFloatTable(ref r, bag.Floats))
               && ((f & PdString) == 0 || ReadStringTable(ref r, bag.Strings))
               && ((f & PdDid) == 0 || ReadUIntTable(ref r, bag.DataIds))
               && ((f & PdIid) == 0 || ReadUIntTable(ref r, bag.InstanceIds));
        truncated = !ok;
        return true;
    }

    private static void AddFinite(Dictionary<uint, double> d, uint key, double v)
    {
        if (double.IsFinite(v)) d.TryAdd(key, v);
    }

    // PackableHashTable on the wire: u16 count, u16 bucket count, then count entries.
    private static bool ReadHeader(ref WireReader r, out int count)
    {
        count = 0;
        if (!r.U16(out ushort n) || !r.Skip(2)) return false;
        count = n;
        return true;
    }

    private static bool ReadIntTable(ref WireReader r, Dictionary<uint, int> d)
    {
        if (!ReadHeader(ref r, out int n) || !r.Has(n * 8)) return false;
        for (int i = 0; i < n; i++) { r.U32(out uint k); r.I32(out int v); d[k] = v; }
        return true;
    }

    private static bool ReadUIntTable(ref WireReader r, Dictionary<uint, uint> d)
    {
        if (!ReadHeader(ref r, out int n) || !r.Has(n * 8)) return false;
        for (int i = 0; i < n; i++) { r.U32(out uint k); r.U32(out uint v); d[k] = v; }
        return true;
    }

    private static bool ReadBoolTable(ref WireReader r, Dictionary<uint, bool> d)
    {
        if (!ReadHeader(ref r, out int n) || !r.Has(n * 8)) return false;
        for (int i = 0; i < n; i++) { r.U32(out uint k); r.U32(out uint v); d[k] = v != 0; }
        return true;
    }

    private static bool ReadInt64Table(ref WireReader r, Dictionary<uint, long> d)
    {
        if (!ReadHeader(ref r, out int n) || !r.Has(n * 12)) return false;
        for (int i = 0; i < n; i++) { r.U32(out uint k); r.I64(out long v); d[k] = v; }
        return true;
    }

    private static bool ReadFloatTable(ref WireReader r, Dictionary<uint, double> d)
    {
        if (!ReadHeader(ref r, out int n) || !r.Has(n * 12)) return false;
        for (int i = 0; i < n; i++)
        {
            r.U32(out uint k);
            r.F64(out double v);
            if (double.IsFinite(v)) d[k] = v;
        }
        return true;
    }

    private static bool ReadStringTable(ref WireReader r, Dictionary<uint, string> d)
    {
        if (!ReadHeader(ref r, out int n)) return false;
        for (int i = 0; i < n; i++)
        {
            if (!r.U32(out uint k) || !r.String16L(out string v)) return false;
            d[k] = v;
        }
        return true;
    }

    /// <summary>Bounds-checked little-endian reader over one message.</summary>
    private ref struct WireReader
    {
        private readonly ReadOnlySpan<byte> _s;
        private int _pos;

        public WireReader(ReadOnlySpan<byte> s) { _s = s; _pos = 0; }

        public bool Has(int n) => n >= 0 && _pos + n <= _s.Length && _pos + n >= _pos;

        public bool Skip(int n)
        {
            if (!Has(n)) return false;
            _pos += n;
            return true;
        }

        public bool U16(out ushort v)
        {
            v = 0;
            if (!Has(2)) return false;
            v = BinaryPrimitives.ReadUInt16LittleEndian(_s.Slice(_pos));
            _pos += 2;
            return true;
        }

        public bool U32(out uint v)
        {
            v = 0;
            if (!Has(4)) return false;
            v = BinaryPrimitives.ReadUInt32LittleEndian(_s.Slice(_pos));
            _pos += 4;
            return true;
        }

        public bool I32(out int v)
        {
            v = 0;
            if (!Has(4)) return false;
            v = BinaryPrimitives.ReadInt32LittleEndian(_s.Slice(_pos));
            _pos += 4;
            return true;
        }

        public bool I64(out long v)
        {
            v = 0;
            if (!Has(8)) return false;
            v = BinaryPrimitives.ReadInt64LittleEndian(_s.Slice(_pos));
            _pos += 8;
            return true;
        }

        public bool F32(out float v)
        {
            v = 0;
            if (!Has(4)) return false;
            v = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(_s.Slice(_pos)));
            _pos += 4;
            return true;
        }

        public bool F64(out double v)
        {
            v = 0;
            if (!Has(8)) return false;
            v = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(_s.Slice(_pos)));
            _pos += 8;
            return true;
        }

        /// <summary>Pads to a 4-byte boundary from the message start (ACE Writer.Align()).</summary>
        public bool Align4() => Skip((4 - (_pos & 3)) & 3);

        /// <summary>
        /// ACE WriteString16L: u16 length, the bytes (code page 1252), then zero padding so
        /// that 2 + length is a multiple of 4.
        /// </summary>
        public bool String16L(out string v)
        {
            v = string.Empty;
            if (!U16(out ushort len) || !Has(len)) return false;
            v = Encoding.Latin1.GetString(_s.Slice(_pos, len));
            _pos += len;
            return Skip((4 - ((2 + len) & 3)) & 3);
        }
    }
}

/// <summary>
/// The PublicWeenieDesc fields every object carries from its CreateObject, filed under the
/// property ids they come from on the server (ACE WorldObject_Networking.SerializeCreateObject)
/// - the only properties the client holds for an object that was never identified. Offsets are
/// from the start of the client's PublicWeenieDesc (Chorizite Weenie.cs PublicWeenieDesc; the
/// engine copies the first <see cref="Size"/> bytes of it in its 10 Hz main-thread walk).
/// A zero field is "not sent" (the header flag was clear), so it answers nothing.
/// </summary>
internal static class PwdLayout
{
    public const int Size = 176;

    public const int Wcid = 12, Icon = 16, IconOverlay = 20, IconUnderlay = 24, Container = 28, Wielder = 32,
                     Priority = 36, ValidLocations = 40, Location = 44, ItemsCapacity = 48, ContainersCapacity = 52,
                     Type = 56, Value = 60, Useability = 64, UseRadius = 68, TargetType = 72, Effects = 76,
                     AmmoType = 80, CombatUse = 84, Structure = 88, MaxStructure = 92, StackSize = 96,
                     MaxStackSize = 100, Bitfield = 104, BlipColor = 108, RadarEnum = 112, Burden = 116,
                     SpellId = 120, HouseOwner = 124, HookType = 136, HookItemTypes = 140, Monarch = 144,
                     MaterialType = 148, Workmanship = 152, CooldownId = 156, CooldownDuration = 160, PetOwner = 168;

    public static int IntOffset(uint stype) => stype switch
    {
        1 => Type,                 // ItemType
        4 => Priority,             // ClothingPriority
        5 => Burden,               // EncumbranceVal
        6 => ItemsCapacity,
        7 => ContainersCapacity,
        9 => ValidLocations,
        10 => Location,            // CurrentWieldedLocation
        11 => MaxStackSize,
        12 => StackSize,
        16 => Useability,          // ItemUseable
        18 => Effects,             // UiEffects
        19 => Value,
        50 => AmmoType,
        51 => CombatUse,
        91 => MaxStructure,
        92 => Structure,
        94 => TargetType,
        95 => BlipColor,           // RadarBlipColor
        131 => MaterialType,
        133 => RadarEnum,          // ShowableOnRadar
        151 => HookType,
        152 => HookItemTypes,      // HookItemType
        280 => CooldownId,         // SharedCooldown
        _ => -1,
    };

    public static int DataIdOffset(uint stype) => stype switch
    {
        8 => Icon,
        28 => SpellId,             // Spell
        50 => IconOverlay,
        52 => IconUnderlay,
        _ => -1,
    };

    public static int InstanceIdOffset(uint stype) => stype switch
    {
        2 => Container,
        3 => Wielder,
        26 => Monarch,
        32 => HouseOwner,
        44 => PetOwner,
        _ => -1,
    };

    /// <summary>v84: every property the CreateObject copy holds, for <see cref="FillBag"/>.</summary>
    public static readonly uint[] IntStypes = { 1, 4, 5, 6, 7, 9, 10, 11, 12, 16, 18, 19, 50, 51, 91, 92, 94, 95, 131, 133, 151, 152, 280 };
    public static readonly uint[] DataIdStypes = { 8, 28, 50, 52 };
    public static readonly uint[] InstanceIdStypes = { 2, 3, 26, 32, 44 };

    /// <summary>
    /// v84 (GetObjectProperties): the CreateObject fields as properties, as the per-key readers
    /// answer them (a zero field answers nothing). Float 280 (the engine's workmanship read) is
    /// left out: it is no property id (workmanship is ItemWorkmanship, int 105, in the identify).
    /// </summary>
    public static void FillBag(ReadOnlySpan<byte> pwd, PropertyBag bag)
    {
        foreach (uint s in IntStypes)
            if (TryGetInt(pwd, s, out int v)) bag.Ints[s] = v;
        foreach (uint s in DataIdStypes)
            if (TryGetDataId(pwd, s, out uint d)) bag.DataIds[s] = d;
        foreach (uint s in InstanceIdStypes)
            if (TryGetInstanceId(pwd, s, out uint i)) bag.InstanceIds[s] = i;
        if (TryGetFloat(pwd, 54, out double r)) bag.Floats[54] = r;
        if (TryGetFloat(pwd, 167, out double c)) bag.Floats[167] = c;
    }

    public static bool TryGetInt(ReadOnlySpan<byte> pwd, uint stype, out int value)
    {
        value = 0;
        int off = IntOffset(stype);
        if (off < 0 || off + 4 > pwd.Length) return false;
        value = BinaryPrimitives.ReadInt32LittleEndian(pwd.Slice(off));
        return value != 0;
    }

    public static bool TryGetDataId(ReadOnlySpan<byte> pwd, uint stype, out uint value)
    {
        value = 0;
        int off = DataIdOffset(stype);
        if (off < 0 || off + 4 > pwd.Length) return false;
        value = BinaryPrimitives.ReadUInt32LittleEndian(pwd.Slice(off));
        return value != 0;
    }

    public static bool TryGetInstanceId(ReadOnlySpan<byte> pwd, uint stype, out uint value)
    {
        value = 0;
        int off = InstanceIdOffset(stype);
        if (off < 0 || off + 4 > pwd.Length) return false;
        value = BinaryPrimitives.ReadUInt32LittleEndian(pwd.Slice(off));
        return value != 0;
    }

    /// <summary>
    /// UseRadius (54, f32), CooldownDuration (167, f64), and 280, the engine's long-standing
    /// read of the item's workmanship (f32; UtilityBelt's SalvageWorkmanship 0xA000009 maps to it).
    /// </summary>
    public static bool TryGetFloat(ReadOnlySpan<byte> pwd, uint stype, out double value)
    {
        value = 0;
        switch (stype)
        {
            case 54:
                if (UseRadius + 4 > pwd.Length) return false;
                value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(pwd.Slice(UseRadius)));
                break;
            case 167:
                if (CooldownDuration + 8 > pwd.Length) return false;
                value = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(pwd.Slice(CooldownDuration)));
                break;
            case 280:
                if (Workmanship + 4 > pwd.Length) return false;
                value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(pwd.Slice(Workmanship)));
                break;
            default:
                return false;
        }
        return value != 0 && double.IsFinite(value);
    }
}
