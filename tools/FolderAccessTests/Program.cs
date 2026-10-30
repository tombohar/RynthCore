using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using RynthCore.App;

namespace FolderAccessTests;

/// <summary>
/// FolderAccess (launcher: start-up folder check, Recovery Actions > Fix folder permissions).
/// Players reported RynthAi metas and navs not saving and "read only" plugin folders (2026-10-13).
/// Writes only under %TEMP%\FolderAccessTests-*; never changes permissions.
/// </summary>
internal static class Program
{
    private static int _checks, _failed;
    private static string _temp = "";

    private static int Main()
    {
        _temp = Path.Combine(Path.GetTempPath(), $"FolderAccessTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temp);
        try
        {
            Scenario("folders checked: RynthSuite, RynthAi and its data folders, RynthCore and Logs", FoldersToCheck);
            Scenario("check: a writable folder passes and leaves no probe file", WritableFolder);
            Scenario("check: read-only files are counted and reported", ReadOnlyFilesCounted);
            Scenario("check: a folder that doesn't exist (or a file in its place) has nothing to save, so no problem", MissingFolder);
            Scenario("elevated step: command line round-trips (spaces, report file)", ArgumentsRoundTrip);
            Scenario("elevated step: malformed command lines are refused", ArgumentsMalformed);
            Scenario("elevated step: refuses drive roots, Windows, Program Files, user folders, strangers", SafeRoots);
            Scenario("elevated step: accepts a RynthCore or RynthSuite folder", OurRoots);
            Scenario("repair (read-only part): clears read-only everywhere, never through a junction", RepairClearsReadOnly);
            Scenario("repair: refused roots are reported, not touched", RepairRefusesStrangers);
            Scenario("repair: the rule is Modify for the account, inherited by folders and files", RuleShape);
            Scenario("controlled folder access: 1123/1124 events parsed (process, path, blocked)", CfaEvents);
            Scenario("controlled folder access: the live query never throws", CfaLive);
            Scenario("virtualized location: Program Files yes, C:\\Games no", Virtualized);
        }
        finally
        {
            try
            {
                foreach (string f in Directory.GetFiles(_temp, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(_temp, recursive: true);
            }
            catch { }
        }
        Console.WriteLine();
        Console.WriteLine($"{_checks} check(s), {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }

    private static void Scenario(string name, Action body)
    {
        int before = _failed;
        try { body(); }
        catch (Exception ex) { Check(false, $"threw {ex.GetType().Name}: {ex.Message}"); }
        Console.WriteLine($"[{(_failed == before ? "PASS" : "FAIL")}]  {name}");
    }

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (ok) return;
        _failed++;
        Console.WriteLine("          - " + what);
    }

    private static string Dir(string name)
    {
        string d = Path.Combine(_temp, name);
        Directory.CreateDirectory(d);
        return d;
    }

    private static void FoldersToCheck()
    {
        var f = FolderAccess.FoldersToCheck(@"D:\X\RynthSuite", @"D:\X\RynthCore");
        Check(f.Contains(@"D:\X\RynthSuite\RynthAi\MetaFiles"), "MetaFiles");
        Check(f.Contains(@"D:\X\RynthSuite\RynthAi\NavProfiles"), "NavProfiles");
        Check(f.Contains(@"D:\X\RynthSuite\RynthAi\LootProfiles"), "LootProfiles");
        Check(f.Contains(@"D:\X\RynthSuite\RynthAi\SettingsProfiles"), "SettingsProfiles");
        Check(f.Contains(@"D:\X\RynthCore\Logs"), "RynthCore Logs");
        Check(f[0] == @"D:\X\RynthSuite", "the suite root first");
    }

    private static void WritableFolder()
    {
        string d = Dir("writable");
        var s = FolderAccess.Check(d);
        Check(s.Exists && s.Writable && s.Ok, "writable and ok");
        Check(Directory.GetFiles(d).Length == 0, "probe removed");
        Check(FolderAccess.DescribeProblems(new[] { s }).Count == 0, "no problem lines");
    }

    private static void ReadOnlyFilesCounted()
    {
        string d = Dir("ro");
        File.WriteAllText(Path.Combine(d, "a.af"), "x");
        File.WriteAllText(Path.Combine(d, "b.nav"), "x");
        File.SetAttributes(Path.Combine(d, "a.af"), FileAttributes.ReadOnly);
        var s = FolderAccess.Check(d);
        Check(s.Writable, "folder still writable");
        Check(s.ReadOnlyFiles == 1, $"one read-only file (got {s.ReadOnlyFiles})");
        Check(!s.Ok, "not ok");
        var lines = FolderAccess.DescribeProblems(new[] { s });
        Check(lines.Count == 1 && lines[0].Contains("1 file(s) marked read-only"), "described: " + string.Join(" | ", lines));
    }

    private static void MissingFolder()
    {
        var s = FolderAccess.Check(Path.Combine(_temp, "nope"));
        Check(!s.Exists && s.Ok, "missing is fine");
        string blocker = Path.Combine(_temp, "blocker");
        File.WriteAllText(blocker, "x");
        var under = FolderAccess.Check(Path.Combine(blocker, "sub"));
        Check(under.Ok, "a path under a file doesn't exist either (nothing to save there)");
    }

    private static void ArgumentsRoundTrip()
    {
        string sid = WindowsIdentity.GetCurrent().User!.Value;
        string args = FolderAccess.BuildRepairArguments(sid, new[] { @"C:\Games\Rynth Suite\", @"D:\RynthCore" }, @"C:\Temp\rynthcore-folderfix-1.txt");
        string[] argv = SplitLikeWindows(args);
        Check(FolderAccess.TryParseRepairArguments(argv, out string s2, out var roots, out string? report), "parses: " + args);
        Check(s2 == sid, "sid");
        Check(roots.Count == 2 && roots[0] == @"C:\Games\Rynth Suite" && roots[1] == @"D:\RynthCore", "roots: " + string.Join(" | ", roots));
        Check(report == @"C:\Temp\rynthcore-folderfix-1.txt", "report path");
    }

    /// <summary>The command line as the elevated launcher's Main gets it (Windows' quoting; our builder never embeds quotes).</summary>
    private static string[] SplitLikeWindows(string commandLine)
    {
        var list = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool quoted = false, any = false;
        foreach (char c in commandLine)
        {
            if (c == '"') { quoted = !quoted; any = true; continue; }
            if (c == ' ' && !quoted) { if (any || cur.Length > 0) list.Add(cur.ToString()); cur.Clear(); any = false; continue; }
            cur.Append(c);
        }
        if (any || cur.Length > 0) list.Add(cur.ToString());
        return list.ToArray();
    }

    private static void ArgumentsMalformed()
    {
        Check(!FolderAccess.TryParseRepairArguments(new[] { "--other" }, out _, out _, out _), "not the command");
        Check(!FolderAccess.TryParseRepairArguments(new[] { FolderAccess.RepairArgument }, out _, out _, out _), "no SID");
        Check(!FolderAccess.TryParseRepairArguments(new[] { FolderAccess.RepairArgument, "not-a-sid", @"C:\Games\RynthSuite" }, out _, out _, out _), "bad SID");
        Check(!FolderAccess.TryParseRepairArguments(new[] { FolderAccess.RepairArgument, "S-1-5-32-545" }, out _, out _, out _), "no folders");
        Check(!FolderAccess.TryParseRepairArguments(new[] { FolderAccess.RepairArgument, "S-1-5-32-545", "--report" }, out _, out _, out _), "--report without a file");
    }

    private static void SafeRoots()
    {
        string[] refused =
        {
            @"C:\", "C:", @"relative\path", "",
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))!,
            Path.Combine(_temp, "does-not-exist"),
            Dir("stranger"),   // exists, but nothing of ours in it
        };
        foreach (string r in refused)
            Check(!FolderAccess.IsSafeRepairRoot(r, out string why) && why.Length > 0, $"refused: '{r}'");
    }

    private static void OurRoots()
    {
        string suite = Dir("suite");
        Directory.CreateDirectory(Path.Combine(suite, "RynthAi"));
        Check(FolderAccess.IsSafeRepairRoot(suite, out string w1), "suite folder (has RynthAi): " + w1);
        string core = Dir("core");
        File.WriteAllText(Path.Combine(core, "RynthCore.exe"), "x");
        Check(FolderAccess.IsSafeRepairRoot(core + "\\", out string w2), "core folder (has RynthCore.exe), trailing slash: " + w2);
        string plug = Dir("plugonly");
        Directory.CreateDirectory(Path.Combine(plug, "RynthNav"));
        File.WriteAllText(Path.Combine(plug, "RynthNav", "RynthCore.Plugin.RynthNav.dll"), "x");
        Check(FolderAccess.IsSafeRepairRoot(plug, out string w3), "suite without RynthAi but with a plugin: " + w3);
    }

    private static bool MakeJunction(string link, string target)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(link);
        psi.ArgumentList.Add(target);
        using var p = Process.Start(psi)!;
        p.WaitForExit(10_000);
        return p.ExitCode == 0 && Directory.Exists(link);
    }

    private static void RepairClearsReadOnly()
    {
        string suite = Dir("repair-suite");
        string meta = Path.Combine(suite, "RynthAi", "MetaFiles");
        Directory.CreateDirectory(meta);
        string a = Path.Combine(meta, "farm.af");
        string b = Path.Combine(suite, "RynthAi", "settings.json");
        File.WriteAllText(a, "x");
        File.WriteAllText(b, "x");
        File.SetAttributes(a, FileAttributes.ReadOnly);
        File.SetAttributes(b, FileAttributes.ReadOnly | FileAttributes.Archive);

        string outside = Dir("outside");
        string o = Path.Combine(outside, "not-ours.txt");
        File.WriteAllText(o, "x");
        File.SetAttributes(o, FileAttributes.ReadOnly);
        bool junction = MakeJunction(Path.Combine(suite, "link"), outside);

        var report = FolderAccess.Repair(new[] { suite }, WindowsIdentity.GetCurrent().User!, changePermissions: false);
        Check(report.Roots == 1 && report.Errors == 0, "one root, no errors: " + report);
        Check(report.ReadOnlyCleared == 2, $"two files cleared (got {report.ReadOnlyCleared})");
        Check((File.GetAttributes(a) & FileAttributes.ReadOnly) == 0, "meta writable");
        Check((File.GetAttributes(b) & FileAttributes.ReadOnly) == 0 && (File.GetAttributes(b) & FileAttributes.Archive) != 0, "other attributes kept");
        if (junction)
            Check((File.GetAttributes(o) & FileAttributes.ReadOnly) != 0, "the file behind the junction is untouched");
        else
            Console.WriteLine("          (no junction could be made here; that part skipped)");
        Check(report.ExplicitGrants == 0, "no permission changes in this mode");
        try { Directory.Delete(Path.Combine(suite, "link")); } catch { }
    }

    private static void RepairRefusesStrangers()
    {
        string stranger = Dir("repair-stranger");
        string f = Path.Combine(stranger, "x.txt");
        File.WriteAllText(f, "x");
        File.SetAttributes(f, FileAttributes.ReadOnly);
        var report = FolderAccess.Repair(new[] { stranger, @"C:\" }, WindowsIdentity.GetCurrent().User!, changePermissions: false);
        Check(report.Roots == 0 && report.Errors == 2 && !report.Ok, "both refused: " + report);
        Check((File.GetAttributes(f) & FileAttributes.ReadOnly) != 0, "nothing touched");
    }

    private static void RuleShape()
    {
        var user = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var dir = FolderAccess.ModifyRule(user, container: true);
        Check((dir.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify, "Modify");
        Check((dir.FileSystemRights & (FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)) == 0, "not Full Control");
        Check(dir.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit), "inherited by folders and files");
        Check(dir.PropagationFlags == PropagationFlags.None && dir.AccessControlType == AccessControlType.Allow, "applies here too, allow");
        Check(dir.IdentityReference.Value == user.Value, "for that account");
        Check(FolderAccess.ModifyRule(user, container: false).InheritanceFlags == InheritanceFlags.None, "a file's rule doesn't inherit");
    }

    private static void CfaEvents()
    {
        const string xml =
            "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><EventID>1123</EventID>" +
            "<TimeCreated SystemTime='2026-10-12T20:01:02.000Z'/></System><EventData>" +
            "<Data Name='Product Name'>Microsoft Defender Antivirus</Data><Data Name='Path'>C:\\Users\\p\\Documents\\RynthSuite\\RynthAi\\MetaFiles\\farm.af</Data>" +
            "<Data Name='Process Name'>C:\\Turbine\\Asheron's Call\\acclient.exe</Data></EventData></Event>" +
            "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><EventID>1124</EventID>" +
            "<TimeCreated SystemTime='2026-10-11T08:00:00.000Z'/></System><EventData>" +
            "<Data Name='Path'>C:\\Users\\p\\Desktop\\x.txt</Data><Data Name='Process Name'>C:\\Tools\\other.exe</Data></EventData></Event>";
        var list = FolderAccess.ParseCfaEvents(xml);
        Check(list.Count == 2, $"two events (got {list.Count})");
        if (list.Count == 2)
        {
            Check(list[0].Blocked && FolderAccess.IsGameClient(list[0].Process), "1123 = blocked, acclient");
            Check(list[0].Path.EndsWith("farm.af"), "path");
            Check(list[0].Time == new DateTime(2026, 10, 12, 20, 1, 2, DateTimeKind.Utc).ToLocalTime() || list[0].Time.ToUniversalTime().Hour == 20, "time");
            Check(!list[1].Blocked && !FolderAccess.IsGameClient(list[1].Process), "1124 = audit only, another app");
        }
        var day = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        var info = new FolderAccess.CfaInfo(1, true, list);
        Check(info.On && info.GameClientBlocks(day).Count == 1, "on and blocking the client");
        Check(info.GameClientBlocks(day.AddDays(10)).Count == 0, "a block from 10 days ago is history");
        Check(new FolderAccess.CfaInfo(1, true, list, new() { list[0].Process.ToUpperInvariant() }).GameClientBlocks(day).Count == 0,
            "the client was allowed through since");
        Check(new FolderAccess.CfaInfo(0, true, new() { list[1] }).GameClientBlocks(day).Count == 0, "an audit of another app isn't a block of the client");
        Check(FolderAccess.ParseCfaEvents("").Count == 0 && FolderAccess.ParseCfaEvents("not xml <").Count == 0, "empty / garbage");
    }

    private static void CfaLive()
    {
        var info = FolderAccess.QueryControlledFolderAccess(5);
        Console.WriteLine($"          (this PC: Controlled folder access mode {info.Mode}{(info.ModeKnown ? "" : " (unknown)")}, {info.RecentBlocks.Count} recent event(s))");
        Check(info.RecentBlocks.Count <= 5, "at most the events asked for");
    }

    private static void Virtualized()
    {
        Check(FolderAccess.IsVirtualizedLocation(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "RynthSuite")), "Program Files (x86)");
        Check(FolderAccess.IsVirtualizedLocation(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RynthSuite")), "Program Files");
        Check(!FolderAccess.IsVirtualizedLocation(@"C:\Games\RynthSuite"), "C:\\Games");
        Check(!FolderAccess.IsVirtualizedLocation(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + " Extra\\X"), "a look-alike name");
        Check(FolderAccess.IsSameOrInside(@"C:\A\B", @"C:\A\") && !FolderAccess.IsSameOrInside(@"C:\AB", @"C:\A"), "inside vs prefix");
    }
}
