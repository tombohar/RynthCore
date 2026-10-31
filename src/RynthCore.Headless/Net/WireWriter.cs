// ============================================================================
//  RynthCore.Headless - Net/WireWriter.cs
//  Growable little-endian writer for client-to-server messages, with AC's string
//  and packing conventions. Reading uses the SDK's NetReader.
// ============================================================================

using System.Buffers.Binary;
using System.Text;

namespace RynthCore.Headless.Net;

internal sealed class WireWriter
{
    private byte[] _buf;
    private int _len;

    public WireWriter(int capacity = 64) => _buf = new byte[capacity];

    public int Length => _len;
    public ReadOnlySpan<byte> Span => _buf.AsSpan(0, _len);
    public byte[] ToArray() => _buf.AsSpan(0, _len).ToArray();

    private Span<byte> Grow(int n)
    {
        if (_len + n > _buf.Length)
            Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + n));
        Span<byte> s = _buf.AsSpan(_len, n);
        _len += n;
        return s;
    }

    public WireWriter U8(byte v) { Grow(1)[0] = v; return this; }
    public WireWriter U16(ushort v) { BinaryPrimitives.WriteUInt16LittleEndian(Grow(2), v); return this; }
    public WireWriter U32(uint v) { BinaryPrimitives.WriteUInt32LittleEndian(Grow(4), v); return this; }
    public WireWriter I32(int v) { BinaryPrimitives.WriteInt32LittleEndian(Grow(4), v); return this; }
    public WireWriter U64(ulong v) { BinaryPrimitives.WriteUInt64LittleEndian(Grow(8), v); return this; }
    public WireWriter F32(float v) { BinaryPrimitives.WriteSingleLittleEndian(Grow(4), v); return this; }
    public WireWriter F64(double v) { BinaryPrimitives.WriteDoubleLittleEndian(Grow(8), v); return this; }
    public WireWriter Bytes(ReadOnlySpan<byte> v) { v.CopyTo(Grow(v.Length)); return this; }

    /// <summary>Zero bytes up to the next multiple of 4 (from the start of this writer).</summary>
    public WireWriter Align4()
    {
        int pad = (4 - (_len & 3)) & 3;
        Grow(pad).Clear();
        return this;
    }

    /// <summary>u16 length, single-byte characters, zero padding so 2 + length is a multiple of 4.</summary>
    public WireWriter String16L(string s)
    {
        byte[] b = Encoding.Latin1.GetBytes(s);
        if (b.Length >= 0xFFFF)
            throw new ArgumentException("String16L too long", nameof(s));
        U16((ushort)b.Length);
        Bytes(b);
        Grow((4 - ((2 + b.Length) & 3)) & 3).Clear();
        return this;
    }

    /// <summary>
    /// u32 byte count, then a length prefix (one byte below 128 characters, two bytes with
    /// the high bit set otherwise), then the characters, then zero padding to 4.
    /// Used for the LoginRequest password.
    /// </summary>
    public WireWriter String32L(string s)
    {
        byte[] b = Encoding.Latin1.GetBytes(s);
        if (b.Length > 0x7FFF)
            throw new ArgumentException("String32L too long", nameof(s));
        int prefix = b.Length < 0x80 ? 1 : 2;
        U32((uint)(prefix + b.Length));
        if (prefix == 1)
            U8((byte)b.Length);
        else
            U8((byte)((b.Length >> 8) | 0x80)).U8((byte)b.Length);
        Bytes(b);
        return Align4();
    }

    /// <summary>AC's packed u32: one u16 below 0x8000, else (high | 0x8000) then low.</summary>
    public WireWriter PackedU32(uint v)
    {
        if (v < 0x8000)
            return U16((ushort)v);
        return U16((ushort)((v >> 16) | 0x8000)).U16((ushort)v);
    }
}
