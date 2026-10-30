using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace RynthCore.PluginSdk;

// ─── Every property of an object (engine API v84) ──────────────────────────
// RynthCoreHost.TryGetObjectProperties returns all the keys the engine holds for one object
// (what UtilityBelt's propertydump lists from Decal's WorldObject), not one key at a time:
//
//   if (Host.TryGetObjectProperties(id, out ObjectProperties p))
//       foreach (var kv in p.Ints) Log($"{kv.Key} = {kv.Value}");
//
// Item properties beyond the CreateObject basics only exist after an identify (RequestId):
// check p.HasIdData. Asked off AC's main thread, the client's own qualities tables are read at
// the next main-thread tick: when p.QualitiesPending is set, ask again ~0.2 s later.

/// <summary>What <see cref="ObjectProperties"/> was built from (the engine's record flags).</summary>
[Flags]
public enum ObjectPropertyFlags : ushort
{
    None = 0,
    /// <summary>The server answered an identify (appraisal) for the object this session.</summary>
    HasIdData = 0x0001,
    /// <summary>The client's own qualities tables of the object were read in.</summary>
    QualitiesRead = 0x0002,
    /// <summary>A read of the client's qualities tables was requested for AC's next main-thread tick: ask again shortly.</summary>
    QualitiesPending = 0x0004,
    /// <summary>The object is the player's character.</summary>
    IsPlayer = 0x0008,
    /// <summary>The client's CreateObject copy of the object was read in.</summary>
    HasCreateData = 0x0010,
}

/// <summary>Every property of one object, a dictionary per AC property type (keys are the AC/ACE property ids).</summary>
public sealed class ObjectProperties
{
    public const uint RecordMagic = 0x31445052;   // "RPD1"
    public const ushort RecordVersion = 1;
    private const int HeaderSize = 20;
    private const byte KindInt = 0, KindInt64 = 1, KindBool = 2, KindFloat = 3, KindString = 4,
                       KindDataId = 5, KindInstanceId = 6, KindSpell = 7;

    public ObjectPropertyFlags Flags { get; private set; }
    /// <summary>Unix seconds of the last identify answer; 0 never.</summary>
    public long LastIdTime { get; private set; }

    public bool HasIdData => (Flags & ObjectPropertyFlags.HasIdData) != 0;
    public bool QualitiesPending => (Flags & ObjectPropertyFlags.QualitiesPending) != 0;
    public bool IsPlayer => (Flags & ObjectPropertyFlags.IsPlayer) != 0;

    public Dictionary<uint, int> Ints { get; } = new();
    public Dictionary<uint, long> Int64s { get; } = new();
    public Dictionary<uint, bool> Bools { get; } = new();
    public Dictionary<uint, double> Floats { get; } = new();
    public Dictionary<uint, string> Strings { get; } = new();
    /// <summary>Data ids (STypeDID: icons, setups, spell, ...).</summary>
    public Dictionary<uint, uint> DataIds { get; } = new();
    /// <summary>Instance ids (STypeIID: container, wielder, owner, monarch, ...).</summary>
    public Dictionary<uint, uint> InstanceIds { get; } = new();
    /// <summary>The spell book from the last identify, in its order.</summary>
    public List<uint> Spells { get; } = new();

    public int Count => Ints.Count + Int64s.Count + Bools.Count + Floats.Count + Strings.Count
                      + DataIds.Count + InstanceIds.Count + Spells.Count;

    /// <summary>
    /// Reads the engine's v84 record. False (and <paramref name="props"/> null) when it isn't
    /// one: wrong magic, a newer format, or an entry that runs past the end.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> rec, out ObjectProperties? props)
    {
        props = null;
        if (rec.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(rec) != RecordMagic)
            return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(rec.Slice(4)) != RecordVersion)
            return false;
        var p = new ObjectProperties
        {
            Flags = (ObjectPropertyFlags)BinaryPrimitives.ReadUInt16LittleEndian(rec.Slice(6)),
            LastIdTime = BinaryPrimitives.ReadInt64LittleEndian(rec.Slice(8)),
        };
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(16));
        int o = HeaderSize;
        for (uint i = 0; i < count; i++)
        {
            if (o + 5 > rec.Length) return false;
            byte kind = rec[o];
            uint key = BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(o + 1));
            o += 5;
            int need = kind switch
            {
                KindInt or KindDataId or KindInstanceId => 4,
                KindInt64 or KindFloat => 8,
                KindBool => 1,
                KindString => 2,
                KindSpell => 0,
                _ => -1,
            };
            if (need < 0 || o + need > rec.Length) return false;
            switch (kind)
            {
                case KindInt: p.Ints[key] = BinaryPrimitives.ReadInt32LittleEndian(rec.Slice(o)); break;
                case KindInt64: p.Int64s[key] = BinaryPrimitives.ReadInt64LittleEndian(rec.Slice(o)); break;
                case KindBool: p.Bools[key] = rec[o] != 0; break;
                case KindFloat: p.Floats[key] = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(rec.Slice(o))); break;
                case KindDataId: p.DataIds[key] = BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(o)); break;
                case KindInstanceId: p.InstanceIds[key] = BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(o)); break;
                case KindSpell: p.Spells.Add(key); break;
                case KindString:
                {
                    int len = BinaryPrimitives.ReadUInt16LittleEndian(rec.Slice(o));
                    if (o + 2 + len > rec.Length) return false;
                    p.Strings[key] = Encoding.UTF8.GetString(rec.Slice(o + 2, len));
                    o += 2 + len;
                    continue;
                }
            }
            o += need;
        }
        props = p;
        return true;
    }
}
