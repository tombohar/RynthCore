using System;
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

    private static string _statusMessage = "Not probed yet.";
    private static GetWeenieObjectDelegate? _getWeenieObject;
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

    private static bool BindDelegates()
    {
        if (_getWeenieObject != null && _getObjectNameStatic != null && _getObjectNameInstance != null)
            return true;

        IntPtr getWeeniePtr = new(ReferenceGetWeenieObject);
        IntPtr getNameStaticPtr = new(ReferenceGetObjectNameStatic);
        IntPtr getNameInstancePtr = new(ReferenceGetObjectNameInstance);
        
        if (!SmartBoxLocator.IsPointerInModule(getWeeniePtr) ||
            !SmartBoxLocator.IsPointerInModule(getNameStaticPtr) ||
            !SmartBoxLocator.IsPointerInModule(getNameInstancePtr))
        {
            _statusMessage =
                $"ClientObject pointers look invalid (getWeenie=0x{getWeeniePtr.ToInt32():X8}, getNameStatic=0x{getNameStaticPtr.ToInt32():X8}, getNameInstance=0x{getNameInstancePtr.ToInt32():X8}).";
            RynthLog.Compat($"Compat: client object bind failed - {_statusMessage}");
            return false;
        }

        _getWeenieObject = Marshal.GetDelegateForFunctionPointer<GetWeenieObjectDelegate>(getWeeniePtr);
        _getObjectNameStatic = Marshal.GetDelegateForFunctionPointer<GetObjectNameStaticDelegate>(getNameStaticPtr);
        _getObjectNameInstance = Marshal.GetDelegateForFunctionPointer<GetObjectNameInstanceDelegate>(getNameInstancePtr);
        _inqType = Marshal.GetDelegateForFunctionPointer<InqTypeDelegate>(new IntPtr(ReferenceInqType));
        _inqInt   = Marshal.GetDelegateForFunctionPointer<InqIntDelegate>(new IntPtr(ReferenceInqInt));
        _inqInt64 = Marshal.GetDelegateForFunctionPointer<InqInt64Delegate>(new IntPtr(ReferenceInqInt64));
        _inqAttribute2ndBaseLevel = Marshal.GetDelegateForFunctionPointer<InqAttribute2ndBaseLevelDelegate>(new IntPtr(ReferenceInqAttribute2ndBaseLevel));
        _inqFloat = Marshal.GetDelegateForFunctionPointer<InqFloatDelegate>(new IntPtr(ReferenceInqFloat));
        _inqBool  = Marshal.GetDelegateForFunctionPointer<InqBoolDelegate>(new IntPtr(ReferenceInqBool));
        _inqString = Marshal.GetDelegateForFunctionPointer<InqStringDelegate>(new IntPtr(ReferenceInqString));
        _getCombatSystem = Marshal.GetDelegateForFunctionPointer<GetCombatSystemDelegate>(new IntPtr(ReferenceGetCombatSystem));
        _objectIsAttackable = Marshal.GetDelegateForFunctionPointer<ObjectIsAttackableDelegate>(new IntPtr(ReferenceObjectIsAttackable));
        _inqSkillLevel = Marshal.GetDelegateForFunctionPointer<InqSkillLevelDelegate>(new IntPtr(ReferenceInqSkillLevel));
        _inqSkillAdvancementClass = Marshal.GetDelegateForFunctionPointer<InqSkillAdvancementClassDelegate>(new IntPtr(ReferenceInqSkillAdvancementClass));
        _inqAttribute = Marshal.GetDelegateForFunctionPointer<InqAttributeDelegate>(new IntPtr(ReferenceInqAttribute));
        _isSpellKnown = Marshal.GetDelegateForFunctionPointer<IsSpellKnownDelegate>(new IntPtr(ReferenceIsSpellKnown));
        _getVitaeValue = Marshal.GetDelegateForFunctionPointer<GetVitaeValueDelegate>(new IntPtr(ReferenceGetVitaeValue));
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

            // Validate the qualities pointer itself — a real CACQualities* on the heap
            // should be at a committed, readable page.
            if (!IsReadablePointer(qualitiesPtr))
                return false;

            return true;
        }
        catch { return false; }
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
                RynthLog.Verbose($"TryGetObjectSkill: m_pQualities null for 0x{objectId:X8}");
                return false;
            }

            if (!TryReadSkillFromTable(qualitiesPtr, skillStype, out SkillNative skill, out IntPtr skillTablePtr, out uint tableSize))
                return false;

            training = unchecked((int)skill.AdvancementClass);
            buffed = unchecked((int)(skill.InitialLevel + skill.LevelFromPracticePoints));

            if (_skillProbeLogCount < 6)
            {
                _skillProbeLogCount++;
                RynthLog.Verbose(
                    $"Compat: skillTableProbe obj=0x{objectId:X8} skill={skillStype} qualities=0x{qualitiesPtr.ToInt32():X8} " +
                    $"table=0x{skillTablePtr.ToInt32():X8} buckets={tableSize} training={training} base={buffed}");
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
    /// Returns a skill level via CACQualities::InqSkill(stype, retval, raw).
    /// raw=0 → buffed (with enchantments), raw=1 → base (no enchantments, includes attribute contribution).
    /// </summary>
    public static unsafe bool TryGetObjectSkillLevel(uint objectId, uint skillStype, int raw, out int level)
    {
        level = 0;
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

    private static bool TryGetObjectQualitiesPtr(uint objectId, out IntPtr qualitiesPtr)
    {
        qualitiesPtr = IntPtr.Zero;

        uint playerId = ClientHelperHooks.GetPlayerId();
        if (objectId == playerId && PlayerVitalsHooks.KnownPlayerQualitiesPtr != IntPtr.Zero)
        {
            qualitiesPtr = PlayerVitalsHooks.KnownPlayerQualitiesPtr;
            return IsReadablePointer(qualitiesPtr);
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
        if (_inqType == null || _getWeenieObject == null)
        {
            if (!Probe() || _inqType == null || _getWeenieObject == null)
                return false;
        }
        try
        {
            IntPtr weeniePtr = _getWeenieObject(objectId);
            if (weeniePtr == IntPtr.Zero)
                return false;

            typeFlags = _inqType(weeniePtr);
            return true;
        }
        catch
        {
            return false;
        }
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
            IntPtr nullBufferAddr = new IntPtr(PStringBaseNullBuffer);
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
    /// Calls ClientCombatSystem::ObjectIsAttackable — the same check the game uses
    /// before queueing an attack. Returns true (assume attackable) if the system is unavailable.
    /// </summary>
    public static bool ObjectIsAttackable(uint objectId)
    {
        if (_getCombatSystem == null || _objectIsAttackable == null || _getWeenieObject == null)
        {
            if (!Probe() || _getCombatSystem == null || _objectIsAttackable == null || _getWeenieObject == null)
                return true;
        }
        try
        {
            // Verify the object is still live in the client's object table before
            // calling into the combat system — stale IDs cause access violations
            // that bypass managed try/catch in NativeAOT.
            IntPtr weeniePtr = _getWeenieObject(objectId);
            if (weeniePtr == IntPtr.Zero)
                return false;

            IntPtr combatSystem = _getCombatSystem();
            if (combatSystem == IntPtr.Zero)
                return true;
            return _objectIsAttackable(combatSystem, objectId) != 0;
        }
        catch
        {
            return true;
        }
    }

    public static bool TryGetObjectName(uint objectId, out string name)
    {
        name = string.Empty;

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

            if (TryMarshalName(_getObjectNameInstance(pWeenie, NameTypeSingular, 0), out name))
                return true;

            if (TryMarshalName(_getObjectNameStatic(pWeenie, objectId, NameTypeSingular, 0), out name))
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
        if (_getWeenieObject == null && !Probe())
            return -1;
        if (_getWeenieObject == null)
            return -1;

        IntPtr weeniePtr = _getWeenieObject(objectId);
        if (weeniePtr == IntPtr.Zero)
            return -1;

        unsafe
        {
            return ((delegate* unmanaged[Thiscall]<IntPtr, int>)new IntPtr(ReferenceGetNumContainedItems))(weeniePtr);
        }
    }

    /// <summary>
    /// Calls ACCWeenieObject::GetNumContainedContainers() — the AC client's own count of
    /// how many packs/foci occupy container slots. Returns -1 if the weenie is unavailable.
    /// </summary>
    public static int GetNumContainedContainers(uint objectId)
    {
        if (_getWeenieObject == null && !Probe())
            return -1;
        if (_getWeenieObject == null)
            return -1;

        IntPtr weeniePtr = _getWeenieObject(objectId);
        if (weeniePtr == IntPtr.Zero)
            return -1;

        unsafe
        {
            return ((delegate* unmanaged[Thiscall]<IntPtr, int>)new IntPtr(ReferenceGetNumContainedContainers))(weeniePtr);
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
