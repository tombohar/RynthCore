using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Xml;
using Microsoft.Win32;

namespace RynthCore.App;

/// <summary>
/// Can this Windows account write where RynthCore and its plugins keep their files, and the
/// launcher's Recovery Actions > Fix folder permissions (2026-10-13).
///
/// Players reported RynthAi metas and navs not saving and "the plugin folders are all read
/// only". On a default Windows install C:\Games\... is writable by every signed-in user, but:
///   - a C:\ whose permissions were changed (hardened / migrated PCs) leaves a folder created by
///     an elevated ("install for all users") setup readable only, so the game client, which runs
///     without administrator rights, can't save there;
///   - files can carry the ReadOnly attribute (copied from a read-only source, a zip, a CD/ISO);
///   - Windows Security's Controlled folder access blocks apps it doesn't know (acclient.exe)
///     from writing in protected folders (Documents, Desktop, ... or folders a player added);
///   - under Program Files, the 32-bit client's writes are silently redirected to
///     %LOCALAPPDATA%\VirtualStore (UAC virtualization), so "saved" files vanish for other tools.
/// <see cref="Check"/> tells which; <see cref="Repair"/> (run elevated, once) grants the player's
/// account Modify on the RynthSuite and RynthCore folders and clears ReadOnly on the files.
/// Explorer's "Read-only" box on a folder is always shown partly ticked and means nothing.
/// </summary>
internal static class FolderAccess
{
    /// <summary>The launcher's elevated step: --fix-folder-permissions &lt;user SID&gt; [--report "file"] "folder" ["folder"...].</summary>
    public const string RepairArgument = "--fix-folder-permissions";
    public const string ReportArgument = "--report";

    // ── Check ────────────────────────────────────────────────────────────────

    /// <summary>One folder as the current account sees it.</summary>
    internal sealed record FolderStatus(string Folder, bool Exists, bool Writable, string? Problem, int ReadOnlyFiles, bool Virtualized)
    {
        public bool Ok => (!Exists || Writable) && ReadOnlyFiles == 0 && !Virtualized;
    }

    /// <summary>The folders RynthAi and the engine write in (only those that exist are checked for writes).</summary>
    public static List<string> FoldersToCheck(string suiteDir, string coreDir)
    {
        string ai = Path.Combine(suiteDir, "RynthAi");
        return new List<string>
        {
            suiteDir, ai,
            Path.Combine(ai, "MetaFiles"), Path.Combine(ai, "NavProfiles"),
            Path.Combine(ai, "LootProfiles"), Path.Combine(ai, "SettingsProfiles"),
            coreDir, Path.Combine(coreDir, "Logs"),
        };
    }

    /// <summary>
    /// Writes (and deletes) a throw-away file in <paramref name="folder"/> and counts files under
    /// it marked read-only (at most <paramref name="maxFiles"/> looked at; junctions not followed).
    /// A folder that doesn't exist is not a problem (nothing to save there yet). Never throws.
    /// </summary>
    public static FolderStatus Check(string folder, bool countReadOnly = true, int maxFiles = 20_000)
    {
        bool virtualized = IsVirtualizedLocation(folder);
        if (!Directory.Exists(folder))
            return new FolderStatus(folder, false, false, null, 0, false);

        bool writable = true;
        string? problem = null;
        string probe = Path.Combine(folder, $".rynthcore-write-check-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
        try
        {
            using var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            fs.WriteByte(0);
        }
        catch (Exception ex)
        {
            writable = false;
            problem = Describe(ex);
        }
        finally
        {
            try { if (File.Exists(probe)) File.Delete(probe); } catch { }
        }

        int readOnly = countReadOnly ? CountReadOnlyFiles(folder, recursive: false, maxFiles) : 0;
        // Windows redirects only writes that would be refused: a writable folder is written for real.
        return new FolderStatus(folder, true, writable, problem, readOnly, virtualized && !writable);
    }

    /// <summary><see cref="Check"/> for each folder; read-only files are counted in each folder's own files.</summary>
    public static List<FolderStatus> CheckAll(IEnumerable<string> folders) =>
        folders.Distinct(StringComparer.OrdinalIgnoreCase).Select(f => Check(f)).ToList();

    /// <summary>The problems in plain words, one line each (empty when everything is fine).</summary>
    public static List<string> DescribeProblems(IEnumerable<FolderStatus> statuses)
    {
        var lines = new List<string>();
        foreach (FolderStatus s in statuses)
        {
            if (s.Exists && !s.Writable)
                lines.Add($"{s.Folder}: can't be written by this Windows account ({s.Problem}).");
            if (s.ReadOnlyFiles > 0)
                lines.Add($"{s.Folder}: {s.ReadOnlyFiles} file(s) marked read-only.");
            if (s.Virtualized)
                lines.Add($"{s.Folder}: is under Program Files, where Windows silently redirects the game client's saves to " +
                          "%LOCALAPPDATA%\\VirtualStore (other tools don't see them). Fix folder permissions makes it writable.");
        }
        return lines;
    }

    internal static string Describe(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "access denied",
        DirectoryNotFoundException => "folder missing",
        IOException io when io.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021) => "in use by another program",
        IOException io when io.HResult is unchecked((int)0x80070070) => "disk full",
        _ => ex.Message,
    };

