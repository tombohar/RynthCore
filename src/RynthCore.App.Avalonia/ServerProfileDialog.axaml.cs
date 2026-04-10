using System;
using Avalonia.Controls;

namespace RynthCore.App.Avalonia;

internal partial class ServerProfileDialog : Window
{
    private readonly RynthCore.App.LaunchServerProfile _profile;

    public ServerProfileDialog(RynthCore.App.LaunchServerProfile profile)
    {
        _profile = profile;
        InitializeComponent();

        EmulatorBox.ItemsSource = Enum.GetNames<RynthCore.App.AcEmulatorKind>();
        NameBox.Text = profile.Name;
        AliasBox.Text = profile.Alias;
        HostBox.Text = profile.Host;
        PortBox.Text = profile.Port > 0 ? profile.Port.ToString() : string.Empty;
        EmulatorBox.SelectedItem = profile.Emulator.ToString();
        RodatCheck.IsChecked = profile.RodatEnabled;

        SaveButton.Click += (_, _) => SaveAndClose();
        CancelButton.Click += (_, _) => Close(false);
    }

    private void SaveAndClose()
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            ValidationText.Text = "Server name is required.";
            return;
        }

        if (string.IsNullOrWhiteSpace(HostBox.Text))
        {
            ValidationText.Text = "Server host is required.";
            return;
        }

        if (!int.TryParse(PortBox.Text?.Trim(), out int port) || port <= 0)
        {
            ValidationText.Text = "Server port must be a positive integer.";
            return;
        }

        if (EmulatorBox.SelectedItem is not string emulatorText ||
            !Enum.TryParse(emulatorText, true, out RynthCore.App.AcEmulatorKind emulator))
        {
            ValidationText.Text = "Select an emulator type.";
            return;
        }

        _profile.Name = NameBox.Text.Trim();
        _profile.Alias = AliasBox.Text?.Trim() ?? string.Empty;
        _profile.Host = HostBox.Text.Trim();
        _profile.Port = port;
        _profile.Emulator = emulator;
        _profile.RodatEnabled = RodatCheck.IsChecked == true;

        Close(true);
    }
}
