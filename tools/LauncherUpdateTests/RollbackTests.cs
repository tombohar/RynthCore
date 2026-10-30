using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RynthCore.App.Avalonia;

// Roll back (RynthUpdater.Rollback.cs): the earlier releases offered, their own signed manifests, what a
// rollback downloads and puts in place, and the hold afterwards.
internal static partial class Program
{
    private const int RbInstalled = 2026100501;   // the feed's release (WriteFeed), installed here

    private static async Task RollbackTests()
    {
        await Run(nameof(RollbackReleaseNumbers), RollbackReleaseNumbers);
        await Run(nameof(RollbackOffersVerifiedEarlierReleases), RollbackOffersVerifiedEarlierReleases);
        await Run(nameof(RollbackRefusesForeignOrForwardManifests), RollbackRefusesForeignOrForwardManifests);
        await Run(nameof(RollbackPreparesThenApplies), RollbackPreparesThenApplies);
        await Run(nameof(RollbackFailedDownloadChangesNothing), RollbackFailedDownloadChangesNothing);
        await Run(nameof(RollbackHoldsUntilANewerRelease), RollbackHoldsUntilANewerRelease);
        await Run(nameof(RollbackHoldEndsWhenUpdatedByHand), RollbackHoldEndsWhenUpdatedByHand);
        await Run(nameof(RollbackCardWhileHeld), RollbackCardWhileHeld);
    }

    private static Feed RollbackSetup(string tag)
    {
        Feed f = Setup(tag);
        File.WriteAllText(Path.Combine(f.Dir, "app", "release.txt"), RbInstalled.ToString());
        f.Upd.UpdatesDir = Path.Combine(f.Dir, "updates");
        f.Upd.RollbackFloor = 2026100101;
        return f;
    }

    private static string VersionOf(int release) =>
        $"{release / 1000000}.{release / 10000 % 100}.{release / 100 % 100}.{release % 100}";

