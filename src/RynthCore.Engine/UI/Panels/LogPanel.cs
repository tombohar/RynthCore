// ============================================================================
//  RynthCore.Engine - UI/Panels/LogPanel.cs
//  Scrollable log viewer. Shows UiDataHub's log snapshot (UiSources.Log).
//  Auto-scroll follows the tail; checkbox to pin scroll position.
// ============================================================================

using System;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using RynthCore.Engine.UI.Data;

namespace RynthCore.Engine.UI.Panels;

internal static class LogPanel
{
    internal static Control Create()
    {
        var listBox = new ListBox
        {
            FontSize = 10,
            FontFamily = new FontFamily("Consolas,Courier New,monospace"),
            Background = Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0)
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(listBox, ScrollBarVisibility.Disabled);
        // Not virtualized: popped out, the panel is rendered from a copy parked
        // off-screen (x=2500), where a virtualizing panel realizes no rows and
        // the list showed empty. At most 256 lines, so realizing all is cheap.
        listBox.ItemsPanel = new Avalonia.Controls.Templates.FuncTemplate<Panel?>(() => new StackPanel());

        var autoScrollCheck = new CheckBox
        {
            Content = "Auto-scroll",
            IsChecked = true,
            Margin = new Avalonia.Thickness(4, 0, 0, 0)
        };

        var clearButton = new Button
        {
            Content = "Clear",
            Margin = new Avalonia.Thickness(4, 0, 0, 0)
        };

        // Clear remembers the sequence number of the newest line; later ticks
        // show only newer lines. (It used to remember a line count, which left
        // the panel blank for good once the 256-line ring was full.)
        long clearedSeq = 0;
        long seenVersion = -1;
        clearButton.Click += (_, _) =>
        {
            clearedSeq = UiSources.Log.Current?.Value.LastSeq ?? EntryPoint.RecentLogSeq;
            seenVersion = -1;
            listBox.ItemsSource = Array.Empty<string>();
        };

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { autoScrollCheck, clearButton }
        };

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = listBox
        };

        var root = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Bottom);
        root.Children.Add(toolbar);
        root.Children.Add(scroll);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            var snap = UiSources.Log.Current;
            if (snap == null || snap.Version == seenVersion) return;
            seenVersion = snap.Version;

            LogSnapshot log = snap.Value;
            int start = (int)Math.Clamp(clearedSeq - log.FirstSeq + 1, 0, log.Lines.Length);
            string[] lines = start == 0 ? log.Lines : log.Lines[start..];
            listBox.ItemsSource = lines;

            if (autoScrollCheck.IsChecked == true && lines.Length > 0)
                scroll.ScrollToEnd();
        };
        // Stop with the visual tree — a running DispatcherTimer roots the closed
        // view forever (one immortal poller per open/close). RadarPanel idiom;
        // must restart on attach: drag/resize fires Detached→Attached. The data
        // comes from UiDataHub, which polls only while a face is subscribed.
        root.AttachedToVisualTree += (_, _) =>
        {
            UiSources.Log.Subscribe();
            UiSources.Log.RequestRefresh();
            seenVersion = -1;
            if (!timer.IsEnabled) timer.Start();
        };
        root.DetachedFromVisualTree += (_, _) =>
        {
            timer.Stop();
            UiSources.Log.Unsubscribe();
        };

        return root;
    }
}
