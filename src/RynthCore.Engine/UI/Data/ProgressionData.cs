// ============================================================================
//  RynthCore.Engine - UI/Data/ProgressionData.cs
//  The ILT progression planners (augmentations, enlightenment) and the
//  infinite-attribute raiser for the Skills panel's Progression tab: RynthILT's
//  since the ILT Hub moved there (2026-10-13; RynthAi's before, still read from
//  an older RynthAi). The plugin owns the math and the persisted inputs; this side
//  only shows the snapshot and sends "prog ..." commands (IltCommands.Progression).
// ============================================================================

using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.UI.Data;

/// <summary>RynthILTGetProgressionJson's payload (RynthAi's RynthPluginGetProgressionJson before). Display strings arrive preformatted.</summary>
internal sealed class ProgressionInfo
{
    /// <summary>False off ILT-like worlds (or before the ILT Hub exists): the tab is hidden.</summary>
    [JsonPropertyName("available")] public bool Available { get; set; }
    [JsonPropertyName("world")]     public string World { get; set; } = "";
    [JsonPropertyName("aug")]       public AugPlanner Aug { get; set; } = new();
    [JsonPropertyName("enl")]       public EnlPlanner Enl { get; set; } = new();
    /// <summary>Absent from a much older RynthAi: stays empty and the section shows "update".</summary>
    [JsonPropertyName("attr")]      public AttrRaiser? Attr { get; set; }
}

/// <summary>The ILT Hub's IltAttributeRaiser (RynthILT): server costs ("/xp all") and the raise settings.</summary>
internal sealed class AttrRaiser
{
    /// <summary>The raiser can't run here; <see cref="Reason"/> says why.</summary>
    [JsonPropertyName("off")]        public bool Off { get; set; }
    [JsonPropertyName("reason")]     public string Reason { get; set; } = "";
    [JsonPropertyName("auto")]       public bool Auto { get; set; }
    /// <summary>Minutes between auto-raise runs.</summary>
    [JsonPropertyName("every")]      public int Every { get; set; } = 5;
    /// <summary>0 priority, 1 round robin, 2 cheapest first.</summary>
    [JsonPropertyName("mode")]       public int Mode { get; set; }
    [JsonPropertyName("running")]    public bool Running { get; set; }
    /// <summary>XP reserve, preformatted ("0", "1.50B").</summary>
    [JsonPropertyName("keep")]       public string Keep { get; set; } = "0";
    /// <summary>XP reserve in full ("1,234,567"): what the reserve box edits.</summary>
    [JsonPropertyName("keepFull")]   public string KeepFull { get; set; } = "";
    [JsonPropertyName("status")]     public string Status { get; set; } = "";
    [JsonPropertyName("lastRun")]    public string LastRun { get; set; } = "";
    [JsonPropertyName("unassigned")] public string Unassigned { get; set; } = "";
    /// <summary>Time to the next auto run ("" when auto is off).</summary>
    [JsonPropertyName("next")]       public string Next { get; set; } = "";
    [JsonPropertyName("costsAge")]   public string CostsAge { get; set; } = "";
    /// <summary>Local time the costs were last read ("-" before the first read; "" from older RynthAi).</summary>
    [JsonPropertyName("updated")]    public string Updated { get; set; } = "";
    /// <summary>"Level N   Total XP X   Unassigned Y" ("" from older RynthAi).</summary>
    [JsonPropertyName("xpHeader")]   public string XpHeader { get; set; } = "";
    /// <summary>In raise-priority order.</summary>
    [JsonPropertyName("rows")]       public AttrRow[] Rows { get; set; } = Array.Empty<AttrRow>();
}

