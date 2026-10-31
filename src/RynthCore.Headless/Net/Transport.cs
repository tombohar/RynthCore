// ============================================================================
//  RynthCore.Headless - Net/Transport.cs
//  The reliable layer under a session: the connect handshake, sequence numbers,
//  checksums and ISAAC keys, acks, retransmit requests (NAKs) both ways, and
//  reassembly of the server's fragments into whole messages.
//
//  Rules we follow (see docs\HEADLESS.md):
//    - LoginRequest is seq 0, ConnectResponse seq 1 (to port+1); our next sequenced
//      packet is 2. Fragment sequence starts at 1.
//    - Every new sequenced packet is encrypted with the next ClientSeed word. A resend is
//      the same packet with the Retransmission flag: same payload, same key, header
//      re-hashed. (The server matches keys in a look-ahead window, so re-keying a resend
//      would waste keystream.)
//    - Ack-only, NAK and Disconnect packets are plain and reuse our current sequence.
//    - The server only resends when asked: a sequence gap is NAKed until it is filled or
//      the server rejects it.
//    - Server keys are matched in a look-ahead window like the server does with ours; a
//      mismatch is counted, not fatal.
//  Single-threaded: every call comes from the session's tick.
// ============================================================================

using System.Buffers.Binary;
using RynthCore.Engine.Net;

namespace RynthCore.Headless.Net;

/// <summary>Where datagrams go. <c>connectPort</c> = the server's port+1 (ConnectResponse only).</summary>
internal interface IDatagramLink
{
    void Send(ReadOnlySpan<byte> datagram, bool connectPort);
    bool TryReceive(out byte[] datagram);
}

/// <summary>Message groups (queue ids) in fragment headers. The server ignores them on input.</summary>
internal static class MessageQueue
{
    public const ushort Event = 1, Control = 2, Weenie = 3, Login = 4, Database = 5, UI = 9;
}

internal sealed unsafe class Transport : IServerMessageSink
{
    public const long ConnectResponseDelayMs = 200;
    public const long ConnectResponseRepeatMs = 333;
    public const long AckIntervalMs = 1000;
    public const long KeepAliveMs = 5000;
    public const long NakRepeatMs = 1000;
    public const int MaxNakTries = 10;
    public const int MaxNaksPerPacket = 115;

    private readonly IDatagramLink _link;
    private readonly Func<long> _now;
    private readonly long _startMs;
    private readonly FragmentReassembler _reassembler = new();
    private readonly Queue<byte[]> _messages = new();

    // Connection
    private ConnectRequestData? _connect;
    private ushort _clientId, _iteration;
    private long _connectResponseDue = -1;
    private bool _serverTalked;           // anything after the ConnectRequest
    private bool _disconnected;

    // Outbound
    private Isaac? _outKeys;
    private uint _seq;                    // last sequence we used
    private uint _fragSeq;
    private readonly Dictionary<uint, SentPacket> _sent = new();

    // Inbound
    private InboundKeys? _inKeys;
    private uint _highest = 1;            // the server's packets start at 2 after the ConnectRequest
    private readonly SortedDictionary<uint, NakState> _missing = new();
    private bool _ackDirty;
    private long _lastSendMs, _lastAckMs;
    private uint _lastAckSent;

    private sealed class SentPacket { public required byte[] Data; public required uint Sealed; public bool Resent; }
    private sealed class NakState { public long LastMs = -NakRepeatMs; public int Tries; }

    public Transport(IDatagramLink link, Func<long> nowMs)
    {
        _link = link;
        _now = nowMs;
        _startMs = nowMs();
    }

    // ── state and counters ──────────────────────────────────────────────────

    public bool Connected => _connect != null;
    public bool ServerTalked => _serverTalked;
    public ConnectRequestData? ConnectData => _connect;
    public uint LastSequenceSent => _seq;
    public uint HighestReceived => _highest;
    public uint ContiguousReceived => _missing.Count == 0 ? _highest : _missing.Keys.First() - 1;
    public IReadOnlyCollection<uint> Missing => _missing.Keys;

    public long PacketsIn, PacketsOut, Resends, NaksSent, NakRequestsServed, Rejected, BadPackets, BadChecksums, Duplicates;
    public double LastServerTime;
    /// <summary>The local clock (ms) when <see cref="LastServerTime"/> arrived.</summary>
    public long LastServerTimeAtMs;

    // ── sending ─────────────────────────────────────────────────────────────

    private ushort TimeField => (ushort)(1 + (_now() - _startMs) / 500);

    private void Emit(byte[] d, bool connectPort = false)
    {
        _link.Send(d, connectPort);
        PacketsOut++;
        _lastSendMs = _now();
    }

    /// <summary>The first packet: seq 0, LoginRequest only, plain. The body is from <see cref="Messages.LoginRequest"/>.</summary>
    public void SendLoginRequest(ReadOnlySpan<byte> loginBody)
        => Emit(Packet.Build(0, PacketFlags.LoginRequest, 0, TimeField, 0, loginBody, null, null, out _));

