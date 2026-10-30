// ============================================================================
//  RynthCore.Engine — UI/Panels/RynthChatPanel.cs
//  Avalonia face of RynthChat (the popped-out chat; docked it's
//  ImGui/Panels/ChatFace.cs). Both read the same state (UI/Data/ChatData.cs):
//
//    • Per-channel tabs: All / Chat / Channels / System / Combat / Rynth / Other
//      plus user-defined tabs fed by regex filter rules
//    • Per-channel accent colors + timestamps, auto-scroll to the tail
//    • Search box
//    • Mouse line-selection: drag across lines to highlight, copies to the
//      Windows clipboard on release (works docked and floating — keyboard
//      stays with the game, so copy is mouse-driven by design)
//    • The input line: ChatModel owns it (Win32Backend's chat keys); this
//      face only shows it
//
//  Lines come from UiSources.Chat (RynthChatGetScrollbackJson on the pump).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.ImGuiBackend;
using RynthCore.Engine.UI.Data;

namespace RynthCore.Engine.UI.Panels;

internal static class RynthChatPanel
{
    // Runtime: true while the view is pinned to the newest line. Cleared when the
    // user scrolls up so incoming lines stop yanking the view back to the tail.
    private static bool   _stickToBottom   = true;
    // Treat the view as "at the bottom" when within this many px of the end — absorbs
    // ScrollToEnd landing a line short under deferred layout without dropping follow.
    private const  double ScrollStickThresholdPx = 24.0;

    // ── Mouse line-selection state (Avalonia UI thread only) ─────────────
    //
    // Drag across scrollback lines to highlight a range; releasing the mouse
    // copies the highlighted lines to the Windows clipboard. Keyboard never
    // leaves the game, so copy is deliberately mouse-driven. A plain click
    // (no drag) just clears the selection.
    private static StackPanel?   _selStack;          // chatStack of the live panel
    private static int           _selAnchor = -1;
    private static int           _selEnd    = -1;
    private static bool          _selDragging;
    private static bool          _selMoved;
    private static Point         _selDownPt;
    private static TextBlock?    _flashLabel;
    private static DispatcherTimer? _flashTimer;

    // ⚠ Lazy, NOT eager: an eager Avalonia-object static initializer would run
    // on the first static touch of this class. Nothing touches it before
    // AvaloniaOverlay.Start() any more (the settings load moved to ChatModel),
    // but keep the brushes lazy so that stays harmless — see the dispatcher
    // invariant in EntryPoint.
    private static IBrush? _selectionBrush;
    private static IBrush SelectionBrush =>
        _selectionBrush ??= new SolidColorBrush(Color.FromArgb(0x66, 0x3A, 0x6E, 0xA5));
    private static IBrush? _tabActiveBrush;
    private static IBrush TabActiveBrush   => _tabActiveBrush   ??= new SolidColorBrush(Color.FromArgb(0xFF, 0x26, 0x4C, 0x59));
    private static IBrush? _tabInactiveBrush;
    private static IBrush TabInactiveBrush => _tabInactiveBrush ??= new SolidColorBrush(Color.FromArgb(0xFF, 0x0F, 0x1F, 0x2E));

    private static Color ChannelColor(string chan) => Color.FromUInt32(ChatModel.ChannelArgb(chan));

    // ── Panel construction ────────────────────────────────────────────────

    internal static Control Create()
    {
        ChatModel.EnsureSettingsLoaded();

        string activeTab = ChatModel.CurrentTab();
        string searchFilter = "";
        long lastShownId = 0;
        long seenLinesVersion = -1;
        long seenFiltersVersion = ChatModel.ViewVersion;

        // ── Tab strip with filter + gear buttons ───────────────────────
        var tabButtons = new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase);
        var tabInner = new WrapPanel { Orientation = Orientation.Horizontal };
        var gearBtn = new Button
        {
            Content = "⚙",
            FontSize = 10,
            Padding = new Thickness(5, 2),
            Margin  = new Thickness(0, 1, 2, 0),
            Background = TabInactiveBrush,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
        };
        var filtersBtn = new Button
        {
            Content = "Filters",
            FontSize = 9,
            Padding = new Thickness(5, 2),
            Margin  = new Thickness(0, 1, 1, 0),
            Background = TabInactiveBrush,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
        };
        var tabStrip = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(gearBtn, Dock.Right);
        DockPanel.SetDock(filtersBtn, Dock.Right);
        tabStrip.Children.Add(gearBtn);
        tabStrip.Children.Add(filtersBtn);
        tabStrip.Children.Add(tabInner);

