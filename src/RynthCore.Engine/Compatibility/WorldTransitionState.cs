// ============================================================================
//  RynthCore.Engine - Compatibility/WorldTransitionState.cs
//
//  Is the player in a world transition: dead, in portal space (teleporting), or
//  within SettleMs after either, or after a landblock JUMP (a teleport, not walking
//  into the next landblock)? AC is then tearing objects down and loading new ones,
//  and nothing the engine queued for the main thread may run against it.
//
//  Seen in a real crash: a client died right after a death recall's landblock
//  jump (0x00AE -> 0xA9B4), under GameTickHooks.UseTimeDetour, which runs queued casts
//  before AC's tick; a queued SetAutoRun(off) had run 1047 ms late a moment before.
//  The engine's own queued work runs inside those ticks, so it is the first
//  suspect.
//
//  Gate rule for queued game-mutating work (AcMainThreadQueue, at its drains):
//    - while in transition, everything is HELD: nothing queued runs;
//    - once it ends, work queued at or before the last moment a drain saw the
//      transition is DROPPED (casts, selects, uses, moves, appraisals: the plugins
//      re-send what they still want), EXCEPT teardown work: stops and cancels (autorun
//      off, a motion off, StopMovement, StopCompletely, CancelAttack, a container close),
//      which still run, in queue order, as one group. A dropped stop can leave the
//      character running or turning, and AcMainThreadQueue never drops stops.
//
//  Walking across a landblock boundary is not a transition: it happens constantly in a
//  fight, AC streams the neighbour in without tearing the player's world down, and a 5 s
//  hold there would stall every border fight.
//
//  Safety valve (2026-10-18 review): the gate holds everything, with no way out but the
//  transition ending. Dead is "MaxHealth > 0 && Health == 0" and the portal flag is AC's own;
//  if a revived character kept reading Health 0, or the flag stuck, every bot would freeze
//  silently. So a transition that has held CONTINUOUSLY for MaxHoldMs opens the gate by
//  itself (Overridden: counted in Overrides, announced once through Edge.GateOverride) and
//  stays open until the raw transition ends. Enabled = false (engine.json TransitionGate)
//  turns the gate off altogether.
//
//  Pure: AC main thread only (single writer and reader: the UseTime sample and both
//  main-thread drains). Compiled into tools\EngineRobustnessTests.
// ============================================================================

using System;

namespace RynthCore.Engine.Compatibility;

internal sealed class WorldTransitionState
{
    public const long SettleMs = 5_000;

    /// <summary>
    /// A transition held this long without a break opens the gate. A real death + portal +
    /// settle is well under 30 s (a slow portal load or a very long death screen included).
    /// </summary>
    public const long MaxHoldMs = 120_000;

    [Flags]
    public enum Edge
    {
        None = 0,
        Died = 1,
        Revived = 2,
        PortalEntered = 4,
        PortalExited = 8,
        LandblockJump = 16,
        /// <summary>The valve opened: the transition outlasted <see cref="MaxHoldMs"/>.</summary>
        GateOverride = 32,
    }

    private bool _init;
    public bool Dead { get; private set; }
    public bool Portaling { get; private set; }
    public uint Landblock { get; private set; }
    public uint PreviousLandblock { get; private set; }
    /// <summary>The settle window runs until this TickCount64.</summary>
    public long SettleUntilMs { get; private set; }
    /// <summary>Last time a gate check saw the transition (0 = never).</summary>
    public long LastBlockedMs { get; private set; }

    /// <summary>False = the gate is off (engine.json TransitionGate): nothing is ever held.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The valve is open: the current transition outlasted <see cref="MaxHoldMs"/>.</summary>
    public bool Overridden { get; private set; }

    /// <summary>Times the valve has opened, over the life of this state.</summary>
    public int Overrides { get; private set; }

    /// <summary>How long the current transition has been held, as of the last sample (0 = none).</summary>
    public long HeldForMs(long nowMs) => _heldSinceMs == 0 ? 0 : nowMs - _heldSinceMs;

    private long _heldSinceMs;

    /// <summary>Dead, portaling, or inside the settle window, whatever the valve and the setting say.</summary>
    public bool RawInTransition(long nowMs) => Dead || Portaling || nowMs < SettleUntilMs;

