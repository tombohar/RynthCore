namespace RynthCore.Engine.Compatibility;

// ============================================================================
//  The stop before a cast goes a server answer ahead of the cast (2026-10-12).
//
//  ClientMagicSystem::FreeHandsAndCastSpell (every cast path: ours, the UI's, Decal's
//  ClientMagicSystem::CastSpell) starts with cmdinterp->MaybeStopCompletely(): while the player
//  has control of the character (controlled_by_server == 0) that is StopCompletely, and so a
//  MoveToState stop in the SAME frame as the CastTargetedSpell. Flight 63260: every run of
//  projectile casts that never landed began with a stop burst in a cast's frame; ACE launches
//  projectiles from its own physics copy of the caster, which such a frame can leave wrong.
//
//  So, for a queued cast while the player has control, AcMainThreadQueue.DrainCasts:
//    1. sends the stop itself (CommandInterpreterHooks.StopCompletely) and does not cast;
//    2. waits for the server's answer to it - the next UpdateMotion for the player
//       (SmartBoxHooks counts them) - or EchoMaxMs;
//    3. then casts with MaybeStopCompletely's stop suppressed (the state it would send is the
//       one just sent and answered).
//  With the server in control (after a cast's server turn: the usual case between casts) the
//  cast sends no stop anyway and goes at once. Pure decision code (no AC access), so the tests
//  in tools/PreCastStopTests compile it on its own.
// ============================================================================

internal enum PreCastStep
{
    /// <summary>Cast now, as before (no stop would ride with it, or the state can't be read).</summary>
    CastNow,
    /// <summary>Send the pre-cast stop now; don't cast this tick.</summary>
    SendStop,
    /// <summary>The stop went out; wait for the server's answer.</summary>
    Wait,
    /// <summary>The stop was answered (or the wait ran out): cast, without the cast's own stop.</summary>
    CastWithoutStop,
}

internal static class PreCastStopRules
{
    /// <summary>The longest a cast waits for the server's answer to its stop.</summary>
    public const long EchoMaxMs = 750;

    /// <param name="controlledByServer">1 server has control, 0 the player has, -1 unknown.</param>
    /// <param name="stopSent">The pre-cast stop for this cast already went out.</param>
    /// <param name="echoesSinceStop">Player UpdateMotions counted since the stop went out.</param>
    /// <param name="msSinceStop">Milliseconds since the stop went out.</param>
    public static PreCastStep Decide(int controlledByServer, bool stopSent, long echoesSinceStop, long msSinceStop)
    {
        if (stopSent)
            return echoesSinceStop > 0 || msSinceStop >= EchoMaxMs ? PreCastStep.CastWithoutStop : PreCastStep.Wait;
        return controlledByServer == 0 ? PreCastStep.SendStop : PreCastStep.CastNow;
    }
}
