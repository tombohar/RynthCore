using System.Buffers.Binary;
using System.Diagnostics;
using RynthCore.Engine.Net;
using RynthCore.PluginSdk.Net;

namespace NetMessageTests;

/// <summary>Collects what the reassembler hands over (copied: the pointer is only valid during the call).</summary>
internal sealed unsafe class Collect : IServerMessageSink
{
    public readonly List<(uint Seq, byte[] Bytes)> Messages = new();
    public void OnServerMessage(uint fragmentSequence, byte* message, int length)
        => Messages.Add((fragmentSequence, new ReadOnlySpan<byte>(message, length).ToArray()));
}

/// <summary>Counts only (the perf runs: no copies).</summary>
internal sealed unsafe class CountOnly : IServerMessageSink
{
    public long Count, Bytes;
    public uint OpcodeXor;
    public void OnServerMessage(uint fragmentSequence, byte* message, int length)
    {
        Count++;
        Bytes += length;
        OpcodeXor ^= *(uint*)message;
    }
}

internal static unsafe partial class Program
{
    private static int _checks, _failed;
    private static string _case = "";
    private static long _now = 1_000_000;

    private static int Main(string[] args)
    {
        // Datagram walk
        WalkPlain();
        WalkOptionalHeaders();
        WalkBadDatagrams();
        GoldenHexDatagram();

        // Reassembly
        SingleFragment();
        MultiFragmentInOrder();
        MultiFragmentOutOfOrder();
        DuplicateFragmentsAndRetransmits();
        MissingFragmentGoesStale();
        Oversized();
        InconsistentAndBadFragments();
        SlotExhaustion();
        SeveralMessagesPerDatagram();
        SessionReset();
        DoneWindowSlides();

        // Ring, interest
        RingBasics();
        RingWrapAndFull();
        RingCounterWrap();
        RingThreaded();
        Interest();

        // Parsers
        Strings();
        ChatParsers();
        FellowshipParsers();
        FriendsAndAllegiance();
        SystemTextAndConfirmation();
        TruncationNeverThrows();
        EndToEndGameEvents();
        InventoryOrderTests();
        VaultWireTests();

        // Captures given on the command line
        foreach (string path in args)
            Replay(path);

        Console.WriteLine();
        Console.WriteLine($"{_checks - _failed}/{_checks} checks passed.");
        Console.WriteLine(_failed == 0 ? "ALL NET MESSAGE TESTS PASSED." : $"{_failed} FAILED.");

        Performance();
        return _failed == 0 ? 0 : 1;
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static void Feed(FragmentReassembler r, byte[] datagram, IServerMessageSink sink)
    {
        fixed (byte* p = datagram)
            r.ProcessDatagram(p, datagram.Length, _now, sink);
    }

    private static PacketWalk Walk(byte[] d, out int start, out int end)
    {
        fixed (byte* p = d)
            return AcPacketWire.LocateFragments(p, d.Length, out start, out end);
    }

    private static byte[] Msg(int length, uint opcode = 0xF745, byte seed = 1)
    {
        var b = new byte[length];
        if (length >= 4) BinaryPrimitives.WriteUInt32LittleEndian(b, opcode);
        for (int i = 4; i < length; i++) b[i] = (byte)(seed + i * 7);
        return b;
    }

    private static void Case(string name)
    {
        _case = name;
        Console.WriteLine($"- {name}");
    }

    private static void Eq<T>(T actual, T expected, string what) =>
        Check(EqualityComparer<T>.Default.Equals(actual, expected), $"{what}: expected {expected}, got {actual}");

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (ok) return;
        _failed++;
        Console.WriteLine($"  FAIL [{_case}] {what}");
    }

    // ── datagram walk ───────────────────────────────────────────────────────

    private static void WalkPlain()
    {
        Case("walk: one fragment, no optional headers");
        byte[] d = Datagram.Frags(new Frag(1, 1, 0, Msg(40)));
        Eq(Walk(d, out int s, out int e), PacketWalk.Ok, "status");
        Eq(s, 20, "fragments start after the 20-byte header");
        Eq(e, d.Length, "fragments end at the datagram end");

        Case("walk: no BlobFragments flag (ack only)");
        byte[] ack = Datagram.Build(Datagram.AckSequence);
        Eq(Walk(ack, out _, out _), PacketWalk.NoFragments, "status");
    }

    private static void WalkOptionalHeaders()
    {
        Case("walk: ack + time sync + echo response + flow before the fragments");
        uint flags = Datagram.AckSequence | Datagram.TimeSync | Datagram.EchoResponse | Datagram.Flow;
        byte[] d = Datagram.Build(flags, 0, null, new Frag(1, 1, 0, Msg(30)));
        Eq(Walk(d, out int s, out _), PacketWalk.Ok, "status");
        Eq(s, 20 + 4 + 8 + 8 + 6, "fragment start skips 26 bytes of optional headers");

        Case("walk: reject-retransmit list (u32 count + ids)");
        byte[] r = Datagram.Build(Datagram.RejectRetransmit | Datagram.AckSequence, 3, null, new Frag(1, 1, 0, Msg(12)));
        Eq(Walk(r, out s, out _), PacketWalk.Ok, "status");
        Eq(s, 20 + 4 + 12 + 4, "start after the list and the ack");

        Case("walk: a header this reader can't size (referral) is skipped, not guessed");
        byte[] referral = Datagram.Build(Datagram.Referral, 0, null, new Frag(1, 1, 0, Msg(12)));
        Eq(Walk(referral, out _, out _), PacketWalk.UnknownOptional, "status");
    }

    private static void WalkBadDatagrams()
    {
        Case("walk: malformed datagrams are rejected whole");
        Eq(Walk(new byte[10], out _, out _), PacketWalk.Malformed, "shorter than a header");
        byte[] good = Datagram.Frags(new Frag(1, 1, 0, Msg(40)));
        Eq(Walk(good[..^1], out _, out _), PacketWalk.Malformed, "header size runs past the bytes received");
        byte[] longer = Datagram.Build(0, 0, 40 + 16 + 4, new Frag(1, 1, 0, Msg(40)));
        Eq(Walk(longer, out _, out _), PacketWalk.Malformed, "header size larger than the datagram");
        byte[] fragTooBig = Datagram.Frags(new Frag(1, 1, 0, Msg(40), SizeOverride: 16 + 41));
        Eq(Walk(fragTooBig, out _, out _), PacketWalk.Malformed, "fragment size past the end");
        byte[] fragTooSmall = Datagram.Frags(new Frag(1, 1, 0, Msg(40), SizeOverride: 15));
        Eq(Walk(fragTooSmall, out _, out _), PacketWalk.Malformed, "fragment size under its header");
        byte[] gap = Datagram.Build(0, 0, 40 + 16 + 3, new Frag(1, 1, 0, Msg(40)));
        Array.Resize(ref gap, gap.Length + 3);
        Eq(Walk(gap, out _, out _), PacketWalk.Malformed, "fragments don't tile the declared size");
        byte[] none = Datagram.Build(Datagram.BlobFragments);
        Eq(Walk(none, out _, out _), PacketWalk.Malformed, "fragment flag with no fragment");

        var r = new FragmentReassembler();
        var c = new Collect();
        Feed(r, good[..^1], c);
        Feed(r, longer, c);
        Eq(c.Messages.Count, 0, "nothing delivered from malformed datagrams");
        Eq(r.PacketsMalformed, 2L, "malformed counter");
    }

