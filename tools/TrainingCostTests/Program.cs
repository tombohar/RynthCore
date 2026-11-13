using RynthCore.Engine.Compatibility;
using RynthCore.Engine.UI.ScriptWindows;
using RynthCore.PluginSdk;

namespace TrainingCostTests;

/// <summary>
/// Spending experience, offline: the portal XpTable as the engine reads it, the XP a raise of
/// n ranks costs (what the engine sends and the phone shows), and the checks a raise from the
/// plugin API must pass before anything is sent.
/// </summary>
internal static class Program
{
    private static int _checks, _failed;

    private static int Main(string[] args)
    {
        string dat = FindDat(args.Length > 0 ? args[0] : @"C:\Turbine\Asheron's Call");
        XpTableData? t = RealTable(dat);
        if (t != null)
        {
            TableShape(t);
            CostsOnTheRealTable(t);
            AffordableMatchesALinearScan(t);
            RaiseChecks(t);
            MaxAfterUntrainAndRetrain(t);
            MaxAcrossTheSpecializedSwitch(t);
            MaxNeverAsksPastTheTop(t);
        }
        TrainChecks();
        ParserRefusesJunk();
        SdkNumbersMatch();
        Console.WriteLine();
        Console.WriteLine(_failed == 0 && _checks > 0 ? $"PASS ({_checks} checks)" : $"FAIL ({_failed} of {_checks} checks failed)");
        return _failed == 0 && _checks > 0 ? 0 : 1;
    }

    private static string FindDat(string arg)
    {
        if (File.Exists(arg)) return arg;
        foreach (string name in new[] { "client_portal.dat", "portal.dat" })
        {
            string p = Path.Combine(arg, name);
            if (File.Exists(p)) return p;
        }
        return Path.Combine(arg, "client_portal.dat");
    }

    private static XpTableData? RealTable(string path)
    {
        Section("the XpTable (0x0E000018) from " + path);
        using AcDatFile? dat = AcDatFile.Open(path, out string why);
        Check(dat != null, "portal dat opens read-only" + (dat == null ? ": " + why : ""));
        if (dat == null) return null;
        byte[]? raw = dat.Read(0x0E000018, 1024 * 1024);
        Check(raw != null && raw.Length > 100, "XpTable present");
        if (raw == null) return null;
        bool ok = TrainingCosts.TryParseXpTable(raw, out XpTableData t);
        Check(ok, "XpTable parses");
        Console.WriteLine($"     ranks: attributes {t.AttributeXp.Length - 1}, vitals {t.VitalXp.Length - 1}, trained {t.TrainedXp.Length - 1}, specialized {t.SpecializedXp.Length - 1}, levels {t.LevelXp.Length - 1}");        return ok ? t : null;
    }

    private static void TableShape(XpTableData t)
    {
        Section("table shape (retail: 190 attribute, 196 vital, 208 trained, 226 specialized ranks, 275 levels)");
        Check(t.AttributeXp.Length == 191 && t.VitalXp.Length == 197 && t.TrainedXp.Length == 209 && t.SpecializedXp.Length == 227,
            "rank counts");
        Check(t.LevelXp.Length == 276, "level count");
        foreach (var (name, a) in Tables(t))
        {
            Check(a[0] == 0, name + ": rank 0 costs nothing");
            bool rising = true;
            for (int i = 1; i < a.Length; i++) if (a[i] <= a[i - 1]) rising = false;
            Check(rising, name + ": strictly rising (every rank costs something)");
        }
        Check(t.AttributeXp[^1] == 4_019_438_644u, $"attribute top = 4,019,438,644 XP (retail max attribute XP) - got {t.AttributeXp[^1]:N0}");
        Check(t.SpecializedXp[^1] == 4_100_490_438u, $"specialized top = 4,100,490,438 XP - got {t.SpecializedXp[^1]:N0}");
    }

