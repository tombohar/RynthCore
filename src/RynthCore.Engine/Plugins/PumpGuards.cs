// ============================================================================
//  RynthCore.Engine - Plugins/PumpGuards.cs
//
//  Small pieces for PluginManager.TickAll, the plugin pump thread and plugin shutdown.
//
//  StepFaultLog: TickAll's own steps (the main-thread freeze check, the delayed
//  self-identify, the Nav3D frame clear/commit, dropping failed plugins' script
//  windows, the plugin loop itself) are caught one by one, so nothing outside a
//  plugin's own Tick can take the pump down. A throwing step is logged with its
//  exception the first time, then only every RepeatEvery-th time with a count, so a
//  step that throws on every tick can't flood the log.
//
//  PumpDispatchInfo: what the plugin pump is calling right now (plugin, export, since
//  when), formatted for the hang watchdog. A plugin export that faults natively (an
//  access violation in a managed plugin's code) is fatal in .NET 10 and leaves only
//  "TickAll" in the event log; the process then sits ~12 s while createdump writes
//  the dump, the hang watchdog reports, and this line names the plugin
//  (2026-10-13 06:00: RynthAi's tick jumped to address 0).
//
//  PluginShutdownGate: plugins are shut down only once the pump has stopped (or
//  never ran), so no plugin's Shutdown runs alongside its Tick (audit 2026-10-21).
//
//  Pure: no engine state. Compiled into tools\EngineRobustnessTests.
// ============================================================================

namespace RynthCore.Engine.Plugins;

/// <summary>TickAll's own steps (not the plugins' ticks).</summary>
internal enum TickStep
{
    FreezeCheck,
    SelfIdentify,
    Nav3DClear,
    PluginLoop,
    DropFailedOwners,
    Nav3DCommit,
    Count,
}

/// <summary>
/// Counts throws per step and says when one should be written: the first, then every
/// <see cref="RepeatEvery"/>-th. Single-threaded by contract (the one pump thread).
/// </summary>
internal sealed class StepFaultLog
{
    public const int RepeatEvery = 1000;

    private readonly int[] _counts;

    public StepFaultLog(int steps) => _counts = new int[steps];

    /// <summary>Records one throw; true when this one should be logged.</summary>
    public bool Record(int step, out int count)
    {
        count = ++_counts[step];
        return count == 1 || count % RepeatEvery == 0;
    }

    public int CountOf(int step) => _counts[step];

    public static string Describe(TickStep step, string exceptionType, string message, int count) =>
        count == 1
            ? $"PluginManager.TickAll: {step} threw {exceptionType}: {message} - caught, the pump carries on (logged once; repeats are counted)."
            : $"PluginManager.TickAll: {step} threw {exceptionType}: {message} - {count} times so far, still caught.";
}

/// <summary>
/// The plugin call the pump is inside, for the hang watchdog. The pump thread writes
/// the fields, the watchdog reads them, unsynchronized: a torn read only garbles a
/// diagnostic line.
/// </summary>
internal static class PumpDispatchInfo
{
    /// <summary>
    /// "RynthAi Tick for 12375 ms", or null when no plugin call is in progress (or the
    /// one in progress started too recently to matter).
    /// </summary>
    public static string? Describe(string? plugin, string? export, long sinceMs, long nowMs, long minMs = 1000)
    {
        if (plugin == null || sinceMs == 0)
            return null;
        long ms = nowMs - sinceMs;
        if (ms < minMs)
            return null;
        return $"inside {plugin} {export ?? "call"} for {ms} ms";
    }
}

/// <summary>
/// Whether PluginManager.ShutdownAll may call the plugins' Shutdown now: never while the
/// plugin pump may still be inside a frame on its own thread, or a plugin's Tick and events
/// would run alongside its Shutdown, which frees their threads, textures and handles (a
/// native fault there can't be caught: audit 2026-10-21, 01 #1). EngineLifecycle.Shutdown
/// stops the pump before anything shuts a plugin down; this checks that the stop finished.
/// </summary>
internal static class PluginShutdownGate
{
    /// <summary>How long ShutdownAll waits for the pump on top of EngineLifecycle's own join.</summary>
    public const int ShutdownWaitMs = 5000;

    internal enum Verdict
    {
        /// <summary>No pump was started in this generation (plugins off, or before login).</summary>
        NoPump,
        /// <summary>The pump has left its loop: nothing ticks the plugins any more.</summary>
        PumpStopped,
        /// <summary>Called on the pump thread itself: no frame of it can run alongside.</summary>
        OnPumpThread,
        /// <summary>The pump is still running (a frame outlasted the wait): leave the plugins alone.</summary>
        PumpStillRunning,
    }

    /// <summary>
    /// <paramref name="pumpStarted"/>: a pump thread was started; <paramref name="pumpExited"/>:
    /// it has left its loop (after the stop and the wait); <paramref name="onPumpThread"/>: the
    /// caller is that thread.
    /// </summary>
    public static Verdict Decide(bool pumpStarted, bool pumpExited, bool onPumpThread) =>
        !pumpStarted ? Verdict.NoPump
        : pumpExited ? Verdict.PumpStopped
        : onPumpThread ? Verdict.OnPumpThread
        : Verdict.PumpStillRunning;

    public static bool MayShutDownPlugins(Verdict verdict) => verdict != Verdict.PumpStillRunning;

    /// <summary>The log line when ShutdownAll leaves the plugins alone.</summary>
    public static string DescribeRefusal(int plugins, string? pumpInside) =>
        $"PluginManager: the plugin pump is still running ({pumpInside ?? "no plugin call named"}) - " +
        $"{plugins} plugin(s) NOT shut down now, so no Shutdown runs alongside a Tick. " +
        "The engine's teardown tries once more after its other steps.";
}