    /// <summary>
    /// One datagram written out byte by byte: packet seq 5, flags 0x4004 (ack + fragments),
    /// size 44, ack 4; fragment seq 0x10, id 0x80000001, count 1, size 40, index 0, queue 10;
    /// message HearEmote 0x01E0 from 0x50000002 "Bob": "waves."
    /// </summary>
    private static void GoldenHexDatagram()
    {
        Case("golden datagram: HearEmote, hand-written bytes");
        byte[] d = Convert.FromHexString(
            "05000000" + "04400000" + "00000000" + "0100" + "0000" + "2C00" + "0000" +   // header
            "04000000" +                                                                    // ack
            "10000000" + "01000080" + "0100" + "2800" + "0000" + "0A00" +                   // fragment header
            "E0010000" + "02000050" + "0300" + "426F62" + "000000" + "0600" + "77617665732E"); // message
        Eq(d.Length, 64, "64 bytes");
        var r = new FragmentReassembler();
        var c = new Collect();
        Feed(r, d, c);
        Eq(c.Messages.Count, 1, "one message");
        if (c.Messages.Count != 1) return;
        Eq(c.Messages[0].Seq, 0x10u, "fragment sequence");
        Check(ServerMessage.TryFromWire(c.Messages[0].Bytes, out ServerMessage m), "wraps");
        Eq(m.Opcode, ServerOpcode.HearEmote, "opcode");
        NetMessageInfo info = NetMessages.Inspect(m);
        Check(info.Parsed && info.Leftover == 0, $"parsed with nothing left ({info.Kind} parsed={info.Parsed} leftover={info.Leftover})");
        var e = info.Value as HearEmote;
        Eq(e?.SenderId, 0x50000002u, "sender");
        Eq(e?.SenderName, "Bob", "name");
        Eq(e?.Text, "waves.", "text");
    }

    // ── reassembly ──────────────────────────────────────────────────────────

    private static void SingleFragment()
    {
        Case("one-fragment message is delivered as is");
        var r = new FragmentReassembler();
        var c = new Collect();
        byte[] m = Msg(100, 0xF7B0);
        Feed(r, Datagram.Frags(new Frag(5, 1, 0, m)), c);
        Eq(c.Messages.Count, 1, "delivered");
        Check(c.Messages.Count == 1 && c.Messages[0].Bytes.AsSpan().SequenceEqual(m), "bytes intact");
        Eq(r.MultiFragmentMessages, 0L, "not counted as multi-fragment");
    }

    private static void MultiFragmentInOrder()
    {
        Case("multi-fragment message, fragments in order, one per datagram");
        var r = new FragmentReassembler();
        var c = new Collect();
        byte[] m = Msg(448 * 3 + 17, 0xF7B0);
        foreach (Frag f in Datagram.Split(9, m))
        {
            Eq(c.Messages.Count, 0, $"nothing before the last fragment (index {f.Index})");
            Feed(r, Datagram.Frags(f), c);
        }
        Eq(c.Messages.Count, 1, "delivered once complete");
        Check(c.Messages.Count == 1 && c.Messages[0].Bytes.AsSpan().SequenceEqual(m), "bytes reassembled exactly");
        Eq(r.PartialCount, 0, "slot freed");
    }

    private static void MultiFragmentOutOfOrder()
    {
        Case("multi-fragment message, fragments reversed and shuffled");
        var rng = new Random(1234);
        for (int trial = 0; trial < 50; trial++)
        {
            var r = new FragmentReassembler();
            var c = new Collect();
            byte[] m = Msg(rng.Next(449, 448 * 20), 0xF7B0, (byte)trial);
            Frag[] frags = Datagram.Split((uint)(100 + trial), m);
            if (trial == 0) Array.Reverse(frags); else rng.Shuffle(frags);
            foreach (Frag f in frags) Feed(r, Datagram.Frags(f), c);
            if (c.Messages.Count != 1 || !c.Messages[0].Bytes.AsSpan().SequenceEqual(m))
            {
                Check(false, $"trial {trial}: {frags.Length} fragments not reassembled exactly ({c.Messages.Count} delivered)");
                return;
            }
        }
        Check(true, "50 random orders reassembled exactly");

        Case("uneven fragment sizes still pack together");
        var r2 = new FragmentReassembler();
        var c2 = new Collect();
        byte[] whole = Msg(300, 0xF7B0);
        Feed(r2, Datagram.Frags(new Frag(77, 3, 2, whole[250..])), c2);
        Feed(r2, Datagram.Frags(new Frag(77, 3, 0, whole[..100])), c2);
        Feed(r2, Datagram.Frags(new Frag(77, 3, 1, whole[100..250])), c2);
        Check(c2.Messages.Count == 1 && c2.Messages[0].Bytes.AsSpan().SequenceEqual(whole), "100 + 150 + 50 bytes");
    }

    private static void DuplicateFragmentsAndRetransmits()
    {
        Case("duplicate fragments and retransmitted datagrams");
        var r = new FragmentReassembler();
        var c = new Collect();
        byte[] m = Msg(1000, 0xF7B0);
        Frag[] f = Datagram.Split(20, m);
        Feed(r, Datagram.Frags(f[0]), c);
        Feed(r, Datagram.Frags(f[0]), c);                                  // duplicate while partial
        Feed(r, Datagram.Build(Datagram.Retransmission, 0, null, f[1]), c);
        Feed(r, Datagram.Frags(f[2]), c);
        Eq(c.Messages.Count, 1, "delivered once");
        Eq(r.DuplicateFragments, 1L, "one duplicate while partial");
        foreach (Frag x in f) Feed(r, Datagram.Build(Datagram.Retransmission, 0, null, x), c);   // whole message again
        Eq(c.Messages.Count, 1, "a retransmitted complete message isn't delivered twice");
        Feed(r, Datagram.Frags(new Frag(21, 1, 0, Msg(20))), c);
        Feed(r, Datagram.Frags(new Frag(21, 1, 0, Msg(20))), c);
        Eq(c.Messages.Count, 2, "one-fragment message delivered once");
        Eq(r.DuplicateMessages, 1L, "duplicate one-fragment message counted");
        Check(c.Messages.Count == 2 && c.Messages[0].Bytes.AsSpan().SequenceEqual(m), "bytes intact");
    }

