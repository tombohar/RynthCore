// ============================================================================
//  RynthCore.Engine - Compatibility/PropertyUpdateHooks.cs
//
//  Hooks CM_Qualities network message dispatchers to cache property updates
//  broadcast by the server.  This solves the problem where
//  CBaseQualities::InqInt/InqBool fail for static world objects (doors, signs,
//  etc.) because m_pQualities is null — those objects never get a qualities
//  block allocated in client memory, but the server still sends property
//  update messages that are processed and discarded.
//
//  Hooked functions (all cdecl, from Chorizite CM.cs):
//    CM_Qualities::DispatchUI_UpdateInt        @ 0x006B0000
//    CM_Qualities::DispatchUI_UpdateBool       @ 0x006AFF00
//    CM_Qualities::DispatchUI_PrivateUpdateInt @ 0x006AF960
//    CM_Qualities::DispatchUI_PrivateUpdateBool@ 0x006AF880
//
//  Eviction via:
//    ECM_Physics::SendNotice_BeingDeleted      @ 0x00693960
//
//  Public message buffer layout:
//    [0..3]  sequence (uint)
//    [4..7]  objectGUID (uint)
//    [8..11] stype (uint)
//    [12..15] value (int / int-as-bool)
//
//  Private message buffer layout (player implied):
//    [0..3]  sequence (uint)
//    [4..7]  stype (uint)
//    [8..11] value (int / int-as-bool)
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;

namespace RynthCore.Engine.Compatibility;

internal static class PropertyUpdateHooks
{
    // --- Hook target VAs ---
    private const int DispatchUI_UpdateIntVa        = 0x006B0000;
    private const int DispatchUI_UpdateBoolVa       = 0x006AFF00;
    private const int DispatchUI_PrivateUpdateIntVa = 0x006AF960;
    private const int DispatchUI_PrivateUpdateBoolVa= 0x006AF880;
    private const int SendNotice_BeingDeletedVa     = 0x00693960;

    // CWeenieObject object ID offset (vfptr(4) + hash_next*(4) + id(4) = offset 8)
    private const int WeenieIdOffset = 8;

    // --- Delegate types ---
    // CM_Qualities dispatch: uint __cdecl (UIQueueManager* ui, void* buf, uint size)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint DispatchDelegate(IntPtr uiPtr, IntPtr bufPtr, uint size);

    // ECM_Physics::SendNotice_BeingDeleted: byte __cdecl (CWeenieObject* obj)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte BeingDeletedDelegate(IntPtr weenieObjPtr);

    // --- Original function pointers (for call-through) ---
    private static DispatchDelegate? _origUpdateInt;
    private static DispatchDelegate? _origUpdateBool;
    private static DispatchDelegate? _origPrivateUpdateInt;
    private static DispatchDelegate? _origPrivateUpdateBool;
    private static BeingDeletedDelegate? _origBeingDeleted;

    // --- Prevent GC of detour delegates ---
    private static DispatchDelegate? _detourUpdateInt;
    private static DispatchDelegate? _detourUpdateBool;
    private static DispatchDelegate? _detourPrivateUpdateInt;
    private static DispatchDelegate? _detourPrivateUpdateBool;
    private static BeingDeletedDelegate? _detourBeingDeleted;

    private static bool _hookInstalled;
    private static string _statusMessage = "Not initialized.";
    private static int _hookCount; // how many of the 5 hooks succeeded

    // --- Property caches ---
    // guid → (stype → value)
    private static readonly ConcurrentDictionary<uint, ConcurrentDictionary<uint, int>> _intCache = new();
    private static readonly ConcurrentDictionary<uint, ConcurrentDictionary<uint, int>> _boolCache = new();

    public static bool IsInstalled => _hookInstalled;
    public static string StatusMessage => _statusMessage;

    // --- Public query API ---

    public static bool TryGetCachedIntProperty(uint objectId, uint stype, out int value)
    {
        value = 0;
        if (_intCache.TryGetValue(objectId, out var props))
            return props.TryGetValue(stype, out value);
        return false;
    }

