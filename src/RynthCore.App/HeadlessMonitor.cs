using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace RynthCore.App;

// ============================================================================
//  The launcher's Headless window (RynthCore.App.Avalonia\HeadlessWindow), without the UI.
//  BCL only, so tools\HeadlessWindowTests compiles it as it is.
//
//  What a headless character tells the world is in files (src\RynthCore.Headless\Host\Status\
//  StatusExporter.cs is the writer and the authority on the schema):
//    <statusDir>\RynthCore.<id>.status.json   every 1 s: the engine's "rynthcore.client-status/1"
//        fields, RynthAi's snapshot in "bot", "opos", and headless: true, hostPid, hostProcess.
//        <id> is a synthetic per-character id (odd, above a billion), not a process id.
//    <statusDir>\commands\RynthCore.<id>.<ticks>-<guid>.cmd.json   the inbox, drained every
//        200 ms, oldest first, each file deleted before it runs (the StatusAgent's format).
//    <suiteDir>\headless-<account>.log   the character's own log (Bot.cs).
//  The window only reads these and writes command files; it never opens a socket or a process.
// ============================================================================

/// <summary>A status dot: what one headless character is doing, as the status file says.</summary>
internal enum HeadlessActivity
{
    /// <summary>In the world with the macro running.</summary>
    Botting,
    /// <summary>In the world, macro off.</summary>
    Idle,
    /// <summary>In the world with no health left.</summary>
    Dead,
    /// <summary>Connecting, at the character list, or waiting to reconnect (inWorld false).</summary>
    Loading,
    /// <summary>The status file stopped changing (over <see cref="HeadlessStatusRules.StaleAfter"/>).</summary>
    Stale,
}

internal enum HeadlessFreshness { Fresh, Stale, Gone }

internal enum HeadlessParseResult { Ok, NotHeadless, Broken }

/// <summary>One line of bot.recentChat: {"t": text, "c": AC chat type}.</summary>
internal readonly record struct HeadlessChatLine(string Text, int Type);

/// <summary>What the window shows of one headless character, read from its status file.</summary>
internal sealed class HeadlessCharacterStatus
{
    public int Id { get; init; }
    public string Character { get; init; } = string.Empty;
    public string Server { get; init; } = string.Empty;
    /// <summary>Used only to find the character's log file (headless-&lt;account&gt;.log); never shown.</summary>
    public string Account { get; init; } = string.Empty;
    /// <summary>The file's "ts" (UTC); the file's write time when "ts" is missing or unreadable.</summary>
    public DateTime TimestampUtc { get; init; }
    public bool InWorld { get; init; }
    public int HostPid { get; init; }
    public string HostProcess { get; init; } = string.Empty;
    public long UptimeSec { get; init; }

    public double VitaePct { get; init; }
    public double XpPerHour { get; init; }
    public long XpSession { get; init; }
    public int DeathsSession { get; init; }
    public double BurdenPct { get; init; }

    public string Landblock { get; init; } = string.Empty;
    public bool Indoor { get; init; }
    public string Cell { get; init; } = string.Empty;
    /// <summary>World position from "opos" (192 units a landblock); null without one.</summary>
    public double? WorldX { get; init; }
    public double? WorldY { get; init; }
    public double? Z { get; init; }
    public double? Heading { get; init; }

    /// <summary>False when "bot" is null (not in the world, or no RynthAi loaded).</summary>
    public bool HasBot { get; init; }
    public bool MacroRunning { get; init; }
    public string BotAction { get; init; } = string.Empty;
    public string CurrentState { get; init; } = string.Empty;
    public string MetaName { get; init; } = string.Empty;
    public string NavName { get; init; } = string.Empty;
    public long TargetId { get; init; }
    public string TargetLabel { get; init; } = string.Empty;
    public double TargetHealthPercent { get; init; }
    public long Health { get; init; }
    public long MaxHealth { get; init; }
    public long Stamina { get; init; }
    public long MaxStamina { get; init; }
    public long Mana { get; init; }
    public long MaxMana { get; init; }
    public long SessionKills { get; init; }
    public double KillsPerHour { get; init; }
    public IReadOnlyList<HeadlessChatLine> RecentChat { get; init; } = Array.Empty<HeadlessChatLine>();

    public bool HasTarget => TargetId != 0;
    public bool HasVitals => HasBot && MaxHealth > 0;
}