    private static void MissingFragmentGoesStale()
    {
        Case("a message missing a fragment is dropped after StaleMs, then a late fragment can't revive it");
        var r = new FragmentReassembler();
        var c = new Collect();
        Frag[] f = Datagram.Split(30, Msg(1500, 0xF7B0));
        Feed(r, Datagram.Frags(f[0]), c);
        Feed(r, Datagram.Frags(f[1]), c);
        Eq(r.PartialCount, 1, "one partial");
        _now += FragmentReassembler.StaleMs + 1;
        Feed(r, Datagram.Frags(new Frag(31, 1, 0, Msg(10))), c);         // any traffic runs the eviction
        Eq(r.PartialCount, 0, "evicted");
        Eq(r.EvictedStale, 1L, "stale counter");
        Feed(r, Datagram.Frags(f[2]), c);
        Feed(r, Datagram.Frags(f[3]), c);
        Eq(c.Messages.Count(m => m.Seq == 30), 0, "never delivered incomplete");
        Eq(r.PartialCount, 0, "late fragments don't start a new partial");
        Eq(r.DuplicateFragments, 2L, "they count as fragments of a finished sequence");
    }

    private static void Oversized()
    {
        Case("oversized messages are dropped and remembered");
        var r = new FragmentReassembler();
        var c = new Collect();
        Feed(r, Datagram.Frags(new Frag(40, FragmentReassembler.MaxFragments + 1, 0, Msg(448))), c);
        Eq(r.Oversized, 1L, "too many fragments");
        Eq(r.PartialCount, 0, "no slot taken");
        Feed(r, Datagram.Frags(new Frag(40, FragmentReassembler.MaxFragments + 1, 1, Msg(448))), c);
        Eq(r.DuplicateFragments, 1L, "its other fragments are ignored as done");

        byte[] big = Msg(FragmentReassembler.FragmentStride + 1, 0xF7B0);
        Feed(r, Datagram.Frags(new Frag(41, 2, 0, Msg(448))), c);
        Feed(r, Datagram.Frags(new Frag(41, 2, 1, big)), c);
        Eq(r.Oversized, 2L, "fragment body over the stride");
        Eq(r.PartialCount, 0, "its partial dropped");
        Eq(c.Messages.Count, 0, "nothing delivered");

        Case("largest allowed message still reassembles");
        byte[] max = Msg(448 * FragmentReassembler.MaxFragments, 0xF7B0);
        foreach (Frag f in Datagram.Split(42, max)) Feed(r, Datagram.Frags(f), c);
        Check(c.Messages.Count == 1 && c.Messages[0].Bytes.AsSpan().SequenceEqual(max), $"{max.Length} bytes in {FragmentReassembler.MaxFragments} fragments");
    }

    private static void InconsistentAndBadFragments()
    {
        Case("bad fragments: index >= count, count 0, count changes mid-message, body under 4 bytes");
        var r = new FragmentReassembler();
        var c = new Collect();
        Feed(r, Datagram.Frags(new Frag(50, 2, 2, Msg(10))), c);
        Feed(r, Datagram.Frags(new Frag(51, 0, 0, Msg(10))), c);
        Feed(r, Datagram.Frags(new Frag(52, 3, 0, Msg(448))), c);
        Feed(r, Datagram.Frags(new Frag(52, 4, 1, Msg(448))), c);
        Feed(r, Datagram.Frags(new Frag(53, 1, 0, new byte[] { 1, 2, 3 })), c);
        Eq(r.BadFragments, 4L, "four bad fragments counted");
        Eq(c.Messages.Count, 0, "nothing delivered");
    }

    private static void SlotExhaustion()
    {
        Case("more partial messages than slots: the least recently touched is dropped");
        var r = new FragmentReassembler();
        var c = new Collect();
        for (uint s = 0; s <= FragmentReassembler.MaxPartials; s++)
        {
            _now += 1;
            Feed(r, Datagram.Frags(new Frag(60 + s, 2, 0, Msg(448, 0xF7B0, (byte)s))), c);
        }
        Eq(r.PartialCount, FragmentReassembler.MaxPartials, "slots full");
        Eq(r.EvictedFull, 1L, "one evicted");
        Feed(r, Datagram.Frags(new Frag(60, 2, 1, Msg(100))), c);
        Eq(c.Messages.Count(m => m.Seq == 60), 0, "the evicted (oldest) message doesn't complete");
        Eq(r.EvictedFull, 1L, "and its straggler evicts nothing else");
        Feed(r, Datagram.Frags(new Frag(61, 2, 1, Msg(100))), c);
        Eq(c.Messages.Count(m => m.Seq == 61), 1, "the next one does");
    }

    private static void SeveralMessagesPerDatagram()
    {
        Case("several fragments of different messages in one datagram, interleaved");
        var r = new FragmentReassembler();
        var c = new Collect();
        byte[] a = Msg(700, 0xF7B0, 3), b = Msg(30, 0x02BB, 4), d = Msg(900, 0xF745, 5);
        Frag[] fa = Datagram.Split(70, a, 400), fd = Datagram.Split(72, d, 400);
        Feed(r, Datagram.Frags(fa[0], new Frag(71, 1, 0, b), fd[2]), c);
        Feed(r, Datagram.Frags(fd[0], fa[1]), c);
        Feed(r, Datagram.Frags(fd[1]), c);
        Eq(c.Messages.Count, 3, "three messages");
        Eq(string.Join(",", c.Messages.Select(m => m.Seq)), "71,70,72", "completion order");
        Check(c.Messages.Count == 3 && c.Messages[1].Bytes.AsSpan().SequenceEqual(a) && c.Messages[2].Bytes.AsSpan().SequenceEqual(d), "bytes intact");
    }

    private static void SessionReset()
    {
        Case("a ConnectRequest (new session) restarts the sequence window");
        var r = new FragmentReassembler();
        var c = new Collect();
        for (uint s = 1; s <= 50; s++) Feed(r, Datagram.Frags(new Frag(s, 1, 0, Msg(8))), c);
        Feed(r, Datagram.Frags(new Frag(2, 1, 0, Msg(8))), c);
        Eq(c.Messages.Count, 50, "an old sequence is a duplicate within a session");
        Feed(r, Datagram.Build(Datagram.ConnectRequest), c);
        Eq(r.SessionResets, 1L, "reset seen");
        Feed(r, Datagram.Frags(new Frag(2, 1, 0, Msg(8))), c);
        Eq(c.Messages.Count, 51, "the same sequence is new after the reset");
    }

