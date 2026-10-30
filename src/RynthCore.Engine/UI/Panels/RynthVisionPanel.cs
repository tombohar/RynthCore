// ============================================================================
//  RynthCore.Engine — UI/Panels/RynthVisionPanel.cs
//  Avalonia settings panel for the RynthVision plugin (RynthSuite): reads the
//  settings JSON to populate controls and pushes changes back.
//
//  Data goes through UiDataHub (UI/Data/VisionData.cs): UiSources.Vision polls
//  RynthVisionGetSettingsJson on the pump thread; VisionCommands saves
//  (RynthVisionSetSettings) and runs RynthVisionInspectTerrain there. The
//  ImGui face (ImGui/Panels/VisionFace.cs) reads the same snapshot. Each
//  change pushes only its own field, like the ImGui face.
// ============================================================================

using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RynthCore.Engine.ImGuiBackend;
using RynthCore.Engine.Plugins;
using RynthCore.Engine.UI.Data;

namespace RynthCore.Engine.UI.Panels;

internal static class RynthVisionPanel
{
    // ── Controls (Avalonia UI thread only) ─────────────────────────────────────

    private static CheckBox? _cbRadar, _cbSlopes, _cbWater, _cbWaterAnyCorner, _cbWaterImpassableOnly;
    private static Slider? _slRange, _slThick, _slHeight, _slSlopeR, _slWaterR, _slSlopeFloor, _slSlopeBias;
    private static TextBox? _tbSlope, _tbWater, _tbRadar, _tbWaterTypes;
    private static Border? _swSlope, _swWater, _swRadar;

    private static uint _slopeColor = 0x60FF2020;
    private static uint _waterColor = 0x600060FF;
    private static uint _radarColor = 0x80FFD000;

    private static bool _suppressPush;
    private static bool _populated;
    private static int _createCounter;

    // The panel's one retry timer (see Create). It also re-runs Populate after
    // a water-types save: the plugin may normalise the list (empty resets to
    // the defaults), so the box is refreshed from the first snapshot newer
    // than _populateAfterVersion.
    private static DispatcherTimer? _populateTimer;
    private static int _populateRetries;
    private static long _populateAfterVersion = -1;

    // ── Panel construction ──────────────────────────────────────────────────────

    internal static Control Create()
    {
        // The static `_populated` flag survives between panel closes/reopens, so
        // without resetting we'd skip Populate() on every reopen — the newly
        // constructed controls would sit at their Avalonia defaults (slider=min,
        // checkbox=null, textbox=empty), and the next user edit would Push()
        // those defaults to disk, blanking the saved settings.
        //
        // Suppress push until the first successful Populate. Without this, any
        // change-event fired during/before Populate (Avalonia raises
        // IsCheckedChanged when IsChecked transitions null→true/false, even when
        // we set it programmatically) writes Avalonia defaults over the JSON.
        // Populate flips _suppressPush back to false in its finally block.
        _populated = false;
        _suppressPush = true;
        _populateAfterVersion = -1;

        int createN = System.Threading.Interlocked.Increment(ref _createCounter);
        RynthLog.UI($"RynthVisionPanel.Create #{createN}: starting (_populated reset, _suppressPush=true)");

        var root = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(8), Spacing = 3 };

        root.Children.Add(Header("Overlays"));
        _cbRadar  = AddCheck(root, "Radar range ring");
        _cbSlopes = AddCheck(root, "Unclimbable slopes");
        _cbWater  = AddCheck(root, "Impassable water");
        _cbRadar.IsCheckedChanged  += (_, _) => Push(VisionKeys.Radar);
        _cbSlopes.IsCheckedChanged += (_, _) => Push(VisionKeys.Slopes);
        _cbWater.IsCheckedChanged  += (_, _) => Push(VisionKeys.Water);

