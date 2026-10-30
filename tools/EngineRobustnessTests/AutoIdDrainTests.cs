using RynthCore.Engine.Compatibility;
using RynthCore.Engine.Net;

namespace EngineRobustnessTests;

/// <summary>
/// AutoIdDrain against a fake world and a fake clock: the pack
/// created before the object snapshot is warm still gets appraised, a refused enqueue
/// is kept, and the bounds.
/// </summary>
internal static partial class Program
{
    private sealed class FakeWorld : IAutoIdWorld
    {
        public bool Warm;
        public readonly Dictionary<uint, (uint Container, uint Wielder)> Snapshot = new();
        public readonly HashSet<uint> Cached = new();
        public readonly HashSet<uint> Released = new();
        public bool Refuse;
        public readonly HashSet<uint> Containers = new();
        public readonly List<uint> Looked = new();     // every CollectOwned call
        public readonly List<(uint Id, IdRequester Who, long At)> Sent = new();
        public long Now;

        public bool TryGetOwnership(uint id, out uint container, out uint wielder)
        {
            container = wielder = 0;
            if (!Warm || !Snapshot.TryGetValue(id, out var e)) return false;
            container = e.Container; wielder = e.Wielder;
            return true;
        }
        public bool SnapshotWarm => Warm;
        public int CollectOwned(uint container, Span<uint> dest)
        {
            Looked.Add(container);
            if (!Warm) return 0;
            int n = 0;
            foreach (var kv in Snapshot)
            {
                if (n >= dest.Length) break;
                if (kv.Key != container && (kv.Value.Container == container || kv.Value.Wielder == container)) dest[n++] = kv.Key;
            }
            return n;
        }
        public bool IsLive(uint id) => !Released.Contains(id);
        public bool HasAppraisal(uint id) => Cached.Contains(id);
        public bool IsFresh(uint id) => Cached.Contains(id);
        public bool IsContainer(uint id) => Containers.Contains(id);
        public bool QueueIdentify(uint id, IdRequester who)
        {
            if (Refuse) return false;
            Sent.Add((id, who, Now));
            Cached.Add(id);   // the reply lands
            return true;
        }
    }

    private const uint Me = 0x50000001, SidePack = 0x80000900;

    private static AutoIdDrain.Context Ctx(FakeWorld w, long loginAt, AutoIdMode mode = AutoIdMode.Limited, long settle = 0, bool combat = false) => new()
    {
        Now = w.Now, Mode = mode, PlayerId = Me, OpenContainer = 0, OpenedAtMs = 0, SettleUntilMs = settle, LoginCompleteAtMs = loginAt,
        CombatBusy = combat,
    };

    /// <summary>Runs the 100 ms drain from w.Now to <paramref name="until"/>.</summary>
    private static void RunUntil(AutoIdDrain d, FakeWorld w, long until, long loginAt, AutoIdMode mode = AutoIdMode.Limited, Action? each = null)
    {
        for (; w.Now <= until; w.Now += 100)
        {
            each?.Invoke();
            d.Tick(Ctx(w, loginAt, mode), 0);
        }
    }

    private static void RunAutoIdDrainTests()
    {
        DrainLoginPackWithColdSnapshot();
        DrainLoginSweep();
        DrainUnknownWaitsForSnapshotThenGivesUp();
        DrainRefusedEnqueueIsKept();
        DrainBounds();
        DrainCombatHoldsInventory();
        DrainSweepRetriesAfterOverflow();
        DrainReachesItemsBehindTheEvaluationBudget();
    }

