// ============================================================================
//  RynthCore.Engine - ImGui/Panels/DungeonMapFace.cs
//  The Dungeon Map: a large, pannable map of the current dungeon. It replaces
//  RynthAi's plugin-drawn DungeonMapUi window (plugin ImGui rendering is no
//  longer called; RYNTHCORE_FORCE_IMGUI only) and is opened from the RynthAi
//  dashboard's Map launcher.
//
//  Data: UiSources.Radar, the same snapshot the Radar face draws
//  (RynthPluginGetRadarSnapshot): every floor's walls and fill strips, the
//  visited cells, portal / door / creature markers with labels, the player pose.
//  The plugin's D3D9 floor textures are not used; floors are strips and walls
//  are lines, drawn without anti-aliasing and culled to the canvas.
//
//  Like the plugin window it replaces:
//    - toolbar: 1U1D (current floor +-1), F1..Fn floor toggles, All, Here, zoom,
//      Follow, Rotate, Reset, Doors, Creatures, opacity; it can be hidden;
//    - markers: the radar's kinds (RadarKind); the kinds added 2026-10-04 use the
//      radar's shapes and colours (RadarMarkerStyle) and its per-kind show boxes;
//    - drag pans (and turns Follow off), the wheel zooms;
//    - portals always shown with their destination; doors and creatures optional;
//    - hidden while you're outdoors or in portal space, back in the next dungeon
//      (the panel stays open; ImGuiPanelHost skips drawing it docked).
//  Every marker has a size, an optional name label and a name size of its own
//  (DungeonMapSettingsStore.Markers, edited in the toolbar's Markers window; the
//  radar keeps its own set).
//  Settings are the engine's (dungeon_map_settings.txt beside radar_settings.txt),
//  not RynthAi's per-character Map* fields. Zoom, pan and floor picks last for the
//  session, as they did.
//
//  RynthAi's nav route (2026-10-12; UiSources.Nav, subscribed only while the map is
//  open and its Route box is on): legs between the points (closed for Circular,
//  dashed after a Recall / Portal step, which jumps), points as dots and the other
//  step types as icon badges, the next waypoint ringed, numbers when zoomed in, a
//  tooltip on hover. A point's floor is the layer nearest its Z (nav Z * 240); points
//  on floors the map isn't showing are hidden, on shown other floors dimmed, like the
//  walls. Points outside this dungeon's floor area aren't drawn.
//
//  "Edit route" (off by default; needs RynthAi with MapEdit >= 1): the map holds still while it
//  is on - fixed on the spot it was showing (not following you), north up, and on the floors it
//  was showing - so a point can be placed or dragged while you walk; turning it off puts Follow
//  back as it was.
//    - click (no drag) on a floor: add a point there (after the selected point, else
//      at the end; Z from that floor: the player's floor first, then the other shown
//      floors nearest it). Off every shown floor: refused with a status line;
//    - click a point: select / deselect it; Esc deselects;
//    - drag a point: move it (it follows the cursor; one move on release; Esc or
//      right-click cancels; a drop off every floor leaves it where it was);
//    - drag empty space: pans as always; the wheel zooms;
//    - right-click a point: Delete / Insert after / Set as next target.
//  Every edit is a RynthAi nav command (addPointAt, movePointTo, deletePoint,
//  setActive); the route file stays RynthAi's and the map shows the next poll.
//  Coordinates: DungeonMapMath (screen <-> world through pan and rotation, world <->
//  nav).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.UI;
using RynthCore.Engine.UI.Data;
using MarkerStyleSettings = RynthCore.Engine.UI.Panels.MarkerStyleSettings;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed class DungeonMapFace : IImGuiPanel
{
    public const string Title = "Dungeon Map";

    public static void Register()
    {
        DungeonMapSettingsStore.Load(); // engine init thread: the file read stays off AC's thread
        ImGuiPanelHost.Register(Title,
            new PanelSpec(new Vector2(480, 520), new Vector2(240, 200), Background: 0, EdgeToEdge: true, OpenCentered: true),
            () => new DungeonMapFace());
    }

    // ── Palette (the plugin map's colours, ARGB) ──────────────────────────
    private const uint CanvasBgArgb = 0xFF0A121F, ToolbarBgArgb = 0xE00A121A;
    private static readonly uint CanvasBorder = C(0xFF334D66), Wall = C(0xFF8CCCFF), PlayerCol = C(0xFF33FF66),
        Portal = C(0xFFE673FF), PortalRing = C(0xB3FFBFFF), PortalLabel = C(0xFFFFE0FF), Door = C(0xFFBF8C40),
        DoorRing = C(0xB3E6BF80), DoorLabel = C(0xFFF2D9A6), Monster = C(0xFFFF4033), MonsterRing = C(0xB3FF8C80),
        Npc = C(0xFFFFD933), NpcRing = C(0xB3FFF28C), CreatureLabel = C(0xFFFFE6E6), Hint = C(0xFF8FA3B8),
        FloorOn = C(0xFF2673B3), FloorHere = C(0xFF1AA64D);
    private static uint ButtonBg => RynthTheme.WithAlpha(RynthTheme.Field, 0xC0);
    private static uint ButtonBorder => RynthTheme.Border;
    private const uint FillFlat = 0x662E8C38, FillUp = 0x668C1F14, FillDown = 0x661A8026, FillVisited = 0xB84D6B94;
    // Route (ARGB, alpha scaled per floor).
    private const uint RouteLegArgb = 0xD9FFA733, RoutePointArgb = 0xFFFFC266, RouteActiveArgb = 0xFFFFF04D,
        RouteSelectedArgb = 0xFFFFFFFF, RouteBadgeBgArgb = 0xE00A121F, RouteNumberArgb = 0xFFFFE6B3,
        RouteBadArgb = 0xFFFF4D4D, StatusArgb = 0xFFFFD27A;
    private static uint C(uint argb) => RynthTheme.Argb(argb);

    /// <summary>ARGB colour with its alpha scaled, as an ImGui colour.</summary>
    private static uint MulAlpha(uint argb, float k) =>
        RynthTheme.Argb((argb & 0x00FFFFFF) | ((uint)Math.Clamp((int)MathF.Round((argb >> 24) * k), 0, 255) << 24));

    private const float DimOtherFloors = 0.25f;   // the plugin map's DimCol
    private const float DefaultZoom = 5f, MinZoom = 1f, MaxZoom = 20f;

    // ── Session state (per open) ─────────────────────────────────────────
    private float _zoom = DefaultZoom;
    private Vector2 _pan;
    private bool _follow = true;
    private bool _oneUpOneDown = true;
    private readonly HashSet<int> _visible = new();
    private int _autoLayer = -1;
    private uint _landblock;
    private int[] _drawOrder = Array.Empty<int>();

    // ── Route state (per open) ───────────────────────────────────────────
    private const float NumberZoom = 6f;      // point numbers from this zoom up
    private const float PickRadius = 8f;      // px: a press this close to a point grabs it
    private const long StatusMs = 4000;
    private bool _navSubscribed;
    private bool _editRoute;
    private bool _followBeforeEdit;           // Follow as it was when editing began
    private bool _anchorSet;                  // the world spot the frozen map is centred on (Edit route)
    private Vector2 _anchorWorld;
    private bool _markersOpen;                // the Markers (sizes and names) window
    private int _selected = -1;               // route index the next added point goes after
    private int _hoverPt = -1;
    private readonly List<(int Idx, Vector2 At)> _drawn = new();   // this frame's drawn points, for picking
    private Vector2[] _ptScreen = Array.Empty<Vector2>();
    private float[] _ptAlpha = Array.Empty<float>();
    private int[] _ptLayer = Array.Empty<int>();
    private bool _pressing;                   // left button went down on the canvas
    private int _pressPt = -1;                // the point under that press (Edit route)
    private bool _pressMoved;                 // the press turned into a drag
    private int _dragPt = -1;                 // the point being dragged
    private Vector2 _dragWorld;
    private bool _dragOnFloor;
    private int _pendingMoveIdx = -1;         // a sent move, drawn there until the route updates
    private Vector2 _pendingMoveWorld;
    private object? _pendingMoveFrom;
    private long _pendingMoveUntil;
    private bool _openPtMenu;
    private int _menuPt = -1;
    private string _status = string.Empty;
    private long _statusUntil;
    private object? _boundsFor;
    private bool _haveBounds;
    private Vector4 _bounds;
    private int[] _layerOrder = new int[16];

    public int PopOutIdleHz => 20;   // markers move

    public bool HiddenWhileDocked
    {
        get
        {
            RadarView? view = UiSources.Radar.Current?.Value;
            return view != null && !view.Live.IsIndoor;
        }
    }

    public void OnShown()
    {
        DungeonMapSettingsStore.Load();
        UiSources.Radar.Subscribe();
        UiSources.Radar.RequestRefresh();
    }

    public void OnHidden()
    {
        UiSources.Radar.Unsubscribe();
        SyncNavSubscription(false);
        CancelPress();
    }

    /// <summary>The route source is polled only while the map is open with Route on.</summary>
    private void SyncNavSubscription(bool want)
    {
        if (want == _navSubscribed) return;
        _navSubscribed = want;
        if (want)
        {
            UiSources.Nav.Subscribe();
            UiSources.Nav.RequestRefresh();
        }
        else UiSources.Nav.Unsubscribe();
    }

    public void Draw()
    {
        SyncNavSubscription(DungeonMapSettingsStore.ShowRoute);
        Vector2 origin = ImGuiNET.ImGui.GetCursorScreenPos();
        Vector2 size = ImGuiNET.ImGui.GetContentRegionAvail();
        if (size.X < 16 || size.Y < 16) return;
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        RadarView? view = UiSources.Radar.Current?.Value;
        NavPayload? nav = DungeonMapSettingsStore.ShowRoute ? UiSources.Nav.Current?.Value : null;

        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui11));
        try
        {
            if (view == null) { CancelPress(); Message(dl, origin, size, "Waiting for map data from RynthAi..."); return; }
            RadarSnapshot live = view.Live;
            if (!live.IsIndoor) { CancelPress(); Message(dl, origin, size, "Outdoors: the map shows while you're in a dungeon."); return; }

            int layers = live.LayerZs.Count;
            TrackLayers(view, live.Player.Landblock, layers);

            // ── Toolbar ──
            float top = origin.Y;
            if (DungeonMapSettingsStore.ShowToolbar)
            {
                float barH = Toolbar(dl, origin, size.X, view, layers);
                top += barH;
            }

            // ── Canvas ──
            var canvasMin = new Vector2(origin.X, top);
            var canvasMax = origin + size;
            if (canvasMax.Y - canvasMin.Y < 24) return;
            dl.AddRectFilled(canvasMin, canvasMax, MulAlpha(CanvasBgArgb, Math.Clamp(DungeonMapSettingsStore.BgOpacity, 0f, 1f)));
            dl.AddRect(canvasMin, canvasMax, CanvasBorder);

            if (_follow) _pan = Vector2.Zero;
            Vector2 centre = (canvasMin + canvasMax) * 0.5f + _pan;

            DungeonMapProjection? proj = null;
            bool routeDrawn = false;
            _drawn.Clear();
            if (layers == 0)
                DrawCentredText(dl, (canvasMin + canvasMax) * 0.5f, Hint,
                    $"Loading dungeon geometry for {live.Player.Landblock:X4}...");
            else
            {
                ImDrawListFlags saved = dl.Flags;
                dl.Flags = saved & ~(ImDrawListFlags.AntiAliasedLines | ImDrawListFlags.AntiAliasedLinesUseTex | ImDrawListFlags.AntiAliasedFill);
                dl.PushClipRect(canvasMin, canvasMax, true);
                // Editing: centred on a fixed spot, north up, so the map doesn't move under the mouse.
                if (_editRoute && !_anchorSet) { _anchorWorld = new Vector2(live.Player.WorldX, live.Player.WorldY); _anchorSet = true; }
                bool frozen = _editRoute && _anchorSet;
                var p = new DungeonMapProjection(centre,
                    frozen ? _anchorWorld.X : live.Player.WorldX, frozen ? _anchorWorld.Y : live.Player.WorldY, live.Player.Heading,
                    DungeonMapSettingsStore.RotateWithPlayer && !frozen, _zoom);
                proj = p;
                DrawFloors(dl, view, p, canvasMin, canvasMax);
                dl.Flags = saved;
                if (nav != null) routeDrawn = DrawRoute(dl, view, nav, p, canvasMin, canvasMax);
                DrawMarkers(dl, live, p, canvasMin, canvasMax);
                DrawPlayer(dl, frozen ? p.P(live.Player.WorldX, live.Player.WorldY) : centre, p, DungeonMapSettingsStore.Markers.SelfSize);
                DrawRouteStatus(dl, canvasMin, canvasMax, nav, routeDrawn);
                dl.PopClipRect();
            }

            // Show-toolbar button over the canvas corner (submitted before the surface so it wins the hover).
            if (!DungeonMapSettingsStore.ShowToolbar)
            {
                if (IconButton("##dmap_showbar", canvasMin + new Vector2(4, 4), PhosphorIcons.CaretDown))
                {
                    DungeonMapSettingsStore.ShowToolbar = true;
                    DungeonMapSettingsStore.Save();
                }
                ImGuiNET.ImGui.SetItemTooltip("Show the map settings");
            }

            CanvasInput(canvasMin, canvasMax, view, nav, proj);
            DrawPointMenu(nav);
            MarkersWindow();
        }
        finally
        {
            ImGuiNET.ImGui.PopFont();
        }
    }

    // ── Floors ────────────────────────────────────────────────────────────

    /// <summary>Follows the player's floor: 1U1D keeps current +-1, Follow alone keeps just the current one.</summary>
    private void TrackLayers(RadarView view, uint landblock, int layers)
    {
        if (landblock != _landblock)
        {
            _landblock = landblock;
            _pan = Vector2.Zero;
            _autoLayer = -1;
            _visible.Clear();
            if (_editRoute) { StopEditing(); SetStatus("Edit route turned off: you went to another dungeon."); }
        }
        if (_editRoute) return;      // the floors shown stay as they were
        int best = view.CurrentLayer;
        if (best < 0 || best == _autoLayer) return;
        _autoLayer = best;
        if (_oneUpOneDown) SetOneUpOneDown(layers);
        else if (_follow || _visible.Count == 0) { _visible.Clear(); _visible.Add(best); }
    }

    private void SetOneUpOneDown(int layers)
    {
        _visible.Clear();
        if (_autoLayer < 0) return;
        if (_autoLayer > 0) _visible.Add(_autoLayer - 1);
        _visible.Add(_autoLayer);
        if (_autoLayer < layers - 1) _visible.Add(_autoLayer + 1);
    }

    private void DrawFloors(ImDrawListPtr dl, RadarView view, in DungeonMapProjection proj, Vector2 clipMin, Vector2 clipMax)
    {
        int layers = view.Live.LayerZs.Count;
        int cur = view.CurrentLayer;
        // Other floors first, the player's floor on top.
        if (_drawOrder.Length < layers) _drawOrder = new int[layers];
        int n = 0;
        foreach (int i in _visible) if (i >= 0 && i < layers && i != cur) _drawOrder[n++] = i;
        bool curVisible = cur >= 0 && _visible.Contains(cur);
        if (curVisible) _drawOrder[n++] = cur;

        // Fill strips: 5 floats (x0, y0, x1, y1, type).
        for (int k = 0; k < n; k++)
        {
            int layer = _drawOrder[k];
            if (view.FillsByLayer[layer] is not float[] strips) continue;
            float a = layer == cur ? 1f : DimOtherFloors;
            uint flat = MulAlpha(FillFlat, a), up = MulAlpha(FillUp, a), down = MulAlpha(FillDown, a);
            for (int i = 0; i + 4 < strips.Length; i += 5)
            {
                int type = (int)strips[i + 4];
                FillRect(dl, proj, strips[i], strips[i + 1], strips[i + 2], strips[i + 3],
                    type == 1 ? up : type == 2 ? down : flat, clipMin, clipMax);
            }
        }

        // Visited cells, the player's floor only: 4 floats per strip.
        if (curVisible && view.VisitedByLayer[cur] is float[] visited)
        {
            uint fill = RynthTheme.Argb(FillVisited);
            for (int i = 0; i + 3 < visited.Length; i += 4)
                FillRect(dl, proj, visited[i], visited[i + 1], visited[i + 2], visited[i + 3], fill, clipMin, clipMax);
        }

        // Walls: 6 floats (ax, ay, bx, by, cell x, cell y).
        for (int k = 0; k < n; k++)
        {
            int layer = _drawOrder[k];
            if (view.WallsByLayer[layer] is not float[] segs) continue;
            bool current = layer == cur;
            uint col = current ? Wall : MulAlpha(0xFF8CCCFF, DimOtherFloors);
            float thick = current ? 1.5f : 1f;
            for (int i = 0; i + 5 < segs.Length; i += 6)
            {
                Vector2 pa = proj.P(segs[i], segs[i + 1]);
                Vector2 pb = proj.P(segs[i + 2], segs[i + 3]);
                if (MathF.Max(pa.X, pb.X) < clipMin.X || MathF.Min(pa.X, pb.X) > clipMax.X ||
                    MathF.Max(pa.Y, pb.Y) < clipMin.Y || MathF.Min(pa.Y, pb.Y) > clipMax.Y) continue;
                dl.AddLine(pa, pb, col, thick);
            }
        }
    }

    private static void FillRect(ImDrawListPtr dl, in DungeonMapProjection proj, float x0, float y0, float x1, float y1,
        uint col, Vector2 clipMin, Vector2 clipMax)
    {
        Vector2 p00 = proj.P(x0, y0), p11 = proj.P(x1, y1);
        if (proj.Rotated)
        {
            Vector2 p10 = proj.P(x1, y0), p01 = proj.P(x0, y1);
            float minX = MathF.Min(MathF.Min(p00.X, p11.X), MathF.Min(p10.X, p01.X));
            float maxX = MathF.Max(MathF.Max(p00.X, p11.X), MathF.Max(p10.X, p01.X));
            float minY = MathF.Min(MathF.Min(p00.Y, p11.Y), MathF.Min(p10.Y, p01.Y));
            float maxY = MathF.Max(MathF.Max(p00.Y, p11.Y), MathF.Max(p10.Y, p01.Y));
            if (maxX < clipMin.X || minX > clipMax.X || maxY < clipMin.Y || minY > clipMax.Y) return;
            dl.AddQuadFilled(p00, p10, p11, p01, col);
        }
        else
        {
            var a = Vector2.Min(p00, p11);
            var b = Vector2.Max(p00, p11);
            if (b.X < clipMin.X || a.X > clipMax.X || b.Y < clipMin.Y || a.Y > clipMax.Y) return;
            dl.AddRectFilled(a, b, col);
        }
    }

    // ── Markers and player ────────────────────────────────────────────────

    private static void DrawMarkers(ImDrawListPtr dl, RadarSnapshot live, in DungeonMapProjection proj, Vector2 clipMin, Vector2 clipMax)
    {
        const float Margin = 20f;
        bool doors = DungeonMapSettingsStore.ShowDoors, creatures = DungeonMapSettingsStore.ShowCreatures,
            corpses = DungeonMapSettingsStore.ShowCorpses;
        MarkerStyleSettings markers = DungeonMapSettingsStore.Markers;
        RadarMarkerStyle.FillColors(KindCols);
        bool anyName = false;
        foreach (RadarMarker m in live.Markers)
        {
            if (!ShowOnMap(m.Kind, doors, creatures, corpses)) continue;
            anyName |= markers.NameShown(m.Kind);
            Vector2 p = proj.P(m.X, m.Y);
            if (p.X < clipMin.X - Margin || p.X > clipMax.X + Margin || p.Y < clipMin.Y - Margin || p.Y > clipMax.Y + Margin) continue;
            float s = markers.SizeOf(m.Kind);
            switch (m.Kind)
            {
                case 0:
                    dl.AddCircleFilled(p, 4f * s, Monster, 10);
                    dl.AddCircle(p, 6f * s, MonsterRing, 10, 1f);
                    break;
                case 1:
                    dl.AddCircleFilled(p, 4f * s, Npc, 10);
                    dl.AddCircle(p, 6f * s, NpcRing, 10, 1.2f);
                    break;
                case 2:
                    dl.AddCircleFilled(p, 5f * s, Portal, 12);
                    dl.AddCircle(p, 7f * s, PortalRing, 12, 1.5f);
                    break;
                case 3:
                    float r = 5f * s, o = r + 1.5f;
                    dl.AddQuadFilled(p + new Vector2(0, -r), p + new Vector2(r, 0), p + new Vector2(0, r), p + new Vector2(-r, 0), Door);
                    dl.AddQuad(p + new Vector2(0, -o), p + new Vector2(o, 0), p + new Vector2(0, o), p + new Vector2(-o, 0), DoorRing, 1.2f);
                    break;
                default:
                    // The kinds added 2026-10-04: the radar's shape and (user-pickable) colour, a size up.
                    RadarMarkerStyle.Draw(dl, m.Kind, p, KindCols[m.Kind], 1.35f * s);
                    break;
            }
        }

        // Names last, over every marker.
        if (!anyName) return;
        ImFontPtr nameFont = ImGuiFonts.Get(UiFont.Ui11);
        foreach (RadarMarker m in live.Markers)
        {
            if (!markers.NameShown(m.Kind) || !ShowOnMap(m.Kind, doors, creatures, corpses)) continue;
            Vector2 p = proj.P(m.X, m.Y);
            if (p.X < clipMin.X - Margin || p.X > clipMax.X + Margin || p.Y < clipMin.Y - Margin || p.Y > clipMax.Y + Margin) continue;
            // Half the marker's width as drawn above (kinds 0-3: their outer ring).
            float reach = m.Kind switch { 0 or 1 => 6f, 2 => 7f, 3 => 6.5f, _ => RadarMarkerStyle.Radius(m.Kind) * 1.35f };
            RadarMarkerStyle.DrawName(dl, nameFont, p, reach * markers.SizeOf(m.Kind), NameColor(m.Kind), m.Label, markers.NameSizeOf(m.Kind));
        }
    }

    /// <summary>The map's label colours: warm white for creatures, tinted for portals and doors.</summary>
    private static uint NameColor(byte kind) => kind switch
    {
        RadarKind.Portal => PortalLabel,
        RadarKind.Door => DoorLabel,
        _ => CreatureLabel,
    };

    private static readonly uint[] KindCols = new uint[RadarKind.Count];

    /// <summary>
    /// Portals always; doors with the map's Doors box; monsters and NPCs with its Creatures box.
    /// The kinds added 2026-10-04 also follow the radar's show box for that kind (so items on the
    /// ground stay off unless turned on there): players, fellows, pets and vendors with Creatures,
    /// corpses (both kinds) with the map's Corpses box too (2026-10-12: off hides them here whatever
    /// the radar says), lifestones and ground items on the radar's box alone. Unknown kinds: hidden.
    /// </summary>
    private static bool ShowOnMap(byte kind, bool doors, bool creatures, bool corpses) => kind switch
    {
        RadarKind.Portal => true,
        RadarKind.Door => doors,
        RadarKind.Monster or RadarKind.Npc => creatures,
        RadarKind.Player or RadarKind.Fellow or RadarKind.Pet or RadarKind.Vendor =>
            creatures && UI.Panels.RadarSettingsStore.ShowKind(kind),
        RadarKind.Corpse or RadarKind.OwnCorpse => corpses && UI.Panels.RadarSettingsStore.ShowKind(kind),
        _ => UI.Panels.RadarSettingsStore.ShowKind(kind),
    };

    private static void DrawPlayer(ImDrawListPtr dl, Vector2 at, in DungeonMapProjection proj, float s)
    {
        dl.AddCircleFilled(at, 4f * s, PlayerCol, 10);
        // Rotating: facing is always up. North-up: AC heading 0 = east, 90 = north.
        Vector2 tip = proj.Rotated ? at + new Vector2(0, -10 * s) : at + new Vector2(proj.Cos * 10 * s, -proj.Sin * 10 * s);
        dl.AddLine(at, tip, PlayerCol, 1.5f);
    }

    // ── Route ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Draws the route's legs and points over the floors. Fills <see cref="_drawn"/> with the
    /// points drawn (for hover and clicks). Returns true when at least one point was drawn.
    /// </summary>
    private bool DrawRoute(ImDrawListPtr dl, RadarView view, NavPayload nav, in DungeonMapProjection proj, Vector2 clipMin, Vector2 clipMax)
    {
        List<NavPoint> pts = nav.Points;
        int n = pts.Count;
        if (n == 0) return false;
        if (_ptScreen.Length < n)
        {
            int cap = Math.Max(n, _ptScreen.Length * 2);
            _ptScreen = new Vector2[cap];
            _ptAlpha = new float[cap];
            _ptLayer = new int[cap];
        }
        UpdateBounds(view);
        List<float> layerZs = view.Live.LayerZs;
        int cur = view.CurrentLayer;
        bool pending = _pendingMoveIdx >= 0 && ReferenceEquals(nav, _pendingMoveFrom) && Environment.TickCount64 < _pendingMoveUntil;
        if (!pending) _pendingMoveIdx = -1;
        const float BoundsMargin = 24f;

        // Where each point is and how strongly it's drawn (0 = not at all).
        for (int i = 0; i < n; i++)
        {
            NavPoint p = pts[i];
            Vector2 w;
            bool dragged = i == _dragPt && _pressMoved;
            if (dragged) w = _dragWorld;
            else if (pending && i == _pendingMoveIdx) w = _pendingMoveWorld;
            else w = DungeonMapMath.NavToWorld(p.NS, p.EW);
            _ptScreen[i] = proj.P(w.X, w.Y);

            float a = 0f;
            int layer = -1;
            bool inside = _haveBounds && w.X >= _bounds.X - BoundsMargin && w.X <= _bounds.Z + BoundsMargin
                && w.Y >= _bounds.Y - BoundsMargin && w.Y <= _bounds.W + BoundsMargin;
            if (inside)
            {
                layer = DungeonMapMath.NearestLayer(layerZs, DungeonMapMath.NavZToWorld(p.Z));
                if (layer >= 0 && _visible.Contains(layer)) a = layer == cur ? 1f : DimOtherFloors;
            }
            if (dragged) a = 1f;
            _ptAlpha[i] = a;
            _ptLayer[i] = layer;
        }

        // Legs: point to point, back to the start on a Circular route.
        bool closed = nav.RouteType == 1 && n > 2;
        int legs = closed ? n : n - 1;
        for (int k = 0; k < legs; k++)
        {
            int ia = k, ib = (k + 1) % n;
            float a = MathF.Min(_ptAlpha[ia], _ptAlpha[ib]);
            if (a <= 0f) continue;
            Vector2 pa = _ptScreen[ia], pb = _ptScreen[ib];
            if (MathF.Max(pa.X, pb.X) < clipMin.X || MathF.Min(pa.X, pb.X) > clipMax.X ||
                MathF.Max(pa.Y, pb.Y) < clipMin.Y || MathF.Min(pa.Y, pb.Y) > clipMax.Y) continue;
            uint col = MulAlpha(RouteLegArgb, a);
            float thick = ib == nav.ActiveNavIndex ? 2.5f : 1.5f;
            if (IsJumpStep(pts[ia].Type)) DashedLine(dl, pa, pb, col, thick);   // a recall / portal: no walk
            else dl.AddLine(pa, pb, col, thick);
            LegArrow(dl, pa, pb, col);
        }

        // Points, the dragged one last (on top).
        ImFontPtr font = ImGuiNET.ImGui.GetFont();
        bool numbers = _zoom >= NumberZoom;
        int drawnCount = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < n; i++)
            {
                bool top = i == _dragPt && _pressMoved;
                if ((pass == 1) != top) continue;
                float a = _ptAlpha[i];
                if (a <= 0f) continue;
                Vector2 at = _ptScreen[i];
                if (at.X < clipMin.X - 12 || at.X > clipMax.X + 12 || at.Y < clipMin.Y - 12 || at.Y > clipMax.Y + 12) continue;
                DrawRoutePoint(dl, font, pts[i].Type, at, a, i == nav.ActiveNavIndex, i == _selected, i == _hoverPt);
                if (top && !_dragOnFloor) dl.AddCircle(at, 9f, RynthTheme.Argb(RouteBadArgb), 16, 2f);
                if (numbers) dl.AddText(at + new Vector2(7, -15), MulAlpha(RouteNumberArgb, a), NumberLabel(i));
                _drawn.Add((i, at));
                drawnCount++;
            }
        }
        return drawnCount > 0;
    }

    /// <summary>A Recall or Portal step: the leg after it is a jump, not a walk (drawn dashed).</summary>
    private static bool IsJumpStep(string type) => type is "Recall" or "PortalNPC";

    private static void DrawRoutePoint(ImDrawListPtr dl, ImFontPtr font, string type, Vector2 at, float a,
        bool active, bool selected, bool hovered)
    {
        if (active) dl.AddCircle(at, 9.5f, MulAlpha(RouteActiveArgb, a), 18, 2f);
        if (selected) dl.AddCircle(at, 12.5f, MulAlpha(RouteSelectedArgb, a), 20, 1.5f);
        string? glyph = TypeGlyph(type);
        if (glyph == null)
        {
            // Point / Checkpoint (walked to).
            dl.AddCircleFilled(at, hovered ? 4.5f : 3.5f, MulAlpha(RoutePointArgb, a), 10);
            if (type == "Checkpoint") dl.AddCircle(at, 6f, MulAlpha(RoutePointArgb, a), 12, 1.2f);
            return;
        }
        const float r = 7f;
        uint col = MulAlpha(TypeColour(type), a);
        dl.AddCircleFilled(at, hovered ? r + 1 : r, MulAlpha(RouteBadgeBgArgb, a), 14);
        dl.AddCircle(at, hovered ? r + 1 : r, col, 14, 1.3f);
        PhosphorIcons.DrawCentered(dl, font, 10f, glyph, at - new Vector2(r, r), new Vector2(2 * r, 2 * r), col);
    }

    /// <summary>The badge icon for a step type that isn't simply walked to (RynthAi's NavPointType names).</summary>
    private static string? TypeGlyph(string type) => type switch
    {
        "Point" or "Checkpoint" or "" => null,
        "Recall" => PhosphorIcons.ArrowCounterClockwise,
        "Pause" => PhosphorIcons.HourglassMedium,
        "Chat" => PhosphorIcons.ChatCircleText,
        "PortalNPC" => PhosphorIcons.Spiral,
        "Npc" => PhosphorIcons.User,
        "OpenVendor" => PhosphorIcons.Storefront,
        "Jump" => PhosphorIcons.ArrowCircleUp,
        _ => PhosphorIcons.Info,
    };

    private static uint TypeColour(string type) => type switch
    {
        "Recall" => 0xFF8CB3FF,
        "Pause" => 0xFFE6E6E6,
        "Chat" => 0xFF8CFFB3,
        "PortalNPC" => 0xFFE673FF,
        "Npc" => 0xFFFFD933,
        "OpenVendor" => 0xFFFFB366,
        "Jump" => 0xFF66E6FF,
        _ => 0xFFB0B0B0,
    };

    /// <summary>"Portal" for PortalNPC and so on, for the tooltip.</summary>
    private static string TypeName(string type) => type switch
    {
        "PortalNPC" => "Portal",
        "Npc" => "NPC",
        "OpenVendor" => "Vendor",
        "" => "Point",
        _ => type,
    };

    private static void DashedLine(ImDrawListPtr dl, Vector2 a, Vector2 b, uint col, float thick)
    {
        float len = Vector2.Distance(a, b);
        if (len < 1f) return;
        if (len > 4000f) { dl.AddLine(a, b, col, thick); return; }
        Vector2 d = (b - a) / len;
        const float On = 5f, Off = 4f;
        for (float t = 0; t < len; t += On + Off)
            dl.AddLine(a + d * t, a + d * MathF.Min(t + On, len), col, thick);
    }

    /// <summary>A small arrowhead at the middle of a leg that's long enough, pointing the way the route goes.</summary>
    private static void LegArrow(ImDrawListPtr dl, Vector2 a, Vector2 b, uint col)
    {
        float len = Vector2.Distance(a, b);
        if (len < 48f || len > 8000f) return;
        Vector2 d = (b - a) / len, nrm = new(-d.Y, d.X);
        Vector2 tip = (a + b) * 0.5f + d * 4f;
        dl.AddTriangleFilled(tip, tip - d * 7f + nrm * 4f, tip - d * 7f - nrm * 4f, col);
    }

    private static readonly List<string> NumberLabels = new();

    private static string NumberLabel(int i)
    {
        while (NumberLabels.Count <= i) NumberLabels.Add(NumberLabels.Count.ToString(CultureInfo.InvariantCulture));
        return NumberLabels[i];
    }

    /// <summary>The floors' bounding box (cached per geometry): route points outside it aren't in this dungeon.</summary>
    private void UpdateBounds(RadarView view)
    {
        if (ReferenceEquals(_boundsFor, view.Fills)) return;
        _boundsFor = view.Fills;
        _haveBounds = DungeonMapMath.FillBounds(view.FillsByLayer, out _bounds);
    }

    /// <summary>The floor (layer index) under a world spot, among the floors shown: the player's first.</summary>
    private int FloorUnder(RadarView view, Vector2 world)
    {
        int layers = view.Live.LayerZs.Count;
        if (_layerOrder.Length < layers) _layerOrder = new int[layers];
        int n = DungeonMapMath.LayerOrder(view.CurrentLayer, _visible, layers, _layerOrder);
        return DungeonMapMath.FloorAt(world.X, world.Y, view.FillsByLayer, _layerOrder.AsSpan(0, n));
    }

    /// <summary>Status line (bottom left of the canvas): the last edit's result, and in Edit route the controls.</summary>
    private void DrawRouteStatus(ImDrawListPtr dl, Vector2 canvasMin, Vector2 canvasMax, NavPayload? nav, bool routeDrawn)
    {
        float y = canvasMax.Y - 18f;
        float x = canvasMin.X + 6f;
        if (_editRoute)
        {
            string hint = _selected >= 0
                ? $"Edit route: click a floor to add after {_selected} | drag a point to move | right-click a point | Esc deselects"
                : "Edit route: click a floor to add at the end | click a point to select | drag to move | right-click a point";
            dl.AddText(new Vector2(x, y), Hint, hint);
            y -= 16f;
        }
        else if (DungeonMapSettingsStore.ShowRoute && nav != null && nav.Points.Count > 0 && !routeDrawn)
        {
            dl.AddText(new Vector2(x, y), Hint, $"Route ({nav.Points.Count} points): none on the floors shown");
            y -= 16f;
        }
        if (_status.Length > 0 && Environment.TickCount64 < _statusUntil)
            dl.AddText(new Vector2(x, y), RynthTheme.Argb(StatusArgb), _status);
    }

    private void SetStatus(string text)
    {
        _status = text;
        _statusUntil = Environment.TickCount64 + StatusMs;
    }

    /// <summary>Edit route can be turned on: RynthAi has the map commands (or hasn't answered yet).</summary>
    private bool TryStartEditing()
    {
        NavPayload? nav = UiSources.Nav.Current?.Value;
        if (nav != null && nav.MapEdit < 1)
        {
            SetStatus("Editing the route on the map needs a newer RynthAi.");
            return false;
        }
        _followBeforeEdit = _follow;
        _follow = false;
        _anchorSet = false;
        _editRoute = true;
        return true;
    }

    private void StopEditing()
    {
        if (_editRoute) { _follow = _followBeforeEdit; _anchorSet = false; }
        _editRoute = false;
        _selected = -1;
        CancelPress();
    }

    private void CancelPress()
    {
        _pressing = false;
        _pressPt = -1;
        _pressMoved = false;
        _dragPt = -1;
    }

    // ── Canvas input ──────────────────────────────────────────────────────

    /// <summary>
    /// Drag pans (and turns Follow off), the wheel zooms. Edit route: a press on a point and a drag
    /// moves it; a click on a point selects it; a click on a floor adds a point; a drag from empty
    /// space still pans; right-click a point for its menu.
    /// </summary>
    private void CanvasInput(Vector2 canvasMin, Vector2 canvasMax, RadarView view, NavPayload? nav, DungeonMapProjection? proj)
    {
        ImGuiIOPtr io = ImGuiNET.ImGui.GetIO();
        ImGuiNET.ImGui.SetCursorScreenPos(canvasMin);
        ImGuiNET.ImGui.InvisibleButton("##dmap_canvas", Vector2.Max(canvasMax - canvasMin, new Vector2(1, 1)),
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        bool hovered = ImGuiNET.ImGui.IsItemHovered();
        Vector2 mouse = io.MousePos;
        bool canEdit = _editRoute && nav != null && nav.MapEdit >= 1 && proj.HasValue;
        int count = nav?.Points.Count ?? 0;
        if (_selected >= count) _selected = -1;

        _hoverPt = (hovered || _pressing) && _drawn.Count > 0 ? DungeonMapMath.Pick(mouse, _drawn, PickRadius) : -1;

        // Press.
        if (hovered && ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            _pressing = true;
            _pressMoved = false;
            _pressPt = canEdit ? _hoverPt : -1;
            _dragPt = -1;
        }

        // Drag: a point (Edit route, press on a point) or the map.
        if (_pressing && ImGuiNET.ImGui.IsMouseDown(ImGuiMouseButton.Left) && ImGuiNET.ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            _pressMoved = true;
            if (_pressPt >= 0)
            {
                if (canEdit && _pressPt < count)
                {
                    _dragPt = _pressPt;
                    _dragWorld = proj!.Value.World(mouse);
                    _dragOnFloor = FloorUnder(view, _dragWorld) >= 0;
                }
            }
            else
            {
                Vector2 delta = io.MouseDelta;
                if (float.IsFinite(delta.X) && float.IsFinite(delta.Y) && delta != Vector2.Zero)
                {
                    _follow = false;
                    _pan += delta;
                }
            }
        }

        // Cancel a point drag: Esc or the right button. Esc with nothing dragged deselects.
        bool rightClicked = hovered && ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Right);
        bool esc = ImGuiNET.ImGui.IsKeyPressed(ImGuiKey.Escape, false);
        if (_pressing && _pressPt >= 0 && (esc || rightClicked))
        {
            if (_dragPt >= 0) SetStatus("Move cancelled.");
            CancelPress();
            rightClicked = false;
        }
        else if (esc && canEdit && _selected >= 0 && ImGuiNET.ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
            _selected = -1;

        // Release.
        if (_pressing && ImGuiNET.ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            if (canEdit)
            {
                if (_pressMoved)
                {
                    if (_dragPt >= 0) CommitMove(view, nav!, _dragPt, _dragWorld);
                }
                else if (_pressPt >= 0) _selected = _selected == _pressPt ? -1 : _pressPt;
                else if (hovered) AddPointAt(view, nav!, proj!.Value.World(mouse));
            }
            else if (_editRoute && !_pressMoved && hovered)
                SetStatus(nav == null ? "Waiting for the route from RynthAi..." : "Editing the route on the map needs a newer RynthAi.");
            CancelPress();
        }
        if (!ImGuiNET.ImGui.IsMouseDown(ImGuiMouseButton.Left) && _pressing) CancelPress();   // released elsewhere

        // Right-click a point: its menu.
        if (rightClicked && canEdit && _hoverPt >= 0)
        {
            _menuPt = _hoverPt;
            _openPtMenu = true;
        }

        if (hovered)
        {
            float wheel = io.MouseWheel;
            if (float.IsFinite(wheel) && wheel != 0f)
                _zoom = Math.Clamp(_zoom + wheel * 0.5f, MinZoom, MaxZoom);
        }

        if (hovered && _hoverPt >= 0 && _dragPt < 0 && nav != null && _hoverPt < count && !ImGuiNET.ImGui.IsPopupOpen("##dmap_ptmenu"))
            PointTooltip(nav, _hoverPt, view);
    }

    private void AddPointAt(RadarView view, NavPayload nav, Vector2 world)
    {
        int layer = FloorUnder(view, world);
        if (layer < 0)
        {
            SetStatus("Not on a floor: click inside a room or corridor (on a floor that's shown).");
            return;
        }
        float z = view.Live.LayerZs[layer];
        int after = _selected;
        NavCommands.Send(new NavCmd
        {
            Cmd = "addPointAt",
            NS = DungeonMapMath.WorldToNav(world.Y),
            EW = DungeonMapMath.WorldToNav(world.X),
            Z = DungeonMapMath.WorldZToNav(z),
            AddMode = after >= 0 ? 2 : 0,
            InsertAt = after,
        });
        if (after >= 0)
        {
            SetStatus($"Added point {after + 1} after {after} ({FloorName(layer)}).");
            _selected = after + 1;   // the next click goes after the new one
        }
        else SetStatus($"Added point {nav.Points.Count} at the end ({FloorName(layer)}).");
    }

    private void CommitMove(RadarView view, NavPayload nav, int idx, Vector2 world)
    {
        if (idx < 0 || idx >= nav.Points.Count) return;
        int layer = FloorUnder(view, world);
        if (layer < 0)
        {
            SetStatus($"Dropped off the floor: point {idx} not moved.");
            return;
        }
        NavCommands.Send(new NavCmd
        {
            Cmd = "movePointTo",
            Index = idx,
            NS = DungeonMapMath.WorldToNav(world.Y),
            EW = DungeonMapMath.WorldToNav(world.X),
            Z = DungeonMapMath.WorldZToNav(view.Live.LayerZs[layer]),
        });
        // Keep it where it was dropped until the route comes back (or 3 s, if RynthAi refused).
        _pendingMoveIdx = idx;
        _pendingMoveWorld = world;
        _pendingMoveFrom = nav;
        _pendingMoveUntil = Environment.TickCount64 + 3000;
        SetStatus($"Moved point {idx} ({FloorName(layer)}).");
    }

    private static string FloorName(int layer) => "F" + (layer + 1).ToString(CultureInfo.InvariantCulture);

    private void PointTooltip(NavPayload nav, int i, RadarView view)
    {
        NavPoint p = nav.Points[i];
        int layer = i < _ptLayer.Length ? _ptLayer[i] : -1;
        ImGuiNET.ImGui.BeginTooltip();
        ImGuiNET.ImGui.TextUnformatted($"{i}  {TypeName(p.Type)}{(i == nav.ActiveNavIndex ? "  (next waypoint)" : "")}");
        if (!string.IsNullOrEmpty(p.Desc) && TypeGlyph(p.Type) != null) ImGuiNET.ImGui.TextUnformatted(p.Desc);   // a Point's is just its coordinates
        ImGuiNET.ImGui.TextUnformatted(string.Format(CultureInfo.InvariantCulture, "{0:0.000}{1}  {2:0.000}{3}  Z {4:0.0}{5}",
            Math.Abs(p.NS), p.NS >= 0 ? "N" : "S", Math.Abs(p.EW), p.EW >= 0 ? "E" : "W",
            DungeonMapMath.NavZToWorld(p.Z), layer >= 0 ? "  " + FloorName(layer) : ""));
        if (_editRoute)
            ImGuiNET.ImGui.TextDisabled("Drag to move; click to select; right-click for more");
        ImGuiNET.ImGui.EndTooltip();
    }

    /// <summary>Right-click menu on a route point (Edit route): Delete / Insert after / Set as next target.</summary>
    private void DrawPointMenu(NavPayload? nav)
    {
        if (_openPtMenu)
        {
            _openPtMenu = false;
            ImGuiNET.ImGui.OpenPopup("##dmap_ptmenu");
        }
        if (!ImGuiNET.ImGui.BeginPopup("##dmap_ptmenu")) return;
        try
        {
            if (nav == null || _menuPt < 0 || _menuPt >= nav.Points.Count || !_editRoute)
            {
                ImGuiNET.ImGui.CloseCurrentPopup();
                return;
            }
            int i = _menuPt;
            SafeImGui.TextDisabled($"Point {i}  {TypeName(nav.Points[i].Type)}");
            ImGuiNET.ImGui.Separator();
            if (ImGuiNET.ImGui.MenuItem("Delete"))
            {
                NavCommands.Send(new NavCmd { Cmd = "deletePoint", Index = i });
                if (_selected == i) _selected = -1;
                else if (_selected > i) _selected--;
                SetStatus($"Deleted point {i}.");
            }
            if (ImGuiNET.ImGui.MenuItem("Insert after"))
            {
                _selected = i;
                SetStatus($"Click a floor to add a point after {i}.");
            }
            if (ImGuiNET.ImGui.MenuItem("Set as next target", null, i == nav.ActiveNavIndex))
            {
                NavCommands.Send(new NavCmd { Cmd = "setActive", Index = i });
                SetStatus($"Next waypoint: {i}.");
            }
            if (_selected >= 0 && ImGuiNET.ImGui.MenuItem("Clear selection"))
                _selected = -1;
        }
        finally
        {
            ImGuiNET.ImGui.EndPopup();
        }
    }

    // ── Markers window ────────────────────────────────────────────────────

    /// <summary>
    /// Per-kind name box, marker size and name size (DungeonMapSettingsStore.Markers), your own marker's
    /// size, and a reset. Show boxes and colours are the toolbar's and the Radar's.
    /// </summary>
    private void MarkersWindow()
    {
        if (!_markersOpen) return;
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.WindowBg, RynthTheme.BoxBg);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, ButtonBorder);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 4f);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 10));
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6, 4));
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize
            | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoCollapse;
        bool visible = ImGuiNET.ImGui.Begin("##dmap_markers_window", ref _markersOpen, flags);
        ImGuiNET.ImGui.PopStyleVar(3);
        ImGuiNET.ImGui.PopStyleColor(2);
        if (visible)
        {
            ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.UiBold11));
            ImGuiNET.ImGui.TextUnformatted("Map markers");
            ImGuiNET.ImGui.PopFont();
            ImGuiNET.ImGui.SameLine(258 * MathF.Max(1f, EngineFrameController.FontScale) + MarkerStyleEditor.SizeSliderWidth - 14);
            if (FaceKit.IconButton("##dmap_markers_close", PhosphorIcons.X, ImGuiNET.ImGui.GetCursorScreenPos(), new Vector2(14, 14),
                    FaceKit.Faded(FaceKit.Mute), RynthTheme.WithAlpha(RynthTheme.Background, 0xDC), font: UiFont.Ui9))
                _markersOpen = false;
            ImGuiNET.ImGui.SetItemTooltip("Close");
            ImGuiNET.ImGui.TextDisabled("Which kinds show, and their colours: the toolbar and the Radar's settings.");
            ImGuiNET.ImGui.Separator();

            float scale = MathF.Max(1f, EngineFrameController.FontScale);
            float xName = 140 * scale, xSize = 176 * scale, xText = 258 * scale;
            MarkerStyleSettings markers = DungeonMapSettingsStore.Markers;
            bool changed = false;
            MarkerStyleEditor.HeaderRow(xName, xSize, xText);
            foreach (var (kind, label) in RadarFace.KindRowsOrder)
            {
                ImGuiNET.ImGui.PushID(kind);
                ImGuiNET.ImGui.AlignTextToFramePadding();
                ImGuiNET.ImGui.TextUnformatted(label);
                changed |= MarkerStyleEditor.KindCells(markers, kind, xName, xSize, xText);
                ImGuiNET.ImGui.PopID();
            }
            ImGuiNET.ImGui.Separator();
            changed |= MarkerStyleEditor.SliderRow("You (dot and arrow)", ref markers.SelfSize,
                MarkerStyleSettings.MinSize, MarkerStyleSettings.MaxSize, "%.1fx", xSize);
            if (ImGuiNET.ImGui.SmallButton("Reset sizes and names"))
            {
                markers.Reset();
                changed = true;
            }
            if (changed) DungeonMapSettingsStore.Save();
        }
        ImGuiNET.ImGui.End();
    }

    // ── Toolbar ───────────────────────────────────────────────────────────

    /// <summary>Draws the settings rows (they wrap to the width). Returns their height.</summary>
    private float Toolbar(ImDrawListPtr dl, Vector2 origin, float width, RadarView view, int layers)
    {
        const float pad = 4;
        // Background first, sized once the rows are laid out: a channel split keeps it underneath.
        dl.ChannelsSplit(2);
        dl.ChannelsSetCurrent(1);
        ImGuiNET.ImGui.SetCursorScreenPos(origin + new Vector2(pad, pad));
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(4, 1));
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6, 4));
        float limit = origin.X + width - pad;
        bool changed = false;

        if (IconButton("##dmap_hidebar", ImGuiNET.ImGui.GetCursorScreenPos(), PhosphorIcons.CaretUp))
        {
            DungeonMapSettingsStore.ShowToolbar = false;
            changed = true;
        }
        ImGuiNET.ImGui.SetItemTooltip("Hide the map settings");

        // 1U1D and the floors.
        SameLineIfFits(40, limit);
        if (ToggleButton("1U1D##dmap_1u1d", _oneUpOneDown, FloorOn))
        {
            _oneUpOneDown = !_oneUpOneDown;
            if (_oneUpOneDown) SetOneUpOneDown(layers);
        }
        ImGuiNET.ImGui.SetItemTooltip("Show your floor and the ones just above and below it");
        if (layers > 1)
        {
            SameLineIfFits(ImGuiNET.ImGui.CalcTextSize("Floors:").X, limit);
            ImGuiNET.ImGui.AlignTextToFramePadding();
            ImGuiNET.ImGui.TextUnformatted("Floors:");
            for (int i = 0; i < layers; i++)
            {
                string label = FloorLabel(i);
                SameLineIfFits(ImGuiNET.ImGui.CalcTextSize(label).X + 8, limit, 2);
                bool on = _visible.Contains(i);
                if (ToggleButton(label, on, i == view.CurrentLayer ? FloorHere : FloorOn))
                {
                    _oneUpOneDown = false;
                    if (on) _visible.Remove(i); else _visible.Add(i);
                }
            }
            SameLineIfFits(24, limit);
            if (ImGuiNET.ImGui.SmallButton("All##dmap_all"))
            {
                _oneUpOneDown = false;
                _visible.Clear();
                for (int i = 0; i < layers; i++) _visible.Add(i);
            }
            SameLineIfFits(34, limit, 2);
            if (ImGuiNET.ImGui.SmallButton("Here##dmap_here"))
            {
                _oneUpOneDown = false;
                _visible.Clear();
                if (view.CurrentLayer >= 0) _visible.Add(view.CurrentLayer);
            }
        }

        // Zoom.
        SameLineIfFits(130, limit, 12);
        ImGuiNET.ImGui.AlignTextToFramePadding();
        ImGuiNET.ImGui.TextUnformatted("Zoom:");
        ImGuiNET.ImGui.SameLine();
        ImGuiNET.ImGui.SetNextItemWidth(80);
        ImGuiNET.ImGui.SliderFloat("##dmap_zoom", ref _zoom, MinZoom, MaxZoom, "%.1fx");
        if (!float.IsFinite(_zoom)) _zoom = DefaultZoom;

        // Follow / Rotate / Reset.
        SameLineIfFits(190, limit, 12);
        ImGuiNET.ImGui.BeginDisabled(_editRoute);
        ImGuiNET.ImGui.Checkbox("Follow##dmap_follow", ref _follow);
        ImGuiNET.ImGui.EndDisabled();
        SafeImGui.SetItemTooltip(_editRoute ? "The map holds still while Edit route is on"
            : "Keep the map centred on you (dragging the map turns this off)");
        ImGuiNET.ImGui.SameLine();
        ImGuiNET.ImGui.BeginDisabled(_editRoute);
        changed |= ImGuiNET.ImGui.Checkbox("Rotate##dmap_rotate", ref DungeonMapSettingsStore.RotateWithPlayer);
        ImGuiNET.ImGui.EndDisabled();
        SafeImGui.SetItemTooltip(_editRoute ? "The map stays north-up while Edit route is on"
            : "Turn the map so the way you face is up");
        ImGuiNET.ImGui.SameLine();
        if (ImGuiNET.ImGui.SmallButton("Reset##dmap_reset"))
        {
            _pan = Vector2.Zero;
            _zoom = DefaultZoom;
            _follow = !_editRoute;           // editing: the map stays put, re-centred on you where you are now
            _anchorSet = false;
            _oneUpOneDown = true;
            SetOneUpOneDown(layers);
        }

        // Doors / Creatures / Corpses.
        SameLineIfFits(230, limit, 12);
        changed |= ImGuiNET.ImGui.Checkbox("Doors##dmap_doors", ref DungeonMapSettingsStore.ShowDoors);
        ImGuiNET.ImGui.SameLine();
        changed |= ImGuiNET.ImGui.Checkbox("Creatures##dmap_creatures", ref DungeonMapSettingsStore.ShowCreatures);
        ImGuiNET.ImGui.SetItemTooltip("Monsters, NPCs, players, fellows, your pet and vendors. Players, fellows, pets and vendors also follow their box in the Radar's settings; lifestones and items on the ground follow the Radar's settings alone.");
        ImGuiNET.ImGui.SameLine();
        changed |= ImGuiNET.ImGui.Checkbox("Corpses##dmap_corpses", ref DungeonMapSettingsStore.ShowCorpses);
        ImGuiNET.ImGui.SetItemTooltip("Corpses on the map. Off hides them here whatever the Radar shows; on, they also follow the Radar's corpse boxes.");
        ImGuiNET.ImGui.SameLine();
        if (ImGuiNET.ImGui.SmallButton("Markers...##dmap_markers"))
        {
            _markersOpen = !_markersOpen;
            if (_markersOpen)
                ImGuiNET.ImGui.SetNextWindowPos(ImGuiNET.ImGui.GetItemRectMin() + new Vector2(0, ImGuiNET.ImGui.GetItemRectSize().Y + 4), ImGuiCond.Always);
        }
        ImGuiNET.ImGui.SetItemTooltip("Marker sizes, and which names to draw beside the markers and how big.");

        // Route / Edit route.
        SameLineIfFits(160, limit, 12);
        if (ImGuiNET.ImGui.Checkbox("Route##dmap_route", ref DungeonMapSettingsStore.ShowRoute))
        {
            changed = true;
            if (!DungeonMapSettingsStore.ShowRoute) StopEditing();
        }
        ImGuiNET.ImGui.SetItemTooltip("RynthAi's nav route: legs (dashed after a recall or portal), points (numbered when zoomed in), the next waypoint ringed. Hover a point for its details.");
        ImGuiNET.ImGui.SameLine();
        ImGuiNET.ImGui.BeginDisabled(!DungeonMapSettingsStore.ShowRoute);
        bool edit = _editRoute;
        if (ImGuiNET.ImGui.Checkbox("Edit route##dmap_editroute", ref edit))
        {
            if (edit) TryStartEditing();
            else StopEditing();
        }
        ImGuiNET.ImGui.EndDisabled();
        ImGuiNET.ImGui.SetItemTooltip("Click a floor to add a point (after the selected point, else at the end). Click a point to select it; drag a point to move it (Esc cancels); right-click a point to delete it, insert after it or make it the next waypoint. Dragging empty space still pans.");

        // Opacity (of the map's background).
        SameLineIfFits(130, limit, 12);
        ImGuiNET.ImGui.AlignTextToFramePadding();
        ImGuiNET.ImGui.TextUnformatted("Opacity:");
        ImGuiNET.ImGui.SameLine(0, 4);
        ImGuiNET.ImGui.SetNextItemWidth(70);
        if (ImGuiNET.ImGui.SliderFloat("##dmap_opacity", ref DungeonMapSettingsStore.BgOpacity, 0f, 1f, "%.2f"))
            DungeonMapSettingsStore.BgOpacity = Math.Clamp(DungeonMapSettingsStore.BgOpacity, 0f, 1f);
        // Save once the drag ends, not on every step.
        if (ImGuiNET.ImGui.IsItemDeactivatedAfterEdit()) changed = true;

        ImGuiNET.ImGui.PopStyleVar(2);
        float bottom = ImGuiNET.ImGui.GetItemRectMax().Y + pad;
        dl.ChannelsSetCurrent(0);
        dl.AddRectFilled(origin, new Vector2(origin.X + width, bottom), C(ToolbarBgArgb));
        dl.ChannelsMerge();

        if (changed) DungeonMapSettingsStore.Save();
        return bottom - origin.Y;
    }

    private static readonly List<string> FloorLabels = new();

    private static string FloorLabel(int i)
    {
        while (FloorLabels.Count <= i) FloorLabels.Add("F" + (FloorLabels.Count + 1) + "##dmap_floor" + FloorLabels.Count);
        return FloorLabels[i];
    }

    /// <summary>Keeps the next item on this row when it fits before <paramref name="limit"/>, else starts a new row.</summary>
    private static void SameLineIfFits(float nextWidth, float limit, float gap = 8)
    {
        if (ImGuiNET.ImGui.GetItemRectMax().X + gap + nextWidth <= limit)
            ImGuiNET.ImGui.SameLine(0, gap);
    }

    private static bool ToggleButton(string label, bool on, uint onColour)
    {
        if (on)
        {
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Button, onColour);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ButtonHovered, onColour);
        }
        bool clicked = ImGuiNET.ImGui.SmallButton(label);
        if (on) ImGuiNET.ImGui.PopStyleColor(2);
        return clicked;
    }

    private static bool IconButton(string id, Vector2 pos, string glyph)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        var size = new Vector2(18, 18);
        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGuiNET.ImGui.InvisibleButton(id, size);
        uint bg = ImGuiNET.ImGui.IsItemActive() ? C(0xE0101820) : ImGuiNET.ImGui.IsItemHovered() ? C(0xE0243444) : ButtonBg;
        dl.AddRectFilled(pos, pos + size, bg, 3);
        dl.AddRect(pos, pos + size, ButtonBorder, 3);
        PhosphorIcons.DrawCentered(dl, ImGuiFonts.Get(UiFont.Ui11), glyph, pos, size, C(0xFFF2F8FF));
        return clicked;
    }

    private static void Message(ImDrawListPtr dl, Vector2 origin, Vector2 size, string text)
    {
        dl.AddRectFilled(origin, origin + size, MulAlpha(CanvasBgArgb, Math.Clamp(DungeonMapSettingsStore.BgOpacity, 0.6f, 1f)));
        DrawCentredText(dl, origin + size * 0.5f, Hint, text);
    }

    private static void DrawCentredText(ImDrawListPtr dl, Vector2 centre, uint col, string text)
    {
        Vector2 ts = ImGuiNET.ImGui.CalcTextSize(text);
        dl.AddText(centre - ts * 0.5f, col, text);
    }
}

