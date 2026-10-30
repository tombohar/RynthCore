using System.Text.RegularExpressions;
using RynthCore.Engine;
using RynthCore.Engine.Compatibility;
using RynthCore.Install;

namespace EngineRobustnessTests;

/// <summary>Checks for the action ring, the mutator gate, appraisal retention, the dump rules and the cast slot.</summary>
internal static partial class Program
{
    private static void RunRingAndGateTests()
    {
        RingTakesAStopWhenFullOfStarts();
        RingRefusesStartsDuringATransition();
        RingStopRunsFirstAfterTheWindow();
        CastStopFindsAnOutOfOrderStop();

        GateTruthTable();
        GateCallSitesAllUseTheSameShape();

        RequestIdNeverMergesIntoAHeldEntry();

        ReleasedDataKeptSoRecreateSkips();
        ReleasedDataStaleOnLongAbsenceIsDroppedAtRecreate();
        DumpBudgetRules();
        ReleasedDataExpiresAndServerDeleteEvicts();

        TeleportMessageStartsTheWindow();

        OutOfMemoryBoxMatchesOnlyAcsBox();
        LauncherHoldsTheKillDuringADump();

        // The cast slot, the dump hold cap and the crash exits
        CastRefusedDuringATransition();
        RingPeekAtWalksWithoutRemoving();
        EnqueueCastPassesTheTransitionState();
        LauncherDumpHoldIsCapped();
        CrashExitExcludesExitMinusOne();
        CrashExitPathsFlushTheCrashLog();
    }

    // ---- the action ring (the real ActionRing, the one AcMainThreadQueue uses) --------

    private readonly record struct Act(string What, long At, bool Teardown);

    private static void RingTakesAStopWhenFullOfStarts()
    {
        _case = "ring reserve";
        var ring = new ActionRing<Act>(256, 32);
        int queued = 0, full = 0;
        for (int i = 0; i < 300; i++)
        {
            var r = ring.TryEnqueue(new Act("start", 1000, false), teardown: false, inTransition: false);
            if (r == ActionRing<Act>.Admit.Queued) queued++;
            else if (r == ActionRing<Act>.Admit.Full) full++;
        }
        Check(queued == 224 && full == 76, $"300 starts: 224 taken, the rest refused at the reserve (got {queued}/{full})");
        Check(ring.TryEnqueue(new Act("stop", 1001, true), teardown: true, inTransition: false) == ActionRing<Act>.Admit.Queued,
            "a stop is still taken");
        for (int i = 0; i < 31; i++)
            ring.TryEnqueue(new Act("cancel", 1002, true), teardown: true, inTransition: false);
        Check(ring.Count == 256, $"the reserve fills only with teardown (count {ring.Count})");
        Check(ring.TryEnqueue(new Act("stop", 1003, true), teardown: true, inTransition: false) == ActionRing<Act>.Admit.Full,
            "a completely full ring refuses even a stop");
    }

    private static void RingRefusesStartsDuringATransition()
    {
        _case = "ring transition";
        var ring = new ActionRing<Act>(256, 32);
        int rejected = 0;
        for (int i = 0; i < 300; i++)
            if (ring.TryEnqueue(new Act("start", 2000, false), teardown: false, inTransition: true) == ActionRing<Act>.Admit.RejectedTransition)
                rejected++;
        Check(rejected == 300 && ring.Count == 0, $"300 starts during a hold are refused, none stored (rejected {rejected}, count {ring.Count})");
        Check(ring.TryEnqueue(new Act("stop", 2001, true), teardown: true, inTransition: true) == ActionRing<Act>.Admit.Queued,
            "a stop during the hold is taken");
    }

