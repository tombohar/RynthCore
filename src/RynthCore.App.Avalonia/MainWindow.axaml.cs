using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    private static readonly TimeSpan AutoLaunchCooldown = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AutoLaunchServerDownLogCooldown = TimeSpan.FromSeconds(30);
    private readonly AcLaunchArgumentBuilder _launchArgumentBuilder = new();
    private readonly AcClientLaunchSettingsService _launchSettings = new();
    private readonly AcClientLoginAutomationService _loginAutomation = new();
    private readonly EngineInjectionService _injector = new();
    private readonly ServerStatusProbeService _serverStatusProbe = new();
    private readonly ObservableCollection<string> _activityItems = [];
    private readonly ObservableCollection<string> _sessionItems = [];
    private readonly ObservableCollection<string> _pluginDllPaths = [];
    private readonly HashSet<int> _launchedSessionPids = [];
    private readonly Dictionary<string, TextBlock> _launchTargetStatusTexts = [];
    private readonly Dictionary<string, CheckBox> _launchTargetChecks = [];
    private readonly Dictionary<string, ComboBox> _launchTargetCharacterDropdowns = [];
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
        AddPluginDllButton.Click += async (_, _) => await AddPluginDllAsync();

        ServerProfilesList.SelectionChanged += (_, _) => OnPrimarySelectionChanged();
        AccountProfilesList.SelectionChanged += (_, _) => OnPrimarySelectionChanged();
    }

    private void LoadSettings()
    {
        _settings = AppSettingsStore.Load();
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
            Position = new PixelPoint((int)_settings.WindowX.Value, (int)_settings.WindowY.Value);
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
    }

    private void SaveSettings()
    {
        AppSettingsStore.Save(_settings);
    }

    private void SaveWindowLayout()
    {
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

        foreach (string dllPath in _pluginDllPaths)
        {
            string capturedPath = dllPath;
            string pluginName = Path.GetFileNameWithoutExtension(dllPath);

            var checkBox = new CheckBox { Content = pluginName, IsChecked = true };

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
                ColumnDefinitions = new ColumnDefinitions("Auto, *, Auto"),
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

            Grid.SetColumn(checkBox, 0);
            Grid.SetColumn(statusText, 1);
            Grid.SetColumn(charDropdown, 2);

            container.Children.Add(checkBox);
            container.Children.Add(statusText);
            container.Children.Add(charDropdown);

            LaunchTargetsPanel.Children.Add(container);
            _launchTargetStatusTexts[account.Id] = statusText;
            _launchTargetChecks[account.Id] = checkBox;
            _launchTargetCharacterDropdowns[account.Id] = charDropdown;
        }

        if (characterNamesChanged)
            SaveSettings();

        UpdateLaunchTargetStatuses();
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

        if (shouldInject && !GetSelectedPluginIds().Contains("rynthcore-engine", StringComparer.OrdinalIgnoreCase))
        {
            AppendActivity("Launch blocked: RynthCore Engine must stay selected in Runtime Loadout when auto-inject is enabled.");
            return;
        }

        SaveRuntimePaths(appendActivity: !isAutomaticLaunch);
        SaveLaunchBehavior(appendActivity: !isAutomaticLaunch);

        try
        {
            SetOperationState(true);
            _launchSettings.Apply(acPath, _settings.AllowMultipleClients, _settings.SkipIntroVideos, AppendActivity);
            AppendActivity($"Preparing {launchLabel}.");
            HashSet<string> activeAccountKeys = GetActiveAccountKeys();

            for (int i = 0; i < accountsToLaunch.Count; i++)
            {
                LaunchAccountProfile account = accountsToLaunch[i];
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
                LaunchContextStore.WriteLegacy(launchContext);

                if (shouldInject)
                {
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

                    if (result.Success)
                    {
                        if (result.ProcessId is int trackedPid)
                        {
                            _launchedSessionPids.Add(trackedPid);
                            activeAccountKeys.Add(accountKey);
                        }

                        AppendActivity(result.ProcessId is int pid
                            ? $"Launch complete for {launchSummary} (PID {pid})."
                            : $"Launch complete for {launchSummary}.");
                    }
                    else
                    {
                        AppendActivity($"Launch failed for {launchSummary}: {result.Summary}");
                    }
                }
                else
                {
                    try
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo(acPath, launchArguments)
                        {
                            UseShellExecute = false,
                            WorkingDirectory = Path.GetDirectoryName(acPath) ?? string.Empty
                        };
                        var proc = System.Diagnostics.Process.Start(psi);
                        if (proc != null)
                        {
                            _launchedSessionPids.Add(proc.Id);
                            LaunchContextStore.WriteForProcess(proc.Id, launchContext);
                            WritePendingSessionState(proc.Id, launchContext);
                            activeAccountKeys.Add(accountKey);
                        }
                        AppendActivity($"Launched AC (no injection) using {launchSummary}.");
                    }
                    catch (Exception launchEx)
                    {
                        AppendActivity($"Launch failed for {launchSummary}: {launchEx.Message}");
                    }
                }

                if (i < accountsToLaunch.Count - 1)
                    await Task.Delay(500);
            }
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

    private LaunchContextRecord BuildLaunchContext(LaunchAccountProfile account, LaunchServerProfile? server)
    {
        string targetCharacter = ResolveTargetCharacterForLaunch(account, server);
        return LaunchContextStore.CreateRecord(
            account.AccountName,
            server?.Name ?? string.Empty,
            targetCharacter,
            _settings.SkipLoginLogos);
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
        string engineSettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RynthCore",
            "engine.json");

        try
        {
            string? dir = Path.GetDirectoryName(engineSettingsPath);
            if (dir != null) Directory.CreateDirectory(dir);

            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteStartArray("PluginPaths");
                foreach (string p in _pluginDllPaths)
                    w.WriteStringValue(p);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            File.WriteAllBytes(engineSettingsPath, ms.ToArray());
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
        _launchedSessionPids.RemoveWhere(pid => !activePids.Contains(pid));

        AcStatusText.Text = targets.Length switch
        {
            0 => "No running AC clients detected.",
            1 => "1 AC client is running.",
            _ => $"{targets.Length} AC clients are running."
        };

        _sessionItems.Clear();
        foreach (Process process in targets.OrderBy(p => p.Id))
        {
            string readiness = _injector.TryDescribeGraphicsReadiness(process, out string status)
                ? "graphics ready"
                : SimplifyReadinessStatus(status);
            string origin = DescribeSessionOrigin(process, activeContexts, activeSessions);
            _sessionItems.Add($"PID {process.Id}  |  {origin}  |  {readiness}");
        }

        if (_sessionItems.Count == 0)
            _sessionItems.Add("No active sessions yet.");

        UpdateLaunchTargetStatuses(targets, activeContexts, activeSessions);
        MaybeQueueAutoLaunch(activeContexts, activeSessions);
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
