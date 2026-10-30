using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using RynthCore.App;

namespace RynthCore.App.Avalonia;

internal partial class AccountProfileDialog : Window
{
    private readonly LaunchAccountProfile _profile;
    private readonly List<LaunchServerProfile> _servers;
    private readonly Func<string, string, List<string>> _knownCharactersFor;
    // The eye beside the password box; it keeps no copy of the password.
    private readonly SavedPasswordReveal _passwordReveal;

    private List<string> _knownCharacters = [];
    // Set once the player picks in "Log in as"; until then the saved choice is kept as it is
    // (an empty one means "the only character, else character select").
    private bool _loginAsChanged;
    private bool _loadingLoginAs;

    /// <param name="knownCharactersFor">(account name, server name) -> the characters known for it.</param>
    public AccountProfileDialog(LaunchAccountProfile profile, List<LaunchServerProfile> servers,
        Func<string, string, List<string>> knownCharactersFor)
    {
        _profile = profile;
        _servers = servers;
        _knownCharactersFor = knownCharactersFor;
        _passwordReveal = SavedPasswordReveal.For(profile);
        InitializeComponent();

        ServerComboBox.ItemsSource = _servers.Select(s => s.DisplayName).ToList();
        int serverIndex = _servers.FindIndex(s => s.Id == profile.ServerId);
        if (serverIndex >= 0)
            ServerComboBox.SelectedIndex = serverIndex;

        AccountBox.Text = profile.AccountName;
        AliasBox.Text = profile.Alias;
        UserPrefsPathBox.Text = profile.UserPrefsPath;
        PasswordHintText.Text = profile.PasswordNeedsReentry
            ? "Password needs re-entering: the saved one can't be read on this Windows user (settings copied from another user or PC?). Type it again to launch this account."
            : !profile.HasSavedPassword
                ? "No password is saved for this profile yet. The game client won't start without one; you'll be asked for it on the first launch."
                : "A password is saved for this profile, encrypted for this Windows user. The eye shows it; type a new one to replace it.";

        ServerComboBox.SelectionChanged += (_, _) => RefreshLoginAs();
        AccountBox.TextChanged += (_, _) => RefreshLoginAs();
        LoginAsBox.SelectionChanged += (_, _) =>
        {
            if (!_loadingLoginAs)
                _loginAsChanged = true;
        };
        RefreshLoginAs();

        ShowPasswordToggle.IsCheckedChanged += (_, _) => OnShowPasswordChanged();
        // The plain text lives only in the box, and only while the dialog is open.
        Closed += (_, _) =>
        {
            PasswordBox.Text = string.Empty;
            _passwordReveal.Reset();
        };

        SaveButton.Click += (_, _) => SaveAndClose();
        CancelButton.Click += (_, _) => Close(false);
        BrowseUserPrefsButton.Click += async (_, _) => await BrowseUserPrefsAsync();
        SnapshotUserPrefsButton.Click += (_, _) => SnapshotLiveUserPrefs();
    }

    /// <summary>
    /// The eye: on shows what's typed, or the saved password (decrypted now) when the box is empty;
    /// off hides it and empties the box again if it still holds the saved password unchanged.
    /// </summary>
    private void OnShowPasswordChanged()
    {
        bool show = ShowPasswordToggle.IsChecked == true;
        PasswordBox.Text = show ? _passwordReveal.EyeOn(PasswordBox.Text) : _passwordReveal.EyeOff(PasswordBox.Text);
        PasswordBox.RevealPassword = show;
        string tip = show ? SavedPasswordReveal.HideTip : SavedPasswordReveal.ShowTip;
        ToolTip.SetTip(ShowPasswordToggle, tip);
        AutomationProperties.SetName(ShowPasswordToggle, tip);
    }

    /// <summary>Lists the characters known for the account on the chosen server in "Log in as".</summary>
    private void RefreshLoginAs()
    {
        string accountName = AccountBox.Text?.Trim() ?? string.Empty;
        string serverName = ServerComboBox.SelectedIndex >= 0 ? _servers[ServerComboBox.SelectedIndex].Name : string.Empty;
        _knownCharacters = accountName.Length > 0 ? _knownCharactersFor(accountName, serverName) : [];

        string? current = _loginAsChanged ? LoginAsBox.SelectedItem as string : null;
        List<string> choices = FirstLoginRules.LoginChoices(_knownCharacters, _profile.CharacterName);

        _loadingLoginAs = true;
        try
        {
            LoginAsBox.ItemsSource = choices;
            LoginAsBox.SelectedItem = current != null && choices.Contains(current) ? current
                : choices.Contains(_profile.CharacterName) ? _profile.CharacterName
                : FirstLoginRules.StopAtCharacterSelect;
        }
        finally
        {
            _loadingLoginAs = false;
        }

        LoginAsHintText.Text = _knownCharacters.Count == 0
            ? "No characters are known for this account on this server yet. Launch it: the client stops at character select, where you can create characters, and RynthCore picks them up there."
            : $"{_knownCharacters.Count} character(s) on this account. Pick the one that logs in automatically, or stop at character select. The launch list's dropdown changes this too.";
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

        LaunchServerProfile? server = ServerComboBox.SelectedIndex >= 0 ? _servers[ServerComboBox.SelectedIndex] : null;
        if (server == null && _servers.Count > 0)
        {
            ValidationText.Text = "Pick the server this account is for.";
            return;
        }

        if (AcLaunchArgumentBuilder.AccountNameProblem(AccountBox.Text, server?.Emulator ?? AcEmulatorKind.Ace) is string nameProblem)
        {
            ValidationText.Text = nameProblem;
            return;
        }

        if (server != null)
            _profile.ServerId = server.Id;
        _profile.AccountName = AccountBox.Text.Trim();
        if (_loginAsChanged)
            _profile.CharacterName = FirstLoginRules.CharacterNameFor(LoginAsBox.SelectedItem as string, _knownCharacters);
        if (ClearPasswordCheckBox.IsChecked == true)
        {
            _profile.ClearPassword();
        }
        else if (_passwordReveal.PasswordToSave(PasswordBox.Text) is string newPassword)
        {
            // Encrypted at once (DPAPI, this Windows user); the plain text isn't kept on the profile.
            // A blank box, or the saved password the eye showed left unchanged, keeps the saved one.
            _profile.SetPassword(newPassword);
        }
        PasswordBox.Text = string.Empty;
        _passwordReveal.Reset();

        _profile.Alias = AliasBox.Text?.Trim() ?? string.Empty;
        _profile.UserPrefsPath = UserPrefsPathBox.Text?.Trim() ?? string.Empty;

        Close(true);
    }
}
