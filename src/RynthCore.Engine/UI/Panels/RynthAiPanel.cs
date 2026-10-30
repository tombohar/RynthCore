// ============================================================================
//  RynthCore.Engine — UI/Panels/RynthAiPanel.cs
//  Avalonia replica of the ImGui RynthAi main dashboard (RynthSuite plugin).
//
//  Layout — top to bottom — mirrors LegacyDashboardRenderer:
//    • Title row    : "R" (teal) + "YNTHAI DASHBOARD" + "v4.0" + chips
//                     (Lock/-/+/_  — the wrapping panel frame already owns X)
//    • Header grid  : macro button + Meta State + Bot Activity (left, 40%)
//                     Profile / Nav / Loot / Meta dropdowns (right, 60%)
//    • Combat panel : 5 subsystem toggles + FR (left, 70px)
//                     target headline + segmented HP bar + player vital rows
//    • Launcher grid: 3 cols × 2-3 rows of GridBtns (visual only — opens
//                     no sub-windows yet; main-dashboard scope only).
//
//  Live data comes from UiDataHub (UiSources.RynthAi / UiSources.Patrol,
//  UI/Data/RynthAiData.cs), which calls the plugin's exports on the pump
//  thread; clicks go through RynthAiCommands. This face never calls a
//  plugin export itself (they used to run on the Avalonia thread, racing the
//  plugin tick). The ImGui face (ImGui/Panels/RynthAiFace.cs) reads the same
//  view.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RynthCore.Engine.Plugins;
using RynthCore.Engine.UI.Data;

namespace RynthCore.Engine.UI.Panels;

internal static partial class RynthAiPanel
{
    // ── Color palette — exact match to LegacyDashboardRenderer ImGui Vector4
    //    floats (multiplied by 255 and rounded). Don't deepen these; the ImGui
    //    contrast is what makes the dashboard read crisp against AC scenes.
    //
    //    ImGui Vec4               Hex          Use
    //    (0.15,0.85,0.90,1.00)    #26D9E6      ColTeal      (accents, RC mark)
    //    (0.91,0.70,0.20,1.00)    #E8B333      ColAmber     (state values)
    //    (0.25,0.85,0.45,1.00)    #40D973      ColGreen     (player ST, run dot)
    //    (0.85,0.90,0.95,1.00)    #D9E6F2      ColTextDim   (target headline)
    //    (0.55,0.65,0.75,1.00)    #8CA6BF      ColTextMute  (labels)
    //    (0.85,0.20,0.20,1.00)    #D93333      ColHp        (HP fill)
    //    (0.15,0.55,0.95,1.00)    #268CF2      ColMana      (MN fill)
    //    (0.08,0.12,0.16,1.00)    #141F29      ColBarBg     (vital bg, toggle off)
    //    (0.04,0.07,0.10,0.95)    #0A121A      ColPanelBg   (combat child window)
    //    (0.15,0.30,0.35,1.00)    #264C59      ColBtnOn     (toggle on bg)
    //    (0.06,0.12,0.18,1.00)    #0F1F2E      ColBtnFill   (idle button bg)
    //    (0.15,0.25,0.35,1.00)    #264059      ColBtnBord   (button border)
    //    (0.04,0.06,0.08,0.95)    #0A0F14      WindowBg     (panel shell)
    private static readonly IBrush ColTeal      = new SolidColorBrush(Color.FromRgb(0x26, 0xD9, 0xE6));
    private static readonly IBrush ColTealSoft  = new SolidColorBrush(Color.FromRgb(0x26, 0x4C, 0x59));
    private static readonly IBrush ColAmber     = new SolidColorBrush(Color.FromRgb(0xE8, 0xB3, 0x33));
    private static readonly IBrush ColGreen     = new SolidColorBrush(Color.FromRgb(0x40, 0xD9, 0x73));
    private static readonly IBrush ColMute      = new SolidColorBrush(Color.FromRgb(0x8C, 0xA6, 0xBF));
    private static readonly IBrush ColTextDim   = new SolidColorBrush(Color.FromRgb(0xD9, 0xE6, 0xF2));
    private static readonly IBrush ColText      = Brushes.White;
    private static readonly IBrush ColHp        = new SolidColorBrush(Color.FromRgb(0xD9, 0x33, 0x33));
    private static readonly IBrush ColMana      = new SolidColorBrush(Color.FromRgb(0x26, 0x8C, 0xF2));
    private static readonly IBrush ColShellBg   = new SolidColorBrush(Color.FromRgb(0x0A, 0x0F, 0x14));
    // Live-testing finding 2026-09-02: the header +/- opacity chips call the
    // plugin's RynthPluginAdjustOpacity export correctly (it mutates and
    // persists LegacyDashboardRenderer._bgOpacity, which flows into the
    // snapshot's bgOpacity field), but this Avalonia panel never actually
    // READ that field back and applied it anywhere — the buttons did
    // something on the plugin side that nothing ever displayed. Mirrors the
    // ImGui-era behavior (PushStyleColor(ImGuiCol.WindowBg, ..., _bgOpacity))
    // as closely as the Avalonia structure allows: this is a concrete
    // SolidColorBrush (not IBrush) so its .Color can be mutated in place —
    // every control sharing this instance updates immediately, no per-
    // control plumbing needed. Alpha is set from snap.BgOpacity in the
    // 33ms snapshot tick below.
    private static readonly SolidColorBrush ColPanelBg = new(Color.FromRgb(0x0A, 0x12, 0x1A));
    private static readonly IBrush ColBarBg     = new SolidColorBrush(Color.FromRgb(0x14, 0x1F, 0x29));
    private static readonly IBrush ColBtnFill   = new SolidColorBrush(Color.FromRgb(0x0F, 0x1F, 0x2E));
    private static readonly IBrush ColBtnBord   = new SolidColorBrush(Color.FromRgb(0x26, 0x40, 0x59));
    // Macro button: (0.10,0.35,0.15) running, (0.25,0.12,0.12) stopped.
    private static readonly IBrush ColMacroRun  = new SolidColorBrush(Color.FromRgb(0x1A, 0x59, 0x26));
    private static readonly IBrush ColMacroStop = new SolidColorBrush(Color.FromRgb(0x40, 0x1F, 0x1F));
    // FR button: (0.28,0.20,0.04).
    private static readonly IBrush ColFrFill    = new SolidColorBrush(Color.FromRgb(0x47, 0x33, 0x0A));

    /// <summary>
    /// Set by AvaloniaOverlay.TogglePanel before invoking the factory. The
    /// panel calls this with its in-panel title-row Border so the wrapping
    /// window can attach drag handlers without us needing a visible chrome
    /// bar above the panel content.
    /// </summary>
    internal static Action<Border>? AttachDragHandle { get; set; }

    /// <summary>Invoked by the ↗ chip to pop the panel into a floating window.</summary>
    internal static Action? RequestPopOut { get; set; }
    /// <summary>Invoked by the ↙ chip to redock the floating panel.</summary>
    internal static Action? RequestRedock { get; set; }
    /// <summary>Returns true while the panel is in a floating LayeredWindow.</summary>
    internal static Func<bool>? IsFloatingNow { get; set; }
    /// <summary>UI deep-dive finding TL;DR #7 (2026-07-02): overlay → this
    /// popout's FloatingPanelHost.MarkDirty(), called once per 33ms snapshot
    /// tick now that alwaysRender is dropped for RynthAi — see PopOutPanel.</summary>
    internal static Action? MarkDirty { get; set; }

