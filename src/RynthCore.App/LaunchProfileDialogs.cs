using System;
using System.Drawing;
using System.Windows.Forms;

namespace RynthCore.App;

internal static class LaunchProfileDialogs
{
    public static bool TryEditServer(IWin32Window owner, LaunchServerProfile profile)
    {
        using var dialog = CreateEditorDialog("Server Profile", 520, 300);
        var nameBox = CreateTextBox(profile.Name);
        var aliasBox = CreateTextBox(profile.Alias);
        var hostBox = CreateTextBox(profile.Host);
        var portBox = CreateTextBox(profile.Port > 0 ? profile.Port.ToString() : string.Empty);
        var emulatorCombo = CreateComboBox();
        emulatorCombo.Items.AddRange(Enum.GetNames<AcEmulatorKind>());
        emulatorCombo.SelectedItem = profile.Emulator.ToString();
        var rodatCheck = new CheckBox
        {
            Text = "Enable Rodat",
            AutoSize = true,
            Checked = profile.RodatEnabled,
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 6, 0, 0)
        };

        var body = CreateFormLayout();
        body.Controls.Add(CreateLabeledRow("Server Name", nameBox), 0, 0);
        body.Controls.Add(CreateLabeledRow("Alias", aliasBox), 0, 1);
        body.Controls.Add(CreateLabeledRow("Host", hostBox), 0, 2);
        body.Controls.Add(CreateLabeledRow("Port", portBox), 0, 3);
        body.Controls.Add(CreateLabeledRow("Emulator", emulatorCombo), 0, 4);
        body.Controls.Add(rodatCheck, 0, 5);
        dialog.Controls.Add(body);

        void SaveAndClose(object? _, EventArgs __)
        {
            if (string.IsNullOrWhiteSpace(nameBox.Text))
            {
                ShowValidation(dialog, "Server name is required.");
                return;
            }

            if (string.IsNullOrWhiteSpace(hostBox.Text))
            {
                ShowValidation(dialog, "Server host is required.");
                return;
            }

            if (!int.TryParse(portBox.Text.Trim(), out int port) || port <= 0)
            {
                ShowValidation(dialog, "Server port must be a positive integer.");
                return;
            }

            if (emulatorCombo.SelectedItem is not string emulatorText ||
                !Enum.TryParse(emulatorText, ignoreCase: true, out AcEmulatorKind emulator))
            {
                ShowValidation(dialog, "Select an emulator type.");
                return;
            }

            profile.Name = nameBox.Text.Trim();
            profile.Alias = aliasBox.Text.Trim();
            profile.Host = hostBox.Text.Trim();
            profile.Port = port;
            profile.Emulator = emulator;
            profile.RodatEnabled = rodatCheck.Checked;

            dialog.DialogResult = DialogResult.OK;
            dialog.Close();
        }

        WireDialogButtons(dialog, SaveAndClose);
        return dialog.ShowDialog(owner) == DialogResult.OK;
    }

    public static bool TryEditAccount(IWin32Window owner, LaunchAccountProfile profile)
    {
        using var dialog = CreateEditorDialog("Account Profile", 520, 300);
        var accountBox = CreateTextBox(profile.AccountName);
        var passwordBox = CreateTextBox(profile.Password);
        passwordBox.UseSystemPasswordChar = true;
        var characterBox = CreateTextBox(profile.CharacterName);
        var aliasBox = CreateTextBox(profile.Alias);

        var body = CreateFormLayout();
        body.Controls.Add(CreateLabeledRow("Account", accountBox), 0, 0);
        body.Controls.Add(CreateLabeledRow("Password", passwordBox), 0, 1);
        body.Controls.Add(CreateLabeledRow("Character / Slot", characterBox), 0, 2);
        body.Controls.Add(CreateLabeledRow("Alias (Optional)", aliasBox), 0, 3);
        dialog.Controls.Add(body);

        void SaveAndClose(object? _, EventArgs __)
        {
            if (string.IsNullOrWhiteSpace(accountBox.Text))
            {
                ShowValidation(dialog, "Account name is required.");
                return;
            }

            profile.AccountName = accountBox.Text.Trim();
            profile.Password = passwordBox.Text;
            profile.CharacterName = characterBox.Text.Trim();
            profile.Alias = aliasBox.Text.Trim();

            dialog.DialogResult = DialogResult.OK;
            dialog.Close();
        }

        WireDialogButtons(dialog, SaveAndClose);
        return dialog.ShowDialog(owner) == DialogResult.OK;
    }

    private static Form CreateEditorDialog(string title, int width, int height)
    {
        return new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            Width = width,
            Height = height,
            BackColor = Color.FromArgb(20, 28, 36),
            ForeColor = Color.White
        };
    }

    private static TableLayoutPanel CreateFormLayout()
    {
        return new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(14, 14, 14, 56),
            AutoSize = false
        };
    }

    private static Panel CreateLabeledRow(string labelText, Control editor)
    {
        var panel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 54
        };

        var label = new Label
        {
            Text = labelText,
            AutoSize = true,
            ForeColor = Color.Gainsboro,
            Location = new Point(0, 2)
        };

        editor.Location = new Point(0, 22);
        editor.Width = 458;
        panel.Controls.Add(label);
        panel.Controls.Add(editor);
        return panel;
    }

    private static TextBox CreateTextBox(string value)
    {
        return new TextBox
        {
            Text = value,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(15, 22, 30),
            ForeColor = Color.White
        };
    }

    private static ComboBox CreateComboBox()
    {
        return new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(15, 22, 30),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
    }

    private static void WireDialogButtons(Form dialog, EventHandler saveHandler)
    {
        var okButton = new Button
        {
            Text = "Save",
            Width = 96,
            Height = 34,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            Location = new Point(dialog.ClientSize.Width - 214, dialog.ClientSize.Height - 46),
            DialogResult = DialogResult.None
        };
        okButton.Click += saveHandler;

        var cancelButton = new Button
        {
            Text = "Cancel",
            Width = 96,
            Height = 34,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            Location = new Point(dialog.ClientSize.Width - 108, dialog.ClientSize.Height - 46),
            DialogResult = DialogResult.Cancel
        };

        dialog.AcceptButton = okButton;
        dialog.CancelButton = cancelButton;
        dialog.Controls.Add(okButton);
        dialog.Controls.Add(cancelButton);
    }

    private static void ShowValidation(IWin32Window owner, string message)
    {
        MessageBox.Show(owner, message, "Incomplete Profile", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
