using System;

namespace RynthCore.PluginSdk;

// ─── Player-to-player trade (engine API v72) ────────────────────────────────
// TradeStateNative (RynthCoreApiNative.cs) is the ABI struct; plugins use the
// TradeState view below, from RynthCoreHost.TryGetTradeState.

/// <summary>Which side of the trade window (the AC trade side numbers).</summary>
public enum TradeSide
{
    /// <summary>Your items.</summary>
    You = 1,
    /// <summary>The other player's items.</summary>
    Partner = 2,
}

/// <summary>
/// A copy of the engine's trade state (API v72). Compare <see cref="Generation"/>,
/// <see cref="Sequence"/> and the counters with an earlier read to see what changed:
/// a new trade bumps Generation; every trade event bumps Sequence.
/// </summary>
public readonly struct TradeState
{
    public TradeState(TradeStateNative raw)
    {
        Flags = raw.Flags;
        Generation = raw.Generation;
        Sequence = raw.Sequence;
        PartnerId = raw.PartnerId;
        InitiatorId = raw.InitiatorId;
        YourItemCount = raw.SelfItemCount;
        PartnerItemCount = raw.PartnerItemCount;
        LastEventType = raw.LastEventType;
        FailureCount = raw.FailureCount;
        LastFailureItemId = raw.LastFailureItemId;
        LastFailureReason = raw.LastFailureReason;
        LastCloseReason = raw.LastCloseReason;
        LastAcceptedBy = raw.LastAcceptedBy;
        LastDeclinedBy = raw.LastDeclinedBy;
        LastResetBy = raw.LastResetBy;
        CompletedCount = raw.CompletedCount;
        PartnerAcceptCount = raw.PartnerAcceptCount;
    }

    public uint Flags { get; }
    /// <summary>A trade window is open.</summary>
    public bool IsOpen => (Flags & 1u) != 0;
    /// <summary>You have accepted the trade as it stands.</summary>
    public bool YouAccepted => (Flags & 2u) != 0;
    /// <summary>The other player has accepted the trade as it stands.</summary>
    public bool PartnerAccepted => (Flags & 4u) != 0;
    /// <summary>The engine sees the server's trade events (false: the state never changes).</summary>
    public bool Watching => (Flags & 8u) != 0;
    /// <summary>All trade actions (open, add, accept, decline, reset, close) are bound on this client.</summary>
    public bool ActionsAvailable => (Flags & 16u) != 0;

    /// <summary>+1 for every trade that opens.</summary>
    public uint Generation { get; }
    /// <summary>+1 for every trade event.</summary>
    public uint Sequence { get; }
    /// <summary>The other player; 0 when no trade is open.</summary>
    public uint PartnerId { get; }
    /// <summary>The player who opened the trade.</summary>
    public uint InitiatorId { get; }
    public int YourItemCount { get; }
    public int PartnerItemCount { get; }
    /// <summary>The last trade GameEvent (0x01FD RegisterTrade ... 0x0208 ClearTradeAcceptance).</summary>
    public uint LastEventType { get; }
    /// <summary>TradeFailure events so far (an item the server refused to put in the window).</summary>
    public uint FailureCount { get; }
    public uint LastFailureItemId { get; }
    /// <summary>WeenieError of the last TradeFailure (e.g. 0x0426 AttunedItem).</summary>
    public uint LastFailureReason { get; }
    /// <summary>Why the last trade closed: 1 normal, 2 entered combat, 0x51 cancelled.</summary>
    public uint LastCloseReason { get; }
    public uint LastAcceptedBy { get; }
    public uint LastDeclinedBy { get; }
    public uint LastResetBy { get; }
    /// <summary>Trades completed so far ("Trade Complete!").</summary>
    public uint CompletedCount { get; }
    /// <summary>Times the other player has accepted so far.</summary>
    public uint PartnerAcceptCount { get; }
}
