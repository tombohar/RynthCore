// ============================================================================
//  RynthCore.Engine - ImGui/Panels/MiniRemoteFace.Sections.cs
//  The Mini Remote's sections (UI/Sections): the rail down the left, and the
//  body of the section picked on it.
//
//  rail     Remote (the slots / RynthAi's remote), then the core sections and
//           the plugins' own, in SectionRegistry's order. Right-click: the
//           menu (sections, active servers' items, hide the rail). Hidden, the
//           sections stay reachable from the menu and /rc miniremote section.
//  body     a row of tabs when there is more than one view: the section's views
//           (engine panels embedded whole, the Session Stats overview, plugin views),
//           then one tab per active server with options here ("Server: ILT")
//           and "Plugins" for options every world gets. A view fills the body;
//           an options tab lists the plugins' option surfaces (collapsible) and
//           command buttons.
//  fit      picking a section sizes the window for its first view; a resize by
//           hand holds until the next pick. Back on Remote, the remote refits.
//  saved    the core section last picked, and whether the rail shows
//           (panel_columns.txt, for the whole PC).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Threading;
using ImGuiNET;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.Plugins;
using RynthCore.Engine.UI;
using RynthCore.Engine.UI.Sections;
using static RynthCore.Engine.ImGuiBackend.Panels.FaceKit;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed partial class MiniRemoteFace
{
    private const string SectionKey = "MiniRemote.section", RailKey = "MiniRemote.rail";
    private const float RailMinW = 52f, RailMaxW = 96f, RailRowH = 18f, TabRowH = 20f;
    /// <summary>The host key that keeps this window's embedded panels apart from other hosts'.</summary>
    private const string EmbedHost = "MiniRemote";
    /// <summary>Preferred body size (logical) for a view that doesn't say.</summary>
    private static readonly Vector2 DefaultViewSize = new(420, 380);
    private static readonly Vector2 OverviewSize = new(360, 200);

    // Shared by every instance (the face is recreated on each open): the section shown.
    private static string _section = LoadSection();
    private static string? _requestedSection, _requestedSurface;   // from commands, taken on the next draw
    private static readonly Dictionary<string, int> TabBySection = new(StringComparer.OrdinalIgnoreCase);

    // This instance: the window fit and the plugin surfaces.
    private bool _fitPending = true;
    private Vector2 _remoteWindowSize;
    private readonly Dictionary<string, byte[]> _surfaceBytes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> _surfaceHeights = new(StringComparer.Ordinal);
    private readonly HashSet<string> _surfaceOff = new(StringComparer.Ordinal);
    private float _railW;
    private int _railRevision = -1;

    private static bool IsRemote => _section.Equals(SectionCatalog.RemoteId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the window is on section <paramref name="sectionId"/> (the dashboard's lit Char launcher).</summary>
    public static bool ShowsSection(string sectionId) => _section.Equals(sectionId, StringComparison.OrdinalIgnoreCase);

    private static bool RailShown => PanelColumnStore.Get(RailKey, 1f) >= 0.5f;

    // ── Requests (commands; render thread) ──────────────────────────────

    /// <summary>Shows section <paramref name="sectionId"/> ("remote" = the remote) on the next draw.</summary>
    public static void RequestSection(string sectionId)
    {
        Volatile.Write(ref _requestedSurface, null);
        Volatile.Write(ref _requestedSection, sectionId);
    }

    /// <summary>
    /// Shows the section and tab holding plugin surface <paramref name="surface"/> (RynthAi's
    /// "section:banking"), when one does now. False when no section has it.
    /// </summary>
    public static bool TryRequestSurface(string surface)
    {
        foreach (SectionView v in SectionRegistry.Current.Sections)
            if (TabOfSurface(v, surface) >= 0)
            {
                Volatile.Write(ref _requestedSurface, surface);
                Volatile.Write(ref _requestedSection, v.Id);
                return true;
            }
        return false;
    }

    private void TakeRequestedSection(SectionRegistry.Snapshot snap)
    {
        string? id = Interlocked.Exchange(ref _requestedSection, null);
        if (id == null) return;
        string? surface = Interlocked.Exchange(ref _requestedSurface, null);
        SelectSection(id);
        SectionView? v = snap.Find(_section);
        if (v != null && surface != null)
        {
            int tab = TabOfSurface(v, surface);
            if (tab >= 0) TabBySection[v.Id] = tab;
        }
    }

    /// <summary>The tab index (views, then option groups) holding <paramref name="surface"/>, else -1.</summary>
    private static int TabOfSurface(SectionView v, string surface)
    {
        for (int i = 0; i < v.Panes.Length; i++)
            if (v.Panes[i].Contribution?.Surface.Equals(surface, StringComparison.OrdinalIgnoreCase) == true) return i;
        for (int g = 0; g < v.Groups.Length; g++)
            foreach (SectionContribution c in v.Groups[g].Items)
                if (c.Surface.Equals(surface, StringComparison.OrdinalIgnoreCase)) return v.Panes.Length + g;
        return -1;
    }

    private void SelectSection(string id)
    {
        string next = id.Length == 0 ? SectionCatalog.RemoteId : id;
        if (next.Equals(_section, StringComparison.OrdinalIgnoreCase)) return;
        bool leavingRemote = IsRemote;
        if (leavingRemote && !ImGuiPopOuts.InPopOutFrame)
            _remoteWindowSize = ImGuiNET.ImGui.GetWindowSize() / ImGuiPanelHost.PlacementScale();
        _section = next;
        _fitPending = true;
        // Non-Remote sections need the left rail (or they draw as an orphan body with no
        // hub tab bar). Rail-off is only for Remote + miniremote-window (Gear/Games/…).
        if (!IsRemote && !RailShown)
            PanelColumnStore.Set(RailKey, 1f);
        if (IsRemote)
        {
            // Back on the remote: RynthAi's asks again; the engine grid gets the size it had.
            _lastWant = Vector2.Zero;
            _requested = Vector2.Zero;
            if (_remoteWindowSize.X > 0 && !_hosted && !ImGuiPopOuts.InPopOutFrame)
                ImGuiPanelHost.RequestSize(Title, _remoteWindowSize);
        }
        SaveSection(_section);
    }

    private static string LoadSection()
    {
        int i = (int)PanelColumnStore.Get(SectionKey, -1f);
        return i >= 0 && i < SectionCatalog.Core.Length ? SectionCatalog.Core[i].Id : SectionCatalog.RemoteId;
    }

    /// <summary>Saves a core section (a plugin's own isn't: it may not be there next time).</summary>
    private static void SaveSection(string id)
    {
        int index = -1;
        for (int i = 0; i < SectionCatalog.Core.Length; i++)
            if (SectionCatalog.Core[i].Id.Equals(id, StringComparison.OrdinalIgnoreCase)) index = i;
        if (PanelColumnStore.Get(SectionKey, -1f) != index) PanelColumnStore.Set(SectionKey, index);
    }

    // ── Rail ────────────────────────────────────────────────────────────

    /// <summary>Draws the rail at the body's left; returns the width it takes (with its gap).</summary>
    private float DrawRail(SectionRegistry.Snapshot snap, Vector2 origin, Vector2 size)
    {
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui9));
        try
        {
            if (_railRevision != snap.Revision || _railW <= 0)
            {
                float widest = ImGuiNET.ImGui.CalcTextSize("Remote").X;
                foreach (SectionView v in snap.Sections) widest = MathF.Max(widest, ImGuiNET.ImGui.CalcTextSize(v.Label).X);
                _railW = Math.Clamp(widest + 14f, RailMinW, RailMaxW);
                _railRevision = snap.Revision;
            }
            float h = Math.Max(RailRowH, size.Y - 12 - GripOverlap());
            ImGuiNET.ImGui.SetCursorScreenPos(origin + new Vector2(6, 6));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            bool open = ImGuiNET.ImGui.BeginChild("##mr_rail", new Vector2(_railW, h), ImGuiChildFlags.None,
                ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar);
            ImGuiNET.ImGui.PopStyleVar();
            try
            {
                if (open)
                {
                    Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
                    RailButton(SectionCatalog.RemoteId, "Remote", "The remote: item slots, and RynthAi's when it is loaded.", true, ref p);
                    var dl = ImGuiNET.ImGui.GetWindowDrawList();
                    dl.AddLine(p + new Vector2(2, 1), p + new Vector2(_railW - 2, 1), BtnBord);
                    p.Y += 4;
                    foreach (SectionView v in snap.Sections)
                        RailButton(v.Id, v.Label, RailTooltip(v), v.HasContent, ref p);
                    ImGuiNET.ImGui.SetCursorScreenPos(p);
                    ImGuiNET.ImGui.Dummy(new Vector2(_railW, 1));
                    if (ImGuiNET.ImGui.IsWindowHovered() && ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                    {
                        _menuSlot = -1;
                        _openMenu = true;
                    }
                }
            }
            finally
            {
                ImGuiNET.ImGui.EndChild();
            }
        }
        finally
        {
            ImGuiNET.ImGui.PopFont();
        }
        return _railW + 4f;
    }

    private void RailButton(string id, string label, string tooltip, bool hasContent, ref Vector2 p)
    {
        bool selected = id.Equals(_section, StringComparison.OrdinalIgnoreCase);
        var size = new Vector2(_railW, RailRowH);
        if (Button("##rail_" + id, label, p, size, hasContent ? Text : Faded(Text), selected ? Selected : BtnFill,
                leftAlign: true, border: selected ? Teal : 0))
            SelectSection(id);
        if (ImGuiNET.ImGui.IsItemClicked(ImGuiMouseButton.Right)) { _menuSlot = -1; _openMenu = true; }
        if (tooltip.Length > 0) SafeImGui.SetItemTooltip(tooltip);
        p.Y += RailRowH + 2;
    }

    private static string RailTooltip(SectionView v)
    {
        if (!v.HasContent) return v.Label + ": " + EmptyNote(v.Id);
        var parts = new List<string>();
        foreach (SectionPane pane in v.Panes) parts.Add(pane.Title);
        foreach (SectionGroup g in v.Groups) parts.Add(g.Title);
        return v.Label + ": " + string.Join(", ", parts);
    }

    /// <summary>What an empty section says (nothing installed fills it on this world).</summary>
    private static string EmptyNote(string id) => id switch
    {
        "friends" => "no friends list yet. A server plugin can add one here.",
        "fellowship" => "the fellowship view comes from RynthAi; it isn't loaded.",
        "pets" or "crafting" or "spells" => "this view comes from RynthAi; it isn't loaded.",
        "map" or "combat" or "nav" or "chat" or "skills" or "inventory" => "its engine panels are off (Settings, or the plugins they need aren't installed).",
        _ => "nothing installed adds to it on this world.",
    };

    // ── Section body ────────────────────────────────────────────────────

    private void DrawSection(SectionRegistry.Snapshot snap, float w, Vector2 origin, Vector2 size)
    {
        SectionView? view = snap.Find(_section);
        if (view == null)
        {
            // A plugin's section that went away (its server isn't active any more): back to the remote.
            if (snap.Sections.Count > 0) SelectSection(SectionCatalog.RemoteId);
            Label("Loading sections...", Mute);
            return;
        }

        int tabs = view.Panes.Length + view.Groups.Length;
        if (tabs == 0)
        {
            Label(view.Label, Teal);
            ImGuiNET.ImGui.PushTextWrapPos(ImGuiNET.ImGui.GetCursorPosX() + w);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Mute);
            SafeImGui.TextWrapped(char.ToUpperInvariant(EmptyNote(view.Id)[0]) + EmptyNote(view.Id)[1..]);
            ImGuiNET.ImGui.PopStyleColor();
            ImGuiNET.ImGui.PopTextWrapPos();
            return;
        }

        if (!TabBySection.TryGetValue(view.Id, out int tab) || tab < 0 || tab >= tabs) tab = 0;
        float haveH = Remaining(origin, size);
        if (tabs > 1) tab = DrawTabRow(view, tab, w);
        TabBySection[view.Id] = tab;

        if (_fitPending)
        {
            _fitPending = false;
            Vector2 pref = PreferredSize(view, tab);
            float place = ImGuiPanelHost.PlacementScale();
            float extra = (tabs > 1 ? TabRowH + 4 : 0) + GripOverlap();
            FitWindow(new Vector2(pref.X * place, pref.Y * place + extra), w, haveH);
        }

        var box = new Vector2(w, Math.Max(40f, Remaining(origin, size) - GripOverlap()));
        if (tab < view.Panes.Length) DrawPane(snap, view.Panes[tab], box);
        else DrawGroup(view.Groups[tab - view.Panes.Length], box);
    }

    /// <summary>The section's tabs as a wrapped row of buttons; returns the one selected.</summary>
    private int DrawTabRow(SectionView view, int tab, float w)
    {
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui9));
        try
        {
            Vector2 start = ImGuiNET.ImGui.GetCursorScreenPos();
            Vector2 p = start;
            int count = view.Panes.Length + view.Groups.Length;
            for (int i = 0; i < count; i++)
            {
                string label = i < view.Panes.Length ? view.Panes[i].Title : view.Groups[i - view.Panes.Length].Title;
                float bw = Math.Min(w, ButtonWidth(label));
                if (p.X > start.X && p.X + bw > start.X + w) p = new Vector2(start.X, p.Y + TabRowH + 2);
                bool selected = i == tab;
                bool enabled = i >= view.Panes.Length || view.Panes[i].Contribution?.Enabled != false;
                if (Button("##sectab_" + view.Id + "_" + i, label, p, new Vector2(bw, TabRowH), enabled ? Text : Faded(Text),
                        selected ? Selected : BtnFill, border: selected ? Teal : 0) && i != tab)
                {
                    tab = i;
                    _fitPending = true;
                }
                p.X += bw + 3;
            }
            NextLine(start, p.Y - start.Y + TabRowH + 4);
        }
        finally
        {
            ImGuiNET.ImGui.PopFont();
        }
        return tab;
    }

    /// <summary>The body size (logical) a tab would like: its panel's default, or what its plugin said.</summary>
    private Vector2 PreferredSize(SectionView view, int tab)
    {
        if (tab >= view.Panes.Length) return DefaultViewSize;
        SectionPane pane = view.Panes[tab];
        switch (pane.Kind)
        {
            case SectionPaneKind.Overview:
                return OverviewSize;
            case SectionPaneKind.CorePanel:
                return ImGuiPanelHost.TryGetDefaultSize(pane.Panel, out Vector2 s) && s.X > 0 && s.Y > 0 ? s : DefaultViewSize;
            default:
                SectionContribution c = pane.Contribution!;
                return new Vector2(c.Width > 0 ? c.Width : DefaultViewSize.X, c.Height > 0 ? c.Height : DefaultViewSize.Y);
        }
    }

    private void DrawPane(SectionRegistry.Snapshot snap, SectionPane pane, Vector2 box)
    {
        switch (pane.Kind)
        {
            case SectionPaneKind.Overview:
                DrawOverview(snap, box);
                break;
            case SectionPaneKind.CorePanel:
                ImGuiPanelHost.DrawEmbedded(EmbedHost, pane.Panel, box);
                break;
            default:
                SectionContribution c = pane.Contribution!;
                if (!c.Enabled) { DrawDisabledNote(c); break; }
                DrawSurface(c, box);
                break;
        }
    }

    /// <summary>An options tab: a server's (or every world's) option surfaces and command buttons.</summary>
    private void DrawGroup(SectionGroup group, Vector2 box)
    {
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(2, 2));
        bool open = ImGuiNET.ImGui.BeginChild("##mr_group_" + group.Title, box, ImGuiChildFlags.AlwaysUseWindowPadding,
            ImGuiWindowFlags.NoBackground);
        ImGuiNET.ImGui.PopStyleVar();
        try
        {
            if (!open) return;
            if (group.Server is ServerState s)
                Label(s.Force == ServerForce.Auto ? $"{s.Name}: detected ({s.Reason})." : $"{s.Name}: forced on for this character.",
                    Faded(Mute));
            float w = ImGuiNET.ImGui.GetContentRegionAvail().X;
            foreach (SectionContribution c in group.Items)
            {
                ImGuiNET.ImGui.PushID(c.Plugin + "|" + c.Label + "|" + c.Surface + c.Command);
                try
                {
                    if (c.IsSurface) DrawOptionSurface(c, w);
                    else DrawCommandButton(c, w);
                }
                finally
                {
                    ImGuiNET.ImGui.PopID();
                }
            }
        }
        finally
        {
            ImGuiNET.ImGui.EndChild();
        }
    }

    private void DrawOptionSurface(SectionContribution c, float w)
    {
        if (!ImGuiNET.ImGui.CollapsingHeader(c.Label, ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (c.Tooltip.Length > 0) SafeImGui.SetItemTooltip(c.Tooltip);
            return;
        }
        if (c.Tooltip.Length > 0) SafeImGui.SetItemTooltip(c.Tooltip);
        if (!c.Enabled) { DrawDisabledNote(c); return; }
        string key = c.Plugin + "|" + c.Surface;
        if (!_surfaceHeights.TryGetValue(key, out float h)) h = c.Height > 0 ? c.Height : 220f;
        Vector2 want = DrawSurface(c, new Vector2(w, h));
        if (want.Y > 0 && Math.Abs(want.Y - h) > 1f) _surfaceHeights[key] = Math.Min(want.Y, SectionManifestParser.MaxSurfaceSize);
    }

    private static void DrawCommandButton(SectionContribution c, float w)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        float bw = Math.Min(w, Math.Max(120f, ButtonWidth(c.Label)));
        if (Button("##cmd", c.Label, p, new Vector2(bw, 22), c.Enabled ? Text : Faded(Text), BtnFill, c.Enabled))
            ServerMenus.Run(c.Command);
        string tip = c.Enabled || c.Note.Length == 0 ? c.Tooltip : c.Note;
        SafeImGui.SetItemTooltip(tip.Length > 0 ? tip + "\n" + c.Command : c.Command);
        NextLine(p, 26);
        if (!c.Enabled && c.Note.Length > 0) DrawDisabledNote(c);
    }

    private static void DrawDisabledNote(SectionContribution c)
    {
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Faded(Mute));
        ImGuiNET.ImGui.PushTextWrapPos(0f);
        SafeImGui.TextWrapped(c.Note.Length > 0 ? c.Note : c.Label + " isn't available right now.");
        ImGuiNET.ImGui.PopTextWrapPos();
        ImGuiNET.ImGui.PopStyleColor();
    }

    /// <summary>
    /// A plugin surface in a child of <paramref name="box"/>; returns the size the plugin asked
    /// for (zero when it didn't say). A surface whose export threw stays off for the session.
    /// </summary>
    private Vector2 DrawSurface(SectionContribution c, Vector2 box)
    {
        string key = c.Plugin + "|" + c.Surface;
        if (_surfaceOff.Contains(key))
        {
            Label($"{c.Label}: {c.Plugin} stopped drawing it (see the log).", Amber);
            return Vector2.Zero;
        }
        if (!_surfaceBytes.TryGetValue(c.Surface, out byte[]? bytes))
            _surfaceBytes[c.Surface] = bytes = Encoding.UTF8.GetBytes(c.Surface + "\0");

        int result = PluginManager.EmbedNotReady;
        Vector2 want = Vector2.Zero;
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        bool open = ImGuiNET.ImGui.BeginChild("##surf_" + key, box, ImGuiChildFlags.None, ImGuiWindowFlags.NoBackground);
        ImGuiNET.ImGui.PopStyleVar();
        try
        {
            if (open)
            {
                // Plugin widgets use the default UI font, as in their own windows.
                ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui11));
                try { result = PluginManager.RenderEmbed(c.Plugin, bytes, box, out want); }
                finally { ImGuiNET.ImGui.PopFont(); }
                if (result == PluginManager.EmbedNotReady) ImGuiNET.ImGui.TextDisabled("Loading...");
                else if (result == PluginManager.EmbedNoPlugin)
                    SafeImGui.TextDisabled($"{c.Plugin} isn't loaded, or is too old for this view.");
            }
        }
        finally
        {
            ImGuiNET.ImGui.EndChild();
        }
        if (result == PluginManager.EmbedFailed) _surfaceOff.Add(key);
        return result == PluginManager.EmbedDrawn ? want : Vector2.Zero;
    }

    // ── Session Stats overview ──────────────────────────────────────────

    /// <summary>
    /// The Session Stats section's stand-in, shown only while no plugin fills the section (RynthAi
    /// does, with its session XP, luminance, kills and rates). The engine keeps no session totals
    /// of its own here: it names the character and says where the numbers come from.
    /// Character details are RynthOracle's Character tab; server picks are Options > Diagnostics.
    /// </summary>
    private void DrawOverview(SectionRegistry.Snapshot snap, Vector2 box)
    {
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(2, 2));
        bool open = ImGuiNET.ImGui.BeginChild("##mr_overview", box, ImGuiChildFlags.AlwaysUseWindowPadding, ImGuiWindowFlags.NoBackground);
        ImGuiNET.ImGui.PopStyleVar();
        try
        {
            if (!open) return;
            string who = SessionStateRegistry.LastCharacterName;
            Label(who.Length > 0 ? who : "No character logged in", Teal);
            float w = ImGuiNET.ImGui.GetContentRegionAvail().X;
            Separator(w);
            ImGuiNET.ImGui.PushTextWrapPos(0f);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Faded(Mute));
            SafeImGui.TextWrapped("This session's XP, luminance, kills and their per-hour rates come with RynthAi's "
                + "Session Stats; RynthAi isn't loaded (or is too old to show them here). "
                + "Character details: RynthOracle's Character tab (/ro character). Server picks: Options > Diagnostics.");
            ImGuiNET.ImGui.PopStyleColor();
            ImGuiNET.ImGui.PopTextWrapPos();
        }
        finally
        {
            ImGuiNET.ImGui.EndChild();
        }
    }

    // ── Menu ────────────────────────────────────────────────────────────

    /// <summary>The menu's section half: every section, the active servers' items, the rail switch.</summary>
    private void DrawSectionsMenuItems()
    {
        SectionRegistry.Snapshot snap = SectionRegistry.Current;
        if (ImGuiNET.ImGui.MenuItem("Remote##sec_remote", "", IsRemote)) SelectSection(SectionCatalog.RemoteId);
        foreach (SectionView v in snap.Sections)
            if (ImGuiNET.ImGui.MenuItem(v.Label + "##sec_" + v.Id, "", v.Id.Equals(_section, StringComparison.OrdinalIgnoreCase)))
                SelectSection(v.Id);
        ServerMenus.DrawImGuiMenu(separatorFirst: true);
        ImGuiNET.ImGui.Separator();
        bool rail = RailShown;
        if (ImGuiNET.ImGui.MenuItem("Show the sections rail", "", rail))
        {
            bool hide = rail;
            PanelColumnStore.Set(RailKey, hide ? 0f : 1f);
            // Hiding the rail only restores the Remote tab bar when we are on Remote
            // (Surface → miniremote-window). Off-Remote + rail off left Crafting/etc.
            // as a lone body in docked and undocked windows (2026-10-16).
            if (hide && !IsRemote)
                SelectSection(SectionCatalog.RemoteId);
        }
        SafeImGui.SetItemTooltip("The column of sections down the left. Hidden, the window returns to Remote so "
            + "RynthAi's tab bar shows again (Pets, Crafting, Gear, Games, Vaulted), and a section is picked "
            + "here or with /rc miniremote section <name> (that turns the rail back on).");
    }
}