/// <summary>
/// The Dungeon Map's settings (the plugin map's MapShowDoors / MapShowCreatures /
/// MapShowToolbar / MapBgOpacity / MapRotateWithPlayer, same defaults; showCorpses and
/// showRoute added 2026-10-12, both on by default). Engine-wide,
/// in %LOCALAPPDATA%\RynthCore\dungeon_map_settings.txt, like radar_settings.txt.
/// </summary>
internal static class DungeonMapSettingsStore
{
    private static readonly object _sync = new();
    private static bool _loaded;

    public static bool ShowDoors = true;
    public static bool ShowCreatures = true;
    public static bool ShowCorpses = true;
    public static bool ShowRoute = true;
    public static bool ShowToolbar = true;
    public static float BgOpacity = 1.0f;
    public static bool RotateWithPlayer;

    /// <summary>
    /// Per-kind marker size and name label (added 2026-10-15). The map has always named everything
    /// but items on the ground, so names start on for every other kind.
    /// </summary>
    public static readonly MarkerStyleSettings Markers = new(new[]
    {
        true, true, true, true, true, true, true, true, true, true, true,
        false, // item on the ground
    });

    private static string FilePath
    {
        get
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(root, "RynthCore", "dungeon_map_settings.txt");
        }
    }

    public static void Load()
    {
        lock (_sync)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                string path = FilePath;
                if (!File.Exists(path)) return;
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith('#')) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line[..eq].Trim();
                    string val = line[(eq + 1)..].Trim();
                    switch (key)
                    {
                        case "showDoors":     ShowDoors = val == "1"; break;
                        case "showCreatures": ShowCreatures = val == "1"; break;
                        case "showCorpses":   ShowCorpses = val == "1"; break;
                        case "showRoute":     ShowRoute = val == "1"; break;
                        case "showToolbar":   ShowToolbar = val == "1"; break;
                        case "rotate":        RotateWithPlayer = val == "1"; break;
                        case "opacity":
                            if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float o))
                                BgOpacity = Math.Clamp(o, 0f, 1f);
                            break;
                        default: Markers.TryLoad(key, val); break;
                    }
                }
            }
            catch { /* best-effort: defaults already in place */ }
        }
    }

    /// <summary>Writes the settings in the background (the caller is AC's render thread).</summary>
    public static void Save()
    {
        // Snapshot on the caller's thread: the writer runs later.
        string text = "# RynthCore dungeon map settings - auto-generated, hand-edits OK.\n"
            + $"showDoors={(ShowDoors ? "1" : "0")}\n"
            + $"showCreatures={(ShowCreatures ? "1" : "0")}\n"
            + $"showCorpses={(ShowCorpses ? "1" : "0")}\n"
            + $"showRoute={(ShowRoute ? "1" : "0")}\n"
            + $"showToolbar={(ShowToolbar ? "1" : "0")}\n"
            + $"rotate={(RotateWithPlayer ? "1" : "0")}\n"
            + FormattableString.Invariant($"opacity={BgOpacity:0.##}\n");
        var styleLines = new List<string>();
        Markers.AppendLines(styleLines);
        foreach (string line in styleLines) text += line + "\n";
        UiBackgroundWriter.Enqueue("dungeon map settings", () =>
        {
            lock (_sync)
            {
                try
                {
                    string path = FilePath;
                    string? dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(path, text);
                }
                catch { /* non-fatal */ }
            }
        });
    }
}
