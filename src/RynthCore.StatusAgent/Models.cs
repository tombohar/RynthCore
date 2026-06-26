using System.Text.Json.Serialization;

namespace RynthCore.StatusAgent;

// ── Incoming: the per-client status file the engine writes ──────────────────

/// <summary>Mirror of the engine's <c>rynthcore.client-status/1</c> file.</summary>
internal sealed class StatusFileModel
{
    [JsonPropertyName("schema")]            public string Schema { get; set; } = "";
    [JsonPropertyName("ts")]                public DateTimeOffset Ts { get; set; }
    [JsonPropertyName("host")]              public string Host { get; set; } = "";
    [JsonPropertyName("pid")]               public int Pid { get; set; }
    [JsonPropertyName("account")]           public string Account { get; set; } = "";
    [JsonPropertyName("character")]         public string Character { get; set; } = "";
    [JsonPropertyName("server")]            public string Server { get; set; } = "";
    [JsonPropertyName("uptimeSec")]         public long UptimeSec { get; set; }
    [JsonPropertyName("fps")]               public int Fps { get; set; }
    [JsonPropertyName("pluginTicksPerSec")] public int PluginTicksPerSec { get; set; }
    [JsonPropertyName("workingSetMB")]      public long WorkingSetMB { get; set; }
    [JsonPropertyName("inWorld")]           public bool InWorld { get; set; }
    [JsonPropertyName("queueDropped")]      public long QueueDropped { get; set; }
    [JsonPropertyName("reconciles")]        public long Reconciles { get; set; }
    [JsonPropertyName("forceClears")]       public long ForceClears { get; set; }
    // Player stats written by the engine (PrefetchPlayerStats) — top-level, not in the bot blob,
    // because the off-thread plugin pump can't read them. kills/hour stays bot-derived below.
    [JsonPropertyName("deaths")]            public int Deaths { get; set; }
    [JsonPropertyName("deathsSession")]     public int DeathsSession { get; set; }
    [JsonPropertyName("vitaePct")]          public double VitaePct { get; set; }
    [JsonPropertyName("xpPerHour")]         public double XpPerHour { get; set; }
    [JsonPropertyName("luminancePerHour")]  public double LuminancePerHour { get; set; }
    [JsonPropertyName("xpSession")]         public long XpSession { get; set; }
    [JsonPropertyName("burdenPct")]         public double BurdenPct { get; set; }
    [JsonPropertyName("area")]              public string Area { get; set; } = "";
    [JsonPropertyName("lastIssue")]         public string? LastIssue { get; set; }
    [JsonPropertyName("lastIssueAgeSec")]   public long LastIssueAgeSec { get; set; } = -1;
    [JsonPropertyName("bot")]               public BotSnapshot? Bot { get; set; }
}

/// <summary>The subset of the RynthAi snapshot the agent surfaces.</summary>
internal sealed class BotSnapshot
{
    [JsonPropertyName("macroRunning")]      public bool MacroRunning { get; set; }
    [JsonPropertyName("currentState")]      public string CurrentState { get; set; } = "";
    [JsonPropertyName("botAction")]         public string BotAction { get; set; } = "";
    [JsonPropertyName("selectedProfile")]   public string SelectedProfile { get; set; } = "";
    [JsonPropertyName("currentNavName")]    public string CurrentNavName { get; set; } = "";
    [JsonPropertyName("currentLootName")]   public string CurrentLootName { get; set; } = "";
    [JsonPropertyName("currentMetaName")]   public string CurrentMetaName { get; set; } = "";
    [JsonPropertyName("combatEnabled")]     public bool CombatEnabled { get; set; }
    [JsonPropertyName("buffingEnabled")]    public bool BuffingEnabled { get; set; }
    [JsonPropertyName("navigationEnabled")] public bool NavigationEnabled { get; set; }
    [JsonPropertyName("lootingEnabled")]    public bool LootingEnabled { get; set; }
    [JsonPropertyName("metaEnabled")]       public bool MetaEnabled { get; set; }
    // Profile lists + current selection, so a remote client can offer a profile picker.
    [JsonPropertyName("navProfiles")]       public List<string>? NavProfiles { get; set; }
    [JsonPropertyName("lootProfiles")]      public List<string>? LootProfiles { get; set; }
    [JsonPropertyName("metaProfiles")]      public List<string>? MetaProfiles { get; set; }
    [JsonPropertyName("selectedNavIdx")]    public int SelectedNavIdx { get; set; } = -1;
    [JsonPropertyName("selectedLootIdx")]   public int SelectedLootIdx { get; set; } = -1;
    [JsonPropertyName("selectedMetaIdx")]   public int SelectedMetaIdx { get; set; } = -1;
    [JsonPropertyName("targetLabel")]       public string TargetLabel { get; set; } = "";
    [JsonPropertyName("playerHealth")]      public uint PlayerHealth { get; set; }
    [JsonPropertyName("playerMaxHealth")]   public uint PlayerMaxHealth { get; set; }
    [JsonPropertyName("playerStamina")]     public uint PlayerStamina { get; set; }
    [JsonPropertyName("playerMaxStamina")]  public uint PlayerMaxStamina { get; set; }
    [JsonPropertyName("playerMana")]        public uint PlayerMana { get; set; }
    [JsonPropertyName("playerMaxMana")]     public uint PlayerMaxMana { get; set; }
    [JsonPropertyName("killsPerHour")]      public double KillsPerHour { get; set; }
    [JsonPropertyName("sessionKills")]      public int SessionKills { get; set; }
    [JsonPropertyName("secsSinceLastKill")] public int SecsSinceLastKill { get; set; } = -1;
    [JsonPropertyName("freeSlots")]         public int FreeSlots { get; set; } = -1;
    [JsonPropertyName("uiHidden")]          public bool UiHidden { get; set; }
    [JsonPropertyName("isMinimized")]       public bool IsMinimized { get; set; }
    [JsonPropertyName("scarabs")]           public int Scarabs { get; set; } = -1;
    [JsonPropertyName("tapers")]            public int Tapers { get; set; } = -1;
    [JsonPropertyName("scarabsByType")]     public List<ScarabCount>? ScarabsByType { get; set; }
    [JsonPropertyName("equipment")]         public List<EquipItem>? Equipment { get; set; }
    [JsonPropertyName("recentChat")]        public List<ChatLine>? RecentChat { get; set; }
}

