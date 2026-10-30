using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RynthCore.App.Avalonia;

/// <summary>
/// Roll back: go back to an earlier release when a new one broke something (2026-10-21).
///
/// Every release keeps its own signed manifest beside its files on the server
/// (releases/&lt;release&gt;/update.json; Publish-Update.ps1 uploads it, and did by hand for 2026.10.19.1
/// on). Which releases exist comes from releases.json (unsigned; only used to find candidates); each
/// candidate is offered only once its own manifest verifies, and everything a rollback downloads is
/// checked against that manifest's signed sizes and hashes, exactly like an update. Only releases older
/// than the installed one, and not older than <see cref="FirstRollbackRelease"/>.
///
/// A rollback puts back that release's build of every plugin the player has (the files it ships; other
/// plugins are left alone), then runs that release's installer (the core: engine, launcher, RynthAi),
/// with every game client closed, like a core update. Afterwards updates are held
/// (<see cref="StartHold"/>): no automatic plugin updates while the feed offers nothing newer than the
/// release left behind, so the launcher doesn't put it straight back. The hold ends by itself when a
/// newer release comes out, or when the player updates by hand.
///
/// A launcher from before this feature (the rolled-back one may be) knows nothing of the hold, so a
/// rollback also turns "Update plugins automatically" off, and a later launcher turns it back on when
/// the hold ends (<see cref="HoldTurnedOffAutoUpdate"/>).
/// </summary>
internal sealed partial class RynthUpdater
{
    /// <summary>The oldest release whose signed manifest is kept on the server (2026.10.19.1).</summary>
    public const int FirstRollbackRelease = 2026101901;

    /// <summary>How many earlier releases Roll back offers.</summary>
    public const int RollbackChoices = 5;

    private const long MaxReleasesListBytes = 4L << 20;

    /// <summary>Tests lower it; the launcher uses <see cref="FirstRollbackRelease"/>.</summary>
    internal int RollbackFloor { get; set; } = FirstRollbackRelease;

    /// <summary>An earlier release Roll back can install, from its own verified manifest.</summary>
    public sealed record RollbackTarget(Manifest Manifest)
    {
        public int Release => Manifest.Release;
        public string Version => Manifest.Version;
    }

    /// <summary>What a prepared rollback installs: the verified installer and the player's plugins, staged.</summary>
    public sealed record RollbackPlan(Manifest Manifest, int LeavingRelease, string InstallerPath,
                                      IReadOnlyList<(PluginEntry Entry, string Path, string Staged)> Plugins);

    /// <summary>"2026.10.21.2" → 2026102102 (Publish-Update.ps1's numbering); 0 when it isn't a release version.</summary>
    public static int ReleaseOf(string? version)
    {
        string[] p = (version ?? "").Trim().Split('.');
        if (p.Length != 4) return 0;
        if (!int.TryParse(p[0], out int y) || !int.TryParse(p[1], out int m) || !int.TryParse(p[2], out int d) || !int.TryParse(p[3], out int n))
            return 0;
        if (y < 2000 || y > 2999 || m < 1 || m > 12 || d < 1 || d > 31 || n < 0 || n > 99) return 0;
        return y * 1000000 + m * 10000 + d * 100 + n;
    }

    /// <summary>
    /// The earlier releases Roll back offers, newest first: up to <paramref name="max"/>, older than the
    /// installed release, not older than <see cref="RollbackFloor"/>, each with a manifest that verifies.
    /// A release whose manifest is missing or doesn't verify is skipped.
    /// </summary>
    public async Task<IReadOnlyList<RollbackTarget>> ListRollbackTargetsAsync(int max = RollbackChoices, CancellationToken ct = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("updates are not set up in this build (no signing key)");
        int installed = InstalledCoreRelease;
        if (installed <= 0)
            throw new InvalidOperationException("this RynthCore wasn't installed from a release, so there's nothing to go back from");

        byte[] list = await DownloadBytesAsync(_allowedPrefix + "releases.json", MaxReleasesListBytes, ct);
        var candidates = new List<int>();
        using (JsonDocument doc = JsonDocument.Parse(list))
        {
            foreach (JsonElement r in doc.RootElement.GetProperty("releases").EnumerateArray())
            {
                int release = r.TryGetProperty("version", out JsonElement v) ? ReleaseOf(v.GetString()) : 0;
                if (release > 0 && release < installed && release >= RollbackFloor && !candidates.Contains(release))
                    candidates.Add(release);
            }
        }

        var targets = new List<RollbackTarget>();
        foreach (int release in candidates.OrderByDescending(r => r))
        {
            if (targets.Count >= max) break;
            try { targets.Add(new RollbackTarget(await LoadReleaseManifestAsync(release, ct))); }
            catch (OperationCanceledException) { throw; }
            catch { /* no signed manifest kept for it, or it doesn't verify: not offered */ }
        }
        return targets;
    }

