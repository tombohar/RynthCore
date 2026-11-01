// ============================================================================
//  RynthCore.Engine — UI/Panels/SettingsPanel.cs
//  Avalonia replica of LegacyAdvancedSettingsUi (RynthSuite plugin).
//
//  Tabs and rows come from SettingsSchema (UI/Data/SettingsData.cs), which
//  the ImGui face draws too: add a setting there, not here. Data goes through
//  UiDataHub (UiSources.Settings, SettingsCommands.Save), so the plugin's
//  exports run on the pump thread instead of this UI thread.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RynthCore.Engine.ImGuiBackend;
using RynthCore.Engine.Plugins;
using RynthCore.Engine.UI.Data;

namespace RynthCore.Engine.UI.Panels;

internal static class SettingsPanel
{
    // ── Color palette (shared with other panels) ─────────────────────────────
    private static readonly IBrush ColTeal      = new SolidColorBrush(Color.FromRgb(0x26, 0xD9, 0xE6));
    private static readonly IBrush ColAmber     = new SolidColorBrush(Color.FromRgb(0xE8, 0xB3, 0x33));
    private static readonly IBrush ColGreen     = new SolidColorBrush(Color.FromRgb(0x33, 0xCC, 0x66));
    private static readonly IBrush ColMute      = new SolidColorBrush(Color.FromRgb(0x8C, 0xA6, 0xBF));
    private static readonly IBrush ColTextDim   = new SolidColorBrush(Color.FromRgb(0xD9, 0xE6, 0xF2));
    private static readonly IBrush ColShellBg   = new SolidColorBrush(Color.FromRgb(0x0A, 0x0F, 0x14));
    private static readonly IBrush ColPanelBg   = new SolidColorBrush(Color.FromRgb(0x14, 0x1F, 0x29));
    private static readonly IBrush ColBtnFill   = new SolidColorBrush(Color.FromRgb(0x0F, 0x1F, 0x2E));
    private static readonly IBrush ColBtnBord   = new SolidColorBrush(Color.FromRgb(0x26, 0x40, 0x59));
    private static readonly IBrush ColToggleOn  = new SolidColorBrush(Color.FromRgb(0x33, 0xFF, 0x33));
    private static readonly IBrush ColToggleOff = new SolidColorBrush(Color.FromRgb(0x33, 0x44, 0x55));
    private static readonly IBrush ColTabActive = new SolidColorBrush(Color.FromRgb(0x1A, 0x2E, 0x42));

    private sealed class PanelState
    {
        public RynthAiSettings Data = new();
        public int SelectedTab;
        // Hub snapshot version last applied (replaces the old raw-JSON gate).
        public long SeenVersion = -1;
        // Companion fix (SettingsPanel.cs:501 finding): visibility-gating
        // controls (EnableFPSLimit, UseArcs, meleeAuto, missileAuto,
        // OpenDoors, MovementMode, EnableMissileCrafting, LootJumpEnabled)
        // call this right after Push() so their dependent rows appear/
        // disappear immediately instead of waiting up to 5s for the poll —
        // required once the poll itself is gated (JSON diff + focus guard
        // above), since the poll was previously the ONLY thing that ever
        // rebuilt these rows.
        public Action? Rebuild;
    }

    // ── Picker overlay state (shared across tab renders) ─────────────────────
    private sealed class PickerState
    {
        public Canvas Canvas = null!;
        public Border? ActivePicker;
        public Button? ActiveAnchor;
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
                int captured = i;
                var entry = new Button
                {
                    Content = items[i],
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Background = i == selected ? new SolidColorBrush(Color.FromRgb(0x1A, 0x2E, 0x42)) : ColBtnFill,
                    Foreground = i == selected ? ColTeal : ColTextDim,
                    BorderBrush = ColBtnBord,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(6, 2),
                    FontSize = 10,
                    Height = 20,
                };
                entry.Click += (_, _) => { onPick(captured); Close(); };
                stack.Children.Add(entry);
            }