internal sealed class AttrRow
{
    /// <summary>/attr abbreviation: str end coo qui foc sel hea sta man.</summary>
    [JsonPropertyName("key")]    public string Key { get; set; } = "";
    [JsonPropertyName("label")]  public string Label { get; set; } = "";
    [JsonPropertyName("vital")]  public bool Vital { get; set; }
    /// <summary>Ticked: "Raise now" and auto-raise may spend on it.</summary>
    [JsonPropertyName("on")]     public bool On { get; set; }
    /// <summary>Base value RynthAi last saw in a server reply (-1 = unknown; the panel prefers the client's).</summary>
    [JsonPropertyName("base")]   public int Base { get; set; } = -1;
    /// <summary>Next-level cost from "/xp all", preformatted ("?" until read).</summary>
    [JsonPropertyName("cost")]   public string Cost { get; set; } = "?";
    /// <summary>Next level fits the raiser's budget (unassigned minus the reserve).</summary>
    [JsonPropertyName("afford")] public bool Afford { get; set; }
    /// <summary>The next-level cost has been read (false from older RynthAi too).</summary>
    [JsonPropertyName("known")]      public bool Known { get; set; }
    /// <summary>By hand: +1 fits the unassigned XP (no reserve).</summary>
    [JsonPropertyName("afford1")]    public bool Afford1 { get; set; }
    /// <summary>By hand: the projected +10 fits the unassigned XP.</summary>
    [JsonPropertyName("afford10")]   public bool Afford10 { get; set; }
    /// <summary>Projected cost of ten levels (7.7% growth per level), e.g. "~1.2T"; "" from older RynthAi.</summary>
    [JsonPropertyName("cost10")]     public string Cost10 { get; set; } = "";
    /// <summary>Exact next-level cost with separators, for the tooltip.</summary>
    [JsonPropertyName("costFull")]   public string CostFull { get; set; } = "";
    [JsonPropertyName("cost10Full")] public string Cost10Full { get; set; } = "";
}

internal sealed class AugPlanner
{
    /// <summary>The server reports /aug off.</summary>
    [JsonPropertyName("off")]        public bool Off { get; set; }
    [JsonPropertyName("status")]     public string Status { get; set; } = "";
    [JsonPropertyName("lumPerCoin")] public long LumPerCoin { get; set; }
    [JsonPropertyName("rows")]       public AugRow[] Rows { get; set; } = Array.Empty<AugRow>();
    [JsonPropertyName("total")]      public string Total { get; set; } = "";
    /// <summary>Empty when the bank holds enough Enlightened Coins.</summary>
    [JsonPropertyName("short")]      public string Short { get; set; } = "";
    [JsonPropertyName("banked")]     public string Banked { get; set; } = "";
    // Totals row and e-coin summary (all empty / zero from older RynthAi).
    [JsonPropertyName("totCur")]      public long TotalCurrent { get; set; }
    [JsonPropertyName("totTgt")]      public long TotalTarget { get; set; }
    [JsonPropertyName("totInc")]      public long TotalIncrease { get; set; }
    [JsonPropertyName("totLum")]      public string TotalLum { get; set; } = "";
    [JsonPropertyName("totCoins")]    public long TotalCoins { get; set; }
    [JsonPropertyName("coinsLine")]   public string CoinsLine { get; set; } = "";
    [JsonPropertyName("lumToBuy")]    public string LumToBuy { get; set; } = "";
    /// <summary>Ready-made clipboard texts (UB's Copy Discord / Copy In-Game).</summary>
    [JsonPropertyName("copyDiscord")] public string CopyDiscord { get; set; } = "";
    [JsonPropertyName("copyIngame")]  public string CopyIngame { get; set; } = "";
    /// <summary>This world's cost rules for the Cost settings editor (null from older RynthAi).</summary>
    [JsonPropertyName("cfg")]         public AugCostConfig? Config { get; set; }
}

