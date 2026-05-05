// ============================================================================
//  RynthCore.Engine — UI/Panels/NavPanel.cs
//  Avalonia replica of LegacyNavigationUi.cs (RynthSuite plugin).
//
//  Plugin exports used:
//    RynthPluginGetNavJson     → polled every 1s for live state
//    RynthPluginSendNavCommand → dispatched on button actions
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.UI.Panels;

[JsonSerializable(typeof(NavPanel.Payload))]
[JsonSerializable(typeof(NavPanel.NavCmd))]
[JsonSerializable(typeof(List<NavPanel.NavPoint>))]
[JsonSerializable(typeof(List<string>), TypeInfoPropertyName = "StringList")]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = false, IncludeFields = false)]
internal partial class NavPanelJsonContext : JsonSerializerContext { }

internal static class NavPanel
{
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetNavJsonFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SendNavCommandFn(IntPtr ansiJson);

    private static GetNavJsonFn?     _getNavJson;
    private static SendNavCommandFn? _sendNavCommand;

    private static readonly IBrush ColAmber   = new SolidColorBrush(Color.FromRgb(0xE8, 0xB3, 0x33));
    private static readonly IBrush ColGreen   = new SolidColorBrush(Color.FromRgb(0x33, 0xCC, 0x66));
    private static readonly IBrush ColTeal    = new SolidColorBrush(Color.FromRgb(0x26, 0xD9, 0xE6));
    private static readonly IBrush ColMute    = new SolidColorBrush(Color.FromRgb(0x8C, 0xA6, 0xBF));
    private static readonly IBrush ColTextDim = new SolidColorBrush(Color.FromRgb(0xD9, 0xE6, 0xF2));
    private static readonly IBrush ColShellBg = new SolidColorBrush(Color.FromRgb(0x0A, 0x0F, 0x14));
    private static readonly IBrush ColPanelBg = new SolidColorBrush(Color.FromRgb(0x14, 0x1F, 0x29));
    private static readonly IBrush ColRowAlt  = new SolidColorBrush(Color.FromRgb(0x10, 0x18, 0x22));
    private static readonly IBrush ColBtnFill = new SolidColorBrush(Color.FromRgb(0x0F, 0x1F, 0x2E));
    private static readonly IBrush ColBtnBord = new SolidColorBrush(Color.FromRgb(0x26, 0x40, 0x59));

    private static readonly IBrush ColStartBg = new SolidColorBrush(Color.FromRgb(0x19, 0x61, 0x19));
    private static readonly IBrush ColStopBg  = new SolidColorBrush(Color.FromRgb(0x80, 0x19, 0x19));

    private static readonly string[] RouteTypes = { "Once", "Circular", "Linear", "Follow" };
    private static readonly string[] AddModes   = { "End", "Above", "Below" };

    private static readonly int[] RecallIds =
    {
        48, 2645, 2647,
        1635, 1636,
        157, 158, 1637,
        2648, 2649, 2650,
        2931, 2023, 2041, 2358, 2813, 2941, 2943,
        3865, 3929, 3930, 4084, 4198, 4213,
        4907, 4908, 4909,
        5175, 5330, 5541, 6150, 6321, 6322,
    };

    // ── Data types (mirror NavBridgePayload / NavCommand on plugin side) ──────

    internal sealed class NavPoint
    {
        public int    Idx  { get; set; }
        public string Type { get; set; } = string.Empty;
        public string Desc { get; set; } = string.Empty;
        public double NS   { get; set; }
        public double EW   { get; set; }
        public double Z    { get; set; }
    }

    internal sealed class Payload
    {
        public string         ActiveNavName     { get; set; } = string.Empty;
        public string         NavStatusLine     { get; set; } = string.Empty;
        public bool           NavIsStuck        { get; set; }
        public bool           MacroRunning      { get; set; }
        public bool           NavigationEnabled { get; set; }
        public int            RouteType         { get; set; }
        public int            ActiveNavIndex    { get; set; }
        public List<string>   NavFiles          { get; set; } = new();
        public List<NavPoint> Points            { get; set; } = new();
    }

    internal sealed class NavCmd
    {
        public string Cmd       { get; set; } = string.Empty;
        public int    SpellId   { get; set; }
        public int    Index     { get; set; }
        public int    RouteType { get; set; }
        public int    AddMode   { get; set; }
        public int    InsertAt  { get; set; } = -1;
        public string NavName   { get; set; } = string.Empty;
    }

