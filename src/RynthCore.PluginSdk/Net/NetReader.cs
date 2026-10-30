// ============================================================================
//  RynthCore.PluginSdk - Net/NetReader.cs
//  Bounds-checked little-endian reader for server message bodies.
//
//  Every read returns false instead of throwing when the data runs out, so a parser
//  given a short or unexpected message fails cleanly. Written for RynthCore (same rules
//  as the engine's PropertyWire reader); also compiled into the engine.
// ============================================================================

using System;
using System.Buffers.Binary;
using System.Text;

namespace RynthCore.PluginSdk.Net;

/// <summary>Reads AC wire types from a span: u16/u32/f32/f64, 16-bit-length strings, packed lists and tables.</summary>
public ref struct NetReader
{
    private readonly ReadOnlySpan<byte> _s;
    private int _pos;

    public NetReader(ReadOnlySpan<byte> data)
    {
        _s = data;
        _pos = 0;
    }

    public int Position => _pos;
    public int Length => _s.Length;
    public int Remaining => _s.Length - _pos;

    public bool Has(int n) => n >= 0 && _pos + n <= _s.Length && _pos + n >= _pos;

    public bool Skip(int n)
    {
        if (!Has(n)) return false;
        _pos += n;
        return true;
    }

    public bool U8(out byte v)
    {
        v = 0;
        if (!Has(1)) return false;
        v = _s[_pos++];
        return true;
    }

    public bool U16(out ushort v)
    {
        v = 0;
        if (!Has(2)) return false;
        v = BinaryPrimitives.ReadUInt16LittleEndian(_s.Slice(_pos));
        _pos += 2;
        return true;
    }

    public bool U32(out uint v)
    {
        v = 0;
        if (!Has(4)) return false;
        v = BinaryPrimitives.ReadUInt32LittleEndian(_s.Slice(_pos));
        _pos += 4;
        return true;
    }

    public bool I32(out int v)
    {
        v = 0;
        if (!Has(4)) return false;
        v = BinaryPrimitives.ReadInt32LittleEndian(_s.Slice(_pos));
        _pos += 4;
        return true;
    }

    public bool U64(out ulong v)
    {
        v = 0;
        if (!Has(8)) return false;
        v = BinaryPrimitives.ReadUInt64LittleEndian(_s.Slice(_pos));
        _pos += 8;
        return true;
    }

    public bool F32(out float v)
    {
        v = 0;
        if (!Has(4)) return false;
        v = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(_s.Slice(_pos)));
        _pos += 4;
        return true;
    }

    public bool F64(out double v)
    {
        v = 0;
        if (!Has(8)) return false;
        v = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(_s.Slice(_pos)));
        _pos += 8;
        return true;
    }

    /// <summary>A u32 used as a flag: any non-zero value is true.</summary>
    public bool Bool32(out bool v)
    {
        v = false;
        if (!U32(out uint raw)) return false;
        v = raw != 0;
        return true;
    }

    /// <summary>
    /// AC's 16-bit-length string: u16 length, that many single-byte characters, then zero
    /// padding so that 2 + length is a multiple of 4. The long form (length 0xFFFF followed
    /// by a u32) isn't used by the messages parsed here and is refused.
    /// </summary>
    public bool String16L(out string v)
    {
        v = string.Empty;
        if (!U16(out ushort len) || len == 0xFFFF || !Has(len)) return false;
        v = len == 0 ? string.Empty : Encoding.Latin1.GetString(_s.Slice(_pos, len));
        _pos += len;
        return Skip((4 - ((2 + len) & 3)) & 3);
    }

    /// <summary>A packed list's header: u32 count, sanity-checked against the bytes left (each item at least <paramref name="minItemSize"/> bytes).</summary>
    public bool ListCount(out int count, int minItemSize)
    {
        count = 0;
        if (!U32(out uint n)) return false;
        if (minItemSize < 1) minItemSize = 1;
        if (n > (uint)(Remaining / minItemSize)) return false;
        count = (int)n;
        return true;
    }

    /// <summary>A packed hash table's header: u16 count, u16 bucket count (ignored), sanity-checked like <see cref="ListCount"/>.</summary>
    public bool TableCount(out int count, int minEntrySize)
    {
        count = 0;
        if (!U16(out ushort n) || !U16(out _)) return false;
        if (minEntrySize < 1) minEntrySize = 1;
        if (n > Remaining / minEntrySize) return false;
        count = n;
        return true;
    }

    /// <summary>A packed list of u32 (u32 count, then the values).</summary>
    public bool U32List(out uint[] values)
    {
        values = Array.Empty<uint>();
        if (!ListCount(out int n, 4)) return false;
        if (n == 0) return true;
        var a = new uint[n];
        for (int i = 0; i < n; i++)
            if (!U32(out a[i])) return false;
        values = a;
        return true;
    }
}