/// <summary>Per-server aug cost rules: level i costs (Base + (i-1)*Base*Pct/100) x the multiplier of its tier.</summary>
internal sealed class AugCostConfig
{
    [JsonPropertyName("world")]  public string World { get; set; } = "";
    /// <summary>The world has its own saved rules (false = UB / InfiniteLeaftide defaults).</summary>
    [JsonPropertyName("custom")] public bool Custom { get; set; }
    /// <summary>Tier multipliers: below S1, from S1, from S2, from S3, from S4.</summary>
    [JsonPropertyName("mults")]  public double[] Multipliers { get; set; } = Array.Empty<double>();
    [JsonPropertyName("rows")]   public AugCostRule[] Rows { get; set; } = Array.Empty<AugCostRule>();
}

internal sealed class AugCostRule
{
    [JsonPropertyName("key")]   public string Key { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("base")]  public long Base { get; set; }
    /// <summary>Percent of Base added per level.</summary>
    [JsonPropertyName("pct")]   public double Percent { get; set; }
    [JsonPropertyName("coins")] public int Coins { get; set; }
    [JsonPropertyName("s1")]    public int S1 { get; set; }
    [JsonPropertyName("s2")]    public int S2 { get; set; }
    [JsonPropertyName("s3")]    public int S3 { get; set; }
    [JsonPropertyName("s4")]    public int S4 { get; set; }
    /// <summary>Highest target (0 = no limit).</summary>
    [JsonPropertyName("cap")]   public int Cap { get; set; }
}

internal sealed class AugRow
{
    [JsonPropertyName("key")]   public string Key { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("cur")]   public int Current { get; set; }
    [JsonPropertyName("tgt")]   public int Target { get; set; }
    /// <summary>Levels planned (target - current).</summary>
    [JsonPropertyName("inc")]   public int Increase { get; set; }
    /// <summary>Highest target allowed (the aug's cap, or the current level when the server reports more).</summary>
    [JsonPropertyName("cap")]   public int Cap { get; set; }
    [JsonPropertyName("lum")]   public string Lum { get; set; } = "-";
    [JsonPropertyName("coins")] public long Coins { get; set; }
}

internal sealed class EnlPlanner
{
    /// <summary>The server reports /enl off.</summary>
    [JsonPropertyName("off")]           public bool Off { get; set; }
    [JsonPropertyName("header")]        public string Header { get; set; } = "";
    [JsonPropertyName("next")]          public string Next { get; set; } = "";
    [JsonPropertyName("enl")]           public int Level { get; set; }
    [JsonPropertyName("target")]        public int Target { get; set; }
    [JsonPropertyName("plan")]          public string[] Plan { get; set; } = Array.Empty<string>();
    [JsonPropertyName("coinsPerToken")] public int CoinsPerToken { get; set; }
    [JsonPropertyName("tokensShort")]   public string TokensShort { get; set; } = "";
    /// <summary>Client-side requirements not yet met; empty = the client checks pass.</summary>
    [JsonPropertyName("blockers")]      public string[] Blockers { get; set; } = Array.Empty<string>();
    [JsonPropertyName("serverChecks")]  public string ServerChecks { get; set; } = "";
    [JsonPropertyName("auto")]          public bool Auto { get; set; }
    /// <summary>Auto-enlighten is disarmed every session until confirmed again.</summary>
    [JsonPropertyName("armed")]         public bool Armed { get; set; }
    [JsonPropertyName("spendFirst")]    public bool SpendFirst { get; set; }
    [JsonPropertyName("checkEvery")]    public int CheckEvery { get; set; }
    [JsonPropertyName("status")]        public string Status { get; set; } = "";

