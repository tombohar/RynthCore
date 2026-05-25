// =============================================================================
//  RynthCore.Engine — UI/Panels/MetaPanel.cs
//  Avalonia port of LegacyMetaUi.cs (RynthSuite plugin).
//
//  Plugin exports used:
//    RynthPluginGetMetaJson      → polled every 2s for live state
//    RynthPluginSendMetaCommand  → dispatched on user actions
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.UI.Panels;

[JsonSerializable(typeof(MetaPanel.Payload))]
[JsonSerializable(typeof(MetaPanel.MetaCmd))]
[JsonSerializable(typeof(MetaPanel.MetaRuleDto))]
[JsonSerializable(typeof(MetaPanel.MetaFile))]
[JsonSerializable(typeof(List<MetaPanel.MetaRuleDto>), TypeInfoPropertyName = "MetaRuleDtoList")]
[JsonSerializable(typeof(List<MetaPanel.MetaFile>), TypeInfoPropertyName = "MetaFileList")]
[JsonSerializable(typeof(List<string>), TypeInfoPropertyName = "StringList")]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = false, IncludeFields = false)]
internal partial class MetaPanelJsonContext : JsonSerializerContext { }

internal static class MetaPanel
{
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetMetaJsonFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SendMetaCommandFn(IntPtr ansiJson);

    private static GetMetaJsonFn?      _getMetaJson;
    private static SendMetaCommandFn?  _sendMetaCommand;

    // ── Colors ────────────────────────────────────────────────────────────────
    private static readonly IBrush ColTeal    = new SolidColorBrush(Color.FromRgb(0x26, 0xD9, 0xE6));
    private static readonly IBrush ColAmber   = new SolidColorBrush(Color.FromRgb(0xE8, 0xB3, 0x33));
    private static readonly IBrush ColGreen   = new SolidColorBrush(Color.FromRgb(0x33, 0xCC, 0x66));
    private static readonly IBrush ColRed     = new SolidColorBrush(Color.FromRgb(0xCC, 0x33, 0x33));
    private static readonly IBrush ColMute    = new SolidColorBrush(Color.FromRgb(0x8C, 0xA6, 0xBF));
    private static readonly IBrush ColTextDim = new SolidColorBrush(Color.FromRgb(0xD9, 0xE6, 0xF2));
    private static readonly IBrush ColShellBg = new SolidColorBrush(Color.FromRgb(0x0A, 0x0F, 0x14));
    private static readonly IBrush ColPanelBg = new SolidColorBrush(Color.FromRgb(0x14, 0x1F, 0x29));
    private static readonly IBrush ColRowAlt  = new SolidColorBrush(Color.FromRgb(0x10, 0x18, 0x22));
    private static readonly IBrush ColBtnFill = new SolidColorBrush(Color.FromRgb(0x0F, 0x1F, 0x2E));
    private static readonly IBrush ColBtnBord = new SolidColorBrush(Color.FromRgb(0x26, 0x40, 0x59));
    private static readonly IBrush ColFired   = new SolidColorBrush(Color.FromRgb(0x3A, 0x12, 0x12));
    private static readonly IBrush ColBtnOn      = new SolidColorBrush(Color.FromRgb(0x0E, 0x2E, 0x3A));
    private static readonly IBrush ColStateHdrA  = new SolidColorBrush(Color.FromRgb(0x0E, 0x1E, 0x30));
    private static readonly IBrush ColStateHdrB  = new SolidColorBrush(Color.FromRgb(0x16, 0x2A, 0x40));

    // ── Condition / action name tables (mirrors LegacyMetaUi) ─────────────────
    private static readonly string[] ConditionNames =
    {
        "Never", "Always", "All", "Any", "Chat Message", "Pack Slots <=",
        "Seconds in State >=", "Character Death", "Any Vendor Open",
        "Vendor Closed", "Inventory Item Count <=", "Inventory Item Count >=",
        "Monster Name Count Within Dist", "Monster Priority Count Within Dist",
        "Need To Buff", "No Monsters Within Dist", "Landblock ==",
        "Landcell ==", "Portalspace Entered", "Portalspace Exited", "Not",
        "Seconds in State (P) >=", "Time Left On Spell >=", "Time Left On Spell <=",
        "Burden % >=", "Dist Any Route PT >=", "Expression",
        "Chat Message Capture", "Navroute Empty",
        "Main Health <=", "Main Health % >=", "Main Mana <=", "Main Mana % >=",
        "Main Stam <=", "Vitae % >=",
    };

    private static readonly string[] ConditionHints =
    {
        "", "", "(sub-conditions)", "(sub-conditions)", "Regex pattern", "Min slots (e.g. 5)",
        "Seconds (e.g. 10)", "", "", "",
        "name,count (e.g. Mana Stone,5)", "name,count (e.g. Mana Stone,5)",
        "name regex,distance,count", "count,distance (e.g. 1,20)",
        "", "Distance (e.g. 20)", "Hex (e.g. A9B40000)", "Hex (e.g. A9B40000)",
        "", "", "(sub-conditions)", "Seconds (e.g. 10)",
        "spellId,seconds (e.g. 2293,30)", "spellId,seconds (e.g. 2293,30)",
        "Percentage (e.g. 250)", "Distance (e.g. 10)", "Expression",
        "Regex pattern", "",
        "", "", "", "", "", "Vitae % (e.g. 5)",
    };

    private static readonly string[] ActionNames =
    {
        "None", "Chat Command", "Set Meta State", "Embedded Nav Route", "All",
        "Call Meta State", "Return From Call", "Expression Action", "Chat Expression",
        "Set Watchdog", "Clear Watchdog", "Get RA Option", "Set RA Option",
        "Create View", "Destroy View", "Destroy All Views",
    };

    private static readonly string[] ActionHints =
    {
        "", "e.g. /say hello", "State name", "Route name", "(sub-actions)",
        "State name", "", "Expression", "Expression",
        "state;meters;seconds (e.g. Default;10;60)", "", "Option name", "OptionName;Value",
        "", "", "",
    };

    // Composite condition indices: All=2, Any=3, Not=20
    private static bool IsCompositeCondition(int idx) => idx is 2 or 3 or 20;
    // All action index = 4
    private static bool IsAllAction(int idx) => idx == 4;

    // ── Data types ────────────────────────────────────────────────────────────

