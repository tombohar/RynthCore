// ============================================================================
//  RynthCore.Engine - UI/Sections/ServerSelection.cs
//  "Which server is this?" as one choice, on top of the per-server overrides
//  (ServerForce): the Diagnostics page's server picker and "/rc server pick".
//
//    pick <id>   that server forced On, every other server forced Off, so
//                exactly one server is active whatever detection says;
//    pick auto   every override back to Auto (detection decides again).
//
//  Also the questions every reader of the server list asks the same way: which
//  server is the active one, and which plugin owns a server (the one plugins
//  ask for its settings, RynthServerPack's "RynthServer.Profile").
//
//  Pure (no AC, no engine state, no I/O): tools/SectionsTests compiles it.
//  ServerSelectionStore.cs applies a plan to ServerForceStore.
// ============================================================================

using System;
using System.Collections.Generic;

namespace RynthCore.Engine.UI.Sections;

internal static class ServerSelection
{
    /// <summary>The pick that hands the choice back to detection.</summary>
    public const string Auto = "auto";

    /// <summary>
    /// The override each server gets for <paramref name="pick"/>: Auto for all with
    /// <see cref="Auto"/> (or ""), else On for the picked id and Off for the rest. Null when the
    /// pick names no server in <paramref name="servers"/>.
    /// </summary>
    public static List<(string Id, ServerForce Force)>? Plan(IReadOnlyList<ServerState> servers, string? pick)
    {
        string id = (pick ?? string.Empty).Trim();
        bool auto = id.Length == 0 || id.Equals(Auto, StringComparison.OrdinalIgnoreCase);
        if (!auto && Find(servers, id) == null) return null;

        var plan = new List<(string, ServerForce)>(servers.Count);
        foreach (ServerState s in servers)
        {
            ServerForce force = auto ? ServerForce.Auto
                : s.Id.Equals(id, StringComparison.OrdinalIgnoreCase) ? ServerForce.On : ServerForce.Off;
            plan.Add((s.Id, force));
        }
        return plan;
    }

    /// <summary>
    /// The current pick: <see cref="Auto"/> when no override is set, a server id when exactly that
    /// server is forced On and none other is On, or null for any other mix (overrides set one by
    /// one with "/rc server &lt;id&gt; on|off").
    /// </summary>
    public static string? CurrentPick(IReadOnlyList<ServerState> servers)
    {
        string? on = null;
        bool anyForced = false;
        foreach (ServerState s in servers)
        {
            if (s.Force == ServerForce.Auto) continue;
            anyForced = true;
            if (s.Force != ServerForce.On) continue;
            if (on != null) return null;
            on = s.Id;
        }
        if (!anyForced) return Auto;
        if (on == null) return null;
        // A pick forces every other server Off; one left on Auto is a hand-made mix.
        foreach (ServerState s in servers)
            if (!s.Id.Equals(on, StringComparison.OrdinalIgnoreCase) && s.Force == ServerForce.Auto) return null;
        return on;
    }

    /// <summary>
    /// The server this world counts as: the first active one forced On (an override beats
    /// detection), else the first active one (built-in servers come first). Null when none is.
    /// </summary>
    public static ServerState? ActiveServer(IReadOnlyList<ServerState> servers)
    {
        ServerState? first = null;
        foreach (ServerState s in servers)
        {
            if (!s.Active) continue;
            if (s.Force == ServerForce.On) return s;
            first ??= s;
        }
        return first;
    }

    /// <summary>
    /// The plugin that owns <paramref name="serverId"/>: the first manifest that declares it, else
    /// the plugin with the most contributions for it (RynthILT for the built-in "ilt"; ties go to
    /// the name that sorts first). "" when no plugin has anything for it. <paramref name="enginePlugin"/>
    /// (the engine's own manifest) never owns a server.
    /// </summary>
    public static string OwnerPlugin(IReadOnlyList<SectionManifest> manifests, string serverId, string enginePlugin)
    {
        foreach (SectionManifest m in manifests)
        {
            if (IsEngine(m, enginePlugin)) continue;
            foreach (SectionServerDecl d in m.Servers)
                if (d.Id.Equals(serverId, StringComparison.OrdinalIgnoreCase)) return m.Plugin;
        }

        string best = string.Empty;
        int bestCount = 0;
        foreach (SectionManifest m in manifests)
        {
            if (IsEngine(m, enginePlugin)) continue;
            int n = 0;
            foreach (SectionContribution c in m.Contributions)
                if (c.Server.Equals(serverId, StringComparison.OrdinalIgnoreCase)) n++;
            if (n > bestCount || (n == bestCount && n > 0 && string.Compare(m.Plugin, best, StringComparison.OrdinalIgnoreCase) < 0))
            {
                best = m.Plugin;
                bestCount = n;
            }
        }
        return best;
    }

    private static bool IsEngine(SectionManifest m, string enginePlugin)
        => m.Plugin.Length == 0 || m.Plugin.Equals(enginePlugin, StringComparison.OrdinalIgnoreCase);

    private static ServerState? Find(IReadOnlyList<ServerState> servers, string id)
    {
        foreach (ServerState s in servers)
            if (s.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) return s;
        return null;
    }
}
