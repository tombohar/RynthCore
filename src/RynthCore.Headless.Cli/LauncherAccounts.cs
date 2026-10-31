// ============================================================================
//  rynth-headless - LauncherAccounts.cs
//  Reads an account saved in the RynthCore launcher (%APPDATA%\RynthCore\appsettings.json)
//  for the server we are connecting to, and decrypts its password (Windows DPAPI, this user
//  only) at the moment of login. The password is never printed, logged or written.
// ============================================================================

using System.Text.Json;
using RynthCore.App;

namespace RynthCore.Headless.Cli;

internal static class LauncherAccounts
{
    private static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RynthCore", "appsettings.json");

    /// <summary>
    /// The saved password for <paramref name="account"/> on host:port. Null with a reason when
    /// the launcher has no such account for that server, or the password can't be decrypted.
    /// </summary>
    public static string? TryGetPassword(string account, string host, int port, out string problem)
    {
        problem = "";
        if (!File.Exists(SettingsPath)) { problem = "no launcher settings"; return null; }

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        JsonElement root = doc.RootElement;

        var serverIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("ServerProfiles", out JsonElement servers))
            foreach (JsonElement s in servers.EnumerateArray())
                if (string.Equals(Str(s, "Host"), host, StringComparison.OrdinalIgnoreCase)
                    && s.TryGetProperty("Port", out JsonElement p) && p.TryGetInt32(out int sp) && sp == port)
                    serverIds.Add(Str(s, "Id"));
        if (serverIds.Count == 0) { problem = $"the launcher has no server {host}:{port}"; return null; }

        if (root.TryGetProperty("AccountProfiles", out JsonElement accounts))
        {
            foreach (JsonElement a in accounts.EnumerateArray())
            {
                if (!serverIds.Contains(Str(a, "ServerId"))
                    || !string.Equals(Str(a, "AccountName"), account, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (AccountPasswordProtection.TryUnprotect(Str(a, "PasswordProtected"), out string pw))
                    return pw;
                problem = "the saved password can't be decrypted on this Windows user";
                return null;
            }
        }
        problem = $"the launcher has no account {account} for {host}:{port}";
        return null;
    }

    /// <summary>The saved password of the launcher account profile <paramref name="profileId"/>.</summary>
    public static string? TryGetPasswordById(string profileId, out string problem)
    {
        problem = "";
        if (!File.Exists(SettingsPath)) { problem = "no launcher settings"; return null; }
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        if (doc.RootElement.TryGetProperty("AccountProfiles", out JsonElement accounts))
            foreach (JsonElement a in accounts.EnumerateArray())
            {
                if (!string.Equals(Str(a, "Id"), profileId, StringComparison.OrdinalIgnoreCase)) continue;
                if (AccountPasswordProtection.TryUnprotect(Str(a, "PasswordProtected"), out string pw)) return pw;
                problem = "the saved password can't be decrypted on this Windows user";
                return null;
            }
        problem = $"the launcher has no account profile {profileId}";
        return null;
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
