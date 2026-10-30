// ============================================================================
//  RynthCore.Engine - ImGui/Panels/WorldMapView.cs
//  The map itself, drawn into a rectangle by the World Map face (full: toolbar, find box) and the
//  Mini Map face (a small follow-you view). One class so both look and behave the same.
//
//  The picture is NavData\worldmap.rmap (WorldMapTiles; RynthNav.Baker --map --map-tiles renders it
//  from client_cell_1.dat): a pyramid of 256 px tiles, so zooming in loads sharper tiles under the
//  view and the coarser ones already loaded show meanwhile.
//
//  On it (bottom to top): the tiles; a coordinate grid with edge labels; RynthNav's route (legs
//  between its steps, a dashed jump for a portal / recall / Town Network step, the current step
//  ringed) and the walking path it worked out; the Atlas's places (each kind from its own zoom,
//  labels from a closer one, none that would overlap one already drawn); your other characters
//  (RynthNet); a search highlight; the arrow's target; you (an arrow while RynthAi's radar feed
//  gives a heading, a dot from RynthNav alone, dimmed at the last outdoor spot while indoors).
//
//  Rotate turns the map so the way you face is up (the arrow points up, the compass shows north).
//  Drag pans (and turns Follow off), the wheel zooms about the cursor. A click (full) or a right
//  click (both) on the map or a place opens a menu: Go here, Route here, Arrow here, Copy
//  coordinates (RynthNav's commands, RynthNavCommands).
//
//  Coordinates: a "cell" is 24 yards, 10 per map degree: cell = degrees * 10 + 1019.5 on each axis
//  (the same numbers as NavCoords). The map works in cell units; the picture is cells * PixelsPerCell.
//  Screen = centre + R(phi) * (east, -north) * scale, phi = heading - 90 degrees when rotating.
//
//  Data (subscribed by the faces while open): UiSources.RynthNav (position, route, arrow, path),
//  UiSources.Radar (heading), UiSources.Characters (other characters, when that layer is on).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.UI.Data;
using static RynthCore.Engine.ImGuiBackend.Panels.FaceKit;

namespace RynthCore.Engine.ImGuiBackend.Panels;

/// <summary>Where you are, for the toolbar and the view: this frame's reading, with the last outdoor spot kept.</summary>
internal readonly struct MapPlayer
{
    public MapPlayer(bool have, double ns, double ew, float heading, bool indoors, double lastNs, double lastEw)
    { Have = have; Ns = ns; Ew = ew; Heading = heading; Indoors = indoors; LastNs = lastNs; LastEw = lastEw; }
    /// <summary>An outdoor position this frame.</summary>
    public readonly bool Have;
    public readonly double Ns, Ew;
    public readonly float Heading;            // degrees, 0 = east, 90 = north; NaN when only RynthNav's pose is known
    public readonly bool Indoors;
    public readonly double LastNs, LastEw;     // the last outdoor spot (NaN: never outdoors this session)
    public bool Known => Have || !double.IsNaN(LastNs);
    public double ShowNs => Have ? Ns : LastNs;
    public double ShowEw => Have ? Ew : LastEw;
}

internal sealed class WorldMapView
{
    public enum Kind { Town, Landmark, Dungeon, Lifestone, Bindstone, Portal, Vendor, Npc, Count }
    public static readonly string[] KindNames = { "Towns", "Landmarks", "Dungeons", "Lifestones", "Bindstones", "Portals", "Vendors", "NPCs" };

    // ── Constants ────────────────────────────────────────────────────────
    public const double Origin = 1019.5;                      // cell = degrees * 10 + Origin
    public const float MinScale = 0.12f, MaxScale = 14f, AbsMaxScale = 160f;      // screen px per cell
    /// <summary>How far in the open map is worth zooming: a sharper map (more pixels per cell) allows more, up to 1.25x its own detail.</summary>
    private static float MaxScaleNow => WorldMapTiles.IsOpen ? Math.Clamp(WorldMapTiles.PixelsPerCell * 1.25f, MaxScale, AbsMaxScale) : MaxScale;
    private static readonly float[] ShowFrom = { 0f, 1.2f, 1.0f, 2.0f, 2.0f, 3.0f, 5.0f, 6.0f };
    private static readonly float[] LabelFrom = { 0.45f, 3f, 2.5f, 5f, 5f, 6f, 8f, 10f };
    private static readonly uint OceanBg = RynthTheme.Argb(0xFF1C4276);
    private static readonly uint[] KindColour =
    {
        RynthTheme.Argb(0xFFFFD24D), RynthTheme.Argb(0xFFFFFFFF), RynthTheme.Argb(0xFFC77DFF), RynthTheme.Argb(0xFF5CE1E6),
        RynthTheme.Argb(0xFF3DDC97), RynthTheme.Argb(0xFFFF6BD6), RynthTheme.Argb(0xFFFF9F43), RynthTheme.Argb(0xFFF2F2A0),
    };
    private static readonly uint LabelCol = RynthTheme.Argb(0xFFF4F7FA), LabelHalo = RynthTheme.Argb(0xE0000000),
        RouteCol = RynthTheme.Argb(0xFFFFA733), RouteActive = RynthTheme.Argb(0xFFFFF04D), PathCol = RynthTheme.Argb(0xFF5AD2FF),
        TargetCol = RynthTheme.Argb(0xFF4DD2FF), PlayerFill = RynthTheme.Argb(0xFF33FF66), PlayerEdge = RynthTheme.Argb(0xFF0A2A12),
        CharCol = RynthTheme.Argb(0xFF6EA8FF), HintCol = RynthTheme.Argb(0xFF9FB3C8), BarBg = RynthTheme.Argb(0xE00A121A),
        RoadCol = RynthTheme.Argb(0xFFE6CD78), RoadHalo = RynthTheme.Argb(0xB0301C08), BuildFill = RynthTheme.Argb(0xFF9A4B40),
        BuildEdge = RynthTheme.Argb(0xFF2A1410), GridLine = RynthTheme.Argb(0x30FFFFFF), GridLabel = RynthTheme.Argb(0xC0DDE8F4), HighlightCol = RynthTheme.Argb(0xFFFFFFFF);

