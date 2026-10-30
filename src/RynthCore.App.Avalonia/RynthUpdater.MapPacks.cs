using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RynthCore.App.Avalonia;

/// <summary>
/// The World Map's optional sizes. The Standard map ships in the NavData archive like the tiles; the bigger
/// ones (worldmap-hd.rmap, worldmap-ultra.rmap: tens to hundreds of MB) are separate, optional downloads the
/// player picks on the launcher's front page, each a single file the feed lists under "mappacks":
///   { "key": "hd", "name": "HD", "file": "worldmap-hd.rmap", "url": "...", "size": N, "sha256": "...",
///     "release": N, "description": "..." }
/// A pack is downloaded to MapPacks\&lt;file&gt;.part, checked against the feed's size and SHA-256, and moved into
/// place; MapPacks\&lt;file&gt;.sha256 records which build is installed (so a changed pack shows as an update).
/// MapPacks sits beside the NavData folder (C:\Games\RynthCore\MapPacks by default), not inside it, because
/// a NavData update swaps that whole folder. The engine's World Map uses the best map it finds in either
/// place, so installing a pack needs no restart; a client picks it up when the panel next opens.
/// </summary>
internal sealed partial class RynthUpdater
{
    public const string MapPackDisplayName = "World map";
    public const string MapPacksFolder = "MapPacks";
    private const long MaxMapPackBytes = 4L << 30;

    private static readonly Regex MapPackKey = new(@"^[a-z0-9]{1,16}$", RegexOptions.CultureInvariant);
    private static readonly Regex MapPackFile = new(@"^worldmap-[a-z0-9]{1,16}\.rmap$", RegexOptions.CultureInvariant);

    public sealed record MapPackEntry(string Key, string Name, string File, string Url, long Size, string Sha256, int Release, string Description);
    public enum MapPackState { NotInstalled, UpToDate, NeedsUpdate }
    public sealed record MapPackStatus(MapPackEntry Entry, string Path, MapPackState State);

    /// <summary>Where the packs are installed: beside the NavData folder.</summary>
    public string MapPacksDir
    {
        get
        {
            string nav = NavDataDir.TrimEnd('\\', '/');
            return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(nav) ?? nav, MapPacksFolder);
        }
    }

    /// <summary>The feed's map packs. One that can't be read is left out (the rest still show).</summary>
    private IReadOnlyList<MapPackEntry> ReadMapPacks(JsonElement root)
    {
        var list = new List<MapPackEntry>();
        if (!root.TryGetProperty("mappacks", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array) return list;
        var keys = new HashSet<string>();
        foreach (JsonElement p in arr.EnumerateArray())
        {
            try
            {
                string key = (p.GetProperty("key").GetString() ?? "").ToLowerInvariant();
                string file = p.GetProperty("file").GetString() ?? "";
                if (!MapPackKey.IsMatch(key) || !MapPackFile.IsMatch(file) || !keys.Add(key)) continue;
                long size = WholeNumber(p.GetProperty("size"));
                if (size <= 0 || size > MaxMapPackBytes) continue;
                string sha = (p.GetProperty("sha256").GetString() ?? "").ToLowerInvariant();
                if (sha.Length != 64) continue;
                list.Add(new MapPackEntry(key, p.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? key : key, file,
                    CheckUrl(p.GetProperty("url").GetString()), size, sha,
                    p.TryGetProperty("release", out JsonElement rel) ? (int)WholeNumber(rel) : 0,
                    p.TryGetProperty("description", out JsonElement d) ? d.GetString() ?? "" : ""));
            }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or InvalidOperationException or KeyNotFoundException)
            {
                System.Diagnostics.Debug.WriteLine($"RynthUpdater: a map pack entry is unreadable ({ex.Message}); skipped.");
            }
        }
        return list;
    }

    private IReadOnlyList<MapPackStatus> CheckMapPacks(Manifest manifest)
    {
        string dir = MapPacksDir;
        var list = new List<MapPackStatus>();
        foreach (MapPackEntry e in manifest.MapPacks)
        {
            string path = System.IO.Path.Combine(dir, e.File);
            MapPackState state;
            try
            {
                if (!File.Exists(path)) state = MapPackState.NotInstalled;
                else
                {
                    string marker = path + ".sha256";
                    string installed = File.Exists(marker) ? File.ReadAllText(marker).Trim().ToLowerInvariant() : "";
                    // No marker: a file put there by hand counts as current if it is the size the feed says.
                    state = installed.Length > 0 ? (installed == e.Sha256 ? MapPackState.UpToDate : MapPackState.NeedsUpdate)
                        : new FileInfo(path).Length == e.Size ? MapPackState.UpToDate : MapPackState.NeedsUpdate;
                }
            }
            catch { state = MapPackState.NotInstalled; }
            list.Add(new MapPackStatus(e, path, state));
        }
        return list;
    }

    /// <summary>
    /// Downloads, verifies and installs a map pack (or replaces an older build of it). Returns a line for the
    /// activity log. Throws on anything wrong (disk space, a bad download); the file that was there stays as it
    /// was. Cancel deletes the partial download.
    /// </summary>
    public async Task<string> InstallMapPackAsync(MapPackEntry e, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        string dir = MapPacksDir;
        Directory.CreateDirectory(dir);
        // The pack is downloaded beside where it goes, so the finishing move is a rename.
        long need = e.Size + (64L << 20);
        long free = new DriveInfo(System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(dir))!).AvailableFreeSpace;
        if (free < need)
            throw new IOException($"not enough disk space for the {e.Name} map: {need >> 20} MB needed, {free >> 20} MB free on {System.IO.Path.GetPathRoot(dir)}");

        string target = System.IO.Path.Combine(dir, e.File), part = target + ".part";
        string what = $"{MapPackDisplayName} ({e.Name})";
        try
        {
            await DownloadVerifiedAsync(e.Url, e.Size, e.Sha256, MaxMapPackBytes, part, ct, what, progress);
            ct.ThrowIfCancellationRequested();
            progress?.Report(new DownloadProgress(what, DownloadStage.Installing, 0, 0));
            Retry(() => File.Move(part, target, overwrite: true));      // a client may have it open for a moment
            File.WriteAllText(target + ".sha256", e.Sha256);
        }
        catch
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { }
            throw;
        }
        return $"{MapPackDisplayName} ({e.Name}) installed: {target} ({DownloadProgress.FormatMb(e.Size)}). The World Map uses the best installed size; a map that is already open picks it up when you reopen it.";
    }

    /// <summary>Deletes an installed map pack (and its marker). Returns false when it wasn't there.</summary>
    public bool RemoveMapPack(MapPackEntry e)
    {
        string target = System.IO.Path.Combine(MapPacksDir, e.File);
        bool had = File.Exists(target);
        Retry(() => { if (File.Exists(target)) File.Delete(target); });
        try { if (File.Exists(target + ".sha256")) File.Delete(target + ".sha256"); } catch { }
        try { if (File.Exists(target + ".part")) File.Delete(target + ".part"); } catch { }
        return had;
    }
}
