// ============================================================================
//  RynthCore.Engine — UI/Panels/MonstersPanel.cs
//  Avalonia replica of the ImGui Monsters tab (LegacyMonstersUi.cs).
//
//  Mirrors the 19-column ImGui table: F/B/G/I/Y/V/A/Bl/R/S toggles + Name +
//  Priority + Damage type + Ex Vuln + Weapon + Offhand + PetDmg + [E] + Del.
//  Categories collapsible. "Add Selected" pulls the current target name from
//  the snapshot.
//
//  Plugin exports used:
//    RynthPluginGetMonstersJson  → polled every 1s for external edits
//    RynthPluginSetMonstersJson  → debounced write-back on edit
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

[JsonSerializable(typeof(MonstersPanel.Payload))]
[JsonSerializable(typeof(List<MonstersPanel.Rule>))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = false, IncludeFields = true)]
internal partial class MonstersPanelJsonContext : JsonSerializerContext { }

internal static class MonstersPanel
{
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetMonstersJsonFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetMonstersJsonFn(IntPtr ansiJson);

    private static GetMonstersJsonFn? _getMonstersJson;
    private static SetMonstersJsonFn? _setMonstersJson;

    private static readonly IBrush ColTeal      = new SolidColorBrush(Color.FromRgb(0x26, 0xD9, 0xE6));
    private static readonly IBrush ColAmber     = new SolidColorBrush(Color.FromRgb(0xE8, 0xB3, 0x33));
    private static readonly IBrush ColGreen     = new SolidColorBrush(Color.FromRgb(0x33, 0xCC, 0x66));
    private static readonly IBrush ColMute      = new SolidColorBrush(Color.FromRgb(0x8C, 0xA6, 0xBF));
    private static readonly IBrush ColTextDim   = new SolidColorBrush(Color.FromRgb(0xD9, 0xE6, 0xF2));
    private static readonly IBrush ColShellBg   = new SolidColorBrush(Color.FromRgb(0x0A, 0x0F, 0x14));
    private static readonly IBrush ColPanelBg   = new SolidColorBrush(Color.FromRgb(0x14, 0x1F, 0x29));
    private static readonly IBrush ColRowAlt    = new SolidColorBrush(Color.FromRgb(0x10, 0x18, 0x22));
    private static readonly IBrush ColBtnFill   = new SolidColorBrush(Color.FromRgb(0x0F, 0x1F, 0x2E));
    private static readonly IBrush ColBtnBord   = new SolidColorBrush(Color.FromRgb(0x26, 0x40, 0x59));
    private static readonly IBrush ColCatHeader = new SolidColorBrush(Color.FromRgb(0x1A, 0x2E, 0x42));
    private static readonly IBrush ColToggleOn  = new SolidColorBrush(Color.FromRgb(0x33, 0xFF, 0x33));
    private static readonly IBrush ColToggleOff = new SolidColorBrush(Color.FromRgb(0x4D, 0x4D, 0x4D));

    private static readonly string[] Elements =
        { "Slash", "Pierce", "Bludgeon", "Fire", "Cold", "Lightning", "Acid", "Nether" };
    private static readonly string[] DamageTypes =
        { "Auto", "Slash", "Pierce", "Bludgeon", "Fire", "Cold", "Lightning", "Acid", "Nether" };
    private static readonly string[] ExVulnTypes =
        { "None", "Slash", "Pierce", "Bludgeon", "Fire", "Cold", "Lightning", "Acid", "Nether" };
    private static readonly string[] PetDamageTypes =
        { "PAuto", "Slash", "Pierce", "Bludgeon", "Fire", "Cold", "Lightning", "Acid", "Nether" };

    // The 10 toggle columns. (Field name, display label, tooltip)
    private static readonly (string Field, string Label, string Tip)[] Toggles =
    {
        ("Fester",      "F",  "Fester (Decrepitude's Grasp / Fester Other)"),
        ("Broadside",   "B",  "Broadside (Missile Weapons Ineptitude)"),
        ("GravityWell", "G",  "Gravity Well (Vulnerability Other)"),
        ("Imperil",     "I",  "Imperil (Gossamer Flesh / Imperil Other)"),
        ("Yield",       "Y",  "Yield (Magic Yield Other)"),
        ("Vuln",        "V",  "Vulnerability (element-matched)"),
        ("UseArc",      "A",  "Arc spells / Attack toggle"),
        ("UseBolt",     "Bl", "Bolt spells (default)"),
        ("UseRing",     "R",  "Ring spells"),
        ("UseStreak",   "S",  "Streak spells"),
    };

