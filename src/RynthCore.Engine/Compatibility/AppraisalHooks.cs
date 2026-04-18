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

        return result;
    }

    private static void CacheIntProps(uint guid, IntPtr profilePtr)
    {
        if (profilePtr == IntPtr.Zero)
            return;

        // AppraisalProfile._intStatsTable* is at offset +0x18
        IntPtr intTablePtr = Marshal.ReadIntPtr(profilePtr + 0x18);
        if (intTablePtr == IntPtr.Zero)
            return;

        // PackableHashTable<uint,int>: bucket_array at +0x8, bucket_count at +0xC
        IntPtr bucketArray = Marshal.ReadIntPtr(intTablePtr + 0x08);
        int bucketCount = Marshal.ReadInt32(intTablePtr + 0x0C);

        if (bucketArray == IntPtr.Zero || bucketCount <= 0 || bucketCount > 65536)
            return;

        var props = new Dictionary<uint, int>(8);

        for (int i = 0; i < bucketCount; i++)
        {
            IntPtr node = Marshal.ReadIntPtr(bucketArray + i * 4);
            while (node != IntPtr.Zero)
            {
                uint key = (uint)Marshal.ReadInt32(node);
                int val = Marshal.ReadInt32(node + 4);
                props[key] = val;
                node = Marshal.ReadIntPtr(node + 8);
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
        if (profilePtr == IntPtr.Zero)
            return;

        // AppraisalProfile._boolStatsTable* is at offset +0x20
        IntPtr boolTablePtr = Marshal.ReadIntPtr(profilePtr + 0x20);
        if (boolTablePtr == IntPtr.Zero)
            return;

        // PackableHashTable: bucket_array at +0x8, bucket_count at +0xC
        IntPtr bucketArray = Marshal.ReadIntPtr(boolTablePtr + 0x08);
        int bucketCount = Marshal.ReadInt32(boolTablePtr + 0x0C);

        if (bucketArray == IntPtr.Zero || bucketCount <= 0 || bucketCount > 65536)
            return;

        var props = new Dictionary<uint, bool>(4);

        for (int i = 0; i < bucketCount; i++)
        {
            IntPtr node = Marshal.ReadIntPtr(bucketArray + i * 4);
            while (node != IntPtr.Zero)
            {
                uint key = (uint)Marshal.ReadInt32(node);
                int val = Marshal.ReadInt32(node + 4);
                props[key] = val != 0;
                node = Marshal.ReadIntPtr(node + 8);
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
        if (profilePtr == IntPtr.Zero)
            return;

        // AppraisalProfile._strStatsTable* is at offset +0x28
        IntPtr strTablePtr = Marshal.ReadIntPtr(profilePtr + 0x28);
        if (strTablePtr == IntPtr.Zero)
            return;

        // PackableHashTable<uint, PStringBase<char>>: bucket_array at +0x8, bucket_count at +0xC
        IntPtr bucketArray = Marshal.ReadIntPtr(strTablePtr + 0x08);
        int bucketCount = Marshal.ReadInt32(strTablePtr + 0x0C);

        if (bucketArray == IntPtr.Zero || bucketCount <= 0 || bucketCount > 65536)
            return;

        var props = new Dictionary<uint, string>(4);

        for (int i = 0; i < bucketCount; i++)
        {
            IntPtr node = Marshal.ReadIntPtr(bucketArray + i * 4);
            while (node != IntPtr.Zero)
            {
                uint key = (uint)Marshal.ReadInt32(node);

                // Node value at +4: PStringBase<char>.m_buffer (PSRefBuffer<char>*)
                // PSRefBuffer<char> layout: vtable(4) + m_cRef(4) + m_len(4) + m_size(4) + m_hash(4) + m_data[]
                IntPtr bufferPtr = Marshal.ReadIntPtr(node + 4);
                if (bufferPtr != IntPtr.Zero)
                {
                    int len = Marshal.ReadInt32(bufferPtr + 8);
                    if (len > 1)
                    {
                        string? str = Marshal.PtrToStringAnsi(bufferPtr + 20, len - 1);
                        if (!string.IsNullOrEmpty(str))
                            props[key] = str;
                    }
                }

                node = Marshal.ReadIntPtr(node + 8);
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
