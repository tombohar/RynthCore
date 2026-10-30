using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RynthCore.App.Avalonia;

// Tests for the launcher's RynthNav tile update. A throwaway signing key signs a feed served by
// a local HttpListener; the updater installs the archive into a temp NavData folder.
internal static partial class Program
{
    private static int _fails, _asserts;
    private static void Check(bool cond, string msg)
    {
        _asserts++;
        if (!cond) { _fails++; Console.WriteLine($"  [FAIL] {msg}"); }
    }

    private static string _root = "";
    private static string _base = "";
    private static readonly Dictionary<string, byte[]> Served = new();
    private static ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static async Task<int> Main()
    {
        _root = Path.Combine(Path.GetTempPath(), "rnav-upd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        int port = FreePort();
        _base = $"http://localhost:{port}/rynth/";
        using var http = new HttpListener();
        http.Prefixes.Add(_base);
        http.Start();
        _ = Task.Run(() => Serve(http));
        try
        {
            await Run(nameof(NoNavDataInFeed), NoNavDataInFeed);
            await Run(nameof(NotInstalledWithoutRynthNav), NotInstalledWithoutRynthNav);
            await Run(nameof(InstallsAndKeepsPlayerFiles), InstallsAndKeepsPlayerFiles);
            await Run(nameof(LocalTilesLeftAlone), LocalTilesLeftAlone);
            await Run(nameof(SettingsFolderIsUsed), SettingsFolderIsUsed);
            await Run(nameof(RejectsBadArchive), RejectsBadArchive);
            await Run(nameof(RejectsTamperedArchive), RejectsTamperedArchive);
            await Run(nameof(RejectsUnsignedNavData), RejectsUnsignedNavData);
            await Run(nameof(ReportsProgressToTheEnd), ReportsProgressToTheEnd);
            await Run(nameof(CancelledDownloadLeavesNothing), CancelledDownloadLeavesNothing);
            await Run(nameof(CancelledUnpackLeavesTilesAsTheyWere), CancelledUnpackLeavesTilesAsTheyWere);
            await Run(nameof(TamperedArchiveRefusedWithProgress), TamperedArchiveRefusedWithProgress);
            await Run(nameof(NoMapPacksInFeed), NoMapPacksInFeed);
            await Run(nameof(MapPacksListedInstalledAndRemoved), MapPacksListedInstalledAndRemoved);
            await Run(nameof(MapPackNewBuildIsAnUpdate), MapPackNewBuildIsAnUpdate);
            await Run(nameof(MapPackBadDownloadChangesNothing), MapPackBadDownloadChangesNothing);
            await Run(nameof(MapPackBadEntriesAreSkipped), MapPackBadEntriesAreSkipped);
            await Run(nameof(MapPackCancelLeavesNothing), MapPackCancelLeavesNothing);
            await Run(nameof(NavDataUpdateLeavesMapPacksAlone), NavDataUpdateLeavesMapPacksAlone);
        }
        finally
        {
            http.Stop();
            try { Directory.Delete(_root, true); } catch { }
        }
        Console.WriteLine();
        Console.WriteLine(_fails == 0 && _asserts > 0 ? $"PASS ({_asserts} assertions)" : $"FAIL ({_fails} of {_asserts} assertions failed)");
        return _fails == 0 && _asserts > 0 ? 0 : 1;
    }

    private static async Task Run(string name, Func<Task> test)
    {
        int before = _fails;
        try { await test(); }
        catch (Exception ex) { _fails++; _asserts++; Console.WriteLine($"  [FAIL] {name} threw {ex.GetType().Name}: {ex.Message}"); }
        Console.WriteLine($"{(_fails == before ? "ok  " : "FAIL")} {name}");
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static async Task Serve(HttpListener http)
    {
        while (http.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await http.GetContextAsync(); } catch { return; }
            string name = ctx.Request.Url!.AbsolutePath.Replace("/rynth/", "");
            byte[]? body;
            lock (Served) Served.TryGetValue(name, out body);
            if (body == null) { ctx.Response.StatusCode = 404; ctx.Response.Close(); continue; }
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentLength64 = body.Length;
            HttpListenerContext c = ctx;
            byte[] b = body;
            // Big files go out in pieces with a pause between them, like a real connection, so a
            // download is still running when progress is reported (and can be cancelled half way).
            _ = Task.Run(async () =>
            {
                try
                {
                    for (int off = 0; off < b.Length; off += 64 * 1024)
                    {
                        int n = Math.Min(64 * 1024, b.Length - off);
                        await c.Response.OutputStream.WriteAsync(b.AsMemory(off, n));
                        if (b.Length > 512 * 1024) await Task.Delay(25);
                    }
                }
                catch { }
                finally { try { c.Response.Close(); } catch { } }
            });
        }
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    /// <summary>A zip of the given files (name -> content).</summary>
    private static byte[] Zip(Dictionary<string, string> files)
    {
        using var ms = new MemoryStream();
        using (var za = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (n, c) in files)
            {
                using var s = za.CreateEntry(n).Open();
                s.Write(Encoding.UTF8.GetBytes(c));
            }
        return ms.ToArray();
    }

    private static readonly Dictionary<string, string> TileSet = new()
    {
        ["nav_A9B4.tile"] = "tile A9B4 v2",
        ["nav_A9B5.tile"] = "tile A9B5 v2",
        ["nav_AAB4.tile"] = "tile AAB4 v2",
        ["portals.tsv"] = "1\t2\t3\t4\tportal",
        ["locations.json"] = "{\"locations\":[]}",
        ["navgraph.bin"] = "route graph",
        ["townnet.json"] = "{\"version\":1}",
        ["worldmap.rmap"] = "world map",
        ["worldmap.vec"] = "world map vectors",
    };

    // The feed's URLs must sit in the feed's folder: serve each test's archive under it.
    private static (RynthUpdater upd, string nav, string plugin, string state) SetupInFeedFolder(string tag, byte[]? zip, string id = "0123456789abcdef0123456789abcdef",
        int? files = null, bool signNavData = true, bool withNavData = true, string? overrideSha = null, object[]? mapPacks = null)
    {
        string zipName = $"{tag}/releases/1/RynthNav-NavData.zip";
        string dir = Path.Combine(_root, tag);
        string state = Path.Combine(dir, "state"), app = Path.Combine(dir, "app"), nav = Path.Combine(dir, "NavData"), cache = Path.Combine(dir, "cache");
        Directory.CreateDirectory(state); Directory.CreateDirectory(app);
        string plugin = Path.Combine(dir, "RynthNav", "RynthCore.Plugin.RynthNav.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(plugin)!);
        File.WriteAllText(plugin, "not a real dll");
        if (zip != null) lock (Served) Served[zipName] = zip;
        var manifest = new Dictionary<string, object?>
        {
            ["schema"] = 1, ["release"] = 2026100201, ["version"] = "2026.10.2.1",
            ["published"] = "2026-10-02T00:00:00Z", ["notes"] = "test",
            ["core"] = new Dictionary<string, object> { ["release"] = 2026100201, ["version"] = "2026.10.2.1", ["url"] = _base + $"{tag}/releases/1/RynthCore-Setup.exe", ["size"] = 1, ["sha256"] = "00" },
            ["plugins"] = Array.Empty<object>(),
        };
        var nd = zip == null ? null : new Dictionary<string, object>
        {
            ["id"] = id, ["release"] = 2026100201, ["url"] = _base + zipName, ["size"] = zip.Length,
            ["sha256"] = overrideSha ?? Sha(zip), ["files"] = files ?? TileSet.Count, ["bytes"] = 1000,
        };
        if (mapPacks != null) manifest["mappacks"] = mapPacks;
        if (withNavData && nd != null && signNavData) manifest["navdata"] = nd;
        byte[] signedPayload = JsonSerializer.SerializeToUtf8Bytes(manifest);
        if (withNavData && nd != null && !signNavData) manifest["navdata"] = nd;   // served, but not what was signed
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(manifest);
        byte[] sig = _key.SignData(signedPayload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        byte[] spki = _key.ExportSubjectPublicKeyInfo();
        string envelope = JsonSerializer.Serialize(new { payload = Convert.ToBase64String(payload), signatures = new[] { new { key = RynthUpdater.KeyId(spki), sig = Convert.ToBase64String(sig) } } });
        lock (Served) Served[$"{tag}/update.json"] = Encoding.UTF8.GetBytes(envelope);
        var upd = new RynthUpdater(_base + $"{tag}/update.json", new[] { Convert.ToBase64String(spki) }, state, app);
        upd.SetDefaultNavDataDir(nav);
        upd.NavDataCacheDir = cache;
        return (upd, nav, plugin, state);
    }

    private static void OldNavData(string nav)
    {
        Directory.CreateDirectory(nav);
        File.WriteAllText(Path.Combine(nav, "nav_A9B4.tile"), "tile A9B4 June");
        File.WriteAllText(Path.Combine(nav, "nav_0101.tile"), "a stale June tile not in the new set");
        File.WriteAllText(Path.Combine(nav, "portals.tsv"), "old portals");
        File.WriteAllText(Path.Combine(nav, "atlas.txt"), "my favorites");
        File.WriteAllText(Path.Combine(nav, "recalls.txt"), "my recalls");
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    private static async Task NoNavDataInFeed()
    {
        var (upd, nav, plugin, _) = SetupInFeedFolder("none", Zip(TileSet), withNavData: false);
        var c = await upd.CheckAsync(new[] { plugin });
        Check(c.Manifest.NavData == null && c.NavData == null && !c.NavDataUpdateAvailable, "a feed without navdata (every feed so far) still parses, nothing to do");
    }

    private static async Task NotInstalledWithoutRynthNav()
    {
        var (upd, nav, plugin, _) = SetupInFeedFolder("nonav", Zip(TileSet));
        var c = await upd.CheckAsync(new[] { Path.Combine(Path.GetDirectoryName(plugin)!, "RynthCore.Plugin.RynthAi.dll") });
        Check(c.Manifest.NavData != null, "the feed's navdata parses");
        Check(c.NavData == null, "no RynthNav in the plugin list: no tiles");
    }

    private static async Task InstallsAndKeepsPlayerFiles()
    {
        var (upd, nav, plugin, _) = SetupInFeedFolder("install", Zip(TileSet));
        OldNavData(nav);
        var c = await upd.CheckAsync(new[] { plugin });
        Check(c.NavData is { State: RynthUpdater.NavDataState.NeedsUpdate }, "June tiles (no navdata.id): update needed");
        string line = await upd.UpdateNavDataAsync(c);
        Check(line.Contains($"{TileSet.Count} files"), "reports what it installed: " + line);
        Check(File.ReadAllText(Path.Combine(nav, "navgraph.bin")) == "route graph", "the route graph ships with the tiles");
        Check(File.ReadAllText(Path.Combine(nav, "townnet.json")) == "{\"version\":1}", "the Town Network ships with the tiles");
        Check(File.ReadAllText(Path.Combine(nav, "worldmap.rmap")) == "world map", "the World Map's tiles ship with the tiles");
        Check(File.ReadAllText(Path.Combine(nav, "worldmap.vec")) == "world map vectors", "the World Map's buildings and roads ship with the tiles");
        Check(File.ReadAllText(Path.Combine(nav, "nav_A9B4.tile")) == "tile A9B4 v2", "the tile is the new one");
        Check(File.Exists(Path.Combine(nav, "nav_AAB4.tile")), "new tiles are there");
        Check(!File.Exists(Path.Combine(nav, "nav_0101.tile")), "a stale tile from the old set is gone (old and new don't link)");
        Check(File.ReadAllText(Path.Combine(nav, "portals.tsv")).Contains("portal"), "portals.tsv replaced");
        Check(File.ReadAllText(Path.Combine(nav, "atlas.txt")) == "my favorites", "atlas.txt kept");
        Check(File.ReadAllText(Path.Combine(nav, "recalls.txt")) == "my recalls", "recalls.txt kept");
        Check(File.ReadAllText(Path.Combine(nav, "navdata.id")) == "0123456789abcdef0123456789abcdef", "navdata.id written");
        Check(File.ReadAllText(Path.Combine(nav + ".previous", "nav_0101.tile")).Contains("June"), "the old folder is kept as NavData.previous");
        Check(!Directory.Exists(nav + ".new"), "no staging folder left behind");
        c = await upd.CheckAsync(new[] { plugin });
        Check(c.NavData is { State: RynthUpdater.NavDataState.UpToDate } && !c.NavDataUpdateAvailable, "a second check: up to date");
        Check(await upd.UpdateNavDataAsync(c) == "", "nothing to do when up to date");
    }

    private static async Task LocalTilesLeftAlone()
    {
        var (upd, nav, plugin, _) = SetupInFeedFolder("local", Zip(TileSet));
        OldNavData(nav);
        File.WriteAllText(Path.Combine(nav, "navdata.local"), "");
        var c = await upd.CheckAsync(new[] { plugin });
        Check(c.NavData is { State: RynthUpdater.NavDataState.LocalTiles } && !c.NavDataUpdateAvailable, "navdata.local: left alone");
        await upd.UpdateNavDataAsync(c);
        Check(File.ReadAllText(Path.Combine(nav, "nav_A9B4.tile")) == "tile A9B4 June", "local tiles untouched");
    }

    private static async Task SettingsFolderIsUsed()
    {
        var (upd, nav, plugin, state) = SetupInFeedFolder("settings", Zip(TileSet));
        string other = Path.Combine(_root, "settings", "Elsewhere", "NavData");
        File.WriteAllText(Path.Combine(state, "rynthnav.json"), JsonSerializer.Serialize(new { navDataDir = other }));
        var c = await upd.CheckAsync(new[] { plugin });
        Check(c.NavData != null && c.NavData.Dir == other, "rynthnav.json's navDataDir is where the tiles go");
        await upd.UpdateNavDataAsync(c);
        Check(File.Exists(Path.Combine(other, "nav_A9B4.tile")) && !Directory.Exists(nav), "installed there, not in the default");
    }

    private static async Task RejectsBadArchive()
    {
        var evil = new Dictionary<string, string>(TileSet) { ["../evil.txt"] = "x" };
        var (upd, nav, plugin, _) = SetupInFeedFolder("evil", Zip(evil), files: evil.Count);
        OldNavData(nav);
        var c = await upd.CheckAsync(new[] { plugin });
        bool threw = false;
        try { await upd.UpdateNavDataAsync(c); } catch (InvalidDataException) { threw = true; }
        Check(threw, "a path outside the folder is refused");
        Check(!File.Exists(Path.Combine(_root, "evil", "evil.txt")), "nothing written outside");
        Check(File.ReadAllText(Path.Combine(nav, "nav_A9B4.tile")) == "tile A9B4 June" && !Directory.Exists(nav + ".new"), "the installed folder is as it was, no staging left");

        var odd = new Dictionary<string, string>(TileSet) { ["run.exe"] = "x" };
        (upd, nav, plugin, _) = SetupInFeedFolder("odd", Zip(odd), files: odd.Count);
        c = await upd.CheckAsync(new[] { plugin });
        threw = false;
        try { await upd.UpdateNavDataAsync(c); } catch (InvalidDataException) { threw = true; }
        Check(threw, "a file RynthNav doesn't read is refused");

        (upd, nav, plugin, _) = SetupInFeedFolder("count", Zip(TileSet), files: TileSet.Count + 1);
        c = await upd.CheckAsync(new[] { plugin });
        threw = false;
        try { await upd.UpdateNavDataAsync(c); } catch (InvalidDataException) { threw = true; }
        Check(threw, "a file count that doesn't match the feed is refused");
    }

    private static async Task RejectsTamperedArchive()
    {
        byte[] zip = Zip(TileSet);
        var (upd, nav, plugin, _) = SetupInFeedFolder("tamper", zip);
        OldNavData(nav);
        var c = await upd.CheckAsync(new[] { plugin });
        byte[] bad = (byte[])zip.Clone(); bad[bad.Length / 2] ^= 0xFF;
        lock (Served) Served["tamper/releases/1/RynthNav-NavData.zip"] = bad;
        bool threw = false;
        try { await upd.UpdateNavDataAsync(c); } catch (InvalidDataException) { threw = true; }
        Check(threw, "an archive that doesn't match the signed hash is refused");
        Check(File.ReadAllText(Path.Combine(nav, "nav_A9B4.tile")) == "tile A9B4 June", "and nothing changes");
    }

    /// <summary>Calls back on the reporting thread (Progress&lt;T&gt; would post to the thread pool).</summary>
    private sealed class SyncProgress(Action<DownloadProgress> onReport) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => onReport(value);
    }

    /// <summary>The tile set plus an incompressible file, so the archive takes many reads.</summary>
    private static Dictionary<string, string> BigTileSet()
    {
        var rnd = new Random(42);
        var sb = new StringBuilder();
        for (int i = 0; i < 1_500_000; i++) sb.Append((char)rnd.Next(0x21, 0x7E));
        return new Dictionary<string, string>(TileSet) { ["navgraph.bin"] = sb.ToString() };
    }

    private static async Task ReportsProgressToTheEnd()
    {
        var big = BigTileSet();
        byte[] zip = Zip(big);
        var (upd, nav, plugin, _) = SetupInFeedFolder("progress", zip);
        var c = await upd.CheckAsync(new[] { plugin });
        var reports = new List<DownloadProgress>();
        await upd.UpdateNavDataAsync(c, new SyncProgress(p => { lock (reports) reports.Add(p); }));
        var dl = reports.Where(r => r.Stage == DownloadStage.Downloading).ToList();
        Check(dl.Count >= 2, $"download progress reported ({dl.Count} reports)");
        Check(dl.All(r => r.What == RynthUpdater.NavDataDisplayName && r.Total == zip.Length), "each names the map and the signed size");
        Check(dl.Zip(dl.Skip(1)).All(p => p.Second.Done >= p.First.Done), "bytes only go up");
        Check(dl.Last().Done == zip.Length && dl.Last().Percent == 100, "the download reaches 100%");
        var verified = reports.FirstOrDefault(r => r.Stage == DownloadStage.Verified);
        Check(verified != null && verified.Summary.StartsWith("RynthNav map: ") && verified.Summary.EndsWith(", verified"), "a Verified step with the log line: " + verified?.Summary);
        var unpack = reports.Where(r => r.Stage == DownloadStage.Unpacking).ToList();
        Check(unpack.Count >= 1 && unpack.Last().Done == big.Count && unpack.Last().Total == big.Count, "unpacking counts to every file");
        Check(reports.Last().Stage == DownloadStage.Installing, "installing is the last step");
        int vi = reports.IndexOf(verified!), ui = reports.FindIndex(r => r.Stage == DownloadStage.Unpacking);
        Check(vi > 0 && ui > vi, "steps in order: download, verify, unpack, install");
        Check(File.Exists(Path.Combine(nav, "navgraph.bin")), "and it is installed");
    }

    private static async Task CancelledDownloadLeavesNothing()
    {
        byte[] zip = Zip(BigTileSet());
        var (upd, nav, plugin, _) = SetupInFeedFolder("cancel", zip);
        OldNavData(nav);
        var c = await upd.CheckAsync(new[] { plugin });
        using var cts = new CancellationTokenSource();
        bool threw = false, halfWay = false;
        string cachePart = Path.Combine(_root, "cancel", "cache", "navdata-0123456789abcdef0123456789abcdef.zip.part");
        bool partExisted = false;
        try
        {
            await upd.UpdateNavDataAsync(c, new SyncProgress(p =>
            {
                if (p.Stage == DownloadStage.Downloading && p.Done > 0 && p.Done < p.Total)
                {
                    halfWay = true;
                    partExisted = File.Exists(cachePart);
                    cts.Cancel();
                }
            }), cts.Token);
        }
        catch (OperationCanceledException) { threw = true; }
        Check(halfWay && partExisted, "cancelled half way, with a partial file on disk at that moment");
        Check(threw, "Cancel during the download stops it");
        string cache = Path.Combine(_root, "cancel", "cache");
        Check(!Directory.Exists(cache) || Directory.GetFiles(cache).Length == 0, "no partial (or any) archive left in the cache");
        Check(File.ReadAllText(Path.Combine(nav, "nav_A9B4.tile")) == "tile A9B4 June" && !Directory.Exists(nav + ".new"), "the installed tiles are as they were, no staging");

        // The next try downloads and installs normally.
        c = await upd.CheckAsync(new[] { plugin });
        await upd.UpdateNavDataAsync(c);
        Check(File.ReadAllText(Path.Combine(nav, "nav_A9B4.tile")) == "tile A9B4 v2", "a later try installs");
    }

    private static async Task CancelledUnpackLeavesTilesAsTheyWere()
    {
        byte[] zip = Zip(BigTileSet());
        var (upd, nav, plugin, _) = SetupInFeedFolder("cancelunpack", zip);
        OldNavData(nav);
        var c = await upd.CheckAsync(new[] { plugin });
        using var cts = new CancellationTokenSource();
        bool threw = false;
        try { await upd.UpdateNavDataAsync(c, new SyncProgress(p => { if (p.Stage == DownloadStage.Unpacking) cts.Cancel(); }), cts.Token); }
        catch (OperationCanceledException) { threw = true; }
        Check(threw, "Cancel while unpacking stops it");
        Check(File.ReadAllText(Path.Combine(nav, "nav_A9B4.tile")) == "tile A9B4 June" && File.Exists(Path.Combine(nav, "nav_0101.tile")), "the installed tiles are as they were");
        Check(!Directory.Exists(nav + ".new") && !Directory.Exists(nav + ".previous"), "no staging folder, no swap");
        string cache = Path.Combine(_root, "cancelunpack", "cache");
        Check(Directory.GetFiles(cache, "*.part").Length == 0, "no partial file in the cache (the verified archive may stay for the next try)");
    }

    private static async Task TamperedArchiveRefusedWithProgress()
    {
        byte[] zip = Zip(BigTileSet());
        var (upd, nav, plugin, _) = SetupInFeedFolder("tamperprog", zip);
        OldNavData(nav);
        var c = await upd.CheckAsync(new[] { plugin });
        byte[] bad = (byte[])zip.Clone(); bad[bad.Length / 3] ^= 0x55;
        lock (Served) Served["tamperprog/releases/1/RynthNav-NavData.zip"] = bad;
        var reports = new List<DownloadProgress>();
        bool threw = false;
        try { await upd.UpdateNavDataAsync(c, new SyncProgress(reports.Add)); } catch (InvalidDataException) { threw = true; }
        Check(threw, "with progress on, a wrong hash is still refused");
        Check(!reports.Any(r => r.Stage is DownloadStage.Verified or DownloadStage.Unpacking), "never reported as verified, never unpacked");
        string cache = Path.Combine(_root, "tamperprog", "cache");
        Check(Directory.GetFiles(cache).Length == 0, "the bad download is deleted");
        Check(File.ReadAllText(Path.Combine(nav, "nav_A9B4.tile")) == "tile A9B4 June", "and nothing changes");
    }

    private static async Task RejectsUnsignedNavData()
    {
        var (upd, nav, plugin, _) = SetupInFeedFolder("unsigned", Zip(TileSet), signNavData: false);
        bool threw = false;
        try { await upd.CheckAsync(new[] { plugin }); } catch (InvalidDataException) { threw = true; }
        Check(threw, "a navdata entry added after signing breaks the signature");
    }
}
