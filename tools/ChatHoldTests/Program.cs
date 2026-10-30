using System.Collections.Concurrent;
using RynthCore.Engine.Compatibility;

namespace ChatHoldTests;

/// <summary>
/// The engine's incoming-chat hold (IncomingChatHold.cs) without AC: the hold decision, the
/// release order, eaten lines, the timeout, flushing, and a two-thread run shaped like the
/// real one (AC's main thread holds and prints, the plugin pump writes verdicts).
/// </summary>
internal static class Program
{
    private static int _checks, _failed;
    private static string _case = "";

    private static int Main()
    {
        DecideHoldsANormalLine();
        DecidePassThroughCases();
        DecideFlushCases();
        ReleaseKeepsOrder();
        ReleaseDropsEatenLines();
        ReleaseTimeout();
        ReleaseFlushAll();
        ReleasePrintThrows();
        FirstVerdictWins();
        PluginsAteItRule();
        MetaManagerPollShape();
        TwoThreads();

        Console.WriteLine();
        Console.WriteLine($"{_checks - _failed}/{_checks} checks passed.");
        Console.WriteLine(_failed == 0 ? "ALL CHAT HOLD TESTS PASSED." : $"{_failed} FAILED.");
        return _failed == 0 ? 0 : 1;
    }

    // ── the hold decision ───────────────────────────────────────────────────

    private const long Now = 100_000;

    private static ChatHoldInputs Normal() => new(
        Enabled: true, Disarmed: false, OnMainThread: true, Replaying: false,
        HasText: true, Truncated: false, HasStringInfo: false,
        NowMs: Now, PumpBeatMs: Now - 10, DrainBeatMs: Now - 10, HeldCount: 0);

    private static void DecideHoldsANormalLine()
    {
        Case("decide: a normal line is held");
        Check(HeldChatQueue.Decide(Normal()) == ChatHoldDecision.Hold, "normal line held");
        Check(HeldChatQueue.Decide(Normal() with { HeldCount = HeldChatQueue.MaxHeld - 1 }) == ChatHoldDecision.Hold, "held while under the cap");
        Check(HeldChatQueue.Decide(Normal() with { PumpBeatMs = Now - HeldChatQueue.PumpStaleMs }) == ChatHoldDecision.Hold, "pump beat exactly at the limit still holds");
        Check(HeldChatQueue.Decide(Normal() with { DrainBeatMs = Now - HeldChatQueue.DrainStaleMs }) == ChatHoldDecision.Hold, "drain beat exactly at the limit still holds");
    }

    private static void DecidePassThroughCases()
    {
        Case("decide: lines that print at once, held lines untouched");
        Check(HeldChatQueue.Decide(Normal() with { Replaying = true }) == ChatHoldDecision.PassThrough, "inside our own print");
        Check(HeldChatQueue.Decide(Normal() with { Replaying = true, Enabled = false }) == ChatHoldDecision.PassThrough, "inside our own print, even when switched off (never flush mid-release)");
        Check(HeldChatQueue.Decide(Normal() with { OnMainThread = false }) == ChatHoldDecision.PassThrough, "off AC's main thread");
        Check(HeldChatQueue.Decide(Normal() with { OnMainThread = false, Enabled = false }) == ChatHoldDecision.PassThrough, "off the main thread never touches the held list");
        Check(HeldChatQueue.Decide(Normal() with { HasStringInfo = true }) == ChatHoldDecision.PassThrough, "caller passed a StringInfo");
        Check(HeldChatQueue.Decide(Normal() with { Truncated = true }) == ChatHoldDecision.PassThrough, "line longer than we read");
        Check(HeldChatQueue.Decide(Normal() with { HasText = false }) == ChatHoldDecision.PassThrough, "empty line");
    }

    private static void DecideFlushCases()
    {
        Case("decide: holding off or unsafe flushes what is held");
        Check(HeldChatQueue.Decide(Normal() with { Enabled = false }) == ChatHoldDecision.FlushThenPassThrough, "/rc chathold off");
        Check(HeldChatQueue.Decide(Normal() with { Disarmed = true }) == ChatHoldDecision.FlushThenPassThrough, "engine teardown");
        Check(HeldChatQueue.Decide(Normal() with { PumpBeatMs = 0 }) == ChatHoldDecision.FlushThenPassThrough, "pump never ran");
        Check(HeldChatQueue.Decide(Normal() with { PumpBeatMs = Now - HeldChatQueue.PumpStaleMs - 1 }) == ChatHoldDecision.FlushThenPassThrough, "pump stalled");
        Check(HeldChatQueue.Decide(Normal() with { DrainBeatMs = 0 }) == ChatHoldDecision.FlushThenPassThrough, "drain never ran");
        Check(HeldChatQueue.Decide(Normal() with { DrainBeatMs = Now - HeldChatQueue.DrainStaleMs - 1 }) == ChatHoldDecision.FlushThenPassThrough, "drain stalled");
        Check(HeldChatQueue.Decide(Normal() with { HeldCount = HeldChatQueue.MaxHeld }) == ChatHoldDecision.FlushThenPassThrough, "too many held");
        Check(HeldChatQueue.Decide(Normal() with { Enabled = false, HasStringInfo = true }) == ChatHoldDecision.FlushThenPassThrough, "switched off wins over a StringInfo line (keeps the order)");
    }

