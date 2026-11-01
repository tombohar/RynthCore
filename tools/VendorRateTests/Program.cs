using RynthCore.Engine.Compatibility;

namespace VendorRateTests;

/// <summary>VendorTrade's profile rate check (VendorRatePolicy). See the csproj.</summary>
internal static class Program
{
    private static int _checks, _failed;

    private static int Main()
    {
        // A free storage vendor: BuyPrice 0, SellPrice 0. This is the case that used to turn trading off.
        Check(VendorRatePolicy.LooksSane(0f), "rate 0 (free storage vendor)");
        Check(VendorRatePolicy.LooksSane(-0f), "rate -0 (compares equal to 0)");

        // Ordinary vendors and promissory-note rates.
        foreach (float r in new[] { 0.001f, 0.1f, 0.5f, 0.75f, 1f, 1.15f, 2.5f, 10f, 999.9f })
            Check(VendorRatePolicy.LooksSane(r), $"rate {r}");

        // Misreads stay rejected.
        foreach (float r in new[] { -0.001f, -1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity,
                                    VendorRatePolicy.MaxRate, 1e6f, float.MaxValue, float.MinValue })
            Check(!VendorRatePolicy.LooksSane(r), $"rate {r} rejected");

        Pricing();
        WiringPinned();

        Console.WriteLine(_failed == 0 ? $"PASS ({_checks} checks)" : $"FAIL ({_failed} of {_checks} checks failed)");
        return _failed == 0 ? 0 : 1;
    }

    private static void Eq(long got, long want, string what) => Check(got == want, $"{what}: got {got}, want {want}");

    private const uint Note = VendorRatePolicy.ItemTypePromissoryNote;
    private const uint Misc = 0x00000080;

    // A 0 rate is free (the vault's GetSellCost/GetBuyCost return 0); any rate above 0 keeps ACE's 1 floor.
    private static void Pricing()
    {
        Check(VendorRatePolicy.IsFreeVendor(0f, 0f), "buy=0 sell=0 is a free vendor");
        Check(!VendorRatePolicy.IsFreeVendor(0f, 1.15f), "buy=0 sell=1.15 is not a free vendor");
        Check(!VendorRatePolicy.IsFreeVendor(0.5f, 0f), "buy=0.5 sell=0 is not a free vendor");

        Eq(VendorRatePolicy.PlayerPaysForStack(0f, Misc, 0), 0, "rate 0, value 0: cost");
        Eq(VendorRatePolicy.PlayerPaysForStack(0f, Misc, 1), 0, "rate 0, value 1: cost");
        Eq(VendorRatePolicy.PlayerPaysForStack(0f, Misc, 250_000), 0, "rate 0, value 250000: cost");
        Eq(VendorRatePolicy.PlayerPaysForStack(0f, Note, 250_000), 0, "rate 0, a note: cost");
        Eq(VendorRatePolicy.VendorPaysForStack(0f, Misc, 1), 0, "rate 0, value 1: payout");
        Eq(VendorRatePolicy.VendorPaysForStack(0f, Misc, 250_000), 0, "rate 0, value 250000: payout");
        Eq(VendorRatePolicy.VendorPaysForStack(0f, Note, 250_000), 0, "rate 0, a note: payout");

        // The floor stays for every rate above 0 (stock ACE).
        Eq(VendorRatePolicy.PlayerPaysForStack(0.001f, Misc, 1), 1, "rate 0.001, value 1: cost floors at 1");
        Eq(VendorRatePolicy.VendorPaysForStack(0.001f, Misc, 1), 1, "rate 0.001, value 1: payout floors at 1");
        Eq(VendorRatePolicy.PlayerPaysForStack(0.5f, Misc, 0), 1, "rate 0.5, value 0: cost floors at 1");
        Eq(VendorRatePolicy.PlayerPaysForStack(1.15f, Misc, 100), 115, "rate 1.15, value 100: cost");
        Eq(VendorRatePolicy.VendorPaysForStack(0.75f, Misc, 100), 75, "rate 0.75, value 100: payout");
        Eq(VendorRatePolicy.PlayerPaysForStack(2f, Note, 100), 115, "a note costs 1.15x whatever the rate");
        Eq(VendorRatePolicy.VendorPaysForStack(0.5f, Note, 100), 100, "a note pays 1.0x whatever the rate");
    }

    // VendorTrade.cs must route through VendorRatePolicy, or the checks above test code the engine doesn't run.
    private static void WiringPinned()
    {
        string? src = FindUp(Path.Combine("src", "RynthCore.Engine", "Compatibility", "VendorTrade.cs"));
        Check(src != null, "found src\\RynthCore.Engine\\Compatibility\\VendorTrade.cs walking up from " + AppContext.BaseDirectory);
        if (src == null) return;
        string text = File.ReadAllText(src);
        foreach (string needle in new[]
                 {
                     "snap.ProfileRead = RateLooksSane(snap.BuyRate) && RateLooksSane(snap.SellRate);",
                     "RateLooksSane(float rate) => VendorRatePolicy.LooksSane(rate);",
                     "VendorRatePolicy.PlayerPaysForStack(sellRate, itemType, stackValue);",
                     "VendorRatePolicy.VendorPaysForStack(buyRate, itemType, value);",
                     "VendorRatePolicy.IsFreeVendor(snap.BuyRate, snap.SellRate)",
                 })
            Check(text.Contains(needle, StringComparison.Ordinal), "VendorTrade.cs contains: " + needle);
    }

    private static string? FindUp(string relative)
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            string p = Path.Combine(d.FullName, relative);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (ok) { Console.WriteLine("ok   " + what); return; }
        _failed++;
        Console.WriteLine("FAIL " + what);
    }
}