    private static void DoneWindowSlides()
    {
        Case("the done window slides: a long-finished sequence is still rejected, recent ones tracked by bit");
        var r = new FragmentReassembler();
        var c = new Collect();
        Feed(r, Datagram.Frags(new Frag(10, 1, 0, Msg(8))), c);
        Feed(r, Datagram.Frags(new Frag(10 + FragmentReassembler.DoneWindow + 5, 1, 0, Msg(8))), c);
        Feed(r, Datagram.Frags(new Frag(10, 1, 0, Msg(8))), c);
        Eq(c.Messages.Count, 2, "far-behind sequence rejected");
        Feed(r, Datagram.Frags(new Frag(20 + FragmentReassembler.DoneWindow, 1, 0, Msg(8))), c);
        Eq(c.Messages.Count, 3, "a sequence inside the window that never completed is accepted");
        // A partial that started before many later one-fragment messages still completes.
        Frag[] f = Datagram.Split(30000, Msg(900, 0xF7B0));
        Feed(r, Datagram.Frags(f[0]), c);
        for (uint s = 30001; s < 30001 + 1000; s++) Feed(r, Datagram.Frags(new Frag(s, 1, 0, Msg(8))), c);
        Feed(r, Datagram.Frags(f[1]), c);
        Feed(r, Datagram.Frags(f[2]), c);
        Eq(c.Messages.Count(m => m.Seq == 30000), 1, "slow multi-fragment message completes behind 1000 newer ones");
    }

    // ── ring ────────────────────────────────────────────────────────────────

    private static bool Read(PacketRing ring, out byte[] data, out uint stamp)
    {
        data = Array.Empty<byte>();
        if (!ring.TryPeek(out byte* p, out int len, out stamp, out int rel)) return false;
        data = new ReadOnlySpan<byte>(p, len).ToArray();
        ring.Release(rel);
        return true;
    }

    private static bool Write(PacketRing ring, byte[] d, uint stamp = 0)
    {
        fixed (byte* p = d) return ring.TryWrite(p, d.Length, stamp);
    }

    private static void RingBasics()
    {
        Case("ring: FIFO, stamps, too-long rejected");
        var ring = new PacketRing(4096);
        Check(Write(ring, Msg(100, 1), 11) && Write(ring, Msg(3, 2), 22), "two writes");
        Check(Read(ring, out byte[] a, out uint sa) && a.Length == 100 && sa == 11, "first back");
        Check(Read(ring, out byte[] b, out uint sb) && b.Length == 3 && sb == 22, "second back");
        Check(!Read(ring, out _, out _), "empty");
        Check(!Write(ring, new byte[PacketRing.MaxDatagram + 1]), "too long refused");
        Eq(ring.DroppedTooLong, 1L, "counted");
    }

    private static void RingWrapAndFull()
    {
        Case("ring: wraps at the end, drops (never waits) when full");
        var ring = new PacketRing(4096);
        int wrote = 0, read = 0;
        var rng = new Random(7);
        var pending = new Queue<byte[]>();
        for (int i = 0; i < 5000; i++)
        {
            byte[] d = Msg(rng.Next(20, 500), (uint)i, (byte)i);
            if (Write(ring, d)) { pending.Enqueue(d); wrote++; }
            if (rng.Next(3) == 0)
                while (Read(ring, out byte[] got, out _))
                {
                    read++;
                    if (!got.AsSpan().SequenceEqual(pending.Dequeue())) { Check(false, $"datagram {read} corrupted across a wrap"); return; }
                }
        }
        while (Read(ring, out byte[] got, out _)) { read++; if (!got.AsSpan().SequenceEqual(pending.Dequeue())) { Check(false, "tail corrupted"); return; } }
        Eq(read, wrote, "everything written was read back intact, in order");
        Check(ring.DroppedFull > 0, $"some writes were refused while full ({ring.DroppedFull})");
        Eq(ring.Written, (long)wrote, "written counter");
    }

    private static void RingCounterWrap()
    {
        Case("ring: the byte counters wrap past int.MaxValue");
        var ring = new PacketRing(4096, int.MaxValue - 3000);
        int ok = 0;
        for (int i = 0; i < 200; i++)
        {
            byte[] d = Msg(100 + i, (uint)i, (byte)i);
            if (!Write(ring, d) || !Read(ring, out byte[] got, out _) || !got.AsSpan().SequenceEqual(d)) break;
            ok++;
        }
        Eq(ok, 200, "200 round trips across the wrap");
        Eq(ring.Used, 0, "empty after");
    }

    private static void RingThreaded()
    {
        Case("ring: one producer thread, one consumer thread, 200k datagrams");
        var ring = new PacketRing(64 * 1024);
        const int N = 200_000;
        int produced = 0;
        var t = new Thread(() =>
        {
            byte[] d = new byte[400];
            for (int i = 0; i < N; i++)
            {
                int len = 20 + (i % 380);
                BinaryPrimitives.WriteInt32LittleEndian(d, i);
                d[len - 1] = (byte)i;
                while (!Write(ring, d[..len], (uint)i)) Thread.SpinWait(20);   // the test retries; the hook drops
                produced++;
            }
        });
        t.Start();
        int expect = 0;
        bool ok = true;
        var sw = Stopwatch.StartNew();
        while (expect < N && sw.Elapsed.TotalSeconds < 30)
        {
            if (!Read(ring, out byte[] got, out uint stamp)) { Thread.SpinWait(10); continue; }
            int len = 20 + (expect % 380);
            if (got.Length != len || BinaryPrimitives.ReadInt32LittleEndian(got) != expect || got[len - 1] != (byte)expect || stamp != (uint)expect)
            {
                ok = false;
                break;
            }
            expect++;
        }
        t.Join();
        Check(ok && expect == N, $"all {N} datagrams in order and intact (got {expect})");
    }

