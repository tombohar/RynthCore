using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RynthCore.Injector;

/// <summary>
/// Headless "launch a configured profile + inject RynthCore" command, exposed as
/// <c>RynthCore.Injector.exe --launch</c>. Mirrors the essential bits of the
/// Avalonia launcher's RynthCore launch path (resolve account+server profiles
/// from %APPDATA%\RynthCore\appsettings.json, build AC auto-login args, launch
/// suspended + inject the loader, and write the per-PID launch_context_*.json
/// the engine reads for character auto-select) — without the GUI, so a test
/// harness can start a client with one call.
///
/// Defaults to the private RynthCore AC copy (C:\Games\RynthCore\AcClient) so it
/// never contends on DAT file locks with Decal/ThwargLauncher clients running
/// out of C:\Turbine\Asheron's Call. Override with --client.
///
/// Usage:
///   RynthCore.Injector.exe --launch [--account &lt;name|id&gt;] [--server &lt;name|id&gt;]
///                                   [--client &lt;acclient.exe&gt;] [--engine &lt;loader.dll&gt;]
///                                   [--no-login]
///
/// On success prints a machine-readable <c>LAUNCHED_PID=&lt;pid&gt;</c> line and
/// returns 0. Never prompts (safe for non-interactive shells).
/// </summary>
internal static class LaunchCommand
{
    // The "bulletproof" private AC copy intended for RynthCore launches — a
    // separate DAT lock domain from the shared Turbine install.
    private const string PrivateAcClientPath = @"C:\Games\RynthCore\AcClient\acclient.exe";

    // Matches LaunchAccountProfile.NoneOption — explicit opt-out of auto-login.
    private const string NoneOption = "(None — no auto-login)";

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static int Run(string[] args, Action<string> logToFile)
    {
        void Log(string msg)
        {
            Console.WriteLine(msg);
            logToFile($"[launch] {msg}");
        }

        string? accountSelector = GetOpt(args, "--account");
        string? serverSelector = GetOpt(args, "--server");
        string? clientOverride = GetOpt(args, "--client");
        string? engineOverride = GetOpt(args, "--engine");
        bool noLogin = HasFlag(args, "--no-login");

        string settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RynthCore", "appsettings.json");
        if (!File.Exists(settingsPath))
        {
            Log($"appsettings.json not found at {settingsPath}.");
            return 2;
        }

        AppSettingsDto? settings;
        try
        {
            settings = JsonSerializer.Deserialize<AppSettingsDto>(File.ReadAllText(settingsPath), ReadOpts);
        }
        catch (Exception ex)
        {
            Log($"Failed to parse appsettings.json: {ex.Message}");
            return 2;
        }
        if (settings == null)
        {
            Log("appsettings.json parsed to null.");
            return 2;
        }

        // ── Resolve account ──────────────────────────────────────────────────
        List<AccountDto> accounts = settings.AccountProfiles ?? new();
        string? selector = accountSelector ?? settings.SelectedAccountProfileId;
        AccountDto? account = null;
        if (!string.IsNullOrWhiteSpace(selector))
        {
            account = accounts.FirstOrDefault(a =>
                string.Equals(a.Id, selector, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a.AccountName, selector, StringComparison.OrdinalIgnoreCase));
        }
        if (account == null && (settings.CheckedLaunchAccountProfileIds?.Count ?? 0) > 0)
        {
            account = accounts.FirstOrDefault(a =>
                settings.CheckedLaunchAccountProfileIds!.Contains(a.Id));
        }
        if (account == null)
        {
            Log($"Could not resolve an account (selector='{selector ?? "<none>"}'). " +
                $"Available: {string.Join(", ", accounts.Select(a => a.AccountName))}");
            return 2;
        }

        if (account.InjectionMode == InjMode.Decal)
            Log($"WARNING: account '{account.AccountName}' is configured for Decal mode; --launch always injects the RynthCore engine.");

        // ── Resolve server ───────────────────────────────────────────────────
        List<ServerDto> servers = settings.ServerProfiles ?? new();
        ServerDto? server = null;
        if (!string.IsNullOrWhiteSpace(serverSelector))
        {
            server = servers.FirstOrDefault(s =>
                string.Equals(s.Id, serverSelector, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Name, serverSelector, StringComparison.OrdinalIgnoreCase));
        }
        server ??= servers.FirstOrDefault(s =>
            string.Equals(s.Id, account.ServerId, StringComparison.OrdinalIgnoreCase));
        if (server == null)
        {
            Log($"Could not resolve a server for account '{account.AccountName}' (serverId='{account.ServerId}').");
            return 2;
        }

        // ── Resolve paths ────────────────────────────────────────────────────
        string clientPath;
        if (!string.IsNullOrWhiteSpace(clientOverride))
            clientPath = clientOverride!;
        else if (File.Exists(PrivateAcClientPath))
            clientPath = PrivateAcClientPath;
        else
            clientPath = settings.AcClientPath ?? string.Empty;

        string enginePath = !string.IsNullOrWhiteSpace(engineOverride)
            ? engineOverride!
            : settings.EnginePath ?? string.Empty;

        if (!File.Exists(clientPath))
        {
            Log($"AC client not found: '{clientPath}'.");
            return 2;
        }
        if (string.IsNullOrWhiteSpace(enginePath))
        {
            Log("Engine/loader path is not set (appsettings.EnginePath empty and no --engine).");
            return 2;
        }

        // ── Build AC auto-login args (mirrors AcLaunchArgumentBuilder) ────────
        string conn = ComputeConnectionString(server);
        string rodat = server.RodatEnabled ? "on" : "off";
        string acct = account.AccountName ?? string.Empty;
        string pwd = account.Password ?? string.Empty;

        string arguments = server.Emulator == EmulatorKind.Gdle
            ? string.Join(" ",
                "-h", Quote(server.Host ?? string.Empty),
                "-p", server.Port.ToString(CultureInfo.InvariantCulture),
                "-a", Quote($"{acct}:{pwd}"),
                "-rodat", rodat)
            : string.Join(" ",
                "-a", Quote(acct),
                "-v", Quote(pwd),
                "-h", Quote(conn),
                "-rodat", rodat);

        string targetCharacter = noLogin || account.CharacterName == NoneOption
            ? string.Empty
            : account.CharacterName ?? string.Empty;

        // Credentials deliberately NOT logged (the args carry the password).
        Log($"Launching account='{acct}' server='{server.Name}' ({server.Emulator}) " +
            $"conn='{conn}' rodat={rodat} target='{(targetCharacter.Length == 0 ? "<login screen>" : targetCharacter)}'.");
        Log($"AC client: {clientPath}");
        Log($"Engine   : {enginePath}");

        // ── Launch suspended + inject ────────────────────────────────────────
        var service = new EngineInjectionService();
        int createdPid = 0;
        InjectionResult result = service.LaunchSuspendedAndInject(
            clientPath,
            arguments,
            enginePath,
            line =>
            {
                Console.WriteLine(line);
                logToFile($"[launch] {line}");
            },
            processId =>
            {
                createdPid = processId;
                try
                {
                    WriteLaunchContext(processId, acct, server.Name ?? string.Empty,
                        targetCharacter, settings.SkipLoginLogos, account.OnLoginWaitMs);
                }
                catch (Exception ex)
                {
                    Log($"WARNING: failed to write launch context for PID {processId}: {ex.Message}");
                }
            });

        if (result.Success && result.ProcessId is int pid)
        {
            Log($"SUCCESS — PID {pid} launched and injected.");
            Console.WriteLine($"LAUNCHED_PID={pid}");
            return 0;
        }

        Log($"FAILED: {result.Summary}");
        if (createdPid != 0)
            Console.WriteLine($"LAUNCHED_PID={createdPid}");   // process exists; harness may want to clean it up
        return result.ExitCode == 0 ? 1 : result.ExitCode;
    }

