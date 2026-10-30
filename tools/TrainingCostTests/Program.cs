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
        Console.WriteLine($"     ranks: attributes {t.AttributeXp.Length - 1}, vitals {t.VitalXp.Length - 1}, trained {t.TrainedXp.Length - 1}, specialized {t.SpecializedXp.Length - 1}, levels {t.LevelXp.Length - 1}");
        return ok ? t : null;
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