    // Enl calculator (RynthILT's IltProgression.AppendEnlCalculatorJson; Silentkelpie's rynth PR 6): per-level
    // rows, materials still-need / have, the 301-325 sigil chain and the e-coin lines. All of it is absent from
    // an older RynthILT (or RynthAi) and from a server without ILT: the arrays stay empty, the strings "", and
    // the calculator is not drawn. The row arrays are read leniently (see EnlRowConverters): a null, a
    // non-array, a non-object row or a mistyped field costs that row, never the whole Progression snapshot.
    [JsonPropertyName("levels")]        [JsonConverter(typeof(EnlLevelRowsConverter))]
    public EnlLevelRow[] Levels { get; set; } = Array.Empty<EnlLevelRow>();
    [JsonPropertyName("materials")]     [JsonConverter(typeof(EnlMaterialRowsConverter))]
    public EnlMaterialRow[] Materials { get; set; } = Array.Empty<EnlMaterialRow>();
    [JsonPropertyName("chain")]         [JsonConverter(typeof(EnlChainRowsConverter))]
    public EnlChainRow[] Chain { get; set; } = Array.Empty<EnlChainRow>();
    [JsonPropertyName("chainNote")]     public string ChainNote { get; set; } = "";
    /// <summary>"E-coins needed: N   have M (bank + pack)..." ("" when no e-coins are involved).</summary>
    [JsonPropertyName("ecoins")]        public string Ecoins { get; set; } = "";
    /// <summary>"Lum to buy e-coins: ~X" or the hint to set Lum per e-coin ("" when no coins are needed).</summary>
    [JsonPropertyName("ecoinsLum")]     public string EcoinsLum { get; set; } = "";

    /// <summary>The snapshot has anything for the calculator section to draw.</summary>
    [JsonIgnore] public bool HasCalculator => Levels.Length > 0 || Materials.Length > 0 || Chain.Length > 0;
}

/// <summary>One enlightenment step of the calculator's per-level table (RynthILT lists at most 100).</summary>
internal sealed class EnlLevelRow
{
    private string? _levelText, _countText;

    /// <summary>The enlightenment level this step reaches.</summary>
    [JsonPropertyName("t")]     public int Level { get; set; }
    /// <summary>"Tokens" / "Medallions" / "Sigils" / "Crests", or "-" for a step with no item.</summary>
    [JsonPropertyName("item")]  public string Item { get; set; } = "-";
    [JsonPropertyName("count")] public int Count { get; set; }
    /// <summary>Luminance for the step, preformatted ("1.50B"), "-" for none.</summary>
    [JsonPropertyName("lum")]   public string Lum { get; set; } = "-";
    /// <summary>The simulated stock (pack items, the sigil chain, luminance) still covers this step.</summary>
    [JsonPropertyName("ok")]    public bool Ok { get; set; }

    // Display text, formatted once per snapshot instead of every frame.
    [JsonIgnore] public string LevelText => _levelText ??= Level.ToString(CultureInfo.InvariantCulture);
    [JsonIgnore] public string CountText => _countText ??= Count > 0 ? Count.ToString("N0", CultureInfo.InvariantCulture) : "-";
}

/// <summary>Still-need vs have for one material (or luminance); need / have arrive preformatted.</summary>
internal sealed class EnlMaterialRow
{
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("need")]  public string Need { get; set; } = "0";
    [JsonPropertyName("have")]  public string Have { get; set; } = "0";
    [JsonPropertyName("ok")]    public bool Ok { get; set; }
}

/// <summary>One row of the 301-325 sigil craft chain. Craft rows count crafts left, raw rows what to get.</summary>
internal sealed class EnlChainRow
{
    private string? _needText, _haveText, _displayLabel;

    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("need")]  public long Need { get; set; }
    [JsonPropertyName("have")]  public long Have { get; set; }
    [JsonPropertyName("ok")]    public bool Ok { get; set; }
    /// <summary>An in-between item the player crafts (Ascension Coins, Lifeblood Elixirs, Lifebloom Resins).</summary>
    [JsonPropertyName("craft")] public bool Craft { get; set; }

    /// <summary>The label with "(craft)" for an in-between item the player crafts.</summary>
    [JsonIgnore] public string DisplayLabel => _displayLabel ??= Craft ? Label + " (craft)" : Label;
    [JsonIgnore] public string NeedText => _needText ??= Need.ToString("N0", CultureInfo.InvariantCulture);
    [JsonIgnore] public string HaveText => _haveText ??= Have.ToString("N0", CultureInfo.InvariantCulture);
}

