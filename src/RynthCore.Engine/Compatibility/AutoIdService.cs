// ============================================================================
//  RynthCore.Engine - Compatibility/AutoIdService.cs
//
//  Automatically sends RequestId (appraisal) for objects so that property caches
//  (AppraisalHooks int/bool/string) are populated without requiring the player to
//  manually select each object.
//
//  Candidates come from CreateObjectHooks (and the Decal bridge's create event), from
//  items the server puts into a container (InventoryPutObjInContainer, SmartBoxHooks'
//  game-event hook), from a rescan of a container the player opened, and from the
//  one-shot login sweep of the pack. A background timer drains them (AutoIdDrain), at
//  most AutoIdDrain.MaxPerTick sends per DrainIntervalMs.
//
//  WHICH of them get an appraisal is two rules:
//    - AutoIdEligibility (on create): an object the client released and re-created is
//      not appraised again within 10 minutes of the last one while its data is still
//      cached, unless the server deleted it;
//    - AutoIdPolicy (on drain): the AutoIdMode (engine.json "AutoIdMode", /rc autoid).
//      Limited (the default) appraises only the selected target, the
//      items in the container the player has open, and items in the player's own
//      inventory with nothing cached (at most 5 a second); the last two wait for a
//      minimum object age and a defer after login and after every world transition.
//      All is the old behaviour; Off sends nothing.
//
//  Every send is queued with who asked (Net/IdRequester.cs), so the flight recorder tags
//  it [eng:autoid-target|autoid-container|autoid-inventory|autoid-world].
// ============================================================================

using System;
using System.Threading;
using RynthCore.Engine.Net;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.Compatibility;

internal static class AutoIdService
{
    // How often the drain timer fires (ms)
    private const int DrainIntervalMs = 100;

    // A container the player opened is rescanned this long after the open, so items the
    // client already knew (created before the open, or by an earlier open) are candidates too.
    private const long ContainerRescanDelayMs = AutoIdPolicy.MinAgeMs + 500;

    // How long an appraisal stays good enough not to repeat: a client re-create inside
    // this window, and the selected-target priority appraisal, skip an object whose
    // appraisal is cached and this recent.
    private const long ReappraiseWindowMs = AutoIdEligibility.DefaultWindowMs;

    // Per-object appraisal bookkeeping. Written on AC's main thread (create, client
    // release), the plugin pump (server delete) and the drain timer; every access holds
    // _gate, for a few dictionary operations only.
    private static readonly AutoIdEligibility _eligibility = new(ReappraiseWindowMs);
    private static readonly object _gate = new();

    private static readonly AutoIdDrain _drain = new(new LiveWorld());
    private static int _draining;

    private static Timer? _drainTimer;
    private static bool _started;

    private static volatile int _mode = (int)AutoIdPolicy.DefaultMode;

    // The container the player opened last (ViewObjectContents), and when.
    private static int _openedContainerId;
    private static long _openedAtMs;
    private static long _rescanDueMs;

    private static long _skippedPriorityFresh;

    /// <summary>The engine behind AutoIdDrain: the PWD snapshot, the appraisal cache, eligibility, the main-thread queue.</summary>
    private sealed class LiveWorld : IAutoIdWorld
    {
        public bool TryGetOwnership(uint id, out uint container, out uint wielder) =>
            ClientObjectHooks.TryGetObjectOwnershipInfo(id, out container, out wielder, out _);

        public bool SnapshotWarm => ClientObjectHooks.HasPwdSnapshot;

        public int CollectOwned(uint container, Span<uint> dest) =>
            ClientObjectHooks.CollectOwnedIdsFromSnapshot(container, dest);

        public bool IsLive(uint id)
        {
            lock (_gate) return _eligibility.IsLive(id);
        }

        public bool HasAppraisal(uint id) => AppraisalHooks.HasAppraisalData(id);

        public bool IsFresh(uint id) => AutoIdEligibility.IsAppraisalFresh(
            AppraisalHooks.HasAppraisalData(id), AppraisalHooks.GetLastIdTime(id),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ReappraiseWindowMs);

