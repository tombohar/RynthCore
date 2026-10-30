// ============================================================================
//  RynthCore.Engine - MainThreadHangWatchdog.cs
//  Diagnostic for the "client freezes / character heartbeat stops while the
//  bot keeps trying" class of bug: AC's MAIN thread wedges while our off-thread
//  engine threads keep running, so the freeze never surfaces as a crash and we
//  could only guess where it stuck.
//
//  How it works:
//    * EndSceneDetour (which runs on AC's main/render thread, every frame, even
//      with the ImGui backend disabled) calls MainThreadBeat() — stamping a
//      "last seen alive" tick and recording the main thread id once.
//    * A dedicated background thread polls that tick. If the main thread hasn't
//      beaten in > HangThresholdMs, it is wedged. We OpenThread + SuspendThread
//      just long enough to GetThreadContext (an accurate EIP/EBP/ESP snapshot),
//      immediately ResumeThread, then walk + log the native stack via
//      CrashLogger. (Logging happens AFTER resume so a main thread hung while
//      holding the log-file lock can't deadlock the watchdog. The thread is
//      hung, so its stack memory stays stable for the post-resume walk.)
//
//  This captures the exact wedge location for every client, on every freeze,
//  with no external debugger and no per-PID coordination.
// ============================================================================

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace RynthCore.Engine;

