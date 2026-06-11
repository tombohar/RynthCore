using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RynthCore.Injector;

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
    // Serializes prefs-swap launches so two accounts don't race on the live
    // UserPreferences.ini. Non-prefs launches don't acquire this and stay
    // parallel.
    private readonly SemaphoreSlim _userPrefsSwapLock = new(1, 1);
    // After CreateProcess+inject+resume, hold the prefs lock this long so AC
    // has a chance to read UserPreferences.ini before another account swaps.
    private static readonly TimeSpan UserPrefsSettleDelay = TimeSpan.FromSeconds(2);
    private readonly Dictionary<int, DateTime> _autoInjectFirstReadyAtUtc = [];
    private readonly HashSet<int> _autoInjectAttemptedPids = [];
    private readonly HashSet<int> _autoInjectInFlightPids = [];
    private readonly Dictionary<string, TextBlock> _launchTargetStatusTexts = [];
    private readonly Dictionary<string, CheckBox> _launchTargetChecks = [];
    private readonly Dictionary<string, ComboBox> _launchTargetCharacterDropdowns = [];
    private readonly Dictionary<string, ComboBox> _launchTargetInjectionDropdowns = [];
    private readonly Dictionary<string, DateTime> _autoLaunchAttemptTimesUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _autoLaunchServerDownNoticeTimesUtc = new(StringComparer.OrdinalIgnoreCase);
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
        AppendActivity("Avalonia launcher preview ready.");
        _ = RefreshServerStatusesAsync();
    }

    private void WireEvents()
    {
        AddServerButton.Click += async (_, _) => await AddServerAsync();
        EditServerButton.Click += async (_, _) => await EditServerAsync();
        DeleteServerButton.Click += (_, _) => DeleteServer();

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
        SaveBehaviorButton.Click += (_, _) => SaveLaunchBehavior();
        InjectRunningAcButton.Click += async (_, _) => await InjectRunningAcAsync();
        RefreshSessionsButton.Click += (_, _) => RefreshSessionState();
        ResetOverlayBarButton.Click += (_, _) => ResetOverlayBar();
        AddPluginDllButton.Click += async (_, _) => await AddPluginDllAsync();

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

        AllowMultipleClientsCheckBox.IsChecked = _settings.AllowMultipleClients;
        SkipIntroVideosCheckBox.IsChecked = _settings.SkipIntroVideos;
        SkipLoginLogosCheckBox.IsChecked = _settings.SkipLoginLogos;
        AutoLaunchHeaderCheckBox.IsChecked = _settings.AutoLaunch;
        AutoInjectAfterLaunchCheckBox.IsChecked = _settings.AutoInjectAfterLaunch;
        WatchForAcStartCheckBox.IsChecked = _settings.WatchForAcStart;
        OverrideWindowTitleCheckBox.IsChecked = _settings.OverrideWindowTitle;
        LaunchStaggerMsBox.Value = _settings.LaunchStaggerMs;
        CrashRelaunchLimitBox.Value = _settings.CrashRelaunchLimitInWindow;
        CrashRelaunchWindowBox.Value = _settings.CrashRelaunchWindowMinutes;
        KillStuckClientsCheckBox.IsChecked = _settings.KillStuckClients;
        StuckClientTimeoutBox.Value = _settings.StuckClientTimeoutSeconds;

        // Engine flags live in %APPDATA%\RynthCore\engine.json, not the
        // launcher's own settings. The engine reads them on init; the launcher
        // just provides a UI window into the same file.
        EnableDcompOverlayCheckBox.IsChecked = EngineJsonStore.GetBool("EnableDcompOverlay") ?? false;
        EnableDcompOverlayCheckBox.IsCheckedChanged += OnEnableDcompOverlayChanged;
    }

    private void OnEnableDcompOverlayChanged(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        bool value = EnableDcompOverlayCheckBox.IsChecked == true;
        try
        {
            EngineJsonStore.SetBool("EnableDcompOverlay", value);
            AppendActivity($"Engine setting saved: EnableDcompOverlay={value}. Takes effect on next AC client launch.");
        }
        catch (Exception ex)
        {
            AppendActivity($"Failed to save EnableDcompOverlay to engine.json: {ex.Message}");
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

            PluginLoadoutPanel.Children.Add(BuildPluginCard(checkBox, plugin.Summary, null));
        }

        var disabledSet = new HashSet<string>(
            _settings.DisabledPluginDllPaths ?? new List<string>(),
            StringComparer.OrdinalIgnoreCase);

        foreach (string dllPath in _pluginDllPaths)
        {
            string capturedPath = dllPath;
            string pluginName = Path.GetFileNameWithoutExtension(dllPath);

            var checkBox = new CheckBox
            {
                Content = pluginName,
                IsChecked = !disabledSet.Contains(capturedPath)
            };
            checkBox.IsCheckedChanged += (_, _) => OnUserPluginEnabledChanged(capturedPath, checkBox);

            PluginLoadoutPanel.Children.Add(BuildPluginCard(checkBox, capturedPath, () =>
            {
                for (int j = _pluginDllPaths.Count - 1; j >= 0; j--)
                {
                    if (string.Equals(_pluginDllPaths[j], capturedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        _pluginDllPaths.RemoveAt(j);
                        break;
                    }
                }
                // Also forget any disabled-marker for this path so re-adding
                // it later starts in the enabled state.
                if (_settings.DisabledPluginDllPaths != null)
                {
                    for (int j = _settings.DisabledPluginDllPaths.Count - 1; j >= 0; j--)
                    {
                        if (string.Equals(_settings.DisabledPluginDllPaths[j], capturedPath, StringComparison.OrdinalIgnoreCase))
                        {
                            _settings.DisabledPluginDllPaths.RemoveAt(j);
                            break;
                        }
                    }
                }
                SavePluginDllPaths();
                BuildPluginLoadout();
                AppendActivity($"Removed plugin: {Path.GetFileName(capturedPath)}");
            }));
        }
    }

    private static Border BuildPluginCard(CheckBox checkBox, string description, Action? onRemove)
    {
        var host = new Border
        {
            Background = Brush.Parse("#0F161D"),
            BorderBrush = Brush.Parse("#243742"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12)
        };

        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(checkBox);
        stack.Children.Add(new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush.Parse("#9AA8B3")
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
        LaunchServerProfile? server = GetSelectedServer();
        LaunchAccountProfile? account = GetSelectedAccount();
        int checkedTargets = _settings.CheckedLaunchAccountProfileIds?.Count ?? 0;
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

    private void DeleteServer()
    {
        int index = ServerProfilesList.SelectedIndex;
        if (index < 0 || index >= _settings.ServerProfiles.Count)
            return;

        string removedServerId = _settings.ServerProfiles[index].Id;
        bool removedSelected = string.Equals(removedServerId, _settings.SelectedServerProfileId, StringComparison.OrdinalIgnoreCase);
        _settings.ServerProfiles.RemoveAt(index);
        if (removedSelected)
            _settings.SelectedServerProfileId = _settings.ServerProfiles.FirstOrDefault()?.Id ?? string.Empty;
        _serverStatuses.Remove(removedServerId);
        SaveSettings();
        RefreshLists();
        ServerProfilesList.SelectedIndex = Math.Min(index, _settings.ServerProfiles.Count - 1);
        RefreshSummary();
        AppendActivity($"Server removed: {index + 1}");
    }

    private async System.Threading.Tasks.Task AddAccountAsync()
    {
        var draft = new RynthCore.App.LaunchAccountProfile();
        var dialog = new AccountProfileDialog(draft, _settings.ServerProfiles);
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
        var dialog = new AccountProfileDialog(draft, _settings.ServerProfiles);
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

        // Delete all character cache files for this account name
        DeleteCharacterCacheFiles(removed.AccountName);

        _settings.AccountProfiles.RemoveAt(index);
        _settings.CheckedLaunchAccountProfileIds = _settings.CheckedLaunchAccountProfileIds
            .Where(id => !string.Equals(id, removedId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (string.Equals(removedId, _settings.SelectedAccountProfileId, StringComparison.OrdinalIgnoreCase))
            _settings.SelectedAccountProfileId = _settings.AccountProfiles.FirstOrDefault()?.Id ?? string.Empty;
        SaveSettings();
        RefreshLists();
        AccountProfilesList.SelectedIndex = Math.Min(index, _settings.AccountProfiles.Count - 1);
        RefreshSummary();
        AppendActivity($"Account removed: {removed.DisplayName}");
    }

    private static void DeleteCharacterCacheFiles(string accountName)
    {
        if (string.IsNullOrWhiteSpace(accountName))
            return;

        CharacterCacheStore.DeleteForAccount(accountName);
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

            // Try to find the associated server, or fallback to the first one if not set or invalid
            LaunchServerProfile? server = _settings.ServerProfiles.FirstOrDefault(s => s.Id == account.ServerId);
            if (server == null && _settings.ServerProfiles.Count > 0)
            {
                server = _settings.ServerProfiles.FirstOrDefault();
                account.ServerId = server?.Id ?? string.Empty;
            }

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
            LaunchServerProfile? server = _settings.ServerProfiles.FirstOrDefault(s => s.Id == account.ServerId);
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

        dropdown.Items.Add(rynthItem);
        dropdown.Items.Add(decalItem);

        // If the account is set to Decal but Decal isn't installed, fall back to
        // RynthCore in the UI so the user can't accidentally launch into a
        // disabled mode. We don't mutate the saved profile until they click.
        InjectionMode effectiveMode = (account.InjectionMode == InjectionMode.Decal && !decalAvailable)
            ? InjectionMode.RynthCore
            : account.InjectionMode;
        dropdown.SelectedItem = effectiveMode == InjectionMode.Decal ? decalItem : rynthItem;

        dropdown.SelectionChanged += (_, _) =>
        {
            if (dropdown.SelectedItem is ComboBoxItem item && item.Tag is InjectionMode mode)
            {
                if (account.InjectionMode == mode)
                    return;
                account.InjectionMode = mode;
                SaveSettings();
                AppendActivity($"{account.DisplayName}: injection method set to {mode}.");
            }
        };

        return dropdown;
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

        // Ensure the account is associated with the currently selected server if it doesn't have one
        LaunchServerProfile? selectedServer = GetSelectedServer();
        if (selectedServer != null && string.IsNullOrEmpty(account.ServerId))
        {
            account.ServerId = selectedServer.Id;
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
        SaveSettings();
        RefreshSummary();
        if (appendActivity)
            AppendActivity("Runtime paths saved.");
    }

    private void SaveLaunchBehavior(bool appendActivity = true)
    {
        _settings.AllowMultipleClients = AllowMultipleClientsCheckBox.IsChecked == true;
        _settings.SkipIntroVideos = SkipIntroVideosCheckBox.IsChecked == true;
        _settings.SkipLoginLogos = SkipLoginLogosCheckBox.IsChecked == true;
        _settings.AutoLaunch = AutoLaunchHeaderCheckBox.IsChecked == true;
        _settings.AutoInjectAfterLaunch = AutoInjectAfterLaunchCheckBox.IsChecked == true;
        _settings.WatchForAcStart = WatchForAcStartCheckBox.IsChecked == true;
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
        if (appendActivity)
            AppendActivity("Launch behavior saved.");
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

        string acPath = AcClientPathTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(acPath) || !File.Exists(acPath))
        {
            AppendActivity("Launch blocked: AC client path is missing or invalid.");
            return;
        }

        string enginePath = EnginePathTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(enginePath))
            enginePath = _injector.TryResolveEnginePath(null) ?? string.Empty;

        if (string.IsNullOrWhiteSpace(enginePath))
        {
            AppendActivity("Launch blocked: engine path is missing and no default engine DLL could be resolved.");
            return;
        }

        string overrideArguments = isAutomaticLaunch ? string.Empty : LaunchArgumentsTextBox.Text?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(overrideArguments) && accountsToLaunch.Count > 1)
        {
            AppendActivity("Launch blocked: manual launch arguments can only be used with one account at a time.");
            return;
        }

        bool shouldInject = AutoInjectAfterLaunchCheckBox.IsChecked == true;

        // Engine plugin must be selected when at least one chosen account is
        // RynthCore-mode AND auto-inject is on. Decal-mode accounts don't need
        // it because they don't load the engine.
        bool anyRynthCoreMode = accountsToLaunch.Any(a => a.InjectionMode == InjectionMode.RynthCore);
        if (shouldInject && anyRynthCoreMode && !GetSelectedPluginIds().Contains("rynthcore-engine", StringComparer.OrdinalIgnoreCase))
        {
            AppendActivity("Launch blocked: RynthCore Engine must stay selected in Runtime Loadout when auto-inject is enabled for any RynthCore-mode account.");
            return;
        }

        // Pre-resolve Decal Inject.dll once if any account needs it.
        string? decalInjectPath = accountsToLaunch.Any(a => a.InjectionMode == InjectionMode.Decal && shouldInject)
            ? DecalLocator.TryGetInjectDllPath()
            : null;
        if (shouldInject && accountsToLaunch.Any(a => a.InjectionMode == InjectionMode.Decal) && decalInjectPath == null)
        {
            AppendActivity("Launch blocked: one or more accounts are set to Decal mode but Decal is not installed (HKLM\\SOFTWARE\\Decal\\Agent\\AgentPath\\Inject.dll missing).");
            return;
        }

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
                    string key = BuildAccountKey(account.AccountName);
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
                if (!TryGetLaunchArgumentsForAccount(
                    account,
                    allowArgumentOverride: !isAutomaticLaunch,
                    out string launchArguments,
                    out string launchSummary,
                    out string error))
                {
                    AppendActivity($"Launch blocked for {account.DisplayName}: {error}");
                    continue;
                }

                LaunchServerProfile? contextServer = ResolveServerForAccount(account);
                string accountKey = BuildAccountKey(account.AccountName);
                if (!string.IsNullOrWhiteSpace(accountKey) && activeAccountKeys.Contains(accountKey))
                {
                    string serverLabel = contextServer?.DisplayName ?? "the configured server";
                    AppendActivity($"Skipped {account.DisplayName}: account '{account.AccountName}' already has a running session on {serverLabel}.");
                    continue;
                }

                LaunchContextRecord launchContext = BuildLaunchContext(account, contextServer);
                // Best-effort legacy write — last writer wins. The PID-specific
                // writes inside the launch task are the authoritative path.
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
                if (!shouldInject)
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
                    launchTask = LaunchOneDecalInjectedAsync(p.Account, decalAcPath, p.Args, decalInjectPath!, p.Summary, p.Context);
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
        string resolvedPrefsPath = ResolveUserPrefsPath(account);
        bool needsPrefsSwap = !string.IsNullOrEmpty(resolvedPrefsPath) && File.Exists(resolvedPrefsPath);

        string entryAccountKey = BuildAccountKey(launchContext.AccountName);
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
            if (needsPrefsSwap)
            {
                _launchSettings.SwapUserPreferences(resolvedPrefsPath, _settings.AllowMultipleClients, AppendActivity);
            }

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

            string accountKey = BuildAccountKey(launchContext.AccountName);

            LauncherDiag.Info($"LaunchOneInjected injection result: success={result.Success} pid={(result.ProcessId is int p ? p.ToString() : "null")} summary='{result.Summary}'");

            if (result.Success)
            {
                if (result.ProcessId is int trackedPid)
                {
                    _launchedSessionPids.Add(trackedPid);
                    _launchedPidInfo[trackedPid] = (accountKey, DateTime.UtcNow);
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
        string launchSummary,
        LaunchContextRecord launchContext)
    {
        string resolvedPrefsPath = ResolveUserPrefsPath(account);
        bool needsPrefsSwap = !string.IsNullOrEmpty(resolvedPrefsPath) && File.Exists(resolvedPrefsPath);

        string entryAccountKey = BuildAccountKey(launchContext.AccountName);
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
            if (needsPrefsSwap)
            {
                _launchSettings.SwapUserPreferences(resolvedPrefsPath, _settings.AllowMultipleClients, AppendActivity);
            }

            InjectionResult result = await Task.Run(() =>
                _injector.LaunchSuspendedAndInjectDecal(
                    acPath,
                    launchArguments,
                    decalInjectDllPath,
                    AppendActivity,
                    processId =>
                    {
                        LaunchContextStore.WriteForProcess(processId, launchContext);
                        WritePendingSessionState(processId, launchContext);
                    }));

            string accountKey = BuildAccountKey(launchContext.AccountName);

            LauncherDiag.Info($"LaunchOneDecal injection result: success={result.Success} pid={(result.ProcessId is int p ? p.ToString() : "null")} summary='{result.Summary}'");

            if (result.Success)
            {
                if (result.ProcessId is int trackedPid)
                {
                    _launchedSessionPids.Add(trackedPid);
                    _launchedPidInfo[trackedPid] = (accountKey, DateTime.UtcNow);
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
        string resolvedPrefsPath = ResolveUserPrefsPath(account);
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
            if (needsPrefsSwap)
            {
                _launchSettings.SwapUserPreferences(resolvedPrefsPath, _settings.AllowMultipleClients, AppendActivity);
            }

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
                _launchedPidInfo[proc.Id] = (BuildAccountKey(launchContext.AccountName), DateTime.UtcNow);
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

    private bool TryGetLaunchArgumentsForAccount(
        LaunchAccountProfile account,
        bool allowArgumentOverride,
        out string arguments,
        out string launchSummary,
        out string error)
    {
        string overrideArguments = allowArgumentOverride
            ? LaunchArgumentsTextBox.Text?.Trim() ?? string.Empty
            : string.Empty;
        if (!string.IsNullOrWhiteSpace(overrideArguments))
        {
            arguments = overrideArguments;
            launchSummary = string.IsNullOrWhiteSpace(account.DisplayName)
                ? "Manual argument override"
                : $"Manual argument override / {account.DisplayName}";
            error = string.Empty;
            return true;
        }

        LaunchServerProfile? server = ResolveServerForAccount(account);
        if (server == null)
        {
            arguments = string.Empty;
            launchSummary = string.Empty;
            error = "Select a server profile, or enter manual launch arguments.";
            return false;
        }

        try
        {
            arguments = _launchArgumentBuilder.BuildArguments(server, account);
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

    private LaunchServerProfile? ResolveServerForAccount(LaunchAccountProfile account)
    {
        if (!string.IsNullOrWhiteSpace(account.ServerId))
        {
            LaunchServerProfile? savedServer = _settings.ServerProfiles.FirstOrDefault(server =>
                string.Equals(server.Id, account.ServerId, StringComparison.OrdinalIgnoreCase));
            if (savedServer != null)
                return savedServer;
        }

        return GetSelectedServer() ?? _settings.ServerProfiles.FirstOrDefault();
    }

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

    private void LoadPluginDllPaths()
    {
        _pluginDllPaths.Clear();
        if (_settings.PluginDllPaths != null)
        {
            foreach (string path in _settings.PluginDllPaths)
                _pluginDllPaths.Add(path);
        }
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
        }
        catch (Exception ex)
        {
            AppendActivity($"Failed to sync plugin paths to engine.json: {ex.Message}");
        }
    }

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
            Process target = targets.OrderBy(process => process.Id).First();
            AppendActivity($"Applying selected loadout to running AC (PID {target.Id}).");

            InjectionResult result = await Task.Run(() => _injector.InjectIntoProcess(target, enginePath, AppendActivity));
            if (result.Success)
            {
                _launchedSessionPids.Add(target.Id);
                AppendActivity($"Injection complete for PID {target.Id}.");
            }
            else
            {
                AppendActivity($"Injection failed: {result.Summary}");
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
            targets = _injector.FindTargetProcesses();
        }
        catch (Exception ex)
        {
            AcStatusText.Text = $"Session probe failed: {ex.Message}";
            return;
        }

        HashSet<int> activePids = targets.Select(process => process.Id).ToHashSet();
        LaunchContextStore.DeleteStaleProcessFiles(activePids);
        SessionStateStore.DeleteStaleProcessFiles(activePids);
        Dictionary<int, LaunchContextRecord> activeContexts = LaunchContextStore.ReadForActiveProcesses(activePids);
        Dictionary<int, SessionStateRecord> activeSessions = SessionStateStore.ReadForActiveProcesses(activePids);
        DetectQuickExits(activePids);

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

        AcStatusText.Text = targets.Length switch
        {
            0 => "No running AC clients detected.",
            1 => "1 AC client is running.",
            _ => $"{targets.Length} AC clients are running."
        };

        _sessionItems.Clear();
        foreach (Process process in targets.OrderBy(p => p.Id))
        {
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
        MaybeAutoInjectExternalClients(targets, activePids, activeContexts, activeSessions);
        MaybeKillStuckClients(targets, activeContexts, activeSessions);

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
            string? accountName = sess?.AccountName
                                  ?? (activeContexts.TryGetValue(pid, out LaunchContextRecord? ctx) ? ctx.AccountName : null);
            if (string.IsNullOrWhiteSpace(accountName))
                continue;

            LaunchAccountProfile? profile = _settings.AccountProfiles.FirstOrDefault(a =>
                string.Equals(a.AccountName, accountName, StringComparison.OrdinalIgnoreCase));
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

            try
            {
                AppendActivity($"Killing stuck PID {pid} ({accountName}) — {reason} and window is not responding.");
                process.Kill();
                _launchedSessionPids.Remove(pid);
                _launchedPidInfo.Remove(pid);

                // Count the reap as a crash. The killed client's uptime exceeds
                // the quick-exit window (StuckClientTimeoutSeconds >= 60s vs
                // 30s), so DetectQuickExits classifies it "normal close" — and
                // auto-launch would relaunch a perpetually-stuck client in an
                // endless kill/relaunch loop, bypassing the circuit breaker.
                string crashKey = BuildAccountKey(accountName);
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
            string args = _launchArgumentBuilder.BuildArguments(server, account);
            string password = account.Password ?? string.Empty;
            if (!string.IsNullOrEmpty(password))
                args = args.Replace(password, "********", StringComparison.Ordinal);

            return args;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private HashSet<string> GetActiveAccountKeys()
    {
        Process[] targets = _injector.FindTargetProcesses();
        HashSet<int> activePids = targets.Select(process => process.Id).ToHashSet();

        // Processes whose window is gone but whose OS process is still alive
        // (slow native shutdown, crash dump collection, etc.) should not block
        // relaunch for their account. Only applies to PIDs that completed at
        // least one login — startup-phase clients keep blocking normally.
        foreach (Process p in targets)
        {
            if (!_everLoggedInPids.Contains(p.Id))
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
            string key = BuildAccountKey(context.AccountName);
            if (!string.IsNullOrWhiteSpace(key))
                keys.Add(key);
        }

        foreach (SessionStateRecord session in SessionStateStore.ReadForActiveProcesses(activePids).Values)
        {
            string key = BuildAccountKey(session.AccountName);
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

    private static string BuildAccountKey(string? accountName) =>
        string.IsNullOrWhiteSpace(accountName)
            ? string.Empty
            : accountName.Trim().ToUpperInvariant();

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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowTextW(IntPtr hWnd, string lpString);

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

        // Resolve the server profile ID from the server name so we can match
        // account profiles precisely — two profiles can share an account name
        // on different servers (e.g. "Drakkon" on ACEmulator vs Reefcull).
        string? serverId = null;
        if (!string.IsNullOrWhiteSpace(serverName))
        {
            serverId = _settings.ServerProfiles
                .FirstOrDefault(sp => string.Equals(sp.Name, serverName, StringComparison.OrdinalIgnoreCase))
                ?.Id;
        }

        LaunchAccountProfile? profile = _settings.AccountProfiles.FirstOrDefault(a =>
            string.Equals(a.AccountName, accountName, StringComparison.OrdinalIgnoreCase)
            && (serverId == null || string.Equals(a.ServerId, serverId, StringComparison.OrdinalIgnoreCase)));
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
                LauncherDiag.Info($"WindowPos PID {process.Id} ({accountName}): hwnd=0 — cannot restore or capture.");
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
                    SWP_NOZORDER | SWP_NOACTIVATE);
                LauncherDiag.Info($"WindowPos PID {process.Id} ({accountName}): restored to ({profile.WindowX},{profile.WindowY},{profile.WindowWidth}x{profile.WindowHeight}).");
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
            LauncherDiag.Info($"WindowPos PID {process.Id} ({accountName}): ignored char-select/login shrink ({w}x{h}, <50% of in-game {refW}x{refH}).");
            return;
        }

        _baselineWindowRects[process.Id] = (x, y, w, h);
        profile.WindowX = x;
        profile.WindowY = y;
        profile.WindowWidth = w;
        profile.WindowHeight = h;
        SaveSettings();
        LauncherDiag.Info($"WindowPos PID {process.Id} ({accountName}): saved ({x},{y},{w}x{h}).");
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

        SetWindowTextW(hwnd, desired);
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
    /// keyed by account name. Returns empty when the account has no name yet
    /// (new profile, can't pick a filename).
    /// </summary>
    private static string ResolveUserPrefsPath(LaunchAccountProfile account)
    {
        if (!string.IsNullOrWhiteSpace(account.UserPrefsPath))
            return account.UserPrefsPath.Trim();

        if (string.IsNullOrWhiteSpace(account.AccountName))
            return string.Empty;

        string appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string prefsDir = Path.Combine(appdata, "RynthCore", "prefs");
        string safeName = string.Join("_", account.AccountName.Trim().Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(prefsDir, $"{safeName}.ini");
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
            string.Equals(BuildAccountKey(a.AccountName), accountKey, StringComparison.OrdinalIgnoreCase));
        if (profile == null)
            return;

        string stashPath = ResolveUserPrefsPath(profile);
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
        string accountKey = BuildAccountKey(account.AccountName);
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
            if (HasTrackedPresence(account, server, activeContexts, activeSessions))
                continue;

            if (IsServerKnownDown(server))
            {
                MaybeLogAutoLaunchServerDown(account, server, nowUtc);
                continue;
            }

            if (_autoLaunchAttemptTimesUtc.TryGetValue(account.Id, out DateTime lastAttemptUtc) &&
                nowUtc - lastAttemptUtc < AutoLaunchCooldown)
            {
                continue;
            }

            string accountKey = BuildAccountKey(account.AccountName);
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
        targets ??= _injector.FindTargetProcesses();
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
                : !string.IsNullOrWhiteSpace(account.CharacterName) ? account.CharacterName
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
                : !string.IsNullOrWhiteSpace(account.CharacterName) ? account.CharacterName
                : "Pending";
            string elapsed = FormatElapsed(DateTime.UtcNow - since);
            return ($"{character} | starting {elapsed}", "#E2B348");
        }

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

            _suppressLaunchTargetCharacterSelectionChanged = true;
            try
            {
                dropdown.ItemsSource = detectedCharacters;
                if (account.CharacterName == LaunchAccountProfile.NoneOption)
                    dropdown.SelectedItem = LaunchAccountProfile.NoneOption;
                else
                    dropdown.SelectedItem = !string.IsNullOrWhiteSpace(account.CharacterName) && detectedCharacters.Contains(account.CharacterName)
                        ? account.CharacterName
                        : null;
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
            .Select((account, index) => $"{FormatOrdinal(index, digits)} {account.DisplayName}")
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
