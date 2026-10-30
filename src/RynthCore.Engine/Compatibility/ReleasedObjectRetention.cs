// ============================================================================
//  RynthCore.Engine - Compatibility/ReleasedObjectRetention.cs
//
//  How long AppraisalHooks keeps an object's identify data after the CLIENT released it
//  (AC's BeingDeleted, which fires for every object leaving the client's view).
//
//  Before: the data was evicted on every client release, so when the
//  object came back into view AutoId found nothing cached and appraised it again: the
//  10-minute re-appraisal window (AutoIdEligibility) almost never applied. Now the data is
//  kept for RetainMs after a client release and dropped:
//    - when RetainMs has passed (Expire, from AutoIdService's timer);
//    - when the SERVER deleted the object (0xF747; AppraisalHooks.EvictObject);
//    - at a session clear (logout), and under AppraisalHooks' object cap.
//  A re-create (TakeLive) makes the object current again, but data kept for StaleAfterMs or
//  longer is dropped then (review fix 2026-10-18): the client sent no updates for an object it
//  had released, so a stack count or a structure kept that long may be minutes old. A re-create
//  after a short absence (walking out of and back into view) still finds its data.
//
//  Pure bookkeeping (released ids and when); no locking (AppraisalHooks holds its lock).
//  Compiled into tools\EngineRobustnessTests.
// ============================================================================

using System.Collections.Generic;

namespace RynthCore.Engine.Compatibility;

internal sealed class ReleasedObjectRetention
{
    public const long DefaultRetainMs = 10L * 60 * 1000;
    public const int DefaultMaxReleased = 4096;

    /// <summary>A re-create this long after the release no longer trusts the kept data.</summary>
    public const long StaleAfterMs = 2L * 60 * 1000;

    private readonly Dictionary<uint, long> _releasedAt;
    private readonly long _retainMs;
    private readonly int _max;

    public ReleasedObjectRetention(long retainMs = DefaultRetainMs, int maxReleased = DefaultMaxReleased)
    {
        _retainMs = retainMs;
        _max = maxReleased;
        _releasedAt = new Dictionary<uint, long>(maxReleased + 1);
    }

    public int Count => _releasedAt.Count;
    public long RetainMs => _retainMs;

    /// <summary>
    /// The client released <paramref name="id"/> (which has cached data). Returns true when the
    /// data is kept for now; false when it must be dropped at once (the bound is reached and
    /// nothing has expired to make room).
    /// </summary>
    public bool OnRelease(uint id, long nowMs)
    {
        if (_releasedAt.ContainsKey(id)) { _releasedAt[id] = nowMs; return true; }
        if (_releasedAt.Count >= _max) return false;
        _releasedAt[id] = nowMs == 0 ? 1 : nowMs;
        return true;
    }

    /// <summary>The object is current again (re-created), or its data was dropped for another reason.</summary>
    public void Forget(uint id) => _releasedAt.Remove(id);

    /// <summary>
    /// The object was created again. Returns true when it had been released for
    /// <paramref name="staleAfterMs"/> or longer (its kept data may be out of date: drop it);
    /// false when it was not released, or only briefly. Either way it is current again.
    /// </summary>
    public bool TakeLive(uint id, long nowMs, long staleAfterMs = StaleAfterMs)
    {
        if (!_releasedAt.Remove(id, out long at)) return false;
        return nowMs - at >= staleAfterMs;
    }

    public bool IsReleased(uint id) => _releasedAt.ContainsKey(id);

    /// <summary>Released ids whose data has outlived RetainMs: removed here and added to <paramref name="expired"/>.</summary>
    public int Expire(long nowMs, List<uint> expired)
    {
        int n = 0;
        foreach (KeyValuePair<uint, long> kv in _releasedAt)
        {
            if (nowMs - kv.Value < _retainMs) continue;
            expired.Add(kv.Key);
            n++;
        }
        for (int i = expired.Count - n; i < expired.Count; i++)
            _releasedAt.Remove(expired[i]);
        return n;
    }

    public void Clear() => _releasedAt.Clear();
}
