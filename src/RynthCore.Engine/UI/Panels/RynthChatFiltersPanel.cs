// ============================================================================
//  RynthCore.Engine — UI/Panels/RynthChatFiltersPanel.cs
//  Standalone editor for RynthChat regex filter rules. Registered as its own
//  "ChatFilters" panel (dockable/floatable) — rules get complex enough that
//  a small overlay inside the chat panel doesn't cut it.
//
//  Rule semantics (the rules live in ChatModel, UI/Data/ChatData.cs; the ImGui
//  face is ImGui/Panels/ChatFiltersFace.cs):
//    • Regex, or plain text Contains / Starts with / Ends with (the mode
//      button), case-insensitive.
//    • Standard filters (RynthChatPresets) below the rules apply only to
//      lines no rule moved or hid.
//    • FIRST matching rule wins — order matters, hence the ▲▼ buttons.
//    • Tab name set  → matching lines MOVE to that tab (and leave All).
//    • Tab name empty → matching lines are hidden everywhere.
//  Since 2026-09-29 rules can also copy and colour (action, when, colour);
//  only the ImGui face edits those. Editing a tab here makes the rule a plain
//  Move (or Hide when empty) again.
// ============================================================================

using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using RynthCore.Engine.ImGuiBackend;
using RynthCore.Engine.UI.Data;

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
        ChatModel.EnsureSettingsLoaded();

        var hint = new TextBlock
        {
            Text = "Regex rules, case-insensitive — FIRST match wins (reorder with ▲▼).\n" +
                   "Tab set: matching lines move to that tab (and leave All).\n" +
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
            var filters = ChatModel.Filters;
            rows.Children.Clear();
            for (int i = 0; i < filters.Length; i++)
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
                    ChatModel.FiltersChanged();
                };

                // Click-to-cycle (no ComboBox: the overlay's popup hosting is unreliable).
                var modeBtn = new Button
                {
                    Content = ModeLabels[(int)r.Mode],
                    FontSize = 10,
                    Width = 62,
                    Padding = new Thickness(4, 2),
                    Background = ButtonBg,
                    Foreground = Brushes.White,
                    BorderThickness = new Thickness(0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                ToolTip.SetTip(modeBtn, "How the text is matched: Regex, or plain text Contains / Starts with / Ends with.");

                var patternBox = new TextBox
                {
                    Text = r.Pattern,
                    Watermark = PatternWatermark(r.Mode),
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
                    ChatModel.FiltersChanged();
                };
                modeBtn.Click += (_, _) =>
                {
                    r.Mode = (ChatMatchMode)(((int)r.Mode + 1) % ModeLabels.Length);
                    r.Recompile();
                    modeBtn.Content = ModeLabels[(int)r.Mode];
                    patternBox.Watermark = PatternWatermark(r.Mode);
                    patternBox.BorderBrush = r.Invalid ? Brushes.IndianRed : FieldBorder;
                    ChatModel.FiltersChanged();
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
                    r.Action = r.Tab.Length == 0 ? ChatRuleAction.Hide : ChatRuleAction.Move;
                    ChatModel.FiltersChanged();
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
                    ChatModel.MoveFilter(r, -1);
                    RebuildRows();
                };

                var downBtn = SmallBtn("▼", Brushes.White);
                downBtn.IsEnabled = index < filters.Length - 1;
                downBtn.Click += (_, _) =>
                {
                    ChatModel.MoveFilter(r, +1);
                    RebuildRows();
                };

                var delBtn = SmallBtn("Delete", Brushes.IndianRed);
                delBtn.Click += (_, _) =>
                {
                    ChatModel.EditFilters(list => list.Remove(r));
                    RebuildRows();
                };

                rows.Children.Add(new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Children = { enabledCheck, modeBtn, patternBox, tabBox, upBtn, downBtn, delBtn },
                });
            }

            if (filters.Length == 0)
            {
                rows.Children.Add(new TextBlock
                {
                    Text = "No filters yet — click \"+ Add filter\".",
                    FontSize = 10,
                    Foreground = Brushes.Gray,
                    Margin = new Thickness(2, 6),
                });
            }

            AddStandardFilters();
        }

        // Canned UB-IT-style filters (RynthChatPresets), checked after the rules above.
        void AddStandardFilters()
        {
            rows.Children.Add(new TextBlock
            {
                Text = "Standard filters — tick to hide; type a tab to move the lines there instead.",
                FontSize = 10,
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.White,
                Margin = new Thickness(2, 10, 2, 2),
            });
            foreach (string group in RynthChatPresets.Groups)
            {
                rows.Children.Add(new TextBlock
                {
                    Text = group,
                    FontSize = 10,
                    Foreground = Brushes.LightSteelBlue,
                    Margin = new Thickness(2, 6, 2, 0),
                });
                foreach (var p in RynthChatPresets.All.Where(p => p.Group == group))
                    rows.Children.Add(PresetRow(p));
            }
        }

        Control PresetRow(RynthChatPresets.Preset p)
        {
            var check = new CheckBox
            {
                IsChecked = p.Enabled,
                Content = new TextBlock { Text = p.Label, FontSize = 10, Foreground = Brushes.White },
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 220,
            };
            ToolTip.SetTip(check, $"{p.Description}\nExample: {p.Example}");
            check.IsCheckedChanged += (_, _) =>
            {
                p.Enabled = check.IsChecked == true;
                ChatModel.FiltersChanged();
            };

            var tabBox = new TextBox
            {
                Text = p.Tab,
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
                p.Tab = (tabBox.Text ?? "").Trim();
                ChatModel.FiltersChanged();
            };

            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Margin = new Thickness(8, 0, 0, 0),
                Children = { check, tabBox },
            };
        }

        addBtn.Click += (_, _) =>
        {
            var rule = new ChatFilterRule();
            rule.Recompile();
            // Empty pattern is inert until typed — no display change yet, but
            // persist the row so it survives a relaunch mid-edit.
            ChatModel.EditFilters(list => list.Add(rule));
            RebuildRows();
        };

        // The other face (ImGui) may change the rules too.
        long seen = ChatModel.FiltersVersion;
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            long v = ChatModel.FiltersVersion;
            if (v == seen) return;
            seen = v;
            // Don't rebuild under the user's typing (a keystroke bumps the version too).
            if (Win32Backend.AvaloniaTextInputActive && layout.IsKeyboardFocusWithin) return;
            RebuildRows();
        };
        layout.AttachedToVisualTree += (_, _) => { seen = ChatModel.FiltersVersion; RebuildRows(); timer.Start(); };
        layout.DetachedFromVisualTree += (_, _) => timer.Stop();

        RebuildRows();
        return layout;
    }

    /// <summary>Button captions, indexed by <see cref="ChatMatchMode"/>.</summary>
    private static readonly string[] ModeLabels = { "Regex", "Contains", "Starts", "Ends" };

    private static string PatternWatermark(ChatMatchMode mode) => mode switch
    {
        ChatMatchMode.Contains   => "text anywhere in the line",
        ChatMatchMode.StartsWith => "line starts with...",
        ChatMatchMode.EndsWith   => "line ends with...",
        _                        => "regex (e.g. AutoRun)",
    };
}
