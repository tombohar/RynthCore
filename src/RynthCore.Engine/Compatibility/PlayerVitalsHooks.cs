using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RynthCore.Engine.Hooking;

namespace RynthCore.Engine.Compatibility;

internal readonly record struct PlayerVitalsSnapshot(
    uint Health,
    uint MaxHealth,
    uint Stamina,
    uint MaxStamina,
    uint Mana,
    uint MaxMana);

internal static class PlayerVitalsHooks
{
    /// <summary>
    /// The player's CACQualities* (== PlayerDesc*), set when SendNoticePlayerDescReceived fires.
    /// Used by EnchantmentHooks to filter UpdateEnchantment/RemoveEnchantment to the player only.
    /// </summary>
    internal static IntPtr KnownPlayerQualitiesPtr { get; private set; }

    /// <summary>
    /// Allows other internal hooks to populate the qualities ptr when discovered
    /// via an alternative path (e.g. mid-session injection before the login hook fires).
    /// </summary>
    internal static void SetKnownPlayerQualitiesPtr(IntPtr ptr)
    {
        if (ptr != IntPtr.Zero)
            KnownPlayerQualitiesPtr = ptr;
    }

    private const int UpdateAttribute2ndVa = 0x00559900;
    private const int UpdateAttribute2ndLevelVa = 0x00559920;
    private const int PrivateUpdateAttribute2ndVa = 0x00559B20;
    private const int PrivateUpdateAttribute2ndLevelVa = 0x00559B50;
    private const int OnStatUpdatedIntVa = 0x0058ED50;
    private const int InqAttribute2ndStructVa = 0x005927F0;
    private const int SendNoticePlayerDescReceivedVa = 0x0047A200;
    private const int MaxUpdateLogs = 18;

    private const uint MaxHealthType = 1;
    private const uint HealthType = 2;
    private const uint MaxStaminaType = 3;
    private const uint StaminaType = 4;
    private const uint MaxManaType = 5;
    private const uint ManaType = 6;

    private static readonly object CacheLock = new();
    private static IntPtr _originalUpdateAttribute2ndPtr;
    private static IntPtr _originalUpdateAttribute2ndLevelPtr;
    private static IntPtr _originalPrivateUpdateAttribute2ndPtr;
    private static IntPtr _originalPrivateUpdateAttribute2ndLevelPtr;
    private static IntPtr _originalOnStatUpdatedIntPtr;
    private static IntPtr _originalSendNoticePlayerDescReceivedPtr;
    private static string _statusMessage = "Not probed yet.";
    private static int _updateLogCount;
    private static int _seedLogCount;
    private static PlayerVitalsSnapshot _snapshot;
    private static InqAttribute2ndStructDelegate? _inqAttribute2ndStruct;

    public static bool IsInstalled { get; private set; }
    public static string StatusMessage => _statusMessage;

