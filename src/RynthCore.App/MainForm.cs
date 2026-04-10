using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using RynthCore.Injector;

namespace RynthCore.App;

internal sealed class MainForm : Form
{
    private const int WsExComposited = 0x02000000;
    private const string LauncherTabId = "launcher";
    private const string RuntimeTabId = "runtime";
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
    private readonly AcClientLaunchSettingsService _launchSettings = new();
    private readonly AcLaunchArgumentBuilder _launchArgumentBuilder = new();
    private readonly AcClientLoginAutomationService _loginAutomation = new();
    private readonly List<PluginDefinition> _plugins =
    [
        new PluginDefinition
        {
            Id = "rynthcore-engine",
            Name = "RynthCore Engine",
            Summary = "Injects the in-process runtime and overlay host into acclient.exe.",
            StatusText = "Implemented now",
            RuntimeImplemented = true
        },
        new PluginDefinition
        {
            Id = "nexai",
            Name = "RynthAi",
            Summary = "Adoption target from RynthSuite. Selection is saved now so the desktop host becomes the future loadout surface.",
            StatusText = "Planned adoption",
            RuntimeImplemented = false
        }
    ];

    private readonly Dictionary<string, CheckBox> _pluginChecks = [];
    private readonly HashSet<int> _injectedPids = [];
    private readonly HashSet<int> _pendingInjectionPids = [];
    private readonly HashSet<int> _autoInjectFailedPids = [];
    private readonly object _injectionStateLock = new();
    private readonly object _activityLock = new();
    private readonly System.Windows.Forms.Timer _watchTimer = new() { Interval = 1500 };
    private readonly System.Windows.Forms.Timer _activityFlushTimer = new() { Interval = 120 };
    private readonly Queue<string> _pendingActivityLines = new();

    private AppSettings _settings = new();
    private bool _loadingUiState;
    private bool _rebindingLaunchAccountChecklist;
    private bool _watchTickBusy;
    private bool _operationBusy;

    private readonly TextBox _acPathTextBox = CreateTextBox();
    private readonly TextBox _enginePathTextBox = CreateTextBox();
    private readonly TextBox _launchArgsTextBox = CreateTextBox();
    private readonly ComboBox _serverComboBox = CreateComboBox();
    private readonly ComboBox _accountComboBox = CreateComboBox();
    private readonly CheckedListBox _launchAccountsCheckedList = CreateCheckedListBox();
    private readonly CheckBox _autoInjectCheckBox = CreateCheckBox("Auto Inject");
    private readonly CheckBox _allowMultipleClientsCheckBox = CreateCheckBox("Allow Multiple Clients");
    private readonly CheckBox _skipIntroVideosCheckBox = CreateCheckBox("Skip Intro Videos");
    private readonly CheckBox _skipLoginLogosCheckBox = CreateCheckBox("Skip Login Logos");
    private readonly Label _statusValueLabel = CreateValueLabel();
    private readonly Label _selectedValueLabel = CreateValueLabel();
    private readonly Label _launchProfileValueLabel = CreateValueLabel();
    private readonly Label _hotReloadValueLabel = CreateValueLabel();
    private readonly ListBox _activityListBox = CreateActivityListBox();
    private readonly TabControl _mainTabs = CreateMainTabs();

