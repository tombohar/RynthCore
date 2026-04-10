// ============================================================================
//  RynthCore.Engine - UI/AvaloniaOverlay.cs
//  Refactored: Single transparent overlay window hosting multiple draggable
//  internal "windows" for each plugin.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.Themes.Simple;
using Avalonia.Styling;
using Avalonia.Win32;
using Avalonia.VisualTree;

namespace RynthCore.Engine.UI;

internal static class AvaloniaOverlay
{
    private static readonly OverlaySkiaPlatformGraphics CustomPlatformGraphics = new();
    private readonly struct PhysicalRect
    {
        public PhysicalRect(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public int Left { get; }
        public int Top { get; }
        public int Right { get; }
        public int Bottom { get; }

        public bool Contains(int x, int y) => x >= Left && x <= Right && y >= Top && y <= Bottom;
    }

    private static Thread? _thread;
    private static volatile bool _started;
    private static RynthOverlayWindow? _window;
    private static readonly object HitTestLock = new();
    private static PhysicalRect[] _panelRects = Array.Empty<PhysicalRect>();
    private static Dictionary<string, PhysicalRect> _barButtonRects = new(StringComparer.OrdinalIgnoreCase);

    internal static IntPtr AvaloniaHwnd { get; private set; }
    internal static volatile int AvaloniaWindowLeft;
    internal static volatile int AvaloniaWindowTop;

    // --- Win32Backend hit-testing and dragging ---
    internal static volatile int PanelPhysLeft;
    internal static volatile int PanelPhysTop;
    internal static volatile int PanelPhysRight;
    internal static volatile int PanelPhysBottom;
    internal static volatile bool HasPointerCapture;
    internal static volatile bool IsDragInProgress;
    internal static volatile int DragOffsetX;
    internal static volatile int DragOffsetY;
    internal static volatile bool DragCommitPending;
    internal static volatile bool DragCommitFrameReady;

    /// <summary>
    /// Game viewport dimensions written by OverlayTextureRenderer each frame.
    /// The Avalonia canvas resizes to match so the texture is never stretched.
    /// </summary>
    internal static volatile int ViewportWidth;
    internal static volatile int ViewportHeight;
    internal static volatile int ClientPixelWidth;
    internal static volatile int ClientPixelHeight;
    internal static volatile int SurfacePixelWidth;
    internal static volatile int SurfacePixelHeight;
    internal static volatile float InputScale = 1f;
    internal static bool UseAnglePreferredBridge { get; private set; }
    internal static bool UseCustomSkiaBridge { get; private set; }
    internal static bool ShouldUseCustomSkiaProducer => UseCustomSkiaBridge;

    internal static bool IsOverPanel(int gameClientX, int gameClientY)
    {
        if (IsOverBarInteractiveZone(gameClientX, gameClientY))
            return true;

        lock (HitTestLock)
        {
            foreach (PhysicalRect rect in _panelRects)
            {
                if (rect.Contains(gameClientX, gameClientY))
                    return true;
            }
        }

        return false;
    }

    private static bool IsOverBarInteractiveZone(int gameClientX, int gameClientY)
    {
        if (gameClientX < PanelPhysLeft || gameClientX > PanelPhysRight ||
            gameClientY < PanelPhysTop || gameClientY > PanelPhysBottom)
        {
            return false;
        }

        // Keep the draggable hot zone intentionally narrow so the Avalonia bar
        // doesn't swallow clicks meant for the ImGui shell under it.
        if (gameClientX <= PanelPhysLeft + 36)
        {
            return true;
        }

        lock (HitTestLock)
        {
            foreach (PhysicalRect rect in _barButtonRects.Values)
            {
                if (rect.Contains(gameClientX, gameClientY))
                    return true;
            }
        }

        return false;
    }

    internal static bool TryGetBarButtonTitleAt(int gameClientX, int gameClientY, out string? title)
    {
        lock (HitTestLock)
        {
            foreach ((string buttonTitle, PhysicalRect rect) in _barButtonRects)
            {
                if (!rect.Contains(gameClientX, gameClientY))
                    continue;

                title = buttonTitle;
                return true;
            }
        }

        title = null;
        return false;
    }

    internal static void RequestCapture()
    {
        Dispatcher.UIThread.Post(() => _window?.RequestCapture(), DispatcherPriority.Input);
    }

    internal static void ActivateBarButton(string title)
    {
        Dispatcher.UIThread.Post(() => _window?.ActivateBarButton(title), DispatcherPriority.Input);
    }

    internal static void CommitDrag(int x, int y)
    {
        Dispatcher.UIThread.Post(() => _window?.CommitBarDrag(x, y), DispatcherPriority.Render);
    }

    internal static void MoveBarByPhys(int x, int y)
    {
        Dispatcher.UIThread.Post(() => _window?.MoveBarByPhys(x, y), DispatcherPriority.Input);
    }

    internal static void ResizePanelByPhys(int w, int h)
    {
        Dispatcher.UIThread.Post(() => _window?.ResizeShellByPhys(w, h), DispatcherPriority.Input);
    }

    internal static void NotifyGameSurfaceMetricsChanged()
    {
        if (!UseCustomSkiaBridge)
            return;

        Dispatcher.UIThread.Post(() => _window?.OnGameSurfaceMetricsChanged(), DispatcherPriority.Render);
    }

    internal static void NotifyCustomFrameSubmitted(int width, int height)
    {
        SurfacePixelWidth = width;
        SurfacePixelHeight = height;

        if (!ShouldUseCustomSkiaProducer)
            return;

        Dispatcher.UIThread.Post(() => _window?.OnCustomFrameSubmitted(width, height), DispatcherPriority.Render);
    }

    internal static void PublishPanelRects(params (double Left, double Top, double Width, double Height)[] rects)
    {
        var snapshots = new PhysicalRect[rects.Length];
        for (int i = 0; i < rects.Length; i++)
        {
            snapshots[i] = new PhysicalRect(
                (int)Math.Round(rects[i].Left),
                (int)Math.Round(rects[i].Top),
                (int)Math.Round(rects[i].Left + rects[i].Width),
                (int)Math.Round(rects[i].Top + rects[i].Height));
        }

        lock (HitTestLock)
            _panelRects = snapshots;
    }

    internal static void PublishBarButtonRects(Dictionary<string, (double Left, double Top, double Width, double Height)> rects)
    {
        const int HitSlop = 4;
        var snapshots = new Dictionary<string, PhysicalRect>(rects.Count, StringComparer.OrdinalIgnoreCase);
        foreach ((string title, (double Left, double Top, double Width, double Height) rect) in rects)
        {
            snapshots[title] = new PhysicalRect(
                (int)Math.Round(rect.Left) - HitSlop,
                (int)Math.Round(rect.Top) - HitSlop,
                (int)Math.Round(rect.Left + rect.Width) + HitSlop,
                (int)Math.Round(rect.Top + rect.Height) + HitSlop);
        }

        lock (HitTestLock)
            _barButtonRects = snapshots;
    }

    internal static string DescribeHitTestState(int gameClientX, int gameClientY)
    {
        lock (HitTestLock)
        {
            string hitButton = "none";
            string hitButtonRect = "none";
            foreach ((string title, PhysicalRect rect) in _barButtonRects)
            {
                if (!rect.Contains(gameClientX, gameClientY))
                    continue;

                hitButton = title;
                hitButtonRect = $"[{rect.Left},{rect.Top}]-[{rect.Right},{rect.Bottom}]";
                break;
            }

            return $"point=({gameClientX},{gameClientY}) scale={InputScale:0.###} bar=[{PanelPhysLeft},{PanelPhysTop}]-[{PanelPhysRight},{PanelPhysBottom}] button={hitButton} buttonRect={hitButtonRect}";
        }
    }

    public static void Start()
    {
        if (_started) return;
        _started = true;
        _thread = new Thread(ThreadMain) { Name = "RynthCore.Avalonia", IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private static void ThreadMain()
    {
        try
        {
            UseAnglePreferredBridge = !string.Equals(
                Environment.GetEnvironmentVariable("RYNTHCORE_DISABLE_ANGLE_BRIDGE"),
                "1",
                StringComparison.Ordinal);
            bool customBridgeRequested = string.Equals(
                Environment.GetEnvironmentVariable("RYNTHCORE_EXPERIMENTAL_SKIA_BRIDGE"),
                "1",
                StringComparison.Ordinal);
            bool customBridgeDisabled = string.Equals(
                Environment.GetEnvironmentVariable("RYNTHCORE_DISABLE_CUSTOM_SKIA_BRIDGE"),
                "1",
                StringComparison.Ordinal);
            UseCustomSkiaBridge = customBridgeRequested || (UseAnglePreferredBridge && !customBridgeDisabled);

            if (UseAnglePreferredBridge)
                OverlaySurfaceBridge.UseAngleStub();
            else
                OverlaySurfaceBridge.UseSoftwareFallback();

            var platformOptions = new Win32PlatformOptions
            {
                CompositionMode =
                [
                    Win32CompositionMode.RedirectionSurface
                ],
                ShouldRenderOnUIThread = true
            };

            if (UseCustomSkiaBridge)
            {
                platformOptions.CustomPlatformGraphics = CustomPlatformGraphics;
            }
            else
            {
                platformOptions.RenderingMode =
                [
                    Win32RenderingMode.Software
                ];
            }

            AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .With(platformOptions)
                .AfterSetup(_ => {
                    var app = (App)Application.Current!;
                    if (UseCustomSkiaBridge)
                    {
                        RynthLog.UI(
                            UseAnglePreferredBridge
                                ? "AvaloniaOverlay: Custom Skia producer is active over the ANGLE-preferred hybrid bridge."
                                : "AvaloniaOverlay: Custom Skia producer is active over the software bridge.");
                    }
                    else if (UseAnglePreferredBridge)
                    {
                        RynthLog.UI("AvaloniaOverlay: Software renderer is active over the ANGLE-preferred hybrid bridge.");
                    }
                    else
                    {
                        RynthLog.UI("AvaloniaOverlay: Win32 rendering pinned to Software/RedirectionSurface while GPU interop is under construction.");
                    }
                    app.OnStartup();
                })
                .StartWithClassicDesktopLifetime(Array.Empty<string>());
        }
        catch (Exception ex)
        {
            RynthLog.UI($"AvaloniaOverlay: Thread error: {ex.Message}");
        }
    }

    internal static void RegisterWindow(RynthOverlayWindow window) => _window = window;
    internal static void SetHwnd(IntPtr hwnd) => AvaloniaHwnd = hwnd;
}

public class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new SimpleTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public void OnStartup()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new RynthOverlayWindow();
            desktop.MainWindow = window;
            window.Show();
        }
    }
}

