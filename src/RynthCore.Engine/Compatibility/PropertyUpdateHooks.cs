// ============================================================================
//  RynthCore.Engine - Compatibility/PropertyUpdateHooks.cs
//
//  Caches the property update messages the server sends (UpdateProperty*,
//  public and private, every type) and the player's own properties from
//  PlayerDescription. This solves the problem where CBaseQualities::Inq* fail
//  for objects whose m_pQualities is null (doors, signs, pack items), and where
//  off AC's main thread no Inq* may run at all: plugins, metas and Lua read
//  from these caches instead.
//
//  Sources (all on AC's main thread):
//    - SmartBoxHooks.ParseGameEvent (UIQueueManager::ProcessNetBlobData) hands
//      every UpdateProperty* message (0x02CD-0x02DA) to OnUpdateWire and the
//      PlayerDescription (game event 0x13) to OnPlayerDescriptionWire, parsed by
//      PropertyWire in ACE's exact formats. This is the one path for int64,
//      float, string, data id and instance id updates (2026-09-30).
//    - The older CM_Qualities::DispatchUI_Update{Int,Bool} hooks (from Chorizite
//      CM.cs) stay installed: they cache int/bool updates only when the
//      game-event hook is missing, so nothing is lost if it fails to install.
//        CM_Qualities::DispatchUI_UpdateInt        @ 0x006B0000
//        CM_Qualities::DispatchUI_UpdateBool       @ 0x006AFF00
//        CM_Qualities::DispatchUI_PrivateUpdateInt @ 0x006AF960
//        CM_Qualities::DispatchUI_PrivateUpdateBool@ 0x006AF880
//
//  Bounds: evicted on ECM_Physics::SendNotice_BeingDeleted @ 0x00693960,
//  cleared on logout (PluginManager.DispatchPendingLogout), and pruned to the
//  live objects when more than MaxObjects are cached.
// ============================================================================

