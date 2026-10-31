// ============================================================================
//  rynth-headless - RunConfig.cs
//  `rynth-headless run --config bots.json`: several characters in one process.
//
//  {
//    "host": "127.0.0.1", "port": 9000,
//    "suiteDir": "D:\\Bots\\suite",     // RYNTHSUITE_DIR for the plugins (left out = %LOCALAPPDATA%\RynthCore\headless)
//    "statusDir": "D:\\Bots\\status",   // per-character status files ("" = off; left out = <RynthCore>\Logs\status)
//    "plugins": [ "C:\\...\\RynthCore.Plugin.RynthAi.dll" ],   // loaded for every character
//    "sessions": [
//      { "launcherAccount": "myaccount", "character": "My Character",
//        "commands": [ "/ra start" ], "endCommands": [ "/ra stop" ],
//        "staySeconds": 0, "reconnect": true }
//    ]
//  }
//  Each session needs "launcherAccount" (password saved in the RynthCore launcher for that
//  account on host:port) or "account" + "passwordEnv" (an environment variable's name).
//  Passwords are never read from the config file. staySeconds 0 = until stopped (Ctrl+C).
// ============================================================================

using System.Text.Json;
using System.Text.Json.Serialization;

namespace RynthCore.Headless.Cli;

internal sealed class RunConfig
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 9000;
    /// <summary>The server name status files report (the launcher's profile name); empty = the world name.</summary>
    public string ServerName { get; set; } = "";
    public string SuiteDir { get; set; } = HeadlessDefaults.SuiteDir;
    public string StatusDir { get; set; } = HeadlessDefaults.StatusDir;
    public List<string> Plugins { get; set; } = new();
    public List<SessionConfig> Sessions { get; set; } = new();

    public static RunConfig Load(string path)
    {
        var cfg = JsonSerializer.Deserialize<RunConfig>(File.ReadAllText(path), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        }) ?? throw new InvalidDataException("empty config");
        if (cfg.Sessions.Count == 0) throw new InvalidDataException("config: no sessions");
        foreach (SessionConfig s in cfg.Sessions)
        {
            // A session's own server settings win; the top-level ones are the default.
            s.Host = string.IsNullOrEmpty(s.Host) ? cfg.Host : s.Host;
            s.Port ??= cfg.Port;
            s.ServerName = string.IsNullOrEmpty(s.ServerName) ? cfg.ServerName : s.ServerName;
            if (s.Host.Length == 0) throw new InvalidDataException($"config: session {s.Account} has no host (and there is no top-level host)");
            if (s.Account.Length == 0) s.Account = s.LauncherAccount;
            if (s.Account.Length == 0) throw new InvalidDataException("config: a session has neither launcherAccount nor account");
            if (s.LauncherAccount.Length == 0 && s.LauncherProfileId.Length == 0 && s.PasswordEnv.Length == 0)
                throw new InvalidDataException($"config: session {s.Account} needs launcherProfileId, launcherAccount or passwordEnv");
        }
        var dup = cfg.Sessions.GroupBy(s => $"{s.Account}@{s.Host}:{s.Port}", StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dup != null) throw new InvalidDataException($"config: account {dup.Key} appears twice (ACE allows one character in the world per account)");
        return cfg;
    }
}

internal sealed class SessionConfig
{
    public string LauncherAccount { get; set; } = "";
    /// <summary>This character's server, when it isn't the top-level one (one process can serve several).</summary>
    public string Host { get; set; } = "";
    public int? Port { get; set; }
    public string ServerName { get; set; } = "";
    [JsonIgnore] public int ServerPort => Port ?? 9000;
    /// <summary>The launcher's account profile id (its saved password); what the launcher itself writes.</summary>
    public string LauncherProfileId { get; set; } = "";
    public string Account { get; set; } = "";
    public string PasswordEnv { get; set; } = "";
    public string? Character { get; set; }
    public List<string> Commands { get; set; } = new();
    public List<string> EndCommands { get; set; } = new();
    public List<string>? Plugins { get; set; }
    public int StaySeconds { get; set; }
    public bool Reconnect { get; set; } = true;

    [JsonIgnore] public string Label => Character ?? Account;
}
