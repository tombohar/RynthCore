using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace RynthCore.App;

internal static class CharacterCacheStore
{
    /// Strict reader — only returns characters from the server-scoped file
    /// for this (account, server) pair. Will NOT fall through to the
    /// account-only file. Use this when correctness across accounts/servers
    /// matters more than tolerance for missing data (e.g. UI dropdowns where
    /// stale legacy files would leak the wrong account's characters).
    public static List<string> ReadStrict(string accountName, string serverName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(accountName) || string.IsNullOrWhiteSpace(serverName))
                return [];

            string safeAccount = SanitizeFileName(accountName);
            string safeServer = SanitizeFileName(serverName);
            return ReadCharacterFile(GetServerScopedPath(safeServer, safeAccount));
        }
        catch
        {
            return [];
        }
    }

    public static List<string> Read(string accountName, string serverName = "")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(accountName))
                return [];

            string safeAccount = SanitizeFileName(accountName);
            if (!string.IsNullOrWhiteSpace(serverName))
            {
                string safeServer = SanitizeFileName(serverName);
                List<string> serverCharacters = ReadCharacterFile(GetServerScopedPath(safeServer, safeAccount));
                if (serverCharacters.Count > 0)
                    return serverCharacters;

                // The same account name can exist on several servers. The legacy
                // account-only file predates server scoping, so it is only trusted
                // while no server-scoped file exists for this account at all;
                // otherwise it would hand one server's characters to another.
                if (HasAnyServerScopedFile(safeAccount))
                    return [];
            }

            return ReadCharacterFile(GetAccountScopedPath(safeAccount));
        }
        catch
        {
            return [];
        }
    }

    public static void Write(string accountName, string serverName, IReadOnlyCollection<string> characters)
    {
        if (string.IsNullOrWhiteSpace(accountName))
            return;

        List<string> sanitizedCharacters = SanitizeCharacters(characters);
        if (sanitizedCharacters.Count == 0)
            return;

        Directory.CreateDirectory(GetRootDirectory());
        string safeAccount = SanitizeFileName(accountName);
        string json = BuildCharacterListJson(sanitizedCharacters, DateTime.Now);

        if (!string.IsNullOrWhiteSpace(serverName))
        {
            string safeServer = SanitizeFileName(serverName);
            File.WriteAllText(GetServerScopedPath(safeServer, safeAccount), json);
            return;
        }

        File.WriteAllText(GetAccountScopedPath(safeAccount), json);
    }

    public static void UpsertCharacter(string accountName, string serverName, string characterName)
    {
        if (string.IsNullOrWhiteSpace(accountName) || string.IsNullOrWhiteSpace(characterName))
            return;

        // With a server, seed only from that server's own file so a legacy
        // account-only list cannot be merged into the wrong server.
        List<string> characters = string.IsNullOrWhiteSpace(serverName)
            ? Read(accountName, serverName)
            : ReadStrict(accountName, serverName);
        if (!characters.Any(existing => IsSameCharacter(existing, characterName)))
            characters.Add(characterName);

        Write(accountName, serverName, characters);
    }

    // The client shows admin names with a leading '+' ('+Buffi'); a launch-context
    // name may lack it. Treat both as one character so the list holds no duplicate.
    private static bool IsSameCharacter(string a, string b) =>
        string.Equals(a.Trim().TrimStart('+'), b.Trim().TrimStart('+'), StringComparison.OrdinalIgnoreCase);

    /// Deletes the character cache for one (account, server) pair. Other servers'
    /// files for the same account name are left alone. The legacy account-only
    /// file is removed only when <paramref name="includeLegacy"/> is set (the
    /// caller knows whether another profile still uses this account name).
    public static void DeleteForAccount(string accountName, string serverName, bool includeLegacy)
    {
        if (string.IsNullOrWhiteSpace(accountName))
            return;

        try
        {
            string safeAccount = SanitizeFileName(accountName);
            if (!string.IsNullOrWhiteSpace(serverName))
            {
                string serverScopedPath = GetServerScopedPath(SanitizeFileName(serverName), safeAccount);
                if (File.Exists(serverScopedPath))
                {
                    try { File.Delete(serverScopedPath); } catch { }
                }
            }

            if (includeLegacy)
            {
                string accountScopedPath = GetAccountScopedPath(safeAccount);
                if (File.Exists(accountScopedPath))
                {
                    try { File.Delete(accountScopedPath); } catch { }
                }
            }
        }
        catch
        {
        }
    }

    private static bool HasAnyServerScopedFile(string safeAccountName)
    {
        try
        {
            string rootDirectory = GetRootDirectory();
            return Directory.Exists(rootDirectory) &&
                   Directory.EnumerateFiles(rootDirectory, $"characters_*_{safeAccountName}.json").Any();
        }
        catch
        {
            return false;
        }
    }

    private static string GetRootDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RynthCore");

    private static string GetServerScopedPath(string safeServerName, string safeAccountName) =>
        Path.Combine(GetRootDirectory(), $"characters_{safeServerName}_{safeAccountName}.json");

    private static string GetAccountScopedPath(string safeAccountName) =>
        Path.Combine(GetRootDirectory(), $"characters_{safeAccountName}.json");

    private static List<string> ReadCharacterFile(string filePath)
    {
        if (!File.Exists(filePath))
            return [];

        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(filePath));
        if (!document.RootElement.TryGetProperty("Characters", out JsonElement value) || value.ValueKind != JsonValueKind.Array)
            return [];

        return value.EnumerateArray()
            .Select(element => element.GetString()?.Trim() ?? string.Empty)
            .Where(character => !string.IsNullOrWhiteSpace(character))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> SanitizeCharacters(IEnumerable<string>? characters)
    {
        return (characters ?? [])
            .Select(character => character?.Trim() ?? string.Empty)
            .Where(character => !string.IsNullOrWhiteSpace(character))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildCharacterListJson(IReadOnlyList<string> characters, DateTime updatedAt)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("Characters");
            writer.WriteStartArray();
            foreach (string character in characters)
                writer.WriteStringValue(character);
            writer.WriteEndArray();
            writer.WriteString("UpdatedAt", updatedAt.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string SanitizeFileName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.ToString();
    }
}
