using System;
using System.Collections.Generic;

namespace RynthCore.App;

internal sealed class LaunchAccountProfile
{
    /// Sentinel stored in CharacterName when the user has explicitly opted out of auto-login.
    /// At launch time, ResolveTargetCharacterForLaunch translates this to an empty string so
    /// the engine's CharacterCapture sees a blank TargetCharacter and skips auto-login entirely.
    public const string NoneOption = "(None — no auto-login)";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AccountName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
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
            Password = Password,
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
        Password = source.Password;
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

    public override string ToString()
    {
        return DisplayName;
    }
}
