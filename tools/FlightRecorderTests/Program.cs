using System.Buffers.Binary;
using System.Diagnostics;
using NetMessageTests;
using RynthCore.Engine.Net;

namespace FlightRecorderTests;

internal static unsafe class Program
{
    private static int _checks, _failed;
    private static string _case = "";

    // Fake clock: one tick = one millisecond.
    private static long _t = 1_000_000;
    private static long Clock() => _t;

    private const uint Player = 0x50000ABC;
    private const uint Corpse = 0x80068ACE;
    private const uint Other = 0x80012345;

    private static int Main(string[] args)
    {
        RingBasics();
        RingWrapAndTruncation();
        RingConcurrent();

        PlayerLearnedAndFilters();
        UsedObjectsKept();
        PositionRateLimit();
        RetransmitDedupe();
        ResendRequestMarker();
        EngineTagging();
        IdentifyTagging();
        EngineRepeatFolding();

        DetectUseRefusal();
        DetectUseRefusalDelayedAnswers();
        NoTriggerWhenCorpsesOpen();
        NoTriggerForDrinksAndKits();
        DetectUnanswered();
        DetectTooBusy();
        DumpPolicy();

        DecodeOutbound();
        DecodeMoveToState();
        DecodeInboundEvents();
        DecodeMotion();
        DecodePositionAndObjects();
        DecodeMarkers();
        DecodeTruncatedAndGarbage();
        DumpFormat();

        foreach (string path in args)
        {
            if (path == "--sample") SampleDump();
            else Replay(path);
        }

        Console.WriteLine();
        Console.WriteLine($"{_checks - _failed}/{_checks} checks passed.");
        Console.WriteLine(_failed == 0 ? "ALL FLIGHT RECORDER TESTS PASSED." : $"{_failed} FAILED.");

        Performance();
        return _failed == 0 ? 0 : 1;
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static void Case(string name)
    {
        _case = name;
        Console.WriteLine($"- {name}");
    }

    private static void Eq<T>(T actual, T expected, string what) =>
        Check(EqualityComparer<T>.Default.Equals(actual, expected), $"{what}: expected {expected}, got {actual}");

    private static void Has(string text, string part, string what) =>
        Check(text.Contains(part, StringComparison.Ordinal), $"{what}: expected \"{part}\" in \"{text}\"");

    private static void Lacks(string text, string part, string what) =>
        Check(!text.Contains(part, StringComparison.Ordinal), $"{what}: did not expect \"{part}\" in \"{text}\"");

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (ok) return;
        _failed++;
        Console.WriteLine($"  FAIL [{_case}] {what}");
    }

    private static FlightCapture NewCapture(int capacity = 1024) => new(capacity, Clock, 1000);

    private static uint _fragSeq = 1000;

    /// <summary>One datagram carrying <paramref name="message"/> in one fragment.</summary>
    private static byte[] One(byte[] message, uint extraFlags = 0, uint? seq = null)
        => Datagram.Build(extraFlags, 0, null, new Frag(seq ?? _fragSeq++, 1, 0, message));

    private static void Feed(FlightCapture c, bool outbound, byte[] datagram)
    {
        fixed (byte* p = datagram)
            c.OnDatagram(outbound, p, datagram.Length);
    }

    private static void In(FlightCapture c, byte[] message, uint extraFlags = 0, uint? seq = null) => Feed(c, false, One(message, extraFlags, seq));
    private static void Out(FlightCapture c, byte[] message, uint extraFlags = 0, uint? seq = null) => Feed(c, true, One(message, extraFlags, seq));

    private static uint _actionSeq = 1;
    private static byte[] Action(uint type, Body body) => new Body().U32(0xF7B1).U32(_actionSeq++).U32(type).Bytes(body.ToArray()).ToArray();
    private static byte[] Event(uint type, Body body) => body.GameEvent(type, Player, 42);

    private static FlightRecord[] Recs(FlightCapture c) => c.Ring.Snapshot();
    private static string[] Lines(FlightCapture c) => Recs(c).Select(r => FlightFormat.Arrow(r.Entry.Dir) + " " + FlightFormat.Describe(r)).ToArray();
    private static string Last(FlightCapture c) { var r = Recs(c); return r.Length == 0 ? "" : FlightFormat.Arrow(r[^1].Entry.Dir) + " " + FlightFormat.Describe(r[^1]); }
    private static int Count(FlightCapture c, FlightDir dir) => Recs(c).Count(r => r.Entry.Dir == dir);

    private static void LoginAs(FlightCapture c) => In(c, Event(0x01C7, new Body().U32(0)));   // any game event names the player

    // Message builders (server side as the server writes them, client side as the client does).
    private static byte[] UseDone(uint err) => Event(0x01C7, new Body().U32(err));
    private static byte[] ViewContents(uint container, params uint[] items)
    {
        var b = new Body().U32(container).U32((uint)items.Length);
        foreach (uint i in items) b.U32(i).U32(0);
        return Event(0x0196, b);
    }
    private static byte[] WeenieError(uint code) => Event(0x028A, new Body().U32(code));
    private static byte[] Use(uint obj) => Action(0x0036, new Body().U32(obj));
    private static byte[] UseWithTarget(uint src, uint dst) => Action(0x0035, new Body().U32(src).U32(dst));

    /// <summary>UpdateMotion for <paramref name="obj"/>: header, then a state with the given commands.</summary>
    private static byte[] MotionState(uint obj, ushort style, ushort forward = 0, (ushort Cmd, float Speed)[]? cmds = null, bool autonomous = false)
    {
        cmds ??= Array.Empty<(ushort, float)>();
        uint flags = 0x1 | (forward != 0 ? 0x2u : 0);
        var b = new Body().U32(0xF74C).U32(obj).U16(3).U16(7).U16(9).Bytes(autonomous ? (byte)1 : (byte)0, 0)   // aligned to 16
            .Bytes(0, 0).U16(style)                    // movement type 0 (state), flags, style
            .U32(flags | ((uint)cmds.Length << 7)).U16(style);
        if (forward != 0) b.U16(forward);
        foreach (var (cmd, speed) in cmds) b.U16(cmd).U16(0x8005).F32(speed);
        while (b.Length % 4 != 0) b.Bytes(0);
        return b.ToArray();
    }

    private static byte[] MotionMoveToObject(uint obj, uint target)
        => new Body().U32(0xF74C).U32(obj).U16(3).U16(8).U16(10).Bytes(0, 0)
            .Bytes(6, 0).U16(0x3C)
            .U32(target).U32(0xA9B40017).F32(10).F32(20).F32(0)
            .U32(0x1EE0F).F32(0.6f).F32(0).F32(15).F32(1).F32(15).F32(0).F32(1.5f).ToArray();

