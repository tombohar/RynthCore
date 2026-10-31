// ============================================================================
//  RynthCore.Headless - Messages/ObjectCreate.cs
//  0xF745 CreateObject and 0xF7DB UpdateObject (same body): guid, model data (walked
//  over), physics data (position, setup, parent, sequences) and the whole weenie header.
//
//  Alignment is to 4 bytes from the start of the message (opcode included); the body
//  starts at 4, so aligning the body offset gives the same result.
// ============================================================================

using RynthCore.PluginSdk.Net;

namespace RynthCore.Headless.Messages;

public readonly record struct WorldPosition(uint Cell, float X, float Y, float Z, float Qw, float Qx, float Qy, float Qz)
{
    public uint Landblock => Cell >> 16;
}

/// <summary>Everything CreateObject/UpdateObject says about an object. Absent optional fields are null.</summary>
public sealed class ObjectCreateData
{
    public uint Id;
    public string Name = "";
    public uint WeenieClassId, Icon, ItemType, DescriptionFlags;
    public uint PhysicsFlags, PhysicsState, SetupId, ParentId, ParentLocation;
    public float? Scale;
    public WorldPosition? Position;
    public ushort[] Sequences = Array.Empty<ushort>();

    public uint WeenieFlags, WeenieFlags2;
    public string? PluralName;
    public byte? ItemsCapacity, ContainersCapacity, RadarColor, RadarBehavior;
    public ushort? AmmoType, Structure, MaxStructure, StackSize, MaxStackSize, Script, Burden, Spell, HookType;
    public int? Value, CooldownId;
    public uint? Usable, TargetType, UiEffects, ContainerId, WielderId, ValidLocations, CurrentWieldedLocation,
        Priority, HouseOwner, HookItemTypes, Monarch, IconOverlay, IconUnderlay, MaterialType, PetOwner;
    public float? UseRadius, Workmanship;
    public sbyte? CombatUse;
    public double? CooldownDuration;

    /// <summary>The instance sequence (the object's "life": a recreated object has a newer one).</summary>
    public ushort InstanceSequence => Sequences.Length == 9 ? Sequences[8] : (ushort)0;
}

public static class ObjectCreate
{
    // PhysicsDescriptionFlag
    private const uint CSetup = 0x1, MTable = 0x2, Velocity = 0x4, Acceleration = 0x8, Omega = 0x10, Parent = 0x20,
        Children = 0x40, ObjScale = 0x80, Friction = 0x100, Elasticity = 0x200, STable = 0x800, PeTable = 0x1000,
        DefaultScript = 0x2000, DefaultScriptIntensity = 0x4000, Position = 0x8000, Movement = 0x10000,
        AnimationFrame = 0x20000, Translucency = 0x40000;

    // WeenieHeaderFlags
    private const uint WPluralName = 0x1, WItemsCapacity = 0x2, WContainersCapacity = 0x4, WValue = 0x8, WUsable = 0x10,
        WUseRadius = 0x20, WMonarch = 0x40, WUiEffects = 0x80, WAmmoType = 0x100, WCombatUse = 0x200, WStructure = 0x400,
        WMaxStructure = 0x800, WStackSize = 0x1000, WMaxStackSize = 0x2000, WContainer = 0x4000, WWielder = 0x8000,
        WValidLocations = 0x10000, WCurrentlyWielded = 0x20000, WPriority = 0x40000, WTargetType = 0x80000,
        WRadarColor = 0x100000, WBurden = 0x200000, WSpell = 0x400000, WRadarBehavior = 0x800000,
        WWorkmanship = 0x1000000, WHouseOwner = 0x2000000, WHouseRestrictions = 0x4000000, WScript = 0x8000000,
        WHookType = 0x10000000, WHookItemTypes = 0x20000000, WIconOverlay = 0x40000000, WMaterialType = 0x80000000;
    // WeenieHeaderFlag2
    private const uint W2IconUnderlay = 0x1, W2Cooldown = 0x2, W2CooldownDuration = 0x4, W2PetOwner = 0x8;
    private const uint IncludesSecondHeader = 0x04000000;
    private const uint IconType = 0x06000000;

    public static bool TryParse(ReadOnlySpan<byte> body, out ObjectCreateData obj)
    {
        obj = new ObjectCreateData();
        var r = new NetReader(body);
        if (!r.U32(out obj.Id)) return false;
        if (!SkipModelData(ref r)) return false;
        if (!ReadPhysics(ref r, obj)) return false;
        return ReadWeenieHeader(ref r, obj);
    }