/// <summary>Parses a status file into <see cref="HeadlessCharacterStatus"/>.</summary>
internal static class HeadlessStatusParser
{
    /// <summary>
    /// Parses <paramref name="json"/> (one status file). False for anything that isn't a headless
    /// character's status (a game client's own status file, a half-written or empty file).
    /// <paramref name="fileWriteUtc"/> stands in for a missing "ts".
    /// </summary>
    public static bool TryParse(string json, DateTime fileWriteUtc, out HeadlessCharacterStatus status) =>
        Parse(json, fileWriteUtc, out status) == HeadlessParseResult.Ok;

    /// <summary>
    /// <see cref="TryParse"/> telling a complete file that isn't a headless character's
    /// (<see cref="HeadlessParseResult.NotHeadless"/>) from one that can't be read as JSON now: empty,
    /// truncated, caught mid-replace (<see cref="HeadlessParseResult.Broken"/>).
    /// </summary>
    public static HeadlessParseResult Parse(string json, DateTime fileWriteUtc, out HeadlessCharacterStatus status)
    {
        status = null!;
        if (string.IsNullOrWhiteSpace(json))
            return HeadlessParseResult.Broken;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !Bool(r, "headless"))
                return HeadlessParseResult.NotHeadless;
            int id = (int)Long(r, "pid");
            if (id <= 0)
                return HeadlessParseResult.NotHeadless;

            DateTime ts = DateTime.TryParse(Str(r, "ts"), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime t)
                ? DateTime.SpecifyKind(t, DateTimeKind.Utc)
                : fileWriteUtc;

            r.TryGetProperty("opos", out JsonElement opos);
            bool hasOpos = opos.ValueKind == JsonValueKind.Object;
            r.TryGetProperty("bot", out JsonElement bot);
            bool hasBot = bot.ValueKind == JsonValueKind.Object;

            status = new HeadlessCharacterStatus
            {
                Id = id,
                Character = Str(r, "character"),
                Server = Str(r, "server"),
                Account = Str(r, "account"),
                TimestampUtc = ts,
                InWorld = Bool(r, "inWorld"),
                HostPid = (int)Long(r, "hostPid"),
                HostProcess = Str(r, "hostProcess"),
                UptimeSec = Long(r, "uptimeSec"),
                VitaePct = Dbl(r, "vitaePct"),
                XpPerHour = Dbl(r, "xpPerHour"),
                XpSession = Long(r, "xpSession"),
                DeathsSession = (int)Long(r, "deathsSession"),
                BurdenPct = Dbl(r, "burdenPct"),
                Landblock = Str(r, "landblock"),
                Indoor = hasOpos && opos.TryGetProperty("indoor", out _) ? Bool(opos, "indoor") : Bool(r, "indoor"),
                Cell = hasOpos ? Str(opos, "cell") : string.Empty,
                WorldX = hasOpos ? NDbl(opos, "wx") : null,
                WorldY = hasOpos ? NDbl(opos, "wy") : null,
                Z = hasOpos ? NDbl(opos, "z") : null,
                Heading = hasOpos ? NDbl(opos, "heading") : null,
                HasBot = hasBot,
                MacroRunning = hasBot && Bool(bot, "macroRunning"),
                BotAction = hasBot ? Str(bot, "botAction") : string.Empty,
                CurrentState = hasBot ? Str(bot, "currentState") : string.Empty,
                MetaName = hasBot ? Str(bot, "currentMetaName") : string.Empty,
                NavName = hasBot ? Str(bot, "currentNavName") : string.Empty,
                TargetId = hasBot ? Long(bot, "currentTargetId") : 0,
                TargetLabel = hasBot ? Str(bot, "targetLabel") : string.Empty,
                TargetHealthPercent = hasBot ? Dbl(bot, "targetHealthPercent") : 0,
                Health = hasBot ? Long(bot, "playerHealth") : 0,
                MaxHealth = hasBot ? Long(bot, "playerMaxHealth") : 0,
                Stamina = hasBot ? Long(bot, "playerStamina") : 0,
                MaxStamina = hasBot ? Long(bot, "playerMaxStamina") : 0,
                Mana = hasBot ? Long(bot, "playerMana") : 0,
                MaxMana = hasBot ? Long(bot, "playerMaxMana") : 0,
                SessionKills = hasBot ? Long(bot, "sessionKills") : 0,
                KillsPerHour = hasBot ? Dbl(bot, "killsPerHour") : 0,
                RecentChat = hasBot ? Chat(bot) : Array.Empty<HeadlessChatLine>(),
            };
            return HeadlessParseResult.Ok;
        }
        catch (JsonException)
        {
            return HeadlessParseResult.Broken;   // mid-replace or truncated: the caller keeps what it had
        }
        catch (InvalidOperationException)
        {
            return HeadlessParseResult.NotHeadless;   // a field of an unexpected kind at the top
        }
    }

    private static List<HeadlessChatLine> Chat(JsonElement bot)
    {
        var lines = new List<HeadlessChatLine>();
        if (!bot.TryGetProperty("recentChat", out JsonElement chat) || chat.ValueKind != JsonValueKind.Array)
            return lines;
        foreach (JsonElement e in chat.EnumerateArray())
        {
            if (e.ValueKind == JsonValueKind.Object)
                lines.Add(new HeadlessChatLine(Str(e, "t"), (int)Long(e, "c")));
            else if (e.ValueKind == JsonValueKind.String)
                lines.Add(new HeadlessChatLine(e.GetString() ?? string.Empty, 0));
        }
        return lines;
    }

    private static string Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out JsonElement v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? string.Empty,
                JsonValueKind.Number => v.GetRawText(),
                _ => string.Empty,
            }
            : string.Empty;

    private static bool Bool(JsonElement o, string name) =>
        o.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;

    private static double? NDbl(JsonElement o, string name) =>
        o.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) ? d : null;

    private static double Dbl(JsonElement o, string name) => NDbl(o, name) ?? 0;

    private static long Long(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.Number)
            return 0;
        if (v.TryGetInt64(out long l))
            return l;
        return v.TryGetDouble(out double d) && !double.IsNaN(d) ? (long)Math.Clamp(d, long.MinValue, long.MaxValue) : 0;
    }
}

