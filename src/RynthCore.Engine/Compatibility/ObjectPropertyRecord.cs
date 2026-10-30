// ============================================================================
//  RynthCore.Engine - Compatibility/ObjectPropertyRecord.cs
//
//  Plugin API v84 (GetObjectProperties): every property the engine knows for
//  one object, as one compact buffer the SDK turns into a dictionary per type
//  (RynthCore.PluginSdk ObjectProperties.TryParse). Pure: no AC memory, no
//  engine state, so tools\ObjectPropertyTests compiles it in with the SDK's
//  parser and round-trips it.
//
//  Before v84 a plugin could only ask for one key at a time, so RynthAi's
//  /ub propertydump probed fixed ranges (int 0-400, bool 0-300, ...) and missed
//  every key outside them, had no spell book and no names for int64 / float /
//  data id / instance id properties. UtilityBelt prints every key Decal holds.
//
//  Record (little-endian):
//    +0  u32  magic 0x31445052 ("RPD1")
//    +4  u16  format version (1)
//    +6  u16  flags (ObjectPropertyFlags)
//    +8  i64  last identify time, Unix seconds (0 never identified)
//    +16 u32  entry count
//    +20 entries, sorted by kind then key:
//         u8 kind (PropertyKind 0-6, 7 = a spell of the spell book), u32 key, value:
//         Int i32 | Int64 i64 | Bool u8 | Float f64 | String u16 byte count + UTF-8 |
//         DataId u32 | InstanceId u32 | Spell (no value; the key is the spell id)
// ============================================================================

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace RynthCore.Engine.Compatibility;

/// <summary>v84 record flags (the SDK's ObjectPropertyFlags has the same bits).</summary>
[Flags]
internal enum ObjectPropertyFlags : ushort
{
    None = 0,
    /// <summary>The server answered an identify (appraisal) for the object this session.</summary>
    HasIdData = 0x0001,
    /// <summary>The client's own qualities tables of the object (CBaseQualities) were read in.</summary>
    QualitiesRead = 0x0002,
    /// <summary>Asked off AC's main thread: a walk of the object's qualities tables was requested for
    /// the next main-thread tick. Ask again in a moment for the full set.</summary>
    QualitiesPending = 0x0004,
    /// <summary>The object is the player's character.</summary>
    IsPlayer = 0x0008,
    /// <summary>The client's CreateObject copy (PublicWeenieDesc snapshot) of the object was read in.</summary>
    HasCreateData = 0x0010,
}

internal static class ObjectPropertyRecord
{
    public const uint Magic = 0x31445052;   // "RPD1"
    public const ushort FormatVersion = 1;
    public const int HeaderSize = 20;
    public const byte KindSpell = 7;
    /// <summary>Longest string kept (bytes of UTF-8); longer ones are cut at a character boundary.</summary>
    public const int MaxStringBytes = 4096;
    /// <summary>Most spells kept from a spell book.</summary>
    public const int MaxSpells = 1024;

    /// <summary>
    /// One object's properties from every source, in the order the per-key reads
    /// (TryGetObject*Property / PropertyCaches) prefer them, so the dump shows what a per-key read
    /// returns. Lowest first, later sources replace same-key values:
    ///   the player:  CreateObject copy, last identify, client qualities, public updates, own record
    ///                (PlayerDescription + private updates);
    ///   any other:   CreateObject copy, client qualities, public updates, last identify (instance
    ///                ids: the last identify, then public updates over it).
    /// Then the client's own stack size and container / wielder win when set, as in the per-key reads.
    /// Null sources are skipped. Never changes the sources.
    /// </summary>
    public static PropertyBag Combine(bool isPlayer, PropertyBag? createData, PropertyBag? qualities,
        PropertyBag? identify, PropertyBag? publicUpdates, PropertyBag? self)
    {
        var all = new PropertyBag();
        if (createData != null) all.MergeFrom(createData);
        if (isPlayer)
        {
            if (identify != null) all.MergeFrom(identify);
            if (qualities != null) all.MergeFrom(qualities);
            if (publicUpdates != null) all.MergeFrom(publicUpdates);
            if (self != null) all.MergeFrom(self);
        }
        else
        {
            if (qualities != null) all.MergeFrom(qualities);
            if (publicUpdates != null) all.MergeFrom(publicUpdates);
            if (identify != null) all.MergeFrom(identify);
            if (identify != null && publicUpdates != null)
                foreach (var kv in publicUpdates.InstanceIds) all.InstanceIds[kv.Key] = kv.Value;
        }
        if (createData != null)
        {
            if (createData.Ints.TryGetValue(12, out int stack) && stack > 0) all.Ints[12] = stack;   // StackSize
            if (createData.InstanceIds.TryGetValue(2, out uint container) && container != 0) all.InstanceIds[2] = container;
            if (createData.InstanceIds.TryGetValue(3, out uint wielder) && wielder != 0) all.InstanceIds[3] = wielder;
        }
        return all;
    }