        root.Children.Add(Header("Colors (hex AARRGGBB)"));
        (_tbSlope, _swSlope) = AddColorRow(root, "Slope", VisionKeys.SlopeColor, () => _slopeColor, c => _slopeColor = c);
        (_tbWater, _swWater) = AddColorRow(root, "Water", VisionKeys.WaterColor, () => _waterColor, c => _waterColor = c);
        (_tbRadar, _swRadar) = AddColorRow(root, "Radar", VisionKeys.RadarColor, () => _radarColor, c => _radarColor = c);

        root.Children.Add(Header("Tuning"));
        (_slRange,  _) = AddSlider(root, "Radar range",    VisionKeys.RadarRange, 24,  300, 0, false);
        (_slThick,  _) = AddSlider(root, "Ring thickness", VisionKeys.RingThick,  0.5, 6,   1, false);
        (_slHeight, _) = AddSlider(root, "Ring height (m)",VisionKeys.RingHeight, 0.5, 30,  1, false);
        (_slSlopeR, _)    = AddSlider(root, "Slope radius (cells)", VisionKeys.SlopeRadius, 1, 24,   0, true);
        (_slSlopeFloor, _)= AddSlider(root, "Slope floor Z",        VisionKeys.SlopeFloorZ, 0.30, 0.95, 3, false);
        (_slSlopeBias, _) = AddSlider(root, "Slope height bias (m)",VisionKeys.SlopeBias,   0.00, 1.00, 2, false);
        (_slWaterR, _)    = AddSlider(root, "Water radius (cells)", VisionKeys.WaterRadius, 1, 24,   0, true);

        root.Children.Add(Header("Water terrain types"));
        _cbWaterAnyCorner = AddCheck(root, "Highlight cell if ANY corner is water (else all four)");
        _cbWaterAnyCorner.IsCheckedChanged += (_, _) => Push(VisionKeys.WaterAnyCorner);
        _cbWaterImpassableOnly = AddCheck(root, "Only paint water cells with an unwalkable triangle (real impassable)");
        _cbWaterImpassableOnly.IsCheckedChanged += (_, _) => Push(VisionKeys.WaterImpassableOnly);
        _tbWaterTypes = new TextBox { Watermark = "18,19,20", FontSize = 11, MinWidth = 120 };
        _tbWaterTypes.GotFocus  += (_, _) => Win32Backend.AvaloniaTextInputActive = true;
        _tbWaterTypes.LostFocus += (_, _) => { Win32Backend.AvaloniaTextInputActive = false; Push(VisionKeys.WaterTypes); };
        var applyBtn = new Button { Content = "Apply", FontSize = 11, Margin = new Thickness(4, 0, 0, 0) };
        applyBtn.Click += (_, _) => Push(VisionKeys.WaterTypes);
        var wtRow = new StackPanel { Orientation = Orientation.Horizontal };
        wtRow.Children.Add(_tbWaterTypes);
        wtRow.Children.Add(applyBtn);
        root.Children.Add(wtRow);

        var inspectBtn = new Button { Content = "Log terrain types here", FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
        inspectBtn.Click += (_, _) => VisionCommands.Inspect();
        root.Children.Add(inspectBtn);

        // Bind + populate once the plugin DLL is loaded AND the plugin has
        // finished initializing its settings (Runtime.Plugin is non-null on
        // the plugin side). Until then, GetSettingsJson() returns "{}" and
        // populating from that would blank every control — and the first
        // user edit would Push() those blanks over the real saved JSON.
        // Retries every 500 ms until Populate succeeds (returns true) or the
        // panel is closed. Caps at 60 retries (~30 s) so we don't spin forever
        // if the plugin never initializes.
        _populateRetries = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _populateTimer?.Stop();
        _populateTimer = timer;
        timer.Tick += (_, _) =>
        {
            if (_populated) { timer.Stop(); return; }
            if (++_populateRetries > 60) { timer.Stop(); RynthLog.UI("RynthVisionPanel: gave up populating after 30s"); return; }
            if (Populate())
            {
                _populated = true;
                timer.Stop();
            }
        };
        timer.Start();

        var scroll = new ScrollViewer { Content = root };
        scroll.AttachedToVisualTree += (_, _) =>
        {
            UiSources.Vision.Subscribe();
            UiSources.Vision.RequestRefresh();
            if (!_populated && !timer.IsEnabled) { _populateRetries = 0; timer.Start(); }
        };
        scroll.DetachedFromVisualTree += (_, _) =>
        {
            timer.Stop();
            UiSources.Vision.Unsubscribe();
        };
        return scroll;
    }

