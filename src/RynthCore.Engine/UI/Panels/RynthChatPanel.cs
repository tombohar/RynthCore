// ============================================================================
//  RynthCore.Engine — UI/Panels/RynthChatPanel.cs
//  Avalonia chat-replacement panel backed by the RynthChat plugin (RynthSuite).
//
//  Phase 1: read-only display alongside retail chat (retail not yet suppressed).
//    • Per-channel tabs: All / Local / Tell / Allegiance / Fellow / System / Combat / Other
//    • Per-channel accent colors + timestamps
//    • Auto-scroll to tail
//    • Search/filter TextBox (wired; typing requires mouse hover — Phase 4 fix)
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
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.ImGuiBackend;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.UI.Panels;

internal static class RynthChatPanel
{
    // ── P/Invoke ──────────────────────────────────────────────────────────

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

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

    // ── Panel construction ────────────────────────────────────────────────

    internal static Control Create()
    {
        LoadSettings();
        TryBind();

        // ── Tab strip with gear button ─────────────────────────────────
        var tabButtons = new Dictionary<string, Button>();
        var tabInner = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var tab in Tabs)
        {
            var btn = new Button
            {
                Content = tab,
                FontSize = 9,
                Padding = new Thickness(5, 2),
                Margin  = new Thickness(1, 1, 0, 0),
                Background = tab == "All"
                    ? new SolidColorBrush(Color.FromArgb(0xFF, 0x26, 0x4C, 0x59))
                    : new SolidColorBrush(Color.FromArgb(0xFF, 0x0F, 0x1F, 0x2E)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
            };
            tabButtons[tab] = btn;
            tabInner.Children.Add(btn);
        }
        var gearBtn = new Button
        {
            Content = "⚙",
            FontSize = 10,
            Padding = new Thickness(5, 2),
            Margin  = new Thickness(0, 1, 2, 0),
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x0F, 0x1F, 0x2E)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
        };
        var tabStrip = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(gearBtn, Dock.Right);
        tabStrip.Children.Add(gearBtn);
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

        // ── Scrollback list ────────────────────────────────────────────
        var chatStack = new StackPanel { Orientation = Orientation.Vertical };
        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = chatStack,
        };

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
        fontSizeSlider.ValueChanged += (_, e) =>
        {
            _chatFontSize = e.NewValue;
            fontSizeLabel.Text = $"Font size: {_chatFontSize:F0}";
            RebuildDisplay();
            SaveSettings();
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

        // ── Root (Grid lets settingsOverlay float above main layout) ───
        var root = new Grid();
        root.Children.Add(mainLayout);
        root.Children.Add(settingsOverlay);

        // ── Helpers ────────────────────────────────────────────────────

        void RebuildDisplay()
        {
            chatStack.Children.Clear();
            string filter = _searchFilter;
            foreach (var line in _allLines)
            {
                if (_activeChannel != "All" && line.Channel != _activeChannel) continue;
                if (filter.Length > 0 && !line.FormattedText.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                chatStack.Children.Add(MakeTextBlock(line));
            }
            // A full rebuild (tab/filter/font change) re-pins to the newest line.
            if (autoScrollCheck.IsChecked == true)
            {
                _stickToBottom = true;
                scrollViewer.ScrollToEnd();
            }
        }

        void SelectTab(string tab)
        {
            RynthLog.Info($"[RynthChat] SelectTab({tab}) called.");
            _activeChannel = tab;
            foreach (var kv in tabButtons)
            {
                kv.Value.Background = kv.Key == tab
                    ? new SolidColorBrush(Color.FromArgb(0xFF, 0x26, 0x4C, 0x59))
                    : new SolidColorBrush(Color.FromArgb(0xFF, 0x0F, 0x1F, 0x2E));
            }
            RebuildDisplay();
            SaveSettings();
        }

        void AppendLine(ChatDisplayLine line)
        {
            if (_activeChannel != "All" && line.Channel != _activeChannel) return;
            string filter = _searchFilter;
            if (filter.Length > 0 && !line.FormattedText.Contains(filter, StringComparison.OrdinalIgnoreCase)) return;
            chatStack.Children.Add(MakeTextBlock(line));
            if (autoScrollCheck.IsChecked == true && _stickToBottom)
            {
                // Following the tail: trim backlog from the top and keep the newest line in view.
                while (chatStack.Children.Count > 500)
                    chatStack.Children.RemoveAt(0);
                scrollViewer.ScrollToEnd();
            }
            else
            {
                // User scrolled up to read — don't yank the view or shift it by trimming
                // from the top. Allow a bounded backlog; the next rebuild resyncs to 500.
                while (chatStack.Children.Count > 1000)
                    chatStack.Children.RemoveAt(0);
            }
        }

        // ── Event wiring ───────────────────────────────────────────────
        foreach (var kv in tabButtons)
        {
            var tab = kv.Key;
            kv.Value.Click += (_, _) => SelectTab(tab);
        }

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

        // Apply loaded active-channel to tab button visuals.
        SelectTab(_activeChannel);

        return root;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static SelectableTextBlock MakeTextBlock(ChatDisplayLine line)
    {
        string prefix = line.Sender != null ? $"{line.Timestamp} {line.Sender}: " : $"{line.Timestamp} ";
        return new SelectableTextBlock
        {
            Text         = prefix + line.Text,
            FontSize     = _chatFontSize,
            FontFamily   = new FontFamily("Consolas,Courier New,monospace"),
            Foreground   = new SolidColorBrush(ChannelColor(line.Channel)),
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(3, 0, 3, 0),
        };
    }

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

    private static void LoadSettings()
    {
        try
        {
            string path = SettingsPath;
            if (!File.Exists(path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            if (r.TryGetProperty("fontSize",        out var v)) _chatFontSize       = Math.Clamp(v.GetDouble(), 8, 18);
            if (r.TryGetProperty("backgroundAlpha", out v))     _backgroundAlpha    = (byte)Math.Clamp(v.GetInt32(), 0, 255);
            if (r.TryGetProperty("autoScroll",      out v))     _autoScroll         = v.GetBoolean();
            if (r.TryGetProperty("suppressChat",    out v))     ChatHooks.SuppressOriginalChat = v.GetBoolean();
            if (r.TryGetProperty("logEnabled",      out v))     _logEnabled         = v.GetBoolean();
            if (r.TryGetProperty("activeChannel",   out v))
            {
                string? chan = v.GetString();
                if (chan != null && Tabs.Contains(chan)) _activeChannel = chan;
            }
        }
        catch { }
    }

    private static void SaveSettings()
    {
        try
        {
            string path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string fs   = _chatFontSize.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            string json = "{"
                + $"\"fontSize\":{fs},"
                + $"\"backgroundAlpha\":{_backgroundAlpha},"
                + $"\"autoScroll\":{(_autoScroll ? "true" : "false")},"
                + $"\"suppressChat\":{(ChatHooks.SuppressOriginalChat ? "true" : "false")},"
                + $"\"logEnabled\":{(_logEnabled ? "true" : "false")},"
                + $"\"activeChannel\":\"{_activeChannel}\""
                + "}";
            File.WriteAllText(path, json);
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

[JsonSerializable(typeof(RynthChatLineDto[]))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
internal partial class RynthChatJsonContext : JsonSerializerContext { }
