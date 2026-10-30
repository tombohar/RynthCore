using System;
using System.Collections.Generic;

namespace RynthCore.App;

/// <summary>
/// The launcher never kills a client while a crash dump of it is being written (2026-10-15).
/// A CoreCLR fatal error runs createdump.exe (native\Loader\Loader.c, ConfigureCrashDumps) and
/// Windows Error Reporting runs WerFault.exe, each naming the client's pid on its command line.
/// Either suspends the client while it writes, so the log goes quiet and the window stops
/// answering: to the stuck-client reaper and the wedge watch that is a dead client, and a kill
/// then cuts the dump short (a clr_*.dmp on 2026-10-13 had its header and an all-zero stream
/// directory). So both kills hold while a writer names the client, and for <see cref="Grace"/>
/// after the writer is gone (a dying client normally exits on its own by then); never more than
/// <see cref="MaxHold"/> from the first hold, so a writer that never finishes can't shield a dead
/// client forever. Pure: the launcher finds the processes (CrashDumpWriters.cs in the app).
/// Checked in tools\LauncherSettingsTests.
/// </summary>
internal static class CrashDumpWriterRule
{
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MaxHold = TimeSpan.FromMinutes(15);

    /// <summary>Process names (Process.ProcessName, no ".exe") of the dump writers.</summary>
    public static readonly string[] WriterNames = ["createdump", "WerFault"];

    public enum Hold
    {
        /// <summary>No dump being written: the kill checks run.</summary>
        None,
        /// <summary>A dump is being written, or was within Grace: hold the kill.</summary>
        Holding,
        /// <summary>Held for MaxHold already: the kill checks run.</summary>
        Expired,
    }

    /// <summary>
    /// <paramref name="writerRunning"/>: a writer names the client now; <paramref name="lastSeenUtc"/>:
    /// when one last did (null = never); <paramref name="holdSinceUtc"/>: when the launcher first
    /// held this client (null = not held yet). The launcher keeps both while the result is Holding
    /// or Expired and forgets them on None, so an expired hold does not start again.
    /// </summary>
    public static Hold Decide(bool writerRunning, DateTime? lastSeenUtc, DateTime? holdSinceUtc, DateTime nowUtc)
    {
        bool signs = writerRunning || (lastSeenUtc is DateTime seen && nowUtc - seen < Grace);
        if (!signs) return Hold.None;
        if (holdSinceUtc is DateTime since && nowUtc - since >= MaxHold) return Hold.Expired;
        return Hold.Holding;
    }

    /// <summary>
    /// The pid a dump writer is dumping, from its command line, or null (not a writer, no command
    /// line, or no pid on it). createdump takes the pid as its positional argument
    /// ("createdump [options] pid"; numeric option values such as --crashthread are skipped);
    /// WerFault takes it after -p ("WerFault.exe -u -p 1234 -s 568").
    /// </summary>
    public static int? TargetPid(string? processName, string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        string name = (processName ?? "").Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        List<string> args = SplitArgs(commandLine);
        if (args.Count < 2) return null;   // [0] is the program

        if (name.Equals("createdump", StringComparison.OrdinalIgnoreCase))
        {
            for (int i = 1; i < args.Count; i++)
            {
                string a = args[i];
                if (a.StartsWith('-'))
                {
                    if (CreatedumpValueOptions.Contains(a)) i++;   // skip its value
                    continue;
                }
                if (TryPid(a, out int pid)) return pid;
            }
            return null;
        }
        if (name.Equals("WerFault", StringComparison.OrdinalIgnoreCase))
        {
            for (int i = 1; i + 1 < args.Count; i++)
                if ((args[i].Equals("-p", StringComparison.OrdinalIgnoreCase) || args[i].Equals("/p", StringComparison.OrdinalIgnoreCase))
                    && TryPid(args[i + 1], out int pid))
                    return pid;
            return null;
        }
        return null;
    }

    // createdump options that take a value (debug/createdump/main.cpp); the rest are flags.
    private static readonly HashSet<string> CreatedumpValueOptions = new(StringComparer.Ordinal)
    {
        "-f", "--name", "-l", "--logtofile", "--crashthread", "--signal", "--exceptionrecord",
    };

    private static bool TryPid(string s, out int pid)
    {
        pid = 0;
        if (s.Length == 0 || s.Length > 10) return false;
        foreach (char c in s) if (c < '0' || c > '9') return false;
        return int.TryParse(s, out pid) && pid > 0;
    }

    // Whitespace-separated, double quotes group (enough for these two programs' arguments).
    private static List<string> SplitArgs(string commandLine)
    {
        var args = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool quoted = false, any = false;
        foreach (char c in commandLine)
        {
            if (c == '"') { quoted = !quoted; any = true; continue; }
            if (!quoted && char.IsWhiteSpace(c))
            {
                if (any) { args.Add(cur.ToString()); cur.Clear(); any = false; }
                continue;
            }
            cur.Append(c);
            any = true;
        }
        if (any) args.Add(cur.ToString());
        return args;
    }
}
