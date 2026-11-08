// ============================================================================
//  RynthCore.Engine - UI/Sections/PluginWindows.cs
//  The windows plugins draw (their manifests' "windows", plugin API v89;
//  docs/PLUGIN_WINDOWS.md), hosted as ImGui panels: the engine owns the window
//  (header, pop-out, close, resize, remembered place, bar button), the plugin
//  draws the body through RynthPluginRenderEmbed (PluginWindowFace).
//
//  Sync runs after every SectionRegistry rebuild (AC's render thread): it
//  registers a dynamic panel for each window the manifests ask for (a window
//  with "server" only while that server is active) and unregisters the rest.
//  The panel key is the window's title, so its saved place, hotkeys and /rc
//  commands use the name players see. A title an engine panel has is refused,
//  unless the window declares "takeOver": then it takes that panel's place
//  (ImGuiPanelHost.TakeOver: same key, so the saved place and size, open state,
//  hotkeys and commands carry over) and the engine's face steps aside, not drawn
//  and without its bar button (TakesOver), until the window goes (the plugin
//  unloads or stops declaring it): then the engine's face comes back where the
//  window was, open if it was open. So a screen can move out of the engine with
//  the plugin shipping first and the engine face deleted a release later.
//  A window seen for the first time this session opens if it was open when the
//  game last closed (Remember Open Windows); one that went with a plugin reload
//  opens again when the plugin comes back. Both read the panel layout in use
//  when the window registers; when the layout changes later (UI/PanelLayout: a
//  hot reload that registered windows before the character's layout was in,
//  a character name that came late, a relogin) ImGuiPanelHost's layout switch
//  opens, closes and places the hosted windows from the new layout, like the
//  engine's panels (2026-11-05).
//
//  WindowPlan.cs is the pure part (which windows, which are refused and why,
//  which take over an engine panel).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using RynthCore.Engine.ImGuiBackend;
using RynthCore.Engine.ImGuiBackend.Panels;

namespace RynthCore.Engine.UI.Sections;

internal static class PluginWindows
{
    private sealed record Hosted(SectionWindowDecl Decl, string Signature, bool TakesOver);

    private static readonly Dictionary<string, Hosted> Live = new(StringComparer.OrdinalIgnoreCase);   // render thread
    private static readonly HashSet<string> SeenThisSession = new(StringComparer.OrdinalIgnoreCase);    // render thread
    private static readonly HashSet<string> ReopenWhenBack = new(StringComparer.OrdinalIgnoreCase);     // render thread
    private static readonly HashSet<string> RefusalsLogged = new(StringComparer.Ordinal);               // render thread
    private static volatile SectionWindowDecl[] _barWindows = Array.Empty<SectionWindowDecl>();
    private static volatile string[] _takenOver = Array.Empty<string>();
    private static int _version;

    /// <summary>Hosted windows that asked for a bar button, in manifest order. Any thread.</summary>
    public static SectionWindowDecl[] BarWindows => _barWindows;

    /// <summary>Bumped whenever the hosted set changes (the bar rebuilds its buttons). Any thread.</summary>
    public static int Version => System.Threading.Volatile.Read(ref _version);

    /// <summary>True when <paramref name="title"/> is a plugin window hosted now. Render thread.</summary>
    public static bool IsHosted(string title) => Live.ContainsKey(title);

