// ═══════════════════════════════════════════════════════════════════════════
//  RynthCore.Engine — ImGui/Win32Backend.cs
//  Subclasses the game's WndProc to forward mouse/keyboard input to ImGui.
//  Implements the equivalent of imgui_impl_win32.cpp in pure C#.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using ImGuiNET;
using RynthCore.Engine.D3D9;
using RynthCore.Engine.UI;

namespace RynthCore.Engine.ImGuiBackend;

internal static unsafe class Win32Backend
{
    // ─── Win32 messages ───────────────────────────────────────────────
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_NCMOUSEMOVE = 0x00A0;
    private const uint WM_SETFOCUS = 0x0007;
    private const uint WM_KILLFOCUS = 0x0008;
    private const uint WM_ACTIVATE    = 0x0006;
    private const uint WM_ACTIVATEAPP = 0x001C;
    private const int  WA_INACTIVE    = 0;
    private const int  WA_ACTIVE      = 1;
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
    private const uint WM_CLOSE = 0x0010;

    // Set on the first WM_CLOSE so we kick off the engine teardown exactly
    // once even if AC reposts WM_CLOSE during its shutdown sequence.
    private static int _wmCloseSeen;

    private const int GWL_WNDPROC = -4;
    private const uint GA_ROOT = 2;
    private const int HTCLIENT = 1;

    /// <summary>Posted by AvaloniaSubclassWndProc when Avalonia acquires focus.
    /// Handled here (game thread) so SetFocus is always same-thread — no cross-thread wait.</summary>
    public const uint WM_RYNTH_RESTORE_FOCUS = 0x8001;

    /// <summary>Sent by RunOnGameThread to execute a queued Action on the game's
    /// main thread (where AC's WndProc dispatches). Used to create top-level HWNDs
    /// like floating panels' LayeredWindow on AC's thread instead of Avalonia's UI
    /// thread, so WS_EX_NOACTIVATE + mouse delivery work as documented (cross-thread
    /// HWNDs drop WM_LBUTTONDOWN on Win11 in some focus states).</summary>
    public const uint WM_RYNTH_RUN_ACTION = 0x8002;

    private const int VK_CONTROL = 0x11;
    private const int VK_SHIFT = 0x10;
    private const int VK_MENU = 0x12;  // Alt
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_INSERT = 0x2D;
    private const int VK_RETURN = 0x0D;
    private const int VK_BACK   = 0x08;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_LEFT   = 0x25;
    private const int VK_UP     = 0x26;
    private const int VK_RIGHT  = 0x27;
    private const int VK_DOWN   = 0x28;
    private const int VK_HOME   = 0x24;
    private const int VK_END    = 0x23;
    private const int VK_DELETE = 0x2E;
    // lParam bit 24: extended key flag. Set for numpad Enter, right-side Ctrl/Alt.
    // Used to distinguish numpad Enter (extended) from the main keyboard Enter (not extended).
    private static bool IsExtendedKey(IntPtr lParam) => ((int)(long)lParam & 0x01000000) != 0;

    // ─── Win32 P/Invoke ───────────────────────────────────────────────
    // On x86, SetWindowLongPtr doesn't exist — use SetWindowLong
    [DllImport("user32.dll", EntryPoint = "SetWindowLongA", SetLastError = true)]
    private static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongA", SetLastError = true)]
    private static extern IntPtr GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcA")]
    private static extern IntPtr CallWindowProcA(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

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
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    private static string DescribeHwnd(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "null";
        var cls = new System.Text.StringBuilder(128);
        var txt = new System.Text.StringBuilder(128);
        GetClassNameW(hwnd, cls, cls.Capacity);
        GetWindowTextW(hwnd, txt, txt.Capacity);
        return $"class='{cls}' title='{txt}'";
    }

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
    /// Populate <see cref="GameHwnd"/> from outside the ImGui Init path.
    /// Used by Avalonia-only overlay mode where ImGui is disabled but Avalonia
    /// panels still need the game HWND for owner-window binding. Idempotent —
    /// the WndProc subclass that <see cref="Init"/> installs is *not* set up by
    /// this call, so don't use this if ImGui is going to init too.
    /// </summary>
    public static void SetGameHwndExternal(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero && _gameHwnd == IntPtr.Zero)
            _gameHwnd = hwnd;
    }

