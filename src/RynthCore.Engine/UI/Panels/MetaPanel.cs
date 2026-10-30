// =============================================================================
//  RynthCore.Engine — UI/Panels/MetaPanel.cs
//  Avalonia port of LegacyMetaUi.cs (RynthSuite plugin).
//
//  Data goes through UiDataHub (UI/Data/MetaData.cs): the snapshot comes
//  from UiSources.Meta and commands go out through MetaCommands, so the
//  plugin's exports run on the pump thread, not on this UI thread. The ImGui
//  face (ImGui/Panels/MetaFace.cs) reads the same snapshot.
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
using RynthCore.Engine.UI.Data;
using Payload = RynthCore.Engine.UI.Data.MetaPayload;

namespace RynthCore.Engine.UI.Panels;

internal static class MetaPanel
{
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

    // ── Names, hints, text helpers (shared with the ImGui face) ──────────────
    private static readonly string[] ConditionNames = MetaVocabulary.ConditionNames;
    private static readonly string[] ConditionHints = MetaVocabulary.ConditionHints;
    private static readonly string[] ActionNames = MetaVocabulary.ActionNames;
    private static readonly string[] ActionHints = MetaVocabulary.ActionHints;
    private static bool IsCompositeCondition(int idx) => MetaVocabulary.IsCompositeCondition(idx);
    private static bool IsAllAction(int idx) => MetaVocabulary.IsAllAction(idx);
    private static string CondText(MetaRuleDto c) => MetaVocabulary.CondText(c);

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
        public bool SourceMsgOk = true;
        public DateTime SourceMsgTime = DateTime.MinValue;
        public TextBlock? SourceMsgRef;
        // Source "Apply": the plugin's ApplyResult.Seq before the apply was
        // sent; a newer seq in a snapshot is this apply's result. -2 = none pending.
        public long ApplySeqBefore = -2;
        public DateTime ApplySentAt;
        public string SaveName = string.Empty;
        public bool ShowSaveInput;
        public HashSet<string> CollapsedStates = new();
        public HashSet<int> ExpandedRows = new();
        // UI deep-dive Roadmap #2 (2026-07-02): per-state-group row container,
        // built lazily the first time a group is expanded and reused on every
        // later collapse/expand of that SAME group (see the headerGrid
        // PointerPressed handler in BuildListView) — toggling just flips
        // IsVisible instead of tearing down and reconstructing the whole list
        // view via rebuild(). A collapsed group that has never been expanded
        // still has no entry here and its rows are never built, preserving
        // the earlier memory win for large VTank-migrated metas (~420 controls
        // for a fully-collapsed list). Cleared at the top of every BuildListView
        // call so a genuine rebuild (data change, poll, editor round-trip)
        // reconstructs fresh panels against current rule data instead of
        // reusing stale ones built from a prior Payload.
        public Dictionary<string, Panel> GroupRowCache = new();
        public string LastSig = "init_sig";   // content signature of the last Rebuild
        public long SeenVersion = -1;         // hub snapshot version last taken
        // Roadmap #5 (2026-07-02): live references to the bottom-bar state
        // button and the file-picker button, so the poll timer can update
        // their displayed text in place once CurrentState/CurrentMetaPath
        // are excluded from PayloadSig — see the timer.Tick body. Cleared/
        // reset on every rebuild since these controls are recreated each time.
        public Button? StateBtnRef;
        public Button? FileBtnRef;
    }

    // Roadmap #4 (2026-07-02): enable/move/duplicate/delete apply the same
    // change locally to ps.Data.Rules right away (instant feedback, and a
    // second click on a stale row can't hit the wrong index), then the hub's
    // next snapshot, taken after the plugin applied the command, replaces the
    // local guess (MetaCommands defers the refresh past the plugin tick).

    // Cheap content signature — everything the list view shows EXCEPT the
    // per-tick LastFiredMs (excluded so an idle/botting panel stops flashing;
    // a structural change still triggers a rebuild).
    //
    // Roadmap #5 completion (2026-07-02): CurrentState and CurrentMetaPath
    // used to be included here too — every bot state transition changed the
    // sig and forced a full Rebuild() of the whole list view (flash; on
    // undocked also a full RTT+ULW pass), and recreated the Save-As filename
    // TextBox mid-typing (focus/caret loss) as collateral damage. Both are
    // now updated IN PLACE from the poll timer instead (see StateBtnRef/
    // FileBtnRef and the timer.Tick body) — display-only changes that don't
    // need a structural rebuild.
    private static string PayloadSig(Payload p)
    {
        var sb = new System.Text.StringBuilder(256);
        sb.Append(p.EnableMeta ? '1' : '0').Append(p.MetaDebug ? '1' : '0')
          .Append('|').Append(p.SourceText?.Length ?? 0)
          .Append('|').Append(string.Join(",", p.Files.Select(f => f.Display)))
          .Append('|').Append(string.Join(",", p.States));
        foreach (var r in p.Rules)
            sb.Append('#').Append(r.State).Append('~').Append(r.Condition).Append('~')
              .Append(r.ConditionData).Append('~').Append(r.Action).Append('~')
              .Append(r.ActionData).Append('~').Append(r.Children.Count).Append('~')
              .Append(r.ActionChildren.Count).Append('~').Append(r.Enabled ? '1' : '0');
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

        // Polls the hub's snapshot (fetched every 2 s while subscribed, and
        // right after each command).
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            var snap = UiSources.Meta.Current;
            if (snap != null && snap.Version != ps.SeenVersion)
                CheckApplyResult(ps, snap.Value);
            UpdateSourceMsg(ps);
            if (picker.ActivePicker != null) return;
            if (ps.Mode == ViewMode.Editor) return; // don't clobber in-progress edits
            if (snap == null || snap.Version == ps.SeenVersion) return;
            ps.SeenVersion = snap.Version;
            var fresh = snap.Value.Clone();
            ps.Data = fresh;   // keep click-handlers on fresh data even if we skip the redraw
            if (ps.Mode == ViewMode.Source && string.IsNullOrEmpty(ps.SourceText))
                ps.SourceText = fresh.SourceText;

            // Roadmap #5 completion: CurrentState/CurrentMetaPath are no
            // longer part of PayloadSig, so update their two display
            // controls in place, every poll, regardless of whether the sig
            // changed — property sets only, no layout churn, no rebuild.
            if (ps.StateBtnRef != null) ps.StateBtnRef.Content = fresh.CurrentState;
            if (ps.FileBtnRef != null) ps.FileBtnRef.Content = MetaVocabulary.CurrentFileDisplay(fresh);
            string sig = PayloadSig(fresh);
            if (sig == ps.LastSig) return;   // nothing visible changed → no rebuild → no flash
            ps.LastSig = sig;
            // UI deep-dive P0-6 (2026-07-02): this early-return used to guard
            // only ViewMode.Editor. In ViewMode.Source, any sig change (a
            // CurrentState transition while botting, a file-list change, a
            // plugin-side SourceText edit) called Rebuild() -> BuildSourceView,
            // which constructs a BRAND-NEW TextEditor + TextDocument — discarding
            // the user's caret, selection, scroll offset, undo stack, and
            // keyboard focus mid-typing. Worse, the discarded editor's TextArea
            // may have left Win32Backend.AvaloniaTextInputActive stuck true if
            // LostFocus doesn't fire on subtree detach, swallowing game
            // keystrokes until another editor gets focus. ps.Data/LastSig above
            // still update in the background (cheap, no visual effect) so
            // switching back to List always shows current data via that click
            // handler's own explicit rebuild() call — gating only the automatic
            // poll-driven Rebuild() here is safe.
            if (ps.Mode != ViewMode.List) return;
            Rebuild();
        };
        // Stop with the visual tree — a running DispatcherTimer roots the closed
        // view forever (one immortal poller per open/close). RadarPanel idiom;
        // must restart on attach: drag/resize fires Detached→Attached.
        root.AttachedToVisualTree += (_, _) =>
        {
            UiSources.Meta.Subscribe();
            UiSources.Meta.RequestRefresh();
            if (!timer.IsEnabled) timer.Start();
        };
        root.DetachedFromVisualTree += (_, _) =>
        {
            timer.Stop();
            UiSources.Meta.Unsubscribe();
        };

        var first = UiSources.Meta.Current;
        if (first != null)
        {
            ps.SeenVersion = first.Version;
            ps.Data = first.Value.Clone();
            ps.LastSig = PayloadSig(ps.Data);
        }
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
        // Roadmap #2: a full BuildListView pass always reflects a genuine
        // rebuild (data change, poll, mode round-trip) — invalidate the
        // per-group row cache so the header click handler wired up below
        // starts clean against THIS pass's headerGrid/content instances and
        // current rule data, rather than reusing panels rooted on a
        // now-discarded content tree.
        ps.GroupRowCache.Clear();
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

            // Roadmap #2 (2026-07-02): collapse/expand used to call rebuild(),
            // tearing down and reconstructing the ENTIRE list view (up to
            // ~420 controls for a large VTank-migrated meta) just to hide or
            // show one group. Now it's an in-place operation: the group's row
            // panel is built once on first expand, cached on ps.GroupRowCache,
            // and every later toggle just flips IsVisible — no rebuild(), no
            // rescanning d.Rules, no new controls. A group that starts (or
            // stays) collapsed never gets its panel built at all, so the
            // earlier "collapsed rows are never constructed" memory win is
            // unchanged.
            headerGrid.PointerPressed += (_, _) =>
            {
                if (ps.CollapsedStates.Contains(capturedKey))
                {
                    // Expand.
                    ps.CollapsedStates.Remove(capturedKey);
                    collapseIndicator.Text = "▼";
                    if (ps.GroupRowCache.TryGetValue(capturedKey, out var cached))
                    {
                        cached.IsVisible = true;
                    }
                    else
                    {
                        var built = BuildGroupRowsPanel();
                        ps.GroupRowCache[capturedKey] = built;
                        int headerIdx = content.Children.IndexOf(headerGrid);
                        content.Children.Insert(headerIdx + 1, built);
                    }
                }
                else
                {
                    // Collapse — hide, don't tear down; a re-expand reuses it.
                    ps.CollapsedStates.Add(capturedKey);
                    collapseIndicator.Text = "▶";
                    if (ps.GroupRowCache.TryGetValue(capturedKey, out var cached))
                        cached.IsVisible = false;
                }
            };
            content.Children.Add(headerGrid);

            if (!isCollapsed)
            {
                var panel = BuildGroupRowsPanel();
                ps.GroupRowCache[capturedKey] = panel;
                content.Children.Add(panel);
            }

            continue;

            // Local function: builds this group's row controls (and any
            // inline child-expansion rows) into a standalone panel instead of
            // appending directly to `content`, so it can be cached and its
            // visibility toggled independently of the rest of the list.
            Panel BuildGroupRowsPanel()
            {
            var groupPanel = new StackPanel { Spacing = 0 };
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
                    ColumnDefinitions = new ColumnDefinitions("16,18,18,18,18,20,*,*"),
                    Background = firing ? ColFired : (gi % 2 == 0 ? ColPanelBg : ColRowAlt),
                    MinHeight  = 20,
                    Opacity    = rule.Enabled ? 1.0 : 0.5,
                };

                // Enable/disable toggle (col 0)
                var enBtn = new Button
                {
                    Content = rule.Enabled ? "☑" : "☐",
                    Width = 14, Height = 16,
                    Padding = new Thickness(0), Margin = new Thickness(1),
                    Background = ColBtnFill,
                    Foreground = rule.Enabled ? ColGreen : ColMute,
                    BorderBrush = ColBtnBord, BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(2), FontSize = 10,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                };
                ToolTip.SetTip(enBtn, rule.Enabled ? "Enabled — click to disable" : "Disabled — click to enable");
                enBtn.Click += (_, e) =>
                {
                    e.Handled = true;
                    bool newEnabled = !rule.Enabled;
                    Send(new MetaCmd { Op = "set_rule_enabled", Index = capturedGlobalIdx, Value = newEnabled.ToString().ToLowerInvariant() });
                    // Roadmap #4: optimistic local mutation + reconcile — see PanelState.
                    if (capturedGlobalIdx >= 0 && capturedGlobalIdx < ps.Data.Rules.Count)
                        ps.Data.Rules[capturedGlobalIdx].Enabled = newEnabled;
                    rebuild();
                };
                Grid.SetColumn(enBtn, 0);
                row.Children.Add(enBtn);

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
                    Grid.SetColumn(expBtn, 1);
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
                upBtn.Click += (_, e) =>
                {
                    e.Handled = true;
                    Send(new MetaCmd { Op = "move_up", Index = capturedGlobalIdx });
                    // Roadmap #4: optimistic local mutation — swap with the
                    // previous row WITHIN THIS STATE GROUP (stateRules is in
                    // the same display order the plugin's move_up/move_down
                    // reorders within), using its real global index.
                    if (capturedGi > 0)
                    {
                        int otherGlobalIdx = stateRules[capturedGi - 1].GlobalIdx;
                        if (capturedGlobalIdx < ps.Data.Rules.Count && otherGlobalIdx < ps.Data.Rules.Count)
                            (ps.Data.Rules[capturedGlobalIdx], ps.Data.Rules[otherGlobalIdx]) =
                                (ps.Data.Rules[otherGlobalIdx], ps.Data.Rules[capturedGlobalIdx]);
                    }
                    rebuild();
                };
                Grid.SetColumn(upBtn, 2);
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
                dnBtn.Click += (_, e) =>
                {
                    e.Handled = true;
                    Send(new MetaCmd { Op = "move_down", Index = capturedGlobalIdx });
                    // Roadmap #4: optimistic local mutation — mirrors upBtn above.
                    if (capturedGi < stateRules.Count - 1)
                    {
                        int otherGlobalIdx = stateRules[capturedGi + 1].GlobalIdx;
                        if (capturedGlobalIdx < ps.Data.Rules.Count && otherGlobalIdx < ps.Data.Rules.Count)
                            (ps.Data.Rules[capturedGlobalIdx], ps.Data.Rules[otherGlobalIdx]) =
                                (ps.Data.Rules[otherGlobalIdx], ps.Data.Rules[capturedGlobalIdx]);
                    }
                    rebuild();
                };
                Grid.SetColumn(dnBtn, 3);
                row.Children.Add(dnBtn);

                // Duplicate button (col 4)
                var dupBtn = new Button
                {
                    Content = "❏", Width = 16, Height = 16,
                    Padding = new Thickness(0), Margin = new Thickness(1),
                    Background = ColBtnFill, Foreground = ColMute,
                    BorderBrush = ColBtnBord, BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(2), FontSize = 9,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                };
                ToolTip.SetTip(dupBtn, "Duplicate rule");
                dupBtn.Click += (_, e) =>
                {
                    e.Handled = true;
                    var clone = CloneRule(rule);
                    Send(new MetaCmd { Op = "duplicate_rule", Index = capturedGlobalIdx, Rule = clone });
                    // Roadmap #4: optimistic local mutation — insert right after
                    // the original, matching the natural duplicate convention.
                    int insertAt = Math.Clamp(capturedGlobalIdx + 1, 0, ps.Data.Rules.Count);
                    ps.Data.Rules.Insert(insertAt, CloneRule(rule));
                    rebuild();
                };
                Grid.SetColumn(dupBtn, 4);
                row.Children.Add(dupBtn);

                // Delete button (col 5)
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
                delBtn.Click += (_, e) =>
                {
                    e.Handled = true;
                    Send(new MetaCmd { Op = "delete_rule", Index = capturedGlobalIdx });
                    // Roadmap #4 (the finding's headline bug): local RemoveAt
                    // closes the stale-index delete window — a rapid second
                    // delete click now indexes into the ALREADY-shortened
                    // local list (rebuilt synchronously below), instead of a
                    // list that's stale until the next 2s poll.
                    if (capturedGlobalIdx >= 0 && capturedGlobalIdx < ps.Data.Rules.Count)
                        ps.Data.Rules.RemoveAt(capturedGlobalIdx);
                    rebuild();
                };
                Grid.SetColumn(delBtn, 5);
                row.Children.Add(delBtn);

                // Condition text (col 4)
                string condName = rule.Condition >= 0 && rule.Condition < ConditionNames.Length
                    ? ConditionNames[rule.Condition] : $"Cond({rule.Condition})";
                string condText;
                if (IsCompositeCondition(rule.Condition) && rule.Children.Count > 0)
                {
                    string ft = CondText(rule.Children[0]);
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
                Grid.SetColumn(condLabel, 6);
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
                Grid.SetColumn(actLabel, 7);
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

                groupPanel.Children.Add(row);

                // Inline child expansion
                if (hasChildren && isExpanded)
                {
                    if (IsCompositeCondition(rule.Condition) && rule.Children.Count > 0)
                    {
                        groupPanel.Children.Add(new TextBlock
                        {
                            Text = $"  {ConditionNames[rule.Condition]}:",
                            Foreground = ColAmber, FontSize = 9, FontWeight = FontWeight.SemiBold,
                            Margin = new Thickness(22, 1, 0, 0),
                        });
                        foreach (var child in rule.Children)
                        {
                            string ct = CondText(child);
                            groupPanel.Children.Add(new TextBlock
                            {
                                Text = $"     • {ct}", Foreground = ColMute, FontSize = 9,
                                Margin = new Thickness(22, 0, 0, 0),
                            });
                        }
                    }
                    if (IsAllAction(rule.Action) && rule.ActionChildren.Count > 0)
                    {
                        groupPanel.Children.Add(new TextBlock
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
                            groupPanel.Children.Add(new TextBlock
                            {
                                Text = $"     • {at}", Foreground = ColMute, FontSize = 9,
                                Margin = new Thickness(22, 0, 0, 0),
                            });
                        }
                    }
                }
            }
            return groupPanel;
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
        // Roadmap #5: cleared here (not just set on creation below) since the
        // save-input branch below doesn't create fileBtn at all this render —
        // a stale reference from a previous render would update a control
        // that's no longer in the visual tree.
        ps.FileBtnRef = null;

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
                    string path = System.IO.Path.Combine(MetaVocabulary.MetaFolder, n + ".af");
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
        var fileBtn = MakePickerBtn(MetaVocabulary.CurrentFileDisplay(d));
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
        ps.FileBtnRef = fileBtn; // Roadmap #5: poll-timer in-place update target

        var refreshBtn = MakeSmallBtn("↺");
        ToolTip.SetTip(refreshBtn, "Refresh file list");
        refreshBtn.Click += (_, _) => UiSources.Meta.RequestRefresh(); // the timer rebuilds on the new snapshot
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
        ps.StateBtnRef = stateBtn; // Roadmap #5: poll-timer in-place update target

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
            BuildSubRuleSection(content, ps, picker, rebuild, r.Children,
                                r.Condition == 20 ? "Not (one condition):" : "Sub-Conditions", isAction: false,
                                maxCount: r.Condition == 20 ? 1 : int.MaxValue);
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

    // Sub-conditions nest: a Not/All/Any sub-condition gets its own indented list below its
    // row (a Not takes exactly one). They used to be single flat rows, so a Not under an All
    // had nowhere to say what it negates. Deep enough for anything a meta writes.
    private const int MaxSubConditionDepth = 4;

    private static void BuildSubRuleSection(
        StackPanel content, PanelState ps, PickerState picker, Action rebuild,
        List<MetaRuleDto> subRules, string label, bool isAction, int depth = 0, int maxCount = int.MaxValue)
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

            var typeBtn = MakePickerBtn(isAction ? MetaVocabulary.ActionName(sub.Action) : MetaVocabulary.ConditionName(sub.Condition));
            typeBtn.FontSize = 9;
            typeBtn.Height = 20;
            typeBtn.Click += (_, _) =>
            {
                picker.Show(typeBtn, names, isAction ? sub.Action : sub.Condition, idx =>
                {
                    if (isAction) sub.Action = idx; else sub.Condition = idx;
                    typeBtn.Content = names[idx];
                    if (!isAction)
                    {
                        // Composite ↔ simple changes the row's shape: redraw it.
                        if (!IsCompositeCondition(idx)) sub.Children.Clear();
                        else if (idx == 20 && sub.Children.Count > 1) sub.Children.RemoveRange(1, sub.Children.Count - 1);
                        rebuild();
                    }
                });
            };
            Grid.SetColumn(typeBtn, 0);
            subRow.Children.Add(typeBtn);

            bool nests = !isAction && IsCompositeCondition(sub.Condition) && depth < MaxSubConditionDepth;
            if (nests)
            {
                subRow.Children.Add(new TextBlock
                {
                    Text = sub.Condition == 20 ? "(the condition below)" : "(the conditions below)",
                    Foreground = ColTextDim, FontSize = 9, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 0, 0),
                });
                Grid.SetColumn(subRow.Children[^1], 1);
            }

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
            if (!nests) subRow.Children.Add(dataBox);

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

            if (nests)
            {
                var nested = new StackPanel { Margin = new Thickness(14, 0, 0, 2) };
                string nestedLabel = sub.Condition switch { 20 => "Not:", 2 => "All of:", _ => "Any of:" };
                BuildSubRuleSection(nested, ps, picker, rebuild, sub.Children, nestedLabel, isAction: false,
                                    depth + 1, maxCount: sub.Condition == 20 ? 1 : int.MaxValue);
                content.Children.Add(nested);
            }
        }

        if (subRules.Count >= maxCount) return;   // a Not already has its one operand

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

        ps.SourceMsgRef = new TextBlock { FontSize = 10, TextWrapping = TextWrapping.Wrap };
        content.Children.Add(ps.SourceMsgRef);
        UpdateSourceMsg(ps);

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
            ps.ApplySeqBefore = UiSources.Meta.Current?.Value.ApplyResult?.Seq ?? -1;
            ps.ApplySentAt = DateTime.Now;
            Send(new MetaCmd { Op = "set_source", Text = ps.SourceText });
            SetSourceMsg(ps, "Applying…", ok: true);
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

    private static MetaRuleDto CloneRule(MetaRuleDto src) => src.Clone();

    // =========================================================================
    //  Source Apply result
    // =========================================================================

    private static void SetSourceMsg(PanelState ps, string text, bool ok)
    {
        ps.SourceMsg = text;
        ps.SourceMsgOk = ok;
        ps.SourceMsgTime = DateTime.Now;
        UpdateSourceMsg(ps);
    }

    /// <summary>A snapshot whose ApplyResult is newer than the pending Apply is its result.</summary>
    private static void CheckApplyResult(PanelState ps, Payload fresh)
    {
        if (ps.ApplySeqBefore == -2) return;
        MetaApplyResult? r = fresh.ApplyResult;
        if (r != null && r.Seq > ps.ApplySeqBefore)
        {
            ps.ApplySeqBefore = -2;
            SetSourceMsg(ps, r.Text, r.Ok);
        }
    }

    /// <summary>Shows the message for 8 s; a plugin that doesn't report results gets a pointer to chat.</summary>
    private static void UpdateSourceMsg(PanelState ps)
    {
        if (ps.ApplySeqBefore != -2 && (DateTime.Now - ps.ApplySentAt).TotalSeconds > 3)
        {
            ps.ApplySeqBefore = -2;
            SetSourceMsg(ps, "Sent. This RynthAi version reports the result in chat.", ok: true);
            return;
        }
        if (ps.SourceMsgRef == null) return;
        bool show = ps.SourceMsg.Length > 0 && (ps.ApplySeqBefore != -2 || (DateTime.Now - ps.SourceMsgTime).TotalSeconds < 8);
        ps.SourceMsgRef.IsVisible = show;
        if (!show) return;
        ps.SourceMsgRef.Text = ps.SourceMsg;
        ps.SourceMsgRef.Foreground = ps.SourceMsgOk ? ColGreen : ColRed;
    }

    private static void Send(MetaCmd cmd) => MetaCommands.Send(cmd);
}
