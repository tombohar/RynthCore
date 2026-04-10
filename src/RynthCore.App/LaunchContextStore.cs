using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RynthCore.App;

internal sealed class LaunchContextRecord
{
    public int ProcessId { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public string ServerName { get; set; } = string.Empty;
    public string TargetCharacter { get; set; } = string.Empty;
    public bool SkipLoginLogos { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

internal static class LaunchContextStore
{
    private const string LegacyFileName = "launch_context.json";
    private const string ContextDirectoryName = "launch_contexts";

    public static LaunchContextRecord CreateRecord(
        string accountName,
        string serverName,
        string targetCharacter,
        bool skipLoginLogos)
    {
        return new LaunchContextRecord
        {
            AccountName = accountName ?? string.Empty,
            ServerName = serverName ?? string.Empty,
            TargetCharacter = targetCharacter ?? string.Empty,
            SkipLoginLogos = skipLoginLogos,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    public static void WriteLegacy(LaunchContextRecord context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Directory.CreateDirectory(GetRootDirectory());
        File.WriteAllText(GetLegacyPath(), JsonSerializer.Serialize(context));
    }

    public static void WriteForProcess(int processId, LaunchContextRecord context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Directory.CreateDirectory(GetContextDirectory());

        var processContext = new LaunchContextRecord
        {
            ProcessId = processId,
            AccountName = context.AccountName,
            ServerName = context.ServerName,
            TargetCharacter = context.TargetCharacter,
            SkipLoginLogos = context.SkipLoginLogos,
            CreatedAtUtc = DateTime.UtcNow
        };

        File.WriteAllText(GetProcessPath(processId), JsonSerializer.Serialize(processContext));
    }

    public static LaunchContextRecord? TryReadForProcess(int processId)
    {
        string path = GetProcessPath(processId);
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<LaunchContextRecord>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    public static Dictionary<int, LaunchContextRecord> ReadForActiveProcesses(IEnumerable<int> activeProcessIds)
    {
        var result = new Dictionary<int, LaunchContextRecord>();

        foreach (int processId in activeProcessIds.Distinct())
        {
            LaunchContextRecord? context = TryReadForProcess(processId);
            if (context != null)
                result[processId] = context;
        }

        return result;
    }

    public static void DeleteStaleProcessFiles(IEnumerable<int> activeProcessIds)
    {
        string contextDirectory = GetContextDirectory();
        if (!Directory.Exists(contextDirectory))
            return;

        HashSet<int> active = activeProcessIds.ToHashSet();
        foreach (string filePath in Directory.GetFiles(contextDirectory, "launch_context_*.json"))
        {
            string fileName = Path.GetFileNameWithoutExtension(filePath);
            const string prefix = "launch_context_";
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

    private static string GetLegacyPath() =>
        Path.Combine(GetRootDirectory(), LegacyFileName);

    private static string GetContextDirectory() =>
        Path.Combine(GetRootDirectory(), ContextDirectoryName);

    private static string GetProcessPath(int processId) =>
        Path.Combine(GetContextDirectory(), $"launch_context_{processId}.json");
}