    /// <summary>Encodes the record (the layout above). Spells are kept in their spell book order, without repeats.</summary>
    public static byte[] Encode(PropertyBag bag, IReadOnlyList<uint>? spells, ObjectPropertyFlags flags, long lastIdTime)
    {
        var strings = new List<(uint Key, byte[] Bytes)>(bag.Strings.Count);
        foreach (uint key in SortedKeys(bag.Strings.Keys))
            strings.Add((key, Utf8Cut(bag.Strings[key] ?? string.Empty)));

        var spellList = new List<uint>();
        if (spells != null)
        {
            var seen = new HashSet<uint>();
            foreach (uint s in spells)
                if (s != 0 && seen.Add(s) && spellList.Count < MaxSpells) spellList.Add(s);
        }

        int count = bag.Ints.Count + bag.Int64s.Count + bag.Bools.Count + bag.Floats.Count + strings.Count
                  + bag.DataIds.Count + bag.InstanceIds.Count + spellList.Count;
        int size = HeaderSize
                 + bag.Ints.Count * 9 + bag.Int64s.Count * 13 + bag.Bools.Count * 6 + bag.Floats.Count * 13
                 + bag.DataIds.Count * 9 + bag.InstanceIds.Count * 9 + spellList.Count * 5;
        foreach (var s in strings) size += 7 + s.Bytes.Length;

        byte[] buf = new byte[size];
        Span<byte> o = buf;
        BinaryPrimitives.WriteUInt32LittleEndian(o, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(o.Slice(4), FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(o.Slice(6), (ushort)flags);
        BinaryPrimitives.WriteInt64LittleEndian(o.Slice(8), lastIdTime);
        BinaryPrimitives.WriteUInt32LittleEndian(o.Slice(16), (uint)count);
        int p = HeaderSize;

        foreach (uint k in SortedKeys(bag.Ints.Keys))
        { p = Head(o, p, PropertyKind.Int, k); BinaryPrimitives.WriteInt32LittleEndian(o.Slice(p), bag.Ints[k]); p += 4; }
        foreach (uint k in SortedKeys(bag.Int64s.Keys))
        { p = Head(o, p, PropertyKind.Int64, k); BinaryPrimitives.WriteInt64LittleEndian(o.Slice(p), bag.Int64s[k]); p += 8; }
        foreach (uint k in SortedKeys(bag.Bools.Keys))
        { p = Head(o, p, PropertyKind.Bool, k); o[p++] = bag.Bools[k] ? (byte)1 : (byte)0; }
        foreach (uint k in SortedKeys(bag.Floats.Keys))
        { p = Head(o, p, PropertyKind.Float, k); BinaryPrimitives.WriteInt64LittleEndian(o.Slice(p), BitConverter.DoubleToInt64Bits(bag.Floats[k])); p += 8; }
        foreach (var s in strings)
        {
            p = Head(o, p, PropertyKind.String, s.Key);
            BinaryPrimitives.WriteUInt16LittleEndian(o.Slice(p), (ushort)s.Bytes.Length); p += 2;
            s.Bytes.CopyTo(o.Slice(p)); p += s.Bytes.Length;
        }
        foreach (uint k in SortedKeys(bag.DataIds.Keys))
        { p = Head(o, p, PropertyKind.DataId, k); BinaryPrimitives.WriteUInt32LittleEndian(o.Slice(p), bag.DataIds[k]); p += 4; }
        foreach (uint k in SortedKeys(bag.InstanceIds.Keys))
        { p = Head(o, p, PropertyKind.InstanceId, k); BinaryPrimitives.WriteUInt32LittleEndian(o.Slice(p), bag.InstanceIds[k]); p += 4; }
        foreach (uint s in spellList)
        { o[p++] = KindSpell; BinaryPrimitives.WriteUInt32LittleEndian(o.Slice(p), s); p += 4; }

        return buf;
    }

    private static int Head(Span<byte> o, int p, PropertyKind kind, uint key)
    {
        o[p] = (byte)kind;
        BinaryPrimitives.WriteUInt32LittleEndian(o.Slice(p + 1), key);
        return p + 5;
    }

    private static List<uint> SortedKeys<T>(Dictionary<uint, T>.KeyCollection keys)
    {
        var list = new List<uint>(keys);
        list.Sort();
        return list;
    }

    // UTF-8 of s, at most MaxStringBytes, never ending inside a character.
    private static byte[] Utf8Cut(string s)
    {
        byte[] b = Encoding.UTF8.GetBytes(s);
        if (b.Length <= MaxStringBytes) return b;
        int n = MaxStringBytes;
        while (n > 0 && (b[n] & 0xC0) == 0x80) n--;   // b[n] starts the first character left out
        return b.AsSpan(0, n).ToArray();
    }
}
