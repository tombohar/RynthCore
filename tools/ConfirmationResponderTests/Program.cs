using System.Text;
using RynthCore.Engine.Compatibility;
using P = RynthCore.Engine.Compatibility.ConfirmationArmPolicy;
using V = RynthCore.Engine.Compatibility.ConfirmationArmPolicy.Verdict;

namespace ConfirmationResponderTests;

/// <summary>
/// What the armed answers do with the server's yes/no prompts (ConfirmationArmPolicy.cs, the pure
/// half of ConfirmationResponder): the v85 one-shot arm, unchanged, and the v97 sticky arm
/// (minPercent | 0x100), which answers Yes at its minimum, leaves a prompt below it to the client's
/// own dialog, stays armed, and never fights a one-shot. CraftInteraction is type 5.
/// </summary>
internal static class Program
{
    private const uint Craft = 5, YesNo = 7;
    private const int Flag = P.ArmFlagLeaveBelow;
    private static int _checks, _failed;
    private static string _case = "";

    /// <summary>The responder's two slots, driven the way ConfirmationResponder drives them.</summary>
    private sealed class Slots
    {
        public ConfirmationArm OneShot, Sticky;
        public bool Available = true;
        public int Arm(uint type, int minArg, int timeoutMs, long now)
            => P.Apply(ref OneShot, ref Sticky, type, minArg, timeoutMs, now, Available);
        public V Request(uint type, int percent, long now)
            => P.Decide(ref OneShot, ref Sticky, type, percent, now);
    }

