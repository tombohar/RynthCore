using System;
using System.IO;
using Microsoft.Win32;

namespace RynthCore.App;

/// <summary>
/// Locates a local Decal install via the same registry probe ThwargLauncher
/// uses. Decal records its agent directory at HKLM\SOFTWARE\Decal\Agent (the
/// launcher is x86, so we transparently see the Wow6432Node redirect on 64-bit
/// Windows). Inject.dll lives directly in that directory and exports
/// DecalStartup, which is the entry point the suspended-launch injector calls.
/// </summary>
internal static class DecalLocator
{
    private const string AgentSubKey = @"SOFTWARE\Decal\Agent";
    private const string AgentPathValue = "AgentPath";
    private const string PortalPathValue = "PortalPath";
    private const string InjectDllName = "Inject.dll";
    private const string AcClientName = "acclient.exe";

    /// <summary>Returns the absolute path to Decal's Inject.dll, or null if
    /// Decal is not registered or the file cannot be found beside its agent
    /// directory.</summary>
    public static string? TryGetInjectDllPath()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(AgentSubKey);
            if (key == null)
                return null;

            string? agentPath = key.GetValue(AgentPathValue) as string;
            if (string.IsNullOrWhiteSpace(agentPath))
                return null;

            string injectPath = Path.Combine(agentPath, InjectDllName);
            return File.Exists(injectPath) ? injectPath : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Returns the AC client install Decal expects, derived from its
    /// PortalPath registry value (e.g. C:\Turbine\Asheron's Call). Used when an
    /// account is set to Decal mode and the launcher's global AcClientPath
    /// points elsewhere (e.g. the RynthCore-private duplicate). Returns null if
    /// not registered or the file is missing.</summary>
    public static string? TryGetDecalAcClientPath()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(AgentSubKey);
            if (key == null)
                return null;

            string? portalPath = key.GetValue(PortalPathValue) as string;
            if (string.IsNullOrWhiteSpace(portalPath))
                return null;

            string acPath = Path.Combine(portalPath, AcClientName);
            return File.Exists(acPath) ? acPath : null;
        }
        catch
        {
            return null;
        }
    }

    public static bool IsInstalled() => TryGetInjectDllPath() != null;
}
