// ============================================================================
//  RynthCore.Engine — UI/Panels/ItemsPanel.cs
//  Avalonia replica of the ImGui Items tab (LegacyWeaponsUi.cs).
//
//  Three sections:
//    Weapons     — Name, Element picker, Delete
//    Consumables — Name, Type picker, Delete
//    Mana Stones — Enable tapping toggle, keep count, min mana threshold
//
//  Plugin exports used:
//    RynthPluginGetItemsJson        → polled every 1s for external edits
//    RynthPluginSetItemsJson        → write-back on edit
//    RynthPluginAddSelectedWeapon   → add currently selected inventory item as weapon
//    RynthPluginAddSelectedConsumable → add currently selected inventory item as consumable
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

[JsonSerializable(typeof(ItemsPanel.Payload))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = false, IncludeFields = true)]
internal partial class ItemsPanelJsonContext : JsonSerializerContext { }

internal static class ItemsPanel
{
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetItemsJsonFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetItemsJsonFn(IntPtr ansiJson);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VoidFn();

    private static GetItemsJsonFn?  _getItemsJson;
    private static SetItemsJsonFn?  _setItemsJson;
    private static VoidFn?          _addSelectedWeapon;
    private static VoidFn?          _addSelectedConsumable;

    private static readonly IBrush ColAmber   = new SolidColorBrush(Color.FromRgb(0xE8, 0xB3, 0x33));
    private static readonly IBrush ColRed     = new SolidColorBrush(Color.FromRgb(0xFF, 0x55, 0x55));
    private static readonly IBrush ColTeal    = new SolidColorBrush(Color.FromRgb(0x26, 0xD9, 0xE6));
    private static readonly IBrush ColTextDim = new SolidColorBrush(Color.FromRgb(0xD9, 0xE6, 0xF2));
    private static readonly IBrush ColMute    = new SolidColorBrush(Color.FromRgb(0x8C, 0xA6, 0xBF));
    private static readonly IBrush ColShellBg = new SolidColorBrush(Color.FromRgb(0x0A, 0x0F, 0x14));
    private static readonly IBrush ColPanelBg = new SolidColorBrush(Color.FromRgb(0x14, 0x1F, 0x29));
    private static readonly IBrush ColRowAlt  = new SolidColorBrush(Color.FromRgb(0x10, 0x18, 0x22));
    private static readonly IBrush ColBtnFill = new SolidColorBrush(Color.FromRgb(0x0F, 0x1F, 0x2E));
    private static readonly IBrush ColBtnBord = new SolidColorBrush(Color.FromRgb(0x26, 0x40, 0x59));
    private static readonly IBrush ColHdrBg  = new SolidColorBrush(Color.FromRgb(0x1A, 0x2E, 0x42));

    private static readonly string[] Elements =
        { "Slash", "Pierce", "Bludgeon", "Fire", "Cold", "Lightning", "Acid", "Nether" };

    private static readonly string[] ConsumableTypes =
        { "General", "Lockpick", "HealthKit", "ManaStone", "Stamina", "Pet" };

    internal sealed class Payload
    {
        [JsonPropertyName("weapons")]           public List<WeaponEntry>     Weapons           { get; set; } = new();
        [JsonPropertyName("consumables")]       public List<ConsumableEntry> Consumables       { get; set; } = new();
        [JsonPropertyName("enableManaTapping")] public bool                  EnableManaTapping { get; set; }
        [JsonPropertyName("manaTapMinMana")]    public int                   ManaTapMinMana    { get; set; } = 2500;
        [JsonPropertyName("manaStoneKeepCount")]public int                   ManaStoneKeepCount{ get; set; } = 5;
        [JsonPropertyName("currentTargetName")] public string                CurrentTargetName { get; set; } = string.Empty;
    }

    internal sealed class WeaponEntry
    {
        [JsonPropertyName("id")]      public int    Id      { get; set; }
        [JsonPropertyName("name")]    public string Name    { get; set; } = string.Empty;
        [JsonPropertyName("element")] public string Element { get; set; } = "Slash";
    }

    internal sealed class ConsumableEntry
    {
        [JsonPropertyName("id")]   public int    Id   { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("type")] public string Type { get; set; } = "General";
    }

