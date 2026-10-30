using RynthCore.Engine;

namespace ReloadDeferralTests;

/// <summary>
/// The engine's hot-reload deferral (ReloadDeferral.cs). Drakkon died on 2026-10-05 after an
/// engine reload took every plugin down for 8 s mid-fight; the engine now waits while a plugin
/// (RynthAi) says a reload would hurt, up to 2 minutes.
/// </summary>
internal static class Program
{
    private static int _checks, _failed;
    private static string _case = "";

    private static int Main()
    {
        NoBlockerReloadsAtOnce();
        WaitsWhileInCombatThenReloads();
        CapReloadsAnyway();
        ThrowingQueryNeverHolds();
        SeveralPluginsDescribed();

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

    /// <summary>A fake clock that only moves when the deferral sleeps.</summary>
    private sealed class Rig
    {
        public DateTime Now = new(2026, 10, 5, 13, 42, 54, DateTimeKind.Utc);
        public readonly List<string> Logs = new();
        public readonly List<string> Notes = new();
        public int Queries;
        public Func<int, IReadOnlyList<(string, string)>> Answer = _ => Array.Empty<(string, string)>();

        public ReloadDeferral Make() => new()
        {
            Query = () => Answer(++Queries),
            Log = Logs.Add,
            Notify = Notes.Add,
            Clock = () => Now,
            Sleep = t => Now += t,
        };
    }

    private static readonly (string, string)[] InCombat = { ("RynthAi", "in combat (a monster engaged, health 12% under Emergency Heal At 30%)") };

    private static void NoBlockerReloadsAtOnce()
    {
        _case = "no blocker";
        var r = new Rig();
        var outcome = r.Make().Run(out TimeSpan waited);
        Check(outcome == ReloadDeferral.Outcome.Clear, "nothing objects: Clear");
        Check(waited == TimeSpan.Zero, "no wait");
        Check(r.Queries == 1, "asked once");
        Check(r.Logs.Count == 0 && r.Notes.Count == 0, "nothing logged or said");
    }

    private static void WaitsWhileInCombatThenReloads()
    {
        _case = "in combat";
        var r = new Rig();
        // In combat for the first 80 answers (40 s at 0.5 s), then clear.
        r.Answer = n => n <= 80 ? InCombat : Array.Empty<(string, string)>();
        var outcome = r.Make().Run(out TimeSpan waited);
        Check(outcome == ReloadDeferral.Outcome.ClearedAfterWait, $"cleared after a wait (got {outcome})");
        Check(Math.Abs(waited.TotalSeconds - 40) < 0.01, $"waited 40 s (got {waited.TotalSeconds})");
        Check(r.Logs.Count > 0 && r.Logs[0].StartsWith("engine reload waiting: RynthAi in combat (a monster engaged", StringComparison.Ordinal),
              $"the first line names the plugin and why: {r.Logs.FirstOrDefault()}");
        int repeats = r.Logs.Count(l => l.StartsWith("engine reload waiting:", StringComparison.Ordinal));
        Check(repeats == 3, $"the waiting line at once, then every 15 s (0, 15, 30 s: 3 lines, got {repeats})");
        Check(r.Logs[^1].StartsWith("engine reload: the plugins are clear after 40.0 s", StringComparison.Ordinal), $"the last line says it goes now: {r.Logs[^1]}");
        Check(r.Notes.Count == 1 && r.Notes[0].Contains("RynthAi in combat"), "the player is told once");
    }

    private static void CapReloadsAnyway()
    {
        _case = "cap";
        var r = new Rig();
        r.Answer = _ => InCombat;   // never clears
        var outcome = r.Make().Run(out TimeSpan waited);
        Check(outcome == ReloadDeferral.Outcome.TimedOut, $"timed out (got {outcome})");
        Check(waited >= ReloadDeferral.DefaultCap && waited < ReloadDeferral.DefaultCap + TimeSpan.FromSeconds(1),
              $"at the 2-minute cap (got {waited.TotalSeconds} s)");
        Check(r.Logs[^1].Contains("reloading anyway") && r.Logs[^1].Contains("RynthAi in combat"), $"and says so: {r.Logs[^1]}");
        Check(r.Queries <= 242, $"asked about every 0.5 s, no busier (got {r.Queries})");
    }

    private static void ThrowingQueryNeverHolds()
    {
        _case = "query throws";
        var r = new Rig();
        r.Answer = _ => throw new InvalidOperationException("plugin list changed");
        var outcome = r.Make().Run(out TimeSpan waited);
        Check(outcome == ReloadDeferral.Outcome.Clear, "a failed query doesn't hold the reload");
        Check(waited == TimeSpan.Zero, "no wait");
        Check(r.Logs.Count == 1 && r.Logs[0].Contains("asking the plugins failed"), "and it is logged");

        // Throws while waiting: counts as clear too.
        var w = new Rig();
        w.Answer = n => n == 1 ? InCombat : throw new InvalidOperationException("boom");
        Check(w.Make().Run(out _) == ReloadDeferral.Outcome.ClearedAfterWait, "a query that fails mid-wait ends the wait");
    }

    private static void SeveralPluginsDescribed()
    {
        _case = "describe";
        string d = ReloadDeferral.Describe(new[] { ("RynthAi", "in combat (a monster engaged)"), ("RynthLua", "busy (a script step)") });
        Check(d == "RynthAi in combat (a monster engaged); RynthLua busy (a script step)", $"joined: {d}");
    }
}