    private static void CostsOnTheRealTable(XpTableData t)
    {
        Section("costs: table[ranks + n] - spent");
        uint[] a = t.AttributeXp;
        Check(TrainingCosts.CostOf(a, 0, 0, 1) == a[1], "+1 from rank 0 = table[1]");
        Check(TrainingCosts.CostOf(a, 50, a[50], 1) == a[51] - a[50], "+1 from rank 50, nothing extra spent");
        Check(TrainingCosts.CostOf(a, 50, a[50] + 1000, 1) == a[51] - a[50] - 1000, "XP already put toward the next rank comes off");
        Check(TrainingCosts.CostOf(a, 50, a[50], 10) == a[60] - a[50], "+10 = table[ranks + 10] - spent");
        // +10 equals ten +1s in a row (the server raises as far as the XP covers).
        foreach (var (name, tab) in Tables(t))
        {
            bool same = true;
            for (uint r = 0; r + 10 < tab.Length; r += 7)
            {
                uint spent = tab[r] + (r % 3 == 0 ? 0u : Math.Min(5u, tab[r + 1] - tab[r] - 1));
                long ten = TrainingCosts.CostOf(tab, r, spent, 10);
                long chained = 0; uint rr = r, sp = spent;
                for (int k = 0; k < 10; k++) { long c = TrainingCosts.CostOf(tab, rr, sp, 1); chained += c; sp = (uint)(sp + c); rr++; }
                if (ten != chained) same = false;
            }
            Check(same, name + ": +10 = ten +1s");
        }
        int top = a.Length - 1;
        Check(TrainingCosts.CostOf(a, (uint)top, a[top], 1) == -1 && TrainingCosts.IsMax(a, (uint)top), "at the top: -1, IsMax");
        Check(TrainingCosts.CostOf(a, (uint)top - 5, a[top - 5], 10) == -1 && TrainingCosts.CostOf(a, (uint)top - 5, a[top - 5], 5) > 0,
            "+10 with five ranks left: -1 (+5 still prices)");
        Check(TrainingCosts.RanksLeft(a, (uint)top - 5) == 5 && TrainingCosts.RanksLeft(a, (uint)top) == 0, "ranks left");
        Check(TrainingCosts.CostOf(a, 0, 0, 0) == 0, "+0 costs 0");
        // A few numbers for the report / the phone's first test.
        Console.WriteLine($"     attribute rank 10 -> 11: {TrainingCosts.CostOf(a, 10, a[10], 1):N0} XP; +10: {TrainingCosts.CostOf(a, 10, a[10], 10):N0} XP");
        Console.WriteLine($"     vital rank 10 -> 11: {TrainingCosts.CostOf(t.VitalXp, 10, t.VitalXp[10], 1):N0} XP");
        Console.WriteLine($"     trained rank 10 -> 11: {TrainingCosts.CostOf(t.TrainedXp, 10, t.TrainedXp[10], 1):N0}; specialized: {TrainingCosts.CostOf(t.SpecializedXp, 10, t.SpecializedXp[10], 1):N0}");
        Console.WriteLine($"     attribute rank 180 -> 181: {TrainingCosts.CostOf(a, 180, a[180], 1):N0} XP");
    }

    private static void AffordableMatchesALinearScan(XpTableData t)
    {
        Section("affordable ranks (binary search) = a linear scan");
        var rnd = new Random(79);
        bool all = true;
        foreach (var (_, tab) in Tables(t))
            for (int i = 0; i < 400; i++)
            {
                uint r = (uint)rnd.Next(0, tab.Length);
                uint spent = tab[r] + (r + 1 < tab.Length ? (uint)rnd.Next(0, (int)Math.Min(int.MaxValue, tab[r + 1] - tab[r])) : 0u);
                long have = rnd.Next(4) switch { 0 => 0, 1 => rnd.Next(), 2 => (long)rnd.Next() * 50, _ => long.MaxValue / 4 };
                int lin = 0;
                for (int n = 1; ; n++)
                {
                    long c = TrainingCosts.CostOf(tab, r, spent, n);
                    if (c < 0 || c > have) break;
                    lin = n;
                }
                if (TrainingCosts.Affordable(tab, r, spent, have) != lin) all = false;
            }
        Check(all, "1,600 random ranks / XP amounts on the four tables");
        Check(TrainingCosts.Affordable(t.AttributeXp, 0, 0, long.MaxValue) == t.AttributeXp.Length - 1, "unlimited XP buys every rank");
    }

