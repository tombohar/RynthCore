using RynthCore.Install;

namespace InstallPathsTests;

/// <summary>
/// RynthInstallPaths: where RynthCore and RynthSuite live. Order is environment, HKCU, HKLM,
/// then today's C:\Games folders. Idea from Silentkelpie (rynth PR #1); re-implemented here.
/// </summary>
internal static class Program
{
    private static int _checks, _failed;
    private static string _case = "";

    private static int Main()
    {
        NothingSetGivesTodaysFolders();
        Order();
        UnusableValuesAreSkipped();
        Tidying();
        SpacesInPaths();
        ThrowingSourceFallsThrough();
        RealRegistryMissingValueIsNull();

        Console.WriteLine($"{_checks} check(s), {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (ok) return;
        _failed++;
        Console.WriteLine($"[FAIL] {_case}: {what}");
    }

    private static void Eq(string? actual, string? expected, string what)
        => Check(string.Equals(actual, expected, StringComparison.Ordinal), $"{what}: expected '{expected}', got '{actual}'");

    /// <summary>An in-memory environment and registry; records what was asked.</summary>
    private sealed class Fake : IInstallSettingSource
    {
        public readonly Dictionary<string, string?> Env = new();
        public readonly Dictionary<string, string?> User = new();
        public readonly Dictionary<string, string?> Machine = new();
        public readonly List<string> Asked = new();
        public bool Throw;

        public string? EnvironmentVariable(string name) { Asked.Add("env:" + name); if (Throw) throw new InvalidOperationException(); return Env.GetValueOrDefault(name); }
        public string? CurrentUser(string valueName) { Asked.Add("hkcu:" + valueName); if (Throw) throw new UnauthorizedAccessException(); return User.GetValueOrDefault(valueName); }
        public string? LocalMachine(string valueName) { Asked.Add("hklm:" + valueName); if (Throw) throw new IOException(); return Machine.GetValueOrDefault(valueName); }
    }

    private static string Core(Fake f) => RynthInstallPaths.Resolve(RynthInstallPaths.CoreEnvironmentVariable,
        RynthInstallPaths.CoreValueName, RynthInstallPaths.DefaultCoreDir, f);

    private static string Suite(Fake f) => RynthInstallPaths.Resolve(RynthInstallPaths.SuiteEnvironmentVariable,
        RynthInstallPaths.SuiteValueName, RynthInstallPaths.DefaultSuiteDir, f);

    private static void NothingSetGivesTodaysFolders()
    {
        _case = "nothing set";
        var f = new Fake();
        Eq(Core(f), @"C:\Games\RynthCore", "core default is today's folder");
        Eq(Suite(f), @"C:\Games\RynthSuite", "suite default is today's folder");
        Eq(Path.Combine(Core(f), "Logs"), @"C:\Games\RynthCore\Logs", "logs under the default");
        Eq(Path.Combine(Suite(f), "RynthAi"), @"C:\Games\RynthSuite\RynthAi", "RynthAi under the default");
        Check(f.Asked.Contains("env:RYNTHCORE_DIR") && f.Asked.Contains("hkcu:CoreDir") && f.Asked.Contains("hklm:CoreDir"),
            "all three sources asked for the core folder");
        Check(f.Asked.Contains("env:RYNTHSUITE_DIR") && f.Asked.Contains("hkcu:SuiteDir") && f.Asked.Contains("hklm:SuiteDir"),
            "all three sources asked for the suite folder");
        Eq(RynthInstallPaths.RegistryKey, @"Software\Rynth", "registry key");
    }

    private static void Order()
    {
        _case = "order";
        var f = new Fake();
        f.Machine["CoreDir"] = @"D:\Machine\RynthCore";
        Eq(Core(f), @"D:\Machine\RynthCore", "HKLM beats the default");

        f.User["CoreDir"] = @"E:\User\RynthCore";
        Eq(Core(f), @"E:\User\RynthCore", "HKCU beats HKLM");

        f.Env["RYNTHCORE_DIR"] = @"F:\Dev\RynthCore";
        Eq(Core(f), @"F:\Dev\RynthCore", "environment beats HKCU");

        f.Asked.Clear();
        Core(f);
        Check(f.Asked.Count == 1 && f.Asked[0] == "env:RYNTHCORE_DIR", "a usable environment value stops the search");

        // The two folders are independent.
        var g = new Fake();
        g.User["SuiteDir"] = @"E:\Suite";
        Eq(Core(g), @"C:\Games\RynthCore", "a suite setting leaves the core folder alone");
        Eq(Suite(g), @"E:\Suite", "suite from HKCU");
        g.Env["RYNTHCORE_DIR"] = @"F:\Core";
        Eq(Suite(g), @"E:\Suite", "a core variable leaves the suite folder alone");
    }

    private static void UnusableValuesAreSkipped()
    {
        _case = "unusable values";
        foreach (string? bad in new[] { null, "", "   ", "\"\"", @"Games\RynthCore", @"..\RynthCore", "RynthCore", "C:", @"C:Games", "D:\\bad|name", "\0" })
        {
            var f = new Fake();
            f.Env["RYNTHCORE_DIR"] = bad;
            f.User["CoreDir"] = bad;
            f.Machine["CoreDir"] = @"D:\Machine";
            Eq(Core(f), @"D:\Machine", $"'{bad}' in env and HKCU falls through to HKLM");

            var g = new Fake();
            g.Machine["CoreDir"] = bad;
            Eq(Core(g), @"C:\Games\RynthCore", $"'{bad}' in HKLM falls back to the default");
        }
    }

    private static void Tidying()
    {
        _case = "tidying";
        Eq(RynthInstallPaths.Normalize(@"D:\Rynth\"), @"D:\Rynth", "trailing separator dropped");
        Eq(RynthInstallPaths.Normalize(@"D:\Rynth\\"), @"D:\Rynth", "doubled trailing separator dropped");
        Eq(RynthInstallPaths.Normalize("D:/Rynth/Core/"), @"D:\Rynth\Core", "forward slashes");
        Eq(RynthInstallPaths.Normalize("  \"D:\\Rynth\"  "), @"D:\Rynth", "whitespace and quotes trimmed");
        Eq(RynthInstallPaths.Normalize(@"D:\Rynth\.\Core\..\Suite"), @"D:\Rynth\Suite", "dot segments resolved");
        Eq(RynthInstallPaths.Normalize(@"D:\"), @"D:\", "a drive root stays a root");
        Eq(RynthInstallPaths.Normalize(@"\\nas\games\RynthCore\"), @"\\nas\games\RynthCore", "UNC path");
    }

    private static void SpacesInPaths()
    {
        _case = "spaces";
        var f = new Fake();
        f.User["SuiteDir"] = @"C:\Program Files (x86)\Rynth Suite\";
        Eq(Suite(f), @"C:\Program Files (x86)\Rynth Suite", "HKCU value with spaces");
        Eq(Path.Combine(Suite(f), "RynthAi", "MetaFiles"), @"C:\Program Files (x86)\Rynth Suite\RynthAi\MetaFiles", "joined path keeps the spaces");

        var g = new Fake();
        g.Env["RYNTHCORE_DIR"] = "\"D:\\My Games\\Rynth Core\"";
        Eq(Core(g), @"D:\My Games\Rynth Core", "quoted environment value with spaces");

        var h = new Fake();
        h.Machine["CoreDir"] = @"  E:\Tom's Games\RynthCore  ";
        Eq(Core(h), @"E:\Tom's Games\RynthCore", "HKLM value with spaces and an apostrophe, padded");
    }

    private static void ThrowingSourceFallsThrough()
    {
        _case = "throwing source";
        var f = new Fake { Throw = true };
        Eq(Core(f), @"C:\Games\RynthCore", "every source throws -> default, no exception");
        Check(f.Asked.Count == 3, "all three sources were still tried");
    }

    private static void RealRegistryMissingValueIsNull()
    {
        _case = "real registry (read only)";
        string missing = "NoSuchValue-" + Guid.NewGuid().ToString("N");
        Check(SystemInstallSettings.Instance.CurrentUser(missing) == null, "HKCU read of a missing value is null");
        Check(SystemInstallSettings.Instance.LocalMachine(missing) == null, "HKLM read of a missing value is null");
        Check(SystemInstallSettings.Instance.EnvironmentVariable(missing) == null, "missing environment variable is null");
        // Whatever this machine has set, the live answers are absolute folders.
        Check(Path.IsPathFullyQualified(RynthInstallPaths.CoreDir), "CoreDir is absolute: " + RynthInstallPaths.CoreDir);
        Check(Path.IsPathFullyQualified(RynthInstallPaths.SuiteDir), "SuiteDir is absolute: " + RynthInstallPaths.SuiteDir);
        Eq(RynthInstallPaths.CoreLogsDir, Path.Combine(RynthInstallPaths.CoreDir, "Logs"), "CoreLogsDir");
        Eq(RynthInstallPaths.RynthAiDir, Path.Combine(RynthInstallPaths.SuiteDir, "RynthAi"), "RynthAiDir");
    }
}
