// ============================================================================
//  RynthCore.Engine - WebPanel/WebPanelHost.cs   (CoreCLR engine only)
//
//  One WebView2 panel over the game's client area, on a dedicated STA thread with its own
//  GetMessage loop and an UNOWNED WS_POPUP host window (an owned popup would attach the game's
//  input queue). Lifted from the 2026-11-07 spike (spikes\webview2\RynthWebSpike\WebPanel.cs,
//  popup-free path), which ran it inside a live acclient.
//
//  Threads: Open/Navigate/Close/GetState run on any thread (AC's main thread, the plugin pump,
//  ImGui draws) and never block: they validate, update the caller-visible state under _sync,
//  queue work and post WM_RUN to the host window. Everything that touches Win32 windows or
//  WebView2 runs on the panel thread. Shutdown posts the teardown and joins within a budget.
//
//  Lifetime: everything started or registered here (thread, window class, WinEvent hook, timer,
//  WebView2 controller/environment) is torn down by Shutdown, before the collectible engine
//  context unloads.
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using RynthCore.Engine.Plugins;
using static RynthCore.Engine.WebPanel.WebPanelNative;

namespace RynthCore.Engine.WebPanel;

internal sealed unsafe class WebPanelHost
{
    private const uint WM_RUN = WM_APP + 1;
    private const int CaptionPx = 28, CloseBoxPx = 34;
    private const uint FlagFallback = 1, FlagPageKeepsEscape = 2;
    private const uint StateVisible = 1, StateFocused = 2, StateOpenedInBrowser = 4;
    private const uint VK_ESCAPE = 0x1B;
    private static readonly IntPtr TimerId = (IntPtr)1;
    private static readonly uint Background = Rgb(32, 32, 36), CaptionColor = Rgb(44, 44, 50), TextColor = Rgb(222, 222, 228);

    // The WndProc and WinEvent target. One live host per engine generation (WebPanelApi holds it).
    private static WebPanelHost? s_active;
    private static int s_classSeq;

    private readonly IWebPanelEnvironment _env;
    private readonly uint _ourPid = GetCurrentProcessId();

    // ---- caller-visible state: under _sync ----
    private readonly object _sync = new();
    private readonly ConcurrentQueue<Action> _queue = new();
    private int _nextHandle;
    private int _live;
    private int _status;
    private uint _flags;
    private int _error, _http;
    private uint _navCount;
    private string _liveUrl = "";
    private bool _shuttingDown;
    private Thread? _thread;
    private bool _threadDone;
    private IntPtr _hwnd;            // published once the window exists; WM_RUN goes here

    // ---- panel thread only ----
    private IntPtr _staHwnd, _hook, _bgBrush, _captionBrush;
    private string _className = "";
    private bool _windowFailed, _tornDown, _timerOn, _inSizeMove;
    private volatile bool _open;
    private int _serving;            // the handle whose request the panel shows
    private WebPanelRequest? _req;
    private string _title = "RynthCore";
    private PanelFractions _fractions = WebPanelWindowPolicy.Default(0, 0);
    private string _pendingUrl = "";
    private bool _loaderLoaded;
    private CoreWebView2Environment? _wvEnv;
    private CoreWebView2Controller? _ctl;
    private CoreWebView2? _web;
    private string? _profileFolder;
    private bool _creating, _needsRecreate;
    private int _createSeq;
    // Off-list pages to the system browser: only after a click, and rate-limited (panel thread only).
    private readonly WebPanelBrowserGate _browserGate = new();

    // ---- diagnostics (Describe and the offline harness) ----
    private volatile int _environmentCount, _acceleratorEvents;
    private volatile uint _browserPid;
    private volatile string _source = "";

    public WebPanelHost(IWebPanelEnvironment env) => _env = env;

    // ============================ any thread ============================

    public int Open(WebPanelRequest request)
    {
        if (request == null) return (int)WebPanelCode.BadArgs;
        lock (_sync) if (_shuttingDown) return (int)WebPanelCode.ShuttingDown;
        if (!WebPanelUrlPolicy.IsAllowed(request.Url, request.Allowlist)) return (int)WebPanelCode.UrlNotAllowed;
        IntPtr game = _env.GameHwnd;
        if (game == IntPtr.Zero || !IsWindow(game)) return (int)WebPanelCode.NoGameWindow;

        lock (_sync)
        {
            if (_shuttingDown) return (int)WebPanelCode.ShuttingDown;
            if (_nextHandle <= 0) _nextHandle = (Environment.TickCount & 0x3FFFFFFF) | 1;
            int handle = _nextHandle++;
            // Kept to undo below if the thread cannot start: the caller then sees a failure, and the state must agree.
            int prevLive = _live, prevStatus = _status, prevError = _error, prevHttp = _http;
            uint prevFlags = _flags, prevNav = _navCount;
            string prevUrl = _liveUrl;
            string[] prevAllow = _currentAllowlist;
            _live = handle;
            _status = (int)WebPanelStatusCode.Starting;
            _flags = 0;
            _error = 0;
            _http = 0;
            _navCount = 0;
            _liveUrl = request.Url;
            _currentAllowlist = request.Allowlist;
            _queue.Enqueue(() => ProcessOpen(handle, request));
            if (_thread == null || _threadDone)
            {
                // First call (or the thread died): start it and return at once. The thread creates its
                // window first, then drains the queue.
                try
                {
                    StartThreadLocked();
                }
                catch
                {
                    // The queued ProcessOpen stays behind but is skipped: its handle is no longer live.
                    _live = prevLive; _status = prevStatus; _flags = prevFlags; _error = prevError;
                    _http = prevHttp; _navCount = prevNav; _liveUrl = prevUrl; _currentAllowlist = prevAllow;
                    _threadDone = true;
                    throw; // WebPanelApi reports Unavailable
                }
            }
            else if (_hwnd != IntPtr.Zero)
            {
                PostMessageW(_hwnd, WM_RUN, IntPtr.Zero, IntPtr.Zero);
            }
            return handle;
        }
    }

