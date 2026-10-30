using System;
using System.Collections.Generic;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using RynthCore.App;

namespace RynthCore.App.Avalonia;

/// <summary>
/// Asked before launching an account that has no usable password (a new server, or a profile added
/// without one): the account name (one you already use, or a new one), its password, and which
/// character logs in. Edits the profile passed in only when the player presses Log In.
/// </summary>
internal partial class FirstLoginDialog : Window
{
    private readonly LaunchAccountProfile _profile;
    private readonly LaunchServerProfile _server;
    private readonly IReadOnlyList<LaunchAccountProfile> _allProfiles;
    private readonly Func<string, List<string>> _knownCharactersFor;

    private List<string> _knownCharacters = [];
    private LaunchAccountProfile? _passwordSource;

    public FirstLoginDialog(
        LaunchAccountProfile profile,
        LaunchServerProfile server,
        IReadOnlyList<LaunchAccountProfile> allProfiles,
        Func<string, List<string>> knownCharactersFor)
    {
        _profile = profile;
        _server = server;
        _allProfiles = allProfiles;
        _knownCharactersFor = knownCharactersFor;
        InitializeComponent();

        string serverLabel = server.DisplayName;
        HeaderText.Text = $"First login on {serverLabel}";
        IntroText.Text = profile.PasswordNeedsReentry
            ? "The saved password for this account can't be read on this Windows user (settings copied from another user or PC?). Enter it again to log in."
            : "No password is saved for this account yet, and the game client won't start without one. "
              + "ACE servers create a new account the first time it logs in (if the server allows it). "
              + "With no character picked, the client stops at character select, where you can create characters. "
              + "Every character on the account is remembered, and you pick which one logs in from the launch list.";

        KnownAccountBox.ItemsSource = FirstLoginRules.KnownAccountNames(allProfiles, profile.Id);
        KnownAccountBox.IsEnabled = KnownAccountBox.ItemCount > 0;
        KnownAccountBox.SelectionChanged += (_, _) =>
        {
            if (KnownAccountBox.SelectedItem is string picked)
                AccountBox.Text = picked;
        };

        AccountBox.Text = profile.AccountName;
        AccountBox.TextChanged += (_, _) => OnAccountChanged();
        ReuseSavedPasswordCheckBox.IsCheckedChanged += (_, _) => UpdatePasswordPanel();

        ShowPasswordToggle.IsCheckedChanged += (_, _) => Reveal(PasswordBox, ShowPasswordToggle);
        ShowConfirmToggle.IsCheckedChanged += (_, _) => Reveal(ConfirmBox, ShowConfirmToggle);
        // The plain text lives only in the boxes, and only while the dialog is open.
        Closed += (_, _) =>
        {
            PasswordBox.Text = string.Empty;
            ConfirmBox.Text = string.Empty;
        };

        LoginButton.Click += (_, _) => LogInAndClose();
        CancelButton.Click += (_, _) => Close(false);

        OnAccountChanged();
    }

    /// <summary>An eye: shows or hides what's typed in its box (nothing saved to fill in here).</summary>
    private static void Reveal(TextBox box, ToggleButton eye)
    {
        bool show = eye.IsChecked == true;
        box.RevealPassword = show;
        string tip = show ? SavedPasswordReveal.HideTip : SavedPasswordReveal.ShowTip;
        ToolTip.SetTip(eye, tip);
        AutomationProperties.SetName(eye, tip);
    }

    /// <summary>Re-reads the account's characters and whether another profile's password can be reused.</summary>
    private void OnAccountChanged()
    {
        string name = AccountBox.Text?.Trim() ?? string.Empty;

        _passwordSource = FirstLoginRules.SavedPasswordSource(_allProfiles, name, _profile.Id);
        if (_passwordSource != null)
        {
            string from = _passwordSource.DisplayName;
            ReuseSavedPasswordCheckBox.Content = $"Use the password saved for '{name}' in profile {from}";
            ReuseSavedPasswordCheckBox.IsVisible = true;
        }
        else
        {
            ReuseSavedPasswordCheckBox.IsVisible = false;
            ReuseSavedPasswordCheckBox.IsChecked = false;
        }
        UpdatePasswordPanel();

        _knownCharacters = name.Length > 0 ? _knownCharactersFor(name) : [];
        string? keep = LoginAsBox.SelectedItem as string;
        List<string> choices = FirstLoginRules.LoginChoices(_knownCharacters, _profile.CharacterName);
        LoginAsBox.ItemsSource = choices;
        LoginAsBox.SelectedItem = keep != null && choices.Contains(keep) ? keep
            : choices.Contains(_profile.CharacterName) ? _profile.CharacterName
            : FirstLoginRules.StopAtCharacterSelect;
        LoginAsHintText.Text = _knownCharacters.Count == 0
            ? $"No characters are known for this account on {_server.DisplayName} yet. Create them at character select; RynthCore picks them up there."
            : $"{_knownCharacters.Count} character(s) known for this account. Pick one to log in automatically, or stop at character select.";
    }

    private void UpdatePasswordPanel()
    {
        bool reuse = ReuseSavedPasswordCheckBox.IsVisible && ReuseSavedPasswordCheckBox.IsChecked == true;
        PasswordPanel.IsVisible = !reuse;
    }

    private void LogInAndClose()
    {
        bool reuse = _passwordSource != null && ReuseSavedPasswordCheckBox.IsChecked == true;
        string name = AccountBox.Text?.Trim() ?? string.Empty;

        if (FirstLoginRules.Problem(name, PasswordBox.Text, ConfirmBox.Text, reuse, _server.Emulator) is string problem)
        {
            ValidationText.Text = problem;
            return;
        }

        _profile.AccountName = name;
        _profile.ServerId = _server.Id;
        if (reuse)
            _profile.CopyPasswordFrom(_passwordSource!);
        else
            _profile.SetPassword(PasswordBox.Text);   // encrypted at once; the plain text isn't kept
        PasswordBox.Text = string.Empty;
        ConfirmBox.Text = string.Empty;

        _profile.CharacterName = FirstLoginRules.CharacterNameFor(LoginAsBox.SelectedItem as string, _knownCharacters);
        Close(true);
    }
}