    // Review fix 2026-10-18: more than MaxEvaluatedPerTick items waiting (here the pack waits through
    // combat) used to leave the newest ones, at the end of the list, unlooked-at for as long as the
    // earlier ones kept waiting. The drain now resumes where it stopped.
    private static void DrainReachesItemsBehindTheEvaluationBudget()
    {
        _case = "drain round robin";
        const uint Chest = 0x80000B00, Loot = 0x80000B01;
        var w = new FakeWorld { Now = 1_000_000, Warm = true };
        var d = new AutoIdDrain(w);
        int pack = AutoIdDrain.MaxEvaluatedPerTick + 88;
        for (uint i = 1; i <= pack; i++) { w.Snapshot[0x8000D000 + i] = (Me, 0); d.Offer(0x8000D000 + i, w.Now - 10_000); }
        w.Snapshot[Loot] = (Chest, 0); d.Offer(Loot, w.Now - 10_000);   // newest: last in the list
        AutoIdDrain.Context Fight() => new()
        {
            Now = w.Now, Mode = AutoIdMode.Limited, PlayerId = Me, OpenContainer = Chest, OpenedAtMs = w.Now - 5_000,
            SettleUntilMs = 0, LoginCompleteAtMs = 1, CombatBusy = true,
        };
        d.Tick(Fight(), 0);
        Check(d.PendingCount == pack + 1, $"first tick: the pack waits, the chest item is behind the budget (pending {d.PendingCount})");
        Check(!w.Sent.Any(s => s.Id == Loot), "first tick: the chest item was not reached yet");
        for (int k = 0; k < 4; k++) { w.Now += 100; d.Tick(Fight(), 0); }
        Check(w.Sent.Any(s => s.Id == Loot), "within a few ticks the chest item is reached and sent although the pack keeps waiting");
        Check(!w.Sent.Any(s => (s.Id & 0xFFFFF000) == 0x8000D000), "while the pack, in combat, sent nothing");
        Check(d.PendingCount == pack, $"and the waiting pack is all still pending (pending {d.PendingCount})");
        // Every pack item is looked at in turn: end the fight and they all go out.
        AutoIdDrain.Context Calm() => new()
        {
            Now = w.Now, Mode = AutoIdMode.Limited, PlayerId = Me, OpenContainer = 0, OpenedAtMs = 0,
            SettleUntilMs = 0, LoginCompleteAtMs = 1, CombatBusy = false,
        };
        for (int k = 0; k < 4000 && d.PendingCount > 0; k++) { w.Now += 100; d.Tick(Calm(), 0); }
        Check(d.PendingCount == 0, $"after combat the whole backlog drains (pending {d.PendingCount})");
    }

    // The pack is created at login, before the snapshot is warm; it must still go out.
    private static void DrainLoginPackWithColdSnapshot()
    {
        _case = "drain login pack, cold snapshot";
        var w = new FakeWorld { Now = 10_000 };
        var d = new AutoIdDrain(w);
        // The login create burst: 20 pack items and a side pack with 5 more, created at 10 s.
        var pack = new List<uint>();
        for (uint i = 1; i <= 20; i++) { pack.Add(0x80001000 + i); w.Snapshot[0x80001000 + i] = (Me, 0); }
        w.Snapshot[SidePack] = (Me, 0); w.Containers.Add(SidePack);
        pack.Add(SidePack);
        for (uint i = 1; i <= 5; i++) { pack.Add(0x80002000 + i); w.Snapshot[0x80002000 + i] = (SidePack, 0); }
        foreach (uint id in pack) d.Offer(id, w.Now);
        const long LoginAt = 12_000;
        const long WarmAt = 40_000;   // the snapshot starts 28 s after login-complete (cold for N seconds)

        // Before login completes, and while the snapshot is cold: nothing is sent, nothing dropped.
        RunUntil(d, w, LoginAt - 100, loginAt: 0);
        Check(w.Sent.Count == 0, $"nothing before login-complete, sent {w.Sent.Count}");
        RunUntil(d, w, WarmAt - 100, LoginAt);
        Check(w.Sent.Count == 0, $"nothing while the snapshot is cold, sent {w.Sent.Count}");
        Check(d.PendingCount == pack.Count, $"all {pack.Count} kept while the snapshot is cold, pending {d.PendingCount}");

        w.Warm = true;
        RunUntil(d, w, 120_000, LoginAt);
        var sentIds = w.Sent.Select(s => s.Id).ToHashSet();
        Check(pack.All(sentIds.Contains), $"every pack item eventually sent ({sentIds.Count}/{pack.Count})");
        Check(w.Sent.All(s => s.Who == IdRequester.AutoIdInventory), "all as autoid-inventory");
        Check(w.Sent.Min(s => s.At) >= LoginAt + AutoIdPolicy.DeferMs, "none before login + DeferMs");
        int worst = w.Sent.Max(s => w.Sent.Count(t => t.At >= s.At && t.At < s.At + 1_000));
        Check(worst <= 5, $"at most 5 a second, got {worst}");
    }