internal static class MainThreadHangWatchdog
{
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint dwDesiredAccess, bool bInheritHandle, uint dwThreadId);

    [DllImport("kernel32.dll")]
    private static extern uint SuspendThread(IntPtr hThread);

    [DllImport("kernel32.dll")]
    private static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetThreadContext(IntPtr hThread, IntPtr lpContext);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint THREAD_GET_CONTEXT       = 0x0008;
    private const uint THREAD_SUSPEND_RESUME    = 0x0002;
    private const uint THREAD_QUERY_INFORMATION = 0x0040;

    // x86 CONTEXT_FULL = CONTEXT_i386(0x10000) | CONTROL(1) | INTEGER(2) | SEGMENTS(4).
    private const int CONTEXT_FULL = 0x10007;
    // x86 CONTEXT is 716 bytes; over-allocate for safety. ContextFlags is at offset 0.
    private const int CONTEXT_SIZE = 1232;

    // While a wedge lasts, the "still hung" line is repeated this often.
    private const long StillHungRepeatMs = 20_000;

    /// <summary>"unhandled ACCESS_VIOLATION at acclient.exe+0x18712F" when the current hang is a crash dialog.</summary>
    internal static volatile string? CrashCause;

    // Stalls longer than this with no EndScene beat = main thread wedged.
    // Also the plugin pump's freeze gate (MainThreadFreeze): one signal for both.
    internal const long HangThresholdMs = 4000;
    // Max stack samples to take during a single hang (1/sec). A permanent freeze
    // shows a stable eip across these = the true wedge; a transient stall logs
    // RECOVERED. Capped to keep the log readable.
    private const int  MaxSamplesPerHang = 6;
    private const int  CTX_OFF_EIP = 184;   // x86 CONTEXT.Eip

    private static volatile uint _mainThreadId;
    private static long _lastBeatTick;
    private static int _running;
    // EndScene frame tally — single-writer (AC render thread via MainThreadBeat),
    // read by HeartbeatLogger to report fps (0 fps = render dead but process alive).
    private static int _frameCount;
    // Once-per-session latch so a permanent wedge writes exactly one minidump.
    private static int _dumpWritten;

    /// <summary>Monotonic EndScene frame count since engine init.</summary>
    internal static int FrameCount => _frameCount;

    /// <summary>True once the main thread has beaten (its id is known). Any thread.</summary>
    internal static bool HasBeaten => _mainThreadId != 0;

    /// <summary>Environment.TickCount64 of the last main-thread beat. Any thread.</summary>
    internal static long LastBeatTick => Volatile.Read(ref _lastBeatTick);

    /// <summary>
    /// Called from EndSceneDetour on AC's main thread, every frame. Must be
    /// cheap and never throw.
    /// </summary>
    internal static void MainThreadBeat()
    {
        if (_mainThreadId == 0)
            _mainThreadId = GetCurrentThreadId();
        _frameCount++;
        Volatile.Write(ref _lastBeatTick, Environment.TickCount64);
    }

    /// <summary>
    /// Second beat source: the Client::UseTime game-logic tick. Does NOT bump
    /// the frame counter (fps must keep meaning rendered frames). Closes the
    /// watchdog's blind spot where the main thread wedges BEFORE the new
    /// generation's EndScene hook ever fires (the 2026-06-11 reload wedge ran
    /// for ~60s with no hang detection and therefore no minidump) — with this
    /// beat, a wedged main thread always goes silent on BOTH sources and the
    /// watchdog produces the dump that names where the thread sits.
    /// </summary>
    internal static void MainThreadBeatNoFrame()
    {
        if (_mainThreadId == 0)
            _mainThreadId = GetCurrentThreadId();
        Volatile.Write(ref _lastBeatTick, Environment.TickCount64);
    }

    private static Thread? _thread;

    internal static void Start()
    {
        if (Interlocked.Exchange(ref _running, 1) != 0) return;
        Volatile.Write(ref _lastBeatTick, Environment.TickCount64);
        _thread = new Thread(Loop) { Name = "RynthCore.MainThreadHangWatchdog", IsBackground = true };
        _thread.Start();
        RynthLog.Info($"MainThreadHangWatchdog: armed (threshold={HangThresholdMs}ms).");
    }

    /// <summary>
    /// Stop the watchdog thread. Must run during EngineLifecycle.Shutdown: once
    /// the EndScene detour is uninstalled the beat goes silent, and a still-running
    /// old-generation watchdog declares a false hang within 4s of every hot-reload —
    /// suspending AC's healthy main thread and writing a spurious minidump while
    /// the next engine generation is initializing.
    /// </summary>
    internal static void Stop()
    {
        if (Interlocked.Exchange(ref _running, 0) == 0) return;
        var t = _thread;
        _thread = null;
        if (t != null && !t.Join(1500))
            RynthLog.Warn("MainThreadHangWatchdog: thread did not exit within 1500ms — continuing shutdown.");
        else
            RynthLog.Info("MainThreadHangWatchdog: stopped.");
    }

    private static void Loop()
    {
        bool inHang = false;
        int  sample = 0;
        long hangStartBeat = 0;
        long lastStillHungLog = 0;

        while (Volatile.Read(ref _running) != 0)
        {
            try
            {
                Thread.Sleep(1000);

                uint tid = _mainThreadId;
                if (tid == 0) continue;                       // EndScene hasn't beaten yet

                // The main thread is writing the OUT OF MEMORY full-memory dump (CrashDump,
                // from MessageBoxHooks). Neither suspend it nor report the wedge yet: the
                // launcher kills the client on "still hung after", which would cut the dump
                // short. The hang is detected and reported as usual once the dump is done.
                if (CrashDump.FullDumpInProgress)
                    continue;

                long lastBeat = Volatile.Read(ref _lastBeatTick);
                long stale    = Environment.TickCount64 - lastBeat;

                if (stale >= HangThresholdMs)
                {
                    if (!inHang)
                    {
                        inHang = true;
                        sample = 0;
                        hangStartBeat = lastBeat;
                        RynthLog.Info("================================================================");
                        RynthLog.Error($"==== MAIN THREAD HANG DETECTED tid={tid} build={EntryPoint.BuildStamp} initCount={EntryPoint.InitCount} (stalled {stale}ms) ====");
                        // A modal dialog (AC's error MessageBox) holds the main thread: say which.
                        string? box = Compatibility.MessageBoxHooks.Describe();
                        if (box != null) RynthLog.Error($"  hang cause: {box}");
                        // The plugin pump stuck in (or crashed inside) a plugin call: say which. A
                        // native fault in a managed plugin's export is fatal and leaves only
                        // "TickAll" in the event log; this line is written during the ~12 s the
                        // crash dump takes (2026-10-13: RynthAi's Tick).
                        string? pump = Plugins.PluginManager.DescribePumpDispatch();
                        if (pump != null) RynthLog.Error($"  plugin pump: {pump}");
                        CrashCause = null;
                    }

                    if (sample < MaxSamplesPerHang)
                    {
                        sample++;
                        // Full stack on the first sample; one-line eip on the rest.
                        CaptureMainThreadStack(tid, stale, sample, fullStack: sample == 1);
                    }
                    else if (sample == MaxSamplesPerHang)
                    {
                        sample++;
                        RynthLog.Info($"  (still hung after {MaxSamplesPerHang} samples — suppressing further samples until recovery)");
                        lastStillHungLog = Environment.TickCount64;
                        // Confirmed permanent wedge (~10s+): write one targeted
                        // minidump for WinDbg post-mortem if the log stack walk
                        // isn't enough. Once per session.
                        TryWriteHangDump(tid, stale);
                    }
                    else if (Environment.TickCount64 - lastStillHungLog >= StillHungRepeatMs)
                    {
                        // Repeat the confirmation while the wedge lasts. The
                        // launcher's wedge check reads only the last 16 KB of
                        // this log, and plugin lines kept coming after the
                        // 2026-09-28 12:04 crash dialog, pushing the single
                        // line out of that window: the crashed client was
                        // never restarted. The launcher also needs the banner
                        // in that window (it looks for "still hung after" AFTER
                        // the last "MAIN THREAD HANG DETECTED"), so the repeat
                        // carries it: the 18:06 death-portal crash sat frozen
                        // for over an hour with only the bare repeat.
                        lastStillHungLog = Environment.TickCount64;
                        RynthLog.Info($"==== MAIN THREAD HANG DETECTED (continuing) ==== (still hung after {MaxSamplesPerHang} samples — stalled {stale / 1000}s{(CrashCause != null ? ", " + CrashCause : "")})");
                    }
                }
                else if (inHang)
                {
                    inHang = false;
                    RynthLog.Info($"==== MAIN THREAD RECOVERED after ~{Environment.TickCount64 - hangStartBeat}ms ({Math.Min(sample, MaxSamplesPerHang)} sample(s) taken) ====");
                    RynthLog.Info("================================================================");
                }
            }
            catch { /* a diagnostic must never destabilize the host */ }
        }
    }

    /// <summary>
    /// On the first confirmed permanent hang of the session, write a minidump
    /// (gated by EngineSettings.EnableHangMinidump). Best-effort; never throws.
    /// </summary>
    private static void TryWriteHangDump(uint tid, long staleMs)
    {
        if (Interlocked.Exchange(ref _dumpWritten, 1) != 0) return;   // once per session
        try
        {
            if (!Plugins.EngineSettings.EnableHangMinidump)
            {
                RynthLog.Info("  (hang minidump disabled via EngineSettings.EnableHangMinidump)");
                return;
            }
            if (CrashDump.FullDumpWrittenPath is { } full)
            {
                RynthLog.Info($"  (hang minidump skipped: the full-memory dump {full} already covers this hang)");
                return;
            }
            CrashDump.WriteSelfDump(
                $"main-thread hang tid={tid} stale={staleMs}ms build={EntryPoint.BuildStamp}", out _);
        }
        catch { /* a diagnostic must never destabilize the host */ }
    }

    // ── Crash dialogs ─────────────────────────────────────────────────────
    // An unhandled exception on AC's main thread ends in msvcr70's _XcptFilter
    // -> kernelbase!UnhandledExceptionFilter -> NtRaiseHardError: Windows'
    // "Application Error" box, which waits there - a hang to this watchdog.
    // The exception is still on that frozen stack: UnhandledExceptionFilter's
    // one argument is the EXCEPTION_POINTERS. Find it by walking the EBP chain
    // and checking each frame's first argument for a plausible exception
    // record + x86 CONTEXT, then log the exception and walk the faulting stack.
    // Read-only, from this thread, while the main thread sits in the dialog:
    // nothing runs inside exception dispatch (in-process VEH/SUEF handlers are
    // fatal in NativeAOT; see CrashLogger).

    private const int CTX_OFF_EBP = 180;
    private const int X86_CONTEXT_BYTES = 716;
    private const uint CONTEXT_i386 = 0x10000;

    private static void TryLogUnhandledException(IntPtr hangCtx)
    {
        try
        {
            uint ebp = (uint)Marshal.ReadInt32(hangCtx, CTX_OFF_EBP);
            for (int frame = 0; frame < 12 && ebp != 0; frame++)
            {
                IntPtr f = (IntPtr)(long)ebp;
                if (!Compatibility.SmartBoxLocator.IsMemoryReadable(f, 12)) return;
                uint arg0 = (uint)Marshal.ReadInt32(f, 8);
                if (TryReadExceptionPointers(arg0, out uint rec, out uint ctx))
                {
                    LogUnhandled(rec, ctx);
                    return;
                }
                uint next = (uint)Marshal.ReadInt32(f);
                if (next <= ebp) return;   // the chain must go up the stack
                ebp = next;
            }
        }
        catch { /* a diagnostic must never destabilize the host */ }
    }

    private static bool TryReadExceptionPointers(uint ep, out uint rec, out uint ctx)
    {
        rec = ctx = 0;
        if (ep < 0x10000 || !Compatibility.SmartBoxLocator.IsMemoryReadable((IntPtr)(long)ep, 8)) return false;
        rec = (uint)Marshal.ReadInt32((IntPtr)(long)ep);
        ctx = (uint)Marshal.ReadInt32((IntPtr)(long)ep, 4);
        if (rec < 0x10000 || ctx < 0x10000) return false;
        if (!Compatibility.SmartBoxLocator.IsMemoryReadable((IntPtr)(long)rec, 80)) return false;
        if (!Compatibility.SmartBoxLocator.IsMemoryReadable((IntPtr)(long)ctx, X86_CONTEXT_BYTES)) return false;
        uint code = (uint)Marshal.ReadInt32((IntPtr)(long)rec);
        uint nParams = (uint)Marshal.ReadInt32((IntPtr)(long)rec, 16);
        uint flags = (uint)Marshal.ReadInt32((IntPtr)(long)ctx);
        // System errors (0xC0xxxxxx), C++ throw, breakpoint. The looser 0xC0000000
        // mask let stack garbage through: a normal close logged "exception
        // code=0xD5000B88 at 0x00000000" (2026-09-28 20:16).
        bool plausibleCode = (code & 0xFF000000) == 0xC0000000 || code == 0xE06D7363 || code == 0x80000003;
        return plausibleCode && nParams <= 15 && (flags & CONTEXT_i386) != 0
            && Marshal.ReadInt32((IntPtr)(long)ctx, 184) != 0;   // Eip
    }

    private static void LogUnhandled(uint rec, uint ctx)
    {
        IntPtr r = (IntPtr)(long)rec;
        uint code = (uint)Marshal.ReadInt32(r);
        uint addr = (uint)Marshal.ReadInt32(r, 12);
        uint nParams = (uint)Marshal.ReadInt32(r, 16);
        string name = code switch
        {
            0xC0000005 => "ACCESS_VIOLATION",
            0xC000001D => "ILLEGAL_INSTRUCTION",
            0xC0000094 => "INT_DIVIDE_BY_ZERO",
            0xC00000FD => "STACK_OVERFLOW",
            0xC0000409 => "STACK_BUFFER_OVERRUN",
            0xC0000374 => "HEAP_CORRUPTION",
            0xE06D7363 => "C++ exception",
            _ => "exception",
        };
        string where = CrashLogger.ResolveCodeAddr((IntPtr)(long)addr);
        string detail = "";
        if (code == 0xC0000005 && nParams >= 2)
        {
            uint op = (uint)Marshal.ReadInt32(r, 20);
            uint target = (uint)Marshal.ReadInt32(r, 24);
            detail = $" — {(op == 0 ? "read of" : op == 1 ? "write to" : op == 8 ? "execute at" : "access to")} 0x{target:X8}";
        }
        CrashCause = $"unhandled {name} at {where}";
        RynthLog.Error($"==== UNHANDLED EXCEPTION on AC's main thread (Windows' error dialog is open): {name} code=0x{code:X8} at 0x{addr:X8} {where}{detail} ====");

        // Walk the faulting stack from a private copy of the CONTEXT.
        IntPtr copy = Marshal.AllocHGlobal(CONTEXT_SIZE);
        try
        {
            for (int i = 0; i < CONTEXT_SIZE; i += 4) Marshal.WriteInt32(copy, i, 0);
            for (int i = 0; i < X86_CONTEXT_BYTES; i += 4)
                Marshal.WriteInt32(copy, i, Marshal.ReadInt32((IntPtr)(long)ctx, i));
            CrashLogger.DumpExternalContext(copy, "CRASH");
        }
        finally { Marshal.FreeHGlobal(copy); }
    }

    private static void CaptureMainThreadStack(uint tid, long staleMs, int sampleNum, bool fullStack)
    {
        IntPtr h = OpenThread(THREAD_GET_CONTEXT | THREAD_SUSPEND_RESUME | THREAD_QUERY_INFORMATION, false, tid);
        if (h == IntPtr.Zero)
        {
            RynthLog.Info($"  sample#{sampleNum}: OpenThread failed err={Marshal.GetLastWin32Error()}");
            return;
        }

        IntPtr ctx = Marshal.AllocHGlobal(CONTEXT_SIZE);
        bool ctxOk = false;
        uint suspendCount = 0xFFFFFFFF;
        try
        {
            for (int i = 0; i < CONTEXT_SIZE; i += 4)
                Marshal.WriteInt32(ctx, i, 0);
            Marshal.WriteInt32(ctx, 0, CONTEXT_FULL);          // ContextFlags @ offset 0

            // Minimal suspend window: snapshot the register context, then resume
            // immediately so the (possibly log-lock-holding) main thread isn't
            // frozen while we do file I/O below.
            suspendCount = SuspendThread(h);
            if (suspendCount != 0xFFFFFFFF)
            {
                ctxOk = GetThreadContext(h, ctx);
                ResumeThread(h);
            }

            if (!ctxOk)
            {
                RynthLog.Info($"  sample#{sampleNum}: GetThreadContext failed (suspendCount={suspendCount})");
                return;
            }

            uint eip = (uint)Marshal.ReadInt32(ctx, CTX_OFF_EIP);
            RynthLog.Info($"  sample#{sampleNum} (stale {staleMs}ms) eip=0x{eip:X8} {CrashLogger.ResolveCodeAddr((IntPtr)eip)}");

            if (fullStack)
            {
                CrashLogger.DumpExternalContext(ctx, "HANG");
                // A closing client stalls in ExitProcess, not in a crash dialog.
                if (!Compatibility.ProcessExitHooks.TerminationIntercepted)
                    TryLogUnhandledException(ctx);
            }
        }
        catch (Exception ex)
        {
            RynthLog.Info($"  sample#{sampleNum}: capture threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Marshal.FreeHGlobal(ctx);
            CloseHandle(h);
        }
    }
}
