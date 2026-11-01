// ============================================================================
//  RynthCore.Engine - UI/Data/DamageRowStats.cs
//  A Damage panel row's combat numbers since login (or the plugin's reset), as
//  the plugin sends them: the optional "mstats" object of a row of
//  RynthPluginGetMonsterDamageJson (RynthAi 2026-10-30, a player's request:
//  "combat statistics under individual monsters, evades/resists for both the
//  player and the monster"). DamageFace draws Line() under the monster's row and
//  Tip() over it. A plugin that sends no "mstats" gets no line (as before).
//
//    "mstats": { "sw": swings, "ev": evaded, "sp": spells, "rs": resisted, "ne": no effect,
//                "at": attacks, "yev": you evaded, "ysp": spells at you, "yrs": you resisted,
//                "yne": no effect on you, "k": kills, "dd": damage dealt, "dt": damage taken }
//
//  sw = your melee and missile attacks at it (hits + its evades), sp = your spells that
//  reached it (landed + resisted + no effect); at / ysp the same from it to you. Missing
//  numbers are 0. The engine only words them; the plugin counts.
//
//  Pure (System.Text.Json only): tools/DamageRowStatsTests compiles this file as is.
//  The plugin sees an engine that draws them through GetUiHookFlags bit 8.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace RynthCore.Engine.UI.Data;

internal sealed record DamageRowStats(
    int Swings, int Evaded, int Spells, int Resisted, int NoEffect,
    int Attacks, int YouEvaded, int SpellsAtYou, int YouResisted, int NoEffectOnYou,
    int Kills, long Dealt, long Taken)
{
    /// <summary>The row's "mstats", or null when it has none (or nothing counted in it).</summary>
    public static DamageRowStats? Parse(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("mstats", out JsonElement m) || m.ValueKind != JsonValueKind.Object)
            return null;
        var s = new DamageRowStats(I(m, "sw"), I(m, "ev"), I(m, "sp"), I(m, "rs"), I(m, "ne"),
            I(m, "at"), I(m, "yev"), I(m, "ysp"), I(m, "yrs"), I(m, "yne"), I(m, "k"), L(m, "dd"), L(m, "dt"));
        return s.IsEmpty ? null : s;
    }

    public bool IsEmpty => Swings == 0 && Spells == 0 && Attacks == 0 && SpellsAtYou == 0 && Kills == 0;

    /// <summary>"23%" (whole from 10% up), "4.5%" (one decimal below), "-" with nothing to divide by. RynthAi words it the same.</summary>
    public static string Percent(int part, int whole)
    {
        if (whole <= 0) return "-";
        double p = Math.Clamp((double)part / whole, 0, 1) * 100;
        p = p >= 10 ? Math.Round(p, MidpointRounding.AwayFromZero) : Math.Round(p, 1, MidpointRounding.AwayFromZero);
        return p.ToString("0.#", CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>
    /// The line under the row: "It evades 23% (14/61) · It resists 10% (3/30) · You evade 42% (50/120) ·
    /// You resist 13% (1/8)", each part only when there is something to divide by; "" for none.
    /// </summary>
    public string Line()
    {
        var parts = new List<string>(4);
        if (Swings > 0) parts.Add($"It evades {Percent(Evaded, Swings)} ({N(Evaded)}/{N(Swings)})");
        if (Spells > 0) parts.Add($"It resists {Percent(Resisted, Spells)} ({N(Resisted)}/{N(Spells)})");
        if (Attacks > 0) parts.Add($"You evade {Percent(YouEvaded, Attacks)} ({N(YouEvaded)}/{N(Attacks)})");
        if (SpellsAtYou > 0) parts.Add($"You resist {Percent(YouResisted, SpellsAtYou)} ({N(YouResisted)}/{N(SpellsAtYou)})");
        return string.Join("  ·  ", parts);
    }

    /// <summary>The tooltip: both ways in full.</summary>
    public string Tip()
    {
        string noEffect = NoEffect > 0 ? $", {N(NoEffect)} no effect (immune)" : "";
        string noEffectYou = NoEffectOnYou > 0 ? $", {N(NoEffectOnYou)} no effect (immune)" : "";
        return "Since you logged in (or /ra mstats reset):\n"
            + $"You at it: {N(Swings)} melee/missile attacks, {N(Swings - Evaded)} hit, it evaded {N(Evaded)} ({Percent(Evaded, Swings)}); "
            + $"{N(Spells)} spells reached it, it resisted {N(Resisted)} ({Percent(Resisted, Spells)}){noEffect}; "
            + $"{N(Dealt)} damage, {N(Kills)} {(Kills == 1 ? "kill" : "kills")}.\n"
            + $"It at you: {N(Attacks)} melee/missile attacks, {N(Attacks - YouEvaded)} hit, you evaded {N(YouEvaded)} ({Percent(YouEvaded, Attacks)}); "
            + $"{N(SpellsAtYou)} spells reached you, you resisted {N(YouResisted)} ({Percent(YouResisted, SpellsAtYou)}){noEffectYou}; "
            + $"{N(Taken)} damage.\n"
            + "The killing blow counts as a hit (melee, missile) or a landed spell (magic).";
    }

    private static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

    private static int I(JsonElement m, string key)
        => m.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long x)
            ? (int)Math.Clamp(x, 0, int.MaxValue) : 0;

    private static long L(JsonElement m, string key)
        => m.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long x) ? Math.Max(0, x) : 0;
}