/// <summary>One scarab tier and its inventory count.</summary>
internal sealed class ScarabCount
{
    [JsonPropertyName("name")]  public string Name { get; set; } = "";
    [JsonPropertyName("count")] public int Count { get; set; }
}

/// <summary>One captured chat line: text + AC chat-type (for colouring).</summary>
internal sealed class ChatLine
{
    [JsonPropertyName("t")] public string Text { get; set; } = "";
    [JsonPropertyName("c")] public int Type { get; set; }
}

/// <summary>One worn/wielded item with its full appraisal (Assess/Identify data).</summary>
internal sealed class EquipItem
{
    [JsonPropertyName("name")]        public string Name { get; set; } = "";
    [JsonPropertyName("id")]          public uint Id { get; set; }
    [JsonPropertyName("slot")]        public int Slot { get; set; }
    [JsonPropertyName("armorLevel")]  public int ArmorLevel { get; set; }
    [JsonPropertyName("resist")]      public List<double>? Resist { get; set; }   // 7: slash,pierce,bludge,cold,fire,acid,electric
    [JsonPropertyName("value")]       public int Value { get; set; }
    [JsonPropertyName("burden")]      public int Burden { get; set; }
    [JsonPropertyName("workmanship")] public int Workmanship { get; set; }
    [JsonPropertyName("material")]    public int Material { get; set; }
    [JsonPropertyName("maxMana")]     public int MaxMana { get; set; }
    [JsonPropertyName("curMana")]     public int CurMana { get; set; }
    [JsonPropertyName("damage")]      public int Damage { get; set; }
    [JsonPropertyName("damageType")]  public int DamageType { get; set; }
    [JsonPropertyName("weaponDef")]   public double WeaponDef { get; set; }
    [JsonPropertyName("missileDef")]  public double MissileDef { get; set; }
    [JsonPropertyName("magicDef")]    public double MagicDef { get; set; }
    [JsonPropertyName("variance")]    public double Variance { get; set; }
    [JsonPropertyName("elementalMod")]public double ElementalMod { get; set; }
    [JsonPropertyName("spells")]      public List<string>? Spells { get; set; }
    [JsonPropertyName("longDesc")]    public string? LongDesc { get; set; }
}

// ── Outgoing: the rolled-up payload posted to the user's backend ────────────

internal sealed class Vitals
{
    [JsonPropertyName("hp")]    public uint Hp { get; set; }
    [JsonPropertyName("maxHp")] public uint MaxHp { get; set; }
    [JsonPropertyName("st")]    public uint St { get; set; }
    [JsonPropertyName("maxSt")] public uint MaxSt { get; set; }
    [JsonPropertyName("mn")]    public uint Mn { get; set; }
    [JsonPropertyName("maxMn")] public uint MaxMn { get; set; }
}