    private static void RaiseChecks(XpTableData t)
    {
        Section("the checks before a raise is sent (CheckRaise)");
        uint[] a = t.AttributeXp;
        long c1 = TrainingCosts.CostOf(a, 100, a[100], 1);
        Check(TrainingCosts.CheckRaise(a, 100, a[100], c1, 1, c1, out long sent) == RaiseStatus.Sent && sent == c1, "exactly enough XP, confirmed cost -> Sent with that XP");
        Check(TrainingCosts.CheckRaise(a, 100, a[100], c1 - 1, 1, c1, out _) == RaiseStatus.NotEnough, "one XP short -> NotEnough");
        Check(TrainingCosts.CheckRaise(a, 100, a[100], long.MaxValue, 1, c1 + 1, out _) == RaiseStatus.CostChanged, "a different confirmed cost -> CostChanged (nothing sent)");
        Check(TrainingCosts.CheckRaise(a, 101, a[101], long.MaxValue, 1, c1, out _) == RaiseStatus.CostChanged, "the stat moved on since the user looked -> CostChanged");
        Check(TrainingCosts.CheckRaise(a, 100, a[100], long.MaxValue, 1, 0, out sent) == RaiseStatus.Sent && sent == c1, "0 = don't compare");
        long c10 = TrainingCosts.CostOf(a, 100, a[100], 10);
        Check(TrainingCosts.CheckRaise(a, 100, a[100], long.MaxValue, 10, c10, out sent) == RaiseStatus.Sent && sent == c10, "+10");
        int top = a.Length - 1;
        Check(TrainingCosts.CheckRaise(a, (uint)top, a[top], long.MaxValue, 1, 0, out _) == RaiseStatus.NotRaisable, "top rank -> NotRaisable");
        Check(TrainingCosts.CheckRaise(a, (uint)top - 3, a[top - 3], long.MaxValue, 10, 0, out _) == RaiseStatus.NotRaisable, "+10 past the top -> NotRaisable");
        Check(TrainingCosts.CheckRaise(null, 0, 0, long.MaxValue, 1, 0, out _) == RaiseStatus.NotRaisable, "untrained skill (no table) -> NotRaisable");
        Check(TrainingCosts.TableFor(t, 3, 1) == null && TrainingCosts.TableFor(t, 3, 0) == null, "untrained / unknown skill: no table");
        Check(TrainingCosts.TableFor(t, 3, 2) == t.TrainedXp && TrainingCosts.TableFor(t, 3, 3) == t.SpecializedXp, "trained / specialized tables");
        Check(TrainingCosts.TableFor(t, 1, 0) == t.AttributeXp && TrainingCosts.TableFor(t, 2, 0) == t.VitalXp && TrainingCosts.TableFor(t, 9, 0) == null, "attribute / vital / unknown kind");
        Check(TrainingCosts.CheckRaise(a, 100, a[100], long.MaxValue, 0, 0, out _) == RaiseStatus.BadArguments
              && TrainingCosts.CheckRaise(a, 100, a[100], long.MaxValue, 101, 0, out _) == RaiseStatus.BadArguments
              && TrainingCosts.CheckRaise(a, 100, a[100], long.MaxValue, 1, -5, out _) == RaiseStatus.BadArguments, "0 ranks, over 100 ranks, a negative cost -> BadArguments");
        Check(TrainingCosts.CheckRaise(a, 0, 0, long.MaxValue, 100, 0, out long hundred) == RaiseStatus.Sent && hundred == a[100], "+100 from rank 0 = table[100], within one 32-bit game action");
        // A fake table whose step exceeds 32 bits can't be sent in one game action.
        uint[] huge = { 0, uint.MaxValue };
        Check(TrainingCosts.CheckRaise(huge, 0, 0, long.MaxValue, 1, 0, out long hc) == RaiseStatus.Sent && hc == uint.MaxValue, "uint.MaxValue XP fits");
        Check(TrainingCosts.CheckRaise(a, 50, a[51], long.MaxValue, 1, 0, out _) == RaiseStatus.NotRaisable, "spent already covers the next rank (cost 0) -> NotRaisable, nothing sent");
    }

    // ── Max after untrain / train again, and across trained <-> specialized ──
    //
    // A model of ACE's own rules (Player_Skills.cs): UntrainSkill / UnspecializeSkill / ResetSkill
    // refund ExperienceSpent and zero ranks and XP; TrainSkill (credits) starts at rank 0 with nothing
    // spent; SpecializeSkill at the temple keeps the XP and re-ranks it on the specialized table
    // (CalcSkillRank); HandleActionRaiseSkill / SpendSkillXp refuse, without a word, an amount over the
    // unassigned XP or over ExperienceLeft (table[top] - spent, never below 0) and otherwise spend it all.

