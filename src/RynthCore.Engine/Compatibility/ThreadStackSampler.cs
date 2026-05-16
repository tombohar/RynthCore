// ============================================================================
//  RynthCore.Engine - Compatibility/ThreadStackSampler.cs
//
//  Cross-thread stack sampler. Once per heartbeat tick, walks every other
//  thread in the process via Win32 SuspendThread + GetThreadContext +
//  EBP-chain unwinding. For each thread, records how deep its stack currently
//  is (number of return addresses landing inside our engine module). If any
//  thread crosses the runaway-depth threshold, dumps the full chain of
//  engine-RVA return addresses to the log.
//
//  This catches recursion that the per-detour RecursionGuard misses — namely,
//  recursion contained entirely between two engine-internal methods that
//  never re-enter any of our hook detours.
//
//  We don't symbolicate inline; the dumped RVAs can be cross-referenced to
//  the PDB after the fact.
// ============================================================================

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace RynthCore.Engine.Compatibility;

internal static class ThreadStackSampler
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwThreadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SuspendThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadContext(IntPtr hThread, IntPtr lpContext);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetModuleHandleExW(uint dwFlags, IntPtr lpModuleName, out IntPtr phModule);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetModuleInformation(IntPtr hProcess, IntPtr hModule, out MODULEINFO lpmodinfo, uint cb);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [StructLayout(LayoutKind.Sequential)]
    private struct MODULEINFO
    {
        public IntPtr lpBaseOfDll;
        public uint SizeOfImage;
        public IntPtr EntryPoint;
    }

    private const uint THREAD_SUSPEND_RESUME      = 0x0002;
    private const uint THREAD_GET_CONTEXT         = 0x0008;
    private const uint THREAD_QUERY_INFORMATION   = 0x0040;
    private const uint THREAD_QUERY_LIMITED_INFO  = 0x0800;

    private const uint CONTEXT_CONTROL  = 0x00010001;
    private const uint CONTEXT_INTEGER  = 0x00010002;

    // x86 CONTEXT layout: 716 bytes. Field offsets verified against winnt.h.
    private const int CTX_SIZE = 716;
    private const int CTX_OFF_EBP = 180;
    private const int CTX_OFF_EIP = 184;
    private const int CTX_OFF_ESP = 196;
    private const int CTX_OFF_FLAGS = 0;

    /// <summary>Engine module base + end, set once at first sample.</summary>
    private static IntPtr _engineBase;
    private static IntPtr _engineEnd;

    /// <summary>Threshold for "this thread is deep in recursion": number of
    /// distinct return addresses that land in our engine module.</summary>
    private const int DepthAlarm = 50;

    private static int _samplesEmittedTotal;
    private const int MaxSamplesEmitted = 5;

    public static void SampleAll()
    {
        if (_samplesEmittedTotal >= MaxSamplesEmitted) return;

        if (_engineBase == IntPtr.Zero)
            ResolveEngineRange();
        if (_engineBase == IntPtr.Zero) return;

        uint myTid = GetCurrentThreadId();
        IntPtr ctxBuf = Marshal.AllocHGlobal(CTX_SIZE);

        try
        {
            ProcessThread[] threads;
            try { threads = new ProcessThread[Process.GetCurrentProcess().Threads.Count]; Process.GetCurrentProcess().Threads.CopyTo(threads, 0); }
            catch { return; }

            foreach (ProcessThread t in threads)
            {
                uint tid = (uint)t.Id;
                if (tid == myTid) continue;

                IntPtr h = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_QUERY_LIMITED_INFO, false, tid);
                if (h == IntPtr.Zero) continue;

                try
                {
                    if (SuspendThread(h) == 0xFFFFFFFF) continue;

                    Marshal.WriteInt32(ctxBuf, CTX_OFF_FLAGS, (int)(CONTEXT_CONTROL | CONTEXT_INTEGER));
                    if (!GetThreadContext(h, ctxBuf)) continue;

                    uint eip = (uint)Marshal.ReadInt32(ctxBuf, CTX_OFF_EIP);
                    uint ebp = (uint)Marshal.ReadInt32(ctxBuf, CTX_OFF_EBP);
                    uint esp = (uint)Marshal.ReadInt32(ctxBuf, CTX_OFF_ESP);

                    int engineHits = WalkAndCount(ebp, esp, out string captured);
                    if (engineHits >= DepthAlarm)
                    {
                        if (Interlocked.Increment(ref _samplesEmittedTotal) > MaxSamplesEmitted) return;
                        RynthLog.Info("================================================================");
                        RynthLog.Info($"==== DEEP STACK in tid 0x{tid:X} ({tid}) name='{t.Id}' eip=0x{eip:X8} ebp=0x{ebp:X8} esp=0x{esp:X8} engineHits={engineHits} ====");
                        RynthLog.Info(captured);
                        RynthLog.Info("================================================================");
                    }
                }
                finally
                {
                    ResumeThread(h);
                    CloseHandle(h);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ctxBuf);
        }
    }

    /// <summary>Walk EBP frame chain and count return addresses inside engine.
    /// Builds a trace string (only filled if depth crosses alarm).</summary>
    private static int WalkAndCount(uint ebp, uint esp, out string trace)
    {
        var sb = new System.Text.StringBuilder();
        int hits = 0;
        uint baseAddr = (uint)_engineBase.ToInt32();
        uint endAddr  = (uint)_engineEnd.ToInt32();

        // 1) Walk EBP chain
        uint cur = ebp;
        for (int i = 0; i < 1024; i++)
        {
            if (cur == 0 || (cur & 3) != 0) break;
            uint nextEbp, ret;
            try
            {
                nextEbp = (uint)Marshal.ReadInt32((IntPtr)cur);
                ret     = (uint)Marshal.ReadInt32((IntPtr)cur, 4);
            }
            catch { break; }

            if (ret >= baseAddr && ret < endAddr)
            {
                hits++;
                if (hits <= 60)
                    sb.AppendLine($"  ebp[{i:D3}] ret=0x{ret:X8}  engine+0x{(ret - baseAddr):X}");
            }

            if (nextEbp <= cur || nextEbp - cur > 0x100000) break;
            cur = nextEbp;
        }

        // 2) ESP scan as fallback (catches FPO frames)
        if (hits < 5)
        {
            uint scanFrom = esp;
            for (int i = 0; i < 4096; i++)
            {
                uint addr = scanFrom + (uint)(i * 4);
                uint val;
                try { val = (uint)Marshal.ReadInt32((IntPtr)addr); }
                catch { break; }
                if (val >= baseAddr && val < endAddr)
                {
                    hits++;
                    if (hits <= 60)
                        sb.AppendLine($"  esp+0x{i * 4:X4}  ret=0x{val:X8}  engine+0x{(val - baseAddr):X}");
                }
            }
        }

        trace = sb.ToString();
        return hits;
    }

    private static void ResolveEngineRange()
    {
        try
        {
            // Resolve our own DLL by passing the address of one of our methods.
            const uint GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS = 0x4;
            const uint GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT = 0x2;

            unsafe
            {
                delegate*<void> anchor = &Anchor;
                if (!GetModuleHandleExW(
                        GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                        (IntPtr)anchor,
                        out IntPtr hModule) || hModule == IntPtr.Zero)
                {
                    return;
                }

                if (!GetModuleInformation(GetCurrentProcess(), hModule, out MODULEINFO mi, (uint)Marshal.SizeOf<MODULEINFO>()))
                    return;

                _engineBase = mi.lpBaseOfDll;
                _engineEnd  = (IntPtr)((long)mi.lpBaseOfDll + mi.SizeOfImage);
                RynthLog.Info($"ThreadStackSampler: engine module range 0x{_engineBase.ToInt32():X8}..0x{_engineEnd.ToInt32():X8}");
            }
        }
        catch (Exception ex)
        {
            RynthLog.Info($"ThreadStackSampler: ResolveEngineRange threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Anchor() { }
}