/// <summary>One client's derived status, as the phone app consumes it.</summary>
internal sealed class ClientStatus
{
    [JsonPropertyName("pid")]               public int Pid { get; set; }
    [JsonPropertyName("host")]              public string Host { get; set; } = "";
    [JsonPropertyName("account")]           public string Account { get; set; } = "";
    [JsonPropertyName("character")]         public string Character { get; set; } = "";
    [JsonPropertyName("server")]            public string Server { get; set; } = "";

    /// <summary>running | botting | idle | loading | wedged | hung | dead.</summary>
    [JsonPropertyName("state")]             public string State { get; set; } = "unknown";
    [JsonPropertyName("healthy")]           public bool Healthy { get; set; }
    /// <summary>Seconds since this client's snapshot was last refreshed.</summary>
    [JsonPropertyName("ageSec")]            public double AgeSec { get; set; }
    /// <summary>"status-file" (rich) or "heartbeat-log" (basic fallback).</summary>
    [JsonPropertyName("source")]            public string Source { get; set; } = "";

    [JsonPropertyName("uptimeSec")]         public long UptimeSec { get; set; }
    [JsonPropertyName("fps")]               public int Fps { get; set; }
    [JsonPropertyName("pluginTicksPerSec")] public int PluginTicksPerSec { get; set; }
    [JsonPropertyName("workingSetMB")]      public long WorkingSetMB { get; set; }
    [JsonPropertyName("inWorld")]           public bool InWorld { get; set; }

    [JsonPropertyName("macroRunning")]      public bool MacroRunning { get; set; }
    [JsonPropertyName("currentState")]      public string CurrentState { get; set; } = "";
    [JsonPropertyName("botAction")]         public string BotAction { get; set; } = "";
    [JsonPropertyName("profile")]           public string Profile { get; set; } = "";
    [JsonPropertyName("navProfile")]        public string NavProfile { get; set; } = "";
    [JsonPropertyName("lootProfile")]       public string LootProfile { get; set; } = "";
    [JsonPropertyName("metaProfile")]       public string MetaProfile { get; set; } = "";
    [JsonPropertyName("target")]            public string Target { get; set; } = "";

    // Control state (status-file clients): the five subsystem flags + profile lists/selection,
    // so a remote client can show the current switch positions and offer a profile picker.
    [JsonPropertyName("combatEnabled")]     public bool CombatEnabled { get; set; }
    [JsonPropertyName("buffingEnabled")]    public bool BuffingEnabled { get; set; }
    [JsonPropertyName("navigationEnabled")] public bool NavigationEnabled { get; set; }
    [JsonPropertyName("lootingEnabled")]    public bool LootingEnabled { get; set; }
    [JsonPropertyName("metaEnabled")]       public bool MetaEnabled { get; set; }
    [JsonPropertyName("navProfiles")]       public List<string>? NavProfiles { get; set; }
    [JsonPropertyName("lootProfiles")]      public List<string>? LootProfiles { get; set; }
    [JsonPropertyName("metaProfiles")]      public List<string>? MetaProfiles { get; set; }
    [JsonPropertyName("selectedNavIdx")]    public int SelectedNavIdx { get; set; } = -1;
    [JsonPropertyName("selectedLootIdx")]   public int SelectedLootIdx { get; set; } = -1;
    [JsonPropertyName("selectedMetaIdx")]   public int SelectedMetaIdx { get; set; } = -1;
    [JsonPropertyName("player")]            public Vitals? Player { get; set; }

    [JsonPropertyName("queueDropped")]      public long QueueDropped { get; set; }
    [JsonPropertyName("reconciles")]        public long Reconciles { get; set; }
    [JsonPropertyName("forceClears")]       public long ForceClears { get; set; }

    [JsonPropertyName("deaths")]            public int Deaths { get; set; }
    [JsonPropertyName("deathsSession")]     public int DeathsSession { get; set; }
    [JsonPropertyName("vitaePct")]          public double VitaePct { get; set; }
    [JsonPropertyName("killsPerHour")]      public double KillsPerHour { get; set; }
    [JsonPropertyName("xpPerHour")]         public double XpPerHour { get; set; }
    [JsonPropertyName("luminancePerHour")]  public double LuminancePerHour { get; set; }
    [JsonPropertyName("xpSession")]         public long XpSession { get; set; }
    [JsonPropertyName("burdenPct")]         public double BurdenPct { get; set; }
    [JsonPropertyName("area")]              public string Area { get; set; } = "";
    [JsonPropertyName("sessionKills")]      public int SessionKills { get; set; }
    [JsonPropertyName("secsSinceLastKill")] public int SecsSinceLastKill { get; set; } = -1;
    [JsonPropertyName("freeSlots")]         public int FreeSlots { get; set; } = -1;
    [JsonPropertyName("uiHidden")]          public bool UiHidden { get; set; }
    [JsonPropertyName("isMinimized")]       public bool IsMinimized { get; set; }
    [JsonPropertyName("scarabs")]           public int Scarabs { get; set; } = -1;
    [JsonPropertyName("tapers")]            public int Tapers { get; set; } = -1;
    [JsonPropertyName("scarabsByType")]     public List<ScarabCount>? ScarabsByType { get; set; }
    [JsonPropertyName("equipment")]         public List<EquipItem>? Equipment { get; set; }
    [JsonPropertyName("recentChat")]        public List<ChatLine>? RecentChat { get; set; }
    [JsonPropertyName("lastIssue")]         public string? LastIssue { get; set; }
    [JsonPropertyName("lastIssueAgeSec")]   public long LastIssueAgeSec { get; set; } = -1;
}

