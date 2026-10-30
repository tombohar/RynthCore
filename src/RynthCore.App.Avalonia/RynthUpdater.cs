using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RynthCore.App.Avalonia;

/// <summary>
/// Signed updates for RynthCore and the RynthSuite plugins.
///
/// The feed is a signed envelope — {"payload": base64(manifest JSON), "signatures": [{"key": id,
/// "sig": base64}]}, ECDSA P-256 / SHA-256 over the payload bytes (IEEE P1363). Nothing in it is
/// used until a signature from one of <see cref="UpdateTrust.PublicKeys"/> verifies. The signing
/// key lives on the publisher's PC, never on the server, so a compromised aelrynth.com cannot push
/// code to players. Every download is then checked against the signed size and SHA-256.
///
/// Plugins update in place. The engine runs plugins from shadow copies, so swapping the DLL never
/// touches a running bot: the new version loads the next time AC starts, or on RL if the player
/// chooses — the updater never reloads anything. The core (engine, Loader, launcher) updates by
/// running the release's installer, and only while no RynthCore client is running.
///
/// RynthNav's data (navmesh tiles, portals.tsv, locations.json) is an optional signed archive in
/// the same manifest ("navdata"), installed only for players who have RynthNav in their plugin
/// list, and only when its id (a hash of the tile set) changes. See RynthUpdater.NavData.cs.
/// </summary>
internal sealed partial class RynthUpdater
{
    public const string DefaultFeedUrl = "https://aelrynth.com/downloads/rynth/update.json";

    private const long MaxFeedBytes = 256 * 1024;
    private const long MaxPluginBytes = 64L << 20;
    private const long MaxInstallerBytes = 256L << 20;

    /// <summary>
    /// One plugin in the feed. <paramref name="Description"/> is the optional one-line "description"
    /// (written since 2026.10.5); older feeds have none and the launcher's own table fills in
    /// (<see cref="DescribePlugin"/>). Launchers before it ignore the field.
    /// <paramref name="MinEngineApi"/> is the optional "minEngineApi", from the plugin's embedded
    /// manifest (Publish-Update.ps1 copies it): the oldest engine plugin API the build runs on.
    /// 0 (older feeds, plugins without a manifest) means no limit. See <see cref="EngineBlocker"/>.
    /// </summary>
    public sealed record PluginEntry(string Name, string File, string Url, long Size, string Sha256, string Description = "",
                                     uint MinEngineApi = 0);
    /// <summary>The core release. <paramref name="EngineApi"/>: the optional "engineApi", its engine's plugin API (0 = not said).</summary>
    public sealed record InstallerEntry(int Release, string Version, string Url, long Size, string Sha256, uint EngineApi = 0);
    public sealed record Manifest(int Release, string Version, DateTime PublishedUtc, string Notes,
                                  InstallerEntry Core, IReadOnlyList<PluginEntry> Plugins, NavDataEntry? NavData = null)
    {
        /// <summary>
        /// The release's changes, one line each: the optional "changes" array (written since 2026.10.5;
        /// signed with the rest). Empty for older feeds, which only have <see cref="Notes"/>.
        /// </summary>
        public IReadOnlyList<string> Changes { get; init; } = Array.Empty<string>();
        /// <summary>The optional World Map sizes the feed offers (RynthUpdater.MapPacks.cs); empty for older feeds.</summary>
        public IReadOnlyList<MapPackEntry> MapPacks { get; init; } = Array.Empty<MapPackEntry>();
    }

    /// <summary>One installed copy of a plugin the feed knows.</summary>
    public sealed record PluginStatus(PluginEntry Entry, string Path, PluginState State)
    {
        /// <summary>For <see cref="PluginState.NeedsNewerEngine"/>: "needs RynthCore 2026.10.6.1".</summary>
        public string Blocker { get; init; } = "";
    }
    /// <summary>
    /// NeedsNewerEngine: the feed's build needs a newer engine plugin API than the RynthCore installed
    /// here, so the installed copy is kept until RynthCore itself is updated (the engine would refuse
    /// the new build and the player would lose the plugin meanwhile).
    /// </summary>
    public enum PluginState { UpToDate, NeedsUpdate, LocalBuild, NeedsNewerEngine }

    /// <summary>A plugin split out of one the player has, to be installed beside it (see <see cref="Companions"/>).</summary>
    public sealed record CompanionInstall(PluginEntry Entry, string Path, string Reason);

