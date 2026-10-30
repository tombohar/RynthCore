// ============================================================================
//  RynthCore.Engine - Compatibility/PendingIdQueue.cs
//
//  The pending queue behind AcMainThreadQueue's RequestId (0xC8 appraisal) slot.
//  A bounded FIFO of (object id, enqueue time, login epoch) that MERGES a request
//  for an object already waiting, the way the QueryHealth slot does: one appraisal
//  reply answers every caller that asked for it. Before this, AutoId's priority
//  appraisal, its create backlog and the plugins' RequestIdFn each queued their own
//  copy; one dump had 437 of 3211 identifies repeat an object within 125 ms.
//
//  A pending entry only absorbs a new request while it is from the same login and
//  young enough that the drain will still send it (MergeMaxAgeMs). An entry the drain
//  would drop as stale, or one from an earlier login, takes no merges: the new
//  request is queued on its own so it is not lost with the old one.
//
//  Pure data structure: no engine state, no locking (the caller holds its lock), and
//  no allocation after construction (a fixed ring). Compiled into
//  tools\EngineRobustnessTests.
// ============================================================================

namespace RynthCore.Engine.Compatibility;

internal sealed class PendingIdQueue
{
    public enum Result
    {
        /// <summary>Added as a new pending request.</summary>
        Queued,
        /// <summary>The object already had a pending request; nothing added.</summary>
        Merged,
        /// <summary>The queue is full; the request was not taken.</summary>
        Full,
    }

    public readonly struct Item
    {
        public readonly uint Id;
        public readonly long At;
        public readonly int Epoch;
        /// <summary>Who asked (Net.IdRequester as a byte): the first request's, a merge keeps it.</summary>
        public readonly byte Source;

        public Item(uint id, long at, int epoch, byte source = 0)
        {
            Id = id;
            At = at;
            Epoch = epoch;
            Source = source;
        }
    }

    private readonly Item[] _ring;
    private int _head;    // index of the oldest item
    private int _count;

    public PendingIdQueue(int capacity)
    {
        if (capacity <= 0) throw new System.ArgumentOutOfRangeException(nameof(capacity));
        _ring = new Item[capacity];
    }

    public int Count => _count;
    public int Capacity => _ring.Length;

    /// <summary>Requests absorbed by an already-pending entry since construction.</summary>
    public long MergedTotal { get; private set; }

    /// <summary>
    /// Queue a request for <paramref name="id"/>, or merge it into a pending one for the
    /// same object from the same <paramref name="epoch"/> that is at most
    /// <paramref name="mergeMaxAgeMs"/> old at <paramref name="nowMs"/> and was queued AFTER
    /// <paramref name="mergeOnlyAfterMs"/> (the last moment a world transition held the
    /// drains: an entry from before then is dropped when the drain reaches it, and a request
    /// merged into it would be lost with it).
    /// </summary>
    public Result Enqueue(uint id, long nowMs, int epoch, long mergeMaxAgeMs, long mergeOnlyAfterMs = 0, byte source = 0)
    {
        for (int i = 0; i < _count; i++)
        {
            ref readonly Item q = ref _ring[(_head + i) % _ring.Length];
            if (q.Id == id && q.Epoch == epoch && nowMs - q.At <= mergeMaxAgeMs
                && (mergeOnlyAfterMs <= 0 || q.At > mergeOnlyAfterMs))   // 0 = no transition: no floor
            {
                MergedTotal++;
                return Result.Merged;
            }
        }
        if (_count >= _ring.Length)
            return Result.Full;
        _ring[(_head + _count) % _ring.Length] = new Item(id, nowMs, epoch, source);
        _count++;
        return Result.Queued;
    }

    public bool TryDequeue(out Item item)
    {
        if (_count == 0)
        {
            item = default;
            return false;
        }
        item = _ring[_head];
        _ring[_head] = default;
        _head = (_head + 1) % _ring.Length;
        _count--;
        return true;
    }

    public void Clear()
    {
        System.Array.Clear(_ring);
        _head = 0;
        _count = 0;
    }
}