    private static int Main()
    {
        Run(nameof(ArgumentsAreChecked), ArgumentsAreChecked);
        Run(nameof(OneShotUnchanged), OneShotUnchanged);
        Run(nameof(StickyYesAtTheMinimum), StickyYesAtTheMinimum);
        Run(nameof(StickyBelowIsLeftAndStaysArmed), StickyBelowIsLeftAndStaysArmed);
        Run(nameof(StickyExpiresAndReArms), StickyExpiresAndReArms);
        Run(nameof(OtherTypesAreLeft), OtherTypesAreLeft);
        Run(nameof(OneShotGoesFirstAndNeverFights), OneShotGoesFirstAndNeverFights);
        Run(nameof(DisarmPicksTheSlot), DisarmPicksTheSlot);
        Run(nameof(Unavailable), Unavailable);
        Run(nameof(PercentParse), PercentParse);

        Console.WriteLine($"{_checks} check(s), {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action body)
    {
        _case = name;
        try { body(); }
        catch (Exception ex) { _checks++; _failed++; Console.WriteLine($"[FAIL] {name}: threw {ex.GetType().Name}: {ex.Message}"); }
    }

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (ok) return;
        _failed++;
        Console.WriteLine($"[FAIL] {_case}: {what}");
    }

    private static void Eq<T>(T actual, T expected, string what) =>
        Check(EqualityComparer<T>.Default.Equals(actual, expected), $"{what}: expected {expected}, got {actual}");

    private static void ArgumentsAreChecked()
    {
        var s = new Slots();
        // v85's rules, as before.
        Eq(s.Arm(0, 50, 1000, 0), P.ArmBadArgs, "type 0");
        Eq(s.Arm(Craft, -1, 1000, 0), P.ArmBadArgs, "minimum -1");
        Eq(s.Arm(Craft, 101, 1000, 0), P.ArmBadArgs, "minimum 101");
        Eq(s.Arm(Craft, 50, 120_001, 0), P.ArmBadArgs, "timeout over 120 s");
        Check(!s.OneShot.Armed && !s.Sticky.Armed, "bad arguments arm nothing");
        Eq(s.Arm(Craft, 0, 1000, 0), P.ArmOk, "one-shot, minimum 0");
        Eq(s.Arm(Craft, 100, 120_000, 0), P.ArmOk, "one-shot 100 for 120 s");
        Check(s.OneShot.Armed && s.OneShot.MinPercent == 100 && !s.Sticky.Armed, "a one-shot goes in the one-shot slot");

        // v97's flag.
        Eq(Flag, 0x100, "the flag's value (ABI)");
        s = new Slots();
        Eq(s.Arm(Craft, 100 | Flag, 60_000, 0), P.ArmOk, "sticky 100");
        Check(s.Sticky.Armed && s.Sticky.MinPercent == 100 && !s.OneShot.Armed, "a sticky arm goes in the sticky slot, minimum 100");
        Eq(s.Arm(Craft, 1 | Flag, 60_000, 0), P.ArmOk, "sticky 1");
        Eq(s.Arm(Craft, 0 | Flag, 60_000, 0), P.ArmBadArgs, "sticky with no minimum (would say Yes to everything)");
        Eq(s.Arm(Craft, 101 | Flag, 60_000, 0), P.ArmBadArgs, "sticky 101");
        Eq(s.Arm(Craft, 100 | Flag, 120_001, 0), P.ArmBadArgs, "sticky: the same 120 s cap");
        Eq(s.Arm(Craft, 100 | 0x200, 60_000, 0), P.ArmBadArgs, "an unknown flag bit");
        Eq(s.Arm(Craft, 100 | 0x300, 60_000, 0), P.ArmBadArgs, "the flag with an unknown bit");
        Eq(s.Arm(Craft, int.MinValue | 100, 60_000, 0), P.ArmBadArgs, "the sign bit");
        Eq(s.Sticky.MinPercent, 1, "a refused call leaves the arm before it (sticky 1)");
        // What an engine before v97 does with the flag: its check was minPercent > 100 -> -3.
        Check((100 | Flag) > 100 && (1 | Flag) > 100, "the flag makes minPercent > 100, which a v85-v96 engine refuses as bad arguments");
    }

    private static void OneShotUnchanged()
    {
        long t = 1_000;
        var s = new Slots();
        s.Arm(Craft, 80, 15_000, t);
        Eq(s.Request(Craft, 90, t + 10), V.Accept, "90% with a minimum of 80: Yes");
        Check(!s.OneShot.Armed, "spent after one answer");
        Eq(s.Request(Craft, 100, t + 20), V.LeaveToClient, "the next prompt is the client's");

        s.Arm(Craft, 80, 15_000, t);
        Eq(s.Request(Craft, 38, t + 10), V.Decline, "38% with a minimum of 80: No (as v85 always did)");
        Check(!s.OneShot.Armed, "spent after a No too");

        s.Arm(Craft, 80, 15_000, t);
        Eq(s.Request(Craft, -1, t + 10), V.Decline, "no percent with a minimum: No");

        s.Arm(Craft, 0, 15_000, t);
        Eq(s.Request(Craft, -1, t + 10), V.Accept, "minimum 0: Yes even without a percent");

        s.Arm(Craft, 80, 15_000, t);
        Eq(s.Request(Craft, 100, t + 15_000), V.Accept, "at the timeout itself: still armed");
        s.Arm(Craft, 80, 15_000, t);
        Eq(s.Request(Craft, 100, t + 15_001), V.LeaveToClient, "past the timeout: the client's");
        Check(!s.OneShot.Armed, "an expired arm is dropped");

        s.Arm(Craft, 80, 15_000, t);
        s.Arm(Craft, 50, 15_000, t + 1);
        Eq(s.Request(Craft, 60, t + 2), V.Accept, "a new one-shot replaces the old (60% with 50: Yes)");
        Eq(s.Arm(Craft, 0, 0, t + 3), P.ArmOk, "disarm: 1");
    }

    private static void StickyYesAtTheMinimum()
    {
        long t = 5_000;
        var s = new Slots();
        s.Arm(Craft, 100 | Flag, 60_000, t);
        for (int i = 0; i < 5; i++)
        {
            Eq(s.Request(Craft, 100, t + 1_000 * (i + 1)), V.AcceptSticky, $"craft {i + 1} at 100%: Yes (sticky)");
            Check(s.Sticky.Armed, $"still armed after craft {i + 1}");
        }
        s.Arm(Craft, 90 | Flag, 60_000, t);
        Eq(s.Request(Craft, 90, t + 1), V.AcceptSticky, "90% with a minimum of 90: Yes");
        Eq(s.Request(Craft, 95, t + 2), V.AcceptSticky, "95%: Yes");
        Check(s.Sticky.Armed, "and still armed");
    }

    private static void StickyBelowIsLeftAndStaysArmed()
    {
        long t = 10_000;
        var s = new Slots();
        s.Arm(Craft, 100 | Flag, 60_000, t);
        Eq(s.Request(Craft, 99, t + 1), V.LeaveToClient, "99% with a minimum of 100: the client's dialog, never No");
        Check(s.Sticky.Armed, "still armed after a prompt it left");
        Eq(s.Request(Craft, 0, t + 2), V.LeaveToClient, "0%: the client's");
        Eq(s.Request(Craft, -1, t + 3), V.LeaveToClient, "no percent in the text: the client's");
        Check(s.Sticky.Armed, "still armed");
        Eq(s.Request(Craft, 100, t + 4), V.AcceptSticky, "then 100%: Yes");
        Check(s.Sticky.Armed, "still armed after that");
        for (int pct = -1; pct <= 100; pct++)
        {
            V v = s.Request(Craft, pct, t + 5);
            Check(v != V.Decline, $"never No ({pct}%)");
            Eq(v, pct == 100 ? V.AcceptSticky : V.LeaveToClient, $"{pct}% with a minimum of 100");
        }
    }

    private static void StickyExpiresAndReArms()
    {
        long t = 20_000;
        var s = new Slots();
        s.Arm(Craft, 100 | Flag, 60_000, t);
        Eq(s.Request(Craft, 100, t + 60_000), V.AcceptSticky, "at the timeout itself: Yes");
        Eq(s.Request(Craft, 100, t + 60_001), V.LeaveToClient, "past it: the client's");
        Check(!s.Sticky.Armed, "dropped");
        Eq(s.Request(Craft, 100, t + 60_002), V.LeaveToClient, "and stays dropped");

        // Re-arming before it runs out keeps it going (what RynthAi does).
        s.Arm(Craft, 100 | Flag, 60_000, t);
        s.Arm(Craft, 100 | Flag, 60_000, t + 30_000);
        Eq(s.Request(Craft, 100, t + 75_000), V.AcceptSticky, "re-armed at +30 s: still Yes at +75 s");
        // A new sticky arm replaces the old one (one sticky slot).
        s.Arm(Craft, 90 | Flag, 60_000, t + 76_000);
        Eq(s.Request(Craft, 92, t + 76_001), V.AcceptSticky, "re-armed at 90: 92% is Yes");
    }

    private static void OtherTypesAreLeft()
    {
        long t = 30_000;
        var s = new Slots();
        s.Arm(Craft, 100 | Flag, 60_000, t);
        Eq(s.Request(YesNo, 100, t + 1), V.LeaveToClient, "another type: the client's");
        Check(s.Sticky.Armed, "the craft arm is kept");
        s.Arm(Craft, 80, 15_000, t);
        Eq(s.Request(YesNo, 100, t + 1), V.LeaveToClient, "one-shot, another type: the client's");
        Check(s.OneShot.Armed, "the one-shot is kept for its own type (as before)");

        var none = new Slots();
        Eq(none.Request(Craft, 100, t), V.LeaveToClient, "unarmed: the client's");
    }

    private static void OneShotGoesFirstAndNeverFights()
    {
        // A tinker run arms a one-shot per step (Yes at its own minimum, else No) and disarms it when a
        // step fails to go out or the run stops; a sticky arm stands the whole time.
        long t = 40_000;
        var s = new Slots();
        s.Arm(Craft, 100 | Flag, 60_000, t);
        s.Arm(Craft, 70, 15_000, t + 1);
        Check(s.Sticky.Armed && s.OneShot.Armed, "both armed");
        Eq(s.Request(Craft, 60, t + 2), V.Decline, "the step's prompt: the one-shot's own rule (60% under 70 is No)");
        Check(!s.OneShot.Armed, "the one-shot is spent");
        Check(s.Sticky.Armed, "the sticky arm is untouched");
        Eq(s.Request(Craft, 100, t + 3), V.AcceptSticky, "the next (hand-made) prompt: the sticky arm");

        s.Arm(Craft, 0, 15_000, t + 4);
        Eq(s.Request(Craft, 100, t + 5), V.Accept, "a one-shot's Yes is the one-shot's (not counted as the sticky arm's)");

        // The sticky arm re-arming mid-step does not replace the step's one-shot.
        s.Arm(Craft, 70, 15_000, t + 6);
        s.Arm(Craft, 100 | Flag, 60_000, t + 7);
        Check(s.OneShot.Armed && s.OneShot.MinPercent == 70, "a sticky re-arm leaves the one-shot as it was");
        Eq(s.Request(Craft, 80, t + 8), V.Accept, "80% on the step: the one-shot's Yes");

        // The run's disarm (timeout 0, no flag) leaves the sticky arm.
        s.Arm(Craft, 70, 15_000, t + 9);
        Eq(s.Arm(Craft, 0, 0, t + 10), P.ArmOk, "the run disarms its one-shot");
        Check(!s.OneShot.Armed && s.Sticky.Armed, "only the one-shot is gone");
        Eq(s.Request(Craft, 100, t + 11), V.AcceptSticky, "a 100% prompt after the run: the sticky arm");
        Eq(s.Request(Craft, 50, t + 12), V.LeaveToClient, "a 50% prompt after the run: the client's");

        // An expired one-shot falls through to the sticky arm.
        s.Arm(Craft, 70, 15_000, t + 13);
        Eq(s.Request(Craft, 100, t + 13 + 15_001), V.AcceptSticky, "past the one-shot's timeout: the sticky arm");
    }

    private static void DisarmPicksTheSlot()
    {
        long t = 50_000;
        var s = new Slots();
        s.Arm(Craft, 100 | Flag, 60_000, t);
        s.Arm(Craft, 70, 15_000, t);
        Eq(s.Arm(Craft, Flag, 0, t + 1), P.ArmOk, "sticky disarm (the flag, timeout 0)");
        Check(!s.Sticky.Armed && s.OneShot.Armed, "only the sticky arm is gone");
        Eq(s.Arm(Craft, 100 | Flag, -1, t + 2), P.ArmOk, "a negative timeout with the flag disarms too");
        s.Arm(Craft, 100 | Flag, 60_000, t + 3);
        Eq(s.Arm(Craft, 100 | Flag, 0, t + 4), P.ArmOk, "the flag with a minimum and timeout 0: disarm");
        Check(!s.Sticky.Armed, "gone");
        Eq(s.Request(Craft, 100, t + 5), V.Accept, "the one-shot is still there");
    }

    private static void Unavailable()
    {
        var s = new Slots { Available = false };
        Eq(s.Arm(Craft, 100 | Flag, 60_000, 0), P.ArmUnavailable, "no sender or hook: 0");
        Eq(s.Arm(Craft, 100, 15_000, 0), P.ArmUnavailable, "one-shot: 0");
        Eq(s.Arm(Craft, 101 | Flag, 60_000, 0), P.ArmBadArgs, "bad arguments still -3 first");
        Check(!s.OneShot.Armed && !s.Sticky.Armed, "nothing armed");
        Eq(s.Arm(Craft, Flag, 0, 0), P.ArmOk, "a disarm still answers 1");
    }

    private static int Pct(string s) => P.ParsePercent(Encoding.Latin1.GetBytes(s));

    private static void PercentParse()
    {
        Eq(Pct("You determine that you have a 38 percent chance to succeed."), 38, "38");
        Eq(Pct("You determine that you have a 100 percent chance to succeed."), 100, "100");
        Eq(Pct("You determine that you have a 0 percent chance to succeed."), 0, "0");
        Eq(Pct("You determine that you have a 5 percent chance to succeed."), 5, "5");
        Eq(Pct("Are you sure?"), -1, "no percent");
        Eq(Pct(" percent at the start"), -1, "' percent' first");
        Eq(Pct("a percent chance"), -1, "no digits");
        Eq(Pct("1000 percent"), -1, "four digits");
        Eq(Pct("999 percent"), 100, "over 100 reads as 100");
        Eq(Pct(""), -1, "empty");
    }
}
