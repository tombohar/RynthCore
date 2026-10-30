// ============================================================================
//  RynthCore.Engine - ImGui/Panels/WorldMapFace.cs
//  The World Map: an interactive map of Dereth, opened from the RynthNav panel's Map button.
//  The map itself is WorldMapView (also the Mini Map's); this face is the toolbar around it:
//    Follow   keeps you centred (a drag, or Fit, turns it off; a double click on the map turns it on)
//    Rotate   turns the map so the way you face is up
//    - + Fit  zoom out / in / the whole world
//    Show     which kinds of place, and the labels / route / path / grid / characters / buildings layers
//    Find     a place by name (the Atlas's surface places, towns and dungeons first): Enter or a click
//             centres on it, zooms in and rings it
//  and, over the map's top-left, what is under the cursor: its coordinates, the place's name, and how
//  far and which way it is from you.
//  Settings (zoom, Follow, Rotate, the layers) last across sessions (WorldMapSettings).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.UI.Data;
using static RynthCore.Engine.ImGuiBackend.Panels.FaceKit;

namespace RynthCore.Engine.ImGuiBackend.Panels;

/// <summary>The data both map faces draw from: subscribed while a face is open.</summary>
internal static class WorldMapFeeds
{
    private static readonly string VectorPath = System.IO.Path.Combine(RynthCore.Install.RynthInstallPaths.CoreDir, "NavData", "worldmap.vec");

    public static void Subscribe()
    {
        UiSources.RynthNav.Subscribe();
        UiSources.RynthNav.RequestRefresh();
        UiSources.Radar.Subscribe();
        UiSources.Radar.RequestRefresh();
        RynthNavAtlas.EnsureLoadedForNames();
        WorldMapMaps.Refresh();                // finds which map pictures are installed and opens the one to use
        WorldMapVectors.Open(VectorPath);
    }

    public static void Unsubscribe()
    {
        UiSources.RynthNav.Unsubscribe();
        UiSources.Radar.Unsubscribe();
    }

    /// <summary>The characters feed (RynthNet) is polled only while the layer is on; each face keeps its own flag.</summary>
    public static void SyncCharacters(ref bool subscribed)
    {
        bool want = WorldMapSettings.Characters;
        if (want == subscribed) return;
        subscribed = want;
        if (want) { UiSources.Characters.Subscribe(); UiSources.Characters.RequestRefresh(); }
        else UiSources.Characters.Unsubscribe();
    }

    public static void ReleaseCharacters(ref bool subscribed)
    {
        if (subscribed) UiSources.Characters.Unsubscribe();
        subscribed = false;
    }
}

internal sealed class WorldMapFace : IImGuiPanel
{
    public const string Title = "World Map";

    public static void Register()
    {
        WorldMapSettings.Load();   // engine init thread: the file read stays off AC's thread
        ImGuiPanelHost.Register(Title,
            new PanelSpec(new Vector2(680, 600), new Vector2(340, 280), EdgeToEdge: true, GripInBody: true, NoPopOut: true, OpenCentered: true),
            () => new WorldMapFace());
        MiniMapFace.Register();
    }

    private readonly WorldMapView _view = new(mini: false);
    private bool _charsSub;
    private readonly byte[] _find = new byte[64];
    private string _findText = "";
    private List<NavPlace> _found = new();
    private bool _findOpen;
    private Vector2 _findBoxMin, _findBoxMax;

    public void OnShown() => WorldMapFeeds.Subscribe();

    public void OnHidden()
    {
        WorldMapFeeds.Unsubscribe();
        WorldMapFeeds.ReleaseCharacters(ref _charsSub);
        _findOpen = false;
    }