    /// <summary>
    /// The flood scenario against the real ring: a bot floods starts before the transition is
    /// seen (the ring fills to the reserve), the player dies, 300 more starts arrive during the
    /// hold, then a stop. Nothing runs during the hold; after it the stale starts are dropped and
    /// the stops are the first things to run.
    /// </summary>
    private static void RingStopRunsFirstAfterTheWindow()
    {
        _case = "ring drain";
        var ring = new ActionRing<Act>(256, 32);
        var s = new WorldTransitionState();
        s.Sample(900, false, false, 0x00AE);
        for (int i = 0; i < 300; i++)
            ring.TryEnqueue(new Act("start", 1000, false), false, s.InTransition(1000));
        s.Sample(1500, true, false, 0x00AE);                                   // dies
        for (int i = 0; i < 300; i++)
            ring.TryEnqueue(new Act("start-held", 2000, false), false, s.InTransition(2000));
        var stopAdmit = ring.TryEnqueue(new Act("stop", 2500, true), true, s.InTransition(2500));
        Check(stopAdmit == ActionRing<Act>.Admit.Queued, $"the stop is accepted ({stopAdmit})");

        var ran = new List<string>();
        int dropped = 0;
        bool heldClean = true;
        for (long now = 1500; now < 20_000; now += 16)
        {
            s.Sample(now, now < 9000, false, 0x00AE);
            int before = ring.Count;
            if (s.CheckHolding(now)) { heldClean &= ring.Count == before; continue; }   // Drain's early return
            while (ring.TryDequeue(out Act e))
            {
                var v = s.Decide(now, e.At, e.Teardown);
                if (v == WorldTransitionState.Verdict.Drop) dropped++;
                else ran.Add(e.What);
            }
        }
        Check(heldClean, "nothing leaves the ring while held");
        Check(ran.Count > 0 && ran[0] == "stop", $"the stop runs first after the window (ran [{string.Join(",", ran.Take(3))}])");
        Check(ran.Count == 1 && dropped == 224, $"the 224 stale starts are dropped, nothing else runs (ran {ran.Count}, dropped {dropped})");
    }

    // ---- the cast drain's stop check (the real CastStopRules over the real ActionRing) ------

    /// <summary>
    /// A stop queued at or before the cast is found wherever it sits in the ring, including
    /// behind an entry stamped after the cast (two producers can land out of time order).
    /// </summary>
    private static void CastStopFindsAnOutOfOrderStop()
    {
        _case = "cast stop check";
        static bool Any(ActionRing<Act> r, long at) =>
            CastStopRules.AnyStopAtOrBefore(r, at, static e => e.At, static e => e.Teardown);

        var empty = new ActionRing<Act>(8, 2);
        Check(!Any(empty, 1000), "an empty ring has no stop");

        var outOfOrder = new ActionRing<Act>(8, 2);
        outOfOrder.TryEnqueue(new Act("start", 1200, false), false, false);   // newer than the cast
        outOfOrder.TryEnqueue(new Act("stop", 900, true), true, false);       // older, queued behind it
        Check(Any(outOfOrder, 1000), "a stop older than the cast, behind a newer entry, is found");

        var onlyNewer = new ActionRing<Act>(8, 2);
        onlyNewer.TryEnqueue(new Act("start", 500, false), false, false);
        onlyNewer.TryEnqueue(new Act("stop", 1500, true), true, false);
        Check(!Any(onlyNewer, 1000), "a stop queued after the cast does not count");

        var sameTime = new ActionRing<Act>(8, 2);
        sameTime.TryEnqueue(new Act("stop", 1000, true), true, false);
        Check(Any(sameTime, 1000), "a stop queued at the cast's own time counts");

        var noStop = new ActionRing<Act>(8, 2);
        noStop.TryEnqueue(new Act("start", 100, false), false, false);
        noStop.TryEnqueue(new Act("start", 200, false), false, false);
        Check(!Any(noStop, 1000), "older entries that are not stops do not count");
        Check(noStop.Count == 2 && outOfOrder.Count == 2, "the check removes nothing");
    }

    // ---- the mutator gate --------------------------------------------------------

    private static void GateTruthTable()
    {
        _case = "mutator gate";
        int asked = 0;
        Func<bool> holding = () => { asked++; return true; };
        Func<bool> clear = () => { asked++; return false; };
        Check(MutatorGate.MustQueue(false, false, false, clear), "off the main thread: queue");
        Check(MutatorGate.MustQueue(false, true, true, clear), "off the main thread, even teardown in a drain: queue");
        Check(!MutatorGate.MustQueue(true, false, false, clear), "main thread, no transition: call now");
        Check(MutatorGate.MustQueue(true, false, false, holding), "main thread in a transition (render-thread host action): queue");
        Check(!MutatorGate.MustQueue(true, true, false, holding), "main thread in a transition, a stop: call now");
        asked = 0;
        Check(!MutatorGate.MustQueue(true, false, true, holding) && asked == 0, "called by a drain: call now, without asking the transition state");
        asked = 0;
        MutatorGate.MustQueue(false, false, false, holding);
        Check(asked == 0, "off the main thread the (main-thread-only) transition state is never asked");
    }

