// ============================================================================
//  RynthCore.Engine - UI/Sections/WindowPlan.cs
//  Which plugin windows to host (PluginWindows.cs), from the manifests and the
//  servers: pure, so tools/SectionsTests compiles it as is.
// ============================================================================

using System;
using System.Collections.Generic;

namespace RynthCore.Engine.UI.Sections;

/// <summary>What already uses a window's title (its panel key).</summary>
internal enum TitleUse
{
    /// <summary>Nothing, or this window's own panel (hosted already).</summary>
    Free,
    /// <summary>An engine panel: a window that declares "takeOver" draws in its place; any other is refused.</summary>
    EnginePanel,
    /// <summary>Another panel that isn't the engine's (a script window): refused.</summary>
    Other,
}

/// <summary>Which plugin windows to host, from the manifests and the servers. Pure.</summary>
internal static class WindowPlan
{
    /// <summary>
    /// The windows to host, keyed by title, and the ones left out with the reason. A server
    /// window counts only while its server is active; the first plugin to claim a title keeps it.
    /// A title an engine panel has (<paramref name="titleUse"/>) is hosted only when the window
    /// declares "takeOver": it then goes in <paramref name="takeOvers"/>, and the engine's face
    /// for that title steps aside while the window is hosted. Other used titles are refused.
    /// </summary>
    public static Dictionary<string, SectionWindowDecl> Build(IReadOnlyList<SectionManifest> manifests,
        IReadOnlyList<ServerState> servers, Func<string, TitleUse> titleUse, List<string> refused, ISet<string> takeOvers)
    {
        var want = new Dictionary<string, SectionWindowDecl>(StringComparer.OrdinalIgnoreCase);
        foreach (SectionManifest m in manifests)
            foreach (SectionWindowDecl w in m.Windows)
            {
                if (!w.IsUniversal && !ServerActive(servers, w.Server)) continue;
                TitleUse use = titleUse(w.Title);
                if (use == TitleUse.Other) { refused.Add($"{w.Plugin}'s window '{w.Title}': another panel has that title"); continue; }
                if (use == TitleUse.EnginePanel && !w.TakeOver)
                {
                    refused.Add($"{w.Plugin}'s window '{w.Title}': an engine panel has that title (\"takeOver\": true takes its place)");
                    continue;
                }
                if (want.TryGetValue(w.Title, out SectionWindowDecl? first))
                {
                    refused.Add($"{w.Plugin}'s window '{w.Title}': {first.Plugin} has a window with that title");
                    continue;
                }
                want[w.Title] = w;
                if (use == TitleUse.EnginePanel) takeOvers.Add(w.Title);
            }
        return want;
    }

    private static bool ServerActive(IReadOnlyList<ServerState> servers, string id)
    {
        foreach (ServerState s in servers)
            if (s.Active && s.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>What a hosted window's panel depends on: a change re-registers it.</summary>
    public static string Signature(SectionWindowDecl w) =>
        string.Join("|", w.Plugin, w.Surface, w.Icon, w.Width, w.Height, w.MinWidth, w.MinHeight, w.Bar, w.EdgeToEdge, w.TakeOver,
            w.Frameless, w.FitHeight);
}
