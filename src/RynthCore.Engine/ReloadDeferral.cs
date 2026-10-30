// ============================================================================
//  RynthCore.Engine - ReloadDeferral.cs
//  Holds a hot reload while a plugin says one now would hurt (2026-10-05).
//
//  Drakkon died on DreamWeave at 13:43:20 after the loader's file watcher saw a
//  new RynthCore.Engine.dll at 13:42:54 and reloaded the engine mid-fight: every
//  plugin was down for about 8 s, then RynthAi came back as if it had just logged
//  in. The loader (native\Loader, off-limits here) decides to reload and calls
//  RynthCoreShutdown on its own reload thread; nothing in the game waits on that
//  thread. So the engine's side of the handshake is: before tearing down, ask
//  every plugin's optional RynthPluginReloadBlocker export, and while one answers
//  with a reason, wait and ask again, up to Cap. The game keeps running on the
//  old engine meanwhile; the loader stages the new one after Shutdown returns, so
//  the newest file on disk is what loads.
//
//  Pure: the plugin query, the clock, the sleep and the log are injected, so the
//  loop is tested offline (tools\ReloadDeferralTests). EntryPoint wires it.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading;

namespace RynthCore.Engine;

internal sealed class ReloadDeferral
{
    /// <summary>Longest a reload waits for the plugins (then it goes ahead, and says so).</summary>
    public static readonly TimeSpan DefaultCap = TimeSpan.FromMinutes(2);
    /// <summary>How often the plugins are asked again while one blocks.</summary>
    public static readonly TimeSpan DefaultPoll = TimeSpan.FromMilliseconds(500);
    /// <summary>While waiting, the "still waiting" line repeats this often (the first line is at once).</summary>
    public static readonly TimeSpan DefaultLogEvery = TimeSpan.FromSeconds(15);

    public enum Outcome
    {
        /// <summary>No plugin objected: reload at once.</summary>
        Clear,
        /// <summary>A plugin objected, then cleared within the cap.</summary>
        ClearedAfterWait,
        /// <summary>Still objecting at the cap: reload anyway.</summary>
        TimedOut,
    }

    /// <summary>The plugins that object now, each with its reason (empty = safe).</summary>
    public required Func<IReadOnlyList<(string Plugin, string Reason)>> Query { get; init; }
    public required Action<string> Log { get; init; }
    /// <summary>Told once, when the wait starts (the engine puts it in chat).</summary>
    public Action<string>? Notify { get; init; }
    public Func<DateTime> Clock { get; init; } = () => DateTime.UtcNow;
    public Action<TimeSpan> Sleep { get; init; } = t => Thread.Sleep(t);
    public TimeSpan Cap { get; init; } = DefaultCap;
    public TimeSpan Poll { get; init; } = DefaultPoll;
    public TimeSpan LogEvery { get; init; } = DefaultLogEvery;

    /// <summary>Asks the plugins until none objects or <see cref="Cap"/> has passed.</summary>
    public Outcome Run(out TimeSpan waited)
    {
        DateTime start = Clock();
        waited = TimeSpan.Zero;
        var blockers = SafeQuery();
        if (blockers.Count == 0) return Outcome.Clear;

        string first = Describe(blockers);
        Log($"engine reload waiting: {first} (asking again every {Poll.TotalSeconds:0.#} s, at most {Cap.TotalSeconds:0} s).");
        try { Notify?.Invoke(first); } catch { }
        DateTime lastLog = start;

        while (true)
        {
            DateTime now = Clock();
            waited = now - start;
            if (waited >= Cap)
            {
                Log($"engine reload: waited {waited.TotalSeconds:0} s and {Describe(blockers)} still; reloading anyway.");
                return Outcome.TimedOut;
            }
            Sleep(Poll);
            blockers = SafeQuery();
            now = Clock();
            waited = now - start;
            if (blockers.Count == 0)
            {
                Log($"engine reload: the plugins are clear after {waited.TotalSeconds:0.0} s; reloading now.");
                return Outcome.ClearedAfterWait;
            }
            if (now - lastLog >= LogEvery)
            {
                lastLog = now;
                Log($"engine reload waiting: {Describe(blockers)} ({waited.TotalSeconds:0} s of at most {Cap.TotalSeconds:0} s).");
            }
        }
    }

    /// <summary>"RynthAi in combat (a monster engaged)", several joined with "; ".</summary>
    public static string Describe(IReadOnlyList<(string Plugin, string Reason)> blockers)
    {
        var parts = new List<string>(blockers.Count);
        foreach (var (plugin, reason) in blockers) parts.Add($"{plugin} {reason}");
        return string.Join("; ", parts);
    }

    // A plugin query that throws must never hold a reload: count it as "nothing objects".
    private IReadOnlyList<(string Plugin, string Reason)> SafeQuery()
    {
        try { return Query() ?? Array.Empty<(string, string)>(); }
        catch (Exception ex)
        {
            try { Log($"engine reload: asking the plugins failed ({ex.GetType().Name}: {ex.Message}); not waiting."); } catch { }
            return Array.Empty<(string, string)>();
        }
    }
}