    private static byte[] MotionTurnToObject(uint obj, uint target)
        => new Body().U32(0xF74C).U32(obj).U16(3).U16(9).U16(11).Bytes(0, 0)
            .Bytes(8, 0).U16(0x3D)
            .U32(target).F32(90).U32(0x2).F32(1).F32(90).ToArray();

    private static byte[] UpdatePosition(uint obj, uint cell, ushort posSeq, ushort tele = 1, ushort force = 1)
        => new Body().U32(0xF748).U32(obj).U32(0x04 | 0x10 | 0x20).U32(cell).F32(1).F32(2).F32(3).F32(1).F32(0)
            .U16(5).U16(posSeq).U16(tele).U16(force).ToArray();

    private static byte[] AutoPos(uint cell, ushort inst = 5, ushort srv = 6, ushort tele = 1, ushort force = 1)
        => Action(0xF753, new Body().U32(cell).F32(84.5f).F32(12.25f).F32(0).F32(1).F32(0).F32(0).F32(0)
            .U16(inst).U16(srv).U16(tele).U16(force).Bytes(1, 0, 0, 0));

    // ── ring ────────────────────────────────────────────────────────────────

    private static void RingBasics()
    {
        Case("ring: order, kinds, names");
        var ring = new FlightRing(16);
        byte[] m = { 1, 2, 3, 4, 5 };
        fixed (byte* p = m) ring.WriteMessage(10, FlightDir.In, FlightFlags.None, p, m.Length, m.Length, 7, 0);
        ring.WriteMark(11, FlightMark.EngineCall, "UseObject", 0x80000001, 0, 0, default);
        ring.WriteMark(12, FlightMark.Note, null, 0, 0, 0, "hello");
        var recs = ring.Snapshot();
        Eq(recs.Length, 3, "three entries");
        Eq(recs[0].Entry.Dir, FlightDir.In, "first is the message");
        Eq(recs[0].Entry.Len, (ushort)5, "message length kept");
        Eq(recs[0].Entry.A, 7u, "fragment sequence kept");
        Eq(recs[1].Name, "UseObject", "marker name");
        Eq(recs[2].Entry.Stamp, 12L, "stamp");
        Has(FlightFormat.Describe(recs[2]), "note: hello", "note text");
        Eq(ring.Total, 3, "total");
    }

    private static void RingWrapAndTruncation()
    {
        Case("ring: wrap keeps the newest Capacity, long messages are cut");
        var ring = new FlightRing(16);
        for (uint i = 1; i <= 40; i++) ring.WriteMark(i, FlightMark.Note, null, i, 0, 0, "x");
        var recs = ring.Snapshot();
        Eq(recs.Length, 16, "capacity entries");
        Eq(recs[0].Entry.A, 25u, "oldest kept is #25");
        Eq(recs[^1].Entry.A, 40u, "newest is #40");
        Check(recs.Zip(recs.Skip(1)).All(p => p.First.Entry.Serial < p.Second.Entry.Serial), "oldest first");

        byte[] big = new byte[300];
        for (int i = 0; i < big.Length; i++) big[i] = (byte)i;
        fixed (byte* p = big) ring.WriteMessage(99, FlightDir.Out, FlightFlags.None, p, big.Length, big.Length, 1, 0);
        var last = ring.Snapshot()[^1].Entry;
        Eq((int)last.Len, FlightEntry.DataCap, "kept DataCap bytes");
        Eq(last.FullLen, (ushort)300, "full length remembered");
        Check((last.Flags & FlightFlags.Truncated) != 0, "flagged truncated");

        string longText = new string('a', 500);
        ring.WriteMark(100, FlightMark.Note, null, 0, 0, 0, longText);
        Has(FlightFormat.Describe(ring.Snapshot()[^1]), "...", "long note cut with ...");
    }

