using RynthCore.Engine.Compatibility;

namespace EngineRobustnessTests;

/// <summary>
/// The native attack's stuck attack-in-progress flag (Compatibility/NativeAttackRearm.cs), from
/// the 2026-11-06 wedge: a buff-wand swap mid-fight, then 37 minutes of native attacks the client
/// queued behind a flag no AttackDone would ever clear (the regular attack fought at once). Plus a
/// source scan that ClientCombatHooks and CombatModeHooks use the rule where it matters.
/// </summary>
internal static partial class Program
{
    private static void RunNativeAttackRearmTests()
    {
        RearmAfterStanceChange();
        RearmWhenStale();
        RearmLeavesASwingAlone();
        RearmWithoutTheAttackingFlag();
        RearmModes();
        RearmSourceScan();
    }

    private static void RearmAfterStanceChange()
    {
        _case = "native re-arm: after a stance change";
        long since = 0;
        // Back in Melee after the Staff: the flag set, no swing, the mode changed since the last native attack.
        var v = NativeAttackRearm.Judge(inProgress: true, serverSwinging: false, stanceChangedSinceLastSend: true, 1000, ref since);
        Check(v == NativeAttackRearm.Verdict.AfterStanceChange, $"cleared at the first native attack, got {v}");
        Check(since == 0, "no stuck clock left running");

        // The same with the flag clear: nothing to do.
        v = NativeAttackRearm.Judge(false, false, true, 1000, ref since);
        Check(v == NativeAttackRearm.Verdict.Leave, "flag clear: left alone");
    }

    private static void RearmWhenStale()
    {
        _case = "native re-arm: stale";
        long since = 0;
        var v = NativeAttackRearm.Judge(true, false, false, 10_000, ref since);
        Check(v == NativeAttackRearm.Verdict.Leave && since == 10_000, "first sight: the clock starts, no clear (a request can be on its way)");
        v = NativeAttackRearm.Judge(true, false, false, 10_000 + NativeAttackRearm.StuckMs - 1, ref since);
        Check(v == NativeAttackRearm.Verdict.Leave, "under 2 s: still left");
        v = NativeAttackRearm.Judge(true, false, false, 10_000 + NativeAttackRearm.StuckMs, ref since);
        Check(v == NativeAttackRearm.Verdict.Stale, $"2 s with no swing: cleared, got {v}");
        Check(since == 0, "the clock starts over");

        // The answer comes in between: the clock resets.
        since = 0;
        NativeAttackRearm.Judge(true, false, false, 20_000, ref since);
        NativeAttackRearm.Judge(false, false, false, 20_500, ref since);   // AttackDone cleared it
        Check(since == 0, "the flag cleared by the server: clock reset");
        v = NativeAttackRearm.Judge(true, false, false, 22_100, ref since);
        Check(v == NativeAttackRearm.Verdict.Leave, "set again later: a fresh 2 s");
    }

    private static void RearmLeavesASwingAlone()
    {
        _case = "native re-arm: a swing in progress";
        long since = 0;
        for (long t = 0; t <= 10_000; t += 500)
        {
            var v = NativeAttackRearm.Judge(true, serverSwinging: true, stanceChangedSinceLastSend: t == 0, t, ref since);
            Check(v == NativeAttackRearm.Verdict.Leave, $"the server swinging (CommenceAttack, no AttackDone yet) at {t} ms: left alone");
        }
        Check(since == 0, "no stuck clock while it swings");
    }

    private static void RearmWithoutTheAttackingFlag()
    {
        _case = "native re-arm: attacking flag unknown";
        long since = 0;
        var v = NativeAttackRearm.Judge(true, serverSwinging: null, stanceChangedSinceLastSend: true, 0, ref since);
        Check(v == NativeAttackRearm.Verdict.AfterStanceChange, "the stance rule still applies");
        for (long t = 0; t <= 20_000; t += 1000)
        {
            v = NativeAttackRearm.Judge(true, null, false, t, ref since);
            Check(v == NativeAttackRearm.Verdict.Leave, $"no stale clear at {t} ms: a swing can't be told from a stuck flag");
        }
    }

    private static void RearmModes()
    {
        _case = "native re-arm: modes";
        Check(NativeAttackRearm.AttackMode(2) && NativeAttackRearm.AttackMode(4), "Melee and Missile attack");
        Check(!NativeAttackRearm.AttackMode(1) && !NativeAttackRearm.AttackMode(8), "Peace and Magic don't (the native attack is refused there)");
        Check(NativeAttackRearm.DisarmFor(1) && NativeAttackRearm.DisarmFor(8), "Peace and Magic disarm a left-over attack");
        Check(!NativeAttackRearm.DisarmFor(2) && !NativeAttackRearm.DisarmFor(4), "Melee and Missile keep it");
    }

    private static void RearmSourceScan()
    {
        _case = "native re-arm: source scan";
        string? compat = FindRepoDir(Path.Combine("src", "RynthCore.Engine", "Compatibility"));
        Check(compat != null, "found src\\RynthCore.Engine\\Compatibility by walking up");
        if (compat == null) return;

        string hooks = File.ReadAllText(Path.Combine(compat, "ClientCombatHooks.cs"));
        int body = hooks.IndexOf("public static bool NativeAttack(", StringComparison.Ordinal);
        int guard = hooks.IndexOf("NativeAttackRearm.AttackMode(mode)", body, StringComparison.Ordinal);
        int rearm = hooks.IndexOf("RearmIfStuck(cs);", body, StringComparison.Ordinal);
        int start = hooks.IndexOf("_startAttackRequest(cs);", body, StringComparison.Ordinal);
        int stamp = hooks.IndexOf("_modeChangesAtLastSend = CombatModeHooks.ModeChanges;", body, StringComparison.Ordinal);
        Check(body >= 0 && guard > body && rearm > guard && start > rearm,
            "NativeAttack: the mode guard, then the re-arm, then StartAttackRequest");
        Check(stamp > start, "the mode-change count is taken after the attack went out");

        string mode = File.ReadAllText(Path.Combine(compat, "CombatModeHooks.cs"));
        int detour = mode.IndexOf("private static void SetCombatModeDetour(", StringComparison.Ordinal);
        int request = mode.IndexOf("public static unsafe bool RequestCombatMode(", StringComparison.Ordinal);
        Check(detour >= 0 && mode.IndexOf("ClientCombatHooks.DisarmOutOfAttackStance(", detour, StringComparison.Ordinal) > detour,
            "the SetCombatMode detour disarms a left-over attack");
        int trampoline = request >= 0 ? mode.IndexOf("_originalSetCombatMode(combatSystem, combatMode, 1);", request, StringComparison.Ordinal) : -1;
        int disarm = trampoline >= 0 ? mode.IndexOf("ClientCombatHooks.DisarmOutOfAttackStance(", trampoline, StringComparison.Ordinal) : -1;
        Check(trampoline > request && disarm > trampoline && disarm < detour,
            "RequestCombatMode (the trampoline skips the detour) disarms too");
    }
}