        // Marshal onto AC's main thread (EndScene drain): the Timer thread must not call the
        // native 0xC8 send directly (off-thread heap alloc + non-atomic UI-counter increment).
        // The appraisal counts as sent only when the main thread sends it (OnSent, from
        // AcMainThreadQueue.DrainRequestIds): a queued request dropped there leaves it due again.
        // Block-bodied, and named apart from the create-path Enqueue, so
        // tools/check_offthread_mutators.py scans it and its allow-list entry covers only this.
        public bool QueueIdentify(uint id, IdRequester who)
        {
            return AcMainThreadQueue.EnqueueRequestId(id, who);
        }

        public bool IsContainer(uint id) => ClientObjectHooks.IsContainerInSnapshot(id);
    }

    /// <summary>Re-creates not appraised because a recent appraisal is still cached.</summary>
    public static long SkippedRecentCount
    {
        get { lock (_gate) return _eligibility.SkippedRecentTotal; }
    }

    /// <summary>Selected-target priority appraisals skipped because the target's data is cached and fresh.</summary>
    public static long SkippedPriorityFreshCount => Interlocked.Read(ref _skippedPriorityFresh);

    /// <summary>The live mode (engine.json "AutoIdMode", /rc autoid).</summary>
    public static AutoIdMode Mode => (AutoIdMode)_mode;

    /// <summary>Switch the mode now; <paramref name="persist"/> also saves it to engine.json.</summary>
    public static void SetMode(AutoIdMode mode, bool persist)
    {
        _mode = (int)mode;
        if (persist)
            EngineSettings.AutoIdMode = mode;
        RynthLog.Compat($"Compat: AutoId mode {AutoIdPolicy.Name(mode)}.");
    }

    public static void Start()
    {
        if (_started)
            return;
        _started = true;
        try { _mode = (int)EngineSettings.AutoIdMode; } catch { }
        _drainTimer = new Timer(DrainTick, null, DrainIntervalMs, DrainIntervalMs);
        RynthLog.Verbose($"Compat: AutoIdService started (mode {AutoIdPolicy.Name(Mode)}).");
    }

    /// <summary>
    /// Dispose the drain timer at engine shutdown so it doesn't keep firing in
    /// the old generation's (intentionally still-mapped) module across hot-reloads.
    /// </summary>
    public static void Stop()
    {
        if (!_started)
            return;
        _started = false;
        _drainTimer?.Dispose();
        _drainTimer = null;
        RynthLog.Verbose("Compat: AutoIdService stopped.");
    }

    /// <summary>
    /// Enqueue an object for automatic appraisal. Called from CreateObjectHooks detour
    /// (AC's main thread) and the Decal bridge's create event.
    /// </summary>
    public static void Enqueue(uint objectId)
    {
        if (objectId == 0)
            return;

        // Skip the player character
        uint playerId = ClientHelperHooks.GetPlayerId();
        if (playerId != 0 && objectId == playerId)
            return;

        // Created again: data kept since a client release is current again (and still cached
        // if the release was inside ReleasedObjectRetention's time), unless it was kept so long
        // that it may be out of date (StaleAfterMs): then it is dropped and the object, uncached,
        // is a candidate again.
        long now = Environment.TickCount64;
        AppraisalHooks.MarkLive(objectId, now);
        bool cached = AppraisalHooks.HasAppraisalData(objectId);
        AutoIdEligibility.CreateDecision decision;
        lock (_gate)
            decision = _eligibility.OnCreate(objectId, now, cached);

        if (decision == AutoIdEligibility.CreateDecision.Appraise)
            _drain.Offer(objectId, now);
    }

    /// <summary>
    /// The server put <paramref name="itemId"/> into <paramref name="containerId"/>
    /// (InventoryPutObjInContainer, game event 0x0022; SmartBoxHooks' game-event hook on AC's
    /// main thread, or the Decal bridge on the pump). In Limited mode an item that landed in
    /// the player's inventory with nothing cached is a candidate; where it landed is decided
    /// at the drain, from the object snapshot.
    /// </summary>
    public static void OnPutInContainer(uint itemId, uint containerId)
    {
        if (itemId == 0 || containerId == 0 || Mode != AutoIdMode.Limited)
            return;
        if (AppraisalHooks.HasAppraisalData(itemId))
            return;
        _drain.Offer(itemId, Environment.TickCount64);
    }