    /// <summary>One game-tick sample. Returns the edges crossed (for the lifecycle log).</summary>
    public Edge Sample(long nowMs, bool dead, bool portaling, uint landblock)
    {
        Edge edges = SampleCore(nowMs, dead, portaling, landblock);
        if (!RawInTransition(nowMs))
        {
            _heldSinceMs = 0;
            Overridden = false;
        }
        else if (_heldSinceMs == 0)
        {
            _heldSinceMs = nowMs;
        }
        else if (!Overridden && nowMs - _heldSinceMs >= MaxHoldMs)
        {
            Overridden = true;
            Overrides++;
            edges |= Edge.GateOverride;
        }
        return edges;
    }

    private Edge SampleCore(long nowMs, bool dead, bool portaling, uint landblock)
    {
        if (!_init)
        {
            _init = true;
            Dead = dead;
            Portaling = portaling;
            Landblock = landblock;
            if (dead || portaling) SettleUntilMs = nowMs + SettleMs;
            return Edge.None;
        }

        Edge edges = Edge.None;
        if (dead || portaling || Dead || Portaling)
            SettleUntilMs = nowMs + SettleMs;
        if (dead != Dead) { edges |= dead ? Edge.Died : Edge.Revived; Dead = dead; }
        if (portaling != Portaling) { edges |= portaling ? Edge.PortalEntered : Edge.PortalExited; Portaling = portaling; }

        if (landblock != 0 && Landblock != 0 && landblock != Landblock)
        {
            if (IsJump(Landblock, landblock))
            {
                edges |= Edge.LandblockJump;
                SettleUntilMs = Math.Max(SettleUntilMs, nowMs + SettleMs);
            }
        }
        if (landblock != 0 && landblock != Landblock)
        {
            PreviousLandblock = Landblock;
            Landblock = landblock;
        }
        return edges;
    }

    /// <summary>
    /// The server's PlayerTeleport message (0xF751) arrived: a teleport begins now, whether or not
    /// a tick sample ever sees teleport_in_progress or a landblock jump (a recall into the next
    /// landblock shows neither). Starts (or extends) the settle window. Returns false when the
    /// window was already running past it.
    /// </summary>
    public bool OnTeleportMessage(long nowMs)
    {
        long until = nowMs + SettleMs;
        if (until <= SettleUntilMs) return false;
        SettleUntilMs = until;
        return true;
    }

    /// <summary>Two landblocks more than one apart on either axis: a teleport, not a walk.</summary>
    public static bool IsJump(uint from, uint to)
    {
        int dx = (int)((to >> 8) & 0xFF) - (int)((from >> 8) & 0xFF);
        int dy = (int)(to & 0xFF) - (int)(from & 0xFF);
        return Math.Abs(dx) > 1 || Math.Abs(dy) > 1;
    }

    /// <summary>
    /// Dead, portaling, or inside the settle window, and the gate is on and its valve has not
    /// opened: the answer every drain and every "is it safe to poke AC" check wants.
    /// </summary>
    public bool InTransition(long nowMs) => Enabled && !Overridden && RawInTransition(nowMs);

    public enum Verdict { Run, Hold, Drop }

    /// <summary>
    /// A drain's check for the head of a queue: true = hold everything queued now. Records the
    /// moment, so work queued up to here is judged by <see cref="Decide"/> once it ends.
    /// </summary>
    public bool CheckHolding(long nowMs)
    {
        if (!InTransition(nowMs)) return false;
        LastBlockedMs = nowMs;
        return true;
    }

    /// <summary>
    /// What a drain does with one queued item: Hold while in transition; afterwards Drop it
    /// if it was queued during the transition (at or before the last held drain) unless it is
    /// <paramref name="teardown"/> work, which Runs; anything queued after the transition Runs.
    /// </summary>
    public Verdict Decide(long nowMs, long queuedAtMs, bool teardown)
    {
        if (CheckHolding(nowMs)) return Verdict.Hold;
        if (LastBlockedMs != 0 && queuedAtMs <= LastBlockedMs)
            return teardown ? Verdict.Run : Verdict.Drop;
        return Verdict.Run;
    }
}