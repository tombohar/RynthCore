using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using RynthCore.App;

namespace RynthCore.App.Avalonia;

internal partial class AccountProfileDialog : Window
{
    private readonly LaunchAccountProfile _profile;
    private readonly List<LaunchServerProfile> _servers;

    public AccountProfileDialog(LaunchAccountProfile profile, List<LaunchServerProfile> servers)
    {
        _profile = profile;
        _servers = servers;
        InitializeComponent();

        ServerComboBox.ItemsSource = _servers.Select(s => s.DisplayName).ToList();
        int serverIndex = _servers.FindIndex(s => s.Id == profile.ServerId);
        if (serverIndex >= 0)
            ServerComboBox.SelectedIndex = serverIndex;

        AccountBox.Text = profile.AccountName;
        AliasBox.Text = profile.Alias;
        PasswordHintText.Text = string.IsNullOrEmpty(profile.Password)
            ? "No password is saved for this profile yet."
            : "A password is already saved for this profile. It stays hidden unless you replace or clear it.";

        PopulateCharacterDropdown(profile.AccountName, GetSelectedServerName(), profile.CharacterName);

        // Re-populate characters when account name or server changes
        AccountBox.TextChanged += (_, _) =>
        {
            string currentChar = GetSelectedCharacterName();
            PopulateCharacterDropdown(AccountBox.Text?.Trim() ?? string.Empty, GetSelectedServerName(), currentChar);
        };
        ServerComboBox.SelectionChanged += (_, _) =>
        {
            PopulateCharacterDropdown(AccountBox.Text?.Trim() ?? string.Empty, GetSelectedServerName(), GetSelectedCharacterName());
        };

        SaveButton.Click += (_, _) => SaveAndClose();
        CancelButton.Click += (_, _) => Close(false);
    }

    private string GetSelectedServerName() =>
        ServerComboBox.SelectedIndex >= 0 && ServerComboBox.SelectedIndex < _servers.Count
            ? _servers[ServerComboBox.SelectedIndex].Name
            : string.Empty;

    private void PopulateCharacterDropdown(string accountName, string serverName, string selectedCharacter)
    {
        List<string> chars = LoadDetectedCharacters(accountName, serverName);

        // Ensure the existing saved name appears even if the scan file is empty
        if (!string.IsNullOrEmpty(selectedCharacter)
            && selectedCharacter != LaunchAccountProfile.NoneOption
            && !chars.Contains(selectedCharacter))
        {
            chars.Insert(0, selectedCharacter);
        }

        // "(None)" sentinel at the top so users can opt out of auto-login per account
        chars.Insert(0, LaunchAccountProfile.NoneOption);

        CharacterBox.ItemsSource = chars;

        if (string.IsNullOrEmpty(selectedCharacter) || selectedCharacter == LaunchAccountProfile.NoneOption)
            CharacterBox.SelectedItem = LaunchAccountProfile.NoneOption;
        else if (chars.Contains(selectedCharacter))
            CharacterBox.SelectedItem = selectedCharacter;
    }

    private string GetSelectedCharacterName() =>
        CharacterBox.SelectedItem as string ?? string.Empty;

    private static List<string> LoadDetectedCharacters(string accountName, string serverName = "")
    {
        try
        {
            List<string> cachedCharacters = CharacterCacheStore.Read(accountName, serverName);
            if (cachedCharacters.Count > 0)
                return cachedCharacters;

            // 3. ThwargLauncher character files
            return ReadThwargCharacters(accountName, serverName);
        }
        catch
        {
            return [];
        }
    }

    private static List<string> ReadThwargCharacters(string accountName, string serverName = "")
    {
        try
        {
            string charDir = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "ThwargLauncher", "characters");

            if (!Directory.Exists(charDir))
                return [];

            var results = new List<string>();
            foreach (string file in Directory.GetFiles(charDir, "*.txt"))
            {
                try
                {
                    string json = File.ReadAllText(file);
                    using var doc = JsonDocument.Parse(json);
                    foreach (var entry in doc.RootElement.EnumerateObject())
                    {
                        bool accountMatches = string.IsNullOrWhiteSpace(accountName) ||
                            entry.Name.EndsWith("-" + accountName, System.StringComparison.OrdinalIgnoreCase);
                        bool serverMatches = string.IsNullOrWhiteSpace(serverName) ||
                            entry.Name.StartsWith(serverName + "-", System.StringComparison.OrdinalIgnoreCase);

                        if (!accountMatches || !serverMatches) continue;
                        if (!entry.Value.TryGetProperty("CharacterList", out var list)) continue;

                        foreach (var charEl in list.EnumerateArray())
                        {
                            if (charEl.TryGetProperty("Name", out var nameEl))
                            {
                                string name = nameEl.GetString() ?? string.Empty;
                                if (!string.IsNullOrEmpty(name) && !results.Contains(name))
                                    results.Add(name);
                            }
                        }
                    }
                }
                catch { }
            }
            return results;
        }
        catch
        {
            return [];
        }
    }
    private void SaveAndClose()
    {
        if (string.IsNullOrWhiteSpace(AccountBox.Text))
        {
            ValidationText.Text = "Account name is required.";
            return;
        }

        if (ServerComboBox.SelectedIndex >= 0)
            _profile.ServerId = _servers[ServerComboBox.SelectedIndex].Id;

        _profile.AccountName = AccountBox.Text.Trim();
        if (ClearPasswordCheckBox.IsChecked == true)
        {
            _profile.Password = string.Empty;
        }
        else if (!string.IsNullOrEmpty(PasswordBox.Text))
        {
            _profile.Password = PasswordBox.Text;
        }

        _profile.CharacterName = GetSelectedCharacterName();
        _profile.Alias = AliasBox.Text?.Trim() ?? string.Empty;

        Close(true);
    }
}