    /// <summary>AC packed u32: u16, and if its high bit is set, a second u16 for the low word.</summary>
    public static bool PackedU32(ref NetReader r, out uint v)
    {
        v = 0;
        if (!r.U16(out ushort a)) return false;
        if ((a & 0x8000) == 0) { v = a; return true; }
        if (!r.U16(out ushort b)) return false;
        v = ((uint)(a & 0x7FFF) << 16) | b;
        return true;
    }

    private static bool PackedKnown(ref NetReader r, uint type, out uint v)
    {
        if (!PackedU32(ref r, out v)) return false;
        if (v != 0) v |= type;
        return true;
    }

    internal static bool Align(ref NetReader r) => r.Skip((4 - (r.Position & 3)) & 3);

    private static bool SkipModelData(ref NetReader r)
    {
        if (!r.U8(out _) || !r.U8(out byte palettes) || !r.U8(out byte textures) || !r.U8(out byte parts)) return false;
        if (palettes > 0 && !PackedU32(ref r, out _)) return false;
        for (int i = 0; i < palettes; i++)
            if (!PackedU32(ref r, out _) || !r.Skip(2)) return false;
        for (int i = 0; i < textures; i++)
            if (!r.Skip(1) || !PackedU32(ref r, out _) || !PackedU32(ref r, out _)) return false;
        for (int i = 0; i < parts; i++)
            if (!r.Skip(1) || !PackedU32(ref r, out _)) return false;
        return Align(ref r);
    }

    internal static bool ReadPosition(ref NetReader r, out WorldPosition pos)
    {
        pos = default;
        if (!r.U32(out uint cell) || !r.F32(out float x) || !r.F32(out float y) || !r.F32(out float z)
            || !r.F32(out float qw) || !r.F32(out float qx) || !r.F32(out float qy) || !r.F32(out float qz))
            return false;
        pos = new WorldPosition(cell, x, y, z, qw, qx, qy, qz);
        return true;
    }

    private static bool ReadPhysics(ref NetReader r, ObjectCreateData o)
    {
        if (!r.U32(out uint flags) || !r.U32(out o.PhysicsState)) return false;
        o.PhysicsFlags = flags;

        if ((flags & Movement) != 0)
        {
            if (!r.U32(out uint len) || !r.Skip((int)len)) return false;
            if (len > 0 && !r.Skip(4)) return false;
        }
        else if ((flags & AnimationFrame) != 0 && !r.Skip(4)) return false;

        if ((flags & Position) != 0)
        {
            if (!ReadPosition(ref r, out WorldPosition p)) return false;
            o.Position = p;
        }
        if ((flags & MTable) != 0 && !r.Skip(4)) return false;
        if ((flags & STable) != 0 && !r.Skip(4)) return false;
        if ((flags & PeTable) != 0 && !r.Skip(4)) return false;
        if ((flags & CSetup) != 0 && !r.U32(out o.SetupId)) return false;
        if ((flags & Parent) != 0 && (!r.U32(out o.ParentId) || !r.U32(out o.ParentLocation))) return false;
        if ((flags & Children) != 0)
        {
            if (!r.ListCount(out int n, 8) || !r.Skip(n * 8)) return false;
        }
        if ((flags & ObjScale) != 0)
        {
            if (!r.F32(out float s)) return false;
            o.Scale = s;
        }
        if ((flags & Friction) != 0 && !r.Skip(4)) return false;
        if ((flags & Elasticity) != 0 && !r.Skip(4)) return false;
        if ((flags & Translucency) != 0 && !r.Skip(4)) return false;
        if ((flags & Velocity) != 0 && !r.Skip(12)) return false;
        if ((flags & Acceleration) != 0 && !r.Skip(12)) return false;
        if ((flags & Omega) != 0 && !r.Skip(12)) return false;
        if ((flags & DefaultScript) != 0 && !r.Skip(4)) return false;
        if ((flags & DefaultScriptIntensity) != 0 && !r.Skip(4)) return false;

        o.Sequences = new ushort[9];
        for (int i = 0; i < 9; i++)
            if (!r.U16(out o.Sequences[i])) return false;
        return Align(ref r);
    }

