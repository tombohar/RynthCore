// ============================================================================
//  RynthCore.Engine - Compatibility/PlayerLifecycleLog.cs
//  One log line per player lifecycle edge: death (health reaches 0), revival,
//  portal space entered/exited, and landblock jumps (a teleport, not walking
//  into the next landblock), each with the landblock.
//
//  Why: 2026-09-28 a client crashed in AC's own teardown code
//  (acclient+0x18712F, null read in a hash-table clear) during the death
//  portal to the lifestone, and nothing in the log recorded the death or the
//  teleport. With these lines a crash can be lined up with what the character
//  was doing.
//
//  Also the world-transition gate (WorldTransitionState): while the player is dead,
//  portaling, or settling after either or after a landblock jump, AcMainThreadQueue
//  holds every queued game-mutating call instead of running it into a world AC is
//  tearing down (a client died right after a death recall's landblock jump).
//
//  Sampled from GameTickHooks.UseTimeDetour on AC's main thread, before AC's
//  tick and before the cast drain, so the reads see a consistent world and the gate
//  knows about a death in the same tick. Alloc-free except on an edge.
// ============================================================================

using System;
using System.Threading;

namespace RynthCore.Engine.Compatibility;

internal static class PlayerLifecycleLog
{
    // Main thread only (the UseTime sample and both main-thread drains).
    private static readonly WorldTransitionState _state = new();
    private static long _settleUntil;            // published copy for other threads
    private static volatile bool _portaling, _dead;
    private static volatile bool _gateOpen;      // the valve opened, or engine.json TransitionGate is off
    private static int _gateOverrides;
    private static long _portalEnteredAt, _diedAt;

    /// <summary>
    /// Dead, in portal space, or within 5 s after either or after a landblock jump: AC is
    /// tearing down and loading the world, so nothing should poke at its state
    /// (BusyCountHooks' force-clear). As of the last game tick; any thread.
    /// </summary>
    public static bool InWorldTransition =>
        !_gateOpen && (_dead || _portaling || Environment.TickCount64 < Interlocked.Read(ref _settleUntil));

    /// <summary>
    /// Times the transition gate's safety valve opened (a transition held more than
    /// WorldTransitionState.MaxHoldMs without a break), for the heartbeat (gateov=N).
    /// </summary>
    public static int GateOverrides => Volatile.Read(ref _gateOverrides);

    /// <summary>
    /// When the last settle window ends (TickCount64; 0 = no transition yet), as of the last
    /// game tick; any thread. Moves only on a death, a portal, a teleport or a landblock jump,
    /// never on a walk into the next landblock (AutoIdPolicy's post-transition defer).
    /// </summary>
    public static long SettleUntilMs => Interlocked.Read(ref _settleUntil);

    /// <summary>
    /// AC main thread only (the queue drains): true = hold everything queued now (see
    /// WorldTransitionState for the rule).
    /// </summary>
    public static bool HoldQueuedWork(long nowMs)
    {
        bool hold = _state.CheckHolding(nowMs);
        if (hold) Interlocked.Exchange(ref _lastBlockedPublished, nowMs);
        return hold;
    }

    /// <summary>AC main thread only: what a drain does with one queued item (WorldTransitionState.Decide).</summary>
    public static WorldTransitionState.Verdict DecideQueued(long nowMs, long queuedAtMs, bool teardown)
    {
        WorldTransitionState.Verdict v = _state.Decide(nowMs, queuedAtMs, teardown);
        if (v == WorldTransitionState.Verdict.Hold) Interlocked.Exchange(ref _lastBlockedPublished, nowMs);
        return v;
    }

    private static long _lastBlockedPublished;

    /// <summary>
    /// The last moment a drain was held by a world transition (0 = never), for other threads:
    /// queued work from at or before it is dropped when the drains reach it.
    /// </summary>
    public static long LastBlockedMs => Interlocked.Read(ref _lastBlockedPublished);

