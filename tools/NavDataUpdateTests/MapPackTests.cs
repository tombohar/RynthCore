using System.IO.Compression;
using RynthCore.App.Avalonia;

// World map packs (RynthUpdater.MapPacks.cs): the optional HD / Ultra downloads. Part of the same program as
// the NavData tests (same signed feed, same local HTTP server).
internal static partial class Program
{
    private static byte[] Rand(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }

    /// <summary>A feed entry for a pack whose bytes are served under the feed's folder.</summary>
    private static Dictionary<string, object> PackEntry(string tag, string key, string name, string file, byte[] data, string? sha = null, long? size = null)
    {
        lock (Served) Served[$"{tag}/packs/{file}"] = data;
        return new Dictionary<string, object>
        {
            ["key"] = key, ["name"] = name, ["file"] = file, ["url"] = _base + $"{tag}/packs/{file}", ["size"] = size ?? data.Length,
            ["sha256"] = sha ?? Sha(data), ["release"] = 2026100201, ["description"] = name + " test map",
        };
    }

    private static async Task NoMapPacksInFeed()
    {
        var (upd, nav, plugin, _) = SetupInFeedFolder("nopack", null, withNavData: false);
        var c = await upd.CheckAsync(new[] { plugin });
        Check(c.Manifest.MapPacks.Count == 0 && c.MapPacks.Count == 0, "a feed without mappacks (every feed so far) still parses, nothing to offer");
    }

