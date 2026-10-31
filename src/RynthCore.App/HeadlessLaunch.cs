using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace RynthCore.App;

/// <summary>One character of a headless run config (one "sessions" entry).</summary>
internal sealed class HeadlessSessionSpec
{
    /// <summary>LaunchAccountProfile.Id: rynth-headless --launcher reads that profile's saved password.</summary>
    public string LauncherProfileId { get; set; } = string.Empty;
    public string Account { get; set; } = string.Empty;
    /// <summary>The character to log in; null/empty = rynth-headless picks.</summary>
    public string? Character { get; set; }
    /// <summary>Chat commands sent after login (the account's OnLogin commands for that character).</summary>
    public List<string> Commands { get; set; } = new();
    /// <summary>0 = stay until stopped.</summary>
    public int StaySeconds { get; set; }
    public bool Reconnect { get; set; } = true;
}

/// <summary>What one rynth-headless process runs: the server, the plugins and its characters.</summary>
internal sealed class HeadlessRunSpec
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    /// <summary>The launcher's server profile Name (what session files and the account keys use).</summary>
    public string ServerName { get; set; } = string.Empty;
    /// <summary>Empty = left out, so rynth-headless uses its default.</summary>
    public string SuiteDir { get; set; } = string.Empty;
    /// <summary>Empty = left out, so rynth-headless uses its default.</summary>
    public string StatusDir { get; set; } = string.Empty;
    public List<string> Plugins { get; set; } = new();
    public List<HeadlessSessionSpec> Sessions { get; set; } = new();
}

/// <summary>
/// The launcher side of <see cref="InjectionMode.Headless"/>, without the UI: where
/// rynth-headless.exe is, the run config the launcher writes for an account, its command line,
/// and the stop channel. BCL only: rynth-headless (src\RynthCore.Headless.Cli) compiles this file
/// too, so both sides agree on the stop event's name.
///
/// The launcher runs one rynth-headless process per Headless account:
///   rynth-headless.exe run --config %APPDATA%\RynthCore\headless\launch_&lt;profile id&gt;.json
///                          --launcher [--ac-folder &lt;AC folder&gt;]
/// --launcher: the process writes sessions\session_&lt;its pid&gt;.json and the character cache as the
/// engine does, and reads the account's password from appsettings.json by launcherProfileId (the
/// config never holds a password). Stopping: <see cref="TrySignalStop"/>, then a kill after
/// <see cref="StopGrace"/> if it is still running.
/// </summary>
internal static class HeadlessLaunch
{
    /// <summary>Process name (Process.GetProcessesByName) of the headless client.</summary>
    public const string ProcessName = "rynth-headless";
    public const string ExeName = ProcessName + ".exe";

    /// <summary>Default install folder of rynth-headless.exe, beside the launcher.</summary>
    public const string DefaultFolderName = "Headless";

    /// <summary>Folder under the launcher's settings folder holding the run configs.</summary>
    public const string ConfigFolderName = "headless";

    /// <summary>
    /// Folder under the Headless folder with the 64-bit plugin builds the installer ships, one folder
    /// per plugin with its dependencies: Headless\plugins\&lt;Name&gt;\RynthCore.Plugin.&lt;Name&gt;.dll.
    /// </summary>
    public const string PluginsFolderName = "plugins";

