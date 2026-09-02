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
//  Live data comes from the RynthAi plugin via these C exports:
//    RynthPluginGetSnapshotJson         → JSON blob (polled every 33 ms)
//    RynthPluginToggleMacro             → flip IsMacroRunning
//    RynthPluginSetSubsystemEnabled(id) → 0=Combat 1=Buff 2=Nav 3=Loot 4=Meta
//    RynthPluginSelectProfile(kind, i)  → 0=nav 1=loot 2=meta 3=profile
//    RynthPluginForceRebuff             → buff manager force rebuff
//    RynthPluginCancelForceRebuff       → cancel pending FR
//    RynthPluginAdjustOpacity(delta)    → header +/- chips (mirrored)
//    RynthPluginTogglePanelLock         → Lock chip (mirrored)
//    RynthPluginTogglePanelMinimize     → _ chip (mirrored)
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

namespace RynthCore.Engine.UI.Panels;

internal static partial class RynthAiPanel
{
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetSnapshotJsonFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VoidFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void IntFn(int arg);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void IntIntFn(int a, int b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FloatFn(float arg);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void PtrFn(IntPtr arg);

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
    private static readonly IBrush ColPanelBg   = new SolidColorBrush(Color.FromRgb(0x0A, 0x12, 0x1A));
    private static readonly IBrush ColBarBg     = new SolidColorBrush(Color.FromRgb(0x14, 0x1F, 0x29));
    private static readonly IBrush ColBtnFill   = new SolidColorBrush(Color.FromRgb(0x0F, 0x1F, 0x2E));
    private static readonly IBrush ColBtnBord   = new SolidColorBrush(Color.FromRgb(0x26, 0x40, 0x59));
    // Macro button: (0.10,0.35,0.15) running, (0.25,0.12,0.12) stopped.
    private static readonly IBrush ColMacroRun  = new SolidColorBrush(Color.FromRgb(0x1A, 0x59, 0x26));
    private static readonly IBrush ColMacroStop = new SolidColorBrush(Color.FromRgb(0x40, 0x1F, 0x1F));
    // FR button: (0.28,0.20,0.04).
    private static readonly IBrush ColFrFill    = new SolidColorBrush(Color.FromRgb(0x47, 0x33, 0x0A));

    private static GetSnapshotJsonFn? _getSnapshotJson;
    private static VoidFn? _toggleMacro;
    private static IntIntFn? _setSubsystemEnabled;
    private static IntIntFn? _selectProfile;
    private static VoidFn? _forceRebuff;
    private static VoidFn? _cancelForceRebuff;
    private static FloatFn? _adjustOpacity;
    private static VoidFn? _togglePanelLock;
    private static VoidFn? _togglePanelMinimize;
    private static PtrFn? _sendNavCommand;
    private static GetSnapshotJsonFn? _getPatrolInfoJson;
    private static bool _bindingLogged;

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

    // Last-known good vitals — persists across panel recreates AND across
    // engine hot-reloads (saved to disk). After a reload the plugin's vital
    // cache is cold for a tick or two; without this the panel shows 0/0,
    // which the user sees as the bars wiping. Updated only when the snapshot
    // gives us a real (non-zero max) reading.
    private static uint _cachedHp, _cachedMaxHp;
    private static uint _cachedSt, _cachedMaxSt;
    private static uint _cachedMn, _cachedMaxMn;
    private static bool _vitalCacheLoaded;
    private static long _vitalCacheLastSavedTick;

    // Avalonia panel's OWN minimize state, persisted independently of the
    // ImGui dashboard. Sharing snap.IsMinimized via the plugin caused the
    // ImGui "_" chip to also collapse the Avalonia panel (and vice versa) —
    // which the user noticed because the Avalonia minimized look hadn't
    // been built out yet, so the panel just blanked.
    private static bool _avaloniaMinimized;
    private static bool _avaloniaMinimizedLoaded;

    private static string VitalCachePath =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RynthCore", "vitals.cache");

    private static string AvaloniaMinimizedPath =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RynthCore", "rynthai_avalonia.cache");

    private static void LoadAvaloniaMinimized()
    {
        if (_avaloniaMinimizedLoaded) return;
        _avaloniaMinimizedLoaded = true;
        try
        {
            string path = AvaloniaMinimizedPath;
            if (!System.IO.File.Exists(path)) return;
            _avaloniaMinimized = string.Equals(
                System.IO.File.ReadAllText(path).Trim(),
                "minimized",
                StringComparison.OrdinalIgnoreCase);
        }
        catch { /* corrupt cache — start fresh */ }
    }

    private static void SaveAvaloniaMinimized()
    {
        try
        {
            string path = AvaloniaMinimizedPath;
            string dir = System.IO.Path.GetDirectoryName(path)!;
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(path, _avaloniaMinimized ? "minimized" : "expanded");
        }
        catch { /* best-effort */ }
    }

    private static void LoadVitalCache()
    {
        if (_vitalCacheLoaded) return;
        _vitalCacheLoaded = true;
        try
        {
            string path = VitalCachePath;
            if (!System.IO.File.Exists(path)) return;
            string[] parts = System.IO.File.ReadAllText(path).Split(',');
            if (parts.Length < 6) return;
            _cachedHp     = uint.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
            _cachedMaxHp  = uint.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            _cachedSt     = uint.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
            _cachedMaxSt  = uint.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture);
            _cachedMn     = uint.Parse(parts[4], System.Globalization.CultureInfo.InvariantCulture);
            _cachedMaxMn  = uint.Parse(parts[5], System.Globalization.CultureInfo.InvariantCulture);
        }
        catch { /* corrupt cache — start fresh */ }
    }

