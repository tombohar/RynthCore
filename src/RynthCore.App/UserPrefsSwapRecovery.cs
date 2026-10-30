using System;
using System.IO;

namespace RynthCore.App;

/// <summary>
/// One launch's swap of a saved UserPreferences.ini over the live file in
/// My Documents\Asheron's Call: the saved copy it came from, and the live file as it was before.
/// <paramref name="PreviousLiveBytes"/> is null when there was no live file.
/// </summary>
internal sealed record UserPrefsSwap(
    string StashPath,
    string LivePath,
    byte[] StashBytes,
    byte[]? PreviousLiveBytes);

/// <summary>What <see cref="UserPrefsSwapRecovery.Recover"/> did. Null paths mean that file was left alone.</summary>
internal sealed record UserPrefsRecoveryResult(
    string? QuarantinedStashPath,
    string? QuarantinedLegacyPath,
    bool LiveRestored);

/// <summary>
/// Undoes a UserPreferences.ini swap whose client quit at startup. The game reads its
/// resolution and full-screen mode from that file, so a saved copy with a display mode the PC
/// can't use stops every launch with "Could not initialize Direct3D". The saved copy is only
/// refreshed after a normal session, so without this it would be restored on every launch.
/// Pure file work: no UI, no launcher state.
/// </summary>
internal static class UserPrefsSwapRecovery
{
    /// <summary>Where a failed saved copy is kept: next to it, renamed so it is never swapped in again.</summary>
    internal static string QuarantinePath(string stashPath, DateTime nowLocal) =>
        $"{stashPath}.failed-{nowLocal:yyyyMMdd-HHmmss}";

    /// <summary>
    /// True when the swap changed the live file. When the saved copy matched the live file, the
    /// swap can't have caused the failure, so nothing is undone.
    /// </summary>
    internal static bool SwapChangedPrefs(UserPrefsSwap swap) =>
        swap.PreviousLiveBytes == null || !swap.StashBytes.AsSpan().SequenceEqual(swap.PreviousLiveBytes);

    /// <summary>
    /// Sets the failed saved copy aside (when <paramref name="quarantineStash"/>) and puts the
    /// previous live file back (when <paramref name="restoreLive"/>).
    /// <paramref name="legacyStashPath"/> is the account's name-only copy: it seeds a missing
    /// per-server copy, so it is set aside too when it holds the same failed settings.
    /// </summary>
    internal static UserPrefsRecoveryResult Recover(
        UserPrefsSwap swap,
        bool quarantineStash,
        bool restoreLive,
        string? legacyStashPath,
        DateTime nowLocal)
    {
        string? quarantined = null;
        string? quarantinedLegacy = null;

        if (quarantineStash && File.Exists(swap.StashPath))
        {
            quarantined = QuarantinePath(swap.StashPath, nowLocal);
            File.Move(swap.StashPath, quarantined, overwrite: true);

            if (!string.IsNullOrWhiteSpace(legacyStashPath)
                && !string.Equals(Path.GetFullPath(legacyStashPath), Path.GetFullPath(swap.StashPath), StringComparison.OrdinalIgnoreCase)
                && File.Exists(legacyStashPath)
                && File.ReadAllBytes(legacyStashPath).AsSpan().SequenceEqual(swap.StashBytes))
            {
                quarantinedLegacy = QuarantinePath(legacyStashPath, nowLocal);
                File.Move(legacyStashPath, quarantinedLegacy, overwrite: true);
            }
        }

        bool restored = false;
        if (restoreLive)
        {
            // No live file before the swap: remove it so the game writes its own defaults.
            if (swap.PreviousLiveBytes != null)
                File.WriteAllBytes(swap.LivePath, swap.PreviousLiveBytes);
            else if (File.Exists(swap.LivePath))
                File.Delete(swap.LivePath);
            restored = true;
        }

        return new UserPrefsRecoveryResult(quarantined, quarantinedLegacy, restored);
    }
}
