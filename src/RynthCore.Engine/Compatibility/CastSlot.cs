// ============================================================================
//  RynthCore.Engine - Compatibility/CastSlot.cs
//
//  AcMainThreadQueue's single-outstanding cast slot (SelectItem + CastSpell pair): one
//  pending cast at a time, taken by DrainCasts on AC's game-logic tick.
//
//  A cast is refused outright during a world transition (dead, in portal space, or the
//  settle window after a teleport): the drain would only hold it and then drop it, and
//  meanwhile the slot would refuse every newer cast. The caller (BuffManager /
//  CombatManager) already treats false as "retry next tick". Refused like the action ring
//  refuses non-teardown work during a transition (ActionRing.Admit.RejectedTransition).
//
//  Pure: no engine state, no locking (AcMainThreadQueue holds _castLock for every write;
//  DrainCasts reads Pending and QueuedAt with Volatile, hence public fields). Compiled
//  into tools\EngineRobustnessTests.
// ============================================================================

using System.Threading;

namespace RynthCore.Engine.Compatibility;

internal sealed class CastSlot
{
    public enum Admit
    {
        Queued,
        /// <summary>A cast is already pending; nothing changed.</summary>
        Busy,
        /// <summary>A world transition is in progress; nothing changed.</summary>
        RejectedTransition,
    }

    public int Pending;
    public uint TargetId;
    public uint SpellId;
    /// <summary>TickCount64 when the pending cast was queued.</summary>
    public long QueuedAt;
    /// <summary>
    /// The drain has counted this cast's wait for a movement stop queued before it (once per
    /// cast). Reset by every queued cast; set by DrainCasts on the main thread.
    /// </summary>
    public bool StopWaitNoted;

    /// <summary>Under the caller's lock. The slot is written only when this returns Queued.</summary>
    public Admit TryEnqueue(uint targetId, uint spellId, long nowMs, bool inTransition)
    {
        if (inTransition) return Admit.RejectedTransition;
        if (Pending != 0) return Admit.Busy;
        TargetId = targetId;
        SpellId = spellId;
        // Volatile: DrainCasts reads both without the lock, and a plain 64-bit store can
        // tear on x86. The time and the stop-wait reset first, then the flag that publishes
        // them (a release store, so the drain's Volatile.Read of Pending sees both).
        Volatile.Write(ref QueuedAt, nowMs);
        StopWaitNoted = false;
        Volatile.Write(ref Pending, 1);
        return Admit.Queued;
    }
}
