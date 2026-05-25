using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace RynthCore.App.Avalonia;

/// <summary>
/// Live process-monitor for acclient.exe. Mirrors the columns and trend logic
/// from <c>scripts\Watch-AcMemory.ps1</c> and <c>scripts\Show-AcMemoryTrend.ps1</c>:
/// all-time slope, recent slope (~75 s), STABLE/GROWING/SHRINKING status with
/// text+arrow (mandatory — user is colorblind).
///
/// Lifecycle:
/// * <see cref="_processListTimer"/> polls the OS for acclient.exe PIDs every
///   2 s and refreshes the PID dropdown without disturbing the user's selection.
/// * <see cref="_sampleTimer"/> takes a sample at the user-chosen interval and
///   pushes it to <see cref="_history"/>. Pause stops sampling but keeps the
///   timer ticking so resume is instant.
/// * When the watched process disappears, <see cref="HandleProcessExit"/> freezes
///   the chart and shows the exit banner. User picks a new PID to restart.
///
/// History buffer: full session in memory. 24h @ 5s = 17,280 samples ~= 700 KB.
/// The zoom window selects what gets rendered, not what's stored.
/// </summary>
internal sealed partial class MemoryTabView : UserControl
{
    // ---- Metric definitions ----
    // Order here drives both the metric grid row layout and the legend.
    // Stroke palette is teal / amber / violet / pale-blue / pale-green; each
    // has a distinct DashPattern so colorblind users can still match line to
    // metric via the dash key in the legend.
    private static readonly MetricDef WsDef       = new("WorkingSet", "MB", Brushes.MediumTurquoise, DashPattern.Solid,    IsHandleMetric: false, IsVirtualMetric: false);
    private static readonly MetricDef PrivateDef  = new("Private",    "MB", Brushes.Khaki,           DashPattern.Dash,     IsHandleMetric: false, IsVirtualMetric: false);
    private static readonly MetricDef VirtualDef  = new("Virtual",    "MB", Brushes.LightCoral,      DashPattern.LongDash, IsHandleMetric: false, IsVirtualMetric: true);
    private static readonly MetricDef HandlesDef  = new("Handles",    "",   Brushes.LightSkyBlue,    DashPattern.Dot,      IsHandleMetric: true,  IsVirtualMetric: false);
    private static readonly MetricDef ThreadsDef  = new("Threads",    "",   Brushes.LightGreen,      DashPattern.DashDot,  IsHandleMetric: false, IsVirtualMetric: false);

    private static readonly MetricDef[] AllMetrics = new[] { WsDef, PrivateDef, VirtualDef, HandlesDef, ThreadsDef };

    // ---- State ----
    // Full history for the watched PID. Cleared when the user picks a different
    // PID (the old samples belonged to a different process).
    private readonly List<MemorySample> _history = new();
    // Refresh-rate options shown in the dropdown. Default 5 s matches the
    // PowerShell viewer.
    private static readonly int[] RefreshSecondsOptions = { 1, 2, 5, 10, 30 };
    // Recent-trend slope window: last RecentTrendSeconds of samples.
    private const int RecentTrendSeconds = 75;

    private readonly DispatcherTimer _processListTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _sampleTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    private int? _watchedPid;
    private DateTime? _watchedStartTimeLocal;
    private bool _isPaused;
    private bool _processHasExited;
    private DateTime? _processExitedAtLocal;
    private double _memoryThresholdMbPerMin = 0.5;
    private double _handlesThresholdPerHour = 1.0;
    private ZoomWindow _zoomWindow = ZoomWindow.FiveMin;
    // Per-metric "show in chart" checkbox state.
    private readonly Dictionary<string, bool> _showInChart = new(StringComparer.Ordinal);
    // Per-metric grid row controls so we can update text in place per tick.
    private readonly Dictionary<string, MetricRowControls> _metricRows = new(StringComparer.Ordinal);
    // Internal flag set when we mutate PidComboBox.SelectedItem ourselves so
    // the SelectionChanged handler doesn't treat it as a user pick.
    private bool _suppressPidSelectionEvent;
    // Last bottom-up screen of available PIDs. Lets us reconcile the dropdown
    // without flickering it on every poll when nothing changed.
    private List<int> _lastKnownPids = new();
    // List<int> of the four zoom buttons — keeps the active-style toggling
    // logic in one place.
    private List<Button> _zoomButtons = new();