    public void Draw()
    {
        WorldMapFeeds.SyncCharacters(ref _charsSub);
        float w = Begin(out Vector2 origin, out Vector2 size);
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui11));
        try
        {
            RynthNavStatus? st = UiSources.RynthNav.Current?.Value;
            RadarView? radar = UiSources.Radar.Current?.Value;
            MapPlayer pl = WorldMapView.GatherPlayer(st, radar);
            CharacterRow[]? chars = WorldMapSettings.Characters ? UiSources.Characters.Current?.Value?.Rows : null;

            Vector2 barPos = ImGuiNET.ImGui.GetCursorScreenPos();
            float canvasTop = DrawToolbar(barPos, w, pl.Known);
            var canvasMin = new Vector2(origin.X + 1, canvasTop);
            var canvasMax = new Vector2(origin.X + size.X - 1, origin.Y + size.Y - 1);

            // The find dropdown covers part of the map: tell the map to ignore the mouse there.
            (Vector2, Vector2)? drop = null;
            if (_findOpen && _found.Count > 0)
            {
                var dmin = new Vector2(_findBoxMin.X, _findBoxMax.Y + 2);
                drop = (dmin, dmin + new Vector2(Math.Max(_findBoxMax.X - _findBoxMin.X, 250f), _found.Count * 20 + 4));
            }
            _view.Blocked = drop;
            _view.Draw(canvasMin, canvasMax, pl, st, chars);

            // What's under the cursor, over the map's top-left.
            string tip = _view.HoverText;
            if (tip.Length > 0)
            {
                Vector2 ts = ImGuiNET.ImGui.CalcTextSize(tip);
                Vector2 a = canvasMin + new Vector2(8, 8), b = a + ts + new Vector2(12, 8);
                if (b.X < canvasMax.X - 8)
                {
                    var dl = ImGuiNET.ImGui.GetWindowDrawList();
                    dl.AddRectFilled(a, b, RynthTheme.Argb(0xD00A121A), 4);
                    dl.AddText(a + new Vector2(6, 4), Text, tip);
                }
            }

            DrawFindDropdown(drop);
            WorldMapSettings.FlushIfDue();
        }
        finally
        {
            ImGuiNET.ImGui.PopFont();
            End(origin, size);
        }
    }

    // ── Toolbar ──────────────────────────────────────────────────────────
    private float DrawToolbar(Vector2 pos, float width, bool canFollow)
    {
        float x = pos.X, y = pos.Y;
        const float h = 22f;
        bool follow = _view.Follow, rotate = _view.Rotate;
        if (Button("##wm_follow", "Follow", new Vector2(x, y), new Vector2(54, h), follow ? Teal : Text, follow ? Selected : BtnFill,
                enabled: canFollow, border: follow ? Teal : 0))
            _view.Follow = !follow;
        x += 58;
        if (Button("##wm_rotate", "Rotate", new Vector2(x, y), new Vector2(52, h), rotate ? Teal : Text, rotate ? Selected : BtnFill,
                enabled: canFollow, border: rotate ? Teal : 0))
            _view.Rotate = !rotate;
        x += 56;
        if (Button("##wm_out", "-", new Vector2(x, y), new Vector2(24, h), Text, BtnFill)) _view.ZoomBy(1f / 1.5f);
        x += 28;
        if (Button("##wm_in", "+", new Vector2(x, y), new Vector2(24, h), Text, BtnFill)) _view.ZoomBy(1.5f);
        x += 28;
        if (Button("##wm_fit", "Fit", new Vector2(x, y), new Vector2(34, h), Text, BtnFill)) _view.FitWorld();
        x += 38;
        if (Button("##wm_show", "Show", new Vector2(x, y), new Vector2(46, h), Text, BtnFill)) ImGuiNET.ImGui.OpenPopup("##wm_show_pop");
        var showMenuAt = new Vector2(x, y + h + 2);
        x += 50;

        ImGuiNET.ImGui.SetNextWindowPos(showMenuAt);
        if (ImGuiNET.ImGui.BeginPopup("##wm_show_pop"))
        {
            // Which picture: the best installed, or one of the installed ones.
            var installed = WorldMapMaps.Installed;
            if (installed.Length > 0)
            {
                SafeImGui.TextDisabled("Map detail" + (WorldMapMaps.CurrentLabel.Length > 0 ? ": " + WorldMapMaps.CurrentLabel : ""));
                if (ImGuiNET.ImGui.RadioButton("Best installed", WorldMapSettings.Detail.Length == 0)) { WorldMapSettings.Detail = ""; WorldMapSettings.MarkDirty(); WorldMapMaps.Apply(); }
                foreach (var t in installed)
                    if (ImGuiNET.ImGui.RadioButton(t.Label, WorldMapSettings.Detail == t.Key)) { WorldMapSettings.Detail = t.Key; WorldMapSettings.MarkDirty(); WorldMapMaps.Apply(); }
                ImGuiNET.ImGui.Separator();
            }
            for (int k = 0; k < (int)WorldMapView.Kind.Count; k++)
                if (ImGuiNET.ImGui.Checkbox(WorldMapView.KindNames[k], ref WorldMapSettings.Show[k])) WorldMapSettings.MarkDirty();
            ImGuiNET.ImGui.Separator();
            if (ImGuiNET.ImGui.Checkbox("Labels", ref WorldMapSettings.Labels)) WorldMapSettings.MarkDirty();
            if (ImGuiNET.ImGui.Checkbox("Route", ref WorldMapSettings.Route)) WorldMapSettings.MarkDirty();
            if (ImGuiNET.ImGui.Checkbox("Walking path", ref WorldMapSettings.WalkPath)) WorldMapSettings.MarkDirty();
            if (ImGuiNET.ImGui.Checkbox("Grid", ref WorldMapSettings.Grid)) WorldMapSettings.MarkDirty();
            if (ImGuiNET.ImGui.Checkbox("Other characters", ref WorldMapSettings.Characters)) WorldMapSettings.MarkDirty();
            if (ImGuiNET.ImGui.Checkbox("Buildings and roads", ref WorldMapSettings.Buildings)) WorldMapSettings.MarkDirty();
            ImGuiNET.ImGui.EndPopup();
        }

        // Find: the rest of the row, at most 220 px.
        float room = pos.X + width - x;
        if (room >= 90)
        {
            float fw = Math.Min(room, 220f);
            _findBoxMin = new Vector2(x, y);
            _findBoxMax = new Vector2(x + fw, y + h);
            bool enter = TextBox("##wm_findbox", _find, _findBoxMin, fw, "Find a place", out bool active, ImGuiInputTextFlags.EnterReturnsTrue);
            string text = Utf8(_find).Trim();
            if (text != _findText)
            {
                _findText = text;
                _found = WorldMapView.Find(text, 8);
                _findOpen = text.Length > 0;
            }
            else if (active && text.Length > 0) _findOpen = true;
            if (active && ImGuiNET.ImGui.IsKeyPressed(ImGuiKey.Escape, false)) ClearFind();
            if (enter && _found.Count > 0) PickFound(_found[0]);
            else if (enter) ClearFind();
        }
        else _findBoxMin = _findBoxMax = new Vector2(x, y);

        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(pos.X, y + 28));
        return y + 28;
    }

    private void ClearFind()
    {
        Array.Clear(_find);
        _findText = "";
        _found = new List<NavPlace>();
        _findOpen = false;
    }

    private void PickFound(NavPlace p)
    {
        float zoom = p.Type switch { "Town" => 3.5f, "Dungeon" => 5f, _ => 7f };
        _view.Show(p.Name, p.Ns, p.Ew, zoom);
        ClearFind();
    }

    /// <summary>The results under the find box. Drawn after the map, so its buttons win the mouse.</summary>
    private void DrawFindDropdown((Vector2 Min, Vector2 Max)? drop)
    {
        if (drop is not { } d || !_findOpen || _found.Count == 0) return;
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        Vector2 mouse = ImGuiNET.ImGui.GetIO().MousePos;
        bool inBox = mouse.X >= _findBoxMin.X && mouse.X <= _findBoxMax.X && mouse.Y >= _findBoxMin.Y && mouse.Y <= _findBoxMax.Y;
        bool inDrop = mouse.X >= d.Min.X && mouse.X <= d.Max.X && mouse.Y >= d.Min.Y && mouse.Y <= d.Max.Y;
        if (ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !inBox && !inDrop) { _findOpen = false; return; }

        dl.AddRectFilled(d.Min, d.Max, ShellBg, 4);
        dl.AddRect(d.Min, d.Max, Teal, 4);
        NavPlace? pick = null;
        for (int i = 0; i < _found.Count; i++)
        {
            NavPlace p = _found[i];
            var rowMin = new Vector2(d.Min.X + 2, d.Min.Y + 2 + i * 20);
            var rowSize = new Vector2(d.Max.X - d.Min.X - 4, 20);
            ImGuiNET.ImGui.SetCursorScreenPos(rowMin);
            bool clicked = ImGuiNET.ImGui.InvisibleButton("##wm_found" + i, rowSize);
            if (ImGuiNET.ImGui.IsItemHovered()) dl.AddRectFilled(rowMin, rowMin + rowSize, Selected, 3);
            dl.AddText(rowMin + new Vector2(6, 3), Text, p.Name);
            string t = p.Type;
            Vector2 ts = ImGuiNET.ImGui.CalcTextSize(t);
            dl.AddText(rowMin + new Vector2(rowSize.X - ts.X - 6, 3), Mute, t);
            if (clicked) pick = p;
        }
        if (pick != null) PickFound(pick);
    }
}