internal class RynthOverlayWindow : Window
{
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private const int  GWL_EXSTYLE      = -20;
    private const int  WS_EX_NOACTIVATE = 0x08000000;
    private const int  WS_EX_TOOLWINDOW = 0x00000080;
    private const uint SWP_NOMOVE       = 0x0002;
    private const uint SWP_NOSIZE       = 0x0001;
    private const uint SWP_NOACTIVATE   = 0x0010;
    private const uint SWP_NOZORDER     = 0x0004;

    private Canvas _desktopCanvas = new();
    private RenderTargetBitmap? _rtt;
    private byte[]? _softwareBuffer;
    private DispatcherTimer _captureTimer;
    private Dictionary<string, Border> _activePanels = new();
    private readonly Dictionary<string, Func<Control>> _registeredPanels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Button> _barButtons = new(StringComparer.OrdinalIgnoreCase);
    private Border? _barBorder;
    private StackPanel? _barStack;
    private bool _captureDirty = true;
    private bool _captureQueued;
    private bool _awaitingCustomFrame;
    private long _customFrameWaitStartedAt;
    private bool _loggedCustomFallback;
    private bool _hasPresentedFrame;

    public RynthOverlayWindow()
    {
        Title = "RynthCore Overlay";
        WindowStartupLocation = WindowStartupLocation.Manual;

        // Prevent the overlay from stealing focus from the game window.
        ShowActivated  = false;
        ShowInTaskbar  = false;

        // Default to 1920×1080; CaptureNow resizes to the actual game viewport
        // once OverlayTextureRenderer starts reporting ViewportWidth/Height.
        Width  = 800;
        Height = 600;

        // Live off-screen — the D3D9 compositor blits the captured pixels in-game.
        AvaloniaOverlay.AvaloniaWindowLeft = -10000;
        AvaloniaOverlay.AvaloniaWindowTop  = -10000;
        Position = new PixelPoint(AvaloniaOverlay.AvaloniaWindowLeft, AvaloniaOverlay.AvaloniaWindowTop);

        SystemDecorations = SystemDecorations.None;
        Background = Brushes.Transparent;
        TransparencyBackgroundFallback = Brushes.Transparent;
        TransparencyLevelHint = new[]
        {
            WindowTransparencyLevel.Transparent
        };
        Topmost = true;

        Content = BuildRoot();

        Opened += (s, e) =>
        {
            try
            {
                // Get the native HWND via the platform handle.
                var hwnd = TryGetHwnd();
                if (hwnd != IntPtr.Zero)
                {
                    AvaloniaOverlay.SetHwnd(hwnd);

                    // WS_EX_NOACTIVATE: clicking the off-screen window never steals focus.
                    // WS_EX_TOOLWINDOW: hides it from Alt+Tab.
                    int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                    SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

                    // Re-apply position without activating.
                    SetWindowPos(hwnd, IntPtr.Zero, -10000, -10000, 0, 0,
                        SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                }
            }
            catch { }
        };

        AvaloniaOverlay.RegisterWindow(this);

        _captureTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
        {
            if (_captureDirty)
            {
                if (AvaloniaOverlay.ShouldUseCustomSkiaProducer)
                {
                    RenderCustomProducerFrame();
                    if (ShouldUseSoftwareFallbackCapture())
                        CaptureSoftwareFallbackNow();
                }
                else
                    CaptureSoftwareFallbackNow();
            }
        });
        _captureTimer.Start();
        if (AvaloniaOverlay.ShouldUseCustomSkiaProducer)
            Dispatcher.UIThread.Post(OnGameSurfaceMetricsChanged, DispatcherPriority.Render);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private IntPtr TryGetHwnd()
    {
        try
        {
            // Avalonia 11: TopLevel.TryGetPlatformHandle() is the supported path.
            var handle = (this as TopLevel)?.TryGetPlatformHandle();
            if (handle != null) return handle.Handle;
        }
        catch { }

        try
        {
            // Fallback: reflection through PlatformImpl for older builds.
            var pi = typeof(Window).GetProperty("PlatformImpl",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var p = pi?.GetValue(this);
            if (p == null) return IntPtr.Zero;
            var hi = p.GetType().GetProperty("Handle");
            var handleVal = hi?.GetValue(p);
            if (handleVal == null) return IntPtr.Zero;
            var hpi = handleVal.GetType().GetProperty("Handle");
            var h   = hpi?.GetValue(handleVal);
            if (h is IntPtr ptr) return ptr;
        }
        catch { }

        return IntPtr.Zero;
    }

    private Control BuildRoot()
    {
        var registeredPanels = OverlayHost.GetPanels();

        _registeredPanels.Clear();
        _barButtons.Clear();

        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(8, 4) };
        _barStack = stack;
        stack.Children.Add(new TextBlock { Text = "RC", FontWeight = FontWeight.Bold, Foreground = Brushes.LightGreen, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,10,0) });

        foreach (var p in registeredPanels)
        {
            _registeredPanels[p.Title] = p.Factory;
            var btn = new Button { Content = p.Title, FontSize = 11, Padding = new Thickness(10, 2) };
            btn.Click += (_, _) => ActivateBarButton(p.Title);
            stack.Children.Add(btn);
            _barButtons[p.Title] = btn;
        }

        _barBorder = new Border {
            Background = new SolidColorBrush(Color.Parse("#E60A0A14")), 
            CornerRadius = new CornerRadius(4), 
            Child = stack 
        };

        // Update physical bar bounds for dragging (fixed position in this version)
        AvaloniaOverlay.PanelPhysLeft = 50;
        AvaloniaOverlay.PanelPhysTop = 5;
        AvaloniaOverlay.PanelPhysRight = 50 + 400; // rough estimate
        AvaloniaOverlay.PanelPhysBottom = 5 + 40;

        _desktopCanvas = new Canvas
        {
            Background = Brushes.Transparent,
            Width  = Width,
            Height = Height
        };
        _desktopCanvas.LayoutUpdated += (_, _) => RefreshHitTestSnapshot();
        _desktopCanvas.Children.Add(_barBorder);
        Canvas.SetLeft(_barBorder, AvaloniaOverlay.PanelPhysLeft);
        Canvas.SetTop(_barBorder, AvaloniaOverlay.PanelPhysTop);
        RefreshHitTestSnapshot();

        return _desktopCanvas;
    }

