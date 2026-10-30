// ============================================================================
//  RynthCore.Engine - UI/Data/MetaStateClock.cs
//  The Meta panel's timers (2026-10-11): how long the meta has been in its
//  current state, and the Watchdog's countdown, from RynthAi's state-machine
//  snapshot ("snapshot" in RynthPluginGetMetaJson), counted on between
//  snapshots. No ImGui: tools/MetaEditTests compiles this file.
// ============================================================================

using System;

namespace RynthCore.Engine.UI.Data;

/// <summary>RynthAi's meta state machine as of the snapshot ("snapshot"): how long the current state
/// has run, and the Watchdog. Read-only for the faces.</summary>
internal sealed class MetaStateSnapshot
{
    public double SecondsInState { get; set; }
    public bool WatchdogActive { get; set; }
    public string WatchdogState { get; set; } = string.Empty;
    public double WatchdogSecondsRemaining { get; set; }
}

/// <summary>
/// Seconds in the current state and the Watchdog's countdown between snapshots (they come every
/// couple of seconds): counted on from the last one, so the Meta panel's timers tick each second.
/// A state picked in the panel starts at 0 until the next snapshot says otherwise.
/// </summary>
internal sealed class MetaStateClock
{
    private MetaStateSnapshot? _last;
    private long _atMs;

    public bool Known => _last != null;

    public void Snapshot(MetaStateSnapshot? snap, long nowMs)
    {
        _last = snap;
        _atMs = nowMs;
    }

    /// <summary>The panel set the state: its clock starts again.</summary>
    public void StateSet(long nowMs)
    {
        if (_last == null) return;
        _last = new MetaStateSnapshot { SecondsInState = 0 };
        _atMs = nowMs;
    }

    public double SecondsInState(long nowMs) =>
        _last == null ? 0 : _last.SecondsInState + Math.Max(0, nowMs - _atMs) / 1000.0;

    /// <summary>Seconds until the Watchdog fires (its state in <paramref name="state"/>); -1 when none is set.</summary>
    public double WatchdogLeft(long nowMs, out string state)
    {
        state = _last?.WatchdogState ?? string.Empty;
        if (_last == null || !_last.WatchdogActive) return -1;
        return Math.Max(0, _last.WatchdogSecondsRemaining - Math.Max(0, nowMs - _atMs) / 1000.0);
    }

    /// <summary>"42 s" under an hour (rules count seconds: "In state >= 300 s"), else "1:02:03".</summary>
    public static string Format(double seconds)
    {
        long s = (long)Math.Max(0, seconds);
        if (s < 3600) return s.ToString(System.Globalization.CultureInfo.InvariantCulture) + " s";
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}");
    }
}
