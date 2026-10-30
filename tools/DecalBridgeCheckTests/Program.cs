using RynthCore.App;
using static RynthCore.App.DecalBridgeCheck;

namespace DecalBridgeCheckTests;

/// <summary>
/// The Decal bridge check against a fake machine. The scenario that broke on a player's PC
/// (2026-10-03, release 2026.10.3.1) is "elevated client, per-user registration only":
/// Decal reads HKLM, the per-user entry is invisible, the bridge never loads.
/// </summary>
internal static partial class Program
{
    private static int _checks, _failed;

    private const string Agent = @"C:\Program Files (x86)\Decal 3.0\";
    private const string BridgeDir = @"C:\Games\RynthCore\DecalBridge";
    private const string AcClient = @"C:\Turbine\Asheron's Call\acclient.exe";
    private static readonly string Guid = DecalBridgeRegistration.FilterGuid;
    private static readonly string VsFilters = VirtualStoreDecalPath(true, "NetworkFilters");
    private static readonly string HkFilters = MachineDecalPath("NetworkFilters");

    private static int Main()
    {
        Scenario("Tom's PC: non-elevated, per-user copy with the bridge", () =>
        {
            FakeHost h = Baseline();
            h.Set(RegRoot.User64, VsFilters, new());
            h.Set(RegRoot.User64, VsFilters + "\\" + Guid, BridgeEntry());
            MirrorStock(h, RegRoot.User64, VsFilters);
            h.OwnView = BridgeEntry();
            Result r = Run(h, BridgeDir, AcClient, null);
            Check(r.ClientVirtualized, "client virtualized");
            Check(r.ReadsPerUserCopy, "Decal reads the per-user copy");
            Check(r.BridgeVisibleToDecal && !r.Blocking, "bridge visible, not blocking");
            Check(Has(r, Level.Ok, "Read back as AC will see it: bridge listed"), "live read-back agrees");
            Check(!Has(r, Level.Warn, "Hidden from AC"), "no hidden filters (all mirrored)");
        });

        Scenario("THE PLAYER CASE: launcher (so AC) elevated, bridge only in the per-user copy", () =>
        {
            FakeHost h = Baseline();
            h.LauncherElevated = true;
            h.LauncherVirtualized = false;
            h.Set(RegRoot.User64, VsFilters, new());
            h.Set(RegRoot.User64, VsFilters + "\\" + Guid, BridgeEntry());
            h.OwnView = null;   // the elevated view is the real HKLM: no bridge there
            Result r = Run(h, BridgeDir, AcClient, null);
            Check(!r.ClientVirtualized, "client not virtualized");
            Check(r.ClientReason.Contains("administrator"), "reason names administrator");
            Check(!r.ReadsPerUserCopy, "Decal reads HKLM");
            Check(r.FiltersReadFrom.Contains(@"HKLM\SOFTWARE\WOW6432Node\Decal\NetworkFilters"), "describes the WOW6432Node key");
            Check(r.Blocking && !r.BridgeVisibleToDecal, "blocking");
            Check(r.NeedsMachineWideRegistration, "needs the machine-wide registration (admin)");
            Check(Has(r, Level.Info, "per-user registration exists, but this AC client doesn't read it"), "explains the invisible per-user entry");
            Check(Has(r, Level.Ok, "Read back as AC will see it: bridge not listed"), "live read-back agrees (not listed)");
        });

        // ── after the installer / an update (installer\RynthCore.iss RegisterDecalBridge) ──

        Scenario("installer plan: machine always; per-user only into an existing copy", () =>
        {
            FakeHost h = Baseline();
            Check(PlanInstall(h) == (true, false), "no copy: machine only (a copy is never created)");
            h.Set(RegRoot.User64, VsFilters, new());
            Check(PlanInstall(h) == (true, true), "copy exists: machine + per-user");
            h.Remove(RegRoot.Machine32, MachineDecalPath("Agent"));
            Check(PlanInstall(h) == (false, false), "no Decal: nothing");
        });

        foreach (bool elevated in new[] { false, true })
        foreach (bool uacOff in new[] { false, true })
        {
            string who = (elevated ? "elevated" : "non-elevated") + (uacOff ? ", UAC off" : "");
            Scenario($"installer wrote both (per-user copy existed): {who} client loads the bridge", () =>
            {
                FakeHost h = InstalledBoth(withCopy: true, elevated, uacOff);
                Result r = Run(h, BridgeDir, AcClient, null);
                Check(r.BridgeVisibleToDecal && !r.Blocking, "visible, not blocking");
                Check(r.ReadsPerUserCopy == (!elevated && !uacOff), "reads the copy only when virtualized");
            });
            Scenario($"installer wrote machine only (no per-user copy): {who} client loads the bridge", () =>
            {
                FakeHost h = InstalledBoth(withCopy: false, elevated, uacOff);
                Result r = Run(h, BridgeDir, AcClient, null);
                Check(r.BridgeVisibleToDecal && !r.Blocking && !r.ReadsPerUserCopy, "visible through HKLM, not blocking");
            });
        }

        Scenario("negative control: copy existed but the per-user half didn't run", () =>
        {
            FakeHost h = InstalledBoth(withCopy: true, elevated: false, uacOff: false);
            h.Remove(RegRoot.User64, VsFilters + "\\" + Guid);
            h.OwnView = null;
            Result r = Run(h, BridgeDir, AcClient, null);
            Check(r.Blocking && r.ReadsPerUserCopy, "the copy hides the machine entry: why the per-user half is needed");
        });

        Scenario("elevated, machine-wide entry present", () =>
        {
            FakeHost h = Baseline();
            h.LauncherElevated = true;
            h.LauncherVirtualized = false;
            h.Set(RegRoot.Machine32, HkFilters + "\\" + Guid, BridgeEntry());
            h.OwnView = BridgeEntry();
            Result r = Run(h, BridgeDir, AcClient, null);
            Check(!r.Blocking && r.BridgeVisibleToDecal && !r.ReadsPerUserCopy, "OK through HKLM");
        });

        Scenario("non-elevated, no per-user copy, nothing registered", () =>
        {
            FakeHost h = Baseline();
            h.OwnView = null;
            Result r = Run(h, BridgeDir, AcClient, null);
            Check(r.ClientVirtualized && !r.ReadsPerUserCopy, "virtualized client reads HKLM when no copy exists");
            Check(r.Blocking && r.NeedsMachineWideRegistration, "blocking; the place read is HKLM");
        });

        Scenario("non-elevated, per-user copy exists without the bridge, HKLM has it", () =>
        {
            FakeHost h = Baseline();
            h.Set(RegRoot.User64, VsFilters, new());
            h.Set(RegRoot.User64, VsFilters + "\\{108ED493-48AD-42A6-AADC-EF773E4F185A}", new() { [""] = "Virindi AutomaticUpdatesFilter", ["Enabled"] = 1 });
            h.Set(RegRoot.Machine32, HkFilters + "\\" + Guid, BridgeEntry());
            h.OwnView = null;
            Result r = Run(h, BridgeDir, AcClient, null);
            Check(r.ReadsPerUserCopy && r.Blocking && !r.NeedsMachineWideRegistration, "per-user copy hides the HKLM entry; fixable per-user");
            Check(Has(r, Level.Info, "machine-wide entry exists, but your per-user copy hides it"), "explains the hidden machine entry");
            Check(Has(r, Level.Warn, "World Object Filter"), "warns that World Object Filter is hidden");
        });

        Scenario("UAC off", () =>
        {
            FakeHost h = Baseline();
            h.Set(RegRoot.Machine64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", new() { ["EnableLUA"] = 0, ["EnableVirtualization"] = 1 });
            h.LauncherVirtualized = false;
            h.Set(RegRoot.User64, VsFilters, new());
            h.Set(RegRoot.User64, VsFilters + "\\" + Guid, BridgeEntry());
            Result r = Run(h, BridgeDir, AcClient, null);
            Check(!r.ClientVirtualized && r.ClientReason.Contains("UAC is turned off"), "UAC off: not virtualized");
            Check(r.Blocking && r.NeedsMachineWideRegistration, "per-user registration is not enough");
        });

        Scenario("virtualization off by policy", () =>
        {
            FakeHost h = Baseline();
            h.Set(RegRoot.Machine64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", new() { ["EnableLUA"] = 1, ["EnableVirtualization"] = 0 });
            Check(!PredictClientVirtualized(h, AcClient, out string why) && why.Contains("EnableVirtualization=0"), "policy: not virtualized");
        });

        Scenario("acclient.exe with a manifest", () =>
        {
            FakeHost h = Baseline();
            h.Levels[AcClient] = "asInvoker";
            Check(!PredictClientVirtualized(h, AcClient, out string why) && why.Contains("asInvoker"), "manifest: not virtualized");
            Check(PredictClientVirtualized(h, @"C:\Other\acclient.exe", out _), "a different exe without manifest is virtualized");
        });

        Scenario("acclient.exe set to Run as administrator, Decal's COM class only per-user", () =>
        {
            FakeHost h = Baseline();
            h.Set(RegRoot.User64, @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers", new() { [AcClient.ToUpperInvariant()] = "~ RUNASADMIN" });
            h.Remove(RegRoot.Machine32, @"SOFTWARE\Classes\CLSID\" + SurrogateClsid + @"\InprocServer32");
            h.Set(RegRoot.User32, @"Software\Classes\CLSID\" + SurrogateClsid + @"\InprocServer32", ComEntry());
            h.Set(RegRoot.Machine32, HkFilters + "\\" + Guid, BridgeEntry());
            Result r = Run(h, BridgeDir, AcClient, null);
            Check(!r.ClientVirtualized && r.ClientReason.Contains("RUNASADMIN"), "RUNASADMIN: not virtualized");
            Check(r.BridgeVisibleToDecal, "bridge visible in HKLM");
            Check(Has(r, Level.Fail, "ignores per-user COM classes"), "per-user-only COM class fails for an elevated client");
        });

        Scenario("non-elevated client resolves a per-user COM class", () =>
        {
            FakeHost h = Baseline();
            h.Remove(RegRoot.Machine32, @"SOFTWARE\Classes\CLSID\" + SurrogateClsid + @"\InprocServer32");
            h.Set(RegRoot.User32, @"Software\Classes\CLSID\" + SurrogateClsid + @"\InprocServer32", ComEntry());
            Check(SurrogateComProblem(h, clientElevated: false, out string where) == null && where == "per-user", "per-user COM class OK");
        });

        Scenario("COM class missing / pointing at a missing file", () =>
        {
            FakeHost h = Baseline();
            h.Remove(RegRoot.Machine32, @"SOFTWARE\Classes\CLSID\" + SurrogateClsid + @"\InprocServer32");
            Check(SurrogateComProblem(h, false, out _)?.Contains("not registered") == true, "missing COM class");
            var com = ComEntry();
            com["CodeBase"] = @"file:///D:\Gone\Decal.Adapter.dll";
            h.Set(RegRoot.Machine32, @"SOFTWARE\Classes\CLSID\" + SurrogateClsid + @"\InprocServer32", com);
            Check(SurrogateComProblem(h, false, out _)?.Contains("missing file") == true, "CodeBase to a missing file");
        });

        Scenario("entry problems", () =>
        {
            FakeHost h = Baseline();
            var disabled = BridgeEntry(); disabled["Enabled"] = 0;
            h.Set(RegRoot.Machine32, HkFilters + "\\" + Guid, disabled);
            Check(EntryProblem(h, RegRoot.Machine32, HkFilters + "\\" + Guid, out _)?.Contains("disabled") == true, "disabled entry");
            var moved = BridgeEntry(); moved["Path"] = @"D:\Old\DecalBridge";
            h.Set(RegRoot.Machine32, HkFilters + "\\" + Guid, moved);
            Check(EntryProblem(h, RegRoot.Machine32, HkFilters + "\\" + Guid, out _)?.Contains("missing") == true, "DLL missing at the registered path");
            var foreign = BridgeEntry(); foreign["Object"] = "Someone.Else";
            h.Set(RegRoot.Machine32, HkFilters + "\\" + Guid, foreign);
            Check(EntryProblem(h, RegRoot.Machine32, HkFilters + "\\" + Guid, out _)?.Contains("another filter") == true, "foreign entry");
            var badSur = BridgeEntry(); badSur["Surrogate"] = "{00000000-0000-0000-0000-000000000000}";
            h.Set(RegRoot.Machine32, HkFilters + "\\" + Guid, badSur);
            Check(EntryProblem(h, RegRoot.Machine32, HkFilters + "\\" + Guid, out _)?.Contains("Surrogate") == true, "wrong surrogate");
        });

        Scenario("Decal older than the bridge's reference", () =>
        {
            FakeHost h = Baseline();
            h.AssemblyVersions[Agent + "Decal.Adapter.dll"] = new Version(2, 9, 7, 5);
            h.Set(RegRoot.Machine32, HkFilters + "\\" + Guid, BridgeEntry());
            Result r = Run(h, BridgeDir, AcClient, null);
            Check(r.Blocking && Has(r, Level.Fail, "older than the 2.9.8.3"), "old Decal blocks");
        });

        Scenario("Decal's surrogate disabled / not listed in the per-user Surrogates copy", () =>
        {
            FakeHost h = Baseline();
            h.Set(RegRoot.Machine32, HkFilters + "\\" + Guid, BridgeEntry());
            h.Set(RegRoot.Machine32, MachineDecalPath("Surrogates") + "\\" + SurrogateClsid, new() { [""] = "Decal.Adapter Surrogate", ["Enabled"] = 0 });
            Check(Has(Run(h, BridgeDir, AcClient, null), Level.Fail, "disabled in Decal"), "disabled surrogate");
            h.Set(RegRoot.Machine32, MachineDecalPath("Surrogates") + "\\" + SurrogateClsid, new() { [""] = "Decal.Adapter Surrogate", ["Enabled"] = 1 });
            h.Set(RegRoot.User64, VirtualStoreDecalPath(true, "Surrogates"), new());
            Check(Has(Run(h, BridgeDir, AcClient, null), Level.Fail, "surrogate is not listed"), "a per-user Surrogates copy without it hides it");
        });

        Scenario("engine.json DecalBridge Off", () =>
        {
            FakeHost h = Baseline();
            h.Set(RegRoot.Machine32, HkFilters + "\\" + Guid, BridgeEntry());
            h.Files[@"C:\cfg\engine.json"] = "{ \"DecalBridge\": \"Off\", \"DecalInGameImGui\": false }";
            Result r = Run(h, BridgeDir, AcClient, @"C:\cfg\engine.json");
            Check(Has(r, Level.Fail, "\"DecalBridge\": Off"), "DecalBridge Off fails");
            Check(Has(r, Level.Warn, "DecalInGameImGui"), "DecalInGameImGui false warns");
        });

        Scenario("live view disagrees with the predicted place", () =>
        {
            FakeHost h = Baseline();
            h.Set(RegRoot.User64, VsFilters, new());
            h.Set(RegRoot.User64, VsFilters + "\\" + Guid, BridgeEntry());
            h.OwnView = null;
            Result r = Run(h, BridgeDir, AcClient, null);
            Check(r.Blocking && Has(r, Level.Fail, "bridge NOT listed"), "disagreement fails");
        });

        Scenario("launcher virtualized, client not: no live read-back", () =>
        {
            FakeHost h = Baseline();
            h.Levels[AcClient] = "asInvoker";
            h.Set(RegRoot.Machine32, HkFilters + "\\" + Guid, BridgeEntry());
            h.OwnView = null;
            Result r = Run(h, BridgeDir, AcClient, null);
            Check(!r.Blocking && Has(r, Level.Info, "Not read back live"), "skips the live read when views differ");
        });

        Scenario("bridge DLL downloaded mark, .NET older than 4.8", () =>
        {
            FakeHost h = Baseline();
            h.Set(RegRoot.Machine32, HkFilters + "\\" + Guid, BridgeEntry());
            h.Zoned.Add(BridgeDir + "\\" + DecalBridgeRegistration.AssemblyFile);
            h.OwnView = BridgeEntry();
            h.Set(RegRoot.Machine32, @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", new() { ["Release"] = 461814 });
            Result r = Run(h, BridgeDir, AcClient, null);
            Check(Has(r, Level.Warn, "downloaded from the internet") && Has(r, Level.Warn, "4.8"), "both warn");
            Check(!r.Blocking, "warnings don't block");
        });

        Scenario("Decal not installed", () =>
        {
            FakeHost h = Baseline();
            h.Remove(RegRoot.Machine32, MachineDecalPath("Agent"));
            Result r = Run(h, BridgeDir, AcClient, null);
            Check(r.Blocking && !r.DecalInstalled && r.Lines.Count == 1, "stops at 'not installed'");
        });

        Scenario("32-bit Windows paths", () =>
        {
            Check(VirtualStoreDecalPath(false, "NetworkFilters") == @"Software\Classes\VirtualStore\MACHINE\SOFTWARE\Decal\NetworkFilters", "no WOW6432Node in the copy's path");
            Check(Describe(false, RegRoot.Machine32, HkFilters) == @"HKLM\SOFTWARE\Decal\NetworkFilters", "no WOW6432Node in the HKLM description");
        });

        Scenario("manifest level parsing", () =>
        {
            Check(RealDecalCheckHost.LevelIn("<requestedExecutionLevel level=\"asInvoker\" uiAccess=\"false\"/>") == "asInvoker", "double quotes");
            Check(RealDecalCheckHost.LevelIn("<requestedExecutionLevel uiAccess='false' level='requireAdministrator' />") == "requireAdministrator", "single quotes, other order");
            Check(RealDecalCheckHost.LevelIn("<assembly><description>x</description></assembly>") == null, "no element");
        });

        RegistrationScenarios();

        Console.WriteLine();
        Console.WriteLine(_failed == 0 ? $"PASS: {_checks} checks" : $"FAIL: {_failed} of {_checks} checks failed");
        return _failed == 0 ? 0 : 1;
    }

    // ── machine ─────────────────────────────────────────────────────────────

    private static Dictionary<string, object> BridgeEntry() => new()
    {
        [""] = DecalBridgeRegistration.FilterName,
        ["Enabled"] = 1,
        ["Assembly"] = DecalBridgeRegistration.AssemblyFile,
        ["Path"] = BridgeDir,
        ["Object"] = DecalBridgeRegistration.FilterObject,
        ["Surrogate"] = SurrogateClsid,
    };

    private static Dictionary<string, object> ComEntry() => new()
    {
        [""] = "mscoree.dll",
        ["Class"] = "Decal.Adapter.Surrogate",
        ["Assembly"] = "Decal.Adapter, Version=2.9.8.3, Culture=neutral, PublicKeyToken=bd1c8ce002ce221e",
        ["RuntimeVersion"] = "v4.0.30319",
        ["CodeBase"] = @"file:///C:\Program Files (x86)\Decal 3.0\Decal.Adapter.dll",
    };

    private static readonly (string Id, string Name)[] Stock =
    {
        ("{53092D1B-F0B0-46FF-BF11-8F031EC9B137}", "World Object Filter"),
        ("{34239EAD-6317-4c40-A405-193BA5232DD8}", "Echo Filter 2"),
    };

    /// <summary>A healthy 64-bit machine with Decal 2.9.8.3, UAC on, a non-elevated launcher, nothing registered.</summary>
    private static FakeHost Baseline()
    {
        var h = new FakeHost();
        h.Set(RegRoot.Machine64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", new() { ["EnableLUA"] = 1, ["EnableVirtualization"] = 1 });
        h.Set(RegRoot.Machine32, MachineDecalPath("Agent"), new() { ["AgentPath"] = Agent, ["PortalPath"] = @"C:\Turbine\Asheron's Call" });
        h.Set(RegRoot.Machine32, HkFilters, new());
        foreach (var f in Stock)
            h.Set(RegRoot.Machine32, HkFilters + "\\" + f.Id, new() { [""] = f.Name, ["Enabled"] = 1 });
        h.Set(RegRoot.Machine32, MachineDecalPath("Surrogates") + "\\" + SurrogateClsid, new() { [""] = "Decal.Adapter Surrogate", ["Enabled"] = 1 });
        h.Set(RegRoot.Machine32, @"SOFTWARE\Classes\CLSID\" + SurrogateClsid + @"\InprocServer32", ComEntry());
        h.Set(RegRoot.Machine32, @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", new() { ["Release"] = 533325 });
        h.Files[Agent + "Inject.dll"] = "";
        h.Files[Agent + "Decal.Adapter.dll"] = "";
        h.Files[@"C:\Program Files (x86)\Decal 3.0\Decal.Adapter.dll"] = "";
        h.Files[BridgeDir + "\\" + DecalBridgeRegistration.AssemblyFile] = "";
        h.Files[AcClient] = "";
        h.AssemblyVersions[Agent + "Decal.Adapter.dll"] = new Version(2, 9, 8, 3);
        h.ReferenceVersions[BridgeDir + "\\" + DecalBridgeRegistration.AssemblyFile] = new Version(2, 9, 8, 3);
        return h;
    }

    /// <summary>The machine after the installer: HKLM entry, plus the per-user entry when a copy existed.</summary>
    private static FakeHost InstalledBoth(bool withCopy, bool elevated, bool uacOff)
    {
        FakeHost h = Baseline();
        h.Set(RegRoot.Machine32, HkFilters + "\\" + Guid, BridgeEntry());
        if (withCopy)
        {
            h.Set(RegRoot.User64, VsFilters, new());
            MirrorStock(h, RegRoot.User64, VsFilters);
            h.Set(RegRoot.User64, VsFilters + "\\" + Guid, BridgeEntry());
        }
        if (uacOff)
            h.Set(RegRoot.Machine64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", new() { ["EnableLUA"] = 0, ["EnableVirtualization"] = 1 });
        h.LauncherElevated = elevated;
        h.LauncherVirtualized = !elevated && !uacOff;
        h.OwnView = BridgeEntry();   // both views list it
        return h;
    }

    private static void MirrorStock(FakeHost h, RegRoot root, string path)
    {
        foreach (var f in Stock)
            h.Set(root, path + "\\" + f.Id, new() { [""] = f.Name, ["Enabled"] = 1 });
    }

    // ── harness ─────────────────────────────────────────────────────────────

    private static bool Has(Result r, Level level, string text) =>
        r.Lines.Any(l => l.Level == level && l.Text.Contains(text, StringComparison.Ordinal));

    private static void Scenario(string name, Action body)
    {
        Console.WriteLine($"-- {name}");
        try { body(); }
        catch (Exception ex) { _checks++; _failed++; Console.WriteLine($"   FAIL  threw {ex}"); }
    }

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (!ok) _failed++;
        Console.WriteLine($"   {(ok ? "ok  " : "FAIL")}  {what}");
    }
}

internal sealed class FakeHost : IDecalCheckHost
{
    private readonly Dictionary<(RegRoot, string), Dictionary<string, object>> _keys = new(new KeyComparer());
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Zoned { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string?> Levels { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Version> AssemblyVersions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Version> ReferenceVersions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, object>? OwnView { get; set; }

    public bool Is64BitOs { get; set; } = true;
    public bool LauncherElevated { get; set; }
    public bool LauncherVirtualized { get; set; } = true;

    public void Set(RegRoot root, string path, Dictionary<string, object> values) => _keys[(root, path)] = values;
    public void Remove(RegRoot root, string path) => _keys.Remove((root, path));

    public IRegKey? OpenReal(RegRoot root, string subKey)
    {
        bool exists = _keys.TryGetValue((root, subKey), out var values);
        var subs = _keys.Keys
            .Where(k => k.Item1 == root && k.Item2.StartsWith(subKey + "\\", StringComparison.OrdinalIgnoreCase))
            .Select(k => k.Item2.Substring(subKey.Length + 1).Split('\\')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!exists && subs.Count == 0) return null;
        return Snap(values, subs);
    }

    public IRegKey? OpenOwnViewMachine32(string subKey) => OwnView == null ? null : Snap(OwnView, new List<string>());

    private static RegSnapshot Snap(Dictionary<string, object>? values, List<string> subs)
    {
        var s = new RegSnapshot();
        if (values != null) foreach (var kv in values) s.Values[kv.Key] = kv.Value;
        s.SubKeys.AddRange(subs);
        return s;
    }

    public bool FileExists(string path) => Files.ContainsKey(path);
    public bool HasZoneIdentifier(string path) => Zoned.Contains(path);
    public Version? AssemblyVersionOf(string path) => AssemblyVersions.TryGetValue(path, out var v) ? v : null;
    public Version? ReferenceVersionOf(string assemblyPath, string referenceName) =>
        referenceName == "Decal.Adapter" && ReferenceVersions.TryGetValue(assemblyPath, out var v) ? v : null;
    public string? ExecutionLevelOf(string exePath) => Levels.TryGetValue(exePath, out var l) ? l : null;
    public string? ReadTextFile(string path) => Files.TryGetValue(path, out var t) ? t : null;

    private sealed class KeyComparer : IEqualityComparer<(RegRoot, string)>
    {
        public bool Equals((RegRoot, string) a, (RegRoot, string) b) => a.Item1 == b.Item1 && string.Equals(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((RegRoot, string) k) => HashCode.Combine(k.Item1, StringComparer.OrdinalIgnoreCase.GetHashCode(k.Item2));
    }
}
