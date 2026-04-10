using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// Waits for the post-connect pre-character signal, then sends the same dismiss
/// clicks Thwarg uses to advance past the startup logo screens. This must be
/// started early so the client window is available before the login sequence runs.
/// </summary>
internal static class LogoBypassHooks
{
    private const int IdlePollMs = 50;
    private const int TimeoutSeconds = 30;
    private const int HwndPollMs = 200;
    private const int HwndPollMaxMs = 10_000;
    private const int ScheduledDismissClicks = 2;
    private const int PostConnectDismissStartDelayMs = 1000;
    private const int CharacterListDismissStartDelayMs = 300;
    private const int DismissRepeatDelayMs = 300;
    private const int AutoLoginAfterDismissDelayMs = 900;
    private const int DismissClickX = 350;
    private const int DismissClickY = 100;

    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private static volatile bool _started;
    private static readonly object StateLock = new();
    private static int _pendingDismissClicks;
    private static long _nextDismissTick;
    private static long _recommendedAutoLoginTick;

    /// <summary>
    /// Call early in InitWorker, before D3D9/Win32Backend are initialized.
    /// Reads <c>SkipLoginLogos</c> from <c>launch_context.json</c>; does nothing if false or missing.
    /// </summary>
    public static void Start()
    {
        if (_started)
            return;

        if (!ReadSkipLogosFromContext())
            return;

        if (LoginLifecycleHooks.HasObservedLoginComplete)
        {
            RynthLog.Compat("LogoBypass: Login already complete at start - skipping.");
            return;
        }

        _started = true;
        lock (StateLock)
        {
            _pendingDismissClicks = 0;
            _nextDismissTick = 0;
            _recommendedAutoLoginTick = 0;
        }

        var thread = new Thread(BypassThread)
        {
            Name = "RynthCore.LogoBypass",
            IsBackground = true
        };
        thread.Start();

        RynthLog.Compat("LogoBypass: Started - waiting for post-connect or character-list signal before dismiss clicks.");
    }

    public static void NotifyCharacterListObserved()
    {
        ScheduleDismissClicks("Character-list signal", CharacterListDismissStartDelayMs);
    }

    public static void NotifyPostConnectObserved()
    {
        ScheduleDismissClicks("Post-connect signal", PostConnectDismissStartDelayMs);
    }

    public static int GetRecommendedAutoLoginDelayMs()
    {
        lock (StateLock)
        {
            if (_recommendedAutoLoginTick == 0)
                return 0;

            long remaining = _recommendedAutoLoginTick - Environment.TickCount64;
            return remaining > 0 ? (int)Math.Min(remaining, int.MaxValue) : 0;
        }
    }

    private static void BypassThread()
    {
        IntPtr hwnd = PollForGameWindow();
        if (hwnd == IntPtr.Zero)
        {
            RynthLog.Compat("LogoBypass: Could not find game HWND - giving up.");
            return;
        }

        RynthLog.Compat($"LogoBypass: Found game HWND 0x{hwnd:X8} - awaiting post-connect or character-list signal.");
        BypassLoop(hwnd);
    }

    private static void ScheduleDismissClicks(string triggerName, int startDelayMs)
    {
        if (!_started)
            return;

        long now = Environment.TickCount64;
        bool scheduled = false;
        lock (StateLock)
        {
            if (_pendingDismissClicks <= 0 && (_recommendedAutoLoginTick == 0 || now >= _recommendedAutoLoginTick))
            {
                _pendingDismissClicks = ScheduledDismissClicks;
                _nextDismissTick = now + startDelayMs;
                _recommendedAutoLoginTick = _nextDismissTick
                    + ((ScheduledDismissClicks - 1L) * DismissRepeatDelayMs)
                    + AutoLoginAfterDismissDelayMs;
                scheduled = true;
            }
        }

        if (scheduled)
        {
            RynthLog.Compat(
                $"LogoBypass: {triggerName} observed - scheduling {ScheduledDismissClicks} dismiss click(s) at ({DismissClickX}, {DismissClickY}) with {startDelayMs}ms start delay.");
        }
    }

    private static IntPtr PollForGameWindow()
    {
        uint pid = GetCurrentProcessId();
        int elapsed = 0;
        while (elapsed < HwndPollMaxMs)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((hWnd, _) =>
            {
                GetWindowThreadProcessId(hWnd, out uint windowPid);
                if (windowPid == pid && IsWindowVisible(hWnd))
                {
                    found = hWnd;
                    return false;
                }

                return true;
            }, IntPtr.Zero);

            if (found != IntPtr.Zero)
                return found;

            Thread.Sleep(HwndPollMs);
            elapsed += HwndPollMs;
        }

        return IntPtr.Zero;
    }

    private static void BypassLoop(IntPtr hwnd)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(TimeoutSeconds);
        try
        {
            while (!LoginLifecycleHooks.HasObservedLoginComplete &&
                   DateTime.UtcNow < deadline)
            {
                if (TryTakeDueDismissClick())
                    SendDismissInput(hwnd);
                else
                    Thread.Sleep(IdlePollMs);
            }
        }
        finally
        {
            string reason = LoginLifecycleHooks.HasObservedLoginComplete ? "login complete" : "timeout";
            RynthLog.Compat($"LogoBypass: Stopped ({reason}).");
        }
    }

    private static bool TryTakeDueDismissClick()
    {
        lock (StateLock)
        {
            if (_pendingDismissClicks <= 0)
                return false;

            long now = Environment.TickCount64;
            if (now < _nextDismissTick)
                return false;

            _pendingDismissClicks--;
            _nextDismissTick = _pendingDismissClicks > 0 ? now + DismissRepeatDelayMs : 0;
            return true;
        }
    }

    private static void SendDismissInput(IntPtr hwnd)
    {
        IntPtr lParam = MakeLParam(DismissClickX, DismissClickY);
        PostMessage(hwnd, WM_MOUSEMOVE, IntPtr.Zero, lParam);
        PostMessage(hwnd, WM_LBUTTONDOWN, (IntPtr)0x0001, lParam);
        PostMessage(hwnd, WM_LBUTTONUP, IntPtr.Zero, lParam);
        RynthLog.Compat($"LogoBypass: Sent dismiss click at ({DismissClickX}, {DismissClickY}).");
    }

    private static IntPtr MakeLParam(int x, int y) =>
        (IntPtr)unchecked((uint)((y << 16) | (x & 0xFFFF)));

    private static bool ReadSkipLogosFromContext()
    {
        try
        {
            string rootDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RynthCore");

            string processPath = Path.Combine(rootDir, "launch_contexts", $"launch_context_{Environment.ProcessId}.json");
            bool? processValue = ReadSkipLogosFromFile(processPath);
            if (processValue.HasValue)
                return processValue.Value;

            return ReadSkipLogosFromFile(Path.Combine(rootDir, "launch_context.json")) ?? false;
        }
        catch
        {
            return false;
        }
    }

    private static bool? ReadSkipLogosFromFile(string path)
    {
        if (!File.Exists(path))
            return null;

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(path));
        return doc.RootElement.TryGetProperty("SkipLoginLogos", out JsonElement el) ? el.GetBoolean() : null;
    }
}
