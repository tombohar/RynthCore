using RynthCore.Engine.Compatibility;

namespace PreCastStopTests;

/// <summary>
/// The pre-cast stop (PreCastStopRules.cs). Flight 63260: runs of projectile casts that never
/// landed each began with a stop burst in a cast's frame; FreeHandsAndCastSpell's own
/// MaybeStopCompletely is one of those stops whenever the player has control.
/// </summary>
internal static class Program
{
    private static int _checks, _failed;

    private static int Main()
    {
        // Server in control (after a cast's server turn): no stop would ride, cast at once.
        Check(PreCastStopRules.Decide(1, false, 0, 0) == PreCastStep.CastNow, "server in control: cast now");
        // Unknown state: as before.
        Check(PreCastStopRules.Decide(-1, false, 0, 0) == PreCastStep.CastNow, "unreadable: cast now (old behavior)");
        // Player in control: send the stop first, never the cast in the same tick.
        Check(PreCastStopRules.Decide(0, false, 0, 0) == PreCastStep.SendStop, "player in control: the stop goes first");
        // Stop sent: wait for the answer.
        Check(PreCastStopRules.Decide(0, true, 0, 0) == PreCastStep.Wait, "stop sent, no answer: wait");
        Check(PreCastStopRules.Decide(0, true, 0, PreCastStopRules.EchoMaxMs - 1) == PreCastStep.Wait, "still waiting 1 ms before the limit");
        // Answered: cast without the cast's own stop.
        Check(PreCastStopRules.Decide(0, true, 1, 120) == PreCastStep.CastWithoutStop, "answered: cast, no stop in its frame");
        // Control state is not re-read once the stop is out (the answer may be a server motion).
        Check(PreCastStopRules.Decide(1, true, 1, 120) == PreCastStep.CastWithoutStop, "answered, server took control meanwhile: still no stop");
        // No answer in time: cast anyway (never park a cast), still without a second stop.
        Check(PreCastStopRules.Decide(0, true, 0, PreCastStopRules.EchoMaxMs) == PreCastStep.CastWithoutStop, "no answer in 750 ms: cast anyway");

        Console.WriteLine($"{_checks} check(s), {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (ok) return;
        _failed++;
        Console.WriteLine($"[FAIL] {what}");
    }
}
