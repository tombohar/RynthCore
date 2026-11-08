using System.Text.Json;
using RynthCore.Engine.UI.Data;

namespace DamageRowStatsTests;

/// <summary>
/// The line under a monster in the Damage panel (DamageRowStats): RynthAi's "mstats" on a
/// RynthPluginGetMonsterDamageJson row, read and worded. The JSON here is the shape RynthAi
/// writes (RynthSuite RynthAiHostTests MonsterStatsTests checks RynthAi's side of it).
/// </summary>
internal static class Program
{
    private static int _checks, _failed;

    private static void Check(bool cond, string what)
    {
        _checks++;
        if (cond) return;
        _failed++;
        Console.WriteLine("  [FAIL] " + what);
    }

    private static void Eq<T>(T actual, T expected, string what)
        => Check(EqualityComparer<T>.Default.Equals(actual, expected), $"{what}: expected '{expected}', got '{actual}'");

    private static DamageRowStats? Row(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return DamageRowStats.Parse(doc.RootElement);
    }

    private static int Main()
    {
        Reads();
        Missing();
        Words();
        Percents();
        XpPerKill();
        CorpseTime();
        ShieldTaken();
        Console.WriteLine($"DamageRowStatsTests: {_checks} checks, {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }

    private const string Full =
        "{\"wcid\":7,\"name\":\"Drudge Skulker\",\"mstats\":{\"sw\":61,\"ev\":14,\"sp\":30,\"rs\":3,\"ne\":2,"
        + "\"at\":120,\"yev\":50,\"ysp\":8,\"yrs\":1,\"yne\":0,\"k\":12,\"dd\":12345,\"dt\":2345}}";

    private static void Reads()
    {
        DamageRowStats? s = Row(Full);
        Check(s != null, "a row with mstats reads");
        if (s == null) return;
        Eq((s.Swings, s.Evaded, s.Spells, s.Resisted, s.NoEffect), (61, 14, 30, 3, 2), "your side");
        Eq((s.Attacks, s.YouEvaded, s.SpellsAtYou, s.YouResisted, s.NoEffectOnYou), (120, 50, 8, 1, 0), "its side");
        Eq((s.Kills, s.Dealt, s.Taken), (12, 12345L, 2345L), "kills and damage");

        DamageRowStats? part = Row("{\"mstats\":{\"at\":4,\"yev\":3}}");
        Check(part != null && part.Attacks == 4 && part.YouEvaded == 3 && part.Swings == 0 && part.Dealt == 0, "missing numbers are 0");
        DamageRowStats? odd = Row("{\"mstats\":{\"sw\":-5,\"ev\":\"x\",\"sp\":2.5,\"at\":3,\"yev\":1,\"dd\":-9}}");
        Check(odd != null && odd.Swings == 0 && odd.Evaded == 0 && odd.Spells == 0 && odd.Dealt == 0 && odd.Attacks == 3,
            "negative, text and fractional numbers read as 0");
        DamageRowStats? big = Row("{\"mstats\":{\"sw\":99999999999,\"ev\":1}}");
        Check(big != null && big.Swings == int.MaxValue, "a count past an int is capped");
    }

    private static void Missing()
    {
        Check(Row("{\"wcid\":7,\"name\":\"Drudge Skulker\",\"kills\":3}") == null, "an older RynthAi's row (no mstats): no line");
        Check(Row("{\"mstats\":{}}") == null, "nothing counted: no line");
        Check(Row("{\"mstats\":{\"sw\":0,\"at\":0}}") == null, "all zero: no line");
        Check(Row("{\"mstats\":5}") == null, "mstats not an object");
        Check(Row("[1,2]") == null, "not a row");
        Check(Row("{\"mstats\":{\"k\":1}}") != null, "a kill alone is something");
    }

    private static void Words()
    {
        DamageRowStats s = Row(Full)!;
        Eq(s.Line(), "It evades 23% (14/61)  ·  It resists 10% (3/30)  ·  You evade 42% (50/120)  ·  You resist 13% (1/8)", "the line");
        Eq(Row("{\"mstats\":{\"at\":4,\"yev\":3}}")!.Line(), "You evade 75% (3/4)", "only the parts with something to divide by");
        Eq(Row("{\"mstats\":{\"k\":1}}")!.Line(), "", "a kill alone: no rates to show (the row stays one line)");
        Eq(Row("{\"mstats\":{\"sw\":2000,\"ev\":1500}}")!.Line(), "It evades 75% (1,500/2,000)", "thousands");

        string tip = s.Tip();
        Check(tip.Contains("61 melee/missile attacks, 47 hit, it evaded 14 (23%)"), "tip: your attacks: " + tip);
        Check(tip.Contains("30 spells reached it, it resisted 3 (10%), 2 no effect (immune)"), "tip: your spells");
        Check(tip.Contains("12,345 damage, 12 kills"), "tip: damage and kills");
        Check(tip.Contains("120 melee/missile attacks, 70 hit, you evaded 50 (42%)"), "tip: its attacks");
        Check(tip.Contains("8 spells reached you, you resisted 1 (13%); 2,345 damage"), "tip: its spells (no 'no effect' when there is none)");
        Check(Row("{\"mstats\":{\"k\":1,\"sw\":1}}")!.Tip().Contains("1 kill."), "one kill");
    }

    private static void Percents()
    {
        Eq(DamageRowStats.Percent(0, 0), "-", "nothing to divide by");
        Eq(DamageRowStats.Percent(0, 5), "0%", "none");
        Eq(DamageRowStats.Percent(5, 5), "100%", "all");
        Eq(DamageRowStats.Percent(1, 8), "13%", "whole numbers from 10% (12.5 rounds up)");
        Eq(DamageRowStats.Percent(1, 3), "33%", "a third");
        Eq(DamageRowStats.Percent(2, 3), "67%", "two thirds");
        Eq(DamageRowStats.Percent(9, 200), "4.5%", "one decimal below 10%");
        Eq(DamageRowStats.Percent(7, 3), "100%", "clamped");
    }

    private static void XpPerKill()
    {
        // An older RynthAi sends no xp/xk: nothing new on the line or in the tip.
        DamageRowStats old = Row(Full)!;
        Eq((old.Xp, old.XpKills, old.XpPerKill), (0L, 0, 0L), "no xp/xk: 0");
        Check(!old.Line().Contains("XP/kill"), "no xp/xk: no XP/kill on the line");
        Check(!old.Tip().Contains("XP:"), "no xp/xk: no XP in the tip");

        DamageRowStats s = Row("{\"mstats\":{\"sw\":61,\"ev\":14,\"k\":3,\"xp\":3703701,\"xk\":3}}")!;
        Eq((s.Xp, s.XpKills, s.XpPerKill), (3703701L, 3, 1234567L), "xp and xk read");
        Eq(s.Line(), "XP/kill 1,234,567  ·  It evades 23% (14/61)", "XP/kill leads the line, thousands");
        Check(s.Tip().Contains("XP: 3,703,701 from 3 kills, 1,234,567 per kill."), "tip: XP " + s.Tip());

        Eq(Row("{\"mstats\":{\"xp\":5,\"xk\":2}}")!.XpPerKill, 3L, "rounded (2.5 up)");
        Eq(Row("{\"mstats\":{\"xp\":10,\"xk\":3}}")!.XpPerKill, 3L, "rounded (3.33 down)");
        Eq(Row("{\"mstats\":{\"k\":1,\"xp\":900,\"xk\":1}}")!.Line(), "XP/kill 900", "a kill with XP alone: the line shows XP/kill");
        Check(Row("{\"mstats\":{\"k\":1,\"xp\":900,\"xk\":1}}")!.Tip().Contains("from 1 kill,"), "one kill");
        Eq(Row("{\"mstats\":{\"k\":2,\"xp\":900,\"xk\":0}}")!.Line(), "", "xk 0: no XP/kill");
        Eq(Row("{\"mstats\":{\"k\":2,\"xp\":-50,\"xk\":-1}}")!.Line(), "", "negative xp/xk read as 0");
        Check(Row("{\"mstats\":{\"xp\":0,\"xk\":0}}") == null, "xp/xk all zero: no line");
        Eq(Row("{\"mstats\":{\"xp\":50000000000,\"xk\":2}}")!.Line(), "XP/kill 25,000,000,000", "XP past an int");
    }

    private static void CorpseTime()
    {
        // An older RynthAi sends no cd/cn: nothing new.
        DamageRowStats old = Row(Full)!;
        Eq((old.CorpseMs, old.CorpseKills), (0, 0), "no cd/cn: 0");
        Check(!old.Line().Contains("Death animation"), "no cd/cn: no death animation on the line");
        Check(!old.Tip().Contains("Corpse appears"), "no cd/cn: no corpse in the tip");

        DamageRowStats s = Row("{\"mstats\":{\"sw\":61,\"ev\":14,\"k\":3,\"xp\":3703701,\"xk\":3,\"cd\":2140,\"cn\":3}}")!;
        Eq((s.CorpseMs, s.CorpseKills), (2140, 3), "cd and cn read");
        Eq(s.Line(), "XP/kill 1,234,567  ·  It evades 23% (14/61)  ·  Death animation 2.1 s", "death animation last, after the rates");
        Eq(Row("{\"mstats\":{\"sw\":61,\"ev\":14,\"at\":120,\"yev\":50,\"ysp\":8,\"yrs\":1,\"k\":3,\"cd\":2140,\"cn\":3}}")!.Line(),
            "It evades 23% (14/61)  ·  You evade 42% (50/120)  ·  You resist 13% (1/8)  ·  Death animation 2.1 s", "far right, after You evade / You resist");
        Check(s.Tip().Contains("Corpse appears 2.1 s after the kill (over 3 kills)."), "tip: corpse " + s.Tip());
        Eq(Row("{\"mstats\":{\"k\":1,\"cd\":2140,\"cn\":1}}")!.Line(), "Death animation 2.1 s", "death animation alone (no XP sent)");
        Check(Row("{\"mstats\":{\"k\":1,\"cd\":900,\"cn\":1}}")!.Tip().Contains("(over 1 kill)."), "one kill");
        Check(Row("{\"mstats\":{\"cd\":1500,\"cn\":2}}") != null, "a timed corpse alone is something");

        // Rounding to one decimal.
        Eq(Row("{\"mstats\":{\"cd\":2150,\"cn\":1}}")!.CorpseSeconds, "2.2", "2.15 s rounds up");
        Eq(Row("{\"mstats\":{\"cd\":2149,\"cn\":1}}")!.CorpseSeconds, "2.1", "2.149 s rounds down");
        Eq(Row("{\"mstats\":{\"cd\":3000,\"cn\":1}}")!.CorpseSeconds, "3.0", "whole seconds keep the decimal");
        Eq(Row("{\"mstats\":{\"cd\":40,\"cn\":1}}")!.CorpseSeconds, "0.0", "under 50 ms");
        Eq(Row("{\"mstats\":{\"cd\":12345,\"cn\":1}}")!.Line(), "Death animation 12.3 s", "over ten seconds");

        // Bad values.
        Eq(Row("{\"mstats\":{\"k\":2,\"cd\":2000,\"cn\":0}}")!.Line(), "", "cn 0: no corpse");
        Eq(Row("{\"mstats\":{\"k\":2,\"cd\":2000}}")!.Line(), "", "cd without cn: no corpse");
        Eq(Row("{\"mstats\":{\"k\":2,\"cd\":2000,\"cn\":-4}}")!.Line(), "", "negative cn read as 0");
        Eq(Row("{\"mstats\":{\"cd\":-500,\"cn\":2}}")!.Line(), "Death animation 0.0 s", "negative cd read as 0");
        Eq(Row("{\"mstats\":{\"cd\":\"2000\",\"cn\":2}}")!.Line(), "Death animation 0.0 s", "text cd read as 0");
        Eq(Row("{\"mstats\":{\"k\":1,\"cd\":2000,\"cn\":1.5}}")!.Line(), "", "fractional cn read as 0");
        Check(Row("{\"mstats\":{\"cd\":0,\"cn\":0}}") == null, "cd/cn all zero: no line");
    }

    // RynthAi 2026-11-08: the hits it landed by shield and facing (learned across logins).
    private const string Split =
        "{\"mstats\":{\"hsf\":19,\"dsf\":209,\"msf\":11,\"hsb\":1,\"dsb\":274,\"msb\":274,\"hnf\":54,\"dnf\":2916,\"mnf\":80}}";

    private static void ShieldTaken()
    {
        // An older RynthAi sends no cells: nothing new.
        DamageRowStats old = Row(Full)!;
        Check(!old.HasShieldTaken, "no cells: none");
        Check(!old.Line().Contains("Hits taken"), "no cells: nothing on the line");
        Check(!old.Tip().Contains("Shield up"), "no cells: nothing in the tip");

        // Cells alone (nothing since login): a row of its own, the session's numbers left out.
        DamageRowStats? s = Row(Split);
        Check(s != null, "the cells alone are something");
        if (s == null) return;
        Check(!s.HasSession, "no session numbers");
        Eq((s.ShieldCells[0].Hits, s.ShieldCells[0].Total, s.ShieldCells[0].Max), (19, 209L, 11L), "shield front read");
        Eq(s.ShieldCells[1].Hits, 1, "shield behind read");
        Eq(s.ShieldCells[2].Hits, 0, "a missing cell is 0");
        Eq((s.ShieldHits(true), s.ShieldHits(false)), (20, 54), "hits each way");
        Eq(Math.Round(s.ShieldAvg(true), 2), 24.15, "shield mean over every facing ((209 + 274) / 20)");
        Eq(s.Line(), "Hits taken: shield 24 avg (front 95%), no shield 54 avg", "the line");
        string tip = s.Tip();
        Check(!tip.Contains("Since you logged in"), "no session block: " + tip);
        Check(tip.Contains("Shield up: 20 hits, avg 24 - front 19 hits, avg 11, max 11; behind 1 hit, avg 274, max 274; facing unknown -."),
            "tip: shield cells: " + tip);
        Check(tip.Contains("No shield: 54 hits, avg 54 - front 54 hits, avg 54, max 80; behind -; facing unknown -."), "tip: no shield cells");
        Check(tip.Contains("within 90 degrees of your facing"), "tip: ACE's rule");

        // With the session's numbers: the cells after XP/kill, the death animation last.
        DamageRowStats both = Row("{\"mstats\":{\"sw\":61,\"ev\":14,\"k\":3,\"xp\":3703701,\"xk\":3,\"cd\":2140,\"cn\":3,"
            + "\"hsf\":2,\"dsf\":20,\"msf\":12,\"hnb\":1,\"dnb\":60,\"mnb\":60}}")!;
        Eq(both.Line(), "XP/kill 1,234,567  ·  Hits taken: shield 10 avg (front 100%), no shield 60 avg  ·  It evades 23% (14/61)  ·  Death animation 2.1 s",
            "after XP/kill, before the rates; death animation last");
        Check(both.Tip().StartsWith("Since you logged in") && both.Tip().Contains("\n\nDamage it did to you, learned across logins:"),
            "tip: the session first, then the cells");

        // One side only, and facing never measured.
        Eq(Row("{\"mstats\":{\"hnf\":3,\"dnf\":150,\"mnf\":60}}")!.Line(), "Hits taken: no shield 50 avg", "no shield only");
        Eq(Row("{\"mstats\":{\"hsu\":4,\"dsu\":40,\"msu\":12}}")!.Line(), "Hits taken: shield 10 avg (facing unknown)", "facing never measured");
        Eq(Row("{\"mstats\":{\"hsf\":1,\"dsf\":5,\"hsu\":3,\"dsu\":15}}")!.Line(), "Hits taken: shield 5 avg (front 100%)",
            "front share over the measured ones only");

        // Bad values.
        Check(Row("{\"mstats\":{\"hsf\":0,\"dsf\":50}}") == null, "damage without hits: nothing");
        Check(Row("{\"mstats\":{\"hsf\":-3,\"dsf\":50}}") == null, "negative hits read as 0");
        Eq(Row("{\"mstats\":{\"hsf\":2,\"dsf\":-50}}")!.Line(), "Hits taken: shield 0 avg (front 100%)", "negative damage read as 0");
        Check(Row("{\"mstats\":{\"hsf\":1.5,\"dsf\":50}}") == null, "fractional hits read as 0");
    }
}
