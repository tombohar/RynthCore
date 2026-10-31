using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RynthCore.App;
using Path = System.IO.Path;

namespace RynthCore.App.Avalonia;

/// <summary>What the Headless window needs from the launcher.</summary>
internal sealed class HeadlessWindowHost
{
    /// <summary>The launcher's settings (the window keeps its size and place in them).</summary>
    public required AppSettings Settings { get; init; }
    public required Action SaveSettings { get; init; }
    /// <summary>The status folder rynth-headless writes (AppSettings.HeadlessStatusDir or its default).</summary>
    public required Func<string> StatusDir { get; init; }
    /// <summary>AppSettings.HeadlessSuiteDir ("" = the default).</summary>
    public required Func<string> ConfiguredSuiteDir { get; init; }
    /// <summary>%APPDATA%\RynthCore\headless, where the launcher writes the run configs.</summary>
    public required string LaunchConfigDir { get; init; }
    /// <summary>Asks rynth-headless process &lt;pid&gt; to log out and exit; returns what happened.</summary>
    public required Func<int, string> LogOutProcess { get; init; }
    /// <summary>Whether AutoLaunch is on (a logged-out account is started again).</summary>
    public required Func<bool> AutoLaunch { get; init; }
}

/// <summary>
/// The launcher's Headless window: what each headless character (rynth-headless.exe, no game window)
/// is doing, from its status file, its chat and its log, with a command box, macro on/off and log out.
/// Reads files once a second off the UI thread and writes only command files
/// (RynthCore.App\HeadlessMonitor.cs has the rules and the tests in tools\HeadlessWindowTests).
/// Never shows a password or a config file; no account names (characters and servers only).
/// </summary>
internal partial class HeadlessWindow : Window
{
    private static readonly IBrush BottingBrush = Brush("#26C1A6");
    private static readonly IBrush IdleBrush = Brush("#4A90E2");
    private static readonly IBrush DeadBrush = Brush("#E05A5A");
    private static readonly IBrush LoadingBrush = Brush("#6F7F8A");
    private static readonly IBrush StaleBrush = Brush("#F2C14E");
    private static readonly IBrush TextBrush = Brush("#EAF0F4");
    private static readonly IBrush MutedBrush = Brush("#9AA8B3");
    private static readonly IBrush WarnBrush = Brush("#F2C14E");

    /// <summary>How long the window waits for a chat line after a command.</summary>
    private static readonly TimeSpan ReplyWait = TimeSpan.FromSeconds(20);
    /// <summary>A command file nobody took in this long is removed (the StatusAgent prunes at 60 s too).</summary>
    private static readonly TimeSpan UntakenLimit = TimeSpan.FromSeconds(60);
    /// <summary>How long a found log path is trusted before it is looked up again.</summary>
    private static readonly TimeSpan LogLookupEvery = TimeSpan.FromSeconds(15);

    private readonly HeadlessWindowHost? _host;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly HeadlessRoster _roster = new();
    private readonly ObservableCollection<Control> _items = new();
    private readonly Dictionary<int, Row> _rows = new();
    private int? _selectedId;
    private bool _suppressSelection;
    private bool _refreshing;
    private bool _closed;
    private bool _autoSelected;

    // The log of the selected character (touched by the background read only while _refreshing).
    private string? _logAccount;
    private string? _logPath;
    private List<string> _logDirs = new();
    private DateTime _logLookedUpUtc;
    private (long Length, DateTime Written)? _logStamp;
    private int _logForId;

    // The last command sent from the box, until its reply shows or the wait is over.
    private PendingCommand? _pending;
    // Command files written here and not taken yet (path, written).
    private readonly List<(string Path, DateTime WrittenUtc)> _untaken = new();

    private sealed class Row
    {
        public required Border Root { get; init; }
        public required Ellipse Dot { get; init; }
        public required TextBlock Name { get; init; }
        public required TextBlock Server { get; init; }
        public required ProgressBar Health { get; init; }
    }

