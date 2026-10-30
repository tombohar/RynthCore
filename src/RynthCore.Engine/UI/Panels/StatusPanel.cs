// ============================================================================
//  RynthCore.Engine - UI/Panels/StatusPanel.cs
//  Shows engine build info, game HWND, and the loaded plugin list.
//  Refreshes on demand (Refresh button) — not polled continuously.
// ============================================================================

using System;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RynthCore.Engine.Plugins;
using RynthCore.Engine.UI.Data;

namespace RynthCore.Engine.UI.Panels;

internal static class StatusPanel
{
    internal static Control Create()
    {
        var content = new StackPanel { Spacing = 4 };

        var refreshButton = new Button { Content = "Refresh", HorizontalAlignment = HorizontalAlignment.Left };
        refreshButton.Click += (_, _) =>
        {
            UiSources.PluginStatus.RequestRefresh();
            Populate(content);
        };

        var root = new DockPanel { Margin = new Avalonia.Thickness(8) };
        DockPanel.SetDock(refreshButton, Dock.Bottom);
        root.Children.Add(refreshButton);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = content
        };
        root.Children.Add(scroll);

        // Plugin rows come from UiDataHub (read on the pump thread that owns the
        // plugin list); repopulate when that snapshot changes.
        long seen = -1;
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            var snap = UiSources.PluginStatus.Current;
            if (snap == null || snap.Version == seen) return;
            seen = snap.Version;
            Populate(content);
        };
        root.AttachedToVisualTree += (_, _) =>
        {
            UiSources.PluginStatus.Subscribe();
            UiSources.PluginStatus.RequestRefresh();
            if (!timer.IsEnabled) timer.Start();
        };
        root.DetachedFromVisualTree += (_, _) =>
        {
            timer.Stop();
            UiSources.PluginStatus.Unsubscribe();
        };

        Populate(content);
        return root;
    }

    private static void Populate(StackPanel panel)
    {
        panel.Children.Clear();

        AddHeader(panel, "Engine");
        AddRow(panel, "Version", EntryPoint.BuildStamp);
        AddRow(panel, "Game HWND", $"0x{EntryPoint.GameHwnd:X8}");
        AddRow(panel, "Plugin dir", PluginManager.PluginDirectory);

        panel.Children.Add(new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            Margin = new Avalonia.Thickness(0, 6, 0, 6)
        });

        PluginStatusRow[] plugins = UiSources.PluginStatus.Current?.Value ?? Array.Empty<PluginStatusRow>();
        AddHeader(panel, $"Plugins ({plugins.Length})");

        if (plugins.Length == 0)
        {
            AddRow(panel, "", "None loaded");
        }
        else
        {
            foreach (PluginStatusRow p in plugins)
                AddRow(panel, p.Label, p.State, p.IsError);
        }

        AddSeparator(panel);
        AddHeader(panel, "UI faces");
        foreach (string line in PanelRouter.Describe())
            AddRow(panel, "", line);
    }

    private static void AddSeparator(StackPanel panel)
    {
        panel.Children.Add(new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            Margin = new Avalonia.Thickness(0, 6, 0, 6)
        });
    }

    private static void AddHeader(StackPanel panel, string text)
    {
        panel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.FromArgb(200, 140, 200, 255)),
            Margin = new Avalonia.Thickness(0, 4, 0, 2)
        });
    }

    private static void AddRow(StackPanel panel, string label, string value, bool error = false)
    {
        var color = error
            ? Color.FromArgb(220, 255, 100, 100)
            : Color.FromArgb(200, 210, 210, 210);

        string display = label.Length > 0 ? $"{label}: {value}" : value;

        panel.Children.Add(new TextBlock
        {
            Text = display,
            FontSize = 11,
            Foreground = new SolidColorBrush(color),
            TextWrapping = TextWrapping.Wrap
        });
    }
}