    private static void SaveVitalCache()
    {
        // Throttle disk writes to once per second so polling 4×/s doesn't
        // beat up AppData. We only save when at least one max changed anyway.
        long now = Environment.TickCount64;
        if (now - _vitalCacheLastSavedTick < 1000) return;
        _vitalCacheLastSavedTick = now;
        try
        {
            string path = VitalCachePath;
            string dir = System.IO.Path.GetDirectoryName(path)!;
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(path,
                $"{_cachedHp},{_cachedMaxHp},{_cachedSt},{_cachedMaxSt},{_cachedMn},{_cachedMaxMn}");
        }
        catch { /* best-effort, never throw out of a UI tick */ }
    }

    internal static Control Create()
    {
        RynthLog.Info("RynthAiPanel.Create: entry");
        TryBind();
        LoadVitalCache();
        LoadAvaloniaMinimized();
        RynthLog.Info("RynthAiPanel.Create: TryBind ok");

        Snapshot snap = new();
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
        lockChip.Click  += (_, _) => { ClosePicker(); _togglePanelLock?.Invoke(); };
        minusChip.Click += (_, _) => { ClosePicker(); _adjustOpacity?.Invoke(-0.1f); };
        plusChip.Click  += (_, _) => { ClosePicker(); _adjustOpacity?.Invoke(0.1f); };
        minChip.Click   += (_, _) =>
        {
            ClosePicker();
            _avaloniaMinimized = !_avaloniaMinimized;
            SaveAvaloniaMinimized();
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
        macroButton.Click += (_, _) => { ClosePicker(); _toggleMacro?.Invoke(); };
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
        rightStack.Children.Add(BuildSelectorRow("Loot:",    lootSelector));
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
        minimizedMacroButton.Click += (_, _) => { ClosePicker(); _toggleMacro?.Invoke(); };
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
        frButton.Click += (_, _) => { ClosePicker(); _forceRebuff?.Invoke(); };
        // Right-click cancel
        frButton.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(frButton).Properties.IsRightButtonPressed)
            {
                e.Handled = true;
                _cancelForceRebuff?.Invoke();
            }
        };
        togglesPanel.Children.Add(frButton);

        combatGrid.Children.Add(togglesPanel);

