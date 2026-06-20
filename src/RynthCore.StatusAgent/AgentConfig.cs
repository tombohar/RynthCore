using System.Text.Json;
using System.Text.Json.Serialization;

namespace RynthCore.StatusAgent;

/// <summary>
/// Agent configuration, loaded from %APPDATA%\RynthCore\statusagent.json.
/// The agent writes a commented template here on first run and never sends
/// anything until <see cref="Endpoint"/> is set to a non-empty URL.
/// </summary>
internal sealed class AgentConfig
{
    /// <summary>Backend URL the agent POSTs the status payload to. EMPTY = the
    /// agent never opens a network connection (local print-only mode).</summary>
    [JsonPropertyName("Endpoint")] public string Endpoint { get; set; } = "";

    /// <summary>Optional auth header sent with each POST (e.g. "Authorization").</summary>
    [JsonPropertyName("AuthHeaderName")] public string AuthHeaderName { get; set; } = "Authorization";

    /// <summary>Value for <see cref="AuthHeaderName"/> (e.g. "Bearer &lt;token&gt;"). Empty = no header.</summary>
    [JsonPropertyName("AuthHeaderValue")] public string AuthHeaderValue { get; set; } = "";

    /// <summary>Seconds between status posts.</summary>
    [JsonPropertyName("IntervalSeconds")] public int IntervalSeconds { get; set; } = 5;

    /// <summary>HTTP request timeout in seconds.</summary>
    [JsonPropertyName("TimeoutSeconds")] public int TimeoutSeconds { get; set; } = 15;

    /// <summary>Directory holding the engine's per-client status files.</summary>
    [JsonPropertyName("StatusDirectory")] public string StatusDirectory { get; set; } = @"C:\Games\RynthCore\Logs\status";

    /// <summary>Directory holding per-client RynthCore.&lt;pid&gt;.log files (heartbeat fallback).</summary>
    [JsonPropertyName("LogDirectory")] public string LogDirectory { get; set; } = @"C:\Games\RynthCore\Logs";

    /// <summary>When true and no status file exists for a running client, derive
    /// basic status from that client's heartbeat log line. Lets the agent work
    /// even if EnableStatusExport is off (you just don't get bot/macro detail).</summary>
    [JsonPropertyName("UseHeartbeatLogFallback")] public bool UseHeartbeatLogFallback { get; set; } = true;

    /// <summary>A live-PID client whose snapshot is older than this is reported "hung".</summary>
    [JsonPropertyName("StaleAfterSeconds")] public int StaleAfterSeconds { get; set; } = 12;

    /// <summary>A dead-PID status file older than this is reported once then deleted.</summary>
    [JsonPropertyName("DropDeadAfterSeconds")] public int DropDeadAfterSeconds { get; set; } = 300;

    /// <summary>Label for this machine in the payload. Empty = use the machine name.</summary>
    [JsonPropertyName("Host")] public string Host { get; set; } = "";

    public static string DefaultConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RynthCore", "statusagent.json");

    // Indented, trim-safe context just for writing the human-edited template.
    private static readonly AgentJsonContext IndentedContext =
        new(new JsonSerializerOptions { WriteIndented = true });

    /// <summary>
    /// Load config from <paramref name="path"/>, creating a default template if
    /// it doesn't exist. <paramref name="created"/> is set true when a fresh
    /// template was written, so the caller can tell the user where it lives.
    /// </summary>
    public static AgentConfig LoadOrCreate(string path, out bool created)
    {
        created = false;
        try
        {
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                AgentConfig? cfg = JsonSerializer.Deserialize(json, AgentJsonContext.Default.AgentConfig);
                if (cfg != null) return cfg;
            }
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"Failed to read config '{path}' ({ex.Message}); using defaults.");
            return new AgentConfig();
        }

        var fresh = new AgentConfig();
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(fresh, IndentedContext.AgentConfig));
            created = true;
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"Could not write template config '{path}' ({ex.Message}).");
        }
        return fresh;
    }

    public string EffectiveHost =>
        string.IsNullOrWhiteSpace(Host) ? SafeMachineName() : Host;

    private static string SafeMachineName()
    {
        try { return Environment.MachineName; }
        catch { return "unknown"; }
    }
}
