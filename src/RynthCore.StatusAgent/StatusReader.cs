using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RynthCore.StatusAgent;

/// <summary>
/// Builds the per-client status list from the engine's local status files
/// (rich) and, as a fallback, the per-client heartbeat logs (basic).
/// </summary>
internal static partial class StatusReader
{
    [GeneratedRegex(@"^RynthCore\.(\d+)\.log$", RegexOptions.IgnoreCase)]
    private static partial Regex LogNameRegex();

    [GeneratedRegex(@"hb #\d+ up=(\d+)s fps=(-?\d+) plug=(-?\d+)/s ws=(\d+)MB login=(\d+)")]
    private static partial Regex HeartbeatRegex();

    public static List<ClientStatus> ReadAll(AgentConfig cfg)
    {
        var clients = new List<ClientStatus>();
        var seenPids = new HashSet<int>();

        ReadStatusFiles(cfg, clients, seenPids);

        if (cfg.UseHeartbeatLogFallback)
            ReadHeartbeatLogs(cfg, clients, seenPids);

        clients.Sort((a, b) => a.Pid.CompareTo(b.Pid));
        return clients;
    }

    // ── Rich source: per-client status JSON files ───────────────────────────

    private static void ReadStatusFiles(AgentConfig cfg, List<ClientStatus> clients, HashSet<int> seenPids)
    {
        string dir = cfg.StatusDirectory;
        if (!Directory.Exists(dir))
            return;

        foreach (string path in SafeEnumerate(dir, "RynthCore.*.status.json"))
        {
            int pid = ParsePidFromName(Path.GetFileName(path), ".status.json");
            if (pid <= 0) continue;

            StatusFileModel? model;
            try
            {
                model = JsonSerializer.Deserialize(File.ReadAllText(path), AgentJsonContext.Default.StatusFileModel);
            }
            catch (Exception ex)
            {
                AgentLog.Debug($"status file '{path}' unreadable: {ex.Message}");
                continue;
            }
            if (model == null) continue;

            double ageSec = AgeSeconds(model.Ts, path);
            bool alive = IsAcClientAlive(pid);

            // Dead client: report it once (so the phone shows the stop), then
            // retire the stale file so it doesn't linger forever.
            if (!alive && ageSec > cfg.DropDeadAfterSeconds)
            {
                try { File.Delete(path); } catch { }
                continue;
            }

            bool stale = alive && ageSec > cfg.StaleAfterSeconds;
            (string state, bool healthy) = Classify(
                alive, stale, model.InWorld, model.Fps,
                hasBot: model.Bot != null,
                macroRunning: model.Bot?.MacroRunning ?? false,
                pps: model.PluginTicksPerSec);

            var cs = new ClientStatus
            {
                Pid = pid,
                Host = string.IsNullOrEmpty(model.Host) ? cfg.EffectiveHost : model.Host,
                Account = model.Account,
                Character = model.Character,
                Server = model.Server,
                State = state,
                Healthy = healthy,
                AgeSec = Math.Round(ageSec, 1),
                Source = "status-file",
                UptimeSec = model.UptimeSec,
                Fps = model.Fps,
                PluginTicksPerSec = model.PluginTicksPerSec,
                WorkingSetMB = model.WorkingSetMB,
                InWorld = model.InWorld,
                QueueDropped = model.QueueDropped,
                Reconciles = model.Reconciles,
                ForceClears = model.ForceClears,
            };

            if (model.Bot is { } bot)
            {
                cs.MacroRunning = bot.MacroRunning;
                cs.CurrentState = bot.CurrentState;
                cs.BotAction = bot.BotAction;
                cs.Profile = bot.SelectedProfile;
                cs.NavProfile = bot.CurrentNavName;
                cs.LootProfile = bot.CurrentLootName;
                cs.MetaProfile = bot.CurrentMetaName;
                cs.Target = bot.TargetLabel;
                cs.Player = new Vitals
                {
                    Hp = bot.PlayerHealth, MaxHp = bot.PlayerMaxHealth,
                    St = bot.PlayerStamina, MaxSt = bot.PlayerMaxStamina,
                    Mn = bot.PlayerMana, MaxMn = bot.PlayerMaxMana,
                };
                cs.KillsPerHour = bot.KillsPerHour;   // bot-derived (kill counter)
            }
            // Engine-written player stats live at the top level of the status file, not in bot.
            cs.Deaths = model.Deaths;
            cs.VitaePct = model.VitaePct;
            cs.XpPerHour = model.XpPerHour;
            cs.LuminancePerHour = model.LuminancePerHour;

            clients.Add(cs);
            seenPids.Add(pid);
        }
    }

    // ── Basic fallback: tail each per-client heartbeat log ──────────────────

