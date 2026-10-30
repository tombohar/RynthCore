using System;

namespace RynthCore.App;

/// <summary>
/// The launcher puts a client's saved window back once, when AC reports the login. AC can still shrink
/// its window to the small login / character-select size afterwards, while it brings the character in,
/// and then draw the game at full size into that small window. Drakkon (2026-10-25 23:04): restored to
/// 1606x929 three seconds after the login, back at 806x629 three seconds later, and left there all
/// night while the game drew 1600x900 into it; the tracker only refused to save that size. For a
/// short while after the restore, a window that collapsed below half the saved area is put back, a few
/// times at most, and never once the client has logged out (that is the real character select).
/// Pure: MainWindow does the window calls. Checked in tools\LauncherSettingsTests.
/// </summary>
internal static class AcWindowRestoreRule
{
    /// <summary>How long after the restore a login-size window is put back.</summary>
    public static readonly TimeSpan Watch = TimeSpan.FromSeconds(60);
    /// <summary>The most times it is put back after one restore.</summary>
    public const int MaxReapplies = 5;
    /// <summary>The least time between two put-backs (AC may still be resizing).</summary>
    public static readonly TimeSpan ReapplyEvery = TimeSpan.FromSeconds(3);

    /// <summary>The window collapsed below half the saved in-game area: AC's login / character-select window.</summary>
    public static bool IsLoginShrink(int width, int height, int savedWidth, int savedHeight) =>
        savedWidth > 0 && savedHeight > 0 && (long)width * height * 2L < (long)savedWidth * savedHeight;

    /// <summary>
    /// Put the saved window back now: a login-size window within <see cref="Watch"/> of the restore,
    /// not logged out, fewer than <see cref="MaxReapplies"/> put-backs, the last one at least
    /// <see cref="ReapplyEvery"/> ago.
    /// </summary>
    public static bool ShouldReapply(DateTime nowUtc, DateTime restoredAtUtc, DateTime? lastReapplyUtc, int reapplies,
                                     bool loggedOut, int width, int height, int savedWidth, int savedHeight)
    {
        if (loggedOut || reapplies >= MaxReapplies) return false;
        if (nowUtc - restoredAtUtc > Watch) return false;
        if (lastReapplyUtc is DateTime last && nowUtc - last < ReapplyEvery) return false;
        return IsLoginShrink(width, height, savedWidth, savedHeight);
    }
}