    internal void RequestCapture()
    {
        if (AvaloniaOverlay.ShouldUseCustomSkiaProducer)
        {
            RequestFrameRefresh();
            return;
        }

        _captureDirty = true;
        QueueCapture();
    }

    internal void ActivateBarButton(string title)
    {
        if (_registeredPanels.TryGetValue(title, out Func<Control>? factory))
        {
            TogglePanel(title, factory);
            return;
        }
    }

    private void TogglePanel(string title, Func<Control> factory)
    {
        if (_activePanels.TryGetValue(title, out var existing))
        {
            ClosePanel(title, existing);
        }
        else
        {
            bool isRynthAi = string.Equals(title, "RynthAi", StringComparison.OrdinalIgnoreCase);
            var panelContent = factory();
            var windowFrame = new Border
            {
                Width = isRynthAi ? 430 : 400,
                Height = isRynthAi ? 350 : 500,
                MinWidth = isRynthAi ? 360 : 320,
                MinHeight = isRynthAi ? 260 : 240,
                Background = new SolidColorBrush(Color.Parse("#D212121C")),
                BorderBrush = Brushes.Teal,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                ClipToBounds = true
            };

            var grid = new Grid { RowDefinitions = new RowDefinitions("24, *") };
            var dragGlyph = new TextBlock
            {
                Text = "DRAG",
                FontSize = 9,
                Margin = new Thickness(8, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.Teal
            };
            var headerTitle = new TextBlock
            {
                Text = title,
                FontSize = 10,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var closeButton = new Button
            {
                Content = "X",
                Width = 20,
                Height = 20,
                FontSize = 10,
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 4, 2)
            };
            closeButton.PointerPressed += (_, e) => e.Handled = true;
            closeButton.Click += (_, _) => ClosePanel(title, windowFrame);

            var dragSurface = new Border
            {
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var dragGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto, *")
            };
            dragGrid.Children.Add(dragGlyph);
            dragGrid.Children.Add(headerTitle);
            Grid.SetColumn(dragGlyph, 0);
            Grid.SetColumn(headerTitle, 1);
            dragSurface.Child = dragGrid;

            var headerGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*, Auto")
            };
            headerGrid.Children.Add(dragSurface);
            headerGrid.Children.Add(closeButton);
            Grid.SetColumn(dragSurface, 0);
            Grid.SetColumn(closeButton, 1);

            var header = new Border {
                Background = new SolidColorBrush(Color.Parse("#FF1A1A28")),
                Child = headerGrid
            };

            Point dragStartCanvas = default;
            double dragStartLeft = 0;
            double dragStartTop = 0;
            bool isDragging = false;
            double resizeStartWidth = 0;
            double resizeStartHeight = 0;
            bool isResizing = false;

            dragSurface.PointerPressed += (s, e) => {
                var props = e.GetCurrentPoint(dragSurface).Properties;
                if (props.IsLeftButtonPressed)
                {
                    BringPanelToFront(title, windowFrame);
                    dragStartCanvas = e.GetPosition(_desktopCanvas);
                    dragStartLeft = GetCanvasLeft(windowFrame);
                    dragStartTop = GetCanvasTop(windowFrame);
                    isDragging = true;
                    e.Pointer.Capture(dragSurface);
                    e.Handled = true;
                }
            };
            dragSurface.PointerMoved += (s, e) => {
                var props = e.GetCurrentPoint(dragSurface).Properties;
                if (props.IsLeftButtonPressed && isDragging && e.Pointer.Captured == dragSurface)
                {
                    Point currentCanvas = e.GetPosition(_desktopCanvas);
                    double nextLeft = dragStartLeft + (currentCanvas.X - dragStartCanvas.X);
                    double nextTop = dragStartTop + (currentCanvas.Y - dragStartCanvas.Y);
                    SetPanelPosition(windowFrame, nextLeft, nextTop);
                    RefreshHitTestSnapshot();
                    RequestFrameRefresh();
                    e.Handled = true;
                }
            };
            dragSurface.PointerReleased += (s, e) => {
                if (e.Pointer.Captured == dragSurface)
                {
                    e.Pointer.Capture(null);
                    isDragging = false;
                    RefreshHitTestSnapshot();
                    RequestFrameRefresh();
                    e.Handled = true;
                }
            };
            dragSurface.PointerCaptureLost += (_, _) => isDragging = false;

            var body = new Border { Child = panelContent };
            var resizeGlyph = new TextBlock
            {
                Text = "//",
                FontSize = 9,
                Foreground = Brushes.Teal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            var resizeGrip = new Border
            {
                Width = 18,
                Height = 18,
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 2, 2),
                Child = resizeGlyph
            };
            resizeGrip.PointerPressed += (s, e) =>
            {
                var props = e.GetCurrentPoint(resizeGrip).Properties;
                if (!props.IsLeftButtonPressed)
                    return;

                BringPanelToFront(title, windowFrame);
                resizeStartWidth = windowFrame.Width;
                resizeStartHeight = windowFrame.Height;
                dragStartCanvas = e.GetPosition(_desktopCanvas);
                isResizing = true;
                e.Pointer.Capture(resizeGrip);
                e.Handled = true;
            };
            resizeGrip.PointerMoved += (s, e) =>
            {
                var props = e.GetCurrentPoint(resizeGrip).Properties;
                if (!props.IsLeftButtonPressed || !isResizing || e.Pointer.Captured != resizeGrip)
                    return;

                Point currentCanvas = e.GetPosition(_desktopCanvas);
                double requestedWidth = resizeStartWidth + (currentCanvas.X - dragStartCanvas.X);
                double requestedHeight = resizeStartHeight + (currentCanvas.Y - dragStartCanvas.Y);
                SetPanelSize(windowFrame, requestedWidth, requestedHeight);
                SetPanelPosition(windowFrame, GetCanvasLeft(windowFrame), GetCanvasTop(windowFrame));
                RefreshHitTestSnapshot();
                RequestFrameRefresh();
                e.Handled = true;
            };
            resizeGrip.PointerReleased += (s, e) =>
            {
                if (e.Pointer.Captured != resizeGrip)
                    return;

                e.Pointer.Capture(null);
                isResizing = false;
                RefreshHitTestSnapshot();
                RequestFrameRefresh();
                e.Handled = true;
            };
            resizeGrip.PointerCaptureLost += (_, _) => isResizing = false;
            
            grid.Children.Add(header);
            grid.Children.Add(body);
            grid.Children.Add(resizeGrip);
            Grid.SetRow(header, 0);
            Grid.SetRow(body, 1);
            Grid.SetRow(resizeGrip, 1);

            windowFrame.Child = grid;
            windowFrame.PointerPressed += (_, _) => BringPanelToFront(title, windowFrame);
            _desktopCanvas.Children.Add(windowFrame);
            SetPanelPosition(windowFrame, 100 + (_activePanels.Count * 20), 100 + (_activePanels.Count * 20));
            _activePanels[title] = windowFrame;
        }

        RefreshHitTestSnapshot();
        RequestFrameRefresh();
    }

