// ============================================================================
//  HeadlessSessionTests - ClientSession against a scripted, in-process fake server.
//  The fake server follows the protocol as documented in docs\HEADLESS.md: ConnectRequest,
//  CharacterList/ServerName/DDD, ServerReady, PlayerCreate + CreateObjects, speech,
//  logout. It checks every client packet (sequence rules, checksums, the client's ISAAC
//  keys) and can drop chosen packets in either direction to exercise NAKs and resends.
// ============================================================================

using System.Buffers.Binary;
using RynthCore.Engine.Net;
using RynthCore.Headless.Messages;
using RynthCore.Headless.Net;
using RynthCore.Headless.Session;
using RynthCore.PluginSdk.Net;

namespace HeadlessSessionTests;

internal static class Program
{
    private static int _checks, _failed;
    private static string _case = "";

    private static int Main()
    {
        FullSession(dropServer: Array.Empty<uint>(), dropClient: Array.Empty<uint>(), "clean session");
        FullSession(dropServer: new uint[] { 3, 6 }, dropClient: Array.Empty<uint>(), "server packets 3 and 6 lost once");
        FullSession(dropServer: Array.Empty<uint>(), dropClient: new uint[] { 3 }, "client packet 3 lost once");
        Walking();
        WrongCharacter();
        CharacterError();
        ServerGoesSilent();

        Console.WriteLine();
        Console.WriteLine($"{_checks - _failed}/{_checks} checks passed.");
        Console.WriteLine(_failed == 0 ? "ALL HEADLESS SESSION TESTS PASSED." : $"{_failed} FAILED.");
        return _failed == 0 ? 0 : 1;
    }

    private const uint Player = 0x50000001, Drudge = 0x80000042;
    private const string Account = "tester";

    // ── scenarios ───────────────────────────────────────────────────────────

    private static void FullSession(uint[] dropServer, uint[] dropClient, string name)
    {
        Case(name);
        var h = new Harness(dropServer, dropClient);
        var chat = new List<ChatLine>();
        var objects = new List<ObjectSeen>();
        h.Session.Chat += chat.Add;
        h.Session.ObjectCreated += objects.Add;

        h.Session.Start("hunter2");
        h.RunUntil(() => h.Session.State == SessionState.InWorld && h.Server.LoginCompletes > 0, 10_000);
        Eq(h.Session.State, SessionState.InWorld, "in world");
        Eq(h.Session.PlayerId, Player, "player id");
        Check(h.Server.EnterWorldAccount == Account && h.Server.EnterWorldGuid == Player, "enter-world echoes guid + server account name");
        Check(h.Server.LoginCompletes == 1, "LoginComplete once (our object came before PlayerCreate)");
        Check(h.Server.DddAnswered, "DDD answered");
        Check(objects.Any(o => o.Id == Drudge && o.Name == "Drudge Skulker"), "the drudge appeared");
        Check(chat.Any(c => c.Text == "Welcome to Testworld" && c.ChatType == 0x05), "server message");

        h.Session.Say("hello world");
        h.RunUntil(() => chat.Any(c => c.Text == "hello world"), 5_000);
        Check(h.Server.Said.Contains("hello world"), "server heard Talk");
        Check(chat.Any(c => c.Text == "hello world" && c.Sender == "Alpha" && c.ChatType == 0x02), "own speech echoed back");

        h.Run(3_000);                                // a few seconds of acks/keepalives
        h.Session.Logout();
        h.RunUntil(() => h.Session.Finished, 15_000);
        Eq(h.Session.State, SessionState.Closed, "closed");
        Check(h.Session.FailReason == null, "no failure");
        Check(h.Server.LogoffSeen && h.Server.DisconnectSeen, "server saw LogOff then Disconnect");

        Eq(h.Server.ClientBadKeys, 0, "every client key matched the ClientSeed stream");
        Eq(h.Server.ClientBadPlain, 0, "every plain client checksum valid");
        Eq(h.Server.ClientSeqErrors, 0, "client sequence rules");
        Eq(h.Session.Transport.BadChecksums, 0L, "client verified every server checksum");
        Eq(h.Session.Transport.Missing.Count, 0, "client has no gaps left");
        if (dropServer.Length > 0)
            Check(h.Server.NaksReceived > 0 && h.Server.Resent >= dropServer.Length, $"client NAKed the lost server packets (naks {h.Server.NaksReceived}, resent {h.Server.Resent})");
        if (dropClient.Length > 0)
            Check(h.Session.Transport.Resends >= dropClient.Length, "client resent its lost packet");
    }

