using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace RynthCore.PluginSdk;

public readonly unsafe struct RynthCoreHost
{
    /// <summary>
    /// The engine API this SDK's table (<see cref="RynthCoreApiNative"/>) covers. It is the engine's
    /// own constant, compiled in from src\RynthCore.Engine\Plugins\PluginContractVersion.cs, so the
    /// two can't drift (this said 68 for eight versions); tools\PluginManifestTests checks the
    /// SDK's table really reaches it.
    /// </summary>
    public const uint CurrentApiVersion = RynthCore.Engine.Plugins.PluginContractVersion.Current;

    /// <summary>
    /// The oldest engine API a plugin built on this SDK loads on by default
    /// (<see cref="RynthCore.PluginCore.RynthPluginBase.MinimumApiVersion"/>). Players get
    /// plugin updates automatically but engine updates only when they click, so defaulting
    /// to <see cref="CurrentApiVersion"/> made every rebuilt plugin refuse the engine most
    /// players still run. Calls newer than this are version-checked by their wrappers
    /// (e.g. HasVendorTrade); a plugin that truly needs a newer engine overrides
    /// MinimumApiVersion. Raise this only with a release that forces the engine update.
    /// </summary>
    public const uint BaselineApiVersion = 66;

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
    public bool HasGetNumContainedItems => _api.GetNumContainedItemsFn != IntPtr.Zero;
    public bool HasGetNumContainedContainers => _api.GetNumContainedContainersFn != IntPtr.Zero;
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
    public bool HasIsPortaling      => _api.IsPortalingFn      != IntPtr.Zero;
    public bool HasGetVitae         => _api.GetVitaeFn         != IntPtr.Zero;
    public bool HasGetAccountName   => _api.GetAccountNameFn   != IntPtr.Zero;
    public bool HasGetWorldName     => _api.GetWorldNameFn     != IntPtr.Zero;
    public bool HasGetObjectWcid       => _api.GetObjectWcidFn       != IntPtr.Zero;
    public bool HasHasAppraisalData    => _api.HasAppraisalDataFn    != IntPtr.Zero;
    public bool HasGetLastIdTime       => _api.GetLastIdTimeFn       != IntPtr.Zero;
    public bool HasGetObjectHeading    => _api.GetObjectHeadingFn    != IntPtr.Zero;
    public bool HasGetBusyState        => _api.GetBusyStateFn        != IntPtr.Zero;
    public bool HasForceResetBusyCount => _api.ForceResetBusyCountFn != IntPtr.Zero;
    public bool HasGetCastBusyState    => _api.GetCastBusyStateFn    != IntPtr.Zero;
    public bool HasGetObjectSpellIds   => _api.GetObjectSpellIdsFn   != IntPtr.Zero;
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
    public bool HasReadKnownSpells => _api.ReadKnownSpellsFn != IntPtr.Zero;
    public bool HasGetServerTime => _api.GetServerTimeFn != IntPtr.Zero;
    public bool HasReadObjectEnchantments => _api.ReadObjectEnchantmentsFn != IntPtr.Zero;
    public bool HasWorldToScreen => _api.WorldToScreenFn != IntPtr.Zero;
    public bool HasGetViewportSize => _api.GetViewportSizeFn != IntPtr.Zero;
    public bool HasNav3D => _api.Nav3DClearFn != IntPtr.Zero && _api.Nav3DAddRingFn != IntPtr.Zero && _api.Nav3DAddLineFn != IntPtr.Zero;
    public bool HasNav3DTriangle => _api.Nav3DAddTriangleFn != IntPtr.Zero;
    public bool HasNav3DRingHeight => _api.Nav3DAddRingExFn != IntPtr.Zero;
    public bool HasInvokeChatParser => _api.InvokeChatParserFn != IntPtr.Zero;
    public bool HasGetObjectDoubleProperty => _api.GetObjectDoublePropertyFn != IntPtr.Zero;
    public bool HasGetObjectQuadProperty => _api.GetObjectQuadPropertyFn != IntPtr.Zero;
    public bool HasGetObjectAttribute2ndBaseLevel => _api.GetObjectAttribute2ndBaseLevelFn != IntPtr.Zero;
    public bool HasGetPlayerBaseVitals => _api.GetPlayerBaseVitalsFn != IntPtr.Zero;
    public bool HasGetObjectStringProperty => _api.GetObjectStringPropertyFn != IntPtr.Zero;
    public bool HasGetObjectWielderInfo => _api.GetObjectWielderInfoFn != IntPtr.Zero;
    public bool HasNativeAttack => _api.NativeAttackFn != IntPtr.Zero;
    public bool HasIsPlayerReady => _api.IsPlayerReadyFn != IntPtr.Zero;
    public bool HasSetFpsLimit => _api.SetFpsLimitFn != IntPtr.Zero;
    public bool HasGetContainerContents => _api.GetContainerContentsFn != IntPtr.Zero;
    public bool HasGetObjectOwnershipInfo => _api.GetObjectOwnershipInfoFn != IntPtr.Zero;
    public bool HasGetCurrentCombatMode => _api.GetCurrentCombatModeFn != IntPtr.Zero;
    public bool HasSalvagePanel => _api.SalvagePanelOpenFn != IntPtr.Zero && _api.SalvagePanelAddItemFn != IntPtr.Zero && _api.SalvagePanelExecuteFn != IntPtr.Zero;
    public bool HasGetObjectPalettes => _api.Version >= 50 && _api.GetObjectPalettesFn != IntPtr.Zero;
    public bool HasCommenceJump => _api.Version >= 51 && _api.CommenceJumpFn != IntPtr.Zero;
    public bool HasDoJump => _api.Version >= 51 && _api.DoJumpFn != IntPtr.Zero;
    public bool HasLaunchJumpWithMotion => _api.Version >= 52 && _api.LaunchJumpWithMotionFn != IntPtr.Zero;
    public bool HasGetRadarRect => _api.Version >= 53 && _api.GetRadarRectFn != IntPtr.Zero;
    public bool HasSetRadarSuppressed    => _api.Version >= 54 && _api.SetRadarSuppressedFn    != IntPtr.Zero;
    public bool HasSetChatSuppressed     => _api.Version >= 55 && _api.SetChatSuppressedFn     != IntPtr.Zero;
    public bool HasSetPowerbarSuppressed => _api.Version >= 56 && _api.SetPowerbarSuppressedFn != IntPtr.Zero;
    public bool HasGiveObjectTo => _api.Version >= 62 && _api.GiveObjectToFn != IntPtr.Zero;
    public bool HasGetEngineStatusJson   => _api.Version >= 64 && _api.GetEngineStatusJsonFn   != IntPtr.Zero;
    public bool HasGetPluginSnapshotJson => _api.Version >= 64 && _api.GetPluginSnapshotJsonFn != IntPtr.Zero;
    public bool HasSendPluginCommand     => _api.Version >= 64 && _api.SendPluginCommandFn     != IntPtr.Zero;
    public bool HasGetObjectDataIdProperty => _api.Version >= 65 && _api.GetObjectDataIdPropertyFn != IntPtr.Zero;
    /// <summary>v73: <see cref="TryGetObjectInstanceIdProperty"/> (any PropertyInstanceId).</summary>
    public bool HasGetObjectInstanceIdProperty => _api.Version >= 73 && _api.GetObjectInstanceIdPropertyFn != IntPtr.Zero;
    /// <summary>v74: <see cref="TryGetVTankState"/> (VTank's macro under the Decal bridge).</summary>
    public bool HasGetVTankState => _api.Version >= 74 && _api.GetVTankStateFn != IntPtr.Zero;
    /// <summary>v75: <see cref="TryGetCharacterTitles"/> (the titles the player holds).</summary>
    public bool HasGetCharacterTitles => _api.Version >= 75 && _api.GetCharacterTitlesFn != IntPtr.Zero;
    /// <summary>v75: <see cref="TryGetServerInfo"/> (which server this is).</summary>
    public bool HasGetServerInfo => _api.Version >= 75 && _api.GetServerInfoFn != IntPtr.Zero;
    /// <summary>v86: <see cref="TryGetServerStateJson"/> (the server list for every plugin).</summary>
    public bool HasGetServerStateJson => _api.Version >= 86 && _api.GetServerStateJsonFn != IntPtr.Zero;
    /// <summary>v87: <see cref="TryGetWieldedItems"/> (what the player wields, from the server's messages).</summary>
    public bool HasGetWieldedItems => _api.Version >= 87 && _api.GetWieldedItemsFn != IntPtr.Zero;
    /// <summary>v89: <see cref="GetUiColor"/> and <see cref="GetUiFont"/> (the engine's look, for a plugin's own windows).</summary>
    public bool HasUiTheme => _api.Version >= 89 && _api.GetUiColorFn != IntPtr.Zero && _api.GetUiFontFn != IntPtr.Zero;
    /// <summary>v89: <see cref="SetPanelOpen"/> (open, close or toggle a panel by its key, a plugin window's title included).</summary>
    public bool HasSetPanelOpen => _api.Version >= 89 && _api.SetPanelOpenFn != IntPtr.Zero;
    /// <summary>v90: windows that draw their own frame: the manifest's "chrome": "none" and "fitHeight",
    /// <see cref="PanelDragWithLastItem"/>, <see cref="SetPanelPopOut"/>, <see cref="PanelTextSize"/>, and the
    /// v90 colours (Muted, KeyFace, Hairline, Inset, Faint) and fonts (Dash*, Narrow*).</summary>
    public bool HasFramelessWindows => _api.Version >= 90 && _api.PanelDragWithLastItemFn != IntPtr.Zero
        && _api.SetPanelPopOutFn != IntPtr.Zero && _api.PanelTextSizeFn != IntPtr.Zero;
    /// <summary>v90: <see cref="RenderSettingsDrawer"/> (the dashboard drawers that show Settings rows).</summary>
    public bool HasSettingsDrawers => _api.Version >= 90 && _api.RenderSettingsDrawerFn != IntPtr.Zero;
    /// <summary>v90: the player's AC shortcut slots (<see cref="GetShortcuts"/>, <see cref="ShortcutAction"/>),
    /// AC's key bindings (<see cref="GetActionKeys"/>) and item pictures (<see cref="GetIconTexture"/>).</summary>
    public bool HasShortcuts => _api.Version >= 90 && _api.GetShortcutsFn != IntPtr.Zero && _api.ShortcutActionFn != IntPtr.Zero
        && _api.GetActionKeysFn != IntPtr.Zero && _api.GetIconTextureFn != IntPtr.Zero;
    /// <summary>v76: <see cref="CloseContainer"/> (close a corpse or chest like the client's window close).</summary>
    public bool HasCloseContainer => _api.Version >= 76 && _api.CloseContainerFn != IntPtr.Zero;
    /// <summary>v77: <see cref="GetScreenMode"/>, <see cref="GetUiHookFlags"/> and the UI callbacks
    /// (OnScreenChanged, OnClientCleanup, OnTooltipShow/Hide, OnDragStart, OnItemDropped).</summary>
    public bool HasUiHooks => _api.Version >= 77 && _api.GetScreenModeFn != IntPtr.Zero && _api.GetUiHookFlagsFn != IntPtr.Zero;

    /// <summary>v78: <see cref="SetServerMessageInterest"/> (reassembled server messages on RynthPluginOnServerMessage).</summary>
    public bool HasServerMessages => _api.Version >= 78 && _api.SetServerMessageInterestFn != IntPtr.Zero;

    /// <summary>v79: <see cref="TryGetTrainingInfo"/>, <see cref="Raise"/> and <see cref="TrainSkill"/>
    /// (spend unassigned XP and skill credits like the character window).</summary>
    public bool HasTraining => _api.Version >= 79 && _api.GetTrainingInfoFn != IntPtr.Zero
                               && _api.RaiseFn != IntPtr.Zero && _api.TrainSkillFn != IntPtr.Zero;

    /// <summary>v80: <see cref="TryGetContainerOrder"/> (a container's items in real slot order, and the
    /// side packs' order in the pack column).</summary>
    public bool HasContainerOrder => _api.Version >= 80 && _api.GetContainerOrderFn != IntPtr.Zero;

    /// <summary>v80: <see cref="PutItemInContainer"/> (a move that lands at a slot; a reorder within a
    /// pack). The sender can still be unbound on an odd client: the call then answers -1.</summary>
    public bool HasPutItemInContainer => _api.Version >= 80 && _api.PutItemInContainerFn != IntPtr.Zero;

    /// <summary>v82: <see cref="SalvageDirect"/> (salvage without opening AC's salvage panel). The sender
    /// can still be unbound on an odd client: the call then answers -1.</summary>
    public bool HasSalvageDirect => _api.Version >= 82 && _api.SalvageDirectFn != IntPtr.Zero;

    /// <summary>v82: most ids one <see cref="SalvageDirect"/> request takes (the engine refuses more).</summary>
    public const int MaxSalvageDirectItems = 100;

    /// <summary>v83: <see cref="GetObjectServerAge"/> (when the server last talked about an object).</summary>
    public bool HasGetObjectServerAge => _api.Version >= 83 && _api.GetObjectServerAgeFn != IntPtr.Zero;

    /// <summary>v83: <see cref="RemoveClientObject"/> (drop a ghost creature from the client).</summary>
    public bool HasRemoveClientObject => _api.Version >= 83 && _api.RemoveClientObjectFn != IntPtr.Zero;

    /// <summary>v84: <see cref="TryGetObjectProperties"/> (every property of an object at once).</summary>
    public bool HasGetObjectProperties => _api.Version >= 84 && _api.GetObjectPropertiesFn != IntPtr.Zero;
    public bool HasGetPluginExportJson     => _api.Version >= 66 && _api.GetPluginExportJsonFn     != IntPtr.Zero;
    // Version-checked first: on an older engine the field lies past the end of its API table.
    public bool HasGetMergeStackResult     => _api.Version >= 81 && _api.GetMergeStackResultFn     != IntPtr.Zero;
    /// <summary>v85: <see cref="ArmConfirmation"/> and <see cref="TryGetLastConfirmation"/>.</summary>
    public bool HasConfirmationAnswers     => _api.Version >= 85 && _api.ArmConfirmationFn != IntPtr.Zero && _api.GetLastConfirmationFn != IntPtr.Zero;
    /// <summary>v97: <see cref="ArmConfirmationLeaveBelow"/> (a sticky arm that leaves prompts below its minimum to the client).</summary>
    public bool HasConfirmationLeaveBelow  => HasConfirmationAnswers && _api.Version >= 97;
    public bool HasGetPluginInterface      => _api.Version >= 68 && _api.GetPluginInterfaceFn      != IntPtr.Zero;
    public bool HasGetLiveObjectIds        => _api.Version >= 69 && _api.GetLiveObjectIdsFn        != IntPtr.Zero;
    public bool HasGetLastUseDone          => _api.Version >= 70 && _api.GetLastUseDoneFn          != IntPtr.Zero;
    public bool HasGetLastWeenieError      => _api.Version >= 70 && _api.GetLastWeenieErrorFn      != IntPtr.Zero;
    public bool HasWieldItem               => _api.Version >= 70 && _api.WieldItemFn               != IntPtr.Zero;
    /// <summary>
    /// v70: the engine raises OnEnchantmentAdded / OnEnchantmentRemoved for the player's
    /// enchantment changes (older engines never called them). Added carries the spell id and
    /// the duration in seconds (-1 or 0 for ones without a timer); Removed carries the spell id.
    /// Both reach the plugin after AC applied the change, so a registry read then sees it.
    /// </summary>
    public bool HasEnchantmentEvents       => _api.Version >= 70;
    /// <summary>v71: script windows (UiSubmit / UiPollEvents / UiGetInfo).</summary>
    public bool HasUi                      => _api.Version >= 71 && _api.UiSubmitFn != IntPtr.Zero
                                              && _api.UiPollEventsFn != IntPtr.Zero && _api.UiGetInfoFn != IntPtr.Zero;
    /// <summary>v72: player-to-player trade (TryGetTradeState / GetTradeItems / Trade* actions).</summary>
    public bool HasTrade                   => _api.Version >= 72 && _api.GetTradeStateFn != IntPtr.Zero
                                              && _api.GetTradeItemsFn != IntPtr.Zero && _api.TradeOpenFn != IntPtr.Zero
                                              && _api.TradeAddFn != IntPtr.Zero && _api.TradeAcceptFn != IntPtr.Zero
                                              && _api.TradeDeclineFn != IntPtr.Zero && _api.TradeResetFn != IntPtr.Zero
                                              && _api.TradeCloseFn != IntPtr.Zero;

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

    /// <summary>Reads the current combat mode directly from AC client memory (1=NonCombat 2=Melee 4=Missile 8=Magic).</summary>
    public int GetCurrentCombatMode()
    {
        return _api.GetCurrentCombatModeFn != IntPtr.Zero
            ? ((delegate* unmanaged[Cdecl]<int>)_api.GetCurrentCombatModeFn)()
            : 1; // NonCombat fallback
    }

    /// <summary>Open the salvage panel for the given salvage tool (async — wait ~400 ms before adding items).</summary>
    public bool SalvagePanelOpen(uint toolId)
        => _api.SalvagePanelOpenFn != IntPtr.Zero &&
           ((delegate* unmanaged[Cdecl]<uint, int>)_api.SalvagePanelOpenFn)(toolId) != 0;

    /// <summary>Add an item to the open salvage panel. Requires the panel to have been opened at least once.</summary>
    public bool SalvagePanelAddItem(uint itemId)
        => _api.SalvagePanelAddItemFn != IntPtr.Zero &&
           ((delegate* unmanaged[Cdecl]<uint, int>)_api.SalvagePanelAddItemFn)(itemId) != 0;

    /// <summary>Execute the salvage operation (equivalent to clicking the Salvage button).</summary>
    public bool SalvagePanelExecute()
        => _api.SalvagePanelExecuteFn != IntPtr.Zero &&
           ((delegate* unmanaged[Cdecl]<int>)_api.SalvagePanelExecuteFn)() != 0;

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

    /// <summary>
    /// Starts a charged jump (mirrors the keyboard spacebar-down path).
    /// Follow with <see cref="DoJump"/> once the desired charge time has elapsed.
    /// </summary>
    public bool CommenceJump()
    {
        return _api.CommenceJumpFn != IntPtr.Zero &&
               ((delegate* unmanaged[Cdecl]<int>)_api.CommenceJumpFn)() != 0;
    }

    /// <summary>
    /// Releases a jump begun with <see cref="CommenceJump"/> (mirrors the keyboard spacebar-up path).
    /// Pass autonomous=true to match UB's Jumper (player-authoritative extent).
    /// </summary>
    public bool DoJump(bool autonomous)
    {
        return _api.DoJumpFn != IntPtr.Zero &&
               ((delegate* unmanaged[Cdecl]<int, int>)_api.DoJumpFn)(autonomous ? 1 : 0) != 0;
    }

    /// <summary>
    /// Writes forward/back/strafe motion directly into CMotionInterp, calls
    /// DoJump(autonomous=1), then clears the motion — mirrors UB's UBHelper.Jumper
    /// algorithm. This is the only reliable way to jump *with momentum*; SetMotion
    /// does not bake velocity into the physics simulation in time for DoJump.
    /// Call this *in place of* <see cref="DoJump"/> when you want a directional jump.
    /// </summary>
    public bool LaunchJumpWithMotion(bool shift, bool holdW, bool holdX, bool holdZ, bool holdC)
    {
        return _api.LaunchJumpWithMotionFn != IntPtr.Zero &&
               ((delegate* unmanaged[Cdecl]<int, int, int, int, int, int>)_api.LaunchJumpWithMotionFn)(
                   shift ? 1 : 0, holdW ? 1 : 0, holdX ? 1 : 0, holdZ ? 1 : 0, holdC ? 1 : 0) != 0;
    }

    /// <summary>
    /// Returns the retail gmRadarUI element's current screen rect in pixels
    /// (x0,y0 top-left, x1,y1 bottom-right exclusive). Returns false until the
    /// radar has rendered at least once this session.
    /// </summary>
    public bool TryGetRadarRect(out int x0, out int y0, out int x1, out int y1)
    {
        x0 = y0 = x1 = y1 = 0;
        if (_api.GetRadarRectFn == IntPtr.Zero)
            return false;

        fixed (int* x0Ptr = &x0)
        fixed (int* y0Ptr = &y0)
        fixed (int* x1Ptr = &x1)
        fixed (int* y1Ptr = &y1)
        {
            return ((delegate* unmanaged[Cdecl]<int*, int*, int*, int*, int>)_api.GetRadarRectFn)(
                x0Ptr, y0Ptr, x1Ptr, y1Ptr) != 0;
        }
    }

    /// <summary>
    /// When enabled, the engine suppresses the vanilla gmRadarUI::DrawObjects
    /// call so the radar rect is blank and a plugin can own it entirely.
    /// </summary>
    public void SetRadarSuppressed(bool enabled)
    {
        if (_api.SetRadarSuppressedFn == IntPtr.Zero)
            return;

        ((delegate* unmanaged[Cdecl]<int, void>)_api.SetRadarSuppressedFn)(enabled ? 1 : 0);
    }

    /// <summary>
    /// When enabled, the engine hides the retail gmMainChatUI each frame via
    /// UIElement::SetVisible(false). Re-asserted every frame so the game can't
    /// sneak it back on.
    /// </summary>
    public void SetPowerbarSuppressed(bool enabled)
    {
        if (_api.SetPowerbarSuppressedFn == IntPtr.Zero)
            return;

        ((delegate* unmanaged[Cdecl]<int, void>)_api.SetPowerbarSuppressedFn)(enabled ? 1 : 0);
    }

    public void SetChatSuppressed(bool enabled)
    {
        if (_api.SetChatSuppressedFn == IntPtr.Zero)
            return;

        ((delegate* unmanaged[Cdecl]<int, void>)_api.SetChatSuppressedFn)(enabled ? 1 : 0);
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

    public int GetNumContainedItems(uint objectId)
    {
        return _api.GetNumContainedItemsFn != IntPtr.Zero
            ? ((delegate* unmanaged[Cdecl]<uint, int>)_api.GetNumContainedItemsFn)(objectId)
            : -1;
    }

    public int GetNumContainedContainers(uint objectId)
    {
        return _api.GetNumContainedContainersFn != IntPtr.Zero
            ? ((delegate* unmanaged[Cdecl]<uint, int>)_api.GetNumContainedContainersFn)(objectId)
            : -1;
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

    /// <summary>
    /// Raised (static: per plugin DLL) after this plugin issues <see cref="UseObject"/> (target 0) or
    /// <see cref="UseObjectOn"/>: (source, target). Lets a plugin know which items'
    /// properties (uses left, stack size ...) may have just changed.
    /// </summary>
    public static event Action<uint, uint>? ObjectUsed;

    public bool UseObject(uint objectId)
    {
        bool ok = _api.UseObjectFn != IntPtr.Zero &&
                  ((delegate* unmanaged[Cdecl]<uint, int>)_api.UseObjectFn)(objectId) != 0;
        ObjectUsed?.Invoke(objectId, 0);
        return ok;
    }

    public bool UseObjectOn(uint sourceObjectId, uint targetObjectId)
    {
        bool ok = _api.UseObjectOnFn != IntPtr.Zero &&
                  ((delegate* unmanaged[Cdecl]<uint, uint, int>)_api.UseObjectOnFn)(sourceObjectId, targetObjectId) != 0;
        ObjectUsed?.Invoke(sourceObjectId, targetObjectId);
        return ok;
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

    /// <summary>Outcome codes returned by <see cref="GetMergeStackResult"/>.</summary>
    public static class MergeStackStatus
    {
        public const int None       = 0; // no request recorded for this pair (or it aged out)
        public const int Queued     = 1; // accepted onto AC's main-thread queue, not executed yet
        public const int Sent       = 2; // merge sent to the server for 'amount' units
        public const int TargetFull = 3; // skipped: the target stack was already full
        public const int Failed     = 4; // invalid ids, merge API unavailable, AC rejected, or threw
        public const int QueueFull  = 5; // dropped: main-thread queue full
    }

    /// <summary>
    /// What happened to the latest <see cref="MergeStackInternal"/> request for this pair
    /// (a <see cref="MergeStackStatus"/> code). <paramref name="amount"/> = units sent
    /// (Sent only); <paramref name="ageMs"/> = how long ago the outcome was recorded.
    /// Returns <see cref="MergeStackStatus.None"/> on engines older than API v68.
    /// </summary>
    public int GetMergeStackResult(uint sourceObjectId, uint targetObjectId, out int amount, out int ageMs)
    {
        amount = 0;
        ageMs = 0;
        if (!HasGetMergeStackResult)
            return MergeStackStatus.None;
        int a = 0, age = 0;
        int status = ((delegate* unmanaged[Cdecl]<uint, uint, int*, int*, int>)_api.GetMergeStackResultFn)(
            sourceObjectId, targetObjectId, &a, &age);
        amount = a;
        ageMs = age;
        return status;
    }

    /// <summary>Server yes/no prompt types (ConfirmationRequest 0x0274).</summary>
    public static class ConfirmationKind
    {
        public const uint SwearAllegiance = 1;
        public const uint AlterSkill = 2;
        public const uint AlterAttribute = 3;
        public const uint Fellowship = 4;
        public const uint CraftInteraction = 5;
        public const uint Augmentation = 6;
        public const uint YesNo = 7;
    }

    /// <summary>Flags OR'ed into <see cref="ArmConfirmation"/>'s minPercent.</summary>
    public static class ConfirmationArmFlags
    {
        /// <summary>v97: the sticky slot. A prompt below the minimum (or without a percent) is left to the client
        /// instead of answered No, and the arm stays armed for later prompts until it times out or is disarmed.
        /// Engines before v97 refuse it (-3).</summary>
        public const int LeaveBelow = 0x100;
    }

    /// <summary>What happened to the last prompt (<see cref="TryGetLastConfirmation"/>).</summary>
    public static class ConfirmationOutcome
    {
        public const int None = 0;       // no prompt this session
        public const int Shown = 1;      // not armed: the client showed its dialog
        public const int Accepted = 2;   // answered Yes for the plugin
        public const int Declined = 3;   // answered No: the server's percent was below the minimum
        public const int SendFailed = 4; // armed, but the send failed: the client showed it
        public const int AcceptedSticky = 5; // v97: answered Yes by the sticky arm (ArmConfirmationLeaveBelow)
    }

    /// <summary>
    /// v85: answer the next server prompt of <paramref name="type"/> (a
    /// <see cref="ConfirmationKind"/>) that arrives within <paramref name="timeoutMs"/>,
    /// without the client opening its dialog: Yes when the server's "N percent chance" is at
    /// least <paramref name="minPercent"/> (or it is 0), else No. One-shot; timeoutMs &lt;= 0
    /// disarms. 1 armed, 0 unavailable (engine before v85, sender or hook missing), -3 bad
    /// arguments.
    /// </summary>
    public int ArmConfirmation(uint type, int minPercent, int timeoutMs)
    {
        if (!HasConfirmationAnswers)
            return 0;
        return ((delegate* unmanaged[Cdecl]<uint, int, int, int>)_api.ArmConfirmationFn)(type, minPercent, timeoutMs);
    }

    /// <summary>
    /// v97: a sticky arm for prompts of <paramref name="type"/>: every one that arrives within
    /// <paramref name="timeoutMs"/> (1-120000) is answered Yes when the server's percent is at least
    /// <paramref name="minPercent"/> (1-100), with outcome <see cref="ConfirmationOutcome.AcceptedSticky"/>;
    /// one below it, or without a percent, is left to the client's own dialog (never answered No). The arm
    /// stays until it times out, <see cref="DisarmConfirmationLeaveBelow"/>, or another sticky arm replaces
    /// it (one sticky slot per client, shared by every plugin). A one-shot <see cref="ArmConfirmation"/>
    /// goes first, and its arm or disarm never touches the sticky arm. 1 armed, 0 unavailable, -3 bad
    /// arguments, and -3 from an engine before v97 (check <see cref="HasConfirmationLeaveBelow"/> first).
    /// </summary>
    public int ArmConfirmationLeaveBelow(uint type, int minPercent, int timeoutMs)
    {
        if (minPercent < 1 || minPercent > 100 || timeoutMs <= 0)
            return -3;
        return ArmConfirmation(type, minPercent | ConfirmationArmFlags.LeaveBelow, timeoutMs);
    }

    /// <summary>
    /// v97: drops the sticky arm (<see cref="ArmConfirmationLeaveBelow"/>); a one-shot arm stays. Does
    /// nothing (0) before v97, where a disarm would clear the one-shot arm instead.
    /// </summary>
    public int DisarmConfirmationLeaveBelow(uint type)
    {
        if (!HasConfirmationLeaveBelow)
            return 0;
        return ArmConfirmation(type, ConfirmationArmFlags.LeaveBelow, 0);
    }

    /// <summary>
    /// v85: the last prompt the server sent. <paramref name="seq"/> counts them (0 = none yet);
    /// <paramref name="contextId"/> is the server's context for it; <paramref name="percent"/> is
    /// -1 when the text has none; <paramref name="outcome"/> is a <see cref="ConfirmationOutcome"/>.
    /// False on an engine before v85.
    /// </summary>
    public bool TryGetLastConfirmation(out int seq, out uint type, out uint contextId, out int percent, out int outcome, out int ageMs)
    {
        seq = 0; type = 0; contextId = 0; percent = -1; outcome = 0; ageMs = 0;
        if (!HasConfirmationAnswers)
            return false;
        uint t = 0, ctx = 0;
        int pct = -1, oc = 0, age = 0;
        seq = ((delegate* unmanaged[Cdecl]<uint*, uint*, int*, int*, int*, int>)_api.GetLastConfirmationFn)(&t, &ctx, &pct, &oc, &age);
        type = t; contextId = ctx; percent = pct; outcome = oc; ageMs = age;
        return true;
    }

    /// <summary>v85: <see cref="TryGetLastConfirmation(out int, out uint, out uint, out int, out int, out int)"/> without the context id.</summary>
    public bool TryGetLastConfirmation(out int seq, out uint type, out int percent, out int outcome, out int ageMs)
        => TryGetLastConfirmation(out seq, out type, out _, out percent, out outcome, out ageMs);

    /// <summary>
    /// Gives an item to an NPC or another player by sending the F7B1 give
    /// GameAction (CM_Inventory::Event_GiveObjectRequest). This is the correct
    /// primitive for /mt givep — MoveItemExternal is move-to-container and
    /// silently fails to give to an NPC. amount=0 gives the whole object;
    /// positive = partial stack. Requires engine API v62+ (check HasGiveObjectTo).
    /// </summary>
    public bool GiveObjectTo(uint objectId, uint targetId, int amount = 0)
    {
        return _api.GiveObjectToFn != IntPtr.Zero &&
               ((delegate* unmanaged[Cdecl]<uint, uint, int, int>)_api.GiveObjectToFn)(objectId, targetId, amount) != 0;
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

    public bool IsPortaling()
    {
        if (_api.IsPortalingFn == IntPtr.Zero) return false;
        return ((delegate* unmanaged[Cdecl]<int>)_api.IsPortalingFn)() != 0;
    }

    public float GetVitae(uint playerId)
    {
        if (_api.GetVitaeFn == IntPtr.Zero) return 1.0f;
        return ((delegate* unmanaged[Cdecl]<uint, float>)_api.GetVitaeFn)(playerId);
    }

    public bool TryGetAccountName(out string name)
    {
        name = string.Empty;
        if (_api.GetAccountNameFn == IntPtr.Zero) return false;
        IntPtr ptr = ((delegate* unmanaged[Cdecl]<IntPtr>)_api.GetAccountNameFn)();
        if (ptr == IntPtr.Zero) return false;
        name = Marshal.PtrToStringAnsi(ptr) ?? string.Empty;
        return !string.IsNullOrEmpty(name);
    }

    public bool TryGetWorldName(out string name)
    {
        name = string.Empty;
        if (_api.GetWorldNameFn == IntPtr.Zero) return false;
        IntPtr ptr = ((delegate* unmanaged[Cdecl]<IntPtr>)_api.GetWorldNameFn)();
        if (ptr == IntPtr.Zero) return false;
        name = Marshal.PtrToStringAnsi(ptr) ?? string.Empty;
        return !string.IsNullOrEmpty(name);
    }

    public bool TryGetObjectWcid(uint objectId, out uint wcid)
    {
        wcid = 0;
        if (_api.GetObjectWcidFn == IntPtr.Zero) return false;
        wcid = ((delegate* unmanaged[Cdecl]<uint, uint>)_api.GetObjectWcidFn)(objectId);
        return wcid != 0;
    }

    public bool HasGetObjectBitfield => _api.GetObjectBitfieldFn != IntPtr.Zero;

    public bool TryGetObjectBitfield(uint objectId, out uint bitfield)
    {
        bitfield = 0;
        if (_api.GetObjectBitfieldFn == IntPtr.Zero) return false;
        bitfield = ((delegate* unmanaged[Cdecl]<uint, uint>)_api.GetObjectBitfieldFn)(objectId);
        return true;
    }

    public bool HasAppraisalData(uint objectId)
    {
        if (_api.HasAppraisalDataFn == IntPtr.Zero) return false;
        return ((delegate* unmanaged[Cdecl]<uint, int>)_api.HasAppraisalDataFn)(objectId) != 0;
    }

    public long GetLastIdTime(uint objectId)
    {
        if (_api.GetLastIdTimeFn == IntPtr.Zero) return 0L;
        return ((delegate* unmanaged[Cdecl]<uint, long>)_api.GetLastIdTimeFn)(objectId);
    }

    public bool TryGetObjectHeading(uint objectId, out float headingDegrees)
    {
        headingDegrees = 0;
        if (_api.GetObjectHeadingFn == IntPtr.Zero) return false;
        fixed (float* hPtr = &headingDegrees)
            return ((delegate* unmanaged[Cdecl]<uint, float*, int>)_api.GetObjectHeadingFn)(objectId, hPtr) != 0;
    }

    public int GetBusyState()
    {
        if (_api.GetBusyStateFn == IntPtr.Zero) return 0;
        return ((delegate* unmanaged[Cdecl]<int>)_api.GetBusyStateFn)();
    }

    public void ForceResetBusyCount()
    {
        if (_api.ForceResetBusyCountFn != IntPtr.Zero)
            ((delegate* unmanaged[Cdecl]<void>)_api.ForceResetBusyCountFn)();
    }

    /// <summary>
    /// The REAL cast gate: 0 = clear to cast, 1 = a cast/action gesture is
    /// animating. Distinct from <see cref="GetBusyState"/> (the ClientUISystem
    /// hourglass, which reads 0 while AC still rejects a cast with "You're too
    /// busy!"). Returns 0 (clear) on an engine that predates this API field —
    /// callers should pair it with their own throttle so a missing gate
    /// degrades gracefully rather than spamming.
    /// </summary>
    public int GetCastBusyState()
    {
        if (_api.GetCastBusyStateFn == IntPtr.Zero) return 0;
        return ((delegate* unmanaged[Cdecl]<int>)_api.GetCastBusyStateFn)();
    }

    /// <summary>
    /// True when no cast/action gesture is animating (clear to issue a cast).
    /// Defaults to true on an engine without the cast-gate field so older
    /// engines fall back to the consumer's throttle/park behaviour.
    /// </summary>
    public bool CanCastNow => GetCastBusyState() == 0;

    /// <summary>True when the engine exposes the server UseDone (0x1C7) counter.</summary>
    public bool HasUseDoneSeq => _api.GetUseDoneSeqFn != IntPtr.Zero;

    /// <summary>
    /// Monotonic count of inbound server UseDone (GameEvent 0x01C7) events. The
    /// server sends one when it FINISHES an action (cast/use) — completed or
    /// refused. Record this at cast time and watch for it to change to know the
    /// server resolved the cast, so combat casts can be paced on real completion
    /// instead of a blind interval. Returns 0 on an engine without the signal
    /// (callers must fall back to a timeout). Requires API v63+.
    /// </summary>
    public int GetUseDoneSeq()
    {
        if (_api.GetUseDoneSeqFn == IntPtr.Zero) return 0;
        return ((delegate* unmanaged[Cdecl]<int>)_api.GetUseDoneSeqFn)();
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

    /// <summary>
    /// Reads a STypeDID property that lives in the object's PublicWeenieDesc: Icon=8 (→ _iconID,
    /// e.g. 0x06xxxxxx), and on engines from 2026-09-30 IconOverlay=50 / IconUnderlay=52 (0 = none). Works on UNequipped/never-appraised pack items
    /// (PWD is network-populated; no qualities pointer or appraisal required). Engines from
    /// API v73 answer every data id the client knows (an identified object's data id table,
    /// the player's PlayerDescription, UpdatePropertyDataID, the PWD Spell 28). Returns false
    /// when the engine predates API v65 or the read fails.
    /// </summary>
    public bool TryGetObjectDataIdProperty(uint objectId, uint stype, out uint value)
    {
        value = 0;
        if (_api.GetObjectDataIdPropertyFn == IntPtr.Zero)
            return false;

        fixed (uint* valuePtr = &value)
        {
            return ((delegate* unmanaged[Cdecl]<uint, uint, uint*, int>)_api.GetObjectDataIdPropertyFn)(objectId, stype, valuePtr) != 0;
        }
    }

    /// <summary>
    /// v73: reads a PropertyInstanceId (STypeIID) - another object's id, e.g. Container 2,
    /// Wielder 3, Monarch 26, HouseOwner 32, PetOwner 44, or the player's Allegiance/Patron.
    /// Any thread. False when the engine predates v73, or the client doesn't know it (0).
    /// On older engines Container and Wielder are still available through
    /// <see cref="TryGetObjectOwnershipInfo"/>.
    /// </summary>
    public bool TryGetObjectInstanceIdProperty(uint objectId, uint stype, out uint value)
    {
        value = 0;
        if (!HasGetObjectInstanceIdProperty)
            return false;

        fixed (uint* valuePtr = &value)
        {
            return ((delegate* unmanaged[Cdecl]<uint, uint, uint*, int>)_api.GetObjectInstanceIdPropertyFn)(objectId, stype, valuePtr) != 0;
        }
    }

    /// <summary>
    /// v74: VTank's macro in this client, as the engine sees it under the Decal bridge
    /// (VTank's documented /vt start and /vt stop through Decal's chat parser; VTank's own
    /// Run Macro button is not seen). <paramref name="watching"/> is false without Decal, or
    /// when Decal runs without the bridge: then <paramref name="running"/> is always false.
    /// <paramref name="sequence"/> changes on every change. False when the engine predates v74.
    /// Any thread.
    /// </summary>
    public bool TryGetVTankState(out bool watching, out bool running, out int sequence)
    {
        watching = false;
        running = false;
        sequence = 0;
        if (!HasGetVTankState)
            return false;

        int seq = 0;
        int flags = ((delegate* unmanaged[Cdecl]<int*, int>)_api.GetVTankStateFn)(&seq);
        watching = (flags & 1) != 0;
        running = watching && (flags & 2) != 0;
        sequence = seq;
        return true;
    }

    /// <summary>
    /// v75: the title ids the player holds and the one on display, from the server's title
    /// events (sent at login and for each new title). False when the engine predates v75 or
    /// hasn't received the list for this character (e.g. a plugin build on an engine that was
    /// updated mid-session: it comes at the next login). Any thread.
    /// </summary>
    public bool TryGetCharacterTitles(out uint[] titleIds, out uint currentTitle)
    {
        titleIds = Array.Empty<uint>();
        currentTitle = 0;
        if (!HasGetCharacterTitles)
            return false;
        var fn = (delegate* unmanaged[Cdecl]<uint*, int, uint*, int>)_api.GetCharacterTitlesFn;
        uint cur = 0;
        int count = fn(null, 0, &cur);
        if (count < 0)
            return false;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint[] buffer = new uint[Math.Max(count, 1)];
            int total;
            fixed (uint* p = buffer) total = fn(p, buffer.Length, &cur);
            if (total < 0)
                return false;
            if (total <= buffer.Length)
            {
                Array.Resize(ref buffer, total);
                titleIds = buffer;
                currentTitle = cur;
                return true;
            }
            count = total;
        }
        return false;
    }

    /// <summary>
    /// v75: which server this client is on, as the engine decides it: <paramref name="isAelrynth"/>
    /// (the connect host, the world name the server announced, or the Bank mod's properties),
    /// <paramref name="isStaging"/> (Aelrynth's staging world), and the world name the server
    /// announced at login ("" until then; never the launcher's profile name). False when the
    /// engine predates v75. Any thread.
    /// </summary>
    public bool TryGetServerInfo(out bool isAelrynth, out bool isStaging, out string worldName)
    {
        isAelrynth = false;
        isStaging = false;
        worldName = string.Empty;
        if (!HasGetServerInfo)
            return false;
        byte* buf = stackalloc byte[257];
        int flags = ((delegate* unmanaged[Cdecl]<byte*, int, int>)_api.GetServerInfoFn)(buf, 257);
        isAelrynth = (flags & 1) != 0;
        isStaging = (flags & 2) != 0;
        if ((flags & 4) != 0)
        {
            int n = 0;
            while (n < 256 && buf[n] != 0) n++;
            worldName = System.Text.Encoding.UTF8.GetString(buf, n);
        }
        return true;
    }

    /// <summary>
    /// v86: which server this is, as JSON: every server the engine knows (built in, or declared
    /// by a loaded server plugin) with its verdict, this character's override, the reason and the
    /// plugin that owns its settings, plus "active" / "activePlugin" and the Diagnostics "pick".
    /// Schema: RynthCore.Engine UI/Sections/ServerStateJson.cs. False when the engine predates
    /// v86 or the call failed. Any thread; a few hundred bytes, so poll it (every second or two)
    /// rather than caching it for a session: an override changes it live.
    /// </summary>
    /// <summary>
    /// v87: what the player wields and where (EquipMask: 0x00100000 MeleeWeapon, 0x00200000 Shield,
    /// 0x00400000 MissileWeapon, 0x00800000 MissileAmmo, 0x01000000 Held, 0x02000000 TwoHanded, ...),
    /// from the server's wield / contain / drop messages. False when the engine doesn't have it or
    /// can't start yet (no player). Any thread.
    /// </summary>
    public bool TryGetWieldedItems(out (uint Id, uint Location)[] items)
    {
        items = Array.Empty<(uint, uint)>();
        if (!HasGetWieldedItems) return false;
        const int Max = 64;
        uint* ids = stackalloc uint[Max];
        uint* locs = stackalloc uint[Max];
        int n = ((delegate* unmanaged[Cdecl]<uint*, uint*, int, int>)_api.GetWieldedItemsFn)(ids, locs, Max);
        if (n < 0) return false;
        var list = new (uint, uint)[n];
        for (int i = 0; i < n; i++) list[i] = (ids[i], locs[i]);
        items = list;
        return true;
    }

    public bool TryGetServerStateJson(out string json)
    {
        json = string.Empty;
        if (!HasGetServerStateJson)
            return false;
        var fn = (delegate* unmanaged[Cdecl]<byte*, int, int>)_api.GetServerStateJsonFn;
        int capacity = 2048;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            byte[] buffer = new byte[capacity];
            int length;
            fixed (byte* p = buffer) length = fn(p, capacity);
            if (length < 0) return false;
            if (length < capacity)
            {
                json = System.Text.Encoding.UTF8.GetString(buffer, 0, length);
                return true;
            }
            capacity = length + 256;
        }
        return false;
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

    public bool HasGetObjectSkillBuffed => _api.GetObjectSkillBuffedFn != IntPtr.Zero;

    /// <summary>raw=0 → buffed (with enchantments), raw=1 → base (no enchantments, includes attribute contribution).</summary>
    public bool TryGetObjectSkillLevel(uint objectId, uint skillStype, int raw, out int level)
    {
        level = 0;
        if (_api.GetObjectSkillBuffedFn == IntPtr.Zero) return false;
        fixed (int* p = &level)
            return ((delegate* unmanaged[Cdecl]<uint, uint, int, int*, int>)_api.GetObjectSkillBuffedFn)(objectId, skillStype, raw, p) != 0;
    }

    public bool HasGetObjectAttribute => _api.GetObjectAttributeFn != IntPtr.Zero;

    public bool TryGetObjectAttribute(uint objectId, uint stype, int raw, out uint value)
    {
        value = 0;
        if (_api.GetObjectAttributeFn == IntPtr.Zero) return false;
        fixed (uint* p = &value)
            return ((delegate* unmanaged[Cdecl]<uint, uint, int, uint*, int>)_api.GetObjectAttributeFn)(objectId, stype, raw, p) != 0;
    }

    public bool HasGetObjectMotionOn => _api.GetObjectMotionOnFn != IntPtr.Zero;

    /// <summary>
    /// Returns true if a DoMotion On/Off state is known for the object.
    /// isOn is true if the last observed motion was On (e.g. door opened), false if Off (door closed).
    /// Returns false if no motion state has been observed for this object since injection.
    /// </summary>
    public bool TryGetObjectMotionOn(uint objectId, out bool isOn)
    {
        isOn = false;
        if (_api.GetObjectMotionOnFn == IntPtr.Zero) return false;
        int v = 0;
        if (((delegate* unmanaged[Cdecl]<uint, int*, int>)_api.GetObjectMotionOnFn)(objectId, &v) == 0)
            return false;
        isOn = v != 0;
        return true;
    }

    public bool HasGetObjectState => _api.GetObjectStateFn != IntPtr.Zero;

    /// <summary>
    /// Returns true if a PhysicsState value has been received from the server for this object.
    /// state is the raw PhysicsState bitfield (e.g. 0x100 = Open).
    /// Returns false if no SetState has been observed for this object since injection.
    /// </summary>
    public bool TryGetObjectState(uint objectId, out uint state)
    {
        state = 0;
        if (_api.GetObjectStateFn == IntPtr.Zero) return false;
        uint s = 0;
        if (((delegate* unmanaged[Cdecl]<uint, uint*, int>)_api.GetObjectStateFn)(objectId, &s) == 0)
            return false;
        state = s;
        return true;
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

    public int ReadKnownSpells(uint[] spellIds, int maxCount)
    {
        if (_api.ReadKnownSpellsFn == IntPtr.Zero || spellIds == null || maxCount <= 0)
            return -1;

        IntPtr buf = Marshal.AllocHGlobal(maxCount * sizeof(uint));
        try
        {
            int result = ((delegate* unmanaged[Cdecl]<uint*, int, int>)_api.ReadKnownSpellsFn)(
                (uint*)buf, maxCount);
            int count = Math.Max(0, Math.Min(result, Math.Min(maxCount, spellIds.Length)));
            uint* sp = (uint*)buf;
            for (int i = 0; i < count; i++)
                spellIds[i] = sp[i];
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
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

    public int GetObjectSpellIds(uint objectId, uint[] spellIds, int maxCount)
    {
        if (_api.GetObjectSpellIdsFn == IntPtr.Zero || spellIds == null || maxCount <= 0)
            return -1;

        IntPtr spellBuf = Marshal.AllocHGlobal(maxCount * sizeof(uint));
        try
        {
            int result = ((delegate* unmanaged[Cdecl]<uint, uint*, int, int>)_api.GetObjectSpellIdsFn)(
                objectId, (uint*)spellBuf, maxCount);

            int count = result > 0 ? Math.Min(result, Math.Min(maxCount, spellIds.Length)) : 0;
            uint* sp = (uint*)spellBuf;
            for (int i = 0; i < count; i++)
                spellIds[i] = sp[i];
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(spellBuf);
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

    /// <summary>
    /// Same as Nav3DAddRing but with explicit wall height in world units. On
    /// older engines (API &lt; 61) where this isn't available, falls back to
    /// Nav3DAddRing — the user still gets the ring at the legacy 0.5 m height.
    /// </summary>
    public void Nav3DAddRingEx(float wx, float wy, float wz, float radius, float thickness, float height, uint colorArgb)
    {
        if (_api.Nav3DAddRingExFn != IntPtr.Zero)
        {
            ((delegate* unmanaged[Cdecl]<float, float, float, float, float, float, uint, void>)_api.Nav3DAddRingExFn)(
                wx, wy, wz, radius, thickness, height, colorArgb);
            return;
        }
        Nav3DAddRing(wx, wy, wz, radius, thickness, colorArgb);
    }

    public void Nav3DAddLine(float x1, float y1, float z1, float x2, float y2, float z2, float thickness, uint colorArgb)
    {
        if (_api.Nav3DAddLineFn == IntPtr.Zero)
            return;
        ((delegate* unmanaged[Cdecl]<float, float, float, float, float, float, float, uint, void>)_api.Nav3DAddLineFn)(
            x1, y1, z1, x2, y2, z2, thickness, colorArgb);
    }

    /// <summary>
    /// Submits a filled 3D triangle in world coordinates (D3D: X=EW, Y=height,
    /// Z=NS). Use for terrain-conforming overlays — passing the actual mesh
    /// vertices makes the face hug the slope exactly. ARGB colour.
    /// </summary>
    public void Nav3DAddTriangle(float x1, float y1, float z1,
                                 float x2, float y2, float z2,
                                 float x3, float y3, float z3,
                                 uint colorArgb)
    {
        if (_api.Nav3DAddTriangleFn == IntPtr.Zero)
            return;
        ((delegate* unmanaged[Cdecl]<float, float, float, float, float, float, float, float, float, uint, void>)_api.Nav3DAddTriangleFn)(
            x1, y1, z1, x2, y2, z2, x3, y3, z3, colorArgb);
    }

    // ─── Chat parser ────────────────────────────────────────────────────────

    /// <summary>
    /// v89: a colour of the player's theme as ImGui's packed colour (ABGR, for draw lists and
    /// PushStyleColor), or <paramref name="fallback"/> on an older engine. Any thread; cheap, so
    /// read it each frame and a theme change shows at once.
    /// </summary>
    public uint GetUiColor(RynthUiColor token, uint fallback)
    {
        if (!HasUiTheme) return fallback;
        uint c = ((delegate* unmanaged[Cdecl]<int, uint>)_api.GetUiColorFn)((int)token);
        return c != 0 ? c : fallback;
    }

    /// <summary>
    /// v89: the engine's <c>ImFont*</c> for <paramref name="font"/> at the text size of the panel being
    /// drawn (wrap it in ImGuiNET's ImFontPtr and PushFont it). Zero on an older engine, for an
    /// unknown font, or off AC's render thread (call it from RynthPluginRenderEmbed).
    /// </summary>
    public IntPtr GetUiFont(RynthUiFont font)
        => HasUiTheme ? ((delegate* unmanaged[Cdecl]<int, IntPtr>)_api.GetUiFontFn)((int)font) : IntPtr.Zero;

    /// <summary>
    /// v89: opens, closes or toggles the panel <paramref name="key"/> (a plugin window's title, or an
    /// engine panel's), or asks. Returns 1 open / 0 closed afterwards (the change lands on the next
    /// frame), -1 for no such panel or an older engine. Any thread.
    /// </summary>
    public int SetPanelOpen(string key, RynthPanelOpen mode)
    {
        if (!HasSetPanelOpen || string.IsNullOrEmpty(key) || key.Length > 64) return -1;
        Span<byte> utf8 = stackalloc byte[260];
        int n = System.Text.Encoding.UTF8.GetBytes(key, utf8[..^1]);
        utf8[n] = 0;
        fixed (byte* p = utf8)
            return ((delegate* unmanaged[Cdecl]<byte*, int, int>)_api.SetPanelOpenFn)(p, (int)mode);
    }

    /// <summary>
    /// v90: while ImGui's last item is held and dragged, the panel being drawn follows the mouse (a
    /// frameless window's own title row: call it right after that row's InvisibleButton). Docked, the
    /// window moves within the game view; popped out, its OS window. Only from RynthPluginRenderEmbed
    /// on AC's render thread; does nothing elsewhere or on an older engine.
    /// </summary>
    public void PanelDragWithLastItem()
    {
        if (!HasFramelessWindows) return;
        ((delegate* unmanaged[Cdecl]<void>)_api.PanelDragWithLastItemFn)();
    }

    /// <summary>
    /// v90: pops the open panel <paramref name="key"/> out into its own window, docks it back, toggles,
    /// or asks. Returns 1 popped out / 0 docked afterwards (the change lands on the next frame), -1 for
    /// no such open panel or an older engine. Any thread.
    /// </summary>
    public int SetPanelPopOut(string key, RynthPanelPopOut mode) => CallWithKey(_api.SetPanelPopOutFn, key, (int)mode);

    /// <summary>
    /// v90: steps the panel <paramref name="key"/>'s text size by <paramref name="delta"/> (0 = just ask)
    /// and returns it as a percentage of normal (100 to 250); -1 for no such panel or an older engine.
    /// The fonts re-bake a moment later. Any thread.
    /// </summary>
    public int PanelTextSize(string key, int delta) => CallWithKey(_api.PanelTextSizeFn, key, delta);

    /// <summary>
    /// v90: how big the frameless window <paramref name="key"/> draws itself, in percent of normal
    /// (30-250; Settings > Interface > In The Retail Toolbar's Place sets it too): <paramref name="percent"/> 0 asks, -1 goes back to
    /// following the text size, 30-250 sets it. Returns it now: 0 = follow the text size (draw at the
    /// fonts' size), -1 on an older engine. Any thread; cheap to ask every frame.
    /// </summary>
    public int PanelScale(string key, int percent = 0) =>
        _api.Version >= 90 && _api.PanelScaleFn != IntPtr.Zero ? CallWithKey(_api.PanelScaleFn, key, percent) : -1;

    /// <summary>
    /// v90: draws the body of one of the dashboard drawers that show Settings rows, which are still
    /// RynthCore's (the Settings panel hasn't moved to RynthAi): "ranges" (the selected monster and the
    /// key ranges), "patrol" (the Dungeon Patrol settings, hazards and routes) or "pinned" (My settings;
    /// <paramref name="openPinList"/> opens its list of settings to pin). At ImGui's cursor in the window
    /// being drawn, <paramref name="width"/> pixels wide and at most <paramref name="room"/> tall (0 = its
    /// own height). Returns the height it wants (pixels), or -1 for another key, an older engine, or a
    /// call outside a docked window's RynthPluginRenderEmbed on AC's render thread.
    /// 2026-10-27 (engines with v91): also "nameplates" and "combattext", the Vision window's tabs for
    /// RynthCore's nameplates and combat text (RynthVision's own Vision window), docked or popped out.
    /// </summary>
    public float RenderSettingsDrawer(string key, float width, float room, bool openPinList = false)
    {
        if (!HasSettingsDrawers || string.IsNullOrEmpty(key) || key.Length > 64) return -1f;
        Span<byte> utf8 = stackalloc byte[260];
        int n = System.Text.Encoding.UTF8.GetBytes(key, utf8[..^1]);
        utf8[n] = 0;
        fixed (byte* p = utf8)
            return ((delegate* unmanaged[Cdecl]<byte*, float, float, int, float>)_api.RenderSettingsDrawerFn)(p, width, room, openPinList ? 1 : 0);
    }

    /// <summary>
    /// v90: the player's 18 shortcut slots (AC's own: what the retail toolbar shows and its keys
    /// use): each slot's object id (0 = empty) and spell id. Returns 18, or 0 out of the world or on
    /// an older engine. Any thread.
    /// </summary>
    public int GetShortcuts(Span<uint> objectIds, Span<uint> spellIds)
    {
        if (!HasShortcuts) return 0;
        int max = Math.Min(objectIds.Length, spellIds.Length);
        fixed (uint* o = objectIds)
        fixed (uint* s = spellIds)
            return ((delegate* unmanaged[Cdecl]<uint*, uint*, int, int>)_api.GetShortcutsFn)(o, s, max);
    }

    /// <summary>
    /// v90: uses shortcut slot <paramref name="slot"/> (0-17) as its key would, puts
    /// <paramref name="objectId"/> in it, or empties it: through the retail toolbar's own functions
    /// on AC's game thread, so the server, the retail toolbar and the keys all follow. True when
    /// queued. Any thread.
    /// </summary>
    public bool ShortcutAction(RynthShortcutOp op, int slot, uint objectId = 0)
    {
        if (!HasShortcuts) return false;
        return ((delegate* unmanaged[Cdecl]<int, int, uint, int>)_api.ShortcutActionFn)((int)op, slot, objectId) == 1;
    }

    /// <summary>
    /// v90: AC's keys for keymap action <paramref name="action"/> (e.g. "UseQuickSlot_1") as the player
    /// reads them, "1, Ctrl+1", or the engine hotkeys running a chat command ("/rdef use 2"); "" when
    /// unbound, unknown, or on an older engine. Any thread.
    /// </summary>
    public string GetActionKeys(string action)
    {
        if (!HasShortcuts || string.IsNullOrEmpty(action) || action.Length > 64) return string.Empty;
        Span<byte> name = stackalloc byte[260];
        int n = System.Text.Encoding.UTF8.GetBytes(action, name[..^1]);
        name[n] = 0;
        Span<byte> text = stackalloc byte[256];
        int len;
        fixed (byte* a = name)
        fixed (byte* t = text)
            len = ((delegate* unmanaged[Cdecl]<byte*, byte*, int, int>)_api.GetActionKeysFn)(a, t, text.Length);
        if (len <= 0) return string.Empty;
        return System.Text.Encoding.UTF8.GetString(text[..Math.Min(len, text.Length - 1)]);
    }

    /// <summary>
    /// v90: an item's (kind Object: its icon with underlay and overlay), a spell's or an icon id's
    /// picture as an ImGui texture id for ImGui.Image; IntPtr.Zero while it is being made (ask again
    /// next frame). AC's render thread, inside a docked window's draw.
    /// </summary>
    public IntPtr GetIconTexture(RynthIconKind kind, uint id)
    {
        if (!HasShortcuts || id == 0) return IntPtr.Zero;
        return ((delegate* unmanaged[Cdecl]<int, uint, IntPtr>)_api.GetIconTextureFn)((int)kind, id);
    }

    /// <summary>v90: changing keys: <see cref="BeginKeyCapture"/> ("press a key") and <see cref="BindActionKey"/>.</summary>
    public bool HasKeyBinding => _api.Version >= 90 && _api.KeyCaptureFn != IntPtr.Zero && _api.SetActionKeyFn != IntPtr.Zero;

    /// <summary>
    /// v90: waits for the next key the player presses (with Ctrl/Alt/Shift held); it never reaches AC, and
    /// Esc cancels. Ask with <see cref="PollKeyCapture"/> each frame. AC's main thread (a window's draw).
    /// </summary>
    public void BeginKeyCapture()
    {
        if (HasKeyBinding) ((delegate* unmanaged[Cdecl]<int, byte*, int, int>)_api.KeyCaptureFn)(0, null, 0);
    }

    /// <summary>v90: stops waiting for a key.</summary>
    public void CancelKeyCapture()
    {
        if (HasKeyBinding) ((delegate* unmanaged[Cdecl]<int, byte*, int, int>)_api.KeyCaptureFn)(2, null, 0);
    }

    /// <summary>
    /// v90: the key capture: still waiting (<paramref name="text"/>: why the last key couldn't be taken, or
    /// ""), pressed (<paramref name="text"/>: its name, "Ctrl+F5") or cancelled. AC's main thread.
    /// </summary>
    public RynthKeyCapture PollKeyCapture(out string text)
    {
        text = string.Empty;
        if (!HasKeyBinding) return RynthKeyCapture.Cancelled;
        Span<byte> buf = stackalloc byte[128];
        int r;
        fixed (byte* b = buf)
            r = ((delegate* unmanaged[Cdecl]<int, byte*, int, int>)_api.KeyCaptureFn)(1, b, buf.Length);
        int n = buf.IndexOf((byte)0);
        text = System.Text.Encoding.UTF8.GetString(buf[..(n < 0 ? buf.Length : n)]);
        return r switch { 1 => RynthKeyCapture.Pressed, 0 => RynthKeyCapture.Waiting, _ => RynthKeyCapture.Cancelled };
    }

    /// <summary>
    /// v90: what binding <paramref name="key"/> ("Ctrl+F5") to <paramref name="action"/> would take from
    /// others. The action is AC's keymap action ("UseQuickSlot_5": AC's own keys, the retail toolbar's) or a
    /// chat command for an engine hotkey ("/rdef use 2"). <paramref name="message"/> names what has the key
    /// now, or why it can't be bound. Any thread.
    /// </summary>
    public RynthKeyCheck CheckActionKey(string action, string key, out string message) =>
        (RynthKeyCheck)Math.Clamp(CallSetActionKey(action, key, 0, out message), -1, 1);

    /// <summary>
    /// v90: binds <paramref name="key"/> to <paramref name="action"/> (see <see cref="CheckActionKey"/>): it
    /// comes off everything else that had it, in AC's keymap and RynthCore's hotkeys, and every running
    /// client follows within a second. Done on AC's game thread; the result goes to chat. False (why in
    /// <paramref name="message"/>) when it can't be. Any thread.
    /// </summary>
    public bool BindActionKey(string action, string key, out string message) => CallSetActionKey(action, key, 1, out message) == 1;

    /// <summary>
    /// v90: removes <paramref name="key"/> from <paramref name="action"/>; the key may be one of the names
    /// <see cref="GetActionKeys"/> gave ("Ctrl+1", "Mouse4"). Queued like <see cref="BindActionKey"/>.
    /// </summary>
    public bool UnbindActionKey(string action, string key, out string message) => CallSetActionKey(action, key, 2, out message) == 1;

    /// <summary>v92: <see cref="BindCommandKeyOnly"/> works (SetActionKey op 3).</summary>
    public bool HasCommandKeyOnly => HasKeyBinding && _api.Version >= 92;

    /// <summary>
    /// Binds <paramref name="key"/> ("Ctrl+F5") to a RynthCore chat command (<paramref name="command"/>, starting
    /// with '/': "/rdef use 3") as an engine hotkey only, for a bar whose slots are its own: the key comes off
    /// RynthCore's other hotkeys, but AC's keymap is not edited and no client reloads its keys. AC keeps any
    /// action it has on that key; RynthCore's hotkey takes the key before AC sees it. Done on AC's game thread;
    /// the result goes to chat. Needs plugin API v92 (SetActionKey op 3, after release 2026.10.27.1): an older
    /// engine refuses it, and this returns false (why in <paramref name="message"/>), never falling back to
    /// <see cref="BindActionKey"/>. False too for an action that isn't a command. Any thread.
    /// </summary>
    public bool BindCommandKeyOnly(string command, string key, out string message)
    {
        if (string.IsNullOrEmpty(command) || command[0] != '/') { message = "Not a RynthCore command (\"/rdef use 3\")."; return false; }
        if (!HasCommandKeyOnly) { message = "This RynthCore can't bind a command's key on its own (it needs a newer engine)."; return false; }
        int r = CallSetActionKey(command, key, 3, out message);
        if (r != 1 && message == "Unknown op.") message = "This RynthCore can't bind a command's key on its own (it needs a newer engine).";
        return r == 1;
    }

    /// <summary>
    /// Removes <paramref name="key"/> from a RynthCore chat command's engine hotkeys (<paramref name="command"/>,
    /// starting with '/'), the other half of <see cref="BindCommandKeyOnly"/>: AC's keymap is never touched.
    /// An engine command's unbind has never touched AC's keymap, so this works on any v90 engine (it is
    /// <see cref="UnbindActionKey"/> for a command); false for an action that isn't a command, or when it
    /// can't be (why in <paramref name="message"/>). Queued like <see cref="BindActionKey"/>. Any thread.
    /// </summary>
    public bool UnbindCommandKeyOnly(string command, string key, out string message)
    {
        if (string.IsNullOrEmpty(command) || command[0] != '/') { message = "Not a RynthCore command (\"/rdef use 3\")."; return false; }
        return CallSetActionKey(command, key, 2, out message) == 1;
    }

    /// <summary>v90: items dragged out of AC's UI can be dropped on a plugin window (<see cref="DropTarget"/>).</summary>
    public bool HasDropTargets => _api.Version >= 90 && _api.DropTargetFn != IntPtr.Zero;

    /// <summary>
    /// v90: the plugin window <paramref name="window"/> (its panel title) takes items dragged out of AC's
    /// UI (a pack, the inventory) and let go over <paramref name="min"/>-<paramref name="max"/> (ImGui
    /// screen coordinates). Call it every frame the window draws; <paramref name="accepting"/> false =
    /// drawn but a drop isn't wanted now (it goes to AC as usual). A drop taken never reaches AC: the item
    /// stays where it was. Returns the object dropped since the last call (0 = none);
    /// <paramref name="dragging"/>: an item is being dragged now. AC's main thread, in the window's draw.
    /// </summary>
    public uint DropTarget(string window, System.Numerics.Vector2 min, System.Numerics.Vector2 max, bool accepting, out bool dragging)
    {
        dragging = false;
        if (!HasDropTargets || string.IsNullOrEmpty(window) || window.Length > 64) return 0;
        Span<byte> utf8 = stackalloc byte[260];
        int n = System.Text.Encoding.UTF8.GetBytes(window, utf8[..^1]);
        utf8[n] = 0;
        int drag = 0;
        uint item;
        fixed (byte* p = utf8)
            item = ((delegate* unmanaged[Cdecl]<byte*, float, float, float, float, int, int*, uint>)_api.DropTargetFn)(
                p, min.X, min.Y, max.X, max.Y, accepting ? 1 : 0, &drag);
        dragging = drag != 0;
        return item;
    }

    /// <summary>v90: AC's own panels (<see cref="IsAcPanelOpen"/>, <see cref="AcPanel"/>).</summary>
    public bool HasAcPanels => _api.Version >= 90 && _api.AcPanelFn != IntPtr.Zero;

    /// <summary>
    /// v90: whether AC's panel is open: true, false, or null when this client has no such panel or the
    /// engine can't tell. AC's main thread (a window's draw); cheap to ask every frame.
    /// </summary>
    public bool? IsAcPanelOpen(RynthAcPanel panel)
    {
        if (!HasAcPanels) return null;
        int r = ((delegate* unmanaged[Cdecl]<int, int, int>)_api.AcPanelFn)((int)panel, 0);
        return r < 0 ? null : r == 1;
    }

    /// <summary>
    /// v90: opens, closes or toggles AC's own panel, the way AC's toolbar buttons and keys do (one main
    /// panel shows at a time, as in AC). Done on AC's game thread; true when queued. Any thread.
    /// </summary>
    public bool AcPanel(RynthAcPanel panel, RynthAcPanelOp op = RynthAcPanelOp.Toggle)
    {
        if (!HasAcPanels) return false;
        return ((delegate* unmanaged[Cdecl]<int, int, int>)_api.AcPanelFn)((int)panel, (int)op) == 1;
    }

    /// <summary>
    /// v91: <see cref="SetAmbientLightFloor"/> and <see cref="TryGetAmbientLight"/> (a floor under the
    /// world's ambient light). True on an engine that has the call; whether its hook is live is what
    /// SetAmbientLightFloor returns.
    /// </summary>
    public bool HasAmbientLight => _api.Version >= 91 && _api.SetAmbientLightFloorFn != IntPtr.Zero && _api.GetAmbientLightFn != IntPtr.Zero;

    /// <summary>
    /// v91: the least ambient light AC may draw the world with, 0x00RRGGBB (alpha ignored), 0 = none (AC's
    /// own). Per channel the brighter of AC's and this, from the next frame; drawing only, in this client
    /// (nothing goes to the server). AC lights dungeons at 0x333333 (20 %), outdoors from the sky. One floor
    /// for all plugins (the last call wins); the engine clears it when the plugins unload, so a plugin
    /// clears it itself when it stops wanting it (and in Shutdown). Returns true when the engine's hook is
    /// live, false when it isn't (an older engine, not installed, or switched off with /rc hooks). Any thread.
    /// </summary>
    public bool SetAmbientLightFloor(uint rgb)
    {
        if (!HasAmbientLight) return false;
        return ((delegate* unmanaged[Cdecl]<uint, int>)_api.SetAmbientLightFloorFn)(rgb & 0x00FFFFFFu) == 1;
    }

    /// <summary>
    /// v91: the ambient colour AC last asked for (<paramref name="client"/>) and the one drawn
    /// (<paramref name="applied"/>), 0xAARRGGBB, 0 before the first frame. False when the hook isn't live
    /// (or the engine is older). Any thread.
    /// </summary>
    public bool TryGetAmbientLight(out uint client, out uint applied)
    {
        client = 0;
        applied = 0;
        if (!HasAmbientLight) return false;
        uint c = 0, a = 0;
        int r = ((delegate* unmanaged[Cdecl]<uint*, uint*, int>)_api.GetAmbientLightFn)(&c, &a);
        client = c;
        applied = a;
        return r == 1;
    }

    /// <summary>
    /// v93: <see cref="TryGetMonsterHealth"/> (the monster plates' last server health reading per object).
    /// </summary>
    public bool HasMonsterHealth => _api.Version >= 93 && _api.GetMonsterHealthFn != IntPtr.Zero;

    /// <summary>
    /// v93: the last health ratio (0..1) the server sent for <paramref name="id"/>, as the engine's monster
    /// plates keep it, and how many ms ago (<paramref name="ageMs"/>). The plates ask the server for the
    /// monsters around the player round-robin (about one a second; ones in a fight every 2 s, quiet ones
    /// every 12 s), so this covers monsters whose health the server streams to nobody else here. Those
    /// probe replies never arrive as UpdateHealth events (a plugin must not count someone else's hit as
    /// its own): use this for ranking, not for kill or damage tracking. Never the plates' estimates from
    /// the player's hits. False when the server never sent this object's health, or on an older engine.
    /// Read-only, any thread.
    /// </summary>
    public bool TryGetMonsterHealth(uint id, out float ratio, out uint ageMs)
    {
        ratio = -1f;
        ageMs = uint.MaxValue;
        if (!HasMonsterHealth || id == 0) return false;
        float r = -1f;
        uint age = uint.MaxValue;
        int ok = ((delegate* unmanaged[Cdecl]<uint, float*, uint*, int>)_api.GetMonsterHealthFn)(id, &r, &age);
        if (ok != 1 || float.IsNaN(r) || r < 0f) return false;
        ratio = Math.Min(r, 1f);
        ageMs = age;
        return true;
    }

    // ── v94: a web panel over the game window (number provisional) ───────────

    /// <summary>
    /// v94: <see cref="OpenWebPanel"/>, <see cref="NavigateWebPanel"/>, <see cref="CloseWebPanel"/> and
    /// <see cref="GetWebPanelState"/>. True only on an engine at API 94 or newer that fills all four.
    /// </summary>
    public bool HasWebPanel => _api.Version >= 94 && _api.WebPanelOpenFn != IntPtr.Zero && _api.WebPanelNavigateFn != IntPtr.Zero
                            && _api.WebPanelCloseFn != IntPtr.Zero && _api.WebPanelGetStateFn != IntPtr.Zero;

    /// <summary>
    /// v94: opens the web panel over the game window (a second call replaces the first). Returns a handle
    /// &gt; 0, or a <see cref="WebPanelResult"/> (BadArgs for an empty URL or allowlist, more than 16
    /// entries or a newline inside one; Unavailable on an older engine). Never blocks; the panel loads
    /// after this returns - poll <see cref="GetWebPanelState"/>. Any thread.
    /// </summary>
    public int OpenWebPanel(WebPanelOptions options)
    {
        if (options == null || string.IsNullOrEmpty(options.Url) || options.Allowlist == null
            || options.Allowlist.Count < 1 || options.Allowlist.Count > 16)
            return (int)WebPanelResult.BadArgs;
        foreach (string entry in options.Allowlist)
            if (string.IsNullOrEmpty(entry) || entry.Contains('\n')) return (int)WebPanelResult.BadArgs;
        if (!HasWebPanel) return (int)WebPanelResult.Unavailable;

        string allowlist = string.Join("\n", options.Allowlist);
        string profile = options.ProfileKey ?? "";
        string title = options.Title ?? "";
        var enc = System.Text.Encoding.UTF8;
        int nUrl = enc.GetByteCount(options.Url), nAllow = enc.GetByteCount(allowlist);
        int nProfile = enc.GetByteCount(profile), nTitle = enc.GetByteCount(title);
        int total = nUrl + nAllow + nProfile + nTitle + 4;

        byte[]? rented = null;
        Span<byte> buf = total <= 1024 ? stackalloc byte[total] : (rented = System.Buffers.ArrayPool<byte>.Shared.Rent(total)).AsSpan(0, total);
        try
        {
            int o = 0;
            int oUrl = o; o += enc.GetBytes(options.Url, buf[o..]); buf[o++] = 0;
            int oAllow = o; o += enc.GetBytes(allowlist, buf[o..]); buf[o++] = 0;
            int oProfile = o; o += enc.GetBytes(profile, buf[o..]); buf[o++] = 0;
            int oTitle = o; o += enc.GetBytes(title, buf[o..]); buf[o++] = 0;
            fixed (byte* b = buf)
            {
                var open = new WebPanelOpenNative
                {
                    Size = (uint)sizeof(WebPanelOpenNative),
                    Flags = (uint)options.Flags,
                    Url = b + oUrl,
                    Allowlist = b + oAllow,
                    ProfileKey = nProfile == 0 ? null : b + oProfile,
                    Title = nTitle == 0 ? null : b + oTitle,
                    WidthPercent = options.WidthPercent,
                    HeightPercent = options.HeightPercent,
                };
                return ((delegate* unmanaged[Cdecl]<WebPanelOpenNative*, int>)_api.WebPanelOpenFn)(&open);
            }
        }
        finally
        {
            if (rented != null) System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// v94: points the panel <paramref name="handle"/> at <paramref name="url"/>. 1 queued; -2
    /// (UrlNotAllowed) when it is off the allowlist (the engine opens it in the system browser when that is
    /// safe); -6 (StaleHandle) when the handle is not the live panel. Never blocks. Any thread.
    /// </summary>
    public int NavigateWebPanel(int handle, string url)
    {
        if (string.IsNullOrEmpty(url)) return (int)WebPanelResult.BadArgs;
        if (!HasWebPanel) return (int)WebPanelResult.Unavailable;
        int n = System.Text.Encoding.UTF8.GetByteCount(url);
        byte[]? rented = null;
        Span<byte> buf = n + 1 <= 1024 ? stackalloc byte[n + 1] : (rented = System.Buffers.ArrayPool<byte>.Shared.Rent(n + 1)).AsSpan(0, n + 1);
        try
        {
            System.Text.Encoding.UTF8.GetBytes(url, buf);
            buf[n] = 0;
            fixed (byte* p = buf)
                return ((delegate* unmanaged[Cdecl]<int, byte*, int>)_api.WebPanelNavigateFn)(handle, p);
        }
        finally
        {
            if (rented != null) System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>v94: closes the panel <paramref name="handle"/>. 1 queued, 0 already closed, -6 stale handle. Never blocks. Any thread.</summary>
    public int CloseWebPanel(int handle)
    {
        if (!HasWebPanel) return (int)WebPanelResult.Unavailable;
        return ((delegate* unmanaged[Cdecl]<int, int>)_api.WebPanelCloseFn)(handle);
    }

    /// <summary>
    /// v94: where the panel <paramref name="handle"/> is. A handle that is not the live panel reads
    /// Superseded. None (and a default <paramref name="state"/>) on an older engine or bad arguments.
    /// Any thread.
    /// </summary>
    public WebPanelStatus GetWebPanelState(int handle, out WebPanelState state)
    {
        state = default;
        if (!HasWebPanel) return WebPanelStatus.None;
        WebPanelStateNative native = default;
        native.Size = (uint)sizeof(WebPanelStateNative);
        int r = ((delegate* unmanaged[Cdecl]<int, WebPanelStateNative*, int>)_api.WebPanelGetStateFn)(handle, &native);
        if (r < 0) return WebPanelStatus.None;
        var status = (WebPanelStatus)native.Status;
        state = new WebPanelState(native.Handle, status, (WebPanelStateFlags)native.Flags, (WebPanelResult)native.Error, native.LastHttpStatus, native.NavigationCount);
        return status;
    }

    private int CallSetActionKey(string action, string key, int op, out string message)
    {
        message = string.Empty;
        if (!HasKeyBinding) { message = "This RynthCore can't change keys."; return -1; }
        if (string.IsNullOrEmpty(action) || action.Length > 64 || string.IsNullOrEmpty(key) || key.Length > 40) { message = "No action or key."; return -1; }
        Span<byte> a = stackalloc byte[260];
        Span<byte> k = stackalloc byte[164];
        Span<byte> msg = stackalloc byte[512];
        int an = System.Text.Encoding.UTF8.GetBytes(action, a[..^1]);
        a[an] = 0;
        int kn = System.Text.Encoding.UTF8.GetBytes(key, k[..^1]);
        k[kn] = 0;
        int r;
        fixed (byte* ap = a)
        fixed (byte* kp = k)
        fixed (byte* mp = msg)
            r = ((delegate* unmanaged[Cdecl]<byte*, byte*, int, byte*, int, int>)_api.SetActionKeyFn)(ap, kp, op, mp, msg.Length);
        int n = msg.IndexOf((byte)0);
        message = System.Text.Encoding.UTF8.GetString(msg[..(n < 0 ? msg.Length : n)]);
        return r;
    }

    private int CallWithKey(IntPtr fn, string key, int arg)
    {
        if (!HasFramelessWindows || fn == IntPtr.Zero || string.IsNullOrEmpty(key) || key.Length > 64) return -1;
        Span<byte> utf8 = stackalloc byte[260];
        int n = System.Text.Encoding.UTF8.GetBytes(key, utf8[..^1]);
        utf8[n] = 0;
        fixed (byte* p = utf8)
            return ((delegate* unmanaged[Cdecl]<byte*, int, int>)fn)(p, arg);
    }

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

    public bool TryGetObjectAttribute2ndBaseLevel(uint objectId, uint stype2nd, out uint value)
    {
        value = 0;
        if (_api.GetObjectAttribute2ndBaseLevelFn == IntPtr.Zero)
            return false;

        fixed (uint* valuePtr = &value)
        {
            return ((delegate* unmanaged[Cdecl]<uint, uint, uint*, int>)_api.GetObjectAttribute2ndBaseLevelFn)(objectId, stype2nd, valuePtr) != 0;
        }
    }

    public bool TryGetPlayerBaseVitals(out uint baseMaxHp, out uint baseMaxStam, out uint baseMaxMana)
    {
        baseMaxHp = baseMaxStam = baseMaxMana = 0;
        if (_api.GetPlayerBaseVitalsFn == IntPtr.Zero)
            return false;

        fixed (uint* hpPtr = &baseMaxHp)
        fixed (uint* stamPtr = &baseMaxStam)
        fixed (uint* manaPtr = &baseMaxMana)
        {
            return ((delegate* unmanaged[Cdecl]<uint*, uint*, uint*, int>)_api.GetPlayerBaseVitalsFn)(hpPtr, stamPtr, manaPtr) != 0;
        }
    }

    public bool TryGetObjectQuadProperty(uint objectId, uint stype, out long value)
    {
        value = 0;
        if (_api.GetObjectQuadPropertyFn == IntPtr.Zero)
            return false;

        fixed (long* valuePtr = &value)
        {
            return ((delegate* unmanaged[Cdecl]<uint, uint, long*, int>)_api.GetObjectQuadPropertyFn)(objectId, stype, valuePtr) != 0;
        }
    }

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

    /// <summary>
    /// Returns palette subpalette data (sorted by offset) for an object captured at CreateObject time.
    /// subIds[i] = palette DID; offsets[i] = range-start offset (slot index).
    /// Returns total count (may exceed maxCount), or -1 if no data captured.
    /// </summary>
    public int GetObjectPalettes(uint objectId, uint[] subIds, uint[] offsets, int maxCount)
    {
        if (_api.GetObjectPalettesFn == IntPtr.Zero || subIds == null || offsets == null || maxCount <= 0)
            return -1;

        IntPtr subIdsBuf = Marshal.AllocHGlobal(maxCount * sizeof(uint));
        IntPtr offsetsBuf = Marshal.AllocHGlobal(maxCount * sizeof(uint));
        try
        {
            int result = ((delegate* unmanaged[Cdecl]<uint, uint*, uint*, int, int>)_api.GetObjectPalettesFn)(
                objectId, (uint*)subIdsBuf, (uint*)offsetsBuf, maxCount);

            int count = result > 0 ? Math.Min(result, maxCount) : 0;
            uint* sp = (uint*)subIdsBuf;
            uint* op = (uint*)offsetsBuf;
            for (int i = 0; i < count; i++)
            {
                subIds[i] = sp[i];
                offsets[i] = op[i];
            }
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(subIdsBuf);
            Marshal.FreeHGlobal(offsetsBuf);
        }
    }

    // ─── Status export / cross-plugin bridges (v64) ─────────────────────────

    /// <summary>
    /// Returns the engine-side per-client status fields as a JSON object string
    /// (everything in the status snapshot EXCEPT the bot sub-object), or null if
    /// unavailable. A generic "here are my own metrics" accessor. The native
    /// buffer is freed on the next call on this thread, so this copies it before
    /// returning. Requires engine API v64+ (check <see cref="HasGetEngineStatusJson"/>).
    /// </summary>
    public string? GetEngineStatusJson()
    {
        if (_api.GetEngineStatusJsonFn == IntPtr.Zero)
            return null;

        IntPtr ptr = ((delegate* unmanaged[Cdecl]<IntPtr>)_api.GetEngineStatusJsonFn)();
        return ptr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ptr);
    }

    /// <summary>
    /// Returns the named plugin's snapshot JSON (its RynthPluginGetSnapshotJson
    /// export), brokered by the engine so the caller never resolves a sibling
    /// plugin's exports directly. Returns null when that plugin isn't loaded or
    /// produced no snapshot. The native buffer is owned by the target plugin
    /// (valid until its next snapshot call), so this copies it before returning.
    /// Requires API v64+ (check <see cref="HasGetPluginSnapshotJson"/>).
    /// </summary>
    public string? GetPluginSnapshotJson(string pluginName)
    {
        if (_api.GetPluginSnapshotJsonFn == IntPtr.Zero || string.IsNullOrEmpty(pluginName))
            return null;

        IntPtr namePtr = Marshal.StringToHGlobalAnsi(pluginName);
        try
        {
            IntPtr ptr = ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)_api.GetPluginSnapshotJsonFn)(namePtr);
            return ptr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(namePtr);
        }
    }

    /// <summary>
    /// Generic sibling of <see cref="GetPluginSnapshotJson"/>: brokers a parameterless JSON-getter
    /// export (convention RynthPluginGet*Json) on the named plugin and returns its JSON. Used to pull
    /// a plugin's secondary surfaces (e.g. RynthAi's RynthPluginGetInventoryJson) without GetProcAddress.
    /// The target owns the returned buffer (valid until its next call on that export), so this copies it.
    /// Returns null pre-v66 or on failure. Requires API v66+ (check <see cref="HasGetPluginExportJson"/>).
    /// </summary>
    /// <summary>
    /// A named plugin's typed interface table (v68): <paramref name="iface"/> at
    /// <paramref name="version"/>, e.g. ("RynthAi", "RynthAi.Script", 1). The pointer belongs to
    /// the target plugin; call its functions only from the plugin pump thread (OnTick and the
    /// events it drains). IntPtr.Zero when the plugin isn't loaded, isn't initialised yet, or
    /// doesn't offer that interface: resolve lazily and again after each Init.
    /// </summary>
    public IntPtr GetPluginInterface(string pluginName, string iface, uint version)
    {
        if (_api.Version < 68 || _api.GetPluginInterfaceFn == IntPtr.Zero || string.IsNullOrEmpty(pluginName) || string.IsNullOrEmpty(iface))
            return IntPtr.Zero;
        IntPtr namePtr = Marshal.StringToHGlobalAnsi(pluginName);
        IntPtr ifacePtr = Marshal.StringToHGlobalAnsi(iface);
        try
        {
            return ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, IntPtr>)_api.GetPluginInterfaceFn)(namePtr, ifacePtr, version);
        }
        finally
        {
            Marshal.FreeHGlobal(namePtr);
            Marshal.FreeHGlobal(ifacePtr);
        }
    }

    /// <summary>
    /// v69: every object the client currently knows (landscape, inventory, everything in its
    /// object table), from the engine's snapshot (refreshed on the game thread a few times a
    /// second). Empty on older engines or before the first snapshot.
    /// </summary>
    public uint[] GetLiveObjectIds()
    {
        if (_api.Version < 69 || _api.GetLiveObjectIdsFn == IntPtr.Zero)
            return Array.Empty<uint>();
        var fn = (delegate* unmanaged[Cdecl]<uint*, int, int>)_api.GetLiveObjectIdsFn;
        int capacity = 1024;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint[] buffer = new uint[capacity];
            int total;
            fixed (uint* p = buffer) total = fn(p, capacity);
            if (total <= 0) return Array.Empty<uint>();
            if (total <= capacity)
            {
                if (total == capacity) return buffer;
                Array.Resize(ref buffer, total);
                return buffer;
            }
            capacity = total + 256;
        }
        return Array.Empty<uint>();
    }

    /// <summary>
    /// v70: the most recent server UseDone (GameEvent 0x01C7): <paramref name="seq"/> is its
    /// number in the same count <see cref="GetUseDoneSeq"/> returns, <paramref name="error"/> its
    /// WeenieError code (0 = the action completed, e.g. 0x1D YoureTooBusy). Both come from one
    /// snapshot. False on older engines or when the engine can't watch UseDone; seq 0 = none yet.
    /// </summary>
    public bool TryGetLastUseDone(out int seq, out uint error)
    {
        seq = 0;
        error = 0;
        if (_api.Version < 70 || _api.GetLastUseDoneFn == IntPtr.Zero)
            return false;
        int s;
        uint e;
        int ok = ((delegate* unmanaged[Cdecl]<int*, uint*, int>)_api.GetLastUseDoneFn)(&s, &e);
        seq = s;
        error = e;
        return ok != 0;
    }

    /// <summary>
    /// v70: the most recent refusal the server sent: <paramref name="eventType"/> 0x028A
    /// WeenieError (e.g. 0x402 YourSpellFizzled; a fizzled cast still ends with UseDone(0)),
    /// 0x028B WeenieErrorWithString, or 0x00A0 InventoryServerSaveFailed (a refused wield or
    /// move; <paramref name="objectId"/> is the item, else 0). <paramref name="seq"/> counts them
    /// (0 = none yet): record it before an action and compare. False on older engines.
    /// </summary>
    public bool TryGetLastWeenieError(out int seq, out uint error, out uint eventType, out uint objectId)
    {
        seq = 0;
        error = eventType = objectId = 0;
        if (_api.Version < 70 || _api.GetLastWeenieErrorFn == IntPtr.Zero)
            return false;
        int s;
        uint e, t, o;
        int ok = ((delegate* unmanaged[Cdecl]<int*, uint*, uint*, uint*, int>)_api.GetLastWeenieErrorFn)(&s, &e, &t, &o);
        seq = s;
        error = e;
        eventType = t;
        objectId = o;
        return ok != 0;
    }

    /// <summary>
    /// v70: wield an item into the given EquipMask slot(s), as dragging it onto that paperdoll
    /// slot does (the 0x001A GetAndWieldItem game action). E.g. 0x00100000 MeleeWeapon,
    /// 0x00200000 Shield, 0x00400000 MissileWeapon, 0x01000000 Held, 0x02000000 TwoHanded;
    /// rings and bracelets have a left and a right bit. The server checks the slot against the
    /// item and answers with the wield or InventoryServerSaveFailed (see
    /// <see cref="TryGetLastWeenieError"/>). Check <see cref="HasWieldItem"/>.
    /// </summary>
    public bool WieldItem(uint objectId, uint equipMask)
    {
        return _api.Version >= 70 && _api.WieldItemFn != IntPtr.Zero &&
               ((delegate* unmanaged[Cdecl]<uint, uint, int>)_api.WieldItemFn)(objectId, equipMask) != 0;
    }

    /// <summary>
    /// v76: close an external container (a corpse, a chest) the way the client does when its
    /// window is closed: the 0x0195 NoLongerViewingContents game action. It is not an inventory
    /// request, so it adds nothing to the busy count and doesn't hold the client's one pending
    /// item request. The server closes the container (if this player is viewing it) and answers
    /// with CloseGroundContainer, so <c>OnStopViewingObjectContents</c> follows. Queued on AC's
    /// main thread ahead of any action queued after it (a following UseObject goes out after
    /// it, in the same frame). False when the engine predates v76. Check <see cref="HasCloseContainer"/>.
    /// </summary>
    public bool CloseContainer(uint containerId)
    {
        return HasCloseContainer &&
               ((delegate* unmanaged[Cdecl]<uint, int>)_api.CloseContainerFn)(containerId) != 0;
    }

    /// <summary>
    /// v77: the client's screen, the UIFlow mode (<see cref="RynthScreenMode"/>): the world,
    /// character select, the intro... <see cref="RynthScreenMode.Unknown"/> before the engine
    /// has seen one, and on an engine before v77 (check <see cref="HasUiHooks"/>).
    /// <paramref name="previousMode"/> is the screen before it. Any thread.
    /// </summary>
    public int GetScreenMode(out int previousMode)
    {
        previousMode = 0;
        if (!HasUiHooks) return 0;
        int prev = 0;
        int mode = ((delegate* unmanaged[Cdecl]<int*, int>)_api.GetScreenModeFn)(&prev);
        previousMode = prev;
        return mode;
    }

    /// <summary>
    /// v77: which UI hooks are live on this client (<see cref="RynthUiHookFlags"/>), i.e. which of
    /// the v77 callbacks can arrive. <see cref="RynthUiHookFlags.None"/> on an engine before v77.
    /// Any thread.
    /// </summary>
    public RynthUiHookFlags GetUiHookFlags()
        => HasUiHooks ? (RynthUiHookFlags)((delegate* unmanaged[Cdecl]<uint>)_api.GetUiHookFlagsFn)() : RynthUiHookFlags.None;

    // ─── Spending experience (API v79) ─────────────────────────────────────
    // See TrainingTypes.cs. The engine reads the player's numbers on AC's main thread every
    // 2 s while a plugin keeps asking (TryGetTrainingInfo), and raises only against numbers
    // at most info.StaleAfterMs old: a Stale result means "ask again in a moment".

    /// <summary>
    /// v79: unassigned XP, credits and, in <paramref name="entries"/> (may be null), one entry per
    /// attribute, vital and skill with the XP for +1 and +10. <paramref name="entryCount"/> = the
    /// entries there are (at most entries.Length are written). False on an engine before v79 or
    /// while there are no numbers yet (not in the world, or the first read is still coming).
    /// Any thread.
    /// </summary>
    public bool TryGetTrainingInfo(out TrainingInfoNative info, TrainingEntryNative[]? entries, out int entryCount)
    {
        info = default;
        entryCount = 0;
        if (!HasTraining) return false;
        TrainingInfoNative raw = default;
        raw.Size = (uint)sizeof(TrainingInfoNative);
        var fn = (delegate* unmanaged[Cdecl]<TrainingInfoNative*, TrainingEntryNative*, int, int>)_api.GetTrainingInfoFn;
        int n;
        if (entries is { Length: > 0 })
            fixed (TrainingEntryNative* p = entries) n = fn(&raw, p, entries.Length);
        else
            n = fn(&raw, null, 0);
        info = raw;
        entryCount = Math.Max(0, n);
        return raw.Size >= 8 && (raw.Flags & TrainingInfoFlags.HaveNumbers) != 0 && n > 0;
    }

    /// <summary>
    /// v79: raise one attribute (1..6), vital (1 health, 3 stamina, 5 mana) or trained/specialized
    /// skill by <paramref name="ranks"/> (1..100) with unassigned XP, like the character window's
    /// "+". <paramref name="expectedXp"/> is the cost the user confirmed (the entry's CostOne /
    /// CostTen); the engine refuses with <see cref="RaiseResult.CostChanged"/> when the cost now
    /// differs (0 skips that check). <paramref name="xpSent"/> = the XP sent. Any thread.
    /// </summary>
    public RaiseResult Raise(TrainingKind kind, uint stype, uint ranks, long expectedXp, out long xpSent)
    {
        xpSent = 0;
        if (!HasTraining) return RaiseResult.EngineTooOld;
        long sent = 0;
        int r = ((delegate* unmanaged[Cdecl]<uint, uint, uint, long, long*, int>)_api.RaiseFn)((uint)kind, stype, ranks, expectedXp, &sent);
        xpSent = sent;
        return (RaiseResult)r;
    }

    /// <summary>
    /// v79: train an untrained skill with skill credits (exactly the entry's TrainCredits, which
    /// the caller passes as <paramref name="expectedCredits"/>; 0 skips that check). Any thread.
    /// </summary>
    public RaiseResult TrainSkill(uint stype, int expectedCredits)
        => HasTraining
            ? (RaiseResult)((delegate* unmanaged[Cdecl]<uint, int, int>)_api.TrainSkillFn)(stype, expectedCredits)
            : RaiseResult.EngineTooOld;

    // ─── Slot order and placed moves (API v80) ─────────────────────────────
    // The server keeps every container's items in an order (the pack grid on screen); the engine
    // follows it from the login message, a pack's contents list and every item move. A container
    // is only known once a full list for it was seen: then and only then is there an order.

    /// <summary>
    /// v80: the items in <paramref name="containerId"/> in real slot order (slot 0 first), or with
    /// <paramref name="slotKind"/> the side packs and foci in the main pack (the pack column, top
    /// first). <paramref name="count"/> = how many there are; the buffer grows to fit. False on an
    /// engine before v80 or when the order isn't known (no full list for that container yet).
    /// Any thread.
    /// </summary>
    public bool TryGetContainerOrder(uint containerId, bool slotKind, ref uint[] buffer, out int count)
    {
        count = 0;
        if (!HasContainerOrder || containerId == 0) return false;
        buffer ??= new uint[64];
        var fn = (delegate* unmanaged[Cdecl]<uint, int, uint*, int, int>)_api.GetContainerOrderFn;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int n;
            fixed (uint* p = buffer) n = fn(containerId, slotKind ? 1 : 0, buffer.Length > 0 ? p : null, buffer.Length);
            if (n < 0) return false;
            if (n <= buffer.Length) { count = n; return true; }
            buffer = new uint[Math.Max(n, buffer.Length * 2)];
        }
        return false;
    }

    /// <summary>
    /// v80: moves <paramref name="itemId"/> into <paramref name="containerId"/> at slot
    /// <paramref name="placement"/> (the index it should end at, counted without it), like a drop on
    /// a cell of the pack grid; within one container it reorders. The server decides and answers as
    /// for any move. 1 sent or queued, 0 refused before sending, -1 the sender isn't bound on this
    /// client, -2 an engine before v80. Any thread (queued to AC's main thread).
    /// </summary>
    public int PutItemInContainer(uint itemId, uint containerId, int placement)
        => HasPutItemInContainer
            ? ((delegate* unmanaged[Cdecl]<uint, uint, int, int>)_api.PutItemInContainerFn)(itemId, containerId, placement)
            : -2;

    // ─── Salvage without the panel (API v82) ───────────────────────────────

    /// <summary>
    /// v82: salvages <paramref name="itemIds"/> with the UST <paramref name="toolId"/> without opening
    /// AC's salvage panel: the engine sends the 0x027D salvage request through AC's own sender, the
    /// same game action the panel's Salvage button sends. Salvage bags in the list are merged with the
    /// new salvage. At most <see cref="MaxSalvageDirectItems"/> ids; none zero, none the tool. The
    /// server checks the UST is carried, salvages each item it can (in the packs, not worn, not
    /// retained, with a material) and skips the rest without a word; its answer is the items leaving
    /// the pack, new salvage bags and a "You obtain ..." chat line. Returns the count sent or queued,
    /// 0 refused before sending, -1 the sender isn't bound on this client, -2 an engine before v82.
    /// Any thread (queued to AC's main thread).
    /// </summary>
    // ─── Ghost creatures (API v83) ─────────────────────────────────────────

    /// <summary>
    /// v83: milliseconds since the server last sent a message about <paramref name="objectId"/>
    /// (create, position, motion, vector, state, parent, pickup, effects, object description,
    /// update object). -1 when it sent none since the engine started watching (or the object was
    /// deleted), -2 an engine before v83. A monster fighting anyone near the player gets motion
    /// updates every few seconds; one that sends nothing for a long time while it is attacked and
    /// no swing at it ever starts is a ghost. Any thread.
    /// </summary>
    public int GetObjectServerAge(uint objectId)
        => HasGetObjectServerAge
            ? ((delegate* unmanaged[Cdecl]<uint, int>)_api.GetObjectServerAgeFn)(objectId)
            : -2;

    /// <summary>
    /// v83: removes a ghost creature from the client the way a server DeleteObject would (plugins
    /// get OnDeleteObject). Refused for the player, players, anything that isn't a creature, and an
    /// object the server sent a message about in the last 5 s. A real object comes back with the
    /// server's next CreateObject for it. Returns 1 removed, 2 queued for AC's main thread, 0
    /// refused, -1 the client's DeleteObject isn't bound yet, -2 an engine before v83. Any thread.
    /// </summary>
    public int RemoveClientObject(uint objectId)
        => HasRemoveClientObject
            ? ((delegate* unmanaged[Cdecl]<uint, int>)_api.RemoveClientObjectFn)(objectId)
            : -2;

    // ─── Every property of an object (API v84) ─────────────────────────────

    /// <summary>
    /// v84: every property the engine knows for <paramref name="objectId"/> (int, int64, bool,
    /// float, string, data id, instance id and the spell book), merged from the client's
    /// CreateObject copy, its qualities tables, the last identify and the server's property
    /// updates, the way the per-key reads (TryGetObjectIntProperty, ...) answer. False on an
    /// engine before v84 (fall back to per-key reads) or when the engine knows nothing about
    /// the object. Item details need an identify first (<see cref="RequestId"/>, then
    /// <see cref="ObjectProperties.HasIdData"/>). When <see cref="ObjectProperties.QualitiesPending"/>
    /// is set, the client's qualities tables are read at AC's next main-thread tick: ask again
    /// shortly. Any thread.
    /// </summary>
    public bool TryGetObjectProperties(uint objectId, out ObjectProperties? properties)
    {
        properties = null;
        if (!HasGetObjectProperties || objectId == 0) return false;
        var fn = (delegate* unmanaged[Cdecl]<uint, byte*, int, int>)_api.GetObjectPropertiesFn;
        byte[] buffer = new byte[8192];
        for (int attempt = 0; attempt < 4; attempt++)
        {
            int n;
            fixed (byte* p = buffer) n = fn(objectId, p, buffer.Length);
            if (n <= 0) return false;
            if (n <= buffer.Length)
                return ObjectProperties.TryParse(new ReadOnlySpan<byte>(buffer, 0, n), out properties);
            buffer = new byte[Math.Max(n + 1024, buffer.Length * 2)];   // grew between calls: some room
        }
        return false;
    }

    public int SalvageDirect(uint toolId, ReadOnlySpan<uint> itemIds)
    {
        if (!HasSalvageDirect) return -2;
        if (toolId == 0 || itemIds.Length == 0 || itemIds.Length > MaxSalvageDirectItems) return 0;
        fixed (uint* p = itemIds)
            return ((delegate* unmanaged[Cdecl]<uint, uint*, int, int>)_api.SalvageDirectFn)(toolId, p, itemIds.Length);
    }

    /// <summary>
    /// v78: which reassembled server messages this plugin wants on its
    /// <c>RynthPluginOnServerMessage(uint opcode, byte* data, int length)</c> export (Cdecl;
    /// see <see cref="Net.ServerMessage"/> for the export and <see cref="Net.ServerOpcode"/> /
    /// <see cref="Net.GameEventType"/> for the ids the SDK parses). Replaces the previous set;
    /// two empty lists turn it off. <paramref name="opcodes"/>: top-level opcodes, 0xF7B0 for
    /// every game event, <see cref="Net.ServerOpcode.All"/> for everything.
    /// <paramref name="gameEvents"/>: single game event types. Call from Init, Tick or an event
    /// handler (the engine identifies the plugin by the call). Returns 1 streaming, 2 accepted
    /// but the engine's stream is switched off, 0 no export, -1 unknown caller, -3 bad
    /// arguments, -4 engine predates v78. Check <see cref="HasServerMessages"/>.
    /// </summary>
    public int SetServerMessageInterest(ReadOnlySpan<uint> opcodes, ReadOnlySpan<uint> gameEvents)
    {
        if (!HasServerMessages)
            return -4;
        fixed (uint* ops = opcodes)
        fixed (uint* evs = gameEvents)
        {
            return ((delegate* unmanaged[Cdecl]<uint*, int, uint*, int, int>)_api.SetServerMessageInterestFn)(
                ops, opcodes.Length, evs, gameEvents.Length);
        }
    }

    /// <summary>
    /// v71: submits this plugin's complete set of script windows (display-list format 1,
    /// RynthSuite Docs/RYNTHLUA_WINDOWS_DESIGN.md §4.2). Call only from the plugin's own tick or
    /// event (else -2). The engine copies what it needs before returning. Returns 0, or -1
    /// malformed, -2 not in a dispatch, -3 over a limit (nothing applied), -4 unsupported
    /// format; -100 on an engine without <see cref="HasUi"/>.
    /// </summary>
    public int UiSubmit(ReadOnlySpan<byte> data)
    {
        if (!HasUi) return -100;
        fixed (byte* p = data)
            return ((delegate* unmanaged[Cdecl]<byte*, int, int>)_api.UiSubmitFn)(p, data.Length);
    }

    /// <summary>
    /// v71: copies whole queued window events (format 1, §4.3) into <paramref name="buffer"/> and
    /// returns the bytes written; <paramref name="remaining"/> = events still queued. Same thread
    /// rule as <see cref="UiSubmit"/>. 0 on an engine without <see cref="HasUi"/>.
    /// </summary>
    public int UiPollEvents(Span<byte> buffer, out int remaining)
    {
        remaining = 0;
        if (!HasUi) return 0;
        int rem = 0;
        int written;
        fixed (byte* p = buffer)
            written = ((delegate* unmanaged[Cdecl]<byte*, int, int*, int>)_api.UiPollEventsFn)(p, buffer.Length, &rem);
        remaining = rem;
        return written < 0 ? 0 : written;
    }

    /// <summary>
    /// v71: limits and layout facts for script windows (text line height, ASCII advances for
    /// CalcTextSize, display size). False on an engine without <see cref="HasUi"/> or with no
    /// ImGui (e.g. Decal coexistence mode).
    /// </summary>
    public bool TryGetUiInfo(out UiInfoNative info)
    {
        info = default;
        if (!HasUi) return false;
        UiInfoNative local = default;
        local.Size = (uint)sizeof(UiInfoNative);
        int n = ((delegate* unmanaged[Cdecl]<UiInfoNative*, int>)_api.UiGetInfoFn)(&local);
        info = local;
        return n > 0;
    }

    // ─── Player-to-player trade (API v72) ──────────────────────────────────
    // The engine tracks the trade window from the server's trade events (on AC's main
    // thread) and serves copies; poll TryGetTradeState from your tick and compare
    // Generation / Sequence / the counters with your last read to see what changed.
    // The actions send the retail client's own trade game actions, queued for AC's
    // main thread; the server's answer shows up in the next state reads.

    /// <summary>
    /// v72: the trade window right now. False on an engine without <see cref="HasTrade"/>.
    /// A closed trade still returns true (IsOpen false) with the counters.
    /// </summary>
    public bool TryGetTradeState(out TradeState state)
    {
        state = default;
        if (!HasTrade) return false;
        TradeStateNative raw = default;
        raw.Size = (uint)sizeof(TradeStateNative);
        int n = ((delegate* unmanaged[Cdecl]<TradeStateNative*, int>)_api.GetTradeStateFn)(&raw);
        if (n < 8) return false;
        state = new TradeState(raw);
        return true;
    }

    /// <summary>
    /// v72: the item ids on one side of the trade window (<see cref="TradeSide.You"/> or
    /// <see cref="TradeSide.Partner"/>); empty when none, or on an older engine.
    /// </summary>
    public uint[] GetTradeItems(TradeSide side)
    {
        if (!HasTrade) return Array.Empty<uint>();
        var fn = (delegate* unmanaged[Cdecl]<int, uint*, int, int>)_api.GetTradeItemsFn;
        int capacity = 32;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            uint[] buffer = new uint[capacity];
            int total;
            fixed (uint* p = buffer) total = fn((int)side, p, capacity);
            if (total <= 0) return Array.Empty<uint>();
            if (total <= capacity)
            {
                if (total < capacity) Array.Resize(ref buffer, total);
                return buffer;
            }
            capacity = total + 16;
        }
        return Array.Empty<uint>();
    }

    /// <summary>v72: ask a player to trade (the server walks you into range first).</summary>
    public bool TradeOpen(uint targetId)
        => HasTrade && ((delegate* unmanaged[Cdecl]<uint, int>)_api.TradeOpenFn)(targetId) != 0;

    /// <summary>
    /// v72: put one of your items in the open trade window (slot 0 = next free). The server
    /// answers with the item on your side, or a TradeFailure (FailureCount / LastFailure*).
    /// </summary>
    public bool TradeAdd(uint itemId, uint slot = 0)
        => HasTrade && ((delegate* unmanaged[Cdecl]<uint, uint, int>)_api.TradeAddFn)(itemId, slot) != 0;

    /// <summary>v72: accept the trade as it stands (the trade window's Accept button).</summary>
    public bool TradeAccept()
        => HasTrade && ((delegate* unmanaged[Cdecl]<int>)_api.TradeAcceptFn)() != 0;

    /// <summary>v72: withdraw your acceptance (the Decline button).</summary>
    public bool TradeDecline()
        => HasTrade && ((delegate* unmanaged[Cdecl]<int>)_api.TradeDeclineFn)() != 0;

    /// <summary>v72: empty both sides of the trade window.</summary>
    public bool TradeReset()
        => HasTrade && ((delegate* unmanaged[Cdecl]<int>)_api.TradeResetFn)() != 0;

    /// <summary>v72: end the trade.</summary>
    public bool TradeClose()
        => HasTrade && ((delegate* unmanaged[Cdecl]<int>)_api.TradeCloseFn)() != 0;

    public string? GetPluginExportJson(string pluginName, string exportName)
    {
        if (_api.GetPluginExportJsonFn == IntPtr.Zero || string.IsNullOrEmpty(pluginName) || string.IsNullOrEmpty(exportName))
            return null;

        IntPtr namePtr = Marshal.StringToHGlobalAnsi(pluginName);
        IntPtr exportPtr = Marshal.StringToHGlobalAnsi(exportName);
        try
        {
            IntPtr ptr = ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)_api.GetPluginExportJsonFn)(namePtr, exportPtr);
            return ptr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(namePtr);
            Marshal.FreeHGlobal(exportPtr);
        }
    }

    /// <summary>
    /// Forwards a (action,value) command to the named plugin via the engine
    /// broker (its RynthPluginApplyRemoteCommand export). The receiving plugin
    /// copies the args and applies them on its OWN pump/main thread. Returns true
    /// if delivered. Requires API v64+ (check <see cref="HasSendPluginCommand"/>).
    /// </summary>
    public bool SendPluginCommand(string pluginName, string action, string value)
    {
        if (_api.SendPluginCommandFn == IntPtr.Zero || string.IsNullOrEmpty(pluginName) || string.IsNullOrEmpty(action))
            return false;

        IntPtr namePtr = Marshal.StringToHGlobalAnsi(pluginName);
        IntPtr actionPtr = Marshal.StringToHGlobalAnsi(action);
        IntPtr valuePtr = Marshal.StringToHGlobalAnsi(value ?? string.Empty);
        try
        {
            return ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int>)_api.SendPluginCommandFn)(
                namePtr, actionPtr, valuePtr) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(namePtr);
            Marshal.FreeHGlobal(actionPtr);
            Marshal.FreeHGlobal(valuePtr);
        }
    }

    // ─── Vendor trading (API v67) ──────────────────────────────────────────
    // Decal's WorldFilter.OpenVendor + Actions.VendorBuyAll/VendorSellAll shape as
    // stateless calls (VendorCart adds the Add/Clear list shape on top). Reads come
    // from a snapshot the engine takes on AC's main thread when the vendor list
    // arrives. Buy/sell are checked, queued, re-checked on AC's main thread (funds,
    // pack slots, burden, ownership) and sent through the client's own
    // gmVendorUI::SendShopEvent. One transaction is in flight at a time: wait for
    // GetVendorTradeStatus to finish before sending the next batch.

    /// <summary>True when the engine exposes vendor trading (API v67+).</summary>
    public bool HasVendorTrade => _api.Version >= 67
                                  && _api.GetVendorInfoFn != IntPtr.Zero && _api.GetVendorItemsFn != IntPtr.Zero
                                  && _api.VendorBuyFn != IntPtr.Zero && _api.VendorSellFn != IntPtr.Zero
                                  && _api.GetVendorTradeStatusFn != IntPtr.Zero;

    /// <summary>The vendor that is open right now, or false if none (or pre-v67 engine).</summary>
    public bool TryGetVendorInfo(out VendorInfo info)
    {
        info = null!;
        if (_api.Version < 67 || _api.GetVendorInfoFn == IntPtr.Zero)
            return false;

        VendorInfoNative raw;
        if (((delegate* unmanaged[Cdecl]<VendorInfoNative*, int>)_api.GetVendorInfoFn)(&raw) == 0)
            return false;

        info = new VendorInfo
        {
            VendorId = raw.VendorId,
            Name = ReadAnsi(raw.Name, 64),
            Generation = raw.Generation,
            ShopMode = raw.ShopMode,
            ItemTypes = raw.ItemTypes,
            MinValue = raw.MinValue,
            MaxValue = raw.MaxValue,
            DealsMagic = raw.DealsMagic != 0,
            BuyRate = raw.BuyRate,
            SellRate = raw.SellRate,
            AltCurrencyWcid = raw.AltCurrencyWcid,
            AltCurrencyName = ReadAnsi(raw.AltCurrencyName, 64),
            AltCurrencyServerCount = raw.AltCurrencyServerCount,
            AltCurrencyHave = raw.AltCurrencyHave,
            PlayerCoins = raw.PlayerCoins,
            ItemCount = raw.ItemCount,
            TradingAvailable = (raw.Flags & 1u) != 0,
            TradeInFlight = (raw.Flags & 2u) != 0,
        };
        return true;
    }

    /// <summary>The open vendor's items; empty if no vendor is open (or pre-v67 engine).</summary>
    public VendorItem[] GetVendorItems()
    {
        if (_api.Version < 67 || _api.GetVendorItemsFn == IntPtr.Zero)
            return Array.Empty<VendorItem>();

        var fn = (delegate* unmanaged[Cdecl]<VendorItemNative*, int, int>)_api.GetVendorItemsFn;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int total = fn(null, 0);
            if (total <= 0)
                return Array.Empty<VendorItem>();

            int capacity = total + 8;   // headroom in case the list is re-sent between calls
            IntPtr buf = Marshal.AllocHGlobal(capacity * sizeof(VendorItemNative));
            try
            {
                VendorItemNative* items = (VendorItemNative*)buf;
                int now = fn(items, capacity);
                if (now < 0)
                    return Array.Empty<VendorItem>();
                if (now > capacity)
                    continue;   // grew past the headroom; size again

                var result = new VendorItem[now];
                for (int i = 0; i < now; i++)
                {
                    VendorItemNative* it = items + i;
                    result[i] = new VendorItem
                    {
                        ObjectId = it->ObjectId,
                        Wcid = it->Wcid,
                        Name = ReadAnsi(it->Name, 64),
                        ItemType = it->ItemType,
                        IconId = it->IconId,
                        Amount = it->Amount,
                        StackSize = it->StackSize,
                        MaxStackSize = it->MaxStackSize,
                        Value = it->Value,
                        UnitValue = it->UnitValue,
                        UnitPrice = it->UnitPrice,
                        Burden = it->Burden,
                        Unlimited = (it->Flags & 1u) != 0,
                        Stackable = (it->Flags & 2u) != 0,
                        NeedsContainerSlot = (it->Flags & 4u) != 0,
                    };
                }
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        return Array.Empty<VendorItem>();
    }

    /// <summary>
    /// Buy from the open vendor (Decal VendorBuyAll). vendorId 0 = whichever vendor is
    /// open; pass the id you read to make sure it's still that vendor. Returns the request
    /// id, or 0 if refused (reason in <see cref="TryGetVendorTradeStatus"/>).
    /// </summary>
    public uint VendorBuy(uint vendorId, IReadOnlyList<VendorTradeEntryNative> items)
    {
        if (_api.Version < 67 || _api.VendorBuyFn == IntPtr.Zero || items == null || items.Count == 0)
            return 0;

        int n = items.Count;
        IntPtr buf = Marshal.AllocHGlobal(n * sizeof(VendorTradeEntryNative));
        try
        {
            VendorTradeEntryNative* p = (VendorTradeEntryNative*)buf;
            for (int i = 0; i < n; i++)
                p[i] = items[i];
            return ((delegate* unmanaged[Cdecl]<uint, VendorTradeEntryNative*, int, uint>)_api.VendorBuyFn)(vendorId, p, n);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>Buy <paramref name="amount"/> of one vendor item.</summary>
    public uint VendorBuy(uint objectId, int amount, uint vendorId = 0) =>
        VendorBuy(vendorId, new[] { new VendorTradeEntryNative(objectId, amount) });

    /// <summary>
    /// Sell your own items (whole stacks) to the open vendor (Decal VendorSellAll).
    /// Items must be in your packs, not equipped, and of a type the vendor buys.
    /// Returns the request id, or 0 if refused.
    /// </summary>
    public uint VendorSell(uint vendorId, IReadOnlyList<uint> itemIds)
    {
        if (_api.Version < 67 || _api.VendorSellFn == IntPtr.Zero || itemIds == null || itemIds.Count == 0)
            return 0;

        int n = itemIds.Count;
        IntPtr buf = Marshal.AllocHGlobal(n * sizeof(uint));
        try
        {
            uint* p = (uint*)buf;
            for (int i = 0; i < n; i++)
                p[i] = itemIds[i];
            return ((delegate* unmanaged[Cdecl]<uint, uint*, int, uint>)_api.VendorSellFn)(vendorId, p, n);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>Sell one of your items.</summary>
    public uint VendorSell(uint itemId) => VendorSell(0, new[] { itemId });

    /// <summary>State of the most recent buy/sell request (check RequestId is yours).</summary>
    public bool TryGetVendorTradeStatus(out VendorTradeStatus status)
    {
        status = null!;
        if (_api.Version < 67 || _api.GetVendorTradeStatusFn == IntPtr.Zero)
            return false;

        VendorTradeStatusNative raw;
        if (((delegate* unmanaged[Cdecl]<VendorTradeStatusNative*, int>)_api.GetVendorTradeStatusFn)(&raw) == 0)
            return false;

        status = new VendorTradeStatus
        {
            RequestId = raw.RequestId,
            State = (VendorTradeState)raw.State,
            Result = (VendorTradeResult)raw.Result,
            IsBuy = raw.IsBuy != 0,
            VendorId = raw.VendorId,
            EntryCount = raw.EntryCount,
            Estimate = raw.Estimate,
            Message = ReadAnsi(raw.Message, 128),
        };
        return true;
    }

    private static string ReadAnsi(byte* p, int capacity)
    {
        int len = 0;
        while (len < capacity && p[len] != 0)
            len++;
        if (len == 0)
            return string.Empty;
        var chars = new char[len];
        for (int i = 0; i < len; i++)
            chars[i] = (char)p[i];   // Latin-1, the client's single-byte names
        return new string(chars);
    }
}
