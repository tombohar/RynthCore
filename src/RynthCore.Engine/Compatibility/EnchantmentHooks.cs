using System;
using System.Runtime.InteropServices;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// Reads the player's active spell enchantments from the CEnchantmentRegistry.
///
/// Hookless approach: reads the registry directly from the player's CACQualities struct.
///
/// Path: PlayerVitalsHooks.KnownPlayerQualitiesPtr → CACQualities+0x70 → CEnchantmentRegistry*
/// Offset 0x70 confirmed by disassembling CACQualities::EnchantAttribute2nd (0x0058FEC0):
///   8B 49 70 = mov ecx, [ecx+0x70]  ; reads _enchantment_reg from this (CACQualities*)
///
/// CEnchantmentRegistry layout (from Chorizite + PDB):
///   +0  PackObj (vtable, 4 bytes)
///   +4  PackableList&lt;Enchantment&gt;* _mult_list
///   +8  PackableList&lt;Enchantment&gt;* _add_list
///   +12 PackableList&lt;Enchantment&gt;* _cooldown_list
///   +16 Enchantment* _vitae
///   +20 uint m_cHelpfulEnchantments
///   +24 uint m_cHarmfulEnchantments
///
/// Enchantment struct (sizeof=72, pack 4):
///   +0  PackObj (vtable)     +4  _id (uint)
///   +8  m_SpellSetID         +12 _spell_category
///   +16 _power_level         +20 _start_time (double)
///   +28 _duration (double)   +36 _caster (uint)
///   +40 _degrade_modifier    +44 _degrade_limit
///   +48 _last_time_degraded  +56 StatMod(16 bytes)
///
/// PackableLLNode&lt;Enchantment&gt;: data(72) + next*(4) + prev*(4)
/// </summary>
internal static class EnchantmentHooks
{
    // Offset of _enchantment_reg within CACQualities
    // Confirmed by disassembly: mov ecx, [ecx+0x70] at 0x0058FEC0
    private const int QualitiesRegistryOffset = 0x70;

    // CEnchantmentRegistry offsets
    private const int RegistryMultListOffset     = 4;
    private const int RegistryAddListOffset      = 8;
    private const int RegistryCooldownListOffset = 12;

    // PackableList<Enchantment>: +0=vtable, +4=head, +8=tail, +12=curNum
    private const int ListHeadOffset = 4;

    // Enchantment fields within each PackableLLNode
    // MSVC x86 pack(8): doubles are 8-byte aligned, inserting 4 bytes padding after _power_level
    private const int EnchantmentIdOffset        = 4;   // _id (uint)
    private const int EnchantmentStartTimeOffset = 24;  // _start_time (double) — padded from +20 to +24
    private const int EnchantmentDurationOffset  = 32;  // _duration (double)
    // sizeof(Enchantment) = 80 with MSVC padding; PackableLLNode has next* and prev* after data
    private const int NodeNextOffset             = 80;  // next*

    private static bool _initialized;
    private static bool _loggedFirstRead;

    public static bool IsInitialized => _initialized;

    public static bool Initialize()
    {
        if (_initialized) return true;

        _initialized = true;
        RynthLog.Verbose("Compat: enchantment reader ready (hookless, CACQualities+0x70)");
        return true;
    }

    /// <summary>
    /// Reads active enchantments from the player's CEnchantmentRegistry.
    /// Returns count written, 0 if no enchantments, -1 if player qualities not available.
    /// Expiry times are server-time seconds (subtract current server time for remaining).
    /// </summary>
    public static unsafe int ReadPlayerEnchantments(uint* spellIds, double* expiryTimes, int maxCount)
    {
        if (maxCount <= 0) return 0;

        // Get player's CACQualities pointer (set by login hooks)
        IntPtr qualPtr = PlayerVitalsHooks.KnownPlayerQualitiesPtr;
        if (qualPtr == IntPtr.Zero) return -1;

        int count = ReadEnchantmentsFromQualities(qualPtr, spellIds, expiryTimes, maxCount);

        if (_enchReadLogCount < 8)
        {
            System.Threading.Interlocked.Increment(ref _enchReadLogCount);
            RynthLog.Info($"[EnchRead] player read #{_enchReadLogCount}: {count} enchantments (qualPtr=0x{qualPtr.ToInt64():X8})");
        }

        return count;
    }
    private static int _enchReadLogCount;

