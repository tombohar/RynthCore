using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using RynthCore.App;

namespace DecalBridgeCheckTests;

/// <summary>
/// DecalBridgeRegistration (register / unregister / Repair Decal) against a scratch registry:
/// HKCU\Software\RynthCoreTests\&lt;run&gt;\Machine stands in for HKLM (32-bit view) and
/// ...\User for HKCU (DecalRegistryRoots.ForTests), with a temp folder for %APPDATA%\RynthCore.
/// Every scenario deletes its scratch key and folder. The real Decal keys and the real
/// VirtualStore copies are only read, before and after, to prove nothing changed there.
/// </summary>
internal static partial class Program
{
    private static readonly string VsNetworkFilters = DecalBridgeCheck.VirtualStoreDecalPath(true, "NetworkFilters");
    private const string VsDecalWow = @"Software\Classes\VirtualStore\MACHINE\SOFTWARE\WOW6432Node\Decal";
    private const string VsDecalPlain = @"Software\Classes\VirtualStore\MACHINE\SOFTWARE\Decal";
    private const string MachineFilters = @"SOFTWARE\Decal\NetworkFilters";
    private const string ForeignId = "{AAAAAAAA-1111-2222-3333-444444444444}";
    private const string LateId = "{BBBBBBBB-1111-2222-3333-444444444444}";   // a filter installed for all users after the copy was made