    private sealed class State
    {
        public Payload Data        = new();
        public int     SelectedIdx = -1;
        public int     AddModeIdx  = 0;
    }

    // ── Picker overlay (identical pattern to SettingsPanel) ──────────────────

    private sealed class PickerState
    {
        public Canvas   Canvas      = null!;
        public Border?  ActivePicker;
        public Button?  ActiveAnchor;
        public Control? Root;

        public void Close()
        {
            if (ActivePicker != null) { Canvas.Children.Remove(ActivePicker); ActivePicker = null; }
            ActiveAnchor = null;
            Canvas.IsHitTestVisible = false;
        }

        public void Show(Button anchor, string[] items, int selected, Action<int> onPick)
        {
            if (ReferenceEquals(anchor, ActiveAnchor)) { Close(); return; }
            Close();
            ActiveAnchor = anchor;

            var stack = new StackPanel { Spacing = 1 };
            for (int i = 0; i < items.Length; i++)
            {
                int ci = i;
                var entry = new Button
                {
                    Content = items[i],
                    HorizontalAlignment        = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Background      = i == selected ? new SolidColorBrush(Color.FromRgb(0x1A, 0x2E, 0x42)) : ColBtnFill,
                    Foreground      = i == selected ? ColTeal : ColTextDim,
                    BorderBrush     = ColBtnBord,
                    BorderThickness = new Thickness(1),
                    Padding         = new Thickness(6, 2),
                    FontSize        = 10,
                    Height          = 20,
                };
                entry.Click += (_, _) => { onPick(ci); Close(); };
                stack.Children.Add(entry);
            }

            const double pickerWidth = 220;
            Point ap         = anchor.TranslatePoint(new Point(0, anchor.Bounds.Height), Canvas) ?? new Point(8, 8);
            double rootWidth  = Root?.Bounds.Width  ?? 400;
            double rootHeight = Root?.Bounds.Height ?? 300;
            double left = Math.Clamp(ap.X, 4, Math.Max(4, rootWidth - pickerWidth - 4));
            double top  = ap.Y;
            double maxH = Math.Min(items.Length * 22 + 8, Math.Max(80, rootHeight - top - 4));

            var pb = new Border
            {
                Width           = pickerWidth,
                MaxHeight       = maxH,
                Background      = ColShellBg,
                BorderBrush     = ColTeal,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(4),
                Padding         = new Thickness(2),
                Child = new ScrollViewer
                {
                    VerticalScrollBarVisibility   = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    Content = stack,
                },
            };
            Canvas.Children.Add(pb);
            Avalonia.Controls.Canvas.SetLeft(pb, left);
            Avalonia.Controls.Canvas.SetTop(pb, top);
            ActivePicker = pb;
            Canvas.IsHitTestVisible = true;
        }
    }

    // =========================================================================
    //  Create
    // =========================================================================

    public static Control Create()
    {
        TryBind();
        var state = new State();

        var root = new Border
        {
            Background      = ColShellBg,
            BorderBrush     = ColBtnBord,
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4),
            MinWidth        = 360,
        };

        var rootGrid = new Grid();
        root.Child = rootGrid;

        var pickerCanvas = new Canvas { IsHitTestVisible = false, Background = Brushes.Transparent };
        var picker = new PickerState { Canvas = pickerCanvas, Root = root };

        var content = new StackPanel { Margin = new Thickness(6), Spacing = 4 };

