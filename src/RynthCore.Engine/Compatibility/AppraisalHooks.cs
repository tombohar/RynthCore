// ============================================================================
//  RynthCore.Engine - Compatibility/AppraisalHooks.cs
//
//  Caches what the last identify of each object carried, for objects whose
//  m_pQualities is null (CBaseQualities::Inq* return 0 for them) and for every
//  read off AC's main thread (where no Inq* may run). Two feeds: the identify
//  message itself (OnIdentifyWire, from SmartBoxHooks.ParseGameEvent: all six
//  tables plus the armour/weapon/hook profiles, see PropertyWire) and, merged
//  over it, AC's parsed AppraisalProfile via a hook on
//  CM_Examine::SendNotice_SetAppraiseInfo (int, bool, string, spells, vitals).
//
//  VA derivation (map_offset + 0x00401000 = live VA):
//    002AF5B0 CM_Examine::SendNotice_SetAppraiseInfo → 0x006B05B0
//
//  AppraisalProfile layout (from AppraisalProfile::Clear at 0x005B3BB0):
//    +0x00  vtable
//    +0x04  success_flag
//    +0x08  creature_profile*
//    +0x0c  hook_profile*
//    +0x10  weapon_profile*
//    +0x14  armor_profile*
//    +0x18  _intStatsTable*
//    +0x1c  _int64StatsTable*
//    +0x20  _boolStatsTable*        ← used here
//    +0x24  _floatStatsTable*
//    +0x28  _strStatsTable*
//    +0x2c  _didStatsTable*
//    ...
//
//  PackableHashTable<uint,int> layout (from FUN_005d5760 lookup):
//    +0x08  bucket_array (IntPtr[] of node ptrs)
//    +0x0c  bucket_count (modulus for key % count)
//  Node: [+0]=key(uint32), [+4]=value(int), [+8]=next(node* or null)
// ============================================================================

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;

namespace RynthCore.Engine.Compatibility;

internal static class AppraisalHooks
{
    // CM_Examine::SendNotice_SetAppraiseInfo — cdecl (uint guid, AppraisalProfile*)
    // Map: 002AF5B0 → live VA: 0x006B05B0
    private const int SendNoticeSetAppraiseInfoVa = 0x006B05B0;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SendNoticeSetAppraiseInfoDelegate(uint guid, IntPtr profilePtr);

    private static SendNoticeSetAppraiseInfoDelegate? _originalSendNotice;
    private static SendNoticeSetAppraiseInfoDelegate? _sendNoticeDetour; // held alive to prevent GC

    private static bool _hookInstalled;
    private static string _statusMessage = "Not initialized.";

    // Tracks every guid for which we've received a SendNotice_SetAppraiseInfo (has assess data)
    private static readonly HashSet<uint> _appraisedGuids = new();
    // Unix timestamp (seconds) of last appraisal receipt per guid
    private static readonly Dictionary<uint, long> _lastIdTime = new();
    // Every property the last identify of an object carried: guid → bag (2026-09-30).
    // Two sources, both on AC's main thread inside UIQueueManager::ProcessNetBlobData:
    //  - the identify message itself (SmartBoxHooks.ParseGameEvent → OnIdentifyWire, before
    //    AC handles it): all six tables (int, int64, bool, float, string, data id) plus the
    //    armour/weapon/hook profile values filed under their property ids (PropertyWire);
    //  - AC's parsed AppraisalProfile (SendNoticeDetour, after): int, bool and string, the
    //    long-proven path, merged over the wire values.
    // Before, only the second existed, so int64, float and data id properties never answered
    // off the main thread (the float capture of 2026-09-30 sat in a parser nothing called).
    private static readonly Dictionary<uint, PropertyBag> _bags = new();
    // The guid whose wire record ParseGameEvent stored in the current dispatch (main thread only):
    // SendNoticeDetour then merges AC's tables into it instead of replacing them.
    private static uint _wireGuidThisDispatch;
    private static int _wireLogCount, _wireMissLogCount;
    // Hard cap on identified objects kept (they are also evicted on delete and cleared on
    // logout). Past it the oldest identify is dropped.
    private const int MaxAppraisedObjects = 4096;
    // Spell book cache: guid → spell ID array (from AppraisalProfile._spellBook PSmartArray<UInt32> at +0x30)
    private static readonly Dictionary<uint, uint[]> _spellIdCache = new();
    private static readonly object _cacheLock = new();
    // Objects the CLIENT released, whose data is kept a while (ReleasedObjectRetention).
    private static readonly ReleasedObjectRetention _released = new();
    private static readonly List<uint> _expiredScratch = new();