    private static bool ReadWeenieHeader(ref NetReader r, ObjectCreateData o)
    {
        if (!r.U32(out uint f) || !r.String16L(out o.Name)) return false;
        if (!PackedU32(ref r, out o.WeenieClassId) || !PackedKnown(ref r, IconType, out o.Icon)) return false;
        if (!r.U32(out o.ItemType) || !r.U32(out o.DescriptionFlags) || !Align(ref r)) return false;
        o.WeenieFlags = f;
        uint f2 = 0;
        if ((o.DescriptionFlags & IncludesSecondHeader) != 0 && !r.U32(out f2)) return false;
        o.WeenieFlags2 = f2;

        uint u32; ushort u16; byte u8; float f32;
        if ((f & WPluralName) != 0) { if (!r.String16L(out string pl)) return false; o.PluralName = pl; }
        if ((f & WItemsCapacity) != 0) { if (!r.U8(out u8)) return false; o.ItemsCapacity = u8; }
        if ((f & WContainersCapacity) != 0) { if (!r.U8(out u8)) return false; o.ContainersCapacity = u8; }
        if ((f & WAmmoType) != 0) { if (!r.U16(out u16)) return false; o.AmmoType = u16; }
        if ((f & WValue) != 0) { if (!r.I32(out int v)) return false; o.Value = v; }
        if ((f & WUsable) != 0) { if (!r.U32(out u32)) return false; o.Usable = u32; }
        if ((f & WUseRadius) != 0) { if (!r.F32(out f32)) return false; o.UseRadius = f32; }
        if ((f & WTargetType) != 0) { if (!r.U32(out u32)) return false; o.TargetType = u32; }
        if ((f & WUiEffects) != 0) { if (!r.U32(out u32)) return false; o.UiEffects = u32; }
        if ((f & WCombatUse) != 0) { if (!r.U8(out u8)) return false; o.CombatUse = (sbyte)u8; }
        if ((f & WStructure) != 0) { if (!r.U16(out u16)) return false; o.Structure = u16; }
        if ((f & WMaxStructure) != 0) { if (!r.U16(out u16)) return false; o.MaxStructure = u16; }
        if ((f & WStackSize) != 0) { if (!r.U16(out u16)) return false; o.StackSize = u16; }
        if ((f & WMaxStackSize) != 0) { if (!r.U16(out u16)) return false; o.MaxStackSize = u16; }
        if ((f & WContainer) != 0) { if (!r.U32(out u32)) return false; o.ContainerId = u32; }
        if ((f & WWielder) != 0) { if (!r.U32(out u32)) return false; o.WielderId = u32; }
        if ((f & WValidLocations) != 0) { if (!r.U32(out u32)) return false; o.ValidLocations = u32; }
        if ((f & WCurrentlyWielded) != 0) { if (!r.U32(out u32)) return false; o.CurrentWieldedLocation = u32; }
        if ((f & WPriority) != 0) { if (!r.U32(out u32)) return false; o.Priority = u32; }
        if ((f & WRadarColor) != 0) { if (!r.U8(out u8)) return false; o.RadarColor = u8; }
        if ((f & WRadarBehavior) != 0) { if (!r.U8(out u8)) return false; o.RadarBehavior = u8; }
        if ((f & WScript) != 0) { if (!r.U16(out u16)) return false; o.Script = u16; }
        if ((f & WWorkmanship) != 0) { if (!r.F32(out f32)) return false; o.Workmanship = f32; }
        if ((f & WBurden) != 0) { if (!r.U16(out u16)) return false; o.Burden = u16; }
        if ((f & WSpell) != 0) { if (!r.U16(out u16)) return false; o.Spell = u16; }
        if ((f & WHouseOwner) != 0) { if (!r.U32(out u32)) return false; o.HouseOwner = u32; }
        if ((f & WHouseRestrictions) != 0)
        {
            // version, open status, monarch, then a hash table of (character, access).
            if (!r.Skip(12) || !r.TableCount(out int n, 8) || !r.Skip(n * 8)) return false;
        }
        if ((f & WHookItemTypes) != 0) { if (!r.U32(out u32)) return false; o.HookItemTypes = u32; }
        if ((f & WMonarch) != 0) { if (!r.U32(out u32)) return false; o.Monarch = u32; }
        if ((f & WHookType) != 0) { if (!r.U16(out u16)) return false; o.HookType = u16; }
        if ((f & WIconOverlay) != 0) { if (!PackedKnown(ref r, IconType, out u32)) return false; o.IconOverlay = u32; }
        if ((f2 & W2IconUnderlay) != 0) { if (!PackedKnown(ref r, IconType, out u32)) return false; o.IconUnderlay = u32; }
        if ((f & WMaterialType) != 0) { if (!r.U32(out u32)) return false; o.MaterialType = u32; }
        if ((f2 & W2Cooldown) != 0) { if (!r.I32(out int cd)) return false; o.CooldownId = cd; }
        if ((f2 & W2CooldownDuration) != 0) { if (!r.F64(out double cdd)) return false; o.CooldownDuration = cdd; }
        if ((f2 & W2PetOwner) != 0) { if (!r.U32(out u32)) return false; o.PetOwner = u32; }
        return Align(ref r);
    }
}
