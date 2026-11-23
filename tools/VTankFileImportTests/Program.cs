using System.Text;
using RynthCore.App;
using static RynthCore.App.VTankFileImport;

namespace VTankFileImportTests;

/// <summary>
/// VTankFileImport: the installer's "copy your VirindiTank files to RynthAi" offer and the launcher's
/// Import VirindiTank files button (the plan: what goes where, identical files, conflicts, keep both).
/// Writes only under %TEMP%\VTankFileImportTests-*; never reads the PC's real VTank folders.
/// </summary>
internal static class Program
{
    private static int _checks, _failed;
    private static string _temp = "";

    private static int Main()
    {
        _temp = Path.Combine(Path.GetTempPath(), $"VTankFileImportTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temp);
        try
        {
            Scenario("Decal entries: VTank's GUID or object name gives its folder; other plugins and bad paths don't", PluginEntries);
            Scenario("folders: Decal's first, then the usual ones; duplicates (case, trailing slash) once", Candidates);
            Scenario("scan: top level only, exact extensions (.met .af .nav .utl); .usd, others and VTank's \"--\" state files left out", TopLevelOnly);
            Scenario("scan: counts by kind; files RynthAi already has are not new", CountsAndExisting);
            Scenario("scan: the same name in two VTank folders is offered once", DuplicateNames);
            Scenario("scan: missing folders skipped; a folder inside RynthAi is never read", MissingAndInside);
            Scenario("scan: changes nothing anywhere", ScanChangesNothing);
            Scenario("copy: new files copied byte for byte into MetaFiles, NavProfiles, LootProfiles", CopiesIntoFolders);
            Scenario("copy: never overwrites a file RynthAi has", NeverOverwrites);
            Scenario("copy: the VTank folder is left exactly as it was (copy, not move)", SourceUntouched);
            Scenario("copy: a read-only source gives a writable copy; the source stays read-only", ReadOnlySource);
            Scenario("copy: a second run copies nothing (everything already there)", SecondRun);
            Scenario("copy: every copied and skipped file is logged", Logged);
            Scenario("kinds: .met .af -> MetaFiles, .nav -> NavProfiles, .utl -> LootProfiles, flat; others none", Kinds);
            Scenario("scan: a file identical to RynthAi's is 'same', a different one is a conflict", SameVersusConflict);
            Scenario("copy, skip: conflicts and identical files are left alone, RynthAi's file kept", SkipMode);
            Scenario("copy, keep both: a conflict is copied as '<name> (VTank)', RynthAi's file kept", KeepBothMode);
            Scenario("copy, keep both: a second run copies nothing; a taken '(VTank)' name gives '(VTank 2)'", KeepBothAgain);
            Scenario("keep-both names: '(VTank)', '(VTank 2)' ...; first free one", KeepBothNames);
            Scenario("picked folder: itself, or its VirindiTank / VirindiPlugins\\VirindiTank subfolder", PickedFolder);
            Scenario("plain words: the plan and the result say counts, folders and what was kept", PlainWords);
            Scenario("command line: round-trips (spaces); malformed ones refused", Arguments);
            Scenario("command: scan writes counts and copies nothing", RunScan);
            Scenario("command: copy writes counts and the log lines", RunCopy);
            Scenario("command: an existing report, a missing RynthAi folder or a bad line does nothing (exit 2)", RunRefuses);
            Scenario("command: no VTank folder at all gives zero counts", RunNothingFound);
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

    private static string NewDir(string name)
    {
        string d = Path.Combine(_temp, Guid.NewGuid().ToString("N")[..8], name);
        Directory.CreateDirectory(d);
        return d;
    }

    private static string Put(string dir, string name, string text = "")
    {
        Directory.CreateDirectory(dir);
        string p = Path.Combine(dir, name);
        File.WriteAllText(p, text.Length > 0 ? text : "content of " + name);
        return p;
    }

    /// <summary>A RynthAi folder like the installer's (three subfolders made).</summary>
    private static string RynthAi()
    {
        string d = NewDir("Rynth Suite");
        string ai = Path.Combine(d, "RynthAi");
        foreach (string s in new[] { "MetaFiles", "NavProfiles", "LootProfiles" }) Directory.CreateDirectory(Path.Combine(ai, s));
        return ai;
    }

    /// <summary>A VTank folder as a real install has it: everything at the top level.</summary>
    private static string VTank(params string[] names)
    {
        string d = NewDir("Virindi Tank");
        foreach (string n in names) Put(d, n);
        return d;
    }

    private static Dictionary<string, (long, DateTime, FileAttributes, string)> Snapshot(string dir)
    {
        var map = new Dictionary<string, (long, DateTime, FileAttributes, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
        {
            var fi = new FileInfo(f);
            map[f] = (fi.Length, fi.LastWriteTimeUtc, fi.Attributes, File.ReadAllText(f));
        }
        foreach (string d in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories)) map[d + "\\"] = (0, default, default, "");
        return map;
    }

    private static bool SameSnapshot(Dictionary<string, (long, DateTime, FileAttributes, string)> a,
                                     Dictionary<string, (long, DateTime, FileAttributes, string)> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v.Equals(kv.Value));

    // ------------------------------------------------------------------------------------------

    private static void PluginEntries()
    {
        var got = VTankPathsFromPluginEntries(new (string, string?, string?)[]
        {
            ("{642F1F48-16BE-48BF-B1D4-286652C4533E}", "uTank2.PluginCore", @"C:\Games\VirindiPlugins\VirindiTank\"),
            ("{642f1f48-16be-48bf-b1d4-286652c4533e}", null, @"D:\VT by guid"),
            ("{00000000-0000-0000-0000-000000000001}", "uTank2.PluginCore", "\"E:\\VT by object\""),
            ("{0A7B25AC-79A9-4F6A-9751-419B87A7BB05}", "VCS5.PluginCore", @"C:\Games\VirindiPlugins\VirindiChatSystem5\"),
            ("{642F1F48-16BE-48BF-B1D4-286652C4533E}", "uTank2.PluginCore", @"relative\VirindiTank"),
            ("{642F1F48-16BE-48BF-B1D4-286652C4533E}", "uTank2.PluginCore", ""),
            ("{642F1F48-16BE-48BF-B1D4-286652C4533E}", "uTank2.PluginCore", null),
        });
        Check(got.Count == 3, $"3 VTank folders expected, got {got.Count}: {string.Join(" | ", got)}");
        Check(got.Contains(@"C:\Games\VirindiPlugins\VirindiTank\"), "the GUID+object entry");
        Check(got.Contains(@"D:\VT by guid"), "matched by GUID (any case)");
        Check(got.Contains(@"E:\VT by object"), "matched by object name, quotes removed");
        Check(!got.Any(g => g.Contains("ChatSystem")), "another Virindi plugin is not VTank");
    }

    private static void Candidates()
    {
        var c = CandidateRoots(new[] { @"D:\Decal VT\", @"c:\games\virindiplugins\virinditank\" },
                               @"C:\Users\P\Documents", @"C:\Users\P\AppData\Local", @"C:\Program Files (x86)", @"C:\Program Files");
        Check(c.Count > 0 && c[0] == @"D:\Decal VT", $"Decal's folder first, without the slash: {string.Join(" | ", c)}");
        Check(c.Count(x => x.Equals(@"C:\Games\VirindiPlugins\VirindiTank", StringComparison.OrdinalIgnoreCase)) == 1, "C:\\Games one listed once");
        foreach (string want in new[]
                 {
                     @"C:\Users\P\Documents\VirindiPlugins\VirindiTank",
                     @"C:\Users\P\Documents\Decal\VirindiPlugins\VirindiTank",
                     @"C:\Users\P\AppData\Local\VirindiPlugins\VirindiTank",
                     @"C:\Program Files (x86)\VirindiPlugins\VirindiTank",
                     @"C:\Program Files\VirindiPlugins\VirindiTank",
                 })
            Check(c.Contains(want, StringComparer.OrdinalIgnoreCase), "listed: " + want);
        Check(c.Count == 7, $"7 folders, got {c.Count}");
        var none = CandidateRoots(Array.Empty<string>(), null, null, null, null);
        Check(none.Count == 1 && none[0] == @"C:\Games\VirindiPlugins\VirindiTank", "with no known folders only C:\\Games");
    }

    private static void TopLevelOnly()
    {
        string vt = VTank("Hunt.met", "Town.nav", "Loot.utl", "Server_Char.usd", "utank2-i.dll", "Hunt.af",
                          "notes.meta", "route.navx", "loot.utlx", "UPPER.MET", "readme.txt", "x.afx", "Char.cdf", "Char.ast",
                          "--Char_Server.met", "--Char_Server.nav", "--Char_Server.usd", "--Other_Server.met");
        Put(Path.Combine(vt, "old"), "Deep.met");
        Put(Path.Combine(vt, "old", "older"), "Deeper.nav");
        string ai = RynthAi();
        var s = Find(new[] { vt }, ai);
        var names = s.Files.Select(f => Path.GetFileName(f.Source)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        Check(names.SequenceEqual(new[] { "Hunt.af", "Hunt.met", "Loot.utl", "Town.nav", "UPPER.MET" }, StringComparer.OrdinalIgnoreCase),
              "found: " + string.Join(", ", names));
        Check(s.Files.Single(f => f.Source.EndsWith("UPPER.MET")).Kind == Kind.Meta, "extension case ignored");
        Check(s.Files.All(f => Path.GetDirectoryName(f.Source) == vt), "only the top level");
        Check(s.Files.Single(f => f.Source.EndsWith("Hunt.af")).Kind == Kind.Meta
              && s.Files.Single(f => f.Source.EndsWith("Hunt.af")).Destination.EndsWith(@"MetaFiles\Hunt.af"), ".af (metaf text metas) go to MetaFiles");
        Check(s.StateFilesLeftOut == 3, $"3 VTank \"--\" state files (.met/.nav) left out, got {s.StateFilesLeftOut}");
        Check(s.Log.Any(l => l.Contains("state file")), "the left-out state files are logged");
        Check(s.Count(Kind.Meta) == 3, $"3 metas (Hunt.met, Hunt.af, UPPER.MET), got {s.Count(Kind.Meta)}");
    }

    private static void CountsAndExisting()
    {
        string vt = VTank("a.met", "b.met", "c.met", "r1.nav", "r2.nav", "l.utl");
        string ai = RynthAi();
        Put(Path.Combine(ai, "MetaFiles"), "B.MET", "the player's own");
        Put(Path.Combine(ai, "LootProfiles"), "l.utl", "the player's own");
        var s = Find(new[] { vt }, ai);
        Check(s.Count(Kind.Meta) == 3 && s.Count(Kind.Nav) == 2 && s.Count(Kind.Loot) == 1,
              $"counts {s.Count(Kind.Meta)}/{s.Count(Kind.Nav)}/{s.Count(Kind.Loot)}");
        Check(s.NewCount == 4, $"4 new (b.met and l.utl are there), got {s.NewCount}");
        var b = s.Files.Single(f => f.Source.EndsWith("b.met"));
        Check(b.State == State.AlreadyInRynthAi, "b.met already in RynthAi (any case)");
        Check(b.Destination == Path.Combine(ai, "MetaFiles", "b.met"), "destination in MetaFiles");
        Check(s.Files.Single(f => f.Source.EndsWith("r1.nav")).Destination == Path.Combine(ai, "NavProfiles", "r1.nav"), "navs to NavProfiles");
        Check(s.Files.Single(f => f.Source.EndsWith("l.utl")).Destination == Path.Combine(ai, "LootProfiles", "l.utl"), "loot to LootProfiles");
    }

    private static void DuplicateNames()
    {
        string vt1 = VTank("same.met", "only1.nav");
        string vt2 = VTank("SAME.met", "only2.nav");
        File.WriteAllText(Path.Combine(vt2, "SAME.met"), "second folder's");
        string ai = RynthAi();
        var s = Find(new[] { vt1, vt2 }, ai);
        Check(s.Count(Kind.Meta) == 1, $"one meta by name, got {s.Count(Kind.Meta)}");
        Check(s.Count(Kind.Nav) == 2, "two different navs");
        Check(s.Files.Single(f => f.Source.StartsWith(vt1) && f.Kind == Kind.Meta).State == State.New, "the first folder's is offered");
        Check(s.Files.Single(f => f.Source.StartsWith(vt2) && f.Kind == Kind.Meta).State == State.DuplicateName, "the second is a duplicate");
        var c = Copy(s);
        Check(File.ReadAllText(Path.Combine(ai, "MetaFiles", "same.met")) == "content of same.met", "the first folder's file copied");
        Check(c.Copied == 3 && c.Skipped == 1, $"3 copied 1 skipped, got {c.Copied}/{c.Skipped}");
    }

    private static void MissingAndInside()
    {
        string ai = RynthAi();
        Put(Path.Combine(ai, "MetaFiles"), "mine.met");
        string inside = Path.Combine(ai, "MetaFiles");
        string vt = VTank("x.met");
        var s = Find(new[] { Path.Combine(_temp, "no such folder"), ai, inside, vt }, ai);
        Check(s.Roots.Count == 1 && s.Roots[0] == vt, "only the real VTank folder read: " + string.Join(" | ", s.Roots));
        Check(s.Files.Count == 1, $"one file, got {s.Files.Count}");
        Check(s.Log.Count(l => l.Contains("inside RynthAi")) == 2, "the two RynthAi folders are logged as not read");
    }

    private static void ScanChangesNothing()
    {
        string vt = VTank("a.met", "b.nav", "c.utl");
        string ai = RynthAi();
        Directory.Delete(Path.Combine(ai, "LootProfiles"));
        var beforeVt = Snapshot(vt);
        var beforeAi = Snapshot(ai);
        Find(new[] { vt }, ai);
        Check(SameSnapshot(beforeVt, Snapshot(vt)), "VTank folder unchanged");
        Check(SameSnapshot(beforeAi, Snapshot(ai)), "RynthAi folder unchanged (no folder created by a scan)");
    }

    private static void CopiesIntoFolders()
    {
        string vt = VTank();
        Put(vt, "Hunt Meta.met", "meta bytes\u00e9");
        Put(vt, "Route.nav", "uTank2 NAV 1.2\r\n4\r\n");
        Put(vt, "Loot.utl", "UTL\0binary-ish");
        string ai = RynthAi();
        Directory.Delete(Path.Combine(ai, "NavProfiles"));
        var c = Copy(Find(new[] { vt }, ai));
        Check(c.Copied == 3 && c.Skipped == 0 && c.Failed == 0, $"3 copied, got {c.Copied}/{c.Skipped}/{c.Failed}");
        foreach (var (sub, name) in new[] { ("MetaFiles", "Hunt Meta.met"), ("NavProfiles", "Route.nav"), ("LootProfiles", "Loot.utl") })
        {
            string dest = Path.Combine(ai, sub, name);
            Check(File.Exists(dest) && File.ReadAllBytes(dest).SequenceEqual(File.ReadAllBytes(Path.Combine(vt, name))), $"{sub}\\{name} identical");
        }
    }

    private static void NeverOverwrites()
    {
        string vt = VTank("a.met", "b.nav");
        string ai = RynthAi();
        string mine = Put(Path.Combine(ai, "MetaFiles"), "a.met", "the player's own edited meta");
        File.SetLastWriteTimeUtc(mine, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var c = Copy(Find(new[] { vt }, ai));
        Check(File.ReadAllText(mine) == "the player's own edited meta", "the player's file kept");
        Check(File.GetLastWriteTimeUtc(mine) == new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc), "not even touched");
        Check(c.Copied == 1 && c.Skipped == 1, $"1 copied 1 skipped, got {c.Copied}/{c.Skipped}");

        // A file that appears after the scan, before the copy: still not replaced.
        string vt2 = VTank("late.met");
        var scan = Find(new[] { vt2 }, ai);
        string late = Put(Path.Combine(ai, "MetaFiles"), "late.met", "arrived in between");
        var c2 = Copy(scan);
        Check(File.ReadAllText(late) == "arrived in between", "a file that arrived after the scan is kept");
        Check(c2.Copied == 0 && c2.Skipped == 1, $"0 copied 1 skipped, got {c2.Copied}/{c2.Skipped}");
    }

    private static void SourceUntouched()
    {
        string vt = VTank("a.met", "b.nav", "c.utl", "Char.usd", "utank2-i.dll");
        Put(Path.Combine(vt, "sub"), "d.met");
        string ai = RynthAi();
        Put(Path.Combine(ai, "NavProfiles"), "b.nav", "mine");
        var before = Snapshot(vt);
        Copy(Find(new[] { vt }, ai));
        Check(SameSnapshot(before, Snapshot(vt)), "every VTank file still there, same size, time, attributes and content");
    }

    private static void ReadOnlySource()
    {
        string vt = VTank("ro.met");
        string src = Path.Combine(vt, "ro.met");
        File.SetAttributes(src, FileAttributes.ReadOnly);
        string ai = RynthAi();
        var c = Copy(Find(new[] { vt }, ai));
        string dest = Path.Combine(ai, "MetaFiles", "ro.met");
        Check(c.Copied == 1, "copied");
        Check(File.Exists(dest) && (File.GetAttributes(dest) & FileAttributes.ReadOnly) == 0, "the copy is writable");
        Check((File.GetAttributes(src) & FileAttributes.ReadOnly) != 0, "the VTank file is still read-only");
    }

    private static void SecondRun()
    {
        string vt = VTank("a.met", "b.nav", "c.utl");
        string ai = RynthAi();
        var first = Copy(Find(new[] { vt }, ai));
        var again = Find(new[] { vt }, ai);
        var second = Copy(again);
        Check(first.Copied == 3, "first run copies 3");
        Check(again.NewCount == 0, "nothing new the second time");
        Check(again.SameCount == 3, $"all 3 are the same file as RynthAi's now, got {again.SameCount}");
        Check(second.Copied == 0 && second.Skipped == 0 && second.AlreadyThere == 3 && second.Failed == 0,
              $"0 copied 3 already there, got {second.Copied}/{second.Skipped}/{second.AlreadyThere}/{second.Failed}");
    }

    private static void Logged()
    {
        string vt = VTank("new.met", "old.nav");
        string ai = RynthAi();
        Put(Path.Combine(ai, "NavProfiles"), "old.nav", "the player's own old.nav");
        var c = Copy(Find(new[] { vt }, ai));
        Check(c.Log.Count == 2, $"two lines, got {c.Log.Count}");
        Check(c.Log.Any(l => l.Contains("copied") && l.Contains("new.met") && l.Contains("MetaFiles")), "copied line names source and destination");
        Check(c.Log.Any(l => l.Contains("skipped") && l.Contains("old.nav") && l.Contains("already has")), "skipped line says why");
    }

    private static void Kinds()
    {
        Check(KindOf("a.met") == Kind.Meta && KindOf("A.MET") == Kind.Meta && KindOf("a.af") == Kind.Meta, "metas");
        Check(KindOf("r.nav") == Kind.Nav && KindOf("l.utl") == Kind.Loot, "navs and loot");
        foreach (string n in new[] { "c.usd", "c.cdf", "c.ast", "g.ugd", "x.meta", "x.afx", "x.navx", "readme.txt", "noext" })
            Check(KindOf(n) == null, "not imported: " + n);
        Check(RynthAiSubfolder(Kind.Meta) == "MetaFiles" && RynthAiSubfolder(Kind.Nav) == "NavProfiles" && RynthAiSubfolder(Kind.Loot) == "LootProfiles",
              "RynthAi's three folders");
        Check(IsVTankStateFile(@"C:\VT\--Char_Server.met") && !IsVTankStateFile(@"C:\VT\-one dash.met") && !IsVTankStateFile("Hunt--x.met"),
              "state files are the \"--\" names");

        string vt = VTank("a.met", "b.af", "c.nav", "d.utl");
        string ai = RynthAi();
        var s = Find(new[] { vt }, ai);
        foreach (var f in s.Files)
            Check(Path.GetDirectoryName(f.Destination) == Path.Combine(ai, RynthAiSubfolder(f.Kind)),
                  $"{Path.GetFileName(f.Source)} goes to the top level of {RynthAiSubfolder(f.Kind)} (no server or character subfolder)");
    }

    private static void SameVersusConflict()
    {
        string vt = VTank("same.met", "diff.met", "same.nav");
        string ai = RynthAi();
        File.Copy(Path.Combine(vt, "same.met"), Path.Combine(ai, "MetaFiles", "same.met"));
        File.Copy(Path.Combine(vt, "same.nav"), Path.Combine(ai, "NavProfiles", "SAME.NAV"));
        Put(Path.Combine(ai, "MetaFiles"), "diff.met", "the player's own diff.met");
        var s = Find(new[] { vt }, ai);
        Check(s.Files.Single(f => f.Source.EndsWith("same.met")).State == State.SameInRynthAi, "identical meta: same");
        Check(s.Files.Single(f => f.Source.EndsWith("same.nav")).State == State.SameInRynthAi, "identical nav, name in another case: same");
        Check(s.Files.Single(f => f.Source.EndsWith("diff.met")).State == State.AlreadyInRynthAi, "different content: conflict");
        Check(s.NewCount == 0 && s.SameCount == 2 && s.ConflictCount == 1, $"0 new 2 same 1 conflict, got {s.NewCount}/{s.SameCount}/{s.ConflictCount}");
        // Same length, different bytes is still a conflict.
        string vt2 = VTank();
        Put(vt2, "len.met", "AAAA");
        Put(Path.Combine(ai, "MetaFiles"), "len.met", "AAAB");
        Check(Find(new[] { vt2 }, ai).Files.Single().State == State.AlreadyInRynthAi, "same length, other bytes: conflict");
        Check(SameFile(Path.Combine(vt, "same.met"), Path.Combine(ai, "MetaFiles", "same.met")), "SameFile on identical files");
        Check(!SameFile(Path.Combine(vt, "same.met"), Path.Combine(_temp, "missing.met")), "SameFile with a missing file is false");
    }

    private static void SkipMode()
    {
        string vt = VTank("new.met", "diff.met", "same.utl");
        string ai = RynthAi();
        string mine = Put(Path.Combine(ai, "MetaFiles"), "diff.met", "the player's own diff.met");
        File.Copy(Path.Combine(vt, "same.utl"), Path.Combine(ai, "LootProfiles", "same.utl"));
        var c = Copy(Find(new[] { vt }, ai), ConflictMode.Skip);
        Check(c.Copied == 1 && c.Skipped == 1 && c.AlreadyThere == 1 && c.KeptBoth == 0 && c.Failed == 0,
              $"1 copied 1 skipped 1 already there, got {c.Copied}/{c.Skipped}/{c.AlreadyThere}/{c.KeptBoth}/{c.Failed}");
        Check(File.ReadAllText(mine) == "the player's own diff.met", "RynthAi's file kept");
        Check(!File.Exists(Path.Combine(ai, "MetaFiles", "diff (VTank).met")), "no keep-both copy in skip mode");
        Check(c.Log.Any(l => l.Contains("skipped") && l.Contains("different")), "the skip line says RynthAi's file is different");
    }

    private static void KeepBothMode()
    {
        string vt = VTank();
        Put(vt, "diff.met", "VTank's diff.met");
        Put(vt, "Route.nav", "VTank's Route.nav");
        Put(vt, "new.utl", "VTank's new.utl");
        Put(vt, "same.met", "the same");
        string ai = RynthAi();
        string mineMeta = Put(Path.Combine(ai, "MetaFiles"), "diff.met", "the player's own diff.met");
        string mineNav = Put(Path.Combine(ai, "NavProfiles"), "route.NAV", "the player's own route");
        Put(Path.Combine(ai, "MetaFiles"), "same.met", "the same");
        var before = Snapshot(vt);
        var c = Copy(Find(new[] { vt }, ai), ConflictMode.KeepBoth);
        Check(c.Copied == 3 && c.KeptBoth == 2 && c.AlreadyThere == 1 && c.Skipped == 0 && c.Failed == 0,
              $"3 copied (2 kept both) 1 already there, got {c.Copied}/{c.KeptBoth}/{c.AlreadyThere}/{c.Skipped}/{c.Failed}");
        Check(File.ReadAllText(mineMeta) == "the player's own diff.met" && File.ReadAllText(mineNav) == "the player's own route", "RynthAi's files kept");
        Check(File.ReadAllText(Path.Combine(ai, "MetaFiles", "diff (VTank).met")) == "VTank's diff.met", "meta copied as 'diff (VTank).met'");
        Check(File.ReadAllText(Path.Combine(ai, "NavProfiles", "route (VTank).NAV")) == "VTank's Route.nav"
              || File.ReadAllText(Path.Combine(ai, "NavProfiles", "Route (VTank).nav")) == "VTank's Route.nav", "nav copied beside it as '(VTank)'");
        Check(File.ReadAllText(Path.Combine(ai, "LootProfiles", "new.utl")) == "VTank's new.utl", "a new file keeps its own name");
        Check(!File.Exists(Path.Combine(ai, "MetaFiles", "same (VTank).met")), "an identical file is never copied, even with keep both");
        Check(Path.GetExtension(Path.Combine(ai, "MetaFiles", "diff (VTank).met")) == ".met", "the copy keeps its extension, so RynthAi lists it");
        Check(SameSnapshot(before, Snapshot(vt)), "VTank folder unchanged");
        Check(c.Log.Any(l => l.Contains("diff (VTank).met") && l.Contains("kept")), "the log names the new name and says RynthAi's was kept");
    }

    private static void KeepBothAgain()
    {
        string vt = VTank();
        Put(vt, "diff.met", "VTank's diff.met");
        string ai = RynthAi();
        Put(Path.Combine(ai, "MetaFiles"), "diff.met", "the player's own");
        var first = Copy(Find(new[] { vt }, ai), ConflictMode.KeepBoth);
        var again = Find(new[] { vt }, ai);
        var second = Copy(again, ConflictMode.KeepBoth);
        Check(first.KeptBoth == 1, "first run keeps both");
        Check(again.SameCount == 1 && again.ConflictCount == 0, "second scan: the '(VTank)' copy is the same file");
        Check(second.Copied == 0 && second.AlreadyThere == 1, $"second run copies nothing, got {second.Copied}/{second.AlreadyThere}");
        Check(!File.Exists(Path.Combine(ai, "MetaFiles", "diff (VTank 2).met")), "no '(VTank 2)' from a second run");

        // A different VTank file of that name later: "(VTank)" is taken by the older copy -> "(VTank 2)".
        string vt2 = VTank();
        Put(vt2, "diff.met", "a newer VTank diff.met");
        var third = Copy(Find(new[] { vt2 }, ai), ConflictMode.KeepBoth);
        Check(third.KeptBoth == 1 && File.ReadAllText(Path.Combine(ai, "MetaFiles", "diff (VTank 2).met")) == "a newer VTank diff.met",
              "copied as 'diff (VTank 2).met'");
        Check(File.ReadAllText(Path.Combine(ai, "MetaFiles", "diff (VTank).met")) == "VTank's diff.met", "the earlier copy kept");
    }

    private static void KeepBothNames()
    {
        string d = @"C:\R\MetaFiles\Hunt Here.met";
        Check(KeepBothName(d, 1) == @"C:\R\MetaFiles\Hunt Here (VTank).met", "n=1: " + KeepBothName(d, 1));
        Check(KeepBothName(d, 2) == @"C:\R\MetaFiles\Hunt Here (VTank 2).met", "n=2: " + KeepBothName(d, 2));
        Check(KeepBothName(@"C:\R\NavProfiles\a.b.nav", 1) == @"C:\R\NavProfiles\a.b (VTank).nav", "only the last extension");
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { KeepBothName(d, 1), KeepBothName(d, 2) };
        Check(FreeKeepBothName(d, taken.Contains) == KeepBothName(d, 3), "first free one");
        Check(FreeKeepBothName(d, _ => true) == null, "none free: null (the file is skipped)");
    }

    private static void PickedFolder()
    {
        string plugins = NewDir("VirindiPlugins");
        string vt = Path.Combine(plugins, "VirindiTank");
        Put(vt, "a.met");
        Check(ResolvePickedFolder(vt) == vt, "the VTank folder itself");
        Check(ResolvePickedFolder(vt + "\\") == vt, "trailing slash");
        Check(ResolvePickedFolder(plugins) == vt, "the VirindiPlugins folder above it -> VirindiTank");
        string games = Path.GetDirectoryName(plugins)!;
        Check(ResolvePickedFolder(games) == vt, "the folder above VirindiPlugins -> VirindiPlugins\\VirindiTank");
        string empty = NewDir("empty");
        Check(ResolvePickedFolder(empty) == empty, "a folder without VTank files: itself (the scan then says so)");
        string onlyState = VTank("--Char_Server.met", "--Char_Server.nav", "Char.usd");
        Check(!HasImportableFiles(onlyState), "only state files and settings: nothing to import");
        Check(HasImportableFiles(vt) && !HasImportableFiles(Path.Combine(_temp, "missing")), "HasImportableFiles");
    }

    private static void PlainWords()
    {
        string vt = VTank("a.met", "b.met", "r.nav", "l.utl", "--Char_Server.met");
        string ai = RynthAi();
        Put(Path.Combine(ai, "MetaFiles"), "b.met", "mine");
        File.Copy(Path.Combine(vt, "l.utl"), Path.Combine(ai, "LootProfiles", "l.utl"));
        var s = Find(new[] { vt }, ai);
        string plan = DescribePlan(s);
        Check(plan.Contains(vt), "names the folder");
        Check(plan.Contains("2 metas (1 new) -> MetaFiles") && plan.Contains("1 nav route (1 new) -> NavProfiles")
              && plan.Contains("1 loot profile (0 new) -> LootProfiles"), "counts per kind and where they go:\n" + plan);
        Check(plan.Contains("2 files are new"), "how many are new");
        Check(plan.Contains("1 file is already in RynthAi"), "how many are already there");
        Check(plan.Contains("1 file has the same name") && plan.Contains("keep both") && plan.Contains("never replaced"), "the conflict and the choice");
        Check(plan.Contains("1 VirindiTank character file") && plan.Contains("left out"), "the state files left out");

        var noConflict = DescribePlan(Find(new[] { VTank("x.nav") }, RynthAi()));
        Check(!noConflict.Contains("keep both") && !noConflict.Contains("character file"), "no conflict or state lines when there are none");

        var r = new CopyResult { Copied = 3, KeptBoth = 1, Skipped = 2, AlreadyThere = 4, Failed = 1 };
        string text = DescribeResult(r, ai);
        Check(text.Contains("Copied 3 files to " + ai), "copied count and where: " + text);
        Check(text.Contains("1 of them was copied as \"<name> (VTank)\""), "kept both");
        Check(text.Contains("4 files were already in RynthAi"), "already there");
        Check(text.Contains("2 files were skipped") && text.Contains("yours was kept"), "skipped");
        Check(text.Contains("1 file could not be copied"), "failed");
        Check(text.Contains("within a few seconds"), "when they show in RynthAi");
        Check(DescribeResult(new CopyResult(), ai).StartsWith("No files copied."), "nothing copied");
    }

    private static string[] SplitCommandLine(string line)
    {
        // Enough for these lines: quoted parts, no embedded quotes.
        var list = new List<string>();
        var sb = new StringBuilder();
        bool q = false, any = false;
        foreach (char ch in line)
        {
            if (ch == '"') { q = !q; any = true; continue; }
            if (ch == ' ' && !q) { if (any) list.Add(sb.ToString()); sb.Clear(); any = false; continue; }
            sb.Append(ch); any = true;
        }
        if (any) list.Add(sb.ToString());
        return list.ToArray();
    }

    private static void Arguments()
    {
        string folder = @"C:\Games\Rynth Suite\RynthAi";
        string report = @"C:\Users\A B\AppData\Local\Temp\is-X.tmp\rynthcore-vtank-import-scan.txt";
        foreach (bool copy in new[] { false, true })
        {
            string[] args = SplitCommandLine(BuildArguments(copy, folder, report));
            Check(TryParseArguments(args, out bool c, out string f, out string r) && c == copy && f == folder && r == report,
                  $"round trip ({(copy ? "copy" : "scan")}): {string.Join(" | ", args)}");
        }
        string[][] bad =
        {
            new[] { "--vtank-import" },
            new[] { "--vtank-import", "scan" },
            new[] { "--vtank-import", "move", folder, "--report", report },
            new[] { "--vtank-import", "delete", folder, "--report", report },
            new[] { "--vtank-import", "scan", @"relative\RynthAi", "--report", report },
            new[] { "--vtank-import", "scan", "--report", report },
            new[] { "--vtank-import", "scan", folder },
            new[] { "--vtank-import", "scan", folder, "--report" },
            new[] { "--vtank-import", "scan", folder, "--report", @"C:\Windows\win.ini" },
            new[] { "--vtank-import", "scan", folder, "--report", @"C:\x\rynthcore-vtank-import.log" },
            new[] { "--vtank-import", "scan", folder, "--report", @"relative\rynthcore-vtank-import.txt" },
            new[] { "--decal-bridge-status" },
        };
        foreach (string[] b in bad)
            Check(!TryParseArguments(b, out _, out _, out _), "refused: " + string.Join(" ", b));
    }

    private static Dictionary<string, string> ReadReport(string path, out List<string> logs, out bool bom)
    {
        byte[] bytes = File.ReadAllBytes(path);
        bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var map = new Dictionary<string, string>();
        logs = new List<string>();
        foreach (string line in File.ReadAllLines(path))
        {
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string k = line[..eq], v = line[(eq + 1)..];
            if (k == "log") logs.Add(v); else map[k] = v;
        }
        return map;
    }

    private static string ReportPath(string tag) => Path.Combine(NewDir("tmp"), $"rynthcore-vtank-import-{tag}.txt");

    private static void RunScan()
    {
        string vt = VTank("a.met", "b.met", "r.nav", "l.utl");
        string ai = RynthAi();
        Put(Path.Combine(ai, "MetaFiles"), "b.met");
        Put(Path.Combine(ai, "LootProfiles"), "l.utl", "a different loot profile");
        string report = ReportPath("scan");
        var before = Snapshot(ai);
        int code = Run(new[] { "--vtank-import", "scan", ai, "--report", report }, new[] { vt }, out string summary);
        Check(code == 0, $"exit 0, got {code} ({summary})");
        var m = ReadReport(report, out var logs, out bool bom);
        Check(bom, "UTF-8 BOM (paths with any characters)");
        Check(File.ReadAllLines(report)[0].StartsWith("report="), "the first line is the header nothing reads (BOM-safe)");
        Check(m.GetValueOrDefault("metas") == "2" && m.GetValueOrDefault("navs") == "1" && m.GetValueOrDefault("loot") == "1",
              "counts: " + string.Join(", ", m.Select(kv => kv.Key + "=" + kv.Value)));
        Check(m.GetValueOrDefault("new") == "2", "2 new (a.met, r.nav)");
        Check(m.GetValueOrDefault("same") == "1", "b.met is the same file: same=1");
        Check(m.GetValueOrDefault("conflicts") == "1", "l.utl differs: conflicts=1");
        Check(!m.ContainsKey("copied"), "a scan has no copy counts");
        Check(logs.Any(l => l.Contains(vt)), "the folder looked in is logged");
        Check(SameSnapshot(before, Snapshot(ai)), "nothing copied");
    }

    private static void RunCopy()
    {
        string vt = VTank("a.met", "b.met", "r.nav", "l.utl");
        string ai = RynthAi();
        Put(Path.Combine(ai, "MetaFiles"), "b.met", "the player's own b.met");
        Put(Path.Combine(ai, "LootProfiles"), "l.utl");
        string report = ReportPath("copy");
        int code = Run(new[] { "--vtank-import", "copy", ai, "--report", report }, new[] { vt }, out string summary);
        Check(code == 0, $"exit 0, got {code} ({summary})");
        var m = ReadReport(report, out var logs, out _);
        Check(m.GetValueOrDefault("copied") == "2" && m.GetValueOrDefault("skipped") == "1" && m.GetValueOrDefault("already") == "1"
              && m.GetValueOrDefault("failed") == "0",
              "copy counts: " + string.Join(", ", m.Select(kv => kv.Key + "=" + kv.Value)));
        Check(logs.Count(l => l.Contains("copied ")) == 2 && logs.Count(l => l.Contains("skipped ")) == 1
              && logs.Count(l => l.Contains("already in RynthAi")) == 1, "a line per file");
        Check(File.Exists(Path.Combine(ai, "NavProfiles", "r.nav")) && File.Exists(Path.Combine(ai, "MetaFiles", "a.met")), "files there");
        Check(File.ReadAllText(Path.Combine(ai, "MetaFiles", "b.met")) == "the player's own b.met", "the command never replaces");
        Check(!Directory.EnumerateFiles(ai, "*(VTank*", SearchOption.AllDirectories).Any(), "the command never keeps both");
        Check(summary.Contains("2 copied"), "summary for launcher.log: " + summary);
    }

    private static void RunRefuses()
    {
        string vt = VTank("a.met");
        string ai = RynthAi();
        string report = ReportPath("exists");
        File.WriteAllText(report, "someone else's");
        int code = Run(new[] { "--vtank-import", "copy", ai, "--report", report }, new[] { vt }, out _);
        Check(code == 2, $"existing report: exit 2, got {code}");
        Check(File.ReadAllText(report) == "someone else's", "the existing file is not overwritten");
        Check(!File.Exists(Path.Combine(ai, "MetaFiles", "a.met")), "and nothing copied");

        string missing = Path.Combine(_temp, "no RynthAi here");
        code = Run(new[] { "--vtank-import", "copy", missing, "--report", ReportPath("missing") }, new[] { vt }, out _);
        Check(code == 2 && !Directory.Exists(missing), $"missing RynthAi folder: exit 2 and not created, got {code}");

        code = Run(new[] { "--vtank-import", "copy" }, new[] { vt }, out _);
        Check(code == 2, $"malformed: exit 2, got {code}");
    }

    private static void RunNothingFound()
    {
        string ai = RynthAi();
        string report = ReportPath("none");
        int code = Run(new[] { "--vtank-import", "scan", ai, "--report", report }, new[] { Path.Combine(_temp, "nope") }, out _);
        var m = ReadReport(report, out var logs, out _);
        Check(code == 0 && m.GetValueOrDefault("metas") == "0" && m.GetValueOrDefault("navs") == "0"
              && m.GetValueOrDefault("loot") == "0" && m.GetValueOrDefault("new") == "0", "all zero");
        Check(logs.Any(l => l.Contains("no VirindiTank folder")), "says no folder was found");
    }
}