    public static bool IsInstalled => _hookInstalled;
    public static string StatusMessage => _statusMessage;

    /// <summary>
    /// Returns true if a SendNotice_SetAppraiseInfo has been received for this guid this session.
    /// </summary>
    public static bool HasAppraisalData(uint guid)
    {
        lock (_cacheLock)
            return _appraisedGuids.Contains(guid);
    }

    /// <summary>
    /// Deep-audit finding #33 (2026-06-18): these seven session-scoped
    /// collections were populated per appraised guid and never cleared or
    /// bounded — no ClearSession() existed, and this class was conspicuously
    /// absent from DispatchPendingLogout's reset pipeline (contrast the peer
    /// ObjectQualityCache, which has both MaxEntries and a logout
    /// ClearSession()). Guids don't survive a session, so a daily
    /// multi-boxer accumulates entries indefinitely across relogs — worth
    /// closing given this stack's documented 32-bit VA-exhaustion
    /// sensitivity even though growth itself is slow. Call from
    /// DispatchPendingLogout alongside the other ResetSession() calls.
    /// </summary>
    public static void ClearSession()
    {
        lock (_cacheLock)
        {
            _appraisedGuids.Clear();
            _lastIdTime.Clear();
            _bags.Clear();
            _spellIdCache.Clear();
            _failedRollLogged.Clear();
            _released.Clear();
        }
    }

    /// <summary>
    /// Drops everything cached for an object: the SERVER deleted it (0xF747, SmartBoxHooks /
    /// ServerMessageStream), or its kept-after-release time ran out. Any thread.
    /// </summary>
    public static void EvictObject(uint guid)
    {
        lock (_cacheLock)
            EvictLocked(guid);
    }

    // Caller holds _cacheLock.
    private static void EvictLocked(uint guid)
    {
        _appraisedGuids.Remove(guid);
        _lastIdTime.Remove(guid);
        _bags.Remove(guid);
        _spellIdCache.Remove(guid);
        _failedRollLogged.Remove(guid);
        _released.Forget(guid);
    }

    /// <summary>
    /// The CLIENT released an object (AC's BeingDeleted: it left the client's view). Its data is
    /// kept for ReleasedObjectRetention.RetainMs, so a re-create inside that time still finds it
    /// (AutoId then skips the re-appraisal). Main thread (the BeingDeleted detour).
    /// </summary>
    public static void OnClientRelease(uint guid, long nowMs)
    {
        lock (_cacheLock)
        {
            if (!_appraisedGuids.Contains(guid) && !_bags.ContainsKey(guid)) return;
            if (!_released.OnRelease(guid, nowMs))
                EvictLocked(guid);   // the kept-released bound is full: drop it now
        }
    }

    /// <summary>
    /// The object was created again: its data is current, not merely kept. Except data kept since
    /// a release longer ago than ReleasedObjectRetention.StaleAfterMs: the client sent no updates
    /// meanwhile, so it is dropped here (the next appraisal refreshes it). Returns true when it was.
    /// </summary>
    public static bool MarkLive(uint guid, long nowMs)
    {
        lock (_cacheLock)
        {
            if (!_released.TakeLive(guid, nowMs)) return false;
            EvictLocked(guid);
            return true;
        }
    }

    /// <summary>Drops the data of objects released longer ago than the retention time. Timer thread.</summary>
    public static int ExpireReleased(long nowMs)
    {
        lock (_cacheLock)
        {
            _expiredScratch.Clear();
            int n = _released.Expire(nowMs, _expiredScratch);
            foreach (uint g in _expiredScratch)
                EvictLocked(g);
            _expiredScratch.Clear();
            return n;
        }
    }

    public static int CachedObjectCount
    {
        get { lock (_cacheLock) return _bags.Count; }
    }

    // Caller holds _cacheLock. Keeps the bag count under MaxAppraisedObjects by dropping the
    // object identified longest ago (a scan, only when the cap is hit).
    private static void EnforceCapLocked(uint keep)
    {
        while (_bags.Count >= MaxAppraisedObjects)
        {
            uint oldest = 0;
            long oldestTime = long.MaxValue;
            foreach (uint g in _bags.Keys)
            {
                if (g == keep) continue;
                long t = _lastIdTime.TryGetValue(g, out long v) ? v : 0;
                if (t < oldestTime) { oldestTime = t; oldest = g; }
            }
            if (oldest == 0) return;
            EvictLocked(oldest);
        }
    }

    // Caller holds _cacheLock.
    private static PropertyBag GetOrAddBagLocked(uint guid)
    {
        if (!_bags.TryGetValue(guid, out PropertyBag? bag))
        {
            EnforceCapLocked(guid);
            _bags[guid] = bag = new PropertyBag();
        }
        return bag;
    }

