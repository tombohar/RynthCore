using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using RynthCore.Engine.Plugins;
using RynthCore.PluginSdk;
using RynthCore.PluginSdk.Manifest;

// What the SDK targets add to a plugin built with RynthPluginMinEngineApi=71 (see ManifestAttributeDrivesRuntimeMinimum).
[assembly: RynthPluginManifest(71)]

// Offline tests for plugin manifests: parsing, the engine's gate (API + dependencies + order),
// reading the embedded resource from PE files, and the SDK's API version. See the csproj.
internal static class Program
{
    private static int _fails, _asserts;
    private static void Check(bool cond, string msg)
    {
        _asserts++;
        if (!cond) { _fails++; Console.WriteLine($"  [FAIL] {msg}"); }
    }
    private static void Eq<T>(T actual, T expected, string msg) =>
        Check(EqualityComparer<T>.Default.Equals(actual, expected), $"{msg}: expected '{expected}', got '{actual}'");

    private static string _root = "";
    private const uint Api = PluginContractVersion.Current;

    private static int Main(string[] args)
    {
        _root = Path.Combine(Path.GetTempPath(), "rynth-manifest-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        try
        {
            Run(nameof(ParsesFullManifest), ParsesFullManifest);
            Run(nameof(ParsesShorthandAndIgnoresUnknown), ParsesShorthandAndIgnoresUnknown);
            Run(nameof(RejectsBrokenManifests), RejectsBrokenManifests);
            Run(nameof(RoundTripsThroughJson), RoundTripsThroughJson);
            Run(nameof(EngineApiCheck), EngineApiCheck);
            Run(nameof(VersionComparisons), VersionComparisons);
            Run(nameof(NoManifestsLoadExactlyAsListed), NoManifestsLoadExactlyAsListed);
            Run(nameof(TooNewPluginIsRefusedOthersKeepOrder), TooNewPluginIsRefusedOthersKeepOrder);
            Run(nameof(RequiredDependencyMissingRefuses), RequiredDependencyMissingRefuses);
            Run(nameof(OptionalDependencyNeverRefusesOrReorders), OptionalDependencyNeverRefusesOrReorders);
            Run(nameof(RefusalCascadesDownTheChain), RefusalCascadesDownTheChain);
            Run(nameof(RequiredDependenciesStartFirst), RequiredDependenciesStartFirst);
            Run(nameof(DependencyVersions), DependencyVersions);
            Run(nameof(DependencyOnPluginWithoutManifest), DependencyOnPluginWithoutManifest);
            Run(nameof(RequiredCycleRefusesNothing), RequiredCycleRefusesNothing);
            Run(nameof(CheckRequiredStartedAtInit), CheckRequiredStartedAtInit);
            Run(nameof(ReadsResourceFromPe32AndPe32Plus), ReadsResourceFromPe32AndPe32Plus);
            Run(nameof(NoResourceAndNotAPe), NoResourceAndNotAPe);
            Run(nameof(GatePlanFromFiles), GatePlanFromFiles);
            Run(nameof(ServerBlockParsesAndRoundTrips), ServerBlockParsesAndRoundTrips);
            Run(nameof(ServerBlockRejectsBadIds), ServerBlockRejectsBadIds);
            Run(nameof(ServerHostMatching), ServerHostMatching);
            Run(nameof(ServerGateKeepsOnlyTheHostsPlugin), ServerGateKeepsOnlyTheHostsPlugin);
            Run(nameof(ServerGateUnclaimedHost), ServerGateUnclaimedHost);
            Run(nameof(ServerGatePlanFromFiles), ServerGatePlanFromFiles);
            Run(nameof(SdkApiVersionCannotDrift), SdkApiVersionCannotDrift);
            Run(nameof(TrainingStructsMirror), TrainingStructsMirror);
            Run(nameof(ContainerOrderSlots), ContainerOrderSlots);
            Run(nameof(ManifestAttributeDrivesRuntimeMinimum), ManifestAttributeDrivesRuntimeMinimum);
            Run(nameof(DedupeSamePathTwice), DedupeSamePathTwice);
            Run(nameof(DedupeCaseAndSlashes), DedupeCaseAndSlashes);
            Run(nameof(DedupeSameNameTwoFolders), DedupeSameNameTwoFolders);
            Run(nameof(DedupeAgainstAlreadyLoaded), DedupeAgainstAlreadyLoaded);
            Run(nameof(DedupeKeepsOrderAndText), DedupeKeepsOrderAndText);
            foreach (string dll in args)
                Run("Plugin " + Path.GetFileName(dll), () => CheckBuiltPlugin(dll));
        }
        finally
        {
            try { Directory.Delete(_root, true); } catch { }
        }
        Console.WriteLine();
        Console.WriteLine(_fails == 0 && _asserts > 0 ? $"PASS ({_asserts} assertions)" : $"FAIL ({_fails} of {_asserts} assertions failed)");
        return _fails == 0 && _asserts > 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        int before = _fails;
        try { test(); }
        catch (Exception ex) { _fails++; _asserts++; Console.WriteLine($"  [FAIL] {name} threw {ex.GetType().Name}: {ex.Message}"); }
        Console.WriteLine($"{(_fails == before ? "ok  " : "FAIL")} {name}");
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static RynthPluginManifest M(string name, uint min = 66, string version = "2026.10.5.1", uint max = 0,
                                         params (string Name, string Min, bool Optional)[] deps)
    {
        var m = new RynthPluginManifest { Name = name, Version = version, MinEngineApi = min, MaxEngineApi = max };
        foreach (var d in deps) m.Dependencies.Add(new RynthPluginDependency { Name = d.Name, MinVersion = d.Min, Optional = d.Optional });
        return m;
    }

    private static PluginCandidate C(string name, RynthPluginManifest? m = null, string fileVersion = "") =>
        new($@"C:\Games\RynthSuite\{name}\RynthCore.Plugin.{name}.dll", m, fileVersion);

    private static string Names(IEnumerable<PluginCandidate> list) => string.Join(",", list.Select(c => c.Name));

    // ── Parsing ──────────────────────────────────────────────────────────────

    private static void ParsesFullManifest()
    {
        var m = RynthPluginManifest.Parse("""
            {
              "schema": 1,
              "name": "RynthOracle",
              "version": "2026.10.5.1",
              "minEngineApi": 71,
              "maxEngineApi": 90,
              "dependencies": [
                { "name": "RynthAi", "optional": true },
                { "name": "RynthNav", "minVersion": "2026.10.1.1" }
              ],
              "author": "RynthSuite",
              "description": "Quests, character, titles and leaderboards.",
              "homepage": "https://aelrynth.com/rynth.html",
              "license": "MIT"
            }
            """);
        Eq(m.Name, "RynthOracle", "name");
        Eq(m.Version, "2026.10.5.1", "version");
        Eq(m.MinEngineApi, 71u, "minEngineApi");
        Eq(m.MaxEngineApi, 90u, "maxEngineApi");
        Eq(m.Dependencies.Count, 2, "dependency count");
        Check(m.Dependencies[0].Optional && m.Dependencies[0].Name == "RynthAi", "first dependency optional RynthAi");
        Check(!m.Dependencies[1].Optional && m.Dependencies[1].MinVersion == "2026.10.1.1", "second dependency required with min version");
        Eq(m.Author, "RynthSuite", "author");
        Eq(m.License, "MIT", "license");
        Eq(m.Homepage, "https://aelrynth.com/rynth.html", "homepage");
    }

    private static void ParsesShorthandAndIgnoresUnknown()
    {
        var m = RynthPluginManifest.Parse("""{ "schema": 2, "name": "X", "minEngineApi": "70", "dependencies": ["RynthNav@2026.10.1.1", "RynthAi"], "futureField": { "a": 1 } }""");
        Eq(m.Schema, 2, "a later schema is read");
        Eq(m.MinEngineApi, 70u, "minEngineApi as a string");
        Eq(m.Dependencies[0].Name, "RynthNav", "shorthand name");
        Eq(m.Dependencies[0].MinVersion, "2026.10.1.1", "shorthand version");
        Check(!m.Dependencies[1].Optional, "shorthand is required");
        var bare = RynthPluginManifest.Parse("""{ "name": "Bare" }""");
        Eq(bare.MinEngineApi, 0u, "no minEngineApi means no limit");
        Eq(bare.Dependencies.Count, 0, "no dependencies");
    }

    private static void RejectsBrokenManifests()
    {
        string[] bad =
        {
            "not json",
            "[]",
            """{ "version": "1.0" }""",
            """{ "name": "" }""",
            """{ "name": "X", "minEngineApi": 76, "maxEngineApi": 70 }""",
            """{ "name": "X", "minEngineApi": -1 }""",
            """{ "name": "X", "minEngineApi": "abc" }""",
            """{ "name": "X", "dependencies": ["X"] }""",
            """{ "name": "X", "dependencies": [{ "name": "Y", "minVersion": "soon" }] }""",
            """{ "name": "X", "dependencies": [42] }""",
            """{ "name": "X", "dependencies": {} }""",
            """{ "name": "X", "schema": 0 }""",
        };
        foreach (string json in bad)
        {
            Check(RynthPluginManifest.TryParse(json, out string err) == null && err.Length > 0, $"refused: {json}");
        }
    }

    private static void RoundTripsThroughJson()
    {
        var m = M("RynthAi", 66, "2026.10.5.1", 0, ("RynthNav", "", true), ("RynthLua", "2026.9.1.1", false));
        m.Description = "The bot: \"combat\", buffs\nand more";
        m.License = "MIT";
        var back = RynthPluginManifest.Parse(m.ToJson());
        Eq(back.Name, m.Name, "name");
        Eq(back.Description, m.Description, "description with quotes and newline");
        Eq(back.Dependencies.Count, 2, "dependencies");
        Check(back.Dependencies[0].Optional && !back.Dependencies[1].Optional, "optional flags");
        Eq(back.Dependencies[1].MinVersion, "2026.9.1.1", "min version");
        Check(!m.ToJson().Contains("maxEngineApi"), "no maxEngineApi written when unset");
    }

    private static void EngineApiCheck()
    {
        Eq(M("RynthOracle", 77).CheckEngine(76), "RynthOracle needs a newer RynthCore (API 77)", "too new");
        Check(M("RynthOracle", 76).CheckEngine(76) == null, "exact minimum runs");
        Check(M("RynthOracle", 66).CheckEngine(76) == null, "older minimum runs");
        Check(M("Old", 60, max: 70).CheckEngine(76)?.Contains("too old") == true, "past maxEngineApi refused");
        Check(M("Old", 60, max: 76).CheckEngine(76) == null, "at maxEngineApi runs");
        Eq(RynthPluginManifest.NameFromFile(@"C:\x\RynthCore.Plugin.RynthNav.dll"), "RynthNav", "name from file");
        Eq(RynthPluginManifest.NameFromFile(@"C:\x\Other.dll"), "Other", "name from other file");
    }

    private static void VersionComparisons()
    {
        Check(PluginVersion.Satisfies("2026.10.5.1", "2026.10.1.1"), "newer satisfies");
        Check(PluginVersion.Satisfies("2026.10.1.1", "2026.10.1.1"), "equal satisfies");
        Check(!PluginVersion.Satisfies("2026.9.30.4", "2026.10.1.1"), "older doesn't");
        Check(PluginVersion.Satisfies("2026.10.5.1+8ca322f", "2026.10.5"), "build metadata ignored, 3 vs 4 parts");
        Check(PluginVersion.Satisfies("1.0.0", "2026.10.1.1"), "dev build satisfies anything");
        Check(PluginVersion.Satisfies("1.0.0+abc", "2026.10.1.1"), "dev build with commit satisfies");
        Check(PluginVersion.IsDevBuild("1.0.0.0") && !PluginVersion.IsDevBuild("1.0.1"), "dev build detection");
        Check(!PluginVersion.Satisfies("", "2026.10.1.1"), "no version doesn't satisfy a minimum");
        Check(PluginVersion.Satisfies("", ""), "no minimum, anything goes");
        Check(PluginVersion.TryParse("v2.0.0-beta", out var v) && v.Major == 2, "v prefix and prerelease");
    }

    // ── Resolution (the engine's load plan) ──────────────────────────────────

    private static void NoManifestsLoadExactlyAsListed()
    {
        var list = new[] { C("RynthChat"), C("RynthAi"), C("RynthNav"), C("Zed") };
        PluginResolution r = PluginManifestResolver.Resolve(list, Api);
        Eq(Names(r.Order), "RynthChat,RynthAi,RynthNav,Zed", "order unchanged");
        Eq(r.Refused.Count, 0, "nothing refused");
        Eq(r.Notes.Count, 0, "nothing to say");
    }

    private static void TooNewPluginIsRefusedOthersKeepOrder()
    {
        var list = new[] { C("RynthChat"), C("RynthOracle", M("RynthOracle", Api + 1)), C("RynthAi", M("RynthAi", 66)) };
        PluginResolution r = PluginManifestResolver.Resolve(list, Api);
        Eq(Names(r.Order), "RynthChat,RynthAi", "the rest load in order");
        Eq(r.Refused.Count, 1, "one refused");
        Eq(r.Refused[0].Reason, $"RynthOracle needs a newer RynthCore (API {Api + 1})", "reason fit for chat");
        Check(r.Refused[0].IsEngineMismatch, "engine mismatch flagged");
    }

    private static void RequiredDependencyMissingRefuses()
    {
        var list = new[] { C("RynthLua", M("RynthLua", 66, deps: ("RynthAi", "", false))), C("RynthChat") };
        PluginResolution r = PluginManifestResolver.Resolve(list, Api);
        Eq(Names(r.Order), "RynthChat", "dependent not loaded");
        Eq(r.Refused.Single().Reason, "RynthLua needs the RynthAi plugin, which is not in the plugin list", "reason");
        Check(!r.Refused[0].IsEngineMismatch, "dependency refusal flagged");
    }

    private static void OptionalDependencyNeverRefusesOrReorders()
    {
        // RynthAi and RynthLua can each use the other: optional both ways is no cycle and no reorder.
        var list = new[]
        {
            C("RynthAi", M("RynthAi", 66, deps: new[] { ("RynthNav", "", true), ("RynthLua", "", true), ("RynthNet", "", true) })),
            C("RynthLua", M("RynthLua", 66, deps: ("RynthAi", "", true))),
            C("RynthNet", M("RynthNet", 66, deps: ("RynthAi", "", true))),
        };
        PluginResolution r = PluginManifestResolver.Resolve(list, Api);
        Eq(Names(r.Order), "RynthAi,RynthLua,RynthNet", "order unchanged");
        Eq(r.Refused.Count, 0, "nothing refused");
        Check(r.Notes.Any(n => n.Contains("optional plugin RynthNav is not loaded")), "missing optional noted");
    }

    private static void RefusalCascadesDownTheChain()
    {
        var list = new[]
        {
            C("C", M("C", 66, deps: ("B", "", false))),
            C("B", M("B", 66, deps: ("A", "", false))),
            C("A", M("A", Api + 5)),
            C("D"),
        };
        PluginResolution r = PluginManifestResolver.Resolve(list, Api);
        Eq(Names(r.Order), "D", "only the independent plugin loads");
        Eq(r.Refused.Count, 3, "three refused");
        Check(r.Refused.Any(x => x.Plugin.Name == "B" && x.Reason == "B needs A, which was not loaded"), "B refused for A");
        Check(r.Refused.Any(x => x.Plugin.Name == "C" && x.Reason == "C needs B, which was not loaded"), "C refused for B");
    }

    private static void RequiredDependenciesStartFirst()
    {
        var list = new[]
        {
            C("RynthChat"),
            C("Addon", M("Addon", 66, deps: ("RynthNav", "", false))),
            C("RynthTracker"),
            C("RynthNav", M("RynthNav", 66)),
            C("RynthVision"),
        };
        PluginResolution r = PluginManifestResolver.Resolve(list, Api);
        Eq(Names(r.Order), "RynthChat,RynthNav,Addon,RynthTracker,RynthVision", "RynthNav moved before Addon, the rest as listed");
    }

    private static void DependencyVersions()
    {
        var tooOld = new[] { C("Addon", M("Addon", 66, deps: ("RynthNav", "2026.10.1.1", false))), C("RynthNav", M("RynthNav", 66, "2026.9.30.2")) };
        PluginResolution r = PluginManifestResolver.Resolve(tooOld, Api);
        Eq(Names(r.Order), "RynthNav", "too-old dependency refuses the dependent");
        Eq(r.Refused.Single().Reason, "Addon needs RynthNav 2026.10.1.1 or newer (this one is 2026.9.30.2)", "reason");

        var dev = new[] { C("Addon", M("Addon", 66, deps: ("RynthNav", "2026.10.1.1", false))), C("RynthNav", M("RynthNav", 66, "1.0.0")) };
        r = PluginManifestResolver.Resolve(dev, Api);
        Eq(Names(r.Order), "RynthNav,Addon", "a dev build satisfies");
        Check(r.Notes.Any(n => n.Contains("dev build")), "dev build noted");

        var optOld = new[] { C("Addon", M("Addon", 66, deps: ("RynthNav", "2026.10.1.1", true))), C("RynthNav", M("RynthNav", 66, "2026.9.1.1")) };
        r = PluginManifestResolver.Resolve(optOld, Api);
        Eq(r.Refused.Count, 0, "an old optional dependency refuses nothing");
        Check(r.Notes.Any(n => n.Contains("older")), "old optional noted");
    }

    private static void DependencyOnPluginWithoutManifest()
    {
        // RynthNav from before manifests: found by its file name, versioned by its version resource.
        var list = new[] { C("Addon", M("Addon", 66, deps: ("RynthNav", "2026.10.1.1", false))), C("RynthNav", null, "2026.10.4.3+abcdef") };
        PluginResolution r = PluginManifestResolver.Resolve(list, Api);
        Eq(Names(r.Order), "RynthNav,Addon", "satisfied by file name and version resource");

        var noVersion = new[] { C("Addon", M("Addon", 66, deps: ("RynthNav", "2026.10.1.1", false))), C("RynthNav") };
        r = PluginManifestResolver.Resolve(noVersion, Api);
        Check(r.Refused.Single().Reason.Contains("has no version"), "an unversioned plugin can't meet a minimum");

        var anyVersion = new[] { C("Addon", M("Addon", 66, deps: ("RynthNav", "", false))), C("RynthNav") };
        r = PluginManifestResolver.Resolve(anyVersion, Api);
        Eq(r.Refused.Count, 0, "no minimum: any copy will do");
    }

    private static void RequiredCycleRefusesNothing()
    {
        var list = new[] { C("A", M("A", 66, deps: ("B", "", false))), C("B", M("B", 66, deps: ("A", "", false))), C("Z") };
        PluginResolution r = PluginManifestResolver.Resolve(list, Api);
        Eq(r.Refused.Count, 0, "a cycle isn't a reason to refuse");
        Eq(r.Order.Count, 3, "all load");
        Check(r.Notes.Any(n => n.Contains("cycle")), "cycle noted");
    }

    private static void CheckRequiredStartedAtInit()
    {
        var m = M("Addon", 66, deps: new[] { ("RynthNav", "", false), ("RynthAi", "", true) });
        Check(PluginManifestGate.CheckRequiredStarted(null, _ => null) == null, "no manifest, no check");
        Check(PluginManifestGate.CheckRequiredStarted(m, n => n == "RynthNav" ? true : null) == null, "required started, optional absent: fine");
        Eq(PluginManifestGate.CheckRequiredStarted(m, n => n == "RynthNav" ? false : true), "Addon needs RynthNav, which failed to start", "required failed");
        Eq(PluginManifestGate.CheckRequiredStarted(m, _ => null), "Addon needs RynthNav, which is not loaded", "required missing");
    }

    // ── PE files ─────────────────────────────────────────────────────────────

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr BeginUpdateResourceW(string file, bool deleteExisting);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UpdateResourceW(IntPtr h, IntPtr type, string name, ushort lang, byte[] data, uint cb);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool EndUpdateResourceW(IntPtr h, bool discard);

    /// <summary>What the build's RynthEmbedPluginManifest task does.</summary>
    private static void Embed(string path, string json, string name = RynthPluginManifest.ResourceName)
    {
        IntPtr h = BeginUpdateResourceW(path, false);
        if (h == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        byte[] data = Encoding.UTF8.GetBytes(json);
        if (!UpdateResourceW(h, (IntPtr)RynthPluginManifest.ResourceType, name, 0, data, (uint)data.Length))
        {
            int err = Marshal.GetLastWin32Error();
            EndUpdateResourceW(h, true);
            throw new Win32Exception(err);
        }
        if (!EndUpdateResourceW(h, false)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static string CopyOf(string source, string name)
    {
        string dest = Path.Combine(_root, Guid.NewGuid().ToString("N")[..8], name);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(source, dest);
        return dest;
    }

    private static string ThisDll => typeof(Program).Assembly.Location;
    private static string ThisExe => Path.ChangeExtension(ThisDll, ".exe");

    private static void ReadsResourceFromPe32AndPe32Plus()
    {
        // The test's own IL DLL is PE32 (AnyCPU); its apphost .exe is a native PE32+ image.
        foreach (string source in new[] { ThisDll, ThisExe })
        {
            string copy = CopyOf(source, "RynthCore.Plugin.Probe" + Path.GetExtension(source));
            Check(PluginManifestReader.Read(copy) == null, $"{Path.GetFileName(source)}: no manifest before embedding");
            string json = M("Probe", 71, deps: ("RynthAi", "", true)).ToJson();
            Embed(copy, json);
            RynthPluginManifest? m = PluginManifestReader.Read(copy);
            Check(m != null, $"{Path.GetFileName(source)}: manifest read back");
            Eq(m?.MinEngineApi ?? 0, 71u, $"{Path.GetFileName(source)}: minEngineApi");
            Eq(PluginManifestReader.ReadJson(copy), json, $"{Path.GetFileName(source)}: exact JSON");
            Check(!string.IsNullOrEmpty(System.Diagnostics.FileVersionInfo.GetVersionInfo(copy).ProductVersion),
                  $"{Path.GetFileName(source)}: version resource survives");
            // Replace in place (a rebuild): the new one wins.
            Embed(copy, M("Probe", 72).ToJson());
            Eq(PluginManifestReader.Read(copy)?.MinEngineApi ?? 0, 72u, $"{Path.GetFileName(source)}: re-embedded");
            // Readable while another process has it open for execution-style sharing (the engine's shadow copy case).
            using (new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read))
                Check(PluginManifestReader.Read(copy) != null, $"{Path.GetFileName(source)}: readable while open");
        }
        // Lower-case name in the file still matches (resource names are case-insensitive).
        string lower = CopyOf(ThisDll, "lower.dll");
        Embed(lower, M("Lower", 66).ToJson(), "rynth_plugin_manifest");
        Eq(PluginManifestReader.Read(lower)?.Name, "Lower", "case-insensitive resource name");
    }

    private static void NoResourceAndNotAPe()
    {
        string text = Path.Combine(_root, "notes.dll");
        File.WriteAllText(text, "this is not a DLL, just text long enough to have a header-sized body .................................");
        try { PluginManifestReader.Read(text); Check(false, "a text file should throw"); }
        catch (InvalidDataException) { Check(true, "text file refused as not a PE"); }

        string tiny = Path.Combine(_root, "tiny.dll");
        File.WriteAllBytes(tiny, new byte[] { 0x4D, 0x5A });
        try { PluginManifestReader.Read(tiny); Check(false, "a 2-byte file should throw"); }
        catch (InvalidDataException) { Check(true, "truncated file refused"); }

        // Another RCDATA resource, not ours: still "no manifest".
        string other = CopyOf(ThisDll, "other.dll");
        Embed(other, "{}", "SOMETHING_ELSE");
        Check(PluginManifestReader.Read(other) == null, "other resources are not a manifest");
    }

    private static void GatePlanFromFiles()
    {
        string dir = Path.Combine(_root, "suite");
        Directory.CreateDirectory(dir);
        string P(string name) => Path.Combine(dir, $"RynthCore.Plugin.{name}.dll");
        File.Copy(ThisDll, P("RynthChat"));                        // no manifest: loads as before
        File.Copy(ThisDll, P("RynthOracle"));
        Embed(P("RynthOracle"), M("RynthOracle", Api + 1).ToJson());
        File.Copy(ThisDll, P("RynthAi"));
        Embed(P("RynthAi"), M("RynthAi", 66, deps: ("RynthLua", "", true)).ToJson());
        File.Copy(ThisDll, P("Broken"));
        Embed(P("Broken"), "{ \"name\": ");                       // broken JSON: loads as without one
        File.Copy(ThisDll, P("Needy"));
        Embed(P("Needy"), M("Needy", 66, deps: ("RynthNav", "", false)).ToJson());

        var paths = new[] { P("RynthChat"), P("RynthOracle"), P("RynthAi"), P("Broken"), P("Needy") };
        PluginManifestGate.LoadPlan plan = PluginManifestGate.Plan(paths, Api, _ => "2026.10.4.3");
        Eq(string.Join(",", plan.Load.Select(l => RynthPluginManifest.NameFromFile(l.Path))), "RynthChat,RynthAi,Broken", "load list");
        Check(plan.Load.Single(l => l.Path == P("RynthChat")).Manifest == null, "RynthChat has no manifest");
        Check(plan.Load.Single(l => l.Path == P("RynthAi")).Manifest?.Name == "RynthAi", "RynthAi carries its manifest");
        Eq(plan.Refused.Count, 2, "two refused");
        Check(plan.Refused.Any(r => r.Reason == $"RynthOracle needs a newer RynthCore (API {Api + 1})"), "Oracle refused for API");
        Check(plan.Refused.Any(r => r.Reason.StartsWith("Needy needs the RynthNav plugin")), "Needy refused for RynthNav");
        Check(plan.Log.Any(l => l.Contains("Broken") && l.Contains("manifest unreadable")), "broken manifest logged");
        Check(plan.Log.Any(l => l.Contains("optional plugin RynthLua")), "optional note logged");

        PluginManifestGate.LoadPlan none = PluginManifestGate.Plan(new[] { P("RynthChat"), P("Broken") }, Api);
        Eq(none.Load.Count, 2, "no manifests: everything loads");
        Check(none.Log.Any(l => l.Contains("no plugin has a manifest")), "said once");
    }

    // ── Server plugins (the manifest's "server" block and the load gate) ────

    private static RynthPluginManifest S(string name, string id, string[] hosts, params string[] worlds)
    {
        var m = M(name);
        m.Server = new RynthPluginServer { Id = id, Name = name, Hosts = hosts.ToList(), WorldNames = worlds.ToList() };
        return m;
    }

    private static void ServerBlockParsesAndRoundTrips()
    {
        var m = RynthPluginManifest.Parse("""
            { "name": "RynthColdeve", "minEngineApi": 66,
              "server": { "id": "Coldeve", "name": "Coldeve", "hosts": ["play.coldeve.ac", "", 5], "worldNames": ["Coldeve"] } }
            """);
        Check(m.Server != null, "server block read");
        Eq(m.Server!.Id, "coldeve", "id lower-cased");
        Eq(string.Join(",", m.Server.Hosts), "play.coldeve.ac", "blank and non-string hosts dropped");
        Eq(string.Join(",", m.Server.WorldNames), "Coldeve", "world names");
        var back = RynthPluginManifest.Parse(m.ToJson());
        Eq(back.Server?.Id, "coldeve", "round trip id");
        Eq(back.Server?.Hosts.Count, 1, "round trip hosts");
        Check(RynthPluginManifest.Parse("""{ "name": "RynthAi" }""").Server == null, "no block, not a server plugin");
        Check(!M("RynthAi").ToJson().Contains("\"server\""), "no server block written for other plugins");
    }

    private static void ServerBlockRejectsBadIds()
    {
        string[] bad =
        {
            """{ "name": "X", "server": "coldeve" }""",
            """{ "name": "X", "server": { "name": "No id" } }""",
            """{ "name": "X", "server": { "id": "has space" } }""",
            """{ "name": "X", "server": { "id": "ok", "hosts": "not-an-array" } }""",
        };
        foreach (string json in bad)
            Check(RynthPluginManifest.TryParse(json, out string err) == null && err.Length > 0, $"refused: {json}");
    }

    private static void ServerHostMatching()
    {
        var ilt = new RynthPluginServer { Id = "ilt", Hosts = { "infiniteleaftide.online" } };
        Check(ilt.MatchesHost("game.infiniteleaftide.online", out string hit) && hit == "infiniteleaftide.online", "a host under the name");
        Check(ilt.MatchesHost("INFINITELEAFTIDE.ONLINE.", out _), "case and trailing dot ignored");
        Check(!ilt.MatchesHost("notinfiniteleaftide.online", out _), "not a suffix without the dot");
        var ip = new RynthPluginServer { Id = "aelrynth", Hosts = { "144.217.84.148" } };
        Check(ip.MatchesHost("144.217.84.148", out _), "an IP matches itself");
        Check(!ip.MatchesHost("1.144.217.84.148", out _), "an IP never matches as a suffix");
        var wild = new RynthPluginServer { Id = "x", Hosts = { "*.ddns.net" } };
        Check(wild.MatchesHost("frostcull.ddns.net", out _), "wildcard");
        Check(!wild.MatchesHost("", out _), "no host, no match");
    }

    private static void ServerGateKeepsOnlyTheHostsPlugin()
    {
        var list = new List<PluginCandidate>
        {
            C("RynthAi", M("RynthAi")),
            C("RynthChat"),
            C("RynthColdeve", S("RynthColdeve", "coldeve", new[] { "play.coldeve.ac" }, "Coldeve")),
            C("RynthLeafDawn", S("RynthLeafDawn", "leafdawn", new[] { "leafdawn.hopto.org" }, "LeafDawn")),
            C("RynthLeafdawning", S("RynthLeafdawning", "leafdawning", new[] { "leafdawning.duckdns.org" }, "Leafdawning")),
        };
        var log = new List<string>();
        HashSet<string> skip = PluginManifestGate.SelectServerPlugins(list, "leafdawn.hopto.org", false, log);
        Eq(skip.Count, 2, "the two other server plugins are left out");
        Check(skip.Contains(list[2].Key) && skip.Contains(list[4].Key), "Coldeve and Leafdawning left out");
        Check(!skip.Contains(list[0].Key) && !skip.Contains(list[1].Key), "other plugins always load");
        Check(log.Any(l => l.Contains("RynthColdeve") && l.Contains("not loaded")), "logged why");
        Eq(PluginManifestGate.SelectServerPlugins(list, "leafdawn.hopto.org", true, log).Count, 0, "LoadAllServerPlugins keeps every one");
    }

    private static void ServerGateUnclaimedHost()
    {
        var list = new List<PluginCandidate>
        {
            C("RynthColdeve", S("RynthColdeve", "coldeve", new[] { "play.coldeve.ac" })),
            C("RynthFrostcull", S("RynthFrostcull", "frostcull", new[] { "frostcull.ddns.net" })),
        };
        var log = new List<string>();
        Eq(PluginManifestGate.SelectServerPlugins(list, "", false, log).Count, 0, "no connect host: all load");
        Eq(PluginManifestGate.SelectServerPlugins(list, "10.0.0.5", false, log).Count, 2, "a host nobody lists: none load now (late load by world name or a pick)");
        Check(log.Any(l => l.Contains("none lists host 10.0.0.5")), "said why");
    }

    private static void ServerGatePlanFromFiles()
    {
        string dir = Path.Combine(_root, "servers");
        Directory.CreateDirectory(dir);
        string P(string name) => Path.Combine(dir, $"RynthCore.Plugin.{name}.dll");
        File.Copy(ThisDll, P("RynthAi"));
        Embed(P("RynthAi"), M("RynthAi").ToJson());
        File.Copy(ThisDll, P("RynthILT"));
        Embed(P("RynthILT"), S("RynthILT", "ilt", new[] { "infiniteleaftide.online" }, "*Leaftide*").ToJson());
        File.Copy(ThisDll, P("RynthAelrynth"));
        Embed(P("RynthAelrynth"), S("RynthAelrynth", "aelrynth", new[] { "144.217.84.148" }, "Aelrynth*").ToJson());

        var paths = new[] { P("RynthAi"), P("RynthILT"), P("RynthAelrynth") };
        PluginManifestGate.LoadPlan onIlt = PluginManifestGate.Plan(paths, Api, null, "game.infiniteleaftide.online");
        Eq(string.Join(",", onIlt.Load.Select(l => RynthPluginManifest.NameFromFile(l.Path))), "RynthAi,RynthILT", "on ILT");
        Eq(onIlt.SkippedForServer.Count, 1, "Aelrynth's plugin skipped");
        // The skipped one keeps its manifest: Options > Diagnostics lists its server, and forcing it loads it.
        Eq(onIlt.SkippedServerPlugins.Count, 1, "skipped server plugin kept with its manifest");
        Eq(onIlt.SkippedServerPlugins[0].Manifest.Server?.Id ?? "", "aelrynth", "its server id");
        Eq(onIlt.SkippedServerPlugins[0].Path, P("RynthAelrynth"), "its path");
        Eq(onIlt.Refused.Count, 0, "skipping is not a refusal (no chat notice)");
        Check(onIlt.Log.Any(l => l.Contains("server ilt")), "the manifest line names the server");
        PluginManifestGate.LoadPlan onAelrynth = PluginManifestGate.Plan(paths, Api, null, "144.217.84.148");
        Eq(string.Join(",", onAelrynth.Load.Select(l => RynthPluginManifest.NameFromFile(l.Path))), "RynthAi,RynthAelrynth", "on Aelrynth");
    }

    // ── The SDK's version ────────────────────────────────────────────────────

    private static void SdkApiVersionCannotDrift()
    {
        Eq(RynthCoreHost.CurrentApiVersion, PluginContractVersion.Current, "RynthCoreHost.CurrentApiVersion");
        Check(RynthCoreHost.BaselineApiVersion <= RynthCoreHost.CurrentApiVersion, "baseline at or below current");

        // The SDK's copy of the table must reach as far as the engine's (same field count, same size).
        int sdkFields = typeof(RynthCoreApiNative).GetFields().Length;
        int engineFields = typeof(RynthCoreAPI).GetFields().Length;
        Eq(sdkFields, engineFields, "RynthCoreApiNative fields vs the engine's RynthCoreAPI (add the new fields to the SDK)");
        Eq(Marshal.SizeOf<RynthCoreApiNative>(), Marshal.SizeOf<RynthCoreAPI>(), "table size");

        // The build targets' copy, used to refuse a RynthPluginMinEngineApi the SDK doesn't know.
        string targets = FindUp(Path.Combine("src", "RynthCore.PluginSdk", "build", "RynthCore.PluginSdk.targets"));
        Match match = Regex.Match(File.ReadAllText(targets), @"<RynthCoreSdkApiVersion>(\d+)</RynthCoreSdkApiVersion>");
        Check(match.Success, "targets declare RynthCoreSdkApiVersion");
        Eq(match.Success ? uint.Parse(match.Groups[1].Value) : 0u, PluginContractVersion.Current, "RynthCoreSdkApiVersion in RynthCore.PluginSdk.targets");

        // Release (shared-SDK) builds raise a minimum below the managed-plugin API to the SDK's API
        // (Publish-Update.ps1 checks for this rule before building).
        string targetsText = File.ReadAllText(targets);
        Match managed = Regex.Match(targetsText, @"<RynthCoreManagedPluginApi>(\d+)</RynthCoreManagedPluginApi>");
        Check(managed.Success && uint.Parse(managed.Groups[1].Value) == 88u, "targets: managed plugins need API 88 (RynthCoreManagedPluginApi)");
        Check(targetsText.Contains("'$(RynthPluginSharedSdk)' == 'true'") &&
              targetsText.Contains("MinEngineApi=\"$(_RynthPluginEffectiveMinEngineApi)\"") &&
              targetsText.Contains("<_Parameter1>$(_RynthPluginEffectiveMinEngineApi)</_Parameter1>"),
            "targets: the manifest and the runtime attribute both use the derived minimum");
    }

    // v79: the SDK's copies of the training structs must be the engine's, byte for byte.
    private static void TrainingStructsMirror()
    {
        Eq(Marshal.SizeOf<RynthCore.PluginSdk.TrainingInfoNative>(), 64, "SDK TrainingInfoNative size");
        Eq(Marshal.SizeOf<RynthCore.Engine.Plugins.TrainingInfoNative>(), 64, "engine TrainingInfoNative size");
        Eq(Marshal.SizeOf<RynthCore.PluginSdk.TrainingEntryNative>(), 64, "SDK TrainingEntryNative size");
        Eq(Marshal.SizeOf<RynthCore.Engine.Plugins.TrainingEntryNative>(), 64, "engine TrainingEntryNative size");
        foreach (var f in typeof(RynthCore.Engine.Plugins.TrainingInfoNative).GetFields())
            Eq((int)Marshal.OffsetOf<RynthCore.PluginSdk.TrainingInfoNative>(f.Name), (int)Marshal.OffsetOf<RynthCore.Engine.Plugins.TrainingInfoNative>(f.Name), "TrainingInfoNative." + f.Name + " offset");
        foreach (var f in typeof(RynthCore.Engine.Plugins.TrainingEntryNative).GetFields())
            Eq((int)Marshal.OffsetOf<RynthCore.PluginSdk.TrainingEntryNative>(f.Name), (int)Marshal.OffsetOf<RynthCore.Engine.Plugins.TrainingEntryNative>(f.Name), "TrainingEntryNative." + f.Name + " offset");
        Eq(RynthCore.PluginSdk.TrainingInfoFlags.Busy, RynthCore.Engine.Plugins.TrainingInfoFlags.Busy, "info flag bits");
        Eq(RynthCore.PluginSdk.TrainingEntryFlags.Trainable, RynthCore.Engine.Plugins.TrainingEntryFlags.Trainable, "entry flag bits");
        // The three v79 slots follow v78's, then v80's two and v81's GetMergeStackResultFn (v82-v91 follow:
        // eleven slots to v89, then v90's thirteen and v91's two).
        var names = typeof(RynthCore.Engine.Plugins.RynthCoreAPI).GetFields().Select(f => f.Name).SkipLast(26).TakeLast(6).ToArray();
        Check(names.SequenceEqual(new[] { "GetTrainingInfoFn", "RaiseFn", "TrainSkillFn", "GetContainerOrderFn", "PutItemInContainerFn", "GetMergeStackResultFn" }),
            "v79, v80 then v81 slots last: " + string.Join(",", names));
        var sdkNames = typeof(RynthCore.PluginSdk.RynthCoreApiNative).GetFields().Select(f => f.Name).SkipLast(26).TakeLast(6).ToArray();
        Check(sdkNames.SequenceEqual(names), "SDK v79, v80 and v81 slots in the same order");
    }

    // v80: GetContainerOrder and PutItemInContainer come right before v81's slot, engine and SDK alike;
    // v81: GetMergeStackResult, then v82: SalvageDirect, v83's two and v84's GetObjectProperties last. The whole table has the same field names in the same
    // order on both sides, and the same size.
    private static void ContainerOrderSlots()
    {
        var all = typeof(RynthCore.Engine.Plugins.RynthCoreAPI).GetFields().Select(f => f.Name).ToArray();
        var sdkAll = typeof(RynthCore.PluginSdk.RynthCoreApiNative).GetFields().Select(f => f.Name).ToArray();
        // v91: the ambient light floor's two slots end the table (2026-10-27); the checks after these are
        // on the table before them.
        string[] v91 = { "SetAmbientLightFloorFn", "GetAmbientLightFn" };
        Check(all.TakeLast(v91.Length).SequenceEqual(v91), "v91 slots last: " + string.Join(",", all.TakeLast(v91.Length)));
        Check(sdkAll.TakeLast(v91.Length).SequenceEqual(v91), "SDK v91 slots last: " + string.Join(",", sdkAll.TakeLast(v91.Length)));
        foreach (string slot in v91)
            Eq((int)Marshal.OffsetOf<RynthCore.PluginSdk.RynthCoreApiNative>(slot),
               (int)Marshal.OffsetOf<RynthCore.Engine.Plugins.RynthCoreAPI>(slot), slot + " offset");
        Eq((int)Marshal.OffsetOf<RynthCore.Engine.Plugins.RynthCoreAPI>("SetAmbientLightFloorFn"),
           (int)Marshal.OffsetOf<RynthCore.Engine.Plugins.RynthCoreAPI>("DropTargetFn") + IntPtr.Size, "SetAmbientLightFloorFn right after DropTargetFn");
        all = all.SkipLast(v91.Length).ToArray();
        sdkAll = sdkAll.SkipLast(v91.Length).ToArray();
        // v90: the frameless-window slots and the settings drawers end the table; the checks after these
        // are on the table before them.
        string[] v90 = { "PanelDragWithLastItemFn", "SetPanelPopOutFn", "PanelTextSizeFn", "RenderSettingsDrawerFn",
                         "GetShortcutsFn", "ShortcutActionFn", "GetActionKeysFn", "GetIconTextureFn", "KeyCaptureFn", "SetActionKeyFn", "AcPanelFn", "PanelScaleFn", "DropTargetFn" };
        Check(all.TakeLast(v90.Length).SequenceEqual(v90), "v90 slots last: " + string.Join(",", all.TakeLast(v90.Length)));
        Check(sdkAll.TakeLast(v90.Length).SequenceEqual(v90), "SDK v90 slots last: " + string.Join(",", sdkAll.TakeLast(v90.Length)));
        foreach (string slot in v90)
            Eq((int)Marshal.OffsetOf<RynthCore.PluginSdk.RynthCoreApiNative>(slot),
               (int)Marshal.OffsetOf<RynthCore.Engine.Plugins.RynthCoreAPI>(slot), slot + " offset");
        Eq((int)Marshal.OffsetOf<RynthCore.Engine.Plugins.RynthCoreAPI>("PanelDragWithLastItemFn"),
           (int)Marshal.OffsetOf<RynthCore.Engine.Plugins.RynthCoreAPI>("SetPanelOpenFn") + IntPtr.Size, "PanelDragWithLastItemFn right after SetPanelOpenFn");
        var names = all.SkipLast(v90.Length).ToArray();
        var sdkNames = sdkAll.SkipLast(v90.Length).ToArray();
        // v81's GetMergeStackResultFn follows v80's two slots.
        Check(names.SkipLast(12).TakeLast(2).SequenceEqual(new[] { "GetContainerOrderFn", "PutItemInContainerFn" }), "v80 slots before v81's to v85's: " + string.Join(",", names.SkipLast(12).TakeLast(2)));
        Check(sdkNames.SkipLast(12).TakeLast(2).SequenceEqual(names.SkipLast(12).TakeLast(2)), "SDK v80 slots in the same order");
        Check(names.SkipLast(10).Last() == "SalvageDirectFn", "v82 slot before v83's");
        // v83: GetObjectServerAge and RemoveClientObject (ghost creatures), then v84's slot.
        Check(names.SkipLast(8).TakeLast(2).SequenceEqual(new[] { "GetObjectServerAgeFn", "RemoveClientObjectFn" }), "v83 slots before v84's: " + string.Join(",", names.SkipLast(8).TakeLast(2)));
        // v84: GetObjectProperties (every property of an object, /ub propertydump), then v85's two.
        Check(names.SkipLast(7).Last() == "GetObjectPropertiesFn", "v84 slot before v85's: " + names.SkipLast(7).Last());
        // v85: ArmConfirmation and GetLastConfirmation (the server's yes/no prompts), then v86's slot.
        Check(names.SkipLast(5).TakeLast(2).SequenceEqual(new[] { "ArmConfirmationFn", "GetLastConfirmationFn" }), "v85 slots before v86's: " + string.Join(",", names.SkipLast(5).TakeLast(2)));
        // v86: GetServerStateJson (which server this is, for every plugin), then v87's slot.
        Check(names.SkipLast(4).Last() == "GetServerStateJsonFn", "v86 slot before v87's: " + names.SkipLast(4).Last());
        // v87: GetWieldedItems (what the player wields, from the server's messages), then v89's three.
        Check(names.SkipLast(3).Last() == "GetWieldedItemsFn", "v87 slot before v89's: " + names.SkipLast(3).Last());
        // v89: plugin windows (the engine's look, panels by key) end the table before v90's.
        Check(names.TakeLast(3).SequenceEqual(new[] { "GetUiColorFn", "GetUiFontFn", "SetPanelOpenFn" }), "v89 slots before v90's: " + string.Join(",", names.TakeLast(3)));
        Check(typeof(RynthCore.PluginSdk.RynthCoreApiNative).GetFields().Select(f => f.Name).SequenceEqual(typeof(RynthCore.Engine.Plugins.RynthCoreAPI).GetFields().Select(f => f.Name)), "SDK table has the engine's slots in the same order");
        Eq(Marshal.SizeOf<RynthCore.PluginSdk.RynthCoreApiNative>(), Marshal.SizeOf<RynthCore.Engine.Plugins.RynthCoreAPI>(), "SDK table size = engine table size");
        Eq((int)Marshal.OffsetOf<RynthCore.PluginSdk.RynthCoreApiNative>("PutItemInContainerFn"),
           (int)Marshal.OffsetOf<RynthCore.Engine.Plugins.RynthCoreAPI>("PutItemInContainerFn"), "PutItemInContainerFn offset");
        Eq((int)Marshal.OffsetOf<RynthCore.PluginSdk.RynthCoreApiNative>("SalvageDirectFn"),
           (int)Marshal.OffsetOf<RynthCore.Engine.Plugins.RynthCoreAPI>("SalvageDirectFn"), "SalvageDirectFn offset");
        // ClientHelperHooks.MaxSalvageDirectItems (engine side, not linked here) is 100 too.
        Eq(RynthCore.PluginSdk.RynthCoreHost.MaxSalvageDirectItems, 100, "SalvageDirect item cap");
        Eq((int)Marshal.OffsetOf<RynthCore.PluginSdk.RynthCoreApiNative>("RemoveClientObjectFn"),
           (int)Marshal.OffsetOf<RynthCore.Engine.Plugins.RynthCoreAPI>("RemoveClientObjectFn"), "RemoveClientObjectFn offset");
        Eq((int)Marshal.OffsetOf<RynthCore.PluginSdk.RynthCoreApiNative>("GetObjectPropertiesFn"),
           (int)Marshal.OffsetOf<RynthCore.Engine.Plugins.RynthCoreAPI>("GetObjectPropertiesFn"), "GetObjectPropertiesFn offset");
        Eq((int)Marshal.OffsetOf<RynthCore.Engine.Plugins.RynthCoreAPI>("GetObjectPropertiesFn"),
           (int)Marshal.OffsetOf<RynthCore.Engine.Plugins.RynthCoreAPI>("RemoveClientObjectFn") + IntPtr.Size, "GetObjectPropertiesFn right after RemoveClientObjectFn");
        Eq((int)Marshal.OffsetOf<RynthCore.PluginSdk.RynthCoreApiNative>("ArmConfirmationFn"),
           (int)Marshal.OffsetOf<RynthCore.Engine.Plugins.RynthCoreAPI>("ArmConfirmationFn"), "ArmConfirmationFn offset");
        Eq((int)Marshal.OffsetOf<RynthCore.Engine.Plugins.RynthCoreAPI>("ArmConfirmationFn"),
           (int)Marshal.OffsetOf<RynthCore.Engine.Plugins.RynthCoreAPI>("GetObjectPropertiesFn") + IntPtr.Size, "ArmConfirmationFn right after GetObjectPropertiesFn");
        Eq((int)Marshal.OffsetOf<RynthCore.PluginSdk.RynthCoreApiNative>("GetLastConfirmationFn"),
           (int)Marshal.OffsetOf<RynthCore.Engine.Plugins.RynthCoreAPI>("GetLastConfirmationFn"), "GetLastConfirmationFn offset");
        Eq((int)Marshal.OffsetOf<RynthCore.PluginSdk.RynthCoreApiNative>("GetServerStateJsonFn"),
           (int)Marshal.OffsetOf<RynthCore.Engine.Plugins.RynthCoreAPI>("GetServerStateJsonFn"), "GetServerStateJsonFn offset");
        Eq((int)Marshal.OffsetOf<RynthCore.PluginSdk.RynthCoreApiNative>("GetWieldedItemsFn"),
           (int)Marshal.OffsetOf<RynthCore.Engine.Plugins.RynthCoreAPI>("GetWieldedItemsFn"), "GetWieldedItemsFn offset");
        foreach (string slot in new[] { "GetUiColorFn", "GetUiFontFn", "SetPanelOpenFn" })
            Eq((int)Marshal.OffsetOf<RynthCore.PluginSdk.RynthCoreApiNative>(slot),
               (int)Marshal.OffsetOf<RynthCore.Engine.Plugins.RynthCoreAPI>(slot), slot + " offset");
        Eq((int)Marshal.OffsetOf<RynthCore.Engine.Plugins.RynthCoreAPI>("GetUiColorFn"),
           (int)Marshal.OffsetOf<RynthCore.Engine.Plugins.RynthCoreAPI>("GetWieldedItemsFn") + IntPtr.Size, "GetUiColorFn right after GetWieldedItemsFn");
    }

    private sealed class ProbePlugin : RynthCore.PluginCore.RynthPluginBase { }
    private sealed class OverridingPlugin : RynthCore.PluginCore.RynthPluginBase { public override uint MinimumApiVersion => 70; }

    private static void ManifestAttributeDrivesRuntimeMinimum()
    {
        // RynthPluginBase.MinimumApiVersion defaults to the assembly's [RynthPluginManifest(min)] ...
        Eq(new ProbePlugin().MinimumApiVersion, 71u, "default follows the manifest attribute");
        // ... an explicit override still wins ...
        Eq(new OverridingPlugin().MinimumApiVersion, 70u, "an override wins");
        // ... and an assembly without one (RynthCore.PluginSdk itself has none) gets the baseline.
        Check(typeof(RynthCoreHost).Assembly.GetCustomAttributes(typeof(RynthPluginManifestAttribute), false).Length == 0,
              "the SDK assembly carries no manifest attribute");
    }

    // ── PluginPaths de-duplication (PluginPathDedupe; Silentkelpie, rynth PR #1) ──

    private const string SuiteDir = @"C:\Games\RynthSuite\Plugins";
    private const string OtherDir = @"D:\Old\RynthSuite";

    private static void DedupeSamePathTwice()
    {
        string ai = Path.Combine(SuiteDir, "RynthCore.Plugin.RynthAi.dll");
        string chat = Path.Combine(SuiteDir, "RynthCore.Plugin.RynthChat.dll");
        var r = PluginPathDedupe.Dedupe(new[] { ai, chat, ai });
        Eq(string.Join("|", r.Load), ai + "|" + chat, "loads each once, in order");
        Eq(r.Skipped.Count, 1, "one skipped");
        Check(r.Skipped[0].SameFile && r.Skipped[0].Path == ai && r.Skipped[0].KeptPath == ai, "repeat flagged as the same file");
        Check(r.Skipped[0].Message.Contains("listed twice") && !r.Skipped[0].Message.Contains("conflict"), "repeat is not called a conflict: " + r.Skipped[0].Message);
    }

    private static void DedupeCaseAndSlashes()
    {
        string ai = SuiteDir + @"\RynthCore.Plugin.RynthAi.dll";
        string[] spellings =
        {
            ai.ToUpperInvariant(),
            ai.Replace('\\', '/'),
            @"C:\Games\RynthSuite\Plugins\..\Plugins\.\rynthcore.plugin.rynthai.DLL",
            "  " + ai + "  ",
        };
        foreach (string other in spellings)
        {
            var r = PluginPathDedupe.Dedupe(new[] { ai, other });
            Eq(r.Load.Count, 1, $"'{other}' loads once");
            Check(r.Skipped.Count == 1 && r.Skipped[0].SameFile, $"'{other}' is the same file, not a conflict");
        }
        Eq(PluginPathDedupe.Normalize("C:/Games/RynthSuite/Plugins/"), @"C:\Games\RynthSuite\Plugins", "normalize: slashes and trailing separator");
    }

    private static void DedupeSameNameTwoFolders()
    {
        string first = Path.Combine(SuiteDir, "RynthCore.Plugin.RynthAi.dll");
        string second = Path.Combine(OtherDir, "rynthcore.plugin.rynthai.dll");
        string nav = Path.Combine(OtherDir, "RynthCore.Plugin.RynthNav.dll");
        var r = PluginPathDedupe.Dedupe(new[] { first, nav, second });
        Eq(string.Join("|", r.Load), first + "|" + nav, "the first listed wins; other plugins in that folder still load");
        Eq(r.Skipped.Count, 1, "one skipped");
        PluginPathDedupe.Skipped s = r.Skipped[0];
        Check(!s.SameFile && !s.KeptAlreadyLoaded, "flagged as a conflict between two copies");
        Eq(s.KeptPath, first, "kept path");
        Check(s.Message.StartsWith("plugin conflict") && s.Message.Contains(first) && s.Message.Contains(second) && s.Message.Contains("listed first"),
              "warning names both copies and which won: " + s.Message);

        // Reversed order: the other one wins. First listed is the rule, not a folder preference.
        var rev = PluginPathDedupe.Dedupe(new[] { second, first });
        Eq(rev.Load.Single(), second, "reversed: the first listed wins");
    }

    private static void DedupeAgainstAlreadyLoaded()
    {
        string loaded = Path.Combine(SuiteDir, "RynthCore.Plugin.RynthAi.dll");
        string copy = Path.Combine(OtherDir, "RynthCore.Plugin.RynthAi.dll");
        var r = PluginPathDedupe.Dedupe(new[] { loaded.ToLowerInvariant(), copy }, new[] { loaded });
        Eq(r.Load.Count, 0, "nothing new to load");
        Eq(r.Skipped.Count, 2, "both skipped");
        Check(r.Skipped[0].SameFile && r.Skipped[0].KeptAlreadyLoaded && r.Skipped[0].Message.Contains("already loaded"), "same file as a loaded plugin");
        Check(!r.Skipped[1].SameFile && r.Skipped[1].KeptAlreadyLoaded && r.Skipped[1].Message.Contains("already loaded"), "a second copy of a loaded plugin is a conflict");
    }

    private static void DedupeKeepsOrderAndText()
    {
        string[] paths =
        {
            Path.Combine(SuiteDir, "RynthCore.Plugin.RynthChat.dll"),
            "",
            Path.Combine(SuiteDir, "RynthCore.Plugin.RynthAi.dll"),
            Path.Combine(SuiteDir, "RynthCore.Plugin.RynthNav.dll"),
        };
        var r = PluginPathDedupe.Dedupe(paths);
        Eq(string.Join("|", r.Load), paths[0] + "|" + paths[2] + "|" + paths[3], "no duplicates: everything loads, as listed, text unchanged");
        Eq(r.Skipped.Count, 0, "nothing skipped");
        Eq(PluginPathDedupe.Dedupe(Array.Empty<string>()).Load.Count, 0, "empty list");
    }

    private static string FindUp(string relative)
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            string p = Path.Combine(d.FullName, relative);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException(relative);
    }

    // ── Real plugin DLLs (arguments) ─────────────────────────────────────────

    private static void CheckBuiltPlugin(string dll)
    {
        RynthPluginManifest? m = PluginManifestReader.Read(dll);
        Check(m != null, $"{dll}: has a manifest");
        if (m == null) return;
        Console.WriteLine($"     {PluginManifestGate.Describe(m)}");
        Eq(m.Name, RynthPluginManifest.NameFromFile(dll), $"{Path.GetFileName(dll)}: manifest name matches the file");
        Check(m.MinEngineApi >= RynthCoreHost.BaselineApiVersion, $"{m.Name}: minEngineApi {m.MinEngineApi} at or above the SDK baseline");
        Check(m.CheckEngine(PluginContractVersion.Current) == null, $"{m.Name}: runs on this engine (API {PluginContractVersion.Current})");
        Check(m.Description.Length > 0, $"{m.Name}: has a description");
        Check(!string.IsNullOrEmpty(System.Diagnostics.FileVersionInfo.GetVersionInfo(dll).ProductVersion), $"{m.Name}: version resource intact");
    }
}