    private static void RingConcurrent()
    {
        Case("ring: 4 writers and a reader, every copied entry is whole");
        var ring = new FlightRing(256);
        int stop = 0;
        long torn = 0, seen = 0;
        var writers = Enumerable.Range(0, 4).Select(w => new Thread(() =>
        {
            byte[] buf = new byte[64];
            uint n = 0;
            while (Volatile.Read(ref stop) == 0)
            {
                n++;
                uint v = (uint)(w << 24) | n;
                for (int i = 0; i < buf.Length; i += 4) BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(i), v);
                fixed (byte* p = buf) ring.WriteMessage(v, FlightDir.In, FlightFlags.None, p, buf.Length, buf.Length, v, v);
            }
        })).ToList();
        writers.ForEach(t => t.Start());
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 400)
        {
            foreach (var r in ring.Snapshot())
            {
                seen++;
                FlightEntry e = r.Entry;
                uint v = e.A;
                bool ok = e.B == v && e.Stamp == v;
                for (int i = 0; i < e.Len && ok; i += 4)
                    ok = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(e.Data + i, 4)) == v;
                if (!ok) torn++;
            }
        }
        Volatile.Write(ref stop, 1);
        writers.ForEach(t => t.Join());
        Check(seen > 1000, $"read many entries ({seen})");
        Eq(torn, 0L, "torn entries");
    }

    // ── what is kept ────────────────────────────────────────────────────────

    private static void PlayerLearnedAndFilters()
    {
        Case("capture: the player id comes from game events; other objects' motion is not kept");
        var c = NewCapture();
        In(c, MotionState(Player, 0x3D));                        // before the id is known: dropped
        Eq(Count(c, FlightDir.In), 0, "nothing kept before the player is known");
        LoginAs(c);
        Eq(c.PlayerId, Player, "player id learned");
        In(c, MotionState(Player, 0x3D));
        In(c, MotionState(Other, 0x3C));                         // a monster
        In(c, Event(0x0013, new Body().U32(0)));                  // PlayerDescription: not wanted
        In(c, new Body().Str("hi").Str("Bob").U32(Other).U32(2).Message(0x02BB));   // local chat: never kept
        In(c, WeenieError(0x001D));
        string[] lines = Lines(c);
        Eq(lines.Length, 3, "UseDone (login), player motion, WeenieError");
        Check(lines.Any(l => l.Contains("UpdateMotion 0x50000ABC")), "player motion kept");
        Check(!lines.Any(l => l.Contains("0x80012345")), "monster motion not kept");
        Check(!lines.Any(l => l.Contains("Bob")), "chat not kept");
    }

    private static void UsedObjectsKept()
    {
        Case("capture: messages about an object the character just used are kept, for 20 s");
        var c = NewCapture();
        LoginAs(c);
        Out(c, Use(Corpse));
        In(c, new Body().U32(Corpse).U16(1).Message(0xF747));            // delete
        In(c, new Body().U32(Other).U16(1).Message(0xF747));             // someone else's
        In(c, MotionState(Corpse, 0x3D));                                 // a door opening, say
        string all = string.Join("\n", Lines(c));
        Has(all, $"ObjectDelete 0x{Corpse:X8}", "used object's delete kept");
        Lacks(all, $"ObjectDelete 0x{Other:X8}", "other delete not kept");
        Has(all, $"UpdateMotion 0x{Corpse:X8}", "used object's motion kept");
        _t += 21_000;
        int before = Recs(c).Length;
        In(c, new Body().U32(Corpse).U16(2).Message(0xF747));
        Eq(Recs(c).Length, before, "forgotten after 20 s");
    }

    private static void PositionRateLimit()
    {
        Case("capture: AutonomousPosition / the player's UpdatePosition are rate-limited, changes always kept");
        var c = NewCapture();
        LoginAs(c);
        int start = Recs(c).Length;
        for (int i = 0; i < 10; i++) { Out(c, AutoPos(0xA9B40017)); _t += 100; }
        Eq(Recs(c).Length - start, 1, "one kept in a second");
        _t += 2000;
        Out(c, AutoPos(0xA9B40017));
        var last = Recs(c)[^1];
        Eq(last.Entry.B, 9u, "the next kept one counts the 9 skipped");
        Has(FlightFormat.Describe(last), "(+9 like it not kept)", "described");
        Out(c, AutoPos(0xA9B40018));
        Has(Last(c), "@0xA9B40018", "cell change kept at once");
        Out(c, AutoPos(0xA9B40018, force: 2));
        Has(Last(c), "f2", "force-position change kept at once");

        int n = Recs(c).Length;
        for (ushort i = 0; i < 5; i++) In(c, UpdatePosition(Player, 0xA9B40018, (ushort)(10 + i)));
        Eq(Recs(c).Length - n, 1, "server positions for the player: one kept");
        In(c, UpdatePosition(Player, 0xA9B40018, 20, tele: 2));
        Has(Last(c), "t2", "teleport sequence change kept");
        In(c, UpdatePosition(Other, 0xA9B40018, 21));
        Lacks(Last(c), "0x80012345", "other objects' positions not kept");
    }

    private static void RetransmitDedupe()
    {
        Case("capture: a retransmitted copy of a message already seen is not kept twice");
        var c = NewCapture();
        LoginAs(c);
        byte[] msg = UseDone(0);
        In(c, msg, seq: 777);
        In(c, msg, Datagram.Retransmission, seq: 777);
        In(c, msg, Datagram.Retransmission, seq: 778);         // first copy lost: kept, flagged
        var recs = Recs(c);
        Eq(recs.Count(r => r.Entry.Dir == FlightDir.In), 3, "login + one + the unseen retransmit");
        Has(FlightFormat.Describe(recs[^1]), "[retransmitted]", "flagged");
    }

    private static void ResendRequestMarker()
    {
        Case("capture: a resend request in a packet header becomes a marker (rate-limited)");
        var c = NewCapture();
        byte[] nak = new Body().U32(1).U32(0x1000).U32(0).U16(1).U16(0).U16(12).U16(0).U32(2).U32(555).U32(556).ToArray();
        Feed(c, false, nak);
        Feed(c, false, nak);
        var marks = Recs(c).Where(r => r.Entry.Dir == FlightDir.Mark).ToArray();
        Eq(marks.Length, 1, "one marker within 500 ms");
        Has(FlightFormat.Describe(marks[0]), "server asks for 2 lost packet(s) again (first seq 555)", "described");
        _t += 600;
        Feed(c, true, nak);
        Has(Last(c), "client asks", "client side named");
        var s = new FlightSummary();
        c.TakeCounters(ref s);
        Eq(s.InResendRequests, (ushort)2, "both server requests counted");
        Eq(s.OutResendRequests, (ushort)1, "client request counted");
        Eq(s.InPackets, (ushort)2, "packets counted");
    }

    private static void EngineTagging()
    {
        Case("capture: an action right after the engine's call is [eng], otherwise [cli]");
        var c = NewCapture();
        LoginAs(c);
        c.EngineCall("UseObject", Corpse, 0);
        _t += 20;
        Out(c, Use(Corpse));
        Has(Last(c), "[eng] Use 0x80068ACE", "engine use");
        Out(c, Use(Corpse));
        Has(Last(c), "[cli] Use", "a second use without a call is the client's");
        c.EngineCall("UseObject", Corpse, 0);
        _t += 2000;
        Out(c, Use(Corpse));
        Has(Last(c), "[cli]", "too late to match");
        c.EngineCall("CastSpell", Other, 1234);
        Out(c, Action(0x004A, new Body().U32(Other).U32(1234)));
        Has(Last(c), "[eng] CastTargetedSpell spell 1234 on 0x80012345", "cast matched by spell");
        c.EngineCall("ChangeCombatMode", 1, 0);
        Out(c, Action(0x0053, new Body().U32(8)));
        Has(Last(c), "[cli] ChangeCombatMode Magic", "different mode does not match");
        c.EngineCall("SetAutoRun", 1, 0);
        Out(c, Action(0xF61C, new Body().U32(0)));
        Has(Last(c), "[eng] MoveToState", "movement matched by time");
        c.EngineCall("GiveObjectTo", Corpse, Other);
        Out(c, Action(0x00CD, new Body().U32(Other).U32(Corpse).U32(1)));
        Has(Last(c), "[eng] GiveObjectRequest 0x80068ACE x1 to 0x80012345", "give matched on the object");
        string engineLines = string.Join("\n", Lines(c).Where(l => l.StartsWith("**")));
        Has(engineLines, "engine UseObject(0x80068ACE, 0)", "engine call markers written");
    }

    private static byte[] Identify(uint obj) => Action(0x00C8, new Body().U32(obj));

    private static void IdentifyTagging()
    {
        Case("capture: an IdentifyObject is [eng:<who>] after an engine send for that id, otherwise [cli]");
        var c = NewCapture();
        LoginAs(c);
        int marks = Count(c, FlightDir.Mark);
        c.EngineSend((byte)IdRequester.AutoIdContainer, Corpse);
        Eq(Count(c, FlightDir.Mark), marks, "an engine send writes no marker line");
        _t += 30;
        Out(c, Identify(Corpse));
        Has(Last(c), "[eng:autoid-container] IdentifyObject 0x80068ACE", "autoid container send");
        Eq(c.IdentifyForeignTotal, 0L, "not counted as foreign");

        Out(c, Identify(Corpse));
        Has(Last(c), "[cli] IdentifyObject", "a second 0xC8 with no engine send is AC's or Decal's");
        Eq(c.IdentifyForeignTotal, 1L, "counted as foreign");

        c.EngineSend((byte)IdRequester.Plugin, Other);
        Out(c, Identify(Corpse));
        Has(Last(c), "[cli]", "an engine send for another id does not tag this one");
        Out(c, Identify(Other));
        Has(Last(c), "[eng:plugin] IdentifyObject 0x80012345", "plugin send matched by id");

        c.EngineSend((byte)IdRequester.AutoIdTarget, Other);
        _t += FlightCapture.EngineMatchMs + 1;
        Out(c, Identify(Other));
        Has(Last(c), "[cli]", "too late to match");

        c.EngineSend((byte)IdRequester.AutoIdTarget, Other);
        c.EngineSend((byte)IdRequester.AutoIdInventory, Other);
        Out(c, Identify(Other));
        Has(Last(c), "[eng:autoid-target]", "two sends for one id: the older first");
        Out(c, Identify(Other));
        Has(Last(c), "[eng:autoid-inventory]", "then the newer");

        c.EngineSend(0, Corpse);
        Out(c, Identify(Corpse));
        Has(Last(c), "[cli]", "a send without a requester is not registered");
        Eq(c.EngineSend(0, Corpse), -1, "no requester: no token");

        // Registered before the call (CombatActionHooks.RequestId): a 0xC8 that AC sends
        // synchronously, inside the call, is still tagged.
        int sync = c.EngineSend((byte)IdRequester.AutoIdInventory, Corpse);
        Out(c, Identify(Corpse));
        Has(Last(c), "[eng:autoid-inventory] IdentifyObject 0x80068ACE", "a send made inside the call is tagged");
        c.EngineSendFailed(sync, Corpse); // already matched: a no-op
        Out(c, Identify(Corpse));
        Has(Last(c), "[cli]", "a matched entry is not revived");

        // The call returned false (or threw): its entry is retired, so a later 0xC8 for the
        // same id is AC's or Decal's.
        long foreignFail = c.IdentifyForeignTotal;
        int failed = c.EngineSend((byte)IdRequester.AutoIdInventory, Corpse);
        Check(failed >= 0, "a registration returns a token");
        c.EngineSendFailed(failed, Corpse);
        Out(c, Identify(Corpse));
        Has(Last(c), "[cli]", "a failed send leaves no live entry");
        Eq(c.IdentifyForeignTotal, foreignFail + 1, "and that 0xC8 counts as foreign");

        // Retiring a token whose slot now holds another id leaves that entry alone.
        int stale = c.EngineSend((byte)IdRequester.AutoIdTarget, Other);
        c.EngineSendFailed(stale, Corpse);
        c.EngineSendFailed(-1, Other);
        c.EngineSendFailed(int.MaxValue, Other);
        Out(c, Identify(Other));
        Has(Last(c), "[eng:autoid-target]", "a retire for another id, or a bad token, is a no-op");

        // 200 sends in a burst (a full RequestId queue drained at once) do not push out other
        // engine calls (their own table), and every one of them is matched.
        c.EngineCall("UseObject", Corpse, 0);
        for (uint i = 0; i < 200; i++) c.EngineSend((byte)IdRequester.AutoIdWorld, 0x80100000 + i);
        Out(c, Use(Corpse));
        Has(Last(c), "[eng] Use", "an identify burst leaves the pending use intact");
        long foreignBefore = c.IdentifyForeignTotal;
        int matched = 0;
        for (uint i = 0; i < 200; i++)
        {
            Out(c, Identify(0x80100000 + i));
            if (Last(c).Contains("[eng:autoid-world] IdentifyObject", StringComparison.Ordinal)) matched++;
        }
        Eq(matched, 200, "200 registrations, 200 tagged identifies (oldest to newest)");
        Eq(c.IdentifyForeignTotal, foreignBefore, "none of the burst counted as foreign");

        foreach (IdRequester who in Enum.GetValues<IdRequester>())
        {
            if (who == IdRequester.None) continue;
            Check(IdRequesters.Label((byte)who).Length > 0, $"{who} has a label");
        }
    }

    private static void EngineRepeatFolding()
    {
        Case("capture: identical engine calls within 1 s fold into one line plus a 'repeated' line");
        var c = NewCapture();
        for (int i = 0; i < 30; i++) { c.EngineCall("SetMotion", 0x45000005, 0); _t += 33; }
        Eq(Recs(c).Length, 1, "one line while repeating");
        c.EngineCall("StopCompletely", 0, 0);
        string[] lines = Lines(c);
        Eq(lines.Length, 3, "first, repeated, next");
        Has(lines[1], "repeated 29 more time(s)", "count of folded calls");
        Check(Recs(c)[1].Entry.Stamp < Recs(c)[2].Entry.Stamp, "repeat line at the last repeat's time");
        c.EngineCall("StopCompletely", 0, 0);
        c.FlushEngineRepeats();
        Has(Last(c), "StopCompletely(0, 0) repeated 1 more time(s)", "flush before a dump");
    }

    // ── detectors ───────────────────────────────────────────────────────────

    private static void DetectUseRefusal()
    {
        Case("detector: 4 uses answered UseDone(0) with nothing opened -> use-refusal trigger");
        var c = NewCapture();
        LoginAs(c);
        uint[] corpses = { 0x80000001, 0x80000002, 0x80000003, 0x80000004 };
        foreach (uint id in corpses)
        {
            Out(c, Use(id));
            _t += 60;
            In(c, UseDone(0));
            _t += 1600;
            c.Tick();
        }
        string? t = c.TakeTrigger();
        Check(t != null && t.StartsWith("use-refusal: 4 uses"), $"trigger ({t})");
        Check(t != null && t.Contains("0x80000004"), "names the objects");
        Eq(c.TakeTrigger(), null, "taken once");
    }

    private static void DetectUseRefusalDelayedAnswers()
    {
        Case("detector: the stall shape (each answer arrives after the next use) still triggers");
        var c = NewCapture();
        LoginAs(c);
        for (int i = 0; i < 6; i++)
        {
            Out(c, Use(Corpse));
            _t += 65;
            if (i > 0) In(c, UseDone(0));     // answers the previous use
            _t += 1500;
            c.Tick();
        }
        _t += 2000;
        c.Tick();
        Check(c.TakeTrigger()?.StartsWith("use-refusal") == true, "trigger");
    }

    private static void NoTriggerWhenCorpsesOpen()
    {
        Case("detector: opens that work never trigger; one open resets the streak");
        var c = NewCapture();
        LoginAs(c);
        for (int i = 0; i < 10; i++)
        {
            uint id = 0x80001000u + (uint)i;
            Out(c, Use(id));
            _t += 200;
            In(c, MotionTurnToObject(Player, id));
            In(c, UseDone(0));
            In(c, ViewContents(id, 0x81000001));
            _t += 2000;
            c.Tick();
        }
        Eq(c.TakeTrigger(), null, "no trigger");
        for (int i = 0; i < 3; i++) { Out(c, Use(Corpse)); _t += 60; In(c, UseDone(0)); _t += 1600; c.Tick(); }
        In(c, ViewContents(Other));
        for (int i = 0; i < 3; i++) { Out(c, Use(Corpse)); _t += 60; In(c, UseDone(0)); _t += 1600; c.Tick(); }
        Eq(c.TakeTrigger(), null, "3 + open + 3 does not trigger");
        Eq(c.RefusedStreak, 3, "streak restarted after the open");
    }

    private static void NoTriggerForDrinksAndKits()
    {
        Case("detector: potions (player animation), kits (use-with-target) and errors don't count");
        var c = NewCapture();
        LoginAs(c);
        for (int i = 0; i < 6; i++)
        {
            Out(c, Use(Other));
            _t += 50;
            In(c, MotionState(Player, 0x3D, cmds: new[] { ((ushort)0x1B, 1f) }));   // Drink
            In(c, UseDone(0));
            _t += 2000;
            c.Tick();
        }
        for (int i = 0; i < 6; i++)
        {
            Out(c, UseWithTarget(0x80000777, Player));
            _t += 2400;
            In(c, UseDone(0));
            _t += 2000;
            c.Tick();
        }
        for (int i = 0; i < 6; i++)
        {
            Out(c, Use(Corpse));
            _t += 50;
            In(c, UseDone(0x0037));                    // ObjectGone: an answer with an error
            _t += 2000;
            c.Tick();
        }
        Eq(c.TakeTrigger(), null, "no trigger");
    }

    private static void DetectUnanswered()
    {
        Case("detector: 3 uses with no UseDone at all -> use-unanswered trigger");
        var c = NewCapture();
        LoginAs(c);
        for (int i = 0; i < 3; i++) { Out(c, Use(Corpse)); _t += 5100; c.Tick(); }
        Check(c.TakeTrigger()?.StartsWith("use-unanswered") == true, "trigger");
    }

    private static void DetectTooBusy()
    {
        Case("detector: 3 'too busy' within 20 s -> too-busy trigger (WeenieError or system text)");
        var c = NewCapture();
        LoginAs(c);
        In(c, WeenieError(0x001D)); _t += 15_000;
        In(c, WeenieError(0x001D)); _t += 15_000;
        In(c, WeenieError(0x001D));
        Eq(c.TakeTrigger(), null, "spread over 30 s: none");
        _t += 1000;
        In(c, new Body().Str("You're too busy!").U32(0x1A).Message(0xF7E0));
        Check(c.TakeTrigger()?.StartsWith("too-busy") == true, "three within 20 s");
        In(c, new Body().Str("You cast Heal Self on yourself").U32(0x1A).Message(0xF7E0));
        Eq(c.TakeTrigger(), null, "other system text is not 'too busy'");
    }

    private static void DumpPolicy()
    {
        Case("dump policy: one automatic dump per 10 minutes");
        var p = new FlightDumpPolicy(600_000);
        Check(p.TryAccept(1000, out _), "first");
        Check(!p.TryAccept(2000, out long wait), "second within the cooldown");
        Eq(wait, 599_000L, "wait reported");
        Check(!p.TryAccept(600_999, out _), "just before");
        Check(p.TryAccept(601_000, out _), "after the cooldown");
        Eq(p.Refused, 2, "refusals counted");
    }

    // ── decoding ────────────────────────────────────────────────────────────

    private static string DescribeOut(byte[] message, FlightFlags flags = FlightFlags.None)
    {
        var ring = new FlightRing(16);
        fixed (byte* p = message) ring.WriteMessage(0, FlightDir.Out, flags, p, message.Length, message.Length, 1, 0);
        return FlightFormat.Describe(ring.Snapshot()[0]);
    }

    private static string DescribeIn(byte[] message)
    {
        var ring = new FlightRing(16);
        fixed (byte* p = message) ring.WriteMessage(0, FlightDir.In, FlightFlags.None, p, message.Length, message.Length, 1, 0);
        return FlightFormat.Describe(ring.Snapshot()[0]);
    }

    private static void DecodeOutbound()
    {
        Case("decode: client actions");
        _actionSeq = 400;
        Has(DescribeOut(Use(Corpse)), "Use 0x80068ACE #400", "use");
        Has(DescribeOut(UseWithTarget(0x80000777, Player)), "UseWithTarget 0x80000777 on 0x50000ABC", "use with target");
        Has(DescribeOut(Action(0x0048, new Body().U32(157))), "CastUntargetedSpell spell 157", "untargeted cast");
        Has(DescribeOut(Action(0x004A, new Body().U32(Other).U32(2000))), "CastTargetedSpell spell 2000 on 0x80012345", "targeted cast");
        Has(DescribeOut(Action(0x0053, new Body().U32(2))), "ChangeCombatMode Melee", "combat mode");
        Has(DescribeOut(Action(0x0008, new Body().U32(Other).U32(2).F32(0.5f))), "MeleeAttack 0x80012345 height 2 power 0.5", "melee");
        Has(DescribeOut(Action(0x0019, new Body().U32(Other).U32(Player).I32(0))), "PutItemInContainer 0x80012345 into 0x50000ABC slot 0", "put item");
        Has(DescribeOut(Action(0x001B, new Body().U32(Other))), "DropItem 0x80012345", "drop");
        Has(DescribeOut(Action(0x0195, new Body().U32(Corpse))), "NoLongerViewingContents 0x80068ACE", "no longer viewing");
        Has(DescribeOut(Action(0x01B7, new Body())), "CancelAttack", "cancel attack");
        Has(DescribeOut(Action(0xF61B, new Body().F32(0.8f).F32(0).F32(1).F32(5).U16(1).U16(2).U16(3).U16(4))), "Jump extent 0.8 velocity (0,1,5) seq i1/s2/t3/f4", "jump");
        Has(DescribeOut(AutoPos(0xA9B40017)), "AutonomousPosition @0xA9B40017 (84.5,12.25,0) yaw 0 seq i5/s6/t1/f1 contact=1", "autonomous position");
        string talk = DescribeOut(Action(0x0015, new Body().Str("my secret words")));
        Has(talk, "Talk (", "talk named");
        Lacks(talk, "secret", "talk text not written");
        Has(DescribeOut(Action(0x9999, new Body().U32(1).U32(2))), "action 0x9999 args 0x1 0x2", "unknown action raw");
        Has(DescribeOut(new Body().U32(0x1234).Message(0xF7C8)), "CharacterEnterWorldRequest", "non-action message");
        Has(DescribeOut(Use(Corpse), FlightFlags.Engine), "[eng] Use", "engine tag");
    }

    private static void DecodeMoveToState()
    {
        Case("decode: MoveToState raw motion state, commands, position, sequences");
        // hold key, style, forward command + hold + speed, turn command + speed, one command.
        uint flags = 0x001 | 0x002 | 0x004 | 0x008 | 0x010 | 0x100 | 0x400;
        var b = new Body().U32(flags | (1u << 11))
            .U32(2).U32(0x80000049).U32(0x44000007).U32(2).F32(1.25f).U32(0x6500000D).F32(1.5f)
            .U16(0x31).U16(0x8007).F32(1)
            .U32(0xA9B40017).F32(84.5f).F32(12.25f).F32(0).F32(0.7071f).F32(0).F32(0).F32(0.7071f)
            .U16(11).U16(12).U16(13).U16(14).Bytes(1, 0, 0, 0);
        string s = DescribeOut(Action(0xF61C, b));
        Has(s, "MoveToState hold=Run style=Magic fwd=RunForward(Run)@1.25 turn=TurnRight@1.5", "fields");
        Has(s, "cmds=[MagicHeal@1#7a]", "command with autonomous sequence");
        Has(s, "@0xA9B40017 (84.5,12.25,0) yaw 90", "position");
        Has(s, "seq i11/s12/t13/f14 contact=1", "sequences");
        Lacks(s, "...", "not cut");
        Has(DescribeOut(Action(0xF61C, new Body().U32(0).U32(0xA9B40017).F32(1).F32(2).F32(3).F32(1).F32(0).F32(0).F32(0).U16(1).U16(1).U16(1).U16(1).Bytes(1, 0, 0, 0))),
            "(no motion: stand still)", "empty state");
    }

    private static void DecodeInboundEvents()
    {
        Case("decode: server game events");
        Has(DescribeIn(UseDone(0)), "UseDone err 0x0000 none #42", "use done");
        Has(DescribeIn(UseDone(0x0037)), "UseDone err 0x0037 ObjectGone", "use done error");
        Has(DescribeIn(ViewContents(Corpse, 1, 2, 3)), "ViewContents 0x80068ACE items 3: 0x00000001 0x00000002 0x00000003", "view contents");
        Has(DescribeIn(Event(0x0052, new Body().U32(Corpse))), "CloseGroundContainer 0x80068ACE", "close ground container");
        Has(DescribeIn(WeenieError(0x001D)), "WeenieError 0x001D YoureTooBusy", "too busy");
        Has(DescribeIn(WeenieError(0x0402)), "YourSpellFizzled", "fizzle");
        Has(DescribeIn(Event(0x028B, new Body().U32(0x0036).Str("Interrupted!"))), "WeenieErrorWithString 0x0036 ActionCancelled \"Interrupted!\"", "error with string");
        Has(DescribeIn(Event(0x00A0, new Body().U32(Other).U32(0x0020))), "InventoryServerSaveFailed 0x80012345 err 0x0020", "save failed");
        Has(DescribeIn(Event(0x0022, new Body().U32(Other).U32(Player).I32(3).U32(0))), "InventoryPutObjInContainer 0x80012345 in 0x50000ABC slot 3", "put in container");
        Has(DescribeIn(Event(0x01A7, new Body().U32(0))), "AttackDone err 0x0000", "attack done");
        Has(DescribeIn(Event(0x01B8, new Body())), "CombatCommenceAttack", "commence attack");
        Has(DescribeIn(Event(0x01B1, new Body().Str("Drudge Ravener").U32(1).Bytes(BitConverter.GetBytes(0.4)).U32(37).U32(1).U32(0).U32(0))),
            "AttackerNotification hit 'Drudge Ravener' for 37 (crit) (40% of its max health)", "attacker notification");
        var ench = new Body().U16(2053).U16(1).U16(5).U16(0).U32(400).Bytes(BitConverter.GetBytes(0.0)).Bytes(BitConverter.GetBytes(1800.0)).U32(Player);
        Has(DescribeIn(Event(0x02C2, ench)), "MagicUpdateEnchantment spell 2053 layer 1 duration 1800 s caster 0x50000ABC", "enchantment");
        Has(DescribeIn(Event(0x02C3, new Body().U16(2053).U16(1))), "MagicRemoveEnchantment spell 2053 layer 1", "remove enchantment");
        Has(DescribeIn(Event(0x02EB, new Body().Str("You can't do that"))), "TransientString \"You can't do that\"", "transient string");
        Has(DescribeIn(new Body().Str("You're too busy!").U32(0x1A).Message(0xF7E0)), "ServerMessage \"You're too busy!\" (chat type 26)", "system text");
    }

    private static void DecodeMotion()
    {
        Case("decode: UpdateMotion state, move-to-object, turn-to-object");
        string s = DescribeIn(MotionState(Player, 0x49, forward: 0x03, cmds: new[] { ((ushort)0x31, 1f) }));
        Has(s, "UpdateMotion 0x50000ABC server State style=Magic fwd=Ready cmds=[MagicHeal@1#5a] seq i3/m7/c9", "state");
        Lacks(s, "...", "whole");
        Has(DescribeIn(MotionState(Player, 0x3D, autonomous: true)), "autonomous State style=NonCombat", "autonomous flag");
        string mt = DescribeIn(MotionMoveToObject(Player, Corpse));
        Has(mt, "MoveToObject style=HandCombat target 0x80068ACE origin @0xA9B40017 (10,20,0) params 0x1EE0F dist 0.6", "move to object");
        Has(mt, "runrate 1.5 seq i3/m8/c10", "run rate and sequences");
        Has(DescribeIn(MotionTurnToObject(Player, Corpse)), "TurnToObject style=NonCombat target 0x80068ACE heading 90 params 0x2 speed 1", "turn to object");
    }

    private static void DecodePositionAndObjects()
    {
        Case("decode: positions, teleport, object messages");
        Has(DescribeIn(UpdatePosition(Player, 0xA9B40017, 33, 4, 5)), "UpdatePosition 0x50000ABC @0xA9B40017 (1,2,3) yaw 0 grounded seq i5/p33/t4/f5", "update position");
        Has(DescribeIn(new Body().U16(7).U16(0).Message(0xF751)), "PlayerTeleport seq t7", "teleport");
        Has(DescribeIn(new Body().U32(Corpse).U16(2).U16(0).Message(0xF747)), "ObjectDelete 0x80068ACE i2", "delete");
        Has(DescribeIn(new Body().U32(Player).F32(0).F32(0).F32(5).F32(0).F32(0).F32(0).U16(1).U16(2).Message(0xF74E)), "VectorUpdate 0x50000ABC velocity (0,0,5)", "vector");
        Has(DescribeIn(new Body().U32(Player).U32(0x57).F32(1).Message(0xF755)), "PlayEffect 0x50000ABC script 0x57", "effect");
        Has(DescribeIn(new Body().Bytes(1).U32(Other).U32(9).U32(90).Message(0x0197)), "SetStackSize 0x80012345 size 9", "stack size");
    }

    private static void DecodeMarkers()
    {
        Case("decode: markers and the per-second summary");
        var c = NewCapture();
        c.Mark(FlightMark.BusyIncrement, 1, 1);
        c.Mark(FlightMark.BusyForceClear, 2, 0, text: "with the motion flush");
        c.Mark(FlightMark.BusyWatchdog, 1, 0, 4100, "real m_cBusy stuck");
        c.Mark(FlightMark.GateCleared, 3, Corpse, text: "manual (/rc unlockactions)");
        c.Mark(FlightMark.GateCleared, 0, 0, text: "watchdog");
        c.Mark(FlightMark.EngineInit, 3);
        c.Mark(FlightMark.Trigger, text: "plugin: loot wedge");
        var s = new FlightSummary { RealBusy = 1, ShadowBusy = 0, PendingType = 3, PendingObject = Corpse, Attacking = 0, Gesture = 1, InPackets = 12, InMessages = 30, InKept = 4, OutPackets = 8, OutMessages = 9, OutKept = 2, SendErrors = 1 };
        c.WriteSummary(s);
        string all = string.Join("\n", Lines(c));
        Has(all, "** m_cBusy +1 -> real 1 (engine count 1)", "increment");
        Has(all, "m_cBusy force-cleared (was real 2, engine count 0) - with the motion flush", "force clear");
        Has(all, "busy watchdog (real m_cBusy stuck): real 1, engine count 0, stuck 4100 ms", "watchdog");
        Has(all, "action gate cleared: pending item request type 3 on 0x80068ACE (manual (/rc unlockactions))", "request gate");
        Has(all, "action gate cleared: attacking flag (watchdog)", "attack gate");
        Has(all, "engine generation 3 started (hot reload", "hot reload");
        Has(all, "AUTO DUMP TRIGGER: plugin: loot wedge", "trigger");
        Has(all, "-- 1s: in 12 pk/30 msg (4 kept), out 8 pk/9 msg (2 kept)", "summary counters");
        Has(all, "SEND ERRORS 1", "send errors");
        Has(all, "m_cBusy 1 (engine 0) | item request type 3 on 0x80068ACE | attacking 0 | gesture 1", "summary state");
        var u = new FlightSummary { RealBusy = FlightSummary.Unknown, PendingType = 0xFFFFFFFF, Attacking = 255, Gesture = 255 };
        c.WriteSummary(u);
        Has(Last(c), "m_cBusy ? (engine 0) | item request ? | attacking ? | gesture ?", "unknowns");
    }

    private static void DecodeTruncatedAndGarbage()
    {
        Case("decode: cut-off messages end with '...', garbage never throws");
        // A MoveToState with 12 commands is longer than DataCap.
        var b = new Body().U32(0x002 | (12u << 11)).U32(0x80000049);
        for (int i = 0; i < 12; i++) b.U16(0x31).U16((ushort)i).F32(1);
        b.U32(0xA9B40017).F32(1).F32(2).F32(3).F32(1).F32(0).F32(0).F32(0).U16(1).U16(1).U16(1).U16(1).Bytes(1, 0, 0, 0);
        string s = DescribeOut(Action(0xF61C, b));
        Has(s, "...", "cut off");
        Lacks(s, "undecodable", "no exception");

        var rnd = new Random(7);
        int bad = 0;
        for (int i = 0; i < 20000; i++)
        {
            int len = rnd.Next(0, 130);
            byte[] m = new byte[len];
            rnd.NextBytes(m);
            if (len >= 4 && rnd.Next(2) == 0)
            {
                uint[] ops = { 0xF7B0, 0xF7B1, 0xF74C, 0xF748, 0xF7E0, 0xF751, 0xF755, 0x0197 };
                BinaryPrimitives.WriteUInt32LittleEndian(m, ops[rnd.Next(ops.Length)]);
            }
            string d1 = rnd.Next(2) == 0 ? DescribeIn(m) : DescribeOut(m);
            if (d1.Contains("undecodable")) bad++;
        }
        Eq(bad, 0, "random messages decoded without an exception");

        var c = NewCapture();
        int crashes = 0;
        for (int i = 0; i < 20000; i++)
        {
            byte[] d = new byte[rnd.Next(0, 200)];
            rnd.NextBytes(d);
            if (d.Length >= 20 && rnd.Next(2) == 0)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(4), 0x4);
                BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(16), (ushort)(d.Length - 20));
            }
            try { Feed(c, rnd.Next(2) == 0, d); } catch { crashes++; }
        }
        Eq(crashes, 0, "random datagrams never throw");
    }

    private static void DumpFormat()
    {
        Case("dump: header, legend, one line per entry, time relative to the request");
        var c = NewCapture();
        LoginAs(c);
        c.EngineCall("UseObject", Corpse, 0);
        _t += 5;
        Out(c, Use(Corpse));
        _t += 60;
        In(c, UseDone(0));
        long zero = _t;
        _t += 250;
        c.Mark(FlightMark.Note, text: "after");
        var sw = new StringWriter();
        var recs = c.Ring.Snapshot();
        FlightFormat.WriteDump(sw, recs, zero, new DateTime(2026, 10, 6, 12, 0, 0), 1000, new[] { "Reason: test" });
        string[] lines = sw.ToString().Split(Environment.NewLine);
        Eq(lines[0], "RynthCore flight recorder dump", "title");
        Eq(lines[1], "Reason: test", "header line");
        Has(lines[2], $"Entries: {recs.Length}", "entry count");
        Check(lines.Any(l => l.StartsWith("How to read")), "legend");
        string[] body = lines.Where(l => l.Contains(" ms  ")).ToArray();
        Eq(body.Length, recs.Length, "one line per entry");
        Has(body.First(l => l.Contains("[eng] Use")), "      -60 ms  11:59:59.940  -> [eng] Use 0x80068ACE", "outbound line");
        Has(body.First(l => l.Contains("UseDone") && l.Contains("12:00:00.000")), "        0 ms  12:00:00.000  <- UseDone err 0x0000 none", "inbound line at time 0");
        Has(body.First(l => l.Contains("note")), "     +250 ms  12:00:00.250  ** note: after", "marker after the request");
    }

    // ── sample ──────────────────────────────────────────────────────────────

    /// <summary>"--sample": a made-up corpse-use stall, dumped to the console (what a dump looks like).</summary>
    private static void SampleDump()
    {
        Console.WriteLine();
        Console.WriteLine("Sample dump (synthetic stall):");
        var c = NewCapture(4096);
        LoginAs(c);
        var s = new FlightSummary { RealBusy = 0, PendingType = 0, Attacking = 0, Gesture = 0, InPackets = 14, InMessages = 22, InKept = 3, OutPackets = 9, OutMessages = 7, OutKept = 2 };
        c.WriteSummary(s);
        // A normal open: use, server turns the character, UseDone, contents.
        c.EngineCall("UseObject", 0x80068ACD, 0); _t += 3;
        Out(c, Use(0x80068ACD)); c.Mark(FlightMark.BusyIncrement, 1, 1); _t += 120;
        In(c, MotionTurnToObject(Player, 0x80068ACD)); _t += 180;
        In(c, UseDone(0)); In(c, ViewContents(0x80068ACD, 0x81000001, 0x81000002)); _t += 400;
        Out(c, Action(0x0195, new Body().U32(0x80068ACD))); _t += 900;
        // The onset: use in melee stance, a peace-mode swap 3 ms later.
        c.EngineCall("UseObject", Corpse, 0); _t += 2;
        Out(c, Use(Corpse)); _t += 3;
        c.EngineCall("ChangeCombatMode", 1, 0); _t += 1;
        Out(c, Action(0x0053, new Body().U32(1))); _t += 40;
        In(c, MotionState(Player, 0x3D)); _t += 1500;
        for (int i = 0; i < 4; i++)
        {
            c.EngineCall("UseObject", Corpse, 0); _t += 2;
            Out(c, Use(Corpse)); _t += 65;
            In(c, UseDone(0)); _t += 1600;
            c.Tick();
            c.WriteSummary(s);
        }
        string? why = c.TakeTrigger();
        if (why != null) c.Mark(FlightMark.Trigger, text: "engine: " + why);
        long zero = _t;
        _t += 3000;
        var recs = c.Ring.Snapshot();
        var w = new StringWriter();
        FlightFormat.WriteDump(w, recs, zero, new DateTime(2026, 10, 6, 6, 18, 50), 1000, new[] { "Reason: auto: " + (why ?? "?") });
        Console.WriteLine(w.ToString());
    }

    // ── captures ────────────────────────────────────────────────────────────

    private static void Replay(string path)
    {
        Case($"replay {Path.GetFileName(path)} (server datagrams through the recorder)");
        byte[] all = File.ReadAllBytes(path);
        if (all.Length < 4 || all[0] != 'R' || all[1] != 'N' || all[2] != 'C' || all[3] != '1')
        {
            Check(false, "not an RNC1 capture");
            return;
        }
        var c = new FlightCapture(16384, Clock, 1000);
        int pos = 4, datagrams = 0;
        long firstStamp = -1;
        while (pos + 6 <= all.Length)
        {
            uint stamp = BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(pos));
            int len = BinaryPrimitives.ReadUInt16LittleEndian(all.AsSpan(pos + 4));
            pos += 6;
            if (pos + len > all.Length) break;
            if (firstStamp < 0) firstStamp = stamp;
            _t = 1_000_000 + (stamp - firstStamp);
            fixed (byte* p = &all[pos]) c.OnDatagram(false, p, len);
            pos += len;
            datagrams++;
        }
        var recs = c.Ring.Snapshot();
        Console.WriteLine($"  {datagrams} datagrams, {recs.Length} entries kept, player 0x{c.PlayerId:X8}");
        int undecodable = 0;
        foreach (var r in recs.TakeLast(60))
            Console.WriteLine("  " + FlightFormat.Line(r, recs[^1].Entry.Stamp, DateTime.Now, 1000));
        foreach (var r in recs) if (FlightFormat.Describe(r).Contains("undecodable")) undecodable++;
        Eq(undecodable, 0, "every kept entry decodes");
    }

    // ── cost ────────────────────────────────────────────────────────────────

    private static void Performance()
    {
        Console.WriteLine();
        Console.WriteLine("Cost (hot path, real Stopwatch clock):");
        var c = new FlightCapture(16384, Stopwatch.GetTimestamp, Stopwatch.Frequency);
        LoginAs(c);
        byte[] autoPos = One(AutoPos(0xA9B40017));
        byte[] monsterPos = One(UpdatePosition(Other, 0xA9B40017, 1));
        byte[] playerMotion = One(MotionState(Player, 0x3D));
        byte[] create = One(new byte[400]);
        BinaryPrimitives.WriteUInt32LittleEndian(create.AsSpan(36), 0xF745);   // CreateObject: not kept

        void Run(string what, byte[] d, bool outbound, int n)
        {
            fixed (byte* p = d)
            {
                for (int i = 0; i < 2000; i++) c.OnDatagram(outbound, p, d.Length);   // warm up
                // Up to three tries, the fewest bytes counted: a one-off allocation on this thread inside
                // the timed loop (tiered JIT settling: 5296 bytes in one of four runs, 2026-10-23) is not
                // the hot path's. An allocation per datagram shows in every try (n of them).
                long alloc = long.MaxValue, ticks = 0;
                for (int attempt = 0; attempt < 3 && alloc != 0; attempt++)
                {
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    long t0 = Stopwatch.GetTimestamp();
                    for (int i = 0; i < n; i++) c.OnDatagram(outbound, p, d.Length);
                    ticks = Stopwatch.GetTimestamp() - t0;
                    alloc = Math.Min(alloc, GC.GetAllocatedBytesForCurrentThread() - before);
                }
                double ns = ticks * 1e9 / Stopwatch.Frequency / n;
                Console.WriteLine($"  {what,-44} {ns,7:0} ns/datagram, {alloc} bytes allocated over {n}");
                _case = "cost";
                Eq(alloc, 0L, $"{what}: no allocation");
            }
        }

        Run("AutonomousPosition (rate-limited away)", autoPos, true, 200_000);
        Run("monster UpdatePosition (filtered out)", monsterPos, false, 200_000);
        Run("CreateObject-size message (not kept)", create, false, 200_000);
        Run("player UpdateMotion (kept: 120-byte copy)", playerMotion, false, 200_000);
        Console.WriteLine($"  ring memory: {c.Ring.Bytes / 1024} KB for {c.Ring.Capacity} entries ({sizeof(FlightEntry)} bytes each)");
        Console.WriteLine(_failed == 0 ? "COST CHECKS PASSED." : $"{_failed} FAILED (incl. cost).");
    }
}
