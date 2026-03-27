using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using NexCore.Injector;

namespace NexCore.App;

internal sealed class MainForm : Form
{
    private static readonly Color AppBack = Color.FromArgb(12, 18, 24);
    private static readonly Color PanelBack = Color.FromArgb(20, 28, 36);
    private static readonly Color PanelAlt = Color.FromArgb(15, 22, 30);
    private static readonly Color Border = Color.FromArgb(44, 58, 70);
    private static readonly Color Accent = Color.FromArgb(42, 193, 166);
    private static readonly Color Gold = Color.FromArgb(226, 179, 72);
    private static readonly Color TextPrimary = Color.FromArgb(234, 240, 244);
    private static readonly Color TextMuted = Color.FromArgb(154, 168, 179);
    private static readonly Color Good = Color.FromArgb(102, 214, 135);

    private readonly EngineInjectionService _injector = new();
    private readonly List<PluginDefinition> _plugins =
    [
        new PluginDefinition
        {
            Id = "nexcore-engine",
            Name = "NexCore Engine",
            Summary = "Injects the in-process runtime and overlay host into acclient.exe.",
            StatusText = "Implemented now",
            RuntimeImplemented = true
        },
        new PluginDefinition
        {
            Id = "nexai",
            Name = "NexAi",
            Summary = "Adoption target from NexSuite. Selection is saved now so the desktop host becomes the future loadout surface.",
            StatusText = "Planned adoption",
            RuntimeImplemented = false
        }
    ];

    private readonly Dictionary<string, CheckBox> _pluginChecks = [];
    private readonly HashSet<int> _injectedPids = [];
    private readonly HashSet<int> _pendingInjectionPids = [];
    private readonly HashSet<int> _autoInjectFailedPids = [];
    private readonly object _injectionStateLock = new();
    private readonly System.Windows.Forms.Timer _watchTimer = new() { Interval = 1500 };

    private AppSettings _settings = new();
    private bool _watchTickBusy;
    private bool _operationBusy;

    private readonly TextBox _acPathTextBox = CreateTextBox();
    private readonly TextBox _enginePathTextBox = CreateTextBox();
    private readonly TextBox _launchArgsTextBox = CreateTextBox();
    private readonly CheckBox _autoInjectCheckBox = CreateCheckBox("Auto Inject");
    private readonly Label _statusValueLabel = CreateValueLabel();
    private readonly Label _selectedValueLabel = CreateValueLabel();
    private readonly Label _hotReloadValueLabel = CreateValueLabel();
    private readonly ListBox _activityListBox = CreateActivityListBox();

    private readonly Button _launchButton = CreateActionButton("Launch AC");
    private readonly Button _injectButton = CreateActionButton("Inject Into Running AC");
    private readonly Button _launchAndInjectButton = CreateAccentButton("Launch + Apply Loadout");

