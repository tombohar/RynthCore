// ============================================================================
//  RynthCore.Engine - Net/FlightRing.cs
//  The flight recorder's ring: a fixed number of fixed-size entries, allocated once.
//
//  Writers (the send/receive detours, the busy-count hooks, engine calls, any thread)
//  claim a slot with one Interlocked.Increment and fill it in place: no allocation, no
//  lock, never waits. A writer takes its slot by swapping the slot's Serial to minus its
//  record number (compare-exchange), fills it, and publishes the record number; a reader
//  copies a slot only when Serial is the number it expects before and after the copy, so a
//  slot being rewritten is skipped (it was about to be overwritten anyway).
//
//  An entry is a message (its first DataCap bytes, decoded only when dumped) or a
//  marker (kind + three numbers, optional short text, optional string-literal name).
//
//  Pure code (no engine state); compiled into tools/FlightRecorderTests.
// ============================================================================

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace RynthCore.Engine.Net;

internal enum FlightDir : byte
{
    None = 0,
    /// <summary>Server to client.</summary>
    In = 1,
    /// <summary>Client to server.</summary>
    Out = 2,
    /// <summary>Engine-side marker.</summary>
    Mark = 3,
}

[Flags]
internal enum FlightFlags : byte
{
    None = 0,
    /// <summary>Outbound action matched an engine direct-action call made just before.</summary>
    Engine = 0x01,
    /// <summary>Only the first DataCap bytes were kept.</summary>
    Truncated = 0x02,
    /// <summary>The message spans several fragments; only the first was seen.</summary>
    MultiFragment = 0x04,
    /// <summary>A retransmitted copy of a message not seen before.</summary>
    Retransmit = 0x08,
    /// <summary>sendto returned an error for the datagram carrying this message.</summary>
    SendFailed = 0x10,
    /// <summary>Outbound action with no engine call to match: the client's own path.</summary>
    Client = 0x20,
}

/// <summary>Marker kinds (FlightEntry.Kind when Dir == Mark).</summary>
internal enum FlightMark : ushort
{
    Note = 1,               // text: a manual or plugin note
    EngineCall = 2,         // Name = call, A/B = args, C = collapsed repeats before it
    BusyIncrement = 3,      // A = real m_cBusy after, B = shadow after
    BusyDecrement = 4,      // A = real m_cBusy after, B = shadow after
    BusyForceClear = 5,     // A = real before, B = shadow before; text = who
    BusyForceClearAsk = 6,  // text = who asked (plugin host API, off-thread latch)
    BusyReconcile = 7,      // A = decrements done, B = real after
    BusyNegativeRepair = 8, // A = value found (as int)
    BusyWatchdog = 9,       // A = real, B = shadow, C = ms stuck; text = which watchdog
    GateCleared = 10,       // A = request type (0 = attacking flag), B = object; text = why
    EngineInit = 11,        // A = engine generation (init count)
    EngineShutdown = 12,
    Summary = 13,           // Data = FlightSummary
    Trigger = 14,           // text = reason (auto dump requested)
    NetControl = 15,        // A = flag bit, B = count, C = first sequence; dir byte in Data[0]
    Dump = 16,              // text = note (manual dump requested)
    PluginReload = 17,      // text = plugin
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FlightEntry
{
    public const int DataCap = 120;

    /// <summary>Stopwatch timestamp.</summary>
    public long Stamp;
    /// <summary>1-based record number once written; minus the number while being written; 0 never written.</summary>
    public int Serial;
    public FlightDir Dir;
    public FlightFlags Flags;
    /// <summary>Marker kind (FlightMark) for markers; 0 for messages.</summary>
    public ushort Kind;
    /// <summary>Bytes kept in Data.</summary>
    public ushort Len;
    /// <summary>Message length as seen (first fragment's payload, or count * size for multi-fragment), clamped.</summary>
    public ushort FullLen;
    /// <summary>Messages: fragment sequence. Markers: first number.</summary>
    public uint A;
    /// <summary>Messages: rate-limited copies dropped before this one. Markers: second number.</summary>
    public uint B;
    /// <summary>Messages: an engine 0xC8's requester (IdRequester), else 0. Markers: third number.</summary>
    public uint C;
    public fixed byte Data[DataCap];
}

/// <summary>A copied, consistent entry plus the marker's name (string literal) if any.</summary>
internal struct FlightRecord
{
    public FlightEntry Entry;
    public string? Name;
}

internal sealed unsafe class FlightRing
{
    private readonly FlightEntry[] _slots;
    private readonly string?[] _names;
    private readonly int _mask;
    private int _next;   // records claimed (Interlocked)