    internal sealed class Payload
    {
        [JsonPropertyName("rules")]             public List<Rule>     Rules     { get; set; } = new();
        [JsonPropertyName("items")]             public List<ItemRef>  Items     { get; set; } = new();
        [JsonPropertyName("currentTargetName")] public string         CurrentTargetName { get; set; } = string.Empty;
        [JsonPropertyName("captured")]          public Dictionary<string, CapturedInfo> Captured { get; set; } = new();
    }

    internal sealed class CapturedInfo
    {
        [JsonPropertyName("maxHealth")]    public uint   MaxHealth    { get; set; }
        [JsonPropertyName("armorLevel")]   public int    ArmorLevel   { get; set; }
        [JsonPropertyName("weakestType")]  public string WeakestType  { get; set; } = string.Empty;
        [JsonPropertyName("weakestValue")] public double WeakestValue { get; set; } = 1.0;
        [JsonPropertyName("samples")]      public int    Samples      { get; set; }
    }

    internal sealed class ItemRef
    {
        [JsonPropertyName("id")]   public int    Id   { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    }

    internal sealed class Rule
    {
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string MatchExpression { get; set; } = string.Empty;
        public int Priority { get; set; } = 1;
        public string DamageType { get; set; } = "Auto";
        public int WeaponId { get; set; }
        public bool Fester { get; set; }
        public bool Broadside { get; set; }
        public bool GravityWell { get; set; }
        public bool Imperil { get; set; }
        public bool Yield { get; set; }
        public bool Vuln { get; set; }
        public bool UseArc { get; set; }
        public bool UseRing { get; set; }
        public bool UseStreak { get; set; }
        public bool UseBolt { get; set; } = true;
        public string ExVuln { get; set; } = "None";
        public int OffhandId { get; set; }
        public string PetDamage { get; set; } = "PAuto";

        public bool GetToggle(string field) => field switch
        {
            "Fester"      => Fester,
            "Broadside"   => Broadside,
            "GravityWell" => GravityWell,
            "Imperil"     => Imperil,
            "Yield"       => Yield,
            "Vuln"        => Vuln,
            "UseArc"      => UseArc,
            "UseBolt"     => UseBolt,
            "UseRing"     => UseRing,
            "UseStreak"   => UseStreak,
            _             => false,
        };

        public void SetToggle(string field, bool v)
        {
            switch (field)
            {
                case "Fester":      Fester = v; break;
                case "Broadside":   Broadside = v; break;
                case "GravityWell": GravityWell = v; break;
                case "Imperil":     Imperil = v; break;
                case "Yield":       Yield = v; break;
                case "Vuln":        Vuln = v; break;
                case "UseArc":      UseArc = v; break;
                case "UseBolt":     UseBolt = v; break;
                case "UseRing":     UseRing = v; break;
                case "UseStreak":   UseStreak = v; break;
            }
        }
    }

    private sealed class State
    {
        public Payload Data = new();
        public Dictionary<string, bool> CategoryExpanded = new(StringComparer.OrdinalIgnoreCase);
        public string NewName = string.Empty;
        public string NewCategory = string.Empty;
        public bool Dirty;
        public int ExprEditIndex = -1;
    }


    public static Control Create()
    {
        TryBind();

        var state = new State();

        var root = new Border
        {
            Background = ColShellBg,
            BorderBrush = ColBtnBord,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6),
        };

        // Root grid layers content (DockPanel) + picker overlay (Canvas).
        var rootGrid = new Grid();
        root.Child = rootGrid;

        var rootStack = new DockPanel { LastChildFill = true };
        rootGrid.Children.Add(rootStack);

        var pickerCanvas = new Canvas
        {
            // Only hit-test when a picker is open so the canvas doesn't swallow
            // clicks meant for the rule rows beneath it.
            IsHitTestVisible = false,
            Background = Brushes.Transparent,
        };
        rootGrid.Children.Add(pickerCanvas);

        Border? activePicker = null;
        Button? activeAnchor = null;
        Border? tipBorder = null;

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
        rootStack.PointerPressed += (_, _) => { ClosePicker(); HideTip(); };

        void HideTip()
        {
            if (tipBorder != null)
            {
                pickerCanvas.Children.Remove(tipBorder);
                tipBorder = null;
            }
        }

        void ShowTip(Control anchor, string text)
        {
            HideTip();
            if (string.IsNullOrEmpty(text)) return;

            var tip = new Border
            {
                Background = ColShellBg,
                BorderBrush = ColTeal,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 3),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = text,
                    Foreground = ColTextDim,
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 320,
                },
            };

