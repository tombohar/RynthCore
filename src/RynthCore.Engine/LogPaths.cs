// ============================================================================
//  RynthCore.Engine - LogPaths.cs
//  Log file layout under C:\Games\RynthCore\Logs\:
//
//    RynthCore.log              SHARED launch/orchestration log. Written by the
//                               injector and launcher (separate processes), plus
//                               a one-line session pointer from each engine at
//                               startup. This is the human entry point / index.
//    RynthCore.<pid>.log        PER-CLIENT session log. Written by the loader,
//                               engine, and plugins inside one acclient.exe
//                               (they all share that client's PID). One file per
//                               running client.
//    RynthCore.<pid>.log.old    Its newest rotated copy (5 MB); older ones are
//    RynthCore.<pid>.log.old.N  .old.1 (next newest) ... .old.7 (LogRotation.cs;
//                               engine.json "LogKeepCopies", default 8 copies).
//
//  Why per-client: a daily multi-boxer runs N acclient.exe at once. With one
//  shared file their lines interleave, the "scan hb #N for a gap" silent-death
//  check is defeated (other clients' heartbeats fill every gap), and startup
//  rotation from one client can truncate the log out from under the others.
//  Per-PID files give each client an independent, gap-detectable session log
//  with no cross-process rotation race.
// ============================================================================

using System;
using System.IO;
using RynthCore.Install;

namespace RynthCore.Engine;

internal static class LogPaths
{
    /// <summary>Unified log directory for all RynthCore components.</summary>
    internal static string LogDirectory => RynthCore.Install.RynthInstallPaths.CoreLogsDir; // C:\Games\RynthCore\Logs by default

    /// <summary>Shared launch/orchestration log (injector + launcher + per-engine session pointer).</summary>
    internal const string SharedLogFileName = "RynthCore.log";

    /// <summary>Rotate the active per-client log when it grows beyond this size at engine startup.</summary>
    internal const long MaxLogBytes = 5L * 1024 * 1024;

    /// <summary>Delete per-client logs older than this during startup prune.</summary>
    private static readonly TimeSpan MaxLogAge = TimeSpan.FromDays(7);

    /// <summary>Per-client session log filename, keyed on this process's PID.</summary>
    internal static string LogFileName => $"RynthCore.{Environment.ProcessId}.log";

    /// <summary>Full path to this client's per-PID session log (loader + engine + plugins).</summary>
    internal static string LogFilePath => Path.Combine(LogDirectory, LogFileName);

    /// <summary>Newest rotated copy of this client's session log (older ones: .old.1, .old.2, ... - LogRotation).</summary>
    internal static string RotatedLogFilePath => Path.Combine(LogDirectory, LogFileName + ".old");

    /// <summary>Full path to the shared launch/orchestration log.</summary>
    internal static string SharedLogFilePath => Path.Combine(LogDirectory, SharedLogFileName);

    // Per-client status log files (Logs\status\RynthCore.<pid>.status.json) are written by the private
    // RynthRemote plugin; the engine only knows the directory + filename so the startup log-pruner below
    // can clean up stale files left by crashed clients. The engine writes no status file and never networks.

    /// <summary>Directory for per-client status JSON files (Logs\status).</summary>
    internal static string StatusDirectory => Path.Combine(LogDirectory, "status");

    /// <summary>This process's per-PID status filename (so the pruner skips our own live file).</summary>
    internal static string StatusFileName => $"RynthCore.{Environment.ProcessId}.status.json";

