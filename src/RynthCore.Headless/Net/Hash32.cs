// ============================================================================
//  RynthCore.Headless - Net/Hash32.cs
//  AC's packet checksum primitive: seed = length << 16, plus every little-endian
//  u32 word, plus the 1-3 tail bytes placed high (shifts 24, 16, 8). Wraps mod 2^32.
//  The length is part of the seed, so how data is split into spans matters.
// ============================================================================

using System.Buffers.Binary;

namespace RynthCore.Headless.Net;

internal static class Hash32
{
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint h = (uint)data.Length << 16;
        int words = data.Length & ~3;
        for (int i = 0; i < words; i += 4)
            h += BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i));
        int shift = 24;
        for (int i = words; i < data.Length; i++, shift -= 8)
            h += (uint)data[i] << shift;
        return h;
    }
}
