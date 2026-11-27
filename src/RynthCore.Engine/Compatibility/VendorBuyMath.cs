// ============================================================================
//  RynthCore.Engine - Compatibility/VendorBuyMath.cs
//
//  The Inventory panel's "Buy how many?" box (2026-11-26, a player at a vendor
//  couldn't buy in bulk with RynthCore's windows up): an item dragged out of an
//  open vendor's list onto the panel asks how many, then buys through
//  VendorTrade. These are its numbers:
//
//    max      the most one buy can ask for: what the vendor has (unlimited: the
//             engine's 24-bit limit), and, when the money is known, what it pays
//             for (the cost isn't linear: ACE prices each created stack with a
//             floor of 1, so it is searched, not divided). Pack room and burden
//             aren't counted here: VendorTrade checks them before it sends and
//             says why it won't.
//    prefill  the box starts at the retail toolbar's number (AC's split_amount,
//             what a drag of that item would take in AC's own windows) when it
//             is 2 or more and can be bought, else at 1.
//    clamp    a typed number stays between 1 and max (1 when max is 0: the Buy
//             button is then off and says why).
//
//  Pure (no AC, no engine state): tools\VendorRateTests compiles it in.
// ============================================================================

using System;

namespace RynthCore.Engine.Compatibility;

internal static class VendorBuyMath
{
    /// <summary>The most one request carries (an ItemProfile packs 24 bits; VendorTrade's MaxTradeAmount).</summary>
    public const int MaxAmount = 0x7FFFFF;

    /// <summary>
    /// The most one buy of this item can ask for. <paramref name="stock"/>: what the vendor has (below 0:
    /// unlimited). <paramref name="money"/>: what the player can pay with (below 0: unknown, not counted).
    /// <paramref name="cost"/>: the price of n (rises with n). 0 when not even one can be bought.
    /// </summary>
    public static int MaxBuy(int stock, long money, Func<int, long> cost)
    {
        int cap = stock < 0 ? MaxAmount : Math.Min(stock, MaxAmount);
        if (cap < 1) return 0;
        if (money < 0) return cap;
        if (Cost(cost, 1) > money) return 0;
        if (Cost(cost, cap) <= money) return cap;
        // The largest n in 1..cap with cost(n) <= money: cost(lo) fits, cost(hi) doesn't.
        int lo = 1, hi = cap;
        while (hi - lo > 1)
        {
            int mid = lo + (hi - lo) / 2;
            if (Cost(cost, mid) <= money) lo = mid; else hi = mid;
        }
        return lo;
    }

    private static long Cost(Func<int, long> cost, int n)
    {
        long c = cost(n);
        return c < 0 ? long.MaxValue : c;
    }

    /// <summary>Where the box starts: the retail toolbar's number when it is 2 or more and within <paramref name="max"/>, else 1.</summary>
    public static int Prefill(int toolbarNumber, int max)
    {
        if (max < 1) return 1;
        if (toolbarNumber >= 2) return Math.Min(toolbarNumber, max);
        return 1;
    }

    /// <summary>A typed number kept between 1 and <paramref name="max"/> (1 when nothing can be bought).</summary>
    public static int Clamp(int typed, int max) => max < 1 ? 1 : Math.Clamp(typed, 1, max);

    /// <summary>Half of <paramref name="max"/>, at least 1.</summary>
    public static int Half(int max) => Math.Max(1, max / 2);
}