    /// <summary>
    /// Stores what an identify message carried (PropertyWire.TryParseIdentify), replacing the
    /// object's previous identify. AC's main thread, from SmartBoxHooks.ParseGameEvent, just
    /// before AC handles the message; SendNoticeDetour then merges AC's own tables in.
    /// </summary>
    internal static void StoreIdentify(IdentifyRecord rec)
    {
        lock (_cacheLock)
        {
            if (!_bags.ContainsKey(rec.ObjectId))
                EnforceCapLocked(rec.ObjectId);
            _bags[rec.ObjectId] = rec.Properties;
        }
        _wireGuidThisDispatch = rec.ObjectId;

        if (_wireLogCount < 20)
        {
            _wireLogCount++;
            PropertyBag b = rec.Properties;
            RynthLog.Compat($"Compat: identify wire obj=0x{rec.ObjectId:X8} flags=0x{rec.Flags:X4} ok={rec.Success} " +
                $"int={b.Ints.Count} int64={b.Int64s.Count} bool={b.Bools.Count} float={b.Floats.Count} " +
                $"string={b.Strings.Count} did={b.DataIds.Count} armor={rec.HasArmorProfile} weapon={rec.HasWeaponProfile} " +
                $"hook={rec.HasHookProfile} creature={rec.HasCreatureProfile}{(rec.Truncated ? " TRUNCATED" : "")}");
        }
    }

    /// <summary>
    /// The identify message (game event 0xC9) as UIQueueManager::ProcessNetBlobData received it,
    /// from its event type on. AC's main thread, before AC handles it. Never touches AC state.
    /// </summary>
    internal static unsafe void OnIdentifyWire(IntPtr data, int size)
    {
        _wireGuidThisDispatch = 0;
        if (data == IntPtr.Zero || size < 16)
            return;
        if (PropertyWire.TryParseIdentify(new ReadOnlySpan<byte>((void*)data, size), out IdentifyRecord rec))
            StoreIdentify(rec);
    }

    /// <summary>
    /// Folds a property update into an already-identified object so the cache doesn't keep
    /// serving the value from the last identify (a used pet essence's Structure, a changed
    /// float or string). Objects never identified are left alone; PropertyUpdateHooks keeps
    /// their updates.
    /// </summary>
    internal static void ApplyUpdate(uint guid, in PropertyUpdate u)
    {
        lock (_cacheLock)
        {
            if (_bags.TryGetValue(guid, out PropertyBag? bag))
                bag.Apply(u);
        }
    }

    /// <summary>
    /// v84 (GetObjectProperties): a copy of everything the object's last identify carried (with later
    /// updates folded in) and its spell book, or false when it was never identified. Any thread.
    /// </summary>
    internal static bool TryCopyIdentify(uint guid, out PropertyBag? bag, out uint[]? spells)
    {
        bag = null;
        spells = null;
        lock (_cacheLock)
        {
            if (_bags.TryGetValue(guid, out PropertyBag? b))
            {
                bag = new PropertyBag();
                bag.MergeFrom(b);
            }
            if (_spellIdCache.TryGetValue(guid, out uint[]? s))
                spells = (uint[])s.Clone();
        }
        return bag != null || spells != null;
    }

    public static bool TryGetCachedInt64Property(uint guid, uint stype, out long value)
    {
        value = 0;
        lock (_cacheLock)
            return _bags.TryGetValue(guid, out PropertyBag? b) && b.Int64s.TryGetValue(stype, out value);
    }

    public static bool TryGetCachedDataIdProperty(uint guid, uint stype, out uint value)
    {
        value = 0;
        lock (_cacheLock)
            return _bags.TryGetValue(guid, out PropertyBag? b) && b.DataIds.TryGetValue(stype, out value);
    }

    public static bool TryGetCachedInstanceIdProperty(uint guid, uint stype, out uint value)
    {
        value = 0;
        lock (_cacheLock)
            return _bags.TryGetValue(guid, out PropertyBag? b) && b.InstanceIds.TryGetValue(stype, out value);
    }

    /// <summary>
    /// Returns the Unix timestamp (seconds) of when appraisal data was last received for this guid, or 0 if never.
    /// </summary>
    public static long GetLastIdTime(uint guid)
    {
        lock (_cacheLock)
            return _lastIdTime.TryGetValue(guid, out long t) ? t : 0L;
    }

    /// <summary>
    /// Returns an int property from the last server appraisal for this object.
    /// Only populated after the player has identified the item (RequestId).
    /// </summary>
    public static bool TryGetCachedIntProperty(uint guid, uint stype, out int value)
    {
        value = 0;
        lock (_cacheLock)
            return _bags.TryGetValue(guid, out PropertyBag? b) && b.Ints.TryGetValue(stype, out value);
    }