    public sealed record CheckResult(Manifest Manifest, int InstalledCoreRelease, IReadOnlyList<PluginStatus> Plugins)
    {
        public bool CoreUpdateAvailable => Manifest.Core.Release > InstalledCoreRelease;
        public IEnumerable<PluginStatus> PluginsToUpdate => Plugins.Where(p => p.State == PluginState.NeedsUpdate);
        /// <summary>Plugin updates held back until RynthCore is updated (<see cref="PluginState.NeedsNewerEngine"/>).</summary>
        public IEnumerable<PluginStatus> PluginsWaitingForCore => Plugins.Where(p => p.State == PluginState.NeedsNewerEngine);
        /// <summary>New plugins that came out of an installed one; installed with the plugin updates.</summary>
        public IReadOnlyList<CompanionInstall> Companions { get; init; } = Array.Empty<CompanionInstall>();
        /// <summary>Feed plugins not in the plugin list, for the player to install or not (see <see cref="GetAvailable"/>).</summary>
        public IReadOnlyList<AvailablePlugin> Available { get; init; } = Array.Empty<AvailablePlugin>();
        /// <summary>Available plugins this launcher sees in the feed for the first time: said once in the activity log.</summary>
        public IReadOnlyList<AvailablePlugin> NewlyAnnounced { get; init; } = Array.Empty<AvailablePlugin>();
        /// <summary>RynthNav's tiles: null when the feed has none or RynthNav isn't in the plugin list.</summary>
        public NavDataStatus? NavData { get; init; }
        public bool NavDataUpdateAvailable => NavData is { State: NavDataState.NeedsUpdate };
        /// <summary>The optional World Map sizes: what the feed offers and what is installed. Not part of "Update plugins": the player chooses.</summary>
        public IReadOnlyList<MapPackStatus> MapPacks { get; init; } = Array.Empty<MapPackStatus>();
        /// <summary>
        /// The release the player went back from with Roll back (0: none; RynthUpdater.Rollback.cs). While
        /// the feed offers nothing newer and the player hasn't updated past it, updates are held.
        /// </summary>
        public int HoldThrough { get; init; }
        /// <summary>Held after a rollback: no automatic plugin updates; the card says so.</summary>
        public bool Held => HoldThrough > 0 && Manifest.Release <= HoldThrough && InstalledCoreRelease < HoldThrough;
        /// <summary>A rollback's hold is over: a newer release is out, or the player updated past it by hand.</summary>
        public bool HoldEnded => HoldThrough > 0 && !Held;
    }

    /// <summary>
    /// Features that moved out of a plugin into their own. When the player has the parent and not the
    /// child, the child is installed in a sibling folder and registered, once: a player who removes it
    /// afterwards keeps it removed.
    ///
    /// Only for a split, where the player's scripts stop working without the child. A plugin that is
    /// simply new (RynthOracle, RynthInventory: 2026.10.4.3 auto-installed them this way) is offered
    /// instead, under Available plugins with a "New" badge (<see cref="RecentlyAdded"/>); players who
    /// already got them keep them.
    /// </summary>
    private static readonly (string ParentFile, string ChildName, string Reason)[] Companions =
    {
        ("RynthCore.Plugin.RynthAi.dll", "RynthLua", "Lua scripting, which moved out of RynthAi"),
    };

    private static readonly HttpClient Http = CreateHttp();

    private readonly string _feedUrl;
    private readonly string _allowedPrefix;
    private readonly IReadOnlyList<string> _trustedKeys;
    private readonly string _stateDir;
    private readonly string _appDir;

    public RynthUpdater(string feedUrl = DefaultFeedUrl, IReadOnlyList<string>? trustedKeys = null,
                        string? stateDir = null, string? appDir = null)
    {
        _feedUrl = feedUrl;
        // Downloads must come from the feed's own folder — the signature already covers the URLs,
        // this just refuses to follow a signed manifest anywhere else.
        _allowedPrefix = feedUrl[..(feedUrl.LastIndexOf('/') + 1)];
        _trustedKeys = trustedKeys ?? UpdateTrust.PublicKeys;
        _stateDir = stateDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RynthCore");
        _appDir = appDir ?? AppContext.BaseDirectory;
    }

    /// <summary>
    /// The plugin API of the engine installed with this launcher. The installer ships the launcher and
    /// the engine together (a core update replaces both), so it is the engine's own constant,
    /// compiled in from PluginContractVersion.cs. Tests set it.
    /// </summary>
    internal uint InstalledEngineApi { get; set; } = RynthCore.Engine.Plugins.PluginContractVersion.Current;

