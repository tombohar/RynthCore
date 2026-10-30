using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            // Before the general enum converter: InjectionMode is written so that every
            // launcher can read it ("Decal + RynthCore" lives in decal-accounts.json).
            new SharedInjectionModeConverter(),
            new JsonStringEnumConverter()
        }
    };

    /// <summary>Tests only (tools\LauncherSettingsTests): a folder used instead of %APPDATA%\RynthCore.</summary>
    internal static string? DirectoryOverride { get; set; }

    private static string SettingsDirectory => DirectoryOverride ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RynthCore");

    private static string SettingsPath => Path.Combine(SettingsDirectory, "appsettings.json");
    private static string BackupPath => SettingsPath + ".bak";
    private static string DecalAccountsPath => DecalAccountModes.PathIn(SettingsDirectory);

    /// <summary>
    /// Set when the last Load() hit a corrupt/unreadable file (quarantined it,
    /// possibly recovered from the .bak). Null when the load was clean. The
    /// launcher surfaces this after startup — this store is shared source and
    /// must not depend on launcher-only logging.
    /// </summary>
    public static string? LastLoadDiagnostic { get; private set; }

    /// <summary>
    /// Set when the last Load() moved "Decal + RynthCore" accounts out of appsettings.json
    /// into decal-accounts.json (a file written by a pre-release Decal bridge build), or found
    /// decal-accounts.json unreadable. Null otherwise.
    /// </summary>
    public static string? LastMigrationNote { get; private set; }

    /// <summary>
    /// Set when the last Load() encrypted plain-text account passwords from an older settings
    /// file ("migrated N account password(s) to encrypted storage"), or could not. Never holds a
    /// password. Null otherwise.
    /// </summary>
    public static string? LastPasswordMigrationNote { get; private set; }

    /// <summary>
    /// Set when the last Load() found saved passwords this Windows user can't decrypt (settings
    /// copied from another user or PC); those accounts have PasswordNeedsReentry set. Null otherwise.
    /// </summary>
    public static string? LastPasswordProblemNote { get; private set; }

    public static AppSettings Load()
    {
        LastLoadDiagnostic = null;
        LastMigrationNote = null;
        LastPasswordMigrationNote = null;
        LastPasswordProblemNote = null;
        // Passwords after the Decal accounts: a save before ApplyDecalAccounts would drop
        // decal-accounts.json (every account still reads as RynthCore at that point).
        AppSettings settings = ApplyDecalAccounts(LoadShared());
        MigratePasswords(settings);
        return settings;
    }

    /// <summary>
    /// Encrypts plain-text passwords from an older appsettings.json (LaunchAccountProfile
    /// "Password") into PasswordProtected and saves once, .bak included, so the plain text is
    /// gone from both files. Also marks accounts whose encrypted password this Windows user can't
    /// decrypt. Never logs or reports a password.
    /// </summary>
    private static void MigratePasswords(AppSettings settings)
    {
        int migrated = 0, cleared = 0, failed = 0;
        var unreadable = new List<string>();
        foreach (LaunchAccountProfile account in settings.AccountProfiles ?? new List<LaunchAccountProfile>())
        {
            if (account.LegacyPlainPassword != null)
            {
                if (account.LegacyPlainPassword.Length == 0)
                {
                    account.LegacyPlainPassword = null;
                    cleared++;
                    continue;
                }
                try
                {
                    account.PasswordProtected = AccountPasswordProtection.Protect(account.LegacyPlainPassword);
                    account.LegacyPlainPassword = null;
                    account.PasswordNeedsReentry = false;
                    migrated++;
                }
                catch
                {
                    failed++;   // kept as it was (still usable); the next load tries again
                }
                continue;
            }

            if (!string.IsNullOrEmpty(account.PasswordProtected) &&
                !AccountPasswordProtection.CanUnprotect(account.PasswordProtected))
            {
                account.PasswordNeedsReentry = true;
                unreadable.Add(string.IsNullOrWhiteSpace(account.AccountName) ? account.Id : account.AccountName);
            }
        }

        if (unreadable.Count > 0)
        {
            LastPasswordProblemNote = $"{unreadable.Count} saved account password(s) can't be decrypted on this Windows user " +
                                      $"(settings copied from another user or PC?) and need re-entering: {string.Join(", ", unreadable)}.";
        }

        if (migrated + cleared == 0)
        {
            if (failed > 0)
                LastPasswordMigrationNote = $"Could not encrypt {failed} account password(s); they stay as they were and the next start tries again.";
            return;
        }

        try
        {
            Save(settings);
            // Save() moved the old file (plain text) to .bak: replace it with the encrypted one.
            File.Copy(SettingsPath, BackupPath, overwrite: true);
            if (migrated == 0 && failed == 0)
                return;   // only empty plain-text fields removed
            LastPasswordMigrationNote = $"migrated {migrated} account password(s) to encrypted storage";
            if (failed > 0)
                LastPasswordMigrationNote += $"; {failed} could not be encrypted and stay as they were";
            int quarantined = CountQuarantinedFiles();
            if (quarantined > 0)
                LastPasswordMigrationNote += $". {quarantined} older appsettings.json.corrupt-* file(s) in {SettingsDirectory} " +
                                             "may still hold plain-text passwords; delete them when no longer needed";
        }
        catch (Exception ex)
        {
            LastPasswordMigrationNote = $"Could not save the encrypted account passwords ({ex.GetType().Name}); the next save retries.";
        }
    }

    private static int CountQuarantinedFiles()
    {
        try { return Directory.GetFiles(SettingsDirectory, "appsettings.json.corrupt-*").Length; }
        catch { return 0; }
    }

    private static AppSettings LoadShared()
    {
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

    /// <summary>
    /// Puts back the "Decal + RynthCore" accounts listed in decal-accounts.json (those that
    /// appsettings.json still lists as RynthCore). An appsettings.json that itself says
    /// "DecalBridge" (written by a pre-release bridge build) is rewritten at once, .bak
    /// included, so no launcher finds that value there again.
    /// </summary>
    private static AppSettings ApplyDecalAccounts(AppSettings settings)
    {
        List<LaunchAccountProfile> accounts = settings.AccountProfiles ?? new List<LaunchAccountProfile>();
        int legacy = accounts.Count(a => a.InjectionMode == InjectionMode.DecalBridge);

        HashSet<string> ids = DecalAccountModes.ReadIds(DecalAccountsPath, out string? error);
        if (error != null)
            LastMigrationNote = error;
        foreach (LaunchAccountProfile account in accounts)
            if (DecalAccountModes.IsDecalBridge(ids, account.Id, account.InjectionMode))
                account.InjectionMode = InjectionMode.DecalBridge;

        if (legacy > 0)
        {
            try
            {
                Save(settings);
                File.Copy(SettingsPath, BackupPath, overwrite: true);
                LastMigrationNote = $"Moved {legacy} \"Decal + RynthCore\" account(s) from appsettings.json to " +
                                    $"{DecalAccountModes.FileName}, so older launchers can still read appsettings.json.";
            }
            catch (Exception ex)
            {
                LastMigrationNote = $"Could not move \"Decal + RynthCore\" accounts to {DecalAccountModes.FileName} " +
                                    $"({ex.GetType().Name}: {ex.Message}); the next save retries.";
            }
        }
        return settings;
    }

    public static void Save(AppSettings settings)
    {
        // "Decal + RynthCore" accounts go to decal-accounts.json first; appsettings.json
        // lists them as RynthCore (SharedInjectionModeConverter).
        Directory.CreateDirectory(SettingsDirectory);
        DecalAccountModes.WriteIds(DecalAccountsPath,
            (settings.AccountProfiles ?? new List<LaunchAccountProfile>())
                .Where(a => a.InjectionMode == InjectionMode.DecalBridge)
                .Select(a => a.Id));

        // Atomic write: serialize to a temp file, then swap it in. A crash or
        // power cut mid-write can no longer truncate the live file (which holds
        // every server/account profile and is rewritten constantly); the swap
        // also maintains a .bak Load() can recover from.
        string tmp = SettingsPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
        if (File.Exists(SettingsPath))
            File.Replace(tmp, SettingsPath, BackupPath);
        else
            File.Move(tmp, SettingsPath);
    }

    /// <summary>
    /// InjectionMode in appsettings.json. Writes only names every launcher knows: DecalBridge is
    /// written as <see cref="DecalAccountModes.SharedMode"/> (decal-accounts.json holds the real
    /// choice). Reads names and numbers; a name or number this build doesn't know reads as RynthCore
    /// instead of making the whole file unreadable.
    /// </summary>
    private sealed class SharedInjectionModeConverter : JsonConverter<InjectionMode>
    {
        public override InjectionMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int number))
                return Enum.IsDefined((InjectionMode)number) ? (InjectionMode)number : InjectionMode.RynthCore;
            if (reader.TokenType == JsonTokenType.String)
            {
                return Enum.TryParse(reader.GetString(), ignoreCase: true, out InjectionMode mode) && Enum.IsDefined(mode)
                    ? mode
                    : InjectionMode.RynthCore;
            }
            throw new JsonException($"InjectionMode: unexpected {reader.TokenType}.");
        }

        public override void Write(Utf8JsonWriter writer, InjectionMode value, JsonSerializerOptions options)
        {
            InjectionMode shared = value == InjectionMode.DecalBridge ? DecalAccountModes.SharedMode : value;
            writer.WriteStringValue(shared.ToString());
        }
    }
}
