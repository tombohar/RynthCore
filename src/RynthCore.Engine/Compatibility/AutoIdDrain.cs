// ============================================================================
//  RynthCore.Engine - Compatibility/AutoIdDrain.cs
//
//  AutoIdService's backlog: candidates in (any thread), decisions and sends out (the
//  drain timer). Everything it needs from the engine comes through IAutoIdWorld, so it
//  runs in tools\EngineRobustnessTests against a fake world and a fake clock.
//
//  Per tick: take the new candidates, the one-shot login sweep, then look at the pending
//  list in order (at most MaxEvaluatedPerTick, at most MaxPerTick sends) and decide each
//  one with AutoIdPolicy. When the evaluation budget runs out with items still unlooked-at,
//  the ones just evaluated (and kept) move to the back, so the next tick starts where this
//  one stopped: with more than MaxEvaluatedPerTick items waiting, a newer item at the end
//  is reached in a few ticks instead of never (review fix 2026-10-18). Waits stay; skips leave; a send leaves only when the main-thread
//  queue took it (a refusal, in a transition or with the ring full, keeps the entry and
//  ends this tick's sending).
//
//  Login sweep: the pack is created before the 10 Hz object
//  snapshot is warm (ClientObjectHooks.PrefetchPositions only starts after the post-login
//  bootstrap), so a pack item created at login may still be unplaceable when its create
//  candidate is decided. Once per login (and per hot reload, which marks a login), at
//  login-complete + DeferMs and with the snapshot warm, every uncached item the player
//  owns, and every item in a pack the player owns, becomes a candidate. The 5 a second
//  Inventory cap still applies. An Unknown candidate also waits for the snapshot (up to
//  AutoIdPolicy.UnknownMaxWaitMs) instead of being dropped at MinAgeMs. Only containers are
//  looked into, and a sweep that overflowed the pending list counts as not done: it runs
//  again once there is room.
//
//  Combat: Inventory sends wait while a cast or attack is in flight
//  (Context.CombatBusy, AutoIdPolicy.InCombat; a selected target alone does not count). Each 0xC8 raises AC's busy count, and plugins gate
//  their casts on it. The selected target and an open container are not held.
//
//  Bounds: _incoming is capped at MaxIncoming (oldest dropped, counted) because the drain
//  returns early through a world transition (a long death); the pending list is capped at
//  MaxPendingLimited in Limited mode only (All keeps the old unbounded backlog).
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using RynthCore.Engine.Net;

namespace RynthCore.Engine.Compatibility;

/// <summary>What AutoIdDrain reads and does in the engine (a fake in the tests).</summary>
internal interface IAutoIdWorld
{
    /// <summary>The object's container and wielder from the object snapshot; false when it is not in it.</summary>
    bool TryGetOwnership(uint id, out uint container, out uint wielder);
    /// <summary>The object snapshot has been filled at least once (it holds any object at all).</summary>
    bool SnapshotWarm { get; }
    /// <summary>Ids whose container or wielder is <paramref name="container"/>, from the snapshot.</summary>
    int CollectOwned(uint container, Span<uint> dest);
    /// <summary>Created and not released (AutoIdEligibility).</summary>
    bool IsLive(uint id);
    /// <summary>Its appraisal is cached.</summary>
    bool HasAppraisal(uint id);
    /// <summary>Its appraisal is cached and recent.</summary>
    bool IsFresh(uint id);
    /// <summary>Queue the 0xC8 for AC's main thread; false = refused (transition, ring full).</summary>
    bool QueueIdentify(uint id, IdRequester who);
    /// <summary>It holds items (a pack, a chest): only these are looked into by the login sweep.</summary>
    bool IsContainer(uint id);
}

internal sealed class AutoIdDrain
{
    public const int MaxPerTick = 3;
    public const int MaxEvaluatedPerTick = 512;
    public const int MaxPendingLimited = 4096;
    public const int MaxIncoming = 16384;
    public const int MaxSweepItems = 512;

