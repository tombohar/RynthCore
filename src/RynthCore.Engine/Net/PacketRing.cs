// ============================================================================
//  RynthCore.Engine - Net/PacketRing.cs
//  Hand-off of received datagrams from the RecvFrom detour to the plugin pump.
//
//  The producer (the detour) only copies the datagram: no allocation, no lock, no
//  wait. A full ring, or a second producer arriving while one is writing, drops the
//  datagram and counts it; the client's own copy is never touched either way. The
//  consumer (the single TickAll driver) reads records in place and releases them.
//
//  Records are 8-byte aligned: [i32 length][u32 stamp ms][bytes][pad]. A length of -1
//  means "the rest of the buffer is unused, continue at 0". Capacity is a power of two
//  and a multiple of 8, so a wrap marker always fits.
//
//  Pure code (native memory, no engine state); compiled into the NetMessageTests runner.
// ============================================================================

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace RynthCore.Engine.Net;

internal sealed unsafe class PacketRing
{
    private const int RecordHeader = 8;
    private const int WrapMarker = -1;

    private readonly byte* _buf;
    private readonly int _capacity;
    private readonly int _mask;
    // Byte counters that wrap (int, so reads and writes are single instructions on x86);
    // only their difference and their low bits are used.
    private int _head;      // bytes written (producer)
    private int _tail;      // bytes released (consumer)
    private int _writing;   // producer claim

    /// <summary>Largest datagram accepted; anything longer is dropped (AC's are under 500 bytes).</summary>
    public const int MaxDatagram = 2048;

    public long Written;
    public long DroppedFull;
    public long DroppedContended;
    public long DroppedTooLong;

    /// <summary>The ring's memory is never freed: the detour may still be running at teardown.</summary>
    /// <param name="startCounter">Where the wrapping byte counters start (tests start them near int.MaxValue).</param>
    public PacketRing(int capacityPow2, int startCounter = 0)
    {
        _head = _tail = startCounter & ~7;
        if (capacityPow2 < 4096 || (capacityPow2 & (capacityPow2 - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(capacityPow2));
        _capacity = capacityPow2;
        _mask = capacityPow2 - 1;
        _buf = (byte*)NativeMemory.AllocZeroed((nuint)capacityPow2);
    }

    public int Capacity => _capacity;

    /// <summary>Bytes in use (approximate when read from another thread).</summary>
    public int Used => unchecked(Volatile.Read(ref _head) - Volatile.Read(ref _tail));

    /// <summary>Producer: copy one datagram in. Never blocks; false = dropped (counted).</summary>
    public bool TryWrite(byte* data, int length, uint stampMs)
    {
        if (length <= 0 || length > MaxDatagram)
        {
            DroppedTooLong++;
            return false;
        }
        if (Interlocked.CompareExchange(ref _writing, 1, 0) != 0)
        {
            Interlocked.Increment(ref DroppedContended);
            return false;
        }
        try
        {
            int need = RecordHeader + ((length + 7) & ~7);
            int head = _head;
            int used = unchecked(head - Volatile.Read(ref _tail));
            int pos = head & _mask;
            int toEnd = _capacity - pos;
            int skip = need > toEnd ? toEnd : 0;
            if (used + skip + need > _capacity)
            {
                DroppedFull++;
                return false;
            }
            if (skip != 0)
            {
                *(int*)(_buf + pos) = WrapMarker;
                pos = 0;
            }
            *(int*)(_buf + pos) = length;
            *(uint*)(_buf + pos + 4) = stampMs;
            Buffer.MemoryCopy(data, _buf + pos + RecordHeader, length, length);
            Written++;
            Volatile.Write(ref _head, unchecked(head + skip + need));   // publish after the bytes
            return true;
        }
        finally
        {
            Volatile.Write(ref _writing, 0);
        }
    }

    /// <summary>
    /// Consumer: the oldest record, in place. Call <see cref="Release"/> with the returned
    /// token when done with it (the bytes stay valid until then).
    /// </summary>
    public bool TryPeek(out byte* data, out int length, out uint stampMs, out int releaseTo)
    {
        data = null;
        length = 0;
        stampMs = 0;
        releaseTo = 0;
        int tail = _tail;
        int head = Volatile.Read(ref _head);
        if (tail == head)
            return false;
        int pos = tail & _mask;
        int len = *(int*)(_buf + pos);
        if (len == WrapMarker)
        {
            tail = unchecked(tail + _capacity - pos);
            pos = 0;
            len = *(int*)_buf;
        }
        data = _buf + pos + RecordHeader;
        length = len;
        stampMs = *(uint*)(_buf + pos + 4);
        releaseTo = unchecked(tail + RecordHeader + ((len + 7) & ~7));
        return true;
    }

    public void Release(int releaseTo) => Volatile.Write(ref _tail, releaseTo);

    /// <summary>Consumer: drop everything queued (used when the stream is switched on again).</summary>
    public void Clear() => Volatile.Write(ref _tail, Volatile.Read(ref _head));
}
