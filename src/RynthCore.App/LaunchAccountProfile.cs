using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RynthCore.App;

internal sealed class LaunchAccountProfile
{
    /// Sentinel stored in CharacterName when the user has explicitly opted out of auto-login.
    /// At launch time, ResolveTargetCharacterForLaunch translates this to an empty string so
    /// the engine's CharacterCapture sees a blank TargetCharacter and skips auto-login entirely.
    public const string NoneOption = "(None — no auto-login)";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AccountName { get; set; } = string.Empty;

    /// The saved password, DPAPI-encrypted for the current Windows user (base64, see
    /// AccountPasswordProtection). Empty = no password saved. Decrypt only when launching:
    /// TryGetPasswordForLaunch.
    public string PasswordProtected { get; set; } = string.Empty;

    /// Plain-text "Password" from a settings file written before passwords were encrypted.
    /// Read only: AppSettingsStore.Load encrypts it into PasswordProtected and clears this, so it
    /// is never written again (null is skipped on save). Stays set only if encryption failed.
    [JsonPropertyName("Password")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyPlainPassword { get; set; }

    /// Set when the saved password can't be decrypted on this Windows user (settings copied
    /// from another user or PC). The UI asks for the password again; launching is refused.
    [JsonIgnore]
    public bool PasswordNeedsReentry { get; set; }

    [JsonIgnore]
    public bool HasSavedPassword => !string.IsNullOrEmpty(PasswordProtected) || !string.IsNullOrEmpty(LegacyPlainPassword);
    public string CharacterName { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;
    public string ServerId { get; set; } = string.Empty;

    // Saved AC client window placement, restored on next launch and updated
    // when the user moves/resizes the window. Null until first save.
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }

    /// Optional path to a per-account stash of UserPreferences.ini. If set, the
    /// launcher copies this file over My Documents\Asheron's Call\UserPreferences.ini
    /// before launching this account. Empty/missing path = no swap, AC reads
    /// whatever happens to be in place.
    public string UserPrefsPath { get; set; } = string.Empty;

    /// Which modding stack to inject when this account is launched. Defaults to
    /// RynthCore. Decal mode launches AC with Decal's Inject.dll instead and
    /// does not load the RynthCore engine into the process.
    public InjectionMode InjectionMode { get; set; } = InjectionMode.RynthCore;

    /// Per-character chat commands to dispatch after login completes. Key is
    /// the character name (case-insensitive on lookup). Each list entry is one
    /// command line — "/say hi", "/fellow create xyz", etc. — sent in order
    /// with a small gap between commands. Empty/missing key = no commands.
    public Dictionary<string, List<string>> OnLoginCommandsByCharacter { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// Milliseconds to wait after the engine observes login-complete before
    /// dispatching the first OnLogin command. Matches Thwargle's default.
    public int OnLoginWaitMs { get; set; } = 3000;

    public string DisplayName
    {
        get
        {
            string launchLabel = !string.IsNullOrWhiteSpace(CharacterName) && CharacterName != NoneOption
                ? CharacterName
                : Alias;

            return string.IsNullOrWhiteSpace(launchLabel)
                ? AccountName
                : $"{launchLabel} ({AccountName})";
        }
    }

    public LaunchAccountProfile Clone()
    {
        return new LaunchAccountProfile
        {
            Id = Id,
            AccountName = AccountName,
            PasswordProtected = PasswordProtected,
            LegacyPlainPassword = LegacyPlainPassword,
            PasswordNeedsReentry = PasswordNeedsReentry,
            CharacterName = CharacterName,
            Alias = Alias,
            ServerId = ServerId,
            WindowX = WindowX,
            WindowY = WindowY,
            WindowWidth = WindowWidth,
            WindowHeight = WindowHeight,
            UserPrefsPath = UserPrefsPath,
            InjectionMode = InjectionMode,
            OnLoginCommandsByCharacter = new Dictionary<string, List<string>>(OnLoginCommandsByCharacter, StringComparer.OrdinalIgnoreCase),
            OnLoginWaitMs = OnLoginWaitMs,
        };
    }

    public void CopyFrom(LaunchAccountProfile source)
    {
        Id = source.Id;
        AccountName = source.AccountName;
        PasswordProtected = source.PasswordProtected;
        LegacyPlainPassword = source.LegacyPlainPassword;
        PasswordNeedsReentry = source.PasswordNeedsReentry;
        CharacterName = source.CharacterName;
        Alias = source.Alias;
        ServerId = source.ServerId;
        WindowX = source.WindowX;
        WindowY = source.WindowY;
        WindowWidth = source.WindowWidth;
        WindowHeight = source.WindowHeight;
        UserPrefsPath = source.UserPrefsPath;
        InjectionMode = source.InjectionMode;
        OnLoginCommandsByCharacter = new Dictionary<string, List<string>>(source.OnLoginCommandsByCharacter, StringComparer.OrdinalIgnoreCase);
        OnLoginWaitMs = source.OnLoginWaitMs;
    }

    /// <summary>Saves a new password (encrypted at once; the plain text is not kept).</summary>
    public void SetPassword(string? plainText)
    {
        PasswordProtected = AccountPasswordProtection.Protect(plainText);
        LegacyPlainPassword = null;
        PasswordNeedsReentry = false;
    }

    /// <summary>
    /// Reuses <paramref name="source"/>'s saved password (the same account name on another server).
    /// The encrypted blob is copied as it is, so the plain text is never handled; it is readable
    /// here because both profiles belong to the same Windows user's settings.
    /// </summary>
    public void CopyPasswordFrom(LaunchAccountProfile source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!string.IsNullOrEmpty(source.LegacyPlainPassword))
        {
            SetPassword(source.LegacyPlainPassword);
            return;
        }
        PasswordProtected = source.PasswordProtected;
        LegacyPlainPassword = null;
        PasswordNeedsReentry = source.PasswordNeedsReentry;
    }

    public void ClearPassword()
    {
        PasswordProtected = string.Empty;
        LegacyPlainPassword = null;
        PasswordNeedsReentry = false;
    }

    /// <summary>
    /// The password to hand the client, decrypted now. True with "" when none is saved. False
    /// (and PasswordNeedsReentry set) when the saved one can't be decrypted on this Windows user;
    /// the caller must not launch with an empty password instead. Use the result at once and
    /// don't store it.
    /// </summary>
    public bool TryGetPasswordForLaunch(out string password, out string problem)
    {
        problem = string.Empty;
        if (!string.IsNullOrEmpty(LegacyPlainPassword))
        {
            password = LegacyPlainPassword;
            return true;
        }
        if (string.IsNullOrEmpty(PasswordProtected))
        {
            password = string.Empty;
            return true;
        }
        if (AccountPasswordProtection.TryUnprotect(PasswordProtected, out password))
        {
            PasswordNeedsReentry = false;
            return true;
        }

        PasswordNeedsReentry = true;
        problem = PasswordNeedsReentryMessage(AccountName);
        return false;
    }

    public static string PasswordNeedsReentryMessage(string? accountName) =>
        $"The saved password for account '{accountName}' can't be read on this Windows user " +
        "(settings copied from another user or PC?). Password needs re-entering: edit the account and type it again.";

    public override string ToString()
    {
        return DisplayName;
    }
}
