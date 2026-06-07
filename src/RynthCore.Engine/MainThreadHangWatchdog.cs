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

    // Stalls longer than this with no EndScene beat = main thread wedged.
    private const long HangThresholdMs = 4000;
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

    internal static void Start()
    {
        if (Interlocked.Exchange(ref _running, 1) != 0) return;
        Volatile.Write(ref _lastBeatTick, Environment.TickCount64);
        new Thread(Loop) { Name = "RynthCore.MainThreadHangWatchdog", IsBackground = true }.Start();
        RynthLog.Info($"MainThreadHangWatchdog: armed (threshold={HangThresholdMs}ms).");
    }

    private static void Loop()
    {
        bool inHang = false;
        int  sample = 0;
        long hangStartBeat = 0;

        while (Volatile.Read(ref _running) != 0)
        {
            try
            {
                Thread.Sleep(1000);

                uint tid = _mainThreadId;
                if (tid == 0) continue;                       // EndScene hasn't beaten yet

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
                        // Confirmed permanent wedge (~10s+): write one targeted
                        // minidump for WinDbg post-mortem if the log stack walk
                        // isn't enough. Once per session.
                        TryWriteHangDump(tid, stale);
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
            CrashDump.WriteSelfDump(
                $"main-thread hang tid={tid} stale={staleMs}ms build={EntryPoint.BuildStamp}", out _);
        }
        catch { /* a diagnostic must never destabilize the host */ }
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
                CrashLogger.DumpExternalContext(ctx, "HANG");
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
