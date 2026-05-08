using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
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
        UserPrefsPathBox.Text = profile.UserPrefsPath;
        PasswordHintText.Text = string.IsNullOrEmpty(profile.Password)
            ? "No password is saved for this profile yet."
            : "A password is already saved for this profile. It stays hidden unless you replace or clear it.";

        SaveButton.Click += (_, _) => SaveAndClose();
        CancelButton.Click += (_, _) => Close(false);
        BrowseUserPrefsButton.Click += async (_, _) => await BrowseUserPrefsAsync();
        SnapshotUserPrefsButton.Click += (_, _) => SnapshotLiveUserPrefs();
    }

    private async System.Threading.Tasks.Task BrowseUserPrefsAsync()
    {
        var topLevel = GetTopLevel(this);
        if (topLevel == null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select a UserPreferences.ini stash",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Asheron's Call prefs (*.ini)") { Patterns = ["*.ini"] },
                new FilePickerFileType("All Files") { Patterns = ["*"] }
            ]
        });

        if (files.Count == 0)
            return;

        UserPrefsPathBox.Text = Path.GetFullPath(files[0].Path.LocalPath);
    }

    private void SnapshotLiveUserPrefs()
    {
        string stashPath = UserPrefsPathBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(stashPath))
        {
            string accountName = AccountBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(accountName))
            {
                UserPrefsHintText.Text = "Snapshot needs an account name (so it can pick a default stash file).";
                return;
            }

            string appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string safeName = string.Join("_", accountName.Split(Path.GetInvalidFileNameChars()));
            stashPath = Path.Combine(appdata, "RynthCore", "prefs", $"{safeName}.ini");
        }

        string livePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Asheron's Call",
            "UserPreferences.ini");

        if (!File.Exists(livePath))
        {
            UserPrefsHintText.Text = $"Live UserPreferences.ini not found at {livePath}.";
            return;
        }

        try
        {
            string? dir = Path.GetDirectoryName(stashPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            File.Copy(livePath, stashPath, overwrite: true);
            UserPrefsHintText.Text = $"Snapshot saved to {stashPath}.";
        }
        catch (Exception ex)
        {
            UserPrefsHintText.Text = $"Snapshot failed: {ex.Message}";
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

        _profile.Alias = AliasBox.Text?.Trim() ?? string.Empty;
        _profile.UserPrefsPath = UserPrefsPathBox.Text?.Trim() ?? string.Empty;

        Close(true);
    }
}
