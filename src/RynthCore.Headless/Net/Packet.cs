// ============================================================================
//  RynthCore.Headless - Net/Packet.cs
//  One AC datagram: the 20-byte header, the optional sections (in ascending flag-bit
//  order) and the blob fragments, plus the checksum that seals them.
//
//  Header: seq u32, flags u32, checksum u32, id u16, time u16, size u16, iteration u16.
//  Checksum = Hash32(header with checksum = 0xBADD70DD)
//           + payload, where payload = Hash32(all optional bytes)
//                                    + sum over fragments of Hash32(frag header) + Hash32(frag data)
//  and, with EncryptedChecksum, payload is XORed with one ISAAC word first.
// ============================================================================

using System.Buffers.Binary;
using RynthCore.Engine.Net;

namespace RynthCore.Headless.Net;

/// <summary>One message fragment as it travels: 16-byte header, then data.</summary>
internal readonly record struct Fragment(uint Sequence, uint Id, ushort Count, ushort Index, ushort Queue, byte[] Data)
{
    public const int HeaderSize = 16;
    public const int MaxSize = 464;
    public const int MaxData = MaxSize - HeaderSize;   // 448
}

internal static class Packet
{
    public const int HeaderSize = 20;
    public const int MaxBody = 464;
    public const uint ChecksumPlaceholder = 0xBADD70DD;

    public static uint HeaderHash(ReadOnlySpan<byte> header20)
    {
        Span<byte> h = stackalloc byte[HeaderSize];
        header20.Slice(0, HeaderSize).CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(8), ChecksumPlaceholder);
        return Hash32.Compute(h);
    }

    /// <summary>Payload hash of a whole datagram: optional sections as one span, then each fragment's header and data.</summary>
    public static bool TryPayloadHash(ReadOnlySpan<byte> datagram, int optionalEnd, out uint hash)
    {
        hash = Hash32.Compute(datagram.Slice(HeaderSize, optionalEnd - HeaderSize));
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(datagram.Slice(4));
        if ((flags & PacketFlags.BlobFragments) == 0)
            return true;
        int end = HeaderSize + BinaryPrimitives.ReadUInt16LittleEndian(datagram.Slice(16));
        int pos = optionalEnd;
        while (pos < end)
        {
            if (pos + Fragment.HeaderSize > end) return false;
            int size = BinaryPrimitives.ReadUInt16LittleEndian(datagram.Slice(pos + 10));
            if (size < Fragment.HeaderSize || pos + size > end) return false;
            hash += Hash32.Compute(datagram.Slice(pos, Fragment.HeaderSize));
            hash += Hash32.Compute(datagram.Slice(pos + Fragment.HeaderSize, size - Fragment.HeaderSize));
            pos += size;
        }
        return true;
    }

    /// <summary>
    /// Builds a datagram. <paramref name="optional"/> is the optional-section bytes, already in
    /// flag order. With a key the checksum is encrypted (and the flag added). Returns the bytes
    /// and the sealed payload (payload hash, XORed with the key if any) for byte-identical resends.
    /// </summary>
    public static byte[] Build(uint seq, uint flags, ushort id, ushort time, ushort iteration,
        ReadOnlySpan<byte> optional, IReadOnlyList<Fragment>? fragments, uint? key, out uint sealedPayload)
    {
        if (fragments is { Count: > 0 }) flags |= PacketFlags.BlobFragments;
        if (key.HasValue) flags |= PacketFlags.EncryptedChecksum;

        int size = optional.Length;
        if (fragments != null)
            foreach (Fragment f in fragments)
                size += Fragment.HeaderSize + f.Data.Length;
        if (size > MaxBody)
            throw new InvalidOperationException($"packet body {size} > {MaxBody}");

        var d = new byte[HeaderSize + size];
        Span<byte> s = d;
        BinaryPrimitives.WriteUInt32LittleEndian(s, seq);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(4), flags);
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(12), id);
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(14), time);
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(16), (ushort)size);
        BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(18), iteration);
        optional.CopyTo(s.Slice(HeaderSize));

        uint payload = Hash32.Compute(optional);
        int pos = HeaderSize + optional.Length;
        if (fragments != null)
        {
            foreach (Fragment f in fragments)
            {
                Span<byte> fh = s.Slice(pos, Fragment.HeaderSize);
                BinaryPrimitives.WriteUInt32LittleEndian(fh, f.Sequence);
                BinaryPrimitives.WriteUInt32LittleEndian(fh.Slice(4), f.Id);
                BinaryPrimitives.WriteUInt16LittleEndian(fh.Slice(8), f.Count);
                BinaryPrimitives.WriteUInt16LittleEndian(fh.Slice(10), (ushort)(Fragment.HeaderSize + f.Data.Length));
                BinaryPrimitives.WriteUInt16LittleEndian(fh.Slice(12), f.Index);
                BinaryPrimitives.WriteUInt16LittleEndian(fh.Slice(14), f.Queue);
                f.Data.CopyTo(s.Slice(pos + Fragment.HeaderSize));
                payload += Hash32.Compute(fh) + Hash32.Compute(f.Data);
                pos += Fragment.HeaderSize + f.Data.Length;
            }
        }

        sealedPayload = key.HasValue ? payload ^ key.Value : payload;
        Seal(d, sealedPayload);
        return d;
    }

    /// <summary>Writes the checksum from the (current) header and an already sealed payload.</summary>
    public static void Seal(byte[] datagram, uint sealedPayload)
        => BinaryPrimitives.WriteUInt32LittleEndian(datagram.AsSpan(8), HeaderHash(datagram) + sealedPayload);
}