    /// <summary>
    /// Reads active enchantments from any game object's CEnchantmentRegistry.
    /// Path: GetWeenieObject(objectId) → weenie+qualitiesOffset → CACQualities+0x70.
    /// Returns count written, 0 if no enchantments, -1 if object not found or has no registry.
    ///
    /// Uses VirtualQuery to validate every pointer before dereferencing — critical because
    /// AccessViolationException cannot be caught in NativeAOT/.NET 5+.
    /// </summary>
    public static unsafe int ReadObjectEnchantments(uint objectId, uint* spellIds, double* expiryTimes, int maxCount)
    {
        if (maxCount <= 0) return 0;

        if (!ClientObjectHooks.TryGetWeenieObjectPtr(objectId, out IntPtr weeniePtr))
            return -1;

        // Navigate weenie → CACQualities via the probed/fallback offset
        IntPtr qualAddr = weeniePtr + ClientObjectHooks.WeenieQualitiesOffset;
        if (!SmartBoxLocator.IsMemoryReadable(qualAddr, 4))
            return -1;
        IntPtr qualPtr = Marshal.ReadIntPtr(qualAddr);
        if (qualPtr == IntPtr.Zero) return -1;

        // Validate: CACQualities inherits PackObj → vtable must point into acclient.exe.
        if (!SmartBoxLocator.IsMemoryReadable(qualPtr, 4))
            return -1;
        IntPtr vtable = Marshal.ReadIntPtr(qualPtr);
        if (!SmartBoxLocator.IsPointerInModule(vtable))
            return -1;

        // Validate the enchantment registry pointer
        IntPtr regAddr = qualPtr + QualitiesRegistryOffset;
        if (!SmartBoxLocator.IsMemoryReadable(regAddr, 4))
            return -1;
        IntPtr regPtr = Marshal.ReadIntPtr(regAddr);
        if (regPtr == IntPtr.Zero) return 0;

        // Validate registry vtable
        if (!SmartBoxLocator.IsMemoryReadable(regPtr, 4))
            return -1;
        IntPtr regVtable = Marshal.ReadIntPtr(regPtr);
        if (!SmartBoxLocator.IsPointerInModule(regVtable))
            return -1;

        return ReadEnchantmentsFromQualities(qualPtr, spellIds, expiryTimes, maxCount);
    }

