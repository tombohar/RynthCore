using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace RynthCore.App;

/// <summary>
/// Where <see cref="DecalBridgeRegistration"/> reads and writes: the 32-bit view of HKLM,
/// HKCU, and %APPDATA%\RynthCore. <see cref="Real"/> is the machine. The offline tests
/// (tools\DecalBridgeCheckTests) use <see cref="ForTests"/>: a scratch key under
/// HKCU\Software\RynthCoreTests and a temp folder, so they never touch Decal's keys.
/// </summary>
internal sealed class DecalRegistryRoots
{
    private readonly Func<RegistryKey> _machine;
    private readonly Func<RegistryKey> _machineForWrite;
    private readonly Func<RegistryKey> _user;
    private readonly Func<bool> _decalInstalled;

    public bool Is64BitOs { get; }
    /// <summary>The user base as reg.exe names it ("HKCU", or the test key), for backups.</summary>
    public string UserRegPath { get; }
    /// <summary>%APPDATA%\RynthCore: the record file and the backups.</summary>
    public string DataDirectory { get; }

    private DecalRegistryRoots(Func<RegistryKey> machine, Func<RegistryKey> machineForWrite, Func<RegistryKey> user,
        Func<bool> decalInstalled, bool is64BitOs, string userRegPath, string dataDirectory)
    {
        _machine = machine;
        _machineForWrite = machineForWrite;
        _user = user;
        _decalInstalled = decalInstalled;
        Is64BitOs = is64BitOs;
        UserRegPath = userRegPath;
        DataDirectory = dataDirectory;
    }

    /// <summary>HKLM, 32-bit view (what the 32-bit client reads). Dispose it.</summary>
    public RegistryKey Machine() => _machine();
    /// <summary>HKLM for a write. Refuses while this process is still virtualized (the write would land in the per-user copy).</summary>
    public RegistryKey MachineForWrite() => _machineForWrite();
    /// <summary>HKCU (64-bit view on 64-bit Windows, where the VirtualStore lives). Dispose it.</summary>
    public RegistryKey User() => _user();
    public bool DecalInstalled() => _decalInstalled();

