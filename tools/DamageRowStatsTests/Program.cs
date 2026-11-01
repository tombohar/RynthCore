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
}