    /// <summary>
    /// The client opened <paramref name="containerId"/>'s contents (ViewObjectContents, AC's
    /// main thread). Its time decides the defer exemption, and a rescan after the minimum
    /// age makes the items already known in it candidates.
    /// </summary>
    public static void OnContainerOpened(uint containerId)
    {
        if (containerId == 0) return;
        long now = Environment.TickCount64;
        Interlocked.Exchange(ref _openedAtMs, now);
        Interlocked.Exchange(ref _openedContainerId, unchecked((int)containerId));
        Interlocked.Exchange(ref _rescanDueMs, now + ContainerRescanDelayMs);
    }

    /// <summary>
    /// The CLIENT released an object (ACCObjectMaint::DeleteObject, or the Decal bridge's
    /// delete event). A queued appraisal for it is skipped; its last appraisal time is kept,
    /// so a re-create inside the window is not appraised again.
    /// </summary>
    public static void OnClientRelease(uint objectId)
    {
        if (objectId == 0) return;
        lock (_gate)
            _eligibility.OnClientRelease(objectId);
    }

    /// <summary>
    /// The SERVER deleted an object (0xF747 ObjectDelete, from the server message stream on
    /// the plugin pump). Whatever is created under that id next is appraised.
    /// </summary>
    public static void OnServerDelete(uint objectId)
    {
        if (objectId == 0) return;
        lock (_gate)
            _eligibility.OnServerDelete(objectId);
        // It really went away: its kept identify data goes too.
        AppraisalHooks.EvictObject(objectId);
    }

    /// <summary>
    /// The 0xC8 for <paramref name="objectId"/> really went out (AcMainThreadQueue's drain, on
    /// AC's main thread, for every RequestId caller). Starts its re-appraisal window.
    /// </summary>
    public static void OnSent(uint objectId, long nowMs)
    {
        if (objectId == 0) return;
        lock (_gate)
            _eligibility.OnSent(objectId, nowMs);
    }

    // Last target we issued a priority appraisal for (fires once per target change).
    private static uint _lastPriorityTarget;
    private static long _lastPruneMs;

