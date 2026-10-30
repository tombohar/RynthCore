// ============================================================================
//  RynthCore.Engine - Net/FragmentReassembler.cs
//  Turns server-to-client datagrams into whole messages (u32 opcode + body).
//
//  Runs on the consumer side of PacketRing (the plugin pump), never in the RecvFrom
//  detour. Read-only: it sees copies of the client's datagrams.
//
//  Bounded:
//    - at most MaxPartials messages in progress; the least recently touched one is
//      dropped when another starts, and any untouched for StaleMs is dropped (either
//      way its sequence counts as done, so its late fragments are ignored);
//    - at most MaxFragments fragments (MaxFragments * FragmentStride bytes) per message;
//      a bigger one is dropped and its sequence remembered as done;
//    - a window of the last DoneWindow completed sequences rejects retransmitted copies.
//  Slot buffers are pinned arrays reused from one message to the next, so a steady
//  stream allocates nothing; a one-fragment message is handed over straight from the
//  datagram without a copy.
//
//  Messages are delivered in the order they complete (a long message completes after
//  shorter ones that started later). The client applies them in its own order.
//
//  Pure code; compiled into the NetMessageTests replay runner.
// ============================================================================

using System;

namespace RynthCore.Engine.Net;

/// <summary>Receives each completed message. <paramref name="message"/> starts with the u32 opcode and is valid only during the call.</summary>
internal unsafe interface IServerMessageSink
{
    void OnServerMessage(uint fragmentSequence, byte* message, int length);
}

internal sealed unsafe class FragmentReassembler
{
    public const int FragmentStride = 512;       // > the largest fragment body a 484-byte datagram can carry
    public const int MaxFragments = 256;         // 256 * 448 = ~112 KB of message
    public const int MaxPartials = 16;
    public const int DoneWindow = 4096;          // power of two
    public const long StaleMs = 30_000;

    private sealed class Slot
    {
        public bool InUse;
        public uint Sequence;
        public int Count;
        public int Received;
        public int Bytes;
        public long TouchedMs;
        public byte[]? Buffer;                   // pinned; Count * FragmentStride
        public readonly ushort[] Lengths = new ushort[MaxFragments];
        public readonly ulong[] Have = new ulong[MaxFragments / 64];
    }

    private readonly Slot[] _slots = new Slot[MaxPartials];
    private readonly ulong[] _done = new ulong[DoneWindow / 64];
    private uint _maxDone;
    private bool _anyDone;

    // ── counters (read by /rc netmsg; written on the consumer thread only) ──
    public long Packets, PacketsNoFragments, PacketsMalformed, PacketsUnknownOptional;
    public long Fragments, Messages, MultiFragmentMessages, DuplicateFragments, DuplicateMessages;
    public long BadFragments, Oversized, EvictedStale, EvictedFull, SessionResets;
    public uint LastUnknownFlags;

    public FragmentReassembler()
    {
        for (int i = 0; i < _slots.Length; i++)
            _slots[i] = new Slot();
    }

    public int PartialCount
    {
        get
        {
            int n = 0;
            foreach (Slot s in _slots) if (s.InUse) n++;
            return n;
        }
    }

    /// <summary>Forget everything in progress and the done window (a new session, or the stream switched on).</summary>
    public void Reset()
    {
        foreach (Slot s in _slots)
            s.InUse = false;
        Array.Clear(_done);
        _maxDone = 0;
        _anyDone = false;
    }

    /// <summary>One received datagram. <paramref name="nowMs"/> is any monotonic millisecond clock.</summary>
    public void ProcessDatagram(byte* data, int length, long nowMs, IServerMessageSink sink)
    {
        Packets++;
        uint flags = AcPacketWire.Flags(data, length);
        if ((flags & PacketFlags.ConnectRequest) != 0)
        {
            // The server's half of a new connection: fragment sequences start over.
            Reset();
            SessionResets++;
        }

        PacketWalk walk = AcPacketWire.LocateFragments(data, length, out int pos, out int end);
        switch (walk)
        {
            case PacketWalk.NoFragments: PacketsNoFragments++; return;
            case PacketWalk.Malformed: PacketsMalformed++; return;
            case PacketWalk.UnknownOptional: PacketsUnknownOptional++; LastUnknownFlags = flags; return;
        }

        while (pos < end)
        {
            byte* f = data + pos;
            int size = AcPacketWire.ReadU16(f + 10);
            Fragments++;
            Fragment(AcPacketWire.ReadU32(f), AcPacketWire.ReadU16(f + 8), AcPacketWire.ReadU16(f + 12),
                     f + AcPacketWire.FragmentHeaderSize, size - AcPacketWire.FragmentHeaderSize, nowMs, sink);
            pos += size;
        }

        EvictStale(nowMs);
    }

