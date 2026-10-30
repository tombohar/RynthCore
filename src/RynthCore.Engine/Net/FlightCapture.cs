// ============================================================================
//  RynthCore.Engine - Net/FlightCapture.cs
//  What the flight recorder keeps from each datagram, and its stall detectors.
//
//  Runs inside the sendto/recvfrom detours (Compatibility/RawPacketHooks.cs), after the
//  real call, on the bytes the client sent or got. Per datagram: the header walk
//  (AcPacketWire.LocateFragments), then for every fragment that starts a message, the
//  opcode decides whether the message is kept. A kept message is its first
//  FlightEntry.DataCap bytes, copied into the ring; decoding into text happens only when
//  the ring is dumped (FlightFormat). No allocation, no lock on the datagram path
//  (the engine-call table and the detectors take a small lock on the few messages
//  they care about).
//
//  Kept, client to server: every game action (0xF7B1) - AutonomousPosition at most one
//  per AutoPosIntervalMs unless a sequence or the cell changed - and any other message.
//  Kept, server to client: the game events in WantedEvent, the player's motion,
//  position (rate-limited like AutonomousPosition), vector and effect messages,
//  teleports, system text, and object messages (delete, pickup, state, motion) about
//  the objects the character just used.
//
//  Strictly read-only: nothing here changes, delays or drops traffic.
//  Pure code (no engine state); compiled into tools/FlightRecorderTests.
// ============================================================================

using System;
using System.Buffers.Binary;
using System.Threading;

namespace RynthCore.Engine.Net;

/// <summary>Opcodes, game event types and game action types the recorder knows.</summary>
internal static class FlightOp
{
    // Top-level messages.
    public const uint GameEvent = 0xF7B0, GameAction = 0xF7B1;
    public const uint UpdateMotion = 0xF74C, UpdatePosition = 0xF748, PlayerTeleport = 0xF751, VectorUpdate = 0xF74E;
    public const uint PlayEffect = 0xF755, PlayScriptId = 0xF754, ObjectDelete = 0xF747, PickupEvent = 0xF74A, SetState = 0xF74B;
    public const uint ParentEvent = 0xF749, ServerMessage = 0xF7E0, PlayerCreate = 0xF746, CharacterLogOff = 0xF653;
    public const uint InventoryRemoveObject = 0x0024, PlayerKilled = 0x019E, SetStackSize = 0x0197;

    // Game events (inside 0xF7B0).
    public const uint EvPopupString = 0x0004, EvInventoryPutObjInContainer = 0x0022, EvWieldObject = 0x0023;
    public const uint EvCloseGroundContainer = 0x0052, EvInventoryServerSaveFailed = 0x00A0, EvViewContents = 0x0196;
    public const uint EvInventoryPutObjectIn3D = 0x019A, EvAttackDone = 0x01A7, EvMagicRemoveSpell = 0x01A8;
    public const uint EvVictimNotification = 0x01AC, EvKillerNotification = 0x01AD, EvAttackerNotification = 0x01B1;
    public const uint EvDefenderNotification = 0x01B2, EvEvasionAttacker = 0x01B3, EvEvasionDefender = 0x01B4;
    public const uint EvCombatCommenceAttack = 0x01B8, EvUseDone = 0x01C7, EvConfirmationRequest = 0x0274;
    public const uint EvWeenieError = 0x028A, EvWeenieErrorWithString = 0x028B;
    public const uint EvMagicUpdateSpell = 0x02C1, EvMagicUpdateEnchantment = 0x02C2, EvMagicRemoveEnchantment = 0x02C3;
    public const uint EvMagicUpdateMultipleEnchantments = 0x02C4, EvMagicRemoveMultipleEnchantments = 0x02C5;
    public const uint EvMagicPurgeEnchantments = 0x02C6, EvMagicDispelEnchantment = 0x02C7;
    public const uint EvMagicDispelMultipleEnchantments = 0x02C8, EvTransientString = 0x02EB, EvMagicPurgeBadEnchantments = 0x0312;

    // Game actions (inside 0xF7B1).
    public const uint AcTargetedMeleeAttack = 0x0008, AcTargetedMissileAttack = 0x000A, AcTalk = 0x0015;
    public const uint AcPutItemInContainer = 0x0019, AcGetAndWieldItem = 0x001A, AcDropItem = 0x001B;
    public const uint AcTalkDirect = 0x0032, AcUseWithTarget = 0x0035, AcUse = 0x0036;
    public const uint AcRaiseVital = 0x0044, AcRaiseAttribute = 0x0045, AcRaiseSkill = 0x0046, AcTrainSkill = 0x0047;
    public const uint AcCastUntargetedSpell = 0x0048, AcCastTargetedSpell = 0x004A, AcChangeCombatMode = 0x0053;
    public const uint AcStackableMerge = 0x0054, AcStackableSplitToContainer = 0x0055, AcStackableSplitTo3D = 0x0056;
    public const uint AcTell = 0x005D, AcLoginComplete = 0x00A1, AcIdentifyObject = 0x00C8, AcGiveObjectRequest = 0x00CD;
    public const uint AcChatChannel = 0x0147, AcNoLongerViewingContents = 0x0195, AcStackableSplitToWield = 0x019B;
    public const uint AcCancelAttack = 0x01B7, AcQueryHealth = 0x01BF;
    public const uint AcJump = 0xF61B, AcMoveToState = 0xF61C, AcDoMovementCommand = 0xF61E, AcTurnTo = 0xF649;
    public const uint AcStopMovementCommand = 0xF661, AcAutonomyLevel = 0xF752, AcAutonomousPosition = 0xF753;
    public const uint AcJumpNonAutonomous = 0xF7C9;

    // WeenieError codes the detectors look at.
    public const uint ErrYoureTooBusy = 0x001D;