    public MainForm()
    {
        Text = "NexCore";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1100, 720);
        ClientSize = new Size(1240, 820);
        BackColor = AppBack;
        ForeColor = TextPrimary;
        Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point);

        BuildLayout();
        LoadSettingsIntoUi();

        _watchTimer.Tick += WatchTimer_Tick;
        _watchTimer.Start();

        _launchButton.Click += async (_, _) => await LaunchAcAsync(injectAfterLaunch: false);
        _injectButton.Click += async (_, _) => await InjectIntoRunningAcAsync();
        _launchAndInjectButton.Click += async (_, _) => await LaunchAcAsync(injectAfterLaunch: true);
        _autoInjectCheckBox.CheckedChanged += (_, _) =>
        {
            SaveUiState();
            UpdateStatusSummary();
        };
        FormClosing += (_, _) => SaveUiState();

        UpdateStatusSummary();
        AppendActivity("Desktop host ready.");
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = AppBack,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(18)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 118f));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        Controls.Add(root);

        root.Controls.Add(BuildHeroPanel(), 0, 0);
        root.Controls.Add(BuildContentPanel(), 0, 1);
    }

    private Control BuildHeroPanel()
    {
        var hero = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = PanelBack,
            Padding = new Padding(18),
            Margin = new Padding(0, 0, 0, 14)
        };
        hero.Paint += DrawPanelBorder;

        var title = new Label
        {
            Text = "NEXCORE DESKTOP HOST",
            ForeColor = Accent,
            Font = new Font("Segoe UI Semibold", 22f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(14, 10)
        };

        var subtitle = new Label
        {
            Text = "Pick the loadout in Windows, launch Asheron's Call, and let the host apply the selected runtime automatically.",
            ForeColor = TextPrimary,
            AutoSize = true,
            MaximumSize = new Size(880, 0),
            Location = new Point(16, 52)
        };

        var note = new Label
        {
            Text = "Hot reload bonus path: the host already watches for new AC sessions and is structured so plugin reapply and future engine-side reload workflows have a place to live.",
            ForeColor = TextMuted,
            AutoSize = true,
            MaximumSize = new Size(930, 0),
            Location = new Point(16, 80)
        };

        hero.Controls.Add(title);
        hero.Controls.Add(subtitle);
        hero.Controls.Add(note);
        return hero;
    }

    private Control BuildContentPanel()
    {
        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = AppBack,
            ColumnCount = 2,
            RowCount = 1
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 41f));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 59f));

        content.Controls.Add(CreateScrollHost(BuildPluginColumn()), 0, 0);
        content.Controls.Add(CreateScrollHost(BuildHostColumn()), 1, 0);
        return content;
    }

    private Control BuildPluginColumn()
    {
        var host = CreateSectionPanel();
        host.Dock = DockStyle.Top;
        host.AutoSize = true;
        host.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        host.Margin = new Padding(0, 0, 10, 0);

        var layout = CreateSectionLayout(5);
        host.Controls.Add(layout);

        layout.Controls.Add(CreateSectionTitle("Plugin Loadout"), 0, 0);
        layout.Controls.Add(CreateSectionBody("This is the desktop-side selection surface. Today it injects NexCore Engine and remembers the future adoption targets you want attached to the session."), 0, 1);

        var pluginFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            BackColor = PanelBack,
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 0)
        };

        foreach (PluginDefinition plugin in _plugins)
            pluginFlow.Controls.Add(CreatePluginCard(plugin));

        layout.Controls.Add(pluginFlow, 0, 2);

        var footer = CreateFooterLabel("Selections are persisted in AppData so the host can reopen with your preferred loadout.");
        layout.Controls.Add(footer, 0, 3);

        return host;
    }

    private Control BuildHostColumn()
    {
        var host = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            BackColor = AppBack,
            ColumnCount = 1,
            RowCount = 3,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0)
        };
        host.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        host.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        host.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        host.Controls.Add(BuildPathsPanel(), 0, 0);
        host.Controls.Add(BuildSessionPanel(), 0, 1);
        host.Controls.Add(BuildActivityPanel(), 0, 2);
        return host;
    }

    private Control BuildPathsPanel()
    {
        var panel = CreateSectionPanel();
        panel.Dock = DockStyle.Top;
        panel.AutoSize = true;
        panel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        panel.Margin = new Padding(10, 0, 0, 10);

        var layout = CreateSectionLayout(7);
        panel.Controls.Add(layout);

        layout.Controls.Add(CreateSectionTitle("Launcher + Runtime"), 0, 0);
        layout.Controls.Add(CreateSectionBody("Point the host at your AC client and the published NexCore engine. Then you can launch, inject into a running client, or do both in one step."), 0, 1);

        layout.Controls.Add(CreateFieldRow("Asheron's Call", _acPathTextBox, BrowseForAcClient), 0, 2);
        layout.Controls.Add(CreateFieldRow("NexCore Engine", _enginePathTextBox, BrowseForEngineDll), 0, 3);
        layout.Controls.Add(CreateFieldRow("Launch Arguments", _launchArgsTextBox, null), 0, 4);

        var optionsRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            BackColor = PanelBack,
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0, 8, 0, 0)
        };
        optionsRow.Controls.Add(_autoInjectCheckBox);
        optionsRow.Controls.Add(CreateSectionBody("When enabled, NexCore injects clients it launches and newly detected AC clients."));
        layout.Controls.Add(optionsRow, 0, 5);

        var buttonRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            BackColor = PanelBack,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0, 10, 0, 0)
        };
        buttonRow.Controls.Add(_launchButton);
        buttonRow.Controls.Add(_injectButton);
        buttonRow.Controls.Add(_launchAndInjectButton);
        layout.Controls.Add(buttonRow, 0, 6);

        return panel;
    }

    private Control BuildSessionPanel()
    {
        var panel = CreateSectionPanel();
        panel.Dock = DockStyle.Top;
        panel.AutoSize = true;
        panel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        panel.Margin = new Padding(10, 0, 0, 10);

        var layout = CreateSectionLayout(2);
        panel.Controls.Add(layout);

        layout.Controls.Add(CreateSectionTitle("Session State"), 0, 0);

        var stateGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            BackColor = PanelBack,
            ColumnCount = 2,
            RowCount = 3,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 6, 0, 0)
        };
        stateGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190f));
        stateGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        AddStateRow(stateGrid, 0, "AC Status", _statusValueLabel);
        AddStateRow(stateGrid, 1, "Selected Loadout", _selectedValueLabel);
        AddStateRow(stateGrid, 2, "Hot Reload Path", _hotReloadValueLabel);

        layout.Controls.Add(stateGrid, 0, 1);
        return panel;
    }

    private Control BuildActivityPanel()
    {
        var panel = CreateSectionPanel();
        panel.Dock = DockStyle.Top;
        panel.AutoSize = true;
        panel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        panel.Margin = new Padding(10, 0, 0, 0);

        var layout = CreateSectionLayout(3);
        panel.Controls.Add(layout);

        _activityListBox.Dock = DockStyle.Top;
        _activityListBox.Height = 280;

        layout.Controls.Add(CreateSectionTitle("Activity"), 0, 0);
        layout.Controls.Add(CreateSectionBody("Runtime decisions, launch actions, injection output, and future plugin-host events surface here."), 0, 1);
        layout.Controls.Add(_activityListBox, 0, 2);

        return panel;
    }

    private Panel CreatePluginCard(PluginDefinition plugin)
    {
        var card = new Panel
        {
            Width = 420,
            Height = 124,
            BackColor = PanelAlt,
            Margin = new Padding(0, 0, 0, 10),
            Padding = new Padding(14)
        };
        card.Paint += DrawPanelBorder;

        var checkBox = CreateCheckBox(plugin.Name);
        checkBox.Font = new Font("Segoe UI Semibold", 11f, FontStyle.Bold);
        checkBox.Location = new Point(12, 10);
        checkBox.CheckedChanged += (_, _) =>
        {
            UpdateStatusSummary();
            SaveUiState();
        };

        var badge = new Label
        {
            Text = plugin.StatusText.ToUpperInvariant(),
            ForeColor = plugin.RuntimeImplemented ? Good : Gold,
            AutoSize = true,
            Location = new Point(16, 42),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
        };

        var summary = new Label
        {
            Text = plugin.Summary,
            ForeColor = TextMuted,
            AutoSize = true,
            MaximumSize = new Size(370, 0),
            Location = new Point(16, 66)
        };

        card.Controls.Add(checkBox);
        card.Controls.Add(badge);
        card.Controls.Add(summary);

        _pluginChecks[plugin.Id] = checkBox;
        return card;
    }

    private void LoadSettingsIntoUi()
    {
        _settings = AppSettingsStore.Load();

        _acPathTextBox.Text = _settings.AcClientPath;
        _enginePathTextBox.Text = !string.IsNullOrWhiteSpace(_settings.EnginePath)
            ? _settings.EnginePath
            : _injector.TryResolveEnginePath(null) ?? string.Empty;
        _launchArgsTextBox.Text = _settings.LaunchArguments;
        _autoInjectCheckBox.Checked = _settings.AutoInjectAfterLaunch || _settings.WatchForAcStart;

        foreach ((string pluginId, CheckBox checkBox) in _pluginChecks)
            checkBox.Checked = _settings.EnabledPluginIds.Contains(pluginId, StringComparer.OrdinalIgnoreCase);

        if (!_pluginChecks.Values.Any(cb => cb.Checked) && _pluginChecks.TryGetValue("nexcore-engine", out CheckBox? engineCheck))
            engineCheck.Checked = true;
    }

    private void SaveUiState()
    {
        _settings.AcClientPath = _acPathTextBox.Text.Trim();
        _settings.EnginePath = _enginePathTextBox.Text.Trim();
        _settings.LaunchArguments = _launchArgsTextBox.Text.Trim();
        _settings.AutoInjectAfterLaunch = _autoInjectCheckBox.Checked;
        _settings.WatchForAcStart = _autoInjectCheckBox.Checked;
        _settings.EnabledPluginIds = GetSelectedPluginIds().ToList();
        AppSettingsStore.Save(_settings);
    }

    private async Task LaunchAcAsync(bool injectAfterLaunch)
    {
        if (_operationBusy)
            return;

        string acPath = _acPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(acPath) || !File.Exists(acPath))
        {
            AppendActivity("Asheron's Call path is missing or invalid.");
            return;
        }

        SaveUiState();

        try
        {
            SetOperationState(true);

            var startInfo = new ProcessStartInfo(acPath)
            {
                WorkingDirectory = Path.GetDirectoryName(acPath) ?? string.Empty,
                Arguments = _launchArgsTextBox.Text.Trim()
            };

            Process? launched = Process.Start(startInfo);
            if (launched == null)
            {
                AppendActivity("AC launch failed: Process.Start returned null.");
                return;
            }

            AppendActivity($"Launched AC (PID {launched.Id}).");

            bool shouldInject = injectAfterLaunch || _autoInjectCheckBox.Checked;
            if (shouldInject)
                QueueAutoInject(launched, "Applying selected loadout after launch");
        }
        catch (Exception ex)
        {
            AppendActivity($"Launch failed: {ex.Message}");
        }
        finally
        {
            SetOperationState(false);
            UpdateStatusSummary();
        }
    }

    private async Task InjectIntoRunningAcAsync()
    {
        Process[] targets = _injector.FindTargetProcesses();
        if (targets.Length == 0)
        {
            AppendActivity("No running acclient.exe process found.");
            UpdateStatusSummary();
            return;
        }

        await ApplySelectedLoadoutAsync(targets[0], "Applying selected loadout to running AC");
    }

    private async Task ApplySelectedLoadoutAsync(Process targetProcess, string reason)
    {
        if (_operationBusy)
            return;

        string enginePath = _enginePathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(enginePath))
            enginePath = _injector.TryResolveEnginePath(null) ?? string.Empty;

        string[] selectedPluginIds = GetSelectedPluginIds().ToArray();
        if (selectedPluginIds.Length == 0)
        {
            AppendActivity("No plugins selected.");
            return;
        }

        SaveUiState();
        SetOperationState(true);

        try
        {
            AppendActivity(reason + $" (PID {targetProcess.Id}).");

            if (selectedPluginIds.Contains("nexai", StringComparer.OrdinalIgnoreCase))
            {
                AppendActivity("NexAi is selected and saved in the loadout. Runtime adoption into NexCore is not wired yet, so it is queued as a host-level intent.");
            }

            if (!selectedPluginIds.Contains("nexcore-engine", StringComparer.OrdinalIgnoreCase))
            {
                AppendActivity("NexCore Engine is not selected, so there is nothing injectable for this session yet.");
                return;
            }

            InjectionResult result = await Task.Run(() => _injector.InjectIntoProcess(targetProcess, enginePath, AppendActivity));
            if (result.Success)
            {
                lock (_injectionStateLock)
                    _injectedPids.Add(targetProcess.Id);

                AppendActivity($"Injection complete for PID {targetProcess.Id}.");
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
            UpdateStatusSummary();
        }
    }

    private void WatchTimer_Tick(object? sender, EventArgs e)
    {
        UpdateStatusSummary();

        if (_watchTickBusy || !_autoInjectCheckBox.Checked || _operationBusy)
            return;

        _watchTickBusy = true;
        try
        {
            Process[] processes = _injector.FindTargetProcesses();
            PruneInjectedPidCache(processes);

            Process? nextProcess = processes.FirstOrDefault(process => !IsHandledOrPending(process.Id));
            if (nextProcess == null)
                return;

            QueueAutoInject(nextProcess, "Detected a new AC session");
        }
        finally
        {
            _watchTickBusy = false;
        }
    }

    private void PruneInjectedPidCache(IEnumerable<Process> processes)
    {
        var activePids = processes.Select(p => p.Id).ToHashSet();

        lock (_injectionStateLock)
        {
            _injectedPids.RemoveWhere(pid => !activePids.Contains(pid));
            _pendingInjectionPids.RemoveWhere(pid => !activePids.Contains(pid));
            _autoInjectFailedPids.RemoveWhere(pid => !activePids.Contains(pid));
        }
    }

    private IEnumerable<string> GetSelectedPluginIds()
    {
        return _pluginChecks
            .Where(pair => pair.Value.Checked)
            .Select(pair => pair.Key);
    }

    private void UpdateStatusSummary()
    {
        Process[] targets = _injector.FindTargetProcesses();
        _statusValueLabel.Text = targets.Length == 0
            ? "Waiting for acclient.exe"
            : $"{targets.Length} client session(s) detected";

        string[] selectedNames = _plugins
            .Where(plugin => _pluginChecks.TryGetValue(plugin.Id, out CheckBox? checkBox) && checkBox.Checked)
            .Select(plugin => plugin.Name)
            .ToArray();

        _selectedValueLabel.Text = selectedNames.Length == 0 ? "None" : string.Join(", ", selectedNames);
        _hotReloadValueLabel.Text = _autoInjectCheckBox.Checked
            ? "Auto Inject is on and now boots NexCore early so the engine can wait for the real D3D9 device in-process"
            : "Auto Inject is off; use Launch + Apply Loadout or Inject Into Running AC";
    }

    private void SetOperationState(bool busy)
    {
        _operationBusy = busy;
        _launchButton.Enabled = !busy;
        _injectButton.Enabled = !busy;
        _launchAndInjectButton.Enabled = !busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }

    private void BrowseForAcClient()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Asheron's Call Client|acclient.exe|Executable Files|*.exe|All Files|*.*",
            Title = "Select acclient.exe"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        _acPathTextBox.Text = dialog.FileName;
        SaveUiState();
    }

    private void BrowseForEngineDll()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "NexCore Engine|NexCore.Engine.dll|DLL Files|*.dll|All Files|*.*",
            Title = "Select NexCore.Engine.dll"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        _enginePathTextBox.Text = dialog.FileName;
        SaveUiState();
    }

    private void AppendActivity(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string>(AppendActivity), message);
            return;
        }

        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _activityListBox.Items.Add(line);
        while (_activityListBox.Items.Count > 500)
            _activityListBox.Items.RemoveAt(0);

        _activityListBox.TopIndex = _activityListBox.Items.Count - 1;
    }

    private void QueueAutoInject(Process targetProcess, string reason)
    {
        bool shouldQueue;
        lock (_injectionStateLock)
        {
            shouldQueue =
                !_injectedPids.Contains(targetProcess.Id) &&
                !_autoInjectFailedPids.Contains(targetProcess.Id) &&
                _pendingInjectionPids.Add(targetProcess.Id);
        }

        if (!shouldQueue)
            return;

        AppendActivity($"Queued auto-inject for PID {targetProcess.Id}.");

        _ = Task.Run(async () =>
        {
            try
            {
                AppendActivity($"PID {targetProcess.Id}: injecting early so NexCore can bootstrap against the real D3D9 device.");

                while (true)
                {
                    targetProcess.Refresh();
                    if (targetProcess.HasExited)
                    {
                        AppendActivity($"PID {targetProcess.Id}: AC exited before auto-inject could run.");
                        return;
                    }

                    break;
                }

                while (_operationBusy)
                    await Task.Delay(500);

                if (IsHandledOrPending(targetProcess.Id) && !targetProcess.HasExited)
                {
                    lock (_injectionStateLock)
                    {
                        if (_injectedPids.Contains(targetProcess.Id))
                            return;
                    }
                }

                await ApplySelectedLoadoutAsync(targetProcess, reason);

                lock (_injectionStateLock)
                {
                    if (!_injectedPids.Contains(targetProcess.Id))
                        _autoInjectFailedPids.Add(targetProcess.Id);
                }
            }
            catch (Exception ex)
            {
                lock (_injectionStateLock)
                    _autoInjectFailedPids.Add(targetProcess.Id);

                AppendActivity($"Auto-inject wait failed for PID {targetProcess.Id}: {ex.Message}");
            }
            finally
            {
                lock (_injectionStateLock)
                    _pendingInjectionPids.Remove(targetProcess.Id);

                if (!IsDisposed && IsHandleCreated)
                    BeginInvoke(new Action(UpdateStatusSummary));
            }
        });
    }

    private bool IsHandledOrPending(int pid)
    {
        lock (_injectionStateLock)
            return _injectedPids.Contains(pid) || _pendingInjectionPids.Contains(pid);
    }

    private static TableLayoutPanel CreateSectionLayout(int rowCount, int? fillRowIndex = null)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            BackColor = PanelBack,
            ColumnCount = 1,
            RowCount = rowCount,
            Padding = new Padding(14),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        for (int i = 0; i < rowCount; i++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }
        return layout;
    }

    private static Panel CreateSectionPanel()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = PanelBack
        };
        panel.Paint += DrawPanelBorder;
        return panel;
    }

    private static Panel CreateScrollHost(Control content)
    {
        var host = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = AppBack,
            AutoScroll = true,
            Padding = new Padding(0, 0, 6, 0)
        };

        bool updating = false;
        content.Location = Point.Empty;
        content.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        host.Controls.Add(content);

        void UpdateScrollLayout(object? _, EventArgs __)
        {
            if (updating)
                return;

            try
            {
                updating = true;
                int availableWidth = Math.Max(1, host.ClientSize.Width - host.Padding.Horizontal);
                Size preferredSize = content.GetPreferredSize(new Size(availableWidth, 0));
                int width = Math.Max(availableWidth, preferredSize.Width);
                int height = Math.Max(preferredSize.Height, content.Height);

                if (content.Size.Width != width || content.Size.Height != height)
                    content.Size = new Size(width, height);

                host.AutoScrollMinSize = new Size(width, height);
            }
            finally
            {
                updating = false;
            }
        }

        host.Resize += UpdateScrollLayout;
        host.Layout += UpdateScrollLayout;
        content.SizeChanged += UpdateScrollLayout;
        UpdateScrollLayout(null, EventArgs.Empty);
        return host;
    }

    private static Label CreateSectionTitle(string text)
    {
        return new Label
        {
            Text = text,
            ForeColor = TextPrimary,
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 14f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 4)
        };
    }

    private static Label CreateSectionBody(string text)
    {
        return new Label
        {
            Text = text,
            ForeColor = TextMuted,
            AutoSize = true,
            MaximumSize = new Size(620, 0),
            Margin = new Padding(0, 0, 0, 8)
        };
    }

    private static Label CreateFooterLabel(string text)
    {
        return new Label
        {
            Text = text,
            ForeColor = TextMuted,
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            Margin = new Padding(0, 8, 0, 0)
        };
    }

    private static Panel CreateFieldRow(string labelText, TextBox textBox, Action? browseAction)
    {
        var row = new Panel
        {
            Dock = DockStyle.Top,
            Height = 74,
            BackColor = PanelBack,
            Margin = new Padding(0, 0, 0, 4)
        };

        var label = new Label
        {
            Text = labelText,
            ForeColor = TextMuted,
            AutoSize = true,
            Location = new Point(0, 4)
        };

        textBox.Location = new Point(0, 28);
        textBox.Width = browseAction == null ? 520 : 430;

        row.Controls.Add(label);
        row.Controls.Add(textBox);

        if (browseAction != null)
        {
            var browseButton = CreateActionButton("Browse");
            browseButton.Location = new Point(textBox.Right + 10, 26);
            browseButton.Width = 90;
            browseButton.Click += (_, _) => browseAction();
            row.Controls.Add(browseButton);
        }

        return row;
    }

    private static void AddStateRow(TableLayoutPanel layout, int rowIndex, string labelText, Label valueLabel)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));

        var nameLabel = new Label
        {
            Text = labelText,
            ForeColor = TextMuted,
            AutoSize = true,
            Anchor = AnchorStyles.Left
        };

        valueLabel.Anchor = AnchorStyles.Left;
        layout.Controls.Add(nameLabel, 0, rowIndex);
        layout.Controls.Add(valueLabel, 1, rowIndex);
    }

    private static TextBox CreateTextBox()
    {
        return new TextBox
        {
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = PanelAlt,
            ForeColor = TextPrimary
        };
    }

    private static CheckBox CreateCheckBox(string text)
    {
        return new CheckBox
        {
            Text = text,
            AutoSize = true,
            ForeColor = TextPrimary,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 6)
        };
    }

    private static Button CreateActionButton(string text)
    {
        var button = new Button
        {
            Text = text,
            Height = 38,
            Width = 168,
            FlatStyle = FlatStyle.Flat,
            BackColor = PanelAlt,
            ForeColor = TextPrimary,
            Margin = new Padding(0, 0, 10, 0)
        };
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.BorderSize = 1;
        return button;
    }

    private static Button CreateAccentButton(string text)
    {
        var button = new Button
        {
            Text = text,
            Height = 38,
            Width = 210,
            FlatStyle = FlatStyle.Flat,
            BackColor = Accent,
            ForeColor = Color.Black,
            Margin = new Padding(0)
        };
        button.FlatAppearance.BorderColor = Accent;
        button.FlatAppearance.BorderSize = 1;
        return button;
    }

    private static Label CreateValueLabel()
    {
        return new Label
        {
            AutoSize = true,
            ForeColor = TextPrimary,
            Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold)
        };
    }

    private static ListBox CreateActivityListBox()
    {
        return new ListBox
        {
            Dock = DockStyle.Fill,
            BackColor = PanelAlt,
            ForeColor = TextPrimary,
            BorderStyle = BorderStyle.None,
            HorizontalScrollbar = true,
            IntegralHeight = false
        };
    }

    private static void DrawPanelBorder(object? sender, PaintEventArgs e)
    {
        if (sender is not Control control)
            return;

        using var borderPen = new Pen(Border);
        e.Graphics.DrawRectangle(borderPen, 0, 0, control.Width - 1, control.Height - 1);

        using var accentPen = new Pen(Gold);
        e.Graphics.DrawLine(accentPen, 0, 0, Math.Min(86, control.Width - 1), 0);
    }
}
