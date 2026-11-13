// ============================================================================
//  RynthCore.Engine - ImGui/Panels/RynthAiFace.cs
//  ImGui face of the RynthAi dashboard (docs/IMGUI_PARITY_PLAN.md §2.1),
//  first laid out from the Avalonia face (UI/Panels/RynthAiPanel.cs), then
//  made compact with Phosphor icons, after DrakBot's dashboard. Top to bottom:
//    title row    RYNTH, running/stopped | target, vitals, lock, opacity -/+,
//                 minimize, pop out; a teal accent line along the top edge
//    header grid  popped out only: meta state, bot activity | pickers (docked,
//                 they are the Loaded files drawer)
//    control row  macro start/stop, combat, buffing, nav, looting and meta
//                 toggles (on: lit with a bar under the icon; off: dimmed),
//                 bot activity as a coloured label ... force rebuff. A
//                 toggle's right-click opens its settings (2026-10-14):
//                 Combat the Monsters panel, Buffing Settings > Buffing,
//                 Navigation the Nav panel, Looting the Loot Editor on the
//                 loot profile in use, Meta the Meta panel.
//    launchers    one row of icon buttons (names in tooltips; labels too
//                 when the panel is wide enough); a button lights up while
//                 its window (or drawer) is open. Nav and Meta left the row
//                 for their toggles' right-click (2026-10-14).
//    footer       FPS (coloured by rate) | version | uptime
//    bars         player vitals, then the target (name + health), each
//                 toggled from the title row. Last on purpose, like DrakBot's
//                 target row: what comes and goes with a fight never moves
//                 the buttons above it.
//    drawers      tabs on the dashboard's edges (DashboardDrawers.cs), one
//                 open per side, windows of their own beside the dashboard:
//                 Ranges (RangesSlideOut.cs), Loaded files, Patrol and Mini
//                 Remote (RynthAiFace.Drawers.cs) on the left, My settings
//                 (QuickSettingsDrawer.cs) on the right; a tab's right-click
//                 moves its drawer to the other side. Only the bars stay at
//                 the bottom (Tom 2026-10-05).
//  Minimized keeps the title row, the control row and the bars. The window's
//  minimum height is the content's, and toggling the bars or minimize fits the
//  window to the content. Data: UiSources.RynthAi (a RynthAiView with every
//  string preformatted); clicks: RynthAiCommands.
//
//  Chromeless (like the Avalonia face): the title row is the drag surface.
//
//  Size: the dashboard's panel text size (PanelTextScale.Get(Title): its own
//  row in Settings > Interface, else All Panels). The host draws the face with
//  the fonts baked for that step (ImGuiFonts.CurrentStep, the nearest baked
//  one until a re-bake lands) and every box here is multiplied by that same
//  step's factor (Px), so text, icons and boxes grow together; size 0 is the
//  plain pixel layout. The title row has no host title bar, so it carries the
//  control: Ctrl+wheel over it steps the size, right-click opens the size
//  popup (ImGuiPanelHost.TextSizeRows). A size change scales the window's
//  width with it and fits the height to the content (MeasureAndFit).
//
//  AC thread only; no per-frame string building except the FPS/uptime footer,
//  which is cached until its value changes.
// ============================================================================