    /// <summary>
    /// Every AC-mutating helper opens with the same gate: no helper may still decide with the old
    /// bare IsOnMainThread() check before an AcMainThreadQueue.Enqueue*, and each stop/cancel
    /// passes teardown: true while every other passes false (or !enabled for the two toggles).
    /// </summary>
    private static void GateCallSitesAllUseTheSameShape()
    {
        _case = "gate call sites";
        string? compat = FindRepoDir(Path.Combine("src", "RynthCore.Engine", "Compatibility"));
        Check(compat != null, "found src\\RynthCore.Engine\\Compatibility by walking up");
        if (compat == null) return;
        string[] files = { "ClientCombatHooks.cs", "CommandInterpreterHooks.cs", "ClientHelperHooks.cs", "CombatActionHooks.cs",
                           "MovementActionHooks.cs", "PlayerPhysicsHooks.cs", "PlayerTrade.cs", "PlayerTraining.cs",
                           "DeleteObjectHooks.cs" };
        var enqueue = new Regex(@"AcMainThreadQueue\.(Enqueue\w+)\(");
        var gate = new Regex(@"AcMainThreadQueue\.MustQueue\(teardown: (true|false|!enabled)\)");
        string[] teardownTrue = { "EnqueueStopCompletely", "EnqueueStopMovement", "EnqueueCancelAttack", "EnqueueCloseContainer" };
        string[] toggles = { "EnqueueSetAutoRun", "EnqueueSetMotion" };
        int sites = 0, bad = 0;
        foreach (string f in files)
        {
            string[] lines = File.ReadAllLines(Path.Combine(compat, f));
            for (int i = 0; i < lines.Length; i++)
            {
                Match m = enqueue.Match(lines[i]);
                if (!m.Success) continue;
                string name = m.Groups[1].Value;
                if (name is "EnqueueWriteToChat" or "EnqueueChatCommand") continue;
                sites++;
                // The gate line is the nearest "if (" line above the enqueue (within 3 lines).
                string? gateLine = null;
                for (int j = i; j >= Math.Max(0, i - 3); j--)
                    if (lines[j].TrimStart().StartsWith("if (", StringComparison.Ordinal)) { gateLine = lines[j]; break; }
                Match g = gateLine == null ? Match.Empty : gate.Match(gateLine);
                string want = teardownTrue.Contains(name) ? "true" : toggles.Contains(name) ? "!enabled" : "false";
                if (!g.Success || g.Groups[1].Value != want || gateLine!.Contains("IsOnMainThread"))
                {
                    bad++;
                    Console.WriteLine($"[gate] {f}:{i + 1} {name}: gate line '{gateLine?.Trim()}' (want teardown: {want})");
                }
            }
        }
        // 45 in PR #8, + SalvageDirect (v82, ClientHelperHooks) + RemoveClientObject (v83, DeleteObjectHooks).
        Check(sites == 47, $"47 gated mutator call sites found (got {sites})");
        Check(bad == 0, $"every one opens with AcMainThreadQueue.MustQueue(teardown: ...) and the right teardown value ({bad} wrong)");
    }

    private static string? FindRepoDir(string relative)
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            string candidate = Path.Combine(dir, relative);
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    // ---- no merge into an entry a transition will drop -------------------------------

    private static void RequestIdNeverMergesIntoAHeldEntry()
    {
        _case = "requestid merge floor";
        var q = new PendingIdQueue(8);
        q.Enqueue(42, 1000, 1, 7_500);
        // A transition held the drains up to 1500: the entry from 1000 will be dropped.
        Check(q.Enqueue(42, 2000, 1, 7_500, mergeOnlyAfterMs: 1500) == PendingIdQueue.Result.Queued,
            "a request after the window does not merge into the doomed entry");
        Check(q.Count == 2, "so it survives the drop");
        Check(q.Enqueue(42, 2100, 1, 7_500, mergeOnlyAfterMs: 1500) == PendingIdQueue.Result.Merged,
            "it merges into the new one");
        var r = new PendingIdQueue(8);
        r.Enqueue(43, 1000, 1, 7_500);
        Check(r.Enqueue(43, 2000, 1, 7_500, mergeOnlyAfterMs: 0) == PendingIdQueue.Result.Merged, "no transition: merges as before");
    }

