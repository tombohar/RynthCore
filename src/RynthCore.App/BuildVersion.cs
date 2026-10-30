using System.Diagnostics;
using System.IO;

namespace RynthCore.App;

/// <summary>
/// Human-readable build versions, for the launcher and the in-game UI.
///
/// Release builds are stamped with the release number (Build-Installer.ps1 and
/// Publish-Update.ps1 pass -p:Version=yyyy.m.d.n), and the .NET SDK appends the git
/// commit to the product version: "2026.9.26.3+8ca322f…". Anything built without a
/// version (Deploy-RynthCore.ps1, an IDE build) reads "1.0.0+8ca322f…" — a dev build.
/// NativeAOT DLLs carry the same Windows version resource, so plugins need no code.
/// </summary>
internal static class BuildVersion
{
    /// <summary>"2026.9.26.3 (8ca322f)" for a release, "dev (8ca322f)" otherwise.</summary>
    public static string Format(string? productVersion)
    {
        if (string.IsNullOrWhiteSpace(productVersion)) return "unknown";
        string version = productVersion.Trim();
        string commit = "";
        int plus = version.IndexOf('+');
        if (plus >= 0)
        {
            commit = version[(plus + 1)..];
            version = version[..plus];
        }
        if (commit.Length > 7) commit = commit[..7];
        if (version is "1.0.0" or "1.0.0.0") version = "dev";
        return commit.Length > 0 ? $"{version} ({commit})" : version;
    }

    /// <summary>A DLL's or EXE's version resource, formatted; "" if it has none or can't be read.</summary>
    public static string OfFile(string path)
    {
        try
        {
            return File.Exists(path) ? Format(FileVersionInfo.GetVersionInfo(path).ProductVersion) : "";
        }
        catch
        {
            return "";
        }
    }
}