/// <summary>A received datagram's header and optional sections.</summary>
internal sealed class InboundPacket
{
    public uint Sequence, Flags, Checksum;
    public ushort Id, Time, Size, Iteration;
    public int OptionalEnd;                 // offset where fragments start
    public uint[] Naks = Array.Empty<uint>();
    public uint[] Rejects = Array.Empty<uint>();
    public uint Ack;
    public double TimeSync;
    public ConnectRequestData? Connect;
    public float EchoClientTime, EchoServerDelta;

    public bool Has(uint flag) => (Flags & flag) != 0;
    public bool Encrypted => Has(PacketFlags.EncryptedChecksum);

    /// <summary>Header and optional sections; false if the sizes don't add up or a section can't be sized.</summary>
    public static bool TryParse(ReadOnlySpan<byte> d, out InboundPacket p)
    {
        p = new InboundPacket();
        if (d.Length < Packet.HeaderSize) return false;
        p.Sequence = BinaryPrimitives.ReadUInt32LittleEndian(d);
        p.Flags = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(4));
        p.Checksum = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(8));
        p.Id = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(12));
        p.Time = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(14));
        p.Size = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(16));
        p.Iteration = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(18));
        int end = Packet.HeaderSize + p.Size;
        if (end > d.Length) return false;
        if ((p.Flags & PacketFlags.Unsized & ~PacketFlags.LoginRequest) != 0) return false;

        int pos = Packet.HeaderSize;
        bool Need(int n, ref int at) => at + n <= end;
        uint U32(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(at));

        if (p.Has(PacketFlags.ServerSwitch)) { if (!Need(8, ref pos)) return false; pos += 8; }
        if (p.Has(PacketFlags.RequestRetransmit) && !ReadList(d, end, ref pos, out p.Naks)) return false;
        if (p.Has(PacketFlags.RejectRetransmit) && !ReadList(d, end, ref pos, out p.Rejects)) return false;
        if (p.Has(PacketFlags.AckSequence)) { if (!Need(4, ref pos)) return false; p.Ack = U32(d, pos); pos += 4; }
        if (p.Has(PacketFlags.LoginRequest)) pos = end;   // the rest of the body (client to server only)
        if (p.Has(PacketFlags.WorldLoginRequest)) { if (!Need(8, ref pos)) return false; pos += 8; }
        if (p.Has(PacketFlags.ConnectRequest))
        {
            if (!Need(32, ref pos)) return false;
            p.Connect = new ConnectRequestData(
                BinaryPrimitives.ReadDoubleLittleEndian(d.Slice(pos)),
                BinaryPrimitives.ReadUInt64LittleEndian(d.Slice(pos + 8)),
                U32(d, pos + 16), U32(d, pos + 20), U32(d, pos + 24));
            pos += 32;
        }
        if (p.Has(PacketFlags.ConnectResponse)) { if (!Need(8, ref pos)) return false; pos += 8; }
        if (p.Has(PacketFlags.NetError)) { if (!Need(8, ref pos)) return false; pos += 8; }
        if (p.Has(PacketFlags.NetErrorDisconnect)) { if (!Need(8, ref pos)) return false; pos += 8; }
        if (p.Has(PacketFlags.CICMDCommand)) { if (!Need(8, ref pos)) return false; pos += 8; }
        if (p.Has(PacketFlags.TimeSync)) { if (!Need(8, ref pos)) return false; p.TimeSync = BinaryPrimitives.ReadDoubleLittleEndian(d.Slice(pos)); pos += 8; }
        if (p.Has(PacketFlags.EchoRequest)) { if (!Need(4, ref pos)) return false; pos += 4; }
        if (p.Has(PacketFlags.EchoResponse))
        {
            if (!Need(8, ref pos)) return false;
            p.EchoClientTime = BinaryPrimitives.ReadSingleLittleEndian(d.Slice(pos));
            p.EchoServerDelta = BinaryPrimitives.ReadSingleLittleEndian(d.Slice(pos + 4));
            pos += 8;
        }
        if (p.Has(PacketFlags.Flow)) { if (!Need(6, ref pos)) return false; pos += 6; }
        p.OptionalEnd = pos;
        return true;
    }

    private static bool ReadList(ReadOnlySpan<byte> d, int end, ref int pos, out uint[] list)
    {
        list = Array.Empty<uint>();
        if (pos + 4 > end) return false;
        uint n = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(pos));
        pos += 4;
        if (n > (uint)((end - pos) / 4)) return false;
        list = new uint[n];
        for (int i = 0; i < n; i++, pos += 4)
            list[i] = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(pos));
        return true;
    }
}

/// <summary>The server's ConnectRequest: its clock, the cookie to echo, our net id and both ISAAC seeds.</summary>
internal sealed record ConnectRequestData(double ServerTime, ulong Cookie, uint ClientId, uint ServerSeed, uint ClientSeed);