    // ---- identify data kept across a client release ---------------------------------

    /// <summary>
    /// Wired as the engine wires them: AppraisalHooks' cache (here a set) keeps an object while
    /// ReleasedObjectRetention says so; AutoIdEligibility decides the re-create.
    /// </summary>
    private static void ReleasedDataKeptSoRecreateSkips()
    {
        _case = "release keeps data";
        var cache = new HashSet<uint>();
        var keep = new ReleasedObjectRetention();
        var elig = new AutoIdEligibility();
        long t = 1000;
        Check(elig.OnCreate(50, t, cache.Contains(50)) == AutoIdEligibility.CreateDecision.Appraise, "first create: appraised");
        elig.OnSent(50, t + 100);
        cache.Add(50);                                     // the identify arrived
        // Client release (BeingDeleted): AppraisalHooks.OnClientRelease
        if (!keep.OnRelease(50, t + 60_000)) cache.Remove(50);
        elig.OnClientRelease(50);
        // Re-created 30 s later (out of view and back): AutoIdService.Enqueue (MarkLive, then the decision)
        if (keep.TakeLive(50, t + 90_000)) cache.Remove(50);
        var d = elig.OnCreate(50, t + 90_000, cache.Contains(50));
        Check(d == AutoIdEligibility.CreateDecision.SkipRecent, $"re-created soon after the release with the data kept: skipped ({d})");
        Check(elig.SkippedRecentTotal == 1, $"SkippedRecentCount moves (got {elig.SkippedRecentTotal})");
    }

    /// <summary>
    /// Review fix 2026-10-18: data kept since a release ReleasedObjectRetention.StaleAfterMs ago or
    /// more is dropped when the object is re-created (the client sent no updates meanwhile, so a
    /// stack count could be minutes old), and the re-create is appraised again.
    /// </summary>
    private static void ReleasedDataStaleOnLongAbsenceIsDroppedAtRecreate()
    {
        _case = "release stale";
        var cache = new HashSet<uint>();
        var keep = new ReleasedObjectRetention();
        var elig = new AutoIdEligibility();
        long t = 1000;
        elig.OnCreate(60, t, cache.Contains(60));
        elig.OnSent(60, t + 100);
        cache.Add(60);
        if (!keep.OnRelease(60, t + 1000)) cache.Remove(60);
        elig.OnClientRelease(60);
        Check(keep.IsReleased(60), "released: kept");

        // 5 minutes later it comes back.
        long back = t + 1000 + 5 * 60_000;
        bool stale = keep.TakeLive(60, back);
        Check(stale, "re-created 5 min after the release: its kept data is stale");
        if (stale) cache.Remove(60);   // AppraisalHooks.MarkLive evicts it
        Check(!keep.IsReleased(60), "and it is current again");
        var d = elig.OnCreate(60, back, cache.Contains(60));
        Check(d == AutoIdEligibility.CreateDecision.Appraise, $"the re-create is a candidate again ({d})");

        // Exactly at the bound, and just under it.
        var k2 = new ReleasedObjectRetention();
        k2.OnRelease(1, 10_000); k2.OnRelease(2, 10_000);
        Check(!k2.TakeLive(1, 10_000 + ReleasedObjectRetention.StaleAfterMs - 1), "just under StaleAfterMs: kept");
        Check(k2.TakeLive(2, 10_000 + ReleasedObjectRetention.StaleAfterMs), "at StaleAfterMs: stale");
        Check(!k2.TakeLive(3, 99_999), "never released: not stale");
        Check(!k2.TakeLive(2, 99_999_999), "and a second create finds nothing to expire");
    }

