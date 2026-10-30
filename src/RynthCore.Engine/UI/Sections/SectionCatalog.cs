// ============================================================================
//  RynthCore.Engine - UI/Sections/SectionCatalog.cs
//  The Mini Remote's sections: the twelve core ones every world has, and what
//  installed plugins add, laid out for the servers that are active now.
//
//  Pure (no AC, no ImGui, no engine state): tools/SectionsTests compiles it.
//  SectionRegistry.cs feeds it the plugins' manifests, the engine panels that
//  exist and the server verdicts; MiniRemoteFace draws the result.
//
//  A section's body, top to bottom:
//    panes    what fills it: the engine's core panels for it (embedded whole),
//             the Session Stats overview (dropped once a plugin's universal
//             primary surface fills that section, e.g. RynthAi's session
//             stats), and plugins' primary surfaces. More than one pane = a
//             row of sub-tabs.
//    groups   "Server: <name>" for each active server with items here, then
//             "Plugins" for items every world gets: inline plugin surfaces
//             (options and configuration) and command buttons.
//  Core sections always show (with a note when nothing fills them); a plugin's
//  own section shows only while something in it applies to this world.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Engine.Compatibility;

namespace RynthCore.Engine.UI.Sections;

/// <summary>A core section: always on the rail.</summary>
internal sealed record CoreSection(string Id, string Label, string[] Panels, bool Overview = false);

/// <summary>A server and whether it is active for this character now.</summary>
internal sealed record ServerState(string Id, string Name, bool Active, bool Detected, ServerForce Force, bool BuiltIn, string Reason);

internal enum SectionPaneKind
{
    /// <summary>The Session Stats section's engine overview (XP / luminance this session), when no plugin fills it.</summary>
    Overview,
    /// <summary>An engine panel embedded whole.</summary>
    CorePanel,
    /// <summary>A plugin surface (RynthPluginRenderEmbed).</summary>
    Surface,
}

/// <summary>One sub-tab of a section's body.</summary>
internal sealed record SectionPane(SectionPaneKind Kind, string Title, string Panel, SectionContribution? Contribution);

/// <summary>A titled group of a section's items: one active server, or "Plugins" (every world).</summary>
internal sealed record SectionGroup(string Title, ServerState? Server, SectionContribution[] Items);

/// <summary>One section as it should be drawn now.</summary>
internal sealed record SectionView(string Id, string Label, bool Core, int Order, SectionPane[] Panes, SectionGroup[] Groups)
{
    public bool HasContent => Panes.Length > 0 || Groups.Length > 0;
}

internal static class SectionCatalog
{
    /// <summary>The rail entry for the remote itself (RynthAi's remote, or the engine's slot grid).</summary>
    public const string RemoteId = "remote";

    /// <summary>The "char" section's label: the session's XP, luminance and kills.</summary>
    public const string SessionStatsLabel = "Session Stats";

    /// <summary>
    /// The core sections, in rail order, with the engine panels that fill each (the first
    /// that exists is the default sub-tab). Sections without panels are filled by plugins.
    /// </summary>
    public static readonly CoreSection[] Core =
    {
        // Session Stats (was "Char" until 2026.10.5.54; the id stays "char" so saved rail picks,
        // "/rc miniremote section char" and plugin manifests keep working). Character details
        // live in RynthOracle's Character tab now, server picks in Options > Diagnostics.
        new("char", SessionStatsLabel, Array.Empty<string>(), Overview: true),
        new("map", "Map", new[] { "Radar", "Dungeon Map" }),
        new("skills", "Skills", new[] { "Skills" }),
        new("combat", "Combat", new[] { "Damage", "Monsters" }),
        new("nav", "Nav", new[] { "Nav", "RynthNav" }),
        new("inventory", "Inventory", new[] { "Inventory", "Item Count", "Items" }),
        new("crafting", "Crafting", Array.Empty<string>()),
        new("spells", "Spells", Array.Empty<string>()),
        new("chat", "Chat", new[] { "Chat" }),
        new("friends", "Friends", Array.Empty<string>()),
        new("fellowship", "Fellowship", Array.Empty<string>()),
        new("pets", "Pets", Array.Empty<string>()),
    };

    /// <summary>Plugin sections sort after the core ones unless they ask for a smaller order.</summary>
    private const int CoreOrderStep = 100;

    public static CoreSection? FindCore(string id)
    {
        foreach (CoreSection c in Core)
            if (c.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) return c;
        return null;
    }

    /// <summary>
    /// A plugin-declared server's own detection: its launch host or announced world name
    /// matches one of its lists (the same matching as Infinite Leaftide's).
    /// </summary>
    public static bool DetectDeclared(SectionServerDecl server, string? host, string? world, out string reason)
    {
        if (IltDetect.HostMatches(host, server.Hosts, out string hostHit))
        {
            reason = $"host matches \"{hostHit}\"";
            return true;
        }
        if (IltDetect.WorldNameMatches(world, server.WorldNames, out string worldHit))
        {
            reason = $"world name matches \"{worldHit}\"";
            return true;
        }
        reason = server.Hosts.Length + server.WorldNames.Length == 0 ? "no host or world-name list" : "no host or world name matches";
        return false;
    }

