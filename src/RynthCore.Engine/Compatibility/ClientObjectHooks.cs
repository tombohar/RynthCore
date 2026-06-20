using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace RynthCore.Engine.Compatibility;

internal static class ClientObjectHooks
{
    private const int ReferenceGetWeenieObject = 0x005583F0;

    // ACCWeenieObject::GetNumContainedItems() — ThisCall, no args, returns int
    // Source: Chorizite Weenie.cs (0x0058CCE0)
    private const int ReferenceGetNumContainedItems = 0x0058CCE0;

    // ACCWeenieObject::GetNumContainedContainers() — ThisCall, no args, returns int
    // Source: Chorizite Weenie.cs (0x0058CCF0)
    private const int ReferenceGetNumContainedContainers = 0x0058CCF0;
    private const int ReferenceGetObjectNameStatic = 0x0058F840;
    private const int ReferenceGetObjectNameInstance = 0x0058F510;

    // ACCWeenieObject::InqType() — returns ITEM_TYPE flags for the object (ThisCall, no args)
    private const int ReferenceInqType = 0x0058D700;

    // CBaseQualities::InqInt(UInt32 stype, int* retval, int raw, int allow_negative) — ThisCall
    // Expects CBaseQualities* as `this` — apply CBaseQualitiesOffset to CACQualities* first.
    private const int ReferenceInqInt = 0x00590C20;

    // CBaseQualities::InqFloat(UInt32 stype, double* retval, int raw) — ThisCall
    private const int ReferenceInqFloat = 0x00590CD0;

    // CBaseQualities::InqInt64(UInt32 stype, __int64* retval) — ThisCall
    private const int ReferenceInqInt64 = 0x00590C70;

    // CACQualities::InqAttribute2nd(ulong stype, uint* retval, int raw) — ThisCall on CACQualities* (no CBaseQualitiesOffset)
    // stype: 1=MAX_HEALTH, 3=MAX_STAMINA, 5=MAX_MANA. raw=0 returns base+gear+aug (no spell enchants).
    // Map: 00191D20 → live VA: 0x00592D20 (offset +0x401000)
    private const int ReferenceInqAttribute2ndBaseLevel = 0x00592D20;


    // CBaseQualities::InqBool(UInt32 stype, int* retval) — ThisCall
    private const int ReferenceInqBool = 0x00590CA0;

    // CBaseQualities::InqString(UInt32 stype, AC1Legacy::PStringBase<char>& retval) — ThisCall
    // enum_Entrypoint address from Chorizite Weenie.cs (.text=0x00590CF0, enum=0x005919F0).
    // PDB returns the .text address — must use the enum address like InqInt/InqBool/InqFloat.
    private const int ReferenceInqString = 0x005919F0;

    // AC1Legacy.PStringBase<char>.s_NullBuffer — pointer to the default PSRefBuffer<char>*.
    // PStringBase<char> is a 4-byte struct { PSRefBuffer<char>* m_buffer }.
    // InqString's operator= calls Release() on the old m_buffer before assigning — if m_buffer
    // is zero (uninitialized), it dereferences null and crashes. Must initialize with s_NullBuffer.
    private const int PStringBaseNullBuffer = 0x008EF11C;

    // ClientCombatSystem::GetCombatSystem() — Cdecl, no args, returns ClientCombatSystem*
    private const int ReferenceGetCombatSystem = 0x0056B210;

    // ClientCombatSystem::ObjectIsAttackable(uint objectId) — ThisCall, returns byte (0/1)
    private const int ReferenceObjectIsAttackable = 0x0056B340;

    // CACQualities::IsSpellKnown(uint spellId) — returns 1 if spell is in the character's spell book
    private const int ReferenceIsSpellKnown = 0x0058FCF0;

    // CACQualities::InqSkill(uint stype, int* retval, int raw) — raw=0 returns buffed level, raw=1 returns base
    // Using InqSkill (0x00593380) instead of InqSkillLevel (0x00592B40) — same result with explicit raw flag
    private const int ReferenceInqSkillLevel = 0x00593380;

    // CACQualities::InqSkillAdvancementClass(uint stype, SKILL_ADVANCEMENT_CLASS* retval)
    // SKILL_ADVANCEMENT_CLASS: UNDEF=0, UNTRAINED=1, TRAINED=2, SPECIALIZED=3
    private const int ReferenceInqSkillAdvancementClass = 0x00592B70;

    // CACQualities::InqAttribute(uint stype, uint* retval, int raw) — raw=0→buffed, raw=1→base
    // stype: 1=Strength, 2=Endurance, 3=Quickness, 4=Coordination, 5=Focus, 6=Self
    private const int ReferenceInqAttribute = 0x00592700;

    // CACQualities::GetVitaeValue() — ThisCall, no args, returns float (1.0=no vitae, 0.95=5% penalty)
    // Map: 0018EE80 → live VA: 0x0058FE80
    private const int ReferenceGetVitaeValue = 0x0058FE80;
    private const int NameTypeSingular = 0;
    private const int MaxLookupLogs = 12;

    // ACCWeenieObject._phys_obj offset (CPhysicsObj pointer within the weenie)
    // Auto-discovered at runtime by ProbePhysObjOffset. Fallback = 0x94 (confirmed 2026-04-02).
    private const int FallbackWeeniePhysicsObjOffset = 0x94;
    private static int _weeniePhysicsObjOffset = -1; // -1 = not yet probed

    // ACCWeenieObject.m_pQualities offset (PlayerDesc* pointer, which starts with CACQualities).
    // CACQualities::InqSkill/IsSpellKnown require the CACQualities* (== PlayerDesc*), NOT the weenie ptr.
    // Fallback = 0x94 + 4 + sizeof(PublicWeenieDesc=176) + 4 (ACWTimeStamper*) = 0x14C.
    // Auto-discovered at runtime via ProbeQualitiesOffset once a known PlayerDesc* is available.
    private const int FallbackWeenieQualitiesOffset = 0x14C;

    // Offset from CACQualities* (== PlayerDesc*) to its CBaseQualities sub-object.
    // CACQualities layout (MSVC /Zp8): DBObj(48) + PackObj vtable(4) + padding(4) = 56.
    // The adjustor{48} in Chorizite is for the PackObj base, NOT CBaseQualities.
    // Confirmed empirically: vtable scan shows CBaseQualities vtable at +0x38 (56).
    // CBaseQualities::InqInt/InqFloat/InqBool require this offset applied.
    private const int CBaseQualitiesOffset = 56;

    // CACQualities::_skillStatsTable from the local Chorizite layout:
    // SerializeUsingPackDBObj (0x38) + CBaseQualities (0x28) + _attribCache (0x04) = 0x64.
    private const int SkillStatsTableOffset = 0x64;
    private static int _weenieQualitiesOffset = -1;
    private static int _weenieNullCount = 0;

    /// <summary>
    /// The resolved offset from ACCWeenieObject* to its m_pQualities (CACQualities*).
    /// Returns fallback (0x14C) if not yet probed. Used by EnchantmentHooks for item reads.
    /// </summary>
    internal static int WeenieQualitiesOffset
    {
        get
        {
            if (_weenieQualitiesOffset < 0)
                return FallbackWeenieQualitiesOffset;
            return _weenieQualitiesOffset;
        }
    }
    private static IntPtr _pendingPlayerDescPtr = IntPtr.Zero;
    private static int _skillProbeLogCount;

    // Cold-start lazy-reseed throttle. On a normal launch the player's
    // CACQualities* is seeded by the SendNoticePlayerDescReceived detour
    // during login. If auto-login reaches in-world before that hook is armed
    // (fast launch / mid-session inject), the ptr stays zero all session and
    // every player Inq* (skills/attributes/enchantments) silently fails.
    // Hot-reload and Decal paths reseed explicitly; the normal cold path had
    // no fallback. We self-heal in TryGetObjectQualitiesPtr, throttled so a
    // per-tick skill poll doesn't run the full reseed every frame.
    private static DateTime _lastLazyQualitiesReseedUtc = DateTime.MinValue;
    private static bool _loggedLazyQualitiesReseed;
    private const int LazyQualitiesReseedThrottleMs = 1000;

    // Main-thread player-skill snapshot cache. As of 2026-05-16 the plugin
    // tick NEVER runs on AC's main thread (it's pumped from a managed worker
    // to keep a GC off AC's reverse-P/Invoke thread). But every player skill
    // read funnels through TryGetObjectQualitiesPtr, which fail-closes off
    // the main thread for AV-safety. Net: every plugin skill read would
    // return (0,0) → tier-1 casts + "skill not usable" → bot never buffs.
    // Fix: refresh skills ON the main thread (PrefetchPlayerSkills, driven
    // from the EndScene always-on path) into this cache, and serve it to the
    // off-thread plugin pump. Reads still only ever touch AC on the main
    // thread — the cache is the only thing crossing threads.
    private static readonly object _playerSkillCacheLock = new();
    private static readonly Dictionary<uint, (int buffed, int training)> _playerSkillCache = new();
    private static uint _playerSkillCacheOwner;
    private static DateTime _lastPlayerSkillPrefetchUtc = DateTime.MinValue;
    private static bool _loggedSkillCacheServe;
    private const int PlayerSkillPrefetchThrottleMs = 1000;

    // Main-thread player-stats snapshot (XP / luminance / deaths / vitae) — same off-thread-safe
    // pattern as the skill cache. These are all main-thread-only Inq* reads, so the off-thread
    // plugin pump (and StatusSnapshotWriter) can't read them live; snapshot here on the EndScene
    // path and serve the cache. [status-export]
    private static readonly object _playerStatsCacheLock = new();
    private static long _cachedTotalXp;
    private static long _cachedLuminance;
    private static int _cachedDeaths;
    private static float _cachedVitae = 1.0f;
    private static uint _playerStatsCacheOwner;
    private static DateTime _lastPlayerStatsPrefetchUtc = DateTime.MinValue;
    private const int PlayerStatsPrefetchThrottleMs = 1000;

    // Main-thread known-spell (spellbook) snapshot — same off-thread-safe
    // pattern as the skill cache above. AC's spellbook is a PackableHashTable
    // at [CACQualities+0x6C] (buckets +0x0C, count +0x10, node key +0x00,
    // next +0x0C — confirmed by disasm of CACQualities::IsSpellKnown @
    // 0x596420). Read on the main thread (TryGetObjectQualitiesPtr fail-closes
    // off-thread) and served as a snapshot to the off-thread plugin pump.
    private static readonly object _knownSpellCacheLock = new();
    private static readonly HashSet<uint> _knownSpellCache = new();
    private static uint _knownSpellCacheOwner;
    private static DateTime _lastKnownSpellPrefetchUtc = DateTime.MinValue;
    private static bool _loggedKnownSpellServe;
    private const int KnownSpellPrefetchThrottleMs = 2000;
    private const int SpellBookTableOffset = 0x6C;

    // Attackable snapshot — see ObjectIsAttackable / PrefetchAttackable. The
    // off-thread plugin pump can't call AC's combat system safely (cross-thread
    // AV class), so the REAL ClientCombatSystem::ObjectIsAttackable bit is
    // sampled on AC's main thread (EndScene path) and served from here. Rebuilt
    // from scratch each prefetch so dead ids self-evict (mirrors the spellbook
    // snapshot). Without this the pump got hardcoded true → NPCs/vendors were
    // promoted to attackable creatures and the bot cast war magic at them.
    private static readonly object _attackableCacheLock = new();
    private static readonly Dictionary<uint, bool> _attackableCache = new();
    private static DateTime _lastAttackablePrefetchUtc = DateTime.MinValue;
    private static bool _loggedAttackableServe;
    // 2026-05-18 DIAGNOSTIC: raised 1000→8000 to 8x-reduce this per-cycle
    // full-CObjectMaint-table walk + per-object _getWeenieObject/ObjectIsAttackable
    // calls, as empirical isolation of the object-teardown AV class
    // (DBOCache::DestroyObj / List<ObjectRangeInfo>::remove — dump-verified, NOT
    // cast). NPC protection is preserved (cache stays warm; entries refresh
    // every 8s; the AttackWithMagic veto + WorldObjectCache still consult it).
    // Trade-off while diagnosing: a brand-new mob may take up to ~8s to become
    // attackable. If crash frequency drops materially with this, this walk is
    // implicated → redesign incremental (compute-once-per-id + evict on delete);
    // if unchanged, it's exonerated → move to the object create/delete plumbing.
    // 2026-05-21: dump-verified the crashing CObjectMaint walk is the OFF-THREAD
    // _getWeenieObject in un-guarded position/property reads (pump thread 0x5698 @
    // 0x67E779), NOT this main-thread prefetch walk — so the 8000ms diagnostic was
    // throttling the wrong (already-safe) walk. Lowered to keep the off-thread
    // snapshot fresh for swarm churn now that the pump is fully gated to it.
    private const int AttackablePrefetchThrottleMs = 500;

    // Main-thread object name/type snapshot — mirrors PrefetchAttackable.
    // Off-thread classifier (WorldObjectCache) reads name/type from here
    // instead of walking AC's live CObjectMaint table → avoids the
    // 0x0067E779 READ-AV class (cross-thread object-graph access).
    private static readonly object _objectIdentityCacheLock = new();
    private static readonly Dictionary<uint, string> _objectNameCache = new();
    private static readonly Dictionary<uint, uint> _objectTypeCache = new();
    private static DateTime _lastObjectIdentityPrefetchUtc = DateTime.MinValue;
    private static bool _loggedObjectIdentityServe;
    private const int ObjectIdentityPrefetchThrottleMs = 500;

    // Position snapshot — mirrors the attackable/identity snapshots, but sampled
    // EVERY EndScene (no throttle) because positions change per-frame. The
    // off-thread plugin pump reads position from here instead of resolving
    // _getWeenieObject(id) live — that resolution is AC's CObjectMaint hash walk
    // and is the dump-verified 0x0067E779 READ-AV when done off the main thread.
    // MUST stay ZERO-ALLOC (reused dict via Clear()+re-add, cached enumerate
    // delegate, struct values) so per-frame capture in the EndScene reverse-
    // P/Invoke can't trigger a GC → NativeAOT fail-fast (the same hazard that
    // moved TickAll off this thread; see EngineFrameController.OnEndScene).
    private struct PosEntry { public uint Cell; public float X, Y, Z; }
    private static readonly object _positionCacheLock = new();
    private static readonly Dictionary<uint, PosEntry> _positionCache = new(512);
    private static bool _loggedPositionServe;
    private static readonly Action<uint> _capturePositionDelegate = CapturePositionForId;
    private static DateTime _lastPositionPrefetchUtc = DateTime.MinValue;
    // The per-frame full walk (CObjectMaint enumerate + per-object _getWeenieObject)
    // on the RENDER thread at ~60Hz was ~30x the rate of the other prefetches
    // (attackable/identity at 500ms) — far more render-thread load than necessary.
    // 100ms (10Hz) keeps positions fresh for combat while bringing the render-
    // thread cost in line with the other walks. (Positions are volatile, but AC's
    // server position updates are coarser than 10Hz anyway.)
    private const int PositionPrefetchThrottleMs = 100;

