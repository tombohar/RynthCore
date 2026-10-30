// ============================================================================
//  RynthCore.Engine - UI/Data/UiDataHub.cs
//  The one fetcher for panel data (docs/IMGUI_PARITY_PLAN.md §4.3).
//
//  Step() runs on the plugin pump thread right after PluginManager.TickAll,
//  so every plugin export a panel needs is called on the same thread as the
//  plugin's own tick and object events: no race with the plugin, and exports
//  that free their previous return buffer on the next call never see two
//  callers. Panels (either face) only read published snapshots and post
//  commands; they never call a plugin export themselves.
//
//  Commands (mutating calls: toggles, settings writes, nav edits) are queued
//  from any thread and executed here, before the sources poll, so the next
//  snapshot already reflects them.
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.UI.Data;

internal static class UiDataHub
{
    private const int MaxQueuedCommands = 256;
    private const double CommandBudgetMs = 2.0;
    /// <summary>A step slower than this is logged (at most once a minute).</summary>
    private const double StepWarnMs = 4.0;

    private static UiSource[] _sources = Array.Empty<UiSource>();
    private static readonly object RegisterSync = new();
    private static readonly ConcurrentQueue<(string Label, Action Action)> _commands = new();
    private static int _queued;
    private static volatile bool _armed = true;
    private static volatile bool _resetPending;
    private static long _lastSlowLogTicks;
    private static int _droppedCommands;

    static UiDataHub()
    {
        // Raised on the pump thread after every plugin is shut down (RL /
        // rescan). Sources re-bind to the fresh plugin copies on their next poll.
        PluginManager.PluginsUnloaded += () => _resetPending = true;
    }

    /// <summary>Adds a source. Call at init (any thread); sources are never removed.</summary>
    public static void Register(UiSource source)
    {
        lock (RegisterSync)
        {
            var next = new UiSource[_sources.Length + 1];
            Array.Copy(_sources, next, _sources.Length);
            next[^1] = source;
            Volatile.Write(ref _sources, next);
        }
    }

    /// <summary>
    /// Queues <paramref name="action"/> to run on the pump thread at the next
    /// step. Any thread. Returns false (and logs) when the hub is shutting down
    /// or the queue is full; commands are never run after shutdown begins.
    /// </summary>
    public static bool Post(string label, Action action)
    {
        if (!_armed)
        {
            RynthLog.UI($"UiDataHub: '{label}' dropped - shutting down.");
            return false;
        }
        if (Interlocked.Increment(ref _queued) > MaxQueuedCommands)
        {
            Interlocked.Decrement(ref _queued);
            if (Interlocked.Increment(ref _droppedCommands) <= 20)
                RynthLog.UI($"UiDataHub: '{label}' dropped - command queue full.");
            return false;
        }
        _commands.Enqueue((label, action));
        return true;
    }

    /// <summary>Stops accepting commands and drops the queued ones. Engine shutdown.</summary>
    public static void Disarm()
    {
        _armed = false;
        int dropped = 0;
        while (_commands.TryDequeue(out _)) dropped++;
        Interlocked.Exchange(ref _queued, 0);
        if (dropped > 0)
            RynthLog.UI($"UiDataHub: disarmed, {dropped} queued command(s) dropped.");
    }

    /// <summary>Pump thread only, right after PluginManager.TickAll.</summary>
    public static void Step()
    {
        if (!_armed) return;
        long start = Stopwatch.GetTimestamp();
        UiSource[] sources = Volatile.Read(ref _sources);

        if (_resetPending)
        {
            _resetPending = false;
            foreach (UiSource source in sources)
            {
                try { source.Reset(); }
                catch (Exception ex) { RynthLog.UI($"UiDataHub: {source.Name}.Reset threw {ex.GetType().Name}: {ex.Message}"); }
            }
        }

        // A refresh deferred by a command in the previous step: the plugin has
        // ticked since, so it has applied that command.
        foreach (UiSource source in sources)
            source.PromoteDeferredRefresh();

        // Commands first, so this step's polls already see their effect. A long
        // burst yields after the budget; the rest run next step.
        long commandDeadline = start + (long)(Stopwatch.Frequency * CommandBudgetMs / 1000);
        while (_commands.TryDequeue(out var command))
        {
            Interlocked.Decrement(ref _queued);
            try { command.Action(); }
            catch (Exception ex) { RynthLog.UI($"UiDataHub: command '{command.Label}' threw {ex.GetType().Name}: {ex.Message}"); }
            if (Stopwatch.GetTimestamp() > commandDeadline) break;
        }

        string? slowest = null;
        long slowestTicks = 0;
        foreach (UiSource source in sources)
        {
            long now = Stopwatch.GetTimestamp();
            if (!source.HasSubscribers || !source.IsDue(now)) continue;
            try { source.Poll(); }
            catch (Exception ex) { RynthLog.UI($"UiDataHub: {source.Name}.Poll threw {ex.GetType().Name}: {ex.Message}"); }
            long after = Stopwatch.GetTimestamp();
            source.MarkPolled(after, after - now); // an expensive poll backs off (UiSource.CostBackoff)
            if (after - now > slowestTicks) { slowestTicks = after - now; slowest = source.Name; }
        }

        long total = Stopwatch.GetTimestamp() - start;
        if (total > Stopwatch.Frequency * StepWarnMs / 1000 && total > 0)
        {
            long nowTicks = Stopwatch.GetTimestamp();
            if (nowTicks - _lastSlowLogTicks > Stopwatch.Frequency * 60)
            {
                _lastSlowLogTicks = nowTicks;
                double toMs = 1000.0 / Stopwatch.Frequency;
                RynthLog.UI($"UiDataHub: slow step {total * toMs:0.0} ms (slowest source {slowest ?? "-"} {slowestTicks * toMs:0.0} ms). The plugin tick waits for this.");
            }
        }
    }
}
