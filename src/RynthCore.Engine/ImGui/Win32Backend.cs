// ═══════════════════════════════════════════════════════════════════════════
//  RynthCore.Engine — ImGui/Win32Backend.cs
//  Subclasses the game's WndProc to forward mouse/keyboard input to ImGui.
//  Implements the equivalent of imgui_impl_win32.cpp in pure C#.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ImGuiNET;
using RynthCore.Engine.D3D9;
using RynthCore.Engine.UI;

namespace RynthCore.Engine.ImGuiBackend;

internal static unsafe class Win32Backend
{
    // ─── Win32 messages ───────────────────────────────────────────────
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_SETFOCUS = 0x0007;
    private const uint WM_KILLFOCUS = 0x0008;
    private const uint WM_ACTIVATEAPP = 0x001C;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_MBUTTONDOWN = 0x0207;
    private const uint WM_MBUTTONUP = 0x0208;
    private const uint WM_MOUSEWHEEL = 0x020A;
    private const uint WM_MOUSEHWHEEL = 0x020E;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const uint WM_SYSKEYDOWN = 0x0104;
    private const uint WM_SYSKEYUP = 0x0105;
    private const uint WM_CHAR = 0x0102;
    private const uint WM_SETCURSOR = 0x0020;
    private const uint WM_XBUTTONDOWN = 0x020B;
    private const uint WM_XBUTTONUP = 0x020C;

    private const int GWL_WNDPROC = -4;
    private const uint GA_ROOT = 2;
    private const int HTCLIENT = 1;

    private const int VK_CONTROL = 0x11;
    private const int VK_SHIFT = 0x10;
    private const int VK_MENU = 0x12;  // Alt
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_INSERT = 0x2D;

