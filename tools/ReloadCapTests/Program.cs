using RynthCore.Engine;

namespace ReloadCapTests;

/// <summary>
/// The engine's side of the hot-reload cap (ReloadCapNotice.cs): the loader refuses reloads past
/// engine.json MaxHotReloads (default 20, a backstop) and the engine tells the player.
/// </summary>
internal static class Program
{
    private static int _checks, _failed;
    private static string _case = "";

    private static int Main()
    {
        ChatTextIsThePlainSentence();
        ToldOnceThenOnlyWhenAskedByHand();
        AskedByHandFirstStillCountsAsTheOnce();
        ManualWindow();
        LogLineSaysWhy();

        Console.WriteLine($"{_checks} check(s), {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (ok) return;
        _failed++;
        Console.WriteLine($"[FAIL] {_case}: {what}");
    }

    private static void ChatTextIsThePlainSentence()
    {
        _case = "chat text";
        Check(ReloadCapNotice.ChatText(3) == "RynthCore: this client has hot reloaded 3 times; restart the client to pick up the new engine.",
              $"3: {ReloadCapNotice.ChatText(3)}");
        Check(ReloadCapNotice.ChatText(1) == "RynthCore: this client has hot reloaded 1 time; restart the client to pick up the new engine.",
              $"1: {ReloadCapNotice.ChatText(1)}");
        Check(ReloadCapNotice.ChatText(0).Contains("hot reloaded 0 times"), $"0 (MaxHotReloads 0): {ReloadCapNotice.ChatText(0)}");
        Check(ReloadCapNotice.DefaultMaxHotReloads == 20, "the default cap is 20 (Loader.c DEFAULT_MAX_HOT_RELOADS)");
    }

    private static void ToldOnceThenOnlyWhenAskedByHand()
    {
        _case = "once";
        var n = new ReloadCapNotice();
        Check(n.ShouldChat(askedByHand: false), "the first refusal (an engine publish) goes to chat");
        Check(!n.ShouldChat(askedByHand: false), "the second automatic one is logged only");
        Check(!n.ShouldChat(askedByHand: false), "and the third");
        Check(n.ShouldChat(askedByHand: true), "a press of RL is answered");
        Check(n.ShouldChat(askedByHand: true), "every time");
        Check(!n.ShouldChat(askedByHand: false), "automatic ones stay quiet after that");
    }

    private static void AskedByHandFirstStillCountsAsTheOnce()
    {
        _case = "by hand first";
        var n = new ReloadCapNotice();
        Check(n.ShouldChat(askedByHand: true), "the first refusal, asked by hand, goes to chat");
        Check(!n.ShouldChat(askedByHand: false), "the next automatic one doesn't repeat it");
    }

    private static void ManualWindow()
    {
        _case = "manual window";
        Check(!ReloadCapNotice.IsRecentManualSignal(0, 50_000), "never signalled: not by hand");
        Check(ReloadCapNotice.IsRecentManualSignal(100_000, 100_250), "250 ms after RL: by hand");
        Check(ReloadCapNotice.IsRecentManualSignal(100_000, 100_000 + ReloadCapNotice.ManualWindowMs - 1), "just inside the window");
        Check(!ReloadCapNotice.IsRecentManualSignal(100_000, 100_000 + ReloadCapNotice.ManualWindowMs), "at the window's end: not");
        Check(!ReloadCapNotice.IsRecentManualSignal(100_000, 99_000), "a signal 'in the future' (clock oddity): not");
    }

    private static void LogLineSaysWhy()
    {
        _case = "log";
        string auto = ReloadCapNotice.LogText(3, 3, askedByHand: false);
        string hand = ReloadCapNotice.LogText(3, 3, askedByHand: true);
        Check(auto.Contains("hot reloaded 3 time(s)") && auto.Contains("MaxHotReloads is 3") && auto.Contains("restart"), auto);
        Check(!auto.Contains("by hand") && hand.Contains("(asked for by hand)"), hand);
    }
}
