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
//                "yne": no effect on you, "k": kills, "dd": damage dealt, "dt": damage taken,
//                "xp": XP from its kills, "xk": the kills that XP is from,
//                "cd": average ms from our kill to its corpse appearing, "cn": kills that was timed over,
//                "h<c>": hits it landed on you, "d<c>": their total damage, "m<c>": the biggest,
//                  per cell c = shield (s: one in the off hand in a combat stance, n: none) +
//                  facing (f: it stood within 90 degrees of your facing, b: outside it, u: unknown):
//                  sf sb su nf nb nu }
//
//  The h/d/m cells (RynthAi 2026-11-08: does a shield cut a boss's hits?) are learned across
//  logins (monster_damage.txt's TS rows), not since login; RynthAi sends them even with nothing
//  counted since login, and then nothing else. ACE counts a shield only against an attacker in
//  front (Creature_Combat.GetShieldMod), so "front" is the share of shield hits where it could.
//
//  sw = your melee and missile attacks at it (hits + its evades), sp = your spells that
//  reached it (landed + resisted + no effect); at / ysp the same from it to you. xp / xk
//  (RynthAi 2026-11-08, for Outdoor Hunt's Most XP) give "XP/kill" first on the line; an
//  older RynthAi sends neither. cd / cn (its death animation) give "Death animation 2.1 s"
//  last on the line, only when cn > 0. Missing numbers are 0. The engine only words them; the
//  plugin counts.
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
    int Kills, long Dealt, long Taken, long Xp = 0, int XpKills = 0, int CorpseMs = 0, int CorpseKills = 0)
{
    /// <summary>One cell of the hits it landed by shield and facing: count, total damage, biggest.</summary>
    internal readonly record struct TakenCell(int Hits, long Total, long Max)
    {
        public double Avg => Hits > 0 ? (double)Total / Hits : 0;
    }

    /// <summary>The cells' keys, in <see cref="ShieldCells"/> order: shield front, behind, unknown; no shield the same.</summary>
    public static readonly string[] CellKeys = { "sf", "sb", "su", "nf", "nb", "nu" };

    /// <summary>The hits it landed on you by shield and facing (<see cref="CellKeys"/> order); all zero when not sent.</summary>
    public TakenCell[] ShieldCells { get; init; } = new TakenCell[6];

    /// <summary>The row's "mstats", or null when it has none (or nothing counted in it).</summary>
    public static DamageRowStats? Parse(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("mstats", out JsonElement m) || m.ValueKind != JsonValueKind.Object)
            return null;
        var cells = new TakenCell[6];
        for (int i = 0; i < 6; i++)
        {
            string k = CellKeys[i];
            int h = I(m, "h" + k);
            cells[i] = h > 0 ? new TakenCell(h, L(m, "d" + k), L(m, "m" + k)) : default;
        }
        var s = new DamageRowStats(I(m, "sw"), I(m, "ev"), I(m, "sp"), I(m, "rs"), I(m, "ne"),
            I(m, "at"), I(m, "yev"), I(m, "ysp"), I(m, "yrs"), I(m, "yne"), I(m, "k"), L(m, "dd"), L(m, "dt"),
            L(m, "xp"), I(m, "xk"), I(m, "cd"), I(m, "cn")) { ShieldCells = cells };
        return s.IsEmpty ? null : s;
    }

    /// <summary>Something was counted since login (the evades, resists, kills, XP, corpse times).</summary>
    public bool HasSession => Swings != 0 || Spells != 0 || Attacks != 0 || SpellsAtYou != 0 || Kills != 0 || XpKills != 0 || CorpseKills != 0;

    /// <summary>Hits learned by shield and facing.</summary>
    public bool HasShieldTaken => ShieldHits(true) + ShieldHits(false) > 0;

    public bool IsEmpty => !HasSession && !HasShieldTaken;

    /// <summary>Hits it landed with a shield up (<paramref name="shield"/>) or without, every facing.</summary>
    public int ShieldHits(bool shield)
    {
        int o = shield ? 0 : 3;
        return ShieldCells[o].Hits + ShieldCells[o + 1].Hits + ShieldCells[o + 2].Hits;
    }

    /// <summary>Their mean damage (0 with none).</summary>
    public double ShieldAvg(bool shield)
    {
        int o = shield ? 0 : 3;
        int n = ShieldHits(shield);
        return n > 0 ? (double)(ShieldCells[o].Total + ShieldCells[o + 1].Total + ShieldCells[o + 2].Total) / n : 0;
    }

    /// <summary>
    /// "Hits taken: shield 11 avg (front 95%), no shield 54 avg": the mean hit with a shield up and
    /// the share of those from in front (of the ones measured; "facing unknown" when none was),
    /// then without one; each half only with hits; "" with none.
    /// </summary>
    public string ShieldLine()
    {
        var parts = new List<string>(2);
        int sh = ShieldHits(true);
        if (sh > 0)
        {
            int front = ShieldCells[0].Hits, behind = ShieldCells[1].Hits;
            string facing = front + behind > 0 ? $"front {Percent(front, front + behind)}" : "facing unknown";
            parts.Add($"shield {N(Whole(ShieldAvg(true)))} avg ({facing})");
        }
        if (ShieldHits(false) > 0) parts.Add($"no shield {N(Whole(ShieldAvg(false)))} avg");
        return parts.Count == 0 ? "" : "Hits taken: " + string.Join(", ", parts);
    }

    /// <summary>The tooltip's block: every cell in full, and ACE's rule.</summary>
    public string ShieldTip()
    {
        if (!HasShieldTaken) return "";
        string Cell(int i, string facing)
        {
            TakenCell c = ShieldCells[i];
            return c.Hits == 0 ? $"{facing} -" : $"{facing} {N(c.Hits)} {(c.Hits == 1 ? "hit" : "hits")}, avg {N(Whole(c.Avg))}, max {N(c.Max)}";
        }
        string Half(bool shield)
        {
            int o = shield ? 0 : 3;
            int n = ShieldHits(shield);
            return (shield ? "Shield up: " : "No shield: ")
                + (n == 0 ? "no hits" : $"{N(n)} {(n == 1 ? "hit" : "hits")}, avg {N(Whole(ShieldAvg(shield)))} - "
                    + Cell(o, "front") + "; " + Cell(o + 1, "behind") + "; " + Cell(o + 2, "facing unknown"));
        }
        return "Damage it did to you, learned across logins:\n"
            + Half(true) + ".\n" + Half(false) + ".\n"
            + "A shield counts only in a combat stance and against an attacker within 90 degrees of your facing (front).";
    }

    private static long Whole(double v) => (long)Math.Round(v, MidpointRounding.AwayFromZero);

    /// <summary>The death animation in seconds, one decimal ("2.1"): cd / 1000, halves rounded up.</summary>
    public string CorpseSeconds
        => (Math.Round(CorpseMs / 100.0, MidpointRounding.AwayFromZero) / 10).ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>XP per kill, rounded (xp / xk); 0 when no kill counted XP.</summary>
    public long XpPerKill => XpKills > 0 ? (long)Math.Round((double)Xp / XpKills, MidpointRounding.AwayFromZero) : 0;

    /// <summary>"23%" (whole from 10% up), "4.5%" (one decimal below), "-" with nothing to divide by. RynthAi words it the same.</summary>
    public static string Percent(int part, int whole)
    {
        if (whole <= 0) return "-";
        double p = Math.Clamp((double)part / whole, 0, 1) * 100;
        p = p >= 10 ? Math.Round(p, MidpointRounding.AwayFromZero) : Math.Round(p, 1, MidpointRounding.AwayFromZero);
        return p.ToString("0.#", CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>
    /// The line under the row: "XP/kill 1,234,567 · Hits taken: shield 24 avg (front 95%), no shield 54 avg ·
    /// It evades 23% (14/61) · It resists 10% (3/30) · You evade 42% (50/120) · You resist 13% (1/8) ·
    /// Death animation 2.1 s", each part only when there is something to divide by; "" for none. XP/kill
    /// leads so a narrow panel's clip never cuts it, the hits taken by shield (<see cref="ShieldLine"/>)
    /// follow it, and the death animation sits at the far right (Tom 2026-11-08).
    /// </summary>
    public string Line()
    {
        var parts = new List<string>(7);
        if (XpKills > 0) parts.Add($"XP/kill {N(XpPerKill)}");
        string shield = ShieldLine();
        if (shield.Length > 0) parts.Add(shield);
        if (Swings > 0) parts.Add($"It evades {Percent(Evaded, Swings)} ({N(Evaded)}/{N(Swings)})");
        if (Spells > 0) parts.Add($"It resists {Percent(Resisted, Spells)} ({N(Resisted)}/{N(Spells)})");
        if (Attacks > 0) parts.Add($"You evade {Percent(YouEvaded, Attacks)} ({N(YouEvaded)}/{N(Attacks)})");
        if (SpellsAtYou > 0) parts.Add($"You resist {Percent(YouResisted, SpellsAtYou)} ({N(YouResisted)}/{N(SpellsAtYou)})");
        if (CorpseKills > 0) parts.Add($"Death animation {CorpseSeconds} s");
        return string.Join("  ·  ", parts);
    }

    /// <summary>The tooltip: both ways in full, then the hits taken by shield and facing.</summary>
    public string Tip()
    {
        string shield = ShieldTip();
        if (!HasSession) return shield;
        return SessionTip() + (shield.Length > 0 ? "\n\n" + shield : "");
    }

    /// <summary>The numbers since login in full.</summary>
    private string SessionTip()
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
            + (XpKills > 0 ? $"XP: {N(Xp)} from {N(XpKills)} {(XpKills == 1 ? "kill" : "kills")}, {N(XpPerKill)} per kill.\n" : "")
            + (CorpseKills > 0 ? $"Corpse appears {CorpseSeconds} s after the kill (over {N(CorpseKills)} {(CorpseKills == 1 ? "kill" : "kills")}).\n" : "")
            + "The killing blow counts as a hit (melee, missile) or a landed spell (magic).";
    }

    private static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

    private static int I(JsonElement m, string key)
        => m.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long x)
            ? (int)Math.Clamp(x, 0, int.MaxValue) : 0;

    private static long L(JsonElement m, string key)
        => m.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long x) ? Math.Max(0, x) : 0;
}
