using System.Collections.Generic;

namespace RynthCore.App;

internal sealed class AppSettings
{
    // A new install starts with the standard client folder (2026-10-14); a saved path always wins.
    public string AcClientPath { get; set; } = DefaultAcClientPath;
    public const string DefaultAcClientPath = @"C:\Turbine\Asheron's Call\acclient.exe";
    public string EnginePath { get; set; } = string.Empty;
    public string LaunchArguments { get; set; } = string.Empty;
    public string SelectedServerProfileId { get; set; } = string.Empty;
    public string SelectedAccountProfileId { get; set; } = string.Empty;
    public List<string> CheckedLaunchAccountProfileIds { get; set; } = [];
    public string SelectedMainTabId { get; set; } = "launcher";
    // New-install defaults (2026-10-14): multiple clients, no intro videos or logos, inject after
    // launch, kill stuck clients; leave clients launched outside RynthCore alone.
    public bool AllowMultipleClients { get; set; } = true;
    public bool SkipIntroVideos { get; set; } = true;
    public bool SkipLoginLogos { get; set; } = true;
    public bool AutoLaunch { get; set; }
    public bool AutoInjectAfterLaunch { get; set; } = true;
    public bool WatchForAcStart { get; set; }
    public bool InjectAllRunningClients { get; set; }
    public string LoggingLevel { get; set; } = "Info";
    public int LaunchStaggerMs { get; set; } = 250;
    public int CrashRelaunchLimitInWindow { get; set; } = 3;
    public int CrashRelaunchWindowMinutes { get; set; } = 5;
    public bool OverrideWindowTitle { get; set; } = true;

    /// When true (default), the launcher installs RynthSuite plugin updates as soon as a
    /// check finds them. Safe mid-session: running clients keep the version they started
    /// with; the new one loads on the next AC start. Core updates always ask first.
    public bool AutoUpdatePlugins { get; set; } = true;

    /// "Help improve RynthCore": one anonymous usage report a day (see the launcher's
    /// UsageStats.cs for exactly what is sent). OFF unless the player turns it on.
    public bool UsageStatsEnabled { get; set; }

    /// True once the launcher has explained usage statistics and asked (shown once).
    public bool UsageStatsAsked { get; set; }

    /// The player said "leave it" (or no at Windows' prompt) when the launcher offered to take
    /// the RynthCore Decal bridge out of Decal's list for all users because no account uses
    /// Decal + RynthCore any more. The launcher then doesn't ask again at start (switching the
    /// last account away still asks). Cleared when the bridge is registered again.
    public bool DecalFilterRemovalDeclined { get; set; }

    /// When true, the launcher kills any RynthCore-mode acclient.exe it
    /// launched that hasn't reached IsLoggedIn within
    /// <see cref="StuckClientTimeoutSeconds"/>. Catches stuck char-select,
    /// patcher hangs, and "you have been disconnected" failure screens at
    /// the front of the launch flow. Off by default — destructive.
    /// Decal-mode clients are skipped (no engine = no IsLoggedIn signal).
    public bool KillStuckClients { get; set; } = true;
    public int StuckClientTimeoutSeconds { get; set; } = 60;

    /// When true (default), the launcher kills+relaunches a RynthCore-mode
    /// client whose per-PID heartbeat shows the WEDGE signature: login=1 and
    /// fps=0 sustained for <see cref="WedgeRestartSeconds"/> while the engine
    /// heartbeat keeps flowing (AC main thread dead, process alive — the AV
    /// class). Containment for the residual corruption AVs: an overnight soak
    /// loses ~2 minutes to a wedge instead of the rest of the night.
    /// Minimized windows are exempt (some drivers stop presenting). Relaunch
    /// rides the existing crash-relaunch machinery (counts toward the
    /// circuit breaker) and needs AutoLaunch for the relaunch half.
    public bool AutoRestartWedgedClients { get; set; } = true;
    public int WedgeRestartSeconds { get; set; } = 90;
    public List<string> EnabledPluginIds { get; set; } = [];
    public List<string> PluginDllPaths { get; set; } = [];

    /// User-added plugin DLL paths whose checkboxes are currently unchecked.
    /// The path stays in <see cref="PluginDllPaths"/> (so the row remains in
    /// the UI), but is filtered out when syncing PluginPaths into engine.json.
    /// Case-insensitive set semantics; comparisons must use OrdinalIgnoreCase.
    public List<string> DisabledPluginDllPaths { get; set; } = [];

    /// <summary>
    /// Last installer plugin hand-off (registry <c>Software\Rynth\PendingPluginRegistration</c>)
    /// the launcher applied. Lets an all-users install, whose HKLM value the launcher cannot
    /// delete, register its plugins once without re-adding a plugin the user later removed.
    /// </summary>
    public string? AppliedInstallerPluginRegistration { get; set; }

    public List<LaunchServerProfile> ServerProfiles { get; set; } = [];
    public List<LaunchAccountProfile> AccountProfiles { get; set; } = [];

    public double? WindowX { get; set; }
    public double? WindowY { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
}
