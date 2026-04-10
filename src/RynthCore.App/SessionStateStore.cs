using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace RynthCore.App;

internal sealed class SessionStateRecord
{
    public int ProcessId { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public string ServerName { get; set; } = string.Empty;
    public string TargetCharacter { get; set; } = string.Empty;
    public string CharacterName { get; set; } = string.Empty;
    public DateTime LaunchStartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LoginCompletedAtUtc { get; set; }
    public DateTime LastUpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsLoggedIn { get; set; }
}

internal static class SessionStateStore
{
    private const string SessionDirectoryName = "sessions";

    public static void WriteForProcess(int processId, SessionStateRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Directory.CreateDirectory(GetSessionDirectory());

        record.ProcessId = processId;
        record.LastUpdatedAtUtc = DateTime.UtcNow;
        File.WriteAllText(GetProcessPath(processId), BuildSessionJson(record), Encoding.UTF8);
    }

    public static SessionStateRecord? TryReadForProcess(int processId)
    {
        string path = GetProcessPath(processId);
        if (!File.Exists(path))
            return null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
            JsonElement root = document.RootElement;

            var record = new SessionStateRecord
            {
                ProcessId = GetInt32(root, "ProcessId"),
                AccountName = GetString(root, "AccountName"),
                ServerName = GetString(root, "ServerName"),
                TargetCharacter = GetString(root, "TargetCharacter"),
                CharacterName = GetString(root, "CharacterName"),
                LaunchStartedAtUtc = GetDateTime(root, "LaunchStartedAtUtc") ?? DateTime.UtcNow,
                LoginCompletedAtUtc = GetDateTime(root, "LoginCompletedAtUtc"),
                LastUpdatedAtUtc = GetDateTime(root, "LastUpdatedAtUtc") ?? DateTime.UtcNow,
                IsLoggedIn = GetBoolean(root, "IsLoggedIn")
            };

            if (record.ProcessId == 0)
                record.ProcessId = processId;

            return record;
        }
        catch
        {
            return null;
        }
    }

    public static Dictionary<int, SessionStateRecord> ReadForActiveProcesses(IEnumerable<int> activeProcessIds)
    {
        var result = new Dictionary<int, SessionStateRecord>();

        foreach (int processId in activeProcessIds.Distinct())
        {
            SessionStateRecord? record = TryReadForProcess(processId);
            if (record != null)
                result[processId] = record;
        }

        return result;
    }

    public static void DeleteStaleProcessFiles(IEnumerable<int> activeProcessIds)
    {
        string directory = GetSessionDirectory();
        if (!Directory.Exists(directory))
            return;

        HashSet<int> active = activeProcessIds.ToHashSet();
        foreach (string filePath in Directory.GetFiles(directory, "session_*.json"))
        {
            string fileName = Path.GetFileNameWithoutExtension(filePath);
            const string prefix = "session_";
            if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!int.TryParse(fileName[prefix.Length..], out int processId) || active.Contains(processId))
                continue;

            try
            {
                File.Delete(filePath);
            }
            catch
            {
            }
        }
    }

    private static string GetRootDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RynthCore");

    private static string GetSessionDirectory() =>
        Path.Combine(GetRootDirectory(), SessionDirectoryName);

    private static string GetProcessPath(int processId) =>
        Path.Combine(GetSessionDirectory(), $"session_{processId}.json");

    private static string BuildSessionJson(SessionStateRecord record)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("ProcessId", record.ProcessId);
            writer.WriteString("AccountName", record.AccountName ?? string.Empty);
            writer.WriteString("ServerName", record.ServerName ?? string.Empty);
            writer.WriteString("TargetCharacter", record.TargetCharacter ?? string.Empty);
            writer.WriteString("CharacterName", record.CharacterName ?? string.Empty);
            writer.WriteString("LaunchStartedAtUtc", record.LaunchStartedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            if (record.LoginCompletedAtUtc.HasValue)
                writer.WriteString("LoginCompletedAtUtc", record.LoginCompletedAtUtc.Value.ToString("O", CultureInfo.InvariantCulture));
            else
                writer.WriteNull("LoginCompletedAtUtc");
            writer.WriteString("LastUpdatedAtUtc", record.LastUpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteBoolean("IsLoggedIn", record.IsLoggedIn);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string GetString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static int GetInt32(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)
            ? number
            : 0;
    }

    private static bool GetBoolean(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : false;
    }

    private static DateTime? GetDateTime(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value))
            return null;

        if (value.ValueKind == JsonValueKind.Null)
            return null;

        if (value.ValueKind != JsonValueKind.String)
            return null;

        return DateTime.TryParse(
            value.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out DateTime parsed)
            ? parsed
            : null;
    }
}