    private sealed class AceSkill
    {
        public uint[]? Table;   // null = untrained
        public uint Ranks, Spent;
    }

    private static uint AceRank(uint[] table, uint spent)
    {
        for (int i = table.Length - 1; i >= 0; i--) if (spent >= table[i]) return (uint)i;
        return 0;
    }

    private static bool AceRaise(AceSkill k, ref long unassigned, long amount)
    {
        if (k.Table == null || amount <= 0 || amount > uint.MaxValue || amount > unassigned) return false;
        if (k.Ranks >= k.Table.Length - 1) return false;                               // IsMaxRank
        long left = Math.Max(0L, (long)k.Table[^1] - k.Spent);                         // ExperienceLeft
        if (amount > left) return false;
        unassigned -= amount;
        k.Spent += (uint)amount;
        k.Ranks = AceRank(k.Table, k.Spent);
        return true;
    }

    private static void AceUntrain(AceSkill k, ref long unassigned)
    {
        unassigned += k.Spent;
        k.Table = null; k.Ranks = 0; k.Spent = 0;
    }

    /// <summary>The panel's Max on the numbers the client shows, sent to the model server. True when it was taken.</summary>
    private static bool PanelMax(AceSkill k, ref long unassigned, out MaxRaisePlan plan)
    {
        plan = TrainingCosts.PlanMax(k.Table!, k.Ranks, k.Spent, unassigned);
        return plan.Ranks > 0 && AceRaise(k, ref unassigned, plan.Cost);
    }

    private static void MaxAfterUntrainAndRetrain(XpTableData t)
    {
        Section("Max after untrain -> train again (ACE: XP refunded, back at rank 0 with nothing spent)");
        uint[] tr = t.TrainedXp;
        int top = tr.Length - 1;

        // A maxed trained skill, untrained and trained again: the refund buys every rank back in one Max.
        var k = new AceSkill { Table = tr, Ranks = (uint)top, Spent = tr[top] };
        long xp = 50_000_000;
        Check(TrainingCosts.IsMax(tr, k.Ranks) && TrainingCosts.PlanMax(tr, k.Ranks, k.Spent, xp) is { Ranks: 0, RanksLeft: 0, Missing: 0 },
            "before: at the top, Max has nothing to buy");
        AceUntrain(k, ref xp);
        k.Table = tr;                                    // trained again with credits
        Check(k.Ranks == 0 && k.Spent == 0 && xp == 50_000_000 + tr[top], "untrain refunded the XP; trained again at rank 0, nothing spent");
        bool sent = PanelMax(k, ref xp, out MaxRaisePlan p);
        Check(p.ReachesTop && p.Ranks == top && p.Cost == tr[top] && p.Missing == 0,
            $"Max from rank 0 offers all {top} ranks for {tr[top]:N0} XP (got +{p.Ranks}, {p.Cost:N0})");
        Check(p.Cost <= uint.MaxValue, "rank 0 to the top fits one 32-bit raise");
        Check(sent && k.Ranks == top && TrainingCosts.IsMax(tr, k.Ranks), "the server takes it: the skill is at the top again");
        MaxRaisePlan after = TrainingCosts.PlanMax(tr, k.Ranks, k.Spent, xp);
        Check(after.Ranks == 0 && after.RanksLeft == 0 && TrainingCosts.CostOf(tr, k.Ranks, k.Spent, 1) == -1,
            "at the true top it still shows max (nothing left, +1 prices -1)");

        // Partly raised (rank 150 and some way to 151), untrained, trained again: the refund alone buys rank 150 back and more.
        uint part = tr[150] + (tr[151] - tr[150]) / 3;
        k = new AceSkill { Table = tr, Ranks = 150, Spent = part };
        xp = 0;
        AceUntrain(k, ref xp);
        k.Table = tr;
        sent = PanelMax(k, ref xp, out p);
        Check(sent && p.Ranks == 150 && k.Ranks == 150 && k.Spent == tr[150] && xp == part - tr[150],
            $"refund only: Max buys the 150 ranks back exactly (+{p.Ranks}); the rest stays unassigned ({xp:N0})");
        Check(!p.ReachesTop && p.Missing == tr[top] - (long)part && p.RanksLeft == top,
            $"and says it stops short: {p.Missing:N0} more XP for the top");

        // Specialized and maxed, untrained (temple: specialized -> trained -> untrained), trained again with
        // credits: the trained top costs more than the specialized one, so the refund alone falls short.
        uint[] sp = t.SpecializedXp;
        k = new AceSkill { Table = sp, Ranks = (uint)(sp.Length - 1), Spent = sp[^1] };
        xp = 0;
        AceUntrain(k, ref xp);
        k.Table = tr;
        p = TrainingCosts.PlanMax(tr, 0, 0, xp);
        Check(!p.ReachesTop && p.Ranks < top && p.Missing == (long)tr[top] - sp[^1],
            $"specialized max refunded ({sp[^1]:N0}) and trained again: Max (+{p.Ranks} of {top}), {p.Missing:N0} XP short of the trained top");
        sent = PanelMax(k, ref xp, out _);
        Check(sent && k.Ranks == p.Ranks, "that partial Max is taken as offered");
        xp += p.Missing;
        sent = PanelMax(k, ref xp, out p);
        Check(sent && p.ReachesTop && TrainingCosts.IsMax(tr, k.Ranks), "with the missing XP the next Max reaches the top");
    }