    /// <summary>The server-to-client game events the recorder keeps.</summary>
    public static bool WantedEvent(uint ev) => ev switch
    {
        EvPopupString or EvInventoryPutObjInContainer or EvWieldObject or EvCloseGroundContainer
        or EvInventoryServerSaveFailed or EvViewContents or EvInventoryPutObjectIn3D or EvAttackDone
        or EvMagicRemoveSpell or EvVictimNotification or EvKillerNotification or EvAttackerNotification
        or EvDefenderNotification or EvEvasionAttacker or EvEvasionDefender or EvCombatCommenceAttack
        or EvUseDone or EvConfirmationRequest or EvWeenieError or EvWeenieErrorWithString
        or EvMagicUpdateSpell or EvMagicUpdateEnchantment or EvMagicRemoveEnchantment
        or EvMagicUpdateMultipleEnchantments or EvMagicRemoveMultipleEnchantments or EvMagicPurgeEnchantments
        or EvMagicDispelEnchantment or EvMagicDispelMultipleEnchantments or EvTransientString
        or EvMagicPurgeBadEnchantments => true,
        _ => false,
    };

    /// <summary>Actions whose first argument (and for some the second) names the object the character uses.</summary>
    public static bool UsesObject(uint action) => action is AcUse or AcUseWithTarget or AcPutItemInContainer
        or AcNoLongerViewingContents or AcGiveObjectRequest or AcDropItem or AcGetAndWieldItem or AcIdentifyObject;
}

/// <summary>The per-second marker's numbers (FlightMark.Summary), packed into the entry's Data.</summary>
internal struct FlightSummary
{
    public const int Size = 40;
    public const int Unknown = int.MinValue;

    public int RealBusy;          // AC's m_cBusy; Unknown when unreadable
    public int ShadowBusy;        // the engine's counter
    public uint PendingType;      // ClientActionGates pending item request type; 0xFFFFFFFF unknown
    public uint PendingObject;
    public byte Attacking;        // 0/1, 255 unknown
    public byte Gesture;          // cast/action gesture animating: 0/1, 255 unknown
    public ushort InPackets, InMessages, InKept, InRetransmits, InResendRequests;
    public ushort OutPackets, OutMessages, OutKept, OutRetransmits, OutResendRequests;
    public ushort SendErrors;

    public readonly void Write(Span<byte> d)
    {
        BinaryPrimitives.WriteInt32LittleEndian(d, RealBusy);
        BinaryPrimitives.WriteInt32LittleEndian(d[4..], ShadowBusy);
        BinaryPrimitives.WriteUInt32LittleEndian(d[8..], PendingType);
        BinaryPrimitives.WriteUInt32LittleEndian(d[12..], PendingObject);
        d[16] = Attacking;
        d[17] = Gesture;
        W(d, 0, InPackets); W(d, 1, InMessages); W(d, 2, InKept); W(d, 3, InRetransmits); W(d, 4, InResendRequests);
        W(d, 5, OutPackets); W(d, 6, OutMessages); W(d, 7, OutKept); W(d, 8, OutRetransmits); W(d, 9, OutResendRequests);
        W(d, 10, SendErrors);
    }

    private static ushort R(ReadOnlySpan<byte> d, int i) => BinaryPrimitives.ReadUInt16LittleEndian(d[(18 + 2 * i)..]);
    private static void W(Span<byte> d, int i, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(d[(18 + 2 * i)..], v);

    public static FlightSummary Read(ReadOnlySpan<byte> d)
    {
        var s = new FlightSummary();
        if (d.Length < Size) { s.RealBusy = s.ShadowBusy = Unknown; s.PendingType = 0xFFFFFFFF; s.Attacking = s.Gesture = 255; return s; }
        s.RealBusy = BinaryPrimitives.ReadInt32LittleEndian(d);
        s.ShadowBusy = BinaryPrimitives.ReadInt32LittleEndian(d[4..]);
        s.PendingType = BinaryPrimitives.ReadUInt32LittleEndian(d[8..]);
        s.PendingObject = BinaryPrimitives.ReadUInt32LittleEndian(d[12..]);
        s.Attacking = d[16];
        s.Gesture = d[17];
        s.InPackets = R(d, 0); s.InMessages = R(d, 1); s.InKept = R(d, 2); s.InRetransmits = R(d, 3); s.InResendRequests = R(d, 4);
        s.OutPackets = R(d, 5); s.OutMessages = R(d, 6); s.OutKept = R(d, 7); s.OutRetransmits = R(d, 8); s.OutResendRequests = R(d, 9);
        s.SendErrors = R(d, 10);
        return s;
    }
}

internal sealed unsafe class FlightCapture
{
    public const int AutoPosIntervalMs = 2000;       // AutonomousPosition / the player's UpdatePosition
    public const int EngineMatchMs = 1500;           // an engine call explains an action sent this soon after
    public const int EngineRepeatMs = 1000;          // identical engine calls this close are folded into one line
    public const int TargetMemoryMs = 20_000;        // objects used this recently have their messages kept
    public const int ResendMarkIntervalMs = 500;     // resend-request markers per direction
    public const int UseSettleMs = 1500;             // UseDone(0) with nothing else this long after = refused
    public const int UseRefusalStreak = 4;           // refused uses in a row that trigger a dump
    public const int UseUnansweredMs = 5000;         // a use with no UseDone this long = unanswered
    public const int UseUnansweredStreak = 3;
    public const int TooBusyWindowMs = 20_000;
    public const int TooBusyStreak = 3;

    public readonly FlightRing Ring;
    private readonly Func<long> _clock;
    private readonly long _ticksPerSecond;

    private volatile uint _playerId;
    public uint PlayerId => _playerId;

