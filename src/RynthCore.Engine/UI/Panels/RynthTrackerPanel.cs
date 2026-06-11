// ============================================================================
//  RynthCore.Engine — UI/Panels/RynthTrackerPanel.cs
//  Session-stats panel backed by the RynthTracker plugin (RynthSuite).
//
//  Bridge exports (resolved from the RynthTracker plugin DLL via GetProcAddress):
//    RynthTrackerGetSnapshotJson()   → ANSI JSON object, polled every 500ms
//    RynthTrackerReset()             → resets session counters + timer
// ============================================================================

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.UI.Panels;

internal static class RynthTrackerPanel
{
    // ── P/Invoke ──────────────────────────────────────────────────────────────

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetSnapshotJsonFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ResetFn();

    // ── Bridge state ──────────────────────────────────────────────────────────

    private static GetSnapshotJsonFn? _getSnapshotJson;
    private static ResetFn?           _reset;
    private static bool               _bindingLogged;

    // ── Persisted settings ────────────────────────────────────────────────────

    private static byte _bgAlpha = 0xCC; // ~80% default

    // ── Panel construction ────────────────────────────────────────────────────

    internal static Control Create()
    {
        LoadSettings();
        TryBind();

        // ── Value TextBlocks (updated by poll timer) ───────────────────
        // Rates first — most important at the top when panel is tiny.
        var sessionVal  = Val();
        var xpHrVal     = Val();
        var lumHrVal    = Val();
        var killHrVal   = Val();
        var xpVal       = Val();
        var lumVal      = Val();
        var killVal     = Val();
        var xpkVal      = Val();
        var deathVal    = Val();

        // ── Background brush — only this changes with the slider ───────
        SolidColorBrush MakeBg() => new(Color.FromArgb(_bgAlpha, 0x0A, 0x12, 0x1A));
        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Background  = MakeBg(),
        };

