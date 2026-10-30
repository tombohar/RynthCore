// ============================================================================
//  RynthCore.Engine - Compatibility/MemoryUse.cs
//
//  Where the memory goes while the engine and its plugins start (a client start or a
//  hot reload). 2026-10-15: a reload frees the old engine (~550 MB of address space
//  back) and the new one takes ~400 MB again within two seconds of the plugins starting,
//  all of it freshly touched, so the working set jumps from ~480 MB to ~1.1 GB. The log
//  had no per-plugin numbers, so nobody could say what takes it.
//
//  Mark(label) logs one "[Mem]" line with private bytes (committed), the .NET heap
//  (all managed plugins share the one runtime), the working set and free address
//  space, each with its change since the last mark. StartBurst samples every 100 ms for
//  a while and logs every rise of BurstStepMb or more in private bytes with the last
//  mark's label, so work a plugin does after its Init returns (a background load, its
//  first ticks) is still caught. Never throws; any thread.
// ============================================================================

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace RynthCore.Engine.Compatibility;

internal static class MemoryUse
{
    /// <summary>A burst logs each rise in private bytes of at least this much.</summary>
    private const long BurstStepMb = 16;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS_EX
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, out PROCESS_MEMORY_COUNTERS_EX counters, uint size);

    /// <summary>One reading, in MB (-1 when a value couldn't be read).</summary>
    internal readonly record struct Reading(long PrivateMb, long WorkingSetMb, long GcHeapMb, long GcCommittedMb, long VaFreeMb);

    private static readonly object Gate = new();
    private static Reading _lastMark;
    private static bool _haveMark;
    private static volatile string _label = "start";
    private static int _burstRunning;

    public static Reading Read()
    {
        long priv = -1, ws = -1, gcHeap = -1, gcCommit = -1;
        try
        {
            var c = new PROCESS_MEMORY_COUNTERS_EX { cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX>() };
            if (GetProcessMemoryInfo(GetCurrentProcess(), out c, c.cb))
            {
                priv = (long)((ulong)c.PrivateUsage >> 20);
                ws = (long)((ulong)c.WorkingSetSize >> 20);
            }
        }
        catch { }
        try
        {
            GCMemoryInfo gc = GC.GetGCMemoryInfo();
            gcHeap = gc.HeapSizeBytes >> 20;
            gcCommit = gc.TotalCommittedBytes >> 20;
        }
        catch { }
        long va = AddressSpace.FreeAndLargestMb().FreeMb;
        return new Reading(priv, ws, gcHeap, gcCommit, va);
    }

    /// <summary>Logs a reading with its change since the last mark, and names what runs next.</summary>
    public static void Mark(string label)
    {
        try
        {
            Reading now = Read();
            Reading prev;
            bool had;
            lock (Gate)
            {
                prev = _lastMark;
                had = _haveMark;
                _lastMark = now;
                _haveMark = true;
                _label = label;
            }
            RynthLog.Info($"[Mem] {label}: {Format(now, had ? prev : (Reading?)null)}");
        }
        catch { }
    }

    /// <summary>
    /// Samples every 100 ms for <paramref name="seconds"/> and logs each rise in private
    /// bytes of BurstStepMb or more, with the label of the last <see cref="Mark"/>. One
    /// burst at a time.
    /// </summary>
    public static void StartBurst(int seconds, string why)
    {
        if (Interlocked.Exchange(ref _burstRunning, 1) == 1) return;
        try
        {
            var t = new Thread(() =>
            {
                try
                {
                    Reading start = Read();
                    Reading last = start;
                    RynthLog.Info($"[Mem] burst for {seconds} s ({why}): {Format(start, null)}");
                    long end = Environment.TickCount64 + seconds * 1000L;
                    while (Environment.TickCount64 < end)
                    {
                        Thread.Sleep(100);
                        Reading r = Read();
                        if (r.PrivateMb >= 0 && last.PrivateMb >= 0 && r.PrivateMb - last.PrivateMb >= BurstStepMb)
                        {
                            RynthLog.Info($"[Mem] +{r.PrivateMb - last.PrivateMb} MB private during '{_label}': {Format(r, last)}");
                            last = r;
                        }
                    }
                    Reading done = Read();
                    RynthLog.Info($"[Mem] burst done ({why}): {Format(done, start)} (change over the whole burst)");
                }
                catch { }
                finally { Interlocked.Exchange(ref _burstRunning, 0); }
            })
            { IsBackground = true, Name = "RynthCore.MemoryUse" };
            t.Start();
        }
        catch { Interlocked.Exchange(ref _burstRunning, 0); }
    }

    private static string Format(Reading r, Reading? prev)
    {
        static string D(long now, long? before) => before is long b && b >= 0 && now >= 0 ? $" ({now - b:+0;-0})" : "";
        return $"private {r.PrivateMb} MB{D(r.PrivateMb, prev?.PrivateMb)}, .NET heap {r.GcHeapMb} MB{D(r.GcHeapMb, prev?.GcHeapMb)}" +
               $" (committed {r.GcCommittedMb}{D(r.GcCommittedMb, prev?.GcCommittedMb)}), working set {r.WorkingSetMb} MB{D(r.WorkingSetMb, prev?.WorkingSetMb)}," +
               $" free address space {r.VaFreeMb} MB{D(r.VaFreeMb, prev?.VaFreeMb)}";
    }
}
