// ============================================================================
//  RynthCore.Engine — UI/Panels/RynthChatFiltersPanel.cs
//  Standalone editor for RynthChat regex filter rules. Registered as its own
//  "ChatFilters" panel (dockable/floatable) — rules get complex enough that
//  a small overlay inside the chat panel doesn't cut it.
//
//  Rule semantics (shared state lives in RynthChatPanel):
//    • Regex, case-insensitive, matched against the formatted line.
//    • FIRST matching rule wins — order matters, hence the ▲▼ buttons.
//    • Tab name set  → matching lines MOVE to that tab (still shown in All).
//    • Tab name empty → matching lines are hidden everywhere.
// ============================================================================

using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using RynthCore.Engine.ImGuiBackend;

namespace RynthCore.Engine.UI.Panels;

internal static class RynthChatFiltersPanel
{
    private static readonly IBrush PanelBg     = new SolidColorBrush(Color.FromArgb(0xF2, 0x0A, 0x12, 0x1A));
    private static readonly IBrush FieldBg     = new SolidColorBrush(Color.FromArgb(0xFF, 0x0A, 0x12, 0x1A));
    private static readonly IBrush FieldBorder = new SolidColorBrush(Color.FromArgb(0xFF, 0x26, 0x40, 0x59));
    private static readonly IBrush AccentBg    = new SolidColorBrush(Color.FromArgb(0xFF, 0x26, 0x4C, 0x59));
    private static readonly IBrush ButtonBg    = new SolidColorBrush(Color.FromArgb(0xFF, 0x0F, 0x1F, 0x2E));

    internal static Control Create()
    {
        RynthChatPanel.EnsureSettingsLoaded();

        var hint = new TextBlock
        {
            Text = "Regex rules, case-insensitive — FIRST match wins (reorder with ▲▼).\n" +
                   "Tab set: matching lines move to that tab (still visible in All).\n" +
                   "Tab empty: matching lines are hidden everywhere.",
            FontSize = 9,
            Foreground = Brushes.Gray,
            Margin = new Thickness(4, 4, 4, 2),
        };

        var rows = new StackPanel { Orientation = Orientation.Vertical, Spacing = 4, Margin = new Thickness(4) };

        var addBtn = new Button
        {
            Content = "+ Add filter",
            FontSize = 10,
            Padding = new Thickness(8, 3),
            Margin = new Thickness(4, 2, 4, 6),
            Background = AccentBg,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
        };

        var layout = new DockPanel { Background = PanelBg };
        DockPanel.SetDock(hint, Dock.Top);
        DockPanel.SetDock(addBtn, Dock.Bottom);
        layout.Children.Add(hint);
        layout.Children.Add(addBtn);
        layout.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = rows,
        });

        void RebuildRows()
        {
            var filters = RynthChatPanel.Filters;
            rows.Children.Clear();
            for (int i = 0; i < filters.Count; i++)
            {
                var r = filters[i];
                int index = i;

                var enabledCheck = new CheckBox
                {
                    IsChecked = r.Enabled,
                    Margin = new Thickness(0, 0, 2, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                enabledCheck.IsCheckedChanged += (_, _) =>
                {
                    r.Enabled = enabledCheck.IsChecked == true;
                    RynthChatPanel.NotifyFiltersChanged();
                };

                var patternBox = new TextBox
                {
                    Text = r.Pattern,
                    Watermark = "regex (e.g. AutoRun)",
                    FontSize = 10,
                    MinWidth = 220,
                    Height = 24,
                    Background = FieldBg,
                    Foreground = Brushes.White,
                    BorderBrush = r.Invalid ? Brushes.IndianRed : FieldBorder,
                    BorderThickness = new Thickness(1),
                };
                ScrollViewer.SetHorizontalScrollBarVisibility(patternBox, ScrollBarVisibility.Disabled);
                ScrollViewer.SetVerticalScrollBarVisibility(patternBox, ScrollBarVisibility.Disabled);
                patternBox.GotFocus  += (_, _) => Win32Backend.AvaloniaTextInputActive = true;
                patternBox.LostFocus += (_, _) => Win32Backend.AvaloniaTextInputActive = false;
                patternBox.TextChanged += (_, _) =>
                {
                    r.Pattern = patternBox.Text ?? "";
                    r.Recompile();
                    patternBox.BorderBrush = r.Invalid ? Brushes.IndianRed : FieldBorder;
                    RynthChatPanel.NotifyFiltersChanged();
                };

                var tabBox = new TextBox
                {
                    Text = r.Tab,
                    Watermark = "tab (empty = hide)",
                    FontSize = 10,
                    Width = 120,
                    Height = 24,
                    Background = FieldBg,
                    Foreground = Brushes.White,
                    BorderBrush = FieldBorder,
                    BorderThickness = new Thickness(1),
                };
                ScrollViewer.SetHorizontalScrollBarVisibility(tabBox, ScrollBarVisibility.Disabled);
                ScrollViewer.SetVerticalScrollBarVisibility(tabBox, ScrollBarVisibility.Disabled);
                tabBox.GotFocus  += (_, _) => Win32Backend.AvaloniaTextInputActive = true;
                tabBox.LostFocus += (_, _) => Win32Backend.AvaloniaTextInputActive = false;
                tabBox.TextChanged += (_, _) =>
                {
                    r.Tab = (tabBox.Text ?? "").Trim();
                    RynthChatPanel.NotifyFiltersChanged();
                };

                Button SmallBtn(string label, IBrush fg) => new()
                {
                    Content = label,
                    FontSize = 10,
                    Padding = new Thickness(5, 2),
                    Background = ButtonBg,
                    Foreground = fg,
                    BorderThickness = new Thickness(0),
                    VerticalAlignment = VerticalAlignment.Center,
                };

                var upBtn = SmallBtn("▲", Brushes.White);
                upBtn.IsEnabled = index > 0;
                upBtn.Click += (_, _) =>
                {
                    if (index <= 0) return;
                    filters.RemoveAt(index);
                    filters.Insert(index - 1, r);
                    RebuildRows();
                    RynthChatPanel.NotifyFiltersChanged();
                };

                var downBtn = SmallBtn("▼", Brushes.White);
                downBtn.IsEnabled = index < filters.Count - 1;
                downBtn.Click += (_, _) =>
                {
                    if (index >= filters.Count - 1) return;
                    filters.RemoveAt(index);
                    filters.Insert(index + 1, r);
                    RebuildRows();
                    RynthChatPanel.NotifyFiltersChanged();
                };

                var delBtn = SmallBtn("Delete", Brushes.IndianRed);
                delBtn.Click += (_, _) =>
                {
                    filters.Remove(r);
                    RebuildRows();
                    RynthChatPanel.NotifyFiltersChanged();
                };

                rows.Children.Add(new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Children = { enabledCheck, patternBox, tabBox, upBtn, downBtn, delBtn },
                });
            }

            if (filters.Count == 0)
            {
                rows.Children.Add(new TextBlock
                {
                    Text = "No filters yet — click \"+ Add filter\".",
                    FontSize = 10,
                    Foreground = Brushes.Gray,
                    Margin = new Thickness(2, 6),
                });
            }
        }

        addBtn.Click += (_, _) =>
        {
            var rule = new RynthChatPanel.ChatFilterRule();
            rule.Recompile();
            RynthChatPanel.Filters.Add(rule);
            RebuildRows();
            // Empty pattern is inert until typed — no display change yet, but
            // persist the row so it survives a relaunch mid-edit.
            RynthChatPanel.NotifyFiltersChanged();
        };

        RebuildRows();
        return layout;
    }
}
