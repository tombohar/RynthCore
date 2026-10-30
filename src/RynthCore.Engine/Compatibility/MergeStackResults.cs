using System;
using System.Collections.Generic;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// Outcome of the most recent <see cref="ClientHelperHooks.MergeStackInternal"/> request per
/// (source, target) pair, so a plugin can learn what happened on AC's main thread instead of
/// only "queued". Before this, a merge the engine skipped (target already full) or that AC
/// rejected looked identical to one in flight, and RynthAi's AutoStack waited 10 s per pair
/// and logged a "confirmed failure". Thread-safe; bounded.
/// </summary>
internal static class MergeStackResults
{
    // ABI status codes (GetMergeStackResult return value). Never renumber.
    public const int None        = 0; // no request recorded for this pair (or it aged out)
    public const int Queued      = 1; // accepted onto the main-thread queue, not executed yet
    public const int Sent        = 2; // Event_StackableMerge sent for 'amount' units
    public const int TargetFull  = 3; // skipped: target stack already at max stack size
    public const int Failed      = 4; // invalid ids, merge API unavailable, AC returned 0, or threw
    public const int QueueFull   = 5; // main-thread queue full; request dropped before execution

    private struct Entry { public int Status; public int Amount; public long TickMs; }

    private const int MaxEntries = 512;          // prune threshold
    private const long PruneAgeMs = 120_000;     // results older than this are dropped on prune

    private static readonly object _lock = new();
    private static readonly Dictionary<ulong, Entry> _results = new();

    private static ulong Key(uint source, uint target) => ((ulong)source << 32) | target;

    /// <summary>Records the latest outcome for a pair (replaces any earlier one).</summary>
    public static void Record(uint source, uint target, int status, int amount = 0)
    {
        long now = Environment.TickCount64;
        lock (_lock)
        {
            if (_results.Count >= MaxEntries)
                Prune(now);
            _results[Key(source, target)] = new Entry { Status = status, Amount = amount, TickMs = now };
        }
    }

    /// <summary>Latest outcome for the pair, or <see cref="None"/>. <paramref name="amount"/>
    /// is the unit count sent (Sent only) and <paramref name="ageMs"/> how long ago it was recorded.</summary>
    public static int Get(uint source, uint target, out int amount, out int ageMs)
    {
        amount = 0;
        ageMs = 0;
        lock (_lock)
        {
            if (!_results.TryGetValue(Key(source, target), out Entry e))
                return None;
            amount = e.Amount;
            long age = Environment.TickCount64 - e.TickMs;
            ageMs = age > int.MaxValue ? int.MaxValue : (int)age;
            return e.Status;
        }
    }

    // Drops aged results; if everything is recent, clears the map (callers re-request anyway).
    private static void Prune(long now)
    {
        List<ulong>? old = null;
        foreach (var kv in _results)
            if (now - kv.Value.TickMs > PruneAgeMs)
                (old ??= new List<ulong>()).Add(kv.Key);
        if (old == null)
        {
            _results.Clear();
            return;
        }
        foreach (ulong k in old)
            _results.Remove(k);
    }
}