    /// <summary>DumpBudget: the full OUT OF MEMORY dump needs room on the volume and a cap across clients.</summary>
    private static void DumpBudgetRules()
    {
        _case = "dump budget";
        const long GB = 1024L * 1024 * 1024, MB = 1024L * 1024;
        long est = DumpBudget.EstimateFullDumpBytes(3 * GB);
        Check(est == 3 * GB + DumpBudget.FullDumpOverheadBytes, "the estimate is the private bytes plus the overhead");
        Check(DumpBudget.EstimateFullDumpBytes(0) == DumpBudget.FallbackProcessBytes + DumpBudget.FullDumpOverheadBytes,
            "unknown size: the 32-bit maximum");

        Check(DumpBudget.Evaluate(100 * GB, est, 0, 0) == DumpVerdict.Full, "plenty of room, no other dumps: full");
        Check(DumpBudget.Evaluate(-1, est, 0, 0) == DumpVerdict.LowDisk, "free space unknown: not full");
        Check(DumpBudget.Evaluate(est + DumpBudget.MinFreeAfterFullDump - 1, est, 0, 0) == DumpVerdict.LowDisk,
            "one byte short of leaving the margin free: low disk");
        Check(DumpBudget.Evaluate(est + DumpBudget.MinFreeAfterFullDump, est, 0, 0) == DumpVerdict.Full, "exactly the margin: full");
        Check(DumpBudget.Evaluate(2 * GB, est, 0, 0) == DumpVerdict.LowDisk, "a nearly full disk: small dump instead");

        // N clients hitting the OOM box together: placeholders count, so only a few get a full dump.
        int full = 0;
        int count = 0;
        long bytes = 0;
        for (int client = 0; client < 10; client++)
        {
            if (DumpBudget.Evaluate(500 * GB, est, count, bytes) == DumpVerdict.Full) { full++; count++; bytes += 0; /* a placeholder is empty */ }
        }
        Check(full == DumpBudget.MaxFullDumpsAllPids, $"10 clients at once: {DumpBudget.MaxFullDumpsAllPids} full dumps at most, got {full}");
        Check(DumpBudget.Evaluate(500 * GB, est, 2, 12 * GB) == DumpVerdict.TooManyBytes, "two dumps already at 12 GB: the bytes cap");
        Check(DumpBudget.Evaluate(500 * GB, est, DumpBudget.MaxFullDumpsAllPids, 1 * MB) == DumpVerdict.TooManyDumps, "the count cap");
        Check(DumpBudget.Evaluate(1 * GB, est, DumpBudget.MaxFullDumpsAllPids, 0) == DumpVerdict.LowDisk, "low disk is reported first");

        Check(DumpBudget.SmallDumpAllowed(DumpBudget.SmallDumpMinFree) && !DumpBudget.SmallDumpAllowed(DumpBudget.SmallDumpMinFree - 1)
              && !DumpBudget.SmallDumpAllowed(-1), "the small dump needs its own margin, and not an unknown disk");
    }

    private static void ReleasedDataExpiresAndServerDeleteEvicts()
    {
        _case = "release expiry";
        var keep = new ReleasedObjectRetention(10 * 60_000, 4);
        var expired = new List<uint>();
        keep.OnRelease(1, 1000);
        keep.OnRelease(2, 2000);
        Check(keep.Expire(1000 + 10 * 60_000 - 1, expired) == 0, "kept inside the 10 minutes");
        Check(keep.Expire(2000 + 10 * 60_000, expired) == 2 && expired.SequenceEqual(new uint[] { 1, 2 }), "both dropped once past it");
        Check(keep.Count == 0, "and no longer tracked");
        for (uint i = 10; i < 14; i++) keep.OnRelease(i, 5000);
        Check(!keep.OnRelease(99, 5000), "at the bound a release is not kept (the caller evicts it at once)");
        keep.Forget(10);
        Check(keep.OnRelease(99, 5000), "room again once one is forgotten (re-created or server-deleted)");
    }

    // ---- 0xF751: the teleport message as its own signal ----------------------------------

    private static void TeleportMessageStartsTheWindow()
    {
        _case = "teleport message";
        var s = new WorldTransitionState();
        s.Sample(1000, false, false, 0xA9B4);
        Check(!s.InTransition(2000), "no transition before the message");
        Check(s.OnTeleportMessage(2000), "PlayerTeleport received: the window starts");
        // No portal flag, no landblock change, alive: only the message.
        s.Sample(2016, false, false, 0xA9B4);
        Check(s.InTransition(2000 + WorldTransitionState.SettleMs - 1), "held for the full settle on the message alone");
        Check(s.Decide(3000, 2500, false) == WorldTransitionState.Verdict.Hold, "queued work held");
        Check(!s.InTransition(2000 + WorldTransitionState.SettleMs), "window ends 5 s after it");
        Check(s.Decide(2000 + WorldTransitionState.SettleMs, 2500, false) == WorldTransitionState.Verdict.Drop, "then stale work is dropped");
        Check(!s.OnTeleportMessage(1500), "an older message never shortens a running window");
    }