    /// <summary>
    /// Writes %APPDATA%\RynthCore\launch_contexts\launch_context_&lt;pid&gt;.json with the
    /// same PascalCase shape the launcher writes — the engine reads it to know
    /// which character to auto-select and whether to skip login logos.
    /// </summary>
    private static void WriteLaunchContext(
        int pid, string accountName, string serverName, string targetCharacter,
        bool skipLoginLogos, int onLoginWaitMs)
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RynthCore", "launch_contexts");
        Directory.CreateDirectory(dir);

        var record = new LaunchContextDto
        {
            ProcessId = pid,
            AccountName = accountName,
            ServerName = serverName,
            TargetCharacter = targetCharacter,
            SkipLoginLogos = skipLoginLogos,
            CreatedAtUtc = DateTime.UtcNow,
            OnLoginCommands = null,
            OnLoginWaitMs = onLoginWaitMs
        };

        File.WriteAllText(
            Path.Combine(dir, $"launch_context_{pid}.json"),
            JsonSerializer.Serialize(record));
    }

    private static string ComputeConnectionString(ServerDto s) =>
        string.IsNullOrWhiteSpace(s.Host) || s.Port <= 0
            ? string.Empty
            : $"{s.Host}:{s.Port}";

    // Mirrors AcLaunchArgumentBuilder.Quote — only quote when needed, escape
    // backslashes + quotes inside.
    private static string Quote(string value)
    {
        value ??= string.Empty;
        if (value.Length == 0)
            return "\"\"";
        bool needs = value.Any(ch => char.IsWhiteSpace(ch) || ch == '"');
        if (!needs)
            return value;
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static string? GetOpt(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return i + 1 < args.Length ? args[i + 1] : null;
            if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return args[i].Substring(name.Length + 1);
        }
        return null;
    }

    private static bool HasFlag(string[] args, string name) =>
        args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    // ── Minimal mirrors of the launcher's persisted DTOs ─────────────────────
    private sealed class AppSettingsDto
    {
        public string? AcClientPath { get; set; }
        public string? EnginePath { get; set; }
        public bool SkipLoginLogos { get; set; }
        public string? SelectedAccountProfileId { get; set; }
        public List<string>? CheckedLaunchAccountProfileIds { get; set; }
        public List<AccountDto>? AccountProfiles { get; set; }
        public List<ServerDto>? ServerProfiles { get; set; }
    }

    private sealed class AccountDto
    {
        public string Id { get; set; } = string.Empty;
        public string AccountName { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string CharacterName { get; set; } = string.Empty;
        public string ServerId { get; set; } = string.Empty;
        public int OnLoginWaitMs { get; set; }
        public InjMode InjectionMode { get; set; }
    }

    private sealed class ServerDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; }
        public EmulatorKind Emulator { get; set; }
        public bool RodatEnabled { get; set; }
    }

    private enum EmulatorKind { Ace = 0, Gdle = 1 }

    private enum InjMode { RynthCore = 0, Decal = 1 }

    private sealed class LaunchContextDto
    {
        public int ProcessId { get; set; }
        public string AccountName { get; set; } = string.Empty;
        public string ServerName { get; set; } = string.Empty;
        public string TargetCharacter { get; set; } = string.Empty;
        public bool SkipLoginLogos { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public List<string>? OnLoginCommands { get; set; }
        public int OnLoginWaitMs { get; set; }
    }
}