    // ── per direction (each touched by its own detour only) ────────────────
    private sealed class Side
    {
        public readonly uint[] Seen = new uint[256];   // fragment sequences of recent messages
        public int SeenNext;
        public long PosStamp;                          // last kept position message
        public uint PosCell;
        public ushort PosA, PosB, PosC, PosD;          // its four sequence numbers
        public uint PosSuppressed;
        public long ResendStamp;
        public uint ResendSuppressed;
        // counters since the last summary
        public int Packets, Messages, Kept, Retransmits, ResendRequests, SendErrors;
    }
    private readonly Side _in = new(), _out = new();

    // ── objects the character used (written by outbound, read by inbound) ──
    private readonly uint[] _targets = new uint[8];
    private readonly long[] _targetAt = new long[8];
    private int _targetNext;

    // ── engine calls waiting for their action ──────────────────────────────
    private readonly object _engineLock = new();
    private struct PendingCall { public string? Name; public uint A, B; public long Stamp; public bool Used; }
    private readonly PendingCall[] _pending = new PendingCall[16];
    private int _pendingNext;
    private string? _repeatName;
    private uint _repeatA, _repeatB, _repeatCount;
    private long _repeatStamp;

    // ── stall detectors ────────────────────────────────────────────────────
    private readonly object _detectLock = new();
    private struct UseTrack { public uint Target; public bool Counted; public long Sent; public bool Answered; public uint Err; public long AnsweredAt; public bool Responded; public bool Settled; }
    private readonly UseTrack[] _uses = new UseTrack[8];
    private int _useCount;
    private int _refusedStreak, _unansweredStreak;
    private readonly uint[] _refusedTargets = new uint[UseRefusalStreak];
    private readonly long[] _tooBusy = new long[TooBusyStreak];
    private int _tooBusyNext;
    private string? _trigger;

    public FlightCapture(int capacityPow2, Func<long> clock, long ticksPerSecond)
    {
        Ring = new FlightRing(capacityPow2);
        _clock = clock;
        _ticksPerSecond = ticksPerSecond;
    }

    public long Now() => _clock();
    private long Ms(long ticks) => ticks * 1000 / _ticksPerSecond;
    private long TicksFromMs(long ms) => ms * _ticksPerSecond / 1000;

    /// <summary>Set the player id when it is known some other way (also learned from game event headers).</summary>
    public void SetPlayerId(uint id) { if (id != 0) _playerId = id; }

    // ── datagrams ──────────────────────────────────────────────────────────

    /// <summary>One datagram the client sent (<paramref name="outbound"/>) or got.</summary>
    public void OnDatagram(bool outbound, byte* data, int length, bool sendFailed = false)
    {
        if (data == null || length < AcPacketWire.HeaderSize)
            return;
        Side side = outbound ? _out : _in;
        side.Packets++;
        if (sendFailed) side.SendErrors++;
        uint flags = AcPacketWire.ReadU32(data + 4);
        bool retransmit = (flags & PacketFlags.Retransmission) != 0;
        if (retransmit) side.Retransmits++;

        long now = 0;
        if ((flags & (PacketFlags.RequestRetransmit | PacketFlags.Disconnect)) != 0)
        {
            now = _clock();
            NoteControl(outbound, side, flags, data, length, now);
        }

        if (AcPacketWire.LocateFragments(data, length, out int pos, out int end) != PacketWalk.Ok)
            return;

        int guard = 0;
        while (pos + AcPacketWire.FragmentHeaderSize <= end && ++guard <= 64)
        {
            byte* frag = data + pos;
            int size = AcPacketWire.ReadU16(frag + 10);
            if (size < AcPacketWire.FragmentHeaderSize || pos + size > end)
                break;
            pos += size;
            ushort index = AcPacketWire.ReadU16(frag + 12);
            if (index != 0)
                continue;
            int msgLen = size - AcPacketWire.FragmentHeaderSize;
            if (msgLen < 4)
                continue;
            uint seq = AcPacketWire.ReadU32(frag);
            ushort count = AcPacketWire.ReadU16(frag + 8);
            if (retransmit && Seen(side, seq))
                continue;   // the first copy was already handled
            Remember(side, seq);
            side.Messages++;
            if (now == 0) now = _clock();

            FlightFlags f = FlightFlags.None;
            if (count > 1) f |= FlightFlags.MultiFragment;
            if (retransmit) f |= FlightFlags.Retransmit;
            if (sendFailed) f |= FlightFlags.SendFailed;
            int fullLen = count > 1 ? count * msgLen : msgLen;
            byte* msg = frag + AcPacketWire.FragmentHeaderSize;
            if (outbound) Outbound(msg, msgLen, fullLen, seq, f, now);
            else Inbound(msg, msgLen, fullLen, seq, f, now);
        }
    }

    private static bool Seen(Side side, uint seq)
    {
        uint[] seen = side.Seen;
        for (int i = 0; i < seen.Length; i++)
            if (seen[i] == seq) return true;
        return false;
    }

    private static void Remember(Side side, uint seq)
    {
        side.Seen[side.SeenNext] = seq;
        side.SeenNext = (side.SeenNext + 1) & (side.Seen.Length - 1);
    }