/// <summary>The stale and gone rules, and the status dot.</summary>
internal static class HeadlessStatusRules
{
    /// <summary>The exporter writes every second: older than this, the character isn't ticking.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(10);
    /// <summary>Older than this, the character is gone (the process ended without removing its files).</summary>
    public static readonly TimeSpan GoneAfter = TimeSpan.FromSeconds(30);

    public static TimeSpan Age(HeadlessCharacterStatus s, DateTime nowUtc)
    {
        TimeSpan age = nowUtc - s.TimestampUtc;
        return age < TimeSpan.Zero ? TimeSpan.Zero : age;
    }

    public static HeadlessFreshness Freshness(TimeSpan age) =>
        age > GoneAfter ? HeadlessFreshness.Gone : age > StaleAfter ? HeadlessFreshness.Stale : HeadlessFreshness.Fresh;

    public static HeadlessActivity Activity(HeadlessCharacterStatus s, DateTime nowUtc)
    {
        if (Freshness(Age(s, nowUtc)) != HeadlessFreshness.Fresh)
            return HeadlessActivity.Stale;
        if (!s.InWorld)
            return HeadlessActivity.Loading;
        if (s.HasVitals && s.Health <= 0)
            return HeadlessActivity.Dead;
        return s.MacroRunning ? HeadlessActivity.Botting : HeadlessActivity.Idle;
    }

    public static string ActivityText(HeadlessActivity a) => a switch
    {
        HeadlessActivity.Botting => "botting",
        HeadlessActivity.Idle => "idle",
        HeadlessActivity.Dead => "dead",
        HeadlessActivity.Loading => "not in the world",
        _ => "not answering",
    };
}

/// <summary>
/// Keeps the window's list: the last good status per character. A file that couldn't be read this
/// time (being replaced) keeps its last status; a character whose file is gone, or whose status is
/// older than <see cref="HeadlessStatusRules.GoneAfter"/>, leaves the list.
/// </summary>
internal sealed class HeadlessRoster
{
    private readonly Dictionary<int, HeadlessCharacterStatus> _byId = new();

    public IReadOnlyCollection<HeadlessCharacterStatus> All => _byId.Values;

    public bool TryGet(int id, out HeadlessCharacterStatus status) => _byId.TryGetValue(id, out status!);

