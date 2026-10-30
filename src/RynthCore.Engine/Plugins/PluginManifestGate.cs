// ============================================================================
//  RynthCore.Engine - Plugins/PluginManifestGate.cs
//  The engine's use of plugin manifests (RynthCore.PluginSdk.Manifest, compiled in):
//    - before any plugin DLL is loaded, read each one's embedded manifest from the
//      file (no LoadLibrary: a refused NativeAOT plugin would otherwise keep a whole
//      runtime in acclient's 4 GB for good) and decide which may load, in what order;
//    - before a plugin's Init, check the plugins it requires started.
//  A plugin without a manifest is never refused and keeps its place in the list,
//  so a plugin list with no manifests loads exactly as before.
//
//  BCL + the manifest files only, so tools\PluginManifestTests compiles it too.
//  PluginManager owns the logging and the chat notice.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using RynthCore.PluginSdk.Manifest;

namespace RynthCore.Engine.Plugins;

internal static class PluginManifestGate
{
    internal sealed class LoadPlan
    {
        /// <summary>Paths to load, in order, with their manifests (null for a plugin without one).</summary>
        public List<(string Path, RynthPluginManifest? Manifest)> Load { get; } = new();
        /// <summary>Plugins not loaded, with the reason (one sentence, fit for chat).</summary>
        public List<PluginRefusal> Refused { get; } = new();
        /// <summary>Log lines: manifests found, broken manifests, notes.</summary>
        public List<string> Log { get; } = new();
        /// <summary>Server plugins for another server, left out on purpose (logged, never said in chat).</summary>
        public List<string> SkippedForServer { get; } = new();
        /// <summary>
        /// The same plugins with their manifests (each has a <c>Server</c>): Options > Diagnostics lists
        /// their servers, and forcing one On loads its plugin (PluginManager.RequestServerPluginLoad).
        /// </summary>
        public List<(string Path, RynthPluginManifest Manifest)> SkippedServerPlugins { get; } = new();
    }

