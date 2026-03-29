namespace NexCore.Engine.Compatibility;

internal readonly record struct ClientActionHookStatus(
    bool CombatInitialized,
    bool MovementInitialized,
    bool CommandInterpreterInitialized,
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
        EntryPoint.Log("Compat: probing NexAi action hooks...");

        bool combatReady = CombatActionHooks.Probe(EntryPoint.Log);
        bool movementReady = MovementActionHooks.Probe(EntryPoint.Log);
        bool cmdInterpReady = CommandInterpreterHooks.Probe(EntryPoint.Log);

        EntryPoint.Log($"Compat: probe complete. combat={(combatReady ? "ready" : "off")}, movement={(movementReady ? "ready" : "off")}, local={(cmdInterpReady ? "ready" : "off")}");
    }

    public static ClientActionHookStatus GetStatus()
    {
        return new ClientActionHookStatus(
            CombatActionHooks.IsInitialized,
            MovementActionHooks.IsInitialized,
            CommandInterpreterHooks.IsInitialized,
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
}
