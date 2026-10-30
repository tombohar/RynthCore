// ============================================================================
//  RynthCore.Engine - ImGui/Panels/RynthNavFace.cs
//  ImGui face of the RynthNav panel (UI/Panels/RynthNavPanel.cs is the old
//  Avalonia face, the movement part only). Everything RynthNav's /rnav
//  commands do, with buttons and text boxes. Three tabs, under a status line
//  (what RynthNav is doing, or how the last goto ended; Stop while walking):
//
//    Go      the destination box (searches the Atlas as you type: places,
//            dungeons, towns, portals, with type and distance; takes
//            coordinates too), favorites and recent places while it's empty,
//            Go / Route / Arrow / Stop, the "Use portals and recalls" and
//            "Recalls" switches, the path markers switches (RynthNav 0.6.6:
//            the trip on the ground, labels, guide line), the route step by
//            step (the current step highlighted, with the distance left) and
//            "save as nav route" (a RynthAi .nav in NavProfiles), then two sections that
//            fold: Recalls (portal ties and where they land, "which tie was
//            it?", the recalls routes can use) and Info (here, the NavData
//            folder, the route graph, tiles; Load / Test / Preview and the
//            movement pad).
//    Atlas   every place: search by name and type, Nearby, Favorites,
//            Recent; the star, Arrow and Go per row; details on a click.
//    Arrow   the arrow's target, distance and direction; show or hide the
//            arrow overlay (RynthNavArrowFace), Go / Stop / Clear / favorite,
//            set it to a place or coordinates, the arrival radius.
//
//  Our take on GoArrow's arrow, atlas and routes (Digero 2006, Virindi 2011,
//  MIT). RynthNav (the plugin) owns the arrow, favorites, recent, routing and
//  recalls; this face shows its status and sends it /rnav commands
//  (RynthNavCommands). Atlas rows come from RynthNavAtlasSource (the file read
//  read-only here). Older RynthNavs: the parts they can't feed say so.
//
//  Data: UiSources.RynthNav (hub, 250 ms), UiSources.RynthNavAtlas (500 ms,
//  while the Go or Atlas tab shows) and RynthNavCommands.
// ============================================================================

