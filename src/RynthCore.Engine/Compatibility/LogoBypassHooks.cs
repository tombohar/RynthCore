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

    // Early-burst dismiss: covers the pre-connect splash/EULA/Turbine logo
    // screens. Those screens appear before AC contacts the server, so the
    // packet-driven NotifyPostConnect / NotifyCharacterList triggers can never
    // catch them. As soon as we find a window of our PID, fire a series of
    // clicks at the splash hotspot to dismiss whatever shows up. Decal sidesteps
    // this by directly patching AC's splash code; we don't have that path, so
    // we brute-force it with PostMessage spam.
    private const int EarlyBurstClicks = 12;
    private const int EarlyBurstIntervalMs = 400;

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

        RynthLog.Compat($"LogoBypass: Found game HWND 0x{hwnd:X8} - firing {EarlyBurstClicks} early dismiss clicks before awaiting packet signals.");
        EarlyBurstDismiss(hwnd);
        BypassLoop(hwnd);
    }

    /// <summary>
    /// Fires a burst of dismiss clicks immediately, covering the pre-connect
    /// splash/EULA screens that the packet-driven triggers can't catch.
    /// Stops early if the client transitions out of the pre-login state
    /// (LoginComplete or auto-login-already-fired). Cheap to call into a
    /// post-splash window: the clicks just become spurious viewport clicks
    /// that AC's pre-connect UI ignores.
    /// </summary>
    private static void EarlyBurstDismiss(IntPtr hwnd)
    {
        for (int i = 0; i < EarlyBurstClicks; i++)
        {
            if (LoginLifecycleHooks.HasObservedLoginComplete)
                return;
            SendDismissInput(hwnd);
            Thread.Sleep(EarlyBurstIntervalMs);
        }
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
            RynthLog.Verbose($"LogoBypass: Stopped ({reason}).");
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
        RynthLog.Compat($"LogoBypass: Sent dismiss click at ({DismissClickX}, {DismissClickY}) to hwnd=0x{hwnd.ToInt64():X8}.");
    }

    private static IntPtr MakeLParam(int x, int y) =>
        (IntPtr)unchecked((uint)((y << 16) | (x & 0xFFFF)));

    private static bool ReadSkipLogosFromContext()
    {
        string rootDir = "(unresolved)";
        try
        {
            rootDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RynthCore");

            string processPath = Path.Combine(rootDir, "launch_contexts", $"launch_context_{Environment.ProcessId}.json");
            RynthLog.Compat($"LogoBypass: ReadSkipLogosFromContext processId={Environment.ProcessId} processPath='{processPath}'");
            bool? processValue = ReadSkipLogosFromFile(processPath, "process");
            if (processValue.HasValue)
            {
                RynthLog.Compat($"LogoBypass: process-scoped context resolved SkipLoginLogos={processValue.Value}.");
                return processValue.Value;
            }

            string rootPath = Path.Combine(rootDir, "launch_context.json");
            RynthLog.Compat($"LogoBypass: process file did not yield value, falling back to rootPath='{rootPath}'");
            bool? rootValue = ReadSkipLogosFromFile(rootPath, "root");
            bool resolved = rootValue ?? false;
            RynthLog.Compat($"LogoBypass: root context resolved SkipLoginLogos={(rootValue.HasValue ? rootValue.Value.ToString() : "MISSING")} (effective={resolved}).");
            return resolved;
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"LogoBypass: ReadSkipLogosFromContext threw {ex.GetType().Name}: {ex.Message} (rootDir='{rootDir}'). Treating as SkipLoginLogos=false.");
            return false;
        }
    }

    private static bool? ReadSkipLogosFromFile(string path, string label)
    {
        if (!File.Exists(path))
        {
            RynthLog.Compat($"LogoBypass: {label} context file missing at '{path}'.");
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (!doc.RootElement.TryGetProperty("SkipLoginLogos", out JsonElement el))
            {
                RynthLog.Compat($"LogoBypass: {label} context at '{path}' has no SkipLoginLogos property.");
                return null;
            }
            return el.GetBoolean();
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"LogoBypass: {label} context at '{path}' read/parse failed: {ex.GetType().Name}: {ex.Message}.");
            return null;
        }
    }
}