    /// <summary>
    /// Returns a bool property from the last server appraisal for this object.
    /// Only populated after the player has identified the item (RequestId).
    /// </summary>
    public static bool TryGetCachedBoolProperty(uint guid, uint stype, out bool value)
    {
        value = false;
        lock (_cacheLock)
            return _bags.TryGetValue(guid, out PropertyBag? b) && b.Bools.TryGetValue(stype, out value);
    }

    /// <summary>A float (double) property from the last server appraisal of this object.</summary>
    public static bool TryGetCachedDoubleProperty(uint guid, uint stype, out double value)
    {
        value = 0;
        lock (_cacheLock)
            return _bags.TryGetValue(guid, out PropertyBag? b) && b.Floats.TryGetValue(stype, out value);
    }

    /// <summary>
    /// Returns a string property from the last server appraisal for this object.
    /// Only populated after the player has identified the item (RequestId).
    /// </summary>
    public static bool TryGetCachedStringProperty(uint guid, uint stype, out string value)
    {
        value = string.Empty;
        lock (_cacheLock)
        {
            if (_bags.TryGetValue(guid, out PropertyBag? b) && b.Strings.TryGetValue(stype, out string? s))
            {
                value = s;
                return true;
            }
            return false;
        }
    }

    // SendNoticeDetour's tables from AC's AppraisalProfile. When this dispatch's wire record is
    // in place they are merged over it (same keys win, the profile-derived values stay); when
    // the wire parse didn't run, they replace the table as they always did.
    private static void StoreProfileTable<T>(uint guid, Dictionary<uint, T> props, Func<PropertyBag, Dictionary<uint, T>> table)
    {
        lock (_cacheLock)
        {
            PropertyBag bag = GetOrAddBagLocked(guid);
            Dictionary<uint, T> dst = table(bag);
            if (_wireGuidThisDispatch != guid)
                dst.Clear();
            foreach (var kv in props) dst[kv.Key] = kv.Value;
        }
    }

