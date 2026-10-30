// ============================================================================
//  RynthCore.Engine — UI/Panels/RynthNavPanel.cs
//  Avalonia bar panel for the RynthNav plugin (mesh navigation): shows its
//  status and drives load/test/preview/go/move. Data goes through UiDataHub
//  (UI/Data/RynthNavData.cs): UiSources.RynthNav polls the status on the pump
//  thread and RynthNavCommands runs the actions there; the ImGui face
//  (ImGui/Panels/RynthNavFace.cs) reads the same snapshot.
//  This is the only visible RynthNav surface in Decal-coexistence mode (ImGui and
//  the Nav3D world overlay are both unavailable there).
//
//  Plugin exports (called by the hub):
//    RynthNavGetStatusJson()        → ANSI JSON status
//    RynthNavLoadTile()             → load current landblock's baked tile
//    RynthNavTestQuery()            → FindNearestPoly at the player
//    RynthNavPreviewPath(char* loc) → preview a route to a /loc coordinate
// ============================================================================

using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RynthCore.Engine.ImGuiBackend;
using RynthCore.Engine.Plugins;
using RynthCore.Engine.UI.Data;

namespace RynthCore.Engine.UI.Panels;

internal static class RynthNavPanel
{
    // ── Palette (matches RynthAiPanel) ──────────────────────────────────────────
    private static readonly IBrush ColTeal   = new SolidColorBrush(Color.FromRgb(0x26, 0xD9, 0xE6));
    private static readonly IBrush ColGreen  = new SolidColorBrush(Color.FromRgb(0x40, 0xD9, 0x73));
    private static readonly IBrush ColAmber  = new SolidColorBrush(Color.FromRgb(0xE8, 0xB3, 0x33));
    private static readonly IBrush ColRed     = new SolidColorBrush(Color.FromRgb(0xD9, 0x55, 0x55));
    private static readonly IBrush ColMute   = new SolidColorBrush(Color.FromRgb(0x8C, 0xA6, 0xBF));
    private static readonly IBrush ColText   = Brushes.White;
    private static readonly IBrush ColShellBg = new SolidColorBrush(Color.FromRgb(0x0A, 0x0F, 0x14));
    private static readonly IBrush ColPanelBg = new SolidColorBrush(Color.FromRgb(0x0A, 0x12, 0x1A));
    private static readonly IBrush ColBtnFill = new SolidColorBrush(Color.FromRgb(0x0F, 0x1F, 0x2E));
    private static readonly IBrush ColBtnBord = new SolidColorBrush(Color.FromRgb(0x26, 0x40, 0x59));
    private static readonly IBrush ColSep      = new SolidColorBrush(Color.FromRgb(0x1A, 0x2A, 0x39));

    // ── Controls (UI thread only) ───────────────────────────────────────────────
    private static Ellipse? _dot;
    private static TextBlock? _playerVal, _meshVal, _statusVal, _pathVal;
    private static TextBox? _coordBox;
    private static Button? _fwdBtn, _backBtn, _leftBtn, _rightBtn;

    internal static Control Create()
    {

        var root = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(10), Spacing = 5, MinWidth = 300 };

        // Title
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _dot = new Ellipse { Width = 9, Height = 9, Fill = ColMute, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(_dot);
        title.Children.Add(new TextBlock { Text = "RynthNav", FontSize = 15, FontWeight = FontWeight.Bold, Foreground = ColTeal });
        title.Children.Add(new TextBlock { Text = "mesh nav · v0.2", FontSize = 10, Foreground = ColMute, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(2, 0, 0, 2) });
        root.Children.Add(title);
        root.Children.Add(Sep());

        // Status block
        _playerVal = ValueRow(root, "Player");
        _meshVal   = ValueRow(root, "Navmesh");
        _statusVal = new TextBlock { Text = "—", FontSize = 11, Foreground = ColMute, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) };
        root.Children.Add(_statusVal);
        root.Children.Add(Sep());

        // Movement d-pad (each tap is marshaled to the plugin tick thread)
        root.Children.Add(new TextBlock { Text = "MOVE", FontSize = 9, FontWeight = FontWeight.Bold, Foreground = ColTeal, Margin = new Thickness(0, 2, 0, 1) });
        root.Children.Add(BuildDpad());
        root.Children.Add(new TextBlock { Text = "tap ▲▼ run  ·  tap ◄► turn (tap again to stop)  ·  ■ stop", FontSize = 9, Foreground = ColMute, HorizontalAlignment = HorizontalAlignment.Center });
        root.Children.Add(Sep());

        // Actions
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        actions.Children.Add(Btn("Load tile", RynthNavCommands.LoadTile));
        actions.Children.Add(Btn("Test @ me", RynthNavCommands.TestQuery));
        root.Children.Add(actions);
        root.Children.Add(Sep());

