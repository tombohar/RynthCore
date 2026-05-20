using System;
using System.Threading;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// Writes one short "hb #N" line to the unified log every second from a
/// background thread. Purpose: when AC dies and no termination hook fires,
/// the heartbeat gives a hard upper-bound timestamp for when the process
/// went silent — so external traces (Procmon, Application Event Log,
/// network captures) can be correlated to the second.
///
/// Cheap (one log line / second). Background thread, won't keep the
/// process alive on its own. One-shot start; safe to call from
/// pre-resume early-init.
/// </summary>
internal static class HeartbeatLogger
{
    private const int IntervalMs = 1000;
    private static int _started;
    private static int _stopRequested;
    private static int _exited;
    private static Thread? _thread;

    public static void Start()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            return;

        _thread = new Thread(Run)
        {
            Name = "RynthCore.Heartbeat",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal
        };
        _thread.Start();
        RynthLog.Info("HeartbeatLogger: started (1s cadence).");
    }

    /// <summary>
    /// Signals the heartbeat thread to exit and waits up to <paramref name="timeoutMs"/>
    /// for it to do so. Called from EngineLifecycle.Shutdown — without this, the
    /// thread keeps running past loader FreeLibrary of the engine, executing code
    /// pages that have been unmapped → CLR exception / FAIL_FAST during hot reload.
    /// </summary>
    public static bool StopAndJoin(int timeoutMs = 1500)
    {
        if (_thread == null) return true;
        Interlocked.Exchange(ref _stopRequested, 1);

        long deadline = Environment.TickCount64 + timeoutMs;
        while (Volatile.Read(ref _exited) == 0 && Environment.TickCount64 < deadline)
            Thread.Sleep(10);

        bool exited = Volatile.Read(ref _exited) != 0;
        if (!exited)
            RynthLog.Info($"HeartbeatLogger: did NOT exit within {timeoutMs}ms.");
        return exited;
    }

    private static void Run()
    {
        long tick = 0;
        try
        {
            while (Volatile.Read(ref _stopRequested) == 0)
            {
                tick++;
                try { RynthLog.Info($"hb #{tick}"); }
                catch { /* never let the heartbeat itself bring anything down */ }
                // Self-healing: clear a stuck floating-panel click-through
                // (DockedPanelPointerCaptureActive whose disarm was lost ->
                // every floating panel left WS_EX_TRANSPARENT). Runs here on
                // purpose: independent of the input path that strands it.
                try { RynthCore.Engine.UI.AvaloniaOverlay.WatchdogClearStuckClickThrough(); }
                catch { /* never let the heartbeat itself bring anything down */ }
                // Sleep in short slices so a stop signal is observed within <100ms,
                // not up to a full IntervalMs after request.
                int slept = 0;
                while (slept < IntervalMs && Volatile.Read(ref _stopRequested) == 0)
                {
                    try { Thread.Sleep(50); } catch { return; }
                    slept += 50;
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _exited, 1);
        }
    }
}