    /// <summary>
    /// Core: reads enchantments from a CACQualities pointer's enchantment registry.
    /// </summary>
    private static unsafe int ReadEnchantmentsFromQualities(IntPtr qualPtr, uint* spellIds, double* expiryTimes, int maxCount)
    {
        // While AC is inside a DB object-cache teardown (DbCacheTeardownHooks sets this
        // on AC's main thread for the duration of DestroyObjectCaches — which fires at
        // world-load, zone change, logout, AND final close), refuse to walk the
        // CEnchantmentRegistry linked lists: AC is concurrently freeing those nodes, and
        // an off-thread (plugin-pump) walk over a half-freed node is the recurring
        // 0x00416C86 (DBOCache::DestroyObj, [null+0x28]) AV.
        if (DbCacheTeardownHooks.TeardownActive)
            return -1;

        // Deep-audit finding #23 (2026-06-18): ReadObjectEnchantments (the
        // other caller of this method) validates the qualities pointer's own
        // vtable-in-module before ever reaching here; the player path used to
        // skip straight to walking the registry off a cached pointer with no
        // such check. KnownPlayerQualitiesPtr is zeroed on logout and
        // TeardownActive covers the dominant relog race, but a
        // committed-but-stale pointer surviving both (mainly a
        // Decal-coexistence exposure) would otherwise be walked as if it
        // were still a real CACQualities object — a use-after-free read.
        // Reuses the same canonical-vtable check the skill-read path already
        // relies on instead of a fresh page-probe-only check.
        if (!SmartBoxLocator.IsMemoryReadable(qualPtr, 4) || !ClientObjectHooks.IsCacQualitiesObject(qualPtr))
            return -1;

        IntPtr regAddr = qualPtr + QualitiesRegistryOffset;
        if (!SmartBoxLocator.IsMemoryReadable(regAddr, 4))
            return -1;
        IntPtr registryPtr = Marshal.ReadIntPtr(regAddr);

        if (registryPtr == IntPtr.Zero) return 0;

        // Validate the registry's own vtable too, mirroring ReadObjectEnchantments.
        if (!SmartBoxLocator.IsMemoryReadable(registryPtr, 4))
            return -1;
        IntPtr registryVtable = Marshal.ReadIntPtr(registryPtr);
        if (!SmartBoxLocator.IsPointerInModule(registryVtable))
            return -1;

        // Stability check (2026-09-02 "infinite buffing loop" bug): AC's own
        // main thread mutates these linked lists as buffs land/expire, and in
        // Decal-coexistence mode this reader runs off-thread on the 30Hz
        // plugin pump (no EndScene-driven main-thread tick exists there) — a
        // walk here can race that mutation. A torn/truncated walk silently
        // under-reports the active buff set; BuffManager.RefreshFromLiveMemory
        // treats a short read as "buffs expired" and clears its timers,
        // making a just-completed rebuff pass look like nothing landed and
        // instantly restarting it. Walk twice and only trust an exact match;
        // on a mismatch, skip this tick (return -1, already a safe no-op for
        // every caller) rather than report a truncated set.
        int countA = WalkAllLists(registryPtr, spellIds, expiryTimes, maxCount);

        uint* idsB = stackalloc uint[maxCount];
        double* expB = stackalloc double[maxCount];
        int countB = WalkAllLists(registryPtr, idsB, expB, maxCount);

        if (countA != countB)
            return -1;
        for (int i = 0; i < countA; i++)
        {
            if (spellIds[i] != idsB[i] || expiryTimes[i] != expB[i])
                return -1;
        }
        return countA;
    }

    private static unsafe int WalkAllLists(IntPtr registryPtr, uint* spellIds, double* expiryTimes, int maxCount)
    {
        int count = 0;
        count = WalkEnchantList(registryPtr + RegistryMultListOffset,     spellIds, expiryTimes, maxCount, count);
        count = WalkEnchantList(registryPtr + RegistryAddListOffset,      spellIds, expiryTimes, maxCount, count);
        count = WalkEnchantList(registryPtr + RegistryCooldownListOffset, spellIds, expiryTimes, maxCount, count);
        return count;
    }

    private static unsafe int WalkEnchantList(IntPtr listPtrAddress, uint* spellIds, double* expiryTimes, int maxCount, int count)
    {
        if (!SmartBoxLocator.IsMemoryReadable(listPtrAddress, 4))
            return count;
        IntPtr listPtr = Marshal.ReadIntPtr(listPtrAddress);
        if (listPtr == IntPtr.Zero) return count;

        if (!SmartBoxLocator.IsMemoryReadable(listPtr + ListHeadOffset, 4))
            return count;
        IntPtr nodePtr = Marshal.ReadIntPtr(listPtr + ListHeadOffset);

        int guard = 0;
        while (nodePtr != IntPtr.Zero && guard++ < 512 && count < maxCount)
        {
            // Validate entire node is readable before accessing any field
            if (!SmartBoxLocator.IsMemoryReadable(nodePtr, NodeNextOffset + 4))
                break;

            // _id field is enchantment ID: (layer << 16) | spellId — mask to get spell ID
            uint spellId    = unchecked((uint)Marshal.ReadInt32(nodePtr + EnchantmentIdOffset)) & 0xFFFF;
            long startBits  = Marshal.ReadInt64(nodePtr + EnchantmentStartTimeOffset);
            long durBits    = Marshal.ReadInt64(nodePtr + EnchantmentDurationOffset);
            double start    = BitConverter.Int64BitsToDouble(startBits);
            double duration = BitConverter.Int64BitsToDouble(durBits);

            if (spellId != 0)
            {
                spellIds[count]    = spellId;
                expiryTimes[count] = duration > 0 ? start + duration : double.MaxValue;
                count++;
            }

            nodePtr = Marshal.ReadIntPtr(nodePtr + NodeNextOffset);
        }

        return count;
    }
}
