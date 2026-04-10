using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using RynthCore.App;

namespace RynthCore.Engine.Compatibility;

internal static class SessionStateRegistry
{
    private static bool _initialized;
    private static bool _loginRecorded;

    public static void Initialize()
    {
        if (_initialized)
            return;

        _initialized = true;
        LoginLifecycleHooks.LoginComplete += OnLoginComplete;
    }

    public static void Poll()
    {
        if (_loginRecorded || !LoginLifecycleHooks.HasObservedLoginComplete)
            return;

        TryWriteLoginState();
    }

    private static void OnLoginComplete()
    {
        TryWriteLoginState();
    }

    private static void TryWriteLoginState()
    {
        if (_loginRecorded)
            return;

        try
        {
            SessionStateRecord? existingRecord = SessionStateStore.TryReadForProcess(Environment.ProcessId);
            (string accountName, string serverName, string targetCharacter) = ReadLaunchContext();

            accountName = Coalesce(accountName, existingRecord?.AccountName);
            serverName = Coalesce(serverName, existingRecord?.ServerName);
            targetCharacter = Coalesce(targetCharacter, existingRecord?.TargetCharacter);

            string characterName = ResolveCharacterName(targetCharacter);
            if (string.IsNullOrWhiteSpace(characterName))
                characterName = Coalesce(existingRecord?.CharacterName, targetCharacter);

            var record = new SessionStateRecord
            {
                ProcessId = Environment.ProcessId,
                AccountName = accountName,
                ServerName = serverName,
                TargetCharacter = targetCharacter,
                CharacterName = characterName,
                LaunchStartedAtUtc = existingRecord?.LaunchStartedAtUtc ?? GetProcessStartTimeUtc(),
                LoginCompletedAtUtc = DateTime.UtcNow,
                IsLoggedIn = true
            };

            SessionStateStore.WriteForProcess(Environment.ProcessId, record);
            if (!string.IsNullOrWhiteSpace(accountName) && !string.IsNullOrWhiteSpace(characterName))
                CharacterCacheStore.UpsertCharacter(accountName, serverName, characterName);

            _loginRecorded = true;
            RynthLog.Compat($"SessionState: recorded login session for PID {Environment.ProcessId} account='{accountName}' character='{characterName}'.");
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"SessionState: failed to record login session - {ex.Message}");
        }
    }

    private static string ResolveCharacterName(string fallbackCharacter)
    {
        uint playerId = ClientHelperHooks.GetPlayerId();
        if (playerId != 0 && ClientObjectHooks.TryGetObjectName(playerId, out string actualName) && !string.IsNullOrWhiteSpace(actualName))
            return actualName;

        return fallbackCharacter ?? string.Empty;
    }

    private static DateTime GetProcessStartTimeUtc()
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            return process.StartTime.ToUniversalTime();
        }
        catch
        {
            return DateTime.UtcNow;
        }
    }

    private static (string accountName, string serverName, string targetCharacter) ReadLaunchContext()
    {
        try
        {
            string rootDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RynthCore");

            string processPath = Path.Combine(rootDir, "launch_contexts", $"launch_context_{Environment.ProcessId}.json");
            (string accountName, string serverName, string targetCharacter) processContext = ReadLaunchContextFile(processPath);
            if (!string.IsNullOrWhiteSpace(processContext.accountName) ||
                !string.IsNullOrWhiteSpace(processContext.serverName) ||
                !string.IsNullOrWhiteSpace(processContext.targetCharacter))
            {
                return processContext;
            }

            return ReadLaunchContextFile(Path.Combine(rootDir, "launch_context.json"));
        }
        catch
        {
            return (string.Empty, string.Empty, string.Empty);
        }
    }

    private static (string accountName, string serverName, string targetCharacter) ReadLaunchContextFile(string filePath)
    {
        if (!File.Exists(filePath))
            return (string.Empty, string.Empty, string.Empty);

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(filePath));
        JsonElement root = doc.RootElement;
        string accountName = root.TryGetProperty("AccountName", out JsonElement an) ? an.GetString() ?? string.Empty : string.Empty;
        string serverName = root.TryGetProperty("ServerName", out JsonElement sn) ? sn.GetString() ?? string.Empty : string.Empty;
        string target = root.TryGetProperty("TargetCharacter", out JsonElement tc) ? tc.GetString() ?? string.Empty : string.Empty;
        return (accountName, serverName, target);
    }

    private static string Coalesce(string? primary, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(primary))
            return primary.Trim();

        return fallback?.Trim() ?? string.Empty;
    }
}