    public MemoryTabView()
    {
        InitializeComponent();

        foreach (MetricDef m in AllMetrics)
            _showInChart[m.Name] = true; // Default: all visible.

        BuildRefreshRateOptions();
        BuildMetricRows();
        BuildLegend();
        WireEvents();

        // Initial state: process-list poll runs immediately, sample timer is
        // armed but does nothing until a PID is chosen.
        RefreshProcessList();
        _processListTimer.Tick += (_, _) => RefreshProcessList();
        _processListTimer.Start();
        _sampleTimer.Tick += (_, _) => OnSampleTimerTick();
        _sampleTimer.Start();

        SetZoomActive(_zoomWindow);
        UpdateMetricGrid();
        UpdateChart();
    }

    // -------------------------------------------------------------------
    //  Construction helpers
    // -------------------------------------------------------------------

    private void BuildRefreshRateOptions()
    {
        foreach (int s in RefreshSecondsOptions)
            RefreshRateComboBox.Items.Add(new ComboBoxItem { Content = $"{s} s", Tag = s });
        // Default to 5 s (index 2). Matches the PowerShell viewer default.
        RefreshRateComboBox.SelectedIndex = 2;

        MemoryThresholdBox.Value = (decimal)_memoryThresholdMbPerMin;
        HandlesThresholdBox.Value = (decimal)_handlesThresholdPerHour;
    }

    private void BuildMetricRows()
    {
        // Grid header is in AXAML at row 0. Append a row per metric here so we
        // can stash references to the per-cell TextBlocks for live updates.
        // Avalonia 11.2.3 Grid lacks ColumnSpacing — we put right/bottom
        // margins on each child to make rows readable.
        var cellMargin = new global::Avalonia.Thickness(0, 0, 8, 4);
        var rightAlignMargin = new global::Avalonia.Thickness(0, 0, 0, 4);

        for (int i = 0; i < AllMetrics.Length; i++)
        {
            MetricDef def = AllMetrics[i];
            int row = i + 1;

            MetricGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var nameCell = new TextBlock { Text = def.Name, Classes = { "metricCell" }, FontWeight = FontWeight.SemiBold, Margin = cellMargin };
            var curCell  = new TextBlock { Text = "—", Classes = { "metricCell" }, Margin = cellMargin };
            var delCell  = new TextBlock { Text = "—", Classes = { "metricCell" }, Margin = cellMargin };
            var amCell   = new TextBlock { Text = "—", Classes = { "metricCell" }, Margin = cellMargin };
            var ahCell   = new TextBlock { Text = "—", Classes = { "metricCell" }, Margin = cellMargin };
            var recCell  = new TextBlock { Text = "—", Classes = { "metricCell" }, Margin = cellMargin };
            var statCell = new TextBlock { Text = "—", Classes = { "metricCell" }, FontWeight = FontWeight.SemiBold, Margin = cellMargin };
            var showBox  = new CheckBox  { IsChecked = _showInChart[def.Name], MinWidth = 0, Margin = rightAlignMargin };
            showBox.IsCheckedChanged += (_, _) =>
            {
                _showInChart[def.Name] = showBox.IsChecked == true;
                UpdateChart();
            };

            SetGrid(nameCell, row, 0);
            SetGrid(curCell,  row, 1);
            SetGrid(delCell,  row, 2);
            SetGrid(amCell,   row, 3);
            SetGrid(ahCell,   row, 4);
            SetGrid(recCell,  row, 5);
            SetGrid(statCell, row, 6);
            SetGrid(showBox,  row, 7);

            MetricGrid.Children.Add(nameCell);
            MetricGrid.Children.Add(curCell);
            MetricGrid.Children.Add(delCell);
            MetricGrid.Children.Add(amCell);
            MetricGrid.Children.Add(ahCell);
            MetricGrid.Children.Add(recCell);
            MetricGrid.Children.Add(statCell);
            MetricGrid.Children.Add(showBox);

            _metricRows[def.Name] = new MetricRowControls(curCell, delCell, amCell, ahCell, recCell, statCell, showBox);
        }
    }