    // ── releasing ───────────────────────────────────────────────────────────

    private static HeldChatLine Line(string text, long at = 0) => new(text + "\n", 0, 1, new IntPtr(0x1234), at);

    private static void ReleaseKeepsOrder()
    {
        Case("release: oldest first, stops at the first line still waiting");
        var q = new HeldChatQueue();
        var printed = new List<string>();
        HeldChatLine a = Line("a"), b = Line("b"), c = Line("c");
        q.Add(a); q.Add(b); q.Add(c);
        a.Decide(eat: false);
        c.Decide(eat: false);
        int n = q.Release(10, false, l => printed.Add(l.Raw));
        Check(n == 1 && printed.SequenceEqual(new[] { "a\n" }), "only a prints while b waits");
        Check(q.Count == 2, "b and c still held");
        b.Decide(eat: false);
        q.Release(20, false, l => printed.Add(l.Raw));
        Check(printed.SequenceEqual(new[] { "a\n", "b\n", "c\n" }), "then b and c, in order");
        Check(q.Count == 0 && q.ShownTotal == 3 && q.HeldTotal == 3, "counters");
        Check(q.Release(30, false, l => printed.Add(l.Raw)) == 0, "empty release prints nothing");
    }

    private static void ReleaseDropsEatenLines()
    {
        Case("release: eaten lines never print");
        var q = new HeldChatQueue();
        var printed = new List<string>();
        HeldChatLine a = Line("a"), b = Line("quest line"), c = Line("c");
        q.Add(a); q.Add(b); q.Add(c);
        a.Decide(false); b.Decide(true); c.Decide(false);
        q.Release(5, false, l => printed.Add(l.Raw));
        Check(printed.SequenceEqual(new[] { "a\n", "c\n" }), "a and c print, the eaten line doesn't");
        Check(q.EatenTotal == 1 && q.ShownTotal == 2 && q.Count == 0, "counters");
    }

    private static void ReleaseTimeout()
    {
        Case("release: a line with no verdict prints after the timeout");
        var q = new HeldChatQueue();
        var printed = new List<string>();
        HeldChatLine a = Line("a", at: 1000), b = Line("b", at: 1100);
        q.Add(a); q.Add(b);
        q.Release(1000 + HeldChatQueue.HoldTimeoutMs - 1, false, l => printed.Add(l.Raw));
        Check(printed.Count == 0, "not yet");
        q.Release(1000 + HeldChatQueue.HoldTimeoutMs, false, l => printed.Add(l.Raw));
        Check(printed.SequenceEqual(new[] { "a\n" }) && q.TimedOutTotal == 1, "a prints late; b (held later) still waits");
        b.Decide(true);
        q.Release(1000 + HeldChatQueue.HoldTimeoutMs + 1, false, l => printed.Add(l.Raw));
        Check(printed.Count == 1 && q.EatenTotal == 1 && q.Count == 0, "b eaten in time");
        a.Decide(true); // a verdict after the line already printed does nothing
        Check(q.EatenTotal == 1 && q.Count == 0, "late verdict ignored");
    }

    private static void ReleaseFlushAll()
    {
        Case("release: flushAll prints everything not eaten");
        var q = new HeldChatQueue();
        var printed = new List<string>();
        HeldChatLine a = Line("a"), b = Line("b"), c = Line("c");
        q.Add(a); q.Add(b); q.Add(c);
        b.Decide(true);
        q.Release(0, true, l => printed.Add(l.Raw));
        Check(printed.SequenceEqual(new[] { "a\n", "c\n" }), "a and c print without a verdict, eaten b still dropped");
        Check(q.TimedOutTotal == 2 && q.EatenTotal == 1 && q.Count == 0, "counters");
    }

    private static void ReleasePrintThrows()
    {
        Case("release: a print that throws doesn't stop the rest");
        var q = new HeldChatQueue();
        var printed = new List<string>();
        HeldChatLine a = Line("bad"), b = Line("b");
        q.Add(a); q.Add(b);
        a.Decide(false); b.Decide(false);
        q.Release(0, false, l => { if (l.Raw.StartsWith("bad")) throw new InvalidOperationException(); printed.Add(l.Raw); });
        Check(printed.SequenceEqual(new[] { "b\n" }) && q.PrintFailedTotal == 1 && q.Count == 0, "b still prints");
    }

