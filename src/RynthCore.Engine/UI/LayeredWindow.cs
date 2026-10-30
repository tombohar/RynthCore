// =============================================================================
//  RynthCore.Engine - UI/LayeredWindow.cs
//  Native Win32 layered top-level window. Used by FloatingPanelHost to display
//  popped-out panels OUTSIDE AC's client area without going through Avalonia's
//  CustomPlatformGraphics bridge.
//
//  Avalonia's bridge is wired to a single shared off-screen capture target
//  sized to AC's client area; any second Avalonia Window flashes that target.
//  By using a non-Avalonia HWND for display we sidestep the bridge entirely:
//  the panel renders into its own RenderTargetBitmap (driven explicitly from
//  the Avalonia UI thread), and we push the BGRA pixels here via
//  UpdateLayeredWindow with per-pixel alpha.
//
//  Position handling: move (top CaptionHeight) and resize (bottom-right grip)
//  are tracked NON-MODALLY in the WndProc (BeginDrag/UpdateDrag/EndDrag) — we do
//  NOT return HTCAPTION/HTBOTTOMRIGHT, because the OS modal move/size loop would
//  run on this WndProc's thread (AC's game thread) and freeze the client for the
//  whole drag. SetCapture + WM_MOUSEMOVE + SetWindowPos keeps the pump live so AC
//  keeps rendering. The DIB backing store is grown in chunks (never per-frame)
//  to avoid the realloc churn that corrupts the heap on a drag.
//
//  Input forwarding: mouse messages fire OnInput; the host translates the
//  layered-client coords into Avalonia-canvas coords (where the panel Border
//  is parented at off-bounds positions) and PostMessages to the Avalonia HWND
//  so the existing input pipeline routes hits to the right Avalonia control.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RynthCore.Engine.UI;

internal sealed unsafe class LayeredWindow : IDisposable
{
    // ─── Win32 constants ───────────────────────────────────────────────────
    // Class registrations live at the process level for the lifetime of the
    // process, while the engine DLL gets unloaded + reloaded across RL. A
    // hard-coded class name collides on the second load (RegisterClassEx
    // returns ERROR_CLASS_ALREADY_EXISTS = 1410) AND we can't reuse the old
    // class because its WndProc points into the unloaded DLL. So bake a
    // per-DLL-load Guid into the name — each engine instance gets a fresh
    // class atom backed by its own WndProc pointer.
    private const string ClassNamePrefix = "RynthCoreLayeredPanel_";
    private static readonly string ClassName = ClassNamePrefix + Guid.NewGuid().ToString("N");

    private const int  WS_POPUP         = unchecked((int)0x80000000);
    private const int  WS_EX_LAYERED    = 0x00080000;
    private const int  WS_EX_NOACTIVATE = 0x08000000;
    private const int  WS_EX_TOOLWINDOW = 0x00000080;
    private const int  WS_EX_TRANSPARENT = 0x00000020;
    private const int  GWL_EXSTYLE      = -20;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

    private const uint WM_DESTROY      = 0x0002;
    private const uint WM_MOVE         = 0x0003;
    private const uint WM_SIZE         = 0x0005;
    private const uint WM_SETFOCUS     = 0x0007;
    // Custom message handled by Win32Backend.WndProcHook (game thread) to
    // SetFocus back to the AC client window. Kept in sync with
    // Win32Backend.WM_RYNTH_RESTORE_FOCUS — value, not a shared reference,
    // because LayeredWindow lives in UI/ and avoids depending on the ImGui
    // subsystem.
    private const uint WM_RYNTH_RESTORE_FOCUS = 0x8001;
    private const uint WM_ENTERSIZEMOVE = 0x0231;
    private const uint WM_EXITSIZEMOVE  = 0x0232;
    private const uint WM_NCHITTEST    = 0x0084;
    private const uint WM_MOUSEACTIVATE = 0x0021;
    private const uint WM_MOUSEMOVE    = 0x0200;
    private const uint WM_LBUTTONDOWN  = 0x0201;
    private const uint WM_LBUTTONUP    = 0x0202;
    private const uint WM_RBUTTONDOWN  = 0x0204;
    private const uint WM_RBUTTONUP    = 0x0205;
    private const uint WM_MBUTTONDOWN  = 0x0207;
    private const uint WM_MBUTTONUP    = 0x0208;
    private const uint WM_MOUSEWHEEL   = 0x020A;
    private const uint WM_MOUSEHWHEEL  = 0x020E;
    private const uint WM_NCMOUSEMOVE  = 0x00A0;
    private const uint WM_CAPTURECHANGED = 0x0215;

    private const int HTCLIENT      = 1;
    private const int HTCAPTION     = 2;
    private const int HTBOTTOMRIGHT = 17;
    private const int HTTRANSPARENT = -1;
    private const int MA_NOACTIVATE = 3;
    private const int MA_NOACTIVATEANDEAT = 4;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    private const int VK_CONTROL = 0x11;
    private static bool IsCtrlHeld() => (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;

    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOZORDER   = 0x0004;
    private const uint SWP_NOSIZE     = 0x0001;
    private const uint SWP_NOMOVE     = 0x0002;

    private const int SW_HIDE           = 0;
    private const int SW_SHOWNOACTIVATE = 4;

    private const uint ULW_ALPHA     = 0x00000002;
    private const byte AC_SRC_OVER   = 0x00;
    private const byte AC_SRC_ALPHA  = 0x01;

    private const uint BI_RGB         = 0;
    private const uint DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE  { public int CX, CY; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int  biWidth;
        public int  biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int  biXPelsPerMeter;
        public int  biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public IntPtr lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW wndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        int dwExStyle, IntPtr lpClassName, IntPtr lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    /// <summary>UI deep-dive finding P1-B (2026-07-02): true iff this window's
    /// native HWND is currently visible (not minimized/hidden). Used by
    /// FloatingPanelHost.Tick to skip the whole per-tick raster+blit for a
    /// popout the user can't see anyway.</summary>
    public bool IsVisible => Hwnd != IntPtr.Zero && IsWindowVisible(Hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int    ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetCapture();
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool   SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool   BringWindowToTop(IntPtr hWnd);
    private const uint GW_OWNER = 4;
    [DllImport("user32.dll")] private static extern bool   ReleaseCapture();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool   AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] private static extern uint   GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool   GetCursorPos(out POINT lpPoint);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hDC, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObj);
    [DllImport("gdi32.dll")] private static extern bool   DeleteObject(IntPtr hObj);
    [DllImport("gdi32.dll")] private static extern bool   DeleteDC(IntPtr hDC);

    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandleW(IntPtr lpModuleName);

    // ─── Window class registration (once per process) ──────────────────────
    private static readonly object _classLock = new();
    private static bool _classRegistered;
    private static IntPtr _classNamePtr;

    private static void EnsureClassRegistered()
    {
        if (_classRegistered) return;
        lock (_classLock)
        {
            if (_classRegistered) return;

            // Keep the class-name buffer alive for the process lifetime —
            // RegisterClassEx stores the pointer; if we free it later, the
            // class name lookup at CreateWindow time would dereference a
            // freed buffer.
            _classNamePtr = Marshal.StringToHGlobalUni(ClassName);

            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                style = 0,
                lpfnWndProc = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&StaticWndProc,
                cbClsExtra = 0,
                cbWndExtra = 0,
                hInstance = GetModuleHandleW(IntPtr.Zero),
                hIcon = IntPtr.Zero,
                hCursor = IntPtr.Zero,
                hbrBackground = IntPtr.Zero,
                lpszMenuName = IntPtr.Zero,
                lpszClassName = _classNamePtr,
                hIconSm = IntPtr.Zero
            };

            ushort atom = RegisterClassExW(ref wc);
            if (atom == 0)
                throw new InvalidOperationException(
                    $"LayeredWindow.RegisterClassExW failed: {Marshal.GetLastWin32Error()}");

            _classRegistered = true;
        }
    }