    private static void DrainTick(object? state)
    {
        // A slow tick must not overlap the next one: the drain's pending list is this timer's alone.
        if (Interlocked.CompareExchange(ref _draining, 1, 0) != 0)
            return;
        try
        {
            if (!CombatActionHooks.HasRequestId)
                return;
            // Dead / portaling / settling: AcMainThreadQueue refuses appraisals now, so the
            // backlog waits here instead of being dequeued into a refusal and lost.
            if (PlayerLifecycleLog.InWorldTransition)
                return;

            long now = Environment.TickCount64;
            AutoIdMode mode = Mode;
            int sent = 0;
            uint playerId = ClientHelperHooks.GetPlayerId();

            // Combat-target priority: when the selected target CHANGES, appraise it
            // immediately, ahead of the backlog, so a fast kill still gets its max-HP
            // appraisal out first. Fires once per change (guarded), so no per-tick
            // re-appraisal. Skipped when the target's appraisal is already cached and
            // fresh. Exempt from the minimum age, the defers and the inventory cap. A
            // refused enqueue forgets the change, so the next tick tries again.
            uint target = SelectedTargetHooks.ReadCurrentSelectedId();
            if (target != 0 && target != _lastPriorityTarget)
            {
                _lastPriorityTarget = target;
                if (target != playerId)
                {
                    bool cached = AppraisalHooks.HasAppraisalData(target);
                    bool fresh = AutoIdEligibility.IsAppraisalFresh(
                        cached, AppraisalHooks.GetLastIdTime(target), DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ReappraiseWindowMs);
                    switch (AutoIdPolicy.Decide(mode, AutoIdPolicy.Class.Target, 0, false, false, fresh, cached))
                    {
                        case AutoIdPolicy.Verdict.SkipFresh:
                            Interlocked.Increment(ref _skippedPriorityFresh);
                            _drain.CountSkipFresh();
                            break;
                        case AutoIdPolicy.Verdict.Send:
                            if (AcMainThreadQueue.EnqueueRequestId(target, IdRequester.AutoIdTarget))
                                sent++;
                            else
                                _lastPriorityTarget = 0;
                            break;
                        default:
                            _drain.CountSkipMode();
                            break;
                    }
                }
            }

            RescanOpenedContainer(now, mode);

            uint open = ClientHelperHooks.GetGroundContainerId();
            var ctx = new AutoIdDrain.Context
            {
                Now = now,
                Mode = mode,
                PlayerId = playerId,
                OpenContainer = open,
                OpenedAtMs = open != 0 && unchecked((uint)Volatile.Read(ref _openedContainerId)) == open
                    ? Interlocked.Read(ref _openedAtMs) : 0,
                SettleUntilMs = PlayerLifecycleLog.SettleUntilMs,
                LoginCompleteAtMs = LoginLifecycleHooks.LoginCompleteAtMs,
                // Each 0xC8 raises AC's busy count, which plugins gate their casts on: the
                // pack waits while a cast is in flight or an attack runs (not for a selection alone).
                CombatBusy = AutoIdPolicy.InCombat(target, CastGate.GetCastBusyState(),
                    ClientActionGates.TryReadAttacking(out bool attacking) && attacking),
            };
            _drain.Tick(ctx, sent);

            // Keep the bookkeeping bounded. Here, off AC's main thread: the create path
            // never prunes.
            if (now - _lastPruneMs >= 10_000)
            {
                _lastPruneMs = now;
                int removed;
                lock (_gate)
                    removed = _eligibility.Count > _eligibility.MaxTracked / 2 ? _eligibility.Prune(now) : 0;
                if (removed > 0)
                    RynthLog.Verbose($"Compat: AutoIdService pruned {removed} appraisal record(s).");
                // Identify data kept after a client release: dropped once its time is up.
                int expired = AppraisalHooks.ExpireReleased(now);
                if (expired > 0)
                    RynthLog.Verbose($"Compat: AppraisalHooks dropped {expired} released object(s) past the retention time.");
            }
        }
        catch { }
        finally
        {
            Volatile.Write(ref _draining, 0);
        }
    }

    /// <summary>Limited mode: the items already in a container the player opened become candidates.</summary>
    private static void RescanOpenedContainer(long now, AutoIdMode mode)
    {
        long due = Interlocked.Read(ref _rescanDueMs);
        if (due == 0 || now < due)
            return;
        Interlocked.CompareExchange(ref _rescanDueMs, 0, due);
        if (mode != AutoIdMode.Limited)
            return;
        uint container = unchecked((uint)Volatile.Read(ref _openedContainerId));
        if (container == 0 || ClientHelperHooks.GetGroundContainerId() != container)
            return;   // closed again (or another one opened: its own rescan follows)
        _drain.AddContainerContents(container, Interlocked.Read(ref _openedAtMs), mode);
    }

    // -- counters: /rc autoid and the per-minute log line --

    private static readonly IdRequester[] Sources =
    {
        IdRequester.AutoIdTarget, IdRequester.AutoIdContainer, IdRequester.AutoIdInventory, IdRequester.AutoIdWorld,
        IdRequester.Plugin, IdRequester.Self, IdRequester.Ui, IdRequester.None,
    };
    private static readonly long[] _lastSent = new long[IdRequesters.Count];
    private static long _lastForeign, _lastSkipMode, _lastSkipAge, _lastSkipDefer, _lastSkipFresh, _lastSkipRate, _lastSkipCombat;

