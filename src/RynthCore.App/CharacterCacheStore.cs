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

        List<string> characters = Read(accountName, serverName);
        if (!characters.Contains(characterName, StringComparer.OrdinalIgnoreCase))
            characters.Add(characterName);

        Write(accountName, serverName, characters);
    }

    public static void DeleteForAccount(string accountName)
    {
        if (string.IsNullOrWhiteSpace(accountName))
            return;

        try
        {
            string rootDirectory = GetRootDirectory();
            if (!Directory.Exists(rootDirectory))
                return;

            string safeAccount = SanitizeFileName(accountName);
            foreach (string file in Directory.GetFiles(rootDirectory, $"characters_*_{safeAccount}.json"))
            {
                try { File.Delete(file); } catch { }
            }

            string accountScopedPath = GetAccountScopedPath(safeAccount);
            if (File.Exists(accountScopedPath))
            {
                try { File.Delete(accountScopedPath); } catch { }
            }
        }
        catch
        {
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
