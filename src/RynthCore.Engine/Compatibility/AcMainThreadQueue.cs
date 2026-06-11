using System;
using System.Threading;

namespace RynthCore.Engine.Compatibility;

// Marshals off-thread bot ACTIONS onto AC's main (game) thread.
//
// AC is single-threaded and non-reentrant. The plugin pump (and chat-command /
// UI threads) run OFF AC's main thread; when they invoke AC state-mutating
// functions directly — combat-mode flips, melee/missile attacks, movement,
// object use — they race AC's own per-tick bookkeeping and corrupt its
// object / range lists. AC then access-violates LATER on its own thread during
// routine teardown / range recompute: the dump-verified object-teardown class
// 0x0055FA24 (List<ObjectRangeInfo>::remove via CPlayerSystem::CalculateObjectRangeChecks)
// and 0x00416C86 (DBOCache::DestroyObj). The per-callsite SEH trampoline only
// CONTAINS an AV that fires inside OUR call — it cannot stop AC tripping over
// corruption we left behind. The only real fix is to not mutate off-thread:
// enqueue here, execute on the main thread.
//
// Drained from the always-on EngineFrameController.OnEndScene tick (AC's main
// thread; ~22-33 Hz; fires regardless of EnableImGuiBackend / idle / combat).
//
// Multi-producer-safe: Enqueue takes a short lock (it runs on engine-owned
// threads, never inside an AC detour, so a lock is fine). Drain is the single
// consumer (the main thread) and is lock-free + zero-alloc, so it never blocks
// inside the reverse-P/Invoke detour and never reintroduces the GC-in-detour
// fail-fast class.
//
// The drain re-invokes the EXISTING public action methods. Each of those
// methods self-gates on MainThreadGuard.IsOnMainThread(): off-thread it
// enqueues here; on the main thread (i.e. when called from Drain, or from any
// legitimately main-thread caller) it executes the AC call directly. So there
// is no separate "Direct" body to keep in sync, and no risk of an enqueue loop.
//
// CASTS: routed via the separate cast slot below (EnqueueCast / DrainCasts),
// drained from AC's GAME-LOGIC tick (Client::UseTime, via GameTickHooks) — NOT
// EndScene. EndScene is the render phase and does not drive AC's cast state machine
// to completion (proven dead-end, reverted 2026-05-19); UseTime is where AC processes
// player actions, so the cast completes there. SelectItem is paired inside
// CombatActionHooks.CastSpell's main-thread path, so it stays ordered with the cast.
internal static class AcMainThreadQueue
{
    internal enum ActionKind : byte
    {
        ChangeCombatMode,
        MeleeAttack,
        MissileAttack,
        DoMovement,
        StopMovement,
        Jump,
        UseObject,
        // Item mutators marshalled 2026-06-05 (off-thread UseObjectOn/MoveItem/stack ops
        // raced AC's per-tick object graph -> null-deref AVs in Client::UseTime, e.g.
        // acclient+0xF24E0; same off-thread class as the P1 UseObject fix).
        UseObjectOn,
        UseEquippedItem,
        MoveItemExternal,
        MoveItemInternal,
        SplitStackInternal,
        MergeStackInternal,
    }

    // Four payload slots cover every routed action (the 4th was added for
    // MoveItemInternal/SplitStackInternal: id, container, slot, amount). Floats are
    // carried as their IEEE bit pattern in a uint slot (BitConverter round-trip).
    private readonly struct Entry(AcMainThreadQueue.ActionKind kind, uint a, uint b, uint c, uint d)
    {
        public readonly ActionKind Kind = kind;
        public readonly uint A = a;
        public readonly uint B = b;
        public readonly uint C = c;
        public readonly uint D = d;
    }

    private const int Capacity = 256; // power of two
    private const int Mask = Capacity - 1;
    private static readonly Entry[] _slots = new Entry[Capacity];
    private static readonly object _producerLock = new();

    // Monotonic counters. Producers advance _tail under _producerLock; the sole
    // consumer (main thread) advances _head.
    private static int _head;
    private static int _tail;
    private static long _dropped;

    public static long DroppedCount => Interlocked.Read(ref _dropped);

    private static bool Enqueue(ActionKind kind, uint a, uint b, uint c, uint d = 0)
    {
        lock (_producerLock)
        {
            int tail = _tail;
            int head = Volatile.Read(ref _head);
            if (tail - head >= Capacity)
            {
                // Full. Drop rather than block or fall back to an off-thread AC
                // mutation — a dropped movement/attack tick self-corrects on the
                // next bot tick; an off-thread mutation can crash the client.
                Interlocked.Increment(ref _dropped);
                return false;
            }

            _slots[tail & Mask] = new Entry(kind, a, b, c, d);
            Volatile.Write(ref _tail, tail + 1);
            return true;
        }
    }