    // ─── Win32 P/Invoke ───────────────────────────────────────────────
    // On x86, SetWindowLongPtr doesn't exist — use SetWindowLong
    [DllImport("user32.dll", EntryPoint = "SetWindowLongA", SetLastError = true)]
    private static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcA")]
    private static extern IntPtr CallWindowProcA(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private readonly struct OverlayPoint
    {
        public OverlayPoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int X { get; }
        public int Y { get; }
    }

    private readonly struct AvaloniaMessagePoint
    {
        public AvaloniaMessagePoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int X { get; }
        public int Y { get; }
    }

    private readonly struct QueuedInputMessage
    {
        public readonly uint Msg;
        public readonly IntPtr WParam;
        public readonly IntPtr LParam;

        public QueuedInputMessage(uint msg, IntPtr wParam, IntPtr lParam)
        {
            Msg = msg;
            WParam = wParam;
            LParam = lParam;
        }
    }

    // ─── WndProc delegate ─────────────────────────────────────────────
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private static WndProcDelegate? _wndProcDelegate;
    private static IntPtr _originalWndProc;
    private static IntPtr _gameHwnd;
    private static bool _initialized;

    /// <summary>Custom WM_USER message for deferred chat command dispatch.</summary>
    internal const uint WM_RYNTHCORE_CHAT = 0x0400 + 0x5243; // WM_USER + "RC"

    /// <summary>The game's main window handle.</summary>
    public static IntPtr GameHwnd => _gameHwnd;

    /// <summary>
    /// Sends a message directly to the game's original WndProc, bypassing our subclass.
    /// </summary>
    public static IntPtr SendToGameWndProc(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (_originalWndProc == IntPtr.Zero || _gameHwnd == IntPtr.Zero)
            return IntPtr.Zero;
        return CallWindowProcA(_originalWndProc, _gameHwnd, msg, wParam, lParam);
    }
    private static readonly bool[] _mouseButtons = new bool[5];
    private static readonly object _inputLock = new();
    private static readonly Queue<QueuedInputMessage> _pendingInput = new();
    private static volatile bool _wantCaptureMouse;
    private static volatile bool _wantCaptureKeyboard;
    private static bool _hasMouseCapture;
    private static bool _uiCaptureEnabled;
    private static bool _hasFocus;
    private static bool _focusInitialized;
    private static bool _insertWasDown;
    private static bool _avaloniaHasMouse;     // cursor is currently over the Avalonia panel
    private static int  _lastPanelClientX;     // last game-client pos over the panel (for wheel)
    private static int  _lastPanelClientY;

    // Panel drag / resize tracked entirely in physical coords here —
    // avoids any dependency on Avalonia's off-screen coordinate system.
    private static bool _avIsDragging;
    private static bool _avIsResizing;
    private static bool _avIsButtonCapture;
    private static bool _avHasNativeCapture;
    private static int  _avPrevPhysX;
    private static int  _avPrevPhysY;
    private static double _avDragResidualX;
    private static double _avDragResidualY;
    private static string? _pendingBarButtonTitle;
    private static int _lastKnownClientWidth;
    private static int _lastKnownClientHeight;
    private static int _lastAvaloniaMessageX;
    private static int _lastAvaloniaMessageY;
    private static int _avaloniaDebugLogCount;

    // ─── Public API ───────────────────────────────────────────────────

    public static bool Init(IntPtr hWnd)
    {
        if (_initialized) return true;
        _gameHwnd = hWnd;
        _uiCaptureEnabled = true;
        _hasFocus = false;
        _focusInitialized = false;
        _wantCaptureMouse = false;
        _wantCaptureKeyboard = false;
        _insertWasDown = false;

        // Subclass the window
        _wndProcDelegate = WndProcHook;
        IntPtr hookPtr = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
        _originalWndProc = SetWindowLong32(hWnd, GWL_WNDPROC, hookPtr);

        if (_originalWndProc == IntPtr.Zero)
        {
            RynthLog.Render($"Win32Backend: SetWindowLongPtr failed (error {Marshal.GetLastWin32Error()})");
            return false;
        }

        _initialized = true;
        RynthLog.Render("Win32Backend: Initialized (WndProc subclassed).");
        RynthLog.Render("Win32Backend: UI capture ENABLED by default (Insert to release).");
        return true;
    }

    public static void Shutdown()
    {
        if (!_initialized) return;

        // Restore original WndProc
        SetWindowLong32(_gameHwnd, GWL_WNDPROC, _originalWndProc);
        lock (_inputLock)
            _pendingInput.Clear();
        Array.Clear(_mouseButtons, 0, _mouseButtons.Length);
        _wantCaptureMouse = false;
        _wantCaptureKeyboard = false;
        if (_hasMouseCapture)
        {
            ReleaseCapture();
            _hasMouseCapture = false;
        }
        _uiCaptureEnabled = false;
        _hasFocus = false;
        _focusInitialized = false;
        _insertWasDown = false;
        _avaloniaHasMouse = false;
        _avIsDragging = false;
        _avIsResizing = false;
        _avIsButtonCapture = false;
        ReleaseAvaloniaNativeCapture();
        _avDragResidualX = 0;
        _avDragResidualY = 0;
        _pendingBarButtonTitle = null;
        _lastKnownClientWidth = 0;
        _lastKnownClientHeight = 0;
        AvaloniaOverlay.HasPointerCapture  = false;
        AvaloniaOverlay.IsDragInProgress   = false;
        AvaloniaOverlay.DragCommitPending  = false;
        AvaloniaOverlay.DragOffsetX        = 0;
        AvaloniaOverlay.DragOffsetY        = 0;
        _initialized = false;
    }

    private static int _newFrameLogCount;

    public static void NewFrame()
    {
        ImGuiIOPtr io = ImGuiNET.ImGui.GetIO();

        // Update display size
        GetClientRect(_gameHwnd, out RECT rect);
        int w = rect.Right - rect.Left;
        int h = rect.Bottom - rect.Top;

        _newFrameLogCount++;

        AvaloniaOverlay.ClientPixelWidth = w;
        AvaloniaOverlay.ClientPixelHeight = h;
        if ((_lastKnownClientWidth != w || _lastKnownClientHeight != h) && w > 1 && h > 1)
        {
            _lastKnownClientWidth = w;
            _lastKnownClientHeight = h;
            AvaloniaOverlay.NotifyGameSurfaceMetricsChanged();
            AvaloniaOverlay.RequestCapture();
        }

        // If GetClientRect returns 0x0 or 1x1, the HWND might be wrong.
        // DisplaySize will be set from D3D viewport in ImGuiController instead.
        if (w > 1 && h > 1)
            io.DisplaySize = new System.Numerics.Vector2(w, h);

        FlushQueuedInput(io);

        // Always refresh the cursor position relative to the device window.
        GetCursorPos(out POINT cursorPos);
        ScreenToClient(_gameHwnd, ref cursorPos);
        io.AddMousePosEvent(cursorPos.X, cursorPos.Y);
        SyncFocusState(io);

        bool insertDown = (GetAsyncKeyState(VK_INSERT) & 0x8000) != 0;
        if (insertDown && !_insertWasDown)
        {
            _uiCaptureEnabled = !_uiCaptureEnabled;
            RynthLog.Render($"Win32Backend: ImGui capture {(_uiCaptureEnabled ? "ENABLED" : "DISABLED")} (Insert)");
        }
        _insertWasDown = insertDown;

        // Mouse button state comes from WndProc messages only (via FlushQueuedInput).
        // GetAsyncKeyState(VK_LBUTTON) is unreliable in AC's context (reports stuck True).

        bool anyMouseButtonDown =
            _mouseButtons[0] || _mouseButtons[1] || _mouseButtons[2] || _mouseButtons[3] || _mouseButtons[4];
        if (_wantCaptureMouse && anyMouseButtonDown && !_hasMouseCapture)
        {
            SetCapture(_gameHwnd);
            _hasMouseCapture = true;
        }
        else if ((!_wantCaptureMouse || !anyMouseButtonDown) && _hasMouseCapture)
        {
            ReleaseCapture();
            _hasMouseCapture = false;
        }

        // Update modifier keys
        io.AddKeyEvent(ImGuiKey.ModCtrl, (GetKeyState(VK_CONTROL) & 0x8000) != 0);
        io.AddKeyEvent(ImGuiKey.ModShift, (GetKeyState(VK_SHIFT) & 0x8000) != 0);
        io.AddKeyEvent(ImGuiKey.ModAlt, (GetKeyState(VK_MENU) & 0x8000) != 0);
        io.AddKeyEvent(ImGuiKey.ModSuper, ((GetKeyState(VK_LWIN) | GetKeyState(VK_RWIN)) & 0x8000) != 0);
    }

    // ─── WndProc Hook ─────────────────────────────────────────────────

    private static int _wndProcLogCount;

    private static IntPtr WndProcHook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            _wndProcLogCount++;

            // ── Avalonia panel hit-test & input forwarding ────────────────
            if (IsMouseMessage(msg))
            {
                bool handled = TryForwardToAvalonia(msg, wParam, lParam);
                if (handled)
                    return IntPtr.Zero;
            }
            else if (IsKeyMessage(msg) && _avaloniaHasMouse)
            {
                // Keyboard goes to Avalonia only while the cursor is over the panel.
                IntPtr avHwnd = AvaloniaOverlay.AvaloniaHwnd;
                if (avHwnd != IntPtr.Zero)
                {
                    PostMessage(avHwnd, msg, wParam, lParam);
                    AvaloniaOverlay.RequestCapture();
                    return IntPtr.Zero;
                }
            }
            // ─────────────────────────────────────────────────────────────

            if (IsMouseMessage(msg) || IsKeyMessage(msg) || IsFocusMessage(msg))
                EnqueueInput(msg, wParam, lParam);

            // If ImGui wants mouse input, eat mouse messages so the game doesn't get them
            if (_wantCaptureMouse && IsMouseMessage(msg))
                return IntPtr.Zero;

            // If ImGui wants keyboard input, eat keyboard messages
            if (_wantCaptureKeyboard && IsKeyMessage(msg))
                return IntPtr.Zero;

            // ── Background FPS unlock: lie to AC so it never idle-throttles ──
            if (msg == WM_ACTIVATEAPP && EndSceneHook.FpsLimitEnabled)
                return CallWindowProcA(_originalWndProc, hWnd, msg, (IntPtr)1, lParam);

            // Pass through to original WndProc
            return CallWindowProcA(_originalWndProc, hWnd, msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            RynthLog.Render($"Win32Backend.WndProcHook: {ex.GetType().Name}: {ex.Message}");
            return CallWindowProcA(_originalWndProc, hWnd, msg, wParam, lParam);
        }
    }