    private static void SetGrid(Control c, int row, int col)
    {
        Grid.SetRow(c, row);
        Grid.SetColumn(c, col);
    }

    private void BuildLegend()
    {
        LegendPanel.Children.Clear();
        foreach (MetricDef def in AllMetrics)
        {
            // Each legend item: a short line drawn with the metric's actual
            // dash style + the metric name. Colorblind users key off the dash
            // pattern, sighted users get the colour too.
            var sample = new Line
            {
                StartPoint = new global::Avalonia.Point(0, 5),
                EndPoint = new global::Avalonia.Point(28, 5),
                Stroke = def.Stroke,
                StrokeThickness = 2,
                Width = 28,
                Height = 10,
                VerticalAlignment = VerticalAlignment.Center
            };
            sample.StrokeDashArray = MakeDashArray(def.Dash);

            var label = new TextBlock
            {
                Text = def.Name + (string.IsNullOrEmpty(def.Unit) ? "" : $" ({def.Unit})"),
                Foreground = Brush.Parse("#EAF0F4"),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new global::Avalonia.Thickness(6, 0, 0, 0)
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(sample);
            row.Children.Add(label);
            LegendPanel.Children.Add(row);
        }
    }

    private static global::Avalonia.Collections.AvaloniaList<double> MakeDashArray(DashPattern p) => p switch
    {
        DashPattern.Solid    => new global::Avalonia.Collections.AvaloniaList<double>(),
        DashPattern.Dash     => new global::Avalonia.Collections.AvaloniaList<double> { 2, 2 },
        DashPattern.Dot      => new global::Avalonia.Collections.AvaloniaList<double> { 0.5, 1.5 },
        DashPattern.DashDot  => new global::Avalonia.Collections.AvaloniaList<double> { 2, 2, 0.5, 2 },
        DashPattern.LongDash => new global::Avalonia.Collections.AvaloniaList<double> { 4, 2 },
        _                    => new global::Avalonia.Collections.AvaloniaList<double>()
    };

    private void WireEvents()
    {
        PidComboBox.SelectionChanged += (_, _) => OnPidSelectionChanged();
        RefreshRateComboBox.SelectionChanged += (_, _) => OnRefreshRateChanged();
        PauseToggleButton.IsCheckedChanged += (_, _) => OnPauseToggleChanged();
        SnapshotNowButton.Click += (_, _) => TakeSnapshot(force: true);
        ExportCsvButton.Click += async (_, _) => await ExportCsvAsync();

        Zoom5MinButton.Click  += (_, _) => SetZoomActive(ZoomWindow.FiveMin);
        Zoom30MinButton.Click += (_, _) => SetZoomActive(ZoomWindow.ThirtyMin);
        Zoom1HrButton.Click   += (_, _) => SetZoomActive(ZoomWindow.OneHour);
        ZoomAllButton.Click   += (_, _) => SetZoomActive(ZoomWindow.All);
        _zoomButtons = new List<Button> { Zoom5MinButton, Zoom30MinButton, Zoom1HrButton, ZoomAllButton };

        MemoryThresholdBox.ValueChanged += (_, _) =>
        {
            if (MemoryThresholdBox.Value is decimal d) _memoryThresholdMbPerMin = (double)d;
            UpdateMetricGrid();
        };
        HandlesThresholdBox.ValueChanged += (_, _) =>
        {
            if (HandlesThresholdBox.Value is decimal d) _handlesThresholdPerHour = (double)d;
            UpdateMetricGrid();
        };
    }

    // -------------------------------------------------------------------
    //  Process discovery
    // -------------------------------------------------------------------

    private void RefreshProcessList()
    {
        List<int> pids = new();
        try
        {
            foreach (Process p in Process.GetProcessesByName("acclient"))
            {
                pids.Add(p.Id);
                p.Dispose();
            }
        }
        catch
        {
            // Process enumeration occasionally throws on transient access
            // errors — skip this tick rather than poison the dropdown.
            return;
        }
        pids.Sort();

        // Skip the rebuild if the live PID list hasn't changed; rebuilding
        // would flash the dropdown and reset the user's selection.
        if (pids.SequenceEqual(_lastKnownPids))
            return;
        _lastKnownPids = pids;

        // Preserve the user's current selection if that PID is still live.
        int? prior = _watchedPid;
        _suppressPidSelectionEvent = true;
        try
        {
            PidComboBox.Items.Clear();
            foreach (int pid in pids)
            {
                string label = BuildPidLabel(pid);
                PidComboBox.Items.Add(new ComboBoxItem { Content = label, Tag = pid });
            }

            if (pids.Count == 0)
            {
                PidComboBox.SelectedIndex = -1;
                // The watched PID may have just vanished — let the sample
                // timer's "process gone" path handle the exit banner.
            }
            else if (prior.HasValue && pids.Contains(prior.Value))
            {
                for (int i = 0; i < PidComboBox.Items.Count; i++)
                {
                    if (PidComboBox.Items[i] is ComboBoxItem ci && ci.Tag is int p && p == prior.Value)
                    {
                        PidComboBox.SelectedIndex = i;
                        break;
                    }
                }
            }
            else
            {
                // Auto-pick the first PID when nothing is watched yet.
                PidComboBox.SelectedIndex = 0;
                StartWatching((int)((ComboBoxItem)PidComboBox.Items[0]!).Tag!);
            }
        }
        finally
        {
            _suppressPidSelectionEvent = false;
        }
    }

    private static string BuildPidLabel(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            DateTime st = p.StartTime;
            TimeSpan up = DateTime.Now - st;
            return $"PID {pid}   (started {st:HH:mm:ss}, up {FormatHms(up)})";
        }
        catch
        {
            return $"PID {pid}";
        }
    }