    private void NoteControl(bool outbound, Side side, uint flags, byte* data, int length, long now)
    {
        uint bit = (flags & PacketFlags.Disconnect) != 0 ? PacketFlags.Disconnect : PacketFlags.RequestRetransmit;
        uint count = 0, first = 0;
        if (bit == PacketFlags.RequestRetransmit)
        {
            side.ResendRequests++;
            // The resend list is the first optional header when ServerSwitch is absent (it is
            // between ServerSwitch and the rest). Read it only when it is plainly there.
            int p = AcPacketWire.HeaderSize + ((flags & PacketFlags.ServerSwitch) != 0 ? 8 : 0);
            if (p + 8 <= length)
            {
                count = AcPacketWire.ReadU32(data + p);
                first = count > 0 ? AcPacketWire.ReadU32(data + p + 4) : 0;
            }
            if (Ms(now - side.ResendStamp) < ResendMarkIntervalMs)
            {
                side.ResendSuppressed++;
                return;
            }
            side.ResendStamp = now;
        }
        Span<byte> dirByte = stackalloc byte[4];
        dirByte[0] = (byte)(outbound ? FlightDir.Out : FlightDir.In);
        BinaryPrimitives.WriteUInt16LittleEndian(dirByte[2..], (ushort)Math.Min(side.ResendSuppressed, ushort.MaxValue));
        Ring.WriteMarkBytes(now, FlightMark.NetControl, bit, count, first, dirByte);
        side.ResendSuppressed = 0;
    }

    // ── client to server ───────────────────────────────────────────────────

    private void Outbound(byte* msg, int len, int fullLen, uint seq, FlightFlags f, long now)
    {
        uint opcode = AcPacketWire.ReadU32(msg);
        if (opcode != FlightOp.GameAction || len < 12)
        {
            Keep(_out, now, FlightDir.Out, f, msg, len, fullLen, seq, 0);
            return;
        }
        uint action = AcPacketWire.ReadU32(msg + 8);
        uint arg0 = len >= 16 ? AcPacketWire.ReadU32(msg + 12) : 0;
        uint arg1 = len >= 20 ? AcPacketWire.ReadU32(msg + 16) : 0;

        if (action == FlightOp.AcAutonomousPosition)
        {
            // Position(cell, xyz, wxyz) = 32 bytes after the action header, then 4 u16 sequences.
            if (len >= 12 + 32 + 8)
            {
                uint cell = arg0;
                byte* s = msg + 12 + 32;
                if (!PositionChanged(_out, now, cell, AcPacketWire.ReadU16(s), AcPacketWire.ReadU16(s + 2), AcPacketWire.ReadU16(s + 4), AcPacketWire.ReadU16(s + 6)))
                    return;
            }
            Keep(_out, now, FlightDir.Out, f | FlightFlags.Client, msg, len, fullLen, seq, TakePosSuppressed(_out));
            return;
        }

        if (FlightOp.UsesObject(action))
        {
            NoteTarget(arg0, now);
            if (action is FlightOp.AcUseWithTarget or FlightOp.AcPutItemInContainer or FlightOp.AcGiveObjectRequest)
                NoteTarget(arg1, now);
        }

        // An IdentifyObject is the engine's when an engine send for that object was
        // registered just before (EngineSend, with who asked); otherwise AC or Decal sent it.
        byte idWho = action == FlightOp.AcIdentifyObject ? MatchEngineSend(arg0, now) : (byte)0;
        bool engine = idWho != 0 || MatchEngineCall(action, arg0, arg1, now);
        if (action == FlightOp.AcIdentifyObject && !engine)
            Interlocked.Increment(ref _identifyForeign);
        f |= engine ? FlightFlags.Engine : FlightFlags.Client;
        Keep(_out, now, FlightDir.Out, f, msg, len, fullLen, seq, 0, idWho);

        if (action == FlightOp.AcUse || action == FlightOp.AcUseWithTarget)
            DetectUseSent(action == FlightOp.AcUse ? arg0 : arg1, counted: action == FlightOp.AcUse, now);
    }

    // ── server to client ───────────────────────────────────────────────────

    private void Inbound(byte* msg, int len, int fullLen, uint seq, FlightFlags f, long now)
    {
        uint opcode = AcPacketWire.ReadU32(msg);
        uint obj = len >= 8 ? AcPacketWire.ReadU32(msg + 4) : 0;
        switch (opcode)
        {
            case FlightOp.GameEvent:
            {
                if (len < 16) return;
                if (obj != 0 && obj != _playerId) _playerId = obj;   // game events are addressed to the player
                uint ev = AcPacketWire.ReadU32(msg + 12);
                if (!FlightOp.WantedEvent(ev)) return;
                Keep(_in, now, FlightDir.In, f, msg, len, fullLen, seq, 0);
                uint a = len >= 20 ? AcPacketWire.ReadU32(msg + 16) : 0;
                if (ev == FlightOp.EvUseDone) DetectUseDone(a, now);
                else if (ev == FlightOp.EvViewContents) DetectOpened(a, now);
                else if ((ev == FlightOp.EvWeenieError || ev == FlightOp.EvWeenieErrorWithString) && a == FlightOp.ErrYoureTooBusy)
                    DetectTooBusy(now);
                return;
            }
            case FlightOp.UpdateMotion:
                if (obj == _playerId && obj != 0)
                {
                    Keep(_in, now, FlightDir.In, f, msg, len, fullLen, seq, 0);
                    DetectPlayerMotion(msg, len, now);
                }
                else if (IsTarget(obj, now))
                {
                    Keep(_in, now, FlightDir.In, f, msg, len, fullLen, seq, 0);
                    DetectResponse(obj, now);
                }
                return;
            case FlightOp.UpdatePosition:
                if (obj == 0 || obj != _playerId) return;
                if (TryPositionPackSequences(msg + 8, len - 8, out uint cell, out ushort s1, out ushort s2, out ushort s3, out ushort s4)
                    && !PositionChanged(_in, now, cell, s1, s2, s3, s4))
                    return;
                Keep(_in, now, FlightDir.In, f, msg, len, fullLen, seq, TakePosSuppressed(_in));
                return;
            case FlightOp.VectorUpdate:
            case FlightOp.PlayEffect:
            case FlightOp.PlayScriptId:
            case FlightOp.ParentEvent:
                if (obj != 0 && (obj == _playerId || IsTarget(obj, now)))
                    Keep(_in, now, FlightDir.In, f, msg, len, fullLen, seq, 0);
                return;
            case FlightOp.ObjectDelete:
            case FlightOp.PickupEvent:
            case FlightOp.SetState:
            case FlightOp.InventoryRemoveObject:
            case FlightOp.SetStackSize:
                if (obj != 0 && (obj == _playerId || IsTarget(obj, now)))
                {
                    Keep(_in, now, FlightDir.In, f, msg, len, fullLen, seq, 0);
                    DetectResponse(obj, now);
                }
                return;
            case FlightOp.PlayerCreate:
                if (obj != 0) _playerId = obj;
                Keep(_in, now, FlightDir.In, f, msg, len, fullLen, seq, 0);
                return;
            case FlightOp.PlayerTeleport:
            case FlightOp.CharacterLogOff:
            case FlightOp.PlayerKilled:
                Keep(_in, now, FlightDir.In, f, msg, len, fullLen, seq, 0);
                return;
            case FlightOp.ServerMessage:
                Keep(_in, now, FlightDir.In, f, msg, len, fullLen, seq, 0);
                if (ContainsTooBusy(msg + 4, len - 4))
                    DetectTooBusy(now);
                return;
        }
    }

