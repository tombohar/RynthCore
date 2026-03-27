using System.Collections.Generic;

namespace NexCore.App;

internal sealed class AppSettings
{
    public string AcClientPath { get; set; } = string.Empty;
    public string EnginePath { get; set; } = string.Empty;
    public string LaunchArguments { get; set; } = string.Empty;
    public bool AutoInjectAfterLaunch { get; set; } = true;
    public bool WatchForAcStart { get; set; } = true;
    public List<string> EnabledPluginIds { get; set; } = [];
}
