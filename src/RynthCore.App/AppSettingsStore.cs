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
    private static string BackupPath => SettingsPath + ".bak";

    /// <summary>
    /// Set when the last Load() hit a corrupt/unreadable file (quarantined it,
    /// possibly recovered from the .bak). Null when the load was clean. The
    /// launcher surfaces this after startup — this store is shared source and
    /// must not depend on launcher-only logging.
    /// </summary>
    public static string? LastLoadDiagnostic { get; private set; }

    public static AppSettings Load()
    {
        LastLoadDiagnostic = null;

        if (!File.Exists(SettingsPath))
            return new AppSettings();

        if (TryLoadFrom(SettingsPath, out AppSettings? settings))
            return settings!;

        // The primary file is corrupt (truncated write, disk issue). This file
        // holds every server/account profile — NEVER silently reset to defaults.
        // Quarantine the bad file for post-mortem, then try the backup the
        // atomic Save() keeps.
        string quarantine = SettingsPath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        try { File.Move(SettingsPath, quarantine); }
        catch { quarantine = "(quarantine move failed)"; }

        if (File.Exists(BackupPath) && TryLoadFrom(BackupPath, out AppSettings? backup))
        {
            try { File.Copy(BackupPath, SettingsPath, overwrite: true); } catch { }
            LastLoadDiagnostic = $"appsettings.json was corrupt — recovered from .bak (bad file kept at {quarantine}).";
            return backup!;
        }

        LastLoadDiagnostic = $"appsettings.json was corrupt and no usable .bak exists — starting with defaults (bad file kept at {quarantine}).";
        return new AppSettings();
    }

    private static bool TryLoadFrom(string path, out AppSettings? settings)
    {
        settings = null;
        try
        {
            string json = File.ReadAllText(path);
            AppSettings loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();

            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty(nameof(AppSettings.AutoLaunch), out _) &&
                root.TryGetProperty("AutoRelaunch", out JsonElement legacyAutoRelaunch) &&
                legacyAutoRelaunch.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                loaded.AutoLaunch = legacyAutoRelaunch.GetBoolean();
            }

            settings = loaded;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Save(AppSettings settings)
    {
        // Atomic write: serialize to a temp file, then swap it in. A crash or
        // power cut mid-write can no longer truncate the live file (which holds
        // every server/account profile and is rewritten constantly); the swap
        // also maintains a .bak Load() can recover from.
        Directory.CreateDirectory(SettingsDirectory);
        string tmp = SettingsPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
        if (File.Exists(SettingsPath))
            File.Replace(tmp, SettingsPath, BackupPath);
        else
            File.Move(tmp, SettingsPath);
    }
}