    private void OnPidSelectionChanged()
    {
        if (_suppressPidSelectionEvent)
            return;
        if (PidComboBox.SelectedItem is not ComboBoxItem ci || ci.Tag is not int pid)
            return;
        if (_watchedPid == pid && !_processHasExited)
            return; // No-op — already watching this PID.

        StartWatching(pid);
    }

    private void StartWatching(int pid)
    {
        _watchedPid = pid;
        _history.Clear();
        _processHasExited = false;
        _processExitedAtLocal = null;
        ExitBanner.IsVisible = false;
        WarningBanner.IsVisible = false;

        try
        {
            using Process p = Process.GetProcessById(pid);
            _watchedStartTimeLocal = p.StartTime;
        }
        catch
        {
            _watchedStartTimeLocal = null;
        }

        // Take an immediate first sample so the user gets data instantly.
        TakeSnapshot(force: true);
        UpdateProcessInfoText();
        UpdateStatusBanner();
    }

    // -------------------------------------------------------------------
    //  Sampling
    // -------------------------------------------------------------------

    private void OnRefreshRateChanged()
    {
        int seconds = 5;
        if (RefreshRateComboBox.SelectedItem is ComboBoxItem ci && ci.Tag is int s)
            seconds = s;
        _sampleTimer.Interval = TimeSpan.FromSeconds(seconds);
        UpdateStatusBanner();
    }

    private void OnPauseToggleChanged()
    {
        _isPaused = PauseToggleButton.IsChecked == true;
        PauseToggleButton.Content = _isPaused ? "Resume" : "Pause";
        UpdateStatusBanner();
    }

    private void OnSampleTimerTick()
    {
        if (_isPaused || _watchedPid == null || _processHasExited)
            return;
        TakeSnapshot(force: false);
    }