using System;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.UI;
using RynthCore.Engine.UI.Data;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed partial class RynthAiFace : IImGuiPanel
{
    public const string Title = "RynthAi";

    public static void Register()
    {
        ImGuiPanelHost.Register(Title,
            new PanelSpec(new Vector2(296, 200), new Vector2(MinWidth, 54), PanelChrome.None,
                Background: RynthTheme.RynthAiBackground, BorderColor: RynthTheme.RynthAiBorder,
                Rounding: RynthTheme.RynthAiRounding),
            () => new RynthAiFace());
        // The Loot Editor edits RynthAi's loot profiles and opens from this dashboard.
        LootEditorFace.Register();
        // The Map launcher's panel. Registered here (only with RynthAi present, which
        // feeds its data) so engine init needs no new line in EntryPoint.
        DungeonMapFace.Register();
    }

    private const float MinWidth = 280;

    // ── Palette (RynthAiPanel's, ARGB) ─────────────────────────────────
    private static readonly uint Green = C(0xFF40D973), White = C(0xFFFFFFFF), Hp = C(0xFFD93333), Mana = C(0xFF268CF2),
        MacroRun = C(0xFF1A5926), MacroStop = C(0xFF401F1F), FrFill = C(0xFF47330A), SelectorBg = C(0xFF2D3847),
        Off = C(0xFF6E8197), Faint = C(0xFF5A7088), RunGlow = C(0x5540D973);
    private static uint Teal => RynthTheme.Accent;
    private static uint TealSoft => RynthTheme.AccentFill;
    private static uint Amber => RynthTheme.Highlight;
    private static uint Mute => RynthTheme.TextMuted;
    private static uint TextDim => RynthTheme.Text;
    private static uint ShellBg => RynthTheme.Background;
    private static uint BarBg => RynthTheme.Field;
    private static uint BtnFill => RynthTheme.Button;
    private static uint BtnBord => RynthTheme.Border;
    private static uint C(uint argb) => RynthTheme.Argb(argb);

    private static readonly uint[] SegmentColors = BuildSegmentColors();

    private string _fpsText = "FPS —", _upText = "Up —";
    private int _fpsShown = -1;
    private long _upShown = -1;
    private bool _patrolOpen;
    // The drawers on the dashboard's left edge (their own window, placed against this one).
    private readonly DashboardDrawers _drawers;
    private readonly PatrolDrawer _patrol = new();
    private readonly RemoteDrawer _remote = new();
    // This frame's data, for the drawers.
    private RynthAiView? _view;
    private RynthAiSnapshot _raw = Empty;
    // Picker toggle: ImGui closes a popup on the press outside it, so remember
    // whether it was showing when the selector press began (a second click closes).
    private readonly int[] _pickerShownFrame = { -10, -10, -10, -10, -10 };
    private readonly bool[] _pressedWhileOpen = new bool[5];
    // The content's height (logical units, body padding included), measured each
    // frame: the window's minimum height. _fit: resize the window to it once.
    private float _contentHeight;
    private bool _fit;
    // The host's resize grip, which keeps its own size (it is not the panel's text).
    private const float GripRoom = 12;

    // This frame's size factor (PanelTextScale.Factor of the step the fonts are drawn at); 1 at size 0.
    // Static: the popups' and drawers' static helpers read it too. AC thread.
    private static float _k = 1f;
    /// <summary>A layout length (pixels at size 0) at the dashboard's size.</summary>
    private static float Px(float v) => v * _k;
    // The size factor the docked window was last fitted at and its width then (logical units):
    // a size change scales the width from there (not from a width the new minimum already grew).
    private float _fitK, _fitWidth;
    private const string SizePopupId = "##dash_size";

    /// <summary>The factor of the step the host draws this panel's fonts at (ImGuiPanelHost.DrawPanel sets the same step).</summary>
    private static float SizeFactor() => PanelTextScale.Factor(ImGuiFonts.BakedStepNear(PanelTextScale.Get(Title)));

    public RynthAiFace()
    {
        // Tab order top to bottom; the first one saved open wins at load.
        // Each side's in this order; My settings starts on the right.
        _drawers = new DashboardDrawers(new RangesSlideOut(), new FilesDrawer(this), _patrol, _remote, new QuickSettingsDrawer());
    }

    public Vector2? MinSize
    {
        get
        {
            float k = SizeFactor();
            if (RynthAiDashboardState.Classic) return _classic.MinSize(k);
            return new Vector2(MinWidth * k, _contentHeight > 0 ? _contentHeight
                : (RynthAiDashboardState.Minimized ? 54 : 180) * k);
        }
    }

    public void OnShown()
    {
        UiSources.RynthAi.Subscribe();
        UiSources.RynthSortWatch.Subscribe();   // the Sort toggle, while RynthSort is loaded
        UiSources.RynthAi.RequestRefresh();
        _fit = true; // drop the empty space a taller saved size leaves under the content
        _drewClassic = false;
    }

    public void OnHidden()
    {
        UiSources.RynthAi.Unsubscribe();
        UiSources.RynthSortWatch.Unsubscribe();
        if (_patrolOpen) { UiSources.Patrol.Unsubscribe(); _patrolOpen = false; }
        _drawers.Detach();
        if (_drewClassic) _classic.OnHidden();
        _drewClassic = false;
    }

    // ── The Classic look (ImGui/Panels/Classic): drawn instead of the dashboard while it's on ──
    private readonly Classic.ClassicDashboard _classic = new();
    private bool _drewClassic;
    // The dashboard's own size (logical px) while the Classic look is showing, put back when it ends.
    private static Vector2 _modernSize;

    /// <summary>
    /// Switches the dashboard between its Modern (compact) and Classic (retail) looks: the title bar
    /// buttons of either look and Settings > Interface. The window refits on the next frame. AC thread.
    /// </summary>
    internal static void SwitchLook(bool classic) => RynthAiDashboardState.SetClassic(classic);

    /// <summary>The Classic look's frame; false when the Modern dashboard draws.</summary>
    private bool DrawClassic(RynthAiView? view, RynthAiSnapshot raw)
    {
        if (!RynthAiDashboardState.Classic)
        {
            if (_drewClassic)
            {
                // Back from Classic: the dashboard's size as it was, then fit to its content.
                _drewClassic = false;
                _classic.OnHidden();
                if (_modernSize.X > 0 && !ImGuiPopOuts.InPopOutFrame) ImGuiPanelHost.RequestSize(Title, _modernSize);
                _fit = true;
            }
            return false;
        }
        if (!_drewClassic)
        {
            _drewClassic = true;
            _drawers.Detach();
            float place = Math.Max(0.01f, ImGuiPanelHost.PlacementScale());
            _modernSize = ImGuiNET.ImGui.GetWindowSize() / place;
            _classic.OnShown();
        }
        _k = PanelTextScale.Factor(ImGuiFonts.CurrentStep);
        _classic.Draw(view, raw, _k);
        return true;
    }

    public void Draw()
    {
        RynthAiView? view = UiSources.RynthAi.Current?.Value;
        RynthAiSnapshot raw = view?.Raw ?? Empty;
        if (DrawClassic(view, raw)) return;
        bool minimized = RynthAiDashboardState.Minimized;
        bool popped = ImGuiPopOuts.InPopOutFrame;
        _view = view;
        _raw = raw;
        uint boxBg = (RynthTheme.BoxBg & 0x00FFFFFF) | ((uint)(view?.PanelAlpha ?? 242) << 24);
        // The host set the step these fonts are drawn at; the boxes take the same step's factor.
        _k = PanelTextScale.Factor(ImGuiFonts.CurrentStep);
        // Enlarged: tooltips and menus opened from the dashboard at its size too. (At size 0 the
        // default font already is Ui11: nothing is pushed, so nothing changes.)
        bool pushFont = ImGuiFonts.CurrentStep > 0;
        if (pushFont) ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui11));
        try
        {
            Vector2 start = ImGuiNET.ImGui.GetCursorScreenPos();
            float x0 = start.X;
            float width = ImGuiNET.ImGui.GetContentRegionAvail().X;

            AccentLine();
            TitleRow(raw, x0, width, minimized);
            Gap(4);
            // Popped out (no drawers there): the Profile/Nav/Loot/Meta pickers inline while expanded
            // (the caret on the control row). Docked they are the Loaded files drawer.
            if (popped && !minimized && RynthAiDashboardState.FilesOpen)
            {
                HeaderGrid(view, raw, x0, width);
                Gap(4);
            }
            ControlRow(view, raw, x0, width, minimized, popped, boxBg);
            if (!minimized)
            {
                Gap(4);
                Launchers(x0, width);
                Gap(6);
                Footer(view, x0, width);
            }
            Bars(view, raw, x0, width, boxBg);
            PatrolPopup();
            CharMenu();
            SizePopup();
            MeasureAndFit(start.Y);
        }
        finally
        {
            if (pushFont) ImGuiNET.ImGui.PopFont();
        }
        // Last: a window of its own, placed against this one's rect (hidden popped out).
        _drawers.Draw();
    }

    /// <summary>
    /// Records the content height as the window's minimum (so the bars at the
    /// bottom are never cut off) and, after a toggle, fits the window to it.
    /// After a size change the width scales with the size (from the width it had
    /// at the old size), never wider than the screen, and the window is kept on
    /// the screen.
    /// </summary>
    private void MeasureAndFit(float startY)
    {
        float pad = ImGuiNET.ImGui.GetCursorStartPos().Y;   // the body's top padding (the bottom's is the same)
        float px = ImGuiNET.ImGui.GetCursorScreenPos().Y - startY + 2 * pad;
        float place = ImGuiPanelHost.PlacementScale();
        // + room for the host's resize grip, which is painted over the body's bottom-right corner.
        _contentHeight = MathF.Ceiling(px / place) + GripRoom;
        // Popped out the window is the OS window's (sized by the player); a size change made
        // meanwhile is fitted when it docks back (_fitK still holds the docked size).
        if (ImGuiPopOuts.InPopOutFrame) return;

        float width = ImGuiNET.ImGui.GetWindowWidth() / place;
        if (_fitK > 0 && _k != _fitK)
        {
            _fit = false;
            width = _fitWidth * (_k / _fitK);
            float screenW = ImGuiNET.ImGui.GetIO().DisplaySize.X / place;
            if (screenW > 1) width = MathF.Min(width, screenW);
            width = MathF.Max(width, MinWidth * _k);
            ImGuiPanelHost.RequestSize(Title, new Vector2(width, _contentHeight));
            // A bigger dashboard near the right or bottom edge moves in rather than running off: the host
            // pulls it onto the screen from its home at the new size (UI/PanelPlacement; the spot asked for
            // here is where it draws, so it changes nothing), and the home stays the saved spot.
            ImGuiPanelHost.RequestPos(Title, ImGuiNET.ImGui.GetWindowPos() / place);
        }
        else if (_fit)
        {
            _fit = false;
            ImGuiPanelHost.RequestSize(Title, new Vector2(width, _contentHeight));
        }
        _fitK = _k;
        _fitWidth = width;
    }

    /// <summary>A thin teal line along the window's top edge, inside its rounded corners.</summary>
    private static void AccentLine()
    {
        Vector2 wp = ImGuiNET.ImGui.GetWindowPos(), ws = ImGuiNET.ImGui.GetWindowSize();
        float r = RynthTheme.RynthAiRounding;
        ImGuiNET.ImGui.GetWindowDrawList().AddRectFilled(new Vector2(wp.X + r, wp.Y), new Vector2(wp.X + ws.X - r, wp.Y + MathF.Max(1.5f, Px(2))), Teal);
    }

    // ── Title row: "R" + "YNTH", running | chips (drag surface) ──────

    private void TitleRow(RynthAiSnapshot raw, float x0, float width, bool minimized)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        float y = ImGuiNET.ImGui.GetCursorScreenPos().Y;
        const int chips = 8;
        float chipW = Px(18), chipH = Px(16), chipGap = Px(2), chipRound = Px(2);
        float chipsW = chips * chipW + (chips - 1) * chipGap;
        float rowH = Px(20);

        // Drag surface: everything left of the chips.
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x0, y));
        ImGuiNET.ImGui.InvisibleButton("##dash_drag", new Vector2(Math.Max(1, width - chipsW), rowH));
        ImGuiPanelHost.DragWindowWithLastItem();
        SafeImGui.SetItemTooltip(DragTip);

        ImFontPtr title = ImGuiFonts.Get(UiFont.Dash14);
        float ty = y + (rowH - title.FontSize) * 0.5f;
        dl.PushClipRect(new Vector2(x0, y), new Vector2(x0 + Math.Max(1, width - chipsW - Px(4)), y + rowH), true);
        dl.AddText(title, title.FontSize, new Vector2(x0, ty), Teal, "R");
        float rw = CalcWidth(title, "R");
        dl.AddText(title, title.FontSize, new Vector2(x0 + rw, ty), White, "YNTH");
        // Running / stopped, at a glance: a dot and the word in a pill after the name.
        {
            ImFontPtr pf = ImGuiFonts.Get(UiFont.Dash9);
            bool run = raw.MacroRunning;
            string word = run ? "running" : "stopped";
            float px = x0 + rw + CalcWidth(title, "YNTH") + Px(7);
            float ph = pf.FontSize + Px(4), pw = Px(13) + CalcWidth(pf, word) + Px(6);
            float py = y + (rowH - ph) * 0.5f;
            uint col = run ? Green : Off;
            dl.AddRect(new Vector2(px, py), new Vector2(px + pw, py + ph), run ? MacroRun : BtnBord, ph * 0.5f);
            dl.AddCircleFilled(new Vector2(px + Px(7), py + ph * 0.5f), Px(2.5f), col);
            dl.AddText(pf, pf.FontSize, new Vector2(px + Px(13), py + (ph - pf.FontSize) * 0.5f), col, word);
        }
        dl.PopClipRect();

        float cx = x0 + width - chipsW;
        float cy = y + (rowH - chipH) * 0.5f;
        var chip = new Vector2(chipW, chipH);
        const UiFont chipFont = UiFont.Dash11;

        bool target = RynthAiDashboardState.ShowTargetBar;
        if (IconButton("##chip_target", new Vector2(cx, cy), chip, PhosphorIcons.Crosshair, chipFont, BtnFill, target ? Teal : Mute, BtnBord, chipRound))
        {
            RynthAiDashboardState.SetShowTargetBar(!target);
            _fit = true;
        }
        SafeImGui.SetItemTooltip(target
            ? "Target bar on: the target's name and health at the bottom. Click to hide it."
            : "Target bar off. Click to show the target's name and health at the bottom.");
        cx += chipW + chipGap;
        bool vitals = RynthAiDashboardState.ShowVitals;
        if (IconButton("##chip_vitals", new Vector2(cx, cy), chip, PhosphorIcons.Heartbeat, chipFont, BtnFill, vitals ? Teal : Mute, BtnBord, chipRound))
        {
            RynthAiDashboardState.SetShowVitals(!vitals);
            _fit = true;
        }
        SafeImGui.SetItemTooltip(vitals
            ? "Player vitals on: your health, stamina and mana at the bottom. Click to hide them."
            : "Player vitals off. Click to show your health, stamina and mana at the bottom.");
        cx += chipW + chipGap;
        if (IconButton("##chip_lock", new Vector2(cx, cy), chip, raw.IsLocked ? PhosphorIcons.Lock : PhosphorIcons.LockOpen,
                chipFont, BtnFill, raw.IsLocked ? Amber : Mute, BtnBord, chipRound))
            RynthAiCommands.TogglePanelLock();
        SafeImGui.SetItemTooltip(raw.IsLocked ? "Locked. Click to unlock the dashboard." : "Click to lock the dashboard in place.");
        cx += chipW + chipGap;
        if (IconButton("##chip_minus", new Vector2(cx, cy), chip, PhosphorIcons.Minus, chipFont, BtnFill, Mute, BtnBord, chipRound))
            RynthAiCommands.AdjustOpacity(-0.1f);
        ImGuiNET.ImGui.SetItemTooltip("More transparent");
        cx += chipW + chipGap;
        if (IconButton("##chip_plus", new Vector2(cx, cy), chip, PhosphorIcons.Plus, chipFont, BtnFill, Mute, BtnBord, chipRound))
            RynthAiCommands.AdjustOpacity(0.1f);
        ImGuiNET.ImGui.SetItemTooltip("More opaque");
        cx += chipW + chipGap;
        if (IconButton("##chip_classic", new Vector2(cx, cy), chip, PhosphorIcons.Scroll, chipFont, BtnFill, Mute, BtnBord, chipRound))
            SwitchLook(classic: true);
        SafeImGui.SetItemTooltip("Classic look: the retail-style window with tabs\n(Main, Monsters, Gear, Buffs, Route, Loot, Meta, Tuning).");
        cx += chipW + chipGap;
        if (IconButton("##chip_min", new Vector2(cx, cy), chip,
                minimized ? PhosphorIcons.ArrowsOutLineVertical : PhosphorIcons.ArrowsInLineVertical, chipFont, BtnFill, Mute, BtnBord, chipRound))
        {
            RynthAiDashboardState.SetMinimized(!minimized);
            _fit = true;   // the window fits the other mode's content next frame
        }
        SafeImGui.SetItemTooltip(minimized ? "Expand" : "Minimize");
        cx += chipW + chipGap;
        if (PanelRouter.CanPopOut)
        {
            if (IconButton("##chip_dock", new Vector2(cx, cy), chip, ImGuiPanelHost.PopOutGlyph, chipFont, BtnFill, Mute, BtnBord, chipRound))
                ImGuiPanelHost.TogglePopOut(Title);
            SafeImGui.SetItemTooltip(ImGuiPanelHost.PopOutTooltip);
        }

        // The size, from the title row (no host title bar here): Ctrl+wheel steps it, right-click
        // opens the size popup. The whole row, chips included; the body below keeps its wheel.
        if (ImGuiNET.ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem)
            && ImGuiNET.ImGui.IsMouseHoveringRect(new Vector2(x0, y), new Vector2(x0 + width, y + rowH), false))
        {
            // Ctrl+wheel: the panel host steps its size (over any panel).
            if (ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                ImGuiNET.ImGui.OpenPopup(SizePopupId);
        }

        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x0, y + rowH));
    }

    private const string DragTip = "Drag to move the dashboard.\nRight-click: dashboard size. Ctrl+wheel here: bigger or smaller.";

    /// <summary>
    /// The title row's right-click popup: the dashboard's size (its panel text size, the same
    /// setting as Settings > Interface > RynthAi Dashboard, or All Panels until it is set on its own).
    /// </summary>
    private static void SizePopup()
    {
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, ShellBg);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, BtnBord);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Px(8), Px(8)));
        bool open = ImGuiNET.ImGui.BeginPopup(SizePopupId);
        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.PopStyleColor(2);
        if (!open) return;
        try
        {
            ImGuiPanelHost.TextSizeRows(Title, _k, "Dashboard size");
        }
        finally
        {
            ImGuiNET.ImGui.EndPopup();
        }
    }

    // ── Header: macro + state (40%) | Profile/Nav/Loot/Meta/Buffs pickers (60%) ──

    private void HeaderGrid(RynthAiView? view, RynthAiSnapshot raw, float x0, float width)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        float y0 = ImGuiNET.ImGui.GetCursorScreenPos().Y + Px(2);
        float leftW = width * 0.4f;

        // Left: Meta State / Bot Activity (the macro start/stop is the control row's first square).
        ImFontPtr f10 = ImGuiFonts.Get(UiFont.Dash10);
        float ly = y0;
        dl.AddText(f10, f10.FontSize, new Vector2(x0, ly), Mute, "Meta State:");
        dl.AddText(f10, f10.FontSize, new Vector2(x0, ly + f10.FontSize + Px(1)), Amber, view?.MetaStateText ?? "Default");
        ly += 2 * (f10.FontSize + Px(1)) + Px(4);
        dl.AddText(f10, f10.FontSize, new Vector2(x0, ly), Mute, "Bot Activity:");
        dl.AddText(f10, f10.FontSize, new Vector2(x0, ly + f10.FontSize + Px(1)), Amber, view?.BotActivityText ?? "Idle");
        float leftBottom = ly + 2 * (f10.FontSize + Px(1));

        // Right: five selector rows (label 36 px, picker fills, spacing 3; x the size).
        float rx = x0 + leftW, rw = width - leftW, ry = y0;
        // The fold arrow sits at the end of the Profile row, where it always fits.
        float foldW = Px(18f), selH = Px(16), selStep = Px(16 + 3);
        Selector(0, "Profile:", view?.ProfileText ?? "Default", raw.Profiles, raw.SelectedProfileIdx, 3, rx, ry, rw - foldW - Px(2), null);
        if (IconButton("##files_fold", new Vector2(rx + rw - foldW, ry), new Vector2(foldW, selH), PhosphorIcons.CaretUp, UiFont.Dash11, BtnFill, Mute, BtnBord, Px(2)))
            RynthAiDashboardState.SetFilesOpen(false);
        ImGuiNET.ImGui.SetItemTooltip("Fold the loaded files into one line");
        ry += selStep;
        Selector(1, "Nav:", view?.NavText ?? "None", raw.NavProfiles, raw.SelectedNavIdx, 0, rx, ry, rw, null);
        ry += selStep;
        Selector(2, "Loot:", view?.LootText ?? "None", raw.LootProfiles, raw.SelectedLootIdx, 1, rx, ry, rw, raw);
        ry += selStep;
        Selector(3, "Meta:", view?.MetaText ?? "None", raw.MetaProfiles, raw.SelectedMetaIdx, 2, rx, ry, rw, null);
        ry += selStep;
        Selector(4, "Buffs:", view?.BuffText ?? "Built-in", raw.BuffProfiles, raw.SelectedBuffIdx, 4, rx, ry, rw, null, spellsButton: true);
        ry += selH;

        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x0, Math.Max(leftBottom, ry) + Px(4)));
    }

    private static readonly string[] SelectorIds = { "##sel_profile", "##sel_nav", "##sel_loot", "##sel_meta", "##sel_buff" };
    private static readonly string[] PickerIds = { "##pick_profile", "##pick_nav", "##pick_loot", "##pick_meta", "##pick_buff" };
    private static readonly string[] NoneItems = { "None" };

    /// <summary>
    /// One file picker row. <paramref name="kind"/> is RynthPluginSelectProfile's list (0 nav,
    /// 1 loot, 2 meta, 3 settings profile, 4 buff profile). <paramref name="lootEdit"/> adds the
    /// Loot Editor button, <paramref name="spellsButton"/> the RynthAi Spells window button.
    /// </summary>
    private void Selector(int slot, string label, string text, string[] items, int selected, int kind,
        float x, float y, float w, RynthAiSnapshot? lootEdit, bool spellsButton = false)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        ImFontPtr f10 = ImGuiFonts.Get(UiFont.Dash10), f9 = ImGuiFonts.Get(UiFont.Dash9);
        float rowH = Px(16), round = Px(2);
        dl.AddText(f10, f10.FontSize, new Vector2(x, y + (rowH - f10.FontSize) * 0.5f), Mute, label);

        float editW = 0;
        if (spellsButton)
        {
            editW = Px(18);
            var ep = new Vector2(x + w - editW, y);
            if (IconButton("##buff_spells", ep, new Vector2(editW, rowH), PhosphorIcons.Sparkle, UiFont.Dash11, SelectorBg, Mute, 0, round))
                RynthAiCommands.ApplyRemoteCommand("spells", "toggle");
            ImGuiNET.ImGui.SetItemTooltip("RynthAi Spells: browse the spell list and build buff profiles.");
            editW += Px(3);
        }
        if (lootEdit != null)
        {
            editW = Px(18);
            var ep = new Vector2(x + w - editW, y);
            if (IconButton("##loot_edit", ep, new Vector2(editW, rowH), PhosphorIcons.TreasureChest, UiFont.Dash11, SelectorBg, Mute, 0, round))
            {
                string path = !string.IsNullOrEmpty(lootEdit.VendorProfilePath) ? lootEdit.VendorProfilePath : lootEdit.CurrentLootPath;
                // The in-game Loot Editor panel. The external editor is one click away on the
                // panel's toolbar (LootEditorFace.LaunchExternal, an STA thread of its own).
                LootEditorFace.ToggleFor(path);
            }
            ImGuiNET.ImGui.SetItemTooltip("Loot Editor: the loot profile in use, or the open vendor's AutoVendor profile.");
            editW += Px(3);
        }

        var pos = new Vector2(x + Px(36), y);
        var size = new Vector2(Math.Max(Px(20), w - Px(36) - editW), rowH);
        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGuiNET.ImGui.InvisibleButton(SelectorIds[slot], size);
        bool hovered = ImGuiNET.ImGui.IsItemHovered();
        if (ImGuiNET.ImGui.IsItemActivated())
            _pressedWhileOpen[slot] = ImGuiNET.ImGui.GetFrameCount() - _pickerShownFrame[slot] <= 1;
        bool open = ImGuiNET.ImGui.IsPopupOpen(PickerIds[slot]);
        dl.AddRectFilled(pos, pos + size, hovered ? Lighten(SelectorBg) : SelectorBg, round);
        dl.AddRect(pos, pos + size, BtnBord, round);
        float arrowW = Px(12);
        dl.PushClipRect(pos, pos + size - new Vector2(arrowW + Px(4), 0), true);
        dl.AddText(f9, f9.FontSize, pos + new Vector2(Px(5), (rowH - f9.FontSize) * 0.5f), White, text);
        dl.PopClipRect();
        PhosphorIcons.DrawCentered(dl, f10, open ? PhosphorIcons.CaretUp : PhosphorIcons.CaretDown,
            new Vector2(pos.X + size.X - arrowW - Px(3), y), new Vector2(arrowW, rowH), Mute);

        if (clicked && !_pressedWhileOpen[slot] && !open)
        {
            ImGuiNET.ImGui.SetNextWindowPos(new Vector2(pos.X, pos.Y + size.Y + Px(2)));
            ImGuiNET.ImGui.OpenPopup(PickerIds[slot]);
        }
        Picker(slot, items.Length == 0 ? NoneItems : items, Math.Clamp(selected, 0, Math.Max(0, items.Length - 1)), kind, items.Length > 0);
    }

    private void Picker(int slot, string[] items, int selected, int kind, bool real)
    {
        // Same look as the Avalonia picker: 200 wide, teal border, radius 6 (x the size).
        float maxH = Math.Min(items.Length * Px(26) + Px(12), Math.Max(Px(60),
            ImGuiNET.ImGui.GetIO().DisplaySize.Y - ImGuiNET.ImGui.GetCursorScreenPos().Y - Px(20)));
        ImGuiNET.ImGui.SetNextWindowSize(new Vector2(Px(200), maxH));
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, ShellBg);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, Teal);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, Px(6f));
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 1f);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Px(4), Px(4)));
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(Px(2), Px(2)));
        bool open = ImGuiNET.ImGui.BeginPopup(PickerIds[slot]);
        ImGuiNET.ImGui.PopStyleVar(4);
        ImGuiNET.ImGui.PopStyleColor(2);
        if (!open) return;
        _pickerShownFrame[slot] = ImGuiNET.ImGui.GetFrameCount();

        float w = ImGuiNET.ImGui.GetContentRegionAvail().X;
        for (int i = 0; i < items.Length; i++)
        {
            ImGuiNET.ImGui.PushID(i);
            bool isSel = i == selected;
            Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
            if (ItemButton("##item", p, new Vector2(w, Px(24)), items[i], isSel ? TealSoft : BtnFill, isSel ? Teal : White))
            {
                if (real) RynthAiCommands.SelectProfile(kind, i);
                ImGuiNET.ImGui.CloseCurrentPopup();
            }
            ImGuiNET.ImGui.PopID();
        }
        ImGuiNET.ImGui.EndPopup();
    }

    private static bool ItemButton(string id, Vector2 pos, Vector2 size, string text, uint bg, uint fg)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGuiNET.ImGui.InvisibleButton(id, size);
        uint fill = ImGuiNET.ImGui.IsItemActive() ? Darken(bg) : ImGuiNET.ImGui.IsItemHovered() ? Lighten(bg) : bg;
        dl.AddRectFilled(pos, pos + size, fill, Px(3));
        dl.AddRect(pos, pos + size, BtnBord, Px(3));
        ImFontPtr f = ImGuiFonts.Get(UiFont.Dash11);
        dl.PushClipRect(pos, pos + size, true);
        dl.AddText(f, f.FontSize, pos + new Vector2(Px(8), (size.Y - f.FontSize) * 0.5f), fg, text);
        dl.PopClipRect();
        return clicked;
    }

    // ── Control row: (ON/OFF) combat, buffing, nav, looting, meta, [sort] ... force rebuff ──

    private const float Square = 24, SquareStep = 27, RowPad = 3;

    private void ControlRow(RynthAiView? view, RynthAiSnapshot raw, float x0, float width, bool minimized, bool popped, uint boxBg)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        float top = ImGuiNET.ImGui.GetCursorScreenPos().Y;
        float sq = Px(Square), sqStep = Px(SquareStep), round = Px(3);
        var square = new Vector2(sq, sq);
        float bottom = top + sq + 2 * Px(RowPad);
        dl.AddRectFilled(new Vector2(x0, top), new Vector2(x0 + width, bottom), boxBg, round);
        dl.AddRect(new Vector2(x0, top), new Vector2(x0 + width, bottom), BtnBord, round);

        float x = x0 + Px(5), y = top + Px(RowPad);
        // Macro start/stop: a square like the toggles, green with a Stop glyph while running.
        {
            bool on = raw.MacroRunning;
            // Running: a soft green halo around the square.
            if (on) dl.AddRect(new Vector2(x - Px(1.5f), y - Px(1.5f)), new Vector2(x + sq + Px(1.5f), y + sq + Px(1.5f)), RunGlow, round + Px(1.5f), ImDrawFlags.None, Px(2));
            if (IconButton("##macro", new Vector2(x, y), square, on ? PhosphorIcons.Stop : PhosphorIcons.Play,
                    UiFont.Dash14, on ? MacroRun : BarBg, on ? White : Mute, on ? Green : BtnBord, round))
                RynthAiCommands.ToggleMacro();
            SafeImGui.SetItemTooltip(on ? "Macro running. Click to stop it." : "Macro stopped. Click to start it.");
            x += sqStep + Px(6);
        }
        Toggle("##t_combat", PhosphorIcons.Sword, raw.CombatEnabled, 0, new Vector2(x, y), "Combat",
            "the Monsters panel (weapons, spells and rules per monster)", PanelRouter.ToggleMonsters);
        x += sqStep;
        Toggle("##t_buff", PhosphorIcons.Sparkle, raw.BuffingEnabled, 1, new Vector2(x, y), "Buffing",
            "the buffing settings", () => ShowSettingsTab("Buffing"));
        x += sqStep;
        Toggle("##t_nav", PhosphorIcons.SneakerMove, raw.NavigationEnabled, 2, new Vector2(x, y), "Navigation",
            "the Nav panel (routes)", () => PanelRouter.Toggle("Nav"));
        x += sqStep;
        string lootPath = raw.CurrentLootPath ?? string.Empty;
        Toggle("##t_loot", PhosphorIcons.Bag, raw.LootingEnabled, 3, new Vector2(x, y), "Looting",
            "the Loot Editor on the loot profile in use", () => LootEditorFace.ToggleFor(lootPath));
        x += sqStep;
        Toggle("##t_meta", PhosphorIcons.Code, raw.MetaEnabled, 4, new Vector2(x, y), "Meta (the macro rules)",
            "the Meta panel", () => PanelRouter.Toggle("Meta"));
        x += sqStep;
        // RynthSort's auto-sort, when RynthSort is loaded and knows the character.
        int sort = RynthSortWatch.State;
        if (sort >= 0)
        {
            bool on = sort == 1;
            if (IconButton("##t_sort", new Vector2(x, y), square, PhosphorIcons.SortAscending, UiFont.Dash14,
                    on ? TealSoft : BarBg, on ? Teal : Mute, on ? Teal : BtnBord, round))
                RynthSortCommands.SetAuto(!on);
            SafeImGui.SetItemTooltip(on ? "Auto-sort (RynthSort) on. Click to turn it off." : "Auto-sort (RynthSort) off. Click to turn it on.");
            x += sqStep;
        }

        // Popped out: the pickers fold out from a caret left of FR (expanded mode, while folded away).
        // Docked they are the Loaded files drawer on the left edge.
        float caretW = Px(16f);
        bool caret = popped && !minimized && !RynthAiDashboardState.FilesOpen;
        float frX = x0 + width - Px(5) - sq;
        if (caret)
        {
            if (IconButton("##files_open", new Vector2(frX - Px(4) - caretW, y), new Vector2(caretW, sq), PhosphorIcons.CaretDown,
                    UiFont.Dash11, BtnFill, Teal, BtnBord, Px(2)))
                RynthAiDashboardState.SetFilesOpen(true);
            SafeImGui.SetItemTooltip("Profile: " + (view?.ProfileText ?? "Default") + "\nNav: " + (view?.NavText ?? "None") +
                "\nLoot: " + (view?.LootText ?? "None") + "\nMeta: " + (view?.MetaText ?? "None") +
                "\nBuffs: " + (view?.BuffText ?? "Built-in") +
                "\n\nClick to show the Profile, Nav, Loot, Meta and Buffs pickers.");
        }

        // What the bot is doing, always in view (every mode, minimized too): between the toggles and FR.
        {
            ImFontPtr af = ImGuiFonts.Get(UiFont.Dash10);
            float ax = x + Px(2), aRight = (caret ? frX - Px(4) - caretW : frX) - Px(6);
            if (aRight - ax > Px(20))
            {
                string activity = view?.BotActivityText ?? "Idle";
                // A pill coloured by what the bot is doing, with its icon.
                ActivityStyle(activity, out uint fg, out uint bg, out string icon);
                float ph = MathF.Min(sq, af.FontSize + Px(6));
                float py = y + (sq - ph) * 0.5f;
                float iconW = icon.Length > 0 ? CalcWidth(af, icon) + Px(4) : 0;
                float pw = MathF.Min(aRight - ax, Px(8) + iconW + CalcWidth(af, activity) + Px(8));
                dl.PushClipRect(new Vector2(ax, y), new Vector2(aRight, y + sq), true);
                dl.AddRectFilled(new Vector2(ax, py), new Vector2(ax + pw, py + ph), bg, ph * 0.5f);
                float tx = ax + Px(8), ay = py + (ph - af.FontSize) * 0.5f;
                if (icon.Length > 0) dl.AddText(af, af.FontSize, new Vector2(tx, ay), fg, icon);
                dl.PushClipRect(new Vector2(ax, py), new Vector2(ax + pw - Px(6), py + ph), true);
                dl.AddText(af, af.FontSize, new Vector2(tx + iconW, ay), fg, activity);
                dl.PopClipRect();
                dl.PopClipRect();
                ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(ax, y));
                ImGuiNET.ImGui.InvisibleButton("##activity", new Vector2(aRight - ax, sq));
                SafeImGui.SetItemTooltip("Bot activity: " + activity + "\nMeta state: " + (view?.MetaStateText ?? "Default"));
            }
        }

        // Force rebuff, set apart at the right (DrakBot's place for it).
        if (IconButton("##fr", new Vector2(frX, y), square, PhosphorIcons.ArrowsClockwise,
                UiFont.Dash14, FrFill, Amber, BtnBord, round))
            RynthAiCommands.ForceRebuff();
        if (ImGuiNET.ImGui.IsItemClicked(ImGuiMouseButton.Right))
            RynthAiCommands.CancelForceRebuff();
        ImGuiNET.ImGui.SetItemTooltip("Force rebuff (FR): recast all buffs. Right-click: cancel.");

        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x0, bottom));
        ImGuiNET.ImGui.Dummy(new Vector2(width, 0));
    }

    /// <summary>
    /// A subsystem toggle: on is lit (teal, with a bar under the icon), off is dimmed so a subsystem
    /// left off stands out. Right-click runs <paramref name="onRight"/>, which opens
    /// <paramref name="rightWhat"/> (its settings).
    /// </summary>
    private void Toggle(string id, string icon, bool on, int subsystem, Vector2 pos, string name, string rightWhat, Action onRight)
    {
        float sq = Px(Square);
        if (IconButton(id, pos, new Vector2(sq, sq), icon, UiFont.Dash14, on ? TealSoft : BarBg, on ? Teal : Off, on ? Teal : BtnBord, Px(3)))
            RynthAiCommands.SetSubsystemEnabled(subsystem, !on);
        bool right = ImGuiNET.ImGui.IsItemClicked(ImGuiMouseButton.Right);
        SafeImGui.SetItemTooltip((on ? name + " on. Click to turn it off." : name + " off. Click to turn it on.") +
                                      "\nRight-click: " + rightWhat + ".");
        if (on)
            ImGuiNET.ImGui.GetWindowDrawList().AddRectFilled(new Vector2(pos.X + Px(5), pos.Y + sq - Px(3.5f)),
                new Vector2(pos.X + sq - Px(5), pos.Y + sq - Px(1.5f)), Teal, Px(1));
        if (right) onRight();
    }

    /// <summary>Opens Settings (if closed) on tab <paramref name="tab"/>.</summary>
    private static void ShowSettingsTab(string tab)
    {
        SettingsFace.RequestTab(tab);
        if (!ImGuiPanelHost.IsOpen(SettingsFace.Title)) PanelRouter.Toggle(SettingsFace.Title);
    }

    /// <summary>The activity pill's colours and icon: red in a fight, gold looting, teal moving, and so on; grey idle.</summary>
    private static void ActivityStyle(string activity, out uint fg, out uint bg, out string icon)
    {
        switch (activity)
        {
            case "Combat": fg = C(0xFFFF7A7A); bg = C(0xFF4A1A1A); icon = PhosphorIcons.Sword; break;
            case "Looting":
            case "Salvaging": fg = Amber; bg = FrFill; icon = PhosphorIcons.Bag; break;
            case "Navigating": fg = Teal; bg = TealSoft; icon = PhosphorIcons.SneakerMove; break;
            case "Buffing": fg = C(0xFFC2A8FF); bg = C(0xFF2E2350); icon = PhosphorIcons.Sparkle; break;
            case "Healing": fg = C(0xFFFF8A8A); bg = C(0xFF4A1A1A); icon = PhosphorIcons.Heart; break;
            case "Restoring mana": fg = C(0xFF6CB4FF); bg = C(0xFF15304D); icon = PhosphorIcons.Drop; break;
            case "Restoring stamina": fg = Green; bg = C(0xFF173D24); icon = PhosphorIcons.Lightning; break;
            default: fg = Mute; bg = C(0xFF1C2733); icon = ""; break;
        }
    }

    // ── Launchers: one row of icon buttons ──────────────────────────────

    private const float LauncherH = 22, LauncherGap = 2;

    private void Launchers(float x0, float width)
    {
        float y = ImGuiNET.ImGui.GetCursorScreenPos().Y;
        // Nav and Meta left the row (2026-10-14): right-click the Navigation and Meta toggles.
        // Hub left it 2026-10-15 (the Mini Remote: the bar button, Char's menu, its drawer tab).
        // World joins them when the engine has the world map (WorldMapFace; its tiles may not be installed).
        bool worldMap = ImGuiPanelHost.HasFace(WorldMapFace.Title);
        int buttons = worldMap ? 8 : 7;
        float gap = Px(LauncherGap);
        float bw = MathF.Floor((width - buttons * gap) / buttons);
        // Short labels only when every button has room for its icon and label.
        bool labels = bw >= Px(68);
        float x = x0;

        Launcher("##l_lua", PhosphorIcons.FileCode, "Lua", "Lua scripts", x, y, bw, labels, () => PanelRouter.Toggle("Lua"),
            ImGuiPanelHost.IsOpen("Lua"));
        x += bw + gap;
        // Monsters opens the Damage panel (the basic Monsters panel and its
        // Simple/Advanced chip were retired 2026-10-01).
        Launcher("##l_mon", PhosphorIcons.Skull, "Monsters", "Monsters: weapons, spells and rules per monster (the Damage panel)",
            x, y, bw, labels, PanelRouter.ToggleMonsters, ImGuiPanelHost.IsOpen(PanelRouter.Canonical("Monsters")));
        x += bw + gap;
        Launcher("##l_set", PhosphorIcons.Gear, "Settings", "Settings", x, y, bw, labels, () => PanelRouter.Toggle("Settings"),
            ImGuiPanelHost.IsOpen(SettingsFace.Title));
        x += bw + gap;
        Launcher("##l_map", PhosphorIcons.MapPin, "Map", "Dungeon Map (shows while you're in a dungeon)", x, y, bw, labels,
            () => PanelRouter.Toggle(DungeonMapFace.Title), ImGuiPanelHost.IsOpen(DungeonMapFace.Title));
        x += bw + gap;
        if (worldMap)
        {
            Launcher("##l_world", PhosphorIcons.GlobeHemisphereWest, "World", "World Map: Dereth, with your position, the route and places (also in the RynthNav panel)", x, y, bw, labels,
                () => PanelRouter.Toggle(WorldMapFace.Title), ImGuiPanelHost.IsOpen(WorldMapFace.Title));
            x += bw + gap;
        }
        Launcher("##l_items", PhosphorIcons.Backpack, "Items", "Items", x, y, bw, labels, () => PanelRouter.Toggle("Items"),
            ImGuiPanelHost.IsOpen("Items"));
        x += bw + gap;
        bool popped = ImGuiPopOuts.InPopOutFrame;
        Launcher("##l_patrol", PhosphorIcons.Footprints, "Patrol",
            popped ? "Patrol. Left-click: start dungeon patrol.  Right-click: routes & recorded hazards."
                   : "Patrol. Left-click: start dungeon patrol.  Right-click: the Patrol drawer (patrol settings, hazards and routes).",
            x, y, bw, labels, () => RynthAiCommands.SendNavCommand("{\"Cmd\":\"dunPatrol\"}"), !popped && _drawers.IsOpen(_patrol));
        if (ImGuiNET.ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            if (popped)
            {
                // Popped out there are no drawers: the popup, as before.
                ImGuiNET.ImGui.SetNextWindowPos(ImGuiNET.ImGui.GetItemRectMin(), ImGuiCond.Always, new Vector2(0, 1));
                ImGuiNET.ImGui.OpenPopup("##patrol");
            }
            else _drawers.Toggle(_patrol);
        }
        x += bw + gap;

        // Char opens RynthOracle's Character tab (character, level XP, session rates) since
        // 2026.10.5.54 (Infinite Leaftide's charms stay RynthILT's, in the Mini Remote's ILT tabs,
        // Char's right-click > Server); without RynthOracle, the Mini Remote's
        // Session Stats section. The Mini Remote as it was left is the bar button (/rc miniremote)
        // and "Mini Remote" in Char's right-click menu; its drawer is the drawer tab on the dashboard's
        // edge. (The Hub launcher, which opened the same window, left the row 2026-10-15.)
        Launcher("##l_char", PhosphorIcons.User, "Char",
            "Char. Left-click: RynthOracle's Character tab (Infinite Leaftide's charms and banking: the Mini Remote's ILT tabs).  Right-click: Character windows (Mini Remote, Spells, Quests, Pets, Crafting, Fellowship, Skills, Inventory...), then Server: this server's plugin windows.",
            x, y, bw, labels, ShowCharacter, CharacterOpen());
        if (ImGuiNET.ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            ImGuiNET.ImGui.SetNextWindowPos(ImGuiNET.ImGui.GetItemRectMin(), ImGuiCond.Always, new Vector2(0, 1));
            ImGuiNET.ImGui.OpenPopup("##charmenu");
        }

        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x0, y + Px(LauncherH)));
    }

    /// <param name="lit">Its window (or drawer) is open: teal icon and outline, with a bar along the bottom.</param>
    private void Launcher(string id, string icon, string label, string tooltip, float x, float y, float w, bool showLabel, Action? onClick, bool lit)
    {
        var pos = new Vector2(x, y);
        float lh = Px(LauncherH);
        var size = new Vector2(Math.Max(Px(10), w), lh);
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGuiNET.ImGui.InvisibleButton(id, size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight)
                       && ImGuiNET.ImGui.IsMouseReleased(ImGuiMouseButton.Left);
        SafeImGui.SetItemTooltip(tooltip);
        bool hot = ImGuiNET.ImGui.IsItemHovered();
        uint fill = ImGuiNET.ImGui.IsItemActive() ? Darken(BtnFill) : hot ? Lighten(BtnFill) : BtnFill;
        uint fg = lit ? Teal : hot ? White : Mute;
        dl.AddRectFilled(pos, pos + size, fill, Px(3));
        dl.AddRect(pos, pos + size, lit ? Teal : BtnBord, Px(3));
        if (lit) dl.AddRectFilled(new Vector2(pos.X + Px(3), pos.Y + lh - Px(2)), new Vector2(pos.X + size.X - Px(3), pos.Y + lh - Px(0.5f)), Teal);

        ImFontPtr iconFont = ImGuiFonts.Get(UiFont.Dash14);
        if (showLabel)
        {
            ImFontPtr f9 = ImGuiFonts.Get(UiFont.Dash9);
            float iconBox = Px(16);
            PhosphorIcons.DrawCentered(dl, iconFont, icon, pos + new Vector2(Px(4), 0), new Vector2(iconBox, lh), fg);
            dl.PushClipRect(pos, pos + size, true);
            dl.AddText(f9, f9.FontSize, new Vector2(pos.X + Px(4) + iconBox + Px(4), pos.Y + (lh - f9.FontSize) * 0.5f), fg, label);
            dl.PopClipRect();
        }
        else
        {
            PhosphorIcons.DrawCentered(dl, iconFont, icon, pos, size, fg);
        }
        if (clicked && onClick != null)
        {
            onClick();
        }
    }

    // ── Footer: FPS | version | uptime ─────────────────────────────────────

    private static string Clock(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes}:{t.Seconds:D2}";

    private static DateTime _clientStartedUtc = DateTime.MinValue;
    private static bool _clientStartRead;

    /// <summary>When acclient.exe started (read once; MinValue if Windows won't say).</summary>
    private static DateTime ClientStartedUtc()
    {
        if (_clientStartRead) return _clientStartedUtc;
        _clientStartRead = true;
        try
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            _clientStartedUtc = p.StartTime.ToUniversalTime();
        }
        catch { _clientStartedUtc = DateTime.MinValue; }
        return _clientStartedUtc;
    }

    private void Footer(RynthAiView? view, float x0, float width)
    {
        int fps = (int)MathF.Round(D3D9.EndSceneHook.MeasuredFps);
        if (fps != _fpsShown) { _fpsShown = fps; _fpsText = fps > 0 ? $"FPS {fps}" : "FPS —"; }
        // Bot: since this engine loaded (a hot reload restarts it). Client: since the game client
        // started (2026-10-08), which a reload doesn't touch.
        DateTime started = EntryPoint.InitStartedUtc;
        if (started != DateTime.MinValue)
        {
            DateTime now = DateTime.UtcNow;
            TimeSpan up = now - started;
            long secs = (long)up.TotalSeconds;
            if (secs != _upShown)
            {
                _upShown = secs;
                DateTime client = ClientStartedUtc();
                _upText = client != DateTime.MinValue
                    ? $"Bot {Clock(up)}  Client {Clock(now - client)}"
                    : $"Up {Clock(up)}";
            }
        }
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        ImFontPtr f10 = ImGuiFonts.Get(UiFont.Dash10);
        float y = ImGuiNET.ImGui.GetCursorScreenPos().Y;
        float fpsW = CalcWidth(f10, _fpsText), upW = CalcWidth(f10, _upText);
        // FPS coloured by rate: green smooth, amber workable, red struggling.
        uint fpsCol = fps <= 0 ? Mute : fps >= 45 ? Green : fps >= 25 ? Amber : C(0xFFFF6B6B);
        dl.AddText(f10, f10.FontSize, new Vector2(x0, y), fpsCol, _fpsText);
        dl.AddText(f10, f10.FontSize, new Vector2(x0 + width - upW, y), Mute, _upText);

        // The version between them: centred when there's room, else after FPS, clipped short of the uptime.
        string version = view?.Version ?? string.Empty;
        if (version.Length > 0)
        {
            float left = x0 + fpsW + Px(8), right = x0 + width - upW - Px(8);
            if (right > left)
            {
                float vw = CalcWidth(f10, version);
                float vx = x0 + (width - vw) * 0.5f;
                if (vx < left || vx + vw > right) vx = left;
                dl.PushClipRect(new Vector2(left, y), new Vector2(right, y + f10.FontSize + Px(2)), true);
                dl.AddText(f10, f10.FontSize, new Vector2(vx, y), Faint, version);
                dl.PopClipRect();
            }
        }
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x0, y + f10.FontSize + Px(2)));
        ImGuiNET.ImGui.Dummy(new Vector2(width, 0));
    }

    // ── Bars (bottom): player vitals, then the target ──────────────────────

    // A vital bar's row and the gap under it; three rows.
    private const float VitalH = 14, VitalGap = 3;

    private void Bars(RynthAiView? view, RynthAiSnapshot raw, float x0, float width, uint boxBg)
    {
        bool vitals = RynthAiDashboardState.ShowVitals, target = RynthAiDashboardState.ShowTargetBar;
        if (!vitals && !target) return;
        Gap(4);

        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        ImFontPtr f11 = ImGuiFonts.Get(UiFont.Dash11), f10 = ImGuiFonts.Get(UiFont.Dash10);
        // The target's stamina/mana rows are held while that option is on, so they
        // don't grow the box (and the window) as targets come and go.
        bool subRows = raw.ShowTargetStaminaMana;
        float targetH = f11.FontSize + Px(2) + Px(3) + Px(12) + (subRows ? 2 * (Px(13) + Px(3)) - Px(3) : 0);
        float vh = Px(VitalH), vgap = Px(VitalGap);
        float vitalsH = vh + vgap + vh + vgap + vh;
        float top = ImGuiNET.ImGui.GetCursorScreenPos().Y;
        float height = Px(5) + (vitals ? vitalsH : 0) + (vitals && target ? Px(6) : 0) + (target ? targetH : 0) + Px(4);
        var tl = new Vector2(x0, top);
        var br = new Vector2(x0 + width, top + height);
        dl.AddRectFilled(tl, br, boxBg, Px(3));
        dl.AddRect(tl, br, BtnBord, Px(3));

        float rx = x0 + Px(5), rw = width - Px(10), ry = top + Px(5);
        if (vitals)
        {
            VitalRow(dl, rx, ry, rw, PhosphorIcons.Heart, view?.Health ?? default, Hp, f10); ry += vh + vgap;
            VitalRow(dl, rx, ry, rw, PhosphorIcons.Lightning, view?.Stamina ?? default, Green, f10); ry += vh + vgap;
            VitalRow(dl, rx, ry, rw, PhosphorIcons.Drop, view?.Mana ?? default, Mana, f10); ry += vh;
            if (target) ry += Px(6);
        }
        if (target)
        {
            // Crosshair + name | health, then the 15-segment bar.
            string hpText = raw.TargetHealthDisplay;
            float hpW = CalcWidth(f11, hpText);
            float iconW = CalcWidth(f11, PhosphorIcons.Crosshair);
            dl.AddText(f11, f11.FontSize, new Vector2(rx, ry), Mute, PhosphorIcons.Crosshair);
            dl.PushClipRect(new Vector2(rx, ry), new Vector2(rx + rw - hpW - Px(4), ry + f11.FontSize + Px(2)), true);
            dl.AddText(f11, f11.FontSize, new Vector2(rx + iconW + Px(3), ry), TextDim, view?.TargetHeadline ?? "NO TARGET");
            dl.PopClipRect();
            dl.AddText(f11, f11.FontSize, new Vector2(rx + rw - hpW, ry), White, hpText);
            ry += f11.FontSize + Px(2) + Px(3);

            // 15-segment gradient bar, 10 high, 2 px between segments (x the size).
            int lit = view?.TargetSegmentsLit ?? 0;
            float segGap = Px(2);
            float segW = (rw - segGap * (RynthAiView.Segments - 1)) / RynthAiView.Segments;
            for (int i = 0; i < RynthAiView.Segments; i++)
            {
                float sx = rx + i * (segW + segGap);
                dl.AddRectFilled(new Vector2(sx, ry + Px(1)), new Vector2(sx + segW, ry + Px(11)), i < lit ? SegmentColors[i] : BarBg, Px(1));
            }
            ry += Px(12);

            if (subRows)
            {
                bool shown = view != null && view.ShowTargetSubBars;
                ry += Px(3);
                CompactBar(dl, rx, ry, rw, shown ? view!.TargetStamina : default, Green, f10); ry += Px(13) + Px(3);
                CompactBar(dl, rx, ry, rw, shown ? view!.TargetMana : default, Mana, f10);
            }
        }

        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x0, top + height));
        ImGuiNET.ImGui.Dummy(new Vector2(width, 0));
    }

    private static void CompactBar(ImDrawListPtr dl, float x, float y, float w, VitalView bar, uint color, ImFontPtr font)
    {
        var a = new Vector2(x, y + Px(1));
        var b = new Vector2(x + w, y + Px(12));
        dl.AddRectFilled(a, b, BarBg, Px(2));
        if (bar.Fraction > 0) dl.AddRectFilled(a, new Vector2(x + w * bar.Fraction, b.Y), color, Px(2));
        dl.AddText(font, font.FontSize, new Vector2(x + Px(4), y + Px(1) + (Px(11) - font.FontSize) * 0.5f), White, bar.Text ?? "");
    }

    private static void VitalRow(ImDrawListPtr dl, float x, float y, float w, string icon, VitalView bar, uint color, ImFontPtr font)
    {
        var a = new Vector2(x, y + Px(1));
        var b = new Vector2(x + w, y + Px(14));
        dl.AddRectFilled(a, b, BarBg, Px(6));
        if (bar.Fraction > 0) dl.AddRectFilled(a, new Vector2(x + w * bar.Fraction, b.Y), color, Px(6));
        float ty = y + Px(1) + (Px(13) - font.FontSize) * 0.5f;
        dl.AddText(font, font.FontSize, new Vector2(x + Px(4), ty), Mute, icon);
        dl.AddText(font, font.FontSize, new Vector2(x + Px(4) + CalcWidth(font, icon) + Px(3), ty), White, bar.Text ?? "");
    }

    // ── Char menu (right-click Char) ───────────────────────────────────
    // Top: "Character", the windows every server has. Bottom: the server section
    // (UI.ServerMenus): the installed server plugins' windows for the world you're on.

    /// <summary>
    /// Opens the Mini Remote window on one of its tabs ("" = the remote). Pets and Crafting show on
    /// every server (Quests and Fellowship are RynthOracle's windows since 2026-10-13); RynthILT's ILT
    /// tabs show once the server reports that feature on (and only with RynthILT), and until then
    /// the window shows the remote.
    /// </summary>
    private static void ShowMiniRemoteTab(string section)
    {
        ImGuiPanelHost.Open(MiniRemoteFace.Title);
        RynthAiCommands.ApplyRemoteCommand("hub", section.Length > 0 ? "tab " + section : "tab");
    }

    /// <summary>
    /// RynthOracle's Character tab ("/ro character"), or the Mini Remote's Session Stats section
    /// when RynthOracle isn't loaded. Every generic "Character" link goes here (Leaftide's Charms
    /// and Banking are RynthILT's Mini Remote tabs, in the menu's server half).
    /// </summary>
    private static void ShowCharacter()
    {
        if (UI.ScriptWindows.ScriptWindowRegistry.Has(OracleMainKey)) UI.ServerMenus.Run("/ro character");
        else UI.ServerMenus.Run("/rc miniremote section char");
    }

    /// <summary>The Char launcher is lit: RynthOracle's window is open (else the Mini Remote on Session Stats).</summary>
    private static bool CharacterOpen() =>
        UI.ScriptWindows.ScriptWindowRegistry.Has(OracleMainKey)
            ? UI.ScriptWindows.ScriptWindowRegistry.IsOpen(OracleMainKey)
            : ImGuiPanelHost.IsOpen(MiniRemoteFace.Title) && MiniRemoteFace.ShowsSection("char");

    /// <summary>A Char menu item that toggles a registered ImGui panel (checked while open).</summary>
    private static void PanelMenuItem(string title, string command)
    {
        if (!ImGuiPanelHost.HasFace(title)) return;
        if (ImGuiNET.ImGui.MenuItem(title, command, ImGuiPanelHost.IsOpen(title)))
            PanelRouter.Toggle(title);
    }

    /// <summary>
    /// A Character menu line that opens Skills (0), Attributes (1) or Progress (2): in RynthOracle when its tabs
    /// are there, else the standalone Skills panel on that tab. Checked while the place it opens is showing it.
    /// </summary>
    private static void SkillsMenuItem(string label, string command, int tab, string tooltip)
    {
        bool viaOracle = SkillsRouting.OracleTabsAvailable;
        if (!viaOracle && (tab != 0 || !ImGuiPanelHost.HasFace(SkillsFace.Title))) return;   // standalone Skills has its own tab strip
        bool on = viaOracle
            ? UI.ScriptWindows.ScriptWindowRegistry.IsOpen(OracleMainKey) && OracleSkillsEmbed.ActiveTab == tab
            : ImGuiPanelHost.IsOpen(SkillsFace.Title);
        if (ImGuiNET.ImGui.MenuItem(label, viaOracle ? SkillsRouting.OracleCommand(tab) : command, on))
            SkillsRouting.Open(tab);
        SafeImGui.SetItemTooltip(viaOracle ? "RynthOracle's " + label + " tab: " + tooltip : tooltip);
    }

    // RynthOracle's script window keys (RynthOraclePlugin: UiWindow keys).
    private const string OracleMainKey = "RynthOracle/Oracle";
    private const string OracleTimersKey = "RynthOracle/QuestTimers";

    /// <summary>
    /// A Character menu line for one of RynthOracle's script windows: toggles it as the bar does,
    /// checked while open, greyed out when RynthOracle isn't loaded (or hasn't drawn it yet).
    /// </summary>
    private static void OracleMenuItem(string label, string command, string key, string tooltip)
    {
        bool has = UI.ScriptWindows.ScriptWindowRegistry.Has(key);
        bool open = has && UI.ScriptWindows.ScriptWindowRegistry.IsOpen(key);
        if (ImGuiNET.ImGui.MenuItem(label, command, open, has))
            UI.ScriptWindows.ScriptWindowRegistry.PlayerSetVisible(key, !open);
        SafeImGui.SetItemTooltip(has ? tooltip : tooltip + " (RynthOracle isn't loaded.)");
    }

    private static void CharMenu()
    {
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, ShellBg);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, BtnBord);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Px(8), Px(8)));
        bool open = ImGuiNET.ImGui.BeginPopup("##charmenu");
        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.PopStyleColor(2);
        if (!open) return;
        try
        {
            ImGuiNET.ImGui.TextDisabled("Character");
            bool hasOracle = UI.ScriptWindows.ScriptWindowRegistry.Has(OracleMainKey);
            if (ImGuiNET.ImGui.MenuItem("Character", "/ro character", false, hasOracle)) ShowCharacter();
            SafeImGui.SetItemTooltip(hasOracle
                ? "RynthOracle's Character tab: level, XP and session rates. Infinite Leaftide's charms and banking are the Mini Remote's ILT tabs (Server, below)."
                : "RynthOracle's Character tab (RynthOracle isn't loaded).");
            if (ImGuiNET.ImGui.MenuItem("Mini Remote", "/rc miniremote", ImGuiPanelHost.IsOpen(MiniRemoteFace.Title)))
                ShowMiniRemoteTab("");
            if (ImGuiNET.ImGui.MenuItem("Spells", "/rc spells"))
                RynthAiCommands.ApplyRemoteCommand("spells", "toggle");
            ImGuiNET.ImGui.SetItemTooltip("RynthAi Spells: the spell catalog and buff profiles.");
            // Quests live in RynthOracle (its script windows), since 2026-10-13.
            OracleMenuItem("Quests", "/ro", OracleMainKey,
                "RynthOracle: every quest flag, timers, tracking, rewards and (Infinite Leaftide) the /qb list.");
            OracleMenuItem("Quest Timers", "/ro timers", OracleTimersKey,
                "RynthOracle's Quest Timers: the quests you track and the time left on each.");
            // The Mini Remote's universal tabs (every server); RynthILT's ILT tabs are in the server half.
            if (ImGuiNET.ImGui.MenuItem("Pets", "/rc miniremote tab pets")) ShowMiniRemoteTab("pets");
            ImGuiNET.ImGui.SetItemTooltip("The Mini Remote's Pets tab: summoning essences, priorities and the healing pet (RynthAi).");
            if (ImGuiNET.ImGui.MenuItem("Crafting", "/rc miniremote tab crafting")) ShowMiniRemoteTab("crafting");
            ImGuiNET.ImGui.SetItemTooltip("The Mini Remote's Crafting tab: armor, weapon, jewelry and item tinkering, chunking and arrows (RynthAi).");
            // Fellowship is RynthOracle's Fellowship tab ("/ro fellowship" opens the window on it).
            if (ImGuiNET.ImGui.MenuItem("Fellowship", "/ro fellowship", false, hasOracle)) UI.ServerMenus.Run("/ro fellowship");
            SafeImGui.SetItemTooltip(hasOracle
                ? "RynthOracle's Fellowship tab: members, leader, open / locked, XP sharing and (Infinite Leaftide) the /fship commands."
                : "RynthOracle's Fellowship tab (RynthOracle isn't loaded).");
            // Skills / Attributes / Progress live under RynthOracle (raise/train + ILT planners) when Oracle
            // provides the tabs; otherwise these open the standalone Skills panel (SkillsRouting).
            SkillsMenuItem("Skills", "/rc skills", 0, "Raise and train skills.");
            SkillsMenuItem("Attributes", "/ro attrs", 1, "Attributes and vitals.");
            SkillsMenuItem("Progress", "/rc progression", 2, "Augmentation, Enlightenment and XP planners (Infinite Leaftide).");
            PanelMenuItem(InventoryFace.Title, "/rc inventory");
            PanelMenuItem(ItemCountFace.Title, "/rc itemcount");
            PanelMenuItem(ItemToolFace.Title, "/rc itemtool");
            // RynthInventory's script window (same as /ginv and Inventory's GInv button).
            const string ginvKey = "RynthInventory/Main";
            bool hasGinv = UI.ScriptWindows.ScriptWindowRegistry.Has(ginvKey);
            if (ImGuiNET.ImGui.MenuItem("Global Inventory", "/ginv",
                    hasGinv && UI.ScriptWindows.ScriptWindowRegistry.IsOpen(ginvKey), hasGinv))
                UI.ScriptWindows.ScriptWindowRegistry.PlayerSetVisible(ginvKey,
                    !UI.ScriptWindows.ScriptWindowRegistry.IsOpen(ginvKey));
            SafeImGui.SetItemTooltip(hasGinv
                ? "Every character's saved packs and storage (RynthInventory)."
                : "Global Inventory (RynthInventory isn't loaded).");
            PanelMenuItem("Characters", "/rc characters");   // RynthNet's window (a plugin window since 2026-10-21)

            UI.ServerMenus.DrawImGuiMenu(separatorFirst: true);
        }
        finally
        {
            ImGuiNET.ImGui.EndPopup();
        }
    }

    // ── Patrol popup (right-click Patrol, popped out; docked it's the Patrol drawer) ──

    private void PatrolPopup()
    {
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, ShellBg);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, BtnBord);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Px(8), Px(8)));
        ImGuiNET.ImGui.SetNextWindowSizeConstraints(new Vector2(Px(260), 0), new Vector2(Px(260), Px(376)));
        bool open = ImGuiNET.ImGui.BeginPopup("##patrol");
        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.PopStyleColor(2);

        if (open != _patrolOpen)
        {
            _patrolOpen = open;
            if (open) { UiSources.Patrol.Subscribe(); UiSources.Patrol.RequestRefresh(); }
            else UiSources.Patrol.Unsubscribe();
        }
        if (!open) return;

        var snap = UiSources.Patrol.Current;
        PatrolInfo info = snap?.Value ?? new PatrolInfo();
        PatrolBody(info, withTitle: true);
        ImGuiNET.ImGui.EndPopup();
    }

    /// <summary>The patrol lists at the cursor (the popup's body and the Patrol drawer's).</summary>
    private static void PatrolBody(PatrolInfo info, bool withTitle)
    {
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Dash10));
        if (withTitle)
        {
            ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Dash11));
            SafeImGui.TextColored(RynthTheme.VecOf(RynthTheme.Accent), PatrolTitle);
            ImGuiNET.ImGui.PopFont();
        }

        Section("THIS DUNGEON");
        if (info.InDungeon)
        {
            SafeImGui.TextColored(RynthTheme.VecOf(RynthTheme.Text), $"0x{info.CurrentLandblock}  ·  {info.CurrentHazards} hazard cell(s)");
            if (info.CurrentHazards > 0)
            {
                ImGuiNET.ImGui.SameLine();
                if (Mini("Clear##cur"))
                    RynthAiCommands.SendNavCommand($"{{\"Cmd\":\"clearHazards\",\"NavName\":\"{info.CurrentLandblock}\"}}", refreshPatrol: true);
            }
            if (Mini(MarkHazardLabel))
                RynthAiCommands.SendNavCommand("{\"Cmd\":\"markHazardHere\"}", refreshPatrol: true);
            ImGuiNET.ImGui.SameLine();
            if (Mini("Unmark"))
                RynthAiCommands.SendNavCommand("{\"Cmd\":\"unmarkHazardHere\"}", refreshPatrol: true);
            ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Dash9));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, RynthTheme.VecOf(RynthTheme.TextMuted));
            ImGuiNET.ImGui.TextWrapped("Stand on the lava/acid, click Mark, then re-run Patrol.");
            ImGuiNET.ImGui.PopStyleColor();
            ImGuiNET.ImGui.PopFont();
        }
        else
        {
            ImGuiNET.ImGui.TextColored(RynthTheme.VecOf(RynthTheme.TextMuted), "Not in a dungeon.");
        }

        Section($"RECORDED HAZARDS ({info.Dungeons.Length})");
        if (info.Dungeons.Length == 0)
            ImGuiNET.ImGui.TextColored(RynthTheme.VecOf(RynthTheme.TextMuted), "None recorded yet.");
        for (int i = 0; i < info.Dungeons.Length; i++)
        {
            DungeonHazards d = info.Dungeons[i];
            ImGuiNET.ImGui.PushID(i);
            SafeImGui.TextColored(RynthTheme.VecOf(RynthTheme.Text), $"0x{d.Landblock}  ·  {d.Cells} cell(s)");
            ImGuiNET.ImGui.SameLine();
            if (Mini("Clear"))
                RynthAiCommands.SendNavCommand($"{{\"Cmd\":\"clearHazards\",\"NavName\":\"{d.Landblock}\"}}", refreshPatrol: true);
            ImGuiNET.ImGui.PopID();
        }
        if (info.Dungeons.Length > 0 && Mini("Clear all recorded hazards"))
            RynthAiCommands.SendNavCommand("{\"Cmd\":\"clearHazardsAll\"}", refreshPatrol: true);

        Section($"SAVED ROUTES ({info.Routes.Length})");
        if (info.Routes.Length == 0)
            ImGuiNET.ImGui.TextColored(RynthTheme.VecOf(RynthTheme.TextMuted), "No .nav routes saved.");
        foreach (string name in info.Routes)
            SafeImGui.TextColored(RynthTheme.VecOf(RynthTheme.Text), "• " + name);

        ImGuiNET.ImGui.PopFont();
    }

    private const string PatrolTitle = PhosphorIcons.Footprints + " PATROL & ROUTES";
    private const string MarkHazardLabel = PhosphorIcons.Warning + " Mark cell as hazard";

    private static void Section(string text)
    {
        ImGuiNET.ImGui.Dummy(new Vector2(0, Px(4)));
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Dash9));
        SafeImGui.TextColored(RynthTheme.VecOf(RynthTheme.TextMuted), text);
        ImGuiNET.ImGui.PopFont();
    }

    private static bool Mini(string label)
    {
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Dash9));
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Button, BtnFill);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Lighten(BtnFill));
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ButtonActive, Darken(BtnFill));
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Amber);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, BtnBord);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, Px(2f));
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(Px(6), Px(1)));
        bool clicked = ImGuiNET.ImGui.Button(label);
        ImGuiNET.ImGui.PopStyleVar(3);
        ImGuiNET.ImGui.PopStyleColor(5);
        ImGuiNET.ImGui.PopFont();
        return clicked;
    }

    // ── Shared helpers ────────────────────────────────────────────────────

    /// <summary>Draws the Avalonia face's flat button frame; returns true on left click.</summary>
    private static bool ButtonFrame(string id, Vector2 pos, Vector2 size, uint bg, uint border, float rounding)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGuiNET.ImGui.InvisibleButton(id, size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight)
                       && ImGuiNET.ImGui.IsMouseReleased(ImGuiMouseButton.Left);
        uint fill = ImGuiNET.ImGui.IsItemActive() ? Darken(bg) : ImGuiNET.ImGui.IsItemHovered() ? Lighten(bg) : bg;
        dl.AddRectFilled(pos, pos + size, fill, rounding);
        if (border != 0) dl.AddRect(pos, pos + size, border, rounding);
        return clicked;
    }

    /// <summary>A flat button with centred text; returns true on left click.</summary>
    private static bool Button(string id, Vector2 pos, Vector2 size, string text, UiFont font,
        uint bg, uint fg, uint border, float rounding)
    {
        bool clicked = ButtonFrame(id, pos, size, bg, border, rounding);
        ImFontPtr f = ImGuiFonts.Get(font);
        float tw = CalcWidth(f, text);
        ImGuiNET.ImGui.GetWindowDrawList().AddText(f, f.FontSize, pos + new Vector2((size.X - tw) * 0.5f, (size.Y - f.FontSize) * 0.5f), fg, text);
        return clicked;
    }

    /// <summary>A flat button with a Phosphor icon centred on it; returns true on left click.</summary>
    private static bool IconButton(string id, Vector2 pos, Vector2 size, string icon, UiFont font,
        uint bg, uint fg, uint border, float rounding)
    {
        bool clicked = ButtonFrame(id, pos, size, bg, border, rounding);
        PhosphorIcons.DrawCentered(ImGuiNET.ImGui.GetWindowDrawList(), ImGuiFonts.Get(font), icon, pos, size, fg);
        return clicked;
    }

    private static float CalcWidth(ImFontPtr font, string text)
    {
        ImGuiNET.ImGui.PushFont(font);
        float w = ImGuiNET.ImGui.CalcTextSize(text).X;
        ImGuiNET.ImGui.PopFont();
        return w;
    }

    private static uint Lighten(uint abgr) => Scale(abgr, 1.25f, 12);
    private static uint Darken(uint abgr) => Scale(abgr, 0.8f, 0);

    private static uint Scale(uint abgr, float k, int add)
    {
        uint a = abgr & 0xFF000000;
        uint r = (uint)Math.Min(255, (int)((abgr & 0xFF) * k) + add);
        uint g = (uint)Math.Min(255, (int)(((abgr >> 8) & 0xFF) * k) + add);
        uint b = (uint)Math.Min(255, (int)(((abgr >> 16) & 0xFF) * k) + add);
        return a | (b << 16) | (g << 8) | r;
    }

    private static void Gap(float h) => ImGuiNET.ImGui.Dummy(new Vector2(0, Px(h)));

    private static uint[] BuildSegmentColors()
    {
        var colors = new uint[RynthAiView.Segments];
        for (int i = 0; i < colors.Length; i++)
        {
            float t = (float)i / (colors.Length - 1);
            uint r, g, b;
            if (t < 0.33f) { float f = t / 0.33f; r = 255; g = (uint)(f * 255); b = 0; }
            else if (t < 0.66f) { float f = (t - 0.33f) / 0.33f; r = (uint)((1f - f) * 255); g = 255; b = 0; }
            else { float f = (t - 0.66f) / 0.34f; r = 0; g = 255; b = (uint)(f * 255); }
            colors[i] = C(0xFF000000 | (r << 16) | (g << 8) | b);
        }
        return colors;
    }

    private static readonly RynthAiSnapshot Empty = new();
}
