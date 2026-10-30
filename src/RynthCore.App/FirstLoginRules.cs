using System;
using System.Collections.Generic;
using System.Linq;

namespace RynthCore.App;

/// <summary>
/// The rules behind the launcher's "First login" dialog, shown when an account is launched with no
/// usable password (a new server, or a profile added without one). Pure: no UI, no files.
///
/// The client refuses an empty password before it connects ("You can only run the game from the
/// launcher program"), so a password is always required. ACE creates an account the first time a
/// new name logs on with a password (AllowAutoAccountCreation), so a new name here is how an
/// account is created. With no character known yet the client stops at character select, where
/// characters are created; the engine saves the account's character list from there.
/// </summary>
internal static class FirstLoginRules
{
    /// <summary>The "Log in as" entry that stops at character select.</summary>
    public const string StopAtCharacterSelect = "Stop at character select (create or pick a character there)";

    /// <summary>
    /// Account names from the other profiles, distinct (case-insensitive) and sorted, for picking
    /// an account you already use elsewhere.
    /// </summary>
    internal static List<string> KnownAccountNames(IEnumerable<LaunchAccountProfile> profiles, string excludeProfileId)
    {
        return profiles
            .Where(p => !string.Equals(p.Id, excludeProfileId, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.AccountName?.Trim() ?? string.Empty)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Another profile with the same account name and a password readable on this Windows user,
    /// whose saved password can be reused (null = none). The same name on another server is a
    /// different account, so the caller offers this as a choice, never silently.
    /// </summary>
    internal static LaunchAccountProfile? SavedPasswordSource(
        IEnumerable<LaunchAccountProfile> profiles, string accountName, string excludeProfileId)
    {
        string name = accountName?.Trim() ?? string.Empty;
        if (name.Length == 0)
            return null;

        return profiles.FirstOrDefault(p =>
            !string.Equals(p.Id, excludeProfileId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(p.AccountName?.Trim(), name, StringComparison.OrdinalIgnoreCase)
            && p.HasSavedPassword
            && !p.PasswordNeedsReentry);
    }

    /// <summary>
    /// The "Log in as" choices: stop at character select, then the account's known characters
    /// (the saved one first if the list hasn't seen it yet).
    /// </summary>
    internal static List<string> LoginChoices(IReadOnlyList<string> knownCharacters, string? savedCharacter)
    {
        var choices = new List<string> { StopAtCharacterSelect };
        if (!string.IsNullOrWhiteSpace(savedCharacter)
            && savedCharacter != LaunchAccountProfile.NoneOption
            && !knownCharacters.Contains(savedCharacter, StringComparer.OrdinalIgnoreCase))
        {
            choices.Add(savedCharacter);
        }
        choices.AddRange(knownCharacters.Where(c => !string.IsNullOrWhiteSpace(c)));
        return choices;
    }

    /// <summary>
    /// The CharacterName to save for <paramref name="choice"/>. Stopping at character select with
    /// no character known yet saves "" rather than the explicit "(None)": once the first character
    /// is created and it is the only one, the launcher picks it for the next launch.
    /// </summary>
    internal static string CharacterNameFor(string? choice, IReadOnlyList<string> knownCharacters)
    {
        if (string.IsNullOrWhiteSpace(choice) || choice == StopAtCharacterSelect)
            return knownCharacters.Count == 0 ? string.Empty : LaunchAccountProfile.NoneOption;
        return choice;
    }

    /// <summary>What's wrong with the entries (null = ready to log in).</summary>
    internal static string? Problem(string? accountName, string? password, string? confirm, bool reuseSavedPassword, AcEmulatorKind emulator)
    {
        if (AcLaunchArgumentBuilder.AccountNameProblem(accountName, emulator) is string nameProblem)
            return nameProblem;
        if (reuseSavedPassword)
            return null;
        if (string.IsNullOrEmpty(password))
            return "Enter the password. The client won't start without one; for a new account it becomes the account's password.";
        if (!string.Equals(password, confirm, StringComparison.Ordinal))
            return "The two passwords don't match.";
        return null;
    }
}