    // The one-shot sweep at login + DeferMs, also after a hot reload (a new login stamp).
    private static void DrainLoginSweep()
    {
        _case = "drain login sweep";
        var w = new FakeWorld { Now = 100_000, Warm = true };
        var d = new AutoIdDrain(w);
        for (uint i = 1; i <= 8; i++) w.Snapshot[0x80003000 + i] = (Me, 0);           // main pack, never offered
        w.Snapshot[SidePack] = (Me, 0); w.Containers.Add(SidePack);
        for (uint i = 1; i <= 4; i++) w.Snapshot[0x80004000 + i] = (SidePack, 0);     // side pack contents
        w.Snapshot[0x80005000] = (0, Me);                                               // worn
        w.Cached.Add(0x80003001);                                                       // already appraised
        w.Snapshot[0x80006000] = (0, 0);                                                // a ground item: not ours
        long loginAt = 100_000;

        RunUntil(d, w, loginAt + AutoIdPolicy.DeferMs - 100, loginAt);
        Check(w.Sent.Count == 0, $"no sweep before login + DeferMs, sent {w.Sent.Count}");
        RunUntil(d, w, loginAt + AutoIdPolicy.DeferMs + 10_000, loginAt);
        var ids = w.Sent.Select(s => s.Id).ToHashSet();
        Check(Enumerable.Range(2, 7).All(i => ids.Contains(0x80003000 + (uint)i)), "the uncached main-pack items are swept");
        Check(!ids.Contains(0x80003001), "a cached one is not");
        Check(Enumerable.Range(1, 4).All(i => ids.Contains(0x80004000 + (uint)i)), "the side pack's contents are swept");
        Check(ids.Contains(SidePack) && ids.Contains(0x80005000), "the side pack itself and worn gear are swept");
        Check(!ids.Contains(0x80006000), "a ground item is not");
        // Only containers are looked into: the player, then the side pack; never a sword.
        Check(w.Looked.All(x => x == Me || x == SidePack) && w.Looked.Contains(SidePack),
            $"only containers looked into ({string.Join(",", w.Looked.Distinct().Select(x => x.ToString("X8")))})");
        Check(d.Swept == 13, $"13 swept (7 main pack, the side pack, its 4, 1 worn), got {d.Swept}");
        int count = w.Sent.Count;

        // The same login: no second sweep, even with new uncached items.
        w.Snapshot[0x80007000] = (Me, 0);
        RunUntil(d, w, w.Now + 5_000, loginAt);
        Check(w.Sent.Count == count, "one sweep per login");

        // A hot reload (MarkAlreadyComplete) stamps a new login: swept again after its defer.
        long reloadAt = w.Now;
        RunUntil(d, w, reloadAt + AutoIdPolicy.DeferMs + 2_000, reloadAt);
        Check(w.Sent.Any(s => s.Id == 0x80007000), "a new login stamp sweeps again");

        // All mode never sweeps (it appraises every create anyway).
        var w2 = new FakeWorld { Now = 100_000, Warm = true };
        w2.Snapshot[0x80008000] = (Me, 0);
        var d2 = new AutoIdDrain(w2);
        RunUntil(d2, w2, 100_000 + AutoIdPolicy.DeferMs + 5_000, 100_000, AutoIdMode.All);
        Check(w2.Sent.Count == 0, "All mode: no sweep");
    }

    private static void DrainUnknownWaitsForSnapshotThenGivesUp()
    {
        _case = "drain unknown";
        // Warm snapshot, object not in it: dropped at MinAgeMs (a creature gone, or never placed).
        var w = new FakeWorld { Now = 1_000_000, Warm = true };
        var d = new AutoIdDrain(w);
        d.Offer(0x80009001, w.Now);
        RunUntil(d, w, w.Now + AutoIdPolicy.MinAgeMs + 200, loginAt: 1);
        Check(d.PendingCount == 0 && d.SkipMode == 1, $"warm snapshot: dropped after MinAgeMs (pending {d.PendingCount}, skipMode {d.SkipMode})");

        // Cold snapshot that never warms: kept until UnknownMaxWaitMs, then dropped.
        var c = new FakeWorld { Now = 1_000_000 };
        var e = new AutoIdDrain(c);
        e.Offer(0x80009002, c.Now);
        long t0 = c.Now;
        RunUntil(e, c, t0 + AutoIdPolicy.UnknownMaxWaitMs - 200, loginAt: 1);
        Check(e.PendingCount == 1, "cold snapshot: still kept just before UnknownMaxWaitMs");
        RunUntil(e, c, t0 + AutoIdPolicy.UnknownMaxWaitMs + 200, loginAt: 1);
        Check(e.PendingCount == 0 && e.SkipMode == 1, "cold snapshot: dropped at UnknownMaxWaitMs");
    }

