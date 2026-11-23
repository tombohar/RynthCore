using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace RynthCore.App;

/// <summary>
/// Copies a player's VirindiTank files into RynthAi. Used by the installer's offer
/// (installer\RynthCore.iss, OfferVTankImport, through the --vtank-import command below) and by the
/// launcher's Runtime > Recovery Actions > "Import VirindiTank files" button
/// (RynthCore.App.Avalonia\MainWindow.VTankImport.cs). RynthAi loads these files as they are:
///   *.met  metas                -> RynthAi\MetaFiles
///   *.af   metas as text (metaf) -> RynthAi\MetaFiles
///   *.nav  nav routes           -> RynthAi\NavProfiles
///   *.utl  loot profiles        -> RynthAi\LootProfiles
/// RynthAi lists only the top level of those three folders (no per-server or per-character
/// subfolders; its pickers re-list a folder within 3 s of a change), so that is where they go.
/// Character settings (.usd, .cdf, .ast) are not part of this: they are imported per character
/// in game. VTank's own per-character state files ("--&lt;character&gt;_&lt;server&gt;.met/.nav",
/// the meta and nav each character last had loaded) are left out too: RynthAi's meta list hides
/// "--" names anyway, and hundreds of them would bury the real navs.
///
/// Where VTank keeps them: the top level of its own install folder (a real install has every one
/// of them there, no subfolders). So only the top level of each VTank folder is read, never deeper
/// and never a whole drive. The folders are VTank's entry in Decal's plugin list (its Path value,
/// read only), the usual install locations, or one the player picks in the launcher.
///
/// The copy never overwrites, never deletes, never moves: nothing in a VTank folder is changed,
/// and a file whose name RynthAi already has is skipped, unless the player chose "keep both" in the
/// launcher: then the VTank one is copied as "&lt;name&gt; (VTank)&lt;ext&gt;". A file identical to the
/// one RynthAi has (or to an earlier "keep both" copy) is never copied again. A copy that arrives
/// read-only (a read-only source) has ReadOnly cleared, or RynthAi couldn't save edits to it.
///
/// Command line (RynthCore.exe, run by the installer as the installing user, no window):
///   --vtank-import scan "&lt;RynthAi folder&gt;" --report "&lt;file&gt;"   counts what would be offered
///   --vtank-import copy "&lt;RynthAi folder&gt;" --report "&lt;file&gt;"   copies (never keep both); each file logged
/// The report is "key=value" lines in UTF-8 with a BOM (paths can have any characters):
/// a "report=" header first (which nothing reads, so a reader that keeps the BOM on line 1
/// loses nothing), metas, navs, loot (found, by distinct name), new (not yet in RynthAi),
/// conflicts (a different file of that name in RynthAi), same (already in RynthAi), copied,
/// skipped, failed, and any number of "log=" lines for the setup log.
///
/// VirindiSense (a separate Virindi plugin, its own Decal entry and folder, usually
/// C:\Games\VirindiPlugins\VirindiSense) is not read here.
/// </summary>
public static class VTankFileImport
{
    public const string Argument = "--vtank-import";

    /// <summary>The report must be a new file of this name shape (a stray path can't be overwritten).</summary>
    public const string ReportPrefix = "rynthcore-vtank-import";

    /// <summary>VTank's plugin entry in Decal's list (HKLM\SOFTWARE\Decal\Plugins\{...}, 32-bit view).</summary>
    public const string VTankPluginGuid = "{642F1F48-16BE-48BF-B1D4-286652C4533E}";
    public const string VTankPluginObject = "uTank2.PluginCore";

    /// <summary>Upper bound per VTank folder per extension (a real one has a few hundred to a thousand).</summary>
    public const int MaxFilesPerKind = 20000;

    /// <summary>The name a "keep both" copy gets: "&lt;name&gt; (VTank)&lt;ext&gt;", then "(VTank 2)" ...</summary>
    public const string KeepBothTag = "VTank";
    private const int MaxKeepBothNumber = 999;

    /// <summary>VTank's per-character state files start with this ("--&lt;character&gt;_&lt;server&gt;.met").</summary>
    public const string VTankStatePrefix = "--";

