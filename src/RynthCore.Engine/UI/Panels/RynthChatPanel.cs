// ============================================================================
//  RynthCore.Engine — UI/Panels/RynthChatPanel.cs
//  Avalonia chat-replacement panel backed by the RynthChat plugin (RynthSuite).
//
//  Phase 1: read-only display alongside retail chat (retail not yet suppressed).
//    • Per-channel tabs: All / Chat / Channels / System / Combat / Rynth / Other
//      plus user-defined tabs fed by regex filter rules
//    • Regex filter rules: move matching lines to a custom tab, or hide them
//    • Per-channel accent colors + timestamps
//    • Auto-scroll to tail
//    • Search/filter TextBox (wired; typing requires mouse hover — Phase 4 fix)
//    • Mouse line-selection: drag across lines to highlight, copies to the
//      Windows clipboard on release (works docked and floating — keyboard
//      stays with the game, so copy is mouse-driven by design)
//    • Incremental polling via RynthChatGetScrollbackJson(sinceSeq) every 100 ms
//
//  Bridge exports (resolved from the RynthChat plugin DLL via GetProcAddress):
//    RynthChatGetScrollbackJson(ulong sinceSeq)  → ANSI JSON array of new lines
//    RynthChatSendLine(char* ansiText)            → submit via InvokeChatParser (Phase 5)
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.UI.Panels;

