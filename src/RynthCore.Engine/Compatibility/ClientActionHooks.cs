namespace RynthCore.Engine.Compatibility;

internal readonly record struct ClientActionHookStatus(
    bool CombatInitialized,
    bool MovementInitialized,
    bool CommandInterpreterInitialized,
    bool PlayerPhysicsInitialized,
    bool MeleeAvailable,
    bool MissileAvailable,
    bool ChangeCombatModeAvailable,
    bool CancelAttackAvailable,
    bool QueryHealthAvailable,
    bool DoMovementAvailable,
    bool StopMovementAvailable,
    bool JumpNonAutonomousAvailable,
    bool AutonomyLevelAvailable,
    bool SetAutoRunAvailable,
    bool TapJumpAvailable,
    bool SetMotionAvailable,
    bool GetPlayerHeadingAvailable,
    bool StopCompletelyAvailable,
    bool TurnToHeadingAvailable,
    string CombatStatus,
    string MovementStatus,
    string CommandInterpreterStatus);

internal static class ClientActionHooks
{
    public static void Initialize()
    {
        Probe();
    }

    public static void Probe()
    {
        RynthLog.Verbose("Compat: probing RynthAi action hooks...");

        bool combatReady = CombatActionHooks.Probe();
        bool movementReady = MovementActionHooks.Probe();
        bool cmdInterpReady = CommandInterpreterHooks.Probe();
        bool playerPhysicsReady = PlayerPhysicsHooks.Probe();
        bool objectReady = ClientObjectHooks.Probe();

        RynthLog.Verbose($"Compat: probe complete. combat={(combatReady ? "ready" : "off")}, movement={(movementReady ? "ready" : "off")}, local={(cmdInterpReady ? "ready" : "off")}, player={(playerPhysicsReady ? "ready" : "off")}, objects={(objectReady ? "ready" : "off")}");
    }

    public static ClientActionHookStatus GetStatus()
    {
        return new ClientActionHookStatus(
            CombatActionHooks.IsInitialized,
            MovementActionHooks.IsInitialized,
            CommandInterpreterHooks.IsInitialized,
            PlayerPhysicsHooks.IsInitialized,
            CombatActionHooks.HasMeleeAttack,
            CombatActionHooks.HasMissileAttack,
            CombatActionHooks.HasChangeCombatMode,
            CombatActionHooks.HasCancelAttack,
            CombatActionHooks.HasQueryHealth,
            MovementActionHooks.HasDoMovement,
            MovementActionHooks.HasStopMovement,
            MovementActionHooks.HasJumpNonAutonomous,
            MovementActionHooks.HasAutonomyLevel,
            CommandInterpreterHooks.HasSetAutoRun,
            CommandInterpreterHooks.HasTapJump,
            CommandInterpreterHooks.HasSetMotion,
            PlayerPhysicsHooks.HasGetHeading,
            CommandInterpreterHooks.HasStopCompletely,
            PlayerPhysicsHooks.HasSetHeading || CommandInterpreterHooks.HasTurnToHeading,
            CombatActionHooks.StatusMessage,
            MovementActionHooks.StatusMessage,
            CommandInterpreterHooks.StatusMessage);
    }

    public static bool MeleeAttack(uint targetId, int attackHeight, float powerLevel)
    {
        return CombatActionHooks.MeleeAttack(targetId, attackHeight, powerLevel);
    }

    public static bool MissileAttack(uint targetId, int attackHeight, float accuracyLevel)
    {
        return CombatActionHooks.MissileAttack(targetId, attackHeight, accuracyLevel);
    }

    public static bool ChangeCombatMode(int combatMode)
    {
        return CombatActionHooks.ChangeCombatMode(combatMode);
    }

    public static bool CancelAttack()
    {
        return CombatActionHooks.CancelAttack();
    }

    public static bool QueryHealth(uint targetId)
    {
        return CombatActionHooks.QueryHealth(targetId);
    }

    public static bool RequestId(uint objectId)
    {
        return CombatActionHooks.RequestId(objectId);
    }

    public static bool CastSpell(uint targetId, int spellId)
    {
        return CombatActionHooks.CastSpell(targetId, spellId);
    }

    // Movement calls below are the plugin API's only way in (PluginManager's callbacks), so each
    // one is traced here with the calling plugin: RynthLog.Move, behind RynthLog.MoveTraceEnabled.
    private static string MoveArg(System.FormattableString s) =>
        RynthLog.MoveTraceEnabled ? System.FormattableString.Invariant(s) : "";