    /// <summary>
    /// <paramref name="release"/>'s own signed manifest (releases/&lt;release&gt;/update.json). Throws unless a
    /// trusted key signed it, it is that release (not another one served under its name) and it is
    /// older than the installed release.
    /// </summary>
    public async Task<Manifest> LoadReleaseManifestAsync(int release, CancellationToken ct = default)
    {
        byte[] envelope = await DownloadBytesAsync($"{_allowedPrefix}releases/{release}/update.json", MaxFeedBytes, ct);
        Manifest m = ParseManifest(VerifyEnvelope(envelope, _trustedKeys));
        if (m.Release != release || m.Core.Release != release)
            throw new InvalidDataException($"releases/{release} holds the manifest of release {m.Release}");
        if (m.Release >= InstalledCoreRelease)
            throw new InvalidDataException($"{m.Version} isn't older than the installed release");
        return m;
    }

    /// <summary>
    /// Downloads and verifies everything <paramref name="target"/> installs: its installer, and its build of
    /// each plugin in <paramref name="pluginPaths"/> that it ships (staged, not installed yet). Nothing is
    /// changed if any download fails.
    /// </summary>
    public async Task<RollbackPlan> PrepareRollbackAsync(RollbackTarget target, IEnumerable<string> pluginPaths,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        Manifest m = target.Manifest;
        int leaving = InstalledCoreRelease;
        string installer = await DownloadInstallerAsync(m.Core, progress, ct);

        string stage = Path.Combine(UpdatesDir, $"rollback-{m.Release}");
        Directory.CreateDirectory(stage);
        var byFile = m.Plugins.ToDictionary(p => p.File, StringComparer.OrdinalIgnoreCase);
        var staged = new Dictionary<PluginEntry, string>();
        var plugins = new List<(PluginEntry, string, string)>();
        foreach (string path in pluginPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path) || !byFile.TryGetValue(Path.GetFileName(path), out PluginEntry? e)) continue;
            if (!staged.TryGetValue(e, out string? file))
            {
                file = Path.Combine(stage, e.File);
                bool have = File.Exists(file) && new FileInfo(file).Length == e.Size &&
                            string.Equals(Sha256File(file), e.Sha256, StringComparison.OrdinalIgnoreCase);
                if (!have)
                {
                    string part = file + ".part";
                    await DownloadVerifiedAsync(e.Url, e.Size, e.Sha256, MaxPluginBytes, part, ct, $"{e.Name} {m.Version}", progress);
                    File.Move(part, file, overwrite: true);
                }
                staged[e] = file;
            }
            plugins.Add((e, path, file));
        }
        return new RollbackPlan(m, leaving, installer, plugins);
    }

    /// <summary>
    /// Puts the staged plugins in place (the replaced file kept as .previous, as an update does) and
    /// returns what changed. Stamped with the release's time, so a later check doesn't take them for a
    /// local build. Call with every game client closed, before running the installer.
    /// </summary>
    public List<string> ApplyRollbackPlugins(RollbackPlan plan)
    {
        var done = new List<string>();
        foreach (var (entry, path, file) in plan.Plugins)
        {
            string fresh = path + ".new";
            File.Copy(file, fresh, overwrite: true);
            File.SetLastWriteTimeUtc(fresh, plan.Manifest.PublishedUtc);
            File.Copy(path, path + ".previous", overwrite: true);
            File.Move(fresh, path, overwrite: true);
            done.Add($"{entry.Name} → {plan.Manifest.Version}");
        }
        return done;
    }

    // ── The hold ───────────────────────────────────────────────────────────────

    /// <summary>The release the player rolled back from (0: no hold).</summary>
    public int HeldThroughRelease => ReadStateInt("holdThrough");

    /// <summary>True when the rollback turned "Update plugins automatically" off (turned back on when the hold ends).</summary>
    public bool HoldTurnedOffAutoUpdate => ReadStateBool("holdTurnedOffAutoUpdate");

    /// <summary>Holds updates through <paramref name="leavingRelease"/> (the release rolled back from).</summary>
    public void StartHold(int leavingRelease, bool turnedOffAutoUpdate) => WriteState(s =>
    {
        s["holdThrough"] = leavingRelease;
        s["holdTurnedOffAutoUpdate"] = turnedOffAutoUpdate;
    });

    /// <summary>Ends a rollback's hold (a newer release is out, or the player updated by hand).</summary>
    public void EndHold() => WriteState(s =>
    {
        s.Remove("holdThrough");
        s.Remove("holdTurnedOffAutoUpdate");
    });
}
