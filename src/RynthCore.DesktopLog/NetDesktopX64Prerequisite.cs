// Detects the same "Suite tools" .NET 10+ x64 prerequisite as the Inno installer
// (installer\RynthCore.iss, IsNetDesktop* helpers). If you change registry checks here,
// update RynthCore.iss in lockstep and vice versa.

using System;
using System.Globalization;
using System.Linq;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace RynthCore;

/// <summary>
/// Registry probe for the optional x64 framework-dependent tools (Loot Editor, Monster Editor).
/// Must match <c>IsNetDesktop10PlusX64Present</c> and its helpers in <c>installer/RynthCore.iss</c>
/// (Path A: <c>Microsoft.WindowsDesktop.App</c> shared folders; Path B: <c>sharedhost</c> Version;
/// Path C: <c>InstalledManifests\x64</c> manifest-10. bands). Uses 64-bit registry view from an x86 process.
/// </summary>
[SupportedOSPlatform("windows")]
public static class NetDesktopX64Prerequisite
{
    // Same string as RynthCore.iss DotNet10DesktopDownloadUrl (x64 runtime hub; user picks Desktop / ASP.NET as needed).
    public const string DotNet10RuntimeDownloadUrl = "https://dotnet.microsoft.com/en-us/download/dotnet/10.0/runtime";

    /// <summary>True if the x64 FDD tool prerequisite is met, or the OS is 32-bit (N/A, same as Inno skip).</summary>
    public static bool IsSatisfied()
    {
        if (!Environment.Is64BitOperatingSystem)
            return true;

        return
            IsWindowsDesktopSharedFramework10Plus()
            || IsDotNet10BandInInstalledManifestsX64()
            || IsSharedHostVersion10PlusX64();
    }

    // Path A: HKLM\SOFTWARE\dotnet\shared\Microsoft.WindowsDesktop.App\10.x.y
    private static bool IsWindowsDesktopSharedFramework10Plus()
    {
        using RegistryKey? base64 = OpenLocalMachine64();
        using RegistryKey? key = base64?.OpenSubKey(@"SOFTWARE\dotnet\shared\Microsoft.WindowsDesktop.App");
        if (key is null)
            return false;

        foreach (string name in key.GetSubKeyNames())
        {
            if (ParseMajorFromVersionFolder(name) >= 10)
                return true;
        }

        return false;
    }

    // Path B: sharedhost Version 10+ under Setup\InstalledVersions\x64\sharedhost
    private static bool IsSharedHostVersion10PlusX64()
    {
        if (ReadSharedHostMajor(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedhost") >= 10)
            return true;
        if (ReadSharedHostMajor(@"SOFTWARE\Microsoft\dotnet\Setup\InstalledVersions\x64\sharedhost") >= 10)
            return true;
        return false;
    }

    private static int ReadSharedHostMajor(string subKeyPath)
    {
        using RegistryKey? base64 = OpenLocalMachine64();
        using RegistryKey? key = base64?.OpenSubKey(subKeyPath);
        if (key is null)
            return 0;
        string? v = key.GetValue("Version") as string;
        if (string.IsNullOrWhiteSpace(v))
            return 0;
        return ParseMajorFromVersionFolder(v.Trim());
    }

    // Path C: InstalledManifests x64, subkeys like ...Manifest-10.0.100
    private static bool IsDotNet10BandInInstalledManifestsX64()
    {
        using RegistryKey? base64 = OpenLocalMachine64();
        using RegistryKey? key = base64?.OpenSubKey(@"SOFTWARE\Microsoft\dotnet\InstalledManifests\x64");
        if (key is null)
            return false;

        return key.GetSubKeyNames().Any(s => s != null
            && s.IndexOf("Manifest-10.", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static RegistryKey? OpenLocalMachine64() =>
        RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    /// <summary>First numeric segment of a "10.0.5" style folder or version string (Pascal: Copy before first '.').</summary>
    private static int ParseMajorFromVersionFolder(string name)
    {
        int dot = name.IndexOf('.');
        if (dot <= 0)
            return 0;
        return int.TryParse(
            name.AsSpan(0, dot),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int m)
            ? m
            : 0;
    }
}
