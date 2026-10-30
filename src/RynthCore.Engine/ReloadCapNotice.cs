namespace RynthCore.Engine;

/// <summary>
/// The engine's side of the hot-reload cap (2026-10-11). The native loader (native/Loader/Loader.c,
/// ReloadWatcher) asks the engine for engine.json MaxHotReloads before every hot reload and refuses
/// the reload once this client has done that many; the client keeps running on the current engine
/// and the loader tells the engine (RynthCoreReloadCapped), which logs it and says so in chat: once
/// on its own, and again whenever the player asks for a reload by hand (RL, /rc rl), so a button
/// press never goes unanswered. The cap was 3 after clients died with CoreCLR fatal error
/// 0x80131506 after 4+ reloads (2026-10-08..10); those were the v1 SehTrampoline crash logger
/// (fixed 10-12), so since 2026-10-15 it is 20, a backstop. No engine state here:
/// tools/ReloadCapTests compiles this file in.
/// </summary>
internal sealed class ReloadCapNotice
{
    /// <summary>engine.json MaxHotReloads when the file doesn't set it (and the loader's own
    /// default for an engine without the export, DEFAULT_MAX_HOT_RELOADS in Loader.c).</summary>
    public const int DefaultMaxHotReloads = 20;

    /// <summary>How recent the engine's own reload signal must be to count as "asked by hand".</summary>
    public const long ManualWindowMs = 10_000;

    private bool _told;

    /// <summary>The chat line for the player.</summary>
    public static string ChatText(int reloads) =>
        $"RynthCore: this client has hot reloaded {reloads} {(reloads == 1 ? "time" : "times")}; restart the client to pick up the new engine.";

    /// <summary>The engine log line.</summary>
    public static string LogText(int reloads, int cap, bool askedByHand) =>
        $"Hot reload refused by the loader: this client has hot reloaded {reloads} time(s) and engine.json MaxHotReloads is {cap}" +
        (askedByHand ? " (asked for by hand)" : "") + ". Staying on this engine; a client restart picks up the new one.";

    /// <summary>True when the refusal should go to chat: the first one, and any the player asked
    /// for by hand. Later automatic ones (an engine publish) are logged only.</summary>
    public bool ShouldChat(bool askedByHand)
    {
        if (!_told)
        {
            _told = true;
            return true;
        }
        return askedByHand;
    }

    /// <summary>True when a reload signal the engine sent at <paramref name="signaledAtMs"/>
    /// (Environment.TickCount64; 0 = never) is recent enough at <paramref name="nowMs"/> to count
    /// as the player's.</summary>
    public static bool IsRecentManualSignal(long signaledAtMs, long nowMs) =>
        signaledAtMs != 0 && nowMs - signaledAtMs >= 0 && nowMs - signaledAtMs < ManualWindowMs;
}
