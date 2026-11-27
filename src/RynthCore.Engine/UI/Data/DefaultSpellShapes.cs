// ============================================================================
//  RynthCore.Engine - UI/Data/DefaultSpellShapes.cs
//  Settings > Spell Combat > Attack Spell Shapes (2026-11-26): the DEFAULT
//  monster line's war/void spell shapes (streak, arc, blast, bolt, ring,
//  wall) as plain checkboxes, so "cast streaks instead of bolts" is found
//  where the other attack-spell settings are. Until then it was only on the
//  Damage panel's DEFAULT line (Monster Detail > Shapes), which a new player
//  didn't find.
//
//  The same flags as Monster Detail's Shapes row: these rows read the rules
//  RynthAi sends (UiSources.MonsterRules) and edit the Default rule through
//  DamageCommands.EditRule (the latest rules read fresh, on the pump thread),
//  the path Monster Detail uses. No new plugin export.
//
//  A monster with its own entry (an exact-name rule from the Damage panel, or
//  a name rule) has its own copy of the shapes and keeps them; the status
//  line under the checkboxes says how many do, and which cast something else.
//
//  Pure except the "live" members at the bottom (Loaded, Get, Edit, SummaryNow),
//  so tools\CombatSettingsTests can check the rules by reflection.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text;
using Rule = RynthCore.Engine.UI.Panels.MonstersPanel.Rule;

namespace RynthCore.Engine.UI.Data;

internal static class DefaultSpellShapes
{
    public const string SectionLabel = "Attack Spell Shapes (Default monster)";
    public const string OpenMonstersLabel = "Per monster";

    /// <summary>The checkboxes, in the order they're drawn: (rule field, label, tooltip).</summary>
    public static readonly (string Field, string Label, string Tip)[] Rows =
    {
        ("UseStreak", "Use streaks instead of bolts",
            "Streak spells (Flame Streak, Frost Streak...): a fast projectile that is hard to dodge.\n" +
            "On: streaks are cast instead of bolts. Arcs come first when Use arcs is on too."),
        ("UseArc", "Use arcs instead of bolts",
            "Arc spells (Flame Arc, Frost Arc...): lobbed over low cover.\n" +
            "On: arcs are cast first, before streaks, blasts and bolts."),
        ("UseBlast", "Use blasts instead of bolts",
            "Blast spells (Flame Blast, Frost Blast...): a spread of projectiles, from level III.\n" +
            "Used instead of a bolt when both are on (arcs and streaks come first). With Blast Range\n" +
            "above 0 (below), a blast is cast only when enough monsters are in its fan."),
        ("UseBolt", "Use bolts",
            "Bolt spells (Flame Bolt, Frost Bolt...): the default shape, cast when no shape above is on.\n" +
            "Something is always on: turning the last shape off turns bolts back on."),
        ("UseRing", "Use rings when monsters crowd",
            "Ring spells around you, instead of the shape above, when at least Min Ring Targets\n" +
            "monsters are within Ring Range (Ring Spell Override below, and Ranges)."),
        ("UseWall", "Use walls when monsters line up",
            "Wall spells (Slithering Flames, Os' Wall...): cast when Wall Range is above 0 and\n" +
            "Min Wall Targets monsters stand in the wall's path (Wall Spell Override below)."),
    };

    public const string SectionTip =
        "War and Void Magic shapes for every monster without an entry of its own.\n" +
        "The same switches as the DEFAULT line's Shapes in the Damage panel (Monsters > DEFAULT).\n" +
        "A monster with its own entry keeps its own shapes: Monsters, click its name.";

    public const string OpenMonstersTip =
        "Opens the Damage panel (the dashboard's Monsters button): click a monster's name for its own shapes,\n" +
        "or DEFAULT for these.";

    public static bool IsShapeField(string field) =>
        field is "UseArc" or "UseBolt" or "UseRing" or "UseStreak" or "UseBlast" or "UseWall";

    /// <summary>True for one of this section's checkbox labels.</summary>
    public static bool IsShapeLabel(string? label)
    {
        if (label == null) return false;
        foreach (var r in Rows) if (r.Label == label) return true;
        return false;
    }

    public static bool IsDefault(Rule r) => r.Name.Equals("Default", StringComparison.OrdinalIgnoreCase);

    /// <summary>The Default rule (null when there is none, or no rules).</summary>
    public static Rule? DefaultRule(IReadOnlyList<Rule>? rules)
    {
        if (rules == null) return null;
        foreach (Rule r in rules) if (IsDefault(r)) return r;
        return null;
    }

