using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RynthCore.Injector;
using RynthCore.Install;

namespace RynthCore.App.Avalonia;

internal partial class MainWindow : Window
{
    // Tick-rate race protection for auto-launch attempts. The crash-relaunch
    // circuit breaker (configurable) handles abuse from clients that exit
    // immediately after launch — this just stops a single tick from queuing
    // the same account twice while CreateProcess is still in flight.
    // _autoLaunchPassInFlight + HasTrackedPresence (sees the new PID's
    // LaunchContextRecord, written synchronously on CreateProcessW return)
    // handle most of the work; this cooldown is a final belt-and-braces.
    private static readonly TimeSpan AutoLaunchCooldown = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AutoLaunchServerDownLogCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AutoLaunchNeedsPasswordLogCooldown = TimeSpan.FromMinutes(5);

    // Auto-inject only after the external AC client has been continuously
    // graphics-ready for this long. The window gives Decal/UBLoader time to
    // install its own EndScene/WndProc hooks before RynthCore detours on top.
    private static readonly TimeSpan AutoInjectStableReadyWindow = TimeSpan.FromSeconds(5);

    private readonly AcLaunchArgumentBuilder _launchArgumentBuilder = new();
    private readonly AcClientLaunchSettingsService _launchSettings = new();
    private readonly AcClientLoginAutomationService _loginAutomation = new();
    private readonly EngineInjectionService _injector = new();
    private readonly ServerStatusProbeService _serverStatusProbe = new();
    private readonly ObservableCollection<string> _activityItems = [];
    private readonly ObservableCollection<string> _sessionItems = [];
    private readonly ObservableCollection<string> _pluginDllPaths = [];
    private readonly HashSet<int> _launchedSessionPids = [];
    // rynth-headless.exe processes (InjectionMode.Headless): those this launcher started (until
    // DetectQuickExits drops them), and those the last FindClientProcesses saw. They count as
    // clients for sessions, account keys and auto-launch; acclient-only steps skip them.
    private readonly HashSet<int> _launchedHeadlessPids = [];
    private readonly HashSet<int> _headlessClientPids = [];
    private readonly Dictionary<string, DateTime> _headlessRefusalNoticeTimesUtc = new(StringComparer.OrdinalIgnoreCase);
    // Per-PID metadata for crash detection: which account this client belongs
    // to and when we launched it. Used by the auto-relaunch circuit breaker
    // to distinguish "exited normally after a long session" from "quick exit".
    private readonly Dictionary<int, (string AccountKey, DateTime LaunchedAtUtc)> _launchedPidInfo = [];

    // Process handles whose Exited event we've subscribed to. Held in a field
    // so the GC doesn't collect them mid-session (Exited only fires on a live
    // Process object). Disposed when the PID is removed from _launchedPidInfo
    // in DetectQuickExits. Kernel signals Exited the instant the process truly
    // exits (post-WER if any), so the launcher reacts within milliseconds of
    // close instead of waiting up to 2 s for the next _sessionTimer poll. The
    // poll still runs as a redundant safety net for any PID we missed (e.g.
    // GetProcessById race during very fast launch-and-die).
    private readonly Dictionary<int, Process> _exitWatchers = [];
    /// PIDs we have observed as logged-in at least once. Once a PID is in this
    /// set, the stuck-client reaper will never touch it. This is the safety
    /// net against any session-state race that could spuriously flip a
    /// logged-in client back to !IsLoggedIn — a kill-while-playing was
    /// observed once in the wild and we never want it again.
    private readonly HashSet<int> _everLoggedInPids = [];
    // Sliding-window crash history per account-key. RecordCrash appends; the
    // breaker check trims entries older than CrashRelaunchWindowMinutes.
    private readonly Dictionary<string, List<DateTime>> _crashHistoryByAccount = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _crashBreakerLogTimesUtc = new(StringComparer.OrdinalIgnoreCase);
    // Clients that exit faster than this (from the launcher's perspective) are
    // counted as crashes for the auto-relaunch circuit breaker.
    private static readonly TimeSpan CrashQuickExitWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CrashBreakerLogCooldown = TimeSpan.FromMinutes(2);
    // Tracks PIDs we've already applied a saved window position to. Once
    // applied, we switch to save-on-change mode so the user can freely move
    // the window without us snapping it back.
    private readonly HashSet<int> _windowPositionAppliedPids = [];
    // Last observed window rect per PID — set on first post-login observation,
    // used to detect an actual move before we commit anything to appsettings.
    private readonly Dictionary<int, (int x, int y, int w, int h)> _baselineWindowRects = [];
    // When each client's saved window was put back, and the put-backs since (AcWindowRestoreRule).
    private readonly Dictionary<int, (DateTime At, DateTime? Last, int Count)> _windowRestores = [];
    // Serializes prefs-swap launches so two accounts don't race on the live
    // UserPreferences.ini. Non-prefs launches don't acquire this and stay
    // parallel.
    private readonly SemaphoreSlim _userPrefsSwapLock = new(1, 1);
    // After CreateProcess+inject+resume, hold the prefs lock this long so AC
    // has a chance to read UserPreferences.ini before another account swaps.
    private static readonly TimeSpan UserPrefsSettleDelay = TimeSpan.FromSeconds(2);
    // The UserPreferences.ini swap each launched PID started with, so a quick exit can set the
    // saved copy aside and put the previous live file back (UserPrefsSwapRecovery).
    private readonly Dictionary<int, PrefsSwapLaunch> _prefsSwapByPid = [];
    // Counts swaps; a quick exit only restores the live file when no later swap replaced it.
    private long _prefsSwapGeneration;
    private readonly Dictionary<int, DateTime> _autoInjectFirstReadyAtUtc = [];
    private readonly HashSet<int> _autoInjectAttemptedPids = [];
    private readonly HashSet<int> _autoInjectInFlightPids = [];
    private readonly Dictionary<string, TextBlock> _launchTargetStatusTexts = [];
    private readonly Dictionary<string, CheckBox> _launchTargetChecks = [];
    private readonly Dictionary<string, ComboBox> _launchTargetCharacterDropdowns = [];
    private readonly Dictionary<string, ComboBox> _launchTargetInjectionDropdowns = [];
    private readonly Dictionary<string, DateTime> _autoLaunchAttemptTimesUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _autoLaunchServerDownNoticeTimesUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _autoLaunchNeedsPasswordNoticeTimesUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _autoLaunchNoServerNoticeTimesUtc = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _extraArgumentsNoticeUtc = DateTime.MinValue;
    private readonly Dictionary<string, ServerAvailabilityState> _serverStatuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PluginDefinition> _plugins =
    [
        new PluginDefinition
        {
            Id = "rynthcore-engine",
            Name = "RynthCore Engine",
            Summary = "Injects the in-process runtime and overlay host into acclient.exe.",
            StatusText = "Implemented now",
            RuntimeImplemented = true
        }
    ];
    private readonly Dictionary<string, CheckBox> _pluginChecks = [];
    private readonly DispatcherTimer _sessionTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _serverStatusTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    // Launchers stay open for days on multi-client setups; re-check the update feed now and then.
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    private readonly RynthUpdater _updater = new();
    private RynthUpdater.CheckResult? _updateCheck;
    private bool _updateBusy;
    private AppSettings _settings = new();
    private bool _operationBusy;
    private bool _autoLaunchPassInFlight;
    private bool _serverStatusRefreshInFlight;
    private bool _suppressPrimarySelectionChanged;
    private bool _suppressLaunchTargetCharacterSelectionChanged;
    // Diagnostics: emit "reaper is OFF" to the activity log once per session
    // when MaybeKillStuckClients sees KillStuckClients=false. Lets us
    // definitively rule out the launcher when an AC client vanishes.
    private bool _reaperOffLogged;

    public MainWindow()
    {
        InitializeComponent();
        ActivityList.ItemsSource = _activityItems;
        SessionList.ItemsSource = _sessionItems;
        LoadSettings();
        LoadRuntimeControls();
        InitLoggingControls();
        ApplyInstallerPluginRegistration();
        LoadPluginDllPaths();
        BuildPluginLoadout();
        WireEvents();
        RefreshLists();
        RefreshSummary();
        RefreshSessionState();
        _sessionTimer.Tick += (_, _) => RefreshSessionState();
        _sessionTimer.Start();
        _serverStatusTimer.Tick += async (_, _) => await RefreshServerStatusesAsync();
        _serverStatusTimer.Start();
        Closing += (_, _) => SaveWindowLayout();
        ShowVersion();
        AppendActivity("Avalonia launcher preview ready.");
        // One-line host/runtime sidecar check (full detail in Desktop RynthCore-Launcher.log).
        AppendActivity(LauncherHostDiagnostics.BuildActivitySummaryLine());
        _ = RefreshServerStatusesAsync();
        _updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync();
        _updateTimer.Start();
        _ = CheckForUpdatesAsync();
        WireLauncherScroll();
        Opened += async (_, _) =>
        {
            await AskAboutUsageStatsOnceAsync();
            await CleanUpDecalBridgeIfUnusedAsync(atStartup: true);
            await CheckFolderAccessAtStartupAsync();   // MainWindow.FolderAccess.cs: can the game client save?
        };
    }

    // ── The Launcher tab keeps the player's scroll position ───────────────────
    // Player report R-20261021-183803-97c0: scrolled down, the tab jumped back to the top. It was
    // reset on every SelectionChanged that reached the tab control, and that event bubbles: every
    // ComboBox and ListBox inside the tabs raised it there too, and the launch targets' character
    // dropdowns are refilled every 2 s (RefreshLaunchTargetCharacterSelections), the server list
    // every 15 s, the lists whenever a profile changes. Now only the tab control's own selection
    // counts, and selecting the tab puts the player back where they were.
    // The reset worked around an earlier layout bug (a * row in the profile cards under this
    // scroll left Updates and Launch Targets clipped above the viewport). That is fixed in the
    // layout itself (all-Auto rows, MaxHeight on the lists, MainWindow.axaml), the ScrollViewer
    // clamps a restored offset to the content's current height, and the Plugins tab's update
    // button still opens this tab at the top, where the update card is.
    private global::Avalonia.Vector _launcherScrollOffset;
    private bool _launcherScrollRestorePending;