    // ─── Static WndProc dispatch ───────────────────────────────────────────
    private static readonly object _instancesLock = new();
    private static readonly Dictionary<IntPtr, LayeredWindow> _instances = new();
    private static int _hittestDiagCount;

    /// <summary>
    /// Posted to a panel window to destroy itself. Handled here, before the
    /// instance lookup, so it runs on whichever thread created the window —
    /// DestroyWindow only works there. A window created on the overlay thread
    /// (not the game thread) could not be destroyed by a destroy posted to the
    /// game thread (ACCESS_DENIED) and stayed on screen: the double dashboard.
    /// Must not collide with Win32Backend's game-window messages (0x8001-0x8005).
    /// </summary>
    private const uint WM_RYNTH_SELF_DESTROY = 0x8051;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // Handled before the instance lookup: Dispose has already removed the
        // instance by the time this posted message is dispatched.
        if (msg == WM_RYNTH_SELF_DESTROY)
        {
            bool destroyed = DestroyWindow(hwnd);
            int err = destroyed ? 0 : Marshal.GetLastWin32Error();
            RynthCore.Engine.RynthLog.Info($"LayeredWindow: self-destroy DestroyWindow(0x{hwnd.ToInt64():X}) = {destroyed} err={err} on thread=0x{GetCurrentThreadId():X}.");
            return IntPtr.Zero;
        }

        LayeredWindow? self;
        lock (_instancesLock)
            _instances.TryGetValue(hwnd, out self);

        if (self != null)
            return self.OnMessage(msg, wParam, lParam);

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    [DllImport("user32.dll")]
    private static extern int EnumThreadWindows(uint dwThreadId, delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int> lpfn, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, char* lpClassName, int nMaxCount);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int CollectThreadWindow(IntPtr hwnd, IntPtr lParam)
    {
        if (GCHandle.FromIntPtr(lParam).Target is List<IntPtr> found)
            found.Add(hwnd);
        return 1;
    }