    /// <summary>
    /// <paramref name="parsed"/>: the files read this time. <paramref name="unreadable"/>: ids whose
    /// file exists but didn't parse. Returns the list sorted by character name, then id.
    /// </summary>
    public List<HeadlessCharacterStatus> Update(IEnumerable<HeadlessCharacterStatus> parsed, IEnumerable<int> unreadable, DateTime nowUtc)
    {
        var seen = new HashSet<int>();
        foreach (HeadlessCharacterStatus s in parsed)
        {
            seen.Add(s.Id);
            // A file mid-replace can't go back in time: keep the newer status.
            if (!_byId.TryGetValue(s.Id, out HeadlessCharacterStatus? old) || s.TimestampUtc >= old.TimestampUtc)
                _byId[s.Id] = s;
        }
        foreach (int id in unreadable)
            seen.Add(id);
        foreach (int id in _byId.Keys.ToList())
        {
            if (!seen.Contains(id) ||
                HeadlessStatusRules.Freshness(HeadlessStatusRules.Age(_byId[id], nowUtc)) == HeadlessFreshness.Gone)
                _byId.Remove(id);
        }
        return _byId.Values
            .OrderBy(s => s.Character, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Id)
            .ToList();
    }
}

/// <summary>bot.recentChat: the combat spam filter and the lines new since an earlier snapshot.</summary>
internal static class HeadlessChat
{
    /// <summary>How many chat lines the window shows.</summary>
    public const int ShownLines = 30;

    /// <summary>
    /// AC chat types that are pure combat spam (our hits, their hits, evades): hidden (21 and 22).
    /// </summary>
    public static bool IsCombatSpam(int type) => type is 21 or 22;

    /// <summary>The last <paramref name="max"/> lines that aren't combat spam, oldest first.</summary>
    public static List<HeadlessChatLine> Visible(IReadOnlyList<HeadlessChatLine> lines, int max = ShownLines)
    {
        var shown = lines.Where(l => !IsCombatSpam(l.Type)).ToList();
        return shown.Count > max ? shown.GetRange(shown.Count - max, max) : shown;
    }

    /// <summary>
    /// The lines of <paramref name="current"/> after its overlap with the end of
    /// <paramref name="previous"/>. The export keeps the last 60 lines, so a newer list is the older
    /// one shifted left with new lines at the end (observe.py's new_chat). No overlap at all = every
    /// line is new.
    /// </summary>
    public static List<HeadlessChatLine> NewLines(IReadOnlyList<HeadlessChatLine> previous, IReadOnlyList<HeadlessChatLine> current)
    {
        if (previous.Count == 0)
            return current.ToList();
        for (int k = Math.Min(previous.Count, current.Count); k > 0; k--)
        {
            bool match = true;
            for (int i = 0; i < k && match; i++)
                match = previous[previous.Count - k + i] == current[i];
            if (match)
                return current.Skip(k).ToList();
        }
        return current.ToList();
    }

    /// <summary>The first line in <paramref name="lines"/> that isn't combat spam, or null.</summary>
    public static HeadlessChatLine? FirstReply(IEnumerable<HeadlessChatLine> lines)
    {
        foreach (HeadlessChatLine l in lines)
            if (!IsCombatSpam(l.Type) && l.Text.Length > 0)
                return l;
        return null;
    }
}

/// <summary>Map coordinates from a status file's opos world position.</summary>
internal static class HeadlessCoords
{
    /// <summary>
    /// (north/south, east/west) from world units (192 a landblock), north and east positive: the
    /// conversion of the StatusAgent's NavAtlasService.FromWorld and observe.py's map_of.
    /// </summary>
    public static (double Ns, double Ew) FromWorld(double wx, double wy) =>
        ((wy / 24.0 - 1019.5) / 10.0, (wx / 24.0 - 1019.5) / 10.0);

    /// <summary>"42.1N 33.6E" (RynthNet's CharactersModel.CoordsText).</summary>
    public static string Text(double ns, double ew) =>
        Math.Abs(ns).ToString("0.0", CultureInfo.InvariantCulture) + (ns >= 0 ? "N " : "S ") +
        Math.Abs(ew).ToString("0.0", CultureInfo.InvariantCulture) + (ew >= 0 ? "E" : "W");

