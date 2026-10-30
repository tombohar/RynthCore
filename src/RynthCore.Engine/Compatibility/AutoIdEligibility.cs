// ============================================================================
//  RynthCore.Engine - Compatibility/AutoIdEligibility.cs
//
//  Which created objects AutoIdService appraises (the decision only; AutoIdService
//  does the queueing and the sends).
//
//  Before: a client-side release of an object (ACCObjectMaint::DeleteObject, or the
//  Decal bridge's delete event) forgot that it had been appraised, so every time the
//  client re-created it the engine appraised it again: some objects 16 times in 26
//  minutes. Now each object keeps the time its last appraisal was SENT, across client
//  releases, and a re-create inside the window (default 10 minutes) is not appraised
//  again, unless:
//    - the SERVER deleted the object (0xF747 ObjectDelete): it really went away, and
//      what comes back under that id is appraised fresh; or
//    - the appraisal data is no longer cached (the caller says so): re-appraising is
//      then the only way to get it back, so the window never leaves an object with
//      nothing cached.
//  A release before the queued appraisal went out records no time, so the next create
//  is appraised. A create while the object is still live is a duplicate and ignored.
//
//  Bounded: at most MaxTracked objects. Prune (from the drain timer, never the create
//  path) drops entries that no longer decide anything (released and outside the window
//  or never sent), then the oldest released ones. Past the bound a create is still
//  admitted, only not tracked.
//
//  Pure: no engine state, no locking (the caller locks), and no allocation after
//  construction (pre-sized; Dictionary.Remove during enumeration is allowed on
//  .NET Core 3+). Compiled into tools\EngineRobustnessTests.
// ============================================================================

using System.Collections.Generic;

namespace RynthCore.Engine.Compatibility;

internal sealed class AutoIdEligibility
{
    public const long DefaultWindowMs = 10L * 60 * 1000;
    public const int DefaultMaxTracked = 8192;

    public enum CreateDecision
    {
        /// <summary>Queue an appraisal.</summary>
        Appraise,
        /// <summary>Appraised recently and still cached: skip.</summary>
        SkipRecent,
        /// <summary>Already live (a second create for it): skip.</summary>
        SkipDuplicate,
    }

    private struct Entry
    {
        public bool Live;          // created and not released since
        public long LastSentMs;    // when its last appraisal was sent; 0 = none since tracking (re)started
    }

    private readonly Dictionary<uint, Entry> _map;
    private readonly long _windowMs;
    private readonly int _maxTracked;

    public AutoIdEligibility(long windowMs = DefaultWindowMs, int maxTracked = DefaultMaxTracked)
    {
        _windowMs = windowMs;
        _maxTracked = maxTracked;
        _map = new Dictionary<uint, Entry>(maxTracked + 1);
    }

    public int Count => _map.Count;
    public int MaxTracked => _maxTracked;
    public long WindowMs => _windowMs;

    /// <summary>Re-creates skipped because the last appraisal is recent and still cached.</summary>
    public long SkippedRecentTotal { get; private set; }

    /// <summary>Creates admitted while the map was full (appraised, not tracked).</summary>
    public long UntrackedAdmits { get; private set; }

    /// <summary>
    /// The client created <paramref name="id"/>. <paramref name="appraisalCached"/>: the
    /// engine still holds appraisal data for it (AppraisalHooks).
    /// </summary>
    public CreateDecision OnCreate(uint id, long nowMs, bool appraisalCached)
    {
        if (_map.TryGetValue(id, out Entry e))
        {
            if (e.Live)
                return CreateDecision.SkipDuplicate;
            e.Live = true;
            bool recent = e.LastSentMs != 0 && nowMs - e.LastSentMs < _windowMs;
            _map[id] = e;
            if (recent && appraisalCached)
            {
                SkippedRecentTotal++;
                return CreateDecision.SkipRecent;
            }
            return CreateDecision.Appraise;
        }

        if (_map.Count >= _maxTracked)
        {
            UntrackedAdmits++;
            return CreateDecision.Appraise;
        }
        _map[id] = new Entry { Live = true, LastSentMs = 0 };
        return CreateDecision.Appraise;
    }

    /// <summary>
    /// The appraisal for <paramref name="id"/> was sent: called from the main-thread drain
    /// after CombatActionHooks.RequestId succeeded, never at queue time, so a request the
    /// drain dropped (stale, a world transition, a new login) records nothing.
    /// </summary>
    public void OnSent(uint id, long nowMs)
    {
        if (_map.TryGetValue(id, out Entry e))
        {
            e.LastSentMs = nowMs == 0 ? 1 : nowMs;
            _map[id] = e;
        }
    }

    /// <summary>The client released <paramref name="id"/>; its appraisal time is kept.</summary>
    public void OnClientRelease(uint id)
    {
        if (_map.TryGetValue(id, out Entry e) && e.Live)
        {
            e.Live = false;
            _map[id] = e;
        }
    }

    /// <summary>
    /// The server deleted <paramref name="id"/>: the next create under that id is appraised.
    /// Liveness is left to the client's own release (it may come before or after this).
    /// </summary>
    public void OnServerDelete(uint id)
    {
        if (_map.TryGetValue(id, out Entry e))
        {
            e.LastSentMs = 0;
            _map[id] = e;
        }
    }

    /// <summary>True while <paramref name="id"/> is created and not released (or untracked).</summary>
    public bool IsLive(uint id) => !_map.TryGetValue(id, out Entry e) || e.Live;

    /// <summary>
    /// Keep the map under its bound. First drops entries that decide nothing any more
    /// (released, and never sent or outside the window), then, if still at the bound,
    /// the released entries sent longest ago. Live entries are never dropped.
    /// Returns how many were removed.
    /// </summary>
    public int Prune(long nowMs)
    {
        int removed = 0;
        foreach (KeyValuePair<uint, Entry> kv in _map)
        {
            Entry e = kv.Value;
            if (!e.Live && (e.LastSentMs == 0 || nowMs - e.LastSentMs >= _windowMs))
            {
                _map.Remove(kv.Key);
                removed++;
            }
        }

        // Still over three quarters full: drop released entries oldest first, in a few
        // passes with a shrinking age cut (window/2, /4, /8, then any age). One linear
        // pass each, so a prune never costs more than five walks of the map.
        int target = _maxTracked * 3 / 4;
        for (int pass = 1; pass <= 4 && _map.Count > target; pass++)
        {
            long minAge = pass < 4 ? _windowMs >> pass : long.MinValue;
            foreach (KeyValuePair<uint, Entry> kv in _map)
            {
                if (_map.Count <= target) break;
                if (kv.Value.Live) continue;
                if (pass < 4 && nowMs - kv.Value.LastSentMs < minAge) continue;
                _map.Remove(kv.Key);
                removed++;
            }
        }
        return removed;
    }

    /// <summary>
    /// Fix 2 rule for the selected-target priority appraisal: the target's appraisal is
    /// cached and was received less than <paramref name="windowMs"/> ago.
    /// </summary>
    public static bool IsAppraisalFresh(bool cached, long lastIdUnixSec, long nowUnixSec, long windowMs) =>
        cached && lastIdUnixSec > 0 && (nowUnixSec - lastIdUnixSec) * 1000 < windowMs;
}