    private sealed class PendingCommand
    {
        public required int Id { get; init; }
        public required string Text { get; init; }
        public required string Path { get; init; }
        public required DateTime SentUtc { get; init; }
        public required IReadOnlyList<HeadlessChatLine> ChatAtSend { get; init; }
        public bool Taken { get; set; }
    }

    private sealed class ReadResult
    {
        public List<HeadlessCharacterStatus> Found { get; init; } = new();
        public List<int> Unreadable { get; init; } = new();
        public int LogForId { get; init; }
        public string? LogPath { get; init; }
        /// <summary>Null = unchanged since the last read (or no log).</summary>
        public List<string>? LogLines { get; init; }
        public bool LogMissing { get; init; }
        public string? PendingPath { get; init; }
        public bool PendingFileExists { get; init; }
        public int RemovedUntaken { get; init; }
        public string? Error { get; init; }
    }

    /// <summary>For the XAML designer only.</summary>
    public HeadlessWindow()
    {
        InitializeComponent();
    }

    public HeadlessWindow(HeadlessWindowHost host)
    {
        _host = host;
        InitializeComponent();
        RestoreLayout();
        CharacterList.ItemsSource = _items;
        CharacterList.SelectionChanged += (_, _) => OnSelectionChanged();
        SendButton.Click += async (_, _) => await SendCommandAsync();
        // Tunnel: Enter reaches us before the text box can take it.
        CommandBox.AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await SendCommandAsync();
            }
        }, RoutingStrategies.Tunnel);
        MacroButton.Click += async (_, _) => await ToggleMacroAsync();
        LogOutButton.Click += async (_, _) => await LogOutAsync();
        OpenLogFolderButton.Click += (_, _) => OpenLogFolder();
        _timer.Tick += async (_, _) => await RefreshAsync();
        Opened += async (_, _) =>
        {
            _timer.Start();
            await RefreshAsync();
        };
        Closing += (_, _) => SaveLayout();
        Closed += (_, _) =>
        {
            _closed = true;
            _timer.Stop();
        };
    }

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    // ── size and place ──────────────────────────────────────────────────────

    private void RestoreLayout()
    {
        AppSettings s = _host!.Settings;
        if (s.HeadlessWindowWidth is double w && w >= MinWidth) Width = w;
        if (s.HeadlessWindowHeight is double h && h >= MinHeight) Height = h;
        // The launcher's own rule (MainWindow.LoadSettings): never restore a minimized (-32000) or
        // far off-screen point, which would open the window where nobody can see it.
        if (s.HeadlessWindowX is double x && s.HeadlessWindowY is double y &&
            x > -2000 && y > -2000 && x < 30000 && y < 30000)
        {
            Position = new PixelPoint((int)x, (int)y);
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
    }

    private void SaveLayout()
    {
        if (_host == null || WindowState == WindowState.Minimized || Position.X <= -2000 || Position.Y <= -2000)
            return;
        _host.Settings.HeadlessWindowWidth = Width;
        _host.Settings.HeadlessWindowHeight = Height;
        _host.Settings.HeadlessWindowX = Position.X;
        _host.Settings.HeadlessWindowY = Position.Y;
        _host.SaveSettings();
    }

    // ── refresh: files off the UI thread, then the controls ─────────────────

    private async Task RefreshAsync()
    {
        if (_host == null || _refreshing || _closed)
            return;
        _refreshing = true;
        try
        {
            string statusDir = _host.StatusDir();
            string configuredSuite = _host.ConfiguredSuiteDir();
            HeadlessCharacterStatus? selected = _selectedId is int sid && _roster.TryGet(sid, out var st) ? st : null;
            PendingCommand? pending = _pending;
            DateTime nowUtc = DateTime.UtcNow;
            ReadResult result = await Task.Run(() => Read(statusDir, configuredSuite, selected, pending, nowUtc));
            if (!_closed)
                Apply(result);
        }
        catch (Exception ex)
        {
            ListHintText.Text = "Couldn't read the status files: " + ex.Message;
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>Runs on a pool thread: every file the window needs, read once.</summary>
    private ReadResult Read(string statusDir, string configuredSuite, HeadlessCharacterStatus? selected, PendingCommand? pending, DateTime nowUtc)
    {
        try
        {
            List<HeadlessCharacterStatus> found = HeadlessStatusScanner.Scan(statusDir, nowUtc, out List<int> unreadable);

            // Command files nobody took within a minute: the character isn't reading its inbox.
            int removed = 0;
            lock (_untaken)
            {
                for (int i = _untaken.Count - 1; i >= 0; i--)
                {
                    var (path, written) = _untaken[i];
                    bool exists = File.Exists(path);
                    if (exists && nowUtc - written > UntakenLimit)
                    {
                        try { File.Delete(path); removed++; } catch (IOException) { } catch (UnauthorizedAccessException) { }
                        exists = File.Exists(path);
                    }
                    if (!exists)
                        _untaken.RemoveAt(i);
                }
            }

            // The selected character's log.
            int logFor = selected?.Id ?? 0;
            string? logPath = null;
            List<string>? lines = null;
            bool missing = false;
            if (selected != null && selected.Account.Length > 0)
            {
                if (_logForId != selected.Id || _logAccount != selected.Account)
                {
                    _logForId = selected.Id;
                    _logAccount = selected.Account;
                    _logPath = null;
                    _logStamp = null;
                    _logLookedUpUtc = DateTime.MinValue;
                }
                if (_logPath == null || !File.Exists(_logPath) || nowUtc - _logLookedUpUtc > LogLookupEvery)
                {
                    _logDirs = HeadlessLogs.CandidateSuiteDirs(selected.Account, _host!.LaunchConfigDir, configuredSuite, HeadlessLogs.DefaultSuiteDir);
                    string? path = HeadlessLogs.Find(selected.Account, _logDirs);
                    if (path != _logPath) _logStamp = null;
                    _logPath = path;
                    _logLookedUpUtc = nowUtc;
                }
                logPath = _logPath;
                if (logPath == null)
                {
                    missing = true;
                }
                else
                {
                    var info = new FileInfo(logPath);
                    var stamp = (info.Length, info.LastWriteTimeUtc);
                    if (_logStamp != stamp)
                    {
                        lines = HeadlessLogs.Tail(logPath);
                        if (lines != null) _logStamp = stamp;
                    }
                }
            }

            return new ReadResult
            {
                Found = found,
                Unreadable = unreadable,
                LogForId = logFor,
                LogPath = logPath,
                LogLines = lines,
                LogMissing = missing,
                PendingPath = pending?.Path,
                PendingFileExists = pending != null && File.Exists(pending.Path),
                RemovedUntaken = removed,
            };
        }
        catch (Exception ex)
        {
            return new ReadResult { Error = ex.Message };
        }
    }

    private void Apply(ReadResult r)
    {
        DateTime nowUtc = DateTime.UtcNow;
        if (r.Error != null)
        {
            ListHintText.Text = "Couldn't read the status files: " + r.Error;
            return;
        }

        List<HeadlessCharacterStatus> list = _roster.Update(r.Found, r.Unreadable, nowUtc);
        SyncRows(list, nowUtc);
        string folder = _host!.StatusDir();
        ListHintText.Text = list.Count == 0
            ? $"No headless character is running. Status folder: {folder}"
            : list.Count == 1 ? "1 headless character." : $"{list.Count} headless characters.";
        ToolTip.SetTip(ListHintText, "Status folder: " + folder);

        if (_selectedId is int id && !_roster.TryGet(id, out _))
        {
            _selectedId = null;
            EmptyText.Text = "That character left: no status for 30 s (logged out, or its process ended).";
        }
        // The first character is picked once, when the window opens; after that the player picks.
        if (!_autoSelected && _selectedId == null && list.Count > 0)
        {
            _autoSelected = true;
            HeadlessCharacterStatus first = list.FirstOrDefault(c => HeadlessStatusRules.Activity(c, nowUtc) is
                HeadlessActivity.Botting or HeadlessActivity.Idle or HeadlessActivity.Dead) ?? list[0];
            Select(first.Id);
        }

        if (_selectedId is int sel && _roster.TryGet(sel, out HeadlessCharacterStatus? s))
        {
            ShowDetail(s, nowUtc);
            if (r.LogForId == sel)
                ShowLog(r);
        }
        else
        {
            DetailPanel.IsVisible = false;
            EmptyPanel.IsVisible = true;
            if (list.Count == 0)
                EmptyText.Text = "Accounts set to Headless (experimental) show up here while they run.";
        }

        UpdatePending(r, nowUtc);
    }

    // ── the list ────────────────────────────────────────────────────────────

    private void SyncRows(List<HeadlessCharacterStatus> list, DateTime nowUtc)
    {
        _suppressSelection = true;
        try
        {
            var ids = list.Select(s => s.Id).ToHashSet();
            foreach (int gone in _rows.Keys.Where(k => !ids.Contains(k)).ToList())
            {
                _items.Remove(_rows[gone].Root);
                _rows.Remove(gone);
            }
            for (int i = 0; i < list.Count; i++)
            {
                HeadlessCharacterStatus s = list[i];
                if (!_rows.TryGetValue(s.Id, out Row? row))
                {
                    row = NewRow(s.Id);
                    _rows[s.Id] = row;
                }
                int at = _items.IndexOf(row.Root);
                if (at != i)
                {
                    if (at >= 0) _items.RemoveAt(at);
                    _items.Insert(i, row.Root);
                }
                HeadlessActivity a = HeadlessStatusRules.Activity(s, nowUtc);
                row.Dot.Fill = DotBrush(a);
                ToolTip.SetTip(row.Root, HeadlessStatusRules.ActivityText(a));
                row.Name.Text = s.Character.Length > 0 ? s.Character : "(character not known yet)";
                row.Server.Text = s.Server;
                row.Health.Value = s.HasVitals ? HeadlessFormat.Fraction(s.Health, s.MaxHealth) : 0;
                row.Health.Opacity = s.HasVitals ? 1 : 0.3;
            }
            if (_selectedId is int sel && _rows.TryGetValue(sel, out Row? selRow) && !ReferenceEquals(CharacterList.SelectedItem, selRow.Root))
                CharacterList.SelectedItem = selRow.Root;
        }
        finally
        {
            _suppressSelection = false;
        }
    }

    private Row NewRow(int id)
    {
        var dot = new Ellipse { Width = 10, Height = 10, Fill = LoadingBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var name = new TextBlock { FontWeight = FontWeight.SemiBold, Foreground = TextBrush, TextTrimming = TextTrimming.CharacterEllipsis };
        var server = new TextBlock { FontSize = 11, Foreground = MutedBrush, TextTrimming = TextTrimming.CharacterEllipsis };
        var hp = new ProgressBar
        {
            Minimum = 0, Maximum = 1, Height = 4, MinHeight = 4, MinWidth = 0, Margin = new Thickness(0, 4, 0, 0),
            Foreground = DeadBrush, Background = Brush("#0B1218"),
        };
        var text = new StackPanel { Spacing = 1, Children = { name, server, hp } };
        Grid.SetColumn(text, 1);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { dot, text } };
        var root = new Border { Padding = new Thickness(4, 4), Tag = id, Child = grid };
        return new Row { Root = root, Dot = dot, Name = name, Server = server, Health = hp };
    }

    private static IBrush DotBrush(HeadlessActivity a) => a switch
    {
        HeadlessActivity.Botting => BottingBrush,
        HeadlessActivity.Idle => IdleBrush,
        HeadlessActivity.Dead => DeadBrush,
        HeadlessActivity.Stale => StaleBrush,
        _ => LoadingBrush,
    };

    private void Select(int id)
    {
        _selectedId = id;
        if (_rows.TryGetValue(id, out Row? row))
        {
            _suppressSelection = true;
            try { CharacterList.SelectedItem = row.Root; }
            finally { _suppressSelection = false; }
        }
        ResetDetailViews();
    }

    private void OnSelectionChanged()
    {
        if (_suppressSelection)
            return;
        int? id = (CharacterList.SelectedItem as Control)?.Tag as int?;
        if (id == _selectedId)
            return;
        _selectedId = id;
        ResetDetailViews();
        _ = RefreshAsync();
    }

    private void ResetDetailViews()
    {
        ChatBox.Text = string.Empty;
        LogBox.Text = string.Empty;
        LogPathText.Text = "Looking for the log...";
        CommandStatusText.Text = string.Empty;
        _pending = null;
        _logForId = 0;   // the next read looks the log up again and shows it whole
    }

    // ── details ─────────────────────────────────────────────────────────────

    private void ShowDetail(HeadlessCharacterStatus s, DateTime nowUtc)
    {
        EmptyPanel.IsVisible = false;
        DetailPanel.IsVisible = true;

        TimeSpan age = HeadlessStatusRules.Age(s, nowUtc);
        HeadlessActivity a = HeadlessStatusRules.Activity(s, nowUtc);
        DetailDot.Fill = DotBrush(a);
        DetailNameText.Text = s.Character.Length > 0 ? s.Character : "(character not known yet)";
        string state = HeadlessStatusRules.ActivityText(a);
        if (a == HeadlessActivity.Stale)
            state += $" (no status for {age.TotalSeconds:0} s)";
        DetailStateText.Text = s.Server.Length > 0 ? $"{s.Server} - {state}" : state;

        if (s.HasVitals)
        {
            HealthText.Text = HeadlessFormat.Vital(s.Health, s.MaxHealth);
            StaminaText.Text = HeadlessFormat.Vital(s.Stamina, s.MaxStamina);
            ManaText.Text = HeadlessFormat.Vital(s.Mana, s.MaxMana);
        }
        else
        {
            HealthText.Text = StaminaText.Text = ManaText.Text = "-";
        }
        HealthBar.Value = s.HasVitals ? HeadlessFormat.Fraction(s.Health, s.MaxHealth) : 0;
        StaminaBar.Value = s.HasVitals ? HeadlessFormat.Fraction(s.Stamina, s.MaxStamina) : 0;
        ManaBar.Value = s.HasVitals ? HeadlessFormat.Fraction(s.Mana, s.MaxMana) : 0;

        if (!s.HasBot)
        {
            string why = s.InWorld ? "no RynthAi status (is RynthAi loaded?)" : "not in the world";
            ActionText.Text = why;
            MacroText.Text = MetaText.Text = TargetText.Text = "-";
            TargetBar.Value = 0;
        }
        else
        {
            ActionText.Text = s.BotAction.Length > 0 ? s.BotAction : "-";
            MacroText.Text = s.MacroRunning ? "on" : "off";
            MacroText.Foreground = s.MacroRunning ? BottingBrush : TextBrush;
            string meta = s.MetaName.Length > 0 && !s.MetaName.Equals("None", StringComparison.OrdinalIgnoreCase) ? s.MetaName : "none";
            MetaText.Text = s.CurrentState.Length > 0 && meta != "none" ? $"{meta} (state {s.CurrentState})" : meta;
            TargetText.Text = s.HasTarget
                ? $"{s.TargetLabel} ({s.TargetHealthPercent:0}%)"
                : "none";
            TargetBar.Value = s.HasTarget ? Math.Clamp(s.TargetHealthPercent / 100.0, 0, 1) : 0;
        }

        WhereText.Text = s.InWorld ? HeadlessCoords.Where(s) : "Not in the world";
        XpHourText.Text = HeadlessFormat.Number(s.XpPerHour);
        XpSessionText.Text = HeadlessFormat.Number(s.XpSession);
        KillsText.Text = s.HasBot
            ? (s.KillsPerHour > 0 ? $"{s.SessionKills}, {HeadlessFormat.Number(s.KillsPerHour)}/h" : s.SessionKills.ToString())
            : "-";
        DeathsText.Text = s.DeathsSession.ToString();
        VitaeText.Text = s.VitaePct > 0 ? $"{s.VitaePct:0.#}%" : "none";
        VitaeText.Foreground = s.VitaePct > 0 ? WarnBrush : TextBrush;
        BurdenText.Text = $"{s.BurdenPct:0.#}%";
        BurdenText.Foreground = s.BurdenPct > 100 ? WarnBrush : TextBrush;
        UptimeText.Text = HeadlessFormat.Duration(s.UptimeSec);
        ProcessText.Text = s.HostPid > 0 ? $"PID {s.HostPid}" : "-";

        bool fresh = a != HeadlessActivity.Stale;
        MacroButton.Content = s.MacroRunning ? "Stop macro" : "Start macro";
        MacroButton.IsEnabled = s.HasBot && s.InWorld && fresh;
        LogOutButton.IsEnabled = s.HostPid > 0;
        SendButton.IsEnabled = true;

        string chat = string.Join(Environment.NewLine, HeadlessChat.Visible(s.RecentChat).Select(l => l.Text));
        if (s.RecentChat.Count == 0)
            chat = s.HasBot ? "(no chat yet)" : "(no chat: RynthAi's status isn't there)";
        SetLiveText(ChatBox, chat);
    }

    private void ShowLog(ReadResult r)
    {
        if (r.LogMissing)
        {
            LogPathText.Text = "No log found. Looked in: " + string.Join("; ", _logDirs);
            if (LogBox.Text?.Length > 0) LogBox.Text = string.Empty;
            return;
        }
        if (r.LogPath != null)
            LogPathText.Text = r.LogPath;
        if (r.LogLines != null)
            SetLiveText(LogBox, string.Join(Environment.NewLine, r.LogLines));
    }

    /// <summary>
    /// Replaces a read-only box's text when it changed. It follows the newest line unless the player
    /// has scrolled up to read (then it stays where it is).
    /// </summary>
    private static void SetLiveText(TextBox box, string text)
    {
        if (box.Text == text)
            return;
        ScrollViewer? scroll = box.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        bool follow = string.IsNullOrEmpty(box.Text) || scroll == null ||
                      scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 4;
        box.Text = text;
        if (!follow)
            return;
        // The start of the last line: the box shows the newest line from its left edge (a long log
        // line would otherwise scroll the box sideways).
        box.CaretIndex = text.LastIndexOf('\n') + 1;
        if (scroll == null)
            return;
        // The new text's height is known only after the next layout pass.
        EventHandler? once = null;
        once = (_, _) =>
        {
            box.LayoutUpdated -= once;
            scroll.ScrollToEnd();
            scroll.Offset = new Vector(0, scroll.Offset.Y);
        };
        box.LayoutUpdated += once;
    }

    // ── commands ────────────────────────────────────────────────────────────

    private bool TrySelected(out HeadlessCharacterStatus s)
    {
        s = null!;
        return _selectedId is int id && _roster.TryGet(id, out s!);
    }

    private async Task SendCommandAsync()
    {
        if (!TrySelected(out HeadlessCharacterStatus s))
            return;
        string text = (CommandBox.Text ?? string.Empty).Trim();
        if (HeadlessCommandFile.ChatCommandProblem(text) is string problem)
        {
            ShowCommandStatus(problem, warn: true);
            return;
        }
        string? path = await WriteCommandAsync(s, HeadlessCommandFile.SendChatAction, text);
        if (path == null)
            return;
        _pending = new PendingCommand
        {
            Id = s.Id, Text = text, Path = path, SentUtc = DateTime.UtcNow, ChatAtSend = s.RecentChat,
        };
        CommandBox.Text = string.Empty;
        TimeSpan age = HeadlessStatusRules.Age(s, DateTime.UtcNow);
        ShowCommandStatus(HeadlessStatusRules.Freshness(age) == HeadlessFreshness.Fresh
            ? $"Sent: {text}"
            : $"Sent: {text}, but this character isn't answering (no status for {age.TotalSeconds:0} s).", warn: false);
    }

    private async Task ToggleMacroAsync()
    {
        if (!TrySelected(out HeadlessCharacterStatus s))
            return;
        bool turnOn = !s.MacroRunning;
        if (await WriteCommandAsync(s, HeadlessCommandFile.MacroAction, turnOn ? "on" : "off") != null)
            ShowCommandStatus(turnOn ? "Asked to start the macro." : "Asked to stop the macro.", warn: false);
    }

    /// <summary>Writes one command file for <paramref name="s"/> (off the UI thread); null when it failed.</summary>
    private async Task<string?> WriteCommandAsync(HeadlessCharacterStatus s, string action, string value)
    {
        string commandsDir = Path.Combine(_host!.StatusDir(), HeadlessCommandFile.CommandsFolderName);
        int id = s.Id;
        try
        {
            string path = await Task.Run(() => HeadlessCommandFile.Write(commandsDir, id, action, value, DateTime.UtcNow));
            lock (_untaken)
                _untaken.Add((path, DateTime.UtcNow));
            return path;
        }
        catch (Exception ex)
        {
            ShowCommandStatus($"Couldn't write the command: {ex.Message}", warn: true);
            return null;
        }
    }

    private void UpdatePending(ReadResult r, DateTime nowUtc)
    {
        if (r.RemovedUntaken > 0)
            ShowCommandStatus("The character didn't take the command within a minute (not reading its commands?): it was removed.", warn: true);

        PendingCommand? p = _pending;
        if (p == null)
            return;
        if (!p.Taken && r.PendingPath == p.Path && !r.PendingFileExists)
        {
            p.Taken = true;
            ShowCommandStatus($"Taken: {p.Text}. Waiting for a reply...", warn: false);
        }
        if (_roster.TryGet(p.Id, out HeadlessCharacterStatus? s))
        {
            HeadlessChatLine? reply = HeadlessChat.FirstReply(HeadlessChat.NewLines(p.ChatAtSend, s.RecentChat));
            if (reply is HeadlessChatLine line)
            {
                ShowCommandStatus($"{p.Text}  ->  {line.Text}", warn: false);
                _pending = null;
                return;
            }
        }
        if (nowUtc - p.SentUtc > ReplyWait)
        {
            ShowCommandStatus(p.Taken
                ? $"{p.Text}: taken, no chat reply in {ReplyWait.TotalSeconds:0} s (see the log)."
                : $"{p.Text}: not taken yet after {ReplyWait.TotalSeconds:0} s.", warn: true);
            _pending = null;
        }
    }

    private void ShowCommandStatus(string text, bool warn)
    {
        CommandStatusText.Text = text;
        CommandStatusText.Foreground = warn ? WarnBrush : MutedBrush;
    }

    // ── log out, log folder ─────────────────────────────────────────────────

    private async Task LogOutAsync()
    {
        if (!TrySelected(out HeadlessCharacterStatus s) || s.HostPid <= 0)
            return;
        string name = s.Character.Length > 0 ? s.Character : "this character";
        var others = _roster.All.Where(o => o.HostPid == s.HostPid && o.Id != s.Id).Select(o => o.Character).ToList();
        string message =
            $"Log {name} out? This asks its rynth-headless.exe (PID {s.HostPid}) to log out and exit; it is closed after " +
            $"{HeadlessLaunch.StopGrace.TotalSeconds:0} s if it doesn't.";
        if (others.Count > 0)
            message += $"\n\nThe stop request is per process, and this process also runs {others.Count} other character(s) " +
                       $"({string.Join(", ", others)}): they log out too.";
        if (_host!.AutoLaunch())
            message += "\n\nAutoLaunch is on: a checked account is started again. Uncheck it first to keep it out.";
        if (!await DecalBridgeDialog.AskAsync(this, "Log out headless character", message, "Log out", "Cancel"))
            return;
        string result;
        try { result = _host.LogOutProcess(s.HostPid); }
        catch (Exception ex) { result = "Couldn't stop it: " + ex.Message; }
        ShowCommandStatus(result, warn: false);
    }

    private void OpenLogFolder()
    {
        try
        {
            string? log = _logPath;
            if (log != null && File.Exists(log))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + log + "\"") { UseShellExecute = false });
                return;
            }
            string? dir = _logDirs.FirstOrDefault(Directory.Exists);
            if (dir != null)
            {
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { dir }, UseShellExecute = false });
                return;
            }
            ShowCommandStatus("No log folder found yet. Looked in: " + string.Join("; ", _logDirs), warn: true);
        }
        catch (Exception ex)
        {
            ShowCommandStatus("Couldn't open the folder: " + ex.Message, warn: true);
        }
    }
}