    private void Keep(Side side, long now, FlightDir dir, FlightFlags f, byte* msg, int len, int fullLen, uint seq, uint suppressed, uint requester = 0)
    {
        side.Kept++;
        Ring.WriteMessage(now, dir, f, msg, len, fullLen, seq, suppressed, requester);
    }

    // ── positions ──────────────────────────────────────────────────────────

    /// <summary>True when this position message should be kept (and remembers it as the last kept).</summary>
    private bool PositionChanged(Side side, long now, uint cell, ushort a, ushort b, ushort c, ushort d)
    {
        bool keep = side.PosStamp == 0 || cell != side.PosCell || a != side.PosA || c != side.PosC || d != side.PosD
                    || Ms(now - side.PosStamp) >= AutoPosIntervalMs;
        if (!keep)
        {
            side.PosSuppressed++;
            return false;
        }
        side.PosStamp = now;
        side.PosCell = cell;
        side.PosA = a; side.PosB = b; side.PosC = c; side.PosD = d;
        return true;
    }

    private static uint TakePosSuppressed(Side side)
    {
        uint n = side.PosSuppressed;
        side.PosSuppressed = 0;
        return n;
    }

    /// <summary>
    /// PositionPack: flags, cell, xyz, the rotation parts the flags don't drop, velocity when
    /// flagged, placement when flagged, then four u16 sequences (instance, position, teleport,
    /// force position).
    /// </summary>
    public static bool TryPositionPackSequences(byte* p, int len, out uint cell, out ushort s1, out ushort s2, out ushort s3, out ushort s4)
    {
        cell = 0; s1 = s2 = s3 = s4 = 0;
        if (len < 20) return false;
        uint flags = AcPacketWire.ReadU32(p);
        cell = AcPacketWire.ReadU32(p + 4);
        int pos = 20;
        for (uint bit = 0x08; bit <= 0x40; bit <<= 1)
            if ((flags & bit) == 0) pos += 4;
        if ((flags & 0x01) != 0) pos += 12;
        if ((flags & 0x02) != 0) pos += 4;
        if (pos + 8 > len) return false;
        s1 = AcPacketWire.ReadU16(p + pos);
        s2 = AcPacketWire.ReadU16(p + pos + 2);
        s3 = AcPacketWire.ReadU16(p + pos + 4);
        s4 = AcPacketWire.ReadU16(p + pos + 6);
        return true;
    }

    // ── objects the character used ─────────────────────────────────────────

    private void NoteTarget(uint id, long now)
    {
        if (id == 0 || id == _playerId) return;
        for (int i = 0; i < _targets.Length; i++)
        {
            if (_targets[i] == id) { _targetAt[i] = now; return; }
        }
        int k = _targetNext;
        _targets[k] = id;
        _targetAt[k] = now;
        _targetNext = (k + 1) & (_targets.Length - 1);
    }

    private bool IsTarget(uint id, long now)
    {
        if (id == 0) return false;
        for (int i = 0; i < _targets.Length; i++)
            if (_targets[i] == id) return Ms(now - _targetAt[i]) <= TargetMemoryMs;
        return false;
    }

    // ── engine calls ───────────────────────────────────────────────────────

    /// <summary>
    /// The engine made a direct action call (AcActionTrace names). Writes a marker (identical
    /// calls within EngineRepeatMs fold into one "repeated" line) and remembers the call so
    /// the action it produces is tagged as the engine's.
    /// </summary>
    public void EngineCall(string name, uint a, uint b)
    {
        long now = _clock();
        lock (_engineLock)
        {
            if (Expects(name))
            {
                ref PendingCall p = ref FindPending(name, a, b, now);
                p.Name = name; p.A = a; p.B = b; p.Stamp = now; p.Used = false;
            }
            if (name == _repeatName)
            {
                if (a == _repeatA && b == _repeatB && Ms(now - _repeatStamp) < EngineRepeatMs)
                {
                    _repeatCount++;
                    _repeatStamp = now;
                    return;
                }
            }
            FlushRepeat();
            _repeatName = name; _repeatA = a; _repeatB = b; _repeatStamp = now; _repeatCount = 0;
        }
        Ring.WriteMark(now, FlightMark.EngineCall, name, a, b, 0, default);
    }

    /// <summary>Writes the "repeated N more times" line for folded calls, if any (call under _engineLock).</summary>
    private void FlushRepeat()
    {
        if (_repeatCount > 0 && _repeatName != null)
            Ring.WriteMark(_repeatStamp, FlightMark.EngineCall, _repeatName, _repeatA, _repeatB, _repeatCount, default);
        _repeatCount = 0;
    }

