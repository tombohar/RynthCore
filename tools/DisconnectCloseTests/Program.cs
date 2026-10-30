using RynthCore.Engine.Compatibility;
using V = RynthCore.Engine.Compatibility.ServerDisconnectClosePolicy.Verdict;

namespace DisconnectCloseTests;

/// <summary>
/// When the engine closes the client after AC lost the server (ServerDisconnectClosePolicy.cs).
/// Clients sat on "The server has disconnected" until someone clicked Quit Game, so no launcher
/// relaunched them (Drakkon and Aryze on DreamWeave, 2026-10-11 14:52, for 71+ minutes).
/// </summary>
internal static class Program
{
    private static int _checks, _failed;
    private static string _case = "";

    private static int Main()
    {
        NothingOffTheScreen();
        ClosesAfterTheDelay();
        WaitsForTheFlightDumpButNotForever();
        RetriesThenGivesUp();
        SettingOffOnlyDetects();
        TurnedOnWhileOnTheScreen();
        NothingWhileExiting();
        LeavingTheScreenStartsOver();
        ZeroClockStillCounts();

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

    private static void Eq(V actual, V expected, string what) =>
        Check(actual == expected, $"{what}: expected {expected}, got {actual}");

    /// <summary>Ticks every 16 ms from <paramref name="from"/> to <paramref name="to"/> (exclusive);
    /// returns the verdicts other than None with the time they came.</summary>
    private static List<(long At, V Verdict)> Run(ServerDisconnectClosePolicy p, long from, long to,
        bool disconnected = true, bool enabled = true, bool exiting = false, Func<long, bool>? dumpBusy = null)
    {
        var seen = new List<(long, V)>();
        for (long t = from; t < to; t += 16)
        {
            V v = p.Update(t, disconnected, enabled, exiting, dumpBusy?.Invoke(t) ?? false);
            if (v != V.None) seen.Add((t, v));
        }
        return seen;
    }

    private static void NothingOffTheScreen()
    {
        _case = nameof(NothingOffTheScreen);
        var p = new ServerDisconnectClosePolicy();
        Check(Run(p, 1000, 120_000, disconnected: false).Count == 0, "no verdicts in the world / at character select");
        Check(!p.OnScreen, "not on the screen");
    }

    private static void ClosesAfterTheDelay()
    {
        _case = nameof(ClosesAfterTheDelay);
        var p = new ServerDisconnectClosePolicy();
        Eq(p.Update(10_000, true, true, false, false), V.Detected, "first tick on the screen");
        Check(p.OnScreen, "on the screen");
        var seen = Run(p, 10_016, 15_100);
        Check(seen.Count == 1 && seen[0].Verdict == V.Close, $"one Close in the first 5.1 s ({seen.Count} verdicts)");
        Check(seen.Count == 1 && seen[0].At >= 15_000 && seen[0].At < 15_016, $"Close 5 s after the screen came (at {(seen.Count > 0 ? seen[0].At : -1)})");
        Eq(p.Update(15_200, true, true, false, false), V.None, "no second Close right after");
        Check(p.Attempts == 1, "one attempt");
    }

    private static void WaitsForTheFlightDumpButNotForever()
    {
        _case = nameof(WaitsForTheFlightDumpButNotForever);
        // Dump written from +3 s to +8 s: the close waits for it.
        var p = new ServerDisconnectClosePolicy();
        Eq(p.Update(0 + 100, true, true, false, true), V.Detected, "detected while the dump is scheduled");
        var seen = Run(p, 116, 30_000, dumpBusy: t => t < 8_100);
        Check(seen.Count >= 1 && seen[0].Verdict == V.Close && seen[0].At >= 8_100 && seen[0].At < 8_120, $"Close as soon as the dump is done (at {(seen.Count > 0 ? seen[0].At : -1)})");

        // A dump that never finishes: the close waits at most DumpGraceMs more.
        p = new ServerDisconnectClosePolicy();
        p.Update(100, true, true, false, true);
        seen = Run(p, 116, 30_000, dumpBusy: _ => true);
        long expected = 100 + ServerDisconnectClosePolicy.DefaultDelayMs + ServerDisconnectClosePolicy.DumpGraceMs;
        Check(seen.Count >= 1 && seen[0].Verdict == V.Close && seen[0].At >= expected && seen[0].At < expected + 16, $"Close at the 15 s cap (at {(seen.Count > 0 ? seen[0].At : -1)}, cap {expected})");
    }

    private static void RetriesThenGivesUp()
    {
        _case = nameof(RetriesThenGivesUp);
        var p = new ServerDisconnectClosePolicy();
        p.Update(1, true, true, false, false);
        var seen = Run(p, 17, 300_000);
        Check(seen.Count == 3, $"Close, Retry, GiveUp and nothing more ({string.Join(", ", seen)})");
        if (seen.Count != 3) return;
        Eq(seen[0].Verdict, V.Close, "first");
        Eq(seen[1].Verdict, V.Retry, "second");
        Eq(seen[2].Verdict, V.GiveUp, "third");
        long close = seen[0].At;
        Check(seen[1].At - close >= ServerDisconnectClosePolicy.RetryAfterMs && seen[1].At - close < ServerDisconnectClosePolicy.RetryAfterMs + 16, "Retry 20 s after Close");
        Check(seen[2].At - close >= ServerDisconnectClosePolicy.GiveUpAfterMs && seen[2].At - close < ServerDisconnectClosePolicy.GiveUpAfterMs + 16, "GiveUp 60 s after Close");
        Check(p.Attempts == 2, "two close attempts");
    }

    private static void SettingOffOnlyDetects()
    {
        _case = nameof(SettingOffOnlyDetects);
        var p = new ServerDisconnectClosePolicy();
        var seen = Run(p, 1, 600_000, enabled: false);
        Check(seen.Count == 1 && seen[0].Verdict == V.Detected, $"only Detected with the setting off ({string.Join(", ", seen)})");
    }

    private static void TurnedOnWhileOnTheScreen()
    {
        _case = nameof(TurnedOnWhileOnTheScreen);
        var p = new ServerDisconnectClosePolicy();
        Run(p, 1, 60_000, enabled: false);
        Eq(p.Update(60_000, true, true, false, false), V.Close, "turning it on past the delay closes at once");
    }

    private static void NothingWhileExiting()
    {
        _case = nameof(NothingWhileExiting);
        var p = new ServerDisconnectClosePolicy();
        Check(Run(p, 1, 120_000, exiting: true).Count == 0, "no verdicts while the client is already closing");

        // Quit Game clicked after the screen came, before the close: nothing more from us.
        p = new ServerDisconnectClosePolicy();
        p.Update(1, true, true, false, false);
        Run(p, 17, 2_000);
        Check(Run(p, 2_000, 120_000, exiting: true).Count == 0, "no Close once the client started exiting on its own");
    }

    private static void LeavingTheScreenStartsOver()
    {
        _case = nameof(LeavingTheScreenStartsOver);
        var p = new ServerDisconnectClosePolicy();
        p.Update(1, true, true, false, false);
        Run(p, 17, 2_000);
        Eq(p.Update(2_000, false, true, false, false), V.Left, "the screen went away before the close");
        Check(!p.OnScreen && p.Attempts == 0, "reset");
        Eq(p.Update(2_016, false, true, false, false), V.None, "Left only once");
        Eq(p.Update(50_000, true, true, false, false), V.Detected, "a second visit is detected again");
        var seen = Run(p, 50_016, 55_100);
        Check(seen.Count == 1 && seen[0].Verdict == V.Close && seen[0].At >= 55_000, "and closes 5 s into the second visit");
    }

    private static void ZeroClockStillCounts()
    {
        _case = nameof(ZeroClockStillCounts);
        var p = new ServerDisconnectClosePolicy();
        Eq(p.Update(0, true, true, false, false), V.Detected, "detected at clock 0");
        Check(p.OnScreen, "a clock of 0 still marks the screen as seen");
        var seen = Run(p, 16, 6_000);
        Check(seen.Count == 1 && seen[0].Verdict == V.Close, "closes");
    }
}