internal static class RynthChatPanel
{
    // ── P/Invoke ──────────────────────────────────────────────────────────

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    // Win32 clipboard — Avalonia's clipboard service is unreliable inside the
    // injected acclient process (no proper TopLevel ownership), so write
    // CF_UNICODETEXT directly.
    [DllImport("user32.dll")] private static extern bool   OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")] private static extern bool   CloseClipboard();
    [DllImport("user32.dll")] private static extern bool   EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern bool   GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr hMem);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetScrollbackJsonFn(ulong sinceSeq);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SendLineFn(IntPtr ansiText);

    // ── Bridge state ─────────────────────────────────────────────────────

    private static GetScrollbackJsonFn? _getScrollbackJson;
    private static SendLineFn?          _sendLine;
    private static bool                 _bindingLogged;

    // ── Runtime state (Avalonia UI thread only) ──────────────────────────

    private static ulong _lastSeq;
    private static readonly List<ChatDisplayLine> _allLines = new(500);
    private static string _activeChannel = "All";
    private static string _searchFilter  = "";
    // Game-thread state for the chat input — all fields written only from WndProcHook callbacks.
    private static string _captureText = "";
    private static int    _cursorPos;
    private static readonly List<string> _history = new();
    private static int    _historyIndex = -1;

    // ── Chat display settings ─────────────────────────────────────────────
    private static double _chatFontSize    = 10.0;
    private static byte   _backgroundAlpha = 0xF2;
    private static bool   _autoScroll      = true;
    // Runtime: true while the view is pinned to the newest line. Cleared when the
    // user scrolls up so incoming lines stop yanking the view back to the tail.
    private static bool   _stickToBottom   = true;
    // Treat the view as "at the bottom" when within this many px of the end — absorbs
    // ScrollToEnd landing a line short under deferred layout without dropping follow.
    private const  double ScrollStickThresholdPx = 24.0;

    // ── Regex filter rules + custom tabs (Avalonia UI thread only) ────────
    //
    // Each rule is a regex matched against the formatted line. First match
    // wins. An empty Tab means "hide the line entirely"; otherwise the line
    // is MOVED to that tab (it leaves its original channel tab but still
    // shows under "All"). Rules are edited in the separate "ChatFilters"
    // panel (RynthChatFiltersPanel); the lists live here because the chat
    // panel owns persistence and display routing.
    private static readonly List<ChatFilterRule> _filters = new();
    private static readonly List<string> _customTabs = new();
    private static bool _settingsLoaded;
    // Set by Create(): rebuilds tab strip + display. Invoked (via
    // NotifyFiltersChanged) when the filters panel mutates the rule list.
    private static Action? _onFiltersChanged;

    internal static List<ChatFilterRule> Filters => _filters;
    internal static List<string> CustomTabsStore => _customTabs;

    /// <summary>Persist + re-route after any rule/tab mutation. Safe to call
    /// when the chat panel was never created (settings still save).</summary>
    internal static void NotifyFiltersChanged()
    {
        SaveSettings();
        _onFiltersChanged?.Invoke();
    }

    internal sealed class ChatFilterRule
    {
        internal bool   Enabled = true;
        internal string Pattern = "";
        internal string Tab     = "";      // "" = hide matching lines
        internal Regex? Compiled;
        internal bool   Invalid;

        internal void Recompile()
        {
            Compiled = null;
            Invalid  = false;
            if (Pattern.Length == 0) { Invalid = true; return; }
            try
            {
                Compiled = new Regex(Pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(50));
            }
            catch { Invalid = true; }
        }
    }

    /// <summary>Resolve where a line should display after filter rules.
    /// Returns null when a hide-rule matched; otherwise the effective tab
    /// (a custom tab name, or the line's classified channel).</summary>
    private static string? EffectiveTab(ChatDisplayLine line)
    {
        foreach (var rule in _filters)
        {
            if (!rule.Enabled || rule.Compiled == null) continue;
            bool hit;
            try { hit = rule.Compiled.IsMatch(line.FormattedText); }
            catch (RegexMatchTimeoutException) { continue; }
            if (!hit) continue;
            return rule.Tab.Length == 0 ? null : rule.Tab;
        }
        return line.Channel;
    }

    /// <summary>Custom tabs = explicitly created tabs plus any tab named by an
    /// ENABLED rule (typing a tab name in a rule creates the tab implicitly).
    /// Deleting a tab removes it from the explicit list AND disables rules
    /// targeting it, so it doesn't resurrect through this union.</summary>
    internal static IEnumerable<string> CustomTabs()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in Tabs) seen.Add(t);
        foreach (var t in _customTabs)
            if (seen.Add(t)) yield return t;
        foreach (var rule in _filters)
            if (rule.Enabled && rule.Tab.Length > 0 && seen.Add(rule.Tab))
                yield return rule.Tab;
    }

    internal static bool IsBaseTab(string tab) =>
        Tabs.Contains(tab, StringComparer.OrdinalIgnoreCase);

    internal static void DeleteCustomTab(string tab)
    {
        _customTabs.RemoveAll(t => string.Equals(t, tab, StringComparison.OrdinalIgnoreCase));
        foreach (var rule in _filters)
            if (string.Equals(rule.Tab, tab, StringComparison.OrdinalIgnoreCase))
                rule.Enabled = false;
        NotifyFiltersChanged();
    }

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

    private static readonly IBrush SelectionBrush =
        new SolidColorBrush(Color.FromArgb(0x66, 0x3A, 0x6E, 0xA5));

    // ── Chat logging (Avalonia UI thread only) ────────────────────────────
    private static bool         _logEnabled;
    private static string?      _logCharacterName;
    private static StreamWriter? _logWriter;

    // ── Channel definitions (must match ChatClassifier in plugin) ─────────

    private static readonly string[] Tabs =
        { "All", "Chat", "Channels", "System", "Combat", "Rynth", "Other" };

    private static Color ChannelColor(string chan) => chan switch
    {
        "Chat"     => Color.FromArgb(0xFF, 0xE0, 0xE0, 0xE0),
        "Channels" => Color.FromArgb(0xFF, 0x7A, 0xB8, 0xF5),
        "System"   => Color.FromArgb(0xFF, 0x8C, 0xA6, 0xBF),
        "Combat"   => Color.FromArgb(0xFF, 0xD9, 0x33, 0x33),
        "Rynth"    => Color.FromArgb(0xFF, 0xE6, 0xB4, 0x50),  // amber — distinct from the blue/grey channels
        _          => Color.FromArgb(0xFF, 0xAA, 0xAA, 0xAA),
    };

    private static readonly IBrush TabActiveBrush   = new SolidColorBrush(Color.FromArgb(0xFF, 0x26, 0x4C, 0x59));
    private static readonly IBrush TabInactiveBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x0F, 0x1F, 0x2E));

    // ── Panel construction ────────────────────────────────────────────────

    internal static Control Create()
    {
        EnsureSettingsLoaded();
        TryBind();

        // ── Tab strip with filter + gear buttons ───────────────────────
        var tabButtons = new Dictionary<string, Button>();
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

        // ── Chat input ─────────────────────────────────────────────────
        var chatInputBox = new TextBox
        {
            Watermark = "Press Enter to chat…",
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

        // ── Main layout ────────────────────────────────────────────────
        var mainLayout = new DockPanel
        {
            Background = new SolidColorBrush(Color.FromArgb(_backgroundAlpha, 0x0A, 0x12, 0x1A)),
        };
        DockPanel.SetDock(tabStrip,    Dock.Top);
        DockPanel.SetDock(searchBox,   Dock.Top);
        DockPanel.SetDock(chatInputBox, Dock.Bottom);
        mainLayout.Children.Add(tabStrip);
        mainLayout.Children.Add(searchBox);
        mainLayout.Children.Add(chatInputBox);
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
            IsChecked  = _autoScroll,
            FontSize   = 9,
            Foreground = Brushes.White,
        };

        var suppressChatCheck = new CheckBox
        {
            Content    = "Hide retail chat",
            IsChecked  = ChatHooks.SuppressOriginalChat,
            FontSize   = 9,
            Foreground = Brushes.White,
        };
        suppressChatCheck.IsCheckedChanged += (_, _) =>
        {
            ChatHooks.SuppressOriginalChat = suppressChatCheck.IsChecked == true;
            SaveSettings();
        };

        var logChatCheck = new CheckBox
        {
            Content    = "Log to file",
            IsChecked  = _logEnabled,
            FontSize   = 9,
            Foreground = Brushes.White,
        };
        logChatCheck.IsCheckedChanged += (_, _) =>
        {
            _logEnabled = logChatCheck.IsChecked == true;
            if (!_logEnabled) CloseLog();
            SaveSettings();
        };

        var fontSizeLabel = new TextBlock
        {
            Text = $"Font size: {_chatFontSize:F0}",
            FontSize = 9,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 72,
        };
        var fontSizeSlider = new Slider
        {
            Minimum = 8, Maximum = 18, Value = _chatFontSize,
            Width = 110, TickFrequency = 1, IsSnapToTickEnabled = true,
        };

        var bgOpacityLabel = new TextBlock
        {
            Text = $"Background: {(int)Math.Round(_backgroundAlpha / 2.55)}%",
            FontSize = 9,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 105,
        };
        var bgOpacitySlider = new Slider
        {
            Minimum = 0, Maximum = 255, Value = _backgroundAlpha,
            Width = 110,
        };
        bgOpacitySlider.ValueChanged += (_, e) =>
        {
            _backgroundAlpha = (byte)Math.Round(e.NewValue);
            bgOpacityLabel.Text = $"Background: {(int)Math.Round(_backgroundAlpha / 2.55)}%";
            mainLayout.Background = new SolidColorBrush(Color.FromArgb(_backgroundAlpha, 0x0A, 0x12, 0x1A));
            SaveSettings();
        };

        var statusLabel = new TextBlock
        {
            Text       = _getScrollbackJson != null ? "" : "Plugin not bound",
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
                    statusLabel,
                },
            },
        };
        autoScrollCheck.IsCheckedChanged += (_, _) =>
        {
            _autoScroll = autoScrollCheck.IsChecked == true;
            if (_autoScroll)
            {
                _stickToBottom = true;
                scrollViewer.ScrollToEnd();
            }
            SaveSettings();
        };
        gearBtn.Click += (_, _) => settingsOverlay.IsVisible = !settingsOverlay.IsVisible;

        // Filter rules now live in their own panel — they get complex fast.
        filtersBtn.Click += (_, _) => AvaloniaOverlay.ActivateBarButton("ChatFilters");

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

        bool LineVisible(ChatDisplayLine line, out string? effTab)
        {
            effTab = EffectiveTab(line);
            if (effTab == null) return false;                       // hidden by rule
            if (_activeChannel != "All" && !string.Equals(effTab, _activeChannel, StringComparison.OrdinalIgnoreCase))
                return false;
            string filter = _searchFilter;
            if (filter.Length > 0 && !line.FormattedText.Contains(filter, StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }

        void RebuildDisplay()
        {
            ClearSelectionState();
            chatStack.Children.Clear();
            foreach (var line in _allLines)
            {
                if (!LineVisible(line, out _)) continue;
                chatStack.Children.Add(MakeTextBlock(line));
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
            foreach (var tab in Tabs.Concat(CustomTabs()))
            {
                var btn = new Button
                {
                    Content = tab,
                    FontSize = 9,
                    Padding = new Thickness(5, 2),
                    Margin  = new Thickness(1, 1, 0, 0),
                    Background = string.Equals(tab, _activeChannel, StringComparison.OrdinalIgnoreCase)
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
                if (!IsBaseTab(tab))
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
                    delTabBtn.Click += (_, _) => DeleteCustomTab(captured);
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
            if (!tabButtons.ContainsKey(_activeChannel))
            {
                _activeChannel = "All";
                tabButtons["All"].Background = TabActiveBrush;
            }
        }

        void SelectTab(string tab)
        {
            RynthLog.Info($"[RynthChat] SelectTab({tab}) called.");
            _activeChannel = tab;
            foreach (var kv in tabButtons)
            {
                kv.Value.Background = string.Equals(kv.Key, tab, StringComparison.OrdinalIgnoreCase)
                    ? TabActiveBrush : TabInactiveBrush;
            }
            RebuildDisplay();
            SaveSettings();
        }

        void AppendLine(ChatDisplayLine line)
        {
            if (!LineVisible(line, out _)) return;
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
            if (Tabs.Concat(CustomTabs()).Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                SelectTab(name);   // already exists — just switch to it
                return;
            }
            _customTabs.Add(name);
            SaveSettings();
            RebuildTabs();
            SelectTab(name);
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

        // The filters panel mutates the rule list and calls
        // NotifyFiltersChanged(); re-route + rebuild the tab strip here.
        _onFiltersChanged = () =>
        {
            RebuildTabs();
            RebuildDisplay();
        };

        // ── Event wiring ───────────────────────────────────────────────

        searchBox.TextChanged += (_, _) =>
        {
            _searchFilter = searchBox.Text ?? "";
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

        // ── Chat input callbacks (all key handling in WndProcHook) ────────
        // WndProcHook intercepts WM_CHAR / VK_BACK / VK_RETURN / VK_ESCAPE while
        // ChatCaptureActive and fires these callbacks.  No Avalonia focus needed.

        var defaultBorderBrush = chatInputBox.BorderBrush;
        var activeBorderBrush  = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xD7, 0x00));

        Win32Backend.OnChatCaptureActivated = () =>
        {
            _captureText = "";
            _cursorPos   = 0;
            _historyIndex = -1;
            Dispatcher.UIThread.Post(() =>
            {
                chatInputBox.Text = "";
                chatInputBox.BorderBrush = activeBorderBrush;
            });
        };

        Win32Backend.OnChatChar = c =>
        {
            _captureText = _captureText[.._cursorPos] + c + _captureText[_cursorPos..];
            _cursorPos++;
            _historyIndex = -1;
            var (snap, pos) = (_captureText, _cursorPos);
            Dispatcher.UIThread.Post(() => { chatInputBox.Text = snap; chatInputBox.CaretIndex = pos; });
        };

        Win32Backend.OnChatBackspace = () =>
        {
            if (_cursorPos > 0)
            {
                _captureText = _captureText[..(_cursorPos - 1)] + _captureText[_cursorPos..];
                _cursorPos--;
            }
            var (snap, pos) = (_captureText, _cursorPos);
            Dispatcher.UIThread.Post(() => { chatInputBox.Text = snap; chatInputBox.CaretIndex = pos; });
        };

        Win32Backend.OnChatDelete = () =>
        {
            if (_cursorPos < _captureText.Length)
                _captureText = _captureText[.._cursorPos] + _captureText[(_cursorPos + 1)..];
            var (snap, pos) = (_captureText, _cursorPos);
            Dispatcher.UIThread.Post(() => { chatInputBox.Text = snap; chatInputBox.CaretIndex = pos; });
        };

        Win32Backend.OnChatLeft = () =>
        {
            if (_cursorPos > 0) _cursorPos--;
            var pos = _cursorPos;
            Dispatcher.UIThread.Post(() => chatInputBox.CaretIndex = pos);
        };

        Win32Backend.OnChatRight = () =>
        {
            if (_cursorPos < _captureText.Length) _cursorPos++;
            var pos = _cursorPos;
            Dispatcher.UIThread.Post(() => chatInputBox.CaretIndex = pos);
        };

        Win32Backend.OnChatHome = () =>
        {
            _cursorPos = 0;
            Dispatcher.UIThread.Post(() => chatInputBox.CaretIndex = 0);
        };

        Win32Backend.OnChatEnd = () =>
        {
            _cursorPos = _captureText.Length;
            var pos = _cursorPos;
            Dispatcher.UIThread.Post(() => chatInputBox.CaretIndex = pos);
        };

        Win32Backend.OnChatUp = () =>
        {
            if (_history.Count == 0) return;
            _historyIndex = _historyIndex < 0 ? _history.Count - 1
                          : Math.Max(0, _historyIndex - 1);
            _captureText = _history[_historyIndex];
            _cursorPos   = _captureText.Length;
            var (snap, pos) = (_captureText, _cursorPos);
            Dispatcher.UIThread.Post(() => { chatInputBox.Text = snap; chatInputBox.CaretIndex = pos; });
        };

        Win32Backend.OnChatDown = () =>
        {
            if (_historyIndex < 0) return;
            if (_historyIndex < _history.Count - 1)
            {
                _historyIndex++;
                _captureText = _history[_historyIndex];
            }
            else
            {
                _historyIndex = -1;
                _captureText  = "";
            }
            _cursorPos = _captureText.Length;
            var (snap, pos) = (_captureText, _cursorPos);
            Dispatcher.UIThread.Post(() => { chatInputBox.Text = snap; chatInputBox.CaretIndex = pos; });
        };

        // OnChatSend fires on the game thread (WndProcHook). _sendLine must be
        // called here — before Dispatcher.UIThread.Post — so SimulateChatInput's
        // CallWindowProcA runs on the game thread, not the Avalonia UI thread.
        Win32Backend.OnChatSend = () =>
        {
            string text = _captureText.Trim();
            _captureText  = "";
            _cursorPos    = 0;
            _historyIndex = -1;
            if (text.Length > 0 && (_history.Count == 0 || _history[^1] != text))
            {
                _history.Add(text);
                if (_history.Count > 100) _history.RemoveAt(0);
            }
            Dispatcher.UIThread.Post(() =>
            {
                chatInputBox.Text = "";
                chatInputBox.BorderBrush = defaultBorderBrush;
            });
            if (text.Length > 0 && _sendLine != null)
            {
                IntPtr ptr = Marshal.StringToHGlobalAnsi(text);
                try   { _sendLine(ptr); }
                finally { Marshal.FreeHGlobal(ptr); }
            }
        };

        Win32Backend.OnChatCancel = () =>
        {
            _captureText  = "";
            _cursorPos    = 0;
            _historyIndex = -1;
            Dispatcher.UIThread.Post(() =>
            {
                chatInputBox.Text = "";
                chatInputBox.BorderBrush = defaultBorderBrush;
            });
        };

        // ── Poll timer ─────────────────────────────────────────────────
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            if (_getScrollbackJson == null)
            {
                TryBind();
                if (_getScrollbackJson != null)
                    statusLabel.Text = ""; // clear "Plugin not bound"
                return;
            }

            IntPtr ptr = _getScrollbackJson(_lastSeq);
            if (ptr == IntPtr.Zero) return;
            string? json = Marshal.PtrToStringAnsi(ptr);
            if (string.IsNullOrEmpty(json) || json == "[]") return;

            RynthChatLineDto[]? dtos;
            try { dtos = JsonSerializer.Deserialize(json, RynthChatJsonContext.Default.RynthChatLineDtoArray); }
            catch { return; }
            if (dtos == null || dtos.Length == 0) return;

            foreach (var dto in dtos)
            {
                if (dto.Seq <= _lastSeq) continue;
                _lastSeq = dto.Seq;

                var line = new ChatDisplayLine(dto.Seq, dto.Ts, dto.Chan, dto.Sender, dto.Text);
                if (_allLines.Count >= 500) _allLines.RemoveAt(0);
                _allLines.Add(line);
                AppendLine(line);
                LogLine(line);
            }
        };
        timer.Start();
        // Stop with the visual tree — a running DispatcherTimer roots the closed
        // view forever (this one polls at 100ms!). RadarPanel idiom; must
        // restart on attach: drag/resize fires Detached→Attached.
        root.AttachedToVisualTree   += (_, _) => { if (!timer.IsEnabled) timer.Start(); };
        root.DetachedFromVisualTree += (_, _) => timer.Stop();

        fontSizeSlider.ValueChanged += (_, e) =>
        {
            _chatFontSize = e.NewValue;
            fontSizeLabel.Text = $"Font size: {_chatFontSize:F0}";
            RebuildDisplay();
            SaveSettings();
        };

        // Build tabs (base + custom from loaded filters), filter rows, then
        // apply loaded active-channel to tab button visuals.
        RebuildTabs();
        SelectTab(_activeChannel);

        return root;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static TextBlock MakeTextBlock(ChatDisplayLine line)
    {
        string prefix = line.Sender != null ? $"{line.Timestamp} {line.Sender}: " : $"{line.Timestamp} ";
        // Plain TextBlock, IsHitTestVisible=false: line-range selection is
        // handled at the ScrollViewer level (drag → highlight → copy on
        // release). Tag carries the formatted text for the clipboard join.
        return new TextBlock
        {
            Text         = prefix + line.Text,
            Tag          = line.FormattedText,
            FontSize     = _chatFontSize,
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
        bool ok = SetClipboardText(string.Join("\r\n", parts));
        FlashStatus(ok ? $"Copied {parts.Count} line{(parts.Count == 1 ? "" : "s")}" : "Clipboard busy — try again");
    }

    private static bool SetClipboardText(string text)
    {
        const uint CF_UNICODETEXT = 13;
        const uint GMEM_MOVEABLE  = 0x0002;
        // The clipboard is a shared resource — another app may hold it briefly.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    EmptyClipboard();
                    int bytes = (text.Length + 1) * 2;
                    IntPtr hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
                    if (hMem == IntPtr.Zero) return false;
                    IntPtr dst = GlobalLock(hMem);
                    if (dst == IntPtr.Zero) { GlobalFree(hMem); return false; }
                    unsafe
                    {
                        fixed (char* src = text)
                            Buffer.MemoryCopy(src, (void*)dst, bytes, text.Length * 2);
                        ((char*)dst)[text.Length] = '\0';
                    }
                    GlobalUnlock(hMem);
                    if (SetClipboardData(CF_UNICODETEXT, hMem) == IntPtr.Zero)
                    {
                        GlobalFree(hMem);   // ownership not taken — free it
                        return false;
                    }
                    return true;            // system owns hMem now
                }
                finally { CloseClipboard(); }
            }
            System.Threading.Thread.Sleep(10);
        }
        return false;
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

    private static void TryBind()
    {
        LoadedPlugin? plugin = PluginManager.Plugins.FirstOrDefault(
            p => p.DisplayName.Contains("RynthChat", StringComparison.OrdinalIgnoreCase));
        if (plugin == null || plugin.ModuleHandle == IntPtr.Zero) return;

        _getScrollbackJson ??= Bind<GetScrollbackJsonFn>(plugin, "RynthChatGetScrollbackJson");
        _sendLine          ??= Bind<SendLineFn>(plugin,          "RynthChatSendLine");

        if (!_bindingLogged && _getScrollbackJson != null)
        {
            _bindingLogged = true;
            RynthLog.UI("RynthChatPanel: bound RynthChat plugin exports.");
        }
    }

    private static T? Bind<T>(LoadedPlugin plugin, string exportName) where T : Delegate
    {
        IntPtr addr = GetProcAddress(plugin.ModuleHandle, exportName);
        return addr == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(addr);
    }

    // ── Chat logging helpers (Avalonia UI thread) ─────────────────────────

    private static void LogLine(ChatDisplayLine line)
    {
        if (!_logEnabled) return;
        if (_logWriter == null) EnsureLogOpen();
        _logWriter?.WriteLine(line.FormattedText);
    }

    private static void EnsureLogOpen()
    {
        if (_logWriter != null) return;
        _logCharacterName ??= TryReadCharacterName();
        if (_logCharacterName == null) return;

        string dir  = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RynthCore", "ChatLogs");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"{_logCharacterName}.log");
        _logWriter  = new StreamWriter(path, append: true, System.Text.Encoding.UTF8) { AutoFlush = true };
    }

    private static void CloseLog()
    {
        _logWriter?.Close();
        _logWriter = null;
    }

    // ── Settings persistence ──────────────────────────────────────────────

    private static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "RynthCore", "rynthchat_settings.json");

    /// <summary>Idempotent settings load — called from both the chat panel and
    /// the filters panel Create(), whichever runs first.</summary>
    internal static void EnsureSettingsLoaded()
    {
        if (_settingsLoaded) return;
        _settingsLoaded = true;
        LoadSettings();
    }

    private static void LoadSettings()
    {
        try
        {
            string path = SettingsPath;
            if (!File.Exists(path)) return;
            var dto = JsonSerializer.Deserialize(File.ReadAllText(path),
                RynthChatJsonContext.Default.RynthChatSettingsDto);
            if (dto == null) return;

            _chatFontSize    = Math.Clamp(dto.FontSize <= 0 ? 10 : dto.FontSize, 8, 18);
            _backgroundAlpha = (byte)Math.Clamp(dto.BackgroundAlpha, 0, 255);
            _autoScroll      = dto.AutoScroll;
            ChatHooks.SuppressOriginalChat = dto.SuppressChat;
            _logEnabled      = dto.LogEnabled;

            _customTabs.Clear();
            if (dto.CustomTabs != null)
                foreach (var t in dto.CustomTabs)
                    if (!string.IsNullOrWhiteSpace(t)) _customTabs.Add(t.Trim());

            _filters.Clear();
            if (dto.Filters != null)
            {
                foreach (var f in dto.Filters)
                {
                    var rule = new ChatFilterRule
                    {
                        Enabled = f.Enabled,
                        Pattern = f.Pattern ?? "",
                        Tab     = (f.Tab ?? "").Trim(),
                    };
                    rule.Recompile();
                    _filters.Add(rule);
                }
            }

            if (!string.IsNullOrEmpty(dto.ActiveChannel) &&
                (Tabs.Contains(dto.ActiveChannel) ||
                 CustomTabs().Contains(dto.ActiveChannel, StringComparer.OrdinalIgnoreCase)))
                _activeChannel = dto.ActiveChannel;
        }
        catch { }
    }

    private static void SaveSettings()
    {
        try
        {
            string path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var dto = new RynthChatSettingsDto
            {
                FontSize        = _chatFontSize,
                BackgroundAlpha = _backgroundAlpha,
                AutoScroll      = _autoScroll,
                SuppressChat    = ChatHooks.SuppressOriginalChat,
                LogEnabled      = _logEnabled,
                ActiveChannel   = _activeChannel,
                CustomTabs      = _customTabs.ToArray(),
                Filters         = _filters.Select(f => new RynthChatFilterDto
                {
                    Enabled = f.Enabled,
                    Pattern = f.Pattern,
                    Tab     = f.Tab,
                }).ToArray(),
            };
            File.WriteAllText(path, JsonSerializer.Serialize(dto,
                RynthChatJsonContext.Default.RynthChatSettingsDto));
        }
        catch { }
    }

    private static string? TryReadCharacterName()
    {
        try
        {
            string root        = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RynthCore");
            string perProcPath = Path.Combine(root, "launch_contexts", $"launch_context_{Environment.ProcessId}.json");
            string path        = File.Exists(perProcPath) ? perProcPath : Path.Combine(root, "launch_context.json");
            if (!File.Exists(path)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (doc.RootElement.TryGetProperty("TargetCharacter", out JsonElement tc))
            {
                string? name = tc.GetString();
                if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
            }
            return null;
        }
        catch { return null; }
    }

    // ── Display line (Avalonia UI thread only) ────────────────────────────

    private sealed class ChatDisplayLine
    {
        internal ulong  Seq           { get; }
        internal string Channel       { get; }
        internal string FormattedText { get; }
        internal string Text          { get; }
        internal string? Sender       { get; }
        internal string Timestamp     { get; }

        internal ChatDisplayLine(ulong seq, string ts, string chan, string? sender, string text)
        {
            Seq           = seq;
            Channel       = chan;
            Sender        = sender;
            Text          = text;
            Timestamp     = ts;
            FormattedText = sender != null ? $"{ts} {sender}: {text}" : $"{ts} {text}";
        }
    }
}

// JsonSerializerContext must be at namespace scope (not nested) for source generation.
internal sealed class RynthChatLineDto
{
    [JsonPropertyName("seq")]    public ulong Seq     { get; set; }
    [JsonPropertyName("ts")]     public string Ts     { get; set; } = "";
    [JsonPropertyName("chan")]   public string Chan    { get; set; } = "";
    [JsonPropertyName("sender")] public string? Sender { get; set; }
    [JsonPropertyName("text")]   public string Text    { get; set; } = "";
}

// Property names match the legacy hand-written JSON so existing settings load.
internal sealed class RynthChatFilterDto
{
    [JsonPropertyName("pattern")] public string? Pattern { get; set; }
    [JsonPropertyName("tab")]     public string? Tab     { get; set; }
    [JsonPropertyName("enabled")] public bool    Enabled { get; set; } = true;
}

internal sealed class RynthChatSettingsDto
{
    [JsonPropertyName("fontSize")]        public double FontSize        { get; set; } = 10;
    [JsonPropertyName("backgroundAlpha")] public int    BackgroundAlpha { get; set; } = 0xF2;
    [JsonPropertyName("autoScroll")]      public bool   AutoScroll      { get; set; } = true;
    [JsonPropertyName("suppressChat")]    public bool   SuppressChat    { get; set; }
    [JsonPropertyName("logEnabled")]      public bool   LogEnabled      { get; set; }
    [JsonPropertyName("activeChannel")]   public string? ActiveChannel  { get; set; }
    [JsonPropertyName("customTabs")]      public string[]? CustomTabs   { get; set; }
    [JsonPropertyName("filters")]         public RynthChatFilterDto[]? Filters { get; set; }
}

[JsonSerializable(typeof(RynthChatLineDto[]))]
[JsonSerializable(typeof(RynthChatSettingsDto))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
internal partial class RynthChatJsonContext : JsonSerializerContext { }