    private static void Interest()
    {
        Case("interest sets");
        var s = new ServerMessageInterest();
        Check(s.IsEmpty, "starts empty");
        uint[] ops = { 0x02BB, 0x01E0 };
        uint[] evs = { 0x02BE, 0x0021 };
        fixed (uint* o = ops) fixed (uint* e = evs) Check(s.Set(o, 2, e, 2), "set");
        Check(s.Wants(0x02BB, 0) && s.Wants(0x01E0, 0), "listed opcodes");
        Check(!s.Wants(0xF745, 0), "unlisted opcode");
        Check(s.Wants(0xF7B0, 0x02BE) && s.Wants(0xF7B0, 0x0021), "listed game events");
        Check(!s.Wants(0xF7B0, 0x02C2), "unlisted game event");
        Check(!s.Wants(0x1_0000, 0), "out-of-range opcode never wanted");

        uint[] allEv = { 0xF7B0 };
        fixed (uint* o = allEv) Check(s.Set(o, 1, null, 0), "0xF7B0 alone");
        Check(s.Wants(0xF7B0, 0x1234) && !s.Wants(0x02BB, 0), "every game event, nothing else");

        uint[] all = { 0xFFFFFFFF };
        fixed (uint* o = all) Check(s.Set(o, 1, null, 0), "all");
        Check(s.Wants(0xF745, 0) && s.Wants(0xF7B0, 5), "everything");

        uint[] bad = { 0x02BB, 0x10000 };
        fixed (uint* o = bad) Check(!s.Set(o, 2, null, 0), "an out-of-range opcode refuses the whole set");
        Check(s.IsEmpty, "and leaves it empty");
        Check(!s.Set(null, 3, null, 0), "null with a count refused");
        Check(s.Set(null, 0, null, 0) && s.IsEmpty, "empty set = off");
    }

    // ── parsers ─────────────────────────────────────────────────────────────

    private static void Strings()
    {
        Case("String16L: lengths 0-6 pad to 4, Latin-1, long form refused");
        for (int len = 0; len <= 6; len++)
        {
            string text = new string('x', len);
            byte[] b = new Body().Str(text).U32(0xDEADBEEF).ToArray();
            Eq(b.Length % 4, 0, $"length {len} padded");
            var r = new NetReader(b);
            Check(r.String16L(out string got) && got == text && r.U32(out uint tail) && tail == 0xDEADBEEF, $"length {len} round trip");
        }
        var lat = new NetReader(new Body().Str("Café Æthel").ToArray());
        Check(lat.String16L(out string l) && l == "Café Æthel", "Latin-1 characters");
        var lng = new NetReader(new byte[] { 0xFF, 0xFF, 4, 0, 0, 0, 1, 2, 3, 4 });
        Check(!lng.String16L(out _), "0xFFFF long form refused");
        var shortStr = new NetReader(new byte[] { 10, 0, 1, 2 });
        Check(!shortStr.String16L(out _), "length past the end refused");
    }

    private static NetMessageInfo InspectTop(uint opcode, Body body)
    {
        ServerMessage.TryFromWire(body.Message(opcode), out ServerMessage m);
        return NetMessages.Inspect(m);
    }

    private static NetMessageInfo InspectEvent(uint type, Body body)
    {
        ServerMessage.TryFromWire(body.GameEvent(type), out ServerMessage m);
        return NetMessages.Inspect(m);
    }

    private static void Clean(NetMessageInfo i, string kind)
    {
        Eq(i.Kind, kind, "kind");
        Check(i.Known && i.Parsed, $"{kind} parsed");
        Eq(i.Leftover, 0, $"{kind} leftover");
    }

    private static void ChatParsers()
    {
        Case("HearSpeech 0x02BB");
        var i = InspectTop(0x02BB, new Body().Str("Hello there").Str("Aria").U32(0x50000010).U32(2));
        Clean(i, "HearSpeech");
        var s = (HearSpeech)i.Value!;
        Check(s.Text == "Hello there" && s.SenderName == "Aria" && s.SenderId == 0x50000010 && s.ChatType == 2, "fields");

        Case("HearRangedSpeech 0x02BC");
        i = InspectTop(0x02BC, new Body().Str("Help!").Str("Town Crier").U32(0x7A000001).F32(37.5f).U32(12));
        Clean(i, "HearRangedSpeech");
        var rs = (HearRangedSpeech)i.Value!;
        Check(rs.Text == "Help!" && rs.SenderName == "Town Crier" && rs.SenderId == 0x7A000001 && rs.Range == 37.5f && rs.ChatType == 12, "fields");

        Case("HearEmote 0x01E0 / HearSoulEmote 0x01E2");
        i = InspectTop(0x01E0, new Body().U32(0x50000011).Str("Bren").Str("bows deeply."));
        Clean(i, "HearEmote");
        var e = (HearEmote)i.Value!;
        Check(e.SenderId == 0x50000011 && e.SenderName == "Bren" && e.Text == "bows deeply." && !e.Soul, "emote fields");
        i = InspectTop(0x01E2, new Body().U32(0x50000012).Str("Cy").Str("*laughs*"));
        Clean(i, "HearSoulEmote");
        Check(((HearEmote)i.Value!).Soul, "soul flag");

        Case("HearDirectSpeech (tell) game event 0x02BD");
        i = InspectEvent(0x02BD, new Body().Str("psst").Str("Dax").U32(0x50000013).U32(0x50000001).U32(3).U32(0));
        Clean(i, "HearDirectSpeech");
        var t = (HearDirectSpeech)i.Value!;
        Check(t.Text == "psst" && t.SenderName == "Dax" && t.SenderId == 0x50000013 && t.TargetId == 0x50000001 && t.ChatType == 3 && t.Flags == 0, "fields");

        Case("ChannelBroadcast game event 0x0147");
        i = InspectEvent(0x0147, new Body().U32(0x800).Str("").Str("pulling now"));
        Clean(i, "ChannelBroadcast");
        var cb = (ChannelBroadcast)i.Value!;
        Check(cb.Channel == 0x800 && cb.SenderName == "" && cb.Text == "pulling now", "own line: empty sender");
    }

    private static Body FellowBody(Body b, uint id, string name, uint level, bool shareLoot)
        => b.U32(id).U32(1000).U32(10).U32(level).U32(300).U32(250).U32(200).U32(280).U32(240).U32(190).Bool(shareLoot).Str(name);

