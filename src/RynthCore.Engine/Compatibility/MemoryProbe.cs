using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// "Can I read (or write) this AC memory without an access violation?" for NativeAOT, where an
/// AV cannot be caught. Every IsReadablePointer / IsReadableSpan / IsMemoryReadable /
/// IsWritablePointer in the engine answers through here.
///
/// WHY NOT JUST VirtualQuery (what they all used until 2026-10-02): in a 32-bit process on
/// 64-bit Windows, VirtualQuery's cost grows with the size of the committed region AFTER the
/// address asked about - the kernel walks the page tables to work out RegionSize. Measured on
/// the i7-4770K (tools/FloodProbeBench): 10 us on a small heap block, 0.35 ms 16 MB from the end
/// of a committed run, 2.6 ms at 64 MB, 11 ms at 256 MB, 19 ms at 512 MB. AC's heap reaches
/// 1.0-1.4 GB in a crowded dungeon, and the main-thread snapshot walks probed every hash bucket
/// and 3-4 spots of every object 10 times a second (identity/attackable walks 2 more times),
/// so a big heap turned each walk into tens to hundreds of milliseconds on AC's thread: the
/// 3-10 fps collapse in Matron Hive South (Lucy, 2026-10-02), still there after logout because
/// the heap stays committed.
///
/// HOW: QueryWorkingSetEx answers per PAGE (about 2 us for one call, under 0.1 us per extra
/// page in the same call) no matter how big the region is. A page that is resident ("Valid")
/// is committed, and the call returns its protection, so the old rule is checked exactly:
/// committed, not NOACCESS, not GUARD, plus readable/writable where the caller asked. Only when
/// a page is NOT resident (trimmed to the standby list, never touched, reserved, free) does it
/// fall back to the old VirtualQuery test, which then decides exactly as before. A span is
/// accepted when every page in it passes (VirtualQuery additionally insisted on one region;
/// two adjacent readable regions read just as safely).
///
/// Thread-safe and allocation-free (stackalloc). Counters are for the [SnapshotDiag] line.
/// The snapshot walks open a <see cref="BeginWalk"/> scope so each page is queried once per
/// walk rather than once per probe.
/// </summary>
internal static unsafe class MemoryProbe
{
    private const uint MEM_COMMIT = 0x1000;
    private const uint PAGE_NOACCESS = 0x01;
    private const uint PAGE_GUARD = 0x100;

    /// <summary>Any page protection that can be read (READONLY, READWRITE, WRITECOPY, EXECUTE_READ, ...).</summary>
    internal const uint ReadableMask = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80;
    /// <summary>Any page protection that can be written (READWRITE, WRITECOPY, EXECUTE_READWRITE, EXECUTE_WRITECOPY).</summary>
    internal const uint WritableMask = 0x04 | 0x08 | 0x40 | 0x80;

    private const int PageShift = 12;
    private const long PageSize = 1L << PageShift;
    /// <summary>Spans longer than this many pages go straight to VirtualQuery. 80 pages = 320 KB covers the
    /// largest CObjectMaint bucket array the walk accepts (65536 buckets x 4 bytes = 64 pages).</summary>
    private const int MaxFastPages = 80;

    private static readonly IntPtr CurrentProcess = new(-1);

    // 1 = QueryWorkingSetEx unavailable or failing: every probe uses VirtualQuery (old behaviour).
    private static int _fastDisabled;

    private static long _fastPages;
    private static long _slowCalls;
    private static long _slowTicks;

    [StructLayout(LayoutKind.Sequential)]
    private struct WorkingSetExInfo
    {
        public IntPtr VirtualAddress;
        public UIntPtr Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", EntryPoint = "K32QueryWorkingSetEx")]
    private static extern int QueryWorkingSetEx(IntPtr process, WorkingSetExInfo* info, uint size);

    [DllImport("kernel32.dll")]
    private static extern int VirtualQuery(IntPtr address, MemoryBasicInformation* info, int length);

    /// <summary>Committed, not NOACCESS/GUARD (the old IsReadablePointer / IsReadableSpan rule).</summary>
    public static bool IsAccessible(IntPtr ptr, int length = 1) => Check(ptr, length, 0);