    // A refused enqueue (transition, full ring) is kept, not counted, and sent later.
    private static void DrainRefusedEnqueueIsKept()
    {
        _case = "drain refused enqueue";
        var w = new FakeWorld { Now = 1_000_000, Warm = true, Refuse = true };
        var d = new AutoIdDrain(w);
        for (uint i = 1; i <= 3; i++) { w.Snapshot[0x8000A000 + i] = (0, 0); d.Offer(0x8000A000 + i, w.Now); }
        int sent = d.Tick(Ctx(w, 1, AutoIdMode.All), 0);
        Check(sent == 0, $"a refused send is not counted as sent, got {sent}");
        Check(d.PendingCount == 3, $"all three kept, pending {d.PendingCount}");
        Check(d.Refused == 1, $"the first refusal ends the tick's sending (1 refused), got {d.Refused}");
        w.Now += 100;
        w.Refuse = false;
        sent = d.Tick(Ctx(w, 1, AutoIdMode.All), 0);
        Check(sent == 3 && w.Sent.Count == 3 && d.PendingCount == 0, $"sent once accepted (sent {sent}, pending {d.PendingCount})");

        // The inventory cap counts only a send that was taken.
        var v = new FakeWorld { Now = 1_000_000, Warm = true, Refuse = true };
        var e = new AutoIdDrain(v);
        v.Snapshot[0x8000B001] = (Me, 0); v.Snapshot[0x8000B002] = (Me, 0);
        e.Offer(0x8000B001, 0); e.Offer(0x8000B002, 0);
        e.Tick(Ctx(v, 1), 0);
        v.Refuse = false; v.Now += 100;
        e.Tick(Ctx(v, 1), 0);
        Check(v.Sent.Count == 1, $"a refused inventory send did not use up the rate slot (sent {v.Sent.Count})");
    }

    // All mode keeps the old unbounded backlog; Limited is capped; _incoming is bounded.
    private static void DrainBounds()
    {
        _case = "drain bounds";
        var w = new FakeWorld { Now = 1_000, Warm = true };
        var d = new AutoIdDrain(w);
        int n = AutoIdDrain.MaxPendingLimited + 500;
        for (uint i = 0; i < n; i++) { w.Snapshot[0x81000000 + i] = (0, 0); d.Offer(0x81000000 + i, w.Now); }
        d.Tick(Ctx(w, 0, AutoIdMode.All), AutoIdDrain.MaxPerTick);   // no sends: just take them in
        Check(d.PendingCount == n && d.Overflow == 0, $"All: all {n} pending (got {d.PendingCount}, overflow {d.Overflow})");

        var l = new FakeWorld { Now = 1_000, Warm = true };
        var e = new AutoIdDrain(l);
        for (uint i = 0; i < n; i++) e.Offer(0x82000000 + i, l.Now);
        e.Tick(Ctx(l, 0, AutoIdMode.Limited), AutoIdDrain.MaxPerTick);
        Check(e.PendingCount == AutoIdDrain.MaxPendingLimited && e.Overflow == 500,
            $"Limited: capped at {AutoIdDrain.MaxPendingLimited}, 500 counted (got {e.PendingCount}, {e.Overflow})");

        // While the drain is held (a long death), _incoming keeps the newest MaxIncoming.
        var h = new FakeWorld { Now = 1_000, Warm = true };
        var f = new AutoIdDrain(h);
        int over = 37;
        for (uint i = 0; i < AutoIdDrain.MaxIncoming + over; i++) f.Offer(0x83000000 + i, h.Now);
        Check(f.IncomingDropped == over, $"{over} oldest dropped and counted, got {f.IncomingDropped}");
        f.Tick(Ctx(h, 0, AutoIdMode.All), AutoIdDrain.MaxPerTick);
        Check(f.PendingCount == AutoIdDrain.MaxIncoming, $"the newest {AutoIdDrain.MaxIncoming} kept, got {f.PendingCount}");
    }