    // ---- the OOM dump trigger ------------------------------------------------------

    private static void OutOfMemoryBoxMatchesOnlyAcsBox()
    {
        _case = "oom box rule";
        // The real text, from a real log line.
        const string real = "OUT OF MEMORY: Failed to allocate -778718788 bytes.";
        Check(OutOfMemoryBoxRule.IsOutOfMemoryBox(real, onMainThread: true), "AC's box on its main thread: dump");
        Check(!OutOfMemoryBoxRule.IsOutOfMemoryBox(real, onMainThread: false), "the same text off the main thread: no dump");
        Check(!OutOfMemoryBoxRule.IsOutOfMemoryBox("Plugin X: OUT OF MEMORY: Failed to allocate 4 bytes.", true), "prefix not at the start: no dump");
        Check(!OutOfMemoryBoxRule.IsOutOfMemoryBox("out of memory: failed to allocate 4 bytes.", true), "other case: no dump (ordinal)");
        Check(!OutOfMemoryBoxRule.IsOutOfMemoryBox("OUT OF MEMORY", true), "the bare words: no dump");
        Check(!OutOfMemoryBoxRule.IsOutOfMemoryBox(null, true), "no text: no dump");
    }

    // ---- the launcher holds its kill while a dump is written ---------------------------

    private static void LauncherHoldsTheKillDuringADump()
    {
        _case = "dump in progress";
        var now = new DateTime(2026, 10, 10, 22, 18, 30, DateTimeKind.Utc);
        string started = $"[18:18:16] {DumpInProgressRule.MarkerStarted} pid=123 - ...\n";
        string ended = $"[18:20:01] {DumpInProgressRule.MarkerEnded} pid=123.\n";
        Check(DumpInProgressRule.IsDumping("hb #1\n" + started, now.AddMinutes(-3), null, now), "marker 3 min old, no end: hold the kill");
        Check(!DumpInProgressRule.IsDumping("hb #1\n" + started, now.AddMinutes(-11), null, now), "marker over 10 min old: a client that died mid-dump is killed");
        Check(!DumpInProgressRule.IsDumping(started + ended, now.AddMinutes(-3), null, now), "dump ended: no hold");
        Check(DumpInProgressRule.IsDumping(started + ended + started, now.AddMinutes(-1), null, now), "the LAST marker decides");
        Check(DumpInProgressRule.IsDumping("hb #1\n", now.AddMinutes(-5), now.AddSeconds(-10), now), "no marker, but the dump file was written 10 s ago: hold");
        Check(!DumpInProgressRule.IsDumping("hb #1\n", now.AddMinutes(-5), now.AddMinutes(-2), now), "a dump file quiet for 2 min: no hold");
        Check(!DumpInProgressRule.IsDumping(null, now, null, now), "nothing at all: no hold");
    }

    // ---- the cast slot refuses during a transition (the real CastSlot) -------------

