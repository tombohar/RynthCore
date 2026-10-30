// ============================================================================
//  RynthCore.Engine - UI/Data/BountiesSnapshotJson.cs
//  The Bounties window's JSON shape, as the RynthILT plugin's
//  RynthILTGetBountiesJson writes it (mirrors
//  RynthCore.Plugin.RynthILT.Bounty.BountyBridgePayload). Self-contained:
//  RynthSuite's Tools/RynthILT.Tests links this file to check the plugin's
//  output parses here.
//
//  Compatibility: unknown properties are skipped and missing ones keep their
//  defaults, so either side can add fields without breaking the other.
// ============================================================================

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RynthCore.Engine.UI.Data;

/// <summary>Where the player stands on one reward (the plugin's BountyStatus).</summary>
internal enum BountyRowStatus
{
    Counting = 0,
    Cooldown = 1,
    NeedsQb = 2,
}

internal sealed class BountiesSnapshot
{
    [JsonPropertyName("unsupported")]            public bool Unsupported            { get; set; }
    [JsonPropertyName("reading")]                public bool Reading                { get; set; }
    [JsonPropertyName("status")]                 public string Status               { get; set; } = string.Empty;
    /// <summary>Seconds since the last list read; -1 = never.</summary>
    [JsonPropertyName("listedAgoSec")]           public int ListedAgoSec            { get; set; } = -1;
    [JsonPropertyName("noneAnywhere")]           public bool NoneAnywhere           { get; set; }
    [JsonPropertyName("lastEvent")]              public string LastEvent            { get; set; } = string.Empty;
    [JsonPropertyName("lastEventAgoSec")]        public int LastEventAgoSec         { get; set; } = -1;
    [JsonPropertyName("refreshOnKills")]         public bool RefreshOnKills         { get; set; }
    [JsonPropertyName("minRefreshSeconds")]      public int MinRefreshSeconds       { get; set; }
    [JsonPropertyName("periodicRefreshMinutes")] public int PeriodicRefreshMinutes  { get; set; }
    [JsonPropertyName("hideQbLocked")]           public bool HideQbLocked           { get; set; }
    [JsonPropertyName("sortByProgress")]         public bool SortByProgress         { get; set; }
    /// <summary>Rows the plugin left out because of HideQbLocked.</summary>
    [JsonPropertyName("hiddenCount")]            public int HiddenCount             { get; set; }
    [JsonPropertyName("rows")]                   public List<BountyRowJson> Rows    { get; set; } = new();

    /// <summary>Parses one RynthILTGetBountiesJson reply; null for "null". Throws on malformed JSON.</summary>
    public static BountiesSnapshot? Parse(string json) =>
        JsonSerializer.Deserialize(json, BountiesJsonContext.Default.BountiesSnapshot);
}

/// <summary>One reward of one bounty area, with the status as of the plugin's poll.</summary>
internal sealed class BountyRowJson
{
    [JsonPropertyName("where")]           public string Where        { get; set; } = string.Empty;
    [JsonPropertyName("amount")]          public long Amount         { get; set; }
    [JsonPropertyName("item")]            public string Item         { get; set; } = string.Empty;
    [JsonPropertyName("killsRequired")]   public int KillsRequired   { get; set; }
    [JsonPropertyName("cooldownSeconds")] public int CooldownSeconds { get; set; }
    /// <summary>A <see cref="BountyRowStatus"/> value; an ended cooldown already reads Counting.</summary>
    [JsonPropertyName("status")]          public int Status          { get; set; }
    [JsonPropertyName("kills")]           public int Kills           { get; set; }
    [JsonPropertyName("unlockInSec")]     public int UnlockInSec     { get; set; }
    [JsonPropertyName("qbRequired")]      public long QbRequired     { get; set; }
    [JsonPropertyName("qbHave")]          public long QbHave         { get; set; }
    /// <summary>Kills went up (or an award reset them) in the last 15 seconds.</summary>
    [JsonPropertyName("changed")]         public bool Changed        { get; set; }
}

[JsonSerializable(typeof(BountiesSnapshot))]
internal partial class BountiesJsonContext : JsonSerializerContext { }