    private static void FellowshipParsers()
    {
        Case("FellowshipFullUpdate 0x02BE: members, flags, departed, locks");
        var b = new Body().TableHeader(2);
        FellowBody(b, 0x50000001, "Leader Lee", 126, true);
        FellowBody(b, 0x50000002, "Bo", 80, false);
        b.Str("Hunters").U32(0x50000001).Bool(true).Bool(false).Bool(true).Bool(false);
        b.TableHeader(1).U32(0x50000003).I32(1234);
        b.TableHeader(1).Str("Lugian Citadel").U32(1).U32(2).U32(3).U32(4).U32(5);
        var i = InspectEvent(0x02BE, b);
        Clean(i, "FellowshipFullUpdate");
        var f = (FellowshipFullUpdate)i.Value!;
        Check(f.Name == "Hunters" && f.LeaderId == 0x50000001 && f.ShareXp && !f.EvenXpSplit && f.Open && !f.Locked, "header fields");
        Eq(f.Fellows.Count, 2, "members");
        var lee = f.Fellows[0];
        Check(lee.Id == 0x50000001 && lee.Name == "Leader Lee" && lee.Level == 126 && lee.XpCached == 1000 && lee.LumCached == 10 &&
              lee.MaxHealth == 300 && lee.MaxStamina == 250 && lee.MaxMana == 200 && lee.Health == 280 && lee.Stamina == 240 && lee.Mana == 190 && lee.ShareLoot, "fellow fields");
        Check(!f.Fellows[1].ShareLoot && f.Fellows[1].Name == "Bo", "second fellow");
        Check(f.TailParsed && f.RecentlyDeparted.TryGetValue(0x50000003, out int when) && when == 1234 && f.LockNames.SequenceEqual(new[] { "Lugian Citadel" }), "tail");

        Case("FellowshipFullUpdate whose tail doesn't fit: members still returned, leftover reported");
        var b2 = new Body().TableHeader(1);
        FellowBody(b2, 0x50000001, "Solo", 50, true);
        b2.Str("One").U32(0x50000001).Bool(false).Bool(true).Bool(false).Bool(true);
        b2.Bytes(0x01, 0x02);   // a tail this layout doesn't know
        i = InspectEvent(0x02BE, b2);
        Check(i.Parsed && i.Leftover == 2, $"parsed with 2 leftover (parsed={i.Parsed} leftover={i.Leftover})");
        var f2 = (FellowshipFullUpdate)i.Value!;
        Check(!f2.TailParsed && f2.Fellows.Count == 1 && f2.Locked, "main fields kept, tail flagged unread");

        Case("FellowshipUpdateFellow 0x02C0");
        // fellow id, xp, lum, level, max h/s/m, current h/s/m, share loot, name, update type
        var u = new Body().U32(0x50000002).U32(1000).U32(10).U32(81).U32(300).U32(250).U32(200).U32(100).U32(50).U32(25).Bool(false).Str("Bo").U32(3);
        i = InspectEvent(0x02C0, u);
        Clean(i, "FellowshipUpdateFellow");
        var uf = (FellowshipUpdateFellow)i.Value!;
        Check(uf.Fellow.Id == 0x50000002 && uf.Fellow.Level == 81 && uf.Fellow.Health == 100 && uf.UpdateType == 3, "fields");

        Case("FellowshipQuit 0x00A3, FellowshipDismiss 0x00A4, FellowshipDisband 0x02BF");
        i = InspectEvent(0x00A3, new Body().U32(0x50000004));
        Clean(i, "FellowshipQuit");
        Check(((FellowshipMemberLeft)i.Value!).FellowId == 0x50000004 && !((FellowshipMemberLeft)i.Value!).Dismissed, "quit");
        i = InspectEvent(0x00A4, new Body().U32(0x50000005));
        Clean(i, "FellowshipDismiss");
        Check(((FellowshipMemberLeft)i.Value!).Dismissed, "dismiss");
        i = InspectEvent(0x02BF, new Body());
        Clean(i, "FellowshipDisband");
    }

    private static void FriendsAndAllegiance()
    {
        Case("FriendsUpdate 0x0021: full list");
        var b = new Body().U32(2)
            .U32(0x50000020).Bool(true).Bool(false).Str("Ana").U32List(0x50000001).U32List()
            .U32(0x50000021).Bool(false).Bool(true).Str("Bertram").U32List().U32List(0x50000001, 0x50000002)
            .U32(FriendsUpdate.Full);
        var i = InspectEvent(0x0021, b);
        Clean(i, "FriendsUpdate");
        var fu = (FriendsUpdate)i.Value!;
        Eq(fu.Friends.Count, 2, "two friends");
        Check(fu.Friends[0].Id == 0x50000020 && fu.Friends[0].Online && !fu.Friends[0].AppearOffline && fu.Friends[0].Name == "Ana" &&
              fu.Friends[0].OutFriends.SequenceEqual(new uint[] { 0x50000001 }) && fu.Friends[0].InFriends.Length == 0, "first");
        Check(!fu.Friends[1].Online && fu.Friends[1].AppearOffline && fu.Friends[1].InFriends.Length == 2, "second");
        Eq(fu.UpdateType, FriendsUpdate.Full, "update type");

        Case("FriendsUpdate: login change for one friend");
        i = InspectEvent(0x0021, new Body().U32(1).U32(0x50000020).Bool(false).Bool(false).Str("Ana").U32List().U32List().U32(FriendsUpdate.LoginChange));
        Clean(i, "FriendsUpdate");
        Check(((FriendsUpdate)i.Value!).UpdateType == FriendsUpdate.LoginChange, "type 4");

        Case("FriendsUpdate: absurd count refused without allocating it");
        i = InspectEvent(0x0021, new Body().U32(0x7FFFFFFF).U32(0));
        Check(i.Known && !i.Parsed, "refused");

        Case("AllegianceLoginNotification 0x027A");
        i = InspectEvent(0x027A, new Body().U32(0x50000030).Bool(true));
        Clean(i, "AllegianceLoginNotification");
        var a = (AllegianceLoginNotification)i.Value!;
        Check(a.CharacterId == 0x50000030 && a.LoggedIn, "fields");
    }

    private static void SystemTextAndConfirmation()
    {
        Case("PopUpString 0x0004, TransientString 0x02EB");
        var i = InspectEvent(0x0004, new Body().Str("You have been booted."));
        Clean(i, "PopUpString");
        Check(((SystemText)i.Value!).Text == "You have been booted." && !((SystemText)i.Value!).Transient, "popup");
        i = InspectEvent(0x02EB, new Body().Str("You're too busy!"));
        Clean(i, "TransientString");
        Check(((SystemText)i.Value!).Transient, "transient");

        Case("ConfirmationRequest 0x0274");
        i = InspectEvent(0x0274, new Body().U32(4).U32(0x1234).Str("Ana has invited you to join a fellowship."));
        Clean(i, "ConfirmationRequest");
        var c = (ConfirmationRequest)i.Value!;
        Check(c.ConfirmationType == 4 && c.ContextId == 0x1234 && c.Text.StartsWith("Ana has invited"), "fields");

        Case("unknown messages are reported as unknown");
        i = InspectTop(0xF745, new Body().U32(1));
        Check(!i.Known, "top-level");
        i = InspectEvent(0x02C2, new Body().U32(1));
        Check(!i.Known, "game event");
    }