    /// <summary>
    /// Live-testing finding 2026-09-02: overlay → resize the DOCKED wrapping
    /// panel's height to targetHeight, and move its MinHeight floor to
    /// newMinHeight (SetPanelSize clamps to the wrapping panel's own
    /// MinHeight, so the floor has to move with the preset or a shrink
    /// request would just get clamped back to the old floor). Wired only
    /// for the docked path today — the floating/popped-out LayeredWindow
    /// resize path has a documented history of fragility (modal-loop + DIB
    /// churn crashes, see rynthcore_floating_panel_resize) and isn't
    /// touched here without live verification; toggling minimize while
    /// popped out currently only hides/shows content, same as before this fix.
    /// </summary>
    internal static Action<double, double>? RequestDockedResize { get; set; }

    // Fixed preset heights for the minimize toggle (2026-09-02): Expanded
    // matches the wrapping panel's existing default MinHeight (260,
    // AvaloniaOverlay.cs); Minimized is a much shorter floor — title +
    // chips + the always-visible combat summary, no header/launcher/footer
    // rows. MinHeightFloor pairs are the new MinHeight to apply alongside
    // each target so the wrapping panel's own clamp doesn't fight it.
    private const double ExpandedHeightPreset   = 260;
    private const double ExpandedMinHeightFloor  = 260;
    private const double MinimizedHeightPreset  = 110;
    private const double MinimizedMinHeightFloor = 46;

    // The minimized state lives in UI/RynthAiDashboardState.cs, not here:
    // engine init reads it before the overlay starts, and touching this class
    // then would run its static brushes on the wrong thread.

    public const double ExpandedHeight = ExpandedHeightPreset, ExpandedMinHeight = ExpandedMinHeightFloor;
    public const double MinimizedHeight = MinimizedHeightPreset, MinimizedMinHeight = MinimizedMinHeightFloor;

