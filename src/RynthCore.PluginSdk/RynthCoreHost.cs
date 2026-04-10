using System;
using System.Runtime.InteropServices;

namespace RynthCore.PluginSdk;

public readonly unsafe struct RynthCoreHost
{
    public const uint CurrentApiVersion = 32;

    private readonly RynthCoreApiNative _api;

    public RynthCoreHost(RynthCoreApiNative api)
    {
        _api = api;
    }

    public uint Version => _api.Version;
    public IntPtr ImGuiContext => _api.ImGuiContext;
    public IntPtr D3DDevice => _api.D3DDevice;
    public IntPtr GameHwnd => _api.GameHwnd;

    // ─── Has* availability properties ───────────────────────────────────────
    public bool HasProbeClientHooks => _api.ProbeClientHooksFn != IntPtr.Zero;
    public bool HasGetClientHookFlags => _api.GetClientHookFlagsFn != IntPtr.Zero;
    public bool HasChangeCombatMode => _api.ChangeCombatModeFn != IntPtr.Zero;
    public bool HasCancelAttack => _api.CancelAttackFn != IntPtr.Zero;
    public bool HasQueryHealth => _api.QueryHealthFn != IntPtr.Zero;
    public bool HasMeleeAttack => _api.MeleeAttackFn != IntPtr.Zero;
    public bool HasMissileAttack => _api.MissileAttackFn != IntPtr.Zero;
    public bool HasDoMovement => _api.DoMovementFn != IntPtr.Zero;
    public bool HasStopMovement => _api.StopMovementFn != IntPtr.Zero;
    public bool HasJumpNonAutonomous => _api.JumpNonAutonomousFn != IntPtr.Zero;
    public bool HasSetAutonomyLevel => _api.SetAutonomyLevelFn != IntPtr.Zero;
    public bool HasSetAutoRun => _api.SetAutoRunFn != IntPtr.Zero;
    public bool HasTapJump => _api.TapJumpFn != IntPtr.Zero;
    public bool HasSetIncomingChatSuppression => _api.SetIncomingChatSuppressionFn != IntPtr.Zero;
    public bool HasSelectItem => _api.SelectItemFn != IntPtr.Zero;
    public bool HasSetSelectedObjectId => _api.SetSelectedObjectIdFn != IntPtr.Zero;
    public bool HasGetSelectedItemId => _api.GetSelectedItemIdFn != IntPtr.Zero;
    public bool HasGetPreviousSelectedItemId => _api.GetPreviousSelectedItemIdFn != IntPtr.Zero;
    public bool HasGetPlayerId => _api.GetPlayerIdFn != IntPtr.Zero;
    public bool HasGetGroundContainerId => _api.GetGroundContainerIdFn != IntPtr.Zero;
    public bool HasGetCurCoords => _api.GetCurCoordsFn != IntPtr.Zero;
    public bool HasUseObject => _api.UseObjectFn != IntPtr.Zero;
    public bool HasUseObjectOn => _api.UseObjectOnFn != IntPtr.Zero;
    public bool HasUseEquippedItem => _api.UseEquippedItemFn != IntPtr.Zero;
    public bool HasMoveItemExternal => _api.MoveItemExternalFn != IntPtr.Zero;
    public bool HasMoveItemInternal => _api.MoveItemInternalFn != IntPtr.Zero;
    public bool HasSplitStackInternal => _api.SplitStackInternalFn != IntPtr.Zero;
    public bool HasMergeStackInternal => _api.MergeStackInternalFn != IntPtr.Zero;
    public bool HasWriteToChat => _api.WriteToChatFn != IntPtr.Zero;
    public bool HasGetPlayerPose => _api.GetPlayerPoseFn != IntPtr.Zero;
    public bool HasSetMotion => _api.SetMotionFn != IntPtr.Zero;
    public bool HasStopCompletely => _api.StopCompletelyFn != IntPtr.Zero;
    public bool HasTurnToHeading => _api.TurnToHeadingFn != IntPtr.Zero;
    public bool HasGetPlayerHeading => _api.GetPlayerHeadingFn != IntPtr.Zero;
    public bool HasGetObjectName => _api.GetObjectNameFn != IntPtr.Zero;
    public bool HasGetPlayerVitals => _api.GetPlayerVitalsFn != IntPtr.Zero;
    public bool HasGetObjectPosition => _api.GetObjectPositionFn != IntPtr.Zero;
    public bool HasRequestId => _api.RequestIdFn != IntPtr.Zero;
    public bool HasGetTargetVitals => _api.GetTargetVitalsFn != IntPtr.Zero;
    public bool HasCastSpell => _api.CastSpellFn != IntPtr.Zero;
    public bool HasGetItemType => _api.GetItemTypeFn != IntPtr.Zero;
    public bool HasGetObjectIntProperty => _api.GetObjectIntPropertyFn != IntPtr.Zero;
    public bool HasGetObjectBoolProperty => _api.GetObjectBoolPropertyFn != IntPtr.Zero;
    public bool HasObjectIsAttackable => _api.ObjectIsAttackableFn != IntPtr.Zero;
    public bool HasGetObjectSkill => _api.GetObjectSkillFn != IntPtr.Zero;
    public bool HasIsSpellKnown => _api.IsSpellKnownFn != IntPtr.Zero;
    public bool HasReadPlayerEnchantments => _api.ReadPlayerEnchantmentsFn != IntPtr.Zero;
    public bool HasGetServerTime => _api.GetServerTimeFn != IntPtr.Zero;
    public bool HasReadObjectEnchantments => _api.ReadObjectEnchantmentsFn != IntPtr.Zero;
    public bool HasWorldToScreen => _api.WorldToScreenFn != IntPtr.Zero;
    public bool HasGetViewportSize => _api.GetViewportSizeFn != IntPtr.Zero;
    public bool HasNav3D => _api.Nav3DClearFn != IntPtr.Zero && _api.Nav3DAddRingFn != IntPtr.Zero && _api.Nav3DAddLineFn != IntPtr.Zero;
    public bool HasInvokeChatParser => _api.InvokeChatParserFn != IntPtr.Zero;
    public bool HasGetObjectDoubleProperty => _api.GetObjectDoublePropertyFn != IntPtr.Zero;
    public bool HasGetObjectStringProperty => _api.GetObjectStringPropertyFn != IntPtr.Zero;
    public bool HasGetObjectWielderInfo => _api.GetObjectWielderInfoFn != IntPtr.Zero;
    public bool HasNativeAttack => _api.NativeAttackFn != IntPtr.Zero;
    public bool HasIsPlayerReady => _api.IsPlayerReadyFn != IntPtr.Zero;
    public bool HasSetFpsLimit => _api.SetFpsLimitFn != IntPtr.Zero;
    public bool HasGetContainerContents => _api.GetContainerContentsFn != IntPtr.Zero;
    public bool HasGetObjectOwnershipInfo => _api.GetObjectOwnershipInfoFn != IntPtr.Zero;

    // ─── Methods ────────────────────────────────────────────────────────────

    public void Log(string message)
    {
        if (_api.LogFn == IntPtr.Zero || string.IsNullOrEmpty(message))
            return;

        IntPtr buffer = Marshal.StringToHGlobalAnsi(message);
        try
        {
            ((delegate* unmanaged[Cdecl]<IntPtr, void>)_api.LogFn)(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void ProbeClientHooks()
    {
        if (_api.ProbeClientHooksFn == IntPtr.Zero)
            return;

        ((delegate* unmanaged[Cdecl]<void>)_api.ProbeClientHooksFn)();
    }

    public uint GetClientHookFlags()
    {
        return _api.GetClientHookFlagsFn != IntPtr.Zero
            ? ((delegate* unmanaged[Cdecl]<uint>)_api.GetClientHookFlagsFn)()
            : 0;
    }

    public bool ChangeCombatMode(int combatMode)
    {
        return _api.ChangeCombatModeFn != IntPtr.Zero &&
               ((delegate* unmanaged[Cdecl]<int, int>)_api.ChangeCombatModeFn)(combatMode) != 0;
    }

    public bool CancelAttack()
    {
        return _api.CancelAttackFn != IntPtr.Zero &&
               ((delegate* unmanaged[Cdecl]<int>)_api.CancelAttackFn)() != 0;
    }

    public bool QueryHealth(uint targetId)
    {
        return _api.QueryHealthFn != IntPtr.Zero &&
               ((delegate* unmanaged[Cdecl]<uint, int>)_api.QueryHealthFn)(targetId) != 0;
    }

    public bool MeleeAttack(uint targetId, int attackHeight, float powerLevel)
    {
        return _api.MeleeAttackFn != IntPtr.Zero &&
               ((delegate* unmanaged[Cdecl]<uint, int, float, int>)_api.MeleeAttackFn)(targetId, attackHeight, powerLevel) != 0;
    }

    public bool MissileAttack(uint targetId, int attackHeight, float accuracyLevel)
    {
        return _api.MissileAttackFn != IntPtr.Zero &&
               ((delegate* unmanaged[Cdecl]<uint, int, float, int>)_api.MissileAttackFn)(targetId, attackHeight, accuracyLevel) != 0;
    }

    public bool DoMovement(uint motion, float speed, int holdKey)
    {
        return _api.DoMovementFn != IntPtr.Zero &&
               ((delegate* unmanaged[Cdecl]<uint, float, int, int>)_api.DoMovementFn)(motion, speed, holdKey) != 0;
    }

    public bool StopMovement(uint motion, int holdKey)
    {
        return _api.StopMovementFn != IntPtr.Zero &&
               ((delegate* unmanaged[Cdecl]<uint, int, int>)_api.StopMovementFn)(motion, holdKey) != 0;
    }

    public bool JumpNonAutonomous(float extent)
    {
        return _api.JumpNonAutonomousFn != IntPtr.Zero &&
               ((delegate* unmanaged[Cdecl]<float, int>)_api.JumpNonAutonomousFn)(extent) != 0;
    }

    public bool SetAutonomyLevel(uint level)
    {
        return _api.SetAutonomyLevelFn != IntPtr.Zero &&
               ((delegate* unmanaged[Cdecl]<uint, int>)_api.SetAutonomyLevelFn)(level) != 0;
    }

    public bool SetAutoRun(bool enabled)
    {
        return _api.SetAutoRunFn != IntPtr.Zero &&
               ((delegate* unmanaged[Cdecl]<int, int>)_api.SetAutoRunFn)(enabled ? 1 : 0) != 0;
    }

    public bool TapJump()
    {
        return _api.TapJumpFn != IntPtr.Zero &&
               ((delegate* unmanaged[Cdecl]<int>)_api.TapJumpFn)() != 0;
    }

    public bool SetMotion(uint motion, bool enabled)
    {
        return _api.SetMotionFn != IntPtr.Zero &&
               ((delegate* unmanaged[Cdecl]<uint, int, int>)_api.SetMotionFn)(motion, enabled ? 1 : 0) != 0;
    }

    public bool StopCompletely()
    {
        return _api.StopCompletelyFn != IntPtr.Zero &&
               ((delegate* unmanaged[Cdecl]<int>)_api.StopCompletelyFn)() != 0;
    }

    public bool TurnToHeading(float headingDegrees)
    {
        return _api.TurnToHeadingFn != IntPtr.Zero &&
               ((delegate* unmanaged[Cdecl]<float, int>)_api.TurnToHeadingFn)(headingDegrees) != 0;
    }

    public bool TryGetPlayerHeading(out float headingDegrees)
    {
        headingDegrees = 0;

        if (_api.GetPlayerHeadingFn == IntPtr.Zero)
            return false;

        fixed (float* headingPtr = &headingDegrees)
        {
            return ((delegate* unmanaged[Cdecl]<float*, int>)_api.GetPlayerHeadingFn)(headingPtr) != 0;
        }
    }

    public void SetIncomingChatSuppression(bool enabled)
    {
        if (_api.SetIncomingChatSuppressionFn == IntPtr.Zero)
            return;

        ((delegate* unmanaged[Cdecl]<int, void>)_api.SetIncomingChatSuppressionFn)(enabled ? 1 : 0);
    }

    public bool SelectItem(uint objectId)
    {
        return _api.SelectItemFn != IntPtr.Zero &&
               ((delegate* unmanaged[Cdecl]<uint, int>)_api.SelectItemFn)(objectId) != 0;
    }

    public bool SetSelectedObjectId(uint objectId)
    {
        return _api.SetSelectedObjectIdFn != IntPtr.Zero &&
               ((delegate* unmanaged[Cdecl]<uint, int>)_api.SetSelectedObjectIdFn)(objectId) != 0;
    }

    public uint GetSelectedItemId()
    {
        return _api.GetSelectedItemIdFn != IntPtr.Zero
            ? ((delegate* unmanaged[Cdecl]<uint>)_api.GetSelectedItemIdFn)()
            : 0;
    }

    public uint GetPreviousSelectedItemId()
    {
        return _api.GetPreviousSelectedItemIdFn != IntPtr.Zero
            ? ((delegate* unmanaged[Cdecl]<uint>)_api.GetPreviousSelectedItemIdFn)()
            : 0;
    }

    public uint GetPlayerId()
    {
        return _api.GetPlayerIdFn != IntPtr.Zero
            ? ((delegate* unmanaged[Cdecl]<uint>)_api.GetPlayerIdFn)()
            : 0;
    }

    public uint GetGroundContainerId()
    {
        return _api.GetGroundContainerIdFn != IntPtr.Zero
            ? ((delegate* unmanaged[Cdecl]<uint>)_api.GetGroundContainerIdFn)()
            : 0;
    }

    public bool TryGetCurCoords(out double northSouth, out double eastWest)
    {
        northSouth = 0;
        eastWest = 0;

        if (_api.GetCurCoordsFn == IntPtr.Zero)
            return false;

        fixed (double* ns = &northSouth)
        fixed (double* ew = &eastWest)
        {
            return ((delegate* unmanaged[Cdecl]<double*, double*, int>)_api.GetCurCoordsFn)(ns, ew) != 0;
        }
    }

    public bool UseObject(uint objectId)
    {
        return _api.UseObjectFn != IntPtr.Zero &&
               ((delegate* unmanaged[Cdecl]<uint, int>)_api.UseObjectFn)(objectId) != 0;
    }

    public bool UseObjectOn(uint sourceObjectId, uint targetObjectId)
    {
        return _api.UseObjectOnFn != IntPtr.Zero &&
               ((delegate* unmanaged[Cdecl]<uint, uint, int>)_api.UseObjectOnFn)(sourceObjectId, targetObjectId) != 0;
    }

    public bool UseEquippedItem(uint sourceObjectId, uint targetObjectId)
    {
        return _api.UseEquippedItemFn != IntPtr.Zero &&
               ((delegate* unmanaged[Cdecl]<uint, uint, int>)_api.UseEquippedItemFn)(sourceObjectId, targetObjectId) != 0;
    }

    public bool MoveItemExternal(uint objectId, uint targetContainerId, int amount)
    {
        return _api.MoveItemExternalFn != IntPtr.Zero &&
               ((delegate* unmanaged[Cdecl]<uint, uint, int, int>)_api.MoveItemExternalFn)(objectId, targetContainerId, amount) != 0;
    }

    public bool MoveItemInternal(uint objectId, uint targetContainerId, int slot, int amount)
    {
        return _api.MoveItemInternalFn != IntPtr.Zero &&
               ((delegate* unmanaged[Cdecl]<uint, uint, int, int, int>)_api.MoveItemInternalFn)(objectId, targetContainerId, slot, amount) != 0;
    }

    /// <summary>
    /// Move a stack of items onto a specific slot in the target container, merging
    /// with any existing same-type stack at that slot. Unlike <see cref="MoveItemInternal"/>
    /// which sends opcode 0x19 (whole move, first empty slot), this sends opcode 0x55
    /// (StackableSplitToContainer) which honors the slot parameter and merges into
    /// an existing same-type stack at that slot if one exists.
    /// </summary>
    public bool SplitStackInternal(uint objectId, uint targetContainerId, int slot, int amount)
    {
        return _api.SplitStackInternalFn != IntPtr.Zero &&
               ((delegate* unmanaged[Cdecl]<uint, uint, int, int, int>)_api.SplitStackInternalFn)(objectId, targetContainerId, slot, amount) != 0;
    }

    /// <summary>
    /// Merges two stacks of the same item type by sending opcode 0x1A (STACKABLE_MERGE).
    /// This is the real merge path used by the legacy drag-drop UI — opcode 0x55
    /// (the path used by <see cref="SplitStackInternal"/>) creates new stacks at the
    /// target slot instead of merging, so AutoStack must use this entry point.
    /// </summary>
    public bool MergeStackInternal(uint sourceObjectId, uint targetObjectId)
    {
        return _api.MergeStackInternalFn != IntPtr.Zero &&
               ((delegate* unmanaged[Cdecl]<uint, uint, int>)_api.MergeStackInternalFn)(sourceObjectId, targetObjectId) != 0;
    }

    public bool WriteToChat(string text, int chatType)
    {
        if (_api.WriteToChatFn == IntPtr.Zero || string.IsNullOrEmpty(text))
            return false;

        IntPtr textPtr = Marshal.StringToHGlobalUni(text);
        try
        {
            return ((delegate* unmanaged[Cdecl]<IntPtr, int, int>)_api.WriteToChatFn)(textPtr, chatType) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(textPtr);
        }
    }

    public bool TryGetPlayerPose(
        out uint objCellId,
        out float x,
        out float y,
        out float z,
        out float qw,
        out float qx,
        out float qy,
        out float qz)
    {
        objCellId = 0;
        x = y = z = 0;
        qw = 1f;
        qx = qy = qz = 0;

        if (_api.GetPlayerPoseFn == IntPtr.Zero)
            return false;

        fixed (uint* objCellIdPtr = &objCellId)
        fixed (float* xPtr = &x)
        fixed (float* yPtr = &y)
        fixed (float* zPtr = &z)
        fixed (float* qwPtr = &qw)
        fixed (float* qxPtr = &qx)
        fixed (float* qyPtr = &qy)
        fixed (float* qzPtr = &qz)
        {
            return ((delegate* unmanaged[Cdecl]<uint*, float*, float*, float*, float*, float*, float*, float*, int>)_api.GetPlayerPoseFn)(
                objCellIdPtr,
                xPtr,
                yPtr,
                zPtr,
                qwPtr,
                qxPtr,
                qyPtr,
                qzPtr) != 0;
        }
    }

    public bool TryGetObjectName(uint objectId, out string name)
    {
        name = string.Empty;

        if (_api.GetObjectNameFn == IntPtr.Zero)
            return false;

        unsafe
        {
            IntPtr strPtr = ((delegate* unmanaged[Cdecl]<uint, IntPtr>)_api.GetObjectNameFn)(objectId);
            if (strPtr == IntPtr.Zero)
                return false;

            string? str = Marshal.PtrToStringAnsi(strPtr);
            if (str != null)
            {
                name = str;
                return true;
            }
            return false;
        }
    }

    public bool TryGetPlayerVitals(
        out uint health,
        out uint maxHealth,
        out uint stamina,
        out uint maxStamina,
        out uint mana,
        out uint maxMana)
    {
        health = maxHealth = stamina = maxStamina = mana = maxMana = 0;

        if (_api.GetPlayerVitalsFn == IntPtr.Zero)
            return false;

        fixed (uint* healthPtr = &health)
        fixed (uint* maxHealthPtr = &maxHealth)
        fixed (uint* staminaPtr = &stamina)
        fixed (uint* maxStaminaPtr = &maxStamina)
        fixed (uint* manaPtr = &mana)
        fixed (uint* maxManaPtr = &maxMana)
        {
            return ((delegate* unmanaged[Cdecl]<uint*, uint*, uint*, uint*, uint*, uint*, int>)_api.GetPlayerVitalsFn)(
                healthPtr,
                maxHealthPtr,
                staminaPtr,
                maxStaminaPtr,
                manaPtr,
                maxManaPtr) != 0;
        }
    }

    public bool RequestId(uint objectId)
    {
        return _api.RequestIdFn != IntPtr.Zero &&
               ((delegate* unmanaged[Cdecl]<uint, int>)_api.RequestIdFn)(objectId) != 0;
    }

    public bool TryGetTargetVitals(
        uint objectId,
        out uint health,
        out uint maxHealth,
        out uint stamina,
        out uint maxStamina,
        out uint mana,
        out uint maxMana)
    {
        health = maxHealth = stamina = maxStamina = mana = maxMana = 0;

        if (_api.GetTargetVitalsFn == IntPtr.Zero || objectId == 0)
            return false;

        fixed (uint* healthPtr = &health)
        fixed (uint* maxHealthPtr = &maxHealth)
        fixed (uint* staminaPtr = &stamina)
        fixed (uint* maxStaminaPtr = &maxStamina)
        fixed (uint* manaPtr = &mana)
        fixed (uint* maxManaPtr = &maxMana)
        {
            return ((delegate* unmanaged[Cdecl]<uint, uint*, uint*, uint*, uint*, uint*, uint*, int>)_api.GetTargetVitalsFn)(
                objectId,
                healthPtr,
                maxHealthPtr,
                staminaPtr,
                maxStaminaPtr,
                manaPtr,
                maxManaPtr) != 0;
        }
    }

    public bool CastSpell(uint targetId, int spellId)
    {
        return _api.CastSpellFn != IntPtr.Zero &&
               ((delegate* unmanaged[Cdecl]<uint, int, int>)_api.CastSpellFn)(targetId, spellId) != 0;
    }

    public bool TryGetItemType(uint objectId, out uint typeFlags)
    {
        typeFlags = 0;
        if (_api.GetItemTypeFn == IntPtr.Zero)
            return false;

        fixed (uint* typeFlagsPtr = &typeFlags)
        {
            return ((delegate* unmanaged[Cdecl]<uint, uint*, int>)_api.GetItemTypeFn)(objectId, typeFlagsPtr) != 0;
        }
    }

    public bool TryGetObjectIntProperty(uint objectId, uint stype, out int value)
    {
        value = 0;
        if (_api.GetObjectIntPropertyFn == IntPtr.Zero)
            return false;

        fixed (int* valuePtr = &value)
        {
            return ((delegate* unmanaged[Cdecl]<uint, uint, int*, int>)_api.GetObjectIntPropertyFn)(objectId, stype, valuePtr) != 0;
        }
    }

    public bool TryGetObjectBoolProperty(uint objectId, uint stype, out bool value)
    {
        value = false;
        if (_api.GetObjectBoolPropertyFn == IntPtr.Zero)
            return false;

        int raw = 0;
        int result = ((delegate* unmanaged[Cdecl]<uint, uint, int*, int>)_api.GetObjectBoolPropertyFn)(objectId, stype, &raw);
        if (result != 0)
        {
            value = raw != 0;
            return true;
        }
        return false;
    }

    public bool ObjectIsAttackable(uint objectId)
    {
        return _api.ObjectIsAttackableFn != IntPtr.Zero &&
               ((delegate* unmanaged[Cdecl]<uint, int>)_api.ObjectIsAttackableFn)(objectId) != 0;
    }

    public bool TryGetObjectSkill(uint objectId, uint skillStype, out int buffed, out int training)
    {
        buffed = 0;
        training = 0;
        if (_api.GetObjectSkillFn == IntPtr.Zero)
            return false;

        fixed (int* buffedPtr = &buffed)
        fixed (int* trainingPtr = &training)
        {
            return ((delegate* unmanaged[Cdecl]<uint, uint, int*, int*, int>)_api.GetObjectSkillFn)(objectId, skillStype, buffedPtr, trainingPtr) != 0;
        }
    }

    public bool IsSpellKnown(uint objectId, uint spellId, out bool known)
    {
        known = true;
        if (_api.IsSpellKnownFn == IntPtr.Zero)
            return false;

        int result = ((delegate* unmanaged[Cdecl]<uint, uint, int>)_api.IsSpellKnownFn)(objectId, spellId);
        if (result < 0)
            return false;
        known = result != 0;
        return true;
    }

    public bool TryGetObjectPosition(
        uint objectId,
        out uint objCellId,
        out float x,
        out float y,
        out float z)
    {
        objCellId = 0;
        x = y = z = 0;

        if (_api.GetObjectPositionFn == IntPtr.Zero)
            return false;

        fixed (uint* objCellIdPtr = &objCellId)
        fixed (float* xPtr = &x)
        fixed (float* yPtr = &y)
        fixed (float* zPtr = &z)
        {
            return ((delegate* unmanaged[Cdecl]<uint, uint*, float*, float*, float*, int>)_api.GetObjectPositionFn)(
                objectId,
                objCellIdPtr,
                xPtr,
                yPtr,
                zPtr) != 0;
        }
    }

    // ─── Enchantments & server time ─────────────────────────────────────────

    public int ReadPlayerEnchantments(uint[] spellIds, double[] expiryTimes, int maxCount)
    {
        if (_api.ReadPlayerEnchantmentsFn == IntPtr.Zero || spellIds == null || expiryTimes == null || maxCount <= 0)
            return -1;

        IntPtr spellBuf = Marshal.AllocHGlobal(maxCount * sizeof(uint));
        IntPtr expiryBuf = Marshal.AllocHGlobal(maxCount * sizeof(double));
        try
        {
            int result = ((delegate* unmanaged[Cdecl]<uint*, double*, int, int>)_api.ReadPlayerEnchantmentsFn)(
                (uint*)spellBuf, (double*)expiryBuf, maxCount);

            int count = Math.Max(0, Math.Min(result, Math.Min(maxCount, Math.Min(spellIds.Length, expiryTimes.Length))));
            uint* sp = (uint*)spellBuf;
            double* ep = (double*)expiryBuf;
            for (int i = 0; i < count; i++)
            {
                spellIds[i] = sp[i];
                expiryTimes[i] = ep[i];
            }
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(spellBuf);
            Marshal.FreeHGlobal(expiryBuf);
        }
    }

    public double GetServerTime()
    {
        return _api.GetServerTimeFn != IntPtr.Zero
            ? ((delegate* unmanaged[Cdecl]<double>)_api.GetServerTimeFn)()
            : 0;
    }

    public int ReadObjectEnchantments(uint objectId, uint[] spellIds, double[] expiryTimes, int maxCount)
    {
        if (_api.ReadObjectEnchantmentsFn == IntPtr.Zero || spellIds == null || expiryTimes == null || maxCount <= 0)
            return -1;

        IntPtr spellBuf = Marshal.AllocHGlobal(maxCount * sizeof(uint));
        IntPtr expiryBuf = Marshal.AllocHGlobal(maxCount * sizeof(double));
        try
        {
            int result = ((delegate* unmanaged[Cdecl]<uint, uint*, double*, int, int>)_api.ReadObjectEnchantmentsFn)(
                objectId, (uint*)spellBuf, (double*)expiryBuf, maxCount);

            int count = Math.Max(0, Math.Min(result, Math.Min(maxCount, Math.Min(spellIds.Length, expiryTimes.Length))));
            uint* sp = (uint*)spellBuf;
            double* ep = (double*)expiryBuf;
            for (int i = 0; i < count; i++)
            {
                spellIds[i] = sp[i];
                expiryTimes[i] = ep[i];
            }
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(spellBuf);
            Marshal.FreeHGlobal(expiryBuf);
        }
    }

    // ─── World projection & viewport ────────────────────────────────────────

    public bool WorldToScreen(float worldX, float worldY, float worldZ, out float screenX, out float screenY)
    {
        screenX = screenY = 0;
        if (_api.WorldToScreenFn == IntPtr.Zero)
            return false;

        fixed (float* sxPtr = &screenX)
        fixed (float* syPtr = &screenY)
        {
            return ((delegate* unmanaged[Cdecl]<float, float, float, float*, float*, int>)_api.WorldToScreenFn)(
                worldX, worldY, worldZ, sxPtr, syPtr) != 0;
        }
    }

    public bool TryGetViewportSize(out uint width, out uint height)
    {
        width = height = 0;
        if (_api.GetViewportSizeFn == IntPtr.Zero)
            return false;

        fixed (uint* wPtr = &width)
        fixed (uint* hPtr = &height)
        {
            return ((delegate* unmanaged[Cdecl]<uint*, uint*, int>)_api.GetViewportSizeFn)(wPtr, hPtr) != 0;
        }
    }

    // ─── Nav3D markers ──────────────────────────────────────────────────────

    public void Nav3DClear()
    {
        if (_api.Nav3DClearFn == IntPtr.Zero)
            return;
        ((delegate* unmanaged[Cdecl]<void>)_api.Nav3DClearFn)();
    }

    public void Nav3DAddRing(float wx, float wy, float wz, float radius, float thickness, uint colorArgb)
    {
        if (_api.Nav3DAddRingFn == IntPtr.Zero)
            return;
        ((delegate* unmanaged[Cdecl]<float, float, float, float, float, uint, void>)_api.Nav3DAddRingFn)(
            wx, wy, wz, radius, thickness, colorArgb);
    }

    public void Nav3DAddLine(float x1, float y1, float z1, float x2, float y2, float z2, float thickness, uint colorArgb)
    {
        if (_api.Nav3DAddLineFn == IntPtr.Zero)
            return;
        ((delegate* unmanaged[Cdecl]<float, float, float, float, float, float, float, uint, void>)_api.Nav3DAddLineFn)(
            x1, y1, z1, x2, y2, z2, thickness, colorArgb);
    }

    // ─── Chat parser ────────────────────────────────────────────────────────

    public bool InvokeChatParser(string text)
    {
        if (_api.InvokeChatParserFn == IntPtr.Zero || string.IsNullOrEmpty(text))
            return false;

        IntPtr textPtr = Marshal.StringToHGlobalUni(text);
        try
        {
            return ((delegate* unmanaged[Cdecl]<IntPtr, int>)_api.InvokeChatParserFn)(textPtr) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(textPtr);
        }
    }

    // ─── Extended object properties ─────────────────────────────────────────

    public bool TryGetObjectDoubleProperty(uint objectId, uint stype, out double value)
    {
        value = 0;
        if (_api.GetObjectDoublePropertyFn == IntPtr.Zero)
            return false;

        fixed (double* valuePtr = &value)
        {
            return ((delegate* unmanaged[Cdecl]<uint, uint, double*, int>)_api.GetObjectDoublePropertyFn)(objectId, stype, valuePtr) != 0;
        }
    }

    public bool TryGetObjectStringProperty(uint objectId, uint stype, out string value)
    {
        value = string.Empty;
        if (_api.GetObjectStringPropertyFn == IntPtr.Zero)
            return false;

        IntPtr strPtr = ((delegate* unmanaged[Cdecl]<uint, uint, IntPtr>)_api.GetObjectStringPropertyFn)(objectId, stype);
        if (strPtr == IntPtr.Zero)
            return false;

        string? str = Marshal.PtrToStringAnsi(strPtr);
        if (str != null)
        {
            value = str;
            return true;
        }
        return false;
    }

    public bool TryGetObjectWielderInfo(uint objectId, out uint wielderID, out uint location)
    {
        wielderID = 0;
        location = 0;
        if (_api.GetObjectWielderInfoFn == IntPtr.Zero)
            return false;

        fixed (uint* wPtr = &wielderID)
        fixed (uint* lPtr = &location)
        {
            return ((delegate* unmanaged[Cdecl]<uint, uint*, uint*, int>)_api.GetObjectWielderInfoFn)(objectId, wPtr, lPtr) != 0;
        }
    }

    // ─── Combat helpers ─────────────────────────────────────────────────────

    public bool NativeAttack(int attackHeight, float power)
    {
        return _api.NativeAttackFn != IntPtr.Zero &&
               ((delegate* unmanaged[Cdecl]<int, float, int>)_api.NativeAttackFn)(attackHeight, power) != 0;
    }

    public bool IsPlayerReady()
    {
        return _api.IsPlayerReadyFn != IntPtr.Zero &&
               ((delegate* unmanaged[Cdecl]<int>)_api.IsPlayerReadyFn)() != 0;
    }

    // ─── FPS limiter ────────────────────────────────────────────────────────

    public void SetFpsLimit(bool enabled, int focusedFps, int backgroundFps)
    {
        if (_api.SetFpsLimitFn == IntPtr.Zero)
            return;
        ((delegate* unmanaged[Cdecl]<int, int, int, void>)_api.SetFpsLimitFn)(enabled ? 1 : 0, focusedFps, backgroundFps);
    }

    // ─── Container / ownership ──────────────────────────────────────────────

    public int GetContainerContents(uint containerId, uint[] output)
    {
        if (_api.GetContainerContentsFn == IntPtr.Zero || containerId == 0 || output == null || output.Length == 0)
            return 0;

        IntPtr nativeBuf = Marshal.AllocHGlobal(output.Length * sizeof(uint));
        try
        {
            int result = ((delegate* unmanaged[Cdecl]<uint, uint*, int, int>)_api.GetContainerContentsFn)(
                containerId, (uint*)nativeBuf, output.Length);

            int count = Math.Min(result, output.Length);
            uint* p = (uint*)nativeBuf;
            for (int i = 0; i < count; i++)
                output[i] = p[i];
            return count;
        }
        finally
        {
            Marshal.FreeHGlobal(nativeBuf);
        }
    }

    public bool TryGetObjectOwnershipInfo(uint objectId, out uint containerID, out uint wielderID, out uint location)
    {
        containerID = 0;
        wielderID = 0;
        location = 0;
        if (_api.GetObjectOwnershipInfoFn == IntPtr.Zero)
            return false;

        fixed (uint* cPtr = &containerID)
        fixed (uint* wPtr = &wielderID)
        fixed (uint* lPtr = &location)
        {
            return ((delegate* unmanaged[Cdecl]<uint, uint*, uint*, uint*, int>)_api.GetObjectOwnershipInfoFn)(
                objectId, cPtr, wPtr, lPtr) != 0;
        }
    }
}
