// ============================================================================
//  RynthCore.Engine - MainThreadFreeze.cs
//  The plugin pump's freeze gate (rule and reasons: MainThreadFreezeRule.cs).
//  PluginManager.TickAll asks ShouldPausePluginTick once per pump tick; while it
//  says yes, no plugin Tick runs (no decisions, no actions), while event delivery
//  (ProcessPendingActions: observing) carries on.
//
//  Plugins can read the state through GetEngineStatusJson (additive fields, no API
//  version change): mainThreadFrozen, mainThreadStallMs, pluginTicksPausedMs (a running
//  total, so a stall detector can subtract the paused time from what it measured).
// ============================================================================

using System;
using System.Threading;

namespace RynthCore.Engine;

internal static class MainThreadFreeze
{
    private static readonly PumpPauseTracker Tracker = new();   // pump thread only
    private static int _pausedFlag;                              // published copies, any thread
    private static long _pausedTotalMs, _pausedSinceMs;

    /// <summary>Plugin ticks are paused right now. Any thread.</summary>
    public static bool Paused => Volatile.Read(ref _pausedFlag) != 0;

    /// <summary>Total time plugin ticks have been paused, a pause in progress included. Any thread.</summary>
    public static long PausedTotalMs
    {
        get
        {
            long total = Interlocked.Read(ref _pausedTotalMs);
            long since = Interlocked.Read(ref _pausedSinceMs);
            return Paused && since != 0 ? total + Math.Max(0, Environment.TickCount64 - since) : total;
        }
    }

    /// <summary>How long AC's main thread has gone without a beat (0 before the first one). Any thread.</summary>
    public static long MainThreadStallMs =>
        MainThreadHangWatchdog.HasBeaten ? Math.Max(0, Environment.TickCount64 - MainThreadHangWatchdog.LastBeatTick) : 0;

    /// <summary>Frozen right now by the rule (main-thread message box, or a stale beat). Any thread.</summary>
    public static bool IsFrozenNow(out int mainThreadBoxes, out long staleMs)
    {
        long now = Environment.TickCount64;
        mainThreadBoxes = Compatibility.MessageBoxHooks.MainThreadOpenCount;
        bool beaten = MainThreadHangWatchdog.HasBeaten;
        long last = MainThreadHangWatchdog.LastBeatTick;
        staleMs = beaten ? now - last : 0;
        return MainThreadFreezeRule.IsFrozen(now, last, beaten, mainThreadBoxes, MainThreadHangWatchdog.HangThresholdMs);
    }

    /// <summary>
    /// Pump thread, once per TickAll. True = skip this tick's plugin Ticks. Logs the pause and
    /// the resume. Never throws.
    /// </summary>
    public static bool ShouldPausePluginTick()
    {
        try
        {
            long now = Environment.TickCount64;
            bool frozen = IsFrozenNow(out int boxes, out long staleMs);
            PumpPauseTracker.Transition t = Tracker.Update(frozen, now);
            if (t == PumpPauseTracker.Transition.Paused)
            {
                Interlocked.Exchange(ref _pausedSinceMs, now);
                Volatile.Write(ref _pausedFlag, 1);
                string why = boxes > 0
                    ? $"a message box is open on its main thread ({Compatibility.MessageBoxHooks.Describe() ?? "?"})"
                    : $"no main-thread beat for {staleMs} ms";
                RynthLog.Warn($"PluginManager: AC's main thread is frozen ({why}) - plugin ticks PAUSED (events still delivered).");
            }
            else if (t == PumpPauseTracker.Transition.Resumed)
            {
                Interlocked.Exchange(ref _pausedTotalMs, Tracker.FinishedPausedMs);
                Volatile.Write(ref _pausedFlag, 0);
                long lasted = now - Tracker.PausedSinceMs;
                RynthLog.Info($"PluginManager: AC's main thread is back - plugin ticks resumed after {lasted} ms ({Tracker.TicksSkippedThisPause} tick(s) skipped, pause #{Tracker.Pauses}).");
            }
            return Tracker.Paused;
        }
        catch
        {
            return false;
        }
    }
}