    /// <summary>
    /// Sends a message directly to the game's original WndProc, bypassing our subclass.
    /// </summary>
    public static IntPtr SendToGameWndProc(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (_originalWndProc == IntPtr.Zero || _gameHwnd == IntPtr.Zero)
            return IntPtr.Zero;
        return CallWindowProcA(_originalWndProc, _gameHwnd, msg, wParam, lParam);
    }

    private static readonly object _gameThreadLock = new();
    private static volatile Action? _pendingGameThreadAction;

    /// <summary>
    /// Runs <paramref name="action"/> synchronously on AC's main thread (the one
    /// that dispatches the hooked game WndProc), and returns its result. Used so
    /// HWNDs that must dispatch input on the game thread — most notably the
    /// floating-panel LayeredWindows — can be created from any thread (e.g.
    /// Avalonia's UI thread) without ending up cross-thread to AC. Without
    /// same-thread ownership, Win11's WS_EX_NOACTIVATE silently drops
    /// WM_LBUTTONDOWN in some focus states (the docked panels work because they
    /// already live on the game thread via the EndScene-driven path).
    ///
    /// Implementation: a single shared action slot serialized by a lock; the
    /// action is delivered via SendMessage(WM_RYNTH_RUN_ACTION) which blocks
    /// until the game thread's WndProc has handled it. Game-thread callers
    /// bypass the marshal and invoke inline.
    /// </summary>
    public static T RunOnGameThread<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_gameHwnd == IntPtr.Zero)
            throw new InvalidOperationException("RunOnGameThread called before Win32Backend.Init.");

        GetWindowThreadProcessId(_gameHwnd, out uint gameThreadId);
        if (gameThreadId == GetCurrentThreadId())
            return action();

        lock (_gameThreadLock)
        {
            T result = default!;
            Exception? caught = null;
            _pendingGameThreadAction = () =>
            {
                try { result = action(); }
                catch (Exception ex) { caught = ex; }
            };
            SendMessage(_gameHwnd, WM_RYNTH_RUN_ACTION, IntPtr.Zero, IntPtr.Zero);
            _pendingGameThreadAction = null;
            if (caught != null)
                throw caught;
            return result;
        }
    }

    /// <summary>Void overload of <see cref="RunOnGameThread{T}"/>.</summary>
    public static void RunOnGameThread(Action action)
    {
        RunOnGameThread<bool>(() => { action(); return true; });
    }

    /// <summary>Deactivates chat capture.  The game HWND retains focus throughout so no
    /// Win32 focus transfer is needed.</summary>
    public static void ReturnFocusToGame() => ChatCaptureActive = false;
    /// <summary>Called by AvaloniaSubclassWndProc when WM_LBUTTONUP arrives via the
    /// SetCapture route so _avPanelMouseDown doesn't get stuck true.</summary>
    internal static void ClearPanelMouseDown() => _avPanelMouseDown = false;
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
    /// <summary>While true, WM_CHAR / special keys are consumed for the chat input TextBox
    /// instead of passing to the game.  Focus stays on the game HWND.</summary>
    public static volatile bool ChatCaptureActive;

    /// <summary>While true, an Avalonia panel TextBox currently has keyboard focus and
    /// the WM_SETFOCUS hijack in <see cref="UI.AvaloniaOverlay"/>'s subclass WndProc
    /// must NOT immediately PostMessage focus back to the game window — doing so
    /// yanks focus off the TextBox ~50 ms after the user clicks it, making it
    /// impossible to type. Set true on Avalonia TextBox.GotFocus, false on
    /// LostFocus. (ChatCaptureActive uses a completely different pipeline:
    /// the game HWND keeps focus and WM_CHAR is routed to the chat callbacks,
    /// so it doesn't help for normal panels that genuinely need Avalonia focus.)</summary>
    public static volatile bool AvaloniaTextInputActive;

    /// <summary>Set when we consume the VK_RETURN/VK_ESCAPE that ends chat capture, so the
    /// trailing WM_CHAR ('\r') + WM_KEYUP of that same keystroke are eaten too and never
    /// leak to the game (a lone '\r' can spuriously re-open AC's native chat bar).</summary>
    private static bool _swallowEnterTail;

    // ── Chat input callbacks (set by RynthChatPanel) ─────────────────────
    /// <summary>Fired when Enter in-game activates chat (UI thread dispatch optional).</summary>
    public static Action? OnChatCaptureActivated;
    /// <summary>Fired for each printable character (≥ 0x20) while capture is active.</summary>
    public static Action<char>? OnChatChar;
    /// <summary>Fired on VK_BACK while capture is active.</summary>
    public static Action? OnChatBackspace;
    /// <summary>Fired on VK_DELETE while capture is active.</summary>
    public static Action? OnChatDelete;
    /// <summary>Fired on VK_RETURN while capture is active (ChatCaptureActive already false).</summary>
    public static Action? OnChatSend;
    /// <summary>Fired on VK_ESCAPE while capture is active (ChatCaptureActive already false).</summary>
    public static Action? OnChatCancel;
    /// <summary>Fired on VK_LEFT while capture is active.</summary>
    public static Action? OnChatLeft;
    /// <summary>Fired on VK_RIGHT while capture is active.</summary>
    public static Action? OnChatRight;
    /// <summary>Fired on VK_HOME while capture is active.</summary>
    public static Action? OnChatHome;
    /// <summary>Fired on VK_END while capture is active.</summary>
    public static Action? OnChatEnd;
    /// <summary>Fired on VK_UP while capture is active (history previous).</summary>
    public static Action? OnChatUp;
    /// <summary>Fired on VK_DOWN while capture is active (history next).</summary>
    public static Action? OnChatDown;
    private static int  _lastPanelClientX;     // last game-client pos over the panel (for wheel)
    private static int  _lastPanelClientY;

    // Panel drag / resize tracked entirely in physical coords here —
    // avoids any dependency on Avalonia's off-screen coordinate system.
    private static bool _avIsDragging;
    private static bool _avIsResizing;
    private static bool _avIsButtonCapture;
    private static bool _avHasNativeCapture;
    // Set when a left-button press lands on an opened Avalonia panel (not the
    // bar). While set, every mouse event is forwarded to Avalonia regardless
    // of whether the cursor is still over the panel rect — without this,
    // a fast drag where the cursor outpaces the panel-position update lands
    // outside IsOverPanel and the drag drops.
    private static bool _avPanelMouseDown;
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
        _forwardOnly = false;   // re-arming in this generation — stop any pass-through latch
        _gameHwnd = hWnd;
        _uiCaptureEnabled = true;
        _hasFocus = false;
        _focusInitialized = false;
        _wantCaptureMouse = false;
        _wantCaptureKeyboard = false;
        _insertWasDown = false;
        _swallowEnterTail = false;

        // Subclass the window
        _wndProcDelegate = WndProcHook;
        IntPtr hookPtr = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
        _installedWndProcPtr = hookPtr;
        _originalWndProc = SetWindowLong32(hWnd, GWL_WNDPROC, hookPtr);
        // RynthLog.UI (not .Render — RenderEnabled is false) so chain
        // composition across generations is reconstructible from the log.
        RynthLog.UI($"Win32Backend: subclass installed — hook=0x{hookPtr:X8}, previous WndProc=0x{_originalWndProc:X8}.");

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

    private static IntPtr _installedWndProcPtr;
    private static volatile bool _forwardOnly;

    public static void Shutdown()
    {
        if (!_initialized) return;

        // Restore the original WndProc ONLY if we are still the head of the
        // chain. If something subclassed on top of us after Init (Decal /
        // DINPUT8 re-hooks, a newer engine generation), a blind restore RIPS
        // their hook out of the chain — DirectInput's hook proc then
        // dereferences a freed per-window record on AC's main thread and the
        // render pump never recovers (the 2026-06-11 DINPUT8+0x20CCC reload
        // wedge). Leaving the chain intact is always safe: our thunk stays
        // valid because engine module pages are intentionally leaked across
        // reloads, and a no-longer-initialized backend just forwards.
        IntPtr currentProc = GetWindowLong32(_gameHwnd, GWL_WNDPROC);
        if (currentProc == _installedWndProcPtr || currentProc == IntPtr.Zero)
        {
            SetWindowLong32(_gameHwnd, GWL_WNDPROC, _originalWndProc);
            RynthLog.UI("Win32Backend: Shutdown — WndProc restored (we were chain head).");
        }
        else
        {
            // Become a pure pass-through instead: a frozen generation that
            // keeps PROCESSING input swallows WM_CHAR and kills AC's chat
            // after a reload (the historic stacking bug the unconditional
            // restore was originally added for). Forward-only keeps the
            // foreign chain intact AND keeps this generation inert.
            _forwardOnly = true;
            // A hit on this line positively confirms a foreign subclasser
            // stacks above us in live sessions — the blind-restore wedge's
            // missing precondition. Must be visible in the log.
            RynthLog.UI($"Win32Backend: Shutdown — chain head is 0x{currentProc:X8}, not ours (0x{_installedWndProcPtr:X8}); leaving the chain intact, hook now forward-only.");
        }
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
        _avPanelMouseDown = false;
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
        // Deferred focus restore: AvaloniaSubclassWndProc sets this flag when
        // WM_SETFOCUS lands on the off-screen Avalonia HWND. We reclaim focus
        // here (game thread owns _gameHwnd) so there's no cross-thread wait.
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
        // DisplaySize will be set from D3D viewport in EngineFrameController instead.
        if (w > 1 && h > 1)
            io.DisplaySize = new System.Numerics.Vector2(w, h);

        FlushQueuedInput(io);

        // Refresh the cursor position. With ViewportsEnable, ImGui expects
        // absolute screen coords so it can route clicks to the correct viewport.
        // Without it, client-relative coords are the simpler contract.
        GetCursorPos(out POINT cursorPos);
        if ((io.ConfigFlags & ImGuiConfigFlags.ViewportsEnable) == 0)
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
        // Post-shutdown pass-through: Shutdown could not restore the chain
        // (someone subclassed after us) — this generation must be inert.
        if (_forwardOnly)
            return CallWindowProcA(_originalWndProc, hWnd, msg, wParam, lParam);

        try
        {
            _wndProcLogCount++;

            // ── Game-thread executor: run a queued Action on this thread ──
            // Sent by RunOnGameThread via SendMessage (synchronous, blocks the
            // caller until this returns). Keep this near the top — must dispatch
            // before any chat / focus / mouse paths so callers waiting on a
            // CreateWindowExW etc. aren't stuck behind unrelated input handling.
            if (msg == WM_RYNTH_RUN_ACTION)
            {
                Action? a = _pendingGameThreadAction;
                if (a != null)
                {
                    try { a(); }
                    catch (Exception ex) { RynthLog.Info($"Win32Backend: WM_RYNTH_RUN_ACTION threw {ex.GetType().Name}: {ex.Message}"); }
                }
                return IntPtr.Zero;
            }

            // ── User clicked X / Alt+F4 on AC window — tear our overlay down NOW ──
            // AC's own shutdown takes 20-30 s (network logout, save, etc.) before
            // it finally calls ExitProcess and triggers ProcessExitHooks. During
            // that gap EndScene keeps firing and the floating LayeredWindows
            // (RynthAi panel etc.) stay visible on the OS desktop, which looks
            // like the overlay is "stuck up" long after the close click. Running
            // EngineLifecycle.Shutdown here uninstalls the EndScene hook and
            // closes every floating panel immediately; the later ExitProcess
            // detour is a no-op (interlocked guard inside Shutdown). Run on a
            // background thread so the join on the Avalonia STA doesn't block
            // AC's own message pump during its shutdown.
            if (msg == WM_CLOSE && Interlocked.Exchange(ref _wmCloseSeen, 1) == 0)
            {
                RynthLog.UI("Win32Backend: WM_CLOSE on AC window — quiescing plugin pump, then kicking engine shutdown.");
                // AC is about to free the ClientUISystem singleton — drop the
                // busy watchdog's cached pointer so no force-clear can run
                // against freed memory during the teardown window.
                try { Compatibility.BusyCountHooks.ResetSession(); }
                catch { }
                // Stop the off-thread plugin pump FIRST, synchronously, on AC's main
                // thread — we run here BEFORE falling through to AC's own WndProc (which
                // starts AC's object teardown). The pump's in-flight frame finishes on
                // still-valid AC objects and then it exits; otherwise it keeps ticking
                // into DestroyObjectCaches and races AC's frees -> the recurring on-close
                // AVs (BusyCountHooks.ForceResetBusyCount / PlayerPhysicsHooks.TryGetPlayerPose
                // -> AC, e.g. acclient+0x16547B null+0x1C; EIP->heap). Bounded ~2s inside
                // StopTickPumpAndJoin; the background EngineLifecycle.Shutdown below then
                // finds the pump already stopped (its TickPump.StopAndJoin is a no-op).
                try { EntryPoint.StopTickPumpAndJoin(); }
                catch (Exception ex) { RynthLog.UI($"Win32Backend: WM_CLOSE pump-stop threw {ex.GetType().Name}: {ex.Message}"); }

                new Thread(() =>
                {
                    try { EngineLifecycle.Shutdown(); }
                    catch (Exception ex) { RynthLog.UI($"Win32Backend: WM_CLOSE shutdown threw {ex.GetType().Name}: {ex.Message}"); }
                })
                { Name = "RynthCore.WmCloseShutdown", IsBackground = true }.Start();
                // Fall through to AC's original WndProc so AC starts its own
                // shutdown sequence in parallel with ours.
            }

            // ── Once close is in flight, stop feeding mouse/cursor input to AC ──
            // After WM_CLOSE, AC tears down its world — including the combat-system
            // singleton. AC's ClientUISystem::UpdateCursorState (acclient 0x005653D0)
            // runs on every mouse-move / cursor refresh and dereferences
            // GetCombatSystem()->[+0x1C]; once the singleton is freed that's an AV at
            // acclient 0x0056547B reading [null+0x1C]. The user is actively moving the
            // mouse toward the close control, so a trailing WM_MOUSEMOVE / WM_SETCURSOR
            // lands right after the free. THIS subclass is the one installed on the
            // game window (AvaloniaSubclassWndProc carries a matching block, but it is
            // installed on the off-screen Avalonia HWND and never sees AC's mouse
            // traffic). WM_CLOSE / WM_DESTROY / paint still fall through so the window
            // closes normally.
            if (Volatile.Read(ref _wmCloseSeen) != 0)
            {
                switch (msg)
                {
                    case WM_MOUSEMOVE:
                    case WM_NCMOUSEMOVE:
                    case WM_MOUSEWHEEL:
                    case WM_LBUTTONDOWN:
                    case WM_LBUTTONUP:
                    case WM_RBUTTONDOWN:
                    case WM_RBUTTONUP:
                    case WM_MBUTTONDOWN:
                    case WM_MBUTTONUP:
                        return IntPtr.Zero;
                    case WM_SETCURSOR:
                        return (IntPtr)1; // handled — halt AC's cursor-update chain
                }
            }

            // ── Chat capture: consume all key input for the chat TextBox ────
            // Game HWND keeps Win32 focus throughout; callbacks dispatch Text
            // updates to the panel on Avalonia's UI thread — no Avalonia focus needed.
            if (ChatCaptureActive && IsKeyMessage(msg))
            {
                if (msg == WM_CHAR)
                {
                    int ch = (int)wParam;
                    if (ch >= 0x20)               // printable characters only
                        OnChatChar?.Invoke((char)ch);
                }
                else if (msg == WM_KEYDOWN)
                {
                    int vk = (int)wParam;
                    if      (vk == VK_RETURN && !IsExtendedKey(lParam))  { ChatCaptureActive = false; _swallowEnterTail = true; PostMessage(_gameHwnd, WM_RYNTHCORE_CHAT, IntPtr.Zero, IntPtr.Zero); }
                    else if (vk == VK_ESCAPE)  { ChatCaptureActive = false; _swallowEnterTail = true; OnChatCancel?.Invoke(); }
                    else if (vk == VK_BACK)    { OnChatBackspace?.Invoke(); }
                    else if (vk == VK_DELETE)  { OnChatDelete?.Invoke(); }
                    else if (vk == VK_LEFT)    { OnChatLeft?.Invoke(); }
                    else if (vk == VK_RIGHT)   { OnChatRight?.Invoke(); }
                    else if (vk == VK_HOME)    { OnChatHome?.Invoke(); }
                    else if (vk == VK_END)     { OnChatEnd?.Invoke(); }
                    else if (vk == VK_UP)      { OnChatUp?.Invoke(); }
                    else if (vk == VK_DOWN)    { OnChatDown?.Invoke(); }
                }
                return IntPtr.Zero;   // eat all key messages while chat is active
            }

            // ── Swallow the tail of the send/cancel keystroke ─────────────
            // The block above ends chat capture on VK_RETURN/VK_ESCAPE but eats
            // only the WM_KEYDOWN. The OS still delivers the matching WM_CHAR
            // ('\r') and WM_KEYUP; with ChatCaptureActive now false they'd fall
            // through to the game, and a lone '\r' can spuriously re-open AC's
            // native chat bar (which then sits open, hidden by suppress, eating
            // input until it resets — a multi-second dead window for chat).
            if (_swallowEnterTail)
            {
                if (msg == WM_CHAR)
                    return IntPtr.Zero;
                if ((msg == WM_KEYUP || msg == WM_SYSKEYUP) &&
                    ((int)wParam == VK_RETURN || (int)wParam == VK_ESCAPE) &&
                    !IsExtendedKey(lParam))
                {
                    _swallowEnterTail = false;
                    return IntPtr.Zero;
                }
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                    _swallowEnterTail = false;   // a new keystroke began; stop guarding
            }

            // ── Deferred chat send ────────────────────────────────────────
            // The send Enter (handled above) posts WM_RYNTHCORE_CHAT instead of
            // dispatching inline. We fire OnChatSend here, on a *fresh* game-thread
            // message dispatch — after the Enter's WM_CHAR/WM_KEYUP tail has been
            // consumed and outside the original keystroke's WndProc frame. This
            // matters because OnChatSend → RynthChatSendLine → ChatCommandDispatcher
            // .SimulateChatInput drives CallWindowProcA back into AC's window proc;
            // doing that reentrantly inside the Enter keystroke left AC's chat bar
            // misaligned after the first send ("worked once, then stopped"). The
            // dispatch must stay on the game thread (it owns the window), so a posted
            // message — not a worker-thread tick — is the right deferral.
            if (msg == WM_RYNTHCORE_CHAT)
            {
                OnChatSend?.Invoke();
                return IntPtr.Zero;
            }

            // ── Chat: Enter in-game activates the chat TextBox ───────────
            // Numpad Enter (extended key, lParam bit 24) is reserved for AC functions — never capture it.
            // !AvaloniaTextInputActive: while the user is typing in a docked
            // panel TextBox this branch otherwise runs BEFORE the text-input
            // forwarding block and hijacks Enter into chat capture — stealing
            // the TextBox's own commit handler and every subsequent keystroke.
            if (msg == WM_KEYDOWN && (int)wParam == VK_RETURN && !IsExtendedKey(lParam) && !ChatCaptureActive && !AvaloniaTextInputActive)
            {
                if (OnChatCaptureActivated != null)
                {
                    ChatCaptureActive = true;
                    OnChatCaptureActivated.Invoke();
                    return IntPtr.Zero;
                }
            }

            // ── Restore focus to game when Avalonia acquires it ───────────
            if (msg == WM_RYNTH_RESTORE_FOCUS)
            {
                IntPtr prev = SetFocus(_gameHwnd);
                IntPtr fg   = GetForegroundWindow();
                RynthLog.Info($"Win32Backend: WM_RYNTH_RESTORE_FOCUS → SetFocus(gameHwnd) prev=0x{prev.ToInt64():X} fg=0x{fg.ToInt64():X} game=0x{_gameHwnd.ToInt64():X}.");
                return IntPtr.Zero;
            }

            // ── Diag: game-window focus transitions (driven by clicks on Avalonia panels) ──
            if (msg == WM_SETFOCUS)
                RynthLog.Info($"Win32Backend: game WM_SETFOCUS (from hwnd=0x{wParam.ToInt64():X} {DescribeHwnd(wParam)}).");
            else if (msg == WM_KILLFOCUS)
                RynthLog.Info($"Win32Backend: game WM_KILLFOCUS (to hwnd=0x{wParam.ToInt64():X} {DescribeHwnd(wParam)}).");

            // ── Vital HUD drag/resize ─────────────────────────────────────
            // Give the custom vital-bar HUD first crack at mouse input: a click
            // landing on it moves/resizes the HUD and is swallowed so it never
            // reaches AC (no camera spin) or the Avalonia panels. Placed before
            // the ImGui EnqueueInput/capture-eat below so ImGui never tracks the
            // HUD drag (keeps our SetCapture from fighting NewFrame's). No-op
            // unless the HUD is drawn.
            if (IsMouseMessage(msg) && VitalHud.TryHandleMouse(hWnd, msg, wParam, lParam))
                return IntPtr.Zero;

            // ── Avalonia panel hit-test & input forwarding ────────────────
            if (IsMouseMessage(msg))
            {
                bool handled = TryForwardToAvalonia(msg, wParam, lParam);
                if (handled)
                    return IntPtr.Zero;
            }
            else if (IsKeyMessage(msg) && AvaloniaTextInputActive)
            {
                // Keys go to Avalonia only when a TextBox actually holds keyboard focus.
                // Gating on mouse-hover instead was wrong: a held game key whose KEYUP
                // landed while the cursor was over a panel got swallowed, leaving AC's
                // edge-driven input state latched (character kept moving until the user
                // tapped again with the mouse elsewhere).
                //
                // CRITICAL: forward only WM_KEYDOWN/WM_KEYUP (and SYS variants). Do NOT
                // forward WM_CHAR — Avalonia's own message pump calls TranslateMessage on
                // the WM_KEYDOWN we just posted and synthesises its own WM_CHAR. If we
                // also post WM_CHAR directly, Avalonia receives the character twice and
                // every keypress shows up doubled in TextBoxes.
                if (msg == WM_CHAR)
                    return IntPtr.Zero;

                IntPtr avHwnd = AvaloniaOverlay.AvaloniaHwnd;
                if (avHwnd != IntPtr.Zero)
                {
                    PostMessage(avHwnd, msg, wParam, lParam);
                    AvaloniaOverlay.RequestCapture();
                    return IntPtr.Zero;
                }
            }
            // ─────────────────────────────────────────────────────────────

            // Only enqueue if we did NOT already forward to Avalonia above.
            // (When AvaloniaTextInputActive is true and a key fires, we returned early.)
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

            // WM_ACTIVATE WA_INACTIVE fires when focus moves to another window in the SAME
            // process (e.g. an ImGui viewport popup, the DComp overlay bar). WM_ACTIVATEAPP
            // doesn't fire in that case, so AC would see its window go inactive and idle-
            // throttle its render loop. Always intercept same-process activations — this is
            // unconditionally correct for an injected overlay and must not be gated on
            // FpsLimitEnabled (DComp overlay clicks cause 8fps without this fix).
            if (msg == WM_ACTIVATE)
            {
                int activationCode = (int)((long)wParam & 0xFFFF);
                if (activationCode == WA_INACTIVE && lParam != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(lParam, out uint newActivePid);
                    if (newActivePid == GetCurrentProcessId())
                        return CallWindowProcA(_originalWndProc, hWnd, msg, (IntPtr)WA_ACTIVE, lParam);
                }
            }

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

                // Persist bar position on drag-end. All the live moves were
                // applied via MoveBarByPhys; CommitDrag(0,0) saves the final
                // canvas position without applying any additional delta.
                if (_avIsDragging)
                    AvaloniaOverlay.CommitDrag(0, 0);

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

            // While the left button is held down on a panel, keep forwarding
            // even if the cursor wanders off the panel rect — Avalonia's drag
            // handler needs uninterrupted PointerMoved + PointerReleased to
            // commit/release the drag. The latch clears on WM_LBUTTONUP.
            if (_avPanelMouseDown)
            {
                _lastPanelClientX = overlayPoint.X;
                _lastPanelClientY = overlayPoint.Y;
                AvaloniaMessagePoint heldPoint = ClientToAvaloniaMessagePoint(overlayPoint.X, overlayPoint.Y);
                _lastAvaloniaMessageX = heldPoint.X;
                _lastAvaloniaMessageY = heldPoint.Y;
                PostOverlayMouseMessage(avHwnd, msg, wParam, heldPoint.X, heldPoint.Y);
                if (msg == WM_LBUTTONUP)
                {
                    _avPanelMouseDown = false;
                    AvaloniaOverlay.SetDockedPanelPointerCaptureActive(false);
                }
                if (msg != WM_MOUSEMOVE || AvaloniaOverlay.ShouldUseCustomSkiaProducer)
                    AvaloniaOverlay.RequestCapture();
                return true;
            }

            if (!over)
            {
                if (msg == WM_LBUTTONUP)
                    _pendingBarButtonTitle = null;
                return false;
            }

            _lastPanelClientX = overlayPoint.X;
            _lastPanelClientY = overlayPoint.Y;

            // A docked press is starting (over a docked panel/bar). Assert no
            // floating panel is still marked as the shared-HWND SetCapture
            // coordinate owner: a stale FloatingPanelHost.PointerCapturingHost
            // (left set when a floating WM_LBUTTONUP never reached
            // AvaloniaSubclassWndProc — swallowed by the DockedPanelPointer-
            // CaptureActive early-return, or a redock/close mid-press) would
            // make this docked interaction's capture-routed move/release get
            // remapped into the floating panel's off-bounds space — the docked
            // click is eaten / needs a double-click. A WM_LBUTTONDOWN means the
            // button was up before, so any prior floating press already ended;
            // clearing here cannot truncate a live floating interaction.
            if (msg == WM_LBUTTONDOWN)
                FloatingPanelHost.PointerCapturingHost = null;

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
            {
                LogAvaloniaPointerDebug("forward-down", cx, cy, overlayPoint.X, overlayPoint.Y, messagePoint.X, messagePoint.Y);
                _avPanelMouseDown = true;
                AvaloniaOverlay.SetDockedPanelPointerCaptureActive(true);
            }
            PostOverlayMouseMessage(avHwnd, msg, wParam, messagePoint.X, messagePoint.Y);
            if (msg == WM_LBUTTONUP)
            {
                _avPanelMouseDown = false;
                AvaloniaOverlay.SetDockedPanelPointerCaptureActive(false);
            }
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
        if ((io.ConfigFlags & ImGuiConfigFlags.ViewportsEnable) != 0)
        {
            POINT p = new POINT { X = x, Y = y };
            ClientToScreen(_gameHwnd, ref p);
            io.AddMousePosEvent(p.X, p.Y);
        }
        else
        {
            io.AddMousePosEvent(x, y);
        }
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
        if (messageX.HasValue)
            RynthLog.Info($"AvaloniaPtr [{phase}] client=({clientX},{clientY}) overlay=({overlayX},{overlayY}) msg=({messageX},{messageY}).");
        else
            RynthLog.Info($"AvaloniaPtr [{phase}] client=({clientX},{clientY}) overlay=({overlayX},{overlayY}).");
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
