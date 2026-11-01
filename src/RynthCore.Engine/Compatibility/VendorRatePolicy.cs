// ============================================================================
//  RynthCore.Engine - Compatibility/VendorRatePolicy.cs
//  The pure part of VendorTrade's profile check: whether a buy/sell rate read
//  from the client's vendor profile is a real rate or a misread. Compiled into
//  tools\VendorRateTests.
//
//  A rate of exactly 0 is a real rate. A custom server can run a free storage
//  vendor (a "vault") whose BuyPrice and SellPrice are both 0; it pays nothing
//  for what you hand it and charges nothing for what you take back. Refusing
//  0 turned trading off for such vendors. Negative, NaN, infinite and huge
//  values still mean the profile was misread, and trading stays off for them.
//
//  The price helpers live here too so the test can pin them: a 0 rate prices
//  at 0 (the vault's own GetSellCost/GetBuyCost return 0), while any rate
//  above 0 keeps ACE's 1-per-stack floor. The server stays the authority.
// ============================================================================

using System;

namespace RynthCore.Engine.Compatibility;

internal static class VendorRatePolicy
{
    /// <summary>Rates at or above this are taken as a misread profile.</summary>
    public const float MaxRate = 1000f;

    /// <summary>True when <paramref name="rate"/> is finite and in [0, MaxRate).</summary>
    public static bool LooksSane(float rate) => float.IsFinite(rate) && rate >= 0f && rate < MaxRate;

    /// <summary>True when both rates are exactly 0: a free vendor (it neither charges nor pays).</summary>
    public static bool IsFreeVendor(float buyRate, float sellRate) => buyRate == 0f && sellRate == 0f;

    public const uint ItemTypePromissoryNote = 0x00040000;

    /// <summary>
    /// What the player pays for one created stack worth <paramref name="stackValue"/> (ACE Vendor.GetSellCost):
    /// max(1, ceil(rate * value - 0.1)), promissory notes at 1.15. A vendor rate of exactly 0 is a free
    /// vendor and costs 0 (no 1-per-stack floor); every rate above 0 keeps the stock ACE floor.
    /// </summary>
    public static long PlayerPaysForStack(float sellRate, uint itemType, long stackValue)
    {
        if (sellRate == 0f)
            return 0;
        float rate = itemType == ItemTypePromissoryNote ? 1.15f : sellRate;
        float product = rate * stackValue;
        return Math.Max(1L, (long)Math.Ceiling(product - 0.1));
    }

    /// <summary>
    /// What the vendor pays for one item worth <paramref name="value"/> (ACE Vendor.GetBuyCost):
    /// max(1, floor(rate * value + 0.1)), promissory notes at 1.0. A vendor rate of exactly 0 pays 0.
    /// </summary>
    public static long VendorPaysForStack(float buyRate, uint itemType, long value)
    {
        if (buyRate == 0f)
            return 0;
        float rate = itemType == ItemTypePromissoryNote ? 1.0f : buyRate;
        float product = rate * value;
        return Math.Max(1L, (long)Math.Floor(product + 0.1));
    }
}
