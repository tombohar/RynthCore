// ============================================================================
//  RynthCore.Engine - Compatibility/ActionRing.cs
//
//  The storage and admission policy of AcMainThreadQueue's action ring: a fixed ring of
//  entries, many producers (under one lock), ONE consumer (AC's main thread), FIFO.
//
//  Admission: during a world transition the drain holds the ring
//  (WorldTransitionState), and a bot keeps queueing starts at ~60 Hz meanwhile, so the
//  ring used to fill and then refuse the stops and cancels that matter most. Now:
//    - while the caller says a transition is on, non-teardown entries are refused at
//      once (they would be dropped when it ends anyway);
//    - the top TeardownReserve slots only take teardown entries (stops and cancels), so a
//      ring filled by starts before the transition was seen still takes a stop.
//  What counts as teardown is the caller's (AcMainThreadQueue.IsTeardownKind, the same
//  predicate its drain uses).
//
//  Pure: no engine state. Compiled into tools\EngineRobustnessTests.
// ============================================================================

using System.Threading;

namespace RynthCore.Engine.Compatibility;

internal sealed class ActionRing<T> where T : struct
{
    public enum Admit
    {
        Queued,
        /// <summary>No room (for a non-teardown entry: none below the teardown reserve).</summary>
        Full,
        /// <summary>A world transition is on and the entry is not teardown.</summary>
        RejectedTransition,
    }

    private readonly T[] _slots;
    private readonly int _mask;
    private readonly int _reserve;
    private readonly object _lock = new();
    // Monotonic counters. Producers advance _tail under _lock; the sole consumer advances _head.
    private int _head;
    private int _tail;

    public ActionRing(int capacityPowerOfTwo, int teardownReserve)
    {
        if (capacityPowerOfTwo <= 0 || (capacityPowerOfTwo & (capacityPowerOfTwo - 1)) != 0)
            throw new System.ArgumentOutOfRangeException(nameof(capacityPowerOfTwo));
        if (teardownReserve < 0 || teardownReserve >= capacityPowerOfTwo)
            throw new System.ArgumentOutOfRangeException(nameof(teardownReserve));
        _slots = new T[capacityPowerOfTwo];
        _mask = capacityPowerOfTwo - 1;
        _reserve = teardownReserve;
    }

    public int Capacity => _slots.Length;
    public int TeardownReserve => _reserve;

    /// <summary>Entries waiting (any thread; a snapshot).</summary>
    public int Count => Volatile.Read(ref _tail) - Volatile.Read(ref _head);

    /// <summary>Producer side, any thread.</summary>
    public Admit TryEnqueue(in T entry, bool teardown, bool inTransition)
    {
        lock (_lock)
        {
            if (inTransition && !teardown)
                return Admit.RejectedTransition;
            int tail = _tail;
            int used = tail - Volatile.Read(ref _head);
            int limit = teardown ? _slots.Length : _slots.Length - _reserve;
            if (used >= limit)
                return Admit.Full;
            _slots[tail & _mask] = entry;
            Volatile.Write(ref _tail, tail + 1);
            return Admit.Queued;
        }
    }

    /// <summary>Consumer only: the oldest entry, without removing it.</summary>
    public bool TryPeek(out T entry)
    {
        int head = _head;
        if (head == Volatile.Read(ref _tail))
        {
            entry = default;
            return false;
        }
        entry = _slots[head & _mask];
        return true;
    }

    /// <summary>
    /// Consumer only: the entry <paramref name="offset"/> places behind the oldest (0 = the
    /// oldest), without removing anything. False past the newest entry.
    /// </summary>
    public bool TryPeekAt(int offset, out T entry)
    {
        int head = _head;
        if (offset < 0 || offset >= Volatile.Read(ref _tail) - head)
        {
            entry = default;
            return false;
        }
        entry = _slots[(head + offset) & _mask];
        return true;
    }

    /// <summary>Consumer only: removes the oldest entry (after a successful TryPeek).</summary>
    public void Pop()
    {
        int head = _head;
        if (head == Volatile.Read(ref _tail)) return;
        _slots[head & _mask] = default;
        Volatile.Write(ref _head, head + 1);
    }

    /// <summary>Consumer only: removes and returns the oldest entry.</summary>
    public bool TryDequeue(out T entry)
    {
        if (!TryPeek(out entry)) return false;
        Pop();
        return true;
    }
}