    /// <summary>Flush a pending "repeated" line (before a dump).</summary>
    public void FlushEngineRepeats()
    {
        lock (_engineLock)
        {
            FlushRepeat();
            _repeatName = null;
        }
    }

    private ref PendingCall FindPending(string name, uint a, uint b, long now)
    {
        for (int i = 0; i < _pending.Length; i++)
        {
            ref PendingCall p = ref _pending[i];
            if (p.Name == name && p.A == a && p.B == b) return ref p;
        }
        int k = _pendingNext;
        _pendingNext = (k + 1) % _pending.Length;
        return ref _pending[k];
    }

    private bool MatchEngineCall(uint action, uint arg0, uint arg1, long now)
    {
        lock (_engineLock)
        {
            int best = -1;
            for (int i = 0; i < _pending.Length; i++)
            {
                ref PendingCall p = ref _pending[i];
                if (p.Name == null || p.Used || now < p.Stamp || Ms(now - p.Stamp) > EngineMatchMs) continue;
                if (!Produces(p.Name, p.A, p.B, action, arg0, arg1)) continue;
                if (best < 0 || p.Stamp < _pending[best].Stamp) best = i;   // oldest first
            }
            if (best < 0) return false;
            _pending[best].Used = true;
            return true;
        }
    }

    // -- engine IdentifyObject (0xC8) sends --
    // Kept apart from _pending: AutoId alone can send 30 a second, which would push every
    // other engine call out of that 16-entry table. No marker line per send: the tag on the
    // 0xC8 itself ([eng:<who>]) says who asked. 256 entries = AcMainThreadQueue's
    // MaxRequestIdQueue: one drain can send a whole full queue before any of it reaches sendto.
    private struct IdPending { public uint Id; public byte Who; public long Stamp; public bool Used; }
    private readonly IdPending[] _idPending = new IdPending[256];
    private int _idPendingNext;
    private long _identifyForeign;

    /// <summary>IdentifyObject (0xC8) messages sent with no engine send to match: AC itself or Decal.</summary>
    public long IdentifyForeignTotal => Interlocked.Read(ref _identifyForeign);

    /// <summary>
    /// The engine is about to send an IdentifyObject for <paramref name="id"/> on behalf of
    /// <paramref name="who"/> (an IdRequester, non-zero). The next 0xC8 for that id within
    /// EngineMatchMs is tagged [eng:&lt;who&gt;]. Called BEFORE the send, so a 0xC8 that AC puts
    /// on the wire inside the call is still matched. Returns a token for EngineSendFailed, or -1.
    /// </summary>
    public int EngineSend(byte who, uint id)
    {
        if (who == 0) return -1;
        long now = _clock();
        lock (_engineLock)
        {
            int slot = _idPendingNext;
            ref IdPending p = ref _idPending[slot];
            _idPendingNext = (_idPendingNext + 1) & (_idPending.Length - 1);
            p.Id = id; p.Who = who; p.Stamp = now; p.Used = false;
            return slot;
        }
    }

    /// <summary>
    /// The send registered as <paramref name="token"/> did not happen (the call returned false or
    /// threw): retire its entry so it cannot tag a later 0xC8 for the same id. A no-op when the
    /// entry was already matched or the slot has since been reused for another id.
    /// </summary>
    public void EngineSendFailed(int token, uint id)
    {
        if (token < 0 || token >= _idPending.Length) return;
        lock (_engineLock)
        {
            ref IdPending p = ref _idPending[token];
            if (p.Id == id && !p.Used) p.Used = true;
        }
    }

    /// <summary>The requester of the oldest unmatched engine send for <paramref name="id"/>, or 0.</summary>
    private byte MatchEngineSend(uint id, long now)
    {
        lock (_engineLock)
        {
            int best = -1;
            for (int i = 0; i < _idPending.Length; i++)
            {
                ref IdPending p = ref _idPending[i];
                if (p.Who == 0 || p.Used || p.Id != id || now < p.Stamp || Ms(now - p.Stamp) > EngineMatchMs) continue;
                if (best < 0 || p.Stamp < _idPending[best].Stamp) best = i;
            }
            if (best < 0) return 0;
            _idPending[best].Used = true;
            return _idPending[best].Who;
        }
    }

    /// <summary>Engine calls that send something to the server.</summary>
    private static bool Expects(string name) => name switch
    {
        "SelectItem" or "SelectItem-dedup" or "OverlaySelect" => false,
        _ => true,
    };