    /// <summary>Samples the watched PID once. <paramref name="force"/> bypasses
    /// the pause/exit gates so the Snapshot Now button always grabs one.</summary>
    private void TakeSnapshot(bool force)
    {
        if (_watchedPid == null)
            return;
        if (!force && (_isPaused || _processHasExited))
            return;

        int pid = _watchedPid.Value;
        try
        {
            using Process p = Process.GetProcessById(pid);
            // Refresh() forces a live read; without it cached values from the
            // Process construction are reused.
            p.Refresh();
            var sample = new MemorySample
            {
                TimeUtc = DateTime.UtcNow,
                WorkingSetMb = p.WorkingSet64 / 1024.0 / 1024.0,
                PrivateMb = p.PrivateMemorySize64 / 1024.0 / 1024.0,
                VirtualMb = p.VirtualMemorySize64 / 1024.0 / 1024.0,
                Handles = p.HandleCount,
                Threads = p.Threads.Count
            };
            _history.Add(sample);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // PID gone or no longer accessible — treat as exit.
            HandleProcessExit();
            return;
        }
        catch
        {
            // Transient enumeration failure — skip this tick.
            return;
        }

        UpdateProcessInfoText();
        UpdateMetricGrid();
        UpdateChart();
        UpdateWarningBanner();
        UpdateStatusBanner();
    }

    private void HandleProcessExit()
    {
        if (_processHasExited)
            return;
        _processHasExited = true;
        _processExitedAtLocal = DateTime.Now;

        ExitBanner.IsVisible = true;
        StringBuilder sb = new();
        sb.Append("Process PID ").Append(_watchedPid).Append(" exited at ")
          .Append(_processExitedAtLocal?.ToString("HH:mm:ss") ?? "?");
        if (_history.Count > 0)
        {
            MemorySample first = _history[0];
            MemorySample last = _history[^1];
            double minutes = (last.TimeUtc - first.TimeUtc).TotalMinutes;
            sb.AppendLine();
            sb.Append("Final summary: ").Append(_history.Count).Append(" samples over ")
              .Append(minutes.ToString("F1", CultureInfo.InvariantCulture)).Append(" min. ")
              .Append("WS ").Append(last.WorkingSetMb.ToString("F1", CultureInfo.InvariantCulture)).Append(" MB (")
              .Append(SignedNumber(last.WorkingSetMb - first.WorkingSetMb, 1)).Append("),  ")
              .Append("Private ").Append(last.PrivateMb.ToString("F1", CultureInfo.InvariantCulture)).Append(" MB (")
              .Append(SignedNumber(last.PrivateMb - first.PrivateMb, 1)).Append("),  ")
              .Append("Virtual ").Append(last.VirtualMb.ToString("F1", CultureInfo.InvariantCulture)).Append(" MB (")
              .Append(SignedNumber(last.VirtualMb - first.VirtualMb, 1)).Append(").");
        }
        sb.AppendLine();
        sb.Append("Pick a different PID in the dropdown to resume monitoring.");
        ExitBannerText.Text = sb.ToString();
        UpdateStatusBanner();
    }

    // -------------------------------------------------------------------
    //  UI updates
    // -------------------------------------------------------------------

    private void UpdateProcessInfoText()
    {
        if (_watchedPid == null)
        {
            ProcessInfoText.Text = "No process selected.";
            return;
        }
        StringBuilder sb = new();
        sb.Append("PID ").Append(_watchedPid);
        if (_watchedStartTimeLocal is DateTime st)
        {
            sb.Append("   started ").Append(st.ToString("yyyy-MM-dd HH:mm:ss"));
            TimeSpan up = (_processExitedAtLocal ?? DateTime.Now) - st;
            if (up.TotalSeconds > 0)
                sb.Append("   up ").Append(FormatHms(up));
        }
        if (_processHasExited && _processExitedAtLocal is DateTime ex)
            sb.Append("   EXITED ").Append(ex.ToString("HH:mm:ss"));
        SampleCountText.Text = $"{_history.Count} samples";
        ProcessInfoText.Text = sb.ToString();
    }

    private void UpdateStatusBanner()
    {
        if (_watchedPid == null)
        {
            StatusBannerText.Text = "No process selected.";
            return;
        }
        if (_processHasExited)
        {
            StatusBannerText.Text = "Process exited — polling stopped. Pick a new PID to resume.";
            return;
        }
        int seconds = (int)_sampleTimer.Interval.TotalSeconds;
        StatusBannerText.Text = _isPaused
            ? $"Paused. Polling resumes when you click Resume. Interval {seconds} s."
            : $"Live polling every {seconds} s.";
    }