    /// <summary>
    /// True while a plugin window has taken the place of the engine panel <paramref name="title"/>:
    /// the engine's own bar button for it is left out (the window has its own when it asks). Any thread.
    /// </summary>
    public static bool TakesOver(string title)
    {
        foreach (string t in _takenOver)
            if (t.Equals(title, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>After a SectionRegistry rebuild: register, update and drop the windows' panels. Render thread.</summary>
    public static void Sync(IReadOnlyList<SectionManifest> manifests, IReadOnlyList<ServerState> servers)
    {
        var refused = new List<string>();
        var takeOvers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, SectionWindowDecl> want = WindowPlan.Build(manifests, servers, Use, refused, takeOvers);
        foreach (string r in refused)
            if (RefusalsLogged.Add(r)) RynthLog.UI("PluginWindows: " + r + " - not shown.");

        bool changed = false;
        var gone = new List<string>();
        foreach (string title in Live.Keys)
            if (!want.ContainsKey(title)) gone.Add(title);
        foreach (string title in gone)
        {
            Hosted was = Live[title];
            if (was.TakesOver)
            {
                // The engine's face comes back in the window's place (open if it was open).
                ImGuiPanelHost.ReleaseTakeOver(title);
                RynthLog.UI($"PluginWindows: {was.Decl.Plugin}'s window '{title}' went - the engine's panel is back.");
            }
            else
            {
                if (ImGuiPanelHost.IsOpen(title)) ReopenWhenBack.Add(title);
                ImGuiPanelHost.Unregister(title);
            }
            Live.Remove(title);
            changed = true;
        }

        foreach ((string title, SectionWindowDecl w) in want)
        {
            string sig = WindowPlan.Signature(w);
            if (Live.TryGetValue(title, out Hosted? h) && h.Signature == sig) continue;
            SectionWindowDecl decl = w;
            Func<IImGuiPanel> face = () => new PluginWindowFace(decl.Plugin, decl.Surface, decl.Title, decl.FitHeight, decl.MinWidth, decl.DrawScale);
            if (takeOvers.Contains(title))
            {
                // An engine panel's place: its key, saved place and open state stay; nothing to reopen.
                if (!ImGuiPanelHost.TakeOver(title, title, Spec(decl), face)) continue;
                if (h == null) RynthLog.UI($"PluginWindows: {decl.Plugin}'s window '{title}' takes the engine panel's place.");
                Live[title] = new Hosted(decl, sig, TakesOver: true);
                changed = true;
                continue;
            }
            ImGuiPanelHost.RegisterDynamic(title, title, Spec(decl), face);
            bool first = h == null;
            Live[title] = new Hosted(decl, sig, TakesOver: false);
            changed = true;
            if (!first) continue;
            bool reopen = ReopenWhenBack.Remove(title);
            if (!reopen && SeenThisSession.Add(title) && Plugins.EngineSettings.RememberWindowLayout
                && PanelStateStore.TryGetPanel(title, out PanelStateStore.PanelEntry saved) && saved.Open)
                reopen = true;
            SeenThisSession.Add(title);
            if (reopen) ImGuiPanelHost.Open(title);
        }

        if (!changed) return;
        var bar = new List<SectionWindowDecl>();
        var taken = new List<string>();
        foreach (Hosted h in Live.Values)
        {
            if (h.Decl.Bar) bar.Add(h.Decl);
            if (h.TakesOver) taken.Add(h.Decl.Title);
        }
        _barWindows = bar.ToArray();
        _takenOver = taken.ToArray();
        System.Threading.Interlocked.Increment(ref _version);
    }

    /// <summary>
    /// What already uses <paramref name="title"/>: an engine panel (taken over by a window here or
    /// not), this sync's own hosted window (free), or another panel such as a script window.
    /// </summary>
    private static TitleUse Use(string title) =>
        ImGuiPanelHost.IsEnginePanel(title) ? TitleUse.EnginePanel
        : ImGuiPanelHost.HasFace(title) && !Live.ContainsKey(title) ? TitleUse.Other
        : TitleUse.Free;

    // A frameless window ("chrome": "none", v90) has no header, a transparent background and no
    // border: the plugin paints the whole window and drags it from its own title row. Its resize
    // grip sits over the body's bottom-right corner, like the engine's chromeless panels, and shows
    // only while the mouse is near it: that corner is the plugin's own content.
    private static PanelSpec Spec(SectionWindowDecl w) => w.Frameless
        ? new(new Vector2(w.Width, w.Height), new Vector2(w.MinWidth, w.MinHeight), PanelChrome.None,
            Background: 0, Rounding: 0f, EdgeToEdge: true, GripOnHover: true, OpenCorner: Corner(w.Corner), DrawScale: w.DrawScale,
            // A frameless window that sizes itself (fitHeight) needs no grip of the host's: its own edges resize it.
            NoResize: w.FitHeight && w.UnderUi, UnderUi: w.UnderUi)
        : new(new Vector2(w.Width, w.Height), new Vector2(w.MinWidth, w.MinHeight),
            EdgeToEdge: w.EdgeToEdge, GripInBody: w.EdgeToEdge, OpenCorner: Corner(w.Corner), DrawScale: w.DrawScale);

    private static PanelCorner Corner(string c) => c switch
    {
        "topLeft" => PanelCorner.TopLeft, "topRight" => PanelCorner.TopRight,
        "bottomLeft" => PanelCorner.BottomLeft, "bottomRight" => PanelCorner.BottomRight,
        "top" => PanelCorner.Top, "bottom" => PanelCorner.Bottom, _ => PanelCorner.None,
    };
}
