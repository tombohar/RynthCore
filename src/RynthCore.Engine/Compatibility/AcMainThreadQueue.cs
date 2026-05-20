using System.Threading;

namespace RynthCore.Engine.Compatibility;

// SPSC ring buffer: the plugin pump thread enqueues, AC's main thread drains.
// Fixed allocation at startup — zero heap allocation per enqueue/drain (so it
// is safe to drain from the EndScene reverse-P/Invoke; it does NOT reintroduce
// the GC-in-detour fail-fast class). Prevents the near-null WRITE AVs from
// calling AC state-mutating functions off the game thread (SelectItem /
// ClientMagicSystem::CastSpell from the pump → documented 0x0055FA24
// [null+0x40] / 0x00416C86 [null+0x28], minutes into a cast-heavy session).
internal static class AcMainThreadQueue
{
    private enum ActionKind : byte { SelectItem, CastSpell }

    private readonly struct Entry(ActionKind kind, uint a)
    {
        public readonly ActionKind Kind = kind;
        public readonly uint A = a; // SelectItem=objectId | CastSpell=spellId
    }

    private const int Capacity = 64; // must be power of 2
    private const int Mask = Capacity - 1;
    private static readonly Entry[] _slots = new Entry[Capacity];
    // Monotonic. Only the pump thread writes _tail; only the main thread writes _head.
    private static int _head;
    private static int _tail;

    // Called from the pump thread: queue a SelectItem + CastSpell pair
    // atomically. targetId=0 skips the SelectItem.
    // Returns true if enqueued; false if a cast is already outstanding or the
    // queue is full. The caller (CombatActionHooks.CastSpell) MUST return this
    // bool to the plugin: on false the plugin retries next tick instead of
    // setting _pendingSpellId and waiting for chat that won't be sent until the
    // queued entry drains.
    public static bool EnqueueCast(uint targetId, uint spellId)
    {
        int tail = _tail;               // only the pump thread writes _tail
        int head = Volatile.Read(ref _head);
        // Single-outstanding: if anything is still queued, refuse so the caller
        // returns false and the plugin retries next tick. With the EndScene
        // drain (~22-33Hz) the queue empties within ~1 frame, so this clears
        // promptly — it only throttles, never wedges.
        if (tail != head) return false;
        int needed = targetId != 0 ? 2 : 1;
        if (tail - head + needed > Capacity)
            return false; // full — drop the cast rather than risk an off-thread AC write

        if (targetId != 0)
        {
            _slots[tail & Mask] = new Entry(ActionKind.SelectItem, targetId);
            _slots[(tail + 1) & Mask] = new Entry(ActionKind.CastSpell, spellId);
            Volatile.Write(ref _tail, tail + 2);
        }
        else
        {
            _slots[tail & Mask] = new Entry(ActionKind.CastSpell, spellId);
            Volatile.Write(ref _tail, tail + 1);
        }
        return true;
    }

    // Called on AC's main thread from the ALWAYS-ON EndScene path
    // (EngineFrameController.OnEndScene) — fires every frame regardless of
    // idle / combat / EnableImGuiBackend (~22-33Hz in the user's env). It is
    // deliberately NOT drained from DispatchGameEventDetour: that detour is
    // server-packet-driven and effectively silent while the bot stands idle
    // self-buffing, which strands every queued cast forever and bricks casting
    // (a documented dead-end — do not move the drain there). No heap allocation.
    public static void Drain()
    {
        int head = _head;               // only the main thread writes _head
        int tail = Volatile.Read(ref _tail);
        while (head != tail)
        {
            Entry e = _slots[head & Mask];
            Volatile.Write(ref _head, ++head);

            if (e.Kind == ActionKind.SelectItem)
                ClientHelperHooks.SelectItem(e.A);
            else
                CombatActionHooks.ExecuteQueuedCast(e.A);

            tail = Volatile.Read(ref _tail);
        }
    }
}