    /// <summary>Queues one whole message (u32 opcode first); one packet per fragment.</summary>
    public void SendMessage(ReadOnlySpan<byte> message, ushort queue)
    {
        if (_outKeys == null)
            throw new InvalidOperationException("not connected");
        uint fragSeq = ++_fragSeq;
        int count = Math.Max(1, (message.Length + Fragment.MaxData - 1) / Fragment.MaxData);
        for (int i = 0; i < count; i++)
        {
            int off = i * Fragment.MaxData;
            byte[] data = message.Slice(off, Math.Min(Fragment.MaxData, message.Length - off)).ToArray();
            var frag = new Fragment(fragSeq, 0x80000000, (ushort)count, (ushort)i, queue, data);
            uint seq = ++_seq;
            byte[] d = Packet.Build(seq, 0, _clientId, TimeField, _iteration, ReadOnlySpan<byte>.Empty,
                new[] { frag }, _outKeys.Next(), out uint sealedPayload);
            _sent[seq] = new SentPacket { Data = d, Sealed = sealedPayload };
            Emit(d);
        }
    }

    /// <summary>Ends the session at once (plain Disconnect at the current sequence).</summary>
    public void SendDisconnect()
    {
        if (_connect == null || _disconnected) return;
        _disconnected = true;
        Emit(Packet.Build(_seq, PacketFlags.Disconnect, _clientId, TimeField, _iteration, ReadOnlySpan<byte>.Empty, null, null, out _));
    }

    private void SendAck()
    {
        uint ack = ContiguousReceived;
        Span<byte> body = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(body, ack);
        Emit(Packet.Build(_seq, PacketFlags.AckSequence, _clientId, TimeField, _iteration, body, null, null, out _));
        _ackDirty = false;
        _lastAckMs = _now();
        _lastAckSent = ack;
    }

    private void SendNaks(long now)
    {
        var ids = new List<uint>();
        var giveUp = new List<uint>();
        foreach ((uint seq, NakState st) in _missing)
        {
            if (now - st.LastMs < NakRepeatMs) continue;
            if (st.Tries >= MaxNakTries) { giveUp.Add(seq); continue; }
            st.LastMs = now;
            st.Tries++;
            ids.Add(seq);
            if (ids.Count == MaxNaksPerPacket) break;
        }
        foreach (uint seq in giveUp) { _missing.Remove(seq); Rejected++; }
        if (ids.Count == 0) return;

        var w = new WireWriter(4 + ids.Count * 4);
        w.U32((uint)ids.Count);
        foreach (uint id in ids) w.U32(id);
        Emit(Packet.Build(_seq, PacketFlags.RequestRetransmit, _clientId, TimeField, _iteration, w.Span, null, null, out _));
        NaksSent++;
    }

    /// <summary>Handshake resends, acks, keepalive and NAKs. Call every tick.</summary>
    public void Tick()
    {
        if (_connect == null || _disconnected) return;
        long now = _now();

        if (_connectResponseDue >= 0 && now >= _connectResponseDue)
        {
            if (_serverTalked)
            {
                _connectResponseDue = -1;
            }
            else
            {
                Span<byte> cookie = stackalloc byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(cookie, _connect.Cookie);
                Emit(Packet.Build(1, PacketFlags.ConnectResponse, 0, TimeField, 0, cookie, null, null, out _), connectPort: true);
                _connectResponseDue = now + ConnectResponseRepeatMs;
            }
        }
        if (!_serverTalked) return;

        if (_missing.Count > 0)
            SendNaks(now);
        if ((_ackDirty && now - _lastAckMs >= AckIntervalMs && ContiguousReceived != _lastAckSent)
            || now - _lastSendMs >= KeepAliveMs)
            SendAck();
    }

    // ── receiving ───────────────────────────────────────────────────────────

    /// <summary>The next whole server message (u32 opcode first), if any.</summary>
    public bool TryDequeue(out byte[] message) => _messages.TryDequeue(out message!);

    /// <summary>Drains the link. Returns how many datagrams were processed.</summary>
    public int Pump()
    {
        int n = 0;
        while (_link.TryReceive(out byte[] d))
        {
            Receive(d);
            n++;
        }
        return n;
    }

    public void Receive(byte[] d)
    {
        PacketsIn++;
        if (!InboundPacket.TryParse(d, out InboundPacket p)) { BadPackets++; return; }
        if (!VerifyChecksum(d, p)) BadChecksums++;

        if (p.Connect != null)
        {
            OnConnectRequest(p);
            return;
        }
        if (_connect == null) return;   // nothing is meaningful before the handshake
        _serverTalked = true;

        if (p.Has(PacketFlags.TimeSync)) { LastServerTime = p.TimeSync; LastServerTimeAtMs = _now(); }
        if (p.Has(PacketFlags.AckSequence)) Prune(p.Ack);
        if (p.Naks.Length > 0) Resend(p.Naks);
        foreach (uint r in p.Rejects)
            if (_missing.Remove(r)) Rejected++;

        if (!TrackSequence(p)) { Duplicates++; return; }
        if (p.Has(PacketFlags.BlobFragments))
            fixed (byte* b = d)
                _reassembler.ProcessDatagram(b, d.Length, _now(), this);
    }

