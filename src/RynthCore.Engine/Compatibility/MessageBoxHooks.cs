// ============================================================================
//  RynthCore.Engine - Compatibility/MessageBoxHooks.cs
//  Logs every message box shown in this process (user32!MessageBoxW / A):
//  caption, text, style and the calling module, when it opens and when it
//  closes.
//
//  Why: AC reports some errors in a modal MessageBox on its main thread
//  (acclient+0x3CE80, formatted by the caller at acclient+0x1558B5). The main
//  thread then waits on the dialog, MainThreadHangWatchdog reports a wedge,
//  and the launcher kills and relaunches the client - with the dialog's text
//  lost (the 2026-09-28 Drakkon6 close: heap isn't in the hang minidump).
//  Now the text is in the log, and the watchdog names the open dialog in its
//  hang report (see Describe).
//
//  The detour only observes, with one exception (2026-10-12): AC's "Failed to
//  establish connection to the server" box (a relaunch that logged in too soon
//  after a crash, or the server down). AC's game loop waits on it, so the
//  Close on server disconnect check never ran and the client sat there for
//  10 minutes. With that setting on, the box is answered OK without being
//  shown (AC then exits, as when the button is clicked), and if the client is
//  still open 20 s later the window is sent WM_CLOSE (again at 40 s).
//  Installed per engine generation; EngineLifecycle's MH_DisableHook(ALL)
//  removes it on reload.
// ============================================================================

using System;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.Compatibility;