    private static void TruncationNeverThrows()
    {
        Case("every parser, every truncation of its fixture: false, no exception");
        var fixtures = new List<(string, byte[])>
        {
            ("speech", new Body().Str("Hello there").Str("Aria").U32(1).U32(2).Message(0x02BB)),
            ("ranged", new Body().Str("Help!").Str("Crier").U32(1).F32(1).U32(2).Message(0x02BC)),
            ("emote", new Body().U32(1).Str("Bren").Str("bows.").Message(0x01E0)),
            ("tell", new Body().Str("psst").Str("Dax").U32(1).U32(2).U32(3).U32(0).GameEvent(0x02BD)),
            ("channel", new Body().U32(1).Str("A").Str("B").GameEvent(0x0147)),
            ("friends", new Body().U32(1).U32(1).Bool(true).Bool(false).Str("Ana").U32List(5).U32List(6).U32(0).GameEvent(0x0021)),
            ("allegiance", new Body().U32(1).Bool(true).GameEvent(0x027A)),
            ("confirm", new Body().U32(4).U32(5).Str("ok?").GameEvent(0x0274)),
            ("popup", new Body().Str("hi").GameEvent(0x0004)),
            ("update fellow", new Body().U32(1).U32(1).U32(1).U32(1).U32(1).U32(1).U32(1).U32(1).U32(1).U32(1).Bool(true).Str("Bo").U32(1).GameEvent(0x02C0)),
            ("quit", new Body().U32(1).GameEvent(0x00A3)),
        };
        var full = new Body().TableHeader(1);
        FellowBody(full, 1, "Lee", 5, true);
        full.Str("F").U32(1).Bool(true).Bool(true).Bool(true).Bool(true).TableHeader(0).TableHeader(0);
        fixtures.Add(("fellowship", full.GameEvent(0x02BE)));

        int cuts = 0;
        foreach ((string name, byte[] wire) in fixtures)
        {
            ServerMessage.TryFromWire(wire, out ServerMessage whole);
            NetMessageInfo w = NetMessages.Inspect(whole);
            if (!w.Parsed || w.Leftover != 0) { Check(false, $"{name}: full fixture should parse cleanly"); continue; }
            int headerLen = whole.IsGameEvent ? 16 : 4;
            for (int len = headerLen; len < wire.Length; len++)
            {
                try
                {
                    ServerMessage.TryFromWire(wire.AsSpan(0, len), out ServerMessage m);
                    NetMessageInfo i = NetMessages.Inspect(m);
                    // A cut on a string's padding or the fellowship tail can still parse; it must then report what it used.
                    if (i.Parsed && name != "fellowship" && i.Leftover != 0) Check(false, $"{name} cut at {len}: parsed but leftover {i.Leftover}");
                    cuts++;
                }
                catch (Exception ex)
                {
                    Check(false, $"{name} cut at {len} threw {ex.GetType().Name}");
                }
            }
        }
        Check(cuts > 300, $"{cuts} truncations tried");
    }

    private static void EndToEndGameEvents()
    {
        Case("end to end: a 3-datagram fellowship update, out of order, through ring and reassembler to the parser");
        var b = new Body().TableHeader(9);
        for (uint n = 0; n < 9; n++) FellowBody(b, 0x50000100 + n, $"Member number {n} with a long name", 100 + n, n % 2 == 0);
        b.Str("A fellowship with a long name").U32(0x50000100).Bool(true).Bool(true).Bool(false).Bool(false).TableHeader(0).TableHeader(0);
        byte[] wire = b.GameEvent(0x02BE, 0x50000100, 42);
        Frag[] frags = Datagram.Split(500, wire);
        Check(frags.Length >= 2, $"{wire.Length} bytes needs {frags.Length} fragments");
        var ring = new PacketRing(4096);
        var order = frags.Reverse().ToArray();
        foreach (Frag f in order) Write(ring, Datagram.Build(Datagram.AckSequence | Datagram.TimeSync, 0, null, f));
        var r = new FragmentReassembler();
        var c = new Collect();
        while (ring.TryPeek(out byte* p, out int len, out _, out int rel))
        {
            r.ProcessDatagram(p, len, _now, c);
            ring.Release(rel);
        }
        Eq(c.Messages.Count, 1, "one message");
        if (c.Messages.Count != 1) return;
        ServerMessage.TryFromWire(c.Messages[0].Bytes, out ServerMessage m);
        Check(m.IsGameEvent && m.EventObjectId == 0x50000100 && m.EventSequence == 42 && m.EventType == 0x02BE, "game event header");
        Check(FellowshipFullUpdate.TryParse(m.EventBody, out var fs, out int used) && used == m.EventBody.Length, "parsed whole");
        Check(fs != null && fs.Fellows.Count == 9 && fs.Fellows[8].Name == "Member number 8 with a long name" && fs.TailParsed, "all members");
    }

    // ── replay of captures ──────────────────────────────────────────────────

