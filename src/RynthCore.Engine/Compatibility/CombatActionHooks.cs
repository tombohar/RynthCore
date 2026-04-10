using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Hooking;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.Compatibility;

internal static class CombatActionHooks
{
    private const int QueryHealthResponseVa = 0x006AA900;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool TargetedMeleeAttackDelegate(uint targetId, int attackHeight, float powerLevel);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool TargetedMissileAttackDelegate(uint targetId, int attackHeight, float accuracyLevel);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool ChangeCombatModeDelegate(int combatMode);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool CancelAttackDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool QueryHealthDelegate(uint targetId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool RequestIdDelegate(uint objectId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool CastSpellDelegate(uint targetId, int spellId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint QueryHealthResponseDelegate(IntPtr uiQueueManager, IntPtr buffer, uint size);

    public const int AttackHeightHigh = 1;
    public const int AttackHeightMedium = 2;
    public const int AttackHeightLow = 3;

    public const int CombatModeNonCombat = 1;
    public const int CombatModeMelee = 2;
    public const int CombatModeMissile = 4;
    public const int CombatModeMagic = 8;

    private static readonly byte[] CombatPrologue = [0x83, 0xEC, 0x0C, 0x53, 0x56, 0x57, 0xE8];

    private static TargetedMeleeAttackDelegate? _meleeAttack;
    private static TargetedMissileAttackDelegate? _missileAttack;
    private static ChangeCombatModeDelegate? _changeCombatMode;
    private static CancelAttackDelegate? _cancelAttack;
    private static QueryHealthDelegate? _queryHealth;
    private static RequestIdDelegate? _requestId;
    private static CastSpellDelegate? _castSpell;
    private static QueryHealthResponseDelegate? _queryHealthResponseDetour;
    private static QueryHealthResponseDelegate? _originalQueryHealthResponse;
    private static QueryHealthResponseDelegate? _identifyObjectDetour;
    private static QueryHealthResponseDelegate? _originalIdentifyObject;
    private static string _statusMessage = "Not probed yet.";

    public static bool IsInitialized { get; private set; }
    public static bool HasMeleeAttack => _meleeAttack != null;
    public static bool HasMissileAttack => _missileAttack != null;
    public static bool HasChangeCombatMode => _changeCombatMode != null;
    public static bool HasCancelAttack => _cancelAttack != null;
    public static bool HasQueryHealth => _queryHealth != null;
    public static bool HasRequestId => _requestId != null;
    public static bool HasCastSpell => _castSpell != null;
    public static string StatusMessage => _statusMessage;

    public static bool Probe()
    {
        Reset();

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection textSection))
        {
            _statusMessage = "acclient.exe not available.";
            return false;
        }

        try
        {
            byte[] text = textSection.Bytes;

            byte[] cancelOpcode = [0xC7, 0x02, 0xB7, 0x01, 0x00, 0x00];
            int cancelOpcodeOff = PatternScanner.FindPattern(text, cancelOpcode);
            if (cancelOpcodeOff < 0)
            {
                _statusMessage = "CancelAttack opcode 0x1B7 not found.";
                RynthLog.Compat("Compat: combat probe failed - CancelAttack opcode 0x1B7 not found.");
                return false;
            }

            int cancelFuncOff = PatternScanner.FindPrologueBefore(text, cancelOpcodeOff, CombatPrologue);
            if (cancelFuncOff < 0)
            {
                _statusMessage = "CancelAttack prologue not found.";
                RynthLog.Compat("Compat: combat probe failed - CancelAttack prologue not found.");
                return false;
            }

            int regionStart = Math.Max(0, cancelOpcodeOff - 0x500);
            int regionEnd = Math.Min(text.Length, cancelOpcodeOff + 0x500);

            byte[] changeModeOpcode = [0xC7, 0x02, 0x53, 0x00, 0x00, 0x00];
            int changeModeOff = PatternScanner.FindPatternInRegion(text, changeModeOpcode, regionStart, regionEnd);
            int changeModeFuncOff = changeModeOff >= 0
                ? PatternScanner.FindPrologueBefore(text, changeModeOff, CombatPrologue)
                : -1;

            byte[] queryHealthOpcode = [0xC7, 0x02, 0xBF, 0x01, 0x00, 0x00];
            int queryHealthOff = PatternScanner.FindPatternInRegion(text, queryHealthOpcode, regionStart, regionEnd);
            int queryHealthFuncOff = queryHealthOff >= 0
                ? PatternScanner.FindPrologueBefore(text, queryHealthOff, CombatPrologue)
                : -1;

            byte[] meleeOpcode = [0xC7, 0x02, 0x08, 0x00, 0x00, 0x00];
            int meleeOff = PatternScanner.FindPatternInRegion(text, meleeOpcode, regionStart, regionEnd);
            int meleeFuncOff = meleeOff >= 0
                ? PatternScanner.FindPrologueBefore(text, meleeOff, CombatPrologue)
                : -1;

            byte[] missileOpcode = [0xC7, 0x02, 0x0A, 0x00, 0x00, 0x00];
            int missileOff = PatternScanner.FindPatternInRegion(text, missileOpcode, regionStart, regionEnd);
            int missileFuncOff = missileOff >= 0
                ? PatternScanner.FindPrologueBefore(text, missileOff, CombatPrologue)
                : -1;

            // RequestId / IdentifyObject — game action 0xC8
            // Search wider region since it may not be near the combat actions
            int wideRegionStart = Math.Max(0, cancelOpcodeOff - 0x2000);
            int wideRegionEnd = Math.Min(text.Length, cancelOpcodeOff + 0x2000);
            byte[] requestIdOpcode = [0xC7, 0x02, 0xC8, 0x00, 0x00, 0x00];
            int requestIdOff = PatternScanner.FindPatternInRegion(text, requestIdOpcode, wideRegionStart, wideRegionEnd);
            int requestIdFuncOff = requestIdOff >= 0
                ? PatternScanner.FindPrologueBefore(text, requestIdOff, CombatPrologue)
                : -1;

            // CastSpell — search full text section for known candidate opcodes
            // Candidates: 0x4A (cast targeted), 0x48, 0x55, 0x5D
            int castSpellFuncOff = -1;
            int castSpellOff = -1;
            byte[][] castSpellCandidates = [
                [0xC7, 0x02, 0x4A, 0x00, 0x00, 0x00],
                [0xC7, 0x02, 0x48, 0x00, 0x00, 0x00],
                [0xC7, 0x02, 0x55, 0x00, 0x00, 0x00],
                [0xC7, 0x02, 0x5D, 0x00, 0x00, 0x00],
            ];
            foreach (byte[] candidate in castSpellCandidates)
            {
                int off = PatternScanner.FindPattern(text, candidate);
                if (off >= 0)
                {
                    int funcOff = PatternScanner.FindPrologueBefore(text, off, CombatPrologue);
                    if (funcOff >= 0)
                    {
                        castSpellOff = off;
                        castSpellFuncOff = funcOff;
                        RynthLog.Compat($"Compat: CastSpell candidate opcode=0x{candidate[2]:X2} matched at text+0x{off:X}");
                        break;
                    }
                }
            }

            if (changeModeFuncOff < 0 || queryHealthFuncOff < 0 || meleeFuncOff < 0 || missileFuncOff < 0)
            {
                _statusMessage =
                    $"Incomplete. mode={changeModeFuncOff >= 0}, health={queryHealthFuncOff >= 0}, melee={meleeFuncOff >= 0}, missile={missileFuncOff >= 0}.";
                RynthLog.Compat($"Compat: combat probe incomplete - {_statusMessage}");
                return false;
            }

            if (!PatternScanner.VerifyBytes(text, cancelFuncOff, CombatPrologue) ||
                !PatternScanner.VerifyBytes(text, changeModeFuncOff, CombatPrologue) ||
                !PatternScanner.VerifyBytes(text, queryHealthFuncOff, CombatPrologue) ||
                !PatternScanner.VerifyBytes(text, meleeFuncOff, CombatPrologue) ||
                !PatternScanner.VerifyBytes(text, missileFuncOff, CombatPrologue))
            {
                _statusMessage = "Prologue verification failed.";
                RynthLog.Compat("Compat: combat probe failed - prologue verification failed.");
                return false;
            }

            int cancelVa = textSection.TextBaseVa + cancelFuncOff;
            int changeModeVa = textSection.TextBaseVa + changeModeFuncOff;
            int queryHealthVa = textSection.TextBaseVa + queryHealthFuncOff;
            int meleeVa = textSection.TextBaseVa + meleeFuncOff;
            int missileVa = textSection.TextBaseVa + missileFuncOff;

            _cancelAttack = Marshal.GetDelegateForFunctionPointer<CancelAttackDelegate>(new IntPtr(cancelVa));
            _changeCombatMode = Marshal.GetDelegateForFunctionPointer<ChangeCombatModeDelegate>(new IntPtr(changeModeVa));
            _queryHealth = Marshal.GetDelegateForFunctionPointer<QueryHealthDelegate>(new IntPtr(queryHealthVa));
            _meleeAttack = Marshal.GetDelegateForFunctionPointer<TargetedMeleeAttackDelegate>(new IntPtr(meleeVa));
            _missileAttack = Marshal.GetDelegateForFunctionPointer<TargetedMissileAttackDelegate>(new IntPtr(missileVa));

            if (requestIdFuncOff >= 0)
            {
                int requestIdVa = textSection.TextBaseVa + requestIdFuncOff;
                _requestId = Marshal.GetDelegateForFunctionPointer<RequestIdDelegate>(new IntPtr(requestIdVa));
                RynthLog.Compat($"Compat: RequestId (IdentifyObject 0xC8) found at 0x{requestIdVa:X8}");
            }
            else
            {
                RynthLog.Compat("Compat: RequestId (IdentifyObject 0xC8) not found — target appraisal unavailable.");
            }

            if (castSpellFuncOff >= 0)
            {
                int castSpellVa = textSection.TextBaseVa + castSpellFuncOff;
                _castSpell = Marshal.GetDelegateForFunctionPointer<CastSpellDelegate>(new IntPtr(castSpellVa));
                RynthLog.Compat($"Compat: CastSpell (0x4A) found at 0x{castSpellVa:X8}");
            }
            else
            {
                RynthLog.Compat("Compat: CastSpell (0x4A) not found — magic combat unavailable.");
            }

            InstallQueryHealthResponseHook();
            InstallInnerDispatcherHook();

            IsInitialized = true;
            _statusMessage = "Ready.";

            RynthLog.Compat($"Compat: combat hooks ready - cancel=0x{cancelVa:X8}, mode=0x{changeModeVa:X8}, health=0x{queryHealthVa:X8}, melee=0x{meleeVa:X8}, missile=0x{missileVa:X8}");
            return true;
        }
        catch (Exception ex)
        {
            Reset();
            _statusMessage = ex.Message;
            RynthLog.Compat($"Compat: combat probe failed - {ex.Message}");
            return false;
        }
    }

    public static bool MeleeAttack(uint targetId, int attackHeight, float powerLevel)
    {
        if (_meleeAttack == null)
            return false;

        try
        {
            return _meleeAttack(targetId, attackHeight, Math.Clamp(powerLevel, 0f, 1f));
        }
        catch
        {
            return false;
        }
    }

    public static bool MissileAttack(uint targetId, int attackHeight, float accuracyLevel)
    {
        if (_missileAttack == null)
            return false;

        try
        {
            return _missileAttack(targetId, attackHeight, Math.Clamp(accuracyLevel, 0f, 1f));
        }
        catch
        {
            return false;
        }
    }

    public static bool ChangeCombatMode(int combatMode)
    {
        if (_changeCombatMode == null)
            return false;

        try
        {
            return _changeCombatMode(combatMode);
        }
        catch
        {
            return false;
        }
    }

    public static bool CancelAttack()
    {
        if (_cancelAttack == null)
            return false;

        try
        {
            return _cancelAttack();
        }
        catch
        {
            return false;
        }
    }

    public static bool QueryHealth(uint targetId)
    {
        if (_queryHealth == null)
            return false;

        try
        {
            return _queryHealth(targetId);
        }
        catch
        {
            return false;
        }
    }

    public static bool RequestId(uint objectId)
    {
        if (_requestId == null || objectId == 0)
            return false;

        try
        {
            return _requestId(objectId);
        }
        catch
        {
            return false;
        }
    }

    public static bool CastSpell(uint targetId, int spellId)
    {
        if (_castSpell == null || targetId == 0 || spellId <= 0)
            return false;

        try
        {
            return _castSpell(targetId, spellId);
        }
        catch
        {
            return false;
        }
    }

    public static int MapAttackHeight(int uiHeight)
    {
        return uiHeight switch
        {
            0 => AttackHeightLow,
            2 => AttackHeightHigh,
            _ => AttackHeightMedium
        };
    }

    private static void Reset()
    {
        _meleeAttack = null;
        _missileAttack = null;
        _changeCombatMode = null;
        _cancelAttack = null;
        _queryHealth = null;
        _requestId = null;
        _castSpell = null;
        _queryHealthResponseDetour = null;
        _originalQueryHealthResponse = null;
        _identifyObjectDetour = null;
        _originalIdentifyObject = null;
        IsInitialized = false;
    }

    /// Hooks the inner game-event dispatcher at its entry point. The dispatcher is thiscall
    /// with RET 8: uint __thiscall Dispatch(void* buffer, uint size). We intercept 0xC9
    /// (IdentifyObject response) events and parse the CreatureProfile for health/maxHealth.
    /// </summary>
    private static IntPtr _originalInnerDispatcherPtr;

    private static void InstallInnerDispatcherHook()
    {
        if (!AcClientModule.TryReadTextSection(out AcClientTextSection textSection))
            return;

        byte[] text = textSection.Bytes;
        int textBase = textSection.TextBaseVa;

        // Find the call to QueryHealthResponse to anchor the inner dispatcher region
        int anchorOff = -1;
        for (int i = 0; i < text.Length - 5; i++)
        {
            if (text[i] != 0xE8) continue;
            int rel = BitConverter.ToInt32(text, i + 1);
            if (textBase + i + 5 + rel == QueryHealthResponseVa)
            {
                anchorOff = i;
                break;
            }
        }

        if (anchorOff < 0)
        {
            RynthLog.Compat("Compat: dispatch scan - no CALL to QueryHealthResponse found.");
            return;
        }

        // Find the inner dispatcher function entry by searching backward for SUB ESP, 0x1C0
        int subEspOff = -1;
        for (int i = anchorOff; i >= Math.Max(0, anchorOff - 0x1000); i--)
        {
            if (text[i] == 0x81 && text[i + 1] == 0xEC &&
                text[i + 2] == 0xC0 && text[i + 3] == 0x01 &&
                text[i + 4] == 0x00 && text[i + 5] == 0x00)
            {
                subEspOff = i;
                break;
            }
        }

        if (subEspOff < 0)
        {
            RynthLog.Compat("Compat: inner dispatcher SUB ESP not found.");
            return;
        }

        int funcEntryVa = textBase + subEspOff;
        RynthLog.Compat($"Compat: inner dispatcher entry @ 0x{funcEntryVa:X8}");

        try
        {
            unsafe
            {
                delegate* unmanaged[Thiscall]<IntPtr, IntPtr, uint, uint> pDetour = &InnerDispatcherDetour;
                MinHook.Hook(new IntPtr(funcEntryVa), (IntPtr)pDetour, out _originalInnerDispatcherPtr);
                RynthLog.Compat($"Compat: inner dispatcher hook installed @ 0x{funcEntryVa:X8}");
            }
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"Compat: inner dispatcher hook failed - {ex.Message}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    private static unsafe uint InnerDispatcherDetour(IntPtr thisPtr, IntPtr buffer, uint size)
    {
        var pOriginal = (delegate* unmanaged[Thiscall]<IntPtr, IntPtr, uint, uint>)_originalInnerDispatcherPtr;

        try
        {
            if (buffer != IntPtr.Zero && size >= 4)
            {
                uint eventType = unchecked((uint)Marshal.ReadInt32(buffer));

                // Intercept IdentifyObject response (0xC9)
                if (eventType == 0xC9 && size >= 16)
                    TryParseIdentifyResponse(buffer, size);

                // Intercept character list packet (0xF658) — login screen
                if (eventType == 0xF7EA || eventType == 0xF658)
                    CharacterCaptureHooks.ProcessRawCharacterMessage(buffer, size);
            }
        }
        catch
        {
        }

        return pOriginal(thisPtr, buffer, size);
    }

    /// <summary>
    /// Parses the IdentifyObject response (game event 0xC9) to extract health/maxHealth
    /// from the CreatureProfile section.
    /// Layout: [eventType(4)][objectId(4)][flags(4)][success(4)][sections based on flags...]
    /// Wire serialization order (matches ACE, NOT flag-bit order):
    ///   0x0001 IntStatsTable, 0x1000 Int64StatsTable, 0x0002 BoolStatsTable,
    ///   0x0004 FloatStatsTable, 0x0008 StringStatsTable, 0x0010 SpellBook,
    ///   0x0020 ArmorProfile, 0x0040 CreatureProfile, 0x0080 WeaponProfile,
    ///   0x0100 HookProfile, ...
    /// </summary>
    private static void TryParseIdentifyResponse(IntPtr buffer, uint size)
    {
        try
        {
            // Header: eventType(4) + objectId(4) + flags(4) + success(4) = 16 bytes
            uint objectId = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(buffer, 4)));
            uint flags = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(buffer, 8)));
            uint success = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(buffer, 12)));

            if (success == 0 || objectId == 0)
                return;

            // CreatureProfile = flag 0x1000 in the retail client
            if ((flags & 0x1000) == 0)
                return;

            int offset = 16; // Past the header
            int iSize = (int)size;

            // Skip sections in wire order before CreatureProfile (0x1000):
            // 0x0001 - IntStatsTable: PHashTable<uint,int> = header(4) + count*8
            if ((flags & 0x0001) != 0 && !SkipPackedHashTable(buffer, iSize, ref offset, 8))
                return;
            // 0x0002 - BoolStatsTable: PHashTable<uint,int> = header(4) + count*8
            if ((flags & 0x0002) != 0 && !SkipPackedHashTable(buffer, iSize, ref offset, 8))
                return;
            // 0x0004 - FloatStatsTable: PHashTable<uint,double> = header(4) + count*12
            if ((flags & 0x0004) != 0 && !SkipPackedHashTable(buffer, iSize, ref offset, 12))
                return;
            // 0x0008 - StringStatsTable: PHashTable<uint,string> — variable
            if ((flags & 0x0008) != 0 && !SkipStringHashTable(buffer, iSize, ref offset))
                return;
            // 0x0010 - SpellBook: header(4) + count*4
            if ((flags & 0x0010) != 0 && !SkipPackedHashTable(buffer, iSize, ref offset, 4))
                return;
            // 0x0020, 0x0040, 0x0080 — unknown fixed sections; bail if present
            if ((flags & 0x00E0) != 0)
                return;
            // 0x0100 - Int64StatsTable: PHashTable<uint,int32> = header(4) + count*8
            // (retail client serialises "Int64" properties as 32-bit values)
            if ((flags & 0x0100) != 0 && !SkipPackedHashTable(buffer, iSize, ref offset, 8))
                return;
            // 0x0200, 0x0400, 0x0800 — unknown sections; bail if present
            if ((flags & 0x0E00) != 0)
                return;

            // 0x1000 - CreatureProfile layout:
            //   flags(4), health(4), maxHealth(4),
            //   strength(4), endurance(4), quickness(4), coordination(4), focus(4), self(4),
            //   stamina(4), maxStamina(4), mana(4), maxMana(4)
            // Total: 4 + 12*4 = 52 bytes
            if (offset + 52 > iSize)
                return;

            // Skip the 4-byte CreatureProfile header/flags
            offset += 4;
            uint health = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(buffer, offset)));
            uint maxHealth = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(buffer, offset + 4)));
            // Skip 6 primary attributes (str, end, quick, coord, focus, self) = 24 bytes
            // Layout: stamina(4), mana(4), maxStamina(4), maxMana(4)
            uint stamina = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(buffer, offset + 32)));
            uint mana = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(buffer, offset + 36)));
            uint maxStamina = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(buffer, offset + 40)));
            uint maxMana = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(buffer, offset + 44)));

            if (maxHealth > 0 && maxHealth < 1_000_000 && objectId != 0)
            {
                var vitals = new CreatureVitals(health, maxHealth, stamina, maxStamina, mana, maxMana);
                ObjectQualityCache.SetCreatureVitals(objectId, vitals);

                if (ClientObjectHooks.TryGetWeenieObjectPtr(objectId, out IntPtr pWeenie) && pWeenie != IntPtr.Zero)
                    ObjectQualityCache.SetMaxHealth(pWeenie, maxHealth);

                float ratio = maxHealth > 0 ? (float)health / maxHealth : 0f;
                PluginManager.QueueUpdateHealth(objectId, ratio, health, maxHealth);

                // If this is the player, seed exact max vitals
                uint playerId = ClientHelperHooks.GetPlayerId();
                if (playerId != 0 && objectId == playerId)
                    PlayerVitalsHooks.SeedMaxVitalsFromIdentify(maxHealth, maxStamina, maxMana);
            }
        }
        catch
        {
        }
    }

    /// <summary>Skips a packed hash table with fixed-size entries.</summary>
    private static bool SkipPackedHashTable(IntPtr buffer, int size, ref int offset, int entrySize)
    {
        if (offset + 4 > size) return false;
        ushort entryCount = unchecked((ushort)Marshal.ReadInt16(IntPtr.Add(buffer, offset)));
        offset += 4; // header: count(2) + buckets(2)
        int dataSize = entryCount * entrySize;
        offset += dataSize;
        return offset <= size;
    }

    /// <summary>Skips a packed hash table with string values (variable length).</summary>
    private static bool SkipStringHashTable(IntPtr buffer, int size, ref int offset)
    {
        if (offset + 4 > size) return false;
        ushort entryCount = unchecked((ushort)Marshal.ReadInt16(IntPtr.Add(buffer, offset)));
        offset += 4; // header
        for (int i = 0; i < entryCount; i++)
        {
            if (offset + 6 > size) return false;
            offset += 4; // key (uint32)
            // AC packed string: ushort length, then chars, then padding to 4-byte boundary
            ushort strLen = unchecked((ushort)Marshal.ReadInt16(IntPtr.Add(buffer, offset)));
            offset += 2;
            offset += strLen;
            // Align to 4-byte boundary (from the start of the string field, which is offset-2-strLen)
            int totalStringField = 2 + strLen;
            int padding = (4 - (totalStringField % 4)) % 4;
            offset += padding;
        }
        return offset <= size;
    }

    private static void InstallQueryHealthResponseHook()
    {
        if (_originalQueryHealthResponse != null)
            return;

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection textSection))
            return;

        IntPtr responsePtr = new(QueryHealthResponseVa);
        int textStart = textSection.TextBaseVa;
        int textEnd = textStart + textSection.Bytes.Length;
        int responseVa = responsePtr.ToInt32();
        if (responseVa < textStart || responseVa >= textEnd)
        {
            RynthLog.Compat($"Compat: query-health-response hook skipped - invalid address 0x{QueryHealthResponseVa:X8}");
            return;
        }

        _queryHealthResponseDetour = QueryHealthResponseDetour;
        IntPtr detourPtr = Marshal.GetFunctionPointerForDelegate(_queryHealthResponseDetour);
        IntPtr originalPtr = MinHook.HookCreate(responsePtr, detourPtr);
        _originalQueryHealthResponse = Marshal.GetDelegateForFunctionPointer<QueryHealthResponseDelegate>(originalPtr);
        Thread.MemoryBarrier();
        MinHook.Enable(responsePtr);
        RynthLog.Compat($"Compat: query-health-response hook ready - Handle_Combat__QueryHealthResponse=0x{QueryHealthResponseVa:X8}");
    }

    private static uint QueryHealthResponseDetour(IntPtr uiQueueManager, IntPtr buffer, uint size)
    {
        try
        {
            if (buffer != IntPtr.Zero && size >= 12)
            {
                uint opcode = unchecked((uint)Marshal.ReadInt32(buffer));
                uint targetId = unchecked((uint)Marshal.ReadInt32(IntPtr.Add(buffer, 4)));
                float healthRatio = Marshal.PtrToStructure<float>(IntPtr.Add(buffer, 8));

                // Resolve absolute health: first check the cache, then try a direct
                // read via InqAttribute2ndStruct (works after an IdentifyObject response
                // has populated the weenie's qualities). This detour runs on the game
                // thread so the direct read is safe.
                uint maxHealth = 0;
                uint currentHealth = 0;
                if (ClientObjectHooks.TryGetWeenieObjectPtr(targetId, out IntPtr pWeenie))
                {
                    if (ObjectQualityCache.TryGetMaxHealth(pWeenie, out uint cached))
                    {
                        maxHealth = cached;
                    }
                    else if (PlayerVitalsHooks.TryReadObjectMaxHealth(pWeenie, out uint queried) && queried > 0)
                    {
                        maxHealth = queried;
                        ObjectQualityCache.SetMaxHealth(pWeenie, queried);
                    }

                    if (maxHealth > 0)
                        currentHealth = (uint)Math.Round(maxHealth * Math.Clamp(healthRatio, 0f, 1f));
                }

                // If this response is for the player, derive true MaxHealth from the ratio.
                // e.g. health=92541, ratio=0.925 → trueMax ≈ 99999
                uint playerId = ClientHelperHooks.GetPlayerId();
                if (playerId != 0 && targetId == playerId && healthRatio > 0f && healthRatio <= 1f)
                    PlayerVitalsHooks.UpdateMaxFromHealthRatio(healthRatio);

                PluginManager.QueueUpdateHealth(targetId, healthRatio, currentHealth, maxHealth);
            }
        }
        catch
        {
        }

        return _originalQueryHealthResponse != null
            ? _originalQueryHealthResponse(uiQueueManager, buffer, size)
            : 0;
    }

}