internal static class MessageBoxHooks
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int MessageBoxDelegate(IntPtr hWnd, IntPtr text, IntPtr caption, uint type);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string lpLibFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll")]
    private static extern ushort RtlCaptureStackBackTrace(uint framesToSkip, uint framesToCapture, IntPtr[] backTrace, out uint hash);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private static MessageBoxDelegate? _detourW, _detourA, _originalW, _originalA;
    private static int _installed;

    // The dialog currently open (last one wins if several nest). Read by the
    // hang watchdog's thread; written on the thread showing the dialog.
    private static volatile string? _openDescription;
    private static long _openedAtTicks;
    private static int _openCount;
    // Boxes open on AC's MAIN thread: the main thread is inside a modal loop, so the plugin
    // pump's freeze gate (MainThreadFreeze) pauses plugin ticks at once.
    private static int _mainThreadOpenCount;

    /// <summary>Message boxes open right now on AC's main thread. Any thread.</summary>
    public static int MainThreadOpenCount => Volatile.Read(ref _mainThreadOpenCount);


    public static void Initialize()
    {
        if (Interlocked.CompareExchange(ref _installed, 1, 0) != 0) return;
        try
        {
            IntPtr user32 = GetModuleHandleW("user32.dll");
            if (user32 == IntPtr.Zero) user32 = LoadLibraryW("user32.dll");
            if (user32 == IntPtr.Zero)
            {
                RynthLog.Compat("MessageBoxHooks: user32 not found.");
                return;
            }
            _detourW = MessageBoxWDetour;
            _detourA = MessageBoxADetour;
            _originalW = Hook(user32, "MessageBoxW", _detourW);
            _originalA = Hook(user32, "MessageBoxA", _detourA);
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"MessageBoxHooks: install failed - {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static MessageBoxDelegate? Hook(IntPtr user32, string name, MessageBoxDelegate detour)
    {
        IntPtr target = GetProcAddress(user32, name);
        if (target == IntPtr.Zero)
        {
            RynthLog.Compat($"MessageBoxHooks: GetProcAddress({name}) failed.");
            return null;
        }
        IntPtr trampoline = MinHook.HookCreate(target, Marshal.GetFunctionPointerForDelegate(detour));
        var original = Marshal.GetDelegateForFunctionPointer<MessageBoxDelegate>(trampoline);
        Thread.MemoryBarrier();
        MinHook.Enable(target);
        RynthLog.Info($"MessageBoxHooks: hooked user32!{name} @ 0x{target.ToInt32():X8}.");
        return original;
    }

    /// <summary>
    /// The message box open right now, e.g. "AC message box open 12s on the
    /// main thread: 'Asheron's Call' - '…'", or null. Any thread.
    /// </summary>
    public static string? Describe()
    {
        string? open = _openDescription;
        if (open == null) return null;
        long secs = (Environment.TickCount64 - Interlocked.Read(ref _openedAtTicks)) / 1000;
        return $"message box open {secs}s: {open}";
    }

    private static int MessageBoxWDetour(IntPtr hWnd, IntPtr text, IntPtr caption, uint type) =>
        Show(hWnd, text, caption, type, wide: true);

    private static int MessageBoxADetour(IntPtr hWnd, IntPtr text, IntPtr caption, uint type) =>
        Show(hWnd, text, caption, type, wide: false);

    private static int Show(IntPtr hWnd, IntPtr text, IntPtr caption, uint type, bool wide)
    {
        MessageBoxDelegate? original = wide ? _originalW : _originalA;
        string? description = null;
        bool counted = false;
        long opened = Environment.TickCount64;
        int result = 0;
        try
        {
            try
            {
                // Counted first: the freeze gate must see the box even if the rest throws.
                bool mainThread = MainThreadGuard.IsOnMainThread();
                if (mainThread)
                {
                    Interlocked.Increment(ref _mainThreadOpenCount);
                    counted = true;
                }
                string body = Read(text, wide);
                // AC's OUT OF MEMORY box is modal: the process never crashes, so Windows never
                // writes a dump, and the launcher later kills the client as wedged. Decide and
                // write ONE full-memory dump now, before anything here allocates more (the stack
                // walk and the description below), and before the box shows (this thread waits:
                // the box would hold it anyway, and the user's OK must not let AC run on and
                // exit mid-dump).
                if (OutOfMemoryBoxRule.IsOutOfMemoryBox(body, mainThread))
                    CrashDump.WriteOutOfMemoryDumpOnce(Flatten(body));
                description = Opened(body, caption, type, wide, mainThread);
            }
            catch { /* observation only: never block the dialog */ }

            // The one box answered without showing it (Close on server disconnect, 2026-10-12).
            result = AnswerConnectionFailure(text, wide)
                ? IDOK
                : original != null ? original(hWnd, text, caption, type) : 0;
        }
        finally
        {
            // First, and whatever happened above: a leaked count would pause plugin ticks for good.
            if (counted && Interlocked.Decrement(ref _mainThreadOpenCount) < 0)
                Interlocked.Exchange(ref _mainThreadOpenCount, 0);
            try
            {
                long secs = (Environment.TickCount64 - opened) / 1000;
                if (Interlocked.Decrement(ref _openCount) <= 0)
                {
                    Interlocked.Exchange(ref _openCount, 0);
                    _openDescription = null;
                }
                RynthLog.Info($"MessageBoxHooks: closed after {secs}s (result={result}): {description ?? "?"}");
            }
            catch { }
        }
        return result;
    }

    private const int IDOK = 1;
    private static int _connectionFailureAnswered;

    /// <summary>
    /// True for AC's connection-failure box while Close on server disconnect is on: answered
    /// OK unseen, with a WM_CLOSE fallback if AC doesn't exit by itself. Never throws.
    /// </summary>
    private static bool AnswerConnectionFailure(IntPtr text, bool wide)
    {
        try
        {
            if (!EngineSettings.CloseOnServerDisconnect) return false;
            if (!Read(text, wide).StartsWith("Failed to establish connection to the server", StringComparison.OrdinalIgnoreCase))
                return false;
            RynthLog.Warn("MessageBoxHooks: the connection failed - answering AC's box (OK) unseen so the client exits " +
                          "and a launcher can relaunch it (Close on server disconnect, Settings > Misc).");
            if (Interlocked.Exchange(ref _connectionFailureAnswered, 1) == 0)
            {
                var fallback = new Thread(() =>
                {
                    for (int i = 1; i <= 2; i++)
                    {
                        Thread.Sleep(20_000);
                        ServerDisconnectClose.CloseClientNow($"the client is still open {i * 20}s after the connection-failure box");
                    }
                }) { IsBackground = true, Name = "RynthCore connection-failure close" };
                fallback.Start();
            }
            return true;
        }
        catch { return false; }
    }

    private static string Opened(string body, IntPtr caption, uint type, bool wide, bool mainThread)
    {
        string title = Read(caption, wide);

        // The first frame outside user32 and this file: who asked for the dialog.
        string caller = "?";
        var frames = new IntPtr[8];
        int n = RtlCaptureStackBackTrace(1, (uint)frames.Length, frames, out _);
        for (int i = 0; i < n; i++)
        {
            string module = ProcessExitHooks.ResolveModule(frames[i], out int rva);
            if (module.Contains("RynthCore.Engine", StringComparison.OrdinalIgnoreCase)
                || module.Contains("user32", StringComparison.OrdinalIgnoreCase))
                continue;
            caller = $"{module}+0x{rva:X}";
            break;
        }

        string description = $"'{title}' - '{Flatten(body)}' (style=0x{type:X}, {(mainThread ? "AC main thread" : $"thread {GetCurrentThreadId()}")}, caller {caller})";
        Interlocked.Exchange(ref _openedAtTicks, Environment.TickCount64);
        Interlocked.Increment(ref _openCount);
        _openDescription = description;
        RynthLog.Error($"MessageBoxHooks: MESSAGE BOX OPENED {description}");
        return description;
    }

    // -- Stale main-thread count (safety net) ------------------------------------
    // The count is decremented in Show's finally, so it should never leak; if it ever reads
    // non-zero while AC's main thread owns no dialog window for 30 s, it is reset (and
    // logged), so a leak can never pause plugin ticks for good. Heartbeat thread, 1 Hz.
    private const long StaleCountResetMs = 30_000;
    private static long _countWithoutBoxSinceMs;

    private delegate bool EnumThreadWndProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumThreadWindows(uint threadId, EnumThreadWndProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder name, int max);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private static bool MainThreadOwnsDialog()
    {
        uint tid = MainThreadGuard.MainThreadId;
        if (tid == 0) return false;
        bool found = false;
        var name = new System.Text.StringBuilder(64);
        EnumThreadWindows(tid, (h, _) =>
        {
            name.Clear();
            if (GetClassNameW(h, name, name.Capacity) > 0 && name.ToString() == "#32770" && IsWindowVisible(h))
            {
                found = true;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Heartbeat thread, about once a second. Never throws.</summary>
    public static void ReconcileStaleMainThreadCount()
    {
        try
        {
            long now = Environment.TickCount64;
            // The OOM dump runs before the box window exists: counted, no dialog yet, legitimately.
            if (MainThreadOpenCount <= 0 || CrashDump.FullDumpInProgress || MainThreadOwnsDialog())
            {
                _countWithoutBoxSinceMs = 0;
                return;
            }
            if (_countWithoutBoxSinceMs == 0) { _countWithoutBoxSinceMs = now; return; }
            if (now - _countWithoutBoxSinceMs < StaleCountResetMs) return;
            int was = Interlocked.Exchange(ref _mainThreadOpenCount, 0);
            _countWithoutBoxSinceMs = 0;
            RynthLog.Warn($"MessageBoxHooks: main-thread box count was {was} with no dialog open on AC's main thread for {StaleCountResetMs / 1000}s - reset to 0.");
        }
        catch { }
    }
    private static string Read(IntPtr p, bool wide)
    {
        if (p == IntPtr.Zero) return "";
        string? s = wide ? Marshal.PtrToStringUni(p) : Marshal.PtrToStringAnsi(p);
        if (s == null) return "";
        return s.Length > 2000 ? s.Substring(0, 2000) + "…" : s;
    }

    private static string Flatten(string s) => s.Replace("\r\n", " | ").Replace('\n', ' ').Replace('\r', ' ');
}
