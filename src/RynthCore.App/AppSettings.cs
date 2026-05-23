using System.Collections.Generic;

namespace RynthCore.App;

internal sealed class AppSettings
{
    public string AcClientPath { get; set; } = string.Empty;
    public string EnginePath { get; set; } = string.Empty;
    public string LaunchArguments { get; set; } = string.Empty;
    public string SelectedServerProfileId { get; set; } = string.Empty;
    public string SelectedAccountProfileId { get; set; } = string.Empty;
    public List<string> CheckedLaunchAccountProfileIds { get; set; } = [];
    public string SelectedMainTabId { get; set; } = "launcher";
    public bool AllowMultipleClients { get; set; }
    public bool SkipIntroVideos { get; set; }
    public bool SkipLoginLogos { get; set; }
    public bool AutoLaunch { get; set; }
    public bool AutoInjectAfterLaunch { get; set; } = true;
    public bool WatchForAcStart { get; set; } = true;
    public int LaunchStaggerMs { get; set; } = 250;
    public int CrashRelaunchLimitInWindow { get; set; } = 3;
    public int CrashRelaunchWindowMinutes { get; set; } = 5;
    public bool OverrideWindowTitle { get; set; } = true;

    /// When true, the launcher kills any RynthCore-mode acclient.exe it
    /// launched that hasn't reached IsLoggedIn within
    /// <see cref="StuckClientTimeoutSeconds"/>. Catches stuck char-select,
    /// patcher hangs, and "you have been disconnected" failure screens at
    /// the front of the launch flow. Off by default — destructive.
    /// Decal-mode clients are skipped (no engine = no IsLoggedIn signal).
    public bool KillStuckClients { get; set; }
    public int StuckClientTimeoutSeconds { get; set; } = 60;
    public List<string> EnabledPluginIds { get; set; } = [];
    public List<string> PluginDllPaths { get; set; } = [];

    /// User-added plugin DLL paths whose checkboxes are currently unchecked.
    /// The path stays in <see cref="PluginDllPaths"/> (so the row remains in
    /// the UI), but is filtered out when syncing PluginPaths into engine.json.
    /// Case-insensitive set semantics; comparisons must use OrdinalIgnoreCase.
    public List<string> DisabledPluginDllPaths { get; set; } = [];
    public List<LaunchServerProfile> ServerProfiles { get; set; } = [];
    public List<LaunchAccountProfile> AccountProfiles { get; set; } = [];

    public double? WindowX { get; set; }
    public double? WindowY { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
}
