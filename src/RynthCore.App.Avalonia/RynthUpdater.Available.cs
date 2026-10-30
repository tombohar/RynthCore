using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RynthCore.App.Avalonia;

/// <summary>
/// "Available plugins": every plugin in the signed feed that isn't in the player's plugin list,
/// for the player to install or not.
///
/// Install downloads the feed's file (verified against the signed size and SHA-256, like every
/// update) into C:\Games\RynthSuite\&lt;Name&gt;\&lt;File&gt; - the folder beside RynthAi's, or the
/// default - and the caller adds it to the plugin list. Only plugins named in the signed manifest
/// can be installed, from the URLs it carries; nothing else.
///
/// Removing a plugin from the list never deletes its files; a feed plugin that leaves the list is
/// simply available again.
///
/// "New": a plugin the launcher sees in the feed for the first time is marked New for a month and
/// said once in the activity log. The first time this runs (no update-seen-plugins.json yet),
/// what the feed already had counts as old except <see cref="RecentlyAdded"/>, which a player
/// who never got them should hear about.
/// </summary>
internal sealed partial class RynthUpdater
{
    public const string DefaultSuiteDir = @"C:\Games\RynthSuite";
    private const string RynthAiFile = "RynthCore.Plugin.RynthAi.dll";
    private const int MaxDescriptionLength = 160;
    private const int MaxChanges = 20;
    private static readonly TimeSpan NewBadgeFor = TimeSpan.FromDays(30);
    private static readonly Regex SafeFolderName = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);

    /// <summary>A feed plugin the player can install: where it would go, what it is, and whether it's new.</summary>
    public sealed record AvailablePlugin(PluginEntry Entry, string Path, string Description, string Version, bool IsNew)
    {
        /// <summary>
        /// Empty when it can be installed; otherwise shown instead of the Install button: the feed's
        /// build needs a newer engine than the one installed ("needs RynthCore 2026.10.6.1").
        /// </summary>
        public string Blocker { get; init; } = "";
        public bool CanInstall => Blocker.Length == 0;
    }

    /// <summary>What <see cref="InstallPluginAsync"/> did: Downloaded is false when a good copy was already there.</summary>
    public sealed record PluginInstall(PluginEntry Entry, string Path, string Description, bool Downloaded);

    /// <summary>
    /// Plugins new in the feed before this list existed (2026.10.3.4). 2026.10.4.3 installed them
    /// beside RynthAi automatically; from this launcher on they are offered, not forced.
    /// </summary>
    private static readonly string[] RecentlyAdded = { "RynthOracle", "RynthInventory" };

    /// <summary>One line per plugin for feeds that carry no "description" (every feed before 2026.10.5).</summary>
    private static readonly Dictionary<string, string> BuiltInDescriptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RynthAi"] = "The bot: combat, buffs, looting, navigation routes and metas.",
        ["RynthChat"] = "A chat window that sorts messages into tabs.",
        ["RynthTracker"] = "Kills, experience and luminance per hour for each session.",
        ["RynthNav"] = "Walks you to coordinates and towns, with route planning and the Atlas. Downloads its map (about 540 MB).",
        ["RynthVision"] = "Shows unclimbable slopes, impassable water and your radar range in the world.",
        ["RynthLua"] = "Lua scripting for metas and your own scripts.",
        ["RynthOracle"] = "Quests, character and titles.",
        ["RynthInventory"] = "Search every character's items.",
        ["RynthSort"] = "Keeps your packs sorted: new loot goes to the pack its rule names.",
        ["RynthILT"] = "Infinite Leaftide only: the ILT Hub, the Guardian, bounty tracking and progression planning.",
        ["RynthThreads"] = "DreamWeave only: records Fragment Press results and works out what each component brings.",
        ["RynthNet"] = "Links your characters on this PC: broadcast commands, shared vitals and the Characters panel.",
        ["RynthDefective"] = "A shortcut bar with AC's own keys, buttons for AC's panels and the Selection, in place of the retail toolbar.",
    };

    /// <summary>The feed's description, else the launcher's own line, else "".</summary>
    public static string DescribePlugin(PluginEntry e) =>
        e.Description.Length > 0 ? e.Description
        : BuiltInDescriptions.TryGetValue(e.Name, out string? d) ? d : "";

    /// <summary>The launcher's own line for a RynthSuite plugin by name (no feed entry at hand), else "".</summary>
    public static string DescribeByName(string name) =>
        BuiltInDescriptions.TryGetValue(name, out string? d) ? d : "";

    private string? _suiteDirOverride;
    /// <summary>Tests: the folder to use when no plugin in the list shows where RynthSuite lives.</summary>
    internal void SetDefaultSuiteDir(string dir) => _suiteDirOverride = dir;
    /// <summary>Tests: the clock for the New badge.</summary>
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    /// <summary>
    /// Where RynthSuite plugins go: beside RynthAi (C:\Games\RynthSuite\RynthAi\x.dll -> C:\Games\RynthSuite),
    /// else beside any feed plugin that sits in a folder named after it, else the default.
    /// </summary>
    public string SuiteDir(Manifest manifest, IEnumerable<string> pluginPaths)
    {
        List<string> paths = pluginPaths.ToList();
        string? FromPlugin(string path)
        {
            string? folder = Path.GetDirectoryName(path);
            string? suite = Path.GetDirectoryName(folder);
            return string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(suite) ? null : suite;
        }
        string? ai = paths.FirstOrDefault(p => string.Equals(Path.GetFileName(p), RynthAiFile, StringComparison.OrdinalIgnoreCase));
        if (ai != null && FromPlugin(ai) is { } s1) return s1;
        foreach (string p in paths)
        {
            PluginEntry? e = manifest.Plugins.FirstOrDefault(x => string.Equals(x.File, Path.GetFileName(p), StringComparison.OrdinalIgnoreCase));
            if (e != null && string.Equals(Path.GetFileName(Path.GetDirectoryName(p)), e.Name, StringComparison.OrdinalIgnoreCase)
                && FromPlugin(p) is { } s2)
                return s2;
        }
        return _suiteDirOverride ?? DefaultSuiteDir;
    }

    /// <summary>C:\Games\RynthSuite\&lt;Name&gt;\&lt;File&gt;; null for a name that isn't a plain folder name.</summary>
    private static string? TargetPath(PluginEntry e, string suiteDir) =>
        SafeFolderName.IsMatch(e.Name) && e.Name != "." && e.Name != ".." ? Path.Combine(suiteDir, e.Name, e.File) : null;

    /// <summary>
    /// Every feed plugin whose file isn't in <paramref name="pluginPaths"/> (by file name, like the
    /// updates), except companions about to be installed anyway.
    /// </summary>
    public List<AvailablePlugin> GetAvailable(Manifest manifest, IEnumerable<string> pluginPaths, IEnumerable<CompanionInstall>? pending = null)
    {
        List<string> paths = pluginPaths.ToList();
        var listed = new HashSet<string>(paths.Select(Path.GetFileName).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        var skip = new HashSet<string>((pending ?? Array.Empty<CompanionInstall>()).Select(c => c.Entry.File), StringComparer.OrdinalIgnoreCase);
        string suite = SuiteDir(manifest, paths);
        Dictionary<string, DateTime> seen = ReadSeenPlugins(out _);
        DateTime now = UtcNow();
        var result = new List<AvailablePlugin>();
        foreach (PluginEntry e in manifest.Plugins)
        {
            if (listed.Contains(e.File) || skip.Contains(e.File)) continue;
            if (TargetPath(e, suite) is not { } target) continue;
            bool isNew = seen.TryGetValue(e.Name, out DateTime first) && now - first < NewBadgeFor;
            result.Add(new AvailablePlugin(e, target, DescribePlugin(e), manifest.Version, isNew) { Blocker = EngineBlocker(e, manifest) });
        }
        // New ones first, then by name.
        return result.OrderByDescending(a => a.IsNew).ThenBy(a => a.Entry.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The Available list for a check, against the plugin list as it is now.</summary>
    public List<AvailablePlugin> GetAvailable(CheckResult check, IEnumerable<string> pluginPaths) =>
        GetAvailable(check.Manifest, pluginPaths, check.Companions);

    /// <summary>
    /// Installs one plugin from the signed feed for the caller to add to the plugin list. A file
    /// already at the target is kept when it is the release build or newer (someone's own build);
    /// an older one is replaced and kept beside it as .previous. Throws, with nothing changed,
    /// when the download doesn't match the signed size and hash or is cancelled.
    /// </summary>
    public async Task<PluginInstall> InstallPluginAsync(CheckResult check, string name, IEnumerable<string> pluginPaths,
                                                        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        List<string> paths = pluginPaths.ToList();
        PluginEntry e = check.Manifest.Plugins.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"{name} is not in the update feed");
        if (paths.Any(p => string.Equals(Path.GetFileName(p), e.File, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"{e.Name} is already in your plugin list");
        string target = TargetPath(e, SuiteDir(check.Manifest, paths))
            ?? throw new InvalidDataException($"bad plugin name '{e.Name}' in the feed");
        // A build the installed engine would refuse (its manifest's minEngineApi) isn't installed.
        if (EngineBlocker(e, check.Manifest) is { Length: > 0 } blocker)
            throw new InvalidOperationException($"{e.Name} {blocker}");

        if (File.Exists(target))
        {
            bool same = string.Equals(Sha256File(target), e.Sha256, StringComparison.OrdinalIgnoreCase);
            bool localBuild = File.GetLastWriteTimeUtc(target) > check.Manifest.PublishedUtc.AddMinutes(1);
            if (same || localBuild)
                return new PluginInstall(e, target, DescribePlugin(e), Downloaded: false);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string temp = Path.Combine(Path.GetTempPath(), $"rynth-{Guid.NewGuid():N}.dll");
        try
        {
            await DownloadVerifiedAsync(e.Url, e.Size, e.Sha256, MaxPluginBytes, temp, ct, e.Name, progress);
            progress?.Report(new DownloadProgress(e.Name, DownloadStage.Installing, 0, 0));
            // Stamp the release time so a later check never mistakes it for a local build.
            File.SetLastWriteTimeUtc(temp, check.Manifest.PublishedUtc);
            if (File.Exists(target)) File.Copy(target, target + ".previous", overwrite: true);
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
        return new PluginInstall(e, target, DescribePlugin(e), Downloaded: true);
    }

    // ── Manifest fields (optional, backward-compatible) ───────────────────────

    /// <summary>The plugin entry's optional "description": one line, plain text, at most 160 characters.</summary>
    private static string ReadDescription(JsonElement p)
    {
        if (!p.TryGetProperty("description", out JsonElement d) || d.ValueKind != JsonValueKind.String) return "";
        return OneLine(d.GetString(), MaxDescriptionLength);
    }

    /// <summary>
    /// An optional engine API number ("minEngineApi" on a plugin, "engineApi" on the core; written
    /// since plugin manifests). Missing or unreadable: 0, no limit, as with older feeds.
    /// </summary>
    private static uint ReadApi(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out JsonElement v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetUInt32(out uint n)) return n;
        return 0;
    }

    /// <summary>The manifest's optional "changes": up to 20 lines.</summary>
    private static IReadOnlyList<string> ReadChanges(JsonElement r)
    {
        if (!r.TryGetProperty("changes", out JsonElement c) || c.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        var list = new List<string>();
        foreach (JsonElement e in c.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.String) continue;
            string line = OneLine(e.GetString(), 300);
            if (line.Length > 0) list.Add(line);
            if (list.Count >= MaxChanges) break;
        }
        return list;
    }

    private static string OneLine(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder(Math.Min(s.Length, max + 1));
        bool space = false;
        foreach (char ch in s)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(ch);
            if (sb.Length > max) break;
        }
        string line = sb.ToString();
        return line.Length > max ? line[..(max - 1)].TrimEnd() + "…" : line;
    }

    // ── Seen plugins (the New badge) ──────────────────────────────────────────

    private string SeenPath => Path.Combine(_stateDir, "update-seen-plugins.json");

    private Dictionary<string, DateTime> ReadSeenPlugins(out bool existed)
    {
        var map = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        existed = false;
        try
        {
            if (!File.Exists(SeenPath)) return map;
            using var doc = JsonDocument.Parse(File.ReadAllText(SeenPath));
            foreach (JsonProperty p in doc.RootElement.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && DateTime.TryParse(p.Value.GetString(), null,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime t))
                    map[p.Name] = t;
            existed = true;
        }
        catch { map.Clear(); existed = false; }
        return map;
    }

    /// <summary>Records the feed's plugins as seen; returns the ones seen for the first time that count as new.</summary>
    private HashSet<string> RecordSeenPlugins(Manifest manifest)
    {
        var fresh = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, DateTime> seen = ReadSeenPlugins(out bool existed);
        HashSet<string> offered = ReadOfferedCompanions();
        DateTime now = UtcNow();
        bool changed = !existed;
        foreach (PluginEntry e in manifest.Plugins)
        {
            if (seen.ContainsKey(e.Name)) continue;
            // First run of this list: only the recent additions are news, and not to a player who
            // already had them offered (2026.10.4.3 installed them; removing one keeps it removed).
            bool isNew = existed || (RecentlyAdded.Contains(e.Name, StringComparer.OrdinalIgnoreCase) && !offered.Contains(e.Name));
            seen[e.Name] = isNew ? now : DateTime.UnixEpoch;
            if (isNew) fresh.Add(e.Name);
            changed = true;
        }
        if (changed)
        {
            try
            {
                Directory.CreateDirectory(_stateDir);
                string tmp = SeenPath + ".tmp";
                var ordered = seen.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                                  .ToDictionary(kv => kv.Key, kv => kv.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"));
                File.WriteAllText(tmp, JsonSerializer.Serialize(ordered, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
                File.Move(tmp, SeenPath, overwrite: true);
            }
            catch { }
        }
        return fresh;
    }
}

/// <summary>Edits to the launcher's plugin list (kept apart from the window so tests can run them).</summary>
internal static class PluginListEdits
{
    /// <summary>Adds <paramref name="path"/> once; false when it was already listed.</summary>
    public static bool Add(IList<string> paths, string path)
    {
        if (paths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase))) return false;
        paths.Add(path);
        return true;
    }

    /// <summary>
    /// Takes <paramref name="path"/> out of the list, and out of the disabled list so adding it back
    /// later starts it enabled. Never touches the file.
    /// </summary>
    public static bool Remove(IList<string> paths, IList<string>? disabled, string path)
    {
        bool removed = RemoveAll(paths, path) > 0;
        if (disabled != null) RemoveAll(disabled, path);
        return removed;
    }

    private static int RemoveAll(IList<string> list, string path)
    {
        int n = 0;
        for (int i = list.Count - 1; i >= 0; i--)
            if (string.Equals(list[i], path, StringComparison.OrdinalIgnoreCase)) { list.RemoveAt(i); n++; }
        return n;
    }
}