            const double pickerWidth = 220;
            Point ap = anchor.TranslatePoint(new Point(0, anchor.Bounds.Height), Canvas) ?? new Point(8, 8);
            double rootWidth = Root?.Bounds.Width ?? 400;
            double rootHeight = Root?.Bounds.Height ?? 300;
            double left = Math.Clamp(ap.X, 4, Math.Max(4, rootWidth - pickerWidth - 4));
            double top = ap.Y;
            double maxH = Math.Min(items.Length * 22 + 8, Math.Max(80, rootHeight - top - 4));

            var picker = new Border
            {
                Width = pickerWidth,
                MaxHeight = maxH,
                Background = ColShellBg,
                BorderBrush = ColTeal,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(2),
                Child = new ScrollViewer
                {
                    VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    Content = stack,
                },
            };
            Canvas.Children.Add(picker);
            Avalonia.Controls.Canvas.SetLeft(picker, left);
            Avalonia.Controls.Canvas.SetTop(picker, top);
            ActivePicker = picker;
            Canvas.IsHitTestVisible = true;
        }
    }

    public static Control Create()
    {
        var state = new PanelState();

        var root = new Border
        {
            Background = ColShellBg,
            BorderBrush = ColBtnBord,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
        };

        var rootGrid = new Grid();
        root.Child = rootGrid;

        var pickerCanvas = new Canvas
        {
            IsHitTestVisible = false,
            Background = Brushes.Transparent,
        };

        var picker = new PickerState { Canvas = pickerCanvas };

        // Main layout: 150px sidebar + * content area
        var mainGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("150,*"),
        };
        rootGrid.Children.Add(mainGrid);
        rootGrid.Children.Add(pickerCanvas);

        pickerCanvas.PointerPressed += (_, e) =>
        {
            if (picker.ActivePicker == null || !ReferenceEquals(e.Source, pickerCanvas)) return;
            e.Handled = true;
            picker.Close();
        };

        // ── Sidebar ───────────────────────────────────────────────────────────
        var sidebarScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Background = ColPanelBg,
        };
        Grid.SetColumn(sidebarScroll, 0);
        mainGrid.Children.Add(sidebarScroll);

        var sidebarStack = new StackPanel { Spacing = 1, Margin = new Thickness(2) };
        sidebarScroll.Content = sidebarStack;

        // ── Content area ──────────────────────────────────────────────────────
        var contentScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Background = ColShellBg,
            Padding = new Thickness(8, 6),
        };
        Grid.SetColumn(contentScroll, 1);
        mainGrid.Children.Add(contentScroll);

        var contentStack = new StackPanel { Spacing = 0 };
        contentScroll.Content = contentStack;

        // ── Tab buttons ───────────────────────────────────────────────────────
        var tabButtons = new List<Button>();
        SettingsTab[] tabs = SettingsSchema.Tabs;
        for (int i = 0; i < tabs.Length; i++)
        {
            int idx = i;
            var btn = new Button
            {
                Content = tabs[i].Name,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = ColBtnFill,
                Foreground = ColMute,
                BorderBrush = ColBtnBord,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 4),
                FontSize = 11,
                Height = 26,
                Margin = new Thickness(0, 1, 0, 0),
            };
            btn.Click += (_, _) =>
            {
                picker.Close();
                state.SelectedTab = idx;
                UpdateTabHighlight(tabButtons, idx);
                RebuildContent(state, contentStack, picker);
            };
            tabButtons.Add(btn);
            sidebarStack.Children.Add(btn);
        }

        state.Rebuild = () => RebuildContent(state, contentStack, picker);

        // ── Data: the hub's settings snapshot ─────────────────────────────────
        void ApplySnapshot()
        {
            var snap = UiSources.Settings.Current;
            if (snap == null || snap.Version == state.SeenVersion) return;
            state.SeenVersion = snap.Version;
            state.Data = snap.Value.Clone();
            RebuildContent(state, contentStack, picker);
        }

        picker.Root = root;
        UpdateTabHighlight(tabButtons, 0);
        RebuildContent(state, contentStack, picker);

        // Poll the snapshot (the hub fetches every 5 s while subscribed, and
        // right after each save). Never while a field is being typed in: that
        // used to clobber in-progress edits (UI deep-dive TL;DR #9).
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            if (Win32Backend.AvaloniaTextInputActive) return;
            ApplySnapshot();
        };
        // Stop with the visual tree — a running DispatcherTimer roots the closed
        // view forever (one immortal poller per open/close). RadarPanel idiom;
        // must restart on attach: drag/resize fires Detached→Attached.
        root.AttachedToVisualTree += (_, _) =>
        {
            UiSources.Settings.Subscribe();
            UiSources.Settings.RequestRefresh();
            if (!timer.IsEnabled) timer.Start();
        };
        root.DetachedFromVisualTree += (_, _) =>
        {
            timer.Stop();
            UiSources.Settings.Unsubscribe();
        };

        return root;
    }

    private static void UpdateTabHighlight(List<Button> buttons, int selected)
    {
        for (int i = 0; i < buttons.Count; i++)
        {
            buttons[i].Background = i == selected ? ColTabActive : ColBtnFill;
            buttons[i].Foreground = i == selected ? ColTeal : ColMute;
        }
    }

    private static void RebuildContent(PanelState state, StackPanel panel, PickerState picker)
    {
        panel.Children.Clear();

        // Tab header
        var header = new TextBlock
        {
            Text = $"Advanced Settings > {SettingsSchema.Tabs[state.SelectedTab].Name}",
            Foreground = ColTeal,
            FontSize = 11,
            FontWeight = Avalonia.Media.FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 4),
        };
        panel.Children.Add(header);
        panel.Children.Add(new Border { Height = 1, Background = ColBtnBord, Margin = new Thickness(0, 0, 0, 6) });

        foreach (SettingRow row in SettingsSchema.Tabs[state.SelectedTab].Rows)
        {
            if (!row.IsVisible(state.Data)) continue;
            Control? control = BuildRow(state, row, picker);
            if (control != null) panel.Children.Add(control);
        }
    }

    /// <summary>One schema row as the Avalonia control it has always been.</summary>
    private static Control? BuildRow(PanelState state, SettingRow row, PickerState picker)
    {
        RynthAiSettings d = state.Data;
        void Changed(double v)
        {
            row.Set!(state.Data, v);
            Push(state);
            if (row.Gates) state.Rebuild?.Invoke();
        }

        switch (row.Kind)
        {
            case SettingKind.Bool:
                return BoolRow(row.Label, row.Get!(d) != 0, v => Changed(v ? 1 : 0), row.Tooltip);
            case SettingKind.Int:
                return IntRow(row.Label, (int)row.Get!(d), (int)row.Min, (int)row.Max, (int)row.Step, v => Changed(v), row.Tooltip);
            case SettingKind.Float:
                return FloatRow(row.Label, (float)row.Get!(d), (float)row.Min, (float)row.Max, (float)row.Step, v => Changed(v), row.Tooltip);
            case SettingKind.Double:
                return DoubleRow(row.Label, row.Get!(d), row.Min, row.Max, row.Step, v => Changed(v), row.Tooltip);
            case SettingKind.Combo:
                return ComboRow(row.Label, row.CurrentItems, (int)row.Get!(d), v => Changed(v), picker, row.Tooltip);
            case SettingKind.Section:
                return SectionHeader(row.Label);
            case SettingKind.Spacer:
                return Spacer();
            case SettingKind.Button:
                // A button that asks first is the ImGui panel's only (this panel is being retired).
                if (row.Confirm != null) return null;
                return ButtonRow(row.Label, row.ButtonText ?? row.Label, () =>
                {
                    // A settings edit is pushed like a typed value; the tab is rebuilt so the
                    // number boxes show the new values.
                    if (row.Edit != null)
                    {
                        row.Edit(state.Data);
                        Push(state);
                        state.Rebuild?.Invoke();
                    }
                    row.Click?.Invoke();
                }, row.Tooltip);
            case SettingKind.TraceCategories:
                return TraceCategoriesRow(state, row);
            case SettingKind.Text:
                return TextRow(row.Label, row.GetText?.Invoke(d) ?? string.Empty, row.Hint, v =>
                {
                    row.SetText?.Invoke(state.Data, v);
                    Push(state);
                }, row.Tooltip);
            case SettingKind.Status:
            {
                string status = row.GetText?.Invoke(d) ?? string.Empty;
                return status.Length == 0 ? null : new TextBlock
                {
                    Text = status, Foreground = ColTeal, FontSize = 11,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2),
                };
            }
            case SettingKind.Note:
                return new TextBlock
                {
                    Text = row.Label, Foreground = ColMute, FontSize = row.Label.StartsWith('(') ? 11 : 10,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
                };
            case SettingKind.CraftingStatus:
            {
                var box = new StackPanel();
                var stateRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 2, 0, 2) };
                stateRow.Children.Add(new TextBlock { Text = row.Label, Foreground = ColMute, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
                stateRow.Children.Add(new TextBlock
                {
                    Text = d.MissileCraftingState,
                    Foreground = d.MissileCraftingActive ? ColAmber : ColMute,
                    FontSize = 11,
                    FontWeight = Avalonia.Media.FontWeight.Bold,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                box.Children.Add(stateRow);
                if (!string.IsNullOrEmpty(d.MissileCraftingStatus))
                    box.Children.Add(new TextBlock
                    {
                        Text = d.MissileCraftingStatus, Foreground = ColTextDim, FontSize = 10,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
                    });
                return box;
            }
        }
        return null;
    }

    // =========================================================================
    //  Control builders
    // =========================================================================

    private static Control BoolRow(string label, bool value, Action<bool> onChange, string? tooltip = null)
    {
        bool current = value;
        var dot = new Border
        {
            Width = 12, Height = 12,
            Background = current ? ColToggleOn : ColToggleOff,
            CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        var lbl = new TextBlock
        {
            Text = label,
            Foreground = ColTextDim,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0,
            Margin = new Thickness(0, 2, 0, 2),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        row.Children.Add(dot);
        row.Children.Add(lbl);

        row.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(row).Properties.IsLeftButtonPressed)
            {
                current = !current;
                dot.Background = current ? ColToggleOn : ColToggleOff;
                onChange(current);
            }
        };

        if (tooltip != null)
        {
            ToolTip.SetTip(row, tooltip);
            ToolTip.SetShowDelay(row, 400);
        }

        return row;
    }

    /// <summary>Label above a full-width text box; commits on Enter or focus loss when the text changed.</summary>
    private static Control TextRow(string label, string value, string? hint, Action<string> onChange, string? tooltip = null)
    {
        string committed = value;
        var box = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
        box.Children.Add(new TextBlock { Text = label, Foreground = ColTextDim, FontSize = 11, Margin = new Thickness(0, 0, 0, 2) });
        var tb = new TextBox
        {
            Text = value,
            Watermark = hint,
            FontSize = 11,
            Height = 22,
            Padding = new Thickness(4, 2),
            Background = ColPanelBg,
            Foreground = ColAmber,
            BorderBrush = ColBtnBord,
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        DisableInnerScroll(tb);
        void Commit()
        {
            string text = tb.Text ?? string.Empty;
            if (string.Equals(text, committed, StringComparison.Ordinal)) return;
            committed = text;
            onChange(text);
        }
        // Same keyboard gate as the number boxes: typed keys go to Avalonia, not the game.
        tb.GotFocus += (_, _) => Win32Backend.AvaloniaTextInputActive = true;
        tb.LostFocus += (_, _) =>
        {
            Win32Backend.AvaloniaTextInputActive = false;
            Commit();
        };
        tb.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) Commit();
        };
        box.Children.Add(tb);
        if (tooltip != null)
        {
            ToolTip.SetTip(box, tooltip);
            ToolTip.SetShowDelay(box, 400);
        }
        return box;
    }

    private static Control IntRow(string label, int value, int min, int max, int step, Action<int> onChange, string? tooltip = null)
    {
        int current = Math.Clamp(value, min, max);

        var minusBtn = StepButton("-");
        var plusBtn  = StepButton("+");

        // TextBox for direct entry
        var tb = new TextBox
        {
            Text = current.ToString(),
            FontSize = 11,
            Width = 52,
            Height = 20,
            Padding = new Thickness(2),
            Background = ColPanelBg,
            Foreground = ColAmber,
            BorderBrush = ColBtnBord,
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        DisableInnerScroll(tb);
        // Steppers drive the same visible TextBox the user types into.
        minusBtn.Click += (_, _) =>
        {
            current = Math.Max(min, current - step);
            tb.Text = current.ToString();
            onChange(current);
        };
        plusBtn.Click += (_, _) =>
        {
            current = Math.Min(max, current + step);
            tb.Text = current.ToString();
            onChange(current);
        };
        // Open the keyboard gate while this field has focus so typed keys are
        // routed to Avalonia instead of the game (and focus isn't yanked back).
        tb.GotFocus += (_, _) => Win32Backend.AvaloniaTextInputActive = true;
        tb.LostFocus += (_, _) =>
        {
            Win32Backend.AvaloniaTextInputActive = false;
            if (int.TryParse(tb.Text, out int parsed))
            {
                current = Math.Clamp(parsed, min, max);
                tb.Text = current.ToString();
                onChange(current);
            }
            else
            {
                tb.Text = current.ToString();
            }
        };
        tb.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                if (int.TryParse(tb.Text, out int parsed))
                {
                    current = Math.Clamp(parsed, min, max);
                    tb.Text = current.ToString();
                    onChange(current);
                }
                else tb.Text = current.ToString();
            }
        };

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,20,56,20"),
            Margin = new Thickness(0, 2, 0, 2),
            Height = 22,
        };
        var lblBlock = new TextBlock { Text = label, Foreground = ColTextDim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(lblBlock, 0);
        Grid.SetColumn(minusBtn, 1);
        Grid.SetColumn(tb,       2);
        Grid.SetColumn(plusBtn,  3);
        row.Children.Add(lblBlock);
        row.Children.Add(minusBtn);
        row.Children.Add(tb);
        row.Children.Add(plusBtn);

        if (tooltip != null)
        {
            ToolTip.SetTip(row, tooltip);
            ToolTip.SetShowDelay(row, 400);
        }
        return row;
    }

    private static Control FloatRow(string label, float value, float min, float max, float step, Action<float> onChange, string? tooltip = null)
    {
        float current = Math.Clamp(value, min, max);

        var tb = new TextBox
        {
            Text = current.ToString("G4", CultureInfo.InvariantCulture),
            FontSize = 11,
            Width = 64,
            Height = 20,
            Padding = new Thickness(2),
            Background = ColPanelBg,
            Foreground = ColAmber,
            BorderBrush = ColBtnBord,
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        DisableInnerScroll(tb);

        void Commit()
        {
            if (float.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
            {
                current = Math.Clamp(parsed, min, max);
                tb.Text = current.ToString("G4", CultureInfo.InvariantCulture);
                onChange(current);
            }
            else tb.Text = current.ToString("G4", CultureInfo.InvariantCulture);
        }

        tb.GotFocus += (_, _) => Win32Backend.AvaloniaTextInputActive = true;
        tb.LostFocus += (_, _) => { Win32Backend.AvaloniaTextInputActive = false; Commit(); };
        tb.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Commit(); };

        var minusBtn = StepButton("-");
        var plusBtn  = StepButton("+");
        minusBtn.Click += (_, _) =>
        {
            current = Math.Clamp(current - step, min, max);
            tb.Text = current.ToString("G4", CultureInfo.InvariantCulture);
            onChange(current);
        };
        plusBtn.Click += (_, _) =>
        {
            current = Math.Clamp(current + step, min, max);
            tb.Text = current.ToString("G4", CultureInfo.InvariantCulture);
            onChange(current);
        };

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,20,68,20"),
            Margin = new Thickness(0, 2, 0, 2),
            Height = 22,
        };
        var lblBlock = new TextBlock { Text = label, Foreground = ColTextDim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(lblBlock, 0);
        Grid.SetColumn(minusBtn, 1);
        Grid.SetColumn(tb,       2);
        Grid.SetColumn(plusBtn,  3);
        row.Children.Add(lblBlock);
        row.Children.Add(minusBtn);
        row.Children.Add(tb);
        row.Children.Add(plusBtn);

        if (tooltip != null)
        {
            ToolTip.SetTip(row, tooltip);
            ToolTip.SetShowDelay(row, 400);
        }
        return row;
    }

    private static Control DoubleRow(string label, double value, double min, double max, double step, Action<double> onChange, string? tooltip = null)
    {
        double current = Math.Clamp(value, min, max);

        var tb = new TextBox
        {
            Text = current.ToString("G4", CultureInfo.InvariantCulture),
            FontSize = 11,
            Width = 64,
            Height = 20,
            Padding = new Thickness(2),
            Background = ColPanelBg,
            Foreground = ColAmber,
            BorderBrush = ColBtnBord,
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        DisableInnerScroll(tb);

        void Commit()
        {
            if (double.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                current = Math.Clamp(parsed, min, max);
                tb.Text = current.ToString("G4", CultureInfo.InvariantCulture);
                onChange(current);
            }
            else tb.Text = current.ToString("G4", CultureInfo.InvariantCulture);
        }

        tb.GotFocus += (_, _) => Win32Backend.AvaloniaTextInputActive = true;
        tb.LostFocus += (_, _) => { Win32Backend.AvaloniaTextInputActive = false; Commit(); };
        tb.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Commit(); };

        var minusBtn = StepButton("-");
        var plusBtn  = StepButton("+");
        minusBtn.Click += (_, _) =>
        {
            current = Math.Clamp(current - step, min, max);
            tb.Text = current.ToString("G4", CultureInfo.InvariantCulture);
            onChange(current);
        };
        plusBtn.Click += (_, _) =>
        {
            current = Math.Clamp(current + step, min, max);
            tb.Text = current.ToString("G4", CultureInfo.InvariantCulture);
            onChange(current);
        };

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,20,68,20"),
            Margin = new Thickness(0, 2, 0, 2),
            Height = 22,
        };
        var lblBlock = new TextBlock { Text = label, Foreground = ColTextDim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(lblBlock, 0);
        Grid.SetColumn(minusBtn, 1);
        Grid.SetColumn(tb,       2);
        Grid.SetColumn(plusBtn,  3);
        row.Children.Add(lblBlock);
        row.Children.Add(minusBtn);
        row.Children.Add(tb);
        row.Children.Add(plusBtn);

        if (tooltip != null)
        {
            ToolTip.SetTip(row, tooltip);
            ToolTip.SetShowDelay(row, 400);
        }
        return row;
    }

    private static Control ComboRow(string label, string[] items, int index, Action<int> onChange, PickerState picker, string? tooltip = null)
    {
        int current = Math.Clamp(index, 0, items.Length - 1);
        var btn = new Button
        {
            Content = items[current],
            FontSize = 10,
            Height = 20,
            Padding = new Thickness(4, 1),
            Background = ColBtnFill,
            Foreground = ColTextDim,
            BorderBrush = ColBtnBord,
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        btn.Click += (_, _) =>
            picker.Show(btn, items, current, idx =>
            {
                current = idx;
                btn.Content = items[idx];
                onChange(idx);
            });

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 2, 0, 2),
            Height = 22,
        };
        var lblBlock = new TextBlock { Text = label, Foreground = ColTextDim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        btn.MinWidth = 160;
        Grid.SetColumn(lblBlock, 0);
        Grid.SetColumn(btn,      1);
        row.Children.Add(lblBlock);
        row.Children.Add(btn);

        if (tooltip != null)
        {
            ToolTip.SetTip(row, tooltip);
            ToolTip.SetShowDelay(row, 400);
        }
        return row;
    }

    /// <summary>Label on the left, a single action button on the right.</summary>
    private static Control ButtonRow(string label, string buttonText, Action onClick, string? tooltip = null)
    {
        var btn = new Button
        {
            Content = buttonText,
            FontSize = 10,
            Height = 20,
            MinWidth = 160,
            Padding = new Thickness(4, 1),
            Background = ColBtnFill,
            Foreground = ColTextDim,
            BorderBrush = ColBtnBord,
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        btn.Click += (_, _) => onClick();

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 2, 0, 2),
            Height = 22,
        };
        var lblBlock = new TextBlock { Text = label, Foreground = ColTextDim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(lblBlock, 0);
        Grid.SetColumn(btn,      1);
        row.Children.Add(lblBlock);
        row.Children.Add(btn);

        if (tooltip != null)
        {
            ToolTip.SetTip(row, tooltip);
            ToolTip.SetShowDelay(row, 400);
        }
        return row;
    }

    /// <summary>
    /// [All on] [All off] over a 3-column grid of toggles, one per RynthAi trace
    /// category. Each change edits DiagCategories, saves, and rebuilds the tab so the grid redraws.
    /// </summary>
    private static Control TraceCategoriesRow(PanelState state, SettingRow row)
    {
        var cats = TraceCategoryLevels.Parse(state.Data.DiagCategories);
        if (cats.Count == 0)
            return new TextBlock { Text = "Waiting for RynthAi...", Foreground = ColMute, FontSize = 10, Margin = new Thickness(0, 2, 0, 0) };

        void Save(string levels)
        {
            state.Data.DiagCategories = levels;
            Push(state);
            state.Rebuild?.Invoke();
        }

        Button Bulk(string text, Func<string> next)
        {
            var b = new Button
            {
                Content = text, FontSize = 10, Height = 20, Padding = new Thickness(6, 1),
                Background = ColBtnFill, Foreground = ColTextDim, BorderBrush = ColBtnBord, BorderThickness = new Thickness(1),
            };
            b.Click += (_, _) => Save(next());
            return b;
        }

        var box = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 4) };
        buttons.Children.Add(Bulk("All on", () => TraceCategoryLevels.Set(state.Data.DiagCategories, "", true)));
        buttons.Children.Add(Bulk("All off", () => TraceCategoryLevels.Set(state.Data.DiagCategories, "", false)));
        if (row.Tooltip != null)
        {
            ToolTip.SetTip(buttons, row.Tooltip);
            ToolTip.SetShowDelay(buttons, 400);
        }
        box.Children.Add(buttons);

        var grid = new Avalonia.Controls.Primitives.UniformGrid { Columns = 3 };
        foreach (var (name, level) in cats)
        {
            string catName = name;
            bool on = level != TraceCategoryLevels.Off;
            grid.Children.Add(BoolRow(TraceCategoryLevels.Label(name, level), on,
                v => Save(TraceCategoryLevels.Set(state.Data.DiagCategories, catName, v))));
        }
        box.Children.Add(grid);
        return box;
    }

    // SimpleTheme wraps TextBox content in a ScrollViewer, and its vertical
    // scrollbar's up RepeatButton is a filled triangle Path. On these short
    // (Height=20) single-line numeric fields that scrollbar shows and the
    // triangle lands on top of the digits. These fields never scroll —
    // disable both inner scrollbars so the glyph is gone.
    private static void DisableInnerScroll(Control c)
    {
        ScrollViewer.SetHorizontalScrollBarVisibility(c, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(c, ScrollBarVisibility.Disabled);
    }

    private static Button StepButton(string label) => new Button
    {
        Content = label,
        Width = 18,
        Height = 20,
        FontSize = 10,
        Padding = new Thickness(0),
        Background = ColBtnFill,
        Foreground = ColTextDim,
        BorderBrush = ColBtnBord,
        BorderThickness = new Thickness(1),
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    private static Border SectionHeader(string text) => new Border
    {
        Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x2E, 0x42)),
        Padding = new Thickness(4, 2),
        Margin = new Thickness(0, 4, 0, 2),
        Child = new TextBlock
        {
            Text = text,
            Foreground = ColAmber,
            FontSize = 11,
            FontWeight = Avalonia.Media.FontWeight.Bold,
        },
    };

    private static Border Spacer() => new Border { Height = 6 };

    // =========================================================================
    //  Save
    // =========================================================================

    private static void Push(PanelState state) => SettingsCommands.Save(state.Data.Clone());
}
