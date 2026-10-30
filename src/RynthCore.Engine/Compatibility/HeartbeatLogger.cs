using System;
using System.Runtime.InteropServices;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                     ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    /// <summary>
    /// Free and total user address space of this (32-bit) process, in MB, or
    /// (-1, -1). Every reload leaks the old engine and plugin copies, each a
    /// NativeAOT runtime with its own reservations, so this only goes down;
    /// 2026-09-28 13:57 a client vanished while its 5th engine loaded plugins.
    /// </summary>
    public static (long FreeMb, long TotalMb) AddressSpaceMb()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) return (-1, -1);
        return ((long)(m.ullAvailVirtual >> 20), (long)(m.ullTotalVirtual >> 20));
    }

    public static void Start()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            return;
        try
        {
            var (free, total) = AddressSpaceMb();
            RynthLog.Info($"HeartbeatLogger: address space at engine start: {free} MB free of {total} MB (initCount={EntryPoint.InitCount}).");
        }
        catch { }

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
        int lastFrames = 0;
        int lastDrawn = 0;
        int lastPlugTicks = 0;
        long lastMs = Environment.TickCount64;
        long startMs = lastMs;
        try
        {
            while (Volatile.Read(ref _stopRequested) == 0)
            {
                tick++;
                // Rich beat: a silent-death log tail now shows WHAT the client
                // was doing — render rate (0 fps = render dead, process alive),
                // plugin pump rate (0 = wedged pump), memory trend, and in-world
                // state — not just "alive at T". All cheap, engine-side reads.
                try
                {
                    long nowMs = Environment.TickCount64;
                    long dtMs = nowMs - lastMs; if (dtMs <= 0) dtMs = 1;
                    int frames = MainThreadHangWatchdog.FrameCount;
                    int plug   = Plugins.PluginManager.TickCount;
                    int fps = (int)((frames - lastFrames) * 1000L / dtMs);
                    int pps = (int)((plug - lastPlugTicks) * 1000L / dtMs);
                    lastFrames = frames; lastPlugTicks = plug; lastMs = nowMs;
                    // fps counts every EndScene, including the offscreen passes Decal's views,
                    // VVS and UB render each frame (2-3 per frame in bridge mode), so it read
                    // 40-130 while the game drew 19-42 (2026-10-03). draw= is frames on screen.
                    int drawn = D3D9.EndSceneHook.BackBufferFrames;
                    int draw = drawn >= lastDrawn ? (int)((drawn - lastDrawn) * 1000L / dtMs) : 0;
                    lastDrawn = drawn;

                    long wsMb = 0;
                    try { wsMb = Environment.WorkingSet / (1024 * 1024); } catch { }
                    int login = 0;
                    try { login = LoginLifecycleHooks.HasObservedLoginComplete ? 1 : 0; } catch { }

                    // qdrop = marshalled actions dropped (ring full) since start: a running
                    // TOTAL, not a queue depth. Named qdrop, not qd, since 2026-10-01: launchers
                    // read "qd" as a depth and killed healthy 60 fps clients once the total
                    // passed 2000 (a loot loop had flooded the ring), and an old launcher
                    // doesn't parse qdrop. A total climbing with healthy fps = a producer
                    // flooding the ring; flat = fine.
                    long dropped = 0;
                    try { dropped = AcMainThreadQueue.DroppedCount; } catch { }
                    // qrej = marshalled actions and casts refused during a world transition
                    // (dead / portal / settle window), a running total kept apart from qdrop: a
                    // jump in qrej around a teleport is the gate working, not a flood.
                    long rejected = 0;
                    try { rejected = AcMainThreadQueue.TransitionRejectedCount; } catch { }
                    // rec = busy reconciles (cast/item-action), fcl = force-clears.
                    // Soak health: rec climbs during combat/loot; fcl stays flat.
                    long rec = 0, fcl = 0;
                    try { rec = BusyCountHooks.ReconcileCount; fcl = BusyCountHooks.ForceClearCount; } catch { }
                    // idle = the client's own input-idle clock (diag/logoff-origin); the retail
                    // client logs itself off past InactiveTimeBeforeLogout. Omitted when unknown.
                    int idle = -1;
                    try { idle = LogoffOriginProbe.IdleSecondsForHeartbeat; } catch { }
                    string idleField = idle >= 0 ? $" idle={idle}s" : "";
                    // ui = ImGui cost on AC's render thread per frame, p95/max over the
                    // last second (docs/IMGUI_PARITY_PLAN.md §4.5). Only while ImGui is on.
                    string uiField = "";
                    try
                    {
                        if (Plugins.EngineSettings.EnableImGuiBackend)
                            uiField = $" ui={ImGuiBackend.UiFrameStats.P95Ms:0.0}/{ImGuiBackend.UiFrameStats.MaxMs:0.0}ms";
                    }
                    catch { }
                    // vafree = free address space (MB): reloads leak engine/plugin copies,
                    // so a client that dies mid-reload can be checked for running out.
                    // valargest = the largest free block (MB): a 32-bit client fails an
                    // allocation on fragmentation long before vafree reaches 0.
                    string vaField = "";
                    try
                    {
                        long free = AddressSpaceMb().FreeMb;
                        if (free >= 0) vaField = $" vafree={free}MB";
                        long largest = AddressSpace.FreeAndLargestMb().LargestFreeMb;
                        if (largest >= 0) vaField += $" valargest={largest}MB";
                    }
                    catch { }
                    // Crash logger module cache (native): rebuilt every 10 s here, so a fault
                    // never has to call the loader to name a module.
                    if (tick % 10 == 1)
                    {
                        try { SehTrampoline.RefreshCrashLoggerModules(); } catch { }
                    }
                    // Safety net for the main-thread message box count (MainThreadFreeze).
                    MessageBoxHooks.ReconcileStaleMainThreadCount();
                    // gateov=N = the world-transition gate's safety valve has opened N times (a death/portal
                    // that outlasted WorldTransitionState.MaxHoldMs); omitted while 0.
                    try { int gov = PlayerLifecycleLog.GateOverrides; if (gov > 0) vaField += $" gateov={gov}"; }
                    catch { }
                    // pump=paused = plugin ticks paused because AC's main thread is frozen
                    // (MainThreadFreeze); omitted while running.
                    try { if (MainThreadFreeze.Paused) vaField += $" pump=paused stall={MainThreadFreeze.MainThreadStallMs}ms"; }
                    catch { }
                    // req=<type>/<age>s = the client is waiting on an item request (it refuses
                    // every use/move/equip meanwhile); atk=1 = its attacking flag is set. Both
                    // omitted while open, so a long run of req= in a log is the item-action lock.
                    string gateField = ClientActionGates.HeartbeatField();
                    // vp=<mode>:skip|draw cpu=N% = the video patch (D3D9/VideoPatch) and this
                    // client's CPU share of all cores. Omitted while the patch is off; the patch
                    // also logs before/after averages around each mode change.
                    string vpField = "";
                    try { vpField = D3D9.VideoPatch.OnHeartbeat(draw); }
                    catch { }
                    // Unfiltered on purpose: it is the launcher's liveness signal (see RynthLog.Heartbeat).
                    RynthLog.Heartbeat($"hb #{tick} up={(nowMs - startMs) / 1000}s fps={fps} draw={draw} plug={pps}/s ws={wsMb}MB login={login} qdrop={dropped} qrej={rejected} rec={rec} fcl={fcl}{idleField}{uiField}{vaField}{gateField}{vpField}");

                    // Every 60 s: garbage collections and the time they paused the process.
                    // Under the CoreCLR host the engine and managed plugins share one GC, so a
                    // blocking gen2 stalls AC's main thread whenever it is in managed code.
                    if (tick % 60 == 0)
                        LogGcStats();

                    // Every 60 s: IdentifyObject (0xC8) sends by who asked, and AutoId's skips
                    // (AutoIdService.TakeMinuteLine). Its own line, never part of "hb #".
                    if (tick % 60 == 0)
                    {
                        try { RynthLog.Info(AutoIdService.TakeMinuteLine()); } catch { }
                    }

                    // Every 5 s: hide and destroy any panel window no live panel owns
                    // (the "double UI" orphans).
                    if (tick % 5 == 0)
                    {
                        try { RynthCore.Engine.UI.LayeredWindow.SweepOrphans(); } catch { }
                    }

                    // Cache this client's live metrics so the GetEngineStatusJson host bridge can serve
                    // them to a plugin (the RynthRemote status export). No file write, no networking.
                    EngineStatusMetrics.UpdateMetrics((nowMs - startMs) / 1000, fps, pps, wsMb,
                        login == 1, dropped, rec, fcl);
                }
                catch { /* never let the heartbeat itself bring anything down */ }
                // Self-healing: clear a stuck floating-panel click-through
                // (DockedPanelPointerCaptureActive whose disarm was lost ->
                // every floating panel left WS_EX_TRANSPARENT). Runs here on
                // purpose: independent of the input path that strands it.
                try { RynthCore.Engine.UI.AvaloniaOverlay.WatchdogClearStuckClickThrough(); }
                catch { /* never let the heartbeat itself bring anything down */ }
                // Mid-session log rotation (~once/minute): without it a long
                // soak grows RynthCore.<pid>.log without bound (startup-only
                // rotation never fires mid-session and is skipped on reload).
                if (tick % 60 == 0)
                {
                    try { LogPaths.RotateIfOversized(); }
                    catch { /* never let the heartbeat itself bring anything down */ }
                }
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

    private static int _gc0, _gc1, _gc2;
    private static TimeSpan _gcPause;

    private static void LogGcStats()
    {
        try
        {
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            TimeSpan pause = GC.GetTotalPauseDuration();
            GCMemoryInfo info = GC.GetGCMemoryInfo(GCKind.Any);
            RynthLog.Info($"gc: last 60s g0={g0 - _gc0} g1={g1 - _gc1} g2={g2 - _gc2} paused={(pause - _gcPause).TotalMilliseconds:0}ms " +
                          $"(last GC gen{info.Generation}{(info.Concurrent ? " background" : "")} {info.PauseDurations[0].TotalMilliseconds:0}ms) " +
                          $"heap={GC.GetTotalMemory(false) / 1048576}MB concurrent={System.Runtime.GCSettings.LatencyMode != System.Runtime.GCLatencyMode.Batch}");
            _gc0 = g0; _gc1 = g1; _gc2 = g2; _gcPause = pause;
        }
        catch { }
    }
}
