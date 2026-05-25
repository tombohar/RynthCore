// ============================================================================
//  RynthCore.Engine — UI/Panels/RynthVisionPanel.cs
//  Avalonia settings panel for the RynthVision plugin (RynthSuite). Bridges to
//  the plugin via GetProcAddress on its DLL: reads settings JSON to populate
//  controls and pushes changes back. Replaces the abandoned in-plugin ImGui UI.
//
//  Bridge exports (resolved from the RynthVision plugin DLL):
//    RynthVisionGetSettingsJson()       → ANSI JSON of current settings
//    RynthVisionSetSettings(char* json) → apply + persist settings
//    RynthVisionInspectTerrain()        → log terrain types under the player
// ============================================================================

using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RynthCore.Engine.ImGuiBackend;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.UI.Panels;

internal static class RynthVisionPanel
{
    // ── P/Invoke ──────────────────────────────────────────────────────────────

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetSettingsJsonFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetSettingsFn(IntPtr ansiJson);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void InspectFn();

    // ── Bridge state ──────────────────────────────────────────────────────────

    private static GetSettingsJsonFn? _getSettings;
    private static SetSettingsFn? _setSettings;
    private static InspectFn? _inspect;
    private static bool _bindingLogged;

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

        int createN = System.Threading.Interlocked.Increment(ref _createCounter);
        RynthLog.UI($"RynthVisionPanel.Create #{createN}: starting (_populated reset, _suppressPush=true)");

