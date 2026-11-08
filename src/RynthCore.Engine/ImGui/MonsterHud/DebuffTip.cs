// ============================================================================
//  RynthCore.Engine - ImGui/MonsterHud/DebuffTip.cs
//  The words of the debuff icons' hover tooltip (2026-11-07), on the monster
//  plates and your own plate: what the kind is, what it does, the spells
//  behind the icon and when the first of them ends. DebuffIconRow finds the
//  icon under the cursor and shows the text.
//  Plain text, no engine dependencies (the offline tests compile it in).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RynthCore.Engine.ImGuiBackend.Hud;

internal static class DebuffTip
{
    /// <summary>The kind's name, the tooltip's first line.</summary>
    internal static string Title(DebuffKind kind) => kind switch
    {
        DebuffKind.VulnFire => "Fire vulnerability",
        DebuffKind.VulnCold => "Cold vulnerability",
        DebuffKind.VulnLightning => "Lightning vulnerability",
        DebuffKind.VulnAcid => "Acid vulnerability",
        DebuffKind.VulnSlash => "Slashing vulnerability",
        DebuffKind.VulnPierce => "Piercing vulnerability",
        DebuffKind.VulnBludgeon => "Bludgeoning vulnerability",
        DebuffKind.Imperil => "Imperil",
        DebuffKind.MagicYield => "Magic Yield",
        DebuffKind.Defense => "Defense lowered",
        DebuffKind.Attributes => "Attributes lowered",
        DebuffKind.Vitals => "Vitals lowered",
        DebuffKind.Movement => "Slowed",
        DebuffKind.Skills => "Skills lowered",
        DebuffKind.Dot => "Damage over time",
        _ => "Debuff",
    };

    /// <summary>What it does, on a monster (<paramref name="self"/> false) or on you.</summary>
    internal static string Effect(DebuffKind kind, bool self)
    {
        string who = self ? "You take" : "Takes";
        return kind switch
        {
            DebuffKind.VulnFire => $"{who} more fire damage.",
            DebuffKind.VulnCold => $"{who} more cold damage.",
            DebuffKind.VulnLightning => $"{who} more lightning damage.",
            DebuffKind.VulnAcid => $"{who} more acid damage.",
            DebuffKind.VulnSlash => $"{who} more slashing damage.",
            DebuffKind.VulnPierce => $"{who} more piercing damage.",
            DebuffKind.VulnBludgeon => $"{who} more bludgeoning damage.",
            DebuffKind.Imperil => self ? "Your armor is lowered: physical hits land harder." : "Armor lowered: physical hits land harder.",
            DebuffKind.MagicYield => self ? "Your magic defense is lowered: spells land on you more often." : "Magic defense lowered: spells land more often.",
            DebuffKind.Defense => self ? "Your melee or missile defense is lowered: attacks hit you more often." : "Melee or missile defense lowered: attacks hit more often.",
            DebuffKind.Attributes => "Strength, Endurance, Quickness, Coordination, Focus or Self lowered.",
            DebuffKind.Vitals => "Maximum health, stamina or mana, or how fast they come back, lowered.",
            DebuffKind.Movement => "Run speed lowered.",
            DebuffKind.Skills => self ? "One or more of your skills lowered." : "One or more skills lowered.",
            DebuffKind.Dot => $"{who} damage every few seconds.",
            _ => self ? "A harmful spell on you." : "A harmful spell of yours on it.",
        };
    }

    /// <summary>
    /// An icon's tooltip. <paramref name="spells"/> are the names known (the icon holds up to
    /// DebuffIcon.MaxSpells; <paramref name="stack"/> counts every category it merges).
    /// <paramref name="estimated"/>: the end is worked out from the spell's length (the monster
    /// plates), not read from the server (your plate).
    /// </summary>
    internal static string Text(DebuffKind kind, IReadOnlyList<string> spells, int stack, double remainingSec,
        bool estimated, bool self)
    {
        var sb = new StringBuilder(160);
        sb.Append(Title(kind)).Append('\n').Append(Effect(kind, self));
        foreach (string s in spells)
            if (s.Length > 0) sb.Append("\n  ").Append(s);
        int more = Math.Max(stack, spells.Count) - spells.Count;
        if (more > 0 && spells.Count > 0)
            sb.Append("\n  and ").Append(more.ToString(CultureInfo.InvariantCulture)).Append(" more");
        if (remainingSec > 0)
        {
            sb.Append('\n').Append(stack > 1 ? "First one ends in " : "Ends in ");
            if (estimated) sb.Append("about ");
            sb.Append(Time(remainingSec));
        }
        return sb.ToString();
    }

    /// <summary>The "debuffed by others" marker's tooltip (monster plates only).</summary>
    internal const string Others =
        "Debuffed by someone else\nIts last appraisal showed attributes or vitals lowered that your own spells don't explain.";

    /// <summary>The "+N" tooltip.</summary>
    internal static string More(int n) =>
        n == 1 ? "1 more debuff than fits in the row." : $"{n.ToString(CultureInfo.InvariantCulture)} more debuffs than fit in the row.";

    /// <summary>"42 s", "1:05", "1:02:03".</summary>
    internal static string Time(double sec)
    {
        int s = (int)Math.Ceiling(Math.Max(0, sec));
        if (s < 60) return s.ToString(CultureInfo.InvariantCulture) + " s";
        if (s < 3600) return $"{s / 60}:{s % 60:00}";
        return $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}";
    }
}
