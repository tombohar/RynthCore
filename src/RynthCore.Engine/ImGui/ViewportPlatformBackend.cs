// ═══════════════════════════════════════════════════════════════════════════
//  RynthCore.Engine — ImGui/ViewportPlatformBackend.cs
//  Win32 platform backend for Dear ImGui multi-viewport (equivalent of
//  imgui_impl_win32's viewport-capable portion). Installs Platform_* callbacks
//  into the native ImGuiPlatformIO struct, registers a window class for
//  popped-out ImGui windows, and proxies the required Win32 calls.
//
//  Input for viewport HWNDs is forwarded into the existing Dear ImGui IO
//  event queue via a dedicated WndProc.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;

namespace RynthCore.Engine.ImGuiBackend;

internal static unsafe class ViewportPlatformBackend
{
    // ── Delegate types (cdecl; ImGui callbacks on x86 use the default C ABI) ──
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ViewportVoidDelegate(ImGuiViewport* vp);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ViewportGetVec2Delegate(ImGuiViewport* vp, Vector2* outVec);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ViewportSetVec2Delegate(ImGuiViewport* vp, Vector2 value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte ViewportGetBoolDelegate(ImGuiViewport* vp);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ViewportSetStringDelegate(ImGuiViewport* vp, IntPtr str);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ViewportSetFloatDelegate(ImGuiViewport* vp, float value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int X, int Y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTextW(IntPtr hWnd, string lpString);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool AdjustWindowRectEx(ref RECT lpRect, uint dwStyle, bool bMenu, uint dwExStyle);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const uint MONITORINFOF_PRIMARY = 0x00000001;

    // ImGuiPlatformIO Monitors ImVector (native cimgui.dll layout).
    // Native has 18 Platform_* + 5 Renderer_* = 23 callback pointers = 92 bytes,
    // then Monitors ImVector{ int Size; int Capacity; void* Data; } at +92..+103,
    // then Viewports ImVector at +104..+115. (ImGui.NET's compiled layout adds
    // GetWindowWorkAreaInsets and so puts Monitors at +96 — do NOT use that.)
    // Confirmed by probe dump showing Viewports.Size=1, Cap=8, Data=ptr at
    // +104..+115 when native had just seeded the main viewport.
    private const int MonitorsSize     = 92;
    private const int MonitorsCapacity = 96;
    private const int MonitorsData     = 100;

    // ImGuiPlatformMonitor is 40 bytes (confirmed by cimgui.dll ctor "push 28h"):
    //   +0  MainPos   (ImVec2, 8)
    //   +8  MainSize  (ImVec2, 8)
    //   +16 WorkPos   (ImVec2, 8)
    //   +24 WorkSize  (ImVec2, 8)
    //   +32 DpiScale  (float, 4)
    //   +36 PlatformHandle (void*, 4)
    private const int MonitorStructSize = 40;

    private static IntPtr _monitorArrayPtr;
    private static int _monitorCount;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

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
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    // Win32 constants
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const int SW_SHOWNA = 8;
    private const int SW_HIDE = 0;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const uint WS_CLIPCHILDREN = 0x02000000;
    private const uint WS_CLIPSIBLINGS = 0x04000000;
    private const uint WS_EX_TOOLWINDOW  = 0x00000080;
    private const uint WS_EX_LAYERED     = 0x00080000;
    private const uint WS_EX_APPWINDOW   = 0x00040000;
    private const uint WS_EX_TOPMOST     = 0x00000008;
    private const uint WS_EX_NOACTIVATE  = 0x08000000;
    private const uint LWA_ALPHA = 0x00000002;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_MOVE = 0x0003;
    private const uint WM_SIZE = 0x0005;
    private const uint WM_MOUSEACTIVATE = 0x0021;
    private const uint WM_NCHITTEST = 0x0084;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP   = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_RBUTTONUP   = 0x0205;
    private const uint WM_RBUTTONDBLCLK = 0x0206;
    private const uint WM_MBUTTONDOWN = 0x0207;
    private const uint WM_MBUTTONUP   = 0x0208;
    private const uint WM_MBUTTONDBLCLK = 0x0209;
    private const uint WM_MOUSEWHEEL  = 0x020A;
    private const uint WM_XBUTTONDOWN = 0x020B;
    private const uint WM_XBUTTONUP   = 0x020C;
    private const uint WM_MOUSEHWHEEL = 0x020E;
    private const uint WM_KEYDOWN     = 0x0100;
    private const uint WM_KEYUP       = 0x0101;
    private const uint WM_CHAR        = 0x0102;
    private const uint WM_SYSKEYDOWN  = 0x0104;
    private const uint WM_SYSKEYUP    = 0x0105;
    private const int MA_NOACTIVATE = 3;
    private const int HTCLIENT = 1;
    private const IntPtr IDC_ARROW = 0x7F00;  // MAKEINTRESOURCE(32512)

    private const string WindowClassName = "RynthCoreImGuiViewport";

    // Pinned delegate instances — prevent GC while ImGui holds function pointers.
    private static readonly List<Delegate> _delegateRoots = new();
    private static WndProcDelegate? _viewportWndProc;
    private static IntPtr _viewportWndProcPtr;
    private static bool _initialized;
    private static bool _classRegistered;
    private static IntPtr _hInstance;
    private static IntPtr _mainHwnd;

    // Per-viewport state kept on the managed side.
    // Keyed by the native ImGuiViewport* pointer.
    private static readonly Dictionary<IntPtr, ViewportState> _viewports = new();

    private class ViewportState
    {
        public IntPtr Hwnd;
        public bool HwndOwned;
        public uint DwStyle;
        public uint DwExStyle;
    }

    public static bool Init(IntPtr mainHwnd)
    {
        if (_initialized) return true;

        _mainHwnd = mainHwnd;
        _hInstance = GetModuleHandleW(null);

        if (!EnsureWindowClassRegistered())
        {
            RynthLog.Info($"ViewportPlatform: RegisterClassEx failed (err {Marshal.GetLastWin32Error()})");
            return false;
        }

        ImGuiPlatformIOPtr pio = ImGuiNET.ImGui.GetPlatformIO();
        InstallCallbacks(pio);
        PopulateMonitors(pio);

        ImGuiIOPtr io = ImGuiNET.ImGui.GetIO();
        io.BackendFlags |= ImGuiBackendFlags.PlatformHasViewports;
        // Do NOT set HasMouseHoveredViewport: that flag tells ImGui the backend
        // will route hovered-viewport info via AddMouseViewportEvent each frame.
        // We don't call that, so setting the flag causes mouse input to drop on
        // the floor (IsWindowHovered returns false for every window).

        // The main viewport's PlatformHandle points to the game window; store it
        // so the very first frame doesn't try to create a new HWND for viewport 0.
        ImGuiViewportPtr mainViewport = ImGuiNET.ImGui.GetMainViewport();
        mainViewport.PlatformHandle = _mainHwnd;
        mainViewport.PlatformHandleRaw = _mainHwnd;
        _viewports[(IntPtr)mainViewport.NativePtr] = new ViewportState
        {
            Hwnd = _mainHwnd,
            HwndOwned = false,
            DwStyle = unchecked((uint)(long)GetWindowLongW(_mainHwnd, GWL_STYLE)),
            DwExStyle = unchecked((uint)(long)GetWindowLongW(_mainHwnd, GWL_EXSTYLE)),
        };

        _initialized = true;
        RynthLog.Info("ViewportPlatform: initialized.");
        return true;
    }

    public static void Shutdown()
    {
        if (!_initialized) return;

        // Destroy any viewport windows we created.
        foreach (var kv in _viewports)
        {
            if (kv.Value.HwndOwned && kv.Value.Hwnd != IntPtr.Zero)
                DestroyWindow(kv.Value.Hwnd);
        }
        _viewports.Clear();

        // Clear the Monitors ImVector pointer in PlatformIO before freeing our
        // backing buffer — native ImGui otherwise keeps a stale Data pointer.
        if (_monitorArrayPtr != IntPtr.Zero)
        {
            try
            {
                ImGuiPlatformIOPtr pio = ImGuiNET.ImGui.GetPlatformIO();
                IntPtr pioNative = (IntPtr)pio.NativePtr;
                Marshal.WriteInt32(pioNative, MonitorsSize, 0);
                Marshal.WriteInt32(pioNative, MonitorsCapacity, 0);
                Marshal.WriteIntPtr(pioNative, MonitorsData, IntPtr.Zero);
            }
            catch { /* context may already be torn down */ }

            Marshal.FreeHGlobal(_monitorArrayPtr);
            _monitorArrayPtr = IntPtr.Zero;
            _monitorCount = 0;
        }

        _initialized = false;
    }

    // Dear ImGui asserts Monitor.Size > 0 before the first UpdatePlatformWindows
    // call when ViewportsEnable is on. Populate the Monitors ImVector with the
    // primary (and any secondary) displays so windows can pop out correctly.
    private static void PopulateMonitors(ImGuiPlatformIOPtr pio)
    {
        var rects = new List<(RECT mon, RECT work, bool primary)>();
        MonitorEnumProc cb = (IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr _) =>
        {
            MONITORINFO mi = default;
            mi.cbSize = (uint)Marshal.SizeOf<MONITORINFO>();
            if (GetMonitorInfoW(hMon, ref mi))
                rects.Add((mi.rcMonitor, mi.rcWork, (mi.dwFlags & MONITORINFOF_PRIMARY) != 0));
            return true;
        };

        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero) || rects.Count == 0)
        {
            // Fallback: single primary display via GetSystemMetrics.
            int w = GetSystemMetrics(SM_CXSCREEN);
            int h = GetSystemMetrics(SM_CYSCREEN);
            RECT r = new RECT { Left = 0, Top = 0, Right = w > 0 ? w : 1920, Bottom = h > 0 ? h : 1080 };
            rects.Add((r, r, true));
        }

        GC.KeepAlive(cb);

        _monitorCount = rects.Count;
        _monitorArrayPtr = Marshal.AllocHGlobal(_monitorCount * MonitorStructSize);

        // Primary monitor must be at index 0 for Dear ImGui's viewport code.
        rects.Sort((a, b) => (b.primary ? 1 : 0) - (a.primary ? 1 : 0));

        for (int i = 0; i < _monitorCount; i++)
        {
            var (mon, work, _) = rects[i];
            IntPtr mp = _monitorArrayPtr + i * MonitorStructSize;

            WriteFloat(mp,  0, mon.Left);
            WriteFloat(mp,  4, mon.Top);
            WriteFloat(mp,  8, mon.Right - mon.Left);
            WriteFloat(mp, 12, mon.Bottom - mon.Top);
            WriteFloat(mp, 16, work.Left);
            WriteFloat(mp, 20, work.Top);
            WriteFloat(mp, 24, work.Right - work.Left);
            WriteFloat(mp, 28, work.Bottom - work.Top);
            WriteFloat(mp, 32, 1.0f);
            Marshal.WriteIntPtr(mp, 36, IntPtr.Zero);
        }

        IntPtr pioNative = (IntPtr)pio.NativePtr;
        Marshal.WriteInt32(pioNative, MonitorsSize,     _monitorCount);
        Marshal.WriteInt32(pioNative, MonitorsCapacity, _monitorCount);
        Marshal.WriteIntPtr(pioNative, MonitorsData,    _monitorArrayPtr);

        RynthLog.Info($"ViewportPlatform: populated {_monitorCount} monitor(s) at 0x{_monitorArrayPtr.ToInt64():X8}");
    }