    private static async Task MapPacksListedInstalledAndRemoved()
    {
        byte[] hd = Rand(300_000, 1), ul = Rand(500_000, 2);
        var packs = new object[] { PackEntry("packs", "hd", "HD", "worldmap-hd.rmap", hd), PackEntry("packs", "ultra", "Ultra", "worldmap-ultra.rmap", ul) };
        var (upd, nav, plugin, _) = SetupInFeedFolder("packs", null, withNavData: false, mapPacks: packs);
        var c = await upd.CheckAsync(new[] { plugin });
        Check(c.MapPacks.Count == 2 && c.MapPacks.All(p => p.State == RynthUpdater.MapPackState.NotInstalled), "two packs offered, neither installed");
        string dir = upd.MapPacksDir;
        Check(dir == Path.Combine(Path.GetDirectoryName(nav)!, "MapPacks"), "packs live beside the NavData folder, not inside it");

        string line = await upd.InstallMapPackAsync(c.MapPacks[0].Entry);
        string file = Path.Combine(dir, "worldmap-hd.rmap");
        Check(File.Exists(file) && File.ReadAllBytes(file).AsSpan().SequenceEqual(hd), "the HD pack is installed, byte for byte");
        Check(!File.Exists(file + ".part"), "no partial file is left");
        Check(line.Contains("HD") && line.Contains("installed"), "the activity line says what happened");
        var again = await upd.CheckAsync(new[] { plugin });
        Check(again.MapPacks[0].State == RynthUpdater.MapPackState.UpToDate && again.MapPacks[1].State == RynthUpdater.MapPackState.NotInstalled, "HD is current, Ultra still not installed");

        Check(upd.RemoveMapPack(again.MapPacks[0].Entry), "removing reports it was there");
        Check(!File.Exists(file) && !File.Exists(file + ".sha256"), "the pack and its marker are gone");
        Check(!upd.RemoveMapPack(again.MapPacks[0].Entry), "removing again reports it was not there");

        // A file put there by hand, the size the feed says and no marker, counts as installed.
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "worldmap-ultra.rmap"), ul);
        var hand = await upd.CheckAsync(new[] { plugin });
        Check(hand.MapPacks[1].State == RynthUpdater.MapPackState.UpToDate, "a hand-copied pack of the right size counts as installed");
        File.WriteAllBytes(Path.Combine(dir, "worldmap-ultra.rmap"), new byte[10]);
        var wrong = await upd.CheckAsync(new[] { plugin });
        Check(wrong.MapPacks[1].State == RynthUpdater.MapPackState.NeedsUpdate, "one of the wrong size is an update");
    }

    private static async Task MapPackNewBuildIsAnUpdate()
    {
        byte[] v1 = Rand(200_000, 3), v2 = Rand(210_000, 4);
        var (upd, nav, plugin, _) = SetupInFeedFolder("packupd", null, withNavData: false, mapPacks: new object[] { PackEntry("packupd", "hd", "HD", "worldmap-hd.rmap", v1) });
        var c = await upd.CheckAsync(new[] { plugin });
        await upd.InstallMapPackAsync(c.MapPacks[0].Entry);
        // The feed now carries a new build of the same pack.
        var (upd2, _, plugin2, _) = SetupInFeedFolder("packupd", null, withNavData: false, mapPacks: new object[] { PackEntry("packupd", "hd", "HD", "worldmap-hd.rmap", v2) });
        var c2 = await upd2.CheckAsync(new[] { plugin2 });
        Check(c2.MapPacks[0].State == RynthUpdater.MapPackState.NeedsUpdate, "a new build of an installed pack is an update");
        await upd2.InstallMapPackAsync(c2.MapPacks[0].Entry);
        Check(File.ReadAllBytes(Path.Combine(upd2.MapPacksDir, "worldmap-hd.rmap")).AsSpan().SequenceEqual(v2), "the update replaced the file");
        var c3 = await upd2.CheckAsync(new[] { plugin2 });
        Check(c3.MapPacks[0].State == RynthUpdater.MapPackState.UpToDate, "and it is current afterwards");
    }

    private static async Task MapPackBadDownloadChangesNothing()
    {
        byte[] good = Rand(150_000, 5), served = Rand(150_000, 6);
        // The feed says the SHA-256 of one file; another is served.
        var entry = PackEntry("packbad", "hd", "HD", "worldmap-hd.rmap", served, sha: Sha(good));
        var (upd, nav, plugin, _) = SetupInFeedFolder("packbad", null, withNavData: false, mapPacks: new object[] { entry });
        var c = await upd.CheckAsync(new[] { plugin });
        string file = Path.Combine(upd.MapPacksDir, "worldmap-hd.rmap");
        bool threw = false;
        try { await upd.InstallMapPackAsync(c.MapPacks[0].Entry); } catch (Exception) { threw = true; }
        Check(threw, "a download that does not match the feed's SHA-256 is refused");
        Check(!File.Exists(file) && !File.Exists(file + ".part") && !File.Exists(file + ".sha256"), "nothing was installed and no partial file is left");

        // And an installed pack stays as it was when its update is refused.
        Directory.CreateDirectory(upd.MapPacksDir);
        File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
        threw = false;
        try { await upd.InstallMapPackAsync(c.MapPacks[0].Entry); } catch (Exception) { threw = true; }
        Check(threw && File.ReadAllBytes(file).SequenceEqual(new byte[] { 1, 2, 3 }), "a refused update leaves the installed pack as it was");
    }

    private static async Task MapPackBadEntriesAreSkipped()
    {
        byte[] data = Rand(1000, 7);
        var good = PackEntry("packskip", "hd", "HD", "worldmap-hd.rmap", data);
        var traversal = PackEntry("packskip", "evil", "Evil", "../worldmap-evil.rmap", data);
        var wrongKind = PackEntry("packskip", "dll", "Dll", "worldmap-x.dll", data);
        var badKey = PackEntry("packskip", "BAD KEY!", "Bad", "worldmap-bad.rmap", data);
        var shortSha = PackEntry("packskip", "short", "Short", "worldmap-short.rmap", data, sha: "abc");
        var duplicate = PackEntry("packskip", "hd", "HD again", "worldmap-hd.rmap", data);
        var huge = PackEntry("packskip", "huge", "Huge", "worldmap-huge.rmap", data, size: 9L << 40);
        var (upd, nav, plugin, _) = SetupInFeedFolder("packskip", null, withNavData: false, mapPacks: new object[] { traversal, wrongKind, badKey, shortSha, good, duplicate, huge });
        var c = await upd.CheckAsync(new[] { plugin });
        Check(c.MapPacks.Count == 1 && c.MapPacks[0].Entry.Key == "hd", "entries with a path in the file name, a wrong extension, a bad key, a short hash, a repeated key or an absurd size are skipped; the good one stays");
    }

    private static async Task MapPackCancelLeavesNothing()
    {
        byte[] data = Rand(400_000, 8);
        var (upd, nav, plugin, _) = SetupInFeedFolder("packcancel", null, withNavData: false, mapPacks: new object[] { PackEntry("packcancel", "hd", "HD", "worldmap-hd.rmap", data) });
        var c = await upd.CheckAsync(new[] { plugin });
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        bool cancelled = false;
        try { await upd.InstallMapPackAsync(c.MapPacks[0].Entry, null, cts.Token); } catch (OperationCanceledException) { cancelled = true; }
        string file = Path.Combine(upd.MapPacksDir, "worldmap-hd.rmap");
        Check(cancelled, "cancelling stops the install");
        Check(!File.Exists(file) && !File.Exists(file + ".part"), "and leaves neither the pack nor a partial file");
    }

    private static async Task NavDataUpdateLeavesMapPacksAlone()
    {
        byte[] hd = Rand(120_000, 9);
        var (upd, nav, plugin, _) = SetupInFeedFolder("packnav", Zip(TileSet), mapPacks: new object[] { PackEntry("packnav", "hd", "HD", "worldmap-hd.rmap", hd) });
        OldNavData(nav);
        var c = await upd.CheckAsync(new[] { plugin });
        await upd.InstallMapPackAsync(c.MapPacks[0].Entry);
        Check(c.NavDataUpdateAvailable, "the tiles need an update");
        await upd.UpdateNavDataAsync(c);
        string file = Path.Combine(upd.MapPacksDir, "worldmap-hd.rmap");
        Check(File.Exists(file) && File.ReadAllBytes(file).AsSpan().SequenceEqual(hd) && File.Exists(file + ".sha256"), "updating the tiles swaps the NavData folder but the map pack beside it is untouched");
        Check(!Directory.Exists(Path.Combine(nav, "MapPacks")), "and no MapPacks folder appears inside NavData");
    }
}