    internal sealed class MetaRuleDto
    {
        public string State { get; set; } = "Default";
        public int Condition { get; set; }
        public string ConditionData { get; set; } = string.Empty;
        public int Action { get; set; }
        public string ActionData { get; set; } = string.Empty;
        public List<MetaRuleDto> Children { get; set; } = new();
        public List<MetaRuleDto> ActionChildren { get; set; } = new();
        public long LastFiredMs { get; set; } = 99999;
    }

    internal sealed class MetaFile
    {
        public string Path { get; set; } = string.Empty;
        public string Display { get; set; } = string.Empty;
    }

    internal sealed class Payload
    {
        public bool EnableMeta { get; set; }
        public bool MetaDebug { get; set; }
        public string CurrentState { get; set; } = "Default";
        public string CurrentMetaPath { get; set; } = string.Empty;
        public List<MetaRuleDto> Rules { get; set; } = new();
        public List<MetaFile> Files { get; set; } = new();
        public List<string> States { get; set; } = new();
        public List<string> NavFiles { get; set; } = new();
        public List<string> EmbeddedNavKeys { get; set; } = new();
        public string SourceText { get; set; } = string.Empty;
    }

    internal sealed class MetaCmd
    {
        public string Op { get; set; } = string.Empty;
        public int Index { get; set; } = -1;
        public string Value { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public MetaRuleDto? Rule { get; set; }
    }

    // ── View state ────────────────────────────────────────────────────────────

    private enum ViewMode { List, Editor, Source }

    private sealed class PanelState
    {
        public Payload Data = new();
        public ViewMode Mode = ViewMode.List;
        public int EditingIndex = -1;
        public MetaRuleDto EditingRule = new() { State = "Default", Action = 1 };
        public string SourceText = string.Empty;
        public string SourceMsg = string.Empty;
        public DateTime SourceMsgTime = DateTime.MinValue;
        public string SaveName = string.Empty;
        public bool ShowSaveInput;
        public HashSet<string> CollapsedStates = new();
        public HashSet<int> ExpandedRows = new();
        public string LastSig = " first";   // content signature of the last Rebuild
    }

    // Cheap content signature — everything the list view shows EXCEPT the
    // per-tick LastFiredMs (excluded so an idle/botting panel stops flashing;
    // a structural change still triggers a rebuild).
    private static string PayloadSig(Payload p)
    {
        var sb = new System.Text.StringBuilder(256);
        sb.Append(p.EnableMeta ? '1' : '0').Append(p.MetaDebug ? '1' : '0')
          .Append('|').Append(p.CurrentState).Append('|').Append(p.CurrentMetaPath)
          .Append('|').Append(p.SourceText?.Length ?? 0)
          .Append('|').Append(string.Join(",", p.Files.Select(f => f.Display)))
          .Append('|').Append(string.Join(",", p.States));
        foreach (var r in p.Rules)
            sb.Append('#').Append(r.State).Append('~').Append(r.Condition).Append('~')
              .Append(r.ConditionData).Append('~').Append(r.Action).Append('~')
              .Append(r.ActionData).Append('~').Append(r.Children.Count).Append('~')
              .Append(r.ActionChildren.Count);
        return sb.ToString();
    }

    // ── Picker overlay ────────────────────────────────────────────────────────

    private sealed class PickerState
    {
        public Canvas   Canvas      = null!;
        public Border?  ActivePicker;
        public Button?  ActiveAnchor;
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
                int ci = i;
                var entry = new Button
                {
                    Content = items[i],
                    HorizontalAlignment        = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Background      = i == selected ? new SolidColorBrush(Color.FromRgb(0x1A, 0x2E, 0x42)) : ColBtnFill,
                    Foreground      = i == selected ? ColTeal : ColTextDim,
                    BorderBrush     = ColBtnBord,
                    BorderThickness = new Thickness(1),
                    Padding         = new Thickness(6, 2),
                    FontSize        = 10,
                    Height          = 20,
                };
                entry.Click += (_, _) => { onPick(ci); Close(); };
                stack.Children.Add(entry);
            }

            const double pickerWidth = 240;
            Point ap          = anchor.TranslatePoint(new Point(0, anchor.Bounds.Height), Canvas) ?? new Point(8, 8);
            double rootWidth  = Root?.Bounds.Width  ?? 400;
            double rootHeight = Root?.Bounds.Height ?? 400;
            double left = Math.Clamp(ap.X, 4, Math.Max(4, rootWidth - pickerWidth - 4));
            double top  = ap.Y;
            double maxH = Math.Min(items.Length * 22 + 8, Math.Max(80, rootHeight - top - 4));