    /// <summary>Would engine call <paramref name="name"/>(a, b) send this action?</summary>
    public static bool Produces(string name, uint a, uint b, uint action, uint arg0, uint arg1)
    {
        switch (name)
        {
            case "UseObject":
                return (action == FlightOp.AcUse || action == FlightOp.AcUseWithTarget) && arg0 == a;
            case "UseObjectOn":
            case "UseEquippedItem":
                return (action == FlightOp.AcUseWithTarget && arg0 == a && (b == 0 || arg1 == b))
                       || (action == FlightOp.AcUse && arg0 == a);
            case "CastSpell":
                return (action == FlightOp.AcCastTargetedSpell && arg1 == b)
                       || (action == FlightOp.AcCastUntargetedSpell && arg0 == b);
            case "ChangeCombatMode":
                return action == FlightOp.AcChangeCombatMode && arg0 == a;
            case "MeleeAttack":
                return action == FlightOp.AcTargetedMeleeAttack && arg0 == a;
            case "MissileAttack":
                return action == FlightOp.AcTargetedMissileAttack && arg0 == a;
            case "NativeAttack":
                return action is FlightOp.AcTargetedMeleeAttack or FlightOp.AcTargetedMissileAttack;
            case "CloseContainer":
                return action == FlightOp.AcNoLongerViewingContents && arg0 == a;
            case "MoveItemExternal":
            case "MoveItemInternal":
                return (action is FlightOp.AcPutItemInContainer or FlightOp.AcDropItem or FlightOp.AcGetAndWieldItem
                        or FlightOp.AcStackableMerge or FlightOp.AcStackableSplitToContainer or FlightOp.AcStackableSplitTo3D
                        or FlightOp.AcStackableSplitToWield && arg0 == a)
                       || (action == FlightOp.AcGiveObjectRequest && arg1 == a);
            case "SplitStackInternal":
                return action is FlightOp.AcStackableSplitToContainer or FlightOp.AcStackableSplitTo3D or FlightOp.AcStackableSplitToWield
                       && arg0 == a;
            case "MergeStackInternal":
                return action == FlightOp.AcStackableMerge && arg0 == a;
            case "GiveObjectTo":
                return action == FlightOp.AcGiveObjectRequest && arg1 == a;
            case "WieldItem":
                return action == FlightOp.AcGetAndWieldItem && arg0 == a;
            case "CancelAttack":
                return action == FlightOp.AcCancelAttack;
            case "Raise":
                return action is FlightOp.AcRaiseVital or FlightOp.AcRaiseAttribute or FlightOp.AcRaiseSkill;
            case "TrainSkill":
                return action == FlightOp.AcTrainSkill;
            case "DoMovement":
            case "StopMovement":
            case "SetMotion":
            case "SetAutoRun":
            case "StopCompletely":
            case "TurnToHeading":
                return action is FlightOp.AcMoveToState or FlightOp.AcDoMovementCommand or FlightOp.AcStopMovementCommand or FlightOp.AcTurnTo;
            case "TapJump":
            case "DoJump":
            case "CommenceJump":
            case "LaunchJumpWithMotion":
            case "JumpNonAutonomous":
                return action is FlightOp.AcJump or FlightOp.AcJumpNonAutonomous or FlightOp.AcMoveToState;
            default:
                return false;
        }
    }

    // ── markers ────────────────────────────────────────────────────────────

    public void Mark(FlightMark kind, uint a = 0, uint b = 0, uint c = 0, string? text = null)
        => Ring.WriteMark(_clock(), kind, null, a, b, c, text.AsSpan());

    // ── summary ────────────────────────────────────────────────────────────

    /// <summary>Fills the link counters since the last call and resets them.</summary>
    public void TakeCounters(ref FlightSummary s)
    {
        static ushort C(ref int v) { int x = Interlocked.Exchange(ref v, 0); return (ushort)Math.Clamp(x, 0, ushort.MaxValue); }
        s.InPackets = C(ref _in.Packets); s.InMessages = C(ref _in.Messages); s.InKept = C(ref _in.Kept);
        s.InRetransmits = C(ref _in.Retransmits); s.InResendRequests = C(ref _in.ResendRequests);
        s.OutPackets = C(ref _out.Packets); s.OutMessages = C(ref _out.Messages); s.OutKept = C(ref _out.Kept);
        s.OutRetransmits = C(ref _out.Retransmits); s.OutResendRequests = C(ref _out.ResendRequests);
        s.SendErrors = C(ref _out.SendErrors);
    }

    public void WriteSummary(in FlightSummary s)
    {
        Span<byte> d = stackalloc byte[FlightSummary.Size];
        s.Write(d);
        Ring.WriteMarkBytes(_clock(), FlightMark.Summary, 0, 0, 0, d);
    }

    // ── stall detectors ────────────────────────────────────────────────────
    // use refusal: a Use answered by UseDone(0) with nothing about it afterwards (no
    //   ViewContents, no message about the object, no player motion with an action in it)
    //   for UseSettleMs; UseRefusalStreak of those in a row. A ViewContents resets it.
    // use unanswered: UseUnansweredStreak uses in a row with no UseDone for UseUnansweredMs.
    // too busy: TooBusyStreak "You're too busy" (WeenieError 0x1D or the system text)
    //   within TooBusyWindowMs.

    /// <summary>A dump reason the detectors raised since the last call, or null.</summary>
    public string? TakeTrigger()
    {
        lock (_detectLock)
        {
            string? t = _trigger;
            _trigger = null;
            return t;
        }
    }

    public int RefusedStreak { get { lock (_detectLock) return _refusedStreak; } }

    private void DetectUseSent(uint target, bool counted, long now)
    {
        lock (_detectLock)
        {
            SettleUses(now);
            if (_useCount == _uses.Length)
            {
                Array.Copy(_uses, 1, _uses, 0, _uses.Length - 1);
                _useCount--;
            }
            _uses[_useCount++] = new UseTrack { Target = target, Counted = counted, Sent = now };
        }
    }

    private void DetectUseDone(uint err, long now)
    {
        lock (_detectLock)
        {
            for (int i = 0; i < _useCount; i++)
            {
                if (_uses[i].Answered) continue;
                _uses[i].Answered = true;
                _uses[i].Err = err;
                _uses[i].AnsweredAt = now;
                break;
            }
        }
    }

    private void DetectOpened(uint container, long now)
    {
        lock (_detectLock)
        {
            for (int i = 0; i < _useCount; i++)
                if (_uses[i].Target == container) _uses[i].Responded = true;
            _refusedStreak = 0;
            _unansweredStreak = 0;
        }
    }

    private void DetectResponse(uint obj, long now)
    {
        lock (_detectLock)
        {
            for (int i = 0; i < _useCount; i++)
                if (_uses[i].Target == obj && now >= _uses[i].Sent) _uses[i].Responded = true;
        }
    }