    private void Fragment(uint seq, int count, int index, byte* body, int bodyLen, long nowMs, IServerMessageSink sink)
    {
        if (count == 0 || index >= count)
        {
            BadFragments++;
            return;
        }
        if (IsDone(seq))
        {
            DuplicateFragments++;
            if (count == 1) DuplicateMessages++;
            return;
        }

        if (count == 1)
        {
            if (bodyLen < 4)
            {
                BadFragments++;
                MarkDone(seq);
                return;
            }
            MarkDone(seq);
            Messages++;
            sink.OnServerMessage(seq, body, bodyLen);
            return;
        }

        if (count > MaxFragments || bodyLen > FragmentStride)
        {
            Oversized++;
            DropSequence(seq);
            return;
        }

        Slot? slot = FindSlot(seq) ?? StartSlot(seq, count, nowMs);
        if (slot.Count != count)
        {
            BadFragments++;
            return;
        }
        int word = index >> 6;
        ulong bit = 1UL << (index & 63);
        if ((slot.Have[word] & bit) != 0)
        {
            DuplicateFragments++;
            return;
        }

        slot.Have[word] |= bit;
        slot.Lengths[index] = (ushort)bodyLen;
        slot.Received++;
        slot.Bytes += bodyLen;
        slot.TouchedMs = nowMs;
        fixed (byte* dst = slot.Buffer)
            Buffer.MemoryCopy(body, dst + index * FragmentStride, bodyLen, bodyLen);

        if (slot.Received < slot.Count)
            return;

        // Complete: pack the fragments together in place (fragment i moves down, never up).
        fixed (byte* buf = slot.Buffer)
        {
            int at = 0;
            for (int i = 0; i < slot.Count; i++)
            {
                int len = slot.Lengths[i];
                if (at != i * FragmentStride)
                    Buffer.MemoryCopy(buf + i * FragmentStride, buf + at, len, len);
                at += len;
            }
            slot.InUse = false;
            MarkDone(seq);
            if (at < 4)
            {
                BadFragments++;
                return;
            }
            Messages++;
            MultiFragmentMessages++;
            sink.OnServerMessage(seq, buf, at);
        }
    }

    private Slot? FindSlot(uint seq)
    {
        foreach (Slot s in _slots)
            if (s.InUse && s.Sequence == seq)
                return s;
        return null;
    }

    private Slot StartSlot(uint seq, int count, long nowMs)
    {
        Slot? pick = null;
        foreach (Slot s in _slots)
        {
            if (!s.InUse) { pick = s; break; }
        }
        if (pick == null)
        {
            // All busy: drop the one touched longest ago.
            pick = _slots[0];
            foreach (Slot s in _slots)
                if (s.TouchedMs < pick.TouchedMs) pick = s;
            EvictedFull++;
            MarkDone(pick.Sequence);   // its stragglers are ignored, not given a new slot
        }

        int need = count * FragmentStride;
        if (pick.Buffer == null || pick.Buffer.Length < need)
        {
            // Rounded up so a slot settles at the size it needs; at most MaxFragments * 512.
            int cap = 4096;
            while (cap < need) cap <<= 1;
            pick.Buffer = GC.AllocateUninitializedArray<byte>(cap, pinned: true);
        }
        pick.InUse = true;
        pick.Sequence = seq;
        pick.Count = count;
        pick.Received = 0;
        pick.Bytes = 0;
        pick.TouchedMs = nowMs;
        Array.Clear(pick.Have);
        return pick;
    }

    private void DropSequence(uint seq)
    {
        Slot? s = FindSlot(seq);
        if (s != null) s.InUse = false;
        MarkDone(seq);
    }

    private void EvictStale(long nowMs)
    {
        foreach (Slot s in _slots)
        {
            if (s.InUse && nowMs - s.TouchedMs > StaleMs)
            {
                s.InUse = false;
                MarkDone(s.Sequence);   // a straggler can't start it again
                EvictedStale++;
            }
        }
    }

    // ── done window: the last DoneWindow sequences, by bit ──────────────────

    private bool IsDone(uint seq)
    {
        if (!_anyDone) return false;
        if (seq > _maxDone) return false;
        if (_maxDone - seq >= DoneWindow) return true;   // too old to still be in flight
        return (_done[(seq & (DoneWindow - 1)) >> 6] & (1UL << (int)(seq & 63))) != 0;
    }

    private void MarkDone(uint seq)
    {
        if (!_anyDone)
        {
            _anyDone = true;
            _maxDone = seq;
        }
        else if (seq > _maxDone)
        {
            // Clear the bits of the sequences the window slides over.
            uint advance = seq - _maxDone;
            if (advance >= DoneWindow)
                Array.Clear(_done);
            else
                for (uint s = _maxDone + 1; s != seq; s++)
                    _done[(s & (DoneWindow - 1)) >> 6] &= ~(1UL << (int)(s & 63));
            _maxDone = seq;
        }
        else if (_maxDone - seq >= DoneWindow)
        {
            return;
        }
        _done[(seq & (DoneWindow - 1)) >> 6] |= 1UL << (int)(seq & 63);
    }
}