    private static void WrongCharacter()
    {
        Case("unknown character name");
        var h = new Harness(Array.Empty<uint>(), Array.Empty<uint>(), character: "Nobody");
        h.Session.Start("pw");
        h.RunUntil(() => h.Session.Finished, 5_000);
        Eq(h.Session.State, SessionState.Failed, "failed");
        Check(h.Session.FailReason?.Contains("Nobody") == true, "says which name");
        Check(h.Server.DisconnectSeen, "sent Disconnect");
    }

    private static void CharacterError()
    {
        Case("server refuses enter world");
        var h = new Harness(Array.Empty<uint>(), Array.Empty<uint>()) { };
        h.Server.RefuseEnterWorld = true;
        h.Session.Start("pw");
        h.RunUntil(() => h.Session.Finished, 5_000);
        Eq(h.Session.State, SessionState.Failed, "failed");
        Check(h.Session.FailReason?.Contains("0xD") == true, "character error code reported");
    }

    private static void ServerGoesSilent()
    {
        Case("server goes silent");
        var h = new Harness(Array.Empty<uint>(), Array.Empty<uint>());
        h.Session.Start("pw");
        h.RunUntil(() => h.Session.State == SessionState.InWorld, 5_000);
        h.Server.Mute = true;
        h.RunUntil(() => h.Session.Finished, 60_000);
        Eq(h.Session.State, SessionState.Failed, "failed after silence");
    }


    private static void Walking()
    {
        Case("walking (flat walker)");
        var h = new Harness(Array.Empty<uint>(), Array.Empty<uint>());
        var done = new List<(bool, string)>();
        h.Session.MoveFinished += (ok, why) => done.Add((ok, why));
        h.Session.Start("pw");
        h.RunUntil(() => h.Session.State == SessionState.InWorld && h.Server.LoginCompletes > 0, 5_000);
        // The player was created at 0xA9B40029 (1, 2, 3); walk 20 m north (into the next land cell).
        var start = h.Session.World.PlayerObject!.Position!.Value;
        h.Session.WalkTo(start with { Y = start.Y + 20 });
        h.RunUntil(() => done.Count > 0, 15_000);
        Check(done.Count == 1 && done[0].Item1, $"arrived ({(done.Count > 0 ? done[0].Item2 : "never")})");
        Check(h.Server.Motions.FirstOrDefault() == "forward", "a MoveToState 'run forward' first");
        Check(h.Server.Motions.LastOrDefault() == "stop", "a MoveToState 'stop' last");
        Check(h.Server.Positions.Count >= 3, $"position reports while walking ({h.Server.Positions.Count})");
        var last = h.Server.Positions.LastOrDefault();
        Check(MathF.Abs(last.Y - 21.8f) < 0.1f && MathF.Abs(last.X - 1) < 0.01f, $"last report at the goal ({last.X:F2}, {last.Y:F2})");
        Check(last.Cell == 0xA9B40001, $"with the land cell recomputed (0x{last.Cell:X8})");
        // 1.0 run rate until the server echoes one: 4 m/s, so about 5 s for 20 m.
        h.Session.Logout();
        h.RunUntil(() => h.Session.Finished, 15_000);
    }