    private static void FirstVerdictWins()
    {
        Case("verdict: the first one wins");
        HeldChatLine a = Line("a");
        Check(a.Verdict == HeldChatLine.Pending, "starts pending");
        a.Decide(true);
        a.Decide(false);
        Check(a.Verdict == HeldChatLine.Eat, "eat then show stays eat");
        HeldChatLine b = Line("b");
        b.Decide(false);
        b.Decide(true);
        Check(b.Verdict == HeldChatLine.Show, "show then eat stays show (an overflow drop can't be undone)");
        Check(a.Raw == "a\n" && a.Unknown == 1 && a.This == new IntPtr(0x1234), "fields kept for the reprint");
    }

    private static void PluginsAteItRule()
    {
        Case("verdict: eat flag and plugin errors");
        Check(HeldChatQueue.PluginsAteIt(1, false), "eat set, no error: eaten");
        Check(HeldChatQueue.PluginsAteIt(-1, false), "any non-zero eat counts");
        Check(!HeldChatQueue.PluginsAteIt(0, false), "nobody ate it: shows");
        Check(!HeldChatQueue.PluginsAteIt(1, true), "a plugin threw: shows even if eaten");
        Check(!HeldChatQueue.PluginsAteIt(0, true), "a plugin threw, not eaten: shows");
    }

    // ── shaped like the real thing ──────────────────────────────────────────

    // A burst of chat with RynthAi's own /myquests reply in the middle: only the reply goes.
    private static void MetaManagerPollShape()
    {
        Case("shape: a /myquests reply in the middle of combat text");
        var q = new HeldChatQueue();
        var printed = new List<string>();
        string[] lines =
        {
            "You hit the Drudge Slinker for 42 points of slashing damage!",
            "blightlordlairwait1008 - 3 solves (1759190400)\"Blight Lord Lair\" -1 72000",
            "stipendtimer_monthly - 2 solves (1759276800)\"Monthly stipend\" -1 2592000",
            "Vessa tells you, \"hi\"",
            "The Drudge Slinker hits you for 7 points of bludgeoning damage!",
        };
        var held = lines.Select(t => Line(t)).ToArray();
        foreach (var h in held) q.Add(h);
        // The pump: RynthAi eats the lines that parse as /myquests while its poll is in flight.
        foreach (var h in held) h.Decide(eat: h.Raw.Contains(" solves ("));
        q.Release(1, false, l => printed.Add(l.Raw.TrimEnd('\n')));
        Check(printed.SequenceEqual(new[] { lines[0], lines[3], lines[4] }), "combat, tell, combat print in order; the two quest lines don't");
    }

    // AC's main thread holds and releases while the pump decides on its own thread; with no
    // timeouts in play every line not eaten prints exactly once, in order.
    private static void TwoThreads()
    {
        Case("two threads: main holds/releases, pump decides");
        const int total = 20000;
        var q = new HeldChatQueue();
        var toPump = new ConcurrentQueue<HeldChatLine>();
        var printed = new List<int>();
        using var done = new ManualResetEventSlim(false);

        var pump = new Thread(() =>
        {
            int seen = 0;
            while (seen < total)
            {
                if (toPump.TryDequeue(out var h))
                {
                    int n = int.Parse(h.Raw.AsSpan(0, h.Raw.Length - 1));
                    h.Decide(eat: n % 7 == 3);
                    seen++;
                }
                else Thread.Yield();
            }
            done.Set();
        });
        pump.Start();

        for (int i = 0; i < total; i++)
        {
            var h = new HeldChatLine(i + "\n", 0, 1, IntPtr.Zero, 0);
            q.Add(h);
            toPump.Enqueue(h);
            if ((i & 15) == 0)
                q.Release(0, false, l => printed.Add(int.Parse(l.Raw.AsSpan(0, l.Raw.Length - 1)))); // clock frozen: no timeouts
        }
        done.Wait();
        pump.Join();
        q.Release(0, false, l => printed.Add(int.Parse(l.Raw.AsSpan(0, l.Raw.Length - 1))));

        var expected = Enumerable.Range(0, total).Where(n => n % 7 != 3).ToList();
        Check(q.Count == 0, "nothing left held");
        Check(printed.SequenceEqual(expected), "every line not eaten printed once, in order");
        Check(q.EatenTotal == total - expected.Count && q.TimedOutTotal == 0, "counters");
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
}
