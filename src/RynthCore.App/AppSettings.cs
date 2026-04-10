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
    public List<string> EnabledPluginIds { get; set; } = [];
    public List<LaunchServerProfile> ServerProfiles { get; set; } = [];
    public List<LaunchAccountProfile> AccountProfiles { get; set; } = [];

    public double? WindowX { get; set; }
    public double? WindowY { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
}
