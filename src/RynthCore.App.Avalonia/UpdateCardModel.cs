using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RynthCore.App.Avalonia;

/// <summary>What the update card on the launcher's front page is showing.</summary>
public enum UpdateCardKind { NotConfigured, NotChecked, Checking, Error, UpToDate, CoreUpdate, PluginUpdates, Downloading, Held }

/// <summary>Everything the card needs to know, gathered by the window (no UI types, so tests can build it).</summary>
public sealed record UpdateCardInput
{
    public bool Configured { get; init; } = true;
    public bool Checking { get; init; }
    /// <summary>The last check's error; null when it worked.</summary>
    public string? Error { get; init; }
    /// <summary>When the last check succeeded (local time); null before the first.</summary>
    public DateTime? LastChecked { get; init; }
    /// <summary>True once a check has succeeded this session.</summary>
    public bool HasCheck { get; init; }
    public string InstalledVersion { get; init; } = "";
    public bool CoreUpdateAvailable { get; init; }
    public string AvailableVersion { get; init; } = "";
    public string Notes { get; init; } = "";
    public IReadOnlyList<string> Changes { get; init; } = Array.Empty<string>();
    /// <summary>Plugin updates (and new companions, the RynthNav map) waiting to be installed.</summary>
    public IReadOnlyList<string> PendingPluginUpdates { get; init; } = Array.Empty<string>();
    /// <summary>Available plugins with the New badge.</summary>
    public IReadOnlyList<string> NewPlugins { get; init; } = Array.Empty<string>();
    /// <summary>Game clients running RynthCore (a RynthCore update waits for them to close).</summary>
    public int ClientsRunning { get; init; }
    /// <summary>The download in progress, if any.</summary>
    public DownloadProgress? Download { get; init; }
    /// <summary>After Roll back: the version the player went back from, while updates are held (else "").</summary>
    public string HeldFromVersion { get; init; } = "";
    public bool CanCancel { get; init; }
}

/// <summary>The card's text and which buttons show.</summary>
public sealed record UpdateCard(
    UpdateCardKind Kind,
    string Title,
    string Detail,
    IReadOnlyList<string> WhatsNew,
    bool ShowCheckButton,
    string? ActionButton,
    bool ShowProgress,
    double? ProgressPercent,
    bool ShowCancel,
    string? NewPluginsLine,
    /// <summary>One line for the Plugins tab and the header.</summary>
    string Compact);

/// <summary>
/// The front page's update card as a pure function of <see cref="UpdateCardInput"/>: up to date,
/// RynthCore update (installs once every game client is closed), plugin updates, downloading
/// (bar, MB, speed, time left, step), errors, a line about new plugins, and the hold after a Roll back.
/// </summary>
public static class UpdateCardModel
{
    public const int MaxWhatsNew = 4;
    public const string UpdateCoreButton = "Update RynthCore";
    public const string UpdatePluginsButton = "Update plugins";