    private sealed class State
    {
        public Payload Data  = new();
        public bool    Dirty;
    }

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
            Padding         = new Thickness(6),
        };

        // Root grid: content layer + picker overlay.
        var rootGrid = new Grid();
        root.Child = rootGrid;

        var rootStack = new DockPanel { LastChildFill = true };
        rootGrid.Children.Add(rootStack);

        var pickerCanvas = new Canvas
        {
            IsHitTestVisible = false,
            Background       = Brushes.Transparent,
        };
        rootGrid.Children.Add(pickerCanvas);

        Border? activePicker = null;
        Button? activeAnchor = null;

        void ClosePicker()
        {
            if (activePicker != null)
            {
                pickerCanvas.Children.Remove(activePicker);
                activePicker = null;
            }
            activeAnchor = null;
            pickerCanvas.IsHitTestVisible = false;
        }
        pickerCanvas.PointerPressed += (_, e) =>
        {
            if (activePicker == null || !ReferenceEquals(e.Source, pickerCanvas)) return;
            e.Handled = true;
            ClosePicker();
        };
        rootStack.PointerPressed += (_, _) => ClosePicker();

        void ShowPicker(Button anchor, string[] items, int selected, Action<int> onPick)
        {
            if (ReferenceEquals(anchor, activeAnchor)) { ClosePicker(); return; }
            ClosePicker();
            activeAnchor = anchor;

            var stack = new StackPanel { Spacing = 1 };
            for (int i = 0; i < items.Length; i++)
            {
                int   captured   = i;
                bool  isSel      = i == selected;
                var   entry      = new Button
                {
                    Content                    = items[i],
                    HorizontalAlignment        = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Background                 = isSel ? ColHdrBg : ColBtnFill,
                    Foreground                 = isSel ? ColTeal   : ColTextDim,
                    BorderBrush                = ColBtnBord,
                    BorderThickness            = new Thickness(1),
                    Padding                    = new Thickness(6, 2),
                    FontSize                   = 10,
                    Height                     = 20,
                };
                entry.Click += (_, _) => { onPick(captured); ClosePicker(); };
                stack.Children.Add(entry);
            }

            const double pickerWidth = 160;
            Point ap   = anchor.TranslatePoint(new Point(0, anchor.Bounds.Height), pickerCanvas) ?? new Point(8, 8);
            double left    = Math.Clamp(ap.X, 4, Math.Max(4, root.Bounds.Width - pickerWidth - 4));
            double top     = ap.Y;
            double maxH    = Math.Min(items.Length * 22 + 8, Math.Max(80, root.Bounds.Height - top - 4));

            var picker = new Border
            {
                Width           = pickerWidth,
                MaxHeight       = maxH,
                Background      = ColShellBg,
                BorderBrush     = ColTeal,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(4),
                Padding         = new Thickness(2),
                Child           = new ScrollViewer
                {
                    VerticalScrollBarVisibility   = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    Content                       = stack,
                },
            };
            pickerCanvas.Children.Add(picker);
            Canvas.SetLeft(picker, left);
            Canvas.SetTop(picker,  top);
            activePicker             = picker;
            pickerCanvas.IsHitTestVisible = true;
        }

        // ── Scrollable body ──────────────────────────────────────────────────
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility   = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Background                    = ColPanelBg,
        };
        var body = new StackPanel { Orientation = Orientation.Vertical, Spacing = 0 };
        scroll.Content = body;

        // ── Mana stone section at the bottom ─────────────────────────────────
        var manaSection = BuildManaSectionContainer(state, () => { state.Dirty = true; PushChanges(state); });

        void Refresh()
        {
            body.Children.Clear();

            // ── Weapons ──────────────────────────────────────────────────────
            body.Children.Add(BuildSectionHeader("Weapons"));

            if (state.Data.Weapons.Count == 0)
            {
                body.Children.Add(BuildEmptyRow("No weapons configured"));
            }
            else
            {
                body.Children.Add(BuildTableHeader(new[] { "Item Name", "Element", "" }, new[] { true, false, false }, new double[] { 0, 100, 46 }));
                for (int i = 0; i < state.Data.Weapons.Count; i++)
                {
                    var w = state.Data.Weapons[i];
                    int wi = i;
                    body.Children.Add(BuildWeaponRow(w, wi % 2 == 0, ShowPicker,
                        onChanged: () => { state.Dirty = true; PushChanges(state); },
                        onDelete: () =>
                        {
                            state.Data.Weapons.RemoveAt(wi);
                            state.Dirty = true;
                            PushChanges(state);
                            Refresh();
                        }));
                }
            }

            body.Children.Add(BuildAddButton("Add Selected Weapon", "(select a weapon in inventory first)",
                () =>
                {
                    if (_addSelectedWeapon == null) TryBind();
                    _addSelectedWeapon?.Invoke();
                }));

            body.Children.Add(BuildDivider());

            // ── Consumables ──────────────────────────────────────────────────
            body.Children.Add(BuildSectionHeader("Consumable Items"));

            if (state.Data.Consumables.Count == 0)
            {
                body.Children.Add(BuildEmptyRow("No consumables configured"));
            }
            else
            {
                body.Children.Add(BuildTableHeader(new[] { "Item Name", "Type", "" }, new[] { true, false, false }, new double[] { 0, 100, 46 }));
                for (int i = 0; i < state.Data.Consumables.Count; i++)
                {
                    var c = state.Data.Consumables[i];
                    int ci = i;
                    body.Children.Add(BuildConsumableRow(c, ci % 2 == 0, ShowPicker,
                        onChanged: () => { state.Dirty = true; PushChanges(state); },
                        onDelete: () =>
                        {
                            state.Data.Consumables.RemoveAt(ci);
                            state.Dirty = true;
                            PushChanges(state);
                            Refresh();
                        }));
                }
            }

            body.Children.Add(BuildAddButton("Add Selected Consumable", "(select an item in inventory first)",
                () =>
                {
                    if (_addSelectedConsumable == null) TryBind();
                    _addSelectedConsumable?.Invoke();
                }));

            body.Children.Add(BuildDivider());

            // Mana stone section is rebuilt separately so spinners capture fresh state.
            RebuildManaSection(manaSection, state, () => { state.Dirty = true; PushChanges(state); });
            body.Children.Add(manaSection);
        }

        DockPanel.SetDock(scroll, Dock.Top);
        rootStack.Children.Add(scroll);

        // ── Initial load + polling ───────────────────────────────────────────
        if (TryFetch(out var initial)) state.Data = initial;
        Refresh();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        timer.Tick += (_, _) =>
        {
            if (_getItemsJson == null) TryBind();
            if (state.Dirty) return;
            if (!TryFetch(out var fresh)) return;

            bool weaponsChanged    = WeaponsChanged(state.Data.Weapons, fresh.Weapons);
            bool consumablesChanged = ConsumablesChanged(state.Data.Consumables, fresh.Consumables);
            bool manaChanged       = state.Data.EnableManaTapping  != fresh.EnableManaTapping
                                  || state.Data.ManaTapMinMana     != fresh.ManaTapMinMana
                                  || state.Data.ManaStoneKeepCount != fresh.ManaStoneKeepCount;

            state.Data = fresh;

            if (weaponsChanged || consumablesChanged || manaChanged)
                Refresh();
        };
        timer.Start();

        return root;
    }

    // ── Section header ───────────────────────────────────────────────────────
    private static Control BuildSectionHeader(string title)
    {
        return new Border
        {
            Background      = ColHdrBg,
            Padding         = new Thickness(8, 4),
            Margin          = new Thickness(0, 4, 0, 0),
            Child           = new TextBlock
            {
                Text       = title,
                Foreground = ColAmber,
                FontSize   = 11,
                FontWeight = Avalonia.Media.FontWeight.Bold,
            },
        };
    }

    // ── Table column header ──────────────────────────────────────────────────
    private static Control BuildTableHeader(string[] labels, bool[] stretch, double[] fixedWidths)
    {
        var grid = new Grid { Background = ColHdrBg, Height = 22 };
        var cols = new ColumnDefinitions();
        for (int i = 0; i < labels.Length; i++)
            cols.Add(stretch[i] ? new ColumnDefinition(1, GridUnitType.Star) : new ColumnDefinition(fixedWidths[i], GridUnitType.Pixel));
        grid.ColumnDefinitions = cols;

        for (int i = 0; i < labels.Length; i++)
        {
            var tb = new TextBlock
            {
                Text                = labels[i],
                Foreground          = ColTextDim,
                FontSize            = 10,
                FontWeight          = Avalonia.Media.FontWeight.Bold,
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = string.IsNullOrEmpty(labels[i]) ? HorizontalAlignment.Center : HorizontalAlignment.Left,
                Margin              = new Thickness(6, 0),
            };
            Grid.SetColumn(tb, i);
            grid.Children.Add(tb);
        }
        return grid;
    }

    // ── Empty placeholder row ────────────────────────────────────────────────
    private static Control BuildEmptyRow(string msg)
    {
        return new Border
        {
            Background = ColRowAlt,
            Padding    = new Thickness(8, 4),
            Child      = new TextBlock
            {
                Text       = msg,
                Foreground = ColMute,
                FontSize   = 10,
                FontStyle  = Avalonia.Media.FontStyle.Italic,
            },
        };
    }

    // ── Divider ──────────────────────────────────────────────────────────────
    private static Control BuildDivider()
    {
        return new Border
        {
            Height     = 1,
            Background = ColBtnBord,
            Margin     = new Thickness(0, 6, 0, 0),
        };
    }

    // ── "Add Selected" button row ─────────────────────────────────────────────
    private static Control BuildAddButton(string label, string hint, Action onClick)
    {
        var btn = new Button
        {
            Content         = label,
            Background      = ColBtnFill,
            Foreground      = ColTeal,
            BorderBrush     = ColBtnBord,
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(10, 4),
            FontSize        = 10,
            Margin          = new Thickness(0, 4, 0, 0),
        };
        btn.Click += (_, _) => onClick();

        var hintTb = new TextBlock
        {
            Text              = hint,
            Foreground        = ColMute,
            FontSize          = 9,
            FontStyle         = Avalonia.Media.FontStyle.Italic,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(8, 0, 0, 0),
        };

        var dp = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(btn, Dock.Left);
        dp.Children.Add(btn);
        dp.Children.Add(hintTb);

        return new Border { Padding = new Thickness(4, 2), Child = dp };
    }

    // ── Weapon table row ──────────────────────────────────────────────────────
    private static Control BuildWeaponRow(
        WeaponEntry w, bool alt,
        Action<Button, string[], int, Action<int>> showPicker,
        Action onChanged, Action onDelete)
    {
        var grid = new Grid
        {
            Background      = alt ? ColRowAlt : ColPanelBg,
            Height          = 24,
            ColumnDefinitions = new ColumnDefinitions("*, 100, 46"),
        };

        // Name
        var nameTb = new TextBlock
        {
            Text              = w.Name,
            Foreground        = ColTextDim,
            FontSize          = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(6, 0),
            TextTrimming      = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(nameTb, 0);
        grid.Children.Add(nameTb);

        // Element picker button
        int elemIdx = Array.IndexOf(Elements, w.Element);
        if (elemIdx < 0) elemIdx = 0;
        var elemBtn = new Button
        {
            Content         = w.Element,
            Background      = ColBtnFill,
            Foreground      = ColTextDim,
            BorderBrush     = ColBtnBord,
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(4, 2),
            FontSize        = 10,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin          = new Thickness(2, 2),
        };
        elemBtn.Click += (_, _) =>
        {
            showPicker(elemBtn, Elements, Array.IndexOf(Elements, w.Element), picked =>
            {
                w.Element = Elements[picked];
                elemBtn.Content = w.Element;
                onChanged();
            });
        };
        Grid.SetColumn(elemBtn, 1);
        grid.Children.Add(elemBtn);

        // Delete button
        var del = new Button
        {
            Content         = "Del",
            Background      = ColBtnFill,
            Foreground      = ColRed,
            BorderBrush     = ColBtnBord,
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(4, 2),
            FontSize        = 10,
            Margin          = new Thickness(2, 2),
        };
        del.Click += (_, _) => onDelete();
        Grid.SetColumn(del, 2);
        grid.Children.Add(del);

        return grid;
    }

    // ── Consumable table row ──────────────────────────────────────────────────
    private static Control BuildConsumableRow(
        ConsumableEntry c, bool alt,
        Action<Button, string[], int, Action<int>> showPicker,
        Action onChanged, Action onDelete)
    {
        var grid = new Grid
        {
            Background        = alt ? ColRowAlt : ColPanelBg,
            Height            = 24,
            ColumnDefinitions = new ColumnDefinitions("*, 100, 46"),
        };

        var nameTb = new TextBlock
        {
            Text              = c.Name,
            Foreground        = ColTextDim,
            FontSize          = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(6, 0),
            TextTrimming      = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(nameTb, 0);
        grid.Children.Add(nameTb);

        int typeIdx = Array.IndexOf(ConsumableTypes, c.Type);
        if (typeIdx < 0) typeIdx = 0;
        var typeBtn = new Button
        {
            Content         = c.Type,
            Background      = ColBtnFill,
            Foreground      = ColTextDim,
            BorderBrush     = ColBtnBord,
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(4, 2),
            FontSize        = 10,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin          = new Thickness(2, 2),
        };
        typeBtn.Click += (_, _) =>
        {
            showPicker(typeBtn, ConsumableTypes, Array.IndexOf(ConsumableTypes, c.Type), picked =>
            {
                c.Type = ConsumableTypes[picked];
                typeBtn.Content = c.Type;
                onChanged();
            });
        };
        Grid.SetColumn(typeBtn, 1);
        grid.Children.Add(typeBtn);

        var del = new Button
        {
            Content         = "Del",
            Background      = ColBtnFill,
            Foreground      = ColRed,
            BorderBrush     = ColBtnBord,
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(4, 2),
            FontSize        = 10,
            Margin          = new Thickness(2, 2),
        };
        del.Click += (_, _) => onDelete();
        Grid.SetColumn(del, 2);
        grid.Children.Add(del);

        return grid;
    }

    // ── Mana stone section ────────────────────────────────────────────────────
    private static StackPanel BuildManaSectionContainer(State state, Action onChanged)
    {
        var sp = new StackPanel { Orientation = Orientation.Vertical };
        RebuildManaSection(sp, state, onChanged);
        return sp;
    }

    private static void RebuildManaSection(StackPanel sp, State state, Action onChanged)
    {
        sp.Children.Clear();

        sp.Children.Add(BuildSectionHeader("Mana Stone Tapping"));

        // Row 1: Enable toggle + Keep count
        var row1 = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 4, 4, 2) };

        var enableLbl = new TextBlock
        {
            Text              = "Enable Tapping",
            Foreground        = ColTextDim,
            FontSize          = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 6, 0),
        };
        row1.Children.Add(enableLbl);

        var enableBtn = new Button
        {
            Content         = state.Data.EnableManaTapping ? "ON" : "OFF",
            Background      = state.Data.EnableManaTapping
                                ? new SolidColorBrush(Color.FromRgb(0x10, 0x40, 0x10))
                                : ColBtnFill,
            Foreground      = state.Data.EnableManaTapping
                                ? new SolidColorBrush(Color.FromRgb(0x33, 0xFF, 0x33))
                                : ColMute,
            BorderBrush     = ColBtnBord,
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(8, 2),
            FontSize        = 10,
            Width           = 50,
            Margin          = new Thickness(0, 0, 16, 0),
        };
        enableBtn.Click += (_, _) =>
        {
            state.Data.EnableManaTapping = !state.Data.EnableManaTapping;
            onChanged();
            RebuildManaSection(sp, state, onChanged);
        };
        row1.Children.Add(enableBtn);

        row1.Children.Add(new TextBlock
        {
            Text              = "Keep count:",
            Foreground        = ColTextDim,
            FontSize          = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 6, 0),
        });
        row1.Children.Add(BuildIntSpinner(state.Data.ManaStoneKeepCount, 1, 999,
            v => { state.Data.ManaStoneKeepCount = v; onChanged(); }));

        sp.Children.Add(row1);

        // Row 2: Min mana threshold (only when tapping is on)
        if (state.Data.EnableManaTapping)
        {
            var row2 = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2, 4, 4) };
            row2.Children.Add(new TextBlock
            {
                Text              = "Min MaxMana to tap:",
                Foreground        = ColTextDim,
                FontSize          = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 6, 0),
            });
            row2.Children.Add(BuildIntSpinner(state.Data.ManaTapMinMana, 100, 99999,
                v => { state.Data.ManaTapMinMana = v; onChanged(); }));
            sp.Children.Add(row2);
        }
    }

    // ── Integer spinner (−/value/+) ───────────────────────────────────────────
    private static Control BuildIntSpinner(int value, int min, int max, Action<int> onChanged)
    {
        var lbl = new TextBlock
        {
            Text              = value.ToString(),
            Foreground        = ColTextDim,
            FontSize          = 10,
            Width             = 60,
            TextAlignment     = Avalonia.Media.TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        int cur = value;

        var decBtn = MakeSmallBtn("-", () =>
        {
            cur = Math.Max(min, cur - 1);
            lbl.Text = cur.ToString();
            onChanged(cur);
        });
        var incBtn = MakeSmallBtn("+", () =>
        {
            cur = Math.Min(max, cur + 1);
            lbl.Text = cur.ToString();
            onChanged(cur);
        });

        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        sp.Children.Add(decBtn);
        sp.Children.Add(lbl);
        sp.Children.Add(incBtn);
        return sp;
    }

    private static Button MakeSmallBtn(string label, Action onClick)
    {
        var b = new Button
        {
            Content         = label,
            Background      = ColBtnFill,
            Foreground      = ColTeal,
            BorderBrush     = ColBtnBord,
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(4, 1),
            FontSize        = 10,
            Width           = 22,
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    // ── Plugin binding ────────────────────────────────────────────────────────
    private static void TryBind()
    {
        var plugin = PluginManager.Plugins.FirstOrDefault(
            p => p.DisplayName.Contains("RynthAi", StringComparison.OrdinalIgnoreCase));
        if (plugin == null || plugin.ModuleHandle == IntPtr.Zero) return;

        if (_getItemsJson == null)
        {
            IntPtr p = GetProcAddress(plugin.ModuleHandle, "RynthPluginGetItemsJson");
            if (p != IntPtr.Zero) _getItemsJson = Marshal.GetDelegateForFunctionPointer<GetItemsJsonFn>(p);
        }
        if (_setItemsJson == null)
        {
            IntPtr p = GetProcAddress(plugin.ModuleHandle, "RynthPluginSetItemsJson");
            if (p != IntPtr.Zero) _setItemsJson = Marshal.GetDelegateForFunctionPointer<SetItemsJsonFn>(p);
        }
        if (_addSelectedWeapon == null)
        {
            IntPtr p = GetProcAddress(plugin.ModuleHandle, "RynthPluginAddSelectedWeapon");
            if (p != IntPtr.Zero) _addSelectedWeapon = Marshal.GetDelegateForFunctionPointer<VoidFn>(p);
        }
        if (_addSelectedConsumable == null)
        {
            IntPtr p = GetProcAddress(plugin.ModuleHandle, "RynthPluginAddSelectedConsumable");
            if (p != IntPtr.Zero) _addSelectedConsumable = Marshal.GetDelegateForFunctionPointer<VoidFn>(p);
        }
    }

    private static bool TryFetch(out Payload payload)
    {
        payload = new Payload();
        if (_getItemsJson == null) return false;
        try
        {
            IntPtr ptr = _getItemsJson();
            if (ptr == IntPtr.Zero) return false;
            string? json = Marshal.PtrToStringAnsi(ptr);
            if (string.IsNullOrEmpty(json)) return false;
            var parsed = JsonSerializer.Deserialize(json, ItemsPanelJsonContext.Default.Payload);
            if (parsed == null) return false;
            payload = parsed;
            return true;
        }
        catch { return false; }
    }

    private static void PushChanges(State state)
    {
        if (_setItemsJson == null) TryBind();
        if (_setItemsJson == null) return;

        IntPtr ansi = IntPtr.Zero;
        try
        {
            string json = JsonSerializer.Serialize(state.Data, ItemsPanelJsonContext.Default.Payload);
            ansi = Marshal.StringToHGlobalAnsi(json);
            _setItemsJson(ansi);
            state.Dirty = false;
        }
        catch { }
        finally
        {
            if (ansi != IntPtr.Zero) Marshal.FreeHGlobal(ansi);
        }
    }

    // ── Change detection ──────────────────────────────────────────────────────
    private static bool WeaponsChanged(List<WeaponEntry> a, List<WeaponEntry> b)
    {
        if (a.Count != b.Count) return true;
        for (int i = 0; i < a.Count; i++)
            if (a[i].Id != b[i].Id || a[i].Name != b[i].Name || a[i].Element != b[i].Element) return true;
        return false;
    }

    private static bool ConsumablesChanged(List<ConsumableEntry> a, List<ConsumableEntry> b)
    {
        if (a.Count != b.Count) return true;
        for (int i = 0; i < a.Count; i++)
            if (a[i].Id != b[i].Id || a[i].Name != b[i].Name || a[i].Type != b[i].Type) return true;
        return false;
    }
}
