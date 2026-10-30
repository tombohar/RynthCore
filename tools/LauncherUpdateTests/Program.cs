using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RynthCore.App.Avalonia;

// Tests for the launcher's Available plugins list, plugin installs (verified, with progress and
// Cancel), Remove, old feeds without descriptions, the download meter, and the front page's
// update card. A throwaway signing key signs feeds served by a local HttpListener.
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
    private static readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly DateTime Published = new(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);

    private static async Task<int> Main()
    {
        _root = Path.Combine(Path.GetTempPath(), "rlaunch-upd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        int port = FreePort();
        _base = $"http://localhost:{port}/rynth/";
        using var http = new HttpListener();
        http.Prefixes.Add(_base);
        http.Start();
        _ = Task.Run(() => Serve(http));
        try
        {
            await Run(nameof(AvailableIsFeedMinusInstalled), AvailableIsFeedMinusInstalled);
            await Run(nameof(PluginNamesAndServerSplit), () => { PluginNamesAndServerSplit(); return Task.CompletedTask; });
            await Run(nameof(OnlyRynthLuaIsAutomatic), OnlyRynthLuaIsAutomatic);
            await Run(nameof(OldManifestWithoutDescriptions), OldManifestWithoutDescriptions);
            await Run(nameof(DescriptionsAreOneShortLine), DescriptionsAreOneShortLine);
            await Run(nameof(NewBadgeAndOneAnnouncement), NewBadgeAndOneAnnouncement);
            await Run(nameof(NotNewForPlayersWhoHadThem), NotNewForPlayersWhoHadThem);
            await Run(nameof(InstallDownloadsVerifiesRegisters), InstallDownloadsVerifiesRegisters);
            await Run(nameof(InstallRefusesTamperedFile), InstallRefusesTamperedFile);
            await Run(nameof(InstallOnlyFromTheFeed), InstallOnlyFromTheFeed);
            await Run(nameof(InstallKeepsGoodCopyReplacesOldOne), InstallKeepsGoodCopyReplacesOldOne);
            await Run(nameof(CancelledInstallLeavesNoFile), CancelledInstallLeavesNoFile);
            await Run(nameof(RemoveReturnsPluginToAvailable), RemoveReturnsPluginToAvailable);
            await Run(nameof(SuiteFolderWithoutRynthAi), SuiteFolderWithoutRynthAi);
            await Run(nameof(UnsignedDescriptionBreaksSignature), UnsignedDescriptionBreaksSignature);
            await Run(nameof(UrlOutsideFeedStillRefused), UrlOutsideFeedStillRefused);
            await Run(nameof(MeterThrottlesAndFinishes), MeterThrottlesAndFinishes);
            await Run(nameof(ProgressTextFormats), ProgressTextFormats);
            await Run(nameof(UpdateCardStates), UpdateCardStates);
            await Run(nameof(UpdateCardDownloading), UpdateCardDownloading);
            await Run(nameof(UpdateCardWhatsNew), UpdateCardWhatsNew);
            await Run(nameof(TooNewPluginIsNotInstalled), TooNewPluginIsNotInstalled);
            await Run(nameof(TooNewPluginWithoutCoreApiSaysTheApi), TooNewPluginWithoutCoreApiSaysTheApi);
            await Run(nameof(TooNewUpdateIsHeldBack), TooNewUpdateIsHeldBack);
            await Run(nameof(TooNewCompanionWaits), TooNewCompanionWaits);
            await Run(nameof(MinEngineApiAtOrBelowInstalledIsFine), MinEngineApiAtOrBelowInstalledIsFine);
            await RollbackTests();
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

    private static Task Run(string name, Action test) => Run(name, () => { test(); return Task.CompletedTask; });

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
            // Big files go out in pieces with a pause between them, so a download is still running
            // when progress is reported (and can be cancelled half way).
            _ = Task.Run(async () =>
            {
                try
                {
                    for (int off = 0; off < b.Length; off += 64 * 1024)
                    {
                        await c.Response.OutputStream.WriteAsync(b.AsMemory(off, Math.Min(64 * 1024, b.Length - off)));
                        if (b.Length > 512 * 1024) await Task.Delay(25);
                    }
                }
                catch { }
                finally { try { c.Response.Close(); } catch { } }
            });
        }
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
    // Names that aren't plain folder names (a test of the folder check) get a plain file name.
    private static string FileOf(string name) =>
        name.All(char.IsLetterOrDigit) ? $"RynthCore.Plugin.{name}.dll" : $"RynthCore.Plugin.Weird{(uint)name.GetHashCode():x}.dll";

    private static byte[] Body(string name, string version = "v2")
    {
        if (name == "RynthOracle")
        {
            // Big enough for several progress reports.
            var rnd = new Random(7);
            byte[] b = new byte[1_500_000];
            rnd.NextBytes(b);
            Encoding.ASCII.GetBytes($"dll {name} {version}").CopyTo(b, 0);
            return b;
        }
        return Encoding.UTF8.GetBytes($"dll {name} {version}");
    }

    private static readonly string[] FeedPlugins = { "RynthAi", "RynthChat", "RynthNav", "RynthLua", "RynthOracle", "RynthInventory" };

    private sealed record Feed(RynthUpdater Upd, string Dir, string Suite, string State, string RynthAiPath);

    /// <summary>
    /// A signed feed in its own folder, a state folder, and RynthAi installed at
    /// &lt;tag&gt;\Games\RynthSuite\RynthAi (the installer's layout).
    /// </summary>
    private static Feed Setup(string tag, bool descriptions = true, IEnumerable<string>? extraPlugins = null,
        Dictionary<string, string>? descriptionOverride = null, bool signDescriptions = true, string? badUrlFor = null,
        string[]? changes = null, Dictionary<string, uint>? minEngineApi = null, uint coreEngineApi = 0)
    {
        _minEngineApi = minEngineApi;
        _coreEngineApi = coreEngineApi;
        string dir = Path.Combine(_root, tag);
        string state = Path.Combine(dir, "state"), app = Path.Combine(dir, "app"), suite = Path.Combine(dir, "Games", "RynthSuite");
        Directory.CreateDirectory(state); Directory.CreateDirectory(app);
        string ai = Path.Combine(suite, "RynthAi", FileOf("RynthAi"));
        Directory.CreateDirectory(Path.GetDirectoryName(ai)!);
        File.WriteAllBytes(ai, Body("RynthAi"));
        File.SetLastWriteTimeUtc(ai, Published);
        WriteFeed(tag, descriptions, extraPlugins, descriptionOverride, signDescriptions, badUrlFor, changes);
        var upd = new RynthUpdater(_base + $"{tag}/update.json", new[] { Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo()) }, state, app);
        upd.SetDefaultSuiteDir(Path.Combine(dir, "DefaultSuite"));
        return new Feed(upd, dir, suite, state, ai);
    }

    private static void WriteFeed(string tag, bool descriptions = true, IEnumerable<string>? extraPlugins = null,
        Dictionary<string, string>? descriptionOverride = null, bool signDescriptions = true, string? badUrlFor = null,
        string[]? changes = null)
    {
        var plugins = new List<Dictionary<string, object>>();
        foreach (string name in FeedPlugins.Concat(extraPlugins ?? Array.Empty<string>()))
        {
            byte[] body = Body(name);
            string rel = $"{tag}/releases/1/plugins/{FileOf(name)}";
            lock (Served) Served[rel] = body;
            var p = new Dictionary<string, object>
            {
                ["name"] = name, ["file"] = FileOf(name),
                ["url"] = name == badUrlFor ? "http://elsewhere.example/" + FileOf(name) : _base + rel,
                ["size"] = body.Length, ["sha256"] = Sha(body),
            };
            if (descriptions) p["description"] = descriptionOverride != null && descriptionOverride.TryGetValue(name, out string? d) ? d : $"Feed line for {name}.";
            if (_minEngineApi != null && _minEngineApi.TryGetValue(name, out uint min)) p["minEngineApi"] = min;
            plugins.Add(p);
        }
        var core = new Dictionary<string, object> { ["release"] = 2026100501, ["version"] = "2026.10.5.1", ["url"] = _base + $"{tag}/releases/1/RynthCore-Setup.exe", ["size"] = 1, ["sha256"] = "00" };
        if (_coreEngineApi != 0) core["engineApi"] = _coreEngineApi;
        var manifest = new Dictionary<string, object?>
        {
            ["schema"] = 1, ["release"] = 2026100501, ["version"] = "2026.10.5.1",
            ["published"] = Published.ToString("yyyy-MM-ddTHH:mm:ssZ"), ["notes"] = "Release notes for players.",
            ["core"] = core,
            ["plugins"] = plugins,
        };
        if (changes != null) manifest["changes"] = changes;
        byte[] signedPayload;
        if (!signDescriptions)
        {
            // What was signed had no descriptions; what is served does.
            var unsigned = plugins.Select(p => p.Where(kv => kv.Key != "description").ToDictionary(kv => kv.Key, kv => kv.Value)).ToList();
            var m2 = new Dictionary<string, object?>(manifest) { ["plugins"] = unsigned };
            signedPayload = JsonSerializer.SerializeToUtf8Bytes(m2);
        }
        else signedPayload = JsonSerializer.SerializeToUtf8Bytes(manifest);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(manifest);
        byte[] sig = Key.SignData(signedPayload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        string envelope = JsonSerializer.Serialize(new
        {
            payload = Convert.ToBase64String(payload),
            signatures = new[] { new { key = RynthUpdater.KeyId(Key.ExportSubjectPublicKeyInfo()), sig = Convert.ToBase64String(sig) } },
        });
        lock (Served) Served[$"{tag}/update.json"] = Encoding.UTF8.GetBytes(envelope);
    }

    /// <summary>Installs a plugin by hand at the suite layout (as if from the zip or an earlier launcher).</summary>
    private static string Place(Feed f, string name)
    {
        string path = Path.Combine(f.Suite, name, FileOf(name));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Body(name));
        File.SetLastWriteTimeUtc(path, Published);
        return path;
    }

    private sealed class SyncProgress(Action<DownloadProgress> onReport) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => onReport(value);
    }

    private static List<string> Names(IEnumerable<RynthUpdater.AvailablePlugin> a) => a.Select(x => x.Entry.Name).OrderBy(n => n).ToList();

    // ── Available list ───────────────────────────────────────────────────────

    private static async Task AvailableIsFeedMinusInstalled()
    {
        var f = Setup("avail");
        string chat = Place(f, "RynthChat");
        var paths = new List<string> { f.RynthAiPath, chat };
        var c = await f.Upd.CheckAsync(paths);
        Check(Names(c.Available).SequenceEqual(new[] { "RynthInventory", "RynthNav", "RynthOracle" }),
            "Available = feed minus installed, minus RynthLua (installed automatically): " + string.Join(",", Names(c.Available)));
        var oracle = c.Available.Single(a => a.Entry.Name == "RynthOracle");
        Check(oracle.Path == Path.Combine(f.Suite, "RynthOracle", FileOf("RynthOracle")), "goes beside RynthAi: " + oracle.Path);
        Check(oracle.Version == "2026.10.5.1" && oracle.Entry.Size == Body("RynthOracle").Length, "version and size from the feed");
        Check(oracle.Description == "Feed line for RynthOracle.", "description from the feed");
        Check(f.Upd.GetAvailable(c, paths).Count == c.Available.Count, "recomputed against the same list: the same");
        // Disabled (unticked) plugins are still in the list: not available.
        Check(!c.Available.Any(a => a.Entry.Name == "RynthChat"), "a listed plugin is never available");
    }

    private static async Task OnlyRynthLuaIsAutomatic()
    {
        var f = Setup("companions");
        var c = await f.Upd.CheckAsync(new[] { f.RynthAiPath });
        Check(c.Companions.Count == 1 && c.Companions[0].Entry.Name == "RynthLua", "only RynthLua installs itself beside RynthAi");
        var done = await f.Upd.InstallCompanionsAsync(c);
        Check(done.Count == 1 && File.Exists(done[0].Path), "RynthLua installed");
        var c2 = await f.Upd.CheckAsync(new[] { f.RynthAiPath });
        Check(c2.Companions.Count == 0, "offered once: removed from the list, it stays removed");
        Check(c2.Available.Any(a => a.Entry.Name == "RynthLua"), "but it is under Available if the player wants it back");
        Check(c2.Available.Any(a => a.Entry.Name == "RynthOracle") && c2.Available.Any(a => a.Entry.Name == "RynthInventory"),
            "RynthOracle and RynthInventory are offered, not installed");
    }

    private static async Task OldManifestWithoutDescriptions()
    {
        var f = Setup("olddesc", descriptions: false, extraPlugins: new[] { "RynthSomethingElse" });
        var c = await f.Upd.CheckAsync(new[] { f.RynthAiPath });
        Check(c.Manifest.Plugins.All(p => p.Description == ""), "an old feed parses with no descriptions");
        Check(c.Available.Single(a => a.Entry.Name == "RynthOracle").Description == "Quests, character and titles.",
            "a missing description falls back to the launcher's table");
        Check(c.Available.Single(a => a.Entry.Name == "RynthNav").Description.Contains("540 MB"), "RynthNav's says how big its map is");
        Check(c.Available.Single(a => a.Entry.Name == "RynthSomethingElse").Description == "", "a plugin the table doesn't know: no description, still listed");
        Check(c.Manifest.Changes.Count == 0 && c.Manifest.Notes.Length > 0, "no changes list: the notes are still there");
    }

    private static async Task DescriptionsAreOneShortLine()
    {
        var f = Setup("longdesc", descriptionOverride: new()
        {
            ["RynthOracle"] = "Line one\nline two\t\u0007 with  spaces " + new string('x', 400),
            ["RynthNav"] = "   ",
        }, changes: new[] { "First change.", "  Second\nchange.  ", "" });
        var c = await f.Upd.CheckAsync(new[] { f.RynthAiPath });
        string d = c.Available.Single(a => a.Entry.Name == "RynthOracle").Description;
        Check(!d.Contains('\n') && !d.Contains('\t') && !d.Contains('\u0007') && d.StartsWith("Line one line two with spaces"), "one line, control characters gone: " + d[..40]);
        Check(d.Length <= 160 && d.EndsWith("…"), $"capped at 160 characters ({d.Length})");
        Check(c.Available.Single(a => a.Entry.Name == "RynthNav").Description.Contains("540 MB"), "a blank feed description falls back to the table");
        Check(c.Manifest.Changes.SequenceEqual(new[] { "First change.", "Second change." }), "changes: trimmed, one line each, blanks dropped");
    }

    private static async Task NewBadgeAndOneAnnouncement()
    {
        var f = Setup("newbadge");
        DateTime now = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
        f.Upd.UtcNow = () => now;
        var paths = new[] { f.RynthAiPath };
        var c = await f.Upd.CheckAsync(paths);
        Check(c.Available.Where(a => a.IsNew).Select(a => a.Entry.Name).OrderBy(n => n).SequenceEqual(new[] { "RynthInventory", "RynthOracle" }),
            "first run: the two recent plugins are New, the rest aren't");
        Check(Names(c.NewlyAnnounced).SequenceEqual(new[] { "RynthInventory", "RynthOracle" }), "and announced");
        Check(c.Available.First().IsNew, "New ones are listed first");
        c = await f.Upd.CheckAsync(paths);
        Check(c.NewlyAnnounced.Count == 0, "announced once only");
        Check(c.Available.Count(a => a.IsNew) == 2, "the badge stays");
        now = now.AddDays(31);
        c = await f.Upd.CheckAsync(paths);
        Check(c.Available.All(a => !a.IsNew), "after a month the badge goes");

        // A plugin added to the feed later is New and announced, no table needed.
        WriteFeed("newbadge", extraPlugins: new[] { "RynthBrandNew" });
        c = await f.Upd.CheckAsync(paths);
        Check(c.Available.Single(a => a.Entry.Name == "RynthBrandNew").IsNew && Names(c.NewlyAnnounced).SequenceEqual(new[] { "RynthBrandNew" }),
            "a plugin that appears in the feed later is New and announced");
    }

    private static async Task NotNewForPlayersWhoHadThem()
    {
        var f = Setup("hadthem");
        // 2026.10.4.3 auto-installed both; this player removed RynthInventory afterwards.
        File.WriteAllText(Path.Combine(f.State, "update-companions.json"), "[\"RynthInventory\", \"RynthLua\", \"RynthOracle\"]");
        string oracle = Place(f, "RynthOracle");
        var c = await f.Upd.CheckAsync(new[] { f.RynthAiPath, oracle });
        Check(!c.Available.Any(a => a.Entry.Name == "RynthOracle"), "the one they kept is installed, not offered");
        var inv = c.Available.SingleOrDefault(a => a.Entry.Name == "RynthInventory");
        Check(inv != null && !inv.IsNew && c.NewlyAnnounced.Count == 0, "the one they removed is available, without a New badge or an announcement");
        Check(c.Companions.Count == 0, "and nothing installs itself (RynthLua was offered already)");
    }

    // ── Install ──────────────────────────────────────────────────────────────

    private static async Task InstallDownloadsVerifiesRegisters()
    {
        var f = Setup("install");
        var paths = new List<string> { f.RynthAiPath };
        var c = await f.Upd.CheckAsync(paths);
        var reports = new List<DownloadProgress>();
        var r = await f.Upd.InstallPluginAsync(c, "RynthOracle", paths, new SyncProgress(p => { lock (reports) reports.Add(p); }));
        string expected = Path.Combine(f.Suite, "RynthOracle", FileOf("RynthOracle"));
        Check(r.Path == expected && r.Downloaded, "installed into the suite folder: " + r.Path);
        Check(File.Exists(expected) && Sha(File.ReadAllBytes(expected)) == Sha(Body("RynthOracle")), "the file is the signed one");
        Check(File.GetLastWriteTimeUtc(expected) == Published, "stamped with the release time (never taken for a local build)");
        var dl = reports.Where(p => p.Stage == DownloadStage.Downloading).ToList();
        Check(dl.Count >= 2 && dl.Last().Done == dl.Last().Total && dl.Last().Percent == 100, $"progress reaches 100% ({dl.Count} reports)");
        Check(dl.All(p => p.What == "RynthOracle"), "progress names the plugin");
        Check(reports.Any(p => p.Stage == DownloadStage.Verified) && reports.Last().Stage == DownloadStage.Installing, "verified, then installing");

        Check(PluginListEdits.Add(paths, r.Path), "registered in the plugin list");
        Check(!PluginListEdits.Add(paths, r.Path.ToUpperInvariant()), "only once");
        Check(!f.Upd.GetAvailable(c, paths).Any(a => a.Entry.Name == "RynthOracle"), "no longer available");
        var c2 = await f.Upd.CheckAsync(paths);
        Check(c2.Plugins.Any(p => p.Entry.Name == "RynthOracle" && p.State == RynthUpdater.PluginState.UpToDate), "the next check sees it up to date");
    }

    // ── Plugin manifests: minEngineApi ───────────────────────────────────────

    // The feed fixture's optional fields (set by Setup, read by WriteFeed).
    private static Dictionary<string, uint>? _minEngineApi;
    private static uint _coreEngineApi;
    private const uint Installed = 76;   // the engine API the test launcher "installed"

    private static async Task TooNewPluginIsNotInstalled()
    {
        // RynthOracle's build needs API 77; this feed's core release brings it.
        var f = Setup("api-new", minEngineApi: new() { ["RynthOracle"] = Installed + 1, ["RynthChat"] = 66 }, coreEngineApi: Installed + 1);
        f.Upd.InstalledEngineApi = Installed;
        var paths = new List<string> { f.RynthAiPath };
        var c = await f.Upd.CheckAsync(paths);
        var oracle = c.Available.Single(a => a.Entry.Name == "RynthOracle");
        Check(oracle.Entry.MinEngineApi == Installed + 1, "minEngineApi read from the feed");
        Check(!oracle.CanInstall, "a too-new plugin can't be installed");
        Check(oracle.Blocker == "needs RynthCore 2026.10.5.1", "it says which RynthCore it needs: " + oracle.Blocker);
        Check(c.Available.Single(a => a.Entry.Name == "RynthChat").CanInstall, "a plugin within the engine's API still can");
        Check(c.Available.Single(a => a.Entry.Name == "RynthNav").CanInstall, "a plugin without minEngineApi still can");
        try
        {
            await f.Upd.InstallPluginAsync(c, "RynthOracle", paths);
            Check(false, "InstallPluginAsync should refuse a too-new plugin");
        }
        catch (InvalidOperationException ex)
        {
            Check(ex.Message.Contains("needs RynthCore"), "refused with the reason: " + ex.Message);
        }
        Check(!File.Exists(oracle.Path), "nothing written");
        Check(!Directory.Exists(Path.GetDirectoryName(oracle.Path)!), "no folder created");
    }

    private static async Task TooNewPluginWithoutCoreApiSaysTheApi()
    {
        // The feed's core doesn't say its API (or brings an older one): name the API instead.
        var f = Setup("api-nocore", minEngineApi: new() { ["RynthOracle"] = Installed + 2 }, coreEngineApi: Installed + 1);
        f.Upd.InstalledEngineApi = Installed;
        var c = await f.Upd.CheckAsync(new[] { f.RynthAiPath });
        var oracle = c.Available.Single(a => a.Entry.Name == "RynthOracle");
        Check(oracle.Blocker == $"needs a newer RynthCore (API {Installed + 2})", "names the API: " + oracle.Blocker);

        var g = Setup("api-nocore2", minEngineApi: new() { ["RynthOracle"] = Installed + 1 });
        g.Upd.InstalledEngineApi = Installed;
        var c2 = await g.Upd.CheckAsync(new[] { g.RynthAiPath });
        Check(c2.Available.Single(a => a.Entry.Name == "RynthOracle").Blocker == $"needs a newer RynthCore (API {Installed + 1})",
              "a core without engineApi: the API too");
    }

    private static async Task TooNewUpdateIsHeldBack()
    {
        var f = Setup("api-update", minEngineApi: new() { ["RynthChat"] = Installed + 1, ["RynthNav"] = Installed }, coreEngineApi: Installed + 1);
        f.Upd.InstalledEngineApi = Installed;
        string chat = Place(f, "RynthChat"), nav = Place(f, "RynthNav");
        byte[] oldChat = Encoding.UTF8.GetBytes("dll RynthChat v1"), oldNav = Encoding.UTF8.GetBytes("dll RynthNav v1");
        File.WriteAllBytes(chat, oldChat); File.SetLastWriteTimeUtc(chat, Published.AddDays(-3));
        File.WriteAllBytes(nav, oldNav); File.SetLastWriteTimeUtc(nav, Published.AddDays(-3));
        var c = await f.Upd.CheckAsync(new[] { f.RynthAiPath, chat, nav });
        var chatStatus = c.Plugins.Single(p => p.Entry.Name == "RynthChat");
        Check(chatStatus.State == RynthUpdater.PluginState.NeedsNewerEngine, "too-new update held back: " + chatStatus.State);
        Check(chatStatus.Blocker == "needs RynthCore 2026.10.5.1", "with the reason: " + chatStatus.Blocker);
        Check(c.PluginsWaitingForCore.Any(p => p.Entry.Name == "RynthChat"), "listed as waiting for RynthCore");
        Check(c.Plugins.Single(p => p.Entry.Name == "RynthNav").State == RynthUpdater.PluginState.NeedsUpdate, "an update at the installed API goes ahead");
        Check(c.PluginsToUpdate.Select(p => p.Entry.Name).SequenceEqual(new[] { "RynthNav" }), "only RynthNav is updated");
        await f.Upd.UpdatePluginsAsync(c);
        Check(File.ReadAllBytes(chat).SequenceEqual(oldChat), "RynthChat kept as it was (the engine would refuse the new build)");
        Check(!File.Exists(chat + ".previous"), "no .previous for RynthChat");
        Check(File.ReadAllBytes(nav).SequenceEqual(Body("RynthNav")), "RynthNav updated");

        // After RynthCore is updated (the new launcher knows the new API), the update goes through.
        f.Upd.InstalledEngineApi = Installed + 1;
        var after = await f.Upd.CheckAsync(new[] { f.RynthAiPath, chat, nav });
        Check(after.Plugins.Single(p => p.Entry.Name == "RynthChat").State == RynthUpdater.PluginState.NeedsUpdate, "updated once RynthCore catches up");
    }

    private static async Task TooNewCompanionWaits()
    {
        var f = Setup("api-companion", minEngineApi: new() { ["RynthLua"] = Installed + 1 }, coreEngineApi: Installed + 1);
        f.Upd.InstalledEngineApi = Installed;
        var c = await f.Upd.CheckAsync(new[] { f.RynthAiPath });
        Check(c.Companions.Count == 0, "a too-new companion isn't installed beside RynthAi");
        Check(c.Available.Single(a => a.Entry.Name == "RynthLua").Blocker.Length > 0, "and shows what it needs under Available");
        f.Upd.InstalledEngineApi = Installed + 1;
        var later = await f.Upd.CheckAsync(new[] { f.RynthAiPath });
        Check(later.Companions.Count == 1 && later.Companions[0].Entry.Name == "RynthLua", "offered once RynthCore catches up (not marked as offered before)");
    }

    private static async Task MinEngineApiAtOrBelowInstalledIsFine()
    {
        var f = Setup("api-ok", minEngineApi: new() { ["RynthOracle"] = Installed, ["RynthInventory"] = 71 });
        f.Upd.InstalledEngineApi = Installed;
        var paths = new List<string> { f.RynthAiPath };
        var c = await f.Upd.CheckAsync(paths);
        Check(c.Available.All(a => a.CanInstall), "everything installable: " + string.Join(",", c.Available.Where(a => !a.CanInstall).Select(a => a.Entry.Name)));
        var done = await f.Upd.InstallPluginAsync(c, "RynthOracle", paths);
        Check(done.Downloaded && File.Exists(done.Path), "installed at the exact minimum");
        Check(f.Upd.InstalledEngineApi == Installed && new RynthUpdater().InstalledEngineApi == RynthCore.Engine.Plugins.PluginContractVersion.Current,
              "the launcher's default is the engine's own API constant");
    }

    private static async Task InstallRefusesTamperedFile()
    {
        var f = Setup("tamper");
        var paths = new List<string> { f.RynthAiPath };
        var c = await f.Upd.CheckAsync(paths);
        byte[] bad = Body("RynthInventory"); bad[0] ^= 0xFF;
        lock (Served) Served[$"tamper/releases/1/plugins/{FileOf("RynthInventory")}"] = bad;
        var reports = new List<DownloadProgress>();
        bool threw = false;
        try { await f.Upd.InstallPluginAsync(c, "RynthInventory", paths, new SyncProgress(reports.Add)); }
        catch (InvalidDataException) { threw = true; }
        Check(threw, "a file that doesn't match the signed hash is refused");
        string target = Path.Combine(f.Suite, "RynthInventory", FileOf("RynthInventory"));
        Check(!File.Exists(target), "nothing installed");
        Check(!reports.Any(p => p.Stage is DownloadStage.Verified or DownloadStage.Installing), "never reported as verified");

        // A bigger tampered file, refused at the end of a download with progress on.
        byte[] badBig = Body("RynthOracle"); badBig[badBig.Length - 1] ^= 0x01;
        lock (Served) Served[$"tamper/releases/1/plugins/{FileOf("RynthOracle")}"] = badBig;
        threw = false;
        try { await f.Upd.InstallPluginAsync(c, "RynthOracle", paths, new SyncProgress(_ => { })); }
        catch (InvalidDataException) { threw = true; }
        Check(threw && !File.Exists(Path.Combine(f.Suite, "RynthOracle", FileOf("RynthOracle"))), "with progress on, the hash is still checked");
    }

    private static async Task InstallOnlyFromTheFeed()
    {
        var f = Setup("feedonly", extraPlugins: new[] { "..", "Bad Name" });
        var paths = new List<string> { f.RynthAiPath };
        var c = await f.Upd.CheckAsync(paths);
        Check(!c.Available.Any(a => a.Entry.Name is ".." or "Bad Name"), "a feed name that isn't a plain folder name is never listed");
        bool threw = false;
        try { await f.Upd.InstallPluginAsync(c, "..", paths); } catch (InvalidDataException) { threw = true; }
        Check(threw, "and can't be installed");
        threw = false;
        try { await f.Upd.InstallPluginAsync(c, "RynthNotInFeed", paths); } catch (InvalidOperationException) { threw = true; }
        Check(threw, "a plugin the feed doesn't have can't be installed");
        threw = false;
        try { await f.Upd.InstallPluginAsync(c, "RynthAi", paths); } catch (InvalidOperationException) { threw = true; }
        Check(threw, "one already in the list isn't installed again");
    }

    private static async Task InstallKeepsGoodCopyReplacesOldOne()
    {
        var f = Setup("existing");
        var paths = new List<string> { f.RynthAiPath };
        var c = await f.Upd.CheckAsync(paths);
        string chat = Place(f, "RynthChat");   // the right file already there (from the zip)
        var r = await f.Upd.InstallPluginAsync(c, "RynthChat", paths);
        Check(r.Path == chat && !r.Downloaded, "a good copy at the target is kept, not downloaded again");

        string nav = Path.Combine(f.Suite, "RynthNav", FileOf("RynthNav"));
        Directory.CreateDirectory(Path.GetDirectoryName(nav)!);
        File.WriteAllText(nav, "an old RynthNav");
        File.SetLastWriteTimeUtc(nav, Published.AddDays(-20));
        r = await f.Upd.InstallPluginAsync(c, "RynthNav", paths);
        Check(r.Downloaded && Sha(File.ReadAllBytes(nav)) == Sha(Body("RynthNav")), "an older copy is replaced by the release");
        Check(File.ReadAllText(nav + ".previous") == "an old RynthNav", "and kept beside it as .previous");

        string inv = Path.Combine(f.Suite, "RynthInventory", FileOf("RynthInventory"));
        Directory.CreateDirectory(Path.GetDirectoryName(inv)!);
        File.WriteAllText(inv, "my own build");
        File.SetLastWriteTimeUtc(inv, Published.AddDays(2));
        r = await f.Upd.InstallPluginAsync(c, "RynthInventory", paths);
        Check(!r.Downloaded && File.ReadAllText(inv) == "my own build", "a newer local build is left alone");
    }

    private static async Task CancelledInstallLeavesNoFile()
    {
        var f = Setup("cancel");
        var paths = new List<string> { f.RynthAiPath };
        var c = await f.Upd.CheckAsync(paths);
        using var cts = new CancellationTokenSource();
        bool halfWay = false, threw = false;
        string tempBefore = string.Join("|", Directory.GetFiles(Path.GetTempPath(), "rynth-*.dll").OrderBy(x => x));
        try
        {
            await f.Upd.InstallPluginAsync(c, "RynthOracle", paths, new SyncProgress(p =>
            {
                if (p.Stage == DownloadStage.Downloading && p.Done > 0 && p.Done < p.Total) { halfWay = true; cts.Cancel(); }
            }), cts.Token);
        }
        catch (OperationCanceledException) { threw = true; }
        Check(halfWay && threw, "cancelled half way");
        Check(!File.Exists(Path.Combine(f.Suite, "RynthOracle", FileOf("RynthOracle"))), "nothing installed");
        string tempAfter = string.Join("|", Directory.GetFiles(Path.GetTempPath(), "rynth-*.dll").OrderBy(x => x));
        Check(tempAfter == tempBefore, "the partial download is deleted");
        var r = await f.Upd.InstallPluginAsync(c, "RynthOracle", paths);
        Check(r.Downloaded && File.Exists(r.Path), "a later Install works");
    }

    // ── Remove ───────────────────────────────────────────────────────────────

    private static async Task RemoveReturnsPluginToAvailable()
    {
        var f = Setup("remove");
        string oracle = Place(f, "RynthOracle");
        var paths = new List<string> { f.RynthAiPath, oracle };
        var disabled = new List<string> { oracle.ToUpperInvariant() };
        var c = await f.Upd.CheckAsync(paths);
        Check(!c.Available.Any(a => a.Entry.Name == "RynthOracle"), "installed: not available");
        Check(PluginListEdits.Remove(paths, disabled, oracle), "removed from the list");
        Check(!paths.Contains(oracle) && disabled.Count == 0, "and from the disabled list (adding it back starts it ticked)");
        Check(File.Exists(oracle), "the files stay on disk");
        Check(f.Upd.GetAvailable(c, paths).Any(a => a.Entry.Name == "RynthOracle"), "back under Available");
        c = await f.Upd.CheckAsync(paths);
        Check(c.Available.Any(a => a.Entry.Name == "RynthOracle") && !c.Companions.Any(x => x.Entry.Name == "RynthOracle"), "the next check agrees, and doesn't reinstall it");
        var r = await f.Upd.InstallPluginAsync(c, "RynthOracle", paths);
        Check(!r.Downloaded && r.Path == oracle, "Install again reuses the file that stayed");
        Check(!PluginListEdits.Remove(paths, null, Path.Combine(f.Dir, "never-listed.dll")), "removing something not listed does nothing");
    }

    private static async Task SuiteFolderWithoutRynthAi()
    {
        var f = Setup("suite");
        // RynthAi not in the list, RynthChat in a folder named after it: install beside that.
        string otherSuite = Path.Combine(f.Dir, "Elsewhere", "RynthSuite");
        string chat = Path.Combine(otherSuite, "RynthChat", FileOf("RynthChat"));
        Directory.CreateDirectory(Path.GetDirectoryName(chat)!);
        File.WriteAllBytes(chat, Body("RynthChat"));
        var c = await f.Upd.CheckAsync(new[] { chat });
        Check(c.Available.Single(a => a.Entry.Name == "RynthNav").Path.StartsWith(otherSuite), "beside another suite plugin");
        c = await f.Upd.CheckAsync(new[] { Path.Combine(f.Dir, "loose", "SomeOther.dll") });
        Check(c.Available.Single(a => a.Entry.Name == "RynthNav").Path.StartsWith(Path.Combine(f.Dir, "DefaultSuite")), "else the default folder");
        Check(c.Available.Any(a => a.Entry.Name == "RynthAi"), "RynthAi itself is available when it isn't listed");
    }

    // ── The feed's rules are unchanged ───────────────────────────────────────

    private static async Task UnsignedDescriptionBreaksSignature()
    {
        var f = Setup("unsigned", signDescriptions: false);
        bool threw = false;
        try { await f.Upd.CheckAsync(new[] { f.RynthAiPath }); } catch (InvalidDataException) { threw = true; }
        Check(threw, "descriptions are covered by the signature like everything else");
    }

    private static async Task UrlOutsideFeedStillRefused()
    {
        var f = Setup("badurl", badUrlFor: "RynthOracle");
        bool threw = false;
        try { await f.Upd.CheckAsync(new[] { f.RynthAiPath }); } catch (InvalidDataException) { threw = true; }
        Check(threw, "a plugin URL outside the feed's folder is refused (so it can never be installed)");
    }

    // ── Download meter and text ──────────────────────────────────────────────

    private static void MeterThrottlesAndFinishes()
    {
        TimeSpan t = TimeSpan.Zero;
        var reports = new List<DownloadProgress>();
        long total = 100L << 20;
        var m = new DownloadMeter("RynthNav map", total, new SyncProgress(reports.Add), () => t);
        m.Report(0);
        for (int i = 1; i <= 9; i++) { t += TimeSpan.FromMilliseconds(20); m.Report(i * (1L << 20)); }
        Check(reports.Count == 1, $"reads 20 ms apart are throttled ({reports.Count} reports)");
        t = TimeSpan.FromSeconds(1); m.Report(10L << 20);
        Check(reports.Count == 2, "a read after the interval is reported");
        var r = reports.Last();
        Check(Math.Abs(r.BytesPerSecond - (10L << 20)) < (1L << 20), $"speed ~10 MB/s ({r.BytesPerSecond / (1 << 20):0.0})");
        Check(r.Remaining is { } rem && Math.Abs(rem.TotalSeconds - 9) < 1, $"time left ~9 s ({r.Remaining})");
        Check(r.Percent is { } pc && Math.Abs(pc - 10) < 0.01, "10%");
        t = TimeSpan.FromSeconds(1.01); m.Report(total);
        Check(reports.Last().Done == total && reports.Last().Percent == 100 && reports.Last().Remaining == TimeSpan.Zero, "the end is always reported, at 100%");
        t = TimeSpan.FromSeconds(130); m.Verified();
        Check(reports.Last().Stage == DownloadStage.Verified && reports.Last().Elapsed == TimeSpan.FromSeconds(130), "Verified carries the time taken");
        Check(new DownloadMeter("x", 10, null).Elapsed >= TimeSpan.Zero, "no progress wanted: no reports, no failure");
    }

    private static void ProgressTextFormats()
    {
        var done = new DownloadProgress("RynthNav map", DownloadStage.Verified, 565_182_464, 565_182_464, Elapsed: TimeSpan.FromSeconds(130));
        Check(done.Summary == "RynthNav map: 539 MB in 2m10s, verified", "log line: " + done.Summary);
        var mid = new DownloadProgress("RynthNav map", DownloadStage.Downloading, 327_155_712, 565_182_464, 4.2 * 1024 * 1024, TimeSpan.FromSeconds(55));
        Check(mid.Detail == "312 of 539 MB · 4.2 MB/s · about 55s left", "detail: " + mid.Detail);
        var small = new DownloadProgress("RynthOracle", DownloadStage.Downloading, 524_288, 1_500_000);
        Check(small.Detail == "0.5 of 1.4 MB", "small file, no speed yet: " + small.Detail);
        var unpack = new DownloadProgress("RynthNav map", DownloadStage.Unpacking, 12345, 40112);
        Check(unpack.Detail == "Unpacking 12,345 of 40,112 files…" && unpack.Percent is > 30 and < 31, "unpacking: " + unpack.Detail);
        Check(new DownloadProgress("x", DownloadStage.Installing, 0, 0).Percent == null, "installing has no percentage (an indeterminate bar)");
        Check(DownloadProgress.FormatDuration(TimeSpan.FromMinutes(65)) == "1h05m" && DownloadProgress.FormatDuration(TimeSpan.FromSeconds(8.4)) == "8s", "durations");
    }

    // ── Update card ──────────────────────────────────────────────────────────

    private static void UpdateCardStates()
    {
        var notConfigured = UpdateCardModel.Build(new UpdateCardInput { Configured = false });
        Check(notConfigured.Kind == UpdateCardKind.NotConfigured && !notConfigured.ShowCheckButton, "no signing key: says so, no button");

        var notChecked = UpdateCardModel.Build(new UpdateCardInput { InstalledVersion = "2026.10.4.3" });
        Check(notChecked.Kind == UpdateCardKind.NotChecked && notChecked.ShowCheckButton && notChecked.ActionButton == null, "before the first check: Check button only");

        var checking = UpdateCardModel.Build(new UpdateCardInput { Checking = true, InstalledVersion = "2026.10.4.3" });
        Check(checking.Kind == UpdateCardKind.Checking && !checking.ShowCheckButton, "checking: no buttons");

        DateTime at = DateTime.Now.Date.AddHours(14).AddMinutes(5);
        var upToDate = UpdateCardModel.Build(new UpdateCardInput { HasCheck = true, InstalledVersion = "2026.10.4.3", LastChecked = at });
        Check(upToDate.Kind == UpdateCardKind.UpToDate && upToDate.Title == "RynthCore 2026.10.4.3 - up to date", "up to date: " + upToDate.Title);
        Check(upToDate.Detail == "Last checked at 14:05." && upToDate.ShowCheckButton && upToDate.ActionButton == null, "with the last check time and Check: " + upToDate.Detail);
        Check(upToDate.NewPluginsLine == null, "no new plugins, no line");

        var error = UpdateCardModel.Build(new UpdateCardInput { HasCheck = true, Error = "No such host is known.", LastChecked = at, InstalledVersion = "2026.10.4.3" });
        Check(error.Kind == UpdateCardKind.Error && error.ShowCheckButton && error.Detail == "No such host is known. Last good check: at 14:05.", "error: " + error.Detail);

        var core = UpdateCardModel.Build(new UpdateCardInput
        {
            HasCheck = true, InstalledVersion = "2026.10.4.3", CoreUpdateAvailable = true, AvailableVersion = "2026.10.5.1",
            ClientsRunning = 2, PendingPluginUpdates = new[] { "RynthAi" }, LastChecked = at,
        });
        Check(core.Kind == UpdateCardKind.CoreUpdate && core.Title == "RynthCore 2026.10.5.1 is available" && core.ActionButton == UpdateCardModel.UpdateCoreButton, "RynthCore update: version and Update button");
        Check(core.Detail.Contains("You have 2026.10.4.3") && core.Detail.Contains("installs once every game client is closed"), "says it installs once the game clients are closed");
        Check(core.Detail.Contains("Close your 2 game clients running RynthCore first."), "and how many are still running: " + core.Detail);
        Check(core.Detail.Contains("Plugin updates ready too: RynthAi."), "mentions plugin updates as well");
        Check(core.Compact == "Update available: RynthCore 2026.10.5.1", "compact line: " + core.Compact);
        var oneClient = UpdateCardModel.Build(new UpdateCardInput { HasCheck = true, CoreUpdateAvailable = true, AvailableVersion = "2026.10.5.1", ClientsRunning = 1 });
        Check(oneClient.Detail.Contains("Close your 1 game client running"), "one client: singular");

        var plugins = UpdateCardModel.Build(new UpdateCardInput { HasCheck = true, InstalledVersion = "2026.10.5.1", PendingPluginUpdates = new[] { "RynthAi", "RynthNav map (539 MB download)" } });
        Check(plugins.Kind == UpdateCardKind.PluginUpdates && plugins.ActionButton == UpdateCardModel.UpdatePluginsButton && plugins.Detail.StartsWith("RynthAi, RynthNav map (539 MB download)."), "plugin updates: " + plugins.Detail);

        var withNew = UpdateCardModel.Build(new UpdateCardInput { HasCheck = true, InstalledVersion = "2026.10.5.1", NewPlugins = new[] { "RynthOracle", "RynthInventory" } });
        Check(withNew.Kind == UpdateCardKind.UpToDate && withNew.NewPluginsLine == "New plugins available: RynthOracle, RynthInventory. See Available plugins on the Plugins tab.", "new plugins line: " + withNew.NewPluginsLine);

        Check(UpdateCardModel.FormatWhen(new DateTime(2026, 10, 3, 9, 7, 0), new DateTime(2026, 10, 4, 12, 0, 0)) == "Oct 3 at 09:07", "an earlier day shows the date");
    }

    private static void UpdateCardDownloading()
    {
        var p = new DownloadProgress("RynthNav map", DownloadStage.Downloading, 100L << 20, 400L << 20, 5 << 20, TimeSpan.FromMinutes(1));
        var card = UpdateCardModel.Build(new UpdateCardInput { HasCheck = true, CoreUpdateAvailable = true, AvailableVersion = "x", Download = p, CanCancel = true });
        Check(card.Kind == UpdateCardKind.Downloading && card.Title == "Downloading RynthNav map", "a download takes over the card: " + card.Title);
        Check(card.ShowProgress && card.ProgressPercent == 25 && card.ShowCancel && card.ActionButton == null && !card.ShowCheckButton, "bar at 25%, Cancel, no other buttons");
        Check(card.Detail == "100 of 400 MB · 5.0 MB/s · about 1m00s left", "MB, speed, time left: " + card.Detail);
        var unpack = UpdateCardModel.Build(new UpdateCardInput { Download = p with { Stage = DownloadStage.Unpacking, Done = 10, Total = 40 } });
        Check(unpack.Title == "Unpacking RynthNav map" && unpack.ProgressPercent == 25, "the step after the download");
        var install = UpdateCardModel.Build(new UpdateCardInput { Download = p with { Stage = DownloadStage.Installing, Done = 0, Total = 0 }, CanCancel = true });
        Check(install.Title == "Installing RynthNav map" && install.ProgressPercent == null && !install.ShowCancel, "installing: indeterminate, no Cancel");
        var verified = UpdateCardModel.Build(new UpdateCardInput { Download = p with { Stage = DownloadStage.Verified } });
        Check(verified.Title == "RynthNav map: verified", "verified step shown");
    }

    /// <summary>
    /// The plugin list shows "RynthAi" (not "RynthCore.Plugin.RynthAi"), and a plugin for one game server
    /// goes on the Server plugins tab (RynthThreads too: DreamWeave only), the rest on Plugins.
    /// </summary>
    private static void PluginNamesAndServerSplit()
    {
        Check(RynthCore.App.Avalonia.PluginNames.Friendly(@"C:\Games\RynthSuite\RynthAi\RynthCore.Plugin.RynthAi.dll") == "RynthAi", "a DLL path: the short name");
        Check(RynthCore.App.Avalonia.PluginNames.Friendly("RynthCore.Plugin.RynthNav") == "RynthNav", "a file name without .dll");
        Check(RynthCore.App.Avalonia.PluginNames.Friendly("RynthOracle") == "RynthOracle", "a feed name stays");
        Check(RynthCore.App.Avalonia.PluginNames.Friendly("SomeoneElse.dll") == "SomeoneElse", "another plugin keeps its name");
        foreach (string s in new[] { "RynthDreamWeave", "RynthConquest", "RynthILT", "RynthThreads", "RynthCore.Plugin.RynthAelrynth.dll" })
            Check(RynthCore.App.Avalonia.PluginNames.IsServerPlugin(s), $"{s}: a server plugin");
        foreach (string s in new[] { "RynthAi", "RynthNav", "RynthChat", "RynthNet", "RynthLua", "SomeoneElse" })
            Check(!RynthCore.App.Avalonia.PluginNames.IsServerPlugin(s), $"{s}: a regular plugin");
        Check(RynthCore.App.Avalonia.PluginNames.ServerOf("RynthThreads")?.Name == "DreamWeave", "RynthThreads is DreamWeave's");
        Check(RynthUpdater.DescribeByName("RynthNet").Length > 0 && RynthUpdater.DescribeByName("RynthSort").Length > 0, "every RynthSuite plugin has a line");
    }

    private static void UpdateCardWhatsNew()
    {
        var six = new[] { "One.", "Two.", "Three.", "Four.", "Five.", "Six." };
        var card = UpdateCardModel.Build(new UpdateCardInput { HasCheck = true, CoreUpdateAvailable = true, AvailableVersion = "2026.10.5.1", Changes = six, Notes = "All of it." });
        Check(card.WhatsNew.SequenceEqual(new[] { "One.", "Two.", "Three.", "Four.", "…and 2 more." }), "a few of the changes: " + string.Join(" | ", card.WhatsNew));
        var old = UpdateCardModel.Build(new UpdateCardInput { HasCheck = true, CoreUpdateAvailable = true, AvailableVersion = "2026.10.5.1", Notes = "Old feeds only have notes." });
        Check(old.WhatsNew.SequenceEqual(new[] { "Old feeds only have notes." }), "an old feed shows its notes");
        var upToDate = UpdateCardModel.Build(new UpdateCardInput { HasCheck = true, Changes = six });
        Check(upToDate.WhatsNew.Count == 0, "nothing to install: no what's new");
        Check(UpdateCardModel.WhatsNew(Array.Empty<string>(), new string('n', 500))[0].Length == 300, "long notes are cut");
    }
}