    /// <summary>AC main thread, once per game tick.</summary>
    public static void SampleOnGameThread(bool portaling)
    {
        bool dead = false;
        uint health = 0, maxHealth = 0;
        if (PlayerVitalsHooks.TryGetSnapshot(out PlayerVitalsSnapshot v))
        {
            health = v.Health;
            maxHealth = v.MaxHealth;
            dead = v.MaxHealth > 0 && v.Health == 0;
        }
        uint cell = 0;
        if (PlayerPhysicsHooks.TryGetPlayerPose(out uint c, out _, out _, out _, out _, out _, out _, out _))
            cell = c;
        uint landblock = cell >> 16;

        long now = Environment.TickCount64;
        bool gateSetting = true;
        try { gateSetting = Plugins.EngineSettings.TransitionGate; } catch { }
        _state.Enabled = gateSetting;
        WorldTransitionState.Edge edges = _state.Sample(now, dead, portaling, landblock);
        _dead = _state.Dead;
        _portaling = _state.Portaling;
        PublishGate();
        if (edges == WorldTransitionState.Edge.None)
            return;

        if ((edges & WorldTransitionState.Edge.GateOverride) != 0)
        {
            Interlocked.Exchange(ref _gateOverrides, _state.Overrides);
            RynthLog.Warn($"[Lifecycle] world-transition gate OPENED by its safety valve after {_state.HeldForMs(now) / 1000} s held without a break " +
                $"(dead={_state.Dead}, portaling={_state.Portaling}, health {health}/{maxHealth}, landblock 0x{landblock:X4}); queued game actions run again. " +
                "A character that reads dead after a revive, or a stuck portal flag, would otherwise freeze every bot. " +
                $"Open #{_state.Overrides} this session; engine.json TransitionGate=false turns the gate off.");
        }

        if ((edges & WorldTransitionState.Edge.Died) != 0)
        {
            _diedAt = now;
            RynthLog.Info($"[Lifecycle] player DIED (health 0/{maxHealth}) in landblock 0x{landblock:X4} (cell 0x{cell:X8}).");
        }
        if ((edges & WorldTransitionState.Edge.Revived) != 0)
            RynthLog.Info($"[Lifecycle] player alive again (health {health}/{maxHealth}) {Ago(_diedAt, now)} after death, landblock 0x{landblock:X4}.");
        if ((edges & WorldTransitionState.Edge.PortalEntered) != 0)
        {
            _portalEnteredAt = now;
            string why = _diedAt != 0 && now - _diedAt < 30_000 ? " (death portal)" : "";
            RynthLog.Info($"[Lifecycle] portal space ENTERED{why} from landblock 0x{landblock:X4} (cell 0x{cell:X8}).");
        }
        if ((edges & WorldTransitionState.Edge.PortalExited) != 0)
            RynthLog.Info($"[Lifecycle] portal space EXITED into landblock 0x{landblock:X4} (cell 0x{cell:X8}) after {Ago(_portalEnteredAt, now)}.");
        if ((edges & WorldTransitionState.Edge.LandblockJump) != 0)
            RynthLog.Info($"[Lifecycle] landblock jump 0x{_state.PreviousLandblock:X4} -> 0x{landblock:X4}{(portaling ? " (in portal space)" : "")}; queued game actions held off for {WorldTransitionState.SettleMs / 1000} s.");
    }

    /// <summary>
    /// AC main thread (SmartBoxHooks, after AC handled 0xF751 PlayerTeleport): a teleport starts
    /// now. The settle window runs from here, published at once for the enqueue side.
    /// </summary>
    public static void OnTeleportMessage()
    {
        long now = Environment.TickCount64;
        bool started = _state.OnTeleportMessage(now);
        PublishGate();
        if (started)
            RynthLog.Info($"[Lifecycle] PlayerTeleport (0xF751) received in landblock 0x{_state.Landblock:X4}; queued game actions held off for {WorldTransitionState.SettleMs / 1000} s.");
    }

    // Published for other threads: with the gate open (valve or setting off) the settle window reads as
    // none, so InWorldTransition and AutoId's post-transition defer let go too.
    private static void PublishGate()
    {
        _gateOpen = !_state.Enabled || _state.Overridden;
        Interlocked.Exchange(ref _settleUntil, _gateOpen ? 0 : _state.SettleUntilMs);
    }

    private static string Ago(long since, long now) =>
        since == 0 ? "?" : $"{(now - since) / 1000.0:0.0}s";
}