    /// <summary>
    /// Reads the manifests of <paramref name="paths"/> and resolves them against
    /// <paramref name="engineApi"/>. <paramref name="fileVersion"/> gives a DLL's version resource
    /// (for dependency versions of plugins whose manifest has none); null skips it.
    /// <paramref name="connectHost"/> (the client's -h host, "" when unknown) picks which server
    /// plugins load (<see cref="SelectServerPlugins"/>); <paramref name="loadAllServerPlugins"/>
    /// (engine.json LoadAllServerPlugins) turns that off.
    /// </summary>
    public static LoadPlan Plan(IReadOnlyList<string> paths, uint engineApi, Func<string, string>? fileVersion = null,
                                string? connectHost = null, bool loadAllServerPlugins = false)
    {
        var plan = new LoadPlan();
        var candidates = new List<PluginCandidate>(paths.Count);
        int withManifest = 0;
        foreach (string path in paths)
        {
            RynthPluginManifest? manifest = null;
            try
            {
                manifest = PluginManifestReader.Read(path);
            }
            catch (InvalidDataException ex)
            {
                // A broken manifest is a build problem, not the player's: load it the old way.
                plan.Log.Add($"{Path.GetFileName(path)}: manifest unreadable ({ex.Message}) - loading it as a plugin without one.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                plan.Log.Add($"{Path.GetFileName(path)}: could not read its manifest ({ex.Message}) - loading it as a plugin without one.");
            }

            string fv = "";
            if (fileVersion != null && string.IsNullOrEmpty(manifest?.Version))
            {
                try { fv = fileVersion(path) ?? ""; } catch { fv = ""; }
            }
            if (manifest != null)
            {
                withManifest++;
                plan.Log.Add($"{Path.GetFileName(path)}: manifest {Describe(manifest)}.");
            }
            candidates.Add(new PluginCandidate(path, manifest, fv));
        }

        HashSet<string> skip = SelectServerPlugins(candidates, connectHost, loadAllServerPlugins, plan.Log);
        if (skip.Count > 0)
        {
            foreach (PluginCandidate c in candidates)
                if (skip.Contains(c.Key) && c.Manifest?.Server != null)
                    plan.SkippedServerPlugins.Add((c.Key, c.Manifest));
            candidates.RemoveAll(c => skip.Contains(c.Key));
            plan.SkippedForServer.AddRange(skip);
        }

        PluginResolution res = PluginManifestResolver.Resolve(candidates, engineApi);
        foreach (PluginCandidate c in res.Order)
            plan.Load.Add((c.Key, c.Manifest));
        plan.Refused.AddRange(res.Refused);
        foreach (string note in res.Notes)
            plan.Log.Add(note);
        if (withManifest == 0 && paths.Count > 0)
            plan.Log.Add("no plugin has a manifest; loading them as listed.");
        return plan;
    }

    /// <summary>
    /// The server plugins to leave out (their paths). With <paramref name="connectHost"/> known,
    /// only the plugins that list it load; when none does, none load (every server plugin is a
    /// runtime in a 32-bit client, and a dozen of them for a server nobody claims only cost address
    /// space). A skipped plugin still loads late when its server announces a world name it lists,
    /// or when the character forces its server On (SectionRegistry), so a server that moved to a
    /// new host still gets its plugin. With no host (an externally launched client) all load.
    /// Nothing is left out when <paramref name="loadAll"/> is set.
    /// </summary>
    internal static HashSet<string> SelectServerPlugins(IReadOnlyList<PluginCandidate> candidates, string? connectHost,
                                                        bool loadAll, List<string> log)
    {
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var servers = new List<PluginCandidate>();
        foreach (PluginCandidate c in candidates)
            if (c.Manifest?.Server != null) servers.Add(c);
        if (servers.Count == 0) return skip;
        if (loadAll)
        {
            log.Add($"{servers.Count} server plugin(s): all load (engine.json LoadAllServerPlugins).");
            return skip;
        }
        string host = (connectHost ?? "").Trim();
        if (host.Length == 0)
        {
            log.Add($"{servers.Count} server plugin(s): no connect host on the command line, so all load (each shows only on its own server).");
            return skip;
        }

        var claimants = new List<string>();
        foreach (PluginCandidate c in servers)
            if (c.Manifest!.Server!.MatchesHost(host, out string hit))
                claimants.Add($"{c.Manifest.Name} ({c.Manifest.Server.Id}, \"{hit}\")");
        if (claimants.Count == 0)
            log.Add($"{servers.Count} server plugin(s): none lists host {host}, so none load now (one loads once its world name is announced, or when its server is picked).");

        foreach (PluginCandidate c in servers)
        {
            if (c.Manifest!.Server!.MatchesHost(host, out _)) continue;
            skip.Add(c.Key);
            if (claimants.Count > 0)
                log.Add($"{Path.GetFileName(c.Key)}: not loaded - it is for {c.Manifest.Server.Name}, and host {host} is {string.Join(", ", claimants)}.");
        }
        return skip;
    }

    /// <summary>
    /// Null when every plugin <paramref name="manifest"/> requires has started; otherwise why not.
    /// <paramref name="started"/> answers by plugin name: true started, false loaded but not
    /// started (failed or refused at Init), null not loaded at all.
    /// </summary>
    public static string? CheckRequiredStarted(RynthPluginManifest? manifest, Func<string, bool?> started)
    {
        if (manifest == null) return null;
        foreach (RynthPluginDependency d in manifest.Dependencies)
        {
            if (d.Optional) continue;
            bool? s = started(d.Name);
            if (s == true) continue;
            return s == false
                ? $"{manifest.Name} needs {d.Name}, which failed to start"
                : $"{manifest.Name} needs {d.Name}, which is not loaded";
        }
        return null;
    }

    /// <summary>"RynthOracle 2026.10.5.1, API 71+, needs RynthAi (optional)".</summary>
    public static string Describe(RynthPluginManifest m)
    {
        string api = m.MaxEngineApi != 0 ? $"API {m.MinEngineApi}-{m.MaxEngineApi}" : $"API {m.MinEngineApi}+";
        string s = $"{m.Name}{(m.Version.Length > 0 ? " " + m.Version : "")}, {api}";
        if (m.Dependencies.Count > 0)
        {
            var parts = new List<string>(m.Dependencies.Count);
            foreach (RynthPluginDependency d in m.Dependencies)
                parts.Add(d.Name + (d.MinVersion.Length > 0 ? " " + d.MinVersion + "+" : "") + (d.Optional ? " (optional)" : ""));
            s += ", needs " + string.Join(", ", parts);
        }
        if (m.Server != null) s += $", server {m.Server.Id}";
        return s;
    }
}
