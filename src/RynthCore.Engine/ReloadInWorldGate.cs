// ============================================================================
//  RynthCore.Engine - ReloadInWorldGate.cs
//  Optionally holds an automatic hot reload while the player is in the world.
//
//  From SilentKelpie's RynthCore PR 6 (2026-10-16), reworked 2026-10-18 into an opt-in:
//  stacked FileWatcher reloads mid-session were killing clients on his machine: the loader
//  shut the engine down, then CoreCLR re-init AVed / never logged "Reload: complete".
//  ReloadDeferral only waits for plugin blockers; it does not stop a quiet in-world unload.
//  This gate waits with the old engine still mapped until the player is out of the world.
//
//  Differences from the PR, all the owner's decisions:
//    - OFF by default: engine.json "HoldReloadInWorld" (Settings > Misc, "Hold reloads while in
//      the world"). Off, nothing here runs and a reload behaves exactly as before.
//    - A reload asked for by hand (ERl, the bar RL button, /rc rl, the hotkey) is never held;
//      only automatic ones (the file watcher, an engine deploy) are.
//    - It never holds forever: after "HoldReloadInWorldMaxMinutes" (default 10) it gives up,
//      logs once that it is reloading anyway, and the reload goes ahead.
//
//  Pure: the "still hold?" query, clock, sleep and log are injected so tools\ReloadInWorldGateTests
//  can run offline. EntryPoint wires it from DeferReloadWhilePluginsObject.
// ============================================================================

using System;
using System.Threading;

namespace RynthCore.Engine;

internal static class ReloadInWorldGate
{
    /// <summary>How often we ask again while holding for the world.</summary>
    public static readonly TimeSpan DefaultPoll = TimeSpan.FromSeconds(2);
    /// <summary>While waiting, the "still holding" line repeats this often (first line at once).</summary>
    public static readonly TimeSpan DefaultLogEvery = TimeSpan.FromSeconds(30);

    /// <summary>Default for engine.json "HoldReloadInWorldMaxMinutes".</summary>
    public const int DefaultMaxMinutes = 10;
    public const int MinMaxMinutes = 1;
    public const int MaxMaxMinutes = 240;

    /// <summary>Keeps a configured bound inside its valid range (1-240 minutes).</summary>
    public static int ClampMaxMinutes(int minutes) => Math.Clamp(minutes, MinMaxMinutes, MaxMaxMinutes);

    public enum Outcome
    {
        /// <summary>Not holding (off, by hand, or not in the world): reload at once.</summary>
        Clear,
        /// <summary>Was in the world, then left (or the hold was switched off / bypassed by hand); reload now.</summary>
        ClearedAfterWait,
        /// <summary>Still in the world at the bound: the hold gave up, reload anyway.</summary>
        TimedOut,
    }

    /// <summary>
    /// True when a reload should wait: the hold is switched on, the client is in the world, and
    /// the reload is an automatic one. A reload asked for by hand is never held.
    /// </summary>
    public static bool ShouldHold(bool byHand, bool holdEnabled, bool inWorld)
        => holdEnabled && inWorld && !byHand;

    /// <summary>
    /// Polls <paramref name="stillHold"/> until it is false or <paramref name="maxWait"/> has passed
    /// (then <see cref="Outcome.TimedOut"/>: the caller reloads anyway). A query that throws counts
    /// as "not holding", so a broken gate cannot wedge the client.
    /// </summary>
    public static Outcome Run(
        Func<bool> stillHold,
        Action<string> log,
        Action<string>? notify,
        TimeSpan maxWait,
        out TimeSpan waited,
        Func<DateTime>? clock = null,
        Action<TimeSpan>? sleep = null,
        TimeSpan? poll = null,
        TimeSpan? logEvery = null)
    {
        clock ??= static () => DateTime.UtcNow;
        sleep ??= static t => Thread.Sleep(t);
        TimeSpan pollSpan = poll ?? DefaultPoll;
        TimeSpan logSpan = logEvery ?? DefaultLogEvery;

        waited = TimeSpan.Zero;
        if (!SafeHold(stillHold)) return Outcome.Clear;

        DateTime start = clock();
        log($"engine reload waiting: player is in the world (Hold reloads while in the world is on; loads after logout / character select, or after at most {maxWait.TotalMinutes:0.#} min).");
        try
        {
            notify?.Invoke("player is in the world");
        }
        catch { /* chat must not abort the wait */ }
        DateTime lastLog = start;

        while (true)
        {
            // Never sleep past the bound: the give-up happens on time, not up to a poll late.
            TimeSpan left = maxWait - (clock() - start);
            sleep(left > TimeSpan.Zero && left < pollSpan ? left : pollSpan);
            DateTime now = clock();
            waited = now - start;
            if (!SafeHold(stillHold))
            {
                log($"engine reload: hold released after {waited.TotalSeconds:0.0} s (left the world, or reload asked for by hand, or the hold was switched off); reloading now.");
                return Outcome.ClearedAfterWait;
            }
            if (waited >= maxWait)
            {
                log($"engine reload: held {waited.TotalMinutes:0.#} min, reloading anyway (still in the world at the HoldReloadInWorldMaxMinutes bound of {maxWait.TotalMinutes:0.#} min).");
                return Outcome.TimedOut;
            }
            if (now - lastLog >= logSpan)
            {
                lastLog = now;
                log($"engine reload waiting: still in the world ({waited.TotalSeconds:0} s so far; loads after logout / character select, or at {maxWait.TotalMinutes:0.#} min).");
            }
        }
    }

    private static bool SafeHold(Func<bool> stillHold)
    {
        try { return stillHold(); }
        catch { return false; } // fail open: a broken query must not wedge the client
    }
}