        // ── Search box ─────────────────────────────────────────────────
        var searchBox = new TextBox
        {
            Watermark = "Search…",
            FontSize = 10,
            Height = 22,
            Margin = new Thickness(2, 2, 2, 0),
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x0A, 0x12, 0x1A)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x26, 0x40, 0x59)),
            BorderThickness = new Thickness(1),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(searchBox, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(searchBox, ScrollBarVisibility.Disabled);
        // Keyboard gate: without this, WndProcHook keeps routing keys to the
        // game and the box is untypable (same wiring as SettingsPanel et al).
        searchBox.GotFocus  += (_, _) => Win32Backend.AvaloniaTextInputActive = true;
        searchBox.LostFocus += (_, _) => Win32Backend.AvaloniaTextInputActive = false;

        // ── Scrollback list ────────────────────────────────────────────
        var chatStack = new StackPanel { Orientation = Orientation.Vertical };
        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = chatStack,
        };
        _selStack = chatStack;
        ClearSelectionState();

        // ── Chat input (shows ChatModel's line; keys never reach Avalonia) ──
        const string defaultWatermark = "Press Enter to chat…";
        var chatInputBox = new TextBox
        {
            Watermark = defaultWatermark,
            FontSize  = 10,
            Height    = 22,
            Margin    = new Thickness(2, 1, 2, 1),
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x0A, 0x12, 0x1A)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x26, 0x4C, 0x59)),
            BorderThickness = new Thickness(1),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(chatInputBox, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(chatInputBox, ScrollBarVisibility.Disabled);

        // Tell: start "/tell <selected>, " for the NPC or player selected in the game — quest
        // NPCs usually haven't spoken to you, so there's no name to click in the scrollback.
        // Typing "/tell" (or "/t") alone and pressing Enter does the same.
        var tellButton = new Button
        {
            Content = "Tell",
            FontSize = 10,
            Height = 22,
            Padding = new Thickness(8, 0),
            Margin = new Thickness(0, 1, 2, 1),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x0E, 0x2E, 0x3A)),
            Foreground = Brushes.White,
        };
        ToolTip.SetTip(tellButton, "Select an NPC or player in the game, then click to start /tell <name>, — or type /tell and press Enter.");
        tellButton.Click += (_, _) => Win32Backend.RequestTellSelected();
        var inputRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        inputRow.Children.Add(chatInputBox);
        inputRow.Children.Add(tellButton);
        Grid.SetColumn(tellButton, 1);

        // ── Main layout ────────────────────────────────────────────────
        var mainLayout = new DockPanel
        {
            Background = new SolidColorBrush(Color.FromArgb((byte)ChatModel.BackgroundAlpha, 0x0A, 0x12, 0x1A)),
        };
        DockPanel.SetDock(tabStrip,    Dock.Top);
        DockPanel.SetDock(searchBox,   Dock.Top);
        DockPanel.SetDock(inputRow, Dock.Bottom);
        mainLayout.Children.Add(tabStrip);
        mainLayout.Children.Add(searchBox);
        mainLayout.Children.Add(inputRow);
        mainLayout.Children.Add(scrollViewer);

        // ── "Copied N lines" flash (top-right, above the scrollback) ──
        var flashLabel = new TextBlock
        {
            IsVisible = false,
            FontSize = 9,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x26, 0x4C, 0x59)),
            Padding = new Thickness(6, 2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Top,
            Margin = new Thickness(0, 48, 14, 0),
            IsHitTestVisible = false,
        };
        _flashLabel = flashLabel;

        // ── Settings overlay (opened by gear button) ───────────────────
        var autoScrollCheck = new CheckBox
        {
            Content    = "Auto-scroll",
            IsChecked  = ChatModel.AutoScroll,
            FontSize   = 9,
            Foreground = Brushes.White,
        };

        var suppressChatCheck = new CheckBox
        {
            Content    = "Hide retail chat (Enter types here)",
            IsChecked  = ChatHooks.SuppressOriginalChat,
            FontSize   = 9,
            Foreground = Brushes.White,
        };
        ToolTip.SetTip(suppressChatCheck,
            "On: the retail chatbox is hidden and Enter types in RynthChat.\n" +
            "Off: the retail chatbox is shown and Enter types there; RynthChat keeps showing lines and its Tell button still works.");
        suppressChatCheck.IsCheckedChanged += (_, _) =>
        {
            ChatHooks.SuppressOriginalChat = suppressChatCheck.IsChecked == true;
            ChatModel.SaveSettings();
        };

        var logChatCheck = new CheckBox
        {
            Content    = "Log to file",
            IsChecked  = ChatModel.LogEnabled,
            FontSize   = 9,
            Foreground = Brushes.White,
        };
        logChatCheck.IsCheckedChanged += (_, _) =>
        {
            ChatModel.LogEnabled = logChatCheck.IsChecked == true;   // the pump closes the file
            ChatModel.SaveSettings();
        };

        var clickThroughCheck = new CheckBox
        {
            Content    = "Click-through (hold Ctrl to interact)",
            IsChecked  = ChatModel.CtrlGatedClickThrough,
            FontSize   = 9,
            Foreground = Brushes.White,
        };
        ToolTip.SetTip(clickThroughCheck,
            "Applies to the DOCKED chat panel only — the undocked/popped-out chat is always interactive, like every other floating panel.\nOn: docked clicks pass through to the game; hold Ctrl to interact with chat.\nOff: docked chat is always interactive (default).");
        clickThroughCheck.IsCheckedChanged += (_, _) =>
            ChatModel.SetCtrlGatedClickThrough(clickThroughCheck.IsChecked == true);

        var fontSizeLabel = new TextBlock
        {
            Text = $"Font size: {ChatModel.FontSize:F0}",
            FontSize = 9,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 72,
        };
        var fontSizeSlider = new Slider
        {
            Minimum = 8, Maximum = 18, Value = ChatModel.FontSize,
            Width = 110, TickFrequency = 1, IsSnapToTickEnabled = true,
        };

        var bgOpacityLabel = new TextBlock
        {
            Text = $"Background: {(int)Math.Round(ChatModel.BackgroundAlpha / 2.55)}%",
            FontSize = 9,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 105,
        };
        var bgOpacitySlider = new Slider
        {
            Minimum = 0, Maximum = 255, Value = ChatModel.BackgroundAlpha,
            Width = 110,
        };
        bgOpacitySlider.ValueChanged += (_, e) =>
        {
            ChatModel.BackgroundAlpha = (int)Math.Round(e.NewValue);
            bgOpacityLabel.Text = $"Background: {(int)Math.Round(ChatModel.BackgroundAlpha / 2.55)}%";
            mainLayout.Background = new SolidColorBrush(Color.FromArgb((byte)ChatModel.BackgroundAlpha, 0x0A, 0x12, 0x1A));
            ChatModel.SaveSettings();
        };

        var statusLabel = new TextBlock
        {
            Text       = ChatModel.PluginBound ? "" : "Plugin not bound",
            FontSize   = 8,
            Foreground = Brushes.Gray,
        };

        var settingsOverlay = new Border
        {
            IsVisible           = false,
            Background          = new SolidColorBrush(Color.FromArgb(0xF2, 0x06, 0x0C, 0x14)),
            BorderBrush         = new SolidColorBrush(Color.FromArgb(0xFF, 0x26, 0x4C, 0x59)),
            BorderThickness     = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Top,
            Margin              = new Thickness(0, 22, 2, 0),
            Padding             = new Thickness(10, 8, 10, 10),
            Child = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 6,
                Children =
                {
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6,
                        Children = { fontSizeLabel, fontSizeSlider } },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6,
                        Children = { bgOpacityLabel, bgOpacitySlider } },
                    autoScrollCheck,
                    suppressChatCheck,
                    logChatCheck,
                    clickThroughCheck,
                    statusLabel,
                },
            },
        };
        autoScrollCheck.IsCheckedChanged += (_, _) =>
        {
            ChatModel.AutoScroll = autoScrollCheck.IsChecked == true;
            if (ChatModel.AutoScroll)
            {
                _stickToBottom = true;
                scrollViewer.ScrollToEnd();
            }
            ChatModel.SaveSettings();
        };
        gearBtn.Click += (_, _) => settingsOverlay.IsVisible = !settingsOverlay.IsVisible;

        // Filter rules live in their own panel — they get complex fast.
        filtersBtn.Click += (_, _) => PanelRouter.Toggle("ChatFilters");

        // ── New-tab prompt (opened by the "+" tab button) ──────────────
        var newTabBox = new TextBox
        {
            Watermark = "tab name",
            FontSize = 9,
            Width = 110,
            Height = 20,
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x0A, 0x12, 0x1A)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x26, 0x40, 0x59)),
            BorderThickness = new Thickness(1),
        };
        newTabBox.GotFocus  += (_, _) => Win32Backend.AvaloniaTextInputActive = true;
        newTabBox.LostFocus += (_, _) => Win32Backend.AvaloniaTextInputActive = false;
        var newTabAddBtn = new Button
        {
            Content = "Add",
            FontSize = 9,
            Padding = new Thickness(6, 2),
            Background = TabActiveBrush,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
        };
        var newTabCancelBtn = new Button
        {
            Content = "Cancel",
            FontSize = 9,
            Padding = new Thickness(6, 2),
            Background = TabInactiveBrush,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
        };
        var newTabOverlay = new Border
        {
            IsVisible           = false,
            Background          = new SolidColorBrush(Color.FromArgb(0xF2, 0x06, 0x0C, 0x14)),
            BorderBrush         = new SolidColorBrush(Color.FromArgb(0xFF, 0x26, 0x4C, 0x59)),
            BorderThickness     = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment   = VerticalAlignment.Top,
            Margin              = new Thickness(2, 22, 0, 0),
            Padding             = new Thickness(8, 6),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children = { newTabBox, newTabAddBtn, newTabCancelBtn },
            },
        };

        // ── Root (Grid lets overlays float above main layout) ──────────
        var root = new Grid();
        root.Children.Add(mainLayout);
        root.Children.Add(settingsOverlay);
        root.Children.Add(newTabOverlay);
        root.Children.Add(flashLabel);

        // ── Helpers ────────────────────────────────────────────────────

        ChatLine[] CurrentLines() => UiSources.Chat.Current?.Value ?? Array.Empty<ChatLine>();

        void RebuildDisplay()
        {
            ClearSelectionState();
            chatStack.Children.Clear();
            // The newest 500 shown lines, as AppendLine trims to: the history can be 10,000
            // lines since 2026-10-11 (ChatHistory), far too many TextBlocks. ImGui's face shows all.
            var lines = CurrentLines();
            int from = lines.Length;
            for (int shown = 0; from > 0 && shown < 500; )
                if (ChatModel.LineVisible(lines[--from], activeTab, searchFilter)) shown++;
            if (lines.Length > 0) lastShownId = lines[^1].Id;
            for (int i = from; i < lines.Length; i++)
            {
                if (!ChatModel.LineVisible(lines[i], activeTab, searchFilter)) continue;
                chatStack.Children.Add(MakeTextBlock(lines[i]));
            }
            // A full rebuild (tab/filter/font change) re-pins to the newest line.
            if (autoScrollCheck.IsChecked == true)
            {
                _stickToBottom = true;
                scrollViewer.ScrollToEnd();
            }
        }

        void RebuildTabs()
        {
            tabInner.Children.Clear();
            tabButtons.Clear();
            foreach (var tab in ChatModel.AllTabs())
            {
                var btn = new Button
                {
                    Content = tab,
                    FontSize = 9,
                    Padding = new Thickness(5, 2),
                    Margin  = new Thickness(1, 1, 0, 0),
                    Background = string.Equals(tab, activeTab, StringComparison.OrdinalIgnoreCase)
                        ? TabActiveBrush : TabInactiveBrush,
                    Foreground = Brushes.White,
                    BorderThickness = new Thickness(0),
                };
                string captured = tab;
                btn.Click += (_, _) => SelectTab(captured);
                tabButtons[tab] = btn;
                tabInner.Children.Add(btn);

                // Custom tabs only: a small ✕ to delete the tab (disables any
                // rules that target it so it doesn't resurrect via the union).
                if (!ChatModel.IsBaseTab(tab))
                {
                    var delTabBtn = new Button
                    {
                        Content = "✕",
                        FontSize = 8,
                        Padding = new Thickness(2, 2),
                        Margin  = new Thickness(0, 1, 0, 0),
                        Background = TabInactiveBrush,
                        Foreground = Brushes.IndianRed,
                        BorderThickness = new Thickness(0),
                    };
                    delTabBtn.Click += (_, _) => ChatModel.DeleteCustomTab(captured);
                    tabInner.Children.Add(delTabBtn);
                }
            }
            // "+" — create a new custom tab via the name prompt.
            var addTabBtn = new Button
            {
                Content = "+",
                FontSize = 9,
                Padding = new Thickness(5, 2),
                Margin  = new Thickness(1, 1, 0, 0),
                Background = TabInactiveBrush,
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x7A, 0xE0, 0x9A)),
                BorderThickness = new Thickness(0),
            };
            addTabBtn.Click += (_, _) =>
            {
                newTabBox.Text = "";
                newTabOverlay.IsVisible = true;
                newTabBox.Focus();
            };
            tabInner.Children.Add(addTabBtn);

            // Active tab's source was deleted: fall back to All.
            if (!tabButtons.ContainsKey(activeTab))
            {
                activeTab = "All";
                tabButtons["All"].Background = TabActiveBrush;
            }
        }

        void ShowTab(string tab)
        {
            activeTab = tab;
            foreach (var kv in tabButtons)
            {
                kv.Value.Background = string.Equals(kv.Key, tab, StringComparison.OrdinalIgnoreCase)
                    ? TabActiveBrush : TabInactiveBrush;
            }
            RebuildDisplay();
        }

        void SelectTab(string tab)
        {
            ChatModel.SelectTab(tab);
            ShowTab(tab);
        }

        void AppendLine(ChatLine line)
        {
            lastShownId = line.Id;
            if (!ChatModel.LineVisible(line, activeTab, searchFilter)) return;
            chatStack.Children.Add(MakeTextBlock(line));
            // While a selection drag is live, don't trim from the top — it would
            // shift the highlighted indices under the cursor. The next rebuild
            // or tail-follow resyncs the backlog.
            if (_selDragging) return;
            if (autoScrollCheck.IsChecked == true && _stickToBottom)
            {
                // Following the tail: trim backlog from the top and keep the newest line in view.
                while (chatStack.Children.Count > 500)
                    RemoveHeadLine(chatStack);
                scrollViewer.ScrollToEnd();
            }
            else
            {
                // User scrolled up to read — don't yank the view or shift it by trimming
                // from the top. Allow a bounded backlog; the next rebuild resyncs to 500.
                while (chatStack.Children.Count > 1000)
                    RemoveHeadLine(chatStack);
            }
        }

        // ── New-tab prompt wiring ──────────────────────────────────────

        void CommitNewTab()
        {
            string name = (newTabBox.Text ?? "").Trim();
            newTabOverlay.IsVisible = false;
            Win32Backend.AvaloniaTextInputActive = false;
            if (name.Length == 0) return;
            ChatModel.AddCustomTab(name);
            seenFiltersVersion = ChatModel.ViewVersion;
            RebuildTabs();
            ShowTab(ChatModel.CurrentTab());
        }

        newTabAddBtn.Click += (_, _) => CommitNewTab();
        newTabCancelBtn.Click += (_, _) =>
        {
            newTabOverlay.IsVisible = false;
            Win32Backend.AvaloniaTextInputActive = false;
        };
        newTabBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitNewTab(); e.Handled = true; }
            else if (e.Key == Key.Escape) { newTabOverlay.IsVisible = false; e.Handled = true; }
        };

        // ── Event wiring ───────────────────────────────────────────────

        searchBox.TextChanged += (_, _) =>
        {
            searchFilter = searchBox.Text ?? "";
            RebuildDisplay();
        };

        // Track whether the user is parked at the tail. Scrolling up clears the
        // stick flag so AppendLine stops auto-scrolling; returning to the bottom
        // re-arms it. Only react to genuine offset changes — extent-only changes
        // fire when a new line is appended (extent grows before our queued
        // ScrollToEnd lands) and would wrongly unstick mid-stream, breaking the
        // initial scroll-to-bottom on the first scrollback burst at startup.
        scrollViewer.ScrollChanged += (_, e) =>
        {
            if (e.OffsetDelta.Y == 0) return;
            double maxOffset = scrollViewer.Extent.Height - scrollViewer.Viewport.Height;
            _stickToBottom = maxOffset <= 0 || scrollViewer.Offset.Y >= maxOffset - ScrollStickThresholdPx;
        };

        // ── Mouse line-selection (docked path: real Avalonia pointer events).
        // Line TextBlocks are IsHitTestVisible=false, so these land on the
        // ScrollViewer; presses on the scrollbar are handled (e.Handled) by the
        // ScrollBar before reaching us and never start a selection.
        scrollViewer.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(scrollViewer).Properties.IsLeftButtonPressed) return;
            if (StartSelection(e.GetPosition(chatStack)))
                e.Pointer.Capture(scrollViewer);
        };
        scrollViewer.PointerMoved += (_, e) =>
        {
            if (_selDragging) UpdateSelection(e.GetPosition(chatStack));
        };
        scrollViewer.PointerReleased += (_, e) =>
        {
            if (_selDragging)
            {
                EndSelection(e.GetPosition(chatStack));
                e.Pointer.Capture(null);
            }
        };

        // ── Chat input: ChatModel's line (updated on AC's thread) ─────
        var defaultBorderBrush = chatInputBox.BorderBrush;
        var activeBorderBrush  = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xD7, 0x00));

        void ShowInput()
        {
            string text = ChatModel.InputText;
            chatInputBox.Text = text;
            chatInputBox.CaretIndex = Math.Min(ChatModel.InputCursor, text.Length);
            chatInputBox.Watermark = ChatModel.InputHint ?? defaultWatermark;
            chatInputBox.BorderBrush = Win32Backend.ChatCaptureActive ? activeBorderBrush : defaultBorderBrush;
        }
        void OnInputChanged() => Dispatcher.UIThread.Post(ShowInput);

        // ── Poll timer: new lines, filter/tab changes ──────────────────
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        bool captureShown = false;
        timer.Tick += (_, _) =>
        {
            statusLabel.Text = ChatModel.PluginBound ? "" : "Plugin not bound";

            long filtersVersion = ChatModel.ViewVersion;
            string current = ChatModel.CurrentTab();
            if (filtersVersion != seenFiltersVersion)
            {
                seenFiltersVersion = filtersVersion;
                activeTab = current;
                RebuildTabs();
                ShowTab(activeTab);
                seenLinesVersion = UiSources.Chat.Current?.Version ?? -1;
            }
            else if (!string.Equals(current, activeTab, StringComparison.OrdinalIgnoreCase))
                ShowTab(current);

            var snap = UiSources.Chat.Current;
            if (snap != null && snap.Version != seenLinesVersion)
            {
                seenLinesVersion = snap.Version;
                foreach (var line in snap.Value)
                    if (line.Id > lastShownId) AppendLine(line);
            }

            // Capture ends inside Win32Backend (Enter / Escape) without an input change.
            if (captureShown != Win32Backend.ChatCaptureActive)
            {
                captureShown = Win32Backend.ChatCaptureActive;
                ShowInput();
            }
        };

        fontSizeSlider.ValueChanged += (_, e) =>
        {
            ChatModel.FontSize = (float)e.NewValue;
            fontSizeLabel.Text = $"Font size: {ChatModel.FontSize:F0}";
            RebuildDisplay();
            ChatModel.SaveSettings();
        };

        RebuildTabs();
        ShowTab(activeTab);
        seenLinesVersion = UiSources.Chat.Current?.Version ?? -1;

        // Stop with the visual tree — a running DispatcherTimer roots the closed
        // view forever. Drag/resize fires Detached→Attached, so restart on attach.
        // Whether chat is on screen decides who owns Enter and whether the retail
        // chatbox is hidden (ChatModel.SetShown).
        root.AttachedToVisualTree += (_, _) =>
        {
            UiSources.Chat.Subscribe();
            UiSources.Chat.RequestRefresh();
            ChatModel.InputChanged += OnInputChanged;
            ShowInput();
            ChatModel.SetShown(true);
            if (!timer.IsEnabled) timer.Start();
        };
        root.DetachedFromVisualTree += (_, _) =>
        {
            timer.Stop();
            ChatModel.SetShown(false);
            ChatModel.InputChanged -= OnInputChanged;
            UiSources.Chat.Unsubscribe();
        };

        return root;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static TextBlock MakeTextBlock(ChatLine line)
    {
        // Plain TextBlock, IsHitTestVisible=false: line-range selection is
        // handled at the ScrollViewer level (drag → highlight → copy on
        // release). Tag carries the formatted text for the clipboard join.
        return new TextBlock
        {
            Text         = line.FormattedText,
            Tag          = line.FormattedText,
            FontSize     = ChatModel.FontSize,
            FontFamily   = new FontFamily("Consolas,Courier New,monospace"),
            Foreground   = new SolidColorBrush(ChannelColor(line.Channel)),
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(3, 0, 3, 0),
            IsHitTestVisible = false,
        };
    }

    private static void RemoveHeadLine(StackPanel stack)
    {
        stack.Children.RemoveAt(0);
        // Keep any live (non-dragging) highlight aligned with the shifted list.
        if (_selAnchor > 0) _selAnchor--;
        if (_selEnd    > 0) _selEnd--;
    }

    // ── Mouse line-selection core (Avalonia UI thread) ────────────────────

    private static void ClearSelectionState()
    {
        if (_selStack != null && _selAnchor >= 0)
            foreach (var child in _selStack.Children)
                if (child is TextBlock tb) tb.Background = null;
        _selAnchor = _selEnd = -1;
        _selDragging = false;
        _selMoved = false;
    }

    private static int HitLineIndex(double y, bool clamp)
    {
        if (_selStack == null || _selStack.Children.Count == 0) return -1;
        var children = _selStack.Children;
        if (y < children[0].Bounds.Y)
            return clamp ? 0 : -1;
        for (int i = 0; i < children.Count; i++)
        {
            var b = children[i].Bounds;
            if (y >= b.Y && y < b.Y + b.Height) return i;
        }
        return clamp ? children.Count - 1 : -1;
    }

    private static void ApplyHighlight()
    {
        if (_selStack == null) return;
        int lo = Math.Min(_selAnchor, _selEnd), hi = Math.Max(_selAnchor, _selEnd);
        var children = _selStack.Children;
        for (int i = 0; i < children.Count; i++)
            if (children[i] is TextBlock tb)
                tb.Background = (i >= lo && i <= hi && lo >= 0) ? SelectionBrush : null;
    }

    private static bool StartSelection(Point pInStack)
    {
        ClearSelectionState();
        int idx = HitLineIndex(pInStack.Y, clamp: false);
        if (idx < 0) return false;
        _selAnchor = _selEnd = idx;
        _selDragging = true;
        _selMoved = false;
        _selDownPt = pInStack;
        ApplyHighlight();
        return true;
    }

    private static void UpdateSelection(Point pInStack)
    {
        if (!_selDragging) return;
        if (Math.Abs(pInStack.X - _selDownPt.X) > 3 || Math.Abs(pInStack.Y - _selDownPt.Y) > 3)
            _selMoved = true;
        int idx = HitLineIndex(pInStack.Y, clamp: true);
        if (idx >= 0 && idx != _selEnd)
        {
            _selEnd = idx;
            ApplyHighlight();
        }
    }

    private static void EndSelection(Point pInStack)
    {
        if (!_selDragging) return;
        UpdateSelection(pInStack);
        _selDragging = false;
        // A drag (even within one line) copies; a plain click just clears.
        if (_selMoved || _selEnd != _selAnchor)
            CopySelectionToClipboard();
        else
            ClearSelectionState();
    }

    private static void CopySelectionToClipboard()
    {
        if (_selStack == null || _selAnchor < 0) return;
        int lo = Math.Min(_selAnchor, _selEnd), hi = Math.Max(_selAnchor, _selEnd);
        var parts = new List<string>(hi - lo + 1);
        var children = _selStack.Children;
        for (int i = lo; i <= hi && i < children.Count; i++)
            if (children[i] is TextBlock tb && tb.Tag is string s)
                parts.Add(s);
        if (parts.Count == 0) return;
        FlashStatus(ChatModel.CopyLines(parts));
    }

    private static void FlashStatus(string message)
    {
        if (_flashLabel == null) return;
        _flashLabel.Text = message;
        _flashLabel.IsVisible = true;
        _flashTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _flashTimer.Stop();
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            _flashTimer!.Stop();
            _flashTimer.Tick -= handler;
            if (_flashLabel != null) _flashLabel.IsVisible = false;
        };
        _flashTimer.Tick += handler;
        _flashTimer.Start();
    }

    // ── Floating-panel selection entry points (called from FloatingPanelHost
    //    ForwardInput on the Avalonia UI thread). The panel Border is parked
    //    off-canvas so normal pointer routing never reaches the ScrollViewer;
    //    the host forwards raw mouse DOWN/MOVE/UP in PanelBorder-logical
    //    coordinates and we translate into chatStack space via the same
    //    layout-bounds walk the other floating dispatch blocks use. ──────────

    private static Point? TranslateToStack(Visual root, Point pInRoot)
    {
        if (_selStack == null) return null;
        // Accumulate layout offsets walking UP from the stack to the given
        // root. Scroll offset is reflected in the content's Bounds.Position
        // (ScrollContentPresenter arranges its child at -Offset).
        double dx = 0, dy = 0;
        Visual? v = _selStack;
        while (v != null && v != root)
        {
            var b = v.Bounds;
            dx += b.X; dy += b.Y;
            v = v.GetVisualParent();
        }
        if (v != root) return null;
        return new Point(pInRoot.X - dx, pInRoot.Y - dy);
    }

    internal static bool FloatingSelectionDown(Visual panelRoot, Point pInRoot)
    {
        var p = TranslateToStack(panelRoot, pInRoot);
        return p.HasValue && StartSelection(p.Value);
    }

    internal static void FloatingSelectionMove(Visual panelRoot, Point pInRoot)
    {
        var p = TranslateToStack(panelRoot, pInRoot);
        if (p.HasValue) UpdateSelection(p.Value);
    }

    internal static void FloatingSelectionUp(Visual panelRoot, Point pInRoot)
    {
        var p = TranslateToStack(panelRoot, pInRoot);
        if (p.HasValue) EndSelection(p.Value);
        else { _selDragging = false; ClearSelectionState(); }
    }

    internal static bool FloatingSelectionActive => _selDragging;
}
