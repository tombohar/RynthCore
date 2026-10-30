// ============================================================================
//  RynthCore.Engine - UI/Data/MetaRowText.cs
//  How the Meta panel's rule list reads a rule (2026-10-11 readability pass):
//  words in one colour, the values you typed in another, state names in the
//  state headers' amber, and a problem (an empty chat command, a Set State to
//  a state that doesn't exist) in red.
//
//    Count  Prismatic Taper ≤ 50          (was "Inventory Item Count <=: Prismatic Taper, 50")
//    Expr  getcharintprop[25]<30
//    and not Spell 512 ≥ 305 s left       (an All of two: one line each, joined by "and")
//    /vt opt set enablecombat true        (a DoAll: one line per action, in order)
//    → ReBuff
//
//  Full form: a list of lines (nested All / Any indent under an "and any of"
//  line). Compact form: one line ("a and (b or c)", actions joined by " · ").
//  An All or Any of one condition is just that condition, as RynthAi runs it.
//
//  No ImGui here (tools/MetaEditTests compiles this file): MetaFace turns
//  tones into colours and draws the lines.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RynthCore.Engine.UI.Data;

/// <summary>What a piece of rule text is, for its colour.</summary>
internal enum MetaTone : byte
{
    /// <summary>Words: "Count", "Spell", "s left".</summary>
    Label,
    /// <summary>What was typed: an item name, a number, a chat command.</summary>
    Value,
    /// <summary>A state name (Set / Call Meta State, Watchdog).</summary>
    State,
    /// <summary>An expression or regex.</summary>
    Code,
    /// <summary>"and" / "or" / "not".</summary>
    Join,
    /// <summary>Never, None, "(nothing)".</summary>
    Dim,
    /// <summary>Something that won't work as written: "(empty)", a missing state.</summary>
    Warn,
}

internal readonly struct MetaSeg
{
    public readonly string Text;
    public readonly MetaTone Tone;
    public MetaSeg(string text, MetaTone tone) { Text = text; Tone = tone; }
    public override string ToString() => Text;
}

/// <summary>One line of a rule's condition or action column (full form).</summary>
internal sealed class MetaLine
{
    /// <summary>Nesting: the line starts this many slots in (a guide line is drawn in each).</summary>
    public int Depth;
    /// <summary>The line is an item of an and / or list: <see cref="Join"/> sits in a slot before it,
    /// so the items' text lines up whether or not they have a join word.</summary>
    public bool Slot;
    /// <summary>"and" / "or" on a list's second and later items; "" otherwise.</summary>
    public string Join = string.Empty;
    public MetaSeg[] Segs = Array.Empty<MetaSeg>();

    public string Plain()
    {
        var sb = new StringBuilder();
        if (Join.Length > 0) sb.Append(Join).Append(' ');
        foreach (MetaSeg s in Segs) sb.Append(s.Text);
        return sb.ToString();
    }
}

/// <summary>What the text needs from outside: spell names, which states exist.</summary>
internal sealed class MetaTextContext
{
    /// <summary>A spell's name, or null when unknown (the spell table may still be loading).</summary>
    public Func<uint, string?>? SpellName;
    /// <summary>The meta's states (any case); null skips the missing-state check.</summary>
    public ICollection<string>? States;
    /// <summary>True when the meta or NavProfiles has a route of this name (then "Patrol" is that
    /// route, not the dungeon patrol); null: no route is called Patrol.</summary>
    public Func<string, bool>? RouteExists;
    /// <summary>Set when a spell id was shown as a number for want of a name.</summary>
    public bool MissedSpellName;

