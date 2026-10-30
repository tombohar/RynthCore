// ============================================================================
//  RynthCore.Engine - Net/AcPacketWire.cs
//  Where the message fragments sit in one server-to-client UDP datagram.
//
//  Written from the AC wire format as the engine already reads it (RawPacketParser's
//  20-byte header + 16-byte fragment headers) and general knowledge of the protocol;
//  no generated definitions. Pure code: no engine state, compiled into the
//  NetMessageTests replay runner as is.
//
//  Datagram:
//    header (20 bytes)
//      +0  u32 sequence
//      +4  u32 flags             (PacketFlags below)
//      +8  u32 checksum          (not checked: we only read)
//      +12 u16 recipient id
//      +14 u16 time
//      +16 u16 size              bytes after the header (optional headers + fragments)
//      +18 u16 iteration
//    optional headers, one per flag bit set, in ascending bit order
//    fragments, back to back, when flags has BlobFragments
//
//  Fragment (16-byte header, then its share of the message):
//      +0  u32 sequence          the message's id: every fragment of one message shares it
//      +4  u32 id
//      +8  u16 count             fragments in the message
//      +10 u16 size              this fragment INCLUDING its 16-byte header
//      +12 u16 index             0..count-1
//      +14 u16 queue             the client's dispatch queue (not the opcode)
//
//  The walk is self-checking: the optional headers and the fragments must end exactly at
//  20 + header.size. A datagram that doesn't is counted as malformed and skipped whole,
//  so a wrong guess about an optional header shows up as a counter, never as garbage.
// ============================================================================

namespace RynthCore.Engine.Net;

/// <summary>Packet header flag bits the reader needs.</summary>
internal static class PacketFlags
{
    public const uint Retransmission     = 0x00000001;
    public const uint EncryptedChecksum  = 0x00000002;
    public const uint BlobFragments      = 0x00000004;
    public const uint ServerSwitch       = 0x00000100;  // 8 bytes
    public const uint LogonServerAddr    = 0x00000200;  // size unknown here: skip the datagram
    public const uint EmptyHeader1       = 0x00000400;  // 0 bytes
    public const uint Referral           = 0x00000800;  // size unknown here: skip the datagram
    public const uint RequestRetransmit  = 0x00001000;  // u32 count + count * u32
    public const uint RejectRetransmit   = 0x00002000;  // u32 count + count * u32
    public const uint AckSequence        = 0x00004000;  // 4 bytes
    public const uint Disconnect         = 0x00008000;  // 0 bytes
    public const uint LoginRequest       = 0x00010000;  // client to server only: skip
    public const uint WorldLoginRequest  = 0x00020000;  // 8 bytes
    public const uint ConnectRequest     = 0x00040000;  // 32 bytes; starts a new session
    public const uint ConnectResponse    = 0x00080000;  // 8 bytes
    public const uint NetError           = 0x00100000;  // 8 bytes
    public const uint NetErrorDisconnect = 0x00200000;  // 8 bytes
    public const uint CICMDCommand       = 0x00400000;  // 8 bytes
    public const uint TimeSync           = 0x01000000;  // 8 bytes
    public const uint EchoRequest        = 0x02000000;  // 4 bytes
    public const uint EchoResponse       = 0x04000000;  // 8 bytes
    public const uint Flow               = 0x08000000;  // 6 bytes

    /// <summary>Flags whose optional header this reader can't size; a fragment datagram carrying one is skipped.</summary>
    public const uint Unsized = LogonServerAddr | Referral | LoginRequest | 0x00800000u | 0xF0000000u | 0x000000F8u;
}

internal enum PacketWalk : byte
{
    /// <summary>Fragments located.</summary>
    Ok,
    /// <summary>No BlobFragments flag: nothing to reassemble (acks, pings, time sync...).</summary>
    NoFragments,
    /// <summary>Shorter than a header, or the sizes don't add up.</summary>
    Malformed,
    /// <summary>Carries an optional header this reader can't size.</summary>
    UnknownOptional,
}

internal static unsafe class AcPacketWire
{
    public const int HeaderSize = 20;
    public const int FragmentHeaderSize = 16;

    public static uint ReadU32(byte* p) => (uint)(p[0] | (p[1] << 8) | (p[2] << 16) | (p[3] << 24));
    public static ushort ReadU16(byte* p) => (ushort)(p[0] | (p[1] << 8));

    /// <summary>The datagram's flags, or 0 when it is shorter than a header.</summary>
    public static uint Flags(byte* data, int length) => length >= HeaderSize ? ReadU32(data + 4) : 0;

    /// <summary>
    /// Finds the fragment area of one datagram: [fragStart, fragEnd). Never reads past
    /// <paramref name="length"/>. Ok only when the optional headers sized and fragEnd is
    /// exactly where the header's size says the datagram ends.
    /// </summary>
    public static PacketWalk LocateFragments(byte* data, int length, out int fragStart, out int fragEnd)
    {
        fragStart = fragEnd = 0;
        if (data == null || length < HeaderSize)
            return PacketWalk.Malformed;

        uint flags = ReadU32(data + 4);
        if ((flags & PacketFlags.BlobFragments) == 0)
            return PacketWalk.NoFragments;
        if ((flags & PacketFlags.Unsized) != 0)
            return PacketWalk.UnknownOptional;

        int end = HeaderSize + ReadU16(data + 16);
        if (end > length)
            return PacketWalk.Malformed;

        int pos = HeaderSize;
        if ((flags & PacketFlags.ServerSwitch) != 0) pos += 8;
        if ((flags & PacketFlags.RequestRetransmit) != 0 && !SkipU32List(data, end, ref pos)) return PacketWalk.Malformed;
        if ((flags & PacketFlags.RejectRetransmit) != 0 && !SkipU32List(data, end, ref pos)) return PacketWalk.Malformed;
        if ((flags & PacketFlags.AckSequence) != 0) pos += 4;
        if ((flags & PacketFlags.WorldLoginRequest) != 0) pos += 8;
        if ((flags & PacketFlags.ConnectRequest) != 0) pos += 32;
        if ((flags & PacketFlags.ConnectResponse) != 0) pos += 8;
        if ((flags & PacketFlags.NetError) != 0) pos += 8;
        if ((flags & PacketFlags.NetErrorDisconnect) != 0) pos += 8;
        if ((flags & PacketFlags.CICMDCommand) != 0) pos += 8;
        if ((flags & PacketFlags.TimeSync) != 0) pos += 8;
        if ((flags & PacketFlags.EchoRequest) != 0) pos += 4;
        if ((flags & PacketFlags.EchoResponse) != 0) pos += 8;
        if ((flags & PacketFlags.Flow) != 0) pos += 6;
        if (pos > end)
            return PacketWalk.Malformed;

        // The fragments must tile [pos, end) exactly.
        int walk = pos;
        int guard = 0;
        while (walk < end)
        {
            if (walk + FragmentHeaderSize > end || ++guard > 64)
                return PacketWalk.Malformed;
            int size = ReadU16(data + walk + 10);
            if (size < FragmentHeaderSize || walk + size > end)
                return PacketWalk.Malformed;
            walk += size;
        }
        if (walk == pos)
            return PacketWalk.Malformed;   // the flag promised fragments

        fragStart = pos;
        fragEnd = end;
        return PacketWalk.Ok;
    }

    private static bool SkipU32List(byte* data, int end, ref int pos)
    {
        if (pos + 4 > end) return false;
        uint n = ReadU32(data + pos);
        if (n > 256) return false;
        pos += 4 + (int)n * 4;
        return pos <= end;
    }
}
