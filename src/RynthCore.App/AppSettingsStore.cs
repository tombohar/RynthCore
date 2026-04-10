using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RynthCore.App;

internal static class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    private static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RynthCore");

    private static string SettingsPath => Path.Combine(SettingsDirectory, "appsettings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new AppSettings();

            string json = File.ReadAllText(SettingsPath);
            AppSettings settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();

            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty(nameof(AppSettings.AutoLaunch), out _) &&
                root.TryGetProperty("AutoRelaunch", out JsonElement legacyAutoRelaunch) &&
                legacyAutoRelaunch.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                settings.AutoLaunch = legacyAutoRelaunch.GetBoolean();
            }

            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }
}