    public static bool TryGetCachedBoolProperty(uint objectId, uint stype, out bool value)
    {
        value = false;
        if (_boolCache.TryGetValue(objectId, out var props))
        {
            if (props.TryGetValue(stype, out int raw))
            {
                value = raw != 0;
                return true;
            }
        }
        return false;
    }

    /// <summary>Evict all cached properties for an object (e.g. when it is destroyed).</summary>
    public static void EvictObject(uint objectId)
    {
        _intCache.TryRemove(objectId, out _);
        _boolCache.TryRemove(objectId, out _);
    }

    // --- Initialization ---

    public static void Initialize()
    {
        if (_hookInstalled)
            return;

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection textSection))
        {
            _statusMessage = "acclient.exe not available.";
            return;
        }

        _hookCount = 0;

        TryInstallHook(textSection, DispatchUI_UpdateIntVa, "DispatchUI_UpdateInt",
            _detourUpdateInt = DetourUpdateInt, out _origUpdateInt);

        TryInstallHook(textSection, DispatchUI_UpdateBoolVa, "DispatchUI_UpdateBool",
            _detourUpdateBool = DetourUpdateBool, out _origUpdateBool);

        TryInstallHook(textSection, DispatchUI_PrivateUpdateIntVa, "DispatchUI_PrivateUpdateInt",
            _detourPrivateUpdateInt = DetourPrivateUpdateInt, out _origPrivateUpdateInt);

        TryInstallHook(textSection, DispatchUI_PrivateUpdateBoolVa, "DispatchUI_PrivateUpdateBool",
            _detourPrivateUpdateBool = DetourPrivateUpdateBool, out _origPrivateUpdateBool);

        TryInstallBeingDeletedHook(textSection);

        _hookInstalled = _hookCount > 0;
        _statusMessage = _hookInstalled
            ? $"{_hookCount}/5 hooks installed."
            : "All hooks failed.";
        RynthLog.Verbose($"Compat: property-update hooks — {_statusMessage}");
    }

    // --- Hook installation helpers ---

    private static void TryInstallHook(AcClientTextSection textSection, int va, string name,
        DispatchDelegate detour, out DispatchDelegate? original)
    {
        original = null;
        try
        {
            int funcOff = va - textSection.TextBaseVa;
            if (funcOff < 0 || funcOff >= textSection.Bytes.Length)
            {
                RynthLog.Compat($"Compat: {name} VA out of range @ 0x{va:X8}.");
                return;
            }

            byte firstByte = textSection.Bytes[funcOff];
            if (firstByte is 0x00 or 0xCC or 0xC3)
            {
                RynthLog.Compat($"Compat: {name} looks invalid @ 0x{va:X8} (opcode 0x{firstByte:X2}).");
                return;
            }

            IntPtr target = new IntPtr(textSection.TextBaseVa + funcOff);
            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(detour);
            original = Marshal.GetDelegateForFunctionPointer<DispatchDelegate>(
                MinHook.HookCreate(target, detourPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(target);

            _hookCount++;
            RynthLog.Verbose($"Compat: {name} hooked @ 0x{target.ToInt32():X8}");
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"Compat: {name} hook failed — {ex.Message}");
        }
    }

    private static void TryInstallBeingDeletedHook(AcClientTextSection textSection)
    {
        try
        {
            int funcOff = SendNotice_BeingDeletedVa - textSection.TextBaseVa;
            if (funcOff < 0 || funcOff >= textSection.Bytes.Length)
            {
                RynthLog.Compat($"Compat: BeingDeleted VA out of range @ 0x{SendNotice_BeingDeletedVa:X8}.");
                return;
            }

            byte firstByte = textSection.Bytes[funcOff];
            if (firstByte is 0x00 or 0xCC or 0xC3)
            {
                RynthLog.Compat($"Compat: BeingDeleted looks invalid @ 0x{SendNotice_BeingDeletedVa:X8} (opcode 0x{firstByte:X2}).");
                return;
            }

            IntPtr target = new IntPtr(textSection.TextBaseVa + funcOff);
            _detourBeingDeleted = DetourBeingDeleted;
            IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_detourBeingDeleted);
            _origBeingDeleted = Marshal.GetDelegateForFunctionPointer<BeingDeletedDelegate>(
                MinHook.HookCreate(target, detourPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(target);

            _hookCount++;
            RynthLog.Verbose($"Compat: BeingDeleted hooked @ 0x{target.ToInt32():X8}");
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"Compat: BeingDeleted hook failed — {ex.Message}");
        }
    }

    // --- Detour implementations ---

    private static uint DetourUpdateInt(IntPtr uiPtr, IntPtr bufPtr, uint size)
    {
        try
        {
            // Public: seq(4) + guid(4) + stype(4) + value(4) = 16 bytes minimum
            if (bufPtr != IntPtr.Zero && size >= 16)
            {
                uint guid  = (uint)Marshal.ReadInt32(bufPtr + 4);
                uint stype = (uint)Marshal.ReadInt32(bufPtr + 8);
                int  val   = Marshal.ReadInt32(bufPtr + 12);
                CacheInt(guid, stype, val);
            }
        }
        catch { }
        return _origUpdateInt!(uiPtr, bufPtr, size);
    }

    private static uint DetourUpdateBool(IntPtr uiPtr, IntPtr bufPtr, uint size)
    {
        try
        {
            // Public: seq(4) + guid(4) + stype(4) + value(4) = 16 bytes minimum
            if (bufPtr != IntPtr.Zero && size >= 16)
            {
                uint guid  = (uint)Marshal.ReadInt32(bufPtr + 4);
                uint stype = (uint)Marshal.ReadInt32(bufPtr + 8);
                int  val   = Marshal.ReadInt32(bufPtr + 12);
                CacheBool(guid, stype, val);
            }
        }
        catch { }
        return _origUpdateBool!(uiPtr, bufPtr, size);
    }

    private static uint DetourPrivateUpdateInt(IntPtr uiPtr, IntPtr bufPtr, uint size)
    {
        try
        {
            // Private: seq(4) + stype(4) + value(4) = 12 bytes minimum
            if (bufPtr != IntPtr.Zero && size >= 12)
            {
                uint playerId = ClientHelperHooks.GetPlayerId();
                if (playerId != 0)
                {
                    uint stype = (uint)Marshal.ReadInt32(bufPtr + 4);
                    int  val   = Marshal.ReadInt32(bufPtr + 8);
                    CacheInt(playerId, stype, val);
                }
            }
        }
        catch { }
        return _origPrivateUpdateInt!(uiPtr, bufPtr, size);
    }

    private static uint DetourPrivateUpdateBool(IntPtr uiPtr, IntPtr bufPtr, uint size)
    {
        try
        {
            // Private: seq(4) + stype(4) + value(4) = 12 bytes minimum
            if (bufPtr != IntPtr.Zero && size >= 12)
            {
                uint playerId = ClientHelperHooks.GetPlayerId();
                if (playerId != 0)
                {
                    uint stype = (uint)Marshal.ReadInt32(bufPtr + 4);
                    int  val   = Marshal.ReadInt32(bufPtr + 8);
                    CacheBool(playerId, stype, val);
                }
            }
        }
        catch { }
        return _origPrivateUpdateBool!(uiPtr, bufPtr, size);
    }

    private static byte DetourBeingDeleted(IntPtr weenieObjPtr)
    {
        try
        {
            if (weenieObjPtr != IntPtr.Zero)
            {
                IntPtr idAddr = weenieObjPtr + WeenieIdOffset;
                if (ClientObjectHooks.IsReadablePointer(idAddr))
                {
                    uint objectId = (uint)Marshal.ReadInt32(idAddr);
                    if (objectId != 0)
                        EvictObject(objectId);
                }
            }
        }
        catch { }
        return _origBeingDeleted!(weenieObjPtr);
    }

    // --- Cache helpers ---

    private static void CacheInt(uint guid, uint stype, int value)
    {
        var props = _intCache.GetOrAdd(guid, _ => new ConcurrentDictionary<uint, int>());
        props[stype] = value;
    }

    private static void CacheBool(uint guid, uint stype, int value)
    {
        var props = _boolCache.GetOrAdd(guid, _ => new ConcurrentDictionary<uint, int>());
        props[stype] = value;
    }
}