using System;
using System.Collections.Generic;
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

    // --- Property caches (all under _lock) ---
    // Public updates: guid → every property the server updated for it this session.
    private static readonly Dictionary<uint, PropertyBag> _bags = new();
    // The player: PlayerDescription's tables, then every private update (they carry no guid).
    private static PropertyBag _self = new();
    private static bool _selfDescribed;
    private static readonly object _lock = new();
    private const int MaxObjects = 4096;
    private static int _updateLogCount;

    public static bool IsInstalled => _hookInstalled;
    public static string StatusMessage => _statusMessage;

    // --- Public query API (any thread: dictionary reads under _lock) ---

    public static bool TryGetCachedIntProperty(uint objectId, uint stype, out int value)
    {
        value = 0;
        lock (_lock)
            return _bags.TryGetValue(objectId, out PropertyBag? b) && b.Ints.TryGetValue(stype, out value);
    }

    public static bool TryGetCachedBoolProperty(uint objectId, uint stype, out bool value)
    {
        value = false;
        lock (_lock)
            return _bags.TryGetValue(objectId, out PropertyBag? b) && b.Bools.TryGetValue(stype, out value);
    }

    public static bool TryGetCachedInt64Property(uint objectId, uint stype, out long value)
    {
        value = 0;
        lock (_lock)
            return _bags.TryGetValue(objectId, out PropertyBag? b) && b.Int64s.TryGetValue(stype, out value);
    }

    public static bool TryGetCachedDoubleProperty(uint objectId, uint stype, out double value)
    {
        value = 0;
        lock (_lock)
            return _bags.TryGetValue(objectId, out PropertyBag? b) && b.Floats.TryGetValue(stype, out value);
    }

    public static bool TryGetCachedStringProperty(uint objectId, uint stype, out string value)
    {
        value = string.Empty;
        lock (_lock)
        {
            if (_bags.TryGetValue(objectId, out PropertyBag? b) && b.Strings.TryGetValue(stype, out string? s))
            {
                value = s;
                return true;
            }
            return false;
        }
    }

    public static bool TryGetCachedDataIdProperty(uint objectId, uint stype, out uint value)
    {
        value = 0;
        lock (_lock)
            return _bags.TryGetValue(objectId, out PropertyBag? b) && b.DataIds.TryGetValue(stype, out value);
    }

    public static bool TryGetCachedInstanceIdProperty(uint objectId, uint stype, out uint value)
    {
        value = 0;
        lock (_lock)
            return _bags.TryGetValue(objectId, out PropertyBag? b) && b.InstanceIds.TryGetValue(stype, out value);
    }

    /// <summary>v84 (GetObjectProperties): a copy of every public update the server sent for the object, or null. Any thread.</summary>
    internal static PropertyBag? CopyPublicUpdates(uint objectId)
    {
        lock (_lock)
        {
            if (!_bags.TryGetValue(objectId, out PropertyBag? b)) return null;
            var copy = new PropertyBag();
            copy.MergeFrom(b);
            return copy;
        }
    }

    /// <summary>v84 (GetObjectProperties): a copy of the player's own record (PlayerDescription + private updates). Any thread.</summary>
    internal static PropertyBag CopySelf()
    {
        lock (_lock)
        {
            var copy = new PropertyBag();
            copy.MergeFrom(_self);
            return copy;
        }
    }

    // The player's own record (PlayerDescription + private updates).
    public static bool TryGetSelfInt(uint stype, out int value) { lock (_lock) return _self.Ints.TryGetValue(stype, out value); }
    public static bool TryGetSelfInt64(uint stype, out long value) { lock (_lock) return _self.Int64s.TryGetValue(stype, out value); }
    public static bool TryGetSelfBool(uint stype, out bool value) { lock (_lock) return _self.Bools.TryGetValue(stype, out value); }
    public static bool TryGetSelfFloat(uint stype, out double value) { lock (_lock) return _self.Floats.TryGetValue(stype, out value); }
    public static bool TryGetSelfDataId(uint stype, out uint value) { lock (_lock) return _self.DataIds.TryGetValue(stype, out value); }
    public static bool TryGetSelfInstanceId(uint stype, out uint value) { lock (_lock) return _self.InstanceIds.TryGetValue(stype, out value); }
    public static bool TryGetSelfString(uint stype, out string value)
    {
        value = string.Empty;
        lock (_lock)
        {
            if (!_self.Strings.TryGetValue(stype, out string? s)) return false;
            value = s;
            return true;
        }
    }

    /// <summary>True once this session's PlayerDescription was read.</summary>
    public static bool HasPlayerDescription
    {
        get { lock (_lock) return _selfDescribed; }
    }

    public static int CachedObjectCount
    {
        get { lock (_lock) return _bags.Count; }
    }

    public static int SelfPropertyCount
    {
        get { lock (_lock) return _self.Count; }
    }

    /// <summary>Evict all cached properties for an object (e.g. when it is destroyed).</summary>
    public static void EvictObject(uint objectId)
    {
        lock (_lock)
            _bags.Remove(objectId);
    }

    /// <summary>Logout: nothing here survives the session (guids and the player change).</summary>
    public static void ClearSession()
    {
        lock (_lock)
        {
            _bags.Clear();
            _self = new PropertyBag();
            _selfDescribed = false;
        }
        ClientObjectHooks.ClearPlayerQualitiesSnapshot();
        ClientObjectHooks.ClearObjectQualities();
    }

    // --- Wire entry points (AC's main thread, SmartBoxHooks.ParseGameEvent) ---

    /// <summary>One UpdateProperty* message (0x02CD-0x02DA), from its opcode on.</summary>
    internal static unsafe void OnUpdateWire(IntPtr data, int size)
    {
        if (data == IntPtr.Zero || size < 9)
            return;
        if (PropertyWire.TryParsePropertyUpdate(new ReadOnlySpan<byte>((void*)data, size), out PropertyUpdate u))
            Apply(u);
    }

    /// <summary>The player's PlayerDescription (game event 0x13), from its event type on.</summary>
    internal static unsafe void OnPlayerDescriptionWire(IntPtr data, int size)
    {
        if (data == IntPtr.Zero || size < 12)
            return;
        if (!PropertyWire.TryParsePlayerDescription(new ReadOnlySpan<byte>((void*)data, size), out PropertyBag bag, out bool truncated))
            return;
        lock (_lock)
        {
            // Private updates that raced ahead of the description are newer: keep them on top.
            bag.MergeFrom(_self);
            _self = bag;
            _selfDescribed = true;
        }
        RynthLog.Compat($"Compat: PlayerDescription properties int={bag.Ints.Count} int64={bag.Int64s.Count} bool={bag.Bools.Count} " +
            $"float={bag.Floats.Count} string={bag.Strings.Count} did={bag.DataIds.Count} iid={bag.InstanceIds.Count}{(truncated ? " TRUNCATED" : "")}");
    }

    /// <summary>
    /// Files one update: a private one into the player's record, a public one under its object;
    /// either way it is also folded into that object's identify record so the appraisal cache
    /// (read first) doesn't serve a stale value.
    /// </summary>
    internal static void Apply(in PropertyUpdate u)
    {
        uint target;
        if (u.IsPrivate)
        {
            lock (_lock)
                _self.Apply(u);
            target = ClientHelperHooks.GetPlayerId();
        }
        else
        {
            if (u.ObjectId == 0)
                return;
            lock (_lock)
            {
                if (!_bags.TryGetValue(u.ObjectId, out PropertyBag? bag))
                {
                    if (_bags.Count >= MaxObjects)
                        PruneToLiveLocked();
                    _bags[u.ObjectId] = bag = new PropertyBag();
                }
                bag.Apply(u);
            }
            target = u.ObjectId;
        }
        if (target != 0)
            AppraisalHooks.ApplyUpdate(target, u);

        if (_updateLogCount < 8)
        {
            _updateLogCount++;
            RynthLog.Info($"[PropUpd] {(u.IsPrivate ? "private" : "public")} {u.Kind} obj=0x{target:X8} key={u.Key} " +
                $"value={(u.Kind == PropertyKind.Float ? u.Real.ToString(System.Globalization.CultureInfo.InvariantCulture) : u.Kind == PropertyKind.String ? u.Text : u.Integer.ToString(System.Globalization.CultureInfo.InvariantCulture))}");
        }
    }

    // Caller holds _lock. Drops objects the client no longer knows (the identity walk's id list),
    // or everything if that list is empty; the player's own public updates are kept.
    private static void PruneToLiveLocked()
    {
        uint[] live = ClientObjectHooks.LiveObjectIds;
        uint me = ClientHelperHooks.GetPlayerId();
        if (live.Length == 0)
        {
            PropertyBag? mine = me != 0 && _bags.TryGetValue(me, out PropertyBag? m) ? m : null;
            _bags.Clear();
            if (mine != null) _bags[me] = mine;
            return;
        }
        var keep = new HashSet<uint>(live);
        var drop = new List<uint>();
        foreach (uint id in _bags.Keys)
            if (id != me && !keep.Contains(id))
                drop.Add(id);
        foreach (uint id in drop)
            _bags.Remove(id);
        if (_bags.Count >= MaxObjects)   // everything is live: make room anyway
        {
            PropertyBag? mine = me != 0 && _bags.TryGetValue(me, out PropertyBag? m) ? m : null;
            _bags.Clear();
            if (mine != null) _bags[me] = mine;
        }
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

    // The CM_Qualities dispatchers receive the message from its opcode on (acclient.exe:
    // each begins `mov edx,[buf]; add buf,4; cmp edx,<its opcode>`), the same bytes
    // ProcessNetBlobData hands SmartBoxHooks.ParseGameEvent - which caches every update type
    // when the game-event hook is installed. These detours are the fallback for when it isn't.
    private static unsafe void ApplyFromDispatcher(IntPtr buf, uint size)
    {
        if (SmartBoxHooks.IsGameEventHookInstalled || buf == IntPtr.Zero || size < 9 || size > 0x1000)
            return;
        if (PropertyWire.TryParsePropertyUpdate(new ReadOnlySpan<byte>((void*)buf, (int)size), out PropertyUpdate u))
            Apply(u);
    }

    private static uint DetourUpdateInt(IntPtr uiPtr, IntPtr bufPtr, uint size)
    {
        try { ApplyFromDispatcher(bufPtr, size); } catch { }
        return _origUpdateInt!(uiPtr, bufPtr, size);
    }

    private static uint DetourUpdateBool(IntPtr uiPtr, IntPtr bufPtr, uint size)
    {
        try { ApplyFromDispatcher(bufPtr, size); } catch { }
        return _origUpdateBool!(uiPtr, bufPtr, size);
    }

    private static uint DetourPrivateUpdateInt(IntPtr uiPtr, IntPtr bufPtr, uint size)
    {
        try { ApplyFromDispatcher(bufPtr, size); } catch { }
        return _origPrivateUpdateInt!(uiPtr, bufPtr, size);
    }

    private static uint DetourPrivateUpdateBool(IntPtr uiPtr, IntPtr bufPtr, uint size)
    {
        try { ApplyFromDispatcher(bufPtr, size); } catch { }
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
                    {
                        EvictObject(objectId);
                        // A client release keeps the identify data a while (a server delete,
                        // 0xF747, evicts it): see ReleasedObjectRetention.
                        AppraisalHooks.OnClientRelease(objectId, Environment.TickCount64);
                    }
                }
            }
        }
        catch { }
        return _origBeingDeleted!(weenieObjPtr);
    }
}
