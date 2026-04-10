using System;
using System.IO;
using System.Text.Json;

namespace RynthCore.Engine.Compatibility;

internal static class LauncherSettings
{
    private static readonly object Sync = new();
    private static bool _loaded;
    private static bool _allowMultipleClients;
    private static string _statusMessage = "Launcher settings not loaded.";

    public static bool AllowMultipleClientsEnabled
    {
        get
        {
            EnsureLoaded();
            return _allowMultipleClients;
        }
    }

    public static string StatusMessage
    {
        get
        {
            EnsureLoaded();
            return _statusMessage;
        }
    }

    private static void EnsureLoaded()
    {
        lock (Sync)
        {
            if (_loaded)
                return;

            _loaded = true;

            try
            {
                string settingsPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "RynthCore",
                    "appsettings.json");

                if (!File.Exists(settingsPath))
                {
                    _statusMessage = $"Launcher settings file not found ({settingsPath}).";
                    return;
                }

                using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(settingsPath));
                if (!document.RootElement.TryGetProperty("AllowMultipleClients", out JsonElement property) ||
                    (property.ValueKind != JsonValueKind.True && property.ValueKind != JsonValueKind.False))
                {
                    _statusMessage = "AllowMultipleClients not present in launcher settings.";
                    return;
                }

                _allowMultipleClients = property.GetBoolean();
                _statusMessage = _allowMultipleClients
                    ? "AllowMultipleClients enabled in launcher settings."
                    : "AllowMultipleClients disabled in launcher settings.";
            }
            catch (Exception ex)
            {
                _statusMessage = $"Failed to read launcher settings - {ex.Message}";
            }
        }
    }
}