    private void ClosePanel(string title, Border panel)
    {
        _desktopCanvas.Children.Remove(panel);
        _activePanels.Remove(title);
        RefreshHitTestSnapshot();
        RequestFrameRefresh();
    }

    private void BringPanelToFront(string title, Border panel)
    {
        if (!_desktopCanvas.Children.Contains(panel))
            return;

        _desktopCanvas.Children.Remove(panel);
        _desktopCanvas.Children.Add(panel);

        if (_activePanels.Remove(title))
            _activePanels[title] = panel;

        RefreshHitTestSnapshot();
        RequestFrameRefresh();
    }

    private static double GetCanvasLeft(Control control)
    {
        double left = Canvas.GetLeft(control);
        return double.IsNaN(left) ? 0 : left;
    }

    private static double GetCanvasTop(Control control)
    {
        double top = Canvas.GetTop(control);
        return double.IsNaN(top) ? 0 : top;
    }

    private void SetPanelPosition(Border panel, double requestedLeft, double requestedTop)
    {
        double width = panel.Bounds.Width > 0 ? panel.Bounds.Width : panel.Width;
        double height = panel.Bounds.Height > 0 ? panel.Bounds.Height : panel.Height;
        double maxLeft = Math.Max(0, _desktopCanvas.Bounds.Width - width);
        double maxTop = Math.Max(0, _desktopCanvas.Bounds.Height - height);
        Canvas.SetLeft(panel, Math.Clamp(requestedLeft, 0, maxLeft));
        Canvas.SetTop(panel, Math.Clamp(requestedTop, 0, maxTop));
    }

