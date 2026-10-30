// ============================================================================
//  RynthCore.Engine - UI/Sections/SectionRegistry.cs
//  The live state behind the Mini Remote's sections: which servers are active
//  (one world check for the engine and every plugin), what the installed
//  plugins add (their RynthPluginGetSectionsJson manifests), and the section
//  layout SectionCatalog builds from those.
//
//  Servers:
//    built in   Infinite Leaftide (IltInfo) and Aelrynth (ServerInfo): the engine's
//               own detection, with the per-character override (ServerForceStore).
//    declared   any other id a plugin's manifest names: detected from its host /
//               world-name lists, with the same per-character override.
//    not loaded installed server plugins the connect host left out (NotLoadedServers):
//               listed so Options > Diagnostics can pick them; forced On, the plugin loads.
//  RynthAi follows these verdicts (ServerMenus pushes them), so the engine and
//  RynthAi can't disagree about which world this is.
//
//  The engine's own contributions (Bounties, Guardian, Progression) are a
//  manifest like a plugin's, added at init by which plugins are installed.
//
//  Threads: Tick runs on AC's render thread (ImGuiController frame); readers
//  on any thread get the last published snapshot (volatile, immutable).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading;
using RynthCore.Engine.Compatibility;

namespace RynthCore.Engine.UI.Sections;

internal static class SectionRegistry
{
    /// <summary>The engine's own entry in the manifests (its contributions' "plugin").</summary>
    public const string EnginePlugin = "RynthCore";

    /// <summary>How often the plugins' manifests are read again (they change with server features).</summary>
    private const long ManifestPollMs = 2_000;
    /// <summary>How often the server verdicts and the layout are rebuilt.</summary>
    private const long RebuildMs = 1_000;

    private sealed record BuiltInServer(string Id, string Name, Func<bool> Active, Func<(bool Detected, string Reason)> Detect);

