// ============================================================================
//  RynthCore.Engine — UI/Panels/NavPanel.cs
//  Avalonia replica of LegacyNavigationUi.cs (RynthSuite plugin).
//
//  Data goes through UiDataHub (UI/Data/NavData.cs): UiSources.Nav polls
//  RynthPluginGetNavJson on the pump thread and NavCommands sends
//  RynthPluginSendNavCommand there; the ImGui face (ImGui/Panels/NavFace.cs)
//  reads the same snapshot.
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
using RynthCore.Engine.UI.Data;
using Payload = RynthCore.Engine.UI.Data.NavPayload;

namespace RynthCore.Engine.UI.Panels;

internal static class NavPanel
{
    private static readonly IBrush ColAmber   = new SolidColorBrush(Color.FromRgb(0xE8, 0xB3, 0x33));
    private static readonly IBrush ColGreen   = new SolidColorBrush(Color.FromRgb(0x33, 0xCC, 0x66));
    private static readonly IBrush ColTeal    = new SolidColorBrush(Color.FromRgb(0x26, 0xD9, 0xE6));
    private static readonly IBrush ColMute    = new SolidColorBrush(Color.FromRgb(0x8C, 0xA6, 0xBF));
    private static readonly IBrush ColTextDim = new SolidColorBrush(Color.FromRgb(0xD9, 0xE6, 0xF2));
    private static readonly IBrush ColShellBg = new SolidColorBrush(Color.FromRgb(0x0A, 0x0F, 0x14));
    private static readonly IBrush ColPanelBg = new SolidColorBrush(Color.FromRgb(0x14, 0x1F, 0x29));
    private static readonly IBrush ColRowAlt  = new SolidColorBrush(Color.FromRgb(0x10, 0x18, 0x22));
    private static readonly IBrush ColBtnFill = new SolidColorBrush(Color.FromRgb(0x0F, 0x1F, 0x2E));
    private static readonly IBrush ColBtnBord = new SolidColorBrush(Color.FromRgb(0x26, 0x40, 0x59));

    private static readonly IBrush ColStartBg = new SolidColorBrush(Color.FromRgb(0x19, 0x61, 0x19));
    private static readonly IBrush ColStopBg  = new SolidColorBrush(Color.FromRgb(0x80, 0x19, 0x19));

    private static readonly string[] RouteTypes = NavCommands.RouteTypes;
    private static readonly string[] AddModes   = NavCommands.AddModes;

    private static readonly int[] RecallIds = NavCommands.RecallIds;

    private sealed class State
    {
        public Payload Data        = new();
        public int     SelectedIdx = -1;
        public int     AddModeIdx  = 0;
        public string  SaveName    = string.Empty;
        public string  ChatText    = string.Empty;
        public int     ChatFor     = -1;   // waypoint whose command is in ChatText
        public bool    Typing;     // name box focused: the poll must not rebuild it away
    }

    // ── Picker overlay (identical pattern to SettingsPanel) ──────────────────

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

            const double pickerWidth = 220;
            Point ap         = anchor.TranslatePoint(new Point(0, anchor.Bounds.Height), Canvas) ?? new Point(8, 8);
            double rootWidth  = Root?.Bounds.Width  ?? 400;
            double rootHeight = Root?.Bounds.Height ?? 300;
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
        var state = new State();

        var root = new Border
        {
            Background      = ColShellBg,
            BorderBrush     = ColBtnBord,
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4),
            MinWidth        = 360,
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