    private void UpdateMetricGrid()
    {
        if (_history.Count == 0)
        {
            foreach (MetricDef def in AllMetrics)
            {
                MetricRowControls r = _metricRows[def.Name];
                r.Current.Text = "—";
                r.Delta.Text = "—";
                r.AllMin.Text = "—";
                r.AllHour.Text = "—";
                r.Recent.Text = "—";
                r.Status.Text = "—";
            }
            return;
        }

        MemorySample first = _history[0];
        MemorySample last = _history[^1];

        foreach (MetricDef def in AllMetrics)
        {
            MetricRowControls r = _metricRows[def.Name];
            double current = GetMetricValue(last, def);
            double startVal = GetMetricValue(first, def);
            double delta = current - startVal;

            (double allMin, double allHour) = ComputeAllTimeRates(def);
            double recentMin = ComputeRecentRatePerMinute(def);

            r.Current.Text = FormatCurrent(def, current);
            r.Delta.Text = SignedNumber(delta, def.IsHandleMetric || def.Name == "Threads" ? 0 : 1)
                + (string.IsNullOrEmpty(def.Unit) ? "" : " " + def.Unit);
            r.AllMin.Text = FormatRate(def, allMin, perHour: false);
            r.AllHour.Text = FormatRate(def, allHour, perHour: true);
            r.Recent.Text = FormatRate(def, recentMin, perHour: false);

            // Status uses the per-metric threshold. Memory metrics use the
            // MB/min boundary; handles use a per-hour boundary (converted to
            // per-min for the comparator). Threads are informational — no
            // status, ratio noise dominates.
            if (def.Name == "Threads")
            {
                r.Status.Text = "(info)";
            }
            else
            {
                double threshold = def.IsHandleMetric
                    ? (_handlesThresholdPerHour / 60.0)
                    : _memoryThresholdMbPerMin;
                (string label, string arrow) = ClassifyTrend(recentMin, threshold);
                r.Status.Text = $"{label} {arrow}";
            }
        }
    }

    private static double GetMetricValue(MemorySample s, MetricDef def) => def.Name switch
    {
        "WorkingSet" => s.WorkingSetMb,
        "Private"    => s.PrivateMb,
        "Virtual"    => s.VirtualMb,
        "Handles"    => s.Handles,
        "Threads"    => s.Threads,
        _            => 0.0
    };

    private (double PerMin, double PerHour) ComputeAllTimeRates(MetricDef def)
    {
        if (_history.Count < 2) return (0.0, 0.0);
        MemorySample first = _history[0];
        MemorySample last = _history[^1];
        double minutes = (last.TimeUtc - first.TimeUtc).TotalMinutes;
        if (minutes <= 0.01) return (0.0, 0.0);
        double delta = GetMetricValue(last, def) - GetMetricValue(first, def);
        double perMin = delta / minutes;
        return (perMin, perMin * 60.0);
    }

    private double ComputeRecentRatePerMinute(MetricDef def)
    {
        if (_history.Count < 2) return 0.0;
        MemorySample last = _history[^1];
        // Find the first sample within RecentTrendSeconds of "now". Scan
        // backwards from the end so we exit early as soon as we cross the
        // boundary.
        DateTime cutoff = last.TimeUtc.AddSeconds(-RecentTrendSeconds);
        MemorySample? olderRef = null;
        for (int i = _history.Count - 1; i >= 0; i--)
        {
            if (_history[i].TimeUtc <= cutoff)
            {
                olderRef = _history[i];
                break;
            }
            olderRef = _history[i];
        }
        if (olderRef is not MemorySample older || older.TimeUtc >= last.TimeUtc)
            return 0.0;
        double minutes = (last.TimeUtc - older.TimeUtc).TotalMinutes;
        if (minutes <= 0.01) return 0.0;
        double delta = GetMetricValue(last, def) - GetMetricValue(older, def);
        return delta / minutes;
    }

    private static (string Label, string Arrow) ClassifyTrend(double rate, double thresholdPerMin)
    {
        if (Math.Abs(rate) < thresholdPerMin) return ("STABLE", "->");
        return rate > 0 ? ("GROWING", "^") : ("SHRINKING", "v");
    }

