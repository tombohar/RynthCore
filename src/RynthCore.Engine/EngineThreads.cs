// ============================================================================
//  RynthCore.Engine - EngineThreads.cs
//
//  Every long-lived thread the engine starts goes through here, so shutdown can wake
//  them and join them. Under the CoreCLR host (docs/UNLOADABLE_ENGINE_PLAN.md) a
//  thread with a frame in engine code keeps the whole engine generation from
//  unloading; under NativeAOT it keeps running old code after a reload. Either way
//  EngineLifecycle.Shutdown must leave none behind.
//
//  Loops sleep with EngineThreads.Sleep(ms), which returns false (at once) when the
//  engine is shutting down: `if (!EngineThreads.Sleep(250)) return;`.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading;

namespace RynthCore.Engine;

internal static class EngineThreads
{
    private static readonly object Sync = new();
    private static readonly List<Thread> Threads = new();
    private static readonly ManualResetEvent StopEvent = new(false);

    /// <summary>True once shutdown has asked every engine thread to stop.</summary>
    public static bool Stopping => StopEvent.WaitOne(0);

    /// <summary>Starts a background thread the engine's shutdown will wait for.</summary>
    public static Thread Start(string name, ThreadStart body, ApartmentState apartment = ApartmentState.Unknown)
    {
        var thread = new Thread(body) { Name = name, IsBackground = true };
        if (apartment != ApartmentState.Unknown)
            thread.SetApartmentState(apartment);
        Track(thread);
        thread.Start();
        return thread;
    }

    /// <summary>Registers a thread created elsewhere (call before Start).</summary>
    public static void Track(Thread thread)
    {
        lock (Sync)
        {
            Threads.RemoveAll(t => t.ThreadState.HasFlag(ThreadState.Stopped));
            Threads.Add(thread);
        }
    }

    /// <summary>Sleeps; false (immediately) once the engine is shutting down.</summary>
    public static bool Sleep(int milliseconds) => !StopEvent.WaitOne(Math.Max(0, milliseconds));

    public static bool Sleep(TimeSpan duration) => Sleep((int)Math.Min(int.MaxValue, duration.TotalMilliseconds));

    /// <summary>
    /// Wakes every tracked thread and waits up to <paramref name="budgetMs"/> in total for
    /// them to finish. Logs the ones still running: each keeps its engine generation alive.
    /// </summary>
    public static void StopAndJoinAll(int budgetMs)
    {
        StopEvent.Set();
        List<Thread> threads;
        lock (Sync) threads = new List<Thread>(Threads);

        long deadline = Environment.TickCount64 + budgetMs;
        var stuck = new List<string>();
        foreach (Thread t in threads)
        {
            if (t == Thread.CurrentThread) continue;
            int remaining = (int)Math.Max(0, deadline - Environment.TickCount64);
            if (!t.Join(remaining))
                stuck.Add(t.Name ?? $"#{t.ManagedThreadId}");
        }
        RynthLog.Info(stuck.Count == 0
            ? $"EngineThreads: all {threads.Count} engine thread(s) finished."
            : $"EngineThreads: {stuck.Count} of {threads.Count} thread(s) still running after {budgetMs} ms: {string.Join(", ", stuck)}");
    }
}