    // ── Places (shared by every view; rebuilt when the Atlas reloads) ────
    private readonly struct Mark
    {
        public Mark(float cx, float cy, string name, Kind kind, NavPlace place) { Cx = cx; Cy = cy; Name = name; Kind = kind; Place = place; }
        public readonly float Cx, Cy;
        public readonly string Name;
        public readonly Kind Kind;
        public readonly NavPlace Place;
    }
    private static Mark[]? _marks;
    private static long _marksVersion = -1;
    private static double _lastNs = double.NaN, _lastEw = double.NaN;   // the last outdoor spot, shared by both views

    public static double CellOf(double degrees) => degrees * 10.0 + Origin;
    public static double DegreesOf(double cell) => (cell - Origin) / 10.0;

    // ── This view ────────────────────────────────────────────────────────
    public readonly bool Mini;
    private readonly WorldMapSettings.ViewState _st;
    private double _ccx = 1019.5, _ccy = 1019.5;                // map cell at the canvas centre
    private bool _fitNext, _centreNext;
    private Vector2 _centre;
    private float _phi;                                        // the map's rotation this frame (radians)
    private float _scale;
    private MapProjection Proj => new(_centre, _ccx, _ccy, _scale, _phi);

    public float Scale => _scale;
    public bool Follow { get => _st.Follow; set { if (_st.Follow != value) { _st.Follow = value; WorldMapSettings.MarkDirty(); } } }
    public bool Rotate { get => _st.Rotate; set { if (_st.Rotate != value) { _st.Rotate = value; WorldMapSettings.MarkDirty(); } } }
    /// <summary>The line the toolbar shows for the point under the cursor ("" when none).</summary>
    public string HoverText { get; private set; } = "";
    /// <summary>A screen rectangle the face has covered (a dropdown): the map ignores the mouse inside it.</summary>
    public (Vector2 Min, Vector2 Max)? Blocked;

    // Interaction.
    private bool _pressing, _pressMoved;
    private int _hoverMark = -1, _hoverChar = -1;
    private double _ctxNs, _ctxEw;
    private string _ctxName = "";
    private bool _openCtx;
    private readonly string _ctxId;
    private string _hiName = "";
    private double _hiNs, _hiEw;
    private long _hiUntilMs;
    private readonly List<(Vector2 Min, Vector2 Max)> _labelRects = new(128);
    private readonly List<(Vector2 Pos, int Index)> _drawn = new(256);
    private readonly List<(Vector2 Pos, int Index)> _drawnChars = new(16);
    private CharacterRow[] _chars = Array.Empty<CharacterRow>();

    public WorldMapView(bool mini)
    {
        Mini = mini;
        _st = mini ? WorldMapSettings.Mini : WorldMapSettings.Full;
        _scale = Math.Clamp(_st.Scale, MinScale, AbsMaxScale);
        _ctxId = mini ? "##mm_ctx" : "##wm_ctx";
    }

    // ── Commands from the face ───────────────────────────────────────────
    public void FitWorld() { _fitNext = true; }

    public void ZoomBy(float k, Vector2? about = null)
    {
        float old = _scale;
        float next = Math.Clamp(_scale * k, MinScale, MaxScaleNow);
        if (next == old) return;
        if (about is Vector2 m && !_st.Follow)
            (_ccx, _ccy) = Proj.CentreForZoomAbout(m, next);
        _scale = next;
        _st.Scale = _scale;
        WorldMapSettings.MarkDirty();
    }

    /// <summary>Centres on a map point (degrees), zooming in to at least <paramref name="minScale"/>, and rings it for a few seconds.</summary>
    public void Show(string name, double ns, double ew, float minScale)
    {
        Follow = false;
        _ccx = CellOf(ew); _ccy = CellOf(ns);
        if (_scale < minScale) { _scale = Math.Min(minScale, MaxScaleNow); _st.Scale = _scale; WorldMapSettings.MarkDirty(); }
        _hiName = name; _hiNs = ns; _hiEw = ew;
        _hiUntilMs = Environment.TickCount64 + 6000;
    }

    // ── Projection (MapProjection) ───────────────────────────────────────
    private Vector2 Project(double cx, double cy) => Proj.Project(cx, cy);
    private Vector2 ProjectDeg(double ns, double ew) => Proj.Project(CellOf(ew), CellOf(ns));
    private void Unproject(Vector2 p, out double cx, out double cy) => Proj.Unproject(p, out cx, out cy);
    private Vector2 ScreenDir(float east, float north) => Proj.ScreenDir(east, north);