        combatToggle.Click += (_, _) => { ClosePicker(); _setSubsystemEnabled?.Invoke(0, snap.CombatEnabled ? 0 : 1); };
        buffToggle.Click   += (_, _) => { ClosePicker(); _setSubsystemEnabled?.Invoke(1, snap.BuffingEnabled ? 0 : 1); };
        navToggle.Click    += (_, _) => { ClosePicker(); _setSubsystemEnabled?.Invoke(2, snap.NavigationEnabled ? 0 : 1); };
        lootToggle.Click   += (_, _) => { ClosePicker(); _setSubsystemEnabled?.Invoke(3, snap.LootingEnabled ? 0 : 1); };
        metaToggle.Click   += (_, _) => { ClosePicker(); _setSubsystemEnabled?.Invoke(4, snap.MetaEnabled ? 0 : 1); };

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
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            Margin = new Thickness(0, 4, 0, 0)
        };
        AddSplitLauncher(launcherGrid, 0, 0, "Meta", "⚙", "Lua", "<>",
            onLeftClick: () => AvaloniaOverlay.ActivateBarButton("Meta"));
        AddLauncher(launcherGrid, 0, 1, "Monsters",    "◎",
            onClick: () => AvaloniaOverlay.ActivateBarButton("Monsters"));
        AddLauncher(launcherGrid, 0, 2, "Settings",    "⚒",
            onClick: () => AvaloniaOverlay.ActivateBarButton("Settings"));
        AddSplitLauncher(launcherGrid, 1, 0, "Nav", "➤", "Map", "🗺",
            onLeftClick: () => AvaloniaOverlay.ActivateBarButton("Nav"));
        AddLauncher(launcherGrid, 1, 1, "Items",       "🛡",
            onClick: () => AvaloniaOverlay.ActivateBarButton("Items"));
        var patrolBtn = AddLauncher(launcherGrid, 1, 2, "Patrol", "⬡",
            onClick: () =>
            {
                if (_sendNavCommand == null) TryBind();
                SendNavCmd("{\"Cmd\":\"dunPatrol\"}");
            });
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
            ShowPicker(profileSelector, snap.Profiles, snap.SelectedProfileIdx, idx => _selectProfile?.Invoke(3, idx));
        navSelector.Click += (_, _) =>
            ShowPicker(navSelector, snap.NavProfiles, snap.SelectedNavIdx, idx => _selectProfile?.Invoke(0, idx));
        lootSelector.Click += (_, _) =>
            ShowPicker(lootSelector, snap.LootProfiles, snap.SelectedLootIdx, idx => _selectProfile?.Invoke(1, idx));
        metaSelector.Click += (_, _) =>
            ShowPicker(metaSelector, snap.MetaProfiles, snap.SelectedMetaIdx, idx => _selectProfile?.Invoke(2, idx));

        // ── Polling timer: refresh from snapshot ────────────────────────────
        // 33 ms (~30 Hz) — vital changes feel instant to the eye. The
        // underlying snapshot is event-driven so it's already fresh; this
        // timer only bounds how often we rebuild the JSON snapshot and push
        // it into the panel labels. The overlay composites at ~60 Hz so the
        // panel stays ahead of the eye without saturating the dispatcher.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        timer.Tick += (_, _) =>
        {
            if (_getSnapshotJson == null) TryBind();

            // TL;DR #7: signal the popout host (if floating) that a new
            // snapshot arrived this 33ms tick. Unconditional (not gated on
            // an exact content diff) — the point of this fix is bringing
            // popout render rate down to the panel's own ~30Hz data cadence
            // (was 60Hz via alwaysRender), not further gating below that.
            MarkDirty?.Invoke();

            snap = ReadSnapshot();

            SetText(lockChip, snap.IsLocked ? "Unlk" : "Lock");
            SetText(minChip,  _avaloniaMinimized ? "^" : "_");
            SetText(dockChip, IsFloatingNow?.Invoke() == true ? "↙" : "↗");

            // Mirrors LegacyDashboardRenderer's minimized layout:
            //   • title row (always visible — chips live there)
            //   • combat panel (toggles + target + vitals) — STAYS visible
            //   • inline ON/OFF macro button at top of toggles when minimized
            //   • header grid (macro state, dropdowns) — hidden when minimized
            //   • launcher grid (6 buttons) — hidden when minimized
            bool minimized = _avaloniaMinimized;
            headerGrid.IsVisible          = !minimized;
            launcherGrid.IsVisible        = !minimized;
            footerGrid.IsVisible          = !minimized;
            minimizedMacroButton.IsVisible = minimized;
            // combatBorder stays visible in both modes — bug fix from prior
            // build that hid it and made the panel appear blank.
            combatBorder.IsVisible = true;

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

            SetText(metaStateValue,    string.IsNullOrWhiteSpace(snap.CurrentState) ? "Default" : snap.CurrentState);
            SetText(botActivityValue,  string.IsNullOrWhiteSpace(snap.BotAction) || snap.BotAction == "Default" ? "Idle" : snap.BotAction);

            SetText(profileSelector, Truncate(snap.SelectedProfile, 16));
            SetText(navSelector,     Truncate(snap.CurrentNavName, 16));
            SetText(lootSelector,    Truncate(snap.CurrentLootName, 16));
            SetText(metaSelector,    Truncate(snap.CurrentMetaName, 16));

            UpdateToggle(combatToggle, snap.CombatEnabled);
            UpdateToggle(buffToggle,   snap.BuffingEnabled);
            UpdateToggle(navToggle,    snap.NavigationEnabled);
            UpdateToggle(lootToggle,   snap.LootingEnabled);
            UpdateMetaToggle(metaToggle, snap.MetaEnabled);

            // Target headline + segmented bar
            string label = string.IsNullOrWhiteSpace(snap.TargetLabel) ? "NO TARGET" : snap.TargetLabel.ToUpperInvariant();
            SetText(targetLabel, Truncate(label, 32));
            SetText(targetHp,    snap.TargetHealthDisplay);
            UpdateSegmentedBar(targetSegments, snap.TargetHealthPercent, ref targetSegLastLit);

            bool showTargetSubBars = snap.ShowTargetStaminaMana && snap.TargetMaxStamina > 0;
            targetStRow.IsVisible = showTargetSubBars;
            targetMnRow.IsVisible = showTargetSubBars;
            if (showTargetSubBars)
            {
                UpdateCompactBar(targetStFill, targetStLabel, "ST", snap.TargetStamina, snap.TargetMaxStamina);
                UpdateCompactBar(targetMnFill, targetMnLabel, "MN", snap.TargetMana,    snap.TargetMaxMana);
            }

            // Sticky vitals: keep showing last-known values when the plugin
            // briefly returns 0 after a hot reload (host cache is cold for a
            // tick or two). Update + persist the cache only when we get a
            // real reading.
            bool cacheChanged = false;
            if (snap.PlayerMaxHealth  != 0) { _cachedHp = snap.PlayerHealth;   _cachedMaxHp = snap.PlayerMaxHealth;   cacheChanged = true; }
            if (snap.PlayerMaxStamina != 0) { _cachedSt = snap.PlayerStamina;  _cachedMaxSt = snap.PlayerMaxStamina;  cacheChanged = true; }
            if (snap.PlayerMaxMana    != 0) { _cachedMn = snap.PlayerMana;     _cachedMaxMn = snap.PlayerMaxMana;     cacheChanged = true; }
            if (cacheChanged) SaveVitalCache();

            UpdateVitalRow(hpFill, hpText, "HP",
                snap.PlayerMaxHealth  != 0 ? snap.PlayerHealth  : _cachedHp,
                snap.PlayerMaxHealth  != 0 ? snap.PlayerMaxHealth  : _cachedMaxHp);
            UpdateVitalRow(stFill, stText, "ST",
                snap.PlayerMaxStamina != 0 ? snap.PlayerStamina : _cachedSt,
                snap.PlayerMaxStamina != 0 ? snap.PlayerMaxStamina : _cachedMaxSt);
            UpdateVitalRow(mnFill, mnText, "MN",
                snap.PlayerMaxMana    != 0 ? snap.PlayerMana    : _cachedMn,
                snap.PlayerMaxMana    != 0 ? snap.PlayerMaxMana    : _cachedMaxMn);

            // Footer: FPS (refreshed once/sec by EndSceneHook) + engine
            // gen uptime (re-stamped on every hot-reload). Cheap to recompute
            // every 100 ms — string interpolation only, no native calls.
            float liveFps = D3D9.EndSceneHook.MeasuredFps;
            SetText(fpsText, liveFps > 0 ? $"FPS {liveFps:F0}" : "FPS —");

            DateTime startedUtc = EntryPoint.InitStartedUtc;
            if (startedUtc != DateTime.MinValue)
            {
                TimeSpan up = DateTime.UtcNow - startedUtc;
                string upStr = up.TotalHours >= 1
                    ? $"Up {(int)up.TotalHours}:{up.Minutes:D2}:{up.Seconds:D2}"
                    : $"Up {up.Minutes}:{up.Seconds:D2}";
                SetText(uptimeText, upStr);
            }
        };
        timer.Start();
        RynthLog.Info("RynthAiPanel.Create: timer started, returning root");

        // BringPanelToFront in AvaloniaOverlay does Remove+Add on the
        // windowFrame whenever a drag or resize starts — that fires
        // Detached then Attached on every descendant. Without restarting
        // the timer on Attached, the first drag or resize permanently
        // killed snapshot polling: numbers froze and dropdown selections
        // appeared not to change (the click DID reach the plugin, the
        // panel just never re-polled to refresh the visible label).
        root.AttachedToVisualTree += (_, _) => timer.Start();
        root.DetachedFromVisualTree += (_, _) => { timer.Stop(); ClosePicker(); };
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

    private static Grid BuildSelectorRow(string label, Button selector)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("36,*"),
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
        return row;
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

    private static void UpdateSegmentedBar(Rectangle[] segments, float pct, ref int lastLitCount)
    {
        float clamped = Math.Clamp(pct, 0f, 1f);
        int n = segments.Length;
        int litCount = 0;
        for (int i = 0; i < n; i++)
            if ((float)i / n <= clamped) litCount++;

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

    private static void UpdateCompactBar(Rectangle fill, TextBlock label, string prefix, uint v, uint maxV)
    {
        double pct = maxV == 0 ? 0 : Math.Clamp((double)v / maxV, 0, 1);
        // Width set via parent bounds — bind via width binding using grid actual width
        if (fill.Parent is Grid g && g.Bounds.Width > 0)
            fill.Width = g.Bounds.Width * pct;
        SetText(label, $"{prefix} {v}/{maxV}");
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

    private static void UpdateVitalRow(Rectangle fill, TextBlock text, string label, uint v, uint maxV)
    {
        double pct = maxV == 0 ? 0 : Math.Clamp((double)v / maxV, 0, 1);
        if (fill.Parent is Grid g && g.Bounds.Width > 0)
            fill.Width = g.Bounds.Width * pct;
        string display = maxV == 0 ? (v == 0 ? "--/--" : $"{v}/--") : $"{v}/{maxV}";
        SetText(text, $"{label}: {(int)(pct * 100)}% ({display})");
    }

    /// <summary>
    /// Splits a single launcher cell into two half-width buttons sitting side
    /// by side. Used for Nav | Map so the user can pop the navigation panel
    /// or the dungeon map independently from the same row slot.
    /// </summary>
    private static void AddSplitLauncher(Grid grid, int row, int col,
        string leftLabel, string leftIcon, string rightLabel, string rightIcon,
        Action? onLeftClick = null, Action? onRightClick = null)
    {
        var splitGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        AddLauncher(splitGrid, 0, 0, leftLabel,  leftIcon,  onLeftClick);
        AddLauncher(splitGrid, 0, 1, rightLabel, rightIcon, onRightClick);
        grid.Children.Add(splitGrid);
        Grid.SetRow(splitGrid, row);
        Grid.SetColumn(splitGrid, col);
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

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return "None";
        return value.Length > max ? value[..(max - 1)] + "…" : value;
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
    //  Plugin export binding + snapshot ingest
    // ────────────────────────────────────────────────────────────────────────

    private static int _snapshotDiagsLogged;
    private static string _lastLoggedTargetLabel = string.Empty;
    private static uint _lastLoggedPlayerHealth;
    private static uint _lastLoggedPlayerMaxHealth;

    private static Snapshot ReadSnapshot()
    {
        if (_getSnapshotJson == null) return new Snapshot();

        try
        {
            IntPtr ptr = _getSnapshotJson();
            if (ptr == IntPtr.Zero) return new Snapshot();
            string json = Marshal.PtrToStringAnsi(ptr) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(json)) return new Snapshot();
            Snapshot snap = JsonSerializer.Deserialize(json, SnapshotJsonContext.Default.Snapshot) ?? new Snapshot();

            // Log first 3 snapshots for pipeline-up diagnostic. Then log on
            // any change to a vital/target field so we can see when data
            // starts flowing post-login without flooding.
            if (_snapshotDiagsLogged < 3 ||
                snap.TargetLabel != _lastLoggedTargetLabel ||
                snap.PlayerHealth != _lastLoggedPlayerHealth ||
                snap.PlayerMaxHealth != _lastLoggedPlayerMaxHealth)
            {
                _snapshotDiagsLogged++;
                _lastLoggedTargetLabel = snap.TargetLabel;
                _lastLoggedPlayerHealth = snap.PlayerHealth;
                _lastLoggedPlayerMaxHealth = snap.PlayerMaxHealth;
                RynthLog.Info(
                    $"RynthAiPanel.ReadSnapshot[{_snapshotDiagsLogged}]: " +
                    $"player HP={snap.PlayerHealth}/{snap.PlayerMaxHealth} " +
                    $"ST={snap.PlayerStamina}/{snap.PlayerMaxStamina} " +
                    $"MN={snap.PlayerMana}/{snap.PlayerMaxMana} " +
                    $"target='{snap.TargetLabel}' hp={snap.TargetHealth}/{snap.TargetMaxHealth} " +
                    $"jsonLen={json.Length}");
            }

            return snap;
        }
        catch (Exception ex)
        {
            RynthLog.Info($"RynthAiPanel.ReadSnapshot: deserialize threw {ex.GetType().Name}: {ex.Message}");
            return new Snapshot();
        }
    }

    private static void TryBind()
    {
        LoadedPlugin? plugin = PluginManager.Plugins.FirstOrDefault(
            p => p.DisplayName.Contains("RynthAi", StringComparison.OrdinalIgnoreCase));
        if (plugin == null || plugin.ModuleHandle == IntPtr.Zero) return;

        _getSnapshotJson      ??= Bind<GetSnapshotJsonFn>(plugin, "RynthPluginGetSnapshotJson");
        _toggleMacro          ??= Bind<VoidFn>(plugin,             "RynthPluginToggleMacro");
        _setSubsystemEnabled  ??= Bind<IntIntFn>(plugin,           "RynthPluginSetSubsystemEnabled");
        _selectProfile        ??= Bind<IntIntFn>(plugin,           "RynthPluginSelectProfile");
        _forceRebuff          ??= Bind<VoidFn>(plugin,             "RynthPluginForceRebuff");
        _cancelForceRebuff    ??= Bind<VoidFn>(plugin,             "RynthPluginCancelForceRebuff");
        _adjustOpacity        ??= Bind<FloatFn>(plugin,            "RynthPluginAdjustOpacity");
        _togglePanelLock      ??= Bind<VoidFn>(plugin,             "RynthPluginTogglePanelLock");
        _togglePanelMinimize  ??= Bind<VoidFn>(plugin,             "RynthPluginTogglePanelMinimize");
        _sendNavCommand       ??= Bind<PtrFn>(plugin,              "RynthPluginSendNavCommand");
        _getPatrolInfoJson    ??= Bind<GetSnapshotJsonFn>(plugin,  "RynthPluginGetPatrolInfoJson");

        if (!_bindingLogged && _getSnapshotJson != null)
        {
            _bindingLogged = true;
            RynthLog.UI("RynthAiPanel: bound RynthAi plugin exports for live dashboard data.");
        }
    }

    private static T? Bind<T>(LoadedPlugin plugin, string exportName) where T : Delegate
    {
        IntPtr addr = GetProcAddress(plugin.ModuleHandle, exportName);
        return addr == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(addr);
    }

    private static void SendNavCmd(string json)
    {
        if (_sendNavCommand == null) return;
        IntPtr ptr = Marshal.StringToHGlobalAnsi(json);
        try { _sendNavCommand(ptr); }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    private sealed class Snapshot
    {
        [JsonPropertyName("macroRunning")]    public bool MacroRunning { get; set; }
        [JsonPropertyName("currentState")]    public string CurrentState { get; set; } = string.Empty;
        [JsonPropertyName("botAction")]       public string BotAction { get; set; } = string.Empty;
        [JsonPropertyName("selectedProfile")] public string SelectedProfile { get; set; } = "Default";
        [JsonPropertyName("profiles")]        public string[] Profiles { get; set; } = Array.Empty<string>();
        [JsonPropertyName("navProfiles")]     public string[] NavProfiles { get; set; } = Array.Empty<string>();
        [JsonPropertyName("lootProfiles")]    public string[] LootProfiles { get; set; } = Array.Empty<string>();
        [JsonPropertyName("metaProfiles")]    public string[] MetaProfiles { get; set; } = Array.Empty<string>();
        [JsonPropertyName("currentNavName")]  public string CurrentNavName { get; set; } = string.Empty;
        [JsonPropertyName("currentLootName")] public string CurrentLootName { get; set; } = string.Empty;
        [JsonPropertyName("currentMetaName")] public string CurrentMetaName { get; set; } = string.Empty;
        [JsonPropertyName("selectedNavIdx")]  public int SelectedNavIdx { get; set; }
        [JsonPropertyName("selectedLootIdx")] public int SelectedLootIdx { get; set; }
        [JsonPropertyName("selectedMetaIdx")] public int SelectedMetaIdx { get; set; }
        [JsonPropertyName("selectedProfileIdx")] public int SelectedProfileIdx { get; set; }
        [JsonPropertyName("combatEnabled")]     public bool CombatEnabled { get; set; }
        [JsonPropertyName("buffingEnabled")]    public bool BuffingEnabled { get; set; }
        [JsonPropertyName("navigationEnabled")] public bool NavigationEnabled { get; set; }
        [JsonPropertyName("lootingEnabled")]    public bool LootingEnabled { get; set; }
        [JsonPropertyName("metaEnabled")]       public bool MetaEnabled { get; set; }
        [JsonPropertyName("currentTargetId")]   public uint CurrentTargetId { get; set; }
        [JsonPropertyName("targetLabel")]       public string TargetLabel { get; set; } = "NO TARGET";
        [JsonPropertyName("targetHealthPercent")] public float TargetHealthPercent { get; set; }
        [JsonPropertyName("targetHealthDisplay")] public string TargetHealthDisplay { get; set; } = "0";
        [JsonPropertyName("targetHealth")]    public uint TargetHealth { get; set; }
        [JsonPropertyName("targetMaxHealth")] public uint TargetMaxHealth { get; set; }
        [JsonPropertyName("targetStamina")]   public uint TargetStamina { get; set; }
        [JsonPropertyName("targetMaxStamina")] public uint TargetMaxStamina { get; set; }
        [JsonPropertyName("targetMana")]      public uint TargetMana { get; set; }
        [JsonPropertyName("targetMaxMana")]   public uint TargetMaxMana { get; set; }
        [JsonPropertyName("playerHealth")]    public uint PlayerHealth { get; set; }
        [JsonPropertyName("playerMaxHealth")] public uint PlayerMaxHealth { get; set; }
        [JsonPropertyName("playerStamina")]   public uint PlayerStamina { get; set; }
        [JsonPropertyName("playerMaxStamina")] public uint PlayerMaxStamina { get; set; }
        [JsonPropertyName("playerMana")]      public uint PlayerMana { get; set; }
        [JsonPropertyName("playerMaxMana")]   public uint PlayerMaxMana { get; set; }
        [JsonPropertyName("showTargetStaminaMana")] public bool ShowTargetStaminaMana { get; set; }
        [JsonPropertyName("isLocked")]    public bool IsLocked { get; set; }
        [JsonPropertyName("isMinimized")] public bool IsMinimized { get; set; }
        [JsonPropertyName("bgOpacity")]   public float BgOpacity { get; set; } = 0.95f;
    }

    [JsonSerializable(typeof(Snapshot))]
    private sealed partial class SnapshotJsonContext : JsonSerializerContext { }

    // ────────────────────────────────────────────────────────────────────────
    //  Patrol management flyout (right-click the Patrol launcher button)
    // ────────────────────────────────────────────────────────────────────────

    private static void ShowPatrolFlyout(Control anchor)
    {
        var root = new StackPanel { Orientation = Orientation.Vertical, Spacing = 4, Width = 244, Margin = new Thickness(8) };
        PopulatePatrolFlyout(root);
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
        flyout.ShowAt(anchor);
    }

    private static void PopulatePatrolFlyout(StackPanel root)
    {
        root.Children.Clear();
        PatrolInfo info = FetchPatrolInfo();

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
                    SendNavCmd($"{{\"Cmd\":\"clearHazards\",\"NavName\":\"{info.CurrentLandblock}\"}}");
                    PopulatePatrolFlyout(root);
                };
                Grid.SetColumn(clear, 1);
                row.Children.Add(clear);
            }
            root.Children.Add(row);

            // Mark / unmark the cell the player is standing in — the reliable way to flag
            // environmental lava/acid the name detector can't see.
            var markRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 2, 0, 0) };
            var mark = MiniButton("Mark cell as hazard");
            mark.Click += (_, _) => { SendNavCmd("{\"Cmd\":\"markHazardHere\"}"); PopulatePatrolFlyout(root); };
            var unmark = MiniButton("Unmark");
            unmark.Click += (_, _) => { SendNavCmd("{\"Cmd\":\"unmarkHazardHere\"}"); PopulatePatrolFlyout(root); };
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
            foreach (DungeonHaz d in info.Dungeons)
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
                    SendNavCmd($"{{\"Cmd\":\"clearHazards\",\"NavName\":\"{d.Landblock}\"}}");
                    PopulatePatrolFlyout(root);
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
                SendNavCmd("{\"Cmd\":\"clearHazardsAll\"}");
                PopulatePatrolFlyout(root);
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

    private static PatrolInfo FetchPatrolInfo()
    {
        if (_getPatrolInfoJson == null) TryBind();
        if (_getPatrolInfoJson == null) return new PatrolInfo();
        try
        {
            IntPtr p = _getPatrolInfoJson();
            if (p == IntPtr.Zero) return new PatrolInfo();
            string json = Marshal.PtrToStringAnsi(p) ?? "{}";
            return JsonSerializer.Deserialize(json, PatrolJsonContext.Default.PatrolInfo) ?? new PatrolInfo();
        }
        catch { return new PatrolInfo(); }
    }

    private sealed class PatrolInfo
    {
        [JsonPropertyName("inDungeon")]        public bool InDungeon { get; set; }
        [JsonPropertyName("currentLandblock")] public string CurrentLandblock { get; set; } = "0000";
        [JsonPropertyName("currentHazards")]   public int CurrentHazards { get; set; }
        [JsonPropertyName("liveHazards")]      public int LiveHazards { get; set; }
        [JsonPropertyName("dungeons")]         public DungeonHaz[] Dungeons { get; set; } = Array.Empty<DungeonHaz>();
        [JsonPropertyName("routes")]           public string[] Routes { get; set; } = Array.Empty<string>();
    }

    private sealed class DungeonHaz
    {
        [JsonPropertyName("landblock")] public string Landblock { get; set; } = "0000";
        [JsonPropertyName("cells")]     public int Cells { get; set; }
    }

    [JsonSerializable(typeof(PatrolInfo))]
    private sealed partial class PatrolJsonContext : JsonSerializerContext { }
}