    /// <summary>
    /// Destroys panel windows owned by the calling thread.
    /// Must run on AC's game thread — the panels are created there (see
    /// FloatingPanelHost's RunOnGameThread) and DestroyWindow only works on
    /// the owning thread.
    ///
    /// Synchronous backstop for panels that were never disposed: because the
    /// engine module is never unloaded, such a panel lives on after shutdown,
    /// frozen on its last frame, deaf to input, and hidden under the next
    /// generation's copy of it until that one is moved ("two RynthAi windows").
    /// (Disposed panels destroy themselves via WM_RYNTH_SELF_DESTROY.)
    ///
    /// <paramref name="includeThisGeneration"/>=false spares this engine
    /// load's own windows (class names carry a per-load Guid) and removes only
    /// ones an earlier load left behind.
    /// </summary>
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int EnumWindows(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int> lpfn, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();

    /// <summary>
    /// Takes a panel window off the screen at once (never blocks, any thread)
    /// and asks its own thread to destroy it.
    /// </summary>
    internal static bool Retire(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        ShowWindowAsync(hwnd, SW_HIDE);
        return PostMessage(hwnd, WM_RYNTH_SELF_DESTROY, IntPtr.Zero, IntPtr.Zero);
    }

    // Panel title → the window currently showing it popped out.
    private static readonly Dictionary<string, IntPtr> _windowByTitle = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records <paramref name="hwnd"/> as the one popped-out window for
    /// <paramref name="title"/>; any earlier window for that title is retired.
    /// A panel can never have two popped-out windows.
    /// </summary>
    internal static void ClaimTitle(string title, IntPtr hwnd)
    {
        IntPtr prior;
        lock (_instancesLock)
        {
            _windowByTitle.TryGetValue(title, out prior);
            _windowByTitle[title] = hwnd;
        }
        if (prior != IntPtr.Zero && prior != hwnd && IsWindow(prior))
        {
            RynthCore.Engine.RynthLog.Info($"LayeredWindow: '{title}' already had window 0x{prior.ToInt64():X}; retiring it for 0x{hwnd.ToInt64():X}.");
            Retire(prior);
        }
    }

    /// <summary>
    /// The content rects (screen px: left, top, width, height) of every visible panel window.
    /// They sit above the game window, so nothing drawn in the game frame can show over them.
    /// </summary>
    internal static List<(int Left, int Top, int Width, int Height)> VisibleContentRects()
    {
        LayeredWindow[] windows;
        lock (_instancesLock)
        {
            windows = new LayeredWindow[_instances.Count];
            _instances.Values.CopyTo(windows, 0);
        }
        var rects = new List<(int, int, int, int)>(windows.Length);
        foreach (LayeredWindow w in windows)
            if (w.IsVisible && w.Width > 0 && w.Height > 0)
                rects.Add((w.ScreenLeft, w.ScreenTop, w.Width, w.Height));
        return rects;
    }

    private static readonly HashSet<IntPtr> _sweptLogged = new();

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int CollectTopWindow(IntPtr hwnd, IntPtr lParam)
    {
        if (GCHandle.FromIntPtr(lParam).Target is List<IntPtr> found)
            found.Add(hwnd);
        return 1;
    }

    /// <summary>
    /// Every visible panel window of this process that no live LayeredWindow
    /// owns (it was disposed but survived, or an earlier engine load left it)
    /// is hidden and destroyed. Live windows are always in _instances, so this
    /// can't touch an open panel. Any thread; run from the heartbeat.
    /// </summary>
    internal static void SweepOrphans()
    {
        var found = new List<IntPtr>();
        GCHandle handle = GCHandle.Alloc(found);
        try { EnumWindows(&CollectTopWindow, GCHandle.ToIntPtr(handle)); }
        finally { handle.Free(); }

        uint pid = GetCurrentProcessId();
        char* buffer = stackalloc char[128];
        foreach (IntPtr hwnd in found)
        {
            GetWindowThreadProcessId(hwnd, out uint winPid);
            if (winPid != pid || !IsWindowVisible(hwnd)) continue;
            int length = GetClassNameW(hwnd, buffer, 128);
            if (length <= 0) continue;
            if (!new string(buffer, 0, length).StartsWith(ClassNamePrefix, StringComparison.Ordinal)) continue;
            bool live;
            lock (_instancesLock) live = _instances.ContainsKey(hwnd);
            if (live) continue;
            bool first;
            lock (_instancesLock) first = _sweptLogged.Add(hwnd);
            if (first) RynthCore.Engine.RynthLog.Info($"LayeredWindow: orphan panel window 0x{hwnd.ToInt64():X} still visible; hiding and destroying it.");
            Retire(hwnd);
        }
    }

    internal static void DestroyThreadPanelWindows(bool includeThisGeneration)
    {
        var found = new List<IntPtr>();
        GCHandle handle = GCHandle.Alloc(found);
        try
        {
            EnumThreadWindows(GetCurrentThreadId(), &CollectThreadWindow, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        char* buffer = stackalloc char[128];
        foreach (IntPtr hwnd in found)
        {
            int length = GetClassNameW(hwnd, buffer, 128);
            if (length <= 0)
                continue;
            string className = new(buffer, 0, length);
            if (!className.StartsWith(ClassNamePrefix, StringComparison.Ordinal))
                continue;
            bool thisGeneration = className == ClassName;
            if (thisGeneration && !includeThisGeneration)
                continue;

            bool ok = DestroyWindow(hwnd);
            RynthCore.Engine.RynthLog.Info($"LayeredWindow: DestroyWindow(0x{hwnd.ToInt64():X}) = {ok} — {(thisGeneration ? "still open at shutdown" : "left over from an earlier engine load")}.");
        }
    }

    // ─── Instance state ────────────────────────────────────────────────────
    public IntPtr Hwnd { get; private set; }

    // The panel ("content") rect on the screen. The HWND can be larger: a popped-out
    // ImGui panel's window grows a transparent margin while a tooltip or popup reaches
    // past the panel (Present). The margins are the _inset* fields; they are always 0
    // for windows fed by UpdatePixels (the Avalonia pop-outs), where the HWND is the
    // content. Drag band, grip, moves, resizes and EnsureOnScreen all work on the content.
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int ScreenLeft { get; private set; }
    public int ScreenTop { get; private set; }

    private int _insetL, _insetT, _insetR, _insetB;

    /// <summary>The HWND's rect (the content plus its transparent margins).</summary>
    public int SurfaceLeft => ScreenLeft - _insetL;
    public int SurfaceTop => ScreenTop - _insetT;
    public int SurfaceWidth => Width + _insetL + _insetR;
    public int SurfaceHeight => Height + _insetT + _insetB;

    /// <summary>True while the window is being moved or resized by the mouse.</summary>
    public bool IsDragging => _dragMode != DragNone;

    /// <summary>
    /// True: only the content rect takes the mouse; anything drawn in the margins (a
    /// tooltip) lets clicks through to the window below, like its transparent pixels.
    /// Set false while the margins hold something clickable (a popup menu).
    /// </summary>
    public bool MarginsClickThrough { get; set; } = true;

    /// <summary>
    /// Pixel height of the top "drag" region — WM_NCHITTEST returns HTCAPTION
    /// here so the OS handles drag-to-move with no input forwarding needed.
    /// </summary>
    public int CaptionHeight { get; set; } = 24;

    /// <summary>False: no bottom-right resize corner (a window sized to its content, like the bar).</summary>
    public bool ResizeGripEnabled { get; set; } = true;

    /// <summary>
    /// Pixel width of the right portion of the caption that should NOT act
    /// as a drag handle — used to keep the popout/close buttons clickable
    /// instead of starting an OS-level window move when the user clicks them.
    /// </summary>
    public int CaptionRightInset { get; set; } = 0;

    /// <summary>
    /// Physical-pixel width of the close ("X") button at the right edge of
    /// the caption. Clicks in this region fire <see cref="OnCloseClicked"/>
    /// directly from WndProc — bypasses Avalonia's input dispatch (which
    /// drops events at canvas coords outside the off-screen Window's bounds
    /// for floating panels parked far past AC's client area).
    /// </summary>
    public int CloseButtonWidthPx { get; set; } = 0;

    /// <summary>
    /// Physical-pixel width of the redock ("↙") button immediately to the
    /// left of the close button. Clicks fire <see cref="OnRedockClicked"/>.
    /// </summary>
    public int RedockButtonWidthPx { get; set; } = 0;

    /// <summary>Fired when the user clicks within the close-button region.</summary>
    public Action? OnCloseClicked;

    /// <summary>Fired when the user clicks within the redock-button region.</summary>
    public Action? OnRedockClicked;

    /// <summary>
    /// Fired after the OS finishes resizing the window (HTBOTTOMRIGHT drag).
    /// Args: new pixel width / height. Suppressed during programmatic resizes.
    /// </summary>
    public Action<int, int>? OnResized;

    /// <summary>
    /// Fired once when the user finishes an HTBOTTOMRIGHT resize drag
    /// (WM_EXITSIZEMOVE), so the host can persist the final size. Distinct from
    /// <see cref="OnResized"/>, which fires live on every WM_SIZE for reflow.
    /// Like the deferred move notify, this is skipped for programmatic resizes
    /// (UpdatePixels' SetWindowPos) so only genuine user drags persist.
    /// </summary>
    public Action<int, int>? OnResizeEnd;

    /// <summary>
    /// Fired for forwarded mouse messages. Args: msg, wParam, lParam,
    /// layered-client-x, layered-client-y. The host translates client coords
    /// into Avalonia-canvas coords.
    /// </summary>
    public Action<uint, IntPtr, IntPtr, int, int>? OnInput;

    /// <summary>
    /// Fired when the OS finishes moving the window (HTCAPTION drag, etc.).
    /// Arg: new screen X/Y. Suppressed during programmatic Move() calls.
    /// </summary>
    public Action<int, int>? OnMoved;

    private IntPtr _memDc;
    private IntPtr _hbm;
    private IntPtr _hbmOld;
    private IntPtr _dibBits;
    // Allocated dimensions of the current DIB. Tracked separately from
    // Width/Height because those fields follow the live HWND size during a
    // modal resize, while the DIB is not reallocated until UpdatePixels
    // explicitly calls EnsureDib for the new size. Without this, the early-
    // return guard in EnsureDib would skip reallocation when Width/Height
    // already match the requested size, leaving the DIB undersized and the
    // subsequent pixel writes overrunning the buffer.
    private int _dibWidth;
    private int _dibHeight;
    private bool _disposed;
    private bool _suppressMoveCallback;
    private bool _suppressResizeCallback;
    // Latch: WM_MOVE fires on every pixel during the OS modal move loop;
    // syncing to disk on each one is unworkable. Defer the OnMoved callback
    // to WM_EXITSIZEMOVE (drag end).
    private bool _modalSizeMoveActive;
    private bool _pendingMoveNotify;
    // Same deferral for resize: WM_SIZE fires per-pixel during the modal grip
    // drag; persisting on each is unworkable, so latch and fire OnResizeEnd
    // once at WM_EXITSIZEMOVE.
    private bool _pendingResizeNotify;

    // ─── Custom (non-modal) move/resize tracking ────────────────────────────
    // We deliberately do NOT return HTCAPTION / HTBOTTOMRIGHT from WM_NCHITTEST,
    // because the OS would then run its modal move/size loop INSIDE this HWND's
    // WndProc — which lives on AC's game thread — freezing AC's render loop for
    // the entire drag (the "FPS to 0 / window lags the cursor" problem). Instead
    // we detect a caption/grip press in WM_LBUTTONDOWN and track the drag
    // ourselves via SetCapture + WM_MOUSEMOVE, so each message returns
    // immediately and AC keeps rendering between mouse moves.
    private const int DragNone   = 0;
    private const int DragMove   = 1;
    private const int DragResize = 2;
    // Live-testing finding 2026-09-02: the resize grip was much too large
    // relative to the rest of the panel chrome (close/redock buttons run
    // ~14-20px). Shrunk from 22 to 16 here and in the three visual grip
    // copies in AvaloniaOverlay.cs (BuildResizeGrip + the docked-panel and
    // popped-out-panel inline copies) so the visible glyph and the actual
    // clickable hot zone stay matched.
    private const int GripPx     = 16;   // bottom-right resize hot zone (matches WM_NCHITTEST grip below)
    private const int MinDragW   = 80;   // floor so a resize can't shrink the HWND to nothing
    private const int MinDragH   = 40;
    private int  _dragMode;
    private int  _dragAnchorX, _dragAnchorY;        // cursor screen pos at drag start
    private int  _dragStartLeft, _dragStartTop;     // window screen pos at drag start
    private int  _dragStartW, _dragStartH;          // window size at drag start
    private long _lastResizeApplyTick;              // throttle resize applies (reflow cost)

    public LayeredWindow(int initialWidth, int initialHeight, int screenLeft, int screenTop, IntPtr ownerHwnd)
    {
        EnsureClassRegistered();

        Width = Math.Max(initialWidth, 1);
        Height = Math.Max(initialHeight, 1);
        ScreenLeft = screenLeft;
        ScreenTop = screenTop;

        // Owned (not WS_EX_TOPMOST) — stays above the owner (AC client) in
        // Z-order automatically, but doesn't override other apps the user
        // brings to the foreground. Owner relationship is set via the
        // hWndParent argument of CreateWindowExW for non-WS_CHILD windows.
        // Bonus: when AC is minimized the layered window hides; when AC
        // closes, the layered window is destroyed too.
        IntPtr hwnd = CreateWindowExW(
            WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
            _classNamePtr,
            IntPtr.Zero,
            WS_POPUP,
            screenLeft, screenTop, Width, Height,
            ownerHwnd, IntPtr.Zero, GetModuleHandleW(IntPtr.Zero), IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException(
                $"LayeredWindow.CreateWindowExW failed: {Marshal.GetLastWin32Error()}");

        Hwnd = hwnd;
        lock (_instancesLock)
            _instances[hwnd] = this;

        EnsureDib(Width, Height);

        RynthCore.Engine.RynthLog.Info($"LayeredWindow: created HWND=0x{hwnd.ToInt64():X} on thread=0x{GetCurrentThreadId():X}.");
    }

    private IntPtr OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
            {
                // lParam is screen coord — convert to layered-client.
                int sx = (short)((long)lParam & 0xFFFF);
                int sy = (short)(((long)lParam >> 16) & 0xFFFF);
                int cx = sx - SurfaceLeft;
                int cy = sy - SurfaceTop;

                // Outside the content, in a margin holding only a tooltip: not ours.
                if (MarginsClickThrough && (_insetL | _insetT | _insetR | _insetB) != 0
                    && (cx < _insetL || cy < _insetT || cx >= _insetL + Width || cy >= _insetT + Height))
                    return (IntPtr)HTTRANSPARENT;

                // NOTE: we intentionally do NOT return HTBOTTOMRIGHT (resize
                // grip) or HTCAPTION (move) here. Either would make DefWindowProc
                // run the OS modal move/size loop on THIS WndProc's thread — AC's
                // game thread — freezing the client for the whole drag. The
                // caption-move and grip-resize are handled non-modally in
                // WM_LBUTTONDOWN/WM_MOUSEMOVE instead (see _dragMode). Everything
                // opaque is therefore HTCLIENT.
                //
                // Transparent pixels: let clicks fall through to the AC window
                // beneath so docked panels remain clickable when the floating
                // window's bounding box overlaps their screen position.
                // Snapshot field refs before the bounds check to avoid a race
                // with EnsureDib reallocating on the Avalonia thread.
                IntPtr bits = _dibBits;
                int dw = _dibWidth;
                int dh = _dibHeight;
                if (bits != IntPtr.Zero && cx >= 0 && cy >= 0 && cx < dw && cy < dh)
                {
                    byte alpha = ((byte*)bits)[(cy * dw + cx) * 4 + 3];
                    if (alpha == 0)
                    {
                        if (_hittestDiagCount < 30)
                        {
                            _hittestDiagCount++;
                            RynthCore.Engine.RynthLog.Info($"LayeredWindow(0x{Hwnd.ToInt64():X}): WM_NCHITTEST HTTRANSPARENT at layered=({cx},{cy}) alpha=0.");
                        }
                        return (IntPtr)HTTRANSPARENT;
                    }
                }
                return (IntPtr)HTCLIENT;
            }

            case WM_SETFOCUS:
                // Despite WS_EX_NOACTIVATE + MA_NOACTIVATE, Win32 still hands
                // keyboard focus to this HWND on click (NOACTIVATE blocks
                // activation/foreground but not always focus, especially
                // same-thread). Bounce it back to the owner (game) immediately
                // so the panel feels like an extension of AC: clicks work but
                // never steal keyboard input. PostMessage is async — the
                // current MOUSEACTIVATE → SETFOCUS → LBUTTONDOWN dispatch
                // chain still completes synchronously first (and was the cause
                // of dropped clicks earlier — fixed by the MA_NOACTIVATE
                // constant correction), then the queued reclaim runs and
                // returns focus to AC.
                {
                    IntPtr setFocusOwner = GetWindow(Hwnd, GW_OWNER);
                    if (setFocusOwner != IntPtr.Zero)
                        PostMessage(setFocusOwner, WM_RYNTH_RESTORE_FOCUS, IntPtr.Zero, IntPtr.Zero);
                }
                break;

            case WM_MOUSEACTIVATE:
                // Don't activate the layered window on click — preserves AC's
                // foreground/focus. WS_EX_NOACTIVATE makes this redundant on
                // most paths but doesn't cover every code path Win32 takes.
                //
                // The MA_NOACTIVATE constant value MUST be 3 (per WinUser.h):
                // historical code here had it defined as 2, which is actually
                // MA_ACTIVATEANDEAT — that activated the window AND discarded
                // the mouse message, which is exactly what produced the
                // "double-click required, focus stolen" behavior on undocked
                // panels (first click activates+eaten, only second click —
                // after window is already active so MOUSEACTIVATE doesn't
                // re-fire — delivers WM_LBUTTONDOWN normally).
                //
                // Side effect of returning MA_NOACTIVATE: when both AC and
                // RynthAi are buried behind another app, clicking RynthAi
                // would otherwise leave AC in the background. Forward focus
                // to the owner (AC) so the whole owner-chain comes forward.
                // Same-process activations bypass the Win32 foreground-rights
                // restriction, so SetForegroundWindow works without an
                // explicit AllowSetForegroundWindow grant.
                BringOwnerToForeground();
                return (IntPtr)MA_NOACTIVATE;

            case WM_ENTERSIZEMOVE:
                _modalSizeMoveActive = true;
                _pendingMoveNotify = false;
                _pendingResizeNotify = false;
                break;

            case WM_EXITSIZEMOVE:
                _modalSizeMoveActive = false;
                if (_pendingMoveNotify && !_suppressMoveCallback)
                {
                    _pendingMoveNotify = false;
                    OnMoved?.Invoke(ScreenLeft, ScreenTop);
                }
                if (_pendingResizeNotify && !_suppressResizeCallback)
                {
                    _pendingResizeNotify = false;
                    OnResizeEnd?.Invoke(Width, Height);
                }
                break;

            case WM_MOVE:
            {
                int x = (short)((long)lParam & 0xFFFF);
                int y = (short)(((long)lParam >> 16) & 0xFFFF);
                ScreenLeft = x + _insetL;
                ScreenTop = y + _insetT;
                // During the OS HTCAPTION modal drag loop, WM_TIMER can be
                // suppressed so Tick() / UpdateLayeredWindow may not fire
                // while the HWND moves. Repaint existing pixels at the new
                // screen position on every WM_MOVE so the layered visual
                // tracks the HWND without waiting for the next timer tick.
                // No pixel copy — just repositions the existing DIB.
                // (Not from inside Present's own UpdateLayeredWindow.)
                if (_modalSizeMoveActive && !_presenting)
                    PaintExistingPixelsAt(x, y);
                if (_suppressMoveCallback) break;
                // Defer the persist callback to drag end (WM_EXITSIZEMOVE) —
                // otherwise we file-write per pixel of cursor movement.
                if (_modalSizeMoveActive)
                    _pendingMoveNotify = true;
                else
                    OnMoved?.Invoke(ScreenLeft, ScreenTop);
                break;
            }

            case WM_SIZE:
            {
                int w = (short)((long)lParam & 0xFFFF) - _insetL - _insetR;
                int h = (short)(((long)lParam >> 16) & 0xFFFF) - _insetT - _insetB;
                if (w > 0 && h > 0)
                {
                    Width = w;
                    Height = h;
                    if (_suppressResizeCallback) break;
                    // Fire live so panel content reflows during drag.
                    // UpdatePixels' modalDeferredResize path ensures SetWindowPos
                    // is never called back into this HWND while the OS modal
                    // loop is active, so there is no reentrant WM_SIZE chain.
                    OnResized?.Invoke(w, h);
                    // Latch a one-shot persist for drag end (WM_EXITSIZEMOVE).
                    if (_modalSizeMoveActive)
                        _pendingResizeNotify = true;
                }
                break;
            }

            case WM_LBUTTONDOWN:
            {
                // Client coordinates are the HWND's; the chrome below is the content's.
                int cx = (short)((long)lParam & 0xFFFF) - _insetL;
                int cy = (short)(((long)lParam >> 16) & 0xFFFF) - _insetT;
                RynthCore.Engine.RynthLog.Info($"LayeredWindow(0x{Hwnd.ToInt64():X}): WM_LBUTTONDOWN at layered=({cx},{cy}).");

                // Native chrome-button detection: floating panels live at
                // canvas coords past the off-screen Avalonia Window's
                // bounds, so Avalonia's hit-test drops forwarded clicks.
                // The chrome buttons (close, redock) sit in known pixel
                // regions, so we resolve them at the WndProc level and fire
                // callbacks straight to the host. Side effect: only these
                // two chrome controls work — buttons inside the panel
                // CONTENT (e.g., Monsters' rules editor) still need a
                // proper input-dispatch fix to be usable while floating.
                if (cy >= 0 && cy < CaptionHeight && CloseButtonWidthPx > 0 &&
                    cx >= Width - CloseButtonWidthPx && cx < Width)
                {
                    try { OnCloseClicked?.Invoke(); } catch { }
                    return IntPtr.Zero;
                }
                if (cy >= 0 && cy < CaptionHeight && RedockButtonWidthPx > 0 &&
                    cx >= Width - CloseButtonWidthPx - RedockButtonWidthPx &&
                    cx <  Width - CloseButtonWidthPx)
                {
                    try { OnRedockClicked?.Invoke(); } catch { }
                    return IntPtr.Zero;
                }

                // Resize grip (bottom-right). Handle the resize ourselves rather
                // than via the OS modal loop — see BeginDrag / the WM_NCHITTEST
                // note. Checked before the caption so the corner wins.
                if (ResizeGripEnabled && cx >= Width - GripPx && cy >= Height - GripPx && cx < Width && cy < Height)
                {
                    BeginDrag(DragResize);
                    return IntPtr.Zero;
                }
                // Caption band → non-modal move. Exclude the right inset so the
                // chrome buttons (handled above) and radar's button strip stay
                // clickable / forwardable.
                if (cy >= 0 && cy < CaptionHeight && cx >= 0 && cx < Width - CaptionRightInset)
                {
                    BeginDrag(DragMove);
                    return IntPtr.Zero;
                }

                // Capture before forwarding so out-of-window drags continue
                // to feed us mouse messages (the OS only sends WM_MOUSEMOVE
                // to the window under the cursor unless capture is held).
                SetCapture(Hwnd);
                ForwardInput(msg, wParam, lParam);
                return IntPtr.Zero;
            }
            case WM_RBUTTONDOWN:
            case WM_MBUTTONDOWN:
                SetCapture(Hwnd);
                ForwardInput(msg, wParam, lParam);
                return IntPtr.Zero;

            case WM_LBUTTONUP:
                if (_dragMode != DragNone)
                {
                    EndDrag();
                    return IntPtr.Zero;
                }
                ReleaseCapture();
                ForwardInput(msg, wParam, lParam);
                return IntPtr.Zero;

            case WM_RBUTTONUP:
            case WM_MBUTTONUP:
                ReleaseCapture();
                ForwardInput(msg, wParam, lParam);
                return IntPtr.Zero;

            case WM_MOUSEMOVE:
                // A custom caption-move / grip-resize in progress: track it
                // ourselves and don't forward to Avalonia.
                if (_dragMode != DragNone)
                {
                    UpdateDrag();
                    return IntPtr.Zero;
                }
                if (AvaloniaOverlay.DockedPanelPointerCaptureActive)
                    SetClickThrough(true);
                ForwardInput(msg, wParam, lParam);
                return IntPtr.Zero;

            case WM_MOUSEWHEEL:
            case WM_MOUSEHWHEEL:
                if (AvaloniaOverlay.DockedPanelPointerCaptureActive)
                    SetClickThrough(true);
                ForwardInput(msg, wParam, lParam);
                return IntPtr.Zero;

            case WM_CAPTURECHANGED:
                // Mouse capture was taken from us (alt-tab, another grab, or our
                // own ReleaseCapture in EndDrag). Don't leave a drag latched.
                // lParam is the window gaining capture: this window taking it
                // again is not a loss (a content drag starts while the press
                // already holds capture, and SetCapture on the capturing window
                // still sends this message - it cancelled every content drag).
                if (lParam != Hwnd)
                    CancelDragFromCaptureLoss();
                break;

            case WM_DESTROY:
                lock (_instancesLock)
                    _instances.Remove(Hwnd);
                break;
        }

        return DefWindowProcW(Hwnd, msg, wParam, lParam);
    }

    private void ForwardInput(uint msg, IntPtr wParam, IntPtr lParam)
    {
        int cx = (short)((long)lParam & 0xFFFF);
        int cy = (short)(((long)lParam >> 16) & 0xFFFF);
        try { OnInput?.Invoke(msg, wParam, lParam, cx, cy); }
        catch { /* best-effort: don't let a forwarder error kill the WndProc */ }
    }

    /// <summary>
    /// Pulls the layered window's owner (AC's main HWND) to the foreground.
    /// Called from WM_MOUSEACTIVATE so a click on RynthAi while AC is buried
    /// behind another app brings the whole pair to the top together — owned
    /// non-topmost children follow their owner up the Z-order automatically.
    /// </summary>
    private void BringOwnerToForeground()
    {
        try
        {
            IntPtr owner = GetWindow(Hwnd, GW_OWNER);
            if (owner == IntPtr.Zero) return;

            if (GetForegroundWindow() == owner)
            {
                // Owner already foreground; just make sure we're on top of
                // any sibling owned-window (e.g., a Decal panel) that might
                // be sitting above us in AC's owned-children stack.
                BringWindowToTop(Hwnd);
                return;
            }

            SetForegroundWindow(owner);
            // BringWindowToTop after foreground transfer so we're at the top
            // of AC's owned-children, not just somewhere in the chain.
            BringWindowToTop(Hwnd);
        }
        catch
        {
            // Best-effort z-order management; never let failure kill input.
        }
    }

    private void EnsureDib(int width, int height)
    {
        // Grow-only, chunked capacity. A resize-drag changes the requested size
        // every frame; reallocating the DIB section (CreateDIBSection +
        // DeleteObject) on each one churns GDI and the heap — the same
        // realloc-churn class that corrupts the heap on the RTT side (see
        // rynthcore_overlay_lfh_pitfall). Round the backing store up to a chunk
        // and never shrink so a grow-drag reuses one DIB. UpdatePixels addresses
        // the DIB by its capacity stride (_dibWidth) and blits only the live
        // window rect, so an oversized DIB is safe; the WM_NCHITTEST alpha read
        // already uses _dibWidth as its stride.
        int needW = Math.Max(width, 1);
        int needH = Math.Max(height, 1);
        if (_hbm != IntPtr.Zero && _dibWidth >= needW && _dibHeight >= needH)
            return;

        const int Chunk = 256;
        int capW = Math.Max(_dibWidth, ((needW + Chunk - 1) / Chunk) * Chunk);
        int capH = Math.Max(_dibHeight, ((needH + Chunk - 1) / Chunk) * Chunk);

        DisposeDib();

        IntPtr screenDc = GetDC(IntPtr.Zero);
        try
        {
            _memDc = CreateCompatibleDC(screenDc);

            // biHeight is negative for a top-down DIB so row 0 = topmost row,
            // matching how RenderTargetBitmap.CopyPixels lays out the buffer.
            var bmi = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = capW,
                biHeight = -capH,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB,
                biSizeImage = (uint)(capW * capH * 4)
            };

            _hbm = CreateDIBSection(_memDc, ref bmi, DIB_RGB_COLORS, out _dibBits, IntPtr.Zero, 0);
            if (_hbm == IntPtr.Zero)
                throw new InvalidOperationException(
                    $"LayeredWindow.CreateDIBSection failed: {Marshal.GetLastWin32Error()}");

            _hbmOld = SelectObject(_memDc, _hbm);
            _dibWidth = capW;
            _dibHeight = capH;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void PaintExistingPixelsAt(int screenX, int screenY)
    {
        if (_disposed || Hwnd == IntPtr.Zero || _memDc == IntPtr.Zero || _hbm == IntPtr.Zero) return;
        var dstPt = new POINT { X = screenX, Y = screenY };
        var size   = new SIZE  { CX = SurfaceWidth,  CY = SurfaceHeight };
        var srcPt  = new POINT { X = 0,       Y = 0 };
        var blend  = new BLENDFUNCTION
        {
            BlendOp = AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = AC_SRC_ALPHA
        };
        UpdateLayeredWindow(Hwnd, IntPtr.Zero, ref dstPt, ref size, _memDc, ref srcPt, 0, ref blend, ULW_ALPHA);
    }

    // ─── Custom non-modal move/resize (see the _dragMode fields) ─────────────

    /// <summary>
    /// Starts moving the window from a press inside the content (a chromeless
    /// ImGui panel dragged by its own surface). The content saw the button go
    /// down, so it is sent a button-up at the end of the move.
    /// </summary>
    public void BeginContentMove()
    {
        if (_disposed || Hwnd == IntPtr.Zero || _dragMode != DragNone) return;
        _dragFromContent = true;
        BeginDrag(DragMove);
    }

    private bool _dragFromContent;

    private void ReleaseContentButton()
    {
        if (!_dragFromContent) return;
        _dragFromContent = false;
        if (!GetCursorPos(out POINT p)) return;
        int cx = p.X - SurfaceLeft, cy = p.Y - SurfaceTop;   // HWND client coordinates, like any mouse message
        ForwardInput(WM_LBUTTONUP, IntPtr.Zero, (IntPtr)((cy << 16) | (cx & 0xFFFF)));
    }

    private void BeginDrag(int mode)
    {
        if (!GetCursorPos(out POINT p)) return;
        _dragMode      = mode;
        _dragAnchorX   = p.X;
        _dragAnchorY   = p.Y;
        _dragStartLeft = ScreenLeft;
        _dragStartTop  = ScreenTop;
        _dragStartW    = Width;
        _dragStartH    = Height;
        // Reuse the deferred-paint machinery (WM_MOVE PaintExistingPixelsAt +
        // UpdatePixels' deferred-resize path) for the duration of the drag so
        // the visual tracks the HWND while the panel reflow catches up.
        _modalSizeMoveActive = true;
        _pendingMoveNotify   = false;
        _pendingResizeNotify = false;
        _lastResizeApplyTick = 0;
        // A content move starts while the press that began it already holds capture
        // (WM_LBUTTONDOWN took it). Taking it again sent WM_CAPTURECHANGED, which
        // ended the drag as soon as it began: popped-out faces that drag by their
        // own surface (the RynthAi dashboard, the bar) could not be moved.
        if (GetCapture() != Hwnd)
            SetCapture(Hwnd);
    }

    private void UpdateDrag()
    {
        if (!GetCursorPos(out POINT p)) return;
        int dx = p.X - _dragAnchorX;
        int dy = p.Y - _dragAnchorY;
        if (_dragMode == DragMove)
        {
            // Move() repositions the HWND; WM_MOVE repaints the layered surface
            // at the new spot. No reflow, so apply every move for smooth tracking.
            Move(_dragStartLeft + dx, _dragStartTop + dy);
        }
        else if (_dragMode == DragResize)
        {
            // Each resize triggers a panel reflow + render, so throttle the
            // applies to ~60 Hz; EndDrag applies the final size unconditionally.
            long now = Environment.TickCount64;
            if (now - _lastResizeApplyTick < 15) return;
            _lastResizeApplyTick = now;
            ResizeTo(_dragStartW + dx, _dragStartH + dy);
        }
    }

    private void EndDrag()
    {
        int mode = _dragMode;
        // Clear _dragMode BEFORE ReleaseCapture: ReleaseCapture synchronously
        // posts WM_CAPTURECHANGED to this WndProc, and that handler also ends the
        // drag — clearing first makes it a no-op so we don't fire the callbacks
        // twice.
        _dragMode = DragNone;

        // Apply the final size — the last WM_MOUSEMOVE may have been throttled.
        if (mode == DragResize && GetCursorPos(out POINT p))
            ResizeTo(_dragStartW + (p.X - _dragAnchorX), _dragStartH + (p.Y - _dragAnchorY));

        _modalSizeMoveActive = false;
        _pendingMoveNotify   = false;
        _pendingResizeNotify = false;
        ReleaseCapture();
        ReleaseContentButton();

        // Persist the final geometry (mirrors what WM_EXITSIZEMOVE used to do
        // for the OS modal loop). A move never leaves the window off the screen.
        if (mode == DragMove)
        {
            EnsureOnScreen();
            OnMoved?.Invoke(ScreenLeft, ScreenTop);
        }
        else if (mode == DragResize)
            OnResizeEnd?.Invoke(Width, Height);
    }

    /// <summary>
    /// Capture lost mid-drag (e.g. the user alt-tabbed, or another window grabbed
    /// capture). Abandon the drag at its current geometry rather than leaving
    /// _dragMode latched so the next stray WM_MOUSEMOVE doesn't resize/move the
    /// window. Persists the current size/pos. No-op if EndDrag already cleared
    /// the drag (its own ReleaseCapture routes here).
    /// </summary>
    private void CancelDragFromCaptureLoss()
    {
        if (_dragMode == DragNone) return;
        int mode = _dragMode;
        _dragMode = DragNone;
        _modalSizeMoveActive = false;
        _pendingMoveNotify   = false;
        _pendingResizeNotify = false;
        ReleaseContentButton();
        if (mode == DragMove)
        {
            EnsureOnScreen();
            OnMoved?.Invoke(ScreenLeft, ScreenTop);
        }
        else if (mode == DragResize)
            OnResizeEnd?.Invoke(Width, Height);
    }

    /// <summary>
    /// Pulls a (left, top, width, height) rectangle inside the work area of the
    /// monitor nearest to it: wholly inside when it fits, else its top-left corner.
    /// True when it had to move. Any thread.
    /// </summary>
    public static bool ClampToWorkArea(ref int left, ref int top, int width, int height)
    {
        var r = new RECT { Left = left, Top = top, Right = left + Math.Max(1, width), Bottom = top + Math.Max(1, height) };
        IntPtr monitor = MonitorFromRect(ref r, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfoW(monitor, ref info)) return false;
        RECT work = info.rcWork;
        int x = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - width));
        int y = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - height));
        if (x == left && y == top) return false;
        left = x;
        top = y;
        return true;
    }

    /// <summary>The work area of the monitor nearest a rectangle (screen px). Any thread.</summary>
    public static bool TryGetWorkArea(int left, int top, int width, int height, out int workLeft, out int workTop, out int workRight, out int workBottom)
    {
        workLeft = workTop = workRight = workBottom = 0;
        var r = new RECT { Left = left, Top = top, Right = left + Math.Max(1, width), Bottom = top + Math.Max(1, height) };
        IntPtr monitor = MonitorFromRect(ref r, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfoW(monitor, ref info)) return false;
        workLeft = info.rcWork.Left;
        workTop = info.rcWork.Top;
        workRight = info.rcWork.Right;
        workBottom = info.rcWork.Bottom;
        return workRight > workLeft && workBottom > workTop;
    }

    /// <summary>
    /// Moves the window back inside the nearest monitor's work area when any of
    /// it is off the screen. No OnMoved (the caller persists). True when it moved.
    /// The window's thread.
    /// </summary>
    public bool EnsureOnScreen()
    {
        if (_disposed || Hwnd == IntPtr.Zero || _dragMode != DragNone) return false;   // a drag ends with its own check
        int x = ScreenLeft, y = ScreenTop;
        if (!ClampToWorkArea(ref x, ref y, Width, Height)) return false;
        Move(x, y);
        return true;
    }

    /// <summary>
    /// Resize the HWND in place without the OS modal loop. SetWindowPos sends
    /// WM_SIZE synchronously to our WndProc, which updates Width/Height and fires
    /// OnResized so the panel reflows; UpdatePixels' deferred-resize path paints
    /// the growing window each Tick. Clamped to a sane floor.
    /// </summary>
    private void ResizeTo(int newW, int newH)
    {
        if (_disposed || Hwnd == IntPtr.Zero) return;
        if (newW < MinDragW) newW = MinDragW;
        if (newH < MinDragH) newH = MinDragH;
        if (newW == Width && newH == Height) return;
        SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, newW + _insetL + _insetR, newH + _insetT + _insetB,
            SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private void DisposeDib()
    {
        if (_memDc != IntPtr.Zero)
        {
            if (_hbmOld != IntPtr.Zero)
                SelectObject(_memDc, _hbmOld);
            DeleteDC(_memDc);
        }
        if (_hbm != IntPtr.Zero)
            DeleteObject(_hbm);

        _memDc = IntPtr.Zero;
        _hbmOld = IntPtr.Zero;
        _hbm = IntPtr.Zero;
        _dibBits = IntPtr.Zero;
        _dibWidth = 0;
        _dibHeight = 0;
    }

    /// <summary>
    /// Push BGRA pixels (premultiplied alpha — same format as
    /// RenderTargetBitmap.CopyPixels output) into the layered window.
    /// Resizes both the DIB and the Win32 window if dataWidth/Height differs
    /// from the current backing store. Safe to call from the same thread the
    /// window was created on.
    /// </summary>
    private int _updateLogCount;

    public void UpdatePixels(IntPtr bgraData, int dataWidth, int dataHeight, int rowPitch)
    {
        if (_disposed || Hwnd == IntPtr.Zero) return;
        if (dataWidth <= 0 || dataHeight <= 0 || bgraData == IntPtr.Zero) return;

        // While the user is dragging the HTBOTTOMRIGHT grip, the OS owns the
        // HWND size and Avalonia hasn't relayouted yet (OnResized is deferred
        // to WM_EXITSIZEMOVE). The data we have describes the panel at its
        // PRE-resize size; we paint it into the top-left of a HWND-sized DIB
        // with transparent edges so the new strip on the right/bottom doesn't
        // show stale pixels from a previous UpdateLayeredWindow call.
        bool modalDeferredResize = _modalSizeMoveActive
                                   && (dataWidth != Width || dataHeight != Height);

        if (modalDeferredResize)
        {
            // DIB sized to the live HWND; pixels beyond data extent stay zero.
            EnsureDib(Width, Height);
        }
        else if (dataWidth != Width || dataHeight != Height)
        {
            EnsureDib(dataWidth, dataHeight);
            Width = dataWidth;
            Height = dataHeight;
            _suppressMoveCallback = true;
            _suppressResizeCallback = true;
            try
            {
                SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, Width, Height,
                    SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
            }
            finally
            {
                _suppressMoveCallback = false;
                _suppressResizeCallback = false;
            }
        }
        else
        {
            EnsureDib(dataWidth, dataHeight);
        }

        if (_dibBits == IntPtr.Zero) return;

        // On-screen rect to display this frame (the live window size). The DIB
        // is allocated to a chunked, grow-only CAPACITY that may be larger, so
        // address rows by the DIB's real stride (_dibWidth) and blit only this
        // top-left blitW×blitH region.
        int blitW = modalDeferredResize ? Width : dataWidth;
        int blitH = modalDeferredResize ? Height : dataHeight;
        if (blitW <= 0 || blitH <= 0) return;
        int dibStride = _dibWidth * 4;

        if (modalDeferredResize)
        {
            // Wipe the visible region to transparent so the right/bottom strips
            // beyond the panel data don't show stale pixels from before the drag.
            for (int y = 0; y < blitH; y++)
                new Span<byte>((byte*)_dibBits + (long)y * dibStride, blitW * 4).Clear();
        }

        int copyRows = Math.Min(dataHeight, blitH);
        int copyBytesPerRow = Math.Min(dataWidth * 4, blitW * 4);
        for (int y = 0; y < copyRows; y++)
        {
            byte* src = (byte*)bgraData + (long)y * rowPitch;
            byte* dst = (byte*)_dibBits + (long)y * dibStride;
            Buffer.MemoryCopy(src, dst, dibStride, copyBytesPerRow);
        }

        // Sample a handful of pixels of the visible rect so we can tell whether
        // RenderTargetBitmap actually produced visible content (non-zero alpha).
        // A common failure mode: render into the RTT but the Border's canvas
        // offset shifts everything off and every pixel ends up alpha=0.
        int nonZeroAlpha = 0;
        int sampleCount = Math.Min(64, blitW * blitH);
        for (int i = 0; i < sampleCount; i++)
        {
            int px = (i * 9973) % blitW;
            int py = ((i * 9973) / blitW) % blitH;
            byte alpha = ((byte*)_dibBits)[((long)py * _dibWidth + px) * 4 + 3];
            if (alpha != 0) nonZeroAlpha++;
        }

        var dstPt = new POINT { X = ScreenLeft, Y = ScreenTop };
        var size = new SIZE { CX = blitW, CY = blitH };
        var srcPt = new POINT { X = 0, Y = 0 };
        var blend = new BLENDFUNCTION
        {
            BlendOp = AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = AC_SRC_ALPHA
        };

        bool ok = UpdateLayeredWindow(Hwnd, IntPtr.Zero, ref dstPt, ref size, _memDc, ref srcPt, 0, ref blend, ULW_ALPHA);
        if (_updateLogCount < 3)
        {
            _updateLogCount++;
            int err = ok ? 0 : Marshal.GetLastWin32Error();
            RynthLog.Info($"LayeredWindow(0x{Hwnd.ToInt64():X}): UpdateLayeredWindow #{_updateLogCount} {dataWidth}x{dataHeight} at ({dstPt.X},{dstPt.Y}) ok={ok} err={err} nonZeroAlpha={nonZeroAlpha}/{sampleCount}.");
        }
    }

    private bool _presenting;

    /// <summary>
    /// Shows a picture of <paramref name="surfaceWidth"/> x <paramref name="surfaceHeight"/>
    /// premultiplied BGRA pixels in which the content (the panel) is the
    /// <paramref name="contentWidth"/> x <paramref name="contentHeight"/> rect at
    /// (<paramref name="insetLeft"/>, <paramref name="insetTop"/>); the rest is margin
    /// (transparent, or a tooltip or popup reaching past the panel). The content stays
    /// where it is on the screen: the HWND takes the picture's size and moves by the
    /// margin, all in one UpdateLayeredWindow call, so nothing flickers or jumps.
    /// The popped-out ImGui panels use this; UpdatePixels (no margins) is the Avalonia
    /// pop-outs'. The window's thread.
    /// </summary>
    public void Present(IntPtr bgraData, int rowPitch, int surfaceWidth, int surfaceHeight,
        int insetLeft, int insetTop, int contentWidth, int contentHeight)
    {
        if (_disposed || Hwnd == IntPtr.Zero || bgraData == IntPtr.Zero) return;
        if (surfaceWidth <= 0 || surfaceHeight <= 0 || contentWidth <= 0 || contentHeight <= 0) return;
        int insetRight = surfaceWidth - insetLeft - contentWidth;
        int insetBottom = surfaceHeight - insetTop - contentHeight;
        if (insetLeft < 0 || insetTop < 0 || insetRight < 0 || insetBottom < 0) return;
        // A resize drag outran this picture (it was rendered at the size before the last
        // step): keep the last one up; the next frame is drawn at the new size.
        if (_dragMode == DragResize && (contentWidth != Width || contentHeight != Height)) return;

        EnsureDib(surfaceWidth, surfaceHeight);
        if (_dibBits == IntPtr.Zero) return;
        int dibStride = _dibWidth * 4;
        int rowBytes = surfaceWidth * 4;
        for (int y = 0; y < surfaceHeight; y++)
            Buffer.MemoryCopy((byte*)bgraData + (long)y * rowPitch, (byte*)_dibBits + (long)y * dibStride, dibStride, rowBytes);

        // The picture's geometry becomes the window's. Set first: the WM_MOVE / WM_SIZE
        // the update sends read the content back out of the HWND rect with these.
        _insetL = insetLeft; _insetT = insetTop; _insetR = insetRight; _insetB = insetBottom;
        Width = contentWidth;
        Height = contentHeight;

        var dstPt = new POINT { X = ScreenLeft - insetLeft, Y = ScreenTop - insetTop };
        var size = new SIZE { CX = surfaceWidth, CY = surfaceHeight };
        var srcPt = new POINT { X = 0, Y = 0 };
        var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
        _suppressMoveCallback = true;
        _suppressResizeCallback = true;
        _presenting = true;
        bool ok;
        try
        {
            ok = UpdateLayeredWindow(Hwnd, IntPtr.Zero, ref dstPt, ref size, _memDc, ref srcPt, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            _presenting = false;
            _suppressMoveCallback = false;
            _suppressResizeCallback = false;
        }
        if (!ok && _updateLogCount < 6)
        {
            _updateLogCount++;
            RynthLog.Info($"LayeredWindow(0x{Hwnd.ToInt64():X}): Present {surfaceWidth}x{surfaceHeight} at ({dstPt.X},{dstPt.Y}) failed, err={Marshal.GetLastWin32Error()}.");
        }
    }

    private const int GWL_HWNDPARENT = -8;

    /// <summary>
    /// Re-parent this layered window to a new owner HWND. Used to apply an
    /// AC owner relationship after-the-fact when the layered window was
    /// created before ImGui had captured AC's HWND (popout-before-login
    /// case). Implemented via SetWindowLongW(GWL_HWNDPARENT) — Microsoft
    /// recommends destroy+recreate but the SetWindowLong path works in
    /// practice and doesn't require us to retain DIB/pixel state.
    /// </summary>
    public void SetOwner(IntPtr ownerHwnd)
    {
        if (_disposed || Hwnd == IntPtr.Zero || ownerHwnd == IntPtr.Zero) return;
        SetWindowLong32(Hwnd, GWL_HWNDPARENT, ownerHwnd.ToInt32());
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    public void Move(int screenLeft, int screenTop)
    {
        if (_disposed || Hwnd == IntPtr.Zero) return;
        ScreenLeft = screenLeft;
        ScreenTop = screenTop;
        _suppressMoveCallback = true;
        try
        {
            SetWindowPos(Hwnd, IntPtr.Zero, screenLeft - _insetL, screenTop - _insetT, 0, 0,
                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }
        finally { _suppressMoveCallback = false; }
    }

    public void Show()
    {
        if (_disposed || Hwnd == IntPtr.Zero) return;
        ShowWindow(Hwnd, SW_SHOWNOACTIVATE);
    }

    public void Hide()
    {
        if (_disposed || Hwnd == IntPtr.Zero) return;
        ShowWindow(Hwnd, SW_HIDE);
    }

    /// <summary>
    /// Toggle WS_EX_TRANSPARENT so mouse input either falls through to the
    /// window beneath (true) or is delivered to this window (false). The
    /// style update is no-op if it would not change — safe to call every Tick.
    /// </summary>
    private bool _clickThroughActive;
    public void SetClickThrough(bool enabled)
    {
        if (Hwnd == IntPtr.Zero || _disposed) return;
        if (_clickThroughActive == enabled) return;
        int ex = GetWindowLongW(Hwnd, GWL_EXSTYLE);
        int next = enabled ? (ex | WS_EX_TRANSPARENT) : (ex & ~WS_EX_TRANSPARENT);
        if (next != ex) SetWindowLongW(Hwnd, GWL_EXSTYLE, next);
        _clickThroughActive = enabled;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        IntPtr hwnd = Hwnd;
        Hwnd = IntPtr.Zero;

        if (hwnd != IntPtr.Zero)
        {
            lock (_instancesLock)
            {
                _instances.Remove(hwnd);
                string? owned = null;
                foreach (var kv in _windowByTitle) if (kv.Value == hwnd) { owned = kv.Key; break; }
                if (owned != null) _windowByTitle.Remove(owned);
            }

            // Off the screen now, and ask the window's own thread to destroy it
            // (works for a window created on any thread, never blocks, and still
            // works after Win32Backend has unhooked the game WndProc: the post
            // goes to the panel, not to the game window).
            if (Retire(hwnd)) { DisposeDib(); return; }

            // The post failed: the window is already gone, or its queue is full.
            // DestroyWindow only works on the owner thread, so destroy here only
            // when this thread owns it. The old backup posted the destroy to the
            // game thread, which fails (ACCESS_DENIED) for a panel created on the
            // Avalonia UI thread.
            DestroyIfOwnedHere(hwnd);
        }

        DisposeDib();
    }

    private static void DestroyIfOwnedHere(IntPtr hwnd)
    {
        try
        {
            uint owner = GetWindowThreadProcessId(hwnd, out _);
            uint current = GetCurrentThreadId();
            bool ourClass = false;
            if (owner != 0)
            {
                char* buffer = stackalloc char[128];
                int length = GetClassNameW(hwnd, buffer, 128);
                ourClass = length > 0 && new string(buffer, 0, length).StartsWith(ClassNamePrefix, StringComparison.Ordinal);
            }

            switch (PanelDestroyPlan.Choose(owner, current, ourClass))
            {
                case PanelDestroyFallback.DestroyHere:
                    bool destroyed = DestroyWindow(hwnd);
                    RynthCore.Engine.RynthLog.Info($"LayeredWindow.Dispose: self-destroy post failed; DestroyWindow(0x{hwnd.ToInt64():X}) on its owner thread 0x{current:X} = {destroyed}.");
                    break;
                case PanelDestroyFallback.LeaveHidden:
                    ShowWindowAsync(hwnd, SW_HIDE);
                    RynthCore.Engine.RynthLog.Info($"LayeredWindow.Dispose: self-destroy post to 0x{hwnd.ToInt64():X} failed; owner thread 0x{owner:X} is not this one (0x{current:X}), so it is left hidden (destroyed with its thread or by the shutdown sweep).");
                    break;
                default:
                    break; // already gone, nothing to destroy
            }
        }
        catch (Exception ex)
        {
            RynthCore.Engine.RynthLog.Info($"LayeredWindow.Dispose: destroy fallback threw {ex.GetType().Name}: {ex.Message}.");
        }
    }

    /// <summary>
    /// Destroys <paramref name="hwnd"/> on the thread that actually owns it, never
    /// blocking the caller. Inline when the caller is the owner; otherwise posts
    /// WM_RYNTH_SELF_DESTROY to the window itself so its own WndProc destroys it.
    /// Posting to the panel (not the game window) works for panels created on
    /// any thread, and still works after Win32Backend has unhooked the game
    /// WndProc at shutdown — a post to the game window would be dropped by AC.
    /// </summary>
    private static void DestroyOnOwnerThread(IntPtr hwnd)
    {
        uint ownerThread = GetWindowThreadProcessId(hwnd, out _);
        uint currentThread = GetCurrentThreadId();

        if (ownerThread == 0)
        {
            // Window already gone (e.g. its owning thread exited, which destroys it).
            RynthCore.Engine.RynthLog.Info($"LayeredWindow.Dispose: HWND=0x{hwnd.ToInt64():X} no longer exists — nothing to destroy.");
            return;
        }

        if (ownerThread == currentThread)
        {
            bool destroyed = DestroyWindow(hwnd);
            int err = destroyed ? 0 : Marshal.GetLastWin32Error();
            RynthCore.Engine.RynthLog.Info($"LayeredWindow.Dispose: DestroyWindow(0x{hwnd.ToInt64():X}) inline on owner thread=0x{currentThread:X} = {destroyed} err={err}.");
            return;
        }

        if (PostMessage(hwnd, WM_RYNTH_SELF_DESTROY, IntPtr.Zero, IntPtr.Zero))
        {
            RynthCore.Engine.RynthLog.Info($"LayeredWindow.Dispose: posted self-destroy to HWND=0x{hwnd.ToInt64():X} (owner thread=0x{ownerThread:X}, caller thread=0x{currentThread:X}).");
            return;
        }

        // Post failed (queue full or HWND destroyed in the meantime); a cross-thread
        // DestroyWindow would only fail, so report it rather than attempt it.
        RynthCore.Engine.RynthLog.Error($"LayeredWindow.Dispose: PostMessage(self-destroy) to HWND=0x{hwnd.ToInt64():X} failed err={Marshal.GetLastWin32Error()} (owner thread=0x{ownerThread:X}).");
    }
}