    private void UpdateChart()
    {
        if (_history.Count == 0)
        {
            Sparkline.UpdateData(Array.Empty<MemorySeries>(), DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow);
            return;
        }

        DateTime endUtc = _history[^1].TimeUtc;
        DateTime startUtc = _zoomWindow switch
        {
            ZoomWindow.FiveMin   => endUtc.AddMinutes(-5),
            ZoomWindow.ThirtyMin => endUtc.AddMinutes(-30),
            ZoomWindow.OneHour   => endUtc.AddHours(-1),
            ZoomWindow.All       => _history[0].TimeUtc,
            _                    => endUtc.AddMinutes(-5)
        };
        if (startUtc > _history[0].TimeUtc && _zoomWindow != ZoomWindow.All)
        {
            // Keep startUtc inside the window even if history doesn't fill it
            // yet (so the chart's X-axis labels match what we render).
        }
        // Ensure we always show at least a small span so the chart isn't
        // collapsed on first sample.
        if ((endUtc - startUtc).TotalSeconds < 5)
            startUtc = endUtc.AddSeconds(-5);

        var seriesList = new List<MemorySeries>();
        foreach (MetricDef def in AllMetrics)
        {
            if (!_showInChart.TryGetValue(def.Name, out bool show) || !show)
                continue;

            // Build the visible-window point list. We slice the in-memory
            // history here; the chart control just renders.
            var pts = new List<MemoryPoint>(_history.Count);
            foreach (MemorySample s in _history)
            {
                if (s.TimeUtc < startUtc || s.TimeUtc > endUtc) continue;
                pts.Add(new MemoryPoint(s.TimeUtc, GetMetricValue(s, def)));
            }
            seriesList.Add(new MemorySeries
            {
                Name = def.Name,
                Unit = def.Unit,
                Stroke = def.Stroke,
                Dash = def.Dash,
                IncludeCeiling = def.IsVirtualMetric,
                Points = pts
            });
        }

        Sparkline.UpdateData(seriesList, startUtc, endUtc);
    }

    private void UpdateWarningBanner()
    {
        if (_history.Count == 0)
        {
            WarningBanner.IsVisible = false;
            return;
        }
        double vm = _history[^1].VirtualMb;
        double ceiling = MemorySparkline.X86CeilingMb;
        if (vm >= ceiling * 0.93)
        {
            WarningBanner.IsVisible = true;
            WarningBannerText.FontSize = 14;
            WarningBannerText.Text = $"!! CRITICAL: Virtual memory {vm:F0} MB — within {ceiling - vm:F0} MB of the x86 LAA 4 GB ceiling. Crash imminent. Save state / disconnect now.";
        }
        else if (vm >= ceiling * 0.88)
        {
            WarningBanner.IsVisible = true;
            WarningBannerText.FontSize = 12.5;
            WarningBannerText.Text = $"WARNING: Virtual memory {vm:F0} MB — approaching the x86 LAA 4 GB ceiling ({ceiling - vm:F0} MB headroom).";
        }
        else
        {
            WarningBanner.IsVisible = false;
        }
    }

    private void SetZoomActive(ZoomWindow w)
    {
        _zoomWindow = w;
        // Refresh button visuals: only the active one wears the .active class.
        Button? active = w switch
        {
            ZoomWindow.FiveMin   => Zoom5MinButton,
            ZoomWindow.ThirtyMin => Zoom30MinButton,
            ZoomWindow.OneHour   => Zoom1HrButton,
            ZoomWindow.All       => ZoomAllButton,
            _                    => Zoom5MinButton
        };
        foreach (Button b in _zoomButtons)
        {
            if (b == active)
            {
                if (!b.Classes.Contains("active")) b.Classes.Add("active");
            }
            else
            {
                b.Classes.Remove("active");
            }
        }
        UpdateChart();
    }

    // -------------------------------------------------------------------
    //  CSV export
    // -------------------------------------------------------------------