        // Waypoint list controls live for the panel's lifetime. Rebuild() only refills
        // waypointStack: a ScrollViewer recreated (or given a new Content) resets its
        // offset to the top, which made the list jump back every poll and impossible to edit.
        var waypointStack  = new StackPanel { Spacing = 1 };
        var waypointScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility   = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = waypointStack,
        };
        var waypointHost = new Border { Height = 200, Child = waypointScroll };

        // Last plugin JSON the panel was built from. A rebuild from a user action
        // (optimistic local edit) clears it so the next poll always resyncs with the plugin.
        string lastJson = string.Empty;
        bool   rebuildFromPoll = false;

        void Rebuild()
        {
            if (!rebuildFromPoll) lastJson = string.Empty;
            Vector keepOffset = waypointScroll.Offset;
            content.Children.Clear();
            waypointStack.Children.Clear();
            var d = state.Data;
            bool navActive = d.MacroRunning && d.NavigationEnabled;

            // ── Active nav name ───────────────────────────────────────────────
            content.Children.Add(new TextBlock
            {
                Text         = $"Active Nav: {(string.IsNullOrEmpty(d.ActiveNavName) ? "None (Unsaved)" : d.ActiveNavName)}",
                Foreground   = ColAmber,
                FontSize     = 11,
                FontWeight   = FontWeight.Bold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            // ── Status line ───────────────────────────────────────────────────
            if (!string.IsNullOrEmpty(d.NavStatusLine))
            {
                content.Children.Add(new TextBlock
                {
                    Text         = d.NavStatusLine,
                    Foreground   = d.NavIsStuck ? ColAmber : ColGreen,
                    FontSize     = 10,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }

            // ── Start / Stop button ───────────────────────────────────────────
            var startStopBtn = new Button
            {
                Content                    = navActive ? "Stop Navigation" : "Start Navigation",
                HorizontalAlignment        = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Height          = 28,
                Background      = navActive ? ColStopBg : ColStartBg,
                Foreground      = ColTextDim,
                BorderBrush     = ColBtnBord,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(3),
                FontSize        = 11,
            };
            startStopBtn.Click += (_, _) =>
            {
                bool active = state.Data.MacroRunning && state.Data.NavigationEnabled;
                if (active)
                {
                    Send(new NavCmd { Cmd = "stopNav" });
                    state.Data.NavigationEnabled = false;
                }
                else
                {
                    Send(new NavCmd { Cmd = "startNav" });
                    state.Data.MacroRunning      = true;
                    state.Data.NavigationEnabled = true;
                }
                Rebuild();
            };
            content.Children.Add(startStopBtn);

            content.Children.Add(new Border { Height = 1, Background = ColBtnBord });

            // ── Route type + Insert mode ──────────────────────────────────────
            var controlRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,4,*") };

            int rIdx     = d.RouteType switch { 1 => 1, 2 => 2, 3 => 3, _ => 0 };
            var routeBtn = MakePickerBtn($"Route: {RouteTypes[rIdx]}");
            routeBtn.Click += (_, _) =>
            {
                int cur = state.Data.RouteType switch { 1 => 1, 2 => 2, 3 => 3, _ => 0 };
                picker.Show(routeBtn, RouteTypes, cur, idx =>
                {
                    int enumVal = idx switch { 1 => 1, 2 => 2, 3 => 3, _ => 4 };
                    Send(new NavCmd { Cmd = "setRouteType", RouteType = enumVal });
                    state.Data.RouteType = enumVal;
                    Rebuild();
                });
            };
            Grid.SetColumn(routeBtn, 0);
            controlRow.Children.Add(routeBtn);

            var insertBtn = MakePickerBtn($"Insert: {AddModes[state.AddModeIdx]}");
            insertBtn.Click += (_, _) =>
            {
                int cur = state.AddModeIdx;
                picker.Show(insertBtn, AddModes, cur, idx =>
                {
                    state.AddModeIdx = idx;
                    Rebuild();
                });
            };
            Grid.SetColumn(insertBtn, 2);
            controlRow.Children.Add(insertBtn);

            content.Children.Add(controlRow);

            // ── Action buttons ────────────────────────────────────────────────
            var actionWrap = new WrapPanel { Orientation = Orientation.Horizontal };

            var addWpBtn = MakeActionBtn("Add Waypoint");
            addWpBtn.Click += (_, _) =>
                Send(new NavCmd { Cmd = "addWaypoint", AddMode = state.AddModeIdx, InsertAt = state.SelectedIdx });
            actionWrap.Children.Add(addWpBtn);

            var addPortalBtn = MakeActionBtn("Add Portal");
            addPortalBtn.IsEnabled = false;
            ToolTip.SetTip(addPortalBtn, "Add Portal is not yet implemented.");
            actionWrap.Children.Add(addPortalBtn);

            var addRecallBtn = MakeActionBtn("Add Recall");
            var recallLabels = new string[RecallIds.Length];
            for (int ri = 0; ri < RecallIds.Length; ri++)
                recallLabels[ri] = NavCommands.RecallLabels[ri];
            addRecallBtn.Click += (_, _) =>
                picker.Show(addRecallBtn, recallLabels, -1, idx =>
                    Send(new NavCmd { Cmd = "addRecall", SpellId = RecallIds[idx], AddMode = state.AddModeIdx, InsertAt = state.SelectedIdx }));
            actionWrap.Children.Add(addRecallBtn);

            var clearBtn = MakeActionBtn("Clear Route");
            clearBtn.Click += (_, _) =>
            {
                Send(new NavCmd { Cmd = "clearRoute" });
                state.Data.Points.Clear();
                state.SelectedIdx = -1;
                Rebuild();
            };
            actionWrap.Children.Add(clearBtn);

            var saveBtn = MakeActionBtn("Save Route");
            saveBtn.Click += (_, _) => Send(new NavCmd { Cmd = "saveRoute" });
            actionWrap.Children.Add(saveBtn);

            var dunPatrolBtn = MakeActionBtn("Dungeon Patrol");
            dunPatrolBtn.Click += (_, _) => Send(new NavCmd { Cmd = "dunPatrol" });
            actionWrap.Children.Add(dunPatrolBtn);

            content.Children.Add(actionWrap);

            // ── Chat waypoint ─────────────────────────────────────────────────
            // Sent as if typed when the route reaches it: /ra, /ub, /mt commands,
            // game commands, or plain text to say. Placed per the Add mode.
            // Selecting an existing chat waypoint loads its command; Update saves the edit.
            bool chatSelected = state.SelectedIdx >= 0 && state.SelectedIdx < d.Points.Count
                                && d.Points[state.SelectedIdx].Type == "Chat";
            if (state.SelectedIdx != state.ChatFor)
            {
                if (chatSelected) state.ChatText = d.Points[state.SelectedIdx].Text;
                else if (state.ChatFor >= 0) state.ChatText = string.Empty;
                state.ChatFor = chatSelected ? state.SelectedIdx : -1;
            }
            var chatRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
            var chatBox = new TextBox
            {
                Text = state.ChatText, Watermark = "Chat command, e.g. /ub usei Healing Kit",
                Background = ColBtnFill, Foreground = ColTextDim,
                BorderBrush = ColBtnBord, BorderThickness = new Thickness(1),
                FontSize = 10, Height = 22, Padding = new Thickness(4, 1),
                Margin = new Thickness(0, 0, 4, 0),
            };
            chatBox.TextChanged += (_, _) => state.ChatText = chatBox.Text ?? string.Empty;
            chatBox.GotFocus    += (_, _) => state.Typing = true;
            chatBox.LostFocus   += (_, _) => state.Typing = false;
            var addChatBtn = MakeActionBtn("Add Chat");
            var updChatBtn = MakeActionBtn("Update");
            updChatBtn.IsVisible = chatSelected;
            void AddChat()
            {
                string text = state.ChatText.Trim();
                if (text.Length == 0) return;
                Send(new NavCmd { Cmd = "addChat", Text = text, AddMode = state.AddModeIdx, InsertAt = state.SelectedIdx });
                state.ChatText = string.Empty;
                state.ChatFor = -1;
                state.Typing = false;
                Rebuild();
            }
            void UpdateChat()
            {
                string text = state.ChatText.Trim();
                if (text.Length == 0 || !chatSelected) return;
                Send(new NavCmd { Cmd = "editChat", Index = state.SelectedIdx, Text = text });
                state.Typing = false;
                Rebuild();
            }
            addChatBtn.Click += (_, _) => AddChat();
            updChatBtn.Click += (_, _) => UpdateChat();
            chatBox.KeyDown += (_, e) =>
            {
                if (e.Key != Avalonia.Input.Key.Enter) return;
                e.Handled = true;
                if (chatSelected) UpdateChat(); else AddChat();
            };
            Grid.SetColumn(chatBox, 0);
            Grid.SetColumn(updChatBtn, 1);
            Grid.SetColumn(addChatBtn, 2);
            chatRow.Children.Add(chatBox);
            chatRow.Children.Add(updChatBtn);
            chatRow.Children.Add(addChatBtn);
            content.Children.Add(chatRow);

            // ── Save as a named nav ───────────────────────────────────────────
            // Save Route writes the current file; a route with no file got
            // nowhere to go and there was no way to name one.
            var saveAsRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var nameBox = new TextBox
            {
                Text = state.SaveName, Watermark = "Nav name...",
                Background = ColBtnFill, Foreground = ColTextDim,
                BorderBrush = ColBtnBord, BorderThickness = new Thickness(1),
                FontSize = 10, Height = 22, Padding = new Thickness(4, 1),
                Margin = new Thickness(0, 0, 4, 0),
            };
            nameBox.TextChanged += (_, _) => state.SaveName = nameBox.Text ?? string.Empty;
            nameBox.GotFocus    += (_, _) => state.Typing = true;
            nameBox.LostFocus   += (_, _) => state.Typing = false;
            var saveAsBtn = MakeActionBtn("Save As");
            void SaveAs()
            {
                string name = state.SaveName.Trim();
                if (name.Length == 0) return;
                Send(new NavCmd { Cmd = "saveRoute", NavName = name });
                state.SaveName = string.Empty;
                state.Typing = false;
                Rebuild();
            }
            saveAsBtn.Click += (_, _) => SaveAs();
            nameBox.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; SaveAs(); } };
            Grid.SetColumn(nameBox, 0);
            Grid.SetColumn(saveAsBtn, 1);
            saveAsRow.Children.Add(nameBox);
            saveAsRow.Children.Add(saveAsBtn);
            content.Children.Add(saveAsRow);

            // ── Nav file selector ─────────────────────────────────────────────
            if (d.NavFiles.Count > 0)
            {
                var fileRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                fileRow.Children.Add(new TextBlock
                {
                    Text = "Nav:",
                    Foreground = ColMute,
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0),
                });

                int activeFileIdx  = string.IsNullOrEmpty(d.ActiveNavName) ? -1 : d.NavFiles.IndexOf(d.ActiveNavName);
                string fileBtnText = activeFileIdx >= 0 ? d.NavFiles[activeFileIdx] : "Select nav file...";
                var fileBtn        = MakePickerBtn(fileBtnText);
                Grid.SetColumn(fileBtn, 1);
                fileBtn.Click += (_, _) =>
                {
                    int cur = string.IsNullOrEmpty(state.Data.ActiveNavName) ? -1 : state.Data.NavFiles.IndexOf(state.Data.ActiveNavName);
                    picker.Show(fileBtn, state.Data.NavFiles.ToArray(), cur, idx =>
                        Send(new NavCmd { Cmd = "loadNav", NavName = state.Data.NavFiles[idx] }));
                };
                fileRow.Children.Add(fileBtn);
                content.Children.Add(fileRow);
            }

            // ── Waypoint list ─────────────────────────────────────────────────
            content.Children.Add(new Border { Height = 1, Background = ColBtnBord });
            content.Children.Add(new TextBlock
            {
                Text       = $"Waypoints ({d.Points.Count})",
                Foreground = ColMute,
                FontSize   = 10,
            });

            for (int i = 0; i < d.Points.Count; i++)
            {
                int  ci         = i;
                var  pt         = d.Points[i];
                bool isActive   = i == d.ActiveNavIndex;
                bool isSelected = i == state.SelectedIdx;

                var row = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("22,18,*"),
                    Background = isSelected
                        ? new SolidColorBrush(Color.FromRgb(0x1A, 0x2E, 0x42))
                        : (i % 2 == 0 ? ColPanelBg : ColRowAlt),
                };

                var delBtn = new Button
                {
                    Content     = "X",
                    Width       = 18,
                    Height      = 18,
                    Padding     = new Thickness(0),
                    Margin      = new Thickness(2),
                    Background  = new SolidColorBrush(Color.FromRgb(0x80, 0x1A, 0x1A)),
                    Foreground  = ColTextDim,
                    BorderBrush = ColBtnBord,
                    BorderThickness = new Thickness(1),
                    CornerRadius    = new CornerRadius(2),
                    FontSize = 9,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                };
                delBtn.Click += (_, e) =>
                {
                    e.Handled = true;
                    Send(new NavCmd { Cmd = "deletePoint", Index = ci });
                    if (state.SelectedIdx == ci)        state.SelectedIdx = -1;
                    else if (state.SelectedIdx > ci)    state.SelectedIdx--;
                    if (ci < state.Data.Points.Count)   state.Data.Points.RemoveAt(ci);
                    Rebuild();
                };
                Grid.SetColumn(delBtn, 0);
                row.Children.Add(delBtn);

                var indicator = new TextBlock
                {
                    Text                = isActive ? "=>" : "  ",
                    Foreground          = isActive ? ColTeal : ColMute,
                    FontSize            = 9,
                    VerticalAlignment   = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                Grid.SetColumn(indicator, 1);
                row.Children.Add(indicator);

                string disp = NavCommands.PointText(pt, i);
                var desc = new TextBlock
                {
                    Text             = disp,
                    Foreground       = isActive ? ColTeal : ColTextDim,
                    FontSize         = 10,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin           = new Thickness(4, 0, 2, 0),
                    TextTrimming     = TextTrimming.CharacterEllipsis,
                };
                Grid.SetColumn(desc, 2);
                row.Children.Add(desc);

                row.PointerPressed += (_, e) =>
                {
                    if (e.Handled) return;
                    state.SelectedIdx = ci;
                    Rebuild();
                };

                waypointStack.Children.Add(row);
            }

            content.Children.Add(waypointHost); // same instance every rebuild — keeps scroll offset
            // Re-apply after layout too: the re-attach / new row extent can coerce it to 0.
            waypointScroll.Offset = keepOffset;
            Dispatcher.UIThread.Post(() => waypointScroll.Offset = keepOffset, DispatcherPriority.Loaded);
        }

        // ── Poll timer ────────────────────────────────────────────────────────
        // Reads the hub's snapshot (fetched every 1 s while subscribed, and
        // right after each command).
        long seenVersion = -1;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            if (picker.ActivePicker != null || state.Typing) return;
            // A rebuild between press and release eats the click on X / row select. seenVersion
            // is left alone, so the change is picked up once the pointer leaves the list.
            if (waypointHost.IsPointerOver) return;
            var snap = UiSources.Nav.Current;
            if (snap == null || snap.Version == seenVersion) return;
            seenVersion = snap.Version;
            state.Data = snap.Value.Clone();
            Rebuild();
        };
        // Stop with the visual tree — a running DispatcherTimer roots the closed
        // view forever (one immortal poller per open/close). RadarPanel idiom;
        // must restart on attach: drag/resize fires Detached→Attached.
        root.AttachedToVisualTree += (_, _) =>
        {
            UiSources.Nav.Subscribe();
            UiSources.Nav.RequestRefresh();
            if (!timer.IsEnabled) timer.Start();
        };
        root.DetachedFromVisualTree += (_, _) =>
        {
            timer.Stop();
            UiSources.Nav.Unsubscribe();
        };

        Rebuild();
        return root;
    }

    // =========================================================================
    //  Helpers
    // =========================================================================

    private static Button MakePickerBtn(string text) => new Button
    {
        Content                    = text,
        HorizontalAlignment        = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Left,
        Background      = ColBtnFill,
        Foreground      = ColTextDim,
        BorderBrush     = ColBtnBord,
        BorderThickness = new Thickness(1),
        CornerRadius    = new CornerRadius(3),
        Padding         = new Thickness(6, 2),
        FontSize        = 10,
        Height          = 22,
    };

    private static Button MakeActionBtn(string text) => new Button
    {
        Content         = text,
        Background      = ColBtnFill,
        Foreground      = ColTextDim,
        BorderBrush     = ColBtnBord,
        BorderThickness = new Thickness(1),
        CornerRadius    = new CornerRadius(3),
        Padding         = new Thickness(8, 3),
        FontSize        = 10,
        Height          = 24,
        Margin          = new Thickness(0, 0, 4, 4),
    };

    private static void Send(NavCmd cmd) => NavCommands.Send(cmd);
}
