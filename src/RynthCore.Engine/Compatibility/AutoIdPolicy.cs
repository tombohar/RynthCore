// ============================================================================
//  RynthCore.Engine - Compatibility/AutoIdPolicy.cs
//
//  Which background appraisals AutoIdService sends (the decision only; AutoIdService
//  does the queueing, the lookups and the sends).
//
//  Why: in one 26-minute session (1585 s) the client sent 3211 IdentifyObject (0xC8),
//  about three quarters of everything it sent, for 2479 objects against 28
//  kills. AutoId appraised every object the client created. The bot (RynthAi) already
//  appraises what it needs at the point of use (corpse items its loot rules need, the
//  selected creature, equipment, meta conditions), so most of that was traffic for
//  nothing; it is now limited (engine.json "AutoIdMode", /rc autoid).
//
//  Modes:
//    All      the old behaviour: every created object except the player, the selected
//             target with its fresh-cache skip.
//    Limited  (default) only
//               Target     the selected target (fresh-cache skip, no age, no defer);
//               Container  items in the container the player has open now (a corpse,
//                          a chest, a pack opened through ViewContents);
//               Inventory  items in the player's own inventory (worn, in the main pack
//                          or in a side pack) that have no cached appraisal.
//             Container and Inventory wait until the object is MinAgeMs old, and until
//             DeferMs after a world transition's settle window ends (death, portal,
//             teleport, landblock jump; walking into the next landblock is not one),
//             and until DeferMs after login completes (login is a transition too:
//             a client crashed in AC's CreateObject handling during the
//             login create burst). A container the player opened after the last
//             transition ended is not deferred. Inventory sends are capped at 5 a
//             second and wait while a cast or attack is in flight (a selected
//             target alone does not hold them). Creatures, ground items, other players: never.
//    Off      nothing, the selected target included.
//  Plugins calling the host RequestId are not limited by any of this.
//
//  Pure: no engine state, no locking. Compiled into tools\EngineRobustnessTests and
//  tools\EngineSettingsTests.
// ============================================================================

using System;

namespace RynthCore.Engine.Compatibility;

/// <summary>engine.json "AutoIdMode" (see <see cref="AutoIdPolicy"/>).</summary>
internal enum AutoIdMode { All = 0, Limited = 1, Off = 2 }

internal static class AutoIdPolicy
{
    public const AutoIdMode DefaultMode = AutoIdMode.Limited;

    /// <summary>Container and Inventory appraisals wait until the object is this old.</summary>
    public const long MinAgeMs = 2_000;

    /// <summary>Container and Inventory appraisals wait this long after a transition's settle window ends,
    /// and after login completes.</summary>
    public const long DeferMs = 30_000;

    /// <summary>
    /// Inventory appraisals go out at most one per this many ms (5 a second). In one case
    /// a client crashed in AC's CreateObject handling during the login create burst, 47 s after
    /// start, and the pack's appraisals at 30 a second are what that burst must not carry.
    /// </summary>
    public const long InventoryMinIntervalMs = 200;

    /// <summary>
    /// An object whose place is unknown because the object snapshot has not been filled yet
    /// (at login the pack is created before ClientObjectHooks.PrefetchPositions starts) waits
    /// for it this long at most before it is dropped.
    /// </summary>
    public const long UnknownMaxWaitMs = 120_000;

    public enum Class
    {
        /// <summary>The selected target (the priority path).</summary>
        Target,
        /// <summary>In the container the player has open now.</summary>
        Container,
        /// <summary>In the player's own inventory (worn, main pack, side pack).</summary>
        Inventory,
        /// <summary>Anything else the client knows where it is: a creature, a ground item, another player.</summary>
        World,
        /// <summary>Not in the engine's object snapshot yet (created moments ago), so its place is unknown.</summary>
        Unknown,
    }

    public enum Verdict
    {
        /// <summary>Appraise it now.</summary>
        Send,
        /// <summary>The mode does not appraise this: drop it.</summary>
        SkipMode,
        /// <summary>Too young: keep it and ask again later.</summary>
        WaitAge,
        /// <summary>Inside the post-transition defer: keep it and ask again later.</summary>
        WaitDefer,
        /// <summary>Its appraisal is cached (fresh): drop it.</summary>
        SkipFresh,
        /// <summary>Inventory over its send rate (InventoryMinIntervalMs): keep it and ask again later.</summary>
        WaitRate,
        /// <summary>Place unknown and the object snapshot still empty: keep it (up to UnknownMaxWaitMs).</summary>
        WaitSnapshot,
        /// <summary>Inventory while in combat (a cast or an attack in flight, InCombat): keep it.</summary>
        WaitCombat,
    }

