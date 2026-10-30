using System.Diagnostics;
using System.Runtime.InteropServices;
using RynthCore.Engine.Compatibility;

namespace FloodProbeBench;

/// <summary>
/// MemoryProbe (the engine's AV-safe memory check) without AC.
///
/// Part 1 checks it answers like the old VirtualQuery helpers on every kind of page.
/// Part 2 times the probes of the engine's main-thread snapshot walks against a big committed
/// heap, like AC's in a crowded dungeon (working set 1.0-1.4 GB on 2026-10-02):
///   * the CObjectMaint weenie-table walk (EnumerateLiveWeenieObjectIds): old = one probe per
///     hash bucket plus two per chain node; new = one probe over the bucket array plus one per
///     node (id and next are adjacent);
///   * PrefetchPositions' per-object probes (the PublicWeenieDesc copy and the CPhysicsObj
///     head), 10 walks a second;
///   * PrefetchObjectIdentity's per-object probes (qualities pointer, name string, two PWD
///     ints: about 10), and PrefetchAttackable's walk, 2 walks a second each.
/// The AC calls the walks also make (GetWeenieObject, GetObjectName, ObjectIsAttackable) are
/// not modelled - they did not change. Objects are spread over the heap and the bucket array
/// sits in the middle, so the committed run after a probed address averages half the heap.
/// VirtualQuery's cost grows with that run (WOW64), MemoryProbe's does not.
/// </summary>
internal static unsafe class Program
{
    private static int _checks, _failed;

    private static int Main(string[] args)
    {
        int[] heapSizesMb = args.Length > 0 ? args.Select(int.Parse).ToArray() : new[] { 4, 16, 64 };
        Console.WriteLine($"MemoryProbe: QueryWorkingSetEx fast path {(MemoryProbe.FastPathActive ? "available" : "MISSING")}, IntPtr.Size={IntPtr.Size}");

        Correctness();
        Benchmark(heapSizesMb);

        Console.WriteLine();
        Console.WriteLine(_failed == 0 ? $"PASS ({_checks} checks)" : $"FAIL ({_failed} of {_checks} checks)");
        return _failed == 0 ? 0 : 1;
    }

    // ── Part 1: same answers as the old helpers ───────────────────────────────

    private static void Correctness()
    {
        Console.WriteLine("\n=== Correctness: MemoryProbe vs the old VirtualQuery rule ===");
        const int Page = 4096;

        IntPtr touched = Alloc(4 * Page, MemCommit | MemReserve, PageReadWrite);
        for (int i = 0; i < 4 * Page; i += Page) *((byte*)touched + i) = 1;
        Same("committed, touched (resident)", touched + 16, 1);
        Same("committed, touched, span over 3 pages", touched + 100, 3 * Page - 200);
        Expect("committed, touched: readable", MemoryProbe.IsReadable(touched, 64), true);
        Expect("committed, touched: writable", MemoryProbe.IsWritable(touched, 64), true);

        IntPtr untouched = Alloc(2 * Page, MemCommit | MemReserve, PageReadWrite);
        Same("committed, never touched (not resident)", untouched + 8, 1);
        Same("committed, never touched, span", untouched, 2 * Page);

        IntPtr reserved = Alloc(4 * Page, MemReserve, PageReadWrite);
        Same("reserved only", reserved, 1);
        Expect("reserved only: refused", MemoryProbe.IsAccessible(reserved), false);

        IntPtr mixed = Alloc(4 * Page, MemReserve, PageReadWrite);
        Alloc(Page, MemCommit, PageReadWrite, mixed);
        *(byte*)mixed = 1;
        Expect("span from a committed page into a reserved one: refused",
               MemoryProbe.IsAccessible(mixed + Page - 8, 16), false);
        Same("span from a committed page into a reserved one", mixed + Page - 8, 16);

        IntPtr freed = Alloc(Page, MemCommit | MemReserve, PageReadWrite);
        *(byte*)freed = 1;
        VirtualFree(freed, UIntPtr.Zero, MemRelease);
        Same("freed", freed, 1);
        Expect("freed: refused", MemoryProbe.IsAccessible(freed), false);

        IntPtr guard = Alloc(Page, MemCommit | MemReserve, PageReadWrite | PageGuard);
        Same("guard page", guard, 1);
        Expect("guard page: refused", MemoryProbe.IsAccessible(guard), false);

        IntPtr noAccess = Alloc(Page, MemCommit | MemReserve, PageReadWrite);
        *(byte*)noAccess = 1;
        VirtualProtect(noAccess, (UIntPtr)Page, PageNoAccess, out _);
        Same("no-access page (was resident)", noAccess, 1);
        Expect("no-access page: refused", MemoryProbe.IsAccessible(noAccess), false);

        IntPtr readOnly = Alloc(Page, MemCommit | MemReserve, PageReadWrite);
        *(byte*)readOnly = 1;
        VirtualProtect(readOnly, (UIntPtr)Page, PageReadOnly, out _);
        Expect("read-only page: readable", MemoryProbe.IsReadable(readOnly, 32), true);
        Expect("read-only page: not writable", MemoryProbe.IsWritable(readOnly, 32), false);
        Expect("read-only page: same as old writable rule", MemoryProbe.IsWritable(readOnly), LegacyWritable(readOnly));

        IntPtr code = NativeLibrary.GetExport(NativeLibrary.Load("kernel32.dll"), "VirtualQuery");
        Same("code page (kernel32!VirtualQuery)", code, 16);

        Expect("null pointer refused", MemoryProbe.IsAccessible(IntPtr.Zero), false);
        Expect("zero length refused", MemoryProbe.IsAccessible(touched, 0), false);
        Expect("top of the address space refused", MemoryProbe.IsAccessible(new IntPtr(-16), 64), false);
    }

