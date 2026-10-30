// ============================================================================
//  RynthCore.Engine - UI/ScriptWindows/AcIconPlates.cs
//  What an item's icon is drawn ON.
//
//  An AC item icon is a silhouette (24-83% of its texels are transparent), so retail composites it
//  over an opaque 32x32 PLATE, and which plate is most of an item's colour on screen: armour blue,
//  ammunition brown, pouches green, anything with spells blue. Nothing is hardcoded: two DID mappers
//  in client_portal.dat answer it, 0x25000008 (ItemType -> plate) and 0x25000009 (UiEffects -> plate).
//  Both key on the BIT INDEX plus one (MeleeWeapon 0x1 is key 1, Armor 0x2 is key 2 ...), and key 33
//  is the table's own "Default".
//
//  The order they are asked in is the finding (AcClientReborn's IconPlates, measured against retail
//  captures): the server's own IconUnderlay for the item wins outright; then the effect table
//  (UiEffects is consulted BEFORE ItemType, which is why an enchanted item is blue and void gear is
//  black); then the item-type table; then that table's Default.
//
//  Mapper layout: file id u32, a numbering byte, a compressed count, count x (key u32, plate id u32),
//  then names (not needed). Pure code over AcDatFile; the plate textures are ordinary 0x06 files.
//  Thread: whatever thread owns the AcDatFile (the icon worker).
// ============================================================================

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;

namespace RynthCore.Engine.UI.ScriptWindows;

internal static class AcIconPlates
{
    public const uint IconBackgrounds = 0x25000008u, EffectIcons = 0x25000009u;
    private const uint DefaultKey = 33;

    private static Dictionary<uint, uint>? _byType, _byEffect;
    private static bool _loaded;

    /// <summary>The plate this item asks for; 0 when the dat has neither table (the icon is then drawn bare).</summary>
    public static uint For(AcDatFile dat, uint underlay, uint uiEffects, uint itemType)
    {
        if (underlay != 0) return underlay;
        Load(dat);
        // The plate comes from the item type only: retail shows a magical item's armour on the same blue as
        // a plain one. The effect table colours the OUTLINE instead (RimFor).
        return PlateFor(underlay, 0, itemType, _byEffect, _byType);
    }

    /// <summary>
    /// The texture whose colours the icon's outline takes for these effects (0 = a plain item: the plain outline).
    /// UiEffects keys the table by bit index + 1 (Magical is key 1 and the blue gradient); a bit the table has no
    /// entry for takes its Default (black: why void gear has a black outline).
    /// </summary>
    public static uint RimFor(AcDatFile dat, uint uiEffects)
    {
        if (uiEffects == 0) return 0;
        Load(dat);
        if (_byEffect == null) return 0;
        uint e = ForBit(_byEffect, uiEffects);
        if (e != 0) return e;
        return _byEffect.TryGetValue(DefaultKey, out uint d) ? d : 0;
    }

    /// <summary>The lookup order, apart from the dat (testable).</summary>
    internal static uint PlateFor(uint underlay, uint uiEffects, uint itemType,
        Dictionary<uint, uint>? effects, Dictionary<uint, uint>? types)
    {
        if (underlay != 0) return underlay;
        if (effects != null && uiEffects != 0)
        {
            uint e = ForBit(effects, uiEffects);
            if (e != 0) return e;
            // A UiEffects bit the table has no entry for takes the effect table's Default, not the
            // item type's (this is why void gear is black).
            if (effects.TryGetValue(DefaultKey, out uint ed) && ed != 0) return ed;
        }
        if (types != null)
        {
            uint t = ForBit(types, itemType);
            if (t != 0) return t;
            if (types.TryGetValue(DefaultKey, out uint td)) return td;
        }
        return 0;
    }

    /// <summary>The lowest set bit decides (an item carries one ItemType in practice).</summary>
    private static uint ForBit(Dictionary<uint, uint> map, uint bits)
    {
        if (bits == 0) return 0;
        uint key = (uint)BitOperations.TrailingZeroCount(bits) + 1;
        return map.TryGetValue(key, out uint id) ? id : 0;
    }

    private static void Load(AcDatFile dat)
    {
        if (_loaded) return;
        _loaded = true;   // one try per session: a dat without them draws bare icons
        _byType = ReadMapper(dat.Read(IconBackgrounds, 64 * 1024));
        _byEffect = ReadMapper(dat.Read(EffectIcons, 64 * 1024));
    }

    /// <summary>A DID mapper file's key -> id pairs, or null when it does not parse.</summary>
    internal static Dictionary<uint, uint>? ReadMapper(byte[]? data)
    {
        if (data == null || data.Length < 9) return null;
        try
        {
            int at = 5;   // file id, numbering byte
            uint n = Compressed(data, ref at);
            if (n > 100000 || at + (long)n * 8 > data.Length) return null;
            var map = new Dictionary<uint, uint>((int)n);
            for (uint i = 0; i < n; i++)
            {
                map[BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at))] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 4));
                at += 8;
            }
            return map;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>AC's compressed uint: one byte when the top bit is clear, two when 0x80 is set, four when 0xC0 is.</summary>
    private static uint Compressed(byte[] d, ref int at)
    {
        byte b0 = d[at++];
        if ((b0 & 0x80) == 0) return b0;
        byte b1 = d[at++];
        if ((b0 & 0x40) == 0) return (uint)(((b0 & 0x7F) << 8) | b1);
        int low = d[at] | (d[at + 1] << 8);   // the low half is little-endian
        at += 2;
        return (uint)(((((b0 & 0x3F) << 8) | b1) << 16) | low);
    }
}