    /// <summary>
    /// The decision for one candidate.
    /// <paramref name="ageMs"/>: how long the object has existed (or since it moved, for a move).
    /// <paramref name="inDefer"/>: inside the post-transition defer window (<see cref="InDefer"/>).
    /// <paramref name="openedAfterSettle"/>: Container only, the player opened it after the last
    /// settle window ended (<see cref="OpenedAfterSettle"/>), which exempts it from the defer.
    /// <paramref name="fresh"/>: its appraisal is cached and recent (Target, Container).
    /// <paramref name="cached"/>: its appraisal is cached at all (Inventory).
    /// <paramref name="inventoryRateOk"/>: Inventory only, the send rate allows one now (<see cref="InventoryRateAllows"/>).
    /// <paramref name="snapshotWarm"/>: Unknown only, the object snapshot has been filled (an
    /// object missing from a warm snapshot is not coming; one missing from a cold one may be).
    /// <paramref name="combatBusy"/>: Inventory only, a cast or attack is in flight (<see cref="InCombat"/>). Each 0xC8 raises AC's busy count, which plugins gate casts on, so the pack waits.
    /// </summary>
    public static Verdict Decide(AutoIdMode mode, Class cls, long ageMs, bool inDefer, bool openedAfterSettle,
                                 bool fresh, bool cached, bool inventoryRateOk = true, bool snapshotWarm = true,
                                 bool combatBusy = false)
    {
        switch (mode)
        {
            case AutoIdMode.Off:
                return Verdict.SkipMode;

            case AutoIdMode.All:
                // The old behaviour: the create-time eligibility already decided; only the
                // selected target has the fresh-cache skip.
                if (cls == Class.Target && fresh) return Verdict.SkipFresh;
                return Verdict.Send;

            default: // Limited
                switch (cls)
                {
                    case Class.Target:
                        return fresh ? Verdict.SkipFresh : Verdict.Send;
                    case Class.Container:
                        if (fresh) return Verdict.SkipFresh;
                        if (ageMs < MinAgeMs) return Verdict.WaitAge;
                        if (inDefer && !openedAfterSettle) return Verdict.WaitDefer;
                        return Verdict.Send;
                    case Class.Inventory:
                        if (cached) return Verdict.SkipFresh;
                        if (ageMs < MinAgeMs) return Verdict.WaitAge;
                        if (inDefer) return Verdict.WaitDefer;
                        if (combatBusy) return Verdict.WaitCombat;
                        if (!inventoryRateOk) return Verdict.WaitRate;
                        return Verdict.Send;
                    case Class.Unknown:
                        // Its place shows up in the snapshot within a tick or two; until it is
                        // MinAgeMs old it may still turn out to be in the open container or the pack.
                        // While the snapshot has never been filled (login), it waits for it.
                        if (ageMs < MinAgeMs) return Verdict.WaitAge;
                        if (!snapshotWarm && ageMs < UnknownMaxWaitMs) return Verdict.WaitSnapshot;
                        return Verdict.SkipMode;
                    default: // World
                        return Verdict.SkipMode;
                }
        }
    }

    /// <summary>
    /// In combat for the Inventory hold: a cast in flight (<paramref name="castBusy"/>, CastGate) or an
    /// attack running. A selected target alone is NOT combat (<paramref name="selectedTarget"/> is taken
    /// only so the tests can show that): a hunting bot nearly always has something selected, and the
    /// pack (mana stones included) would never be appraised.
    /// </summary>
    public static bool InCombat(uint selectedTarget, int castBusy, bool attacking) =>
        castBusy != 0 || attacking;

    /// <summary>
    /// When the last transition ended: the later of the settle window's end
    /// (WorldTransitionState.SettleUntilMs, 0 = none yet; it only moves on a death, a portal,
    /// a teleport or a landblock jump, never on a walk into the next landblock) and login
    /// completing (<paramref name="loginCompleteAtMs"/>; login counts as a transition).
    /// </summary>
    public static long TransitionEndMs(long settleUntilMs, long loginCompleteAtMs) =>
        Math.Max(settleUntilMs, loginCompleteAtMs);

    /// <summary>
    /// Inside the defer: in a transition now, login not complete yet
    /// (<paramref name="loginCompleteAtMs"/> 0: the login create burst), or less than
    /// <see cref="DeferMs"/> after <see cref="TransitionEndMs"/>.
    /// </summary>
    public static bool InDefer(long nowMs, long settleUntilMs, long loginCompleteAtMs, bool inTransition) =>
        inTransition || loginCompleteAtMs <= 0 || nowMs < TransitionEndMs(settleUntilMs, loginCompleteAtMs) + DeferMs;

    /// <summary>The player opened the container (at <paramref name="openedAtMs"/>) after the last transition ended
    /// (<paramref name="transitionEndMs"/>, <see cref="TransitionEndMs"/>).</summary>
    public static bool OpenedAfterSettle(long openedAtMs, long transitionEndMs) =>
        openedAtMs > 0 && openedAtMs >= transitionEndMs;

    /// <summary>An Inventory appraisal may go out now: none yet, or the last was at least InventoryMinIntervalMs ago.</summary>
    public static bool InventoryRateAllows(long nowMs, long lastInventorySendMs) =>
        lastInventorySendMs <= 0 || nowMs - lastInventorySendMs >= InventoryMinIntervalMs;

    /// <summary>
    /// Where an object is, from its PublicWeenieDesc ownership fields (<paramref name="known"/> =
    /// the snapshot had it). <paramref name="containerParent"/> is the container's own container
    /// (a side pack's owner), 0 when unknown or none. <paramref name="openContainer"/> is the
    /// container the player has open now (0 = none).
    /// </summary>
    public static Class Classify(bool known, uint playerId, uint openContainer,
                                 uint container, uint wielder, uint containerParent)
    {
        if (!known) return Class.Unknown;
        if (playerId != 0 && (container == playerId || wielder == playerId || (container != 0 && containerParent == playerId)))
            return Class.Inventory;
        // openContainer != 0 also keeps a ground item (container 0) out of here.
        if (openContainer != 0 && (container == openContainer || containerParent == openContainer))
            return Class.Container;
        return Class.World;
    }

    public static bool TryParseMode(string? text, out AutoIdMode mode)
    {
        switch ((text ?? "").Trim().ToLowerInvariant())
        {
            case "all": mode = AutoIdMode.All; return true;
            case "limited": case "limit": mode = AutoIdMode.Limited; return true;
            case "off": case "none": mode = AutoIdMode.Off; return true;
            default: mode = DefaultMode; return false;
        }
    }

    public static string Name(AutoIdMode mode) => mode switch
    {
        AutoIdMode.All => "all",
        AutoIdMode.Off => "off",
        _ => "limited",
    };
}
