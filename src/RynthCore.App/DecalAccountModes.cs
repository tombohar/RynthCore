using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RynthCore.App;

/// <summary>
/// Which accounts run as "Decal + RynthCore" (<see cref="InjectionMode.DecalBridge"/>), kept in
/// <c>decal-accounts.json</c> next to <c>%APPDATA%\RynthCore\appsettings.json</c> and keyed by
/// account profile id.
///
/// Why a file of its own: every launcher of this Windows user shares appsettings.json, and a
/// launcher released before the Decal bridge has no "DecalBridge" value in its InjectionMode.
/// Its AppSettingsStore takes a file with that value for corrupt, quarantines it and falls back
/// to the .bak, or to empty defaults (and then saves those). So appsettings.json always lists a
/// "Decal + RynthCore" account as <see cref="SharedMode"/> (RynthCore), and this file says which
/// of those are really "Decal + RynthCore".
///
/// An entry only counts while appsettings.json still says RynthCore for that account: an older
/// launcher that switched the account to Decal (or that deleted it) wins, and the next save of a
/// newer launcher drops the stale entry. Shared with the injector (LaunchCommand).
/// </summary>
internal static class DecalAccountModes
{
    public const string FileName = "decal-accounts.json";

    /// <summary>What appsettings.json says for a "Decal + RynthCore" account.</summary>
    public const InjectionMode SharedMode = InjectionMode.RynthCore;

    private const int SchemaVersion = 1;

    private const string Note =
        "Accounts the RynthCore launcher runs as \"Decal + RynthCore\" (experimental). " +
        "appsettings.json lists them as RynthCore, so launchers without that mode can still read it. " +
        "Delete this file to set them all back to RynthCore.";

    private sealed class FileModel
    {
        public int Version { get; set; } = SchemaVersion;
        public string Note { get; set; } = DecalAccountModes.Note;
        public List<string> DecalBridgeAccountIds { get; set; } = new();
    }

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static string PathIn(string settingsDirectory) => Path.Combine(settingsDirectory, FileName);

    /// <summary>
    /// The account ids listed in <paramref name="path"/>. An absent file is an empty set; an
    /// unreadable one too, with the reason in <paramref name="error"/> (the accounts then run
    /// as appsettings.json says, which is RynthCore).
    /// </summary>
    public static HashSet<string> ReadIds(string path, out string? error)
    {
        error = null;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(path))
                return ids;
            FileModel? model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path));
            foreach (string id in model?.DecalBridgeAccountIds ?? new List<string>())
                if (!string.IsNullOrWhiteSpace(id))
                    ids.Add(id);
        }
        catch (Exception ex)
        {
            error = $"{FileName} could not be read ({ex.GetType().Name}: {ex.Message}); " +
                    "its accounts run as RynthCore until the mode is picked again.";
            ids.Clear();
        }
        return ids;
    }

    /// <summary>
    /// True when an account that appsettings.json lists with <paramref name="sharedMode"/> is a
    /// "Decal + RynthCore" account: it is in <paramref name="ids"/> and still says RynthCore.
    /// </summary>
    public static bool IsDecalBridge(IReadOnlySet<string> ids, string? accountId, InjectionMode sharedMode) =>
        sharedMode == SharedMode && !string.IsNullOrEmpty(accountId) && ids.Contains(accountId);

    /// <summary>
    /// Records <paramref name="ids"/> as the "Decal + RynthCore" accounts. Writes only when the
    /// list changed; with no accounts left, the file is removed. Atomic (temp file + swap).
    /// </summary>
    public static void WriteIds(string path, IEnumerable<string> ids)
    {
        List<string> sorted = ids.Where(id => !string.IsNullOrWhiteSpace(id))
                                 .Distinct(StringComparer.Ordinal)
                                 .OrderBy(id => id, StringComparer.Ordinal)
                                 .ToList();
        if (sorted.Count == 0)
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }

        if (File.Exists(path))
        {
            HashSet<string> current = ReadIds(path, out string? error);
            if (error == null && current.SetEquals(sorted))
                return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new FileModel { DecalBridgeAccountIds = sorted }, WriteOptions));
        File.Move(tmp, path, overwrite: true);
    }
}