        // ── Opacity slider ─────────────────────────────────────────────
        var opacitySlider = new Slider
        {
            Minimum  = 0,
            Maximum  = 100,
            Value    = Math.Round(_bgAlpha / 2.55),
            Padding  = new Thickness(0),
            Margin   = new Thickness(4, 0, 4, 0),
            VerticalAlignment   = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        opacitySlider.ValueChanged += (_, e) =>
        {
            _bgAlpha = (byte)Math.Round(e.NewValue * 2.55);
            stack.Background = MakeBg();
            SaveSettings();
        };

        // ── Reset button ───────────────────────────────────────────────
        var resetBtn = new Button
        {
            Content         = "R",
            FontSize        = 8,
            Padding         = new Thickness(4, 1),
            Background      = new SolidColorBrush(Color.FromArgb(0xFF, 0x1A, 0x2E, 0x40)),
            Foreground      = Brushes.White,
            BorderBrush     = new SolidColorBrush(Color.FromArgb(0xFF, 0x26, 0x4C, 0x59)),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        resetBtn.Click += (_, _) => _reset?.Invoke();

        // ── Title row: [Tracker] [===slider===] [R] ────────────────────
        var titleText = new TextBlock
        {
            Text      = "Tracker",
            FontSize  = 9,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xE6, 0xB4, 0x50)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin    = new Thickness(0, 0, 4, 0),
        };
        var titleRow = new DockPanel { Margin = new Thickness(4, 2, 4, 2) };
        DockPanel.SetDock(resetBtn,  Dock.Right);
        DockPanel.SetDock(titleText, Dock.Left);
        titleRow.Children.Add(resetBtn);
        titleRow.Children.Add(titleText);
        titleRow.Children.Add(opacitySlider); // LastChildFill = slider

        // ── Stat rows ──────────────────────────────────────────────────
        stack.Children.Add(titleRow);
        stack.Children.Add(Row("Session",  sessionVal));
        stack.Children.Add(Row("XP/hr",   xpHrVal));
        stack.Children.Add(Row("Lum/hr",  lumHrVal));
        stack.Children.Add(Row("Kls/hr",  killHrVal));
        stack.Children.Add(Row("XP",      xpVal));
        stack.Children.Add(Row("Lum",     lumVal));
        stack.Children.Add(Row("Kills",   killVal));
        stack.Children.Add(Row("XP/kl",  xpkVal));
        stack.Children.Add(Row("Deaths",  deathVal));

        // ── Root: clip content when panel is shrunk below content height
        var root = new Grid { ClipToBounds = true };
        root.Children.Add(stack);

        // ── Poll timer (500ms) ─────────────────────────────────────────
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            if (_getSnapshotJson == null) { TryBind(); return; }

            IntPtr ptr = _getSnapshotJson();
            if (ptr == IntPtr.Zero) return;
            string? json = Marshal.PtrToStringAnsi(ptr);
            if (string.IsNullOrEmpty(json)) return;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;

                double ss = r.TryGetProperty("ss", out var v) ? v.GetDouble() : 0;
                long   xt = r.TryGetProperty("xt", out v) ? v.GetInt64()  : 0;
                long   xh = r.TryGetProperty("xh", out v) ? v.GetInt64()  : 0;
                long   lt = r.TryGetProperty("lt", out v) ? v.GetInt64()  : 0;
                long   lh = r.TryGetProperty("lh", out v) ? v.GetInt64()  : 0;
                int    kt = r.TryGetProperty("kt", out v) ? v.GetInt32()  : 0;
                double kh = r.TryGetProperty("kh", out v) ? v.GetDouble() : 0;
                long   xk = r.TryGetProperty("xk", out v) ? v.GetInt64()  : 0;
                int    dt = r.TryGetProperty("dt", out v) ? v.GetInt32()  : 0;

                var ic = System.Globalization.CultureInfo.InvariantCulture;
                sessionVal.Text = FormatTime(ss);
                xpHrVal.Text    = FormatNum(xh);
                lumHrVal.Text   = FormatNum(lh);
                killHrVal.Text  = kh.ToString("F1", ic);
                xpVal.Text      = FormatNum(xt);
                lumVal.Text     = FormatNum(lt);
                killVal.Text    = kt.ToString();
                xpkVal.Text     = FormatNum(xk);
                deathVal.Text   = dt.ToString();
            }
            catch { }
        };
        timer.Start();
        // Stop with the visual tree — a running DispatcherTimer roots the closed
        // view forever (one immortal poller per open/close). RadarPanel idiom;
        // must restart on attach: drag/resize fires Detached→Attached.
        root.AttachedToVisualTree   += (_, _) => { if (!timer.IsEnabled) timer.Start(); };
        root.DetachedFromVisualTree += (_, _) => timer.Stop();

        return root;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // One stat row: label (left, auto-width) + value (right-aligned, fills rest).
    private static Control Row(string labelText, TextBlock value)
    {
        var grid = new Grid { Margin = new Thickness(4, 1, 4, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

        var label = new TextBlock
        {
            Text      = labelText,
            FontSize  = 9,
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x99, 0x99, 0x99)),
            Margin    = new Thickness(0, 0, 6, 0),
        };
        Grid.SetColumn(label, 0);
        Grid.SetColumn(value, 1);
        grid.Children.Add(label);
        grid.Children.Add(value);
        return grid;
    }

    private static TextBlock Val() => new()
    {
        Text       = "—",
        FontSize   = 9,
        FontFamily = new FontFamily("Consolas,Courier New,monospace"),
        Foreground = Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Right,
    };

    private static string FormatTime(double totalSeconds)
    {
        var ts = TimeSpan.FromSeconds(totalSeconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes}:{ts.Seconds:D2}";
    }

    private static string FormatNum(long value)
    {
        if (value == 0) return "0";
        bool neg = value < 0;
        string s = (neg ? -value : value).ToString();
        var sb = new System.Text.StringBuilder(s.Length + s.Length / 3);
        int offset = s.Length % 3;
        for (int i = 0; i < s.Length; i++)
        {
            if (i > 0 && (i - offset) % 3 == 0) sb.Append(',');
            sb.Append(s[i]);
        }
        return neg ? "-" + sb : sb.ToString();
    }

    private static void TryBind()
    {
        LoadedPlugin? plugin = PluginManager.Plugins.FirstOrDefault(
            p => p.DisplayName.Contains("RynthTracker", StringComparison.OrdinalIgnoreCase));
        if (plugin == null || plugin.ModuleHandle == IntPtr.Zero) return;

        _getSnapshotJson ??= Bind<GetSnapshotJsonFn>(plugin, "RynthTrackerGetSnapshotJson");
        _reset           ??= Bind<ResetFn>(plugin,           "RynthTrackerReset");

        if (!_bindingLogged && _getSnapshotJson != null)
        {
            _bindingLogged = true;
            RynthLog.UI("RynthTrackerPanel: bound RynthTracker plugin exports.");
        }
    }

    private static T? Bind<T>(LoadedPlugin plugin, string exportName) where T : Delegate
    {
        IntPtr addr = GetProcAddress(plugin.ModuleHandle, exportName);
        return addr == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(addr);
    }

    // ── Settings persistence ──────────────────────────────────────────────────

    private static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "RynthCore", "rynthtracker_settings.json");

    private static void LoadSettings()
    {
        try
        {
            string path = SettingsPath;
            if (!File.Exists(path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("bgAlpha", out var v))
                _bgAlpha = (byte)Math.Clamp(v.GetInt32(), 0, 255);
        }
        catch { }
    }

    private static void SaveSettings()
    {
        try
        {
            string path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"{{\"bgAlpha\":{_bgAlpha}}}");
        }
        catch { }
    }
}