            // Position tip just below the anchor in pickerCanvas coordinates.
            Point p = anchor.TranslatePoint(new Point(0, anchor.Bounds.Height + 2), pickerCanvas) ?? new Point(8, 8);
            double left = Math.Clamp(p.X, 4, Math.Max(4, root.Bounds.Width - 340));
            double top = p.Y;
            pickerCanvas.Children.Add(tip);
            Canvas.SetLeft(tip, left);
            Canvas.SetTop(tip, top);
            tipBorder = tip;
        }

        void AttachTip(Control c, string text)
        {
            c.PointerEntered += (_, _) => ShowTip(c, text);
            c.PointerExited  += (_, _) => HideTip();
        }

        void ShowPicker(Button anchor, string[] items, int selected, Action<int> onPick)
        {
            if (ReferenceEquals(anchor, activeAnchor)) { ClosePicker(); return; }
            ClosePicker();
            activeAnchor = anchor;

            var stack = new StackPanel { Spacing = 1 };
            for (int i = 0; i < items.Length; i++)
            {
                int captured = i;
                bool isSelected = i == selected;
                var entry = new Button
                {
                    Content = items[i],
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Background = isSelected ? ColCatHeader : ColBtnFill,
                    Foreground = isSelected ? ColTeal : ColTextDim,
                    BorderBrush = ColBtnBord,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(6, 2),
                    FontSize = 10,
                    Height = 20,
                };
                entry.Click += (_, _) => { onPick(captured); ClosePicker(); };
                stack.Children.Add(entry);
            }

            const double pickerWidth = 180;
            Point anchorPoint = anchor.TranslatePoint(new Point(0, anchor.Bounds.Height), pickerCanvas) ?? new Point(8, 8);
            double left = Math.Clamp(anchorPoint.X, 4, Math.Max(4, root.Bounds.Width - pickerWidth - 4));
            double top = anchorPoint.Y;
            double maxHeight = Math.Min(items.Length * 22 + 8, Math.Max(80, root.Bounds.Height - top - 4));

            var picker = new Border
            {
                Width = pickerWidth,
                MaxHeight = maxHeight,
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
            pickerCanvas.Children.Add(picker);
            Canvas.SetLeft(picker, left);
            Canvas.SetTop(picker, top);
            activePicker = picker;
            pickerCanvas.IsHitTestVisible = true;
        }

        // (Wrapper chrome already shows "Monsters" + drag handle — no internal title.)

        // ── Scrollable rule list (declared first so Refresh can close over it) ─
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Background = ColPanelBg,
        };

        var listStack = new StackPanel { Orientation = Orientation.Vertical };
        scroll.Content = listStack;

        // ── Refresh: rebuild the listStack from state.Data.Rules ─────────────
        void Refresh()
        {
            listStack.Children.Clear();
            listStack.Children.Add(BuildHeaderRow());

            string? currentCat = null;
            bool currentCatOpen = true;

            for (int i = 0; i < state.Data.Rules.Count; i++)
            {
                var rule = state.Data.Rules[i];
                string cat = string.IsNullOrEmpty(rule.Category) ? "Default" : rule.Category;

                if (!string.Equals(cat, currentCat, StringComparison.OrdinalIgnoreCase))
                {
                    currentCat = cat;
                    if (!state.CategoryExpanded.ContainsKey(cat))
                        state.CategoryExpanded[cat] = true;
                    currentCatOpen = state.CategoryExpanded[cat];

                    listStack.Children.Add(BuildCategoryHeader(cat, currentCatOpen, () =>
                    {
                        state.CategoryExpanded[cat] = !state.CategoryExpanded[cat];
                        Refresh();
                    }));
                }

                if (!currentCatOpen) continue;

                int rowIndex = i;
                listStack.Children.Add(BuildRuleRow(state, rule, rowIndex, i % 2 == 0,
                    onChanged: () => { state.Dirty = true; PushChanges(state); },
                    onDelete: () =>
                    {
                        if (rule.Name.Equals("Default", StringComparison.OrdinalIgnoreCase)) return;
                        state.Data.Rules.RemoveAt(rowIndex);
                        state.Dirty = true;
                        PushChanges(state);
                        Refresh();
                    },
                    onToggleExpr: () =>
                    {
                        state.ExprEditIndex = (state.ExprEditIndex == rowIndex) ? -1 : rowIndex;
                        Refresh();
                    },
                    showPicker: ShowPicker,
                    attachTip: AttachTip));

                // Inline expression editor (only for the row currently being edited).
                if (state.ExprEditIndex == rowIndex)
                {
                    listStack.Children.Add(BuildExpressionEditor(rule,
                        onApply: () => { state.Dirty = true; state.ExprEditIndex = -1; PushChanges(state); Refresh(); },
                        onCancel: () => { state.ExprEditIndex = -1; Refresh(); }));
                }

                // Inline captured-creature stats line (only when we have a profile).
                if (state.Data.Captured != null
                    && state.Data.Captured.TryGetValue(rule.Name, out var info)
                    && info != null)
                {
                    listStack.Children.Add(BuildCapturedInfoLine(info, i % 2 == 0));
                }
            }
        }

        // ── Add row at the bottom + scrollable list (added in correct DockPanel order)
        var addRow = BuildAddRow(state, Refresh);
        DockPanel.SetDock(addRow, Dock.Bottom);
        rootStack.Children.Add(addRow);
        rootStack.Children.Add(scroll);

        // ── Initial load + polling ───────────────────────────────────────────
        if (TryFetch(out var initial)) state.Data = initial;
        Refresh();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        timer.Tick += (_, _) =>
        {
            if (_getMonstersJson == null) TryBind();
            if (state.Dirty) return; // don't clobber pending edits
            if (!TryFetch(out var fresh)) return;

            // Always refresh non-rule fields (target name, captured stats, items)
            // so "Add Selected" sees the live target and tooltips show fresh data.
            state.Data.CurrentTargetName = fresh.CurrentTargetName;
            state.Data.Items = fresh.Items;

            bool capturedChanged = !CapturedEqual(state.Data.Captured, fresh.Captured);
            state.Data.Captured = fresh.Captured;

            bool rulesChanged = fresh.Rules.Count != state.Data.Rules.Count
                || !RulesEqual(fresh.Rules, state.Data.Rules);

            if (rulesChanged || capturedChanged)
            {
                state.Data.Rules = fresh.Rules;
                Refresh();
            }
        };
        timer.Start();
        // Stop with the visual tree — a running DispatcherTimer roots the closed
        // view forever (one immortal poller per open/close). RadarPanel idiom;
        // must restart on attach: drag/resize fires Detached→Attached.
        root.AttachedToVisualTree   += (_, _) => { if (!timer.IsEnabled) timer.Start(); };
        root.DetachedFromVisualTree += (_, _) => timer.Stop();

        return root;
    }

    // ── Header row: column labels ────────────────────────────────────────────
    private static Control BuildHeaderRow()
    {
        var grid = new Grid
        {
            ColumnDefinitions = BuildColumnDefs(),
            Background = ColCatHeader,
            Height = 22,
        };
        int col = 0;
        foreach (var t in Toggles)
            AddHeaderCell(grid, col++, t.Label);
        AddHeaderCell(grid, col++, "Name");
        AddHeaderCell(grid, col++, "P");
        AddHeaderCell(grid, col++, "Dmg");
        AddHeaderCell(grid, col++, "ExVuln");
        AddHeaderCell(grid, col++, "Weapon");
        AddHeaderCell(grid, col++, "Offhand");
        AddHeaderCell(grid, col++, "PetDmg");
        AddHeaderCell(grid, col++, "E");
        AddHeaderCell(grid, col++, "X");
        return grid;
    }

    private static void AddHeaderCell(Grid grid, int col, string label)
    {
        var tb = new TextBlock
        {
            Text = label,
            Foreground = ColTextDim,
            FontSize = 10,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Grid.SetColumn(tb, col);
        grid.Children.Add(tb);
    }

    private static ColumnDefinitions BuildColumnDefs()
    {
        // 10 toggles (18 each) + Name (*) + P(40) + Dmg(80) + ExVuln(80) + Wep(140) + Off(140) + Pet(70) + E(28) + X(28)
        var widths = new List<string>();
        for (int i = 0; i < Toggles.Length; i++) widths.Add("18");
        widths.AddRange(new[] { "*", "40", "80", "80", "140", "140", "70", "28", "28" });
        return new ColumnDefinitions(string.Join(',', widths));
    }

    // ── Category header ─────────────────────────────────────────────────────
    private static Control BuildCategoryHeader(string cat, bool open, Action onToggle)
    {
        var b = new Border
        {
            Background = ColCatHeader,
            Margin = new Thickness(0, 4, 0, 0),
            Height = 22,
        };
        var dp = new DockPanel { LastChildFill = true };
        var arrow = new Button
        {
            Content = open ? "v" : ">",
            Width = 18,
            Height = 18,
            Background = Brushes.Transparent,
            Foreground = ColAmber,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            FontSize = 10,
        };
        arrow.Click += (_, _) => onToggle();
        DockPanel.SetDock(arrow, Dock.Left);
        dp.Children.Add(arrow);

        var lbl = new TextBlock
        {
            Text = cat,
            Foreground = ColAmber,
            FontSize = 11,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };
        dp.Children.Add(lbl);
        b.Child = dp;
        return b;
    }

    private delegate void ShowPickerDelegate(Button anchor, string[] items, int selected, Action<int> onPick);
    private delegate void AttachTipDelegate(Control c, string text);

    // ── Rule row: 19 columns ────────────────────────────────────────────────
    private static Control BuildRuleRow(State state, Rule rule, int index, bool altRow,
                                        Action onChanged, Action onDelete, Action onToggleExpr,
                                        ShowPickerDelegate showPicker, AttachTipDelegate attachTip)
    {
        var grid = new Grid
        {
            ColumnDefinitions = BuildColumnDefs(),
            Background = altRow ? ColRowAlt : ColPanelBg,
            Height = 26,
        };

        int col = 0;
        bool isDefault = rule.Name.Equals("Default", StringComparison.OrdinalIgnoreCase);

        // 10 toggle squares
        foreach (var t in Toggles)
        {
            string field = t.Field;
            var toggle = BuildToggle(rule.GetToggle(field), t.Tip, on =>
            {
                rule.SetToggle(field, on);
                onChanged();
            });
            attachTip(toggle, t.Tip);
            Grid.SetColumn(toggle, col++);
            grid.Children.Add(toggle);
        }

        // Name
        var nameBox = new TextBox
        {
            Text = rule.Name,
            FontSize = 11,
            Padding = new Thickness(4, 1),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = isDefault ? ColAmber : ColTextDim,
            IsReadOnly = isDefault,
            VerticalAlignment = VerticalAlignment.Center,
        };
        nameBox.LostFocus += (_, _) =>
        {
            if (rule.Name == nameBox.Text) return;
            rule.Name = nameBox.Text ?? string.Empty;
            onChanged();
        };
        Grid.SetColumn(nameBox, col++);
        grid.Children.Add(nameBox);

        // Priority (numeric textbox)
        var prioBox = new TextBox
        {
            Text = rule.Priority.ToString(),
            FontSize = 11,
            Padding = new Thickness(4, 1),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = ColTextDim,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        prioBox.LostFocus += (_, _) =>
        {
            if (int.TryParse(prioBox.Text, out int p) && p != rule.Priority)
            {
                rule.Priority = p;
                onChanged();
            }
            else
            {
                prioBox.Text = rule.Priority.ToString();
            }
        };
        Grid.SetColumn(prioBox, col++);
        grid.Children.Add(prioBox);

        // Damage type picker button
        var dmgBtn = BuildPickerButton(rule.DamageType);
        dmgBtn.Click += (_, _) =>
            showPicker(dmgBtn, DamageTypes, Array.IndexOf(DamageTypes, rule.DamageType), idx =>
            {
                rule.DamageType = DamageTypes[idx];
                SetButtonText(dmgBtn, rule.DamageType);
                onChanged();
            });
        attachTip(dmgBtn, "Damage type for this monster (Auto = use combat rules)");
        Grid.SetColumn(dmgBtn, col++);
        grid.Children.Add(dmgBtn);

        // Ex Vuln picker
        var exVulnBtn = BuildPickerButton(rule.ExVuln);
        exVulnBtn.Click += (_, _) =>
            showPicker(exVulnBtn, ExVulnTypes, Array.IndexOf(ExVulnTypes, rule.ExVuln), idx =>
            {
                rule.ExVuln = ExVulnTypes[idx];
                SetButtonText(exVulnBtn, rule.ExVuln);
                onChanged();
            });
        attachTip(exVulnBtn, "Extra vulnerability cast on this monster (None = skip)");
        Grid.SetColumn(exVulnBtn, col++);
        grid.Children.Add(exVulnBtn);

        // Weapon picker
        string[] weaponLabels = BuildItemLabels(state.Data.Items);
        int weaponIdx = FindItemIndex(state.Data.Items, rule.WeaponId);
        var weaponBtn = BuildPickerButton(weaponLabels[weaponIdx]);
        weaponBtn.Click += (_, _) =>
            showPicker(weaponBtn, weaponLabels, weaponIdx, idx =>
            {
                rule.WeaponId = idx == 0 ? 0 : state.Data.Items[idx - 1].Id;
                SetButtonText(weaponBtn, weaponLabels[idx]);
                onChanged();
            });
        attachTip(weaponBtn, "Weapon to wield against this monster (<AUTO> = let combat rules choose)");
        Grid.SetColumn(weaponBtn, col++);
        grid.Children.Add(weaponBtn);

        // Offhand picker
        int offhandIdx = FindItemIndex(state.Data.Items, rule.OffhandId);
        var offhandBtn = BuildPickerButton(weaponLabels[offhandIdx]);
        offhandBtn.Click += (_, _) =>
            showPicker(offhandBtn, weaponLabels, offhandIdx, idx =>
            {
                rule.OffhandId = idx == 0 ? 0 : state.Data.Items[idx - 1].Id;
                SetButtonText(offhandBtn, weaponLabels[idx]);
                onChanged();
            });
        attachTip(offhandBtn, "Offhand to wield against this monster");
        Grid.SetColumn(offhandBtn, col++);
        grid.Children.Add(offhandBtn);

        // PetDmg picker
        var petBtn = BuildPickerButton(rule.PetDamage);
        petBtn.Click += (_, _) =>
            showPicker(petBtn, PetDamageTypes, Array.IndexOf(PetDamageTypes, rule.PetDamage), idx =>
            {
                rule.PetDamage = PetDamageTypes[idx];
                SetButtonText(petBtn, rule.PetDamage);
                onChanged();
            });
        attachTip(petBtn, "Pet damage type (PAuto = use rules)");
        Grid.SetColumn(petBtn, col++);
        grid.Children.Add(petBtn);

        // [E] expression button
        bool hasExpr = !string.IsNullOrWhiteSpace(rule.MatchExpression);
        var exprBtn = new Button
        {
            Content = "E",
            FontSize = 10,
            Width = 24,
            Height = 22,
            Background = hasExpr ? ColGreen : ColBtnFill,
            Foreground = hasExpr ? Brushes.Black : ColMute,
            BorderBrush = ColBtnBord,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
        };
        attachTip(exprBtn, hasExpr
            ? $"Expression: {rule.MatchExpression}"
            : "Set a match expression\nVariables: name, range, typeid, maxhp, metastate\nExamples: range>5, name==drudge ravener");
        exprBtn.Click += (_, _) => onToggleExpr();
        Grid.SetColumn(exprBtn, col++);
        grid.Children.Add(exprBtn);

        // Delete button
        var delBtn = new Button
        {
            Content = isDefault ? "-" : "X",
            FontSize = 10,
            Width = 24,
            Height = 22,
            Background = ColBtnFill,
            Foreground = isDefault ? ColMute : Brushes.IndianRed,
            BorderBrush = ColBtnBord,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
            IsEnabled = !isDefault,
        };
        delBtn.Click += (_, _) => onDelete();
        if (!isDefault) attachTip(delBtn, "Delete this monster rule");
        Grid.SetColumn(delBtn, col++);
        grid.Children.Add(delBtn);

        return grid;
    }

    // ── Build a small colored-square toggle ─────────────────────────────────
    private static Control BuildToggle(bool initialOn, string tip, Action<bool> onChange)
    {
        bool state = initialOn;
        var rect = new Border
        {
            Width = 12,
            Height = 12,
            Background = state ? ColToggleOn : ColToggleOff,
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var btn = new Button
        {
            Content = rect,
            Width = 16,
            Height = 16,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // Tooltip handled by caller via AttachTip (native ToolTip.SetTip is broken in this overlay context).
        btn.Click += (_, _) =>
        {
            state = !state;
            rect.Background = state ? ColToggleOn : ColToggleOff;
            onChange(state);
        };
        return btn;
    }

    // ── Inline captured-creature info line (under a rule row) ───────────────
    private static Control BuildCapturedInfoLine(CapturedInfo info, bool altRow)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("📊 ");
        if (info.MaxHealth > 0) { sb.Append("HP "); sb.Append(info.MaxHealth); sb.Append("  "); }
        if (info.ArmorLevel > 0) { sb.Append("AL "); sb.Append(info.ArmorLevel); sb.Append("  "); }
        if (!string.IsNullOrEmpty(info.WeakestType) && info.WeakestValue < 1.0)
        {
            sb.Append("Weak: ");
            sb.Append(info.WeakestType);
            sb.Append(" (");
            sb.Append(info.WeakestValue.ToString("0.00"));
            sb.Append(")  ");
        }
        if (info.Samples > 0) { sb.Append("samples "); sb.Append(info.Samples); }

        return new Border
        {
            Background = altRow ? ColRowAlt : ColPanelBg,
            Padding = new Thickness(28, 1, 4, 2),
            Child = new TextBlock
            {
                Text = sb.ToString(),
                Foreground = ColGreen,
                FontSize = 9,
                FontStyle = FontStyle.Italic,
            },
        };
    }

    // ── Picker button (replaces ComboBox in this overlay context) ───────────
    private static Button BuildPickerButton(string label)
    {
        return new Button
        {
            Content = label,
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4, 1),
            Background = ColBtnFill,
            Foreground = ColTextDim,
            BorderBrush = ColBtnBord,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(1),
        };
    }

    private static void SetButtonText(Button btn, string s)
    {
        if (!Equals(btn.Content as string, s)) btn.Content = s;
    }

    private static string[] BuildItemLabels(List<ItemRef> items)
    {
        var labels = new string[items.Count + 1];
        labels[0] = "<AUTO>";
        for (int i = 0; i < items.Count; i++) labels[i + 1] = items[i].Name;
        return labels;
    }

    private static int FindItemIndex(List<ItemRef> items, int id)
    {
        if (id == 0) return 0;
        for (int i = 0; i < items.Count; i++)
            if (items[i].Id == id) return i + 1;
        return 0;
    }

    // ── Inline expression editor row ────────────────────────────────────────
    private static Control BuildExpressionEditor(Rule rule, Action onApply, Action onCancel)
    {
        var border = new Border
        {
            Background = ColCatHeader,
            BorderBrush = ColTeal,
            BorderThickness = new Thickness(1, 0, 1, 1),
            Padding = new Thickness(8, 6),
        };
        var stack = new StackPanel { Orientation = Orientation.Vertical, Spacing = 4 };
        border.Child = stack;

        stack.Children.Add(new TextBlock
        {
            Text = $"Expression for: {rule.Name}",
            Foreground = ColTeal,
            FontSize = 11,
            FontWeight = FontWeight.Bold,
        });
        stack.Children.Add(new TextBlock
        {
            Text = "Variables: name, range, typeid, maxhp, metastate, true, false",
            Foreground = ColMute,
            FontSize = 10,
        });

        var textBox = new TextBox
        {
            Text = rule.MatchExpression ?? string.Empty,
            FontSize = 11,
            Foreground = ColTextDim,
            Background = ColPanelBg,
            BorderBrush = ColBtnBord,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 2),
            AcceptsReturn = false,
        };
        stack.Children.Add(textBox);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var apply  = new Button { Content = "Apply",  Width = 70, Height = 22, Background = ColBtnFill, Foreground = ColTeal,  BorderBrush = ColBtnBord, FontSize = 10 };
        var clear  = new Button { Content = "Clear",  Width = 70, Height = 22, Background = ColBtnFill, Foreground = ColMute,  BorderBrush = ColBtnBord, FontSize = 10 };
        var cancel = new Button { Content = "Cancel", Width = 70, Height = 22, Background = ColBtnFill, Foreground = ColMute,  BorderBrush = ColBtnBord, FontSize = 10 };
        btnRow.Children.Add(apply);
        btnRow.Children.Add(clear);
        btnRow.Children.Add(cancel);
        stack.Children.Add(btnRow);

        apply.Click += (_, _) =>
        {
            rule.MatchExpression = (textBox.Text ?? string.Empty).Trim();
            onApply();
        };
        clear.Click += (_, _) =>
        {
            rule.MatchExpression = string.Empty;
            onApply();
        };
        cancel.Click += (_, _) => onCancel();

        return border;
    }

    // ── Add row at bottom ───────────────────────────────────────────────────
    private static Control BuildAddRow(State state, Action refreshList)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,140,80,100"),
            Margin = new Thickness(0, 6, 0, 0),
            Height = 28,
        };

        var nameBox = new TextBox
        {
            Watermark = "Monster name…",
            FontSize = 11,
            Padding = new Thickness(4, 2),
            Background = ColPanelBg,
            Foreground = ColTextDim,
            BorderBrush = ColBtnBord,
        };
        Grid.SetColumn(nameBox, 0);
        grid.Children.Add(nameBox);

        var catBox = new TextBox
        {
            Watermark = "Category…",
            FontSize = 11,
            Padding = new Thickness(4, 2),
            Background = ColPanelBg,
            Foreground = ColTextDim,
            BorderBrush = ColBtnBord,
            Margin = new Thickness(4, 0, 0, 0),
        };
        Grid.SetColumn(catBox, 1);
        grid.Children.Add(catBox);

        var addBtn = new Button
        {
            Content = "Add",
            Width = 70,
            Height = 24,
            Background = ColBtnFill,
            Foreground = ColTeal,
            BorderBrush = ColBtnBord,
            Margin = new Thickness(4, 0, 0, 0),
        };
        Grid.SetColumn(addBtn, 2);
        grid.Children.Add(addBtn);

        var addSelBtn = new Button
        {
            Content = "Add Sel",
            Width = 90,
            Height = 24,
            Background = ColBtnFill,
            Foreground = ColAmber,
            BorderBrush = ColBtnBord,
            Margin = new Thickness(4, 0, 0, 0),
        };
        Grid.SetColumn(addSelBtn, 3);
        grid.Children.Add(addSelBtn);

        void Add(string name, string category)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            if (state.Data.Rules.Any(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return;
            state.Data.Rules.Add(new Rule
            {
                Name = name.Trim(),
                Category = category?.Trim() ?? string.Empty,
                DamageType = "Auto",
                ExVuln = "None",
                PetDamage = "PAuto",
                Priority = 1,
                UseBolt = true,
            });
            state.Dirty = true;
            PushChanges(state);
            nameBox.Text = string.Empty;
            refreshList();
        }

        addBtn.Click += (_, _) => Add(nameBox.Text ?? string.Empty, catBox.Text ?? string.Empty);
        addSelBtn.Click += (_, _) =>
        {
            string targetName = state.Data.CurrentTargetName;
            if (!string.IsNullOrWhiteSpace(targetName))
                Add(targetName, catBox.Text ?? string.Empty);
        };

        return grid;
    }

    // ── Plugin export binding ───────────────────────────────────────────────
    private static void TryBind()
    {
        var plugin = PluginManager.Plugins.FirstOrDefault(
            p => p.DisplayName.Contains("RynthAi", StringComparison.OrdinalIgnoreCase));
        if (plugin == null || plugin.ModuleHandle == IntPtr.Zero) return;

        if (_getMonstersJson == null)
        {
            IntPtr p1 = GetProcAddress(plugin.ModuleHandle, "RynthPluginGetMonstersJson");
            if (p1 != IntPtr.Zero)
                _getMonstersJson = Marshal.GetDelegateForFunctionPointer<GetMonstersJsonFn>(p1);
        }

        if (_setMonstersJson == null)
        {
            IntPtr p2 = GetProcAddress(plugin.ModuleHandle, "RynthPluginSetMonstersJson");
            if (p2 != IntPtr.Zero)
                _setMonstersJson = Marshal.GetDelegateForFunctionPointer<SetMonstersJsonFn>(p2);
        }
    }

    private static bool TryFetch(out Payload payload)
    {
        payload = new Payload();
        if (_getMonstersJson == null) return false;
        try
        {
            IntPtr ptr = _getMonstersJson();
            if (ptr == IntPtr.Zero) return false;
            string? json = Marshal.PtrToStringAnsi(ptr);
            if (string.IsNullOrEmpty(json)) return false;
            var parsed = JsonSerializer.Deserialize(json, MonstersPanelJsonContext.Default.Payload);
            if (parsed == null) return false;
            payload = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void PushChanges(State state)
    {
        if (_setMonstersJson == null) TryBind();
        if (_setMonstersJson == null) return;

        IntPtr ansi = IntPtr.Zero;
        try
        {
            // Only the rules array is needed by the setter — avoids round-tripping items list.
            string json = JsonSerializer.Serialize(new Payload { Rules = state.Data.Rules }, MonstersPanelJsonContext.Default.Payload);
            ansi = Marshal.StringToHGlobalAnsi(json);
            _setMonstersJson(ansi);
            state.Dirty = false;
        }
        catch { }
        finally
        {
            if (ansi != IntPtr.Zero) Marshal.FreeHGlobal(ansi);
        }
    }

    private static bool CapturedEqual(Dictionary<string, CapturedInfo>? a, Dictionary<string, CapturedInfo>? b)
    {
        a ??= new();
        b ??= new();
        if (a.Count != b.Count) return false;
        foreach (var kv in a)
        {
            if (!b.TryGetValue(kv.Key, out var bv)) return false;
            if (kv.Value.MaxHealth != bv.MaxHealth
                || kv.Value.ArmorLevel != bv.ArmorLevel
                || kv.Value.WeakestType != bv.WeakestType
                || Math.Abs(kv.Value.WeakestValue - bv.WeakestValue) > 0.001
                || kv.Value.Samples != bv.Samples) return false;
        }
        return true;
    }

    private static bool RulesEqual(List<Rule> a, List<Rule> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            var x = a[i]; var y = b[i];
            if (x.Name != y.Name || x.Category != y.Category || x.Priority != y.Priority
                || x.DamageType != y.DamageType || x.ExVuln != y.ExVuln || x.PetDamage != y.PetDamage
                || x.WeaponId != y.WeaponId || x.OffhandId != y.OffhandId
                || x.MatchExpression != y.MatchExpression
                || x.Fester != y.Fester || x.Broadside != y.Broadside || x.GravityWell != y.GravityWell
                || x.Imperil != y.Imperil || x.Yield != y.Yield || x.Vuln != y.Vuln
                || x.UseArc != y.UseArc || x.UseBolt != y.UseBolt
                || x.UseRing != y.UseRing || x.UseStreak != y.UseStreak)
                return false;
        }
        return true;
    }
}