    internal static Control Create()
    {
        RynthLog.Info("RynthAiPanel.Create: entry");
        RynthAiDashboardState.EnsureLoaded();

        // The latest hub view; its raw snapshot backs click handlers (current
        // toggle states, picker lists) exactly as the old per-tick snapshot did.
        RynthAiSnapshot snap = new();
        Border? activePicker = null;
        Button? activeAnchor = null;

        // ── Layout root: scrollable column hosting the dashboard ────────────
        // Cascadia Mono / Consolas fallback gives the dashboard a tighter,
        // more "tech" tone that reads sharper at small sizes than the default
        // Segoe UI proportional. UseLayoutRounding snaps borders / text to
        // whole pixels so nothing renders at sub-pixel offsets. FontFamily
        // is applied via TextElement's attached inherited property because
        // Grid itself doesn't expose one.
        var root = new Grid { UseLayoutRounding = true };
        Avalonia.Controls.Documents.TextElement.SetFontFamily(
            root, new FontFamily("Cascadia Mono, Consolas, Segoe UI"));

        var dashScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0)
        };
        root.Children.Add(dashScroll);

        var pickerCanvas = new Canvas { IsHitTestVisible = true };
        root.Children.Add(pickerCanvas);

        var dash = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            Margin = new Thickness(6, 4, 6, 6)
        };
        dashScroll.Content = dash;

        // ── Title row: "R YNTHAI DASHBOARD" + chips ─────────────────────────
        // Doubles as the drag surface — the wrapping window attaches drag
        // handlers to this Border via AttachDragHandle, so we don't need a
        // visible chrome bar above the panel.
        var titleBorder = new Border
        {
            // Transparent fill so the border is hit-testable across its full
            // width even where there's no child content (the title → chips gap).
            Background = Brushes.Transparent
        };
        var titleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        titleBorder.Child = titleRow;

        var titleStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(new TextBlock
        {
            Text = "R",
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = ColTeal,
            VerticalAlignment = VerticalAlignment.Center
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = "YNTHAI DASHBOARD",
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = ColText,
            VerticalAlignment = VerticalAlignment.Center
        });
        // RynthAi's version (its DLL's version resource), filled by the poll below —
        // the plugin may load after the panel is built, and changes on a reload.
        var versionText = new TextBlock
        {
            FontSize = 10,
            Foreground = ColMute,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 2, 0, 0)
        };
        titleStack.Children.Add(versionText);
        titleRow.Children.Add(titleStack);

        var chipRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        var lockChip     = CreateChip("Lock");
        var minusChip    = CreateChip("-");
        var plusChip     = CreateChip("+");
        var minChip      = CreateChip("_");
        var dockChip     = CreateChip("↗");
        chipRow.Children.Add(lockChip);
        chipRow.Children.Add(minusChip);
        chipRow.Children.Add(plusChip);
        chipRow.Children.Add(minChip);
        chipRow.Children.Add(dockChip);
        titleRow.Children.Add(chipRow);
        Grid.SetColumn(chipRow, 2);

        // Chip clicks must NOT also trigger drag — Button itself handles
        // PointerPressed and marks the event handled, so it doesn't bubble
        // to the title-border drag handler.
        lockChip.Click  += (_, _) => { ClosePicker(); RynthAiCommands.TogglePanelLock(); };
        minusChip.Click += (_, _) => { ClosePicker(); RynthAiCommands.AdjustOpacity(-0.1f); };
        plusChip.Click  += (_, _) => { ClosePicker(); RynthAiCommands.AdjustOpacity(0.1f); };
        minChip.Click   += (_, _) =>
        {
            ClosePicker();
            RynthAiDashboardState.SetMinimized(!RynthAiDashboardState.Minimized);
            // Live-testing finding 2026-09-02: reducing used to hide content
            // rows but leave the window's own size untouched. Actually
            // resize to the preset for the mode just switched to.
            RequestDockedResize?.Invoke(
                RynthAiDashboardState.Minimized ? MinimizedHeightPreset : ExpandedHeightPreset,
                RynthAiDashboardState.Minimized ? MinimizedMinHeightFloor : ExpandedMinHeightFloor);
        };
        dockChip.Click  += (_, _) =>
        {
            ClosePicker();
            if (IsFloatingNow?.Invoke() == true)
                RequestRedock?.Invoke();
            else
                RequestPopOut?.Invoke();
        };

        RynthLog.Info($"RynthAiPanel.Create: titleBorder built (AttachDragHandle={(AttachDragHandle != null ? "set" : "null")})");
        AttachDragHandle?.Invoke(titleBorder);
        dash.Children.Add(titleBorder);
        RynthLog.Info("RynthAiPanel.Create: titleBorder added");

        // ── Header grid (2-col, 40/60): macro+state | dropdowns ─────────────
        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("2*,3*"),
            RowDefinitions = new RowDefinitions("Auto"),
            Margin = new Thickness(0, 2, 0, 4)
        };

        // Left column ────────────────────────────────────────
        var leftStack = new StackPanel { Orientation = Orientation.Vertical, Spacing = 4 };

        var macroRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
        var macroButton = new Button
        {
            Content = "STOPPED",
            Width = 72,
            Height = 20,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = ColMacroStop,
            Foreground = ColText,
            BorderBrush = ColBtnBord,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(0)
        };
        macroButton.Click += (_, _) => { ClosePicker(); RynthAiCommands.ToggleMacro(); };
        macroRow.Children.Add(macroButton);

        // Status circle: 8px filled + 14px ring while running
        var statusDot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = ColMute,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0)
        };
        var statusRing = new Ellipse
        {
            Width = 14,
            Height = 14,
            Stroke = ColGreen,
            StrokeThickness = 1.5,
            IsVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(3, 0, 0, 0)
        };
        var statusOverlay = new Grid { Width = 16, VerticalAlignment = VerticalAlignment.Center };
        statusOverlay.Children.Add(statusDot);
        statusOverlay.Children.Add(statusRing);
        macroRow.Children.Add(statusOverlay);
        Grid.SetColumn(statusOverlay, 1);
        leftStack.Children.Add(macroRow);

        var metaStateRow = BuildLabelValueRow("Meta State:", out TextBlock metaStateValue);
        metaStateValue.Foreground = ColAmber;
        var botActivityRow = BuildLabelValueRow("Bot Activity:", out TextBlock botActivityValue);
        botActivityValue.Foreground = ColAmber;
        leftStack.Children.Add(metaStateRow);
        leftStack.Children.Add(botActivityRow);

        headerGrid.Children.Add(leftStack);

        // Right column ───────────────────────────────────────
        var rightStack = new StackPanel { Orientation = Orientation.Vertical, Spacing = 3 };

        Button profileSelector = CreateSelector("Default");
        Button navSelector     = CreateSelector("None");
        Button lootSelector    = CreateSelector("None");
        Button metaSelector    = CreateSelector("None");

        rightStack.Children.Add(BuildSelectorRow("Profile:", profileSelector));
        rightStack.Children.Add(BuildSelectorRow("Nav:",     navSelector));
        // ✎ opens the standalone Loot Editor on the loot profile in use - or, while a
        // vendor is open, on that vendor's AutoVendor profile (both are .utl files).
        var lootEditButton = new Button
        {
            Content = "✎",
            FontSize = 10,
            Padding = new Thickness(5, 0),
            Margin = new Thickness(3, 0, 0, 0),
            MinHeight = 0,
            Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x38, 0x47)),
            Foreground = ColMute,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        ToolTip.SetTip(lootEditButton, "Edit in the Loot Editor: the loot profile in use, or the open vendor's AutoVendor profile.");
        rightStack.Children.Add(BuildSelectorRow("Loot:",    lootSelector, lootEditButton));
        rightStack.Children.Add(BuildSelectorRow("Meta:",    metaSelector));

        headerGrid.Children.Add(rightStack);
        Grid.SetColumn(rightStack, 1);

        dash.Children.Add(headerGrid);

        // ── Combat panel: bordered child window ─────────────────────────────
        var combatBorder = new Border
        {
            Background = ColPanelBg,
            BorderBrush = ColBtnBord,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 3, 5, 3),
            MinHeight = 142
        };
        dash.Children.Add(combatBorder);

        var combatGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("56,*")
        };
        combatBorder.Child = combatGrid;

        // Left toggles column (5 toggles + FR) ────────────────
        var togglesPanel = new StackPanel { Orientation = Orientation.Vertical, Spacing = 2, Margin = new Thickness(0, 2, 0, 0) };

        // Inline ON/OFF macro button — only visible while the Avalonia panel
        // is minimized. Mirrors LegacyDashboardRenderer.RenderCombatPanel:
        // when minimized, ImGui draws a "ON"/"OFF" SmallButton above the
        // toggles so the user can still flip the macro without expanding
        // the panel.
        var minimizedMacroButton = new Button
        {
            Content = "OFF",
            Width = 51,
            Height = 20,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = ColMacroStop,
            Foreground = ColText,
            BorderBrush = ColBtnBord,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 4),
            IsVisible = false
        };
        ToolTip.SetTip(minimizedMacroButton, "Click to Start / Stop Macro");
        minimizedMacroButton.Click += (_, _) => { ClosePicker(); RynthAiCommands.ToggleMacro(); };
        togglesPanel.Children.Add(minimizedMacroButton);

        // Avalonia 11 Grid has no ColumnSpacing/RowSpacing; we lay the cells
        // out at exact pixel sizes (24+3 gap) so the toggles line up cleanly.
        var toggleGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("24,3,24"),
            RowDefinitions = new RowDefinitions("24,3,24")
        };
        var combatToggle = CreateSquareToggle("⚔");  // sword
        var buffToggle   = CreateSquareToggle("✦");  // buff
        var navToggle    = CreateSquareToggle("➤");  // shoe/move
        var lootToggle   = CreateSquareToggle("◫");  // bag
        toggleGrid.Children.Add(combatToggle); Grid.SetRow(combatToggle, 0); Grid.SetColumn(combatToggle, 0);
        toggleGrid.Children.Add(buffToggle);   Grid.SetRow(buffToggle, 0);   Grid.SetColumn(buffToggle, 2);
        toggleGrid.Children.Add(navToggle);    Grid.SetRow(navToggle, 2);    Grid.SetColumn(navToggle, 0);
        toggleGrid.Children.Add(lootToggle);   Grid.SetRow(lootToggle, 2);   Grid.SetColumn(lootToggle, 2);
        togglesPanel.Children.Add(toggleGrid);

        var metaToggle = new Button
        {
            Content = "MACRO",
            Width = 51,
            Height = 16,
            FontSize = 9,
            FontWeight = FontWeight.SemiBold,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = ColBarBg,
            Foreground = ColMute,
            BorderBrush = ColBtnBord,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 2, 0, 0)
        };
        togglesPanel.Children.Add(metaToggle);

        var frButton = new Button
        {
            Content = "FR",
            Width = 51,
            Height = 14,
            FontSize = 9,
            FontWeight = FontWeight.SemiBold,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = ColFrFill,
            Foreground = ColAmber,
            BorderBrush = ColBtnBord,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 1, 0, 0)
        };
        ToolTip.SetTip(frButton, "Force-recast all buffs.");
        frButton.Click += (_, _) => { ClosePicker(); RynthAiCommands.ForceRebuff(); };
        // Right-click cancel
        frButton.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(frButton).Properties.IsRightButtonPressed)
            {
                e.Handled = true;
                RynthAiCommands.CancelForceRebuff();
            }
        };
        togglesPanel.Children.Add(frButton);

        combatGrid.Children.Add(togglesPanel);

        combatToggle.Click += (_, _) => { ClosePicker(); RynthAiCommands.SetSubsystemEnabled(0, !snap.CombatEnabled); };
        buffToggle.Click   += (_, _) => { ClosePicker(); RynthAiCommands.SetSubsystemEnabled(1, !snap.BuffingEnabled); };
        navToggle.Click    += (_, _) => { ClosePicker(); RynthAiCommands.SetSubsystemEnabled(2, !snap.NavigationEnabled); };
        lootToggle.Click   += (_, _) => { ClosePicker(); RynthAiCommands.SetSubsystemEnabled(3, !snap.LootingEnabled); };
        metaToggle.Click   += (_, _) => { ClosePicker(); RynthAiCommands.SetSubsystemEnabled(4, !snap.MetaEnabled); };

        // Right column: target + vitals ──────────────────────
        var vitalsStack = new StackPanel { Orientation = Orientation.Vertical, Spacing = 3, Margin = new Thickness(6, 2, 0, 0) };

        var targetLine = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var targetLabel = new TextBlock
        {
            Text = "NO TARGET",
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = ColTextDim,
            VerticalAlignment = VerticalAlignment.Center
        };
        var targetHp = new TextBlock
        {
            Text = "0",
            FontSize = 11,
            Foreground = ColText,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        targetLine.Children.Add(targetLabel);
        targetLine.Children.Add(targetHp);
        Grid.SetColumn(targetHp, 1);
        vitalsStack.Children.Add(targetLine);

        var targetBar = BuildSegmentedBar(out Rectangle[] targetSegments);
        vitalsStack.Children.Add(targetBar);
        // UI deep-dive TL;DR #4 / P1-A (2026-07-02): lit-count gate — see
        // UpdateSegmentedBar. -1 sentinel forces the first real update to run.
        int targetSegLastLit = -1;

        // Optional target ST/MN bars (only shown when ShowTargetStaminaMana)
        var targetStRow = BuildCompactBar(out Rectangle targetStFill, out TextBlock targetStLabel, ColGreen);
        var targetMnRow = BuildCompactBar(out Rectangle targetMnFill, out TextBlock targetMnLabel, ColMana);
        targetStRow.IsVisible = false;
        targetMnRow.IsVisible = false;
        vitalsStack.Children.Add(targetStRow);
        vitalsStack.Children.Add(targetMnRow);

        vitalsStack.Children.Add(new TextBlock
        {
            Text = "PLAYER VITALS",
            FontSize = 9,
            Foreground = ColMute,
            Margin = new Thickness(0, 3, 0, 0)
        });

        var hpRow = BuildVitalRow("♥", ColHp,    out Rectangle hpFill, out TextBlock hpText);
        var stRow = BuildVitalRow("➤", ColGreen, out Rectangle stFill, out TextBlock stText);
        var mnRow = BuildVitalRow("◆", ColMana,  out Rectangle mnFill, out TextBlock mnText);
        vitalsStack.Children.Add(hpRow);
        vitalsStack.Children.Add(stRow);
        vitalsStack.Children.Add(mnRow);

        combatGrid.Children.Add(vitalsStack);
        Grid.SetColumn(vitalsStack, 1);

        // ── Launcher grid: 3-col × 2-row ────────────────────────────────────
        var launcherGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            Margin = new Thickness(0, 4, 0, 0)
        };
        AddSplitLauncher(launcherGrid, 0, 0, "Meta", "⚙", "Lua", "<>",
            onLeftClick:  () => PanelRouter.Toggle("Meta"),
            onRightClick: () => PanelRouter.Toggle("Lua"));
        AddMonstersLauncher(launcherGrid, 0, 1);
        AddLauncher(launcherGrid, 0, 2, "Settings",    "⚒",
            onClick: () => PanelRouter.Toggle("Settings"));
        // Map: RynthAi's dungeon map is an ImGui overlay window; the right half toggles it like "/ra map".
        var (_, mapBtn) = AddSplitLauncher(launcherGrid, 1, 0, "Nav", "➤", "Map", "🗺",
            onLeftClick: () => PanelRouter.Toggle("Nav"),
            onRightClick: () =>
            {
                ClosePicker();
                RynthAiCommands.ApplyRemoteCommand("map", "toggle");
            });
        ToolTip.SetTip(mapBtn, "Show/hide the dungeon map (/ra map). It appears when you are indoors.");
        AddLauncher(launcherGrid, 1, 1, "Items",       "🛡",
            onClick: () => PanelRouter.Toggle("Items"));
        var patrolBtn = AddLauncher(launcherGrid, 1, 2, "Patrol", "⬡",
            onClick: () => RynthAiCommands.SendNavCommand("{\"Cmd\":\"dunPatrol\"}"));
        ToolTip.SetTip(patrolBtn, "Left-click: start dungeon patrol.  Right-click: routes & recorded hazards.");
        // Right-click → patrol management flyout (saved routes + recorded dungeon hazards).
        patrolBtn.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(patrolBtn).Properties.IsRightButtonPressed)
            {
                e.Handled = true;
                ShowPatrolFlyout(patrolBtn);
            }
        };
        // Char: same as typing "/ra hub show". The Mini Remote is an ImGui overlay window with no
        // Avalonia panel, so this is the panel's way to open it.
        var charBtn = AddLauncher(launcherGrid, 2, 0, "Char", "♥",
            onClick: () =>
            {
                ClosePicker();
                RynthAiCommands.ApplyRemoteCommand("hub", "show");
            });
        ToolTip.SetTip(charBtn, "Open the Mini Remote (/ra hub show).");

        // Hub: the Mini Remote and the Inventory HUDs setup are ImGui windows inside RynthAi.
        var hudBtn = AddLauncher(launcherGrid, 2, 1, "Hub", "▣",
            onClick: () =>
            {
                ClosePicker();
                RynthAiCommands.ApplyRemoteCommand("remote", "toggle");
            });
        ToolTip.SetTip(hudBtn, "Left-click: show/hide the Mini Remote (/ra remote).  Right-click: Inventory HUDs setup (/ra huds).");
        hudBtn.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(hudBtn).Properties.IsRightButtonPressed)
            {
                e.Handled = true;
                ClosePicker();
                RynthAiCommands.ApplyRemoteCommand("huds", "show");
            }
        };

        dash.Children.Add(launcherGrid);

        // ── Footer: live FPS + engine uptime ───────────────────────────────
        // Two-cell row anchored to the bottom of the dashboard. FPS sourced
        // from EndSceneHook.MeasuredFps (refreshed once/sec on the AC pump
        // thread), uptime from EntryPoint.InitStartedUtc (re-stamped on every
        // hot-reload generation so the value reflects the *current* gen's
        // lifetime — useful for the ongoing heap-corruption diagnostic where
        // each gen seems to die ~5 min in). Same muted palette as labels;
        // hidden when the dashboard is minimised (matches launcherGrid).
        var footerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(0, 6, 0, 0)
        };
        var fpsText = new TextBlock
        {
            Text = "FPS —",
            FontSize = 10,
            Foreground = ColMute,
            VerticalAlignment = VerticalAlignment.Center
        };
        var uptimeText = new TextBlock
        {
            Text = "Up —",
            FontSize = 10,
            Foreground = ColMute,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        Grid.SetColumn(fpsText, 0);
        Grid.SetColumn(uptimeText, 2);
        footerGrid.Children.Add(fpsText);
        footerGrid.Children.Add(uptimeText);
        dash.Children.Add(footerGrid);

        // ── Picker overlay ─────────────────────────────────────────────────
        void ClosePicker()
        {
            if (activePicker == null) return;
            pickerCanvas.Children.Remove(activePicker);
            activePicker = null;
            if (activeAnchor != null)
            {
                if (GetSelectorArrow(activeAnchor) is TextBlock arw) arw.Text = "▾";
                activeAnchor = null;
            }
        }

        pickerCanvas.PointerPressed += (_, e) =>
        {
            if (activePicker == null || !ReferenceEquals(e.Source, pickerCanvas)) return;
            e.Handled = true;
            ClosePicker();
        };
        dashScroll.PointerPressed += (_, _) => ClosePicker();

        void ShowPicker(Button anchor, IList<string> items, int selected, Action<int> onPick)
        {
            if (ReferenceEquals(anchor, activeAnchor)) { ClosePicker(); return; }
            ClosePicker();
            activeAnchor = anchor;
            if (GetSelectorArrow(anchor) is TextBlock arw) arw.Text = "▴";

            string[] resolved = items.Count == 0 ? new[] { "None" } : items.ToArray();
            int safeIdx = Math.Clamp(selected, 0, Math.Max(0, resolved.Length - 1));

            var stack = new StackPanel { Spacing = 2 };
            for (int i = 0; i < resolved.Length; i++)
            {
                int captured = i;
                bool isSelected = i == safeIdx;
                var item = new Button
                {
                    Content = resolved[i],
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Background = isSelected ? ColTealSoft : ColBtnFill,
                    Foreground = isSelected ? ColTeal : ColText,
                    BorderBrush = ColBtnBord,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(8, 4),
                    FontSize = 11,
                    Height = 24
                };
                item.Click += (_, _) => { onPick(captured); ClosePicker(); };
                stack.Children.Add(item);
            }

            const double pickerWidth = 200;

            Point anchorPoint = anchor.TranslatePoint(new Point(0, anchor.Bounds.Height + 2), pickerCanvas) ?? new Point(8, 8);
            double left = Math.Clamp(anchorPoint.X, 4, Math.Max(4, root.Bounds.Width - pickerWidth - 4));
            double top  = anchorPoint.Y;
            double availableBelow = Math.Max(60, root.Bounds.Height - top - 4);
            double pickerHeight = Math.Min(resolved.Length * 26 + 12, availableBelow);

            var picker = new Border
            {
                Width = pickerWidth,
                MaxHeight = pickerHeight,
                Background = ColShellBg,
                BorderBrush = ColTeal,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4),
                Child = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = stack
                }
            };

            pickerCanvas.Children.Add(picker);
            Canvas.SetLeft(picker, left);
            Canvas.SetTop(picker, top);
            activePicker = picker;
        }

        profileSelector.Click += (_, _) =>
            ShowPicker(profileSelector, snap.Profiles, snap.SelectedProfileIdx, idx => RynthAiCommands.SelectProfile(3, idx));
        navSelector.Click += (_, _) =>
            ShowPicker(navSelector, snap.NavProfiles, snap.SelectedNavIdx, idx => RynthAiCommands.SelectProfile(0, idx));
        lootSelector.Click += (_, _) =>
            ShowPicker(lootSelector, snap.LootProfiles, snap.SelectedLootIdx, idx => RynthAiCommands.SelectProfile(1, idx));
        lootEditButton.Click += (_, _) =>
        {
            ClosePicker();
            string path = !string.IsNullOrEmpty(snap.VendorProfilePath) ? snap.VendorProfilePath : snap.CurrentLootPath;
            LaunchLootEditor(path);
        };
        metaSelector.Click += (_, _) =>
            ShowPicker(metaSelector, snap.MetaProfiles, snap.SelectedMetaIdx, idx => RynthAiCommands.SelectProfile(2, idx));

        // ── Polling timer: refresh from snapshot ────────────────────────────
        // 33 ms (~30 Hz) — vital changes feel instant to the eye. The
        // underlying snapshot is event-driven so it's already fresh; this
        // timer only bounds how often we rebuild the JSON snapshot and push
        // it into the panel labels. The overlay composites at ~60 Hz so the
        // panel stays ahead of the eye without saturating the dispatcher.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        timer.Tick += (_, _) =>
        {

            // TL;DR #7: signal the popout host (if floating) that a new
            // snapshot arrived this 33ms tick. Unconditional (not gated on
            // an exact content diff) — the point of this fix is bringing
            // popout render rate down to the panel's own ~30Hz data cadence
            // (was 60Hz via alwaysRender), not further gating below that.
            MarkDirty?.Invoke();

            // Footer: FPS (refreshed once/sec by EndSceneHook) + engine
            // gen uptime (re-stamped on every hot-reload). Engine-local, so it
            // updates even before the first hub view arrives.
            float liveFps = D3D9.EndSceneHook.MeasuredFps;
            SetText(fpsText, liveFps > 0 ? $"FPS {liveFps:F0}" : "FPS —");
            DateTime startedUtc = EntryPoint.InitStartedUtc;
            if (startedUtc != DateTime.MinValue)
            {
                TimeSpan up = DateTime.UtcNow - startedUtc;
                SetText(uptimeText, up.TotalHours >= 1
                    ? $"Up {(int)up.TotalHours}:{up.Minutes:D2}:{up.Seconds:D2}"
                    : $"Up {up.Minutes}:{up.Seconds:D2}");
            }
            SetText(minChip,  RynthAiDashboardState.Minimized ? "^" : "_");
            SetText(dockChip, IsFloatingNow?.Invoke() == true ? "↙" : "↗");

            var current = UiSources.RynthAi.Current;
            if (current == null) return;
            RynthAiView view = current.Value;
            snap = view.Raw;
            SetText(versionText, view.Version);

            // Live-testing finding 2026-09-02: apply the opacity the header
            // +/- chips set (see ColPanelBg's declaration above). Same 0.1
            // floor as the plugin-side clamp so the panel can never fade to
            // fully invisible/unclickable-looking.
            byte bgAlpha = view.PanelAlpha;
            if (ColPanelBg.Color.A != bgAlpha)
                ColPanelBg.Color = new Color(bgAlpha, ColPanelBg.Color.R, ColPanelBg.Color.G, ColPanelBg.Color.B);

            SetText(lockChip, snap.IsLocked ? "Unlk" : "Lock");

            // Mirrors LegacyDashboardRenderer's minimized layout:
            //   • title row (always visible — chips live there)
            //   • combat panel (toggles + target + vitals) — STAYS visible
            //   • inline ON/OFF macro button at top of toggles when minimized
            //   • header grid (macro state, dropdowns) — hidden when minimized
            //   • launcher grid (6 buttons) — hidden when minimized
            bool minimized = RynthAiDashboardState.Minimized;
            headerGrid.IsVisible          = !minimized;
            launcherGrid.IsVisible        = !minimized;
            footerGrid.IsVisible          = !minimized;
            minimizedMacroButton.IsVisible = minimized;
            // combatBorder stays visible in both modes — bug fix from prior
            // build that hid it and made the panel appear blank.
            combatBorder.IsVisible = true;
            // Live-testing finding 2026-09-02: this MinHeight (142) was fixed
            // regardless of minimized state, which put a floor under how far
            // the whole panel could shrink even in minimized mode (compounding
            // the wrapping-panel MinHeight fix in AvaloniaOverlay.cs). Lower
            // it while minimized — combatBorder's minimized content is just
            // the target line + segmented bar, not the full toggle grid.
            combatBorder.MinHeight = minimized ? 46 : 142;

            // Mirror the macro button state into the minimized variant so
            // both buttons reflect the same toggle.
            bool macroRunning = snap.MacroRunning;
            SetText(minimizedMacroButton, macroRunning ? "ON" : "OFF");
            minimizedMacroButton.Background = macroRunning ? ColMacroRun : ColMacroStop;

            // Macro button
            bool running = snap.MacroRunning;
            SetText(macroButton, running ? "RUNNING" : "STOPPED");
            macroButton.Background = running ? ColMacroRun : ColMacroStop;
            statusDot.Fill = running ? ColGreen : ColMute;
            statusRing.IsVisible = running;

            SetText(metaStateValue,    view.MetaStateText);
            SetText(botActivityValue,  view.BotActivityText);

            SetText(profileSelector, view.ProfileText);
            SetText(navSelector,     view.NavText);
            SetText(lootSelector,    view.LootText);
            SetText(metaSelector,    view.MetaText);

            UpdateToggle(combatToggle, snap.CombatEnabled);
            UpdateToggle(buffToggle,   snap.BuffingEnabled);
            UpdateToggle(navToggle,    snap.NavigationEnabled);
            UpdateToggle(lootToggle,   snap.LootingEnabled);
            UpdateMetaToggle(metaToggle, snap.MetaEnabled);

            // Target headline + segmented bar
            SetText(targetLabel, view.TargetHeadline);
            SetText(targetHp,    snap.TargetHealthDisplay);
            UpdateSegmentedBar(targetSegments, view.TargetSegmentsLit, ref targetSegLastLit);

            targetStRow.IsVisible = view.ShowTargetSubBars;
            targetMnRow.IsVisible = view.ShowTargetSubBars;
            if (view.ShowTargetSubBars)
            {
                UpdateBar(targetStFill, targetStLabel, view.TargetStamina);
                UpdateBar(targetMnFill, targetMnLabel, view.TargetMana);
            }

            // Sticky vitals are applied by the hub (StickyVitals).
            UpdateBar(hpFill, hpText, view.Health);
            UpdateBar(stFill, stText, view.Stamina);
            UpdateBar(mnFill, mnText, view.Mana);
        };
        // Not started here: AttachedToVisualTree starts it (and subscribes).
        RynthLog.Info("RynthAiPanel.Create: returning root");

        // BringPanelToFront in AvaloniaOverlay does Remove+Add on the
        // windowFrame whenever a drag or resize starts — that fires
        // Detached then Attached on every descendant. Without restarting
        // the timer on Attached, the first drag or resize permanently
        // killed snapshot polling: numbers froze and dropdown selections
        // appeared not to change (the click DID reach the plugin, the
        // panel just never re-polled to refresh the visible label).
        root.AttachedToVisualTree += (_, _) =>
        {
            UiSources.RynthAi.Subscribe();
            timer.Start();
        };
        root.DetachedFromVisualTree += (_, _) =>
        {
            UiSources.RynthAi.Unsubscribe();
            timer.Stop();
            ClosePicker();
        };
        return root;
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Layout helpers
    // ────────────────────────────────────────────────────────────────────────

    private static Button CreateChip(string label)
    {
        return new Button
        {
            Content = label,
            FontSize = 9,
            Width = 24,
            Height = 16,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = ColBtnFill,
            Foreground = ColMute,
            BorderBrush = ColBtnBord,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2)
        };
    }

    private static StackPanel BuildLabelValueRow(string label, out TextBlock value)
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = ColMute,
            FontSize = 10
        });
        value = new TextBlock
        {
            Text = "Default",
            Foreground = ColAmber,
            FontSize = 10
        };
        stack.Children.Add(value);
        return stack;
    }

    private static Grid BuildSelectorRow(string label, Button selector, Button? trailing = null)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(trailing == null ? "36,*" : "36,*,Auto"),
            Margin = new Thickness(0, 0, 0, 0)
        };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = ColMute,
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(selector);
        Grid.SetColumn(selector, 1);
        if (trailing != null)
        {
            row.Children.Add(trailing);
            Grid.SetColumn(trailing, 2);
        }
        return row;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ShellExecuteW")]
    private static extern IntPtr ShellExecuteW(IntPtr hwnd, string lpOperation, string lpFile,
        string? lpParameters, string? lpDirectory, int nShowCmd);

    /// <summary>
    /// Starts the standalone Loot Editor on <paramref name="profilePath"/> (empty = no file).
    /// The installer puts it in {app}\Tools\LootEditor next to Runtime\, so walk up from the
    /// engine module (Runtime\ or Runtime\.engine_loads\) to find it. ShellExecute, not
    /// Process.Start: the Process API is the documented AV hazard inside acclient.exe.
    /// </summary>
    internal static void LaunchLootEditor(string? profilePath)
    {
        try
        {
            string? exe = null;
            string? dir = EntryPoint.EngineDirectory;
            for (int i = 0; i < 4 && !string.IsNullOrEmpty(dir) && exe == null; i++, dir = System.IO.Path.GetDirectoryName(dir))
            {
                string candidate = System.IO.Path.Combine(dir, "Tools", "LootEditor", "RynthCore.LootEditor.exe");
                if (System.IO.File.Exists(candidate)) exe = candidate;
            }
            exe ??= @"C:\Games\RynthCore\Tools\LootEditor\RynthCore.LootEditor.exe";
            if (!System.IO.File.Exists(exe))
            {
                RynthLog.Info($"RynthAiPanel: Loot Editor not found (looked for {exe}).");
                return;
            }

            string? args = string.IsNullOrEmpty(profilePath) ? null : $"\"{profilePath}\"";
            IntPtr r = ShellExecuteW(IntPtr.Zero, "open", exe, args, System.IO.Path.GetDirectoryName(exe), 1 /* SW_SHOWNORMAL */);
            RynthLog.Info($"RynthAiPanel: Loot Editor {(r.ToInt64() > 32 ? "started" : $"failed to start (code {r.ToInt64()})")}: {exe} {args}");
        }
        catch (Exception ex)
        {
            RynthLog.Info($"RynthAiPanel: Loot Editor launch threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static Button CreateSelector(string text)
    {
        // ImGui dashboard pushes ImGuiCol.FrameBg = (0.18, 0.22, 0.28) → #2D3847
        // for combos. Match that — and put a ▾ glyph on the right edge so it
        // reads as a Windows-style dropdown instead of a plain button.
        var label = new TextBlock
        {
            Text = text,
            FontSize = 9,
            Foreground = ColText,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var arrow = new TextBlock
        {
            Text = "▾",
            FontSize = 10,
            Foreground = ColMute,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(4, 0, 0, 0)
        };
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            VerticalAlignment = VerticalAlignment.Center
        };
        grid.Children.Add(label);
        grid.Children.Add(arrow);
        Grid.SetColumn(arrow, 1);

        return new Button
        {
            Content = grid,
            Height = 16,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x38, 0x47)),
            Foreground = ColText,
            BorderBrush = ColBtnBord,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            FontSize = 9,
            Padding = new Thickness(5, 0),
            Tag = label  // for fast SetText updates without rebuilding the grid
        };
    }

    private static TextBlock? GetSelectorArrow(Button btn)
    {
        if (btn.Content is not Grid g) return null;
        foreach (Control child in g.Children)
            if (child is TextBlock tb && Grid.GetColumn(tb) == 1) return tb;
        return null;
    }

    private static Button CreateSquareToggle(string glyph)
    {
        return new Button
        {
            Content = glyph,
            Width = 24,
            Height = 24,
            FontSize = 12,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = ColBarBg,
            Foreground = ColMute,
            BorderBrush = ColBtnBord,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(0)
        };
    }

    private static Grid BuildSegmentedBar(out Rectangle[] segments)
    {
        const int segCount = 15;
        var grid = new Grid
        {
            Height = 10,
            ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", segCount))),
            Margin = new Thickness(0, 1, 0, 1)
        };
        segments = new Rectangle[segCount];
        for (int i = 0; i < segCount; i++)
        {
            var seg = new Rectangle
            {
                Fill = ColBarBg,
                RadiusX = 1,
                RadiusY = 1,
                Margin = new Thickness(i == 0 ? 0 : 1, 0, i == segCount - 1 ? 0 : 1, 0)
            };
            grid.Children.Add(seg);
            Grid.SetColumn(seg, i);
            segments[i] = seg;
        }
        return grid;
    }

    // UI deep-dive finding TL;DR #4 / P1-A (2026-07-02): "the one
    // unconditional invalidator" — this ran every ~33ms tick regardless of
    // whether the target's health % had actually changed, allocating a
    // FRESH SolidColorBrush per lit segment every single call (up to 15
    // allocations/tick) and re-setting every Rectangle.Fill even when
    // nothing changed. Each Fill assignment is a fresh reference even when
    // the color is unchanged, which invalidates the visual and keeps the
    // ENTIRE docked compositor re-rastering at 30Hz (3× ~5.5MB full-viewport
    // copies per frame, per finding TL;DR #5) even on an idle client.
    // Fixed two ways: (1) BarSegmentBrushes below is computed ONCE — segCount
    // is always 15 (BuildSegmentedBar's const), so every possible `t` value
    // is a fixed, known set; no more per-call allocation. (2) lastLitCount
    // gate — segment fill only changes at 15 discrete lit-count boundaries,
    // so skip the whole loop (and every Fill touch) unless the lit count
    // actually moved since the last call.
    private const int SegCount = 15;
    private static readonly IBrush[] BarSegmentBrushes = BuildSegmentBrushes();

    private static IBrush[] BuildSegmentBrushes()
    {
        var brushes = new IBrush[SegCount];
        for (int i = 0; i < SegCount; i++)
        {
            float t = (float)i / (SegCount - 1);
            brushes[i] = GradientBrush(t);
        }
        return brushes;
    }

    private static void UpdateSegmentedBar(Rectangle[] segments, int litCount, ref int lastLitCount)
    {
        int n = segments.Length;
        if (litCount == lastLitCount)
            return; // nothing crossed a segment boundary since the last call — skip entirely

        lastLitCount = litCount;
        for (int i = 0; i < n; i++)
            segments[i].Fill = i < litCount ? BarSegmentBrushes[i] : ColBarBg;
    }

    private static IBrush GradientBrush(float t)
    {
        if (t < 0.33f)
        {
            float f = t / 0.33f;
            return new SolidColorBrush(Color.FromRgb(255, (byte)(f * 255), 0));
        }
        if (t < 0.66f)
        {
            float f = (t - 0.33f) / 0.33f;
            return new SolidColorBrush(Color.FromRgb((byte)((1f - f) * 255), 255, 0));
        }
        float g = (t - 0.66f) / 0.34f;
        return new SolidColorBrush(Color.FromRgb(0, 255, (byte)(g * 255)));
    }

    private static Grid BuildCompactBar(out Rectangle fill, out TextBlock label, IBrush color)
    {
        var grid = new Grid { Height = 11, Margin = new Thickness(0, 1, 0, 1) };
        var bg = new Rectangle { Fill = ColBarBg, RadiusX = 2, RadiusY = 2 };
        fill = new Rectangle
        {
            Fill = color,
            RadiusX = 2,
            RadiusY = 2,
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 0
        };
        label = new TextBlock
        {
            Text = "ST 0/0",
            FontSize = 10,
            Foreground = ColText,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0)
        };
        grid.Children.Add(bg);
        grid.Children.Add(fill);
        grid.Children.Add(label);
        return grid;
    }

    /// <summary>Sizes a bar's fill to the view's fraction and sets its text.</summary>
    private static void UpdateBar(Rectangle fill, TextBlock text, VitalView bar)
    {
        if (fill.Parent is Grid g && g.Bounds.Width > 0)
            fill.Width = g.Bounds.Width * bar.Fraction;
        SetText(text, bar.Text);
    }

    private static Grid BuildVitalRow(string icon, IBrush color, out Rectangle fill, out TextBlock text)
    {
        var grid = new Grid { Height = 13, Margin = new Thickness(0, 1, 0, 0) };
        var bg = new Rectangle { Fill = ColBarBg, RadiusX = 6, RadiusY = 6 };
        fill = new Rectangle
        {
            Fill = color,
            RadiusX = 6,
            RadiusY = 6,
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 0
        };
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0)
        };
        content.Children.Add(new TextBlock
        {
            Text = icon,
            FontSize = 10,
            Foreground = ColMute,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0)
        });
        text = new TextBlock
        {
            Text = "HP: 0% (0/0)",
            FontSize = 10,
            Foreground = ColText,
            VerticalAlignment = VerticalAlignment.Center
        };
        content.Children.Add(text);
        grid.Children.Add(bg);
        grid.Children.Add(fill);
        grid.Children.Add(content);
        return grid;
    }

    /// <summary>
    /// Splits a single launcher cell into two half-width buttons sitting side
    /// by side. Used for Nav | Map so the user can pop the navigation panel
    /// or the dungeon map independently from the same row slot.
    /// </summary>
    /// <returns>The left and right half buttons (e.g. for tooltips).</returns>
    private static (Button Left, Button Right) AddSplitLauncher(Grid grid, int row, int col,
        string leftLabel, string leftIcon, string rightLabel, string rightIcon,
        Action? onLeftClick = null, Action? onRightClick = null)
    {
        var splitGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        var left  = AddLauncher(splitGrid, 0, 0, leftLabel,  leftIcon,  onLeftClick);
        var right = AddLauncher(splitGrid, 0, 1, rightLabel, rightIcon, onRightClick);
        grid.Children.Add(splitGrid);
        Grid.SetRow(splitGrid, row);
        Grid.SetColumn(splitGrid, col);
        return (left, right);
    }

    private static Button AddLauncher(Grid grid, int row, int col, string label, string icon, Action? onClick = null)
    {
        var btn = new Button
        {
            Height = 22,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = ColBtnFill,
            Foreground = ColMute,
            BorderBrush = ColBtnBord,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 0),
            Margin = new Thickness(1),
            FontSize = 9,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = icon, FontSize = 10, Foreground = ColMute, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = label, FontSize = 9, Foreground = ColMute, VerticalAlignment = VerticalAlignment.Center }
                }
            }
        };
        if (onClick != null) btn.Click += (_, _) => onClick();
        grid.Children.Add(btn);
        Grid.SetRow(btn, row);
        Grid.SetColumn(btn, col);
        return btn;
    }

    // Monsters opens the Damage panel (the basic Monsters panel and its
    // Simple/Advanced chip were retired 2026-10-01).
    private static void AddMonstersLauncher(Grid grid, int row, int col)
    {
        Button open = AddLauncher(grid, row, col, "Monsters", "◎", onClick: PanelRouter.ToggleMonsters);
        ToolTip.SetTip(open, "Monsters: weapons, spells and rules per monster (the Damage panel)");
    }

    private static void UpdateToggle(Button btn, bool on)
    {
        btn.Background = on ? ColTealSoft : ColBarBg;
        btn.BorderBrush = on ? ColTeal : ColBtnBord;
        btn.Foreground = on ? ColTeal : ColMute;
    }

    private static void UpdateMetaToggle(Button btn, bool on)
    {
        btn.Background = on ? ColTealSoft : ColBarBg;
        btn.BorderBrush = on ? ColTeal : ColBtnBord;
        btn.Foreground = on ? ColTeal : ColMute;
    }

    private static void SetText(TextBlock tb, string s)
    {
        if (!string.Equals(tb.Text, s, StringComparison.Ordinal)) tb.Text = s;
    }
    private static void SetText(Button btn, string s)
    {
        // Selector buttons stash their label TextBlock in Tag so we can update
        // text without rebuilding the inner ▾-arrow Grid.
        if (btn.Tag is TextBlock label)
        {
            SetText(label, s);
            return;
        }
        if (!Equals(btn.Content as string, s)) btn.Content = s;
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Patrol management flyout (right-click the Patrol launcher button)
    // ────────────────────────────────────────────────────────────────────────

    private static void ShowPatrolFlyout(Control anchor)
    {
        var root = new StackPanel { Orientation = Orientation.Vertical, Spacing = 4, Width = 244, Margin = new Thickness(8) };
        PopulatePatrolFlyout(root);
        // Patrol info comes from the hub (single-consumer export): subscribe
        // while the flyout is open and repopulate when it changes.
        long seen = UiSources.Patrol.Current?.Version ?? -1;
        var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        poll.Tick += (_, _) =>
        {
            var cur = UiSources.Patrol.Current;
            if (cur == null || cur.Version == seen) return;
            seen = cur.Version;
            PopulatePatrolFlyout(root);
        };
        var scroll = new ScrollViewer
        {
            Content = root,
            MaxHeight = 360,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        var flyout = new Flyout
        {
            Content = scroll,
            Placement = PlacementMode.Top,
        };
        flyout.Opened += (_, _) => { UiSources.Patrol.Subscribe(); UiSources.Patrol.RequestRefresh(); poll.Start(); };
        flyout.Closed += (_, _) => { poll.Stop(); UiSources.Patrol.Unsubscribe(); };
        flyout.ShowAt(anchor);
    }

    private static void PopulatePatrolFlyout(StackPanel root)
    {
        root.Children.Clear();
        PatrolInfo info = UiSources.Patrol.Current?.Value ?? new PatrolInfo();

        root.Children.Add(new TextBlock
        {
            Text = "PATROL & ROUTES", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = ColTeal,
        });

        // ── Current dungeon ───────────────────────────────────────────────
        root.Children.Add(SectionLabel("THIS DUNGEON"));
        if (info.InDungeon)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(new TextBlock
            {
                Text = $"0x{info.CurrentLandblock}  ·  {info.CurrentHazards} hazard cell(s)",
                FontSize = 10, Foreground = ColTextDim, VerticalAlignment = VerticalAlignment.Center,
            });
            if (info.CurrentHazards > 0)
            {
                var clear = MiniButton("Clear");
                clear.Click += (_, _) =>
                {
                    RynthAiCommands.SendNavCommand($"{{\"Cmd\":\"clearHazards\",\"NavName\":\"{info.CurrentLandblock}\"}}", refreshPatrol: true);
                };
                Grid.SetColumn(clear, 1);
                row.Children.Add(clear);
            }
            root.Children.Add(row);

            // Mark / unmark the cell the player is standing in — the reliable way to flag
            // environmental lava/acid the name detector can't see.
            var markRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 2, 0, 0) };
            var mark = MiniButton("Mark cell as hazard");
            mark.Click += (_, _) => RynthAiCommands.SendNavCommand("{\"Cmd\":\"markHazardHere\"}", refreshPatrol: true);
            var unmark = MiniButton("Unmark");
            unmark.Click += (_, _) => RynthAiCommands.SendNavCommand("{\"Cmd\":\"unmarkHazardHere\"}", refreshPatrol: true);
            markRow.Children.Add(mark);
            markRow.Children.Add(unmark);
            root.Children.Add(markRow);
            root.Children.Add(new TextBlock
            {
                Text = "Stand on the lava/acid, click Mark, then re-run Patrol.",
                FontSize = 9, Foreground = ColMute, TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            root.Children.Add(new TextBlock { Text = "Not in a dungeon.", FontSize = 10, Foreground = ColMute });
        }

        // ── Recorded hazard dungeons ──────────────────────────────────────
        root.Children.Add(SectionLabel($"RECORDED HAZARDS ({info.Dungeons.Length})"));
        if (info.Dungeons.Length == 0)
        {
            root.Children.Add(new TextBlock { Text = "None recorded yet.", FontSize = 10, Foreground = ColMute });
        }
        else
        {
            foreach (DungeonHazards d in info.Dungeons)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                row.Children.Add(new TextBlock
                {
                    Text = $"0x{d.Landblock}  ·  {d.Cells} cell(s)",
                    FontSize = 10, Foreground = ColTextDim, VerticalAlignment = VerticalAlignment.Center,
                });
                var clear = MiniButton("Clear");
                clear.Click += (_, _) =>
                {
                    RynthAiCommands.SendNavCommand($"{{\"Cmd\":\"clearHazards\",\"NavName\":\"{d.Landblock}\"}}", refreshPatrol: true);
                };
                Grid.SetColumn(clear, 1);
                row.Children.Add(clear);
                root.Children.Add(row);
            }

            var clearAll = MiniButton("Clear all recorded hazards");
            clearAll.HorizontalAlignment = HorizontalAlignment.Stretch;
            clearAll.Margin = new Thickness(0, 3, 0, 0);
            clearAll.Click += (_, _) =>
            {
                RynthAiCommands.SendNavCommand("{\"Cmd\":\"clearHazardsAll\"}", refreshPatrol: true);
            };
            root.Children.Add(clearAll);
        }

        // ── Saved nav routes ──────────────────────────────────────────────
        root.Children.Add(SectionLabel($"SAVED ROUTES ({info.Routes.Length})"));
        if (info.Routes.Length == 0)
        {
            root.Children.Add(new TextBlock { Text = "No .nav routes saved.", FontSize = 10, Foreground = ColMute });
        }
        else
        {
            foreach (string name in info.Routes)
                root.Children.Add(new TextBlock
                {
                    Text = "• " + name, FontSize = 10, Foreground = ColTextDim, TextWrapping = TextWrapping.NoWrap,
                });
        }
    }

    private static TextBlock SectionLabel(string text) => new TextBlock
    {
        Text = text, FontSize = 9, FontWeight = FontWeight.SemiBold, Foreground = ColMute,
        Margin = new Thickness(0, 6, 0, 1),
    };

    private static Button MiniButton(string label) => new Button
    {
        Content = label, FontSize = 9, Height = 16, Padding = new Thickness(6, 0),
        Background = ColBtnFill, Foreground = ColAmber, BorderBrush = ColBtnBord,
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2),
        VerticalContentAlignment = VerticalAlignment.Center,
        HorizontalContentAlignment = HorizontalAlignment.Center,
    };

}