    public FlightRing(int capacityPow2)
    {
        if (capacityPow2 < 16 || (capacityPow2 & (capacityPow2 - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(capacityPow2));
        _slots = new FlightEntry[capacityPow2];
        _names = new string?[capacityPow2];
        _mask = capacityPow2 - 1;
    }

    public int Capacity => _slots.Length;

    /// <summary>Records ever written (2^31 is years of play; dumps use the last Capacity).</summary>
    public int Total => Volatile.Read(ref _next);

    /// <summary>Approximate memory held by the ring, for status lines.</summary>
    public int Bytes => _slots.Length * (sizeof(FlightEntry) + IntPtr.Size);

    /// <summary>Records dropped because their slot was still being written by a writer a whole
    /// lap behind (a thread stalled for Capacity records; practically never).</summary>
    public long Dropped => Interlocked.Read(ref _dropped);
    private long _dropped;

    /// <summary>
    /// Claims the next record number and takes its slot: the slot's Serial goes from its last
    /// published value to -serial with one compare-exchange, so two writers never fill one slot
    /// at once; a slot still owned by a stalled writer is skipped (counted in Dropped).
    /// </summary>
    private bool Own(out int i, out int serial)
    {
        serial = Interlocked.Increment(ref _next);
        i = (serial - 1) & _mask;
        int prev = Volatile.Read(ref _slots[i].Serial);
        if (prev < 0 || Interlocked.CompareExchange(ref _slots[i].Serial, -serial, prev) != prev)
        {
            Interlocked.Increment(ref _dropped);
            return false;
        }
        return true;
    }

    /// <summary>A message: its first DataCap bytes.</summary>
    /// <param name="requester">Messages' C: for an engine 0xC8, who asked (IdRequester); else 0.</param>
    public void WriteMessage(long stamp, FlightDir dir, FlightFlags flags, byte* msg, int len, int fullLen, uint fragSeq, uint suppressed, uint requester = 0)
    {
        if (!Own(out int i, out int serial)) return;
        ref FlightEntry e = ref _slots[i];
        _names[i] = null;
        int keep = len < 0 ? 0 : Math.Min(len, FlightEntry.DataCap);
        e.Stamp = stamp;
        e.Dir = dir;
        e.Flags = keep < len ? flags | FlightFlags.Truncated : flags;
        e.Kind = 0;
        e.Len = (ushort)keep;
        e.FullLen = (ushort)Math.Clamp(fullLen, 0, ushort.MaxValue);
        e.A = fragSeq;
        e.B = suppressed;
        e.C = requester;
        fixed (byte* d = e.Data)
        {
            if (keep > 0) Buffer.MemoryCopy(msg, d, FlightEntry.DataCap, keep);
        }
        Volatile.Write(ref e.Serial, serial);
    }

    /// <summary>A marker. <paramref name="name"/> must be a string literal (or otherwise long-lived);
    /// <paramref name="text"/> is copied (Latin-1, truncated to DataCap).</summary>
    public void WriteMark(long stamp, FlightMark kind, string? name, uint a, uint b, uint c, ReadOnlySpan<char> text)
    {
        if (!Own(out int i, out int serial)) return;
        ref FlightEntry e = ref _slots[i];
        _names[i] = name;
        int keep = Math.Min(text.Length, FlightEntry.DataCap);
        e.Stamp = stamp;
        e.Dir = FlightDir.Mark;
        e.Flags = keep < text.Length ? FlightFlags.Truncated : FlightFlags.None;
        e.Kind = (ushort)kind;
        e.Len = (ushort)keep;
        e.FullLen = (ushort)Math.Min(text.Length, ushort.MaxValue);
        e.A = a;
        e.B = b;
        e.C = c;
        fixed (byte* d = e.Data)
        {
            for (int k = 0; k < keep; k++)
            {
                char ch = text[k];
                d[k] = ch < 256 ? (byte)ch : (byte)'?';
            }
        }
        Volatile.Write(ref e.Serial, serial);
    }

    /// <summary>A marker carrying raw bytes (the per-second summary).</summary>
    public void WriteMarkBytes(long stamp, FlightMark kind, uint a, uint b, uint c, ReadOnlySpan<byte> bytes)
    {
        if (!Own(out int i, out int serial)) return;
        ref FlightEntry e = ref _slots[i];
        _names[i] = null;
        int keep = Math.Min(bytes.Length, FlightEntry.DataCap);
        e.Stamp = stamp;
        e.Dir = FlightDir.Mark;
        e.Flags = FlightFlags.None;
        e.Kind = (ushort)kind;
        e.Len = (ushort)keep;
        e.FullLen = (ushort)keep;
        e.A = a;
        e.B = b;
        e.C = c;
        fixed (byte* d = e.Data)
        {
            for (int k = 0; k < keep; k++) d[k] = bytes[k];
        }
        Volatile.Write(ref e.Serial, serial);
    }

    /// <summary>
    /// Copies every complete entry, oldest first. Entries being written while this runs are
    /// skipped. Allocates (dump time only).
    /// </summary>
    public FlightRecord[] Snapshot()
    {
        int total = Volatile.Read(ref _next);
        int count = Math.Min(total, _slots.Length);
        var list = new FlightRecord[count];
        int n = 0;
        for (int s = total - count + 1; s <= total; s++)
        {
            int i = (s - 1) & _mask;
            int before = Volatile.Read(ref _slots[i].Serial);
            if (before != s)
                continue;
            FlightEntry copy = _slots[i];
            string? name = _names[i];
            Interlocked.MemoryBarrier();
            if (Volatile.Read(ref _slots[i].Serial) != s)
                continue;
            list[n].Entry = copy;
            list[n].Name = name;
            n++;
        }
        if (n != list.Length)
            Array.Resize(ref list, n);
        return list;
    }
}