    public static void Initialize()
    {
        if (_hookInstalled)
            return;

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection textSection))
        {
            _statusMessage = "acclient.exe not available.";
            return;
        }

        int funcOff = SendNoticeSetAppraiseInfoVa - textSection.TextBaseVa;
        if (funcOff < 0 || funcOff >= textSection.Bytes.Length)
        {
            _statusMessage = $"CM_Examine::SendNotice_SetAppraiseInfo VA out of range @ 0x{SendNoticeSetAppraiseInfoVa:X8}.";
            RynthLog.Compat($"Compat: appraisal hook failed - {_statusMessage}");
            return;
        }

        byte firstByte = textSection.Bytes[funcOff];
        if (firstByte is 0x00 or 0xCC or 0xC3)
        {
            _statusMessage = $"CM_Examine::SendNotice_SetAppraiseInfo looks invalid @ 0x{SendNoticeSetAppraiseInfoVa:X8} (opcode 0x{firstByte:X2}).";
            RynthLog.Compat($"Compat: appraisal hook failed - {_statusMessage}");
            return;
        }

        try
        {
            IntPtr targetAddress = new IntPtr(textSection.TextBaseVa + funcOff);
            _sendNoticeDetour = SendNoticeDetour;
            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_sendNoticeDetour);
            _originalSendNotice = Marshal.GetDelegateForFunctionPointer<SendNoticeSetAppraiseInfoDelegate>(
                MinHook.HookCreate(targetAddress, detourPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(targetAddress);

            _hookInstalled = true;
            _statusMessage = $"Hooked CM_Examine::SendNotice_SetAppraiseInfo @ 0x{targetAddress.ToInt32():X8}.";
            RynthLog.Verbose($"Compat: appraisal hook ready @ 0x{targetAddress.ToInt32():X8}, firstByte=0x{firstByte:X2}");
        }
        catch (Exception ex)
        {
            _statusMessage = ex.Message;
            RynthLog.Compat($"Compat: appraisal hook failed - {ex.Message}");
        }
    }

    private static int SendNoticeDetour(uint guid, IntPtr profilePtr)
    {
        // Call original first — notification fires, profile is still live in our frame
        int result = _originalSendNotice!(guid, profilePtr);

        lock (_cacheLock)
        {
            _appraisedGuids.Add(guid);
            _lastIdTime[guid] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        if (_wireGuidThisDispatch != guid && _wireMissLogCount < 5 && SmartBoxHooks.IsGameEventHookInstalled)
        {
            // The identify reached AC's handler without its wire record: int64, float and data
            // id properties (and the profile values) are missing for it. Seen once per few ids.
            _wireMissLogCount++;
            RynthLog.Compat($"Compat: identify 0x{guid:X8} reached SetAppraiseInfo without its wire record - only int/bool/string cached.");
        }

        try
        {
            CacheIntProps(guid, profilePtr);
        }
        catch (Exception ex)
        {
            try { RynthLog.Compat($"Compat: appraisal int cache error guid=0x{guid:X8} - {ex.GetType().Name}: {ex.Message}"); } catch { }
        }

        try
        {
            CacheBoolProps(guid, profilePtr);
        }
        catch (Exception ex)
        {
            try { RynthLog.Compat($"Compat: appraisal bool cache error guid=0x{guid:X8} - {ex.GetType().Name}: {ex.Message}"); } catch { }
        }

        try
        {
            CacheStringProps(guid, profilePtr);
        }
        catch (Exception ex)
        {
            try { RynthLog.Compat($"Compat: appraisal string cache error guid=0x{guid:X8} - {ex.GetType().Name}: {ex.Message}"); } catch { }
        }

        try
        {
            CacheSpellIds(guid, profilePtr);
        }
        catch (Exception ex)
        {
            try { RynthLog.Compat($"Compat: appraisal spell cache error guid=0x{guid:X8} - {ex.GetType().Name}: {ex.Message}"); } catch { }
        }

        try
        {
            CacheCreatureVitals(guid, profilePtr);
        }
        catch (Exception ex)
        {
            try { RynthLog.Compat($"Compat: appraisal creature-vitals error guid=0x{guid:X8} - {ex.GetType().Name}: {ex.Message}"); } catch { }
        }

        _wireGuidThisDispatch = 0;
        return result;
    }

    private static void CacheIntProps(uint guid, IntPtr profilePtr)
    {
        if (profilePtr == IntPtr.Zero || !ClientObjectHooks.IsReadablePointer(profilePtr))
            return;

        // AppraisalProfile._intStatsTable* is at offset +0x18
        IntPtr intTableFieldAddr = profilePtr + 0x18;
        if (!ClientObjectHooks.IsReadablePointer(intTableFieldAddr))
            return;
        IntPtr intTablePtr = Marshal.ReadIntPtr(intTableFieldAddr);
        if (intTablePtr == IntPtr.Zero || !ClientObjectHooks.IsReadablePointer(intTablePtr))
            return;

        // PackableHashTable<uint,int>: bucket_array at +0x8, bucket_count at +0xC
        IntPtr bucketArrayFieldAddr = intTablePtr + 0x08;
        IntPtr bucketCountFieldAddr = intTablePtr + 0x0C;
        if (!ClientObjectHooks.IsReadablePointer(bucketArrayFieldAddr) || !ClientObjectHooks.IsReadablePointer(bucketCountFieldAddr))
            return;
        IntPtr bucketArray = Marshal.ReadIntPtr(bucketArrayFieldAddr);
        int bucketCount = Marshal.ReadInt32(bucketCountFieldAddr);

        if (bucketArray == IntPtr.Zero || bucketCount <= 0 || bucketCount > 65536 || !ClientObjectHooks.IsReadablePointer(bucketArray))
            return;

        var props = new Dictionary<uint, int>(8);

        int totalGuard = 0;
        for (int i = 0; i < bucketCount; i++)
        {
            IntPtr bucketSlotAddr = bucketArray + i * 4;
            if (!ClientObjectHooks.IsReadablePointer(bucketSlotAddr)) continue;
            IntPtr node = Marshal.ReadIntPtr(bucketSlotAddr);

            int chainGuard = 0;
            while (node != IntPtr.Zero && chainGuard++ < 4096 && totalGuard++ < 65536)
            {
                if (!ClientObjectHooks.IsReadablePointer(node)) break;
                uint key = (uint)Marshal.ReadInt32(node);
                int val = Marshal.ReadInt32(node + 4);
                props[key] = val;
                IntPtr nextAddr = node + 8;
                if (!ClientObjectHooks.IsReadablePointer(nextAddr)) break;
                node = Marshal.ReadIntPtr(nextAddr);
            }
        }

        if (props.Count == 0)
            return;

        StoreProfileTable(guid, props, b => b.Ints);

        RynthLog.Verbose($"Compat: cached {props.Count} int prop(s) for guid=0x{guid:X8}");
    }

    private static void CacheBoolProps(uint guid, IntPtr profilePtr)
    {
        if (profilePtr == IntPtr.Zero || !ClientObjectHooks.IsReadablePointer(profilePtr))
            return;

        // AppraisalProfile._boolStatsTable* is at offset +0x20
        IntPtr boolTableFieldAddr = profilePtr + 0x20;
        if (!ClientObjectHooks.IsReadablePointer(boolTableFieldAddr))
            return;
        IntPtr boolTablePtr = Marshal.ReadIntPtr(boolTableFieldAddr);
        if (boolTablePtr == IntPtr.Zero || !ClientObjectHooks.IsReadablePointer(boolTablePtr))
            return;

        // PackableHashTable: bucket_array at +0x8, bucket_count at +0xC
        IntPtr bucketArrayFieldAddr = boolTablePtr + 0x08;
        IntPtr bucketCountFieldAddr = boolTablePtr + 0x0C;
        if (!ClientObjectHooks.IsReadablePointer(bucketArrayFieldAddr) || !ClientObjectHooks.IsReadablePointer(bucketCountFieldAddr))
            return;
        IntPtr bucketArray = Marshal.ReadIntPtr(bucketArrayFieldAddr);
        int bucketCount = Marshal.ReadInt32(bucketCountFieldAddr);

        if (bucketArray == IntPtr.Zero || bucketCount <= 0 || bucketCount > 65536 || !ClientObjectHooks.IsReadablePointer(bucketArray))
            return;

        var props = new Dictionary<uint, bool>(4);

        int totalGuard = 0;
        for (int i = 0; i < bucketCount; i++)
        {
            IntPtr bucketSlotAddr = bucketArray + i * 4;
            if (!ClientObjectHooks.IsReadablePointer(bucketSlotAddr)) continue;
            IntPtr node = Marshal.ReadIntPtr(bucketSlotAddr);

            int chainGuard = 0;
            while (node != IntPtr.Zero && chainGuard++ < 4096 && totalGuard++ < 65536)
            {
                if (!ClientObjectHooks.IsReadablePointer(node)) break;
                uint key = (uint)Marshal.ReadInt32(node);
                int val = Marshal.ReadInt32(node + 4);
                props[key] = val != 0;
                IntPtr nextAddr = node + 8;
                if (!ClientObjectHooks.IsReadablePointer(nextAddr)) break;
                node = Marshal.ReadIntPtr(nextAddr);
            }
        }

        if (props.Count == 0)
            return;

        StoreProfileTable(guid, props, b => b.Bools);

        RynthLog.Verbose($"Compat: cached {props.Count} bool prop(s) for guid=0x{guid:X8}");
    }

    private static void CacheStringProps(uint guid, IntPtr profilePtr)
    {
        if (profilePtr == IntPtr.Zero || !ClientObjectHooks.IsReadablePointer(profilePtr))
            return;

        // AppraisalProfile._strStatsTable* is at offset +0x28
        IntPtr strTableFieldAddr = profilePtr + 0x28;
        if (!ClientObjectHooks.IsReadablePointer(strTableFieldAddr))
            return;
        IntPtr strTablePtr = Marshal.ReadIntPtr(strTableFieldAddr);
        if (strTablePtr == IntPtr.Zero || !ClientObjectHooks.IsReadablePointer(strTablePtr))
            return;

        // PackableHashTable<uint, PStringBase<char>>: bucket_array at +0x8, bucket_count at +0xC
        IntPtr bucketArrayFieldAddr = strTablePtr + 0x08;
        IntPtr bucketCountFieldAddr = strTablePtr + 0x0C;
        if (!ClientObjectHooks.IsReadablePointer(bucketArrayFieldAddr) || !ClientObjectHooks.IsReadablePointer(bucketCountFieldAddr))
            return;
        IntPtr bucketArray = Marshal.ReadIntPtr(bucketArrayFieldAddr);
        int bucketCount = Marshal.ReadInt32(bucketCountFieldAddr);

        if (bucketArray == IntPtr.Zero || bucketCount <= 0 || bucketCount > 65536 || !ClientObjectHooks.IsReadablePointer(bucketArray))
            return;

        var props = new Dictionary<uint, string>(4);

        int totalGuard = 0;
        for (int i = 0; i < bucketCount; i++)
        {
            IntPtr bucketSlotAddr = bucketArray + i * 4;
            if (!ClientObjectHooks.IsReadablePointer(bucketSlotAddr)) continue;
            IntPtr node = Marshal.ReadIntPtr(bucketSlotAddr);

            int chainGuard = 0;
            while (node != IntPtr.Zero && chainGuard++ < 4096 && totalGuard++ < 65536)
            {
                if (!ClientObjectHooks.IsReadablePointer(node)) break;
                uint key = (uint)Marshal.ReadInt32(node);

                // Node value at +4: PStringBase<char>.m_buffer (PSRefBuffer<char>*)
                // PSRefBuffer<char> layout: vtable(4) + m_cRef(4) + m_len(4) + m_size(4) + m_hash(4) + m_data[]
                IntPtr bufferPtr = Marshal.ReadIntPtr(node + 4);
                if (bufferPtr != IntPtr.Zero && ClientObjectHooks.IsReadablePointer(bufferPtr))
                {
                    IntPtr lenFieldAddr = bufferPtr + 8;
                    if (ClientObjectHooks.IsReadablePointer(lenFieldAddr))
                    {
                        int len = Marshal.ReadInt32(lenFieldAddr);
                        IntPtr strDataAddr = bufferPtr + 20;
                        if (len > 1 && len < 4096 && ClientObjectHooks.IsReadablePointer(strDataAddr))
                        {
                            string? str = Marshal.PtrToStringAnsi(strDataAddr, len - 1);
                            if (!string.IsNullOrEmpty(str))
                                props[key] = str;
                        }
                    }
                }

                IntPtr nextAddr = node + 8;
                if (!ClientObjectHooks.IsReadablePointer(nextAddr)) break;
                node = Marshal.ReadIntPtr(nextAddr);
            }
        }

        if (props.Count == 0)
            return;

        StoreProfileTable(guid, props, b => b.Strings);

        RynthLog.Verbose($"Compat: cached {props.Count} string prop(s) for guid=0x{guid:X8}");
    }

    private static void CacheSpellIds(uint guid, IntPtr profilePtr)
    {
        if (profilePtr == IntPtr.Zero)
            return;

        // AppraisalProfile._spellBook (PSmartArray<UInt32>*) is at offset +0x30
        // PSmartArray layout: +0x00 vtable, +0x04 m_data*, +0x08 m_sizeAndDeallocate, +0x0C m_num
        IntPtr spellBookPtr = Marshal.ReadIntPtr(profilePtr + 0x30);
        if (spellBookPtr == IntPtr.Zero)
            return;

        IntPtr mData = Marshal.ReadIntPtr(spellBookPtr + 0x04);
        int mNum = Marshal.ReadInt32(spellBookPtr + 0x0C);

        if (mNum == 0)
        {
            RynthLog.Verbose($"Compat: guid=0x{guid:X8} spell book present but empty (mNum=0)");
            return;
        }
        if (mData == IntPtr.Zero || mNum < 0 || mNum > 512)
        {
            RynthLog.Compat($"Compat: guid=0x{guid:X8} spell book invalid (mData=0x{mData.ToInt32():X8} mNum={mNum})");
            return;
        }

        var ids = new uint[mNum];
        for (int i = 0; i < mNum; i++)
            ids[i] = (uint)Marshal.ReadInt32(mData + i * 4);

        lock (_cacheLock)
            _spellIdCache[guid] = ids;

        RynthLog.Verbose($"Compat: cached {mNum} spell ID(s) for guid=0x{guid:X8}");
    }

    // Rate-limit counter for the creature-vitals diagnostic log line.
    private static int _creatureVitalsLogCount;
    // Guids already logged with a [FAILED-ROLL] line (dedupe; guarded by _cacheLock).
    private static readonly HashSet<uint> _failedRollLogged = new();

    /// <summary>
    /// Reads the creature sub-profile (AppraisalProfile+0x08 → CreatureAppraisalProfile) for
    /// absolute Health/MaxHealth/Stamina/Mana and publishes them to ObjectQualityCache (polled
    /// via RynthCoreHost.TryGetTargetVitals) and to plugins (OnUpdateHealth push, e.g. RynthAi's
    /// CreatureProfileStore / RynthJuice numbers).
    ///
    /// This is the ONLY opcode that carries a monster's *absolute* max HP — the 0xC9 appraisal
    /// CreatureProfile. The 0x01C0 combat stream is ratio-only (ACE divides Current/MaxValue
    /// server-side). This hook (CM_Examine::SendNotice_SetAppraiseInfo @ 0x006B05B0) is the live
    /// appraisal seam on ACE; the older wire-parse in CombatActionHooks.TryParseIdentifyResponse
    /// is dead (its InnerDispatcher caller is disabled and the SmartBox caller was removed).
    ///
    /// Offsets verified from CreatureAppraisalProfile::InqAttribute2nd (decompile 0x005B6ED0):
    ///   MaxHealth=+0x28  Health=+0x1C  MaxStamina=+0x2C  Stamina=+0x20  MaxMana=+0x30  Mana=+0x24.
    /// The +0x08 pointer is non-null only when the appraisal carried a CreatureProfile (creatures
    /// that are not NPCLooksLikeObject); items/hooks leave it 0 (AppraisalProfile::Clear layout).
    /// It is the engine's own live struct for this dispatch frame, so the read is safe under the
    /// same try/catch the sibling Cache* helpers use — no native call, no AC mutation.
    ///
    /// Crucially this captures max even on a FAILED assess roll: ACE assigns Health/HealthMax
    /// before the success gate (CreatureProfile.cs), so no AssessCreature skill is required.
    /// </summary>
    private static void CacheCreatureVitals(uint guid, IntPtr profilePtr)
    {
        if (profilePtr == IntPtr.Zero)
            return;

        // AppraisalProfile._creatureProfile* is at offset +0x08.
        IntPtr creaturePtr = Marshal.ReadIntPtr(profilePtr + 0x08);
        if (creaturePtr == IntPtr.Zero)
            return; // non-creature appraisal (item / hook) — no vitals present

        uint maxHealth = unchecked((uint)Marshal.ReadInt32(creaturePtr + 0x28));
        if (maxHealth == 0 || maxHealth >= 1_000_000)
            return; // unset / implausible — don't poison the cache

        uint health = unchecked((uint)Marshal.ReadInt32(creaturePtr + 0x1C));
        uint stamina = unchecked((uint)Marshal.ReadInt32(creaturePtr + 0x20));
        uint maxStamina = unchecked((uint)Marshal.ReadInt32(creaturePtr + 0x2C));
        uint mana = unchecked((uint)Marshal.ReadInt32(creaturePtr + 0x24));
        uint maxMana = unchecked((uint)Marshal.ReadInt32(creaturePtr + 0x30));

        if (health > maxHealth)
            health = maxHealth; // clamp a transient over-read

        ObjectQualityCache.SetCreatureVitals(guid,
            new CreatureVitals(health, maxHealth, stamina, maxStamina, mana, maxMana));

        float ratio = (float)health / maxHealth;
        Plugins.PluginManager.QueueUpdateHealth(guid, ratio, health, maxHealth);

        // ShowAttributes (wire flag 0x8) is CLEAR on a FAILED assess roll; CreatureAppraisalProfile::UnPack
        // (decompile 0x005B7240, lines 31-42) then EXPLICITLY zeroes stamina/mana/attributes — so
        // (maxStamina==0 && maxMana==0) is a reliable failed-roll signal, while Health/MaxHealth are written
        // unconditionally. A failed-roll line therefore PROVES the un-gated capture (max obtained with no
        // attributes). Failed rolls are rare (most mobs have Deception==0 → success forced) and are the whole
        // point of this hook, so log them ALWAYS (greppable [FAILED-ROLL] tag); rate-limit the common success case.
        if (maxStamina == 0 && maxMana == 0)
        {
            // Failed roll. Dedupe per guid — the combat target gets re-appraised ~1/0.75s, which would
            // otherwise spam the log (and bloat it over a grind). Log the first capture per mob only.
            bool firstForGuid;
            lock (_cacheLock)
                firstForGuid = _failedRollLogged.Add(guid);
            if (firstForGuid)
                RynthLog.Compat($"Compat: appraisal creature vitals [FAILED-ROLL] guid=0x{guid:X8} hp={health}/{maxHealth} (stam/mana withheld — max captured anyway)");
        }
        else
        {
            // CreatureAppraisalProfile.enchantment_bitfield (+0x34): low 9 bits = attribute / vital
            // modified, bits 16..24 = modified upward. The nameplates' "debuffed by others" marker.
            ImGuiBackend.Hud.HudFeed.OnAppraisal(guid, unchecked((uint)Marshal.ReadInt32(creaturePtr + 0x34)));

            int log = Interlocked.Increment(ref _creatureVitalsLogCount);
            if (log <= 50)
                RynthLog.Compat($"Compat: appraisal creature vitals guid=0x{guid:X8} hp={health}/{maxHealth} stam={stamina}/{maxStamina} mana={mana}/{maxMana}");
        }
    }

    /// <summary>
    /// Fills <paramref name="output"/> with spell IDs from the last appraisal spell book.
    /// Returns the total number of spell IDs (may exceed <paramref name="maxCount"/>), or -1 if no data.
    /// </summary>
    public static int GetObjectSpellIds(uint guid, uint[] output, int maxCount)
    {
        lock (_cacheLock)
        {
            if (!_spellIdCache.TryGetValue(guid, out uint[]? cached))
                return -1;
            int count = Math.Min(cached.Length, Math.Min(maxCount, output.Length));
            Array.Copy(cached, output, count);
            return cached.Length;
        }
    }
}
