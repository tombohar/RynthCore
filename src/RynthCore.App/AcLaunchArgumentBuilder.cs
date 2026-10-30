using System;
using System.Collections.Generic;
using System.Globalization;

namespace RynthCore.App;

internal sealed class AcLaunchArgumentBuilder
{
    /// <summary>Text shown in place of the password in previews.</summary>
    public const string PasswordMask = "********";

    /// <summary>
    /// True when <paramref name="account"/> can't be launched until a password is entered: none
    /// is saved, or the saved one can't be decrypted on this Windows user. Every supported
    /// emulator logs on with a password, and the client refuses an empty one before it connects
    /// ("You can only run the game from the launcher program").
    /// </summary>
    public static bool NeedsPassword(LaunchAccountProfile account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return account.PasswordNeedsReentry || !account.HasSavedPassword;
    }

    /// <summary>Why an account without a password can't be launched (no secrets in it).</summary>
    public static string MissingPasswordMessage(string? accountName) =>
        $"No password is saved for account '{accountName}'. The AC client won't start without one " +
        "(\"You can only run the game from the launcher program\"). Launch it by hand to enter it, or edit the account.";

    /// <summary>
    /// The client's command line, with the saved password decrypted now. The result carries the
    /// password: hand it to the process and never log, display or keep it. Throws when no password
    /// is saved or the saved one can't be decrypted on this Windows user (never launches with an
    /// empty one instead).
    /// </summary>
    public string BuildArguments(LaunchServerProfile server, LaunchAccountProfile account)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(account);

        Validate(server, account);

        if (!account.TryGetPasswordForLaunch(out string password, out string problem))
            throw new InvalidOperationException(problem);
        if (password.Length == 0)
            throw new InvalidOperationException(MissingPasswordMessage(account.AccountName));

        return Build(server, account, password);
    }

    /// <summary>
    /// The same command line for display: the password is never decrypted, a saved one shows as
    /// <see cref="PasswordMask"/>.
    /// </summary>
    public string BuildMaskedArguments(LaunchServerProfile server, LaunchAccountProfile account)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(account);

        Validate(server, account);

        if (account.PasswordNeedsReentry)
            return LaunchAccountProfile.PasswordNeedsReentryMessage(account.AccountName);
        if (!account.HasSavedPassword)
            return MissingPasswordMessage(account.AccountName);

        return Build(server, account, PasswordMask);
    }

    private static string Build(LaunchServerProfile server, LaunchAccountProfile account, string password)
    {
        return server.Emulator switch
        {
            AcEmulatorKind.Ace => string.Join(" ",
                "-a", Quote(account.AccountName),
                "-v", Quote(password),
                "-h", Quote(server.ConnectionString),
                "-rodat", server.RodatEnabled ? "on" : "off"),
            AcEmulatorKind.Gdle => string.Join(" ",
                "-h", Quote(server.Host),
                "-p", server.Port.ToString(CultureInfo.InvariantCulture),
                "-a", Quote($"{account.AccountName}:{password}"),
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
        else if (AccountNameProblem(account.AccountName, server.Emulator) is string nameProblem)
            errors.Add(nameProblem);

        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join(" ", errors));
    }

    /// <summary>Longest account name ACE accepts (AuthenticationHandler rejects longer ones).</summary>
    public const int MaxAccountNameLength = 50;

    /// <summary>
    /// Why <paramref name="accountName"/> can't log on to a server of <paramref name="emulator"/>
    /// (null = fine). GDLE takes "account:password" in one argument, so a colon in the name would
    /// split it in the wrong place.
    /// </summary>
    public static string? AccountNameProblem(string? accountName, AcEmulatorKind emulator)
    {
        string name = accountName?.Trim() ?? string.Empty;
        if (name.Length == 0)
            return "Account name is required.";
        if (name.Length > MaxAccountNameLength)
            return $"Account name is longer than {MaxAccountNameLength} characters.";
        if (emulator == AcEmulatorKind.Gdle && name.Contains(':'))
            return "Account names can't contain ':' on GDLE servers.";
        return null;
    }

    /// <summary>
    /// The switches the launcher writes itself from the account and its server profile (ACE and
    /// GDLE lines above). Extra Launch Arguments may not repeat them.
    /// </summary>
    public static readonly IReadOnlyList<string> LoginSwitches = ["-a", "-v", "-h", "-p", "-rodat"];

    /// <summary>
    /// Checks the Runtime tab's "Extra Launch Arguments" and gives them as one line
    /// (<paramref name="normalized"/>) to put after each account's own arguments with
    /// <see cref="WithExtraArguments"/>. Null = fine (an empty box is fine); else why the launch
    /// can't go ahead, and <paramref name="normalized"/> is empty.
    ///
    /// The box used to replace the whole command line, account, password and host included (and
    /// only for one manual launch at a time), though it was labelled "appended". It now appends,
    /// for every launch. So it may not carry the login switches (<see cref="LoginSwitches"/>): a
    /// saved old override would otherwise ride along on every account and could log each of them
    /// in as the override's account, or send their passwords to the override's host. Line breaks
    /// and tabs become spaces (the client splits on spaces only), and a quote left open is refused
    /// (the client would then drop the whole line, login included).
    /// </summary>
    public static string? ExtraArgumentsProblem(string? extra, out string normalized)
    {
        normalized = string.Empty;
        string line = (extra ?? string.Empty).Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        if (line.Length == 0)
            return null;

        List<string>? args = AcClientCommandLine.Split(line);
        if (args == null)
            return "Extra Launch Arguments have a quote mark left open; the client would ignore its whole command line, login included. Close or remove the quote.";

        foreach (string arg in args)
        {
            foreach (string loginSwitch in LoginSwitches)
            {
                if (string.Equals(arg, loginSwitch, StringComparison.OrdinalIgnoreCase))
                    return $"Extra Launch Arguments can't contain {loginSwitch}: the account and its server profile set " +
                           $"{string.Join(", ", LoginSwitches)} themselves. Remove it (an old whole-line override goes in the account and server profiles now).";
            }
        }

        normalized = line;
        return null;
    }

    /// <summary>
    /// <paramref name="accountArguments"/> (from <see cref="BuildArguments"/> or
    /// <see cref="BuildMaskedArguments"/>) followed by extra arguments already checked by
    /// <see cref="ExtraArgumentsProblem"/>.
    /// </summary>
    public static string WithExtraArguments(string accountArguments, string normalizedExtra) =>
        string.IsNullOrWhiteSpace(normalizedExtra) ? accountArguments : accountArguments + " " + normalizedExtra;

    /// <summary>One argument by acclient's own rules (AcClientCommandLine), not the C runtime's.</summary>
    private static string Quote(string value) => AcClientCommandLine.Token(value);
}
