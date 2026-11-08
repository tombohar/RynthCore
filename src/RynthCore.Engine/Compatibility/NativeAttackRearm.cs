// ============================================================================
//  RynthCore.Engine - Compatibility/NativeAttackRearm.cs
//
//  When the native attack (ClientCombatHooks.NativeAttack) clears the client's
//  stuck attack-in-progress flag first. Pure: no AC memory, so the host test
//  (tools/EngineRobustnessTests) compiles it in.
//
//  ClientCombatSystem keeps one byte, +0x3D, for "an attack is in progress".
//  ExecuteAttack sets it when it sends the attack (and HandleCommenceAttackEvent
//  when the server starts the swing); only HandleAttackDoneEvent (0x01A7) clears
//  it. While it is set EndAttackRequest sends nothing: it queues the request
//  (+0x47) for that AttackDone to replay. ExecuteAttack also sets it when the
//  client's mode is neither Melee nor Missile, where it sends no attack at all
//  (decompile 0x0056C8C0): a power bar that finished filling, or a queued request
//  replayed by a late AttackDone, after a stance change to Peace or Magic. No
//  attack went out, so no AttackDone ever comes, and every native attack after
//  that is queued forever: the server never swings and never turns the character
//  to the target. A regular attack (the raw 0x0008 game action) gets a swing and
//  its AttackDone, which is why turning Use Native Attack off made the character
//  fight again at once (2026-11-06, a melee character back from a buff-wand swap
//  mid-fight: 37 minutes of no swings, 01:29-02:06).
//
//  The flag is stuck when it is set while the server is not swinging (AC's
//  attacking flag 0x00871EE0, set by CommenceAttack, cleared by AttackDone, is
//  clear) and either
//    * the combat mode changed since the last native attack: nothing armed in
//      the old stance can still be answered in this one; or
//    * it has stayed that way for StuckMs: a sent attack is answered (a swing, or
//      AttackDone) within a round trip; a move-to the target starts no swing for
//      longer, and clearing the flag then only sends a second attack request the
//      server folds into the one it is already running.
//  With the attacking flag unknown (its patterns didn't resolve) only the stance
//  rule applies: a swing in progress can't be told from a stuck flag.
// ============================================================================
namespace RynthCore.Engine.Compatibility;

internal static class NativeAttackRearm
{
    /// <summary>How long the flag may stay set with no swing before it counts as stuck.</summary>
    public const long StuckMs = 2000;

    // CombatActionHooks.CombatModeMelee / CombatModeMissile (kept here so the rule compiles alone).
    private const int Melee = 2, Missile = 4;

    public enum Verdict
    {
        /// <summary>Nothing stuck (or not yet long enough): attack as usual.</summary>
        Leave,
        /// <summary>Stuck from before a stance change: clear it now.</summary>
        AfterStanceChange,
        /// <summary>Stuck for <see cref="StuckMs"/> with no swing: clear it now.</summary>
        Stale,
    }

    /// <summary>
    /// One look before a native attack. <paramref name="stuckSinceMs"/> is the caller's state:
    /// when the flag was first seen set with no swing (0 = not seen); this updates it.
    /// </summary>
    /// <param name="inProgress">ClientCombatSystem +0x3D.</param>
    /// <param name="serverSwinging">AC's attacking flag; null when it can't be read.</param>
    /// <param name="stanceChangedSinceLastSend">The combat mode changed since the last native attack.</param>
    public static Verdict Judge(bool inProgress, bool? serverSwinging, bool stanceChangedSinceLastSend,
                                long nowMs, ref long stuckSinceMs)
    {
        if (!inProgress || serverSwinging == true)
        {
            stuckSinceMs = 0;
            return Verdict.Leave;
        }
        if (stanceChangedSinceLastSend)
        {
            stuckSinceMs = 0;
            return Verdict.AfterStanceChange;
        }
        if (serverSwinging == null)
            return Verdict.Leave;
        if (stuckSinceMs == 0)
        {
            stuckSinceMs = nowMs;
            return Verdict.Leave;
        }
        if (nowMs - stuckSinceMs < StuckMs)
            return Verdict.Leave;
        stuckSinceMs = 0;
        return Verdict.Stale;
    }

    /// <summary>
    /// True when the client's attack state must be disarmed for <paramref name="mode"/>: out of
    /// Melee and Missile no attack can go out, so a queued request (+0x47), a filling power bar
    /// (+0x30) or the auto-repeat (+0x44) left from the last stance would only arm the stuck flag.
    /// </summary>
    public static bool DisarmFor(int mode) => mode != Melee && mode != Missile;

    /// <summary>The native attack goes out only in Melee or Missile (see <see cref="DisarmFor"/>).</summary>
    public static bool AttackMode(int mode) => !DisarmFor(mode);
}