    private void SetPanelSize(Border panel, double requestedWidth, double requestedHeight)
    {
        double currentLeft = GetCanvasLeft(panel);
        double currentTop = GetCanvasTop(panel);
        double maxWidth = _desktopCanvas.Bounds.Width > 0
            ? Math.Max(panel.MinWidth, _desktopCanvas.Bounds.Width - currentLeft)
            : requestedWidth;
        double maxHeight = _desktopCanvas.Bounds.Height > 0
            ? Math.Max(panel.MinHeight, _desktopCanvas.Bounds.Height - currentTop)
            : requestedHeight;

        panel.Width = Math.Clamp(requestedWidth, panel.MinWidth, maxWidth);
        panel.Height = Math.Clamp(requestedHeight, panel.MinHeight, maxHeight);
    }

    internal unsafe void CaptureNow()
    {
        if (AvaloniaOverlay.ShouldUseCustomSkiaProducer)
        {
            RenderCustomProducerFrame();
            return;
        }

        CaptureSoftwareFallbackNow();
    }

    internal unsafe void CaptureSoftwareFallbackNow()
    {
        if (!SyncToGameSurfaceSize())
            return;

        RefreshHitTestSnapshot();

        if (!TryGetTargetGameSurfacePixels(out int w, out int h))
            return;

        if (w <= 0 || h <= 0) return;

        if (_rtt == null || _rtt.PixelSize.Width != w || _rtt.PixelSize.Height != h)
        {
            double scale = GetEffectiveRenderScale();
            _rtt = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96 * scale, 96 * scale));
        }

        _rtt.Render(_desktopCanvas);

        int byteCount = w * h * 4;
        if (_softwareBuffer == null || _softwareBuffer.Length != byteCount)
            _softwareBuffer = new byte[byteCount];

        AvaloniaOverlay.SurfacePixelWidth = w;
        AvaloniaOverlay.SurfacePixelHeight = h;
        
        fixed (byte* pBuf = _softwareBuffer) {
            _rtt.CopyPixels(new PixelRect(0, 0, w, h), (IntPtr)pBuf, byteCount, w * 4);
            OverlaySurfaceBridge.SubmitSoftwareFrame((IntPtr)pBuf, byteCount, w, h);
        }

        _hasPresentedFrame = true;
        _captureDirty = false;
    }

    internal void CommitBarDrag(int deltaX, int deltaY)
    {
        if (_barBorder == null)
            return;

        double nextLeft = Canvas.GetLeft(_barBorder) + deltaX;
        double nextTop = Canvas.GetTop(_barBorder) + deltaY;
        Canvas.SetLeft(_barBorder, nextLeft);
        Canvas.SetTop(_barBorder, nextTop);
        AvaloniaOverlay.IsDragInProgress = false;
        RefreshHitTestSnapshot();
        RequestFrameRefresh();
    }

    internal void MoveBarByPhys(int deltaX, int deltaY)
    {
        if (_barBorder == null)
            return;

        double nextLeft = Canvas.GetLeft(_barBorder) + deltaX;
        double nextTop = Canvas.GetTop(_barBorder) + deltaY;
        Canvas.SetLeft(_barBorder, nextLeft);
        Canvas.SetTop(_barBorder, nextTop);
        RefreshHitTestSnapshot();
        RequestFrameRefresh();
    }

    internal void ResizeShellByPhys(int deltaWidth, int deltaHeight)
    {
        Width = Math.Max(320, Width + deltaWidth);
        Height = Math.Max(200, Height + deltaHeight);
        _desktopCanvas.Width = Width;
        _desktopCanvas.Height = Height;
        RefreshHitTestSnapshot();
        RequestFrameRefresh();
    }

    private void RequestFrameRefresh()
    {
        _captureDirty = true;
        if (AvaloniaOverlay.ShouldUseCustomSkiaProducer)
        {
            RenderCustomProducerFrame();
            return;
        }

        InvalidateVisual();
        QueueCapture();
    }

    internal void OnGameSurfaceMetricsChanged()
    {
        _captureDirty = true;
        if (AvaloniaOverlay.ShouldUseCustomSkiaProducer)
        {
            RenderCustomProducerFrame();
            return;
        }

        InvalidateVisual();
        QueueCapture();
    }

    internal void OnCustomFrameSubmitted(int width, int height)
    {
        if (width <= 1 || height <= 1)
            return;

        _awaitingCustomFrame = false;
        _customFrameWaitStartedAt = 0;
        _loggedCustomFallback = false;
        _hasPresentedFrame = true;
        _captureDirty = false;
    }

    private void RenderCustomProducerFrame()
    {
        if (!SyncToGameSurfaceSize())
            return;

        if (!_awaitingCustomFrame)
        {
            _awaitingCustomFrame = true;
            _customFrameWaitStartedAt = Environment.TickCount64;
        }

        RefreshHitTestSnapshot();
        InvalidateVisual();
    }

    private bool ShouldUseSoftwareFallbackCapture()
    {
        if (!_awaitingCustomFrame)
            return false;

        if (_customFrameWaitStartedAt == 0)
            return false;

        long elapsed = Environment.TickCount64 - _customFrameWaitStartedAt;
        if (elapsed < 250)
            return false;

        if (_hasPresentedFrame)
            return false;

        if (!_loggedCustomFallback)
        {
            _loggedCustomFallback = true;
            RynthLog.UI("AvaloniaOverlay: Custom Skia producer is taking too long; capturing a software fallback frame.");
        }

        return true;
    }

    private bool SyncToGameSurfaceSize()
    {
        if (!TryGetTargetGameSurfacePixels(out int targetWidth, out int targetHeight))
            return false;

        double scale = GetEffectiveRenderScale();
        double logicalWidth = targetWidth / scale;
        double logicalHeight = targetHeight / scale;

        bool changed = !AreClose(Width, logicalWidth) ||
                       !AreClose(Height, logicalHeight) ||
                       !AreClose(_desktopCanvas.Width, logicalWidth) ||
                       !AreClose(_desktopCanvas.Height, logicalHeight);

        if (changed)
        {
            Width = logicalWidth;
            Height = logicalHeight;
            _desktopCanvas.Width = logicalWidth;
            _desktopCanvas.Height = logicalHeight;
        }

        return true;
    }

    private bool TryGetTargetGameSurfacePixels(out int width, out int height)
    {
        width = AvaloniaOverlay.ClientPixelWidth > 1
            ? AvaloniaOverlay.ClientPixelWidth
            : AvaloniaOverlay.ViewportWidth;
        height = AvaloniaOverlay.ClientPixelHeight > 1
            ? AvaloniaOverlay.ClientPixelHeight
            : AvaloniaOverlay.ViewportHeight;

        return width > 1 && height > 1;
    }

    private double GetEffectiveRenderScale()
    {
        double scale = RenderScaling;
        return scale > 1.0 ? scale : 1.0;
    }

    private static bool AreClose(double left, double right)
    {
        return Math.Abs(left - right) < 0.01;
    }

    private void RefreshHitTestSnapshot()
    {
        float inputScale = (float)GetEffectiveRenderScale();
        AvaloniaOverlay.InputScale = inputScale;

        if (_barBorder != null)
        {
            Point? barOrigin = _barBorder.TranslatePoint(default, _desktopCanvas);
            double barLeft = barOrigin?.X ?? Canvas.GetLeft(_barBorder);
            double barTop = barOrigin?.Y ?? Canvas.GetTop(_barBorder);
            double barWidth = _barBorder.Bounds.Width > 0 ? _barBorder.Bounds.Width : _barBorder.DesiredSize.Width;
            double barHeight = _barBorder.Bounds.Height > 0 ? _barBorder.Bounds.Height : _barBorder.DesiredSize.Height;

            if (barWidth <= 0)
                barWidth = 400;
            if (barHeight <= 0)
                barHeight = 40;

            AvaloniaOverlay.PanelPhysLeft = (int)Math.Round(barLeft * inputScale);
            AvaloniaOverlay.PanelPhysTop = (int)Math.Round(barTop * inputScale);
            AvaloniaOverlay.PanelPhysRight = (int)Math.Round((barLeft + barWidth) * inputScale);
            AvaloniaOverlay.PanelPhysBottom = (int)Math.Round((barTop + barHeight) * inputScale);
        }

        var buttonRects = new Dictionary<string, (double Left, double Top, double Width, double Height)>(_barButtons.Count, StringComparer.OrdinalIgnoreCase);
        if (_barBorder != null)
        {
            foreach ((string title, Button button) in _barButtons)
            {
                Point? origin = button.TranslatePoint(default, _desktopCanvas);
                if (origin == null)
                    continue;

                double width = button.Bounds.Width > 0 ? button.Bounds.Width : button.DesiredSize.Width;
                double height = button.Bounds.Height > 0 ? button.Bounds.Height : button.DesiredSize.Height;
                if (width <= 0)
                    width = 72;
                if (height <= 0)
                    height = 24;

                buttonRects[title] = (origin.Value.X * inputScale, origin.Value.Y * inputScale, width * inputScale, height * inputScale);
            }
        }

        AvaloniaOverlay.PublishBarButtonRects(buttonRects);

        var rects = new List<(double Left, double Top, double Width, double Height)>(_activePanels.Count);
        foreach (Border panel in _activePanels.Values)
        {
            Point? origin = panel.TranslatePoint(default, _desktopCanvas);
            rects.Add((
                (origin?.X ?? Canvas.GetLeft(panel)) * inputScale,
                (origin?.Y ?? Canvas.GetTop(panel)) * inputScale,
                panel.Width * inputScale,
                panel.Height * inputScale));
        }

        AvaloniaOverlay.PublishPanelRects(rects.ToArray());
    }

    private void QueueCapture()
    {
        if (AvaloniaOverlay.ShouldUseCustomSkiaProducer || _captureQueued)
            return;

        _captureQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _captureQueued = false;
            if (_captureDirty)
                CaptureNow();
        }, DispatcherPriority.Render);
    }
}
