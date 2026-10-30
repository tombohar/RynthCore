using RynthCore.Engine;

namespace ReloadInWorldGateTests;

/// <summary>
/// Offline checks for ReloadInWorldGate (the opt-in hold of an automatic hot reload while the
/// player is in the world). See the csproj comment for the cases.
/// </summary>
internal static class Program
{
    private static int _checks, _failed;
    private static string _case = "";

    private static readonly TimeSpan Ten = TimeSpan.FromMinutes(10);
    private static readonly DateTime T0 = new(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc);

    private static int Main()
    {
        ShouldHoldRules();
        DefaultOffNeverHolds();
        ClearWhenNotHolding();
        WaitsUntilLeftWorld();
        ManualReloadBypassesTheHold();
        ManualReloadMidWaitReleases();
        SettingSwitchedOffMidWaitReleases();
        TimeoutGivesUpAndLogsOnce();
        TimeoutIsNotLateByAPoll();
        LeavingAtTheBoundIsStillCleared();
        ThrowingHoldNeverWedges();
        ThrowingHoldMidWaitReleases();
        MaxMinutesClamp();

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

    private static void ShouldHoldRules()
    {
        _case = "ShouldHold";
        Check(ReloadInWorldGate.ShouldHold(byHand: false, holdEnabled: true, inWorld: true), "automatic + in world + hold on: hold");
        Check(!ReloadInWorldGate.ShouldHold(byHand: true, holdEnabled: true, inWorld: true), "by hand + in world + hold on: never held");
        Check(!ReloadInWorldGate.ShouldHold(byHand: false, holdEnabled: true, inWorld: false), "out of world: never hold");
        Check(!ReloadInWorldGate.ShouldHold(byHand: true, holdEnabled: true, inWorld: false), "by hand + out of world: never hold");
    }

    // The default (HoldReloadInWorld off) must never hold, whatever the world state or the origin.
    private static void DefaultOffNeverHolds()
    {
        _case = "default off";
        foreach (bool byHand in new[] { false, true })
            foreach (bool inWorld in new[] { false, true })
                Check(!ReloadInWorldGate.ShouldHold(byHand, holdEnabled: false, inWorld), $"hold off, byHand={byHand}, inWorld={inWorld}: never hold");

        // The query EntryPoint hands to Run is ShouldHold(..., holdEnabled: false, ...): Run returns at once.
        var logs = new List<string>();
        int sleeps = 0;
        var outcome = ReloadInWorldGate.Run(
            stillHold: () => ReloadInWorldGate.ShouldHold(false, holdEnabled: false, inWorld: true),
            log: logs.Add,
            notify: _ => throw new InvalidOperationException("must not notify"),
            maxWait: Ten,
            waited: out TimeSpan waited,
            sleep: _ => sleeps++);
        Check(outcome == ReloadInWorldGate.Outcome.Clear, $"Clear (got {outcome})");
        Check(waited == TimeSpan.Zero && sleeps == 0, "no wait, no sleep");
        Check(logs.Count == 0, "no new log line");
    }

    private static void ClearWhenNotHolding()
    {
        _case = "clear";
        var logs = new List<string>();
        var outcome = ReloadInWorldGate.Run(
            stillHold: () => false,
            log: logs.Add,
            notify: null,
            maxWait: Ten,
            waited: out TimeSpan waited,
            clock: () => T0,
            sleep: _ => { });
        Check(outcome == ReloadInWorldGate.Outcome.Clear, $"Clear (got {outcome})");
        Check(waited == TimeSpan.Zero, "no wait");
        Check(logs.Count == 0, "nothing logged");
    }

    private static void WaitsUntilLeftWorld()
    {
        _case = "wait then clear";
        var now = T0;
        int polls = 0;
        var logs = new List<string>();
        var notes = new List<string>();
        var outcome = ReloadInWorldGate.Run(
            stillHold: () => ++polls <= 5, // hold for 5 polls, then clear
            log: logs.Add,
            notify: notes.Add,
            maxWait: Ten,
            waited: out TimeSpan waited,
            clock: () => now,
            sleep: t => now += t,
            poll: TimeSpan.FromSeconds(2),
            logEvery: TimeSpan.FromSeconds(30));
        Check(outcome == ReloadInWorldGate.Outcome.ClearedAfterWait, $"ClearedAfterWait (got {outcome})");
        Check(Math.Abs(waited.TotalSeconds - 10) < 0.01, $"waited 10 s (got {waited.TotalSeconds})");
        Check(logs.Count >= 2 && logs[0].Contains("player is in the world", StringComparison.Ordinal),
            $"first log names the world: {logs.FirstOrDefault()}");
        Check(logs[^1].Contains("hold released", StringComparison.Ordinal), $"last log: {logs[^1]}");
        Check(!logs.Any(l => l.Contains("reloading anyway", StringComparison.Ordinal)), "no give-up line when the player left");
        Check(notes.Count == 1 && notes[0].Contains("in the world", StringComparison.Ordinal), "chat notify once");
    }

    // A reload asked for by hand is never held: modelled the way EntryPoint wires it (the hold query is
    // ShouldHold(byHand, ...)), so the gate returns Clear without a wait.
    private static void ManualReloadBypassesTheHold()
    {
        _case = "manual bypass";
        int sleeps = 0;
        var outcome = ReloadInWorldGate.Run(
            stillHold: () => ReloadInWorldGate.ShouldHold(byHand: true, holdEnabled: true, inWorld: true),
            log: _ => { },
            notify: null,
            maxWait: Ten,
            waited: out TimeSpan waited,
            sleep: _ => sleeps++);
        Check(outcome == ReloadInWorldGate.Outcome.Clear, $"Clear (got {outcome})");
        Check(waited == TimeSpan.Zero && sleeps == 0, "no wait");
    }

    // An automatic reload is being held, then the player presses RL / ERl: the next poll releases it.
    private static void ManualReloadMidWaitReleases()
    {
        _case = "manual mid-wait";
        var now = T0;
        bool byHand = false;
        var logs = new List<string>();
        var outcome = ReloadInWorldGate.Run(
            stillHold: () => ReloadInWorldGate.ShouldHold(byHand, holdEnabled: true, inWorld: true),
            log: logs.Add,
            notify: null,
            maxWait: Ten,
            waited: out TimeSpan waited,
            clock: () => now,
            sleep: t => { now += t; if (now - T0 >= TimeSpan.FromSeconds(60)) byHand = true; });
        Check(outcome == ReloadInWorldGate.Outcome.ClearedAfterWait, $"ClearedAfterWait (got {outcome})");
        Check(Math.Abs(waited.TotalSeconds - 60) < 0.01, $"released at the poll after the press, 60 s (got {waited.TotalSeconds})");
        Check(!logs.Any(l => l.Contains("reloading anyway", StringComparison.Ordinal)), "not a give-up");
    }

    private static void SettingSwitchedOffMidWaitReleases()
    {
        _case = "setting off mid-wait";
        var now = T0;
        bool enabled = true;
        var outcome = ReloadInWorldGate.Run(
            stillHold: () => ReloadInWorldGate.ShouldHold(false, holdEnabled: enabled, inWorld: true),
            log: _ => { },
            notify: null,
            maxWait: Ten,
            waited: out TimeSpan waited,
            clock: () => now,
            sleep: t => { now += t; if (now - T0 >= TimeSpan.FromSeconds(6)) enabled = false; });
        Check(outcome == ReloadInWorldGate.Outcome.ClearedAfterWait, $"ClearedAfterWait (got {outcome})");
        Check(Math.Abs(waited.TotalSeconds - 6) < 0.01, $"released after 6 s (got {waited.TotalSeconds})");
    }

    private static void TimeoutGivesUpAndLogsOnce()
    {
        _case = "timeout";
        var now = T0;
        int polls = 0;
        var logs = new List<string>();
        var outcome = ReloadInWorldGate.Run(
            stillHold: () => { polls++; return true; },   // never leaves the world
            log: logs.Add,
            notify: null,
            maxWait: TimeSpan.FromMinutes(3),
            waited: out TimeSpan waited,
            clock: () => now,
            sleep: t => now += t,
            poll: TimeSpan.FromSeconds(2),
            logEvery: TimeSpan.FromSeconds(30));
        Check(outcome == ReloadInWorldGate.Outcome.TimedOut, $"TimedOut (got {outcome})");
        Check(Math.Abs(waited.TotalMinutes - 3) < 0.001, $"waited exactly the bound, 3 min (got {waited.TotalMinutes})");
        int gaveUp = logs.Count(l => l.Contains("reloading anyway", StringComparison.Ordinal));
        Check(gaveUp == 1, $"the give-up is logged once (got {gaveUp})");
        Check(logs[^1].Contains("held 3 min, reloading anyway", StringComparison.Ordinal), $"last log: {logs[^1]}");
        Check(logs.Count(l => l.Contains("still in the world", StringComparison.Ordinal)) >= 3, "periodic 'still in the world' lines while waiting");
        Check(polls < 200, $"bounded number of polls (got {polls})");
    }

    // The bound is not a poll late: with a 1 min bound and a 7 s poll the last sleep is shortened.
    private static void TimeoutIsNotLateByAPoll()
    {
        _case = "timeout on time";
        var now = T0;
        var outcome = ReloadInWorldGate.Run(
            stillHold: () => true,
            log: _ => { },
            notify: null,
            maxWait: TimeSpan.FromMinutes(1),
            waited: out TimeSpan waited,
            clock: () => now,
            sleep: t => now += t,
            poll: TimeSpan.FromSeconds(7));
        Check(outcome == ReloadInWorldGate.Outcome.TimedOut, $"TimedOut (got {outcome})");
        Check(Math.Abs(waited.TotalSeconds - 60) < 0.001, $"gave up at 60 s, not 63 (got {waited.TotalSeconds})");
    }

    // Leaving the world and the bound landing on the same poll: leaving wins (the reload is not "anyway").
    private static void LeavingAtTheBoundIsStillCleared()
    {
        _case = "leaves at the bound";
        var now = T0;
        var outcome = ReloadInWorldGate.Run(
            stillHold: () => now - T0 < TimeSpan.FromMinutes(1),
            log: _ => { },
            notify: null,
            maxWait: TimeSpan.FromMinutes(1),
            waited: out TimeSpan waited,
            clock: () => now,
            sleep: t => now += t,
            poll: TimeSpan.FromSeconds(2));
        Check(outcome == ReloadInWorldGate.Outcome.ClearedAfterWait, $"ClearedAfterWait (got {outcome})");
    }

    private static void ThrowingHoldNeverWedges()
    {
        _case = "throwing stillHold";
        var outcome = ReloadInWorldGate.Run(
            stillHold: () => throw new InvalidOperationException("boom"),
            log: _ => { },
            notify: null,
            maxWait: Ten,
            waited: out TimeSpan waited,
            clock: () => DateTime.UtcNow,
            sleep: _ => { });
        Check(outcome == ReloadInWorldGate.Outcome.Clear, "throwing query fails open to Clear");
        Check(waited == TimeSpan.Zero, "no wait on throw");
    }

    private static void ThrowingHoldMidWaitReleases()
    {
        _case = "throwing mid-wait";
        var now = T0;
        int calls = 0;
        var outcome = ReloadInWorldGate.Run(
            stillHold: () => ++calls <= 3 ? true : throw new InvalidOperationException("boom"),
            log: _ => { },
            notify: null,
            maxWait: Ten,
            waited: out TimeSpan waited,
            clock: () => now,
            sleep: t => now += t);
        Check(outcome == ReloadInWorldGate.Outcome.ClearedAfterWait, $"fails open to ClearedAfterWait (got {outcome})");
    }

    private static void MaxMinutesClamp()
    {
        _case = "max minutes";
        Check(ReloadInWorldGate.DefaultMaxMinutes == 10, "default 10");
        Check(ReloadInWorldGate.ClampMaxMinutes(10) == 10, "10 stays");
        Check(ReloadInWorldGate.ClampMaxMinutes(1) == 1 && ReloadInWorldGate.ClampMaxMinutes(240) == 240, "1 and 240 are valid");
        Check(ReloadInWorldGate.ClampMaxMinutes(0) == 1 && ReloadInWorldGate.ClampMaxMinutes(-3) == 1, "below 1 clamps to 1");
        Check(ReloadInWorldGate.ClampMaxMinutes(241) == 240 && ReloadInWorldGate.ClampMaxMinutes(int.MaxValue) == 240, "above 240 clamps to 240");
    }
}
