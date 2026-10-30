using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace RynthCore.App.Avalonia;

/// <summary>
/// The one-time "Help improve RynthCore?" question and the "Show what is sent" window.
/// Plain words; "No thanks" is the safe answer (Escape and closing the window both mean no).
/// </summary>
internal static class UsageStatsDialog
{
    private static readonly IBrush Back = new SolidColorBrush(Color.Parse("#132028"));
    private static readonly IBrush Text = new SolidColorBrush(Color.Parse("#EAF0F4"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#9AA8B3"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#26C1A6"));

    public const string WhatIsSent =
        "Sent once a day, only if you turn it on: a random ID made just for this, the RynthCore version, " +
        "which RynthSuite plugins you have enabled and their versions (other plugins only as a number), " +
        "how many times RynthCore crashed since the last report (a number), and whether you run Windows 10 or 11.";

    public const string NeverSent =
        "Never sent: character, account or server names, file paths, or anything else about you or your game. " +
        "It goes to aelrynth.com only, never to a game server.";

    public const string HowToStop =
        "Turning it off stops the reports at once and deletes the random ID from this PC.";

    /// <summary>True when the player chose to turn statistics on.</summary>
    public static async Task<bool> AskAsync(Window owner)
    {
        var dialog = NewWindow("Help improve RynthCore?", 560);
        var on = new Button { Content = "Turn it on" };
        var off = new Button { Content = "No thanks", IsCancel = true };
        on.Click += (_, _) => dialog.Close(true);
        off.Click += (_, _) => dialog.Close(false);
        dialog.Content = Layout("Help improve RynthCore?",
            new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    Para("RynthCore can send a small anonymous report once a day, so we can see how many people use it, " +
                         "which versions and plugins are in use, and whether a new release crashes more than the last one.", Text),
                    Para(WhatIsSent, Muted),
                    Para(NeverSent, Muted),
                    Para("It stays off unless you turn it on. You can change your mind any time on the Plugins tab, " +
                         "under Usage statistics. " + HowToStop, Muted),
                },
            },
            on, off);
        bool? result = await dialog.ShowDialog<bool?>(owner);
        return result == true;
    }

    /// <summary>Shows the report exactly as it would be sent now.</summary>
    public static async Task ShowPayloadAsync(Window owner, string json, bool enabled)
    {
        var dialog = NewWindow("Usage statistics: what is sent", 620);
        var body = new TextBox
        {
            Text = json,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
            FontSize = 12,
            MaxHeight = 380,
            Foreground = Text,
            Background = new SolidColorBrush(Color.Parse("#0F161D")),
        };
        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true };
        close.Click += (_, _) => dialog.Close(null);
        dialog.Content = Layout("Usage statistics: what is sent",
            new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    Para(enabled
                        ? "This is today's report, exactly as it goes to aelrynth.com (at most once a day)."
                        : "Statistics are off, so nothing is sent. If you turned them on, this is what would go out.", Muted),
                    body,
                },
            },
            close);
        await dialog.ShowDialog<object?>(owner);
    }

    private static TextBlock Para(string text, IBrush brush) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = brush };

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
