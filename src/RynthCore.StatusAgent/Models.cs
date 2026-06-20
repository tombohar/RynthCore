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
    [JsonPropertyName("vitaePct")]          public double VitaePct { get; set; }
    [JsonPropertyName("xpPerHour")]         public double XpPerHour { get; set; }
    [JsonPropertyName("luminancePerHour")]  public double LuminancePerHour { get; set; }
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
    [JsonPropertyName("targetLabel")]       public string TargetLabel { get; set; } = "";
    [JsonPropertyName("playerHealth")]      public uint PlayerHealth { get; set; }
    [JsonPropertyName("playerMaxHealth")]   public uint PlayerMaxHealth { get; set; }
    [JsonPropertyName("playerStamina")]     public uint PlayerStamina { get; set; }
    [JsonPropertyName("playerMaxStamina")]  public uint PlayerMaxStamina { get; set; }
    [JsonPropertyName("playerMana")]        public uint PlayerMana { get; set; }
    [JsonPropertyName("playerMaxMana")]     public uint PlayerMaxMana { get; set; }
    [JsonPropertyName("killsPerHour")]      public double KillsPerHour { get; set; }
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
    [JsonPropertyName("player")]            public Vitals? Player { get; set; }

    [JsonPropertyName("queueDropped")]      public long QueueDropped { get; set; }
    [JsonPropertyName("reconciles")]        public long Reconciles { get; set; }
    [JsonPropertyName("forceClears")]       public long ForceClears { get; set; }

    [JsonPropertyName("deaths")]            public int Deaths { get; set; }
    [JsonPropertyName("vitaePct")]          public double VitaePct { get; set; }
    [JsonPropertyName("killsPerHour")]      public double KillsPerHour { get; set; }
    [JsonPropertyName("xpPerHour")]         public double XpPerHour { get; set; }
    [JsonPropertyName("luminancePerHour")]  public double LuminancePerHour { get; set; }
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

[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(StatusFileModel))]
[JsonSerializable(typeof(AggregatePayload))]
[JsonSerializable(typeof(AgentConfig))]
internal sealed partial class AgentJsonContext : JsonSerializerContext { }