    // ── Typed enqueue helpers (called by the public action methods when they
    //    detect they're running off AC's main thread) ───────────────────────
    public static bool EnqueueChangeCombatMode(int mode) =>
        Enqueue(ActionKind.ChangeCombatMode, unchecked((uint)mode), 0, 0);

    public static bool EnqueueMeleeAttack(uint targetId, int attackHeight, float powerLevel) =>
        Enqueue(ActionKind.MeleeAttack, targetId, unchecked((uint)attackHeight),
                BitConverter.SingleToUInt32Bits(powerLevel));

    public static bool EnqueueMissileAttack(uint targetId, int attackHeight, float accuracyLevel) =>
        Enqueue(ActionKind.MissileAttack, targetId, unchecked((uint)attackHeight),
                BitConverter.SingleToUInt32Bits(accuracyLevel));

    public static bool EnqueueDoMovement(uint motion, float speed, int holdKey) =>
        Enqueue(ActionKind.DoMovement, motion, unchecked((uint)holdKey),
                BitConverter.SingleToUInt32Bits(speed));

    public static bool EnqueueStopMovement(uint motion, int holdKey) =>
        Enqueue(ActionKind.StopMovement, motion, unchecked((uint)holdKey), 0);

    public static bool EnqueueJump(float extent) =>
        Enqueue(ActionKind.Jump, BitConverter.SingleToUInt32Bits(extent), 0, 0);

    public static bool EnqueueUseObject(uint objectId) =>
        Enqueue(ActionKind.UseObject, objectId, 0, 0);

    public static bool EnqueueUseObjectOn(uint sourceObjectId, uint targetObjectId) =>
        Enqueue(ActionKind.UseObjectOn, sourceObjectId, targetObjectId, 0);

    public static bool EnqueueUseEquippedItem(uint sourceObjectId, uint targetObjectId) =>
        Enqueue(ActionKind.UseEquippedItem, sourceObjectId, targetObjectId, 0);

    public static bool EnqueueMoveItemExternal(uint objectId, uint targetContainerId, int amount) =>
        Enqueue(ActionKind.MoveItemExternal, objectId, targetContainerId, unchecked((uint)amount));

    public static bool EnqueueMoveItemInternal(uint objectId, uint targetContainerId, int slot, int amount) =>
        Enqueue(ActionKind.MoveItemInternal, objectId, targetContainerId, unchecked((uint)slot), unchecked((uint)amount));

    public static bool EnqueueSplitStackInternal(uint objectId, uint targetContainerId, int slot, int amount) =>
        Enqueue(ActionKind.SplitStackInternal, objectId, targetContainerId, unchecked((uint)slot), unchecked((uint)amount));

    public static bool EnqueueMergeStackInternal(uint sourceObjectId, uint targetObjectId) =>
        Enqueue(ActionKind.MergeStackInternal, sourceObjectId, targetObjectId, 0);

    // Latched by EngineLifecycle.Shutdown: once teardown begins, queued plugin
    // actions must NOT keep executing on AC's main thread — the detours stay
    // live until MH_DisableHook(ALL), so without this latch a marshalled
    // mutation (SetAutoRun etc.) can run mid-teardown against state that
    // plugin Shutdowns are concurrently freeing (observed live at
    // 2026-06-11 07:44:03: "Move: SetAutoRun(False)" fired between plugin
    // shutdown steps). Abandoned entries are benign.
    private static volatile bool _disarmed;

    /// <summary>Stop executing queued actions/casts permanently (engine teardown).</summary>
    public static void Disarm() => _disarmed = true;

    // Single-consumer drain on AC's main thread (EngineFrameController.OnEndScene).
    // Re-invokes the public action methods; on the main thread they execute the
    // real AC call directly (their IsOnMainThread gate is satisfied here).
    public static void Drain()
    {
        if (_disarmed) return;
        int head = _head;                       // only the main thread writes _head
        int tail = Volatile.Read(ref _tail);
        while (head != tail)
        {
            Entry e = _slots[head & Mask];
            Volatile.Write(ref _head, ++head);

            try
            {
                switch (e.Kind)
                {
                    case ActionKind.ChangeCombatMode:
                        CombatActionHooks.ChangeCombatMode(unchecked((int)e.A));
                        break;
                    case ActionKind.MeleeAttack:
                        CombatActionHooks.MeleeAttack(e.A, unchecked((int)e.B),
                            BitConverter.UInt32BitsToSingle(e.C));
                        break;
                    case ActionKind.MissileAttack:
                        CombatActionHooks.MissileAttack(e.A, unchecked((int)e.B),
                            BitConverter.UInt32BitsToSingle(e.C));
                        break;
                    case ActionKind.DoMovement:
                        MovementActionHooks.DoMovement(e.A,
                            BitConverter.UInt32BitsToSingle(e.C), unchecked((int)e.B));
                        break;
                    case ActionKind.StopMovement:
                        MovementActionHooks.StopMovement(e.A, unchecked((int)e.B));
                        break;
                    case ActionKind.Jump:
                        MovementActionHooks.JumpNonAutonomous(BitConverter.UInt32BitsToSingle(e.A));
                        break;
                    case ActionKind.UseObject:
                        ClientHelperHooks.UseObject(e.A);
                        break;
                    case ActionKind.UseObjectOn:
                        ClientHelperHooks.UseObjectOn(e.A, e.B);
                        break;
                    case ActionKind.UseEquippedItem:
                        ClientHelperHooks.UseEquippedItem(e.A, e.B);
                        break;
                    case ActionKind.MoveItemExternal:
                        ClientHelperHooks.MoveItemExternal(e.A, e.B, unchecked((int)e.C));
                        break;
                    case ActionKind.MoveItemInternal:
                        ClientHelperHooks.MoveItemInternal(e.A, e.B, unchecked((int)e.C), unchecked((int)e.D));
                        break;
                    case ActionKind.SplitStackInternal:
                        ClientHelperHooks.SplitStackInternal(e.A, e.B, unchecked((int)e.C), unchecked((int)e.D));
                        break;
                    case ActionKind.MergeStackInternal:
                        ClientHelperHooks.MergeStackInternal(e.A, e.B);
                        break;
                }
            }
            catch
            {
                // One action must never break the drain loop. (AC-side AVs are
                // not managed exceptions and won't be caught here — but these
                // now run on the correct thread, which is the whole point.)
            }

            tail = Volatile.Read(ref _tail);
        }

        // Off-thread WriteToChat strings drain here too (same main-thread window).
        DrainChat();
    }

