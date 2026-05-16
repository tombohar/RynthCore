// DComp overlay — bar + free-floating panel windows.
//
// Bar: ⠿ RC | AI | Status | Log | Radar | RL
// Each panel is an independent DcompFloatingPanel (movable + resizable).
// RynthAi footer buttons (Settings, Items, Monsters, Nav, Meta) call
// AvaloniaOverlay.ActivateBarButton() which is redirected here via
// AvaloniaOverlay.DcompActivateBarButton.
// Login gate — bar stays hidden until LoginLifecycleHooks.LoginComplete.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.ImGuiBackend;
using RynthCore.Engine.UI.Panels;

namespace RynthCore.Engine.UI.Dcomp;

internal sealed class DcompOverlayWindow : Window
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLongW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    [StructLayout(LayoutKind.Sequential)] private struct RECT  { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X,  Y;  }

    // Custom bar-drag state (avoids BeginMoveDrag → Windows modal move loop →
    // BringWindowToTop which pushes the bar below panels).
    private bool       _barDragging;
    private int        _barDragStartCursorX, _barDragStartCursorY;
    private PixelPoint _barDragStartWindowPos;

    private const int  GWL_HWNDPARENT   = -8;
    private const int  GWL_EXSTYLE      = -20;
    private const int  WS_EX_NOACTIVATE = 0x08000000;
    private const int  WS_EX_TOOLWINDOW = 0x00000080;
    private const uint SWP_NOACTIVATE   = 0x0010;
    private const uint SWP_NOZORDER     = 0x0004;
    private const uint SWP_NOSIZE       = 0x0001;
    private const uint SWP_NOMOVE       = 0x0002;
    // HWND_TOP = IntPtr.Zero: puts window above all non-topmost siblings.

    private IntPtr _hostHwnd;
    private volatile bool _attached;
    private volatile bool _loginComplete;
    private IntPtr _cachedOurHwnd;

    // Bar offset from AC client top-left. Updated on user drag; preserved when AC moves.
    private int _barOffsetX = 8;
    private int _barOffsetY = 8;
    // Volatile: written on tracker thread, read on UI thread in PositionChanged.
    private volatile int _acScreenX;
    private volatile int _acScreenY;


    // ── Panel registry ────────────────────────────────────────────────────────
    private readonly Dictionary<string, DcompFloatingPanel> _panels = new();

    private readonly record struct PanelDef(Func<Control> Creator, bool WrapInScroll, int W, int H);
    private Dictionary<string, PanelDef>? _panelDefs;

    // Bar toggle buttons — keyed by panel name so TogglePanel can sync color.
    private readonly Dictionary<string, Button> _barButtons = new();

    public DcompOverlayWindow()
    {
        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        CanResize = false;
        ShowActivated = false;
        // Topmost is intentionally false: with multi-client setups, each
        // process's overlay must hide when its AC isn't foreground (handled
        // by foreground tracking in OnAcClientRectChanged). A topmost bar
        // would show across all clients simultaneously.
        Title = "RynthCore Overlay";
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(40, 40);

        Content = BuildBar();

        AcWindowTracker.ClientRectChanged += OnAcClientRectChanged;


        Opened += (_, _) =>
        {
            IntPtr hwnd = TryGetHwnd();
            if (hwnd != IntPtr.Zero)
            {
                int ex = GetWindowLongW(hwnd, GWL_EXSTYLE);
                int next = ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                if (next != ex) SetWindowLongW(hwnd, GWL_EXSTYLE, new IntPtr(next));
            }
        };

        Closed += (_, _) =>
        {
            AcWindowTracker.ClientRectChanged -= OnAcClientRectChanged;
            LoginLifecycleHooks.LoginComplete -= OnLoginComplete;
            AvaloniaOverlay.DcompActivateBarButton = null;
            DcompFloatingPanel.OnAnyPointerPressed = null;
        };
    }

    // ── Bar ───────────────────────────────────────────────────────────────────

    private Control BuildBar()
    {
        var aiBtn     = RegisterBarButton("AI");
        var statusBtn = RegisterBarButton("Status");
        var logBtn    = RegisterBarButton("Log");
        var radarBtn  = RegisterBarButton("Radar");

        var reloadBtn = MakeButton("RL");
        reloadBtn.Foreground = Brushes.Khaki;
        reloadBtn.Click += (_, _) => EngineLifecycle.SignalReload();

        var barStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(8, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = "⠿",
                    Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xAA, 0xAA, 0xAA)),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 4, 0),
                },
                new TextBlock
                {
                    Text = "RC",
                    Foreground = Brushes.LightGreen,
                    FontSize = 12,
                    FontWeight = FontWeight.Bold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                },
                aiBtn,
                statusBtn,
                logBtn,
                radarBtn,
                reloadBtn,
            },
        };

        var barBorder = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#E60A0A14")),
            CornerRadius = new CornerRadius(4),
            Child = barStack,
        };

        // Custom drag — avoids BeginMoveDrag which uses the Windows system move
        // loop (BringWindowToTop side-effect that buries the bar under panels).
        barBorder.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            if (!GetCursorPos(out POINT pt)) return;
            _barDragging = true;
            _barDragStartCursorX  = pt.X;
            _barDragStartCursorY  = pt.Y;
            _barDragStartWindowPos = Position;
            e.Pointer.Capture(barBorder);
            e.Handled = true;
        };

        barBorder.PointerMoved += (_, e) =>
        {
            if (!_barDragging || !GetCursorPos(out POINT pt)) return;
            int dx = pt.X - _barDragStartCursorX;
            int dy = pt.Y - _barDragStartCursorY;
            if (_cachedOurHwnd != IntPtr.Zero)
                SetWindowPos(_cachedOurHwnd, IntPtr.Zero,
                    _barDragStartWindowPos.X + dx, _barDragStartWindowPos.Y + dy,
                    0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        };

        barBorder.PointerReleased += (_, e) =>
        {
            if (!_barDragging) return;
            _barDragging = false;
            e.Pointer.Capture(null);
            // Sync Avalonia Position so PositionChanged-based offset update sees
            // the final dragged position (button is up, guard will skip it).
            if (GetCursorPos(out POINT pt))
            {
                int dx = pt.X - _barDragStartCursorX;
                int dy = pt.Y - _barDragStartCursorY;
                Position = new PixelPoint(_barDragStartWindowPos.X + dx, _barDragStartWindowPos.Y + dy);
                _barOffsetX = Position.X - _acScreenX;
                _barOffsetY = Position.Y - _acScreenY;
            }
        };

        barBorder.PointerCaptureLost += (_, _) => _barDragging = false;

        return barBorder;
    }

    private Button RegisterBarButton(string panelName)
    {
        var btn = MakeButton(panelName);
        _barButtons[panelName] = btn;
        btn.Click += (_, _) => TogglePanel(panelName);
        return btn;
    }

    private static Button MakeButton(string label) => new Button
    {
        Content = label,
        FontSize = 11,
        Padding = new Thickness(6, 2),
        Background = new SolidColorBrush(Color.FromArgb(0x50, 0x30, 0x60, 0xA0)),
        Foreground = Brushes.LightGray,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // ── Panel toggle ──────────────────────────────────────────────────────────

    // Called by bar buttons and by AvaloniaOverlay.DcompActivateBarButton
    // (which routes RynthAiPanel's launcher button clicks here).
    private void TogglePanel(string name)
    {
        if (_panels.TryGetValue(name, out var existing))
        {
            bool nowVisible = !existing.IsVisible;
            if (nowVisible) { existing.Show(); BringBarToFront(); }
            else existing.Hide();
            UpdateBarButtonColor(name, nowVisible);
            return;
        }

        EnsurePanelDefs();
        if (!_panelDefs!.TryGetValue(name, out var def)) return;

        // Stagger new panels so they don't all open at the exact same position.
        int stagger = _panels.Count * 24;
        var panel = new DcompFloatingPanel(
            name, _hostHwnd,
            _acScreenX + 60 + stagger, _acScreenY + 80 + stagger,
            def.W, def.H);
        _panels[name] = panel;

        Control content;
        try { content = def.Creator(); }
        catch (Exception ex)
        {
            RynthLog.UI($"DcompOverlay: {name} panel Create() threw {ex.GetType().Name}: {ex.Message}");
            content = new TextBlock
            {
                Text = $"Panel error: {ex.Message}",
                Foreground = Brushes.Tomato,
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
            };
        }

        panel.ShowWithContent(content, def.WrapInScroll);
        UpdateBarButtonColor(name, true);
        BringBarToFront();
    }

    private void UpdateBarButtonColor(string name, bool open)
    {
        if (_barButtons.TryGetValue(name, out var btn))
            btn.Foreground = open ? Brushes.LightGreen : Brushes.LightGray;
    }

    private void EnsurePanelDefs()
    {
        if (_panelDefs != null) return;
        _panelDefs = new Dictionary<string, PanelDef>
        {
            ["AI"]       = new(CreateAiContent,        false, 400, 520),
            ["Status"]   = new(StatusPanel.Create,     true,  360, 480),
            ["Log"]      = new(LogPanel.Create,        false, 500, 280),
            ["Radar"]    = new(RadarPanel.Create,      true,  420, 480),
            ["Settings"] = new(SettingsPanel.Create,   false, 560, 480),
            ["Items"]    = new(ItemsPanel.Create,      true,  460, 480),
            ["Monsters"] = new(MonstersPanel.Create,   true,  460, 480),
            ["Nav"]      = new(NavPanel.Create,        true,  420, 480),
            ["Meta"]     = new(MetaPanel.Create,       true,  460, 480),
        };
    }

    private static Control CreateAiContent()
    {
        RynthAiPanel.AttachDragHandle = null;
        RynthAiPanel.RequestPopOut   = null;
        RynthAiPanel.RequestRedock   = null;
        RynthAiPanel.IsFloatingNow   = () => false;
        return RynthAiPanel.Create();
    }

    // ── AC window tracking ────────────────────────────────────────────────────

    private void OnAcClientRectChanged(AcWindowTracker.ClientRect rect)
    {
        if (!_attached || !_loginComplete) return;

        int prevX = _acScreenX;
        int prevY = _acScreenY;
        _acScreenX = rect.ScreenX;
        _acScreenY = rect.ScreenY;
        int dx = rect.ScreenX - prevX;
        int dy = rect.ScreenY - prevY;
        int newX = rect.ScreenX + _barOffsetX;
        int newY = rect.ScreenY + _barOffsetY;

        // We never hide the bar based on rect.Visible — Win32's owner-window
        // relationship (GWL_HWNDPARENT) already auto-minimizes / auto-restores
        // owned windows when the owner does.
        if (_cachedOurHwnd != IntPtr.Zero)
        {
            SetWindowPos(_cachedOurHwnd, IntPtr.Zero, newX, newY, 0, 0,
                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            // Panels move on the UI thread (Avalonia Position is UI-thread-only).
            if (dx != 0 || dy != 0)
            {
                int capturedDx = dx, capturedDy = dy;
                Dispatcher.UIThread.Post(() => MovePanels(capturedDx, capturedDy), DispatcherPriority.Send);
            }
        }
    }

    // Restores bar above all panels without making it globally topmost.
    // Called from the diagnostic timer so even if a panel's drag briefly
    // brings it above the bar, the bar recovers within ~500 ms.
    private void BringBarToFront()
    {
        if (_cachedOurHwnd != IntPtr.Zero)
            SetWindowPos(_cachedOurHwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private void MovePanels(int dx, int dy)
    {
        foreach (var panel in _panels.Values)
        {
            if (panel.IsVisible)
                panel.MoveBy(dx, dy);
        }
    }

    // ── Attach + login gate ───────────────────────────────────────────────────

    private int _attachRetryCount;

    public void AttachToGameWindow()
    {
        if (_attached) return;

        IntPtr ourHwnd = TryGetHwnd();
        if (ourHwnd == IntPtr.Zero)
        {
            if (_attachRetryCount < 3)
                RynthLog.UI($"DcompOverlay: HWND not yet realized (retry {_attachRetryCount}).");
            _attachRetryCount++;
            DispatcherTimer.RunOnce(AttachToGameWindow, TimeSpan.FromMilliseconds(100));
            return;
        }

        IntPtr acHwnd = Win32Backend.GameHwnd;
        if (acHwnd == IntPtr.Zero)
        {
            if (_attachRetryCount % 30 == 0)
                RynthLog.UI($"DcompOverlay: waiting for GameHwnd (retry {_attachRetryCount}).");
            _attachRetryCount++;
            DispatcherTimer.RunOnce(AttachToGameWindow, TimeSpan.FromMilliseconds(100));
            return;
        }

        _hostHwnd = acHwnd;

        int ex = GetWindowLongW(ourHwnd, GWL_EXSTYLE);
        SetWindowLongW(ourHwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
        SetWindowLongW(ourHwnd, GWL_HWNDPARENT, acHwnd);
        _cachedOurHwnd = ourHwnd;
        _attached = true;

        // Route RynthAiPanel launcher button clicks to our panel toggle.
        AvaloniaOverlay.DcompActivateBarButton = name =>
            Dispatcher.UIThread.Post(() => TogglePanel(name), DispatcherPriority.Input);
        // Any click inside any panel restores bar z-order immediately.
        DcompFloatingPanel.OnAnyPointerPressed = BringBarToFront;

        RynthLog.UI($"DcompOverlay: attached to host 0x{acHwnd.ToInt64():X}, our HWND 0x{ourHwnd.ToInt64():X}.");

        if (LoginLifecycleHooks.HasObservedLoginComplete)
            ShowAfterLogin(ourHwnd, acHwnd);
        else
        {
            Hide();
            LoginLifecycleHooks.LoginComplete += OnLoginComplete;
            RynthLog.UI("DcompOverlay: waiting for LoginComplete before showing.");
        }
    }

    private void OnLoginComplete()
    {
        LoginLifecycleHooks.LoginComplete -= OnLoginComplete;
        Dispatcher.UIThread.Post(() =>
        {
            IntPtr ourHwnd = TryGetHwnd();
            if (ourHwnd != IntPtr.Zero && _hostHwnd != IntPtr.Zero)
                ShowAfterLogin(ourHwnd, _hostHwnd);
        });
    }

    private void ShowAfterLogin(IntPtr ourHwnd, IntPtr acHwnd)
    {
        _loginComplete = true;

        // Seed AC position directly — GetClientRect is reliable regardless of
        // whether AcWindowTracker has fired yet (which it may not have).
        if (GetClientRect(acHwnd, out RECT rc))
        {
            var pt = new POINT { X = rc.Left, Y = rc.Top };
            if (ClientToScreen(acHwnd, ref pt))
            {
                _acScreenX = pt.X;
                _acScreenY = pt.Y;
            }
        }
        if (_acScreenX == 0 && _acScreenY == 0)
        {
            var cur = AcWindowTracker.Current;
            _acScreenX = cur.ScreenX;
            _acScreenY = cur.ScreenY;
        }

        SetWindowPos(ourHwnd, IntPtr.Zero,
            _acScreenX + _barOffsetX, _acScreenY + _barOffsetY,
            0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

        if (!IsVisible) Show();

        if (GetForegroundWindow() != acHwnd)
        {
            SetForegroundWindow(acHwnd);
            RynthLog.UI("DcompOverlay: returned foreground to AC after login show.");
        }

        RynthLog.UI("DcompOverlay: visible after LoginComplete.");
    }

    private IntPtr TryGetHwnd()
    {
        try { return TryGetPlatformHandle()?.Handle ?? IntPtr.Zero; }
        catch { return IntPtr.Zero; }
    }
}