    /// <summary>
    /// Every section to show now, in rail order (the remote entry is not included).
    /// <paramref name="panelExists"/> says whether an engine panel is registered.
    /// </summary>
    public static List<SectionView> Build(IReadOnlyList<SectionManifest> manifests, Func<string, bool> panelExists,
                                          IReadOnlyList<ServerState> servers)
    {
        var activeServers = new Dictionary<string, ServerState>(StringComparer.OrdinalIgnoreCase);
        foreach (ServerState s in servers)
            if (s.Active) activeServers[s.Id] = s;

        // Contributions that apply to this world, by section.
        var bySection = new Dictionary<string, List<SectionContribution>>(StringComparer.OrdinalIgnoreCase);
        foreach (SectionManifest m in manifests)
            foreach (SectionContribution c in m.Contributions)
            {
                if (!c.IsUniversal && !activeServers.ContainsKey(c.Server)) continue;
                if (!bySection.TryGetValue(c.Section, out var list)) bySection[c.Section] = list = new List<SectionContribution>();
                list.Add(c);
            }

        var views = new List<SectionView>();
        for (int i = 0; i < Core.Length; i++)
        {
            CoreSection core = Core[i];
            bySection.TryGetValue(core.Id, out var items);
            views.Add(BuildOne(core.Id, core.Label, true, i * CoreOrderStep, core, items, panelExists, servers));
        }

        // Plugin sections: the first declaration of an id wins; never one of the core ids.
        var declared = new Dictionary<string, SectionDecl>(StringComparer.OrdinalIgnoreCase);
        foreach (SectionManifest m in manifests)
            foreach (SectionDecl d in m.Sections)
                if (FindCore(d.Id) == null && !d.Id.Equals(RemoteId, StringComparison.OrdinalIgnoreCase))
                    declared.TryAdd(d.Id, d);
        foreach (SectionDecl d in declared.Values)
        {
            if (!bySection.TryGetValue(d.Id, out var items)) continue;   // nothing for this world
            SectionView v = BuildOne(d.Id, d.Label, false, d.Order, null, items, panelExists, servers);
            if (v.HasContent) views.Add(v);
        }
        // Stable: equal orders keep core before plugin sections, then plugin sections by id.
        return views.OrderBy(v => v.Order).ThenBy(v => v.Core ? 0 : 1).ThenBy(v => v.Id, StringComparer.Ordinal).ToList();
    }

    private static SectionView BuildOne(string id, string label, bool isCore, int order, CoreSection? core,
                                        List<SectionContribution>? items, Func<string, bool> panelExists,
                                        IReadOnlyList<ServerState> servers)
    {
        var panes = new List<SectionPane>();
        if (core != null)
        {
            if (core.Overview) panes.Add(new SectionPane(SectionPaneKind.Overview, label, string.Empty, null));
            foreach (string panel in core.Panels)
                if (SafeExists(panelExists, panel)) panes.Add(new SectionPane(SectionPaneKind.CorePanel, panel, panel, null));
        }

        var universal = new List<SectionContribution>();
        var perServer = new Dictionary<string, List<SectionContribution>>(StringComparer.OrdinalIgnoreCase);
        if (items != null)
        {
            items.Sort(CompareItems);
            foreach (SectionContribution c in items)
            {
                if (c.Primary && c.IsSurface)
                {
                    panes.Add(new SectionPane(SectionPaneKind.Surface, c.Label, string.Empty, c));
                    continue;
                }
                if (c.IsUniversal) universal.Add(c);
                else
                {
                    if (!perServer.TryGetValue(c.Server, out var list)) perServer[c.Server] = list = new List<SectionContribution>();
                    list.Add(c);
                }
            }
        }

        // One session on screen: the engine's own Session Stats overview is a stand-in, dropped
        // when a plugin fills the section with an every-world view of its own (RynthAi's session
        // stats, which RynthTracker, RynthOracle and RynthILT follow too).
        if (core is { Overview: true } && panes.Exists(p => p.Kind == SectionPaneKind.Surface && p.Contribution!.IsUniversal))
            panes.RemoveAll(p => p.Kind == SectionPaneKind.Overview);

        var groups = new List<SectionGroup>();
        foreach (ServerState s in servers)   // the servers' own order: built-in first
            if (s.Active && perServer.TryGetValue(s.Id, out var list))
                groups.Add(new SectionGroup("Server: " + s.Name, s, list.ToArray()));
        if (universal.Count > 0) groups.Add(new SectionGroup("Plugins", null, universal.ToArray()));

        return new SectionView(id, label, isCore, order, panes.ToArray(), groups.ToArray());
    }

    /// <summary>Order, then server-agnostic first, then label: the same list always draws the same way.</summary>
    private static int CompareItems(SectionContribution a, SectionContribution b)
    {
        if (a.Order != b.Order) return a.Order.CompareTo(b.Order);
        if (a.IsUniversal != b.IsUniversal) return a.IsUniversal ? -1 : 1;
        int byLabel = string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
        return byLabel != 0 ? byLabel : string.CompareOrdinal(a.Plugin, b.Plugin);
    }

    private static bool SafeExists(Func<string, bool> panelExists, string panel)
    {
        try { return panelExists(panel); }
        catch { return false; }
    }
}