    private static byte[] Envelope(Dictionary<string, object?> manifest, ECDsa? key = null)
    {
        key ??= Key;
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(manifest);
        byte[] sig = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            payload = Convert.ToBase64String(payload),
            signatures = new[] { new { key = RynthUpdater.KeyId(key.ExportSubjectPublicKeyInfo()), sig = Convert.ToBase64String(sig) } },
        }));
    }

    /// <summary>
    /// An earlier release's own signed manifest at releases/&lt;release&gt;/update.json, its installer and its
    /// plugins (RynthAi, RynthChat, RynthNav as "v1"). <paramref name="claim"/>: the release written inside
    /// (another one than its folder, for the test that refuses it).
    /// </summary>
    private static DateTime ServeRelease(string tag, int release, int? claim = null, ECDsa? key = null, string? tamper = null)
    {
        int inside = claim ?? release;
        DateTime published = new(release / 1000000, release / 10000 % 100, release / 100 % 100, 12, 0, 0, DateTimeKind.Utc);
        var plugins = new List<Dictionary<string, object>>();
        foreach (string name in new[] { "RynthAi", "RynthChat", "RynthNav" })
        {
            byte[] body = Body(name, "v1-" + release);
            string rel = $"{tag}/releases/{release}/plugins/{FileOf(name)}";
            lock (Served) Served[rel] = name == tamper ? Encoding.UTF8.GetBytes("tampered " + name) : body;
            plugins.Add(new Dictionary<string, object>
            {
                ["name"] = name, ["file"] = FileOf(name), ["url"] = _base + rel, ["size"] = body.Length, ["sha256"] = Sha(body),
            });
        }
        byte[] setup = Encoding.UTF8.GetBytes("installer " + release);
        lock (Served) Served[$"{tag}/releases/{release}/RynthCore-Setup.exe"] = setup;
        var manifest = new Dictionary<string, object?>
        {
            ["schema"] = 1, ["release"] = inside, ["version"] = VersionOf(inside),
            ["published"] = published.ToString("yyyy-MM-ddTHH:mm:ssZ"), ["notes"] = "Notes " + inside,
            ["changes"] = new[] { $"Change one of {inside}.", "Change two.", "Change three.", "Change four." },
            ["core"] = new Dictionary<string, object>
            {
                ["release"] = inside, ["version"] = VersionOf(inside),
                ["url"] = _base + $"{tag}/releases/{release}/RynthCore-Setup.exe", ["size"] = setup.Length, ["sha256"] = Sha(setup),
            },
            ["plugins"] = plugins,
        };
        lock (Served) Served[$"{tag}/releases/{release}/update.json"] = Envelope(manifest, key);
        return published;
    }

    private static void ServeReleasesList(string tag, params int[] releases)
    {
        var list = releases.Select(r => new { version = VersionOf(r), date = "x", changes = new[] { "c" } }).ToArray();
        lock (Served) Served[$"{tag}/releases.json"] = JsonSerializer.SerializeToUtf8Bytes(new { releases = list });
    }

    /// <summary>The current feed, as a release of its own (WriteFeed's is fixed at <see cref="RbInstalled"/>).</summary>
    private static void ServeFeed(string tag, int release)
    {
        var plugins = new List<Dictionary<string, object>>();
        foreach (string name in FeedPlugins)
        {
            byte[] body = Body(name, "v3-" + release);
            string rel = $"{tag}/releases/{release}/plugins/{FileOf(name)}";
            lock (Served) Served[rel] = body;
            plugins.Add(new Dictionary<string, object> { ["name"] = name, ["file"] = FileOf(name), ["url"] = _base + rel, ["size"] = body.Length, ["sha256"] = Sha(body) });
        }
        var manifest = new Dictionary<string, object?>
        {
            ["schema"] = 1, ["release"] = release, ["version"] = VersionOf(release),
            ["published"] = Published.AddDays(1).ToString("yyyy-MM-ddTHH:mm:ssZ"), ["notes"] = "n",
            ["core"] = new Dictionary<string, object> { ["release"] = release, ["version"] = VersionOf(release), ["url"] = _base + $"{tag}/x.exe", ["size"] = 1, ["sha256"] = "00" },
            ["plugins"] = plugins,
        };
        lock (Served) Served[$"{tag}/update.json"] = Envelope(manifest);
    }

    private static Task RollbackReleaseNumbers()
    {
        Check(RynthUpdater.ReleaseOf("2026.10.21.2") == 2026102102, "2026.10.21.2 → 2026102102");
        Check(RynthUpdater.ReleaseOf("2026.9.26.3") == 2026092603, "a one-digit month and day");
        Check(RynthUpdater.ReleaseOf("2026.10.5.20") == 2026100520, "a two-digit release of the day");
        Check(RynthUpdater.ReleaseOf("0.0.0") == 0 && RynthUpdater.ReleaseOf("2026.13.1.1") == 0 && RynthUpdater.ReleaseOf(null) == 0,
            "not a release: 0");
        Check(RynthUpdater.FirstRollbackRelease == 2026101901, "the launcher goes back to 2026.10.19.1 at the oldest");
        return Task.CompletedTask;
    }

    private static async Task RollbackOffersVerifiedEarlierReleases()
    {
        Feed f = RollbackSetup("rb-list");
        ServeRelease("rb-list", 2026100402);
        ServeRelease("rb-list", 2026100301, claim: 2026100302);          // holds another release's manifest
        ServeRelease("rb-list", 2026100201);
        ServeRelease("rb-list", 2026100105, key: ECDsa.Create(ECCurve.NamedCurves.nistP256));   // signed by a stranger
        ServeRelease("rb-list", 2026093001);                              // below the floor
        // 2026.10.4.1 is listed but has no manifest kept.
        ServeReleasesList("rb-list", RbInstalled, 2026100402, 2026100401, 2026100301, 2026100201, 2026100105, 2026093001);

        var targets = await f.Upd.ListRollbackTargetsAsync();
        Check(string.Join(",", targets.Select(t => t.Version)) == "2026.10.4.2,2026.10.2.1",
            "offered: older, verified, its own release, not below the floor: " + string.Join(",", targets.Select(t => t.Version)));
        Check(targets[0].Manifest.Changes.Count == 4 && targets[0].Manifest.Changes[0] == "Change one of 2026100402.",
            "the changes shown come from the signed manifest");
        Check((await f.Upd.ListRollbackTargetsAsync(max: 1)).Count == 1, "at most max");
    }

    private static async Task RollbackRefusesForeignOrForwardManifests()
    {
        Feed f = RollbackSetup("rb-refuse");
        ServeRelease("rb-refuse", 2026100301, claim: 2026100302);
        ServeRelease("rb-refuse", 2026100601);   // newer than installed
        ServeRelease("rb-refuse", 2026100401, key: ECDsa.Create(ECCurve.NamedCurves.nistP256));

        async Task<bool> Refused(int release)
        {
            try { await f.Upd.LoadReleaseManifestAsync(release); return false; }
            catch (InvalidDataException) { return true; }
        }
        Check(await Refused(2026100301), "another release's manifest under its name: refused");
        Check(await Refused(2026100601), "a release newer than the installed one: refused (that's an update, not a rollback)");
        Check(await Refused(2026100401), "a manifest no trusted key signed: refused");
        bool missing = false;
        try { await f.Upd.LoadReleaseManifestAsync(2026100101); }
        catch (HttpRequestException) { missing = true; }
        Check(missing, "a release with nothing kept: the download fails (and ListRollbackTargets skips it)");
    }

    private static async Task RollbackPreparesThenApplies()
    {
        Feed f = RollbackSetup("rb-apply");
        DateTime published = ServeRelease("rb-apply", 2026100402);
        ServeReleasesList("rb-apply", RbInstalled, 2026100402);
        string chat = Place(f, "RynthChat");
        string mine = Path.Combine(f.Suite, "RynthMine", "RynthCore.Plugin.RynthMine.dll");   // not shipped by the release
        Directory.CreateDirectory(Path.GetDirectoryName(mine)!);
        File.WriteAllText(mine, "my own build");
        var paths = new List<string> { f.RynthAiPath, chat, mine };

        var target = (await f.Upd.ListRollbackTargetsAsync()).Single();
        var plan = await f.Upd.PrepareRollbackAsync(target, paths);
        Check(plan.LeavingRelease == RbInstalled && plan.Manifest.Release == 2026100402, "the plan: from the installed release to the chosen one");
        Check(string.Join(",", plan.Plugins.Select(p => p.Entry.Name).OrderBy(n => n)) == "RynthAi,RynthChat",
            "only the player's plugins the release ships (not RynthNav, not their own): " + string.Join(",", plan.Plugins.Select(p => p.Entry.Name)));
        Check(File.Exists(plan.InstallerPath) && File.ReadAllText(plan.InstallerPath) == "installer 2026100402", "the release's installer, verified");
        Check(File.ReadAllBytes(f.RynthAiPath).SequenceEqual(Body("RynthAi")), "prepared, nothing installed yet");

        List<string> done = f.Upd.ApplyRollbackPlugins(plan);
        Check(done.Count == 2 && done.All(d => d.EndsWith("→ 2026.10.4.2")), "two plugins put back: " + string.Join("; ", done));
        Check(File.ReadAllBytes(f.RynthAiPath).SequenceEqual(Body("RynthAi", "v1-2026100402"))
              && File.ReadAllBytes(chat).SequenceEqual(Body("RynthChat", "v1-2026100402")), "RynthAi and RynthChat are the release's builds");
        Check(File.ReadAllBytes(f.RynthAiPath + ".previous").SequenceEqual(Body("RynthAi")), "the replaced file kept as .previous");
        Check(File.GetLastWriteTimeUtc(f.RynthAiPath) == published, "stamped with the release's time (not taken for a local build)");
        Check(File.ReadAllText(mine) == "my own build", "a plugin the release doesn't ship is left alone");
    }

    private static async Task RollbackFailedDownloadChangesNothing()
    {
        Feed f = RollbackSetup("rb-fail");
        ServeRelease("rb-fail", 2026100402, tamper: "RynthChat");
        ServeReleasesList("rb-fail", RbInstalled, 2026100402);
        string chat = Place(f, "RynthChat");
        var target = (await f.Upd.ListRollbackTargetsAsync()).Single();
        bool threw = false;
        try { await f.Upd.PrepareRollbackAsync(target, new[] { f.RynthAiPath, chat }); }
        catch (InvalidDataException) { threw = true; }
        Check(threw, "a download that doesn't match the signed hash stops the rollback");
        Check(File.ReadAllBytes(f.RynthAiPath).SequenceEqual(Body("RynthAi")) && File.ReadAllBytes(chat).SequenceEqual(Body("RynthChat")),
            "and nothing installed is touched");
        Check(f.Upd.HeldThroughRelease == 0, "and no hold started");
    }

    private static async Task RollbackHoldsUntilANewerRelease()
    {
        Feed f = RollbackSetup("rb-hold");
        // Rolled back from 2026.10.5.1 to 2026.10.4.2: the installer wrote the older release.txt.
        f.Upd.StartHold(RbInstalled, turnedOffAutoUpdate: true);
        File.WriteAllText(Path.Combine(f.Dir, "app", "release.txt"), "2026100402");

        var c = await f.Upd.CheckAsync(new[] { f.RynthAiPath });
        Check(c.Held && !c.HoldEnded && c.HoldThrough == RbInstalled, "the feed still offers the release left behind: held");
        Check(c.CoreUpdateAvailable, "going forward stays possible (by hand)");
        Check(f.Upd.HeldThroughRelease == RbInstalled && f.Upd.HoldTurnedOffAutoUpdate, "the replay guard's write keeps the hold");

        ServeFeed("rb-hold", 2026100601);
        var c2 = await f.Upd.CheckAsync(new[] { f.RynthAiPath });
        Check(!c2.Held && c2.HoldEnded, "a newer release than the one left behind: the hold is over");
        f.Upd.EndHold();
        Check(f.Upd.HeldThroughRelease == 0 && !f.Upd.HoldTurnedOffAutoUpdate, "EndHold clears both");
        var c3 = await f.Upd.CheckAsync(new[] { f.RynthAiPath });
        Check(!c3.Held && !c3.HoldEnded && c3.HoldThrough == 0, "no hold: nothing to say");
    }

    private static async Task RollbackHoldEndsWhenUpdatedByHand()
    {
        Feed f = RollbackSetup("rb-hand");
        f.Upd.StartHold(RbInstalled, turnedOffAutoUpdate: false);
        // release.txt still says 2026100501: the player went forward again by hand (or the rollback didn't install).
        var c = await f.Upd.CheckAsync(new[] { f.RynthAiPath });
        Check(!c.Held && c.HoldEnded, "installed release at or past the hold: it's over");
    }

    private static Task RollbackCardWhileHeld()
    {
        var card = UpdateCardModel.Build(new UpdateCardInput
        {
            HasCheck = true, InstalledVersion = "2026.10.4.2", CoreUpdateAvailable = true, AvailableVersion = "2026.10.5.1",
            HeldFromVersion = "2026.10.5.1", PendingPluginUpdates = new[] { "RynthAi" },
        });
        Check(card.Kind == UpdateCardKind.Held && card.Title == "RynthCore 2026.10.4.2 - rolled back", "held: " + card.Title);
        Check(card.Detail.Contains("went back from 2026.10.5.1") && card.Detail.Contains("on hold"), "says why: " + card.Detail);
        Check(card.ActionButton == UpdateCardModel.UpdateCoreButton, "Update RynthCore still goes forward");
        var normal = UpdateCardModel.Build(new UpdateCardInput
        {
            HasCheck = true, InstalledVersion = "2026.10.4.2", CoreUpdateAvailable = true, AvailableVersion = "2026.10.5.1",
        });
        Check(normal.Kind == UpdateCardKind.CoreUpdate, "without a hold: the usual update card");
        return Task.CompletedTask;
    }
}
