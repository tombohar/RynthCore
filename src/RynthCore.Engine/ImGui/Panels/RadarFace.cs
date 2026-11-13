// ============================================================================
//  RynthCore.Engine - ImGui/Panels/RadarFace.cs
//  ImGui face of the Radar (docs/IMGUI_PARITY_PLAN.md §2.2), drawn the way
//  the Avalonia face's RadarSurface.Render does: gold rim, canvas, crosshair,
//  floors (current layer full, the one above and below at 30%) with slope
//  tints, walls (blue when visited), visited overlay, markers, player arrow,
//  outlined compass on the playfield perimeter, coordinate strip. Top-right
//  pop-out and settings (gear) buttons, wheel zoom, left-drag moves, left-click on a dot
//  selects it in game by object id (hover: name, distance, health), right-click menu, settings
//  popup. Settings are shared with the Avalonia face (RadarSettingsStore).
//  Marker kinds (RadarKind) each have a shape (RadarMarkerStyle), a show box,
//  a colour the user can pick, a size and an optional name label with its own
//  text size (RadarSettingsStore.Markers); the settings popup doubles as the
//  legend. Your own arrow, the compass letters and the coordinates have sizes too.
//  Outdoors the World Map's picture can be drawn behind it (RadarSettingsStore.ShowWorldMap; the
//  tiles come from WorldMapTiles through WorldMapTileDrawer, the same code as the World Map).
//  The object selected in game is highlighted (RadarSettingsStore.HighlightSelected, RadarSelection):
//  a ring in its own colour round its marker and the hover label beside it, or an arrow on the
//  edge when it is off the picture.
//  Range rings round you at RynthAi's Monster Range and Ring Range (RadarRangeRing; each has a box in
//  the settings), read from RynthAi's settings while one is on.
//
//  Data: UiSources.Radar (RadarView, lookups precomputed). World geometry is
//  drawn without anti-aliasing (crisp tiling like the Avalonia face's aliased
//  mode, and far fewer vertices for big dungeons); no per-frame allocation.
// ============================================================================