    private readonly Button _launchButton = CreateAccentButton("Launch");
    private readonly Button _injectButton = CreateActionButton("Inject RynthCore");

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WsExComposited;
            return cp;
        }
    }

    public MainForm()
    {
        Text = "RynthCore";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1100, 720);
        ClientSize = new Size(1240, 820);
        BackColor = AppBack;
        ForeColor = TextPrimary;
        Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point);
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        UpdateStyles();

        BuildLayout();
        LoadSettingsIntoUi();

        _watchTimer.Tick += WatchTimer_Tick;
        _watchTimer.Start();
        _activityFlushTimer.Tick += (_, _) => FlushPendingActivity();
        _activityFlushTimer.Start();

        _launchButton.Click += async (_, _) => await LaunchAcAsync(injectAfterLaunch: _autoInjectCheckBox.Checked);
        _injectButton.Click += async (_, _) => await InjectIntoRunningAcAsync();
        _autoInjectCheckBox.CheckedChanged += (_, _) =>
        {
            SaveUiState();
            UpdateStatusSummary();
        };
        _launchArgsTextBox.TextChanged += (_, _) =>
        {
            SaveUiState();
            UpdateLaunchPreview();
            UpdateStatusSummary();
        };
        _serverComboBox.SelectedIndexChanged += (_, _) =>
        {
            SaveUiState();
            UpdateLaunchPreview();
            UpdateStatusSummary();
        };
        _accountComboBox.SelectedIndexChanged += (_, _) =>
        {
            SaveUiState();
            UpdateLaunchPreview();
            UpdateStatusSummary();
        };
        _launchAccountsCheckedList.ItemCheck += (_, _) =>
        {
            if (_rebindingLaunchAccountChecklist)
                return;

            BeginInvoke(new Action(() =>
            {
                SaveUiState();
                UpdateStatusSummary();
            }));
        };
        _mainTabs.SelectedIndexChanged += (_, _) => SaveUiState();
        _allowMultipleClientsCheckBox.CheckedChanged += (_, _) => SaveAndApplyLaunchSettings();
        _skipIntroVideosCheckBox.CheckedChanged += (_, _) => SaveAndApplyLaunchSettings();
        _skipLoginLogosCheckBox.CheckedChanged += (_, _) => SaveUiState();
        FormClosing += (_, _) => SaveUiState();

        UpdateStatusSummary();
        AppendActivity("Desktop host ready.");
    }

    private void BuildLayout()
    {
        var root = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = AppBack,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(18)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 136f));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        Controls.Add(root);

        root.Controls.Add(BuildHeroPanel(), 0, 0);
        root.Controls.Add(BuildContentPanel(), 0, 1);
    }

    private Control BuildHeroPanel()
    {
        var hero = new BufferedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = PanelBack,
            Padding = new Padding(18),
            Margin = new Padding(0, 0, 0, 14)
        };
        hero.Paint += DrawPanelBorder;

        var heroLayout = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = PanelBack,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        heroLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        heroLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240f));

        var copyPanel = new BufferedFlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = PanelBack,
            FlowDirection = FlowDirection.TopDown,
            AutoSize = false,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };

        var title = new Label
        {
            Text = "RYNTHCORE DESKTOP HOST",
            ForeColor = Accent,
            Font = new Font("Segoe UI Semibold", 22f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(14, 10, 0, 0)
        };

        var subtitle = new Label
        {
            Text = "Launcher first: pick a server, choose the character/account slots you want, and start Asheron's Call from RynthCore. Runtime paths and advanced settings live in the secondary tab.",
            ForeColor = TextPrimary,
            AutoSize = true,
            MaximumSize = new Size(760, 0),
            Margin = new Padding(16, 6, 0, 0)
        };

        var note = new Label
        {
            Text = "Launch is now the default path. Runtime keeps the paths, toggles, and manual injection action for recovery cases, while normal play starts here with RynthCore already in the process.",
            ForeColor = TextMuted,
            AutoSize = true,
            MaximumSize = new Size(760, 0),
            Margin = new Padding(16, 6, 0, 0)
        };

        var actionHost = new BufferedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = PanelBack,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };

        _launchButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _launchButton.Width = 184;
        _launchButton.Height = 42;
        _launchButton.Location = new Point(32, 18);

        var launchNote = new Label
        {
            Text = "Launch uses the selected server and checked targets, then injects RynthCore before AC gets moving.",
            ForeColor = TextMuted,
            AutoSize = false,
            Size = new Size(184, 50),
            Location = new Point(32, 68),
            TextAlign = ContentAlignment.TopRight
        };

        copyPanel.Controls.Add(title);
        copyPanel.Controls.Add(subtitle);
        copyPanel.Controls.Add(note);
        actionHost.Controls.Add(_launchButton);
        actionHost.Controls.Add(launchNote);
        heroLayout.Controls.Add(copyPanel, 0, 0);
        heroLayout.Controls.Add(actionHost, 1, 0);
        hero.Controls.Add(heroLayout);
        return hero;
    }

    private Control BuildContentPanel()
    {
        var launcherPage = CreateTabPage("Launcher");
        launcherPage.Tag = LauncherTabId;
        launcherPage.Controls.Add(CreateScrollHost(BuildLauncherColumn()));

        var runtimePage = CreateTabPage("Runtime");
        runtimePage.Tag = RuntimeTabId;
        runtimePage.Controls.Add(CreateScrollHost(BuildRuntimeColumn()));

        _mainTabs.TabPages.Add(launcherPage);
        _mainTabs.TabPages.Add(runtimePage);
        return _mainTabs;
    }

    private Control BuildLauncherColumn()
    {
        var host = new BufferedTableLayoutPanel
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

        host.Controls.Add(BuildLaunchProfilesPanel(), 0, 0);
        host.Controls.Add(BuildSessionPanel(), 0, 1);
        host.Controls.Add(BuildActivityPanel(), 0, 2);
        return host;
    }

    private Control BuildRuntimeColumn()
    {
        var host = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Top,
            BackColor = AppBack,
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0)
        };
        host.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        host.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        host.Controls.Add(BuildPathsPanel(), 0, 0);
        host.Controls.Add(BuildPluginColumn(), 0, 1);
        return host;
    }

    private Control BuildPluginColumn()
    {
        var host = CreateSectionPanel();
        host.Dock = DockStyle.Top;
        host.AutoSize = true;
        host.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        host.Margin = new Padding(10, 0, 0, 0);

        var layout = CreateSectionLayout(5);
        host.Controls.Add(layout);

        layout.Controls.Add(CreateSectionTitle("Runtime Loadout"), 0, 0);
        layout.Controls.Add(CreateSectionBody("These runtime selections are still saved and applied when you launch from RynthCore, but they now live behind the secondary tab so the launcher can stay front and center."), 0, 1);

        var pluginFlow = new BufferedFlowLayoutPanel
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

        var footer = CreateFooterLabel("Selections are persisted in AppData so RynthCore can reopen with your preferred runtime loadout.");
        layout.Controls.Add(footer, 0, 3);

        return host;
    }

    private Control BuildHostColumn()
    {
        var host = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Top,
            BackColor = AppBack,
            ColumnCount = 1,
            RowCount = 4,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0)
        };
        host.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        host.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        host.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        host.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        host.Controls.Add(BuildPathsPanel(), 0, 0);
        host.Controls.Add(BuildLaunchProfilesPanel(), 0, 1);
        host.Controls.Add(BuildSessionPanel(), 0, 2);
        host.Controls.Add(BuildActivityPanel(), 0, 3);
        return host;
    }

    private Control BuildPathsPanel()
    {
        var panel = CreateSectionPanel();
        panel.Dock = DockStyle.Top;
        panel.AutoSize = true;
        panel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        panel.Margin = new Padding(10, 0, 0, 10);

        var layout = CreateSectionLayout(8);
        panel.Controls.Add(layout);

        layout.Controls.Add(CreateSectionTitle("Runtime Settings"), 0, 0);
        layout.Controls.Add(CreateSectionBody("These are the settings you rarely need to touch: client path, engine path, manual argument override, and the runtime behavior flags that RynthCore applies before launch."), 0, 1);

        layout.Controls.Add(CreateFieldRow("Asheron's Call", _acPathTextBox, BrowseForAcClient), 0, 2);
        layout.Controls.Add(CreateFieldRow("RynthCore Engine", _enginePathTextBox, BrowseForEngineDll), 0, 3);
        layout.Controls.Add(CreateFieldRow("Arguments Override (Optional)", _launchArgsTextBox, null), 0, 4);

        var optionsRow = new BufferedFlowLayoutPanel
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
        optionsRow.Controls.Add(CreateSectionBody("Launch from RynthCore already injects early. Auto Inject only matters for AC sessions that appear outside the launcher."));
        optionsRow.Controls.Add(_allowMultipleClientsCheckBox);
        optionsRow.Controls.Add(CreateSectionBody("Writes ComputeUniquePort=True into Documents\\Asheron's Call\\UserPreferences.ini and, for RynthCore-launched sessions, patches AC's already-running gate before startup."));
        optionsRow.Controls.Add(_skipIntroVideosCheckBox);
        optionsRow.Controls.Add(CreateSectionBody("Parks turbine_logo_ac.avi in the AC install folder so launches skip the intro movie. Turn it off to restore the file."));
        optionsRow.Controls.Add(_skipLoginLogosCheckBox);
        optionsRow.Controls.Add(CreateSectionBody("For RynthCore-launched sessions, sends the same early login-screen clicks that Decal-style launchers use to bypass the logo screens after the client window appears."));
        layout.Controls.Add(optionsRow, 0, 5);

        layout.Controls.Add(CreateRuntimeActionRow(), 0, 6);
        layout.Controls.Add(CreateFooterLabel("The launcher tab is the normal entry point now. Come back here only when you need to change engine paths, toggles, recovery actions, or the advanced override."), 0, 7);

        return panel;
    }

    private Control BuildLaunchProfilesPanel()
    {
        var panel = CreateSectionPanel();
        panel.Dock = DockStyle.Top;
        panel.AutoSize = true;
        panel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        panel.Margin = new Padding(10, 0, 0, 10);

        var layout = CreateSectionLayout(8);
        panel.Controls.Add(layout);

        layout.Controls.Add(CreateSectionTitle("Launcher"), 0, 0);
        layout.Controls.Add(CreateSectionBody("Pick a server and the launch targets you want. RynthCore will build the emulator-specific command line, launch suspended, and apply the runtime before AC gets to its single-instance checks."), 0, 1);
        layout.Controls.Add(CreateProfileSelectorRow("Server", _serverComboBox, AddServerProfile, EditSelectedServerProfile, DeleteSelectedServerProfile), 0, 2);
        layout.Controls.Add(CreateProfileSelectorRow("Primary Target", _accountComboBox, AddAccountProfile, EditSelectedAccountProfile, DeleteSelectedAccountProfile), 0, 3);
        layout.Controls.Add(CreateSectionBody("These profiles can act like remembered character slots. Give them a character or slot label, check the ones you want to start, and RynthCore will remember that launch set the next time you open the launcher."), 0, 4);
        layout.Controls.Add(CreateCheckedListRow("Launch Targets", _launchAccountsCheckedList), 0, 5);
        layout.Controls.Add(CreateFooterLabel("The checked launch targets are persisted in AppData so you can reopen RynthCore and hit Launch again without rebuilding the list."), 0, 6);

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

        var stateGrid = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Top,
            BackColor = PanelBack,
            ColumnCount = 2,
            RowCount = 4,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 6, 0, 0)
        };
        stateGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190f));
        stateGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        AddStateRow(stateGrid, 0, "AC Status", _statusValueLabel);
        AddStateRow(stateGrid, 1, "Selected Loadout", _selectedValueLabel);
        AddStateRow(stateGrid, 2, "Launch Profile", _launchProfileValueLabel);
        AddStateRow(stateGrid, 3, "Hot Reload Path", _hotReloadValueLabel);

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
        var card = new BufferedPanel
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

    private void AddServerProfile()
    {
        var draft = new LaunchServerProfile();
        if (!LaunchProfileDialogs.TryEditServer(this, draft))
            return;

        _settings.ServerProfiles.Add(draft);
        _settings.SelectedServerProfileId = draft.Id;
        RebindLaunchProfiles();
        SaveUiState();
        UpdateLaunchPreview();
        UpdateStatusSummary();
        AppendActivity($"Saved server profile '{draft.DisplayName}'.");
    }

    private void EditSelectedServerProfile()
    {
        LaunchServerProfile? selected = GetSelectedServerProfile();
        if (selected == null)
        {
            AppendActivity("Select a server profile first.");
            return;
        }

        var draft = selected.Clone();
        if (!LaunchProfileDialogs.TryEditServer(this, draft))
            return;

        selected.CopyFrom(draft);
        _settings.SelectedServerProfileId = selected.Id;
        RebindLaunchProfiles();
        SaveUiState();
        UpdateLaunchPreview();
        UpdateStatusSummary();
        AppendActivity($"Updated server profile '{selected.DisplayName}'.");
    }

    private void DeleteSelectedServerProfile()
    {
        LaunchServerProfile? selected = GetSelectedServerProfile();
        if (selected == null)
        {
            AppendActivity("Select a server profile first.");
            return;
        }

        if (MessageBox.Show(
                this,
                $"Delete server profile '{selected.DisplayName}'?",
                "Delete Server Profile",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        _settings.ServerProfiles.RemoveAll(profile => string.Equals(profile.Id, selected.Id, StringComparison.OrdinalIgnoreCase));
        _settings.SelectedServerProfileId = _settings.ServerProfiles.FirstOrDefault()?.Id ?? string.Empty;
        RebindLaunchProfiles();
        SaveUiState();
        UpdateLaunchPreview();
        UpdateStatusSummary();
        AppendActivity($"Deleted server profile '{selected.DisplayName}'.");
    }

    private void AddAccountProfile()
    {
        var draft = new LaunchAccountProfile();
        if (!LaunchProfileDialogs.TryEditAccount(this, draft))
            return;

        _settings.AccountProfiles.Add(draft);
        _settings.SelectedAccountProfileId = draft.Id;
        RebindLaunchProfiles();
        SaveUiState();
        UpdateLaunchPreview();
        UpdateStatusSummary();
        AppendActivity($"Saved account profile '{draft.DisplayName}'.");
    }

    private void EditSelectedAccountProfile()
    {
        LaunchAccountProfile? selected = GetSelectedAccountProfile();
        if (selected == null)
        {
            AppendActivity("Select an account profile first.");
            return;
        }

        var draft = selected.Clone();
        if (!LaunchProfileDialogs.TryEditAccount(this, draft))
            return;

        selected.CopyFrom(draft);
        _settings.SelectedAccountProfileId = selected.Id;
        RebindLaunchProfiles();
        SaveUiState();
        UpdateLaunchPreview();
        UpdateStatusSummary();
        AppendActivity($"Updated account profile '{selected.DisplayName}'.");
    }

    private void DeleteSelectedAccountProfile()
    {
        LaunchAccountProfile? selected = GetSelectedAccountProfile();
        if (selected == null)
        {
            AppendActivity("Select an account profile first.");
            return;
        }

        if (MessageBox.Show(
                this,
                $"Delete account profile '{selected.DisplayName}'?",
                "Delete Account Profile",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        _settings.AccountProfiles.RemoveAll(profile => string.Equals(profile.Id, selected.Id, StringComparison.OrdinalIgnoreCase));
        _settings.SelectedAccountProfileId = _settings.AccountProfiles.FirstOrDefault()?.Id ?? string.Empty;
        RebindLaunchProfiles();
        SaveUiState();
        UpdateLaunchPreview();
        UpdateStatusSummary();
        AppendActivity($"Deleted account profile '{selected.DisplayName}'.");
    }

    private void RebindLaunchProfiles()
    {
        _serverComboBox.DisplayMember = nameof(LaunchServerProfile.DisplayName);
        _serverComboBox.ValueMember = nameof(LaunchServerProfile.Id);
        _serverComboBox.DataSource = null;
        _serverComboBox.DataSource = _settings.ServerProfiles;

        _accountComboBox.DisplayMember = nameof(LaunchAccountProfile.DisplayName);
        _accountComboBox.ValueMember = nameof(LaunchAccountProfile.Id);
        _accountComboBox.DataSource = null;
        _accountComboBox.DataSource = _settings.AccountProfiles;

        SelectComboValue(_serverComboBox, _settings.SelectedServerProfileId);
        SelectComboValue(_accountComboBox, _settings.SelectedAccountProfileId);

        RebindLaunchAccountChecklist();
    }

    private void RebindLaunchAccountChecklist()
    {
        var validIds = _settings.AccountProfiles
            .Select(account => account.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _settings.CheckedLaunchAccountProfileIds = _settings.CheckedLaunchAccountProfileIds
            .Where(id => validIds.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _rebindingLaunchAccountChecklist = true;
        _launchAccountsCheckedList.BeginUpdate();
        try
        {
            _launchAccountsCheckedList.Items.Clear();
            foreach (LaunchAccountProfile account in _settings.AccountProfiles)
            {
                bool isChecked = _settings.CheckedLaunchAccountProfileIds.Contains(account.Id, StringComparer.OrdinalIgnoreCase);
                _launchAccountsCheckedList.Items.Add(account, isChecked);
            }
        }
        finally
        {
            _launchAccountsCheckedList.EndUpdate();
            _rebindingLaunchAccountChecklist = false;
        }
    }

    private static void SelectComboValue(ComboBox comboBox, string selectedId)
    {
        if (comboBox.Items.Count == 0)
            return;

        if (string.IsNullOrWhiteSpace(selectedId))
        {
            comboBox.SelectedIndex = 0;
            return;
        }

        comboBox.SelectedValue = selectedId;
        if (comboBox.SelectedIndex < 0)
            comboBox.SelectedIndex = 0;
    }

    private LaunchServerProfile? GetSelectedServerProfile()
    {
        return _serverComboBox.SelectedItem as LaunchServerProfile;
    }

    private LaunchAccountProfile? GetSelectedAccountProfile()
    {
        return _accountComboBox.SelectedItem as LaunchAccountProfile;
    }

    private List<LaunchAccountProfile> GetCheckedLaunchAccounts()
    {
        return _launchAccountsCheckedList.CheckedItems
            .OfType<LaunchAccountProfile>()
            .ToList();
    }

    private List<LaunchAccountProfile> GetAccountsToLaunch()
    {
        List<LaunchAccountProfile> checkedAccounts = GetCheckedLaunchAccounts();
        if (checkedAccounts.Count > 0)
            return checkedAccounts;

        LaunchAccountProfile? selected = GetSelectedAccountProfile();
        return selected == null ? [] : [selected];
    }

    private bool TryGetEffectiveLaunchArguments(out string arguments, out string launchSummary, out string error)
    {
        LaunchAccountProfile? account = GetSelectedAccountProfile();
        if (account == null)
        {
            arguments = string.Empty;
            launchSummary = string.Empty;
            error = "Select an account profile, or create one first.";
            return false;
        }

        return TryGetLaunchArgumentsForAccount(account, out arguments, out launchSummary, out error);
    }

    private bool TryGetLaunchArgumentsForAccount(LaunchAccountProfile account, out string arguments, out string launchSummary, out string error)
    {
        string overrideArguments = _launchArgsTextBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(overrideArguments))
        {
            arguments = overrideArguments;
            launchSummary = string.IsNullOrWhiteSpace(account.DisplayName)
                ? "Manual argument override"
                : $"Manual argument override / {account.DisplayName}";
            error = string.Empty;
            return true;
        }

        LaunchServerProfile? server = GetSelectedServerProfile();
        if (server == null)
        {
            arguments = string.Empty;
            launchSummary = string.Empty;
            error = "Select a server profile, or enter a manual arguments override.";
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

    private void UpdateLaunchPreview()
    {
        LaunchServerProfile? selectedServer = GetSelectedServerProfile();
        if (selectedServer != null)
            _settings.SelectedServerProfileId = selectedServer.Id;

        LaunchAccountProfile? selectedAccount = GetSelectedAccountProfile();
        if (selectedAccount != null)
            _settings.SelectedAccountProfileId = selectedAccount.Id;

        if (TryGetEffectiveLaunchArguments(out string arguments, out string summary, out _))
        {
            if (!string.IsNullOrWhiteSpace(summary))
                _launchProfileValueLabel.Text = summary;
        }
    }

    private void LoadSettingsIntoUi()
    {
        _loadingUiState = true;
        _settings = AppSettingsStore.Load();
        _settings.ServerProfiles ??= [];
        _settings.AccountProfiles ??= [];
        _settings.CheckedLaunchAccountProfileIds ??= [];
        _settings.SelectedMainTabId = string.IsNullOrWhiteSpace(_settings.SelectedMainTabId)
            ? LauncherTabId
            : _settings.SelectedMainTabId;

        _acPathTextBox.Text = _settings.AcClientPath;
        _enginePathTextBox.Text = !string.IsNullOrWhiteSpace(_settings.EnginePath)
            ? _settings.EnginePath
            : _injector.TryResolveEnginePath(null) ?? string.Empty;
        _launchArgsTextBox.Text = _settings.LaunchArguments;
        _allowMultipleClientsCheckBox.Checked = _settings.AllowMultipleClients;
        _skipIntroVideosCheckBox.Checked = _settings.SkipIntroVideos;
        _skipLoginLogosCheckBox.Checked = _settings.SkipLoginLogos;
        _autoInjectCheckBox.Checked = _settings.AutoInjectAfterLaunch || _settings.WatchForAcStart;

        foreach ((string pluginId, CheckBox checkBox) in _pluginChecks)
            checkBox.Checked = _settings.EnabledPluginIds.Contains(pluginId, StringComparer.OrdinalIgnoreCase);

        if (!_pluginChecks.Values.Any(cb => cb.Checked) && _pluginChecks.TryGetValue("rynthcore-engine", out CheckBox? engineCheck))
            engineCheck.Checked = true;

        RebindLaunchProfiles();
        UpdateLaunchPreview();
        SelectMainTab(_settings.SelectedMainTabId);
        _loadingUiState = false;
    }

    private void SaveUiState()
    {
        if (_loadingUiState)
            return;

        _settings.AcClientPath = _acPathTextBox.Text.Trim();
        _settings.EnginePath = _enginePathTextBox.Text.Trim();
        _settings.LaunchArguments = _launchArgsTextBox.Text.Trim();
        _settings.SelectedServerProfileId = GetSelectedServerProfile()?.Id ?? _settings.SelectedServerProfileId;
        _settings.SelectedAccountProfileId = GetSelectedAccountProfile()?.Id ?? _settings.SelectedAccountProfileId;
        _settings.CheckedLaunchAccountProfileIds = GetCheckedLaunchAccounts().Select(profile => profile.Id).ToList();
        _settings.SelectedMainTabId = GetSelectedMainTabId();
        _settings.AllowMultipleClients = _allowMultipleClientsCheckBox.Checked;
        _settings.SkipIntroVideos = _skipIntroVideosCheckBox.Checked;
        _settings.SkipLoginLogos = _skipLoginLogosCheckBox.Checked;
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

        List<LaunchAccountProfile> accountsToLaunch = GetAccountsToLaunch();
        if (accountsToLaunch.Count == 0)
        {
            AppendActivity("Launch blocked: choose or check at least one account profile.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(_launchArgsTextBox.Text.Trim()) && accountsToLaunch.Count > 1)
        {
            AppendActivity("Launch blocked: manual argument override can only be used for one account at a time.");
            return;
        }

        if (!TryGetLaunchArgumentsForAccount(accountsToLaunch[0], out _, out string firstLaunchSummary, out string launchError))
        {
            AppendActivity($"Launch blocked: {launchError}");
            return;
        }

        SaveUiState();
        ApplyLaunchSettings();

        try
        {
            SetOperationState(true);
            AppendActivity(accountsToLaunch.Count == 1
                ? $"Preparing AC launch using {firstLaunchSummary}."
                : $"Preparing {accountsToLaunch.Count} AC launches from the selected account checklist.");

            bool shouldInject = injectAfterLaunch || _autoInjectCheckBox.Checked;

            for (int i = 0; i < accountsToLaunch.Count; i++)
            {
                LaunchAccountProfile account = accountsToLaunch[i];
                if (!TryGetLaunchArgumentsForAccount(account, out string launchArguments, out string launchSummary, out string accountError))
                {
                    AppendActivity($"Launch blocked for {account.DisplayName}: {accountError}");
                    continue;
                }

                if (shouldInject)
                {
                    string enginePath = _enginePathTextBox.Text.Trim();
                    InjectionResult launchResult = await Task.Run(() =>
                        _injector.LaunchSuspendedAndInject(
                            acPath,
                            launchArguments,
                            enginePath,
                            AppendActivity));

                    if (launchResult.Success)
                    {
                        if (launchResult.ProcessId is int launchedPid)
                        {
                            lock (_injectionStateLock)
                                _injectedPids.Add(launchedPid);

                            AppendActivity($"Launch + inject complete for PID {launchedPid} using {launchSummary}.");
                            QueueSkipLoginLogos(launchedPid);
                        }
                        else
                        {
                            AppendActivity($"Launch + inject completed using {launchSummary}, but no PID was reported.");
                        }
                    }
                    else
                    {
                        AppendActivity($"Launch + inject failed for {launchSummary}: {launchResult.Summary}");
                    }
                }
                else
                {
                    try
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo(acPath, launchArguments)
                        {
                            UseShellExecute = false
                        };
                        System.Diagnostics.Process.Start(psi);
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
            UpdateStatusSummary();
        }
    }

    private async Task InjectIntoRunningAcAsync()
    {
        if (!GetSelectedPluginIds().Contains("rynthcore-engine", StringComparer.OrdinalIgnoreCase))
        {
            AppendActivity("Inject blocked: RynthCore Engine is not selected in Runtime Loadout.");
            UpdateStatusSummary();
            return;
        }

        Process[] targets = _injector.FindTargetProcesses();
        if (targets.Length == 0)
        {
            AppendActivity("No running acclient.exe process found.");
            UpdateStatusSummary();
            return;
        }

        await ApplySelectedLoadoutAsync(targets[0], "Applying selected loadout to running AC");
    }

    private void QueueSkipLoginLogos(int processId)
    {
        if (!_skipLoginLogosCheckBox.Checked)
            return;

        _ = _loginAutomation.TryBypassLoginLogosAsync(processId, AppendActivity);
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
                AppendActivity("RynthAi is selected and saved in the loadout. Runtime adoption into RynthCore is not wired yet, so it is queued as a host-level intent.");
            }

            if (!selectedPluginIds.Contains("rynthcore-engine", StringComparer.OrdinalIgnoreCase))
            {
                AppendActivity("RynthCore Engine is not selected, so there is nothing injectable for this session yet.");
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
        SetLabelText(_statusValueLabel, targets.Length == 0
            ? "Waiting for acclient.exe"
            : $"{targets.Length} client session(s) detected");

        string[] selectedNames = _plugins
            .Where(plugin => _pluginChecks.TryGetValue(plugin.Id, out CheckBox? checkBox) && checkBox.Checked)
            .Select(plugin => plugin.Name)
            .ToArray();

        SetLabelText(_selectedValueLabel, selectedNames.Length == 0 ? "None" : string.Join(", ", selectedNames));
        SetLabelText(_launchProfileValueLabel, string.Join(", ", GetLaunchProfileParts()));
        SetLabelText(_hotReloadValueLabel, _autoInjectCheckBox.Checked
            ? "Launch always injects RynthCore. Auto Inject is also watching for AC sessions started outside the launcher"
            : "Launch always injects RynthCore. Auto Inject is off for AC sessions started outside the launcher");
    }

    private void SetOperationState(bool busy)
    {
        _operationBusy = busy;
        _launchButton.Enabled = !busy;
        _injectButton.Enabled = !busy;
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
        ApplyLaunchSettings();
    }

    private void BrowseForEngineDll()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "RynthCore Engine|RynthCore.Engine.dll|DLL Files|*.dll|All Files|*.*",
            Title = "Select RynthCore.Engine.dll"
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
        lock (_activityLock)
            _pendingActivityLines.Enqueue(line);
    }

    private void FlushPendingActivity()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(FlushPendingActivity));
            return;
        }

        string[] pending;
        lock (_activityLock)
        {
            if (_pendingActivityLines.Count == 0)
                return;

            pending = _pendingActivityLines.ToArray();
            _pendingActivityLines.Clear();
        }

        _activityListBox.BeginUpdate();
        try
        {
            _activityListBox.Items.AddRange(pending);
            while (_activityListBox.Items.Count > 500)
                _activityListBox.Items.RemoveAt(0);

            if (_activityListBox.Items.Count > 0)
                _activityListBox.TopIndex = _activityListBox.Items.Count - 1;
        }
        finally
        {
            _activityListBox.EndUpdate();
        }
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
                AppendActivity($"PID {targetProcess.Id}: injecting early so RynthCore can bootstrap against the real D3D9 device.");

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

    private void SaveAndApplyLaunchSettings()
    {
        SaveUiState();
        ApplyLaunchSettings();
        UpdateStatusSummary();
    }

    private void ApplyLaunchSettings()
    {
        try
        {
            _launchSettings.Apply(
                _acPathTextBox.Text.Trim(),
                _allowMultipleClientsCheckBox.Checked,
                _skipIntroVideosCheckBox.Checked,
                AppendActivity);
        }
        catch (Exception ex)
        {
            AppendActivity($"Launch settings update failed: {ex.Message}");
        }
    }

    private IEnumerable<string> GetLaunchProfileParts()
    {
        LaunchServerProfile? server = GetSelectedServerProfile();
        LaunchAccountProfile? account = GetSelectedAccountProfile();
        int checkedCount = GetCheckedLaunchAccounts().Count;

        if (server != null)
            yield return $"Server: {server.DisplayName}";
        if (account != null)
            yield return $"Account: {account.DisplayName}";
        if (checkedCount > 0)
            yield return $"Launch set: {checkedCount} checked";
        if (!string.IsNullOrWhiteSpace(_launchArgsTextBox.Text.Trim()))
            yield return "Manual args override";
        yield return _allowMultipleClientsCheckBox.Checked ? "Multi-client on" : "Multi-client off";
        yield return _skipIntroVideosCheckBox.Checked ? "Intro videos skipped" : "Intro videos normal";
    }

    private string GetSelectedMainTabId()
    {
        if (_mainTabs.SelectedTab == null)
            return LauncherTabId;

        return string.Equals(_mainTabs.SelectedTab.Tag as string, RuntimeTabId, StringComparison.Ordinal)
            ? RuntimeTabId
            : LauncherTabId;
    }

    private void SelectMainTab(string tabId)
    {
        foreach (TabPage page in _mainTabs.TabPages)
        {
            if (string.Equals(page.Tag as string, tabId, StringComparison.OrdinalIgnoreCase))
            {
                _mainTabs.SelectedTab = page;
                return;
            }
        }

        if (_mainTabs.TabPages.Count > 0)
            _mainTabs.SelectedIndex = 0;
    }

    private static TabControl CreateMainTabs()
    {
        return new BufferedTabControl
        {
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
            Padding = new Point(18, 8)
        };
    }

    private static TabPage CreateTabPage(string title)
    {
        return new TabPage(title)
        {
            BackColor = AppBack,
            Padding = new Padding(8)
        };
    }

    private static TableLayoutPanel CreateSectionLayout(int rowCount, int? fillRowIndex = null)
    {
        var layout = new BufferedTableLayoutPanel
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
        var panel = new BufferedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = PanelBack
        };
        panel.Paint += DrawPanelBorder;
        return panel;
    }

    private static Panel CreateScrollHost(Control content)
    {
        var host = new BufferedPanel
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
                int availableWidth = Math.Max(1, host.ClientSize.Width - host.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);
                if (content.MaximumSize.Width != availableWidth)
                    content.MaximumSize = new Size(availableWidth, 0);

                Size preferredSize = content.GetPreferredSize(new Size(availableWidth, 0));
                int width = Math.Max(availableWidth, preferredSize.Width);
                int height = Math.Max(preferredSize.Height, content.Height);

                if (content.Size.Width != width || content.Size.Height != height)
                    content.Size = new Size(width, height);

                Size minSize = new(width, height);
                if (host.AutoScrollMinSize != minSize)
                    host.AutoScrollMinSize = minSize;
            }
            finally
            {
                updating = false;
            }
        }

        host.ClientSizeChanged += UpdateScrollLayout;
        content.SizeChanged += UpdateScrollLayout;
        UpdateScrollLayout(null, EventArgs.Empty);
        return host;
    }

    private static void SetLabelText(Label label, string value)
    {
        if (!string.Equals(label.Text, value, StringComparison.Ordinal))
            label.Text = value;
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
        var row = new BufferedPanel
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

    private static Panel CreateProfileSelectorRow(
        string labelText,
        ComboBox comboBox,
        Action addAction,
        Action editAction,
        Action deleteAction)
    {
        var row = new BufferedPanel
        {
            Dock = DockStyle.Top,
            Height = 78,
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

        comboBox.Location = new Point(0, 28);
        comboBox.Width = 332;

        var addButton = CreateActionButton("Add");
        addButton.Location = new Point(comboBox.Right + 10, 26);
        addButton.Width = 74;
        addButton.Click += (_, _) => addAction();

        var editButton = CreateActionButton("Edit");
        editButton.Location = new Point(addButton.Right + 8, 26);
        editButton.Width = 74;
        editButton.Click += (_, _) => editAction();

        var deleteButton = CreateActionButton("Delete");
        deleteButton.Location = new Point(editButton.Right + 8, 26);
        deleteButton.Width = 82;
        deleteButton.Click += (_, _) => deleteAction();

        row.Controls.Add(label);
        row.Controls.Add(comboBox);
        row.Controls.Add(addButton);
        row.Controls.Add(editButton);
        row.Controls.Add(deleteButton);
        return row;
    }

    private static Panel CreateCheckedListRow(string labelText, CheckedListBox listBox)
    {
        var row = new BufferedPanel
        {
            Dock = DockStyle.Top,
            Height = 150,
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

        listBox.Location = new Point(0, 28);
        listBox.Size = new Size(520, 112);

        row.Controls.Add(label);
        row.Controls.Add(listBox);
        return row;
    }

    private Panel CreateRuntimeActionRow()
    {
        var row = new BufferedPanel
        {
            Dock = DockStyle.Top,
            Height = 86,
            BackColor = PanelBack,
            Margin = new Padding(0, 4, 0, 0)
        };

        var body = CreateSectionBody("Use this only when AC is already running and you need to attach RynthCore manually. Normal play should start from the Launch button in the top-right header.");
        body.Location = new Point(0, 46);
        body.MaximumSize = new Size(860, 0);

        var buttonRow = new BufferedFlowLayoutPanel
        {
            Dock = DockStyle.Top,
            BackColor = PanelBack,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = false,
            WrapContents = false,
            Margin = new Padding(0),
            Height = 42
        };

        buttonRow.Controls.Add(_injectButton);
        row.Controls.Add(buttonRow);
        row.Controls.Add(body);
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

    private static TextBox CreateTextBox(bool readOnly = false)
    {
        return new TextBox
        {
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = PanelAlt,
            ForeColor = TextPrimary,
            ReadOnly = readOnly
        };
    }

    private static ComboBox CreateComboBox()
    {
        return new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = PanelAlt,
            ForeColor = TextPrimary
        };
    }

    private static CheckedListBox CreateCheckedListBox()
    {
        return new CheckedListBox
        {
            CheckOnClick = true,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = PanelAlt,
            ForeColor = TextPrimary,
            IntegralHeight = false
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
        return new BufferedListBox
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

internal sealed class BufferedListBox : ListBox
{
    public BufferedListBox()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        UpdateStyles();
    }
}

internal sealed class BufferedPanel : Panel
{
    public BufferedPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        UpdateStyles();
    }
}

internal sealed class BufferedTableLayoutPanel : TableLayoutPanel
{
    public BufferedTableLayoutPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        UpdateStyles();
    }
}

internal sealed class BufferedFlowLayoutPanel : FlowLayoutPanel
{
    public BufferedFlowLayoutPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        UpdateStyles();
    }
}

internal sealed class BufferedTabControl : TabControl
{
    public BufferedTabControl()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        UpdateStyles();
    }
}