    /// <summary>
    /// Where the character is: "Outdoors 42.1N 33.6E (landblock A9B3)", "Indoors (landblock 01F5,
    /// cell 01F501BE)", or "unknown".
    /// </summary>
    public static string Where(HeadlessCharacterStatus s)
    {
        string lb = s.Landblock.Length >= 4 ? s.Landblock[^4..] : s.Landblock;
        if (s.Indoor)
        {
            if (lb.Length == 0) return "Indoors";
            return s.Cell.Length > 0 ? $"Indoors (landblock {lb}, cell {s.Cell})" : $"Indoors (landblock {lb})";
        }
        if (s.WorldX is double wx && s.WorldY is double wy)
        {
            var (ns, ew) = FromWorld(wx, wy);
            return lb.Length > 0 ? $"Outdoors {Text(ns, ew)} (landblock {lb})" : $"Outdoors {Text(ns, ew)}";
        }
        return lb.Length > 0 ? $"Outdoors (landblock {lb})" : "unknown";
    }
}

/// <summary>Short numbers and durations for the details panel.</summary>
internal static class HeadlessFormat
{
    /// <summary>"950", "12.3k", "36.8M", "2.4B".</summary>
    public static string Number(double n)
    {
        double a = Math.Abs(n);
        string s = a >= 1e9 ? (n / 1e9).ToString("0.0", CultureInfo.InvariantCulture) + "B"
            : a >= 1e6 ? (n / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + "M"
            : a >= 1e4 ? (n / 1e3).ToString("0.0", CultureInfo.InvariantCulture) + "k"
            : Math.Round(n).ToString("0", CultureInfo.InvariantCulture);
        return s;
    }

    /// <summary>"45s", "12m 05s", "3h 12m", "2d 18h".</summary>
    public static string Duration(long seconds)
    {
        if (seconds < 0) seconds = 0;
        long d = seconds / 86400, h = seconds / 3600 % 24, m = seconds / 60 % 60, s = seconds % 60;
        if (d > 0) return $"{d}d {h}h";
        if (h > 0) return $"{h}h {m:00}m";
        if (m > 0) return $"{m}m {s:00}s";
        return $"{s}s";
    }

    /// <summary>"165 / 230" (no maximum = "-").</summary>
    public static string Vital(long cur, long max) =>
        max <= 0 ? "-" : $"{cur.ToString(CultureInfo.InvariantCulture)} / {max.ToString(CultureInfo.InvariantCulture)}";

    public static double Fraction(long cur, long max) => max <= 0 ? 0 : Math.Clamp((double)cur / max, 0, 1);
}

/// <summary>
/// The command inbox: the file the StatusAgent writes for the phone (LocalStatusServer.HandleCommand,
/// Models.CommandFile), so a headless character takes it the same way (StatusExporter.DrainCommands).
/// </summary>
internal static class HeadlessCommandFile
{
    public const string CommandsFolderName = "commands";
    public const string SendChatAction = "sendChat";
    public const string MacroAction = "macro";

    /// <summary>Chat commands the window's command box sends (RynthAi, RynthNav, meta tools, RynthNet).</summary>
    public static readonly IReadOnlyList<string> AllowedPrefixes = new[] { "/ra", "/rnav", "/mt", "/rn" };

    public const int MaxCommandLength = 400;

    /// <summary>Why <paramref name="text"/> can't be sent (null = it can): one line starting with an allowed prefix.</summary>
    public static string? ChatCommandProblem(string? text)
    {
        string t = (text ?? string.Empty).Trim();
        if (t.Length == 0)
            return "Type a command first, for example /ra pause.";
        if (t.IndexOfAny(new[] { '\r', '\n' }) >= 0)
            return "One command per line.";
        if (t.Length > MaxCommandLength)
            return $"That command is longer than {MaxCommandLength} characters.";
        foreach (string p in AllowedPrefixes)
        {
            if (t.Length == p.Length && t.Equals(p, StringComparison.OrdinalIgnoreCase))
                return null;
            if (t.Length > p.Length && t.StartsWith(p, StringComparison.OrdinalIgnoreCase) && t[p.Length] == ' ')
                return null;
        }
        return "Only " + string.Join(", ", AllowedPrefixes) + " commands can be sent from here.";
    }

    /// <summary>RynthCore.&lt;id&gt;.&lt;ticks, 19 digits&gt;-&lt;guid&gt;.cmd.json: sorts in submit order.</summary>
    public static string FileName(int id, DateTime nowUtc, Guid guid) =>
        $"RynthCore.{id.ToString(CultureInfo.InvariantCulture)}.{nowUtc.Ticks.ToString("D19", CultureInfo.InvariantCulture)}-{guid:N}.cmd.json";