using System;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.UI;
using RynthCore.Engine.UI.Data;
using RynthCore.Engine.UI.Panels;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed class RadarFace : IImGuiPanel
{
    public const string Title = "Radar";

    public static void Register()
    {
        ImGuiPanelHost.Register(Title,
            new PanelSpec(new Vector2(320, 320), new Vector2(120, 120), PanelChrome.None,
                Background: 0, Rounding: 0, EdgeToEdge: true, GoldGrip: true),
            () => new RadarFace());
        // The Inventory panel (engine data only, like the radar): registered here so
        // engine init needs no new line in EntryPoint (as LootEditor / DungeonMap
        // register from RynthAiFace). Opened from the bar and /rc inv.
        InventoryFace.Register();
    }

    // ── Palette (RadarPanel's, ARGB) ────────────────────────────────────
    private const uint FrameOuterArgb = 0xFF0A0D14, CanvasBgArgb = 0xFF0A121A;
    private static readonly uint FrameInner = C(0xFF9E853D), FrameAccent = C(0xFFE6C766), Crosshair = C(0x994D6680),
        Cardinal = C(0xFFFFFFFF), CoordText = C(0xFFF2F8FF), Outline = C(0xF2000000), PlayerCol = C(0xFF33FF66);
    private static uint ButtonBg => RynthTheme.WithAlpha(RynthTheme.Field, 0xC0);
    private static uint ButtonBorder => RynthTheme.Border;
    // Marker colours per kind (RadarSettingsStore.KindColors, user-pickable), refreshed each frame.
    private static readonly uint[] KindCols = new uint[RadarKind.Count];
    private const uint WallUnseen = 0xFFFFFFFF, WallVisited = 0xFF4099FF, FloorFlat = 0x8C2E8C38,
        FloorUp = 0x668C1F14, FloorDown = 0x661A8026, FloorVisited = 0xA64D6B94;
    private static uint C(uint argb) => RynthTheme.Argb(argb);

    /// <summary>ARGB colour with its alpha scaled, as an ImGui colour.</summary>
    private static uint MulAlpha(uint argb, float k) =>
        RynthTheme.Argb((argb & 0x00FFFFFF) | ((uint)Math.Clamp((int)MathF.Round((argb >> 24) * k), 0, 255) << 24));

    private static readonly (string Label, float WorldX, float WorldY)[] Compass =
        { ("N", 0, 1), ("E", 1, 0), ("S", 0, -1), ("W", -1, 0) };

    private bool _settingsOpen;
    private bool _settingsSub;                       // subscribed to RynthAi's settings (the retail radar switch) while the window is open
    private bool? _hidePending;                      // the retail-radar switch as just set, until RynthAi's answer shows it
    private long _hidePendingAt;
    private bool _rangeSub;                          // subscribed to RynthAi's settings while a range ring is on

    // The round radar (RadarSettingsStore.RoundRadar): this frame's circle, read by the drawing helpers.
    private static bool _round;
    private static RoundMask _mask;
    // The selected object's marker this frame (RadarSelection.Highlighted), or null; drawn over everything.
    private static RadarMarker? _sel;

    /// <summary>Markers move: popped out, redraw smoothly even when idle.</summary>
    public int PopOutIdleHz => 20;

    // Docked click-through: the panel takes no input (the game gets the clicks) unless Ctrl is held or
    // the mouse is over a dot or one of the buttons (_hot, worked out by the last frame's Draw), so a
    // dot can always be hovered and clicked. A press that began on a dot keeps the panel interactive
    // until the button is released.
    private bool _hot, _pressHot, _downPrev;
    // Whether the current press on the surface may move the panel (off for a dot press under click-through).
    private readonly RadarDrag _drag = new();

    public bool ClickThrough =>
        RadarSettingsStore.CtrlGatedClickThrough && !ImGuiNET.ImGui.GetIO().KeyCtrl && !_hot;

    // Click-through with "Mouse wheel still zooms": the mouse was on the radar this frame, so the
    // wheel is the radar's (RadarWheel); the engine keeps it from the game.
    private bool _wheelHere;
    public bool ClickThroughWantsWheel => _wheelHere;

    public void OnShown()
    {
        RadarSettingsStore.Load();
        UiSources.Radar.Subscribe();
        UiSources.Radar.RequestRefresh();
    }

    public void OnHidden()
    {
        UiSources.Radar.Unsubscribe();
        if (_settingsSub) { UiSources.Settings.Unsubscribe(); _settingsSub = false; }
        if (_rangeSub) { UiSources.Settings.Unsubscribe(); _rangeSub = false; }
    }

    public void Draw()
    {
        // The range rings need RynthAi's settings (Monster Range, Ring Range): polled only while one is on.
        bool wantRange = RadarSettingsStore.ShowMonsterRangeRing || RadarSettingsStore.ShowRingRangeRing;
        if (wantRange != _rangeSub)
        {
            if (wantRange) { UiSources.Settings.Subscribe(); UiSources.Settings.RequestRefresh(); }
            else UiSources.Settings.Unsubscribe();
            _rangeSub = wantRange;
        }
        bool clickThroughNow = ClickThrough;   // what the host gave this frame's window (before UpdateHot moves _hot)
        _wheelHere = false;
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        Vector2 origin = ImGuiNET.ImGui.GetCursorScreenPos();
        Vector2 size = ImGuiNET.ImGui.GetContentRegionAvail();
        if (size.X < 16 || size.Y < 16) return;
        RadarView? view = UiSources.Radar.Current?.Value;

        bool rim = RadarSettingsStore.ShowGoldRim;
        float canvasInset = rim ? 5 : 0;
        float opacity = Math.Clamp(RadarSettingsStore.Opacity, 0f, 1f);
        // The coordinates sit in a strip of their own under the radar, clear of its frame: the text's height
        // (its size at the UI scale) and a gap above it. The radar - frame, canvas, dial - is everything above.
        float coordH = ImGuiFonts.Get(UiFont.UiBold11).FontSize * RadarSettingsStore.CoordSize / 11f;
        float coordStrip = coordH + 5f;
        Vector2 panelMax = origin + size;
        Vector2 frameMax = new Vector2(panelMax.X, MathF.Max(origin.Y + 16f, panelMax.Y - coordStrip));

        // ── Canvas and world playfield ──
        Vector2 canvasMin = origin + new Vector2(canvasInset, canvasInset);
        Vector2 canvasMax = frameMax - new Vector2(canvasInset, canvasInset);
        var worldMin = canvasMin;
        var worldMax = canvasMax;
        Vector2 centre = (worldMin + worldMax) * 0.5f;
        // Round: the circle (frame included) is as wide as the radar is tall or wide, whichever is less.
        _round = RadarSettingsStore.RoundRadar;
        float frameR = MathF.Max(10f, MathF.Min(frameMax.X - origin.X, frameMax.Y - origin.Y) * 0.5f);
        float dial = MathF.Max(8f, frameR - canvasInset);
        if (_round)
        {
            centre = (origin + frameMax) * 0.5f;
            worldMin = centre - new Vector2(dial, dial);
            worldMax = centre + new Vector2(dial, dial);
        }
        _mask = new RoundMask(centre, dial);

        if (_round)
        {
            if (rim)
            {
                dl.AddCircleFilled(centre, frameR, MulAlpha(FrameOuterArgb, opacity), 72);
                dl.AddCircle(centre, frameR - 1, FrameInner, 72, 2);
                dl.AddCircle(centre, frameR - 3, FrameAccent, 72, 1);
            }
            dl.AddCircleFilled(centre, dial, MulAlpha(CanvasBgArgb, opacity), 72);
            dl.AddLine(new Vector2(centre.X, centre.Y - dial), new Vector2(centre.X, centre.Y + dial), Crosshair);
            dl.AddLine(new Vector2(centre.X - dial, centre.Y), new Vector2(centre.X + dial, centre.Y), Crosshair);
        }
        else
        {
            // ── Frame: outer black (fades) + gold inner border + accent ──
            if (rim)
            {
                dl.AddRectFilled(origin, frameMax, MulAlpha(FrameOuterArgb, opacity));
                dl.AddRect(origin + new Vector2(1, 1), frameMax - new Vector2(1, 1), FrameInner, 0, ImDrawFlags.None, 2);
                dl.AddRect(origin + new Vector2(3, 3), frameMax - new Vector2(3, 3), FrameAccent, 0, ImDrawFlags.None, 1);
            }
            dl.AddRectFilled(canvasMin, canvasMax, MulAlpha(CanvasBgArgb, opacity));
            dl.AddLine(new Vector2(centre.X, worldMin.Y), new Vector2(centre.X, worldMax.Y), Crosshair);
            dl.AddLine(new Vector2(worldMin.X, centre.Y), new Vector2(worldMax.X, centre.Y), Crosshair);
        }

        // The game's current selection (the global AC's SetSelectedObject writes; one read a frame).
        uint selectedId = view != null && RadarSettingsStore.HighlightSelected && Compatibility.SelectedTargetHooks.IsInstalled
            ? Compatibility.SelectedTargetHooks.ReadCurrentSelectedId() : 0;
        _sel = view != null ? RadarSelection.Highlighted(RadarSettingsStore.HighlightSelected, view.Live.Markers, selectedId) : null;

        if (view != null)
        {
            ImDrawListFlags saved = dl.Flags;
            dl.Flags = saved & ~(ImDrawListFlags.AntiAliasedLines | ImDrawListFlags.AntiAliasedLinesUseTex | ImDrawListFlags.AntiAliasedFill);
            dl.PushClipRect(worldMin, worldMax, true);
            DrawWorld(dl, view, centre, worldMin, worldMax);
            dl.PopClipRect();
            dl.Flags = saved;
            DrawSelected(dl, view, centre, worldMin, worldMax, origin, panelMax);

            DrawOutlinedText(dl, ImGuiFonts.Get(UiFont.UiBold11), view.CoordText,
                new Vector2((origin.X + panelMax.X) * 0.5f, panelMax.Y), CoordText, alignBottom: true,
                scale: RadarSettingsStore.CoordSize / 11f);
            DrawCardinals(dl, view, centre, (worldMax.X - worldMin.X) * 0.5f, (worldMax.Y - worldMin.Y) * 0.5f);
        }

        // ── Buttons (submitted before the surface so they win the hover) ──
        // Small and flat, like the Arrow's corner buttons: settings, pop out, close.
        const float btn = 14, gap = 2, inset = 3;
        uint btnFg = FaceKit.Faded(FaceKit.Mute), btnBg = RynthTheme.WithAlpha(RynthTheme.Background, 0xDC);
        var closePos = new Vector2(frameMax.X - inset - btn, origin.Y + inset);
        var popPos = closePos - new Vector2(btn + gap, 0);
        var gearPos = (PanelRouter.CanPopOut ? popPos : closePos) - new Vector2(btn + gap, 0);
        if (FaceKit.IconButton("##radar_close", PhosphorIcons.X, closePos, new Vector2(btn, btn), btnFg, btnBg, font: UiFont.Ui9))
            ImGuiPanelHost.Close(Title);
        ImGuiNET.ImGui.SetItemTooltip("Close the radar");
        if (PanelRouter.CanPopOut)
        {
            if (FaceKit.IconButton("##radar_pop", ImGuiPanelHost.PopOutGlyph, popPos, new Vector2(btn, btn), btnFg, btnBg, font: UiFont.Ui9))
                ImGuiPanelHost.TogglePopOut(Title);
            SafeImGui.SetItemTooltip(ImGuiPanelHost.PopOutTooltip);
        }
        bool gear = FaceKit.IconButton("##radar_gear", PhosphorIcons.Gear, gearPos, new Vector2(btn, btn), btnFg, btnBg, font: UiFont.Ui9);
        ImGuiNET.ImGui.SetItemTooltip("Radar settings");
        if (gear) ToggleSettings(new Vector2(closePos.X + btn, closePos.Y + btn + 4));

        // ── Surface: drag moves the panel, a click on a dot selects, wheel zooms, right-click menu ──
        ImGuiNET.ImGui.SetCursorScreenPos(origin);
        bool surfaceClicked = ImGuiNET.ImGui.InvisibleButton("##radar_surface", size);
        // 2026-11-04: with docked click-through, a press on a dot only selects it; it never moves the
        // radar unless Ctrl was held when it began (RadarDrag; holding on a dot used to drag the panel).
        if (_drag.Update(ImGuiNET.ImGui.IsItemActivated(), RadarSettingsStore.CtrlGatedClickThrough,
                ImGuiNET.ImGui.GetIO().KeyCtrl, ImGuiPopOuts.InPopOutFrame))
            ImGuiPanelHost.DragWindowWithLastItem();
        UpdateHot(view, centre, worldMin, worldMax, gearPos, new Vector2(closePos.X + btn, closePos.Y + btn));
        if (view != null && RadarSettingsStore.ClickToSelect)
            MarkerInput(dl, view, centre, worldMin, worldMax, surfaceClicked);
        // Click-through: the window takes no input, so it is never "hovered"; the wheel is read here
        // instead when the option says so (RadarWheel.Owns).
        var wio = ImGuiNET.ImGui.GetIO();
        _wheelHere = RadarWheel.Owns(RadarSettingsStore.ClickThroughWheelZoom, clickThroughNow, ImGuiPopOuts.InPopOutFrame,
            wio.WantCaptureMouse, wio.MousePos, origin, frameMax, _round ? new RoundMask(centre, frameR) : null);
        if (ImGuiNET.ImGui.IsItemHovered() || _wheelHere)
        {
            float wheel = wio.MouseWheel;
            if (wheel != 0)
            {
                float next = RadarWheel.Zoomed(RadarSettingsStore.Zoom, wheel);
                if (next != RadarSettingsStore.Zoom) { RadarSettingsStore.Zoom = next; RadarSettingsStore.Save(); }
            }
        }
        if (ImGuiNET.ImGui.BeginPopupContextItem("##radar_menu"))
        {
            if (PanelRouter.CanPopOut && ImGuiNET.ImGui.MenuItem(ImGuiPopOuts.InPopOutFrame ? "Dock back" : "Pop out"))
                ImGuiPanelHost.TogglePopOut(Title);
            if (ImGuiNET.ImGui.MenuItem("Radar settings...")) ToggleSettings(ImGuiNET.ImGui.GetMousePos());
            ImGuiNET.ImGui.Separator();
            if (ImGuiNET.ImGui.MenuItem("Rotate with player", "", RadarSettingsStore.RotateWithPlayer))
            {
                RadarSettingsStore.RotateWithPlayer = !RadarSettingsStore.RotateWithPlayer;
                RadarSettingsStore.Save();
            }
            ImGuiPanelHost.TextSizeMenu(Title);
            ImGuiNET.ImGui.Separator();
            if (ImGuiNET.ImGui.MenuItem("Close"))
                ImGuiPanelHost.Close(Title);
            ImGuiNET.ImGui.EndPopup();
        }

        SettingsWindow();
    }

    /// <summary>Opens the settings window with its top-right corner at <paramref name="corner"/>, or closes it.</summary>
    private void ToggleSettings(Vector2 corner)
    {
        if (_settingsOpen) { _settingsOpen = false; return; }
        _settingsOpen = true;
        ImGuiNET.ImGui.SetNextWindowPos(corner, ImGuiCond.Always, new Vector2(1, 0));
    }

    // ── World ─────────────────────────────────────────────────────────────

    /// <summary>World (AC x east, y north) → screen, north-up or rotated so facing is up.</summary>
    private readonly struct Projection
    {
        private readonly float _cx, _cy, _pwx, _pwy, _sin, _cos, _zoom;
        private readonly bool _rotate;

        public Projection(Vector2 centre, RadarPlayer p, bool rotate, float zoom)
        {
            _cx = centre.X; _cy = centre.Y; _pwx = p.WorldX; _pwy = p.WorldY;
            float h = p.Heading * MathF.PI / 180f; // AC: 0 = east, 90 = north
            _sin = MathF.Sin(h); _cos = MathF.Cos(h);
            _rotate = rotate; _zoom = zoom;
        }

        public float Sin => _sin;
        public float Cos => _cos;

        public Vector2 P(float wx, float wy)
        {
            float dx = wx - _pwx, dy = wy - _pwy;
            if (_rotate)
            {
                float right = _sin * dx - _cos * dy;
                float forward = _cos * dx + _sin * dy;
                return new Vector2(_cx + right * _zoom, _cy - forward * _zoom);
            }
            return new Vector2(_cx + dx * _zoom, _cy - dy * _zoom);
        }
    }

    private static void DrawWorld(ImDrawListPtr dl, RadarView view, Vector2 centre, Vector2 clipMin, Vector2 clipMax)
    {
        RadarSnapshot live = view.Live;
        bool rotate = RadarSettingsStore.RotateWithPlayer;
        var proj = new Projection(centre, live.Player, rotate, MathF.Max(0.5f, RadarSettingsStore.Zoom));

        if (RadarSettingsStore.ShowWorldMap && !live.IsIndoor)
            DrawWorldMapBackdrop(dl, live, centre, clipMin, clipMax, rotate);

        int cur = view.CurrentLayer, count = live.LayerZs.Count;
        // Same order as the Avalonia/ImGui-era radar: below, above (dimmed), current.
        if (cur - 1 >= 0) DrawLayer(dl, view, cur - 1, 0.30f, false, proj, rotate, clipMin, clipMax);
        if (cur + 1 < count && cur >= 0) DrawLayer(dl, view, cur + 1, 0.30f, false, proj, rotate, clipMin, clipMax);
        if (cur >= 0) DrawLayer(dl, view, cur, 1.00f, true, proj, rotate, clipMin, clipMax);

        DrawRangeRings(dl, centre, clipMin, clipMax, MathF.Max(0.5f, RadarSettingsStore.Zoom));

        // Markers (shapes: RadarMarkerStyle). Corpses and items first, creatures on top.
        RadarMarkerStyle.FillColors(KindCols);
        MarkerStyleSettings markers = RadarSettingsStore.Markers;
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (RadarMarker m in live.Markers)
            {
                if (m == _sel || !RadarSettingsStore.ShowKind(m.Kind)) continue;   // the selected one: DrawSelected
                if ((pass == 1) != (RadarKind.IsCreature(m.Kind) || m.Kind is RadarKind.Portal or RadarKind.Door)) continue;
                Vector2 pp = proj.P(m.X, m.Y);
                if (_round ? !_mask.Contains(pp) : (pp.X < clipMin.X || pp.X > clipMax.X || pp.Y < clipMin.Y || pp.Y > clipMax.Y)) continue;
                RadarMarkerStyle.Draw(dl, m.Kind, pp, KindCols[m.Kind], markers.SizeOf(m.Kind));
            }
        }

        // Names last, over every marker.
        bool anyName = false;
        for (byte k = 0; k < RadarKind.Count; k++) anyName |= markers.ShowName[k] && RadarSettingsStore.ShowKind(k);
        if (anyName)
        {
            ImFontPtr nameFont = ImGuiFonts.Get(UiFont.UiBold11);
            foreach (RadarMarker m in live.Markers)
            {
                if (!markers.NameShown(m.Kind) || !RadarSettingsStore.ShowKind(m.Kind)) continue;
                if (m == _sel && RadarSettingsStore.ShowSelectedLabel) continue;   // its label has the name
                Vector2 pp = proj.P(m.X, m.Y);
                if (pp.X < clipMin.X - 8 || pp.X > clipMax.X || pp.Y < clipMin.Y - 8 || pp.Y > clipMax.Y + 8) continue;
                if (_round && !_mask.Contains(pp)) continue;
                RadarMarkerStyle.DrawName(dl, nameFont, pp, RadarMarkerStyle.Radius(m.Kind) * markers.SizeOf(m.Kind),
                    RadarMarkerStyle.NameColor(KindCols[m.Kind]), m.Label, markers.NameSizeOf(m.Kind), _round ? _mask : null);
            }
        }

        // Player dot + 10 px heading arrow (always up when rotating).
        float me = markers.SelfSize;
        dl.AddCircleFilled(centre, 3.5f * me, PlayerCol, 10);
        Vector2 tip = rotate ? centre + new Vector2(0, -10 * me) : centre + new Vector2(proj.Cos * 10 * me, -proj.Sin * 10 * me);
        dl.AddLine(centre, tip, PlayerCol, 2);
    }

    // ── Range rings ────────────────────────────────────────────────────────
    // Thin circles round you at RynthAi's Monster Range (warm white) and Ring Range (pale blue), over
    // the floors and under the markers, each on a faint dark line so it reads on the dark canvas and
    // on the world map alike. Nothing until RynthAi has sent its settings (UiSources.Settings).
    private static readonly uint MonsterRangeCol = C(0xB3FFE6B3), RingRangeCol = C(0xA680CCFF), RangeUnder = C(0x59000000);

    private static void DrawRangeRings(ImDrawListPtr dl, Vector2 centre, Vector2 clipMin, Vector2 clipMax, float zoom)
    {
        bool monster = RadarSettingsStore.ShowMonsterRangeRing, ring = RadarSettingsStore.ShowRingRangeRing;
        if (!monster && !ring) return;
        RynthAiSettings? ra = UiSources.Settings.Current?.Value;
        if (ra == null) return;
        ImDrawListFlags saved = dl.Flags;
        dl.Flags = saved | ImDrawListFlags.AntiAliasedLines;   // the world is drawn aliased; a circle looks ragged that way
        RoundMask? round = _round ? _mask : null;
        if (monster) RangeRing(dl, centre, clipMin, clipMax, round, RadarRangeRing.RadiusPx(ra.MonsterRange, zoom), MonsterRangeCol);
        if (ring) RangeRing(dl, centre, clipMin, clipMax, round, RadarRangeRing.RadiusPx(ra.RingRange, zoom), RingRangeCol);
        dl.Flags = saved;
    }

    private static void RangeRing(ImDrawListPtr dl, Vector2 centre, Vector2 clipMin, Vector2 clipMax, RoundMask? round, float r, uint col)
    {
        if (!RadarRangeRing.Shows(r, centre, clipMin, clipMax, round)) return;
        dl.AddCircle(centre, r, RangeUnder, 0, 3f);   // 0 segments: ImGui picks enough for the radius
        dl.AddCircle(centre, r, col, 0, 1.25f);
    }

    // ── The selected object ────────────────────────────────────────────────
    // A ring in the selection colour round its marker (the marker drawn over everything, even when its
    // kind's box is off), and the hover label beside it; off the picture, an arrow on the edge pointing
    // to it with the label next to the arrow. Drawn anti-aliased, after the world.

    private const float ArrowInset = 3f, ArrowLength = 9f, ArrowHalfWidth = 5f;
    private readonly TipCache _selTip = new();

    private void DrawSelected(ImDrawListPtr dl, RadarView view, Vector2 centre, Vector2 clipMin, Vector2 clipMax,
        Vector2 panelMin, Vector2 panelMax)
    {
        RadarMarker? sel = _sel;
        if (sel == null) return;
        var proj = new Projection(centre, view.Live.Player, RadarSettingsStore.RotateWithPlayer, MathF.Max(0.5f, RadarSettingsStore.Zoom));
        Vector2 pp = proj.P(sel.X, sel.Y);
        uint col = RynthTheme.Argb(RadarSettingsStore.SelectedColor);
        Vector2 anchor;
        float gap;
        dl.PushClipRect(clipMin, clipMax, true);
        if (RadarSelection.EdgeArrow(centre, pp, clipMin, clipMax, _round ? _mask : null, ArrowInset, out Vector2 tip, out Vector2 dir))
        {
            RadarSelection.ArrowCorners(tip, dir, ArrowLength, ArrowHalfWidth, out Vector2 a, out Vector2 b, out Vector2 c);
            dl.AddTriangleFilled(a, b, c, col);
            dl.AddTriangle(a, b, c, Outline, 1.5f);
            anchor = tip - dir * (ArrowLength * 0.5f);
            gap = ArrowLength;
        }
        else
        {
            float size = RadarSettingsStore.Markers.SizeOf(sel.Kind);
            float r = MathF.Max(6.5f, RadarMarkerStyle.Radius(sel.Kind) * size + 3f);
            dl.AddCircle(pp, r, Outline, 20, 3.5f);
            RadarMarkerStyle.Draw(dl, sel.Kind, pp, KindCols[sel.Kind], size);
            dl.AddCircle(pp, r, col, 20, 2f);
            anchor = pp;
            gap = r + 4f;
        }
        dl.PopClipRect();
        if (!RadarSettingsStore.ShowSelectedLabel) return;

        // The hover label, as a tooltip-styled box kept inside the panel.
        string text = _selTip.Get(view, sel);
        var style = ImGuiNET.ImGui.GetStyle();
        Vector2 pad = new Vector2(6f, 4f);
        Vector2 box = ImGuiNET.ImGui.CalcTextSize(text) + pad * 2f;
        Vector2 at = RadarSelection.LabelPos(anchor, gap, box, panelMin, panelMax);
        dl.AddRectFilled(at, at + box, ImGuiNET.ImGui.GetColorU32(ImGuiCol.PopupBg), style.PopupRounding);
        dl.AddRect(at, at + box, col, style.PopupRounding, ImDrawFlags.None, 1f);
        dl.AddText(at + pad, ImGuiNET.ImGui.GetColorU32(ImGuiCol.Text), text);
    }

    // ── World map backdrop ─────────────────────────────────────────────────
    // The radar's world coordinates are yards from the world's south-west corner; the World Map's cells
    // are 24 yards, so cell = yards / 24 on both axes, and the radar's zoom (px per yard) is 24x that
    // in px per cell. The rotation is the Mini Map's (the way you face is up).

    private static readonly WorldMapTileDrawer Backdrop = new();
    private static long _mapsAskedAtMs;

    private static void DrawWorldMapBackdrop(ImDrawListPtr dl, RadarSnapshot live, Vector2 centre, Vector2 clipMin, Vector2 clipMax,
        bool rotate)
    {
        if (!WorldMapTiles.IsOpen)
        {
            // Find and open the picture (a pool thread does the file work); asked again every few seconds
            // while it isn't open, which also covers the switch being turned on later.
            long now = Environment.TickCount64;
            if (now - _mapsAskedAtMs > 5000)
            {
                _mapsAskedAtMs = now;
                WorldMapSettings.Load();
                WorldMapMaps.Refresh();
            }
            return;
        }
        float alpha = Math.Clamp(RadarSettingsStore.WorldMapOpacity, 0.1f, 1f) * Math.Clamp(RadarSettingsStore.Opacity, 0f, 1f);
        uint tint = ((uint)(alpha * 255f + 0.5f) << 24) | 0x00FFFFFFu;
        float phi = rotate ? MapProjection.PhiForHeading(live.Player.Heading) : 0f;
        Backdrop.Draw(dl, clipMin, clipMax, centre, live.Player.WorldX / 24.0, live.Player.WorldY / 24.0,
            MathF.Max(0.5f, RadarSettingsStore.Zoom) * 24f, phi, tint, _round ? _mask : null);
    }

    private static void DrawLayer(ImDrawListPtr dl, RadarView view, int layer, float a, bool current,
        in Projection proj, bool rotate, Vector2 clipMin, Vector2 clipMax)
    {
        // Floor strips: 5 floats (x0, y0, x1, y1, type).
        if (view.FillsByLayer[layer] is float[] strips)
        {
            uint flat = MulAlpha(FloorFlat, a), up = MulAlpha(FloorUp, a), down = MulAlpha(FloorDown, a);
            for (int i = 0; i + 4 < strips.Length; i += 5)
            {
                int type = (int)strips[i + 4];
                uint col = type == 1 ? up : type == 2 ? down : flat;
                FillRect(dl, proj, rotate, strips[i], strips[i + 1], strips[i + 2], strips[i + 3], col, clipMin, clipMax);
            }
        }

        // Walls: 6 floats (ax, ay, bx, by, cell x, cell y); visited tint on the current layer.
        if (view.WallsByLayer[layer] is float[] segs)
        {
            uint unseen = MulAlpha(WallUnseen, a), visited = MulAlpha(WallVisited, a);
            float thick = current ? 1.5f : 1.0f;
            bool[]? flags = current ? view.CurrentWallVisited : null;
            for (int i = 0, w = 0; i + 5 < segs.Length; i += 6, w++)
            {
                Vector2 pa = proj.P(segs[i], segs[i + 1]);
                Vector2 pb = proj.P(segs[i + 2], segs[i + 3]);
                if (_round)
                {
                    if (!CircleClip.Segment(_mask, ref pa, ref pb)) continue;
                }
                else if (MathF.Max(pa.X, pb.X) < clipMin.X || MathF.Min(pa.X, pb.X) > clipMax.X ||
                    MathF.Max(pa.Y, pb.Y) < clipMin.Y || MathF.Min(pa.Y, pb.Y) > clipMax.Y) continue;
                bool v = flags != null && w < flags.Length && flags[w];
                dl.AddLine(pa, pb, v ? visited : unseen, thick);
            }
        }

        // Visited overlay (current layer only): 4 floats per strip.
        if (current && view.VisitedByLayer[layer] is float[] vStrips)
        {
            uint fill = MulAlpha(FloorVisited, a);
            for (int i = 0; i + 3 < vStrips.Length; i += 4)
                FillRect(dl, proj, rotate, vStrips[i], vStrips[i + 1], vStrips[i + 2], vStrips[i + 3], fill, clipMin, clipMax);
        }
    }

    private static void FillRect(ImDrawListPtr dl, in Projection proj, bool rotate, float x0, float y0, float x1, float y1,
        uint col, Vector2 clipMin, Vector2 clipMax)
    {
        Vector2 p00 = proj.P(x0, y0), p11 = proj.P(x1, y1);
        if (_round)
        {
            FillRound(dl, p00, proj.P(x1, y0), p11, proj.P(x0, y1), col);
            return;
        }
        if (rotate)
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

    /// <summary>A floor strip (four corners, in order round it) on the round radar: whole, skipped or cut to the circle.</summary>
    private static void FillRound(ImDrawListPtr dl, Vector2 a, Vector2 b, Vector2 c, Vector2 d, uint col)
    {
        Vector2 min = Vector2.Min(Vector2.Min(a, b), Vector2.Min(c, d)), max = Vector2.Max(Vector2.Max(a, b), Vector2.Max(c, d));
        if (!_mask.OverlapsBox(min, max)) return;
        if (_mask.ContainsBox(min, max)) { dl.AddQuadFilled(a, b, c, d, col); return; }
        Span<Vector2> q = stackalloc Vector2[4];
        q[0] = a; q[1] = b; q[2] = c; q[3] = d;
        Span<Vector2> p = stackalloc Vector2[CircleClip.MaxPoints];
        int n = CircleClip.Polygon(_mask, q, default, p, default);
        if (n >= 3) dl.AddConvexPolyFilled(ref p[0], n, col);
    }

    // ── Click-to-select ─────────────────────────────────────────────────────
    // Left-click a dot: that object is selected in game, as a click on it in the
    // 3D world would. A press that moves more than a few pixels is a drag (the
    // panel moves; with docked click-through and no Ctrl it stays put, RadarDrag)
    // and selects nothing. Hover shows the object's name and
    // distance (and its last known health, when the engine has seen one).
    // RynthAi 2026-10-01 and later send each marker's object id, and the click
    // selects exactly that id. Older RynthAi builds send no id (Id = 0): then
    // the clicked marker is matched to the client's object list by position
    // (the engine's ~0.1 s position snapshot, same landblock, name preferred),
    // which can pick the wrong one of two same-name monsters close together.
    // The select itself is queued for AC's main thread
    // (AcMainThreadQueue.EnqueueOverlaySelect), which re-checks the object
    // still exists before selecting.

    private const float ClickSlop = 4f;          // px a press may move and still be a click
    private const float MatchTolerance = 5f;     // metres between a marker and its object (snapshot lag)
    private const uint TypeCreature = 0x10, TypePortal = 0x10000;

    private Vector2 _pressPos;
    private float _pressTravel;
    private readonly TipCache _hoverTip = new();

    private void MarkerInput(ImDrawListPtr dl, RadarView view, Vector2 centre, Vector2 clipMin, Vector2 clipMax, bool clicked)
    {
        Vector2 mouse = ImGuiNET.ImGui.GetIO().MousePos;
        if (ImGuiNET.ImGui.IsItemActivated())
        {
            _pressPos = mouse;
            _pressTravel = 0f;
        }
        if (ImGuiNET.ImGui.IsItemActive())
        {
            _pressTravel = MathF.Max(_pressTravel, Vector2.Distance(mouse, _pressPos));
            // Popped out, a 2 px drag hands the move to the OS window (DragWindowWithLastItem),
            // which then follows the cursor: the context's mouse barely moves, so mark it a drag.
            if (ImGuiPopOuts.InPopOutFrame && ImGuiNET.ImGui.IsMouseDragging(ImGuiMouseButton.Left, 2f))
                _pressTravel = float.MaxValue;
        }

        bool hovered = ImGuiNET.ImGui.IsItemHovered();
        if (!hovered && !clicked) return;
        if (mouse.X < clipMin.X || mouse.X > clipMax.X || mouse.Y < clipMin.Y || mouse.Y > clipMax.Y) return;

        RadarMarker? hit = HitTest(view, centre, clipMin, clipMax, mouse, out Vector2 hitPos);
        if (hit == null) return;

        if (clicked && _pressTravel <= ClickSlop)
        {
            uint id = hit.Id != 0 ? hit.Id : ResolveMarker(view, hit);
            if (id != 0)
                Compatibility.AcMainThreadQueue.EnqueueOverlaySelect(id);
            return;
        }

        if (!hovered || ImGuiNET.ImGui.IsItemActive()) return;
        dl.PushClipRect(clipMin, clipMax, true);
        dl.AddCircle(hitPos, 6.5f, CoordText, 16, 1.5f);
        dl.PopClipRect();
        if (hit == _sel && RadarSettingsStore.ShowSelectedLabel) return;   // its label is already showing
        ImGuiNET.ImGui.BeginTooltip();
        ImGuiNET.ImGui.TextUnformatted(_hoverTip.Get(view, hit));
        ImGuiNET.ImGui.EndTooltip();
    }

    /// <summary>The marker under <paramref name="mouse"/> (a dot's hit radius grows with the zoom and the marker's size), or null.</summary>
    private static RadarMarker? HitTest(RadarView view, Vector2 centre, Vector2 clipMin, Vector2 clipMax, Vector2 mouse, out Vector2 hitPos)
    {
        hitPos = default;
        if (_round && !_mask.Contains(mouse)) return null;
        if (mouse.X < clipMin.X || mouse.X > clipMax.X || mouse.Y < clipMin.Y || mouse.Y > clipMax.Y) return null;
        float zoom = MathF.Max(0.5f, RadarSettingsStore.Zoom);
        var proj = new Projection(centre, view.Live.Player, RadarSettingsStore.RotateWithPlayer, zoom);
        // Hit radius: a little more than the dot, growing with zoom, in UI-scaled pixels.
        float radius = Math.Clamp(5f + 1.5f * zoom, 6f, 14f) * MathF.Max(1f, EngineFrameController.FontScale);
        return MarkerAt(view, proj, mouse, radius, clipMin, clipMax, out hitPos);
    }

    /// <summary>
    /// Whether the mouse is over a selectable dot or one of the two buttons: the next frame's
    /// <see cref="ClickThrough"/> answer. Once a press has begun, it holds until the button is up.
    /// </summary>
    private void UpdateHot(RadarView? view, Vector2 centre, Vector2 clipMin, Vector2 clipMax, Vector2 buttonsMin, Vector2 buttonsMax)
    {
        var io = ImGuiNET.ImGui.GetIO();
        Vector2 m = io.MousePos;
        bool over = m.X >= buttonsMin.X && m.X <= buttonsMax.X && m.Y >= buttonsMin.Y && m.Y <= buttonsMax.Y;
        if (!over && view != null && RadarSettingsStore.ClickToSelect)
            over = HitTest(view, centre, clipMin, clipMax, m, out _) != null;
        bool down = io.MouseDown[0];
        if (down)
        {
            if (!_downPrev) _pressHot = over;
            _hot = _pressHot;
        }
        else _hot = over;
        _downPrev = down;
    }

    /// <summary>The shown marker nearest <paramref name="mouse"/> within <paramref name="radius"/> px, or null.</summary>
    private static RadarMarker? MarkerAt(RadarView view, in Projection proj, Vector2 mouse, float radius,
        Vector2 clipMin, Vector2 clipMax, out Vector2 at)
    {
        RadarMarker? best = null;
        float bestD2 = float.MaxValue;
        at = default;
        float uiScale = MathF.Max(1f, EngineFrameController.FontScale);
        foreach (RadarMarker m in view.Live.Markers)
        {
            if (!RadarSettingsStore.ShowKind(m.Kind)) continue;
            Vector2 pp = proj.P(m.X, m.Y);
            if (pp.X < clipMin.X || pp.X > clipMax.X || pp.Y < clipMin.Y || pp.Y > clipMax.Y) continue;
            // A big marker is grabbed anywhere on it, not only near its centre.
            float reach = MathF.Max(radius, (RadarMarkerStyle.Radius(m.Kind) * RadarSettingsStore.Markers.SizeOf(m.Kind) + 2f) * uiScale);
            float d2 = Vector2.DistanceSquared(pp, mouse);
            if (d2 > reach * reach || d2 >= bestD2) continue;
            bestD2 = d2;
            best = m;
            at = pp;
        }
        return best;
    }

    /// <summary>
    /// The object a marker stands for: the nearest object in the player's landblock
    /// (world = landblock-local + 192 m per landblock step, as the radar's markers)
    /// of the marker's kind, within <see cref="MatchTolerance"/>; a name match wins
    /// over a slightly nearer stranger. 0 when nothing matches. Only for markers
    /// without an id (older RynthAi builds).
    /// </summary>
    private static uint ResolveMarker(RadarView view, RadarMarker m)
    {
        uint landblock = view.Live.Player.Landblock;
        if (landblock == 0) return 0;
        uint playerId = Compatibility.ClientHelperHooks.GetPlayerId();
        string? label = string.IsNullOrEmpty(m.Label) ? null : m.Label;
        const float tol2 = MatchTolerance * MatchTolerance;
        uint best = 0;
        float bestScore = float.MaxValue;
        foreach (uint id in Compatibility.ClientObjectHooks.LiveObjectIds)
        {
            if (id == 0 || id == playerId) continue;
            if (!Compatibility.ClientObjectHooks.TryGetSnapshotPosition(id, out uint cell, out float x, out float y, out float z)) continue;
            if ((cell >> 16) != landblock) continue;
            float dx = x + ((cell >> 24) & 0xFF) * 192f - m.X;
            float dy = y + ((cell >> 16) & 0xFF) * 192f - m.Y;
            float dz = z - m.Z;
            float d2 = dx * dx + dy * dy + dz * dz;
            if (d2 > tol2) continue;
            if (m.Kind <= 2)
            {
                if (!Compatibility.ClientObjectHooks.TryGetSnapshotItemType(id, out uint type)) continue;
                uint need = m.Kind == 2 ? TypePortal : TypeCreature;
                if ((type & need) == 0) continue;
            }
            bool named = label != null && Compatibility.ClientObjectHooks.TryGetSnapshotName(id, out string name)
                         && name.Equals(label, StringComparison.Ordinal);
            float score = named ? d2 : d2 + tol2;
            if (score < bestScore) { bestScore = score; best = id; }
        }
        return best;
    }

    /// <summary>
    /// "Name\n12 yd" for the hovered (or selected) marker, plus "  75%" when the marker has an id and
    /// the engine has a health reading for it (MonsterHudData: one locked lookup, hover and the
    /// selected marker only). Rebuilt only when the marker, whole yards or whole percent change; one
    /// cache for the hover tooltip, one for the selected object's label, so neither rebuilds the other's.
    /// </summary>
    private sealed class TipCache
    {
        private byte _tipKind = byte.MaxValue;
        private string? _tipLabel;
        private int _tipDistance = int.MinValue;
        private int _tipPct = -1;
        private string _tipText = "";

        public string Get(RadarView view, RadarMarker m)
        {
            RadarPlayer p = view.Live.Player;
            float dx = m.X - p.WorldX, dy = m.Y - p.WorldY, dz = m.Z - p.Z;
            int d = (int)MathF.Round(MathF.Sqrt(dx * dx + dy * dy + dz * dz));
            int pct = -1;
            if (m.Id != 0 && RadarKind.IsCreature(m.Kind) && Hud.MonsterHudData.TryGetHealth(m.Id, out Hud.HealthObservation h))
                pct = (int)MathF.Round(h.Ratio * 100f);
            // Markers are rebuilt on every radar poll: key on what the text shows, not the instance.
            if (m.Kind == _tipKind && d == _tipDistance && pct == _tipPct
                && string.Equals(m.Label, _tipLabel, StringComparison.Ordinal))
                return _tipText;
            _tipKind = m.Kind;
            _tipLabel = m.Label;
            _tipDistance = d;
            _tipPct = pct;
            // A label is the object's name; the kind always follows it, so a dot says which show box
            // in the settings hides it (a fellow is not a "Player", your kill's corpse not a "Corpse").
            string name = string.IsNullOrEmpty(m.Label) ? RadarKind.Name(m.Kind)
                : m.Label + "  (" + RadarKind.Name(m.Kind) + ")";
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            _tipText = name + "\n" + d.ToString(inv) + " yd"
                       + (pct >= 0 ? "   " + pct.ToString(inv) + "%" : "");
            return _tipText;
        }
    }

    // ── Compass and text ───────────────────────────────────────────────────

    private static void DrawCardinals(ImDrawListPtr dl, RadarView view, Vector2 centre, float halfW, float halfH)
    {
        float sz = RadarSettingsStore.CompassSize;
        bool rotate = RadarSettingsStore.RotateWithPlayer;
        float h = view.Live.Player.Heading * MathF.PI / 180f;
        float sinH = MathF.Sin(h), cosH = MathF.Cos(h);
        float effW = MathF.Max(1, halfW - sz), effH = MathF.Max(1, halfH - sz);
        ImFontPtr font = ImGuiFonts.Get(UiFont.UiBold14);

        foreach (var c in Compass)
        {
            float dirX, dirY;
            if (rotate)
            {
                dirX = sinH * c.WorldX - cosH * c.WorldY;
                dirY = -(cosH * c.WorldX + sinH * c.WorldY);
            }
            else
            {
                dirX = c.WorldX;
                dirY = -c.WorldY;
            }
            // Onto the rectangular perimeter: max(|dx t|/effW, |dy t|/effH) = 1.
            float m = MathF.Max(MathF.Abs(dirX) / effW, MathF.Abs(dirY) / effH);
            if (m < 1e-4f) continue;
            float t = _round ? MathF.Max(1f, _mask.Radius - sz * 0.6f) / MathF.Sqrt(dirX * dirX + dirY * dirY) : 1 / m;
            DrawOutlinedText(dl, font, c.Label, new Vector2(centre.X + dirX * t, centre.Y + dirY * t), Cardinal, alignBottom: false,
                scale: sz / 14f);
        }
    }

    /// <summary>
    /// Text with a 1 px 4-way black outline, centred on x; centred or bottom-aligned on y.
    /// <paramref name="scale"/> stretches the font's baked size (1 = as baked).
    /// </summary>
    private static void DrawOutlinedText(ImDrawListPtr dl, ImFontPtr font, string text, Vector2 anchor, uint col, bool alignBottom,
        float scale = 1f)
    {
        ImGuiNET.ImGui.PushFont(font);
        Vector2 ts = ImGuiNET.ImGui.CalcTextSize(text) * scale;
        ImGuiNET.ImGui.PopFont();
        var pos = new Vector2(anchor.X - ts.X * 0.5f, alignBottom ? anchor.Y - ts.Y - 1 : anchor.Y - ts.Y * 0.5f);
        float fs = font.FontSize * scale;
        dl.AddText(font, fs, pos + new Vector2(-1, 0), Outline, text);
        dl.AddText(font, fs, pos + new Vector2(1, 0), Outline, text);
        dl.AddText(font, fs, pos + new Vector2(0, -1), Outline, text);
        dl.AddText(font, fs, pos + new Vector2(0, 1), Outline, text);
        dl.AddText(font, fs, pos, col, text);
    }

    // ── Settings (gear) ─────────────────────────────────────────────────────

    private void SettingsWindow()
    {
        if (!_settingsOpen)
        {
            if (_settingsSub) { UiSources.Settings.Unsubscribe(); _settingsSub = false; }
            return;
        }
        if (!_settingsSub)
        {
            UiSources.Settings.Subscribe();
            UiSources.Settings.RequestRefresh();
            _settingsSub = true;
        }
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.WindowBg, RynthTheme.BoxBg);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, ButtonBorder);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 4f);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 10));
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6, 6));
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize
            | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoCollapse;
        bool visible = ImGuiNET.ImGui.Begin("##radar_settings", ref _settingsOpen, flags);
        ImGuiNET.ImGui.PopStyleVar(3);
        ImGuiNET.ImGui.PopStyleColor(2);
        if (visible)
        {
            ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui11));
            ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.UiBold11));
            ImGuiNET.ImGui.TextUnformatted("Radar Settings");
            ImGuiNET.ImGui.PopFont();
            // The small close X, in the top-right corner of the content (the width of the kind rows' last column).
            float uiScale = MathF.Max(1f, EngineFrameController.FontScale);
            ImGuiNET.ImGui.SameLine(350 * uiScale + MarkerStyleEditor.SizeSliderWidth - 14);
            if (FaceKit.IconButton("##radar_settings_close", PhosphorIcons.X, ImGuiNET.ImGui.GetCursorScreenPos(), new Vector2(14, 14),
                    FaceKit.Faded(FaceKit.Mute), RynthTheme.WithAlpha(RynthTheme.Background, 0xDC), font: UiFont.Ui9))
                _settingsOpen = false;
            ImGuiNET.ImGui.SetItemTooltip("Close");
            ImGuiNET.ImGui.Separator();

            bool changed = false;
            changed |= Check("Rotate with player", ref RadarSettingsStore.RotateWithPlayer);
            changed |= Check("Show gold rim", ref RadarSettingsStore.ShowGoldRim);
            changed |= Check("Round radar", ref RadarSettingsStore.RoundRadar);
            if (ImGuiNET.ImGui.IsItemHovered())
                ImGuiNET.ImGui.SetTooltip("A circle instead of a square: the picture, the dots and the world map are all cut to it.");
            changed |= Check("Docked click-through (dots still work; Ctrl for the rest)", ref RadarSettingsStore.CtrlGatedClickThrough);
            if (ImGuiNET.ImGui.IsItemHovered())
                ImGuiNET.ImGui.SetTooltip("On: docked clicks pass through to the game, except on a dot (hover it, click to select) and the two buttons; hold Ctrl to drag or resize.");
            if (RadarSettingsStore.CtrlGatedClickThrough)
            {
                ImGuiNET.ImGui.Indent();
                changed |= Check("Mouse wheel still zooms the radar", ref RadarSettingsStore.ClickThroughWheelZoom);
                if (ImGuiNET.ImGui.IsItemHovered())
                    ImGuiNET.ImGui.SetTooltip("On: with click-through, the wheel over the radar zooms it instead of the game's camera. Clicks still go through to the game.");
                ImGuiNET.ImGui.Unindent();
            }
            changed |= Check("Click a dot to select it", ref RadarSettingsStore.ClickToSelect);
            if (ImGuiNET.ImGui.IsItemHovered())
                ImGuiNET.ImGui.SetTooltip("Left-click a dot to select that object in game. Hover a dot for its name and distance.");
            changed |= Check("Highlight the selected object", ref RadarSettingsStore.HighlightSelected);
            if (ImGuiNET.ImGui.IsItemHovered())
                ImGuiNET.ImGui.SetTooltip("Whatever you have selected in game (a monster, NPC, player, portal, door, corpse or item on the ground)\ngets a ring in this colour round its dot, even when its kind's box is off.\nOff the radar's picture, an arrow on the edge points the way to it.");
            ImGuiNET.ImGui.SameLine();
            Vector4 selCol = RynthTheme.Vec(RadarSettingsStore.SelectedColor);
            if (ImGuiNET.ImGui.ColorEdit4("##selcol", ref selCol, PickFlags))
            {
                RadarSettingsStore.SelectedColor = ToArgb(selCol);
                _colorDirty = true;
            }
            if (ImGuiNET.ImGui.IsItemHovered() && !ImGuiNET.ImGui.IsItemActive())
                ImGuiNET.ImGui.SetTooltip("The selection ring's colour (click to change it).");
            if (RadarSettingsStore.HighlightSelected)
            {
                ImGuiNET.ImGui.Indent();
                changed |= Check("Show its label", ref RadarSettingsStore.ShowSelectedLabel);
                if (ImGuiNET.ImGui.IsItemHovered())
                    ImGuiNET.ImGui.SetTooltip("The hover label (name, distance, health) stays beside the selected object while it is selected.");
                ImGuiNET.ImGui.Unindent();
            }
            changed |= Slider("Opacity", ref RadarSettingsStore.Opacity, 0.05f, 1.0f, "%.2f");
            changed |= Slider("Zoom", ref RadarSettingsStore.Zoom, 0.5f, 6.0f, "%.1f");
            changed |= Check("World map behind the radar (outdoors)", ref RadarSettingsStore.ShowWorldMap);
            if (ImGuiNET.ImGui.IsItemHovered())
                ImGuiNET.ImGui.SetTooltip("Draws the World Map's picture of the ground behind the dots while you're outdoors.\nIts sharpness follows the World Map's Detail setting (the bigger map packs look best zoomed in).");
            if (RadarSettingsStore.ShowWorldMap)
            {
                changed |= Slider("Map", ref RadarSettingsStore.WorldMapOpacity, 0.1f, 1.0f, "%.2f");
                if (!WorldMapTiles.IsOpen && WorldMapTiles.Status.Length > 0)
                    SafeImGui.TextDisabled(WorldMapTiles.Status);
            }
            changed |= RangeRingRows();
            RetailRadarRow();
            ImGuiNET.ImGui.Separator();
            changed |= KindRows();
            if (changed)
            {
                AvaloniaOverlay.RadarCtrlGatedClickThrough = RadarSettingsStore.CtrlGatedClickThrough;
                RadarSettingsStore.Save();
            }
            ImGuiNET.ImGui.PopFont();
        }
        ImGuiNET.ImGui.End();
    }

    private static bool Check(string label, ref bool value) => ImGuiNET.ImGui.Checkbox(label, ref value);

    /// <summary>
    /// The range rings' boxes. Each label shows the range RynthAi has now (its Settings > Ranges set it);
    /// the boxes are the radar's own and save with the radar's settings. True when one changed.
    /// </summary>
    private static bool RangeRingRows()
    {
        RynthAiSettings? ra = UiSources.Settings.Current?.Value;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string Yards(int v) => ra == null ? "" : " (" + v.ToString(inv) + " yd)";
        bool changed = Check("Monster Range ring" + Yards(ra?.MonsterRange ?? 0) + "###mrring", ref RadarSettingsStore.ShowMonsterRangeRing);
        if (ImGuiNET.ImGui.IsItemHovered())
            ImGuiNET.ImGui.SetTooltip("A ring round you at RynthAi's Monster Range: monsters inside it are ones the bot will attack.\nIt follows the setting (Settings > Ranges). Measured along the ground; RynthAi counts height too,\nso a monster a floor up or down can be inside the ring and still out of range.");
        changed |= Check("Ring Range ring" + Yards(ra?.RingRange ?? 0) + "###rrring", ref RadarSettingsStore.ShowRingRangeRing);
        if (ImGuiNET.ImGui.IsItemHovered())
            ImGuiNET.ImGui.SetTooltip("A second, blue ring at RynthAi's Ring Range: how close monsters must be for its ring spells.");
        if (ra == null && (RadarSettingsStore.ShowMonsterRangeRing || RadarSettingsStore.ShowRingRangeRing))
        {
            ImGuiNET.ImGui.SameLine();
            ImGuiNET.ImGui.TextDisabled("(waiting for RynthAi)");
        }
        return changed;
    }

    /// <summary>
    /// "Hide the retail radar": RynthAi's own SuppressRetailRadar setting (RynthAi pushes it to the engine every
    /// tick), the same switch as Settings > Interface > Hide retail radar. This reads it from RynthAi's settings payload
    /// and saves it back through the same command, so the two always agree whichever is changed; saved with the
    /// character's RynthAi settings. Greyed out until RynthAi has answered. The box shows the new value at once,
    /// until RynthAi's answer catches up.
    /// </summary>
    private void RetailRadarRow()
    {
        RynthAiSettings? ra = UiSources.Settings.Current?.Value;
        bool actual = ra?.SuppressRetailRadar ?? false;
        if (_hidePending is bool pending && (pending == actual || Environment.TickCount64 - _hidePendingAt > 3000))
            _hidePending = null;
        bool hide = _hidePending ?? actual;
        ImGuiNET.ImGui.BeginDisabled(ra == null);
        if (ImGuiNET.ImGui.Checkbox("Hide the retail radar", ref hide) && ra != null)
        {
            RynthAiSettings copy = ra.Clone();
            copy.SuppressRetailRadar = hide;
            SettingsCommands.Save(copy);
            _hidePending = hide;
            _hidePendingAt = Environment.TickCount64;
        }
        ImGuiNET.ImGui.EndDisabled();
        if (ImGuiNET.ImGui.IsItemHovered())
            ImGuiNET.ImGui.SetTooltip("Hides the game's built-in radar (bezel, compass, coordinates, blips).\nThe same switch as Settings > Interface > Hide retail radar: change it in either place.");
        if (ra == null)
        {
            ImGuiNET.ImGui.SameLine();
            ImGuiNET.ImGui.TextDisabled("(waiting for RynthAi)");
        }
    }

    // Legend order: creatures, then corpses, then places, then items.
    internal static readonly (byte Kind, string Label)[] KindRowsOrder =
    {
        (RadarKind.Monster, "Monsters"), (RadarKind.Player, "Players"), (RadarKind.Fellow, "Fellows"),
        (RadarKind.Npc, "NPCs"), (RadarKind.Vendor, "Vendors"), (RadarKind.Pet, "Your pets"),
        (RadarKind.Corpse, "Corpses"), (RadarKind.OwnCorpse, "Your corpses"), (RadarKind.Lifestone, "Lifestones"),
        (RadarKind.Portal, "Portals"), (RadarKind.Door, "Doors"), (RadarKind.GroundItem, "Items on the ground"),
    };

    private static readonly string[] KindTips =
    {
        "Creatures you can attack.\nHover a dot on the radar: its tooltip names the box that shows it.",
        "Creatures you can't attack (townsfolk, quest givers).\nShopkeepers have their own box (Vendors).",
        "Portals, with their destination.",
        "Doors, gates and hatches.",
        "Other players, outside your fellowship.\nPlayers in your fellowship have their own box (Fellows).",
        "Players in your fellowship.",
        "Your own combat pet.",
        "Shopkeepers.",
        "Corpses. Off hides every corpse, the ones you may loot too.\nOn: corpses you can't loot (or not identified yet: selecting one tells) show in this colour.",
        "Corpses you may loot: your kills, your fellows' kills and your own corpse, in their own colour. Known once the corpse has been identified (looting or selecting it does that).\nOnly while Corpses is on.",
        "Lifestones.",
        "Items lying on the ground. Off by default: there can be a lot of them.",
    };

    /// <summary>
    /// The legend and per-kind settings: show box, the marker as drawn, its name, a colour swatch
    /// (click to pick), then the name box and the marker and name sizes (MarkerStyleEditor). One row
    /// per kind, then your own arrow's size, the reset buttons. True when something changed.
    /// </summary>
    private static bool KindRows()
    {
        bool changed = false;
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.UiBold11));
        ImGuiNET.ImGui.TextUnformatted("Show (click a swatch to change its colour):");
        ImGuiNET.ImGui.PopFont();
        RadarMarkerStyle.FillColors(KindCols);
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        float scale = MathF.Max(1f, EngineFrameController.FontScale);
        // Columns after the colour swatch (at 190): name box, marker size, name text size.
        float xName = 232 * scale, xSize = 268 * scale, xText = 350 * scale;
        MarkerStyleSettings markers = RadarSettingsStore.Markers;
        MarkerStyleEditor.HeaderRow(xName, xSize, xText);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6, 3));
        foreach (var (kind, label) in KindRowsOrder)
        {
            ImGuiNET.ImGui.PushID(kind);
            // "Your corpses" sits under Corpses: Corpses off hides them too (RadarSettingsStore.ShowKind).
            bool sub = kind == RadarKind.OwnCorpse;
            if (sub) { ImGuiNET.ImGui.Indent(14 * scale); ImGuiNET.ImGui.BeginDisabled(!RadarSettingsStore.ShowCorpses); }
            changed |= ImGuiNET.ImGui.Checkbox("##show", ref RadarSettingsStore.ShowFlag(kind));
            if (sub) ImGuiNET.ImGui.EndDisabled();      // the sizes below stay adjustable
            if (ImGuiNET.ImGui.IsItemHovered()) SafeImGui.SetTooltip(KindTips[kind]);
            ImGuiNET.ImGui.SameLine();
            // The marker exactly as the radar draws it; faded while the kind is hidden, so the
            // legend doesn't look like the box did nothing.
            float box = ImGuiNET.ImGui.GetFrameHeight();
            Vector2 at = ImGuiNET.ImGui.GetCursorScreenPos();
            ImGuiNET.ImGui.Dummy(new Vector2(box, box));
            uint glyph = KindCols[kind];
            if (!RadarSettingsStore.ShowKind(kind))
                glyph = (glyph & 0x00FFFFFFu) | ((uint)((glyph >> 24) * 0.3f) << 24);
            RadarMarkerStyle.Draw(dl, kind, at + new Vector2(box, box) * 0.5f, glyph, 1.3f * scale);
            ImGuiNET.ImGui.SameLine();
            ImGuiNET.ImGui.AlignTextToFramePadding();
            ImGuiNET.ImGui.TextUnformatted(label);
            if (ImGuiNET.ImGui.IsItemHovered()) SafeImGui.SetTooltip(KindTips[kind]);
            ImGuiNET.ImGui.SameLine(190 * scale);
            Vector4 v = RynthTheme.Vec(RadarSettingsStore.KindColors[kind]);
            if (ImGuiNET.ImGui.ColorEdit4("##col", ref v, PickFlags))
            {
                RadarSettingsStore.KindColors[kind] = ToArgb(v);
                _colorDirty = true;
            }
            changed |= MarkerStyleEditor.KindCells(markers, kind, xName, xSize, xText);
            if (sub) ImGuiNET.ImGui.Unindent(14 * scale);
            ImGuiNET.ImGui.PopID();
        }
        ImGuiNET.ImGui.PopStyleVar();
        // Everything else drawn on the radar that has a size.
        ImGuiNET.ImGui.Separator();
        changed |= MarkerStyleEditor.SliderRow("You (heading arrow)", ref markers.SelfSize,
            MarkerStyleSettings.MinSize, MarkerStyleSettings.MaxSize, "%.1fx", xSize);
        changed |= MarkerStyleEditor.SliderRow("Compass letters", ref RadarSettingsStore.CompassSize,
            RadarSettingsStore.MinText, RadarSettingsStore.MaxText, "%.0f px", xText);
        changed |= MarkerStyleEditor.SliderRow("Coordinates", ref RadarSettingsStore.CoordSize,
            RadarSettingsStore.MinText, RadarSettingsStore.MaxText, "%.0f px", xText);
        // The picker reports every drag step: save once no colour edit is in progress.
        if (_colorDirty && !ImGuiNET.ImGui.IsAnyItemActive())
        {
            _colorDirty = false;
            changed = true;
        }
        if (ImGuiNET.ImGui.SmallButton("Reset colours"))
        {
            RadarSettingsStore.ResetKindColors();
            changed = true;
        }
        if (ImGuiNET.ImGui.IsItemHovered()) ImGuiNET.ImGui.SetTooltip("Every kind, and the selection ring, back to its default colour (show boxes stay as they are).");
        ImGuiNET.ImGui.SameLine();
        if (ImGuiNET.ImGui.SmallButton("Reset sizes and names"))
        {
            RadarSettingsStore.ResetSizes();
            changed = true;
        }
        if (ImGuiNET.ImGui.IsItemHovered()) ImGuiNET.ImGui.SetTooltip("Every marker, name, arrow, compass and coordinate size, and the name boxes, back to their defaults.");
        return changed;
    }

    private static bool _colorDirty;

    // The colour swatches: click to pick, alpha included.
    private const ImGuiColorEditFlags PickFlags = ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel
        | ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf;

    private static uint ToArgb(Vector4 v)
    {
        static uint B(float f) => (uint)Math.Clamp((int)MathF.Round(f * 255f), 0, 255);
        return (B(v.W) << 24) | (B(v.X) << 16) | (B(v.Y) << 8) | B(v.Z);
    }

    private static bool Slider(string label, ref float value, float min, float max, string fmt)
    {
        ImGuiNET.ImGui.TextUnformatted(label);
        ImGuiNET.ImGui.SameLine(60);
        ImGuiNET.ImGui.SetNextItemWidth(170);
        ImGuiNET.ImGui.PushID(label);
        bool changed = ImGuiNET.ImGui.SliderFloat("##v", ref value, min, max, fmt);
        ImGuiNET.ImGui.PopID();
        // Save once the drag ends, not on every step.
        return changed && !ImGuiNET.ImGui.IsItemActive() || ImGuiNET.ImGui.IsItemDeactivatedAfterEdit();
    }
}