    // ── Control factories ───────────────────────────────────────────────────────

    private static TextBlock Header(string text) => new()
    {
        Text = text,
        FontSize = 10,
        FontWeight = FontWeight.Bold,
        Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x7A, 0xB8, 0xF5)),
        Margin = new Thickness(0, 6, 0, 1),
    };

    private static CheckBox AddCheck(StackPanel parent, string label)
    {
        var cb = new CheckBox { Content = label, FontSize = 11, Foreground = Brushes.White };
        parent.Children.Add(cb);
        return cb;
    }

    private static (Slider, TextBlock) AddSlider(StackPanel parent, string name, string key, double min, double max, int decimals, bool snap)
    {
        var label = new TextBlock { FontSize = 11, Foreground = Brushes.White, Text = name + ":" };
        var slider = new Slider { Minimum = min, Maximum = max, Width = 170, VerticalAlignment = VerticalAlignment.Center };
        if (snap) { slider.TickFrequency = 1; slider.IsSnapToTickEnabled = true; }

        // Companion textbox: lets the user type exact values (matters for the
        // narrow-range tunables like Slope floor Z 0.30–0.95 where a single
        // pixel of slider travel is meaningful). Kept two-way synced via the
        // `syncing` flag so updating one side doesn't loop back through the
        // other side's change handler.
        string fmt = "F" + decimals;
        var box = new TextBox
        {
            FontSize = 11,
            Width = 64,
            Margin = new Thickness(6, 0, 0, 0),
            FontFamily = new FontFamily("Consolas,monospace"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(slider);
        row.Children.Add(box);

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(label);
        stack.Children.Add(row);
        parent.Children.Add(stack);

        bool syncing = false;
        slider.ValueChanged += (_, e) =>
        {
            string s = e.NewValue.ToString(fmt, CultureInfo.InvariantCulture);
            label.Text = $"{name}: {s}";
            if (syncing) { RynthLog.UI($"RynthVisionPanel.Slider[{name}].ValueChanged → {s} (syncing, no push)"); return; }
            RynthLog.UI($"RynthVisionPanel.Slider[{name}].ValueChanged → {s}");
            syncing = true;
            box.Text = s;
            syncing = false;
            Push(key);
        };
        box.GotFocus += (_, _) =>
        {
            // Block AvaloniaSubclassWndProc from yanking focus back to the
            // game HWND while the user is typing into this textbox.
            Win32Backend.AvaloniaTextInputActive = true;
            RynthLog.UI($"RynthVisionPanel.TextBox[{name}].GotFocus (text='{box.Text}')");
        };
        box.LostFocus += (_, _) =>
        {
            Win32Backend.AvaloniaTextInputActive = false;
            RynthLog.UI($"RynthVisionPanel.TextBox[{name}].LostFocus (text='{box.Text}', syncing={syncing})");
            if (syncing) return;
            // Don't clobber the user's typed value on bad input — leave it
            // visible so they can fix the typo without re-typing from scratch.
            if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            {
                v = Math.Clamp(v, min, max);
                syncing = true;
                slider.Value = v;
                string s = v.ToString(fmt, CultureInfo.InvariantCulture);
                box.Text = s;
                label.Text = $"{name}: {s}";
                syncing = false;
                Push(key);
            }
        };

        return (slider, label);
    }

    private static (TextBox, Border) AddColorRow(StackPanel parent, string name, string key, Func<uint> get, Action<uint> set)
    {
        var lbl = new TextBlock { Text = name, FontSize = 11, Foreground = Brushes.White, Width = 44, VerticalAlignment = VerticalAlignment.Center };
        var tb = new TextBox { FontSize = 11, Width = 92, FontFamily = new FontFamily("Consolas,monospace") };
        var sw = new Border { Width = 22, Height = 16, BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        row.Children.Add(lbl);
        row.Children.Add(tb);
        row.Children.Add(sw);
        parent.Children.Add(row);

        tb.GotFocus  += (_, _) => Win32Backend.AvaloniaTextInputActive = true;
        tb.LostFocus += (_, _) =>
        {
            Win32Backend.AvaloniaTextInputActive = false;
            if (TryParseHex(tb.Text, out uint c)) { set(c); sw.Background = ToBrush(c); Push(key); }
            else tb.Text = get().ToString("X8"); // revert on bad input
        };
        return (tb, sw);
    }

    // ── Sync ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the plugin's current settings JSON and applies it to the panel
    /// controls. Returns true only when the JSON contained at least one
    /// recognised field — an empty "{}" from a still-initializing plugin
    /// returns false so the caller can retry rather than treating control
    /// defaults as the authoritative state.
    /// </summary>
    private static bool Populate()
    {
        var snap = UiSources.Vision.Current;
        if (snap == null || snap.Version <= _populateAfterVersion) return false;
        string? json = snap.Value.Json;
        if (string.IsNullOrEmpty(json) || json == "{}") return false;

        RynthLog.UI($"RynthVisionPanel.Populate: applying JSON ({json.Length} chars): {json}");

        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            _suppressPush = true;

            if (_cbRadar  != null && r.TryGetProperty("radar",  out var v)) _cbRadar.IsChecked  = v.GetInt32() != 0;
            if (_cbSlopes != null && r.TryGetProperty("slopes", out v))     _cbSlopes.IsChecked = v.GetInt32() != 0;
            if (_cbWater  != null && r.TryGetProperty("water",  out v))     _cbWater.IsChecked  = v.GetInt32() != 0;
            if (_cbWaterAnyCorner != null && r.TryGetProperty("waterAnyCorner", out v))
                _cbWaterAnyCorner.IsChecked = v.GetInt32() != 0;
            if (_cbWaterImpassableOnly != null && r.TryGetProperty("waterImpassableOnly", out v))
                _cbWaterImpassableOnly.IsChecked = v.GetInt32() != 0;

            if (r.TryGetProperty("slopeColor", out v)) { _slopeColor = v.GetUInt32(); SetColor(_tbSlope, _swSlope, _slopeColor); }
            if (r.TryGetProperty("waterColor", out v)) { _waterColor = v.GetUInt32(); SetColor(_tbWater, _swWater, _waterColor); }
            if (r.TryGetProperty("radarColor", out v)) { _radarColor = v.GetUInt32(); SetColor(_tbRadar, _swRadar, _radarColor); }

            if (_slRange     != null && r.TryGetProperty("radarRange",  out v)) _slRange.Value     = v.GetDouble();
            if (_slThick     != null && r.TryGetProperty("ringThick",   out v)) _slThick.Value     = v.GetDouble();
            if (_slHeight    != null && r.TryGetProperty("ringHeight",  out v)) _slHeight.Value    = v.GetDouble();
            if (_slSlopeR    != null && r.TryGetProperty("slopeRadius", out v)) _slSlopeR.Value    = v.GetInt32();
            if (_slSlopeFloor!= null && r.TryGetProperty("slopeFloorZ",out v)) _slSlopeFloor.Value = v.GetDouble();
            if (_slSlopeBias != null && r.TryGetProperty("slopeBias",   out v)) _slSlopeBias.Value = v.GetDouble();
            if (_slWaterR    != null && r.TryGetProperty("waterRadius", out v)) _slWaterR.Value    = v.GetInt32();

            if (_tbWaterTypes != null && r.TryGetProperty("waterTypes", out v) && v.ValueKind == JsonValueKind.Array)
                _tbWaterTypes.Text = string.Join(",", v.EnumerateArray().Select(e => e.GetInt32()));
        }
        catch (Exception ex) { RynthLog.UI($"RynthVisionPanel.Populate: parse failed: {ex.GetType().Name}: {ex.Message}"); return false; }
        finally { _suppressPush = false; }
        return true;
    }

    /// <summary>
    /// Saves just the field that changed (<paramref name="key"/>, a
    /// <see cref="VisionKeys"/> constant) from its control, so values changed
    /// elsewhere since Populate (a /rv command, the ImGui face) aren't
    /// written back over.
    /// </summary>
    private static void Push(string key)
    {
        if (_suppressPush)
        {
            RynthLog.UI("RynthVisionPanel.Push: SUPPRESSED (Populate in flight or panel not yet ready)");
            return;
        }
        var ci = CultureInfo.InvariantCulture;
        string? value = key switch
        {
            VisionKeys.Radar => Flag(_cbRadar),
            VisionKeys.Slopes => Flag(_cbSlopes),
            VisionKeys.Water => Flag(_cbWater),
            VisionKeys.WaterAnyCorner => Flag(_cbWaterAnyCorner),
            VisionKeys.WaterImpassableOnly => Flag(_cbWaterImpassableOnly),
            VisionKeys.SlopeColor => _slopeColor.ToString(ci),
            VisionKeys.WaterColor => _waterColor.ToString(ci),
            VisionKeys.RadarColor => _radarColor.ToString(ci),
            VisionKeys.RadarRange => _slRange?.Value.ToString("R", ci),
            VisionKeys.RingThick => _slThick?.Value.ToString("R", ci),
            VisionKeys.RingHeight => _slHeight?.Value.ToString("R", ci),
            VisionKeys.SlopeRadius => _slSlopeR != null ? ((int)_slSlopeR.Value).ToString(ci) : null,
            VisionKeys.SlopeFloorZ => _slSlopeFloor?.Value.ToString("R", ci),
            VisionKeys.SlopeBias => _slSlopeBias?.Value.ToString("R", ci),
            VisionKeys.WaterRadius => _slWaterR != null ? ((int)_slWaterR.Value).ToString(ci) : null,
            // Empty resets to the defaults in the plugin; the republish after
            // the save repopulates the box.
            VisionKeys.WaterTypes => "[" + CleanCsv(_tbWaterTypes?.Text) + "]",
            _ => null,
        };
        if (value == null) return;

        string outJson = "{\"" + key + "\":" + value + "}";
        RynthLog.UI($"RynthVisionPanel.Push: writing JSON: {outJson}");
        VisionCommands.Save(outJson);

        if (key == VisionKeys.WaterTypes)
        {
            _populateAfterVersion = UiSources.Vision.Current?.Version ?? 0;
            _populated = false;
            _populateRetries = 0;
            _populateTimer?.Start();
        }
    }

    private static string? Flag(CheckBox? cb) => cb == null ? null : (cb.IsChecked == true ? "1" : "0");

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void SetColor(TextBox? tb, Border? sw, uint argb)
    {
        if (tb != null) tb.Text = argb.ToString("X8");
        if (sw != null) sw.Background = ToBrush(argb);
    }

    private static string CleanCsv(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return "";
        return string.Join(",", csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                    .Where(s => int.TryParse(s, out _)));
    }

    private static bool TryParseHex(string? s, out uint argb)
    {
        argb = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim().TrimStart('#');
        if (s.Length == 6) s = "FF" + s; // assume opaque
        if (s.Length != 8) return false;
        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out argb);
    }

    private static IBrush ToBrush(uint argb) => new SolidColorBrush(Color.FromArgb(
        (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
}