    /// <summary>Everything the UI reads, published together.</summary>
    internal sealed record Snapshot(
        IReadOnlyList<ServerState> Servers,
        IReadOnlyList<SectionView> Sections,
        IReadOnlyList<SectionManifest> Manifests,
        int Revision)
    {
        public static readonly Snapshot Empty =
            new(Array.Empty<ServerState>(), Array.Empty<SectionView>(), Array.Empty<SectionManifest>(), 0);

        public SectionView? Find(string id)
        {
            foreach (SectionView v in Sections)
                if (v.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) return v;
            return null;
        }

        public ServerState? Server(string id)
        {
            foreach (ServerState s in Servers)
                if (s.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }
    }

    private static readonly BuiltInServer[] BuiltIns =
    {
        new(IltInfo.ServerId, "Infinite Leaftide", static () => IltInfo.IsIlt, static () =>
        {
            IltVerdict v = IltInfo.Detected;
            return (v.IsIlt, v.Reason);
        }),
        new(ServerInfo.ServerId, "Aelrynth", static () => ServerInfo.IsAelrynth, static () =>
        {
            ServerVerdict v = ServerInfo.Detected;
            return (v.IsAelrynth, v.Reason);
        }),
    };

    private static readonly object Gate = new();
    /// <summary>The engine's items; Unless = a plugin whose own manifest replaces the item ("" = always).</summary>
    private static readonly List<(SectionContribution Item, string Unless)> EngineItems = new();   // under Gate
    private static readonly Dictionary<string, (string Json, SectionManifest Manifest)> PluginManifests =
        new(StringComparer.OrdinalIgnoreCase);                                // render thread only

    private static volatile Snapshot _snapshot = Snapshot.Empty;
    private static long _nextPollMs, _nextRebuildMs;
    private static int _lastForceVersion = -1;
    private static int _revision;
    private static string _lastSignature = string.Empty;

    /// <summary>The current servers and sections. Any thread.</summary>
    public static Snapshot Current => _snapshot;

    /// <summary>True for an id the engine detects itself.</summary>
    public static bool IsBuiltInServer(string id)
    {
        foreach (BuiltInServer b in BuiltIns)
            if (b.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// An engine contribution (an engine panel or /rc command for a section), added at init.
    /// Same rules as a plugin's: the command is checked by SectionManifestParser. With
    /// <paramref name="unlessPlugin"/>, the item is left out while that plugin publishes a
    /// manifest of its own (a stand-in for plugins too old to describe themselves).
    /// </summary>
    public static void AddEngineItem(string section, string server, string label, string command, string tooltip,
                                     int order = 1000, string unlessPlugin = "")
    {
        string cmd = SectionManifestParser.Command(command);
        string sec = SectionManifestParser.Id(section);
        if (cmd.Length == 0 || sec.Length == 0) return;
        var item = new SectionContribution(EnginePlugin, sec, SectionManifestParser.Id(server), label, string.Empty,
            cmd, tooltip, Primary: false, Enabled: true, Note: string.Empty, Width: 0, Height: 0, Order: order);
        lock (Gate) EngineItems.Add((item, unlessPlugin));
        Invalidate();
    }

    /// <summary>True when a plugin whose name contains <paramref name="plugin"/> publishes a manifest. Render thread.</summary>
    private static bool HasManifestFrom(string plugin)
    {
        foreach (string name in PluginManifests.Keys)
            if (name.Contains(plugin, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Rebuilds on the next Tick (an override or the character changed). Any thread.</summary>
    public static void Invalidate()
    {
        Volatile.Write(ref _nextRebuildMs, 0);
        IltInfo.Invalidate();
        ServerInfo.Invalidate();
    }

    /// <summary>Reads the manifests again on the next Tick. Any thread.</summary>
    public static void RefreshManifests() => Volatile.Write(ref _nextPollMs, 0);

    /// <summary>Render thread, every frame: cheap until a poll or rebuild is due.</summary>
    public static void Tick()
    {
        long now = Environment.TickCount64;
        bool changed = false;
        if (now >= Volatile.Read(ref _nextPollMs))
        {
            Volatile.Write(ref _nextPollMs, now + ManifestPollMs);
            changed |= PollManifests();
        }
        int forceVersion = ServerForceStore.Version;
        if (forceVersion != _lastForceVersion)
        {
            _lastForceVersion = forceVersion;
            IltInfo.Invalidate();
            ServerInfo.Invalidate();
            changed = true;
        }
        if (!changed && now < Volatile.Read(ref _nextRebuildMs)) return;
        Volatile.Write(ref _nextRebuildMs, now + RebuildMs);
        Rebuild();
    }

    /// <summary>Reads every plugin's manifest; re-parses only the ones whose text changed.</summary>
    private static bool PollManifests()
    {
        bool changed = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string plugin, string json) in Plugins.PluginManager.CollectSectionManifests())
        {
            seen.Add(plugin);
            if (PluginManifests.TryGetValue(plugin, out var old) && old.Json == json) continue;
            SectionManifest m = SectionManifestParser.Parse(plugin, json, out string error);
            if (error.Length > 0) RynthLog.UI($"Sections: {plugin}'s manifest was not read - {error}.");
            PluginManifests[plugin] = (json, m);
            changed = true;
        }
        // A plugin that unloaded (or stopped answering) takes its sections with it.
        var gone = new List<string>();
        foreach (string plugin in PluginManifests.Keys)
            if (!seen.Contains(plugin)) gone.Add(plugin);
        foreach (string plugin in gone) { PluginManifests.Remove(plugin); changed = true; }
        return changed;
    }

    private static void Rebuild()
    {
        var manifests = new List<SectionManifest>();
        var engineItems = new List<SectionContribution>();
        lock (Gate)
            foreach ((SectionContribution item, string unless) in EngineItems)
                if (unless.Length == 0 || !HasManifestFrom(unless)) engineItems.Add(item);
        manifests.Add(new SectionManifest(EnginePlugin, Array.Empty<SectionServerDecl>(), Array.Empty<SectionDecl>(), engineItems.ToArray()));
        foreach (var entry in PluginManifests.Values) manifests.Add(entry.Manifest);

        List<ServerState> servers = BuildServers(manifests);

        // Installed server plugins the connect host left out: listed so they can be picked, and
        // loaded as soon as this character forces their server On (picker, chat or saved override).
        List<NotLoadedServer> notLoaded = NotLoadedServerPlugins();
        NotLoadedServers.Append(servers, notLoaded, ServerForceStore.Get);
        foreach (string id in NotLoadedServers.ToLoad(notLoaded, ServerForceStore.Get, AccountHooks.AnnouncedWorldName))
            Plugins.PluginManager.RequestServerPluginLoad(id);

        List<SectionView> sections = SectionCatalog.Build(manifests, ImGuiBackend.ImGuiPanelHost.CanEmbed, servers);

        // Bump the revision only when what the UI shows changed, so the menus and the push stay quiet.
        string signature = Signature(servers, sections);
        if (signature != _lastSignature)
        {
            _lastSignature = signature;
            _revision++;
        }
        _snapshot = new Snapshot(servers, sections, manifests, _revision);
        // The windows plugins draw (their manifests' "windows"): hosted as panels while wanted.
        try { PluginWindows.Sync(manifests, servers); }
        catch (Exception ex) { RynthLog.UI($"PluginWindows: sync failed - {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>The built-in servers, then every server a manifest declares with a new id.</summary>
    private static List<ServerState> BuildServers(List<SectionManifest> manifests)
    {
        var list = new List<ServerState>();
        foreach (BuiltInServer b in BuiltIns)
        {
            bool active = false, detected = false;
            string reason = string.Empty;
            try
            {
                active = b.Active();
                (detected, reason) = b.Detect();
            }
            catch (Exception ex) { reason = "check failed: " + ex.Message; }
            list.Add(new ServerState(b.Id, b.Name, active, detected, ServerForceStore.Get(b.Id), true, reason));
        }

        string host = ServerInfo.LaunchHost;
        string? world = AccountHooks.AnnouncedWorldName;
        foreach (SectionManifest m in manifests)
            foreach (SectionServerDecl d in m.Servers)
            {
                if (IsBuiltInServer(d.Id) || list.Exists(s => s.Id.Equals(d.Id, StringComparison.OrdinalIgnoreCase))) continue;
                bool detected = SectionCatalog.DetectDeclared(d, host, world, out string reason);
                ServerForce force = ServerForceStore.Get(d.Id);
                list.Add(new ServerState(d.Id, d.Name, ServerForceText.Apply(force, detected), detected, force, false,
                    $"{reason} (declared by {m.Plugin})"));
            }
        return list;
    }

    /// <summary>PluginManager's skipped server plugins, as the pure list NotLoadedServers takes.</summary>
    private static List<NotLoadedServer> NotLoadedServerPlugins()
    {
        IReadOnlyList<Plugins.SkippedServerPlugin> skipped = Plugins.PluginManager.SkippedServerPlugins;
        var list = new List<NotLoadedServer>(skipped.Count);
        foreach (Plugins.SkippedServerPlugin s in skipped)
            list.Add(new NotLoadedServer(SectionManifestParser.Id(s.ServerId), s.ServerName, s.PluginName, s.Manifest.Server!.WorldNames.ToArray()));
        return list;
    }

    /// <summary>A compact text of what the menus and the rail show, to detect a change.</summary>
    private static string Signature(List<ServerState> servers, List<SectionView> sections)
    {
        var sb = new System.Text.StringBuilder(256);
        foreach (ServerState s in servers)
            sb.Append(s.Id).Append(s.Active ? '+' : '-').Append((int)s.Force).Append(';');
        foreach (SectionView v in sections)
        {
            sb.Append('|').Append(v.Id).Append(':');
            foreach (SectionPane p in v.Panes)
                sb.Append(p.Title).Append(p.Contribution?.Enabled == false ? '0' : '1').Append('/');
            foreach (SectionGroup g in v.Groups)
                foreach (SectionContribution c in g.Items)
                    sb.Append(',').Append(c.Label).Append(c.Enabled ? '1' : '0');
        }
        return sb.ToString();
    }
}
