using System.Collections.Generic;
using System.Linq;
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

        SaveButton.Click += (_, _) => SaveAndClose();
        CancelButton.Click += (_, _) => Close(false);
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

        Close(true);
    }
}
