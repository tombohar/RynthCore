using System.Collections.Generic;

namespace RynthCore.App;

/// <summary>
/// Which accounts run headless (<see cref="InjectionMode.Headless"/>: rynth-headless.exe instead
/// of acclient.exe), kept in <c>headless-accounts.json</c> next to appsettings.json and keyed by
/// account profile id. The same arrangement as "Decal + RynthCore" (DecalAccountModes, which
/// explains why): appsettings.json lists a Headless account as RynthCore, so every launcher and
/// the injector can still read it, and this file says which of those are really Headless. An
/// entry only counts while appsettings.json still says RynthCore for that account.
///
/// A file of its own rather than a second list in decal-accounts.json: a launcher that knows the
/// Decal bridge but not Headless rewrites decal-accounts.json with only its own list, and would
/// drop the Headless one. It never touches this file. (Such a launcher launches a Headless
/// account as RynthCore in acclient.exe, which is what its appsettings.json entry says.)
/// </summary>
internal static class HeadlessAccountModes
{
    public const string FileName = "headless-accounts.json";

    /// <summary>What appsettings.json says for a Headless account.</summary>
    public const InjectionMode SharedMode = AccountModeSidecar.SharedMode;

    private static readonly AccountModeSidecar Sidecar = new(
        FileName,
        "HeadlessAccountIds",
        "Accounts the RynthCore launcher runs headless (rynth-headless.exe, experimental). " +
        "appsettings.json lists them as RynthCore, so launchers without that mode can still read it. " +
        "Delete this file to set them all back to RynthCore.");

    public static string PathIn(string settingsDirectory) => Sidecar.PathIn(settingsDirectory);

    /// <summary>The account ids listed in <paramref name="path"/> (absent or unreadable = none; see <paramref name="error"/>).</summary>
    public static HashSet<string> ReadIds(string path, out string? error) => Sidecar.ReadIds(path, out error);

    /// <summary>True when an account appsettings.json lists with <paramref name="sharedMode"/> is a Headless account.</summary>
    public static bool IsHeadless(IReadOnlySet<string> ids, string? accountId, InjectionMode sharedMode) =>
        AccountModeSidecar.Lists(ids, accountId, sharedMode);

    /// <summary>Records <paramref name="ids"/> as the Headless accounts (only when changed; none left = file removed).</summary>
    public static void WriteIds(string path, IEnumerable<string> ids) => Sidecar.WriteIds(path, ids);
}
