using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
    private delegate void ToggleMacroFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetSubsystemEnabledFn(int subsystemId, int enabled);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SelectProfileFn(int kind, int index);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AdjustNavigationSettingFn(int settingId, float delta);

    private static readonly IBrush ColTeal = new SolidColorBrush(Color.Parse("#26C1A6"));
    private static readonly IBrush ColTealSoft = new SolidColorBrush(Color.Parse("#1D3437"));
    private static readonly IBrush ColAmber = new SolidColorBrush(Color.Parse("#E2B348"));
    private static readonly IBrush ColGreen = new SolidColorBrush(Color.Parse("#41D873"));
    private static readonly IBrush ColMute = new SolidColorBrush(Color.Parse("#8EA0AD"));
    private static readonly IBrush ColText = Brushes.White;
    private static readonly IBrush ColShellBg = new SolidColorBrush(Color.Parse("#0A1117"));
    private static readonly IBrush ColPanelBg = new SolidColorBrush(Color.Parse("#132028"));
    private static readonly IBrush ColPanelAlt = new SolidColorBrush(Color.Parse("#172730"));
    private static readonly IBrush ColButtonIdle = new SolidColorBrush(Color.Parse("#10202A"));
    private static readonly IBrush ColBorder = new SolidColorBrush(Color.Parse("#233740"));
    private static readonly IBrush ColOverlayScrim = new SolidColorBrush(Color.Parse("#B8000000"));

    private static GetSnapshotJsonFn? _getSnapshotJson;
    private static ToggleMacroFn? _toggleMacro;
    private static SetSubsystemEnabledFn? _setSubsystemEnabled;
    private static SelectProfileFn? _selectProfile;
    private static AdjustNavigationSettingFn? _adjustNavigationSetting;
    private static bool _bindingAttempted;

    internal static Control Create()
    {
        TryBind();

        Snapshot currentSnapshot = new();
        Border? activePicker = null;
        string? activePageKey = null;

        var launcherButtons = new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase);
        var subsystemButtons = new Dictionary<int, Button>();

        var root = new Grid();

        var dashboardScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        root.Children.Add(dashboardScroll);

        var overlayHost = new Grid();
        root.Children.Add(overlayHost);

        var pageBackdrop = new Border
        {
            IsVisible = false,
            Background = ColOverlayScrim
        };
        overlayHost.Children.Add(pageBackdrop);

        var pageWindow = new Border
        {
            IsVisible = false,
            Margin = new Thickness(16),
            Background = ColShellBg,
            BorderBrush = ColTeal,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14),
            ClipToBounds = true
        };
        overlayHost.Children.Add(pageWindow);

        var pickerCanvas = new Canvas();
        overlayHost.Children.Add(pickerCanvas);

        var pageGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*")
        };
        pageWindow.Child = pageGrid;

        var pageHeader = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 0, 0, 10)
        };
        var pageTitleText = new TextBlock
        {
            Text = "RynthAi",
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            Foreground = ColText
        };
        var pageCloseButton = new Button
        {
            Content = "Close",
            Height = 30,
            Background = ColButtonIdle,
            Foreground = ColText,
            BorderBrush = ColBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 4)
        };
        pageHeader.Children.Add(pageTitleText);
        pageHeader.Children.Add(pageCloseButton);
        Grid.SetColumn(pageCloseButton, 1);
        pageGrid.Children.Add(pageHeader);

        var pageContentHost = new ContentControl();
        pageGrid.Children.Add(pageContentHost);
        Grid.SetRow(pageContentHost, 1);

        void ClosePicker()
        {
            if (activePicker == null)
                return;

            pickerCanvas.Children.Remove(activePicker);
            activePicker = null;
        }

        void ApplyLauncherStyles()
        {
            foreach ((string key, Button button) in launcherButtons)
                ApplyLauncherButton(button, string.Equals(activePageKey, key, StringComparison.OrdinalIgnoreCase));
        }

        void HidePage()
        {
            activePageKey = null;
            pageBackdrop.IsVisible = false;
            pageWindow.IsVisible = false;
            pageContentHost.Content = null;
            ApplyLauncherStyles();
            ClosePicker();
        }

        void ShowPage(string key, string title, Control content)
        {
            if (string.Equals(activePageKey, key, StringComparison.OrdinalIgnoreCase))
            {
                HidePage();
                return;
            }

            activePageKey = key;
            SetText(pageTitleText, title);
            pageContentHost.Content = content;
            pageBackdrop.IsVisible = true;
            pageWindow.IsVisible = true;
            ApplyLauncherStyles();
            ClosePicker();
        }

        pageCloseButton.Click += (_, _) => HidePage();
        pageBackdrop.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            HidePage();
        };
        pageWindow.PointerPressed += (_, e) => e.Handled = true;
        pickerCanvas.PointerPressed += (_, e) =>
        {
            if (activePicker == null || !ReferenceEquals(e.Source, pickerCanvas))
                return;

            e.Handled = true;
            ClosePicker();
        };
        dashboardScroll.PointerPressed += (_, _) => ClosePicker();

        var shell = new Border
        {
            Background = ColShellBg,
            BorderBrush = ColBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(6),
            Margin = new Thickness(2)
        };
        dashboardScroll.Content = shell;

        var dashboard = new StackPanel { Spacing = 6 };
        shell.Child = dashboard;

        var titleGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };
        var titleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2
        };
        titleRow.Children.Add(new TextBlock
        {
            Text = "N",
            FontSize = 17,
            FontWeight = FontWeight.Bold,
            Foreground = ColTeal
        });
        titleRow.Children.Add(new TextBlock
        {
            Text = "NEXAI DASHBOARD",
            FontSize = 17,
            FontWeight = FontWeight.SemiBold,
            Foreground = ColText
        });
        titleGrid.Children.Add(titleRow);

        var headerButtonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        headerButtonRow.Children.Add(CreateHeaderChip("Lock"));
        headerButtonRow.Children.Add(CreateHeaderChip("-"));
        headerButtonRow.Children.Add(CreateHeaderChip("+"));
        headerButtonRow.Children.Add(CreateHeaderChip("_"));
        headerButtonRow.Children.Add(CreateHeaderChip("X"));
        var modePillText = new TextBlock
        {
            Text = "v4.0",
            Foreground = ColMute,
            FontSize = 9,
            Margin = new Thickness(2, 2, 4, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        headerButtonRow.Children.Insert(0, modePillText);
        titleGrid.Children.Add(headerButtonRow);
        Grid.SetColumn(headerButtonRow, 1);
        dashboard.Children.Add(titleGrid);

        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto")
        };
        dashboard.Children.Add(headerGrid);

        var macroStatusLabel = new TextBlock
        {
            Text = "Macro Status:",
            FontSize = 13,
            Foreground = ColMute,
            FontWeight = FontWeight.SemiBold
        };
        headerGrid.Children.Add(macroStatusLabel);
        Grid.SetRow(macroStatusLabel, 0);
        Grid.SetColumn(macroStatusLabel, 0);

        var profileRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*")
        };
        profileRow.Children.Add(new TextBlock
        {
            Text = "Profile:",
            FontSize = 11,
            Foreground = ColMute,
            Margin = new Thickness(0, 3, 8, 0)
        });
        var profileValue = CreateValueText("Default");
        profileValue.FontSize = 11;
        profileRow.Children.Add(profileValue);
        Grid.SetColumn(profileValue, 1);
        headerGrid.Children.Add(profileRow);
        Grid.SetRow(profileRow, 0);
        Grid.SetColumn(profileRow, 1);

        var macroStatusButton = CreateActionButton("STOPPED");
        macroStatusButton.MinWidth = 100;
        macroStatusButton.Height = 24;
        macroStatusButton.HorizontalAlignment = HorizontalAlignment.Left;
        macroStatusButton.Click += (_, _) =>
        {
            ClosePicker();
            _toggleMacro?.Invoke();
        };
        headerGrid.Children.Add(macroStatusButton);
        Grid.SetRow(macroStatusButton, 1);
        Grid.SetColumn(macroStatusButton, 0);

        var navSelectorButton = CreateSelectorButton("None");
        navSelectorButton.Height = 24;
        var navRow = BuildCompactRow("Nav:", navSelectorButton);
        headerGrid.Children.Add(navRow);
        Grid.SetRow(navRow, 1);
        Grid.SetColumn(navRow, 1);

        var stateValue = CreateValueText("Idle");
        stateValue.FontSize = 11;
        stateValue.Foreground = ColAmber;
        var stateRow = BuildCompactRow("State:", stateValue);
        headerGrid.Children.Add(stateRow);
        Grid.SetRow(stateRow, 2);
        Grid.SetColumn(stateRow, 0);

        var lootSelectorButton = CreateSelectorButton("None");
        lootSelectorButton.Height = 24;
        var lootRow = BuildCompactRow("Loot:", lootSelectorButton);
        headerGrid.Children.Add(lootRow);
        Grid.SetRow(lootRow, 2);
        Grid.SetColumn(lootRow, 1);

        var combatBorder = new Border
        {
            Background = ColPanelBg,
            BorderBrush = ColBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8)
        };
        dashboard.Children.Add(combatBorder);

        var combatGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("70,*")
        };
        combatBorder.Child = combatGrid;

        var toggleGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            Margin = new Thickness(0, 12, 8, 0)
        };
        Button combatButton = CreateSubsystemButton("C", 0, subsystemButtons);
        Button buffButton = CreateSubsystemButton("B", 1, subsystemButtons);
        Button navButton = CreateSubsystemButton("N", 2, subsystemButtons);
        Button lootButton = CreateSubsystemButton("L", 3, subsystemButtons);
        Button metaButton = CreateSubsystemButton("MACRO", 4, subsystemButtons);
        combatButton.MinHeight = 26;
        buffButton.MinHeight = 26;
        navButton.MinHeight = 26;
        lootButton.MinHeight = 26;
        metaButton.MinHeight = 22;
        metaButton.FontSize = 10;

        AddGridButton(toggleGrid, combatButton, 0, 0);
        AddGridButton(toggleGrid, buffButton, 0, 1);
        AddGridButton(toggleGrid, navButton, 1, 0);
        AddGridButton(toggleGrid, lootButton, 1, 1);
        AddGridButton(toggleGrid, metaButton, 2, 0, 2);
        combatGrid.Children.Add(toggleGrid);

        void ToggleSubsystem(int subsystemId)
        {
            ClosePicker();

            bool enabled = subsystemId switch
            {
                0 => currentSnapshot.CombatEnabled,
                1 => currentSnapshot.BuffingEnabled,
                2 => currentSnapshot.NavigationEnabled,
                3 => currentSnapshot.LootingEnabled,
                4 => currentSnapshot.MetaEnabled,
                _ => false
            };

            _setSubsystemEnabled?.Invoke(subsystemId, enabled ? 0 : 1);
        }

        combatButton.Click += (_, _) => ToggleSubsystem(0);
        buffButton.Click += (_, _) => ToggleSubsystem(1);
        navButton.Click += (_, _) => ToggleSubsystem(2);
        lootButton.Click += (_, _) => ToggleSubsystem(3);
        metaButton.Click += (_, _) => ToggleSubsystem(4);

        var runtimeStack = new StackPanel { Spacing = 4 };
        var runtimeHeadline = new TextBlock
        {
            Text = "NO TARGET",
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#D8E4F0"))
        };
        var targetHpValue = new TextBlock
        {
            Text = "0%",
            FontSize = 11,
            Foreground = ColText,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var targetHeaderRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };
        targetHeaderRow.Children.Add(runtimeHeadline);
        targetHeaderRow.Children.Add(targetHpValue);
        Grid.SetColumn(targetHpValue, 1);

        var targetBar = new Border
        {
            Height = 8,
            Background = ColButtonIdle,
            BorderBrush = ColBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4)
        };
        var targetBarFill = new Border
        {
            Width = 0,
            Background = ColAmber,
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(4)
        };
        targetBar.Child = targetBarFill;

        var playerVitalsLabel = new TextBlock
        {
            Text = "PLAYER VITALS",
            FontSize = 10,
            Foreground = ColMute
        };
        var hpBar = CreateVitalBar("#C63B3B", "HP");
        var stBar = CreateVitalBar("#36A85B", "ST");
        var mnBar = CreateVitalBar("#347DDD", "MN");
        var targetValue = CreateValueText("0x00000000");
        targetValue.FontSize = 10;
        targetValue.Foreground = ColMute;

        runtimeStack.Children.Add(targetHeaderRow);
        runtimeStack.Children.Add(targetBar);
        runtimeStack.Children.Add(playerVitalsLabel);
        runtimeStack.Children.Add(hpBar.Row);
        runtimeStack.Children.Add(stBar.Row);
        runtimeStack.Children.Add(mnBar.Row);
        combatGrid.Children.Add(runtimeStack);
        Grid.SetColumn(runtimeStack, 1);

        var launcherGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            Margin = new Thickness(0, 2, 0, 0)
        };
        dashboard.Children.Add(launcherGrid);

        var routePanel = CreateListHost();
        var monsterPanel = CreateListHost();
        var metaRulesPanel = CreateListHost();
        var itemPanel = CreateListHost();
        var lootPanel = CreateListHost();
        var buffPanel = CreateListHost();
        var settingsPanel = CreateListHost();
        var navPageSummary = CreateValueText("No navigation profile loaded.");
        var metaPageSummary = CreateValueText("No meta profile loaded.");
        var weaponPageSummary = CreateValueText("No tracked items.");
        var settingsPageSummary = CreateValueText("Settings are waiting for snapshot data.");
        var movementModeValue = CreateValueText("0");
        var followValue = CreateValueText("1.5 yd");
        var stopTurnValue = CreateValueText("20.0 deg");
        var resumeTurnValue = CreateValueText("10.0 deg");
        var deadZoneValue = CreateValueText("4.0 deg");
        var sweepValue = CreateValueText("2.5x");
        var luaScriptBox = CreateReadonlyBox(180, true);
        var luaConsoleBox = CreateReadonlyBox(180, true);
        var navSelectorPageButton = CreateSelectorButton("None");
        var metaSelectorPageButton = CreateSelectorButton("None");
        var tuningPanel = BuildNavTuningPanel(
            movementModeValue,
            followValue,
            stopTurnValue,
            resumeTurnValue,
            deadZoneValue,
            sweepValue,
            (settingId, delta) => _adjustNavigationSetting?.Invoke(settingId, delta));

        Control navigationPage = BuildPageContent(
            ("Active Nav Profile", navSelectorPageButton),
            ("Route Summary", navPageSummary),
            ("Route Points", routePanel));
        Control monstersPage = BuildPageContent(("Monster Rules", monsterPanel));
        Control macroPage = BuildPageContent(
            ("Meta Profile", metaSelectorPageButton),
            ("Macro State", metaPageSummary),
            ("Macro Rules", metaRulesPanel));
        Control weaponsPage = BuildPageContent(
            ("Tracked Equipment", weaponPageSummary),
            ("Weapons & Items", itemPanel),
            ("Loot Rules", lootPanel));
        Control settingsPage = BuildPageContent(
            ("Settings Summary", settingsPageSummary),
            ("Live Nav Tuning", tuningPanel),
            ("Tuning", settingsPanel),
            ("Buff Rules", buffPanel));
        Control luaPage = BuildPageContent(
            ("Lua Script", luaScriptBox),
            ("Lua Console", luaConsoleBox));

        AddLauncherButton(launcherGrid, 0, 0, "meta", "Macro Rules", () => ShowPage("meta", "Macro Rules", macroPage), launcherButtons);
        AddLauncherButton(launcherGrid, 0, 1, "monsters", "Monsters", () => ShowPage("monsters", "Monsters", monstersPage), launcherButtons);
        AddLauncherButton(launcherGrid, 0, 2, "settings", "Settings", () => ShowPage("settings", "Settings", settingsPage), launcherButtons);
        AddLauncherButton(launcherGrid, 1, 0, "navigation", "Navigation", () => ShowPage("navigation", "Navigation", navigationPage), launcherButtons);
        AddLauncherButton(launcherGrid, 1, 1, "weapons", "Weapons", () => ShowPage("weapons", "Weapons", weaponsPage), launcherButtons);
        AddLauncherButton(launcherGrid, 1, 2, "lua", "Lua Scripts", () => ShowPage("lua", "Lua Scripts", luaPage), launcherButtons);
        ApplyLauncherStyles();

        var metaSelectorButton = CreateSelectorButton("None");
        metaSelectorButton.Height = 24;

        void ShowProfilePicker(Button anchor, string[] items, int selectedIndex, Action<int> onSelect)
        {
            ClosePicker();

            string[] resolvedItems = items.Length == 0 ? ["None"] : items;
            int safeIndex = Math.Clamp(selectedIndex, 0, Math.Max(0, resolvedItems.Length - 1));
            Point anchorPoint = anchor.TranslatePoint(new Point(0, anchor.Bounds.Height + 4), pickerCanvas) ?? new Point(16, 16);
            double pickerWidth = 220;
            double rowHeight = 34;
            double pickerHeight = Math.Min((resolvedItems.Length * rowHeight) + 16, Math.Max(180, root.Bounds.Height - 24));

            var pickerStack = new StackPanel { Spacing = 4 };
            for (int i = 0; i < resolvedItems.Length; i++)
            {
                int capturedIndex = i;
                var itemButton = new Button
                {
                    Content = resolvedItems[i],
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Background = i == safeIndex ? ColTealSoft : ColPanelAlt,
                    Foreground = i == safeIndex ? ColTeal : ColText,
                    BorderBrush = ColBorder,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10, 6),
                    MinWidth = 180,
                    Height = 30
                };
                itemButton.Click += (_, _) =>
                {
                    onSelect(capturedIndex);
                    ClosePicker();
                };
                pickerStack.Children.Add(itemButton);
            }

            double maxLeft = Math.Max(8, root.Bounds.Width - pickerWidth - 8);
            double left = Math.Clamp(anchorPoint.X, 8, maxLeft);
            double top = anchorPoint.Y;
            double maxTop = Math.Max(8, root.Bounds.Height - pickerHeight - 8);
            if (top > maxTop)
                top = Math.Max(8, anchorPoint.Y - pickerHeight - anchor.Bounds.Height - 8);
            top = Math.Min(top, maxTop);

            activePicker = new Border
            {
                Width = pickerWidth,
                MaxHeight = pickerHeight,
                Background = ColShellBg,
                BorderBrush = ColTeal,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8),
                Child = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = pickerStack
                }
            };

            pickerCanvas.Children.Add(activePicker);
            Canvas.SetLeft(activePicker, left);
            Canvas.SetTop(activePicker, top);
        }

        navSelectorButton.Click += (_, _) =>
            ShowProfilePicker(navSelectorButton, currentSnapshot.NavProfiles, currentSnapshot.SelectedNavIndex, index => _selectProfile?.Invoke(0, index));
        lootSelectorButton.Click += (_, _) =>
            ShowProfilePicker(lootSelectorButton, currentSnapshot.LootProfiles, currentSnapshot.SelectedLootIndex, index => _selectProfile?.Invoke(1, index));
        metaSelectorButton.Click += (_, _) =>
            ShowProfilePicker(metaSelectorButton, currentSnapshot.MetaProfiles, currentSnapshot.SelectedMetaIndex, index => _selectProfile?.Invoke(2, index));
        navSelectorPageButton.Click += (_, _) =>
            ShowProfilePicker(navSelectorPageButton, currentSnapshot.NavProfiles, currentSnapshot.SelectedNavIndex, index => _selectProfile?.Invoke(0, index));
        metaSelectorPageButton.Click += (_, _) =>
            ShowProfilePicker(metaSelectorPageButton, currentSnapshot.MetaProfiles, currentSnapshot.SelectedMetaIndex, index => _selectProfile?.Invoke(2, index));

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            if (_getSnapshotJson == null)
                TryBind();

            currentSnapshot = ReadSnapshot();
            bool macroRunning = currentSnapshot.MacroRunning;
            SetText(macroStatusButton, macroRunning ? "RUNNING" : "STOPPED");
            ApplyStatusButton(macroStatusButton, macroRunning);

            SetText(stateValue, string.IsNullOrWhiteSpace(currentSnapshot.CurrentState) ? "Idle" : currentSnapshot.CurrentState);
            SetText(profileValue, string.IsNullOrWhiteSpace(currentSnapshot.SelectedProfile) ? "Default" : currentSnapshot.SelectedProfile);
            SetText(navSelectorButton, FormatSelectorText(currentSnapshot.CurrentNavProfile));
            SetText(lootSelectorButton, FormatSelectorText(currentSnapshot.CurrentLootProfile));
            SetText(navSelectorPageButton, FormatSelectorText(currentSnapshot.CurrentNavProfile));
            SetText(metaSelectorPageButton, FormatSelectorText(currentSnapshot.CurrentMetaProfile));

            UpdateSubsystemButton(combatButton, currentSnapshot.CombatEnabled);
            UpdateSubsystemButton(buffButton, currentSnapshot.BuffingEnabled);
            UpdateSubsystemButton(navButton, currentSnapshot.NavigationEnabled);
            UpdateSubsystemButton(lootButton, currentSnapshot.LootingEnabled);
            UpdateSubsystemButton(metaButton, currentSnapshot.MetaEnabled);

            SetText(
                runtimeHeadline,
                currentSnapshot.CurrentTargetId == 0
                    ? "NO TARGET"
                    : $"TARGET 0x{currentSnapshot.CurrentTargetId:X8}");
            SetText(targetValue, currentSnapshot.CurrentTargetId == 0 ? "0x00000000" : $"0x{currentSnapshot.CurrentTargetId:X8}");
            SetText(targetHpValue, currentSnapshot.CurrentTargetId == 0 ? "0" : "LIVE");
            double targetFillWidth = currentSnapshot.CurrentTargetId == 0 ? 0 : 170;
            targetBarFill.Width = targetFillWidth;

            SetText(hpBar.Value, "HP: host hook pending");
            SetText(stBar.Value, $"ST: state {(string.IsNullOrWhiteSpace(currentSnapshot.CurrentState) ? "Idle" : currentSnapshot.CurrentState)}");
            SetText(mnBar.Value, $"MN: target {(currentSnapshot.CurrentTargetId == 0 ? "none" : $"0x{currentSnapshot.CurrentTargetId:X8}")}");
            hpBar.Fill.Width = currentSnapshot.LoginComplete ? 160 : 0;
            stBar.Fill.Width = currentSnapshot.LoginComplete ? 145 : 0;
            mnBar.Fill.Width = currentSnapshot.LoginComplete ? 135 : 0;

            SetText(
                navPageSummary,
                currentSnapshot.NavPointCount == 0
                    ? "No navigation route is currently loaded."
                    : $"{FormatSelectorText(currentSnapshot.CurrentNavProfile)} | {currentSnapshot.NavRouteType} route | point {Math.Min(currentSnapshot.ActiveNavIndex + 1, currentSnapshot.NavPointCount)}/{currentSnapshot.NavPointCount}");
            SetText(
                metaPageSummary,
                $"{FormatSelectorText(currentSnapshot.CurrentMetaProfile)} | {currentSnapshot.MetaRuleCount} rule(s) | state {(string.IsNullOrWhiteSpace(currentSnapshot.CurrentState) ? "Idle" : currentSnapshot.CurrentState)}");
            SetText(
                weaponPageSummary,
                $"Item rules: {CountRealLines(currentSnapshot.ItemRuleLines)} | Loot rules: {currentSnapshot.LootRuleCount} | Known containers: {currentSnapshot.KnownContainers}");
            SetText(
                settingsPageSummary,
                currentSnapshot.SettingsHighlights.Length == 0
                    ? "Settings are waiting for snapshot data."
                    : string.Join(Environment.NewLine, currentSnapshot.SettingsHighlights.Take(3)));
            SetText(movementModeValue, currentSnapshot.MovementMode.ToString());
            SetText(followValue, $"{currentSnapshot.FollowNavMin:0.0} yd");
            SetText(stopTurnValue, $"{currentSnapshot.NavStopTurnAngle:0.0} deg");
            SetText(resumeTurnValue, $"{currentSnapshot.NavResumeTurnAngle:0.0} deg");
            SetText(deadZoneValue, $"{currentSnapshot.NavDeadZone:0.0} deg");
            SetText(sweepValue, $"{currentSnapshot.NavSweepMult:0.0}x");

            UpdateListPanel(routePanel, currentSnapshot.RoutePointLines);
            UpdateListPanel(monsterPanel, currentSnapshot.MonsterRuleLines);
            UpdateListPanel(metaRulesPanel, currentSnapshot.MetaRuleLines);
            UpdateListPanel(itemPanel, currentSnapshot.ItemRuleLines);
            UpdateListPanel(lootPanel, currentSnapshot.LootRuleLines);
            UpdateListPanel(buffPanel, currentSnapshot.BuffRuleLines);
            UpdateListPanel(settingsPanel, currentSnapshot.SettingsHighlights);

            SetText(luaScriptBox, string.IsNullOrWhiteSpace(currentSnapshot.LuaScriptPreview) ? "Nothing loaded yet." : currentSnapshot.LuaScriptPreview);
            SetText(luaConsoleBox, string.IsNullOrWhiteSpace(currentSnapshot.LuaConsolePreview) ? "Nothing loaded yet." : currentSnapshot.LuaConsolePreview);
        };

        timer.Start();
        root.DetachedFromVisualTree += (_, _) =>
        {
            timer.Stop();
            ClosePicker();
        };

        return root;
    }

    private static Border MakePanel()
    {
        return new Border
        {
            Background = ColPanelBg,
            BorderBrush = ColBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12)
        };
    }

    private static TextBlock CreateValueText(string text)
    {
        return new TextBlock
        {
            Text = text,
            Foreground = ColText,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
    }

    private static Border CreateHeaderChip(string text)
    {
        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#203E62")),
            BorderBrush = ColBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 1),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 9,
                Foreground = ColText
            }
        };
    }

    private static Grid BuildCompactRow(string label, Control value)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*")
        };
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = ColMute,
            Margin = new Thickness(0, 4, 8, 0)
        });
        row.Children.Add(value);
        Grid.SetColumn(value, 1);
        return row;
    }

    private static (Grid Row, Border Fill, TextBlock Value) CreateVitalBar(string fillColor, string label)
    {
        var row = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto")
        };

        var value = new TextBlock
        {
            Text = $"{label}: host hook pending",
            FontSize = 10,
            Foreground = ColText
        };
        row.Children.Add(value);

        var bar = new Border
        {
            Height = 11,
            Margin = new Thickness(0, 2, 0, 0),
            Background = ColButtonIdle,
            BorderBrush = ColBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4)
        };
        var fill = new Border
        {
            Width = 0,
            Background = new SolidColorBrush(Color.Parse(fillColor)),
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(4)
        };
        bar.Child = fill;
        row.Children.Add(bar);
        Grid.SetRow(bar, 1);

        return (row, fill, value);
    }

    private static Button CreateActionButton(string text)
    {
        return new Button
        {
            Content = text,
            MinWidth = 120,
            Height = 30,
            Background = ColButtonIdle,
            Foreground = ColText,
            BorderBrush = ColBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            FontWeight = FontWeight.SemiBold,
            FontSize = 11,
            Padding = new Thickness(10, 3)
        };
    }

    private static Button CreateSelectorButton(string text)
    {
        return new Button
        {
            Content = text,
            Height = 28,
            MinWidth = 110,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = ColButtonIdle,
            Foreground = ColText,
            BorderBrush = ColBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            FontSize = 11,
            Padding = new Thickness(8, 3)
        };
    }

    private static Button CreateSubsystemButton(string text, int subsystemId, IDictionary<int, Button> buttonMap)
    {
        var button = new Button
        {
            Content = text,
            MinHeight = 28,
            Background = ColButtonIdle,
            Foreground = ColMute,
            BorderBrush = ColBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            FontWeight = FontWeight.SemiBold,
            FontSize = 10,
            Padding = new Thickness(4, 3)
        };

        buttonMap[subsystemId] = button;
        return button;
    }

    private static void AddGridButton(Grid grid, Button button, int row, int column, int columnSpan = 1)
    {
        grid.Children.Add(button);
        Grid.SetRow(button, row);
        Grid.SetColumn(button, column);
        if (columnSpan > 1)
            Grid.SetColumnSpan(button, columnSpan);
    }

    private static void AddInfoRow(Grid grid, int row, int column, string label, Control value)
    {
        var panel = new Border
        {
            Background = ColPanelAlt,
            BorderBrush = ColBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10)
        };

        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(CreateSectionLabel(label));
        stack.Children.Add(value);
        panel.Child = stack;

        grid.Children.Add(panel);
        Grid.SetRow(panel, row);
        Grid.SetColumn(panel, column);
    }

    private static Border CreateMetricCard(string title, TextBlock value, IBrush accent)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(CreateSectionLabel(title));
        stack.Children.Add(value);

        return new Border
        {
            Background = ColPanelAlt,
            BorderBrush = accent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            Child = stack
        };
    }

    private static TextBlock CreateSectionLabel(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = ColMute
        };
    }

    private static void AddLauncherButton(
        Grid grid,
        int row,
        int column,
        string key,
        string text,
        Action onClick,
        IDictionary<string, Button> buttonMap)
    {
        var button = new Button
        {
            Content = text,
            MinHeight = 28,
            Background = ColButtonIdle,
            Foreground = ColMute,
            BorderBrush = ColBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            FontWeight = FontWeight.Normal,
            FontSize = 10,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 3),
            Margin = new Thickness(0, 0, 0, 4)
        };
        button.Click += (_, _) => onClick();

        grid.Children.Add(button);
        Grid.SetRow(button, row);
        Grid.SetColumn(button, column);
        buttonMap[key] = button;
    }

    private static Control BuildPageContent(params (string Title, Control Content)[] sections)
    {
        var stack = new StackPanel { Spacing = 10 };
        foreach ((string title, Control content) in sections)
        {
            var sectionStack = new StackPanel { Spacing = 6 };
            sectionStack.Children.Add(CreateSectionLabel(title));
            sectionStack.Children.Add(content);

            stack.Children.Add(new Border
            {
                Background = ColPanelBg,
                BorderBrush = ColBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
                Child = sectionStack
            });
        }

        return new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = stack
        };
    }

    private static TextBox CreateListHost()
    {
        return CreateReadonlyBox(220, false);
    }

    private static TextBox CreateReadonlyBox(double minHeight, bool wrap)
    {
        var textBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            MinHeight = minHeight,
            Text = string.Empty,
            FontFamily = new FontFamily("Consolas,Courier New,monospace"),
            FontSize = 12,
            Foreground = ColText,
            Background = ColButtonIdle,
            BorderBrush = ColBorder,
            BorderThickness = new Thickness(1),
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap
        };

        ScrollViewer.SetVerticalScrollBarVisibility(textBox, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(textBox, wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        return textBox;
    }

    private static Control BuildNavTuningPanel(
        TextBlock movementModeValue,
        TextBlock followValue,
        TextBlock stopTurnValue,
        TextBlock resumeTurnValue,
        TextBlock deadZoneValue,
        TextBlock sweepValue,
        Action<int, float> onAdjust)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(CreateTuningRow("Mode", movementModeValue, () => onAdjust(0, -1f), () => onAdjust(0, 1f)));
        stack.Children.Add(CreateTuningRow("Follow Min", followValue, () => onAdjust(1, -0.1f), () => onAdjust(1, 0.1f)));
        stack.Children.Add(CreateTuningRow("Stop Turn", stopTurnValue, () => onAdjust(2, -1f), () => onAdjust(2, 1f)));
        stack.Children.Add(CreateTuningRow("Resume Turn", resumeTurnValue, () => onAdjust(3, -1f), () => onAdjust(3, 1f)));
        stack.Children.Add(CreateTuningRow("Dead Zone", deadZoneValue, () => onAdjust(4, -0.5f), () => onAdjust(4, 0.5f)));
        stack.Children.Add(CreateTuningRow("Sweep", sweepValue, () => onAdjust(5, -0.1f), () => onAdjust(5, 0.1f)));

        return new Border
        {
            Background = ColPanelAlt,
            BorderBrush = ColBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            Child = stack
        };
    }

    private static Control CreateTuningRow(string label, TextBlock valueBlock, Action onMinus, Action onPlus)
    {
        valueBlock.HorizontalAlignment = HorizontalAlignment.Center;
        valueBlock.VerticalAlignment = VerticalAlignment.Center;

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"),
            Margin = new Thickness(0, 0, 0, 2)
        };

        grid.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = ColMute,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        });

        var minusButton = CreateAdjustButton("-");
        minusButton.Click += (_, _) => onMinus();
        grid.Children.Add(minusButton);
        Grid.SetColumn(minusButton, 1);

        grid.Children.Add(valueBlock);
        Grid.SetColumn(valueBlock, 2);

        var plusButton = CreateAdjustButton("+");
        plusButton.Click += (_, _) => onPlus();
        grid.Children.Add(plusButton);
        Grid.SetColumn(plusButton, 3);

        return grid;
    }

    private static Button CreateAdjustButton(string text)
    {
        return new Button
        {
            Content = text,
            Width = 28,
            Height = 26,
            Background = ColButtonIdle,
            Foreground = ColText,
            BorderBrush = ColBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(0)
        };
    }

    private static void ApplyStatusButton(Button button, bool enabled)
    {
        button.Background = enabled ? ColGreen : ColButtonIdle;
        button.BorderBrush = enabled ? ColGreen : ColBorder;
        button.Foreground = enabled ? Brushes.Black : ColText;
    }

    private static void UpdateSubsystemButton(Button button, bool enabled)
    {
        button.Background = enabled ? ColTealSoft : ColButtonIdle;
        button.BorderBrush = enabled ? ColTeal : ColBorder;
        button.Foreground = enabled ? ColTeal : ColText;
        button.FontWeight = enabled ? FontWeight.SemiBold : FontWeight.Normal;
    }

    private static void ApplyLauncherButton(Button button, bool active)
    {
        button.Background = active ? ColTealSoft : ColButtonIdle;
        button.BorderBrush = active ? ColTeal : ColBorder;
        button.Foreground = active ? ColTeal : ColMute;
    }

    private static string FormatSelectorText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) || string.Equals(value, "None", StringComparison.OrdinalIgnoreCase)
            ? "None"
            : value;
    }

    private static void UpdateListPanel(Control host, string[] lines)
    {
        string text = lines.Length == 0 ? "Nothing loaded yet." : string.Join(Environment.NewLine, lines);

        if (host is TextBox textBox)
        {
            SetText(textBox, text);
            return;
        }

        if (host is TextBlock textBlock)
            SetText(textBlock, text);
    }

    private static Snapshot ReadSnapshot()
    {
        if (_getSnapshotJson == null)
            return new Snapshot();

        try
        {
            IntPtr ptr = _getSnapshotJson();
            if (ptr == IntPtr.Zero)
                return new Snapshot();

            string json = Marshal.PtrToStringAnsi(ptr) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(json))
                return new Snapshot();

            return JsonSerializer.Deserialize(json, SnapshotJsonContext.Default.Snapshot) ?? new Snapshot();
        }
        catch (Exception ex)
        {
            return new Snapshot
            {
                LastAction = $"Failed to read RynthAi snapshot: {ex.Message}"
            };
        }
    }

    private static void TryBind()
    {
        LoadedPlugin? plugin = PluginManager.Plugins.FirstOrDefault(
            p => p.DisplayName.Contains("RynthAi", StringComparison.OrdinalIgnoreCase));
        if (plugin == null || plugin.ModuleHandle == IntPtr.Zero)
            return;

        _getSnapshotJson ??= Bind<GetSnapshotJsonFn>(plugin, "RynthPluginGetSnapshotJson");
        _toggleMacro ??= Bind<ToggleMacroFn>(plugin, "RynthPluginToggleMacro");
        _setSubsystemEnabled ??= Bind<SetSubsystemEnabledFn>(plugin, "RynthPluginSetSubsystemEnabled");
        _selectProfile ??= Bind<SelectProfileFn>(plugin, "RynthPluginSelectProfile");
        _adjustNavigationSetting ??= Bind<AdjustNavigationSettingFn>(plugin, "RynthPluginAdjustNavigationSetting");

        if (_bindingAttempted || _getSnapshotJson == null)
            return;

        _bindingAttempted = true;
        RynthLog.UI("RynthAiPanel: bound live RynthAi plugin exports.");
    }

    private static T? Bind<T>(LoadedPlugin plugin, string exportName) where T : Delegate
    {
        IntPtr address = GetProcAddress(plugin.ModuleHandle, exportName);
        return address == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private static string BuildHookSummary(Snapshot snapshot)
    {
        var parts = new List<string>(5);
        if (snapshot.HasCombatHooks)
            parts.Add("combat");
        if (snapshot.HasMovementHooks)
            parts.Add("movement");
        if (snapshot.HasCoords)
            parts.Add("coords");
        if (snapshot.HasPlayerId)
            parts.Add("player");
        if (snapshot.HasSelection)
            parts.Add("selection");

        return parts.Count == 0 ? "Hooks pending." : string.Join(" | ", parts);
    }

    private static int CountRealLines(string[] lines)
    {
        return lines.Length == 1 && lines[0].StartsWith("No ", StringComparison.OrdinalIgnoreCase)
            ? 0
            : lines.Length;
    }

    private static void SetText(TextBlock textBlock, string text)
    {
        if (!string.Equals(textBlock.Text, text, StringComparison.Ordinal))
            textBlock.Text = text;
    }

    private static void SetText(TextBox textBox, string text)
    {
        if (!string.Equals(textBox.Text, text, StringComparison.Ordinal))
            textBox.Text = text;
    }

    private static void SetText(Button button, string text)
    {
        if (!string.Equals(button.Content as string, text, StringComparison.Ordinal))
            button.Content = text;
    }

    private static void SetBackground(Border border, IBrush brush)
    {
        if (!ReferenceEquals(border.Background, brush))
            border.Background = brush;
    }

    private sealed class Snapshot
    {
        public bool Initialized { get; set; }
        public bool UiInitialized { get; set; }
        public bool LoginComplete { get; set; }
        public bool WindowVisible { get; set; }
        public bool MacroRunning { get; set; }
        public string CurrentState { get; set; } = string.Empty;
        public string SelectedProfile { get; set; } = string.Empty;
        public string LastAction { get; set; } = string.Empty;
        public uint HookFlags { get; set; }
        public bool CombatEnabled { get; set; }
        public bool BuffingEnabled { get; set; }
        public bool NavigationEnabled { get; set; }
        public bool LootingEnabled { get; set; }
        public bool MetaEnabled { get; set; }
        public uint CurrentTargetId { get; set; }
        public int CurrentCombatMode { get; set; }
        public int SeenObjects { get; set; }
        public long CreatedObjects { get; set; }
        public long DeletedObjects { get; set; }
        public long UpdatedObjects { get; set; }
        public long InventoryUpdates { get; set; }
        public long ViewedContents { get; set; }
        public long ClosedContents { get; set; }
        public string CurrentPointLabel { get; set; } = string.Empty;
        public double DistanceToWaypoint { get; set; }
        public int NavPointCount { get; set; }
        public int ActiveNavIndex { get; set; }
        public string NavRouteType { get; set; } = string.Empty;
        public uint OpenContainerId { get; set; }
        public int KnownContainers { get; set; }
        public int LootRuleCount { get; set; }
        public int MetaRuleCount { get; set; }
        public string CurrentNavProfile { get; set; } = string.Empty;
        public string CurrentLootProfile { get; set; } = string.Empty;
        public string CurrentMetaProfile { get; set; } = string.Empty;
        public string[] NavProfiles { get; set; } = Array.Empty<string>();
        public string[] LootProfiles { get; set; } = Array.Empty<string>();
        public string[] MetaProfiles { get; set; } = Array.Empty<string>();
        public string[] RoutePointLines { get; set; } = Array.Empty<string>();
        public string[] MonsterRuleLines { get; set; } = Array.Empty<string>();
        public string[] ItemRuleLines { get; set; } = Array.Empty<string>();
        public string[] MetaRuleLines { get; set; } = Array.Empty<string>();
        public string[] LootRuleLines { get; set; } = Array.Empty<string>();
        public string[] BuffRuleLines { get; set; } = Array.Empty<string>();
        public string[] SettingsHighlights { get; set; } = Array.Empty<string>();
        public string LuaScriptPreview { get; set; } = string.Empty;
        public string LuaConsolePreview { get; set; } = string.Empty;
        public int SelectedNavIndex { get; set; }
        public int SelectedLootIndex { get; set; }
        public int SelectedMetaIndex { get; set; }
        public bool HasCombatHooks { get; set; }
        public bool HasMovementHooks { get; set; }
        public bool HasMeleeAttack { get; set; }
        public bool HasMissileAttack { get; set; }
        public bool HasChangeCombatMode { get; set; }
        public bool HasCancelAttack { get; set; }
        public bool HasQueryHealth { get; set; }
        public bool HasDoMovement { get; set; }
        public bool HasStopMovement { get; set; }
        public bool HasJump { get; set; }
        public bool HasAutonomy { get; set; }
        public bool HasAutoRun { get; set; }
        public bool HasTapJump { get; set; }
        public bool HasCoords { get; set; }
        public bool HasPlayerId { get; set; }
        public bool HasSelection { get; set; }
        public int MovementMode { get; set; }
        public float FollowNavMin { get; set; }
        public float NavStopTurnAngle { get; set; }
        public float NavResumeTurnAngle { get; set; }
        public float NavDeadZone { get; set; }
        public float NavSweepMult { get; set; }
    }

    [JsonSerializable(typeof(Snapshot))]
    private sealed partial class SnapshotJsonContext : JsonSerializerContext
    {
    }
}