    public static void Initialize()
    {
        if (IsInstalled)
            return;

        if (!AcClientModule.TryReadTextSection(out _))
        {
            _statusMessage = "acclient.exe not available.";
            return;
        }

        try
        {
            IntPtr updateAttributePtr = new(UpdateAttribute2ndVa);
            IntPtr updatePtr = new(UpdateAttribute2ndLevelVa);
            IntPtr privateUpdateAttributePtr = new(PrivateUpdateAttribute2ndVa);
            IntPtr privateUpdatePtr = new(PrivateUpdateAttribute2ndLevelVa);
            IntPtr onStatUpdatedIntPtr = new(OnStatUpdatedIntVa);
            IntPtr inqAttribute2ndStructPtr = new(InqAttribute2ndStructVa);
            IntPtr sendNoticePlayerDescReceivedPtr = new(SendNoticePlayerDescReceivedVa);

            if (!SmartBoxLocator.IsPointerInModule(updateAttributePtr) ||
                !SmartBoxLocator.IsPointerInModule(updatePtr) ||
                !SmartBoxLocator.IsPointerInModule(privateUpdateAttributePtr) ||
                !SmartBoxLocator.IsPointerInModule(privateUpdatePtr) ||
                !SmartBoxLocator.IsPointerInModule(onStatUpdatedIntPtr) ||
                !SmartBoxLocator.IsPointerInModule(inqAttribute2ndStructPtr) ||
                !SmartBoxLocator.IsPointerInModule(sendNoticePlayerDescReceivedPtr))
            {
                _statusMessage =
                    $"Attribute2nd handlers look invalid (update=0x{updateAttributePtr.ToInt32():X8}, level=0x{updatePtr.ToInt32():X8}, private=0x{privateUpdateAttributePtr.ToInt32():X8}, privateLevel=0x{privateUpdatePtr.ToInt32():X8}, stat=0x{onStatUpdatedIntPtr.ToInt32():X8}, inq2nd=0x{inqAttribute2ndStructPtr.ToInt32():X8}, playerDesc=0x{sendNoticePlayerDescReceivedPtr.ToInt32():X8}).";
                RynthLog.Compat($"Compat: player vitals hooks failed - {_statusMessage}");
                return;
            }

            _inqAttribute2ndStruct = Marshal.GetDelegateForFunctionPointer<InqAttribute2ndStructDelegate>(inqAttribute2ndStructPtr);

            unsafe
            {
                delegate* unmanaged[Thiscall]<IntPtr, byte, uint, uint, SecondaryAttributeNative*, uint> updateAttributeDetour = &HandleUpdateAttribute2ndDetour;
                delegate* unmanaged[Thiscall]<IntPtr, byte, uint, uint, int, uint> updateDetour = &HandleUpdateAttribute2ndLevelDetour;
                delegate* unmanaged[Thiscall]<IntPtr, byte, uint, SecondaryAttributeNative*, uint> privateUpdateAttributeDetour = &HandlePrivateUpdateAttribute2ndDetour;
                delegate* unmanaged[Thiscall]<IntPtr, byte, uint, int, uint> privateUpdateDetour = &HandlePrivateUpdateAttribute2ndLevelDetour;
                delegate* unmanaged[Thiscall]<IntPtr, uint, int, void> onStatUpdatedIntDetour = &HandleOnStatUpdatedIntDetour;
                delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte> sendNoticePlayerDescReceivedDetour = &HandleSendNoticePlayerDescReceivedDetour;

                MinHook.Hook(updateAttributePtr, (IntPtr)updateAttributeDetour, out _originalUpdateAttribute2ndPtr);
                MinHook.Hook(updatePtr, (IntPtr)updateDetour, out _originalUpdateAttribute2ndLevelPtr);
                MinHook.Hook(privateUpdateAttributePtr, (IntPtr)privateUpdateAttributeDetour, out _originalPrivateUpdateAttribute2ndPtr);
                MinHook.Hook(privateUpdatePtr, (IntPtr)privateUpdateDetour, out _originalPrivateUpdateAttribute2ndLevelPtr);
                MinHook.Hook(onStatUpdatedIntPtr, (IntPtr)onStatUpdatedIntDetour, out _originalOnStatUpdatedIntPtr);
                MinHook.Hook(sendNoticePlayerDescReceivedPtr, (IntPtr)sendNoticePlayerDescReceivedDetour, out _originalSendNoticePlayerDescReceivedPtr);
            }

            IsInstalled = true;
            _statusMessage = "Hooks installed.";
            RynthLog.Compat($"Compat: player vitals hooks ready - update=0x{UpdateAttribute2ndVa:X8}, level=0x{UpdateAttribute2ndLevelVa:X8}, private=0x{PrivateUpdateAttribute2ndVa:X8}, privateLevel=0x{PrivateUpdateAttribute2ndLevelVa:X8}, stat=0x{OnStatUpdatedIntVa:X8}, playerDesc=0x{SendNoticePlayerDescReceivedVa:X8}");
        }
        catch (Exception ex)
        {
            _statusMessage = ex.Message;
            RynthLog.Compat($"Compat: player vitals hooks failed - {ex.Message}");
        }
    }

