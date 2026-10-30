// ============================================================================
//  RynthCore.Engine — UI/Panels/RynthTrackerPanel.cs
//  Session-stats panel backed by the RynthTracker plugin (RynthSuite).
//
//  Data comes from UiDataHub (UiSources.Tracker), which is the only caller of
//  RynthTrackerGetSnapshotJson: the export frees its previous buffer on each
//  call, so this face and the ImGui face (ImGui/Panels/P1Faces.cs) must never
//  call it themselves. Reset goes through the hub too. The background opacity
//  is shared with the ImGui face (TrackerSettings).
// ============================================================================

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RynthCore.Engine.UI.Data;

namespace RynthCore.Engine.UI.Panels;

internal static class RynthTrackerPanel
{
    // ── Panel construction ────────────────────────────────────────────────────

    internal static Control Create()
    {

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
        SolidColorBrush MakeBg() => new(Color.FromArgb(TrackerSettings.BgAlpha, 0x0A, 0x12, 0x1A));
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
            Value    = Math.Round(TrackerSettings.BgAlpha / 2.55),
            Padding  = new Thickness(0),
            Margin   = new Thickness(4, 0, 4, 0),
            VerticalAlignment   = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        opacitySlider.ValueChanged += (_, e) =>
        {
            TrackerSettings.SetBgAlpha((byte)Math.Round(e.NewValue * 2.55), save: true);
            stack.Background = MakeBg();
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
        resetBtn.Click += (_, _) => UiSources.Tracker.RequestReset();

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

        // ── Hub snapshot (500ms) ───────────────────────────────────────
        var values = new[] { sessionVal, xpHrVal, lumHrVal, killHrVal, xpVal, lumVal, killVal, xpkVal, deathVal };
        long seen = -1;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            var snap = UiSources.Tracker.Current;
            if (snap == null || snap.Version == seen) return;
            seen = snap.Version;
            for (int i = 0; i < values.Length; i++)
                values[i].Text = snap.Value.Values[i];
            // Opacity may have been changed by the ImGui face.
            stack.Background = MakeBg();
        };
        // Stop with the visual tree — a running DispatcherTimer roots the closed
        // view forever (one immortal poller per open/close). RadarPanel idiom;
        // must restart on attach: drag/resize fires Detached→Attached. The hub
        // only polls the plugin while some face is subscribed.
        root.AttachedToVisualTree += (_, _) =>
        {
            UiSources.Tracker.Subscribe();
            UiSources.Tracker.RequestRefresh();
            if (!timer.IsEnabled) timer.Start();
        };
        root.DetachedFromVisualTree += (_, _) =>
        {
            timer.Stop();
            UiSources.Tracker.Unsubscribe();
        };

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
}