        // Goto preview
        root.Children.Add(new TextBlock { Text = "PREVIEW ROUTE", FontSize = 9, FontWeight = FontWeight.Bold, Foreground = ColTeal, Margin = new Thickness(0, 2, 0, 1) });
        var gotoRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        _coordBox = new TextBox
        {
            Watermark = "42.5N, 33.6E",
            FontSize = 12,
            Width = 150,
            FontFamily = new FontFamily("Consolas,monospace"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _coordBox.GotFocus  += (_, _) => Win32Backend.AvaloniaTextInputActive = true;
        _coordBox.LostFocus += (_, _) => Win32Backend.AvaloniaTextInputActive = false;
        gotoRow.Children.Add(_coordBox);
        gotoRow.Children.Add(Btn("Preview", Preview));
        gotoRow.Children.Add(Btn("Go ▶", Go));
        root.Children.Add(gotoRow);
        _pathVal = new TextBlock { Text = "", FontSize = 12, Foreground = ColText, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 1, 0, 0) };
        root.Children.Add(_pathVal);

        root.Children.Add(new TextBlock { Text = "Go ▶ walks the navmesh path · ■ (or any d-pad tap) stops", FontSize = 9, Foreground = ColMute, Margin = new Thickness(0, 6, 0, 0) });

        // Poll the plugin for status (UI thread).
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        long seenVersion = -1;
        timer.Tick += (_, _) =>
        {
            var snap = UiSources.RynthNav.Current;
            if (snap == null) { if (_dot != null) _dot.Fill = ColMute; return; }
            if (snap.Version == seenVersion) return;
            seenVersion = snap.Version;
            Refresh(snap.Value);
        };
        var outer = new Border { Background = ColShellBg, CornerRadius = new CornerRadius(5), Child = new ScrollViewer { Content = root } };
        outer.AttachedToVisualTree += (_, _) =>
        {
            UiSources.RynthNav.Subscribe();
            UiSources.RynthNav.RequestRefresh();
            timer.Start();
        };
        outer.DetachedFromVisualTree += (_, _) =>
        {
            timer.Stop();
            UiSources.RynthNav.Unsubscribe();
        };
        return outer;
    }

    private static void Refresh(RynthNavStatus st)
    {
        // Change-detection: only touch a control when its value actually changed,
        // so an idle panel issues zero invalidations (no GDI-overlay flicker).
        SetText(_playerVal, st.PlayerText);
        SetText(_meshVal, st.MeshText);
        SetFg(_meshVal, st.TileLoaded ? ColGreen : ColAmber);
        SetFill(_dot, st.TileLoaded ? ColGreen : (st.HasPose ? ColAmber : ColMute));
        SetText(_statusVal, st.Status);
        SetText(_pathVal, st.PathText);
        SetBg(_fwdBtn,   st.RunState == 1 ? ColGreen : ColBtnFill);
        SetBg(_backBtn,  st.RunState == 2 ? ColGreen : ColBtnFill);
        SetBg(_leftBtn,  st.TurnState == -1 ? ColGreen : ColBtnFill);
        SetBg(_rightBtn, st.TurnState == 1 ? ColGreen : ColBtnFill);
    }

    private static void Preview() => RynthNavCommands.Preview(_coordBox?.Text ?? "");

    private static void Go() => RynthNavCommands.Goto(_coordBox?.Text ?? "");

    // ── UI helpers ──────────────────────────────────────────────────────────────
    private static TextBlock ValueRow(StackPanel parent, string label)
    {
        var lbl = new TextBlock { Text = label, FontSize = 10, Foreground = ColMute, Width = 58, VerticalAlignment = VerticalAlignment.Center };
        var val = new TextBlock { Text = "—", FontSize = 12, Foreground = ColText, FontWeight = FontWeight.SemiBold, FontFamily = new FontFamily("Consolas,monospace"), VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(lbl);
        row.Children.Add(val);
        parent.Children.Add(row);
        return val;
    }

    private static Button Btn(string text, Action onClick)
    {
        var b = new Button
        {
            Content = text,
            FontSize = 11,
            Foreground = ColText,
            Background = ColBtnFill,
            BorderBrush = ColBtnBord,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(9, 3, 9, 3),
            Margin = new Thickness(0, 0, 6, 0),
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    private static Border Sep() => new() { Height = 1, Background = ColSep, Margin = new Thickness(0, 3, 0, 3) };

    private static Control BuildDpad()
    {
        var pad = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
        };
        _fwdBtn   = MoveBtn("▲", "Forward (tap to run / stop)", 1);
        _leftBtn  = MoveBtn("◄", "Turn left (tap to start / stop)", 3);
        var stop  = MoveBtn("■", "Stop everything", 5);
        _rightBtn = MoveBtn("►", "Turn right (tap to start / stop)", 4);
        _backBtn  = MoveBtn("▼", "Backward (tap to walk back / stop)", 2);
        Place(pad, _fwdBtn, 0, 1);
        Place(pad, _leftBtn, 1, 0);
        Place(pad, stop, 1, 1);
        Place(pad, _rightBtn, 1, 2);
        Place(pad, _backBtn, 2, 1);
        return pad;
    }

    private static void Place(Grid g, Control c, int row, int col)
    {
        Grid.SetRow(c, row);
        Grid.SetColumn(c, col);
        g.Children.Add(c);
    }

    private static Button MoveBtn(string glyph, string tip, int cmd)
    {
        var b = new Button
        {
            Content = glyph,
            FontSize = 18,
            Width = 58,
            Height = 42,
            Foreground = ColText,
            Background = ColBtnFill,
            BorderBrush = ColBtnBord,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Margin = new Thickness(3),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => RynthNavCommands.Move(cmd);
        return b;
    }

    private static void SetText(TextBlock? tb, string s) { if (tb != null && !string.Equals(tb.Text, s, StringComparison.Ordinal)) tb.Text = s; }
    private static void SetFg(TextBlock? tb, IBrush br) { if (tb != null && !ReferenceEquals(tb.Foreground, br)) tb.Foreground = br; }
    private static void SetBg(Button? b, IBrush br) { if (b != null && !ReferenceEquals(b.Background, br)) b.Background = br; }
    private static void SetFill(Ellipse? e, IBrush br) { if (e != null && !ReferenceEquals(e.Fill, br)) e.Fill = br; }

}
