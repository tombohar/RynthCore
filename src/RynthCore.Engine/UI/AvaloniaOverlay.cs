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
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.ImGuiBackend;

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
    // Radar lives in its own slot so we can apply Ctrl-gated click-through:
    // hits inside the radar are interactive only while Ctrl is held; otherwise
    // they fall through to AC. Other panels stay always-interactive.
    private static PhysicalRect? _radarPanelRect;
    // Settings popup overhangs past the radar's bounds so we publish its rect
    // separately. Clicks inside the popup are always interactive (even with
    // Ctrl-gated click-through enabled) so the user can toggle settings.
    private static PhysicalRect? _radarSettingsPopupRect;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    private const int VK_CONTROL = 0x11;
    /// <summary>True while the user is currently holding either Ctrl key.</summary>
    internal static bool IsCtrlHeld() => (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    /// <summary>
    /// True logical-pixel cursor position relative to AC's client area.
    /// The Avalonia drag handlers can't rely on PointerEventArgs.GetPosition
    /// once Avalonia internally calls SetCapture(avHwnd) — avHwnd is parked at
    /// screen (-10000, -10000), so every WM_MOUSEMOVE that arrives via that
    /// captured route reports lParam offset by +10000 on each axis. The drag
    /// math then computes a delta of ~10000 logical px and snaps the panel
    /// off-screen. GetCursorPos is the only authoritative source while the
    /// OS modal capture loop is active.
    /// </summary>
    internal static bool TryGetCursorCanvasPosition(out double logicalX, out double logicalY)
    {
        logicalX = logicalY = 0;
        IntPtr hwnd = ImGuiBackend.Win32Backend.GameHwnd;
        if (hwnd == IntPtr.Zero) return false;
        if (!GetCursorPos(out POINT p)) return false;
        if (!ScreenToClient(hwnd, ref p)) return false;
        float scale = InputScale > 0 ? InputScale : 1f;
        logicalX = p.X / scale;
        logicalY = p.Y / scale;
        return true;
    }

    /// <summary>
    /// Mirrors RadarSettingsStore.CtrlGatedClickThrough — pushed from the
    /// radar's settings handlers so IsOverPanel and FloatingPanelHost.Tick
    /// can read it without taking a dependency on the panel namespace.
    /// </summary>
    internal static volatile bool RadarCtrlGatedClickThrough;

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
    /// <summary>
    /// True while a docked panel is being dragged or resized (Avalonia logical
    /// pointer capture on the drag handle / resize grip). Floating panel hosts
    /// check this to skip forwarding their own WM_MOUSEMOVE — without the
    /// guard, a cursor wandering over a floating LayeredWindow during a docked
    /// drag would post mouse moves to Avalonia using off-bounds canvas coords
    /// (e.g. canvas X=2500+) where the floating chrome lives, and the captured
    /// drag handler would translate the huge delta into an instant snap to
    /// the bottom-right corner.
    /// </summary>
    internal static volatile bool DockedPanelPointerCaptureActive;

    // Snapshot of every non-gated floating-panel layered window so we can flip
    // WS_EX_TRANSPARENT on all of them the instant a docked drag begins. The
    // reactive path in LayeredWindow.OnMessage(WM_MOUSEMOVE) only fires after
    // a mouse-move is already lost to the floating window, which manifests as
    // a docked drag dropping a few pixels into the move. Eager updates fix that.
    // Atomically swapped via Interlocked so any thread (Win32Backend WndProc
    // included) can iterate without a lock.
    private static volatile (LayeredWindow Window, bool Gated)[] _floatingLayeredSnapshot =
        Array.Empty<(LayeredWindow, bool)>();

    internal static void UpdateFloatingLayeredSnapshot((LayeredWindow Window, bool Gated)[] snapshot)
        => _floatingLayeredSnapshot = snapshot;

    /// <summary>
    /// Set <see cref="DockedPanelPointerCaptureActive"/> and immediately apply
    /// (or clear) WS_EX_TRANSPARENT on every non-gated floating panel. Called
    /// from both the AC-side WndProc latch (Win32Backend) and the Avalonia-side
    /// drag/resize handlers, so click-through flips on the same input event
    /// that arms the latch — no waiting for the next mouse-move or Tick().
    /// </summary>
    internal static void SetDockedPanelPointerCaptureActive(bool active)
    {
        DockedPanelPointerCaptureActive = active;
        var snap = _floatingLayeredSnapshot;
        for (int i = 0; i < snap.Length; i++)
        {
            if (snap[i].Gated) continue;
            try { snap[i].Window.SetClickThrough(active); } catch { }
        }
    }

    // GetAsyncKeyState is already declared on this type (used by IsCtrlHeld).
    private const int VK_LBUTTON = 0x01;
    private static int _stuckClickThroughTicks;

    /// <summary>
    /// Self-healing watchdog, called ~1s from HeartbeatLogger (an engine-owned
    /// thread independent of the input path). <see cref="DockedPanelPointerCaptureActive"/>
    /// makes every non-gated floating panel WS_EX_TRANSPARENT so a docked
    /// drag/resize isn't stolen by a floating window the cursor crosses — a
    /// state that is only ever valid WHILE a docked drag holds the left mouse
    /// button down. Several disarm paths can be missed (Avalonia capture lost,
    /// panel torn down on redock, the release not routed — the chronic failure
    /// mode of this subsystem), stranding every floating panel permanently
    /// click-through: "can't interact at all, clicks go through to whatever's
    /// underneath", with zero input ever reaching FloatingPanelHost. If the
    /// flag is set while the physical left button is NOT down, no drag can be
    /// in progress, so force it off. Cannot interrupt a real drag (a real drag
    /// holds LBUTTON); the 2-tick confirm is pure belt-and-braces.
    /// </summary>
    internal static void WatchdogClearStuckClickThrough()
    {
        if (!DockedPanelPointerCaptureActive) { _stuckClickThroughTicks = 0; return; }
        if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0) { _stuckClickThroughTicks = 0; return; }
        if (++_stuckClickThroughTicks < 2) return;
        _stuckClickThroughTicks = 0;
        RynthLog.Info("AvaloniaOverlay watchdog: DockedPanelPointerCaptureActive was stuck true with the left mouse button up — forcing false (floating panels had been left click-through).");
        SetDockedPanelPointerCaptureActive(false);
    }

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

            // Radar settings popup is always interactive — it overhangs past
            // the radar bounds so the user can adjust settings without losing
            // them when Ctrl-gated click-through is on.
            if (_radarSettingsPopupRect.HasValue && _radarSettingsPopupRect.Value.Contains(gameClientX, gameClientY))
                return true;

            // Radar: when the user has enabled Ctrl-gated click-through,
            // clicks fall through to AC unless Ctrl is held. Otherwise the
            // radar is always interactive like any other panel.
            if (_radarPanelRect.HasValue && _radarPanelRect.Value.Contains(gameClientX, gameClientY))
                return !RadarCtrlGatedClickThrough || IsCtrlHeld();
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

    // Set by DcompOverlayWindow to intercept RynthAiPanel launcher clicks in the DComp path.
    internal static Action<string>? DcompActivateBarButton;

    internal static void ActivateBarButton(string title)
    {
        var dcomp = DcompActivateBarButton;
        if (dcomp != null) { dcomp(title); return; }
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

    /// <summary>
    /// Publish the docked radar's physical bounds for Ctrl-gated click-through.
    /// Pass null to clear (radar closed or popped out — popout uses the layered
    /// window's own WM_NCHITTEST / HTTRANSPARENT path).
    /// </summary>
    internal static void PublishRadarPanelRect((double Left, double Top, double Width, double Height)? rect)
    {
        lock (HitTestLock)
        {
            if (rect == null)
            {
                _radarPanelRect = null;
                return;
            }
            var r = rect.Value;
            _radarPanelRect = new PhysicalRect(
                (int)Math.Round(r.Left),
                (int)Math.Round(r.Top),
                (int)Math.Round(r.Left + r.Width),
                (int)Math.Round(r.Top + r.Height));
        }
    }

    /// <summary>
    /// Publish the radar settings popup's physical bounds (in canvas/AC pixel
    /// coords). Always-interactive: hit-test ignores the Ctrl-gated flag for
    /// this rect so the user can toggle settings without losing the popup.
    /// Pass null to clear (popup hidden / radar closed).
    /// </summary>
    internal static void PublishRadarSettingsPopupRect((double Left, double Top, double Width, double Height)? rect)
    {
        lock (HitTestLock)
        {
            if (rect == null)
            {
                _radarSettingsPopupRect = null;
                return;
            }
            var r = rect.Value;
            _radarSettingsPopupRect = new PhysicalRect(
                (int)Math.Round(r.Left),
                (int)Math.Round(r.Top),
                (int)Math.Round(r.Left + r.Width),
                (int)Math.Round(r.Top + r.Height));
        }
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

        // When ImGui's per-frame backend isn't running, EngineFrameController.Init
        // never executes — and that's the path that normally populates
        // Win32Backend.GameHwnd + EntryPoint.GameHwnd. Avalonia panels read
        // those for owner-window binding (see Win32Backend.GameHwnd usages
        // in this file). Spawn a small background poller that finds AC's
        // visible window via EnumWindows and writes both fields. This
        // de-couples Avalonia mode from the D3D9/ImGui path entirely.
        if (!Plugins.EngineSettings.EnableImGuiBackend || !Plugins.EngineSettings.EnableD3D9Hook)
        {
            new Thread(() =>
            {
                long deadline = Environment.TickCount64 + 60_000;
                while (Environment.TickCount64 < deadline)
                {
                    IntPtr hwnd = ImGuiBackend.EngineFrameController.FindGameWindow();
                    if (hwnd != IntPtr.Zero)
                    {
                        ImGuiBackend.Win32Backend.SetGameHwndExternal(hwnd);
                        EntryPoint.GameHwnd = hwnd;
                        // Subclass the game's WndProc so mouse/keyboard input gets
                        // routed to Avalonia panels. Win32Backend.Init is normally
                        // called by EngineFrameController.Init, but with ImGui disabled
                        // we still need the subclass to forward input to Avalonia.
                        // ImGui-specific capture flags stay false; only Avalonia
                        // hit-testing in WndProcHook activates per panel-area.
                        if (ImGuiBackend.Win32Backend.Init(hwnd))
                            RynthLog.UI($"AvaloniaOverlay: WndProc subclassed for input (ImGui-less mode).");
                        else
                            RynthLog.UI("AvaloniaOverlay: WndProc subclass FAILED — Avalonia panels will not receive input.");
                        RynthLog.UI($"AvaloniaOverlay: GameHwnd populated externally via EnumWindows = 0x{hwnd:X8}.");
                        return;
                    }
                    Thread.Sleep(100);
                }
                RynthLog.UI("AvaloniaOverlay: EnumWindows poll timed out without finding the game window.");
            })
            { Name = "RynthCore.GameHwndPoller", IsBackground = true }.Start();
        }

        _thread = new Thread(ThreadMain) { Name = "RynthCore.Avalonia", IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>
    /// Stops the Avalonia overlay: posts a Shutdown to the UI thread and joins the
    /// dedicated STA thread. Safe to call from any thread; idempotent. Returns once
    /// the Avalonia thread has terminated (or after the join timeout elapses).
    /// </summary>
    public static void Stop(int joinTimeoutMs = 5000)
    {
        if (!_started) return;

        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    // Persist docked panels that are open right now so they
                    // reappear on the next launch. RestoreOpenPanels only
                    // reopens entries marked open=true; the open path doesn't
                    // re-persist that flag once a saved entry exists (it would
                    // corrupt coords against the placeholder canvas), so a
                    // docked panel the user opened but never dragged/resized
                    // stayed marked closed and never came back after a reload
                    // / AC restart. Mirrors the floating-panel persistence in
                    // CloseAllFloatingPanels below.
                    _window?.PersistOpenDockedPanels();

                    // Close any floating panels first. Their windows aren't
                    // owned by the off-screen main window (which lives at
                    // -10000,-10000), so they don't auto-close on lifetime
                    // shutdown — close them by hand to release their HWNDs.
                    _window?.CloseAllFloatingPanels();

                    if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
                        lifetime.Shutdown();
                }
                catch (Exception ex)
                {
                    RynthLog.UI($"AvaloniaOverlay: Stop dispatcher post failed - {ex.Message}");
                }
            }, DispatcherPriority.Send);
        }
        catch (Exception ex)
        {
            RynthLog.UI($"AvaloniaOverlay: Stop dispatcher unavailable - {ex.Message}");
        }

        Thread? thread = _thread;
        if (thread != null && thread.IsAlive)
        {
            if (!thread.Join(joinTimeoutMs))
                RynthLog.UI($"AvaloniaOverlay: Avalonia thread did not exit within {joinTimeoutMs}ms.");
        }

        _thread = null;
        _window = null;
        _started = false;
        AvaloniaHwnd = IntPtr.Zero;
        lock (HitTestLock)
        {
            _panelRects = Array.Empty<PhysicalRect>();
            _barButtonRects = new Dictionary<string, PhysicalRect>(StringComparer.OrdinalIgnoreCase);
        }
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
            // Default ShutdownMode is OnLastWindowClose, which calls
            // Environment.Exit(0) → ExitProcess(0) the moment our last
            // Avalonia window closes. We're hosted INSIDE acclient.exe, so
            // an Avalonia-driven Environment.Exit kills the entire AC client.
            // Switch to OnExplicitShutdown — only EngineLifecycle.Shutdown
            // (or AC closing on its own) ends the process.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

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
    [DllImport("user32.dll", EntryPoint = "SetWindowLongA", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrSubclass(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll", EntryPoint = "CallWindowProcA")]
    private static extern IntPtr CallWindowProcA(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    private const int  GWL_EXSTYLE      = -20;
    private const int  GWL_WNDPROC      = -4;
    private const int  WS_EX_NOACTIVATE = 0x08000000;
    private const int  WS_EX_TOOLWINDOW = 0x00000080;
    private const uint SWP_NOMOVE       = 0x0002;
    private const uint SWP_NOSIZE       = 0x0001;
    private const uint SWP_NOACTIVATE   = 0x0010;
    private const uint SWP_NOZORDER     = 0x0004;
    private const uint WM_MOUSEMOVE     = 0x0200;
    private const uint WM_LBUTTONDOWN   = 0x0201;
    private const uint WM_LBUTTONUP    = 0x0202;
    private const uint WM_MOUSELEAVE    = 0x02A3;
    private const uint WM_NCMOUSELEAVE  = 0x02A2;
    private const uint WM_SETFOCUS      = 0x0007;
    private const uint WM_KILLFOCUS     = 0x0008;
    private const uint WM_MOUSEACTIVATE = 0x0021;
    private const int  MA_NOACTIVATE    = 2;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private static WndProcDelegate? _avaloniaSubclassDelegate;
    private static IntPtr _avaloniaOriginalWndProc;
    private static IntPtr _avaloniaSubclassedHwnd;
    // True between WM_LBUTTONDOWN and WM_LBUTTONUP on avHwnd (panel click in flight).
    // Used to defer WM_KILLFOCUS so Avalonia's pointer capture isn't cancelled mid-click.
    private static volatile bool _avaloniaMouseButtonDown;
    private static volatile bool _pendingAvaloniaKillFocus;

    /// <summary>
    /// Subclasses Avalonia's offscreen HWND to swallow WM_MOUSELEAVE /
    /// WM_NCMOUSELEAVE.
    ///
    /// Avalonia 11 calls TrackMouseEvent(TME_LEAVE) on every WM_MOUSEMOVE so it
    /// can fire PointerExited when the cursor leaves the window. The OS
    /// evaluates "left" against the real cursor position vs the HWND's screen
    /// rect — but our HWND is parked at (-10000, -10000) where the real cursor
    /// can never be, so the OS posts WM_MOUSELEAVE immediately after every
    /// forwarded WM_MOUSEMOVE.
    ///
    /// Net effect without this subclass: PointerEntered fires on each
    /// forwarded WM_MOUSEMOVE → :pointerover applied → WM_MOUSELEAVE fires
    /// almost immediately → PointerExited → :pointerover cleared → ToolTip
    /// timer cancelled. Symptom: button hover styles and tooltips only flash
    /// during cursor motion and disappear the moment the cursor stops, even
    /// though Avalonia thinks the cursor is still over the panel.
    ///
    /// Win32Backend is the source of truth for "is the cursor over a panel"
    /// (it computes IsOverPanel from the real cursor position vs published
    /// panel rects), so dropping the OS leave events is safe — when the
    /// cursor really leaves the panel area we either stop forwarding moves
    /// (no events for the offscreen HWND at all) or we'll forward fresh
    /// PointerExited semantics through future move events into a non-panel
    /// area; Avalonia's internal hit-testing handles the latter from the
    /// synthesised lParam coords.
    /// </summary>
    private static IntPtr AvaloniaSubclassWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_MOUSELEAVE || msg == WM_NCMOUSELEAVE)
            return IntPtr.Zero;

        // Belt-and-suspenders: WS_EX_NOACTIVATE handles most activation paths;
        // this catches any remaining code path that would activate the window
        // (e.g. a future floating sub-window rooted in the off-screen HWND).
        if (msg == WM_MOUSEACTIVATE)
            return (IntPtr)MA_NOACTIVATE;

        // When Avalonia acquires focus, immediately ask the game thread to
        // reclaim it. PostMessage is non-blocking — it queues on the game
        // thread's message pump and WndProcHook handles it there with a
        // same-thread SetFocus call (no cross-thread wait, no click delay).
        if (msg == WM_SETFOCUS && !Win32Backend.ChatCaptureActive)
        {
            RynthLog.Info("AvaloniaSubclass: WM_SETFOCUS (mouseDown=" + _avaloniaMouseButtonDown + ").");
            IntPtr gameHwnd = Win32Backend.GameHwnd;
            if (gameHwnd != IntPtr.Zero)
                PostMessage(gameHwnd, Win32Backend.WM_RYNTH_RESTORE_FOCUS, IntPtr.Zero, IntPtr.Zero);
        }

        // Track mouse-button state on avHwnd so WM_KILLFOCUS can be deferred.
        // Self-heal: if stuck from a prior click whose LBUTTONUP never arrived,
        // reset before the new press so _pendingAvaloniaKillFocus doesn't stale.
        if (msg == WM_LBUTTONDOWN)
        {
            _pendingAvaloniaKillFocus = false;
            _avaloniaMouseButtonDown  = true;
            RynthLog.Info("AvaloniaSubclass: WM_LBUTTONDOWN.");
        }

        // Defer WM_KILLFOCUS while a panel click is in flight.
        // Avalonia's WM_KILLFOCUS handler can release pointer capture and clear
        // IsPressed, aborting the click before WM_LBUTTONUP arrives.  Suppress
        // it here; deliver it after WM_LBUTTONUP so the full press→release
        // cycle completes first and the button fires Click normally.
        if (msg == WM_KILLFOCUS)
        {
            if (_avaloniaMouseButtonDown)
            {
                RynthLog.Info("AvaloniaSubclass: WM_KILLFOCUS deferred (click in flight).");
                _pendingAvaloniaKillFocus = true;
                return IntPtr.Zero;
            }
            RynthLog.Info("AvaloniaSubclass: WM_KILLFOCUS (no active click).");
        }

        // avHwnd is parked at screen (-10000,-10000). When Avalonia calls
        // SetCapture(avHwnd) for a slider drag, Win32 delivers subsequent
        // WM_MOUSEMOVE/WM_LBUTTONUP to avHwnd with lParam relative to that
        // offscreen origin — coords offset by +10000 on each axis.
        //
        // For floating panels (PointerCapturingHost set by ForwardInput):
        //   correct avX = OffBoundsCanvasX * scale + (cursorScreen.X - hostScreenLeft)
        //   — mirrors the ForwardInput translation so Avalonia sees a consistent
        //     coordinate space for both the press and subsequent moves.
        // For docked panels (PointerCapturingHost null):
        //   correct coord = game-client-relative (ScreenToClient(gameHwnd)).
        if (msg == WM_MOUSEMOVE || msg == WM_LBUTTONUP)
        {
            if (GetCursorPos(out POINT cur))
            {
                var captureHost = FloatingPanelHost.PointerCapturingHost;
                if (captureHost != null)
                {
                    float scale = AvaloniaOverlay.InputScale > 0 ? AvaloniaOverlay.InputScale : 1f;
                    int avX = (int)Math.Round(captureHost.OffBoundsCanvasX * scale) + (cur.X - captureHost.ScreenLeft);
                    int avY = (int)Math.Round(captureHost.OffBoundsCanvasY * scale) + (cur.Y - captureHost.ScreenTop);
                    lParam = new IntPtr(((avY & 0xFFFF) << 16) | (avX & 0xFFFF));
                }
                else
                {
                    POINT p = cur;
                    IntPtr gameHwnd = Win32Backend.GameHwnd;
                    if (gameHwnd != IntPtr.Zero && ScreenToClient(gameHwnd, ref p))
                        lParam = new IntPtr(((p.Y & 0xFFFF) << 16) | (p.X & 0xFFFF));
                }
            }
            if (msg == WM_LBUTTONUP)
            {
                _avaloniaMouseButtonDown = false;
                FloatingPanelHost.PointerCapturingHost = null;
                Win32Backend.ClearPanelMouseDown();
                // Avalonia called Win32 SetCapture(avHwnd) after processing the
                // forwarded WM_LBUTTONDOWN, so WM_LBUTTONUP arrives here instead
                // of the game WndProc. The game path (Win32Backend line ~857)
                // is never reached and DockedPanelPointerCaptureActive stays true,
                // making every floating panel WS_EX_TRANSPARENT until the ~1s
                // watchdog fires. Clear it here immediately.
                AvaloniaOverlay.SetDockedPanelPointerCaptureActive(false);
                RynthLog.Info("AvaloniaSubclass: WM_LBUTTONUP; pendingKillFocus=" + _pendingAvaloniaKillFocus + ".");
                // Deliver the deferred WM_KILLFOCUS now that the click is done.
                // Using PostMessage so Avalonia first processes WM_LBUTTONUP
                // (fires Click) and only then sees the focus loss.
                if (_pendingAvaloniaKillFocus)
                {
                    _pendingAvaloniaKillFocus = false;
                    PostMessage(hWnd, WM_KILLFOCUS, IntPtr.Zero, IntPtr.Zero);
                }
            }
        }

        return CallWindowProcA(_avaloniaOriginalWndProc, hWnd, msg, wParam, lParam);
    }

    private Canvas _desktopCanvas = new();
    private RenderTargetBitmap? _rtt;
    private byte[]? _softwareBuffer;
    private DispatcherTimer _captureTimer;
    private Dictionary<string, Border> _activePanels = new();
    // Panels detached into their own native top-level windows. A given title
    // is in either _activePanels (docked) or _floatingPanels (popped out),
    // never both. _panelContents holds the factory-built Control across pop
    // out / re-dock cycles so internal panel state survives reparenting.
    //
    // FloatingPanelHost wraps a non-Avalonia LayeredWindow + a per-panel
    // RenderTargetBitmap. When floating, the panel's chrome Border stays in
    // _desktopCanvas at off-bounds coords (so Avalonia layout/input keep
    // working) but the canvas RTT clips it; the host renders the Border into
    // its own RTT and pushes the pixels to the layered window via
    // UpdateLayeredWindow. This sidesteps OverlaySkiaPlatformGraphics
    // entirely — the bridge is a singleton with one shared render target,
    // so a real visible Avalonia Window would thrash that target.
    private readonly Dictionary<string, FloatingPanelHost> _floatingPanels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Control> _panelContents = new(StringComparer.OrdinalIgnoreCase);
    // Each floating panel parks its chrome Border past the right edge of
    // AC's client area (canvas X = FloatingOffBoundsX, Y = slot * pitch).
    // The X is well beyond any reasonable AC client width so the in-AC RTT
    // (sized to AC client) naturally clips floating panels out of the
    // in-AC capture, regardless of when popout fires relative to
    // SyncToGameSurfaceSize.
    //
    // The Avalonia Window/canvas itself is extended (SyncToGameSurfaceSize)
    // by FloatingExtraLogicalWidth so this region falls inside Window
    // bounds — Avalonia clips input dispatch to TopLevel bounds, and we
    // need forwarded clicks at canvas X >= FloatingOffBoundsX to land
    // inside. Kept conservative to avoid Skia surface-size hiccups
    // (previous +8000 caused the in-AC capture to stop producing output).
    private int _nextFloatingSlotIndex;
    private const double FloatingOffBoundsX = 2500;
    private const double FloatingOffBoundsSlotPitch = 1500;
    private const double FloatingExtraLogicalWidth = 4000;

    /// <summary>
    /// Rebuild the static snapshot AvaloniaOverlay.SetDockedPanelPointerCaptureActive
    /// iterates from any thread. Must be called on the Avalonia UI thread after
    /// every mutation of <see cref="_floatingPanels"/>.
    /// </summary>
    private void RebuildFloatingLayeredSnapshot()
    {
        var arr = new (LayeredWindow, bool)[_floatingPanels.Count];
        int i = 0;
        foreach (FloatingPanelHost host in _floatingPanels.Values)
            arr[i++] = (host.LayeredWindow, host.IsClickThroughGated);
        AvaloniaOverlay.UpdateFloatingLayeredSnapshot(arr);
    }
    private readonly Dictionary<string, Func<Control>> _registeredPanels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Button> _barButtons = new(StringComparer.OrdinalIgnoreCase);
    private bool _deferredFloatingRestoreSubscribed;
    /// <summary>
    /// The radar's settings popup, hosted in _desktopCanvas as a sibling of
    /// the radar (NOT a child of the radar's visual tree). Sibling parenting
    /// gives the popup unbounded measure space so it sizes to its natural
    /// content regardless of the radar's current dimensions, and lets it
    /// extend past the radar's bounds without being clipped by the
    /// windowFrame. Cleared when the radar closes.
    /// </summary>
    private Control? _radarSettingsPopup;
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

                    // Subclass to swallow OS-fired WM_MOUSELEAVE — see the
                    // AvaloniaSubclassWndProc summary for full reasoning.
                    if (_avaloniaSubclassedHwnd != hwnd)
                    {
                        _avaloniaSubclassDelegate = AvaloniaSubclassWndProc;
                        IntPtr fp = Marshal.GetFunctionPointerForDelegate(_avaloniaSubclassDelegate);
                        IntPtr prev = SetWindowLongPtrSubclass(hwnd, GWL_WNDPROC, fp);
                        if (prev != IntPtr.Zero)
                        {
                            _avaloniaOriginalWndProc = prev;
                            _avaloniaSubclassedHwnd = hwnd;
                            RynthLog.UI("AvaloniaOverlay: subclassed offscreen HWND to swallow WM_MOUSELEAVE.");
                        }
                        else
                        {
                            RynthLog.UI($"AvaloniaOverlay: WM_MOUSELEAVE subclass install failed (error {Marshal.GetLastWin32Error()}).");
                        }
                    }
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

            // Floating panels render independently of the in-AC capture path.
            // Tick every frame so popped-out panel content stays live —
            // skipping when there are no floating panels keeps overhead at zero
            // for the common docked-only case.
            TickFloatingPanels();
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

    private static readonly IBrush BarBtnBg     = new SolidColorBrush(Color.Parse("#1A2230"));
    private static readonly IBrush BarBtnFg     = Brushes.White;
    private static readonly IBrush BarBtnBorder = new SolidColorBrush(Color.Parse("#3A4A5A"));

    /// <summary>
    /// Apply explicit dark button styling. Avalonia's SimpleTheme renders
    /// buttons with a near-white default that disappears against the dark bar
    /// background — set every visual property we care about so the buttons
    /// stay legible regardless of theme.
    /// </summary>
    private static Button StyleBarButton(Button btn)
    {
        btn.FontSize = 11;
        btn.Padding = new Thickness(10, 2);
        btn.Background = BarBtnBg;
        if (btn.Foreground == null || ReferenceEquals(btn.Foreground, Brushes.Transparent))
            btn.Foreground = BarBtnFg;
        btn.BorderBrush = BarBtnBorder;
        btn.BorderThickness = new Thickness(1);
        return btn;
    }

    private Control BuildRoot()
    {
        var registeredPanels = OverlayHost.GetPanels();

        _registeredPanels.Clear();
        _barButtons.Clear();

        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(8, 4) };
        _barStack = stack;
        stack.Children.Add(new TextBlock { Text = "RC", FontWeight = FontWeight.Bold, Foreground = Brushes.LightGreen, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,10,0) });

        var barSuppressed = new HashSet<string> { "Monsters", "Settings", "Nav", "Meta", "Items" };
        foreach (var p in registeredPanels)
        {
            _registeredPanels[p.Title] = p.Factory;
            if (barSuppressed.Contains(p.Title)) continue;
            var btn = StyleBarButton(new Button { Content = p.Title });
            btn.Click += (_, _) => ActivateBarButton(p.Title);
            stack.Children.Add(btn);
            _barButtons[p.Title] = btn;
        }

        var reloadBtn = StyleBarButton(new Button
        {
            Content = "RL",
            Foreground = Brushes.Khaki,
            Margin = new Thickness(8, 0, 0, 0),
        });
        ToolTip.SetTip(reloadBtn, "Engine reload — shut down, FreeLibrary, reload from disk, re-init.");
        reloadBtn.Click += (_, _) => EngineLifecycle.SignalReload();
        stack.Children.Add(reloadBtn);
        // Register under a special key so RefreshHitTestSnapshot publishes
        // its rect to Win32Backend — without this, clicks over RL fall
        // through to AC instead of being routed to Avalonia.
        _barButtons["__engine_reload__"] = reloadBtn;

        _barBorder = new Border {
            Background = new SolidColorBrush(Color.Parse("#E60A0A14")), 
            CornerRadius = new CornerRadius(4), 
            Child = stack 
        };

        // Update physical bar bounds for dragging. Use the persisted position
        // if we have one so a reload (or AC restart) keeps the user's layout.
        var savedBar = PanelStateStore.BarPosition;
        int barLeft = savedBar.HasValue ? (int)Math.Round(savedBar.Value.Left) : 50;
        int barTop  = savedBar.HasValue ? (int)Math.Round(savedBar.Value.Top)  : 5;
        AvaloniaOverlay.PanelPhysLeft = barLeft;
        AvaloniaOverlay.PanelPhysTop = barTop;
        AvaloniaOverlay.PanelPhysRight = barLeft + 400; // rough estimate
        AvaloniaOverlay.PanelPhysBottom = barTop + 40;

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

        // After the layout settles, reopen any panels that were open at the
        // last shutdown so a hot reload doesn't snap everything closed.
        Dispatcher.UIThread.Post(RestoreOpenPanels, DispatcherPriority.Background);

        return _desktopCanvas;
    }

    private void RestoreOpenPanels()
    {
        try
        {
            // Floating panels become visible top-level OS windows the moment
            // they restore — popping up over the launcher / character-select
            // before the user is in-game looks broken and (per past reports)
            // can race UI init. Defer them until LoginComplete fires, which is
            // the same gate plugins like RynthAi use to come alive. Docked
            // entries restore eagerly because they live inside the AC overlay
            // canvas and only paint when the overlay itself is visible.
            var deferred = new List<string>();
            foreach (var kv in PanelStateStore.AllOpenPanels())
            {
                if (string.Equals(kv.Key, "Radar", StringComparison.OrdinalIgnoreCase))
                {
                    var e = kv.Value;
                    try { RynthLog.UI($"RestoreOpenPanels(Radar): saved pos=({e.Left:F1},{e.Top:F1}) size=({e.Width:F1}x{e.Height:F1}) floating={e.Floating} fl=({e.FloatingLeft:F1},{e.FloatingTop:F1}) canvas=({_desktopCanvas.Bounds.Width:F1}x{_desktopCanvas.Bounds.Height:F1})"); } catch { }
                }
                if (kv.Value.Floating)
                {
                    deferred.Add(kv.Key);
                    continue;
                }
                if (_registeredPanels.TryGetValue(kv.Key, out Func<Control>? factory))
                    TogglePanel(kv.Key, factory);
            }

            if (deferred.Count == 0) return;

            if (LoginLifecycleHooks.HasObservedLoginComplete)
            {
                RestoreFloatingPanels(deferred);
                return;
            }

            if (_deferredFloatingRestoreSubscribed) return;
            _deferredFloatingRestoreSubscribed = true;

            Action? handler = null;
            handler = () =>
            {
                LoginLifecycleHooks.LoginComplete -= handler!;
                Dispatcher.UIThread.Post(() => RestoreFloatingPanels(deferred));
            };
            LoginLifecycleHooks.LoginComplete += handler;
            try { RynthLog.UI($"AvaloniaOverlay: deferring {deferred.Count} floating panel(s) until LoginComplete: [{string.Join(", ", deferred)}]"); } catch { }
        }
        catch (Exception ex)
        {
            try { RynthLog.UI($"AvaloniaOverlay: RestoreOpenPanels threw {ex.GetType().Name}: {ex.Message}"); } catch { }
        }
    }

    private void RestoreFloatingPanels(List<string> titles)
    {
        try
        {
            foreach (var title in titles)
            {
                if (_registeredPanels.TryGetValue(title, out Func<Control>? factory))
                    TogglePanel(title, factory);
            }
        }
        catch (Exception ex)
        {
            try { RynthLog.UI($"AvaloniaOverlay: RestoreFloatingPanels threw {ex.GetType().Name}: {ex.Message}"); } catch { }
        }
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

        // Action buttons (e.g. engine reload) live in _barButtons but have no
        // registered panel — invoke their Click handler directly so the engine
        // forwarder can route over them.
        if (_barButtons.TryGetValue(title, out Button? btn))
        {
            try
            {
                btn.Command?.Execute(btn.CommandParameter);
                btn.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            }
            catch (Exception ex)
            {
                RynthLog.UI($"AvaloniaOverlay: ActivateBarButton({title}) threw {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void TogglePanel(string title, Func<Control> factory)
    {
        if (_activePanels.TryGetValue(title, out var existing))
        {
            ClosePanel(title, existing);
        }
        else if (_floatingPanels.TryGetValue(title, out var floating))
        {
            // Toggling a panel that's currently popped out closes the
            // floating window entirely (matches the docked-mode toggle).
            CloseFloatingPanel(title, floating);
        }
        else
        {
            bool isRynthAi = string.Equals(title, "RynthAi", StringComparison.OrdinalIgnoreCase);
            bool isMonsters = string.Equals(title, "Monsters", StringComparison.OrdinalIgnoreCase);
            bool isRadar = string.Equals(title, "Radar", StringComparison.OrdinalIgnoreCase);
            // Restore prior dimensions if we have them saved from a previous run/reload.
            bool hasSaved = PanelStateStore.TryGetPanel(title, out PanelStateStore.PanelEntry saved);
            // RynthAi mirrors the ImGui dashboard which is intentionally compact.
            // Radar is square-by-default — the gold rim and coord readout both
            // live inside the surface, so the panel hugs the radar edge-to-edge.
            double defaultW = isRynthAi ? 296 : (isMonsters ? 1100 : (isRadar ? 320 : 400));
            double defaultH = isRynthAi ? 332 : (isMonsters ? 540  : (isRadar ? 320 : 500));
            var windowFrame = new Border
            {
                Width = hasSaved && saved.Width > 0 ? saved.Width : defaultW,
                Height = hasSaved && saved.Height > 0 ? saved.Height : defaultH,
                MinWidth = isRynthAi ? 280 : (isRadar ? 120 : 320),
                MinHeight = isRynthAi ? 260 : (isRadar ? 120 : 240),
                // RynthAi panel matches the ImGui dashboard's WindowBg (0.04,
                // 0.06, 0.08, 0.95) → #F20A0F14, with the ColBtnBord (0.15,
                // 0.25, 0.35) → #264059 edge. Other panels keep the softer
                // "floating glass" look.
                Background = new SolidColorBrush(
                    isRynthAi ? Color.FromArgb(0xF2, 0x0A, 0x0F, 0x14)
                  : isRadar   ? Colors.Transparent
                  :             Color.Parse("#D212121C")),
                BorderBrush = isRynthAi
                    ? new SolidColorBrush(Color.FromRgb(0x26, 0x40, 0x59))
                    : (IBrush)Brushes.Teal,
                // Radar is chromeless — no border, no rounded corners. The
                // gold frame the user sees is rendered by the radar surface
                // itself.
                BorderThickness = isRadar ? new Thickness(0) : new Thickness(1),
                CornerRadius = new CornerRadius(isRynthAi ? 3 : (isRadar ? 0 : 4)),
                // ClipToBounds=true is the safe default: Avalonia limits
                // hover invalidation to the panel's own bounds. The radar's
                // settings popup is no longer a child of the radarGrid (it
                // now lives in _desktopCanvas as a sibling), so the radar
                // doesn't need overflow rendering anymore — and disabling
                // clipping causes the entire overlay to flash on every
                // hover repaint inside the radar.
                ClipToBounds = true
            };

            // RynthAi and Radar suppress the visible chrome entirely. RynthAi
            // renders its own title row + chips inside, that doubles as the
            // drag surface (RynthAiPanel.AttachDragHandle). Radar uses its
            // canvas as the drag surface (RadarPanel.AttachDragHandle) and
            // exposes popout/redock/close on a right-click context menu.
            string headerRow = (isRynthAi || isRadar) ? "0" : "24";
            var grid = new Grid { RowDefinitions = new RowDefinitions($"{headerRow}, *") };
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
            var popoutButton = new Button
            {
                // U+2197 = North-East Arrow — pop out / detach.
                // Matching ↙ glyph on the floating chrome means redock.
                Content = "↗",
                Width = 20,
                Height = 20,
                FontSize = 10,
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 2, 2)
            };
            ToolTip.SetTip(popoutButton, "Pop out into a floating window");
            popoutButton.PointerPressed += (_, e) => e.Handled = true;
            popoutButton.Click += (_, _) => PopOutPanel(title);

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

            // RynthAi and Radar keep an invisible 0-height header — no chrome
            // controls to host. Drag, popout, close are routed through the
            // panel's own surface via the AttachDragHandle / Request* hooks.
            Border header;
            if (isRynthAi || isRadar)
            {
                header = new Border();
            }
            else
            {
                var headerGrid = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*, Auto, Auto")
                };
                headerGrid.Children.Add(dragSurface);
                headerGrid.Children.Add(popoutButton);
                headerGrid.Children.Add(closeButton);
                Grid.SetColumn(dragSurface, 0);
                Grid.SetColumn(popoutButton, 1);
                Grid.SetColumn(closeButton, 2);

                header = new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#FF1A1A28")),
                    Child = headerGrid
                };
            }

            Point dragStartCanvas = default;
            double dragStartLeft = 0;
            double dragStartTop = 0;
            bool isDragging = false;
            double resizeStartWidth = 0;
            double resizeStartHeight = 0;
            bool isResizing = false;

            // Encapsulated so the same drag behavior can be applied both to
            // the wrapping window's chrome dragSurface AND to whatever drag
            // handle a panel registers via RynthAiPanel.AttachDragHandle.
            //
            // Critical guard for popout mode: when the panel is floating,
            // its docked frame is no longer in _desktopCanvas. Firing the
            // drag handler then sets DockedPanelPointerCaptureActive=true,
            // which makes ForwardInput drop every subsequent mouse message
            // for the popped panel — clicks "stick" on LBUTTONDOWN with no
            // matching UP, breaking every Avalonia interaction inside the
            // floating window. OS HTCAPTION already drives drag for popped
            // windows, so the Avalonia-side handler must no-op.
            void WireDragSurface(InputElement surface)
            {
                surface.PointerPressed += (s, e) => {
                    if (_floatingPanels.ContainsKey(title)) return;
                    var props = e.GetCurrentPoint(surface).Properties;
                    if (props.IsLeftButtonPressed)
                    {
                        BringPanelToFront(title, windowFrame);
                        // Anchor drag start to the OS cursor position, not e.GetPosition.
                        // Once Avalonia internally calls SetCapture(avHwnd) every following
                        // WM_MOUSEMOVE arrives offset by avHwnd's offscreen position, so
                        // GetPosition is only authoritative on the very first event.
                        // Sourcing both start and current from GetCursorPos keeps the delta
                        // consistent across the capture transition.
                        if (AvaloniaOverlay.TryGetCursorCanvasPosition(out double sx, out double sy))
                            dragStartCanvas = new Point(sx, sy);
                        else
                            dragStartCanvas = e.GetPosition(_desktopCanvas);
                        dragStartLeft = GetCanvasLeft(windowFrame);
                        dragStartTop = GetCanvasTop(windowFrame);
                        isDragging = true;
                        AvaloniaOverlay.SetDockedPanelPointerCaptureActive(true);
                        e.Pointer.Capture(surface);
                        e.Handled = true;
                    }
                };
                surface.PointerMoved += (s, e) => {
                    if (_floatingPanels.ContainsKey(title)) return;
                    var props = e.GetCurrentPoint(surface).Properties;
                    if (props.IsLeftButtonPressed && isDragging && e.Pointer.Captured == surface)
                    {
                        // Use OS cursor (AC-client logical px) instead of e.GetPosition so
                        // the delta is correct regardless of which HWND owns mouse capture.
                        if (!AvaloniaOverlay.TryGetCursorCanvasPosition(out double cx, out double cy))
                        {
                            e.Handled = true;
                            return;
                        }
                        double nextLeft = dragStartLeft + (cx - dragStartCanvas.X);
                        double nextTop = dragStartTop + (cy - dragStartCanvas.Y);
                        SetPanelPosition(windowFrame, nextLeft, nextTop);
                        RefreshHitTestSnapshot();
                        RequestFrameRefresh();
                        e.Handled = true;
                    }
                };
                surface.PointerReleased += (s, e) => {
                    if (e.Pointer.Captured == surface)
                    {
                        e.Pointer.Capture(null);
                        isDragging = false;
                        AvaloniaOverlay.SetDockedPanelPointerCaptureActive(false);
                        RefreshHitTestSnapshot();
                        RequestFrameRefresh();
                        PersistPanelState(title, windowFrame, open: true);
                        e.Handled = true;
                    }
                };
                surface.PointerCaptureLost += (_, _) => {
                    isDragging = false;
                    AvaloniaOverlay.SetDockedPanelPointerCaptureActive(false);
                };
            }

            WireDragSurface(dragSurface);

            // Hand the same drag wiring to RynthAi's in-panel title border
            // before we invoke the factory so it can attach.
            Control panelContent;
            try
            {
                if (isRynthAi)
                {
                    Panels.RynthAiPanel.AttachDragHandle = WireDragSurface;
                    Panels.RynthAiPanel.RequestPopOut    = () => PopOutPanel(title);
                    Panels.RynthAiPanel.RequestRedock    = () => RedockPanel(title);
                    Panels.RynthAiPanel.IsFloatingNow    = () => _floatingPanels.ContainsKey(title);
                    try { panelContent = factory(); }
                    finally { Panels.RynthAiPanel.AttachDragHandle = null; }
                }
                else if (isRadar)
                {
                    Panels.RadarPanel.AttachDragHandle = WireDragSurface;
                    Panels.RadarPanel.RequestPopOut = () => PopOutPanel(title);
                    Panels.RadarPanel.RequestRedock = () => RedockPanel(title);
                    Panels.RadarPanel.RequestClose  = () =>
                    {
                        if (_floatingPanels.TryGetValue(title, out var floating))
                            CloseFloatingPanel(title, floating);
                        else if (_activePanels.TryGetValue(title, out var docked))
                            ClosePanel(title, docked);
                    };
                    Panels.RadarPanel.IsFloatingNow = () => _floatingPanels.ContainsKey(title);
                    Panels.RadarPanel.AttachSettingsPopup = popup =>
                    {
                        // Tear down any popup left over from the previous radar
                        // instance (e.g., across redock-via-rebuild cycles).
                        if (_radarSettingsPopup != null && _radarSettingsPopup != popup)
                        {
                            try { _desktopCanvas.Children.Remove(_radarSettingsPopup); } catch { }
                        }
                        _radarSettingsPopup = popup;
                        if (!_desktopCanvas.Children.Contains(popup))
                            _desktopCanvas.Children.Add(popup);
                        // Force the popup above every other canvas child so it
                        // renders on top of the radar's windowFrame regardless
                        // of insertion order. ZIndex defaults to 0; any
                        // positive value wins, and 1000 leaves headroom for
                        // future overlays.
                        popup.ZIndex = 1000;

                        // Position the popup so its right edge aligns with the
                        // radar's right edge (minus a 6 px inset) and its top
                        // sits 40 px below the radar's top edge — i.e. just
                        // below the gear/popout button row. Re-runs whenever
                        // the radar moves/resizes or the popup re-measures.
                        void UpdatePos()
                        {
                            if (popup.Parent == null) return;
                            double rl = GetCanvasLeft(windowFrame);
                            double rt = GetCanvasTop(windowFrame);
                            double rw = windowFrame.Bounds.Width > 0 ? windowFrame.Bounds.Width : windowFrame.Width;
                            double pw = popup.Bounds.Width > 0 ? popup.Bounds.Width : popup.DesiredSize.Width;
                            Canvas.SetLeft(popup, rl + rw - pw - 6);
                            Canvas.SetTop(popup, rt + 40);
                        }
                        windowFrame.LayoutUpdated += (_, _) => UpdatePos();
                        popup.LayoutUpdated += (_, _) => UpdatePos();
                        UpdatePos();
                    };
                    try { panelContent = factory(); }
                    finally
                    {
                        Panels.RadarPanel.AttachDragHandle = null;
                        Panels.RadarPanel.AttachSettingsPopup = null;
                        // Keep Request* / IsFloatingNow live for the panel's
                        // context menu — the radar content is cached and
                        // reused across pop/redock cycles.
                    }
                }
                else
                {
                    panelContent = factory();
                }
            }
            catch (Exception ex)
            {
                RynthLog.Info($"AvaloniaOverlay.TogglePanel({title}): factory threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                // Surface a minimal placeholder so the panel system stays in
                // a sane state instead of leaving a half-constructed window.
                panelContent = new TextBlock
                {
                    Text = $"{title} failed to render: {ex.Message}",
                    Foreground = Brushes.OrangeRed,
                    Margin = new Thickness(8),
                    TextWrapping = TextWrapping.Wrap
                };
            }

            var body = new Border
            {
                Child = panelContent,
                // RynthAi and Radar are chromeless — no resize grip to clear.
                Padding = (isRynthAi || isRadar) ? new Thickness(0) : new Thickness(0, 0, 0, 18)
            };
            // Radar gets a smaller, gold-on-dark grip so it doesn't clash
            // with the chromeless gold-rim aesthetic; other panels keep the
            // teal-on-dark grip that matches their teal title bar.
            var resizeGlyph = new TextBlock
            {
                Text = "◢",
                FontSize = isRadar ? 10 : 14,
                Foreground = isRadar ? new SolidColorBrush(Color.FromArgb(0xFF, 0xE6, 0xC7, 0x66)) : Brushes.Teal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            var resizeGripIdle  = isRadar
                ? new SolidColorBrush(Color.FromArgb(0x40, 0x0A, 0x12, 0x1A))
                : new SolidColorBrush(Color.Parse("#2026C1A6"));
            var resizeGripHover = isRadar
                ? new SolidColorBrush(Color.FromArgb(0xA0, 0x0A, 0x12, 0x1A))
                : new SolidColorBrush(Color.Parse("#5526C1A6"));
            var resizeGrip = new Border
            {
                Width  = isRadar ? 14 : 22,
                Height = isRadar ? 14 : 22,
                Background = resizeGripIdle,
                BorderBrush = isRadar ? new SolidColorBrush(Color.FromArgb(0xFF, 0xE6, 0xC7, 0x66)) : Brushes.Teal,
                BorderThickness = new Thickness(0, 1, 0, 0),
                CornerRadius = new CornerRadius(0, 0, isRadar ? 0 : 4, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                // Sit just inside the radar's gold rim (4 px outer black + 1 px gap).
                Margin = isRadar ? new Thickness(0, 0, 4, 4) : new Thickness(0, 0, 1, 1),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.BottomRightCorner),
                Child = resizeGlyph
            };
            resizeGrip.PointerEntered += (_, _) => resizeGrip.Background = resizeGripHover;
            resizeGrip.PointerExited += (_, _) => resizeGrip.Background = resizeGripIdle;
            resizeGrip.PointerPressed += (s, e) =>
            {
                var props = e.GetCurrentPoint(resizeGrip).Properties;
                if (!props.IsLeftButtonPressed)
                    return;

                BringPanelToFront(title, windowFrame);
                resizeStartWidth = windowFrame.Width;
                resizeStartHeight = windowFrame.Height;
                if (AvaloniaOverlay.TryGetCursorCanvasPosition(out double sx, out double sy))
                    dragStartCanvas = new Point(sx, sy);
                else
                    dragStartCanvas = e.GetPosition(_desktopCanvas);
                isResizing = true;
                AvaloniaOverlay.SetDockedPanelPointerCaptureActive(true);
                e.Pointer.Capture(resizeGrip);
                e.Handled = true;
            };
            resizeGrip.PointerMoved += (s, e) =>
            {
                var props = e.GetCurrentPoint(resizeGrip).Properties;
                if (!props.IsLeftButtonPressed || !isResizing || e.Pointer.Captured != resizeGrip)
                    return;

                if (!AvaloniaOverlay.TryGetCursorCanvasPosition(out double cx, out double cy))
                {
                    e.Handled = true;
                    return;
                }
                double requestedWidth = resizeStartWidth + (cx - dragStartCanvas.X);
                double requestedHeight = resizeStartHeight + (cy - dragStartCanvas.Y);
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
                AvaloniaOverlay.SetDockedPanelPointerCaptureActive(false);
                RefreshHitTestSnapshot();
                RequestFrameRefresh();
                PersistPanelState(title, windowFrame, open: true);
                e.Handled = true;
            };
            resizeGrip.PointerCaptureLost += (_, _) => {
                isResizing = false;
                AvaloniaOverlay.SetDockedPanelPointerCaptureActive(false);
            };
            
            grid.Children.Add(header);
            grid.Children.Add(body);
            Grid.SetRow(header, 0);
            Grid.SetRow(body, 1);
            // Radar gets a small, gold-on-dark grip styled above so it can be
            // resized when docked (the popout already handles resize via the
            // OS-level HTBOTTOMRIGHT zone).
            grid.Children.Add(resizeGrip);
            Grid.SetRow(resizeGrip, 1);

            windowFrame.Child = grid;
            windowFrame.PointerPressed += (_, _) => BringPanelToFront(title, windowFrame);
            _desktopCanvas.Children.Add(windowFrame);

            double initialLeft = hasSaved ? saved.Left : 100 + (_activePanels.Count * 20);
            double initialTop  = hasSaved ? saved.Top  : 100 + (_activePanels.Count * 20);
            SetPanelPosition(windowFrame, initialLeft, initialTop);
            _activePanels[title] = windowFrame;
            _panelContents[title] = panelContent;

            // Only write to disk when opening a brand-new panel (no prior entry).
            // If restoring from a saved position, the file already has the correct
            // data — re-writing immediately would clamp coordinates against the
            // initial 800×600 canvas (before CaptureNow resizes to the real game
            // viewport), corrupting the saved position on every reload.
            if (!hasSaved)
                PersistPanelState(title, windowFrame, open: true);

            // If the user previously left this panel popped out, hand it off
            // to a layered window now. RestoreOpenPanels gates floating
            // entries on LoginComplete so this branch normally only fires
            // after the user is in-game; FloatingPanelHost.Tick still has a
            // late-binding SetOwner as a safety net for any other path that
            // pops out before Win32Backend.GameHwnd is set.
            if (hasSaved && saved.Floating)
                PopOutPanel(title);
        }

        RefreshHitTestSnapshot();
        RequestFrameRefresh();
    }

    private void ClosePanel(string title, Border panel)
    {
        // Save final pos/size before tearing the panel down so reopens
        // start where the user left it.
        PersistPanelState(title, panel, open: false);

        _desktopCanvas.Children.Remove(panel);
        _activePanels.Remove(title);
        // Drop the content reference — closing the panel discards in-panel
        // state, matching the previous behavior where reopening called factory()
        // again. Popout/redock keeps the entry, full-close removes it.
        _panelContents.Remove(title);
        // Tear down the radar's settings popup if this is the radar — the
        // popup lives in _desktopCanvas as a sibling and would otherwise
        // leak past the panel close.
        if (string.Equals(title, "Radar", StringComparison.OrdinalIgnoreCase) && _radarSettingsPopup != null)
        {
            try { _desktopCanvas.Children.Remove(_radarSettingsPopup); } catch { }
            _radarSettingsPopup = null;
            AvaloniaOverlay.PublishRadarSettingsPopupRect(null);
        }
        RefreshHitTestSnapshot();
        RequestFrameRefresh();
    }

    private void CloseFloatingPanel(string title, FloatingPanelHost host)
    {
        RynthLog.Info($"CloseFloatingPanel({title}): entry, screen=({host.ScreenLeft},{host.ScreenTop}).");
        // Mirror ClosePanel: persist closed-state with the current screen
        // coords (so re-opening from the bar lands where the user left it),
        // then tear down the layered window and remove the chrome Border
        // from the canvas. Drops the cached content too — closing fully
        // discards in-panel state, matching the docked X behavior.
        PersistFloatingPanelState(title, host.ScreenLeft, host.ScreenTop, floating: true, open: false);
        _floatingPanels.Remove(title);
        RebuildFloatingLayeredSnapshot();
        _panelContents.Remove(title);

        // The host's RootControl (a wrapper Border around the chrome) lives
        // in _desktopCanvas at off-bounds coords while floating; remove it
        // so we don't leak the visual subtree across redock cycles.
        try { _desktopCanvas.Children.Remove(host.RootControl); } catch { /* ignore */ }
        host.Dispose();

        // Tear down the radar's settings popup if this was the floating radar.
        if (string.Equals(title, "Radar", StringComparison.OrdinalIgnoreCase) && _radarSettingsPopup != null)
        {
            try { _desktopCanvas.Children.Remove(_radarSettingsPopup); } catch { }
            _radarSettingsPopup = null;
            AvaloniaOverlay.PublishRadarSettingsPopupRect(null);
        }

        RefreshHitTestSnapshot();
        RequestFrameRefresh();
    }

    /// <summary>
    /// Move a docked panel into a native Win32 layered window. The docked
    /// chrome is torn down and replaced with floating-mode chrome (same
    /// content control, different chrome handlers); the chrome Border is
    /// re-parented at off-bounds canvas coords so Avalonia layout/input
    /// continue to work but the in-AC capture clips it out.
    /// </summary>
    internal void PopOutPanel(string title)
    {
        RynthLog.Info($"PopOutPanel({title}): entry, alreadyFloating={_floatingPanels.ContainsKey(title)}, isActive={_activePanels.ContainsKey(title)}, hasContent={_panelContents.ContainsKey(title)}");
        if (_floatingPanels.ContainsKey(title))
            return;

        if (!_activePanels.TryGetValue(title, out Border? dockedFrame) || dockedFrame == null)
        {
            RynthLog.Info($"PopOutPanel({title}): bailing — no docked frame.");
            return;
        }

        // Detach the radar's settings popup from _desktopCanvas — it'll be
        // re-parented into the floating chrome below so the gear button
        // still works in popout mode (the popup needs to render inside the
        // layered window to be visible there).
        Control? popupToReparent = null;
        if (string.Equals(title, "Radar", StringComparison.OrdinalIgnoreCase) && _radarSettingsPopup != null)
        {
            popupToReparent = _radarSettingsPopup;
            try { _desktopCanvas.Children.Remove(popupToReparent); } catch { }
            _radarSettingsPopup = null;
            AvaloniaOverlay.PublishRadarSettingsPopupRect(null);
        }

        if (!_panelContents.TryGetValue(title, out Control? content) || content == null)
        {
            RynthLog.Info($"PopOutPanel({title}): bailing — no cached content.");
            return;
        }

        double logicalWidth = dockedFrame.Width > 0 ? dockedFrame.Width : dockedFrame.Bounds.Width;
        double logicalHeight = dockedFrame.Height > 0 ? dockedFrame.Height : dockedFrame.Bounds.Height;
        if (logicalWidth <= 0) logicalWidth = 400;
        if (logicalHeight <= 0) logicalHeight = 500;

        bool hasSaved = PanelStateStore.TryGetPanel(title, out PanelStateStore.PanelEntry saved);
        // Prefer saved screen coords (so popout-then-redock-then-popout
        // remembers position). Fall back to a visible default near AC's
        // top-left so the user sees the layered window appear.
        int screenLeft = hasSaved && (saved.FloatingLeft != 0 || saved.FloatingTop != 0)
            ? (int)Math.Round(saved.FloatingLeft)
            : 200;
        int screenTop = hasSaved && (saved.FloatingLeft != 0 || saved.FloatingTop != 0)
            ? (int)Math.Round(saved.FloatingTop)
            : 200;

        // Detach content from docked body so we can hand it to the new
        // floating chrome. We tear the docked Border out of the canvas after.
        // Two structures to handle, matching the docked builds:
        //   - Radar:     dockedFrame.Child = content directly (chromeless).
        //   - Non-radar: dockedFrame.Child = Grid containing header + body
        //     Border (whose Child is the content).
        // Direct-match check FIRST: the radar's content control is itself a
        // Grid, so a `dockedFrame.Child is Grid` heuristic falsely matches
        // the chromeless case and the inner loop finds nothing to detach,
        // leaving content double-parented and the next assignment crashes.
        try
        {
            if (ReferenceEquals(dockedFrame.Child, content))
            {
                dockedFrame.Child = null;
            }
            else if (dockedFrame.Child is Grid dockedGrid)
            {
                foreach (Control child in dockedGrid.Children)
                {
                    if (child is Border body && ReferenceEquals(body.Child, content))
                    {
                        body.Child = null;
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            RynthLog.UI($"AvaloniaOverlay.PopOutPanel({title}): content detach failed - {ex.Message}");
        }

        _desktopCanvas.Children.Remove(dockedFrame);
        _activePanels.Remove(title);

        // Build floating chrome — same visual shape as docked but with
        // redock instead of popout, and with no Avalonia-side drag/resize
        // handlers (the OS handles those via HTCAPTION/HTBOTTOWRIGHT in the
        // LayeredWindow's WndProc). Orphan content first so the chrome
        // builder's `Child = content` assignment can't trip Avalonia's
        // already-has-parent guard if any prior detach missed.
        OrphanControl(content);
        Border floatingFrame = BuildFloatingChrome(title, content, logicalWidth, logicalHeight);

        // Re-parent the radar settings popup into the floating chrome so the
        // gear button still toggles a visible popup in popout mode. Plain
        // Grid wrapper (no Canvas overlay) — Canvas-wrapped popup races the
        // first-render of the layered window and AVs the client. Trade-off:
        // in popout mode the popup is constrained by the floating chrome's
        // bounds rather than its natural content size; user drags the chrome
        // wider to see all settings.
        if (popupToReparent != null && floatingFrame.Child is Control existingChild)
        {
            OrphanControl(popupToReparent);
            floatingFrame.Child = null;
            popupToReparent.HorizontalAlignment = HorizontalAlignment.Right;
            popupToReparent.VerticalAlignment   = VerticalAlignment.Top;
            popupToReparent.Margin              = new Thickness(0, 40, 8, 0);
            popupToReparent.ZIndex              = 1000;
            var hostGrid = new Grid();
            hostGrid.Children.Add(existingChild);
            hostGrid.Children.Add(popupToReparent);
            floatingFrame.Child = hostGrid;
        }

        // Park the chrome inside a single-child wrapper Border. The wrapper
        // goes in _desktopCanvas at off-bounds coords; the chrome gets
        // arranged at (0, 0) WITHIN the wrapper, so its Bounds.Position is
        // (0, 0). Avalonia's ImmediateRenderer applies visual.Bounds.Position
        // as a translation when rendering a visual, so anchoring the chrome
        // to (0, 0) inside its wrapper is what makes _rtt.Render(floatingFrame)
        // actually fill the RTT instead of rendering the chrome 900+px to
        // the right of it (off the RTT entirely).
        var renderWrapper = new Border
        {
            Background = Brushes.Transparent,
            Child = floatingFrame
        };

        // _desktopCanvas RTT is sized to AC client and won't render anything
        // beyond those bounds, so the chrome is invisible in the in-AC
        // capture even though it lives in the same canvas. FloatingPanelHost
        // renders the chrome into a separate per-panel RTT and pushes pixels
        // to the layered window. Use a fixed large X (well past any AC
        // client width) instead of canvas.Width-relative to handle the
        // popout-during-restore case (canvas.Width may still be the
        // pre-sync default of 800 at that moment).
        double offBoundsX = FloatingOffBoundsX;
        double offBoundsY = _nextFloatingSlotIndex * FloatingOffBoundsSlotPitch;
        _nextFloatingSlotIndex++;

        Canvas.SetLeft(renderWrapper, offBoundsX);
        Canvas.SetTop(renderWrapper, offBoundsY);
        _desktopCanvas.Children.Add(renderWrapper);

        // The chrome's caption is 24px tall; reserve ~60px on the right for
        // the redock + close buttons so HTCAPTION drag doesn't swallow them.
        // Radar is chromeless: caption covers the entire window minus a 22px
        // bottom-right resize zone, so left-click drag works from anywhere on
        // the radar. Right-click is forwarded to Avalonia regardless of
        // HTCAPTION so the context menu still opens.
        bool isRadarPopout   = string.Equals(title, "Radar",   StringComparison.OrdinalIgnoreCase);
        bool isRynthAiPopout = string.Equals(title, "RynthAi", StringComparison.OrdinalIgnoreCase);
        float popoutScale = AvaloniaOverlay.InputScale > 0 ? AvaloniaOverlay.InputScale : 1f;
        // RynthAi: HTCAPTION covers only the title row (~22 px at scale 1) so
        // the OS drag handles movement. The right inset carves out the chips
        // region so Lock/-/+/_/↗ remain clickable instead of starting an OS drag.
        int popoutCaptionHeight = isRadarPopout   ? (int)Math.Max(logicalHeight - 22, 8)
                                : isRynthAiPopout ? (int)Math.Round(22 * popoutScale)
                                : 24;
        // Right inset reserves the top-right strip as HTCLIENT.
        int popoutRightInset = isRynthAiPopout ? (int)Math.Round(140 * popoutScale) : 60;
        FloatingPanelHost host;
        try
        {
            host = new FloatingPanelHost(
                owner: this,
                title: title,
                rootControl: renderWrapper,
                panelBorder: floatingFrame,
                panelContent: content,
                logicalWidth: logicalWidth,
                logicalHeight: logicalHeight,
                offBoundsCanvasX: offBoundsX,
                offBoundsCanvasY: offBoundsY,
                screenLeft: screenLeft,
                screenTop: screenTop,
                captionHeight: popoutCaptionHeight,
                captionRightInset: popoutRightInset,
                closeButtonWidthPx: (isRadarPopout || isRynthAiPopout) ? 0 : 30,
                // Radar and RynthAi manage redock through their own in-panel
                // controls; disable the native LayeredWindow redock zone for them.
                redockButtonWidthPx: (isRadarPopout || isRynthAiPopout) ? 0 : 30,
                // Ctrl-gated click-through: radar popout window forwards mouse
                // input to AC by default; Ctrl re-enables interaction with the
                // gear/dock/resize/drag.
                clickThroughWhenCtrlReleased: isRadarPopout);
        }
        catch (Exception ex)
        {
            RynthLog.Info($"PopOutPanel({title}): FloatingPanelHost ctor threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            // Recovery: pull content back out of the floating chrome (which
            // currently owns it as a Visual parent), then re-attach it to the
            // docked Border's body and put the docked frame back in the
            // canvas so the panel is still visible. Without restoring the
            // body.Child link, the panel reads as "empty window" and the
            // NEXT popout attempt crashes Avalonia because content's Parent
            // points at a discarded floating chrome.
            try
            {
                if (floatingFrame.Child is Grid floatingGrid)
                {
                    foreach (Control child in floatingGrid.Children)
                    {
                        if (child is Border body && ReferenceEquals(body.Child, content))
                        {
                            body.Child = null;
                            break;
                        }
                    }
                }
            }
            catch { }
            try { _desktopCanvas.Children.Remove(renderWrapper); } catch { }
            try
            {
                if (dockedFrame.Child is Grid dockedGrid)
                {
                    foreach (Control child in dockedGrid.Children)
                    {
                        if (child is Border body && body.Child == null)
                        {
                            body.Child = content;
                            break;
                        }
                    }
                }
            }
            catch { }
            try { _desktopCanvas.Children.Add(dockedFrame); _activePanels[title] = dockedFrame; } catch { }
            return;
        }

        _floatingPanels[title] = host;
        RebuildFloatingLayeredSnapshot();
        // _panelContents[title] still references the same content — keep
        // it tracked across the float so a future redock can reparent
        // without rebuilding via the factory.

        PersistFloatingPanelState(title, screenLeft, screenTop, floating: true, open: true);
        RynthLog.Info($"PopOutPanel({title}): host created, layered HWND=0x{host.LayeredWindow.Hwnd.ToInt64():X}, screen=({screenLeft},{screenTop}), size={(int)logicalWidth}x{(int)logicalHeight}, offBounds=({offBoundsX:0},{offBoundsY:0}).");
        RefreshHitTestSnapshot();
        RequestFrameRefresh();
    }

    /// <summary>
    /// Build the chrome Border used while a panel is floating. Visual shape
    /// matches the docked chrome so the user sees a continuous identity, but
    /// the popout button becomes a redock button, and the title-bar drag /
    /// resize-grip have no Avalonia handlers (OS handles them at the layered
    /// window level via HTCAPTION / HTBOTTOMRIGHT).
    /// </summary>
    private Border BuildFloatingChrome(string title, Control content, double logicalWidth, double logicalHeight)
    {
        // Radar pops out chromeless — same as docked: just the panel content,
        // transparent border, no caption, no buttons. Drag is handled by
        // LayeredWindow's HTCAPTION on the entire surface (set in
        // PopOutPanel via captionHeight). Close/redock live on the radar's
        // own right-click context menu.
        if (string.Equals(title, "Radar", StringComparison.OrdinalIgnoreCase))
        {
            return new Border
            {
                Width = logicalWidth,
                Height = logicalHeight,
                MinWidth = 120,
                MinHeight = 120,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                ClipToBounds = false,
                Child = content,
            };
        }

        if (string.Equals(title, "RynthAi", StringComparison.OrdinalIgnoreCase))
        {
            return new Border
            {
                Width = logicalWidth,
                Height = logicalHeight,
                MinWidth = 280,
                MinHeight = 260,
                Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x0A, 0x0F, 0x14)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x40, 0x59)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                ClipToBounds = true,
                Child = content,
            };
        }

        var frame = new Border
        {
            Width = logicalWidth,
            Height = logicalHeight,
            MinWidth = 240,
            MinHeight = 160,
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
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brushes.White
        };

        var redockButton = new Button
        {
            // U+2199 = South-West Arrow — mirror of the docked popout.
            Content = "↙",
            Width = 20,
            Height = 20,
            FontSize = 10,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 2, 2, 2)
        };
        ToolTip.SetTip(redockButton, "Re-dock into the game overlay");
        redockButton.PointerPressed += (_, e) => e.Handled = true;
        redockButton.Click += (_, _) => RedockPanel(title);

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
        closeButton.Click += (_, _) => RequestCloseFloating(title);

        // Drag surface is purely visual — clicks here trigger HTCAPTION at
        // the layered HWND's WM_NCHITTEST and the OS runs its move modal.
        var dragSurface = new Border
        {
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeAll)
        };
        var dragGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto, *") };
        dragGrid.Children.Add(dragGlyph);
        dragGrid.Children.Add(headerTitle);
        Grid.SetColumn(dragGlyph, 0);
        Grid.SetColumn(headerTitle, 1);
        dragSurface.Child = dragGrid;

        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*, Auto, Auto") };
        headerGrid.Children.Add(dragSurface);
        headerGrid.Children.Add(redockButton);
        headerGrid.Children.Add(closeButton);
        Grid.SetColumn(dragSurface, 0);
        Grid.SetColumn(redockButton, 1);
        Grid.SetColumn(closeButton, 2);
        var header = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF1A1A28")),
            Child = headerGrid
        };

        var body = new Border
        {
            Padding = new Thickness(0, 0, 0, 18),
            Child = content
        };

        // Bottom-right grip is purely visual; HTBOTTOMRIGHT in the layered
        // window's WndProc gives the OS-level resize.
        var resizeGlyph = new TextBlock
        {
            Text = "◢",
            FontSize = 14,
            Foreground = Brushes.Teal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        var resizeGrip = new Border
        {
            Width = 22,
            Height = 22,
            Background = new SolidColorBrush(Color.Parse("#2026C1A6")),
            BorderBrush = Brushes.Teal,
            BorderThickness = new Thickness(0, 1, 0, 0),
            CornerRadius = new CornerRadius(0, 0, 4, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 1, 1),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.BottomRightCorner),
            Child = resizeGlyph,
            IsHitTestVisible = false
        };

        grid.Children.Add(header);
        grid.Children.Add(body);
        grid.Children.Add(resizeGrip);
        Grid.SetRow(header, 0);
        Grid.SetRow(body, 1);
        Grid.SetRow(resizeGrip, 1);

        frame.Child = grid;
        return frame;
    }

    /// <summary>
    /// Re-attach a floating panel into the in-AC canvas. Detaches the
    /// content from the floating chrome, disposes the layered window, and
    /// rebuilds docked chrome via RehostContentDocked. The same Control
    /// reference is reparented so internal panel state survives.
    /// </summary>
    internal void RedockPanel(string title)
    {
        RynthLog.Info($"RedockPanel({title}): step1-entry");

        if (!_floatingPanels.TryGetValue(title, out FloatingPanelHost? host) || host == null)
        {
            RynthLog.Info($"RedockPanel({title}): bailing — no floating host.");
            return;
        }

        if (!_registeredPanels.TryGetValue(title, out Func<Control>? factory) || factory == null)
        {
            RynthLog.Info($"RedockPanel({title}): bailing — no factory.");
            return;
        }

        int finalScreenLeft = host.ScreenLeft;
        int finalScreenTop = host.ScreenTop;
        Control content = host.PanelContent;
        Border floatingFrame = host.PanelBorder;
        RynthLog.Info($"RedockPanel({title}): step2-got-host content={content?.GetType().Name} floatingFrame.Child={floatingFrame?.Child?.GetType().Name}");

        // Detach content from the floating chrome.
        try
        {
            if (ReferenceEquals(floatingFrame!.Child, content))
            {
                floatingFrame.Child = null;
            }
            else if (floatingFrame.Child is Grid floatingGrid)
            {
                foreach (Control child in floatingGrid.Children)
                {
                    if (child is Border body && ReferenceEquals(body.Child, content))
                    {
                        body.Child = null;
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            RynthLog.UI($"RedockPanel({title}): content detach failed - {ex.Message}");
        }
        RynthLog.Info($"RedockPanel({title}): step3-detached");

        try { _desktopCanvas.Children.Remove(host.RootControl); } catch (Exception ex) { RynthLog.Info($"RedockPanel({title}): canvas-remove threw {ex.GetType().Name}: {ex.Message}"); }
        RynthLog.Info($"RedockPanel({title}): step4-canvas-removed");

        _floatingPanels.Remove(title);
        RebuildFloatingLayeredSnapshot();
        RynthLog.Info($"RedockPanel({title}): step5-dict-removed");

        try { host.Dispose(); } catch (Exception ex) { RynthLog.Info($"RedockPanel({title}): host.Dispose threw {ex.GetType().Name}: {ex.Message}"); }
        RynthLog.Info($"RedockPanel({title}): step6-host-disposed");

        try { PersistFloatingPanelState(title, finalScreenLeft, finalScreenTop, floating: false, open: true); }
        catch (Exception ex) { RynthLog.Info($"RedockPanel({title}): persist threw {ex.GetType().Name}: {ex.Message}"); }
        RynthLog.Info($"RedockPanel({title}): step7-persisted");

        _panelContents[title] = content;
        RynthLog.Info($"RedockPanel({title}): step8-about-to-rehost");

        try { RehostContentDocked(title, content, factory); }
        catch (Exception ex) { RynthLog.Info($"RedockPanel({title}): rehost threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); }
        RynthLog.Info($"RedockPanel({title}): step9-rehosted");

        try { RefreshHitTestSnapshot(); } catch (Exception ex) { RynthLog.Info($"RedockPanel({title}): refresh-hittest threw {ex.GetType().Name}: {ex.Message}"); }
        try { RequestFrameRefresh(); } catch (Exception ex) { RynthLog.Info($"RedockPanel({title}): request-frame-refresh threw {ex.GetType().Name}: {ex.Message}"); }
        RynthLog.Info($"RedockPanel({title}): step10-done");
    }

    /// <summary>
    /// Build a docked Border that adopts the supplied content (no factory
    /// call). Mirrors the dock-build path in TogglePanel but skips the
    /// factory and AttachDragHandle plumbing — those only matter on first
    /// open, not on a redock that's reparenting an existing control.
    /// </summary>
    private void RehostContentDocked(string title, Control? content, Func<Control> factory)
    {
        // If we have no content (lost it), fall through to the normal
        // open path so the factory builds a fresh control.
        if (content == null)
        {
            TogglePanel(title, factory);
            return;
        }

        bool isMonsters = string.Equals(title, "Monsters", StringComparison.OrdinalIgnoreCase);
        bool isRadar    = string.Equals(title, "Radar",    StringComparison.OrdinalIgnoreCase);
        bool isRynthAi  = string.Equals(title, "RynthAi",  StringComparison.OrdinalIgnoreCase);
        bool hasSaved = PanelStateStore.TryGetPanel(title, out PanelStateStore.PanelEntry saved);
        double defaultW = isMonsters ? 1100 : (isRadar ? 320 : 400);
        double defaultH = isMonsters ? 540 : (isRadar ? 320 : 500);
        var windowFrame = new Border
        {
            Width = hasSaved && saved.Width > 0 ? saved.Width : defaultW,
            Height = hasSaved && saved.Height > 0 ? saved.Height : defaultH,
            MinWidth = 320,
            MinHeight = 240,
            // Radar is chromeless when docked — no border, no rounded corners,
            // transparent background. The gold frame is rendered by the radar
            // surface itself. Mirrors the special-case in TogglePanel.
            Background = isRadar
                ? Brushes.Transparent
                : new SolidColorBrush(Color.Parse("#D212121C")),
            BorderBrush = Brushes.Teal,
            BorderThickness = isRadar ? new Thickness(0) : new Thickness(1),
            CornerRadius = new CornerRadius(isRadar ? 0 : 4),
            ClipToBounds = true
        };

        // Radar and RynthAi always rebuild via factory on redock. Reparenting
        // these panels through dock/undock cycles causes AV in the unmanaged
        // renderer (CSE in NativeAOT, uncatchable). Static state is preserved
        // across rebuilds by each panel's own static fields.
        if (isRadar || isRynthAi)
        {
            _panelContents.Remove(title);
            TogglePanel(title, factory);
            return;
        }

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
        var popoutButton = new Button
        {
            Content = "↗",
            Width = 20,
            Height = 20,
            FontSize = 10,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 2, 2, 2)
        };
        ToolTip.SetTip(popoutButton, "Pop out into a floating window");
        popoutButton.PointerPressed += (_, e) => e.Handled = true;
        popoutButton.Click += (_, _) => PopOutPanel(title);

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
        var dragGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto, *") };
        dragGrid.Children.Add(dragGlyph);
        dragGrid.Children.Add(headerTitle);
        Grid.SetColumn(dragGlyph, 0);
        Grid.SetColumn(headerTitle, 1);
        dragSurface.Child = dragGrid;

        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*, Auto, Auto") };
        headerGrid.Children.Add(dragSurface);
        headerGrid.Children.Add(popoutButton);
        headerGrid.Children.Add(closeButton);
        Grid.SetColumn(dragSurface, 0);
        Grid.SetColumn(popoutButton, 1);
        Grid.SetColumn(closeButton, 2);
        var header = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF1A1A28")),
            Child = headerGrid
        };

        Point dragStartCanvas = default;
        double dragStartLeft = 0;
        double dragStartTop = 0;
        bool isDragging = false;

        dragSurface.PointerPressed += (s, e) =>
        {
            var props = e.GetCurrentPoint(dragSurface).Properties;
            if (!props.IsLeftButtonPressed) return;
            BringPanelToFront(title, windowFrame);
            if (AvaloniaOverlay.TryGetCursorCanvasPosition(out double sx, out double sy))
                dragStartCanvas = new Point(sx, sy);
            else
                dragStartCanvas = e.GetPosition(_desktopCanvas);
            dragStartLeft = GetCanvasLeft(windowFrame);
            dragStartTop = GetCanvasTop(windowFrame);
            isDragging = true;
            AvaloniaOverlay.SetDockedPanelPointerCaptureActive(true);
            e.Pointer.Capture(dragSurface);
            e.Handled = true;
        };
        dragSurface.PointerMoved += (s, e) =>
        {
            var props = e.GetCurrentPoint(dragSurface).Properties;
            if (!props.IsLeftButtonPressed || !isDragging || e.Pointer.Captured != dragSurface) return;
            if (!AvaloniaOverlay.TryGetCursorCanvasPosition(out double cx, out double cy))
            {
                e.Handled = true;
                return;
            }
            double nextLeft = dragStartLeft + (cx - dragStartCanvas.X);
            double nextTop = dragStartTop + (cy - dragStartCanvas.Y);
            SetPanelPosition(windowFrame, nextLeft, nextTop);
            RefreshHitTestSnapshot();
            RequestFrameRefresh();
            e.Handled = true;
        };
        dragSurface.PointerReleased += (s, e) =>
        {
            if (e.Pointer.Captured != dragSurface) return;
            e.Pointer.Capture(null);
            isDragging = false;
            AvaloniaOverlay.SetDockedPanelPointerCaptureActive(false);
            RefreshHitTestSnapshot();
            RequestFrameRefresh();
            PersistPanelState(title, windowFrame, open: true);
            e.Handled = true;
        };
        dragSurface.PointerCaptureLost += (_, _) =>
        {
            isDragging = false;
            AvaloniaOverlay.SetDockedPanelPointerCaptureActive(false);
        };

        var body = new Border { Child = content, Padding = new Thickness(0, 0, 0, 18) };
        var resizeGrip = BuildResizeGrip(title, windowFrame);

        grid.Children.Add(header);
        grid.Children.Add(body);
        grid.Children.Add(resizeGrip);
        Grid.SetRow(header, 0);
        Grid.SetRow(body, 1);
        Grid.SetRow(resizeGrip, 1);
        windowFrame.Child = grid;
        windowFrame.PointerPressed += (_, _) => BringPanelToFront(title, windowFrame);
        _desktopCanvas.Children.Add(windowFrame);

        double initialLeft = hasSaved ? saved.Left : 100 + (_activePanels.Count * 20);
        double initialTop = hasSaved ? saved.Top : 100 + (_activePanels.Count * 20);
        SetPanelPosition(windowFrame, initialLeft, initialTop);
        _activePanels[title] = windowFrame;
        _panelContents[title] = content;

        PersistPanelState(title, windowFrame, open: true);
    }

    /// <summary>
    /// Extracted resize grip used by the redock rehost path. The original
    /// in-line grip wiring in TogglePanel is preserved verbatim — this is a
    /// shim that captures the same closures around windowFrame.
    /// </summary>
    private Border BuildResizeGrip(string title, Border windowFrame)
    {
        var resizeGlyph = new TextBlock
        {
            Text = "◢",
            FontSize = 14,
            Foreground = Brushes.Teal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        var resizeGripIdle = new SolidColorBrush(Color.Parse("#2026C1A6"));
        var resizeGripHover = new SolidColorBrush(Color.Parse("#5526C1A6"));
        var resizeGrip = new Border
        {
            Width = 22,
            Height = 22,
            Background = resizeGripIdle,
            BorderBrush = Brushes.Teal,
            BorderThickness = new Thickness(0, 1, 0, 0),
            CornerRadius = new CornerRadius(0, 0, 4, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 1, 1),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.BottomRightCorner),
            Child = resizeGlyph
        };
        resizeGrip.PointerEntered += (_, _) => resizeGrip.Background = resizeGripHover;
        resizeGrip.PointerExited += (_, _) => resizeGrip.Background = resizeGripIdle;

        Point resizeStartCanvas = default;
        double resizeStartWidth = 0;
        double resizeStartHeight = 0;
        bool isResizing = false;

        resizeGrip.PointerPressed += (s, e) =>
        {
            var props = e.GetCurrentPoint(resizeGrip).Properties;
            if (!props.IsLeftButtonPressed) return;
            BringPanelToFront(title, windowFrame);
            resizeStartWidth = windowFrame.Width;
            resizeStartHeight = windowFrame.Height;
            if (AvaloniaOverlay.TryGetCursorCanvasPosition(out double sx, out double sy))
                resizeStartCanvas = new Point(sx, sy);
            else
                resizeStartCanvas = e.GetPosition(_desktopCanvas);
            isResizing = true;
            AvaloniaOverlay.SetDockedPanelPointerCaptureActive(true);
            e.Pointer.Capture(resizeGrip);
            e.Handled = true;
        };
        resizeGrip.PointerMoved += (s, e) =>
        {
            var props = e.GetCurrentPoint(resizeGrip).Properties;
            if (!props.IsLeftButtonPressed || !isResizing || e.Pointer.Captured != resizeGrip) return;
            if (!AvaloniaOverlay.TryGetCursorCanvasPosition(out double cx, out double cy))
            {
                e.Handled = true;
                return;
            }
            double requestedWidth = resizeStartWidth + (cx - resizeStartCanvas.X);
            double requestedHeight = resizeStartHeight + (cy - resizeStartCanvas.Y);
            SetPanelSize(windowFrame, requestedWidth, requestedHeight);
            SetPanelPosition(windowFrame, GetCanvasLeft(windowFrame), GetCanvasTop(windowFrame));
            RefreshHitTestSnapshot();
            RequestFrameRefresh();
            e.Handled = true;
        };
        resizeGrip.PointerReleased += (s, e) =>
        {
            if (e.Pointer.Captured != resizeGrip) return;
            e.Pointer.Capture(null);
            isResizing = false;
            AvaloniaOverlay.SetDockedPanelPointerCaptureActive(false);
            RefreshHitTestSnapshot();
            RequestFrameRefresh();
            PersistPanelState(title, windowFrame, open: true);
            e.Handled = true;
        };
        resizeGrip.PointerCaptureLost += (_, _) =>
        {
            isResizing = false;
            AvaloniaOverlay.SetDockedPanelPointerCaptureActive(false);
        };

        return resizeGrip;
    }

    /// <summary>
    /// Called by FloatingPanelHost when its layered HWND finishes moving
    /// (HTCAPTION drag). Persists screen coords without disturbing docked
    /// geometry.
    /// </summary>
    internal void OnFloatingPanelMoved(string title, int screenLeft, int screenTop)
    {
        PersistFloatingPanelState(title, screenLeft, screenTop, floating: true, open: true);
    }

    /// <summary>
    /// Called by FloatingPanelHost when the user clicks the close button on
    /// the floating chrome. We post the close to the dispatcher so the click
    /// handler completes (and Avalonia finishes its routed-event walk)
    /// before we mutate _floatingPanels and tear down the layered HWND.
    /// </summary>
    internal void RequestCloseFloating(string title)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_floatingPanels.TryGetValue(title, out FloatingPanelHost? host) && host != null)
                CloseFloatingPanel(title, host);
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Called from LayeredWindow's WndProc when the user clicks the redock
    /// button region. Posts the actual RedockPanel call to the dispatcher
    /// so the WndProc returns first (and any in-flight Win32 capture state
    /// settles) before we mutate _floatingPanels and reattach to the canvas.
    /// </summary>
    internal void RequestRedock(string title)
    {
        Dispatcher.UIThread.Post(() => RedockPanel(title), DispatcherPriority.Background);
    }

    /// <summary>
    /// Persist every currently-docked panel as open=true with its live
    /// position/size so the next launch restores it. Called from
    /// AvaloniaOverlay.Stop on engine reload / process exit. Safe here:
    /// PersistPanelState only reads the panel's current canvas position (it
    /// never clamps), so the coords captured are exactly where the user left
    /// the panel — none of the placeholder-canvas corruption the open path
    /// guards against. The floating-panel equivalent is CloseAllFloatingPanels.
    /// </summary>
    internal void PersistOpenDockedPanels()
    {
        if (_activePanels.Count == 0) return;

        foreach ((string title, Border panel) in _activePanels.ToArray())
        {
            try
            {
                PersistPanelState(title, panel, open: true);
            }
            catch (Exception ex)
            {
                RynthLog.UI($"AvaloniaOverlay.PersistOpenDockedPanels({title}): {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Tear down every floating panel host. Called from AvaloniaOverlay.Stop
    /// so engine reload / process exit doesn't leak Win32 HWNDs or DIBs.
    /// We persist each panel's open=true + screen coords so the next launch
    /// restores them in their floating positions.
    /// </summary>
    internal void CloseAllFloatingPanels()
    {
        if (_floatingPanels.Count == 0) return;

        var titles = _floatingPanels.Keys.ToArray();
        foreach (string title in titles)
        {
            if (!_floatingPanels.TryGetValue(title, out FloatingPanelHost? host) || host == null)
                continue;

            try
            {
                PersistFloatingPanelState(title, host.ScreenLeft, host.ScreenTop, floating: true, open: true);
                try { _desktopCanvas.Children.Remove(host.RootControl); } catch { /* ignore */ }
                host.Dispose();
            }
            catch (Exception ex)
            {
                RynthLog.UI($"AvaloniaOverlay.CloseAllFloatingPanels({title}): {ex.GetType().Name}: {ex.Message}");
            }
        }

        _floatingPanels.Clear();
        RebuildFloatingLayeredSnapshot();
        _panelContents.Clear();
    }

    private static void PersistPanelState(string title, Border panel, bool open)
    {
        try
        {
            double left = GetCanvasLeft(panel);
            double top = GetCanvasTop(panel);
            double width = panel.Width > 0 ? panel.Width : panel.Bounds.Width;
            double height = panel.Height > 0 ? panel.Height : panel.Bounds.Height;
            if (string.Equals(title, "Radar", StringComparison.OrdinalIgnoreCase))
            {
                try { RynthLog.UI($"PersistPanelState(Radar): pos=({left:F1},{top:F1}) size=({width:F1}x{height:F1}) open={open}"); } catch { }
            }

            // Preserve any previously-saved floating coords so a popout-then-
            // redock-then-quit cycle still remembers where the floating window
            // wants to live next time the user pops out.
            bool floating = false;
            double floatingLeft = 0;
            double floatingTop = 0;
            if (PanelStateStore.TryGetPanel(title, out PanelStateStore.PanelEntry prior))
            {
                floatingLeft = prior.FloatingLeft;
                floatingTop = prior.FloatingTop;
            }

            PanelStateStore.SetPanel(title, new PanelStateStore.PanelEntry(
                open, left, top, width, height, floating, floatingLeft, floatingTop));
        }
        catch
        {
            // Persistence is best-effort — never let it bring down the UI.
        }
    }

    /// <summary>
    /// Persist the screen-space position of a floating panel without
    /// disturbing its cached docked geometry. Used when the user drags the
    /// floating window around or when popout/redock toggles flip the mode.
    /// </summary>
    private static void PersistFloatingPanelState(string title, double screenLeft, double screenTop, bool floating, bool open)
    {
        try
        {
            // Start from any prior entry so docked Left/Top/Width/Height are
            // preserved across the popout — that's what redock will fall back
            // onto when restoring the in-canvas size.
            double left = 0;
            double top = 0;
            double width = 0;
            double height = 0;
            if (PanelStateStore.TryGetPanel(title, out PanelStateStore.PanelEntry prior))
            {
                left = prior.Left;
                top = prior.Top;
                width = prior.Width;
                height = prior.Height;
            }

            PanelStateStore.SetPanel(title, new PanelStateStore.PanelEntry(
                open, left, top, width, height, floating, screenLeft, screenTop));
        }
        catch
        {
            // Persistence is best-effort — never let it bring down the UI.
        }
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

    /// <summary>
    /// Walk up from <paramref name="control"/>'s current visual parent and
    /// detach it. Avalonia's Border.Child / ContentControl.Content / Panel.
    /// Children setters all throw "Visual already has a parent" if you assign
    /// a control that's still attached, so call this defensively before any
    /// reparent (PopOut → Redock cycles especially).
    /// </summary>
    private static void OrphanControl(Control? control)
    {
        if (control == null) return;
        Visual? parent = control.GetVisualParent();
        if (parent == null) return;
        try
        {
            switch (parent)
            {
                case Border b when ReferenceEquals(b.Child, control):
                    b.Child = null;
                    break;
                case Decorator d when ReferenceEquals(d.Child, control):
                    d.Child = null;
                    break;
                case ContentControl cc when ReferenceEquals(cc.Content, control):
                    cc.Content = null;
                    break;
                case Panel p when p.Children.Contains(control):
                    p.Children.Remove(control);
                    break;
            }
        }
        catch (Exception ex)
        {
            try { RynthLog.UI($"AvaloniaOverlay.OrphanControl: detach from {parent.GetType().Name} threw {ex.GetType().Name}: {ex.Message}"); } catch { }
        }
    }

    private void SetPanelPosition(Border panel, double requestedLeft, double requestedTop)
    {
        // Skip clamping when the canvas doesn't yet have the real game viewport
        // size. The canvas starts at 800×600 and CaptureNow syncs it to the
        // actual game resolution once EndScene starts reporting. If we clamp
        // against the placeholder 800×600 bounds, panels restored from saved
        // positions get pushed into the upper-left corner. Two conditions must
        // both hold before we trust the canvas bounds for clamping:
        //   1. Canvas has been laid out (Bounds > 0)
        //   2. The game viewport is known (ViewportWidth set by OverlayTextureRenderer)
        bool canvasReady = _desktopCanvas.Bounds.Width > 0 &&
                           _desktopCanvas.Bounds.Height > 0 &&
                           TryGetTargetGameSurfacePixels(out _, out _);
        if (!canvasReady)
        {
            Canvas.SetLeft(panel, requestedLeft);
            Canvas.SetTop(panel, requestedTop);
            return;
        }

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
        // Only grow — see FloatingPanelHost.Tick for the LFH-corruption rationale.
        if (_softwareBuffer == null || _softwareBuffer.Length < byteCount)
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
        try { PanelStateStore.SetBarPosition(nextLeft, nextTop); } catch { }
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

    private long _lastFrameRefreshTick;

    private void RequestFrameRefresh()
    {
        _captureDirty = true;

        // Throttle the immediate render call to 60 FPS. This used to fire
        // RenderCustomProducerFrame on every caller (including the per-
        // PointerMoved drag handler firing 100+ Hz), which saturated the
        // Avalonia dispatcher with InvalidateVisual work and starved the
        // RynthAiPanel's 100 ms snapshot timer — so dragging or resizing
        // froze the panel's vital bars and other live data. _captureTimer
        // (16 ms, DispatcherPriority.Render) still picks up _captureDirty
        // on its own cadence, so any moves we skip here get rendered on
        // the next tick.
        long now = Environment.TickCount64;
        if (now - _lastFrameRefreshTick < 16)
            return;
        _lastFrameRefreshTick = now;

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
        (double Left, double Top, double Width, double Height)? radarRect = null;
        foreach ((string title, Border panel) in _activePanels)
        {
            Point? origin = panel.TranslatePoint(default, _desktopCanvas);
            var rect = (
                (origin?.X ?? Canvas.GetLeft(panel)) * inputScale,
                (origin?.Y ?? Canvas.GetTop(panel)) * inputScale,
                panel.Width * inputScale,
                panel.Height * inputScale);
            if (string.Equals(title, "Radar", StringComparison.OrdinalIgnoreCase))
                radarRect = rect;
            else
                rects.Add(rect);
        }

        AvaloniaOverlay.PublishPanelRects(rects.ToArray());
        AvaloniaOverlay.PublishRadarPanelRect(radarRect);
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

    /// <summary>
    /// Render every active floating panel into its dedicated layered window.
    /// Called from the Avalonia UI thread alongside the canvas capture so the
    /// floating panels stay in lockstep with their docked counterparts. The
    /// panel Border lives in _desktopCanvas at off-bounds coords (so layout +
    /// input still work), and we render JUST that subtree to a per-panel RTT.
    /// </summary>
    internal void TickFloatingPanels()
    {
        if (_floatingPanels.Count == 0) return;
        foreach (FloatingPanelHost host in _floatingPanels.Values)
        {
            try { host.Tick(); }
            catch (Exception ex)
            {
                RynthLog.Info($"FloatingPanelHost.Tick({host.Title}) threw {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}

/// <summary>
/// Owns the off-screen rendering and native HWND for a popped-out panel.
/// Holds a per-panel RenderTargetBitmap that captures the panel's chrome
/// Border (which stays parented in _desktopCanvas at coords that are clipped
/// out of the in-AC capture) and pushes the BGRA pixels into a LayeredWindow
/// via UpdateLayeredWindow. Mouse messages from the layered HWND are
/// forwarded to the off-screen Avalonia HWND with translated coords so the
/// panel's buttons/scrollbars/etc. behave the same as when docked.
///
/// This deliberately does NOT use Avalonia's Window — a second Avalonia
/// top-level routes through OverlaySkiaPlatformGraphics' singleton render
/// target and thrashes the in-AC capture (the original "flashing" bug).
/// </summary>
internal sealed unsafe class FloatingPanelHost : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public string Title { get; }
    /// <summary>
    /// The Avalonia control we add to / remove from _desktopCanvas. Wraps
    /// PanelBorder so that PanelBorder.Bounds.Position is (0, 0) inside the
    /// wrapper — required for ImmediateRenderer to render the chrome at the
    /// RTT origin instead of at the off-bounds canvas position.
    /// </summary>
    public Control RootControl { get; }
    public Border PanelBorder { get; }
    public Control PanelContent { get; }
    public LayeredWindow LayeredWindow => _layered;
    public double OffBoundsCanvasX { get; }
    public double OffBoundsCanvasY { get; }
    public int ScreenLeft => _layered.ScreenLeft;
    public int ScreenTop => _layered.ScreenTop;
    /// <summary>Set by ForwardInput before PostMessage(WM_LBUTTONDOWN) so that
    /// AvaloniaSubclassWndProc can translate SetCapture-routed WM_MOUSEMOVE back
    /// to Avalonia off-bounds coordinates instead of game-client coordinates.</summary>
    internal static volatile FloatingPanelHost? PointerCapturingHost;
    /// <summary>
    /// True for panels whose click-through is driven by their own settings
    /// (e.g. radar's Ctrl-gated mode). The eager docked-drag path skips them
    /// so a docked drag doesn't override the user's gating preference.
    /// </summary>
    public bool IsClickThroughGated => _clickThroughGated;

    private readonly RynthOverlayWindow _owner;
    private readonly LayeredWindow _layered;
    private RenderTargetBitmap? _rtt;
    private byte[]? _pixelBuffer;
    private double _logicalWidth;
    private double _logicalHeight;
    private bool _disposed;

    private int _logCount;
    private bool _ownerApplied;
    private readonly bool _clickThroughGated;

    public FloatingPanelHost(
        RynthOverlayWindow owner,
        string title,
        Control rootControl,
        Border panelBorder,
        Control panelContent,
        double logicalWidth,
        double logicalHeight,
        double offBoundsCanvasX,
        double offBoundsCanvasY,
        int screenLeft,
        int screenTop,
        int captionHeight,
        int captionRightInset,
        int closeButtonWidthPx = 30,
        int redockButtonWidthPx = 30,
        bool clickThroughWhenCtrlReleased = false)
    {
        _owner = owner;
        Title = title;
        RootControl = rootControl;
        PanelBorder = panelBorder;
        PanelContent = panelContent;
        _logicalWidth = Math.Max(logicalWidth, 1);
        _logicalHeight = Math.Max(logicalHeight, 1);
        OffBoundsCanvasX = offBoundsCanvasX;
        OffBoundsCanvasY = offBoundsCanvasY;
        _clickThroughGated = clickThroughWhenCtrlReleased;

        float scale = AvaloniaOverlay.InputScale > 0 ? AvaloniaOverlay.InputScale : 1f;
        int pxW = Math.Max(1, (int)Math.Round(_logicalWidth * scale));
        int pxH = Math.Max(1, (int)Math.Round(_logicalHeight * scale));

        RynthLog.Info($"FloatingPanelHost({title}): creating LayeredWindow {pxW}x{pxH} at screen ({screenLeft},{screenTop}), scale={scale}.");

        // Own the layered window to AC so it stays above AC's window in
        // Z-order without going globally topmost. If we don't have AC's
        // HWND yet (popout fired before ImGui init), pass IntPtr.Zero;
        // Tick will SetOwner once GameHwnd becomes available so the
        // window snaps above AC at next opportunity.
        IntPtr ownerHwnd = ImGuiBackend.Win32Backend.GameHwnd;
        _layered = new LayeredWindow(pxW, pxH, screenLeft, screenTop, ownerHwnd)
        {
            CaptionHeight = captionHeight,
            CaptionRightInset = captionRightInset,
            // Each chrome button is 20×20 logical + ~4px margin = ~24
            // logical = 30 physical at scale 1.25. Use 30 as a round
            // value that covers the button hit area at common DPI.
            // Radar uses 0 for close so its gear-button area doesn't fire
            // OnCloseClicked; the user redocks or toggles the bar button.
            CloseButtonWidthPx = closeButtonWidthPx,
            RedockButtonWidthPx = redockButtonWidthPx,
            OnInput = ForwardInput,
            OnMoved = OnLayeredMoved,
            OnResized = OnLayeredResized,
            // Native click handling for the two chrome buttons — Avalonia's
            // hit-test drops forwarded clicks at canvas X past AC client
            // (the panel chrome lives at canvas X = FloatingOffBoundsX),
            // so we resolve those buttons at the WndProc level instead.
            OnRedockClicked = () => _owner.RequestRedock(title),
            OnCloseClicked  = () => _owner.RequestCloseFloating(title)
        };
        if (ownerHwnd != IntPtr.Zero)
            _ownerApplied = true;
        _layered.Show();
        RynthLog.Info($"FloatingPanelHost({title}): LayeredWindow shown, HWND=0x{_layered.Hwnd.ToInt64():X}.");
    }

    /// <summary>
    /// Capture-and-blit one frame. Called from the Avalonia UI thread by
    /// RynthOverlayWindow's _captureTimer so render and panel state stay in
    /// sync. Skips the heavy work if the panel hasn't been arranged yet.
    /// </summary>
    public void Tick()
    {
        if (_disposed || _layered.Hwnd == IntPtr.Zero) return;

        // Late-binding owner: if we created the layered window before ImGui
        // had captured AC's HWND, retro-fit the owner relationship now.
        // Without this, the layered window has no Z-order tie to AC and
        // ends up behind every other top-level on the desktop.
        if (!_ownerApplied)
        {
            IntPtr nowOwner = ImGuiBackend.Win32Backend.GameHwnd;
            if (nowOwner != IntPtr.Zero)
            {
                _layered.SetOwner(nowOwner);
                _ownerApplied = true;
            }
        }

        // Click-through management. SetClickThrough is a no-op if the state
        // hasn't changed, so it's cheap to call every frame.
        if (_clickThroughGated)
        {
            // Radar: Ctrl-gated setting from user prefs.
            bool wantClickThrough =
                AvaloniaOverlay.RadarCtrlGatedClickThrough && !AvaloniaOverlay.IsCtrlHeld();
            _layered.SetClickThrough(wantClickThrough);
        }
        else
        {
            // All other floating panels: become fully click-through while a
            // docked-panel drag is in progress. Without this, the floating
            // window sits above AC in Z-order and intercepts WM_MOUSEMOVE
            // whenever the cursor enters its area — AC's Win32Backend stops
            // receiving mouse messages, Avalonia never gets PointerMoved, and
            // the docked drag freezes immediately. WS_EX_TRANSPARENT routes
            // all mouse events to AC so the drag continues smoothly and the
            // WM_LBUTTONUP that ends the drag reaches Avalonia correctly.
            _layered.SetClickThrough(AvaloniaOverlay.DockedPanelPointerCaptureActive);
        }

        double w = PanelBorder.Bounds.Width > 0 ? PanelBorder.Bounds.Width : PanelBorder.Width;
        double h = PanelBorder.Bounds.Height > 0 ? PanelBorder.Bounds.Height : PanelBorder.Height;
        if (w <= 0 || h <= 0)
        {
            if (_logCount < 4)
            {
                _logCount++;
                RynthLog.Info($"FloatingPanelHost({Title}): Tick skipped — Bounds={PanelBorder.Bounds.Width:0.#}x{PanelBorder.Bounds.Height:0.#}, Width/Height={PanelBorder.Width:0.#}x{PanelBorder.Height:0.#}.");
            }
            return;
        }
        if (_logCount < 2)
        {
            _logCount++;
            RynthLog.Info($"FloatingPanelHost({Title}): Tick #{_logCount} bounds={w:0.#}x{h:0.#}.");
        }

        _logicalWidth = w;
        _logicalHeight = h;

        float scale = AvaloniaOverlay.InputScale > 0 ? AvaloniaOverlay.InputScale : 1f;
        int pxW = Math.Max(1, (int)Math.Round(w * scale));
        int pxH = Math.Max(1, (int)Math.Round(h * scale));

        // Radar's overlay buttons live inside the gold-frame interior, which
        // sits at variable distances from the panel's right edge depending on
        // the surface's aspect ratio (square radar centred in a wider/taller
        // surface). Keep CaptionRightInset wide enough that the entire button
        // strip falls in the HTCLIENT zone — otherwise OS drag eats the
        // clicks for the leftmost button of the strip (only the rightmost
        // button — gear — is far enough right to fall outside HTCAPTION).
        //
        // CaptionRightInset is compared against physical pixels in
        // LayeredWindow's WM_NCHITTEST handler (cx and Width are physical),
        // so we compute the inset in physical pixels too.
        if (string.Equals(Title, "Radar", StringComparison.OrdinalIgnoreCase))
        {
            int desiredInset = Math.Max(120, (int)Math.Ceiling(w / 3.0 * scale));
            if (_layered.CaptionRightInset != desiredInset)
                _layered.CaptionRightInset = desiredInset;

            // When the gear-popup settings panel is open, shrink the
            // HTCAPTION drag region to the top sliver only — otherwise OS
            // caption-drag eats the click on every checkbox/slider inside
            // the settings panel (which sits well below the top edge but
            // inside the default ~91% caption zone). With this, drag still
            // works from the very top strip; settings content below it is
            // HTCLIENT and our hit-test routes the clicks to the controls.
            // The 40 logical-pixel cap leaves a couple of pixels above the
            // settings panel (whose top edge is ~46 logical) so layout
            // jitter can't put the settings into the HTCAPTION zone.
            int desiredCaption = Panels.RadarPanel.IsSettingsVisible
                ? (int)Math.Floor(40 * scale)
                : (int)Math.Max(8, (h - 22) * scale);
            if (_layered.CaptionHeight != desiredCaption)
                _layered.CaptionHeight = desiredCaption;
        }

        // Only grow — never shrink. Pop-out triggers a flurry of size oscillations
        // for several seconds while OS layout, Avalonia layout, and DPI settle.
        // Reallocating the RenderTargetBitmap (Skia surface in native heap) and
        // the BGRA byte[] (LOH) on every pixel difference produced the LFH
        // freelist corruption bisected to ntdll!RtlpHeap RVA 0x5B10C. CopyPixels
        // takes an explicit rect so an RTT larger than (pxW,pxH) is safe.
        if (_rtt == null || _rtt.PixelSize.Width < pxW || _rtt.PixelSize.Height < pxH)
        {
            _rtt?.Dispose();
            _rtt = new RenderTargetBitmap(new PixelSize(pxW, pxH), new Vector(96 * scale, 96 * scale));
        }

        // RenderTargetBitmap.Render walks the Border's visual subtree and
        // produces premultiplied BGRA pixels — the exact format
        // UpdateLayeredWindow expects with AC_SRC_ALPHA.
        _rtt.Render(PanelBorder);

        int byteCount = pxW * pxH * 4;
        if (_pixelBuffer == null || _pixelBuffer.Length < byteCount)
            _pixelBuffer = new byte[byteCount];

        fixed (byte* p = _pixelBuffer)
        {
            _rtt.CopyPixels(new PixelRect(0, 0, pxW, pxH), (IntPtr)p, byteCount, pxW * 4);
            _layered.UpdatePixels((IntPtr)p, pxW, pxH, pxW * 4);
        }
    }

    public void MoveScreen(int screenLeft, int screenTop) => _layered.Move(screenLeft, screenTop);

    private int _inputLogCount;
    // Slider currently being dragged via the popped-out geometry path. The
    // LayeredWindow holds OS capture for the whole press, so DOWN/MOVE/UP all
    // arrive in ForwardInput; this tracks the target across the drag.
    private Slider? _activeSlider;
    private ScrollViewer? _activeScroll;

    private void ForwardInput(uint msg, IntPtr wParam, IntPtr lParam, int layeredClientX, int layeredClientY)
    {
        // Radar's overlay buttons sit inside the panel content. Avalonia's
        // WM_LBUTTONDOWN dispatch routes events to the AvaloniaHwnd but
        // doesn't deliver them to controls on panels parked off-canvas
        // (canvas X ≈ 2500). Workaround: RadarPanel publishes a snapshot
        // of its visible button bounds (panel-content-relative logical px)
        // every layout update; we hit-test against that snapshot here and
        // invoke the matching forwarder. Bypasses Avalonia's broken WM
        // dispatch for radar buttons specifically.
        // Intercept on WM_LBUTTONDOWN, not LBUTTONUP. Avalonia's WM dispatch
        // calls SetCapture on its own HWND when it processes a forwarded
        // LBUTTONDOWN, which steals capture from the LayeredWindow — the
        // matching LBUTTONUP then goes to AvaloniaHwnd and never reaches
        // this WndProc, so an LBUTTONUP-based intercept can never fire.
        const uint WM_LBUTTONDOWN = 0x0201;
        const uint WM_MOUSEWHEEL  = 0x020A;

        // Radar wheel → zoom. Win32's wParam high word holds the wheel
        // delta as a signed short, in WHEEL_DELTA (120) units per notch —
        // divide by 120 to match Avalonia's PointerWheelEventArgs.Delta.Y.
        if (msg == WM_MOUSEWHEEL &&
            string.Equals(Title, "Radar", StringComparison.OrdinalIgnoreCase))
        {
            short wheel = (short)((wParam.ToInt32() >> 16) & 0xFFFF);
            double deltaY = wheel / 120.0;
            try { Panels.RadarPanel.OnWheelDelta?.Invoke(deltaY); } catch { }
            return;
        }

        if (msg == WM_LBUTTONDOWN &&
            string.Equals(Title, "Radar", StringComparison.OrdinalIgnoreCase))
        {
            var snap = Panels.RadarPanel.ButtonBoundsSnapshot;
            float hitScale = AvaloniaOverlay.InputScale > 0 ? AvaloniaOverlay.InputScale : 1f;
            double lx = layeredClientX / hitScale;
            double ly = layeredClientY / hitScale;
            RynthLog.UI($"FloatingPanelHost(Radar): LBUTTONDOWN layered=({layeredClientX},{layeredClientY}) logical=({lx:F1},{ly:F1}) snap={(snap?.Length ?? -1)}");
            if (snap != null && snap.Length > 0)
            {
                foreach (var b in snap)
                {
                    RynthLog.UI($"  candidate {b.Tag} at ({b.X:F1},{b.Y:F1}) {b.W:F1}x{b.H:F1}");
                    if (lx >= b.X && lx <= b.X + b.W && ly >= b.Y && ly <= b.Y + b.H)
                    {
                        RynthLog.UI($"FloatingPanelHost(Radar): snapshot hit → {b.Tag}");
                        switch (b.Tag)
                        {
                            case "radar.gear":   Panels.RadarPanel.OnGearClicked?.Invoke();   return;
                            case "radar.popout": Panels.RadarPanel.OnPopoutClicked?.Invoke(); return;
                            case "radar.redock": Panels.RadarPanel.OnRedockClicked?.Invoke(); return;
                        }
                    }
                }
            }
        }

        // General Button hit-test for popped-out panel content. Avalonia's
        // built-in InputHitTest translates the test point up to the visual
        // root (the off-screen Avalonia Window) and hits the root's
        // renderer — but the panel Border lives at canvas X = 2500, outside
        // the Window's render bounds, so the renderer always rejects the
        // hit. Instead we walk PanelBorder's visual subtree directly using
        // each child's Bounds (parent-space rectangles, populated by layout
        // regardless of where the Border sits in the canvas), and raise
        // Click on the deepest Button under the cursor. Handles Buttons in
        // any popped-out panel content (Monsters rules, Items toolbar,
        // Settings toggles, etc.). Other control types (TextBox, scroll-
        // bars) still fall through and remain unresponsive until we add
        // general pointer dispatch.
        //
        // Fire on DOWN (not UP): same reason as the radar snapshot block —
        // the PostMessage fall-through fires DOWN, Avalonia steals capture
        // from the LayeredWindow, and the matching UP never reaches this
        // WndProc.
        if (msg == WM_LBUTTONDOWN)
        {
            float hitScale = AvaloniaOverlay.InputScale > 0 ? AvaloniaOverlay.InputScale : 1f;
            double lx = layeredClientX / hitScale;
            double ly = layeredClientY / hitScale;
            try
            {
                Button? btn = FindDeepestButtonAt(PanelBorder, new Point(lx, ly));
                RynthLog.Info($"FloatingPanelHost({Title}): LBUTTONDOWN logical=({lx:F1},{ly:F1}) btn={btn?.Content ?? "null"} enabled={btn?.IsEffectivelyEnabled} visible={btn?.IsEffectivelyVisible}.");
                if (btn != null && btn.IsEffectivelyEnabled && btn.IsEffectivelyVisible)
                {
                    RynthLog.Info($"FloatingPanelHost({Title}): button hit '{btn.Content}' at logical=({lx:F1},{ly:F1}).");
                    // Defer the action to a Dispatcher post — the click
                    // handler may tear down the LayeredWindow (e.g. redock
                    // disposes the host, which calls DestroyWindow on the
                    // HWND whose WndProc we're currently inside) and that's
                    // undefined behavior if it runs synchronously here.
                    Button capturedBtn = btn;
                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            // ToggleButton (CheckBox, RadioButton, ToggleButton)
                            // implements toggle inside its OnClick override —
                            // raising ClickEvent directly skips OnClick, so
                            // the IsChecked state never flips and the
                            // IsCheckedChanged subscribers don't fire. Toggle
                            // IsChecked manually for those, which raises
                            // IsCheckedChanged via the property setter.
                            if (capturedBtn is Avalonia.Controls.Primitives.ToggleButton tb)
                            {
                                tb.IsChecked = tb.IsChecked != true;
                            }
                            try { capturedBtn.Command?.Execute(capturedBtn.CommandParameter); } catch { }
                            capturedBtn.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent) { Source = capturedBtn });
                        }
                        catch (Exception ex)
                        {
                            RynthLog.Info($"FloatingPanelHost({Title}): deferred Click raise threw {ex.GetType().Name}: {ex.Message}");
                        }
                    });
                    return;
                }
            }
            catch (Exception ex)
            {
                RynthLog.Info($"FloatingPanelHost({Title}): button hit-test threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        // --- Slider drag for popped-out content -----------------------------
        // Same rationale as the Button block above: the panel Border is parked
        // off-bounds (canvas X ≈ 2500) so Avalonia's renderer hit-test rejects
        // forwarded WMs and a Slider never sees them. Drive Slider.Value
        // directly from cursor geometry instead. The LayeredWindow holds OS
        // capture for the whole press, so DOWN starts the drag and MOVE/UP
        // continue it. Placed before the DockedPanelPointerCaptureActive
        // early-return below so an in-progress slider drag always completes.
        {
            const uint WM_MOUSEMOVE_S = 0x0200;
            const uint WM_LBUTTONUP_S = 0x0202;
            if (msg == WM_LBUTTONDOWN || (_activeSlider != null && (msg == WM_MOUSEMOVE_S || msg == WM_LBUTTONUP_S)))
            {
                float sScale = AvaloniaOverlay.InputScale > 0 ? AvaloniaOverlay.InputScale : 1f;
                double splx = layeredClientX / sScale;
                double sply = layeredClientY / sScale;
                Slider? slider = msg == WM_LBUTTONDOWN
                    ? FindDeepestSliderAt(PanelBorder, new Point(splx, sply))
                    : _activeSlider;
                if (slider != null && slider.IsEffectivelyEnabled && slider.IsEffectivelyVisible)
                {
                    if (msg == WM_LBUTTONDOWN) _activeSlider = slider;
                    Slider captured = slider;
                    double cx = splx, cy = sply;
                    bool end = msg == WM_LBUTTONUP_S;
                    Dispatcher.UIThread.Post(() =>
                    {
                        try { DriveSliderFromPanelPoint(captured, cx, cy); }
                        catch (Exception ex) { RynthLog.Info($"FloatingPanelHost({Title}): slider drive threw {ex.GetType().Name}: {ex.Message}"); }
                    });
                    if (end) _activeSlider = null;
                    return;
                }
                if (msg != WM_LBUTTONDOWN)
                {
                    // Active-slider MOVE/UP but the target vanished (panel
                    // redocked/closed mid-drag): stop, don't forward.
                    if (msg == WM_LBUTTONUP_S) _activeSlider = null;
                    return;
                }
                // WM_LBUTTONDOWN not over a slider: fall through to the
                // existing PostMessage path below.
            }
        }

        // --- Scrollbar drag for popped-out content --------------------------
        // Same off-bounds rationale as Button/Slider: the parked Border's
        // scrollbar never sees Avalonia's renderer hit-test, so a press on it
        // does nothing. Detect a press on a *vertical* ScrollBar, then drive
        // the owning ScrollViewer.Offset directly from cursor geometry. The
        // LayeredWindow holds OS capture for the whole press, so DOWN starts
        // the drag and MOVE/UP continue it. Placed before the
        // DockedPanelPointerCaptureActive early-return so an in-progress drag
        // always completes.
        {
            const uint WM_MOUSEMOVE_SB = 0x0200;
            const uint WM_LBUTTONUP_SB = 0x0202;
            if (msg == WM_LBUTTONDOWN || (_activeScroll != null && (msg == WM_MOUSEMOVE_SB || msg == WM_LBUTTONUP_SB)))
            {
                float sbScale = AvaloniaOverlay.InputScale > 0 ? AvaloniaOverlay.InputScale : 1f;
                double sblx = layeredClientX / sbScale;
                double sbly = layeredClientY / sbScale;
                ScrollViewer? sv = _activeScroll;
                if (msg == WM_LBUTTONDOWN)
                {
                    var bar = FindDeepestScrollBarAt(PanelBorder, new Point(sblx, sbly));
                    sv = (bar != null && bar.Orientation == Orientation.Vertical)
                        ? bar.FindAncestorOfType<ScrollViewer>()
                        : null;
                }
                if (sv != null)
                {
                    if (msg == WM_LBUTTONDOWN) _activeScroll = sv;
                    ScrollViewer captured = sv;
                    double cy = sbly;
                    bool end = msg == WM_LBUTTONUP_SB;
                    Dispatcher.UIThread.Post(() =>
                    {
                        try { DriveScrollFromPanelPoint(captured, cy); }
                        catch (Exception ex) { RynthLog.Info($"FloatingPanelHost({Title}): scroll drive threw {ex.GetType().Name}: {ex.Message}"); }
                    });
                    if (end) _activeScroll = null;
                    return;
                }
                if (msg != WM_LBUTTONDOWN)
                {
                    // Active-scroll MOVE/UP but the target vanished (panel
                    // redocked/closed mid-drag): stop, don't forward.
                    if (msg == WM_LBUTTONUP_SB) _activeScroll = null;
                    return;
                }
                // WM_LBUTTONDOWN not over a scrollbar: fall through.
            }
        }

        // Translate from layered-client (physical px) to Avalonia-client
        // (also physical px). The panel Border is parked at canvas LOGICAL
        // coord (OffBoundsCanvasX, OffBoundsCanvasY); multiply by inputScale
        // to get physical, then add the layered-client offset.
        IntPtr avHwnd = AvaloniaOverlay.AvaloniaHwnd;
        if (avHwnd == IntPtr.Zero) return;

        // While a docked panel drag/resize is active, Avalonia has logical
        // pointer capture on the docked drag handle. If we forward our own
        // mouse moves the captured handler interprets them as deltas in the
        // off-bounds canvas region (canvas X = OffBoundsCanvasX, ~2500+),
        // which snaps the docked panel to the canvas's bottom-right corner.
        // Drop them — the docked drag pauses while the cursor is over the
        // floating window and resumes when it returns to AC's client area.
        if (AvaloniaOverlay.DockedPanelPointerCaptureActive)
            return;

        float scale = AvaloniaOverlay.InputScale > 0 ? AvaloniaOverlay.InputScale : 1f;
        int avX = (int)Math.Round(OffBoundsCanvasX * scale) + layeredClientX;
        int avY = (int)Math.Round(OffBoundsCanvasY * scale) + layeredClientY;

        bool isRadar = string.Equals(Title, "Radar", StringComparison.OrdinalIgnoreCase);
        // Always log LBUTTONDOWN/UP for radar so the snapshot diagnostics
        // aren't hidden by the _inputLogCount limit. Other messages (mostly
        // moves) still respect the limit to avoid flooding the log.
        if (isRadar && (msg == 0x0201 || msg == 0x0202))
        {
            RynthLog.Info($"FloatingPanelHost({Title}): ForwardInput msg=0x{msg:X4} layered=({layeredClientX},{layeredClientY}) -> avalonia=({avX},{avY}).");
        }
        else if (_inputLogCount < 8)
        {
            _inputLogCount++;
            RynthLog.Info($"FloatingPanelHost({Title}): ForwardInput msg=0x{msg:X4} layered=({layeredClientX},{layeredClientY}) -> avalonia=({avX},{avY}).");
        }

        IntPtr packed = new IntPtr(((avY & 0xFFFF) << 16) | (avX & 0xFFFF));
        // Track which host forwarded the LBUTTONDOWN so that
        // AvaloniaSubclassWndProc can re-map SetCapture-routed WM_MOUSEMOVE
        // back into the same off-bounds Avalonia coordinate space.
        if (msg == 0x0201 /* WM_LBUTTONDOWN */)
            PointerCapturingHost = this;
        PostMessage(avHwnd, msg, wParam, packed);
    }

    /// <summary>
    /// Walks <paramref name="root"/>'s visual subtree looking for the
    /// deepest <see cref="Button"/> whose layout rect contains
    /// <paramref name="pointInRoot"/>. Bypasses Avalonia's renderer-based
    /// InputHitTest, which fails for visuals parked outside the off-screen
    /// Window's bounds (the floating-panel parking trick at canvas X=2500).
    /// Ignores RenderTransform — popped-out panel content doesn't use any.
    /// </summary>
    private static Button? FindDeepestButtonAt(Visual root, Point pointInRoot)
    {
        Button? result = null;
        foreach (var child in root.GetVisualChildren())
        {
            if (!child.IsVisible) continue;
            var bounds = child.Bounds;
            if (!bounds.Contains(pointInRoot)) continue;

            // Recurse first — innermost child wins (Avalonia z-order:
            // later children draw on top, but for typical button layouts
            // the deepest hit is the right answer).
            Point pInChild = new Point(pointInRoot.X - bounds.X, pointInRoot.Y - bounds.Y);
            var deeper = FindDeepestButtonAt(child, pInChild);
            if (deeper != null)
                result = deeper;
            else if (child is Button btn)
                result = btn;
        }
        return result;
    }

    /// <summary>
    /// Deepest <see cref="Slider"/> whose layout rect contains
    /// <paramref name="pointInRoot"/>. Same off-bounds-safe geometry walk as
    /// <see cref="FindDeepestButtonAt"/> (the renderer hit-test is unusable
    /// for the parked Border).
    /// </summary>
    private static Slider? FindDeepestSliderAt(Visual root, Point pointInRoot)
    {
        Slider? result = null;
        foreach (var child in root.GetVisualChildren())
        {
            if (!child.IsVisible) continue;
            var bounds = child.Bounds;
            if (!bounds.Contains(pointInRoot)) continue;

            Point pInChild = new Point(pointInRoot.X - bounds.X, pointInRoot.Y - bounds.Y);
            var deeper = FindDeepestSliderAt(child, pInChild);
            if (deeper != null)
                result = deeper;
            else if (child is Slider sl)
                result = sl;
        }
        return result;
    }

    /// <summary>
    /// Set <paramref name="s"/>'s Value from a point in PanelBorder-logical
    /// space (the floating geometry frame). Linear map over the slider's
    /// layout bounds — good enough for settings sliders; the thumb's
    /// half-extent isn't compensated. Must run on the UI thread.
    /// </summary>
    private void DriveSliderFromPanelPoint(Slider s, double panelX, double panelY)
    {
        if (s.TranslatePoint(new Point(0, 0), PanelBorder) is not Point origin)
            return;
        double w = s.Bounds.Width;
        double h = s.Bounds.Height;
        double localX = panelX - origin.X;
        double localY = panelY - origin.Y;
        double frac = s.Orientation == Orientation.Vertical
            ? (h <= 0 ? 0 : 1.0 - Math.Clamp(localY / h, 0, 1))
            : (w <= 0 ? 0 : Math.Clamp(localX / w, 0, 1));
        if (s.IsDirectionReversed)
            frac = 1.0 - frac;
        double v = s.Minimum + frac * (s.Maximum - s.Minimum);
        if (s.IsSnapToTickEnabled && s.TickFrequency > 0)
            v = s.Minimum + Math.Round((v - s.Minimum) / s.TickFrequency) * s.TickFrequency;
        s.Value = Math.Clamp(v, s.Minimum, s.Maximum);
    }

    /// <summary>
    /// Deepest vertical/horizontal <see cref="Avalonia.Controls.Primitives.ScrollBar"/>
    /// whose layout rect contains <paramref name="pointInRoot"/>. Same off-bounds-safe
    /// geometry walk as <see cref="FindDeepestButtonAt"/> — the renderer hit-test is
    /// unusable for the parked Border.
    /// </summary>
    private static Avalonia.Controls.Primitives.ScrollBar? FindDeepestScrollBarAt(Visual root, Point pointInRoot)
    {
        Avalonia.Controls.Primitives.ScrollBar? result = null;
        foreach (var child in root.GetVisualChildren())
        {
            if (!child.IsVisible) continue;
            var bounds = child.Bounds;
            if (!bounds.Contains(pointInRoot)) continue;

            Point pInChild = new Point(pointInRoot.X - bounds.X, pointInRoot.Y - bounds.Y);
            var deeper = FindDeepestScrollBarAt(child, pInChild);
            if (deeper != null)
                result = deeper;
            else if (child is Avalonia.Controls.Primitives.ScrollBar sb)
                result = sb;
        }
        return result;
    }

    /// <summary>
    /// Set <paramref name="sv"/>'s vertical offset from a cursor Y in PanelBorder-logical
    /// space. Absolute map of the cursor over the viewer's height to the scrollable range
    /// (mirrors <see cref="DriveSliderFromPanelPoint"/> — the thumb extent isn't
    /// compensated, so the grab point snaps under the cursor). Must run on the UI thread.
    /// </summary>
    private void DriveScrollFromPanelPoint(ScrollViewer sv, double panelY)
    {
        double scrollable = sv.Extent.Height - sv.Viewport.Height;
        if (scrollable <= 0) return;
        if (sv.TranslatePoint(new Point(0, 0), PanelBorder) is not Point origin) return;
        double h = sv.Bounds.Height;
        if (h <= 0) return;
        double frac = Math.Clamp((panelY - origin.Y) / h, 0, 1);
        sv.Offset = new Vector(sv.Offset.X, frac * scrollable);
    }

    private void OnLayeredMoved(int screenX, int screenY)
    {
        try { _owner.OnFloatingPanelMoved(Title, screenX, screenY); }
        catch { /* persistence is best-effort */ }
    }

    private void OnLayeredResized(int pixelW, int pixelH)
    {
        // OS-driven resize (HTBOTTOMRIGHT) — convert physical back to logical
        // and resize the panel Border so layout responds. Next Tick() picks
        // up the new logical bounds and renders at the new pixel size.
        // Dispatch to the UI thread: WM_SIZE fires on the LayeredWindow's
        // message-pump thread, but Avalonia properties may only be touched
        // from the dispatcher thread. During a modal HTBOTTOMRIGHT drag this
        // fires at high frequency, so a direct cross-thread write races the
        // render Tick and crashes the process.
        float scale = AvaloniaOverlay.InputScale > 0 ? AvaloniaOverlay.InputScale : 1f;
        double logicalW = pixelW / scale;
        double logicalH = pixelH / scale;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                PanelBorder.Width = Math.Max(logicalW, PanelBorder.MinWidth);
                PanelBorder.Height = Math.Max(logicalH, PanelBorder.MinHeight);
            }
            catch { /* ignore — layout will catch up */ }
        }, DispatcherPriority.Input);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // If this host was the shared-HWND SetCapture coordinate owner, drop
        // the reference so a torn-down / redocked panel can't keep docked
        // input remapped into its (now gone) off-bounds region.
        if (PointerCapturingHost == this)
            PointerCapturingHost = null;
        _activeSlider = null;
        _activeScroll = null;

        _layered.OnInput = null;
        _layered.OnMoved = null;
        _layered.OnResized = null;
        _layered.Dispose();

        _rtt?.Dispose();
        _rtt = null;
        _pixelBuffer = null;
    }
}