    private static void Replay(string path)
    {
        Case($"replay {Path.GetFileName(path)}");
        byte[] file;
        try { file = File.ReadAllBytes(path); }
        catch (Exception ex) { Check(false, $"read failed: {ex.Message}"); return; }
        if (file.Length < 4 || file[0] != 'R' || file[1] != 'N' || file[2] != 'C' || file[3] != '1')
        {
            Check(false, "not an RNC1 capture");
            return;
        }
        var r = new FragmentReassembler();
        var c = new Collect();
        int pos = 4, datagrams = 0;
        while (pos + 6 <= file.Length)
        {
            uint stamp = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(pos));
            int len = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(pos + 4));
            pos += 6;
            if (pos + len > file.Length) { Check(false, $"truncated record at {pos}"); break; }
            fixed (byte* p = &file[pos])
                r.ProcessDatagram(p, len, stamp, c);
            pos += len;
            datagrams++;
        }
        var byKind = new SortedDictionary<string, (int Ok, int Failed, int Leftover)>();
        var opcodes = new Dictionary<string, int>();
        foreach (var (_, bytes) in c.Messages)
        {
            ServerMessage.TryFromWire(bytes, out ServerMessage m);
            string key = m.IsGameEvent ? $"F7B0/{m.EventType:X4}" : $"{m.Opcode:X4}";
            opcodes[key] = opcodes.GetValueOrDefault(key) + 1;
            NetMessageInfo i = NetMessages.Inspect(m);
            if (!i.Known) continue;
            var k = byKind.GetValueOrDefault(i.Kind);
            byKind[i.Kind] = (k.Ok + (i.Parsed ? 1 : 0), k.Failed + (i.Parsed ? 0 : 1), k.Leftover + (i.Parsed && i.Leftover != 0 ? 1 : 0));
            if (!i.Parsed || i.Leftover != 0)
                Console.WriteLine($"    {i.Kind} parsed={i.Parsed} leftover={i.Leftover} bytes={Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 64)))}");
        }
        Console.WriteLine($"    {datagrams} datagrams -> {r.Messages} messages ({r.MultiFragmentMessages} multi-fragment); malformed {r.PacketsMalformed}, " +
                          $"unknown header {r.PacketsUnknownOptional}, dup {r.DuplicateFragments}, bad {r.BadFragments}, stale {r.EvictedStale}, {r.PartialCount} left partial");
        Console.WriteLine($"    top opcodes: {string.Join(" ", opcodes.OrderByDescending(o => o.Value).Take(15).Select(o => $"{o.Key}x{o.Value}"))}");
        foreach (var (kind, v) in byKind)
            Console.WriteLine($"    {kind}: {v.Ok} ok, {v.Failed} failed, {v.Leftover} with leftover");
        Check(r.PacketsMalformed == 0, "no malformed datagrams in a real capture");
        Check(byKind.Values.All(v => v.Failed == 0), "every known message parsed");
    }

    // ── performance ─────────────────────────────────────────────────────────

    private static void Performance()
    {
        Console.WriteLine();
        Console.WriteLine("Performance (synthetic server traffic; Release build, this machine):");

        // A mix shaped like play: mostly one-fragment messages of 20-300 bytes, packed 1-3 per
        // datagram, plus a 2-8 fragment message every ~25 datagrams; acks and time sync on many.
        var rng = new Random(99);
        var datagrams = new List<byte[]>();
        uint seq = 1;
        int msgCount = 0;
        while (datagrams.Count < 50_000)
        {
            if (rng.Next(25) == 0)
            {
                byte[] big = Msg(rng.Next(900, 448 * 8), 0xF7B0, (byte)seq);
                foreach (Frag f in Datagram.Split(seq++, big)) datagrams.Add(Datagram.Build(Datagram.AckSequence, 0, null, f));
                msgCount++;
                continue;
            }
            int n = rng.Next(1, 4);
            var frags = new Frag[n];
            int room = 448;
            for (int k = 0; k < n; k++)
            {
                int len = Math.Min(rng.Next(20, 300), room - 16 * (n - k));
                if (len < 8) len = 8;
                room -= len + 16;
                frags[k] = new Frag(seq++, 1, 0, Msg(len, 0xF749, (byte)k));
                msgCount++;
            }
            uint flags = rng.Next(4) == 0 ? Datagram.AckSequence | Datagram.TimeSync : Datagram.AckSequence;
            datagrams.Add(Datagram.Build(flags, 0, null, frags));
        }
        long totalBytes = datagrams.Sum(d => (long)d.Length);
        var pinned = datagrams.Select(d => GC.AllocateArray<byte>(d.Length, pinned: true)).ToArray();
        for (int i = 0; i < pinned.Length; i++) datagrams[i].CopyTo(pinned[i], 0);

        // 1) Receive side: what the detour does while capturing (flags check + ring write),
        //    timed in batches of 1024 datagrams (the ring is emptied between batches, untimed).
        var ring = new PacketRing(512 * 1024);
        const int Rounds = 20, Batch = 1024;
        long copies = 0, hookTicks = 0;
        for (int round = 0; round < Rounds + 2; round++)
        {
            bool timed = round >= 2;
            for (int i0 = 0; i0 < pinned.Length; i0 += Batch)
            {
                int i1 = Math.Min(pinned.Length, i0 + Batch);
                long t0 = Stopwatch.GetTimestamp();
                for (int i = i0; i < i1; i++)
                {
                    byte[] d = pinned[i];
                    fixed (byte* p = d)
                    {
                        uint flags = AcPacketWire.ReadU32(p + 4);
                        if ((flags & (PacketFlags.BlobFragments | PacketFlags.ConnectRequest)) != 0)
                            ring.TryWrite(p, d.Length, 0);
                    }
                }
                long t1 = Stopwatch.GetTimestamp();
                if (timed) { hookTicks += t1 - t0; copies += i1 - i0; }
                ring.Clear();
            }
        }
        double tickNs = 1e9 / Stopwatch.Frequency;
        double hookNs = hookTicks * tickNs / copies;
        Console.WriteLine($"  receive hook copy: {hookNs:0} ns per datagram (avg {totalBytes / datagrams.Count} bytes), {copies:N0} datagrams; ring drops {ring.DroppedFull}");

        // 2) Pump side: ring read + reassembly into a counting sink, timed per drained batch.
        var r = new FragmentReassembler();
        var sink = new CountOnly();
        long allocBefore = 0, processed = 0, pumpTicks = 0, msgsTimed = 0, multiTimed = 0;
        for (int round = 0; round < Rounds + 2; round++)
        {
            bool timed = round >= 2;
            r.Reset();
            if (round == 2) allocBefore = GC.GetAllocatedBytesForCurrentThread();
            long m0 = r.Messages, mm0 = r.MultiFragmentMessages;
            for (int i0 = 0; i0 < pinned.Length; i0 += Batch)
            {
                int i1 = Math.Min(pinned.Length, i0 + Batch);
                for (int i = i0; i < i1; i++)
                    fixed (byte* p = pinned[i])
                        ring.TryWrite(p, pinned[i].Length, 0);
                long t0 = Stopwatch.GetTimestamp();
                while (ring.TryPeek(out byte* rp, out int len, out _, out int rel))
                {
                    r.ProcessDatagram(rp, len, 1000, sink);
                    ring.Release(rel);
                }
                long t1 = Stopwatch.GetTimestamp();
                if (timed) { pumpTicks += t1 - t0; processed += i1 - i0; }
            }
            if (timed) { msgsTimed += r.Messages - m0; multiTimed += r.MultiFragmentMessages - mm0; }
        }
        long allocAfter = GC.GetAllocatedBytesForCurrentThread();
        double pumpNs = pumpTicks * tickNs / processed;
        Console.WriteLine($"  pump (ring read + reassembly): {pumpNs:0} ns per datagram, {pumpTicks * tickNs / msgsTimed:0} ns per message; " +
                          $"{msgsTimed / Rounds:N0} messages per round of {pinned.Length:N0} datagrams ({multiTimed / Rounds:N0} multi-fragment)");
        Console.WriteLine($"  steady-state allocation: {(allocAfter - allocBefore) / (double)processed:0.00} bytes per datagram over {processed:N0} datagrams");

        // 3) What a busy minute costs: ~100 datagrams/s is heavy play, 400/s a portal into a town.
        Console.WriteLine($"  at 400 datagrams/s: hook {hookNs * 400 / 1000:0.0} us/s on the receive thread, pump {pumpNs * 400 / 1000:0.0} us/s");
        Console.WriteLine("  stream off: the detour adds one volatile bool read (ServerMessageStream.OnDatagram returns at once).");
    }
}
