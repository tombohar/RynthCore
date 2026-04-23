using System;

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
            ServerId = ServerId
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
    }

    public override string ToString()
    {
        return DisplayName;
    }
}
