using System.Buffers.Binary;
using System.Text;

namespace NetMessageTests;

/// <summary>Little-endian writer for message bodies, in the AC wire types the parsers read.</summary>
internal sealed class Body
{
    private readonly List<byte> _b = new();

    public byte[] ToArray() => _b.ToArray();
    public int Length => _b.Count;

    public Body U16(ushort v) { Span<byte> t = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(t, v); _b.AddRange(t.ToArray()); return this; }
    public Body U32(uint v) { Span<byte> t = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(t, v); _b.AddRange(t.ToArray()); return this; }
    public Body I32(int v) => U32(unchecked((uint)v));
    public Body F32(float v) => U32(BitConverter.SingleToUInt32Bits(v));
    public Body Bool(bool v) => U32(v ? 1u : 0u);
    public Body Bytes(params byte[] v) { _b.AddRange(v); return this; }

    /// <summary>u16 length, Latin-1 bytes, zero padding so 2 + length is a multiple of 4.</summary>
    public Body Str(string s)
    {
        byte[] raw = Encoding.Latin1.GetBytes(s);
        U16((ushort)raw.Length);
        _b.AddRange(raw);
        for (int pad = (4 - ((2 + raw.Length) & 3)) & 3; pad > 0; pad--) _b.Add(0);
        return this;
    }

    public Body U32List(params uint[] v) { U32((uint)v.Length); foreach (uint x in v) U32(x); return this; }
    public Body TableHeader(int count, int buckets = 32) => U16((ushort)count).U16((ushort)buckets);

    /// <summary>A whole top-level message: u32 opcode then this body.</summary>
    public byte[] Message(uint opcode) => new Body().U32(opcode).Bytes(ToArray()).ToArray();

    /// <summary>A whole game event message: 0xF7B0, object id, sequence, event type, this body.</summary>
    public byte[] GameEvent(uint eventType, uint objectId = 0x50000001, uint sequence = 7)
        => new Body().U32(0xF7B0).U32(objectId).U32(sequence).U32(eventType).Bytes(ToArray()).ToArray();
}

/// <summary>One fragment for <see cref="Datagram"/>.</summary>
internal readonly record struct Frag(uint Sequence, ushort Count, ushort Index, byte[] Data, uint Id = 0x80000001, ushort Queue = 10, int? SizeOverride = null);

/// <summary>Builds server-to-client datagrams: 20-byte header, optional headers, fragments.</summary>
internal static class Datagram
{
    public const uint BlobFragments = 0x4, AckSequence = 0x4000, TimeSync = 0x01000000, EchoResponse = 0x04000000,
        Flow = 0x08000000, RejectRetransmit = 0x2000, Referral = 0x800, ConnectRequest = 0x40000, Retransmission = 0x1;

    private static uint _packetSeq = 100;

    /// <summary>
    /// Optional headers are written for the flags given (fixed sizes, zero contents; a reject
    /// list gets <paramref name="rejectCount"/> entries). BlobFragments is added when frags are given.
    /// </summary>
    public static byte[] Build(uint flags, int rejectCount = 0, int? sizeOverride = null, params Frag[] frags)
    {
        if (frags.Length > 0) flags |= BlobFragments;
        var after = new Body();
        if ((flags & RejectRetransmit) != 0) { after.U32((uint)rejectCount); for (int i = 0; i < rejectCount; i++) after.U32((uint)(i + 1)); }
        if ((flags & AckSequence) != 0) after.U32(99);
        if ((flags & ConnectRequest) != 0) after.Bytes(new byte[32]);
        if ((flags & TimeSync) != 0) after.Bytes(new byte[8]);
        if ((flags & EchoResponse) != 0) after.Bytes(new byte[8]);
        if ((flags & Flow) != 0) after.Bytes(new byte[6]);
        foreach (Frag f in frags)
        {
            after.U32(f.Sequence).U32(f.Id).U16(f.Count).U16((ushort)(f.SizeOverride ?? (16 + f.Data.Length))).U16(f.Index).U16(f.Queue);
            after.Bytes(f.Data);
        }
        byte[] tail = after.ToArray();
        var d = new Body().U32(_packetSeq++).U32(flags).U32(0).U16(1).U16(0).U16((ushort)(sizeOverride ?? tail.Length)).U16(0);
        return d.Bytes(tail).ToArray();
    }

    public static byte[] Frags(params Frag[] frags) => Build(0, 0, null, frags);

    /// <summary>Splits a message into fragments of at most <paramref name="chunk"/> bytes (448 like the server).</summary>
    public static Frag[] Split(uint sequence, byte[] message, int chunk = 448)
    {
        int count = Math.Max(1, (message.Length + chunk - 1) / chunk);
        var list = new Frag[count];
        for (int i = 0; i < count; i++)
        {
            int off = i * chunk;
            list[i] = new Frag(sequence, (ushort)count, (ushort)i, message.AsSpan(off, Math.Min(chunk, message.Length - off)).ToArray());
        }
        return list;
    }
}