    /// <summary>{"schema":"rynthcore.command/1","pid":id,"action":..,"value":..,"ts":..}.</summary>
    public static string Json(int id, string action, string value, DateTime nowUtc)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("schema", "rynthcore.command/1");
            w.WriteNumber("pid", id);
            w.WriteString("action", action);
            w.WriteString("value", value);
            w.WriteString("ts", new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)));
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Writes one command for character <paramref name="id"/> into <paramref name="commandsDir"/>:
    /// a temp file (".tmp", which the drain's *.cmd.json pattern never matches), then a rename, so
    /// the character never reads half a command. Returns the command file's path.
    /// </summary>
    public static string Write(string commandsDir, int id, string action, string value, DateTime nowUtc)
    {
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id));
        if (string.IsNullOrWhiteSpace(action)) throw new ArgumentException("No action.", nameof(action));
        Directory.CreateDirectory(commandsDir);
        string dest = Path.Combine(commandsDir, FileName(id, nowUtc, Guid.NewGuid()));
        string tmp = dest + ".tmp";
        File.WriteAllText(tmp, Json(id, action, value ?? string.Empty, nowUtc), new UTF8Encoding(false));
        File.Move(tmp, dest, overwrite: false);
        return dest;
    }
}

/// <summary>Finds and reads the status files.</summary>
internal static class HeadlessStatusScanner
{
    /// <summary>
    /// Reads every headless status file in <paramref name="statusDir"/>. Files not written for a
    /// minute are skipped without being opened (the game clients' old ones stay in the folder).
    /// <paramref name="unreadable"/> gets the ids whose file couldn't be read or parsed this time.
    /// </summary>
    public static List<HeadlessCharacterStatus> Scan(string statusDir, DateTime nowUtc, out List<int> unreadable)
    {
        unreadable = new List<int>();
        var found = new List<HeadlessCharacterStatus>();
        string[] files;
        try
        {
            if (!Directory.Exists(statusDir))
                return found;
            files = Directory.GetFiles(statusDir, "RynthCore.*.status.json");
        }
        catch (IOException) { return found; }
        catch (UnauthorizedAccessException) { return found; }

        foreach (string path in files)
        {
            if (!TryIdOf(path, out int id))
                continue;
            DateTime written;
            try { written = File.GetLastWriteTimeUtc(path); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            if (nowUtc - written > HeadlessStatusRules.GoneAfter + TimeSpan.FromSeconds(30))
                continue;
            string? text = ReadShared(path);
            if (text == null)
            {
                unreadable.Add(id);
                continue;
            }
            switch (HeadlessStatusParser.Parse(text, written, out HeadlessCharacterStatus s))
            {
                case HeadlessParseResult.Ok:
                    found.Add(s);
                    break;
                case HeadlessParseResult.Broken:
                    // Caught mid-write. The list keeps the character's last status if it had one
                    // (HeadlessRoster); a game client's file in this state is simply not listed.
                    unreadable.Add(id);
                    break;
            }
        }
        return found;
    }

    /// <summary>The &lt;id&gt; of RynthCore.&lt;id&gt;.status.json.</summary>
    public static bool TryIdOf(string path, out int id)
    {
        id = 0;
        string name = Path.GetFileName(path);
        const string prefix = "RynthCore.", suffix = ".status.json";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return false;
        string mid = name.Substring(prefix.Length, name.Length - prefix.Length - suffix.Length);
        return int.TryParse(mid, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
    }

    /// <summary>The whole file, read while others may write or replace it; null when it can't be read now.</summary>
    public static string? ReadShared(string path)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch (FileNotFoundException) { return null; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }
}

/// <summary>A headless character's own log: where it is and its last lines.</summary>
internal static class HeadlessLogs
{
    public const int ShownLines = 50;

    /// <summary>
    /// The suite folder rynth-headless 2026.10.31.1 and later use when the run config names none
    /// (HeadlessDefaults.SuiteDir in the CLI).
    /// </summary>
    public static string DefaultSuiteDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RynthCore", "headless");

    /// <summary>The builds before 2026.10.31.1 used the development sandbox (docs\HEADLESS.md, P3).</summary>
    public const string LegacySuiteDir = @"C:\Projects\headless\sandbox\suite";

    /// <summary>headless-&lt;account&gt;.log, the account's letters and digits kept (Bot.cs's Safe).</summary>
    public static string LogFileName(string account) =>
        "headless-" + string.Concat((account ?? string.Empty).Select(c => char.IsLetterOrDigit(c) ? c : '_')) + ".log";

    /// <summary>
    /// The folders a log of <paramref name="account"/> may be in, most likely first: the suiteDir of
    /// every launcher run config (%APPDATA%\RynthCore\headless\launch_*.json) with a session for the
    /// account (no suiteDir = the default), then <paramref name="configuredSuiteDir"/>
    /// (AppSettings.HeadlessSuiteDir), the default and the legacy folder.
    /// </summary>
    public static List<string> CandidateSuiteDirs(string account, string? launchConfigDir, string? configuredSuiteDir,
        string defaultSuiteDir, string? legacySuiteDir = LegacySuiteDir)
    {
        var dirs = new List<string>();
        void Add(string? d)
        {
            d = (d ?? string.Empty).Trim().Trim('"');
            if (d.Length > 0 && !dirs.Contains(d, StringComparer.OrdinalIgnoreCase))
                dirs.Add(Environment.ExpandEnvironmentVariables(d));
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(launchConfigDir) && Directory.Exists(launchConfigDir) && account.Length > 0)
            {
                foreach (string file in Directory.GetFiles(launchConfigDir, "launch_*.json").OrderBy(f => f, StringComparer.Ordinal))
                {
                    string? text = HeadlessStatusScanner.ReadShared(file);
                    if (text == null) continue;
                    try
                    {
                        using JsonDocument doc = JsonDocument.Parse(text);
                        JsonElement r = doc.RootElement;
                        if (!r.TryGetProperty("sessions", out JsonElement sessions) || sessions.ValueKind != JsonValueKind.Array)
                            continue;
                        bool mine = sessions.EnumerateArray().Any(s =>
                            s.ValueKind == JsonValueKind.Object &&
                            (Matches(s, "account", account) || Matches(s, "launcherAccount", account)));
                        if (!mine) continue;
                        Add(r.TryGetProperty("suiteDir", out JsonElement sd) && sd.ValueKind == JsonValueKind.String
                            ? sd.GetString()
                            : defaultSuiteDir);
                    }
                    catch (JsonException) { }
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        Add(configuredSuiteDir);
        Add(defaultSuiteDir);
        Add(legacySuiteDir);
        return dirs;
    }

    private static bool Matches(JsonElement o, string name, string account) =>
        o.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String &&
        string.Equals(v.GetString(), account, StringComparison.OrdinalIgnoreCase);

    /// <summary>The first existing log of <paramref name="account"/> in <paramref name="dirs"/>, or null.</summary>
    public static string? Find(string account, IEnumerable<string> dirs)
    {
        string name = LogFileName(account);
        foreach (string d in dirs)
        {
            try
            {
                string p = Path.Combine(d, name);
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>
    /// The last <paramref name="maxLines"/> lines of <paramref name="path"/>, reading at most the
    /// last <paramref name="maxBytes"/> (the logs grow to 100 MB and more). Opened shared, so the
    /// writing process is never disturbed. Null when it can't be read.
    /// </summary>
    public static List<string>? Tail(string path, int maxLines = ShownLines, int maxBytes = 128 * 1024)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, fs.Length - maxBytes);
            fs.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[fs.Length - start];
            int read = 0;
            while (read < buffer.Length)
            {
                int n = fs.Read(buffer, read, buffer.Length - read);
                if (n <= 0) break;
                read += n;
            }
            return LastLines(Encoding.UTF8.GetString(buffer, 0, read), maxLines, startsMidFile: start > 0);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// The last <paramref name="maxLines"/> lines of <paramref name="text"/>. With
    /// <paramref name="startsMidFile"/> the first (partial) line is dropped; an unfinished last line is kept.
    /// </summary>
    public static List<string> LastLines(string text, int maxLines, bool startsMidFile)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (startsMidFile && lines.Count > 0) lines.RemoveAt(0);
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines.Count > maxLines ? lines.GetRange(lines.Count - maxLines, maxLines) : lines;
    }
}