    private static void CastRefusedDuringATransition()
    {
        _case = "cast slot transition";
        var slot = new CastSlot();
        Check(slot.TryEnqueue(0x50000001, 1234, 1000, inTransition: true) == CastSlot.Admit.RejectedTransition,
            "an empty slot refuses a cast during a transition");
        Check(slot.Pending == 0 && slot.TargetId == 0 && slot.SpellId == 0 && slot.QueuedAt == 0,
            "and stays untouched");

        Check(slot.TryEnqueue(0x50000002, 99, 2000, inTransition: false) == CastSlot.Admit.Queued, "outside one, a cast is queued");
        Check(slot.TryEnqueue(0x50000003, 77, 2500, inTransition: true) == CastSlot.Admit.RejectedTransition,
            "a transition refuses even with a cast pending (counted as a transition refusal, not busy)");
        Check(slot.Pending == 1 && slot.TargetId == 0x50000002 && slot.SpellId == 99 && slot.QueuedAt == 2000,
            "the pending cast is unchanged");
        Check(slot.TryEnqueue(0x50000004, 55, 2600, inTransition: false) == CastSlot.Admit.Busy, "busy outside a transition");
        slot.Pending = 0;
        Check(slot.TryEnqueue(0x50000005, 66, 9000, inTransition: false) == CastSlot.Admit.Queued
              && slot.SpellId == 66 && slot.QueuedAt == 9000, "after the transition, casts queue again");

        // The drain's once-per-cast stop-wait note belongs to the cast: a new cast clears it, a
        // refused one leaves the pending cast's note alone.
        slot.StopWaitNoted = true;
        Check(slot.TryEnqueue(0x50000006, 67, 9100, inTransition: false) == CastSlot.Admit.Busy && slot.StopWaitNoted,
            "a busy refusal keeps the pending cast's stop-wait note");
        slot.Pending = 0;
        Check(slot.TryEnqueue(0x50000007, 68, 9200, inTransition: false) == CastSlot.Admit.Queued && !slot.StopWaitNoted,
            "a newly queued cast starts with no stop-wait note");
    }

    // ---- The cast drain's walk of the ring (ActionRing.TryPeekAt) --------------------------

    private static void RingPeekAtWalksWithoutRemoving()
    {
        _case = "ring peek-at";
        var ring = new ActionRing<Act>(8, 2);
        Check(!ring.TryPeekAt(0, out _), "an empty ring has nothing at 0");
        // Wrap the ring once so the walk crosses the end of the storage.
        for (int i = 0; i < 5; i++) ring.TryEnqueue(new Act("old" + i, i, false), false, false);
        for (int i = 0; i < 5; i++) ring.TryDequeue(out _);
        for (int i = 0; i < 6; i++) ring.TryEnqueue(new Act("e" + i, 100 + i, i == 4), i == 4, false);
        var seen = new List<string>();
        for (int i = 0; ring.TryPeekAt(i, out Act e); i++) seen.Add(e.What);
        Check(string.Join(",", seen) == "e0,e1,e2,e3,e4,e5", $"oldest first across the wrap, got [{string.Join(",", seen)}]");
        Check(ring.Count == 6, $"nothing removed (count {ring.Count})");
        Check(!ring.TryPeekAt(6, out _) && !ring.TryPeekAt(-1, out _), "out of range is false");
        Check(ring.TryDequeue(out Act head) && head.What == "e0" && ring.TryPeekAt(0, out Act next) && next.What == "e1",
            "after a dequeue, offset 0 is the new oldest");
    }

    private static void EnqueueCastPassesTheTransitionState()
    {
        _case = "EnqueueCast wiring";
        string? compat = FindRepoDir(Path.Combine("src", "RynthCore.Engine", "Compatibility"));
        Check(compat != null, "found src\\RynthCore.Engine\\Compatibility by walking up");
        if (compat == null) return;
        string src = File.ReadAllText(Path.Combine(compat, "AcMainThreadQueue.cs"));
        int start = src.IndexOf("public static bool EnqueueCast(", StringComparison.Ordinal);
        int end = start < 0 ? -1 : src.IndexOf("\n    }", start, StringComparison.Ordinal);
        string body = start < 0 || end < 0 ? "" : src.Substring(start, end - start);
        Check(body.Contains("_cast.TryEnqueue(targetId, spellId, NowMs, PlayerLifecycleLog.InWorldTransition)", StringComparison.Ordinal),
            "EnqueueCast hands CastSlot the live transition state");
        Check(Regex.IsMatch(body, @"RejectedTransition\)\s*Interlocked\.Increment\(ref _transitionRejected\)"),
            "and counts a refusal in TransitionRejectedCount (the heartbeat's qrej)");
        Check(!Regex.IsMatch(src, @"_cast\.(TargetId|SpellId|QueuedAt)\s*=[^=]"),
            "nothing else writes the slot's cast fields");
    }

    // ---- the launcher's dump hold is capped per client --------------------------------

