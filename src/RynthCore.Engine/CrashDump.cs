// ============================================================================
//  RynthCore.Engine - CrashDump.cs
//  Writes a minidump of THIS process via dbghelp!MiniDumpWriteDump. Used by
//  MainThreadHangWatchdog to capture a real post-mortem the moment AC's main
//  thread is confirmed permanently wedged — a targeted replacement for
//  always-on procdump (which also dumped on every clean exit and every handled
//  first-chance AV, so a .dmp's existence never meant a real crash).
//
//  Also writes ONE full-memory dump when AC shows its OUT OF MEMORY box
//  (MessageBoxHooks): that box is modal, so the process never crashes and Windows
//  never writes a dump; the launcher later kills the client as wedged
//  (AutoRestartWedgedClients, kept as it is). The hang dump is a small type and
//  holds no heap, which is what an out-of-memory needs.
//
//  Safe to call from a background thread: MiniDumpWriteDump suspends all other
//  threads internally while it snapshots them, which is exactly what we want for
//  a hang. The dbghelp.dll P/Invoke is lazy-bound (no cost unless we ever fire).
// ============================================================================

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace RynthCore.Engine;

internal static class CrashDump
{
    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(
        IntPtr hProcess,
        uint processId,
        SafeHandle hFile,
        uint dumpType,
        IntPtr exceptionParam,
        IntPtr userStreamParam,
        IntPtr callbackParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceExW(string directory, out ulong freeAvailable, out ulong total, out ulong totalFree);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    // MINIDUMP_TYPE. ⚠ AC ships an ANCIENT dbghelp.dll 6.1 (2003) in its own
    // directory; it's already loaded by base name, so [DllImport("dbghelp.dll")]
    // binds to THAT, not the modern OS v10. dbghelp 6.1 predates
    // MiniDumpWithThreadInfo (0x1000, added in 6.5) and REJECTS it with
    // ERROR_INVALID_PARAMETER (87) — which is why every hang dump failed
    // (diagnosed 2026-06-10). Use only flags 6.1 supports: MiniDumpNormal already
    // carries ALL thread stacks (enough to read a deadlock's lock holder);
    // WithHandleData (0x4, since 5.1) is kept. On any failure we retry bare Normal.
    private const uint DumpTypePreferred = 0x00000000 | 0x00000004; // Normal + WithHandleData
    private const uint DumpTypeFallback  = 0x00000000;              // Normal only (universally supported)

    // MiniDumpWithFullMemory (0x2) is as old as MiniDumpNormal, so dbghelp 6.1 takes it.
    private const uint DumpTypeFull         = 0x00000002 | 0x00000004; // FullMemory + WithHandleData
    private const uint DumpTypeFullFallback = 0x00000002;              // FullMemory only

    // dbghelp is single-threaded: every MiniDumpWriteDump in this process goes through this
    // lock, so the hang watchdog's small dump can never run alongside the OOM full dump.
    private static readonly object DumpLock = new();

    /// <summary>
    /// Write a minidump of the current process to Logs\dumps\. Best-effort;
    /// never throws. Returns true on success and sets <paramref name="dumpPath"/>.
    /// </summary>
    internal static bool WriteSelfDump(string reason, out string dumpPath, string prefix = "hang")
    {
        dumpPath = string.Empty;
        try
        {
            string dir = Path.Combine(LogPaths.LogDirectory, "dumps");
            Directory.CreateDirectory(dir);

            // The small dump is tens of MB; with the volume nearly full it would only fail halfway.
            long free = FreeBytes(dir);
            if (!DumpBudget.SmallDumpAllowed(free))
            {
                RynthLog.Error($"CrashDump: not writing the {prefix} dump - {(free < 0 ? "free space unknown" : (free / (1024 * 1024)) + " MB free")} on the dump volume, reason={reason}");
                return false;
            }

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            dumpPath = Path.Combine(dir, $"{prefix}_{Environment.ProcessId}_{stamp}.dmp");
            return WriteDumpFile(dumpPath, DumpTypePreferred, DumpTypeFallback, reason);
        }
        catch (Exception ex)
        {
            RynthLog.Error($"CrashDump: exception {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Free bytes available to this user on the volume holding <paramref name="dir"/>; -1 when unknown.</summary>
    internal static long FreeBytes(string dir)
    {
        try
        {
            if (GetDiskFreeSpaceExW(dir, out ulong avail, out _, out _))
                return avail > long.MaxValue ? long.MaxValue : (long)avail;
        }
        catch { }
        return -1;
    }

    /// <summary>Crash dumps (<c>crash_&lt;pid&gt;_*.dmp</c>) kept in Logs\dumps.</summary>
    internal const int CrashDumpsKept = 5;

    /// <summary>
    /// On a crash exit (ProcessExitHooks): one small dump of this process unless CoreCLR's
    /// createdump already wrote <c>clr_&lt;pid&gt;_*.dmp</c> for it. Never throws.
    /// </summary>
    internal static void WriteCrashDumpOnce(string via, uint code)
    {
        try
        {
            string dir = Path.Combine(LogPaths.LogDirectory, "dumps");
            try
            {
                if (Directory.Exists(dir) && Directory.GetFiles(dir, $"clr_{Environment.ProcessId}_*.dmp").Length > 0)
                    return;   // the CLR's own crash dump is there: a second one adds nothing
            }
            catch { }
            if (WriteSelfDump($"{via} 0x{code:X8}", out string path, "crash"))
            {
                RynthLog.Info($"ProcessExit: crash dump written {path} ({via} 0x{code:X8})");
                PruneDumps(dir, "crash_", CrashDumpsKept);
            }
        }
        catch { }
    }

    // Writes one dump to <paramref name="path"/>. On ERROR_INVALID_PARAMETER (87: AC's ancient
    // dbghelp 6.1 rejected a flag) retries with <paramref name="fallbackType"/>. Never leaves a
    // 0-byte or partial .dmp behind (it reads as a crash marker during triage; observed:
    // hang_17692_20260611_114430.dmp, 0 bytes, main thread suspended mid-exception-dispatch).
    private static bool WriteDumpFile(string path, uint type, uint fallbackType, string reason)
    {
        lock (DumpLock)
        {
            bool ok;
            int err;
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                ok = MiniDumpWriteDump(GetCurrentProcess(), GetCurrentProcessId(), fs.SafeFileHandle,
                    type, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                err = ok ? 0 : Marshal.GetLastWin32Error();
                if (!ok && err == 87 && fallbackType != type)
                {
                    fs.SetLength(0);
                    ok = MiniDumpWriteDump(GetCurrentProcess(), GetCurrentProcessId(), fs.SafeFileHandle,
                        fallbackType, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    err = ok ? 0 : Marshal.GetLastWin32Error();
                    if (ok) RynthLog.Error($"CrashDump: flags 0x{type:X} rejected by AC's old dbghelp 6.1 - wrote type 0x{fallbackType:X}, reason={reason}");
                }
            }

            if (!ok)
            {
                RynthLog.Error($"CrashDump: MiniDumpWriteDump failed err={err} type=0x{type:X} reason={reason}");
                try { File.Delete(path); } catch { }
                return false;
            }

            long size = 0;
            try { size = new FileInfo(path).Length; } catch { }
            RynthLog.Error($"CrashDump: wrote {path} ({size / 1024}KB) reason={reason}");
            return true;
        }
    }

    // -- OUT OF MEMORY full-memory dump --------------------------------------

    /// <summary>Full dumps kept in the Dumps folder (a full dump is about the size of the process).</summary>
    internal const int OutOfMemoryDumpsKept = 3;
    internal const string OutOfMemoryDumpPrefix = "acclient_oom_";

    // Longest the showing thread waits for the dump writer before letting the box open anyway.
    private const int FullDumpWaitMs = 300_000;

    private static int _oomDumpLatch;
    private static volatile bool _fullDumpInProgress;
    private static volatile string? _fullDumpWrittenPath;

    /// <summary>True while the OUT OF MEMORY full dump is being written (the watchdog holds off).</summary>
    internal static bool FullDumpInProgress => _fullDumpInProgress;

    /// <summary>The OUT OF MEMORY full dump written this session, or null.</summary>
    internal static string? FullDumpWrittenPath => _fullDumpWrittenPath;

    /// <summary>C:\Games\RynthCore\Dumps by default (beside Logs, under the install folder).</summary>
    internal static string OutOfMemoryDumpDirectory => Path.Combine(RynthCore.Install.RynthInstallPaths.CoreDir, "Dumps");

    /// <summary>
    /// Once per process: write a full-memory dump to <see cref="OutOfMemoryDumpDirectory"/>, log
    /// its path, and keep only the newest <see cref="OutOfMemoryDumpsKept"/> of these dumps.
    /// Called on the thread about to show AC's OUT OF MEMORY box, which waits here until the
    /// dump is written (on its own thread, so the showing thread's stack is captured at rest).
    /// Never throws.
    /// </summary>
    internal static void WriteOutOfMemoryDumpOnce(string? reason)
    {
        if (Interlocked.Exchange(ref _oomDumpLatch, 1) != 0) return;
        string why = "AC OUT OF MEMORY box: " + (reason ?? "?");
        _fullDumpInProgress = true;
        // The launcher reads this marker (DumpInProgressRule): MiniDumpWriteDump suspends every
        // thread, so the log goes silent for the whole dump, which would otherwise read as a dead
        // engine and get the client killed mid-dump.
        try { RynthLog.Error($"{DumpMarkerStarted} pid={Environment.ProcessId} - AC OUT OF MEMORY box; writing one full-memory dump before it shows."); }
        catch { }
        Thread? writer = null;
        try
        {
            writer = new Thread(() =>
            {
                // The flag stays up until the writer itself is done, even past a timed-out Join.
                try { WriteOutOfMemoryDumpCore(why); }
                finally { FinishFullDump(); }
            }) { IsBackground = true, Name = "RynthCore.OomDump" };
            writer.Start();
        }
        catch (Exception ex)
        {
            // No thread to be had (out of memory, after all): write it from this one.
            try { RynthLog.Error($"CrashDump: dump thread failed to start ({ex.GetType().Name}) - writing on the calling thread."); } catch { }
            writer = null;
        }
        try
        {
            if (writer == null)
            {
                try { WriteOutOfMemoryDumpCore(why); }
                finally { FinishFullDump(); }
            }
            else if (!writer.Join(FullDumpWaitMs))
            {
                RynthLog.Error($"CrashDump: full dump still writing after {FullDumpWaitMs / 1000}s - letting the box open (the writer keeps going).");
            }
        }
        catch (Exception ex)
        {
            try { RynthLog.Error($"CrashDump: OOM dump failed {ex.GetType().Name}: {ex.Message}"); } catch { }
        }
    }

    /// <summary>Log line written before the OUT OF MEMORY full dump starts (read by the launcher).</summary>
    internal const string DumpMarkerStarted = RynthCore.Install.DumpInProgressRule.MarkerStarted;
    /// <summary>Log line written once the writer has finished, whatever the outcome.</summary>
    internal const string DumpMarkerEnded = RynthCore.Install.DumpInProgressRule.MarkerEnded;

    private static void FinishFullDump()
    {
        _fullDumpInProgress = false;
        try { RynthLog.Error($"{DumpMarkerEnded} pid={Environment.ProcessId}."); } catch { }
    }

    private static void WriteOutOfMemoryDumpCore(string reason)
    {
        try
        {
            string dir = OutOfMemoryDumpDirectory;
            Directory.CreateDirectory(dir);

            string path = Path.Combine(dir, $"{OutOfMemoryDumpPrefix}{Environment.ProcessId}_{DateTime.Now:yyyyMMdd_HHmmss}.dmp");
            if (!ReserveFullDump(dir, path, out string refusal))
            {
                RynthLog.Error($"CrashDump: OUT OF MEMORY full-memory dump skipped ({refusal}); writing the small dump instead.");
                WriteSelfDump(reason, out _, "oom-small");
                return;
            }
            if (WriteDumpFile(path, DumpTypeFull, DumpTypeFullFallback, reason))
            {
                _fullDumpWrittenPath = path;
                RynthLog.Error($"CrashDump: OUT OF MEMORY full-memory dump: {path}");
                // Only after the new dump succeeded: a failed write never costs an older one.
                PruneDumps(dir, OutOfMemoryDumpPrefix, OutOfMemoryDumpsKept);
            }
            else
            {
                RynthLog.Error("CrashDump: OUT OF MEMORY full-memory dump FAILED; writing the small hang-type dump instead.");
                WriteSelfDump(reason, out _, "oom-small");
            }
        }
        catch (Exception ex)
        {
            RynthLog.Error($"CrashDump: OOM dump writer failed {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Every client runs this check-then-create under one machine-wide (per logon session) mutex,
    // and the empty placeholder it creates counts against the next client's check, so clients that
    // hit OUT OF MEMORY together cannot all pass the check before any of them has started writing.
    private const string FullDumpGateName = @"Local\RynthCore.FullDumpGate";
    private const int FullDumpGateWaitMs = 10_000;

    /// <summary>
    /// Whether a full dump may be written to <paramref name="path"/> now (<see cref="DumpBudget"/>:
    /// free space on the volume, and the number and bytes of full dumps already in the folder from
    /// ALL clients). On a refusal it first prunes the folder down to
    /// <c>OutOfMemoryDumpsKept - 1</c> (dumps of dead clients and our own older ones) and asks again.
    /// On success an empty placeholder exists at <paramref name="path"/>.
    /// </summary>
    private static bool ReserveFullDump(string dir, string path, out string refusal)
    {
        refusal = string.Empty;
        Mutex? gate = null;
        bool held = false;
        try
        {
            try
            {
                gate = new Mutex(false, FullDumpGateName);
                held = gate.WaitOne(FullDumpGateWaitMs);
            }
            catch (AbandonedMutexException) { held = true; }   // a client died holding it: ours now
            catch { /* no mutex available: decide without it */ }

            long estimate = DumpBudget.EstimateFullDumpBytes(PrivateBytes());
            DumpVerdict verdict = EvaluateFolder(dir, estimate, out long free, out int count, out long bytes);
            if (verdict != DumpVerdict.Full)
            {
                // Older dumps are the thing most likely to be in the way: make room for this one.
                PruneDumps(dir, OutOfMemoryDumpPrefix, Math.Max(0, OutOfMemoryDumpsKept - 1));
                verdict = EvaluateFolder(dir, estimate, out free, out count, out bytes);
            }
            if (verdict != DumpVerdict.Full)
            {
                refusal = $"{verdict}: {(free < 0 ? "free space unknown" : (free / (1024 * 1024)) + " MB free")}, dump needs ~{estimate / (1024 * 1024)} MB, " +
                          $"{count} full dump(s) / {bytes / (1024 * 1024)} MB already in {dir}";
                return false;
            }
            try
            {
                using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite)) { }
            }
            catch (Exception ex)
            {
                refusal = $"cannot create {path}: {ex.GetType().Name}";
                return false;
            }
            return true;
        }
        finally
        {
            if (gate != null)
            {
                if (held) { try { gate.ReleaseMutex(); } catch { } }
                gate.Dispose();
            }
        }
    }

    private static DumpVerdict EvaluateFolder(string dir, long estimate, out long free, out int count, out long bytes)
    {
        free = FreeBytes(dir);
        count = 0;
        bytes = 0;
        try
        {
            foreach (FileInfo f in new DirectoryInfo(dir).GetFiles(OutOfMemoryDumpPrefix + "*.dmp"))
            {
                count++;
                try { bytes += f.Length; } catch { }
            }
        }
        catch { }
        return DumpBudget.Evaluate(free, estimate, count, bytes);
    }

    private static long PrivateBytes()
    {
        try
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            return p.PrivateMemorySize64;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Deletes all but the newest <paramref name="keep"/> <paramref name="prefix"/>*.dmp files in
    /// <paramref name="dir"/>. The folder is shared by every client: a dump whose name carries
    /// the pid of ANOTHER process that is still running is never deleted (it may still be
    /// writing it, or about to report it), and is not counted against <paramref name="keep"/>.
    /// Never throws.
    /// </summary>
    internal static int PruneDumps(string dir, string prefix, int keep)
    {
        int deleted = 0;
        try
        {
            FileInfo[] files = new DirectoryInfo(dir).GetFiles(prefix + "*.dmp");
            Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            int kept = 0;
            int self = Environment.ProcessId;
            foreach (FileInfo f in files)
            {
                int pid = DumpPid(f.Name, prefix);
                if (pid > 0 && pid != self && IsProcessAlive(pid))
                    continue;   // another live client's dump: not ours to delete
                if (kept < Math.Max(0, keep)) { kept++; continue; }
                string name = f.Name;
                long length = 0;
                try { length = f.Length; } catch { }
                try
                {
                    f.Delete();
                    deleted++;
                    RynthLog.Info($"CrashDump: pruned old dump {name} ({length / (1024 * 1024)}MB).");
                }
                catch { /* in use by a debugger, or already gone */ }
            }
        }
        catch { }
        return deleted;
    }

    /// <summary>The pid in "prefix&lt;pid&gt;_...dmp", or 0.</summary>
    internal static int DumpPid(string fileName, string prefix)
    {
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return 0;
        int start = prefix.Length, end = start;
        while (end < fileName.Length && char.IsDigit(fileName[end])) end++;
        return end > start && int.TryParse(fileName.AsSpan(start, end - start), out int pid) ? pid : 0;
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
