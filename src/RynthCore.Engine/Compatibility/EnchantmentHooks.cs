using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

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

        // Off AC's main thread (plugin pump: RynthAi BuffManager, meta expressions,
        // RynthLua): copy the main-thread snapshot. Walking the registry's linked
        // lists here raced AC adding/removing/expiring buffs on its own thread (a
        // freed node on a still-committed page passes every probe), which the
        // double walk only detected, never prevented.
        int count = MainThreadGuard.IsOnMainThread()
            ? ReadEnchantmentsFromQualities(qualPtr, spellIds, expiryTimes, maxCount)
            : CopyPlayerSnapshot(qualPtr, spellIds, expiryTimes, maxCount);

        if (_enchReadLogCount < 8)
        {
            System.Threading.Interlocked.Increment(ref _enchReadLogCount);
            RynthLog.Info($"[EnchRead] player read #{_enchReadLogCount}: {count} enchantments (qualPtr=0x{qualPtr.ToInt64():X8})");
        }

        return count;
    }
    private static int _enchReadLogCount;

    // ── Main-thread enchantment snapshots (2026-09-30 main-thread audit) ─────────
    // Player: MainThreadSnapshots.Tick walks the player's registry every 250 ms (at
    // once after an enchantment game event marks it dirty) into preallocated
    // back buffers, then swaps them in under _playerEnchLock. Zero-alloc.
    private const int MaxSnapshotEnchantments = 1024;
    private const int PlayerEnchRefreshMs = 250;
    private static readonly object _playerEnchLock = new();
    private static uint[] _pEnchIdsFront = new uint[MaxSnapshotEnchantments];
    private static double[] _pEnchExpFront = new double[MaxSnapshotEnchantments];
    private static uint[] _pEnchIdsBack = new uint[MaxSnapshotEnchantments];
    private static double[] _pEnchExpBack = new double[MaxSnapshotEnchantments];
    // The same entries' StatMod (type, key, value) and spell category, for the Skills
    // panel's per-stat buff lists (CopyPlayerStatMods). Swapped with the arrays above.
    private static uint[] _pEnchTypeFront = new uint[MaxSnapshotEnchantments];
    private static uint[] _pEnchKeyFront = new uint[MaxSnapshotEnchantments];
    private static float[] _pEnchValFront = new float[MaxSnapshotEnchantments];
    private static uint[] _pEnchCatFront = new uint[MaxSnapshotEnchantments];
    private static uint[] _pEnchTypeBack = new uint[MaxSnapshotEnchantments];
    private static uint[] _pEnchKeyBack = new uint[MaxSnapshotEnchantments];
    private static float[] _pEnchValBack = new float[MaxSnapshotEnchantments];
    private static uint[] _pEnchCatBack = new uint[MaxSnapshotEnchantments];
    private static int _pEnchCountFront = -1;          // -1 = nothing valid published
    private static IntPtr _pEnchOwnerFront;
    private static long _nextPlayerEnchRefreshMs;
    private static int _playerEnchDirty;

    /// <summary>
    /// Main thread only: an enchantment game event was just applied by AC. Refresh
    /// the player snapshot immediately (SmartBoxHooks calls this before it queues
    /// the event to plugins, so their re-read sees the change).
    /// </summary>
    internal static void RefreshPlayerSnapshotNow()
    {
        Volatile.Write(ref _playerEnchDirty, 1);
        PrefetchPlayerEnchantments(allowWalk: !LogoutLifecycleHooks.HasObservedLogout);
    }

    // Off-thread copy of the player snapshot; -1 when it is missing, belongs to
    // another qualities object, or a DB-cache teardown is running.
    private static unsafe int CopyPlayerSnapshot(IntPtr qualPtr, uint* spellIds, double* expiryTimes, int maxCount)
    {
        if (DbCacheTeardownHooks.TeardownActive)
            return -1;
        lock (_playerEnchLock)
        {
            int n = _pEnchCountFront;
            if (n < 0 || _pEnchOwnerFront != qualPtr)
                return -1;
            if (n > maxCount) n = maxCount;
            for (int i = 0; i < n; i++)
            {
                spellIds[i] = _pEnchIdsFront[i];
                expiryTimes[i] = _pEnchExpFront[i];
            }
            return n;
        }
    }

    /// <summary>
    /// The player snapshot with each entry's StatMod and spell category (the Skills
    /// panel). Fills up to the arrays' length; returns the count, or -1 when nothing
    /// valid is published (no player, another qualities object, a teardown). Any thread.
    /// </summary>
    internal static int CopyPlayerStatMods(uint[] spellIds, double[] expiryTimes, uint[] modTypes, uint[] modKeys,
        float[] modVals, uint[] categories)
    {
        if (DbCacheTeardownHooks.TeardownActive)
            return -1;
        IntPtr qualPtr = PlayerVitalsHooks.KnownPlayerQualitiesPtr;
        lock (_playerEnchLock)
        {
            int n = _pEnchCountFront;
            if (n < 0 || qualPtr == IntPtr.Zero || _pEnchOwnerFront != qualPtr)
                return -1;
            n = Math.Min(n, Math.Min(Math.Min(spellIds.Length, expiryTimes.Length), Math.Min(modTypes.Length,
                Math.Min(modKeys.Length, Math.Min(modVals.Length, categories.Length)))));
            Array.Copy(_pEnchIdsFront, spellIds, n);
            Array.Copy(_pEnchExpFront, expiryTimes, n);
            Array.Copy(_pEnchTypeFront, modTypes, n);
            Array.Copy(_pEnchKeyFront, modKeys, n);
            Array.Copy(_pEnchValFront, modVals, n);
            Array.Copy(_pEnchCatFront, categories, n);
            return n;
        }
    }

    /// <summary>
    /// Main thread only (MainThreadSnapshots.Tick). Refreshes the player snapshot
    /// every 250 ms, or on the next tick after an enchantment event. With
    /// <paramref name="allowWalk"/> false (no live player: logout window, teardown)
    /// it publishes "nothing valid" instead of walking the cached qualities pointer.
    /// </summary>
    internal static unsafe void PrefetchPlayerEnchantments(bool allowWalk)
    {
        if (!MainThreadGuard.IsOnMainThread())
            return;
        long now = Environment.TickCount64;
        bool dirty = Volatile.Read(ref _playerEnchDirty) != 0;
        if (!dirty && now < _nextPlayerEnchRefreshMs)
            return;
        Volatile.Write(ref _playerEnchDirty, 0);
        _nextPlayerEnchRefreshMs = now + PlayerEnchRefreshMs;

        IntPtr qualPtr = PlayerVitalsHooks.KnownPlayerQualitiesPtr;
        int count = -1;
        if (qualPtr != IntPtr.Zero && allowWalk)
        {
            fixed (uint* ids = _pEnchIdsBack)
            fixed (double* exp = _pEnchExpBack)
            fixed (uint* types = _pEnchTypeBack)
            fixed (uint* keys = _pEnchKeyBack)
            fixed (float* vals = _pEnchValBack)
            fixed (uint* cats = _pEnchCatBack)
                count = ReadEnchantmentsFromQualities(qualPtr, ids, exp, MaxSnapshotEnchantments, types, keys, vals, cats);
        }

        lock (_playerEnchLock)
        {
            (_pEnchIdsFront, _pEnchIdsBack) = (_pEnchIdsBack, _pEnchIdsFront);
            (_pEnchExpFront, _pEnchExpBack) = (_pEnchExpBack, _pEnchExpFront);
            (_pEnchTypeFront, _pEnchTypeBack) = (_pEnchTypeBack, _pEnchTypeFront);
            (_pEnchKeyFront, _pEnchKeyBack) = (_pEnchKeyBack, _pEnchKeyFront);
            (_pEnchValFront, _pEnchValBack) = (_pEnchValBack, _pEnchValFront);
            (_pEnchCatFront, _pEnchCatBack) = (_pEnchCatBack, _pEnchCatFront);
            _pEnchCountFront = count;
            _pEnchOwnerFront = qualPtr;
        }
    }

    // Objects: request-driven. An off-thread ReadObjectEnchantments registers the
    // id and gets -1 (every caller already treats -1 as "skip this tick") until the
    // main thread has walked it; after that it gets the last main-thread result,
    // refreshed every 500 ms while it keeps being asked for. Ids not asked for in
    // 5 s are dropped. At most 64 watched ids.
    private sealed class ObjEnchEntry
    {
        public uint[] Ids = Array.Empty<uint>();
        public double[] Exp = Array.Empty<double>();
        public int Count = -1;           // -1 = not walked yet / unreadable
        public long LastRequestMs;
    }
    private const int MaxWatchedObjects = 64;
    private const int ObjectEnchRefreshMs = 500;
    private const int ObjectEnchIdleDropMs = 5000;
    private static readonly object _objEnchLock = new();
    private static readonly Dictionary<uint, ObjEnchEntry> _objEnch = new(MaxWatchedObjects);
    private static readonly uint[] _objEnchWork = new uint[MaxWatchedObjects];
    private static readonly uint[] _objEnchScratchIds = new uint[MaxSnapshotEnchantments];
    private static readonly double[] _objEnchScratchExp = new double[MaxSnapshotEnchantments];
    private static long _nextObjectEnchRefreshMs;
    private static IntPtr _objEnchSessionOwner;

    private static unsafe int CopyObjectSnapshot(uint objectId, uint* spellIds, double* expiryTimes, int maxCount)
    {
        if (DbCacheTeardownHooks.TeardownActive)
            return -1;
        long now = Environment.TickCount64;
        lock (_objEnchLock)
        {
            if (!_objEnch.TryGetValue(objectId, out ObjEnchEntry? e))
            {
                if (_objEnch.Count >= MaxWatchedObjects)
                {
                    // Full: evict the id asked for least recently.
                    uint oldest = 0; long oldestMs = long.MaxValue;
                    foreach (KeyValuePair<uint, ObjEnchEntry> kv in _objEnch)
                        if (kv.Value.LastRequestMs < oldestMs) { oldestMs = kv.Value.LastRequestMs; oldest = kv.Key; }
                    _objEnch.Remove(oldest);
                }
                _objEnch[objectId] = new ObjEnchEntry { LastRequestMs = now };
                return -1;
            }
            e.LastRequestMs = now;
            int n = e.Count;
            if (n < 0)
                return -1;
            if (n > maxCount) n = maxCount;
            for (int i = 0; i < n; i++)
            {
                spellIds[i] = e.Ids[i];
                expiryTimes[i] = e.Exp[i];
            }
            return n;
        }
    }

    /// <summary>
    /// Main thread only (MainThreadSnapshots.Tick). Walks the watched objects'
    /// registries every 500 ms. Idle (one lock, empty dictionary) when nothing asks.
    /// </summary>
    internal static unsafe void PrefetchWatchedObjectEnchantments()
    {
        if (!MainThreadGuard.IsOnMainThread())
            return;
        long now = Environment.TickCount64;
        if (now < _nextObjectEnchRefreshMs)
            return;
        _nextObjectEnchRefreshMs = now + ObjectEnchRefreshMs;

        int n = 0;
        lock (_objEnchLock)
        {
            // A new character (or none): nothing watched belongs to it.
            IntPtr owner = PlayerVitalsHooks.KnownPlayerQualitiesPtr;
            if (owner != _objEnchSessionOwner)
            {
                _objEnch.Clear();
                _objEnchSessionOwner = owner;
            }
            if (_objEnch.Count == 0)
                return;
            // Live ids fill the work buffer from the front, idle ones from the back
            // (Count <= MaxWatchedObjects, so the two never meet); then drop the idle.
            int drop = 0;
            foreach (KeyValuePair<uint, ObjEnchEntry> kv in _objEnch)
            {
                if (now - kv.Value.LastRequestMs <= ObjectEnchIdleDropMs)
                    _objEnchWork[n++] = kv.Key;
                else
                    _objEnchWork[_objEnchWork.Length - 1 - drop++] = kv.Key;
            }
            for (int i = 0; i < drop; i++)
                _objEnch.Remove(_objEnchWork[_objEnchWork.Length - 1 - i]);
        }

        if (DbCacheTeardownHooks.TeardownActive)
            return;

        for (int i = 0; i < n; i++)
        {
            uint id = _objEnchWork[i];
            int count;
            fixed (uint* ids = _objEnchScratchIds)
            fixed (double* exp = _objEnchScratchExp)
                count = ReadObjectEnchantmentsLive(id, ids, exp, MaxSnapshotEnchantments);

            lock (_objEnchLock)
            {
                if (!_objEnch.TryGetValue(id, out ObjEnchEntry? e))
                    continue; // dropped meanwhile
                if (count > 0 && e.Ids.Length < count)
                {
                    // Grow once per object (rare: only when its buff count rises).
                    e.Ids = new uint[Math.Max(count, 16)];
                    e.Exp = new double[e.Ids.Length];
                }
                for (int k = 0; k < count; k++)
                {
                    e.Ids[k] = _objEnchScratchIds[k];
                    e.Exp[k] = _objEnchScratchExp[k];
                }
                e.Count = count;
            }
        }
    }

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

        // Off AC's main thread (host ReadObjectEnchantmentsFn: RynthAi's opt-in
        // registry diagnostic, RynthLua): the request-driven main-thread snapshot.
        // The live walk below started from a cached, possibly freed weenie pointer.
        if (!MainThreadGuard.IsOnMainThread())
            return CopyObjectSnapshot(objectId, spellIds, expiryTimes, maxCount);

        return ReadObjectEnchantmentsLive(objectId, spellIds, expiryTimes, maxCount);
    }

    // MAIN THREAD ONLY: resolve the weenie natively and walk its registry.
    private static unsafe int ReadObjectEnchantmentsLive(uint objectId, uint* spellIds, double* expiryTimes, int maxCount)
    {
        if (maxCount <= 0) return 0;
        if (!MainThreadGuard.IsOnMainThread())
            return -1;

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
    private static unsafe int ReadEnchantmentsFromQualities(IntPtr qualPtr, uint* spellIds, double* expiryTimes, int maxCount,
        uint* modTypes = null, uint* modKeys = null, float* modVals = null, uint* categories = null)
    {
        // MAIN THREAD ONLY: see the note at the end of this method.
        if (!MainThreadGuard.IsOnMainThread())
            return -1;

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

        // Stability check history (2026-09-02 "infinite buffing loop" bug): when this
        // walk ran off-thread on the plugin pump it could race AC mutating these
        // lists as buffs landed/expired, so it walked twice and trusted only an
        // exact match. Since 2026-09-30 it only runs on AC's main thread (the
        // snapshot prefetches and main-thread callers), where nothing mutates the
        // lists during the walk, so one walk is exact.
        return WalkAllLists(registryPtr, spellIds, expiryTimes, maxCount, modTypes, modKeys, modVals, categories);
    }

    private static unsafe int WalkAllLists(IntPtr registryPtr, uint* spellIds, double* expiryTimes, int maxCount,
        uint* modTypes, uint* modKeys, float* modVals, uint* categories)
    {
        int count = 0;
        count = WalkEnchantList(registryPtr + RegistryMultListOffset,     spellIds, expiryTimes, maxCount, count, modTypes, modKeys, modVals, categories);
        count = WalkEnchantList(registryPtr + RegistryAddListOffset,      spellIds, expiryTimes, maxCount, count, modTypes, modKeys, modVals, categories);
        count = WalkEnchantList(registryPtr + RegistryCooldownListOffset, spellIds, expiryTimes, maxCount, count, modTypes, modKeys, modVals, categories);
        return count;
    }

    // Optional per-enchantment detail (the Skills panel's buffs): the spell category and
    // the StatMod. With the pack(8) padding after _power_level and after _degrade_limit,
    // _last_time_degraded sits at +56 and the StatMod at +64: vtable, type +68, key +72,
    // val +76 (float); sizeof 80 = NodeNextOffset. All inside the node span checked below.
    private const int EnchantmentCategoryOffset = 12;
    private const int EnchantmentStatModTypeOffset = 68;
    private const int EnchantmentStatModKeyOffset = 72;
    private const int EnchantmentStatModValOffset = 76;

    private static unsafe int WalkEnchantList(IntPtr listPtrAddress, uint* spellIds, double* expiryTimes, int maxCount, int count,
        uint* modTypes, uint* modKeys, float* modVals, uint* categories)
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
                if (modTypes != null)
                {
                    modTypes[count] = unchecked((uint)Marshal.ReadInt32(nodePtr + EnchantmentStatModTypeOffset));
                    modKeys[count] = unchecked((uint)Marshal.ReadInt32(nodePtr + EnchantmentStatModKeyOffset));
                    modVals[count] = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(nodePtr + EnchantmentStatModValOffset));
                    categories[count] = unchecked((uint)Marshal.ReadInt32(nodePtr + EnchantmentCategoryOffset));
                }
                count++;
            }

            nodePtr = Marshal.ReadIntPtr(nodePtr + NodeNextOffset);
        }

        return count;
    }
}