    private static void Same(string what, IntPtr p, int len)
    {
        bool legacy = LegacyAccessible(p, len);
        bool probe = MemoryProbe.IsAccessible(p, len);
        bool legacyR = LegacyReadable(p, len);
        bool probeR = MemoryProbe.IsReadable(p, len);
        Expect($"{what}: accessible {probe} (old {legacy})", probe, legacy);
        Expect($"{what}: readable {probeR} (old {legacyR})", probeR, legacyR);
    }

    private static void Expect(string what, bool got, bool want)
    {
        _checks++;
        if (got == want) Console.WriteLine($"  [ok]   {what}");
        else { _failed++; Console.WriteLine($"  [FAIL] {what}: got {got}, want {want}"); }
    }

    // ── Part 2: the snapshot walks ────────────────────────────────────────────

    private const int TableSize = 4096;      // weenie_object_table buckets (AC: 1k-8k)
    private const int ObjectSize = 0x400;    // an ACCWeenieObject-sized block
    private const int PhysOffset = 0x80;     // weenie -> CPhysicsObj* (the probed _phys_obj offset)
    private const int PwdSize = 0x120;       // PwdLayout.Size-like copy
    private const int PhysSize = 0x200;
    private const int PhysProbe = 0xAC;      // CPhysicsObj head through m_state

