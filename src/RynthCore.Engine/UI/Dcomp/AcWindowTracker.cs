// AC window tracker for the DComp overlay path.
//
// Subclasses AC's WndProc to catch WM_MOVE / WM_SIZE / WM_SHOWWINDOW /
// WM_ACTIVATE and fires .NET events the overlay subscribes to. Lets the
// DcompOverlayWindow keep its position locked to AC's client area without
// constant polling.
//
// IMPORTANT: AC's WndProc is ALREADY subclassed by Win32Backend.cs for the
// production overlay's input forwarding path. We can't double-subclass;
// instead we install AFTER Win32Backend if it's present, or directly if not.
// To keep this self-contained for the parallel system, we use a polling
// fallback (every 100ms) — fast enough to feel smooth during drag and avoids
// any subclass-conflict risk with production code. Phase D can replace the
// poller with proper subclass-chaining when this path becomes canonical.

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace RynthCore.Engine.UI.Dcomp;

internal static class AcWindowTracker
{
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint lpdwProcessId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    /// <summary>
    /// When true, the tracker actively restores AC as the foreground window
    /// whenever it sees foreground!=AC — needed because AC's EndSceneHook FPS
    /// governor checks GetForegroundWindow(). Set false for normal user
    /// foreground swaps (Alt-Tab to a different app etc.) — but for now we
    /// always-on this until we're confident the bar isn't stealing focus.
    /// </summary>
    public static bool RestoreForegroundOnDrift { get; set; } = false;

    [StructLayout(LayoutKind.Sequential)] private struct RECT  { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    public readonly record struct ClientRect(int ScreenX, int ScreenY, int Width, int Height, bool Visible, bool Foreground);

    /// <summary>Fires whenever AC's client-area screen rect changes (move, resize, show/hide).</summary>
    public static event Action<ClientRect>? ClientRectChanged;

    private static ClientRect _last;
    private static IntPtr _trackedHwnd;
    private static Thread? _thread;
    private static bool _started;

    public static void Start()
    {
        if (_started) return;
        _started = true;

        _thread = new Thread(PollLoop)
        {
            Name = "RynthCore.Dcomp.AcTracker",
            IsBackground = true,
        };
        _thread.Start();
    }

    private static void PollLoop()
    {
        // Poll cadence: 16ms = ~60 Hz. The user reported visible drag lag at
        // 50ms, which makes sense — that's enough latency to feel "draggy"
        // when the cursor is moving fast. 16ms matches monitor refresh and
        // is virtually free CPU-wise (just a couple of GetClientRect calls).
        const int PollPeriodMs = 16;
        const long DiagnosticPeriodMs = 5000;

        long nextDiagnosticAt = Environment.TickCount64 + DiagnosticPeriodMs;

        while (true)
        {
            try
            {
                IntPtr hwnd = ImGuiBackend.Win32Backend.GameHwnd;
                if (hwnd == IntPtr.Zero)
                {
                    Thread.Sleep(PollPeriodMs);
                    continue;
                }

                if (hwnd != _trackedHwnd)
                {
                    _trackedHwnd = hwnd;
                    _last = default;
                    RynthLog.UI($"AcWindowTracker: now tracking HWND 0x{hwnd.ToInt64():X}.");
                }

                if (!GetClientRect(hwnd, out RECT rc))
                {
                    Thread.Sleep(PollPeriodMs);
                    continue;
                }

                var pt = new POINT { X = rc.Left, Y = rc.Top };
                if (!ClientToScreen(hwnd, ref pt))
                {
                    Thread.Sleep(PollPeriodMs);
                    continue;
                }

                bool visible = IsWindowVisible(hwnd) && !IsIconic(hwnd);
                int width = rc.Right - rc.Left;
                int height = rc.Bottom - rc.Top;

                // Foreground = AC itself, OR any window in our process (overlay
                // bar / panels). With WS_EX_NOACTIVATE clicking the bar shouldn't
                // move foreground, but cover the case anyway via PID match.
                IntPtr fg = GetForegroundWindow();
                bool foreground = false;
                if (fg != IntPtr.Zero)
                {
                    if (fg == hwnd) foreground = true;
                    else
                    {
                        GetWindowThreadProcessId(fg, out uint fgPid);
                        if (fgPid == GetCurrentProcessId()) foreground = true;
                    }
                }

                var current = new ClientRect(pt.X, pt.Y, width, height, visible, foreground);
                if (current != _last)
                {
                    _last = current;
                    try { ClientRectChanged?.Invoke(current); }
                    catch (Exception ex)
                    {
                        RynthLog.UI($"AcWindowTracker: ClientRectChanged subscriber threw {ex.GetType().Name}: {ex.Message}");
                    }
                }

                // Periodic foreground diagnostic. Logs what currently holds
                // foreground vs. AC's HWND, so we can pin down whether AC is
                // really being seen as background by Windows (which would trip
                // EndSceneHook's FPS governor) or whether the governor logic
                // has a different bug. Also optionally restores AC foreground.
                long now = Environment.TickCount64;
                if (now >= nextDiagnosticAt)
                {
                    nextDiagnosticAt = now + DiagnosticPeriodMs;
                    LogForegroundStatus(hwnd);

                    if (RestoreForegroundOnDrift && visible)
                    {
                        IntPtr fgNow = GetForegroundWindow();
                        if (fgNow != hwnd && fgNow != IntPtr.Zero)
                        {
                            SetForegroundWindow(hwnd);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                RynthLog.UI($"AcWindowTracker: PollLoop swallowed {ex.GetType().Name}: {ex.Message}");
            }
            Thread.Sleep(PollPeriodMs);
        }
    }

    private static void LogForegroundStatus(IntPtr acHwnd)
    {
        try
        {
            IntPtr fg = GetForegroundWindow();
            string fgInfo;
            if (fg == IntPtr.Zero)
            {
                fgInfo = "<null>";
            }
            else if (fg == acHwnd)
            {
                fgInfo = "AC";
            }
            else
            {
                var title = new System.Text.StringBuilder(128);
                var cls   = new System.Text.StringBuilder(128);
                GetWindowTextW(fg, title, title.Capacity);
                GetClassNameW(fg, cls, cls.Capacity);
                fgInfo = $"0x{fg.ToInt64():X} title='{title}' cls='{cls}'";
            }
            RynthLog.UI($"AcWindowTracker: foreground={fgInfo}, ac=0x{acHwnd.ToInt64():X}.");
        }
        catch { }
    }

    public static ClientRect Current => _last;
}