/// <summary>How much of the Enl calculator the Progression tab draws (pure, so it is tested).</summary>
internal static class EnlCalculatorView
{
    /// <summary>Per-level rows shown until "Show all" is pressed.</summary>
    public const int PreviewLevels = 12;
    /// <summary>Per-level rows drawn at most (RynthILT sends 100 at most), in a scrolling table.</summary>
    public const int MaxLevels = 100;
    /// <summary>Material / chain rows drawn at most (RynthILT sends 5 and 8).</summary>
    public const int MaxListRows = 16;

    /// <summary>Per-level rows to draw for <paramref name="total"/> rows; <paramref name="showAll"/> lifts the preview limit.</summary>
    public static int VisibleLevels(int total, bool showAll)
    {
        if (total <= 0) return 0;
        return Math.Min(total, showAll ? MaxLevels : PreviewLevels);
    }

    /// <summary>There are more rows than the preview shows, so the Show all toggle is offered.</summary>
    public static bool CanExpand(int total) => total > PreviewLevels;
}

[JsonSerializable(typeof(ProgressionInfo))]
internal sealed partial class ProgressionJsonContext : JsonSerializerContext { }

/// <summary>
/// RynthPluginGetProgressionJson (single static buffer: one caller only) while the Skills
/// panel is open, and right after each Progression edit (RequestRefreshAfterPluginTick).
/// </summary>
internal sealed unsafe class ProgressionSource : UiSource<ProgressionInfo>
{
    // RynthILT's RynthILTGetProgressionJson (UTF-8) since the ILT Hub moved there (2026-10-13);
    // a RynthAi from before the move answers RynthPluginGetProgressionJson (ANSI) instead.
    private delegate* unmanaged[Cdecl]<IntPtr> _getProgressionJson;
    private bool _utf8;

    public ProgressionSource() : base("Progression", periodMs: 1000) { }

    protected internal override void Poll()
    {
        if (_getProgressionJson == null)
        {
            _getProgressionJson = (delegate* unmanaged[Cdecl]<IntPtr>)PluginExportBinder.Resolve("RynthILT", "RynthILTGetProgressionJson");
            _utf8 = _getProgressionJson != null;
            if (_getProgressionJson == null)
                _getProgressionJson = (delegate* unmanaged[Cdecl]<IntPtr>)PluginExportBinder.Resolve("RynthAi", "RynthPluginGetProgressionJson");
        }
        if (_getProgressionJson == null) return;
        IntPtr p = _getProgressionJson();
        string json = p == IntPtr.Zero ? "{}" : (_utf8 ? Marshal.PtrToStringUTF8(p) : Marshal.PtrToStringAnsi(p)) ?? "{}";
        Publish(ParseJson(json));
    }

    /// <summary>
    /// The snapshot for a plugin's JSON: an unreadable payload is an empty snapshot (Available false, the tab
    /// hides), never an exception. The Enl calculator's own fields degrade on their own (EnlRowConverters).
    /// </summary>
    internal static ProgressionInfo ParseJson(string? json)
    {
        ProgressionInfo info;
        try { info = JsonSerializer.Deserialize(json ?? "{}", ProgressionJsonContext.Default.ProgressionInfo) ?? new ProgressionInfo(); }
        catch (JsonException) { info = new ProgressionInfo(); }
        EnlPlanner? enl = info.Enl;
        if (enl != null)
        {
            // JSON null into a string property: the UI reads .Length.
            enl.ChainNote ??= "";
            enl.Ecoins ??= "";
            enl.EcoinsLum ??= "";
        }
        return info;
    }

    protected internal override void Reset()
    {
        _getProgressionJson = null;
        ClearSnapshot();
    }
}