        var root = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(8), Spacing = 3 };

        root.Children.Add(Header("Overlays"));
        _cbRadar  = AddCheck(root, "Radar range ring");
        _cbSlopes = AddCheck(root, "Unclimbable slopes");
        _cbWater  = AddCheck(root, "Impassable water");
        var dungeon = AddCheck(root, "Dungeon lighting (planned)");
        dungeon.IsEnabled = false;
        _cbRadar.IsCheckedChanged  += (_, _) => Push();
        _cbSlopes.IsCheckedChanged += (_, _) => Push();
        _cbWater.IsCheckedChanged  += (_, _) => Push();

        root.Children.Add(Header("Colors (hex AARRGGBB)"));
        (_tbSlope, _swSlope) = AddColorRow(root, "Slope", () => _slopeColor, c => _slopeColor = c);
        (_tbWater, _swWater) = AddColorRow(root, "Water", () => _waterColor, c => _waterColor = c);
        (_tbRadar, _swRadar) = AddColorRow(root, "Radar", () => _radarColor, c => _radarColor = c);

        root.Children.Add(Header("Tuning"));
        (_slRange,  _) = AddSlider(root, "Radar range",    24,  300, 0, false);
        (_slThick,  _) = AddSlider(root, "Ring thickness", 0.5, 6,   1, false);
        (_slHeight, _) = AddSlider(root, "Ring height (m)",0.5, 30,  1, false);
        (_slSlopeR, _)    = AddSlider(root, "Slope radius (cells)", 1, 24,   0, true);
        (_slSlopeFloor, _)= AddSlider(root, "Slope floor Z",        0.30, 0.95, 3, false);
        (_slSlopeBias, _) = AddSlider(root, "Slope height bias (m)",0.00, 1.00, 2, false);
        (_slWaterR, _)    = AddSlider(root, "Water radius (cells)", 1, 24,   0, true);

        root.Children.Add(Header("Water terrain types"));
        _cbWaterAnyCorner = AddCheck(root, "Highlight cell if ANY corner is water (else all four)");
        _cbWaterAnyCorner.IsCheckedChanged += (_, _) => Push();
        _cbWaterImpassableOnly = AddCheck(root, "Only paint water cells with an unwalkable triangle (real impassable)");
        _cbWaterImpassableOnly.IsCheckedChanged += (_, _) => Push();
        _tbWaterTypes = new TextBox { Watermark = "18,19,20", FontSize = 11, MinWidth = 120 };
        _tbWaterTypes.GotFocus  += (_, _) => Win32Backend.AvaloniaTextInputActive = true;
        _tbWaterTypes.LostFocus += (_, _) => { Win32Backend.AvaloniaTextInputActive = false; Push(); };
        var applyBtn = new Button { Content = "Apply", FontSize = 11, Margin = new Thickness(4, 0, 0, 0) };
        applyBtn.Click += (_, _) => Push();
        var wtRow = new StackPanel { Orientation = Orientation.Horizontal };
        wtRow.Children.Add(_tbWaterTypes);
        wtRow.Children.Add(applyBtn);
        root.Children.Add(wtRow);

        var inspectBtn = new Button { Content = "Log terrain types here", FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
        inspectBtn.Click += (_, _) => { TryBind(); _inspect?.Invoke(); };
        root.Children.Add(inspectBtn);

        // Bind + populate once the plugin DLL is loaded AND the plugin has
        // finished initializing its settings (Runtime.Plugin is non-null on
        // the plugin side). Until then, GetSettingsJson() returns "{}" and
        // populating from that would blank every control — and the first
        // user edit would Push() those blanks over the real saved JSON.
        // Retries every 500 ms until Populate succeeds (returns true) or the
        // panel is closed. Caps at 60 retries (~30 s) so we don't spin forever
        // if the plugin never initializes.
        int retries = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            if (_populated) { timer.Stop(); return; }
            if (++retries > 60) { timer.Stop(); RynthLog.UI("RynthVisionPanel: gave up populating after 30s"); return; }
            TryBind();
            if (_getSettings != null && Populate())
            {
                _populated = true;
                timer.Stop();
            }
        };
        timer.Start();

        return new ScrollViewer { Content = root };
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

    private static (Slider, TextBlock) AddSlider(StackPanel parent, string name, double min, double max, int decimals, bool snap)
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
            Push();
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
                Push();
            }
        };

        return (slider, label);
    }

    private static (TextBox, Border) AddColorRow(StackPanel parent, string name, Func<uint> get, Action<uint> set)
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
            if (TryParseHex(tb.Text, out uint c)) { set(c); sw.Background = ToBrush(c); Push(); }
            else tb.Text = get().ToString("X8"); // revert on bad input
        };
        return (tb, sw);
    }

    // ── Plugin binding ────────────────────────────────────────────────────────

    private static void TryBind()
    {
        LoadedPlugin? plugin = PluginManager.Plugins.FirstOrDefault(
            p => p.DisplayName.Contains("RynthVision", StringComparison.OrdinalIgnoreCase));
        if (plugin == null || plugin.ModuleHandle == IntPtr.Zero) return;

        _getSettings ??= Bind<GetSettingsJsonFn>(plugin, "RynthVisionGetSettingsJson");
        _setSettings ??= Bind<SetSettingsFn>(plugin, "RynthVisionSetSettings");
        _inspect     ??= Bind<InspectFn>(plugin, "RynthVisionInspectTerrain");

        if (!_bindingLogged && _getSettings != null)
        {
            _bindingLogged = true;
            RynthLog.UI("RynthVisionPanel: bound RynthVision plugin exports.");
        }
    }

    private static T? Bind<T>(LoadedPlugin plugin, string export) where T : Delegate
    {
        IntPtr addr = GetProcAddress(plugin.ModuleHandle, export);
        return addr == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(addr);
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
        if (_getSettings == null) return false;
        IntPtr ptr = _getSettings();
        if (ptr == IntPtr.Zero) return false;
        string? json = Marshal.PtrToStringAnsi(ptr);
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

    private static void Push()
    {
        if (_suppressPush)
        {
            RynthLog.UI("RynthVisionPanel.Push: SUPPRESSED (Populate in flight or panel not yet ready)");
            return;
        }
        if (_setSettings == null)
        {
            RynthLog.UI("RynthVisionPanel.Push: SKIPPED (plugin not yet bound)");
            return;
        }
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(256);
        sb.Append('{');
        sb.Append("\"radar\":").Append(_cbRadar?.IsChecked == true ? 1 : 0).Append(',');
        sb.Append("\"slopes\":").Append(_cbSlopes?.IsChecked == true ? 1 : 0).Append(',');
        sb.Append("\"water\":").Append(_cbWater?.IsChecked == true ? 1 : 0).Append(',');
        sb.Append("\"slopeColor\":").Append(_slopeColor).Append(',');
        sb.Append("\"waterColor\":").Append(_waterColor).Append(',');
        sb.Append("\"radarColor\":").Append(_radarColor).Append(',');
        sb.Append("\"radarRange\":").Append((_slRange?.Value ?? 192).ToString("R", ci)).Append(',');
        sb.Append("\"ringThick\":").Append((_slThick?.Value ?? 2).ToString("R", ci)).Append(',');
        sb.Append("\"ringHeight\":").Append((_slHeight?.Value ?? 3).ToString("R", ci)).Append(',');
        sb.Append("\"slopeRadius\":").Append((int)(_slSlopeR?.Value ?? 12)).Append(',');
        sb.Append("\"slopeFloorZ\":").Append((_slSlopeFloor?.Value ?? 0.664).ToString("R", ci)).Append(',');
        sb.Append("\"slopeBias\":").Append((_slSlopeBias?.Value ?? 0.15).ToString("R", ci)).Append(',');
        sb.Append("\"waterRadius\":").Append((int)(_slWaterR?.Value ?? 12)).Append(',');
        sb.Append("\"waterAnyCorner\":").Append(_cbWaterAnyCorner?.IsChecked == true ? 1 : 0).Append(',');
        sb.Append("\"waterImpassableOnly\":").Append(_cbWaterImpassableOnly?.IsChecked == true ? 1 : 0).Append(',');
        sb.Append("\"waterTypes\":[").Append(CleanCsv(_tbWaterTypes?.Text)).Append(']');
        sb.Append('}');

        string outJson = sb.ToString();
        RynthLog.UI($"RynthVisionPanel.Push: writing JSON ({outJson.Length} chars): {outJson}");
        IntPtr p = Marshal.StringToHGlobalAnsi(outJson);
        try { _setSettings(p); }
        finally { Marshal.FreeHGlobal(p); }
    }

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