    // ── Player ───────────────────────────────────────────────────────────
    /// <summary>Reads where you are: the radar feed (heading), else RynthNav's pose. Indoors keeps the last outdoor spot.</summary>
    public static MapPlayer GatherPlayer(RynthNavStatus? st, RadarView? radar)
    {
        double ns = double.NaN, ew = double.NaN;
        float heading = float.NaN;
        bool indoors = false;
        if (radar != null)
        {
            indoors = radar.Live.IsIndoor;
            if (!indoors && !double.IsNaN(radar.Live.Ns) && !double.IsNaN(radar.Live.Ew)) { ns = radar.Live.Ns; ew = radar.Live.Ew; heading = radar.Live.Player.Heading; }
        }
        if (double.IsNaN(ns) && st != null && st.HasPose)
        {
            indoors = indoors || st.Indoors;
            if (!st.Indoors) { ns = st.NS; ew = st.EW; }
        }
        if (!double.IsNaN(ns)) { _lastNs = ns; _lastEw = ew; }
        return new MapPlayer(!double.IsNaN(ns), ns, ew, heading, indoors, _lastNs, _lastEw);
    }

    // ── The frame ────────────────────────────────────────────────────────
    /// <summary>Draws the map into the canvas and handles its input. AC's render thread, inside a face's Draw.</summary>
    public void Draw(Vector2 canvasMin, Vector2 canvasMax, in MapPlayer pl, RynthNavStatus? st, CharacterRow[]? chars)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        Vector2 csize = canvasMax - canvasMin;
        if (csize.X < 40 || csize.Y < 40) return;
        _centre = (canvasMin + canvasMax) * 0.5f;
        _chars = chars ?? Array.Empty<CharacterRow>();

        if (_fitNext)
        {
            _fitNext = false;
            Follow = false; Rotate = false;
            _scale = Math.Clamp(Math.Min(csize.X, csize.Y) / (float)WorldMapTiles.CellsHigh, MinScale, MaxScale);
            _st.Scale = _scale;
            _ccx = WorldMapTiles.CellsWide * 0.5; _ccy = WorldMapTiles.CellsHigh * 0.5;
            WorldMapSettings.MarkDirty();
        }
        if (_scale > MaxScaleNow) { _scale = MaxScaleNow; _st.Scale = _scale; }
        _phi = _st.Rotate && !float.IsNaN(pl.Heading) && pl.Have ? MapProjection.PhiForHeading(pl.Heading) : 0f;
        if (_st.Follow && pl.Known) { _ccx = CellOf(pl.ShowEw); _ccy = CellOf(pl.ShowNs); }

        dl.AddRectFilled(canvasMin, canvasMax, OceanBg);
        dl.PushClipRect(canvasMin, canvasMax, true);
        DrawTiles(dl, canvasMin, canvasMax);
        _labelRects.Clear(); _drawn.Clear(); _drawnChars.Clear();
        if (WorldMapSettings.Grid) DrawGrid(dl, canvasMin, canvasMax, _phi != 0f);
        if (WorldMapSettings.Buildings) DrawVectors(dl, canvasMin, canvasMax);
        if (WorldMapSettings.Route) DrawRoute(dl, st, pl);
        DrawMarkers(dl, canvasMin, canvasMax);
        if (WorldMapSettings.Characters) DrawCharacters(dl, canvasMin, canvasMax);
        DrawHighlight(dl);
        DrawTarget(dl, st);
        DrawPlayer(dl, pl);
        dl.PopClipRect();
        dl.AddRect(canvasMin, canvasMax, BtnBord);
        DrawScaleBar(dl, canvasMin, canvasMax);
        DrawCompass(dl, canvasMax);

        if (!WorldMapTiles.IsOpen || WorldMapTiles.Levels.Count == 0)
            DrawCentred(dl, _centre, WorldMapTiles.Status.Length > 0 ? WorldMapTiles.Status : "Loading the map...");
        else if (pl.Indoors && !pl.Have && pl.Known && _st.Follow)
            DrawCentred(dl, new Vector2(_centre.X, canvasMin.Y + 20), "Indoors: the map shows where you went in.");
        else if (!pl.Known && _st.Follow)
            DrawCentred(dl, new Vector2(_centre.X, canvasMin.Y + 20), "Waiting for your position...");