    private static void MaxAcrossTheSpecializedSwitch(XpTableData t)
    {
        Section("Max across trained <-> specialized (the table follows the skill's class)");
        uint[] tr = t.TrainedXp, sp = t.SpecializedXp;
        Check(TrainingCosts.TableFor(t, 3, 2) == tr && TrainingCosts.TableFor(t, 3, 3) == sp, "class 2 -> trained table, class 3 -> specialized");

        // Temple specialize: the XP stays, the ranks are counted again on the specialized table.
        uint spent = tr[150] + 12_345;
        var k = new AceSkill { Table = sp, Ranks = AceRank(sp, spent), Spent = spent };
        Check(k.Ranks > 150 && sp[k.Ranks] <= spent && (k.Ranks + 1 >= sp.Length || sp[k.Ranks + 1] > spent),
            $"trained rank 150 specialized: rank {k.Ranks} on the specialized table, XP kept");
        long xp = long.MaxValue / 4;
        MaxRaisePlan p = TrainingCosts.PlanMax(sp, k.Ranks, k.Spent, xp);
        Check(p.ReachesTop && p.Cost == sp[^1] - (long)spent, $"Max prices the rest on the specialized table ({p.Cost:N0} XP)");
        Check(PanelMax(k, ref xp, out _) && TrainingCosts.IsMax(sp, k.Ranks), "the server takes it: specialized top");

        // A trained skill raised past the specialized top, then specialized: already at the top (ACE's ExperienceLeft is 0).
        spent = tr[^1];
        k = new AceSkill { Table = sp, Ranks = AceRank(sp, spent), Spent = spent };
        p = TrainingCosts.PlanMax(sp, k.Ranks, k.Spent, xp);
        Check(TrainingCosts.IsMax(sp, k.Ranks) && p.Ranks == 0 && p.RanksLeft == 0, "trained XP over the specialized top: shows max, Max buys nothing");

        // Unspecialize (refund, trained at rank 0): priced on the trained table from rank 0.
        k = new AceSkill { Table = sp, Ranks = (uint)(sp.Length - 1), Spent = sp[^1] };
        xp = tr[^1];
        AceUntrain(k, ref xp);
        k.Table = tr;
        Check(PanelMax(k, ref xp, out p) && p.ReachesTop && p.Ranks == tr.Length - 1 && TrainingCosts.IsMax(tr, k.Ranks),
            "unspecialized (XP refunded, trained at rank 0): Max from the trained table reaches the top");
    }

    private static void MaxNeverAsksPastTheTop(XpTableData t)
    {
        Section("Max never asks the server for more than it allows");
        var rnd = new Random(1112);
        bool allTaken = true, neverOver = true;
        foreach (var (_, tab) in Tables(t))
            for (int i = 0; i < 500; i++)
            {
                uint r = (uint)rnd.Next(0, tab.Length - 1);
                uint spent = tab[r] + (uint)rnd.Next(0, (int)Math.Min(int.MaxValue, tab[r + 1] - tab[r]));
                long xp = rnd.Next(3) switch { 0 => rnd.Next(), 1 => (long)rnd.Next() * 3, _ => long.MaxValue / 4 };
                MaxRaisePlan p = TrainingCosts.PlanMax(tab, r, spent, xp);
                if (p.Cost > Math.Max(0L, (long)tab[^1] - spent) || p.Cost > xp) neverOver = false;
                var k = new AceSkill { Table = tab, Ranks = r, Spent = spent };
                if (p.Ranks > 0 && !AceRaise(k, ref xp, p.Cost)) allTaken = false;
            }
        Check(neverOver, "2,000 states: Max's XP is within the unassigned XP and the XP left to the top");
        Check(allTaken, "and the server model takes every one of them");
    }