    public static DecalRegistryRoots Real { get; } = new(
        () => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32),
        () =>
        {
            if (RealRegistryView.ProcessVirtualized())
                throw new InvalidOperationException("UAC registry virtualization is still on for this process; " +
                    "writing HKLM now would create the per-user copy of Decal's settings instead, so nothing was written");
            return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        },
        () => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,
            Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default),
        DecalLocator.IsInstalled,
        Environment.Is64BitOperatingSystem,
        "HKCU",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RynthCore"));

    /// <summary>
    /// A scratch registry: HKCU\<paramref name="testKey"/>\Machine stands in for HKLM (32-bit
    /// view) and HKCU\<paramref name="testKey"/>\User for HKCU. <paramref name="machineWritable"/>
    /// false makes every HKLM write fail with "access denied", as for a non-elevated launcher.
    /// </summary>
    public static DecalRegistryRoots ForTests(string testKey, string dataDirectory, Func<bool> machineWritable)
    {
        if (!testKey.StartsWith(@"Software\RynthCoreTests\", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(@"Test registry roots must live under HKCU\Software\RynthCoreTests\.", nameof(testKey));
        RegistryKey Base(string part)
        {
            using RegistryKey hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            return hkcu.CreateSubKey(testKey + "\\" + part, writable: true);
        }
        return new DecalRegistryRoots(
            () => Base("Machine"),
            () => machineWritable() ? Base("Machine") : throw new UnauthorizedAccessException("test: HKLM is not writable"),
            () => Base("User"),
            () =>
            {
                using RegistryKey m = Base("Machine");
                using RegistryKey? agent = m.OpenSubKey(@"SOFTWARE\Decal\Agent");
                return agent?.GetValue("AgentPath") is string s && s.Length > 0;
            },
            is64BitOs: true,
            userRegPath: @"HKCU\" + testKey + @"\User",
            dataDirectory: dataDirectory);
    }
}

/// <summary>
/// Registers (and cleanly unregisters) the RynthCore Decal bridge - the small Decal
/// network filter in src\RynthCore.DecalBridge - in Decal's filter list. See
/// docs\DECAL_BRIDGE_PLAN.md, "Where Decal reads its filters" and "Opt-in, machine-wide only".
///
/// Machine-wide only (since 2026-10-10). The bridge goes into Decal's own list,
/// HKLM\SOFTWARE\(WOW6432Node\)Decal\NetworkFilters, which needs administrator rights once:
/// the launcher runs itself elevated with --decal-bridge-register-machine (one Windows prompt)
/// when the user turns on "Decal + RynthCore" or launches such an account. If that is declined,
/// Decal + RynthCore can't be used; there is no per-user fallback.
///
/// Why never per-user. acclient.exe is a legacy 32-bit program that Windows "virtualizes": its
/// reads of that HKLM key are served from the per-user copy
/// HKCU\Software\Classes\VirtualStore\MACHINE\SOFTWARE\WOW6432Node\Decal\NetworkFilters whenever
/// that copy exists, and Decal then sees ONLY the copy. Launchers before 2026-10-10 created that
/// copy when they couldn't write HKLM (mirroring HKLM's filters into it). Filters installed for
/// all users afterwards were hidden from plain Decal launches (a player's new plugins and
/// UtilityBelt's newer service vanished, and reinstalling didn't help). This class never creates
/// that copy now:
///   - a copy an older launcher created (the record file says so) is backed up and removed once
///     the machine-wide entry is in place, or at uninstall / when no account uses the mode;
///   - a copy that existed anyway (Decal writes one itself when run without administrator rights)
///     gets our entry too, because a virtualized client reads nothing else; Recovery Actions >
///     Repair Decal removes such copies after a backup (<see cref="RepairDecal"/>).
///
/// The record of an older launcher's copy is %APPDATA%\RynthCore\decal-bridge-registration.json
/// (read only now; deleted once the copy is gone). Every removal of a copy is preceded by
/// "reg export" to %APPDATA%\RynthCore\decal-backup-&lt;date&gt;.reg. Entries in a copy that are
/// neither ours nor in HKLM belong to someone else: the copy is then left in place and the
/// reason logged.
///
/// Opt-in only: nothing calls Register unless the user picked "Decal + RynthCore" for an
/// account (or ran the injector's --decal-bridge register), and the installer registers only
/// for a user who has such an account.
/// </summary>
internal static class DecalBridgeRegistration
{
    /// <summary>The bridge's fixed filter id (one entry, whatever the install path).</summary>
    public const string FilterGuid = "{5B3E0D57-2C41-4F8A-9D6E-8C1B70DECA11}";
    public const string FilterObject = "RynthCore.DecalBridge.BridgeFilter";
    public const string AssemblyFile = "RynthCore.DecalBridge.dll";
    public const string FilterName = "RynthCore Decal Bridge";
    /// <summary>Decal's .NET surrogate, the value every Decal.Adapter filter registers with.</summary>
    private const string DotNetSurrogate = "{71A69713-6593-47EC-0002-0000000DECA1}";

    private const string DecalFiltersSubKey = @"SOFTWARE\Decal\NetworkFilters";   // seen through the 32-bit view
    public const string StateFileName = "decal-bridge-registration.json";

    private static string StatePath(DecalRegistryRoots roots) => Path.Combine(roots.DataDirectory, StateFileName);

    /// <summary>HKCU path of the per-user copy the virtualized 32-bit client reads.</summary>
    public static string VirtualStoreFiltersSubKey => DecalBridgeCheck.VirtualStoreDecalPath(Environment.Is64BitOperatingSystem, "NetworkFilters");

    /// <summary>The per-user copies of Decal's whole key a virtualized program can leave behind (both spellings).</summary>
    public static readonly string[] VirtualStoreDecalKeys =
    {
        @"Software\Classes\VirtualStore\MACHINE\SOFTWARE\WOW6432Node\Decal",
        @"Software\Classes\VirtualStore\MACHINE\SOFTWARE\Decal",
    };

    /// <summary>The folder the launcher registers: &lt;install&gt;\DecalBridge.</summary>
    public static string DefaultBridgeDirectory => Path.Combine(AppContext.BaseDirectory, "DecalBridge");

    public sealed class Status
    {
        public bool DecalInstalled;
        public bool PerUserKeyExists;
        public bool PerUserKeyCreatedByUs;
        public bool RegisteredPerUser;
        public bool RegisteredMachineWide;
        public bool Enabled;
        public bool MachineEnabled;
        public string? RegisteredPath;
        public bool DllPresent;
        /// <summary>A client started by this process gets UAC registry virtualization (reads the per-user copy when it exists).</summary>
        public bool ClientVirtualized = true;
        public string ClientReason = string.Empty;
        /// <summary>HKLM filters a Decal client of this user can't see (hidden by the per-user key).</summary>
        public List<string> HiddenMachineFilters = new();

        /// <summary>What a Decal client started now will load (see DecalBridgeCheck for the full check).</summary>
        public bool EffectiveForClients => ClientVirtualized && PerUserKeyExists
            ? RegisteredPerUser && Enabled
            : RegisteredMachineWide && MachineEnabled;

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append($"Decal installed: {DecalInstalled}. ");
            sb.Append($"Bridge registered for clients started now: {(EffectiveForClients ? "yes" : "no")}");
            if (RegisteredPerUser) sb.Append(" (per-user entry present)");
            if (RegisteredMachineWide) sb.Append(" (machine-wide entry present)");
            if (RegisteredPath != null) sb.Append($", path {RegisteredPath}{(DllPresent ? "" : " (DLL MISSING)")}");
            sb.Append('.');
            if (ClientReason.Length > 0) sb.Append($" Client: {ClientReason}.");
            if (PerUserKeyExists)
                sb.Append($" Per-user filter list: {(PerUserKeyCreatedByUs ? "created by an earlier RynthCore" : "not created by RynthCore")}.");
            if (HiddenMachineFilters.Count > 0)
                sb.Append($" Hidden from Decal clients (in HKLM, not in the per-user list): {string.Join(", ", HiddenMachineFilters)}.");
            return sb.ToString();
        }
    }

    /// <summary>
    /// The record launchers before 2026-10-10 wrote when they created the per-user copy. Read
    /// only now: it tells "our copy" from one Decal made, and which entries we mirrored.
    /// </summary>
    private sealed class State
    {
        public bool CreatedFiltersKey { get; set; }
        public string BridgePath { get; set; } = string.Empty;
        public DateTime RegisteredUtc { get; set; }
        /// <summary>Mirrored HKLM subkey -> fingerprint of the copy as written.</summary>
        public Dictionary<string, string> MirroredKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Mirrored values of the NetworkFilters key itself -> fingerprint.</summary>
        public Dictionary<string, string> MirroredValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    // ── status ───────────────────────────────────────────────────────────────

    public static Status GetStatus(string? acClientPath = null)
    {
        bool virt = RealDecalCheckHost.PredictClientVirtualized(acClientPath ?? DecalLocator.TryGetDecalAcClientPath(), out string reason, out _);
        Status s;
        using (RealRegistryView.Enter()) s = GetStatusCore(DecalRegistryRoots.Real);
        s.ClientVirtualized = virt;
        s.ClientReason = reason;
        return s;
    }

    internal static Status GetStatusCore(DecalRegistryRoots roots)
    {
        var s = new Status { DecalInstalled = roots.DecalInstalled() };
        State? state = LoadState(roots);
        using RegistryKey? machine = OpenMachineFilters(roots);
        using RegistryKey? user = OpenUserFilters(roots, writable: false);
        s.PerUserKeyExists = user != null;
        s.PerUserKeyCreatedByUs = user != null && state?.CreatedFiltersKey == true;

        using (RegistryKey? m = machine?.OpenSubKey(FilterGuid))
        {
            if (m != null && IsOurs(m))
            {
                s.RegisteredMachineWide = true;
                s.MachineEnabled = Enabled(m);
                if (user == null) ReadEntry(m, s);
            }
        }
        using (RegistryKey? u = user?.OpenSubKey(FilterGuid))
        {
            if (u != null && IsOurs(u))
            {
                s.RegisteredPerUser = true;
                ReadEntry(u, s);
            }
        }
        if (machine != null && user != null)
        {
            HashSet<string> seen = new(user.GetSubKeyNames(), StringComparer.OrdinalIgnoreCase);
            foreach (string name in machine.GetSubKeyNames())
                if (!seen.Contains(name))
                    s.HiddenMachineFilters.Add(DescribeFilter(machine, name));
        }
        return s;
    }

    // ── register ─────────────────────────────────────────────────────────────

    public enum Outcome { Registered, NeedsAdmin, Failed }

    /// <summary>
    /// Registers the bridge in Decal's machine-wide filter list, then verifies it with
    /// <see cref="DecalBridgeCheck"/>. <see cref="Outcome.NeedsAdmin"/> when this process may not
    /// write HKLM: the caller runs <c>--decal-bridge-register-machine</c> elevated
    /// (<see cref="RunElevated"/>) and calls this again, which then finishes the per-user side
    /// (an older launcher's per-user copy removed after a backup; our entry added to a per-user
    /// copy that exists anyway). Never creates the per-user copy. Idempotent. Also removes a
    /// "downloaded from the internet" mark from the bridge DLL.
    /// </summary>
    public static Outcome RegisterForClients(string bridgeDirectory, string? acClientPath, string? engineJsonPath,
        out string report, out DecalBridgeCheck.Result? check)
    {
        check = null;
        var log = new List<string>();
        string dir;
        try { dir = Path.GetFullPath(bridgeDirectory).TrimEnd('\\'); }
        catch (Exception ex) { report = $"Bad bridge folder '{bridgeDirectory}': {ex.Message}"; return Outcome.Failed; }

        string dll = Path.Combine(dir, AssemblyFile);
        if (File.Exists(dll) && File.Exists(dll + ":Zone.Identifier"))
            log.Add(RealDecalCheckHost.RemoveZoneIdentifier(dll)
                ? "removed the 'downloaded from the internet' mark from the bridge DLL"
                : "could not remove the 'downloaded from the internet' mark from the bridge DLL");

        Outcome outcome;
        using (RealRegistryView.Enter())
            outcome = RegisterCore(DecalRegistryRoots.Real, dir, log);
        if (outcome == Outcome.NeedsAdmin)
        {
            log.Add("Decal + RynthCore needs the bridge in Decal's filter list for all users on this PC; adding it needs administrator rights once.");
            report = string.Join(" ", log);
            return Outcome.NeedsAdmin;
        }

        check = RealDecalCheckHost.Check(dir, acClientPath, engineJsonPath);
        log.Add(check.Summary);
        report = string.Join(" ", log);
        if (outcome != Outcome.Registered) return Outcome.Failed;
        if (check.Blocking) return check.NeedsMachineWideRegistration ? Outcome.NeedsAdmin : Outcome.Failed;
        return Outcome.Registered;
    }

    /// <summary>
    /// <see cref="RegisterForClients"/> for Decal's own acclient.exe (PortalPath). False (with
    /// the reason) when it needs administrator rights or the check fails.
    /// </summary>
    public static bool Register(string bridgeDirectory, out string report) =>
        RegisterForClients(bridgeDirectory, DecalLocator.TryGetDecalAcClientPath(), null, out report, out _) == Outcome.Registered;

    /// <summary>The registration itself, against <paramref name="roots"/> (the tests pass a scratch registry).</summary>
    internal static Outcome RegisterCore(DecalRegistryRoots roots, string dir, List<string> log)
    {
        if (!roots.DecalInstalled())
        {
            log.Add("Decal is not installed (HKLM\\SOFTWARE\\Decal\\Agent) - nothing registered.");
            return Outcome.Failed;
        }
        if (!File.Exists(Path.Combine(dir, AssemblyFile)))
        {
            log.Add($"{AssemblyFile} not found in {dir} - nothing registered.");
            return Outcome.Failed;
        }
        Outcome mw = MachineWideCore(roots, dir, log);
        if (mw != Outcome.Registered)
            return mw;
        return PerUserCore(roots, dir, log) ? Outcome.Registered : Outcome.Failed;
    }

    /// <summary>
    /// Adds the bridge to the machine-wide list, HKLM\SOFTWARE\(WOW6432Node\)Decal\NetworkFilters
    /// (32-bit view). Needs administrator rights: run elevated (--decal-bridge-register-machine).
    /// Virtualization is off while it writes, so a non-elevated process gets "access denied"
    /// (NeedsAdmin) instead of a silent write into the per-user copy. Idempotent.
    /// </summary>
    public static Outcome RegisterMachineWide(string bridgeDirectory, out string report)
    {
        var log = new List<string>();
        using (RealRegistryView.Enter())
        {
            DecalRegistryRoots roots = DecalRegistryRoots.Real;
            if (!roots.DecalInstalled())
            {
                report = "Decal is not installed (HKLM\\SOFTWARE\\Decal\\Agent) - nothing registered.";
                return Outcome.Failed;
            }
            string dir;
            try { dir = Path.GetFullPath(bridgeDirectory).TrimEnd('\\'); }
            catch (Exception ex) { report = $"Bad bridge folder '{bridgeDirectory}': {ex.Message}"; return Outcome.Failed; }
            if (!File.Exists(Path.Combine(dir, AssemblyFile)))
            {
                report = $"{AssemblyFile} not found in {dir} - nothing registered.";
                return Outcome.Failed;
            }
            Outcome outcome = MachineWideCore(roots, dir, log);
            report = string.Join(" ", log);
            return outcome;
        }
    }

    private static Outcome MachineWideCore(DecalRegistryRoots roots, string dir, List<string> log)
    {
        try
        {
            if (MachineEntryCurrent(roots, dir))
            {
                log.Add($"Decal bridge already in Decal's filter list for all users ({dir}).");
                return Outcome.Registered;
            }
            var changes = new List<string>();
            using RegistryKey hklm = roots.MachineForWrite();
            using RegistryKey filters = hklm.CreateSubKey(DecalFiltersSubKey, writable: true);
            using (RegistryKey? existing = filters.OpenSubKey(FilterGuid))
            {
                if (existing != null && !IsOurs(existing))
                {
                    log.Add($"Another filter uses the bridge's id {FilterGuid} in the machine-wide list - nothing changed.");
                    return Outcome.Failed;
                }
            }
            using (RegistryKey entry = filters.CreateSubKey(FilterGuid, writable: true))
                WriteEntry(entry, dir, changes);
            log.Add(changes.Count == 0
                ? $"Decal bridge already registered for all users ({dir}); nothing changed."
                : $"Decal bridge registered for all users ({dir}): {string.Join("; ", changes)}.");
            return Outcome.Registered;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            log.Add("Registering the Decal bridge for all users needs administrator rights.");
            return Outcome.NeedsAdmin;
        }
        catch (Exception ex)
        {
            log.Add($"Decal bridge registration for all users failed: {Describe(ex)}");
            return Outcome.Failed;
        }
    }

    /// <summary>
    /// The per-user side, after the machine-wide entry is in place. Never creates the per-user copy.
    ///   - A copy an older launcher created (record file): backed up and removed, so Decal reads
    ///     the machine-wide list again (left in place, and logged, when it holds entries that are
    ///     neither ours nor in HKLM).
    ///   - A copy that exists anyway: our entry goes into it, because a virtualized client reads
    ///     nothing else (Repair Decal removes such copies).
    /// </summary>
    private static bool PerUserCore(DecalRegistryRoots roots, string dir, List<string> log)
    {
        bool ok = true;
        State? state = LoadState(roots);
        bool copyExists;
        using (RegistryKey? u = OpenUserFilters(roots, writable: false)) copyExists = u != null;
        if (state?.CreatedFiltersKey == true)
        {
            if (!copyExists)
                DeleteState(roots);   // someone removed the copy already
            else if (MachineEntryCurrent(roots, dir))
            {
                // Not fatal: the copy then stays as it is and gets our entry below.
                var failed = new List<string>();
                CleanUpOwnCopy(roots, state, removeBridgeIfKept: false, log, failed, out _);
                log.AddRange(failed);
            }
        }

        try
        {
            using RegistryKey? user = OpenUserFilters(roots, writable: true);
            if (user == null)
                return ok;
            using (RegistryKey? existing = user.OpenSubKey(FilterGuid))
            {
                if (existing != null && !IsOurs(existing))
                {
                    log.Add($"Another filter uses the bridge's id {FilterGuid} in your per-user copy of Decal's filter list - nothing changed there.");
                    return false;
                }
            }
            var changes = new List<string>();
            using (RegistryKey entry = user.CreateSubKey(FilterGuid, writable: true))
                WriteEntry(entry, dir, changes);
            if (changes.Count > 0)
                log.Add($"Also added to your Windows user's existing copy of Decal's filter list (HKCU\\{DecalBridgeCheck.VirtualStoreDecalPath(roots.Is64BitOs, "NetworkFilters")}), " +
                    "which Decal reads instead of the list for all users when AC runs without administrator rights. Recovery Actions > Repair Decal removes that copy.");
        }
        catch (Exception ex)
        {
            log.Add($"Adding the bridge to your per-user copy of Decal's filter list failed: {Describe(ex)}");
            ok = false;
        }
        return ok;
    }

    /// <summary>
    /// The installer's per-user half (installer\RynthCore.iss, as the installing user, after
    /// the elevated <see cref="RegisterMachineWide"/>, only for a user with a Decal + RynthCore
    /// account). The per-user side of <see cref="RegisterCore"/>: never creates the per-user copy.
    /// </summary>
    public static bool InstallPerUser(string bridgeDirectory, out string report)
    {
        try
        {
            using (RealRegistryView.Enter())
            {
                DecalRegistryRoots roots = DecalRegistryRoots.Real;
                if (!roots.DecalInstalled())
                {
                    report = "Decal is not installed - nothing to do for this user.";
                    return true;
                }
                string dir = Path.GetFullPath(bridgeDirectory).TrimEnd('\\');
                if (!File.Exists(Path.Combine(dir, AssemblyFile)))
                {
                    report = $"{AssemblyFile} not found in {dir} - nothing written.";
                    return false;
                }
                var log = new List<string>();
                bool ok = PerUserCore(roots, dir, log);
                report = log.Count == 0 ? "No per-user copy of Decal's filter list - nothing to do for this user." : string.Join(" ", log);
                return ok;
            }
        }
        catch (Exception ex)
        {
            report = $"Per-user Decal bridge step failed: {Describe(ex)}";
            return false;
        }
    }

    // ── unregister ───────────────────────────────────────────────────────────

    /// <summary>
    /// The machine-wide list holds our entry. With <paramref name="onlyForDir"/>, only an entry
    /// that points at that folder (or at a DLL that is gone): another install's entry is left alone.
    /// </summary>
    public static bool MachineEntryIsOurs(string? onlyForDir = null)
    {
        using (RealRegistryView.Enter())
        {
            try { return MachineEntryIsOursCore(DecalRegistryRoots.Real, onlyForDir); }
            catch { return false; }
        }
    }

    internal static bool MachineEntryIsOursCore(DecalRegistryRoots roots, string? onlyForDir)
    {
        using RegistryKey? machine = OpenMachineFilters(roots);
        using RegistryKey? m = machine?.OpenSubKey(FilterGuid);
        return m != null && IsOurs(m) && (onlyForDir == null || PointsAt(m, onlyForDir));
    }

    /// <summary>Removes our machine-wide entry (only ours; with <paramref name="onlyForDir"/>, only one for that folder). Needs administrator rights.</summary>
    public static Outcome UnregisterMachineWide(out string report, string? onlyForDir = null)
    {
        using (RealRegistryView.Enter())
        {
            var log = new List<string>();
            Outcome o = UnregisterMachineWideCore(DecalRegistryRoots.Real, onlyForDir, log);
            report = string.Join(" ", log);
            return o;
        }
    }

    internal static Outcome UnregisterMachineWideCore(DecalRegistryRoots roots, string? onlyForDir, List<string> log)
    {
        try
        {
            if (!MachineEntryIsOursCore(roots, onlyForDir))
            {
                log.Add(onlyForDir == null
                    ? "No machine-wide Decal bridge entry; nothing to remove."
                    : $"No machine-wide Decal bridge entry for {onlyForDir}; nothing to remove.");
                return Outcome.Registered;
            }
            using RegistryKey hklm = roots.MachineForWrite();
            using RegistryKey filters = hklm.OpenSubKey(DecalFiltersSubKey, writable: true)
                ?? throw new IOException("Decal's filter list disappeared");
            filters.DeleteSubKeyTree(FilterGuid, throwOnMissingSubKey: false);
            log.Add("Removed the machine-wide Decal bridge entry.");
            return Outcome.Registered;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            log.Add("Removing the machine-wide Decal bridge entry needs administrator rights.");
            return Outcome.NeedsAdmin;
        }
        catch (Exception ex)
        {
            log.Add($"Removing the machine-wide Decal bridge entry failed: {Describe(ex)}");
            return Outcome.Failed;
        }
    }

    /// <summary>
    /// Runs <paramref name="exePath"/> elevated (Windows shows its administrator prompt) and
    /// waits for it. NeedsAdmin when the person said no at the prompt.
    /// </summary>
    public static Outcome RunElevated(string exePath, string arguments, out string report)
    {
        try
        {
            var psi = new ProcessStartInfo(exePath, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using Process? p = Process.Start(psi);
            if (p == null)
            {
                report = "Windows did not start the administrator step.";
                return Outcome.Failed;
            }
            if (!p.WaitForExit(120_000))
            {
                report = "The administrator step did not finish within 2 minutes.";
                return Outcome.Failed;
            }
            report = p.ExitCode == 0 ? "The administrator step finished." : $"The administrator step failed (exit code {p.ExitCode}).";
            return p.ExitCode == 0 ? Outcome.Registered : Outcome.Failed;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            report = "Cancelled at the Windows administrator prompt.";
            return Outcome.NeedsAdmin;
        }
        catch (Exception ex)
        {
            report = $"Could not start the administrator step: {ex.Message}";
            return Outcome.Failed;
        }
    }

    /// <summary>
    /// Something of ours is on the per-user side for <see cref="Unregister"/> to remove: our
    /// entry in a per-user copy, an older launcher's copy that can go, or a stale record.
    /// The launcher asks this at start so it never logs "nothing to remove" on every start.
    /// </summary>
    public static bool PerUserCleanupNeeded()
    {
        using (RealRegistryView.Enter())
        {
            try { return PerUserCleanupNeededCore(DecalRegistryRoots.Real); }
            catch { return false; }
        }
    }

    internal static bool PerUserCleanupNeededCore(DecalRegistryRoots roots)
    {
        State? state = LoadState(roots);
        using RegistryKey? user = OpenUserFilters(roots, writable: false);
        if (user == null)
            return state != null;
        foreach (string name in user.GetSubKeyNames())
        {
            using RegistryKey? k = user.OpenSubKey(name);
            if (k != null && IsOurs(k)) return true;
        }
        return state?.CreatedFiltersKey == true && ForeignEntries(roots, user, state).Count == 0;
    }

    /// <summary>
    /// The uninstaller's (and the launcher's "no account uses Decal + RynthCore") per-user
    /// cleanup. Removes our entry from any per-user copy of Decal's filter list. A copy an older
    /// launcher created (record file) goes entirely, whatever is in it now - our entries, the
    /// copies of HKLM filters it mirrored (changed or not; HKLM keeps the originals) - after a
    /// backup to %APPDATA%\RynthCore\decal-backup-&lt;date&gt;.reg. When that copy holds entries
    /// that are neither ours nor in HKLM, they belong to someone else: the copy stays (only our
    /// entry goes) and the reason is logged; the record is kept for a later attempt.
    /// The machine-wide entry is <see cref="UnregisterMachineWide"/>'s job.
    /// </summary>
    public static bool Unregister(out string report)
    {
        using (RealRegistryView.Enter()) return UnregisterCore(DecalRegistryRoots.Real, out report);
    }

    internal static bool UnregisterCore(DecalRegistryRoots roots, out string report)
    {
        var log = new List<string>();
        var failed = new List<string>();
        try
        {
            State? state = LoadState(roots);
            bool copyExists;
            using (RegistryKey? u = OpenUserFilters(roots, writable: false)) copyExists = u != null;
            if (state?.CreatedFiltersKey == true && copyExists)
            {
                CleanUpOwnCopy(roots, state, removeBridgeIfKept: true, log, failed, out bool removed);
                if (!removed && failed.Count == 0)
                    log.Add($"kept {StatePath(roots)} for a later cleanup");
            }
            else
            {
                if (copyExists)
                    RemoveBridgeEntries(roots, log, failed);
                if (failed.Count == 0 && state != null)
                    DeleteState(roots);
            }

            if (failed.Count > 0)
            {
                report = $"Decal bridge unregistration incomplete: {string.Join("; ", failed)}" +
                    (log.Count > 0 ? $" (done: {string.Join("; ", log)})" : "");
                return false;
            }
            report = log.Count == 0 ? "Decal bridge was not registered for this user; nothing to remove." : string.Join("; ", log) + ".";
            return true;
        }
        catch (Exception ex)
        {
            report = $"Decal bridge unregistration failed: {Describe(ex)}" +
                (log.Count > 0 ? $" (done before the failure: {string.Join("; ", log)})" : "");
            return false;
        }
    }

    /// <summary>
    /// An older launcher's per-user copy (state.CreatedFiltersKey): removed after a backup
    /// unless it holds foreign entries. <paramref name="removeBridgeIfKept"/>: when it stays,
    /// take our entry out of it (unregister) or leave it (register: a virtualized client reads
    /// nothing but the copy). Returns false when something failed.
    /// </summary>
    private static bool CleanUpOwnCopy(DecalRegistryRoots roots, State state, bool removeBridgeIfKept,
        List<string> log, List<string> failed, out bool removed)
    {
        removed = false;
        string vs = DecalBridgeCheck.VirtualStoreDecalPath(roots.Is64BitOs, "NetworkFilters");
        List<string> foreign;
        using (RegistryKey? user = OpenUserFilters(roots, writable: false))
        {
            if (user == null)
            {
                DeleteState(roots);
                return true;
            }
            foreign = ForeignEntries(roots, user, state);
        }

        if (foreign.Count > 0)
        {
            log.Add($"left HKCU\\{vs} (a per-user copy of Decal's filter list an earlier RynthCore created) in place: " +
                $"it holds entries RynthCore didn't add and that aren't in the list for all users ({string.Join(", ", foreign)}). " +
                "Recovery Actions > Repair Decal removes it after a backup");
            if (removeBridgeIfKept)
                RemoveBridgeEntries(roots, log, failed);
            return failed.Count == 0;
        }

        if (!BackupUserKeys(roots, new[] { vs }, out string? backup, out string error))
        {
            failed.Add($"left HKCU\\{vs} in place: the backup failed ({error})");
            if (removeBridgeIfKept)
                RemoveBridgeEntries(roots, log, failed);
            return false;
        }
        try
        {
            using (RegistryKey hkcu = roots.User())
                hkcu.DeleteSubKeyTree(vs, throwOnMissingSubKey: false);
            DeleteState(roots);
            removed = true;
            log.Add($"removed HKCU\\{vs}, the per-user copy of Decal's filter list an earlier RynthCore created " +
                $"(Decal reads the list for all users again; backup: {backup})");
            return true;
        }
        catch (Exception ex)
        {
            failed.Add($"removing HKCU\\{vs} failed: {Describe(ex)} (backup: {backup})");
            return false;
        }
    }

    /// <summary>Entries in an older launcher's copy that are neither ours, nor mirrored, nor in HKLM.</summary>
    private static List<string> ForeignEntries(DecalRegistryRoots roots, RegistryKey user, State state)
    {
        HashSet<string> machineKeys = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> machineValues = new(StringComparer.OrdinalIgnoreCase);
        using (RegistryKey? machine = OpenMachineFilters(roots))
        {
            if (machine != null)
            {
                machineKeys.UnionWith(machine.GetSubKeyNames());
                machineValues.UnionWith(machine.GetValueNames());
            }
        }
        var foreign = new List<string>();
        foreach (string name in user.GetSubKeyNames())
        {
            if (machineKeys.Contains(name) || state.MirroredKeys.ContainsKey(name))
                continue;
            using RegistryKey? k = user.OpenSubKey(name);
            if (k != null && IsOurs(k))
                continue;
            foreign.Add(k?.GetValue("") is string friendly && friendly.Length > 0 ? $"{friendly} {name}" : name);
        }
        foreach (string name in user.GetValueNames())
        {
            if (name.Length == 0 || machineValues.Contains(name) || state.MirroredValues.ContainsKey(name))
                continue;
            foreign.Add($"value '{name}'");
        }
        return foreign;
    }

    private static void RemoveBridgeEntries(DecalRegistryRoots roots, List<string> log, List<string> failed)
    {
        using RegistryKey? user = OpenUserFilters(roots, writable: true);
        if (user == null) return;
        foreach (string name in user.GetSubKeyNames())
        {
            try
            {
                bool ours;
                using (RegistryKey? k = user.OpenSubKey(name))
                    ours = k != null && IsOurs(k);
                if (!ours) continue;
                user.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
                log.Add($"removed bridge entry {name} from the per-user copy of Decal's filter list");
            }
            catch (Exception ex) { failed.Add($"{name}: {Describe(ex)}"); }
        }
    }

    // ── Repair Decal ─────────────────────────────────────────────────────────

    public sealed class RepairResult
    {
        public bool Ok;
        public string? BackupPath;
        /// <summary>The keys removed, as HKCU\... paths.</summary>
        public List<string> Removed { get; } = new();
        public string Report = string.Empty;
    }

    /// <summary>The per-user copies of Decal's settings that exist now, as HKCU\... paths (read only).</summary>
    public static List<string> FindVirtualStoreDecalKeys()
    {
        using (RealRegistryView.Enter()) return FindVirtualStoreDecalKeysCore(DecalRegistryRoots.Real).Select(k => @"HKCU\" + k).ToList();
    }

    internal static List<string> FindVirtualStoreDecalKeysCore(DecalRegistryRoots roots)
    {
        var found = new List<string>();
        using RegistryKey hkcu = roots.User();
        foreach (string k in VirtualStoreDecalKeys)
        {
            using RegistryKey? key = hkcu.OpenSubKey(k);
            if (key != null) found.Add(k);
        }
        return found;
    }

    /// <summary>
    /// Recovery Actions > Repair Decal: Decal is reading an old per-user copy of its settings
    /// (UAC virtualization; made by an earlier RynthCore or by Decal itself). Backs up every
    /// HKCU\Software\Classes\VirtualStore\MACHINE\SOFTWARE\(WOW6432Node\)Decal key to one .reg
    /// file in %APPDATA%\RynthCore, then deletes them, so Decal sees every installed plugin and
    /// filter again. Deletes nothing when the backup fails. The caller re-registers the bridge
    /// machine-wide afterwards when an account uses Decal + RynthCore.
    /// </summary>
    public static RepairResult RepairDecal()
    {
        using (RealRegistryView.Enter()) return RepairDecalCore(DecalRegistryRoots.Real);
    }

    internal static RepairResult RepairDecalCore(DecalRegistryRoots roots)
    {
        var r = new RepairResult();
        List<string> keys = FindVirtualStoreDecalKeysCore(roots);
        if (keys.Count == 0)
        {
            r.Ok = true;
            r.Report = "Decal has no per-user copy of its settings; it already reads the settings for all users. Nothing changed.";
            return r;
        }
        if (!BackupUserKeys(roots, keys, out r.BackupPath, out string error))
        {
            r.Report = $"Repair Decal stopped before deleting anything: the backup failed ({error}).";
            return r;
        }
        var failed = new List<string>();
        using (RegistryKey hkcu = roots.User())
        {
            foreach (string k in keys)
            {
                try
                {
                    hkcu.DeleteSubKeyTree(k, throwOnMissingSubKey: false);
                    r.Removed.Add(@"HKCU\" + k);
                }
                catch (Exception ex) { failed.Add($"HKCU\\{k}: {Describe(ex)}"); }
            }
        }
        if (failed.Count == 0)
            DeleteState(roots);   // an earlier RynthCore's copy is gone with them
        r.Ok = failed.Count == 0;
        r.Report = (r.Removed.Count > 0 ? $"Removed {string.Join(", ", r.Removed)}. " : "") +
            (failed.Count > 0 ? $"Could not remove: {string.Join("; ", failed)}. " : "") +
            $"Backup: {r.BackupPath} (double-click it to put the keys back).";
        return r;
    }

    // ── backups ──────────────────────────────────────────────────────────────

    /// <summary>
    /// "reg export" of the given HKCU subkeys into one .reg file,
    /// &lt;DataDirectory&gt;\decal-backup-&lt;date&gt;.reg. False (nothing written) when any export fails.
    /// </summary>
    internal static bool BackupUserKeys(DecalRegistryRoots roots, IReadOnlyList<string> subKeys, out string? backupPath, out string error)
    {
        backupPath = null;
        error = string.Empty;
        try
        {
            Directory.CreateDirectory(roots.DataDirectory);
            string stamp = DateTime.Now.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture);
            string path = Path.Combine(roots.DataDirectory, $"decal-backup-{stamp}.reg");
            for (int n = 2; File.Exists(path); n++)
                path = Path.Combine(roots.DataDirectory, $"decal-backup-{stamp}-{n}.reg");

            var sb = new StringBuilder();
            foreach (string sub in subKeys)
            {
                string tmp = Path.Combine(roots.DataDirectory, $"decal-backup-{Guid.NewGuid():N}.tmp");
                try
                {
                    if (!RegExport(roots.UserRegPath + "\\" + sub, tmp, roots.Is64BitOs, out error))
                        return false;
                    string text = File.ReadAllText(tmp);   // reg.exe writes UTF-16 with a BOM
                    if (sb.Length > 0)
                    {
                        int nl = text.IndexOf('\n');   // one header line per file
                        text = nl >= 0 ? text.Substring(nl + 1) : string.Empty;
                    }
                    sb.Append(text);
                    if (!text.EndsWith("\n", StringComparison.Ordinal)) sb.Append("\r\n");
                }
                finally
                {
                    try { File.Delete(tmp); } catch { }
                }
            }
            File.WriteAllText(path, sb.ToString(), Encoding.Unicode);
            backupPath = path;
            return true;
        }
        catch (Exception ex)
        {
            error = Describe(ex);
            return false;
        }
    }

    private static bool RegExport(string key, string file, bool is64BitOs, out string error)
    {
        error = string.Empty;
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "reg.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("export");
        psi.ArgumentList.Add(key);
        psi.ArgumentList.Add(file);
        psi.ArgumentList.Add("/y");
        if (is64BitOs) psi.ArgumentList.Add("/reg:64");   // the launcher is 32-bit; the VirtualStore is in the 64-bit view
        using Process? p = Process.Start(psi);
        if (p == null)
        {
            error = "reg.exe did not start";
            return false;
        }
        var stdout = p.StandardOutput.ReadToEndAsync();
        string stderr = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(30_000))
        {
            try { p.Kill(); } catch { }
            error = "reg export did not finish within 30 seconds";
            return false;
        }
        _ = stdout.Result;
        if (p.ExitCode != 0 || !File.Exists(file))
        {
            error = $"reg export {key} failed (exit code {p.ExitCode}): {stderr.Trim()}";
            return false;
        }
        return true;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    // Exception type, message and the innermost frame, for a one-line report.
    private static string Describe(Exception ex)
    {
        string? frame = ex.StackTrace?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        return $"{ex.GetType().Name}: {ex.Message}" + (frame != null ? $" [{frame}]" : "");
    }

    /// <summary>A stable text form of a key's values and subkeys (tests compare snapshots with it).</summary>
    internal static string Fingerprint(RegistryKey key)
    {
        var sb = new StringBuilder();
        foreach (string name in key.GetValueNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            object? v = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (v == null) continue;
            RegistryValueKind kind;
            try { kind = key.GetValueKind(name); }
            catch (IOException) { continue; }   // removed between the listing and the read
            sb.Append(name.ToLowerInvariant()).Append('=').Append(ValuePrint(kind, v)).Append(';');
        }
        foreach (string sub in key.GetSubKeyNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            using RegistryKey? s = key.OpenSubKey(sub);
            if (s != null) sb.Append('[').Append(sub.ToLowerInvariant()).Append(':').Append(Fingerprint(s)).Append(']');
        }
        return sb.ToString();
    }

    private static string ValuePrint(RegistryValueKind kind, object v) => kind + ":" + v switch
    {
        byte[] b => Convert.ToHexString(b),
        string[] a => string.Join("\u0001", a),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    private static RegistryKey? OpenUserFilters(DecalRegistryRoots roots, bool writable)
    {
        using RegistryKey hkcu = roots.User();
        return hkcu.OpenSubKey(DecalBridgeCheck.VirtualStoreDecalPath(roots.Is64BitOs, "NetworkFilters"), writable);
    }

    private static RegistryKey? OpenMachineFilters(DecalRegistryRoots roots)
    {
        using RegistryKey hklm = roots.Machine();
        return hklm.OpenSubKey(DecalFiltersSubKey);
    }

    /// <summary>HKLM holds our entry, enabled, for <paramref name="dir"/>, with every value as we write it.</summary>
    private static bool MachineEntryCurrent(DecalRegistryRoots roots, string dir)
    {
        using RegistryKey? machine = OpenMachineFilters(roots);
        using RegistryKey? m = machine?.OpenSubKey(FilterGuid);
        if (m == null || !IsOurs(m) || !Enabled(m)) return false;
        return string.Equals((m.GetValue("Path") as string)?.TrimEnd('\\'), dir, StringComparison.OrdinalIgnoreCase) &&
               Equals(m.GetValue(""), FilterName) &&
               Equals(m.GetValue("Assembly"), AssemblyFile) &&
               string.Equals(m.GetValue("Surrogate") as string, DotNetSurrogate, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PointsAt(RegistryKey entry, string dir)
    {
        string? path = (entry.GetValue("Path") as string)?.TrimEnd('\\');
        if (path == null) return true;
        if (string.Equals(path, dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return true;
        string file = entry.GetValue("Assembly") as string ?? AssemblyFile;
        try { return !File.Exists(Path.Combine(path, file)); }   // a dangling entry is nobody's
        catch { return false; }
    }

    private static void WriteEntry(RegistryKey entry, string dir, List<string> changes)
    {
        SetIfDifferent(entry, "", FilterName, RegistryValueKind.String, changes);
        SetIfDifferent(entry, "Enabled", 1, RegistryValueKind.DWord, changes);
        SetIfDifferent(entry, "Assembly", AssemblyFile, RegistryValueKind.String, changes);
        SetIfDifferent(entry, "Path", dir, RegistryValueKind.String, changes);
        SetIfDifferent(entry, "Object", FilterObject, RegistryValueKind.String, changes);
        SetIfDifferent(entry, "Surrogate", DotNetSurrogate, RegistryValueKind.String, changes);
    }

    private static bool IsOurs(RegistryKey k) =>
        string.Equals(k.GetValue("Object") as string, FilterObject, StringComparison.Ordinal);

    private static bool Enabled(RegistryKey k) => k.GetValue("Enabled") is int e && e != 0;

    private static void ReadEntry(RegistryKey k, Status s)
    {
        s.Enabled = Enabled(k);
        s.RegisteredPath = k.GetValue("Path") as string;
        s.DllPresent = s.RegisteredPath != null && File.Exists(Path.Combine(s.RegisteredPath, AssemblyFile));
    }

    private static string DescribeFilter(RegistryKey parent, string name)
    {
        using RegistryKey? k = parent.OpenSubKey(name);
        return k?.GetValue("") is string friendly && friendly.Length > 0 ? friendly : name;
    }

    private static void SetIfDifferent(RegistryKey key, string name, object value, RegistryValueKind kind, List<string> log)
    {
        object? have = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (have != null && key.GetValueKind(name) == kind && Equals(have, value))
            return;
        key.SetValue(name, value, kind);
        log.Add($"set {(name.Length == 0 ? "(default)" : name)}");
    }

    private static State? LoadState(DecalRegistryRoots roots)
    {
        try
        {
            string path = StatePath(roots);
            if (!File.Exists(path)) return null;
            State? s = JsonSerializer.Deserialize<State>(File.ReadAllText(path));
            if (s == null) return null;
            // Dictionary comparers don't survive serialization.
            s.MirroredKeys = new Dictionary<string, string>(s.MirroredKeys ?? new(), StringComparer.OrdinalIgnoreCase);
            s.MirroredValues = new Dictionary<string, string>(s.MirroredValues ?? new(), StringComparer.OrdinalIgnoreCase);
            return s;
        }
        catch
        {
            return null;
        }
    }

    private static void DeleteState(DecalRegistryRoots roots)
    {
        try
        {
            string path = StatePath(roots);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }
}