        Input(canvasMin, canvasMax, pl);
        DrawContextMenu();
    }

    // ── Tiles (WorldMapTileDrawer, shared with the Radar's world map backdrop) ──
    private readonly WorldMapTileDrawer _tiles = new();

    private void DrawTiles(ImDrawListPtr dl, Vector2 canvasMin, Vector2 canvasMax) =>
        _tiles.Draw(dl, canvasMin, canvasMax, _centre, _ccx, _ccy, _scale, _phi);

    // ── Grid ─────────────────────────────────────────────────────────────
    private void DrawGrid(ImDrawListPtr dl, Vector2 canvasMin, Vector2 canvasMax, bool turned)
    {
        float pxPerDeg = 10f * _scale;
        float[] steps = { 50f, 20f, 10f, 5f, 2f, 1f, 0.5f, 0.2f, 0.1f, 0.05f };
        float step = steps[0];
        foreach (float s in steps) if (s * pxPerDeg >= 90f) step = s;
        if (step * pxPerDeg < 40f) return;
        double halfW = (canvasMax.X - canvasMin.X) * 0.5, halfH = (canvasMax.Y - canvasMin.Y) * 0.5;
        double r = Math.Sqrt(halfW * halfW + halfH * halfH) / _scale;        // cells, the circle around the canvas
        double nsMin = DegreesOf(_ccy - r), nsMax = DegreesOf(_ccy + r), ewMin = DegreesOf(_ccx - r), ewMax = DegreesOf(_ccx + r);
        string fmt = step >= 1f ? "0" : step >= 0.1f ? "0.0" : "0.00";
        int lines = 0;
        for (double ns = Math.Ceiling(nsMin / step) * step; ns <= nsMax && lines < 60; ns += step, lines++)
        {
            Vector2 a = ProjectDeg(ns, ewMin), b = ProjectDeg(ns, ewMax);
            dl.AddLine(a, b, GridLine, 1f);
            if (!turned) EdgeLabel(dl, new Vector2(canvasMin.X + 4, a.Y - 14), canvasMin, canvasMax, Fmt(ns, fmt, 'N', 'S'));
        }
        for (double ew = Math.Ceiling(ewMin / step) * step; ew <= ewMax && lines < 120; ew += step, lines++)
        {
            Vector2 a = ProjectDeg(nsMin, ew), b = ProjectDeg(nsMax, ew);
            dl.AddLine(a, b, GridLine, 1f);
            if (!turned) EdgeLabel(dl, new Vector2(a.X + 3, canvasMin.Y + 3), canvasMin, canvasMax, Fmt(ew, fmt, 'E', 'W'));
        }
    }

    private static string Fmt(double v, string fmt, char pos, char neg) =>
        Math.Abs(v).ToString(fmt, CultureInfo.InvariantCulture) + (v >= 0 ? pos : neg);

    private static void EdgeLabel(ImDrawListPtr dl, Vector2 at, Vector2 min, Vector2 max, string text)
    {
        if (at.X < min.X || at.Y < min.Y || at.X > max.X - 30 || at.Y > max.Y - 14) return;
        dl.AddText(at + new Vector2(1, 1), LabelHalo, text);
        dl.AddText(at, GridLabel, text);
    }

    // ── Roads and buildings (WorldMapVectors) ────────────────────────────
    private const float RoadsFrom = 1.5f, BuildingsFrom = 3f;
    private readonly Vector2[] _poly = new Vector2[16];

    private void DrawVectors(ImDrawListPtr dl, Vector2 canvasMin, Vector2 canvasMax)
    {
        if (_scale < RoadsFrom) return;
        if (WorldMapTiles.IsOpen && WorldMapTiles.PixelsPerCell >= 8) return;      // HD and up have the roads and the real roofs in the picture
        WorldMapVectors.Data? v = WorldMapVectors.Current;
        if (v == null) return;
        // The map area in view, in cells (the circle around the canvas when turned).
        double halfW = (canvasMax.X - canvasMin.X) * 0.5, halfH = (canvasMax.Y - canvasMin.Y) * 0.5;
        if (_phi != 0f) halfW = halfH = Math.Sqrt(halfW * halfW + halfH * halfH);
        float xMin = (float)(_ccx - halfW / _scale) - 1, xMax = (float)(_ccx + halfW / _scale) + 1;
        float yMin = (float)(_ccy - halfH / _scale) - 1, yMax = (float)(_ccy + halfH / _scale) + 1;

        // Roads: a dark under-stroke and the road colour over it; wider as you zoom in.
        float w = Math.Clamp(_scale * 0.4f, 1.5f, 6f);
        ushort[] roads = v.Roads;
        int visible = 0;
        for (int i = 0; i + 3 < roads.Length && visible < 6000; i += 4)
        {
            float x0 = roads[i], y0 = roads[i + 1], x1 = roads[i + 2], y1 = roads[i + 3];
            if ((x0 < xMin && x1 < xMin) || (x0 > xMax && x1 > xMax) || (y0 < yMin && y1 < yMin) || (y0 > yMax && y1 > yMax)) continue;
            visible++;
            Vector2 a = Project(x0, y0), b = Project(x1, y1);
            dl.AddLine(a, b, RoadHalo, w + 1.5f);
        }
        visible = 0;
        for (int i = 0; i + 3 < roads.Length && visible < 6000; i += 4)
        {
            float x0 = roads[i], y0 = roads[i + 1], x1 = roads[i + 2], y1 = roads[i + 3];
            if ((x0 < xMin && x1 < xMin) || (x0 > xMax && x1 > xMax) || (y0 < yMin && y1 < yMin) || (y0 > yMax && y1 > yMax)) continue;
            visible++;
            dl.AddLine(Project(x0, y0), Project(x1, y1), RoadCol, w);
        }

        if (_scale < BuildingsFrom) return;
        for (int i = 0; i < v.Buildings.Length; i++)
        {
            Vector4 bb = v.BuildingBounds[i];
            if (bb.Z < xMin || bb.X > xMax || bb.W < yMin || bb.Y > yMax) continue;
            Vector2[] pts = v.Buildings[i];
            int n = Math.Min(pts.Length, _poly.Length);
            if (n < 3) continue;
            float area = 0;
            for (int k = 0; k < n; k++)
            {
                Vector2 p = Project(pts[k].X, pts[k].Y);
                _poly[k] = p;
            }
            for (int k = 0; k < n; k++) { Vector2 a = _poly[k], b = _poly[(k + 1) % n]; area += a.X * b.Y - b.X * a.Y; }
            if (area < 0) Array.Reverse(_poly, 0, n);          // ImGui wants one winding; the flip of y makes it vary
            dl.AddConvexPolyFilled(ref _poly[0], n, BuildFill);
            for (int k = 0; k < n; k++) dl.AddLine(_poly[k], _poly[(k + 1) % n], BuildEdge, 1.2f);
        }
    }

    // ── Places ───────────────────────────────────────────────────────────
    private static void EnsureMarks()
    {
        long v = RynthNavAtlas.Version;
        if (_marks != null && v == _marksVersion) return;
        var list = new List<Mark>(8192);
        foreach (NavPlace p in RynthNavAtlas.Places)
        {
            if (!p.OnMap || double.IsNaN(p.Ns) || double.IsNaN(p.Ew)) continue;
            Kind k;
            switch (p.Type)
            {
                case "Town": k = Kind.Town; break;
                case "Landmark": k = Kind.Landmark; break;
                case "Dungeon": k = Kind.Dungeon; break;
                case "Lifestone": k = Kind.Lifestone; break;
                case "Bindstone": k = Kind.Bindstone; break;
                case "Portal": k = Kind.Portal; break;
                case "Vendor": k = Kind.Vendor; break;
                case "NPC": k = Kind.Npc; break;
                default: continue;
            }
            list.Add(new Mark((float)CellOf(p.Ew), (float)CellOf(p.Ns), p.Name, k, p));
        }
        _marks = list.ToArray();
        _marksVersion = v;
    }

    /// <summary>The Atlas's surface places whose name has <paramref name="query"/> in it, towns and dungeons first. Any thread.</summary>
    public static List<NavPlace> Find(string query, int max)
    {
        var res = new List<(int Rank, NavPlace Place)>();
        if (query.Length == 0) return new List<NavPlace>();
        foreach (NavPlace p in RynthNavAtlas.Places)
        {
            if (!p.OnMap || p.Name.Length == 0) continue;
            int at = p.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            int kind = p.Type switch { "Town" => 0, "Dungeon" => 1, "Landmark" => 2, "Lifestone" => 3, "Bindstone" => 3, "Portal" => 4, _ => 5 };
            res.Add((kind * 1000 + (at == 0 ? 0 : 400) + Math.Min(p.Name.Length, 399), p));
        }
        res.Sort(static (a, b) => a.Rank.CompareTo(b.Rank));
        var list = new List<NavPlace>(Math.Min(max, res.Count));
        for (int i = 0; i < res.Count && list.Count < max; i++) list.Add(res[i].Place);
        return list;
    }

    private void DrawMarkers(ImDrawListPtr dl, Vector2 canvasMin, Vector2 canvasMax)
    {
        EnsureMarks();
        Mark[]? marks = _marks;
        if (marks == null) return;
        float s = _scale;
        // Towns first (they win a label overlap), then the rest in kind order.
        for (int pass = 0; pass < (int)Kind.Count; pass++)
        {
            if (!WorldMapSettings.Show[pass] || s < ShowFrom[pass]) continue;
            uint col = KindColour[pass];
            float rad = pass == (int)Kind.Town ? 5f : pass is (int)Kind.Npc or (int)Kind.Vendor ? 2.5f : 3.5f;
            bool label = WorldMapSettings.Labels && s >= LabelFrom[pass];
            for (int i = 0; i < marks.Length; i++)
            {
                ref readonly Mark m = ref marks[i];
                if ((int)m.Kind != pass) continue;
                Vector2 pt = Project(m.Cx, m.Cy);
                if (pt.X < canvasMin.X - 40 || pt.X > canvasMax.X + 40 || pt.Y < canvasMin.Y - 12 || pt.Y > canvasMax.Y + 12) continue;
                _drawn.Add((pt, i));
                bool hot = i == _hoverMark;
                DrawMarkShape(dl, pt, m.Kind, col, hot ? rad + 1.5f : rad);
                if (label || hot) DrawLabel(dl, pt, m.Name, rad, hot);
            }
        }
    }

    private static void DrawMarkShape(ImDrawListPtr dl, Vector2 p, Kind k, uint col, float r)
    {
        switch (k)
        {
            case Kind.Dungeon:
                dl.AddQuadFilled(p + new Vector2(0, -r - 1), p + new Vector2(r + 1, 0), p + new Vector2(0, r + 1), p + new Vector2(-r - 1, 0), col);
                dl.AddQuad(p + new Vector2(0, -r - 1), p + new Vector2(r + 1, 0), p + new Vector2(0, r + 1), p + new Vector2(-r - 1, 0), LabelHalo, 1f);
                break;
            case Kind.Town:
                dl.AddCircleFilled(p, r + 1, LabelHalo);
                dl.AddCircleFilled(p, r, col);
                dl.AddCircleFilled(p, r - 2.5f, LabelHalo);
                break;
            default:
                dl.AddCircleFilled(p, r + 1, LabelHalo);
                dl.AddCircleFilled(p, r, col);
                break;
        }
    }

    private void DrawLabel(ImDrawListPtr dl, Vector2 pt, string text, float rad, bool force, uint? colour = null)
    {
        if (text.Length == 0) return;
        Vector2 ts = ImGuiNET.ImGui.CalcTextSize(text);
        var min = new Vector2(pt.X + rad + 4, pt.Y - ts.Y * 0.5f);
        var max = min + ts;
        if (!force)
        {
            for (int i = 0; i < _labelRects.Count; i++)
            {
                var (a, b) = _labelRects[i];
                if (min.X < b.X + 2 && max.X > a.X - 2 && min.Y < b.Y && max.Y > a.Y) return;
            }
        }
        _labelRects.Add((min, max));
        dl.AddText(min + new Vector2(1, 1), LabelHalo, text);
        dl.AddText(min + new Vector2(-1, 1), LabelHalo, text);
        dl.AddText(min, force ? Text : colour ?? LabelCol, text);
    }

    // ── Other characters (RynthNet) ──────────────────────────────────────
    private void DrawCharacters(ImDrawListPtr dl, Vector2 canvasMin, Vector2 canvasMax)
    {
        for (int i = 0; i < _chars.Length; i++)
        {
            CharacterRow c = _chars[i];
            if (c.IsSelf || c.LoggedOut || !c.HasMap) continue;
            Vector2 p = ProjectDeg(c.MapNs, c.MapEw);
            if (p.X < canvasMin.X - 60 || p.X > canvasMax.X + 60 || p.Y < canvasMin.Y - 12 || p.Y > canvasMax.Y + 12) continue;
            _drawnChars.Add((p, i));
            uint col = c.Stale ? (CharCol & 0x00FFFFFF) | 0x80000000u : CharCol;
            var tri = new[] { p + new Vector2(0, -6), p + new Vector2(5.5f, 4), p + new Vector2(-5.5f, 4) };
            dl.AddTriangleFilled(tri[0], tri[1], tri[2], col);
            dl.AddTriangle(tri[0], tri[1], tri[2], LabelHalo, 1.5f);
            DrawLabel(dl, p, c.Name, 6f, i == _hoverChar, CharCol);
        }
    }

    // ── Route, path, target, highlight, player ───────────────────────────
    private void DrawRoute(ImDrawListPtr dl, RynthNavStatus? st, in MapPlayer pl)
    {
        if (st == null) return;
        bool haveFrom = pl.Known;

        // The walking path RynthNav worked out (when it sends one): a line under the route's dots, a segment per walk.
        bool havePath = WorldMapSettings.WalkPath && st.PathSegments.Length > 0;
        if (havePath)
        {
            foreach (float[] seg in st.PathSegments)
            {
                Vector2 prevP = ProjectDeg(seg[0], seg[1]);
                for (int i = 2; i + 1 < seg.Length; i += 2)
                {
                    Vector2 q = ProjectDeg(seg[i], seg[i + 1]);
                    dl.AddLine(prevP, q, LabelHalo, 4f);
                    dl.AddLine(prevP, q, PathCol, 2.5f);
                    prevP = q;
                }
            }
        }

        if (st.Route.Length > 0)
        {
            Vector2 prev = haveFrom ? ProjectDeg(pl.ShowNs, pl.ShowEw) : default;
            bool havePrev = haveFrom;
            for (int i = 0; i < st.Route.Length; i++)
            {
                RynthNavRouteStep step = st.Route[i];
                bool jump = step.Kind is "recall" or "portal" or "town";
                bool hasHere = step.Kind != "recall" && !(step.Ns == 0 && step.Ew == 0);
                uint col = i == st.RouteIdx ? RouteActive : RouteCol;
                if (hasHere)
                {
                    Vector2 p = ProjectDeg(step.Ns, step.Ew);
                    if (havePrev && !havePath) dl.AddLine(prev, p, col, 2f);     // straight legs only without a real path
                    DrawStepDot(dl, p, col, i == st.RouteIdx, jump);
                    prev = p; havePrev = true;
                }
                if (jump && step.HasLanding)
                {
                    Vector2 q = ProjectDeg(step.LandNs, step.LandEw);
                    if (havePrev) DashedLine(dl, prev, q, col);
                    DrawStepDot(dl, q, col, false, false);
                    prev = q; havePrev = true;
                }
            }
        }
    }

    private void DrawTarget(ImDrawListPtr dl, RynthNavStatus? st)
    {
        RynthNavArrow? ar = st?.Arrow;
        if (ar == null || !ar.OnMap) return;
        Vector2 t = ProjectDeg(ar.Ns, ar.Ew);
        dl.AddCircle(t, 8f, LabelHalo, 24, 4f);
        dl.AddCircle(t, 8f, TargetCol, 24, 2f);
        dl.AddCircleFilled(t, 2.5f, TargetCol);
    }

    private void DrawHighlight(ImDrawListPtr dl)
    {
        long left = _hiUntilMs - Environment.TickCount64;
        if (left <= 0) return;
        Vector2 p = ProjectDeg(_hiNs, _hiEw);
        float pulse = 10f + 5f * MathF.Sin(Environment.TickCount64 * 0.008f);
        uint a = (uint)Math.Clamp(left / 6000f * 255f * 1.5f, 0, 255);
        uint col = (HighlightCol & 0x00FFFFFF) | (a << 24);
        dl.AddCircle(p, pulse + 1.5f, LabelHalo & 0x00FFFFFF | (a << 24), 28, 5f);
        dl.AddCircle(p, pulse, col, 28, 2.5f);
        if (_hiName.Length > 0) DrawLabel(dl, p, _hiName, pulse, true);
    }

    private static void DrawStepDot(ImDrawListPtr dl, Vector2 p, uint col, bool current, bool jump)
    {
        dl.AddCircleFilled(p, 4.5f, LabelHalo);
        dl.AddCircleFilled(p, jump ? 3.5f : 3f, col);
        if (current) dl.AddCircle(p, 8f, col, 20, 2f);
    }

    private static void DashedLine(ImDrawListPtr dl, Vector2 a, Vector2 b, uint col)
    {
        Vector2 d = b - a;
        float len = d.Length();
        if (len < 1f || len > 20000f) return;
        Vector2 u = d / len;
        for (float t = 0; t < len; t += 12f)
            dl.AddLine(a + u * t, a + u * Math.Min(t + 6f, len), col, 2f);
    }

    private void DrawPlayer(ImDrawListPtr dl, in MapPlayer pl)
    {
        if (!pl.Known) return;
        Vector2 p = ProjectDeg(pl.ShowNs, pl.ShowEw);
        uint fill = pl.Have ? PlayerFill : (PlayerFill & 0x00FFFFFF) | 0x80000000u;
        if (pl.Have && !float.IsNaN(pl.Heading))
        {
            float h = pl.Heading * MathF.PI / 180f;             // AC: 0 = east, 90 = north
            Vector2 fwd = ScreenDir(MathF.Cos(h), MathF.Sin(h));
            var side = new Vector2(-fwd.Y, fwd.X);
            Vector2 tip = p + fwd * 10f, l = p - fwd * 6f + side * 6f, r = p - fwd * 6f - side * 6f, back = p - fwd * 2.5f;
            dl.AddTriangleFilled(tip, l, back, fill);
            dl.AddTriangleFilled(tip, back, r, fill);
            dl.AddTriangle(tip, l, r, PlayerEdge, 1.5f);
        }
        else
        {
            dl.AddCircleFilled(p, 6f, PlayerEdge);
            dl.AddCircleFilled(p, 4.5f, fill);
        }
        if (pl.Indoors && !pl.Have) dl.AddCircle(p, 10f, fill, 24, 1.5f);
    }

    // ── Furniture ────────────────────────────────────────────────────────
    private void DrawScaleBar(ImDrawListPtr dl, Vector2 canvasMin, Vector2 canvasMax)
    {
        // A cell is 24 yards: pick the roundest length under ~140 px (90 on the mini map).
        float limit = Mini ? 90f : 140f;
        float[] yards = { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 };
        float best = yards[0];
        foreach (float y in yards) if (y / 24f * _scale <= limit) best = y;
        float px = best / 24f * _scale;
        var a = new Vector2(canvasMin.X + 12, canvasMax.Y - 14);
        var b = new Vector2(a.X + px, a.Y);
        dl.AddLine(a + new Vector2(1, 1), b + new Vector2(1, 1), LabelHalo, 3f);
        dl.AddLine(a, b, LabelCol, 2f);
        dl.AddLine(a + new Vector2(0, -4), a + new Vector2(0, 2), LabelCol, 2f);
        dl.AddLine(b + new Vector2(0, -4), b + new Vector2(0, 2), LabelCol, 2f);
        string t = best >= 1000 ? (best / 1000).ToString("0.#", CultureInfo.InvariantCulture) + " k yd" : best.ToString("0", CultureInfo.InvariantCulture) + " yd";
        dl.AddText(a + new Vector2(1, -17), LabelHalo, t);
        dl.AddText(a + new Vector2(0, -18), LabelCol, t);
    }

    private void DrawCompass(ImDrawListPtr dl, Vector2 canvasMax)
    {
        // A needle at north: straight up while the map is north-up, turning as it rotates.
        var c = new Vector2(canvasMax.X - 20, canvasMax.Y - 26);
        Vector2 n = ScreenDir(0, 1);
        var side = new Vector2(-n.Y, n.X);
        dl.AddCircleFilled(c, 12, BarBg);
        dl.AddCircle(c, 12, BtnBord);
        dl.AddTriangleFilled(c + n * 9f, c - n * 1f + side * 3.5f, c - n * 1f - side * 3.5f, LabelCol);
        dl.AddTriangleFilled(c - n * 9f, c + n * 1f + side * 3.5f, c + n * 1f - side * 3.5f, RynthTheme.Argb(0xFF6C7C8C));
        Vector2 ts = ImGuiNET.ImGui.CalcTextSize("N");
        Vector2 lp = c + n * 17f - ts * 0.5f;
        if (lp.Y > canvasMax.Y - 40) lp = c - n * 17f - ts * 0.5f;
        dl.AddText(lp, LabelCol, "N");
    }

    private static void DrawCentred(ImDrawListPtr dl, Vector2 at, string text)
    {
        Vector2 ts = ImGuiNET.ImGui.CalcTextSize(text);
        Vector2 min = at - ts * 0.5f - new Vector2(8, 5), max = at + ts * 0.5f + new Vector2(8, 5);
        dl.AddRectFilled(min, max, BarBg, 4);
        dl.AddText(at - ts * 0.5f, HintCol, text);
    }

    // ── Input ────────────────────────────────────────────────────────────
    private void Input(Vector2 canvasMin, Vector2 canvasMax, in MapPlayer pl)
    {
        ImGuiIOPtr io = ImGuiNET.ImGui.GetIO();
        ImGuiNET.ImGui.SetCursorScreenPos(canvasMin);
        ImGuiNET.ImGui.InvisibleButton(Mini ? "##mm_canvas" : "##wm_canvas", Vector2.Max(canvasMax - canvasMin, new Vector2(1, 1)),
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        Vector2 mouse = io.MousePos;
        bool blocked = Blocked is { } b && mouse.X >= b.Min.X && mouse.X <= b.Max.X && mouse.Y >= b.Min.Y && mouse.Y <= b.Max.Y;
        bool hovered = ImGuiNET.ImGui.IsItemHovered() && !blocked;

        // Nearest drawn character or place within reach, else the map point under the cursor.
        _hoverMark = -1; _hoverChar = -1;
        if (hovered || _pressing)
        {
            float best = 11f * 11f;
            foreach (var (pos, idx) in _drawnChars)
            {
                float dd = Vector2.DistanceSquared(pos, mouse);
                if (dd < best) { best = dd; _hoverChar = idx; _hoverMark = -1; }
            }
            foreach (var (pos, idx) in _drawn)
            {
                float dd = Vector2.DistanceSquared(pos, mouse);
                if (dd < best) { best = dd; _hoverMark = idx; _hoverChar = -1; }
            }
        }
        HoverText = "";
        if (hovered)
        {
            Unproject(mouse, out double hcx, out double hcy);
            double hn = DegreesOf(hcy), he = DegreesOf(hcx);
            string where = RynthNavStatus.Coord(hn, he);
            string name = "";
            if (_hoverChar >= 0 && _hoverChar < _chars.Length) { name = _chars[_hoverChar].Name + " (character)"; hn = _chars[_hoverChar].MapNs; he = _chars[_hoverChar].MapEw; where = RynthNavStatus.Coord(hn, he); }
            else if (_hoverMark >= 0 && _marks != null && _hoverMark < _marks.Length) { Mark m = _marks[_hoverMark]; name = m.Name + " (" + m.Place.Type + ")"; hn = m.Place.Ns; he = m.Place.Ew; where = RynthNavStatus.Coord(hn, he); }
            HoverText = (name.Length > 0 ? name + "   " : "") + where + FromYou(pl, hn, he);
        }

        if (hovered && ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left)) { _pressing = true; _pressMoved = false; }
        if (hovered && ImGuiNET.ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && pl.Known) { Follow = true; _pressing = false; }
        if (_pressing && ImGuiNET.ImGui.IsMouseDown(ImGuiMouseButton.Left) && ImGuiNET.ImGui.IsMouseDragging(ImGuiMouseButton.Left, 4f))
        {
            _pressMoved = true;
            Vector2 d = io.MouseDelta;
            if (float.IsFinite(d.X) && float.IsFinite(d.Y) && d != Vector2.Zero)
            {
                Follow = false;
                (_ccx, _ccy) = Proj.CentreAfterDrag(d);
            }
        }
        if (_pressing && ImGuiNET.ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            if (!_pressMoved && hovered && !Mini) OpenMenuAt(mouse);
            _pressing = false;
        }
        if (!ImGuiNET.ImGui.IsMouseDown(ImGuiMouseButton.Left)) _pressing = false;
        if (hovered && ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Right)) OpenMenuAt(mouse);

        if (hovered)
        {
            float wheel = io.MouseWheel;
            if (float.IsFinite(wheel) && wheel != 0f) ZoomBy(MathF.Pow(1.25f, wheel), mouse);
        }
    }

    /// <summary>"   ·   1.2 k yd NNE" from you to a point, or "".</summary>
    private static string FromYou(in MapPlayer pl, double ns, double ew)
    {
        if (!pl.Known) return "";
        double dn = ns - pl.ShowNs, de = ew - pl.ShowEw;
        double yards = Math.Sqrt(dn * dn + de * de) * 240.0;
        if (yards < 5) return "   ·   here";
        double bearing = Math.Atan2(de, dn) * 180.0 / Math.PI;
        if (bearing < 0) bearing += 360.0;
        string dist = yards >= 1000 ? (yards / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " k yd" : ((int)yards).ToString(CultureInfo.InvariantCulture) + " yd";
        return "   ·   " + dist + " " + RynthNavAtlas.CompassPoint(bearing);
    }

    private void OpenMenuAt(Vector2 mouse)
    {
        if (_hoverChar >= 0 && _hoverChar < _chars.Length)
        {
            CharacterRow c = _chars[_hoverChar];
            _ctxNs = Math.Round(c.MapNs, 2); _ctxEw = Math.Round(c.MapEw, 2); _ctxName = c.Name;
        }
        else if (_hoverMark >= 0 && _marks != null && _hoverMark < _marks.Length)
        {
            Mark m = _marks[_hoverMark];
            _ctxNs = m.Place.Ns; _ctxEw = m.Place.Ew; _ctxName = m.Name;
        }
        else
        {
            Unproject(mouse, out double cx, out double cy);
            _ctxNs = Math.Round(DegreesOf(cy), 2);
            _ctxEw = Math.Round(DegreesOf(cx), 2);
            _ctxName = "";
        }
        _openCtx = true;
    }

    private void DrawContextMenu()
    {
        if (_openCtx) { ImGuiNET.ImGui.OpenPopup(_ctxId); _openCtx = false; }
        if (!ImGuiNET.ImGui.BeginPopup(_ctxId)) return;
        string where = _ctxName.Length > 0 ? _ctxName : "Map point";
        SafeImGui.TextColored(new Vector4(1f, 0.82f, 0.3f, 1f), where);
        SafeImGui.TextDisabled(RynthNavStatus.Coord(_ctxNs, _ctxEw));
        ImGuiNET.ImGui.Separator();
        if (ImGuiNET.ImGui.Selectable("Go here")) RynthNavCommands.Go(where, _ctxNs, _ctxEw);
        if (ImGuiNET.ImGui.Selectable("Route here")) RynthNavCommands.Route(where, _ctxNs, _ctxEw);
        if (ImGuiNET.ImGui.Selectable("Arrow here")) RynthNavCommands.Arrow(where, _ctxNs, _ctxEw);
        if (ImGuiNET.ImGui.Selectable("Copy coordinates")) ImGuiNET.ImGui.SetClipboardText(RynthNavStatus.CommandCoord(_ctxNs, _ctxEw));
        if (Mini)
        {
            ImGuiNET.ImGui.Separator();
            bool follow = Follow, rotate = Rotate;
            if (ImGuiNET.ImGui.Checkbox("Follow me", ref follow)) Follow = follow;
            if (ImGuiNET.ImGui.Checkbox("Rotate with me", ref rotate)) Rotate = rotate;
            if (ImGuiNET.ImGui.Selectable("Zoom in")) ZoomBy(1.5f);
            if (ImGuiNET.ImGui.Selectable("Zoom out")) ZoomBy(1f / 1.5f);
            if (ImGuiNET.ImGui.Selectable("Open the World Map")) UI.PanelRouter.Toggle(WorldMapFace.Title);
        }
        ImGuiNET.ImGui.EndPopup();
    }
}