    /// <summary>Committed, not NOACCESS/GUARD, and a readable protection (the old SmartBoxLocator rule).</summary>
    public static bool IsReadable(IntPtr ptr, int length = 1) => Check(ptr, length, ReadableMask);

    /// <summary>Committed, not NOACCESS/GUARD, and a writable protection (the old IsWritablePointer rule).</summary>
    public static bool IsWritable(IntPtr ptr, int length = 1) => Check(ptr, length, WritableMask);

    /// <summary>
    /// Probe counters since the last call: pages answered by QueryWorkingSetEx, and the
    /// VirtualQuery fallbacks with their total time. Resets them.
    /// </summary>
    public static (long FastPages, long SlowCalls, double SlowMs) TakeStats()
    {
        long fast = Interlocked.Exchange(ref _fastPages, 0);
        long slow = Interlocked.Exchange(ref _slowCalls, 0);
        long ticks = Interlocked.Exchange(ref _slowTicks, 0);
        return (fast, slow, ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
    }

    /// <summary>True while the QueryWorkingSetEx path is in use (false = every probe is a VirtualQuery).</summary>
    public static bool FastPathActive => Volatile.Read(ref _fastDisabled) == 0;

    // ── Walk scope: remember resident pages for the length of one snapshot walk ──────────
    // A walk over every object probes 3-10 spots per object, almost all on one or two pages.
    // Inside a scope (AC's main thread, inside one walk, AC itself not running) a page found
    // resident keeps its answer until the scope ends, so a page costs one query per walk
    // instead of one per probe. Only the thread that opened the scope uses the cache; a probe
    // from any other thread asks the system as usual. Plain statics with an owner thread, not
    // [ThreadStatic]: the engine can live in a collectible context (CoreCLR host).
    private const int CacheSlots = 1024;
    private static readonly uint[] _cachePage = new uint[CacheSlots];   // page number + 1 (0 = empty slot)
    private static readonly ushort[] _cacheProt = new ushort[CacheSlots]; // that page's Win32Protection
    private static int _scopeOwner;  // managed thread id that holds the scope (0 = none)
    private static int _scopeDepth;  // nesting on the owner thread

    /// <summary>
    /// Opens a page cache for one main-thread snapshot walk (dispose to close; nests). Only
    /// for code that reads AC memory while AC is not running (the EndScene / UseTime walks).
    /// A second thread asking while another holds the scope just gets no cache.
    /// </summary>
    public static WalkScope BeginWalk()
    {
        int me = Environment.CurrentManagedThreadId;
        if (Volatile.Read(ref _scopeOwner) == me)
        {
            _scopeDepth++;
            return new WalkScope(true);
        }
        if (Interlocked.CompareExchange(ref _scopeOwner, me, 0) != 0)
            return new WalkScope(false);
        _scopeDepth = 1;
        Array.Clear(_cachePage);
        return new WalkScope(true);
    }

    internal readonly struct WalkScope : IDisposable
    {
        private readonly bool _open;
        public WalkScope(bool open) => _open = open;
        public void Dispose()
        {
            if (!_open || Volatile.Read(ref _scopeOwner) != Environment.CurrentManagedThreadId)
                return;
            if (--_scopeDepth <= 0)
            {
                _scopeDepth = 0;
                Volatile.Write(ref _scopeOwner, 0);
            }
        }
    }

    private static bool InScope => _scopeDepth > 0 && Volatile.Read(ref _scopeOwner) == Environment.CurrentManagedThreadId;

    // Protection of a resident page from the walk cache, or 0 when not cached.
    private static uint CachedProtection(ulong page)
    {
        int slot = (int)(page & (CacheSlots - 1));
        return _cachePage[slot] == (uint)page + 1 ? _cacheProt[slot] : 0u;
    }

    private static void RememberProtection(ulong page, uint protect)
    {
        int slot = (int)(page & (CacheSlots - 1));
        _cachePage[slot] = (uint)page + 1;
        _cacheProt[slot] = (ushort)protect;
    }

    private static int Judge(uint protect, uint requiredMask)
    {
        if ((protect & (PAGE_NOACCESS | PAGE_GUARD)) != 0)
            return 0;
        if (requiredMask != 0 && (protect & requiredMask) == 0)
            return 0;
        return 1;
    }

    private static bool Check(IntPtr ptr, int length, uint requiredMask)
    {
        if (ptr == IntPtr.Zero || length <= 0)
            return false;

        if (Volatile.Read(ref _fastDisabled) == 0)
        {
            int verdict = TryFast(ptr, length, requiredMask);
            if (verdict >= 0)
                return verdict == 1;
        }
        return Slow(ptr, length, requiredMask);
    }

    // 1 = every page resident and acceptable, 0 = a resident page is refused,
    // -1 = can't tell here (a page is not resident, span too long, or the call failed).
    private static int TryFast(IntPtr ptr, int length, uint requiredMask)
    {
        ulong start = unchecked((ulong)(nuint)ptr);
        ulong last = start + (ulong)(length - 1);
        if (last > uint.MaxValue)
            return 0; // runs past the 32-bit address space: never readable
        ulong firstPage = start >> PageShift;
        ulong lastPage = last >> PageShift;
        ulong pageCount = lastPage - firstPage + 1;
        if (pageCount > MaxFastPages)
            return -1;

        int n = (int)pageCount;

        // Inside a walk scope: answer from the pages already seen in this walk.
        bool inScope = InScope;
        if (inScope)
        {
            bool allCached = true;
            for (int i = 0; i < n && allCached; i++)
            {
                uint cached = CachedProtection(firstPage + (ulong)i);
                if (cached == 0) allCached = false;
                else if (Judge(cached, requiredMask) == 0) return 0;
            }
            if (allCached)
                return 1;
        }

        WorkingSetExInfo* info = stackalloc WorkingSetExInfo[n];
        for (int i = 0; i < n; i++)
        {
            info[i].VirtualAddress = (IntPtr)(nint)(long)((firstPage + (ulong)i) << PageShift);
            info[i].Flags = UIntPtr.Zero;
        }

        int ok;
        try
        {
            ok = QueryWorkingSetEx(CurrentProcess, info, (uint)(n * sizeof(WorkingSetExInfo)));
        }
        catch
        {
            // Entry point missing (pre-Windows 7 kernel32) or the call itself failed: VirtualQuery from now on.
            Interlocked.Exchange(ref _fastDisabled, 1);
            return -1;
        }
        if (ok == 0)
            return -1;

        Interlocked.Add(ref _fastPages, n);
        for (int i = 0; i < n; i++)
        {
            ulong flags = (ulong)(nuint)info[i].Flags;
            if ((flags & 1) == 0)
                return -1; // not resident: committed-but-trimmed, untouched, reserved or free - ask VirtualQuery
            uint protect = (uint)((flags >> 4) & 0x7FF); // PSAPI_WORKING_SET_EX_BLOCK.Win32Protection
            if (protect == 0)
                return -1; // resident but no protection reported: let VirtualQuery decide
            if (inScope) RememberProtection(firstPage + (ulong)i, protect);
            if (Judge(protect, requiredMask) == 0)
                return 0;
        }
        return 1;
    }

    // The pre-2026-10-02 rule, unchanged: one VirtualQuery, the span inside that one region.
    private static bool Slow(IntPtr ptr, int length, uint requiredMask)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            MemoryBasicInformation mbi;
            if (VirtualQuery(ptr, &mbi, sizeof(MemoryBasicInformation)) == 0)
                return false;
            if (mbi.State != MEM_COMMIT)
                return false;
            if ((mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) != 0)
                return false;
            if (requiredMask != 0 && (mbi.Protect & requiredMask) == 0)
                return false;
            if (length <= 1)
                return true;
            ulong regionEnd = unchecked((ulong)(nuint)mbi.BaseAddress + (ulong)(nuint)mbi.RegionSize);
            ulong spanEnd = unchecked((ulong)(nuint)ptr + (ulong)length);
            return spanEnd <= regionEnd;
        }
        finally
        {
            Interlocked.Increment(ref _slowCalls);
            Interlocked.Add(ref _slowTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
        }
    }
}
