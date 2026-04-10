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
        RynthLog.Compat("Compat: probing RynthAi action hooks...");

        bool combatReady = CombatActionHooks.Probe();
        bool movementReady = MovementActionHooks.Probe();
        bool cmdInterpReady = CommandInterpreterHooks.Probe();
        bool playerPhysicsReady = PlayerPhysicsHooks.Probe();
        bool objectReady = ClientObjectHooks.Probe();

        RynthLog.Compat($"Compat: probe complete. combat={(combatReady ? "ready" : "off")}, movement={(movementReady ? "ready" : "off")}, local={(cmdInterpReady ? "ready" : "off")}, player={(playerPhysicsReady ? "ready" : "off")}, objects={(objectReady ? "ready" : "off")}");
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

    public static bool DoMovement(uint motion, float speed = 1.0f, int holdKey = MovementActionHooks.HoldKeyRun)
    {
        return MovementActionHooks.DoMovement(motion, speed, holdKey);
    }

    public static bool StopMovement(uint motion, int holdKey = MovementActionHooks.HoldKeyRun)
    {
        return MovementActionHooks.StopMovement(motion, holdKey);
    }

    public static bool JumpNonAutonomous(float extent)
    {
        return MovementActionHooks.JumpNonAutonomous(extent);
    }

    public static bool SetAutonomyLevel(uint level)
    {
        return MovementActionHooks.SetAutonomyLevel(level);
    }

    public static bool SetAutoRun(bool enabled)
    {
        return CommandInterpreterHooks.SetAutoRun(enabled);
    }

    public static bool TapJump()
    {
        return CommandInterpreterHooks.TapJump();
    }

    public static bool SetMotion(uint motion, bool enabled)
    {
        return CommandInterpreterHooks.SetMotion(motion, enabled);
    }

    public static bool StopCompletely()
    {
        return CommandInterpreterHooks.StopCompletely();
    }

    public static bool TurnToHeading(float headingDegrees)
    {
        // Direct quaternion write — instant snap, most reliable (uses proven SmartBox offsets).
        // Equivalent to old Decal Actions.Heading = value.
        if (PlayerPhysicsHooks.SetPlayerHeadingDirect(headingDegrees))
            return true;

        // Fallback: command interpreter gradual turn
        return CommandInterpreterHooks.TurnToHeading(headingDegrees);
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
