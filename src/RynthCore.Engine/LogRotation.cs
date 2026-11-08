// ============================================================================
//  RynthCore.Engine - LogRotation.cs
//  The per-client log's rotated copies (2026-11-08). One .old copy kept a busy
//  client's last 30-60 minutes at most, so the log of a death an hour earlier
//  was already gone. Now a rotation shifts the older copies down a chain:
//
//    RynthCore.<pid>.log          live
//    RynthCore.<pid>.log.old      newest rotated copy (the name every reader knows)
//    RynthCore.<pid>.log.old.1    the one before it
//    ...
//    RynthCore.<pid>.log.old.<keep-1>   the oldest kept
//
//  ".old" stays the newest so the problem report, the launcher, soak_scorecard
//  and everyone's habits keep working unchanged; older copies only ever gain a
//  number. Pure file work with no engine state: tools\ProblemReportTests
//  compiles this file in.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;

namespace RynthCore.Engine;

internal static class LogRotation
{
    /// <summary>Rotated copies kept per client by default (.old + .old.1 ... .old.7, ~40 MB at 5 MB each).</summary>
    public const int DefaultKeepCopies = 8;

    /// <summary>1 = only .old, the behaviour before 2026-11-08.</summary>
    public const int MinKeepCopies = 1;

    /// <summary>Upper bound for engine.json "LogKeepCopies" (250 MB per client at 5 MB each).</summary>
    public const int MaxKeepCopies = 50;

    public static int ClampKeepCopies(int value) =>
        value < MinKeepCopies ? MinKeepCopies : value > MaxKeepCopies ? MaxKeepCopies : value;

    /// <summary>Rotated copy <paramref name="index"/> of <paramref name="active"/>: 0 = ".old" (newest), n = ".old.n".</summary>
    public static string CopyPath(string active, int index) =>
        index <= 0 ? active + ".old" : active + ".old." + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>What <see cref="Rotate"/> did with the live file.</summary>
    public enum Outcome
    {
        /// <summary>The live file was moved to .old (the older copies shifted first).</summary>
        Moved,
        /// <summary>The live file couldn't be moved (a handle without delete sharing); it was emptied instead.</summary>
        Truncated,
        /// <summary>Neither worked; the file is as it was.</summary>
        Failed,
        /// <summary>No live file.</summary>
        Missing,
    }

    /// <summary>
    /// Rotates <paramref name="active"/>: the copies shift one step older (.old -> .old.1 -> ...),
    /// the one that would pass <paramref name="keepCopies"/> is deleted (and any left from a larger
    /// setting), and the live file becomes .old. Each step is best-effort: a copy that can't be
    /// moved stays where it is, and when .old itself can't be moved it is deleted as before, so the
    /// live file still rotates. The caller lets go of its own handle first.
    /// </summary>
    public static Outcome Rotate(string active, int keepCopies)
    {
        if (!File.Exists(active)) return Outcome.Missing;
        int keep = ClampKeepCopies(keepCopies);

        // Drop the oldest (index keep-1 would shift to keep) and anything past the cap.
        foreach (int index in NumberedCopies(active))
        {
            if (index >= keep - 1)
                try { File.Delete(CopyPath(active, index)); } catch { }
        }

        // Shift oldest-first so nothing is overwritten that hasn't moved on.
        for (int index = keep - 2; index >= 0; index--)
        {
            string from = CopyPath(active, index);
            if (!File.Exists(from)) continue;
            try { File.Move(from, CopyPath(active, index + 1), overwrite: true); } catch { }
        }

        string newest = CopyPath(active, 0);
        try { if (File.Exists(newest)) File.Delete(newest); } catch { }   // only when it couldn't shift (or keep = 1)

        try
        {
            File.Move(active, newest);
            return Outcome.Moved;
        }
        catch
        {
            // A hot-reloaded engine generation in this same process may hold the handle.
            // Truncate so the file doesn't grow without bound (only ever our own client's file).
            try { File.WriteAllText(active, string.Empty); return Outcome.Truncated; }
            catch { return Outcome.Failed; }
        }
    }

    /// <summary>The n of every existing "<paramref name="active"/>.old.n" (n ≥ 1), unordered.</summary>
    public static List<int> NumberedCopies(string active)
    {
        var found = new List<int>();
        try
        {
            string? dir = Path.GetDirectoryName(active);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return found;
            string prefix = Path.GetFileName(active) + ".old.";
            // Win32 pattern rules make "x.old.*" match "x.old" too: check the prefix by hand.
            foreach (string path in Directory.EnumerateFiles(dir, prefix + "*"))
            {
                string name = Path.GetFileName(path);
                if (name.Length <= prefix.Length || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                string suffix = name.Substring(prefix.Length);
                if (suffix.Length > 0 && suffix.Length <= 6 && IsDigits(suffix)
                    && int.TryParse(suffix, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n)
                    && n >= 1)
                    found.Add(n);
            }
        }
        catch { }
        return found;
    }

    /// <summary>
    /// The client PID in a per-client log name: RynthCore.&lt;pid&gt;.log, .log.old or .log.old.&lt;n&gt;.
    /// Null for anything else (the shared RynthCore.log, a renamed copy).
    /// </summary>
    public static int? PidOf(string fileName)
    {
        const string head = "RynthCore.";
        if (!fileName.StartsWith(head, StringComparison.OrdinalIgnoreCase)) return null;
        int dot = fileName.IndexOf('.', head.Length);
        if (dot <= head.Length) return null;
        string digits = fileName.Substring(head.Length, dot - head.Length);
        if (digits.Length > 10 || !IsDigits(digits)) return null;
        string rest = fileName.Substring(dot);   // ".log", ".log.old", ".log.old.3"
        bool known = rest.Equals(".log", StringComparison.OrdinalIgnoreCase)
            || rest.Equals(".log.old", StringComparison.OrdinalIgnoreCase)
            || (rest.StartsWith(".log.old.", StringComparison.OrdinalIgnoreCase) && rest.Length > 9 && IsDigits(rest.Substring(9)));
        if (!known) return null;
        return int.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int pid) ? pid : null;
    }

    /// <summary>
    /// The startup pruner's rule for one file matching RynthCore.*.log*: delete it when it is older
    /// than <paramref name="cutoff"/>, is not the shared log, is not one of this client's own files
    /// (<paramref name="selfLogName"/> and its copies), and does not belong to a client that is still
    /// running (<paramref name="isLive"/>; a running client's oldest copies can pass 7 days on a long
    /// soak). A name with no PID in it is judged on age alone, as before.
    /// </summary>
    public static bool ShouldPrune(string fileName, string selfLogName, string sharedLogName,
                                   DateTime lastWrite, DateTime cutoff, Func<int, bool> isLive)
    {
        if (fileName.Equals(sharedLogName, StringComparison.OrdinalIgnoreCase)) return false;
        if (fileName.Equals(selfLogName, StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith(selfLogName + ".", StringComparison.OrdinalIgnoreCase)) return false;
        if (lastWrite >= cutoff) return false;
        int? pid = PidOf(fileName);
        if (pid is int p && isLive(p)) return false;
        return true;
    }

    private static bool IsDigits(string s)
    {
        if (s.Length == 0) return false;
        foreach (char c in s)
            if (c < '0' || c > '9') return false;
        return true;
    }
}
