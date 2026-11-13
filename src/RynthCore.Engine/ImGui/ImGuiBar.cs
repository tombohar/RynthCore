// ============================================================================
//  RynthCore.Engine - ImGui/ImGuiBar.cs
//  The overlay bar's ImGui face (docs/IMGUI_PARITY_PLAN.md §2.0).
//
//  Same content as the Avalonia bar, as square Phosphor icon buttons with
//  the names in tooltips: the grip (RC: build stamp tooltip), one button per
//  registered panel except the ones the RynthAi launcher opens (PanelIcons;
//  an unknown panel shows its first letter), one per plugin script window that
//  asked for a button (WindowFlags.BarButton: RynthSort, Oracle, ...; tooltip =
//  its title; the set is rebuilt as plugins come and go), one per window a plugin
//  draws that asked for one (its manifest's "windows" with "bar", UI/Sections/
//  PluginWindows.cs; an engine panel such a window has taken over loses its own
//  button), reload (RL) and pop out.
//  A plugin window with a "barMenu" (plugin API v95) drops a column of icon buttons below its
//  bar icon after the mouse rests on it (BarMenuHover: open and close delays); a click runs that
//  item's chat command. The icon's own click still toggles the window.
//  A button whose window is open, docked or popped out, draws highlighted in the
//  theme's accent (RynthTheme.BarButtonOpen*); the ILT and Scripts buttons while
//  any window in their menu is (UI/BarOpenState.cs; one IsOpen lookup a key).
//  Dragging the grip moves the bar; the position is the shared bar= row in
//  panel_state.txt. Right-click the grip for the bar's menu: its size, its rows
//  and "Report a problem" (panel buttons have no menu). Pop out moves the ImGui
//  bar into its own window (ImGuiPopOuts, sized to its content; dock back
//  returns it).
//
//  The bar's size is its own text size step (PanelTextScale chrome row): the bar's
//  fonts come from that baked set and its metrics scale by the step's factor. Set it
//  from the grip's right-click menu, Ctrl+wheel over the bar, Settings > Interface or
//  /rc ui barsize.
//
//  Rows (BarLayout): the player picks how many rows the bar has (1-6) and the most
//  icons a row may hold (no limit, or 2-30): icons per row = ceil(icons / rows),
//  capped at the limit, so the cap can add rows. The icons (panels, plugin windows,
//  ILT, Scripts) fill rows left to right, top to bottom, each row starting under the
//  first icon: the grip and the collapse caret lead the first row only. RL and pop out
//  stay at the end of the first row, so one row looks as it always did and they keep
//  their spot as icons come and go on the rows below. The window sizes to its content
//  as before (AlwaysAutoResize; popped out, ImGuiPopOuts sizes to it); a folded bar is
//  still just the grip and caret. Set from the grip's right-click menu, Settings >
//  Display or /rc ui barrows / baricons; kept in panel_state.txt's barlayout= row.
//
//  The bar is always this ImGui face while the ImGui layer is on (Tom, 2026-10-14:
//  the Avalonia and Both faces are gone). The old choice (face key "__bar" in
//  panel_faces.txt, the grip's and panel buttons' Avalonia / ImGui / Both menu
//  items) is no longer offered, and a saved Avalonia or Both is read as ImGui
//  (ResolveFace); docked panels already ignore theirs (PanelRouter.ResolveDockedFace).
//  While the ImGui bar shows, the docked Avalonia bar is hidden and takes no
//  clicks (AvaloniaOverlay.DockedBarSuppressed). AC thread except where noted.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Threading;
using ImGuiNET;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.UI;
using RynthCore.Engine.UI.ScriptWindows;

namespace RynthCore.Engine.ImGuiBackend;

internal static class ImGuiBar
{
    public const string FaceKey = "__bar";

    // Opened from the RynthAi launcher, not the bar (AvaloniaOverlay.BuildRoot). Lua has
    // its own bar button since it became the RynthLua plugin. "Monsters" (retired 2026-10-01,
    // still registered by EntryPoint) must never get a button: PanelRouter sends it to Damage.
    private static readonly HashSet<string> LauncherOnly = new(StringComparer.OrdinalIgnoreCase)
        { "Monsters", "Damage", "Settings", "Nav", "Meta", "Items" };

    // Window: a plugin's script window with a bar button of its own (WindowFlags.BarButton);
    // the button toggles it through ScriptWindowRegistry, like the Scripts menu does.
    // OpenKey: the panel whose open state lights the button (BarOpenState.KeyFor).
    private sealed record ButtonInfo(string Title, string Id, string Icon, bool Letter, string Tooltip,
        string OpenKey, ScriptWindow? Window = null, UI.Sections.SectionBarMenuItem[]? Menu = null);

    // The bar icon dropdown (a plugin window's "barMenu"): the timer, last frame's "mouse is over the
    // dropdown", the menu-bearing buttons' rects this frame, and when the timer was last fed (a gap
    // means the bar wasn't drawn: start from a closed dropdown).
    private static readonly BarMenuHover _menuHover = new();
    private static bool _overMenu;
    private static readonly List<(ButtonInfo Button, Vector2 Min, Vector2 Max)> _menuRects = new();
    private const ImGuiWindowFlags MenuFlags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize
        | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoDocking
        | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav;

    // The open check every highlight uses (one set lookup a key), as one delegate: no allocation a frame.
    private static readonly Func<string, bool> IsOpenCheck = ImGuiPanelHost.IsOpen;

    // Engine panels with an ImGui face only (no OverlayHost entry) that get a bar button.
    // Item Count has no icon of its own since 2026-10-18: Inventory's Count button, /rc itemcount, the Character menu.
    // Skills moves under RynthOracle (Skills / Attributes / Progress tabs): its icon shows only while Oracle's
    // tabs can't be reached (SkillsRouting.ShowSkillsBarIcon), so nobody loses the way in. Mini Remote stays.
    private static readonly string[] ImGuiOnlyBarPanels =
        { Panels.InventoryFace.Title, Panels.SkillsFace.Title, Panels.SenseFace.Title, Panels.MiniRemoteFace.Title,
          Panels.ItemToolFace.Title };

    /// <summary>An ImGui-only panel's bar button is wanted: it has a face, and Skills only while Oracle can't host it.</summary>
    private static bool ImGuiOnlyWanted(string title) =>
        ImGuiPanelHost.HasFace(title)
        && (title != Panels.SkillsFace.Title || Panels.SkillsRouting.ShowSkillsBarIcon);

