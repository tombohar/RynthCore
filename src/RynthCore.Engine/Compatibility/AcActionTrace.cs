// ============================================================================
//  AcActionTrace.cs — Temporary diagnostic for Class B AVs (AC UIElement::
//  GetAttribute_* AVs that fire with NO RynthCore frame on the faulting stack).
//
//  Lock-free 256-entry ring buffer that records every RynthCore-issued AC-
//  mutating call (SelectItem / UseObject / ChangeCombatMode / Cast / attacks)
//  with a timestamp. Dumped to the log by ProcessExitHooks.LogTermination on
//  any process-exit path so post-crash analysis shows the literal sequence of
//  switches RynthCore made in the milliseconds before death — tests the
//  "too many switches" hypothesis without behavioural change.
//
//  Zero per-call allocation: struct array preallocated at startup, lock-free
//  via Interlocked.Increment. Concurrent overwrites of the same slot are
//  tolerated (rare at depth 256; we only need the rough recent sequence).
//
//  REMOVE once Class B is diagnosed and fixed.
// ============================================================================

using System;
using System.Threading;

namespace RynthCore.Engine.Compatibility;

internal static class AcActionTrace
{
    private struct Entry
    {
        public long Ticks;   // DateTime.UtcNow.Ticks
        public string Name;  // string literal at call site → no per-call alloc
        public uint A;       // primary arg (typically objectId)
        public uint B;       // secondary arg (e.g. spellId, mode, attackHeight)
    }

    private const int Capacity = 256;       // power of 2 for cheap mask
    private const int Mask     = Capacity - 1;
    private static readonly Entry[] _slots = new Entry[Capacity];
    private static int _next;               // Interlocked-incremented counter

    /// <summary>
    /// Record an AC-mutating action at the engine callback entry. Safe from
    /// any thread; per call cost = one Interlocked.Increment + 4 struct field
    /// writes. Name MUST be a string literal (no per-call allocation).
    /// </summary>
    public static void Record(string name, uint a = 0, uint b = 0)
    {
        int slot = (Interlocked.Increment(ref _next) - 1) & Mask;
        _slots[slot].Ticks = DateTime.UtcNow.Ticks;
        _slots[slot].Name  = name;
        _slots[slot].A     = a;
        _slots[slot].B     = b;
    }

    /// <summary>
    /// Dump the buffer to the log. Called by ProcessExitHooks.LogTermination
    /// on every termination path so the action history is preserved across
    /// AV / fail-fast / clean exit.
    /// </summary>
    public static void DumpToLog()
    {
        try
        {
            int total = Volatile.Read(ref _next);
            int count = Math.Min(total, Capacity);
            int start = total - count;
            RynthLog.Info($"[AcActionTrace] dumping last {count} actions (total counter={total}):");
            for (int i = start; i < total; i++)
            {
                Entry e = _slots[i & Mask];
                if (e.Name == null) continue;
                DateTime dt = new DateTime(e.Ticks, DateTimeKind.Utc).ToLocalTime();
                RynthLog.Info($"[AcActionTrace]   {dt:HH:mm:ss.fff}  {e.Name}(0x{e.A:X8}, 0x{e.B:X8})");
            }
        }
        catch (Exception ex)
        {
            RynthLog.Info($"[AcActionTrace] dump exception: {ex.Message}");
        }
    }
}