    private bool VerifyChecksum(byte[] d, InboundPacket p)
    {
        if (!Packet.TryPayloadHash(d, p.OptionalEnd, out uint payload)) return false;
        uint header = Packet.HeaderHash(d);
        if (!p.Encrypted)
            return header + payload == p.Checksum;
        if (_inKeys == null) return false;
        uint key = (p.Checksum - header) ^ payload;
        return _inKeys.TryMatch(key, p.Has(PacketFlags.Retransmission));
    }

    private void OnConnectRequest(InboundPacket p)
    {
        if (_connect != null) return;   // a repeat of the same handshake
        ConnectRequestData c = p.Connect!;
        _connect = c;
        _clientId = (ushort)c.ClientId;
        _iteration = p.Iteration;
        _outKeys = new Isaac(c.ClientSeed);
        _inKeys = new InboundKeys(c.ServerSeed);
        _seq = 1;                        // ConnectResponse is seq 1
        LastServerTime = c.ServerTime;
        LastServerTimeAtMs = _now();
        _connectResponseDue = _now() + ConnectResponseDelayMs;
        _reassembler.Reset();
    }

    /// <summary>Sequence bookkeeping. False for a duplicate that should not be processed again.</summary>
    private bool TrackSequence(InboundPacket p)
    {
        uint s = p.Sequence;
        bool carriesData = p.Encrypted || p.Has(PacketFlags.BlobFragments);
        if (!carriesData)
        {
            // Plain control packets. Ack-only and NAK packets reuse the sequence of the server's
            // last encrypted packet, so a newer one means we lost that packet. A plain
            // RejectRetransmit takes a fresh sequence of its own.
            if (s > _highest)
            {
                bool consumes = p.Has(PacketFlags.RejectRetransmit);
                for (uint m = _highest + 1; m < s; m++) AddMissing(m);
                if (!consumes) AddMissing(s);
                _highest = s;
                _ackDirty = true;
            }
            return true;
        }

        if (s > _highest)
        {
            for (uint m = _highest + 1; m < s; m++) AddMissing(m);
            _highest = s;
            _ackDirty = true;
            return true;
        }
        if (_missing.Remove(s))
        {
            _ackDirty = true;
            return true;
        }
        return false;
    }

    private void AddMissing(uint seq)
    {
        if (seq >= 2 && !_missing.ContainsKey(seq))
            _missing[seq] = new NakState();
    }

    private void Prune(uint ack)
    {
        if (_sent.Count == 0) return;
        foreach (uint seq in _sent.Keys.Where(k => k <= ack).ToList())
            _sent.Remove(seq);
    }

    private void Resend(uint[] seqs)
    {
        foreach (uint seq in seqs)
        {
            NakRequestsServed++;
            if (!_sent.TryGetValue(seq, out SentPacket? sp)) continue;
            if (!sp.Resent)
            {
                uint flags = BinaryPrimitives.ReadUInt32LittleEndian(sp.Data.AsSpan(4)) | PacketFlags.Retransmission;
                BinaryPrimitives.WriteUInt32LittleEndian(sp.Data.AsSpan(4), flags);
                Packet.Seal(sp.Data, sp.Sealed);
                sp.Resent = true;
            }
            Emit(sp.Data);
            Resends++;
        }
    }

    void IServerMessageSink.OnServerMessage(uint fragmentSequence, byte* message, int length)
        => _messages.Enqueue(new ReadOnlySpan<byte>(message, length).ToArray());
}

/// <summary>
/// The server's keystream, matched the way the server matches ours: the expected words
/// are kept in a set, and a key that isn't there yet is looked for up to
/// <see cref="LookAhead"/> words further on. A resend reuses its original key.
/// </summary>
internal sealed class InboundKeys
{
    public const int LookAhead = 256;
    public const int MaxPending = 2048;
    public const int UsedMemory = 2048;

    private readonly Isaac _isaac;
    private readonly HashSet<uint> _pending = new();
    private readonly Queue<uint> _pendingOrder = new();
    private readonly HashSet<uint> _used = new();
    private readonly Queue<uint> _usedOrder = new();

    public InboundKeys(uint serverSeed) => _isaac = new Isaac(serverSeed);

    public bool TryMatch(uint key, bool retransmission)
    {
        if (_pending.Remove(key)) { Use(key); return true; }
        if (retransmission && _used.Contains(key)) return true;
        for (int i = 0; i < LookAhead; i++)
        {
            uint k = _isaac.Next();
            if (k == key) { Use(k); return true; }
            _pending.Add(k);
            _pendingOrder.Enqueue(k);
            while (_pendingOrder.Count > MaxPending)
                _pending.Remove(_pendingOrder.Dequeue());
        }
        return false;
    }

    private void Use(uint key)
    {
        if (_used.Add(key)) _usedOrder.Enqueue(key);
        while (_usedOrder.Count > UsedMemory)
            _used.Remove(_usedOrder.Dequeue());
    }
}
