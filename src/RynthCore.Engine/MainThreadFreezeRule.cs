// ============================================================================
//  RynthCore.Engine - MainThreadFreezeRule.cs
//
//  When AC's main thread is frozen, plugin ticks (where plugins decide and act) are
//  paused; event delivery (observing) goes on. In one real case AC's
//  OUT OF MEMORY box held the main thread for 12 s while the plugin pump kept
//  ticking; the bot jumped and asked for Peace mode to break a "stuck cast", and
//  blamed the server, while nothing could reach the server at all.
//
//  Frozen means either of:
//    - a message box is open ON AC'S MAIN THREAD (MessageBoxHooks): modal, the main
//      thread is inside it from the first instant; or
//    - the main-thread heartbeat (EndScene / Client::UseTime beats) is at least the
//      hang watchdog's threshold old: the same signal MainThreadHangWatchdog uses.
//  Before the first beat nothing is frozen (the main thread is not known yet).
//
//  PumpPauseTracker keeps the pause transitions and how long plugin ticks have been
//  paused in total, which plugins can read (GetEngineStatusJson) to discount frozen
//  time from their own stall detectors.
//
//  Pure: no engine state. Compiled into tools\EngineRobustnessTests.
// ============================================================================

namespace RynthCore.Engine;

internal static class MainThreadFreezeRule
{
    public static bool IsFrozen(long nowMs, long lastBeatMs, bool beatSeen, int mainThreadMessageBoxes, long thresholdMs)
    {
        if (mainThreadMessageBoxes > 0)
            return true;
        return beatSeen && nowMs - lastBeatMs >= thresholdMs;
    }
}

/// <summary>
/// Is a message box AC's OUT OF MEMORY box (the one that gets a full-memory dump)? Only on
/// AC's main thread, and only AC's own text, matched exactly at the start (ordinal): a plugin
/// box or a chat line that merely mentions memory must never cost a multi-gigabyte dump.
/// </summary>
internal static class OutOfMemoryBoxRule
{
    public const string Prefix = "OUT OF MEMORY: Failed to allocate";

    public static bool IsOutOfMemoryBox(string? body, bool onMainThread) =>
        onMainThread && body != null && body.StartsWith(Prefix, System.StringComparison.Ordinal);
}

/// <summary>Single-writer (the plugin pump). Not thread-safe; the engine glue publishes copies.</summary>
internal sealed class PumpPauseTracker
{
    public enum Transition { None, Paused, Resumed }

    public bool Paused { get; private set; }
    public long PausedSinceMs { get; private set; }
    /// <summary>Paused time of finished pauses (the current one is added by <see cref="TotalPausedMs"/>).</summary>
    public long FinishedPausedMs { get; private set; }
    public int Pauses { get; private set; }
    public long TicksSkippedThisPause { get; private set; }
    public long TicksSkippedTotal { get; private set; }

    /// <summary>
    /// Feed one pump tick. Returns the transition, if any. While paused the tick is to be
    /// skipped (and is counted as skipped).
    /// </summary>
    public Transition Update(bool frozen, long nowMs)
    {
        if (frozen)
        {
            Transition t = Transition.None;
            if (!Paused)
            {
                Paused = true;
                PausedSinceMs = nowMs;
                Pauses++;
                TicksSkippedThisPause = 0;
                t = Transition.Paused;
            }
            TicksSkippedThisPause++;
            TicksSkippedTotal++;
            return t;
        }
        if (Paused)
        {
            Paused = false;
            long d = nowMs - PausedSinceMs;
            if (d > 0) FinishedPausedMs += d;
            return Transition.Resumed;
        }
        return Transition.None;
    }

    /// <summary>Total time plugin ticks have been paused, including a pause in progress.</summary>
    public long TotalPausedMs(long nowMs) =>
        FinishedPausedMs + (Paused && nowMs > PausedSinceMs ? nowMs - PausedSinceMs : 0);
}