    /// <summary>
    /// Reads MaxHealth from any CWeenieObject via InqAttribute2ndStruct.
    /// Must be called from the game thread (detours, not render thread).
    /// </summary>
    public static unsafe bool TryReadObjectMaxHealth(IntPtr weeniePtr, out uint maxHealth)
    {
        maxHealth = 0;
        if (_inqAttribute2ndStruct == null || weeniePtr == IntPtr.Zero)
            return false;

        SecondaryAttributeNative maxValue = default;
        if (_inqAttribute2ndStruct(weeniePtr, MaxHealthType, &maxValue) != 0 && maxValue._currentLevel > 0)
        {
            maxHealth = maxValue._currentLevel;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns the unbuffed base maximum vitals (base training + gear + augmentations, no spell enchantments).
    /// Uses _initLevel + _levelFromCp from InqAttribute2ndStruct on the current-vital stypes (2/4/6).
    /// This formula is confirmed "unbuffed base max" by the TryReadSecondary fallback path.
    /// Must be called after SendNoticePlayerDescReceived has fired (KnownPlayerQualitiesPtr set).
    /// </summary>
    public static unsafe bool TryGetPlayerBaseVitals(out uint baseMaxHp, out uint baseMaxStam, out uint baseMaxMana)
    {
        baseMaxHp = baseMaxStam = baseMaxMana = 0;
        if (_inqAttribute2ndStruct == null)
            return false;

        IntPtr ptr = KnownPlayerQualitiesPtr;
        if (ptr == IntPtr.Zero)
            return false;

        try
        {
            SecondaryAttributeNative v = default;
            if (_inqAttribute2ndStruct(ptr, HealthType, &v) != 0)
                baseMaxHp = v._initLevel + v._levelFromCp;
            if (_inqAttribute2ndStruct(ptr, StaminaType, &v) != 0)
                baseMaxStam = v._initLevel + v._levelFromCp;
            if (_inqAttribute2ndStruct(ptr, ManaType, &v) != 0)
                baseMaxMana = v._initLevel + v._levelFromCp;

            return baseMaxHp > 0 || baseMaxStam > 0 || baseMaxMana > 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryGetSnapshot(out PlayerVitalsSnapshot snapshot)
    {
        lock (CacheLock)
        {
            snapshot = _snapshot;
            return snapshot.Health > 0 || snapshot.MaxHealth > 0 ||
                   snapshot.Stamina > 0 || snapshot.MaxStamina > 0 ||
                   snapshot.Mana > 0 || snapshot.MaxMana > 0;
        }
    }

    /// <summary>
    /// Called when the player's own IdentifyObject (0xC9) response arrives,
    /// providing exact max vitals from the CreatureProfile section.
    /// </summary>
    public static void SeedMaxVitalsFromIdentify(uint maxHealth, uint maxStamina, uint maxMana)
    {
        if (maxHealth == 0 && maxStamina == 0 && maxMana == 0)
            return;

        lock (CacheLock)
        {
            _snapshot = _snapshot with
            {
                MaxHealth = maxHealth > 0 ? maxHealth : _snapshot.MaxHealth,
                MaxStamina = maxStamina > 0 ? maxStamina : _snapshot.MaxStamina,
                MaxMana = maxMana > 0 ? maxMana : _snapshot.MaxMana
            };
            RynthLog.Compat($"Compat: player max vitals from identify hp={maxHealth} st={maxStamina} mn={maxMana}");
        }
    }

    /// <summary>
    /// Called from QueryHealthResponseDetour when the player's own health ratio arrives.
    /// Computes trueMax = currentHealth / ratio, giving an accurate max immediately
    /// instead of waiting for regen to converge.
    /// </summary>
    public static void UpdateMaxFromHealthRatio(float healthRatio)
    {
        if (healthRatio <= 0f || healthRatio > 1f)
            return;

        lock (CacheLock)
        {
            if (_snapshot.Health == 0)
                return;

            uint derivedMax = (uint)Math.Round(_snapshot.Health / (double)healthRatio);
            if (derivedMax > _snapshot.MaxHealth)
            {
                _snapshot = _snapshot with { MaxHealth = derivedMax };
                RynthLog.Compat($"Compat: player MaxHealth derived from ratio={healthRatio:0.000} hp={_snapshot.Health} → max={derivedMax}");
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvThiscall) })]
    private static unsafe uint HandleUpdateAttribute2ndDetour(IntPtr thisPtr, byte wts, uint sender, uint stype, SecondaryAttributeNative* val)
    {
        var original = (delegate* unmanaged[Thiscall]<IntPtr, byte, uint, uint, SecondaryAttributeNative*, uint>)_originalUpdateAttribute2ndPtr;
        uint result = original(thisPtr, wts, sender, stype, val);
        UpdateCacheFromAttribute(sender, stype, val, isPrivate: false);
        return result;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvThiscall) })]
    private static unsafe uint HandleUpdateAttribute2ndLevelDetour(IntPtr thisPtr, byte wts, uint sender, uint stype, int val)
    {
        var original = (delegate* unmanaged[Thiscall]<IntPtr, byte, uint, uint, int, uint>)_originalUpdateAttribute2ndLevelPtr;
        uint result = original(thisPtr, wts, sender, stype, val);
        UpdateCache(sender, stype, val, isPrivate: false);
        return result;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvThiscall) })]
    private static unsafe uint HandlePrivateUpdateAttribute2ndDetour(IntPtr thisPtr, byte wts, uint stype, SecondaryAttributeNative* val)
    {
        var original = (delegate* unmanaged[Thiscall]<IntPtr, byte, uint, SecondaryAttributeNative*, uint>)_originalPrivateUpdateAttribute2ndPtr;
        uint result = original(thisPtr, wts, stype, val);
        UpdateCacheFromAttribute(0, stype, val, isPrivate: true);
        return result;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvThiscall) })]
    private static unsafe uint HandlePrivateUpdateAttribute2ndLevelDetour(IntPtr thisPtr, byte wts, uint stype, int val)
    {
        var original = (delegate* unmanaged[Thiscall]<IntPtr, byte, uint, int, uint>)_originalPrivateUpdateAttribute2ndLevelPtr;
        uint result = original(thisPtr, wts, stype, val);
        UpdateCache(0, stype, val, isPrivate: true);
        return result;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvThiscall) })]
    private static unsafe void HandleOnStatUpdatedIntDetour(IntPtr thisPtr, uint stype, int val)
    {
        var original = (delegate* unmanaged[Thiscall]<IntPtr, uint, int, void>)_originalOnStatUpdatedIntPtr;
        original(thisPtr, stype, val);

        // Cache MaxHealth for every object — used by CombatActionHooks to resolve the
        // absolute target health from the healthRatio in QueryHealthResponse packets.
        if (stype == MaxHealthType && val > 0)
            ObjectQualityCache.SetMaxHealth(thisPtr, unchecked((uint)val));

        if (!SmartBoxLocator.TryGetPlayer(out IntPtr player, out uint playerId, out _))
            return;

        if (player == IntPtr.Zero || thisPtr != player)
            return;

        UpdateCache(playerId, stype, val, isPrivate: false);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe byte HandleSendNoticePlayerDescReceivedDetour(IntPtr playerDescPtr, IntPtr playerModulePtr)
    {
        var original = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte>)_originalSendNoticePlayerDescReceivedPtr;
        byte result = original(playerDescPtr, playerModulePtr);
        SeedSnapshotFromQualities(playerDescPtr);
        ClientObjectHooks.SetKnownPlayerQualitiesPtr(playerDescPtr);
        KnownPlayerQualitiesPtr = playerDescPtr;
        return result;
    }

    private static unsafe void UpdateCacheFromAttribute(uint sender, uint stype, SecondaryAttributeNative* val, bool isPrivate)
    {
        if (val == null)
            return;

        UpdateCache(sender, stype, unchecked((int)val->_currentLevel), isPrivate);

        // For max-vital types (MaxHealth=1, MaxStamina=3, MaxMana=5), _currentLevel is the
        // effective buffed maximum — that's already handled by the UpdateCache call above.
        //
        // For current-vital types (Health=2, Stamina=4, Mana=6), do NOT derive max from
        // _initLevel + _levelFromCp — that's the unbuffed base max and would overwrite the
        // correct buffed value set by the seed or a direct MaxHealth update.
    }

    private static void UpdateCache(uint sender, uint stype, int val, bool isPrivate)
    {
        uint playerId = ClientHelperHooks.GetPlayerId();
        if (playerId == 0)
            return;

        if (!isPrivate && sender != playerId)
            return;

        if (val < 0)
            return;

        uint value = unchecked((uint)val);
        bool changed = false;

        lock (CacheLock)
        {
            PlayerVitalsSnapshot current = _snapshot;
            PlayerVitalsSnapshot updated = stype switch
            {
                MaxHealthType => current with { MaxHealth = value },
                HealthType => current with { Health = value },
                MaxStaminaType => current with { MaxStamina = value },
                StaminaType => current with { Stamina = value },
                MaxManaType => current with { MaxMana = value },
                ManaType => current with { Mana = value },
                _ => current
            };

            // "Highest value seen" — if a current vital exceeds the stored max,
            // bump the max. Over time this converges to the true maximum as the
            // player regenerates.
            if (stype == HealthType && updated.Health > updated.MaxHealth)
                updated = updated with { MaxHealth = updated.Health };
            if (stype == StaminaType && updated.Stamina > updated.MaxStamina)
                updated = updated with { MaxStamina = updated.Stamina };
            if (stype == ManaType && updated.Mana > updated.MaxMana)
                updated = updated with { MaxMana = updated.Mana };

            changed = !EqualityComparer<PlayerVitalsSnapshot>.Default.Equals(current, updated);
            if (changed)
                _snapshot = updated;
        }

        if (changed && _updateLogCount < 0)
        {
            _updateLogCount++;
            string scope = isPrivate ? "private" : $"sender=0x{sender:X8}";
            RynthLog.Compat($"Compat: player vital update #{_updateLogCount} {scope} stype={stype} value={value}");
        }
    }

    private static unsafe void SeedSnapshotFromQualities(IntPtr playerDescPtr)
    {
        if (playerDescPtr == IntPtr.Zero || _inqAttribute2ndStruct == null)
            return;

        if (!TryReadSecondary(playerDescPtr, HealthType, out uint health, out uint maxHealth) &&
            !TryReadSecondary(playerDescPtr, StaminaType, out uint stamina, out uint maxStamina) &&
            !TryReadSecondary(playerDescPtr, ManaType, out uint mana, out uint maxMana))
        {
            return;
        }

        TryReadSecondary(playerDescPtr, HealthType, out health, out maxHealth);
        TryReadSecondary(playerDescPtr, StaminaType, out stamina, out maxStamina);
        TryReadSecondary(playerDescPtr, ManaType, out mana, out maxMana);

        bool changed = false;
        lock (CacheLock)
        {
            PlayerVitalsSnapshot current = _snapshot;
            // Use "highest seen" for max vitals — seed values from InqAttribute2ndStruct
            // return current health as max, so never overwrite a higher known max.
            uint bestMaxHealth = maxHealth != 0 ? Math.Max(maxHealth, current.MaxHealth) : current.MaxHealth;
            uint bestMaxStamina = maxStamina != 0 ? Math.Max(maxStamina, current.MaxStamina) : current.MaxStamina;
            uint bestMaxMana = maxMana != 0 ? Math.Max(maxMana, current.MaxMana) : current.MaxMana;

            // Also ensure current vital doesn't exceed max
            if (health > bestMaxHealth) bestMaxHealth = health;
            if (stamina > bestMaxStamina) bestMaxStamina = stamina;
            if (mana > bestMaxMana) bestMaxMana = mana;

            PlayerVitalsSnapshot updated = current with
            {
                Health = health != 0 ? health : current.Health,
                MaxHealth = bestMaxHealth,
                Stamina = stamina != 0 ? stamina : current.Stamina,
                MaxStamina = bestMaxStamina,
                Mana = mana != 0 ? mana : current.Mana,
                MaxMana = bestMaxMana
            };

            changed = !EqualityComparer<PlayerVitalsSnapshot>.Default.Equals(current, updated);
            if (changed)
                _snapshot = updated;
        }

        if (changed && _seedLogCount < 1)
        {
            _seedLogCount++;
            RynthLog.Compat($"Compat: player vitals seeded from player desc #{_seedLogCount} hp={health}/{maxHealth} st={stamina}/{maxStamina} mn={mana}/{maxMana}");
        }
    }

    private static unsafe bool TryReadSecondary(IntPtr playerDescPtr, uint stype, out uint current, out uint max)
    {
        current = 0;
        max = 0;

        if (_inqAttribute2ndStruct == null)
            return false;

        SecondaryAttributeNative value = default;
        if (_inqAttribute2ndStruct(playerDescPtr, stype, &value) == 0)
            return false;

        current = value._currentLevel;

        // Query the max-vital attribute type (e.g. MaxHealth=1 for Health=2) to get the
        // buffed effective maximum via its _currentLevel. Fall back to the base formula
        // (_initLevel + _levelFromCp) if the max-vital query fails or returns zero.
        uint maxStype = ToMaxStatType(stype);
        if (maxStype != stype)
        {
            SecondaryAttributeNative maxValue = default;
            if (_inqAttribute2ndStruct(playerDescPtr, maxStype, &maxValue) != 0 && maxValue._currentLevel != 0)
            {
                max = maxValue._currentLevel;
                return true;
            }
        }

        max = value._initLevel + value._levelFromCp;
        return current != 0 || max != 0;
    }

    private static uint ToMaxStatType(uint stype)
    {
        return stype switch
        {
            HealthType => MaxHealthType,
            StaminaType => MaxStaminaType,
            ManaType => MaxManaType,
            _ => stype
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeNative
    {
        public IntPtr _packObj;
        public uint _levelFromCp;
        public uint _initLevel;
        public uint _cpSpent;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecondaryAttributeNative
    {
        public AttributeNative _attribute;
        public uint _currentLevel;

        public uint _levelFromCp => _attribute._levelFromCp;
        public uint _initLevel => _attribute._initLevel;
    }

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private unsafe delegate int InqAttribute2ndStructDelegate(IntPtr thisPtr, uint stype, SecondaryAttributeNative* retval);
}
