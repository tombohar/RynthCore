// Free-floating overlay panel window for the DComp path.
// Wraps any Avalonia Control in a dark-themed shell with a draggable
// header and a resize grip. Never destroyed on close — just hidden.

using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace RynthCore.Engine.UI.Dcomp;

internal sealed class DcompFloatingPanel : Window
{
    [DllImport("user32.dll")] private static extern int SetWindowLongW(IntPtr h, int n, IntPtr v);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(IntPtr h, int n);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    private const int  GWL_HWNDPARENT   = -8;
    private const int  GWL_EXSTYLE      = -20;
    private const int  WS_EX_NOACTIVATE = 0x08000000;
    private const int  WS_EX_TOOLWINDOW = 0x00000080;
    private const uint SWP_NOSIZE       = 0x0001;
    private const uint SWP_NOZORDER     = 0x0004;
    private const uint SWP_NOACTIVATE   = 0x0010;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    // Set by DcompOverlayWindow to restore bar z-order on any panel click.
    // Tunnel-phase so it fires even when a child control handles the event.
    internal static Action? OnAnyPointerPressed;

    private readonly Border _contentHost;
    private readonly IntPtr _hostHwnd;
    private bool _contentSet;

    // Custom drag state — avoids BeginMoveDrag which calls BringWindowToTop
    // and pushes the bar below this panel.
    private bool       _dragging;
    private int        _dragStartCursorX, _dragStartCursorY;
    private PixelPoint _dragStartWindowPos;

    public DcompFloatingPanel(string title, IntPtr hostHwnd, int initialX, int initialY, int initialWidth, int initialHeight)
    {
        _hostHwnd = hostHwnd;

        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        CanResize = true;
        ShowActivated = false;
        Width = initialWidth;
        Height = initialHeight;
        MinWidth = 160;
        MinHeight = 80;
        SizeToContent = SizeToContent.Manual;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(initialX, initialY);
        Title = $"RC — {title}";

        _contentHost = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        Content = BuildShell(title);

        // Tunnel phase fires before any child can handle the event, so the
        // bar is always brought to front on any click inside this panel.
        this.AddHandler(PointerPressedEvent,
            (_, _) => OnAnyPointerPressed?.Invoke(),
            Avalonia.Interactivity.RoutingStrategies.Tunnel);

        Opened += OnOpened;
        Closing += (_, e) => { e.Cancel = true; Hide(); };
    }

    // Called on the Avalonia UI thread (posted by DcompOverlayWindow.MovePanels).
    internal void MoveBy(int dx, int dy)
    {
        Position = new PixelPoint(Position.X + dx, Position.Y + dy);
    }

    public void ShowWithContent(Control content, bool wrapInScroll)
    {
        if (!_contentSet)
        {
            _contentSet = true;
            if (wrapInScroll)
            {
                content = new ScrollViewer
                {
                    Content = content,
                    VerticalScrollBarVisibility  = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment   = VerticalAlignment.Stretch,
                };
            }
            _contentHost.Child = content;
        }
        Show();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        IntPtr hwnd = TryGetHwnd();
        if (hwnd == IntPtr.Zero) return;
        int ex = GetWindowLongW(hwnd, GWL_EXSTYLE);
        SetWindowLongW(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
        if (_hostHwnd != IntPtr.Zero)
            SetWindowLongW(hwnd, GWL_HWNDPARENT, _hostHwnd);
    }

    private Control BuildShell(string title)
    {
        var closeBtn = new Button
        {
            Content = "✕",
            FontSize = 11,
            Padding = new Thickness(6, 2),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xCC, 0xCC, 0xCC)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        closeBtn.Click += (_, _) => Hide();

        var titleLabel = new TextBlock
        {
            Text = title,
            Foreground = Brushes.LightGray,
            FontSize = 11,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        var header = new DockPanel
        {
            Height = 22,
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x0D, 0x0D, 0x22)),
        };
        DockPanel.SetDock(closeBtn, Dock.Right);
        header.Children.Add(closeBtn);
        header.Children.Add(titleLabel);

        // Custom drag: don't use BeginMoveDrag because the Windows system move
        // loop calls BringWindowToTop on the dragged window, pushing the bar
        // below this panel. Instead, track cursor delta via GetCursorPos and
        // apply it with SWP_NOZORDER so z-order is never touched.
        header.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            if (GetCursorPos(out POINT pt))
            {
                _dragging = true;
                _dragStartCursorX  = pt.X;
                _dragStartCursorY  = pt.Y;
                _dragStartWindowPos = Position;
                e.Pointer.Capture(header);
            }
            e.Handled = true;
        };

        header.PointerMoved += (_, e) =>
        {
            if (!_dragging) return;
            if (!GetCursorPos(out POINT pt)) return;
            int dx = pt.X - _dragStartCursorX;
            int dy = pt.Y - _dragStartCursorY;
            IntPtr hwnd = TryGetHwnd();
            if (hwnd != IntPtr.Zero)
            {
                // Move via Win32 with SWP_NOZORDER — no z-order change.
                SetWindowPos(hwnd, IntPtr.Zero,
                    _dragStartWindowPos.X + dx, _dragStartWindowPos.Y + dy,
                    0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            }
        };

        header.PointerReleased += (_, e) =>
        {
            if (!_dragging) return;
            _dragging = false;
            e.Pointer.Capture(null);
            // Sync Avalonia's Position property with the Win32 position so
            // subsequent Position reads are correct (e.g. MoveBy).
            if (GetCursorPos(out POINT pt))
            {
                int dx = pt.X - _dragStartCursorX;
                int dy = pt.Y - _dragStartCursorY;
                Position = new PixelPoint(_dragStartWindowPos.X + dx, _dragStartWindowPos.Y + dy);
            }
        };

        header.PointerCaptureLost += (_, _) => _dragging = false;

        var grip = new Border
        {
            Width = 14,
            Height = 14,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = Brushes.Transparent,
            Child = new TextBlock
            {
                Text = "⊿",
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromArgb(0x60, 0xAA, 0xAA, 0xAA)),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
            },
        };
        grip.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginResizeDrag(WindowEdge.SouthEast, e);
        };

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(grip, Dock.Bottom);
        dock.Children.Add(header);
        dock.Children.Add(grip);
        dock.Children.Add(_contentHost);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xEC, 0x07, 0x07, 0x12)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x35, 0x65, 0xBB)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            Child = dock,
        };
    }

    private IntPtr TryGetHwnd()
    {
        try { return TryGetPlatformHandle()?.Handle ?? IntPtr.Zero; }
        catch { return IntPtr.Zero; }
    }
}
