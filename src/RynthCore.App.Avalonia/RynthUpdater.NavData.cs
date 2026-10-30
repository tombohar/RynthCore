using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RynthCore.App.Avalonia;

/// <summary>
/// RynthNav's data folder (C:\Games\RynthCore\NavData by default): the navmesh tiles, portals.tsv
/// and locations.json, shipped as one archive.
///
/// Feed: an optional "navdata" object in the signed manifest —
///   {"id": hash of the tile set, "release": n, "url", "size", "sha256", "files": file count,
///    "bytes": unpacked size}.
/// The signature covers it exactly like the plugins; the archive is checked against the signed
/// size and SHA-256 before it is opened, and only plain file names RynthNav reads
/// (nav_XXXX.tile, portals.tsv, locations.json, navgraph.bin: the long-range route graph,
/// townnet.json: the Town Network's portals and the walks inside, worldmap.rmap and worldmap.vec: the World Map's tiles, buildings and roads) are
/// accepted from it.
///
/// Install: only when RynthNav is in the plugin list and the folder's navdata.id differs from the
/// feed's id. The archive is unpacked beside the folder (NavData.new), the player's own files there
/// (atlas.txt, recalls.txt, anything else not shipped) are copied across, then the folders swap:
/// the old one is kept as NavData.previous (one generation). Old tiles never mix with new ones —
/// tiles from different bakes don't link. A folder with a navdata.local file in it is never touched
/// (someone baking their own tiles). Running clients keep the tiles they already loaded; new ones
/// load as they move (a relog picks up everything).
/// </summary>
internal sealed partial class RynthUpdater
{
    public static readonly string NavDataDefaultDir = System.IO.Path.Combine(RynthCore.Install.RynthInstallPaths.CoreDir, "NavData"); // C:\Games\RynthCore\NavData by default
    public const string NavDataIdFile = "navdata.id";
    public const string NavDataLocalMarker = "navdata.local";
    private const string RynthNavFile = "RynthCore.Plugin.RynthNav.dll";
    private const long MaxNavDataBytes = 3L << 30;       // the archive
    private const long MaxNavDataEntryBytes = 32L << 20; // one file in it

    private static readonly Regex ShippedName = new(@"^(nav_[0-9A-Fa-f]{4}\.tile|portals\.tsv|locations\.json|navgraph\.bin|townnet\.json|worldmap\.rmap|worldmap\.vec)$", RegexOptions.CultureInvariant);
    private static readonly Regex IdPattern = new(@"^[0-9a-f]{16,64}$", RegexOptions.CultureInvariant);

    public sealed record NavDataEntry(string Id, int Release, string Url, long Size, string Sha256, int Files, long Bytes);
    public enum NavDataState { UpToDate, NeedsUpdate, LocalTiles }
    public sealed record NavDataStatus(NavDataEntry Entry, string Dir, string InstalledId, NavDataState State);

    /// <summary>True for a file name the archive may carry (and that an update replaces).</summary>
    public static bool IsShippedNavFile(string name) => ShippedName.IsMatch(name);

    /// <summary>
    /// A whole number written either way: 1383002945 or 1383002945.0. PowerShell's ConvertTo-Json writes
    /// a double with ".0", and GetInt64 refused it - which failed the whole update check for every
    /// launcher on 2026.10.3.1/2 ("One of the identified items was in an invalid format").
    /// </summary>
    private static long WholeNumber(JsonElement e)
    {
        if (e.TryGetInt64(out long l)) return l;
        double d = e.GetDouble();
        if (d < 0 || d > long.MaxValue || d != Math.Floor(d)) throw new InvalidDataException($"not a whole number: {e.GetRawText()}");
        return (long)d;
    }