    private static void ReadHeartbeatLogs(AgentConfig cfg, List<ClientStatus> clients, HashSet<int> seenPids)
    {
        string dir = cfg.LogDirectory;
        if (!Directory.Exists(dir))
            return;

        foreach (string path in SafeEnumerate(dir, "RynthCore.*.log"))
        {
            Match m = LogNameRegex().Match(Path.GetFileName(path));
            if (!m.Success) continue;
            if (!int.TryParse(m.Groups[1].Value, out int pid)) continue;
            if (seenPids.Contains(pid)) continue;       // status file already covered it
            if (!IsAcClientAlive(pid)) continue;        // only living clients via the log path

            string? hb = TailFindHeartbeat(path);
            if (hb == null) continue;
            Match h = HeartbeatRegex().Match(hb);
            if (!h.Success) continue;

            long uptime = ParseLong(h.Groups[1].Value);
            int fps = ParseInt(h.Groups[2].Value);
            int pps = ParseInt(h.Groups[3].Value);
            long ws = ParseLong(h.Groups[4].Value);
            bool inWorld = h.Groups[5].Value == "1";

            double ageSec = AgeSeconds(default, path);  // log mtime
            bool stale = ageSec > cfg.StaleAfterSeconds;
            (string state, bool healthy) = Classify(
                pidAlive: true, stale: stale, inWorld: inWorld, fps: fps,
                hasBot: false, macroRunning: false, pps: pps);

            clients.Add(new ClientStatus
            {
                Pid = pid,
                Host = cfg.EffectiveHost,
                State = state,
                Healthy = healthy,
                AgeSec = Math.Round(ageSec, 1),
                Source = "heartbeat-log",
                UptimeSec = uptime,
                Fps = fps,
                PluginTicksPerSec = pps,
                WorkingSetMB = ws,
                InWorld = inWorld,
            });
            seenPids.Add(pid);
        }
    }

    /// <summary>
    /// State machine shared by both sources. "running" = in-world and rendering
    /// but bot/macro state unknown (heartbeat-log path); the richer status-file
    /// path resolves further into idle / botting / wedged.
    /// </summary>
    public static (string state, bool healthy) Classify(
        bool pidAlive, bool stale, bool inWorld, int fps, bool hasBot, bool macroRunning, int pps)
    {
        if (!pidAlive)        return ("dead", false);
        if (stale)            return ("hung", false);   // process up but snapshot frozen
        if (!inWorld)         return ("loading", true);  // login / char-select
        if (fps <= 0)         return ("hung", false);    // render thread dead
        if (!hasBot)          return ("running", true);  // in-world, botting unknown
        if (!macroRunning)    return ("idle", true);
        if (pps <= 0)         return ("wedged", false);  // macro on but pump stalled
        return ("botting", true);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static IEnumerable<string> SafeEnumerate(string dir, string pattern)
    {
        try { return Directory.GetFiles(dir, pattern); }
        catch { return Array.Empty<string>(); }
    }

    private static int ParsePidFromName(string fileName, string suffix)
    {
        // RynthCore.<pid>.status.json
        if (!fileName.StartsWith("RynthCore.", StringComparison.OrdinalIgnoreCase)) return -1;
        if (!fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return -1;
        string mid = fileName.Substring("RynthCore.".Length, fileName.Length - "RynthCore.".Length - suffix.Length);
        return int.TryParse(mid, out int pid) ? pid : -1;
    }

    private static double AgeSeconds(DateTimeOffset ts, string path)
    {
        if (ts > DateTimeOffset.UnixEpoch)
            return Math.Max(0, (DateTimeOffset.UtcNow - ts).TotalSeconds);
        try { return Math.Max(0, (DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalSeconds); }
        catch { return double.MaxValue; }
    }

    private static bool IsAcClientAlive(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            // Guard against PID reuse: a recycled PID owned by some other process
            // must not read as a live AC client.
            return p.ProcessName.Contains("acclient", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }   // ArgumentException = no such process
    }

    private static string? TailFindHeartbeat(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int take = (int)Math.Min(fs.Length, 16 * 1024);
            fs.Seek(-take, SeekOrigin.End);
            byte[] buf = new byte[take];
            int read = fs.Read(buf, 0, take);
            string tail = Encoding.UTF8.GetString(buf, 0, read);
            string[] lines = tail.Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                if (lines[i].Contains("hb #", StringComparison.Ordinal))
                    return lines[i];
            }
        }
        catch { }
        return null;
    }

    private static int ParseInt(string s) => int.TryParse(s, out int v) ? v : 0;
    private static long ParseLong(string s) => long.TryParse(s, out long v) ? v : 0;
}