    /// <summary>How long a stop request may take (every character logs out) before the launcher kills the process.</summary>
    public static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Name of the stop event of rynth-headless process <paramref name="pid"/> (a manual-reset
    /// EventWaitHandle the process creates). Set = every character logs out cleanly, then it exits.
    /// </summary>
    public static string StopEventName(int pid) => $@"Local\RynthHeadless.Stop.{pid}";

    /// <summary>
    /// Asks rynth-headless process <paramref name="pid"/> to log out and exit. False when it has no
    /// stop event (not a rynth-headless, too old, still starting, or gone): the caller kills it then.
    /// </summary>
    public static bool TrySignalStop(int pid)
    {
        if (!OperatingSystem.IsWindows())
            return false;
        try
        {
            if (!EventWaitHandle.TryOpenExisting(StopEventName(pid), out EventWaitHandle? stop))
                return false;
            using (stop)
                return stop.Set();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// rynth-headless.exe: <paramref name="configured"/> (AppSettings.HeadlessExePath, environment
    /// variables expanded) when set, else &lt;<paramref name="launcherDirectory"/>&gt;\Headless\rynth-headless.exe.
    /// </summary>
    public static string ResolveExePath(string? configured, string launcherDirectory)
    {
        string path = (configured ?? string.Empty).Trim().Trim('"');
        if (path.Length > 0)
            return Environment.ExpandEnvironmentVariables(path);
        return Path.Combine(launcherDirectory, DefaultFolderName, ExeName);
    }

    /// <summary>
    /// The shipped 64-bit plugins: every &lt;<paramref name="launcherDirectory"/>&gt;\Headless\plugins\&lt;Name&gt;\RynthCore.Plugin.&lt;Name&gt;.dll
    /// that exists, by name. Empty when the install has none (an older install, a dev launcher).
    /// </summary>
    public static List<string> DefaultPluginPaths(string launcherDirectory)
    {
        var found = new List<string>();
        try
        {
            string root = Path.Combine(launcherDirectory, DefaultFolderName, PluginsFolderName);
            if (!Directory.Exists(root))
                return found;
            foreach (string dir in Directory.GetDirectories(root))
            {
                string dll = Path.Combine(dir, $"RynthCore.Plugin.{Path.GetFileName(dir)}.dll");
                if (File.Exists(dll))
                    found.Add(dll);
            }
        }
        catch
        {
            // Unreadable folder: no default plugins.
        }
        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
    }

    /// <summary>
    /// The plugins a Headless account loads: <paramref name="configured"/> (AppSettings.HeadlessPluginPaths,
    /// blank entries dropped) when it names any, else the shipped ones (<see cref="DefaultPluginPaths"/>).
    /// </summary>
    public static List<string> ResolvePluginPaths(IEnumerable<string>? configured, string launcherDirectory)
    {
        var list = new List<string>();
        foreach (string entry in configured ?? Array.Empty<string>())
        {
            string path = (entry ?? string.Empty).Trim().Trim('"').Trim();
            if (path.Length > 0)
                list.Add(path);
        }
        return list.Count > 0 ? list : DefaultPluginPaths(launcherDirectory);
    }

    /// <summary>The run config for account profile <paramref name="profileId"/>, under <paramref name="settingsDirectory"/>\headless.</summary>
    public static string ConfigPath(string settingsDirectory, string profileId)
    {
        var name = new StringBuilder(profileId.Length);
        foreach (char c in profileId)
            name.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
        if (name.Length == 0)
            name.Append("account");
        return Path.Combine(settingsDirectory, ConfigFolderName, $"launch_{name}.json");
    }

    /// <summary>
    /// The --ac-folder value: the folder of the AC client path, or null when none is set (the
    /// argument is left out then).
    /// </summary>
    public static string? AcFolderFor(string? acClientPath)
    {
        string path = (acClientPath ?? string.Empty).Trim().Trim('"');
        if (path.Length == 0)
            return null;
        string? dir = Path.GetDirectoryName(path);
        return string.IsNullOrWhiteSpace(dir) ? null : dir;
    }

    /// <summary>
    /// The run config as rynth-headless reads it (camelCase JSON). There is no password field:
    /// with --launcher the process reads it from appsettings.json by launcherProfileId.
    /// </summary>
    public static string BuildConfigJson(HeadlessRunSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("host", spec.Host ?? string.Empty);
            w.WriteNumber("port", spec.Port);
            w.WriteString("serverName", spec.ServerName ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(spec.SuiteDir))
                w.WriteString("suiteDir", spec.SuiteDir.Trim());
            if (!string.IsNullOrWhiteSpace(spec.StatusDir))
                w.WriteString("statusDir", spec.StatusDir.Trim());
            w.WriteStartArray("plugins");
            foreach (string plugin in spec.Plugins ?? new List<string>())
                if (!string.IsNullOrWhiteSpace(plugin))
                    w.WriteStringValue(plugin.Trim());
            w.WriteEndArray();
            w.WriteStartArray("sessions");
            foreach (HeadlessSessionSpec s in spec.Sessions ?? new List<HeadlessSessionSpec>())
            {
                w.WriteStartObject();
                w.WriteString("launcherProfileId", s.LauncherProfileId ?? string.Empty);
                w.WriteString("account", s.Account ?? string.Empty);
                if (string.IsNullOrWhiteSpace(s.Character))
                    w.WriteNull("character");
                else
                    w.WriteString("character", s.Character.Trim());
                w.WriteStartArray("commands");
                foreach (string command in s.Commands ?? new List<string>())
                    if (!string.IsNullOrWhiteSpace(command))
                        w.WriteStringValue(command);
                w.WriteEndArray();
                w.WriteNumber("staySeconds", Math.Max(0, s.StaySeconds));
                w.WriteBoolean("reconnect", s.Reconnect);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Writes <paramref name="configJson"/> to <paramref name="path"/> (UTF-8, no BOM; temp file + swap).</summary>
    public static void WriteConfig(string path, string configJson)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, configJson, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// rynth-headless's arguments, one per entry (for ProcessStartInfo.ArgumentList, which quotes
    /// them): run --config FILE --launcher [--ac-folder DIR].
    /// </summary>
    public static List<string> BuildArguments(string configPath, string? acFolder)
    {
        var args = new List<string> { "run", "--config", configPath, "--launcher" };
        if (!string.IsNullOrWhiteSpace(acFolder))
        {
            args.Add("--ac-folder");
            args.Add(acFolder);
        }
        return args;
    }
}