    // Off-thread weenie-pointer cache (the COMPLETE gate for the 0x0067E779
    // CObjectMaint READ-AV). The pump must NEVER call _getWeenieObject live; the
    // main-thread position walk records every live id->weeniePtr into
    // _weeniePtrBack, then publishes it to _weeniePtrFront via an atomic swap.
    // GetWeenieObjectResolve serves _weeniePtrFront off-thread. Double-buffered
    // so off-thread reads take only the brief swap lock and never block on the
    // walk (the per-frame single-lock walk previously stalled the render thread).
    private static readonly object _weeniePtrSwapLock = new();
    private static Dictionary<uint, IntPtr> _weeniePtrFront = new(512);
    private static Dictionary<uint, IntPtr> _weeniePtrBack = new(512);
    // Diagnostic (temporary): periodic snapshot-size log + live-read probe of
    // objects with a position but no cached name. Remove once the missing-
    // monsters cause is pinned. _diagSampleBuf preallocated to stay low-alloc.
    private static DateTime _lastSnapshotDiagUtc = DateTime.MinValue;
    private const int SnapshotDiagThrottleMs = 10000;
    private static readonly uint[] _diagSampleBuf = new uint[16];
    // object_table id collection buffer for the table-comparison diagnostic.
    private static readonly uint[] _diagObjTableIds = new uint[1024];
    private static int _diagObjTableN;
    private static readonly Action<uint> _diagObjTableVisit = DiagObjTableVisit;
    private static void DiagObjTableVisit(uint id)
    {
        if (_diagObjTableN < _diagObjTableIds.Length)
            _diagObjTableIds[_diagObjTableN++] = id;
    }

