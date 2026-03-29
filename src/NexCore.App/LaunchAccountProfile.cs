using System;

namespace NexCore.App;

internal sealed class LaunchAccountProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AccountName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string CharacterName { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;

    public string DisplayName
    {
        get
        {
            string launchLabel = !string.IsNullOrWhiteSpace(CharacterName)
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
            Alias = Alias
        };
    }

    public void CopyFrom(LaunchAccountProfile source)
    {
        Id = source.Id;
        AccountName = source.AccountName;
        Password = source.Password;
        CharacterName = source.CharacterName;
        Alias = source.Alias;
    }

    public override string ToString()
    {
        return DisplayName;
    }
}