using System;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.UI.Data;
using static RynthCore.Engine.ImGuiBackend.Panels.FaceKit;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed class RynthNavFace : IImGuiPanel
{
    public const string Title = "RynthNav";

    public static void Register()
    {
        ImGuiPanelHost.Register(Title,
            new PanelSpec(new Vector2(390, 620), new Vector2(330, 420), EdgeToEdge: true, GripInBody: true),
            () => new RynthNavFace());
        // The arrow overlay is RynthNav's too; it opens when a command sets the arrow.
        RynthNavArrowFace.Register();
        RynthNavArrowWatch.ShowArrow = RynthNavArrowFace.Show;
        UiSources.RynthNavArrowWatch.Subscribe();
    }

    private static readonly uint RunGreen = RynthTheme.Argb(0xFF40D973), Sep = RynthTheme.Argb(0xFF1A2A39),
        Gold = RynthTheme.Argb(0xFFFFD24D), Purple = RynthTheme.Argb(0xFFBF4DFF);
    private static uint RowHot => RynthTheme.Button;

    private const int TabGo = 0, TabAtlas = 1, TabArrow = 2;

    private static readonly string[] TabNames =
    {
        PhosphorIcons.SneakerMove + " Go", PhosphorIcons.GlobeHemisphereWest + " Atlas", PhosphorIcons.Compass + " Arrow",
    };

    private RynthNavStatus? _st;
    private int _tab;
    private bool _atlasSubscribed;
    private readonly byte[] _search = new byte[96], _arrowTo = new byte[96], _dest = new byte[96];
    private string _seenSearch = "", _seenDest = "";
    private AtlasMode _mode = AtlasMode.Search;
    private int _typeIdx;
    private AtlasRow? _sel;
    private readonly Picker _typePicker = new("##rnav_type_picker");
    private readonly ArrowReadout _read = new();
    // Strings built when what they show changes, not per frame.
    private double _arriveShown = -1;
    private string _arriveText = "", _typeLabel = "";
    private int _typeLabelIdx = -1;
    private AtlasRow? _detailsFor;
    private string _detailsLine = "", _detailsDest = "";

    // Go tab: the picked destination (a row of the list), what the commands get for it, its line.
    private AtlasRow? _pick;
    private string _pickArg = "", _pickCoord = "", _pickLine = "", _typedLine = "", _typedCoord = "";
    private bool _recallsOpen, _infoOpen;

    public void OnShown()
    {
        UiSources.RynthNav.Subscribe();
        UiSources.RynthNav.RequestRefresh();
        PushTabQuery();
        if (_tab != TabArrow) SubscribeAtlas(true);
    }

    public void OnHidden()
    {
        UiSources.RynthNav.Unsubscribe();
        SubscribeAtlas(false);
    }

    private void SubscribeAtlas(bool on)
    {
        if (on == _atlasSubscribed) return;
        _atlasSubscribed = on;
        if (on) { UiSources.RynthNavAtlas.Subscribe(); UiSources.RynthNavAtlas.RequestRefresh(); }
        else UiSources.RynthNavAtlas.Unsubscribe();
    }

    public int PopOutIdleHz => _tab == TabArrow ? 20 : 10;

    public void Draw()
    {
        _st = UiSources.RynthNav.Current?.Value;
        float w = Begin(out Vector2 origin, out Vector2 size);
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        RynthNavStatus? st = _st;

        // Title: dot, RynthNav, what it is
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        uint dot = st == null ? Mute : st.TileLoaded ? RunGreen : st.HasPose ? Amber : Mute;
        dl.AddCircleFilled(p + new Vector2(5, 10), 4.5f, dot);
        ImFontPtr big = ImGuiFonts.Get(UiFont.UiBold14);
        dl.AddText(big, big.FontSize, p + new Vector2(16, 1), Teal, "RynthNav");
        float tw = ImGuiNET.ImGui.CalcTextSize("RynthNav").X * big.FontSize / ImGuiNET.ImGui.GetFontSize();
        dl.AddText(p + new Vector2(24 + tw, 6), Mute, "go · atlas · arrow");
        if (ImGuiPanelHost.HasFace(WorldMapFace.Title))
        {
            bool mapOpen = ImGuiPanelHost.IsOpen(WorldMapFace.Title), miniOpen = ImGuiPanelHost.IsOpen(MiniMapFace.Title);
            if (Button("##rnav_worldmap", PhosphorIcons.GlobeHemisphereWest + " Map", new Vector2(p.X + w - 62, p.Y - 1), new Vector2(62, 22),
                    mapOpen ? Teal : Text, mapOpen ? Selected : BtnFill, border: mapOpen ? Teal : 0))
                UI.PanelRouter.Toggle(WorldMapFace.Title);
            if (Button("##rnav_minimap", "Mini", new Vector2(p.X + w - 62 - 4 - 40, p.Y - 1), new Vector2(40, 22),
                    miniOpen ? Teal : Text, miniOpen ? Selected : BtnFill, border: miniOpen ? Teal : 0))
                UI.PanelRouter.Toggle(MiniMapFace.Title);
        }
        NextLine(p, 22);
        DrawTabs(w);
        DrawStatusLine(w);

        switch (_tab)
        {
            case TabAtlas: DrawAtlas(w, origin, size); break;
            case TabArrow: DrawArrow(w); break;
            default: DrawGo(w, origin, size); break;
        }

        End(origin, size);
        _typePicker.Draw();
    }

    private void DrawTabs(float w)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        float bw = (w - (TabNames.Length - 1) * 4) / TabNames.Length;
        for (int i = 0; i < TabNames.Length; i++)
        {
            bool sel = i == _tab;
            if (Button("##rnav_tab" + i, TabNames[i], new Vector2(p.X + i * (bw + 4), p.Y), new Vector2(bw, 22),
                    sel ? Teal : Text, sel ? Selected : BtnFill, border: sel ? Teal : 0))
            {
                _tab = i;
                SubscribeAtlas(i != TabArrow);
                PushTabQuery();
            }
        }
        NextLine(p, 28);
    }

    /// <summary>The Atlas source serves one query: the Go tab's destination box or the Atlas tab's search.</summary>
    private void PushTabQuery()
    {
        if (_tab == TabGo) PushDestQuery();
        else PushQuery();
    }

    // ── Status line (every tab) ──────────────────────────────────────────

    /// <summary>
    /// What RynthNav is doing ("walking to Holtburg, 1.2k yd left", "taking portal ..."), or how
    /// the last goto ended ("arrived inside Matron Hive", a stop reason), as it says in chat.
    /// Stop sits at its right while a goto runs on the Atlas and Arrow tabs (the Go tab has its own).
    /// </summary>
    private void DrawStatusLine(float w)
    {
        RynthNavStatus? st = _st;
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        const float h = 22;
        bool walking = st?.Goto == true;
        bool stop = walking && _tab != TabGo;
        float textRight = p.X + w - (stop ? 26 : 6);
        dl.AddRectFilled(p, p + new Vector2(w, h), PanelBg, 3);
        uint col = st == null ? Mute : !walking ? Text
            : st.GoPhase == "portal" ? Purple : st.GoPhase == "recall" ? Amber : st.GoPhase == "town" ? Teal : RunGreen;
        dl.AddCircleFilled(p + new Vector2(9, h * 0.5f), 3.5f, walking ? col : Mute);
        string text = st == null ? "RynthNav isn't answering (is it loaded?)" : st.StatusLine.Length > 0 ? st.StatusLine : "ready";
        dl.PushClipRect(p, new Vector2(textRight, p.Y + h), true);
        dl.AddText(new Vector2(p.X + 18, p.Y + (h - ImGuiNET.ImGui.GetFontSize()) * 0.5f), col, text);
        dl.PopClipRect();
        // The whole line on hover (it's clipped to one line).
        ImGuiNET.ImGui.SetCursorScreenPos(p);
        ImGuiNET.ImGui.InvisibleButton("##rnav_status", new Vector2(Math.Max(1, textRight - p.X), h));
        if (text.Length > 40) SafeImGui.SetItemTooltip(text);
        if (stop)
        {
            if (IconButton("##rnav_status_stop", PhosphorIcons.Stop, new Vector2(p.X + w - 22, p.Y + 1), new Vector2(20, 20), Text, StopBg, font: UiFont.Ui11))
                RynthNavCommands.Move(5);
            ImGuiNET.ImGui.SetItemTooltip("Stop walking");
        }
        NextLine(p, h + 6);
    }

    // ── Go ───────────────────────────────────────────────────────────────

    private const int DestRowsShown = 5;

    private void DrawGo(float w, Vector2 origin, Vector2 size)
    {
        RynthNavStatus? st = _st;
        var dl = ImGuiNET.ImGui.GetWindowDrawList();

        // Destination box (+ clear)
        SectionLabel("DESTINATION");
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        float boxW = Math.Max(80, w - 26);
        bool enter = TextBox("##go_dest", _dest, p, boxW, "A place, a dungeon, a town, or 42.1N, 33.6E", out _,
            ImGuiInputTextFlags.EnterReturnsTrue);
        string text = Utf8(_dest).Trim();
        if (text != _seenDest)
        {
            _seenDest = text;
            if (_pick != null && text != _pick.Name) SetPick(null);
            _typedLine = text.Length > 0 ? "To: “" + text + "”  (pick a result, or Go and RynthNav looks it up)" : "";
            AtlasRow? typedCoords = RynthNavAtlasSource.CoordRow(text);
            _typedCoord = typedCoords != null ? RynthNavStatus.CommandCoord(typedCoords.Ns, typedCoords.Ew) : "";
            PushDestQuery();
        }
        if (IconButton("##go_clear", PhosphorIcons.X, new Vector2(p.X + boxW + 4, p.Y), new Vector2(22, 22), Mute, BtnFill,
                enabled: text.Length > 0 || _pick != null, font: UiFont.Ui11))
        {
            WriteUtf8(_dest, "");
            SetPick(null);
        }
        ImGuiNET.ImGui.SetItemTooltip("Clear");
        NextLine(p, 26);

        // Results (search), or favorites and recent while the box is empty
        AtlasView view = UiSources.RynthNavAtlas.Current?.Value ?? AtlasView.Empty;
        AtlasRow[] rows = view.Rows;
        if (enter && _pick == null && rows.Length > 0) Pick(rows[0]);
        if (rows.Length == 0)
        {
            ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui9));
            Wrapped(view.Summary.Length > 0 ? view.Summary
                : st != null && !st.Travel ? "This RynthNav is older than the Atlas (0.6.0): type coordinates, or update RynthNav."
                : RynthNavAtlas.Status.Length > 0 ? RynthNavAtlas.Status : "Loading…", Mute, w);
            ImGuiNET.ImGui.PopFont();
        }
        else
        {
            float listH = Math.Min(rows.Length, DestRowsShown) * RowH;
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ChildBg, PanelBg);
            bool open = ImGuiNET.ImGui.BeginChild("##go_list", new Vector2(w, listH));
            ImGuiNET.ImGui.PopStyleColor();
            if (open) DrawDestRows(rows, ImGuiNET.ImGui.GetContentRegionAvail().X);
            ImGuiNET.ImGui.EndChild();
        }

        // What the buttons act on
        string line = _pick != null ? _pickLine : _typedLine;
        if (line.Length > 0)
        {
            p = ImGuiNET.ImGui.GetCursorScreenPos();
            bool canStar = _pick != null && _pick.OnMap && st?.Travel == true;
            bool fav = canStar && IsFavorite(st!, _pick!);
            float lx = p.X;
            if (canStar)
            {
                if (IconButton("##go_star", PhosphorIcons.Star, p, new Vector2(18, 18), fav ? Gold : Faded(Mute), fav ? Selected : PanelBg, font: UiFont.Ui11))
                    RynthNavCommands.ToggleFavorite(_pick!.Name, _pick.Ns, _pick.Ew);
                SafeImGui.SetItemTooltip(fav ? "Remove from favorites" : "Add to favorites");
                lx += 22;
            }
            dl.PushClipRect(new Vector2(lx, p.Y), new Vector2(p.X + w, p.Y + 18), true);
            dl.AddText(new Vector2(lx, p.Y + (18 - ImGuiNET.ImGui.GetFontSize()) * 0.5f), _pick != null ? Teal : Mute, line);
            dl.PopClipRect();
            NextLine(p, 22);
        }
        else ImGuiNET.ImGui.Dummy(new Vector2(0, 2));

        // Go / Route / Arrow / Stop
        p = ImGuiNET.ImGui.GetCursorScreenPos();
        bool travel = st?.Travel == true;
        bool has = _pick != null || text.Length > 0;
        bool canGo = st != null && has && (travel || PickCoord().Length > 0);
        bool walking = st?.Goto == true;
        float bw = (w - 3 * 4) / 4f;
        if (Button("##go_go", GoLabel, p, new Vector2(bw, 26), Green, BtnFill, enabled: canGo)) Go(text);
        SafeImGui.SetItemTooltip(travel
            ? (st!.PortalsOn ? "Walk there, through portals and with recalls when that is quicker" : "Walk there (portals and recalls are switched off)")
            : "Walk the navmesh to these coordinates (update RynthNav for places and routes)");
        if (Button("##go_route", PhosphorIcons.Path + " Route", new Vector2(p.X + bw + 4, p.Y), new Vector2(bw, 26), Text, BtnFill, enabled: travel && has))
            RynthNavCommands.Command("route " + Arg(text));
        SafeImGui.SetItemTooltip(travel ? "Plan only: show the steps below, don't walk" : "Update RynthNav (0.6.0+) for routes");
        if (Button("##go_arrow", PhosphorIcons.NavigationArrow + " Arrow", new Vector2(p.X + 2 * (bw + 4), p.Y), new Vector2(bw, 26), Teal, BtnFill, enabled: travel && has))
            RynthNavCommands.Command("arrow " + Arg(text));
        SafeImGui.SetItemTooltip(travel ? "Show the arrow overlay pointing there" : "Update RynthNav (0.6.0+) for the arrow");
        if (Button("##go_stop", PhosphorIcons.Stop + " Stop", new Vector2(p.X + 3 * (bw + 4), p.Y), new Vector2(bw, 26),
                walking ? Text : Red, walking ? StopBg : BtnFill, border: walking ? Red : 0))
            RynthNavCommands.Move(5);
        SafeImGui.SetItemTooltip(walking ? "Stop walking" : "Stop any movement");
        NextLine(p, 32);

        // Switches
        p = ImGuiNET.ImGui.GetCursorScreenPos();
        float half = (w - 4) * 0.5f;
        if (travel)
        {
            if (Toggle("##go_portals", "Use portals and recalls", st!.PortalsOn, p, half))
                RynthNavCommands.Command(st.PortalsOn ? "portals off" : "portals on");
            ImGuiNET.ImGui.SetItemTooltip("Go and Route use portals, the Town Network and (with Recalls) recall spells when that is quicker.\nOff: walking only. From a dungeon there's no walking out, so it says so.");
            // Recalls are part of routes: they only count while "Use portals and recalls" is on.
            Vector2 rp = new(p.X + half + 4, p.Y);
            if (st.PortalsOn)
            {
                if (Toggle("##go_recalls", "Recalls", st.RecallsOn, rp, half))
                    RynthNavCommands.Command(st.RecallsOn ? "recalls off" : "recalls on");
                ImGuiNET.ImGui.SetItemTooltip("Let routes cast the recalls you know (and @lifestone, house recall...).");
            }
            else
            {
                Button("##go_recalls_off", (st.RecallsOn ? PhosphorIcons.CheckSquare : PhosphorIcons.Square) + " Recalls", rp, new Vector2(half, 22),
                    Faded(Mute), BtnFill, leftAlign: true);
                ImGuiNET.ImGui.SetItemTooltip("Recalls are part of routes: turn on \"Use portals and recalls\" first.");
            }
            NextLine(p, 28);
            if (st.HasAvoid) AvoidRows(st, w);
            if (st.HasMarkers) MarkerRow(st, w);
            if (st.HasMesh) MeshRows(st, w);
        }
        Line(w);

        // Route, Recalls, Info: scroll
        float bodyH = Math.Max(40, origin.Y + size.Y - 6 - ImGuiNET.ImGui.GetCursorScreenPos().Y - GripOverlap());
        bool bodyOpen = ImGuiNET.ImGui.BeginChild("##go_body", new Vector2(w, bodyH));
        if (bodyOpen)
        {
            float cw = ImGuiNET.ImGui.GetContentRegionAvail().X - 4;
            DrawRouteView(st, cw);
            ImGuiNET.ImGui.Dummy(new Vector2(0, 4));
            if (Fold("##go_fold_recalls", _recallsOpen ? RecallsOpenLabel : RecallsShutLabel, ref _recallsOpen, cw)) DrawRecalls(st, cw);
            if (Fold("##go_fold_info", _infoOpen ? InfoOpenLabel : InfoShutLabel, ref _infoOpen, cw)) DrawInfo(st, cw);
        }
        ImGuiNET.ImGui.EndChild();
    }

    private void PushDestQuery()
    {
        string text = Utf8(_dest).Trim();
        RynthNavAtlasSource.Query = text.Length == 0
            ? new AtlasQuery("", "All", AtlasMode.Saved)
            : new AtlasQuery(text, "All", AtlasMode.Search);
    }

    /// <summary>Picks a row as the destination: the box shows its name, the buttons act on it.</summary>
    private void Pick(AtlasRow r)
    {
        WriteUtf8(_dest, r.Name);
        _seenDest = Utf8(_dest).Trim();
        _typedLine = "";
        SetPick(r);
        PushDestQuery();
    }

    private void SetPick(AtlasRow? r)
    {
        _pick = r;
        if (r == null) { _pickArg = _pickCoord = _pickLine = ""; return; }
        _pickCoord = r.OnMap ? RynthNavStatus.CommandCoord(r.Ns, r.Ew) : "";
        // Coordinates and a name ("42.08N, 33.60E Holtburg") pin the exact place; a place off the
        // map (in a dungeon) goes by name, and RynthNav finds its entrance portal or says why not.
        bool bareCoords = r.Type == "Coordinates" && r.Name == r.Coord;
        _pickArg = !r.OnMap ? r.Name : bareCoords ? _pickCoord : _pickCoord + " " + r.Name;
        _pickLine = "To: " + r.Name + "  ·  " + r.Type + "  ·  " + r.Coord + (r.Dist.Length > 0 ? "  ·  " + r.Dist : "");
    }

    /// <summary>What a command gets: the picked place, else the typed text (a name or coordinates).</summary>
    private string Arg(string typed) => _pick != null ? _pickArg : typed;

    /// <summary>Coordinates for the navmesh goto and Preview: the picked place's, else coordinates typed.</summary>
    private string PickCoord() => _pick != null ? _pickCoord : _typedCoord;

    private void Go(string typed)
    {
        RynthNavStatus? st = _st;
        if (st == null) return;
        if (st.Travel) RynthNavCommands.Command("go " + Arg(typed));
        else
        {
            // RynthNav before 0.6.0: only the navmesh goto to coordinates.
            string c = PickCoord();
            if (c.Length > 0) RynthNavCommands.Goto(c);
        }
    }

    private static bool IsFavorite(RynthNavStatus st, AtlasRow r)
    {
        foreach (RynthNavPlace f in st.Favorites)
            if (f.Name.Equals(r.Name, StringComparison.OrdinalIgnoreCase) && Math.Abs(f.Ns - r.Ns) < 0.05 && Math.Abs(f.Ew - r.Ew) < 0.05)
                return true;
        return false;
    }

    /// <summary>The destination list: star, type icon, name, type and distance. A click picks the row.</summary>
    private void DrawDestRows(AtlasRow[] rows, float w)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        Vector2 basePos = ImGuiNET.ImGui.GetCursorScreenPos();
        float scrollY = ImGuiNET.ImGui.GetScrollY(), viewH = ImGuiNET.ImGui.GetWindowHeight();
        int first = Math.Max(0, (int)(scrollY / RowH));
        int last = Math.Min(rows.Length, first + (int)(viewH / RowH) + 2);
        ImFontPtr f = ImGuiFonts.Get(UiFont.Ui10), f9 = ImGuiFonts.Get(UiFont.Ui9);
        const float btn = 18, iconW = 20;
        bool travel = _st?.Travel == true;
        for (int i = first; i < last; i++)
        {
            AtlasRow r = rows[i];
            Vector2 rp = basePos + new Vector2(0, i * RowH);
            bool selected = _pick != null && SameRow(_pick, r);
            if (selected) dl.AddRectFilled(rp, rp + new Vector2(w, RowH), Selected);
            else if ((i & 1) == 1) dl.AddRectFilled(rp, rp + new Vector2(w, RowH), RowAlt);

            ImGuiNET.ImGui.PushID(i);
            if (IconButton("##fav", PhosphorIcons.Star, rp + new Vector2(2, 1), new Vector2(btn, btn), r.Favorite ? Gold : Faded(Mute),
                    r.Favorite ? Selected : PanelBg, enabled: travel && r.OnMap, font: UiFont.Ui11))
                RynthNavCommands.ToggleFavorite(r.Name, r.Ns, r.Ew);
            SafeImGui.SetItemTooltip(r.Favorite ? "Remove from favorites" : "Add to favorites");

            float bodyX = rp.X + btn + 4, bodyW = w - btn - 4;
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(bodyX, rp.Y));
            if (ImGuiNET.ImGui.InvisibleButton("##row", new Vector2(Math.Max(1, bodyW), RowH))) Pick(r);
            bool hot = ImGuiNET.ImGui.IsItemHovered();
            ImGuiNET.ImGui.PopID();
            if (hot && !selected) dl.AddRectFilled(new Vector2(bodyX, rp.Y), new Vector2(bodyX + bodyW, rp.Y + RowH), RowHot);

            PhosphorIcons.DrawCentered(dl, ImGuiFonts.Get(UiFont.Ui11), TypeIcon(r.Type), new Vector2(bodyX, rp.Y), new Vector2(iconW, RowH), TypeColor(r.Type));
            float metaW = r.Meta.Length > 0 ? f9.CalcTextSizeA(f9.FontSize, float.MaxValue, 0, r.Meta).X + 6 : 0;
            float nameX = bodyX + iconW + 2, nameMax = bodyX + bodyW - metaW;
            dl.PushClipRect(new Vector2(nameX, rp.Y), new Vector2(Math.Max(nameX, nameMax - 2), rp.Y + RowH), true);
            dl.AddText(f, f.FontSize, new Vector2(nameX, rp.Y + (RowH - f.FontSize) * 0.5f), r.OnMap ? Text : Mute, r.Name);
            dl.PopClipRect();
            if (metaW > 0)
                dl.AddText(f9, f9.FontSize, new Vector2(nameMax, rp.Y + (RowH - f9.FontSize) * 0.5f), Mute, r.Meta);
        }
        ImGuiNET.ImGui.SetCursorScreenPos(basePos);
        ImGuiNET.ImGui.Dummy(new Vector2(w, Math.Max(1, rows.Length * RowH)));
    }

    private const string GoLabel = PhosphorIcons.Play + " Go";
    private const string RecallsOpenLabel = PhosphorIcons.CaretDown + "  RECALLS AND PORTAL TIES",
        RecallsShutLabel = PhosphorIcons.CaretRight + "  RECALLS AND PORTAL TIES",
        InfoOpenLabel = PhosphorIcons.CaretDown + "  INFO, LOAD / TEST, MOVEMENT",
        InfoShutLabel = PhosphorIcons.CaretRight + "  INFO, LOAD / TEST, MOVEMENT";

    /// <summary>A section header that folds: click to open or shut. Returns whether it's open.</summary>
    private static bool Fold(string id, string label, ref bool open, float w)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        if (Button(id, label, p, new Vector2(w, 22), Teal, open ? Selected : BtnFill, leftAlign: true)) open = !open;
        NextLine(p, 26);
        return open;
    }

    // ── Route view ───────────────────────────────────────────────────────

    private void DrawRouteView(RynthNavStatus? st, float cw)
    {
        SectionLabel("ROUTE");
        if (st == null) { Wrapped("RynthNav isn't answering.", Mute, cw); return; }
        if (!st.Travel)
        {
            Wrapped("This RynthNav is older than routes (0.6.0): update RynthNav. "
                + "Go still walks the navmesh to coordinates.", Amber, cw);
            if (st.PathText.Length > 0) Wrapped(st.PathText, Text, cw);
            return;
        }
        if (st.Route.Length == 0)
        {
            Wrapped("No route yet: pick a destination, then Route (plan only) or Go.", Mute, cw);
            return;
        }
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.UiBold11));
        Wrapped(st.RouteHeader, Text, cw);
        ImGuiNET.ImGui.PopFont();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        for (int i = 0; i < st.Route.Length && i < st.RouteLines.Length; i++)
        {
            RynthNavRouteStep s = st.Route[i];
            bool current = st.RouteActive && i == st.RouteIdx;
            bool done = st.RouteActive && i < st.RouteIdx;
            uint col = current ? RunGreen : done ? Faded(Mute)
                : s.Kind == "recall" ? Amber : s.Kind == "portal" ? Purple : s.Kind == "town" ? Teal : Text;
            Vector2 top = ImGuiNET.ImGui.GetCursorScreenPos();
            ImGuiNET.ImGui.Indent(8);
            Wrapped(st.RouteLines[i], col, cw - 8);
            if (current && st.StepLeft.Length > 0)
            {
                ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui9));
                Label(st.StepLeft, RunGreen);
                ImGuiNET.ImGui.PopFont();
            }
            ImGuiNET.ImGui.Unindent(8);
            if (current)
            {
                // A bar down the current step's left edge.
                float bottom = ImGuiNET.ImGui.GetItemRectMax().Y;
                dl.AddRectFilled(new Vector2(top.X, top.Y), new Vector2(top.X + 3, bottom), RunGreen, 1);
            }
        }
        DrawSaveRoute(st, cw);
    }

    // ── Recalls section ──────────────────────────────────────────────────

    private static void DrawRecalls(RynthNavStatus? st, float cw)
    {
        ImGuiNET.ImGui.Indent(4);
        float iw = cw - 4;
        if (st == null || !st.Travel)
        {
            Wrapped(st == null ? "RynthNav isn't answering." : "This RynthNav is older than recall routing (0.6.0): update RynthNav.", Amber, iw);
            ImGuiNET.ImGui.Unindent(4);
            return;
        }

        SectionLabel("PORTAL TIES");
        if (st.HasTies)
        {
            Wrapped(st.Tie1Text, Text, iw);
            Wrapped(st.Tie2Text, Text, iw);
            if (st.TiePendingText.Length > 0)
            {
                Wrapped(st.TiePendingText, Amber, iw);
                Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
                float bw = (iw - 4) * 0.5f;
                if (Button("##rc_tie1", "Primary (tie 1)", p, new Vector2(bw, 24), Text, BtnFill, border: Amber))
                    RynthNavCommands.Command("tie 1");
                ImGuiNET.ImGui.SetItemTooltip("It was Primary Portal Tie");
                if (Button("##rc_tie2", "Secondary (tie 2)", new Vector2(p.X + bw + 4, p.Y), new Vector2(bw, 24), Text, BtnFill, border: Amber))
                    RynthNavCommands.Command("tie 2");
                ImGuiNET.ImGui.SetItemTooltip("It was Secondary Portal Tie");
                NextLine(p, 28);
            }
            if (st.MineLines.Length > 0)
            {
                ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
                SectionLabel("WHERE YOUR RECALLS LAND");
                foreach (string line in st.MineLines) Wrapped(line, Text, iw);
            }
        }
        else Wrapped("Update RynthNav (0.6.1) to see your portal ties here and answer \"which tie was it?\" with a button.", Mute, iw);

        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
        SectionLabel(st.RecallHeader);
        foreach (string line in st.RecallLines) Wrapped(line, Text, iw);
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui9));
        foreach (string n in st.RecallNotes) Wrapped(n, Mute, iw);
        Wrapped("Your own recalls (lifestone, portal ties, house...) are learned as you use them.", Mute, iw);
        ImGuiNET.ImGui.PopFont();
        ImGuiNET.ImGui.Dummy(new Vector2(0, 4));
        ImGuiNET.ImGui.Unindent(4);
    }

    // ── Info section ─────────────────────────────────────────────────────

    private void DrawInfo(RynthNavStatus? st, float cw)
    {
        ImGuiNET.ImGui.Indent(4);
        float iw = cw - 4;
        if (st == null) Wrapped("RynthNav isn't answering.", Mute, iw);
        else
        {
            InfoRow("Here", st.InfoHere, iw);
            InfoRow("Position", st.PlayerText, iw);
            InfoRow("NavData", st.InfoNavData, iw);
            if (st.InfoTiles.Length > 0) InfoRow("Tiles", st.InfoTiles, iw);
            InfoRow("Route graph", st.InfoGraph, iw);
            InfoRow("Town Network", st.InfoTownNet, iw);
            InfoRow("Atlas", st.InfoAtlas, iw);
            InfoRow("Navmesh", st.InfoStatus, iw);
        }
        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));

        // Load / Test / Preview
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        string coord = PickCoord();
        float bw = (iw - 2 * 4) / 3f;
        if (Button("##load", "Load tile", p, new Vector2(bw, 24), Text, BtnFill)) RynthNavCommands.LoadTile();
        ImGuiNET.ImGui.SetItemTooltip("Load the navmesh tiles around you again");
        if (Button("##test", "Test @ me", new Vector2(p.X + bw + 4, p.Y), new Vector2(bw, 24), Text, BtnFill)) RynthNavCommands.TestQuery();
        ImGuiNET.ImGui.SetItemTooltip("Is there navmesh under you?");
        if (Button("##preview", "Preview", new Vector2(p.X + 2 * (bw + 4), p.Y), new Vector2(bw, 24), Text, BtnFill, enabled: coord.Length > 0))
            RynthNavCommands.Preview(coord);
        SafeImGui.SetItemTooltip(coord.Length > 0 ? "Plan the navmesh path to the destination (close places only), no walking"
            : "Pick a destination on the map first");
        NextLine(p, 30);

        // Movement pad
        SectionLabel("MOVE");
        p = ImGuiNET.ImGui.GetCursorScreenPos();
        const float bw2 = 58, bh = 42, gap = 6;
        float left = p.X + (iw - (3 * bw2 + 2 * gap)) * 0.5f;
        MoveButton(PhosphorIcons.ArrowUp, "Forward (tap to run / stop)", 1, new Vector2(left + bw2 + gap, p.Y), st?.RunState == 1);
        MoveButton(PhosphorIcons.ArrowLeft, "Turn left (tap to start / stop)", 3, new Vector2(left, p.Y + bh + gap), st?.TurnState == -1);
        MoveButton(PhosphorIcons.Stop, "Stop everything", 5, new Vector2(left + bw2 + gap, p.Y + bh + gap), false);
        MoveButton(PhosphorIcons.ArrowRight, "Turn right (tap to start / stop)", 4, new Vector2(left + 2 * (bw2 + gap), p.Y + bh + gap), st?.TurnState == 1);
        MoveButton(PhosphorIcons.ArrowDown, "Backward (tap to walk back / stop)", 2, new Vector2(left + bw2 + gap, p.Y + 2 * (bh + gap)), st?.RunState == 2);
        NextLine(p, 3 * bh + 2 * gap + 4);
        CenteredHint(MoveHint, iw);
        ImGuiNET.ImGui.Dummy(new Vector2(0, 4));
        ImGuiNET.ImGui.Unindent(4);
    }

    private static void InfoRow(string label, string value, float w)
    {
        const float lw = 78;
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        ImGuiNET.ImGui.GetWindowDrawList().AddText(p, Mute, label);
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X + lw, p.Y));
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Text);
        ImGuiNET.ImGui.PushTextWrapPos(ImGuiNET.ImGui.GetCursorPosX() + Math.Max(40, w - lw));
        ImGuiNET.ImGui.TextUnformatted(value.Length > 0 ? value : "—");
        ImGuiNET.ImGui.PopTextWrapPos();
        ImGuiNET.ImGui.PopStyleColor();
        // Back to the left edge for the next row.
        Vector2 end = new(p.X, ImGuiNET.ImGui.GetItemRectMax().Y + 3);
        ImGuiNET.ImGui.SetCursorScreenPos(end);
        ImGuiNET.ImGui.Dummy(new Vector2(0, 0));
    }

    private const string MoveHint = "tap " + PhosphorIcons.ArrowUp + PhosphorIcons.ArrowDown + " run  ·  tap "
        + PhosphorIcons.ArrowLeft + PhosphorIcons.ArrowRight + " turn (tap again to stop)  ·  " + PhosphorIcons.Stop + " stop";

    private static void MoveButton(string glyph, string tip, int cmd, Vector2 pos, bool active)
    {
        if (IconButton("##mv" + cmd, glyph, pos, new Vector2(58, 42), Text, active ? RunGreen : BtnFill))
            RynthNavCommands.Move(cmd);
        SafeImGui.SetItemTooltip(tip);
    }

    // Avoidance, one row per kind of thing kept away from: on/off, then − value +. Each click
    // sends /rnav avoid ... (saved in rynthnav.json by RynthNav).
    private static void AvoidRows(RynthNavStatus st, float w)
    {
        AvoidRow("portals", "Avoid portals", st.AvoidPortals, st.AvoidPortalM, 0.5, w,
            "Keep this far from every portal that isn't the next step's own (walking into one teleports you).");
        AvoidRow("walls", "Avoid walls", st.AvoidWalls, st.AvoidWallM, 0.5, w,
            "Swing this far wide of building and wall corners the path turns round.");
        AvoidRow("corners", "Corner reach", null, st.CornerM, 0.5, w,
            "A path corner counts as reached this close. Bigger turns earlier and cuts corners.");
    }

    private static void AvoidRow(string key, string label, bool? on, double metres, double step, float w, string tip)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        float bw = 22, vw = 54, lw = w - 2 * bw - vw - 12;
        if (on is bool b)
        {
            if (Toggle("##av_" + key, label, b, p, lw))
                RynthNavCommands.Command($"avoid {key} {(b ? "off" : "on")}");
        }
        else
            Button("##av_" + key, label, p, new Vector2(lw, 22), Text, BtnFill, enabled: false, leftAlign: true);
        SafeImGui.SetItemTooltip(tip);
        bool active = on ?? true;
        float x = p.X + lw + 4;
        string Fmt(double v) => v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        if (Button("##av_" + key + "_dn", "-", new Vector2(x, p.Y), new Vector2(bw, 22), Text, BtnFill, enabled: active))
            RynthNavCommands.Command($"avoid {key} {Fmt(Math.Max(0, metres - step))}");
        Button("##av_" + key + "_v", Fmt(metres) + " m", new Vector2(x + bw + 4, p.Y), new Vector2(vw, 22), active ? Text : Faded(Mute), PanelBg, enabled: false);
        if (Button("##av_" + key + "_up", "+", new Vector2(x + bw + vw + 8, p.Y), new Vector2(bw, 22), Text, BtnFill, enabled: active))
            RynthNavCommands.Command($"avoid {key} {Fmt(metres + step)}");
        NextLine(p, 26);
    }

    // Path markers (RynthNav 0.6.6+): the planned trip on the ground, its labels, the guide line.
    // Each click sends /rnav markers ... (saved in rynthnav.json by RynthNav).
    private static void MarkerRow(RynthNavStatus st, float w)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        float w1 = (w - 8) * 0.40f, w2 = (w - 8) * 0.28f, w3 = w - 8 - w1 - w2;
        if (Toggle("##mk_on", "Path markers", st.MarkersOn, p, w1))
            RynthNavCommands.Command(st.MarkersOn ? "markers off" : "markers on");
        SafeImGui.SetItemTooltip("Show the planned trip on the ground, the way RynthAi shows its nav routes: rings at the\n"
            + "waypoints, lines between them, the portals, recalls and the goal in their own colours.\n"
            + "They go when the trip ends, you stop, or you land somewhere that isn't on it."
            + (st.MarkerStatus.Length > 0 ? "\n\nNow: " + st.MarkerStatus : ""));
        if (Toggle("##mk_labels", "Labels", st.MarkerLabels, new Vector2(p.X + w1 + 4, p.Y), w2))
            RynthNavCommands.Command(st.MarkerLabels ? "markers labels off" : "markers labels on");
        ImGuiNET.ImGui.SetItemTooltip("Names over the portal, recall, Town Network and goal markers.");
        if (Toggle("##mk_guide", "Guide line", st.MarkerGuide, new Vector2(p.X + w1 + w2 + 8, p.Y), w3))
            RynthNavCommands.Command(st.MarkerGuide ? "markers guide off" : "markers guide on");
        ImGuiNET.ImGui.SetItemTooltip("A line from your feet to the next waypoint (to the nearest marker for a plan you're not walking).");
        NextLine(p, 26);
    }

    // The navmesh drawn in the world (RynthNav 0.6.7+): green where nav can walk, red walls where
    // the mesh ends. Each click sends /rnav mesh ... (saved in rynthnav.json by RynthNav).
    private static void MeshRows(RynthNavStatus st, float w)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        float w1 = (w - 8) * 0.44f, w2 = (w - 8) * 0.26f, w3 = w - 8 - w1 - w2;
        if (Toggle("##ms_on", "Show navmesh", st.MeshOn, p, w1))
            RynthNavCommands.Command(st.MeshOn ? "mesh off" : "mesh on");
        SafeImGui.SetItemTooltip("Draw the navigation mesh in the world: green where nav can walk, red walls where the mesh ends\n"
            + "(buildings, trees, rocks, cliffs, steep ground and the margin round them). Not drawn in dungeons."
            + (st.MeshOn ? $"\n\nNow: {st.MeshDrawn} triangles drawn." : ""));
        if (Toggle("##ms_floor", "Green", st.MeshFloor, new Vector2(p.X + w1 + 4, p.Y), w2))
            RynthNavCommands.Command(st.MeshFloor ? "mesh floor off" : "mesh floor on");
        ImGuiNET.ImGui.SetItemTooltip("The walkable ground, in green.");
        if (Toggle("##ms_walls", "Red walls", st.MeshWalls, new Vector2(p.X + w1 + w2 + 8, p.Y), w3))
            RynthNavCommands.Command(st.MeshWalls ? "mesh walls off" : "mesh walls on");
        ImGuiNET.ImGui.SetItemTooltip("Upright red strips along every edge nav will not cross.");
        NextLine(p, 26);
        MeshStepRow("radius", "Mesh radius", st.MeshRadius, 10, 10, 150, " m", st.MeshOn, w,
            "How far around you the mesh is drawn.");
        MeshStepRow("triangles", "Mesh detail", st.MeshTriangles, 500, 200, 7000, "", st.MeshOn, w,
            "The most triangles the mesh may use. The game's 3D buffer holds 8192 for every plugin, so keep this under about 5000\n"
            + "when RynthVision's slope overlay is on. The nearest ground and walls are kept first.");
    }

    private static void MeshStepRow(string key, string label, int value, int step, int min, int max, string unit, bool active, float w, string tip)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        float bw = 22, vw = 54, lw = w - 2 * bw - vw - 12;
        Button("##ms_" + key, label, p, new Vector2(lw, 22), active ? Text : Faded(Mute), BtnFill, enabled: false, leftAlign: true);
        SafeImGui.SetItemTooltip(tip);
        float x = p.X + lw + 4;
        if (Button("##ms_" + key + "_dn", "-", new Vector2(x, p.Y), new Vector2(bw, 22), Text, BtnFill, enabled: value > min))
            RynthNavCommands.Command($"mesh {key} {Math.Max(min, value - step)}");
        Button("##ms_" + key + "_v", value + unit, new Vector2(x + bw + 4, p.Y), new Vector2(vw, 22), active ? Text : Faded(Mute), PanelBg, enabled: false);
        if (Button("##ms_" + key + "_up", "+", new Vector2(x + bw + vw + 8, p.Y), new Vector2(bw, 22), Text, BtnFill, enabled: value < max))
            RynthNavCommands.Command($"mesh {key} {Math.Min(max, value + step)}");
        NextLine(p, 26);
    }

    // ── Save as nav route (RynthNav 0.6.6+) ──────────────────────────────

    private readonly byte[] _saveName = new byte[96];
    private string _saveSeenFor = "\u0001";

    /// <summary>
    /// Below the route: a name (the place's, to start with) and Save, which writes the trip as a
    /// RynthAi nav route into NavProfiles (/rnav saveroute). A name already taken: Replace.
    /// </summary>
    private void DrawSaveRoute(RynthNavStatus st, float cw)
    {
        if (!st.HasSave || !st.CanSave) return;
        if (st.SaveFor != _saveSeenFor) { _saveSeenFor = st.SaveFor; WriteUtf8(_saveName, st.SaveName); }
        ImGuiNET.ImGui.Dummy(new Vector2(0, 4));
        SectionLabel("SAVE AS NAV ROUTE");
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        const float bw = 78;
        float boxW = Math.Max(60, cw - bw - 4);
        bool enter = TextBox("##save_name", _saveName, p, boxW, "Route name", out _, ImGuiInputTextFlags.EnterReturnsTrue);
        string name = Utf8(_saveName).Trim();
        bool can = name.Length > 0 && !st.SaveBusy;
        if (Button("##save_go", st.SaveBusy ? "Saving…" : PhosphorIcons.FloppyDisk + " Save", new Vector2(p.X + boxW + 4, p.Y), new Vector2(bw, 22),
                Teal, BtnFill, enabled: can) || (enter && can))
            RynthNavCommands.Command("saveroute " + name);
        SafeImGui.SetItemTooltip("Write this trip as a RynthAi nav route (NavProfiles\\" + (name.Length > 0 ? name : "name") + ".nav):\n"
            + "the walks as waypoints, portals as portal steps, recalls as recall steps.\n"
            + "RynthAi lists it in its Nav picker; /ra nav load " + (name.Length > 0 ? name : "<name>") + " loads it.");
        NextLine(p, 26);
        if (st.SaveConflict.Length > 0 && st.SaveConflict.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            p = ImGuiNET.ImGui.GetCursorScreenPos();
            float rw = 78, tw = Math.Max(40, cw - rw - 4);
            var dl = ImGuiNET.ImGui.GetWindowDrawList();
            dl.PushClipRect(p, new Vector2(p.X + tw, p.Y + 22), true);
            dl.AddText(new Vector2(p.X, p.Y + (22 - ImGuiNET.ImGui.GetFontSize()) * 0.5f), Amber, name + ".nav is already there.");
            dl.PopClipRect();
            if (Button("##save_replace", "Replace", new Vector2(p.X + tw + 4, p.Y), new Vector2(rw, 22), Text, BtnFill, enabled: !st.SaveBusy, border: Amber))
                RynthNavCommands.Command("saveroute " + name + " overwrite");
            SafeImGui.SetItemTooltip("Replace NavProfiles\\" + name + ".nav with this trip");
            NextLine(p, 26);
        }
        if (st.SaveLast.Length > 0)
        {
            ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui9));
            Wrapped(st.SaveLast, Mute, cw);
            ImGuiNET.ImGui.PopFont();
        }
    }

    private static bool Toggle(string id, string label, bool on, Vector2 pos, float width)
    {
        string text = (on ? PhosphorIcons.CheckSquare : PhosphorIcons.Square) + " " + label;
        return Button(id, text, pos, new Vector2(width, 22), on ? Teal : Text, on ? Selected : BtnFill, leftAlign: true, border: on ? Teal : 0);
    }

    // ── Atlas ────────────────────────────────────────────────────────────

    private static readonly (AtlasMode Mode, string Label, string Tip)[] Modes =
    {
        (AtlasMode.Search, PhosphorIcons.MagnifyingGlass + " Search", "Every place, best name match first"),
        (AtlasMode.Nearby, PhosphorIcons.Target + " Nearby", "Places on the map, nearest first"),
        (AtlasMode.Favorites, PhosphorIcons.Star + " Favorites", "Places you starred"),
        (AtlasMode.Recent, PhosphorIcons.ClockCounterClockwise + " Recent", "Places you pointed the arrow at or went to"),
    };

    private const float RowH = 20f, DetailH = 118f;

    private void DrawAtlas(float w, Vector2 origin, Vector2 size)
    {
        RynthNavStatus? st = _st;
        if (st != null && !st.Travel) { Wrapped("This RynthNav is older than the Atlas (0.6.0): update RynthNav.", Amber, w); return; }

        // Mode buttons
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        float mw = (w - 3 * 4) / 4f;
        for (int i = 0; i < Modes.Length; i++)
        {
            bool sel = Modes[i].Mode == _mode;
            if (Button("##atl_mode" + i, Modes[i].Label, new Vector2(p.X + i * (mw + 4), p.Y), new Vector2(mw, 22),
                    sel ? Teal : Text, sel ? Selected : BtnFill, border: sel ? Teal : 0))
            {
                _mode = Modes[i].Mode;
                PushQuery();
            }
            SafeImGui.SetItemTooltip(Modes[i].Tip);
        }
        NextLine(p, 26);

        // Search box + type
        p = ImGuiNET.ImGui.GetCursorScreenPos();
        const float typeW = 112;
        float searchW = Math.Max(80, w - typeW - 4);
        TextBox("##atl_search", _search, p, searchW, "Search places by name", out _);
        string text = Utf8(_search);
        if (text != _seenSearch) { _seenSearch = text; PushQuery(); }
        var typePos = new Vector2(p.X + searchW + 4, p.Y);
        if (_typeLabelIdx != _typeIdx)
        {
            _typeLabelIdx = _typeIdx;
            _typeLabel = "Type: " + RynthNavAtlas.Types[_typeIdx] + " " + PhosphorIcons.CaretDown;
        }
        if (Button("##atl_type", _typeLabel, typePos, new Vector2(typeW, 22), Text, BtnFill))
            _typePicker.Open(typePos + new Vector2(0, 24), RynthNavAtlas.Types, _typeIdx, i => { _typeIdx = i; PushQuery(); }, typeW);
        NextLine(p, 26);

        AtlasView view = UiSources.RynthNavAtlas.Current?.Value ?? AtlasView.Empty;
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui9));
        Label(view.Summary.Length > 0 ? view.Summary : RynthNavAtlas.Status.Length > 0 ? RynthNavAtlas.Status : "Loading…", Mute);
        ImGuiNET.ImGui.PopFont();

        // List
        float listH = Math.Max(60, origin.Y + size.Y - 6 - ImGuiNET.ImGui.GetCursorScreenPos().Y - DetailH - 4);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ChildBg, PanelBg);
        bool open = ImGuiNET.ImGui.BeginChild("##atl_list", new Vector2(w, listH));
        ImGuiNET.ImGui.PopStyleColor();
        if (open) DrawRows(view.Rows, ImGuiNET.ImGui.GetContentRegionAvail().X);
        ImGuiNET.ImGui.EndChild();

        DrawDetails(w, origin, size);
    }

    private void PushQuery() =>
        RynthNavAtlasSource.Query = new AtlasQuery(_seenSearch, RynthNavAtlas.Types[_typeIdx], _mode);

    private void DrawRows(AtlasRow[] rows, float w)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        Vector2 basePos = ImGuiNET.ImGui.GetCursorScreenPos();
        float scrollY = ImGuiNET.ImGui.GetScrollY(), viewH = ImGuiNET.ImGui.GetWindowHeight();
        int first = Math.Max(0, (int)(scrollY / RowH));
        int last = Math.Min(rows.Length, first + (int)(viewH / RowH) + 2);
        ImFontPtr f = ImGuiFonts.Get(UiFont.Ui10), f9 = ImGuiFonts.Get(UiFont.Ui9);
        const float btn = 18, iconW = 20;
        for (int i = first; i < last; i++)
        {
            AtlasRow r = rows[i];
            Vector2 rp = basePos + new Vector2(0, i * RowH);
            bool selected = _sel != null && SameRow(_sel, r);
            if (selected) dl.AddRectFilled(rp, rp + new Vector2(w, RowH), Selected);
            else if ((i & 1) == 1) dl.AddRectFilled(rp, rp + new Vector2(w, RowH), RowAlt);

            // Star (favorite)
            ImGuiNET.ImGui.PushID(i);
            var starPos = rp + new Vector2(2, 1);
            if (IconButton("##fav", PhosphorIcons.Star, starPos, new Vector2(btn, btn), r.Favorite ? Gold : Faded(Mute),
                    r.Favorite ? Selected : PanelBg, enabled: r.OnMap, font: UiFont.Ui11))
                RynthNavCommands.ToggleFavorite(r.Name, r.Ns, r.Ew);
            SafeImGui.SetItemTooltip(r.Favorite ? "Remove from favorites" : "Add to favorites");

            // Arrow / Go (right)
            var goPos = new Vector2(rp.X + w - btn - 2, rp.Y + 1);
            var arPos = goPos - new Vector2(btn + 2, 0);
            if (IconButton("##arrow", PhosphorIcons.NavigationArrow, arPos, new Vector2(btn, btn), Teal, BtnFill, enabled: r.OnMap, font: UiFont.Ui11))
            {
                RynthNavCommands.Arrow(r.Name, r.Ns, r.Ew);
                _sel = r;
            }
            SafeImGui.SetItemTooltip(r.OnMap ? "Point the arrow here" : "In a dungeon: no arrow");
            if (IconButton("##go", PhosphorIcons.Play, goPos, new Vector2(btn, btn), Green, BtnFill, enabled: r.OnMap, font: UiFont.Ui11))
            {
                RynthNavCommands.Go(r.Name, r.Ns, r.Ew);
                _sel = r;
            }
            SafeImGui.SetItemTooltip(r.OnMap ? "Go: RynthNav walks here" : "In a dungeon: RynthNav can't walk there");

            // Row body: click selects
            float bodyX = rp.X + btn + 4, bodyW = arPos.X - 4 - bodyX;
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(bodyX, rp.Y));
            if (ImGuiNET.ImGui.InvisibleButton("##row", new Vector2(Math.Max(1, bodyW), RowH))) _sel = r;
            bool hot = ImGuiNET.ImGui.IsItemHovered();
            ImGuiNET.ImGui.PopID();
            if (hot && !selected) dl.AddRectFilled(new Vector2(bodyX, rp.Y), new Vector2(bodyX + bodyW, rp.Y + RowH), RowHot);

            PhosphorIcons.DrawCentered(dl, ImGuiFonts.Get(UiFont.Ui11), TypeIcon(r.Type), new Vector2(bodyX, rp.Y), new Vector2(iconW, RowH), TypeColor(r.Type));
            float distW = r.Dist.Length > 0 ? f9.CalcTextSizeA(f9.FontSize, float.MaxValue, 0, r.Dist).X + 6 : 0;
            float nameX = bodyX + iconW + 2, nameMax = bodyX + bodyW - distW;
            dl.PushClipRect(new Vector2(nameX, rp.Y), new Vector2(Math.Max(nameX, nameMax - 2), rp.Y + RowH), true);
            dl.AddText(f, f.FontSize, new Vector2(nameX, rp.Y + (RowH - f.FontSize) * 0.5f), r.OnMap ? Text : Mute, r.Name);
            dl.PopClipRect();
            if (distW > 0)
                dl.AddText(f9, f9.FontSize, new Vector2(nameMax, rp.Y + (RowH - f9.FontSize) * 0.5f), Mute, r.Dist);
        }
        ImGuiNET.ImGui.SetCursorScreenPos(basePos);
        ImGuiNET.ImGui.Dummy(new Vector2(w, Math.Max(1, rows.Length * RowH)));
    }

    private static bool SameRow(AtlasRow a, AtlasRow b) =>
        a.Name == b.Name && Math.Abs(a.Ns - b.Ns) < 1e-6 && Math.Abs(a.Ew - b.Ew) < 1e-6 && a.Type == b.Type;

    private void DrawDetails(float w, Vector2 origin, Vector2 size)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        float h = Math.Max(40, origin.Y + size.Y - 6 - p.Y);
        dl.AddRectFilled(p, p + new Vector2(w, h), PanelBg, 3);
        AtlasRow? r = _sel;
        if (r == null)
        {
            dl.AddText(p + new Vector2(6, 6), Mute, "Click a place for its details.");
            NextLine(p, h);
            return;
        }
        NavPlace? place = r.Place;
        ImFontPtr bold = ImGuiFonts.Get(UiFont.UiBold11), f9 = ImGuiFonts.Get(UiFont.Ui9);
        float x = p.X + 6, y = p.Y + 5, right = p.X + w - 6;
        dl.PushClipRect(p, p + new Vector2(w, h), true);
        PhosphorIcons.DrawCentered(dl, ImGuiFonts.Get(UiFont.Ui11), TypeIcon(r.Type), new Vector2(x, y), new Vector2(16, 16), TypeColor(r.Type));
        dl.AddText(bold, bold.FontSize, new Vector2(x + 20, y + 1), Teal, r.Name);
        y += 19;
        if (!ReferenceEquals(_detailsFor, r))
        {
            _detailsFor = r;
            _detailsLine = r.Type + "  ·  " + r.Coord + (place != null && place.LevelText.Length > 0 ? "  ·  " + place.LevelText : "")
                + (place != null && place.Quest ? "  ·  needs a quest flag" : "") + (place != null && place.Closed ? "  ·  closed" : "");
            _detailsDest = place != null && place.HasDest ? PhosphorIcons.Spiral + "  Goes to " + place.DestText : "";
        }
        dl.AddText(new Vector2(x, y), Text, _detailsLine);
        y += 16;
        if (_detailsDest.Length > 0)
        {
            dl.AddText(new Vector2(x, y), Purple, _detailsDest);
            y += 16;
        }
        if (place != null && place.Desc.Length > 0)
        {
            float wrap = right - x;
            dl.AddText(f9, f9.FontSize, new Vector2(x, y), Mute, place.Desc, wrap);
        }
        dl.PopClipRect();

        // Buttons along the bottom.
        float by = p.Y + h - 26;
        float bw = (w - 12 - 3 * 4) / 4f;
        bool on = r.OnMap;
        if (Button("##det_arrow", PhosphorIcons.NavigationArrow + " Arrow", new Vector2(x, by), new Vector2(bw, 22), Teal, BtnFill, enabled: on))
            RynthNavCommands.Arrow(r.Name, r.Ns, r.Ew);
        if (Button("##det_go", PhosphorIcons.Play + " Go", new Vector2(x + (bw + 4), by), new Vector2(bw, 22), Green, BtnFill, enabled: on))
            RynthNavCommands.Go(r.Name, r.Ns, r.Ew);
        if (Button("##det_route", PhosphorIcons.Path + " Route", new Vector2(x + 2 * (bw + 4), by), new Vector2(bw, 22), Text, BtnFill, enabled: on))
        {
            // The plan shows on the Go tab, with this place as its destination.
            RynthNavCommands.Route(r.Name, r.Ns, r.Ew);
            _tab = TabGo;
            Pick(r);
        }
        ImGuiNET.ImGui.SetItemTooltip("Plan a route here (portals and recalls) and show it on the Go tab");
        float grip = ImGuiPanelHost.BodyGripReserve;
        float favW = Math.Max(24, bw - Math.Max(0, grip - 6));
        if (Button("##det_fav", PhosphorIcons.Star + (favW > 60 ? (r.Favorite ? " Unstar" : " Star") : ""), new Vector2(x + 3 * (bw + 4), by),
                new Vector2(favW, 22), r.Favorite ? Gold : Text, BtnFill, enabled: on))
            RynthNavCommands.ToggleFavorite(r.Name, r.Ns, r.Ew);
        NextLine(p, h);
    }

    private static string TypeIcon(string type) => type switch
    {
        "Town" => PhosphorIcons.CastleTurret,
        "Dungeon" => PhosphorIcons.Skull,
        "Portal" => PhosphorIcons.Spiral,
        "Lifestone" => PhosphorIcons.Heartbeat,
        "Bindstone" => PhosphorIcons.Crown,
        "Vendor" => PhosphorIcons.Storefront,
        "NPC" => PhosphorIcons.User,
        "Recall" => PhosphorIcons.MagicWand,
        _ => PhosphorIcons.MapPin,
    };

    private static uint TypeColor(string type) => type switch
    {
        "Town" => Teal,
        "Dungeon" => Red,
        "Portal" => Purple,
        "Lifestone" => Green,
        "Bindstone" => Gold,
        "Vendor" => Amber,
        "NPC" => Amber,
        _ => Mute,
    };

    // ── Arrow ────────────────────────────────────────────────────────────

    private void DrawArrow(float w)
    {
        RynthNavStatus? st = _st;
        if (st != null && !st.Travel) { Wrapped("This RynthNav is older than the arrow (0.6.0): update RynthNav.", Amber, w); return; }
        _read.Update(st);
        RynthNavArrow? a = st?.Arrow;
        var dl = ImGuiNET.ImGui.GetWindowDrawList();

        // The arrow and its readout
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        const float d = 76;
        Vector2 c = p + new Vector2(d * 0.5f, d * 0.5f);
        dl.AddCircleFilled(c, d * 0.5f, PanelBg, 40);
        dl.AddCircle(c, d * 0.5f, Sep, 40, 1.5f);
        _read.DrawPointer(dl, c, d * 0.5f - 8);
        float tx = p.X + d + 10;
        ImFontPtr bold = ImGuiFonts.Get(UiFont.UiBold14), f9 = ImGuiFonts.Get(UiFont.Ui9);
        dl.PushClipRect(new Vector2(tx, p.Y), new Vector2(p.X + w, p.Y + d), true);
        dl.AddText(bold, bold.FontSize, new Vector2(tx, p.Y + 4), Teal, _read.Line1);
        dl.AddText(new Vector2(tx, p.Y + 26), Text, _read.Line2);
        dl.AddText(new Vector2(tx, p.Y + 44), Mute, _read.Line3);
        if (a != null && a.ThenText.Length > 0)
            dl.AddText(f9, f9.FontSize, new Vector2(tx, p.Y + 60), Mute, a.ThenText);
        dl.PopClipRect();
        NextLine(p, d + 6);

        // Buttons
        p = ImGuiNET.ImGui.GetCursorScreenPos();
        bool shown = ImGuiPanelHost.IsOpen(RynthNavArrowFace.Title);
        float bw = (w - 4 * 4) / 5f;
        if (Button("##ar_show", (shown ? PhosphorIcons.Eye + " Hide" : PhosphorIcons.Eye + " Show"), p, new Vector2(bw, 24),
                shown ? Teal : Text, shown ? Selected : BtnFill, border: shown ? Teal : 0))
        {
            if (shown) ImGuiPanelHost.Close(RynthNavArrowFace.Title);
            else ImGuiPanelHost.Open(RynthNavArrowFace.Title);
        }
        ImGuiNET.ImGui.SetItemTooltip("Show or hide the small arrow overlay (drag it anywhere; it pops out too)");
        bool has = a != null;
        if (Button("##ar_go", GoLabel, new Vector2(p.X + (bw + 4), p.Y), new Vector2(bw, 24), Green, BtnFill, enabled: has) && a != null)
            RynthNavCommands.Go(a.Final, a.FinalNs, a.FinalEw);
        if (Button("##ar_stop", PhosphorIcons.Stop + " Stop", new Vector2(p.X + 2 * (bw + 4), p.Y), new Vector2(bw, 24), Red, BtnFill))
            RynthNavCommands.Move(5);
        if (Button("##ar_clear", PhosphorIcons.X + " Clear", new Vector2(p.X + 3 * (bw + 4), p.Y), new Vector2(bw, 24), Text, BtnFill, enabled: has))
            RynthNavCommands.Command("arrow off");
        if (Button("##ar_fav", PhosphorIcons.Star + " Star", new Vector2(p.X + 4 * (bw + 4), p.Y), new Vector2(bw, 24), Gold, BtnFill, enabled: has) && a != null)
            RynthNavCommands.ToggleFavorite(a.Final, a.FinalNs, a.FinalEw);
        ImGuiNET.ImGui.SetItemTooltip("Add the target to favorites (or take it off)");
        NextLine(p, 30);
        Line(w);

        // Set the arrow
        SectionLabel("POINT THE ARROW AT");
        p = ImGuiNET.ImGui.GetCursorScreenPos();
        float sw = ButtonWidth("Set"), rw = ButtonWidth(PhosphorIcons.Path + " Via route");
        float boxW = Math.Max(80, w - sw - rw - 8);
        bool enter = TextBox("##ar_to", _arrowTo, p, boxW, "Holtburg, or 42.1N, 33.6E", out _, ImGuiInputTextFlags.EnterReturnsTrue);
        string to = Utf8(_arrowTo).Trim();
        if ((Button("##ar_set", "Set", new Vector2(p.X + boxW + 4, p.Y), new Vector2(sw, 22), Text, BtnFill, enabled: to.Length > 0) || enter) && to.Length > 0)
            RynthNavCommands.Command("arrow " + to);
        if (Button("##ar_route", PhosphorIcons.Path + " Via route", new Vector2(p.X + boxW + 8 + sw, p.Y), new Vector2(rw, 22), Text, BtnFill, enabled: to.Length > 0))
            RynthNavCommands.Command("arrow route " + to);
        ImGuiNET.ImGui.SetItemTooltip("Point along a planned route: each portal and recall, then the place");
        NextLine(p, 28);

        // Arrival radius
        p = ImGuiNET.ImGui.GetCursorScreenPos();
        double arrive = a?.ArriveYards > 0 ? a.ArriveYards : 10;
        if (arrive != _arriveShown)
        {
            _arriveShown = arrive;
            _arriveText = "Arrives within " + arrive.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " yd";
        }
        dl.AddText(p + new Vector2(0, 4), Mute, _arriveText);
        float ax = p.X + ImGuiNET.ImGui.CalcTextSize("Arrives within 000 yd").X + 8;
        if (IconButton("##ar_less", PhosphorIcons.Minus, new Vector2(ax, p.Y), new Vector2(22, 22), Text, BtnFill, enabled: has && arrive > 4, font: UiFont.Ui11))
            RynthNavCommands.Command("arrive " + Math.Max(3, arrive - 5).ToString("0", System.Globalization.CultureInfo.InvariantCulture));
        if (IconButton("##ar_more", PhosphorIcons.Plus, new Vector2(ax + 26, p.Y), new Vector2(22, 22), Text, BtnFill, enabled: has && arrive < 150, font: UiFont.Ui11))
            RynthNavCommands.Command("arrive " + Math.Min(150, arrive + 5).ToString("0", System.Globalization.CultureInfo.InvariantCulture));
        NextLine(p, 28);
        Line(w);
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui9));
        Wrapped("Also: the Atlas tab's " + PhosphorIcons.NavigationArrow + " on any place; right-click coordinates in chat; "
            + "/rnav arrow <place or coords>; /rnav arrow off.", Mute, w - ImGuiPanelHost.BodyGripReserve);
        ImGuiNET.ImGui.PopFont();
    }

    // ── Shared bits ──────────────────────────────────────────────────────

    private static void SectionLabel(string text)
    {
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.UiBold11));
        Label(text, Teal);
        ImGuiNET.ImGui.PopFont();
    }

    private static void Wrapped(string text, uint color, float w)
    {
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGuiNET.ImGui.PushTextWrapPos(ImGuiNET.ImGui.GetCursorPosX() + w);
        ImGuiNET.ImGui.TextUnformatted(text);
        ImGuiNET.ImGui.PopTextWrapPos();
        ImGuiNET.ImGui.PopStyleColor();
    }

    private static void CenteredHint(string text, float w)
    {
        ImFontPtr f9 = ImGuiFonts.Get(UiFont.Ui9);
        ImGuiNET.ImGui.PushFont(f9);
        float tw = ImGuiNET.ImGui.CalcTextSize(text).X;
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        ImGuiNET.ImGui.GetWindowDrawList().AddText(new Vector2(p.X + Math.Max(0, (w - tw) * 0.5f), p.Y), Mute, text);
        ImGuiNET.ImGui.PopFont();
        NextLine(p, f9.FontSize + 4);
    }

    private static void Line(float w)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        ImGuiNET.ImGui.GetWindowDrawList().AddLine(p + new Vector2(0, 3), p + new Vector2(w, 3), Sep);
        ImGuiNET.ImGui.Dummy(new Vector2(w, 6));
    }
}