    // ── harness ─────────────────────────────────────────────────────────────

    private sealed class Harness : IDatagramLink
    {
        public long Now = 1000;
        public readonly FakeServer Server;
        public readonly ClientSession Session;
        private readonly Queue<byte[]> _toClient = new();
        private readonly HashSet<uint> _dropClient;

        public Harness(uint[] dropServer, uint[] dropClient, string character = "Alpha")
        {
            _dropClient = new HashSet<uint>(dropClient);
            Server = new FakeServer(_toClient, new HashSet<uint>(dropServer));
            Session = new ClientSession(this, new SessionOptions { Account = Account, Character = character }, () => Now);
        }

        void IDatagramLink.Send(ReadOnlySpan<byte> d, bool connectPort)
        {
            uint seq = BinaryPrimitives.ReadUInt32LittleEndian(d);
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(4));
            if ((flags & PacketFlags.Retransmission) == 0 && (flags & PacketFlags.BlobFragments) != 0 && _dropClient.Remove(seq))
                return;   // lost on the way, once
            Server.Receive(d.ToArray(), connectPort, Now);
        }

        bool IDatagramLink.TryReceive(out byte[] d) => _toClient.TryDequeue(out d!);

        public void Run(long ms)
        {
            long end = Now + ms;
            while (Now < end && !Session.Finished) Step();
        }

        public void RunUntil(Func<bool> done, long maxMs)
        {
            long end = Now + maxMs;
            while (Now < end && !done() && !Session.Finished) Step();
        }

