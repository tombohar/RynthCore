using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RynthCore.App;

namespace RynthCore.App.Avalonia;

/// <summary>
/// Small code-built dialogs for the Decal bridge: the "Check Decal bridge" report (with
/// Repair / Copy), the yes/no before an administrator step or Repair Decal, and plain messages.
/// </summary>
internal static class DecalBridgeDialog
{
    private static readonly IBrush Back = new SolidColorBrush(Color.Parse("#132028"));
    private static readonly IBrush Text = new SolidColorBrush(Color.Parse("#EAF0F4"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#9AA8B3"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#26C1A6"));

    /// <summary>Yes/no. True = the person pressed <paramref name="yes"/>.</summary>
    public static async Task<bool> AskAsync(Window owner, string title, string message, string yes, string no)
    {
        var dialog = NewWindow(title, 560);
        var yesButton = new Button { Content = yes, IsDefault = true };
        var noButton = new Button { Content = no, IsCancel = true };
        yesButton.Click += (_, _) => dialog.Close(true);
        noButton.Click += (_, _) => dialog.Close(false);
        dialog.Content = Layout(title,
            new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Text },
            yesButton, noButton);
        bool? result = await dialog.ShowDialog<bool?>(owner);
        return result == true;
    }

    /// <summary>A message with one OK button (selectable text, so paths can be copied).</summary>
    public static async Task InfoAsync(Window owner, string title, string message)
    {
        var dialog = NewWindow(title, 620);
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true };
        ok.Click += (_, _) => dialog.Close(true);
        dialog.Content = Layout(title,
            new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Text },
            ok);
        await dialog.ShowDialog<bool?>(owner);
    }

    /// <summary>
    /// Shows the check's report. With <paramref name="offerRepair"/>, a Repair button; returns
    /// true when the person pressed it.
    /// </summary>
    public static async Task<bool> ShowReportAsync(Window owner, DecalBridgeCheck.Result check, bool offerRepair)
    {
        string title = check.Blocking ? "Decal bridge check: it would NOT load" : "Decal bridge check: OK";
        string report = check.ToReport();
        var dialog = NewWindow(title, 760);

        var body = new TextBox
        {
            Text = report,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
            FontSize = 12,
            MaxHeight = 420,
            Foreground = Text,
            Background = new SolidColorBrush(Color.Parse("#0F161D")),
        };
        var copy = new Button { Content = "Copy report" };
        copy.Click += async (_, _) =>
        {
            try
            {
                var clipboard = TopLevel.GetTopLevel(dialog)?.Clipboard;
                if (clipboard != null) await clipboard.SetTextAsync(report);
                copy.Content = "Copied";
            }
            catch { copy.Content = "Copy failed"; }
        };
        var close = new Button { Content = "Close", IsCancel = true };
        close.Click += (_, _) => dialog.Close(false);

        Control[] buttons;
        if (offerRepair)
        {
            var repair = new Button { Content = check.NeedsMachineWideRegistration ? "Repair (asks for administrator)" : "Repair", IsDefault = true };
            repair.Click += (_, _) => dialog.Close(true);
            buttons = new Control[] { repair, copy, close };
        }
        else
        {
            buttons = new Control[] { copy, close };
        }

        string hint = check.Blocking
            ? "A Decal + RynthCore client started now would run without the bridge: no RynthCore overlay, then a crash. " +
              (offerRepair ? "Repair registers the bridge where Decal reads on this PC and checks again." : "Fix the FAIL lines, then check again.")
            : "A Decal + RynthCore client started now will load the bridge.";
        dialog.Content = Layout(title,
            new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Foreground = Muted },
                    body,
                },
            },
            buttons);
        bool? result = await dialog.ShowDialog<bool?>(owner);
        return result == true;
    }

    private static Window NewWindow(string title, double width) => new()
    {
        Title = title,
        Width = width,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Background = Back,
    };

    private static Control Layout(string title, Control body, params Control[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (Control b in buttons) row.Children.Add(b);
        return new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Accent },
                body,
                row,
            },
        };
    }
}
