// ============================================================================
//  RynthCore.Engine - UI/Data/ChatSettingsDto.cs
//  RynthChat's JSON: the scrollback lines the plugin hands over, the settings
//  file (rynthchat_settings.json) and the rule editor's Export / Import. Moved
//  out of ChatData.cs (2026-10-05) so tools/ChatFilterTests can load and save
//  rules with the real serializer.
// ============================================================================

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RynthCore.Engine.UI.Data;

// JsonSerializerContext must be at namespace scope (not nested) for source generation.
internal sealed class RynthChatLineDto
{
    [JsonPropertyName("seq")]    public ulong Seq      { get; set; }
    [JsonPropertyName("ts")]     public string Ts      { get; set; } = "";
    [JsonPropertyName("chan")]   public string Chan    { get; set; } = "";
    /// <summary>ChatMessageType; RynthChat before 0.2.0 doesn't send it.</summary>
    [JsonPropertyName("type")]   public int Type       { get; set; } = -1;
    [JsonPropertyName("sender")] public string? Sender { get; set; }
    [JsonPropertyName("text")]   public string Text    { get; set; } = "";
}

// Property names match the legacy hand-written JSON so existing settings load.
internal sealed class RynthChatFilterDto
{
    [JsonPropertyName("pattern")] public string? Pattern { get; set; }
    [JsonPropertyName("tab")]     public string? Tab     { get; set; }
    [JsonPropertyName("enabled")] public bool    Enabled { get; set; } = true;
    // Version 2:
    /// <summary>"move" | "copy" | "hide" | "color"; absent in version 1 (see ChatModel.FromDto).</summary>
    [JsonPropertyName("action")]    public string? Action    { get; set; }
    /// <summary>"" / absent: any line; "chan:Combat"; "type:3".</summary>
    [JsonPropertyName("when")]      public string? When      { get; set; }
    /// <summary>"line" | "match"; absent: no colour.</summary>
    [JsonPropertyName("colorMode")] public string? ColorMode { get; set; }
    /// <summary>"#RRGGBB".</summary>
    [JsonPropertyName("color")]     public string? Color     { get; set; }
    // 2026-10-05:
    /// <summary>"contains" | "startswith" | "endswith"; absent: a regex (ChatRuleMatching).</summary>
    [JsonPropertyName("mode")]      public string? Mode      { get; set; }
}

internal sealed class RynthChatSettingsDto
{
    /// <summary>Absent (0) in version-1 files.</summary>
    [JsonPropertyName("version")]         public int    Version         { get; set; }
    [JsonPropertyName("fontSize")]        public double FontSize        { get; set; } = 10;
    [JsonPropertyName("backgroundAlpha")] public int    BackgroundAlpha { get; set; } = 0xF2;
    [JsonPropertyName("autoScroll")]      public bool   AutoScroll      { get; set; } = true;
    [JsonPropertyName("suppressChat")]    public bool   SuppressChat    { get; set; }
    [JsonPropertyName("logEnabled")]      public bool   LogEnabled      { get; set; }
    // Chat's own Ctrl-gated click-through, like RadarPanel's (2026-09-02).
    [JsonPropertyName("ctrlGatedClickThrough")] public bool CtrlGatedClickThrough { get; set; }
    [JsonPropertyName("activeChannel")]   public string? ActiveChannel  { get; set; }
    [JsonPropertyName("showTimestamps")]  public bool   ShowTimestamps  { get; set; } = true;
    [JsonPropertyName("mentionHighlight")] public bool  MentionHighlight { get; set; } = true;
    /// <summary>Hidden lines are also kept out of AC's chat window; null (older files) = on.</summary>
    [JsonPropertyName("hideInRetail")]    public bool?  HideInRetail    { get; set; }
    [JsonPropertyName("mentionSound")]    public bool   MentionSound    { get; set; }
    [JsonPropertyName("mentionWords")]    public string? MentionWords   { get; set; }
    // 2026-10-08: per-kind line colours (ChatLineColors), { "say": "#RRGGBB", "chan:General": ..., "tab:Chat": ... };
    // only the colours the user set, and absent while none is.
    [JsonPropertyName("lineColors")]      public Dictionary<string, string>? LineColors { get; set; }
    // 2026-10-28: lines in AC's own colour for their kind (ChatLineColors.RetailColors); absent while on.
    [JsonPropertyName("retailColors")]    public bool?  RetailColors    { get; set; }
    // 2026-10-11: how many lines the scrollback keeps (ChatHistory); absent at the default (2000).
    [JsonPropertyName("historyLines")]    public int?   HistoryLines    { get; set; }
    // 2026-10-20: the tabs in a row across the top instead of the channels drawer; absent while off.
    [JsonPropertyName("tabsAcross")]      public bool?  TabsAcross      { get; set; }
    [JsonPropertyName("customTabs")]      public string[]? CustomTabs   { get; set; }
    [JsonPropertyName("filters")]         public RynthChatFilterDto[]? Filters { get; set; }
    /// <summary>Standard filters' user state (RynthChatPresets); absent while none was changed.</summary>
    [JsonPropertyName("presets")]         public RynthChatPresetDto[]? Presets { get; set; }
}

/// <summary>A standard filter's user state (RynthChatPresets), by preset id.</summary>
internal sealed class RynthChatPresetDto
{
    [JsonPropertyName("id")]      public string? Id      { get; set; }
    [JsonPropertyName("enabled")] public bool    Enabled { get; set; }
    [JsonPropertyName("tab")]     public string? Tab     { get; set; }
}

/// <summary>Export / import on the clipboard (the rule editor's Export and Import).</summary>
internal sealed class RynthChatRuleSetDto
{
    public const string FormatName = "rynthchat-rules/2";
    [JsonPropertyName("format")] public string? Format { get; set; }
    [JsonPropertyName("tabs")]   public string[]? Tabs { get; set; }
    [JsonPropertyName("rules")]  public RynthChatFilterDto[]? Rules { get; set; }
}

[JsonSerializable(typeof(RynthChatLineDto[]))]
[JsonSerializable(typeof(RynthChatLineDto))]
[JsonSerializable(typeof(RynthChatSettingsDto))]
[JsonSerializable(typeof(RynthChatRuleSetDto))]
[JsonSerializable(typeof(RynthChatFilterDto[]))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class RynthChatJsonContext : JsonSerializerContext { }