    private async Task ExportCsvAsync()
    {
        if (_history.Count == 0)
            return;

        IStorageProvider? sp = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (sp == null)
            return;

        string defaultName = $"acclient-memory-pid{_watchedPid?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}-{DateTime.Now:yyyyMMdd-HHmmss}.csv";

        var opts = new FilePickerSaveOptions
        {
            Title = "Export memory history",
            SuggestedFileName = defaultName,
            DefaultExtension = "csv",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } }
            }
        };

        IStorageFile? file = await sp.SaveFilePickerAsync(opts);
        if (file == null)
            return;

        // Column header matches Watch-AcMemory.ps1 exactly so existing parsers
        // (Excel sheets, scripts) keep working.
        var sb = new StringBuilder();
        sb.AppendLine("Timestamp,PID,UptimeMinutes,WorkingSetMB,PrivateMB,VirtualMB,HandleCount,ThreadCount");

        DateTime startUtc = _watchedStartTimeLocal?.ToUniversalTime() ?? _history[0].TimeUtc;
        int pid = _watchedPid ?? 0;

        foreach (MemorySample s in _history)
        {
            double uptimeMin = Math.Round((s.TimeUtc - startUtc).TotalMinutes, 2);
            sb.Append(s.TimeUtc.ToLocalTime().ToString("s", CultureInfo.InvariantCulture)).Append(',')
              .Append(pid).Append(',')
              .Append(uptimeMin.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(((int)s.WorkingSetMb).ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(((int)s.PrivateMb).ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(((int)s.VirtualMb).ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(s.Handles).Append(',')
              .Append(s.Threads)
              .AppendLine();
        }

        await using Stream stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream, Encoding.UTF8);
        await writer.WriteAsync(sb.ToString());
    }

    // -------------------------------------------------------------------
    //  Formatting helpers
    // -------------------------------------------------------------------

    private static string FormatCurrent(MetricDef def, double value)
    {
        if (def.IsHandleMetric || def.Name == "Threads")
            return ((int)value).ToString(CultureInfo.InvariantCulture);
        return value.ToString("F1", CultureInfo.InvariantCulture) + " " + def.Unit;
    }

    private static string FormatRate(MetricDef def, double rate, bool perHour)
    {
        string unit;
        if (def.IsHandleMetric || def.Name == "Threads")
            unit = perHour ? "/hr" : "/min";
        else
            unit = perHour ? " " + def.Unit + "/hr" : " " + def.Unit + "/min";

        int decimals = (def.IsHandleMetric || def.Name == "Threads")
            ? (perHour ? 0 : 1)
            : 2;
        return SignedNumber(rate, decimals) + unit;
    }

    /// <summary>Always emits an explicit sign (+/-/space) so STABLE/GROWING
    /// readout is visually obvious even without colour cues.</summary>
    private static string SignedNumber(double v, int decimals)
    {
        string mag = Math.Abs(v).ToString("F" + decimals, CultureInfo.InvariantCulture);
        if (v > 0) return "+" + mag;
        if (v < 0) return "-" + mag;
        return " " + mag;
    }

    private static string FormatHms(TimeSpan ts)
        => $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";

    // -------------------------------------------------------------------
    //  Types
    // -------------------------------------------------------------------

    private struct MemorySample
    {
        public DateTime TimeUtc;
        public double WorkingSetMb;
        public double PrivateMb;
        public double VirtualMb;
        public int Handles;
        public int Threads;
    }

    /// <summary>Per-metric definition: name, unit, stroke, dash pattern, and a
    /// couple of role bits used for status thresholds + ceiling overlay.
    /// IsHandleMetric flips the status threshold to use the handles-per-hour
    /// boundary; IsVirtualMetric flips on the x86 LAA 4 GB ceiling overlay.</summary>
    private sealed record MetricDef(
        string Name,
        string Unit,
        IBrush Stroke,
        DashPattern Dash,
        bool IsHandleMetric,
        bool IsVirtualMetric);

    private sealed record MetricRowControls(
        TextBlock Current,
        TextBlock Delta,
        TextBlock AllMin,
        TextBlock AllHour,
        TextBlock Recent,
        TextBlock Status,
        CheckBox Show);

    private enum ZoomWindow
    {
        FiveMin,
        ThirtyMin,
        OneHour,
        All
    }
}
