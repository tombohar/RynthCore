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
/// "Headless" accounts follow the same rules in a file of their own (HeadlessAccountModes.cs).
/// </summary>
internal static class DecalAccountModes
{
    public const string FileName = "decal-accounts.json";

    /// <summary>What appsettings.json says for a "Decal + RynthCore" account.</summary>
    public const InjectionMode SharedMode = AccountModeSidecar.SharedMode;

    private static readonly AccountModeSidecar Sidecar = new(
        FileName,
        "DecalBridgeAccountIds",
        "Accounts the RynthCore launcher runs as \"Decal + RynthCore\" (experimental). " +
        "appsettings.json lists them as RynthCore, so launchers without that mode can still read it. " +
        "Delete this file to set them all back to RynthCore.");

    public static string PathIn(string settingsDirectory) => Sidecar.PathIn(settingsDirectory);

    /// <summary>
    /// The account ids listed in <paramref name="path"/>. An absent file is an empty set; an
    /// unreadable one too, with the reason in <paramref name="error"/> (the accounts then run
    /// as appsettings.json says, which is RynthCore).
    /// </summary>
    public static HashSet<string> ReadIds(string path, out string? error) => Sidecar.ReadIds(path, out error);

    /// <summary>
    /// True when an account that appsettings.json lists with <paramref name="sharedMode"/> is a
    /// "Decal + RynthCore" account: it is in <paramref name="ids"/> and still says RynthCore.
    /// </summary>
    public static bool IsDecalBridge(IReadOnlySet<string> ids, string? accountId, InjectionMode sharedMode) =>
        AccountModeSidecar.Lists(ids, accountId, sharedMode);

    /// <summary>
    /// Records <paramref name="ids"/> as the "Decal + RynthCore" accounts. Writes only when the
    /// list changed; with no accounts left, the file is removed. Atomic (temp file + swap).
    /// </summary>
    public static void WriteIds(string path, IEnumerable<string> ids) => Sidecar.WriteIds(path, ids);
}

/// <summary>
/// One file beside appsettings.json listing the account ids that really run in a mode older
/// launchers don't know (appsettings.json says <see cref="SharedMode"/> for them). Used by
/// DecalAccountModes (decal-accounts.json) and HeadlessAccountModes (headless-accounts.json).
/// Each mode has a file of its own, so a launcher that knows only one of them never rewrites the
/// other's list. File: { "Version": 1, "Note": "...", "&lt;IdsProperty&gt;": [ "id", ... ] }.
/// </summary>
internal sealed class AccountModeSidecar
{
    /// <summary>What appsettings.json says for an account listed in a sidecar.</summary>
    public const InjectionMode SharedMode = InjectionMode.RynthCore;

    private const int SchemaVersion = 1;

    private static readonly JsonWriterOptions WriteOptions = new() { Indented = true };

    public AccountModeSidecar(string fileName, string idsProperty, string note)
    {
        FileName = fileName;
        IdsProperty = idsProperty;
        Note = note;
    }

    public string FileName { get; }
    public string IdsProperty { get; }
    public string Note { get; }

    public string PathIn(string settingsDirectory) => Path.Combine(settingsDirectory, FileName);

    /// <summary>True when <paramref name="accountId"/> is in <paramref name="ids"/> and appsettings.json still says RynthCore for it.</summary>
    public static bool Lists(IReadOnlySet<string> ids, string? accountId, InjectionMode sharedMode) =>
        sharedMode == SharedMode && !string.IsNullOrEmpty(accountId) && ids.Contains(accountId);

    /// <summary>The ids listed in <paramref name="path"/>: absent = none; unreadable = none, with the reason in <paramref name="error"/>.</summary>
    public HashSet<string> ReadIds(string path, out string? error)
    {
        error = null;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(path))
                return ids;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Null)
                return ids;
            if (root.ValueKind != JsonValueKind.Object)
                throw new JsonException($"expected an object, found {root.ValueKind}");
            if (!root.TryGetProperty(IdsProperty, out JsonElement list) || list.ValueKind == JsonValueKind.Null)
                return ids;
            if (list.ValueKind != JsonValueKind.Array)
                throw new JsonException($"{IdsProperty}: expected a list, found {list.ValueKind}");
            foreach (JsonElement item in list.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Null)
                    continue;
                if (item.ValueKind != JsonValueKind.String)
                    throw new JsonException($"{IdsProperty}: expected text, found {item.ValueKind}");
                string? id = item.GetString();
                if (!string.IsNullOrWhiteSpace(id))
                    ids.Add(id);
            }
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
    /// Records <paramref name="ids"/>. Writes only when the list changed; with no accounts left,
    /// the file is removed. Atomic (temp file + swap).
    /// </summary>
    public void WriteIds(string path, IEnumerable<string> ids)
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
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new Utf8JsonWriter(stream, WriteOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("Version", SchemaVersion);
            writer.WriteString("Note", Note);
            writer.WriteStartArray(IdsProperty);
            foreach (string id in sorted)
                writer.WriteStringValue(id);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        File.Move(tmp, path, overwrite: true);
    }
}