    private void WireLauncherScroll()
    {
        // ScrollChanged bubbles too (the Launch Targets list and the profile lists scroll inside).
        LauncherScroll.ScrollChanged += (_, e) =>
        {
            if (ReferenceEquals(e.Source, LauncherScroll) && MainTabs.SelectedIndex == 0 && !_launcherScrollRestorePending)
                _launcherScrollOffset = LauncherScroll.Offset;
        };
        MainTabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.Source, MainTabs) && MainTabs.SelectedIndex == 0)
                KeepLauncherScroll();
        };
    }

    /// <summary>
    /// Puts the Launcher tab back where the player had it once layout has run: called when the tab
    /// is selected again and before its content is refilled (lists, launch targets, update card).
    /// Runs before input, so it never undoes the player's own scrolling.
    /// </summary>
    private void KeepLauncherScroll()
    {
        if (_launcherScrollRestorePending)
            return;
        _launcherScrollRestorePending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _launcherScrollRestorePending = false;
            try
            {
                if (MainTabs.SelectedIndex == 0 && LauncherScroll.Offset != _launcherScrollOffset)
                    LauncherScroll.Offset = _launcherScrollOffset;   // clamped to the content by the ScrollViewer
            }
            catch { /* window closing */ }
        }, DispatcherPriority.Loaded);
    }

    /// <summary>The Plugins tab's "show the update card": the Launcher tab, at the top.</summary>
    private void ShowLauncherTabAtTop()
    {
        _launcherScrollOffset = default;
        MainTabs.SelectedIndex = 0;
        KeepLauncherScroll();   // also when the tab was already showing
    }

    private void WireEvents()
    {
        AddServerButton.Click += async (_, _) => await AddServerAsync();
        EditServerButton.Click += async (_, _) => await EditServerAsync();
        DeleteServerButton.Click += async (_, _) => await DeleteServerAsync();

        AddAccountButton.Click += async (_, _) => await AddAccountAsync();
        EditAccountButton.Click += async (_, _) => await EditAccountAsync();
        DeleteAccountButton.Click += (_, _) => DeleteAccount();

        SelectAllTargetsButton.Click += (_, _) => SetAllLaunchTargets(true);
        ClearTargetsButton.Click += (_, _) => SetAllLaunchTargets(false);
        MarkSelectedTargetButton.Click += (_, _) => AddPrimaryAccountToLaunchTargets();
        LaunchPrimaryButton.Click += async (_, _) => await LaunchPrimaryAsync();
        LaunchCheckedTargetsButton.Click += async (_, _) => await LaunchCheckedTargetsAsync();
        HeaderLaunchButton.Click += async (_, _) => await LaunchCheckedTargetsAsync();
        AutoLaunchHeaderCheckBox.IsCheckedChanged += (_, _) => SaveAutoLaunchPreference();
        SavePathsButton.Click += (_, _) => SaveRuntimePaths();
        StopHeadlessButton.Click += (_, _) => StopHeadlessClients();
        SaveBehaviorButton.Click += (_, _) => SaveLaunchBehavior();
        InjectRunningAcButton.Click += async (_, _) => await InjectRunningAcAsync();
        RefreshSessionsButton.Click += (_, _) => RefreshSessionState();
        ResetOverlayBarButton.Click += (_, _) => ResetOverlayBar();
        CheckDecalBridgeButton.Click += async (_, _) => await CheckDecalBridgeAsync();
        RepairDecalButton.Click += async (_, _) => await RepairDecalAsync();
        FixFolderPermissionsButton.Click += async (_, _) => await FixFolderPermissionsAsync();
        AddPluginDllButton.Click += async (_, _) => await AddPluginDllAsync();
        CheckUpdatesButton.Click += async (_, _) => await CheckForUpdatesAsync();
        RollbackButton.Click += async (_, _) => await RollBackAsync();
        UpdateCardActionButton.Click += async (_, _) => await OnUpdateCardActionAsync();
        CancelDownloadButton.Click += (_, _) => CancelDownload();
        UpdateCardNewPluginsButton.Click += (_, _) => ShowAvailablePlugins();
        ShowUpdateCardButton.Click += (_, _) => ShowLauncherTabAtTop();
        AutoUpdatePluginsCheckBox.IsChecked = _settings.AutoUpdatePlugins;
        AutoUpdatePluginsCheckBox.IsCheckedChanged += (_, _) =>
        {
            _settings.AutoUpdatePlugins = AutoUpdatePluginsCheckBox.IsChecked == true;
            SaveSettings();
        };
        UsageStatsWhatText.Text = UsageStatsDialog.WhatIsSent;
        UsageStatsNeverText.Text = UsageStatsDialog.NeverSent;
        UsageStatsStopText.Text = UsageStatsDialog.HowToStop;
        UsageStatsCheckBox.IsChecked = _settings.UsageStatsEnabled;
        UsageStatsCheckBox.IsCheckedChanged += (_, _) => SetUsageStats(UsageStatsCheckBox.IsChecked == true);
        ShowUsageReportButton.Click += async (_, _) => await ShowUsageReportAsync();

        ServerProfilesList.SelectionChanged += (_, _) => OnPrimarySelectionChanged();
        AccountProfilesList.SelectionChanged += (_, _) => OnPrimarySelectionChanged();
    }

    private void LoadSettings()
    {
        _settings = AppSettingsStore.Load();
        if (AppSettingsStore.LastLoadDiagnostic is string loadDiag)
        {
            LauncherDiag.Info("SETTINGS: " + loadDiag);
            try { AppendActivity("Settings: " + loadDiag); } catch { }
        }
        if (AppSettingsStore.LastMigrationNote is string migration)
        {
            LauncherDiag.Info("SETTINGS: " + migration);
            try { AppendActivity("Settings: " + migration); } catch { }
        }
        // Password notes carry counts and account names only, never a password.
        if (AppSettingsStore.LastPasswordMigrationNote is string passwordMigration)
        {
            LauncherDiag.Info("SETTINGS: " + passwordMigration);
            try { AppendActivity("Settings: " + passwordMigration); } catch { }
        }
        if (AppSettingsStore.LastPasswordProblemNote is string passwordProblem)
        {
            LauncherDiag.Info("SETTINGS: " + passwordProblem);
            try { AppendActivity("Settings: " + passwordProblem); } catch { }
        }
        _settings.ServerProfiles ??= [];
        _settings.AccountProfiles ??= [];
        _settings.CheckedLaunchAccountProfileIds ??= [];
        _settings.EnabledPluginIds ??= [];
        if (_settings.EnabledPluginIds.Count == 0)
            _settings.EnabledPluginIds.Add("rynthcore-engine");

        if (_settings.WindowWidth.HasValue) Width = _settings.WindowWidth.Value;
        if (_settings.WindowHeight.HasValue) Height = _settings.WindowHeight.Value;
        if (_settings.WindowX.HasValue && _settings.WindowY.HasValue)
        {
            int wx = (int)_settings.WindowX.Value;
            int wy = (int)_settings.WindowY.Value;
            // Guard against a saved minimized / off-screen position. Windows reports
            // -32000,-32000 for a minimized window, and a stale multi-monitor layout
            // can leave the saved point off every screen; restoring either opens the
            // launcher invisibly (the "launcher won't show" symptom). Honor only a
            // sane on-screen point — otherwise fall back to the default centered
            // location so the window is always reachable.
            if (wx > -2000 && wy > -2000 && wx < 30000 && wy < 30000)
            {
                Position = new PixelPoint(wx, wy);
                WindowStartupLocation = WindowStartupLocation.Manual;
            }
            else
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
        }
    }

    private void SaveSettings()
    {
        // Save is called from dozens of UI handlers (checkbox toggles, window
        // moves, the 2s tick). An IOException here (file locked by a second
        // launcher instance, AV scan, disk full) must not take the launcher
        // down with an unhandled exception — log it and keep running on the
        // in-memory settings; the next successful save persists everything.
        try
        {
            AppSettingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            LauncherDiag.Info($"SETTINGS: save failed ({ex.GetType().Name}: {ex.Message})");
            try { AppendActivity($"Settings save failed: {ex.Message}"); } catch { }
        }
    }

    private void SaveWindowLayout()
    {
        // Never persist a minimized / off-screen geometry. Windows reports
        // Position -32000,-32000 while a window is minimized; saving that makes
        // the next launch restore an invisible window (the launcher "won't show
        // open"). Skip the capture entirely in that state and keep the last good
        // on-screen layout. Maximized still persists (its bounds restore fine).
        if (WindowState == WindowState.Minimized)
            return;
        if (Position.X <= -2000 || Position.Y <= -2000)
            return;
        _settings.WindowWidth = Width;
        _settings.WindowHeight = Height;
        _settings.WindowX = Position.X;
        _settings.WindowY = Position.Y;
        SaveSettings();
    }

    private void LoadRuntimeControls()
    {
        AcClientPathTextBox.Text = _settings.AcClientPath;
        EnginePathTextBox.Text = !string.IsNullOrWhiteSpace(_settings.EnginePath)
            ? _settings.EnginePath
            : _injector.TryResolveEnginePath(null) ?? string.Empty;
        LaunchArgumentsTextBox.Text = _settings.LaunchArguments;
        HeadlessExePathTextBox.Text = _settings.HeadlessExePath;
        HeadlessPluginPathsTextBox.Text = string.Join(Environment.NewLine, _settings.HeadlessPluginPaths ?? []);
        // A whole-line override saved by an older launcher (with -a/-v/-h) would now ride along
        // on every account: launches refuse it, so say so up front.
        if (AcLaunchArgumentBuilder.ExtraArgumentsProblem(_settings.LaunchArguments, out _) is string extraProblem)
        {
            try { AppendActivity($"Runtime > Extra Launch Arguments: {extraProblem} Launches are blocked until it is fixed."); } catch { }
        }

        AllowMultipleClientsCheckBox.IsChecked = _settings.AllowMultipleClients;
        SkipIntroVideosCheckBox.IsChecked = _settings.SkipIntroVideos;
        SkipLoginLogosCheckBox.IsChecked = _settings.SkipLoginLogos;
        AutoLaunchHeaderCheckBox.IsChecked = _settings.AutoLaunch;
        AutoInjectAfterLaunchCheckBox.IsChecked = _settings.AutoInjectAfterLaunch;
        WatchForAcStartCheckBox.IsChecked = _settings.WatchForAcStart;
        InjectAllRunningAcCheckBox.IsChecked = _settings.InjectAllRunningClients;
        SetLoggingLevelSelection(_settings.LoggingLevel);
        OverrideWindowTitleCheckBox.IsChecked = _settings.OverrideWindowTitle;
        LaunchStaggerMsBox.Value = _settings.LaunchStaggerMs;
        CrashRelaunchLimitBox.Value = _settings.CrashRelaunchLimitInWindow;
        CrashRelaunchWindowBox.Value = _settings.CrashRelaunchWindowMinutes;
        KillStuckClientsCheckBox.IsChecked = _settings.KillStuckClients;
        StuckClientTimeoutBox.Value = _settings.StuckClientTimeoutSeconds;

        // Engine flags live in %APPDATA%\RynthCore\engine.json, not the
        // launcher's own settings. The engine reads them on init; the launcher
        // just provides a UI window into the same file.
        // (No "Use DComp overlay" box any more: the DComp overlay is an Avalonia app, and the
        // shipped CoreCLR engine runs without Avalonia (EngineSettings.EnableAvaloniaOverlay is
        // always false there), so EnableDcompOverlay changed nothing. A saved key is left alone.)
        // The engine treats a missing key as true.
        EnableImGuiBackendCheckBox.IsChecked = EngineJsonStore.GetBool("EnableImGuiBackend") ?? true;
        EnableImGuiBackendCheckBox.IsCheckedChanged += OnEnableImGuiBackendChanged;
    }

    private void OnEnableImGuiBackendChanged(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        bool value = EnableImGuiBackendCheckBox.IsChecked == true;
        try
        {
            EngineJsonStore.SetBool("EnableImGuiBackend", value);
            AppendActivity($"Engine setting saved: EnableImGuiBackend={value}. Takes effect on next AC client launch or engine reload.");
        }
        catch (Exception ex)
        {
            AppendActivity($"Failed to save EnableImGuiBackend to engine.json: {ex.Message}");
        }
    }

    private void BuildPluginLoadout()
    {
        PluginLoadoutPanel.Children.Clear();
        _pluginChecks.Clear();

        foreach (PluginDefinition plugin in _plugins)
        {
            var checkBox = new CheckBox
            {
                Content = plugin.Name,
                IsChecked = _settings.EnabledPluginIds.Contains(plugin.Id, StringComparer.OrdinalIgnoreCase)
            };
            checkBox.IsCheckedChanged += (_, _) => OnPluginSelectionChanged(plugin, checkBox);
            _pluginChecks[plugin.Id] = checkBox;

            PluginLoadoutPanel.Children.Add(BuildPluginCard(checkBox, plugin.Summary, null, null));
        }

        var disabledSet = new HashSet<string>(
            _settings.DisabledPluginDllPaths ?? new List<string>(),
            StringComparer.OrdinalIgnoreCase);

        ServerPluginLoadoutPanel.Children.Clear();
        foreach (string dllPath in _pluginDllPaths)
        {
            string capturedPath = dllPath;
            // "RynthAi", what it does and its folder - not "RynthCore.Plugin.RynthAi" over the DLL's full path.
            string pluginName = PluginNames.Friendly(dllPath);
            string pluginVersion = BuildVersion.OfFile(dllPath);
            KnownServer? server = PluginNames.ServerOf(dllPath);
            string about = DescribeInstalled(dllPath, server);

            var checkBox = new CheckBox
            {
                Content = pluginVersion.Length > 0 ? $"{pluginName}   {pluginVersion}" : pluginName,
                IsChecked = !disabledSet.Contains(capturedPath)
            };
            checkBox.IsCheckedChanged += (_, _) => OnUserPluginEnabledChanged(capturedPath, checkBox);

            StackPanel target = server != null ? ServerPluginLoadoutPanel : PluginLoadoutPanel;
            target.Children.Add(BuildPluginCard(checkBox, about, Path.GetDirectoryName(capturedPath) ?? capturedPath, () =>
            {
                // Out of the list (and out of the disabled list, so adding it back starts it
                // enabled). The files stay where they are.
                if (!PluginListEdits.Remove(_pluginDllPaths, _settings.DisabledPluginDllPaths, capturedPath)) return;
                SavePluginDllPaths();
                BuildPluginLoadout();
                bool fromFeed = _updateCheck?.Manifest.Plugins.Any(e =>
                    string.Equals(e.File, Path.GetFileName(capturedPath), StringComparison.OrdinalIgnoreCase)) == true;
                AppendActivity($"Removed plugin: {Path.GetFileName(capturedPath)}. Its files stay in {Path.GetDirectoryName(capturedPath)}." +
                               (fromFeed ? " It is back under Available plugins if you want it again." : ""));
                RefreshUpdateCard();
            }));
        }
        ServerPluginsEmptyText.IsVisible = ServerPluginLoadoutPanel.Children.Count == 0;

        BuildAvailablePlugins();
    }

    /// <summary>
    /// One line about an installed plugin: the update feed's description (or the launcher's own line) for a
    /// RynthSuite plugin; for a server plugin its server and what that server is; otherwise nothing.
    /// </summary>
    private string DescribeInstalled(string dllPath, KnownServer? server)
    {
        RynthUpdater.PluginEntry? e = _updateCheck?.Manifest.Plugins.FirstOrDefault(p =>
            string.Equals(p.File, Path.GetFileName(dllPath), StringComparison.OrdinalIgnoreCase));
        string feed = e != null ? RynthUpdater.DescribePlugin(e) : RynthUpdater.DescribeByName(PluginNames.Friendly(dllPath));
        if (server == null) return feed;
        string forServer = $"For {server.Name} ({server.Address}): {server.Note}";
        return feed.Length > 0 && !feed.Contains(server.Name, StringComparison.OrdinalIgnoreCase) ? $"{forServer} {feed}" : forServer;
    }

    // ── Available plugins (feed plugins not in the list) ──────────────────────

    private List<RynthUpdater.AvailablePlugin> _available = new();

    private void BuildAvailablePlugins()
    {
        AvailablePluginsPanel.Children.Clear();
        AvailableServerPluginsPanel.Children.Clear();
        _available = _updateCheck == null ? new() : _updater.GetAvailable(_updateCheck, _pluginDllPaths);
        if (_updateCheck == null)
        {
            string none = _updater.IsConfigured
                ? "Plugins you can add appear here after the first update check."
                : "Updates aren't set up in this build, so there is no plugin list to install from.";
            AvailablePluginsHint.Text = none;
            AvailableServerPluginsHint.Text = none.Replace("Plugins you can add", "Server plugins you can add");
            return;
        }
        // Server plugins (one game server each) go on their own tab; they muddied the regular list.
        var regular = _available.Where(a => !PluginNames.IsServerPlugin(a.Entry.Name)).ToList();
        var forServers = _available.Where(a => PluginNames.IsServerPlugin(a.Entry.Name))
                                   .OrderBy(a => PluginNames.ServerOf(a.Entry.Name)?.Name, StringComparer.OrdinalIgnoreCase).ToList();
        string suite = _available.Count > 0
            ? Path.GetDirectoryName(Path.GetDirectoryName(_available[0].Path)) ?? RynthUpdater.DefaultSuiteDir
            : RynthUpdater.DefaultSuiteDir;

        AvailablePluginsHint.Text = regular.Count == 0
            ? "You have every RynthSuite plugin."
            : $"RynthSuite plugins you don't have. Install downloads one into {suite} and adds it to the list above; it loads the next time AC starts.";
        foreach (RynthUpdater.AvailablePlugin a in regular)
            AvailablePluginsPanel.Children.Add(BuildAvailableCard(a));

        AvailableServerPluginsHint.Text = forServers.Count == 0
            ? "You have every server plugin."
            : $"Add the one for the server you play on. Install downloads it into {suite}; RynthCore loads it only when you connect to that server.";
        foreach (RynthUpdater.AvailablePlugin a in forServers)
            AvailableServerPluginsPanel.Children.Add(BuildAvailableCard(a));
    }

    private Border BuildAvailableCard(RynthUpdater.AvailablePlugin a)
    {
        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        name.Children.Add(new TextBlock { Text = a.Entry.Name, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#EAF0F4"), VerticalAlignment = VerticalAlignment.Center });
        if (a.IsNew)
            name.Children.Add(new Border
            {
                Background = Brush.Parse("#F2C14E"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "New", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#0B1218") },
            });

        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(name);
        if (PluginNames.ServerOf(a.Entry.Name) is { } server)
            stack.Children.Add(new TextBlock { Text = $"For {server.Name} ({server.Address}): {server.Note}", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9AA8B3") });
        if (a.Description.Length > 0)
            stack.Children.Add(new TextBlock { Text = a.Description, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9AA8B3") });
        stack.Children.Add(new TextBlock
        {
            Text = $"Version {a.Version} · {DownloadProgress.FormatMb(a.Entry.Size)}",
            FontSize = 11,
            Foreground = Brush.Parse("#6F7F8A"),
        });

        Control install;
        if (!a.CanInstall)
        {
            // Its manifest needs a newer engine than this RynthCore: say so instead of offering it.
            install = new TextBlock
            {
                Text = a.Blocker,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                Foreground = Brush.Parse("#F2C14E"),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 200,
            };
            ToolTip.SetTip(install, $"This {a.Entry.Name} build needs a newer RynthCore than the one installed. Update RynthCore, then install it.");
        }
        else
        {
            var button = new Button
            {
                Content = "Install",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                IsEnabled = !_updateBusy,
            };
            ToolTip.SetTip(button, $"Download {a.Entry.Name} (checked against the signed update feed) into {Path.GetDirectoryName(a.Path)} and add it to your plugins.");
            button.Click += async (_, _) => await InstallAvailablePluginAsync(a);
            install = button;
        }

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*, Auto") };
        Grid.SetColumn(stack, 0);
        Grid.SetColumn(install, 1);
        row.Children.Add(stack);
        row.Children.Add(install);
        return new Border
        {
            Background = Brush.Parse("#0F161D"),
            BorderBrush = Brush.Parse(a.IsNew ? "#5A4A1E" : "#243742"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12),
            Child = row,
        };
    }

    private async Task InstallAvailablePluginAsync(RynthUpdater.AvailablePlugin a)
    {
        if (_updateBusy || _updateCheck is not { } check) return;
        _updateBusy = true;
        BuildAvailablePlugins();   // greys the Install buttons
        bool installed = false;
        try
        {
            RynthUpdater.PluginInstall r = await RunDownloadAsync((progress, ct) =>
                _updater.InstallPluginAsync(check, a.Entry.Name, _pluginDllPaths.ToList(), progress, ct));
            if (PluginListEdits.Add(_pluginDllPaths, r.Path)) SavePluginDllPaths();
            installed = true;
            _pluginsUpdatedThisSession.Add($"{r.Entry.Name} (new)");
            string what = r.Description.Length > 0 ? $" ({r.Description.TrimEnd('.')})" : "";
            AppendActivity($"Plugin added: {r.Entry.Name}{what}, {r.Path}{(r.Downloaded ? "" : " (the copy already there)")}. It loads the next time AC starts.");
        }
        catch (OperationCanceledException)
        {
            AppendActivity($"{a.Entry.Name}: download cancelled. Nothing was installed.");
        }
        catch (Exception ex)
        {
            AppendActivity($"Could not install {a.Entry.Name}: {ex.Message}");
        }
        finally
        {
            _updateBusy = false;
            BuildPluginLoadout();
            RefreshUpdateCard();
        }
        // A fresh check shows the new plugin's status (and, for RynthNav, offers its map).
        if (installed) await CheckForUpdatesAsync();
    }

    private void ShowAvailablePlugins()
    {
        MainTabs.SelectedItem = PluginsTab;
        Dispatcher.UIThread.Post(() => AvailablePluginsBorder.BringIntoView(), DispatcherPriority.Background);
    }

    private static Border BuildPluginCard(CheckBox checkBox, string description, string? location, Action? onRemove)
    {
        var host = new Border
        {
            Background = Brush.Parse("#0F161D"),
            BorderBrush = Brush.Parse("#243742"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12)
        };

        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(checkBox);
        if (description.Length > 0)
            stack.Children.Add(new TextBlock
            {
                Text = description,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush.Parse("#9AA8B3")
            });
        if (!string.IsNullOrEmpty(location))
            stack.Children.Add(new TextBlock
            {
                Text = location,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush.Parse("#6F7F8A")
            });

        if (onRemove == null)
        {
            host.Child = stack;
            return host;
        }

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*, Auto") };
        var removeButton = new Button
        {
            Content = "\u2715",
            FontSize = 14,
            Padding = new Thickness(6, 2),
            VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent,
            Foreground = Brush.Parse("#9AA8B3"),
            BorderThickness = new Thickness(0)
        };
        ToolTip.SetTip(removeButton, "Take this plugin out of the list. Its files stay on disk.");
        removeButton.Click += (_, _) => onRemove();

        Grid.SetColumn(stack, 0);
        Grid.SetColumn(removeButton, 1);
        row.Children.Add(stack);
        row.Children.Add(removeButton);

        host.Child = row;
        return host;
    }

    private void RefreshLists()
    {
        KeepLauncherScroll();
        _suppressPrimarySelectionChanged = true;
        try
        {
            ServerProfilesList.ItemsSource = BuildServerProfileDisplayNames();
            AccountProfilesList.ItemsSource = BuildAccountProfileDisplayNames();

            ServerProfilesList.SelectedIndex = FindServerIndex(_settings.SelectedServerProfileId);
            AccountProfilesList.SelectedIndex = FindAccountIndex(_settings.SelectedAccountProfileId);
        }
        finally
        {
            _suppressPrimarySelectionChanged = false;
        }

        RefreshLaunchTargets();
    }

    private void RefreshSummary()
    {
        LaunchAccountProfile? account = GetSelectedAccount();
        // The preview shows what a launch does: the account on its own server, not on the one
        // selected in the server list (the launch never uses that one for it).
        LaunchServerProfile? server = account != null ? ResolveServerForAccount(account) : null;
        int checkedTargets = _settings.CheckedLaunchAccountProfileIds?.Count ?? 0;
        if (account != null && server == null)
        {
            string noServer = $"{account.DisplayName}: {AccountServerRules.NoServerMessage(account, _settings.ServerProfiles.Count > 0)}";
            SummaryText.Text = noServer;
            LaunchProfileStatusText.Text = noServer;
            LaunchPreviewText.Text = noServer;
            return;
        }
        if (server == null || account == null)
        {
            SummaryText.Text = checkedTargets == 0
                ? "Choose a server and account profile to preview the launch pair."
                : $"Launch targets remembered: {checkedTargets}. Choose a primary server and account profile to preview the pair.";
            LaunchProfileStatusText.Text = SummaryText.Text;
            LaunchPreviewText.Text = "Choose a valid server and account profile to preview the launch command shape.";
            return;
        }

        try
        {
            string pairSummary = BuildPrimarySelectionSummary(account, server);
            SummaryText.Text = $"{pairSummary}  |  {_launchArgumentBuilder.Describe(server, account)}  |  Checked launch targets: {checkedTargets}";
            LaunchProfileStatusText.Text = pairSummary;
            LaunchPreviewText.Text = BuildMaskedLaunchPreview(server, account);
        }
        catch (Exception ex)
        {
            SummaryText.Text = ex.Message;
            LaunchProfileStatusText.Text = ex.Message;
            LaunchPreviewText.Text = ex.Message;
        }
    }

    private LaunchServerProfile? GetSelectedServer()
    {
        int index = ServerProfilesList.SelectedIndex;
        return index >= 0 && index < _settings.ServerProfiles.Count ? _settings.ServerProfiles[index] : null;
    }

    private LaunchAccountProfile? GetSelectedAccount()
    {
        int index = AccountProfilesList.SelectedIndex;
        return index >= 0 && index < _settings.AccountProfiles.Count ? _settings.AccountProfiles[index] : null;
    }

    private async System.Threading.Tasks.Task AddServerAsync()
    {
        var draft = new RynthCore.App.LaunchServerProfile();
        var dialog = new ServerProfileDialog(draft);
        bool? accepted = await dialog.ShowDialog<bool?>(this);
        if (accepted != true)
            return;

        _settings.ServerProfiles.Add(draft);
        _settings.SelectedServerProfileId = draft.Id;
        SaveSettings();
        RefreshLists();
        ServerProfilesList.SelectedIndex = _settings.ServerProfiles.Count - 1;
        RefreshSummary();
        AppendActivity($"Server added: {draft.DisplayName}");
        await RefreshServerStatusesAsync();
    }

    private async System.Threading.Tasks.Task EditServerAsync()
    {
        LaunchServerProfile? selected = GetSelectedServer();
        if (selected == null)
            return;

        LaunchServerProfile draft = selected.Clone();
        var dialog = new ServerProfileDialog(draft);
        bool? accepted = await dialog.ShowDialog<bool?>(this);
        if (accepted != true)
            return;

        selected.CopyFrom(draft);
        _settings.SelectedServerProfileId = selected.Id;
        SaveSettings();
        int index = ServerProfilesList.SelectedIndex;
        RefreshLists();
        ServerProfilesList.SelectedIndex = index;
        RefreshSummary();
        AppendActivity($"Server updated: {selected.DisplayName}");
        await RefreshServerStatusesAsync();
    }

    private async Task DeleteServerAsync()
    {
        int index = ServerProfilesList.SelectedIndex;
        if (index < 0 || index >= _settings.ServerProfiles.Count)
            return;

        // Refused while accounts log on to it: they are never moved to another server (their
        // passwords belong to this one). AccountServerRules says why refusing beats clearing.
        LaunchServerProfile toRemove = _settings.ServerProfiles[index];
        if (AccountServerRules.DeleteBlocker(toRemove, _settings.AccountProfiles) is string blocker)
        {
            AppendActivity($"Server not deleted: {toRemove.DisplayName} still has " +
                $"{AccountServerRules.AccountsOn(toRemove, _settings.AccountProfiles).Count} account(s). Move or delete them first.");
            await DecalBridgeDialog.InfoAsync(this, "Server still in use", blocker);
            return;
        }

        string removedServerId = toRemove.Id;
        bool removedSelected = string.Equals(removedServerId, _settings.SelectedServerProfileId, StringComparison.OrdinalIgnoreCase);
        _settings.ServerProfiles.RemoveAt(index);
        if (removedSelected)
            _settings.SelectedServerProfileId = _settings.ServerProfiles.FirstOrDefault()?.Id ?? string.Empty;
        _serverStatuses.Remove(removedServerId);
        SaveSettings();
        RefreshLists();
        ServerProfilesList.SelectedIndex = Math.Min(index, _settings.ServerProfiles.Count - 1);
        RefreshSummary();
        AppendActivity($"Server removed: {toRemove.DisplayName}");
    }

    private async System.Threading.Tasks.Task AddAccountAsync()
    {
        // Defaults to the server selected in the list (a new server's first account), not the
        // first profile, which RefreshLaunchTargets would otherwise assign.
        var draft = new RynthCore.App.LaunchAccountProfile { ServerId = GetSelectedServer()?.Id ?? string.Empty };
        var dialog = new AccountProfileDialog(draft, _settings.ServerProfiles, GetDetectedCharacters);
        bool? accepted = await dialog.ShowDialog<bool?>(this);
        if (accepted != true)
            return;

        _settings.AccountProfiles.Add(draft);
        _settings.SelectedAccountProfileId = draft.Id;
        SaveSettings();
        RefreshLists();
        AccountProfilesList.SelectedIndex = _settings.AccountProfiles.Count - 1;
        RefreshSummary();
        AppendActivity($"Account added: {draft.DisplayName}");
    }

    private async System.Threading.Tasks.Task EditAccountAsync()
    {
        LaunchAccountProfile? selected = GetSelectedAccount();
        if (selected == null)
            return;

        LaunchAccountProfile draft = selected.Clone();
        var dialog = new AccountProfileDialog(draft, _settings.ServerProfiles, GetDetectedCharacters);
        bool? accepted = await dialog.ShowDialog<bool?>(this);
        if (accepted != true)
            return;

        selected.CopyFrom(draft);
        _settings.SelectedAccountProfileId = selected.Id;
        SaveSettings();
        int index = AccountProfilesList.SelectedIndex;
        RefreshLists();
        AccountProfilesList.SelectedIndex = index;
        RefreshSummary();
        AppendActivity($"Account updated: {selected.DisplayName}");
    }

    private void DeleteAccount()
    {
        int index = AccountProfilesList.SelectedIndex;
        if (index < 0 || index >= _settings.AccountProfiles.Count)
            return;

        LaunchAccountProfile removed = _settings.AccountProfiles[index];
        string removedId = removed.Id;

        // Delete this profile's character cache (its own server only). The same
        // account name on another server keeps its list; the legacy account-only
        // file goes only when no other profile still uses the name.
        string removedServerName = ResolveServerForAccount(removed)?.Name ?? string.Empty;
        _settings.AccountProfiles.RemoveAt(index);
        bool nameStillUsed = _settings.AccountProfiles.Any(a =>
            string.Equals(a.AccountName?.Trim(), removed.AccountName?.Trim(), StringComparison.OrdinalIgnoreCase));
        DeleteCharacterCacheFiles(removed.AccountName, removedServerName, includeLegacy: !nameStillUsed);

        _settings.CheckedLaunchAccountProfileIds = _settings.CheckedLaunchAccountProfileIds
            .Where(id => !string.Equals(id, removedId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (string.Equals(removedId, _settings.SelectedAccountProfileId, StringComparison.OrdinalIgnoreCase))
            _settings.SelectedAccountProfileId = _settings.AccountProfiles.FirstOrDefault()?.Id ?? string.Empty;
        SaveSettings();
        if (removed.InjectionMode == InjectionMode.DecalBridge)
            _ = CleanUpDecalBridgeIfUnusedAsync(atStartup: false);
        RefreshLists();
        AccountProfilesList.SelectedIndex = Math.Min(index, _settings.AccountProfiles.Count - 1);
        RefreshSummary();
        AppendActivity($"Account removed: {removed.DisplayName}");
    }

    private static void DeleteCharacterCacheFiles(string accountName, string serverName, bool includeLegacy)
    {
        if (string.IsNullOrWhiteSpace(accountName))
            return;

        CharacterCacheStore.DeleteForAccount(accountName, serverName, includeLegacy);
    }

    private List<string> GetDetectedCharacters(string accountName, string serverName = "")
    {
        try
        {
            List<string> cachedCharacters = CharacterCacheStore.Read(accountName, serverName);
            if (cachedCharacters.Count > 0)
                return cachedCharacters;

            // 3. ThwargLauncher character files (populated by ThwargFilter from prior sessions)
            return ReadThwargCharacters(accountName, serverName);
        }
        catch (Exception ex)
        {
            AppendActivity($"Error reading detected characters: {ex.Message}");
        }
        return [];
    }

    /// <summary>
    /// Reads character names from ThwargLauncher's per-account JSON files.
    /// Keys in those files are "{serverName}-{accountName}".
    /// When serverName is provided, only entries whose key starts with that server name are returned.
    /// </summary>
    private static List<string> ReadThwargCharacters(string accountName, string serverName = "")
    {
        try
        {
            string charDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ThwargLauncher", "characters");

            if (!Directory.Exists(charDir))
                return [];

            var results = new List<string>();
            foreach (string file in Directory.GetFiles(charDir, "*.txt"))
            {
                try
                {
                    string json = File.ReadAllText(file);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    foreach (System.Text.Json.JsonProperty entry in doc.RootElement.EnumerateObject())
                    {
                        // Key is "ServerName-AccountName"
                        bool accountMatches = string.IsNullOrWhiteSpace(accountName) ||
                            entry.Name.EndsWith("-" + accountName, StringComparison.OrdinalIgnoreCase);
                        bool serverMatches = string.IsNullOrWhiteSpace(serverName) ||
                            entry.Name.StartsWith(serverName + "-", StringComparison.OrdinalIgnoreCase);

                        if (!accountMatches || !serverMatches)
                            continue;

                        if (!entry.Value.TryGetProperty("CharacterList", out var list))
                            continue;

                        foreach (var charEl in list.EnumerateArray())
                        {
                            if (charEl.TryGetProperty("Name", out var nameEl))
                            {
                                string name = nameEl.GetString() ?? string.Empty;
                                if (!string.IsNullOrEmpty(name) && !results.Contains(name))
                                    results.Add(name);
                            }
                        }
                    }
                }
                catch { /* skip malformed file */ }
            }
            return results;
        }
        catch
        {
            return [];
        }
    }

    private static List<string> ReadCharacterFile(string filePath)
    {
        if (!File.Exists(filePath))
            return [];

        string json = File.ReadAllText(filePath);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("Characters", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
            return arr.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();

        return [];
    }

    private static string SanitizeFileName(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.ToString();
    }

    private void RefreshLaunchTargets()
    {
        KeepLauncherScroll();
        LaunchTargetsPanel.Children.Clear();
        _launchTargetStatusTexts.Clear();
        _launchTargetChecks.Clear();
        _launchTargetCharacterDropdowns.Clear();
        _launchTargetInjectionDropdowns.Clear();
        bool decalAvailable = DecalLocator.IsInstalled();
        _settings.CheckedLaunchAccountProfileIds ??= [];
        bool characterNamesChanged = false;

        for (int accountIndex = 0; accountIndex < _settings.AccountProfiles.Count; accountIndex++)
        {
            LaunchAccountProfile account = _settings.AccountProfiles[accountIndex];

            // The account's own server only. An account whose server is missing used to be bound
            // to the first server here (and saved), which sent its password to that server on
            // the next launch; now it shows "No Server" and can't launch (AccountServerRules).
            LaunchServerProfile? server = ResolveServerForAccount(account);

            var container = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto, *, Auto, Auto, Auto"),
                Margin = new Thickness(0, 0, 0, 1)
            };

            var checkBox = new CheckBox
            {
                Content = BuildLaunchTargetLabel(account, server, accountIndex),
                IsChecked = _settings.CheckedLaunchAccountProfileIds.Contains(account.Id, StringComparer.OrdinalIgnoreCase),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11.5,
                Margin = new Thickness(0)
            };

            checkBox.IsCheckedChanged += (_, _) =>
            {
                UpdateCheckedLaunchTargetsFromUi();
                SaveSettings();
                RefreshSummary();
                AppendActivity($"Launch targets updated: {_settings.CheckedLaunchAccountProfileIds.Count} checked");
            };

            var charDropdown = new ComboBox
            {
                PlaceholderText = "Select Character...",
                HorizontalAlignment = HorizontalAlignment.Right,
                MinWidth = 136,
                FontSize = 11.5,
                Margin = new Thickness(6, 0, 0, 0)
            };

            var statusText = new TextBlock
            {
                Text = "Offline",
                Foreground = Brush.Parse("#73828D"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 6, 0),
                FontSize = 11.5,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            List<string> dropdownItems = GetDetectedCharacters(account.AccountName, server?.Name ?? string.Empty);
            string bestKnownCharacter = GetPreferredCharacterName(account, server, null, null, dropdownItems);
            if (!string.IsNullOrWhiteSpace(bestKnownCharacter) &&
                !string.Equals(account.CharacterName, bestKnownCharacter, StringComparison.OrdinalIgnoreCase))
            {
                account.CharacterName = bestKnownCharacter;
                characterNamesChanged = true;
            }

            // Preserve any already-saved character name even if scan hasn't seen it yet
            if (!string.IsNullOrEmpty(account.CharacterName)
                && account.CharacterName != LaunchAccountProfile.NoneOption
                && !dropdownItems.Contains(account.CharacterName))
            {
                dropdownItems.Insert(0, account.CharacterName);
            }

            // "(None)" at the top — selecting it opts out of auto-login for this account
            dropdownItems.Insert(0, LaunchAccountProfile.NoneOption);

            charDropdown.ItemsSource = dropdownItems;
            if (account.CharacterName == LaunchAccountProfile.NoneOption)
                charDropdown.SelectedItem = LaunchAccountProfile.NoneOption;
            else if (!string.IsNullOrEmpty(account.CharacterName) && dropdownItems.Contains(account.CharacterName))
                charDropdown.SelectedItem = account.CharacterName;

            charDropdown.SelectionChanged += (_, _) =>
            {
                if (_suppressLaunchTargetCharacterSelectionChanged)
                    return;

                if (charDropdown.SelectedItem is string selectedChar)
                {
                    account.CharacterName = selectedChar;
                    SaveSettings();
                    RefreshSummary();
                }
            };

            var loginCmdsButton = BuildLoginCommandsButton(account);
            var injectDropdown = BuildInjectionMethodDropdown(account, decalAvailable);

            Grid.SetColumn(checkBox, 0);
            Grid.SetColumn(statusText, 1);
            Grid.SetColumn(charDropdown, 2);
            Grid.SetColumn(loginCmdsButton, 3);
            Grid.SetColumn(injectDropdown, 4);

            container.Children.Add(checkBox);
            container.Children.Add(statusText);
            container.Children.Add(charDropdown);
            container.Children.Add(loginCmdsButton);
            container.Children.Add(injectDropdown);

            LaunchTargetsPanel.Children.Add(container);
            _launchTargetStatusTexts[account.Id] = statusText;
            _launchTargetChecks[account.Id] = checkBox;
            _launchTargetCharacterDropdowns[account.Id] = charDropdown;
            _launchTargetInjectionDropdowns[account.Id] = injectDropdown;
        }

        if (characterNamesChanged)
            SaveSettings();

        UpdateLaunchTargetStatuses();
    }

    private Button BuildLoginCommandsButton(LaunchAccountProfile account)
    {
        var button = new Button
        {
            Content = "OnLogin",
            FontSize = 11.5,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(8, 2)
        };
        global::Avalonia.Controls.ToolTip.SetTip(button,
            "Per-character chat commands dispatched after login. Examples: /fellow open, /say ready.");

        button.Click += async (_, _) =>
        {
            LaunchServerProfile? server = ResolveServerForAccount(account);
            var dialog = new LoginCommandsDialog(account, server);
            await dialog.ShowDialog(this);
            if (dialog.ChangesSaved)
            {
                SaveSettings();
                AppendActivity($"Login commands updated for {account.DisplayName}.");
            }
        };

        return button;
    }

    private ComboBox BuildInjectionMethodDropdown(LaunchAccountProfile account, bool decalAvailable)
    {
        var dropdown = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 110,
            FontSize = 11.5,
            Margin = new Thickness(6, 0, 0, 0)
        };
        global::Avalonia.Controls.ToolTip.SetTip(dropdown, decalAvailable
            ? "Which modding stack to inject when this account launches."
            : "Decal not detected in registry — install Decal to enable Decal mode.");

        var rynthItem = new ComboBoxItem { Content = "RynthCore", Tag = InjectionMode.RynthCore };
        var decalItem = new ComboBoxItem
        {
            Content = decalAvailable ? "Decal" : "Decal (not installed)",
            Tag = InjectionMode.Decal,
            IsEnabled = decalAvailable
        };

        var bridgeItem = new ComboBoxItem
        {
            Content = decalAvailable ? "Decal + RynthCore (experimental)" : "Decal + RynthCore (experimental, no Decal)",
            Tag = InjectionMode.DecalBridge,
            IsEnabled = decalAvailable
        };
        global::Avalonia.Controls.ToolTip.SetTip(bridgeItem,
            "EXPERIMENTAL. Decal and RynthCore in the same client: Decal's plugins and RynthCore's " +
            "in-game UI run side by side. RynthCore gets chat from Decal through the RynthCore Decal " +
            "bridge, which this launcher adds to Decal's filter list for all users when you pick this mode " +
            "(once; Windows asks for administrator permission). Recovery Actions > Check Decal bridge shows whether it will load. Run one bot per client (VTank or the RynthAi macro, not both). To stop using it, " +
            "set the account back to RynthCore or Decal: once no account uses it, the launcher takes the " +
            "bridge out of Decal's list again (uninstalling RynthCore does too).");

        var headlessItem = new ComboBoxItem { Content = "Headless (experimental)", Tag = InjectionMode.Headless };
        global::Avalonia.Controls.ToolTip.SetTip(headlessItem,
            "EXPERIMENTAL. No game window: the launcher starts rynth-headless.exe, RynthCore's own headless " +
            "client, for this account. ACE servers only (not GDLE). " +
            "Plugins: the 64-bit ones listed under Runtime > Headless client; with that list empty, the RynthAi " +
            "that comes with RynthCore. The plugins in the Plugins tab and Decal don't run in it, and nothing is drawn: " +
            "give RynthAi its orders with the account's on-login commands (for example /ra start). " +
            "It logs in, runs the plugins and shows in the launcher like any other client; closing it from the launcher logs the character out first.");

        dropdown.Items.Add(rynthItem);
        dropdown.Items.Add(decalItem);
        dropdown.Items.Add(bridgeItem);
        dropdown.Items.Add(headlessItem);

        // If the account is set to a Decal mode but Decal isn't installed, fall back to
        // RynthCore in the UI so the user can't accidentally launch into a
        // disabled mode. We don't mutate the saved profile until they click.
        // Headless needs no Decal, so it stays as it is.
        InjectionMode effectiveMode = ((account.InjectionMode is InjectionMode.Decal or InjectionMode.DecalBridge) && !decalAvailable)
            ? InjectionMode.RynthCore
            : account.InjectionMode;
        dropdown.SelectedItem = effectiveMode switch
        {
            InjectionMode.Decal => decalItem,
            InjectionMode.DecalBridge => bridgeItem,
            InjectionMode.Headless => headlessItem,
            _ => rynthItem,
        };

        dropdown.SelectionChanged += (_, _) =>
        {
            if (dropdown.SelectedItem is ComboBoxItem item && item.Tag is InjectionMode mode)
            {
                if (account.InjectionMode == mode)
                    return;
                bool wasBridge = account.InjectionMode == InjectionMode.DecalBridge;
                account.InjectionMode = mode;
                SaveSettings();
                AppendActivity(mode == InjectionMode.DecalBridge
                    ? $"{account.DisplayName}: injection method set to Decal + RynthCore (experimental). " +
                      "Adding the RynthCore Decal bridge to Decal's filter list for all users (once; Windows may ask for administrator permission)."
                    : mode == InjectionMode.Headless
                    ? $"{account.DisplayName}: injection method set to Headless (experimental): launches start rynth-headless.exe " +
                      "with the plugins under Runtime > Headless client (none listed = the RynthAi that comes with RynthCore). ACE servers only, no game window."
                    : $"{account.DisplayName}: injection method set to {mode}.");
                if (mode == InjectionMode.DecalBridge)
                    _ = EnableDecalBridgeAsync();
                else if (wasBridge)
                    _ = CleanUpDecalBridgeIfUnusedAsync(atStartup: false);
            }
        };

        return dropdown;
    }

    // One Decal registry job at a time (start-up cleanup, a mode change, Repair Decal, a launch).
    private readonly SemaphoreSlim _decalBridgeGate = new(1, 1);

    /// <summary>
    /// No account uses "Decal + RynthCore" (at start, after a mode change or a deleted account):
    /// take the RynthCore Decal bridge out of Decal's filter list again (registration is opt-in
    /// and only lasts while an account uses it).
    ///   - Per-user (no prompt): our entry in a per-user copy of Decal's filter list, and a copy
    ///     an earlier RynthCore created (backed up first). Only when there is something to remove.
    ///   - Machine-wide, this install's entry only: asks first, then Windows' administrator prompt
    ///     (unless the launcher runs elevated). Only when the entry exists; at start, never again
    ///     after the player said "leave it" (<see cref="AppSettings.DecalFilterRemovalDeclined"/>).
    /// Running clients keep what they loaded.
    /// </summary>
    private async Task CleanUpDecalBridgeIfUnusedAsync(bool atStartup)
    {
        if (_settings.AccountProfiles.Any(a => a.InjectionMode == InjectionMode.DecalBridge))
            return;
        await _decalBridgeGate.WaitAsync();
        try
        {
            if (_settings.AccountProfiles.Any(a => a.InjectionMode == InjectionMode.DecalBridge))
                return;

            if (DecalBridgeRegistration.PerUserCleanupNeeded())
            {
                bool ok = DecalBridgeRegistration.Unregister(out string report);
                LauncherDiag.Info($"DECALBRIDGE: no Decal + RynthCore account - per-user cleanup: {report}");
                AppendActivity(ok
                    ? $"No account uses Decal + RynthCore - Decal bridge taken out of your per-user Decal settings: {report}"
                    : $"No account uses Decal + RynthCore, but taking the Decal bridge out of your per-user Decal settings did not finish: {report}");
            }

            string dir = DecalBridgeRegistration.DefaultBridgeDirectory;
            if (!DecalBridgeRegistration.MachineEntryIsOurs(dir))
                return;
            if (atStartup && _settings.DecalFilterRemovalDeclined)
            {
                LauncherDiag.Info("DECALBRIDGE: no Decal + RynthCore account, machine-wide entry left (the player chose to keep it); not asking at start.");
                return;
            }

            var outcome = DecalBridgeRegistration.UnregisterMachineWide(out string mReport, dir);
            LauncherDiag.Info($"DECALBRIDGE: no Decal + RynthCore account - unregister machine-wide: {outcome}: {mReport}");
            if (outcome == DecalBridgeRegistration.Outcome.NeedsAdmin)
            {
                bool yes = await DecalBridgeDialog.AskAsync(this,
                    "Decal + RynthCore is not in use",
                    "No account uses Decal + RynthCore any more, but the RynthCore Decal bridge is still in Decal's plugin list for all users on this PC. " +
                    "It does nothing without RynthCore, but you can take it out now. Windows will ask for administrator permission.\n\n" +
                    "If you leave it, the launcher won't ask again when it starts. Uninstalling RynthCore also takes it out.",
                    "Take it out (asks for administrator)", "Leave it");
                if (!yes)
                {
                    RememberDecalFilterRemovalDeclined(true);
                    AppendActivity("No account uses Decal + RynthCore. The Decal bridge stays in Decal's list for all users (idle without RynthCore); the launcher won't ask again at start.");
                    return;
                }
                string? self = Environment.ProcessPath;
                if (self == null)
                {
                    AppendActivity("Decal bridge: can't find the launcher's own exe for the administrator step.");
                    return;
                }
                outcome = DecalBridgeRegistration.RunElevated(self, $"--decal-bridge-unregister-machine \"{dir}\"", out mReport);
                LauncherDiag.Info($"DECALBRIDGE: elevated unregister machine-wide: {outcome}: {mReport}");
                if (outcome == DecalBridgeRegistration.Outcome.NeedsAdmin)
                {
                    RememberDecalFilterRemovalDeclined(true);
                    AppendActivity("Decal bridge: not taken out (cancelled at the Windows administrator prompt). It stays idle without RynthCore; the launcher won't ask again at start.");
                    return;
                }
            }
            if (outcome == DecalBridgeRegistration.Outcome.Registered && !DecalBridgeRegistration.MachineEntryIsOurs(dir))
            {
                RememberDecalFilterRemovalDeclined(false);
                AppendActivity("No account uses Decal + RynthCore any more - the Decal bridge is out of Decal's list for all users.");
            }
            else
            {
                AppendActivity($"No account uses Decal + RynthCore, but taking the Decal bridge out of Decal's list for all users did not finish: {mReport}");
            }
        }
        catch (Exception ex)
        {
            LauncherDiag.Info($"DECALBRIDGE: cleanup failed ({ex.GetType().Name}: {ex.Message})");
            AppendActivity($"Removing the Decal bridge failed: {ex.Message}");
        }
        finally
        {
            _decalBridgeGate.Release();
        }
    }

    private void RememberDecalFilterRemovalDeclined(bool declined)
    {
        if (_settings.DecalFilterRemovalDeclined == declined)
            return;
        _settings.DecalFilterRemovalDeclined = declined;
        SaveSettings();
    }

    /// <summary>An account was just set to Decal + RynthCore: register now (the one prompt, if needed).</summary>
    private async Task EnableDecalBridgeAsync()
    {
        bool ok = await EnsureDecalBridgeAsync(DecalBridgeAcClientPath, interactive: true);
        if (ok)
            AppendActivity("Decal bridge ready: Decal + RynthCore accounts can launch.");
    }

    // ── Decal bridge: register where this PC's client reads, check, explain ─────────────

    private string DecalBridgeAcClientPath =>
        DecalLocator.TryGetDecalAcClientPath() ?? AcClientPathTextBox.Text?.Trim() ?? string.Empty;

    /// <summary>
    /// Registers the Decal bridge in Decal's filter list for all users
    /// (DecalBridgeRegistration.RegisterForClients) and checks the result. When this launcher may
    /// not write that list, asks first and runs the one elevated step
    /// (--decal-bridge-register-machine), then registers again to finish the per-user side. There
    /// is no per-user fallback: declined or failed = Decal + RynthCore can't be used until it is
    /// registered for all users. False = a Decal + RynthCore client would not load the bridge.
    /// Only interactive callers (the player picked the mode, launched, or pressed Repair) prompt;
    /// automatic launches log the reason and stay blocked.
    /// </summary>
    private async Task<bool> EnsureDecalBridgeAsync(string acClientPath, bool interactive)
    {
        await _decalBridgeGate.WaitAsync();
        try { return await EnsureDecalBridgeCoreAsync(acClientPath, interactive); }
        finally { _decalBridgeGate.Release(); }
    }

    private const string DecalBridgeNotRegisteredText =
        "Decal + RynthCore can't be used until the RynthCore Decal bridge is in Decal's list for all users. " +
        "To add it, launch a Decal + RynthCore account (or use Recovery Actions > Check Decal bridge > Repair) and say yes to Windows' administrator prompt.";

    private async Task<bool> EnsureDecalBridgeCoreAsync(string acClientPath, bool interactive)
    {
        string dir = DecalBridgeRegistration.DefaultBridgeDirectory;
        var outcome = DecalBridgeRegistration.RegisterForClients(dir, acClientPath, EngineJsonStore.Path, out string report, out DecalBridgeCheck.Result? check);
        LauncherDiag.Info($"DECALBRIDGE: register for clients: {outcome}: {report}");
        if (check != null)
            LauncherDiag.Info($"DECALBRIDGE: check after register:{Environment.NewLine}{check.ToReport()}");
        if (outcome == DecalBridgeRegistration.Outcome.Registered)
        {
            RememberDecalFilterRemovalDeclined(false);
            AppendActivity(report);
            return true;
        }

        if (outcome == DecalBridgeRegistration.Outcome.NeedsAdmin)
        {
            AppendActivity(report);
            if (!interactive)
            {
                AppendActivity(DecalBridgeNotRegisteredText);
                return false;
            }
            bool yes = await DecalBridgeDialog.AskAsync(this,
                "Decal + RynthCore: one administrator step",
                "Decal + RynthCore needs the RynthCore Decal bridge in Decal's plugin list for all users on this PC, " +
                "so Decal loads it next to your other Decal plugins (it does nothing in clients without RynthCore).\n\n" +
                "Adding it needs administrator permission once; Windows will ask now. Nothing else in Decal changes. " +
                "Switching every account away from Decal + RynthCore, or uninstalling RynthCore, takes the entry out again.",
                "Add it (asks for administrator)", "Cancel");
            if (!yes)
            {
                AppendActivity("Decal bridge: not added (cancelled). " + DecalBridgeNotRegisteredText);
                return false;
            }
            string? self = Environment.ProcessPath;
            if (self == null)
            {
                AppendActivity("Decal bridge: can't find the launcher's own exe for the administrator step. " + DecalBridgeNotRegisteredText);
                return false;
            }
            var elevated = DecalBridgeRegistration.RunElevated(self, $"--decal-bridge-register-machine \"{dir}\"", out string eReport);
            LauncherDiag.Info($"DECALBRIDGE: elevated register: {elevated}: {eReport}");
            if (elevated != DecalBridgeRegistration.Outcome.Registered)
            {
                AppendActivity($"Decal bridge: not added ({eReport}) " + DecalBridgeNotRegisteredText);
                return false;
            }
            AppendActivity("Decal bridge: " + eReport);
            // Again, without rights: the machine-wide entry is there now, so this only finishes the
            // per-user side and checks.
            outcome = DecalBridgeRegistration.RegisterForClients(dir, acClientPath, EngineJsonStore.Path, out report, out check);
            LauncherDiag.Info($"DECALBRIDGE: register after the administrator step: {outcome}: {report}");
            if (check != null)
                LauncherDiag.Info($"DECALBRIDGE: check after the administrator step:{Environment.NewLine}{check.ToReport()}");
            AppendActivity(report);
            if (outcome == DecalBridgeRegistration.Outcome.Registered)
            {
                RememberDecalFilterRemovalDeclined(false);
                return true;
            }
        }
        else
        {
            AppendActivity(report);
        }

        if (interactive && check != null)
            await DecalBridgeDialog.ShowReportAsync(this, check, offerRepair: false);
        return false;
    }

    /// <summary>
    /// Recovery Actions > Repair Decal: Decal is reading an old per-user copy of its settings
    /// (Windows' UAC virtualization; made by an earlier RynthCore or by Decal itself), which hides
    /// plugins and filters installed for all users since. Asks, backs the copy up to a .reg file in
    /// %APPDATA%\RynthCore, removes it, re-registers the bridge for all users when an account uses
    /// Decal + RynthCore, and reports what it did.
    /// </summary>
    private async Task RepairDecalAsync()
    {
        DecalBridgeRegistration.RepairResult result;
        await _decalBridgeGate.WaitAsync();
        try
        {
            List<string> keys = DecalBridgeRegistration.FindVirtualStoreDecalKeys();
            LauncherDiag.Info($"DECALBRIDGE: Repair Decal: per-user copies found: {(keys.Count == 0 ? "none" : string.Join(", ", keys))}");
            if (keys.Count == 0)
            {
                AppendActivity("Repair Decal: nothing to repair - Decal has no per-user copy of its settings and already reads the settings for all users.");
                await DecalBridgeDialog.InfoAsync(this, "Repair Decal",
                    "Nothing to repair: Decal has no per-user copy of its settings, so it already sees every installed plugin.");
                return;
            }
            bool yes = await DecalBridgeDialog.AskAsync(this, "Repair Decal",
                "Decal is reading an old per-user copy of its settings; this removes it so Decal sees all installed plugins. A backup is saved first.\n\n" +
                "Close every Asheron's Call client first. Settings you changed in Decal while that copy was in use go back to the ones for all users " +
                "(the backup has them).\n\nWill be removed:\n" + string.Join("\n", keys),
                "Back up and remove", "Cancel");
            if (!yes)
            {
                AppendActivity("Repair Decal: cancelled; nothing changed.");
                return;
            }
            result = DecalBridgeRegistration.RepairDecal();
            LauncherDiag.Info($"DECALBRIDGE: Repair Decal: ok={result.Ok}: {result.Report}");
            AppendActivity("Repair Decal: " + result.Report);
        }
        catch (Exception ex)
        {
            LauncherDiag.Info($"DECALBRIDGE: Repair Decal failed ({ex.GetType().Name}: {ex.Message})");
            AppendActivity($"Repair Decal failed: {ex.Message}");
            return;
        }
        finally
        {
            _decalBridgeGate.Release();
        }

        string bridgeLine = string.Empty;
        if (result.Removed.Count > 0 && _settings.AccountProfiles.Any(a => a.InjectionMode == InjectionMode.DecalBridge))
        {
            bool ok = await EnsureDecalBridgeAsync(DecalBridgeAcClientPath, interactive: true);
            bridgeLine = ok
                ? "\n\nThe RynthCore Decal bridge is registered for all users again (an account uses Decal + RynthCore)."
                : "\n\nThe RynthCore Decal bridge is NOT registered for all users (see the Activity tab); Decal + RynthCore can't be used until it is.";
        }
        string text = result.Ok
            ? "Done. Removed:\n" + string.Join("\n", result.Removed) + $"\n\nBackup: {result.BackupPath}\n(double-click it to put the keys back)"
            : "Repair Decal did not finish. " + result.Report;
        text += bridgeLine;
        await DecalBridgeDialog.InfoAsync(this, "Repair Decal", text);
    }

    /// <summary>Recovery Actions > Check Decal bridge: the read-only check, with a Repair button when it fails.</summary>
    private async Task CheckDecalBridgeAsync()
    {
        string acClientPath = DecalBridgeAcClientPath;
        DecalBridgeCheck.Result check = RealDecalCheckHost.Check(DecalBridgeRegistration.DefaultBridgeDirectory, acClientPath, EngineJsonStore.Path);
        LauncherDiag.Info($"DECALBRIDGE: check (button):{Environment.NewLine}{check.ToReport()}");
        AppendActivity(check.Summary);
        bool repair = await DecalBridgeDialog.ShowReportAsync(this, check, offerRepair: check.Blocking && check.DecalInstalled);
        if (!repair)
            return;
        bool ok = await EnsureDecalBridgeAsync(acClientPath, interactive: true);
        AppendActivity(ok ? "Decal bridge repaired: a Decal + RynthCore client started now will load it." : "Decal bridge repair did not finish (see above).");
    }

    private void UpdateCheckedLaunchTargetsFromUi()
    {
        _settings.CheckedLaunchAccountProfileIds = LaunchTargetsPanel.Children
            .OfType<Grid>()
            .Select((grid, index) => new { grid, index })
            .Where(x => x.index >= 0 && x.index < _settings.AccountProfiles.Count)
            .Where(x => x.grid.Children.OfType<CheckBox>().FirstOrDefault()?.IsChecked == true)
            .Select(x => _settings.AccountProfiles[x.index].Id)
            .ToList();
    }

    private void SetAllLaunchTargets(bool isChecked)
    {
        foreach (Grid row in LaunchTargetsPanel.Children.OfType<Grid>())
        {
            CheckBox? checkBox = row.Children.OfType<CheckBox>().FirstOrDefault();
            if (checkBox != null)
                checkBox.IsChecked = isChecked;
        }

        UpdateCheckedLaunchTargetsFromUi();
        SaveSettings();
        RefreshSummary();
        AppendActivity(isChecked ? "All launch targets selected." : "Launch targets cleared.");
    }

    private void AddPrimaryAccountToLaunchTargets()
    {
        LaunchAccountProfile? account = GetSelectedAccount();
        if (account == null)
        {
            AppendActivity("Primary target add skipped: no account selected.");
            return;
        }

        // An account that never had a server (added before any server existed) takes the one
        // selected beside it, at the player's click and said in Activity. An account that names
        // a server, even a deleted one, is never changed here (AccountServerRules).
        LaunchServerProfile? selectedServer = GetSelectedServer();
        if (selectedServer != null && string.IsNullOrWhiteSpace(account.ServerId))
        {
            account.ServerId = selectedServer.Id;
            SaveSettings();
            AppendActivity($"{account.DisplayName} had no server; it now logs on to {selectedServer.DisplayName}.");
        }

        _settings.CheckedLaunchAccountProfileIds ??= [];
        if (_settings.CheckedLaunchAccountProfileIds.Contains(account.Id, StringComparer.OrdinalIgnoreCase))
        {
            AppendActivity($"Primary account already targeted: {account.DisplayName}");
            return;
        }

        _settings.CheckedLaunchAccountProfileIds.Add(account.Id);
        SaveSettings();
        RefreshLaunchTargets();
        RefreshSummary();
        AppendActivity($"Primary account added to targets: {account.DisplayName}");
    }

    private void SaveRuntimePaths(bool appendActivity = true)
    {
        _settings.AcClientPath = AcClientPathTextBox.Text?.Trim() ?? string.Empty;
        _settings.EnginePath = EnginePathTextBox.Text?.Trim() ?? string.Empty;
        _settings.LaunchArguments = LaunchArgumentsTextBox.Text?.Trim() ?? string.Empty;
        _settings.HeadlessExePath = HeadlessExePathTextBox.Text?.Trim() ?? string.Empty;
        _settings.HeadlessPluginPaths = ReadHeadlessPluginPathsBox();
        SaveSettings();
        RefreshSummary();
        if (appendActivity)
        {
            AppendActivity("Runtime paths saved.");
            if (AcLaunchArgumentBuilder.ExtraArgumentsProblem(_settings.LaunchArguments, out _) is string extraProblem)
                AppendActivity($"Launches are blocked until this is fixed: {extraProblem}");
        }
    }

    private void SaveLaunchBehavior(bool appendActivity = true)
    {
        _settings.AllowMultipleClients = AllowMultipleClientsCheckBox.IsChecked == true;
        _settings.SkipIntroVideos = SkipIntroVideosCheckBox.IsChecked == true;
        _settings.SkipLoginLogos = SkipLoginLogosCheckBox.IsChecked == true;
        _settings.AutoLaunch = AutoLaunchHeaderCheckBox.IsChecked == true;
        _settings.AutoInjectAfterLaunch = AutoInjectAfterLaunchCheckBox.IsChecked == true;
        _settings.WatchForAcStart = WatchForAcStartCheckBox.IsChecked == true;
        _settings.InjectAllRunningClients = InjectAllRunningAcCheckBox.IsChecked == true;
        _settings.LoggingLevel = GetSelectedLoggingLevel();
        _settings.OverrideWindowTitle = OverrideWindowTitleCheckBox.IsChecked == true;
        if (LaunchStaggerMsBox.Value is decimal staggerMs)
            _settings.LaunchStaggerMs = (int)staggerMs;
        if (CrashRelaunchLimitBox.Value is decimal crashLimit)
            _settings.CrashRelaunchLimitInWindow = (int)crashLimit;
        if (CrashRelaunchWindowBox.Value is decimal crashWindow)
            _settings.CrashRelaunchWindowMinutes = (int)crashWindow;
        _settings.KillStuckClients = KillStuckClientsCheckBox.IsChecked == true;
        if (StuckClientTimeoutBox.Value is decimal stuckTimeout)
            _settings.StuckClientTimeoutSeconds = (int)stuckTimeout;
        _settings.EnabledPluginIds = GetSelectedPluginIds().ToList();
        if (_settings.EnabledPluginIds.Count == 0)
            _settings.EnabledPluginIds.Add("rynthcore-engine");
        SaveSettings();
        SyncPluginPathsToEngineSettings();
        if (appendActivity)
        {
            AppendActivity($"Logging level saved: {_settings.LoggingLevel}.");
            AppendActivity("Launch behavior saved.");
        }
    }

    private void SaveAutoLaunchPreference()
    {
        bool enabled = AutoLaunchHeaderCheckBox.IsChecked == true;
        bool changed = _settings.AutoLaunch != enabled;
        _settings.AutoLaunch = enabled;
        SaveSettings();

        if (!changed)
            return;

        AppendActivity(enabled
            ? "Auto launch enabled. Checked targets will launch on startup and relaunch if they drop offline."
            : "Auto launch disabled.");

        if (enabled)
            RefreshSessionState();
    }

    private async Task LaunchPrimaryAsync()
    {
        LaunchAccountProfile? account = GetSelectedAccount();
        if (account == null)
        {
            AppendActivity("Launch blocked: choose a primary account first.");
            return;
        }

        await LaunchAccountsAsync([account], "primary pair");
    }

    private async Task LaunchCheckedTargetsAsync()
    {
        List<LaunchAccountProfile> accounts = GetCheckedLaunchAccounts();
        if (accounts.Count == 0)
        {
            LaunchAccountProfile? primary = GetSelectedAccount();
            if (primary == null)
            {
                AppendActivity("Launch blocked: check one or more launch targets, or choose a primary account.");
                return;
            }

            accounts.Add(primary);
        }

        await LaunchAccountsAsync(accounts, accounts.Count == 1 ? "selected target" : $"{accounts.Count} checked targets");
    }

    private async Task LaunchAccountsAsync(
        IReadOnlyList<LaunchAccountProfile> accountsToLaunch,
        string launchLabel,
        bool isAutomaticLaunch = false)
    {
        if (_operationBusy)
            return;

        // Headless accounts start rynth-headless.exe (LaunchOneHeadlessAsync), not acclient.exe:
        // the client checks below (client and engine paths, Extra Launch Arguments, the engine
        // plugin, Decal, the Decal bridge) are about the other accounts only. When they fail in
        // a batch with Headless accounts, the Headless accounts still launch.
        List<LaunchAccountProfile> headlessAccounts = accountsToLaunch.Where(a => a.InjectionMode == InjectionMode.Headless).ToList();
        List<LaunchAccountProfile> clientAccounts = accountsToLaunch.Where(a => a.InjectionMode != InjectionMode.Headless).ToList();

        string acPath = AcClientPathTextBox.Text?.Trim() ?? string.Empty;
        string enginePath = string.Empty;
        string extraArguments = string.Empty;
        bool shouldInject = AutoInjectAfterLaunchCheckBox.IsChecked == true;
        string? decalInjectPath = null;

        if (clientAccounts.Count > 0)
        {
            string? clientBlock = null;   // why the acclient accounts can't launch
            bool logClientBlock = true;

            if (string.IsNullOrWhiteSpace(acPath) || !File.Exists(acPath))
                clientBlock = "AC client path is missing or invalid.";

            if (clientBlock == null)
            {
                enginePath = EnginePathTextBox.Text?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(enginePath))
                    enginePath = _injector.TryResolveEnginePath(null) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(enginePath))
                    clientBlock = "engine path is missing and no default engine DLL could be resolved.";
            }

            // Extra Launch Arguments go after each account's own arguments, for manual and automatic
            // launches alike. They used to replace the whole line instead (and only manually, one
            // account at a time); ExtraArgumentsProblem refuses what an old override would carry.
            if (clientBlock == null &&
                AcLaunchArgumentBuilder.ExtraArgumentsProblem(LaunchArgumentsTextBox.Text, out extraArguments) is string extraProblem)
            {
                clientBlock = extraProblem;
                DateTime nowUtc = DateTime.UtcNow;
                logClientBlock = !isAutomaticLaunch || nowUtc - _extraArgumentsNoticeUtc >= AutoLaunchNeedsPasswordLogCooldown;
                if (logClientBlock)
                    _extraArgumentsNoticeUtc = nowUtc;
            }

            // Engine plugin must be selected when at least one chosen account is
            // RynthCore-mode AND auto-inject is on. Decal-mode accounts don't need
            // it because they don't load the engine.
            bool anyRynthCoreMode = clientAccounts.Any(a => a.InjectionMode is InjectionMode.RynthCore or InjectionMode.DecalBridge);
            if (clientBlock == null && shouldInject && anyRynthCoreMode &&
                !GetSelectedPluginIds().Contains("rynthcore-engine", StringComparer.OrdinalIgnoreCase))
            {
                clientBlock = "RynthCore Engine must stay selected in Runtime Loadout when auto-inject is enabled for any RynthCore-mode account.";
            }

            // Pre-resolve Decal Inject.dll once if any account needs it.
            bool anyDecalMode = clientAccounts.Any(a => a.InjectionMode is InjectionMode.Decal or InjectionMode.DecalBridge);
            if (clientBlock == null && shouldInject && anyDecalMode)
            {
                decalInjectPath = DecalLocator.TryGetInjectDllPath();
                if (decalInjectPath == null)
                    clientBlock = "one or more accounts are set to Decal mode but Decal is not installed (HKLM\\SOFTWARE\\Decal\\Agent\\AgentPath\\Inject.dll missing).";
            }

            // "Decal + RynthCore" accounts: make sure the Decal bridge is registered for this
            // Windows user (idempotent; opt-in - only these accounts ever trigger it).
            // Registered where THIS PC's AC client reads Decal's filter list, then checked: a
            // client that wouldn't load the bridge runs the old coexistence path (no overlay, then
            // a crash), so such a launch is blocked with the reason.
            if (clientBlock == null && shouldInject && clientAccounts.Any(a => a.InjectionMode == InjectionMode.DecalBridge))
            {
                string decalAcPath = DecalLocator.TryGetDecalAcClientPath() ?? acPath;
                if (!await EnsureDecalBridgeAsync(decalAcPath, interactive: !isAutomaticLaunch))
                {
                    clientBlock = "a Decal + RynthCore client would not load the Decal bridge on this PC (see the lines above, or Recovery Actions > Check Decal bridge). " +
                                  "Pick RynthCore or Decal for those accounts until it passes.";
                }
            }

            if (clientBlock != null)
            {
                if (logClientBlock)
                {
                    AppendActivity(headlessAccounts.Count > 0
                        ? $"Launch blocked for the accounts that aren't Headless: {clientBlock} The Headless accounts launch anyway."
                        : $"Launch blocked: {clientBlock}");
                }
                clientAccounts.Clear();
            }
        }

        // rynth-headless.exe must be there (Runtime > Headless client; default <launcher>\Headless).
        string headlessExePath = string.Empty;
        List<string> headlessPlugins = [];
        if (headlessAccounts.Count > 0)
        {
            headlessExePath = HeadlessLaunch.ResolveExePath(HeadlessExePathTextBox.Text, AppContext.BaseDirectory);
            // An empty box = the 64-bit plugins the installer ships in Headless\plugins (RynthAi).
            headlessPlugins = HeadlessLaunch.ResolvePluginPaths(ReadHeadlessPluginPathsBox(), AppContext.BaseDirectory);
            if (!File.Exists(headlessExePath))
            {
                foreach (LaunchAccountProfile account in headlessAccounts)
                {
                    NoteHeadlessRefusal(account, isAutomaticLaunch,
                        $"{HeadlessLaunch.ExeName} not found at {headlessExePath}. Set its path under Runtime > Headless client.");
                }
                headlessAccounts.Clear();
            }

            // Refused before the First Login dialog could ask for a password it then can't use.
            headlessAccounts.RemoveAll(account =>
            {
                LaunchServerProfile? server = ResolveServerForAccount(account);
                if (server?.Emulator != AcEmulatorKind.Gdle)
                    return false;
                NoteHeadlessRefusal(account, isAutomaticLaunch,
                    $"Headless runs on ACE servers only, and {server.DisplayName} is a GDLE server. Pick RynthCore or Decal for this account.");
                return true;
            });
        }

        if (clientAccounts.Count + headlessAccounts.Count == 0)
            return;
        if (clientAccounts.Count + headlessAccounts.Count < accountsToLaunch.Count)
            accountsToLaunch = accountsToLaunch.Where(a => clientAccounts.Contains(a) || headlessAccounts.Contains(a)).ToList();

        // No usable password: the client would refuse to start ("You can only run the game from
        // the launcher program") and exit within seconds. A manual launch asks for it (and the
        // account) in the First Login dialog; an automatic one skips the account.
        var ready = new List<LaunchAccountProfile>(accountsToLaunch.Count);
        foreach (LaunchAccountProfile account in accountsToLaunch)
        {
            if (!AcLaunchArgumentBuilder.NeedsPassword(account))
                ready.Add(account);
            else if (isAutomaticLaunch)
                AppendActivity($"Auto launch skipped for {account.DisplayName}: {AcLaunchArgumentBuilder.MissingPasswordMessage(account.AccountName)}");
            else if (await PromptFirstLoginAsync(account))
                ready.Add(account);
            else
                AppendActivity($"Launch cancelled for {account.DisplayName}: no password entered.");
        }

        if (ready.Count == 0)
            return;
        accountsToLaunch = ready;

        SaveRuntimePaths(appendActivity: !isAutomaticLaunch);
        SaveLaunchBehavior(appendActivity: !isAutomaticLaunch);

        LauncherDiag.Info($"LaunchAccountsAsync ENTRY label='{launchLabel}' auto={isAutomaticLaunch} count={accountsToLaunch.Count} semCount={_userPrefsSwapLock.CurrentCount} sessionPids={_launchedSessionPids.Count} pidInfo={_launchedPidInfo.Count} crashAccounts={_crashHistoryByAccount.Count}");
        if (_launchedPidInfo.Count > 0)
        {
            foreach (KeyValuePair<int, (string AccountKey, DateTime LaunchedAtUtc)> kv in _launchedPidInfo)
                LauncherDiag.Info($"  pidInfo[{kv.Key}] = account='{kv.Value.AccountKey}' launchedUtc={kv.Value.LaunchedAtUtc:HH:mm:ss}");
        }

        try
        {
            SetOperationState(true);
            // acclient's own settings (multiple clients, intro videos); nothing a Headless launch reads.
            if (accountsToLaunch.Any(a => a.InjectionMode != InjectionMode.Headless))
                _launchSettings.Apply(acPath, _settings.AllowMultipleClients, _settings.SkipIntroVideos, AppendActivity);
            AppendActivity($"Preparing {launchLabel}.");
            HashSet<string> activeAccountKeys = GetActiveAccountKeys();

            // Manual launch is the user saying "try again" — clear any crash
            // history for the accounts they're explicitly launching so the
            // circuit breaker doesn't immediately fire on their next auto-tick.
            if (!isAutomaticLaunch)
            {
                foreach (LaunchAccountProfile account in accountsToLaunch)
                {
                    string key = BuildAccountKey(account);
                    if (!string.IsNullOrEmpty(key))
                    {
                        _crashHistoryByAccount.Remove(key);
                        _crashBreakerLogTimesUtc.Remove(key);
                    }
                }
            }

            // Validation + already-running filter pass. Runs synchronously on
            // the UI thread; all skips emit activity messages here so they're
            // ordered and easy to read.
            var prepared = new List<(LaunchAccountProfile Account, string Args, string Summary, LaunchContextRecord Context)>();
            foreach (LaunchAccountProfile account in accountsToLaunch)
            {
                bool headless = account.InjectionMode == InjectionMode.Headless;
                string launchArguments = string.Empty;
                string launchSummary;
                if (headless)
                {
                    // No acclient command line (it would carry the password): rynth-headless reads
                    // the password itself, by profile id (HeadlessLaunch).
                    if (HeadlessLaunchProblem(account) is string headlessProblem)
                    {
                        NoteHeadlessRefusal(account, isAutomaticLaunch, headlessProblem);
                        continue;
                    }
                    launchSummary = _launchArgumentBuilder.Describe(ResolveServerForAccount(account)!, account) + " (headless)";
                }
                else if (!TryGetLaunchArgumentsForAccount(
                    account,
                    extraArguments,
                    out launchArguments,
                    out launchSummary,
                    out string error))
                {
                    AppendActivity($"Launch blocked for {account.DisplayName}: {error}");
                    continue;
                }

                LaunchServerProfile? contextServer = ResolveServerForAccount(account);
                string accountKey = BuildAccountKey(account.AccountName, contextServer?.Name);
                if (!string.IsNullOrWhiteSpace(accountKey) && IsAccountKeyActive(activeAccountKeys, accountKey))
                {
                    string serverLabel = contextServer?.DisplayName ?? "the configured server";
                    AppendActivity($"Skipped {account.DisplayName}: account '{account.AccountName}' already has a running session on {serverLabel}.");
                    continue;
                }

                LaunchContextRecord launchContext = BuildLaunchContext(account, contextServer);
                // Best-effort legacy write — last writer wins. The PID-specific
                // writes inside the launch task are the authoritative path.
                // (Not for Headless: only an engine inside acclient reads it.)
                if (!headless)
                    LaunchContextStore.WriteLegacy(launchContext);

                if (!string.IsNullOrWhiteSpace(accountKey))
                    activeAccountKeys.Add(accountKey);

                prepared.Add((account, launchArguments, launchSummary, launchContext));
            }

            // Launch pass: parallel with stagger. Each task awaits Task.Run for
            // the heavy CreateProcess+inject work, so result-handling resumes
            // back on the UI thread (Avalonia captures the dispatcher context).
            int staggerMs = Math.Max(0, _settings.LaunchStaggerMs);
            var launchTasks = new List<Task>(prepared.Count);
            for (int i = 0; i < prepared.Count; i++)
            {
                if (i > 0 && staggerMs > 0)
                    await Task.Delay(staggerMs);

                var p = prepared[i];
                Task launchTask;
                if (p.Account.InjectionMode == InjectionMode.Headless)
                {
                    // rynth-headless.exe whether or not auto-inject is on (there is nothing to inject).
                    launchTask = LaunchOneHeadlessAsync(p.Account, ResolveServerForAccount(p.Account), headlessExePath,
                        HeadlessLaunch.AcFolderFor(acPath), headlessPlugins, p.Summary, p.Context);
                }
                else if (!shouldInject)
                {
                    launchTask = LaunchOneNoInjectAsync(p.Account, acPath, p.Args, p.Summary, p.Context);
                }
                else if (p.Account.InjectionMode == InjectionMode.Decal)
                {
                    // Decal-mode launches use Decal's registered acclient.exe
                    // (PortalPath) when set; falls back to the global path. The
                    // distinction matters because Decal is built around the
                    // C:\Turbine\Asheron's Call install, while the RynthCore
                    // path may point at the private AcClient duplicate.
                    string decalAcPath = DecalLocator.TryGetDecalAcClientPath() ?? acPath;
                    launchTask = LaunchOneDecalInjectedAsync(p.Account, decalAcPath, p.Args, decalInjectPath!, null, p.Summary, p.Context);
                }
                else if (p.Account.InjectionMode == InjectionMode.DecalBridge)
                {
                    // Decal first, then RynthCore, in Decal's client (as Decal mode above).
                    string decalAcPath = DecalLocator.TryGetDecalAcClientPath() ?? acPath;
                    launchTask = LaunchOneDecalInjectedAsync(p.Account, decalAcPath, p.Args, decalInjectPath!, enginePath, p.Summary, p.Context);
                }
                else
                {
                    launchTask = LaunchOneInjectedAsync(p.Account, acPath, p.Args, enginePath, p.Summary, p.Context);
                }
                launchTasks.Add(launchTask);
            }

            if (launchTasks.Count > 0)
                await Task.WhenAll(launchTasks);
        }
        catch (Exception ex)
        {
            AppendActivity($"Launch failed: {ex.Message}");
        }
        finally
        {
            SetOperationState(false);
            RefreshSessionState();
        }
    }

    private async Task LaunchOneInjectedAsync(
        LaunchAccountProfile account,
        string acPath,
        string launchArguments,
        string enginePath,
        string launchSummary,
        LaunchContextRecord launchContext)
    {
        string resolvedPrefsPath = ResolveUserPrefsPath(account, launchContext.ServerName);
        bool needsPrefsSwap = !string.IsNullOrEmpty(resolvedPrefsPath) && File.Exists(resolvedPrefsPath);

        string entryAccountKey = BuildAccountKey(launchContext.AccountName, launchContext.ServerName);
        int crashHistCount = _crashHistoryByAccount.TryGetValue(entryAccountKey, out List<DateTime>? hist0) ? hist0.Count : 0;
        LauncherDiag.Info($"LaunchOneInjected ENTRY account='{launchContext.AccountName}' needsPrefsSwap={needsPrefsSwap} semCount={_userPrefsSwapLock.CurrentCount} sessionPids={_launchedSessionPids.Count} pidInfo={_launchedPidInfo.Count} crashHist={crashHistCount}");

        if (needsPrefsSwap)
        {
            LauncherDiag.Info($"LaunchOneInjected acquiring _userPrefsSwapLock (semCount={_userPrefsSwapLock.CurrentCount})");
            await _userPrefsSwapLock.WaitAsync();
            LauncherDiag.Info($"LaunchOneInjected acquired _userPrefsSwapLock (semCount={_userPrefsSwapLock.CurrentCount})");
        }

        try
        {
            PrefsSwapLaunch? prefsSwap = needsPrefsSwap ? ApplyPrefsSwap(account, launchContext.ServerName, resolvedPrefsPath) : null;

            InjectionResult result = await Task.Run(() =>
                _injector.LaunchSuspendedAndInject(
                    acPath,
                    launchArguments,
                    enginePath,
                    AppendActivity,
                    processId =>
                    {
                        LaunchContextStore.WriteForProcess(processId, launchContext);
                        WritePendingSessionState(processId, launchContext);
                    }));

            string accountKey = BuildAccountKey(launchContext.AccountName, launchContext.ServerName);

            LauncherDiag.Info($"LaunchOneInjected injection result: success={result.Success} pid={(result.ProcessId is int p ? p.ToString() : "null")} summary='{result.Summary}'");

            if (result.Success)
            {
                if (result.ProcessId is int trackedPid)
                {
                    _launchedSessionPids.Add(trackedPid);
                    _launchedPidInfo[trackedPid] = (accountKey, DateTime.UtcNow);
                    TrackPrefsSwap(trackedPid, prefsSwap);
                    RegisterPidExitWatch(trackedPid);
                }

                AppendActivity(result.ProcessId is int pid
                    ? $"Launch complete for {launchSummary} (PID {pid})."
                    : $"Launch complete for {launchSummary}.");
            }
            else
            {
                RecordCrash(accountKey);
                AppendActivity($"Launch failed for {launchSummary}: {result.Summary}");
            }

            if (needsPrefsSwap)
                await Task.Delay(UserPrefsSettleDelay);
        }
        finally
        {
            if (needsPrefsSwap)
            {
                LauncherDiag.Info($"LaunchOneInjected releasing _userPrefsSwapLock (semCount={_userPrefsSwapLock.CurrentCount})");
                _userPrefsSwapLock.Release();
                LauncherDiag.Info($"LaunchOneInjected released _userPrefsSwapLock (semCount={_userPrefsSwapLock.CurrentCount})");
            }
            LauncherDiag.Info($"LaunchOneInjected EXIT account='{launchContext.AccountName}'");
        }
    }

    private async Task LaunchOneDecalInjectedAsync(
        LaunchAccountProfile account,
        string acPath,
        string launchArguments,
        string decalInjectDllPath,
        string? enginePath,
        string launchSummary,
        LaunchContextRecord launchContext)
    {
        string resolvedPrefsPath = ResolveUserPrefsPath(account, launchContext.ServerName);
        bool needsPrefsSwap = !string.IsNullOrEmpty(resolvedPrefsPath) && File.Exists(resolvedPrefsPath);

        string entryAccountKey = BuildAccountKey(launchContext.AccountName, launchContext.ServerName);
        int crashHistCount = _crashHistoryByAccount.TryGetValue(entryAccountKey, out List<DateTime>? hist0) ? hist0.Count : 0;
        LauncherDiag.Info($"LaunchOneDecal ENTRY account='{launchContext.AccountName}' needsPrefsSwap={needsPrefsSwap} semCount={_userPrefsSwapLock.CurrentCount} sessionPids={_launchedSessionPids.Count} pidInfo={_launchedPidInfo.Count} crashHist={crashHistCount}");

        if (needsPrefsSwap)
        {
            LauncherDiag.Info($"LaunchOneDecal acquiring _userPrefsSwapLock (semCount={_userPrefsSwapLock.CurrentCount})");
            await _userPrefsSwapLock.WaitAsync();
            LauncherDiag.Info($"LaunchOneDecal acquired _userPrefsSwapLock (semCount={_userPrefsSwapLock.CurrentCount})");
        }

        try
        {
            PrefsSwapLaunch? prefsSwap = needsPrefsSwap ? ApplyPrefsSwap(account, launchContext.ServerName, resolvedPrefsPath) : null;

            // enginePath != null: "Decal + RynthCore" - Decal is injected first, then the
            // RynthCore loader, before the client resumes.
            InjectionResult result = await Task.Run(() =>
                _injector.LaunchSuspendedAndInjectDecalThenEngine(
                    acPath,
                    launchArguments,
                    decalInjectDllPath,
                    enginePath,
                    AppendActivity,
                    processId =>
                    {
                        LaunchContextStore.WriteForProcess(processId, launchContext);
                        WritePendingSessionState(processId, launchContext);
                    }));

            string accountKey = BuildAccountKey(launchContext.AccountName, launchContext.ServerName);

            LauncherDiag.Info($"LaunchOneDecal injection result: success={result.Success} pid={(result.ProcessId is int p ? p.ToString() : "null")} summary='{result.Summary}'");

            if (result.Success)
            {
                if (result.ProcessId is int trackedPid)
                {
                    _launchedSessionPids.Add(trackedPid);
                    _launchedPidInfo[trackedPid] = (accountKey, DateTime.UtcNow);
                    TrackPrefsSwap(trackedPid, prefsSwap);
                    RegisterPidExitWatch(trackedPid);
                }

                AppendActivity(result.ProcessId is int pid
                    ? $"Decal launch complete for {launchSummary} (PID {pid})."
                    : $"Decal launch complete for {launchSummary}.");
            }
            else
            {
                RecordCrash(accountKey);
                AppendActivity($"Decal launch failed for {launchSummary}: {result.Summary}");
            }

            if (needsPrefsSwap)
                await Task.Delay(UserPrefsSettleDelay);
        }
        finally
        {
            if (needsPrefsSwap)
            {
                LauncherDiag.Info($"LaunchOneDecal releasing _userPrefsSwapLock (semCount={_userPrefsSwapLock.CurrentCount})");
                _userPrefsSwapLock.Release();
                LauncherDiag.Info($"LaunchOneDecal released _userPrefsSwapLock (semCount={_userPrefsSwapLock.CurrentCount})");
            }
            LauncherDiag.Info($"LaunchOneDecal EXIT account='{launchContext.AccountName}'");
        }
    }

    private async Task LaunchOneNoInjectAsync(
        LaunchAccountProfile account,
        string acPath,
        string launchArguments,
        string launchSummary,
        LaunchContextRecord launchContext)
    {
        string resolvedPrefsPath = ResolveUserPrefsPath(account, launchContext.ServerName);
        bool needsPrefsSwap = !string.IsNullOrEmpty(resolvedPrefsPath) && File.Exists(resolvedPrefsPath);

        LauncherDiag.Info($"LaunchOneNoInject ENTRY account='{launchContext.AccountName}' needsPrefsSwap={needsPrefsSwap} semCount={_userPrefsSwapLock.CurrentCount} sessionPids={_launchedSessionPids.Count} pidInfo={_launchedPidInfo.Count}");

        if (needsPrefsSwap)
        {
            LauncherDiag.Info($"LaunchOneNoInject acquiring _userPrefsSwapLock (semCount={_userPrefsSwapLock.CurrentCount})");
            await _userPrefsSwapLock.WaitAsync();
            LauncherDiag.Info($"LaunchOneNoInject acquired _userPrefsSwapLock (semCount={_userPrefsSwapLock.CurrentCount})");
        }

        try
        {
            PrefsSwapLaunch? prefsSwap = needsPrefsSwap ? ApplyPrefsSwap(account, launchContext.ServerName, resolvedPrefsPath) : null;

            Process? proc = null;
            Exception? launchEx = null;

            await Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo(acPath, launchArguments)
                    {
                        UseShellExecute = false,
                        WorkingDirectory = Path.GetDirectoryName(acPath) ?? string.Empty
                    };
                    proc = Process.Start(psi);
                }
                catch (Exception ex)
                {
                    launchEx = ex;
                }
            });

            if (launchEx != null)
            {
                AppendActivity($"Launch failed for {launchSummary}: {launchEx.Message}");
                return;
            }

            if (proc != null)
            {
                _launchedSessionPids.Add(proc.Id);
                _launchedPidInfo[proc.Id] = (BuildAccountKey(launchContext.AccountName, launchContext.ServerName), DateTime.UtcNow);
                TrackPrefsSwap(proc.Id, prefsSwap);
                RegisterPidExitWatch(proc.Id);
                LaunchContextStore.WriteForProcess(proc.Id, launchContext);
                WritePendingSessionState(proc.Id, launchContext);
                LauncherDiag.Info($"LaunchOneNoInject Process.Start ok pid={proc.Id}");
            }
            else
            {
                LauncherDiag.Info("LaunchOneNoInject Process.Start returned null");
            }
            AppendActivity($"Launched AC (no injection) using {launchSummary}.");

            if (needsPrefsSwap)
                await Task.Delay(UserPrefsSettleDelay);
        }
        finally
        {
            if (needsPrefsSwap)
            {
                LauncherDiag.Info($"LaunchOneNoInject releasing _userPrefsSwapLock (semCount={_userPrefsSwapLock.CurrentCount})");
                _userPrefsSwapLock.Release();
                LauncherDiag.Info($"LaunchOneNoInject released _userPrefsSwapLock (semCount={_userPrefsSwapLock.CurrentCount})");
            }
            LauncherDiag.Info($"LaunchOneNoInject EXIT account='{launchContext.AccountName}'");
        }
    }

    // ── Headless accounts (InjectionMode.Headless) ─────────────────────────
    // One rynth-headless.exe per account, with a run config the launcher writes
    // (HeadlessLaunch). No acclient, so none of its steps apply: no prefs swap,
    // window title or placement, graphics readiness, injection or heartbeat log.
    // The process writes its own session_<pid>.json (--launcher), so the session
    // poll sees it like any client once FindClientProcesses lists it.

    /// <summary>
    /// Starts rynth-headless.exe for <paramref name="account"/>: writes the run config
    /// (%APPDATA%\RynthCore\headless\launch_&lt;profile id&gt;.json, no password in it), starts
    /// the process without a window, writes its launch context and pending session record, and
    /// tracks the pid like the other launch paths (exit watch, quick-exit crash accounting).
    /// </summary>
    private async Task LaunchOneHeadlessAsync(
        LaunchAccountProfile account,
        LaunchServerProfile? server,
        string exePath,
        string? acFolder,
        IReadOnlyList<string> plugins,
        string launchSummary,
        LaunchContextRecord launchContext)
    {
        string accountKey = BuildAccountKey(launchContext.AccountName, launchContext.ServerName);
        LauncherDiag.Info($"LaunchOneHeadless ENTRY account='{launchContext.AccountName}' exe='{exePath}' plugins={plugins.Count} sessionPids={_launchedSessionPids.Count} pidInfo={_launchedPidInfo.Count}");
        if (server == null)
        {
            AppendActivity($"Launch blocked for {account.DisplayName}: {AccountServerRules.NoServerMessage(account, _settings.ServerProfiles.Count > 0)}");
            return;
        }

        var spec = new HeadlessRunSpec
        {
            Host = server.Host,
            Port = server.Port,
            ServerName = server.Name,
            SuiteDir = _settings.HeadlessSuiteDir,
            StatusDir = _settings.HeadlessStatusDir,
            Plugins = plugins.ToList(),
            Sessions =
            {
                new HeadlessSessionSpec
                {
                    LauncherProfileId = account.Id,
                    Account = account.AccountName,
                    // "" = no auto-login for acclient; rynth-headless has no character screen to stop at, so it picks.
                    Character = string.IsNullOrWhiteSpace(launchContext.TargetCharacter) ? null : launchContext.TargetCharacter,
                    Commands = launchContext.OnLoginCommands?.ToList() ?? [],
                    StaySeconds = 0,
                    Reconnect = true,
                },
            },
        };
        string configPath = HeadlessLaunch.ConfigPath(AppSettingsStore.SettingsDirectoryPath, account.Id);

        int? pid = null;
        Exception? launchEx = null;
        await Task.Run(() =>
        {
            try
            {
                HeadlessLaunch.WriteConfig(configPath, HeadlessLaunch.BuildConfigJson(spec));
                var psi = new ProcessStartInfo(exePath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty
                };
                foreach (string arg in HeadlessLaunch.BuildArguments(configPath, acFolder))
                    psi.ArgumentList.Add(arg);
                using Process? proc = Process.Start(psi);
                if (proc != null)
                {
                    // As the injected paths do from their suspended-launch callback. The process
                    // overwrites the session record with its own as soon as it is connecting.
                    LaunchContextStore.WriteForProcess(proc.Id, launchContext);
                    WritePendingSessionState(proc.Id, launchContext);
                    pid = proc.Id;
                }
            }
            catch (Exception ex)
            {
                launchEx = ex;
            }
        });

        if (launchEx != null || pid is not int trackedPid)
        {
            RecordCrash(accountKey);
            string why = launchEx?.Message ?? "the process did not start";
            AppendActivity($"Headless launch failed for {launchSummary}: {why}");
            LauncherDiag.Info($"LaunchOneHeadless failed account='{launchContext.AccountName}': {why}");
            return;
        }

        _launchedSessionPids.Add(trackedPid);
        _launchedHeadlessPids.Add(trackedPid);
        _launchedPidInfo[trackedPid] = (accountKey, DateTime.UtcNow);
        RegisterPidExitWatch(trackedPid);
        AppendActivity($"Headless launch complete for {launchSummary} (PID {trackedPid}).");
        LauncherDiag.Info($"LaunchOneHeadless EXIT account='{launchContext.AccountName}' pid={trackedPid} config='{configPath}'");
    }

    /// <summary>Why <paramref name="account"/> can't run headless now (null = it can).</summary>
    private string? HeadlessLaunchProblem(LaunchAccountProfile account)
    {
        LaunchServerProfile? server = ResolveServerForAccount(account);
        if (server == null)
            return AccountServerRules.NoServerMessage(account, _settings.ServerProfiles.Count > 0);
        if (server.Emulator == AcEmulatorKind.Gdle)
            return $"Headless runs on ACE servers only, and {server.DisplayName} is a GDLE server. Pick RynthCore or Decal for this account.";
        if (string.IsNullOrWhiteSpace(server.Host) || server.Port <= 0)
            return $"the server profile {server.DisplayName} needs a host and a port.";
        if (AcLaunchArgumentBuilder.AccountNameProblem(account.AccountName, AcEmulatorKind.Ace) is string nameProblem)
            return nameProblem;
        // rynth-headless decrypts the saved password itself; check now that this Windows user can.
        if (account.PasswordNeedsReentry ||
            (!string.IsNullOrEmpty(account.PasswordProtected) && !AccountPasswordProtection.CanUnprotect(account.PasswordProtected)))
        {
            account.PasswordNeedsReentry = true;
            return LaunchAccountProfile.PasswordNeedsReentryMessage(account.AccountName);
        }
        return null;
    }

    /// <summary>Logs why a Headless launch of <paramref name="account"/> was refused; automatic launches at most every few minutes.</summary>
    private void NoteHeadlessRefusal(LaunchAccountProfile account, bool isAutomaticLaunch, string problem)
    {
        DateTime nowUtc = DateTime.UtcNow;
        if (isAutomaticLaunch &&
            _headlessRefusalNoticeTimesUtc.TryGetValue(account.Id, out DateTime lastUtc) &&
            nowUtc - lastUtc < AutoLaunchNeedsPasswordLogCooldown)
        {
            return;
        }
        _headlessRefusalNoticeTimesUtc[account.Id] = nowUtc;
        AppendActivity($"{(isAutomaticLaunch ? "Auto launch skipped" : "Launch blocked")} for {account.DisplayName} (Headless): {problem}");
    }

    /// <summary>The Runtime tab's headless plugin box: one DLL path per line, blank lines and surrounding quotes dropped.</summary>
    private List<string> ReadHeadlessPluginPathsBox() =>
        (HeadlessPluginPathsTextBox.Text ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().Trim('"').Trim())
            .Where(line => line.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// acclient.exe and rynth-headless.exe processes: every client the launcher tracks sessions,
    /// account keys and auto-launch against. Also refreshes <see cref="_headlessClientPids"/>.
    /// Steps that only apply to acclient (window, injection, heartbeat log) filter with
    /// <see cref="IsHeadlessPid"/> / <see cref="AcClientsOnly"/>. The caller disposes the array.
    /// </summary>
    private Process[] FindClientProcesses()
    {
        Process[] acClients = _injector.FindTargetProcesses();
        Process[] headless;
        try { headless = Process.GetProcessesByName(HeadlessLaunch.ProcessName); }
        catch { headless = []; }

        _headlessClientPids.Clear();
        foreach (Process p in headless)
        {
            try { _headlessClientPids.Add(p.Id); } catch { }
        }
        return headless.Length == 0 ? acClients : [.. acClients, .. headless];
    }

    /// <summary>True for a rynth-headless process (seen in the last enumeration, or launched as one here).</summary>
    private bool IsHeadlessPid(int pid) => _headlessClientPids.Contains(pid) || _launchedHeadlessPids.Contains(pid);

    /// <summary>
    /// A rynth-headless.exe (from the last FindClientProcesses) already running this account's launch
    /// config. One a previous launcher started isn't in <see cref="_launchedHeadlessPids"/>, and its
    /// session records carry the headless client's own per-character ids, so HasTrackedPresence misses
    /// it: auto launch then started a second one for the same account, which died at once on the
    /// first one's open log (2026-10-30/31), and could otherwise have logged the account in twice.
    /// </summary>
    private bool HeadlessAlreadyRunning(LaunchAccountProfile account)
    {
        if (_headlessClientPids.Count == 0) return false;
        string config = HeadlessLaunch.ConfigPath(AppSettingsStore.SettingsDirectoryPath, account.Id);
        foreach (int pid in _headlessClientPids)
        {
            string? cmd = CrashDumpWriters.TryGetCommandLine(pid);
            if (cmd != null && cmd.Contains(config, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Runtime > Headless client > Log out headless clients: a headless client has no window to close,
    /// so every running rynth-headless gets the stop request (<see cref="EndClientProcess"/>: log out,
    /// exit, killed after the grace time).
    /// </summary>
    private void StopHeadlessClients()
    {
        Process[] clients = FindClientProcesses();
        int asked = 0;
        try
        {
            foreach (Process p in clients)
            {
                int pid;
                try { pid = p.Id; } catch { continue; }
                if (!_headlessClientPids.Contains(pid))
                    continue;
                try
                {
                    EndClientProcess(p, pid);
                    asked++;
                }
                catch (Exception ex)
                {
                    AppendActivity($"PID {pid} (headless): could not stop it: {ex.Message}");
                }
            }
        }
        finally
        {
            foreach (Process p in clients) p.Dispose();
        }
        AppendActivity(asked == 0
            ? "No headless client is running."
            : $"Asked {asked} headless client(s) to log out." +
              (_settings.AutoLaunch ? " AutoLaunch is on: checked accounts are started again." : ""));
    }

    private Process[] AcClientsOnly(Process[] targets) =>
        _headlessClientPids.Count == 0 && _launchedHeadlessPids.Count == 0
            ? targets
            : targets.Where(p => { try { return !IsHeadlessPid(p.Id); } catch { return false; } }).ToArray();

    /// <summary>
    /// Ends a client the launcher has decided to take down. acclient: killed at once. rynth-headless:
    /// asked to log every character out and exit (its stop event), and killed after
    /// <see cref="HeadlessLaunch.StopGrace"/> if it is still running; killed at once when it has no
    /// stop event. Throws as Process.Kill does.
    /// </summary>
    private void EndClientProcess(Process process, int pid)
    {
        if (!IsHeadlessPid(pid))
        {
            process.Kill();
            return;
        }

        if (!HeadlessLaunch.TrySignalStop(pid))
        {
            AppendActivity($"PID {pid} (headless) has no stop channel; killing it.");
            process.Kill();
            return;
        }

        DateTime startedUtc = GetProcessStartTimeUtc(process);
        AppendActivity($"PID {pid} (headless): asked to log out and exit; killed in {HeadlessLaunch.StopGrace.TotalSeconds:0} s if still running.");
        _ = KillHeadlessAfterGraceAsync(pid, startedUtc);
    }

    private async Task KillHeadlessAfterGraceAsync(int pid, DateTime startedUtc)
    {
        await Task.Delay(HeadlessLaunch.StopGrace);
        try
        {
            using Process p = Process.GetProcessById(pid);
            // The same process (not a recycled pid): name and start time match.
            if (p.HasExited ||
                !string.Equals(p.ProcessName, HeadlessLaunch.ProcessName, StringComparison.OrdinalIgnoreCase) ||
                Math.Abs((GetProcessStartTimeUtc(p) - startedUtc).TotalSeconds) > 1)
            {
                return;
            }
            AppendActivity($"PID {pid} (headless) still running {HeadlessLaunch.StopGrace.TotalSeconds:0} s after the stop request; killing it.");
            LauncherDiag.Info($"HEADLESS: PID {pid} ignored the stop request for {HeadlessLaunch.StopGrace.TotalSeconds:0} s - killed.");
            p.Kill();
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
        catch (Exception ex)
        {
            AppendActivity($"PID {pid} (headless): kill after the stop request failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Shows the First Login dialog for <paramref name="account"/> (no usable password). True when
    /// the player entered one: the profile is updated and saved (password encrypted at once).
    /// </summary>
    private async Task<bool> PromptFirstLoginAsync(LaunchAccountProfile account)
    {
        LaunchServerProfile? server = ResolveServerForAccount(account);
        if (server == null)
        {
            AppendActivity($"Launch blocked for {account.DisplayName}: {AccountServerRules.NoServerMessage(account, _settings.ServerProfiles.Count > 0)}");
            return false;
        }

        LaunchAccountProfile draft = account.Clone();
        var dialog = new FirstLoginDialog(draft, server, _settings.AccountProfiles,
            accountName => GetDetectedCharacters(accountName, server.Name));
        bool? accepted = await dialog.ShowDialog<bool?>(this);
        if (accepted != true)
            return false;

        account.CopyFrom(draft);
        SaveSettings();
        RefreshLists();
        RefreshSummary();

        string loginAs = string.IsNullOrWhiteSpace(account.CharacterName) || account.CharacterName == LaunchAccountProfile.NoneOption
            ? "stopping at character select"
            : $"logging in as {account.CharacterName}";
        AppendActivity($"First login on {server.DisplayName}: account '{account.AccountName}', password saved (encrypted), {loginAs}.");
        return true;
    }

    private LaunchContextRecord BuildLaunchContext(LaunchAccountProfile account, LaunchServerProfile? server)
    {
        string targetCharacter = ResolveTargetCharacterForLaunch(account, server);

        // Resolve OnLogin commands for the resolved character. Empty/None
        // selections produce no commands. Lookup is case-insensitive because
        // the dictionary uses an OrdinalIgnoreCase comparer.
        List<string>? onLoginCommands = null;
        if (!string.IsNullOrWhiteSpace(targetCharacter)
            && account.OnLoginCommandsByCharacter.TryGetValue(targetCharacter, out List<string>? cmds)
            && cmds.Count > 0)
        {
            onLoginCommands = new List<string>(cmds);
        }

        return LaunchContextStore.CreateRecord(
            account.AccountName,
            server?.Name ?? string.Empty,
            targetCharacter,
            _settings.SkipLoginLogos,
            onLoginCommands,
            account.OnLoginWaitMs);
    }

    private static void WritePendingSessionState(int processId, LaunchContextRecord launchContext)
    {
        var record = new SessionStateRecord
        {
            ProcessId = processId,
            AccountName = launchContext.AccountName,
            ServerName = launchContext.ServerName,
            TargetCharacter = launchContext.TargetCharacter,
            CharacterName = launchContext.TargetCharacter,
            LaunchStartedAtUtc = DateTime.UtcNow,
            IsLoggedIn = false
        };

        SessionStateStore.WriteForProcess(processId, record);
    }

    /// <param name="extraArguments">Extra Launch Arguments, already checked by
    /// <see cref="AcLaunchArgumentBuilder.ExtraArgumentsProblem"/>; appended after the account's own.</param>
    private bool TryGetLaunchArgumentsForAccount(
        LaunchAccountProfile account,
        string extraArguments,
        out string arguments,
        out string launchSummary,
        out string error)
    {
        LaunchServerProfile? server = ResolveServerForAccount(account);
        if (server == null)
        {
            arguments = string.Empty;
            launchSummary = string.Empty;
            error = AccountServerRules.NoServerMessage(account, _settings.ServerProfiles.Count > 0);
            return false;
        }

        try
        {
            arguments = AcLaunchArgumentBuilder.WithExtraArguments(_launchArgumentBuilder.BuildArguments(server, account), extraArguments);
            launchSummary = _launchArgumentBuilder.Describe(server, account);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            arguments = string.Empty;
            launchSummary = string.Empty;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// The server profile the account names, or null. No fallback to the selected or first
    /// server: that would send this account's password to a server it doesn't belong to.
    /// </summary>
    private LaunchServerProfile? ResolveServerForAccount(LaunchAccountProfile account) =>
        AccountServerRules.ServerFor(account, _settings.ServerProfiles);

    private List<LaunchAccountProfile> GetCheckedLaunchAccounts()
    {
        _settings.CheckedLaunchAccountProfileIds ??= [];
        return _settings.AccountProfiles
            .Where(profile => _settings.CheckedLaunchAccountProfileIds.Contains(profile.Id, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    private IEnumerable<string> GetSelectedPluginIds()
    {
        foreach ((string pluginId, CheckBox checkBox) in _pluginChecks)
        {
            if (checkBox.IsChecked == true)
                yield return pluginId;
        }
    }

    private void OnPluginSelectionChanged(PluginDefinition plugin, CheckBox checkBox)
    {
        if (plugin.Id == "rynthcore-engine" && checkBox.IsChecked != true)
        {
            checkBox.IsChecked = true;
            AppendActivity("RynthCore Engine stays enabled because launch and manual inject depend on it.");
            return;
        }

        _settings.EnabledPluginIds = GetSelectedPluginIds().ToList();
        SaveSettings();
        AppendActivity($"Runtime loadout updated: {plugin.Name} {(checkBox.IsChecked == true ? "enabled" : "disabled")}.");
    }

    /// Handles checkbox toggle for a user-added plugin DLL row. Tracks the
    /// path in <see cref="AppSettings.DisabledPluginDllPaths"/> when off so
    /// the next sync writes a filtered PluginPaths array into engine.json
    /// (the engine only loads paths it sees there). Persists immediately so
    /// the next AC launch picks up the change without a save-as-you-go step.
    private void OnUserPluginEnabledChanged(string dllPath, CheckBox checkBox)
    {
        _settings.DisabledPluginDllPaths ??= new List<string>();

        bool enabled = checkBox.IsChecked == true;
        bool alreadyDisabled = _settings.DisabledPluginDllPaths
            .Any(p => string.Equals(p, dllPath, StringComparison.OrdinalIgnoreCase));

        if (enabled && alreadyDisabled)
        {
            for (int i = _settings.DisabledPluginDllPaths.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_settings.DisabledPluginDllPaths[i], dllPath, StringComparison.OrdinalIgnoreCase))
                {
                    _settings.DisabledPluginDllPaths.RemoveAt(i);
                    break;
                }
            }
        }
        else if (!enabled && !alreadyDisabled)
        {
            _settings.DisabledPluginDllPaths.Add(dllPath);
        }
        else
        {
            // No-op: state already matches checkbox.
            return;
        }

        SaveSettings();
        SyncPluginPathsToEngineSettings();
        AppendActivity($"Runtime loadout updated: {Path.GetFileName(dllPath)} {(enabled ? "enabled" : "disabled")} (engine.json synced).");
    }

    /// <summary>
    /// Applies the installer's one-shot plugin hand-off: the full installer writes the plugin
    /// DLL paths it placed in the user-chosen RynthSuite folder to
    /// <c>Software\Rynth\PendingPluginRegistration</c> (';'-separated). Each existing DLL is added
    /// to the Plugins list, and any entry with the same file name that no longer exists (e.g. the
    /// old <c>C:\Games\RynthSuite\...</c> path after moving the Suite folder) is replaced.
    /// Rows pointing inside the RynthCore folder whose file the installer removed are dropped too.
    /// The value is consumed once (deleted from HKCU; remembered in settings for HKLM).
    /// </summary>
    private void ApplyInstallerPluginRegistration()
    {
        try
        {
            string? pending = InstallerHandoff.ReadSetting("PendingPluginRegistration");
            if (string.IsNullOrWhiteSpace(pending)
                || string.Equals(pending, _settings.AppliedInstallerPluginRegistration, StringComparison.OrdinalIgnoreCase))
                return;

            _settings.PluginDllPaths ??= [];
            var added = new List<string>();

            // The installer clears loose DLLs out of the RynthCore folder (old 0.4.x layouts kept plugins
            // and SDK DLLs there), so drop rows that pointed inside it and are now gone. Rows elsewhere are
            // left alone, since a missing file there may just be on a drive that isn't mounted.
            string coreRoot = Path.TrimEndingDirectorySeparator(RynthInstallPaths.CoreDir) + Path.DirectorySeparatorChar;
            bool IsRemovedCoreFile(string p) =>
                p.StartsWith(coreRoot, StringComparison.OrdinalIgnoreCase) && !File.Exists(p);
            int pruned = _settings.PluginDllPaths.RemoveAll(IsRemovedCoreFile);
            _settings.DisabledPluginDllPaths.RemoveAll(IsRemovedCoreFile);

            foreach (string raw in pending.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string path;
                try { path = Path.GetFullPath(raw); } catch { continue; }
                if (!File.Exists(path)) continue;

                string fileName = Path.GetFileName(path);
                // Drop stale rows for the same plugin that point at a file which is gone.
                _settings.PluginDllPaths.RemoveAll(p =>
                    string.Equals(Path.GetFileName(p), fileName, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)
                    && !File.Exists(p));

                if (!_settings.PluginDllPaths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
                {
                    _settings.PluginDllPaths.Add(path);
                    added.Add(fileName);
                }
            }

            _settings.AppliedInstallerPluginRegistration = pending;
            SaveSettings();
            InstallerHandoff.DeleteUserSetting("PendingPluginRegistration");

            _pluginDllPaths.Clear();
            foreach (string p in _settings.PluginDllPaths) _pluginDllPaths.Add(p);
            SyncPluginPathsToEngineSettings();

            string msg = added.Count > 0
                ? $"Installer registered plugin(s): {string.Join(", ", added)} (engine.json synced)."
                : "Installer plugin registration already up to date.";
            if (pruned > 0)
                msg += $" Removed {pruned} plugin path(s) for files the installer cleared from {RynthInstallPaths.CoreDir}.";
            LauncherDiag.Info("PLUGINS: " + msg);
            try { AppendActivity(msg); } catch { }
        }
        catch (Exception ex)
        {
            LauncherDiag.Info("PLUGINS: installer registration failed: " + ex.Message);
        }
    }

    private void LoadPluginDllPaths()
    {
        _pluginDllPaths.Clear();
        if (_settings.PluginDllPaths != null)
        {
            foreach (string path in _settings.PluginDllPaths)
                _pluginDllPaths.Add(path);
        }

        // Plugins added to engine.json by something else (a deploy script, another launcher,
        // an older setup) join the list, so the next sync - which writes engine.json from this
        // list - can't silently drop them (RynthRemote and RynthLua were missing here, 2026-09-29).
        try
        {
            if (EngineJsonStore.Read().TryGetValue("PluginPaths", out System.Text.Json.JsonElement paths)
                && paths.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                bool adopted = false;
                foreach (System.Text.Json.JsonElement p in paths.EnumerateArray())
                {
                    string? path = p.ValueKind == System.Text.Json.JsonValueKind.String ? p.GetString() : null;
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    if (_pluginDllPaths.Any(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase))) continue;
                    _pluginDllPaths.Add(path);
                    adopted = true;
                }
                if (adopted)
                {
                    _settings.PluginDllPaths = _pluginDllPaths.ToList();
                    SaveSettings();
                }
            }
        }
        catch { /* engine.json unreadable: keep the launcher's own list */ }
    }

    private async Task AddPluginDllAsync()
    {
        var topLevel = GetTopLevel(this);
        if (topLevel == null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select a plugin DLL",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Plugin DLL") { Patterns = ["*.dll"] },
                new FilePickerFileType("All Files") { Patterns = ["*"] }
            ]
        });

        if (files.Count == 0)
            return;

        string fullPath = Path.GetFullPath(files[0].Path.LocalPath);

        if (_pluginDllPaths.Any(p => string.Equals(p, fullPath, StringComparison.OrdinalIgnoreCase)))
        {
            AppendActivity($"Plugin path already added: {fullPath}");
            return;
        }

        _pluginDllPaths.Add(fullPath);
        SavePluginDllPaths();
        BuildPluginLoadout();
        AppendActivity($"Added plugin: {Path.GetFileName(fullPath)}");
    }

    private void SavePluginDllPaths()
    {
        _settings.PluginDllPaths = _pluginDllPaths.ToList();
        SaveSettings();
        SyncPluginPathsToEngineSettings();
    }

    private void SyncPluginPathsToEngineSettings()
    {
        try
        {
            var disabled = new HashSet<string>(
                _settings.DisabledPluginDllPaths ?? new List<string>(),
                StringComparer.OrdinalIgnoreCase);
            List<string> enabledPaths = _pluginDllPaths
                .Where(p => !disabled.Contains(p))
                .ToList();
            EngineJsonStore.SetStringArray("PluginPaths", enabledPaths);
            // The engine reads its log verbosity from engine.json on init (EngineSettings.LoggingLevel).
            EngineJsonStore.SetString("LoggingLevel", GetSelectedLoggingLevel());
        }
        catch (Exception ex)
        {
            AppendActivity($"Failed to sync plugin paths to engine.json: {ex.Message}");
        }
    }

    // ── Version ───────────────────────────────────────────────────────────────

    /// <summary>Launcher version under the title; the engine's (Runtime\) in its tooltip and the log.</summary>
    private void ShowVersion()
    {
        string launcher = BuildVersion.Format(typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
        string engine = BuildVersion.OfFile(Path.Combine(AppContext.BaseDirectory, "Runtime", "RynthCore.Engine.dll"));
        HeaderVersionText.Text = launcher;
        ToolTip.SetTip(HeaderVersionText, $"Launcher {launcher}\nEngine {(engine.Length > 0 ? engine : "not found")}");
        AppendActivity($"RynthCore launcher {launcher}, engine {(engine.Length > 0 ? engine : "not found")}.");
    }

    // ── Updates (RynthUpdater) ────────────────────────────────────────────────
    // Plugins swap in place and never touch a running bot; the core installer runs only
    // with every RynthCore client closed. Nothing here ever triggers a reload (RL).

    private readonly List<string> _pluginsUpdatedThisSession = new();

    private DateTime? _lastUpdateCheckLocal;
    private string? _updateError;
    private bool _updateChecking;
    // The download in progress (shown on the update card and the Plugins tab), its Cancel, and a
    // generation number so a late progress report from a finished download is ignored.
    private DownloadProgress? _download;
    private CancellationTokenSource? _downloadCts;
    private int _downloadGeneration;
    // Cancel on the RynthNav map: automatic updates don't start it again this session (the
    // Update plugins button still does).
    private bool _navDataCancelledThisSession;
    private UpdateCard? _updateCard;

    private async Task CheckForUpdatesAsync()
    {
        if (_updateBusy) return;
        if (!_updater.IsConfigured)
        {
            RefreshUpdateCard();
            CheckUpdatesButton.IsEnabled = false;
            return;
        }

        _updateBusy = true;
        _updateChecking = true;
        CheckUpdatesButton.IsEnabled = false;
        RefreshUpdateCard();
        try
        {
            _updateCheck = await _updater.CheckAsync(_pluginDllPaths.ToList());
            _updateError = null;
            _lastUpdateCheckLocal = DateTime.Now;
            _updateChecking = false;
            AnnounceNewPlugins(_updateCheck);
            BuildAvailablePlugins();
            if (_updateCheck.HoldEnded)
                EndRollbackHold($"RynthCore {_updateCheck.Manifest.Version} is out: updates are back on after the rollback.");
            // After Roll back, the release the player left isn't installed again by itself (RynthUpdater.Rollback.cs).
            if (_settings.AutoUpdatePlugins && !_updateCheck.Held && (_updateCheck.PluginsToUpdate.Any() || _updateCheck.Companions.Count > 0
                                                || (_updateCheck.NavDataUpdateAvailable && !_navDataCancelledThisSession)))
                await ApplyPluginUpdatesAsync(automatic: true);
            SendUsageReportIfDue();   // opt-in only, at most once a day, never awaited
        }
        catch (Exception ex)
        {
            _updateError = ex.Message;
            AppendActivity($"Update check failed: {ex.Message}");
        }
        finally
        {
            _updateBusy = false;
            _updateChecking = false;
            CheckUpdatesButton.IsEnabled = true;
            BuildAvailablePlugins();
            RefreshUpdateCard();
        }
    }

    /// <summary>Once per plugin, the first time the feed has it: "New plugins available: …".</summary>
    private void AnnounceNewPlugins(RynthUpdater.CheckResult c)
    {
        if (c.NewlyAnnounced.Count == 0) return;
        string list = string.Join(", ", c.NewlyAnnounced.Select(a =>
            a.Description.Length > 0 ? $"{a.Entry.Name} ({a.Description.TrimEnd('.')})" : a.Entry.Name));
        AppendActivity($"New plugins available: {list}. Install them from Available plugins on the Plugins tab, or leave them out.");
    }

    // ── Downloads: one progress display for every download ────────────────────

    /// <summary>Hands progress from the updater (any thread) to the UI thread.</summary>
    private sealed class UiProgress(MainWindow window, int generation) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress p) => Dispatcher.UIThread.Post(() => window.OnDownloadProgress(p, generation));
    }

    private void OnDownloadProgress(DownloadProgress p, int generation)
    {
        // The finished line always reaches the log, even when it arrives after the download ended.
        if (p.Stage == DownloadStage.Verified && p.Elapsed > TimeSpan.Zero)
            AppendActivity(p.Summary + ".");
        if (generation != _downloadGeneration || _downloadCts == null) return;
        _download = p;
        RefreshUpdateCard();
    }

    /// <summary>Runs one download operation with the progress display and its Cancel button.</summary>
    private async Task<T> RunDownloadAsync<T>(Func<IProgress<DownloadProgress>, CancellationToken, Task<T>> op)
    {
        using var cts = new CancellationTokenSource();
        _downloadCts = cts;
        int generation = ++_downloadGeneration;
        RefreshUpdateCard();
        try
        {
            return await op(new UiProgress(this, generation), cts.Token);
        }
        finally
        {
            _downloadCts = null;
            _download = null;
            _downloadGeneration++;
            RefreshUpdateCard();
        }
    }

    // ── World map sizes (RynthUpdater.MapPacks.cs) ───────────────────────

    private string _mapPacksShown = "";

    /// <summary>
    /// The front page's "World map detail" card: a row per size the feed offers (name, download size, whether it is
    /// installed) with Download / Update / Remove. Rebuilt only when what it shows changes; the buttons are off while
    /// a download or an update runs. Called from RefreshUpdateCard, so it follows every check and download.
    /// </summary>
    private void RefreshMapPacksCard()
    {
        IReadOnlyList<RynthUpdater.MapPackStatus> packs = _updateCheck?.MapPacks ?? Array.Empty<RynthUpdater.MapPackStatus>();
        MapPacksCard.IsVisible = packs.Count > 0;
        bool busy = _updateBusy || _downloadCts != null;
        string signature = busy + "|" + string.Join(";", packs.Select(p => $"{p.Entry.Key}:{p.State}:{p.Entry.Sha256}"));
        if (signature == _mapPacksShown) return;
        _mapPacksShown = signature;
        MapPacksPanel.Children.Clear();
        foreach (RynthUpdater.MapPackStatus st in packs)
        {
            RynthUpdater.MapPackEntry e = st.Entry;
            string state = st.State switch
            {
                RynthUpdater.MapPackState.UpToDate => "installed",
                RynthUpdater.MapPackState.NeedsUpdate => "installed, a newer one is available",
                _ => "not installed",
            };
            var title = new TextBlock { Text = $"{e.Name}  -  {DownloadProgress.FormatMb(e.Size)}", FontWeight = global::Avalonia.Media.FontWeight.SemiBold, Foreground = global::Avalonia.Media.Brushes.White };
            var detail = new TextBlock { Text = (e.Description.Length > 0 ? e.Description + "  " : "") + "(" + state + ")", TextWrapping = global::Avalonia.Media.TextWrapping.Wrap, Foreground = global::Avalonia.Media.Brush.Parse("#9AA8B3") };
            var text = new StackPanel { Spacing = 2, VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center };
            text.Children.Add(title);
            text.Children.Add(detail);

            var buttons = new WrapPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center, Margin = new global::Avalonia.Thickness(12, 0, 0, 0) };
            if (st.State != RynthUpdater.MapPackState.UpToDate)
            {
                var get = new Button { Content = st.State == RynthUpdater.MapPackState.NeedsUpdate ? "Update" : "Download", IsEnabled = !busy, Margin = new global::Avalonia.Thickness(0, 0, 8, 0) };
                get.Click += async (_, _) => await InstallMapPackAsync(st);
                buttons.Children.Add(get);
            }
            if (st.State != RynthUpdater.MapPackState.NotInstalled)
            {
                var remove = new Button { Content = "Remove", IsEnabled = !busy };
                remove.Click += async (_, _) => await RemoveMapPackAsync(st);
                buttons.Children.Add(remove);
            }
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(text, 0);
            Grid.SetColumn(buttons, 1);
            row.Children.Add(text);
            row.Children.Add(buttons);
            MapPacksPanel.Children.Add(row);
        }
    }

    private async Task InstallMapPackAsync(RynthUpdater.MapPackStatus st)
    {
        if (_updateBusy || _downloadCts != null) return;
        _updateBusy = true;
        RefreshUpdateCard();
        try
        {
            string line = await RunDownloadAsync((progress, ct) => _updater.InstallMapPackAsync(st.Entry, progress, ct));
            AppendActivity(line);
        }
        catch (OperationCanceledException)
        {
            AppendActivity($"World map ({st.Entry.Name}) download cancelled. Nothing was changed.");
        }
        catch (Exception ex)
        {
            AppendActivity($"World map ({st.Entry.Name}) download failed: {ex.Message}");
        }
        finally { _updateBusy = false; }
        _updateCheck = await _updater.CheckAsync(_pluginDllPaths.ToList());
        RefreshUpdateCard();
    }

    private async Task RemoveMapPackAsync(RynthUpdater.MapPackStatus st)
    {
        if (_updateBusy || _downloadCts != null) return;
        try
        {
            bool had = await Task.Run(() => _updater.RemoveMapPack(st.Entry));
            AppendActivity(had ? $"World map ({st.Entry.Name}) removed." : $"World map ({st.Entry.Name}) wasn't installed.");
        }
        catch (Exception ex)
        {
            AppendActivity($"World map ({st.Entry.Name}) couldn't be removed: {ex.Message} (a running game client may have it open).");
        }
        _updateCheck = await _updater.CheckAsync(_pluginDllPaths.ToList());
        RefreshUpdateCard();
    }

    private void CancelDownload()
    {
        if (_downloadCts is not { IsCancellationRequested: false } cts) return;
        cts.Cancel();
        CancelDownloadButton.IsEnabled = false;
    }

    private async Task OnUpdateCardActionAsync()
    {
        switch (_updateCard?.Kind)
        {
            case UpdateCardKind.CoreUpdate or UpdateCardKind.Held: await InstallCoreUpdateAsync(); break;
            case UpdateCardKind.PluginUpdates: await UpdatePluginsAsync(); break;
        }
    }

    private List<string> PendingUpdateNames(RynthUpdater.CheckResult? c)
    {
        if (c == null) return new();
        var pending = c.PluginsToUpdate.Select(p => p.Entry.Name).Distinct()
                       .Concat(c.Companions.Select(x => $"{x.Entry.Name} (new: {x.Reason})")).ToList();
        if (c.NavDataUpdateAvailable)
            pending.Add($"RynthNav map ({DownloadProgress.FormatMb(c.NavData!.Entry.Size)} download)");
        return pending;
    }

    /// <summary>Redraws the front page's update card, the Plugins tab's status line and the header.</summary>
    private void RefreshUpdateCard()
    {
        KeepLauncherScroll();
        RefreshMapPacksCard();
        RynthUpdater.CheckResult? c = _updateCheck;
        bool coreUpdate = c?.CoreUpdateAvailable == true;
        var card = UpdateCardModel.Build(new UpdateCardInput
        {
            Configured = _updater.IsConfigured,
            Checking = _updateChecking,
            Error = _updateError,
            LastChecked = _lastUpdateCheckLocal,
            HasCheck = c != null,
            InstalledVersion = FormatRelease(c?.InstalledCoreRelease ?? _updater.InstalledCoreRelease),
            CoreUpdateAvailable = coreUpdate,
            AvailableVersion = c?.Manifest.Core.Version ?? "",
            Notes = c?.Manifest.Notes ?? "",
            Changes = c?.Manifest.Changes ?? Array.Empty<string>(),
            PendingPluginUpdates = PendingUpdateNames(c),
            NewPlugins = _available.Where(a => a.IsNew).Select(a => a.Entry.Name).ToList(),
            // Only worth the process scan when there is a RynthCore update to wait for.
            ClientsRunning = coreUpdate && _download == null ? RynthCoreClientsRunning() : 0,
            Download = _download,
            CanCancel = _downloadCts is { IsCancellationRequested: false },
            HeldFromVersion = c is { Held: true } ? FormatRelease(c.HoldThrough) : "",
        });
        _updateCard = card;

        UpdateCardTitle.Text = card.Title;
        UpdateCardTitle.Foreground = Brush.Parse(card.Kind switch
        {
            UpdateCardKind.CoreUpdate or UpdateCardKind.PluginUpdates or UpdateCardKind.Held => "#F2C14E",
            UpdateCardKind.Error => "#E57373",
            _ => "#26C1A6",
        });
        UpdateCardBorder.BorderBrush = Brush.Parse(card.Kind is UpdateCardKind.CoreUpdate or UpdateCardKind.PluginUpdates or UpdateCardKind.Held ? "#F2C14E" : "#243742");
        UpdateCardDetail.Text = card.Detail;
        UpdateCardDetail.IsVisible = card.Detail.Length > 0;
        UpdateCardActionButton.IsVisible = card.ActionButton != null;
        UpdateCardActionButton.Content = card.ActionButton ?? "";
        UpdateCardActionButton.IsEnabled = !_updateBusy;
        CheckUpdatesButton.IsVisible = card.ShowCheckButton;
        RollbackButton.IsVisible = card.ShowCheckButton && _updater.IsConfigured && _updater.InstalledCoreRelease > 0;
        RollbackButton.IsEnabled = !_updateBusy;

        UpdateCardWhatsNewPanel.Children.Clear();
        if (card.WhatsNew.Count > 0)
        {
            UpdateCardWhatsNewPanel.Children.Add(new TextBlock { Text = "What's new", FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#EAF0F4") });
            foreach (string line in card.WhatsNew)
                UpdateCardWhatsNewPanel.Children.Add(new TextBlock { Text = "• " + line, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#C5D0D8") });
        }
        UpdateCardWhatsNewPanel.IsVisible = card.WhatsNew.Count > 0;

        DownloadArea.IsVisible = card.ShowProgress;
        DownloadProgressBar.IsIndeterminate = card.ShowProgress && card.ProgressPercent == null;
        DownloadProgressBar.Value = card.ProgressPercent ?? 0;
        CancelDownloadButton.IsVisible = card.ShowProgress && _downloadCts != null;
        CancelDownloadButton.IsEnabled = card.ShowCancel;

        UpdateCardNewPluginsButton.IsVisible = card.NewPluginsLine != null;
        UpdateCardNewPluginsButton.Content = card.NewPluginsLine ?? "";

        // Plugins tab: one compact status (the buttons are on the card), its own small bar, and
        // what this session did.
        var lines = new List<string> { card.Compact };
        if (_pluginsUpdatedThisSession.Count > 0)
            lines.Add($"Updated this session: {string.Join(", ", _pluginsUpdatedThisSession)}. Loads the next time AC starts.");
        var local = c?.Plugins.Where(p => p.State == RynthUpdater.PluginState.LocalBuild).Select(p => p.Entry.Name).Distinct().ToList();
        if (local is { Count: > 0 }) lines.Add($"Left alone (newer local builds): {string.Join(", ", local)}.");
        var waiting = c?.PluginsWaitingForCore.GroupBy(p => p.Entry.Name).Select(g => $"{g.Key} ({g.First().Blocker})").ToList();
        if (waiting is { Count: > 0 }) lines.Add($"Waiting for a RynthCore update: {string.Join(", ", waiting)}.");
        UpdateStatusText.Text = string.Join("\n", lines);
        PluginsTabProgressBar.IsVisible = card.ShowProgress;
        PluginsTabProgressBar.IsIndeterminate = card.ShowProgress && card.ProgressPercent == null;
        PluginsTabProgressBar.Value = card.ProgressPercent ?? 0;

        bool headerShows = card.Kind is UpdateCardKind.CoreUpdate or UpdateCardKind.PluginUpdates or UpdateCardKind.Downloading;
        HeaderUpdateText.IsVisible = headerShows;
        HeaderUpdateText.Text = card.Kind == UpdateCardKind.Downloading && card.ProgressPercent is { } pct
            ? $"{card.Title} ({pct:0}%)"
            : card.Kind == UpdateCardKind.Downloading ? card.Title : card.Compact;
    }

    // ── Usage statistics (opt-in; UsageStats.cs says exactly what is sent) ────

    private readonly UsageStats _usageStats = new();

    private void SetUsageStats(bool on)
    {
        if (_settings.UsageStatsEnabled == on) return;
        _settings.UsageStatsEnabled = on;
        _settings.UsageStatsAsked = true;
        SaveSettings();
        if (on)
        {
            _usageStats.OptIn(DateTime.UtcNow);
            AppendActivity("Usage statistics on: one anonymous report a day. Thank you.");
            SendUsageReportIfDue();
        }
        else
        {
            _usageStats.OptOut();
            AppendActivity("Usage statistics off: no more reports, and the random ID was deleted from this PC.");
        }
    }

    /// <summary>Explains usage statistics once and asks; "No thanks" (or closing the window) keeps them off.</summary>
    private async Task AskAboutUsageStatsOnceAsync()
    {
        if (_settings.UsageStatsAsked) return;
        await Task.Delay(TimeSpan.FromSeconds(2));
        if (_settings.UsageStatsAsked) return;   // already answered with the checkbox
        bool on;
        try { on = await UsageStatsDialog.AskAsync(this); }
        catch { return; }
        _settings.UsageStatsAsked = true;
        SaveSettings();
        if (on) UsageStatsCheckBox.IsChecked = true;   // -> SetUsageStats(true)
    }

    private (List<string> EnabledPaths, Dictionary<string, string> FeedNames) SnapshotPluginsForStats()
    {
        var disabled = new HashSet<string>(_settings.DisabledPluginDllPaths ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        List<string> enabled = _pluginDllPaths.Where(p => !disabled.Contains(p)).ToList();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (RynthUpdater.PluginEntry e in _updateCheck?.Manifest.Plugins ?? (IReadOnlyList<RynthUpdater.PluginEntry>)Array.Empty<RynthUpdater.PluginEntry>())
            names.TryAdd(e.File, e.Name);
        return (enabled, names);
    }

    private static string CoreVersionForStats() =>
        typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";

    /// <summary>Fire and forget, after a successful update check: silent on every failure.</summary>
    private void SendUsageReportIfDue()
    {
        if (!_settings.UsageStatsEnabled || _updateCheck == null) return;
        var (enabled, names) = SnapshotPluginsForStats();
        string version = CoreVersionForStats();
        _ = Task.Run(async () =>
        {
            try
            {
                var (known, other) = UsageStats.DescribePlugins(enabled, names);
                if (await _usageStats.MaybeSendAsync(() => _settings.UsageStatsEnabled, version, known, other, DateTime.UtcNow))
                    LauncherDiag.Info("USAGESTATS: today's anonymous report was sent");
            }
            catch { }
        });
    }

    private async Task ShowUsageReportAsync()
    {
        try
        {
            var (enabled, names) = SnapshotPluginsForStats();
            var (known, other) = UsageStats.DescribePlugins(enabled, names);
            string json = _usageStats.Preview(CoreVersionForStats(), known, other, DateTime.UtcNow);
            if (_updateCheck == null)
                json += "\n\n(The plugin list is filled in after the first successful update check.)";
            await UsageStatsDialog.ShowPayloadAsync(this, json, _settings.UsageStatsEnabled);
        }
        catch (Exception ex)
        {
            AppendActivity($"Could not show the usage report: {ex.Message}");
        }
    }

    private async Task UpdatePluginsAsync()
    {
        if (_updateBusy || _updateCheck == null) return;
        _updateBusy = true;
        RefreshUpdateCard();
        try
        {
            if (_updateCheck.HoldThrough > 0) EndRollbackHold(null);   // updating by hand ends a rollback's hold
            await ApplyPluginUpdatesAsync(automatic: false);
        }
        catch (Exception ex)
        {
            AppendActivity($"Plugin update failed: {ex.Message}");
        }
        finally
        {
            _updateBusy = false;
            BuildAvailablePlugins();
            RefreshUpdateCard();
        }
    }

    /// <summary>
    /// Plugin updates, companions and the RynthNav map, under one progress display. Cancel stops
    /// the download in progress; what finished before it stays finished, nothing is half-installed.
    /// </summary>
    private async Task ApplyPluginUpdatesAsync(bool automatic)
    {
        if (!automatic) _navDataCancelledThisSession = false;
        try
        {
            await RunDownloadAsync(async (progress, ct) =>
            {
                foreach (string done in await _updater.UpdatePluginsAsync(_updateCheck!, progress, ct))
                {
                    _pluginsUpdatedThisSession.Add(done);
                    AppendActivity($"Plugin updated: {done}. Running clients keep their version; it loads the next time AC starts.");
                }
                // Plugins split out of one the player has (RynthLua from RynthAi): installed and added.
                bool added = false;
                foreach (var c in await _updater.InstallCompanionsAsync(_updateCheck!, progress, ct))
                {
                    added |= PluginListEdits.Add(_pluginDllPaths, c.Path);
                    _pluginsUpdatedThisSession.Add($"{c.Entry.Name} (new)");
                    AppendActivity($"Plugin added: {c.Entry.Name} ({c.Reason}), {c.Path}. It loads the next time AC starts.");
                }
                if (added) SavePluginDllPaths();
                // RynthNav's map (only with RynthNav in the plugin list). Separate from the
                // plugins: a failure here (disk space, a client holding the folder) leaves the plugin
                // updates done and is reported on its own.
                if (_updateCheck!.NavDataUpdateAvailable && !(automatic && _navDataCancelledThisSession))
                {
                    try
                    {
                        string line = await _updater.UpdateNavDataAsync(_updateCheck!, progress, ct);
                        if (line.Length > 0)
                        {
                            _pluginsUpdatedThisSession.Add("RynthNav map");
                            AppendActivity(line);
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        _navDataCancelledThisSession = true;
                        AppendActivity("RynthNav map download cancelled. Nothing was changed: the map you had is still in place. Update plugins on the Launcher tab starts it again.");
                    }
                    catch (Exception ex)
                    {
                        AppendActivity($"RynthNav map update failed: {ex.Message}");
                    }
                }
                return true;
            });
        }
        catch (OperationCanceledException)
        {
            AppendActivity("Download cancelled. Updates that finished before it are kept; nothing is half-installed.");
        }
        // Re-check so the panel reflects the files now on disk, and redraw the plugin
        // rows so they show the new versions.
        _updateCheck = await _updater.CheckAsync(_pluginDllPaths.ToList());
        BuildPluginLoadout();
    }

    /// <summary>
    /// Roll back (RynthUpdater.Rollback.cs): with every game client closed, picks an earlier release,
    /// downloads and verifies it, puts its plugins in place, holds updates, then runs its installer (the
    /// launcher restarts as that release).
    /// </summary>
    private async Task RollBackAsync()
    {
        if (_updateBusy) return;
        int running = RynthCoreClientsRunning();
        if (running > 0)
        {
            AppendActivity($"Close your {running} game client(s) running RynthCore or its Decal bridge first: going back replaces files they have open.");
            return;
        }

        _updateBusy = true;
        RefreshUpdateCard();
        try
        {
            IReadOnlyList<RynthUpdater.RollbackTarget> targets = await _updater.ListRollbackTargetsAsync();
            if (targets.Count == 0)
            {
                AppendActivity($"There's no earlier release to go back to (going back works to releases from {FormatRelease(RynthUpdater.FirstRollbackRelease)} on).");
                return;
            }
            var dialog = new RollbackDialog(targets, FormatRelease(_updater.InstalledCoreRelease));
            RynthUpdater.RollbackTarget? chosen = await dialog.ShowDialog<RynthUpdater.RollbackTarget?>(this);
            if (chosen == null) return;

            RynthUpdater.RollbackPlan plan = await RunDownloadAsync((progress, ct) =>
                _updater.PrepareRollbackAsync(chosen, _pluginDllPaths.ToList(), progress, ct));
            if (RynthCoreClientsRunning() > 0)
            {
                AppendActivity("A RynthCore client started meanwhile. Close it, then press Roll back again. Nothing was changed.");
                return;
            }

            List<string> plugins = _updater.ApplyRollbackPlugins(plan);
            // The rolled-back launcher may be older than the hold: it would update the plugins straight back.
            bool turnOff = _settings.AutoUpdatePlugins;
            _updater.StartHold(plan.LeavingRelease, turnOff);
            if (turnOff)
            {
                _settings.AutoUpdatePlugins = false;
                AutoUpdatePluginsCheckBox.IsChecked = false;
                SaveSettings();
            }
            AppendActivity($"Going back to RynthCore {plan.Manifest.Version}" +
                           (plugins.Count > 0 ? $" (plugins: {string.Join(", ", plugins)})" : "") +
                           $"; the launcher restarts when it's done. Updates are on hold until a release newer than {FormatRelease(plan.LeavingRelease)} comes out.");
            RynthUpdater.RunInstaller(plan.InstallerPath);
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }
        catch (OperationCanceledException)
        {
            AppendActivity("Going back cancelled. Nothing was changed.");
        }
        catch (Exception ex)
        {
            AppendActivity($"Going back failed: {ex.Message}");
        }
        finally
        {
            _updateBusy = false;
            RefreshUpdateCard();
        }
    }

    /// <summary>Ends a rollback's hold and turns automatic plugin updates back on if the rollback turned them off.</summary>
    private void EndRollbackHold(string? message)
    {
        bool restore = _updater.HoldTurnedOffAutoUpdate;
        _updater.EndHold();
        if (restore && !_settings.AutoUpdatePlugins)
        {
            _settings.AutoUpdatePlugins = true;
            AutoUpdatePluginsCheckBox.IsChecked = true;
            SaveSettings();
        }
        if (message != null) AppendActivity(message);
    }

    private async Task InstallCoreUpdateAsync()
    {
        if (_updateBusy || _updateCheck is not { CoreUpdateAvailable: true } c) return;
        int running = RynthCoreClientsRunning();
        if (running > 0)
        {
            AppendActivity($"Close your {running} game client(s) running RynthCore or its Decal bridge (headless ones included) first: the update replaces files they have open.");
            RefreshUpdateCard();
            return;
        }

        _updateBusy = true;
        RefreshUpdateCard();
        try
        {
            string installer = await RunDownloadAsync((progress, ct) => _updater.DownloadInstallerAsync(c, progress, ct));
            if (RynthCoreClientsRunning() > 0)
            {
                AppendActivity("A RynthCore client started meanwhile. Close it, then press Update RynthCore again.");
                return;
            }
            if (c.HoldThrough > 0) EndRollbackHold(null);   // going forward by hand ends a rollback's hold
            AppendActivity($"Installing RynthCore {c.Manifest.Core.Version}; the launcher restarts when it's done.");
            RynthUpdater.RunInstaller(installer);
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }
        catch (OperationCanceledException)
        {
            AppendActivity($"RynthCore {c.Manifest.Core.Version} download cancelled. Nothing was changed.");
        }
        catch (Exception ex)
        {
            AppendActivity($"RynthCore update failed: {ex.Message}");
        }
        finally
        {
            _updateBusy = false;
            RefreshUpdateCard();
        }
    }

    private int RynthCoreClientsRunning()
    {
        int n = 0;
        foreach (Process p in Process.GetProcessesByName("acclient"))
        {
            // Decal clients with the Decal bridge loaded count too: the installer replaces
            // DecalBridge\RynthCore.DecalBridge.dll, which they keep open.
            try { if (_injector.IsRynthCoreLoaded(p) || _injector.IsDecalBridgeLoaded(p)) n++; }
            catch { }
            finally { p.Dispose(); }
        }
        // Headless clients (rynth-headless.exe) run RynthCore code and plugins from the install
        // too; no module check needed (and an x86 launcher can't list an x64 process's modules).
        try
        {
            foreach (Process p in Process.GetProcessesByName(HeadlessLaunch.ProcessName))
            {
                n++;
                p.Dispose();
            }
        }
        catch { }
        return n;
    }

    /// <summary>2026092603 → "2026.9.26.3" (the installer's -Version); 0 → "unknown".</summary>
    private static string FormatRelease(int r) =>
        r <= 0 ? "unknown" : $"{r / 1000000}.{r / 10000 % 100}.{r / 100 % 100}.{r % 100}";

    private async Task InjectRunningAcAsync()
    {
        if (_operationBusy)
            return;

        if (!GetSelectedPluginIds().Contains("rynthcore-engine", StringComparer.OrdinalIgnoreCase))
        {
            AppendActivity("Inject blocked: RynthCore Engine is not selected in Runtime Loadout.");
            return;
        }

        Process[] targets = _injector.FindTargetProcesses();
        if (targets.Length == 0)
        {
            AppendActivity("No running acclient.exe process found.");
            RefreshSessionState();
            return;
        }

        string enginePath = EnginePathTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(enginePath))
            enginePath = _injector.TryResolveEnginePath(null) ?? string.Empty;

        if (string.IsNullOrWhiteSpace(enginePath))
        {
            AppendActivity("Inject blocked: engine path is missing and no default engine DLL could be resolved.");
            return;
        }

        SaveRuntimePaths();
        SaveLaunchBehavior();

        try
        {
            SetOperationState(true);
            IReadOnlyList<Process> orderedTargets = targets.OrderBy(process => process.Id).ToList();
            bool injectAllSessions = InjectAllRunningAcCheckBox.IsChecked == true;
            IReadOnlyList<Process> targetsToInject = injectAllSessions
                ? orderedTargets
                : [orderedTargets[0]];
            AppendActivity(injectAllSessions
                ? $"Applying selected loadout to {targetsToInject.Count} running AC session(s)."
                : $"Applying selected loadout to first running AC session (PID {targetsToInject[0].Id}).");

            foreach (Process target in targetsToInject)
            {
                AppendActivity($"Injecting running AC (PID {target.Id}).");
                InjectionResult result = await Task.Run(() => _injector.InjectIntoProcess(target, enginePath, AppendActivity));
                if (result.Success)
                {
                    _launchedSessionPids.Add(target.Id);
                    AppendActivity($"Injection complete for PID {target.Id}.");
                }
                else
                {
                    AppendActivity($"Injection failed for PID {target.Id}: {result.Summary}");
                }
            }
        }
        catch (Exception ex)
        {
            AppendActivity($"Loadout apply failed: {ex.Message}");
        }
        finally
        {
            SetOperationState(false);
            RefreshSessionState();
        }
    }

    private string GetSelectedLoggingLevel()
    {
        return LoggingLevelComboBox.SelectedItem is ComboBoxItem item
            ? item.Content?.ToString() ?? "Info"
            : "Info";
    }

    private void SetLoggingLevelSelection(string? loggingLevel)
    {
        string desiredLevel = string.IsNullOrWhiteSpace(loggingLevel) ? "Info" : loggingLevel.Trim();
        if (LoggingLevelComboBox.Items == null)
        {
            LoggingLevelComboBox.SelectedIndex = 0;
            return;
        }

        foreach (object? item in LoggingLevelComboBox.Items)
        {
            if (item is not ComboBoxItem comboItem)
                continue;

            string candidate = comboItem.Content?.ToString() ?? string.Empty;
            if (!string.Equals(candidate, desiredLevel, StringComparison.OrdinalIgnoreCase))
                continue;

            LoggingLevelComboBox.SelectedItem = comboItem;
            return;
        }

        // Unknown saved value: fall back to Info (index 3 after Off, Error, Warning), not the quietest level.
        LoggingLevelComboBox.SelectedIndex = 3;
    }

    private void OnPrimarySelectionChanged()
    {
        if (_suppressPrimarySelectionChanged)
            return;

        _settings.SelectedServerProfileId = GetSelectedServer()?.Id ?? string.Empty;
        _settings.SelectedAccountProfileId = GetSelectedAccount()?.Id ?? string.Empty;
        SaveSettings();
        RefreshSummary();

        LaunchServerProfile? server = GetSelectedServer();
        LaunchAccountProfile? account = GetSelectedAccount();
        if (server != null || account != null)
            AppendActivity($"Primary selection: {server?.DisplayName ?? "(no server)"} / {account?.DisplayName ?? "(no account)"}");
    }

    private int FindServerIndex(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return _settings.ServerProfiles.Count > 0 ? 0 : -1;

        int index = _settings.ServerProfiles.FindIndex(profile => string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : (_settings.ServerProfiles.Count > 0 ? 0 : -1);
    }

    private int FindAccountIndex(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return _settings.AccountProfiles.Count > 0 ? 0 : -1;

        int index = _settings.AccountProfiles.FindIndex(profile => string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : (_settings.AccountProfiles.Count > 0 ? 0 : -1);
    }

    private void AppendActivity(string message)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => AppendActivity(message));
            return;
        }

        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _activityItems.Insert(0, line);

        while (_activityItems.Count > 80)
            _activityItems.RemoveAt(_activityItems.Count - 1);
    }

    private void SetOperationState(bool isBusy)
    {
        _operationBusy = isBusy;
        LaunchPrimaryButton.IsEnabled = !isBusy;
        LaunchCheckedTargetsButton.IsEnabled = !isBusy;
        MarkSelectedTargetButton.IsEnabled = !isBusy;
        SavePathsButton.IsEnabled = !isBusy;
        SaveBehaviorButton.IsEnabled = !isBusy;
        InjectRunningAcButton.IsEnabled = !isBusy;
        RefreshSessionsButton.IsEnabled = !isBusy;
        ResetOverlayBarButton.IsEnabled = !isBusy;
    }

    /// <summary>
    /// Recovers an off-screen RynthCore overlay bar by writing the
    /// <c>/rc resetbar</c> engine command to %APPDATA%\RynthCore\dispatch.txt.
    /// Every running RynthCore client watches that file (ChatFileDispatcher)
    /// and routes the line through the same chat-command path a typed command
    /// would take, so each one snaps its bar back to the top-left corner.
    /// The <c>force:</c> prefix makes repeated clicks re-fire even though the
    /// file content is unchanged (the dispatcher dedups otherwise).
    /// </summary>
    private void ResetOverlayBar()
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RynthCore",
                "dispatch.txt");

            string? dir = Path.GetDirectoryName(path);
            if (dir != null)
                Directory.CreateDirectory(dir);

            File.WriteAllText(path,
                "# RynthCore dispatch — written by the launcher's Reset Overlay Bar button.\n" +
                "force:/rc resetbar\n");

            AppendActivity("Reset Overlay Bar: signalled running RynthCore client(s) via dispatch.txt.");
        }
        catch (Exception ex)
        {
            AppendActivity($"Reset Overlay Bar failed: {ex.Message}");
        }
    }

    private void RefreshSessionState()
    {
        Process[] targets;
        try
        {
            // rynth-headless.exe too: its session file would otherwise be deleted as stale every
            // tick, and auto-launch would start the account again and again.
            targets = FindClientProcesses();
        }
        catch (Exception ex)
        {
            AcStatusText.Text = $"Session probe failed: {ex.Message}";
            return;
        }

        HashSet<int> activePids = targets.Select(process => process.Id).ToHashSet();
        // acclient.exe only: window, injection and heartbeat-log steps below.
        Process[] acTargets = AcClientsOnly(targets);
        LaunchContextStore.DeleteStaleProcessFiles(activePids);
        SessionStateStore.DeleteStaleProcessFiles(activePids);
        Dictionary<int, LaunchContextRecord> activeContexts = LaunchContextStore.ReadForActiveProcesses(activePids);
        Dictionary<int, SessionStateRecord> activeSessions = SessionStateStore.ReadForActiveProcesses(activePids);
        DetectQuickExits(activePids);
        // After DetectQuickExits (which asks whether an exited pid was headless): a recycled pid starts fresh.
        _launchedHeadlessPids.RemoveWhere(pid => !activePids.Contains(pid));

        // Audit trail for vanished PIDs: log each removal so a "did the
        // launcher kill this client?" question has a concrete answer. The
        // reaper's own kill site logs separately ("Killing stuck PID …") —
        // a PID's removal line without that preceding kill line means
        // something OTHER than the launcher took it down (AC self-exit,
        // network disconnect, manual close, OS).
        List<int>? departedPids = null;
        foreach (int pid in _launchedSessionPids)
        {
            if (!activePids.Contains(pid))
                (departedPids ??= []).Add(pid);
        }
        if (departedPids != null)
        {
            foreach (int pid in departedPids)
            {
                _launchedSessionPids.Remove(pid);
                AppendActivity($"PID {pid} removed from launcher tracking (process is no longer running).");
                LauncherDiag.Info($"RefreshSessionState: PID {pid} departed; removed from _launchedSessionPids (now {_launchedSessionPids.Count}). _launchedPidInfo entry remains until DetectQuickExits processes it.");
            }
        }

        _windowPositionAppliedPids.RemoveWhere(pid => !activePids.Contains(pid));
        _everLoggedInPids.RemoveWhere(pid => !activePids.Contains(pid));
        foreach (int pid in _baselineWindowRects.Keys.Where(pid => !activePids.Contains(pid)).ToList())
            _baselineWindowRects.Remove(pid);
        foreach (int pid in _windowRestores.Keys.Where(pid => !activePids.Contains(pid)).ToList())
            _windowRestores.Remove(pid);

        // Latch any PID that's currently reporting IsLoggedIn=true. Once
        // latched, the reaper will permanently exempt it — even if some race
        // later flips IsLoggedIn back to false without LogoutAtUtc set.
        foreach (KeyValuePair<int, SessionStateRecord> kvp in activeSessions)
        {
            if (kvp.Value.IsLoggedIn && _everLoggedInPids.Add(kvp.Key))
            {
                AppendActivity($"PID {kvp.Key} ({kvp.Value.AccountName}) logged in — exempted from stuck-client reaper.");
            }
        }

        int headlessCount = targets.Length - acTargets.Length;
        AcStatusText.Text = (targets.Length switch
        {
            0 => "No running AC clients detected.",
            1 => "1 AC client is running.",
            _ => $"{targets.Length} AC clients are running."
        }) + (headlessCount > 0 ? $" ({headlessCount} headless)" : string.Empty);

        _sessionItems.Clear();
        foreach (Process process in targets.OrderBy(p => p.Id))
        {
            // Headless: no window, no D3D device. Listed, but none of the window steps apply.
            if (IsHeadlessPid(process.Id))
            {
                _sessionItems.Add($"PID {process.Id}  |  {DescribeSessionOrigin(process, activeContexts, activeSessions)}  |  headless");
                continue;
            }

            bool graphicsReady = _injector.TryDescribeGraphicsReadiness(process, out string status);
            string readiness = graphicsReady ? "graphics ready" : SimplifyReadinessStatus(status);
            string origin = DescribeSessionOrigin(process, activeContexts, activeSessions);
            _sessionItems.Add($"PID {process.Id}  |  {origin}  |  {readiness}");

            if (_settings.OverrideWindowTitle)
                ApplyDesiredWindowTitle(process, activeContexts, activeSessions);

            ApplyOrPersistWindowPlacement(process, graphicsReady, activeContexts, activeSessions);
        }

        if (_sessionItems.Count == 0)
            _sessionItems.Add("No active sessions yet.");

        UpdateLaunchTargetStatuses(targets, activeContexts, activeSessions);
        MaybeQueueAutoLaunch(activeContexts, activeSessions);
        // acclient only: injection, the not-responding window check and the engine heartbeat log
        // mean nothing for rynth-headless (it reconnects by itself; the stop event ends it).
        MaybeAutoInjectExternalClients(acTargets, activePids, activeContexts, activeSessions);
        MaybeKillStuckClients(acTargets, activeContexts, activeSessions);
        MaybeRestartWedgedClients(acTargets, activeContexts, activeSessions);

        // Dispose this tick's Process handles. FindTargetProcesses() allocates fresh
        // System.Diagnostics.Process objects (each holds a native OS handle); on the 2s
        // session timer (~thousands of ticks/session) the undisposed handles + finalizable
        // objects accumulate, the UI thread degrades, and the launcher must be restarted
        // (root cause of the ~2.5h unresponsiveness, 2026-06-03). Every sub-call above
        // consumed this array synchronously — UpdateLaunchTargetStatuses received it (no
        // second allocation) — so it is safe to release the handles here.
        foreach (Process p in targets) p.Dispose();
    }

    /// <summary>
    /// Reaps RynthCore-mode acclient.exe processes that we launched but never
    /// reached IsLoggedIn within <see cref="AppSettings.StuckClientTimeoutSeconds"/>.
    /// Catches stuck char-select, patcher hangs, and disconnect/login-error
    /// screens at the front of the launch flow.
    ///
    /// Strict scoping rules — we only kill a process if ALL of the following:
    ///   - The user opted in via <see cref="AppSettings.KillStuckClients"/>
    ///   - We launched it ourselves (PID is in <see cref="_launchedSessionPids"/>)
    ///   - The associated account profile is set to <see cref="InjectionMode.RynthCore"/>
    ///     (Decal-mode clients have no engine = no IsLoggedIn signal, so this
    ///     heuristic would falsely reap working Decal sessions)
    ///   - <see cref="SessionStateRecord.IsLoggedIn"/> is still false
    ///   - Time-since-launch exceeds the configured timeout
    /// Headless clients (rynth-headless) are never passed in (RefreshSessionState filters
    /// them) and their profiles say Headless, not RynthCore.
    /// </summary>
    private void MaybeKillStuckClients(
        IReadOnlyList<Process> targets,
        IReadOnlyDictionary<int, LaunchContextRecord> activeContexts,
        IReadOnlyDictionary<int, SessionStateRecord> activeSessions)
    {
        if (!_settings.KillStuckClients)
        {
            if (!_reaperOffLogged)
            {
                AppendActivity("Stuck-client reaper is OFF (KillStuckClients=false). The launcher will not kill any AC clients this session.");
                _reaperOffLogged = true;
            }
            return;
        }

        int timeoutSeconds = Math.Max(15, _settings.StuckClientTimeoutSeconds);
        TimeSpan timeout = TimeSpan.FromSeconds(timeoutSeconds);
        DateTime nowUtc = DateTime.UtcNow;

        foreach (Process process in targets)
        {
            int pid = process.Id;

            if (!_launchedSessionPids.Contains(pid))
                continue;
            if (!_launchedPidInfo.TryGetValue(pid, out (string AccountKey, DateTime LaunchedAtUtc) info))
                continue;

            // Hard exemption: once this PID has been observed logged in at
            // any point, the reaper never touches it. Protects healthy
            // in-game clients from any session-state race or transient
            // logout signal that might flip IsLoggedIn back to false.
            if (_everLoggedInPids.Contains(pid))
                continue;

            activeSessions.TryGetValue(pid, out SessionStateRecord? sess);

            // Resolve the account profile to gate on injection mode. If we
            // can't find a matching profile, skip rather than kill blindly.
            activeContexts.TryGetValue(pid, out LaunchContextRecord? ctx);
            string? accountName = sess?.AccountName ?? ctx?.AccountName;
            string? serverName = !string.IsNullOrWhiteSpace(sess?.ServerName) ? sess.ServerName : ctx?.ServerName;
            if (string.IsNullOrWhiteSpace(accountName))
                continue;

            LaunchAccountProfile? profile = FindAccountProfile(accountName, serverName);
            if (profile == null || profile.InjectionMode != InjectionMode.RynthCore)
                continue;

            // Two reap conditions, both gated on the same timeout:
            //   1. Pre-login stuck — never reached IsLoggedIn within timeout
            //      since launch (patcher hang, char-select sit, login error)
            //   2. Post-login stuck — was logged in, then logged out (clean
            //      OR server-driven disconnect), and has sat in that state
            //      longer than the timeout (disconnect dialog, char-select)
            string? reason = null;

            if (sess != null && sess.LogoutAtUtc.HasValue && !sess.IsLoggedIn)
            {
                TimeSpan sinceLogout = nowUtc - sess.LogoutAtUtc.Value;
                if (sinceLogout >= timeout)
                    reason = $"logged out / disconnected for {(int)sinceLogout.TotalSeconds}s";
            }
            else if (sess == null || !sess.IsLoggedIn)
            {
                TimeSpan age = nowUtc - info.LaunchedAtUtc;
                if (age >= timeout)
                    reason = $"no login within {timeoutSeconds}s";
            }

            if (reason == null)
                continue;

            // Final safety gate — only kill if the process is genuinely
            // unresponsive. A healthy AC client (even at char-select or on a
            // disconnect dialog) keeps pumping its WndProc, so Process.Responding
            // is true. A truly hung/crashed-but-not-exited client returns
            // false. Without this gate, transient logoff-class signals (which
            // SessionStateRegistry latches into LogoutAtUtc) could cause us to
            // kill an in-game client that the user is actively interacting
            // with — observed in practice.
            bool responding;
            try { process.Refresh(); responding = process.Responding; }
            catch { responding = true; }

            if (responding)
                continue;

            // A client being crash-dumped doesn't answer either; killing it cuts the dump short.
            if (HoldKillForCrashDump(pid, "stuck-client kill"))
                continue;

            try
            {
                AppendActivity($"Killing stuck PID {pid} ({accountName}) — {reason} and window is not responding.");
                EndClientProcess(process, pid);   // a headless client is asked to stop first (never reaches here today)
                _launchedSessionPids.Remove(pid);
                _launchedPidInfo.Remove(pid);

                // Count the reap as a crash. The killed client's uptime exceeds
                // the quick-exit window (StuckClientTimeoutSeconds >= 60s vs
                // 30s), so DetectQuickExits classifies it "normal close" — and
                // auto-launch would relaunch a perpetually-stuck client in an
                // endless kill/relaunch loop, bypassing the circuit breaker.
                string crashKey = BuildAccountKey(accountName, serverName);
                if (!string.IsNullOrEmpty(crashKey))
                {
                    RecordCrash(crashKey);
                    LauncherDiag.Info($"MaybeKillStuckClients: PID {pid} ({accountName}) reaped -> RecordCrash so the relaunch circuit breaker sees it.");
                }
            }
            catch (Exception ex)
            {
                AppendActivity($"Failed to kill stuck PID {pid}: {ex.Message}");
            }
        }
    }

    // ── Wedge auto-restart ─────────────────────────────────────────────────
    // The residual AV class wedges AC's main thread while the engine lives on:
    // the per-PID heartbeat keeps flowing while the main thread is stuck. Detect
    // that from the log tail and kill the client; the existing crash-relaunch
    // machinery (RecordCrash + AutoLaunch) brings it back. Containment, not cure:
    // an overnight soak loses ~2 minutes to a wedge instead of the rest of the
    // night.
    //
    // Signals (minimized windows are exempted first via IsIconic — a backgrounded
    // client throttles its fps on its own): a confirmed permanent main-thread
    // wedge (HangPermRegex — beats off the UseTime tick, strongest), fps=0 on a
    // VISIBLE in-world client (render dead; a healthy visible client renders tens
    // of fps — verified live), plug=0 (plugin pump dead), qd backing up (main
    // thread not draining the marshalled-action queue), and a stale heartbeat
    // file.
    private readonly Dictionary<int, DateTime> _wedgeZeroFpsSinceUtc = new();
    // PIDs held off for a full-memory dump, and since when (DumpInProgressRule.Decide caps the
    // hold at MaxHold). Kept while held or expired, dropped once no dump is in progress.
    private readonly Dictionary<int, DateTime> _dumpHoldSinceUtc = new();

    // Last write of the newest acclient_oom_<pid>_*.dmp in the shared Dumps folder, or null.
    private static DateTime? NewestOomDumpWriteUtc(int pid)
    {
        try
        {
            string dir = System.IO.Path.Combine(RynthCore.Install.RynthInstallPaths.CoreDir, "Dumps");
            if (!Directory.Exists(dir)) return null;
            DateTime? newest = null;
            foreach (string f in Directory.EnumerateFiles(dir, $"{RynthCore.Install.DumpInProgressRule.OomDumpPrefix}{pid}_*.dmp"))
            {
                DateTime w = File.GetLastWriteTimeUtc(f);
                if (newest == null || w > newest) newest = w;
            }
            return newest;
        }
        catch { return null; }
    }
    // Clients a crash dump is being written of (CrashDumpWriterRule): pid -> when the launcher
    // first held its kill, when a writer naming it was last seen, and whether an expiry was logged.
    private readonly Dictionary<int, (DateTime Since, DateTime LastSeen, bool ExpiryLogged)> _crashDumpHolds = new();
    private Dictionary<int, string> _crashDumpWriters = new();
    private DateTime _crashDumpWritersAtUtc;

    /// <summary>
    /// True when the launcher must not kill <paramref name="pid"/> now (<paramref name="kill"/> names
    /// the kill, for the log): createdump.exe or WerFault.exe is writing a crash dump of it, or was
    /// within the last <see cref="CrashDumpWriterRule.Grace"/> (CrashDumpWriterRule; at most
    /// MaxHold in all). Looks for the writers only when a kill is due, at most once a second.
    /// </summary>
    private bool HoldKillForCrashDump(int pid, string kill)
    {
        DateTime now = DateTime.UtcNow;
        if (now - _crashDumpWritersAtUtc > TimeSpan.FromSeconds(1))
        {
            _crashDumpWriters = CrashDumpWriters.Find();
            _crashDumpWritersAtUtc = now;
            // Holds of clients long gone (exited mid-hold) are dropped; a recycled pid starts fresh.
            foreach (int stale in _crashDumpHolds.Where(kv => now - kv.Value.LastSeen > CrashDumpWriterRule.MaxHold + CrashDumpWriterRule.Grace)
                         .Select(kv => kv.Key).ToList())
                _crashDumpHolds.Remove(stale);
        }

        bool running = _crashDumpWriters.TryGetValue(pid, out string? writer);
        bool known = _crashDumpHolds.TryGetValue(pid, out var held);
        var hold = CrashDumpWriterRule.Decide(running, known ? held.LastSeen : null, known ? held.Since : null, now);
        if (hold == CrashDumpWriterRule.Hold.None)
        {
            if (known)
            {
                _crashDumpHolds.Remove(pid);
                LauncherDiag.Info($"CRASHDUMP: PID {pid} kill hold ended (no dump writer for {CrashDumpWriterRule.Grace.TotalSeconds:0} s) - kill checks resume.");
            }
            return false;
        }

        DateTime lastSeen = running ? now : held.LastSeen;
        if (!known)
        {
            _crashDumpHolds[pid] = (now, lastSeen, false);
            AppendActivity($"PID {pid}: {writer} is writing a crash dump of it - {kill} held off until it ends, then {CrashDumpWriterRule.Grace.TotalSeconds:0} s more.");
            LauncherDiag.Info($"CRASHDUMP: PID {pid} {kill} held off - {writer} is writing a crash dump of it (hold lasts while it runs + {CrashDumpWriterRule.Grace.TotalSeconds:0} s, at most {CrashDumpWriterRule.MaxHold.TotalMinutes:0} min).");
            return true;
        }
        if (hold == CrashDumpWriterRule.Hold.Expired)
        {
            if (!held.ExpiryLogged)
                LauncherDiag.Info($"CRASHDUMP: PID {pid} kill hold expired after {CrashDumpWriterRule.MaxHold.TotalMinutes:0} min - {kill} goes ahead.");
            _crashDumpHolds[pid] = (held.Since, lastSeen, true);
            return false;
        }
        _crashDumpHolds[pid] = (held.Since, lastSeen, held.ExpiryLogged);
        return true;
    }

    // fps (1), plug pump rate (2), login (3), qd = main-thread queue depth (4, optional on older engines).
    private static readonly System.Text.RegularExpressions.Regex WedgeHbRegex =
        new(@"hb #\d+ .*?fps=(\d+) .*?plug=(\d+)/s .*?login=(\d)(?:.*?qd=(\d+))?", System.Text.RegularExpressions.RegexOptions.Compiled);
    // The engine's MainThreadHangWatchdog confirms a permanent main-thread wedge
    // ("still hung after" with no later RECOVERED). Beats off the UseTime tick,
    // so it works in every overlay config — the strongest cross-config signal.
    private static readonly System.Text.RegularExpressions.Regex HangPermRegex =
        new(@"still hung after", System.Text.RegularExpressions.RegexOptions.Compiled);
    // Item-action hard-lock while STILL rendering (fps>0): RynthAi can't re-enter
    // combat mode for a long stretch (AC refuses all item actions on an orphaned
    // pending-action state; only relog clears it). The plugin logs the stuck
    // duration; >5 min is unambiguous. Catches the wedge phase the fps=0 rule
    // misses — observed 2026-06-14: fps=60, item-locked for 2h before fps→0.
    private static readonly System.Text.RegularExpressions.Regex StanceStuckRegex =
        new(@"ChangeCombatMode\([^)]*\) retry #\d+ \(stuck (\d+)s", System.Text.RegularExpressions.RegexOptions.Compiled);
    // Can't enter combat mode for this long while in-world = item-action hard-lock.
    private const int StanceStuckWedgeSeconds = 300;

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    private void MaybeRestartWedgedClients(
        Process[] targets,
        IReadOnlyDictionary<int, LaunchContextRecord> activeContexts,
        IReadOnlyDictionary<int, SessionStateRecord> activeSessions)
    {
        if (!_settings.AutoRestartWedgedClients)
            return;

        DateTime nowUtc = DateTime.UtcNow;
        // Drop bookkeeping for exited PIDs so a recycled PID starts fresh.
        foreach (int stale in _wedgeZeroFpsSinceUtc.Keys.ToList())
            if (!targets.Any(p => { try { return p.Id == stale; } catch { return false; } }))
                _wedgeZeroFpsSinceUtc.Remove(stale);

        foreach (Process process in targets)
        {
            int pid;
            try { pid = process.Id; } catch { continue; }

            // Scope: only clients we launched that reached in-world at least
            // once (pre-login problems belong to the stuck-client reaper).
            if (!_launchedSessionPids.Contains(pid) || !_everLoggedInPids.Contains(pid))
                continue;

            // Minimized windows are exempt — some drivers stop presenting when
            // iconic, which would read as fps=0 on a healthy client.
            try
            {
                IntPtr hwnd = process.MainWindowHandle;
                if (hwnd != IntPtr.Zero && IsIconic(hwnd))
                {
                    _wedgeZeroFpsSinceUtc.Remove(pid);
                    continue;
                }
            }
            catch { continue; }

            // Tail the per-PID log for the last heartbeat line. Cheap: seek to
            // the end, read ~2KB, FileShare-tolerant of the engine's writer.
            string logPath = System.IO.Path.Combine(RynthCore.Install.RynthInstallPaths.CoreLogsDir, $"RynthCore.{pid}.log");
            string tail;
            try
            {
                var fi = new FileInfo(logPath);
                if (!fi.Exists) { _wedgeZeroFpsSinceUtc.Remove(pid); continue; }
                // Heartbeat itself dead >120s while the process lives and the
                // char was in-world = the engine died inside the client. Treat
                // as wedged too (same containment applies).
                bool hbStale = (DateTime.Now - fi.LastWriteTime).TotalSeconds > 120;

                using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                // 16KB tail: enough to contain the every-30s combat-mode-stuck
                // line for the item-lock detector, still cheap per poll.
                long want = Math.Min(16384, fs.Length);
                fs.Seek(-want, SeekOrigin.End);
                byte[] buf = new byte[want];
                int read = fs.Read(buf, 0, (int)want);
                tail = System.Text.Encoding.UTF8.GetString(buf, 0, read);

                // The engine's OUT OF MEMORY full-memory dump suspends every thread for minutes:
                // the log goes silent and the main thread looks hung, but killing now would cut
                // the dump short. Hold off both kills below while it is being written
                // (DumpInProgressRule); the restart for the box that follows is unchanged.
                DateTime holdNow = DateTime.UtcNow;
                DateTime? heldSince = _dumpHoldSinceUtc.TryGetValue(pid, out DateTime hs) ? hs : null;
                var hold = RynthCore.Install.DumpInProgressRule.Decide(tail, fi.LastWriteTimeUtc, NewestOomDumpWriteUtc(pid), heldSince, holdNow);
                if (hold == RynthCore.Install.DumpInProgressRule.Hold.Holding)
                {
                    if (heldSince == null)
                    {
                        _dumpHoldSinceUtc[pid] = holdNow;
                        LauncherDiag.Info($"WEDGE: PID {pid} is writing a full-memory dump - kill held off until it ends (at most {RynthCore.Install.DumpInProgressRule.MaxHold.TotalMinutes:0} min).");
                    }
                    continue;
                }
                if (hold == RynthCore.Install.DumpInProgressRule.Hold.Expired)
                {
                    if (heldSince is DateTime heldAt && heldAt != DateTime.MinValue)
                    {
                        _dumpHoldSinceUtc[pid] = DateTime.MinValue;   // logged; stays expired until the signs clear
                        LauncherDiag.Info($"WEDGE: PID {pid} dump hold expired after {RynthCore.Install.DumpInProgressRule.MaxHold.TotalMinutes:0} min - kill checks resume.");
                    }
                }
                else
                {
                    _dumpHoldSinceUtc.Remove(pid);
                }

                if (hbStale)
                {
                    KillWedgedClient(process, pid, activeContexts, activeSessions,
                        $"engine heartbeat silent for >120s while process alive");
                    continue;
                }
            }
            catch { continue; }

            // STRONGEST signal: the engine's own watchdog confirmed a permanent
            // main-thread wedge. Act immediately — it already held ~10s+ before
            // logging this, and it works even when fps is structurally 0.
            int hangIdx = tail.LastIndexOf("MAIN THREAD HANG DETECTED", StringComparison.Ordinal);
            if (hangIdx >= 0)
            {
                string afterHang = tail.Substring(hangIdx);
                if (HangPermRegex.IsMatch(afterHang)
                    && !afterHang.Contains("MAIN THREAD RECOVERED", StringComparison.Ordinal))
                {
                    KillWedgedClient(process, pid, activeContexts, activeSessions,
                        "in-process MainThreadHangWatchdog confirmed a permanent main-thread wedge");
                    continue;
                }
            }

            var matches = WedgeHbRegex.Matches(tail);
            if (matches.Count == 0) continue;
            var last = matches[matches.Count - 1];
            bool inWorld = last.Groups[3].Value == "1";

            if (!inWorld)
            {
                _wedgeZeroFpsSinceUtc.Remove(pid);
                continue;
            }

            // ── Item-action hard-lock while the engine threads keep ticking ──
            // AC refuses every item action (combat-mode, casts, opens) on an
            // orphaned pending-action state. The plugin's combat-mode retry
            // counter is the proof; >5 min stuck = a relog-only lock the engine
            // busy watchdog can't clear. No hold timer — the logged duration is
            // already the dwell.
            int stanceStuckSec = 0;
            foreach (System.Text.RegularExpressions.Match sm in StanceStuckRegex.Matches(tail))
                if (int.TryParse(sm.Groups[1].Value, out int s) && s > stanceStuckSec)
                    stanceStuckSec = s;
            if (stanceStuckSec >= StanceStuckWedgeSeconds)
            {
                KillWedgedClient(process, pid, activeContexts, activeSessions,
                    $"combat-mode stuck {stanceStuckSec}s with login=1 (item-action hard-lock — heartbeat alive)");
                continue;
            }

            // ── render dead OR pump dead OR queue backing up = wedged ─────────
            // Minimized clients were exempted above, so fps=0 here = a VISIBLE
            // client that stopped rendering (a healthy visible client renders
            // tens of fps); plug=0 = the plugin pump itself died.
            bool fpsZero  = last.Groups[1].Value == "0";
            bool pumpDead = last.Groups[2].Value == "0";
            // No queue rule: the engine's qd (qdrop since 2026-10-01) is a running total of
            // DROPPED actions, not a queue depth, so "qd > 2000 for 90 s" killed healthy
            // 60 fps clients after a long session (a tester's, 2026-10-01). A truly wedged
            // main thread stops rendering, which fpsZero already catches.
            string? wedgeReason =
                fpsZero ? "fps=0 with login=1 (render dead)"
                : pumpDead ? "plugin pump dead (plug=0/s) with login=1"
                : null;

            if (wedgeReason == null)
            {
                _wedgeZeroFpsSinceUtc.Remove(pid);
                continue;
            }

            if (!_wedgeZeroFpsSinceUtc.TryGetValue(pid, out DateTime since))
            {
                _wedgeZeroFpsSinceUtc[pid] = nowUtc;
                continue;
            }

            int holdSec = Math.Max(30, _settings.WedgeRestartSeconds);
            if ((nowUtc - since).TotalSeconds < holdSec)
                continue;

            KillWedgedClient(process, pid, activeContexts, activeSessions,
                $"{wedgeReason} for >{holdSec}s (heartbeat alive — AC main thread/pump wedged)");
        }
    }

    private void KillWedgedClient(
        Process process, int pid,
        IReadOnlyDictionary<int, LaunchContextRecord> activeContexts,
        IReadOnlyDictionary<int, SessionStateRecord> activeSessions,
        string reason)
    {
        // Every wedge kill goes through here. While a crash dump of the client is written the
        // wedge signs stay (silent log, fps=0): held, the next poll asks again; the fps timer is
        // kept, so a client still wedged once the hold ends is restarted at once.
        if (HoldKillForCrashDump(pid, "wedge restart"))
            return;

        _wedgeZeroFpsSinceUtc.Remove(pid);

        activeSessions.TryGetValue(pid, out SessionStateRecord? sess);
        activeContexts.TryGetValue(pid, out LaunchContextRecord? ctx);
        string? accountName = sess?.AccountName ?? ctx?.AccountName;
        string? serverName = !string.IsNullOrWhiteSpace(sess?.ServerName) ? sess.ServerName : ctx?.ServerName;

        try
        {
            AppendActivity($"Wedge restart: killing PID {pid} ({accountName ?? "?"}) — {reason}."
                + (_settings.AutoLaunch ? " Auto-launch will relaunch it." : " AutoLaunch is OFF — enable it for automatic relaunch."));
            LauncherDiag.Info($"WEDGE: PID {pid} ({accountName ?? "?"}) killed — {reason}");
            EndClientProcess(process, pid);   // a headless client is asked to stop first (never reaches here today)
            _launchedSessionPids.Remove(pid);
            _launchedPidInfo.Remove(pid);

            // Count it as a crash so the relaunch circuit breaker sees a
            // repeatedly-wedging account and stops the loop.
            if (!string.IsNullOrWhiteSpace(accountName))
            {
                string key = BuildAccountKey(accountName, serverName);
                if (!string.IsNullOrEmpty(key))
                    RecordCrash(key);
            }
        }
        catch (Exception ex)
        {
            AppendActivity($"Wedge restart: failed to kill PID {pid}: {ex.Message}");
        }
    }

    /// <summary>
    /// Watches each polling tick for acclient.exe processes that the launcher
    /// did NOT start (e.g., spawned by Decal/UBLoader/ThwargLauncher) and
    /// auto-injects RynthCore once the client has been graphics-ready for
    /// <see cref="AutoInjectStableReadyWindow"/> consecutive seconds. The
    /// stable window gives the foreign launcher time to install its own
    /// EndScene/WndProc hooks before we detour on top.
    /// </summary>
    private void MaybeAutoInjectExternalClients(
        Process[] targets,
        HashSet<int> activePids,
        IReadOnlyDictionary<int, LaunchContextRecord> activeContexts,
        IReadOnlyDictionary<int, SessionStateRecord> activeSessions)
    {
        // Drop bookkeeping for processes that have exited so a future PID
        // recycle doesn't inherit our previous decision.
        foreach (int pid in _autoInjectFirstReadyAtUtc.Keys.ToList())
        {
            if (!activePids.Contains(pid))
                _autoInjectFirstReadyAtUtc.Remove(pid);
        }
        _autoInjectAttemptedPids.RemoveWhere(pid => !activePids.Contains(pid));
        _autoInjectInFlightPids.RemoveWhere(pid => !activePids.Contains(pid));

        if (!_settings.WatchForAcStart || _operationBusy)
            return;

        if (!GetSelectedPluginIds().Contains("rynthcore-engine", StringComparer.OrdinalIgnoreCase))
            return;

        string enginePath = EnginePathTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(enginePath))
            enginePath = _injector.TryResolveEnginePath(null) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(enginePath))
            return;

        DateTime nowUtc = DateTime.UtcNow;

        foreach (Process target in targets)
        {
            int pid = target.Id;

            if (_autoInjectAttemptedPids.Contains(pid) || _autoInjectInFlightPids.Contains(pid))
                continue;

            // Skip clients tied to this launcher — manual / on-launch inject
            // owns those, the watcher only handles foreign processes.
            if (_launchedSessionPids.Contains(pid) ||
                activeContexts.ContainsKey(pid) ||
                activeSessions.ContainsKey(pid))
            {
                continue;
            }

            // Already injected (manually, by another launcher instance, or
            // via reload) — don't re-inject.
            if (_injector.IsRynthCoreLoaded(target))
            {
                _autoInjectAttemptedPids.Add(pid);
                continue;
            }

            if (!_injector.TryDescribeGraphicsReadiness(target, out _))
            {
                _autoInjectFirstReadyAtUtc.Remove(pid);
                continue;
            }

            if (!_autoInjectFirstReadyAtUtc.TryGetValue(pid, out DateTime firstReadyUtc))
            {
                _autoInjectFirstReadyAtUtc[pid] = nowUtc;
                string? decalModule = _injector.TryDetectDecalStack(target);
                AppendActivity(decalModule != null
                    ? $"Auto-inject: external AC PID {pid} detected with {decalModule} loaded — waiting {AutoInjectStableReadyWindow.TotalSeconds:0}s for stable readiness."
                    : $"Auto-inject: external AC PID {pid} detected — waiting {AutoInjectStableReadyWindow.TotalSeconds:0}s for stable readiness.");
                continue;
            }

            if (nowUtc - firstReadyUtc < AutoInjectStableReadyWindow)
                continue;

            _autoInjectAttemptedPids.Add(pid);
            _autoInjectInFlightPids.Add(pid);
            _ = AutoInjectExternalClientAsync(pid, enginePath);
        }
    }

    private async Task AutoInjectExternalClientAsync(int pid, string enginePath)
    {
        try
        {
            AppendActivity($"Auto-inject: starting injection into external AC PID {pid}.");
            // Re-open the process by PID inside the task. The caller's Process
            // array is disposed at the end of the same session tick, which races
            // this async injection: Dispose() resets the cached PID, so reading
            // target.Id/.ProcessName inside InjectIntoProcess threw
            // InvalidOperationException — and since the PID was already in
            // _autoInjectAttemptedPids it was never retried (auto-inject was
            // broken-to-flaky since the 0cb8f76 dispose fix).
            InjectionResult result = await Task.Run(() =>
            {
                using Process target = Process.GetProcessById(pid);
                return _injector.InjectIntoProcess(
                    target,
                    enginePath,
                    line => Dispatcher.UIThread.Post(() => AppendActivity(line)));
            });

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (result.Success)
                {
                    _launchedSessionPids.Add(pid);
                    AppendActivity($"Auto-inject: success for external PID {pid}.");
                }
                else
                {
                    AppendActivity($"Auto-inject: failed for PID {pid}: {result.Summary}");
                }
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                AppendActivity($"Auto-inject: exception for PID {pid}: {ex.Message}"));
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => _autoInjectInFlightPids.Remove(pid));
        }
    }

    private static string SimplifyReadinessStatus(string status)
    {
        const string Prefix = "Auto-inject wait: ";
        if (status.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            status = status[Prefix.Length..];

        return status.TrimEnd('.');
    }

    private string BuildMaskedLaunchPreview(LaunchServerProfile server, LaunchAccountProfile account)
    {
        try
        {
            // Headless: no acclient command line; rynth-headless gets a run config (no password in it).
            if (account.InjectionMode == InjectionMode.Headless)
            {
                if (server.Emulator == AcEmulatorKind.Gdle)
                    return $"Headless runs on ACE servers only, and {server.DisplayName} is a GDLE server.";
                if (AcLaunchArgumentBuilder.NeedsPassword(account))
                    return AcLaunchArgumentBuilder.MissingPasswordMessage(account.AccountName);
                return $"{HeadlessLaunch.ExeName} " + string.Join(" ", HeadlessLaunch.BuildArguments(
                    HeadlessLaunch.ConfigPath(AppSettingsStore.SettingsDirectoryPath, account.Id),
                    HeadlessLaunch.AcFolderFor(AcClientPathTextBox.Text)).Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
            }

            // Never decrypts: a saved password shows as the mask. Extra Launch Arguments follow,
            // as they do at launch.
            string masked = _launchArgumentBuilder.BuildMaskedArguments(server, account);
            if (AcLaunchArgumentBuilder.NeedsPassword(account))
                return masked;   // the message saying why, not a command line
            return AcLaunchArgumentBuilder.ExtraArgumentsProblem(LaunchArgumentsTextBox.Text, out string extra) is string problem
                ? problem
                : AcLaunchArgumentBuilder.WithExtraArguments(masked, extra);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private HashSet<string> GetActiveAccountKeys()
    {
        Process[] targets = FindClientProcesses();
        HashSet<int> activePids = targets.Select(process => process.Id).ToHashSet();

        // Processes whose window is gone but whose OS process is still alive
        // (slow native shutdown, crash dump collection, etc.) should not block
        // relaunch for their account. Only applies to PIDs that completed at
        // least one login — startup-phase clients keep blocking normally.
        // Headless clients never have a window: they count while they run.
        foreach (Process p in targets)
        {
            if (!_everLoggedInPids.Contains(p.Id) || IsHeadlessPid(p.Id))
                continue;
            try
            {
                p.Refresh();
                if (p.MainWindowHandle == IntPtr.Zero)
                {
                    activePids.Remove(p.Id);
                    LauncherDiag.Info($"GetActiveAccountKeys: PID {p.Id} window is gone (process still alive) — treating as departed for account-key purposes.");
                }
            }
            catch { }
        }

        LaunchContextStore.DeleteStaleProcessFiles(activePids);
        SessionStateStore.DeleteStaleProcessFiles(activePids);

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (LaunchContextRecord context in LaunchContextStore.ReadForActiveProcesses(activePids).Values)
        {
            string key = BuildAccountKey(context.AccountName, context.ServerName);
            if (!string.IsNullOrWhiteSpace(key))
                keys.Add(key);
        }

        foreach (SessionStateRecord session in SessionStateStore.ReadForActiveProcesses(activePids).Values)
        {
            string key = BuildAccountKey(session.AccountName, session.ServerName);
            if (!string.IsNullOrWhiteSpace(key))
                keys.Add(key);
        }

        return keys;
    }

    private string DescribeSessionOrigin(
        Process process,
        IReadOnlyDictionary<int, LaunchContextRecord> activeContexts,
        IReadOnlyDictionary<int, SessionStateRecord> activeSessions)
    {
        if (activeSessions.TryGetValue(process.Id, out SessionStateRecord? session))
        {
            string account = string.IsNullOrWhiteSpace(session.AccountName) ? "unknown account" : session.AccountName;
            string server = string.IsNullOrWhiteSpace(session.ServerName) ? "unknown server" : session.ServerName;
            string character = !string.IsNullOrWhiteSpace(session.CharacterName) ? session.CharacterName
                : !string.IsNullOrWhiteSpace(session.TargetCharacter) ? session.TargetCharacter
                : "character pending";
            string elapsed = FormatElapsed(DateTime.UtcNow - (session.LoginCompletedAtUtc ?? session.LaunchStartedAtUtc));
            return $"{account} [{server}] / {character} / online {elapsed}";
        }

        if (activeContexts.TryGetValue(process.Id, out LaunchContextRecord? context))
        {
            string account = string.IsNullOrWhiteSpace(context.AccountName) ? "unknown account" : context.AccountName;
            string server = string.IsNullOrWhiteSpace(context.ServerName) ? "unknown server" : context.ServerName;
            string character = string.IsNullOrWhiteSpace(context.TargetCharacter) ? "character pending" : context.TargetCharacter;
            string elapsed = FormatElapsed(DateTime.UtcNow - GetProcessStartTimeUtc(process));
            return $"{account} [{server}] / {character} / starting {elapsed}";
        }

        return _launchedSessionPids.Contains(process.Id) ? "launched here" : "external";
    }

    // Account identity for running-client detection, crash history and prefs
    // snapshots. The same account name can exist on several servers (e.g.
    // "Buffi" on ACEmulator and on RynthAce), so the key is NAME@SERVER. A
    // session/context with no server name yields "NAME@", which
    // IsAccountKeyActive treats as matching any server for that name.
    private const char AccountKeyServerSeparator = '@';

    private static string BuildAccountKey(string? accountName, string? serverName) =>
        string.IsNullOrWhiteSpace(accountName)
            ? string.Empty
            : $"{accountName.Trim().ToUpperInvariant()}{AccountKeyServerSeparator}{(serverName ?? string.Empty).Trim().ToUpperInvariant()}";

    private string BuildAccountKey(LaunchAccountProfile account) =>
        BuildAccountKey(account.AccountName, ResolveServerForAccount(account)?.Name);

    private static bool IsAccountKeyActive(HashSet<string> activeKeys, string accountKey)
    {
        if (activeKeys.Contains(accountKey))
            return true;

        // A running client whose server is unknown blocks every server for that
        // name, and a profile with no server is blocked by that name anywhere.
        int sep = accountKey.IndexOf(AccountKeyServerSeparator);
        if (sep < 0)
            return false;

        string namePrefix = accountKey[..(sep + 1)];
        if (activeKeys.Contains(namePrefix))
            return true;

        return sep == accountKey.Length - 1 &&
               activeKeys.Any(k => k.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Finds the account profile for a running client. Matches on account name
    /// AND server, because two profiles can share an account name on different
    /// servers. With no server name, falls back to the first name match.
    /// </summary>
    private LaunchAccountProfile? FindAccountProfile(string? accountName, string? serverName)
    {
        if (string.IsNullOrWhiteSpace(accountName))
            return null;

        string name = accountName.Trim();
        List<LaunchAccountProfile> byName = _settings.AccountProfiles
            .Where(a => string.Equals(a.AccountName?.Trim(), name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (byName.Count == 0)
            return null;

        if (string.IsNullOrWhiteSpace(serverName))
            return byName[0];

        string server = serverName.Trim();
        LaunchAccountProfile? exact = byName.FirstOrDefault(a =>
            string.Equals(ResolveServerForAccount(a)?.Name?.Trim(), server, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return exact;

        // Server name not among the saved server profiles (renamed or removed):
        // nothing better to match on, so keep the old name-only behaviour.
        bool knownServer = _settings.ServerProfiles.Any(sp =>
            string.Equals(sp.Name?.Trim(), server, StringComparison.OrdinalIgnoreCase));
        return knownServer ? null : byName[0];
    }

    /// <summary>
    /// Walks <see cref="_launchedPidInfo"/> and counts any client that exited
    /// within <see cref="CrashQuickExitWindow"/> as a crash, feeding the
    /// auto-relaunch circuit breaker. Long-lived sessions that exit normally
    /// (user closed the window after playing) are silently dropped.
    /// </summary>
    /// <summary>
    /// Subscribe to <see cref="Process.Exited"/> for a launched PID so the
    /// launcher snaps to attention the instant the kernel signals the process
    /// handle — no need to wait for the next 2 s session-poll tick. Combined
    /// with the existing poll, the effective close-detection latency drops
    /// from "up to 2 s + WER duration" to "WER duration + scheduler hop".
    ///
    /// Idempotent: safe to call twice for the same PID (no-op on the second).
    /// Tolerant of GetProcessById failing (race window where the process
    /// already vanished before we got here — the existing poll catches it).
    /// </summary>
    private void RegisterPidExitWatch(int pid)
    {
        if (_exitWatchers.ContainsKey(pid))
            return;
        try
        {
            Process proc = Process.GetProcessById(pid);
            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) =>
            {
                // Fires on a thread-pool thread when the kernel signals the
                // process handle. Marshal to the Avalonia UI thread because
                // RefreshSessionState mutates UI-bound state (status text,
                // session list ItemsSource).
                try { Dispatcher.UIThread.Post(RefreshSessionState); }
                catch { /* dispatcher tearing down at app exit — ignore */ }
            };
            _exitWatchers[pid] = proc;

            // Race-window check: if the process already exited between
            // launch and our subscribe, the event won't fire. Snap once.
            if (proc.HasExited)
                Dispatcher.UIThread.Post(RefreshSessionState);
        }
        catch (Exception ex)
        {
            // Could be ArgumentException (no such PID) or Win32Exception
            // (access denied). Either way: the 2 s poll covers it.
            LauncherDiag.Info($"RegisterPidExitWatch: GetProcessById({pid}) failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void DetectQuickExits(HashSet<int> activePids)
    {
        if (_launchedPidInfo.Count == 0)
            return;

        DateTime nowUtc = DateTime.UtcNow;
        List<int>? toRemove = null;
        foreach (KeyValuePair<int, (string AccountKey, DateTime LaunchedAtUtc)> entry in _launchedPidInfo)
        {
            if (activePids.Contains(entry.Key))
                continue;

            (toRemove ??= []).Add(entry.Key);
            TimeSpan uptime = nowUtc - entry.Value.LaunchedAtUtc;
            if (uptime < CrashQuickExitWindow && !string.IsNullOrEmpty(entry.Value.AccountKey))
            {
                RecordCrash(entry.Value.AccountKey);
                AppendActivity($"Account '{entry.Value.AccountKey}' client (PID {entry.Key}) exited after {uptime.TotalSeconds:F0}s — counted as crash.");
                int hist = _crashHistoryByAccount.TryGetValue(entry.Value.AccountKey, out List<DateTime>? h) ? h.Count : 0;
                LauncherDiag.Info($"DetectQuickExits: PID {entry.Key} ({entry.Value.AccountKey}) uptime={uptime.TotalSeconds:F1}s -> RecordCrash. crashHist now {hist}.");
                RecoverFailedPrefsSwap(entry.Key, entry.Value.AccountKey, uptime);
            }
            else if (!string.IsNullOrEmpty(entry.Value.AccountKey) && _launchedHeadlessPids.Contains(entry.Key))
            {
                // Headless: no acclient ran, so the live UserPreferences.ini isn't this account's.
                LauncherDiag.Info($"DetectQuickExits: PID {entry.Key} ({entry.Value.AccountKey}) uptime={uptime.TotalSeconds:F1}s -> normal close (headless, no prefs snapshot).");
            }
            else if (!string.IsNullOrEmpty(entry.Value.AccountKey))
            {
                LauncherDiag.Info($"DetectQuickExits: PID {entry.Key} ({entry.Value.AccountKey}) uptime={uptime.TotalSeconds:F1}s -> normal close, snapshotting prefs.");
                // Real play session ended. Snapshot live UserPreferences.ini
                // back to this account's stash so settings the user changed
                // in-game survive into next launch.
                AutoSnapshotUserPrefs(entry.Value.AccountKey);
            }
        }

        if (toRemove != null)
        {
            foreach (int pid in toRemove)
            {
                _launchedPidInfo.Remove(pid);
                _prefsSwapByPid.Remove(pid);
                // Drop the exit-watcher Process handle so the OS can reclaim
                // the kernel object. Exited already fired (or never will),
                // so we no longer need to hold the handle alive.
                if (_exitWatchers.Remove(pid, out Process? watcher))
                {
                    try { watcher.Dispose(); } catch { }
                }
            }
            LauncherDiag.Info($"DetectQuickExits: removed {toRemove.Count} _launchedPidInfo entries; remaining={_launchedPidInfo.Count}.");
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeoutMs, out IntPtr result);

    private const uint WM_SETTEXT = 0x000C;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct WinRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out WinRect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    // Posts the move to the client's thread instead of waiting for it: a plain
    // SetWindowPos on a frozen client's window never returns, and this runs on
    // the launcher's UI thread (2026-10-22: a frozen +Buffy kept the launcher
    // from ever showing its window).
    private const uint SWP_ASYNCWINDOWPOS = 0x4000;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    /// Fallback for when Process.MainWindowHandle returns 0.
    /// Enumerates all top-level windows and returns the handle of the largest
    /// visible one owned by <paramref name="processId"/>.
    private static IntPtr FindLargestVisibleWindowForProcess(int processId)
    {
        IntPtr best = IntPtr.Zero;
        int bestArea = 0;
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd))
                return true;
            // Skip layered windows (RynthAi panel etc.) — we only want the game window.
            if ((GetWindowLong(hWnd, GWL_EXSTYLE) & WS_EX_LAYERED) != 0)
                return true;
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid != (uint)processId)
                return true;
            if (GetWindowRect(hWnd, out WinRect r))
            {
                int area = (r.Right - r.Left) * (r.Bottom - r.Top);
                if (area > bestArea) { bestArea = area; best = hWnd; }
            }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    /// <summary>
    /// On the first tick after AC reaches graphics-ready for a tracked client,
    /// applies the saved window placement for that account. After that, every
    /// tick reads the current rect and persists changes back to the profile so
    /// user-initiated moves/resizes survive restart. Idempotent within a tolerance
    /// to avoid spamming SaveSettings on DWM-frame fudge.
    /// </summary>
    private void ApplyOrPersistWindowPlacement(
        Process process,
        bool graphicsReady,
        IReadOnlyDictionary<int, LaunchContextRecord> activeContexts,
        IReadOnlyDictionary<int, SessionStateRecord> activeSessions)
    {
        string? accountName = null;
        string? serverName = null;
        if (activeSessions.TryGetValue(process.Id, out SessionStateRecord? s))
        {
            accountName = s.AccountName;
            serverName = s.ServerName;
        }
        else if (activeContexts.TryGetValue(process.Id, out LaunchContextRecord? c))
        {
            accountName = c.AccountName;
            serverName = c.ServerName;
        }

        if (string.IsNullOrWhiteSpace(accountName))
            return;

        // Match on account name AND server — two profiles can share an account
        // name on different servers (e.g. "Drakkon" on ACEmulator vs Reefcull).
        LaunchAccountProfile? profile = FindAccountProfile(accountName, serverName);
        if (profile == null)
            return;

        IntPtr hwnd;
        try
        {
            process.Refresh();
            hwnd = process.MainWindowHandle;
        }
        catch
        {
            return;
        }
        // Reject layered windows — the RynthAi floating panel is WS_EX_LAYERED and
        // can win the MainWindowHandle race before the game window is ready.
        // Fall back to EnumWindows, which also filters layered windows, to find
        // the actual game window.
        if (hwnd != IntPtr.Zero && (GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_LAYERED) != 0)
            hwnd = IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
            hwnd = FindLargestVisibleWindowForProcess(process.Id);

        bool hasSaved = profile.WindowX.HasValue && profile.WindowY.HasValue
                        && profile.WindowWidth.HasValue && profile.WindowHeight.HasValue;

        // Only act once AC reports IsLoggedIn — graphicsReady fires while AC
        // is still in its small login window, and AC will resize itself again
        // at character-select / portal-in. SetWindowPos before that point
        // visibly fights AC's own window setup (the "all over the place
        // until login" symptom). Likewise, capturing during login locks in
        // a transient size that gets restored too early next launch.
        bool loggedIn = activeSessions.TryGetValue(process.Id, out SessionStateRecord? sess) && sess.IsLoggedIn;
        if (!loggedIn)
            return;

        if (!_windowPositionAppliedPids.Contains(process.Id))
        {
            _windowPositionAppliedPids.Add(process.Id);

            if (hwnd == IntPtr.Zero)
            {
                LauncherDiag.Info($"WindowPos PID {process.Id} ({accountName}@{serverName}): hwnd=0 — cannot restore or capture.");
                return;
            }

            if (hasSaved)
            {
                SetWindowPos(
                    hwnd,
                    IntPtr.Zero,
                    profile.WindowX!.Value,
                    profile.WindowY!.Value,
                    profile.WindowWidth!.Value,
                    profile.WindowHeight!.Value,
                    SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
                _windowRestores[process.Id] = (DateTime.UtcNow, null, 0);
                LauncherDiag.Info($"WindowPos PID {process.Id} ({accountName}@{serverName}): restored to ({profile.WindowX},{profile.WindowY},{profile.WindowWidth}x{profile.WindowHeight}).");
                return;
            }
            // No saved position — don't capture immediately. The window may still
            // be at character-select/login size. Record the baseline on the next
            // poll and only persist once the user actually moves or resizes it.
        }

        if (hwnd == IntPtr.Zero)
            return;

        if (!GetWindowRect(hwnd, out WinRect rect))
            return;

        int x = rect.Left;
        int y = rect.Top;
        int w = rect.Right - rect.Left;
        int h = rect.Bottom - rect.Top;

        if (w < 200 || h < 200)
            return;

        // Reject windows parked off-screen — AC places the window at (-10000,-10000)
        // during startup before moving it into view. Multi-monitor setups can have
        // negative coords, but never this negative.
        if (x < -2000 || y < -2000)
            return;

        const int Tolerance = 2;

        // On first observation with no saved position, record the current rect as
        // baseline in memory without persisting. Only save once the window moves
        // or resizes away from that baseline — that's user intent, not a transient.
        if (!hasSaved)
        {
            if (!_baselineWindowRects.TryGetValue(process.Id, out var baseline))
            {
                _baselineWindowRects[process.Id] = (x, y, w, h);
                return; // wait for the user to move/resize before saving
            }

            bool movedFromBaseline = Math.Abs(baseline.x - x) > Tolerance
                || Math.Abs(baseline.y - y) > Tolerance
                || Math.Abs(baseline.w - w) > Tolerance
                || Math.Abs(baseline.h - h) > Tolerance;
            if (!movedFromBaseline)
                return;
        }
        else
        {
            bool changed = Math.Abs(profile.WindowX!.Value - x) > Tolerance
                || Math.Abs(profile.WindowY!.Value - y) > Tolerance
                || Math.Abs(profile.WindowWidth!.Value - w) > Tolerance
                || Math.Abs(profile.WindowHeight!.Value - h) > Tolerance;
            if (!changed)
                return;
        }

        // ── Reject AC's character-select / login window ──────────────────────
        // IsLoggedIn latches true for the whole acclient.exe process: it is set
        // once at the first LoginComplete and is deliberately never flipped back
        // at char-select (so the stuck-client reaper can't kill a healthy in-game
        // client — see SessionStateRegistry). So when the player logs a character
        // back out to character-select, AC shrinks its own window to the small
        // login/char-select size while this method still sees loggedIn==true and
        // would happily persist that tiny rect over the real in-game geometry.
        // Next launch then restores the tiny size and the game is unplayable.
        //
        // AC's char-select/login window is *much* smaller than any window a player
        // games in, so treat a capture that collapses the window below half the
        // established in-game area as that transient window and skip it. Growth
        // and moderate resizes (genuine user intent) still persist; a profile
        // whose saved size was already corrupted small self-heals on the first
        // resize back up to a real in-game size.
        int refW = profile.WindowWidth ?? 0;
        int refH = profile.WindowHeight ?? 0;
        if ((refW <= 0 || refH <= 0) && _baselineWindowRects.TryGetValue(process.Id, out var refRect))
        {
            refW = refRect.w;
            refH = refRect.h;
        }
        if (refW > 0 && refH > 0 && (long)w * h * 2L < (long)refW * refH)
        {
            // Shortly after our restore it is AC shrinking the window back while it brings the character in
            // (AcWindowRestoreRule): put the saved window back instead of leaving the game drawn into a small one.
            if (hasSaved && _windowRestores.TryGetValue(process.Id, out var rs)
                && AcWindowRestoreRule.ShouldReapply(DateTime.UtcNow, rs.At, rs.Last, rs.Count, sess?.LogoutAtUtc != null,
                                                     w, h, profile.WindowWidth!.Value, profile.WindowHeight!.Value))
            {
                SetWindowPos(hwnd, IntPtr.Zero, profile.WindowX!.Value, profile.WindowY!.Value,
                             profile.WindowWidth!.Value, profile.WindowHeight!.Value,
                             SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
                _windowRestores[process.Id] = (rs.At, DateTime.UtcNow, rs.Count + 1);
                LauncherDiag.Info($"WindowPos PID {process.Id} ({accountName}@{serverName}): AC shrank it to {w}x{h} after the restore: put back to ({profile.WindowX},{profile.WindowY},{profile.WindowWidth}x{profile.WindowHeight}) (#{rs.Count + 1}).");
                return;
            }
            LauncherDiag.Info($"WindowPos PID {process.Id} ({accountName}@{serverName}): ignored char-select/login shrink ({w}x{h}, <50% of in-game {refW}x{refH}).");
            return;
        }

        _baselineWindowRects[process.Id] = (x, y, w, h);
        profile.WindowX = x;
        profile.WindowY = y;
        profile.WindowWidth = w;
        profile.WindowHeight = h;
        SaveSettings();
        LauncherDiag.Info($"WindowPos PID {process.Id} ({accountName}@{serverName}): saved ({x},{y},{w}x{h}).");
    }

    /// <summary>
    /// Re-applies a launcher-controlled title (e.g. "Buffi / Buffi @ RynthAce")
    /// to a tracked acclient.exe window. AC overwrites its own title back to
    /// "Asheron's Call" periodically, so this fires every <see cref="_sessionTimer"/>
    /// tick. Untracked clients (no LaunchContext / SessionState record) are
    /// left alone — those are foreign launches we don't own naming for.
    /// </summary>
    private static void ApplyDesiredWindowTitle(
        Process process,
        IReadOnlyDictionary<int, LaunchContextRecord> activeContexts,
        IReadOnlyDictionary<int, SessionStateRecord> activeSessions)
    {
        string desired = ComputeDesiredWindowTitle(process.Id, activeContexts, activeSessions);
        if (string.IsNullOrEmpty(desired))
            return;

        IntPtr hwnd;
        try
        {
            process.Refresh();
            hwnd = process.MainWindowHandle;
        }
        catch
        {
            return;
        }

        if (hwnd == IntPtr.Zero)
            return;

        // SetWindowText waits for the client's thread to take WM_SETTEXT, with no
        // time limit, and this runs on the launcher's UI thread every tick: one
        // frozen client froze the launcher (2026-10-22, +Buffy). Reading the title
        // never waits on another process, so skip the send when it already reads
        // right; otherwise give up on a hung or busy window and try next tick.
        string current;
        try { current = process.MainWindowTitle; } catch { current = string.Empty; }
        if (string.Equals(current, desired, StringComparison.Ordinal))
            return;

        SendMessageTimeoutW(hwnd, WM_SETTEXT, IntPtr.Zero, desired, SMTO_ABORTIFHUNG, 250, out _);
    }

    private static string ComputeDesiredWindowTitle(
        int pid,
        IReadOnlyDictionary<int, LaunchContextRecord> activeContexts,
        IReadOnlyDictionary<int, SessionStateRecord> activeSessions)
    {
        string account = string.Empty;
        string server = string.Empty;
        string character = string.Empty;

        if (activeSessions.TryGetValue(pid, out SessionStateRecord? session))
        {
            account = session.AccountName ?? string.Empty;
            server = session.ServerName ?? string.Empty;
            character = !string.IsNullOrWhiteSpace(session.CharacterName)
                ? session.CharacterName
                : (session.TargetCharacter ?? string.Empty);
        }
        else if (activeContexts.TryGetValue(pid, out LaunchContextRecord? context))
        {
            account = context.AccountName ?? string.Empty;
            server = context.ServerName ?? string.Empty;
            character = context.TargetCharacter ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(account))
            return string.Empty;

        var sb = new StringBuilder(account.Trim());
        if (!string.IsNullOrWhiteSpace(character))
            sb.Append(" / ").Append(character.Trim());
        if (!string.IsNullOrWhiteSpace(server))
            sb.Append(" @ ").Append(server.Trim());
        return sb.ToString();
    }

    /// <summary>
    /// Resolves the per-account UserPreferences.ini stash path. If the profile
    /// has an explicit path set, that wins (advanced override). Otherwise the
    /// launcher uses an auto-managed file under %APPDATA%\RynthCore\prefs\
    /// keyed by account name and server ("name@server.ini"), since the same
    /// account name can exist on several servers. The old name-only file
    /// ("name.ini") is copied (not moved) to seed a missing per-server file, so
    /// existing users keep their prefs on every server that shares the name.
    /// Returns empty when the account has no name yet (new profile, can't pick
    /// a filename).
    /// </summary>
    private static string ResolveUserPrefsPath(LaunchAccountProfile account, string? serverName)
    {
        if (!string.IsNullOrWhiteSpace(account.UserPrefsPath))
            return account.UserPrefsPath.Trim();

        string legacyPath = ResolveLegacyUserPrefsPath(account);
        if (string.IsNullOrEmpty(legacyPath))
            return string.Empty;
        if (string.IsNullOrWhiteSpace(serverName))
            return legacyPath;

        string prefsDir = Path.GetDirectoryName(legacyPath)!;
        string scopedPath = Path.Combine(prefsDir, $"{SafePrefsFileName(account.AccountName)}@{SafePrefsFileName(serverName)}.ini");
        if (!File.Exists(scopedPath) && File.Exists(legacyPath))
        {
            try { File.Copy(legacyPath, scopedPath, overwrite: false); }
            catch { return legacyPath; }
        }

        return scopedPath;
    }

    /// <summary>
    /// The account's old name-only stash (%APPDATA%\RynthCore\prefs\name.ini), which seeds a
    /// missing per-server one. Empty when the account has no name yet.
    /// </summary>
    private static string ResolveLegacyUserPrefsPath(LaunchAccountProfile account)
    {
        if (string.IsNullOrWhiteSpace(account.AccountName))
            return string.Empty;

        string appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appdata, "RynthCore", "prefs", $"{SafePrefsFileName(account.AccountName)}.ini");
    }

    private static string SafePrefsFileName(string name) =>
        string.Join("_", name.Trim().Split(Path.GetInvalidFileNameChars()));

    /// <summary>
    /// A launch's UserPreferences.ini swap. <see cref="IsProfileOverride"/>: the stash is a file the
    /// user picked on the profile (never renamed by the launcher). <see cref="LegacyPath"/>: the
    /// account's name-only stash, set aside with a failed per-server one when identical.
    /// </summary>
    private sealed record PrefsSwapLaunch(UserPrefsSwap Swap, long Generation, bool IsProfileOverride, string LegacyPath);

    /// <summary>
    /// Copies the account's saved UserPreferences.ini over the live one (caller holds
    /// _userPrefsSwapLock) and returns what was swapped, or null when nothing was.
    /// </summary>
    private PrefsSwapLaunch? ApplyPrefsSwap(LaunchAccountProfile account, string? serverName, string resolvedPrefsPath)
    {
        UserPrefsSwap? swap = _launchSettings.SwapUserPreferences(resolvedPrefsPath, _settings.AllowMultipleClients, AppendActivity);
        if (swap == null)
            return null;

        long generation = ++_prefsSwapGeneration;
        bool isOverride = !string.IsNullOrWhiteSpace(account.UserPrefsPath);
        LauncherDiag.Info($"PrefsSwap: account='{account.AccountName}' server='{serverName}' gen={generation} override={isOverride} changedLive={UserPrefsSwapRecovery.SwapChangedPrefs(swap)} stash='{swap.StashPath}'");
        return new PrefsSwapLaunch(swap, generation, isOverride, ResolveLegacyUserPrefsPath(account));
    }

    /// <summary>Remembers which swap a launched client started with (no-op without a swap).</summary>
    private void TrackPrefsSwap(int pid, PrefsSwapLaunch? prefsSwap)
    {
        if (prefsSwap != null)
            _prefsSwapByPid[pid] = prefsSwap;
    }

    /// <summary>
    /// A client quit within CrashQuickExitWindow. If it started with a saved UserPreferences.ini
    /// that differs from the live one, that saved copy is the likely cause (the game reads its
    /// display mode from it: "Could not initialize Direct3D"), and it would be swapped in again on
    /// every launch because it is only refreshed after a normal session. So: set the saved copy
    /// aside (renamed, kept), and put the previous live file back when no later launch replaced it.
    /// The next normal session saves a fresh copy.
    /// </summary>
    private void RecoverFailedPrefsSwap(int pid, string accountKey, TimeSpan uptime)
    {
        if (!_prefsSwapByPid.Remove(pid, out PrefsSwapLaunch? launch))
            return;

        if (!UserPrefsSwapRecovery.SwapChangedPrefs(launch.Swap))
        {
            LauncherDiag.Info($"PrefsSwap: PID {pid} ({accountKey}) quick exit, but its stash matched the live file; left alone.");
            return;
        }

        bool restoreLive = launch.Generation == _prefsSwapGeneration;
        try
        {
            UserPrefsRecoveryResult result = UserPrefsSwapRecovery.Recover(
                launch.Swap,
                quarantineStash: !launch.IsProfileOverride,
                restoreLive,
                launch.LegacyPath,
                DateTime.Now);

            LauncherDiag.Info($"PrefsSwap: PID {pid} ({accountKey}) recovery: quarantined='{result.QuarantinedStashPath}' legacy='{result.QuarantinedLegacyPath}' liveRestored={result.LiveRestored}.");

            string stashName = Path.GetFileName(launch.Swap.StashPath);
            string what = launch.IsProfileOverride
                ? $"the UserPreferences file set on its profile ({launch.Swap.StashPath}). Fix or clear that file in the account's settings if launches keep failing"
                : $"this account's saved game settings ({stashName}). They were set aside as {Path.GetFileName(result.QuarantinedStashPath ?? stashName)}, so the next launch uses the current UserPreferences.ini";
            string live = result.LiveRestored
                ? " The previous UserPreferences.ini was put back."
                : " UserPreferences.ini was left as is (another launch has swapped it since).";
            AppendActivity($"Account '{accountKey}' client (PID {pid}) quit after {uptime.TotalSeconds:F0}s using {what}.{live}");
        }
        catch (Exception ex)
        {
            AppendActivity($"Account '{accountKey}': could not undo the UserPreferences.ini swap after a quick exit: {ex.Message}");
        }
    }

    /// <summary>
    /// Copies the current live UserPreferences.ini to the resolved stash for
    /// the named account. Called on tracked-client exit so per-account prefs
    /// stay current without the user clicking anything. Silently no-ops if
    /// AC isn't present or the account can't be resolved.
    /// </summary>
    private void AutoSnapshotUserPrefs(string accountKey)
    {
        if (string.IsNullOrEmpty(accountKey))
            return;

        LaunchAccountProfile? profile = _settings.AccountProfiles.FirstOrDefault(a =>
            string.Equals(BuildAccountKey(a), accountKey, StringComparison.OrdinalIgnoreCase));
        if (profile == null)
            return;

        string stashPath = ResolveUserPrefsPath(profile, ResolveServerForAccount(profile)?.Name);
        if (string.IsNullOrEmpty(stashPath))
            return;

        string livePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Asheron's Call",
            "UserPreferences.ini");
        if (!File.Exists(livePath))
            return;

        try
        {
            string? dir = Path.GetDirectoryName(stashPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            File.Copy(livePath, stashPath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppendActivity($"Auto-snapshot of UserPreferences.ini failed for {profile.AccountName}: {ex.Message}");
        }
    }

    private void RecordCrash(string accountKey)
    {
        if (string.IsNullOrEmpty(accountKey))
            return;

        if (!_crashHistoryByAccount.TryGetValue(accountKey, out List<DateTime>? history))
            _crashHistoryByAccount[accountKey] = history = [];

        history.Add(DateTime.UtcNow);
    }

    private bool IsAccountInCircuitBreaker(string accountKey, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(accountKey))
            return false;

        if (!_crashHistoryByAccount.TryGetValue(accountKey, out List<DateTime>? history) || history.Count == 0)
            return false;

        TimeSpan window = TimeSpan.FromMinutes(Math.Max(1, _settings.CrashRelaunchWindowMinutes));
        history.RemoveAll(t => nowUtc - t > window);
        return history.Count >= Math.Max(1, _settings.CrashRelaunchLimitInWindow);
    }

    private void MaybeLogCircuitBreaker(LaunchAccountProfile account, DateTime nowUtc)
    {
        string accountKey = BuildAccountKey(account);
        if (_crashBreakerLogTimesUtc.TryGetValue(accountKey, out DateTime lastLogUtc) &&
            nowUtc - lastLogUtc < CrashBreakerLogCooldown)
        {
            return;
        }

        _crashBreakerLogTimesUtc[accountKey] = nowUtc;
        int limit = Math.Max(1, _settings.CrashRelaunchLimitInWindow);
        int window = Math.Max(1, _settings.CrashRelaunchWindowMinutes);
        AppendActivity($"Auto launch paused for {account.DisplayName}: {limit} quick exits in {window} min. Manually relaunch to retry.");
    }

    private void MaybeQueueAutoLaunch(
        IReadOnlyDictionary<int, LaunchContextRecord> activeContexts,
        IReadOnlyDictionary<int, SessionStateRecord> activeSessions)
    {
        if (!_settings.AutoLaunch || _operationBusy || _autoLaunchPassInFlight)
            return;

        List<LaunchAccountProfile> candidates = GetAutoLaunchCandidates(activeContexts, activeSessions);
        if (candidates.Count == 0)
            return;

        _ = AutoLaunchMissingTargetsAsync(candidates);
    }

    private List<LaunchAccountProfile> GetAutoLaunchCandidates(
        IReadOnlyDictionary<int, LaunchContextRecord> activeContexts,
        IReadOnlyDictionary<int, SessionStateRecord> activeSessions)
    {
        var candidates = new List<LaunchAccountProfile>();
        DateTime nowUtc = DateTime.UtcNow;

        foreach (LaunchAccountProfile account in GetCheckedLaunchAccounts())
        {
            LaunchServerProfile? server = ResolveServerForAccount(account);
            if (HasTrackedPresence(account, server, activeContexts, activeSessions) || HeadlessAlreadyRunning(account))
                continue;

            // No server profile (deleted, or never picked): never launched on another server.
            if (server == null)
            {
                MaybeLogAutoLaunchNoServer(account, nowUtc);
                continue;
            }

            if (IsServerKnownDown(server))
            {
                MaybeLogAutoLaunchServerDown(account, server, nowUtc);
                continue;
            }

            // Needs the First Login dialog, which only a manual launch shows.
            if (AcLaunchArgumentBuilder.NeedsPassword(account))
            {
                MaybeLogAutoLaunchNeedsPassword(account, nowUtc);
                continue;
            }

            if (_autoLaunchAttemptTimesUtc.TryGetValue(account.Id, out DateTime lastAttemptUtc) &&
                nowUtc - lastAttemptUtc < AutoLaunchCooldown)
            {
                continue;
            }

            string accountKey = BuildAccountKey(account.AccountName, server?.Name);
            if (IsAccountInCircuitBreaker(accountKey, nowUtc))
            {
                MaybeLogCircuitBreaker(account, nowUtc);
                continue;
            }

            _autoLaunchAttemptTimesUtc[account.Id] = nowUtc;
            candidates.Add(account);
        }

        return candidates;
    }

    private async Task AutoLaunchMissingTargetsAsync(IReadOnlyList<LaunchAccountProfile> accounts)
    {
        if (accounts.Count == 0)
            return;

        _autoLaunchPassInFlight = true;
        try
        {
            string label = accounts.Count == 1
                ? $"auto launch for {accounts[0].DisplayName}"
                : $"auto launch for {accounts.Count} checked targets";
            AppendActivity($"Preparing {label}.");
            await LaunchAccountsAsync(accounts, label, isAutomaticLaunch: true);
        }
        finally
        {
            _autoLaunchPassInFlight = false;
        }
    }

    private void MaybeLogAutoLaunchServerDown(LaunchAccountProfile account, LaunchServerProfile? server, DateTime nowUtc)
    {
        if (_autoLaunchServerDownNoticeTimesUtc.TryGetValue(account.Id, out DateTime lastNoticeUtc) &&
            nowUtc - lastNoticeUtc < AutoLaunchServerDownLogCooldown)
        {
            return;
        }

        _autoLaunchServerDownNoticeTimesUtc[account.Id] = nowUtc;
        AppendActivity($"Auto launch waiting for {account.DisplayName}: {(server?.DisplayName ?? "the selected server")} is reporting down.");
    }

    private void MaybeLogAutoLaunchNeedsPassword(LaunchAccountProfile account, DateTime nowUtc)
    {
        if (_autoLaunchNeedsPasswordNoticeTimesUtc.TryGetValue(account.Id, out DateTime lastNoticeUtc) &&
            nowUtc - lastNoticeUtc < AutoLaunchNeedsPasswordLogCooldown)
        {
            return;
        }

        _autoLaunchNeedsPasswordNoticeTimesUtc[account.Id] = nowUtc;
        AppendActivity($"Auto launch waiting for {account.DisplayName}: {AcLaunchArgumentBuilder.MissingPasswordMessage(account.AccountName)}");
    }

    private void MaybeLogAutoLaunchNoServer(LaunchAccountProfile account, DateTime nowUtc)
    {
        if (_autoLaunchNoServerNoticeTimesUtc.TryGetValue(account.Id, out DateTime lastNoticeUtc) &&
            nowUtc - lastNoticeUtc < AutoLaunchNeedsPasswordLogCooldown)
        {
            return;
        }

        _autoLaunchNoServerNoticeTimesUtc[account.Id] = nowUtc;
        AppendActivity($"Auto launch skipped for {account.DisplayName}: {AccountServerRules.NoServerMessage(account, _settings.ServerProfiles.Count > 0)}");
    }

    private bool IsServerKnownDown(LaunchServerProfile? server)
    {
        if (server == null)
            return false;

        return _serverStatuses.TryGetValue(server.Id, out ServerAvailabilityState state) &&
               state == ServerAvailabilityState.Down;
    }

    private static bool HasTrackedPresence(
        LaunchAccountProfile account,
        LaunchServerProfile? server,
        IReadOnlyDictionary<int, LaunchContextRecord> activeContexts,
        IReadOnlyDictionary<int, SessionStateRecord> activeSessions)
    {
        foreach (SessionStateRecord session in activeSessions.Values)
        {
            if (SessionMatchesAccount(account, server, session.AccountName, session.ServerName))
                return true;
        }

        foreach (LaunchContextRecord context in activeContexts.Values)
        {
            if (SessionMatchesAccount(account, server, context.AccountName, context.ServerName))
                return true;
        }

        return false;
    }

    private void UpdateLaunchTargetStatuses(
        IReadOnlyList<Process>? targets = null,
        IReadOnlyDictionary<int, LaunchContextRecord>? activeContexts = null,
        IReadOnlyDictionary<int, SessionStateRecord>? activeSessions = null)
    {
        // Dispose the Process[] only if WE allocated it here (standalone call). When
        // called from RefreshSessionState the array is passed in and that caller owns it.
        bool ownTargets = targets is null;
        targets ??= FindClientProcesses();   // headless clients have session files too
        HashSet<int> activePids = targets.Select(process => process.Id).ToHashSet();

        activeContexts ??= LaunchContextStore.ReadForActiveProcesses(activePids);
        activeSessions ??= SessionStateStore.ReadForActiveProcesses(activePids);
        RefreshLaunchTargetCharacterSelections(activeContexts, activeSessions);

        foreach (LaunchAccountProfile account in _settings.AccountProfiles)
        {
            if (!_launchTargetStatusTexts.TryGetValue(account.Id, out TextBlock? statusText))
                continue;

            LaunchServerProfile? server = ResolveServerForAccount(account);
            if (_launchTargetChecks.TryGetValue(account.Id, out CheckBox? checkBox))
                checkBox.Content = BuildLaunchTargetLabel(account, server, GetAccountOrdinal(account));

            (string text, string color) status = BuildLaunchTargetStatus(account, server, targets, activeContexts, activeSessions);
            statusText.Text = status.text;
            statusText.Foreground = Brush.Parse(status.color);
        }

        if (ownTargets)
            foreach (Process p in targets) p.Dispose();
    }

    private (string text, string color) BuildLaunchTargetStatus(
        LaunchAccountProfile account,
        LaunchServerProfile? server,
        IReadOnlyList<Process> targets,
        IReadOnlyDictionary<int, LaunchContextRecord> activeContexts,
        IReadOnlyDictionary<int, SessionStateRecord> activeSessions)
    {
        foreach ((int pid, SessionStateRecord session) in activeSessions)
        {
            if (!SessionMatchesAccount(account, server, session.AccountName, session.ServerName))
                continue;

            string character = !string.IsNullOrWhiteSpace(session.CharacterName) ? session.CharacterName
                : !string.IsNullOrWhiteSpace(session.TargetCharacter) ? session.TargetCharacter
                : !string.IsNullOrWhiteSpace(account.CharacterName) && account.CharacterName != LaunchAccountProfile.NoneOption ? account.CharacterName
                : "Logged in";
            string elapsed = FormatElapsed(DateTime.UtcNow - (session.LoginCompletedAtUtc ?? session.LaunchStartedAtUtc));
            return ($"{character} | online {elapsed}", "#66D687");
        }

        foreach ((int pid, LaunchContextRecord context) in activeContexts)
        {
            if (!SessionMatchesAccount(account, server, context.AccountName, context.ServerName))
                continue;

            Process? process = targets.FirstOrDefault(p => p.Id == pid);
            DateTime since = process != null ? GetProcessStartTimeUtc(process) : context.CreatedAtUtc.ToUniversalTime();
            string character = !string.IsNullOrWhiteSpace(context.TargetCharacter) ? context.TargetCharacter
                : !string.IsNullOrWhiteSpace(account.CharacterName) && account.CharacterName != LaunchAccountProfile.NoneOption ? account.CharacterName
                : "Character select";
            string elapsed = FormatElapsed(DateTime.UtcNow - since);
            return ($"{character} | starting {elapsed}", "#E2B348");
        }

        if (AcLaunchArgumentBuilder.NeedsPassword(account))
            return ("Needs password | launch to set up", "#E2B348");
        if (account.CharacterName == LaunchAccountProfile.NoneOption)
            return ("Character select | offline", "#73828D");
        return (!string.IsNullOrWhiteSpace(account.CharacterName) ? $"{account.CharacterName} | offline" : "Offline", "#73828D");
    }

    private static bool SessionMatchesAccount(
        LaunchAccountProfile account,
        LaunchServerProfile? server,
        string sessionAccountName,
        string sessionServerName)
    {
        if (!string.Equals(account.AccountName?.Trim(), sessionAccountName?.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.IsNullOrWhiteSpace(server?.Name) || string.IsNullOrWhiteSpace(sessionServerName))
            return true;

        return string.Equals(server.Name.Trim(), sessionServerName.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime GetProcessStartTimeUtc(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime();
        }
        catch
        {
            return DateTime.UtcNow;
        }
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;

        return elapsed.TotalHours >= 1
            ? elapsed.ToString(@"hh\:mm\:ss")
            : elapsed.ToString(@"mm\:ss");
    }

    private void RefreshLaunchTargetCharacterSelections(
        IReadOnlyDictionary<int, LaunchContextRecord> activeContexts,
        IReadOnlyDictionary<int, SessionStateRecord> activeSessions)
    {
        bool changed = false;

        foreach (LaunchAccountProfile account in _settings.AccountProfiles)
        {
            LaunchServerProfile? server = ResolveServerForAccount(account);
            List<string> detectedCharacters = GetDetectedCharacters(account.AccountName, server?.Name ?? string.Empty);
            string bestKnownCharacter = GetPreferredCharacterName(account, server, activeContexts, activeSessions, detectedCharacters);

            if (!string.IsNullOrWhiteSpace(bestKnownCharacter) &&
                !string.Equals(account.CharacterName, bestKnownCharacter, StringComparison.OrdinalIgnoreCase))
            {
                account.CharacterName = bestKnownCharacter;
                changed = true;
            }

            if (!_launchTargetCharacterDropdowns.TryGetValue(account.Id, out ComboBox? dropdown))
                continue;

            if (!string.IsNullOrWhiteSpace(account.CharacterName)
                && account.CharacterName != LaunchAccountProfile.NoneOption
                && !detectedCharacters.Contains(account.CharacterName))
            {
                detectedCharacters.Insert(0, account.CharacterName);
            }

            // "(None)" at the top — selecting it opts out of auto-login for this account
            detectedCharacters.Insert(0, LaunchAccountProfile.NoneOption);

            string? wanted = account.CharacterName == LaunchAccountProfile.NoneOption
                ? LaunchAccountProfile.NoneOption
                : !string.IsNullOrWhiteSpace(account.CharacterName) && detectedCharacters.Contains(account.CharacterName)
                    ? account.CharacterName
                    : null;
            // This runs every 2 s: refill a dropdown only when its list or choice changed, so the
            // Launcher tab isn't refilled (and its selection events raised) for nothing.
            if (dropdown.ItemsSource is IEnumerable<string> shown && shown.SequenceEqual(detectedCharacters)
                && Equals(dropdown.SelectedItem as string, wanted))
                continue;

            KeepLauncherScroll();
            _suppressLaunchTargetCharacterSelectionChanged = true;
            try
            {
                dropdown.ItemsSource = detectedCharacters;
                dropdown.SelectedItem = wanted;
            }
            finally
            {
                _suppressLaunchTargetCharacterSelectionChanged = false;
            }
        }

        if (changed)
        {
            SaveSettings();
            RefreshSummary();
        }
    }

    private string ResolveTargetCharacterForLaunch(LaunchAccountProfile account, LaunchServerProfile? server)
    {
        // User explicitly opted out of auto-login → blank target so engine skips it.
        if (account.CharacterName == LaunchAccountProfile.NoneOption)
            return string.Empty;

        List<string> detectedCharacters = GetDetectedCharacters(account.AccountName, server?.Name ?? string.Empty);
        string bestKnownCharacter = GetPreferredCharacterName(account, server, null, null, detectedCharacters);
        if (!string.IsNullOrWhiteSpace(bestKnownCharacter) &&
            !string.Equals(account.CharacterName, bestKnownCharacter, StringComparison.OrdinalIgnoreCase))
        {
            account.CharacterName = bestKnownCharacter;
            SaveSettings();
        }

        return account.CharacterName ?? string.Empty;
    }

    private string GetPreferredCharacterName(
        LaunchAccountProfile account,
        LaunchServerProfile? server,
        IReadOnlyDictionary<int, LaunchContextRecord>? activeContexts,
        IReadOnlyDictionary<int, SessionStateRecord>? activeSessions,
        List<string>? detectedCharacters = null)
    {
        if (!string.IsNullOrWhiteSpace(account.CharacterName))
            return account.CharacterName;

        if (activeSessions != null)
        {
            foreach (SessionStateRecord session in activeSessions.Values)
            {
                if (!SessionMatchesAccount(account, server, session.AccountName, session.ServerName))
                    continue;

                if (!string.IsNullOrWhiteSpace(session.CharacterName))
                    return session.CharacterName;

                if (!string.IsNullOrWhiteSpace(session.TargetCharacter))
                    return session.TargetCharacter;
            }
        }

        if (activeContexts != null)
        {
            foreach (LaunchContextRecord context in activeContexts.Values)
            {
                if (!SessionMatchesAccount(account, server, context.AccountName, context.ServerName))
                    continue;

                if (!string.IsNullOrWhiteSpace(context.TargetCharacter))
                    return context.TargetCharacter;
            }
        }

        detectedCharacters ??= GetDetectedCharacters(account.AccountName, server?.Name ?? string.Empty);
        return detectedCharacters.Count == 1 ? detectedCharacters[0] : string.Empty;
    }

    private async Task RefreshServerStatusesAsync()
    {
        if (_serverStatusRefreshInFlight)
            return;

        List<LaunchServerProfile> servers = _settings.ServerProfiles.Select(server => server.Clone()).ToList();
        if (servers.Count == 0)
        {
            _serverStatuses.Clear();
            RefreshServerProfileDisplay();
            return;
        }

        _serverStatusRefreshInFlight = true;
        try
        {
            Dictionary<string, ServerAvailabilityState> statuses = await _serverStatusProbe.ProbeAsync(servers);
            _serverStatuses.Clear();
            foreach ((string serverId, ServerAvailabilityState status) in statuses)
                _serverStatuses[serverId] = status;

            RefreshServerProfileDisplay();
        }
        catch (Exception ex)
        {
            AppendActivity($"Server status refresh failed: {ex.Message}");
        }
        finally
        {
            _serverStatusRefreshInFlight = false;
        }
    }

    private void RefreshServerProfileDisplay()
    {
        KeepLauncherScroll();
        int selectedIndex = ServerProfilesList.SelectedIndex;
        _suppressPrimarySelectionChanged = true;
        try
        {
            ServerProfilesList.ItemsSource = BuildServerProfileDisplayNames();
            if (_settings.ServerProfiles.Count == 0)
            {
                ServerProfilesList.SelectedIndex = -1;
            }
            else if (selectedIndex >= 0 && selectedIndex < _settings.ServerProfiles.Count)
            {
                ServerProfilesList.SelectedIndex = selectedIndex;
            }
            else
            {
                ServerProfilesList.SelectedIndex = FindServerIndex(_settings.SelectedServerProfileId);
            }
        }
        finally
        {
            _suppressPrimarySelectionChanged = false;
        }

        RefreshSummary();
        UpdateLaunchTargetStatuses();
    }

    private string[] BuildServerProfileDisplayNames()
    {
        return _settings.ServerProfiles
            .Select(server => $"{GetServerStatusSymbolSafe(server)} {server.DisplayName}")
            .ToArray();
    }

    private string[] BuildAccountProfileDisplayNames()
    {
        int digits = Math.Max(2, _settings.AccountProfiles.Count.ToString().Length);
        return _settings.AccountProfiles
            .Select((account, index) => $"{FormatOrdinal(index, digits)} {account.DisplayName}" +
                                        (account.PasswordNeedsReentry ? "  [password needs re-entering]" : string.Empty))
            .ToArray();
    }

    private string BuildPrimarySelectionSummary(LaunchAccountProfile account, LaunchServerProfile server)
    {
        return $"{account.DisplayName} / {GetServerStatusSymbolSafe(server)} {server.DisplayName}";
    }

    private string BuildLaunchTargetLabel(LaunchAccountProfile account, LaunchServerProfile? server, int accountIndex)
    {
        string serverName = server?.DisplayName ?? "No Server";
        int digits = Math.Max(2, _settings.AccountProfiles.Count.ToString().Length);
        return $"{FormatOrdinal(accountIndex, digits)} {account.AccountName} [{GetServerStatusSymbolSafe(server)} {serverName}]";
    }

    private int GetAccountOrdinal(LaunchAccountProfile account)
    {
        int index = _settings.AccountProfiles.FindIndex(profile =>
            string.Equals(profile.Id, account.Id, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : 0;
    }

    private static string FormatOrdinal(int index, int digits)
    {
        int safeDigits = Math.Max(2, digits);
        return (index + 1).ToString($"D{safeDigits}") + ".";
    }

    private string GetServerStatusSymbolSafe(LaunchServerProfile? server)
    {
        if (server == null)
            return "X";

        return _serverStatuses.TryGetValue(server.Id, out ServerAvailabilityState state) && state == ServerAvailabilityState.Up
            ? char.ConvertFromUtf32(0x2713)
            : "X";
    }

    private string GetServerStatusSymbol(LaunchServerProfile? server)
    {
        if (server == null)
            return "X";

        return _serverStatuses.TryGetValue(server.Id, out ServerAvailabilityState state) && state == ServerAvailabilityState.Up
            ? "✓"
            : "X";
    }
}
