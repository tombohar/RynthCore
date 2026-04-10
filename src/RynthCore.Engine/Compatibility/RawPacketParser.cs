using System;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// Minimal parser for Asheron's Call S2C UDP packets.
///
/// Packet layout:
///   [Header — 20 bytes]
///     +0  SeqNo      uint32
///     +4  RecID      uint32
///     +8  Checksum   uint32
///     +12 Flags      uint32
///     +16 Time       uint16
///     +18 Size       uint16
///
///   [One or more BlobFragments follow, contiguously]
///   BlobFragHeader (16 bytes):
///     +0  BlobId     uint64
///     +8  NumFrags   uint16
///     +10 FragSize   uint16  ← payload bytes immediately following this header
///     +12 FragIndex  uint16
///     +14 QueueID    uint16  ← THIS IS THE OPCODE
///
///   [FragSize bytes of payload]
///   [Next BlobFragHeader starts immediately after]
///
/// Checksum validation is intentionally skipped — we are read-only observers.
/// </summary>
internal static class RawPacketParser
{
    private const int PacketHeaderSize  = 20;
    private const int BlobHeaderSize    = 16;
    private const int FragSizeOffset    = 10;
    private const int QueueIdOffset     = 14;
    private const int MaxBlobsPerPacket = 64; // guard against malformed packets

    public static unsafe void Parse(byte* data, int length)
    {
        if (length < PacketHeaderSize)
            return;

        int offset = PacketHeaderSize;
        int blobCount = 0;

        while (offset + BlobHeaderSize <= length && blobCount++ < MaxBlobsPerPacket)
        {
            ushort fragSize = (ushort)(data[offset + FragSizeOffset] | (data[offset + FragSizeOffset + 1] << 8));
            ushort queueId  = (ushort)(data[offset + QueueIdOffset]  | (data[offset + QueueIdOffset  + 1] << 8));

            int payloadStart = offset + BlobHeaderSize;
            int payloadEnd   = payloadStart + fragSize;

            // Bounds check before touching any payload bytes
            if (payloadEnd > length || payloadEnd < payloadStart)
                break;

            if (queueId != 0)
                RawOpcodeTracker.Track(queueId, data + payloadStart, fragSize);

            offset = payloadEnd;
        }
    }
}
