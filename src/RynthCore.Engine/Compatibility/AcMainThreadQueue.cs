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
        GiveObjectTo,
        PutItemInContainer,
        // CommandInterpreter movement marshalled 2026-06-12: SetAutoRun /
        // TurnToHeading / StopCompletely were the last direct off-thread AC
        // mutators (dump-proven: a SetAutoRun executed 15s after a main thread
        // had wedged) — they rewrite the locomotion channels of the SAME
        // CommandInterpreter/motion graph the CSequence-AV class corrupts.
        SetAutoRun,
        TurnToHeading,
        StopCompletely,
        SetMotion,
        // SetSelectedObject marshalled 2026-06-13: the host-API SelectItem /
        // SetSelectedObjectId (looting/targeting) was the LAST AC-mutating helper
        // still calling AC directly off the pump thread. AC's SetSelectedObject
        // (0x0058D110) updates the selection/targeting UIElement subtree; run off
        // the main thread it raced AC's own UI walk -> deterministic corruption AV
        // at acclient+0x60D1D (UIElement smart-ptr refcount writeback into
        // read-only .text, 5 captures in native-crash.log, two threads at once).
        SetSelectedObject,
        // NativeAttack marshalled 2026-09-01 (deep-audit finding #15): the
        // ClientCombatSystem StartAttackRequest/EndAttackRequest pair (plus the
        // height-change notify) had no MainThreadGuard gate, unlike the sibling
        // MeleeAttack/MissileAttack/ChangeCombatMode. UseNativeAttack defaults
        // true, so CombatManager.FireAttack ran this un-marshalled every fight
        // right after a correctly-marshalled SelectItem on the same pump thread.
        // Carried as one entry (not split) so the whole ordered sequence executes
        // atomically after the paired selection, per the audit's fix note.
        NativeAttack,
        // Jump trio marshalled 2026-09-02 (deep-audit finding #2): CommenceJump/
        // DoJump/TapJump call native CommandInterpreter members through
        // _boundCmdInterp with zero MainThreadGuard gate — unlike every sibling
        // (SetAutoRun/SetMotion/StopCompletely/TurnToHeading), which all marshal
        // via this queue. Live-exercised off-thread today via Jumper.cs's
        // Decal-coexistence pump-thread tick. CommenceJump and the later
        // DoJump/LaunchJumpWithMotion release land in separate queue entries
        // (separate ticks, ~msToHoldDown apart) — that's fine, the ordering
        // just needs to stay FIFO within this single-consumer queue, which it does.
        CommenceJump,
        TapJump,
        DoJumpAutonomous,
        // LaunchJumpWithMotion (PlayerPhysicsHooks.cs) is the more dangerous of
        // the two release paths: it Marshal.WriteInt32's directly into
        // CMotionInterp's forward/strafe/turn fields with NO thread gate at
        // all (not even the thiscall-through-delegate try/catch the
        // CommandInterpreter trio has) before calling DoJump. A torn write here
        // racing AC's own motion-graph tick is exactly the CSequence corruption
        // class. Carries the 5 hold-flags packed into A (bit0=shift, bit1=W,
        // bit2=X, bit3=Z, bit4=C).
        LaunchJumpWithMotion,
        // v70 WieldItem: CM_Inventory::Event_GetAndWieldItem (the 0x001A GetAndWieldItem
        // game action) is an inventory send like GiveObjectTo, so it runs here too.
        WieldItem,
        // v72 player-to-player trade: CM_Trade::Event_* / ClientTradeSystem::Accept/DeclineTrade
        // send trade game actions (and Accept/Decline touch the client's Trade object), so they
        // run here too. A = PlayerTrade.TradeOp, B/C = its arguments.
        Trade,
        // Salvage panel marshalled 2026-09-29: CM_Inventory::SendNotice_OpenSalvagePanel,
        // gmSalvageUI::AddNewItem and gmSalvageUI::Salvage were called straight from
        // the plugin pump (RynthAi SalvageManager, Meta expressions). AddNewItem and
        // Salvage are thiscall on the live gmSalvageUI and mutate its UIElement tree
        // (same class as the SetSelectedObject 0x60D1D AV). A = toolId / itemId.
        // The instance pointer is NOT carried: the drain re-enters the helper, which
        // re-reads SalvageHooks.GmSalvageUIInstance on the main thread.
        SalvageOpen,
        SalvageAddItem,
        SalvageExecute,
        // CancelAttack marshalled 2026-09-29: CM_Combat::Event_CancelAttack (0x1B7) is
        // the same CM_Combat game-action sender class as Melee/Missile/ChangeCombatMode
        // but was called straight from the plugin pump. It rides the RING (not a side
        // queue) on purpose: every RynthAi caller pairs it with ring actions
        // (StopCompletely, ChangeCombatMode, MoveItemInternal) and it must land after
        // any attack already queued, so FIFO order with those is what matters. When a
        // gesture defers the ring, the paired actions wait too, so the teardown stays
        // one ordered group.
        CancelAttack,
        // SetAutonomyLevel marshalled 2026-09-29: CM_Movement::Event_AutonomyLevel
        // (0xF752 game-action send) is exported to plugins as SetAutonomyLevelFn and
        // was the only movement sender still called straight from the plugin pump.
        // Rides the ring beside DoMovement/StopMovement/Jump so it stays ordered with
        // them. A = level.
        SetAutonomyLevel,
        // DropItem (2026-09-30, Inventory panel): CM_Inventory::Event_DropItem (0x001B game
        // action) is an inventory send like WieldItem / GiveObjectTo. A = item id.
        DropItem,
        // Skills panel raises (2026-09-30): CM_Train::Event_TrainAttribute / _TrainAttribute2nd /
        // _TrainSkill send the RaiseAttribute / RaiseVital / RaiseSkill game actions. A game-action
        // send like Trade, so it rides the ring too. A = PlayerTraining.TrainKind, B = stype, C = XP
        // (skill credits for TrainKind.TrainWithCredits: Event_TrainSkillAdvancementClass, 0x0047).
        Train,
        // v76 CloseContainer (2026-10-04): CM_Inventory::Event_NoLongerViewingContents (the
        // 0x0195 game action the client sends when a corpse/chest window closes). An inventory
        // send like DropItem; in the ring so a close queued before the next corpse's UseObject
        // goes out first, in the same drain. A = container id.
        CloseContainer,
        // v82 SalvageDirect (2026-10-12): CM_Inventory::Event_CreateTinkeringTool (the 0x027D salvage
        // game action, without the panel). An inventory send like DropItem. The item list doesn't fit
        // the four slots: A = the UST, B = the index of the list in _salvageDirectLists.
        SalvageDirect,
        // v83 RemoveClientObject (2026-10-12): drop a ghost creature from the client
        // (DeleteObjectHooks.RemoveNow re-checks on the main thread).
        RemoveClientObject,
    }

    // Four payload slots cover every routed action (the 4th was added for
    // MoveItemInternal/SplitStackInternal: id, container, slot, amount). Floats are
    // carried as their IEEE bit pattern in a uint slot (BitConverter round-trip).
    private readonly struct Entry(AcMainThreadQueue.ActionKind kind, uint a, uint b, uint c, uint d, long at, int epoch)
    {
        public readonly ActionKind Kind = kind;
        public readonly uint A = a;
        public readonly uint B = b;
        public readonly uint C = c;
        public readonly uint D = d;
        /// <summary>Environment.TickCount64 when it was queued (2026-10-06).</summary>
        public readonly long At = at;
        /// <summary>The login it was queued in (<see cref="NewLoginEpoch"/>).</summary>
        public readonly int Epoch = epoch;
    }

    // ── Age and login epoch of queued work (2026-10-06) ──────────────────────────
    // Analysis by Silentkelpie; re-implemented here. Nothing queued here carried a time: the
    // ring's gesture defer and the cast slot counted Drain()/tick CALLS (250 of them: about 2 s
    // in front, over 10 s in a 30 fps background client), and past the cap everything queued
    // ran however old - an attack on a mob that died meanwhile, a stance change or a turn from
    // seconds ago, landing on top of whatever the bot had sent since (on ACE a stale mode change
    // cancels a pending use's walk and fails a cast). The side queues (appraisals, health
    // queries, chat) were bounded by count only and could run after a logout and a new login.
    // Now every entry has its time and login; the defers are in milliseconds; at run time an
    // attack, a stance change or a movement START older than StaleActionMs is dropped (the bot
    // re-sends what it still wants), while stops always run and item actions run late (logged);
    // side-queue entries from an earlier login, or older than their limit, are dropped.
    private static int _loginEpoch;

    /// <summary>A new login begins (the logout pipeline): queued work from the old one is stale.</summary>
    public static void NewLoginEpoch() => Interlocked.Increment(ref _loginEpoch);

    private static long NowMs => Environment.TickCount64;

    private const long DrainDeferMaxMs = 2_500;
    private const long StaleActionMs = 1_500;
    private const long LateActionLogMs = 1_000;
    private const long CastDeferMaxMs = 3_000;
    private const long RequestIdMaxAgeMs = 15_000;
    private const long QueryHealthMaxAgeMs = 5_000;
    private const long ChatMaxAgeMs = 30_000;
    private const long ChatCommandMaxAgeMs = 15_000;

    private static long _staleDropped;
    private static long _lateRun;
    public static long StaleDroppedCount => Interlocked.Read(ref _staleDropped);
    public static long LateRunCount => Interlocked.Read(ref _lateRun);

    // One summary line at most every StaleLogEveryMs (main thread only).
    private const long StaleLogEveryMs = 5_000;
    private static long _staleLogAt;
    private static readonly System.Text.StringBuilder _staleNote = new();
    private static int _staleNoteCount;

    private static void NoteStale(string what, long ageMs, bool dropped)
    {
        if (dropped) Interlocked.Increment(ref _staleDropped);
        else Interlocked.Increment(ref _lateRun);
        if (_staleNoteCount < 8)
        {
            if (_staleNote.Length > 0) _staleNote.Append(", ");
            _staleNote.Append(dropped ? "dropped " : "ran late ").Append(what).Append(' ').Append(ageMs).Append(" ms");
        }
        _staleNoteCount++;
        long now = NowMs;
        if (now - _staleLogAt < StaleLogEveryMs) return;
        _staleLogAt = now;
        string line = $"AcMainThreadQueue: {_staleNoteCount} stale queued item(s): {_staleNote}{(_staleNoteCount > 8 ? ", ..." : "")}";
        _staleNote.Clear();
        _staleNoteCount = 0;
        try { RynthLog.Compat(line); } catch { }
        try { Net.FlightRecorder.Mark(Net.FlightMark.Note, text: line); } catch { }
    }

    /// <summary>
    /// An attack, a stance change or a movement start: dropped when older than StaleActionMs at
    /// run time. Stops (autorun off, a motion off, StopMovement, StopCompletely, CancelAttack) and
    /// item actions are never dropped.
    /// </summary>
    private static bool DropsWhenStale(in Entry e) => e.Kind switch
    {
        ActionKind.MeleeAttack or ActionKind.MissileAttack or ActionKind.NativeAttack => true,
        ActionKind.ChangeCombatMode => true,
        ActionKind.SetAutoRun => e.A != 0,
        ActionKind.SetMotion => e.B != 0,
        ActionKind.DoMovement or ActionKind.TurnToHeading or ActionKind.Jump
            or ActionKind.CommenceJump or ActionKind.TapJump or ActionKind.DoJumpAutonomous
            or ActionKind.LaunchJumpWithMotion => true,
        _ => false,
    };

    /// <summary>
    /// Stops and cancels: the teardown group that still runs after a world transition (every
    /// other entry queued during one is dropped). Never dropped as stale either.
    /// </summary>
    private static bool IsTeardown(in Entry e) => IsTeardownKind(e.Kind, e.A, e.B);

    /// <summary>
    /// The ONE teardown predicate: the enqueue side (ActionRing admission, the teardown reserve)
    /// and the drain side (which entries survive a world transition) both ask it.
    /// </summary>
    internal static bool IsTeardownKind(ActionKind kind, uint a, uint b) => kind switch
    {
        ActionKind.SetAutoRun => a == 0,
        ActionKind.SetMotion => b == 0,
        ActionKind.StopMovement or ActionKind.StopCompletely or ActionKind.CancelAttack
            or ActionKind.CloseContainer => true,
        _ => false,
    };

    // World-transition drops: counted, and folded into the rate-limited stale summary line.
    private static long _transitionDropped;
    public static long TransitionDroppedCount => Interlocked.Read(ref _transitionDropped);

    private static void NoteTransitionDrop(string what, long ageMs)
    {
        Interlocked.Increment(ref _transitionDropped);
        NoteStale(what + " (queued during a death/portal/teleport)", ageMs, dropped: true);
    }

    private static string Describe(in Entry e) => e.Kind switch
    {
        ActionKind.SetAutoRun => e.A != 0 ? "SetAutoRun(on)" : "SetAutoRun(off)",
        ActionKind.SetMotion => e.B != 0 ? $"SetMotion(0x{e.A:X8}, on)" : $"SetMotion(0x{e.A:X8}, off)",
        ActionKind.ChangeCombatMode => $"ChangeCombatMode({e.A})",
        ActionKind.UseObject => $"UseObject(0x{e.A:X8})",
        ActionKind.MeleeAttack or ActionKind.MissileAttack => $"{e.Kind}(0x{e.A:X8})",
        _ => e.Kind.ToString(),
    };

    // The ring (ActionRing: storage, FIFO, admission). 256 entries, the top 32 for teardown
    // only, so a ring a bot filled with starts still takes its stops and cancels.
    private const int Capacity = 256; // power of two
    private const int TeardownReserve = 32;
    private static readonly ActionRing<Entry> _ring = new(Capacity, TeardownReserve);
    private static long _dropped;
    private static long _transitionRejected;

    /// <summary>Entries refused because the ring was full (a running total; the heartbeat's qdrop).</summary>
    public static long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>Non-teardown entries refused while the player was in a world transition.</summary>
    public static long TransitionRejectedCount => Interlocked.Read(ref _transitionRejected);

    // -- The mutator gate ----------------------------------------------------------
    // Every AC-mutating helper (ClientHelperHooks, CombatActionHooks, MovementActionHooks,
    // CommandInterpreterHooks, ClientCombatHooks, PlayerPhysicsHooks, PlayerTrade,
    // PlayerTraining) opens with the same line:
    //     if (AcMainThreadQueue.MustQueue(teardown: <is this a stop/cancel>)) return Enqueue...;
    // Off AC's main thread it queues (as always). ON the main thread it also queues while a
    // world transition holds the drains, unless it is being called BY a drain (IsDraining)
    // or is teardown: render-thread host actions (ImGui panels, the EndScene plugin calls)
    // used to run straight into a world AC was tearing down, around the gate. The queue
    // then refuses or holds it like any other queued work (WorldTransitionState).
    private static int _drainDepth;   // AC main thread only

    /// <summary>AC main thread only: a drain is dispatching queued work right now.</summary>
    public static bool IsDraining => _drainDepth > 0;

    /// <summary>AC main thread only: a direct call now would run into a world transition.</summary>
    public static bool ShouldDefer() => !IsDraining && PlayerLifecycleLog.HoldQueuedWork(NowMs);

    /// <summary>
    /// The one gate every mutator opens with: true = queue instead of calling AC now.
    /// <paramref name="teardown"/>: a stop or cancel (IsTeardownKind), which runs at once on
    /// the main thread even in a transition.
    /// </summary>
    public static bool MustQueue(bool teardown) =>
        MutatorGate.MustQueue(MainThreadGuard.IsOnMainThread(), teardown, IsDraining, HoldNow);

    // Cached (no allocation per call): main thread only, asked only by MutatorGate on it.
    private static readonly Func<bool> HoldNow = () => PlayerLifecycleLog.HoldQueuedWork(NowMs);

    private static bool Enqueue(ActionKind kind, uint a, uint b, uint c, uint d = 0)
    {
        bool teardown = IsTeardownKind(kind, a, b);
        // Dead / portaling / settling: a start queued now would be dropped when the window
        // ends anyway (WorldTransitionState), so it is refused here and never takes a slot a
        // stop needs. Stops and cancels are always taken (the reserve keeps room for them).
        var r = _ring.TryEnqueue(new Entry(kind, a, b, c, d, NowMs, Volatile.Read(ref _loginEpoch)),
                                 teardown, PlayerLifecycleLog.InWorldTransition);
        if (r == ActionRing<Entry>.Admit.Queued) return true;
        // Full: drop rather than block or fall back to an off-thread AC mutation - a dropped
        // movement/attack tick self-corrects on the next bot tick; an off-thread mutation can
        // crash the client.
        if (r == ActionRing<Entry>.Admit.Full) Interlocked.Increment(ref _dropped);
        else Interlocked.Increment(ref _transitionRejected);
        return false;
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

    public static bool EnqueueCancelAttack() =>
        Enqueue(ActionKind.CancelAttack, 0, 0, 0);

    public static bool EnqueueDoMovement(uint motion, float speed, int holdKey) =>
        Enqueue(ActionKind.DoMovement, motion, unchecked((uint)holdKey),
                BitConverter.SingleToUInt32Bits(speed));

    public static bool EnqueueStopMovement(uint motion, int holdKey) =>
        Enqueue(ActionKind.StopMovement, motion, unchecked((uint)holdKey), 0);

    public static bool EnqueueSetAutoRun(bool enabled) =>
        Enqueue(ActionKind.SetAutoRun, enabled ? 1u : 0u, 0, 0);

    public static bool EnqueueTurnToHeading(float headingDegrees) =>
        Enqueue(ActionKind.TurnToHeading, BitConverter.SingleToUInt32Bits(headingDegrees), 0, 0);

    public static bool EnqueueStopCompletely() =>
        Enqueue(ActionKind.StopCompletely, 0, 0, 0);

    // ── Direct-heading slot (coalesced, latest value wins) ──────────────────────
    // PlayerPhysicsHooks.SetPlayerHeadingDirect snaps the player's CPhysicsObj
    // orientation quaternion with two raw stores (qw, then qz). Plugins call it
    // (Host.TurnToHeading) from the pump thread on every nav tick; run there it
    // raced AC's own physics integration and could leave a torn, non-unit
    // quaternion (new qw with old qz). Off-thread callers now park the heading
    // here and Drain applies it on the main thread. One slot, not a ring entry:
    // nav re-issues the heading every tick and only the newest one matters, so a
    // steering bot can never fill the action ring with headings. Lock-free and
    // allocation-free; never drops (a newer heading simply replaces an older one).
    // Deliberately NOT ActionKind.TurnToHeading: that dispatches the gradual
    // CommandInterpreter turn, which would change the steering behaviour.
    private static int _directHeadingPending;
    private static uint _directHeadingBits;
    private static long _directHeadingAt;

    public static bool EnqueueDirectHeading(float headingDegrees)
    {
        Volatile.Write(ref _directHeadingBits, BitConverter.SingleToUInt32Bits(headingDegrees));
        Interlocked.Exchange(ref _directHeadingAt, NowMs);
        Volatile.Write(ref _directHeadingPending, 1);
        return true;
    }

    // Main thread only (from Drain). Same order as the pre-queue code: the direct
    // snap first, the CommandInterpreter turn only when there is no player.
    // World transition: the pending heading is held; once the window ends a heading set
    // during it is stale (the bot steers again from where it landed) and is dropped.
    private static void DrainDirectHeading()
    {
        if (Volatile.Read(ref _directHeadingPending) == 0) return;
        long now = NowMs;
        long at = Interlocked.Read(ref _directHeadingAt);
        WorldTransitionState.Verdict v = PlayerLifecycleLog.DecideQueued(now, at, teardown: false);
        if (v == WorldTransitionState.Verdict.Hold) return;
        if (Interlocked.Exchange(ref _directHeadingPending, 0) == 0) return;
        if (v == WorldTransitionState.Verdict.Drop)
        {
            NoteTransitionDrop("heading", now - at);
            return;
        }
        float heading = BitConverter.UInt32BitsToSingle(Volatile.Read(ref _directHeadingBits));
        try
        {
            if (!PlayerPhysicsHooks.SetPlayerHeadingDirect(heading))
                CommandInterpreterHooks.TurnToHeading(heading);
        }
        catch { }
    }

    // ── Overlay click-to-select slot (coalesced, latest click wins) ─────────────
    // A click on a Radar dot or a monster nameplate (ImGui overlays, 2026-09-30)
    // parks the object id here; Drain runs ClientHelperHooks.SelectFromOverlay on
    // the main thread, which re-checks that the object still exists and that the
    // client isn't portaling or logging out right before AC's SetSelectedObject.
    // The faces always park here (never select inside the ImGui frame build), so
    // the select runs in the same main-thread phase as every other queued action.
    // One slot, not a ring entry: a click is one select, two clicks in one frame
    // collapse to the newer one, and the slot sits before the ring's gesture
    // defer (a selection is a UI change, not a motion; a click in the 3D world
    // selects mid-gesture too). Lock-free and allocation-free.
    private static int _overlaySelectPending;
    private static uint _overlaySelectId;
    private static long _overlaySelectAt;

    public static bool EnqueueOverlaySelect(uint objectId)
    {
        if (objectId == 0) return false;
        Volatile.Write(ref _overlaySelectId, objectId);
        Interlocked.Exchange(ref _overlaySelectAt, NowMs);
        Volatile.Write(ref _overlaySelectPending, 1);
        return true;
    }

    // Main thread only (from Drain). World transition: held, and kept when the window ends:
    // it is the user's own click, and SelectFromOverlay re-checks the object still exists.
    private static void DrainOverlaySelect()
    {
        if (Volatile.Read(ref _overlaySelectPending) == 0) return;
        if (PlayerLifecycleLog.DecideQueued(NowMs, Interlocked.Read(ref _overlaySelectAt), teardown: true)
            == WorldTransitionState.Verdict.Hold)
            return;
        if (Interlocked.Exchange(ref _overlaySelectPending, 0) == 0) return;
        uint id = Volatile.Read(ref _overlaySelectId);
        try { ClientHelperHooks.SelectFromOverlay(id); }
        catch { }
    }

    public static bool EnqueueSetMotion(uint motion, bool enabled) =>
        Enqueue(ActionKind.SetMotion, motion, enabled ? 1u : 0u, 0);

    public static bool EnqueueJump(float extent) =>
        Enqueue(ActionKind.Jump, BitConverter.SingleToUInt32Bits(extent), 0, 0);

    public static bool EnqueueSetAutonomyLevel(uint level) =>
        Enqueue(ActionKind.SetAutonomyLevel, level, 0, 0);

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

    public static bool EnqueueGiveObjectTo(uint objectId, uint targetId, int amount) =>
        Enqueue(ActionKind.GiveObjectTo, objectId, targetId, unchecked((uint)amount));

    public static bool EnqueuePutItemInContainer(uint objectId, uint containerId, int slot) =>
        Enqueue(ActionKind.PutItemInContainer, objectId, containerId, unchecked((uint)slot));

    public static bool EnqueueWieldItem(uint objectId, uint equipMask) =>
        Enqueue(ActionKind.WieldItem, objectId, equipMask, 0);

    public static bool EnqueueDropItem(uint objectId) =>
        Enqueue(ActionKind.DropItem, objectId, 0, 0);

    public static bool EnqueueCloseContainer(uint containerId) =>
        Enqueue(ActionKind.CloseContainer, containerId, 0, 0);

    public static bool EnqueueTrade(uint op, uint a, uint b) =>
        Enqueue(ActionKind.Trade, op, a, b);

    public static bool EnqueueTrain(uint kind, uint stype, uint xp) =>
        Enqueue(ActionKind.Train, kind, stype, xp);

    public static bool EnqueueSalvageOpen(uint toolId) =>
        Enqueue(ActionKind.SalvageOpen, toolId, 0, 0);

    public static bool EnqueueSalvageAddItem(uint itemId) =>
        Enqueue(ActionKind.SalvageAddItem, itemId, 0, 0);

    public static bool EnqueueSalvageExecute() =>
        Enqueue(ActionKind.SalvageExecute, 0, 0, 0);

    // ── Salvage-without-panel item lists (v82) ──────────────────────────────────
    // A SalvageDirect entry carries its item list here: the producer copies the ids into a fresh
    // array and parks it in a free slot (under _salvageDirectLock), the ring entry carries the slot
    // index, and the drain takes it back with one Interlocked.Exchange (lock-free, no allocation on
    // the main thread). Eight slots: the bot has one request in flight at a time; a full set refuses.
    private const int SalvageDirectSlots = 8;
    private static readonly uint[]?[] _salvageDirectLists = new uint[]?[SalvageDirectSlots];
    private static int _salvageDirectNext;
    // Serializes slot claims (the ring has its own lock since ActionRing<T>, PR #8).
    private static readonly object _salvageDirectLock = new();

    public static bool EnqueueSalvageDirect(uint toolId, uint[] itemIds)
    {
        if (toolId == 0 || itemIds == null || itemIds.Length == 0) return false;
        uint[] copy = (uint[])itemIds.Clone();
        lock (_salvageDirectLock)
        {
            for (int k = 0; k < SalvageDirectSlots; k++)
            {
                int i = (_salvageDirectNext + k) % SalvageDirectSlots;
                if (Volatile.Read(ref _salvageDirectLists[i]) != null) continue;
                Volatile.Write(ref _salvageDirectLists[i], copy);
                if (!Enqueue(ActionKind.SalvageDirect, toolId, (uint)i, 0))
                {
                    Volatile.Write(ref _salvageDirectLists[i], null);
                    return false;
                }
                _salvageDirectNext = (i + 1) % SalvageDirectSlots;
                return true;
            }
        }
        Interlocked.Increment(ref _dropped);
        return false;
    }

    /// <summary>Main thread: takes the list a SalvageDirect entry parked (null if none).</summary>
    private static uint[]? TakeSalvageDirectList(uint slot) =>
        slot < SalvageDirectSlots ? Interlocked.Exchange(ref _salvageDirectLists[slot], null) : null;

    public static bool EnqueueRemoveClientObject(uint objectId) =>
        Enqueue(ActionKind.RemoveClientObject, objectId, 0, 0);

    public static bool EnqueueSetSelectedObject(uint objectId) =>
        Enqueue(ActionKind.SetSelectedObject, objectId, 0, 0);

    public static bool EnqueueNativeAttack(int attackHeight, float power) =>
        Enqueue(ActionKind.NativeAttack, unchecked((uint)attackHeight),
                BitConverter.SingleToUInt32Bits(power), 0);

    public static bool EnqueueCommenceJump() => Enqueue(ActionKind.CommenceJump, 0, 0, 0);
    public static bool EnqueueTapJump() => Enqueue(ActionKind.TapJump, 0, 0, 0);
    public static bool EnqueueDoJumpAutonomous(bool autonomous) =>
        Enqueue(ActionKind.DoJumpAutonomous, autonomous ? 1u : 0u, 0, 0);

    public static bool EnqueueLaunchJumpWithMotion(bool shift, bool holdW, bool holdX, bool holdZ, bool holdC)
    {
        uint flags = (shift ? 1u : 0u) | (holdW ? 2u : 0u) | (holdX ? 4u : 0u) | (holdZ ? 8u : 0u) | (holdC ? 16u : 0u);
        return Enqueue(ActionKind.LaunchJumpWithMotion, flags, 0, 0);
    }

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

    /// <summary>Engine teardown has begun (ChatCallbackHooks stops holding chat).</summary>
    public static bool IsDisarmed => _disarmed;

    // Single-consumer drain on AC's main thread (EngineFrameController.OnEndScene).
    // Re-invokes the public action methods; on the main thread they execute the
    // real AC call directly (their IsOnMainThread gate is satisfied here).
    // Gesture-phase defer state for the action ring (mirrors the cast slot's
    // 56e6946 guard; see DrainDeferTickCap note below for why we PROCEED
    // rather than drop after the cap).
    private static long _drainDeferSinceMs;

    public static void Drain()
    {
        // IsDraining: the helpers this dispatches run their AC call directly (MustQueue).
        _drainDepth++;
        try { DrainCore(); }
        finally { _drainDepth--; }
    }

    private static void DrainCore()
    {
        if (_disarmed)
        {
            // Held incoming chat still prints during teardown (the AddTextToScroll detour
            // stays live until MH_DisableHook(ALL)): it is AC's own line, not a plugin action.
            try { ChatCallbackHooks.OnMainThreadDrain(disarmed: true); } catch { }
            return;
        }

        // We are on AC's main thread by definition. Latch it here too: after a hot
        // reload the first SmartBox/combat detour (the other latches) can come well
        // after the first drain, and until the latch is set every dispatch below
        // thinks it's off-thread and re-queues itself. A chat command a plugin queued
        // straight after the reload then spun the main thread forever
        // (DrainChatCommands -> Dispatch -> EnqueueChatCommand, 2026-09-29).
        MainThreadGuard.RecordIfFirst();

        // Incoming chat held for the plugins' verdict prints first, before anything below
        // writes its own lines, so the chat window keeps the order lines arrived in.
        try { ChatCallbackHooks.OnMainThreadDrain(disarmed: false); } catch { }

        // Keep the off-thread combat-mode cache true to the client (stale after a hot reload).
        CombatModeHooks.MainThreadRefresh();

        // Deep-audit finding #22 (2026-06-18): these three queues are
        // documented as deliberately separate from the gesture-gated action
        // ring below specifically so appraisals/chat "can never get
        // gesture-deferred" — but the gesture-defer early-return further
        // down used to run BEFORE these calls, so a non-empty action ring
        // sitting behind an in-flight gesture silently stalled chat output
        // and 0xC8 appraisal sends too, contradicting that invariant. Each
        // drains only while ITS OWN queue is non-empty, so moving them here
        // costs nothing when they're empty (the common case).
        // World transition gate (dead / portal / landblock jump, WorldTransitionState): AC is
        // tearing objects down and loading new ones, so nothing queued that mutates the game
        // runs now. Chat output (AC's own text window) and the char-select service still do.
        long gateNow = NowMs;
        bool holding = PlayerLifecycleLog.HoldQueuedWork(gateNow);

        DrainChat();
        if (!holding) DrainChatCommands();   // held: their own 15 s age limit still applies
        DrainRequestIds();
        DrainQueryHealth();
        // Vendor buy/sell: a packet send through the client's own SendShopEvent, not a
        // motion, so it sits with the non-gesture queues above. Also polls whether the
        // vendor window is still open. Idle fast path when no vendor is open.
        if (!holding)
        {
            try { VendorTrade.MainThreadTick(); } catch { }
        }
        // Char-select service: publishes the UIFlow mode, runs a pending auto-login
        // LogOnCharacter request and snapshots the native character list (throttled).
        // Pre-login this runs from the Client::UseTime drain (EndScene is not hooked
        // until after login). Idle in the world.
        try { CharacterManagementHooks.MainThreadTick(); } catch { }

        // Plugin heading snap (coalesced slot). Before the ring and its gesture
        // defer: the pre-queue raw write landed immediately, gesture or not, and
        // ahead of the SetAutoRun a nav tick queues right after it. Keep both.
        // Both are latest-value slots (a heading, a select): held through a world transition,
        // then decided by their own drains (a stale heading dropped, a user's select kept).
        DrainDirectHeading();

        // Overlay click-to-select (Radar dot / nameplate click): also before the
        // gesture defer, so a click selects at once even while the bot swings.
        DrainOverlaySelect();

        // In a world transition the ring is held whole (FIFO kept): nothing runs, nothing is
        // dropped yet. Once it ends, the loop below drops what was queued during it, except
        // stops and cancels, which run in order as one teardown group.
        if (holding)
            return;

        // ── Gesture-phase serialization (extends the 56e6946 anim-walk guard
        // to the whole ring) ─────────────────────────────────────────────────
        // Every ring action perturbs AC's action/motion state to some degree:
        // UseObject starts a reach gesture, ChangeCombatMode rebuilds the
        // motion graph, movement rewrites locomotion channels. Retail AC
        // serializes these against the IN-FLIGHT gesture via its action queue;
        // we previously serialized thread + tick-phase but not gesture-phase.
        // Defer the drain while the pending-motion list [CMI+0x80] is
        // non-empty — bounded, then PROCEED (unlike the cast slot we never
        // drop: dropping arbitrary item/movement actions desyncs the bot far
        // worse than a late injection, and the pre-2026-06-12 behavior was
        // "always inject" anyway, so proceeding past the cap is never worse).
        // Gesture-safe entries at the head still run (RunsDuringGesture): while deferring they
        // run from the head of the ring up to the first one that doesn't (FIFO order is kept:
        // nothing overtakes a held entry).
        bool gestureSafeOnly = false;
        if (_ring.Count > 0)
        {
            // In milliseconds (was 250 Drain calls: 2 s to over 10 s depending on the frame rate).
            if (PlayerPhysicsHooks.TryGetCastGestureInProgress(out bool gestureInFlight) && gestureInFlight)
            {
                long nowDefer = NowMs;
                if (_drainDeferSinceMs == 0) _drainDeferSinceMs = nowDefer;
                gestureSafeOnly = nowDefer - _drainDeferSinceMs <= DrainDeferMaxMs;
            }
            if (!gestureSafeOnly) _drainDeferSinceMs = 0;
        }
        long runAt = NowMs;
        int epochNow = Volatile.Read(ref _loginEpoch);
        // Bounded to what is queued now plus a capacity's worth: an action that re-queues
        // itself can never spin the main thread.
        int budget = Capacity * 2;
        while (budget-- > 0 && _ring.TryPeek(out Entry e))
        {
            if (gestureSafeOnly && !RunsDuringGesture(e.Kind)) break;
            _ring.Pop();
            long age = runAt - e.At;
            if (PlayerLifecycleLog.DecideQueued(runAt, e.At, IsTeardown(e)) == WorldTransitionState.Verdict.Drop)
            {
                NoteTransitionDrop(Describe(e), age);
                if (e.Kind == ActionKind.SalvageDirect) TakeSalvageDirectList(e.B);   // free its list slot
                continue;
            }
            if (e.Epoch != epochNow || (age > StaleActionMs && DropsWhenStale(e)))
            {
                NoteStale(Describe(e) + (e.Epoch != epochNow ? " (queued before this login)" : ""), age, dropped: true);
                if (e.Kind == ActionKind.SalvageDirect) TakeSalvageDirectList(e.B);   // free its list slot
                continue;
            }
            if (age > LateActionLogMs)
                NoteStale(Describe(e), age, dropped: false);

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
                    case ActionKind.CancelAttack:
                        CombatActionHooks.CancelAttack();
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
                    case ActionKind.SetAutonomyLevel:
                        MovementActionHooks.SetAutonomyLevel(e.A);
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
                    case ActionKind.GiveObjectTo:
                        ClientHelperHooks.GiveObjectTo(e.A, e.B, unchecked((int)e.C));
                        break;
                    case ActionKind.PutItemInContainer:
                        ClientHelperHooks.PutItemInContainerAt(e.A, e.B, unchecked((int)e.C));
                        break;
                    case ActionKind.WieldItem:
                        ClientHelperHooks.WieldItem(e.A, e.B);
                        break;
                    case ActionKind.DropItem:
                        ClientHelperHooks.DropItem(e.A);
                        break;
                    case ActionKind.CloseContainer:
                        ClientHelperHooks.CloseContainer(e.A);
                        break;
                    case ActionKind.Trade:
                        PlayerTrade.RunQueued(e.A, e.B, e.C);
                        break;
                    case ActionKind.Train:
                        PlayerTraining.RunQueued(e.A, e.B, e.C);
                        break;
                    case ActionKind.SalvageOpen:
                        ClientHelperHooks.SalvagePanelOpen(e.A);
                        break;
                    case ActionKind.SalvageAddItem:
                        ClientHelperHooks.SalvagePanelAddItem(e.A);
                        break;
                    case ActionKind.SalvageExecute:
                        ClientHelperHooks.SalvagePanelExecute();
                        break;
                    case ActionKind.SalvageDirect:
                        if (TakeSalvageDirectList(e.B) is uint[] salvageItems)
                            ClientHelperHooks.SalvageDirect(e.A, salvageItems);
                        break;
                    case ActionKind.RemoveClientObject:
                        DeleteObjectHooks.RemoveNow(e.A);
                        break;
                    case ActionKind.SetSelectedObject:
                        // On the main thread now -> SetSelectedObjectId's gate is
                        // satisfied and it calls AC's SetSelectedObject directly.
                        ClientHelperHooks.SetSelectedObjectId(e.A);
                        break;
                    case ActionKind.NativeAttack:
                        // On the main thread now -> NativeAttack's gate is
                        // satisfied and it fires the real StartAttackRequest/
                        // EndAttackRequest pair directly.
                        ClientCombatHooks.NativeAttack(unchecked((int)e.A),
                            BitConverter.UInt32BitsToSingle(e.B));
                        break;
                    case ActionKind.CommenceJump:
                        CommandInterpreterHooks.CommenceJump();
                        break;
                    case ActionKind.TapJump:
                        CommandInterpreterHooks.TapJump();
                        break;
                    case ActionKind.DoJumpAutonomous:
                        CommandInterpreterHooks.DoJump(e.A != 0);
                        break;
                    case ActionKind.LaunchJumpWithMotion:
                        PlayerPhysicsHooks.LaunchJumpWithMotion(
                            (e.A & 1u) != 0, (e.A & 2u) != 0, (e.A & 4u) != 0,
                            (e.A & 8u) != 0, (e.A & 16u) != 0);
                        break;
                    case ActionKind.SetAutoRun:
                        CommandInterpreterHooks.SetAutoRun(e.A != 0);
                        break;
                    case ActionKind.TurnToHeading:
                        CommandInterpreterHooks.TurnToHeading(BitConverter.UInt32BitsToSingle(e.A));
                        break;
                    case ActionKind.StopCompletely:
                        CommandInterpreterHooks.StopCompletely();
                        break;
                    case ActionKind.SetMotion:
                        CommandInterpreterHooks.SetMotion(e.A, e.B != 0);
                        break;
                }
            }
            catch
            {
                // One action must never break the drain loop. (AC-side AVs are
                // not managed exceptions and won't be caught here — but these
                // now run on the correct thread, which is the whole point.)
            }
        }
        // The gesture-safe entries emptied the ring: the next held entry gets its own full defer.
        if (gestureSafeOnly && _ring.Count == 0) _drainDeferSinceMs = 0;
    }

    /// <summary>
    /// Entries that run while a gesture is in flight (2026-10-14). Each is what the retail
    /// client does from a key press at any moment, swing or not: the target select, the
    /// keyboard attack (ClientCombatSystem StartAttackRequest/EndAttackRequest, which the client
    /// itself holds until the motion allows) and the 0x1B7 cancel. With a crossbow or bow a
    /// gesture is in flight nearly all the time, so these waited up to DrainDeferMaxMs every
    /// cycle (Silentkelpie: target select and attacks 1.0-1.9 s late, stale attacks dropped).
    /// Stance changes, movement, raw attack packets and item moves stay deferred.
    /// </summary>
    internal static bool RunsDuringGesture(ActionKind kind) => kind switch
    {
        ActionKind.SetSelectedObject or ActionKind.NativeAttack or ActionKind.CancelAttack => true,
        _ => false,
    };

    // ── RequestId slot (0xC8 appraisal sends from AutoIdService, and every other
    //    off-thread CombatActionHooks.RequestId caller: plugin RequestIdFn, the
    //    PluginManager.TickAll login self-identify) ─────────────────────────────
    // AutoIdService runs on a Timer thread; sending 0xC8 directly there does AC heap
    // allocation + a non-atomic shared UI-counter increment off AC's main thread (the
    // off-thread-send class flagged for RequestId). Marshal onto the main thread here.
    // Kept in its OWN queue — NOT the gesture-deferred action ring above — so a backlog
    // of appraisals can never crowd out combat/movement actions or get gesture-deferred:
    // an appraisal is just a packet send and doesn't perturb AC's motion/UI state.
    // A request for an object that already has one pending is merged into it, as the
    // QueryHealth slot does: the one appraisal reply answers every caller (PendingIdQueue).
    private const int MaxRequestIdQueue = 256;
    private static readonly PendingIdQueue _requestIdQueue = new(MaxRequestIdQueue);
    private static readonly object _requestIdLock = new();
    // Merge only into an entry the drain will still send: half the stale limit leaves room
    // for a drain that runs late (a stalled main thread).
    private const long RequestIdMergeMaxAgeMs = RequestIdMaxAgeMs / 2;

    /// <summary>RequestId calls absorbed by an already-pending request for the same object.</summary>
    public static long RequestIdMergedCount
    {
        get { lock (_requestIdLock) return _requestIdQueue.MergedTotal; }
    }

    // Pump/Timer-thread enqueue. Drops (returns false) if the queue is full. A request for an
    // object already waiting returns true without queueing a second send.
    // <paramref name="who"/> rides with the entry so the drain can mark the real send as the
    // engine's (Net/IdRequester.cs); a merge keeps the first requester.
    public static bool EnqueueRequestId(uint objectId, Net.IdRequester who = Net.IdRequester.None)
    {
        if (objectId == 0) return false;
        // Dead / portaling / settling: an appraisal queued now is dropped when the window
        // ends, so it is refused here (the caller sees false and may ask again later). Nor
        // does a request merge into an entry from before the last held drain: that entry is
        // dropped when the drain reaches it, and the merged request would go with it.
        if (PlayerLifecycleLog.InWorldTransition) return false;
        long mergeOnlyAfter = PlayerLifecycleLog.LastBlockedMs;
        lock (_requestIdLock)
        {
            PendingIdQueue.Result r = _requestIdQueue.Enqueue(objectId, NowMs, Volatile.Read(ref _loginEpoch),
                                                              RequestIdMergeMaxAgeMs, mergeOnlyAfter, (byte)who);
            if (r == PendingIdQueue.Result.Full)
            {
                Interlocked.Increment(ref _dropped);
                return false;
            }
            return true;
        }
    }

    // Single-consumer drain on AC's main thread (from Drain()). On the main thread
    // CombatActionHooks.RequestId sends the 0xC8 directly.
    private static void DrainRequestIds()
    {
        // Bounded like DrainChatCommands: a re-queue never spins the main thread.
        int budget;
        lock (_requestIdLock) budget = _requestIdQueue.Count;
        if (budget == 0) return;
        long now = NowMs;
        // World transition (dead / portal / landblock jump): hold every appraisal; once it
        // ends, those queued during it are dropped (WorldTransitionState).
        if (PlayerLifecycleLog.HoldQueuedWork(now)) return;
        int epoch = Volatile.Read(ref _loginEpoch);
        while (budget-- > 0)
        {
            PendingIdQueue.Item item;
            lock (_requestIdLock)
            {
                if (!_requestIdQueue.TryDequeue(out item)) return;
            }
            if (item.Epoch != epoch || now - item.At > RequestIdMaxAgeMs)
            {
                NoteStale($"RequestId(0x{item.Id:X8})", now - item.At, dropped: true);
                continue;
            }
            if (PlayerLifecycleLog.DecideQueued(now, item.At, teardown: false) == WorldTransitionState.Verdict.Drop)
            {
                NoteTransitionDrop($"RequestId(0x{item.Id:X8})", now - item.At);
                continue;
            }
            bool sent = false;
            try
            {
                using (Net.EngineSendMarker.Enter((Net.IdRequester)item.Source))
                    sent = CombatActionHooks.RequestId(item.Id);
            }
            catch { }
            // AutoId's re-appraisal window starts at the real send, not at the queueing:
            // a request dropped here (stale, transition) leaves the object due again.
            if (sent)
            {
                try { AutoIdService.OnSent(item.Id, now); } catch { }
            }
        }
    }

    // ── QueryHealth slot (0x1BF CM_Combat::Event_QueryHealth sends) ─────────────────
    // Plugins (RynthAi CombatManager, once per fight target) call QueryHealthFn from the
    // plugin pump; the send allocates an AC blob and pushes AC's send queue, so it is
    // main-thread-only like 0xC8. Same shape as the RequestId slot: its own small
    // queue, NOT the gesture-deferred action ring (a packet send doesn't perturb the
    // motion graph and a health query must not wait out a swing). Callers ignore the
    // result and only want the async 0x01C0 reply, so a target that is already the LAST
    // one waiting here is not queued twice. Only the last: the server streams health for
    // the most recent query, so "pet, then the fight target again" must keep that order
    // even when the fight target was already queued before the pet (deduping against any
    // earlier entry left the stream on the pet). Pre-sized, so enqueue does not allocate.
    private const int MaxQueryHealthQueue = 64;
    private static readonly System.Collections.Generic.Queue<(uint Id, long At, int Epoch)> _queryHealthQueue = new(MaxQueryHealthQueue);
    private static readonly object _queryHealthLock = new();
    private static int _queryHealthPending; // lock-free "anything queued?" for the per-frame drain
    private static uint _queryHealthLastId;  // the newest entry's id (under _queryHealthLock)

    // Pump-thread enqueue. Drops (returns false) if the queue is full.
    public static bool EnqueueQueryHealth(uint targetId)
    {
        if (targetId == 0) return false;
        lock (_queryHealthLock)
        {
            if (_queryHealthQueue.Count > 0 && _queryHealthLastId == targetId)
                return true; // already the last pending; one reply answers both, the order holds
            if (_queryHealthQueue.Count >= MaxQueryHealthQueue)
            {
                Interlocked.Increment(ref _dropped);
                return false;
            }
            _queryHealthQueue.Enqueue((targetId, NowMs, Volatile.Read(ref _loginEpoch)));
            _queryHealthLastId = targetId;
            Volatile.Write(ref _queryHealthPending, _queryHealthQueue.Count);
            return true;
        }
    }

    // Single-consumer drain on AC's main thread (from Drain()). On the main thread
    // CombatActionHooks.QueryHealth sends the 0x1BF directly.
    private static void DrainQueryHealth()
    {
        if (Volatile.Read(ref _queryHealthPending) == 0) return;
        int budget;
        lock (_queryHealthLock) budget = _queryHealthQueue.Count;
        long now = NowMs;
        if (PlayerLifecycleLog.HoldQueuedWork(now)) return;   // world transition: hold
        int epoch = Volatile.Read(ref _loginEpoch);
        while (budget-- > 0)
        {
            (uint Id, long At, int Epoch) item;
            lock (_queryHealthLock)
            {
                if (_queryHealthQueue.Count == 0) { Volatile.Write(ref _queryHealthPending, 0); return; }
                item = _queryHealthQueue.Dequeue();
                Volatile.Write(ref _queryHealthPending, _queryHealthQueue.Count);
            }
            if (item.Epoch != epoch || now - item.At > QueryHealthMaxAgeMs)
            {
                NoteStale($"QueryHealth(0x{item.Id:X8})", now - item.At, dropped: true);
                continue;
            }
            if (PlayerLifecycleLog.DecideQueued(now, item.At, teardown: false) == WorldTransitionState.Verdict.Drop)
            {
                NoteTransitionDrop($"QueryHealth(0x{item.Id:X8})", now - item.At);
                continue;
            }
            try { CombatActionHooks.QueryHealth(item.Id); } catch { }
        }
    }

    // ── Chat slot (WriteToChat strings) ──────────────────────────────────────────
    // WriteToChat carries a string, which can't ride the uint Entry queue. Off-thread
    // AddTextToScroll races AC's chat-scroll buffer and corrupts it (the recurring
    // 0x00460D1D write-AV that killed a 5h+ session 2026-06-05). Chat writes marshal
    // here and drain on the main thread from Drain(), alongside the item/movement
    // actions. WriteToChat's 100 ms rate-limit runs BEFORE the enqueue, so a bot retry
    // burst is dropped on the pump thread and this queue never fills with spam.
    private static readonly System.Collections.Generic.Queue<(string Text, int ChatType, long At, int Epoch)> _chatQueue = new();
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
            _chatQueue.Enqueue((text, chatType, NowMs, Volatile.Read(ref _loginEpoch)));
            return true;
        }
    }

    // Single-consumer drain on AC's main thread. Re-invokes WriteToChat, whose
    // IsOnMainThread gate is satisfied here so it runs AddTextToScroll directly.
    private static void DrainChat()
    {
        // Bounded like DrainChatCommands: a re-queue never spins the main thread.
        int budget;
        lock (_chatLock) budget = _chatQueue.Count;
        long now = NowMs;
        int epoch = Volatile.Read(ref _loginEpoch);
        while (budget-- > 0)
        {
            (string Text, int ChatType, long At, int Epoch) item;
            lock (_chatLock)
            {
                if (_chatQueue.Count == 0) return;
                item = _chatQueue.Dequeue();
            }
            if (item.Epoch != epoch || now - item.At > ChatMaxAgeMs)
            {
                NoteStale("a chat line", now - item.At, dropped: true);
                continue;
            }
            try { ClientHelperHooks.WriteToChat(item.Text, item.ChatType); }
            catch { }
        }
    }

    // ── Chat COMMAND slot (full ChatCommandDispatcher.Dispatch lines) ────────────
    // Deep-audit finding #4 (2026-06-18): ChatCommandDispatcher.Dispatch is not
    // network-only — it runs arbitrary plugin OnChatBarEnter handlers,
    // constructs/destructs AC's native PStringBase<char> (alloc/free in AC's
    // heap), and calls Event_Talk/Event_Emote/etc. directly against AC's live
    // chat-manager, all of which assumed (incorrectly) "safe from any thread".
    // Off-thread callers exist today: OnLoginCommandRunner (ThreadPool via
    // Task.Run), ChatFileDispatcher (FileSystemWatcher thread), and
    // Host.InvokeChatParser (plugin export, any thread). Gating inside
    // Dispatch() itself (rather than at each caller) covers all three at once.
    // A full command line can't ride the uint Entry ring, so it gets its own
    // string queue, same shape as the WriteToChat slot above.
    private static readonly System.Collections.Generic.Queue<(string Text, long At, int Epoch, ChatCommandOrigin Origin)> _chatCommandQueue = new();
    private static readonly object _chatCommandLock = new();
    private const int MaxChatCommandQueue = 64;

    // Off-thread enqueue. Drops (returns false) if the queue is full.
    public static bool EnqueueChatCommand(string text) => EnqueueChatCommand(text, ChatCommandOrigin.Script);

    // The same, saying who sent the line (Player: a RynthCore hotkey), for the answer of an engine
    // /rc command (ChatCommandDispatcher.Dispatch, RynthCoreChatCommands).
    public static bool EnqueueChatCommand(string text, ChatCommandOrigin origin)
    {
        lock (_chatCommandLock)
        {
            if (_chatCommandQueue.Count >= MaxChatCommandQueue)
            {
                Interlocked.Increment(ref _dropped);
                return false;
            }
            _chatCommandQueue.Enqueue((text, NowMs, Volatile.Read(ref _loginEpoch), origin));
            return true;
        }
    }

    // Single-consumer drain on AC's main thread. Re-invokes Dispatch, whose
    // IsOnMainThread gate is satisfied here so it runs the full body directly
    // instead of re-enqueuing.
    private static void DrainChatCommands()
    {
        // Only what was queued before this drain: anything a dispatch queues again
        // waits for the next frame instead of spinning here.
        int budget;
        lock (_chatCommandLock) budget = _chatCommandQueue.Count;
        long now = NowMs;
        int epoch = Volatile.Read(ref _loginEpoch);
        while (budget-- > 0)
        {
            (string Text, long At, int Epoch, ChatCommandOrigin Origin) item;
            lock (_chatCommandLock)
            {
                if (_chatCommandQueue.Count == 0) return;
                item = _chatCommandQueue.Dequeue();
            }
            if (item.Epoch != epoch || now - item.At > ChatCommandMaxAgeMs)
            {
                NoteStale("a chat command", now - item.At, dropped: true);
                continue;
            }
            try { ChatCommandDispatcher.Dispatch(item.Text, item.Origin); }
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
    private static readonly CastSlot _cast = new();   // writes under _castLock
    private static readonly object _castLock = new();

    // Ticks DrainCasts has deferred the pending cast because a motion gesture was
    // still in flight. Bounded so a stuck gesture (the documented wand-wield wedge
    // variant) can't park a cast forever: past the cap the cast is DROPPED — the
    // plugin's no-chat-resolve machinery already retries casts that produce no
    // chat, so a drop degrades to one retry cycle, never a wedge.
    private static long _castDeferDrops;


    /// <summary>Casts dropped after deferring CastDeferTickCap ticks (stuck gesture).</summary>
    public static long CastDeferDropCount => Interlocked.Read(ref _castDeferDrops);

    // Pump-thread enqueue. Returns false (caller retries next tick) if a cast is
    // already pending, or during a world transition (CastSlot): the drain would only hold
    // it and then drop it, and the slot would refuse every newer cast meanwhile.
    public static bool EnqueueCast(uint targetId, uint spellId)
    {
        CastSlot.Admit admit;
        lock (_castLock)
            admit = _cast.TryEnqueue(targetId, spellId, NowMs, PlayerLifecycleLog.InWorldTransition);
        if (admit == CastSlot.Admit.RejectedTransition)
            Interlocked.Increment(ref _transitionRejected);
        return admit == CastSlot.Admit.Queued;
    }

    // Casts that waited for a movement stop queued ahead of them (counted once per cast;
    // the per-cast flag is CastSlot.StopWaitNoted, reset by each new cast).
    private static long _castStopWaits;

    /// <summary>Casts that waited one or more ticks for a stop queued ahead of them.</summary>
    public static long CastStopWaitCount => Interlocked.Read(ref _castStopWaits);

    /// <summary>A movement stop (autorun off, a motion off, StopMovement, StopCompletely).</summary>
    private static bool IsMovementStop(in Entry e) => e.Kind switch
    {
        ActionKind.SetAutoRun => e.A == 0,
        ActionKind.SetMotion => e.B == 0,
        ActionKind.StopMovement or ActionKind.StopCompletely => true,
        _ => false,
    };

    /// <summary>
    /// True when the action ring holds a movement stop queued at or before <paramref name="castQueuedAt"/>.
    /// Main thread only (the ring's sole consumer). An entry's time is taken before ActionRing's
    /// producer lock, so two producers can land slightly out of time order: the walk checks every
    /// queued entry (at most Capacity) instead of stopping at the first newer one (CastStopRules).
    /// </summary>
    private static bool StopQueuedAtOrBefore(long castQueuedAt) =>
        CastStopRules.AnyStopAtOrBefore(_ring, castQueuedAt, static e => e.At, static e => IsMovementStop(e));

    // Drained on AC's MAIN thread from the Client::UseTime detour (game-logic tick).
    // Re-invokes CombatActionHooks.CastSpell, which on the main thread executes the
    // cast directly via the SEH trampoline (targeted: FreeHandsAndCastSpell with the target
    // passed explicitly, no SelectItem; self: ClientMagicSystem::CastSpell).
    public static void DrainCasts()
    {
        _drainDepth++;
        try { DrainCastsCore(); }
        finally { _drainDepth--; }
    }

    private static void DrainCastsCore()
    {
        if (_disarmed) return;                               // engine teardown in progress
        if (Volatile.Read(ref _cast.Pending) == 0) return;   // alloc-free fast path

        // ── Anim-walk race guard (dump-proven 2026-06-12) ────────────────────
        // DrainCasts runs PRE-tick (selection timing), but a cast initiates a
        // wind-up gesture that REPLACES the player's pending CSequence motions —
        // and the SAME UseTime call then walks that sequence. If a gesture is
        // already mid-flight when we inject, the walker can hit a freed/null
        // node: AV at acclient 0x00526840 AnimSequenceNode::get_part_frame
        // [null+0xC], full stack in CrashDumps\acclient_anim_av_4716.dmp
        // (7.6 h overnight soak, crash INSIDE _originalUseTime, our detour on
        // the stack below it). Same mechanism the post-tick reorder fixed for
        // UseObject/movement — casts were exempted and carried the residue.
        // Defer while the pending-motion list is non-empty (the exact structure
        // the tick walks); main-thread read, alloc-free, fail-open: if the read
        // itself fails we cast (pre-fix behavior) rather than park.
        // In milliseconds from the queueing (was 250 game ticks, about 4-8 s): past
        // CastDeferMaxMs the cast is dropped and logged with its age. RynthAi waits up to 7 s
        // for a cast's answer before it sends another (ActionPacer), so a drop here can't
        // leave two casts in flight.
        // World transition (dead / portal / landblock jump): hold the cast; a cast queued
        // during it is dropped once it ends (WorldTransitionState). Before the gesture check:
        // nothing about the player's motion is read while AC is tearing the world down.
        long nowCast = NowMs;
        long queuedAt = Volatile.Read(ref _cast.QueuedAt);
        WorldTransitionState.Verdict castVerdict = PlayerLifecycleLog.DecideQueued(nowCast, queuedAt, teardown: false);
        if (castVerdict == WorldTransitionState.Verdict.Hold)
            return;
        if (castVerdict == WorldTransitionState.Verdict.Drop)
        {
            uint droppedSpell;
            lock (_castLock) { droppedSpell = _cast.SpellId; _cast.Pending = 0; }
            NoteTransitionDrop($"CastSpell({droppedSpell})", nowCast - queuedAt);
            return;
        }

        long castAge = nowCast - queuedAt;
        if (PlayerPhysicsHooks.TryGetCastGestureInProgress(out bool gestureInFlight) && gestureInFlight)
        {
            if (castAge <= CastDeferMaxMs)
                return;                                      // retry next tick
            // Gesture stuck past the deadline — drop the cast instead of injecting
            // into a wedged motion graph; the plugin retries via no-chat-resolve.
            uint dropped;
            lock (_castLock) { dropped = _cast.SpellId; _cast.Pending = 0; }
            Interlocked.Increment(ref _castDeferDrops);
            NoteStale($"CastSpell({dropped}) behind a gesture", castAge, dropped: true);
            return;
        }

        // ── A stop queued before the cast goes first (2026-10-12) ─────────────
        // Casts drain here (Client::UseTime, before the tick), the action ring at EndScene,
        // so an autorun-off or a motion-off the bot queued BEFORE its cast used to run AFTER
        // it: the cast's FreeHandsAndCastSpell -> MaybeStopCompletely sent its stop with AC's
        // locomotion still on, and the queued stops followed as extra MoveToStates. Wait (at
        // most until EndScene drains the ring, about one frame; the ring's own gesture defer
        // is shorter than CastDeferMaxMs) while one is queued; past the limit cast anyway.
        if (StopQueuedAtOrBefore(queuedAt))
        {
            if (castAge <= CastDeferMaxMs)
            {
                if (!_cast.StopWaitNoted)
                {
                    _cast.StopWaitNoted = true;
                    Interlocked.Increment(ref _castStopWaits);
                    try { Net.FlightRecorder.Mark(Net.FlightMark.Note, text: "cast waits for a movement stop queued before it"); } catch { }
                }
                return;                                      // retry next tick, after the ring drains
            }
        }

        // ── The stop goes a server answer ahead of the cast (2026-10-12, PreCastStopRules) ──
        long castStamp = queuedAt;
        if (_preStopFor != castStamp) { _preStopFor = castStamp; _preStopSent = false; }
        long nowMs = NowMs;
        PreCastStep step = PreCastStopRules.Decide(
            _preStopSent ? 0 : CommandInterpreterHooks.ReadControlledByServer(),
            _preStopSent,
            Interlocked.Read(ref _playerMotionEchoes) - _preStopEchoBase,
            nowMs - _preStopAt);
        if (step == PreCastStep.SendStop)
        {
            if (castAge <= CastDeferMaxMs)
            {
                _preStopEchoBase = Interlocked.Read(ref _playerMotionEchoes);
                if (CommandInterpreterHooks.StopCompletely())
                {
                    _preStopSent = true;
                    _preStopAt = nowMs;
                    Interlocked.Increment(ref _preCastStops);
                    try { Net.FlightRecorder.Mark(Net.FlightMark.Note, text: "pre-cast stop sent; the cast waits for the server's answer"); } catch { }
                    return;                                  // cast on a later tick, after the answer
                }
            }
            step = PreCastStep.CastNow;                      // couldn't send it: cast as before
        }
        else if (step == PreCastStep.Wait)
        {
            if (castAge <= CastDeferMaxMs + PreCastStopRules.EchoMaxMs)
                return;
            step = PreCastStep.CastWithoutStop;
        }
        bool withoutStop = step == PreCastStep.CastWithoutStop;
        if (withoutStop && Interlocked.Read(ref _playerMotionEchoes) == _preStopEchoBase)
            Interlocked.Increment(ref _preCastEchoTimeouts);

        uint target, spell;
        lock (_castLock)
        {
            if (_cast.Pending == 0) return;
            target = _cast.TargetId;
            spell = _cast.SpellId;
            _cast.Pending = 0;
        }
        if (castAge > LateActionLogMs)
            NoteStale($"CastSpell({spell})", castAge, dropped: false);
        CombatActionHooks.CastSpell(target, unchecked((int)spell), suppressMoveStop: withoutStop);
    }

    // Pre-cast stop state (main thread only).
    private static long _preStopFor = long.MinValue;
    private static bool _preStopSent;
    private static long _preStopAt;
    private static long _preStopEchoBase;
    private static long _playerMotionEchoes;
    private static long _preCastStops;
    private static long _preCastEchoTimeouts;

    /// <summary>Casts whose stop the engine sent ahead of them.</summary>
    public static long PreCastStopCount => Interlocked.Read(ref _preCastStops);
    /// <summary>Of those, casts that went without seeing the server's answer (EchoMaxMs ran out).</summary>
    public static long PreCastEchoTimeoutCount => Interlocked.Read(ref _preCastEchoTimeouts);

    /// <summary>An autonomous UpdateMotion for the player arrived (SmartBoxHooks, main thread).</summary>
    public static void NotePlayerMotionEcho() => Interlocked.Increment(ref _playerMotionEchoes);
}
