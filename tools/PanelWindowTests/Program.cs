using RynthCore.Engine.UI;

namespace PanelWindowTests;

/// <summary>
/// The backup path of LayeredWindow.Dispose (PanelDestroyPlan.Choose). Win32 only lets the creating
/// thread destroy a window. Idea from Silentkelpie (rynth PR #1); re-implemented here.
/// </summary>
internal static class Program
{
    private static int _checks, _failed;

    private static int Main()
    {
        const uint game = 0x1A2C, avalonia = 0x2B40;

        Check(PanelDestroyPlan.Choose(0, game, ourPanel: false) == PanelDestroyFallback.AlreadyGone,
            "window gone (no owner thread) -> nothing");
        Check(PanelDestroyPlan.Choose(0, game, ourPanel: true) == PanelDestroyFallback.AlreadyGone,
            "window gone even if the class looked like ours -> nothing");

        Check(PanelDestroyPlan.Choose(game, game, ourPanel: true) == PanelDestroyFallback.DestroyHere,
            "game thread owns it, game thread disposing -> destroy here");
        Check(PanelDestroyPlan.Choose(avalonia, avalonia, ourPanel: true) == PanelDestroyFallback.DestroyHere,
            "Avalonia UI thread owns it, Avalonia UI thread disposing -> destroy here");

        Check(PanelDestroyPlan.Choose(avalonia, game, ourPanel: true) == PanelDestroyFallback.LeaveHidden,
            "panel made on the Avalonia UI thread, disposed from the game thread -> leave hidden, no cross-thread destroy");
        Check(PanelDestroyPlan.Choose(game, avalonia, ourPanel: true) == PanelDestroyFallback.LeaveHidden,
            "panel made on the game thread, disposed from the Avalonia UI thread (redock) -> leave hidden");

        Check(PanelDestroyPlan.Choose(game, game, ourPanel: false) == PanelDestroyFallback.AlreadyGone,
            "handle reused by a window that isn't a panel -> never destroy it");
        Check(PanelDestroyPlan.Choose(avalonia, game, ourPanel: false) == PanelDestroyFallback.AlreadyGone,
            "handle reused on another thread -> never touch it");

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