    private static bool EnsureWindowClassRegistered()
    {
        if (_classRegistered) return true;

        _viewportWndProc = ViewportWndProc;
        _viewportWndProcPtr = Marshal.GetFunctionPointerForDelegate(_viewportWndProc);

        WNDCLASSEXW wc = default;
        wc.cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>();
        wc.style = 0x0020 /*CS_OWNDC*/ | 0x0002 /*CS_HREDRAW*/ | 0x0001 /*CS_VREDRAW*/;
        wc.lpfnWndProc = _viewportWndProcPtr;
        wc.hInstance = _hInstance;
        wc.hCursor = LoadCursorW(IntPtr.Zero, IDC_ARROW);
        wc.hbrBackground = IntPtr.Zero;
        wc.lpszClassName = WindowClassName;

        ushort atom = RegisterClassExW(ref wc);
        if (atom == 0)
        {
            int err = Marshal.GetLastWin32Error();
            // 1410 = ERROR_CLASS_ALREADY_EXISTS; fine if we were re-initialized.
            if (err != 1410) return false;
        }

        _classRegistered = true;
        return true;
    }

    // ─── Callback installation ────────────────────────────────────────────
    private static void InstallCallbacks(ImGuiPlatformIOPtr pio)
    {
        IntPtr pioNative = (IntPtr)pio.NativePtr;

        IntPtr createPtr      = RootAndGetPointer<ViewportVoidDelegate>(ViewportCreate);
        IntPtr destroyPtr     = RootAndGetPointer<ViewportVoidDelegate>(ViewportDestroy);
        IntPtr showPtr        = RootAndGetPointer<ViewportVoidDelegate>(ViewportShow);
        IntPtr setPosPtr      = RootAndGetPointer<ViewportSetVec2Delegate>(ViewportSetPos);
        IntPtr setSizePtr     = RootAndGetPointer<ViewportSetVec2Delegate>(ViewportSetSize);
        IntPtr setFocusPtr    = RootAndGetPointer<ViewportVoidDelegate>(ViewportSetFocus);
        IntPtr getFocusPtr    = RootAndGetPointer<ViewportGetBoolDelegate>(ViewportGetFocus);
        IntPtr getMinPtr      = RootAndGetPointer<ViewportGetBoolDelegate>(ViewportGetMinimized);
        IntPtr setTitlePtr    = RootAndGetPointer<ViewportSetStringDelegate>(ViewportSetTitle);
        IntPtr setAlphaPtr    = RootAndGetPointer<ViewportSetFloatDelegate>(ViewportSetAlpha);
        IntPtr updatePtr      = RootAndGetPointer<ViewportVoidDelegate>(ViewportUpdate);

        // Direct writes at native cimgui.dll offsets (NOT ImGui.NET's compiled
        // layout offsets — those are off by 36 bytes). Void-return and
        // small-return callbacks match the cdecl ABI naturally.
        Marshal.WriteIntPtr(pioNative, ViewportOffsets.PlatformCreateWindow,       createPtr);
        Marshal.WriteIntPtr(pioNative, ViewportOffsets.PlatformDestroyWindow,      destroyPtr);
        Marshal.WriteIntPtr(pioNative, ViewportOffsets.PlatformShowWindow,         showPtr);
        Marshal.WriteIntPtr(pioNative, ViewportOffsets.PlatformSetWindowPos,       setPosPtr);
        Marshal.WriteIntPtr(pioNative, ViewportOffsets.PlatformSetWindowSize,      setSizePtr);
        Marshal.WriteIntPtr(pioNative, ViewportOffsets.PlatformSetWindowFocus,     setFocusPtr);
        Marshal.WriteIntPtr(pioNative, ViewportOffsets.PlatformGetWindowFocus,     getFocusPtr);
        Marshal.WriteIntPtr(pioNative, ViewportOffsets.PlatformGetWindowMinimized, getMinPtr);
        Marshal.WriteIntPtr(pioNative, ViewportOffsets.PlatformSetWindowTitle,     setTitlePtr);
        Marshal.WriteIntPtr(pioNative, ViewportOffsets.PlatformSetWindowAlpha,     setAlphaPtr);
        Marshal.WriteIntPtr(pioNative, ViewportOffsets.PlatformUpdateWindow,       updatePtr);

        // GetWindowPos / GetWindowSize return ImVec2 by value (x86 cdecl struct
        // return — hidden first arg on stack). Our C# delegates take an out-ptr
        // instead; cimgui ships a wrapper helper that adapts the ABI and writes
        // the wrapper at the right offset for us. Must install via helper, not
        // Marshal.WriteIntPtr, or native crashes on the first call.
        IntPtr getPosPtr  = RootAndGetPointer<ViewportGetVec2Delegate>(ViewportGetPos);
        IntPtr getSizePtr = RootAndGetPointer<ViewportGetVec2Delegate>(ViewportGetSize);
        ImGuiNative.ImGuiPlatformIO_Set_Platform_GetWindowPos(pio.NativePtr, getPosPtr);
        ImGuiNative.ImGuiPlatformIO_Set_Platform_GetWindowSize(pio.NativePtr, getSizePtr);

        // Read back and log so a silent mismatch surfaces as a log line, not an IM_ASSERT.
        IntPtr verifyCreate  = Marshal.ReadIntPtr(pioNative, ViewportOffsets.PlatformCreateWindow);
        IntPtr verifyGetPos  = Marshal.ReadIntPtr(pioNative, ViewportOffsets.PlatformGetWindowPos);
        RynthLog.Info($"ViewportPlatform: installed handlers (CreateWindow readback=0x{verifyCreate.ToInt64():X8}, expected=0x{createPtr.ToInt64():X8}, GetWindowPos shim=0x{verifyGetPos.ToInt64():X8})");
    }