    public static bool DoMovement(uint motion, float speed = 1.0f, int holdKey = MovementActionHooks.HoldKeyRun)
    {
        bool ok = MovementActionHooks.DoMovement(motion, speed, holdKey);
        RynthLog.Move("DoMovement", MoveArg($"0x{motion:X8}, {speed:0.##}, {holdKey}"), ok, a: motion, b: (uint)holdKey);
        return ok;
    }

    public static bool StopMovement(uint motion, int holdKey = MovementActionHooks.HoldKeyRun)
    {
        bool ok = MovementActionHooks.StopMovement(motion, holdKey);
        RynthLog.Move("StopMovement", MoveArg($"0x{motion:X8}, {holdKey}"), ok, a: motion, b: (uint)holdKey);
        return ok;
    }

    public static bool JumpNonAutonomous(float extent)
    {
        bool ok = MovementActionHooks.JumpNonAutonomous(extent);
        RynthLog.Move("JumpNonAutonomous", MoveArg($"{extent:0.##}"), ok);
        return ok;
    }

    public static bool SetAutonomyLevel(uint level)
    {
        return MovementActionHooks.SetAutonomyLevel(level);
    }

    public static bool SetAutoRun(bool enabled)
    {
        bool ok = CommandInterpreterHooks.SetAutoRun(enabled);
        RynthLog.Move("SetAutoRun", enabled ? "True" : "False", ok, a: enabled ? 1u : 0u);
        return ok;
    }

    public static bool TapJump()
    {
        bool ok = CommandInterpreterHooks.TapJump();
        RynthLog.Move("TapJump", "", ok);
        return ok;
    }

    public static bool CommenceJump()
    {
        bool ok = CommandInterpreterHooks.CommenceJump();
        RynthLog.Move("CommenceJump", "", ok);
        return ok;
    }

    public static bool DoJump(bool autonomous)
    {
        bool ok = CommandInterpreterHooks.DoJump(autonomous);
        RynthLog.Move("DoJump", autonomous ? "True" : "False", ok);
        return ok;
    }

    public static bool LaunchJumpWithMotion(bool shift, bool holdW, bool holdX, bool holdZ, bool holdC)
    {
        bool ok = PlayerPhysicsHooks.LaunchJumpWithMotion(shift, holdW, holdX, holdZ, holdC);
        RynthLog.Move("LaunchJumpWithMotion", MoveArg($"shift={shift}, w={holdW}, x={holdX}, z={holdZ}, c={holdC}"), ok);
        return ok;
    }

    public static bool SetMotion(uint motion, bool enabled)
    {
        bool ok = CommandInterpreterHooks.SetMotion(motion, enabled);
        RynthLog.Move("SetMotion", MoveArg($"0x{motion:X8}, {enabled}"), ok, a: motion, b: enabled ? 1u : 0u);
        return ok;
    }

    public static bool StopCompletely()
    {
        bool ok = CommandInterpreterHooks.StopCompletely();
        RynthLog.Move("StopCompletely", "", ok);
        return ok;
    }

    public static bool TurnToHeading(float headingDegrees)
    {
        // Direct quaternion write — instant snap, most reliable (uses proven SmartBox offsets).
        // Equivalent to old Decal Actions.Heading = value.
        // Off AC's main thread (plugin pump) SetPlayerHeadingDirect only parks the
        // heading in AcMainThreadQueue's coalesced slot and returns true; the snap
        // (and the no-player CommandInterpreter fallback below) run in Drain.
        bool ok = PlayerPhysicsHooks.SetPlayerHeadingDirect(headingDegrees)
                  // Fallback: command interpreter gradual turn
                  || CommandInterpreterHooks.TurnToHeading(headingDegrees);
        // Successive turns from one caller collapse into one line whatever the angle (nav steers every tick).
        RynthLog.Move("TurnToHeading", MoveArg($"{headingDegrees:0.0}"), ok, collapseByKind: true);
        return ok;
    }

    public static bool TryGetPlayerHeading(out float headingDegrees)
    {
        return PlayerPhysicsHooks.TryGetPlayerHeading(out headingDegrees);
    }

    public static bool TryGetObjectName(uint objectId, out string name)
    {
        return ClientObjectHooks.TryGetObjectName(objectId, out name);
    }
}