    /// <summary>One tick's view of the engine (read by AutoIdService each tick).</summary>
    public struct Context
    {
        public long Now;
        public AutoIdMode Mode;
        public uint PlayerId;
        public uint OpenContainer;
        public long OpenedAtMs;          // when OpenContainer was opened (0 = unknown)
        public long SettleUntilMs;       // PlayerLifecycleLog.SettleUntilMs
        public long LoginCompleteAtMs;   // LoginLifecycleHooks.LoginCompleteAtMs
        public bool CombatBusy;          // a cast or attack in flight, AutoIdPolicy.InCombat (Inventory waits)
    }

    private struct Pending
    {
        public uint Id;
        public long AtMs;      // created (or moved, or its container opened, or swept) at
        public byte Counted;   // waits already counted: 1 = age, 2 = defer, 4 = rate, 8 = combat
    }

    private readonly IAutoIdWorld _w;
    private readonly ConcurrentQueue<(uint Id, long AtMs)> _incoming = new();
    private int _incomingCount;
    private readonly List<Pending> _pending = new(256);
    private readonly HashSet<uint> _pendingIds = new();
    private readonly List<Pending> _rotateScratch = new(MaxEvaluatedPerTick);
    private int _pendingCount;
    private long _lastInventorySendMs;
    private long _sweptLoginAt;

    private long _skipMode, _skipAge, _skipDefer, _skipFresh, _skipRate, _skipCombat, _overflow, _incomingDropped, _refused, _swept;

    public AutoIdDrain(IAutoIdWorld world) { _w = world; }

    public long SkipMode => Interlocked.Read(ref _skipMode);
    public long SkipAge => Interlocked.Read(ref _skipAge);
    public long SkipDefer => Interlocked.Read(ref _skipDefer);
    public long SkipFresh => Interlocked.Read(ref _skipFresh);
    public long SkipRate => Interlocked.Read(ref _skipRate);
    /// <summary>Inventory candidates that waited for combat to end (counted once each).</summary>
    public long SkipCombat => Interlocked.Read(ref _skipCombat);
    /// <summary>Candidates not taken because the Limited pending list was full.</summary>
    public long Overflow => Interlocked.Read(ref _overflow);
    /// <summary>Oldest new candidates dropped because _incoming was full (the drain was held).</summary>
    public long IncomingDropped => Interlocked.Read(ref _incomingDropped);
    /// <summary>Sends the main-thread queue refused (kept and tried again).</summary>
    public long Refused => Interlocked.Read(ref _refused);
    /// <summary>Items the login sweeps made candidates.</summary>
    public long Swept => Interlocked.Read(ref _swept);
    public int PendingCount => Volatile.Read(ref _pendingCount);

    /// <summary>The selected-target path counts its fresh / mode skips here too.</summary>
    public void CountSkipFresh() => Interlocked.Increment(ref _skipFresh);
    public void CountSkipMode() => Interlocked.Increment(ref _skipMode);

