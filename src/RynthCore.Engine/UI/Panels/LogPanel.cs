// ============================================================================
//  RynthCore.Engine - UI/Panels/LogPanel.cs
//  Scrollable log viewer. Polls EntryPoint.GetRecentLogLines() every second.
//  Auto-scroll follows the tail; checkbox to pin scroll position.
// ============================================================================

using System;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

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

        string[]? _clearedSnapshot = null;
        clearButton.Click += (_, _) =>
        {
            _clearedSnapshot = EntryPoint.GetRecentLogLines();
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
            string[] lines = EntryPoint.GetRecentLogLines();

            // If cleared, only show lines that arrived after the clear snapshot
            if (_clearedSnapshot != null)
            {
                int clearCount = _clearedSnapshot.Length;
                if (lines.Length > clearCount)
                    lines = lines[clearCount..];
                else
                    lines = Array.Empty<string>();
            }

            listBox.ItemsSource = lines;

            if (autoScrollCheck.IsChecked == true && lines.Length > 0)
                scroll.ScrollToEnd();
        };
        timer.Start();

        return root;
    }
}