    public int Navigate(int handle, string url)
    {
        lock (_sync)
        {
            if (_shuttingDown) return (int)WebPanelCode.ShuttingDown;
            if (handle <= 0 || handle != _live) return (int)WebPanelCode.StaleHandle;
            if (_status == (int)WebPanelStatusCode.Closed || _status == (int)WebPanelStatusCode.Failed) return (int)WebPanelCode.StaleHandle;
        }
        if (string.IsNullOrEmpty(url)) return (int)WebPanelCode.BadArgs;
        string[] allow;
        lock (_sync) allow = _currentAllowlist;
        if (!WebPanelUrlPolicy.IsAllowed(url, allow))
        {
            // Off the list: the system browser when that is safe (on the panel thread: ShellExecute can be slow).
            Post(() => RouteOffList(url, userInitiated: true)); // a plugin asked: a button or a command
            return (int)WebPanelCode.UrlNotAllowed;
        }
        return Post(() => NavigateNow(handle, url)) ? (int)WebPanelCode.Queued : (int)WebPanelCode.ShuttingDown;
    }

    public int Close(int handle)
    {
        lock (_sync)
        {
            if (_shuttingDown) return (int)WebPanelCode.ShuttingDown;
            if (handle <= 0 || handle != _live) return (int)WebPanelCode.StaleHandle;
            if (_status == (int)WebPanelStatusCode.Closed || _status == (int)WebPanelStatusCode.Failed) return (int)WebPanelCode.AlreadyClosed;
        }
        return Post(() => CloseNow(handle)) ? (int)WebPanelCode.Queued : (int)WebPanelCode.ShuttingDown;
    }

    public int GetState(int handle, out WebPanelSnapshot s)
    {
        lock (_sync)
        {
            if (_shuttingDown)
            {
                s = new WebPanelSnapshot(handle, (int)WebPanelStatusCode.None, 0, (int)WebPanelCode.ShuttingDown, 0, 0);
                return (int)WebPanelCode.ShuttingDown;
            }
            if (handle <= 0 || handle != _live)
            {
                s = new WebPanelSnapshot(handle, (int)WebPanelStatusCode.Superseded, 0, 0, 0, 0);
                return (int)WebPanelStatusCode.Superseded;
            }
            s = new WebPanelSnapshot(_live, _status, _flags, _error, _http, _navCount);
            return _status;
        }
    }

    /// <summary>Idempotent. Posts the teardown (which ends with WM_QUIT) and joins the thread within the budget.</summary>
    public void Shutdown(int budgetMs)
    {
        Thread? t;
        lock (_sync)
        {
            t = _thread;
            if (!_shuttingDown)
            {
                _shuttingDown = true;
                if (t != null && !_threadDone)
                {
                    _queue.Enqueue(TeardownOnPanelThread);
                    if (_hwnd != IntPtr.Zero) PostMessageW(_hwnd, WM_RUN, IntPtr.Zero, IntPtr.Zero);
                }
            }
        }
        if (t == null || t == Thread.CurrentThread) return;
        if (!t.Join(Math.Max(0, budgetMs)))
            SafeLog($"shutdown: the panel thread did not finish within {budgetMs} ms");
    }

    /// <summary>The live handle (0 before the first Open), for engine-side callers such as a close-on-command.</summary>
    public int LiveHandle { get { lock (_sync) return _live; } }

    public string Describe()
    {
        lock (_sync)
        {
            string thread = _thread == null ? "not started" : _threadDone ? "stopped" : "running";
            string status = ((WebPanelStatusCode)_status).ToString();
            return $"web panel: thread {thread}{(_shuttingDown ? " (shut down)" : "")}, handle {_live}, {status}, error {_error}, " +
                   $"http {_http}, navigations {_navCount}, environments {_environmentCount}, visible {((_flags & StateVisible) != 0 ? "yes" : "no")}, focused {((_flags & StateFocused) != 0 ? "yes" : "no")}, in browser {((_flags & StateOpenedInBrowser) != 0 ? "yes" : "no")}, browser pid {_browserPid}, url {WebPanelUrlPolicy.Redact(_liveUrl)}";
        }
    }

    /// <summary>Under _sync. _threadDone goes false only once the thread exists, so a failed start leaves it true.</summary>
    private void StartThreadLocked()
    {
        _hwnd = IntPtr.Zero;
        _thread = _env.StartStaThread("RynthCore.WebPanel", ThreadMain);
        _threadDone = false;
    }

    // The live request's allowlist, for Navigate's caller-side check (set on Open, under _sync).
    private string[] _currentAllowlist = Array.Empty<string>();

    private bool Post(Action a)
    {
        lock (_sync)
        {
            if (_thread == null || _threadDone) return false;
            _queue.Enqueue(a);
            if (_hwnd != IntPtr.Zero) PostMessageW(_hwnd, WM_RUN, IntPtr.Zero, IntPtr.Zero);
            return true;
        }
    }

    /// <summary>Applies <paramref name="apply"/> to the caller-visible state only while <paramref name="handle"/> is the live one.</summary>
    private void SetState(int handle, Action apply)
    {
        lock (_sync) if (handle == _live) apply();
    }

    private void SafeLog(string line) { try { _env.Log(line); } catch { } }
    private void SafeChat(string line) { try { _env.Chat(line); } catch { } }