    public bool StateExists(string s)
    {
        if (States == null) return true;
        foreach (string x in States)
            if (string.Equals(x, s, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

internal static class MetaRowText
{
    public const int All = 2, Any = 3, Not = 20, DoAll = 4;
    private const int MaxLevels = 8;   // the editor nests 4; a hand-written file could go deeper

    public const string Le = "≤", Ge = "≥", Arrow = "→", Dot = "·";

    // =====================================================================
    //  Conditions
    // =====================================================================

    /// <summary>The condition column's lines. An All / Any of two or more at the top lists its
    /// conditions one per line, the second and later ones starting "and" / "or".</summary>
    public static MetaLine[] ConditionLines(MetaRuleDto rule, MetaTextContext ctx)
    {
        var lines = new List<MetaLine>();
        Cond(rule, 0, slot: false, join: "", lead: null, lines, ctx, 0);
        return lines.ToArray();
    }

    // c's lines. depth/slot/join place its first line (an item of the parent's list gets the slot);
    // lead goes before its text on that line ("not ").
    private static void Cond(MetaRuleDto c, int depth, bool slot, string join, List<MetaSeg>? lead,
        List<MetaLine> lines, MetaTextContext ctx, int level)
    {
        if (level > MaxLevels)
        {
            lines.Add(Line(depth, slot, join, lead, new MetaSeg("…", MetaTone.Dim)));
            return;
        }
        if (c.Condition is All or Any)
        {
            List<MetaRuleDto> kids = c.Children;
            string what = c.Condition == All ? "all of" : "any of";
            if (kids.Count == 0)
            {
                lines.Add(Line(depth, slot, join, lead, new MetaSeg(what, MetaTone.Label), new MetaSeg(" (nothing)", MetaTone.Warn)));
                return;
            }
            if (kids.Count == 1)
            {
                Cond(kids[0], depth, slot, join, lead, lines, ctx, level + 1);   // the same as the one condition
                return;
            }
            string word = c.Condition == All ? "and" : "or";
            int kidDepth = depth;
            if (slot || lead != null)
            {
                // An item of a list, or after "not": an "all of" / "any of" line, the conditions under it.
                lines.Add(Line(depth, slot, join, lead, new MetaSeg(what, MetaTone.Label)));
                kidDepth = depth + 1;
            }
            for (int i = 0; i < kids.Count; i++)
                Cond(kids[i], kidDepth, slot: true, join: i == 0 ? "" : word, lead: null, lines, ctx, level + 1);
            return;
        }
        if (c.Condition == Not)
        {
            var notLead = new List<MetaSeg>();
            if (lead != null) notLead.AddRange(lead);
            notLead.Add(new MetaSeg("not ", MetaTone.Join));
            if (c.Children.Count == 0)
            {
                notLead.Add(new MetaSeg("(nothing)", MetaTone.Warn));
                lines.Add(Line(depth, slot, join, notLead));
                return;
            }
            Cond(c.Children[0], depth, slot, join, notLead, lines, ctx, level + 1);
            return;
        }
        lines.Add(Line(depth, slot, join, lead, Leaf(c, ctx)));
    }

    /// <summary>The condition on one line: "a and (b or c)", "not (a and b)".</summary>
    public static MetaSeg[] ConditionCompact(MetaRuleDto rule, MetaTextContext ctx)
    {
        var segs = new List<MetaSeg>();
        CondCompact(rule, segs, nested: false, ctx, 0);
        return segs.ToArray();
    }

    private static void CondCompact(MetaRuleDto c, List<MetaSeg> o, bool nested, MetaTextContext ctx, int level)
    {
        if (level > MaxLevels) { o.Add(new MetaSeg("…", MetaTone.Dim)); return; }
        if (c.Condition is All or Any)
        {
            List<MetaRuleDto> kids = c.Children;
            if (kids.Count == 0)
            {
                o.Add(new MetaSeg(c.Condition == All ? "all of" : "any of", MetaTone.Label));
                o.Add(new MetaSeg(" (nothing)", MetaTone.Warn));
                return;
            }
            if (kids.Count == 1) { CondCompact(kids[0], o, nested, ctx, level + 1); return; }
            string word = c.Condition == All ? " and " : " or ";
            if (nested) o.Add(new MetaSeg("(", MetaTone.Join));
            for (int i = 0; i < kids.Count; i++)
            {
                if (i > 0) o.Add(new MetaSeg(word, MetaTone.Join));
                CondCompact(kids[i], o, nested: true, ctx, level + 1);
            }
            if (nested) o.Add(new MetaSeg(")", MetaTone.Join));
            return;
        }
        if (c.Condition == Not)
        {
            o.Add(new MetaSeg("not ", MetaTone.Join));
            if (c.Children.Count == 0) o.Add(new MetaSeg("(nothing)", MetaTone.Warn));
            else CondCompact(c.Children[0], o, nested: true, ctx, level + 1);
            return;
        }
        o.AddRange(Leaf(c, ctx));
    }

    /// <summary>One plain condition (not All / Any / Not).</summary>
    public static MetaSeg[] Leaf(MetaRuleDto c, MetaTextContext ctx)
    {
        string d = (c.ConditionData ?? string.Empty).Trim();
        switch (c.Condition)
        {
            case 0: return One("Never", MetaTone.Dim);
            case 1: return One("Always", MetaTone.Label);
            case 4: return Typed("Chat matches ", d, MetaTone.Code);
            case 27: return Typed("Chat captures ", d, MetaTone.Code);
            case 26: return Typed("Expr ", d, MetaTone.Code);
            case 5: return Typed("Free pack slots " + Le + " ", d, MetaTone.Value);
            case 6: return Seconds("In state " + Ge + " ", d);
            case 21: return Seconds("In state (P) " + Ge + " ", d);
            case 7: return One("Character death", MetaTone.Label);
            case 8: return One("Vendor open", MetaTone.Label);
            case 9: return One("Vendor closed", MetaTone.Label);
            case 10: return ItemCount(d, Le);
            case 11: return ItemCount(d, Ge);
            case 12: return MobsNamed(d);
            case 13: return PriorityMobs(d);
            case 14: return One("Need to buff", MetaTone.Label);
            case 15:
                return d.Length == 0
                    ? new[] { new MetaSeg("No mobs within ", MetaTone.Label), new MetaSeg("20", MetaTone.Value), new MetaSeg(" (default)", MetaTone.Dim) }
                    : new[] { new MetaSeg("No mobs within ", MetaTone.Label), new MetaSeg(d, MetaTone.Value) };
            case 16: return Typed("Landblock ", d.ToUpperInvariant(), MetaTone.Value);
            case 17: return Typed("Landcell ", d.ToUpperInvariant(), MetaTone.Value);
            case 18: return One("Entered portal space", MetaTone.Label);
            case 19: return One("Left portal space", MetaTone.Label);
            case 22: return SpellLeft(d, Ge, ctx);
            case 23: return SpellLeft(d, Le, ctx);
            case 24: return Unit("Burden " + Ge + " ", d, "%");
            case 25: return Typed("Off route " + Ge + " ", d, MetaTone.Value);
            case 28: return One("Nav route empty", MetaTone.Label);
            case 29: return Typed("Health " + Le + " ", d, MetaTone.Value);
            case 30: return Unit("Health " + Ge + " ", d, "%");
            case 31: return Typed("Mana " + Le + " ", d, MetaTone.Value);
            case 32: return Unit("Mana " + Ge + " ", d, "%");
            case 33: return Typed("Stamina " + Le + " ", d, MetaTone.Value);
            case 34: return Unit("Vitae " + Ge + " ", d, "%");
            case 35: return Unit("No kill for ", d, " min");
            default:
                // A condition this engine doesn't know yet (a newer RynthAi): its number and data.
                string name = "Condition " + c.Condition.ToString(CultureInfo.InvariantCulture);
                return d.Length == 0 ? One(name, MetaTone.Label)
                    : new[] { new MetaSeg(name + " ", MetaTone.Label), new MetaSeg(d, MetaTone.Value) };
        }
    }

    // "Count Prismatic Taper ≤ 50" from "Prismatic Taper,50" (the count after the last comma).
    private static MetaSeg[] ItemCount(string d, string op)
    {
        int comma = d.LastIndexOf(',');
        if (comma < 0) return Typed("Count ", d, MetaTone.Value);
        string name = d.Substring(0, comma).Trim(), count = d.Substring(comma + 1).Trim();
        return new[]
        {
            new MetaSeg("Count ", MetaTone.Label), Val(name), new MetaSeg(" " + op + " ", MetaTone.Label), Val(count),
        };
    }

    // "Mobs Drudge ≥ 3 within 20" from "Drudge,20,3" (name regex, distance, count; the regex may hold commas).
    private static MetaSeg[] MobsNamed(string d)
    {
        int c2 = d.LastIndexOf(',');
        int c1 = c2 > 0 ? d.LastIndexOf(',', c2 - 1) : -1;
        if (c1 < 0) return Typed("Mobs named ", d, MetaTone.Code);
        string name = d.Substring(0, c1).Trim(), dist = d.Substring(c1 + 1, c2 - c1 - 1).Trim(), count = d.Substring(c2 + 1).Trim();
        return new[]
        {
            new MetaSeg("Mobs ", MetaTone.Label), new MetaSeg(name.Length == 0 ? "(any name)" : name, name.Length == 0 ? MetaTone.Warn : MetaTone.Code),
            new MetaSeg(" " + Ge + " ", MetaTone.Label), Val(count), new MetaSeg(" within ", MetaTone.Label), Val(dist),
        };
    }

    // "Priority mobs ≥ 1 within 20" from "1,20" (count, distance).
    private static MetaSeg[] PriorityMobs(string d)
    {
        int comma = d.IndexOf(',');
        if (comma < 0) return Typed("Priority mobs ", d, MetaTone.Value);
        return new[]
        {
            new MetaSeg("Priority mobs " + Ge + " ", MetaTone.Label), Val(d.Substring(0, comma).Trim()),
            new MetaSeg(" within ", MetaTone.Label), Val(d.Substring(comma + 1).Trim()),
        };
    }

    // "Spell Strength Other VI ≥ 300 s left" from "2293,300" (spell id, seconds).
    private static MetaSeg[] SpellLeft(string d, string op, MetaTextContext ctx)
    {
        int comma = d.IndexOf(',');
        string id = (comma < 0 ? d : d.Substring(0, comma)).Trim();
        string secs = comma < 0 ? "" : d.Substring(comma + 1).Trim();
        string shown = id;
        if (uint.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out uint sid))
        {
            string? name = ctx.SpellName?.Invoke(sid);
            if (!string.IsNullOrEmpty(name)) shown = name;
            else ctx.MissedSpellName = true;
        }
        return new[]
        {
            new MetaSeg("Spell ", MetaTone.Label), id.Length == 0 ? new MetaSeg("(not set)", MetaTone.Warn) : Val(shown),
            new MetaSeg(" " + op + " ", MetaTone.Label), Val(secs), new MetaSeg(" s left", MetaTone.Label),
        };
    }

    private static MetaSeg[] Seconds(string label, string d) => Unit(label, d, " s");

    private static MetaSeg[] Unit(string label, string d, string unit) =>
        d.Length == 0
            ? new[] { new MetaSeg(label, MetaTone.Label), new MetaSeg("(not set)", MetaTone.Warn) }
            : new[] { new MetaSeg(label, MetaTone.Label), Val(d), new MetaSeg(unit, MetaTone.Label) };

    // =====================================================================
    //  Actions
    // =====================================================================

    /// <summary>The action column's lines: one per action, a DoAll's in order.</summary>
    public static MetaLine[] ActionLines(MetaRuleDto rule, MetaTextContext ctx)
    {
        var lines = new List<MetaLine>();
        Act(rule, lines, ctx, 0);
        return lines.ToArray();
    }

    private static void Act(MetaRuleDto a, List<MetaLine> lines, MetaTextContext ctx, int level)
    {
        if (a.Action == DoAll && level <= MaxLevels)
        {
            if (a.ActionChildren.Count == 0)
            {
                lines.Add(Line(0, false, "", null, new MetaSeg("do all", MetaTone.Label), new MetaSeg(" (nothing)", MetaTone.Warn)));
                return;
            }
            foreach (MetaRuleDto kid in a.ActionChildren) Act(kid, lines, ctx, level + 1);
            return;
        }
        lines.Add(Line(0, false, "", null, ActionLeaf(a, ctx)));
    }

    /// <summary>The actions on one line, joined by " · ".</summary>
    public static MetaSeg[] ActionCompact(MetaRuleDto rule, MetaTextContext ctx)
    {
        var segs = new List<MetaSeg>();
        foreach (MetaLine line in ActionLines(rule, ctx))
        {
            if (segs.Count > 0) segs.Add(new MetaSeg("  " + Dot + "  ", MetaTone.Dim));
            segs.AddRange(line.Segs);
        }
        return segs.ToArray();
    }

    /// <summary>One action (not DoAll).</summary>
    public static MetaSeg[] ActionLeaf(MetaRuleDto a, MetaTextContext ctx)
    {
        string d = (a.ActionData ?? string.Empty).Trim();
        switch (a.Action)
        {
            case 0: return d.Length == 0 ? One("None", MetaTone.Dim) : Typed("None ", d, MetaTone.Value);
            case 1:
                // A command reads as itself ("/vt opt set ..."); plain text is said in chat.
                if (d.Length == 0) return new[] { new MetaSeg("Chat ", MetaTone.Label), new MetaSeg("(empty)", MetaTone.Warn) };
                return d.StartsWith('/') ? new[] { Val(d) } : new[] { new MetaSeg("Chat ", MetaTone.Label), Val(d) };
            case 2: return StateRef(Arrow + " ", d, ctx);
            case 5: return StateRef("Call " + Arrow + " ", d, ctx);
            case 6: return One("Return from call", MetaTone.Label);
            case 3:
            {
                int semi = d.IndexOf(';');
                string route = semi >= 0 ? d.Substring(0, semi).Trim() : d;
                // "Patrol" with no route of that name: RynthAi's dungeon patrol (/ra dunnav-patrol).
                if (string.Equals(route, "Patrol", StringComparison.OrdinalIgnoreCase) && ctx.RouteExists?.Invoke(route) != true)
                    return new[] { new MetaSeg("Nav ", MetaTone.Label), new MetaSeg(route, MetaTone.Value), new MetaSeg(" (dungeon patrol)", MetaTone.Dim) };
                return Typed("Nav ", route, MetaTone.Value);
            }
            case 7: return Typed("Expr ", d, MetaTone.Code);
            case 8: return Typed("Chat expr ", d, MetaTone.Code);
            case 9: return Watchdog(d, ctx);
            case 10: return One("Clear watchdog", MetaTone.Label);
            case 11:
            {
                // "variable;option" (RynthAi keeps metaf's GetOpt {Option} {Variable} as variable;option).
                int semi = d.IndexOf(';');
                if (semi < 0) return Typed("Get option ", d, MetaTone.Value);
                return new[]
                {
                    new MetaSeg("Get option ", MetaTone.Label), Val(d.Substring(semi + 1).Trim()),
                    new MetaSeg(" " + Arrow + " ", MetaTone.Label), new MetaSeg(d.Substring(0, semi).Trim(), MetaTone.Code),
                };
            }
            case 12:
            {
                int semi = d.IndexOf(';');
                if (semi < 0) return Typed("Set option ", d, MetaTone.Value);
                return new[]
                {
                    new MetaSeg("Set ", MetaTone.Label), Val(d.Substring(0, semi).Trim()),
                    new MetaSeg(" = ", MetaTone.Label), Val(d.Substring(semi + 1).Trim()),
                };
            }
            case 13:
            {
                int semi = d.IndexOf(';');
                return Typed("Create view ", semi >= 0 ? d.Substring(0, semi).Trim() : d, MetaTone.Value);
            }
            case 14: return Typed("Destroy view ", d, MetaTone.Value);
            case 15: return One("Destroy all views", MetaTone.Label);
            default:
                string name = "Action " + a.Action.ToString(CultureInfo.InvariantCulture);
                return d.Length == 0 ? One(name, MetaTone.Label)
                    : new[] { new MetaSeg(name + " ", MetaTone.Label), new MetaSeg(d, MetaTone.Value) };
        }
    }

    private static MetaSeg[] StateRef(string label, string state, MetaTextContext ctx)
    {
        if (state.Length == 0) return new[] { new MetaSeg(label, MetaTone.Label), new MetaSeg("(no state)", MetaTone.Warn) };
        if (!ctx.StateExists(state))
            return new[] { new MetaSeg(label, MetaTone.Label), new MetaSeg(state, MetaTone.Warn), new MetaSeg(" (no such state)", MetaTone.Warn) };
        return new[] { new MetaSeg(label, MetaTone.Label), new MetaSeg(state, MetaTone.State) };
    }

    // "Watchdog 5 m in 30 s → Default" from "Default;5;30" (state; meters; seconds): if you haven't
    // moved that far in that long, the meta goes to the state.
    private static MetaSeg[] Watchdog(string d, MetaTextContext ctx)
    {
        string[] p = d.Split(';');
        if (p.Length < 3) return Typed("Watchdog ", d, MetaTone.Value);
        var segs = new List<MetaSeg>
        {
            new("Watchdog ", MetaTone.Label), Val(p[1].Trim()), new(" m in ", MetaTone.Label), Val(p[2].Trim()), new(" s ", MetaTone.Label),
        };
        segs.AddRange(StateRef(Arrow + " ", p[0].Trim(), ctx));
        return segs.ToArray();
    }

    // =====================================================================
    //  Helpers
    // =====================================================================

    /// <summary>The text of segments, for search and tooltips.</summary>
    public static string Plain(MetaSeg[] segs)
    {
        var sb = new StringBuilder();
        foreach (MetaSeg s in segs) sb.Append(s.Text);
        return sb.ToString();
    }

    private static MetaSeg Val(string text) =>
        text.Length == 0 ? new MetaSeg("(not set)", MetaTone.Warn) : new MetaSeg(text, MetaTone.Value);

    private static MetaSeg[] One(string text, MetaTone tone) => new[] { new MetaSeg(text, tone) };

    // "Label value"; an empty value shows "(not set)".
    private static MetaSeg[] Typed(string label, string value, MetaTone tone) =>
        new[] { new MetaSeg(label, MetaTone.Label), value.Length == 0 ? new MetaSeg("(not set)", MetaTone.Warn) : new MetaSeg(value, tone) };

    private static MetaLine Line(int depth, bool slot, string join, List<MetaSeg>? lead, params MetaSeg[] segs)
    {
        MetaSeg[] all = segs;
        if (lead != null && lead.Count > 0)
        {
            all = new MetaSeg[lead.Count + segs.Length];
            lead.CopyTo(all, 0);
            segs.CopyTo(all, lead.Count);
        }
        return new MetaLine { Depth = depth, Slot = slot, Join = join, Segs = all };
    }

    private static MetaLine Line(int depth, bool slot, string join, List<MetaSeg> lead) =>
        new() { Depth = depth, Slot = slot, Join = join, Segs = lead.ToArray() };
}