    private static void RegistrationScenarios()
    {
        string realBefore = RealDecalFingerprint();

        RegScenario("ForTests refuses a root outside HKCU\\Software\\RynthCoreTests", _ =>
        {
            bool threw = false;
            try { DecalRegistryRoots.ForTests(@"Software\Classes\VirtualStore\X", Path.GetTempPath(), () => true); }
            catch (ArgumentException) { threw = true; }
            Check(threw, "ArgumentException");
        });

        RegScenario("register, HKLM not writable: NeedsAdmin and NO per-user copy", t =>
        {
            t.MachineWritable = false;
            var log = new List<string>();
            var o = DecalBridgeRegistration.RegisterCore(t.Roots, t.BridgeDir, log);
            Check(o == DecalBridgeRegistration.Outcome.NeedsAdmin, $"NeedsAdmin (got {o}: {string.Join(" ", log)})");
            Check(!t.UserExists(VsNetworkFilters), "no per-user NetworkFilters copy created");
            Check(!t.UserExists(@"Software\Classes\VirtualStore"), "nothing written under the VirtualStore at all");
            Check(!t.MachineExists(MachineFilters + "\\" + Guid), "no machine entry");
            Check(t.StateFiles().Length == 0, "no record file written");
        });

        RegScenario("register, HKLM writable (the elevated step): machine entry only", t =>
        {
            var log = new List<string>();
            var o = DecalBridgeRegistration.RegisterCore(t.Roots, t.BridgeDir, log);
            Check(o == DecalBridgeRegistration.Outcome.Registered, $"Registered ({string.Join(" ", log)})");
            using (RegistryKey? e = t.OpenMachine(MachineFilters + "\\" + Guid))
            {
                Check(e?.GetValue("Object") as string == DecalBridgeRegistration.FilterObject, "Object");
                Check(e?.GetValue("Path") as string == t.BridgeDir, "Path");
                Check(e?.GetValue("Enabled") is int en && en == 1, "Enabled=1");
            }
            Check(!t.UserExists(VsNetworkFilters), "no per-user copy");
            t.MachineWritable = false;   // the non-elevated launcher afterwards
            log.Clear();
            o = DecalBridgeRegistration.RegisterCore(t.Roots, t.BridgeDir, log);
            Check(o == DecalBridgeRegistration.Outcome.Registered && log.Any(l => l.Contains("already")), "second call without rights: already registered, no prompt needed");
            Check(!t.UserExists(VsNetworkFilters), "still no per-user copy");
        });

        RegScenario("a per-user copy Decal made itself: our entry goes in, the copy is never deleted", t =>
        {
            t.SetUser(VsNetworkFilters + "\\" + StockIds[0], new() { [""] = "World Object Filter", ["Enabled"] = 0 });   // Decal toggled it
            var log = new List<string>();
            var o = DecalBridgeRegistration.RegisterCore(t.Roots, t.BridgeDir, log);
            Check(o == DecalBridgeRegistration.Outcome.Registered, $"Registered ({string.Join(" ", log)})");
            Check(t.UserExists(VsNetworkFilters + "\\" + Guid), "bridge entry added to the existing copy");
            Check(DecalBridgeRegistration.PerUserCleanupNeededCore(t.Roots), "cleanup needed (our entry is there)");
            bool ok = DecalBridgeRegistration.UnregisterCore(t.Roots, out string report);
            Check(ok, "unregister ok: " + report);
            Check(!t.UserExists(VsNetworkFilters + "\\" + Guid), "bridge entry removed");
            Check(t.UserExists(VsNetworkFilters + "\\" + StockIds[0]), "Decal's own entry kept");
            Check(t.BackupFiles().Length == 0, "no backup needed (only our entry removed)");
            Check(!DecalBridgeRegistration.PerUserCleanupNeededCore(t.Roots), "nothing left to clean up");
        });

        RegScenario("an earlier RynthCore's copy: left alone while HKLM can't be written, migrated once it can", t =>
        {
            t.MakeOldLauncherCopy();
            string before = t.UserFingerprint(VsNetworkFilters);
            t.MachineWritable = false;
            var log = new List<string>();
            var o = DecalBridgeRegistration.RegisterCore(t.Roots, t.BridgeDir, log);
            Check(o == DecalBridgeRegistration.Outcome.NeedsAdmin, "NeedsAdmin");
            Check(t.UserFingerprint(VsNetworkFilters) == before, "copy untouched until the machine entry exists");

            t.MachineWritable = true;
            log.Clear();
            o = DecalBridgeRegistration.RegisterCore(t.Roots, t.BridgeDir, log);
            Check(o == DecalBridgeRegistration.Outcome.Registered, $"Registered ({string.Join(" ", log)})");
            Check(t.MachineExists(MachineFilters + "\\" + Guid), "machine entry");
            Check(!t.UserExists(VsNetworkFilters), "the old copy is gone (Decal reads HKLM again)");
            Check(t.StateFiles().Length == 0, "record file deleted");
            string[] backups = t.BackupFiles();
            Check(backups.Length == 1 && Path.GetFileName(backups[0]).StartsWith("decal-backup-"), "one decal-backup-<date>.reg");
            string text = backups.Length == 1 ? File.ReadAllText(backups[0]) : "";
            Check(text.StartsWith("Windows Registry Editor Version 5.00") && text.Contains(Guid) && text.Contains(StockIds[0]), "backup holds the copy (bridge + mirrors)");
            Check(log.Any(l => l.Contains("backup:")), "log names the backup");
        });

        RegScenario("uninstall: an earlier RynthCore's copy goes, whatever is in it now", t =>
        {
            t.MakeOldLauncherCopy();
            // Changed since: Decal disabled a mirrored filter, and wrote a filter installed for
            // all users later (in HKLM, not in the record) into the copy.
            t.SetMachine(MachineFilters + "\\" + LateId, new() { [""] = "UtilityBelt Service", ["Enabled"] = 1 });
            t.SetUser(VsNetworkFilters + "\\" + StockIds[1], new() { [""] = "Echo Filter 2", ["Enabled"] = 0 });
            t.SetUser(VsNetworkFilters + "\\" + LateId, new() { [""] = "UtilityBelt Service", ["Enabled"] = 1 });
            string machineBefore = t.MachineFingerprint(MachineFilters);
            Check(DecalBridgeRegistration.PerUserCleanupNeededCore(t.Roots), "cleanup needed");
            bool ok = DecalBridgeRegistration.UnregisterCore(t.Roots, out string report);
            Check(ok, "unregister ok: " + report);
            Check(!t.UserExists(VsNetworkFilters), "copy removed (bridge, changed and unrecorded mirrors, key)");
            Check(t.StateFiles().Length == 0, "record deleted");
            Check(t.BackupFiles().Length == 1, "backed up first");
            Check(t.MachineFingerprint(MachineFilters) == machineBefore, "HKLM untouched");
            Check(!DecalBridgeRegistration.PerUserCleanupNeededCore(t.Roots), "nothing left");
        });

        RegScenario("uninstall: entries that are neither ours nor in HKLM keep the copy", t =>
        {
            t.MakeOldLauncherCopy();
            t.SetUser(VsNetworkFilters + "\\" + ForeignId, new() { [""] = "Someone's Filter", ["Enabled"] = 1 });
            bool ok = DecalBridgeRegistration.UnregisterCore(t.Roots, out string report);
            Check(ok, "unregister ok: " + report);
            Check(t.UserExists(VsNetworkFilters + "\\" + ForeignId), "foreign entry kept");
            Check(!t.UserExists(VsNetworkFilters + "\\" + Guid), "our entry removed");
            Check(t.UserExists(VsNetworkFilters + "\\" + StockIds[0]), "mirrors kept with it (a copy of only the foreign entry would hide World Object Filter)");
            Check(report.Contains("Someone's Filter") && report.Contains("Repair Decal"), "logged, with the way out");
            Check(t.StateFiles().Length == 1, "record kept for a later cleanup");
            Check(t.BackupFiles().Length == 0, "no backup (nothing but our entry removed)");
            Check(!DecalBridgeRegistration.PerUserCleanupNeededCore(t.Roots), "not 'needed' again at every start");
            // Register with such a copy: the bridge must stay visible in it.
            var log = new List<string>();
            var o = DecalBridgeRegistration.RegisterCore(t.Roots, t.BridgeDir, log);
            Check(o == DecalBridgeRegistration.Outcome.Registered && t.UserExists(VsNetworkFilters + "\\" + Guid), "register: copy kept, our entry back in it");
            Check(t.UserExists(VsNetworkFilters + "\\" + ForeignId), "foreign entry still kept");
        });

        RegScenario("no account uses Decal + RynthCore: machine entry removed (only this install's)", t =>
        {
            var log = new List<string>();
            DecalBridgeRegistration.RegisterCore(t.Roots, t.BridgeDir, log);
            Check(!DecalBridgeRegistration.PerUserCleanupNeededCore(t.Roots), "nothing per-user to remove (no prompt-free work to do)");
            Check(DecalBridgeRegistration.MachineEntryIsOursCore(t.Roots, t.BridgeDir), "entry is this install's");
            string other = Path.Combine(t.DataDir, "OtherInstall");
            Directory.CreateDirectory(other);
            File.WriteAllText(Path.Combine(other, DecalBridgeRegistration.AssemblyFile), "");
            Check(!DecalBridgeRegistration.MachineEntryIsOursCore(t.Roots, other), "another install's folder: not ours to remove");
            t.MachineWritable = false;
            log.Clear();
            Check(DecalBridgeRegistration.UnregisterMachineWideCore(t.Roots, t.BridgeDir, log) == DecalBridgeRegistration.Outcome.NeedsAdmin, "needs admin without rights");
            Check(t.MachineExists(MachineFilters + "\\" + Guid), "still there");
            t.MachineWritable = true;
            log.Clear();
            Check(DecalBridgeRegistration.UnregisterMachineWideCore(t.Roots, t.BridgeDir, log) == DecalBridgeRegistration.Outcome.Registered, "removed: " + string.Join(" ", log));
            Check(!t.MachineExists(MachineFilters + "\\" + Guid), "gone");
            Check(t.MachineExists(MachineFilters + "\\" + StockIds[0]), "other filters untouched");
            Check(!DecalBridgeRegistration.MachineEntryIsOursCore(t.Roots, t.BridgeDir), "nothing to ask about at the next start");
            t.MachineWritable = false;
            log.Clear();
            Check(DecalBridgeRegistration.UnregisterMachineWideCore(t.Roots, t.BridgeDir, log) == DecalBridgeRegistration.Outcome.Registered, "nothing to remove: no rights needed");
        });

        RegScenario("dangling machine entry (DLL gone) counts as ours to remove", t =>
        {
            var gone = BridgeEntryAt(Path.Combine(t.DataDir, "Gone"));
            t.SetMachine(MachineFilters + "\\" + Guid, gone);
            Check(DecalBridgeRegistration.MachineEntryIsOursCore(t.Roots, t.BridgeDir), "dangling = ours");
        });

        RegScenario("Repair Decal: backup, then delete both VirtualStore Decal keys; the backup restores", t =>
        {
            t.MakeOldLauncherCopy();
            t.SetUser(VsDecalWow + "\\Plugins\\{CCCCCCCC-1111-2222-3333-444444444444}", new() { [""] = "Some Plugin", ["Enabled"] = 1 });
            t.SetUser(VsDecalWow, new() { ["BarAlpha"] = 255, ["BarState"] = "1" });
            t.SetUser(VsDecalPlain + "\\Plugins", new() { ["X"] = "y" });
            string wowBefore = t.UserFingerprint(VsDecalWow), plainBefore = t.UserFingerprint(VsDecalPlain);
            List<string> found = DecalBridgeRegistration.FindVirtualStoreDecalKeysCore(t.Roots);
            Check(found.Count == 2, "both spellings found");

            var r = DecalBridgeRegistration.RepairDecalCore(t.Roots);
            Check(r.Ok, "ok: " + r.Report);
            Check(r.Removed.Count == 2 && r.Removed.All(k => k.StartsWith(@"HKCU\Software\Classes\VirtualStore\")), "reports both keys as HKCU paths");
            Check(!t.UserExists(VsDecalWow) && !t.UserExists(VsDecalPlain), "both deleted");
            Check(r.BackupPath != null && File.Exists(r.BackupPath), "backup file exists");
            Check(t.StateFiles().Length == 0, "the earlier RynthCore's record goes with its copy");
            string text = r.BackupPath != null ? File.ReadAllText(r.BackupPath) : "";
            Check(text.Contains("BarAlpha") && text.Contains("Some Plugin") && text.Contains(Guid), "backup has values, plugins, filters");
            int headers = text.Split("Windows Registry Editor Version 5.00").Length - 1;
            Check(headers == 1, "one .reg header for both keys");

            // Round trip, only into the scratch key: every section in the file must be under it.
            string root = @"[HKEY_CURRENT_USER\" + t.Key + @"\User\";
            bool allScratch = text.Split('\n').Where(l => l.StartsWith("[")).All(l => l.StartsWith(root, StringComparison.OrdinalIgnoreCase));
            Check(allScratch, "every key in the backup is under the scratch root");
            if (allScratch && r.BackupPath != null)
            {
                Check(RegImport(r.BackupPath), "reg import of the backup");
                Check(t.UserFingerprint(VsDecalWow) == wowBefore && t.UserFingerprint(VsDecalPlain) == plainBefore, "restored exactly");
            }
        });

        RegScenario("Repair Decal with nothing to repair", t =>
        {
            var r = DecalBridgeRegistration.RepairDecalCore(t.Roots);
            Check(r.Ok && r.Removed.Count == 0 && r.BackupPath == null && t.BackupFiles().Length == 0, "nothing removed, no backup");
        });

        RegScenario("Repair Decal: a failed backup deletes nothing", t =>
        {
            t.SetUser(VsDecalWow + "\\Plugins", new() { ["X"] = "y" });
            string blocker = Path.Combine(t.DataDir, "not-a-folder");
            File.WriteAllText(blocker, "");
            var roots = DecalRegistryRoots.ForTests(t.Key, Path.Combine(blocker, "sub"), () => true);
            var r = DecalBridgeRegistration.RepairDecalCore(roots);
            Check(!r.Ok && r.Removed.Count == 0 && r.Report.Contains("backup failed"), "stopped: " + r.Report);
            Check(t.UserExists(VsDecalWow + "\\Plugins"), "key still there");
        });

        RegScenario("status reads the scratch registry", t =>
        {
            t.MakeOldLauncherCopy();
            t.SetMachine(MachineFilters + "\\" + LateId, new() { [""] = "UtilityBelt Service", ["Enabled"] = 1 });
            var s = DecalBridgeRegistration.GetStatusCore(t.Roots);
            Check(s.DecalInstalled && s.PerUserKeyExists && s.PerUserKeyCreatedByUs && s.RegisteredPerUser, "copy, ours, bridge in it");
            Check(s.HiddenMachineFilters.Contains("UtilityBelt Service"), "the later filter is hidden (the player's bug)");
        });

        string realAfter = RealDecalFingerprint();
        Scenario("the real Decal keys and VirtualStore copies did not change", () =>
            Check(realAfter == realBefore, "real HKLM Decal + HKCU VirtualStore Decal fingerprints match"));
        Scenario("no scratch keys left", () =>
        {
            using RegistryKey hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using RegistryKey? left = hkcu.OpenSubKey(@"Software\RynthCoreTests");
            Check(left == null || left.SubKeyCount == 0, @"HKCU\Software\RynthCoreTests empty or gone");
        });
    }

    private static readonly string[] StockIds = Stock.Select(s => s.Id).ToArray();

    private static Dictionary<string, object> BridgeEntryAt(string dir) => new()
    {
        [""] = DecalBridgeRegistration.FilterName,
        ["Enabled"] = 1,
        ["Assembly"] = DecalBridgeRegistration.AssemblyFile,
        ["Path"] = dir,
        ["Object"] = DecalBridgeRegistration.FilterObject,
        ["Surrogate"] = DecalBridgeCheck.SurrogateClsid,
    };

    private static void RegScenario(string name, Action<RegTestBed> body)
    {
        Scenario(name, () =>
        {
            using var t = new RegTestBed();
            body(t);
        });
    }

    /// <summary>Read only: the real HKLM Decal tree and the real VirtualStore Decal keys.</summary>
    private static string RealDecalFingerprint()
    {
        var sb = new StringBuilder();
        using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
        using (RegistryKey? d = hklm.OpenSubKey(@"SOFTWARE\Decal"))
            sb.Append("HKLM:").Append(d == null ? "<none>" : DecalBridgeRegistration.Fingerprint(d));
        using (RegistryKey hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
        {
            foreach (string k in DecalBridgeRegistration.VirtualStoreDecalKeys)
            {
                using RegistryKey? v = hkcu.OpenSubKey(k);
                sb.Append('|').Append(k).Append(':').Append(v == null ? "<none>" : DecalBridgeRegistration.Fingerprint(v));
            }
        }
        return sb.ToString();
    }

    private static bool RegImport(string file)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "reg.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("import");
        psi.ArgumentList.Add(file);
        psi.ArgumentList.Add("/reg:64");
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        if (p.ExitCode != 0) Console.WriteLine("   reg import: " + err.Trim());
        return p.ExitCode == 0;
    }
}

/// <summary>A scratch registry + folder for one scenario; deleted on Dispose.</summary>
internal sealed class RegTestBed : IDisposable
{
    public string Key { get; } = @"Software\RynthCoreTests\DecalBridge-" + Guid.NewGuid().ToString("N");
    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "RynthCoreTests", Guid.NewGuid().ToString("N"));
    public string BridgeDir { get; }
    public bool MachineWritable { get; set; } = true;
    public DecalRegistryRoots Roots { get; }

    private static readonly (string Id, string Name)[] Stock =
    {
        ("{53092D1B-F0B0-46FF-BF11-8F031EC9B137}", "World Object Filter"),
        ("{34239EAD-6317-4c40-A405-193BA5232DD8}", "Echo Filter 2"),
    };

    public RegTestBed()
    {
        Roots = DecalRegistryRoots.ForTests(Key, DataDir, () => MachineWritable);
        BridgeDir = Path.Combine(DataDir, "DecalBridge");
        Directory.CreateDirectory(BridgeDir);
        File.WriteAllText(Path.Combine(BridgeDir, DecalBridgeRegistration.AssemblyFile), "");
        // A machine with Decal and two stock filters (stands in for HKLM, 32-bit view).
        SetMachine(@"SOFTWARE\Decal\Agent", new() { ["AgentPath"] = @"C:\Program Files (x86)\Decal 3.0\" });
        SetMachine(@"SOFTWARE\Decal\NetworkFilters", new() { ["Version"] = "2.9.8.3" });
        foreach (var f in Stock)
            SetMachine(@"SOFTWARE\Decal\NetworkFilters\" + f.Id, new() { [""] = f.Name, ["Enabled"] = 1 });
    }

    /// <summary>What a launcher before 2026-10-10 left: a full mirror + our entry + its record file.</summary>
    public void MakeOldLauncherCopy()
    {
        string vs = DecalBridgeCheck.VirtualStoreDecalPath(true, "NetworkFilters");
        SetUser(vs, new() { ["Version"] = "2.9.8.3" });
        foreach (var f in Stock)
            SetUser(vs + "\\" + f.Id, new() { [""] = f.Name, ["Enabled"] = 1 });
        SetUser(vs + "\\" + DecalBridgeRegistration.FilterGuid, new()
        {
            [""] = DecalBridgeRegistration.FilterName,
            ["Enabled"] = 1,
            ["Assembly"] = DecalBridgeRegistration.AssemblyFile,
            ["Path"] = BridgeDir,
            ["Object"] = DecalBridgeRegistration.FilterObject,
            ["Surrogate"] = DecalBridgeCheck.SurrogateClsid,
        });
        var state = new
        {
            CreatedFiltersKey = true,
            BridgePath = BridgeDir,
            RegisteredUtc = new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc),
            MirroredKeys = Stock.ToDictionary(f => f.Id, f => "print"),
            MirroredValues = new Dictionary<string, string> { ["Version"] = "print" },
        };
        File.WriteAllText(Path.Combine(DataDir, DecalBridgeRegistration.StateFileName), JsonSerializer.Serialize(state));
    }

    public void SetMachine(string path, Dictionary<string, object> values) => Set("Machine", path, values);
    public void SetUser(string path, Dictionary<string, object> values) => Set("User", path, values);
    public bool MachineExists(string path) { using var k = OpenMachine(path); return k != null; }
    public bool UserExists(string path) { using var k = Open("User", path); return k != null; }
    public RegistryKey? OpenMachine(string path) => Open("Machine", path);
    public string MachineFingerprint(string path) { using var k = Open("Machine", path); return k == null ? "<none>" : DecalBridgeRegistration.Fingerprint(k); }
    public string UserFingerprint(string path) { using var k = Open("User", path); return k == null ? "<none>" : DecalBridgeRegistration.Fingerprint(k); }
    public string[] StateFiles() => Directory.Exists(DataDir) ? Directory.GetFiles(DataDir, DecalBridgeRegistration.StateFileName) : Array.Empty<string>();
    public string[] BackupFiles() => Directory.Exists(DataDir) ? Directory.GetFiles(DataDir, "decal-backup-*.reg") : Array.Empty<string>();

    private RegistryKey? Open(string part, string path)
    {
        using RegistryKey hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        return hkcu.OpenSubKey(Key + "\\" + part + "\\" + path);
    }

    private void Set(string part, string path, Dictionary<string, object> values)
    {
        using RegistryKey hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using RegistryKey k = hkcu.CreateSubKey(Key + "\\" + part + "\\" + path, writable: true);
        foreach (var kv in values)
            k.SetValue(kv.Key, kv.Value, kv.Value is int ? RegistryValueKind.DWord : RegistryValueKind.String);
    }

    public void Dispose()
    {
        using RegistryKey hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        try { hkcu.DeleteSubKeyTree(Key, throwOnMissingSubKey: false); } catch (Exception ex) { Console.WriteLine($"   (cleanup of HKCU\\{Key} failed: {ex.Message})"); }
        try
        {
            using RegistryKey? parent = hkcu.OpenSubKey(@"Software\RynthCoreTests");
            if (parent != null && parent.SubKeyCount == 0 && parent.ValueCount == 0)
                hkcu.DeleteSubKey(@"Software\RynthCoreTests", throwOnMissingSubKey: false);
        }
        catch { }
        try { if (Directory.Exists(DataDir)) Directory.Delete(DataDir, recursive: true); } catch { }
    }
}
