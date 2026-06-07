// ============================================================================
//  RynthCore.Engine - CrashDump.cs
//  Writes a minidump of THIS process via dbghelp!MiniDumpWriteDump. Used by
//  MainThreadHangWatchdog to capture a real post-mortem the moment AC's main
//  thread is confirmed permanently wedged — a targeted replacement for
//  always-on procdump (which also dumped on every clean exit and every handled
//  first-chance AV, so a .dmp's existence never meant a real crash).
//
//  Safe to call from a background thread: MiniDumpWriteDump suspends all other
//  threads internally while it snapshots them, which is exactly what we want for
//  a hang. The dbghelp.dll P/Invoke is lazy-bound (no cost unless we ever fire).
// ============================================================================

using System;
using System.IO;
using System.Runtime.InteropServices;

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

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    // MINIDUMP_TYPE: Normal (all thread stacks + module list) + thread timing +
    // handle table. Deliberately NOT WithFullMemory — AC's working set is ~1 GB
    // and a hang dump only needs stacks to find the wedge.
    private const uint DumpType =
        0x00000000   // MiniDumpNormal
      | 0x00001000   // MiniDumpWithThreadInfo
      | 0x00000004;  // MiniDumpWithHandleData

    /// <summary>
    /// Write a minidump of the current process to Logs\dumps\. Best-effort;
    /// never throws. Returns true on success and sets <paramref name="dumpPath"/>.
    /// </summary>
    internal static bool WriteSelfDump(string reason, out string dumpPath)
    {
        dumpPath = string.Empty;
        try
        {
            string dir = Path.Combine(LogPaths.LogDirectory, "dumps");
            Directory.CreateDirectory(dir);

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            dumpPath = Path.Combine(dir, $"hang_{Environment.ProcessId}_{stamp}.dmp");

            using var fs = new FileStream(dumpPath, FileMode.Create, FileAccess.Write, FileShare.None);
            bool ok = MiniDumpWriteDump(
                GetCurrentProcess(), GetCurrentProcessId(), fs.SafeFileHandle,
                DumpType, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                RynthLog.Error($"CrashDump: MiniDumpWriteDump failed err={err} reason={reason}");
                return false;
            }

            long size = 0;
            try { size = new FileInfo(dumpPath).Length; } catch { }
            RynthLog.Error($"CrashDump: wrote {dumpPath} ({size / 1024}KB) reason={reason}");
            return true;
        }
        catch (Exception ex)
        {
            RynthLog.Error($"CrashDump: exception {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}