    /// <summary>
    /// Empty when the installed engine runs <paramref name="e"/>; otherwise what the player needs, shown
    /// in place of the Install button: "needs RynthCore 2026.10.6.1" when the feed's own core release
    /// brings a new enough engine, else "needs a newer RynthCore (API 77)".
    /// </summary>
    public string EngineBlocker(PluginEntry e, Manifest manifest)
    {
        if (e.MinEngineApi == 0 || e.MinEngineApi <= InstalledEngineApi) return "";
        InstallerEntry core = manifest.Core;
        bool coreBringsIt = core.EngineApi >= e.MinEngineApi && core.Release > InstalledCoreRelease && core.Version.Length > 0;
        return coreBringsIt ? $"needs RynthCore {core.Version}" : $"needs a newer RynthCore (API {e.MinEngineApi})";
    }

    /// <summary>Where installers (and a rollback's plugins) are downloaded: %LOCALAPPDATA%\RynthCore\updates. Tests set it.</summary>
    internal string UpdatesDir { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RynthCore", "updates");

    /// <summary>False until the signing keys exist: with no trusted key nothing can verify.</summary>
    public bool IsConfigured => _trustedKeys.Count > 0;

    /// <summary>The core release this install came from (release.txt, written by the installer); 0 when unknown.</summary>
    public int InstalledCoreRelease
    {
        get
        {
            try
            {
                string path = Path.Combine(_appDir, "release.txt");
                if (!File.Exists(path)) return 0;
                string first = File.ReadLines(path).FirstOrDefault() ?? "";
                return int.TryParse(first.Trim(), out int r) ? r : 0;
            }
            catch { return 0; }
        }
    }

    // ── Check ─────────────────────────────────────────────────────────────────

    public async Task<CheckResult> CheckAsync(IEnumerable<string> pluginPaths, CancellationToken ct = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("updates are not set up in this build (no signing key)");

        byte[] envelope = await DownloadBytesAsync(_feedUrl, MaxFeedBytes, ct);
        byte[] payload = VerifyEnvelope(envelope, _trustedKeys);
        Manifest manifest = ParseManifest(payload);

        // Replay guard: a signed but older feed (an attacker re-serving last month's) is refused.
        int floor = Math.Max(ReadHighestRelease(), InstalledCoreRelease);
        if (manifest.Release < floor)
            throw new InvalidDataException($"the feed is older ({manifest.Release}) than one already seen ({floor})");
        if (manifest.Release > ReadHighestRelease())
            WriteHighestRelease(manifest.Release);

        var statuses = new List<PluginStatus>();
        var byFile = manifest.Plugins.ToDictionary(p => p.File, StringComparer.OrdinalIgnoreCase);
        foreach (string path in pluginPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!byFile.TryGetValue(Path.GetFileName(path), out PluginEntry? entry) || !File.Exists(path))
                continue;   // not a plugin this feed ships (a private or third-party one): never touched

            PluginState state;
            string blocker = "";
            if (string.Equals(Sha256File(path), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                state = PluginState.UpToDate;
            else if (File.GetLastWriteTimeUtc(path) > manifest.PublishedUtc.AddMinutes(1))
                state = PluginState.LocalBuild;   // newer than the release: someone's own build, leave it
            else if ((blocker = EngineBlocker(entry, manifest)).Length > 0)
                state = PluginState.NeedsNewerEngine;   // keep the copy that runs until RynthCore catches up
            else
                state = PluginState.NeedsUpdate;
            statuses.Add(new PluginStatus(entry, path, state) { Blocker = blocker });
        }

        List<CompanionInstall> companions = FindCompanions(manifest, pluginPaths);
        HashSet<string> fresh = RecordSeenPlugins(manifest);   // before GetAvailable: it reads the New badges
        List<AvailablePlugin> available = GetAvailable(manifest, pluginPaths, companions);
        return new CheckResult(manifest, InstalledCoreRelease, statuses)
        {
            Companions = companions,
            NavData = CheckNavData(manifest, pluginPaths),
            MapPacks = CheckMapPacks(manifest),
            Available = available,
            NewlyAnnounced = available.Where(a => fresh.Contains(a.Entry.Name)).ToList(),
            HoldThrough = HeldThroughRelease,
        };
    }

    private List<CompanionInstall> FindCompanions(Manifest manifest, IEnumerable<string> pluginPaths)
    {
        var result = new List<CompanionInstall>();
        List<string> paths = pluginPaths.ToList();
        HashSet<string> offered = ReadOfferedCompanions();
        foreach (var (parentFile, childName, reason) in Companions)
        {
            if (offered.Contains(childName)) continue;
            string? parent = paths.FirstOrDefault(p =>
                string.Equals(Path.GetFileName(p), parentFile, StringComparison.OrdinalIgnoreCase) && File.Exists(p));
            PluginEntry? child = manifest.Plugins.FirstOrDefault(e => string.Equals(e.Name, childName, StringComparison.OrdinalIgnoreCase));
            if (parent == null || child == null) continue;
            if (EngineBlocker(child, manifest).Length > 0) continue;   // not yet: offered once RynthCore is updated
            if (paths.Any(p => string.Equals(Path.GetFileName(p), child.File, StringComparison.OrdinalIgnoreCase)))
            {
                MarkCompanionOffered(childName);   // already there: nothing to do, ever
                continue;
            }
            // C:\Games\RynthSuite\RynthAi\x.dll -> C:\Games\RynthSuite\RynthLua\<file>
            string? suiteDir = Path.GetDirectoryName(Path.GetDirectoryName(parent));
            if (string.IsNullOrEmpty(suiteDir)) continue;
            result.Add(new CompanionInstall(child, Path.Combine(suiteDir, childName, child.File), reason));
        }
        return result;
    }

    /// <summary>
    /// Installs <see cref="CheckResult.Companions"/> (verified like every plugin; an existing file at the
    /// target is kept) and returns their paths for the caller to register in the plugin list.
    /// </summary>
    public async Task<List<CompanionInstall>> InstallCompanionsAsync(CheckResult check, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var done = new List<CompanionInstall>();
        foreach (CompanionInstall c in check.Companions)
        {
            if (!File.Exists(c.Path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(c.Path)!);
                string temp = Path.Combine(Path.GetTempPath(), $"rynth-{Guid.NewGuid():N}.dll");
                try
                {
                    await DownloadVerifiedAsync(c.Entry.Url, c.Entry.Size, c.Entry.Sha256, MaxPluginBytes, temp, ct, c.Entry.Name, progress);
                    File.SetLastWriteTimeUtc(temp, check.Manifest.PublishedUtc);
                    File.Move(temp, c.Path, overwrite: false);
                }
                finally
                {
                    try { File.Delete(temp); } catch { }
                }
            }
            MarkCompanionOffered(c.Entry.Name);
            done.Add(c);
        }
        return done;
    }

    // ── Plugins ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Swaps each outdated plugin DLL for the verified release build. The previous file is kept
    /// beside it as .previous. Running clients keep the version they started with.
    /// </summary>
    public async Task<List<string>> UpdatePluginsAsync(CheckResult check, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var done = new List<string>();
        foreach (var group in check.PluginsToUpdate.GroupBy(p => p.Entry))
        {
            PluginEntry e = group.Key;
            string temp = Path.Combine(Path.GetTempPath(), $"rynth-{Guid.NewGuid():N}.dll");
            try
            {
                await DownloadVerifiedAsync(e.Url, e.Size, e.Sha256, MaxPluginBytes, temp, ct, e.Name, progress);
                foreach (PluginStatus p in group)
                {
                    string staged = p.Path + ".new";
                    File.Copy(temp, staged, overwrite: true);
                    // Stamp the release time so a later check never mistakes it for a local build.
                    File.SetLastWriteTimeUtc(staged, check.Manifest.PublishedUtc);
                    File.Copy(p.Path, p.Path + ".previous", overwrite: true);
                    File.Move(staged, p.Path, overwrite: true);
                    done.Add($"{e.Name} → {check.Manifest.Version}");
                }
            }
            finally
            {
                try { File.Delete(temp); } catch { }
            }
        }
        return done;
    }

    // ── Core ──────────────────────────────────────────────────────────────────

    /// <summary>Downloads (or reuses) the release installer, verified, and returns its path.</summary>
    public Task<string> DownloadInstallerAsync(CheckResult check, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default) =>
        DownloadInstallerAsync(check.Manifest.Core, progress, ct);

    /// <summary>Downloads (or reuses) <paramref name="core"/>'s installer, verified, and returns its path.</summary>
    public async Task<string> DownloadInstallerAsync(InstallerEntry core, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        string dir = Path.Combine(UpdatesDir, core.Release.ToString());
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "RynthCore-Setup.exe");
        if (File.Exists(path) && new FileInfo(path).Length == core.Size &&
            string.Equals(Sha256File(path), core.Sha256, StringComparison.OrdinalIgnoreCase))
            return path;

        string temp = path + ".part";
        await DownloadVerifiedAsync(core.Url, core.Size, core.Sha256, MaxInstallerBytes, temp, ct, $"RynthCore {core.Version}", progress);
        File.Move(temp, path, overwrite: true);
        return path;
    }

    /// <summary>Runs the installer silently; it closes the launcher, upgrades in place and relaunches it.</summary>
    public static void RunInstaller(string installerPath)
    {
        // DownloadInstallerAsync verified it moments ago, into a per-user folder.
        Process.Start(new ProcessStartInfo(installerPath,
            "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH") { UseShellExecute = true });
    }

    // ── Envelope + manifest ───────────────────────────────────────────────────

    /// <summary>Returns the payload bytes if any trusted key signed them; throws otherwise.</summary>
    public static byte[] VerifyEnvelope(byte[] envelope, IReadOnlyList<string> trustedKeys)
    {
        using var doc = JsonDocument.Parse(envelope);
        byte[] payload = Convert.FromBase64String(doc.RootElement.GetProperty("payload").GetString() ?? "");
        var sigs = doc.RootElement.GetProperty("signatures");

        foreach (string key in trustedKeys)
        {
            byte[] spki = Convert.FromBase64String(key);
            string id = KeyId(spki);
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(spki, out _);
            foreach (JsonElement s in sigs.EnumerateArray())
            {
                if (!string.Equals(s.GetProperty("key").GetString(), id, StringComparison.OrdinalIgnoreCase))
                    continue;
                byte[] sig = Convert.FromBase64String(s.GetProperty("sig").GetString() ?? "");
                if (ec.VerifyData(payload, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                    return payload;
            }
        }
        throw new InvalidDataException("the update feed's signature is not valid");
    }

    /// <summary>First 8 bytes of SHA-256 over the public key (SubjectPublicKeyInfo), as hex.</summary>
    public static string KeyId(byte[] spki) => Convert.ToHexString(SHA256.HashData(spki).AsSpan(0, 8)).ToLowerInvariant();

    private Manifest ParseManifest(byte[] payload)
    {
        using var doc = JsonDocument.Parse(payload);
        JsonElement r = doc.RootElement;
        if (r.GetProperty("schema").GetInt32() != 1)
            throw new InvalidDataException("unknown update feed format — update RynthCore by hand");

        JsonElement c = r.GetProperty("core");
        var core = new InstallerEntry(c.GetProperty("release").GetInt32(), c.GetProperty("version").GetString() ?? "",
            CheckUrl(c.GetProperty("url").GetString()), c.GetProperty("size").GetInt64(), c.GetProperty("sha256").GetString() ?? "",
            ReadApi(c, "engineApi"));

        var plugins = new List<PluginEntry>();
        foreach (JsonElement p in r.GetProperty("plugins").EnumerateArray())
        {
            string file = p.GetProperty("file").GetString() ?? "";
            if (file.Length == 0 || file != Path.GetFileName(file) || !file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"bad plugin file name '{file}'");
            plugins.Add(new PluginEntry(p.GetProperty("name").GetString() ?? file, file,
                CheckUrl(p.GetProperty("url").GetString()), p.GetProperty("size").GetInt64(), p.GetProperty("sha256").GetString() ?? "",
                ReadDescription(p), ReadApi(p, "minEngineApi")));
        }

        return new Manifest(r.GetProperty("release").GetInt32(), r.GetProperty("version").GetString() ?? "",
            r.GetProperty("published").GetDateTime().ToUniversalTime(),
            r.TryGetProperty("notes", out JsonElement n) ? n.GetString() ?? "" : "", core, plugins,
            r.TryGetProperty("navdata", out JsonElement nd) && nd.ValueKind == JsonValueKind.Object ? TryParseNavData(nd) : null)
        {
            Changes = ReadChanges(r),
            MapPacks = ReadMapPacks(r),
        };
    }

    private string CheckUrl(string? url)
    {
        if (url == null || !url.StartsWith(_allowedPrefix, StringComparison.Ordinal) || url.Contains(".."))
            throw new InvalidDataException($"download outside the update folder: '{url}'");
        return url;
    }

    // ── Downloads ─────────────────────────────────────────────────────────────

    private static async Task<byte[]> DownloadBytesAsync(string url, long max, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        byte[] buf = new byte[16384];
        int n;
        while ((n = await s.ReadAsync(buf, ct)) > 0)
        {
            if (ms.Length + n > max) throw new InvalidDataException($"{url} is larger than expected");
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Downloads to <paramref name="dest"/> and checks it against the signed size and SHA-256. On any
    /// failure (a wrong hash, a lost connection, Cancel) the partial file is deleted. Progress, when
    /// asked for, goes to <paramref name="progress"/> a few times a second (<see cref="DownloadMeter"/>),
    /// then one <see cref="DownloadStage.Verified"/> report once the hash matched.
    /// </summary>
    private static async Task DownloadVerifiedAsync(string url, long size, string sha256, long max, string dest, CancellationToken ct,
                                                    string? what = null, IProgress<DownloadProgress>? progress = null)
    {
        if (size <= 0 || size > max) throw new InvalidDataException($"{url}: size {size} out of range");
        var meter = new DownloadMeter(what ?? Path.GetFileName(url), size, progress);
        try
        {
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            meter.Report(0);
            await using (var s = await resp.Content.ReadAsStreamAsync(ct))
            await using (var f = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] buf = new byte[81920];
                int n;
                while ((n = await s.ReadAsync(buf, ct)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    total += n;
                    if (total > size) throw new InvalidDataException($"{url} is larger than the signed size");
                    hash.AppendData(buf, 0, n);
                    await f.WriteAsync(buf.AsMemory(0, n), ct);
                    meter.Report(total);
                }
            }
            string got = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (total != size || !string.Equals(got, sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{Path.GetFileName(url)} does not match the signed hash — not installed");
            meter.Verified();
        }
        catch
        {
            // The file stream is closed by now (its using block ended), so the delete can't be refused by it.
            try { File.Delete(dest); } catch { }
            throw;
        }
    }

    public static string Sha256File(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RynthCore-Launcher");
        return http;
    }

    // ── State ─────────────────────────────────────────────────────────────────

    private string StatePath => Path.Combine(_stateDir, "update-state.json");
    private string CompanionsPath => Path.Combine(_stateDir, "update-companions.json");

    private HashSet<string> ReadOfferedCompanions()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(CompanionsPath)) return set;
            using var doc = JsonDocument.Parse(File.ReadAllText(CompanionsPath));
            foreach (JsonElement e in doc.RootElement.EnumerateArray())
                if (e.GetString() is { Length: > 0 } s) set.Add(s);
        }
        catch { }
        return set;
    }

    private void MarkCompanionOffered(string name)
    {
        try
        {
            HashSet<string> set = ReadOfferedCompanions();
            if (!set.Add(name)) return;
            Directory.CreateDirectory(_stateDir);
            string tmp = CompanionsPath + ".tmp";
            File.WriteAllText(tmp, "[" + string.Join(", ", set.OrderBy(s => s).Select(s => JsonSerializer.Serialize(s))) + "]", Encoding.UTF8);
            File.Move(tmp, CompanionsPath, overwrite: true);
        }
        catch { }
    }

    private int ReadHighestRelease() => ReadStateInt("highestRelease");

    private void WriteHighestRelease(int release) => WriteState(s => s["highestRelease"] = release);

    // update-state.json holds several values (the replay guard's highest release, a rollback's hold):
    // each write changes its own keys and keeps the rest.

    private System.Text.Json.Nodes.JsonObject ReadState()
    {
        try
        {
            if (File.Exists(StatePath) && System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(StatePath)) is System.Text.Json.Nodes.JsonObject o)
                return o;
        }
        catch { }
        return new System.Text.Json.Nodes.JsonObject();
    }

    private int ReadStateInt(string key)
    {
        try { return ReadState()[key]?.GetValue<int>() ?? 0; }
        catch { return 0; }
    }

    private bool ReadStateBool(string key)
    {
        try { return ReadState()[key]?.GetValue<bool>() ?? false; }
        catch { return false; }
    }

    private void WriteState(Action<System.Text.Json.Nodes.JsonObject> change)
    {
        try
        {
            System.Text.Json.Nodes.JsonObject state = ReadState();
            change(state);
            Directory.CreateDirectory(_stateDir);
            string tmp = StatePath + ".tmp";
            File.WriteAllText(tmp, state.ToJsonString(), Encoding.UTF8);
            File.Move(tmp, StatePath, overwrite: true);
        }
        catch { }
    }
}
