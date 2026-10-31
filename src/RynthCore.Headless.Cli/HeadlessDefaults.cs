// ============================================================================
//  rynth-headless - HeadlessDefaults.cs
//  Where an installed rynth-headless keeps its files when the run config (or the
//  launcher's HeadlessSuiteDir / HeadlessStatusDir) names no folder.
//
//  Suite folder: %LOCALAPPDATA%\RynthCore\headless. It becomes RYNTHSUITE_DIR, so the
//    headless plugins' data (RynthAi\ profiles, metas, loot profiles, logs) and the
//    headless-<account>.log files live there. Per Windows user (the launcher's saved
//    passwords are per user too), always writable without administrator rights, outside
//    the install folder (an update or uninstall never touches it), and apart from the
//    game clients' C:\Games\RynthSuite, so a headless RynthAi never writes the files a
//    RynthAi in a game client is using.
//  Status folder: <RynthCore>\Logs\status (C:\Games\RynthCore\Logs\status unless
//    RYNTHCORE_DIR or the registry says otherwise), the folder the status files of the
//    game clients use and the StatusAgent reads by default.
// ============================================================================

using RynthCore.Install;

namespace RynthCore.Headless.Cli;

internal static class HeadlessDefaults
{
    public static string SuiteDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RynthCore", "headless");

    public static string StatusDir => Path.Combine(RynthInstallPaths.CoreLogsDir, "status");
}