    /// <summary>
    /// The last minute's IdentifyObject counts (heartbeat thread, once a minute): sent per
    /// requester, cli = sent by AC or Decal (from the flight recorder; ? while it is off),
    /// and AutoId's skips by reason. None of its fields contains fps=, plug=, login= or qd=
    /// (the launcher's heartbeat regex), and the line does not start with "hb #".
    /// </summary>
    public static string TakeMinuteLine()
    {
        long foreign = FlightRecorder.ForeignIdentifyTotal;
        long sm = _drain.SkipMode, sa = _drain.SkipAge, sd = _drain.SkipDefer, sf = _drain.SkipFresh, sr = _drain.SkipRate, sc = _drain.SkipCombat;
        var sb = new System.Text.StringBuilder(176);
        sb.Append("autoid 60s: mode=").Append(AutoIdPolicy.Name(Mode)).Append(" sent");
        long total = 0;
        foreach (IdRequester who in Sources)
        {
            long now = IdSendStats.Sent(who);
            long d = now - _lastSent[(int)who];
            _lastSent[(int)who] = now;
            total += d;
            if (who == IdRequester.None && d == 0) continue;   // unmarked engine sends: shown only when there are any
            sb.Append(' ').Append(IdRequesters.Short(who)).Append('=').Append(d);
        }
        if (foreign >= 0)
        {
            long d = foreign - _lastForeign;
            _lastForeign = foreign;
            total += d;
            sb.Append(" cli=").Append(d);
        }
        else
        {
            sb.Append(" cli=?");
        }
        sb.Append(" tot=").Append(total);
        sb.Append(" skip mode=").Append(sm - _lastSkipMode).Append(" age=").Append(sa - _lastSkipAge)
          .Append(" defer=").Append(sd - _lastSkipDefer).Append(" fresh=").Append(sf - _lastSkipFresh)
          .Append(" rate=").Append(sr - _lastSkipRate).Append(" combat=").Append(sc - _lastSkipCombat);
        sb.Append(" pend=").Append(_drain.PendingCount);
        _lastSkipMode = sm; _lastSkipAge = sa; _lastSkipDefer = sd; _lastSkipFresh = sf; _lastSkipRate = sr; _lastSkipCombat = sc;
        return sb.ToString();
    }

    /// <summary>/rc autoid [all|limited|off|status]</summary>
    public static string[] HandleCommand(string args)
    {
        string verb = (args ?? "").Trim();
        if (verb.Length > 0 && !verb.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            if (!AutoIdPolicy.TryParseMode(verb, out AutoIdMode mode))
                return new[] { "Usage: /rc autoid [all|limited|off]  (alone: status)" };
            SetMode(mode, persist: true);
        }
        return StatusLines();
    }

    public static string[] StatusLines()
    {
        var sent = new System.Text.StringBuilder();
        foreach (IdRequester who in Sources)
        {
            long n = IdSendStats.Sent(who);
            if (who == IdRequester.None && n == 0) continue;
            sent.Append(sent.Length > 0 ? ", " : "").Append(IdRequesters.Short(who)).Append(' ').Append(n);
        }
        long foreign = FlightRecorder.ForeignIdentifyTotal;
        string modeText = Mode switch
        {
            AutoIdMode.All => "all (every created object, the old behaviour)",
            AutoIdMode.Off => "off (no background appraisals, not even the selected target)",
            _ => $"limited (selected target; open container and own inventory after {AutoIdPolicy.MinAgeMs / 1000} s, {AutoIdPolicy.DeferMs / 1000} s after login or a portal/death/teleport; inventory at most {1000 / AutoIdPolicy.InventoryMinIntervalMs}/s, and not while a cast or attack is in flight)",
        };
        string extra = "";
        if (_drain.Overflow > 0) extra += $", overflow {_drain.Overflow}";
        if (_drain.IncomingDropped > 0) extra += $", dropped while held {_drain.IncomingDropped}";
        return new[]
        {
            $"AutoId: {modeText}. Saved in engine.json; /rc autoid all|limited|off switches it now. Plugins' own identifies are never limited.",
            $"Identifies sent since start: {sent}; by AC or Decal: {(foreign >= 0 ? foreign.ToString() : "? (flight recorder off)")}.",
            $"AutoId skipped: mode {_drain.SkipMode}, fresh {_drain.SkipFresh}, recent re-create {SkippedRecentCount}; " +
            $"waited: age {_drain.SkipAge}, defer {_drain.SkipDefer}, rate {_drain.SkipRate}, combat {_drain.SkipCombat}; refused {_drain.Refused} (kept); " +
            $"login sweep {_drain.Swept}; pending now {_drain.PendingCount}{extra}.",
        };
    }
}