    // ---- test and diagnostic views (no secrets: Source is never logged) ----
    internal IntPtr HostHwndForTests { get { lock (_sync) return _hwnd; } }
    internal int EnvironmentCountForTests => _environmentCount;
    internal uint BrowserProcessIdForTests => _browserPid;
    internal int AcceleratorKeyEventsForTests => _acceleratorEvents;
    internal string SourceForTests => _source;
    internal bool ThreadRunningForTests { get { lock (_sync) return _thread != null && !_threadDone && _thread.IsAlive; } }
    internal bool PostForTests(Action<CoreWebView2?> a) => Post(() => a(_web));
    internal bool PanelOpenForTests => _open;
    /// <summary>A renderer-unresponsive report, as WebView2 would raise it (counting against UnresponsiveLimit).</summary>
    internal bool UnresponsiveForTests() => Post(() => HandleProcessFailed(CoreWebView2ProcessFailedKind.RenderProcessUnresponsive));
    /// <summary>A failure arriving late for <paramref name="handle"/> (the harness passes a superseded one).</summary>
    internal bool FailForTests(int handle) => Post(() => Fail(handle, WebPanelCode.ProcessFailed, "synthetic (harness)", allowFallback: false));

    // ============================ panel thread ============================

    private void ThreadMain()
    {
        // A thread restarted after a failed one (no window) starts clean.
        _tornDown = false;
        _windowFailed = false;
        try
        {
            try { CreateHostWindow(); }
            catch (Exception ex)
            {
                _windowFailed = true;
                SafeLog("could not create the panel window: " + ex.GetType().Name + " " + ex.Message);
            }
            lock (_sync) _hwnd = _staHwnd;
            Drain();
            if (_staHwnd != IntPtr.Zero)
            {
                while (GetMessageW(out MSG msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                }
            }
            else
            {
                TeardownOnPanelThread();
            }
        }
        catch (Exception ex)
        {
            SafeLog("panel thread failed: " + ex.GetType().Name + " " + ex.Message);
        }
        finally
        {
            // However the loop ended (WM_QUIT from the teardown, a GetMessage error, an exception), nothing
            // this thread registered (window class, WinEvent hook, WebView2) may outlive it: the class's WndProc
            // points into the collectible engine context.
            if (!_tornDown) { try { TeardownOnPanelThread(); } catch { } }
            lock (_sync)
            {
                _threadDone = true;
                _hwnd = IntPtr.Zero;
                // An Open queued after this thread's last drain (e.g. while a failed window creation was being
                // torn down) would otherwise wait for the next Open: start a fresh thread for it now. The new
                // thread cannot finish (its finally takes this lock) before _threadDone is set back.
                if (!_shuttingDown && !_queue.IsEmpty)
                {
                    try { StartThreadLocked(); }
                    catch (Exception ex) { SafeLog("could not restart the panel thread: " + ex.GetType().Name); }
                }
            }
        }
    }

    private void CreateHostWindow()
    {
        _className = $"RynthCoreWebPanel_{Environment.TickCount64:X}_{Interlocked.Increment(ref s_classSeq):X}";
        _bgBrush = CreateSolidBrush(Background);
        _captionBrush = CreateSolidBrush(CaptionColor);
        IntPtr inst = GetModuleHandleW(IntPtr.Zero);
        fixed (char* pName = _className)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                style = CS_HREDRAW | CS_VREDRAW,
                lpfnWndProc = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WndProc,
                hInstance = inst,
                hCursor = LoadCursorW(IntPtr.Zero, (IntPtr)IDC_ARROW),
                hbrBackground = _bgBrush,
                lpszClassName = (IntPtr)pName,
            };
            if (RegisterClassExW(ref wc) == 0)
            {
                int err = Marshal.GetLastWin32Error();
                _className = "";
                throw new InvalidOperationException("RegisterClassExW failed " + err);
            }
        }
        s_active = this;
        IntPtr h = CreateWindowExW(WS_EX_TOOLWINDOW, _className, "RynthCore", WS_POPUP | WS_CLIPCHILDREN | WS_THICKFRAME,
            0, 0, 640, 480, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        if (h == IntPtr.Zero) throw new InvalidOperationException("CreateWindowExW failed " + Marshal.GetLastWin32Error());
        _staHwnd = h;
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, &WinEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        WebPanelHost? p = s_active;
        if (p != null)
        {
            try
            {
                if (p.HandleMessage(h, m, w, l, out IntPtr r)) return r;
            }
            catch (Exception ex)
            {
                p.SafeLog("WndProc threw " + ex.GetType().Name + " " + ex.Message);
            }
        }
        return DefWindowProcW(h, m, w, l);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        WebPanelHost? p = s_active;
        if (p == null || hook != p._hook) return;
        try { p.Track(); }
        catch (Exception ex) { p.SafeLog("foreground tracking threw " + ex.GetType().Name + " " + ex.Message); }
    }