    private static IntPtr RootAndGetPointer<TDelegate>(TDelegate del) where TDelegate : Delegate
    {
        _delegateRoots.Add(del);
        return Marshal.GetFunctionPointerForDelegate<TDelegate>(del);
    }

    private static void WriteFloat(IntPtr basePtr, int offset, float value)
    {
        Marshal.WriteInt32(basePtr, offset, BitConverter.SingleToInt32Bits(value));
    }

    // ─── Callback implementations ─────────────────────────────────────────
    private static void ViewportCreate(ImGuiViewport* vp)
    {
        uint flags = unchecked((uint)vp->Flags);
        bool noDecoration = (flags & (uint)ImGuiViewportFlags.NoDecoration) != 0;
        bool topMost = (flags & (uint)ImGuiViewportFlags.TopMost) != 0;

        uint dwStyle = noDecoration
            ? WS_POPUP | WS_CLIPCHILDREN | WS_CLIPSIBLINGS
            : WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN | WS_CLIPSIBLINGS;

        // Always use WS_EX_TOOLWINDOW so tear-off viewports don't clutter the taskbar / Alt-Tab
        // list when users run many clients. Owned by _mainHwnd so it still closes with the game.
        uint dwExStyle = WS_EX_TOOLWINDOW;
        if (topMost) dwExStyle |= WS_EX_TOPMOST;
        dwExStyle |= WS_EX_NOACTIVATE;

        // Compute outer window rect that yields the requested client size.
        RECT rect;
        rect.Left = (int)vp->Pos.X;
        rect.Top = (int)vp->Pos.Y;
        rect.Right = rect.Left + (int)vp->Size.X;
        rect.Bottom = rect.Top + (int)vp->Size.Y;
        AdjustWindowRectEx(ref rect, dwStyle, false, dwExStyle);

        IntPtr hwnd = CreateWindowExW(
            dwExStyle, WindowClassName, "RynthCore Viewport", dwStyle,
            rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top,
            _mainHwnd, IntPtr.Zero, _hInstance, IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
        {
            RynthLog.Info($"ViewportPlatform: CreateWindowEx failed (err {Marshal.GetLastWin32Error()})");
            return;
        }

        var state = new ViewportState { Hwnd = hwnd, HwndOwned = true, DwStyle = dwStyle, DwExStyle = dwExStyle };
        _viewports[(IntPtr)vp] = state;

        vp->PlatformHandle = (void*)hwnd;
        vp->PlatformHandleRaw = (void*)hwnd;
    }

    private static void ViewportDestroy(ImGuiViewport* vp)
    {
        IntPtr key = (IntPtr)vp;
        if (_viewports.TryGetValue(key, out var state))
        {
            if (state.HwndOwned && state.Hwnd != IntPtr.Zero)
                DestroyWindow(state.Hwnd);
            _viewports.Remove(key);
        }

        vp->PlatformHandle = (void*)IntPtr.Zero;
        vp->PlatformHandleRaw = (void*)IntPtr.Zero;
    }

    private static void ViewportShow(ImGuiViewport* vp)
    {
        if (!TryGetState(vp, out var state)) return;

        ShowWindow(state.Hwnd, SW_SHOWNA);
    }

    private static void ViewportSetPos(ImGuiViewport* vp, Vector2 pos)
    {
        if (!TryGetState(vp, out var state)) return;

        RECT rect;
        rect.Left = (int)pos.X;
        rect.Top = (int)pos.Y;
        rect.Right = rect.Left;
        rect.Bottom = rect.Top;
        AdjustWindowRectEx(ref rect, state.DwStyle, false, state.DwExStyle);
        SetWindowPos(state.Hwnd, IntPtr.Zero, rect.Left, rect.Top, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private static void ViewportSetSize(ImGuiViewport* vp, Vector2 size)
    {
        if (!TryGetState(vp, out var state)) return;

        RECT rect;
        rect.Left = 0; rect.Top = 0;
        rect.Right = (int)size.X;
        rect.Bottom = (int)size.Y;
        AdjustWindowRectEx(ref rect, state.DwStyle, false, state.DwExStyle);
        SetWindowPos(state.Hwnd, IntPtr.Zero, 0, 0,
            rect.Right - rect.Left, rect.Bottom - rect.Top,
            SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private static void ViewportGetPos(ImGuiViewport* vp, Vector2* outPos)
    {
        *outPos = new Vector2(0, 0);
        if (!TryGetState(vp, out var state)) return;
        // Return the CLIENT-area origin in screen coords, not the outer window
        // rect. ImGui's hit-testing uses this as the viewport origin; if we
        // include the title-bar height, every click misses by that offset.
        POINT p = new POINT { X = 0, Y = 0 };
        if (!ClientToScreen(state.Hwnd, ref p)) return;
        *outPos = new Vector2(p.X, p.Y);
    }

    private static void ViewportGetSize(ImGuiViewport* vp, Vector2* outSize)
    {
        *outSize = new Vector2(0, 0);
        if (!TryGetState(vp, out var state)) return;
        if (!GetClientRect(state.Hwnd, out RECT r)) return;
        *outSize = new Vector2(r.Right - r.Left, r.Bottom - r.Top);
    }

    private static void ViewportSetFocus(ImGuiViewport* vp)
    {
        if (!TryGetState(vp, out var state)) return;
        SetForegroundWindow(state.Hwnd);
    }

    private static byte ViewportGetFocus(ImGuiViewport* vp)
    {
        if (!TryGetState(vp, out var state)) return 0;
        return GetForegroundWindow() == state.Hwnd ? (byte)1 : (byte)0;
    }

    private static byte ViewportGetMinimized(ImGuiViewport* vp)
    {
        if (!TryGetState(vp, out var state)) return 0;
        return IsIconic(state.Hwnd) ? (byte)1 : (byte)0;
    }

    private static void ViewportSetTitle(ImGuiViewport* vp, IntPtr strPtr)
    {
        if (!TryGetState(vp, out var state)) return;
        string title = Marshal.PtrToStringUTF8(strPtr) ?? "RynthCore Viewport";
        SetWindowTextW(state.Hwnd, title);
    }

    private static void ViewportSetAlpha(ImGuiViewport* vp, float alpha)
    {
        if (!TryGetState(vp, out var state)) return;
        IntPtr exStyle = GetWindowLongW(state.Hwnd, GWL_EXSTYLE);
        long exs = (long)exStyle;
        if (alpha < 1.0f)
            SetWindowLongW(state.Hwnd, GWL_EXSTYLE, (IntPtr)(exs | WS_EX_LAYERED));
        byte a = (byte)Math.Clamp((int)(alpha * 255f + 0.5f), 0, 255);
        SetLayeredWindowAttributes(state.Hwnd, 0, a, LWA_ALPHA);
    }

    private static void ViewportUpdate(ImGuiViewport* vp)
    {
        // No-op on Win32 — imgui_impl_win32 handles monitor updates and other
        // housekeeping elsewhere. We refresh monitors from ImGuiController.
    }

    private static bool TryGetState(ImGuiViewport* vp, out ViewportState state)
    {
        return _viewports.TryGetValue((IntPtr)vp, out state!);
    }

    // ─── WndProc for viewport HWNDs ───────────────────────────────────────
    internal static IntPtr ImGuiContext;

    private static IntPtr ViewportWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // OS delivers messages (WM_GETICON, WM_PAINT, etc.) asynchronously outside
        // of EndScene. By then our saved context has been restored → GImGui is null,
        // and any ImGui call here AVs. Temporarily set our context for the message.
        IntPtr saved = ImGuiNET.ImGui.GetCurrentContext();
        if (ImGuiContext != IntPtr.Zero && saved != ImGuiContext)
            ImGuiNET.ImGui.SetCurrentContext(ImGuiContext);

        try
        {
            ImGuiViewportPtr vp = ImGuiNET.ImGui.FindViewportByPlatformHandle(hWnd);
            if (vp.NativePtr == null)
                return DefWindowProcW(hWnd, msg, wParam, lParam);

            ImGuiIOPtr io = ImGuiNET.ImGui.GetIO();

            // ── Mouse position: forward in screen coords (multi-viewport) ────
            if (msg == WM_MOUSEMOVE || msg == WM_LBUTTONDOWN || msg == WM_LBUTTONUP
                || msg == WM_LBUTTONDBLCLK || msg == WM_RBUTTONDOWN || msg == WM_RBUTTONUP
                || msg == WM_RBUTTONDBLCLK || msg == WM_MBUTTONDOWN || msg == WM_MBUTTONUP
                || msg == WM_MBUTTONDBLCLK || msg == WM_XBUTTONDOWN || msg == WM_XBUTTONUP)
            {
                short cx = (short)((long)lParam & 0xFFFF);
                short cy = (short)(((long)lParam >> 16) & 0xFFFF);
                POINT pt = new POINT { X = cx, Y = cy };
                ClientToScreen(hWnd, ref pt);
                io.AddMouseSourceEvent(ImGuiMouseSource.Mouse);
                io.AddMousePosEvent(pt.X, pt.Y);
            }

            switch (msg)
            {
                case WM_LBUTTONDOWN:
                case WM_LBUTTONDBLCLK:
                    if (SetCapture(hWnd) == IntPtr.Zero) { /* ignore */ }
                    io.AddMouseButtonEvent(0, true);
                    return IntPtr.Zero;
                case WM_LBUTTONUP:
                    io.AddMouseButtonEvent(0, false);
                    ReleaseCapture();
                    return IntPtr.Zero;

                case WM_RBUTTONDOWN:
                case WM_RBUTTONDBLCLK:
                    SetCapture(hWnd);
                    io.AddMouseButtonEvent(1, true);
                    return IntPtr.Zero;
                case WM_RBUTTONUP:
                    io.AddMouseButtonEvent(1, false);
                    ReleaseCapture();
                    return IntPtr.Zero;

                case WM_MBUTTONDOWN:
                case WM_MBUTTONDBLCLK:
                    SetCapture(hWnd);
                    io.AddMouseButtonEvent(2, true);
                    return IntPtr.Zero;
                case WM_MBUTTONUP:
                    io.AddMouseButtonEvent(2, false);
                    ReleaseCapture();
                    return IntPtr.Zero;

                case WM_XBUTTONDOWN:
                {
                    int btn = (((int)(long)wParam >> 16) & 0xFFFF) == 1 ? 3 : 4;
                    SetCapture(hWnd);
                    io.AddMouseButtonEvent(btn, true);
                    return IntPtr.Zero;
                }
                case WM_XBUTTONUP:
                {
                    int btn = (((int)(long)wParam >> 16) & 0xFFFF) == 1 ? 3 : 4;
                    io.AddMouseButtonEvent(btn, false);
                    ReleaseCapture();
                    return IntPtr.Zero;
                }

                case WM_MOUSEWHEEL:
                    io.AddMouseWheelEvent(0f, (short)(((long)wParam >> 16) & 0xFFFF) / 120f);
                    return IntPtr.Zero;
                case WM_MOUSEHWHEEL:
                    io.AddMouseWheelEvent((short)(((long)wParam >> 16) & 0xFFFF) / 120f, 0f);
                    return IntPtr.Zero;

                case WM_KEYDOWN:
                case WM_SYSKEYDOWN:
                {
                    ImGuiKey key = VkToImGuiKey((int)(long)wParam);
                    if (key != ImGuiKey.None) io.AddKeyEvent(key, true);
                    // Let DefWindowProc run so system keys still function (Alt+F4 etc.).
                    break;
                }
                case WM_KEYUP:
                case WM_SYSKEYUP:
                {
                    ImGuiKey key = VkToImGuiKey((int)(long)wParam);
                    if (key != ImGuiKey.None) io.AddKeyEvent(key, false);
                    break;
                }
                case WM_CHAR:
                {
                    uint ch = (uint)(long)wParam;
                    if (ch > 0 && ch < 0x10000) io.AddInputCharacter(ch);
                    return IntPtr.Zero;
                }

                case WM_CLOSE:
                    vp.PlatformRequestClose = true;
                    return IntPtr.Zero;

                case WM_MOVE:
                    vp.PlatformRequestMove = true;
                    break;

                case WM_SIZE:
                    vp.PlatformRequestResize = true;
                    break;

                case WM_MOUSEACTIVATE:
                {
                    // If AC is the foreground app, don't steal its focus — just process the click.
                    // If the user is tabbed out (some other app is foreground), allow Windows to
                    // activate this viewport so the click actually brings it to the front.
                    IntPtr fg = GetForegroundWindow();
                    IntPtr game = Win32Backend.GameHwnd;
                    if (game != IntPtr.Zero && fg == game)
                        return new IntPtr(MA_NOACTIVATE);
                    break;
                }

                case WM_NCHITTEST:
                {
                    uint flags = unchecked((uint)vp.Flags);
                    if ((flags & (uint)ImGuiViewportFlags.NoInputs) != 0)
                        return new IntPtr(-1 /*HTTRANSPARENT*/);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            RynthLog.Info($"ViewportPlatform.WndProc: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (ImGuiContext != IntPtr.Zero && saved != ImGuiContext)
                ImGuiNET.ImGui.SetCurrentContext(saved);
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
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
            >= 0x30 and <= 0x39 => ImGuiKey._0 + (vk - 0x30),
            >= 0x41 and <= 0x5A => ImGuiKey.A + (vk - 0x41),
            >= 0x60 and <= 0x69 => ImGuiKey.Keypad0 + (vk - 0x60),
            >= 0x70 and <= 0x7B => ImGuiKey.F1 + (vk - 0x70),
            0x10 => ImGuiKey.LeftShift,
            0x11 => ImGuiKey.LeftCtrl,
            0x12 => ImGuiKey.LeftAlt,
            _ => ImGuiKey.None,
        };
    }
}