        rootGrid.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility   = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = content,
        });
        rootGrid.Children.Add(pickerCanvas);

        pickerCanvas.PointerPressed += (_, e) =>
        {
            if (picker.ActivePicker != null && ReferenceEquals(e.Source, pickerCanvas))
            { e.Handled = true; picker.Close(); }
        };

        void Rebuild()
        {
            content.Children.Clear();
            var d = state.Data;
            bool navActive = d.MacroRunning && d.NavigationEnabled;

            // ── Active nav name ───────────────────────────────────────────────
            content.Children.Add(new TextBlock
            {
                Text         = $"Active Nav: {(string.IsNullOrEmpty(d.ActiveNavName) ? "None (Unsaved)" : d.ActiveNavName)}",
                Foreground   = ColAmber,
                FontSize     = 11,
                FontWeight   = FontWeight.Bold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            // ── Status line ───────────────────────────────────────────────────
            if (!string.IsNullOrEmpty(d.NavStatusLine))
            {
                content.Children.Add(new TextBlock
                {
                    Text         = d.NavStatusLine,
                    Foreground   = d.NavIsStuck ? ColAmber : ColGreen,
                    FontSize     = 10,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }

            // ── Start / Stop button ───────────────────────────────────────────
            var startStopBtn = new Button
            {
                Content                    = navActive ? "Stop Navigation" : "Start Navigation",
                HorizontalAlignment        = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Height          = 28,
                Background      = navActive ? ColStopBg : ColStartBg,
                Foreground      = ColTextDim,
                BorderBrush     = ColBtnBord,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(3),
                FontSize        = 11,
            };
            startStopBtn.Click += (_, _) =>
            {
                bool active = state.Data.MacroRunning && state.Data.NavigationEnabled;
                if (active)
                {
                    Send(new NavCmd { Cmd = "stopNav" });
                    state.Data.NavigationEnabled = false;
                }
                else
                {
                    Send(new NavCmd { Cmd = "startNav" });
                    state.Data.MacroRunning      = true;
                    state.Data.NavigationEnabled = true;
                }
                Rebuild();
            };
            content.Children.Add(startStopBtn);

            content.Children.Add(new Border { Height = 1, Background = ColBtnBord });

            // ── Route type + Insert mode ──────────────────────────────────────
            var controlRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,4,*") };

            int rIdx     = d.RouteType switch { 1 => 1, 2 => 2, 3 => 3, _ => 0 };
            var routeBtn = MakePickerBtn($"Route: {RouteTypes[rIdx]}");
            routeBtn.Click += (_, _) =>
            {
                int cur = state.Data.RouteType switch { 1 => 1, 2 => 2, 3 => 3, _ => 0 };
                picker.Show(routeBtn, RouteTypes, cur, idx =>
                {
                    int enumVal = idx switch { 1 => 1, 2 => 2, 3 => 3, _ => 4 };
                    Send(new NavCmd { Cmd = "setRouteType", RouteType = enumVal });
                    state.Data.RouteType = enumVal;
                    Rebuild();
                });
            };
            Grid.SetColumn(routeBtn, 0);
            controlRow.Children.Add(routeBtn);

            var insertBtn = MakePickerBtn($"Insert: {AddModes[state.AddModeIdx]}");
            insertBtn.Click += (_, _) =>
            {
                int cur = state.AddModeIdx;
                picker.Show(insertBtn, AddModes, cur, idx =>
                {
                    state.AddModeIdx = idx;
                    Rebuild();
                });
            };
            Grid.SetColumn(insertBtn, 2);
            controlRow.Children.Add(insertBtn);

            content.Children.Add(controlRow);

            // ── Action buttons ────────────────────────────────────────────────
            var actionWrap = new WrapPanel { Orientation = Orientation.Horizontal };

            var addWpBtn = MakeActionBtn("Add Waypoint");
            addWpBtn.Click += (_, _) =>
                Send(new NavCmd { Cmd = "addWaypoint", AddMode = state.AddModeIdx, InsertAt = state.SelectedIdx });
            actionWrap.Children.Add(addWpBtn);

            var addPortalBtn = MakeActionBtn("Add Portal");
            addPortalBtn.IsEnabled = false;
            ToolTip.SetTip(addPortalBtn, "Add Portal is not yet implemented.");
            actionWrap.Children.Add(addPortalBtn);

            var addRecallBtn = MakeActionBtn("Add Recall");
            var recallLabels = new string[RecallIds.Length];
            for (int ri = 0; ri < RecallIds.Length; ri++)
                recallLabels[ri] = $"Spell {RecallIds[ri]} ({RecallIds[ri]})";
            addRecallBtn.Click += (_, _) =>
                picker.Show(addRecallBtn, recallLabels, -1, idx =>
                    Send(new NavCmd { Cmd = "addRecall", SpellId = RecallIds[idx], AddMode = state.AddModeIdx, InsertAt = state.SelectedIdx }));
            actionWrap.Children.Add(addRecallBtn);

            var clearBtn = MakeActionBtn("Clear Route");
            clearBtn.Click += (_, _) =>
            {
                Send(new NavCmd { Cmd = "clearRoute" });
                state.Data.Points.Clear();
                state.SelectedIdx = -1;
                Rebuild();
            };
            actionWrap.Children.Add(clearBtn);

            var saveBtn = MakeActionBtn("Save Route");
            saveBtn.Click += (_, _) => Send(new NavCmd { Cmd = "saveRoute" });
            actionWrap.Children.Add(saveBtn);

            var dunPatrolBtn = MakeActionBtn("Dungeon Patrol");
            dunPatrolBtn.Click += (_, _) => Send(new NavCmd { Cmd = "dunPatrol" });
            actionWrap.Children.Add(dunPatrolBtn);

            content.Children.Add(actionWrap);

            // ── Nav file selector ─────────────────────────────────────────────
            if (d.NavFiles.Count > 0)
            {
                var fileRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                fileRow.Children.Add(new TextBlock
                {
                    Text = "Nav:",
                    Foreground = ColMute,
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0),
                });

                int activeFileIdx  = string.IsNullOrEmpty(d.ActiveNavName) ? -1 : d.NavFiles.IndexOf(d.ActiveNavName);
                string fileBtnText = activeFileIdx >= 0 ? d.NavFiles[activeFileIdx] : "Select nav file...";
                var fileBtn        = MakePickerBtn(fileBtnText);
                Grid.SetColumn(fileBtn, 1);
                fileBtn.Click += (_, _) =>
                {
                    int cur = string.IsNullOrEmpty(state.Data.ActiveNavName) ? -1 : state.Data.NavFiles.IndexOf(state.Data.ActiveNavName);
                    picker.Show(fileBtn, state.Data.NavFiles.ToArray(), cur, idx =>
                        Send(new NavCmd { Cmd = "loadNav", NavName = state.Data.NavFiles[idx] }));
                };
                fileRow.Children.Add(fileBtn);
                content.Children.Add(fileRow);
            }

            // ── Waypoint list ─────────────────────────────────────────────────
            content.Children.Add(new Border { Height = 1, Background = ColBtnBord });
            content.Children.Add(new TextBlock
            {
                Text       = $"Waypoints ({d.Points.Count})",
                Foreground = ColMute,
                FontSize   = 10,
            });

            var waypointStack = new StackPanel { Spacing = 1 };
            for (int i = 0; i < d.Points.Count; i++)
            {
                int  ci         = i;
                var  pt         = d.Points[i];
                bool isActive   = i == d.ActiveNavIndex;
                bool isSelected = i == state.SelectedIdx;

                var row = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("22,18,*"),
                    Background = isSelected
                        ? new SolidColorBrush(Color.FromRgb(0x1A, 0x2E, 0x42))
                        : (i % 2 == 0 ? ColPanelBg : ColRowAlt),
                };

                var delBtn = new Button
                {
                    Content     = "X",
                    Width       = 18,
                    Height      = 18,
                    Padding     = new Thickness(0),
                    Margin      = new Thickness(2),
                    Background  = new SolidColorBrush(Color.FromRgb(0x80, 0x1A, 0x1A)),
                    Foreground  = ColTextDim,
                    BorderBrush = ColBtnBord,
                    BorderThickness = new Thickness(1),
                    CornerRadius    = new CornerRadius(2),
                    FontSize = 9,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                };
                delBtn.Click += (_, e) =>
                {
                    e.Handled = true;
                    Send(new NavCmd { Cmd = "deletePoint", Index = ci });
                    if (state.SelectedIdx == ci)        state.SelectedIdx = -1;
                    else if (state.SelectedIdx > ci)    state.SelectedIdx--;
                    if (ci < state.Data.Points.Count)   state.Data.Points.RemoveAt(ci);
                    Rebuild();
                };
                Grid.SetColumn(delBtn, 0);
                row.Children.Add(delBtn);

                var indicator = new TextBlock
                {
                    Text                = isActive ? "=>" : "  ",
                    Foreground          = isActive ? ColTeal : ColMute,
                    FontSize            = 9,
                    VerticalAlignment   = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                Grid.SetColumn(indicator, 1);
                row.Children.Add(indicator);

                string disp = !string.IsNullOrEmpty(pt.Desc)
                    ? $"[{i}] {pt.Desc}"
                    : $"[{i}] {pt.Type} ({pt.NS:F2}N, {pt.EW:F2}E, {pt.Z:F1})";
                var desc = new TextBlock
                {
                    Text             = disp,
                    Foreground       = isActive ? ColTeal : ColTextDim,
                    FontSize         = 10,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin           = new Thickness(4, 0, 2, 0),
                    TextTrimming     = TextTrimming.CharacterEllipsis,
                };
                Grid.SetColumn(desc, 2);
                row.Children.Add(desc);

                row.PointerPressed += (_, e) =>
                {
                    if (e.Handled) return;
                    state.SelectedIdx = ci;
                    Rebuild();
                };

                waypointStack.Children.Add(row);
            }

            content.Children.Add(new Border
            {
                Height = 200,
                Child = new ScrollViewer
                {
                    VerticalScrollBarVisibility   = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    Content = waypointStack,
                },
            });
        }

        // ── Poll timer ────────────────────────────────────────────────────────
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            if (_getNavJson == null) TryBind();
            if (picker.ActivePicker != null) return;
            if (!TryFetch(out var fresh)) return;
            state.Data = fresh;
            Rebuild();
        };
        timer.Start();

        Rebuild();
        return root;
    }

    // =========================================================================
    //  Helpers
    // =========================================================================

    private static Button MakePickerBtn(string text) => new Button
    {
        Content                    = text,
        HorizontalAlignment        = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Left,
        Background      = ColBtnFill,
        Foreground      = ColTextDim,
        BorderBrush     = ColBtnBord,
        BorderThickness = new Thickness(1),
        CornerRadius    = new CornerRadius(3),
        Padding         = new Thickness(6, 2),
        FontSize        = 10,
        Height          = 22,
    };

    private static Button MakeActionBtn(string text) => new Button
    {
        Content         = text,
        Background      = ColBtnFill,
        Foreground      = ColTextDim,
        BorderBrush     = ColBtnBord,
        BorderThickness = new Thickness(1),
        CornerRadius    = new CornerRadius(3),
        Padding         = new Thickness(8, 3),
        FontSize        = 10,
        Height          = 24,
        Margin          = new Thickness(0, 0, 4, 4),
    };

    // =========================================================================
    //  Plugin bridge
    // =========================================================================

    private static void TryBind()
    {
        var plugin = PluginManager.Plugins.FirstOrDefault(
            p => p.DisplayName.Contains("RynthAi", StringComparison.OrdinalIgnoreCase));
        if (plugin == null || plugin.ModuleHandle == IntPtr.Zero) return;

        if (_getNavJson == null)
        {
            IntPtr p1 = GetProcAddress(plugin.ModuleHandle, "RynthPluginGetNavJson");
            if (p1 != IntPtr.Zero)
                _getNavJson = Marshal.GetDelegateForFunctionPointer<GetNavJsonFn>(p1);
        }
        if (_sendNavCommand == null)
        {
            IntPtr p2 = GetProcAddress(plugin.ModuleHandle, "RynthPluginSendNavCommand");
            if (p2 != IntPtr.Zero)
                _sendNavCommand = Marshal.GetDelegateForFunctionPointer<SendNavCommandFn>(p2);
        }
    }

    private static bool TryFetch(out Payload payload)
    {
        payload = new Payload();
        if (_getNavJson == null) return false;
        try
        {
            IntPtr ptr = _getNavJson();
            if (ptr == IntPtr.Zero) return false;
            string? json = Marshal.PtrToStringAnsi(ptr);
            if (string.IsNullOrEmpty(json)) return false;
            var parsed = JsonSerializer.Deserialize(json, NavPanelJsonContext.Default.Payload);
            if (parsed == null) return false;
            payload = parsed;
            return true;
        }
        catch { return false; }
    }

    private static void Send(NavCmd cmd)
    {
        if (_sendNavCommand == null) TryBind();
        if (_sendNavCommand == null) return;
        IntPtr ansi = IntPtr.Zero;
        try
        {
            string json = JsonSerializer.Serialize(cmd, NavPanelJsonContext.Default.NavCmd);
            ansi = Marshal.StringToHGlobalAnsi(json);
            _sendNavCommand(ansi);
        }
        catch { }
        finally
        {
            if (ansi != IntPtr.Zero) Marshal.FreeHGlobal(ansi);
        }
    }
}