        private void Step()
        {
            Now += 15;
            Server.Tick(Now);
            Session.Tick();
        }
    }

    private sealed unsafe class FakeServer : IServerMessageSink
    {
        private const uint ClientSeed = 0x0BADF00D, ServerSeed = 0x5EED5EED;
        private readonly Queue<byte[]> _out;
        private readonly HashSet<uint> _drop;
        private readonly Isaac _keys = new(ServerSeed);
        private readonly InboundKeys _clientKeys = new(ClientSeed);
        private readonly FragmentReassembler _reasm = new();
        private readonly Dictionary<uint, byte[]> _cache = new();
        private uint _seq = 1, _fragSeq, _lastClientSeq = 1;
        private readonly HashSet<uint> _clientMissing = new();
        private bool _connected;
        private long _lastAck, _lastHeard;

        public bool RefuseEnterWorld, Mute;
        public int LoginCompletes, NaksReceived, Resent, ClientBadKeys, ClientBadPlain, ClientSeqErrors;
        public bool DddAnswered, LogoffSeen, DisconnectSeen;
        public string? EnterWorldAccount;
        public uint EnterWorldGuid;
        public readonly List<string> Said = new();
        public readonly List<string> Motions = new();
        public readonly List<(uint Cell, float X, float Y)> Positions = new();

        public FakeServer(Queue<byte[]> output, HashSet<uint> drop)
        {
            _out = output;
            _drop = drop;
        }

        public void Receive(byte[] d, bool connectPort, long now)
        {
            _lastHeard = now;
            if (!InboundPacket.TryParse(d, out InboundPacket p)) { ClientBadPlain++; return; }
            Packet.TryPayloadHash(d, p.OptionalEnd, out uint payload);
            uint header = Packet.HeaderHash(d);
            if (p.Encrypted)
            {
                if (!_clientKeys.TryMatch((p.Checksum - header) ^ payload, p.Has(PacketFlags.Retransmission))) ClientBadKeys++;
            }
            else if (header + payload != p.Checksum)
            {
                ClientBadPlain++;
            }

            if (p.Has(PacketFlags.LoginRequest))
            {
                if (p.Sequence != 0 || connectPort) ClientSeqErrors++;
                var w = new WireWriter();
                w.F64(5000.0).U64(0xC00C1E).U32(0x0007).U32(ServerSeed).U32(ClientSeed).U32(0);
                Emit(Packet.Build(0, PacketFlags.ConnectRequest, 0x0B, 0, 1, w.Span, null, null, out _), cache: false);
                return;
            }
            if (p.Has(PacketFlags.ConnectResponse))
            {
                if (!connectPort || p.Sequence != 1 || BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(20)) != 0xC00C1E) ClientSeqErrors++;
                if (_connected) return;
                _connected = true;
                var cl = new WireWriter().U32(Opcode.CharacterList).U32(0).U32(2)
                    .U32(Player).String16L("Alpha").U32(0)
                    .U32(0x50000002).String16L("Beta").U32(0)
                    .U32(0).U32(11).String16L(Account).U32(1).U32(1);
                Send(cl.ToArray());
                Send(new WireWriter().U32(Opcode.ServerName).I32(1).I32(100).String16L("Testworld").ToArray());
                Send(new WireWriter().U32(Opcode.DddInterrogation).U32(1).U32(1).U32(1).U32(2).U32(0).U32(1).ToArray());
                return;
            }
            if (connectPort) { ClientSeqErrors++; return; }
            if (p.Id != 0x0007) ClientSeqErrors++;

            if (p.Has(PacketFlags.Disconnect)) { DisconnectSeen = true; return; }
            if (p.Naks.Length > 0)
            {
                foreach (uint s in p.Naks)
                {
                    NaksReceived++;
                    if (_cache.TryGetValue(s, out byte[]? c))
                    {
                        // Resend with the Retransmission flag; same key, header re-hashed.
                        byte[] r = (byte[])c.Clone();
                        InboundPacket.TryParse(r, out InboundPacket rp);
                        Packet.TryPayloadHash(r, rp.OptionalEnd, out uint rph);
                        uint sealedPayload = rp.Checksum - Packet.HeaderHash(r);
                        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(4), rp.Flags | PacketFlags.Retransmission);
                        Packet.Seal(r, sealedPayload);
                        _out.Enqueue(r);
                        Resent++;
                    }
                }
            }
            if (p.Flags == PacketFlags.AckSequence)
            {
                if (p.Sequence > _lastClientSeq + 1) ClientSeqErrors++;   // ack-only reuses the current sequence
                return;
            }
            if (p.Has(PacketFlags.BlobFragments))
            {
                // Like the server: a gap is NAKed (plain, at the server's current sequence).
                if (p.Sequence > _lastClientSeq + 1)
                {
                    var nak = new WireWriter().U32(p.Sequence - _lastClientSeq - 1);
                    for (uint s = _lastClientSeq + 1; s < p.Sequence; s++) { nak.U32(s); _clientMissing.Add(s); }
                    Emit(Packet.Build(_seq, PacketFlags.RequestRetransmit, 0x0B, 0, 1, nak.Span, null, null, out _), cache: false);
                }
                else if (p.Sequence <= _lastClientSeq && !_clientMissing.Remove(p.Sequence))
                {
                    ClientSeqErrors++;   // a duplicate nobody asked for
                }
                if (p.Sequence > _lastClientSeq) _lastClientSeq = p.Sequence;
                fixed (byte* b = d) _reasm.ProcessDatagram(b, d.Length, now, this);
            }
        }

        public void Tick(long now)
        {
            if (!_connected || Mute) return;
            if (now - _lastAck >= 2000)
            {
                _lastAck = now;
                var w = new WireWriter().U32(_lastClientSeq);
                Emit(Packet.Build(_seq, PacketFlags.AckSequence, 0x0B, 0, 1, w.Span, null, null, out _), cache: false);
            }
        }

        void IServerMessageSink.OnServerMessage(uint fragmentSequence, byte* message, int length)
            => Handle(new ReadOnlySpan<byte>(message, length).ToArray());

        private void Handle(byte[] m)
        {
            uint op = BinaryPrimitives.ReadUInt32LittleEndian(m);
            var r = new NetReader(m.AsSpan(4));
            switch (op)
            {
                case Opcode.DddInterrogationResponse:
                    DddAnswered = true;
                    Send(new WireWriter().U32(Opcode.DddEnd).ToArray());
                    break;
                case Opcode.CharacterEnterWorldRequest:
                    Send(new WireWriter().U32(Opcode.EnterWorldServerReady).ToArray());
                    break;
                case Opcode.CharacterEnterWorld:
                    r.U32(out EnterWorldGuid);
                    r.String16L(out string acct);
                    EnterWorldAccount = acct;
                    if (RefuseEnterWorld)
                    {
                        Send(new WireWriter().U32(Opcode.CharacterError).U32(0x0D).ToArray());
                        break;
                    }
                    // Our own object arrives before PlayerCreate (different queues).
                    Send(CreateObject(Player, "Alpha"));
                    Send(new WireWriter().U32(Opcode.PlayerCreate).U32(Player).ToArray());
                    Send(CreateObject(Drudge, "Drudge Skulker"));
                    Send(new WireWriter().U32(Opcode.ServerMessage).String16L("Welcome to Testworld").U32(0x05).ToArray());
                    break;
                case Opcode.GameAction:
                    r.U32(out _);
                    r.U32(out uint type);
                    if (type == GameActionType.LoginComplete) LoginCompletes++;
                    if (type == ActionType.MoveToState && r.U32(out uint motionFlags))
                        Motions.Add(motionFlags == 0 ? "stop" : (motionFlags & 0x4) != 0 ? "forward" : $"0x{motionFlags:X}");
                    if (type == ActionType.AutonomousPosition && r.U32(out uint pcell) && r.F32(out float px) && r.F32(out float py))
                        Positions.Add((pcell, px, py));
                    if (type == GameActionType.Talk && r.String16L(out string text))
                    {
                        Said.Add(text);
                        Send(new WireWriter().U32(Opcode.HearSpeech).String16L(text).String16L("Alpha").U32(Player).U32(0x02).ToArray());
                    }
                    break;
                case Opcode.CharacterLogOff:
                    LogoffSeen = true;
                    Send(new WireWriter().U32(Opcode.CharacterLogOff).ToArray());
                    Send(new WireWriter().U32(Opcode.CharacterList).U32(0).U32(0).U32(0).U32(11).String16L(Account).U32(1).U32(1).ToArray());
                    break;
            }
        }

        private static byte[] CreateObject(uint id, string name)
        {
            var w = new WireWriter().U32(Opcode.ObjectCreate).U32(id);
            w.U8(0x11).U8(0).U8(0).U8(0);
            w.U32(0x8000).U32(0).U32(0xA9B40029).F32(1).F32(2).F32(3).F32(1).F32(0).F32(0).F32(0);
            for (int i = 0; i < 9; i++) w.U16(0);
            w.Align4();
            w.U32(0).String16L(name).PackedU32(1).PackedU32(0x1000).U32(0x10).U32(0).Align4();
            return w.ToArray();
        }

        private void Send(byte[] message)
        {
            if (Mute) return;
            var frag = new Fragment(++_fragSeq, 0x80000000, 1, 0, MessageQueue.UI, message);
            uint seq = ++_seq;
            byte[] d = Packet.Build(seq, 0, 0x0B, 0, 1, ReadOnlySpan<byte>.Empty, new[] { frag }, _keys.Next(), out _);
            _cache[seq] = d;
            if (_drop.Remove(seq)) return;          // lost on the way, once
            _out.Enqueue(d);
        }

        private void Emit(byte[] d, bool cache)
        {
            if (Mute) return;
            _out.Enqueue(d);
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static void Case(string name)
    {
        _case = name;
        Console.WriteLine($"-- {name}");
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
        Console.WriteLine($"   FAIL [{_case}] {what}: got {actual}, expected {expected}");
    }
}