    public static UpdateCard Build(UpdateCardInput i)
    {
        string? newLine = i.NewPlugins.Count > 0
            ? $"New plugins available: {string.Join(", ", i.NewPlugins)}. See Available plugins on the Plugins tab."
            : null;
        string installed = i.InstalledVersion.Length > 0 ? i.InstalledVersion : "unknown";

        if (!i.Configured)
            return new UpdateCard(UpdateCardKind.NotConfigured, "Updates", "Updates aren't set up in this build.",
                Array.Empty<string>(), false, null, false, null, false, null, "Updates aren't set up in this build.");

        if (i.Download is { } d)
        {
            string title = d.Stage switch
            {
                DownloadStage.Downloading => $"Downloading {d.What}",
                DownloadStage.Verified => $"{d.What}: verified",
                DownloadStage.Unpacking => $"Unpacking {d.What}",
                _ => $"Installing {d.What}",
            };
            // Installing is the last step (files moving into place): it always runs to the end.
            bool cancel = i.CanCancel && d.Stage != DownloadStage.Installing;
            return new UpdateCard(UpdateCardKind.Downloading, title, d.Detail, Array.Empty<string>(), false, null,
                true, d.Percent, cancel, newLine, $"{title}: {d.Detail}");
        }

        if (i.Checking)
            return new UpdateCard(UpdateCardKind.Checking, "Checking for updates…", $"RynthCore {installed}",
                Array.Empty<string>(), false, null, false, null, false, newLine, "Checking for updates…");

        if (i.Error != null)
        {
            string when = i.LastChecked is { } t ? $" Last good check: {FormatWhen(t)}." : "";
            return new UpdateCard(UpdateCardKind.Error, "Couldn't check for updates", i.Error.TrimEnd('.', ' ') + "." + when,
                Array.Empty<string>(), true, null, false, null, false, newLine, $"Update check failed: {i.Error}");
        }

        if (!i.HasCheck)
            return new UpdateCard(UpdateCardKind.NotChecked, $"RynthCore {installed}", "Not checked for updates yet.",
                Array.Empty<string>(), true, null, false, null, false, newLine, "Not checked yet.");

        string checkedLine = i.LastChecked is { } lc ? $"Last checked {FormatWhen(lc)}." : "";
        IReadOnlyList<string> whatsNew = WhatsNew(i.Changes, i.Notes);

        if (i.HeldFromVersion.Length > 0)
        {
            // Roll back: the launcher doesn't push the release the player left straight back.
            var detail = new List<string> { $"You went back from {i.HeldFromVersion}. Updates are on hold until a newer release comes out." };
            if (i.CoreUpdateAvailable)
                detail.Add($"{UpdateCoreButton} goes forward to {i.AvailableVersion} now.");
            if (checkedLine.Length > 0) detail.Add(checkedLine);
            return new UpdateCard(UpdateCardKind.Held, $"RynthCore {installed} - rolled back", string.Join(" ", detail),
                Array.Empty<string>(), true, i.CoreUpdateAvailable ? UpdateCoreButton : null, false, null, false, newLine,
                $"RynthCore {installed} - rolled back from {i.HeldFromVersion}; updates on hold.");
        }

        if (i.CoreUpdateAvailable)
        {
            var detail = new List<string> { $"You have {installed}. The update installs once every game client is closed." };
            if (i.ClientsRunning > 0)
                detail.Add($"Close your {i.ClientsRunning} game client{(i.ClientsRunning == 1 ? "" : "s")} running RynthCore first.");
            if (i.PendingPluginUpdates.Count > 0)
                detail.Add($"Plugin updates ready too: {string.Join(", ", i.PendingPluginUpdates)}.");
            if (checkedLine.Length > 0) detail.Add(checkedLine);
            return new UpdateCard(UpdateCardKind.CoreUpdate, $"RynthCore {i.AvailableVersion} is available", string.Join(" ", detail),
                whatsNew, true, UpdateCoreButton, false, null, false, newLine, $"Update available: RynthCore {i.AvailableVersion}");
        }

        if (i.PendingPluginUpdates.Count > 0)
        {
            string detail = $"{string.Join(", ", i.PendingPluginUpdates)}. Running bots keep their version until AC restarts.";
            if (checkedLine.Length > 0) detail += " " + checkedLine;
            return new UpdateCard(UpdateCardKind.PluginUpdates, "Plugin updates ready", detail,
                whatsNew, true, UpdatePluginsButton, false, null, false, newLine, $"Plugin updates ready: {string.Join(", ", i.PendingPluginUpdates)}");
        }

        return new UpdateCard(UpdateCardKind.UpToDate, $"RynthCore {installed} - up to date", checkedLine,
            Array.Empty<string>(), true, null, false, null, false, newLine, $"RynthCore {installed} - up to date.");
    }

    /// <summary>A few of the release's changes; the notes when the feed has no list (feeds before 2026.10.5).</summary>
    public static IReadOnlyList<string> WhatsNew(IReadOnlyList<string> changes, string notes)
    {
        if (changes.Count > 0)
        {
            var list = changes.Take(MaxWhatsNew).ToList();
            if (changes.Count > MaxWhatsNew) list.Add($"…and {changes.Count - MaxWhatsNew} more.");
            return list;
        }
        if (string.IsNullOrWhiteSpace(notes)) return Array.Empty<string>();
        string n = notes.Trim();
        return new[] { n.Length > 300 ? n[..299].TrimEnd() + "…" : n };
    }

    /// <summary>"at 14:05" today, else "Oct 3 at 14:05".</summary>
    public static string FormatWhen(DateTime local, DateTime? now = null)
    {
        DateTime today = (now ?? DateTime.Now).Date;
        string time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        return local.Date == today ? $"at {time}" : $"{local.ToString("MMM d", CultureInfo.InvariantCulture)} at {time}";
    }
}