    private static void TrainChecks()
    {
        Section("the checks before a train is sent (CheckTrain)");
        Check(TrainingCosts.CheckTrain(1, 6, 6, 6) == RaiseStatus.Sent, "untrained, priced 6, 6 credits, confirmed 6 -> Sent");
        Check(TrainingCosts.CheckTrain(0, 6, 10, 0) == RaiseStatus.Sent, "not in the skill table yet (class 0) counts as untrained");
        Check(TrainingCosts.CheckTrain(1, 6, 5, 6) == RaiseStatus.NotEnough, "5 credits -> NotEnough");
        Check(TrainingCosts.CheckTrain(1, 6, 10, 4) == RaiseStatus.CostChanged, "confirmed a different price -> CostChanged");
        Check(TrainingCosts.CheckTrain(2, 6, 10, 6) == RaiseStatus.NotRaisable && TrainingCosts.CheckTrain(3, 6, 10, 6) == RaiseStatus.NotRaisable, "already trained / specialized -> NotRaisable");
        Check(TrainingCosts.CheckTrain(1, 0, 10, 0) == RaiseStatus.NotRaisable, "no price in the dat -> NotRaisable");
        Check(TrainingCosts.CheckTrain(1, 6, 10, -1) == RaiseStatus.BadArguments, "negative confirm -> BadArguments");
    }

    private static void ParserRefusesJunk()
    {
        Section("the XpTable parser refuses junk");
        Check(!TrainingCosts.TryParseXpTable(Array.Empty<byte>(), out _), "empty");
        Check(!TrainingCosts.TryParseXpTable(new byte[23], out _), "short header");
        var b = new byte[24];
        BitConverter.GetBytes(0x0E000018u).CopyTo(b, 0);
        BitConverter.GetBytes(50000u).CopyTo(b, 4);
        Check(!TrainingCosts.TryParseXpTable(b, out _), "absurd counts");
        var c = new byte[24 + 4 * 2 * 4 + 8 * 2 + 3];   // counts of 1: two of each u32 table, two u64 levels; then cut short
        BitConverter.GetBytes(1u).CopyTo(c, 4); BitConverter.GetBytes(1u).CopyTo(c, 8); BitConverter.GetBytes(1u).CopyTo(c, 12);
        BitConverter.GetBytes(1u).CopyTo(c, 16); BitConverter.GetBytes(1u).CopyTo(c, 20);
        Check(TrainingCosts.TryParseXpTable(c, out XpTableData small) && small.AttributeXp.Length == 2 && small.LevelXp.Length == 2, "the smallest well-formed table");
        Check(!TrainingCosts.TryParseXpTable(c.AsSpan(0, c.Length - 4), out _), "the same, a few bytes short");
    }

    private static void SdkNumbersMatch()
    {
        Section("the SDK's RaiseResult = the engine's RaiseStatus");
        foreach (RaiseStatus s in Enum.GetValues<RaiseStatus>())
            Check(Enum.TryParse(s.ToString(), out RaiseResult r) && (int)r == (int)s, $"{s} = {(int)s}");
        Check((int)RaiseResult.EngineTooOld == -100 && !Enum.IsDefined(typeof(RaiseStatus), -100), "EngineTooOld is the SDK's own");
        Check((uint)TrainingKind.Attribute == 1 && (uint)TrainingKind.Vital == 2 && (uint)TrainingKind.Skill == 3, "kinds 1 / 2 / 3");
    }

    private static IEnumerable<(string, uint[])> Tables(XpTableData t) =>
        new[] { ("attribute", t.AttributeXp), ("vital", t.VitalXp), ("trained", t.TrainedXp), ("specialized", t.SpecializedXp) };

    private static void Section(string title) => Console.WriteLine("\n== " + title);

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (!ok) _failed++;
        Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what);
    }
}