    // CPhysicsObj.m_position offset (same as PlayerPhysicsHooks.PhysicsPositionOffset)
    private const int PhysicsPositionOffset = 0x48;
    private const int PositionObjCellIdOffset = 0x04;
    private const int PositionQwOffset = 0x08;
    private const int PositionQzOffset = 0x14;
    private const int PositionOriginXOffset = 0x3C;
    private const int PositionOriginYOffset = 0x40;
    private const int PositionOriginZOffset = 0x44;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetWeenieObjectDelegate(uint objectId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetObjectNameStaticDelegate(IntPtr weenieObjPtr, uint objectId, int nameType, int playerIsBackpack);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate IntPtr GetObjectNameInstanceDelegate(IntPtr weenieObjPtr, int nameType, int playerIsBackpack);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate uint InqTypeDelegate(IntPtr weenieObjPtr);

    [StructLayout(LayoutKind.Sequential)]
    private struct PackObjNative
    {
        public IntPtr Vfptr;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SkillNative
    {
        public PackObjNative PackObj;
        public uint AdvancementClass;
        public uint PracticePoints;
        public uint InitialLevel;
        public uint LevelFromPracticePoints;
        public int ResistanceOfLastCheck;
        public double LastUsedTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PackableHashTableUInt32SkillNative
    {
        public PackObjNative PackObj;
        public int ThrowawayDuplicateKeysOnUnpack;
        public IntPtr Buckets;
        public uint TableSize;
        public uint Count;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PackableHashDataUInt32SkillNative
    {
        public uint Key;
        public SkillNative Data;
        public IntPtr Next;
        public int HashValue;
    }

    // int __thiscall CBaseQualities::InqInt(unsigned int stype, int* retval, int raw, int allow_negative)
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private unsafe delegate int InqIntDelegate(IntPtr qualitiesPtr, uint stype, int* retval, int raw, int allowNegative);

    // int __thiscall CBaseQualities::InqInt64(unsigned int stype, __int64* retval)
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private unsafe delegate int InqInt64Delegate(IntPtr qualitiesPtr, uint stype, long* retval);

    // int __thiscall CACQualities::InqAttribute2nd(ulong stype, uint* retval, int raw)
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private unsafe delegate int InqAttribute2ndBaseLevelDelegate(IntPtr cacQualitiesPtr, uint stype, uint* retval, int raw);


    // int __thiscall CBaseQualities::InqFloat(unsigned int stype, double* retval, int raw)
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private unsafe delegate int InqFloatDelegate(IntPtr qualitiesPtr, uint stype, double* retval, int raw);

    // int __thiscall CBaseQualities::InqBool(unsigned int stype, int* retval)
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private unsafe delegate int InqBoolDelegate(IntPtr qualitiesPtr, uint stype, int* retval);

    // int __thiscall CBaseQualities::InqString(unsigned int stype, PStringBase<char>* retval)
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private unsafe delegate int InqStringDelegate(IntPtr qualitiesPtr, uint stype, byte* pstringOut);

    // int __thiscall CACQualities::InqSkill(unsigned int stype, int* retval, int raw)  raw=0→buffed, raw=1→base
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private unsafe delegate int InqSkillLevelDelegate(IntPtr qualitiesPtr, uint stype, int* retval, int raw);

    // int __thiscall CACQualities::InqSkillAdvancementClass(unsigned int stype, int* retval)
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private unsafe delegate int InqSkillAdvancementClassDelegate(IntPtr qualitiesPtr, uint stype, int* retval);

    // int __thiscall CACQualities::InqAttribute(unsigned int stype, unsigned int* retval, int raw)  raw=0→buffed, raw=1→base
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private unsafe delegate int InqAttributeDelegate(IntPtr qualitiesPtr, uint stype, uint* retval, int raw);

    // int __thiscall CACQualities::IsSpellKnown(uint spellId)
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int IsSpellKnownDelegate(IntPtr qualitiesPtr, uint spellId);

    // float __thiscall CACQualities::GetVitaeValue()
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate float GetVitaeValueDelegate(IntPtr qualitiesPtr);

    // ClientCombatSystem* __cdecl ClientCombatSystem::GetCombatSystem()
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetCombatSystemDelegate();

    // byte __thiscall ClientCombatSystem::ObjectIsAttackable(uint objectId)
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate byte ObjectIsAttackableDelegate(IntPtr combatSystemPtr, uint objectId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int VirtualQuery(IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, int dwLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    private const uint MEM_COMMIT = 0x1000;
    private const uint PAGE_NOACCESS = 0x01;
    private const uint PAGE_GUARD = 0x100;

    /// <summary>
    /// Checks if a pointer's memory page is committed and readable via VirtualQuery.
    /// Critical for NativeAOT where try/catch does NOT catch access violations.
    /// </summary>
    internal static bool IsReadablePointer(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero)
            return false;
        if (VirtualQuery(ptr, out var mbi, Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == 0)
            return false;
        if (mbi.State != MEM_COMMIT)
            return false;
        if ((mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) != 0)
            return false;
        return true;
    }

    /// <summary>
    /// Stronger check than <see cref="IsReadablePointer"/>: confirms the pointer
    /// looks like a heap-allocated AC C++ object. Every CACQualities /
    /// ACCWeenieObject / etc. instance starts with a vtable pointer whose value
    /// is the address of a vtable in acclient.exe's .rdata. If the first 4
    /// bytes don't resolve to an address inside the acclient.exe module
    /// window, the pointer isn't pointing at a real C++ object and using it
    /// will AV downstream inside AC's code.
    ///
    /// Caller's job is the page-readability check; this only re-validates
    /// after reading.
    /// </summary>
    internal static bool LooksLikeAcHeapObject(IntPtr ptr)
    {
        return TryReadAcObjectVtable(ptr, out _);
    }

    /// <summary>
    /// Same vtable-in-module check as <see cref="LooksLikeAcHeapObject"/> but
    /// also yields the vtable pointer so callers can compare against a known
    /// reference vtable (e.g. "is this the same C++ class as the player's
    /// CACQualities").
    /// </summary>
    internal static bool TryReadAcObjectVtable(IntPtr ptr, out IntPtr vtable)
    {
        vtable = IntPtr.Zero;
        if (!IsReadablePointer(ptr))
            return false;

        try
        {
            vtable = Marshal.ReadIntPtr(ptr);
        }
        catch
        {
            vtable = IntPtr.Zero;
            return false;
        }

        if (vtable == IntPtr.Zero || vtable.ToInt64() < 0x10000)
        {
            vtable = IntPtr.Zero;
            return false;
        }

        return SmartBoxLocator.IsPointerInModule(vtable);
    }

    /// <summary>
    /// The vtable pointer at offset 0 of a real CACQualities object.
    /// Discovered the first time we see the player's known-good qualities ptr,
    /// then used to filter out same-shape-but-wrong-class pointers we may
    /// read from non-player weenies whose m_pQualities offset is different
    /// (or whose qualities slot holds a sibling C++ object that happens to
    /// also live in the module). Once captured, ANY pointer we treat as
    /// CACQualities must have *ptr == _cacQualitiesVtable.
    /// </summary>
    private static IntPtr _cacQualitiesVtable;

    /// <summary>
    /// True if the given pointer's first 4 bytes match the cached
    /// CACQualities vtable. Falls back to permissive vtable-in-module
    /// check until we've observed a known-good qualities pointer to learn
    /// the canonical vtable.
    /// </summary>
    private static bool IsCacQualitiesObject(IntPtr ptr)
    {
        if (!TryReadAcObjectVtable(ptr, out IntPtr vtable))
            return false;

        if (_cacQualitiesVtable == IntPtr.Zero)
            return true; // not yet calibrated — accept any module-vtable object.

        return vtable == _cacQualitiesVtable;
    }

    /// <summary>
    /// Capture the canonical CACQualities vtable from a known-good qualities
    /// pointer (the player's). Idempotent.
    /// </summary>
    private static void CaptureCacQualitiesVtable(IntPtr knownGoodPtr)
    {
        if (_cacQualitiesVtable != IntPtr.Zero)
            return;
        if (!TryReadAcObjectVtable(knownGoodPtr, out IntPtr vtable))
            return;
        _cacQualitiesVtable = vtable;
        RynthLog.Compat($"ClientObjectHooks: CACQualities vtable captured = 0x{vtable.ToInt32():X8} (from known player qualities ptr 0x{knownGoodPtr.ToInt32():X8}).");
    }

    private static string _statusMessage = "Not probed yet.";
    private static GetWeenieObjectDelegate? _getWeenieObject;
    // Raw native resolver. _getWeenieObject (above) is a GATED wrapper
    // (GetWeenieObjectResolve) that calls this only on AC's main thread; off the
    // main thread it serves a cached pointer instead of walking AC's table.
    private static GetWeenieObjectDelegate? _getWeenieObjectNative;
    private static GetObjectNameStaticDelegate? _getObjectNameStatic;
    private static GetObjectNameInstanceDelegate? _getObjectNameInstance;
    private static InqTypeDelegate? _inqType;
    private static InqIntDelegate? _inqInt;
    private static InqInt64Delegate? _inqInt64;
    private static InqAttribute2ndBaseLevelDelegate? _inqAttribute2ndBaseLevel;
    private static InqFloatDelegate? _inqFloat;
    private static InqBoolDelegate? _inqBool;
    private static InqStringDelegate? _inqString;
    private static GetCombatSystemDelegate? _getCombatSystem;
    private static ObjectIsAttackableDelegate? _objectIsAttackable;
    private static InqSkillLevelDelegate? _inqSkillLevel;
    private static InqSkillAdvancementClassDelegate? _inqSkillAdvancementClass;
    private static InqAttributeDelegate? _inqAttribute;
    private static IsSpellKnownDelegate? _isSpellKnown;
    private static GetVitaeValueDelegate? _getVitaeValue;
    private static int _lookupLogCount;

    // Raw native function pointers cached alongside the delegates above.
    // Passed to SehTrampoline so the SEH-wrapped call goes directly to AC's
    // native code — no managed round-trip through the delegate's reverse-stub.
    private static IntPtr _getWeenieObjectPtr;
    private static IntPtr _getCombatSystemPtr;
    private static IntPtr _objectIsAttackablePtr;
    private static IntPtr _inqTypePtr;
    // AV log throttle — one message per crash site per session is enough.
    private static int _sehAvLogCount;

    public static bool IsInitialized { get; private set; }
    public static string StatusMessage => _statusMessage;

    public static bool Probe()
    {
        bool ready = SmartBoxLocator.Probe();
        if (ready)
            ready = BindDelegates();

        if (ready)
        {
            IsInitialized = true;
            _statusMessage = "Ready.";
            RynthLog.Verbose($"Compat: client objects ready - smartbox candidates={SmartBoxLocator.CandidateCount}");

        }
        else
        {
            IsInitialized = false;
            if (_getWeenieObject != null || _getObjectNameStatic != null || _getObjectNameInstance != null)
            {
                _getWeenieObject = null;
                _getWeenieObjectNative = null;
                _getObjectNameStatic = null;
                _getObjectNameInstance = null;
                _inqType = null;
                _inqInt   = null;
                _inqInt64 = null;
                _inqAttribute2ndBaseLevel = null;
                _inqBool  = null;
                _inqString = null;
                _getCombatSystem = null;
                _objectIsAttackable = null;
                _inqSkillLevel = null;
                _inqSkillAdvancementClass = null;
                _inqAttribute = null;
                _isSpellKnown = null;
            }

            if (_statusMessage == "Not probed yet.")
                _statusMessage = SmartBoxLocator.StatusMessage;
        }

        return ready;
    }

    // --- Pattern-resolved binding (1a hardening; re-applied 2026-06-06 after an early
    // copy was lost in a git checkout/commit during the crash fix) -----------------
    // Each Reference* VA above is now only a FALLBACK. HookResolver pattern-scans the live
    // acclient .text for these signatures (verified UNIQUE + landing exactly at the fallback
    // VA offline via tools/pe_pattern.py, over the same 4 MB window AcClientModule reads),
    // so binding survives AC-patch / ACE-rebuild drift. null = wildcard (rel32 operand).
    private static readonly byte?[] PatGetWeenieObject = [ 0x8B, 0x0D, 0xDC, 0x2A, 0x84, 0x00, 0x85, 0xC9, 0x74, 0x0B, 0x8B, 0x44, 0x24, 0x04, 0x50, 0xE8, null, null, null, null, 0xC3, 0x33, 0xC0, 0xC3, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x8B, 0x0D ];
    private static readonly byte?[] PatGetNumContainedItems = [ 0x8B, 0x41, 0x50, 0x85, 0xC0, 0x74, 0x04, 0x8B, 0x40, 0x1C ];
    private static readonly byte?[] PatGetNumContainedContainers = [ 0x8B, 0x41, 0x50, 0x85, 0xC0, 0x74, 0x04, 0x8B, 0x40, 0x34 ];
    private static readonly byte?[] PatGetObjectNameStatic = [ 0x8B, 0x44, 0x24, 0x0C, 0x85, 0xC0, 0x8B, 0x44, 0x24, 0x04, 0x74 ];
    private static readonly byte?[] PatGetObjectNameInstance = [ 0x83, 0xEC, 0x0C, 0x8B, 0x44, 0x24, 0x14, 0x85 ];
    private static readonly byte?[] PatInqType = [ 0x8B, 0x81, 0xD0, 0x00, 0x00, 0x00, 0xC3, 0x90 ];
    private static readonly byte?[] PatInqInt = [ 0x56, 0x8B, 0xF1, 0x8B, 0x4E, 0x08, 0x85, 0xC9, 0x74, 0x0E ];
    private static readonly byte?[] PatInqFloat = [ 0x56, 0x8B, 0xF1, 0x8B, 0x4E, 0x14, 0x85, 0xC9, 0x74, 0x0E ];
    private static readonly byte?[] PatInqInt64 = [ 0x8B, 0x49, 0x0C, 0x85, 0xC9, 0x74, 0x0E, 0x8D ];
    private static readonly byte?[] PatInqAttribute2ndBaseLevel = [ 0x51, 0x53, 0x55, 0x57, 0x8B, 0x7C, 0x24, 0x14 ];
    private static readonly byte?[] PatInqBool = [ 0x8B, 0x49, 0x10, 0x85, 0xC9, 0x74, 0x0E, 0x8D ];
    private static readonly byte?[] PatInqString = [ 0x8B, 0x49, 0x18, 0x85, 0xC9, 0x57, 0x74, 0x10 ];
    private static readonly byte?[] PatGetCombatSystem = [ 0xA1, 0x6C, 0x16, 0x87, 0x00, 0xC3, 0x90, 0x90 ];
    private static readonly byte?[] PatObjectIsAttackable = [ 0x8B, 0x4C, 0x24, 0x04, 0x85, 0xC9, 0x74, 0x17 ];
    private static readonly byte?[] PatIsSpellKnown = [ 0x8B, 0x49, 0x6C, 0x85, 0xC9, 0x74, 0x05, 0xE9, null, null, null, null, 0x33, 0xC0, 0xC2, 0x04, 0x00, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x8B, 0x49 ];
    private static readonly byte?[] PatInqSkillLevel = [ 0x8B, 0x44, 0x24, 0x0C, 0x83, 0xEC, 0x0C, 0x53, 0x55, 0x8B ];
    private static readonly byte?[] PatInqSkillAdvancementClass = [ 0x8B, 0x49, 0x64, 0x85, 0xC9, 0x74, 0x0E, 0x8D, 0x44, 0x24, 0x04, 0x50, 0xE8, null, null, null, null, 0x85, 0xC0, 0x75, 0x05, 0x33, 0xC0, 0xC2, 0x08, 0x00, 0x8B, 0x48 ];
    private static readonly byte?[] PatInqAttribute = [ 0x53, 0x8B, 0xD9, 0x8B, 0x4B, 0x60, 0x85, 0xC9 ];
    private static readonly byte?[] PatGetVitaeValue = [ 0x8B, 0x49, 0x70, 0x85, 0xC9, 0x75, 0x07, 0xD9 ];

    private static IntPtr _getNumContainedItemsPtr;
    private static IntPtr _getNumContainedContainersPtr;

    // Phase B: PStringBase<char>::s_NullBuffer (0x008EF11C) resolved by code-xref
    // (cmp edi,[s_NullBuffer] = 3B 3D <addr>), operand at offset 2; VA stays fallback.
    private static readonly byte?[] PatXrefPStringNullBuffer = [ 0x3B, 0x3D, null, null, null, null, 0x74, 0xF1 ];
    private static IntPtr _pStringNullBufferAddr = new(PStringBaseNullBuffer);

    private static IntPtr ResolveAddr(AcClientTextSection text, string name, byte?[] pattern, int fallbackVa)
    {
        HookResolver.ResolveResult r = HookResolver.Resolve(text, name, pattern, fallbackVa);
        return r.Success ? r.Address : IntPtr.Zero;
    }

    private static T? BindResolved<T>(AcClientTextSection text, string name, byte?[] pattern, int fallbackVa, out IntPtr resolvedPtr) where T : Delegate
    {
        resolvedPtr = ResolveAddr(text, name, pattern, fallbackVa);
        return resolvedPtr == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(resolvedPtr);
    }

    private static bool BindDelegates()
    {
        if (_getWeenieObject != null && _getObjectNameStatic != null && _getObjectNameInstance != null)
            return true;

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection text))
        {
            _statusMessage = "ClientObject: acclient.exe .text not readable for pattern resolve.";
            RynthLog.Compat($"Compat: client object bind failed - {_statusMessage}");
            return false;
        }

        IntPtr getWeeniePtr = ResolveAddr(text, "ClientObject.GetWeenieObject", PatGetWeenieObject, ReferenceGetWeenieObject);
        IntPtr getNameStaticPtr = ResolveAddr(text, "ClientObject.GetObjectNameStatic", PatGetObjectNameStatic, ReferenceGetObjectNameStatic);
        IntPtr getNameInstancePtr = ResolveAddr(text, "ClientObject.GetObjectNameInstance", PatGetObjectNameInstance, ReferenceGetObjectNameInstance);
        
        if (!SmartBoxLocator.IsPointerInModule(getWeeniePtr) ||
            !SmartBoxLocator.IsPointerInModule(getNameStaticPtr) ||
            !SmartBoxLocator.IsPointerInModule(getNameInstancePtr))
        {
            _statusMessage =
                $"ClientObject pointers look invalid (getWeenie=0x{getWeeniePtr.ToInt32():X8}, getNameStatic=0x{getNameStaticPtr.ToInt32():X8}, getNameInstance=0x{getNameInstancePtr.ToInt32():X8}).";
            RynthLog.Compat($"Compat: client object bind failed - {_statusMessage}");
            return false;
        }

        _getWeenieObjectNative = Marshal.GetDelegateForFunctionPointer<GetWeenieObjectDelegate>(getWeeniePtr);
        // _getWeenieObject is the GATED wrapper (see GetWeenieObjectResolve):
        // native walk on AC's main thread (and cache the ptr), cached-ptr serve
        // off-thread so the pump never walks CObjectMaint (0x0067E779 READ-AV).
        _getWeenieObject = GetWeenieObjectResolve;
        _getWeenieObjectPtr = getWeeniePtr;
        _getObjectNameStatic = Marshal.GetDelegateForFunctionPointer<GetObjectNameStaticDelegate>(getNameStaticPtr);
        _getObjectNameInstance = Marshal.GetDelegateForFunctionPointer<GetObjectNameInstanceDelegate>(getNameInstancePtr);
        _inqType = BindResolved<InqTypeDelegate>(text, "ClientObject.InqType", PatInqType, ReferenceInqType, out _inqTypePtr);
        _inqInt = BindResolved<InqIntDelegate>(text, "ClientObject.InqInt", PatInqInt, ReferenceInqInt, out _);
        _inqInt64 = BindResolved<InqInt64Delegate>(text, "ClientObject.InqInt64", PatInqInt64, ReferenceInqInt64, out _);
        _inqAttribute2ndBaseLevel = BindResolved<InqAttribute2ndBaseLevelDelegate>(text, "ClientObject.InqAttribute2nd", PatInqAttribute2ndBaseLevel, ReferenceInqAttribute2ndBaseLevel, out _);
        _inqFloat = BindResolved<InqFloatDelegate>(text, "ClientObject.InqFloat", PatInqFloat, ReferenceInqFloat, out _);
        _inqBool = BindResolved<InqBoolDelegate>(text, "ClientObject.InqBool", PatInqBool, ReferenceInqBool, out _);
        _inqString = BindResolved<InqStringDelegate>(text, "ClientObject.InqString", PatInqString, ReferenceInqString, out _);
        _getCombatSystem = BindResolved<GetCombatSystemDelegate>(text, "ClientObject.GetCombatSystem", PatGetCombatSystem, ReferenceGetCombatSystem, out _getCombatSystemPtr);
        _objectIsAttackable = BindResolved<ObjectIsAttackableDelegate>(text, "ClientObject.ObjectIsAttackable", PatObjectIsAttackable, ReferenceObjectIsAttackable, out _objectIsAttackablePtr);
        _inqSkillLevel = BindResolved<InqSkillLevelDelegate>(text, "ClientObject.InqSkillLevel", PatInqSkillLevel, ReferenceInqSkillLevel, out _);
        _inqSkillAdvancementClass = BindResolved<InqSkillAdvancementClassDelegate>(text, "ClientObject.InqSkillAdvancementClass", PatInqSkillAdvancementClass, ReferenceInqSkillAdvancementClass, out _);
        _inqAttribute = BindResolved<InqAttributeDelegate>(text, "ClientObject.InqAttribute", PatInqAttribute, ReferenceInqAttribute, out _);
        _isSpellKnown = BindResolved<IsSpellKnownDelegate>(text, "ClientObject.IsSpellKnown", PatIsSpellKnown, ReferenceIsSpellKnown, out _);
        _getVitaeValue = BindResolved<GetVitaeValueDelegate>(text, "ClientObject.GetVitaeValue", PatGetVitaeValue, ReferenceGetVitaeValue, out _);
        _getNumContainedItemsPtr = ResolveAddr(text, "ClientObject.GetNumContainedItems", PatGetNumContainedItems, ReferenceGetNumContainedItems);
        _getNumContainedContainersPtr = ResolveAddr(text, "ClientObject.GetNumContainedContainers", PatGetNumContainedContainers, ReferenceGetNumContainedContainers);
        _pStringNullBufferAddr = HookResolver.ResolveData(text, "ClientObject.PStringChar_NullBuffer", PatXrefPStringNullBuffer, 2, PStringBaseNullBuffer).Address;
        RynthLog.Verbose(
            $"Compat: client object hooks ready - getWeenie=0x{getWeeniePtr.ToInt32():X8}, getNameStatic=0x{getNameStaticPtr.ToInt32():X8}, getNameInstance=0x{getNameInstancePtr.ToInt32():X8}");
        return true;
    }

    /// <summary>
    /// Checks whether a spell is in the given object's spell book via CACQualities::IsSpellKnown.
    /// </summary>
    public static bool TryIsSpellKnown(uint objectId, uint spellId, out bool known)
    {
        known = false;
        // Off-thread: refuse — _isSpellKnown is an AC native call that walks
        // qualities, racing with main-thread object teardown
        // (Class A AV in TFile2IDTable::~TFile2IDTable).
        if (!MainThreadGuard.IsOnMainThread())
            return false;
        if (_isSpellKnown == null || _getWeenieObject == null)
        {
            if (!Probe() || _isSpellKnown == null || _getWeenieObject == null)
                return false;
        }
        try
        {
            IntPtr weeniePtr = _getWeenieObject(objectId);
            if (weeniePtr == IntPtr.Zero)
                return false;

            if (!TryGetQualitiesPtr(weeniePtr, out IntPtr qualitiesPtr))
                return false;

            known = _isSpellKnown(qualitiesPtr, spellId) != 0;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the player's vitae multiplier via CACQualities::GetVitaeValue.
    /// Returns 1.0 when there is no vitae penalty. 0.95 = 5% penalty, etc.
    /// </summary>
    public static bool TryGetVitae(uint playerId, out float value)
    {
        value = 1.0f;
        // Off-thread: refuse — _getVitaeValue is an AC native call (Class A guard).
        if (!MainThreadGuard.IsOnMainThread())
            return false;
        if (_getVitaeValue == null || _getWeenieObject == null)
        {
            if (!Probe() || _getVitaeValue == null || _getWeenieObject == null)
                return false;
        }
        try
        {
            IntPtr weeniePtr = _getWeenieObject(playerId);
            if (weeniePtr == IntPtr.Zero)
                return false;

            if (!TryGetQualitiesPtr(weeniePtr, out IntPtr qualitiesPtr))
                return false;

            value = _getVitaeValue(qualitiesPtr);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Called by PlayerVitalsHooks when SendNoticePlayerDescReceived fires, providing the
    /// known CACQualities* (== PlayerDesc*) so we can probe its offset within ACCWeenieObject.
    /// </summary>
    internal static void SetKnownPlayerQualitiesPtr(IntPtr playerDescPtr)
    {
        if (playerDescPtr == IntPtr.Zero || _weenieQualitiesOffset >= 0)
            return;
        _pendingPlayerDescPtr = playerDescPtr;
    }

    private static void ProbeQualitiesOffset()
    {
        IntPtr knownPtr = _pendingPlayerDescPtr;
        _pendingPlayerDescPtr = IntPtr.Zero;

        if (knownPtr == IntPtr.Zero || _getWeenieObject == null)
        {
            _weenieQualitiesOffset = FallbackWeenieQualitiesOffset;
            RynthLog.Verbose($"Compat: qualitiesOffsetProbe no known ptr, using fallback +0x{FallbackWeenieQualitiesOffset:X3}");
            return;
        }

        uint playerId = ClientHelperHooks.GetPlayerId();
        if (playerId == 0)
        {
            _weenieQualitiesOffset = FallbackWeenieQualitiesOffset;
            RynthLog.Verbose($"Compat: qualitiesOffsetProbe playerId==0, using fallback +0x{FallbackWeenieQualitiesOffset:X3}");
            return;
        }

        try
        {
            IntPtr weeniePtr = _getWeenieObject(playerId);
            if (weeniePtr == IntPtr.Zero)
            {
                _weenieQualitiesOffset = FallbackWeenieQualitiesOffset;
                RynthLog.Verbose($"Compat: qualitiesOffsetProbe weenie null, using fallback +0x{FallbackWeenieQualitiesOffset:X3}");
                return;
            }

            int target = knownPtr.ToInt32();
            for (int scan = 0x80; scan <= 0x200; scan += 4)
            {
                try
                {
                    int val = Marshal.ReadInt32(weeniePtr + scan);
                    if (val == target)
                    {
                        _weenieQualitiesOffset = scan;
                        RynthLog.Verbose($"Compat: qualitiesOffsetProbe found m_pQualities at +0x{scan:X3} (player=0x{playerId:X8})");
                        return;
                    }
                }
                catch { break; }
            }

            _weenieQualitiesOffset = FallbackWeenieQualitiesOffset;
            RynthLog.Verbose($"Compat: qualitiesOffsetProbe no match, using fallback +0x{FallbackWeenieQualitiesOffset:X3} (player=0x{playerId:X8})");
        }
        catch
        {
            _weenieQualitiesOffset = FallbackWeenieQualitiesOffset;
        }
    }

    /// <summary>
    /// Reads the CACQualities* (== PlayerDesc*) from an ACCWeenieObject via m_pQualities.
    /// </summary>
    private static bool TryGetQualitiesPtr(IntPtr weeniePtr, out IntPtr qualitiesPtr)
    {
        qualitiesPtr = IntPtr.Zero;

        // P0-2 (2026-05-17): the dominant off-main-thread chokepoint. ~10
        // public accessors (Inq* int/float/bool/string/quad/attribute2nd,
        // IsSpellKnown, Vitae, ItemType, ObjectName) resolve their qualities
        // ptr here and then call straight into AC's helpers. AC's client is
        // not thread-safe; on the plugin pump thread we observe a qualities
        // sub-table mid-reassignment, AC walks a transiently-null pointer and
        // AVs in its OWN code (read [null+0xC] at acclient.exe+0x27E779 during
        // combat classification; read [null+0x1C] at acclient.exe+0x16547B
        // during corpse-loot — 3 crashes 2026-05-17 10:06/10:23/10:26). The
        // capital-O TryGetObjectQualitiesPtr already fails closed off-thread
        // for the player-skill path (snapshot-cache served instead); this is
        // the same gate for every OTHER accessor that bypasses it. Off-thread
        // callers fall back to their packet/appraisal/PWD-direct paths (which
        // every caller already has) instead of crashing the client.
        if (!MainThreadGuard.IsOnMainThread())
            return false;

        if (weeniePtr == IntPtr.Zero)
            return false;

        if (_weenieQualitiesOffset < 0)
        {
            if (_pendingPlayerDescPtr != IntPtr.Zero)
                ProbeQualitiesOffset();
            else
            {
                _weenieQualitiesOffset = FallbackWeenieQualitiesOffset;
                RynthLog.Verbose($"Compat: TryGetQualitiesPtr - no probe ptr, using fallback +0x{FallbackWeenieQualitiesOffset:X3}");
            }
        }

        try
        {
            // Validate the read address before dereferencing (NativeAOT AV safety).
            IntPtr readAddr = weeniePtr + _weenieQualitiesOffset;
            if (!IsReadablePointer(readAddr))
                return false;

            qualitiesPtr = Marshal.ReadIntPtr(readAddr);
            if (qualitiesPtr == IntPtr.Zero)
                return false;

            // Strongest validation we can do cheaply: a real CACQualities*
            // must begin with the SAME vtable pointer the player's qualities
            // begins with (we capture that as _cacQualitiesVtable on first
            // sight). If the read produced a same-shape-but-different-class
            // C++ object — observed on some weenies where m_pQualities
            // appears to point at a sibling type that has a module vtable
            // but isn't CACQualities — InqSkill will dereference fields
            // that don't exist for that class and AV inside AC.
            if (!IsCacQualitiesObject(qualitiesPtr))
            {
                qualitiesPtr = IntPtr.Zero;
                return false;
            }

            return true;
        }
        catch
        {
            qualitiesPtr = IntPtr.Zero;
            return false;
        }
    }

    /// <summary>
    /// Reads skill level and training class for any weenie object (typically the player).
    /// skillStype: STypeSkill value (sequential enum from Chorizite STypes.cs).
    /// buffed: current buffed level. training: 0=undef,1=untrained,2=trained,3=specialized.
    /// </summary>
    public static unsafe bool TryGetObjectSkill(uint objectId, uint skillStype, out int buffed, out int training)
    {
        buffed = 0;
        training = 0;
        if (_getWeenieObject == null)
        {
            if (!Probe() || _getWeenieObject == null)
                return false;
        }
        try
        {
            if (!TryGetObjectQualitiesPtr(objectId, out IntPtr qualitiesPtr))
            {
                // Off-thread (plugin pump) or not-yet-seeded callers can't
                // safely touch AC. Serve the main-thread-populated snapshot
                // so the pump still gets real skill levels instead of (0,0)
                // → tier-1 / "skill not usable" / never buffs.
                if (TryServeCachedPlayerSkill(objectId, skillStype, out buffed, out training))
                    return true;
                RynthLog.Verbose($"TryGetObjectSkill: m_pQualities null for 0x{objectId:X8}");
                return false;
            }

            if (!TryReadSkillFromTable(qualitiesPtr, skillStype, out SkillNative skill, out IntPtr skillTablePtr, out uint tableSize))
                return false;

            training = unchecked((int)skill.AdvancementClass);

            // Use CACQualities::InqSkill(stype, retval, raw=0) for the FULL buffed
            // skill — that includes attribute contribution, augmentations, and
            // spell-buff enchantments. The raw struct fields (InitialLevel +
            // LevelFromPracticePoints) only give the base trained level and miss
            // everything else, which is why this used to return ~208 for a
            // character whose true buffed Creature Enchantment was 360+.
            int rawBase = unchecked((int)(skill.InitialLevel + skill.LevelFromPracticePoints));
            int trulyBuffed = rawBase;
            // Re-enabled 2026-05-14 after determining retail and ACE acclient.exe
            // are byte-identical except for 3 PE-header bytes — so 0x00593380 IS
            // CACQualities::InqSkill on both. Earlier 0x00416C86 crashes were
            // from us passing a bogus qualitiesPtr (wrong probe offset on some
            // weenie types). TryGetObjectQualitiesPtr now rejects pointers that
            // don't look like AC C++ heap objects (vtable-in-module check), so
            // by this point qualitiesPtr is verified.
            if (_inqSkillLevel != null)
            {
                int retval = 0;
                if (_inqSkillLevel(qualitiesPtr, skillStype, &retval, 0) != 0)
                    trulyBuffed = retval;
            }
            buffed = trulyBuffed;

            // This read happened on the main thread (TryGetObjectQualitiesPtr
            // passed). Snapshot it so off-thread callers can be served.
            if (objectId != 0 && objectId == ClientHelperHooks.GetPlayerId())
            {
                lock (_playerSkillCacheLock)
                {
                    if (_playerSkillCacheOwner != objectId)
                    {
                        _playerSkillCache.Clear();
                        _playerSkillCacheOwner = objectId;
                    }
                    _playerSkillCache[skillStype] = (buffed, training);
                }
            }

            if (_skillProbeLogCount < 6)
            {
                _skillProbeLogCount++;
                RynthLog.Verbose(
                    $"Compat: skillTableProbe obj=0x{objectId:X8} skill={skillStype} qualities=0x{qualitiesPtr.ToInt32():X8} " +
                    $"table=0x{skillTablePtr.ToInt32():X8} buckets={tableSize} training={training} base={rawBase} buffed={buffed}");
            }

            return true;
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"TryGetObjectSkill exception: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Serves a previously snapshotted player skill to callers that can't
    /// read AC live — i.e. the off-thread plugin pump, where
    /// TryGetObjectQualitiesPtr fail-closes. False if cache is cold or the
    /// object isn't the player.
    /// </summary>
    private static bool TryServeCachedPlayerSkill(uint objectId, uint skillStype, out int buffed, out int training)
    {
        buffed = 0;
        training = 0;
        if (objectId == 0 || objectId != ClientHelperHooks.GetPlayerId())
            return false;
        lock (_playerSkillCacheLock)
        {
            if (_playerSkillCacheOwner != objectId ||
                !_playerSkillCache.TryGetValue(skillStype, out var v))
                return false;
            buffed = v.buffed;
            training = v.training;
        }
        if (!_loggedSkillCacheServe)
        {
            _loggedSkillCacheServe = true;
            RynthLog.Compat("ClientObjectHooks: serving player skills from main-thread snapshot cache (off-thread plugin pump).");
        }
        return true;
    }

    /// <summary>
    /// Refreshes the whole player-skill snapshot from AC. MUST run on AC's
    /// main thread — driven from the EndScene always-on path. Throttled:
    /// skills change slowly and tier decisions tolerate ~1s lag. One table
    /// walk caches every skill the character has; the off-thread plugin pump
    /// then reads the snapshot via TryServeCachedPlayerSkill instead of
    /// getting (0,0) and dropping to tier-1 / "skill not usable".
    /// </summary>
    public static unsafe void PrefetchPlayerSkills()
    {
        if (!MainThreadGuard.IsOnMainThread())
            return;
        if ((DateTime.UtcNow - _lastPlayerSkillPrefetchUtc).TotalMilliseconds < PlayerSkillPrefetchThrottleMs)
            return;
        _lastPlayerSkillPrefetchUtc = DateTime.UtcNow;

        uint playerId = ClientHelperHooks.GetPlayerId();
        if (playerId == 0)
            return;

        if (_getWeenieObject == null)
        {
            if (!Probe() || _getWeenieObject == null)
                return;
        }

        try
        {
            // On the main thread this resolves — and the lazy reseed inside
            // also covers the cold-start unseeded-ptr case for free.
            if (!TryGetObjectQualitiesPtr(playerId, out IntPtr qualitiesPtr))
                return;

            IntPtr tableFieldPtr = qualitiesPtr + SkillStatsTableOffset;
            if (!IsReadablePointer(tableFieldPtr))
                return;
            IntPtr skillTablePtr = Marshal.ReadIntPtr(tableFieldPtr);
            if (skillTablePtr == IntPtr.Zero || !IsReadablePointer(skillTablePtr))
                return;

            PackableHashTableUInt32SkillNative table =
                Marshal.PtrToStructure<PackableHashTableUInt32SkillNative>(skillTablePtr);
            if (table.TableSize == 0 || table.TableSize > 4096 ||
                table.Buckets == IntPtr.Zero || !IsReadablePointer(table.Buckets))
                return;

            var snapshot = new Dictionary<uint, (int buffed, int training)>();
            for (uint b = 0; b < table.TableSize; b++)
            {
                IntPtr bucketPtrAddr = table.Buckets + unchecked((int)(b * (uint)IntPtr.Size));
                if (!IsReadablePointer(bucketPtrAddr))
                    continue;
                IntPtr nodePtr = Marshal.ReadIntPtr(bucketPtrAddr);
                int guard = 0;
                while (nodePtr != IntPtr.Zero && guard++ < 512)
                {
                    if (!IsReadablePointer(nodePtr))
                        break;
                    PackableHashDataUInt32SkillNative node =
                        Marshal.PtrToStructure<PackableHashDataUInt32SkillNative>(nodePtr);

                    int training = unchecked((int)node.Data.AdvancementClass);
                    int buffed = unchecked((int)(node.Data.InitialLevel + node.Data.LevelFromPracticePoints));
                    // Guard: InqSkill reads CEnchantmentRegistry at [CACQualities+0x70].
                    // If that pointer is null (early login, enchantments not yet received
                    // from server), InqSkill AVs at acclient.exe+0x27E779 reading
                    // [null+0xC]. [+0x64] was already IsReadablePointer-checked above so
                    // the same page covers +0x70; no second VirtualQuery needed.
                    if (_inqSkillLevel != null &&
                        Marshal.ReadIntPtr(qualitiesPtr + 0x70) != IntPtr.Zero)
                    {
                        int retval = 0;
                        if (_inqSkillLevel(qualitiesPtr, node.Key, &retval, 0) != 0)
                            buffed = retval;
                    }
                    snapshot[node.Key] = (buffed, training);

                    nodePtr = node.Next;
                }
            }

            if (snapshot.Count == 0)
                return;

            lock (_playerSkillCacheLock)
            {
                if (_playerSkillCacheOwner != playerId)
                {
                    _playerSkillCache.Clear();
                    _playerSkillCacheOwner = playerId;
                }
                foreach (var kv in snapshot)
                    _playerSkillCache[kv.Key] = kv.Value;
            }
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"PrefetchPlayerSkills exception: {ex.Message}");
        }
    }

    /// <summary>
    /// Snapshots the player's XP / luminance / deaths / vitae on AC's main thread (driven
    /// from the EndScene path, same as PrefetchPlayerSkills) into a cache the off-thread
    /// plugin pump and StatusSnapshotWriter can read. These are all main-thread-only Inq*
    /// reads. Throttled; fully defensive. [status-export]
    /// </summary>
    public static void PrefetchPlayerStats()
    {
        if (!MainThreadGuard.IsOnMainThread())
            return;
        if ((DateTime.UtcNow - _lastPlayerStatsPrefetchUtc).TotalMilliseconds < PlayerStatsPrefetchThrottleMs)
            return;
        _lastPlayerStatsPrefetchUtc = DateTime.UtcNow;

        uint playerId = ClientHelperHooks.GetPlayerId();
        if (playerId == 0)
            return;

        try
        {
            // Read AC on this (main) thread, then publish under the lock so the off-thread
            // reader never sees a torn 64-bit value (x86 long writes aren't atomic).
            bool sameOwner = playerId == _playerStatsCacheOwner;
            long xp = sameOwner ? _cachedTotalXp : 0;
            long lum = sameOwner ? _cachedLuminance : 0;
            int deaths = sameOwner ? _cachedDeaths : 0;
            float vitae = sameOwner ? _cachedVitae : 1.0f;

            if (TryGetObjectQuadProperty(playerId, 1u, out long x) && x > 0) xp = x;     // PropertyInt64.TotalExperience
            if (TryGetObjectQuadProperty(playerId, 6u, out long l) && l > 0) lum = l;    // PropertyInt64.AvailableLuminance
            if (TryGetObjectIntProperty(playerId, 43u, out int d)) deaths = d;           // PropertyInt.NumDeaths
            if (TryGetVitae(playerId, out float v)) vitae = v;

            lock (_playerStatsCacheLock)
            {
                _cachedTotalXp = xp; _cachedLuminance = lum; _cachedDeaths = deaths; _cachedVitae = vitae;
                _playerStatsCacheOwner = playerId;
            }
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"PrefetchPlayerStats exception: {ex.Message}");
        }
    }

    /// <summary>
    /// Off-thread-safe: serve the cached player-stats snapshot (XP / luminance / deaths /
    /// vitae multiplier, 1.0 = no penalty). Returns false until the cache is populated.
    /// </summary>
    public static bool TryGetPlayerStats(out long totalXp, out long luminance, out int deaths, out float vitae)
    {
        lock (_playerStatsCacheLock)
        {
            totalXp = _cachedTotalXp; luminance = _cachedLuminance; deaths = _cachedDeaths; vitae = _cachedVitae;
            return _playerStatsCacheOwner != 0;
        }
    }

    /// <summary>
    /// Refreshes the known-spell (spellbook) snapshot from AC. MUST run on AC's
    /// main thread — driven from the same EndScene path as PrefetchPlayerSkills.
    /// Walks the [CACQualities+0x6C] PackableHashTable; fully defensive (any
    /// layout mismatch / bad pointer yields no change, never an AC fault).
    /// </summary>
    public static void PrefetchKnownSpells()
    {
        if (!MainThreadGuard.IsOnMainThread())
            return;
        if ((DateTime.UtcNow - _lastKnownSpellPrefetchUtc).TotalMilliseconds < KnownSpellPrefetchThrottleMs)
            return;
        _lastKnownSpellPrefetchUtc = DateTime.UtcNow;

        uint playerId = ClientHelperHooks.GetPlayerId();
        if (playerId == 0)
            return;

        if (_getWeenieObject == null)
        {
            if (!Probe() || _getWeenieObject == null)
                return;
        }

        try
        {
            if (!TryGetObjectQualitiesPtr(playerId, out IntPtr qualitiesPtr))
                return;

            IntPtr tableFieldPtr = qualitiesPtr + SpellBookTableOffset;
            if (!IsReadablePointer(tableFieldPtr))
                return;
            IntPtr tablePtr = Marshal.ReadIntPtr(tableFieldPtr);
            if (tablePtr == IntPtr.Zero || !IsReadablePointer(tablePtr))
                return;

            // PackableHashTable: buckets ptr at +0x0C, bucket count at +0x10.
            IntPtr bucketsFieldPtr = tablePtr + 0x0C;
            IntPtr countFieldPtr   = tablePtr + 0x10;
            if (!IsReadablePointer(bucketsFieldPtr) || !IsReadablePointer(countFieldPtr))
                return;
            IntPtr buckets = Marshal.ReadIntPtr(bucketsFieldPtr);
            uint bucketCount = unchecked((uint)Marshal.ReadInt32(countFieldPtr));
            if (buckets == IntPtr.Zero || !IsReadablePointer(buckets) ||
                bucketCount == 0 || bucketCount > 8192)
                return;

            var snapshot = new HashSet<uint>();
            int totalGuard = 0;
            for (uint b = 0; b < bucketCount; b++)
            {
                IntPtr bucketAddr = buckets + unchecked((int)(b * (uint)IntPtr.Size));
                if (!IsReadablePointer(bucketAddr))
                    continue;
                IntPtr nodePtr = Marshal.ReadIntPtr(bucketAddr);
                int chainGuard = 0;
                while (nodePtr != IntPtr.Zero && chainGuard++ < 1024 && totalGuard++ < 20000)
                {
                    if (!IsReadablePointer(nodePtr))
                        break;
                    // node: key (spell id) at +0x00, next at +0x0C.
                    uint spellId = unchecked((uint)Marshal.ReadInt32(nodePtr));
                    if (spellId != 0 && spellId < 0x10000)
                        snapshot.Add(spellId);
                    IntPtr nextAddr = nodePtr + 0x0C;
                    if (!IsReadablePointer(nextAddr))
                        break;
                    nodePtr = Marshal.ReadIntPtr(nextAddr);
                }
            }

            if (snapshot.Count == 0)
                return;

            lock (_knownSpellCacheLock)
            {
                _knownSpellCache.Clear();
                _knownSpellCacheOwner = playerId;
                foreach (uint id in snapshot)
                    _knownSpellCache.Add(id);
            }
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"PrefetchKnownSpells exception: {ex.Message}");
        }
    }

    /// <summary>
    /// Serves the main-thread known-spell snapshot to the off-thread plugin
    /// pump. Returns -1 (cold / wrong owner / empty) so the caller keeps its
    /// existing fallback behavior; otherwise the count written to outIds.
    /// </summary>
    public static unsafe int CopyCachedKnownSpells(uint* outIds, int maxCount)
    {
        if (outIds == null || maxCount <= 0)
            return -1;
        uint playerId = ClientHelperHooks.GetPlayerId();
        lock (_knownSpellCacheLock)
        {
            if (_knownSpellCacheOwner == 0 || _knownSpellCacheOwner != playerId ||
                _knownSpellCache.Count == 0)
                return -1;
            int i = 0;
            foreach (uint id in _knownSpellCache)
            {
                if (i >= maxCount) break;
                outIds[i++] = id;
            }
            if (!_loggedKnownSpellServe)
            {
                _loggedKnownSpellServe = true;
                RynthLog.Compat($"ClientObjectHooks: serving {_knownSpellCache.Count} known spells from main-thread snapshot (off-thread pump).");
            }
            return i;
        }
    }

    /// <summary>
    /// Returns a skill level via CACQualities::InqSkill(stype, retval, raw).
    /// raw=0 → buffed (with enchantments), raw=1 → base (no enchantments, includes attribute contribution).
    /// </summary>
    public static unsafe bool TryGetObjectSkillLevel(uint objectId, uint skillStype, int raw, out int level)
    {
        level = 0;
        // Off-thread: refuse — _inqSkillLevel is an AC native call (Class A guard).
        if (!MainThreadGuard.IsOnMainThread())
            return false;
        if (_inqSkillLevel == null || _getWeenieObject == null)
        {
            if (!Probe() || _inqSkillLevel == null || _getWeenieObject == null)
                return false;
        }
        try
        {
            if (!TryGetObjectQualitiesPtr(objectId, out IntPtr qualitiesPtr))
                return false;
            int retval = 0;
            if (_inqSkillLevel(qualitiesPtr, skillStype, &retval, raw) == 0)
                return false;
            level = retval;
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Reads a primary attribute via CACQualities::InqAttribute(stype, retval, raw).
    /// raw=0 → buffed (with enchantments), raw=1 → base (no enchantments).
    /// stype: 1=Strength, 2=Endurance, 3=Quickness, 4=Coordination, 5=Focus, 6=Self.
    /// </summary>
    public static unsafe bool TryGetObjectAttribute(uint objectId, uint stype, int raw, out uint value)
    {
        value = 0;
        // Off-thread: refuse — _inqAttribute is an AC native call (Class A guard).
        if (!MainThreadGuard.IsOnMainThread())
            return false;
        if (_inqAttribute == null || _getWeenieObject == null)
        {
            if (!Probe() || _inqAttribute == null || _getWeenieObject == null)
                return false;
        }
        try
        {
            if (!TryGetObjectQualitiesPtr(objectId, out IntPtr qualitiesPtr))
                return false;
            uint retval = 0;
            if (_inqAttribute(qualitiesPtr, stype, &retval, raw) == 0)
                return false;
            value = retval;
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Derives the player's CACQualities* on demand from the current player object.
    /// Works even when injected mid-session (before SendNoticePlayerDescReceived fires).
    /// </summary>
    public static bool TryGetPlayerQualitiesPtr(out IntPtr qualitiesPtr)
    {
        qualitiesPtr = IntPtr.Zero;
        if (_getWeenieObject == null)
        {
            if (!Probe() || _getWeenieObject == null)
                return false;
        }

        uint playerId = ClientHelperHooks.GetPlayerId();
        if (playerId == 0)
            return false;

        try
        {
            IntPtr weeniePtr = _getWeenieObject(playerId);
            if (weeniePtr == IntPtr.Zero)
                return false;

            return TryGetQualitiesPtr(weeniePtr, out qualitiesPtr);
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"TryGetPlayerQualitiesPtr: exception - {ex.Message}");
            return false;
        }
    }

    public static bool TryGetWeenieObjectPtr(uint objectId, out IntPtr ptr)
    {
        ptr = IntPtr.Zero;
        if (_getWeenieObject == null)
        {
            if (!Probe() || _getWeenieObject == null)
                return false;
        }
        try
        {
            ptr = _getWeenieObject(objectId);
            return ptr != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads PublicWeenieDesc ownership fields directly from the weenie struct.
    /// containerID is the parent container object (0 = not contained).
    /// wielderID is the object ID of whoever is wielding this item (0 = not wielded).
    /// location is the body slot bitmask (e.g. 0x2 = shield hand, 0x1 = melee weapon).
    /// Layout: ACCWeenieObject + _phys_obj_offset + 4 = start of PublicWeenieDesc.
    /// PublicWeenieDesc+28 = _containerID, +32 = _wielderID, +44 = _location.
    /// </summary>
    public static bool TryGetObjectOwnershipInfo(uint objectId, out uint containerID, out uint wielderID, out uint location)
    {
        containerID = 0;
        wielderID = 0;
        location = 0;
        if (_getWeenieObject == null)
        {
            if (!Probe() || _getWeenieObject == null)
                return false;
        }
        if (_weeniePhysicsObjOffset < 0)
            return false; // phys_obj offset not yet discovered
        try
        {
            IntPtr weeniePtr = _getWeenieObject(objectId);
            if (weeniePtr == IntPtr.Zero)
                return false;
            // PublicWeenieDesc starts at _phys_obj offset + 4 (pointer size)
            int pwdBase = _weeniePhysicsObjOffset + 4;
            // Page-probe both ends of the field span (siblings TryGetObjectWcid/
            // Bitfield do the same): a freed-but-decommitted weenie here is an
            // uncatchable AV under NativeAOT — the catch below only covers
            // null-page faults.
            if (!IsReadablePointer(weeniePtr + pwdBase + 28) || !IsReadablePointer(weeniePtr + pwdBase + 44))
                return false;
            containerID = (uint)Marshal.ReadInt32(weeniePtr + pwdBase + 28);
            wielderID = (uint)Marshal.ReadInt32(weeniePtr + pwdBase + 32);
            location = (uint)Marshal.ReadInt32(weeniePtr + pwdBase + 44);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads PublicWeenieDesc._wcid directly from the weenie struct.
    /// Returns the Weenie Class ID (WCID) for the given object.
    /// Layout: ACCWeenieObject + _phys_obj_offset + 4 = start of PublicWeenieDesc.
    /// PublicWeenieDesc+12 = _wcid: WeenieDesc(4) + _name(4) + _plural_name(4) = 12.
    /// </summary>
    public static bool TryGetObjectWcid(uint objectId, out uint wcid)
    {
        wcid = 0;
        if (_getWeenieObject == null)
        {
            if (!Probe() || _getWeenieObject == null)
                return false;
        }
        if (_weeniePhysicsObjOffset < 0)
            return false;
        try
        {
            IntPtr weeniePtr = _getWeenieObject(objectId);
            if (weeniePtr == IntPtr.Zero)
                return false;
            int pwdBase = _weeniePhysicsObjOffset + 4;
            IntPtr fieldAddr = weeniePtr + pwdBase + 12;
            if (!IsReadablePointer(fieldAddr))
                return false;
            wcid = (uint)Marshal.ReadInt32(fieldAddr);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads PublicWeenieDesc._bitfield from the weenie struct.
    /// Returns the ObjectDescriptionFlags bitfield (e.g. BF_DOOR = 0x1000).
    /// Layout: PublicWeenieDesc+104 = _bitfield (26 × 4-byte fields from start).
    /// </summary>
    public static bool TryGetObjectBitfield(uint objectId, out uint bitfield)
    {
        bitfield = 0;
        if (_getWeenieObject == null)
        {
            if (!Probe() || _getWeenieObject == null)
                return false;
        }
        if (_weeniePhysicsObjOffset < 0)
            return false;
        try
        {
            IntPtr weeniePtr = _getWeenieObject(objectId);
            if (weeniePtr == IntPtr.Zero)
                return false;
            int pwdBase = _weeniePhysicsObjOffset + 4;
            IntPtr fieldAddr = weeniePtr + pwdBase + 104;
            if (!IsReadablePointer(fieldAddr))
                return false;
            bitfield = (uint)Marshal.ReadInt32(fieldAddr);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Master gate: if false, <see cref="TryGetObjectQualitiesPtr"/> only ever
    /// returns the player's qualities, never anyone else's. Set false 2026-05-14
    /// after extensive crash logs showed AC's CACQualities accessor functions
    /// (InqSkill, InqInt, InqFloat, ...) AV at 0x00416C86 / 0x00460BE1 when
    /// called on non-player CACQualities objects on this ACE build — even when
    /// the qualities pointer itself passes a vtable-in-module check.
    ///
    /// AC's Inq* helpers reach 3+ frames deep into sub-table lookups, and on
    /// monsters/items the sub-tables aren't fully populated client-side: the
    /// outer struct is a real CACQualities but its internal stat/skill tables
    /// are null, so any helper that dereferences those tables faults.
    ///
    /// Until we have an SEH-guarded native trampoline that can catch AVs
    /// inside AC and return false to the caller, "player only" is the safe
    /// behavior — bots lose the ability to introspect monster/item stats
    /// via these native calls, but the client doesn't crash.
    /// Set true at your own risk on a binary you've verified.
    /// </summary>
    internal static bool AllowNonPlayerQualities;

    /// <summary>
    /// Read a creature's REAL MaxHealth straight from the live client (its
    /// qualities table), SEH-guarded. Appraisal-free — returns the true HP even
    /// for mobs the character's Assess skill can't read (where the appraisal path
    /// only yields a 50 stub). MAIN-THREAD ONLY: AC isn't thread-safe, so this
    /// returns false when called off AC's main thread. Returns false if the
    /// weenie can't be resolved or the guarded read AVs / yields 0.
    /// </summary>
    public static bool TryReadCreatureMaxHealth(uint objectId, out uint maxHealth)
    {
        maxHealth = 0;
        if (objectId == 0 || _getWeenieObject == null) return false;
        if (!MainThreadGuard.IsOnMainThread()) return false;

        IntPtr weeniePtr;
        try { weeniePtr = _getWeenieObject(objectId); } catch { return false; }
        if (weeniePtr == IntPtr.Zero || !IsReadablePointer(weeniePtr)) return false;

        // InqAttribute2nd's `this` is the CACQualities sub-object (it derefs
        // this+0x60 = the attribute cache), NOT the weenie object — passing the
        // raw weenie reads garbage. Resolve the qualities pointer first.
        if (!TryGetQualitiesPtr(weeniePtr, out IntPtr qualitiesPtr) || qualitiesPtr == IntPtr.Zero)
            return false;
        if (!IsReadablePointer(qualitiesPtr)) return false;

        return PlayerVitalsHooks.TryReadCreatureMaxHealthSafe(qualitiesPtr, out maxHealth);
    }

    private static bool TryGetObjectQualitiesPtr(uint objectId, out IntPtr qualitiesPtr)
    {
        qualitiesPtr = IntPtr.Zero;

        // AC's client is not thread-safe. Any caller that's about to cross
        // into AC's Inq* helpers via this pointer must be running on AC's
        // main thread, or we'll observe sub-pointers in mid-reassignment
        // and AV deep inside AC code (consistent crash at 0x00416C86 from
        // the DecalCoexistence plugin-tick thread, 2026-05-14).
        //
        // Fails closed: before any Compatibility detour has fired (and so
        // the main thread isn't yet known), IsOnMainThread returns false
        // and we refuse the lookup. That's OK — the plugin tick pump only
        // starts well after several detours have fired.
        if (!MainThreadGuard.IsOnMainThread())
            return false;

        uint playerId = ClientHelperHooks.GetPlayerId();

        // Cold-start self-heal: if this is the player and the qualities ptr
        // was never seeded (SendNoticePlayerDescReceived fired before our
        // detour armed, and this is neither a hot-reload nor a Decal-
        // coexistence launch — the only two paths with an explicit reseed),
        // derive it on demand. Covers every caller funnelling through this
        // gate (skills, attributes, enchantments). Throttled, and only
        // attempted while still unseeded. TryReseedFromCurrentPlayer resolves
        // the ptr via TryGetPlayerQualitiesPtr (a separate path that does not
        // re-enter this method) and is already main-thread-guarded above.
        if (objectId == playerId &&
            playerId != 0 &&
            PlayerVitalsHooks.KnownPlayerQualitiesPtr == IntPtr.Zero &&
            (DateTime.UtcNow - _lastLazyQualitiesReseedUtc).TotalMilliseconds > LazyQualitiesReseedThrottleMs)
        {
            _lastLazyQualitiesReseedUtc = DateTime.UtcNow;
            if (PlayerVitalsHooks.TryReseedFromCurrentPlayer() && !_loggedLazyQualitiesReseed)
            {
                _loggedLazyQualitiesReseed = true;
                RynthLog.Compat("PlayerVitals: cold-start — player qualities ptr lazily re-seeded (SendNoticePlayerDescReceived missed before hook arm).");
            }
        }

        if (objectId == playerId && PlayerVitalsHooks.KnownPlayerQualitiesPtr != IntPtr.Zero)
        {
            qualitiesPtr = PlayerVitalsHooks.KnownPlayerQualitiesPtr;
            // Even the cached "known" qualities ptr can be stale across logout/login
            // or on a hot-reload — verify it's still a live AC C++ object before
            // returning it. The player's qualities is by definition a real
            // CACQualities, so use it to learn the canonical vtable for filtering
            // other objects' would-be qualities pointers later.
            if (!LooksLikeAcHeapObject(qualitiesPtr))
            {
                qualitiesPtr = IntPtr.Zero;
                return false;
            }
            CaptureCacQualitiesVtable(qualitiesPtr);
            return true;
        }

        if (!AllowNonPlayerQualities)
        {
            // Hard gate: refuse to expose non-player CACQualities pointers
            // until we can call into AC's Inq* helpers without risking AV.
            // Callers fall back to packet-driven ObjectQualityCache where
            // available, or just return defaults.
            return false;
        }

        IntPtr weeniePtr = _getWeenieObject!(objectId);
        if (weeniePtr == IntPtr.Zero)
        {
            RynthLog.Verbose($"TryGetObjectSkill: GetWeenieObject(0x{objectId:X8}) returned null");
            return false;
        }

        return TryGetQualitiesPtr(weeniePtr, out qualitiesPtr);
    }

    private static bool TryReadSkillFromTable(
        IntPtr qualitiesPtr,
        uint skillStype,
        out SkillNative skill,
        out IntPtr skillTablePtr,
        out uint tableSize)
    {
        skill = default;
        skillTablePtr = IntPtr.Zero;
        tableSize = 0;

        IntPtr tableFieldPtr = qualitiesPtr + SkillStatsTableOffset;
        if (!IsReadablePointer(tableFieldPtr))
            return false;

        skillTablePtr = Marshal.ReadIntPtr(tableFieldPtr);
        if (skillTablePtr == IntPtr.Zero || !IsReadablePointer(skillTablePtr))
            return false;

        PackableHashTableUInt32SkillNative table = Marshal.PtrToStructure<PackableHashTableUInt32SkillNative>(skillTablePtr);
        tableSize = table.TableSize;
        if (table.TableSize == 0 || table.Buckets == IntPtr.Zero || !IsReadablePointer(table.Buckets))
            return false;

        uint bucketIndex = skillStype % table.TableSize;
        IntPtr bucketPtrAddr = table.Buckets + unchecked((int)(bucketIndex * (uint)IntPtr.Size));
        if (!IsReadablePointer(bucketPtrAddr))
            return false;

        IntPtr nodePtr = Marshal.ReadIntPtr(bucketPtrAddr);
        int guard = 0;
        while (nodePtr != IntPtr.Zero && guard++ < 512)
        {
            if (!IsReadablePointer(nodePtr))
                return false;

            PackableHashDataUInt32SkillNative node = Marshal.PtrToStructure<PackableHashDataUInt32SkillNative>(nodePtr);
            if (node.Key == skillStype)
            {
                skill = node.Data;
                return true;
            }

            nodePtr = node.Next;
        }

        return false;
    }

    /// <summary>
    /// Reads PublicWeenieDesc._wielderID and _location directly from the weenie struct.
    /// Preserved for older callers that do not need container ownership.
    /// </summary>
    public static bool TryGetObjectWielderInfo(uint objectId, out uint wielderID, out uint location)
    {
        return TryGetObjectOwnershipInfo(objectId, out _, out wielderID, out location);
    }

    /// <summary>
    /// Calls ACCWeenieObject::InqType() and returns the ITEM_TYPE flags.
    /// Returns false if the weenie is not found or the call throws.
    /// </summary>
    public static bool TryGetItemType(uint objectId, out uint typeFlags)
    {
        typeFlags = 0;
        // Off-thread: serve from the main-thread snapshot populated by PrefetchObjectIdentity().
        if (!MainThreadGuard.IsOnMainThread())
        {
            lock (_objectIdentityCacheLock)
            {
                if (_objectTypeCache.TryGetValue(objectId, out typeFlags))
                    return true;
            }
            typeFlags = 0;
            return false;
        }
        if (_inqType == null || _getWeenieObject == null)
        {
            if (!Probe() || _inqType == null || _getWeenieObject == null)
                return false;
        }
        try
        {
            IntPtr weeniePtr;
            if (SehTrampoline.IsAvailable)
            {
                weeniePtr = SehTrampoline.CdeclPtrUint(_getWeenieObjectPtr, objectId, out bool avW);
                if (avW)
                {
                    if (System.Threading.Interlocked.Increment(ref _sehAvLogCount) <= 20)
                        RynthLog.Compat($"[SEH] AV in GetWeenieObject (TryGetItemType) for 0x{objectId:X8} — caught");
                    return false;
                }
            }
            else
            {
                weeniePtr = _getWeenieObject(objectId);
            }

            if (weeniePtr == IntPtr.Zero)
                return false;

            // _inqType reads m_pQualities->m_type. Null qualities causes an AV
            // (a Corrupted State Exception in NativeAOT, which managed catch
            // can NOT recover from — it FailFasts the entire AC client process).
            // Skip the call when qualities is null and fall through to the
            // PWD-direct fallback below.
            if (TryGetQualitiesPtr(weeniePtr, out _))
            {
                if (SehTrampoline.IsAvailable)
                {
                    typeFlags = SehTrampoline.ThiscallUintNoArg(_inqTypePtr, weeniePtr, out bool avT);
                    if (avT)
                    {
                        if (System.Threading.Interlocked.Increment(ref _sehAvLogCount) <= 20)
                            RynthLog.Compat($"[SEH] AV in InqType for 0x{objectId:X8} — caught");
                        return false;
                    }
                }
                else
                {
                    typeFlags = _inqType(weeniePtr);
                }
                return true;
            }

            // PWD.type is set up by network packets independently of qualities.
            // PublicWeenieDesc + 56 = _type (ITEM_TYPE uint). Layout per Chorizite
            // Weenie.cs: WeenieDesc(4) + _name(4) + _plural_name(4) + _wcid(4) +
            // _iconID(4) + _iconOverlayID(4) + _iconUnderlayID(4) + _containerID(4) +
            // _wielderID(4) + _priority(4) + _valid_locations(4) + _location(4) +
            // _itemsCapacity(4) + _containersCapacity(4) = 56 → _type.
            if (_weeniePhysicsObjOffset >= 0)
            {
                int pwdBase = _weeniePhysicsObjOffset + 4;
                IntPtr typeAddr = weeniePtr + pwdBase + 56;
                if (IsReadablePointer(typeAddr))
                {
                    typeFlags = (uint)Marshal.ReadInt32(typeAddr);
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        // Last resort: appraisal cache from IdentifyObject responses.
        if (AppraisalHooks.TryGetCachedIntProperty(objectId, 1 /* STypeInt.ItemType */, out int cachedType))
        {
            typeFlags = unchecked((uint)cachedType);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Reads a STypeInt property from any object. Stypes whose values live in
    /// PublicWeenieDesc (stack size, location, capacity, etc.) are read directly
    /// from the embedded PWD struct — InqInt on inventory items goes through a
    /// qualities pointer that often isn't populated for items, so we bypass it.
    /// All other stypes fall through to CBaseQualities::InqInt.
    /// Common stypes: LOCATIONS=9, CURRENT_WIELDED_LOCATION=10, STACK_SIZE=12, DAMAGE_TYPE=45.
    /// </summary>
    public static unsafe bool TryGetObjectIntProperty(uint objectId, uint stype, out int value)
    {
        value = 0;

        // Appraisal cache covers objects where m_pQualities is null (doors, inventory items, etc.)
        if (AppraisalHooks.TryGetCachedIntProperty(objectId, stype, out value))
            return true;

        if (_getWeenieObject == null)
        {
            if (!Probe() || _getWeenieObject == null)
                return false;
        }

        // Fast path: stypes whose values live in PublicWeenieDesc.
        // PWD starts at weenie + _phys_obj_offset + 4. Layout (Chorizite Weenie.cs:1734):
        //   +28 _containerID, +32 _wielderID, +40 _valid_locations, +44 _location,
        //   +48 _itemsCapacity, +52 _containersCapacity, +96 _stackSize, +100 _maxStackSize,
        //   +148 _material_type
        // CBaseQualities::InqInt fails for inventory/corpse items (m_pQualities is null on pack wienies).
        // Any stype whose value lives in PWD must be served from here instead.
        int pwdFieldOffset = stype switch
        {
            6   => 48,   // ITEMS_CAPACITY → _itemsCapacity
            7   => 52,   // CONTAINERS_CAPACITY → _containersCapacity
            9   => 40,   // LOCATIONS → _valid_locations
            10  => 44,   // CURRENT_WIELDED_LOCATION → _location
            11  => 100,  // MAX_STACK_SIZE → _maxStackSize
            12  => 96,   // STACK_SIZE → _stackSize
            131 => 148,  // MATERIAL_TYPE → _material_type (4 bytes, int)
            _   => -1,
        };

        if (pwdFieldOffset >= 0 && _weeniePhysicsObjOffset >= 0)
        {
            try
            {
                IntPtr weeniePtr = _getWeenieObject(objectId);
                if (weeniePtr == IntPtr.Zero)
                    return false;

                int pwdBase = _weeniePhysicsObjOffset + 4;
                IntPtr fieldAddr = weeniePtr + pwdBase + pwdFieldOffset;
                if (!IsReadablePointer(fieldAddr))
                    return false;

                value = Marshal.ReadInt32(fieldAddr);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // Fall through to CBaseQualities::InqInt for stypes not in PWD.
        // Off-thread: refuse the native fallback — racing with main-thread
        // qualities teardown produces the Class A 0x67E779 AV.
        if (!MainThreadGuard.IsOnMainThread())
            return false;
        if (_inqInt == null)
        {
            if (!Probe() || _inqInt == null)
                return false;
        }
        try
        {
            IntPtr weeniePtr = _getWeenieObject(objectId);
            if (weeniePtr == IntPtr.Zero)
                return false;

            if (!TryGetQualitiesPtr(weeniePtr, out IntPtr qualitiesPtr))
                return false;

            IntPtr baseQualitiesPtr = qualitiesPtr + CBaseQualitiesOffset;
            if (!IsReadablePointer(baseQualitiesPtr))
                return false;

            int retval = 0;
            int result = _inqInt(baseQualitiesPtr, stype, &retval, 0, 1);

            if (result != 0)
            {
                value = retval;
                return true;
            }

            // Fallback: network property cache (covers static world objects like doors
            // where m_pQualities is null and InqInt returns 0).
            if (PropertyUpdateHooks.TryGetCachedIntProperty(objectId, stype, out value))
                return true;

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Calls CBaseQualities::InqFloat on the object's weenie to read a STypeFloat property.
    /// AC stores these as doubles despite the "Float" name.
    /// Fast path: stype 280 (ITEM_WORKMANSHIP) is read from PublicWeenieDesc._workmanship
    /// (Single at PWD+152) because it lives in the weenie descriptor, not CBaseQualities,
    /// and InqFloat fails for pack/inventory items that have no m_pQualities pointer.
    /// </summary>
    public static unsafe bool TryGetObjectDoubleProperty(uint objectId, uint stype, out double value)
    {
        value = 0;
        if (_getWeenieObject == null)
        {
            if (!Probe() || _getWeenieObject == null)
                return false;
        }

        // Fast path: ITEM_WORKMANSHIP (STypeFloat=280) → PublicWeenieDesc._workmanship (Single at PWD+152)
        if (stype == 280u && _weeniePhysicsObjOffset >= 0)
        {
            try
            {
                IntPtr weeniePtr = _getWeenieObject(objectId);
                if (weeniePtr == IntPtr.Zero)
                    return false;
                int pwdBase = _weeniePhysicsObjOffset + 4;
                IntPtr fieldAddr = weeniePtr + pwdBase + 152;
                if (!IsReadablePointer(fieldAddr))
                    return false;
                int bits = Marshal.ReadInt32(fieldAddr);
                float f = BitConverter.Int32BitsToSingle(bits);
                if (f == 0f)
                    return false;
                value = f;
                return true;
            }
            catch { return false; }
        }

        // Off-thread: refuse the InqFloat native fallback (Class A guard).
        if (!MainThreadGuard.IsOnMainThread())
            return false;
        if (_inqFloat == null)
        {
            if (!Probe() || _inqFloat == null)
                return false;
        }
        try
        {
            IntPtr weeniePtr = _getWeenieObject(objectId);
            if (weeniePtr == IntPtr.Zero)
                return false;

            if (!TryGetQualitiesPtr(weeniePtr, out IntPtr qualitiesPtr))
                return false;

            IntPtr baseQualitiesPtr = qualitiesPtr + CBaseQualitiesOffset;
            if (!IsReadablePointer(baseQualitiesPtr))
                return false;

            double retval = 0;
            int result = _inqFloat(baseQualitiesPtr, stype, &retval, 0);
            if (result == 0)
                return false;

            value = retval;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Calls CBaseQualities::InqInt64 on the object's weenie to read a STypeInt64 (quad) property.
    /// Example: stype=1 = TOTAL_EXPERIENCE.
    /// </summary>
    public static unsafe bool TryGetObjectQuadProperty(uint objectId, uint stype, out long value)
    {
        value = 0;
        // Off-thread: refuse — _inqInt64 is an AC native call (Class A guard).
        if (!MainThreadGuard.IsOnMainThread())
            return false;
        if (_inqInt64 == null)
        {
            if (!Probe() || _inqInt64 == null)
                return false;
        }
        try
        {
            IntPtr weeniePtr = _getWeenieObject!(objectId);
            if (weeniePtr == IntPtr.Zero)
                return false;

            if (!TryGetQualitiesPtr(weeniePtr, out IntPtr qualitiesPtr))
                return false;

            IntPtr baseQualitiesPtr = qualitiesPtr + CBaseQualitiesOffset;
            if (!IsReadablePointer(baseQualitiesPtr))
                return false;

            long retval = 0;
            int result = _inqInt64(baseQualitiesPtr, stype, &retval);
            if (result == 0)
                return false;

            value = retval;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Calls CACQualities::InqAttribute2nd(stype, &result, raw=0) to read the base maximum vital.
    /// Returns base training + gear bonuses + augmentations, but NOT spell enchantments.
    /// stype: 1=MAX_HEALTH, 3=MAX_STAMINA, 5=MAX_MANA (STypeAttribute2nd enum).
    /// Calls on CACQualities* directly — no CBaseQualitiesOffset applied.
    /// </summary>
    public static unsafe bool TryGetObjectAttribute2ndBaseLevel(uint objectId, uint stype, out uint value)
    {
        value = 0;
        // Off-thread: refuse — _inqAttribute2ndBaseLevel is an AC native call (Class A guard).
        if (!MainThreadGuard.IsOnMainThread())
            return false;
        if (_inqAttribute2ndBaseLevel == null)
        {
            if (!Probe() || _inqAttribute2ndBaseLevel == null)
                return false;
        }
        try
        {
            IntPtr weeniePtr = _getWeenieObject!(objectId);
            if (weeniePtr == IntPtr.Zero)
                return false;

            if (!TryGetQualitiesPtr(weeniePtr, out IntPtr qualitiesPtr))
                return false;

            uint retval = 0;
            int result = _inqAttribute2ndBaseLevel(qualitiesPtr, stype, &retval, 0);
            if (result == 0)
                return false;

            value = retval;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Calls CBaseQualities::InqBool on the object's weenie to read a STypeBool property.
    /// Common stypes: ATTACKABLE=19, PLAYER_KILLER=7.
    /// </summary>
    public static unsafe bool TryGetObjectBoolProperty(uint objectId, uint stype, out bool value)
    {
        value = false;

        // Appraisal cache covers inventory items where m_pQualities is null
        // (CBaseQualities::InqBool always returns 0 for such objects).
        if (AppraisalHooks.TryGetCachedBoolProperty(objectId, stype, out value))
            return true;

        // Off-thread: refuse — _inqBool is an AC native call (Class A guard).
        // PropertyUpdateHooks cache fallback also gated below — only the
        // safe network-cache lookup is allowed off-thread there.
        if (!MainThreadGuard.IsOnMainThread())
        {
            // Allow the network-property cache to serve off-thread (pure dict read).
            if (PropertyUpdateHooks.TryGetCachedBoolProperty(objectId, stype, out value))
                return true;
            return false;
        }
        if (_inqBool == null || _getWeenieObject == null)
        {
            if (!Probe() || _inqBool == null || _getWeenieObject == null)
                return false;
        }
        try
        {
            IntPtr weeniePtr = _getWeenieObject(objectId);
            if (weeniePtr == IntPtr.Zero)
                return false;

            if (!TryGetQualitiesPtr(weeniePtr, out IntPtr qualitiesPtr))
                return false;

            IntPtr baseQualitiesPtr = qualitiesPtr + CBaseQualitiesOffset;
            if (!IsReadablePointer(baseQualitiesPtr))
                return false;

            int retval = 0;
            int result = _inqBool(baseQualitiesPtr, stype, &retval);

            if (result == 0)
            {
                // Fallback: network property cache (covers static world objects like doors
                // where m_pQualities is null and InqBool returns 0).
                if (PropertyUpdateHooks.TryGetCachedBoolProperty(objectId, stype, out value))
                    return true;
                return false;
            }

            value = retval != 0;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Calls CBaseQualities::InqString on the object's weenie to read a STypeString property.
    /// Common stypes: NAME=1, TITLE=7, INSCRIPTION=8, SCRIBE_NAME=10.
    /// </summary>
    public static unsafe bool TryGetObjectStringProperty(uint objectId, uint stype, out string value)
    {
        value = string.Empty;

        // Appraisal cache covers inventory items where m_pQualities is null
        // (CBaseQualities::InqString always returns 0 for such objects).
        if (AppraisalHooks.TryGetCachedStringProperty(objectId, stype, out value))
            return true;

        // Off-thread: refuse — _inqString is an AC native call (Class A guard).
        if (!MainThreadGuard.IsOnMainThread())
            return false;
        if (_inqString == null || _getWeenieObject == null)
        {
            if (!Probe() || _inqString == null || _getWeenieObject == null)
                return false;
        }
        try
        {
            IntPtr weeniePtr = _getWeenieObject(objectId);
            if (weeniePtr == IntPtr.Zero)
                return false;

            if (!TryGetQualitiesPtr(weeniePtr, out IntPtr qualitiesPtr))
                return false;

            IntPtr baseQualitiesPtr = qualitiesPtr + CBaseQualitiesOffset;
            if (!IsReadablePointer(baseQualitiesPtr))
                return false;

            // Read s_NullBuffer to properly initialize PStringBase<char>.
            // PStringBase<char> is 4 bytes: { PSRefBuffer<char>* m_buffer }.
            // InqString calls operator= which Release()s the old m_buffer — crashes if zero.
            IntPtr nullBufferAddr = _pStringNullBufferAddr;
            if (!IsReadablePointer(nullBufferAddr))
                return false;
            IntPtr nullBuffer = Marshal.ReadIntPtr(nullBufferAddr);

            byte* pstring = stackalloc byte[4];
            *(IntPtr*)pstring = nullBuffer;

            int result = _inqString(baseQualitiesPtr, stype, pstring);
            if (result == 0)
                return false;

            // PSRefBuffer<char> layout:
            //   +0: Turbine_RefCount { vfptr(4), m_cRef(4) } = 8 bytes
            //   +8: m_len (Int32, includes null terminator)
            //  +12: m_size (UInt32)
            //  +16: m_hash (UInt32)
            //  +20: m_data[] (char array — the actual string)
            IntPtr bufferPtr = *(IntPtr*)pstring;
            if (bufferPtr == IntPtr.Zero || !IsReadablePointer(bufferPtr))
                return false;

            int len = Marshal.ReadInt32(bufferPtr + 8);
            if (len <= 1)
                return false;

            IntPtr dataPtr = bufferPtr + 20;
            if (!IsReadablePointer(dataPtr))
                return false;

            string? str = Marshal.PtrToStringAnsi(dataPtr, len - 1);
            if (string.IsNullOrEmpty(str))
                return false;

            value = str;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Whether AC's combat system considers the object attackable — the same
    /// check the game uses before queueing an attack (NPCs/vendors → false).
    ///
    /// The off-thread plugin pump can't call ClientCombatSystem::ObjectIsAttackable
    /// directly (cross-thread AV class — P0-2 2026-05-17, same reason as the
    /// qualities chokepoint). It used to return hardcoded TRUE off-thread, which
    /// made the plugin classifier promote NPCs/vendors/signs to attackable
    /// creatures → the bot cast war magic at NPCs. Now off-thread callers are
    /// served a snapshot sampled on AC's main thread by PrefetchAttackable
    /// (EndScene always-on path, same as PrefetchPlayerSkills/KnownSpells).
    /// Unknown id off-thread → FALSE (fail safe: never report attackable for an
    /// object whose real bit hasn't been confirmed on the main thread; a fresh
    /// monster becomes attackable within one ~1s prefetch). Main-thread callers
    /// still get the real value directly.
    /// </summary>
    public static bool ObjectIsAttackable(uint objectId)
    {
        if (!MainThreadGuard.IsOnMainThread())
        {
            lock (_attackableCacheLock)
                return _attackableCache.TryGetValue(objectId, out bool a) && a;
        }
        return ComputeAttackableOnMainThread(objectId);
    }

    /// <summary>
    /// The real AC call. MUST run on AC's main thread (callers: the main-thread
    /// branch of <see cref="ObjectIsAttackable"/> and <see cref="PrefetchAttackable"/>).
    /// Defensive: any "can't determine" → true, so a live monster is never
    /// wrongly skipped by a main-thread caller. The off-thread serve layer is
    /// the one that fails safe to false.
    /// </summary>
    private static bool ComputeAttackableOnMainThread(uint objectId)
    {
        if (_getCombatSystem == null || _objectIsAttackable == null || _getWeenieObject == null)
        {
            if (!Probe() || _getCombatSystem == null || _objectIsAttackable == null || _getWeenieObject == null)
                return true;
        }
        try
        {
            IntPtr weeniePtr;
            if (SehTrampoline.IsAvailable)
            {
                weeniePtr = SehTrampoline.CdeclPtrUint(_getWeenieObjectPtr, objectId, out bool avW);
                if (avW)
                {
                    if (System.Threading.Interlocked.Increment(ref _sehAvLogCount) <= 20)
                        RynthLog.Compat($"[SEH] AV in GetWeenieObject for 0x{objectId:X8} — caught, skipping attackable");
                    return true;
                }
            }
            else
            {
                weeniePtr = _getWeenieObject(objectId);
            }

            if (weeniePtr == IntPtr.Zero)
            {
                if (System.Threading.Interlocked.Increment(ref _weenieNullCount) <= 20)
                    RynthLog.Compat($"ObjectIsAttackable: weenie null for 0x{objectId:X8} (count {_weenieNullCount})");
                return true;
            }

            IntPtr combatSystem;
            if (SehTrampoline.IsAvailable)
            {
                combatSystem = SehTrampoline.CdeclPtrVoid(_getCombatSystemPtr, out bool avC);
                if (avC)
                {
                    if (System.Threading.Interlocked.Increment(ref _sehAvLogCount) <= 20)
                        RynthLog.Compat($"[SEH] AV in GetCombatSystem — caught");
                    return true;
                }
            }
            else
            {
                combatSystem = _getCombatSystem();
            }

            if (combatSystem == IntPtr.Zero)
                return true;

            if (SehTrampoline.IsAvailable)
            {
                byte att = SehTrampoline.ThiscallByteUint(_objectIsAttackablePtr, combatSystem, objectId, out bool avA);
                if (avA)
                {
                    if (System.Threading.Interlocked.Increment(ref _sehAvLogCount) <= 20)
                        RynthLog.Compat($"[SEH] AV in ObjectIsAttackable for 0x{objectId:X8} — caught, defaulting true");
                    return true;
                }
                return att != 0;
            }
            return _objectIsAttackable(combatSystem, objectId) != 0;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Samples the REAL ClientCombatSystem::ObjectIsAttackable for every live
    /// weenie on AC's main thread (EndScene always-on path) into a snapshot the
    /// off-thread plugin pump reads via <see cref="ObjectIsAttackable"/>.
    /// Mirrors PrefetchKnownSpells: main-thread-guarded, throttled, fully
    /// defensive, rebuilt from scratch so dead ids self-evict. Without this the
    /// pump can't tell an NPC (attackable=false) from a monster.
    /// </summary>
    public static void PrefetchAttackable()
    {
        if (!MainThreadGuard.IsOnMainThread())
            return;
        if ((DateTime.UtcNow - _lastAttackablePrefetchUtc).TotalMilliseconds < AttackablePrefetchThrottleMs)
            return;
        _lastAttackablePrefetchUtc = DateTime.UtcNow;

        try
        {
            var snapshot = new Dictionary<uint, bool>();
            int n = CObjectMaintHooks.EnumerateLiveWeenieObjectIds(id =>
            {
                snapshot[id] = ComputeAttackableOnMainThread(id);
            });
            if (n <= 0)
                return; // walk failed/empty — keep the last good snapshot

            lock (_attackableCacheLock)
            {
                _attackableCache.Clear();
                foreach (var kv in snapshot)
                    _attackableCache[kv.Key] = kv.Value;
            }

            if (!_loggedAttackableServe)
            {
                _loggedAttackableServe = true;
                int atkCount = 0;
                foreach (var kv in snapshot)
                    if (kv.Value) atkCount++;
                RynthLog.Compat($"ClientObjectHooks: attackable snapshot warm — {snapshot.Count} live objects, {atkCount} attackable (served off-thread to the plugin pump).");
            }
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"PrefetchAttackable exception: {ex.Message}");
        }
    }

    public static void PrefetchObjectIdentity()
    {
        if (!MainThreadGuard.IsOnMainThread())
            return;
        if ((DateTime.UtcNow - _lastObjectIdentityPrefetchUtc).TotalMilliseconds < ObjectIdentityPrefetchThrottleMs)
            return;
        _lastObjectIdentityPrefetchUtc = DateTime.UtcNow;

        try
        {
            var nameSnap = new Dictionary<uint, string>();
            var typeSnap = new Dictionary<uint, uint>();

            int n = CObjectMaintHooks.EnumerateLiveWeenieObjectIds(id =>
            {
                if (TryGetObjectName(id, out string nm) && nm.Length > 0)
                    nameSnap[id] = nm;
                if (TryGetItemType(id, out uint typeFlags))
                    typeSnap[id] = typeFlags;
            });

            if (n <= 0)
                return; // walk failed/empty — keep the last good snapshot

            lock (_objectIdentityCacheLock)
            {
                _objectNameCache.Clear();
                foreach (var kv in nameSnap)
                    _objectNameCache[kv.Key] = kv.Value;
                _objectTypeCache.Clear();
                foreach (var kv in typeSnap)
                    _objectTypeCache[kv.Key] = kv.Value;
            }

            if (!_loggedObjectIdentityServe)
            {
                _loggedObjectIdentityServe = true;
                RynthLog.Compat($"ClientObjectHooks: object identity snapshot warm — {nameSnap.Count} names, {typeSnap.Count} types (served off-thread to the plugin pump).");
            }
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"PrefetchObjectIdentity exception: {ex.Message}");
        }
    }

    public static bool TryGetObjectName(uint objectId, out string name)
    {
        name = string.Empty;
        // Off-thread: serve from the main-thread snapshot populated by PrefetchObjectIdentity().
        // Never walk AC's live object table off-thread (0x0067E779 READ-AV class).
        if (!MainThreadGuard.IsOnMainThread())
        {
            lock (_objectIdentityCacheLock)
            {
                if (_objectNameCache.TryGetValue(objectId, out var cached))
                {
                    name = cached;
                    return true;
                }
            }
            name = string.Empty;
            return false;
        }

        if (_getWeenieObject == null || _getObjectNameStatic == null || _getObjectNameInstance == null)
        {
            if (!Probe() || _getWeenieObject == null || _getObjectNameStatic == null || _getObjectNameInstance == null)
                return false;
        }

        try
        {
            IntPtr pWeenie = _getWeenieObject(objectId);
            if (pWeenie == IntPtr.Zero)
            {
                LogLookup($"Compat: object name lookup miss - GetWeenieObject returned null for 0x{objectId:X8}");
                return false;
            }

            // _getObjectNameInstance reads m_pQualities. Null qualities causes
            // an AV (Corrupted State Exception → AC FailFast in NativeAOT).
            // Gate ONLY this call on qualities being non-null.
            if (TryGetQualitiesPtr(pWeenie, out _) &&
                TryMarshalName(_getObjectNameInstance(pWeenie, NameTypeSingular, 0), out name))
                return true;

            // The cdecl static ACCWeenieObject::GetObjectName also touches qualities
            // for some name-modification paths (corpse-of-X, +N enchant suffix), so
            // gate it on qualities too.
            if (TryGetQualitiesPtr(pWeenie, out _) &&
                TryMarshalName(_getObjectNameStatic(pWeenie, objectId, NameTypeSingular, 0), out name))
                return true;

            // PWD-direct fallback: PublicWeenieDesc + 4 = _name (PStringBase<char>).
            // PStringBase<char> = pointer to PSRefBuffer<char>:
            //   +0  Turbine_RefCount (vfptr 4 + m_cRef 4 = 8)
            //   +8  m_len (int — includes null terminator)
            //   +12 m_size, +16 m_hash
            //   +20 m_data — actual ANSI chars start here.
            // PWD is set up by network packets independently of m_pQualities,
            // so this resolves names for doors, fresh-spawn objects, and pack
            // items where qualities is null.
            if (_weeniePhysicsObjOffset >= 0 && TryReadPwdString(pWeenie, _weeniePhysicsObjOffset + 4 + 4, out name))
                return true;

            LogLookup($"Compat: object name lookup miss - both name calls returned null/empty for 0x{objectId:X8} weenie=0x{pWeenie.ToInt32():X8}");
            return false;
        }
        catch (Exception ex)
        {
            _statusMessage = ex.Message;
            LogLookup($"Compat: object name lookup failed for 0x{objectId:X8} - {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Reads an AC1Legacy::PStringBase&lt;char&gt; field at <paramref name="absoluteOffset"/>
    /// inside an ACCWeenieObject. PStringBase = pointer to PSRefBuffer; PSRefBuffer layout
    /// is { vfptr(4), refCount(4), m_len(4), m_size(4), m_hash(4), m_data[] }. m_len
    /// includes the null terminator. ANSI text. Safe against torn pointers and
    /// uncommitted memory via IsReadablePointer.
    /// </summary>
    private static bool TryReadPwdString(IntPtr weeniePtr, int absoluteOffset, out string value)
    {
        value = string.Empty;
        try
        {
            IntPtr fieldAddr = weeniePtr + absoluteOffset;
            if (!IsReadablePointer(fieldAddr)) return false;

            IntPtr bufferPtr = Marshal.ReadIntPtr(fieldAddr);
            if (bufferPtr == IntPtr.Zero || !IsReadablePointer(bufferPtr)) return false;

            // m_len at PSRefBuffer+8 (includes null terminator).
            IntPtr lenAddr = bufferPtr + 8;
            if (!IsReadablePointer(lenAddr)) return false;
            int rawLen = Marshal.ReadInt32(lenAddr);
            if (rawLen <= 1 || rawLen > 4096) return false; // 1 = empty (just terminator)
            int byteLen = rawLen - 1;

            // m_data at PSRefBuffer+20.
            IntPtr dataAddr = bufferPtr + 20;
            if (!IsReadablePointer(dataAddr)) return false;

            string? str = Marshal.PtrToStringAnsi(dataAddr, byteLen);
            if (string.IsNullOrEmpty(str)) return false;
            value = str;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryMarshalName(IntPtr pName, out string name)
    {
        name = string.Empty;
        if (pName == IntPtr.Zero)
            return false;

        string? str = Marshal.PtrToStringAnsi(pName);
        if (string.IsNullOrWhiteSpace(str))
            return false;

        name = str;
        return true;
    }

    /// <summary>
    /// Reads an object's position from its CPhysicsObj.
    /// Path: GetWeenieObject(id) → ACCWeenieObject+offset → CPhysicsObj+0x48 → Position.
    /// The _phys_obj offset is auto-discovered at runtime via ProbePhysObjOffset.
    /// </summary>
    public static bool TryGetObjectPosition(
        uint objectId,
        out uint objCellId,
        out float x,
        out float y,
        out float z)
    {
        objCellId = 0;
        x = y = z = 0;

        // Off-thread (plugin pump): serve from the main-thread position snapshot.
        // NEVER resolve the object off-thread — TryGetWeenieObjectPtr →
        // _getWeenieObject is AC's CObjectMaint hash walk and AVs cross-thread
        // (dump-verified 0x0067E779 READ-AV). A miss (object not sampled yet, or
        // already gone) returns false; the pump treats it as out-of-range this
        // tick and re-checks next tick once PrefetchPositions samples it.
        if (!MainThreadGuard.IsOnMainThread())
        {
            lock (_positionCacheLock)
            {
                if (_positionCache.TryGetValue(objectId, out PosEntry p))
                {
                    objCellId = p.Cell;
                    x = p.X; y = p.Y; z = p.Z;
                    return true;
                }
            }
            return false;
        }

        return ReadObjectPositionLive(objectId, out objCellId, out x, out y, out z);
    }

    /// <summary>
    /// Live position read — resolves the weenie and walks CPhysicsObj. ONLY safe
    /// on AC's main thread (resolves _getWeenieObject = CObjectMaint walk). Called
    /// by the on-main-thread <see cref="TryGetObjectPosition"/> and by
    /// <see cref="PrefetchPositions"/>.
    /// </summary>
    private static bool ReadObjectPositionLive(
        uint objectId,
        out uint objCellId,
        out float x,
        out float y,
        out float z)
    {
        objCellId = 0;
        x = y = z = 0;

        if (_weeniePhysicsObjOffset < 0)
        {
            ProbePhysObjOffset();
            if (_weeniePhysicsObjOffset < 0)
            {
                LogLookup("Compat: objpos - physObj offset probe failed, cannot read positions");
                return false;
            }
        }

        if (!TryGetWeenieObjectPtr(objectId, out IntPtr weeniePtr))
            return false;

        try
        {
            IntPtr physicsObj = Marshal.ReadIntPtr(weeniePtr + _weeniePhysicsObjOffset);
            if (physicsObj == IntPtr.Zero)
                return false;

            // Guard: weeniePtr may have been freed between _getWeenieObject and here
            // (TOCTOU — object deleted on main thread mid-tick). physicsObj would then
            // be garbage (e.g. 0xC1085000) pointing to an unmapped page; the vtable
            // read below would AV in NativeAOT, bypassing try/catch. IsReadablePointer
            // catches the unmapped-page case; the vtable module check catches any
            // garbage-but-mapped value.
            if (!IsReadablePointer(physicsObj))
                return false;

            IntPtr vtable = Marshal.ReadIntPtr(physicsObj);
            if (!SmartBoxLocator.IsPointerInModule(vtable))
                return false;

            IntPtr pos = physicsObj + PhysicsPositionOffset;
            objCellId = unchecked((uint)Marshal.ReadInt32(pos + PositionObjCellIdOffset));
            x = ReadFloat(pos + PositionOriginXOffset);
            y = ReadFloat(pos + PositionOriginYOffset);
            z = ReadFloat(pos + PositionOriginZOffset);
            return true;
        }
        catch (Exception ex)
        {
            LogLookup($"Compat: object position read failed for 0x{objectId:X8} - {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Samples every live object's position on AC's main thread (EndScene path)
    /// into <see cref="_positionCache"/>, which the off-thread plugin pump reads
    /// via <see cref="TryGetObjectPosition"/>. Sampled every frame (positions are
    /// volatile) and ZERO-ALLOC: the dict is reused (Clear + re-add of struct
    /// values) and the per-id callback is a cached delegate, so this never
    /// allocates inside the EndScene reverse-P/Invoke (which would risk the
    /// NativeAOT GC fail-fast that moved TickAll off this thread). The lock is
    /// held across the walk to stay zero-alloc; the pump's per-id read contends
    /// only briefly. Walk runs every frame, so a transient empty (walk failure)
    /// self-heals next frame — no last-good retention needed (unlike the
    /// throttled attackable/identity snapshots).
    /// </summary>
    public static void PrefetchPositions()
    {
        if (!MainThreadGuard.IsOnMainThread())
            return;
        if ((DateTime.UtcNow - _lastPositionPrefetchUtc).TotalMilliseconds < PositionPrefetchThrottleMs)
            return;
        _lastPositionPrefetchUtc = DateTime.UtcNow;
        if (_weeniePhysicsObjOffset < 0)
        {
            ProbePhysObjOffset();
            if (_weeniePhysicsObjOffset < 0)
                return;
        }

        try
        {
            lock (_positionCacheLock)
            {
                _positionCache.Clear();
                _weeniePtrBack.Clear();
                CObjectMaintHooks.EnumerateLiveWeenieObjectIds(_capturePositionDelegate);
            }

            // Publish the freshly-walked id->weeniePtr map to off-thread readers
            // (GetWeenieObjectResolve). The swap is brief and is NOT held during
            // the walk, so the pump's resolver reads never stall on the walk.
            lock (_weeniePtrSwapLock)
                (_weeniePtrFront, _weeniePtrBack) = (_weeniePtrBack, _weeniePtrFront);

            if (!_loggedPositionServe && _positionCache.Count > 0)
            {
                _loggedPositionServe = true;
                RynthLog.Compat($"ClientObjectHooks: position snapshot warm — {_positionCache.Count} live objects (served off-thread to the plugin pump).");
            }

            LogSnapshotDiagnostics();
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"PrefetchPositions exception: {ex.Message}");
        }
    }

    // ── DIAGNOSTIC (logging only, temporary) ────────────────────────────────
    // Runs on AC's main thread (called from PrefetchPositions). Every ~10s logs
    // the CURRENT snapshot sizes, then probes a few objects that have a position
    // but no cached name with a LIVE main-thread name/type read. Interpretation:
    //   live read WORKS but cache empty  → snapshot POPULATION is broken
    //   live read FAILS                  → AC genuinely lacks the object's data
    private static void LogSnapshotDiagnostics()
    {
        if ((DateTime.UtcNow - _lastSnapshotDiagUtc).TotalMilliseconds < SnapshotDiagThrottleMs)
            return;
        _lastSnapshotDiagUtc = DateTime.UtcNow;
        try
        {
            int posN, atkN, atkTrue = 0, nameN, typeN, ptrN;
            lock (_positionCacheLock) posN = _positionCache.Count;
            lock (_attackableCacheLock)
            {
                atkN = _attackableCache.Count;
                foreach (bool v in _attackableCache.Values) if (v) atkTrue++;
            }
            lock (_objectIdentityCacheLock) { nameN = _objectNameCache.Count; typeN = _objectTypeCache.Count; }
            lock (_weeniePtrSwapLock) ptrN = _weeniePtrFront.Count;
            RynthLog.Compat($"[SnapshotDiag] pos={posN} attackable={atkN}({atkTrue} atk) names={nameN} types={typeN} ptr={ptrN}");

            // Compare CObjectMaint's physics tables — object_table (+0x84) and
            // null_object_table (+0x9C, objects with pending/null weenie) —
            // against weenie_object_table (+0xB4) that our snapshots use.
            // "only" = ids present in that table but NOT in the weenie ptr cache.
            _diagObjTableN = 0;
            int objN = CObjectMaintHooks.EnumerateTableIds(0x84, _diagObjTableVisit);
            int objOnly = 0;
            lock (_weeniePtrSwapLock)
                for (int i = 0; i < _diagObjTableN; i++)
                    if (!_weeniePtrFront.ContainsKey(_diagObjTableIds[i])) objOnly++;

            _diagObjTableN = 0;
            int nullN = CObjectMaintHooks.EnumerateTableIds(0x9C, _diagObjTableVisit);
            int nullOnly = 0, sampleN = 0;
            lock (_weeniePtrSwapLock)
                for (int i = 0; i < _diagObjTableN; i++)
                {
                    uint id = _diagObjTableIds[i];
                    if (_weeniePtrFront.ContainsKey(id)) continue;
                    nullOnly++;
                    if (sampleN < 3) _diagSampleBuf[sampleN++] = id;
                }
            RynthLog.Compat($"[SnapshotDiag] objectTable={objN}(only {objOnly}) nullTable={nullN}(only {nullOnly}) weenieTable={ptrN}");

            // Probe a few null_object_table ids that aren't in the weenie cache —
            // LIVE main-thread resolve + name/type. If they resolve + read, then
            // null_object_table holds the missing monsters and we enumerate it.
            for (int i = 0; i < sampleN; i++)
            {
                uint id = _diagSampleBuf[i];
                IntPtr p = _getWeenieObjectNative != null ? _getWeenieObjectNative(id) : IntPtr.Zero;
                bool gotName = TryGetObjectName(id, out _);
                bool gotType = TryGetItemType(id, out uint tf);
                RynthLog.Compat($"[SnapshotDiag] null-probe 0x{id:X8}: resolve={p != IntPtr.Zero} liveName={gotName} liveType={gotType}(0x{tf:X})");
            }
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"[SnapshotDiag] exception: {ex.Message}");
        }
    }

    // Cached delegate target for PrefetchPositions' enumerate. Runs on the main
    // thread under _positionCacheLock; reads the live position and stores it.
    // Method group is cached in _capturePositionDelegate so the enumerate call
    // allocates no closure per frame.
    private static void CapturePositionForId(uint id)
    {
        // Resolve natively here (we are on AC's main thread, so the CObjectMaint
        // walk is safe) and stash the ptr so the off-thread pump can resolve it
        // via GetWeenieObjectResolve without walking AC's table itself.
        IntPtr weeniePtr = _getWeenieObjectNative != null ? _getWeenieObjectNative(id) : IntPtr.Zero;
        if (weeniePtr != IntPtr.Zero)
            _weeniePtrBack[id] = weeniePtr;
        if (ReadObjectPositionLive(id, out uint cell, out float x, out float y, out float z))
            _positionCache[id] = new PosEntry { Cell = cell, X = x, Y = y, Z = z };
    }

    /// <summary>
    /// Gated replacement for the raw _getWeenieObject delegate. On AC's main
    /// thread it resolves natively (the CObjectMaint walk is safe there). Off the
    /// main thread (plugin pump) it serves the pointer cached by the main-thread
    /// position walk and NEVER walks AC's object table — that walk is the
    /// dump-verified 0x0067E779 READ-AV. A cache miss returns Zero so every
    /// downstream read fails closed (false/default) instead of crashing.
    /// </summary>
    private static IntPtr GetWeenieObjectResolve(uint objectId)
    {
        if (MainThreadGuard.IsOnMainThread())
            return _getWeenieObjectNative != null ? _getWeenieObjectNative(objectId) : IntPtr.Zero;
        lock (_weeniePtrSwapLock)
            return _weeniePtrFront.TryGetValue(objectId, out IntPtr p) ? p : IntPtr.Zero;
    }

    // CPhysicsObj::m_state offset — confirmed from Ghidra set_state disasm: MOV [ESI+0xa8], EAX
    private const int PhysicsStateOffset = 0xA8;

    /// <summary>
    /// Reads CPhysicsObj::m_state directly from memory.
    /// Notable bits: ETHEREAL_PS=0x4 (door open/passable), STATIC_PS=0x1, NODRAW_PS=0x20.
    /// </summary>
    public static bool TryGetObjectPhysicsState(uint objectId, out uint state)
    {
        state = 0;

        if (_weeniePhysicsObjOffset < 0)
        {
            ProbePhysObjOffset();
            if (_weeniePhysicsObjOffset < 0)
                return false;
        }

        if (!TryGetWeenieObjectPtr(objectId, out IntPtr weeniePtr))
            return false;

        try
        {
            IntPtr physicsObj = Marshal.ReadIntPtr(weeniePtr + _weeniePhysicsObjOffset);
            if (physicsObj == IntPtr.Zero)
                return false;

            if (!IsReadablePointer(physicsObj))
                return false;

            IntPtr vtable = Marshal.ReadIntPtr(physicsObj);
            if (!SmartBoxLocator.IsPointerInModule(vtable))
                return false;

            state = unchecked((uint)Marshal.ReadInt32(physicsObj + PhysicsStateOffset));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads an object's facing heading (0–360°, clockwise, 0=North) from its CPhysicsObj quaternion.
    /// Same qw/qz offsets as the player position struct.
    /// </summary>
    public static bool TryGetObjectHeading(uint objectId, out float headingDegrees)
    {
        headingDegrees = 0;

        if (_weeniePhysicsObjOffset < 0)
        {
            ProbePhysObjOffset();
            if (_weeniePhysicsObjOffset < 0)
                return false;
        }

        if (!TryGetWeenieObjectPtr(objectId, out IntPtr weeniePtr))
            return false;

        try
        {
            IntPtr physicsObj = Marshal.ReadIntPtr(weeniePtr + _weeniePhysicsObjOffset);
            if (physicsObj == IntPtr.Zero)
                return false;

            IntPtr vtable = Marshal.ReadIntPtr(physicsObj);
            if (!SmartBoxLocator.IsPointerInModule(vtable))
                return false;

            IntPtr pos = physicsObj + PhysicsPositionOffset;
            float qw = ReadFloat(pos + PositionQwOffset);
            float qz = ReadFloat(pos + PositionQzOffset);

            // Same formula as NavigationEngine / CorpseOpenController
            double physYawDeg = 2.0 * Math.Atan2(qz, qw) * (180.0 / Math.PI);
            headingDegrees = (float)(((-physYawDeg) % 360.0 + 720.0) % 360.0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static float ReadFloat(IntPtr address)
    {
        int bits = Marshal.ReadInt32(address);
        return BitConverter.Int32BitsToSingle(bits);
    }

    /// <summary>
    /// Auto-discovers the _phys_obj offset by scanning the player's weenie for the
    /// known CPhysicsObj pointer (from SmartBox). Falls back to FallbackWeeniePhysicsObjOffset
    /// if the probe can't run (e.g. player not loaded yet).
    /// </summary>
    private static void ProbePhysObjOffset()
    {
        if (!SmartBoxLocator.TryGetPlayer(out IntPtr playerPhysObj, out uint playerId, out _))
            return;

        if (playerId == 0 || playerPhysObj == IntPtr.Zero || _getWeenieObject == null)
            return;

        IntPtr playerWeenie;
        try
        {
            playerWeenie = _getWeenieObject(playerId);
        }
        catch { return; }

        if (playerWeenie == IntPtr.Zero)
        {
            RynthLog.Verbose($"Compat: physOffsetProbe - GetWeenieObject returned null for player 0x{playerId:X8}");
            return;
        }

        int targetValue = playerPhysObj.ToInt32();
        int foundOffset = -1;

        for (int scan = 0x00; scan <= 0x200; scan += 4)
        {
            try
            {
                int val = Marshal.ReadInt32(playerWeenie + scan);
                if (val == targetValue)
                {
                    foundOffset = scan;
                    break;
                }
            }
            catch { break; }
        }

        if (foundOffset >= 0)
        {
            _weeniePhysicsObjOffset = foundOffset;
            RynthLog.Verbose($"Compat: physOffsetProbe discovered _phys_obj at +0x{foundOffset:X2} (player=0x{playerId:X8})");
        }
        else
        {
            _weeniePhysicsObjOffset = FallbackWeeniePhysicsObjOffset;
            RynthLog.Verbose($"Compat: physOffsetProbe no match found, using fallback +0x{FallbackWeeniePhysicsObjOffset:X2} (player=0x{playerId:X8})");
        }
    }

    /// <summary>
    /// Calls ACCWeenieObject::GetNumContainedItems() — the AC client's own count of
    /// items in the container. Returns -1 if the weenie is unavailable.
    /// </summary>
    public static int GetNumContainedItems(uint objectId)
    {
        // P0-2 (2026-05-17): bypasses TryGetQualitiesPtr — direct thiscall
        // into ACCWeenieObject::GetNumContainedItems, which walks the weenie's
        // container list. Called during corpse-loot; cross-thread against AC
        // streaming items into a just-opened corpse on the main thread reads a
        // torn pointer and AVs in AC code (read [null+0x1C] at
        // acclient.exe+0x16547B, 2026-05-17 10:06). Off the AC main thread,
        // return the existing "unavailable" sentinel; the loot controller
        // already handles -1 by waiting / using packet inventory.
        if (!MainThreadGuard.IsOnMainThread())
            return -1;

        if (_getWeenieObject == null && !Probe())
            return -1;
        if (_getWeenieObject == null)
            return -1;

        IntPtr weeniePtr = _getWeenieObject(objectId);
        if (weeniePtr == IntPtr.Zero)
            return -1;

        unsafe
        {
            IntPtr fn = _getNumContainedItemsPtr != IntPtr.Zero ? _getNumContainedItemsPtr : new IntPtr(ReferenceGetNumContainedItems);
            return ((delegate* unmanaged[Thiscall]<IntPtr, int>)fn)(weeniePtr);
        }
    }

    /// <summary>
    /// Calls ACCWeenieObject::GetNumContainedContainers() — the AC client's own count of
    /// how many packs/foci occupy container slots. Returns -1 if the weenie is unavailable.
    /// </summary>
    public static int GetNumContainedContainers(uint objectId)
    {
        // P0-2 (2026-05-17): same cross-thread AV class as
        // GetNumContainedItems — direct thiscall into AC's container walk.
        // Off the AC main thread, return the "unavailable" sentinel.
        if (!MainThreadGuard.IsOnMainThread())
            return -1;

        if (_getWeenieObject == null && !Probe())
            return -1;
        if (_getWeenieObject == null)
            return -1;

        IntPtr weeniePtr = _getWeenieObject(objectId);
        if (weeniePtr == IntPtr.Zero)
            return -1;

        unsafe
        {
            IntPtr fn = _getNumContainedContainersPtr != IntPtr.Zero ? _getNumContainedContainersPtr : new IntPtr(ReferenceGetNumContainedContainers);
            return ((delegate* unmanaged[Thiscall]<IntPtr, int>)fn)(weeniePtr);
        }
    }

    private static void LogLookup(string message)
    {
        if (_lookupLogCount >= MaxLookupLogs)
            return;

        _lookupLogCount++;
        RynthLog.Verbose(message);
    }
}
