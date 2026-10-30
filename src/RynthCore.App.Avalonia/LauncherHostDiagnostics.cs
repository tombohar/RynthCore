using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using RynthCore;

namespace RynthCore.App.Avalonia;

/// <summary>
/// One-shot host metadata for the Avalonia launcher: helps confirm the renamed apphost
/// (<c>RynthCore.exe</c>) sits next to matching <c>.runtimeconfig.json</c> / <c>.deps.json</c> after installer staging.
/// </summary>
internal static class LauncherHostDiagnostics
{
    /// <summary>Single line for the in-window activity feed (short).</summary>
    internal static string BuildActivitySummaryLine()
    {
        string exe = TryGetHostExePath();
        string dir = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory;
        HostSidecars side = DescribeSidecars(dir);
        // x64 FDD tool prereq: same registry keys as installer/RynthCore.iss (see NetDesktopX64Prerequisite).
        string suiteTools = NetDesktopX64Prerequisite.IsSatisfied()
            ? "x64 suite tools: .NET 10+ OK (registry, same as setup)"
            : "x64 suite tools: .NET 10+ not found — install x64 runtime from " + NetDesktopX64Prerequisite.DotNet10RuntimeDownloadUrl;
        if (!Environment.Is64BitOperatingSystem)
            suiteTools = "x64 suite tools: n/a on 32-bit OS (same as setup)";

        return
            $"Host: {RuntimeInformation.FrameworkDescription}, arch={RuntimeInformation.ProcessArchitecture}, " +
            $"runtimeconfig={(side.RenamedRuntimeConfig ? "RynthCore" : side.LegacyRuntimeConfig ? "App.Avalonia (mismatch?)" : "missing")}, " +
            $"deps={(side.RenamedDeps ? "RynthCore" : side.LegacyDeps ? "App.Avalonia (mismatch?)" : "missing")}; {suiteTools}";
    }

    /// <summary>Writes a detailed block to the desktop rolling launcher log.</summary>
    internal static void AppendDesktopLogBlock(string timestampIso)
    {
        try
        {
            string exe = TryGetHostExePath();
            string dir = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory;
            HostSidecars side = DescribeSidecars(dir);

            var sb = new StringBuilder(512);
            sb.Append('[').Append(timestampIso).Append("] [pid:").Append(Environment.ProcessId).AppendLine("] Launcher host resolution:");
            sb.Append("  ProcessPath: ").AppendLine(exe);
            sb.Append("  BaseDirectory: ").AppendLine(AppContext.BaseDirectory);
            sb.Append("  Framework: ").AppendLine(RuntimeInformation.FrameworkDescription);
            sb.Append("  OSDescription: ").AppendLine(RuntimeInformation.OSDescription);
            sb.Append("  ProcessArchitecture: ").AppendLine(RuntimeInformation.ProcessArchitecture.ToString());
            sb.Append("  CLR: ").AppendLine(Environment.Version.ToString());
            sb.Append("  RynthCore.runtimeconfig.json: ").AppendLine(side.RenamedRuntimeConfig ? "present" : "absent");
            sb.Append("  RynthCore.App.Avalonia.runtimeconfig.json: ").AppendLine(side.LegacyRuntimeConfig ? "present (unexpected if exe is RynthCore.exe)" : "absent");
            sb.Append("  RynthCore.deps.json: ").AppendLine(side.RenamedDeps ? "present" : "absent");
            sb.Append("  RynthCore.App.Avalonia.deps.json: ").AppendLine(side.LegacyDeps ? "present (unexpected if exe is RynthCore.exe)" : "absent");
            // Loot/Monster editors are x64 FDD: probe matches RynthCore.iss prerequisite page (64-bit registry view).
            sb.Append("  x64 FDD tool runtime (Loot/Monster, same checks as installer): ")
                .AppendLine(NetDesktopX64Prerequisite.IsSatisfied() ? "present" : "not detected")
                .Append("  .NET 10+ x64 download hub: ")
                .AppendLine(NetDesktopX64Prerequisite.DotNet10RuntimeDownloadUrl);

            DesktopRollingLog.AppendLine(DesktopRollingLog.StemLauncher, sb.ToString());
        }
        catch
        {
            // Logging must never prevent startup.
        }
    }

    private static string TryGetHostExePath()
    {
        try
        {
            // Prefer the actual apphost path (works for published rename to RynthCore.exe).
            string? p = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(p))
                return p;
        }
        catch
        {
            // ignore
        }

        try
        {
            return typeof(App).Assembly.Location;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static HostSidecars DescribeSidecars(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return new HostSidecars(false, false, false, false);

        bool renamedRc = File.Exists(Path.Combine(directory, "RynthCore.runtimeconfig.json"));
        bool legacyRc = File.Exists(Path.Combine(directory, "RynthCore.App.Avalonia.runtimeconfig.json"));
        bool renamedDeps = File.Exists(Path.Combine(directory, "RynthCore.deps.json"));
        bool legacyDeps = File.Exists(Path.Combine(directory, "RynthCore.App.Avalonia.deps.json"));
        return new HostSidecars(renamedRc, legacyRc, renamedDeps, legacyDeps);
    }

    private readonly record struct HostSidecars(
        bool RenamedRuntimeConfig,
        bool LegacyRuntimeConfig,
        bool RenamedDeps,
        bool LegacyDeps);
}