    /// <summary>A new candidate (any thread). Past MaxIncoming the oldest waiting one is dropped.</summary>
    public void Offer(uint id, long atMs)
    {
        if (id == 0) return;
        _incoming.Enqueue((id, atMs));
        if (Interlocked.Increment(ref _incomingCount) > MaxIncoming && _incoming.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _incomingCount);
            Interlocked.Increment(ref _incomingDropped);
        }
    }

    /// <summary>Drain thread: the items in <paramref name="container"/> with nothing cached become candidates.</summary>
    public void AddContainerContents(uint container, long atMs, AutoIdMode mode)
    {
        if (container == 0) return;
        Span<uint> ids = stackalloc uint[256];
        int n = _w.CollectOwned(container, ids);
        for (int i = 0; i < n; i++)
            if (_w.IsLive(ids[i]) && !_w.HasAppraisal(ids[i]))
                AddPending(ids[i], atMs, mode);
    }

    /// <summary>Drain thread, once per timer tick. Returns the sends so far (starting at <paramref name="sent"/>).</summary>
    public int Tick(in Context c, int sent)
    {
        TakeIncoming(c.Mode);
        LoginSweep(c);
        return DrainPending(c, sent);
    }

    private void TakeIncoming(AutoIdMode mode)
    {
        while (_incoming.TryDequeue(out (uint Id, long AtMs) x))
        {
            Interlocked.Decrement(ref _incomingCount);
            AddPending(x.Id, x.AtMs, mode);
        }
    }

    private void AddPending(uint id, long atMs, AutoIdMode mode)
    {
        if (id == 0 || _pendingIds.Contains(id))
            return;
        if (mode != AutoIdMode.All && _pending.Count >= MaxPendingLimited)
        {
            Interlocked.Increment(ref _overflow);
            return;
        }
        _pendingIds.Add(id);
        _pending.Add(new Pending { Id = id, AtMs = atMs });
    }

    /// <summary>Once per login-complete stamp, at login + DeferMs with the snapshot warm: the uncached pack.</summary>
    private void LoginSweep(in Context c)
    {
        if (c.Mode != AutoIdMode.Limited || c.LoginCompleteAtMs <= 0 || c.LoginCompleteAtMs == _sweptLoginAt)
            return;
        if (c.Now < c.LoginCompleteAtMs + AutoIdPolicy.DeferMs || c.PlayerId == 0 || !_w.SnapshotWarm)
            return;
        // A full pending list: try again on a later tick (no walk, no overflow counted).
        if (_pending.Count >= MaxPendingLimited)
            return;
        Span<uint> top = stackalloc uint[MaxSweepItems];
        Span<uint> inner = stackalloc uint[MaxSweepItems];
        int n = _w.CollectOwned(c.PlayerId, top);
        int before = _pending.Count;
        long overflowBefore = Interlocked.Read(ref _overflow);
        for (int i = 0; i < n; i++)
        {
            uint id = top[i];
            if (_w.IsLive(id) && !_w.HasAppraisal(id))
                AddPending(id, c.LoginCompleteAtMs, c.Mode);
            // A side pack: its contents are the pack too. Only a container is looked into.
            if (!_w.IsContainer(id))
                continue;
            int m = _w.CollectOwned(id, inner);
            for (int k = 0; k < m; k++)
                if (_w.IsLive(inner[k]) && !_w.HasAppraisal(inner[k]))
                    AddPending(inner[k], c.LoginCompleteAtMs, c.Mode);
        }
        Interlocked.Add(ref _swept, _pending.Count - before);
        // Done only when everything fit; an overflowed sweep runs again once there is room
        // (what it already added is not added twice: AddPending skips ids already pending).
        if (Interlocked.Read(ref _overflow) == overflowBefore)
            _sweptLoginAt = c.LoginCompleteAtMs;
    }

    private int DrainPending(in Context c, int sent)
    {
        if (_pending.Count == 0)
        {
            Volatile.Write(ref _pendingCount, 0);
            return sent;
        }

        long transitionEnd = AutoIdPolicy.TransitionEndMs(c.SettleUntilMs, c.LoginCompleteAtMs);
        bool inDefer = AutoIdPolicy.InDefer(c.Now, c.SettleUntilMs, c.LoginCompleteAtMs, inTransition: false);
        bool openedAfterSettle = AutoIdPolicy.OpenedAfterSettle(c.OpenedAtMs, transitionEnd);
        bool snapshotWarm = _w.SnapshotWarm;
        bool sending = true;

        int keep = 0, evaluated = 0;
        int keptBeforeBudgetEnd = -1;   // evaluated-and-kept items at the front when the budget ran out
        for (int i = 0; i < _pending.Count; i++)
        {
            Pending p = _pending[i];
            if (!sending || sent >= MaxPerTick || evaluated >= MaxEvaluatedPerTick)
            {
                if (keptBeforeBudgetEnd < 0 && sending && sent < MaxPerTick)
                    keptBeforeBudgetEnd = keep;   // only the evaluation budget ended it (not a send limit or a refusal)
                _pending[keep++] = p;
                continue;
            }
            evaluated++;

            // Released since it was queued: gone, without spending a send.
            if (!_w.IsLive(p.Id))
            {
                _pendingIds.Remove(p.Id);
                continue;
            }

            AutoIdPolicy.Class cls = Classify(p.Id, c.PlayerId, c.OpenContainer);
            bool cached = _w.HasAppraisal(p.Id);
            bool fresh = cached && _w.IsFresh(p.Id);
            bool rateOk = AutoIdPolicy.InventoryRateAllows(c.Now, _lastInventorySendMs);
            AutoIdPolicy.Verdict v = AutoIdPolicy.Decide(c.Mode, cls, c.Now - p.AtMs, inDefer, openedAfterSettle,
                                                         fresh, cached, rateOk, snapshotWarm, c.CombatBusy);
            switch (v)
            {
                case AutoIdPolicy.Verdict.Send:
                    if (!_w.QueueIdentify(p.Id, RequesterFor(cls)))
                    {
                        // Refused (a transition began, or the ring is full): keep it, and
                        // stop sending this tick.
                        Interlocked.Increment(ref _refused);
                        _pending[keep++] = p;
                        sending = false;
                        break;
                    }
                    sent++;
                    if (cls == AutoIdPolicy.Class.Inventory && c.Mode == AutoIdMode.Limited)
                        _lastInventorySendMs = c.Now;
                    _pendingIds.Remove(p.Id);
                    break;
                case AutoIdPolicy.Verdict.WaitAge:
                case AutoIdPolicy.Verdict.WaitSnapshot:
                    if ((p.Counted & 1) == 0) { p.Counted |= 1; Interlocked.Increment(ref _skipAge); }
                    _pending[keep++] = p;
                    break;
                case AutoIdPolicy.Verdict.WaitDefer:
                    if ((p.Counted & 2) == 0) { p.Counted |= 2; Interlocked.Increment(ref _skipDefer); }
                    _pending[keep++] = p;
                    break;
                case AutoIdPolicy.Verdict.WaitRate:
                    if ((p.Counted & 4) == 0) { p.Counted |= 4; Interlocked.Increment(ref _skipRate); }
                    _pending[keep++] = p;
                    break;
                case AutoIdPolicy.Verdict.WaitCombat:
                    if ((p.Counted & 8) == 0) { p.Counted |= 8; Interlocked.Increment(ref _skipCombat); }
                    _pending[keep++] = p;
                    break;
                case AutoIdPolicy.Verdict.SkipFresh:
                    Interlocked.Increment(ref _skipFresh);
                    _pendingIds.Remove(p.Id);
                    break;
                default:
                    Interlocked.Increment(ref _skipMode);
                    _pendingIds.Remove(p.Id);
                    break;
            }
        }
        _pending.RemoveRange(keep, _pending.Count - keep);
        // Round-robin: [evaluated and kept | not looked at] becomes [not looked at | evaluated and kept].
        if (keptBeforeBudgetEnd > 0 && keptBeforeBudgetEnd < _pending.Count)
        {
            _rotateScratch.Clear();
            for (int i = 0; i < keptBeforeBudgetEnd; i++) _rotateScratch.Add(_pending[i]);
            _pending.RemoveRange(0, keptBeforeBudgetEnd);
            _pending.AddRange(_rotateScratch);
            _rotateScratch.Clear();
        }
        Volatile.Write(ref _pendingCount, _pending.Count);
        return sent;
    }

    /// <summary>Where the object is now, from the snapshot.</summary>
    private AutoIdPolicy.Class Classify(uint id, uint playerId, uint open)
    {
        if (!_w.TryGetOwnership(id, out uint container, out uint wielder))
            return AutoIdPolicy.Classify(false, playerId, open, 0, 0, 0);
        uint parent = 0;
        if (container != 0 && container != playerId && container != open
            && _w.TryGetOwnership(container, out uint cc, out _))
            parent = cc;
        return AutoIdPolicy.Classify(true, playerId, open, container, wielder, parent);
    }

    public static IdRequester RequesterFor(AutoIdPolicy.Class cls) => cls switch
    {
        AutoIdPolicy.Class.Target => IdRequester.AutoIdTarget,
        AutoIdPolicy.Class.Container => IdRequester.AutoIdContainer,
        AutoIdPolicy.Class.Inventory => IdRequester.AutoIdInventory,
        _ => IdRequester.AutoIdWorld,
    };
}
