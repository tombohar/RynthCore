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

    // Bool property cache: guid → (stype → value)
    private static readonly Dictionary<uint, Dictionary<uint, bool>> _boolCache = new();
    private static readonly object _cacheLock = new();

    public static bool IsInstalled => _hookInstalled;
    public static string StatusMessage => _statusMessage;

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
            RynthLog.Compat($"Compat: appraisal hook ready @ 0x{targetAddress.ToInt32():X8}, firstByte=0x{firstByte:X2}");
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

        try
        {
            CacheBoolProps(guid, profilePtr);
        }
        catch (Exception ex)
        {
            try { RynthLog.Compat($"Compat: appraisal bool cache error guid=0x{guid:X8} - {ex.GetType().Name}: {ex.Message}"); } catch { }
        }

        return result;
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

        RynthLog.Compat($"Compat: cached {props.Count} bool prop(s) for guid=0x{guid:X8}");
    }
}