    private static void Benchmark(int[] heapSizesMb)
    {
        foreach (int heapMb in heapSizesMb)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Benchmark: snapshot-walk probes, {heapMb} MB committed heap, {TableSize} buckets ===");
            long size = (long)heapMb << 20;
            IntPtr heap = Alloc((int)size, MemCommit | MemReserve, PageReadWrite);
            if (heap == IntPtr.Zero) { Console.WriteLine("  could not reserve the heap - pass smaller sizes"); _failed++; continue; }
            for (long off = 0; off < size; off += 4096) *((byte*)heap + off) = 1;

            foreach (int objects in new[] { 150, 750 })
            {
                var table = BuildTable(heap, size, objects, seed: objects);
                MemoryProbe.TakeStats();
                double oldPos = Time(() => WalkOld(table, identity: false), 1);
                double newPos = Time(() => WalkNew(table, identity: false), 20);
                double oldId = Time(() => WalkOld(table, identity: true), 1);
                double newId = Time(() => WalkNew(table, identity: true), 20);
                double oldAtk = Time(() => EnumerateOld(table, null), 1);
                double newAtk = Time(() => { using var scope = MemoryProbe.BeginWalk(); EnumerateNew(table, null); }, 20);

                // Per second on AC's main thread: positions 10x, identity 2x, attackable 2x.
                double oldPerSec = oldPos * 10 + oldId * 2 + oldAtk * 2;
                double newPerSec = newPos * 10 + newId * 2 + newAtk * 2;
                Console.WriteLine($"  {objects,4} objects:");
                Console.WriteLine($"     position walk   old {oldPos,10:0.00} ms   new {newPos,7:0.000} ms");
                Console.WriteLine($"     identity walk   old {oldId,10:0.00} ms   new {newId,7:0.000} ms");
                Console.WriteLine($"     attackable walk old {oldAtk,10:0.00} ms   new {newAtk,7:0.000} ms");
                Console.WriteLine($"     AC main thread per second: old {oldPerSec,8:0} ms   new {newPerSec,6:0.0} ms");
                var (fast, slow, slowMs) = MemoryProbe.TakeStats();
                Console.WriteLine($"     MemoryProbe: {fast} pages answered by QueryWorkingSetEx, {slow} VirtualQuery fallbacks ({slowMs:0.0} ms)");
                _checks++;
                if (oldPerSec > 50 && newPerSec * 10 > oldPerSec) { _failed++; Console.WriteLine("  [FAIL] the new walk is not at least 10x cheaper"); }
                if (slow > 0) { _failed++; Console.WriteLine("  [FAIL] resident heap pages fell back to VirtualQuery"); }
            }
            VirtualFree(heap, UIntPtr.Zero, MemRelease);
        }
    }

    private sealed class Table
    {
        public IntPtr Header;                  // HashBase: +12 buckets, +16 table_size
        public readonly Dictionary<uint, IntPtr> ById = new();
    }

    private static Table BuildTable(IntPtr heap, long heapSize, int objects, int seed)
    {
        var rng = new Random(seed);
        var t = new Table { Header = heap + 0x1000 };
        IntPtr buckets = heap + (int)(heapSize / 2);
        new Span<byte>((void*)buckets, TableSize * 4).Clear();
        *(IntPtr*)(t.Header + 12) = buckets;
        *(int*)(t.Header + 16) = TableSize;
        for (int i = 0; i < objects; i++)
        {
            uint id = 0x80000000u + (uint)(0x2000 + i * 7);
            IntPtr obj = heap + (int)(0x10000 + (rng.NextInt64(heapSize / 2 - 0x20000) & ~0xFL));
            IntPtr phys = heap + (int)(heapSize / 2 + 0x10000 + (rng.NextInt64(heapSize / 2 - 0x20000) & ~0xFL));
            *(uint*)(obj + 8) = id;
            *(IntPtr*)(obj + PhysOffset) = phys;
            IntPtr slot = buckets + (int)((id & (TableSize - 1)) * 4);
            *(IntPtr*)(obj + 4) = *(IntPtr*)slot;  // hash_next
            *(IntPtr*)slot = obj;
            t.ById[id] = obj;
        }
        return t;
    }

    private static int _sink;

    // The pre-2026-10-02 walk: SmartBoxLocator.IsMemoryReadable (VirtualQuery) per bucket and
    // twice per node, then CapturePositionForId's two IsReadableSpan probes per object; the
    // identity walk's ~10 probes per object.
    private static int EnumerateOld(Table t, Action<uint>? visit)
    {
        if (!LegacyReadable(t.Header + 12, 4)) return -1;
        IntPtr buckets = *(IntPtr*)(t.Header + 12);
        if (!LegacyReadable(t.Header + 16, 4)) return -1;
        int size = *(int*)(t.Header + 16);
        int count = 0;
        for (int i = 0; i < size; i++)
        {
            IntPtr slot = buckets + i * 4;
            if (!LegacyReadable(slot, 4)) break;
            IntPtr node = *(IntPtr*)slot;
            while (node != IntPtr.Zero)
            {
                if (!LegacyReadable(node + 8, 4)) break;
                uint id = *(uint*)(node + 8);
                visit?.Invoke(id);
                count++;
                if (!LegacyReadable(node + 4, 4)) break;
                node = *(IntPtr*)(node + 4);
            }
        }
        return count;
    }

    private static void WalkOld(Table t, bool identity)
    {
        EnumerateOld(t, id =>
        {
            IntPtr w = t.ById[id];
            if (identity)
            {
                for (int k = 0; k < 10; k++)
                    if (LegacyAccessible(w + 0x10 + k * 0x20, 4)) _sink++;
                return;
            }
            if (LegacyAccessible(w + PhysOffset + 4, PwdSize)) _sink++;
            IntPtr phys = *(IntPtr*)(w + PhysOffset);
            if (LegacyAccessible(phys, PhysProbe + 4)) _sink += *(byte*)phys;
        });
    }

    // The new walk: one probe over the bucket array, one per node, MemoryProbe everywhere, inside
    // a walk scope (each page queried once per walk), as the engine's PrefetchPositions /
    // PrefetchObjectIdentity / PrefetchAttackable.
    private static int EnumerateNew(Table t, Action<uint>? visit)
    {
        if (!MemoryProbe.IsReadable(t.Header + 12, 8)) return -1;
        IntPtr buckets = *(IntPtr*)(t.Header + 12);
        int size = *(int*)(t.Header + 16);
        if (!MemoryProbe.IsReadable(buckets, size * 4)) return -1;
        int count = 0;
        for (int i = 0; i < size; i++)
        {
            IntPtr node = *(IntPtr*)(buckets + i * 4);
            while (node != IntPtr.Zero)
            {
                if (!MemoryProbe.IsReadable(node + 4, 8)) break;
                uint id = *(uint*)(node + 8);
                visit?.Invoke(id);
                count++;
                node = *(IntPtr*)(node + 4);
            }
        }
        return count;
    }

    private static void WalkNew(Table t, bool identity)
    {
        using var scope = MemoryProbe.BeginWalk(); // as the engine's walks
        EnumerateNew(t, id =>
        {
            IntPtr w = t.ById[id];
            if (identity)
            {
                for (int k = 0; k < 10; k++)
                    if (MemoryProbe.IsAccessible(w + 0x10 + k * 0x20, 4)) _sink++;
                return;
            }
            if (MemoryProbe.IsAccessible(w + PhysOffset + 4, PwdSize)) _sink++;
            IntPtr phys = *(IntPtr*)(w + PhysOffset);
            if (MemoryProbe.IsAccessible(phys, PhysProbe + 4)) _sink += *(byte*)phys;
        });
    }

    private static double Time(Action a, int reps)
    {
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < reps; i++) a();
        return sw.Elapsed.TotalMilliseconds / reps;
    }

    // ── The old helpers, verbatim in behaviour ────────────────────────────────

    // ClientObjectHooks.IsReadablePointer / IsReadableSpan before 2026-10-02.
    private static bool LegacyAccessible(IntPtr ptr, int length)
    {
        if (ptr == IntPtr.Zero || length <= 0) return false;
        if (VirtualQuery(ptr, out var mbi, Marshal.SizeOf<Mbi>()) == 0) return false;
        if (mbi.State != MemCommit) return false;
        if ((mbi.Protect & (PageNoAccess | PageGuard)) != 0) return false;
        if (length <= 1) return true;
        ulong regionEnd = (ulong)(nuint)mbi.BaseAddress + (ulong)(nuint)mbi.RegionSize;
        return (ulong)(nuint)ptr + (ulong)length <= regionEnd;
    }

    // SmartBoxLocator.IsMemoryReadable before 2026-10-02.
    private static bool LegacyReadable(IntPtr ptr, int length)
    {
        if (ptr == IntPtr.Zero || length <= 0) return false;
        if (VirtualQuery(ptr, out var mbi, Marshal.SizeOf<Mbi>()) == 0) return false;
        if (mbi.State != MemCommit) return false;
        if ((mbi.Protect & PageNoAccess) != 0 || (mbi.Protect & PageGuard) != 0) return false;
        if ((mbi.Protect & MemoryProbe.ReadableMask) == 0) return false;
        long regionEnd = mbi.BaseAddress.ToInt64() + mbi.RegionSize.ToInt64();
        return ptr.ToInt64() + length <= regionEnd;
    }

    // ClientObjectHooks.IsWritablePointer before 2026-10-02.
    private static bool LegacyWritable(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return false;
        if (VirtualQuery(ptr, out var mbi, Marshal.SizeOf<Mbi>()) == 0) return false;
        if (mbi.State != MemCommit) return false;
        if ((mbi.Protect & (PageNoAccess | PageGuard)) != 0) return false;
        return (mbi.Protect & MemoryProbe.WritableMask) != 0;
    }

    private const uint MemCommit = 0x1000, MemReserve = 0x2000, MemRelease = 0x8000;
    private const uint PageNoAccess = 0x01, PageReadOnly = 0x02, PageReadWrite = 0x04, PageGuard = 0x100;

    private static IntPtr Alloc(int size, uint type, uint protect, IntPtr at = default)
        => VirtualAlloc(at, (UIntPtr)(uint)size, type, protect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Mbi { public IntPtr BaseAddress, AllocationBase; public uint AllocationProtect; public IntPtr RegionSize; public uint State, Protect, Type; }

    [DllImport("kernel32.dll")] private static extern int VirtualQuery(IntPtr a, out Mbi m, int len);
    [DllImport("kernel32.dll")] private static extern IntPtr VirtualAlloc(IntPtr a, UIntPtr size, uint type, uint protect);
    [DllImport("kernel32.dll")] private static extern bool VirtualFree(IntPtr a, UIntPtr size, uint type);
    [DllImport("kernel32.dll")] private static extern bool VirtualProtect(IntPtr a, UIntPtr size, uint protect, out uint old);
}