    /// <summary>The player's motion: one carrying actions (a command list: eat, drink, a cast...) or a
    /// server move/turn to an object answers the uses still open.</summary>
    private void DetectPlayerMotion(byte* msg, int len, long now)
    {
        // opcode, guid, u16 instance, u16 movement, u16 server control, u8 autonomous, pad to 16,
        // then u8 movement type, u8 flags, u16 style, then the type's data.
        if (len < 24) return;
        byte type = msg[16];
        uint target = 0;
        bool action = false;
        if (type == 0)
        {
            uint packed = AcPacketWire.ReadU32(msg + 20);
            action = (packed >> 7) != 0;   // number of commands
        }
        else if ((type == 6 || type == 8) && len >= 24)
        {
            target = AcPacketWire.ReadU32(msg + 20);
        }
        if (!action && target == 0) return;
        lock (_detectLock)
        {
            for (int i = 0; i < _useCount; i++)
            {
                ref UseTrack u = ref _uses[i];
                if (u.Settled || now < u.Sent) continue;
                if (action || u.Target == target) u.Responded = true;
            }
        }
    }

    private void DetectTooBusy(long now)
    {
        lock (_detectLock)
        {
            _tooBusy[_tooBusyNext] = now;
            _tooBusyNext = (_tooBusyNext + 1) % _tooBusy.Length;
            long oldest = long.MaxValue;
            foreach (long t in _tooBusy)
            {
                if (t == 0) return;
                if (t < oldest) oldest = t;
            }
            if (Ms(now - oldest) <= TooBusyWindowMs)
            {
                _trigger ??= $"too-busy: {TooBusyStreak} \"You're too busy\" answers within {Ms(now - oldest) / 1000.0:0.0} s";
                Array.Clear(_tooBusy);
            }
        }
    }

    /// <summary>Settle uses whose time is up (call regularly; also runs on every Use sent).</summary>
    public void Tick()
    {
        long now = _clock();
        lock (_detectLock) SettleUses(now);
    }

    private void SettleUses(long now)
    {
        int keep = 0;
        for (int i = 0; i < _useCount; i++)
        {
            UseTrack u = _uses[i];
            bool done = false;
            if (u.Answered)
            {
                if (!u.Counted)
                {
                    done = u.Responded || u.Err != 0 || Ms(now - u.AnsweredAt) >= UseSettleMs;
                }
                else if (u.Responded || u.Err != 0)
                {
                    done = true;
                    if (u.Responded) _refusedStreak = 0;
                    _unansweredStreak = 0;
                }
                else if (Ms(now - u.AnsweredAt) >= UseSettleMs)
                {
                    done = true;
                    _unansweredStreak = 0;
                    _refusedTargets[_refusedStreak % _refusedTargets.Length] = u.Target;
                    _refusedStreak++;
                    if (_refusedStreak >= UseRefusalStreak)
                    {
                        _trigger ??= $"use-refusal: {_refusedStreak} uses in a row answered UseDone(0) with nothing opened ({TargetList()})";
                        _refusedStreak = 0;
                    }
                }
            }
            else if (Ms(now - u.Sent) >= UseUnansweredMs)
            {
                done = true;
                if (!u.Counted) continue;   // a use-with-target nobody answered: just drop it
                _unansweredStreak++;
                if (_unansweredStreak >= UseUnansweredStreak)
                {
                    _trigger ??= $"use-unanswered: {_unansweredStreak} uses in a row got no UseDone within {UseUnansweredMs / 1000} s";
                    _unansweredStreak = 0;
                }
            }
            if (!done)
                _uses[keep++] = u;
        }
        for (int i = keep; i < _useCount; i++) _uses[i] = default;
        _useCount = keep;
    }

    /// <summary>The refused objects, each once (trigger time only).</summary>
    private string TargetList()
    {
        int n = Math.Min(_refusedStreak, _refusedTargets.Length);
        var parts = new System.Collections.Generic.List<string>(n);
        for (int i = 0; i < n; i++)
        {
            string id = $"0x{_refusedTargets[i]:X8}";
            if (!parts.Contains(id)) parts.Add(id);
        }
        return string.Join(", ", parts);
    }

    /// <summary>"too busy" in a u16-length-prefixed Latin-1 string, without allocating.</summary>
    private static bool ContainsTooBusy(byte* p, int len)
    {
        if (len < 2) return false;
        int n = Math.Min(AcPacketWire.ReadU16(p), len - 2);
        byte* s = p + 2;
        ReadOnlySpan<byte> needle = "too busy"u8;
        for (int i = 0; i + needle.Length <= n; i++)
        {
            int k = 0;
            while (k < needle.Length)
            {
                byte c = s[i + k];
                if (c >= (byte)'A' && c <= (byte)'Z') c = (byte)(c + 32);
                if (c != needle[k]) break;
                k++;
            }
            if (k == needle.Length) return true;
        }
        return false;
    }
}

/// <summary>At most one automatic dump per cooldown; manual dumps don't go through this.</summary>
internal sealed class FlightDumpPolicy
{
    private readonly long _cooldownMs;
    private long _lastMs;
    private bool _any;
    private readonly object _lock = new();

    public FlightDumpPolicy(long cooldownMs) { _cooldownMs = cooldownMs; }

    public long CooldownMs => _cooldownMs;
    public int Refused { get; private set; }

    /// <summary>True (and the cooldown starts) when an automatic dump may be written now;
    /// otherwise <paramref name="waitMs"/> is how long until one may.</summary>
    public bool TryAccept(long nowMs, out long waitMs)
    {
        lock (_lock)
        {
            if (_any && nowMs - _lastMs < _cooldownMs)
            {
                waitMs = _cooldownMs - (nowMs - _lastMs);
                Refused++;
                return false;
            }
            _any = true;
            _lastMs = nowMs;
            waitMs = 0;
            return true;
        }
    }
}
