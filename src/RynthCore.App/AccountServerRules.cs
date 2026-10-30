using System;
using System.Collections.Generic;
using System.Linq;

namespace RynthCore.App;

/// <summary>
/// Which server an account logs on to, and when a server profile may be deleted. Pure: no UI,
/// no files.
///
/// An account's name and password belong to one server, so an account only ever resolves to the
/// server profile it names (<see cref="LaunchAccountProfile.ServerId"/>). There is no fallback to
/// the selected or the first server: that fallback re-pointed every account of a deleted server
/// at another one and saved it, so the next launch (or an automatic relaunch within seconds) sent
/// one server's account name and password to a different server's operator, with nothing on
/// screen (structural audit 2026-10-21, F2). An account whose server profile is missing has no
/// server until the player picks one in its editor, and it doesn't launch.
///
/// Deleting a server that accounts still use is refused, with the accounts named, rather than
/// leaving them with no server. Refusing changes nothing behind the player's back: they move each
/// account to its new server (or delete it) and then delete the server. The other choice, clearing
/// the accounts' server, would quietly stop checked accounts from auto-launching (overnight
/// multi-boxing) with only a line in Activity. The no-server rule above stays as the backstop for
/// accounts whose server went some other way (settings copied from another PC, a second
/// launcher, a hand edit).
/// </summary>
internal static class AccountServerRules
{
    /// <summary>
    /// The server profile <paramref name="account"/> names, or null when it names none or one
    /// that isn't in <paramref name="servers"/>. Never another server.
    /// </summary>
    public static LaunchServerProfile? ServerFor(LaunchAccountProfile account, IEnumerable<LaunchServerProfile> servers)
    {
        ArgumentNullException.ThrowIfNull(account);
        string id = account.ServerId?.Trim() ?? string.Empty;
        if (id.Length == 0 || servers == null)
            return null;
        return servers.FirstOrDefault(s => s != null && string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The accounts that log on to <paramref name="server"/>.</summary>
    public static List<LaunchAccountProfile> AccountsOn(LaunchServerProfile server, IEnumerable<LaunchAccountProfile> accounts)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (accounts == null || string.IsNullOrWhiteSpace(server.Id))
            return [];
        return accounts
            .Where(a => a != null && string.Equals(a.ServerId?.Trim(), server.Id.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Why <paramref name="server"/> can't be deleted (it names the accounts that use it), or null
    /// when no account uses it.
    /// </summary>
    public static string? DeleteBlocker(LaunchServerProfile server, IEnumerable<LaunchAccountProfile> accounts)
    {
        List<LaunchAccountProfile> users = AccountsOn(server, accounts);
        if (users.Count == 0)
            return null;

        const int MaxNamed = 8;
        IEnumerable<string> names = users.Take(MaxNamed).Select(a => "  - " + a.DisplayName);
        string more = users.Count > MaxNamed ? $"\n  ...and {users.Count - MaxNamed} more" : string.Empty;
        return $"{server.DisplayName} can't be deleted while {users.Count} account(s) log on to it:\n" +
               string.Join("\n", names) + more + "\n\n" +
               "Their names and passwords belong to that server, so they are never moved to another one for you. " +
               "Edit each account and pick its server (or delete the account), then delete the server.";
    }

    /// <summary>Why <paramref name="account"/> can't launch: it has no server profile.</summary>
    public static string NoServerMessage(LaunchAccountProfile account, bool anyServerProfiles)
    {
        ArgumentNullException.ThrowIfNull(account);
        return anyServerProfiles
            ? $"account '{account.AccountName}' has no server (its server profile was deleted or never set). " +
              "Edit the account and pick the server it logs on to; it isn't sent to another server in the meantime."
            : $"account '{account.AccountName}' has no server. Add a server profile, then edit the account and pick it.";
    }
}
