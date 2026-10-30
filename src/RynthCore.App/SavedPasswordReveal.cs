using System;

namespace RynthCore.App;

/// <summary>
/// The "show password" eye beside the account editor's password box. Pure: no UI.
///
/// The box starts empty ("leave blank to keep the saved password"). Turning the eye on with the
/// box empty fills it with the saved password, decrypted at that moment, so the player can read
/// it; with something typed, the eye only reveals what was typed. Turning the eye off empties the
/// box again when it still holds the saved password it was filled with, so blank = keep holds,
/// and saving it unchanged keeps the saved password as it is (nothing re-encrypted). A saved
/// password that can't be read on this Windows user (needs re-entering) is never filled in.
///
/// Keeps no copy of the password: only whether the box was filled; the box's text is compared
/// with a fresh decrypt. Never log what these return.
/// </summary>
internal sealed class SavedPasswordReveal
{
    public const string ShowTip = "Show password";
    public const string HideTip = "Hide password";

    // The saved password decrypted now; null when none is saved or it can't be read here.
    private readonly Func<string?> _readSaved;

    public SavedPasswordReveal(Func<string?> readSaved)
    {
        _readSaved = readSaved ?? throw new ArgumentNullException(nameof(readSaved));
    }

    /// <summary>For an account profile (the editor's draft): its saved password, if readable.</summary>
    public static SavedPasswordReveal For(LaunchAccountProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new SavedPasswordReveal(() =>
            profile.HasSavedPassword && !profile.PasswordNeedsReentry
            && profile.TryGetPasswordForLaunch(out string saved, out _) && saved.Length > 0
                ? saved
                : null);
    }

    /// <summary>True while the box holds the saved password the eye filled in.</summary>
    public bool FilledFromSaved { get; private set; }

    /// <summary>The eye turned on: the box's text, or the saved password when the box is empty.</summary>
    public string EyeOn(string? boxText)
    {
        if (string.IsNullOrEmpty(boxText) && _readSaved() is string saved)
        {
            FilledFromSaved = true;
            return saved;
        }
        FilledFromSaved = false;
        return boxText ?? string.Empty;
    }

    /// <summary>
    /// The eye turned off: the box's new text. Empty again when it still holds the saved password
    /// it was filled with; anything the player typed or changed stays.
    /// </summary>
    public string EyeOff(string? boxText)
    {
        string text = boxText ?? string.Empty;
        bool unchanged = HoldsSaved(text);
        FilledFromSaved = false;
        return unchanged ? string.Empty : text;
    }

    /// <summary>
    /// What Save does with the box: null = keep the saved password as it is (the box is blank, or
    /// holds the saved password the eye showed, unchanged); otherwise the new password to save.
    /// </summary>
    public string? PasswordToSave(string? boxText)
    {
        if (string.IsNullOrEmpty(boxText) || HoldsSaved(boxText))
            return null;
        return boxText;
    }

    /// <summary>Forgets the fill (the dialog closes).</summary>
    public void Reset() => FilledFromSaved = false;

    private bool HoldsSaved(string text) =>
        FilledFromSaved && text.Length > 0 && _readSaved() is string saved && string.Equals(saved, text, StringComparison.Ordinal);
}