    private bool HandleMessage(IntPtr h, uint m, IntPtr w, IntPtr l, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (_staHwnd == IntPtr.Zero || h != _staHwnd) return false; // during CreateWindowExW: default handling
        switch (m)
        {
            case WM_RUN:
                Drain();
                return true;
            case WM_TIMER:
                if (w != TimerId) return false;
                Track();
                return true;
            case WM_SIZE:
                ResizeController();
                return false;
            case WM_SETFOCUS:
                try { _ctl?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic); } catch { }
                return false;
            case WM_CLOSE: // Alt+F4 on the panel: close it, keep the window
                CloseNow(_serving);
                return true;
            case WM_NCHITTEST:
            {
                result = DefWindowProcW(h, m, w, l);
                if (result == (IntPtr)HTCLIENT)
                {
                    var pt = new POINT { X = (short)((long)l & 0xFFFF), Y = (short)(((long)l >> 16) & 0xFFFF) };
                    ScreenToClient(h, ref pt);
                    if (pt.Y >= 0 && pt.Y < CaptionPx && !InCloseBox(pt)) result = (IntPtr)HTCAPTION;
                }
                return true;
            }
            case WM_LBUTTONDOWN:
            {
                var pt = new POINT { X = (short)((long)l & 0xFFFF), Y = (short)(((long)l >> 16) & 0xFFFF) };
                if (InCloseBox(pt)) CloseNow(_serving);
                return true;
            }
            case WM_PAINT:
                Paint(h);
                return true;
            case WM_ENTERSIZEMOVE:
                _inSizeMove = true;
                return false;
            case WM_EXITSIZEMOVE:
                _inSizeMove = false;
                RememberUserLayout();
                return false;
        }
        return false;
    }

    private bool InCloseBox(POINT pt)
    {
        GetClientRect(_staHwnd, out RECT rc);
        return pt.Y >= 0 && pt.Y < CaptionPx && pt.X >= rc.Right - CloseBoxPx && pt.X < rc.Right;
    }

    private void Paint(IntPtr h)
    {
        IntPtr dc = BeginPaint(h, out PAINTSTRUCT ps);
        try
        {
            GetClientRect(h, out RECT rc);
            var strip = new RECT { Left = 0, Top = 0, Right = rc.Right, Bottom = Math.Min(CaptionPx, rc.Bottom) };
            FillRect(dc, ref strip, _captionBrush);
            SetBkMode(dc, TRANSPARENT);
            SetTextColor(dc, TextColor);
            IntPtr old = SelectObject(dc, GetStockObject(DEFAULT_GUI_FONT));
            var title = new RECT { Left = 10, Top = 0, Right = Math.Max(10, rc.Right - CloseBoxPx - 6), Bottom = strip.Bottom };
            DrawTextW(dc, _title, -1, ref title, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX);
            var x = new RECT { Left = rc.Right - CloseBoxPx, Top = 0, Right = rc.Right, Bottom = strip.Bottom };
            DrawTextW(dc, "X", -1, ref x, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
            SelectObject(dc, old);
        }
        finally { EndPaint(h, ref ps); }
    }

    private void Drain()
    {
        while (_queue.TryDequeue(out Action? a))
        {
            try { a(); }
            catch (Exception ex) { SafeLog("panel action threw " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8")); } // no Message: it could carry a URL
        }
    }

    private bool IsLive(int handle) { lock (_sync) return handle == _live && !_shuttingDown; }

    private void ProcessOpen(int handle, WebPanelRequest r)
    {
        if (_tornDown || !IsLive(handle)) return; // a newer Open is queued behind this one
        _serving = handle;
        _unresponsiveCount = 0; // a new panel starts its own count
        _req = r;
        _title = string.IsNullOrWhiteSpace(r.Title) ? "RynthCore" : r.Title.Trim();
        _fractions = WebPanelLayoutStore.TryLoad(_env.LocalAppData, out PanelFractions stored)
            ? stored
            : WebPanelWindowPolicy.Default(r.WidthPercent, r.HeightPercent);
        _pendingUrl = r.Url;
        if (_windowFailed) { Fail(handle, WebPanelCode.CreateFailed, "no panel window", allowFallback: true); return; }

        if (_needsRecreate || (_profileFolder != null && !string.Equals(_profileFolder, r.ProfileFolder, StringComparison.OrdinalIgnoreCase)))
            TearDownWebView();

        _open = true;
        InvalidateRect(_staHwnd, IntPtr.Zero, true);
        Show();
        if (_ctl != null) { NavigatePending(handle); return; }
        if (!_creating) StartCreate(handle, r.ProfileFolder);
    }

    private void StartCreate(int handle, string folder)
    {
        _creating = true;
        int seq = ++_createSeq;
        _profileFolder = folder;
        if (!EnsureLoader(out string why)) { CreationFailed(seq, WebPanelCode.CreateFailed, why); return; }
        try
        {
            string version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (string.IsNullOrEmpty(version)) { CreationFailed(seq, WebPanelCode.RuntimeMissing, "no WebView2 runtime"); return; }
        }
        catch (Exception ex)
        {
            CreationFailed(seq, Classify(ex), "runtime probe: " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8"));
            return;
        }
        int delay = _env.EnvironmentDelayMsForTests;
        if (delay > 0) Task.Delay(delay).ContinueWith(_ => Post(() => CreateEnvironment(seq, folder)));
        else CreateEnvironment(seq, folder);
    }

    private bool EnsureLoader(out string why)
    {
        why = "";
        if (_loaderLoaded) return true;
        string path = Path.Combine(_env.RuntimeDir ?? "", "WebView2Loader.dll");
        try
        {
            // By full path first, so the Core wrapper's DllImport("WebView2Loader.dll") finds the loaded module.
            NativeLibrary.Load(path);
            _loaderLoaded = true;
            return true;
        }
        catch (Exception ex)
        {
            why = "WebView2Loader.dll did not load from the runtime folder: " + ex.GetType().Name;
            return false;
        }
    }

    private void CreateEnvironment(int seq, string folder)
    {
        if (seq != _createSeq || _tornDown) return;
        Task<CoreWebView2Environment> t;
        try
        {
            Directory.CreateDirectory(folder);
            t = CoreWebView2Environment.CreateAsync(_env.BrowserExecutableFolderForTests, folder, new CoreWebView2EnvironmentOptions());
        }
        catch (Exception ex)
        {
            CreationFailed(seq, WebPanelCode.CreateFailed, "environment: " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8"));
            return;
        }
        t.ContinueWith(done => Post(() => OnEnvironment(seq, done)), TaskContinuationOptions.ExecuteSynchronously);
    }

    private void OnEnvironment(int seq, Task<CoreWebView2Environment> t)
    {
        if (seq != _createSeq || _tornDown) return;
        if (!t.IsCompletedSuccessfully)
        {
            Exception? ex = t.Exception?.GetBaseException();
            CreationFailed(seq, WebPanelCode.CreateFailed, "environment: " + (ex?.GetType().Name ?? "cancelled") + " 0x" + (ex?.HResult ?? 0).ToString("X8"));
            return;
        }
        _wvEnv = t.Result;
        _environmentCount++;
        Task<CoreWebView2Controller> c;
        try { c = _wvEnv.CreateCoreWebView2ControllerAsync(_staHwnd); }
        catch (Exception ex)
        {
            CreationFailed(seq, WebPanelCode.CreateFailed, "controller: " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8"));
            return;
        }
        c.ContinueWith(done => Post(() => OnController(seq, done)), TaskContinuationOptions.ExecuteSynchronously);
    }

    private void OnController(int seq, Task<CoreWebView2Controller> t)
    {
        if (seq != _createSeq || _tornDown)
        {
            if (t.IsCompletedSuccessfully) { try { t.Result.Close(); } catch { } }
            return;
        }
        if (!t.IsCompletedSuccessfully)
        {
            Exception? ex = t.Exception?.GetBaseException();
            CreationFailed(seq, WebPanelCode.CreateFailed, "controller: " + (ex?.GetType().Name ?? "cancelled") + " 0x" + (ex?.HResult ?? 0).ToString("X8"));
            return;
        }
        _creating = false;
        _ctl = t.Result;
        _web = _ctl.CoreWebView2;
        _ctl.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 32, 32, 36);

        CoreWebView2Settings s = _web.Settings;
        s.AreDevToolsEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.IsStatusBarEnabled = false;
        s.AreHostObjectsAllowed = false;
        s.IsWebMessageEnabled = false;   // phase 2 turns it on per open
        s.IsPasswordAutosaveEnabled = false;

        _web.NavigationStarting += OnNavigationStarting;
        _web.NavigationCompleted += OnNavigationCompleted;
        _web.NewWindowRequested += OnNewWindowRequested;
        _web.PermissionRequested += OnPermissionRequested;
        _web.ProcessFailed += OnProcessFailed;
        _web.LaunchingExternalUriScheme += OnLaunchingExternalUriScheme;
        _ctl.AcceleratorKeyPressed += OnAcceleratorKeyPressed;
        _browserPid = _web.BrowserProcessId;

        _env.ConfigureForTests?.Invoke(_web);
        ResizeController();
        if (_open)
        {
            _ctl.IsVisible = true;
            NavigatePending(_serving);
            if (ForegroundIsThisProcess()) FocusPage(); // the rule Show uses: never focus from another program
        }
        else
        {
            _ctl.IsVisible = false;
        }
    }

    private void CreationFailed(int seq, WebPanelCode code, string reason)
    {
        if (seq != _createSeq) return;
        _creating = false;
        _wvEnv = null;
        _profileFolder = null; // the next Open tries again
        Fail(_serving, code, reason, allowFallback: true);
    }

    /// <summary>Only the runtime probe can say RuntimeMissing; a failure after it is CreateFailed.</summary>
    private static WebPanelCode Classify(Exception? ex) =>
        ex is WebView2RuntimeNotFoundException ? WebPanelCode.RuntimeMissing : WebPanelCode.CreateFailed;

    private void NavigatePending(int handle)
    {
        if (_web == null || !IsLive(handle)) return;
        string url = _pendingUrl;
        SetState(handle, () => _status = (int)WebPanelStatusCode.Loading);
        try { _web.Navigate(url); }
        catch (Exception ex) { SafeLog("navigate threw " + ex.GetType().Name + " for " + WebPanelUrlPolicy.Redact(url)); }
    }

    private void NavigateNow(int handle, string url)
    {
        if (!IsLive(handle) || _serving != handle) return;
        _pendingUrl = url;
        if (_web == null) return; // still creating: the controller navigates to _pendingUrl when it is ready
        NavigatePending(handle);
    }

    // ---- navigation guard ----

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        try
        {
            string url = e.Uri;
            if (string.Equals(url, "about:blank", StringComparison.OrdinalIgnoreCase)) return; // our own close, or the page's
            string[] allow = _req?.Allowlist ?? Array.Empty<string>();
            if (!WebPanelUrlPolicy.IsAllowed(url, allow))
            {
                e.Cancel = true;
                RouteOffList(url, e.IsUserInitiated);
                return;
            }
            int handle = _serving;
            SetState(handle, () =>
            {
                _navCount++;
                if (_status != (int)WebPanelStatusCode.Closed && _status != (int)WebPanelStatusCode.Failed) _status = (int)WebPanelStatusCode.Loading;
            });
        }
        catch
        {
            // Fail closed: a guard that threw must not let the navigation through.
            try { e.Cancel = true; } catch { }
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _unresponsiveCount = 0; // a completed navigation means the renderer answered
        string source = _web?.Source ?? "";
        if (string.Equals(source, "about:blank", StringComparison.OrdinalIgnoreCase) || !_open) return;
        _source = source;
        int http = e.HttpStatusCode;
        int handle = _serving;
        SetState(handle, () =>
        {
            if (_status == (int)WebPanelStatusCode.Closed || _status == (int)WebPanelStatusCode.Failed) return;
            _status = (int)WebPanelStatusCode.Ready;
            if (http != 0) _http = http;
        });
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        try
        {
            e.Handled = true; // never a second window
            string url = e.Uri;
            string[] allow = _req?.Allowlist ?? Array.Empty<string>();
            if (WebPanelUrlPolicy.IsAllowed(url, allow))
            {
                try { _web?.Navigate(url); } catch { }
            }
            else
            {
                RouteOffList(url, e.IsUserInitiated);
            }
        }
        catch
        {
            // Fail closed: still no second window.
            try { e.Handled = true; } catch { }
        }
    }

    /// <summary>
    /// An off-list URL: the system browser when it is browser-safe, came from a click (or a plugin) and
    /// isn't too soon after the last (WebPanelBrowserGate); else dropped (logged redacted).
    /// </summary>
    private void RouteOffList(string url, bool userInitiated)
    {
        if (WebPanelUrlPolicy.IsBrowserSafe(url))
        {
            BrowserOpenVerdict verdict = _browserGate.Decide(userInitiated, Environment.TickCount64);
            if (verdict != BrowserOpenVerdict.Open)
            {
                SafeLog((verdict == BrowserOpenVerdict.NotUserInitiated
                    ? "kept an off-list page out of the system browser (not from a click): "
                    : "kept an off-list page out of the system browser (too many, too fast): ") + WebPanelUrlPolicy.Redact(url));
                return;
            }
            bool ok = false;
            try { ok = _env.OpenInBrowser(url); } catch { }
            string host = Uri.TryCreate(url, UriKind.Absolute, out Uri? u) ? u.Host : "the page";
            SafeLog((ok ? "opened in the system browser: " : "the system browser did not open: ") + WebPanelUrlPolicy.Redact(url));
            if (ok) SafeChat("[RynthCore] Opened " + host + " in your web browser.");
        }
        else
        {
            SafeLog("blocked a navigation (" + SchemeOf(url) + "): " + WebPanelUrlPolicy.Redact(url));
        }
    }

    private static string SchemeOf(string url)
    {
        int colon = url.IndexOf(':');
        if (colon <= 0 || colon > 32) return "no scheme";
        foreach (char c in url.AsSpan(0, colon))
            if (!char.IsAsciiLetterOrDigit(c) && c != '+' && c != '-' && c != '.') return "no scheme";
        return url.Substring(0, colon).ToLowerInvariant();
    }

    private void OnLaunchingExternalUriScheme(object? sender, CoreWebView2LaunchingExternalUriSchemeEventArgs e)
    {
        e.Cancel = true; // never hand a custom scheme to the shell, and never show WebView2's prompt for one
        SafeLog("blocked an external scheme launch (" + SchemeOf(e.Uri) + ")");
    }

    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        // ClipboardRead only for a frame on the allowlist (iframes are unrestricted otherwise); everything else denied.
        CoreWebView2PermissionState state = CoreWebView2PermissionState.Deny;
        try
        {
            // IsAllowed accepts about:blank (for our own close); a blank frame gets no clipboard.
            if (e.PermissionKind == CoreWebView2PermissionKind.ClipboardRead
                && !string.Equals(e.Uri, "about:blank", StringComparison.OrdinalIgnoreCase)
                && WebPanelUrlPolicy.IsAllowed(e.Uri, _req?.Allowlist ?? Array.Empty<string>()))
                state = CoreWebView2PermissionState.Allow;
        }
        catch { state = CoreWebView2PermissionState.Deny; }
        // Never remember a decision in the profile: an Allow must not outlive an allowlist change.
        try { e.SavesInProfile = false; } catch { }
        e.State = state;
    }

    private void OnAcceleratorKeyPressed(object? sender, CoreWebView2AcceleratorKeyPressedEventArgs e)
    {
        _acceleratorEvents++;
        if (e.KeyEventKind != CoreWebView2KeyEventKind.KeyDown || e.VirtualKey != VK_ESCAPE) return;
        if (((_req?.Flags ?? 0) & FlagPageKeepsEscape) != 0) return; // the page gets it
        e.Handled = true;
        int handle = _serving;
        Post(() => CloseNow(handle)); // not inside WebView2's own callback
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        CoreWebView2ProcessFailedKind kind = e.ProcessFailedKind;
        Post(() => HandleProcessFailed(kind)); // act outside WebView2's own callback
    }

    // RenderProcessUnresponsive: WebView2 raises it repeatedly while the main frame's renderer stays hung. One
    // report can be a long script; this many within the window fails the panel like a renderer exit.
    private const int UnresponsiveLimit = 3;
    private const long UnresponsiveWindowMs = 30000;
    private int _unresponsiveCount;
    private long _unresponsiveSinceMs;

    /// <summary>
    /// Only two kinds end the panel (WebView2's own docs, Microsoft.Web.WebView2.Core.xml,
    /// CoreWebView2ProcessFailedKind): BrowserProcessExited (everything is gone: full teardown) and
    /// RenderProcessExited (the main frame's renderer: about:blank, recreate on the next Open). GPU, utility,
    /// sandbox helper and PPAPI processes are recreated by WebView2 and are not fatal; a frame-only renderer exit
    /// does not touch the top-level document; those are logged only. Acts on the panel being served now, not on
    /// the handle when the event fired, so a late event never hides a newer panel (Fail ignores a non-live handle).
    /// </summary>
    private void HandleProcessFailed(CoreWebView2ProcessFailedKind kind)
    {
        if (_tornDown) return;
        int handle = _serving;
        switch (kind)
        {
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                SafeLog("WebView2 process failed: " + kind);
                TearDownWebView();
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
                SafeLog("WebView2 process failed: " + kind);
                try { _web?.Navigate("about:blank"); } catch { }
                _needsRecreate = true; // the next Open recreates the controller and environment
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
            {
                long now = Environment.TickCount64;
                if (_unresponsiveCount == 0 || now - _unresponsiveSinceMs > UnresponsiveWindowMs) { _unresponsiveCount = 0; _unresponsiveSinceMs = now; }
                _unresponsiveCount++;
                SafeLog($"WebView2 renderer unresponsive ({_unresponsiveCount} of {UnresponsiveLimit} within {UnresponsiveWindowMs / 1000} s)");
                if (_unresponsiveCount < UnresponsiveLimit) return;
                _unresponsiveCount = 0;
                try { _web?.Navigate("about:blank"); } catch { }
                _needsRecreate = true;
                break;
            }
            default:
                SafeLog("WebView2 process failed (not fatal, panel kept): " + kind);
                return;
        }
        // Never a browser fallback here: a navigation already ran, so the launch code is spent.
        Fail(handle, WebPanelCode.ProcessFailed, kind.ToString(), allowFallback: false);
    }

    // ---- open, close, fail ----

    private void Fail(int handle, WebPanelCode code, string reason, bool allowFallback)
    {
        bool fallback = false, ignored = false;
        string url = "";
        lock (_sync)
        {
            if (handle != _live || _status == (int)WebPanelStatusCode.Closed)
            {
                ignored = true;
            }
            else
            {
                WebPanelRequest? r = _req;
                if (allowFallback && r != null && (code == WebPanelCode.RuntimeMissing || code == WebPanelCode.CreateFailed)
                    && (r.Flags & FlagFallback) != 0 && _navCount == 0 && WebPanelUrlPolicy.IsBrowserSafe(r.Url))
                {
                    // Failed is published only together with the fallback's outcome (below): a plugin polling in
                    // between must never see Failed without OpenedInBrowser and open the page a second time.
                    fallback = true;
                    url = r.Url;
                }
                else
                {
                    _status = (int)WebPanelStatusCode.Failed;
                    _error = (int)code;
                }
            }
        }
        if (ignored) { SafeLog($"panel failure after close or supersede, panel left as it is: {code} ({reason})"); return; }
        HidePanel();
        SafeLog($"panel failed: {code} ({reason})");
        if (!fallback) return;
        bool ok = false;
        try { ok = _env.OpenInBrowser(url); } catch { }
        // One lock: Failed, the error and (only when the browser opened) the flag become visible together, and only
        // for the panel that failed; a Close or a newer Open in the meantime wins.
        lock (_sync)
        {
            if (handle == _live && _status != (int)WebPanelStatusCode.Closed)
            {
                _status = (int)WebPanelStatusCode.Failed;
                _error = (int)code;
                if (ok) _flags |= StateOpenedInBrowser;
            }
        }
        SafeLog((ok ? "fallback: opened in the system browser: " : "fallback: the system browser did not open: ") + WebPanelUrlPolicy.Redact(url));
        string why = code == WebPanelCode.RuntimeMissing ? "WebView2 runtime not installed" : "it failed to start";
        SafeChat(ok
            ? "[RynthCore] The in-game browser could not start (" + why + "); opened the page in your web browser."
            : "[RynthCore] The in-game browser could not start (" + why + "), and your web browser did not open.");
    }

    private void Show()
    {
        PlaceNow(force: true);
        ShowWindow(_staHwnd, SW_SHOWNA);
        // Focus the panel only from within this process (the game, or a pop-out): a plugin opening it while the
        // player is in another program must not pull the foreground away (the PID rule keeps it hidden meanwhile).
        // Never while the foreground is null (mid alt-tab): that would grab focus on the way to another program.
        // Only this process, defensively: a foreground in the WebView2 browser process is assumed (not observed) to
        // be the page's own dialog, which should keep it (IsOurPid, which includes the browser, is for the
        // visibility rule only).
        bool inProcess = ForegroundIsThisProcess();
        if (inProcess) SetForegroundWindow(_staHwnd);
        if (_ctl != null) { try { _ctl.IsVisible = true; } catch { } }
        if (inProcess) FocusPage(); // same rule, defensively: moving WebView2 focus might activate the host window (not observed)
        if (!_timerOn) { SetTimer(_staHwnd, TimerId, 50, IntPtr.Zero); _timerOn = true; }
        UpdateStateFlags();
    }

    /// <summary>The foreground is a window of this process (not null, not another program, not the browser process).</summary>
    private bool ForegroundIsThisProcess()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        GetWindowThreadProcessId(fg, out uint fgPid);
        return fgPid == _ourPid;
    }

    private void FocusPage()
    {
        try { _ctl?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic); } catch { }
    }

    private void CloseNow(int handle)
    {
        if (!IsLive(handle) || handle != _serving) return;
        if (_open)
        {
            HidePanel();
            if (_ctl != null) { try { _web?.Navigate("about:blank"); } catch { } }
        }
        SetState(handle, () =>
        {
            if (_status != (int)WebPanelStatusCode.Failed) _status = (int)WebPanelStatusCode.Closed;
            _flags &= ~(StateVisible | StateFocused);
        });
    }

    /// <summary>Hides the panel (controller, window, timer); hands the foreground back to the game when the panel held it.</summary>
    private void HidePanel()
    {
        bool hadForeground = HoldsForeground();
        _open = false;
        if (_timerOn) { KillTimer(_staHwnd, TimerId); _timerOn = false; }
        if (_ctl != null) { try { _ctl.IsVisible = false; } catch { } }
        if (_staHwnd != IntPtr.Zero) ShowWindow(_staHwnd, SW_HIDE);
        lock (_sync) if (_serving == _live) _flags &= ~(StateVisible | StateFocused);
        if (hadForeground)
        {
            IntPtr game = _env.GameHwnd;
            if (game != IntPtr.Zero && IsWindow(game)) SetForegroundWindow(game);
        }
    }

    private bool HoldsForeground()
    {
        if (_staHwnd == IntPtr.Zero) return false;
        IntPtr fg = GetForegroundWindow();
        return fg != IntPtr.Zero && (fg == _staHwnd || GetAncestor(fg, GA_ROOT) == _staHwnd);
    }

    // ---- tracking: 50 ms timer and the foreground WinEvent ----

    private void Track()
    {
        if (!_open || _staHwnd == IntPtr.Zero) return;
        IntPtr game = _env.GameHwnd;
        if (game == IntPtr.Zero || !IsWindow(game)) { CloseNow(_serving); return; }
        IntPtr fg = GetForegroundWindow();
        uint fgPid = 0;
        if (fg != IntPtr.Zero) GetWindowThreadProcessId(fg, out fgPid);
        // The page's own dialogs (alert, file picker, sign-in) are windows of the WebView2 browser process: count
        // that process as ours so they do not hide the panel they belong to.
        if (IsOurPid(fgPid)) fgPid = _ourPid;
        bool? visible = WebPanelWindowPolicy.Visible(true, true, IsIconic(game), IsWindowVisible(game), fg == IntPtr.Zero, fgPid, _ourPid);
        bool shown = IsWindowVisible(_staHwnd);
        if (visible == false)
        {
            if (shown) ShowWindow(_staHwnd, SW_HIDE);
        }
        else
        {
            if (visible == true && !shown) { ShowWindow(_staHwnd, SW_SHOWNA); shown = true; }
            if (shown)
            {
                if (!_inSizeMove) PlaceNow(force: false);
                RaiseAboveGame(game);
            }
        }
        UpdateStateFlags();
    }

    /// <summary>
    /// This process, or this panel's WebView2 browser process. The browser PID is CoreWebView2.BrowserProcessId,
    /// read when the controller is created (OnController) and cleared by TearDownWebView; 0 while there is none.
    /// </summary>
    private bool IsOurPid(uint pid)
    {
        if (pid == 0) return false;
        uint browser = _browserPid;
        return pid == _ourPid || (browser != 0 && pid == browser);
    }

    private bool GameClientScreen(out PanelRect rect)
    {
        rect = default;
        IntPtr game = _env.GameHwnd;
        if (game == IntPtr.Zero || !GetClientRect(game, out RECT c)) return false;
        var p = new POINT();
        if (!ClientToScreen(game, ref p)) return false;
        rect = new PanelRect(p.X, p.Y, p.X + c.W, p.Y + c.H);
        return rect.Width > 0 && rect.Height > 0;
    }

    private void PlaceNow(bool force)
    {
        if (!GameClientScreen(out PanelRect client)) return;
        PanelRect want = WebPanelWindowPolicy.Place(client, _fractions);
        GetWindowRect(_staHwnd, out RECT have);
        if (!force && have.Left == want.Left && have.Top == want.Top && have.Right == want.Right && have.Bottom == want.Bottom) return;
        SetWindowPos(_staHwnd, IntPtr.Zero, want.Left, want.Top, want.Width, want.Height, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>Raises the panel only when it is below the game window, so it never covers a window the user brought forward.</summary>
    private void RaiseAboveGame(IntPtr game)
    {
        int guard = 0;
        for (IntPtr h = GetWindow(game, GW_HWNDPREV); h != IntPtr.Zero && guard < 4096; h = GetWindow(h, GW_HWNDPREV), guard++)
            if (h == _staHwnd) return;
        SetWindowPos(_staHwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private void UpdateStateFlags()
    {
        bool visible = _staHwnd != IntPtr.Zero && IsWindowVisible(_staHwnd);
        bool focused = HoldsForeground();
        lock (_sync)
        {
            if (_serving != _live) return;
            _flags = (_flags & StateOpenedInBrowser) | (visible && _open ? StateVisible : 0) | (focused && _open ? StateFocused : 0);
        }
    }

    private void RememberUserLayout()
    {
        if (!GameClientScreen(out PanelRect client)) return;
        GetWindowRect(_staHwnd, out RECT r);
        _fractions = WebPanelWindowPolicy.FromUserRect(client, new PanelRect(r.Left, r.Top, r.Right, r.Bottom));
        if (!WebPanelLayoutStore.Save(_env.LocalAppData, _fractions)) SafeLog("could not save the panel layout");
    }

    private void ResizeController()
    {
        if (_ctl == null || _staHwnd == IntPtr.Zero) return;
        GetClientRect(_staHwnd, out RECT rc);
        try { _ctl.Bounds = new System.Drawing.Rectangle(0, CaptionPx, Math.Max(0, rc.W), Math.Max(0, rc.H - CaptionPx)); } catch { }
    }

    // ---- teardown ----

    /// <summary>Drops the controller and environment (profile switch, browser exit, shutdown). Pending creations become stale.</summary>
    private void TearDownWebView()
    {
        _createSeq++;
        _creating = false;
        _needsRecreate = false;
        _unresponsiveCount = 0;
        CoreWebView2? web = _web;
        CoreWebView2Controller? ctl = _ctl;
        _web = null;
        _ctl = null;
        _wvEnv = null;
        _profileFolder = null;
        _browserPid = 0;
        if (web != null)
        {
            try
            {
                web.NavigationStarting -= OnNavigationStarting;
                web.NavigationCompleted -= OnNavigationCompleted;
                web.NewWindowRequested -= OnNewWindowRequested;
                web.PermissionRequested -= OnPermissionRequested;
                web.ProcessFailed -= OnProcessFailed;
                web.LaunchingExternalUriScheme -= OnLaunchingExternalUriScheme;
            }
            catch { }
        }
        if (ctl != null)
        {
            try { ctl.AcceleratorKeyPressed -= OnAcceleratorKeyPressed; } catch { }
            try { ctl.Close(); } catch { }
        }
    }

    private void TeardownOnPanelThread()
    {
        if (_tornDown) return;
        _tornDown = true;
        _open = false;
        if (_hook != IntPtr.Zero) { UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
        if (_timerOn && _staHwnd != IntPtr.Zero) { KillTimer(_staHwnd, TimerId); _timerOn = false; }
        TearDownWebView();
        if (_staHwnd != IntPtr.Zero) { DestroyWindow(_staHwnd); _staHwnd = IntPtr.Zero; }
        lock (_sync) _hwnd = IntPtr.Zero;
        if (_className.Length != 0) { UnregisterClassW(_className, GetModuleHandleW(IntPtr.Zero)); _className = ""; }
        if (_bgBrush != IntPtr.Zero) { DeleteObject(_bgBrush); _bgBrush = IntPtr.Zero; }
        if (_captionBrush != IntPtr.Zero) { DeleteObject(_captionBrush); _captionBrush = IntPtr.Zero; }
        if (s_active == this) s_active = null;
        _req = null;
        PostQuitMessage(0);
    }
}
