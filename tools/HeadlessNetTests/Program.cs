// ============================================================================
//  HeadlessNetTests - offline checks for RynthCore.Headless's protocol layer.
//
//  Known-answer vectors:
//    - ISAAC with AC seeding, seed 0x12345678, and the Hash32 cases are taken from
//      OpenAC's tests (tests/AcDream.Core.Net.Tests/Cryptography/IsaacRandomTests.cs and
//      Hash32Tests.cs, MIT licence, Copyright (c) the OpenAC contributors), where they are
//      recorded as matching ACE.
//    - Captures\ace-login-session.txt: a real session against the local ACE server
//      (login, enter world, chat, logout), replayed through the transport and parsers.
//  Everything else builds packets the way the protocol describes them and checks both
//  directions against each other plus hand-computed values.
// ============================================================================

using System.Buffers.Binary;
using RynthCore.Engine.Net;
using RynthCore.Headless.Messages;
using RynthCore.Headless.Net;
using RynthCore.Headless.World;
using RynthCore.Headless.Session;
using RynthCore.PluginSdk.Net;

namespace HeadlessNetTests;

internal static class Program
{
    private static int _checks, _failed;
    private static string _case = "";

    private static int Main()
    {
        Hash32Vectors();
        IsaacVectors();
        IsaacStreamsDiffer();
        Strings();
        PackedU32();
        PacketPlainRoundTrip();
        PacketEncryptedRoundTrip();
        OptionalSections();
        HandshakeAndSequences();
        ResendIsByteIdentical();
        InboundGapNakAndFill();
        InboundOutOfOrderFragments();
        InboundKeyLookAhead();
        AckOnlyAtNewSequenceMeansLoss();
        RejectRetransmitStopsNaks();
        LargeMessageSplits();
        LoginRequestLayout();
        CharacterListParse();
        ObjectCreateParse();
        CapturedAceSession();
        CapturedWorldModel();
        CapturedP1Session();
        UpdatePositionLayouts();
        VitalIdMapping();
        WeenieHeaderAllFields();
        PlayerDescriptionWithBlob();
        RelocateCells();
        InventoryEvents();
        MotionPacking();

        Console.WriteLine();
        Console.WriteLine($"{_checks - _failed}/{_checks} checks passed.");
        Console.WriteLine(_failed == 0 ? "ALL HEADLESS NET TESTS PASSED." : $"{_failed} FAILED.");
        return _failed == 0 ? 0 : 1;
    }

    // ── primitives ──────────────────────────────────────────────────────────

