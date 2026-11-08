using RynthCore.Engine;
using RynthCore.Engine.Compatibility;

namespace EngineRobustnessTests;

/// <summary>
/// The pure decision parts of the engine-robustness pass. Every clock is a plain
/// number; nothing touches AC.
/// </summary>
internal static partial class Program
{
    private static int _checks, _failed;
    private static string _case = "";

    private static int Main()
    {
        QueueMergesSameObject();
        QueueDoesNotMergeAcrossLoginOrWhenStale();
        QueueFullStillMerges();

        EligibilityCreateAndDuplicate();
        EligibilityWindowAfterClientRelease();
        EligibilityServerDeleteReArms();
        EligibilityUncachedIsAppraised();
        EligibilityReleaseBeforeSendRecordsNothing();
        EligibilityMapStaysBounded();
        PriorityFreshRule();

        FreezeRule();
        PauseTracker();

        TransitionHoldsWhileDead();
        TransitionLandblockJumpWithoutPortalHolds();
        TransitionAdjacentCrossingDoesNotHold();
        TransitionPortalHolds();
        TransitionShortTeleportToAdjacentLandblockHolds();
        TransitionDiagonalAndBorderWalksNeverHold();
        TransitionTwoLandblockJumpHolds();
        TransitionDrainHoldThenStopsSurvive();
        TransitionValveOpensAfterMaxHold();
        TransitionValveStuckPortalAndReset();
        TransitionGateDisabledNeverHolds();
        TransitionValveIgnoresShortTransitions();

        AddressSpaceFold();

        RunRingAndGateTests();

        RunAutoIdLimitTests();
        RunAutoIdDrainTests();

        RunPumpGuardTests();
        RunShutdownOrderTests();
        RunReloadStallTests();

        RunNativeAttackRearmTests();

        Console.WriteLine($"{_checks} check(s), {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (ok) return;
        _failed++;
        Console.WriteLine($"[FAIL] {_case}: {what}");
    }

    // ---- fix 1: the RequestId slot merges ---------------------------------------------

    private const long MergeMax = 15_000;

    private static void QueueMergesSameObject()
    {
        _case = "queue merge";
        var q = new PendingIdQueue(8);
        Check(q.Enqueue(0x80000001, 1000, 1, MergeMax) == PendingIdQueue.Result.Queued, "first request queued");
        Check(q.Enqueue(0x80000002, 1010, 1, MergeMax) == PendingIdQueue.Result.Queued, "other object queued");
        Check(q.Enqueue(0x80000001, 1100, 1, MergeMax) == PendingIdQueue.Result.Merged, "same object merged");
        Check(q.Enqueue(0x80000001, 1200, 1, MergeMax) == PendingIdQueue.Result.Merged, "merged again");
        Check(q.Count == 2, $"two pending, got {q.Count}");
        Check(q.MergedTotal == 2, $"two merges counted, got {q.MergedTotal}");
        Check(q.TryDequeue(out var a) && a.Id == 0x80000001 && a.At == 1000, "FIFO head is the first request, original time");
        Check(q.TryDequeue(out var b) && b.Id == 0x80000002, "then the second object");
        Check(!q.TryDequeue(out _), "empty");
        // Once sent (dequeued), a new request for the same object queues again.
        Check(q.Enqueue(0x80000001, 1300, 1, MergeMax) == PendingIdQueue.Result.Queued, "re-queued after the send");
    }

    private static void QueueDoesNotMergeAcrossLoginOrWhenStale()
    {
        _case = "queue no merge";
        var q = new PendingIdQueue(8);
        q.Enqueue(7, 1000, 1, MergeMax);
        Check(q.Enqueue(7, 1100, 2, MergeMax) == PendingIdQueue.Result.Queued, "a new login's request is not absorbed by the old one");
        Check(q.Enqueue(7, 1200, 1, MergeMax) == PendingIdQueue.Result.Merged,
            "a later epoch-1 request still merges into the epoch-1 entry");
        Check(q.Enqueue(7, 1300, 2, MergeMax) == PendingIdQueue.Result.Merged,
            "and an epoch-2 one into the epoch-2 entry");
        Check(q.Count == 2, $"one entry per login, got {q.Count}");
        var r = new PendingIdQueue(8);
        r.Enqueue(9, 1000, 1, MergeMax);
        Check(r.Enqueue(9, 1000 + MergeMax + 1, 1, MergeMax) == PendingIdQueue.Result.Queued, "a stale pending entry takes no merge");
        Check(r.Count == 2, "both kept");
    }

    private static void QueueFullStillMerges()
    {
        _case = "queue full";
        var q = new PendingIdQueue(3);
        q.Enqueue(1, 0, 1, MergeMax); q.Enqueue(2, 0, 1, MergeMax); q.Enqueue(3, 0, 1, MergeMax);
        Check(q.Enqueue(4, 1, 1, MergeMax) == PendingIdQueue.Result.Full, "a new object is refused when full");
        Check(q.Enqueue(2, 1, 1, MergeMax) == PendingIdQueue.Result.Merged, "a pending object still merges when full");
        // Wrap-around: dequeue one, add one, order holds.
        q.TryDequeue(out _);
        q.Enqueue(5, 2, 1, MergeMax);
        var order = new List<uint>();
        while (q.TryDequeue(out var it)) order.Add(it.Id);
        Check(string.Join(",", order) == "2,3,5", $"FIFO across the wrap, got {string.Join(",", order)}");
    }

    // ---- fix 3: re-eligibility ---------------------------------------------------------

    private const long Min = 60_000;

    private static void EligibilityCreateAndDuplicate()
    {
        _case = "eligibility create";
        var e = new AutoIdEligibility();
        Check(e.OnCreate(10, 1000, false) == AutoIdEligibility.CreateDecision.Appraise, "a new object is appraised");
        Check(e.OnCreate(10, 1001, false) == AutoIdEligibility.CreateDecision.SkipDuplicate, "a second create while live is a duplicate");
        Check(e.IsLive(10), "live");
        e.OnClientRelease(10);
        Check(!e.IsLive(10), "released");
        Check(e.IsLive(999), "an untracked id counts as live (the drain sends it)");
    }

    private static void EligibilityWindowAfterClientRelease()
    {
        _case = "eligibility window";
        var e = new AutoIdEligibility();
        e.OnCreate(10, 0, false);
        e.OnSent(10, 1000);
        e.OnClientRelease(10);
        Check(e.OnCreate(10, 1000 + 1 * Min, true) == AutoIdEligibility.CreateDecision.SkipRecent, "re-created 1 min later, cached: skipped");
        e.OnClientRelease(10);
        Check(e.OnCreate(10, 1000 + 9 * Min, true) == AutoIdEligibility.CreateDecision.SkipRecent, "9 min later: still skipped");
        e.OnClientRelease(10);
        Check(e.OnCreate(10, 1000 + 10 * Min, true) == AutoIdEligibility.CreateDecision.Appraise, "10 min later: appraised again");
    }

    private static void EligibilityServerDeleteReArms()
    {
        _case = "eligibility server delete";
        var e = new AutoIdEligibility();
        e.OnCreate(20, 0, false);
        e.OnSent(20, 1000);
        e.OnServerDelete(20);
        e.OnClientRelease(20);
        Check(e.OnCreate(20, 2000, true) == AutoIdEligibility.CreateDecision.Appraise, "server delete then re-create inside the window: appraised");
        // Client release first, server delete after: same result.
        e.OnSent(20, 3000);
        e.OnClientRelease(20);
        e.OnServerDelete(20);
        Check(e.OnCreate(20, 4000, true) == AutoIdEligibility.CreateDecision.Appraise, "release then server delete: appraised");
    }

    private static void EligibilityUncachedIsAppraised()
    {
        _case = "eligibility uncached";
        var e = new AutoIdEligibility();
        e.OnCreate(30, 0, false);
        e.OnSent(30, 1000);
        e.OnClientRelease(30);
        Check(e.OnCreate(30, 2000, false) == AutoIdEligibility.CreateDecision.Appraise, "nothing cached any more: appraised even inside the window");
    }

    private static void EligibilityReleaseBeforeSendRecordsNothing()
    {
        _case = "eligibility unsent";
        var e = new AutoIdEligibility();
        e.OnCreate(40, 0, false);
        e.OnClientRelease(40);      // released before the drain sent its appraisal
        Check(e.OnCreate(40, 500, true) == AutoIdEligibility.CreateDecision.Appraise, "never sent: the next create is appraised");
    }

    private static void EligibilityMapStaysBounded()
    {
        _case = "eligibility bound";
        var e = new AutoIdEligibility(10 * Min, 100);
        for (uint i = 1; i <= 100; i++) e.OnCreate(i, 0, false);
        Check(e.Count == 100, $"100 tracked, got {e.Count}");
        Check(e.OnCreate(101, 0, false) == AutoIdEligibility.CreateDecision.Appraise, "past the bound a create is still appraised");
        Check(e.Count == 100 && e.UntrackedAdmits == 1, $"but not tracked (count {e.Count}, untracked {e.UntrackedAdmits})");
        Check(e.Prune(1000) == 0, "all live: nothing pruned");

        // 80 of them appraised (sent at different times) and released, all inside the window.
        for (uint i = 1; i <= 80; i++) { e.OnSent(i, 1000 + i); e.OnClientRelease(i); }
        int removed = e.Prune(2 * Min);
        Check(e.Count <= 75, $"pruned to three quarters of the bound, count {e.Count} (removed {removed})");
        bool liveKept = true;
        for (uint i = 81; i <= 100; i++) liveKept &= e.IsLive(i) && e.OnCreate(i, 2 * Min, true) == AutoIdEligibility.CreateDecision.SkipDuplicate;
        Check(liveKept, "the 20 live entries are never pruned");
        // The newest-sent released entries survive the age passes before the oldest.
        Check(e.OnCreate(80, 2 * Min, true) == AutoIdEligibility.CreateDecision.SkipRecent, "the most recently sent released entry survived");

        // Outside the window everything released goes.
        var f = new AutoIdEligibility(10 * Min, 100);
        for (uint i = 1; i <= 50; i++) { f.OnCreate(i, 0, false); f.OnSent(i, 1); f.OnClientRelease(i); }
        f.Prune(11 * Min);
        Check(f.Count == 0, $"expired released entries are pruned, count {f.Count}");
    }

    private static void PriorityFreshRule()
    {
        _case = "priority fresh";
        long now = 1_800_000_000;
        Check(AutoIdEligibility.IsAppraisalFresh(true, now - 60, now, 10 * Min), "cached, 1 min old: fresh (skip)");
        Check(!AutoIdEligibility.IsAppraisalFresh(true, now - 601, now, 10 * Min), "cached, 10 min+ old: stale (appraise)");
        Check(!AutoIdEligibility.IsAppraisalFresh(false, now - 60, now, 10 * Min), "not cached: appraise");
        Check(!AutoIdEligibility.IsAppraisalFresh(true, 0, now, 10 * Min), "no receive time: appraise");
    }

    // ---- fix 4: the plugin pump pauses while AC is frozen --------------------------------

    private static void FreezeRule()
    {
        _case = "freeze rule";
        const long T = 4000;
        Check(!MainThreadFreezeRule.IsFrozen(100_000, 0, false, 0, T), "no beat yet: not frozen");
        Check(!MainThreadFreezeRule.IsFrozen(100_000, 99_000, true, 0, T), "beat 1 s ago: running");
        Check(MainThreadFreezeRule.IsFrozen(100_000, 96_000, true, 0, T), "beat 4 s ago: frozen (the hang watchdog's threshold)");
        Check(MainThreadFreezeRule.IsFrozen(100_000, 99_900, true, 1, T), "a main-thread message box: frozen at once");
        Check(MainThreadFreezeRule.IsFrozen(100_000, 0, false, 1, T), "a message box before the first beat: frozen");
    }

    private static void PauseTracker()
    {
        _case = "pause tracker";
        var t = new PumpPauseTracker();
        Check(t.Update(false, 1000) == PumpPauseTracker.Transition.None && !t.Paused, "running");
        Check(t.Update(true, 2000) == PumpPauseTracker.Transition.Paused && t.Paused, "pauses");
        Check(t.Update(true, 2016) == PumpPauseTracker.Transition.None, "stays paused");
        Check(t.TicksSkippedThisPause == 2, $"2 ticks skipped, got {t.TicksSkippedThisPause}");
        Check(t.TotalPausedMs(5000) == 3000, $"3000 ms paused in progress, got {t.TotalPausedMs(5000)}");
        Check(t.Update(false, 14000) == PumpPauseTracker.Transition.Resumed && !t.Paused, "resumes");
        Check(t.FinishedPausedMs == 12000 && t.TotalPausedMs(20000) == 12000, $"12 s in total, got {t.FinishedPausedMs}");
        t.Update(true, 30000); t.Update(false, 30500);
        Check(t.Pauses == 2 && t.FinishedPausedMs == 12500 && t.TicksSkippedTotal == 3, "second pause adds up");
    }

    // ---- the world-transition gate (coordinator items c and g) --------------------------

    private static WorldTransitionState Started(uint landblock = 0x00AE)
    {
        var s = new WorldTransitionState();
        s.Sample(1000, false, false, landblock);
        return s;
    }

    private static void TransitionHoldsWhileDead()
    {
        _case = "transition dead";
        var s = Started();
        Check(!s.InTransition(1100), "alive and still: no transition");
        Check(s.Decide(1100, 1050, false) == WorldTransitionState.Verdict.Run, "work runs normally");
        var edges = s.Sample(2000, true, false, 0x00AE);
        Check((edges & WorldTransitionState.Edge.Died) != 0, "death edge");
        Check(s.Decide(2000, 1900, false) == WorldTransitionState.Verdict.Hold, "a cast queued before the death is held");
        Check(s.Decide(2000, 1900, true) == WorldTransitionState.Verdict.Hold, "a stop is held too (never run into the death)");
        s.Sample(9000, false, false, 0x00AE);   // revived
        Check(s.InTransition(9000 + WorldTransitionState.SettleMs - 1), "settle window after revival");
        Check(!s.InTransition(9000 + WorldTransitionState.SettleMs), "window ends");
    }

    // The safety valve (review 2026-10-18): a character that keeps reading dead (Health 0 after a
    // revive) must not freeze every queued action for good.
    private static void TransitionValveOpensAfterMaxHold()
    {
        _case = "valve dead forever";
        var s = Started();
        long t = 2000;
        var e = s.Sample(t, true, false, 0x00AE);
        Check((e & WorldTransitionState.Edge.Died) != 0 && !s.Overridden, "dead: gate held, valve shut");
        Check(s.Decide(t, 1500, false) == WorldTransitionState.Verdict.Hold, "an action queued before is held");
        int overrideEdges = 0;
        long openedAt = 0;
        for (t += 1000; t <= 2000 + WorldTransitionState.MaxHoldMs + 10_000; t += 1000)
        {
            e = s.Sample(t, true, false, 0x00AE);   // still reading dead
            if ((e & WorldTransitionState.Edge.GateOverride) != 0) { overrideEdges++; openedAt = t; }
            if (t < 2000 + WorldTransitionState.MaxHoldMs)
                Check(s.InTransition(t) && !s.Overridden, $"held before the bound (t={t})");
        }
        Check(overrideEdges == 1, $"exactly one override edge, got {overrideEdges}");
        Check(openedAt == 2000 + WorldTransitionState.MaxHoldMs, $"opens at the bound, got {openedAt - 2000} ms");
        Check(s.Overridden && s.Overrides == 1, "valve open, counted once");
        long now = t;
        Check(!s.InTransition(now), "open: nothing is in transition any more");
        Check(s.RawInTransition(now), "(the raw state still says dead)");
        Check(s.Decide(now, now - 10, false) == WorldTransitionState.Verdict.Run, "new work runs");
        Check(s.Decide(now, 1500, false) == WorldTransitionState.Verdict.Drop, "work queued during the hold is dropped as stale, like after any transition");
        Check(s.Decide(now, 1500, true) == WorldTransitionState.Verdict.Run, "a stop queued during the hold still runs");
        // The character does come back later: the raw transition ends, and the valve re-arms.
        s.Sample(now + 1000, false, false, 0x00AE);
        Check(s.Overridden, "revived, but the settle window is still running: still open");
        s.Sample(now + 1000 + WorldTransitionState.SettleMs, false, false, 0x00AE);
        Check(!s.Overridden && !s.RawInTransition(now + 1000 + WorldTransitionState.SettleMs), "settled: valve re-armed");
        var d = s.Sample(now + 20_000, true, false, 0x00AE);
        Check((d & WorldTransitionState.Edge.Died) != 0 && s.InTransition(now + 20_000), "the next death holds again");
        Check(s.HeldForMs(now + 20_000) == 0, "held-for restarts from the new transition");
    }

    private static void TransitionValveStuckPortalAndReset()
    {
        _case = "valve stuck portal";
        var s = Started();
        s.Sample(2000, false, true, 0x00AE);
        long t = 2000;
        bool opened = false;
        for (; t < 2000 + WorldTransitionState.MaxHoldMs + 5000; t += 500)
            if ((s.Sample(t, false, true, 0x00AE) & WorldTransitionState.Edge.GateOverride) != 0) opened = true;
        Check(opened && !s.InTransition(t) && s.Overrides == 1, "a portal flag that never clears opens the gate once");
        // Exiting the portal ends it.
        s.Sample(t, false, false, 0x00AE);
        s.Sample(t + WorldTransitionState.SettleMs + 1, false, false, 0x00AE);
        Check(!s.Overridden, "re-armed after the transition ends");
    }

    private static void TransitionGateDisabledNeverHolds()
    {
        _case = "gate disabled";
        var s = Started();
        s.Enabled = false;
        s.Sample(2000, true, false, 0x00AE);
        Check(s.RawInTransition(2000) && !s.InTransition(2000), "dead, gate off: not in transition");
        Check(!s.CheckHolding(2000) && s.LastBlockedMs == 0, "nothing held, nothing recorded");
        Check(s.Decide(2000, 1900, false) == WorldTransitionState.Verdict.Run, "work runs through a death");
        Check(s.Decide(2000, 1900, true) == WorldTransitionState.Verdict.Run, "and stops too");
        long late = 2000 + WorldTransitionState.MaxHoldMs + 1000;
        s.Sample(late, true, false, 0x00AE);
        Check(s.Overrides == 1, "the valve counter still counts (only information while the gate is off)");
        s.Enabled = true;
        s.Sample(late + 1000, true, false, 0x00AE);
        Check(!s.InTransition(late + 1000), "turned on again while the valve is open: stays open until the transition ends");
    }

    private static void TransitionValveIgnoresShortTransitions()
    {
        _case = "valve short transitions";
        var s = Started();
        long t = 2000;
        // 40 deaths/portals in a row, each well under the bound, each followed by a quiet spell.
        for (int i = 0; i < 40; i++)
        {
            s.Sample(t, true, false, 0x00AE);
            t += 20_000;
            s.Sample(t, false, true, 0x00AE);
            t += 20_000;
            s.Sample(t, false, false, 0x00AE);
            t += WorldTransitionState.SettleMs + 1000;
            s.Sample(t, false, false, 0x00AE);
            t += 10_000;
        }
        Check(s.Overrides == 0, $"transitions that end on their own never trip it, got {s.Overrides}");
        // A death straight into a portal without a gap is ONE continuous transition.
        var u = Started();
        u.Sample(2000, true, false, 0x00AE);
        u.Sample(2000 + 60_000, false, true, 0x00AE);
        var edges = u.Sample(2000 + 125_000, false, true, 0x00AE);
        Check((edges & WorldTransitionState.Edge.GateOverride) != 0, "dead then portaling without a gap: one transition, valve opens at the bound");
    }

    private static void TransitionLandblockJumpWithoutPortalHolds()
    {
        _case = "transition jump";
        // A real case: a death recall jumped 0x00AE -> 0xA9B4 with no portal-space edge.
        var s = Started(0x00AE);
        var edges = s.Sample(5000, false, false, 0xA9B4);
        Check((edges & WorldTransitionState.Edge.LandblockJump) != 0, "jump edge");
        Check(s.InTransition(5000) && s.InTransition(5000 + WorldTransitionState.SettleMs - 1), "the jump alone starts the settle window");
        Check(s.Decide(6000, 4900, false) == WorldTransitionState.Verdict.Hold, "queued work is held after the jump");
        Check(WorldTransitionState.IsJump(0x00AE, 0xA9B4) && !WorldTransitionState.IsJump(0xA9B4, 0xA9B5) && !WorldTransitionState.IsJump(0xA9B4, 0xAAB5),
            "adjacent (including diagonal) is not a jump");
    }

    private static void TransitionAdjacentCrossingDoesNotHold()
    {
        _case = "transition adjacent";
        var s = Started(0xA9B4);
        var edges = s.Sample(5000, false, false, 0xA9B5);
        Check(edges == WorldTransitionState.Edge.None, "walking into the next landblock: no edge");
        Check(!s.InTransition(5000) && s.Decide(5000, 4990, false) == WorldTransitionState.Verdict.Run, "and no hold");
    }

    private static void TransitionPortalHolds()
    {
        _case = "transition portal";
        var s = Started();
        s.Sample(2000, false, true, 0x00AE);
        Check(s.Decide(2500, 1990, false) == WorldTransitionState.Verdict.Hold, "held in portal space");
        s.Sample(4000, false, false, 0x00AE);
        Check(s.InTransition(4000 + WorldTransitionState.SettleMs - 1), "settle window after portal exit");
    }

    /// <summary>
    /// A recall or portal into the NEXT landblock (a lifestone just over the border): no
    /// non-adjacent jump, but teleport_in_progress (the portaling flag) is the signal, and the
    /// full settle follows it.
    /// </summary>
    private static void TransitionShortTeleportToAdjacentLandblockHolds()
    {
        _case = "transition short teleport";
        var s = Started(0xA9B4);
        s.Sample(2000, false, true, 0xA9B4);    // teleport_in_progress set
        s.Sample(2500, false, true, 0xA9B5);    // lands one landblock over
        var edges = s.Sample(3000, false, false, 0xA9B5);
        Check((edges & WorldTransitionState.Edge.PortalExited) != 0, "portal exit edge");
        Check(s.Decide(3000 + WorldTransitionState.SettleMs - 1, 2900, false) == WorldTransitionState.Verdict.Hold,
            "full 5 s settle after a teleport to an adjacent landblock");
        Check(s.Decide(3000 + WorldTransitionState.SettleMs, 2900, false) == WorldTransitionState.Verdict.Drop,
            "then the stale action is dropped");
    }

    /// <summary>Walking across a border never gates, not even for a tick.</summary>
    private static void TransitionDiagonalAndBorderWalksNeverHold()
    {
        _case = "transition walk";
        var s = Started(0xA9B4);
        long now = 2000;
        foreach (uint lb in new uint[] { 0xA9B5, 0xAAB6, 0xA9B5, 0xA8B4, 0xA9B4, 0xAAB3 })
        {
            var edges = s.Sample(now, false, false, lb);
            Check(edges == WorldTransitionState.Edge.None, $"walk into 0x{lb:X4}: no edge");
            Check(!s.InTransition(now) && s.Decide(now, now - 1, false) == WorldTransitionState.Verdict.Run, $"walk into 0x{lb:X4}: runs at once");
            now += 16;
        }
        // A cell read of 0 (pose not available for a tick) is not a jump to 0x0000 and back.
        s.Sample(now, false, false, 0);
        var back = s.Sample(now + 16, false, false, 0xAAB3);
        Check(back == WorldTransitionState.Edge.None && !s.InTransition(now + 16), "an unknown pose for one tick is not a jump");
    }

    private static void TransitionTwoLandblockJumpHolds()
    {
        _case = "transition two-block jump";
        var sx = Started(0xA9B4);
        Check((sx.Sample(2000, false, false, 0xABB4) & WorldTransitionState.Edge.LandblockJump) != 0 && sx.InTransition(2000), "2 apart in x: a teleport");
        var sy = Started(0xA9B4);
        Check((sy.Sample(2000, false, false, 0xA9B2) & WorldTransitionState.Edge.LandblockJump) != 0 && sy.InTransition(2000), "2 apart in y: a teleport");
    }

    /// <summary>
    /// The drain the gate sits in (AcMainThreadQueue.Drain): a FIFO walked head to tail, each
    /// entry's verdict asked of the gate. While held, nothing runs and nothing is lost; once
    /// the transition ends the stale cast and use are dropped, and both stops run, in order.
    /// </summary>
    private static void TransitionDrainHoldThenStopsSurvive()
    {
        _case = "transition drain";
        var s = Started();
        var fifo = new List<(string What, long At, bool Teardown)>
        {
            ("cast", 1500, false),
            ("autorun-off", 1600, true),
            ("use", 1700, false),
            ("cancel-attack", 1800, true),
        };
        s.Sample(2000, true, false, 0x00AE);    // dies

        var ran = new List<string>(); var dropped = new List<string>();
        long revivedAt = 0;
        bool queuedNew = false;
        for (long now = 2000; now < 20_000; now += 16)
        {
            // Game tick sample: dead until 9 s, alive after.
            s.Sample(now, now < 9000, false, 0x00AE);
            if (now >= 9000 && revivedAt == 0) revivedAt = now;
            if (!queuedNew && revivedAt != 0 && now >= revivedAt + WorldTransitionState.SettleMs + 500)
            {
                fifo.Add(("cast-new", now, false));                // queued after the window
                queuedNew = true;
            }
            Drain(s, now, fifo, ran, dropped);
            if (revivedAt == 0 || now < revivedAt + WorldTransitionState.SettleMs)
                Check(ran.Count == 0 && dropped.Count == 0 && fifo.Count == 4, $"held: nothing runs or drops (t={now})");
        }
        Check(dropped.SequenceEqual(new[] { "cast", "use" }), $"stale cast and use dropped, got [{string.Join(",", dropped)}]");
        Check(ran.SequenceEqual(new[] { "autorun-off", "cancel-attack", "cast-new" }),
            $"stops run in queue order as a group, then new work, got [{string.Join(",", ran)}]");
        Check(fifo.Count == 0, "queue empty");
    }

    private static void Drain(WorldTransitionState s, long now, List<(string What, long At, bool Teardown)> fifo,
                              List<string> ran, List<string> dropped)
    {
        while (fifo.Count > 0)
        {
            var e = fifo[0];
            var v = s.Decide(now, e.At, e.Teardown);
            if (v == WorldTransitionState.Verdict.Hold) return;
            fifo.RemoveAt(0);
            if (v == WorldTransitionState.Verdict.Drop) dropped.Add(e.What);
            else ran.Add(e.What);
        }
    }

    // ---- fix 7: largest free block -------------------------------------------------------

    private static void AddressSpaceFold()
    {
        _case = "address space";
        ulong free = 0, largest = 0;
        AddressSpace.Accumulate(true, 10, ref free, ref largest);
        AddressSpace.Accumulate(false, 500, ref free, ref largest);
        AddressSpace.Accumulate(true, 40, ref free, ref largest);
        AddressSpace.Accumulate(true, 30, ref free, ref largest);
        Check(free == 80 && largest == 40, $"free 80 largest 40, got {free}/{largest}");
        var (f, l) = AddressSpace.FreeAndLargestMb();
        Check(f > 0 && l > 0 && l <= f, $"live walk of this process: free {f} MB, largest {l} MB");
    }
}
