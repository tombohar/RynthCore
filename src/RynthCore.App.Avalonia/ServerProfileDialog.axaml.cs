using System;
using System.Collections.Generic;
using Avalonia.Controls;
using RynthCore.App;

namespace RynthCore.App.Avalonia;

internal partial class ServerProfileDialog : Window
{
    /// <summary>The picker's first entry: keep whatever is typed in the fields.</summary>
    private const string CustomServerLabel = "Custom server (fill in the fields below)";

    private readonly RynthCore.App.LaunchServerProfile _profile;

    /// <summary>True while the constructor selects the profile's known server, so it isn't re-applied.</summary>
    private bool _loading;

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

        var choices = new List<object> { CustomServerLabel };
        choices.AddRange(KnownServerCatalog.All);
        KnownServerBox.ItemsSource = choices;
        KnownServerBox.SelectionChanged += (_, _) => OnKnownServerChanged();

        _loading = true;
        KnownServer? current = KnownServerCatalog.FindByHost(profile.Host);
        KnownServerBox.SelectedItem = current != null ? current : CustomServerLabel;
        _loading = false;
        ShowKnownServerNote(current);

        SaveButton.Click += (_, _) => SaveAndClose();
        CancelButton.Click += (_, _) => Close(false);
    }

    /// <summary>
    /// Picking a known server fills Name, Host, Port and Emulator from the catalog; Alias and
    /// Rodat stay as typed. Picking "Custom server" leaves the fields alone.
    /// </summary>
    private void OnKnownServerChanged()
    {
        KnownServer? server = KnownServerBox.SelectedItem as KnownServer;
        ShowKnownServerNote(server);
        if (_loading || server == null)
            return;

        NameBox.Text = server.Name;
        HostBox.Text = server.Host;
        PortBox.Text = server.Port.ToString();
        EmulatorBox.SelectedItem = server.Emulator.ToString();
        ValidationText.Text = string.Empty;
    }

    /// <summary>The picked server's description and the RynthSuite plugin RynthCore loads for it.</summary>
    private void ShowKnownServerNote(KnownServer? server)
    {
        KnownServerNote.Text = server == null
            ? "Any server works. Known servers fill in the connection details; each has a RynthSuite server plugin name."
            : $"{server.Note}  Server plugin: {server.PluginName} (RynthCore loads it when you connect to {server.Host}, if it is installed).";
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
