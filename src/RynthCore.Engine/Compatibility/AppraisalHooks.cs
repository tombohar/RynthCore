// ============================================================================
//  RynthCore.Engine - Compatibility/AppraisalHooks.cs
//
//  Hooks CM_Examine::SendNotice_SetAppraiseInfo to cache appraisal bool
//  properties for inventory items whose m_pQualities is null.
//  CBaseQualities::InqBool returns 0 for such items because the property
//  setter skips CBaseQualities storage when m_pQualities is null.
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
    // Int property cache: guid → (stype → value)
    private static readonly Dictionary<uint, Dictionary<uint, int>> _intCache = new();
    // Bool property cache: guid → (stype → value)
    private static readonly Dictionary<uint, Dictionary<uint, bool>> _boolCache = new();
    // String property cache: guid → (stype → value)
    private static readonly Dictionary<uint, Dictionary<uint, string>> _stringCache = new();
    // Spell book cache: guid → spell ID array (from AppraisalProfile._spellBook PSmartArray<UInt32> at +0x30)
    private static readonly Dictionary<uint, uint[]> _spellIdCache = new();
    private static readonly object _cacheLock = new();

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
        {
            if (!_intCache.TryGetValue(guid, out Dictionary<uint, int>? props))
                return false;
            return props.TryGetValue(stype, out value);
        }
    }

    /// <summary>
    /// Returns a bool property from the last server appraisal for this object.
    /// Only populated after the player has identified the item (RequestId).
    /// </summary>
    public static bool TryGetCachedBoolProperty(uint guid, uint stype, out bool value)
    {
        value = false;
        lock (_cacheLock)
        {
            if (!_boolCache.TryGetValue(guid, out Dictionary<uint, bool>? props))
                return false;
            return props.TryGetValue(stype, out value);
        }
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
            if (!_stringCache.TryGetValue(guid, out Dictionary<uint, string>? props))
                return false;
            return props.TryGetValue(stype, out value!);
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

        lock (_cacheLock)
        {
            _intCache[guid] = props;
        }

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

        lock (_cacheLock)
        {
            _boolCache[guid] = props;
        }

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

        lock (_cacheLock)
        {
            _stringCache[guid] = props;
        }

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