    // ── Chat slot (WriteToChat strings) ──────────────────────────────────────────
    // WriteToChat carries a string, which can't ride the uint Entry queue. Off-thread
    // AddTextToScroll races AC's chat-scroll buffer and corrupts it (the recurring
    // 0x00460D1D write-AV that killed a 5h+ session 2026-06-05). Chat writes marshal
    // here and drain on the main thread from Drain(), alongside the item/movement
    // actions. WriteToChat's 100 ms rate-limit runs BEFORE the enqueue, so a bot retry
    // burst is dropped on the pump thread and this queue never fills with spam.
    private static readonly System.Collections.Generic.Queue<(string Text, int ChatType)> _chatQueue = new();
    private static readonly object _chatLock = new();
    private const int MaxChatQueue = 64;

    // Pump-thread enqueue. Drops (returns false) if the queue is full.
    public static bool EnqueueWriteToChat(string text, int chatType)
    {
        lock (_chatLock)
        {
            if (_chatQueue.Count >= MaxChatQueue)
            {
                Interlocked.Increment(ref _dropped);
                return false;
            }
            _chatQueue.Enqueue((text, chatType));
            return true;
        }
    }

    // Single-consumer drain on AC's main thread. Re-invokes WriteToChat, whose
    // IsOnMainThread gate is satisfied here so it runs AddTextToScroll directly.
    private static void DrainChat()
    {
        while (true)
        {
            (string Text, int ChatType) item;
            lock (_chatLock)
            {
                if (_chatQueue.Count == 0) return;
                item = _chatQueue.Dequeue();
            }
            try { ClientHelperHooks.WriteToChat(item.Text, item.ChatType); }
            catch { }
        }
    }

    // ── Cast slot (SelectItem + CastSpell pair) ─────────────────────────────────
    // Drained at AC's GAME-LOGIC tick (Client::UseTime via GameTickHooks), NOT the
    // EndScene render tick. Single-outstanding: EnqueueCast returns false while a cast
    // is pending so the off-thread caller (BuffManager / CombatManager) retries next
    // tick — they already handle a false return without wedging. At ~frame-rate drain
    // the slot clears within ~1 frame. This is the LAST off-thread AC mutator to be
    // marshalled, closing the off-thread object-graph corruption class at its source.
    private static int _castPending;
    private static uint _castTargetId;
    private static uint _castSpellId;
    private static readonly object _castLock = new();

    // Pump-thread enqueue. Returns false (caller retries next tick) if a cast is
    // already pending.
    public static bool EnqueueCast(uint targetId, uint spellId)
    {
        lock (_castLock)
        {
            if (_castPending != 0) return false;
            _castTargetId = targetId;
            _castSpellId = spellId;
            _castPending = 1;
            return true;
        }
    }

    // Drained on AC's MAIN thread from the Client::UseTime detour (game-logic tick).
    // Re-invokes CombatActionHooks.CastSpell, which on the main thread executes the
    // cast directly (SelectItem + ClientMagicSystem::CastSpell via the SEH trampoline).
    public static void DrainCasts()
    {
        if (_disarmed) return;                               // engine teardown in progress
        if (Volatile.Read(ref _castPending) == 0) return;   // alloc-free fast path
        uint target, spell;
        lock (_castLock)
        {
            if (_castPending == 0) return;
            target = _castTargetId;
            spell = _castSpellId;
            _castPending = 0;
        }
        CombatActionHooks.CastSpell(target, unchecked((int)spell));
    }
}
