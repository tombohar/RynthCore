using System;
using System.Runtime.InteropServices;
using RynthCore.Install;

namespace RynthCore.App.Avalonia;

/// <summary>
/// One-shot values the installer leaves under <c>Software\Rynth</c> for the launcher
/// (e.g. <c>PendingPluginRegistration</c>). Kept out of the shared, read-only
/// <see cref="RynthInstallPaths"/>, which must stay byte-identical with RynthSuite's copy.
/// </summary>
internal static class InstallerHandoff
{
    /// <summary>A string value from <c>Software\Rynth</c>: HKCU first, then HKLM (32-bit view); null when absent.</summary>
    public static string? ReadSetting(string valueName)
        => SystemInstallSettings.Instance.CurrentUser(valueName)
           ?? SystemInstallSettings.Instance.LocalMachine(valueName);

    /// <summary>Deletes a value from HKCU <c>Software\Rynth</c> once it has been acted on. Best effort.</summary>
    public static void DeleteUserSetting(string valueName)
    {
        try { RegDeleteKeyValueW(HKEY_CURRENT_USER, RynthInstallPaths.RegistryKey, valueName); } catch { }
    }

    private static readonly IntPtr HKEY_CURRENT_USER = new(unchecked((int)0x80000001));

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegDeleteKeyValueW(IntPtr hkey, string lpSubKey, string lpValueName);
}
