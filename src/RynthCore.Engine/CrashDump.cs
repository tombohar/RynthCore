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
                DumpTypePreferred, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            int err = ok ? 0 : Marshal.GetLastWin32Error();

            // ERROR_INVALID_PARAMETER (87) ⇒ AC's ancient dbghelp 6.1 rejected a flag.
            // Retry with bare MiniDumpNormal (universally supported, still has every
            // thread stack — what we need to read the deadlock's lock holder).
            if (!ok && err == 87)
            {
                fs.SetLength(0);
                ok = MiniDumpWriteDump(
                    GetCurrentProcess(), GetCurrentProcessId(), fs.SafeFileHandle,
                    DumpTypeFallback, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                err = ok ? 0 : Marshal.GetLastWin32Error();
                if (ok) RynthLog.Error($"CrashDump: rich flags rejected by AC's old dbghelp 6.1 — wrote bare Normal dump, reason={reason}");
            }

            if (!ok)
            {
                RynthLog.Error($"CrashDump: MiniDumpWriteDump failed err={err} reason={reason}");
                // Don't leave a 0-byte .dmp behind — it reads as a crash marker
                // during triage (observed: hang_17692_20260611_114430.dmp, 0
                // bytes, main thread suspended mid-exception-dispatch).
                try { fs.Close(); File.Delete(dumpPath); } catch { }
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