    // The ILT section: ImGui-only Infinite Leaftide panels (Bounties and Guardian, both RynthILT's).
    // They never get buttons of their own; one ILT button stands for them, shown only on Infinite
    // Leaftide (IltInfo.IsIlt) with RynthILT loaded (IltInfo.PluginLoaded), and only for the ones
    // registered (EntryPoint registers them when RynthILT is installed).
    private static readonly string[] IltPanels = { Panels.BountiesFace.Title, Panels.GuardianFace.Title };

    // Known panels' icons (DrakBot's where the same window exists); others show their first letter.
    private static readonly Dictionary<string, string> PanelIcons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RynthAi"] = PhosphorIcons.Robot,
        ["Chat"] = PhosphorIcons.ChatCircleText,
        ["ChatFilters"] = PhosphorIcons.Funnel,
        ["Radar"] = PhosphorIcons.Compass,
        ["Vision"] = PhosphorIcons.Eye,
        ["Nav"] = PhosphorIcons.MapTrifold,
        ["RynthNav"] = PhosphorIcons.NavigationArrow,
        ["Tracker"] = PhosphorIcons.ChartLineUp,
        ["Lua"] = PhosphorIcons.FileCode,
        ["Settings"] = PhosphorIcons.Gear,
        ["Damage"] = PhosphorIcons.ChartBar,
        ["Meta"] = PhosphorIcons.ListChecks,
        ["Items"] = PhosphorIcons.Backpack,
        ["Loot Editor"] = PhosphorIcons.TreasureChest,
        ["Skills"] = PhosphorIcons.GraduationCap,
        ["Sense"] = PhosphorIcons.Binoculars,
        ["Status"] = PhosphorIcons.Info,
        ["Inventory"] = PhosphorIcons.Package,   // not Backpack: the dashboard's Items launcher has that
        ["Log"] = PhosphorIcons.TerminalWindow,
        ["Item Count"] = PhosphorIcons.Bag,
        ["Item Tool"] = PhosphorIcons.Wrench,
        ["Mini Remote"] = PhosphorIcons.SquaresFour,
        ["Characters"] = PhosphorIcons.UsersThree,
        ["Item Tool"] = PhosphorIcons.Wrench,   // RynthAi's chest looter + Quick give
        // Plugin windows with a bar button of their own (script windows, by title).
        ["RynthSort"] = PhosphorIcons.SortAscending,
        ["Oracle"] = PhosphorIcons.Sparkle,
        ["Global Inventory"] = PhosphorIcons.GlobeHemisphereWest,
    };

    /// <summary>A panel's bar icon: its Phosphor icon, else its first letter (letter = true).</summary>
    internal static string IconFor(string title, out bool letter)
    {
        letter = !PanelIcons.TryGetValue(title, out string? icon);
        if (!letter) return icon!;
        return FirstLetter(title);
    }

    /// <summary>The bar's letter fallback: the first letter or digit of <paramref name="text"/>, upper-cased, else "?".</summary>
    private static string FirstLetter(string text)
    {
        foreach (char c in text)
            if (char.IsLetterOrDigit(c)) return char.ToUpperInvariant(c).ToString();
        return "?";
    }

    // Square buttons, logical px (x the DPI scale).
    private const float ButtonSize = 20f, ButtonGap = 3f;

    /// <summary>
    /// The bar's text size step (the nearest one already baked) and its scale factor. The one
    /// place either is worked out, so the bar's fonts and its metrics always agree.
    /// </summary>
    private static int ChromeStep(out float factor)
    {
        int step = ImGuiFonts.BakedStepNear(PanelTextScale.GetChrome());
        factor = PanelTextScale.Factor(step);
        return step;
    }

    private static ButtonInfo[]? _buttons;
    // What _buttons was built from: the plugin windows' snapshot (a new array on every change)
    // and which ImGui-only panels had a face. Either changing rebuilds the buttons.
    private static ScriptBarItem[]? _builtScriptButtons;
    private static int _builtImGuiOnlyMask;
    private static int _builtPluginWindows = -1;
    // The Scripts menu's panel keys (its button lights while any is open), rebuilt with the snapshot.
    private static ScriptBarItem[]? _scriptKeysFor;
    private static string[] _scriptKeys = Array.Empty<string>();
    private static string _tooltip = "";
    private static bool _placed;
    private static Vector2 _pos;
    // The bar's home (the bar= row, or where the player dragged it) and where that draws: pulled onto
    // the screen for the bar's size and the display, worked out again from the home whenever either
    // changes (UI/PanelPlacement). Only the player's drag writes the bar= row (2026-11-12: a pull from
    // a size change - a text size step, new rows, icons added as plugins load, a font re-bake - stayed
    // for the session and was saved, so the bar came back moved after a reload or login).
    private static readonly HomePlace Place = new();
    private static Vector2 _savedHome;   // the home the bar= row holds (logical units)
    private static bool _userMoved, _pullLogged;

    // The bar's rows and icons-per-row limit (BarLayout). Read from panel_state once, then kept
    // here so a change shows at once (the file is written in the background); one reference, so
    // a reader on another thread never sees half a change.
    private sealed record RowSettings(int Rows, int MaxPerRow);
    private static RowSettings? _rowSettings;
    // Icons per row as last drawn: where the icons-per-row "-" starts from when there is no limit.
    private static int _shownPerRow = 1;

    private static RowSettings RowLayout
    {
        get
        {
            RowSettings? cur = Volatile.Read(ref _rowSettings);
            if (cur != null) return cur;
            var (rows, max) = PanelStateStore.BarRows;
            Interlocked.CompareExchange(ref _rowSettings, new RowSettings(rows, max), null);
            return Volatile.Read(ref _rowSettings)!;
        }
    }

    /// <summary>The bar's rows (1-6). Any thread.</summary>
    public static int Rows => RowLayout.Rows;

    /// <summary>The most icons a bar row holds; 0 = no limit. Any thread.</summary>
    public static int MaxPerRow => RowLayout.MaxPerRow;

    /// <summary>Icons per row as the bar last drew them. Any thread.</summary>
    public static int ShownPerRow => Volatile.Read(ref _shownPerRow);

    /// <summary>Sets the bar's rows and icons-per-row limit (clamped; BarLayout) and saves them. Any thread.</summary>
    public static void SetRowLayout(int rows, int maxPerRow)
    {
        var next = new RowSettings(BarLayout.ClampRows(rows), BarLayout.ClampMaxPerRow(maxPerRow));
        if (next == RowLayout) return;
        Volatile.Write(ref _rowSettings, next);
        UiBackgroundWriter.Enqueue("persist bar rows", () => PanelStateStore.SetBarRows(next.Rows, next.MaxPerRow));
    }

    /// <summary>"Bar rows: 2, icons per row: no limit." for chat. Any thread.</summary>
    public static string DescribeRowLayout()
    {
        RowSettings cur = RowLayout;
        return $"Bar rows: {cur.Rows.ToString(CultureInfo.InvariantCulture)}, icons per row: {BarLayout.DescribeMaxPerRow(cur.MaxPerRow).ToLowerInvariant()}.";
    }

    private const string PopOutTitle = "__bar";
    // Popped out (the ImGui bar in its own window). Read from panel_state once,
    // then kept here so a switch shows at once (the file is written in the background).
    private static bool? _floating;
    private static bool Floating => _floating ??= PanelStateStore.BarFloating;

    /// <summary>True when the ImGui bar is popped out into its own window.</summary>
    public static bool PoppedOut => ResolveFace() == PanelFace.ImGui && Floating;

    /// <summary>
    /// The bar's docked face: ImGui whenever the ImGui layer is on, Avalonia only without it.
    /// A face saved in panel_faces.txt (Avalonia or Both, from before) is ignored. Any thread.
    /// </summary>
    public static PanelFace ResolveFace() =>
        Plugins.EngineSettings.EnableImGuiBackend ? PanelFace.ImGui : PanelFace.Avalonia;

    /// <summary>
    /// Shows/hides the docked Avalonia bar to match the bar's face. Call at
    /// init and whenever the face or the ImGui layer changes. Any thread.
    /// </summary>
    public static void ApplyFace()
    {
        AvaloniaOverlay.SetDockedBarSuppressed(ResolveFace() == PanelFace.ImGui);
        _placed = false; // re-read the shared position next frame
    }

    /// <summary>True when the ImGui bar should draw (the ImGui layer on, not popped out).</summary>
    public static bool Visible => ResolveFace() != PanelFace.Avalonia && !Floating;

    /// <summary>
    /// The panel store now holds another layout (UI/PanelLayout: another character's, the default, or a
    /// reset): the bar's place, rows and popped-out state are read from it again; a popped-out bar's
    /// window closes and SyncPopOut opens it at the new layout's spot. AC thread, between frames.
    /// </summary>
    public static void OnLayoutSwitched()
    {
        Volatile.Write(ref _rowSettings, null);
        _floating = null;
        _placed = false;
        _userMoved = false;
        if (ImGuiPopOuts.IsPopped(PopOutTitle)) ImGuiPopOuts.Close(PopOutTitle);
    }

    /// <summary>
    /// Opens or closes the bar's own window to match <see cref="PoppedOut"/>. AC
    /// thread, in the world, with the main context current (after the panels).
    /// </summary>
    public static void SyncPopOut(float s)
    {
        bool have = ImGuiPopOuts.IsPopped(PopOutTitle);
        if (PoppedOut && !have)
        {
            EnsureButtons();
            var saved = PanelStateStore.BarFloatingPosition;
            Vector2 at = saved.HasValue
                ? new Vector2((float)saved.Value.Left, (float)saved.Value.Top)
                : ImGuiPopOuts.ClientOriginOnScreen() + _pos;
            ImGuiPopOuts.Open(PopOutTitle, (int)at.X, (int)at.Y, (int)(400 * s), (int)(28 * s), 0, 0, s,
                draw: DrawPopped,
                onMoved: static (x, y) => UiBackgroundWriter.Enqueue("persist bar", () => PanelStateStore.SetBarFloatingState(true, x, y)),
                sizeToContent: true);
        }
        else if (!PoppedOut && have)
        {
            ImGuiPopOuts.Close(PopOutTitle);
        }
    }

    /// <summary>
    /// Pops the ImGui bar out into its own window (at the docked bar's screen
    /// spot) or docks it back where it was docked. The window opens/closes on
    /// the next frame (SyncPopOut). Any thread.
    /// </summary>
    public static void SetPoppedOut(bool popped)
    {
        if (popped == Floating) return;
        _floating = popped;
        if (popped)
        {
            Vector2 screen = ImGuiPopOuts.ClientOriginOnScreen() + _pos;
            UiBackgroundWriter.Enqueue("pop out bar", () => PanelStateStore.SetBarFloatingState(true, screen.X, screen.Y));
        }
        else
        {
            _placed = false; // back where it was docked (bar= row); the popped-out spot is kept
            var at = PanelStateStore.BarFloatingPosition ?? (0, 0);
            UiBackgroundWriter.Enqueue("dock bar", () => PanelStateStore.SetBarFloatingState(false, at.Item1, at.Item2));
        }
    }

    /// <summary>The bar in its own window: the same content at the pop-out's panel origin, sized to fit. AC thread.</summary>
    private static void DrawPopped(float s, Vector2 display)
    {
        EnsureButtons();
        int step = ChromeStep(out float factor);
        s *= factor;
        ImGuiNET.ImGui.SetNextWindowPos(ImGuiPopOuts.PanelOrigin, ImGuiCond.Always);
        PushBarWindowStyle(s);
        ImGuiNET.ImGui.Begin("##rc_bar", BarFlags);
        try
        {
            ImGuiFonts.CurrentStep = step;
            ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui11));
            DrawContent(s, popped: true);
            ImGuiNET.ImGui.PopFont();
            // Not on its first frame: an auto-resizing window has no content size yet
            // and reports just its padding (8x6), which would shrink the window to it.
            if (!ImGuiNET.ImGui.IsWindowAppearing())
                ImGuiPopOuts.ReportContentSize(ImGuiNET.ImGui.GetWindowSize());
        }
        finally
        {
            ImGuiFonts.CurrentStep = 0;
            ImGuiNET.ImGui.End();
            PopBarWindowStyle();
        }
    }

    private const ImGuiWindowFlags BarFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
        | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoMove
        | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoFocusOnAppearing
        | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNav;

    private static void PushBarWindowStyle(float s)
    {
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(4, 3) * s);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(ButtonGap, ButtonGap) * s);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, RynthTheme.BarRounding);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.WindowBg, RynthTheme.BarBackground);
    }

    private static void PopBarWindowStyle()
    {
        ImGuiNET.ImGui.PopStyleColor();
        ImGuiNET.ImGui.PopStyleVar(5);
    }

    public static void Draw(float s)
    {
        // ServerMenus.Tick runs from EngineFrameController every frame, bar shown or not.
        EnsureButtons();
        int step = ChromeStep(out float factor);
        s *= factor;
        float place = AvaloniaOverlay.InputScale > 0 ? AvaloniaOverlay.InputScale : 1f;

        if (!_placed)
        {
            var saved = PanelStateStore.BarPosition;
            Vector2 logical = saved.HasValue ? new Vector2((float)saved.Value.Left, (float)saved.Value.Top) : new Vector2(50, 5);
            _savedHome = logical;
            // The size isn't known before its first frame: KeepOnScreen fits it once the window has one.
            Place.Set(logical * place, Vector2.Zero, ImGuiNET.ImGui.GetIO().DisplaySize);
            _pos = Place.Drawn;
            ImGuiNET.ImGui.SetNextWindowPos(_pos, ImGuiCond.Always);
            _placed = true;
            _userMoved = false;
            _pullLogged = false;
            RynthLog.UI("ImGuiBar: " + Place.Describe("the bar", saved.HasValue ? "its saved bar row" : "the default spot (no bar row)")
                + $" Layout {PanelStateStore.CurrentPath}.");
        }

        PushBarWindowStyle(s);
        ImGuiNET.ImGui.Begin("##rc_bar", BarFlags);
        try
        {
            ImGuiFonts.CurrentStep = step;
            ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui11));
            DrawContent(s, popped: false);
            ImGuiNET.ImGui.PopFont();
            KeepOnScreen();
        }
        finally
        {
            ImGuiFonts.CurrentStep = 0;
            _pos = ImGuiNET.ImGui.GetWindowPos();
            ImGuiNET.ImGui.End();
            PopBarWindowStyle();
        }

        // Only the player's drag moves the bar's home and writes the bar= row (once the mouse is up).
        if (_userMoved && !ImGuiNET.ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            _userMoved = false;
            Vector2 logical = Place.Home / place;
            if (logical != _savedHome)
            {
                _savedHome = logical;
                double left = logical.X, top = logical.Y;
                UiBackgroundWriter.Enqueue("persist bar", () => PanelStateStore.SetBarPosition(left, top));
                AvaloniaOverlay.SyncDockedBarPosition(left, top);
            }
        }
    }

    /// <summary>
    /// A bigger size, new rows, icons added as plugins load or a smaller display can push the bar's right or
    /// bottom end off the screen: drawn from its home, pulled back only as far as needed, and back home once
    /// it fits again (UI/PanelPlacement). Never saved. Not while it is being dragged.
    /// </summary>
    private static void KeepOnScreen()
    {
        if (ImGuiNET.ImGui.IsMouseDown(ImGuiMouseButton.Left) && _userMoved) return;
        if (!Place.Fit(ImGuiNET.ImGui.GetWindowSize(), ImGuiNET.ImGui.GetIO().DisplaySize)) return;
        ImGuiNET.ImGui.SetWindowPos(Place.Drawn);
        if (Place.Pulled && !_pullLogged)
        {
            _pullLogged = true;
            RynthLog.UI("ImGuiBar: " + Place.DescribePull("the bar") + $" (bar row {HomePlace.Xy(_savedHome)}).");
        }
    }

    private static void DrawContent(float s, bool popped)
    {
        // Ctrl+wheel over the bar steps its size (the panels' title bars do the same).
        ImGuiIOPtr io = ImGuiNET.ImGui.GetIO();
        if (io.KeyCtrl && io.MouseWheel != 0f && ImGuiNET.ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem))
            PanelTextScale.SetChrome(PanelTextScale.GetChrome() + (io.MouseWheel > 0f ? 1 : -1));

        var square = new Vector2(ButtonSize, ButtonSize) * s;
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        ImFontPtr iconFont = ImGuiFonts.Get(UiFont.Ui14);

        // Grip (the RC label's place, in its light green): the bar's drag handle
        // (the Avalonia bar drags from its left edge, over the same spot).
        Vector2 rcPos = ImGuiNET.ImGui.GetCursorScreenPos();
        ImGuiNET.ImGui.InvisibleButton("##bardrag", new Vector2(square.X * 0.8f, square.Y));
        if (popped)
        {
            // Popped out, the window itself moves (it follows the cursor from here on).
            if (ImGuiNET.ImGui.IsItemActive() && ImGuiNET.ImGui.IsMouseDragging(ImGuiMouseButton.Left, 2f))
                ImGuiPopOuts.BeginMoveCurrent();
        }
        else if (ImGuiNET.ImGui.IsItemActive() && ImGuiNET.ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0f))
        {
            Vector2 display = ImGuiNET.ImGui.GetIO().DisplaySize;
            Vector2 size = ImGuiNET.ImGui.GetWindowSize();
            Vector2 next = ImGuiNET.ImGui.GetWindowPos() + ImGuiNET.ImGui.GetIO().MouseDelta;
            if (display.X > 1)
                next = Vector2.Clamp(next, Vector2.Zero, Vector2.Max(Vector2.Zero, display - size));
            ImGuiNET.ImGui.SetWindowPos(next);
            Place.MoveTo(next, size, display);   // the player's spot: the bar's home
            _userMoved = true;
        }
        SafeImGui.SetItemTooltip(_tooltip);
        if (ImGuiNET.ImGui.IsItemClicked(ImGuiMouseButton.Right))
            ImGuiNET.ImGui.OpenPopup("##bar_menu");
        PhosphorIcons.DrawCentered(dl, iconFont, PhosphorIcons.DotsSixVertical, rcPos, new Vector2(square.X * 0.8f, square.Y), RynthTheme.RcLabel);
        BarMenu(s);

        // Collapse: the bar folds to its grip and this caret.
        bool collapsed = RynthAiDashboardState.BarCollapsed;
        ImGuiNET.ImGui.SameLine(0, 0);
        Vector2 cPos = ImGuiNET.ImGui.GetCursorScreenPos();
        var caretSize = new Vector2(square.X * 0.6f, square.Y);
        if (ImGuiNET.ImGui.InvisibleButton("##barcollapse", caretSize))
            RynthAiDashboardState.SetBarCollapsed(!collapsed);
        bool caretHot = ImGuiNET.ImGui.IsItemHovered();
        SafeImGui.SetItemTooltip(collapsed ? "Show the bar" : "Fold the bar to this button");
        PhosphorIcons.DrawCentered(dl, iconFont, collapsed ? PhosphorIcons.CaretRight : PhosphorIcons.CaretLeft, cPos, caretSize,
            caretHot ? RynthTheme.RcLabel : 0xFF9A9A9Au);
        double menuNow = ImGuiNET.ImGui.GetTime();
        _menuRects.Clear();
        if (collapsed) { _menuHover.Update(null, false, menuNow); _overMenu = false; return; }

        // Rows (header): perRow icons a row, each row from iconsX (under the first icon);
        // RL and pop out go at the end of the first row (NextSlot, or after the icons when
        // they all fit on it).
        int iltCount = IltPanelCount();
        ScriptBarItem[] scripts = ScriptWindowRegistry.BarItems;
        int icons = _buttons!.Length + (iltCount > 0 ? 1 : 0) + (scripts.Length > 0 ? 1 : 0);
        RowSettings rows = RowLayout;
        int perRow = BarLayout.PerRow(icons, rows.Rows, rows.MaxPerRow);
        Volatile.Write(ref _shownPerRow, perRow);
        ImGuiNET.ImGui.SameLine();
        float iconsX = ImGuiNET.ImGui.GetCursorPosX();
        int slot = 0;

        PushBarButtonStyle(s);
        ImFontPtr letterFont = ImGuiFonts.Get(UiFont.UiBold11);
        // A button whose window is open (docked or popped out) draws in the theme's accent
        // (BarOpenState); a menu button (ILT, Scripts) while any window it lists is open.
        string? menuHovered = null;
        foreach (ButtonInfo b in _buttons!)
        {
            NextSlot(ref slot, perRow, iconsX, s, iconFont, popped);
            bool open = BarOpenState.IsOpen(b.OpenKey, IsOpenCheck);
            if (IconButton(b.Id, b.Letter ? letterFont : iconFont, b.Icon, square, RynthTheme.BarText, open))
            {
                if (b.Window != null) ScriptWindowRegistry.PlayerSetVisible(b.Window, !open);
                else PanelRouter.Toggle(b.Title);
            }
            if (b.Menu != null)
            {
                if (ImGuiNET.ImGui.IsItemHovered()) menuHovered = b.Id;
                _menuRects.Add((b, ImGuiNET.ImGui.GetItemRectMin(), ImGuiNET.ImGui.GetItemRectMax()));
            }
            if (_menuHover.OpenFor != b.Id) SafeImGui.SetItemTooltip(b.Tooltip);   // the dropdown sits where it would show
        }

        // The ILT section: one button, only on Infinite Leaftide with ILT panels registered
        // (Bounties, Guardian). With one panel it toggles that panel; with more it opens a menu.
        if (iltCount > 0)
        {
            NextSlot(ref slot, perRow, iconsX, s, iconFont, popped);
            string? only = iltCount == 1 ? FirstIltPanel() : null;
            bool open = BarOpenState.AnyOpen(IltPanels, IsOpenCheck);
            if (IconButton("##ilt", iconFont, PhosphorIcons.Leaf, square, RynthTheme.BarText, open))
            {
                if (only != null) PanelRouter.Toggle(only);
                else ImGuiNET.ImGui.OpenPopup("##ilt_menu");
            }
            SafeImGui.SetItemTooltip(only != null ? "Infinite Leaftide: " + only : "Infinite Leaftide");
        }

        // Script windows (decision 2): one button with a menu, only while a ShowInBar
        // script window exists. A window that also sets BarButton (plugin windows such as
        // RynthSort's) has a button of its own instead (EnsureButtons) and isn't listed here.
        if (scripts.Length > 0)
        {
            NextSlot(ref slot, perRow, iconsX, s, iconFont, popped);
            bool open = BarOpenState.AnyOpen(ScriptKeys(scripts), IsOpenCheck);
            if (IconButton("##scripts", iconFont, PhosphorIcons.AppWindow, square, RynthTheme.BarText, open))
                ImGuiNET.ImGui.OpenPopup("##scripts_menu");
            ImGuiNET.ImGui.SetItemTooltip("Script windows");
        }

        // Every icon fit on the first row: RL and pop out follow them.
        if (slot <= perRow) DrawControls(s, iconFont, popped);
        PopBarButtonStyle();

        // Drawn after the bar buttons' style is popped, so the menu has the normal look.
        ScriptsMenu(scripts);
        IltMenu();
        _menuHover.Update(menuHovered, _overMenu, menuNow);
        DrawBarMenu(s, square, iconFont, letterFont);
    }

    /// <summary>
    /// The bar icon dropdown of the button <see cref="BarMenuHover.OpenFor"/> names: a column of square icon
    /// buttons flush under the icon (flush above it when that would cross the bottom of the screen), clamped
    /// to the screen sideways. Its own window, not a popup, so the bar keeps its hover. A click runs the
    /// item's chat command as the player's. AC thread, inside the bar window.
    /// </summary>
    private static void DrawBarMenu(float s, Vector2 square, ImFontPtr iconFont, ImFontPtr letterFont)
    {
        _overMenu = false;
        string? openFor = _menuHover.OpenFor;
        if (openFor == null) return;
        ButtonInfo? owner = null;
        Vector2 min = default, max = default;
        foreach (var r in _menuRects)
            if (r.Button.Id == openFor) { owner = r.Button; min = r.Min; max = r.Max; break; }
        if (owner?.Menu == null || owner.Menu.Length == 0) { _menuHover.Close(); return; }

        UI.Sections.SectionBarMenuItem[] items = owner.Menu;
        Vector2 display = ImGuiNET.ImGui.GetIO().DisplaySize;
        float inset = 4f * s;                                                         // WindowPadding (4, 3)
        float width = square.X + 2f * inset;
        float height = items.Length * square.Y + (items.Length - 1) * ButtonGap * s + 6f * s;
        float x = min.X - inset, y = max.Y;                                           // icons straight under the parent
        if (display.X > 1 && display.Y > 1)
        {
            if (y + height > display.Y && min.Y - height >= 0f) y = min.Y - height;   // flush above instead
            x = Math.Max(0f, Math.Min(x, display.X - width));
        }
        ImGuiNET.ImGui.SetNextWindowPos(new Vector2(x, y), ImGuiCond.Always);

        PushBarWindowStyle(s);
        PushBarButtonStyle(s);
        bool begun = ImGuiNET.ImGui.Begin("##rc_bar_menu", MenuFlags);
        bool clicked = false;
        try
        {
            if (begun)
            {
                // NoFocusOnAppearing leaves a re-shown menu behind a panel focused since: lift it
                // every frame (no focus change, so another window's text edit stays active).
                MenuToFront();
                _overMenu = ImGuiNET.ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
                // ImGui truncates the window's position, which can leave the icons up to a pixel left of
                // the parent; put each one exactly where the placement meant (the parent's left edge
                // unless the viewport clamp moved the window). Each new line starts back at the window's
                // own content edge, so this is set per icon.
                float iconX = x + inset;
                foreach (UI.Sections.SectionBarMenuItem item in items)
                {
                    ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(iconX, ImGuiNET.ImGui.GetCursorScreenPos().Y));
                    bool letter = item.Icon.Length == 0;
                    string glyph = letter ? FirstLetter(item.Tooltip) : item.Icon;
                    if (IconButton(owner.Id + "_m_" + item.Id, letter ? letterFont : iconFont, glyph, square, RynthTheme.BarText))
                    {
                        if (!AcMainThreadQueue.EnqueueChatCommand(item.Command, ChatCommandOrigin.Player))
                            AcMainThreadQueue.EnqueueWriteToChat("[RynthCore] Bar menu: the chat command queue is full; try again.", 1);
                        clicked = true;
                    }
                    SafeImGui.SetItemTooltip(item.Tooltip);
                }
            }
        }
        finally
        {
            ImGuiNET.ImGui.End();
            PopBarButtonStyle();
            PopBarWindowStyle();
        }
        if (clicked) { _menuHover.Close(); _overMenu = false; }
    }

    private static IntPtr _getCurrentWindow, _bringToFront;
    private static int _frontState;   // 0 = not resolved, 1 = available, -1 = off

    /// <summary>
    /// Brings the current window (the bar menu) to the display front without focusing it, through
    /// cimgui's igGetCurrentWindow and igBringWindowToDisplayFront (resolved once, as ImGuiStackGuard
    /// does). Missing exports: logged once, and the menu keeps its normal order. Inside the menu's Begin.
    /// </summary>
    private static unsafe void MenuToFront()
    {
        if (_frontState == 0)
        {
            _frontState = -1;
            IntPtr module = EntryPoint.ImGuiNativeHandle;
            if (module != IntPtr.Zero
                && System.Runtime.InteropServices.NativeLibrary.TryGetExport(module, "igGetCurrentWindow", out IntPtr get)
                && System.Runtime.InteropServices.NativeLibrary.TryGetExport(module, "igBringWindowToDisplayFront", out IntPtr bring))
            {
                _getCurrentWindow = get;
                _bringToFront = bring;
                _frontState = 1;
            }
            else
                RynthLog.UI("ImGuiBar: this cimgui.dll has no igGetCurrentWindow / igBringWindowToDisplayFront exports; the bar menu can draw behind a panel focused since.");
        }
        if (_frontState < 0) return;
        void* window = ((delegate* unmanaged[Cdecl]<void*>)_getCurrentWindow)();
        if (window != null) ((delegate* unmanaged[Cdecl]<void*, void>)_bringToFront)(window);
    }

    /// <summary>
    /// Places the next icon: beside the last one, or at the start of a new row (under the first
    /// icon) every <paramref name="perRow"/> icons. Leaving the first row, RL and pop out go at
    /// its end first. AC thread, inside the bar window with the bar buttons' style pushed.
    /// </summary>
    private static void NextSlot(ref int slot, int perRow, float iconsX, float s, ImFontPtr iconFont, bool popped)
    {
        // Slot 0: DrawContent's SameLine after the caret already placed it.
        if (slot > 0)
        {
            if (slot % perRow != 0) ImGuiNET.ImGui.SameLine();
            else
            {
                if (slot == perRow) DrawControls(s, iconFont, popped);
                ImGuiNET.ImGui.SetCursorPosX(iconsX);
            }
        }
        slot++;
    }

    /// <summary>RL and pop out, after the first row's last icon (or the caret on a bar with no icons). AC thread.</summary>
    private static void DrawControls(float s, ImFontPtr iconFont, bool popped)
    {
        var square = new Vector2(ButtonSize, ButtonSize) * s;
        ImGuiNET.ImGui.SameLine(0, 6 * s);
        if (IconButton("##reload", iconFont, PhosphorIcons.ArrowsClockwise, square, RynthTheme.ReloadLabel))
        {
            // By hand: not held while a plugin objects (combat, looting); the player chose the moment.
            EntryPoint.NoteManualReload();
            ThreadPool.UnsafeQueueUserWorkItem(static _ => EngineLifecycle.SignalReload(), null);
        }
        ImGuiNET.ImGui.SetItemTooltip("Engine reload (RL) — shut down, FreeLibrary, reload from disk, re-init.");

        // The bar draws only as the ImGui face (ResolveFace), so it pops out as itself.
        ImGuiNET.ImGui.SameLine();
        if (IconButton("##popout", iconFont, popped ? PhosphorIcons.ArrowSquareIn : PhosphorIcons.ArrowSquareOut, square, RynthTheme.PopOutLabel))
            SetPoppedOut(!popped);
        SafeImGui.SetItemTooltip(popped ? "Dock the bar back into the game window." : "Pop the bar out into its own floating window.");
    }

    /// <summary>The ILT panels the bar offers now: 0 off Infinite Leaftide or without RynthILT. AC thread.</summary>
    private static int IltPanelCount()
    {
        int n = 0;
        foreach (string title in IltPanels)
            if (ImGuiPanelHost.HasFace(title)) n++;
        return n > 0 && IltInfo.IsIlt && IltInfo.PluginLoaded ? n : 0;
    }

    private static string? FirstIltPanel()
    {
        foreach (string title in IltPanels)
            if (ImGuiPanelHost.HasFace(title)) return title;
        return null;
    }

    /// <summary>The ILT button's menu (two or more ILT panels): a checkbox item each for open.</summary>
    private static void IltMenu()
    {
        if (!ImGuiNET.ImGui.BeginPopup("##ilt_menu")) return;
        try
        {
            ImGuiNET.ImGui.TextDisabled("Infinite Leaftide");
            foreach (string title in IltPanels)
            {
                if (!ImGuiPanelHost.HasFace(title)) continue;
                bool open = ImGuiPanelHost.IsOpen(title);
                if (ImGuiNET.ImGui.MenuItem(title, "", open))
                    PanelRouter.Toggle(title);
            }
        }
        finally
        {
            ImGuiNET.ImGui.EndPopup();
        }
    }

    /// <summary>
    /// The Scripts button's popup: the ShowInBar script windows grouped by script, a
    /// checkbox item each for open. Toggling tells the owner (Visibility, reason 2).
    /// </summary>
    private static void ScriptsMenu(ScriptBarItem[] items)
    {
        if (!ImGuiNET.ImGui.BeginPopup("##scripts_menu")) return;
        try
        {
            if (items.Length == 0) ImGuiNET.ImGui.CloseCurrentPopup();
            uint disabled = ImGuiNET.ImGui.GetColorU32(ImGuiCol.TextDisabled);
            foreach (ScriptBarItem item in items)
            {
                if (item.GroupStart)
                {
                    // A script's own text: never a format string.
                    ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, disabled);
                    ImGuiNET.ImGui.TextUnformatted(item.Group);
                    ImGuiNET.ImGui.PopStyleColor();
                }
                bool open = ImGuiPanelHost.IsOpen(item.PanelKey);
                if (ImGuiNET.ImGui.MenuItem(item.MenuLabel, "", open))
                    ScriptWindowRegistry.PlayerSetVisible(item.Window, !open);
            }
        }
        finally
        {
            ImGuiNET.ImGui.EndPopup();
        }
    }

    /// <summary>The Scripts menu's panel keys, rebuilt only when its snapshot changes (a new array then). AC thread.</summary>
    private static string[] ScriptKeys(ScriptBarItem[] items)
    {
        if (ReferenceEquals(items, _scriptKeysFor)) return _scriptKeys;
        var keys = new string[items.Length];
        for (int i = 0; i < items.Length; i++) keys[i] = items[i].PanelKey;
        _scriptKeys = keys;
        _scriptKeysFor = items;
        return keys;
    }

    /// <summary>
    /// A square bar button (the bar's button style) with an icon, or a letter, centred on it.
    /// <paramref name="open"/>: its window is open, so it draws in the theme's accent.
    /// </summary>
    private static bool IconButton(string id, ImFontPtr font, string icon, Vector2 size, uint color, bool open = false)
    {
        Vector2 at = ImGuiNET.ImGui.GetCursorScreenPos();
        if (open)
        {
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Button, RynthTheme.BarButtonOpen);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ButtonHovered, RynthTheme.BarButtonOpenHover);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, RynthTheme.BarButtonOpenBorder);
        }
        bool clicked = ImGuiNET.ImGui.Button(id, size);
        if (open) ImGuiNET.ImGui.PopStyleColor(3);
        PhosphorIcons.DrawCentered(ImGuiNET.ImGui.GetWindowDrawList(), font, icon, at, size, color);
        return clicked;
    }

    /// <summary>The grip's right-click menu: the bar's size, its rows and "Report a problem".</summary>
    private static void BarMenu(float s)
    {
        if (!ImGuiNET.ImGui.BeginPopup("##bar_menu")) return;
        ImGuiNET.ImGui.TextDisabled("RynthCore bar");
        BarSizeRows(s);
        BarLayoutRows(s);
        ReportRow();
        ImGuiNET.ImGui.EndPopup();
    }

    /// <summary>The bar's own popup: smaller / bigger, the current size, reset (the panels' text size popup, for the bar).</summary>
    private static void BarSizeRows(float s)
    {
        int step = PanelTextScale.GetChrome();
        int max = PanelTextScale.SizeNames.Length - 1;
        float btn = ButtonSize * s;
        ImFontPtr iconFont = ImGuiFonts.Get(UiFont.Ui14);
        uint text = ImGuiNET.ImGui.GetColorU32(ImGuiCol.Text);

        ImGuiNET.ImGui.Separator();
        ImGuiNET.ImGui.TextUnformatted("Bar size");
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
        ImGuiNET.ImGui.BeginDisabled(step <= 0);
        if (IconButton("##bs_smaller", iconFont, PhosphorIcons.Minus, new Vector2(btn, btn), text))
            PanelTextScale.SetChrome(step - 1);
        ImGuiNET.ImGui.EndDisabled();
        ImGuiNET.ImGui.SetItemTooltip("Smaller");
        ImGuiNET.ImGui.SameLine(0, 4 * s);
        ImGuiNET.ImGui.BeginDisabled(step >= max);
        if (IconButton("##bs_bigger", iconFont, PhosphorIcons.Plus, new Vector2(btn, btn), text))
            PanelTextScale.SetChrome(step + 1);
        ImGuiNET.ImGui.EndDisabled();
        ImGuiNET.ImGui.SetItemTooltip("Bigger");
        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.SameLine(0, 8 * s);
        ImGuiNET.ImGui.AlignTextToFramePadding();
        ImGuiNET.ImGui.TextUnformatted(PanelTextScale.Describe(step));

        ImGuiNET.ImGui.BeginDisabled(step == 0);
        if (ImGuiNET.ImGui.Button(PhosphorIcons.ArrowCounterClockwise + " Reset"))
            PanelTextScale.SetChrome(0);
        ImGuiNET.ImGui.EndDisabled();
        ImGuiNET.ImGui.SetItemTooltip("Back to normal size");
    }

    /// <summary>
    /// The grip menu's Rows and Icons per row steppers (BarLayout, header). Icons per row runs
    /// 2..30 then No limit; its "-" from No limit caps one below what a row shows now.
    /// </summary>
    private static void BarLayoutRows(float s)
    {
        RowSettings cur = RowLayout;
        ImGuiNET.ImGui.Separator();
        int d = Stepper("##br_rows", "Rows", cur.Rows.ToString(CultureInfo.InvariantCulture),
            cur.Rows > BarLayout.MinRows, cur.Rows < BarLayout.MaxRows, "Fewer rows", "More rows", s);
        if (d != 0) SetRowLayout(cur.Rows + d, cur.MaxPerRow);
        d = Stepper("##br_max", "Icons per row", BarLayout.DescribeMaxPerRow(cur.MaxPerRow),
            cur.MaxPerRow != BarLayout.MinPerRow, cur.MaxPerRow != BarLayout.NoLimit,
            "Fewer icons per row (more rows when the bar has more icons)", "More icons per row (past 30: no limit)", s);
        if (d != 0) SetRowLayout(cur.Rows, BarLayout.StepMaxPerRow(cur.MaxPerRow, d, ShownPerRow));
    }

    /// <summary>A label, then - and + buttons and the value (the bar size row's look). -1, 0 or +1 for the click.</summary>
    private static int Stepper(string id, string label, string value, bool canDown, bool canUp, string downTip, string upTip, float s)
    {
        float btn = ButtonSize * s;
        ImFontPtr iconFont = ImGuiFonts.Get(UiFont.Ui14);
        uint text = ImGuiNET.ImGui.GetColorU32(ImGuiCol.Text);
        int delta = 0;

        ImGuiNET.ImGui.TextUnformatted(label);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
        ImGuiNET.ImGui.BeginDisabled(!canDown);
        if (IconButton(id + "_down", iconFont, PhosphorIcons.Minus, new Vector2(btn, btn), text)) delta = -1;
        ImGuiNET.ImGui.EndDisabled();
        SafeImGui.SetItemTooltip(downTip);
        ImGuiNET.ImGui.SameLine(0, 4 * s);
        ImGuiNET.ImGui.BeginDisabled(!canUp);
        if (IconButton(id + "_up", iconFont, PhosphorIcons.Plus, new Vector2(btn, btn), text)) delta = 1;
        ImGuiNET.ImGui.EndDisabled();
        SafeImGui.SetItemTooltip(upTip);
        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.SameLine(0, 8 * s);
        ImGuiNET.ImGui.AlignTextToFramePadding();
        ImGuiNET.ImGui.TextUnformatted(value);
        return delta;
    }

    /// <summary>The grip menu's "Report a problem" entry (Report/ProblemReport.cs).</summary>
    private static void ReportRow()
    {
        if (!ImGuiPanelHost.HasFace(Panels.ReportFace.Title)) return;
        ImGuiNET.ImGui.Separator();
        if (ImGuiNET.ImGui.MenuItem(PhosphorIcons.Warning + " Report a problem..."))
            Report.ProblemReport.OpenPanel();
        ImGuiNET.ImGui.SetItemTooltip("Tell the staff what went wrong (also /rc report).");
    }

    private static void PushBarButtonStyle(float s)
    {
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 2f);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Button, RynthTheme.BarButton);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ButtonHovered, RynthTheme.FrameHover);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ButtonActive, RynthTheme.FrameActive);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, RynthTheme.BarButtonBorder);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, RynthTheme.BarText);
    }

    private static void PopBarButtonStyle()
    {
        ImGuiNET.ImGui.PopStyleColor(5);
        ImGuiNET.ImGui.PopStyleVar(3);
    }

    /// <summary>Bit i set: ImGuiOnlyBarPanels[i] gets a button now (a face, and for Skills no Oracle tabs).</summary>
    private static int ImGuiOnlyMask()
    {
        int mask = 0;
        for (int i = 0; i < ImGuiOnlyBarPanels.Length; i++)
            if (ImGuiOnlyWanted(ImGuiOnlyBarPanels[i])) mask |= 1 << i;
        return mask;
    }

    /// <summary>
    /// The bar's buttons, rebuilt when the set changes: a plugin window with a bar button
    /// appeared, went (plugin unloaded, window dropped) or was renamed, or an ImGui-only
    /// panel's face was registered or removed. AC thread, every frame (two cheap checks).
    /// </summary>
    private static void EnsureButtons()
    {
        ScriptBarItem[] scriptButtons = ScriptWindowRegistry.BarButtons;
        int mask = ImGuiOnlyMask();
        int pluginWindows = UI.Sections.PluginWindows.Version;
        if (_buttons != null && ReferenceEquals(scriptButtons, _builtScriptButtons) && mask == _builtImGuiOnlyMask
            && pluginWindows == _builtPluginWindows) return;
        _builtScriptButtons = scriptButtons;
        _builtImGuiOnlyMask = mask;
        _builtPluginWindows = pluginWindows;
        var list = new List<ButtonInfo>();
        // An engine panel a plugin window has taken over ("takeOver") has no engine button: the
        // window's own (below) stands in when it asks for one.
        foreach (var (title, _) in OverlayHost.GetPanels())
            if (!LauncherOnly.Contains(title) && !UI.Sections.PluginWindows.TakesOver(title))
            {
                string icon = IconFor(title, out bool letter);
                list.Add(new ButtonInfo(title, "##bar_" + title, icon, letter, title, BarOpenState.KeyFor(title, null)));
            }
        foreach (string title in ImGuiOnlyBarPanels)
            if (ImGuiOnlyWanted(title) && !LauncherOnly.Contains(title) && !UI.Sections.PluginWindows.TakesOver(title))
            {
                string icon = IconFor(title, out bool letter);
                list.Add(new ButtonInfo(title, "##bar_" + title, icon, letter, title, BarOpenState.KeyFor(title, null)));
            }
        // Windows plugins draw (their manifests' "windows" with "bar", docs/PLUGIN_WINDOWS.md): the
        // icon they name (a Phosphor glyph), else the title's letter; the button toggles the panel.
        foreach (UI.Sections.SectionWindowDecl w in UI.Sections.PluginWindows.BarWindows)
        {
            bool letter = false;
            string icon = w.Icon.Length > 0 ? w.Icon : IconFor(w.Title, out letter);
            list.Add(new ButtonInfo(w.Title, "##bar_pw_" + w.Plugin + "_" + w.Id, icon, letter, w.Title,
                BarOpenState.KeyFor(w.Title, null), Menu: w.Menu.Length > 0 ? w.Menu : null));
        }
        // Plugin script windows that asked for a button (RynthSort, Oracle, ...): after the
        // engine's own. Only while the plugin is loaded and has submitted the window.
        foreach (ScriptBarItem item in scriptButtons)
        {
            ScriptWindow w = item.Window;
            string title = w.Title.Length > 0 ? w.Title : w.Key;
            string icon = IconFor(title, out bool letter);
            string id = "##bar_sw" + w.Hash.ToString("X8");
            list.Add(new ButtonInfo(title, id, icon, letter, title, BarOpenState.KeyFor(title, w.PanelKey), Window: w));
        }
        _buttons = list.ToArray();
        _tooltip = $"RynthCore {EntryPoint.BuildStamp}\nDrag here to move the bar. Right-click: bar size and rows. Ctrl+wheel over the bar: size.";
    }
}
