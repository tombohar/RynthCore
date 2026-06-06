using System;
using System.Runtime.InteropServices;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// Enumerates AC's authoritative world-object table via CObjectMaint.
///
/// Primary use: seed PluginManager._liveObjects on hot-reload, since the new
/// engine module's static state starts empty after a reload. Walking the
/// maintainer gives an immediate snapshot of every weenie the client knows
/// about so we can replay create-object events to a freshly loaded plugin
/// without waiting for new spawn/visibility events.
///
/// Secondary use: reconciliation / drift detection — even on cold start, if
/// any CreateObject hook fires miss the live-object set the maintainer is
/// authoritative and catches the omission.
///
/// Singleton: CObjectMaint::s_pcInstance @ 0x00842ADC (CObjectMaint**)
///
/// CObjectMaint layout (from Chorizite acclient.map + struct defs):
///   +0x00  Interface a0           (vtable*, 4)
///   +0x04  NoticeHandler a1       (vtable*, 4)
///   +0x08  Turbine_RefCount       (vtable + m_cRef, 8)
///   +0x10  is_active              (Int32, 4)
///   +0x14  lost_cell_table        (IntrusiveHashTable, 112)
///   +0x84  object_table           (LongHash&lt;CPhysicsObj&gt;, 24)
///   +0x9C  null_object_table      (LongHash&lt;CPhysicsObj&gt;, 24)
///   +0xB4  weenie_object_table    (LongHash&lt;CWeenieObject&gt;, 24)
///
/// HashBase&lt;UInt32&gt; layout (LongHash wraps this):
///   +0   vfptr
///   +4   table_mask
///   +8   key_shift
///   +12  buckets (HashBaseData&lt;UInt32&gt;**)
///   +16  table_size
///   +20  fPlacementNew_
///
/// HashBaseData&lt;UInt32&gt; layout:
///   +0   vfptr
///   +4   hash_next
///   +8   id (UInt32)
/// </summary>
internal static class CObjectMaintHooks
{
    private const int S_PC_INSTANCE_VA           = 0x00842ADC;
    private const int WEENIE_OBJECT_TABLE_OFFSET = 0xB4;
    // DIAGNOSTIC (temporary): the CPhysicsObj table — superset of physics
    // objects, including ones whose weenie isn't registered in
    // weenie_object_table yet (the suspected missing-monster set).
    private const int OBJECT_TABLE_OFFSET        = 0x84;
    private const int HASHBASE_BUCKETS_OFFSET    = 12;
    private const int HASHBASE_TABLE_SIZE_OFFSET = 16;
    private const int HASHBASEDATA_HASH_NEXT_OFFSET = 4;
    private const int HASHBASEDATA_ID_OFFSET     = 8;

    // Sanity bounds — table_size larger than this almost certainly means
    // we're reading garbage (real AC tables are typically 1k-8k buckets).
    private const int MaxBuckets        = 65536;
    private const int MaxChainPerBucket = 1024;

    /// <summary>
    /// Walks CObjectMaint::weenie_object_table and invokes <paramref name="visit"/> for each
    /// known object id. Returns the number of ids visited, or -1 if the maintainer or
    /// table layout couldn't be read safely.
    /// </summary>
    public static int EnumerateLiveWeenieObjectIds(Action<uint> visit)
    {
        if (visit == null) return -1;

        IntPtr maintainer = ReadMaintainer();
        if (maintainer == IntPtr.Zero)
            return -1;

        IntPtr tablePtr = maintainer + WEENIE_OBJECT_TABLE_OFFSET;

        if (!SmartBoxLocator.IsMemoryReadable(tablePtr + HASHBASE_BUCKETS_OFFSET, 4))
            return -1;
        IntPtr buckets = Marshal.ReadIntPtr(tablePtr + HASHBASE_BUCKETS_OFFSET);
        if (buckets == IntPtr.Zero)
            return -1;

        if (!SmartBoxLocator.IsMemoryReadable(tablePtr + HASHBASE_TABLE_SIZE_OFFSET, 4))
            return -1;
        uint tableSize = (uint)Marshal.ReadInt32(tablePtr + HASHBASE_TABLE_SIZE_OFFSET);
        if (tableSize == 0 || tableSize > MaxBuckets)
            return -1;

        int count = 0;
        try
        {
            for (uint i = 0; i < tableSize; i++)
            {
                IntPtr bucketSlot = buckets + (int)(i * 4);
                if (!SmartBoxLocator.IsMemoryReadable(bucketSlot, 4))
                    break;
                IntPtr node = Marshal.ReadIntPtr(bucketSlot);

                int chainGuard = 0;
                while (node != IntPtr.Zero && chainGuard++ < MaxChainPerBucket)
                {
                    if (!SmartBoxLocator.IsMemoryReadable(node + HASHBASEDATA_ID_OFFSET, 4))
                        break;
                    uint id = (uint)Marshal.ReadInt32(node + HASHBASEDATA_ID_OFFSET);
                    if (id != 0)
                    {
                        visit(id);
                        count++;
                    }
                    if (!SmartBoxLocator.IsMemoryReadable(node + HASHBASEDATA_HASH_NEXT_OFFSET, 4))
                        break;
                    node = Marshal.ReadIntPtr(node + HASHBASEDATA_HASH_NEXT_OFFSET);
                }
            }
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"CObjectMaint enumerate threw {ex.GetType().Name}: {ex.Message}");
            return count > 0 ? count : -1;
        }

