using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Media;

namespace RynthCore.App.Avalonia;

/// <summary>
/// Roll back's choice (RynthUpdater.Rollback.cs): the earlier releases whose signed manifests verified,
/// newest first, each with its date and first changes. Closes with the chosen target, or null.
/// </summary>
internal partial class RollbackDialog : Window
{
    private const int ChangesShown = 3;
    private RynthUpdater.RollbackTarget? _chosen;

    public RollbackDialog() : this(new List<RynthUpdater.RollbackTarget>(), "") { }   // the XAML previewer

    public RollbackDialog(IReadOnlyList<RynthUpdater.RollbackTarget> targets, string installedVersion)
    {
        InitializeComponent();
        HeaderText.Text = $"Go back from RynthCore {installedVersion}";
        HoldText.Text = "Every game client must be closed. The launcher installs that release and restarts; " +
                        "updates then wait until a release newer than this one comes out, or until you press Update RynthCore.";

        foreach (RynthUpdater.RollbackTarget t in targets)
        {
            var choice = new RadioButton { GroupName = "release", Tag = t };
            var body = new StackPanel { Spacing = 2 };
            body.Children.Add(new TextBlock
            {
                Text = $"RynthCore {t.Version}  ·  {t.Manifest.PublishedUtc.ToLocalTime():MMM d}",
                Foreground = Brush.Parse("#EAF0F4"), FontWeight = FontWeight.SemiBold,
            });
            IReadOnlyList<string> lines = t.Manifest.Changes.Count > 0
                ? t.Manifest.Changes.Take(ChangesShown).ToList()
                : (t.Manifest.Notes.Length > 0 ? new[] { t.Manifest.Notes } : new string[0]);
            foreach (string line in lines)
                body.Children.Add(new TextBlock
                {
                    Text = "• " + (line.Length > 160 ? line[..159].TrimEnd() + "…" : line),
                    Foreground = Brush.Parse("#C5D0D8"), TextWrapping = TextWrapping.Wrap,
                });
            if (t.Manifest.Changes.Count > ChangesShown)
                body.Children.Add(new TextBlock { Text = $"…and {t.Manifest.Changes.Count - ChangesShown} more.", Foreground = Brush.Parse("#9AA8B3") });
            choice.Content = body;
            choice.IsCheckedChanged += (_, _) =>
            {
                if (choice.IsChecked != true) return;
                _chosen = (RynthUpdater.RollbackTarget)choice.Tag!;
                RollbackButton.Content = $"Go back to {_chosen.Version}";
                RollbackButton.IsEnabled = true;
            };
            ReleasesPanel.Children.Add(choice);
        }

        CancelButton.Click += (_, _) => Close(null);
        RollbackButton.Click += (_, _) => Close(_chosen);
    }
}