    /// <summary>
    /// Sets one shape on <paramref name="rule"/>. A rule is never left with no shape (RynthAi then
    /// refuses to attack with magic): turning the last one off turns Bolt on, as Monster Detail does.
    /// </summary>
    public static void Set(Rule rule, string field, bool on)
    {
        if (!IsShapeField(field)) return;
        rule.SetToggle(field, on);
        if (!on && !HasAnyShape(rule)) rule.UseBolt = true;
    }

    public static bool HasAnyShape(Rule r) => r.UseArc || r.UseBolt || r.UseRing || r.UseStreak || r.UseBlast || r.UseWall;

    /// <summary>The same six shapes on both.</summary>
    public static bool SameShapes(Rule a, Rule b) =>
        a.UseArc == b.UseArc && a.UseBolt == b.UseBolt && a.UseRing == b.UseRing
        && a.UseStreak == b.UseStreak && a.UseBlast == b.UseBlast && a.UseWall == b.UseWall;

    /// <summary>"Streak", "Streak, Ring when enough are near", ... (RynthAi CombatManager.PickBaseShape's order).</summary>
    public static string Casts(Rule r)
    {
        string b = DamageCommands.BaseShapeName(r);
        if (b.Length == 0) return "nothing";
        var sb = new StringBuilder(b);
        if (r.UseRing) sb.Append(", Ring when enough are near");
        if (r.UseWall) sb.Append(", Wall when they line up");
        return sb.ToString();
    }

    /// <summary>Monster names shown in the line before "+N more".</summary>
    public const int NamesShown = 3;

    /// <summary>
    /// The status line under the checkboxes: what the Default line casts, and the monsters with
    /// their own entry ("3 monsters have their own setting: ..."). Empty when there's no Default rule.
    /// </summary>
    public static string Summary(IReadOnlyList<Rule>? rules)
    {
        Rule? def = DefaultRule(rules);
        if (def == null || rules == null) return string.Empty;
        var differ = new List<string>();
        int own = 0;
        foreach (Rule r in rules)
        {
            if (IsDefault(r) || string.IsNullOrWhiteSpace(r.Name)) continue;
            own++;
            if (!SameShapes(r, def)) differ.Add(r.Name.Trim());
        }

        var sb = new StringBuilder();
        sb.Append(own == 0 ? "Every monster casts " : "Monsters without their own entry cast ").Append(Casts(def)).Append('.');
        if (own == 0) return sb.ToString();

        if (differ.Count > 0)
        {
            sb.Append(' ').Append(Count(differ.Count)).Append(differ.Count == 1 ? " has its own setting: " : " have their own setting: ");
            for (int i = 0; i < Math.Min(NamesShown, differ.Count); i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(differ[i]);
            }
            if (differ.Count > NamesShown) sb.Append(" (+").Append(differ.Count - NamesShown).Append(" more)");
            sb.Append('.');
        }
        int same = own - differ.Count;
        if (same > 0)
        {
            sb.Append(' ');
            if (differ.Count > 0) sb.Append(same.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" more");
            else sb.Append(Count(same));
            sb.Append(same == 1 ? " has its own entry, set the same for now." : " have their own entry, set the same for now.");
        }
        sb.Append(own == 1 ? " It keeps its own shapes" : " Those keep their own shapes")
          .Append(": change one in Monsters (click its name).");
        return sb.ToString();
    }

    private static string Count(int n) => n == 1 ? "1 monster" : n.ToString(System.Globalization.CultureInfo.InvariantCulture) + " monsters";

    // ── Live: the rules RynthAi sent (Settings is drawn on AC's thread) ──────────

    private static IReadOnlyList<Rule>? CurrentRules => UiSources.MonsterRules.Current?.Value.Parsed.Rules;

    /// <summary>RynthAi has sent its monster rules, Default among them (the rows show).</summary>
    public static bool Loaded => DefaultRule(CurrentRules) != null;

    /// <summary>The Default rule's <paramref name="field"/> as last sent.</summary>
    public static bool Get(string field) => DefaultRule(CurrentRules)?.GetToggle(field) ?? false;

    /// <summary>Sets the Default rule's <paramref name="field"/> (DamageCommands: the latest rules, pump thread).</summary>
    public static void Edit(string field, bool on) => DamageCommands.EditRule("Default", r => Set(r, field, on));

    public static string SummaryNow() => Summary(CurrentRules);
}