    /// <summary>
    /// Best-effort: create the log directory. Safe to call repeatedly. Never throws.
    /// </summary>
    internal static void EnsureLogDirectory()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
        }
        catch
        {
            // No fallback — if C:\Games\RynthCore\Logs can't be created the
            // sink will silently swallow writes. We deliberately do NOT fall
            // back to Desktop because that scatters logs across machines.
        }
    }

    /// <summary>
    /// Engine-startup rotation: if THIS client's per-PID log exceeds
    /// <see cref="MaxLogBytes"/>, move it to RynthCore.&lt;pid&gt;.log.old, after shifting the
    /// older copies to .old.1 ... .old.&lt;N-1&gt; (<see cref="LogRotation"/>; N = engine.json
    /// "LogKeepCopies", default 8). With per-PID files this only ever touches the current
    /// client's own files, so there is no cross-process rotation race (the bug the old
    /// shared-file design had). Rotation still matters because Windows reuses PIDs across launches.
    /// </summary>
    internal static void RotateAtStartup()
    {
        try
        {
            EnsureLogDirectory();
            string active = LogFilePath;
            if (!File.Exists(active)) return;

            long size;
            try { size = new FileInfo(active).Length; }
            catch { return; }

            if (size < MaxLogBytes) return;

            EntryPoint.ReleaseLogFile();   // the engine's open handle would follow the file to .old
            LogRotation.Rotate(active, LogSettings.KeepCopies);
        }
        catch
        {
        }
    }

    /// <summary>The shared native crash log every client appends to (see <see cref="RotateNativeCrashLogAtStartup"/>).</summary>
    internal const string NativeCrashLogName = "native-crash.log";

    /// <summary>A native-crash.log larger than this is moved aside when a client starts.</summary>
    internal const long NativeCrashLogRotateBytes = 1L * 1024 * 1024;

    /// <summary>
    /// Engine-startup rotation of the SHARED native-crash.log (one fixed name for every client,
    /// so <see cref="RotateAtStartup"/>'s per-PID rule does not cover it). Over
    /// <see cref="NativeCrashLogRotateBytes"/> it moves to native-crash.log.old (replacing the
    /// previous one). Must run BEFORE the native logger opens the file. A client that already has
    /// it open (they open it share-delete) keeps writing to the moved file, which the logger's
    /// own byte budget keeps bounded; nothing is lost, it just continues in the .old file.
    /// Best-effort; never throws. Returns true when it moved the file.
    /// </summary>
    internal static bool RotateNativeCrashLogAtStartup()
    {
        try
        {
            string active = Path.Combine(LogDirectory, NativeCrashLogName);
            if (!File.Exists(active)) return false;
            if (new FileInfo(active).Length < NativeCrashLogRotateBytes) return false;
            string rotated = active + ".old";
            try { if (File.Exists(rotated)) File.Delete(rotated); } catch { }
            File.Move(active, rotated);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Startup housekeeping: delete per-client session logs (and their .old / .old.N
    /// rotations) older than <see cref="MaxLogAge"/>. Each client launch produces a
    /// new RynthCore.&lt;pid&gt;.log, so without this the directory grows without bound.
    /// Never touches the shared RynthCore.log, the current client's own files, or the files
    /// of a client that is still running (<see cref="LogRotation.ShouldPrune"/>).
    /// Best-effort; never throws.
    /// </summary>
    internal static void PruneOldLogs()
    {
        try
        {
            EnsureLogDirectory();
            string self = LogFileName;
            DateTime cutoff = DateTime.Now - MaxLogAge;
            var live = new System.Collections.Generic.Dictionary<int, bool>();
            bool IsLive(int pid)
            {
                if (!live.TryGetValue(pid, out bool alive)) live[pid] = alive = IsPidAlive(pid);
                return alive;
            }
            foreach (string path in Directory.GetFiles(LogDirectory, "RynthCore.*.log*"))
            {
                string name = Path.GetFileName(path);
                try
                {
                    if (LogRotation.ShouldPrune(name, self, SharedLogFileName, File.GetLastWriteTime(path), cutoff, IsLive))
                        File.Delete(path);
                }
                catch { /* locked by a live client or already gone — skip */ }
            }

            // Stale status files: a crashed client leaves its RynthCore.<pid>.status.json behind. The
            // StatusAgent deletes dead-PID files itself, but prune here too so they never accumulate if
            // the agent isn't running. Never touches our own current file.
            string statusDir = StatusDirectory;
            if (Directory.Exists(statusDir))
            {
                string selfStatus = StatusFileName;
                foreach (string sf in Directory.GetFiles(statusDir, "RynthCore.*.status.json"))
                {
                    if (Path.GetFileName(sf).Equals(selfStatus, StringComparison.OrdinalIgnoreCase)) continue;
                    try { if (File.GetLastWriteTime(sf) < cutoff) File.Delete(sf); }
                    catch { /* locked by a live client or already gone — skip */ }
                }
            }

            // Dump retention: hang/crash minidumps under Logs\dumps otherwise
            // accumulate forever, and a failed write attempt leaves a 0-byte
            // .dmp that reads as a crash marker during triage.
            string dumps = Path.Combine(LogDirectory, "dumps");
            if (Directory.Exists(dumps))
            {
                foreach (string dmp in Directory.GetFiles(dumps, "*.dmp"))
                {
                    try
                    {
                        var fi = new FileInfo(dmp);
                        if (fi.Length == 0 || fi.LastWriteTime < cutoff)
                            fi.Delete();
                    }
                    catch { }
                }
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// Mid-session rotation — same size-gated rotate as startup, safe to call
    /// while the client runs (the writer opens per-append; on a held handle it
    /// falls back to truncate). Called from the heartbeat loop ~once/minute:
    /// startup-only rotation never ran on a 10h+ soak and was skipped entirely
    /// on hot-reload (initCount > 1), so the per-PID log grew without bound.
    /// </summary>
    internal static void RotateIfOversized() => RotateAtStartup();

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// Whether a process with this PID is running (Win32, like PluginLoader.IsPidAliveWin32:
    /// Process.GetProcessById is avoided in acclient). Errs towards "alive": access denied means
    /// the process exists, so its logs are kept. A PID reused by another program only delays a prune.
    /// </summary>
    private static bool IsPidAlive(int pid)
    {
        if (pid <= 0) return false;
        if (pid == Environment.ProcessId) return true;
        IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero)
            return System.Runtime.InteropServices.Marshal.GetLastWin32Error() == 5;   // ERROR_ACCESS_DENIED: it exists
        try
        {
            return !GetExitCodeProcess(h, out uint code) || code == 259 /* STILL_ACTIVE */;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>
    /// Append a single best-effort line to the SHARED RynthCore.log so it acts as
    /// an index of client sessions ("which PID, which build, which file"). Keeps
    /// "start at RynthCore.log" discoverability after the per-PID split.
    /// </summary>
    internal static void WriteSessionPointer(string line)
    {
        try
        {
            EnsureLogDirectory();
            using var fs = new FileStream(SharedLogFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(line + "\r\n");
            fs.Write(bytes, 0, bytes.Length);
        }
        catch
        {
        }
    }
}