        return count;
    }

    // DIAGNOSTIC (temporary): same walk as EnumerateLiveWeenieObjectIds but over
    // an arbitrary CObjectMaint table (object_table +0x84 / null_object_table
    // +0x9C / weenie_object_table +0xB4). Duplicated rather than refactoring the
    // tested enumerator. Remove with the diagnostic.
    public static int EnumerateTableIds(int tableOffset, Action<uint> visit)
    {
        if (visit == null) return -1;

        IntPtr maintainer = ReadMaintainer();
        if (maintainer == IntPtr.Zero)
            return -1;

        IntPtr tablePtr = maintainer + tableOffset;

        if (!SmartBoxLocator.IsMemoryReadable(tablePtr + HASHBASE_BUCKETS_OFFSET, 4))
            return -1;
        IntPtr buckets = Marshal.ReadIntPtr(tablePtr + HASHBASE_BUCKETS_OFFSET);
        if (buckets == IntPtr.Zero)
            return -1;

        if (!SmartBoxLocator.IsMemoryReadable(tablePtr + HASHBASE_TABLE_SIZE_OFFSET, 4))
            return -1;
        uint tableSize = (uint)Marshal.ReadInt32(tablePtr + HASHBASE_TABLE_SIZE_OFFSET);
        if (tableSize == 0 || tableSize > MaxBuckets)
            return -1;

        int count = 0;
        try
        {
            for (uint i = 0; i < tableSize; i++)
            {
                IntPtr bucketSlot = buckets + (int)(i * 4);
                if (!SmartBoxLocator.IsMemoryReadable(bucketSlot, 4))
                    break;
                IntPtr node = Marshal.ReadIntPtr(bucketSlot);

                int chainGuard = 0;
                while (node != IntPtr.Zero && chainGuard++ < MaxChainPerBucket)
                {
                    if (!SmartBoxLocator.IsMemoryReadable(node + HASHBASEDATA_ID_OFFSET, 4))
                        break;
                    uint id = (uint)Marshal.ReadInt32(node + HASHBASEDATA_ID_OFFSET);
                    if (id != 0)
                    {
                        visit(id);
                        count++;
                    }
                    if (!SmartBoxLocator.IsMemoryReadable(node + HASHBASEDATA_HASH_NEXT_OFFSET, 4))
                        break;
                    node = Marshal.ReadIntPtr(node + HASHBASEDATA_HASH_NEXT_OFFSET);
                }
            }
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"CObjectMaint object-table enumerate threw {ex.GetType().Name}: {ex.Message}");
            return count > 0 ? count : -1;
        }

        return count;
    }

    // Phase B: resolve s_pcInstance's address by code-xref ("mov ecx,[s_pcInstance]" =
    // 8B 0D <addr>), operand at offset 2; the VA stays as fallback. Resolved lazily once.
    private static readonly byte?[] PatXrefInstancePP = [ 0x8B, 0x0D, null, null, null, null, 0x55, 0xE8 ];
    private static int _instancePPAddr;

    private static int InstancePPAddr()
    {
        if (_instancePPAddr == 0)
        {
            _instancePPAddr = S_PC_INSTANCE_VA;
            if (AcClientModule.TryReadTextSection(out AcClientTextSection text))
                _instancePPAddr = HookResolver.ResolveData(text, "CObjectMaint.s_pcInstance", PatXrefInstancePP, 2, S_PC_INSTANCE_VA).Address.ToInt32();
        }
        return _instancePPAddr;
    }

    private static IntPtr ReadMaintainer()
    {
        IntPtr instancePP = new IntPtr(InstancePPAddr());
        if (!SmartBoxLocator.IsMemoryReadable(instancePP, 4))
            return IntPtr.Zero;
        IntPtr maintainer = Marshal.ReadIntPtr(instancePP);
        if (maintainer == IntPtr.Zero)
            return IntPtr.Zero;

        if (!SmartBoxLocator.IsMemoryReadable(maintainer, 4))
            return IntPtr.Zero;
        IntPtr vtable = Marshal.ReadIntPtr(maintainer);
        if (!SmartBoxLocator.IsPointerInModule(vtable))
            return IntPtr.Zero;

        return maintainer;
    }
}