            var pb = new Border
            {
                Width           = pickerWidth,
                MaxHeight       = maxH,
                Background      = ColShellBg,
                BorderBrush     = ColTeal,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(4),
                Padding         = new Thickness(2),
                Child = new ScrollViewer
                {
                    VerticalScrollBarVisibility   = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    Content = stack,
                },
            };
            Canvas.Children.Add(pb);
            Avalonia.Controls.Canvas.SetLeft(pb, left);
            Avalonia.Controls.Canvas.SetTop(pb, top);
            ActivePicker = pb;
            Canvas.IsHitTestVisible = true;
        }
    }

    // =========================================================================
    //  Create
    // =========================================================================

    public static Control Create()
    {
        TryBind();
        var ps = new PanelState();

        var root = new Border
        {
            Background      = ColShellBg,
            BorderBrush     = ColBtnBord,
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4),
            MinWidth        = 380,
        };

        var rootGrid = new Grid();
        root.Child = rootGrid;

        var pickerCanvas = new Canvas { IsHitTestVisible = false, Background = Brushes.Transparent };
        var picker = new PickerState { Canvas = pickerCanvas, Root = root };

        var content = new StackPanel { Margin = new Thickness(6), Spacing = 4 };
        rootGrid.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility   = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = content,
        });
        rootGrid.Children.Add(pickerCanvas);

        pickerCanvas.PointerPressed += (_, e) =>
        {
            if (picker.ActivePicker != null && ReferenceEquals(e.Source, pickerCanvas))
            { e.Handled = true; picker.Close(); }
        };

        void Rebuild()
        {
            content.Children.Clear();
            switch (ps.Mode)
            {
                case ViewMode.List:   BuildListView(content, ps, picker, Rebuild); break;
                case ViewMode.Editor: BuildEditorView(content, ps, picker, Rebuild); break;
                case ViewMode.Source: BuildSourceView(content, ps, Rebuild); break;
            }
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            if (_getMetaJson == null) TryBind();
            if (picker.ActivePicker != null) return;
            if (ps.Mode == ViewMode.Editor) return; // don't clobber in-progress edits
            if (!TryFetch(out var fresh)) return;
            ps.Data = fresh;   // keep click-handlers on fresh data even if we skip the redraw
            if (ps.Mode == ViewMode.Source && string.IsNullOrEmpty(ps.SourceText))
                ps.SourceText = fresh.SourceText;
            string sig = PayloadSig(fresh);
            if (sig == ps.LastSig) return;   // nothing visible changed → no rebuild → no flash
            ps.LastSig = sig;
            Rebuild();
        };
        timer.Start();

        Rebuild();
        return root;
    }

    // =========================================================================
    //  List view
    // =========================================================================

    private static void BuildListView(StackPanel content, PanelState ps, PickerState picker, Action rebuild)
    {
        var d = ps.Data;

        // ── Load/Save toolbar ─────────────────────────────────────────────────
        BuildLoadSaveBar(content, ps, picker, rebuild);

        content.Children.Add(new Border { Height = 1, Background = ColBtnBord });

        // ── Mode + Enable/Debug toggles ───────────────────────────────────────
        var modeRow = new WrapPanel { Orientation = Orientation.Horizontal };

        var visualBtn = MakeToggleBtn("Visual", ps.Mode == ViewMode.List);
        visualBtn.Click += (_, _) => { ps.Mode = ViewMode.List; rebuild(); };
        modeRow.Children.Add(visualBtn);

        var sourceBtn = MakeToggleBtn("Source", false);
        sourceBtn.Click += (_, _) =>
        {
            ps.SourceText = ps.Data.SourceText;
            ps.SourceMsg  = string.Empty;
            ps.Mode       = ViewMode.Source;
            rebuild();
        };
        modeRow.Children.Add(sourceBtn);

        modeRow.Children.Add(new Border { Width = 1, Background = ColBtnBord, Margin = new Thickness(2, 2, 2, 2) });

        var enableChk = MakeCheckBtn("Meta", d.EnableMeta);
        enableChk.Click += (_, _) => Send(new MetaCmd { Op = "set_enabled", Value = (!d.EnableMeta).ToString().ToLower() });
        modeRow.Children.Add(enableChk);

        var debugChk = MakeCheckBtn("Debug", d.MetaDebug);
        debugChk.Click += (_, _) => Send(new MetaCmd { Op = "set_debug", Value = (!d.MetaDebug).ToString().ToLower() });
        modeRow.Children.Add(debugChk);

        content.Children.Add(modeRow);

        content.Children.Add(new Border { Height = 1, Background = ColBtnBord });

        // ── Rule list grouped by state ────────────────────────────────────────
        var groups = d.Rules
            .Select((r, i) => (Rule: r, GlobalIdx: i))
            .GroupBy(x => x.Rule.State)
            .OrderBy(g => g.Key)
            .ToList();

        if (groups.Count == 0)
        {
            content.Children.Add(new TextBlock
            {
                Text       = "No rules. Click New Rule to add one.",
                Foreground = ColMute,
                FontSize   = 10,
                Margin     = new Thickness(4),
            });
        }

        for (int groupIdx = 0; groupIdx < groups.Count; groupIdx++)
        {
            var group = groups[groupIdx];
            var stateRules = group.ToList();
            bool anyFiring = stateRules.Any(x => x.Rule.LastFiredMs < 1500);
            bool isCollapsed = ps.CollapsedStates.Contains(group.Key);
            string capturedKey = group.Key;
            IBrush headerBg = groupIdx % 2 == 0 ? ColStateHdrA : ColStateHdrB;

            // State group header (clickable to collapse/expand)
            var headerGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                Background = headerBg,
                Margin = new Thickness(0, 2, 0, 0),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            var collapseIndicator = new TextBlock
            {
                Text = isCollapsed ? "▶" : "▼",
                Foreground = anyFiring ? ColRed : ColAmber,
                FontSize = 10, FontWeight = FontWeight.Bold,
                Padding = new Thickness(4, 2, 2, 2),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(collapseIndicator, 0);
            headerGrid.Children.Add(collapseIndicator);

            var headerLabel = new TextBlock
            {
                Text       = anyFiring ? $"{group.Key}  ({stateRules.Count})  — firing" : $"{group.Key}  ({stateRules.Count})",
                Foreground = anyFiring ? ColRed : ColAmber,
                FontSize   = 11, FontWeight = FontWeight.Bold,
                Padding    = new Thickness(2, 2, 4, 2),
            };
            Grid.SetColumn(headerLabel, 1);
            headerGrid.Children.Add(headerLabel);

            headerGrid.PointerPressed += (_, _) =>
            {
                if (ps.CollapsedStates.Contains(capturedKey)) ps.CollapsedStates.Remove(capturedKey);
                else ps.CollapsedStates.Add(capturedKey);
                rebuild();
            };
            content.Children.Add(headerGrid);

            if (isCollapsed) continue;

            // Rows
            for (int gi = 0; gi < stateRules.Count; gi++)
            {
                var (rule, globalIdx) = stateRules[gi];
                int capturedGi = gi;
                int capturedGlobalIdx = globalIdx;
                bool firing = rule.LastFiredMs < 1500;
                bool hasChildren = (IsCompositeCondition(rule.Condition) && rule.Children.Count > 0)
                                || (IsAllAction(rule.Action) && rule.ActionChildren.Count > 0);
                bool isExpanded = ps.ExpandedRows.Contains(capturedGlobalIdx);

                var row = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("18,18,18,22,*,*"),
                    Background = firing ? ColFired : (gi % 2 == 0 ? ColPanelBg : ColRowAlt),
                    MinHeight  = 20,
                };

                // Expand button (col 0) — only when rule has children
                if (hasChildren)
                {
                    var expBtn = new Button
                    {
                        Content = isExpanded ? "−" : "+",
                        Width = 14, Height = 14,
                        Padding = new Thickness(0), Margin = new Thickness(2),
                        Background = ColBtnFill, Foreground = ColTeal,
                        BorderBrush = ColBtnBord, BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(2), FontSize = 9,
                        HorizontalContentAlignment = HorizontalAlignment.Center,
                    };
                    expBtn.Click += (_, e) =>
                    {
                        e.Handled = true;
                        if (ps.ExpandedRows.Contains(capturedGlobalIdx)) ps.ExpandedRows.Remove(capturedGlobalIdx);
                        else ps.ExpandedRows.Add(capturedGlobalIdx);
                        rebuild();
                    };
                    Grid.SetColumn(expBtn, 0);
                    row.Children.Add(expBtn);
                }

                // Up button (col 1)
                var upBtn = new Button
                {
                    Content = "^", Width = 16, Height = 16,
                    Padding = new Thickness(0), Margin = new Thickness(1),
                    Background = ColBtnFill, Foreground = ColMute,
                    BorderBrush = ColBtnBord, BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(2), FontSize = 9,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    IsEnabled = gi > 0,
                };
                upBtn.Click += (_, e) => { e.Handled = true; Send(new MetaCmd { Op = "move_up", Index = capturedGlobalIdx }); };
                Grid.SetColumn(upBtn, 1);
                row.Children.Add(upBtn);

                // Down button (col 2)
                var dnBtn = new Button
                {
                    Content = "v", Width = 16, Height = 16,
                    Padding = new Thickness(0), Margin = new Thickness(1),
                    Background = ColBtnFill, Foreground = ColMute,
                    BorderBrush = ColBtnBord, BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(2), FontSize = 9,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    IsEnabled = gi < stateRules.Count - 1,
                };
                dnBtn.Click += (_, e) => { e.Handled = true; Send(new MetaCmd { Op = "move_down", Index = capturedGlobalIdx }); };
                Grid.SetColumn(dnBtn, 2);
                row.Children.Add(dnBtn);

                // Delete button (col 3)
                var delBtn = new Button
                {
                    Content = "X", Width = 18, Height = 16,
                    Padding = new Thickness(0), Margin = new Thickness(1),
                    Background = new SolidColorBrush(Color.FromRgb(0x7A, 0x14, 0x14)),
                    Foreground = ColTextDim,
                    BorderBrush = ColBtnBord, BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(2), FontSize = 9,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                };
                delBtn.Click += (_, e) => { e.Handled = true; Send(new MetaCmd { Op = "delete_rule", Index = capturedGlobalIdx }); };
                Grid.SetColumn(delBtn, 3);
                row.Children.Add(delBtn);

                // Condition text (col 4)
                string condName = rule.Condition >= 0 && rule.Condition < ConditionNames.Length
                    ? ConditionNames[rule.Condition] : $"Cond({rule.Condition})";
                string condText;
                if (IsCompositeCondition(rule.Condition) && rule.Children.Count > 0)
                {
                    var first = rule.Children[0];
                    string fn = first.Condition >= 0 && first.Condition < ConditionNames.Length
                        ? ConditionNames[first.Condition] : $"Cond({first.Condition})";
                    string ft = string.IsNullOrEmpty(first.ConditionData) ? fn : $"{fn}: {first.ConditionData}";
                    condText = rule.Children.Count > 1
                        ? $"{condName}: {ft}  (+{rule.Children.Count - 1})"
                        : $"{condName}: {ft}";
                }
                else
                {
                    condText = string.IsNullOrEmpty(rule.ConditionData) ? condName : $"{condName}: {rule.ConditionData}";
                }
                var condLabel = new TextBlock
                {
                    Text = condText, Foreground = firing ? ColRed : ColTextDim,
                    FontSize = 10, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(3, 0, 2, 0), TextTrimming = TextTrimming.CharacterEllipsis,
                };
                Grid.SetColumn(condLabel, 4);
                row.Children.Add(condLabel);

                // Action text (col 5)
                string actName = rule.Action >= 0 && rule.Action < ActionNames.Length
                    ? ActionNames[rule.Action] : $"Act({rule.Action})";
                string actText;
                if (IsAllAction(rule.Action) && rule.ActionChildren.Count > 0)
                {
                    var first = rule.ActionChildren[0];
                    string fn = first.Action >= 0 && first.Action < ActionNames.Length
                        ? ActionNames[first.Action] : $"Act({first.Action})";
                    string ft = string.IsNullOrEmpty(first.ActionData) ? fn : $"{fn}: {first.ActionData}";
                    actText = rule.ActionChildren.Count > 1
                        ? $"{actName}: {ft}  (+{rule.ActionChildren.Count - 1})"
                        : $"{actName}: {ft}";
                }
                else
                {
                    actText = string.IsNullOrEmpty(rule.ActionData) ? actName : $"{actName}: {rule.ActionData}";
                }
                var actLabel = new TextBlock
                {
                    Text = actText, Foreground = firing ? ColAmber : ColMute,
                    FontSize = 10, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 2, 0), TextTrimming = TextTrimming.CharacterEllipsis,
                };
                Grid.SetColumn(actLabel, 5);
                row.Children.Add(actLabel);

                // Click row to edit
                row.PointerPressed += (_, e) =>
                {
                    if (e.Handled) return;
                    ps.EditingIndex = capturedGlobalIdx;
                    ps.EditingRule  = CloneRule(rule);
                    ps.Mode         = ViewMode.Editor;
                    rebuild();
                };

                content.Children.Add(row);

                // Inline child expansion
                if (hasChildren && isExpanded)
                {
                    if (IsCompositeCondition(rule.Condition) && rule.Children.Count > 0)
                    {
                        content.Children.Add(new TextBlock
                        {
                            Text = $"  {ConditionNames[rule.Condition]}:",
                            Foreground = ColAmber, FontSize = 9, FontWeight = FontWeight.SemiBold,
                            Margin = new Thickness(22, 1, 0, 0),
                        });
                        foreach (var child in rule.Children)
                        {
                            string cn = child.Condition >= 0 && child.Condition < ConditionNames.Length
                                ? ConditionNames[child.Condition] : $"Cond({child.Condition})";
                            string ct = string.IsNullOrEmpty(child.ConditionData) ? cn : $"{cn}: {child.ConditionData}";
                            content.Children.Add(new TextBlock
                            {
                                Text = $"     • {ct}", Foreground = ColMute, FontSize = 9,
                                Margin = new Thickness(22, 0, 0, 0),
                            });
                        }
                    }
                    if (IsAllAction(rule.Action) && rule.ActionChildren.Count > 0)
                    {
                        content.Children.Add(new TextBlock
                        {
                            Text = "  All (actions):",
                            Foreground = ColAmber, FontSize = 9, FontWeight = FontWeight.SemiBold,
                            Margin = new Thickness(22, 1, 0, 0),
                        });
                        foreach (var child in rule.ActionChildren)
                        {
                            string an = child.Action >= 0 && child.Action < ActionNames.Length
                                ? ActionNames[child.Action] : $"Act({child.Action})";
                            string at = string.IsNullOrEmpty(child.ActionData) ? an : $"{an}: {child.ActionData}";
                            content.Children.Add(new TextBlock
                            {
                                Text = $"     • {at}", Foreground = ColMute, FontSize = 9,
                                Margin = new Thickness(22, 0, 0, 0),
                            });
                        }
                    }
                }
            }
        }

        content.Children.Add(new Border { Height = 1, Background = ColBtnBord });

        // ── Bottom bar ────────────────────────────────────────────────────────
        BuildBottomBar(content, ps, picker, rebuild);
    }

    // =========================================================================
    //  Load/Save bar
    // =========================================================================

    private static void BuildLoadSaveBar(StackPanel content, PanelState ps, PickerState picker, Action rebuild)
    {
        var d = ps.Data;

        if (ps.ShowSaveInput)
        {
            // ── Save name input ───────────────────────────────────────────────
            var saveRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };

            var nameBox = new TextBox
            {
                Text = ps.SaveName, Watermark = "Filename (no ext)...",
                Background = ColBtnFill, Foreground = ColTextDim,
                BorderBrush = ColTeal, BorderThickness = new Thickness(1),
                FontSize = 10, Height = 22, Padding = new Thickness(4, 1),
            };
            nameBox.TextChanged += (_, _) => ps.SaveName = nameBox.Text ?? string.Empty;
            Grid.SetColumn(nameBox, 0);
            saveRow.Children.Add(nameBox);

            var doSaveBtn = MakeSmallBtn("Save");
            doSaveBtn.Click += (_, _) =>
            {
                string n = ps.SaveName.Trim();
                if (!string.IsNullOrEmpty(n))
                {
                    string path = @$"C:\Games\RynthSuite\RynthAi\MetaFiles\{n}.af";
                    Send(new MetaCmd { Op = "save_file", Path = path });
                }
                ps.ShowSaveInput = false;
                rebuild();
            };
            Grid.SetColumn(doSaveBtn, 1);
            saveRow.Children.Add(doSaveBtn);

            var cancelSaveBtn = MakeSmallBtn("✕");
            cancelSaveBtn.Click += (_, _) => { ps.ShowSaveInput = false; rebuild(); };
            Grid.SetColumn(cancelSaveBtn, 2);
            saveRow.Children.Add(cancelSaveBtn);

            content.Children.Add(saveRow);
            return;
        }

        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };

        // File picker button
        string currentDisplay = string.Empty;
        if (!string.IsNullOrEmpty(d.CurrentMetaPath) && d.Files.Count > 1)
        {
            var match = d.Files.FirstOrDefault(f => string.Equals(f.Path, d.CurrentMetaPath, StringComparison.OrdinalIgnoreCase));
            if (match != null) currentDisplay = match.Display;
        }
        var fileBtn = MakePickerBtn(string.IsNullOrEmpty(currentDisplay) ? "-- None --" : currentDisplay);
        fileBtn.Click += (_, _) =>
        {
            var items = ps.Data.Files.Select(f => f.Display).ToArray();
            int sel   = string.IsNullOrEmpty(ps.Data.CurrentMetaPath) ? 0
                : ps.Data.Files.FindIndex(f => string.Equals(f.Path, ps.Data.CurrentMetaPath, StringComparison.OrdinalIgnoreCase));
            picker.Show(fileBtn, items, sel, idx =>
            {
                if (idx < ps.Data.Files.Count)
                    Send(new MetaCmd { Op = "load_file", Path = ps.Data.Files[idx].Path });
            });
        };
        Grid.SetColumn(fileBtn, 0);
        bar.Children.Add(fileBtn);

        var refreshBtn = MakeSmallBtn("↺");
        ToolTip.SetTip(refreshBtn, "Refresh file list");
        refreshBtn.Click += (_, _) =>
        {
            if (TryFetch(out var fresh)) { ps.Data = fresh; rebuild(); }
        };
        Grid.SetColumn(refreshBtn, 1);
        bar.Children.Add(refreshBtn);

        var saveBtn = MakeSmallBtn("Save");
        saveBtn.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(ps.Data.CurrentMetaPath))
                Send(new MetaCmd { Op = "save_file", Path = ps.Data.CurrentMetaPath });
            else
            { ps.ShowSaveInput = true; rebuild(); }
        };
        Grid.SetColumn(saveBtn, 2);
        bar.Children.Add(saveBtn);

        var saveAsBtn = MakeSmallBtn("Save As");
        saveAsBtn.Click += (_, _) => { ps.ShowSaveInput = true; rebuild(); };
        Grid.SetColumn(saveAsBtn, 3);
        bar.Children.Add(saveAsBtn);

        content.Children.Add(bar);
    }

    // =========================================================================
    //  Bottom bar (state + new rule)
    // =========================================================================

    private static void BuildBottomBar(StackPanel content, PanelState ps, PickerState picker, Action rebuild)
    {
        var d = ps.Data;
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        var stateLabel = new TextBlock
        {
            Text = "State:", Foreground = ColMute, FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0),
        };
        Grid.SetColumn(stateLabel, 0);
        bar.Children.Add(stateLabel);

        var stateBtn = MakePickerBtn(d.CurrentState);
        stateBtn.Click += (_, _) =>
        {
            var allStates = ps.Data.States.ToList();
            if (!allStates.Contains("Default")) allStates.Insert(0, "Default");
            int sel = allStates.IndexOf(ps.Data.CurrentState);
            picker.Show(stateBtn, allStates.ToArray(), sel, idx =>
                Send(new MetaCmd { Op = "set_state", Value = allStates[idx] }));
        };
        Grid.SetColumn(stateBtn, 1);
        bar.Children.Add(stateBtn);

        var newRuleBtn = new Button
        {
            Content = "New Rule",
            Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x2E, 0x1A)),
            Foreground = ColTextDim, BorderBrush = ColGreen,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
            Padding = new Thickness(8, 3), FontSize = 10, Height = 24,
            Margin = new Thickness(4, 0, 0, 0),
        };
        newRuleBtn.Click += (_, _) =>
        {
            ps.EditingIndex = -1;
            ps.EditingRule  = new MetaRuleDto { State = d.CurrentState, Action = 1 };
            ps.Mode         = ViewMode.Editor;
            rebuild();
        };
        Grid.SetColumn(newRuleBtn, 2);
        bar.Children.Add(newRuleBtn);

        content.Children.Add(bar);
    }

    // =========================================================================
    //  Rule editor view
    // =========================================================================

    private static void BuildEditorView(StackPanel content, PanelState ps, PickerState picker, Action rebuild)
    {
        var r = ps.EditingRule;
        var d = ps.Data;

        // Back button
        var backBtn = new Button
        {
            Content = "← Back to List",
            Background = ColBtnFill, Foreground = ColMute,
            BorderBrush = ColBtnBord, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3), Padding = new Thickness(8, 3),
            FontSize = 10, HorizontalAlignment = HorizontalAlignment.Left,
        };
        backBtn.Click += (_, _) => { ps.Mode = ViewMode.List; rebuild(); };
        content.Children.Add(backBtn);

        content.Children.Add(new Border { Height = 1, Background = ColBtnBord });
        content.Children.Add(new TextBlock
        {
            Text       = ps.EditingIndex == -1 ? "New Rule" : $"Edit Rule (index {ps.EditingIndex})",
            Foreground = ColTeal, FontSize = 11, FontWeight = FontWeight.Bold,
        });

        // ── State ─────────────────────────────────────────────────────────────
        content.Children.Add(MakeFieldLabel("State:"));
        var stateBtn = MakePickerBtn(r.State);
        stateBtn.Click += (_, _) =>
        {
            var states = ps.Data.States.ToList();
            if (!states.Contains("Default")) states.Insert(0, "Default");
            int sel = states.IndexOf(r.State);
            picker.Show(stateBtn, states.ToArray(), sel, idx => { r.State = states[idx]; stateBtn.Content = r.State; });
        };
        content.Children.Add(stateBtn);

        // ── Condition ─────────────────────────────────────────────────────────
        content.Children.Add(MakeFieldLabel("Condition:"));
        var condBtn = MakePickerBtn(r.Condition < ConditionNames.Length ? ConditionNames[r.Condition] : $"Cond({r.Condition})");
        condBtn.Click += (_, _) =>
        {
            picker.Show(condBtn, ConditionNames, r.Condition, idx =>
            {
                r.Condition = idx;
                condBtn.Content = ConditionNames[idx];
                if (!IsCompositeCondition(idx)) r.Children.Clear();
                rebuild(); // rebuild to show/hide sub-condition section
            });
        };
        content.Children.Add(condBtn);

        if (!IsCompositeCondition(r.Condition))
        {
            string hint = r.Condition < ConditionHints.Length ? ConditionHints[r.Condition] : "";
            var condData = MakeDataBox(r.ConditionData, hint);
            condData.TextChanged += (_, _) => r.ConditionData = condData.Text ?? string.Empty;
            content.Children.Add(condData);
        }
        else
        {
            BuildSubRuleSection(content, ps, picker, rebuild, r.Children, "Sub-Conditions", isAction: false);
        }

        // ── Action ────────────────────────────────────────────────────────────
        content.Children.Add(new Border { Height = 1, Background = ColBtnBord, Margin = new Thickness(0, 4, 0, 0) });
        content.Children.Add(MakeFieldLabel("Action:"));
        var actBtn = MakePickerBtn(r.Action < ActionNames.Length ? ActionNames[r.Action] : $"Act({r.Action})");
        actBtn.Click += (_, _) =>
        {
            picker.Show(actBtn, ActionNames, r.Action, idx =>
            {
                r.Action = idx;
                actBtn.Content = ActionNames[idx];
                if (!IsAllAction(idx)) r.ActionChildren.Clear();
                rebuild();
            });
        };
        content.Children.Add(actBtn);

        if (!IsAllAction(r.Action))
        {
            string hint = r.Action < ActionHints.Length ? ActionHints[r.Action] : "";

            if (r.Action == 2 || r.Action == 5) // Set/Call Meta State
            {
                var stateCombo = MakePickerBtn(string.IsNullOrEmpty(r.ActionData) ? "Select state..." : r.ActionData);
                stateCombo.Click += (_, _) =>
                {
                    var states = ps.Data.States.ToList();
                    int sel = states.IndexOf(r.ActionData);
                    picker.Show(stateCombo, states.ToArray(), sel, idx => { r.ActionData = states[idx]; stateCombo.Content = r.ActionData; });
                };
                content.Children.Add(stateCombo);
            }
            else if (r.Action == 3) // Embedded Nav Route
            {
                var allRoutes = ps.Data.NavFiles.Where(n => n != "None").ToList();
                foreach (var k in ps.Data.EmbeddedNavKeys) if (!allRoutes.Contains(k)) allRoutes.Add($"[emb] {k}");
                var routeCombo = MakePickerBtn(string.IsNullOrEmpty(r.ActionData) ? "Select route..." : r.ActionData);
                routeCombo.Click += (_, _) =>
                {
                    int sel = allRoutes.IndexOf(r.ActionData);
                    picker.Show(routeCombo, allRoutes.ToArray(), sel, idx => { r.ActionData = allRoutes[idx]; routeCombo.Content = r.ActionData; });
                };
                content.Children.Add(routeCombo);
            }
            else
            {
                var actData = MakeDataBox(r.ActionData, hint);
                actData.TextChanged += (_, _) => r.ActionData = actData.Text ?? string.Empty;
                content.Children.Add(actData);
            }
        }
        else
        {
            BuildSubRuleSection(content, ps, picker, rebuild, r.ActionChildren, "Sub-Actions", isAction: true);
        }

        content.Children.Add(new Border { Height = 1, Background = ColBtnBord, Margin = new Thickness(0, 4, 0, 0) });

        // ── Save / Cancel ─────────────────────────────────────────────────────
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

        var saveRuleBtn = new Button
        {
            Content = ps.EditingIndex == -1 ? "Add Rule" : "Save Rule",
            Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x2E, 0x1A)),
            Foreground = ColTextDim, BorderBrush = ColGreen,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
            Padding = new Thickness(12, 4), FontSize = 10,
        };
        saveRuleBtn.Click += (_, _) =>
        {
            if (ps.EditingIndex == -1)
                Send(new MetaCmd { Op = "add_rule", Rule = CloneRule(r) });
            else
                Send(new MetaCmd { Op = "update_rule", Index = ps.EditingIndex, Rule = CloneRule(r) });
            ps.Mode = ViewMode.List;
            rebuild();
        };
        btnRow.Children.Add(saveRuleBtn);

        var cancelBtn = new Button
        {
            Content = "Cancel", Background = ColBtnFill, Foreground = ColMute,
            BorderBrush = ColBtnBord, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3), Padding = new Thickness(12, 4), FontSize = 10,
        };
        cancelBtn.Click += (_, _) => { ps.Mode = ViewMode.List; rebuild(); };
        btnRow.Children.Add(cancelBtn);

        content.Children.Add(btnRow);
    }

    private static void BuildSubRuleSection(
        StackPanel content, PanelState ps, PickerState picker, Action rebuild,
        List<MetaRuleDto> subRules, string label, bool isAction)
    {
        content.Children.Add(new TextBlock
        {
            Text = label, Foreground = ColAmber, FontSize = 10,
            FontWeight = FontWeight.Bold, Margin = new Thickness(0, 4, 0, 2),
        });

        for (int si = 0; si < subRules.Count; si++)
        {
            int ci = si;
            var sub = subRules[si];
            string[] names = isAction ? ActionNames : ConditionNames;
            string[] hints = isAction ? ActionHints : ConditionHints;

            var subRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,Auto"), Margin = new Thickness(0, 1, 0, 0) };

            var typeBtn = MakePickerBtn(sub.Condition < names.Length ? names[isAction ? sub.Action : sub.Condition] : "?");
            typeBtn.FontSize = 9;
            typeBtn.Height = 20;
            typeBtn.Click += (_, _) =>
            {
                picker.Show(typeBtn, names, isAction ? sub.Action : sub.Condition, idx =>
                {
                    if (isAction) sub.Action = idx; else sub.Condition = idx;
                    typeBtn.Content = names[idx];
                });
            };
            Grid.SetColumn(typeBtn, 0);
            subRow.Children.Add(typeBtn);

            var dataBox = new TextBox
            {
                Text = isAction ? sub.ActionData : sub.ConditionData,
                Watermark = "data...",
                Background = ColBtnFill, Foreground = ColTextDim,
                BorderBrush = ColBtnBord, BorderThickness = new Thickness(1),
                FontSize = 9, Height = 20, Padding = new Thickness(3, 1),
            };
            dataBox.TextChanged += (_, _) => { if (isAction) sub.ActionData = dataBox.Text ?? ""; else sub.ConditionData = dataBox.Text ?? ""; };
            Grid.SetColumn(dataBox, 1);
            subRow.Children.Add(dataBox);

            var rmBtn = new Button
            {
                Content = "X", Width = 20, Height = 20,
                Padding = new Thickness(0), Margin = new Thickness(2, 0, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(0x7A, 0x14, 0x14)),
                Foreground = ColTextDim, BorderBrush = ColBtnBord,
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2),
                FontSize = 9, HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            rmBtn.Click += (_, e) => { e.Handled = true; subRules.RemoveAt(ci); rebuild(); };
            Grid.SetColumn(rmBtn, 2);
            subRow.Children.Add(rmBtn);

            content.Children.Add(subRow);
        }

        var addSubBtn = new Button
        {
            Content = $"+ Add",
            Background = ColBtnFill, Foreground = ColTeal,
            BorderBrush = ColBtnBord, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 2),
            FontSize = 9, Height = 20, HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 2, 0, 0),
        };
        addSubBtn.Click += (_, _) =>
        {
            subRules.Add(new MetaRuleDto { State = ps.EditingRule.State });
            rebuild();
        };
        content.Children.Add(addSubBtn);
    }

    // =========================================================================
    //  Source view
    // =========================================================================

    private static void BuildSourceView(StackPanel content, PanelState ps, Action rebuild)
    {
        BuildLoadSaveBarSource(content, ps, rebuild);

        content.Children.Add(new Border { Height = 1, Background = ColBtnBord });

        var modeRow = new WrapPanel { Orientation = Orientation.Horizontal };
        modeRow.Children.Add(MakeToggleBtn("Visual", false));
        ((Button)modeRow.Children[0]).Click += (_, _) => { ps.Mode = ViewMode.List; rebuild(); };
        modeRow.Children.Add(MakeToggleBtn("Source", true));
        content.Children.Add(modeRow);

        content.Children.Add(new Border { Height = 1, Background = ColBtnBord });

        if (!string.IsNullOrEmpty(ps.SourceMsg) && (DateTime.Now - ps.SourceMsgTime).TotalSeconds < 5)
        {
            content.Children.Add(new TextBlock
            {
                Text = ps.SourceMsg, Foreground = ColGreen, FontSize = 10,
            });
        }

        var sourceEditor = MetaSourceEditor.Create(
            getText:        () => ps.SourceText,
            onTextChanged:  t  => ps.SourceText = t ?? string.Empty,
            getStates:      () => ps.Data.States,
            getNavs:        () => CombineNavs(ps.Data.NavFiles, ps.Data.EmbeddedNavKeys));
        content.Children.Add(sourceEditor);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };

        var applyBtn = new Button
        {
            Content = "Apply",
            Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x2E, 0x1A)),
            Foreground = ColTextDim, BorderBrush = ColGreen,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
            Padding = new Thickness(12, 4), FontSize = 10,
        };
        applyBtn.Click += (_, _) =>
        {
            Send(new MetaCmd { Op = "set_source", Text = ps.SourceText });
            ps.SourceMsg  = "Applied.";
            ps.SourceMsgTime = DateTime.Now;
            rebuild();
        };
        btnRow.Children.Add(applyBtn);

        var revertBtn = new Button
        {
            Content = "Revert", Background = ColBtnFill, Foreground = ColMute,
            BorderBrush = ColBtnBord, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3), Padding = new Thickness(12, 4), FontSize = 10,
        };
        revertBtn.Click += (_, _) => { ps.SourceText = ps.Data.SourceText; rebuild(); };
        btnRow.Children.Add(revertBtn);

        content.Children.Add(btnRow);
    }

    private static void BuildLoadSaveBarSource(StackPanel content, PanelState ps, Action rebuild)
    {
        var d = ps.Data;
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto") };

        string name = string.IsNullOrEmpty(d.CurrentMetaPath)
            ? "Unsaved"
            : System.IO.Path.GetFileName(d.CurrentMetaPath);
        bar.Children.Add(new TextBlock
        {
            Text = $"File: {name}", Foreground = ColAmber, FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
        });

        var saveBtn = MakeSmallBtn("Save");
        Grid.SetColumn(saveBtn, 1);
        saveBtn.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(d.CurrentMetaPath))
                Send(new MetaCmd { Op = "save_file", Path = d.CurrentMetaPath });
        };
        bar.Children.Add(saveBtn);

        content.Children.Add(bar);
    }

    // =========================================================================
    //  Helpers
    // =========================================================================

    private static Button MakePickerBtn(string text) => new Button
    {
        Content = text,
        HorizontalAlignment        = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Left,
        Background = ColBtnFill, Foreground = ColTextDim,
        BorderBrush = ColBtnBord, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 2),
        FontSize = 10, Height = 22,
    };

    private static Button MakeSmallBtn(string text) => new Button
    {
        Content = text,
        Background = ColBtnFill, Foreground = ColTextDim,
        BorderBrush = ColBtnBord, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 2),
        FontSize = 10, Height = 22, Margin = new Thickness(2, 0, 0, 0),
    };

    private static Button MakeToggleBtn(string text, bool on) => new Button
    {
        Content = text,
        Background = on ? ColBtnOn : ColBtnFill,
        Foreground = on ? ColTeal : ColMute,
        BorderBrush = on ? ColTeal : ColBtnBord, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(3), Padding = new Thickness(8, 2),
        FontSize = 10, Height = 22, Margin = new Thickness(0, 0, 4, 0),
    };

    private static Button MakeCheckBtn(string text, bool on) => new Button
    {
        Content = (on ? "☑ " : "☐ ") + text,
        Background = on ? ColBtnOn : ColBtnFill,
        Foreground = on ? ColTeal : ColMute,
        BorderBrush = on ? ColTeal : ColBtnBord, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 2),
        FontSize = 10, Height = 22, Margin = new Thickness(0, 0, 4, 0),
    };

    private static TextBlock MakeFieldLabel(string text) => new TextBlock
    {
        Text = text, Foreground = ColMute, FontSize = 10,
        Margin = new Thickness(0, 4, 0, 1),
    };

    private static TextBox MakeDataBox(string value, string hint) => new TextBox
    {
        Text = value, Watermark = hint,
        Background = ColBtnFill, Foreground = ColTextDim,
        BorderBrush = ColBtnBord, BorderThickness = new Thickness(1),
        FontSize = 10, Height = 22, Padding = new Thickness(4, 1),
    };

    private static IReadOnlyList<string> CombineNavs(List<string> nav1, List<string> nav2)
    {
        if ((nav1?.Count ?? 0) == 0) return nav2 ?? (IReadOnlyList<string>)Array.Empty<string>();
        if ((nav2?.Count ?? 0) == 0) return nav1!;
        var combined = new List<string>(nav1!.Count + nav2!.Count);
        combined.AddRange(nav1);
        foreach (var n in nav2) if (!combined.Contains(n)) combined.Add(n);
        return combined;
    }

    private static MetaRuleDto CloneRule(MetaRuleDto src)
    {
        var r = new MetaRuleDto
        {
            State = src.State, Condition = src.Condition,
            ConditionData = src.ConditionData, Action = src.Action,
            ActionData = src.ActionData, LastFiredMs = src.LastFiredMs,
        };
        foreach (var c in src.Children) r.Children.Add(CloneRule(c));
        foreach (var a in src.ActionChildren) r.ActionChildren.Add(CloneRule(a));
        return r;
    }

    // =========================================================================
    //  Plugin bridge
    // =========================================================================

    private static void TryBind()
    {
        var plugin = PluginManager.Plugins.FirstOrDefault(
            p => p.DisplayName.Contains("RynthAi", StringComparison.OrdinalIgnoreCase));
        if (plugin == null || plugin.ModuleHandle == IntPtr.Zero) return;

        if (_getMetaJson == null)
        {
            IntPtr p1 = GetProcAddress(plugin.ModuleHandle, "RynthPluginGetMetaJson");
            if (p1 != IntPtr.Zero)
                _getMetaJson = Marshal.GetDelegateForFunctionPointer<GetMetaJsonFn>(p1);
        }
        if (_sendMetaCommand == null)
        {
            IntPtr p2 = GetProcAddress(plugin.ModuleHandle, "RynthPluginSendMetaCommand");
            if (p2 != IntPtr.Zero)
                _sendMetaCommand = Marshal.GetDelegateForFunctionPointer<SendMetaCommandFn>(p2);
        }
    }

    private static bool TryFetch(out Payload payload)
    {
        payload = new Payload();
        if (_getMetaJson == null) return false;
        try
        {
            IntPtr ptr = _getMetaJson();
            if (ptr == IntPtr.Zero) return false;
            string? json = Marshal.PtrToStringAnsi(ptr);
            if (string.IsNullOrEmpty(json)) return false;
            var parsed = JsonSerializer.Deserialize(json, MetaPanelJsonContext.Default.Payload);
            if (parsed == null) return false;
            payload = parsed;
            return true;
        }
        catch { return false; }
    }

    private static void Send(MetaCmd cmd)
    {
        if (_sendMetaCommand == null) TryBind();
        if (_sendMetaCommand == null) return;
        IntPtr ansi = IntPtr.Zero;
        try
        {
            string json = JsonSerializer.Serialize(cmd, MetaPanelJsonContext.Default.MetaCmd);
            ansi = Marshal.StringToHGlobalAnsi(json);
            _sendMetaCommand(ansi);
        }
        catch { }
        finally
        {
            if (ansi != IntPtr.Zero) Marshal.FreeHGlobal(ansi);
        }
    }
}