    private static void LauncherDumpHoldIsCapped()
    {
        _case = "dump hold cap";
        var now = new DateTime(2026, 10, 10, 22, 18, 30, DateTimeKind.Utc);
        string open = $"hb #1\n[18:00:00] {DumpInProgressRule.MarkerStarted} pid=123 - ...\nhb #2\n";
        DateTime freshLog = now.AddSeconds(-5);
        Check(DumpInProgressRule.Decide(open, freshLog, null, null, now) == DumpInProgressRule.Hold.Holding,
            "an open marker, first seen now: hold");
        Check(DumpInProgressRule.Decide(open, freshLog, null, now.AddMinutes(-5), now) == DumpInProgressRule.Hold.Holding,
            "held 5 min: still holding");
        Check(DumpInProgressRule.Decide(open, freshLog, null, now.AddMinutes(-16), now) == DumpInProgressRule.Hold.Expired,
            "fresh log, marker held over 15 min: the kill checks run");
        Check(DumpInProgressRule.Decide("hb #1\n", freshLog, now.AddSeconds(-3), now.AddMinutes(-16), now) == DumpInProgressRule.Hold.Expired,
            "a dump file still being written does not extend the cap either");
        Check(DumpInProgressRule.Decide(open, freshLog, null, DateTime.MinValue, now) == DumpInProgressRule.Hold.Expired,
            "the launcher's 'expired, logged' mark stays expired");
        Check(DumpInProgressRule.Decide("hb #1\n", freshLog, null, now.AddMinutes(-16), now) == DumpInProgressRule.Hold.None,
            "no sign of a dump: None (the launcher forgets the pid)");
    }

    // ---- which exit codes are crash exits ---------------------------------------------

    private static void CrashExitExcludesExitMinusOne()
    {
        _case = "crash exit rule";
        Check(CrashExitRule.IsCrashExit(0xC0000005u), "0xC0000005 access violation: crash");
        Check(CrashExitRule.IsCrashExit(0xC0000409u), "0xC0000409 stack buffer overrun: crash");
        Check(CrashExitRule.IsCrashExit(0xC0000374u), "0xC0000374 heap corruption: crash");
        Check(CrashExitRule.IsCrashExit(0xC00000FDu), "0xC00000FD stack overflow: crash");
        Check(CrashExitRule.IsCrashExit(0xE0434352u), "0xE0434352 CLR unhandled exception: crash (as before)");
        Check(!CrashExitRule.IsCrashExit(0xFFFFFFFFu), "ExitProcess(-1): not a crash");
        Check(!CrashExitRule.IsCrashExit(0u), "0: not a crash");
        Check(!CrashExitRule.IsCrashExit(1u), "1: not a crash");
        Check(!CrashExitRule.IsCrashExit(0x80000003u), "0x80000003 (warning severity): not a crash");
        Check(!CrashExitRule.IsCrashExit(0x40010004u), "0x40010004 (informational): not a crash");
    }

    private static void CrashExitPathsFlushTheCrashLog()
    {
        _case = "crash exit flush";
        string? compat = FindRepoDir(Path.Combine("src", "RynthCore.Engine", "Compatibility"));
        Check(compat != null, "found src\\RynthCore.Engine\\Compatibility by walking up");
        if (compat == null) return;
        string src = File.ReadAllText(Path.Combine(compat, "ProcessExitHooks.cs"));
        Check(src.Contains("private static bool IsCrashExit(uint code) => CrashExitRule.IsCrashExit(code);", StringComparison.Ordinal),
            "ProcessExitHooks uses CrashExitRule");
        foreach (string detour in new[] { "ExitProcessDetour", "TerminateProcessDetour", "RtlExitUserProcessDetour", "NtTerminateProcessDetour" })
        {
            int start = src.IndexOf($"private static {(detour.Contains("Terminate") ? (detour.StartsWith("Nt") ? "uint" : "bool") : "void")} {detour}(", StringComparison.Ordinal);
            int end = start < 0 ? -1 : src.IndexOf("\n    }", start, StringComparison.Ordinal);
            string body = start < 0 || end < 0 ? "" : src.Substring(start, end - start);
            int flush = body.IndexOf("SehTrampoline.FlushCrashLog(", StringComparison.Ordinal);
            int orig = body.IndexOf("Original!(", StringComparison.Ordinal);
            Check(flush >= 0 && orig > flush, $"{detour}: a crash exit flushes the crash log before the original");
        }
    }
}
