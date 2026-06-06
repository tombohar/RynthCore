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

    /// <summary>
    /// Drops the cached qualities ptr on logout so the next buffed-max inq
    /// call can't dereference a freed allocation. Called by PluginManager's
    /// logout dispatch.
    /// </summary>
    internal static void ResetSession()
    {
        KnownPlayerQualitiesPtr = IntPtr.Zero;
    }

    /// <summary>
    /// Re-seed the qualities ptr and the vital snapshot from the live player object
    /// without waiting for SendNoticePlayerDescReceived. Used by the engine's
    /// hot-reload path: after a reload the new engine's static state is fresh,
    /// so KnownPlayerQualitiesPtr starts at zero and AC won't fire the
    /// notification again until the player relogs. Returns true if a non-zero
    /// qualities ptr was found and applied.
    /// </summary>
    public static bool TryReseedFromCurrentPlayer()
    {
        if (!ClientObjectHooks.TryGetPlayerQualitiesPtr(out IntPtr qualitiesPtr) || qualitiesPtr == IntPtr.Zero)
            return false;

        KnownPlayerQualitiesPtr = qualitiesPtr;
        ClientObjectHooks.SetKnownPlayerQualitiesPtr(qualitiesPtr);

        try
        {
            // Struct path first — gives us current health/stamina/mana plus
            // a baseline max (treated as a floor, not the truth).
            SeedSnapshotFromQualities(qualitiesPtr);
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"PlayerVitalsHooks: TryReseedFromCurrentPlayer struct seed threw {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            // Buffed-uint path — what AC actually shows on the vital bars.
            // This OVERRIDES the max values from the struct path.
            ReseedBuffedMaxFromQualities(qualitiesPtr);
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"PlayerVitalsHooks: TryReseedFromCurrentPlayer buffed-max threw {ex.GetType().Name}: {ex.Message}");
        }

        return true;
    }

    private static unsafe void ReseedBuffedMaxFromQualities(IntPtr qualitiesPtr)
    {
        if (qualitiesPtr == IntPtr.Zero || _inqAttribute2ndUint == null)
            return;

        if (!TryInqUint(qualitiesPtr, MaxHealthType, out uint maxHealth) ||
            !TryInqUint(qualitiesPtr, MaxStaminaType, out uint maxStamina) ||
            !TryInqUint(qualitiesPtr, MaxManaType, out uint maxMana))
        {
            return;
        }

        bool changed = false;
        lock (CacheLock)
        {
            PlayerVitalsSnapshot current = _snapshot;
            PlayerVitalsSnapshot updated = current with
            {
                MaxHealth = maxHealth != 0 ? maxHealth : current.MaxHealth,
                MaxStamina = maxStamina != 0 ? maxStamina : current.MaxStamina,
                MaxMana = maxMana != 0 ? maxMana : current.MaxMana,
            };
            if (!EqualityComparer<PlayerVitalsSnapshot>.Default.Equals(current, updated))
            {
                _snapshot = updated;
                changed = true;
            }
        }

        if (changed)
            RynthLog.Compat($"Compat: player vitals buffed-max re-seed hp_max={maxHealth} st_max={maxStamina} mn_max={maxMana}");
    }

    private static unsafe bool TryInqUint(IntPtr qualitiesPtr, uint stype, out uint value)
    {
        value = 0;
        try
        {
            uint result = 0;
            // raw=0 → buffed effective (the same number AC renders on the bars).
            int rc = _inqAttribute2ndUint!(qualitiesPtr, stype, &result, 0);
            if (rc == 0) return false;
            value = result;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private const int UpdateAttribute2ndVa = 0x00559900;
    private const int UpdateAttribute2ndLevelVa = 0x00559920;
    private const int PrivateUpdateAttribute2ndVa = 0x00559B20;
    private const int PrivateUpdateAttribute2ndLevelVa = 0x00559B50;
    private const int OnStatUpdatedIntVa = 0x0058ED50;
    private const int InqAttribute2ndStructVa = 0x005927F0;
    private const int SendNoticePlayerDescReceivedVa = 0x0047A200;
    private const int MaxUpdateLogs = 18;

    // ── Pattern-resolved binding (1a hardening, 2026-06-05) ─────────────
    // *Va consts above are FALLBACKs; signatures verified unique + landing at the VA offline
    // (tools/pe_pattern.py). The 4 Update*Attribute2nd thunks are near-identical templates
    // differing only in their call target, so they use LITERAL patterns (trailing byte pins
    // the rel32 low byte to stay unique); the rest are wildcarded (null = rel32 operand).
    private static readonly byte?[] PatUpdateAttribute2nd = [ 0x8B, 0x44, 0x24, 0x10, 0x8B, 0x54, 0x24, 0x08, 0x50, 0x8B, 0x44, 0x24, 0x08, 0x52, 0x8B, 0x54, 0x24, 0x14, 0x50, 0x52, 0xE8, 0xA7 ];
    private static readonly byte?[] PatUpdateAttribute2ndLevel = [ 0x8B, 0x44, 0x24, 0x10, 0x8B, 0x54, 0x24, 0x08, 0x50, 0x8B, 0x44, 0x24, 0x08, 0x52, 0x8B, 0x54, 0x24, 0x14, 0x50, 0x52, 0xE8, 0x07, 0xF1 ];
    private static readonly byte?[] PatPrivateUpdateAttribute2nd = [ 0xA1, 0x58, 0xDA, 0x83, 0x00, 0x85, 0xC0, 0x74, 0x08, 0x8B, 0x80, 0xF4, 0x00, 0x00, 0x00, 0xEB, 0x02, 0x33, 0xC0, 0x8B, 0x54, 0x24, 0x0C, 0x52, 0x8B, 0x54, 0x24, 0x0C, 0x50, 0x8B, 0x44, 0x24, 0x0C, 0x50, 0x52, 0xE8, 0x78 ];
    private static readonly byte?[] PatPrivateUpdateAttribute2ndLevel = [ 0xA1, 0x58, 0xDA, 0x83, 0x00, 0x85, 0xC0, 0x74, 0x08, 0x8B, 0x80, 0xF4, 0x00, 0x00, 0x00, 0xEB, 0x02, 0x33, 0xC0, 0x8B, 0x54, 0x24, 0x0C, 0x52, 0x8B, 0x54, 0x24, 0x0C, 0x50, 0x8B, 0x44, 0x24, 0x0C, 0x50, 0x52, 0xE8, 0xC8 ];
    private static readonly byte?[] PatOnStatUpdatedInt = [ 0x8B, 0x44, 0x24, 0x04, 0x48, 0x3D, 0x97, 0x00 ];
    private static readonly byte?[] PatInqAttribute2ndStruct = [ 0x8B, 0x49, 0x60, 0x85, 0xC9, 0x74, 0x13, 0x8B, 0x44, 0x24, 0x08, 0x8B, 0x54, 0x24, 0x04, 0x50, 0x52, 0xE8, null, null, null, null, 0x85, 0xC0, 0x75, 0x05, 0x33, 0xC0, 0xC2, 0x08, 0x00, 0xB8, 0x01, 0x00, 0x00, 0x00, 0xC2, 0x08, 0x00, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x68 ];
    private static readonly byte?[] PatSendNoticePlayerDescReceived = [ 0xE8, null, null, null, null, 0x8B, 0x10, 0x68, 0xF0 ];
    private static readonly byte?[] PatInqAttribute2ndUint = [ 0x51, 0x53, 0x55, 0x57, 0x8B, 0x7C, 0x24, 0x14 ];

    private static IntPtr Resolve(AcClientTextSection text, string name, byte?[] pattern, int fallbackVa)
    {
        HookResolver.ResolveResult r = HookResolver.Resolve(text, name, pattern, fallbackVa);
        return r.Success ? r.Address : IntPtr.Zero;
    }

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

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection text))
        {
            _statusMessage = "acclient.exe not available.";
            return;
        }

        try
        {
            IntPtr updateAttributePtr = Resolve(text, "PlayerVitals.UpdateAttribute2nd", PatUpdateAttribute2nd, UpdateAttribute2ndVa);
            IntPtr updatePtr = Resolve(text, "PlayerVitals.UpdateAttribute2ndLevel", PatUpdateAttribute2ndLevel, UpdateAttribute2ndLevelVa);
            IntPtr privateUpdateAttributePtr = Resolve(text, "PlayerVitals.PrivateUpdateAttribute2nd", PatPrivateUpdateAttribute2nd, PrivateUpdateAttribute2ndVa);
            IntPtr privateUpdatePtr = Resolve(text, "PlayerVitals.PrivateUpdateAttribute2ndLevel", PatPrivateUpdateAttribute2ndLevel, PrivateUpdateAttribute2ndLevelVa);
            IntPtr onStatUpdatedIntPtr = Resolve(text, "PlayerVitals.OnStatUpdatedInt", PatOnStatUpdatedInt, OnStatUpdatedIntVa);
            IntPtr inqAttribute2ndStructPtr = Resolve(text, "PlayerVitals.InqAttribute2nd_struct", PatInqAttribute2ndStruct, InqAttribute2ndStructVa);
            IntPtr sendNoticePlayerDescReceivedPtr = Resolve(text, "PlayerVitals.SendNotice_PlayerDescReceived", PatSendNoticePlayerDescReceived, SendNoticePlayerDescReceivedVa);

            if (updateAttributePtr == IntPtr.Zero || updatePtr == IntPtr.Zero ||
                privateUpdateAttributePtr == IntPtr.Zero || privateUpdatePtr == IntPtr.Zero ||
                onStatUpdatedIntPtr == IntPtr.Zero || inqAttribute2ndStructPtr == IntPtr.Zero ||
                sendNoticePlayerDescReceivedPtr == IntPtr.Zero)
            {
                _statusMessage = "One or more Attribute2nd handlers failed to resolve (pattern + fallback VA both missed).";
                RynthLog.Compat($"Compat: player vitals hooks failed - {_statusMessage}");
                return;
            }

            _inqAttribute2ndStruct = Marshal.GetDelegateForFunctionPointer<InqAttribute2ndStructDelegate>(inqAttribute2ndStructPtr);

            // Buffed-uint overload — used by hot-reload re-seed and any path
            // that needs the same number AC's vital bars render. The struct
            // overload returns base values; this one returns buffed.
            IntPtr inqAttribute2ndUintPtr = Resolve(text, "PlayerVitals.InqAttribute2nd_uint", PatInqAttribute2ndUint, InqAttribute2ndUintVa);
            if (inqAttribute2ndUintPtr != IntPtr.Zero)
                _inqAttribute2ndUint = Marshal.GetDelegateForFunctionPointer<InqAttribute2ndUintDelegate>(inqAttribute2ndUintPtr);

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
            RynthLog.Verbose($"Compat: player vitals hooks ready - update=0x{UpdateAttribute2ndVa:X8}, level=0x{UpdateAttribute2ndLevelVa:X8}, private=0x{PrivateUpdateAttribute2ndVa:X8}, privateLevel=0x{PrivateUpdateAttribute2ndLevelVa:X8}, stat=0x{OnStatUpdatedIntVa:X8}, playerDesc=0x{SendNoticePlayerDescReceivedVa:X8}");
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
            RynthLog.Verbose($"Compat: player max vitals from identify hp={maxHealth} st={maxStamina} mn={maxMana}");
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
                RynthLog.Verbose($"Compat: player MaxHealth derived from ratio={healthRatio:0.000} hp={_snapshot.Health} → max={derivedMax}");
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
            RynthLog.Verbose($"Compat: player vital update #{_updateLogCount} {scope} stype={stype} value={value}");
        }

        // Diagnostic: log every max-vital update so we can see whether the
        // UpdateAttribute2nd hook is actually firing for stype 1/3/5 after a
        // hot reload, vs being silenced. Cheap (only fires on max changes).
        if (changed && (stype == MaxHealthType || stype == MaxStaminaType || stype == MaxManaType))
        {
            RynthLog.Compat($"PlayerVitals: max update stype={stype} value={value} private={isPrivate}");
        }

        // Refresh MaxHealth from the live buffed-uint inq. Runs from the
        // detour context (game thread, AC just finished an event), so the
        // nested-pointer derefs inside InqAttribute2nd are safe. Catches
        // god-mode / enchantment-drop cases where the buffed effective max
        // changes without a server-pushed UpdateAttribute2nd(MaxHealth) event.
        TryRefreshLiveMaxVitals();
    }

    // Called from every UpdateCache() invocation so buff/vitae changes that don't
    // generate an explicit UpdateAttribute2nd packet are still picked up promptly.
    private static void TryRefreshLiveMaxVitals()
    {
        if (_inqAttribute2ndUint == null) return;
        IntPtr qualities = KnownPlayerQualitiesPtr;
        if (qualities == IntPtr.Zero) return;
        if (!LoginLifecycleHooks.HasObservedLoginComplete) return;
        if (!ClientObjectHooks.IsReadablePointer(qualities)) return;

        TryInqUint(qualities, MaxHealthType,   out uint liveHp);
        TryInqUint(qualities, MaxStaminaType,  out uint liveSt);
        TryInqUint(qualities, MaxManaType,     out uint liveMn);

        lock (CacheLock)
        {
            PlayerVitalsSnapshot s = _snapshot;
            if (liveHp != 0 && s.MaxHealth  != liveHp) s = s with { MaxHealth  = liveHp };
            if (liveSt != 0 && s.MaxStamina != liveSt) s = s with { MaxStamina = liveSt };
            if (liveMn != 0 && s.MaxMana    != liveMn) s = s with { MaxMana    = liveMn };
            _snapshot = s;
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
            RynthLog.Verbose($"Compat: player vitals seeded from player desc #{_seedLogCount} hp={health}/{maxHealth} st={stamina}/{maxStamina} mn={mana}/{maxMana}");
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

    /// <summary>
    /// CACQualities::InqAttribute2nd uint overload at 0x00592D20.
    /// raw=0 → returns buffed effective value (initLevel + levelFromCp +
    /// endurance contribution + EnchantAttribute2nd buffs). raw=1 → unbuffed.
    /// This is the function AC itself uses to render the vital bars.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private unsafe delegate int InqAttribute2ndUintDelegate(IntPtr thisPtr, uint stype, uint* retval, int raw);

    private const int InqAttribute2ndUintVa = 0x00592D20;
    private static InqAttribute2ndUintDelegate? _inqAttribute2ndUint;
}