    /// <summary>Files (not folders) under <paramref name="folder"/> with the ReadOnly attribute; junctions are not followed.</summary>
    public static int CountReadOnlyFiles(string folder, bool recursive, int maxFiles = 20_000)
    {
        int n = 0, seen = 0;
        try
        {
            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                ReturnSpecialDirectories = false,
            };
            foreach (string f in Directory.EnumerateFiles(folder, "*", opts))
            {
                if (++seen > maxFiles) break;
                try { if ((File.GetAttributes(f) & FileAttributes.ReadOnly) != 0) n++; } catch { }
            }
        }
        catch { }
        return n;
    }

    /// <summary>Under Program Files (either) or Windows: a legacy 32-bit client's writes there go to VirtualStore.</summary>
    public static bool IsVirtualizedLocation(string folder)
    {
        foreach (Environment.SpecialFolder sf in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Windows })
        {
            string root;
            try { root = Environment.GetFolderPath(sf); } catch { continue; }
            if (IsSameOrInside(folder, root)) return true;
        }
        return false;
    }

    internal static bool IsSameOrInside(string path, string root)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
        string p = path.TrimEnd('\\', '/');
        string r = root.TrimEnd('\\', '/');
        return p.Equals(r, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase);
    }

    // ── Repair (elevated) ────────────────────────────────────────────────────

    /// <summary>The elevated step's command line (each folder quoted).</summary>
    public static string BuildRepairArguments(string userSid, IEnumerable<string> roots, string? reportPath = null)
    {
        var sb = new StringBuilder(RepairArgument).Append(' ').Append(userSid);
        if (!string.IsNullOrEmpty(reportPath)) sb.Append(' ').Append(ReportArgument).Append(" \"").Append(reportPath).Append('"');
        foreach (string r in roots) sb.Append(" \"").Append(r.TrimEnd('\\')).Append('"');
        return sb.ToString();
    }

    /// <summary>Reads <see cref="BuildRepairArguments"/> back. False when it isn't that command or is malformed.</summary>
    public static bool TryParseRepairArguments(string[] args, out string userSid, out List<string> roots, out string? reportPath)
    {
        userSid = string.Empty;
        roots = new List<string>();
        reportPath = null;
        int i = Array.FindIndex(args, a => string.Equals(a, RepairArgument, StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= args.Length) return false;
        userSid = args[i + 1];
        try { _ = new SecurityIdentifier(userSid); }
        catch { return false; }
        for (int j = i + 2; j < args.Length; j++)
        {
            if (string.Equals(args[j], ReportArgument, StringComparison.OrdinalIgnoreCase))
            {
                if (++j >= args.Length) return false;
                reportPath = args[j];
                continue;
            }
            roots.Add(args[j]);
        }
        return roots.Count > 0;
    }

    /// <summary>
    /// A folder the elevated step may change: absolute, existing, not a junction, not a drive
    /// root, not Windows / Program Files / ProgramData / a user profile or anything containing
    /// one, and recognisably ours (RynthCore.exe in it, or a RynthAi folder / plugin DLL in it).
    /// </summary>
    public static bool IsSafeRepairRoot(string path, out string why)
    {
        why = string.Empty;
        string full;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) { why = "not an absolute path"; return false; }
            full = Path.GetFullPath(path).TrimEnd('\\');
        }
        catch { why = "not a valid path"; return false; }

        string root = (Path.GetPathRoot(full) ?? string.Empty).TrimEnd('\\');
        if (full.Length <= root.Length) { why = "a drive root"; return false; }
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) { why = "a network path"; return false; }
        if (!Directory.Exists(full)) { why = "doesn't exist"; return false; }
        try
        {
            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) { why = "a junction or link"; return false; }
        }
        catch { why = "can't be read"; return false; }

        foreach (string special in ProtectedFolders())
            if (IsSameOrInside(special, full)) { why = $"is (or contains) {special}"; return false; }
        if (IsSameOrInside(full, Environment.GetFolderPath(Environment.SpecialFolder.Windows))) { why = "inside Windows"; return false; }
        // Inside Program Files is allowed: once the folder is writable, Windows stops redirecting
        // the client's saves to VirtualStore.

        bool ours = File.Exists(Path.Combine(full, "RynthCore.exe"))
                 || Directory.Exists(Path.Combine(full, "RynthAi"))
                 || SafeAny(() => Directory.EnumerateFiles(full, "RynthCore.Plugin.*.dll", SearchOption.AllDirectories).Any());
        if (!ours) { why = "doesn't look like a RynthCore or RynthSuite folder"; return false; }
        return true;
    }

    private static bool SafeAny(Func<bool> f)
    {
        try { return f(); } catch { return false; }
    }

    private static IEnumerable<string> ProtectedFolders()
    {
        foreach (Environment.SpecialFolder sf in new[]
                 {
                     Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86,
                     Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                     Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.UserProfile,
                 })
        {
            string p;
            try { p = Environment.GetFolderPath(sf); } catch { continue; }
            if (!string.IsNullOrEmpty(p)) yield return p.TrimEnd('\\');
        }
        string? users;
        try { users = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)); } catch { users = null; }
        if (!string.IsNullOrEmpty(users)) yield return users.TrimEnd('\\');
    }

    /// <summary>What <see cref="Repair"/> did.</summary>
    internal sealed class RepairReport
    {
        public int Roots;
        public int ExplicitGrants;
        public int ReadOnlyCleared;
        public int Errors;
        public readonly List<string> Lines = new();
        public bool Ok => Errors == 0 && Roots > 0;
        public override string ToString() => string.Join(Environment.NewLine, Lines);
    }

    /// <summary>The ACE the repair adds: Modify for the account, inherited by every sub-folder and file.</summary>
    internal static FileSystemAccessRule ModifyRule(SecurityIdentifier user, bool container) =>
        new(user, FileSystemRights.Modify | FileSystemRights.Synchronize,
            container ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None,
            PropagationFlags.None, AccessControlType.Allow);

    /// <summary>
    /// The elevated step. For each root (checked with <see cref="IsSafeRepairRoot"/>): grants
    /// <paramref name="user"/> Modify, inherited by everything below (Windows passes it down to
    /// every sub-folder and file that inherits); sub-folders and files that don't inherit (their
    /// own protected permissions) get it explicitly; ReadOnly is cleared on every file. Junctions
    /// and links are never followed. Errors are counted and reported, never thrown.
    /// <paramref name="changePermissions"/> false: only the read-only part (the offline tests,
    /// which must not change permissions on the PC they run on).
    /// </summary>
    public static RepairReport Repair(IEnumerable<string> roots, SecurityIdentifier user, bool changePermissions = true)
    {
        var report = new RepairReport();
        foreach (string raw in roots)
        {
            if (!IsSafeRepairRoot(raw, out string why))
            {
                report.Errors++;
                report.Lines.Add($"Skipped {raw}: {why}.");
                continue;
            }
            string root = Path.GetFullPath(raw).TrimEnd('\\');
            report.Roots++;
            if (changePermissions)
            try
            {
                var di = new DirectoryInfo(root);
                DirectorySecurity sec = di.GetAccessControl(AccessControlSections.Access);
                sec.AddAccessRule(ModifyRule(user, container: true));
                di.SetAccessControl(sec);
                report.Lines.Add($"{root}: Modify granted to {user.Value} (passed down to everything inside).");
            }
            catch (Exception ex)
            {
                report.Errors++;
                report.Lines.Add($"{root}: couldn't change its permissions: {ex.Message}");
                continue;
            }

            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                ReturnSpecialDirectories = false,
            };
            int before = report.Errors;
            foreach (string d in changePermissions ? SafeEnumerate(() => Directory.EnumerateDirectories(root, "*", opts)) : Array.Empty<string>())
            {
                try
                {
                    var sub = new DirectoryInfo(d);
                    DirectorySecurity s = sub.GetAccessControl(AccessControlSections.Access);
                    if (s.AreAccessRulesProtected)
                    {
                        s.AddAccessRule(ModifyRule(user, container: true));
                        sub.SetAccessControl(s);
                        report.ExplicitGrants++;
                    }
                }
                catch (Exception ex) { Fail(report, d, ex); }
            }
            foreach (string f in SafeEnumerate(() => Directory.EnumerateFiles(root, "*", opts)))
            {
                try
                {
                    if (changePermissions)
                    {
                        var fi = new FileInfo(f);
                        FileSecurity s = fi.GetAccessControl(AccessControlSections.Access);
                        if (s.AreAccessRulesProtected)
                        {
                            s.AddAccessRule(ModifyRule(user, container: false));
                            fi.SetAccessControl(s);
                            report.ExplicitGrants++;
                        }
                    }
                    FileAttributes a = File.GetAttributes(f);
                    if ((a & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(f, a & ~FileAttributes.ReadOnly);
                        report.ReadOnlyCleared++;
                    }
                }
                catch (Exception ex) { Fail(report, f, ex); }
            }
            if (report.Errors > before + 20)
                report.Lines.Add($"... {report.Errors - before - 20} more error(s) under {root} not listed.");
        }
        report.Lines.Add($"Done: {report.Roots} folder(s); {report.ReadOnlyCleared} read-only file(s) cleared; " +
                         $"{report.ExplicitGrants} item(s) with their own permissions granted; {report.Errors} error(s).");
        return report;
    }

    private static void Fail(RepairReport report, string path, Exception ex)
    {
        report.Errors++;
        if (report.Lines.Count < 40) report.Lines.Add($"{path}: {ex.Message}");
    }

    private static IEnumerable<string> SafeEnumerate(Func<IEnumerable<string>> make)
    {
        IEnumerator<string>? e = null;
        try { e = make().GetEnumerator(); } catch { }
        if (e == null) yield break;
        using (e)
        {
            while (true)
            {
                string cur;
                try
                {
                    if (!e.MoveNext()) yield break;
                    cur = e.Current;
                }
                catch { yield break; }
                yield return cur;
            }
        }
    }

    // ── Controlled folder access (Windows Security ransomware protection) ────

    /// <summary>What Windows Security's Controlled folder access says about the game client.</summary>
    internal sealed record CfaInfo(int Mode, bool ModeKnown, List<CfaBlock> RecentBlocks, List<string>? AllowedApps = null)
    {
        /// <summary>A block older than this is history, not a reason to warn at every start.</summary>
        public static readonly TimeSpan Recent = TimeSpan.FromDays(3);

        /// <summary>1 = on (blocks), 2 = audit only, 0 = off.</summary>
        public bool On => Mode == 1;

        /// <summary>The game client was blocked lately, and isn't in the allowed apps since (as far as we can read them).</summary>
        public bool BlocksGameClient => GameClientBlocks(DateTime.UtcNow).Count > 0;

        public List<CfaBlock> GameClientBlocks(DateTime nowUtc) => RecentBlocks
            .Where(b => b.Blocked && IsGameClient(b.Process)
                        && (b.Time == default || nowUtc - b.Time.ToUniversalTime() <= Recent)
                        && !(AllowedApps ?? new List<string>()).Any(a => string.Equals(a, b.Process, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    internal sealed record CfaBlock(DateTime Time, string Process, string Path, bool Blocked);

    internal static bool IsGameClient(string process) =>
        process.EndsWith("acclient.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Best effort, no administrator rights needed: the Controlled folder access mode from the
    /// registry (Windows Security's switch, or group policy), and the last blocks (Defender event
    /// 1123; 1124 = audited) from Microsoft-Windows-Windows Defender/Operational via wevtutil.
    /// Never throws; unknown parts stay empty.
    /// </summary>
    public static CfaInfo QueryControlledFolderAccess(int maxEvents = 50)
    {
        int mode = 0;
        bool known = false;
        foreach (string key in new[]
                 {
                     @"SOFTWARE\Policies\Microsoft\Windows Defender\Windows Defender Exploit Guard\Controlled Folder Access",
                     @"SOFTWARE\Microsoft\Windows Defender\Windows Defender Exploit Guard\Controlled Folder Access",
                 })
        {
            try
            {
                using RegistryKey? k = Registry.LocalMachine.OpenSubKey(key);
                if (k?.GetValue("EnableControlledFolderAccess") is int v)
                {
                    mode = v;
                    known = true;
                    break;
                }
                if (k != null) known = true;
            }
            catch { }
        }

        // Apps already allowed through (Windows Security's list; group policy's is not read).
        var allowed = new List<string>();
        try
        {
            using RegistryKey? k = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows Defender\Windows Defender Exploit Guard\Controlled Folder Access\AllowedApplications");
            if (k != null) allowed.AddRange(k.GetValueNames());
        }
        catch { }

        var blocks = new List<CfaBlock>();
        try
        {
            string wevtutil = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wevtutil.exe");
            var psi = new ProcessStartInfo(wevtutil)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            foreach (string a in new[] { "qe", "Microsoft-Windows-Windows Defender/Operational",
                                         "/q:*[System[(EventID=1123 or EventID=1124)]]", $"/c:{maxEvents}", "/rd:true", "/f:xml" })
                psi.ArgumentList.Add(a);
            using Process? p = Process.Start(psi);
            if (p != null)
            {
                string xml = p.StandardOutput.ReadToEnd();
                p.WaitForExit(10_000);
                blocks.AddRange(ParseCfaEvents(xml));
            }
        }
        catch { }
        return new CfaInfo(mode, known, blocks, allowed);
    }

    /// <summary>Defender 1123/1124 events (wevtutil /f:xml output, events back to back) as blocks.</summary>
    internal static List<CfaBlock> ParseCfaEvents(string xml)
    {
        var list = new List<CfaBlock>();
        if (string.IsNullOrWhiteSpace(xml)) return list;
        try
        {
            var doc = new XmlDocument();
            doc.LoadXml("<Events>" + xml + "</Events>");
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("e", "http://schemas.microsoft.com/win/2004/08/events/event");
            XmlNodeList? events = doc.SelectNodes("/Events/e:Event", ns);
            if (events == null) return list;
            foreach (XmlNode ev in events)
            {
                string id = ev.SelectSingleNode("e:System/e:EventID", ns)?.InnerText ?? "";
                string time = ev.SelectSingleNode("e:System/e:TimeCreated/@SystemTime", ns)?.Value ?? "";
                string process = "", path = "";
                XmlNodeList? data = ev.SelectNodes("e:EventData/e:Data", ns);
                if (data != null)
                    foreach (XmlNode d in data)
                    {
                        string name = d.Attributes?["Name"]?.Value ?? "";
                        if (name.Equals("Process Name", StringComparison.OrdinalIgnoreCase)) process = d.InnerText;
                        else if (name.Equals("Path", StringComparison.OrdinalIgnoreCase)) path = d.InnerText;
                    }
                DateTime.TryParse(time, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime t);
                list.Add(new CfaBlock(t, process, path, id == "1123"));
            }
        }
        catch { }
        return list;
    }

    /// <summary>How to let the game client through, in Windows Security's words.</summary>
    public const string CfaAllowHowTo =
        "Windows Security > Virus & threat protection > Manage ransomware protection > " +
        "Allow an app through Controlled folder access > Add an allowed app > Browse all apps, and pick acclient.exe " +
        "(in your Asheron's Call folder). Or keep RynthSuite in C:\\Games, which isn't protected unless you added it.";
}
