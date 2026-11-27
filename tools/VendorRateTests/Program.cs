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
        BuyBox();
        BuyBoxWiringPinned();

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

    // The Inventory panel's "Buy how many?" box (VendorBuyMath, 2026-11-26).
    private static void BuyBox()
    {
        static long Linear12(int n) => 12L * n;
        const int Max = VendorBuyMath.MaxAmount;

        // Stock and money.
        Eq(VendorBuyMath.MaxBuy(-1, -1, Linear12), Max, "unlimited stock, money unknown: the engine's limit");
        Eq(VendorBuyMath.MaxBuy(0, 1000, Linear12), 0, "the vendor has none");
        Eq(VendorBuyMath.MaxBuy(50, -1, Linear12), 50, "50 in stock, money unknown: 50");
        Eq(VendorBuyMath.MaxBuy(-1, 1000, Linear12), 83, "12 each, 1,000 to spend: 83");
        Eq(VendorBuyMath.MaxBuy(-1, 11, Linear12), 0, "12 each, 11 to spend: none");
        Eq(VendorBuyMath.MaxBuy(-1, 12, Linear12), 1, "12 each, 12 to spend: one");
        Eq(VendorBuyMath.MaxBuy(100, 1200, Linear12), 100, "exactly enough for the whole stock");
        Eq(VendorBuyMath.MaxBuy(100, 1_000_000, Linear12), 100, "rich: the stock");
        Eq(VendorBuyMath.MaxBuy(Max + 5, -1, Linear12), Max, "a stock past the limit is capped");
        Eq(VendorBuyMath.MaxBuy(-1, 0, _ => 0), Max, "a free vendor (cost 0): the engine's limit");
        Eq(VendorBuyMath.MaxBuy(-1, 100, _ => -1), 0, "a cost that overflowed to negative counts as too much");

        // ACE's per-stack pricing (each created stack rounds up, floor 1): searched, not divided.
        foreach (float rate in new[] { 1.15f, 0.9f, 2.5f })
            foreach (int maxStack in new[] { 1, 7, 100 })
            {
                long StackCost(int n)
                {
                    long full = n / maxStack, rem = n % maxStack;
                    return full * VendorRatePolicy.PlayerPaysForStack(rate, Misc, 3L * maxStack)
                           + (rem > 0 ? VendorRatePolicy.PlayerPaysForStack(rate, Misc, 3L * rem) : 0);
                }
                foreach (long money in new long[] { 0, 1, 3, 4, 100, 345, 1000, 9999 })
                {
                    int got = VendorBuyMath.MaxBuy(5000, money, StackCost);
                    bool fits = got == 0 || StackCost(got) <= money;
                    bool most = got == 5000 || StackCost(got + 1) > money;
                    Check(fits && most, $"rate {rate}, stacks of {maxStack}, {money} to spend: {got} is the most that fits");
                }
            }

        // Where the number starts and how a typed one is kept.
        Eq(VendorBuyMath.Prefill(50, 200), 50, "the toolbar's 50, 200 buyable: 50");
        Eq(VendorBuyMath.Prefill(500, 200), 200, "the toolbar's 500, 200 buyable: 200");
        Eq(VendorBuyMath.Prefill(1, 200), 1, "the toolbar's 1: 1");
        Eq(VendorBuyMath.Prefill(0, 200), 1, "no toolbar number: 1");
        Eq(VendorBuyMath.Prefill(-7, 200), 1, "a junk toolbar number: 1");
        Eq(VendorBuyMath.Prefill(50, 0), 1, "nothing buyable: 1 (Buy is off)");
        Eq(VendorBuyMath.Clamp(0, 10), 1, "typed 0: 1");
        Eq(VendorBuyMath.Clamp(-3, 10), 1, "typed -3: 1");
        Eq(VendorBuyMath.Clamp(11, 10), 10, "typed 11 of 10: 10");
        Eq(VendorBuyMath.Clamp(7, 10), 7, "typed 7 of 10: 7");
        Eq(VendorBuyMath.Clamp(7, 0), 1, "nothing buyable: 1");
        Eq(VendorBuyMath.Half(0), 1, "half of 0: 1");
        Eq(VendorBuyMath.Half(1), 1, "half of 1: 1");
        Eq(VendorBuyMath.Half(9), 4, "half of 9: 4");
    }

    // The box and the split-globals fix must use what is tested here.
    private static void BuyBoxWiringPinned()
    {
        string? box = FindUp(Path.Combine("src", "RynthCore.Engine", "ImGui", "Panels", "InventoryFace.VendorBuy.cs"));
        Check(box != null, "found InventoryFace.VendorBuy.cs");
        if (box != null)
        {
            string text = File.ReadAllText(box);
            foreach (string needle in new[]
                     {
                         "VendorBuyMath.Prefill(toolbar, MaxBuy(snap, item))",
                         "VendorBuyMath.MaxBuy(item.Unlimited ? -1 : item.Amount, VendorTrade.MoneyFor(snap)",
                         "VendorBuyMath.Clamp(_buyAmount, max)",
                         "UiDropTargets.Publish(VendorDropKey, Title, origin, origin + size, snap != null, IsVendorItem)",
                         "VendorTrade.RequestBuy(_buyVendor, [_buyItem], [_buyAmount])",
                     })
                Check(text.Contains(needle, StringComparison.Ordinal), "InventoryFace.VendorBuy.cs contains: " + needle);
        }

        string? hooks = FindUp(Path.Combine("src", "RynthCore.Engine", "Compatibility", "ClientHelperHooks.cs"));
        Check(hooks != null, "found ClientHelperHooks.cs");
        if (hooks != null)
        {
            string text = File.ReadAllText(hooks);
            int reads = CountOf(text, "SplitGlobals keep = SplitGlobals.Read();");
            int restores = CountOf(text, "keep.Restore();");
            Check(reads == 2 && restores == 2, $"MoveItemInternal and SplitStackInternal both keep AC's split globals (reads {reads}, restores {restores})");
            Check(!text.Contains("try { Marshal.WriteInt32(new IntPtr(_splitAmountAddr), 1); } catch { }", StringComparison.Ordinal),
                "SplitStackInternal no longer leaves split_amount at 1 after it");
        }
    }

    private static int CountOf(string text, string needle)
    {
        int n = 0;
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
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
