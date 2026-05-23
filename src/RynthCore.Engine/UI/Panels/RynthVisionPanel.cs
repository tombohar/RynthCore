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

    private static CheckBox? _cbRadar, _cbSlopes, _cbWater;
    private static Slider? _slRange, _slThick, _slSlopeR, _slWaterR;
    private static TextBox? _tbSlope, _tbWater, _tbRadar, _tbWaterTypes;
    private static Border? _swSlope, _swWater, _swRadar;

    private static uint _slopeColor = 0x60FF2020;
    private static uint _waterColor = 0x600060FF;
    private static uint _radarColor = 0x80FFD000;

    private static bool _suppressPush;
    private static bool _populated;

    // ── Panel construction ──────────────────────────────────────────────────────

    internal static Control Create()
    {
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
        (_slSlopeR, _) = AddSlider(root, "Slope radius",   1,   7,   0, true);
        (_slWaterR, _) = AddSlider(root, "Water radius",   1,   7,   0, true);

        root.Children.Add(Header("Water terrain types"));
        _tbWaterTypes = new TextBox { Watermark = "19,20,21", FontSize = 11, MinWidth = 120 };
        _tbWaterTypes.LostFocus += (_, _) => Push();
        var applyBtn = new Button { Content = "Apply", FontSize = 11, Margin = new Thickness(4, 0, 0, 0) };
        applyBtn.Click += (_, _) => Push();
        var wtRow = new StackPanel { Orientation = Orientation.Horizontal };
        wtRow.Children.Add(_tbWaterTypes);
        wtRow.Children.Add(applyBtn);
        root.Children.Add(wtRow);

        var inspectBtn = new Button { Content = "Log terrain types here", FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
        inspectBtn.Click += (_, _) => { TryBind(); _inspect?.Invoke(); };
        root.Children.Add(inspectBtn);

        // Bind + populate once the plugin DLL is loaded (it may load slightly
        // after this panel is constructed). Retries every 500 ms until bound.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            if (_populated) { timer.Stop(); return; }
            TryBind();
            if (_getSettings != null) { Populate(); _populated = true; timer.Stop(); }
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
        var slider = new Slider { Minimum = min, Maximum = max, Width = 200 };
        if (snap) { slider.TickFrequency = 1; slider.IsSnapToTickEnabled = true; }
        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(label);
        stack.Children.Add(slider);
        parent.Children.Add(stack);
        slider.ValueChanged += (_, e) =>
        {
            label.Text = $"{name}: {e.NewValue.ToString("F" + decimals, CultureInfo.InvariantCulture)}";
            Push();
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

        tb.LostFocus += (_, _) =>
        {
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

    private static void Populate()
    {
        if (_getSettings == null) return;
        IntPtr ptr = _getSettings();
        if (ptr == IntPtr.Zero) return;
        string? json = Marshal.PtrToStringAnsi(ptr);
        if (string.IsNullOrEmpty(json)) return;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            _suppressPush = true;

            if (_cbRadar  != null && r.TryGetProperty("radar",  out var v)) _cbRadar.IsChecked  = v.GetInt32() != 0;
            if (_cbSlopes != null && r.TryGetProperty("slopes", out v))     _cbSlopes.IsChecked = v.GetInt32() != 0;
            if (_cbWater  != null && r.TryGetProperty("water",  out v))     _cbWater.IsChecked  = v.GetInt32() != 0;

            if (r.TryGetProperty("slopeColor", out v)) { _slopeColor = v.GetUInt32(); SetColor(_tbSlope, _swSlope, _slopeColor); }
            if (r.TryGetProperty("waterColor", out v)) { _waterColor = v.GetUInt32(); SetColor(_tbWater, _swWater, _waterColor); }
            if (r.TryGetProperty("radarColor", out v)) { _radarColor = v.GetUInt32(); SetColor(_tbRadar, _swRadar, _radarColor); }

            if (_slRange  != null && r.TryGetProperty("radarRange",  out v)) _slRange.Value  = v.GetDouble();
            if (_slThick  != null && r.TryGetProperty("ringThick",   out v)) _slThick.Value  = v.GetDouble();
            if (_slSlopeR != null && r.TryGetProperty("slopeRadius", out v)) _slSlopeR.Value = v.GetInt32();
            if (_slWaterR != null && r.TryGetProperty("waterRadius", out v)) _slWaterR.Value = v.GetInt32();

            if (_tbWaterTypes != null && r.TryGetProperty("waterTypes", out v) && v.ValueKind == JsonValueKind.Array)
                _tbWaterTypes.Text = string.Join(",", v.EnumerateArray().Select(e => e.GetInt32()));
        }
        catch { }
        finally { _suppressPush = false; }
    }

    private static void Push()
    {
        if (_suppressPush || _setSettings == null) return;
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
        sb.Append("\"slopeRadius\":").Append((int)(_slSlopeR?.Value ?? 4)).Append(',');
        sb.Append("\"waterRadius\":").Append((int)(_slWaterR?.Value ?? 4)).Append(',');
        sb.Append("\"waterTypes\":[").Append(CleanCsv(_tbWaterTypes?.Text)).Append(']');
        sb.Append('}');

        IntPtr p = Marshal.StringToHGlobalAnsi(sb.ToString());
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