    public enum Kind { Meta, Nav, Loot }

    /// <summary>What to do with a VTank file whose name RynthAi already has (a different file).</summary>
    public enum ConflictMode
    {
        /// <summary>Leave it: RynthAi's file stays, the VTank one is not copied (the installer's only mode).</summary>
        Skip,
        /// <summary>Copy the VTank one beside it as "&lt;name&gt; (VTank)&lt;ext&gt;".</summary>
        KeepBoth,
    }

    public static IReadOnlyList<string> Extensions(Kind k) => k switch
    {
        Kind.Meta => new[] { ".met", ".af" },
        Kind.Nav => new[] { ".nav" },
        _ => new[] { ".utl" },
    };

    public static string RynthAiSubfolder(Kind k) => k switch { Kind.Meta => "MetaFiles", Kind.Nav => "NavProfiles", _ => "LootProfiles" };
    public static readonly Kind[] AllKinds = { Kind.Meta, Kind.Nav, Kind.Loot };

    /// <summary>The kind for a file name, by exact extension (any case); null when it isn't imported.</summary>
    public static Kind? KindOf(string fileName)
    {
        string ext = Path.GetExtension(fileName);
        foreach (Kind k in AllKinds)
            if (Extensions(k).Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase))) return k;
        return null;
    }

    /// <summary>VTank's own per-character state ("--name_server.met"), never imported.</summary>
    public static bool IsVTankStateFile(string fileName) =>
        Path.GetFileName(fileName).StartsWith(VTankStatePrefix, StringComparison.Ordinal);

    public enum State
    {
        /// <summary>Not in RynthAi yet: copied.</summary>
        New,
        /// <summary>RynthAi has a different file of this name: skipped, or copied beside it with "keep both".</summary>
        AlreadyInRynthAi,
        /// <summary>RynthAi already has this very file (same bytes, same name or an earlier "keep both" copy): left alone.</summary>
        SameInRynthAi,
        /// <summary>Another VTank folder had the same name first: that one is the one offered.</summary>
        DuplicateName,
    }

    public sealed record FoundFile(Kind Kind, string Source, string Destination, State State);

    public sealed class ScanResult
    {
        public List<string> Roots { get; } = new();
        public List<FoundFile> Files { get; } = new();
        public List<string> Log { get; } = new();

        /// <summary>VTank's per-character state files seen and left out.</summary>
        public int StateFilesLeftOut { get; set; }

        /// <summary>Distinct names of this kind found (duplicates across VTank folders counted once).</summary>
        public int Count(Kind k) => Files.Count(f => f.Kind == k && f.State != State.DuplicateName);
        public int Count(Kind k, State s) => Files.Count(f => f.Kind == k && f.State == s);
        public int CountState(State s) => Files.Count(f => f.State == s);
        public int NewCount => CountState(State.New);
        public int ConflictCount => CountState(State.AlreadyInRynthAi);
        public int SameCount => CountState(State.SameInRynthAi);
        public int TotalFound => Files.Count(f => f.State != State.DuplicateName);
    }

    public sealed class CopyResult
    {
        /// <summary>Copied, under their own name or (keep both) a "(VTank)" one.</summary>
        public int Copied;
        /// <summary>Of <see cref="Copied"/>, those copied beside a different file of the same name.</summary>
        public int KeptBoth;
        /// <summary>Not copied: RynthAi has a different file of that name, or another VTank folder had it first.</summary>
        public int Skipped;
        /// <summary>Not copied: RynthAi already has this very file.</summary>
        public int AlreadyThere;
        public int Failed;
        public List<string> Log { get; } = new();
    }

    // ---------------------------------------------------------------------------- folders

    /// <summary>
    /// VTank folders from Decal's plugin entries (guid, Object value, Path value): the entry with
    /// VTank's GUID or object name. Returns each Path that is an absolute folder path.
    /// </summary>
    public static List<string> VTankPathsFromPluginEntries(IEnumerable<(string Guid, string? Object, string? Path)> entries)
    {
        var list = new List<string>();
        foreach (var (guid, obj, path) in entries)
        {
            bool isVTank = string.Equals(guid?.Trim(), VTankPluginGuid, StringComparison.OrdinalIgnoreCase)
                           || string.Equals(obj?.Trim(), VTankPluginObject, StringComparison.OrdinalIgnoreCase);
            if (!isVTank || string.IsNullOrWhiteSpace(path)) continue;
            string p = path.Trim().Trim('"');
            if (!IsAbsoluteFolderPath(p)) continue;
            list.Add(p);
        }
        return list;
    }

    /// <summary>
    /// Every place to look, in order: Decal's entry first, then the usual install locations.
    /// Duplicates (any case, trailing slash) once. Existence is checked by <see cref="Find"/>.
    /// </summary>
    public static List<string> CandidateRoots(IEnumerable<string> fromDecal, string? documents, string? localAppData,
                                              string? programFilesX86, string? programFiles)
    {
        var all = new List<string>(fromDecal);
        all.Add(@"C:\Games\VirindiPlugins\VirindiTank");
        if (!string.IsNullOrEmpty(documents))
        {
            all.Add(Path.Combine(documents, "VirindiPlugins", "VirindiTank"));
            all.Add(Path.Combine(documents, "Decal", "VirindiPlugins", "VirindiTank"));
        }
        if (!string.IsNullOrEmpty(localAppData)) all.Add(Path.Combine(localAppData, "VirindiPlugins", "VirindiTank"));
        if (!string.IsNullOrEmpty(programFilesX86)) all.Add(Path.Combine(programFilesX86, "VirindiPlugins", "VirindiTank"));
        if (!string.IsNullOrEmpty(programFiles)) all.Add(Path.Combine(programFiles, "VirindiPlugins", "VirindiTank"));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (string r in all)
        {
            if (!IsAbsoluteFolderPath(r)) continue;
            string norm;
            try { norm = Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)); } catch { continue; }
            if (seen.Add(norm)) result.Add(norm);
        }
        return result;
    }

    /// <summary>This PC's folders: Decal's VTank entry (read only, both registry views a 32-bit Decal uses) + the usual ones.</summary>
    public static List<string> DefaultRoots()
    {
        var entries = new List<(string, string?, string?)>();
        ReadPluginEntries(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Decal\Plugins", entries);
        // A per-user (VirtualStore) copy of Decal's list, which a non-elevated client reads instead.
        ReadPluginEntries(RegistryHive.CurrentUser, RegistryView.Default,
                          @"Software\Classes\VirtualStore\MACHINE\SOFTWARE\WOW6432Node\Decal\Plugins", entries);
        return CandidateRoots(VTankPathsFromPluginEntries(entries),
                              SafeFolder(Environment.SpecialFolder.MyDocuments),
                              SafeFolder(Environment.SpecialFolder.LocalApplicationData),
                              SafeFolder(Environment.SpecialFolder.ProgramFilesX86),
                              SafeFolder(Environment.SpecialFolder.ProgramFiles));
    }

    /// <summary>
    /// A folder the player picked: itself when it has VTank files at the top level, else its
    /// VirindiTank or VirindiPlugins\VirindiTank subfolder when that has them (the player picked the
    /// Virindi folder above it), else itself.
    /// </summary>
    public static string ResolvePickedFolder(string picked)
    {
        string p;
        try { p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(picked)); } catch { return picked; }
        if (HasImportableFiles(p)) return p;
        foreach (string sub in new[] { Path.Combine(p, "VirindiTank"), Path.Combine(p, "VirindiPlugins", "VirindiTank") })
            if (HasImportableFiles(sub)) return sub;
        return p;
    }

    /// <summary>True when the top level of <paramref name="folder"/> has a file this import copies.</summary>
    public static bool HasImportableFiles(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return false;
            return Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
                            .Any(f => KindOf(f) != null && !IsVTankStateFile(f));
        }
        catch { return false; }
    }

    private static void ReadPluginEntries(RegistryHive hive, RegistryView view, string subKey, List<(string, string?, string?)> into)
    {
        try
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(hive, view);
            using RegistryKey? plugins = root.OpenSubKey(subKey, writable: false);
            if (plugins == null) return;
            foreach (string name in plugins.GetSubKeyNames())
            {
                try
                {
                    using RegistryKey? k = plugins.OpenSubKey(name, writable: false);
                    if (k == null) continue;
                    into.Add((name, k.GetValue("Object") as string, k.GetValue("Path") as string));
                }
                catch { }
            }
        }
        catch { }
    }

    private static string? SafeFolder(Environment.SpecialFolder f)
    {
        try { string s = Environment.GetFolderPath(f); return string.IsNullOrEmpty(s) ? null : s; } catch { return null; }
    }

    private static bool IsAbsoluteFolderPath(string p)
    {
        try { return !string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p) && p.IndexOfAny(Path.GetInvalidPathChars()) < 0; }
        catch { return false; }
    }

    // ---------------------------------------------------------------------------- scan + copy

    /// <summary>
    /// The .met/.af/.nav/.utl files at the top level of each existing root (exact extension: "*.met"
    /// alone would also match ".meta" through Windows' short names), VTank's "--" state files left
    /// out. A root that is the RynthAi folder or inside it is never read. Changes nothing.
    /// </summary>
    public static ScanResult Find(IEnumerable<string> roots, string rynthAiFolder)
    {
        var result = new ScanResult();
        string dest = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rynthAiFolder));
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots)
        {
            string r;
            try { r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); } catch { continue; }
            if (!Directory.Exists(r) || !seenRoots.Add(r)) continue;
            if (IsSameOrInside(r, dest))
            {
                result.Log.Add($"VTank import: {r} is inside RynthAi's folder - not read.");
                continue;
            }
            result.Roots.Add(r);
            int stateFiles = 0;
            foreach (Kind k in AllKinds)
            {
                foreach (string ext in Extensions(k))
                {
                    int n = 0;
                    List<string> files;
                    try
                    {
                        files = Directory.EnumerateFiles(r, "*" + ext, SearchOption.TopDirectoryOnly)
                                         .Where(f => string.Equals(Path.GetExtension(f), ext, StringComparison.OrdinalIgnoreCase))
                                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                                         .ToList();
                    }
                    catch (Exception ex)
                    {
                        result.Log.Add($"VTank import: cannot read {r} ({ext}): {ex.Message}");
                        continue;
                    }
                    foreach (string src in files)
                    {
                        if (IsVTankStateFile(src)) { stateFiles++; continue; }
                        if (++n > MaxFilesPerKind)
                        {
                            result.Log.Add($"VTank import: more than {MaxFilesPerKind} {ext} files in {r} - the rest left out.");
                            break;
                        }
                        string to = Path.Combine(dest, RynthAiSubfolder(k), Path.GetFileName(src));
                        if (!claimed.Add(to))
                        {
                            result.Files.Add(new FoundFile(k, src, to, State.DuplicateName));
                            continue;
                        }
                        if (!File.Exists(to))
                        {
                            result.Files.Add(new FoundFile(k, src, to, State.New));
                            continue;
                        }
                        // RynthAi has that name. The same file (or an earlier "keep both" copy of it) = nothing to do.
                        string? same = SameFile(src, to) ? to : FindKeptCopy(src, to);
                        result.Files.Add(same != null
                            ? new FoundFile(k, src, same, State.SameInRynthAi)
                            : new FoundFile(k, src, to, State.AlreadyInRynthAi));
                    }
                }
            }
            if (stateFiles > 0)
            {
                result.StateFilesLeftOut += stateFiles;
                result.Log.Add($"VTank import: {stateFiles} VirindiTank character state file(s) (\"--\" names) in {r} left out.");
            }
        }
        return result;
    }

    /// <summary>"&lt;dir&gt;\&lt;stem&gt; (VTank)&lt;ext&gt;" for n = 1, "(VTank n)" after.</summary>
    public static string KeepBothName(string destination, int n)
    {
        string dir = Path.GetDirectoryName(destination) ?? "";
        string stem = Path.GetFileNameWithoutExtension(destination);
        string ext = Path.GetExtension(destination);
        string tag = n <= 1 ? $" ({KeepBothTag})" : $" ({KeepBothTag} {n})";
        return Path.Combine(dir, stem + tag + ext);
    }

    /// <summary>The first free "keep both" name for <paramref name="destination"/>; null after 999 tries.</summary>
    public static string? FreeKeepBothName(string destination, Func<string, bool> taken)
    {
        for (int n = 1; n <= MaxKeepBothNumber; n++)
        {
            string c = KeepBothName(destination, n);
            if (!taken(c)) return c;
        }
        return null;
    }

    /// <summary>An existing "keep both" copy of <paramref name="source"/> beside <paramref name="destination"/>, if any.</summary>
    private static string? FindKeptCopy(string source, string destination)
    {
        for (int n = 1; n <= MaxKeepBothNumber; n++)
        {
            string c = KeepBothName(destination, n);
            if (!File.Exists(c)) return null;
            if (SameFile(source, c)) return c;
        }
        return null;
    }

    /// <summary>Same length and bytes. False when either can't be read.</summary>
    public static bool SameFile(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (!fa.Exists || !fb.Exists || fa.Length != fb.Length) return false;
            using var sa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var ba = new byte[81920];
            var bb = new byte[81920];
            while (true)
            {
                int ra = sa.ReadAtLeast(ba, ba.Length, throwOnEndOfStream: false);
                int rb = sb.ReadAtLeast(bb, bb.Length, throwOnEndOfStream: false);
                if (ra != rb) return false;
                if (ra == 0) return true;
                if (!ba.AsSpan(0, ra).SequenceEqual(bb.AsSpan(0, rb))) return false;
            }
        }
        catch { return false; }
    }

    /// <summary>
    /// Copies the <see cref="State.New"/> files, and with <see cref="ConflictMode.KeepBoth"/> the
    /// <see cref="State.AlreadyInRynthAi"/> ones under a "(VTank)" name. Never overwrites; the sources are only read.
    /// </summary>
    public static CopyResult Copy(ScanResult scan, ConflictMode mode = ConflictMode.Skip)
    {
        var result = new CopyResult();
        var usedThisRun = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (FoundFile f in scan.Files)
        {
            string target = f.Destination;
            bool keptBoth = false;
            switch (f.State)
            {
                case State.SameInRynthAi:
                    result.AlreadyThere++;
                    result.Log.Add($"VTank import: {f.Source} - already in RynthAi as {f.Destination}");
                    continue;
                case State.DuplicateName:
                    result.Skipped++;
                    result.Log.Add($"VTank import: skipped {f.Source} - another VirindiTank folder has a file of the same name");
                    continue;
                case State.AlreadyInRynthAi when mode == ConflictMode.Skip:
                    result.Skipped++;
                    result.Log.Add($"VTank import: skipped {f.Source} - RynthAi already has a different {f.Destination}");
                    continue;
                case State.AlreadyInRynthAi:
                    string? free = FreeKeepBothName(f.Destination, p => usedThisRun.Contains(p) || File.Exists(p));
                    if (free == null)
                    {
                        result.Skipped++;
                        result.Log.Add($"VTank import: skipped {f.Source} - no free \"({KeepBothTag})\" name beside {f.Destination}");
                        continue;
                    }
                    target = free;
                    keptBoth = true;
                    break;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    result.Skipped++;
                    result.Log.Add($"VTank import: skipped {f.Source} - RynthAi already has {target}");
                    continue;
                }
                File.Copy(f.Source, target, overwrite: false);
                usedThisRun.Add(target);
                try
                {
                    var a = File.GetAttributes(target);
                    if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(target, a & ~FileAttributes.ReadOnly);
                }
                catch { }
                result.Copied++;
                if (keptBoth) result.KeptBoth++;
                result.Log.Add(keptBoth
                    ? $"VTank import: copied {f.Source} -> {target} (RynthAi's own {Path.GetFileName(f.Destination)} kept)"
                    : $"VTank import: copied {f.Source} -> {target}");
            }
            catch (IOException) when (File.Exists(target))
            {
                // Appeared between the check and the copy: still never replaced.
                result.Skipped++;
                result.Log.Add($"VTank import: skipped {f.Source} - RynthAi already has {target}");
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Log.Add($"VTank import: could not copy {f.Source}: {ex.Message}");
            }
        }
        return result;
    }

    private static bool IsSameOrInside(string path, string folder)
    {
        if (string.Equals(path, folder, StringComparison.OrdinalIgnoreCase)) return true;
        return path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------- plain words (launcher)

    private static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";

    private static string KindWords(Kind k, int n) => k switch
    {
        Kind.Meta => Plural(n, "meta", "metas"),
        Kind.Nav => Plural(n, "nav route", "nav routes"),
        _ => Plural(n, "loot profile", "loot profiles"),
    };

    /// <summary>What the scan found and what a copy would do, for the launcher's question.</summary>
    public static string DescribePlan(ScanResult scan)
    {
        var sb = new StringBuilder();
        sb.Append(scan.Roots.Count == 1 ? $"VirindiTank folder: {scan.Roots[0]}" : "VirindiTank folders: " + string.Join(", ", scan.Roots));
        sb.Append("\n\nFound:");
        foreach (Kind k in AllKinds)
        {
            int all = scan.Count(k);
            int fresh = scan.Count(k, State.New);
            sb.Append($"\n  {KindWords(k, all)}" + (all > 0 ? $" ({fresh} new) -> {RynthAiSubfolder(k)}" : ""));
        }
        sb.Append("\n");
        sb.Append($"\n{Plural(scan.NewCount, "file is", "files are")} new to RynthAi and will be copied.");
        if (scan.SameCount > 0)
            sb.Append($"\n{Plural(scan.SameCount, "file is", "files are")} already in RynthAi (the same file), so left alone.");
        if (scan.ConflictCount > 0)
            sb.Append($"\n{Plural(scan.ConflictCount, "file has", "files have")} the same name as a different file in RynthAi. " +
                      $"Your RynthAi file is never replaced: \"Copy\" skips these; \"Copy, keep both\" copies them as " +
                      $"\"<name> ({KeepBothTag})\".");
        if (scan.StateFilesLeftOut > 0)
            sb.Append($"\n{Plural(scan.StateFilesLeftOut, "VirindiTank character file", "VirindiTank character files")} " +
                      "(names starting with \"--\", the meta and nav each character last had loaded) are left out.");
        sb.Append("\n\nNothing in your VirindiTank folder is changed. Character settings (.usd) are imported per character in game.");
        return sb.ToString();
    }

    /// <summary>The result in plain words, for the launcher's closing message.</summary>
    public static string DescribeResult(CopyResult c, string rynthAiFolder)
    {
        var sb = new StringBuilder();
        sb.Append(c.Copied == 0 ? "No files copied." : $"Copied {Plural(c.Copied, "file", "files")} to {rynthAiFolder}.");
        if (c.KeptBoth > 0)
            sb.Append($"\n{Plural(c.KeptBoth, "of them was", "of them were")} copied as \"<name> ({KeepBothTag})\" beside your own file of the same name.");
        if (c.AlreadyThere > 0)
            sb.Append($"\n{Plural(c.AlreadyThere, "file was", "files were")} already in RynthAi.");
        if (c.Skipped > 0)
            sb.Append($"\n{Plural(c.Skipped, "file was", "files were")} skipped: RynthAi already has a different file of that name (yours was kept).");
        if (c.Failed > 0)
            sb.Append($"\n{Plural(c.Failed, "file", "files")} could not be copied (launcher.log says why).");
        if (c.Copied > 0)
            sb.Append("\n\nThey show in RynthAi's Meta, Nav and Loot lists within a few seconds, also in a game that is running.");
        return sb.ToString();
    }

    // ---------------------------------------------------------------------------- command line

    public static bool TryParseArguments(string[] args, out bool copy, out string rynthAiFolder, out string reportPath)
    {
        copy = false; rynthAiFolder = ""; reportPath = "";
        int i = Array.FindIndex(args, a => string.Equals(a, Argument, StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 2 >= args.Length) return false;
        string mode = args[i + 1];
        if (string.Equals(mode, "scan", StringComparison.OrdinalIgnoreCase)) copy = false;
        else if (string.Equals(mode, "copy", StringComparison.OrdinalIgnoreCase)) copy = true;
        else return false;
        string folder = args[i + 2].Trim().Trim('"');
        if (folder.StartsWith("--", StringComparison.Ordinal) || !IsAbsoluteFolderPath(folder)) return false;
        int r = Array.FindIndex(args, a => string.Equals(a, "--report", StringComparison.OrdinalIgnoreCase));
        if (r < 0 || r + 1 >= args.Length) return false;
        string report = args[r + 1].Trim().Trim('"');
        if (!IsAbsoluteFolderPath(report)) return false;
        string name = Path.GetFileName(report);
        if (!name.StartsWith(ReportPrefix, StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            return false;
        rynthAiFolder = folder;
        reportPath = report;
        return true;
    }

    public static string BuildArguments(bool copy, string rynthAiFolder, string reportPath) =>
        $"{Argument} {(copy ? "copy" : "scan")} \"{rynthAiFolder}\" --report \"{reportPath}\"";

    public static List<string> ReportLines(ScanResult scan, CopyResult? copy)
    {
        var lines = new List<string>
        {
            // First line carries nothing the installer reads: a reader that keeps the BOM on line 1 loses nothing.
            "report=rynthcore-vtank-import 2",
            $"metas={scan.Count(Kind.Meta)}",
            $"navs={scan.Count(Kind.Nav)}",
            $"loot={scan.Count(Kind.Loot)}",
            $"new={scan.NewCount}",
            $"conflicts={scan.ConflictCount}",
            $"same={scan.SameCount}",
        };
        if (copy != null)
        {
            lines.Add($"copied={copy.Copied}");
            lines.Add($"skipped={copy.Skipped}");
            lines.Add($"already={copy.AlreadyThere}");
            lines.Add($"failed={copy.Failed}");
        }
        foreach (string r in scan.Roots) lines.Add($"log=VTank import: looked in {r}");
        if (scan.Roots.Count == 0) lines.Add("log=VTank import: no VirindiTank folder found.");
        lines.AddRange(scan.Log.Select(OneLine));
        if (copy != null) lines.AddRange(copy.Log.Select(OneLine));
        return lines;
    }

    private static string OneLine(string s) => "log=" + s.Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>
    /// The command. Exit 0 = done (scan, or a copy with no failures), 1 = a copy failed or the
    /// report couldn't be written, 2 = malformed command line or no RynthAi folder (nothing done).
    /// <paramref name="summary"/> is one line for launcher.log. The command never keeps both.
    /// </summary>
    public static int Run(string[] args, IEnumerable<string>? roots, out string summary)
    {
        if (!TryParseArguments(args, out bool copy, out string folder, out string report))
        {
            summary = "malformed command line; nothing done.";
            return 2;
        }
        if (!Directory.Exists(folder))
        {
            summary = $"no RynthAi folder at {folder}; nothing done.";
            return 2;
        }
        if (File.Exists(report))
        {
            summary = $"the report file {report} already exists; nothing done.";
            return 2;
        }
        ScanResult scan = Find(roots ?? DefaultRoots(), folder);
        CopyResult? copied = copy ? Copy(scan, ConflictMode.Skip) : null;
        summary = copied == null
            ? $"scan: {scan.Count(Kind.Meta)} metas, {scan.Count(Kind.Nav)} navs, {scan.Count(Kind.Loot)} loot, {scan.NewCount} new, " +
              $"{scan.ConflictCount} name conflicts, {scan.SameCount} already there, in {scan.Roots.Count} folder(s)."
            : $"copy: {copied.Copied} copied, {copied.Skipped} skipped, {copied.AlreadyThere} already there, {copied.Failed} failed.";
        try
        {
            File.WriteAllLines(report, ReportLines(scan, copied), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch (Exception ex)
        {
            summary += $" Report not written: {ex.Message}";
            return 1;
        }
        return copied is { Failed: > 0 } ? 1 : 0;
    }
}
