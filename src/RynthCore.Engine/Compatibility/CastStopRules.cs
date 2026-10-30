// ============================================================================
//  RynthCore.Engine - Compatibility/CastStopRules.cs
//
//  The cast drain's question about the action ring: is a movement stop queued at or
//  before the pending cast? (AcMainThreadQueue.StopQueuedAtOrBefore: such a stop runs
//  first, so the cast's own stop is not sent while AC's locomotion is still on.)
//
//  Every queued entry is checked. An entry's time is taken before ActionRing's producer
//  lock, so two producers can land slightly out of time order, and a walk that stopped
//  at the first entry newer than the cast could miss an older stop queued behind it.
//
//  Pure: no engine state. Consumer side of the ring only (AC's main thread). Compiled
//  into tools\EngineRobustnessTests.
// ============================================================================

using System;

namespace RynthCore.Engine.Compatibility;

internal static class CastStopRules
{
    /// <summary>
    /// True when any entry in <paramref name="ring"/> was queued at or before
    /// <paramref name="castQueuedAt"/> (by <paramref name="queuedAt"/>) and is a movement stop
    /// (by <paramref name="isStop"/>). Pass static lambdas: nothing is allocated per call.
    /// </summary>
    public static bool AnyStopAtOrBefore<T>(ActionRing<T> ring, long castQueuedAt,
                                            Func<T, long> queuedAt, Func<T, bool> isStop) where T : struct
    {
        int count = ring.Count;
        for (int i = 0; i < count; i++)
        {
            if (!ring.TryPeekAt(i, out T e)) break;
            if (queuedAt(e) <= castQueuedAt && isStop(e)) return true;
        }
        return false;
    }
}