internal sealed class AggregatePayload
{
    [JsonPropertyName("schema")]         public string Schema { get; set; } = "rynthcore.status-agent/1";
    [JsonPropertyName("host")]           public string Host { get; set; } = "";
    [JsonPropertyName("agentVersion")]   public string AgentVersion { get; set; } = "";
    [JsonPropertyName("generatedAtUtc")] public DateTimeOffset GeneratedAtUtc { get; set; }
    [JsonPropertyName("clientCount")]    public int ClientCount { get; set; }
    [JsonPropertyName("clients")]        public List<ClientStatus> Clients { get; set; } = new();
}

// ── Run archive: one finished (or in-progress) play session per client ──────

/// <summary>One AC client session — login→exit — with its final/last stats. The agent captures these
/// as clients exit (or restart), so the player can review how past runs did. Schema "rynthcore.runs/1".</summary>
internal sealed class RunRecord
{
    /// <summary>Stable per-session id: "&lt;pid&gt;-&lt;startUnixSec&gt;".</summary>
    [JsonPropertyName("runId")]            public string RunId { get; set; } = "";
    [JsonPropertyName("pid")]              public int Pid { get; set; }
    [JsonPropertyName("account")]          public string Account { get; set; } = "";
    [JsonPropertyName("character")]        public string Character { get; set; } = "";
    [JsonPropertyName("server")]           public string Server { get; set; } = "";
    [JsonPropertyName("startUtc")]         public DateTimeOffset StartUtc { get; set; }
    /// <summary>Null while the run is still in progress.</summary>
    [JsonPropertyName("endUtc")]           public DateTimeOffset? EndUtc { get; set; }
    [JsonPropertyName("durationSec")]      public long DurationSec { get; set; }
    [JsonPropertyName("kills")]            public int Kills { get; set; }
    [JsonPropertyName("killsPerHour")]     public double KillsPerHour { get; set; }
    [JsonPropertyName("xp")]               public long Xp { get; set; }
    [JsonPropertyName("xpPerHour")]        public double XpPerHour { get; set; }
    [JsonPropertyName("luminancePerHour")] public double LuminancePerHour { get; set; }
    /// <summary>Deaths during this session (not all-time).</summary>
    [JsonPropertyName("deaths")]           public int Deaths { get; set; }
    [JsonPropertyName("vitaePct")]         public double VitaePct { get; set; }
    [JsonPropertyName("area")]             public string Area { get; set; } = "";
    /// <summary>True for the live, not-yet-finished run (shown at the top, updates each cycle).</summary>
    [JsonPropertyName("ongoing")]          public bool Ongoing { get; set; }
}

/// <summary>The /runs payload: finished runs (newest first), with any in-progress run marked ongoing.</summary>
internal sealed class RunsPayload
{
    [JsonPropertyName("schema")]         public string Schema { get; set; } = "rynthcore.runs/1";
    [JsonPropertyName("host")]           public string Host { get; set; } = "";
    [JsonPropertyName("generatedAtUtc")] public DateTimeOffset GeneratedAtUtc { get; set; }
    [JsonPropertyName("count")]          public int Count { get; set; }
    [JsonPropertyName("runs")]           public List<RunRecord> Runs { get; set; } = new();
}

/// <summary>A remote-control command the agent writes for the plugin to poll + apply.</summary>
internal sealed class CommandFile
{
    [JsonPropertyName("schema")] public string Schema { get; set; } = "rynthcore.command/1";
    [JsonPropertyName("pid")]    public int Pid { get; set; }
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("value")]  public string Value { get; set; } = "";
    [JsonPropertyName("ts")]     public DateTimeOffset Ts { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(StatusFileModel))]
[JsonSerializable(typeof(AggregatePayload))]
[JsonSerializable(typeof(AgentConfig))]
[JsonSerializable(typeof(CommandFile))]
[JsonSerializable(typeof(RunsPayload))]
internal sealed partial class AgentJsonContext : JsonSerializerContext { }