    // The pack waits through combat (a cast or attack in flight); kept, not
    // dropped; an open container is not held.
    private static void DrainCombatHoldsInventory()
    {
        _case = "drain combat";
        const uint Chest = 0x80000A00, Loot = 0x80000A01;
        var w = new FakeWorld { Now = 1_000_000, Warm = true };
        var d = new AutoIdDrain(w);
        for (uint i = 1; i <= 3; i++) { w.Snapshot[0x8000C000 + i] = (Me, 0); d.Offer(0x8000C000 + i, w.Now - 10_000); }
        w.Snapshot[Loot] = (Chest, 0); d.Offer(Loot, w.Now - 10_000);
        AutoIdDrain.Context Fight(bool combat) => new()
        {
            Now = w.Now, Mode = AutoIdMode.Limited, PlayerId = Me, OpenContainer = Chest, OpenedAtMs = w.Now - 5_000,
            SettleUntilMs = 0, LoginCompleteAtMs = 1, CombatBusy = combat,
        };
        for (int k = 0; k < 50; k++) { d.Tick(Fight(true), 0); w.Now += 100; }
        Check(w.Sent.Count == 1 && w.Sent[0].Id == Loot, $"in combat: only the open container's item is sent ({w.Sent.Count})");
        Check(d.PendingCount == 3, $"the pack is kept, pending {d.PendingCount}");
        Check(d.SkipCombat == 3, $"each counted once, got {d.SkipCombat}");
        for (int k = 0; k < 20; k++) { d.Tick(Fight(false), 0); w.Now += 100; }
        Check(w.Sent.Count == 4 && d.PendingCount == 0, $"out of combat: the pack goes out (sent {w.Sent.Count}, pending {d.PendingCount})");
        Check(w.Sent.Skip(1).All(s => s.Who == IdRequester.AutoIdInventory), "as autoid-inventory");
    }

    // A sweep that overflowed the pending list is not marked done; it runs again once
    // there is room, and every pack item is still sent.
    private static void DrainSweepRetriesAfterOverflow()
    {
        _case = "drain sweep overflow";
        long loginAt = 100_000, sweepAt = loginAt + AutoIdPolicy.DeferMs;
        var pack = new List<uint>();
        for (uint i = 1; i <= 5; i++) pack.Add(0x8000D000 + i);

        // Full: the sweep does not walk (no overflow counted) and is not done.
        var w = new FakeWorld { Now = sweepAt, Warm = true };
        foreach (uint id in pack) w.Snapshot[id] = (Me, 0);
        var d = new AutoIdDrain(w);
        for (uint i = 0; i < AutoIdDrain.MaxPendingLimited; i++) d.Offer(0x84000000 + i, w.Now);   // young, unplaced: they wait
        d.Tick(Ctx(w, loginAt), AutoIdDrain.MaxPerTick);
        Check(d.PendingCount == AutoIdDrain.MaxPendingLimited && d.Swept == 0 && d.Overflow == 0,
            $"pending full: no sweep yet (pending {d.PendingCount}, swept {d.Swept}, overflow {d.Overflow})");
        RunUntil(d, w, sweepAt + 20_000, loginAt);
        Check(pack.All(id => w.Sent.Any(s => s.Id == id)), $"swept once there was room: all 5 sent ({w.Sent.Count})");

        // Partly full: 2 fit, 3 overflow; the sweep runs again and the other 3 go out too.
        var v = new FakeWorld { Now = sweepAt, Warm = true };
        foreach (uint id in pack) v.Snapshot[id] = (Me, 0);
        var e = new AutoIdDrain(v);
        for (uint i = 0; i < AutoIdDrain.MaxPendingLimited - 2; i++) e.Offer(0x85000000 + i, v.Now);
        e.Tick(Ctx(v, loginAt), AutoIdDrain.MaxPerTick);
        Check(e.Swept == 2 && e.Overflow == 3, $"2 fit, 3 overflowed (swept {e.Swept}, overflow {e.Overflow})");
        RunUntil(e, v, sweepAt + 20_000, loginAt);
        Check(pack.All(id => v.Sent.Any(s => s.Id == id)), $"the overflowed 3 are swept again and sent ({v.Sent.Count})");
        Check(e.Swept == 5, $"5 swept in all, got {e.Swept}");
    }
}