    /// <summary>
    /// If the cursor is over the Avalonia shell panel, PostMessage the mouse event to
    /// the off-screen Avalonia HWND and return true (caller should suppress game delivery).
    /// </summary>
    private static bool TryForwardToAvalonia(uint msg, IntPtr wParam, IntPtr lParam)
    {
        IntPtr avHwnd = AvaloniaOverlay.AvaloniaHwnd;
        if (avHwnd == IntPtr.Zero)
            return false;

        // ── Pointer-capture mode: panel drag or resize in progress ────────────
        if (AvaloniaOverlay.HasPointerCapture)
        {
            if (msg == WM_MOUSEMOVE)
            {
                short cx = (short)((long)lParam & 0xFFFF);
                short cy = (short)(((long)lParam >> 16) & 0xFFFF);
                OverlayPoint overlayPoint = ClientToOverlayPoint(cx, cy);
                int dx = overlayPoint.X - _avPrevPhysX;
                int dy = overlayPoint.Y - _avPrevPhysY;
                _avPrevPhysX = overlayPoint.X;
                _avPrevPhysY = overlayPoint.Y;
                _lastPanelClientX = overlayPoint.X;
                _lastPanelClientY = overlayPoint.Y;

                if (_avIsDragging)
                {
                    int logicalDx = ConvertClientDeltaToLogical(dx, ref _avDragResidualX);
                    int logicalDy = ConvertClientDeltaToLogical(dy, ref _avDragResidualY);
                    if (logicalDx != 0 || logicalDy != 0)
                        AvaloniaOverlay.MoveBarByPhys(logicalDx, logicalDy);
                    return true;
                }
                else if (_avIsResizing)
                {
                    int logicalDx = ConvertClientDeltaToLogical(dx, ref _avDragResidualX);
                    int logicalDy = ConvertClientDeltaToLogical(dy, ref _avDragResidualY);
                    if (logicalDx != 0 || logicalDy != 0)
                        AvaloniaOverlay.ResizePanelByPhys(logicalDx, logicalDy);
                }
                else if (_avIsButtonCapture)
                {
                    AvaloniaOverlay.RequestCapture();
                    return true;
                }

                AvaloniaMessagePoint messagePoint = ClientToAvaloniaMessagePoint(overlayPoint.X, overlayPoint.Y);
                _lastAvaloniaMessageX = messagePoint.X;
                _lastAvaloniaMessageY = messagePoint.Y;
                PostOverlayMouseMessage(avHwnd, msg, wParam, messagePoint.X, messagePoint.Y);
                return true;
            }

            if (msg == WM_LBUTTONUP)
            {
                short upClientX = (short)((long)lParam & 0xFFFF);
                short upClientY = (short)(((long)lParam >> 16) & 0xFFFF);
                OverlayPoint releasePoint = ClientToOverlayPoint(upClientX, upClientY);
                _lastPanelClientX = releasePoint.X;
                _lastPanelClientY = releasePoint.Y;

                if (_avIsButtonCapture &&
                         !string.IsNullOrEmpty(_pendingBarButtonTitle) &&
                         AvaloniaOverlay.TryGetBarButtonTitleAt(releasePoint.X, releasePoint.Y, out string? releasedButtonTitle) &&
                         string.Equals(_pendingBarButtonTitle, releasedButtonTitle, StringComparison.OrdinalIgnoreCase))
                {
                    AvaloniaOverlay.ActivateBarButton(releasedButtonTitle!);
                }

                AvaloniaOverlay.IsDragInProgress = false;
                _avIsDragging = false;
                _avIsResizing = false;
                _avIsButtonCapture = false;
                ReleaseAvaloniaNativeCapture();
                _pendingBarButtonTitle = null;
                AvaloniaOverlay.HasPointerCapture = false;
                AvaloniaOverlay.RequestCapture();
                return true;
            }

            if (msg == WM_MOUSEWHEEL || msg == WM_MOUSEHWHEEL)
            {
                int avScreenX = AvaloniaOverlay.AvaloniaWindowLeft + _lastAvaloniaMessageX;
                int avScreenY = AvaloniaOverlay.AvaloniaWindowTop  + _lastAvaloniaMessageY;
                PostMessage(avHwnd, msg, wParam, new IntPtr((avScreenY << 16) | (avScreenX & 0xFFFF)));
                AvaloniaOverlay.RequestCapture();
                return true;
            }

            short rawX = (short)((long)lParam & 0xFFFF);
            short rawY = (short)(((long)lParam >> 16) & 0xFFFF);
            OverlayPoint forwardedPoint = ClientToOverlayPoint(rawX, rawY);
            AvaloniaMessagePoint capturedMessagePoint = ClientToAvaloniaMessagePoint(forwardedPoint.X, forwardedPoint.Y);
            _lastAvaloniaMessageX = capturedMessagePoint.X;
            _lastAvaloniaMessageY = capturedMessagePoint.Y;
            PostOverlayMouseMessage(avHwnd, msg, wParam, capturedMessagePoint.X, capturedMessagePoint.Y);
            return true;
        }

        // ── Normal mode: hit-test position-carrying messages ──────────────────
        if (msg == WM_MOUSEMOVE || msg == WM_LBUTTONDOWN || msg == WM_LBUTTONUP
            || msg == WM_RBUTTONDOWN || msg == WM_RBUTTONUP
            || msg == WM_MBUTTONDOWN || msg == WM_MBUTTONUP
            || msg == WM_XBUTTONDOWN || msg == WM_XBUTTONUP)
        {
            short cx = (short)((long)lParam & 0xFFFF);
            short cy = (short)(((long)lParam >> 16) & 0xFFFF);
            OverlayPoint overlayPoint = ClientToOverlayPoint(cx, cy);
            bool over = AvaloniaOverlay.IsOverPanel(overlayPoint.X, overlayPoint.Y);

            if (over != _avaloniaHasMouse)
            {
                _avaloniaHasMouse = over;
            }

            if (!over)
            {
                if (msg == WM_LBUTTONUP)
                    _pendingBarButtonTitle = null;
                return false;
            }

            _lastPanelClientX = overlayPoint.X;
            _lastPanelClientY = overlayPoint.Y;

            if (AvaloniaOverlay.TryGetBarButtonTitleAt(overlayPoint.X, overlayPoint.Y, out string? barButtonTitle))
            {
                if (msg == WM_LBUTTONDOWN)
                {
                    LogAvaloniaPointerDebug("button-down", cx, cy, overlayPoint.X, overlayPoint.Y);
                    _pendingBarButtonTitle = barButtonTitle;
                    _avIsButtonCapture = true;
                    _avPrevPhysX = overlayPoint.X;
                    _avPrevPhysY = overlayPoint.Y;
                    _avDragResidualX = 0;
                    _avDragResidualY = 0;
                    _lastPanelClientX = overlayPoint.X;
                    _lastPanelClientY = overlayPoint.Y;
                    AvaloniaOverlay.HasPointerCapture = true;
                    AcquireAvaloniaNativeCapture();
                    AvaloniaOverlay.RequestCapture();
                    return true;
                }

                if (msg == WM_LBUTTONUP)
                {
                    if (!string.IsNullOrEmpty(_pendingBarButtonTitle) &&
                        string.Equals(_pendingBarButtonTitle, barButtonTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        AvaloniaOverlay.ActivateBarButton(barButtonTitle!);
                    }

                    _avIsButtonCapture = false;
                    _pendingBarButtonTitle = null;
                    return true;
                }

                if (msg == WM_MOUSEMOVE || msg == WM_RBUTTONDOWN || msg == WM_RBUTTONUP || msg == WM_MBUTTONDOWN || msg == WM_MBUTTONUP || msg == WM_XBUTTONDOWN || msg == WM_XBUTTONUP)
                    return true;
            }
            else if (msg == WM_LBUTTONUP)
            {
                _avIsButtonCapture = false;
                _pendingBarButtonTitle = null;
            }

            // Detect header drag and resize-grip on left-button press
            if (msg == WM_LBUTTONDOWN)
            {
                // PanelPhys* are in Avalonia-origin coords; add DragOffset for screen coords.
                int physLeft   = AvaloniaOverlay.PanelPhysLeft   + AvaloniaOverlay.DragOffsetX;
                int physTop    = AvaloniaOverlay.PanelPhysTop    + AvaloniaOverlay.DragOffsetY;
                int physRight  = AvaloniaOverlay.PanelPhysRight  + AvaloniaOverlay.DragOffsetX;
                int physBottom = AvaloniaOverlay.PanelPhysBottom + AvaloniaOverlay.DragOffsetY;

                bool withinBar =
                    overlayPoint.X >= physLeft &&
                    overlayPoint.X <= physRight &&
                    overlayPoint.Y >= physTop &&
                    overlayPoint.Y <= physBottom;

                // Shell resizing is disabled for now because the previous hit-test
                // region was swallowing normal control clicks across the popup area.
                bool onGrip = false;

                // Drag zone is only the left ~32px of the bar header ("RC").
                // Everything else should flow through to Avalonia controls normally.
                bool onHeader = withinBar && overlayPoint.X <= physLeft + 32;

                if (onGrip || onHeader)
                {
                    LogAvaloniaPointerDebug(onHeader ? "drag-start" : "resize-start", cx, cy, overlayPoint.X, overlayPoint.Y);
                    _pendingBarButtonTitle = null;
                    _avIsDragging = onHeader;
                    _avIsResizing = onGrip;
                    _avIsButtonCapture = false;
                    _avPrevPhysX  = overlayPoint.X;
                    _avPrevPhysY  = overlayPoint.Y;
                    _avDragResidualX = 0;
                    _avDragResidualY = 0;
                    AvaloniaOverlay.HasPointerCapture = true;
                    AcquireAvaloniaNativeCapture();

                    if (onHeader)
                    {
                        AvaloniaOverlay.IsDragInProgress = true;
                        AvaloniaOverlay.DragOffsetX = 0;
                        AvaloniaOverlay.DragOffsetY = 0;
                    }

                    // Do NOT PostMessage WM_LBUTTONDOWN to Avalonia here.
                    // Doing so causes Avalonia to call SetCapture(avHwnd) internally,
                    // which hijacks WM_LBUTTONUP away from our WndProc — HasPointerCapture
                    // then never gets cleared and release requires a second click.
                    AvaloniaOverlay.RequestCapture();
                    return true;
                }
            }

            AvaloniaMessagePoint messagePoint = ClientToAvaloniaMessagePoint(overlayPoint.X, overlayPoint.Y);
            _lastAvaloniaMessageX = messagePoint.X;
            _lastAvaloniaMessageY = messagePoint.Y;
            if (msg == WM_LBUTTONDOWN)
                LogAvaloniaPointerDebug("forward-down", cx, cy, overlayPoint.X, overlayPoint.Y, messagePoint.X, messagePoint.Y);
            PostOverlayMouseMessage(avHwnd, msg, wParam, messagePoint.X, messagePoint.Y);
            if (msg != WM_MOUSEMOVE || AvaloniaOverlay.ShouldUseCustomSkiaProducer)
                AvaloniaOverlay.RequestCapture();
            return true;
        }

        // ── Scroll: forward while cursor is over the panel ────────────────────
        if ((msg == WM_MOUSEWHEEL || msg == WM_MOUSEHWHEEL) && _avaloniaHasMouse)
        {
            int avScreenX = AvaloniaOverlay.AvaloniaWindowLeft + _lastAvaloniaMessageX;
            int avScreenY = AvaloniaOverlay.AvaloniaWindowTop  + _lastAvaloniaMessageY;
            PostMessage(avHwnd, msg, wParam, new IntPtr((avScreenY << 16) | (avScreenX & 0xFFFF)));
            AvaloniaOverlay.RequestCapture();
            return true;
        }

        return false;
    }

    private static ImGuiKey VkToImGuiKey(int vk)
    {
        return vk switch
        {
            0x09 => ImGuiKey.Tab,
            0x25 => ImGuiKey.LeftArrow,
            0x27 => ImGuiKey.RightArrow,
            0x26 => ImGuiKey.UpArrow,
            0x28 => ImGuiKey.DownArrow,
            0x21 => ImGuiKey.PageUp,
            0x22 => ImGuiKey.PageDown,
            0x24 => ImGuiKey.Home,
            0x23 => ImGuiKey.End,
            0x2D => ImGuiKey.Insert,
            0x2E => ImGuiKey.Delete,
            0x08 => ImGuiKey.Backspace,
            0x20 => ImGuiKey.Space,
            0x0D => ImGuiKey.Enter,
            0x1B => ImGuiKey.Escape,
            0x6A => ImGuiKey.KeypadMultiply,
            0x6B => ImGuiKey.KeypadAdd,
            0x6D => ImGuiKey.KeypadSubtract,
            0x6E => ImGuiKey.KeypadDecimal,
            0x6F => ImGuiKey.KeypadDivide,
            0xBE => ImGuiKey.Period,
            0xBC => ImGuiKey.Comma,
            0xBD => ImGuiKey.Minus,
            0xBB => ImGuiKey.Equal,
            0xBA => ImGuiKey.Semicolon,
            0xBF => ImGuiKey.Slash,
            0xC0 => ImGuiKey.GraveAccent,
            0xDB => ImGuiKey.LeftBracket,
            0xDC => ImGuiKey.Backslash,
            0xDD => ImGuiKey.RightBracket,
            0xDE => ImGuiKey.Apostrophe,
            0x14 => ImGuiKey.CapsLock,
            0x91 => ImGuiKey.ScrollLock,
            0x90 => ImGuiKey.NumLock,
            0x2C => ImGuiKey.PrintScreen,
            0x13 => ImGuiKey.Pause,
            >= 0x30 and <= 0x39 => ImGuiKey._0 + (vk - 0x30),       // 0-9
            >= 0x41 and <= 0x5A => ImGuiKey.A + (vk - 0x41),         // A-Z
            >= 0x60 and <= 0x69 => ImGuiKey.Keypad0 + (vk - 0x60),   // Numpad 0-9
            >= 0x70 and <= 0x7B => ImGuiKey.F1 + (vk - 0x70),        // F1-F12
            0x10 => ImGuiKey.LeftShift,
            0x11 => ImGuiKey.LeftCtrl,
            0x12 => ImGuiKey.LeftAlt,
            0x5B => ImGuiKey.LeftSuper,
            _ => ImGuiKey.None,
        };
    }

    private static bool IsMouseMessage(uint msg)
    {
        return msg >= WM_MOUSEMOVE && msg <= WM_MBUTTONUP
            || msg == WM_MOUSEWHEEL || msg == WM_MOUSEHWHEEL
            || msg == WM_XBUTTONDOWN || msg == WM_XBUTTONUP;
    }

    private static bool IsKeyMessage(uint msg)
    {
        return msg == WM_KEYDOWN || msg == WM_KEYUP
            || msg == WM_SYSKEYDOWN || msg == WM_SYSKEYUP
            || msg == WM_CHAR;
    }

    private static bool IsFocusMessage(uint msg)
    {
        return msg == WM_SETFOCUS || msg == WM_KILLFOCUS || msg == WM_ACTIVATEAPP;
    }

    private static void AddMousePosFromLParam(ImGuiIOPtr io, IntPtr lParam)
    {
        short x = (short)((long)lParam & 0xFFFF);
        short y = (short)(((long)lParam >> 16) & 0xFFFF);
        io.AddMousePosEvent(x, y);
    }

    private static void UpdateMouseButton(ImGuiIOPtr io, int buttonIndex, int virtualKey)
    {
        bool isDown = (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
        SetMouseButtonState(io, buttonIndex, isDown);
    }

    public static void UpdateCaptureFlags(bool wantMouse, bool wantKeyboard)
    {
        // ImGui.NET's WantCaptureMouse/WantCaptureKeyboard read from wrong native
        // offsets (struct layout mismatch, same class of bug as ImDrawCmd stride).
        // Use _uiCaptureEnabled directly — Insert key gives user explicit control.
        _wantCaptureMouse = _uiCaptureEnabled && _hasFocus && wantMouse;
        _wantCaptureKeyboard = _uiCaptureEnabled && _hasFocus && wantKeyboard;
    }

    public static bool IsUiCaptureEnabled()
    {
        return _uiCaptureEnabled;
    }

    private static OverlayPoint ClientToOverlayPoint(int clientX, int clientY)
    {
        if (!GetClientRect(_gameHwnd, out RECT rect))
            return new OverlayPoint(clientX, clientY);

        int clientW = Math.Max(1, rect.Right - rect.Left);
        int clientH = Math.Max(1, rect.Bottom - rect.Top);

        // Avalonia hit-testing and bar layout live in the logical game-client space.
        // Do not map through the submitted surface pixel size here: the custom Skia
        // producer may render a higher-resolution frame (for DPI / scaling), which
        // would make drag deltas and hit locations move faster than the actual mouse.
        return new OverlayPoint(
            Math.Clamp(clientX, 0, clientW),
            Math.Clamp(clientY, 0, clientH));
    }

    private static void PostOverlayMouseMessage(IntPtr hwnd, uint msg, IntPtr wParam, int overlayX, int overlayY)
    {
        PostMessage(hwnd, msg, wParam, new IntPtr(((overlayY & 0xFFFF) << 16) | (overlayX & 0xFFFF)));
    }

    private static int ConvertClientDeltaToLogical(int clientDelta, ref double residual)
    {
        float scale = AvaloniaOverlay.InputScale;
        if (scale <= 1.001f)
            return clientDelta;

        double logicalDelta = (clientDelta / (double)scale) + residual;
        int rounded = (int)Math.Truncate(logicalDelta);
        residual = logicalDelta - rounded;
        return rounded;
    }

    private static AvaloniaMessagePoint ClientToAvaloniaMessagePoint(int clientX, int clientY)
    {
        // Win32 mouse-message lParams are always in client physical pixels.
        // The off-screen Avalonia top-level is now sized in logical units that
        // match the live game surface after RenderScaling is applied, so Avalonia
        // can perform its normal physical->logical conversion internally.
        return new AvaloniaMessagePoint(clientX, clientY);
    }

    private static void LogAvaloniaPointerDebug(string phase, int clientX, int clientY, int overlayX, int overlayY, int? messageX = null, int? messageY = null)
    {
        // Suppressed — these diagnostics are no longer needed for stable operation.
    }

    private static void AcquireAvaloniaNativeCapture()
    {
        if (_avHasNativeCapture)
            return;

        SetCapture(_gameHwnd);
        _avHasNativeCapture = true;
    }

    private static void ReleaseAvaloniaNativeCapture()
    {
        if (!_avHasNativeCapture)
            return;

        ReleaseCapture();
        _avHasNativeCapture = false;
    }

    private static void EnqueueInput(uint msg, IntPtr wParam, IntPtr lParam)
    {
        lock (_inputLock)
            _pendingInput.Enqueue(new QueuedInputMessage(msg, wParam, lParam));
    }

    private static void FlushQueuedInput(ImGuiIOPtr io)
    {
        while (true)
        {
            QueuedInputMessage message;
            lock (_inputLock)
            {
                if (_pendingInput.Count == 0) break;
                message = _pendingInput.Dequeue();
            }

            switch (message.Msg)
            {
                case WM_MOUSEMOVE:
                    AddMousePosFromLParam(io, message.LParam);
                    break;

                case WM_LBUTTONDOWN:
                    AddMousePosFromLParam(io, message.LParam);
                    SetMouseButtonState(io, 0, true);
                    break;

                case WM_LBUTTONUP:
                    AddMousePosFromLParam(io, message.LParam);
                    SetMouseButtonState(io, 0, false);
                    break;

                case WM_RBUTTONDOWN:
                    AddMousePosFromLParam(io, message.LParam);
                    SetMouseButtonState(io, 1, true);
                    break;

                case WM_RBUTTONUP:
                    AddMousePosFromLParam(io, message.LParam);
                    SetMouseButtonState(io, 1, false);
                    break;

                case WM_MBUTTONDOWN:
                    AddMousePosFromLParam(io, message.LParam);
                    SetMouseButtonState(io, 2, true);
                    break;

                case WM_MBUTTONUP:
                    AddMousePosFromLParam(io, message.LParam);
                    SetMouseButtonState(io, 2, false);
                    break;

                case WM_XBUTTONDOWN:
                    AddMousePosFromLParam(io, message.LParam);
                    SetMouseButtonState(io, GetXButtonIndex(message.WParam), true);
                    break;

                case WM_XBUTTONUP:
                    AddMousePosFromLParam(io, message.LParam);
                    SetMouseButtonState(io, GetXButtonIndex(message.WParam), false);
                    break;

                case WM_MOUSEWHEEL:
                    io.AddMouseWheelEvent(0f, (short)((long)message.WParam >> 16) / 120f);
                    break;

                case WM_MOUSEHWHEEL:
                    io.AddMouseWheelEvent((short)((long)message.WParam >> 16) / 120f, 0f);
                    break;

                case WM_KEYDOWN:
                case WM_SYSKEYDOWN:
                    {
                        ImGuiKey key = VkToImGuiKey((int)(long)message.WParam);
                        if (key != ImGuiKey.None)
                            io.AddKeyEvent(key, true);
                        break;
                    }

                case WM_KEYUP:
                case WM_SYSKEYUP:
                    {
                        ImGuiKey key = VkToImGuiKey((int)(long)message.WParam);
                        if (key != ImGuiKey.None)
                            io.AddKeyEvent(key, false);
                        break;
                    }

                case WM_CHAR:
                    {
                        uint ch = (uint)(long)message.WParam;
                        if (ch > 0 && ch < 0x10000)
                            io.AddInputCharacter(ch);
                        break;
                    }

                case WM_SETFOCUS:
                    ApplyFocusState(io, true);
                    break;

                case WM_KILLFOCUS:
                    ApplyFocusState(io, false);
                    break;

                case WM_ACTIVATEAPP:
                    ApplyFocusState(io, message.WParam != IntPtr.Zero);
                    break;
            }
        }
    }

    private static void SyncFocusState(ImGuiIOPtr io)
    {
        IntPtr rootWindow = GetAncestor(_gameHwnd, GA_ROOT);
        IntPtr targetWindow = rootWindow != IntPtr.Zero ? rootWindow : _gameHwnd;
        bool isFocused = GetForegroundWindow() == targetWindow;

        if (!_focusInitialized || isFocused != _hasFocus)
            ApplyFocusState(io, isFocused);
    }

    private static void ApplyFocusState(ImGuiIOPtr io, bool isFocused)
    {
        if (_focusInitialized && _hasFocus == isFocused)
            return;

        _focusInitialized = true;
        _hasFocus = isFocused;
        io.AddFocusEvent(isFocused);

        if (isFocused)
            return;

        for (int buttonIndex = 0; buttonIndex < _mouseButtons.Length; buttonIndex++)
            SetMouseButtonState(io, buttonIndex, false);

        if (_hasMouseCapture)
        {
            ReleaseCapture();
            _hasMouseCapture = false;
        }
    }

    private static void SetMouseButtonState(ImGuiIOPtr io, int buttonIndex, bool isDown)
    {
        if (_mouseButtons[buttonIndex] == isDown) return;

        _mouseButtons[buttonIndex] = isDown;
        io.AddMouseButtonEvent(buttonIndex, isDown);
    }

    private static int GetXButtonIndex(IntPtr wParam)
    {
        int button = ((int)(long)wParam >> 16) & 0xFFFF;
        return button == 1 ? 3 : 4;
    }
}
