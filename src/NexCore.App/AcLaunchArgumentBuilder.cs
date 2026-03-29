using System;
using System.Collections.Generic;
using System.Globalization;

namespace NexCore.App;

internal sealed class AcLaunchArgumentBuilder
{
    public string BuildArguments(LaunchServerProfile server, LaunchAccountProfile account)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(account);

        Validate(server, account);

        return server.Emulator switch
        {
            AcEmulatorKind.Ace => string.Join(" ",
                "-a", Quote(account.AccountName),
                "-v", Quote(account.Password),
                "-h", Quote(server.ConnectionString),
                "-rodat", server.RodatEnabled ? "on" : "off"),
            AcEmulatorKind.Gdle => string.Join(" ",
                "-h", Quote(server.Host),
                "-p", server.Port.ToString(CultureInfo.InvariantCulture),
                "-a", Quote($"{account.AccountName}:{account.Password}"),
                "-rodat", server.RodatEnabled ? "on" : "off"),
            _ => throw new InvalidOperationException($"Unsupported emulator type '{server.Emulator}'.")
        };
    }

    public string Describe(LaunchServerProfile server, LaunchAccountProfile account)
    {
        return $"{server.DisplayName} / {account.DisplayName}";
    }

    private static void Validate(LaunchServerProfile server, LaunchAccountProfile account)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(server.Name))
            errors.Add("Server name is required.");
        if (string.IsNullOrWhiteSpace(server.Host))
            errors.Add("Server host is required.");
        if (server.Port <= 0)
            errors.Add("Server port must be greater than zero.");
        if (string.IsNullOrWhiteSpace(account.AccountName))
            errors.Add("Account name is required.");

        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join(" ", errors));
    }

    private static string Quote(string value)
    {
        value ??= string.Empty;

        if (value.Length == 0)
            return "\"\"";

        if (!NeedsQuoting(value))
            return value;

        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static bool NeedsQuoting(string value)
    {
        foreach (char ch in value)
        {
            if (char.IsWhiteSpace(ch) || ch == '"')
                return true;
        }

        return false;
    }
}
