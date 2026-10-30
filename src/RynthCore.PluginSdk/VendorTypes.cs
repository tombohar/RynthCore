using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace RynthCore.PluginSdk;

// ─── Vendor trading ABI structs (engine API v67) ────────────────────────────
// Must mirror RynthCore.Engine/Plugins/PluginContract.cs exactly (Pack=4, same
// field order, same fixed-buffer sizes). Plugins normally use the managed
// VendorInfo / VendorItem / VendorTradeStatus types below instead.

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public unsafe struct VendorInfoNative
{
    public uint VendorId;
    public uint Generation;
    public int ShopMode;
    public uint ItemTypes;
    public int MinValue;
    public int MaxValue;
    public int DealsMagic;
    public float BuyRate;
    public float SellRate;
    public uint AltCurrencyWcid;
    public int AltCurrencyServerCount;
    public int AltCurrencyHave;
    public int PlayerCoins;
    public int ItemCount;
    public uint Flags;
    public uint Reserved0;
    public fixed byte Name[64];
    public fixed byte AltCurrencyName[64];
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public unsafe struct VendorItemNative
{
    public uint ObjectId;
    public uint Wcid;
    public uint ItemType;
    public uint IconId;
    public int Amount;
    public int StackSize;
    public int MaxStackSize;
    public int Value;
    public int UnitValue;
    public int UnitPrice;
    public int Burden;
    public uint Flags;
    public uint Reserved0;
    public uint Reserved1;
    public fixed byte Name[64];
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct VendorTradeEntryNative
{
    public uint ObjectId;
    public int Amount;

    public VendorTradeEntryNative(uint objectId, int amount)
    {
        ObjectId = objectId;
        Amount = amount;
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public unsafe struct VendorTradeStatusNative
{
    public uint RequestId;
    public int State;
    public int Result;
    public int IsBuy;
    public uint VendorId;
    public int EntryCount;
    public int Estimate;
    public uint Reserved0;
    public fixed byte Message[128];
}

// ─── Managed views ──────────────────────────────────────────────────────────

/// <summary>The vendor that is open right now (Decal: WorldFilter.OpenVendor).</summary>
public sealed class VendorInfo
{
    public uint VendorId { get; init; }
    public string Name { get; init; } = string.Empty;
    /// <summary>Bumps each time the server (re)sends the vendor list: on open and after every accepted buy/sell.</summary>
    public uint Generation { get; init; }
    public int ShopMode { get; init; }
    /// <summary>ITEM_TYPE mask of what the vendor will buy from you.</summary>
    public uint ItemTypes { get; init; }
    public int MinValue { get; init; }
    public int MaxValue { get; init; }
    public bool DealsMagic { get; init; }
    /// <summary>The vendor pays value x BuyRate when you sell (promissory notes at 1.0).</summary>
    public float BuyRate { get; init; }
    /// <summary>The vendor charges value x SellRate when you buy (promissory notes at 1.15).</summary>
    public float SellRate { get; init; }
    /// <summary>WCID of the currency this vendor takes; 0 = pyreals.</summary>
    public uint AltCurrencyWcid { get; init; }
    public string AltCurrencyName { get; init; } = string.Empty;
    /// <summary>Alt currency count as the server put it in the vendor list (-1 if none).</summary>
    public int AltCurrencyServerCount { get; init; }
    /// <summary>Alt currency the engine counted in your packs (-1 = not counted yet / not an alt-currency vendor).</summary>
    public int AltCurrencyHave { get; init; }
    /// <summary>Your pyreals (CoinValue), refreshed on AC's main thread; -1 unknown.</summary>
    public int PlayerCoins { get; init; }
    public int ItemCount { get; init; }
    /// <summary>The engine can trade with this vendor (client functions found, layout checked).</summary>
    public bool TradingAvailable { get; init; }
    /// <summary>A buy/sell is queued or waiting for the server.</summary>
    public bool TradeInFlight { get; init; }
    public bool UsesAltCurrency => AltCurrencyWcid != 0;
    public bool Buys(uint itemType) => (ItemTypes & itemType) != 0;
}

/// <summary>One item on the open vendor's list.</summary>
public sealed class VendorItem
{
    public uint ObjectId { get; init; }
    public uint Wcid { get; init; }
    public string Name { get; init; } = string.Empty;
    public uint ItemType { get; init; }
    public uint IconId { get; init; }
    /// <summary>How many the vendor has; -1 = unlimited.</summary>
    public int Amount { get; init; }
    public int StackSize { get; init; }
    public int MaxStackSize { get; init; }
    /// <summary>Value of the listed stack.</summary>
    public int Value { get; init; }
    public int UnitValue { get; init; }
    /// <summary>What one unit costs you at this vendor (server rounding).</summary>
    public int UnitPrice { get; init; }
    public int Burden { get; init; }
    public bool Unlimited { get; init; }
    public bool Stackable { get; init; }
    /// <summary>Packs and foci need a container slot.</summary>
    public bool NeedsContainerSlot { get; init; }
}

/// <summary>Outcome of the most recent VendorBuy / VendorSell request.</summary>
public sealed class VendorTradeStatus
{
    public uint RequestId { get; init; }
    public VendorTradeState State { get; init; }
    public VendorTradeResult Result { get; init; }
    public bool IsBuy { get; init; }
    public uint VendorId { get; init; }
    public int EntryCount { get; init; }
    /// <summary>Buy: estimated cost (pyreals or alt currency). Sell: estimated payout in pyreals.</summary>
    public int Estimate { get; init; }
    public string Message { get; init; } = string.Empty;
    /// <summary>Finished one way or the other (done or refused).</summary>
    public bool IsFinished => State is VendorTradeState.Done or VendorTradeState.Refused;
    public bool Succeeded => State == VendorTradeState.Done && Result == VendorTradeResult.Accepted;
}

public enum VendorTradeState
{
    None = 0,
    /// <summary>Passed the snapshot checks; waiting for AC's main thread.</summary>
    Queued = 1,
    /// <summary>Sent through the client; waiting for the server.</summary>
    Sent = 2,
    /// <summary>The server answered (see Result).</summary>
    Done = 3,
    /// <summary>Never sent: no vendor, unknown item, not enough money/space/burden, ... (see Message).</summary>
    Refused = 4,
}

public enum VendorTradeResult
{
    None = 0,
    /// <summary>The server re-sent the vendor list: the trade went through.</summary>
    Accepted = 1,
    /// <summary>The server answered without re-sending the list: it refused (busy, funds, space...).</summary>
    AnsweredNoRefresh = 2,
    /// <summary>No answer within 10 s.</summary>
    TimedOut = 3,
}

/// <summary>
/// Decal-shaped buy/sell lists (Actions.VendorAddBuyList / VendorBuyAll / VendorClearBuyList
/// and the Sell equivalents) on top of the stateless engine calls. Plugin-local: each
/// plugin keeps its own cart. BuyAll/SellAll send the whole list as ONE transaction
/// (max 100 lines) and return the request id (0 = refused, see GetVendorTradeStatus);
/// the list is cleared only when the request is accepted for sending.
/// Wait for the status to finish (and the vendor Generation to bump) before the next batch.
/// </summary>
public sealed class VendorCart
{
    private readonly List<VendorTradeEntryNative> _buy = new();
    private readonly List<uint> _sell = new();

    public IReadOnlyList<VendorTradeEntryNative> BuyList => _buy;
    public IReadOnlyList<uint> SellList => _sell;

    /// <summary>Add (or top up) a vendor item on the buy list.</summary>
    public void AddBuy(uint objectId, int amount)
    {
        if (objectId == 0 || amount <= 0) return;
        for (int i = 0; i < _buy.Count; i++)
        {
            if (_buy[i].ObjectId == objectId)
            {
                _buy[i] = new VendorTradeEntryNative(objectId, _buy[i].Amount + amount);
                return;
            }
        }
        _buy.Add(new VendorTradeEntryNative(objectId, amount));
    }

    /// <summary>Add one of your own items (whole stack) to the sell list.</summary>
    public void AddSell(uint objectId)
    {
        if (objectId != 0 && !_sell.Contains(objectId))
            _sell.Add(objectId);
    }

    public void ClearBuy() => _buy.Clear();
    public void ClearSell() => _sell.Clear();

    public uint BuyAll(RynthCoreHost host, uint vendorId = 0)
    {
        uint id = host.VendorBuy(vendorId, _buy);
        if (id != 0) _buy.Clear();
        return id;
    }

    public uint SellAll(RynthCoreHost host, uint vendorId = 0)
    {
        uint id = host.VendorSell(vendorId, _sell);
        if (id != 0) _sell.Clear();
        return id;
    }
}
