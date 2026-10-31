// ============================================================================
//  RynthCore.Headless - World/WorldObject.cs
//  One object the client knows about: what CreateObject/UpdateObject said, the
//  properties public updates and identify replies added, and where it is.
// ============================================================================

using RynthCore.Headless.Messages;

namespace RynthCore.Headless.World;

public sealed class WorldObject
{
    public WorldObject(uint id) => Id = id;

    public uint Id { get; }
    public ObjectCreateData Desc { get; internal set; } = new();

    public string Name => Desc.Name;
    public uint WeenieClassId => Desc.WeenieClassId;
    public uint ItemType => Desc.ItemType;
    public uint Icon => Desc.Icon;

    /// <summary>The containing object (pack, corpse, chest); 0 = on the ground / not contained.</summary>
    public uint ContainerId { get; internal set; }
    /// <summary>The creature wielding it; 0 = not wielded.</summary>
    public uint WielderId { get; internal set; }
    public uint WieldedLocation { get; internal set; }
    public uint StackSize { get; internal set; } = 1;
    public int Value { get; internal set; }
    public WorldPosition? Position { get; internal set; }
    public float Vx { get; internal set; }
    public float Vy { get; internal set; }
    public float Vz { get; internal set; }
    public uint PhysicsState { get; internal set; }
    public ushort Stance { get; internal set; }
    /// <summary>Last health fraction from UpdateHealth (creatures), or null if never queried.</summary>
    public float? HealthRatio { get; internal set; }

    // Sequences, kept for the player's own movement packets.
    public ushort InstanceSeq { get; internal set; }
    public ushort PositionSeq { get; internal set; }
    public ushort TeleportSeq { get; internal set; }
    public ushort ServerControlSeq { get; internal set; }
    public ushort ForcePositionSeq { get; internal set; }

    internal readonly Dictionary<uint, int> Ints = new();
    internal readonly Dictionary<uint, long> Int64s = new();
    internal readonly Dictionary<uint, bool> Bools = new();
    internal readonly Dictionary<uint, double> Floats = new();
    internal readonly Dictionary<uint, string> Strings = new();
    internal readonly Dictionary<uint, uint> DataIds = new();
    internal readonly Dictionary<uint, uint> InstanceIds = new();
    internal uint[] SpellBook = Array.Empty<uint>();

    public bool Identified { get; internal set; }
    /// <summary>A CreateObject/UpdateObject has described it (false for placeholders from inventory events).</summary>
    public bool Described { get; private set; }
    public long CreatedMs { get; internal set; }
    public long UpdatedMs { get; internal set; }

    public bool TryGetInt(uint key, out int v) => Ints.TryGetValue(key, out v);
    public bool TryGetInt64(uint key, out long v) => Int64s.TryGetValue(key, out v);
    public bool TryGetBool(uint key, out bool v) => Bools.TryGetValue(key, out v);
    public bool TryGetFloat(uint key, out double v) => Floats.TryGetValue(key, out v);
    public bool TryGetString(uint key, out string v) => Strings.TryGetValue(key, out v!);
    public bool TryGetDataId(uint key, out uint v) => DataIds.TryGetValue(key, out v);
    public bool TryGetInstanceId(uint key, out uint v) => InstanceIds.TryGetValue(key, out v);
    public IReadOnlyList<uint> Spells => SpellBook;

    public bool IsCreature => (ItemType & 0x10) != 0;
    public bool IsContainer => (ItemType & 0x200) != 0;

    internal void Apply(ObjectCreateData d, long nowMs)
    {
        Described = true;
        Desc = d;
        ContainerId = d.ContainerId ?? 0;
        WielderId = d.WielderId ?? 0;
        WieldedLocation = d.CurrentWieldedLocation ?? 0;
        StackSize = d.StackSize ?? 1;
        Value = d.Value ?? 0;
        if (d.Position.HasValue) Position = d.Position;
        PhysicsState = d.PhysicsState;
        if (d.Sequences.Length == 9)
        {
            PositionSeq = d.Sequences[0];
            TeleportSeq = d.Sequences[4];
            ServerControlSeq = d.Sequences[5];
            ForcePositionSeq = d.Sequences[6];
            InstanceSeq = d.Sequences[8];
        }
        UpdatedMs = nowMs;
        if (CreatedMs == 0) CreatedMs = nowMs;
    }

    public override string ToString() => $"0x{Id:X8} {Name}";
}