    /// <summary>A navdata entry that can't be read leaves the tiles out of this check; the core and plugins still update.</summary>
    private NavDataEntry? TryParseNavData(JsonElement nd)
    {
        try { return ParseNavData(nd); }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            System.Diagnostics.Debug.WriteLine($"RynthUpdater: navdata entry unreadable ({ex.Message}); tiles skipped this check.");
            return null;
        }
    }

    private NavDataEntry ParseNavData(JsonElement nd)
    {
        string id = (nd.GetProperty("id").GetString() ?? "").ToLowerInvariant();
        if (!IdPattern.IsMatch(id)) throw new InvalidDataException($"bad navdata id '{id}'");
        int files = (int)WholeNumber(nd.GetProperty("files"));
        if (files <= 0) throw new InvalidDataException("navdata lists no files");
        return new NavDataEntry(id, (int)WholeNumber(nd.GetProperty("release")), CheckUrl(nd.GetProperty("url").GetString()),
            WholeNumber(nd.GetProperty("size")), nd.GetProperty("sha256").GetString() ?? "", files,
            nd.TryGetProperty("bytes", out JsonElement b) ? WholeNumber(b) : 0);
    }

    /// <summary>
    /// Where RynthNav reads its data: "navDataDir" in %APPDATA%\RynthCore\rynthnav.json (the same
    /// file the plugin reads; see the plugin's NavDataConfig), else the default.
    /// </summary>
    public string NavDataDir
    {
        get
        {
            try
            {
                string settings = Path.Combine(_stateDir, "rynthnav.json");
                if (File.Exists(settings))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(settings),
                        new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        foreach (JsonProperty p in doc.RootElement.EnumerateObject())
                            if (p.Name.Equals("navDataDir", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String
                                && p.Value.GetString() is { } dir && Path.IsPathFullyQualified(dir.Trim()))
                                return dir.Trim().TrimEnd('\\', '/');
                }
            }
            catch { }
            return _navDataDirOverride ?? NavDataDefaultDir;
        }
    }

    private string? _navDataDirOverride;
    /// <summary>Where the archive is downloaded to; null = %LOCALAPPDATA%\RynthCore\updates (tests set their own).</summary>
    internal string? NavDataCacheDir { get; set; }
    /// <summary>Tests: the folder to use when rynthnav.json doesn't name one.</summary>
    internal void SetDefaultNavDataDir(string dir) => _navDataDirOverride = dir;

    private NavDataStatus? CheckNavData(Manifest manifest, IEnumerable<string> pluginPaths)
    {
        if (manifest.NavData is not { } e) return null;
        bool hasNav = pluginPaths.Any(p => string.Equals(Path.GetFileName(p), RynthNavFile, StringComparison.OrdinalIgnoreCase) && File.Exists(p));
        if (!hasNav) return null;
        string dir = NavDataDir;
        string installed = "";
        try { if (File.Exists(Path.Combine(dir, NavDataIdFile))) installed = File.ReadAllText(Path.Combine(dir, NavDataIdFile)).Trim().ToLowerInvariant(); }
        catch { }
        NavDataState state = File.Exists(Path.Combine(dir, NavDataLocalMarker)) ? NavDataState.LocalTiles
            : installed == e.Id ? NavDataState.UpToDate : NavDataState.NeedsUpdate;
        return new NavDataStatus(e, dir, installed, state);
    }

    /// <summary>
    /// Downloads, verifies and installs the feed's RynthNav data (see the class notes). Returns a
    /// line for the activity log. Throws on anything wrong, leaving the installed folder as it was.
    /// </summary>
    public const string NavDataDisplayName = "RynthNav map";

    /// <summary>
    /// Cancel is safe at any point before the folder swap: the partial download is deleted, an
    /// unpacked staging folder is removed, and the installed tiles stay as they were. A fully
    /// downloaded, verified archive stays in the cache and is reused by the next try. The swap
    /// itself (two folder renames) doesn't stop half way.
    /// </summary>
    public async Task<string> UpdateNavDataAsync(CheckResult check, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        if (check.NavData is not { State: NavDataState.NeedsUpdate } st) return "";
        NavDataEntry e = st.Entry;
        string dir = st.Dir.TrimEnd('\\', '/');
        string parent = Path.GetDirectoryName(dir) ?? throw new InvalidOperationException($"bad NavData folder {dir}");
        Directory.CreateDirectory(parent);

        // Room for the archive, the unpacked copy and the old folder kept as .previous.
        long need = e.Size + Math.Max(e.Bytes, e.Size * 3) + (64L << 20);
        long free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!).AvailableFreeSpace;
        if (free < need)
            throw new IOException($"not enough disk space for the navmesh tiles: {need >> 20} MB needed, {free >> 20} MB free on {Path.GetPathRoot(dir)}");

        string cache = NavDataCacheDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RynthCore", "updates");
        Directory.CreateDirectory(cache);
        string zip = Path.Combine(cache, $"navdata-{e.Id}.zip");
        if (!(File.Exists(zip) && new FileInfo(zip).Length == e.Size && string.Equals(Sha256File(zip), e.Sha256, StringComparison.OrdinalIgnoreCase)))
        {
            await DownloadVerifiedAsync(e.Url, e.Size, e.Sha256, MaxNavDataBytes, zip + ".part", ct, NavDataDisplayName, progress);
            File.Move(zip + ".part", zip, overwrite: true);
        }

        // Unpacking tens of thousands of files takes a while: off the caller's (UI) thread.
        await Task.Run(() =>
        {
            progress?.Report(new DownloadProgress(NavDataDisplayName, DownloadStage.Unpacking, 0, e.Files));
            string staging = dir + ".new";
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);
            try
            {
                int count = ExtractNavData(zip, staging, ct,
                    n => progress?.Report(new DownloadProgress(NavDataDisplayName, DownloadStage.Unpacking, n, e.Files)));
                if (count != e.Files) throw new InvalidDataException($"the tile archive has {count} files, the feed says {e.Files}");

                // The player's own files (atlas.txt, recalls.txt, …) and anything else not shipped come along.
                if (Directory.Exists(dir)) CopyUnshipped(dir, staging);
                File.WriteAllText(Path.Combine(staging, NavDataIdFile), e.Id);

                // Last chance to cancel: from here the swap runs to the end.
                ct.ThrowIfCancellationRequested();
                progress?.Report(new DownloadProgress(NavDataDisplayName, DownloadStage.Installing, 0, 0));
                SwapNavData(dir, staging);
            }
            catch
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
                throw;
            }
        }, ct);
        try { File.Delete(zip); } catch { }
        return $"RynthNav navmesh tiles updated: {e.Files} files into {dir} (the old ones are in {Path.GetFileName(dir)}.previous). Running clients load the new tiles as they move; a relog picks up all of them.";
    }

    /// <summary>Unpacks only plain, known file names; returns how many files were written.</summary>
    internal static int ExtractNavData(string zipPath, string dest, CancellationToken ct = default, Action<int>? unpacked = null)
    {
        int n = 0;
        var sinceReport = System.Diagnostics.Stopwatch.StartNew();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using ZipArchive za = ZipFile.OpenRead(zipPath);
        foreach (ZipArchiveEntry z in za.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (z.FullName.EndsWith('/') && z.Length == 0) continue;   // a folder entry
            string name = z.FullName;
            if (name != z.Name || !IsShippedNavFile(name))
                throw new InvalidDataException($"unexpected file in the tile archive: '{name}'");
            if (!seen.Add(name)) throw new InvalidDataException($"'{name}' is in the tile archive twice");
            if (z.Length > MaxNavDataEntryBytes) throw new InvalidDataException($"'{name}' is too large ({z.Length} bytes)");
            string target = Path.Combine(dest, name);
            using (Stream src = z.Open())
            using (var f = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] buf = new byte[81920];
                long total = 0; int r;
                while ((r = src.Read(buf, 0, buf.Length)) > 0)
                {
                    total += r;
                    if (total > MaxNavDataEntryBytes) throw new InvalidDataException($"'{name}' unpacks larger than it says");
                    f.Write(buf, 0, r);
                }
            }
            n++;
            if (unpacked != null && sinceReport.Elapsed >= DownloadMeter.ReportEvery)
            {
                unpacked(n);
                sinceReport.Restart();
            }
        }
        unpacked?.Invoke(n);
        return n;
    }

    private static void CopyUnshipped(string from, string to)
    {
        foreach (string f in Directory.GetFiles(from))
        {
            string name = Path.GetFileName(f);
            if (IsShippedNavFile(name) || name.Equals(NavDataIdFile, StringComparison.OrdinalIgnoreCase)) continue;
            string target = Path.Combine(to, name);
            if (!File.Exists(target)) File.Copy(f, target);
        }
        foreach (string d in Directory.GetDirectories(from))
        {
            string target = Path.Combine(to, Path.GetFileName(d));
            Directory.CreateDirectory(target);
            CopyTree(d, target);
        }
    }

    private static void CopyTree(string from, string to)
    {
        foreach (string f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: false);
        foreach (string d in Directory.GetDirectories(from))
        {
            string t = Path.Combine(to, Path.GetFileName(d));
            Directory.CreateDirectory(t);
            CopyTree(d, t);
        }
    }

    // dir -> dir.previous, staging -> dir. A client reading a tile at that moment holds it open for
    // a few milliseconds, so each move is retried; if the second fails the first is undone.
    private static void SwapNavData(string dir, string staging)
    {
        string previous = dir + ".previous";
        bool hadOld = Directory.Exists(dir);
        if (hadOld)
        {
            if (Directory.Exists(previous)) Directory.Delete(previous, recursive: true);
            Retry(() => Directory.Move(dir, previous));
        }
        try { Retry(() => Directory.Move(staging, dir)); }
        catch
        {
            if (hadOld && !Directory.Exists(dir)) { try { Directory.Move(previous, dir); } catch { } }
            throw;
        }
    }

    private static void Retry(Action a)
    {
        for (int i = 0; ; i++)
        {
            try { a(); return; }
            catch (IOException) when (i < 9) { Thread.Sleep(500); }
            catch (UnauthorizedAccessException) when (i < 9) { Thread.Sleep(500); }
        }
    }
}
