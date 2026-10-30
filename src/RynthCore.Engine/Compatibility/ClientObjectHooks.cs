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
    // plugin pump (and the GetEngineStatusJson accessor) can't read them live; snapshot here on the
    // EndScene path and serve the cache.
    private static readonly object _playerStatsCacheLock = new();
    private static long _cachedTotalXp;
    private static long _cachedLuminance;
    private static int _cachedDeaths;
    private static float _cachedVitae = 1.0f;
    private static int _cachedEncVal;       // EncumbranceVal (current burden)
    private static int _cachedEncCap;       // EncumbranceCapacity (max)
    private static uint _cachedLandcell;    // player cell id (landblock = cell >> 16)
    private static float _cachedPx, _cachedPy, _cachedPz;   // [status-export] player cell-local position (for the live map dot)
    private static uint _playerStatsCacheOwner;
    // Primary attributes 1..6 (index = stype), buffed and raw, and the int64 properties plugins
    // ask about (TotalExperience 1, AvailableExperience 2, AvailableLuminance 6, MaximumLuminance 7):
    // main-thread-only Inq* reads, served to the off-thread plugin pump from this snapshot.
    private static readonly uint[] _cachedAttrBuffed = new uint[7];
    private static readonly uint[] _cachedAttrRaw = new uint[7];
    // 1 TotalExperience, 2 AvailableExperience, 6 AvailableLuminance, 7 MaximumLuminance, then
    // Aelrynth's Bank session totals (custom ids, absent on other servers): 9101 Radiance earned,
    // 9102 Luminance auto-banked, 9103 Luminance drawn from the bank. RynthTracker reads them.
    private static readonly uint[] PlayerQuadStypes = { 1u, 2u, 6u, 7u, 9101u, 9102u, 9103u };
    private static readonly Dictionary<uint, long> _cachedPlayerQuad = new();
    // Secondary attributes (vitals) by stype 1..6 from InqAttribute2ndBaseLevel: the buffed
    // maximums RynthAi's getcharvital_buffedmax and RynthLua read.
    private static readonly uint[] _cachedAttr2nd = new uint[7];
    // Skill levels by stype 1..54 (InqSkillLevel), buffed and raw.
    private const int MaxSkillStype = 54;
    private static readonly int[] _cachedSkillLevelBuffed = new int[MaxSkillStype + 1];
    private static readonly int[] _cachedSkillLevelRaw = new int[MaxSkillStype + 1];
    private static readonly bool[] _cachedSkillLevelKnown = new bool[MaxSkillStype + 1];
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
    // Live PublicWeenieDesc _location (CURRENT_WIELDED_LOCATION) per object, so the
    // off-thread pump reads where an item is wielded from the client, not from an
    // appraisal/update cache that can be stale after an equip swap.
    private static readonly Dictionary<uint, int> _objectLocationCache = new();
    // Live PublicWeenieDesc _stackSize per object: the caches only know a stack the
    // server sent an update or appraisal for, so an unappraised stack read as 1.
    private static readonly Dictionary<uint, int> _objectStackCache = new();
    private static DateTime _lastObjectIdentityPrefetchUtc = DateTime.MinValue;
    private static bool _loggedObjectIdentityServe;
    private const int ObjectIdentityPrefetchThrottleMs = 500;

    // Create-time identity seeds. ACCObjectMaint::CreateObject receives the server's
    // PublicWeenieDesc, which already carries the object's name and ITEM_TYPE before
    // qualities/appraisal exist. The main-thread snapshot above can't resolve names for
    // ~10 s after login (qualities not populated yet), so off-thread readers fall back to
    // these seeds when the snapshot has no entry (or reports type 0). Entries live from
    // create until delete; the cap only guards against a missed delete hook.
    private static readonly object _createSeedLock = new();
    private static readonly Dictionary<uint, string> _createSeedNames = new(1024);
    private static readonly Dictionary<uint, uint> _createSeedTypes = new(1024);
    private const int CreateSeedCap = 16384;
    // PublicWeenieDesc field offsets (Chorizite Weenie.cs; same layout the PWD-direct
    // fallbacks in TryGetObjectName / TryGetItemType read from the embedded copy).
    // PwdTypeOffset is declared with the other PublicWeenieDesc offsets further down.
    private const int PwdNameOffset = 4;
    private static int _createSeedCount;
    private static bool _loggedCreateSeedServe;

    // Every id the same walk visited, swapped whole (readers never see a half-built array).
    private static volatile uint[] _liveObjectIds = Array.Empty<uint>();

    /// <summary>
    /// Ids of every weenie object the client knew at the last identity snapshot (main-thread
    /// walk, at most ~0.5 s old). Empty until the first snapshot.
    /// </summary>
    public static uint[] LiveObjectIds => _liveObjectIds;

    // ── Snapshot-only reads (monster nameplates) ────────────────────────────
    // The same main-thread snapshots the off-thread pump is served, readable
    // from ANY thread without resolving the object: a dictionary lookup, never
    // an AC call. Used where a few hundred ids are scanned several times a
    // second (ImGui/MonsterHud), so the live paths' per-id native calls stay out.

    /// <summary>The attackable snapshot's value (main-thread ObjectIsAttackable, ~0.5 s old). False when not sampled.</summary>
    public static bool TryGetSnapshotAttackable(uint objectId, out bool attackable)
    {
        lock (_attackableCacheLock)
            return _attackableCache.TryGetValue(objectId, out attackable);
    }

    /// <summary>The identity snapshot's name (~0.5 s old).</summary>
    public static bool TryGetSnapshotName(uint objectId, out string name)
    {
        lock (_objectIdentityCacheLock)
        {
            if (_objectNameCache.TryGetValue(objectId, out string? n)) { name = n; return true; }
        }
        name = string.Empty;
        return false;
    }

    /// <summary>The identity snapshot's ITEM_TYPE flags (~0.5 s old).</summary>
    public static bool TryGetSnapshotItemType(uint objectId, out uint typeFlags)
    {
        lock (_objectIdentityCacheLock)
            return _objectTypeCache.TryGetValue(objectId, out typeFlags);
    }

    /// <summary>The position snapshot's cell + landblock-local origin (~0.1 s old).</summary>
    public static bool TryGetSnapshotPosition(uint objectId, out uint objCellId, out float x, out float y, out float z)
    {
        lock (_positionCacheLock)
        {
            if (_positionCache.TryGetValue(objectId, out PosEntry p))
            {
                objCellId = p.Cell; x = p.X; y = p.Y; z = p.Z;
                return true;
            }
        }
        objCellId = 0; x = y = z = 0;
        return false;
    }

    /// <summary>
    /// The PWD snapshot's ObjectDescriptionFlags bitfield (BF_PLAYER 0x8, BF_CORPSE
    /// 0x2000, BF_PORTAL 0x40000, ...), container and wielder (~0.1 s old). Any
    /// thread: on the main thread too it reads the snapshot, never AC memory
    /// (the Sense scan, 2026-09-30).
    /// </summary>
    public static bool TryGetSnapshotPwdInfo(uint objectId, out uint bitfield, out uint containerId, out uint wielderId)
    {
        if (TryGetPwdSnapshot(objectId, out PwdEntry e))
        {
            bitfield = e.Bitfield;
            containerId = e.Container;
            wielderId = e.Wielder;
            return true;
        }
        bitfield = containerId = wielderId = 0;
        return false;
    }

    // Position snapshot — mirrors the attackable/identity snapshots, but sampled
    // EVERY EndScene (no throttle) because positions change per-frame. The
    // off-thread plugin pump reads position from here instead of resolving
    // _getWeenieObject(id) live — that resolution is AC's CObjectMaint hash walk
    // and is the dump-verified 0x0067E779 READ-AV when done off the main thread.
    // MUST stay ZERO-ALLOC (reused dict via Clear()+re-add, cached enumerate
    // delegate, struct values) so per-frame capture in the EndScene reverse-
    // P/Invoke can't trigger a GC → NativeAOT fail-fast (the same hazard that
    // moved TickAll off this thread; see EngineFrameController.OnEndScene).
    // Qw/Qz and State (CPhysicsObj m_state) ride along since 2026-09-30 so the
    // off-thread TryGetObjectHeading / TryGetObjectPhysicsState are served from
    // this snapshot too, instead of dereferencing a cached weenie pointer.
    private struct PosEntry { public uint Cell; public float X, Y, Z, Qw, Qz; public uint State; public bool HasState; }

    // PublicWeenieDesc field snapshot (2026-09-30 main-thread audit). The off-thread
    // wcid / bitfield / icon / ownership readers used to take a raw weenie pointer
    // from _weeniePtrFront (up to ~100 ms old) and read the PWD fields live; a
    // weenie freed and reused in that window passes the page probe and hands the bot
    // garbage (a wrong door bit, container or wcid), and a decommit between probe and
    // read is an uncatchable AV. The same 10 Hz main-thread walk now copies the
    // fields for EVERY id that resolves a weenie (pack items have no position), into
    // a back/front pair swapped with the pointer maps under _weeniePtrSwapLock.
    // Struct values in reused dictionaries: zero-alloc in the EndScene walk.
    // IconOverlay / IconUnderlay ride along since 2026-09-30 (script window item icons).
    // Since the property-coverage work (2026-09-30) the whole PublicWeenieDesc is copied
    // (PwdLayout.Size bytes): every CreateObject field (value, burden, useability, radar,
    // monarch, pet owner, ...) answers off the main thread as the property it came from.
    [System.Runtime.CompilerServices.InlineArray(PwdLayout.Size)]
    private struct PwdBytes { private byte _first; }
    private struct PwdEntry
    {
        public PwdBytes Bytes;
        public readonly uint Wcid => U32(in this, PwdLayout.Wcid);
        public readonly uint Icon => U32(in this, PwdLayout.Icon);
        public readonly uint IconOverlay => U32(in this, PwdLayout.IconOverlay);
        public readonly uint IconUnderlay => U32(in this, PwdLayout.IconUnderlay);
        public readonly uint Container => U32(in this, PwdLayout.Container);
        public readonly uint Wielder => U32(in this, PwdLayout.Wielder);
        public readonly uint Location => U32(in this, PwdLayout.Location);
        public readonly uint Bitfield => U32(in this, PwdLayout.Bitfield);
        private static uint U32(in PwdEntry e, int offset)
            => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(((ReadOnlySpan<byte>)e.Bytes).Slice(offset));
    }
    private static Dictionary<uint, PwdEntry> _pwdFront = new(512);
    private static Dictionary<uint, PwdEntry> _pwdBack = new(512);
    private const int PwdWcidOffset = PwdLayout.Wcid, PwdIconOffset = PwdLayout.Icon, PwdIconOverlayOffset = PwdLayout.IconOverlay,
                      PwdIconUnderlayOffset = PwdLayout.IconUnderlay, PwdContainerOffset = PwdLayout.Container,
                      PwdWielderOffset = PwdLayout.Wielder, PwdLocationOffset = PwdLayout.Location, PwdBitfieldOffset = PwdLayout.Bitfield;
    private static readonly object _positionCacheLock = new();
    // Double-buffered (2026-10-02): the walk fills _positionBack with no lock held, then the
    // two swap under _positionCacheLock. Off-thread readers lock only to look up the front.
    // The walk used to fill the front under the lock, so every plugin position read waited
    // for the whole walk, and a slow walk (MemoryProbe) held the plugin tick to 4-9 a second.
    private static Dictionary<uint, PosEntry> _positionCache = new(512);
    private static Dictionary<uint, PosEntry> _positionBack = new(512);
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
    // (Since 2026-09-30 GetWeenieObjectResolve returns Zero off-thread instead of
    // serving this map: off-thread readers use value snapshots. The map is kept
    // for the snapshot diagnostics and swaps with _pwdFront.) Double-buffered
    // so off-thread reads take only the brief swap lock and never block on the
    // walk (the per-frame single-lock walk previously stalled the render thread).
    private static readonly object _weeniePtrSwapLock = new();
    private static Dictionary<uint, IntPtr> _weeniePtrFront = new(512);
    private static Dictionary<uint, IntPtr> _weeniePtrBack = new(512);

    // Diagnostic (temporary): periodic snapshot-size log + live-read probe of
    // objects with a position but no cached name. Remove once the missing-
    // monsters cause is pinned. _diagSampleBuf preallocated to stay low-alloc.
    private static DateTime _lastSnapshotDiagUtc = DateTime.MinValue;

    // Time AC's main thread spends in each snapshot walk, reported (and reset) by the
    // [SnapshotDiag] line every 10 s. The 2026-10-02 frame-rate collapse was these walks
    // growing with AC's heap; this line is how a log shows whether they stay small.
    private struct WalkStat
    {
        public int Count;
        public long Ticks, MaxTicks;
        public void Add(long ticks) { Count++; Ticks += ticks; if (ticks > MaxTicks) MaxTicks = ticks; }
        public string TakeText()
        {
            double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            string text = Count == 0 ? "-" : $"{Count}x avg {Ticks * toMs / Count:0.00} max {MaxTicks * toMs:0.00} ms";
            this = default;
            return text;
        }
    }
    private static WalkStat _walkPositions, _walkIdentity, _walkAttackable;
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
    /// Checks if a pointer's memory page is committed and not NOACCESS/GUARD.
    /// Critical for NativeAOT where try/catch does NOT catch access violations.
    /// Answered by <see cref="MemoryProbe"/> (was a VirtualQuery per call, whose cost grows
    /// with AC's heap under WOW64 - the 2026-10-02 frame-rate collapse; same answer).
    /// </summary>
    internal static bool IsReadablePointer(IntPtr ptr) => MemoryProbe.IsAccessible(ptr);

    /// <summary>
    /// Like <see cref="IsReadablePointer"/> for a whole span: every page of the span must
    /// pass (<see cref="MemoryProbe"/>; a span reaching into an uncommitted page fails closed).
    /// </summary>
    internal static bool IsReadableSpan(IntPtr ptr, int length) => MemoryProbe.IsAccessible(ptr, length);

    private const uint PAGE_WRITABLE_MASK = 0x04 /*READWRITE*/ | 0x08 /*WRITECOPY*/
                                          | 0x40 /*EXECUTE_READWRITE*/ | 0x80 /*EXECUTE_WRITECOPY*/;

    /// <summary>
    /// Like <see cref="IsReadablePointer"/> but also requires a writable page.
    /// Probe before a raw store into AC memory: under NativeAOT a write AV is not
    /// catchable, so a freed or read-only target must fail closed instead.
    /// </summary>
    internal static bool IsWritablePointer(IntPtr ptr) => MemoryProbe.IsWritable(ptr);

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
    /// Internal (not private): EnchantmentHooks.ReadEnchantmentsFromQualities
    /// reuses this exact check (deep-audit finding #23) rather than keeping
    /// a second, divergent vtable-cache of its own — there's only one
    /// canonical CACQualities vtable for the whole engine to learn.
    /// </summary>
    internal static bool IsCacQualitiesObject(IntPtr ptr)
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
        // Off-thread: _getVitaeValue is an AC native call (Class A guard). The player's vitae
        // comes from the main-thread snapshot (PrefetchPlayerStats); others refuse.
        if (!MainThreadGuard.IsOnMainThread())
        {
            if (playerId == 0 || playerId != ClientHelperHooks.GetPlayerId())
                return false;
            lock (_playerStatsCacheLock)
            {
                if (_playerStatsCacheOwner != playerId) return false;
                value = _cachedVitae;
                return true;
            }
        }
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
        // Lazy probe only on AC's main thread: a probe re-binds delegates the main
        // thread uses and resets SmartBoxLocator's shared candidate list. Off-thread
        // callers skip it and fall through to the cached-skill serve below.
        if (_getWeenieObject == null && MainThreadGuard.IsOnMainThread())
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
    /// plugin pump and the GetEngineStatusJson accessor can read. These are all main-thread-only
    /// Inq* reads. Throttled; fully defensive.
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
            int encVal = sameOwner ? _cachedEncVal : 0;
            int encCap = sameOwner ? _cachedEncCap : 0;
            uint cell = sameOwner ? _cachedLandcell : 0;
            float px = sameOwner ? _cachedPx : 0f, py = sameOwner ? _cachedPy : 0f, pz = sameOwner ? _cachedPz : 0f;

            if (TryGetObjectQuadProperty(playerId, 1u, out long x) && x > 0) xp = x;     // PropertyInt64.TotalExperience
            if (TryGetObjectQuadProperty(playerId, 6u, out long l) && l > 0) lum = l;    // PropertyInt64.AvailableLuminance
            if (TryGetObjectIntProperty(playerId, 43u, out int d)) deaths = d;           // PropertyInt.NumDeaths
            if (TryGetVitae(playerId, out float v)) vitae = v;
            if (TryGetObjectIntProperty(playerId, 5u, out int ev)) encVal = ev;          // PropertyInt.EncumbranceVal (current burden)
            // EncumbranceCapacity (PropertyInt 96) is a server-calculated value the retail client
            // usually doesn't store in its queryable int table (AC's UI has no burden bar). When it's
            // absent, derive capacity from buffed Strength using AC's formula (capacity = Strength*150).
            if (TryGetObjectIntProperty(playerId, 96u, out int ec) && ec > 0) encCap = ec;
            else if (TryGetObjectAttribute(playerId, 1u, 0, out uint str) && str > 0) encCap = (int)(str * 150u);
            if (TryGetObjectPosition(playerId, out uint c, out float fx, out float fy, out float fz) && c != 0)
            { cell = c; px = fx; py = fy; pz = fz; }   // [status-export] cell-local position for the live map dot

            var attrBuffed = new uint[7];
            var attrRaw = new uint[7];
            for (uint a = 1; a <= 6; a++)
            {
                if (TryGetObjectAttribute(playerId, a, 0, out uint ab)) attrBuffed[a] = ab;
                if (TryGetObjectAttribute(playerId, a, 1, out uint ar)) attrRaw[a] = ar;
            }
            var attr2nd = new uint[7];
            for (uint a2 = 1; a2 <= 6; a2++)
                if (TryGetObjectAttribute2ndBaseLevel(playerId, a2, out uint v2)) attr2nd[a2] = v2;
            var skB = new int[MaxSkillStype + 1];
            var skR = new int[MaxSkillStype + 1];
            var skK = new bool[MaxSkillStype + 1];
            for (uint sk = 1; sk <= MaxSkillStype; sk++)
            {
                bool b = TryGetObjectSkillLevel(playerId, sk, 0, out int lb);
                bool r = TryGetObjectSkillLevel(playerId, sk, 1, out int lr);
                if (b) skB[sk] = lb;
                if (r) skR[sk] = lr;
                skK[sk] = b || r;
            }
            var quad = new Dictionary<uint, long>();
            foreach (uint q in PlayerQuadStypes)
                if (TryGetObjectQuadProperty(playerId, q, out long qv)) quad[q] = qv;

            lock (_playerStatsCacheLock)
            {
                Array.Copy(attrBuffed, _cachedAttrBuffed, 7);
                Array.Copy(attrRaw, _cachedAttrRaw, 7);
                Array.Copy(attr2nd, _cachedAttr2nd, 7);
                Array.Copy(skB, _cachedSkillLevelBuffed, skB.Length);
                Array.Copy(skR, _cachedSkillLevelRaw, skR.Length);
                Array.Copy(skK, _cachedSkillLevelKnown, skK.Length);
                _cachedPlayerQuad.Clear();
                foreach (var kv in quad) _cachedPlayerQuad[kv.Key] = kv.Value;
                _cachedTotalXp = xp; _cachedLuminance = lum; _cachedDeaths = deaths; _cachedVitae = vitae;
                _cachedEncVal = encVal; _cachedEncCap = encCap; _cachedLandcell = cell;
                _cachedPx = px; _cachedPy = py; _cachedPz = pz;
                _playerStatsCacheOwner = playerId;
            }

            SnapshotPlayerQualities(playerId);
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"PrefetchPlayerStats exception: {ex.Message}");
        }
    }

    // ── The player's own qualities tables (2026-09-30) ─────────────────────
    // Every property the client holds for the player, copied from its CBaseQualities hash
    // tables once a second on AC's main thread (PrefetchPlayerStats): memory reads, no calls
    // except a one-time layout check through InqInt64/InqFloat. PropertyCaches serves it after
    // the PlayerDescription record, so the player answers in full even when the engine
    // started after login (an injected or hot-reloaded engine never saw PlayerDescription).
    // Raw values, as the server sent them (what UtilityBelt shows), not enchanted.
    //   CBaseQualities (Chorizite Weenie.cs; offsets match the InqInt/InqInt64/InqBool/
    //   InqFloat/InqString patterns): +0x08 int, +0x0C int64, +0x10 bool, +0x14 float,
    //   +0x18 string, +0x1C data id, +0x20 instance id tables.
    //   PackableHashTable: +0x08 buckets, +0x0C bucket count. PackableHashData<K,V>:
    //   { K key; V data; next; hashVal }, so 4-byte data at +4 / next +8, 8-byte data
    //   (int64, double) at +8 / next +16 (MSVC aligns them to 8), a string's buffer at +4.
    private sealed record OwnedBag(uint Owner, PropertyBag Bag);
    private static volatile OwnedBag? _playerQualities;
    private static int _qualitiesInt64Layout, _qualitiesFloatLayout;   // 0 unchecked, 1 verified, -1 refused

    /// <summary>The player's qualities snapshot (~1 s old), or null. Any thread; never touches AC.</summary>
    internal static PropertyBag? PlayerQualitiesSnapshot(uint playerId)
    {
        OwnedBag? b = _playerQualities;
        return b != null && playerId != 0 && b.Owner == playerId ? b.Bag : null;
    }

    /// <summary>Logout: the snapshot belongs to the character that left.</summary>
    internal static void ClearPlayerQualitiesSnapshot() => _playerQualities = null;

    // MAIN THREAD ONLY (PrefetchPlayerStats).
    private static unsafe void SnapshotPlayerQualities(uint playerId)
    {
        if (!TryGetPlayerQualitiesPtr(out IntPtr qualities) || qualities == IntPtr.Zero)
            return;
        IntPtr bq = qualities + CBaseQualitiesOffset;
        PropertyBag? bag = WalkQualitiesBag(qualities, _qualitiesInt64Layout >= 0, _qualitiesFloatLayout >= 0);
        if (bag == null)
            return;

        VerifyQualitiesLayout(bq, bag);
        if (_qualitiesInt64Layout < 0) bag.Int64s.Clear();
        if (_qualitiesFloatLayout < 0) bag.Floats.Clear();
        _playerQualities = new OwnedBag(playerId, bag);
    }

    // Every table of one CACQualities' CBaseQualities, copied (MAIN THREAD ONLY; every node is
    // page-checked before it is read). The int64 and float tables (8-byte values) only when asked:
    // their node layout is verified once against AC's own readers (VerifyQualitiesLayout).
    // Null when the CBaseQualities header isn't readable.
    private static PropertyBag? WalkQualitiesBag(IntPtr qualities, bool int64s, bool floats)
    {
        IntPtr bq = qualities + CBaseQualitiesOffset;
        if (!IsReadableSpan(bq, 0x24))
            return null;

        var bag = new PropertyBag();
        WalkQualitiesTable(Marshal.ReadIntPtr(bq + 0x08), 8, (k, n) => bag.Ints[k] = Marshal.ReadInt32(n + 4));
        WalkQualitiesTable(Marshal.ReadIntPtr(bq + 0x10), 8, (k, n) => bag.Bools[k] = Marshal.ReadInt32(n + 4) != 0);
        WalkQualitiesTable(Marshal.ReadIntPtr(bq + 0x1C), 8, (k, n) => bag.DataIds[k] = unchecked((uint)Marshal.ReadInt32(n + 4)));
        WalkQualitiesTable(Marshal.ReadIntPtr(bq + 0x20), 8, (k, n) => bag.InstanceIds[k] = unchecked((uint)Marshal.ReadInt32(n + 4)));
        WalkQualitiesTable(Marshal.ReadIntPtr(bq + 0x18), 8, (k, n) =>
        {
            IntPtr buf = Marshal.ReadIntPtr(n + 4);   // PStringBase -> PSRefBuffer: +8 length (with NUL), +20 chars
            if (buf == IntPtr.Zero || !IsReadableSpan(buf, 20)) return;
            int len = Marshal.ReadInt32(buf + 8);
            if (len > 1 && len < 4096 && IsReadableSpan(buf + 20, len - 1))
                bag.Strings[k] = Marshal.PtrToStringAnsi(buf + 20, len - 1) ?? string.Empty;
        });
        if (int64s)
            WalkQualitiesTable(Marshal.ReadIntPtr(bq + 0x0C), 16, (k, n) => bag.Int64s[k] = Marshal.ReadInt64(n + 8));
        if (floats)
            WalkQualitiesTable(Marshal.ReadIntPtr(bq + 0x14), 16, (k, n) =>
            {
                double v = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(n + 8));
                if (double.IsFinite(v)) bag.Floats[k] = v;
            });
        return bag;
    }

    // ── v84: every property of one object (GetObjectProperties, ObjectPropertyDump) ──
    // The client's qualities tables of any object, read on AC's main thread: live when the call
    // is made there, else the plugin's request is served at the next main-thread tick
    // (ServeObjectQualitiesRequest, next to PrefetchPlayerStats) and the copy answers off-thread
    // for a few seconds. One object at a time: it is a diagnostic (/ub propertydump).
    private sealed record ObjectQualities(uint Owner, PropertyBag? Bag, long AtMs);
    private static volatile ObjectQualities? _objectQualities;
    private static uint _objectQualitiesRequest;
    private const long ObjectQualitiesKeepMs = 5000, ObjectQualitiesRefreshMs = 500;

    /// <summary>
    /// v84: the client's qualities tables of <paramref name="objectId"/> (null: it has none, or
    /// not read yet). Off AC's main thread a walk is requested and <paramref name="pending"/> is
    /// set until the first copy for this object exists. Never touches AC memory off the main thread.
    /// The bag returned is never changed afterwards (callers only read it).
    /// </summary>
    internal static PropertyBag? GetObjectQualitiesForDump(uint objectId, out bool pending, out bool read)
    {
        pending = false;
        read = false;
        if (objectId == 0)
            return null;
        if (MainThreadGuard.IsOnMainThread())
        {
            PropertyBag? live = TryWalkObjectQualitiesLive(objectId);
            read = live != null;
            return live;
        }
        long now = Environment.TickCount64;
        ObjectQualities? q = _objectQualities;
        if (q != null && q.Owner == objectId && now - q.AtMs <= ObjectQualitiesKeepMs)
        {
            if (now - q.AtMs > ObjectQualitiesRefreshMs)
                System.Threading.Volatile.Write(ref _objectQualitiesRequest, objectId);
            read = q.Bag != null;
            return q.Bag;
        }
        System.Threading.Volatile.Write(ref _objectQualitiesRequest, objectId);
        pending = true;
        return null;
    }

    /// <summary>v84: serves a plugin's qualities request (MAIN THREAD; returns at once when there is none).</summary>
    public static void ServeObjectQualitiesRequest()
    {
        if (System.Threading.Volatile.Read(ref _objectQualitiesRequest) == 0 || !MainThreadGuard.IsOnMainThread())
            return;
        uint id = System.Threading.Interlocked.Exchange(ref _objectQualitiesRequest, 0);
        if (id == 0)
            return;
        PropertyBag? bag = null;
        try { bag = TryWalkObjectQualitiesLive(id); }
        catch (Exception ex) { RynthLog.Compat($"Compat: object qualities walk 0x{id:X8} failed - {ex.GetType().Name}: {ex.Message}"); }
        _objectQualities = new ObjectQualities(id, bag, Environment.TickCount64);
    }

    /// <summary>Logout: the copy belongs to the session that ended.</summary>
    internal static void ClearObjectQualities()
    {
        _objectQualities = null;
        System.Threading.Volatile.Write(ref _objectQualitiesRequest, 0);
    }

    // MAIN THREAD ONLY: a freshly resolved weenie (never a cached pointer), its qualities pointer,
    // then the page-checked table walk. Int64 / float tables only once their node layout was
    // verified on the player's own tables.
    private static PropertyBag? TryWalkObjectQualitiesLive(uint objectId)
    {
        if (!MainThreadGuard.IsOnMainThread())
            return null;
        if (_getWeenieObject == null)
        {
            if (!Probe() || _getWeenieObject == null)
                return null;
        }
        try
        {
            IntPtr weeniePtr;
            if (SehTrampoline.IsAvailable)
            {
                weeniePtr = SehTrampoline.CdeclPtrUint(_getWeenieObjectPtr, objectId, out bool av);
                if (av)
                    return null;
            }
            else
            {
                weeniePtr = _getWeenieObject(objectId);
            }
            if (weeniePtr == IntPtr.Zero || !TryGetQualitiesPtr(weeniePtr, out IntPtr qualities) || qualities == IntPtr.Zero)
                return null;
            // Only a real CACQualities (the vtable learned from the player's): no AC calls are made
            // here, but a sibling object in the qualities slot would read as garbage properties.
            if (!IsCacQualitiesObject(qualities))
                return null;
            return WalkQualitiesBag(qualities, _qualitiesInt64Layout == 1, _qualitiesFloatLayout == 1);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>v84: the CreateObject copy's fields of <paramref name="objectId"/> as properties (the ~0.1 s
    /// old PublicWeenieDesc snapshot). False when the last walk didn't see it. Any thread; never touches AC.</summary>
    internal static bool CopyCreateDataProperties(uint objectId, PropertyBag dst)
    {
        if (!TryGetPwdSnapshot(objectId, out PwdEntry e))
            return false;
        PwdLayout.FillBag(e.Bytes, dst);
        return true;
    }

    // The 8-byte node layout is checked once against AC's own readers: a walked value must
    // equal InqInt64 / InqFloat for the same key (raw or enchanted). A mismatch on every key
    // refuses that table for the session instead of serving garbage.
    private static unsafe void VerifyQualitiesLayout(IntPtr bq, PropertyBag bag)
    {
        if (_qualitiesInt64Layout == 0 && _inqInt64 != null && bag.Int64s.Count > 0)
        {
            bool match = false;
            foreach (var kv in bag.Int64s)
            {
                long v = 0;
                if (_inqInt64(bq, kv.Key, &v) != 0 && v == kv.Value) { match = true; break; }
            }
            _qualitiesInt64Layout = match ? 1 : -1;
            RynthLog.Compat($"Compat: player qualities int64 layout {(match ? "verified" : "REFUSED (no key matched InqInt64)")} ({bag.Int64s.Count} keys)");
        }
        if (_qualitiesFloatLayout == 0 && _inqFloat != null && bag.Floats.Count > 0)
        {
            bool match = false;
            foreach (var kv in bag.Floats)
            {
                double raw = 0, enchanted = 0;
                bool okRaw = _inqFloat(bq, kv.Key, &raw, 1) != 0;
                bool okEnch = _inqFloat(bq, kv.Key, &enchanted, 0) != 0;
                if ((okRaw && raw == kv.Value) || (okEnch && enchanted == kv.Value)) { match = true; break; }
            }
            _qualitiesFloatLayout = match ? 1 : -1;
            RynthLog.Compat($"Compat: player qualities float layout {(match ? "verified" : "REFUSED (no key matched InqFloat)")} ({bag.Floats.Count} keys)");
        }
    }

    // Walks one PackableHashTable (MAIN THREAD ONLY), bounded like AppraisalHooks' walkers.
    private static void WalkQualitiesTable(IntPtr table, int nextOffset, Action<uint, IntPtr> onNode)
    {
        if (table == IntPtr.Zero || !IsReadableSpan(table, 0x10))
            return;
        IntPtr buckets = Marshal.ReadIntPtr(table + 0x08);
        int bucketCount = Marshal.ReadInt32(table + 0x0C);
        if (buckets == IntPtr.Zero || bucketCount <= 0 || bucketCount > 65536 || !IsReadableSpan(buckets, bucketCount * 4))
            return;
        int total = 0;
        for (int i = 0; i < bucketCount; i++)
        {
            IntPtr node = Marshal.ReadIntPtr(buckets + i * 4);
            int chain = 0;
            while (node != IntPtr.Zero && chain++ < 4096 && total++ < 65536)
            {
                if (!IsReadableSpan(node, nextOffset + 4)) break;
                onNode(unchecked((uint)Marshal.ReadInt32(node)), node);
                node = Marshal.ReadIntPtr(node + nextOffset);
            }
        }
    }

    /// <summary>
    /// Off-thread-safe: serve the cached player-stats snapshot (XP / luminance / deaths /
    /// vitae multiplier, 1.0 = no penalty). Returns false until the cache is populated.
    /// </summary>
    public static bool TryGetPlayerStats(out long totalXp, out long luminance, out int deaths, out float vitae,
                                         out int encumbranceVal, out int encumbranceCap, out uint landcell,
                                         out float px, out float py, out float pz)
    {
        lock (_playerStatsCacheLock)
        {
            totalXp = _cachedTotalXp; luminance = _cachedLuminance; deaths = _cachedDeaths; vitae = _cachedVitae;
            encumbranceVal = _cachedEncVal; encumbranceCap = _cachedEncCap; landcell = _cachedLandcell;
            px = _cachedPx; py = _cachedPy; pz = _cachedPz;
            return _playerStatsCacheOwner != 0;
        }
    }

    // CACQualities::_attribCache (the Chorizite layout: SerializeUsingPackDBObj 0x38 +
    // CBaseQualities 0x28), just before _skillStatsTable. AttributeCache: vtable, then six
    // Attribute* (strength .. self) and three SecondaryAttribute* (health, stamina, mana).
    // Attribute: vtable, _level_from_cp +4, _init_level +8, _cp_spent +12 (16 bytes);
    // SecondaryAttribute adds _current_level +16. InqAttribute2nd (0x00592D20) reads the
    // same [this+0x60] cache.
    private const int AttributeCacheOffset = 0x60;
    private const int QualitiesEnchantmentRegistryOffset = 0x70;

    /// <summary>
    /// The Skills panel's reads (PlayerProgressHooks.Prefetch): the player's skill table
    /// rows, the attribute cache (ranks, innate level, XP spent), buffed and base skills,
    /// attributes and vital maximums, level, skill credits, XP, luminance and vitae, into
    /// <paramref name="dst"/>. MAIN THREAD ONLY; every pointer is checked before it is read.
    /// False (and <paramref name="dst"/> cleared) when the player or its qualities aren't there.
    /// </summary>
    internal static unsafe bool ReadPlayerProgressLive(PlayerProgress dst)
    {
        if (!MainThreadGuard.IsOnMainThread())
            return false;
        dst.Clear();
        uint playerId = ClientHelperHooks.GetPlayerId();
        if (playerId == 0)
            return false;
        if (_getWeenieObject == null && (!Probe() || _getWeenieObject == null))
            return false;

        try
        {
            if (!TryGetObjectQualitiesPtr(playerId, out IntPtr q))
                return false;
            // InqSkill / InqAttribute / InqAttribute2nd with raw 0 read the enchantment
            // registry at [q+0x70] without a null check (early login): wait for it.
            if (!IsReadableSpan(q, QualitiesEnchantmentRegistryOffset + 4)
                || Marshal.ReadIntPtr(q + QualitiesEnchantmentRegistryOffset) == IntPtr.Zero)
                return false;
            dst.PlayerId = playerId;

            dst.SkillTableRead = ReadSkillRowsLive(q, dst);
            dst.AttributeCacheRead = ReadAttributeCacheLive(q, dst);

            if (_inqSkillLevel != null)
            {
                for (uint sk = 1; sk <= PlayerProgress.MaxSkill; sk++)
                {
                    int buffed = 0, raw = 0;
                    bool b = _inqSkillLevel(q, sk, &buffed, 0) != 0;
                    bool r = _inqSkillLevel(q, sk, &raw, 1) != 0;
                    dst.SkillLevelKnown[sk] = b || r;
                    dst.SkillBuffed[sk] = b ? buffed : raw;
                    dst.SkillBase[sk] = r ? raw : buffed;
                }
            }
            if (_inqAttribute != null)
            {
                for (uint a = 1; a <= 6; a++)
                {
                    uint v = 0;
                    if (_inqAttribute(q, a, &v, 0) != 0) dst.AttrBuffed[a] = v;
                    v = 0;
                    if (_inqAttribute(q, a, &v, 1) != 0) dst.AttrBase[a] = v;
                }
            }
            if (_inqAttribute2ndBaseLevel != null)
            {
                // STypeAttribute2nd maximums: 1 health, 3 stamina, 5 mana. raw 0 applies
                // EnchantAttribute2nd (decompile of 0x00592D20); raw 1 is the base maximum.
                for (uint i = 1; i <= 3; i++)
                {
                    uint stype = i * 2 - 1, v = 0;
                    if (_inqAttribute2ndBaseLevel(q, stype, &v, 0) != 0) dst.VitalBuffed[i] = v;
                    v = 0;
                    if (_inqAttribute2ndBaseLevel(q, stype, &v, 1) != 0) dst.VitalBase[i] = v;
                }
            }

            if (TryGetObjectIntProperty(playerId, 25u, out int level)) dst.Level = level;              // PropertyInt.Level
            if (TryGetObjectIntProperty(playerId, 24u, out int credits)) dst.SkillCredits = credits;   // AvailableSkillCredits
            if (TryGetObjectQuadProperty(playerId, 1u, out long total)) dst.TotalXp = total;           // TotalExperience
            if (TryGetObjectQuadProperty(playerId, 2u, out long avail)) dst.UnassignedXp = avail;      // AvailableExperience
            if (TryGetObjectQuadProperty(playerId, 6u, out long lum)) dst.Luminance = lum;             // AvailableLuminance
            if (TryGetObjectQuadProperty(playerId, 7u, out long maxLum)) dst.MaxLuminance = maxLum;    // MaximumLuminance
            if (TryGetVitae(playerId, out float vitae)) dst.Vitae = vitae;
            return true;
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"ReadPlayerProgressLive exception: {ex.Message}");
            dst.Clear();
            return false;
        }
    }

    /// <summary>The skill table at [q+0x64] into dst's skill rows (class, XP spent, ranks, innate). Main thread.</summary>
    private static bool ReadSkillRowsLive(IntPtr q, PlayerProgress dst)
    {
        IntPtr tableFieldPtr = q + SkillStatsTableOffset;
        if (!IsReadablePointer(tableFieldPtr))
            return false;
        IntPtr skillTablePtr = Marshal.ReadIntPtr(tableFieldPtr);
        // Marshal's layout, as PrefetchPlayerSkills reads it (the proven path).
        if (skillTablePtr == IntPtr.Zero || !IsReadableSpan(skillTablePtr, Marshal.SizeOf<PackableHashTableUInt32SkillNative>()))
            return false;
        PackableHashTableUInt32SkillNative table = Marshal.PtrToStructure<PackableHashTableUInt32SkillNative>(skillTablePtr);
        if (table.TableSize == 0 || table.TableSize > 4096 || table.Buckets == IntPtr.Zero
            || !IsReadableSpan(table.Buckets, (int)table.TableSize * IntPtr.Size))
            return false;

        int nodeSize = Marshal.SizeOf<PackableHashDataUInt32SkillNative>();
        int total = 0;
        for (uint b = 0; b < table.TableSize; b++)
        {
            IntPtr nodePtr = Marshal.ReadIntPtr(table.Buckets + unchecked((int)(b * (uint)IntPtr.Size)));
            int guard = 0;
            while (nodePtr != IntPtr.Zero && guard++ < 512 && total++ < 4096)
            {
                if (!IsReadableSpan(nodePtr, nodeSize))
                    break;
                PackableHashDataUInt32SkillNative node = Marshal.PtrToStructure<PackableHashDataUInt32SkillNative>(nodePtr);
                if (node.Key >= 1 && node.Key <= PlayerProgress.MaxSkill)
                {
                    dst.SkillInTable[node.Key] = true;
                    dst.SkillClass[node.Key] = node.Data.AdvancementClass;
                    dst.SkillXpSpent[node.Key] = node.Data.PracticePoints;
                    dst.SkillRanks[node.Key] = node.Data.LevelFromPracticePoints;
                    dst.SkillInit[node.Key] = node.Data.InitialLevel;
                }
                nodePtr = node.Next;
            }
        }
        return true;
    }

    /// <summary>The attribute cache at [q+0x60] into dst's attribute and vital rows. Main thread.</summary>
    private static bool ReadAttributeCacheLive(IntPtr q, PlayerProgress dst)
    {
        IntPtr cacheFieldPtr = q + AttributeCacheOffset;
        if (!IsReadablePointer(cacheFieldPtr))
            return false;
        IntPtr cache = Marshal.ReadIntPtr(cacheFieldPtr);
        if (cache == IntPtr.Zero || !IsReadableSpan(cache, 40))
            return false;
        bool any = false;
        for (int i = 0; i < 9; i++)
        {
            IntPtr a = Marshal.ReadIntPtr(cache + 4 + i * 4);
            int size = i < 6 ? 16 : 20;
            if (a == IntPtr.Zero || !IsReadableSpan(a, size) || !SmartBoxLocator.IsPointerInModule(Marshal.ReadIntPtr(a)))
                continue;
            uint ranks = unchecked((uint)Marshal.ReadInt32(a + 4));
            uint init = unchecked((uint)Marshal.ReadInt32(a + 8));
            uint spent = unchecked((uint)Marshal.ReadInt32(a + 12));
            if (i < 6)
            {
                dst.AttrRanks[i + 1] = ranks;
                dst.AttrInit[i + 1] = init;
                dst.AttrXpSpent[i + 1] = spent;
            }
            else
            {
                dst.VitalRanks[i - 5] = ranks;
                dst.VitalInit[i - 5] = init;
                dst.VitalXpSpent[i - 5] = spent;
            }
            any = true;
        }
        return any;
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
        // Off-thread: _inqSkillLevel is an AC native call (Class A guard). The player's own
        // skill levels come from the main-thread snapshot (PrefetchPlayerStats); others refuse.
        if (!MainThreadGuard.IsOnMainThread())
        {
            if (skillStype < 1 || skillStype > MaxSkillStype || objectId == 0 || objectId != ClientHelperHooks.GetPlayerId())
                return false;
            lock (_playerStatsCacheLock)
            {
                if (_playerStatsCacheOwner != objectId || !_cachedSkillLevelKnown[skillStype]) return false;
                level = raw != 0 ? _cachedSkillLevelRaw[skillStype] : _cachedSkillLevelBuffed[skillStype];
                return true;
            }
        }
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
        // Off-thread: _inqAttribute is an AC native call (Class A guard). The player's own
        // attributes come from the main-thread snapshot (PrefetchPlayerStats); others refuse.
        if (!MainThreadGuard.IsOnMainThread())
        {
            if (stype < 1 || stype > 6 || objectId == 0 || objectId != ClientHelperHooks.GetPlayerId())
                return false;
            lock (_playerStatsCacheLock)
            {
                if (_playerStatsCacheOwner != objectId) return false;
                value = raw != 0 ? _cachedAttrRaw[stype] : _cachedAttrBuffed[stype];
                return value != 0;
            }
        }
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
        // Main thread only. The hot-reload / Decal-coexistence init threads call this
        // through PlayerVitalsHooks.TryReseedFromCurrentPlayer (EntryPoint); off the
        // main thread TryGetQualitiesPtr refused anyway, so they always got false.
        // Returning first skips the pointless lookup; the real reseed happens on the
        // main thread in TryGetObjectQualitiesPtr's lazy path.
        if (!MainThreadGuard.IsOnMainThread())
            return false;
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
        // A raw weenie pointer is only meaningful on AC's main thread (off-thread the
        // resolver returns Zero); refusing here also keeps the lazy Probe on-thread.
        if (!MainThreadGuard.IsOnMainThread())
            return false;
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
        // Off AC's main thread (plugin pump: RynthAi WorldObjectCache / loot / wield,
        // RynthLua, GetContainerContents): the PWD snapshot from the 10 Hz main-thread
        // walk. A miss (not walked yet, or gone) is false, as a cache miss in the
        // weenie-pointer map was before. Never reads AC memory off-thread.
        if (!MainThreadGuard.IsOnMainThread())
        {
            if (!TryGetPwdSnapshot(objectId, out PwdEntry e))
                return false;
            containerID = e.Container;
            wielderID = e.Wielder;
            location = e.Location;
            return true;
        }
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
    /// Reads one 4-byte field of an object's PublicWeenieDesc by its offset from the
    /// start of the PWD (e.g. +56 _type, +60 _value, +96 _stackSize, +116 _burden; layout
    /// in Chorizite Weenie.cs:1734, same base as <see cref="TryGetObjectOwnershipInfo"/>).
    /// MAIN THREAD ONLY: off the main thread it returns false without touching AC memory
    /// (a snapshot-cached weenie pointer can be freed and reused under us). Both ends of
    /// the read are page-probed, so a bad pointer fails closed instead of faulting.
    /// Used by vendor trading to read item value/burden for pack items, whose qualities
    /// pointer is null (so InqInt can't serve them).
    /// </summary>
    internal static bool TryReadPwdInt32(uint objectId, int pwdFieldOffset, out int value)
    {
        value = 0;
        if (pwdFieldOffset < 0 || pwdFieldOffset > 172 || (pwdFieldOffset & 3) != 0)
            return false;
        // Off-thread: served from the main-thread PWD byte snapshot, never from AC memory.
        if (!MainThreadGuard.IsOnMainThread())
            return TryGetPwdSnapshotRawInt32(objectId, pwdFieldOffset, out value);
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
            IntPtr fieldAddr = weeniePtr + _weeniePhysicsObjOffset + 4 + pwdFieldOffset;
            if (!IsReadablePointer(fieldAddr) || !IsReadablePointer(fieldAddr + 3))
                return false;
            value = Marshal.ReadInt32(fieldAddr);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>An item's PublicWeenieDesc fields, as the Inventory panel shows them.</summary>
    internal struct ItemPwdFields
    {
        public uint Wcid, Icon, Container, Wielder, ValidLocations, Location, Type, Value, Useability,
            StackSize, MaxStackSize, Bitfield, SpellId;
        public int ItemsCapacity, Burden, MaterialType;
        public float Workmanship;
    }

    // PublicWeenieDesc offsets (Chorizite Weenie.cs:1734; the same base as TryReadPwdInt32).
    private const int PwdItemsCapacityOffset = 48, PwdTypeOffset = 56, PwdValueOffset = 60, PwdUseabilityOffset = 64,
        PwdValidLocationsOffset = 40, PwdStackSizeOffset = 96, PwdMaxStackSizeOffset = 100, PwdBurdenOffset = 116,
        PwdSpellIdOffset = 120, PwdMaterialTypeOffset = 148, PwdWorkmanshipOffset = 152;

    /// <summary>
    /// Reads every field the Inventory panel needs from an object's PublicWeenieDesc in one
    /// go: the weenie is resolved once and PWD+12..+155 (wcid through workmanship) is
    /// page-probed as one span, then plain reads. MAIN THREAD ONLY: off it this returns false
    /// without touching AC memory. An object that is gone (or not a weenie) is false, never
    /// a fault, so a caller holding an old id just finds nothing.
    /// </summary>
    internal static bool TryReadItemFields(uint objectId, out ItemPwdFields f)
    {
        f = default;
        if (objectId == 0 || !MainThreadGuard.IsOnMainThread())
            return false;
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
            IntPtr pwd = weeniePtr + _weeniePhysicsObjOffset + 4;
            if (!IsReadableSpan(pwd + PwdWcidOffset, PwdWorkmanshipOffset + 4 - PwdWcidOffset))
                return false;
            f.Wcid = (uint)Marshal.ReadInt32(pwd + PwdWcidOffset);
            f.Icon = (uint)Marshal.ReadInt32(pwd + PwdIconOffset);
            f.Container = (uint)Marshal.ReadInt32(pwd + PwdContainerOffset);
            f.Wielder = (uint)Marshal.ReadInt32(pwd + PwdWielderOffset);
            f.ValidLocations = (uint)Marshal.ReadInt32(pwd + PwdValidLocationsOffset);
            f.Location = (uint)Marshal.ReadInt32(pwd + PwdLocationOffset);
            f.ItemsCapacity = Marshal.ReadInt32(pwd + PwdItemsCapacityOffset);
            f.Type = (uint)Marshal.ReadInt32(pwd + PwdTypeOffset);
            f.Value = (uint)Marshal.ReadInt32(pwd + PwdValueOffset);
            f.Useability = (uint)Marshal.ReadInt32(pwd + PwdUseabilityOffset);
            f.StackSize = (uint)Marshal.ReadInt32(pwd + PwdStackSizeOffset);
            f.MaxStackSize = (uint)Marshal.ReadInt32(pwd + PwdMaxStackSizeOffset);
            f.Bitfield = (uint)Marshal.ReadInt32(pwd + PwdBitfieldOffset);
            f.Burden = Marshal.ReadInt32(pwd + PwdBurdenOffset);
            f.SpellId = (uint)Marshal.ReadInt32(pwd + PwdSpellIdOffset);
            f.MaterialType = Marshal.ReadInt32(pwd + PwdMaterialTypeOffset);
            f.Workmanship = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(pwd + PwdWorkmanshipOffset));
            if (float.IsNaN(f.Workmanship) || f.Workmanship < 0 || f.Workmanship > 100) f.Workmanship = 0;
            return true;
        }
        catch
        {
            f = default;
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
        // Off-thread (RynthAi ExpressionEngine / WorldObjectCache, RynthLua): the PWD
        // snapshot (<= ~100 ms old; a wcid never changes for an object).
        if (!MainThreadGuard.IsOnMainThread())
        {
            if (!TryGetPwdSnapshot(objectId, out PwdEntry e))
                return false;
            wcid = e.Wcid;
            return true;
        }
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
        // Off-thread (RynthAi DoorInteractionController's BF_DOOR check, player
        // filters): the PWD snapshot (<= ~100 ms old). MonsterHud (main thread)
        // keeps the live read below.
        if (!MainThreadGuard.IsOnMainThread())
        {
            if (!TryGetPwdSnapshot(objectId, out PwdEntry e))
                return false;
            bitfield = e.Bitfield;
            return true;
        }
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
                // A zero snapshot value means qualities weren't populated when the walk
                // ran; prefer the create-time descriptor type in that case.
                if (_objectTypeCache.TryGetValue(objectId, out typeFlags) && typeFlags != 0)
                    return true;
            }
            if (TryGetCreateSeedType(objectId, out uint seeded))
            {
                typeFlags = seeded;
                return true;
            }
            // Snapshot hit with flags=0 is still an answer (static scenery often has no type).
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
        bool inqOk = false;
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
                // Qualities of a never-appraised world object (a portal) can hold type 0;
                // the type the server sent in CreateObject is in the PWD below. Portals
                // were missing from the radar and /ub usel until clicked (2026-09-29).
                if (typeFlags != 0)
                    return true;
                inqOk = true;
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
                    if (typeFlags != 0)
                        return true;
                    inqOk = true;
                }
            }
        }
        catch
        {
            return false;
        }

        // Last resort: appraisal cache from IdentifyObject responses.
        if (AppraisalHooks.TryGetCachedIntProperty(objectId, 1 /* STypeInt.ItemType */, out int cachedType) && cachedType != 0)
        {
            typeFlags = unchecked((uint)cachedType);
            return true;
        }
        typeFlags = 0;
        return inqOk;   // read fine, the type just isn't known yet
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

        // STACK_SIZE: the client's own copy is the truth (it follows every split,
        // merge and use); the caches below only know stacks the server sent a
        // property update or appraisal for. Live on the main thread, snapshot off it.
        if (stype == 12)
        {
            if (TryReadPwdInt32(objectId, 96, out value) && value > 0) return true;
            lock (_objectIdentityCacheLock)
            {
                if (_objectStackCache.TryGetValue(objectId, out value)) return true;
            }
            value = 0;
        }


        // The player's current burden off the main thread: the PrefetchPlayerStats snapshot.
        if (stype == 5 && !MainThreadGuard.IsOnMainThread() && objectId != 0 && objectId == ClientHelperHooks.GetPlayerId())
        {
            lock (_playerStatsCacheLock)
            {
                if (_playerStatsCacheOwner == objectId) { value = _cachedEncVal; return true; }
            }
        }

        // The property caches (PropertyCaches: the player's PlayerDescription + private
        // updates, else the last identify, then public updates). They cover objects whose
        // m_pQualities is null (doors, inventory items), and server updates for
        // never-appraised objects (an essence's uses after a use or refill) are newer than
        // the PublicWeenieDesc copy read below.
        if (PropertyCaches.TryGetInt(objectId, stype, out value))
            return true;

        if (_getWeenieObject == null)
        {
            if (!Probe() || _getWeenieObject == null)
                return false;
        }

        // Fast path: stypes whose values live in PublicWeenieDesc.
        // PWD starts at weenie + _phys_obj_offset + 4. Layout (Chorizite Weenie.cs:1734):
        //   +28 _containerID, +32 _wielderID, +40 _valid_locations, +44 _location,
        //   +48 _itemsCapacity, +52 _containersCapacity, +76 _effects, +88 _structure, +92 _maxStructure,
        //   +96 _stackSize, +100 _maxStackSize,
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
            18  => 76,   // UI_EFFECTS → _effects (a pet essence's element)
            91  => 92,   // MAX_STRUCTURE → _maxStructure
            92  => 88,   // STRUCTURE → _structure (uses left on kits, essences)
            131 => 148,  // MATERIAL_TYPE → _material_type (4 bytes, int)
            _   => -1,
        };

        // Deep-audit finding #24 (2026-06-18): this PWD fast path used to run
        // BEFORE the IsOnMainThread check below, dereferencing a weenie
        // pointer captured on a <=100ms-stale main-thread walk with raw
        // Marshal.ReadInt32. IsReadablePointer catches an unmapped page but
        // not a freed-then-reallocated one (TOCTOU) — off-thread that risks
        // feeding a garbage property value into loot/salvage logic. Hoisted
        // the gate up here (same place the InqInt fallback already gates) so
        // the fast path only ever runs on the main thread; off-thread
        // callers fall through to the appraisal cache only (already checked
        // above) rather than touching AC memory directly.
        if (!MainThreadGuard.IsOnMainThread())
        {
            // Off the main thread the PWD copy of CURRENT_WIELDED_LOCATION comes from the
            // identity snapshot. Only after the caches above: the PWD _location can stay 0
            // for an item wielded after it was created (a Longbow in hand read 0).
            if (stype == 10)
            {
                lock (_objectIdentityCacheLock)
                {
                    if (_objectLocationCache.TryGetValue(objectId, out value))
                        return true;
                }
                value = 0;
            }
            // Every other CreateObject field (value, burden, capacity, useability, ...) from the
            // PWD snapshot, the way an unidentified object's properties reach UtilityBelt.
            return TryGetPwdSnapshotInt(objectId, stype, out value);
        }

        // CBaseQualities::InqInt for stypes not in PWD.
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

            if (TryGetQualitiesPtr(weeniePtr, out IntPtr qualitiesPtr))
            {
                IntPtr baseQualitiesPtr = qualitiesPtr + CBaseQualitiesOffset;
                if (IsReadablePointer(baseQualitiesPtr))
                {
                    int retval = 0;
                    int result = _inqInt(baseQualitiesPtr, stype, &retval, 0, 1);
                    if (result != 0)
                    {
                        value = retval;
                        return true;
                    }
                }
            }

            // Fallback (no qualities, or InqInt doesn't know it): the rest of the
            // PublicWeenieDesc, as off the main thread (the caches were read above).
            return TryGetPwdSnapshotInt(objectId, stype, out value);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads a STypeDID property that lives in PublicWeenieDesc directly from the
    /// embedded PWD struct, mirroring the <see cref="TryGetObjectIntProperty"/> PWD
    /// fast-path. DataID properties otherwise go through a qualities table that is
    /// null for inventory/pack items (InqDataID would fail), so we bypass it. PWD is
    /// network-populated, so this works on UNequipped items with no qualities pointer
    /// and no appraisal — no native call. Off AC's main thread the value comes from
    /// the PWD field snapshot the 10 Hz main-thread walk takes (since 2026-09-30).
    /// Supported stypes: Icon=8 (PWD._iconID at +16), IconOverlay=50 (_iconOverlayID
    /// at +20) and IconUnderlay=52 (_iconUnderlayID at +24; both since 2026-09-30).
    /// Layout per the _type reader above: WeenieDesc(4) + _name(4) + _plural_name(4) +
    /// _wcid(4) = 16 → _iconID. 0 in the overlay/underlay fields means "none".
    /// </summary>
    public static bool TryGetObjectDataIdProperty(uint objectId, uint stype, out uint dataId)
    {
        if (TryGetPwdIconDataId(objectId, stype, out dataId))
            return true;
        // Every other data id (2026-09-30): the property caches (identify DID table,
        // PlayerDescription, UpdatePropertyDataID), then the rest of the PWD (Spell 28).
        if (PropertyCaches.TryGetDataId(objectId, stype, out dataId))
            return true;
        return TryGetPwdSnapshotDataId(objectId, stype, out dataId);
    }

    /// <summary>
    /// v73: a STypeIID (PropertyInstanceId) property - another object's id. Any thread; never
    /// touches AC memory off the main thread. Container (2) and Wielder (3) come from the
    /// client's own PublicWeenieDesc first (the client moves items itself; the snapshot follows
    /// it), then the property caches (the player's PlayerDescription and private updates:
    /// allegiance, monarch, patron, ...; public UpdatePropertyInstanceID), then the rest of the
    /// PWD (Monarch 26, HouseOwner 32, PetOwner 44). A zero id answers nothing.
    /// </summary>
    public static bool TryGetObjectInstanceIdProperty(uint objectId, uint stype, out uint value)
    {
        value = 0;
        if (objectId == 0 || stype == 0)
            return false;
        if (stype is 2 or 3 && TryGetObjectOwnershipInfo(objectId, out uint container, out uint wielder, out _))
        {
            value = stype == 2 ? container : wielder;
            if (value != 0) return true;
        }
        if (PropertyCaches.TryGetInstanceId(objectId, stype, out value) && value != 0)
            return true;
        return TryGetPwdSnapshotInstanceId(objectId, stype, out value);
    }

    private static bool TryGetPwdIconDataId(uint objectId, uint stype, out uint dataId)
    {
        dataId = 0;

        int pwdFieldOffset = stype switch
        {
            8 => PwdIconOffset,              // PropertyDataId.Icon → _iconID
            50 => PwdIconOverlayOffset,      // PropertyDataId.IconOverlay → _iconOverlayID
            52 => PwdIconUnderlayOffset,     // PropertyDataId.IconUnderlay → _iconUnderlayID
            _ => -1,
        };
        if (pwdFieldOffset < 0)
            return false;

        // Off AC's main thread (RynthAi icons, AutoVendorManager): the PWD snapshot
        // from the 10 Hz main-thread walk. Reading PWD through the cached weenie
        // pointer here was the deep-audit #24 TOCTOU (a freed, reused weenie still
        // passes the page probe). Blank icons are worse than a 100 ms old one, so
        // serve the copy rather than refusing.
        if (!MainThreadGuard.IsOnMainThread())
        {
            if (!TryGetPwdSnapshot(objectId, out PwdEntry e))
                return false;
            dataId = pwdFieldOffset switch
            {
                PwdIconOffset => e.Icon,
                PwdIconOverlayOffset => e.IconOverlay,
                _ => e.IconUnderlay,
            };
            return true;
        }

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
            IntPtr fieldAddr = weeniePtr + pwdBase + pwdFieldOffset;
            // Page-probe BOTH ends of the 4-byte read span (mirrors the int PWD
            // fast-path): a freed-but-decommitted weenie whose field straddles a
            // committed/decommitted page boundary is an uncatchable AV under
            // NativeAOT; the catch below only covers null-page faults.
            if (!IsReadablePointer(fieldAddr) || !IsReadablePointer(fieldAddr + 3))
                return false;

            dataId = (uint)Marshal.ReadInt32(fieldAddr);
            return true;
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

        // Deep-audit finding #24 (2026-06-18): same TOCTOU class as
        // TryGetObjectIntProperty above — this PWD fast path used to run
        // before the IsOnMainThread check, raw-dereferencing a
        // possibly-stale weenie pointer off-thread. Hoisted the gate up
        // before the fast path; no appraisal-cache fallback exists for this
        // stype today, so off-thread callers now just get false instead of
        // an unguarded read. (2026-09-30: also ahead of the lazy Probe, which
        // must not run off-thread.)
        //
        // 2026-09-30: the property caches hold float properties too (the identify message's
        // float table and armour/weapon profiles, PlayerDescription, UpdatePropertyFloat), so
        // objects answer off-thread like their int, bool and string properties do. Before,
        // every off-thread double read failed. UseRadius / CooldownDuration / workmanship of
        // an unidentified object come from the PWD snapshot.
        if (PropertyCaches.TryGetFloat(objectId, stype, out value))
            return true;
        if (!MainThreadGuard.IsOnMainThread())
            return TryGetPwdSnapshotFloat(objectId, stype, out value);

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

        // (IsOnMainThread already checked above, before the PWD fast path.)
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

            if (TryGetQualitiesPtr(weeniePtr, out IntPtr qualitiesPtr))
            {
                IntPtr baseQualitiesPtr = qualitiesPtr + CBaseQualitiesOffset;
                if (IsReadablePointer(baseQualitiesPtr))
                {
                    double retval = 0;
                    int result = _inqFloat(baseQualitiesPtr, stype, &retval, 0);
                    if (result != 0)
                    {
                        value = retval;
                        return true;
                    }
                }
            }
            return TryGetPwdSnapshotFloat(objectId, stype, out value);
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
        // The property caches first (2026-09-30): the player's PlayerDescription + private
        // updates, an identified object's int64 table, public UpdatePropertyInt64.
        if (PropertyCaches.TryGetInt64(objectId, stype, out value))
            return true;
        // Off-thread: _inqInt64 is an AC native call (Class A guard). The player's experience and
        // luminance come from the main-thread snapshot (PrefetchPlayerStats); others refuse.
        if (!MainThreadGuard.IsOnMainThread())
        {
            if (objectId == 0 || objectId != ClientHelperHooks.GetPlayerId())
                return false;
            lock (_playerStatsCacheLock)
                return _playerStatsCacheOwner == objectId && _cachedPlayerQuad.TryGetValue(stype, out value);
        }
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
        // Off-thread: _inqAttribute2ndBaseLevel is an AC native call (Class A guard). The player's
        // own values come from the main-thread snapshot (PrefetchPlayerStats); others refuse.
        if (!MainThreadGuard.IsOnMainThread())
        {
            if (stype < 1 || stype > 6 || objectId == 0 || objectId != ClientHelperHooks.GetPlayerId())
                return false;
            lock (_playerStatsCacheLock)
            {
                if (_playerStatsCacheOwner != objectId) return false;
                value = _cachedAttr2nd[stype];
                return value != 0;
            }
        }
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

        // The property caches cover inventory items where m_pQualities is null
        // (CBaseQualities::InqBool always returns 0 for such objects), the player's own
        // record and public updates (PropertyCaches; pure dictionary reads).
        if (PropertyCaches.TryGetBool(objectId, stype, out value))
            return true;

        // Off-thread: refuse — _inqBool is an AC native call (Class A guard).
        if (!MainThreadGuard.IsOnMainThread())
            return false;
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

        // The property caches cover inventory items where m_pQualities is null
        // (CBaseQualities::InqString always returns 0 for such objects), the player's own
        // record (PlayerDescription) and UpdatePropertyString (PropertyCaches).
        if (PropertyCaches.TryGetString(objectId, stype, out value))
            return true;

        // Off-thread: refuse — _inqString is an AC native call (Class A guard). The name
        // (PropertyString.Name, 1) of any object comes from the identity snapshot.
        if (!MainThreadGuard.IsOnMainThread())
            return stype == 1 && TryGetSnapshotName(objectId, out value) && value.Length > 0;
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
        long walkT0 = System.Diagnostics.Stopwatch.GetTimestamp();
        using var probeScope = MemoryProbe.BeginWalk(); // one page query per page per walk (MemoryProbe)

        try
        {
            // Scratch reused across walks (main thread only): the walk used to allocate a new
            // dictionary and closure every 500 ms on AC's thread, growing with the object count.
            var snapshot = _attackableScratch;
            snapshot.Clear();
            int n = CObjectMaintHooks.EnumerateLiveWeenieObjectIds(_captureAttackableDelegate);
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
        finally
        {
            _walkAttackable.Add(System.Diagnostics.Stopwatch.GetTimestamp() - walkT0);
        }
    }

    // Main-thread scratch for the attackable / identity walks (see PrefetchAttackable).
    private static readonly Dictionary<uint, bool> _attackableScratch = new(1024);
    private static readonly Dictionary<uint, string> _identityNameScratch = new(1024);
    private static readonly Dictionary<uint, uint> _identityTypeScratch = new(1024);
    private static readonly Dictionary<uint, int> _identityLocScratch = new(1024);
    private static readonly Dictionary<uint, int> _identityStackScratch = new(1024);
    private static readonly List<uint> _identityIdScratch = new(1024);
    private static readonly Action<uint> _captureAttackableDelegate = CaptureAttackableForId;
    private static readonly Action<uint> _captureIdentityDelegate = CaptureIdentityForId;

    private static void CaptureAttackableForId(uint id) => _attackableScratch[id] = ComputeAttackableOnMainThread(id);

    private static void CaptureIdentityForId(uint id)
    {
        _identityIdScratch.Add(id);
        if (TryGetObjectName(id, out string nm) && nm.Length > 0)
            _identityNameScratch[id] = nm;
        if (TryGetItemType(id, out uint typeFlags))
            _identityTypeScratch[id] = typeFlags;
        if (TryReadPwdInt32(id, 44, out int loc))
            _identityLocScratch[id] = loc;
        if (TryReadPwdInt32(id, 96, out int stack) && stack > 0)
            _identityStackScratch[id] = stack;
    }

    public static void PrefetchObjectIdentity()
    {
        if (!MainThreadGuard.IsOnMainThread())
            return;
        if ((DateTime.UtcNow - _lastObjectIdentityPrefetchUtc).TotalMilliseconds < ObjectIdentityPrefetchThrottleMs)
            return;
        _lastObjectIdentityPrefetchUtc = DateTime.UtcNow;
        long walkT0 = System.Diagnostics.Stopwatch.GetTimestamp();
        using var probeScope = MemoryProbe.BeginWalk(); // one page query per page per walk (MemoryProbe)

        try
        {
            // Scratch reused across walks (main thread only), as PrefetchAttackable.
            var nameSnap = _identityNameScratch;
            var typeSnap = _identityTypeScratch;
            var locSnap = _identityLocScratch;
            var stackSnap = _identityStackScratch;
            var idSnap = _identityIdScratch;
            nameSnap.Clear(); typeSnap.Clear(); locSnap.Clear(); stackSnap.Clear(); idSnap.Clear();

            int n = CObjectMaintHooks.EnumerateLiveWeenieObjectIds(_captureIdentityDelegate);

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
                _objectLocationCache.Clear();
                foreach (var kv in locSnap)
                    _objectLocationCache[kv.Key] = kv.Value;
                _objectStackCache.Clear();
                foreach (var kv in stackSnap)
                    _objectStackCache[kv.Key] = kv.Value;
            }
            _liveObjectIds = idSnap.ToArray();

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
        finally
        {
            _walkIdentity.Add(System.Diagnostics.Stopwatch.GetTimestamp() - walkT0);
        }
    }

    /// <summary>
    /// Records an object's name and ITEM_TYPE straight from the PublicWeenieDesc passed
    /// to ACCObjectMaint::CreateObject. Called from the create hook on AC's main thread,
    /// after the original returns. The descriptor is server data that exists before the
    /// object's qualities do, so this makes names/types readable off-thread immediately
    /// instead of after the first successful identity snapshot (~10 s at login).
    /// Every read is pointer-validated; a bad descriptor simply records nothing.
    /// </summary>
    internal static void SeedIdentityFromCreate(uint objectId, IntPtr weenieDesc)
    {
        if (objectId == 0 || weenieDesc == IntPtr.Zero)
            return;

        try
        {
            // PublicWeenieDesc derives from PackObj, so a real descriptor starts with an
            // acclient.exe vtable; reject anything else before reading fields.
            if (!LooksLikeAcHeapObject(weenieDesc))
                return;

            bool gotName = TryReadPwdString(weenieDesc, PwdNameOffset, out string name);

            uint typeFlags = 0;
            IntPtr typeAddr = weenieDesc + PwdTypeOffset;
            bool gotType = IsReadablePointer(typeAddr);
            if (gotType)
                typeFlags = unchecked((uint)Marshal.ReadInt32(typeAddr));

            if (!gotName && (!gotType || typeFlags == 0))
                return;

            lock (_createSeedLock)
            {
                // Safety valve for a missed delete: drop everything rather than grow forever.
                // The live snapshot covers anything still in the world.
                if (_createSeedNames.Count >= CreateSeedCap || _createSeedTypes.Count >= CreateSeedCap)
                {
                    _createSeedNames.Clear();
                    _createSeedTypes.Clear();
                }
                if (gotName)
                    _createSeedNames[objectId] = name;
                if (gotType && typeFlags != 0)
                    _createSeedTypes[objectId] = typeFlags;
            }

            int seedNo = System.Threading.Interlocked.Increment(ref _createSeedCount);
            // First few samples let a log reader confirm the descriptor offsets resolve
            // to sensible names/types on this client build.
            if (seedNo <= 5)
                RynthLog.Compat($"[CreateSeed] #{seedNo} 0x{objectId:X8} name='{(gotName ? name : "")}' type=0x{typeFlags:X8}");
            if (!_loggedCreateSeedServe && seedNo >= 50)
            {
                _loggedCreateSeedServe = true;
                int names, types;
                lock (_createSeedLock) { names = _createSeedNames.Count; types = _createSeedTypes.Count; }
                RynthLog.Compat($"ClientObjectHooks: create-descriptor identity seeds active — {names} names, {types} types captured at CreateObject.");
            }
        }
        catch
        {
            // Descriptor read failures are non-fatal; the periodic snapshot still runs.
        }
    }

    /// <summary>Drops create-time seeds for a deleted object (called from the delete hook).</summary>
    internal static void ForgetCreateSeed(uint objectId)
    {
        lock (_createSeedLock)
        {
            _createSeedNames.Remove(objectId);
            _createSeedTypes.Remove(objectId);
        }
    }

    private static bool TryGetCreateSeedName(uint objectId, out string name)
    {
        lock (_createSeedLock)
        {
            if (_createSeedNames.TryGetValue(objectId, out string? seeded) && seeded.Length > 0)
            {
                name = seeded;
                return true;
            }
        }
        name = string.Empty;
        return false;
    }

    private static bool TryGetCreateSeedType(uint objectId, out uint typeFlags)
    {
        lock (_createSeedLock)
            return _createSeedTypes.TryGetValue(objectId, out typeFlags);
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
            // Snapshot miss (login burst / object newer than the last walk): serve the
            // name captured from the create descriptor.
            if (TryGetCreateSeedName(objectId, out name))
                return true;
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

        return ReadPhysicsFromWeenie(objectId, weeniePtr, out objCellId, out x, out y, out z, out _, out _, out _, out _);
    }

    /// <summary>
    /// Reads cell/origin plus the heading quaternion's qw/qz and CPhysicsObj m_state
    /// from a weenie pointer resolved on the main thread. MAIN THREAD ONLY (callers:
    /// <see cref="ReadObjectPositionLive"/> and the <see cref="PrefetchPositions"/> walk).
    /// </summary>
    private static bool ReadPhysicsFromWeenie(
        uint objectId,
        IntPtr weeniePtr,
        out uint objCellId,
        out float x,
        out float y,
        out float z,
        out float qw,
        out float qz,
        out uint state,
        out bool hasState)
    {
        objCellId = 0;
        x = y = z = 0;
        qw = 1f;
        qz = 0;
        state = 0;
        hasState = false;

        if (weeniePtr == IntPtr.Zero || _weeniePhysicsObjOffset < 0)
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
            // One VirtualQuery covers the object head through m_state (+0xA8); if the
            // span happens to straddle two regions, fall back to the old head-only
            // probe and leave m_state unknown for this pass.
            bool spanOk = IsReadableSpan(physicsObj, PhysicsStateOffset + 4);
            if (!spanOk && !IsReadablePointer(physicsObj))
                return false;

            IntPtr vtable = Marshal.ReadIntPtr(physicsObj);
            if (!SmartBoxLocator.IsPointerInModule(vtable))
                return false;

            IntPtr pos = physicsObj + PhysicsPositionOffset;
            objCellId = unchecked((uint)Marshal.ReadInt32(pos + PositionObjCellIdOffset));
            x = ReadFloat(pos + PositionOriginXOffset);
            y = ReadFloat(pos + PositionOriginYOffset);
            z = ReadFloat(pos + PositionOriginZOffset);
            qw = ReadFloat(pos + PositionQwOffset);
            qz = ReadFloat(pos + PositionQzOffset);
            if (spanOk)
            {
                state = unchecked((uint)Marshal.ReadInt32(physicsObj + PhysicsStateOffset));
                hasState = true;
            }
            return true;
        }
        catch (Exception ex)
        {
            LogLookup($"Compat: object position read failed for 0x{objectId:X8} - {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // Heading (0–360°, clockwise, 0 = North) from a yaw quaternion's qw/qz. Same
    // formula as NavigationEngine / CorpseOpenController.
    private static float HeadingFromQuaternion(float qw, float qz)
    {
        double physYawDeg = 2.0 * Math.Atan2(qz, qw) * (180.0 / Math.PI);
        return (float)(((-physYawDeg) % 360.0 + 720.0) % 360.0);
    }

    /// <summary>
    /// Samples every live object's position on AC's main thread (EndScene path)
    /// into <see cref="_positionCache"/>, which the off-thread plugin pump reads
    /// via <see cref="TryGetObjectPosition"/>. Sampled every frame (positions are
    /// volatile) and ZERO-ALLOC: the dict is reused (Clear + re-add of struct
    /// values) and the per-id callback is a cached delegate, so this never
    /// allocates inside the EndScene reverse-P/Invoke (which would risk the
    /// NativeAOT GC fail-fast that moved TickAll off this thread). Two reused
    /// dictionaries swap after the walk (still zero-alloc), so the pump's per-id
    /// read never waits for the walk. Walk runs every frame, so a transient empty (walk failure)
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
        long walkT0 = System.Diagnostics.Stopwatch.GetTimestamp();
        using var probeScope = MemoryProbe.BeginWalk(); // one page query per page per walk (MemoryProbe)
        if (_weeniePhysicsObjOffset < 0)
        {
            ProbePhysObjOffset();
            if (_weeniePhysicsObjOffset < 0)
                return;
        }

        try
        {
            // Back buffers are main-thread only: fill them with no lock held.
            _positionBack.Clear();
            _weeniePtrBack.Clear();
            _pwdBack.Clear();
            CObjectMaintHooks.EnumerateLiveWeenieObjectIds(_capturePositionDelegate);

            lock (_positionCacheLock)
                (_positionCache, _positionBack) = (_positionBack, _positionCache);

            // Publish the freshly-walked id->weeniePtr map and the PWD field copy
            // to off-thread readers together. The swap is brief and is NOT held
            // during the walk, so the pump's reads never stall on the walk.
            lock (_weeniePtrSwapLock)
            {
                (_weeniePtrFront, _weeniePtrBack) = (_weeniePtrBack, _weeniePtrFront);
                (_pwdFront, _pwdBack) = (_pwdBack, _pwdFront);
            }

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
        finally
        {
            _walkPositions.Add(System.Diagnostics.Stopwatch.GetTimestamp() - walkT0);
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
            var (fastPages, slowCalls, slowMs) = MemoryProbe.TakeStats();
            RynthLog.Compat($"[SnapshotDiag] walks (main thread, last 10 s): positions {_walkPositions.TakeText()}; identity {_walkIdentity.TakeText()}; attackable {_walkAttackable.TakeText()}; memory probes {fastPages} pages fast, {slowCalls} VirtualQuery ({slowMs:0.0} ms){(MemoryProbe.FastPathActive ? "" : " FAST PATH OFF")}");

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
    // thread (no lock: it fills the back buffer); reads the live position and stores it.
    // Method group is cached in _capturePositionDelegate so the enumerate call
    // allocates no closure per frame.
    private static void CapturePositionForId(uint id)
    {
        // Resolve natively here (we are on AC's main thread, so the CObjectMaint
        // walk is safe). The pointer map only feeds the snapshot diagnostics now;
        // the pump reads the VALUES copied below (PWD fields, position, state).
        IntPtr weeniePtr = _getWeenieObjectNative != null ? _getWeenieObjectNative(id) : IntPtr.Zero;
        if (weeniePtr == IntPtr.Zero)
            return;
        _weeniePtrBack[id] = weeniePtr;
        // PWD fields for every weenie (pack items included: they have no position).
        if (TryReadPwdFieldsLive(weeniePtr, out PwdEntry pwd))
            _pwdBack[id] = pwd;
        // Reuses the pointer resolved above (the old path resolved it a second time
        // through TryGetWeenieObjectPtr, which on this thread is the same native call).
        if (ReadPhysicsFromWeenie(id, weeniePtr, out uint cell, out float x, out float y, out float z,
                                  out float qw, out float qz, out uint state, out bool hasState))
            _positionBack[id] = new PosEntry { Cell = cell, X = x, Y = y, Z = z, Qw = qw, Qz = qz, State = state, HasState = hasState };
    }

    // One probe over the whole PublicWeenieDesc (PwdLayout.Size bytes), then one copy.
    // MAIN THREAD ONLY (the PrefetchPositions walk, with a freshly resolved pointer).
    private static unsafe bool TryReadPwdFieldsLive(IntPtr weeniePtr, out PwdEntry entry)
    {
        entry = default;
        if (weeniePtr == IntPtr.Zero || _weeniePhysicsObjOffset < 0)
            return false;
        try
        {
            IntPtr pwd = weeniePtr + _weeniePhysicsObjOffset + 4;
            if (!IsReadableSpan(pwd, PwdLayout.Size))
                return false;
            new ReadOnlySpan<byte>((void*)pwd, PwdLayout.Size).CopyTo(entry.Bytes);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Off-thread PWD lookup (brief swap lock, no AC memory).
    private static bool TryGetPwdSnapshot(uint objectId, out PwdEntry entry)
    {
        lock (_weeniePtrSwapLock)
            return _pwdFront.TryGetValue(objectId, out entry);
    }

    // The snapshot's CreateObject fields as properties (PwdLayout: a zero field answers nothing).
    private static bool TryGetPwdSnapshotInt(uint objectId, uint stype, out int value)
    {
        value = 0;
        return PwdLayout.IntOffset(stype) >= 0 && TryGetPwdSnapshot(objectId, out PwdEntry e)
            && PwdLayout.TryGetInt(e.Bytes, stype, out value);
    }

    /// <summary>
    /// Off-thread read of one 4-byte PWD field by raw offset (the <see cref="TryReadPwdInt32"/>
    /// contract) from the main-thread snapshot. False for objects the last walk didn't see.
    /// </summary>
    private static bool TryGetPwdSnapshotRawInt32(uint objectId, int pwdFieldOffset, out int value)
    {
        value = 0;
        if (pwdFieldOffset < 0 || pwdFieldOffset + 4 > PwdLayout.Size)
            return false;
        if (!TryGetPwdSnapshot(objectId, out PwdEntry e))
            return false;
        value = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(((ReadOnlySpan<byte>)e.Bytes).Slice(pwdFieldOffset));
        return true;
    }

    private static bool TryGetPwdSnapshotFloat(uint objectId, uint stype, out double value)
    {
        value = 0;
        return stype is 54 or 167 or 280 && TryGetPwdSnapshot(objectId, out PwdEntry e)
            && PwdLayout.TryGetFloat(e.Bytes, stype, out value);
    }

    private static bool TryGetPwdSnapshotDataId(uint objectId, uint stype, out uint value)
    {
        value = 0;
        return PwdLayout.DataIdOffset(stype) >= 0 && TryGetPwdSnapshot(objectId, out PwdEntry e)
            && PwdLayout.TryGetDataId(e.Bytes, stype, out value);
    }

    private static bool TryGetPwdSnapshotInstanceId(uint objectId, uint stype, out uint value)
    {
        value = 0;
        return PwdLayout.InstanceIdOffset(stype) >= 0 && TryGetPwdSnapshot(objectId, out PwdEntry e)
            && PwdLayout.TryGetInstanceId(e.Bytes, stype, out value);
    }

    /// <summary>The PWD snapshot has been filled at least once (AutoId: an object missing from it may still come).</summary>
    internal static bool HasPwdSnapshot
    {
        get { lock (_weeniePtrSwapLock) return _pwdFront.Count > 0; }
    }

    /// <summary>The object holds items (ItemsCapacity, stype 6, &gt; 0 in the PWD snapshot: a pack, a chest).</summary>
    internal static bool IsContainerInSnapshot(uint objectId) =>
        TryGetPwdSnapshotInt(objectId, 6, out int capacity) && capacity > 0;

    /// <summary>
    /// EquipmentTracker's start: the ids the PWD snapshot shows as wielded by <paramref name="playerId"/>
    /// in no container, with their location. Both fields must agree: the wielder alone stays on an
    /// item after it went into a pack (2026-10-17). Off-thread safe (snapshot only).
    /// </summary>
    internal static void CollectWieldedFromSnapshot(uint playerId, List<(uint Id, uint Loc)> dest)
    {
        lock (_weeniePtrSwapLock)
        {
            foreach (KeyValuePair<uint, PwdEntry> kv in _pwdFront)
            {
                if (kv.Key == playerId) continue;
                if (kv.Value.Wielder == playerId && kv.Value.Container == 0 && kv.Value.Location != 0)
                    dest.Add((kv.Key, kv.Value.Location));
            }
        }
    }

    /// <summary>
    /// Off-thread helper for UpdateObjectInventoryHooks: writes the ids whose PWD
    /// container or wielder is <paramref name="containerId"/> from the main-thread
    /// snapshot, under one lock, with no AC access. Returns the count written.
    /// </summary>
    internal static int CollectOwnedIdsFromSnapshot(uint containerId, Span<uint> dest)
    {
        int found = 0;
        lock (_weeniePtrSwapLock)
        {
            foreach (KeyValuePair<uint, PwdEntry> kv in _pwdFront)
            {
                if (found >= dest.Length) break;
                if (kv.Key == containerId) continue;
                if (kv.Value.Container == containerId || kv.Value.Wielder == containerId)
                    dest[found++] = kv.Key;
            }
        }
        return found;
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
        // Off the main thread: never hand out a raw AC object pointer (2026-09-30).
        // It used to serve _weeniePtrFront, but a pointer up to ~100 ms old can be
        // freed and reused before the caller dereferences it (the use-after-free
        // class behind the old PWD / physics / enchantment reads). Every off-thread
        // reader is now served from a value snapshot (identity, position, PWD
        // fields, enchantments) before it gets here, so Zero only makes any
        // remaining or future caller fail closed. _weeniePtrFront stays for the
        // snapshot diagnostics.
        return IntPtr.Zero;
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

        // Off AC's main thread (RynthAi DoorInteractionController's ETHEREAL check,
        // meta expressions): m_state from the position snapshot (<= ~100 ms old).
        // The live path below dereferenced a cached, possibly freed weenie and its
        // CPhysicsObj from the pump. A miss is false, as before.
        if (!MainThreadGuard.IsOnMainThread())
        {
            lock (_positionCacheLock)
            {
                if (_positionCache.TryGetValue(objectId, out PosEntry p) && p.HasState)
                {
                    state = p.State;
                    return true;
                }
            }
            return false;
        }

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

        // Off AC's main thread (meta getheading, CorpseOpenController): the heading
        // from the position snapshot's quaternion (<= ~100 ms old). The live path
        // below read a cached weenie's CPhysicsObj vtable with no probe at all.
        if (!MainThreadGuard.IsOnMainThread())
        {
            lock (_positionCacheLock)
            {
                if (_positionCache.TryGetValue(objectId, out PosEntry p))
                {
                    headingDegrees = HeadingFromQuaternion(p.Qw, p.Qz);
                    return true;
                }
            }
            return false;
        }

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

            // Same guard as TryGetObjectPhysicsState / ReadPhysicsFromWeenie.
            if (!IsReadablePointer(physicsObj))
                return false;

            IntPtr vtable = Marshal.ReadIntPtr(physicsObj);
            if (!SmartBoxLocator.IsPointerInModule(vtable))
                return false;

            IntPtr pos = physicsObj + PhysicsPositionOffset;
            float qw = ReadFloat(pos + PositionQwOffset);
            float qz = ReadFloat(pos + PositionQzOffset);

            headingDegrees = HeadingFromQuaternion(qw, qz);
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
        // Main thread only: it walks the SmartBox player and scans 0x200 bytes of
        // the player weenie. PrefetchPositions (main thread) runs it anyway.
        if (!MainThreadGuard.IsOnMainThread())
            return;

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