    private static void Hash32Vectors()
    {
        Case("Hash32 vectors");
        Eq(Hash32.Compute(Array.Empty<byte>()), 0u, "empty");
        Eq(Hash32.Compute(new byte[] { 0x01, 0x02, 0x03, 0x04 }), 0x04070201u, "one word");
        Eq(Hash32.Compute(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE }), 0xCBD1BBAAu, "word + 1 tail");
        Eq(Hash32.Compute(new byte[] { 0x42 }), 0x42010000u, "1 byte");
        Eq(Hash32.Compute(new byte[] { 0x42, 0x7F }), 0x42810000u, "2 bytes");
        Eq(Hash32.Compute(new byte[] { 0x01, 0x02, 0x03 }), 0x01050300u, "3 bytes");
    }

    private static void IsaacVectors()
    {
        Case("ISAAC, AC seeding, seed 0x12345678");
        uint[] expected =
        {
            0xFDD4AFEB, 0x398311D2, 0xC71829A4, 0xB19DF96A, 0x7ED4A1E7, 0xB718A446, 0x5FA77DF1, 0x5CB793D2,
            0x52C95BAC, 0xA8F8FD7E, 0x18582C04, 0xD257F506, 0x4D11A3F0, 0x919DE7A7, 0x1D809C6B, 0xE97F65F8,
        };
        var isaac = new Isaac(0x12345678);
        for (int i = 0; i < expected.Length; i++)
            Eq(isaac.Next(), expected[i], $"word {i + 1}");

        // Past a round boundary the generator keeps going without repeating its first block.
        var a = new Isaac(0x12345678);
        var first = new uint[256];
        for (int i = 0; i < 256; i++) first[i] = a.Next();
        int same = 0;
        for (int i = 0; i < 256; i++) if (a.Next() == first[i]) same++;
        Check(same < 4, "second block differs from the first");
    }

    private static void IsaacStreamsDiffer()
    {
        Case("ISAAC seeds");
        Check(new Isaac(1).Next() != new Isaac(2).Next(), "different seeds, different words");
        Eq(new Isaac(77).Next(), new Isaac(77).Next(), "same seed, same word");
    }

    private static void Strings()
    {
        Case("String16L / String32L");
        Bytes(new WireWriter().String16L("").ToArray(), "00 00 00 00", "empty");
        Bytes(new WireWriter().String16L("ab").ToArray(), "02 00 61 62", "2 chars, no pad");
        Bytes(new WireWriter().String16L("abc").ToArray(), "03 00 61 62 63 00 00 00", "3 chars, padded");
        Bytes(new WireWriter().String16L("1802").ToArray(), "04 00 31 38 30 32 00 00", "version string");
        var r = new NetReader(new WireWriter().String16L("Hello").U32(7).ToArray());
        Check(r.String16L(out string s) && s == "Hello" && r.U32(out uint v) && v == 7, "SDK reader reads it back");

        Bytes(new WireWriter().String32L("pw").ToArray(), "03 00 00 00 02 70 77 00", "short password");
        byte[] longPw = new WireWriter().String32L(new string('x', 200)).ToArray();
        Eq(BinaryPrimitives.ReadUInt32LittleEndian(longPw), 202u, "long: byte count");
        Check(longPw[4] == 0x80 && longPw[5] == 200, "long: two-byte prefix");
    }

    private static void PackedU32()
    {
        Case("packed u32");
        Bytes(new WireWriter().PackedU32(0x1234).ToArray(), "34 12", "small");
        Bytes(new WireWriter().PackedU32(0x00012345).ToArray(), "01 80 45 23", "large");
        foreach (uint v in new uint[] { 0, 1, 0x7FFF, 0x8000, 0x12345, 0x06001234 & 0xFFFFFF, 0x7FFFFFFF })
        {
            var r = new NetReader(new WireWriter().PackedU32(v).ToArray());
            Check(ObjectCreate.PackedU32(ref r, out uint back) && back == v, $"round trip 0x{v:X}");
        }
    }

    // ── packets ─────────────────────────────────────────────────────────────

    private static void PacketPlainRoundTrip()
    {
        Case("packet, plain checksum");
        byte[] msg = new WireWriter().U32(0xF7C8).ToArray();
        var frag = new Fragment(1, 0x80000000, 1, 0, MessageQueue.Login, msg);
        byte[] d = Packet.Build(5, 0, 0x0003, 9, 1, ReadOnlySpan<byte>.Empty, new[] { frag }, null, out uint sealedPayload);

        Eq(d.Length, 20 + 16 + 4, "length");
        Check(InboundPacket.TryParse(d, out InboundPacket p), "parses");
        Eq(p.Sequence, 5u, "seq");
        Eq(p.Flags, PacketFlags.BlobFragments, "flags");
        Eq(p.Id, (ushort)3, "id");
        Eq(p.Size, (ushort)20, "size");
        Eq(p.OptionalEnd, 20, "no optional");

        // Hand computation: header hash + Hash32(fragment header) + Hash32(data).
        uint expect = Packet.HeaderHash(d) + Hash32.Compute(d.AsSpan(20, 16)) + Hash32.Compute(d.AsSpan(36, 4));
        Eq(p.Checksum, expect, "checksum = header + fragment header + data");
        Eq(sealedPayload, expect - Packet.HeaderHash(d), "sealed payload");

        // The header hash really uses 0xBADD70DD, not the stored checksum.
        byte[] h = d.AsSpan(0, 20).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), 0xBADD70DD);
        Eq(Packet.HeaderHash(d), Hash32.Compute(h), "header hash placeholder");

        // The reassembler (engine code) walks the datagram we built.
        var sink = new Sink();
        unsafe { fixed (byte* b = d) new FragmentReassembler().ProcessDatagram(b, d.Length, 0, sink); }
        Check(sink.Messages.Count == 1 && sink.Messages[0].SequenceEqual(msg), "engine reassembler reads it");
    }

    private static void PacketEncryptedRoundTrip()
    {
        Case("packet, encrypted checksum");
        var keys = new Isaac(0xCAFEBABE);
        uint key = keys.Next();
        var frag = new Fragment(2, 0x80000000, 1, 0, MessageQueue.UI, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 });
        byte[] d = Packet.Build(2, 0, 7, 1, 1, ReadOnlySpan<byte>.Empty, new[] { frag }, key, out _);
        Check(InboundPacket.TryParse(d, out InboundPacket p), "parses");
        Check(p.Encrypted && p.Has(PacketFlags.BlobFragments), "flags");
        Check(Packet.TryPayloadHash(d, p.OptionalEnd, out uint payload), "payload hash");
        Eq((p.Checksum - Packet.HeaderHash(d)) ^ payload, key, "key recovered from checksum");
    }

    private static void OptionalSections()
    {
        Case("optional sections");
        var w = new WireWriter();
        w.U32(2).U32(10).U32(11);       // RequestRetransmit
        w.U32(1).U32(12);               // RejectRetransmit
        w.U32(9);                       // AckSequence
        w.F64(1234.5);                  // TimeSync
        w.F32(1.5f).F32(0.25f);         // EchoResponse
        uint flags = PacketFlags.RequestRetransmit | PacketFlags.RejectRetransmit | PacketFlags.AckSequence
                     | PacketFlags.TimeSync | PacketFlags.EchoResponse;
        byte[] d = Packet.Build(3, flags, 0x0B, 0, 1, w.Span, null, null, out _);
        Check(InboundPacket.TryParse(d, out InboundPacket p), "parses");
        Check(p.Naks.SequenceEqual(new uint[] { 10, 11 }), "naks");
        Check(p.Rejects.SequenceEqual(new uint[] { 12 }), "rejects");
        Eq(p.Ack, 9u, "ack");
        Eq(p.TimeSync, 1234.5, "time sync");
        Check(p.EchoClientTime == 1.5f && p.EchoServerDelta == 0.25f, "echo response");
        Eq(p.OptionalEnd, d.Length, "all consumed");
        Check(Packet.TryPayloadHash(d, p.OptionalEnd, out uint ph) && Packet.HeaderHash(d) + ph == p.Checksum, "plain checksum covers optional bytes");

        // A size that runs past the datagram is refused.
        byte[] bad = (byte[])d.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(bad.AsSpan(16), 400);
        Check(!InboundPacket.TryParse(bad, out _), "oversized size refused");
        Check(!InboundPacket.TryParse(new byte[10], out _), "short datagram refused");
    }

    // ── transport ───────────────────────────────────────────────────────────

    private sealed class FakeLink : IDatagramLink
    {
        public readonly List<(byte[] Data, bool ConnectPort)> Sent = new();
        public readonly Queue<byte[]> Inbox = new();
        public void Send(ReadOnlySpan<byte> d, bool connectPort) => Sent.Add((d.ToArray(), connectPort));
        public bool TryReceive(out byte[] d) => Inbox.TryDequeue(out d!);
    }

    /// <summary>The server side of a connection, enough to drive a Transport.</summary>
    private sealed class FakeServer
    {
        public const uint ClientSeed = 0x11111111, ServerSeed = 0x22222222;
        public readonly Isaac Keys = new(ServerSeed);
        public readonly Isaac ClientKeys = new(ClientSeed);
        public uint Seq;
        public uint FragSeq;

        public byte[] ConnectRequest()
        {
            var w = new WireWriter();
            w.F64(100.0).U64(0x0102030405060708).U32(0x0005).U32(ServerSeed).U32(ClientSeed).U32(0);
            return Packet.Build(0, PacketFlags.ConnectRequest, 0x0B, 0, 1, w.Span, null, null, out _);
        }

        public byte[] Message(byte[] msg, uint? seq = null)
        {
            var frag = new Fragment(++FragSeq, 0x80000000, 1, 0, MessageQueue.UI, msg);
            return Packet.Build(seq ?? ++Seq, 0, 0x0B, 0, 1, ReadOnlySpan<byte>.Empty, new[] { frag }, Keys.Next(), out _);
        }

        public byte[] Fragments(byte[] msg, uint fragSeq, int index, int count, int chunk)
        {
            byte[] part = msg.AsSpan(index * chunk, Math.Min(chunk, msg.Length - index * chunk)).ToArray();
            var frag = new Fragment(fragSeq, 0x80000000, (ushort)count, (ushort)index, MessageQueue.UI, part);
            return Packet.Build(++Seq, 0, 0x0B, 0, 1, ReadOnlySpan<byte>.Empty, new[] { frag }, Keys.Next(), out _);
        }

        public byte[] AckOnly(uint ack, uint seq)
        {
            var w = new WireWriter().U32(ack);
            return Packet.Build(seq, PacketFlags.AckSequence, 0x0B, 0, 1, w.Span, null, null, out _);
        }

        public byte[] Control(uint flags, uint seq, params uint[] list)
        {
            var w = new WireWriter().U32((uint)list.Length);
            foreach (uint v in list) w.U32(v);
            return Packet.Build(seq, flags, 0x0B, 0, 1, w.Span, null, null, out _);
        }
    }

    private static (Transport t, FakeLink link, FakeServer srv, Func<long> clock, Action<long> advance) Connected()
    {
        long now = 1000;
        var link = new FakeLink();
        var t = new Transport(link, () => now);
        var srv = new FakeServer();
        t.SendLoginRequest(LoginMessages.LoginRequest("acct", "pw", 1));
        t.Receive(srv.ConnectRequest());
        now += Transport.ConnectResponseDelayMs;
        t.Tick();
        srv.Seq = 1;                          // the server's first encrypted packet is seq 2
        t.Receive(srv.Message(new WireWriter().U32(0xF7E1).U32(0).U32(0).String16L("x").ToArray()));
        return (t, link, srv, () => now, ms => now += ms);
    }

    private static void HandshakeAndSequences()
    {
        Case("handshake and our sequences");
        var (t, link, srv, _, advance) = Connected();

        var login = link.Sent[0];
        Check(InboundPacket.TryParse(login.Data, out InboundPacket lp) && lp.Sequence == 0 && lp.Flags == PacketFlags.LoginRequest, "LoginRequest: seq 0, flag only");
        Check(!login.ConnectPort, "LoginRequest to the main port");
        Check(Packet.TryPayloadHash(login.Data, lp.OptionalEnd, out uint lph) && Packet.HeaderHash(login.Data) + lph == lp.Checksum, "LoginRequest checksum");

        var cr = link.Sent[1];
        Check(cr.ConnectPort, "ConnectResponse to port + 1");
        Check(InboundPacket.TryParse(cr.Data, out InboundPacket cp) && cp.Sequence == 1 && cp.Flags == PacketFlags.ConnectResponse, "ConnectResponse: seq 1");
        Eq(BinaryPrimitives.ReadUInt64LittleEndian(cr.Data.AsSpan(20)), 0x0102030405060708ul, "cookie echoed");
        Check(t.ServerTalked && t.TryDequeue(out byte[] first) && BinaryPrimitives.ReadUInt32LittleEndian(first) == 0xF7E1, "first message delivered");
        Eq(t.BadChecksums, 0L, "server checksums verify");

        // No more ConnectResponses once the server talks.
        int before = link.Sent.Count;
        advance(1000);
        t.Tick();
        Check(link.Sent.Skip(before).All(s => !s.ConnectPort), "ConnectResponse stops");

        // First game message: seq 2, fragment seq 1, encrypted with the first ClientSeed word.
        t.SendMessage(LoginMessages.EnterWorldRequest(), MessageQueue.Login);
        byte[] m = link.Sent[^1].Data;
        Check(InboundPacket.TryParse(m, out InboundPacket mp), "message parses");
        Eq(mp.Sequence, 2u, "first sequenced packet is 2");
        Eq(mp.Id, (ushort)5, "header id = our client id");
        Check(mp.Encrypted && mp.Has(PacketFlags.BlobFragments), "encrypted, fragments");
        Eq(BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(20)), 1u, "fragment seq 1");
        Check(Packet.TryPayloadHash(m, mp.OptionalEnd, out uint mph), "payload");
        Eq((mp.Checksum - Packet.HeaderHash(m)) ^ mph, srv.ClientKeys.Next(), "key = ClientSeed word 1");

        t.SendMessage(LoginMessages.LogOff(), MessageQueue.Login);
        Check(InboundPacket.TryParse(link.Sent[^1].Data, out InboundPacket mp2) && mp2.Sequence == 3, "then 3");
        Check(Packet.TryPayloadHash(link.Sent[^1].Data, mp2.OptionalEnd, out uint mph2), "payload 2");
        Eq((mp2.Checksum - Packet.HeaderHash(link.Sent[^1].Data)) ^ mph2, srv.ClientKeys.Next(), "key = ClientSeed word 2");

        // Ack: plain, AckSequence only, our current sequence, the server's contiguous high.
        advance(Transport.AckIntervalMs);
        t.Receive(srv.Message(new WireWriter().U32(0xF7E1).ToArray()));
        t.Tick();
        byte[] ack = link.Sent[^1].Data;
        Check(InboundPacket.TryParse(ack, out InboundPacket ap) && ap.Flags == PacketFlags.AckSequence, "ack-only flags");
        Eq(ap.Sequence, 3u, "ack reuses our current sequence");
        Eq(ap.Ack, 3u, "acks server seq 3");

        // Keepalive: an ack even with nothing new.
        int n = link.Sent.Count;
        advance(Transport.KeepAliveMs);
        t.Tick();
        Check(link.Sent.Count == n + 1 && InboundPacket.TryParse(link.Sent[^1].Data, out InboundPacket kp) && kp.Flags == PacketFlags.AckSequence, "keepalive ack");
    }

    private static void ResendIsByteIdentical()
    {
        Case("resend on server NAK");
        var (t, link, srv, _, _) = Connected();
        t.SendMessage(LoginMessages.EnterWorldRequest(), MessageQueue.Login);
        byte[] original = link.Sent[^1].Data.ToArray();

        t.Receive(srv.Control(PacketFlags.RequestRetransmit, srv.Seq, 2));
        byte[] again = link.Sent[^1].Data;
        Eq(t.Resends, 1L, "resent once");
        Check(InboundPacket.TryParse(again, out InboundPacket p) && p.Has(PacketFlags.Retransmission), "Retransmission flag");
        Check(again.AsSpan(12).SequenceEqual(original.AsSpan(12)) && again.AsSpan(0, 4).SequenceEqual(original.AsSpan(0, 4)), "same seq, id, size, body");
        Check(Packet.TryPayloadHash(again, p.OptionalEnd, out uint ph), "payload");
        InboundPacket.TryParse(original, out InboundPacket op);
        Packet.TryPayloadHash(original, 20, out uint oph);
        Eq((p.Checksum - Packet.HeaderHash(again)) ^ ph, (op.Checksum - Packet.HeaderHash(original)) ^ oph, "same key");

        // Acked packets are forgotten.
        t.Receive(srv.AckOnly(2, srv.Seq));
        long r = t.Resends;
        t.Receive(srv.Control(PacketFlags.RequestRetransmit, srv.Seq, 2));
        Eq(t.Resends, r, "nothing to resend after the ack");
    }

    private static void InboundGapNakAndFill()
    {
        Case("inbound gap: NAK, then fill");
        var (t, link, srv, _, advance) = Connected();
        byte[] m3 = srv.Message(Msg(0x1003));
        byte[] m4 = srv.Message(Msg(0x1004));
        byte[] m5 = srv.Message(Msg(0x1005));
        t.Receive(m3);
        t.Receive(m5);                               // 4 lost
        Check(t.Missing.SequenceEqual(new uint[] { 4 }), "4 missing");
        Eq(t.ContiguousReceived, 3u, "contiguous stops at 3");
        Eq(t.BadChecksums, 0L, "keys matched across the gap");

        advance(10);
        t.Tick();
        byte[]? nak = link.Sent.Select(s => s.Data).LastOrDefault(d => InboundPacket.TryParse(d, out InboundPacket p) && p.Has(PacketFlags.RequestRetransmit));
        Check(nak != null && InboundPacket.TryParse(nak, out InboundPacket np) && np.Naks.SequenceEqual(new uint[] { 4 }) && !np.Encrypted, "plain NAK for 4");

        int nakCount = link.Sent.Count;
        t.Tick();
        Check(link.Sent.Count == nakCount, "NAK not repeated within a second");

        // The server resends 4 with its original key and the Retransmission flag.
        byte[] resent = (byte[])m4.Clone();
        InboundPacket.TryParse(resent, out InboundPacket rp);
        Packet.TryPayloadHash(resent, rp.OptionalEnd, out uint rph);
        uint key = (rp.Checksum - Packet.HeaderHash(resent)) ^ rph;
        BinaryPrimitives.WriteUInt32LittleEndian(resent.AsSpan(4), rp.Flags | PacketFlags.Retransmission);
        Packet.Seal(resent, rph ^ key);
        t.Receive(resent);
        Eq(t.Missing.Count, 0, "gap filled");
        Eq(t.ContiguousReceived, 5u, "contiguous 5");
        Eq(t.BadChecksums, 0L, "resent key accepted");

        var opcodes = Drain(t);
        Check(opcodes.Contains(0x1003u) && opcodes.Contains(0x1004u) && opcodes.Contains(0x1005u), "all three delivered");

        t.Receive(m5);
        Eq(t.Duplicates, 1L, "duplicate dropped");
        Eq(Drain(t).Count, 0, "not delivered twice");
    }

    private static void InboundOutOfOrderFragments()
    {
        Case("multi-fragment message, out of order");
        var (t, _, srv, _, _) = Connected();
        Drain(t);
        var big = new WireWriter().U32(0x2222);
        for (int i = 0; i < 300; i++) big.U32((uint)i);
        byte[] msg = big.ToArray();                 // 1204 bytes -> 3 fragments of 448
        uint fs = ++srv.FragSeq;
        byte[] f0 = srv.Fragments(msg, fs, 0, 3, 448);
        byte[] f1 = srv.Fragments(msg, fs, 1, 3, 448);
        byte[] f2 = srv.Fragments(msg, fs, 2, 3, 448);
        t.Receive(f2);
        t.Receive(f0);
        Eq(Drain(t).Count, 0, "incomplete: nothing yet");
        t.Receive(f1);
        Check(t.TryDequeue(out byte[] whole) && whole.SequenceEqual(msg), "reassembled in index order");
        Eq(t.BadChecksums, 0L, "keys out of order still match");
    }

    private static void InboundKeyLookAhead()
    {
        Case("inbound key window");
        var srvKeys = new Isaac(42);
        var k = new InboundKeys(42);
        uint w1 = srvKeys.Next(), w2 = srvKeys.Next(), w3 = srvKeys.Next();
        Check(k.TryMatch(w3, false), "third word first (look-ahead)");
        Check(k.TryMatch(w1, false), "then the first (pending)");
        Check(!k.TryMatch(w1, false), "a used key is not accepted again");
        Check(k.TryMatch(w1, true), "except on a retransmission");
        Check(k.TryMatch(w2, false), "the second");
        Check(!k.TryMatch(0xDEADBEEF, false), "garbage refused");
    }

    private static void AckOnlyAtNewSequenceMeansLoss()
    {
        Case("server ack-only at a newer sequence");
        var (t, _, srv, _, _) = Connected();
        srv.Seq++;                                    // server sent seq 3 (lost) ...
        t.Receive(srv.AckOnly(2, srv.Seq));           // ... then an ack-only reusing seq 3
        Check(t.Missing.SequenceEqual(new uint[] { 3 }), "3 is missing");
    }

    private static void RejectRetransmitStopsNaks()
    {
        Case("RejectRetransmit");
        var (t, link, srv, _, advance) = Connected();
        srv.Seq++;
        t.Receive(srv.Message(Msg(0x3333)));          // seq 4, 3 missing
        Check(t.Missing.SequenceEqual(new uint[] { 3 }), "3 missing");
        t.Receive(srv.Control(PacketFlags.RejectRetransmit, ++srv.Seq, 3));
        Eq(t.Missing.Count, 0, "rejected sequence forgotten");
        Eq(t.Rejected, 1L, "counted");
        Check(t.Missing.Count == 0 && t.ContiguousReceived == 5, "the reject's own sequence counts as received");
    }

    private static void LargeMessageSplits()
    {
        Case("our large message splits into fragments");
        var (t, link, _, _, _) = Connected();
        int before = link.Sent.Count;
        byte[] msg = new byte[1000];
        BinaryPrimitives.WriteUInt32LittleEndian(msg, 0xF7B1);
        t.SendMessage(msg, MessageQueue.UI);
        var sent = link.Sent.Skip(before).Select(s => s.Data).ToList();
        Eq(sent.Count, 3, "3 packets");
        var sink = new Sink();
        var r = new FragmentReassembler();
        unsafe
        {
            foreach (byte[] d in sent.AsEnumerable().Reverse())
                fixed (byte* b = d) r.ProcessDatagram(b, d.Length, 0, sink);
        }
        Check(sink.Messages.Count == 1 && sink.Messages[0].SequenceEqual(msg), "reassembles to the original");
        Check(sent.All(d => d.Length <= 20 + 464), "every datagram within 484 bytes");
    }

    // ── messages ────────────────────────────────────────────────────────────

    private static void LoginRequestLayout()
    {
        Case("LoginRequest body");
        byte[] b = LoginMessages.LoginRequest("tester", "secret", 0x01020304);
        var r = new NetReader(b);
        Check(r.String16L(out string ver) && ver == "1802", "version 1802");
        Check(r.U32(out uint rest) && rest == r.Remaining, "rest-length = bytes after it");
        Check(r.U32(out uint auth) && auth == 2, "AccountPassword");
        Check(r.U32(out uint flags) && flags == 0, "flags 0");
        Check(r.U32(out uint ts) && ts == 0x01020304, "timestamp");
        Check(r.String16L(out string acct) && acct == "tester", "account");
        Check(r.String16L(out string loginAs) && loginAs == "", "login-as empty");
        Check(r.U32(out uint pwLen) && pwLen == 7 && r.U8(out byte n) && n == 6, "String32L header");
        Check(b.AsSpan(r.Position, 6).SequenceEqual("secret"u8), "password bytes");
    }

    private static void CharacterListParse()
    {
        Case("CharacterList");
        var w = new WireWriter();
        w.U32(0).U32(2);
        w.U32(0x50000001).String16L("Alpha").U32(0);
        w.U32(0x50000002).String16L("+Beta").U32(3600);
        w.U32(0).U32(11).String16L("tester").U32(1).U32(1);
        Check(LoginMessages.TryParseCharacterList(w.Span, out CharacterListData l), "parses");
        Eq(l.Characters.Count, 2, "two characters");
        Check(l.Characters[1].Name == "+Beta" && l.Characters[1].DeleteSeconds == 3600, "second");
        Check(l.Account == "tester" && l.Slots == 11 && l.TurbineChat, "trailer");
        Check(!LoginMessages.TryParseCharacterList(w.Span.Slice(0, 20), out _), "truncated refused");
    }

    private static void ObjectCreateParse()
    {
        Case("ObjectCreate");
        var w = new WireWriter();
        w.U32(0x80001234);
        // ModelData: 1 palette (+ base), 1 texture, 1 anim part.
        w.U8(0x11).U8(1).U8(1).U8(1);
        w.PackedU32(0x7E).PackedU32(0x0F7E).U8(0).U8(8);
        w.U8(2).PackedU32(0x1234).PackedU32(0x12345);
        w.U8(3).PackedU32(0x0ABC);
        w.Align4();
        // PhysicsData: Movement (8 bytes + autonomous), Position, CSetup, MTable, Parent, Children, ObjScale, Velocity.
        uint pflags = 0x10000 | 0x8000 | 0x1 | 0x2 | 0x20 | 0x40 | 0x80 | 0x4;
        w.U32(pflags).U32(0x00000408);
        w.U32(8).U32(1).U32(2).U32(0);
        w.U32(0xA9B40029).F32(10).F32(20).F32(30).F32(1).F32(0).F32(0).F32(0);
        w.U32(0x09000001);                      // MTable
        w.U32(0x02000001);                      // CSetup
        w.U32(0x50000001).U32(1);               // Parent
        w.U32(1).U32(0x80000009).U32(2);        // Children
        w.F32(1.5f);                            // ObjScale
        w.F32(0).F32(0).F32(0);                 // Velocity
        for (int i = 0; i < 9; i++) w.U16((ushort)(i + 1));
        w.Align4();
        // Weenie header
        w.U32(0).String16L("Drudge Skulker").PackedU32(0x8F12).PackedU32(0x1A2B).U32(0x10).U32(0).Align4();

        Check(ObjectCreate.TryParse(w.Span, out ObjectCreateData o), "parses");
        Eq(o.Id, 0x80001234u, "guid");
        Eq(o.Name, "Drudge Skulker", "name");
        Eq(o.WeenieClassId, 0x8F12u, "wcid (packed, large)");
        Eq(o.Icon, 0x06001A2Bu, "icon gets its type prefix");
        Eq(o.ItemType, 0x10u, "item type");
        Check(o.Position is { Cell: 0xA9B40029, X: 10, Y: 20, Z: 30 }, "position");
        Eq(o.SetupId, 0x02000001u, "setup");
        Eq(o.ParentId, 0x50000001u, "parent");
        Check(o.Sequences.Length == 9 && o.Sequences[8] == 9, "nine sequences");

        Check(!ObjectCreate.TryParse(w.Span.Slice(0, w.Length - 20), out _), "truncated refused");
    }


    // ── captured live session ───────────────────────────────────────────────

    private sealed class NullLink : IDatagramLink
    {
        public void Send(ReadOnlySpan<byte> d, bool connectPort) { }
        public bool TryReceive(out byte[] d) { d = Array.Empty<byte>(); return false; }
    }

    /// <summary>
    /// A session captured from ACE: the server's datagrams go through our transport (every
    /// checksum and ISAAC key must verify, no gaps, every message must parse) and our own
    /// datagrams are checked against the sequence and key rules ACE accepted them under.
    /// </summary>
    private static void CapturedAceSession()
    {
        Case("captured ACE session");
        string path = Path.Combine(AppContext.BaseDirectory, "Captures", "ace-login-session.txt");
        if (!HaveCapture(path)) return;

        var server = new List<byte[]>();
        var client = new List<byte[]>();
        foreach (string line in File.ReadLines(path))
        {
            if (line.StartsWith('#') || line.Length == 0) continue;
            string[] f = line.Split(' ');
            (f[1] == "S" ? server : client).Add(Convert.FromHexString(f[2]));
        }
        Check(server.Count > 100 && client.Count > 10, $"capture has both directions ({server.Count} S, {client.Count} C)");

        long now = 0;
        var t = new Transport(new NullLink(), () => now);
        foreach (byte[] d in server) { now += 5; t.Receive(d); }
        Check(t.Connected && t.ConnectData != null, "ConnectRequest read");
        Eq(t.BadPackets, 0L, "every server datagram parses");
        Eq(t.BadChecksums, 0L, "every server checksum verifies (plain + ISAAC ServerSeed)");
        Eq(t.Missing.Count, 0, "no sequence gaps");

        var ops = new Dictionary<uint, int>();
        int created = 0, createdOk = 0;
        bool sawList = false, sawSpeech = false, sawLogoff = false;
        while (t.TryDequeue(out byte[] m))
        {
            uint op = BinaryPrimitives.ReadUInt32LittleEndian(m);
            ops[op] = ops.GetValueOrDefault(op) + 1;
            ReadOnlySpan<byte> body = m.AsSpan(4);
            switch (op)
            {
                case Opcode.CharacterList:
                    sawList |= LoginMessages.TryParseCharacterList(body, out CharacterListData l)
                               && l.Characters.Count > 0 && l.Characters.All(c => c.Name.Length > 1) && l.Account.Length > 0;
                    break;
                case Opcode.ObjectCreate:
                    created++;
                    if (ObjectCreate.TryParse(body, out ObjectCreateData o) && o.Name.Length > 0) createdOk++;
                    break;
                case Opcode.HearSpeech:
                    sawSpeech |= HearSpeech.TryParse(body, out HearSpeech hs) && hs.Text == "capture run";
                    break;
                case Opcode.CharacterLogOff:
                    sawLogoff = true;
                    break;
            }
        }
        Check(sawList, "CharacterList parses (character names, account name)");
        Check(created > 100, $"many CreateObjects ({created})");
        Eq(createdOk, created, "every CreateObject parses to a name");
        Check(sawSpeech, "our own speech came back as HearSpeech");
        Check(sawLogoff, "logoff confirmed");
        Check(ops.ContainsKey(Opcode.PlayerCreate) && ops.ContainsKey(Opcode.EnterWorldServerReady) && ops.ContainsKey(Opcode.DddInterrogation), "login landmarks present");

        // Our side, as ACE accepted it.
        ConnectRequestData c = t.ConnectData!;
        var keys = new InboundKeys(c.ClientSeed);
        uint last = 1;
        int encrypted = 0, keyed = 0, seqOk = 0, plainOk = 0, plain = 0;
        foreach (byte[] d in client)
        {
            if (!InboundPacket.TryParse(d, out InboundPacket p) || !Packet.TryPayloadHash(d, p.OptionalEnd, out uint ph)) continue;
            uint h = Packet.HeaderHash(d);
            if (p.Encrypted)
            {
                encrypted++;
                if (keys.TryMatch((p.Checksum - h) ^ ph, p.Has(PacketFlags.Retransmission))) keyed++;
                if (p.Sequence == last + 1) seqOk++;
                last = p.Sequence;
            }
            else
            {
                plain++;
                if (h + ph == p.Checksum) plainOk++;
            }
        }
        Check(InboundPacket.TryParse(client[0], out InboundPacket cr) && cr.Flags == PacketFlags.ConnectResponse && cr.Sequence == 1, "first of ours: ConnectResponse seq 1");
        Check(encrypted > 5, $"our encrypted packets ({encrypted})");
        Eq(keyed, encrypted, "each used the next ClientSeed word");
        Eq(seqOk, encrypted, "sequences 2, 3, 4 ... with no holes");
        Eq(plainOk, plain, "plain checksums valid");
    }


    /// <summary>The captured session through the world model: PlayerDescription, inventory, vitals, skills.</summary>
    private static void CapturedWorldModel()
    {
        Case("captured ACE session: world model");
        string path = Path.Combine(AppContext.BaseDirectory, "Captures", "ace-login-session.txt");
        if (!HaveCapture(path)) return;
        long now = 0;
        var t = new Transport(new NullLink(), () => now);
        foreach (string line in File.ReadLines(path))
        {
            if (line.StartsWith('#') || line.Length == 0) continue;
            string[] f = line.Split(' ');
            if (f[1] == "S") { now += 5; t.Receive(Convert.FromHexString(f[2])); }
        }
        var world = new WorldState(() => now);
        byte[]? pdBody = null;
        string listed = "";
        while (t.TryDequeue(out byte[] m))
        {
            if (!ServerMessage.TryFromWire(m, out ServerMessage msg)) continue;
            if (msg.IsGameEvent && msg.EventType == PlayerDescription.EventType) pdBody = msg.EventBody.ToArray();
            if (msg.Opcode == Opcode.CharacterList && LoginMessages.TryParseCharacterList(msg.Body, out CharacterListData cl))
                listed = cl.Characters.FirstOrDefault(c => c.Id == 0x50000001u)?.Name ?? listed;
            world.Apply(msg);
        }
        PlayerState p = world.Player;
        Check(pdBody != null && PlayerDescription.TryParse(pdBody, out _), "PlayerDescription parses");
        Check(p.DescriptionReceived, "description applied");
        Eq(p.Id, 0x50000001u, "player id");
        Eq(p.AttributeTable.Count, 6, "six attributes");
        Check(p.AttributeTable.Values.All(a => a.Base >= 10), "attributes look sane (>= 10)");
        Check(p.CurrentVital(Vital.Health) > 0 && p.CurrentVital(Vital.Health) <= p.BaseMaxVital(Vital.Health) + 500, $"health {p.CurrentVital(Vital.Health)}/{p.BaseMaxVital(Vital.Health)}");
        Check(p.SkillTable.Count > 20, $"skills ({p.SkillTable.Count})");
        Check(p.SkillTable.Values.All(s => s.Training <= 3), "skill training classes valid");
        Check(p.Description!.InventoryFound, "inventory lists located");
        WorldObject? me = world.PlayerObject;
        Check(listed.Length > 1 && me != null && me.TryGetString(1, out string name) && name.TrimStart('+') == listed.TrimStart('+'),
              "player name property (the character list's name)");

        // Every main-pack entry from PlayerDescription is an object the server created in our pack.
        var mainPack = p.MainPackOrder.Concat(p.SidePackOrder).ToList();
        Check(mainPack.Count > 5, $"main pack entries ({mainPack.Count})");
        int inPack = mainPack.Count(id => world.Get(id) is { } o && o.ContainerId == p.Id);
        Eq(inPack, mainPack.Count, "main pack list matches CreateObject containers");
        int created = world.Objects.Values.Count(o => o.ContainerId == p.Id);
        Eq(created, mainPack.Count, "and nothing in the pack is missing from the list");
        int wielded = p.EquippedItems.Keys.Count(id => world.Get(id) is { } o && o.WielderId == p.Id);
        Eq(wielded, p.EquippedItems.Count, "equipped list matches CreateObject wielders");
        Check(world.Inventory().Count() >= mainPack.Count, $"inventory incl. side packs ({world.Inventory().Count()})");
        Console.WriteLine($"   info: {p.SkillTable.Count} skills, {p.SpellBook.Count} spells, {p.ActiveEnchantments.Count} enchantments, " +
                          $"{mainPack.Count} pack entries, {p.EquippedItems.Count} equipped, {world.Objects.Count} objects, options 0x{p.Description.OptionFlags:X}");
    }


    /// <summary>A P1 scenario session (teleport, identify, use, cast, pickup with a walk, give, drop, melee kill, teleport back).</summary>
    private static void CapturedP1Session()
    {
        Case("captured ACE P1 session");
        string path = Path.Combine(AppContext.BaseDirectory, "Captures", "ace-p1-session.txt");
        if (!HaveCapture(path)) return;
        long now = 0;
        var t = new Transport(new NullLink(), () => now);
        var client = new List<byte[]>();
        foreach (string line in File.ReadLines(path))
        {
            if (line.StartsWith('#') || line.Length == 0) continue;
            string[] f = line.Split(' ');
            if (f[1] == "S") { now += 5; t.Receive(Convert.FromHexString(f[2])); }
            else client.Add(Convert.FromHexString(f[2]));
        }
        Eq(t.BadChecksums, 0L, "server checksums");
        Eq(t.Missing.Count, 0, "no gaps");

        var w = new WorldState(() => now);
        var useDone = new List<uint>();
        var enchant = new List<ushort>();
        var combat = new List<CombatNotice>();
        var moveTo = new List<MotionUpdate>();
        var identified = new List<(uint, bool)>();
        var cells = new List<uint>();
        uint pyreal = 0;
        bool pyrealInPack = false, pyrealDropped = false;
        w.UseDone += useDone.Add;
        w.EnchantmentAdded += e => enchant.Add(e.SpellId);
        w.Combat += combat.Add;
        w.PlayerMoveTo += moveTo.Add;
        w.Identified += (id, ok) => identified.Add((id, ok));
        w.ObjectCreated += o => { if (o.WeenieClassId == 273 && o.ContainerId == 0 && o.Position != null) pyreal = o.Id; };
        w.ObjectUpdated += o =>
        {
            if (o.Id != pyreal || pyreal == 0) return;
            if (o.ContainerId == w.Player.Id) pyrealInPack = true;
            else if (pyrealInPack && o.ContainerId == 0) pyrealDropped = true;
        };
        while (t.TryDequeue(out byte[] m))
        {
            if (!ServerMessage.TryFromWire(m, out ServerMessage msg)) continue;
            w.Apply(msg);
            if (w.PlayerObject?.Position is { } p && (cells.Count == 0 || cells[^1] >> 16 != p.Cell >> 16)) cells.Add(p.Cell);
        }
        Check(cells.Count >= 3 && cells[0] >> 16 == 0x6146 && cells.Any(c => c >> 16 == 0xA9B4) && cells[^1] >> 16 == 0x6146,
            $"teleported out and back ({string.Join(" -> ", cells.Select(c => c.ToString("X8")))})");
        Check(identified.Any(x => x.Item2), "an identify succeeded");
        Check(useDone.Count >= 2 && useDone.All(e => e == 0), $"UseDone results ({string.Join(",", useDone.Select(e => e.ToString("X")))})");
        Check(enchant.Contains(24), "Armor Self I (24) applied");
        Check(pyreal != 0, "the pyreal appeared on the ground");
        Check(moveTo.Any(m => m.TargetId == pyreal && m.MovementType == 6 && m.DistanceToObject > 0), "server asked to move to it (MoveToObject + distance)");
        Check(pyrealInPack, "pyreal went into the pack");
        Check(pyrealDropped, "and was dropped again");
        Check(combat.Any(c => c.EventType == 0x01AD && c.Name.Contains("Drudge Skulker")), "killer notification for the drudge");
        Eq(w.Player.MainPackOrder.Count + w.Player.SidePackOrder.Count, 54, "pack back to 54 entries");

        // Our side: the AutonomousPosition reports walked toward the pyreal.
        int positions = 0;
        foreach (byte[] d in client)
        {
            if (!InboundPacket.TryParse(d, out InboundPacket ip) || !ip.Has(PacketFlags.BlobFragments)) continue;
            int at = ip.OptionalEnd + 16;
            if (d.Length >= at + 12 && BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at)) == 0xF7B1
                && BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at + 8)) == ActionType.AutonomousPosition)
                positions++;
        }
        Check(positions >= 2, $"we reported our position while walking ({positions})");
    }

    private static void UpdatePositionLayouts()
    {
        Case("UpdatePosition flags");
        // Grounded, no X/Y (0x10|0x20): qw and qz are sent; velocity (0x01) and placement (0x02) present.
        var w = new WireWriter();
        w.U32(0x80000001).U32(0x01 | 0x02 | 0x04 | 0x10 | 0x20).U32(0xA9B40029).F32(1).F32(2).F32(3);
        w.F32(0.5f);                     // qw (0x08 clear)
        w.F32(0.25f);                    // qz (0x40 clear)
        w.F32(4).F32(5).F32(6);          // velocity
        w.U32(65);                       // placement
        w.U16(7).U16(8).U16(9).U16(10);
        Check(WorldMessages.TryUpdatePosition(w.Span, out PositionUpdate u), "parses");
        Check(u.Position is { Cell: 0xA9B40029, X: 1, Y: 2, Z: 3, Qw: 0.5f, Qx: 0, Qy: 0, Qz: 0.25f }, "position + partial rotation");
        Check(u.Vx == 4 && u.Vz == 6 && u.Grounded, "velocity, grounded");
        Check(u.InstanceSeq == 7 && u.ForcePositionSeq == 10, "sequences");
        Check(!WorldMessages.TryUpdatePosition(w.Span.Slice(0, w.Length - 2), out _), "truncated refused");
    }

    private static void VitalIdMapping()
    {
        Case("vital updates");
        var full = new WireWriter().U8(3).U32(5).U32(10).U32(20).U32(30).U32(99).ToArray();   // MaxMana = 5
        Check(WorldMessages.TryPrivateVital(full, out uint v, out VitalInfo vi) && v == 3 && vi.Current == 99 && vi.Ranks == 10, "0x02E7: 5 (MaxMana) -> Mana");
        var cur = new WireWriter().U8(1).U32(2).U32(1234).ToArray();                            // Health = 2
        Check(WorldMessages.TryPrivateVitalCurrent(cur, out uint c, out uint cv) && c == 1 && cv == 1234, "0x02E9: 2 (Health) -> Health");
        var skill = new WireWriter().U8(0).U32(6).U16(50).U16(1).U32(3).U32(1000).U32(10).U32(0).F64(0).ToArray();
        Check(WorldMessages.TryPrivateSkill(skill, out SkillInfo s) && s.Id == 6 && s.Ranks == 50 && s.Training == 3, "0x02DD skill");
    }

    private static void WeenieHeaderAllFields()
    {
        Case("weenie header, every optional field");
        uint f = 0xFFFFFFFF;           // every flag
        var w = new WireWriter();
        w.U32(0x80000077);
        w.U8(0x11).U8(0).U8(0).U8(0);
        w.U32(0).U32(0);               // physics: no flags
        for (int i = 0; i < 9; i++) w.U16(0);
        w.Align4();
        w.U32(f).String16L("Thing").PackedU32(0x9999).PackedU32(0x1234).U32(0x80).U32(0x04000000);
        w.Align4();
        w.U32(0xF);                    // flags2: underlay, cooldown, cooldown duration, pet owner
        w.String16L("Things").U8(24).U8(7).U16(3).I32(500).U32(0x8).F32(1.5f).U32(0x10).U32(0x40).U8(2)
         .U16(9).U16(10).U16(25).U16(100).U32(0x50000001).U32(0x50000002).U32(0x1000000).U32(0x1000000).U32(0x20)
         .U8(5).U8(6).U16(0x55).F32(7.5f).U16(150).U16(2001).U32(0x50000003)
         .U32(0x10000002).U32(1).U32(0x50000004).U16(1).U16(32).U32(0x50000005).U32(0x77)   // restrictions: 1 entry
         .U32(0x200).U32(0x50000006).U16(3).PackedU32(0x0ABC).PackedU32(0x0DEF).U32(17).I32(42).F64(30).U32(0x50000007);
        w.Align4();
        Check(ObjectCreate.TryParse(w.Span, out ObjectCreateData o), "parses");
        Check(o.Name == "Thing" && o.PluralName == "Things" && o.ItemsCapacity == 24 && o.ContainersCapacity == 7, "names, capacities");
        Check(o.AmmoType == 3 && o.Value == 500 && o.Usable == 8 && o.UseRadius == 1.5f && o.TargetType == 0x10 && o.UiEffects == 0x40, "value..ui effects");
        Check(o.CombatUse == 2 && o.Structure == 9 && o.MaxStructure == 10 && o.StackSize == 25 && o.MaxStackSize == 100, "structure, stack");
        Check(o.ContainerId == 0x50000001 && o.WielderId == 0x50000002 && o.ValidLocations == 0x1000000 && o.CurrentWieldedLocation == 0x1000000 && o.Priority == 0x20, "container/wielder/locations");
        Check(o.RadarColor == 5 && o.RadarBehavior == 6 && o.Script == 0x55 && o.Workmanship == 7.5f && o.Burden == 150 && o.Spell == 2001, "radar..spell");
        Check(o.HouseOwner == 0x50000003 && o.HookItemTypes == 0x200 && o.Monarch == 0x50000006 && o.HookType == 3, "house, hooks, monarch");
        Check(o.IconOverlay == 0x06000ABC && o.IconUnderlay == 0x06000DEF && o.MaterialType == 17, "icons, material");
        Check(o.CooldownId == 42 && o.CooldownDuration == 30 && o.PetOwner == 0x50000007, "flags2 fields");
        Check(o.Icon == 0x06001234 && o.WeenieClassId == 0x9999, "wcid, icon");
    }

    private static void PlayerDescriptionWithBlob()
    {
        Case("PlayerDescription: lists after a gameplay-options blob");
        var w = new WireWriter();
        w.U32(0x01).U32(1);                                  // int table only
        w.U16(1).U16(64).U32(25).I32(275);                   // level 275
        w.U32(0x001 | 0x002).U32(1);                         // attributes + skills
        w.U32(0x1FF);
        for (int i = 0; i < 6; i++) w.U32(100).U32(50).U32(0);
        for (int i = 0; i < 3; i++) w.U32(10).U32(0).U32(0).U32(500);
        w.U16(1).U16(32).U32(6).U16(40).U16(1).U32(2).U32(0).U32(10).U32(0).F64(0);
        w.U32(0x200 | 0x400 | 0x40 | 0x20).I32(0x12345);     // gameplay options, 8 bars, options2, filters
        for (int b = 0; b < 8; b++) w.U32(0);
        w.U32(0x3FFF).I32(7);
        w.Bytes(new byte[] { 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0 });   // blob full of small numbers and zeros
        w.U32(2).U32(0x80000010).U32(0).U32(0x80000011).U32(1);                     // inventory: item, then pack
        w.U32(1).U32(0x80000020).U32(0x200000).U32(0);                              // equipped: shield
        Check(PlayerDescription.TryParse(w.Span, out PlayerDescriptionData pd), "parses");
        Eq(pd.Ints.GetValueOrDefault(25u), 275, "level");
        Check(pd.Attributes.Count == 6 && pd.Attributes[1].Base == 150, "attributes");
        Check(pd.Vitals.Count == 3 && pd.Vitals[1].Current == 500, "vitals");
        Check(pd.Skills.Count == 1 && pd.Skills[6].Ranks == 40, "skills");
        Check(pd.InventoryFound, "lists found from the end");
        Check(pd.Inventory.Count == 2 && pd.Inventory[0].Id == 0x80000010 && pd.Inventory[1].ContainerType == 1, "inventory list");
        Check(pd.Equipped.Count == 1 && pd.Equipped[0].Location == 0x200000, "equipped list");
    }

    private static void RelocateCells()
    {
        Case("outdoor cell from position");
        var p = new WorldPosition(0xA9B40029, 133.6f, 17.4f, 94, 1, 0, 0, 0);
        Eq(ClientSession.Relocate(p, 133.6f, 17.4f).Cell, 0xA9B40029u, "same spot keeps its cell (x/24=5, y/24=0 -> 0x29)");
        Eq(ClientSession.Relocate(p, 10f, 30f).Cell, 0xA9B40002u, "cell 0x02");
        Eq(ClientSession.Relocate(p, 195f, 5f).Cell, 0xAAB40001u, "east edge -> next landblock x");
        Eq(ClientSession.Relocate(p, 5f, -2f).Cell, 0xA9B30008u, "south edge -> previous landblock y, top row");
        var indoor = new WorldPosition(0x61460249, 90, -150, -12, 1, 0, 0, 0);
        Eq(ClientSession.Relocate(indoor, 95, -155).Cell, 0x61460249u, "indoor keeps its cell");
    }

    private static void InventoryEvents()
    {
        Case("inventory events update the model");
        long now = 0;
        var w = new WorldState(() => now);
        void Apply(byte[] b) { if (ServerMessage.TryFromWire(b, out ServerMessage m)) w.Apply(m); }
        byte[] Ev(uint type, WireWriter body) => new WireWriter().U32(0xF7B0).U32(0x50000001).U32(1).U32(type).Bytes(body.Span).ToArray();
        Apply(new WireWriter().U32(0xF746).U32(0x50000001).ToArray());
        Apply(Ev(0x0022, new WireWriter().U32(0x80000001).U32(0x50000001).I32(0).U32(0)));
        Apply(Ev(0x0022, new WireWriter().U32(0x80000002).U32(0x50000001).I32(0).U32(0)));
        Check(w.Player.MainPackOrder.SequenceEqual(new uint[] { 0x80000002, 0x80000001 }), "placement 0 inserts at the front");
        Apply(Ev(0x0023, new WireWriter().U32(0x80000001).I32(0x100000)));
        Check(w.Player.EquippedItems.ContainsKey(0x80000001) && !w.Player.MainPackOrder.Contains(0x80000001), "wield moves it out of the pack");
        Check(w.Get(0x80000001)!.WielderId == 0x50000001, "wielder set");
        Apply(Ev(0x019A, new WireWriter().U32(0x80000002)));
        Check(w.Player.MainPackOrder.Count == 0 && w.Get(0x80000002)!.ContainerId == 0, "dropped to 3D");
        Apply(new WireWriter().U32(0x0197).U8(1).U32(0x80000002).U32(25).U32(250).ToArray());
        Check(w.Get(0x80000002)!.StackSize == 25 && w.Get(0x80000002)!.Value == 250, "SetStackSize");
        Apply(new WireWriter().U32(0xF747).U32(0x80000002).U16(0).U16(0).ToArray());
        Check(w.Get(0x80000002) == null, "deleted");
        byte[] ench = new WireWriter().U16(24).U16(2).U16(1).U16(1).U32(10).F64(0).F64(1800).U32(0x50000001).F32(0).F32(0).F64(0)
            .U32(0x2008010).U32(28).F32(20).U32(0).ToArray();
        Apply(Ev(0x02C2, new WireWriter().Bytes(ench)));
        Check(w.Player.ActiveEnchantments.Any(e => e.SpellId == 24 && e.Duration == 1800 && e.StatModValue == 20), "enchantment added");
        Apply(Ev(0x02C3, new WireWriter().U16(24).U16(2)));
        Check(!w.Player.ActiveEnchantments.Any(e => e.SpellId == 24), "enchantment removed");
    }


    /// <summary>
    /// MoveToState / RawMotionState packing. The WalkForward bytes are the RawMotionState vector in
    /// OpenAC's tests (tests/AcDream.Core.Net.Tests/Messages/RawMotionStatePackTests.cs, MIT).
    /// </summary>
    private static void MotionPacking()
    {
        Case("MoveToState packing");
        var w = new WireWriter();
        MotionMessages.WriteRawMotion(w, RawMotion.Stop);
        Bytes(w.ToArray(), "00 00 00 00", "stop = no fields (Ready, NonCombat, speed 1)");
        w = new WireWriter();
        MotionMessages.WriteRawMotion(w, RawMotion.WalkForward);
        Bytes(w.ToArray(), "0C 00 00 00 05 00 00 45 01 00 00 00", "walk forward (OpenAC vector)");
        w = new WireWriter();
        MotionMessages.WriteRawMotion(w, RawMotion.RunForward);
        Bytes(w.ToArray(), "0D 00 00 00 02 00 00 00 05 00 00 45 02 00 00 00", "run = WalkForward held with Run");

        var p = new WorldPosition(0xA9B40029, 1, 2, 3, 1, 0, 0, 0);
        byte[] mts = MotionMessages.MoveToState(RawMotion.RunForward, p, 1, 2, 3, 4, true);
        Eq(mts.Length % 4, 0, "padded to 4");
        Eq(BinaryPrimitives.ReadUInt32LittleEndian(mts.AsSpan(16)), 0xA9B40029u, "position after the motion state");
        Eq(BinaryPrimitives.ReadUInt16LittleEndian(mts.AsSpan(48)), (ushort)1, "instance sequence");
        Eq(mts[56], (byte)1, "contact byte");
        Eq(MotionMessages.Jump(0.5f, 0, 0, 5, 1, 2, 3, 4).Length, 32, "jump: 24 bytes + object + spell");

        // The server's echo: UpdateMotion header, type 0, then forward RunForward at the run rate.
        var um = new WireWriter().U32(0x50000001).U16(1).U16(2).U16(3).U8(0).U8(0).U8(0).U8(0).U16(0x3D);
        um.U32(0x02 | 0x04).U16(0x0007).F32(2.75f).Align4();
        Check(MotionMessages.TryInterpreted(um.Span, out InterpretedMotion im) && im.ForwardCommand == MotionCommand.RunForward && im.ForwardSpeed == 2.75f,
              "interpreted state: RunForward at the run rate");
        Check(im.Style == MotionCommand.NonCombat, "stance from the header");

        // Headings: 0 = north (+Y), 90 = east; AC rotates about Z with forward = +Y.
        var north = ClientSession.WithHeading(p, 0);
        Check(MathF.Abs(north.Qw - 1) < 1e-5 && MathF.Abs(north.Qz) < 1e-5, "north = identity");
        var south = ClientSession.WithHeading(p, MathF.PI);
        Check(MathF.Abs(south.Qw) < 1e-5 && MathF.Abs(MathF.Abs(south.Qz) - 1) < 1e-5, "south = (0,0,0,±1), as ACE logs a creature facing south");
        var east = ClientSession.WithHeading(p, MathF.PI / 2);
        // Rotate +Y by the quaternion: x' = -2*qw*qz (for a pure Z rotation), should be +1 (east).
        Check(MathF.Abs(-2 * east.Qw * east.Qz - 1) < 1e-4, "east turns +Y to +X");
        (float dx, float dy) = ClientSession.Delta(new WorldPosition(0xA9B40001, 190, 10, 0, 1, 0, 0, 0), new WorldPosition(0xAAB40001, 2, 10, 0, 1, 0, 0, 0));
        Check(MathF.Abs(dx - 4) < 1e-4 && dy == 0, "delta across a landblock edge");
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static byte[] Msg(uint opcode) => new WireWriter().U32(opcode).U32(0).ToArray();

    private static List<uint> Drain(Transport t)
    {
        var l = new List<uint>();
        while (t.TryDequeue(out byte[] m)) l.Add(BinaryPrimitives.ReadUInt32LittleEndian(m));
        return l;
    }

    private sealed unsafe class Sink : IServerMessageSink
    {
        public readonly List<byte[]> Messages = new();
        public void OnServerMessage(uint fragmentSequence, byte* message, int length)
            => Messages.Add(new ReadOnlySpan<byte>(message, length).ToArray());
    }

    private static void Case(string name)
    {
        _case = name;
        Console.WriteLine($"-- {name}");
    }

    /// <summary>
    /// The captured session at <paramref name="path"/>, or false (said, not a failure) when it isn't there:
    /// the public source snapshots leave Captures\ out, because the captures hold a live login.
    /// </summary>
    private static bool HaveCapture(string path)
    {
        if (File.Exists(path)) return true;
        Console.WriteLine($"   SKIPPED [{_case}] no captured session at {path} (the public source leaves the captures out)");
        return false;
    }

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (ok) return;
        _failed++;
        Console.WriteLine($"   FAIL [{_case}] {what}");
    }

    private static void Eq<T>(T actual, T expected, string what)
    {
        _checks++;
        if (EqualityComparer<T>.Default.Equals(actual, expected)) return;
        _failed++;
        Console.WriteLine($"   FAIL [{_case}] {what}: got {Fmt(actual)}, expected {Fmt(expected)}");
    }

    private static string Fmt<T>(T v) => v is uint u ? $"0x{u:X8}" : v?.ToString() ?? "null";

    private static void Bytes(byte[] actual, string hex, string what)
    {
        byte[] expected = hex.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(h => Convert.ToByte(h, 16)).ToArray();
        _checks++;
        if (actual.SequenceEqual(expected)) return;
        _failed++;
        Console.WriteLine($"   FAIL [{_case}] {what}: got {Convert.ToHexString(actual)}, expected {Convert.ToHexString(expected)}");
    }
}
