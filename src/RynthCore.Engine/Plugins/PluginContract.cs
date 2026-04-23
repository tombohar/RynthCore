// ============================================================================
//  RynthCore.Engine - Plugins/PluginContract.cs
//  Defines the C ABI contract between RynthCore and plugin DLLs.
//
//  A plugin DLL must export at least:
//    int  RynthPluginInit(RynthCoreAPI* api)   — return 0 on success
//    void RynthPluginShutdown()
//
//  Optional exports:
//    const char* RynthPluginName()           — human-readable name
//    const char* RynthPluginVersion()        — version string (e.g. "1.0.0")
//    void        RynthPluginTick()           — per-frame logic (before render)
//    void        RynthPluginRender()         — per-frame ImGui drawing
// ============================================================================

using System;
using System.Runtime.InteropServices;

namespace RynthCore.Engine.Plugins;

/// <summary>
/// The host API struct passed to every plugin at init time.
/// Plugins receive a pointer to this and can call back into RynthCore.
/// Layout must stay ABI-stable — append new fields at the end only.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RynthCoreAPI
{
    /// <summary>API version. Plugins should check this before using later fields.</summary>
    public uint Version;

    /// <summary>ImGui context pointer — plugin must call igSetCurrentContext before drawing.</summary>
    public IntPtr ImGuiContext;

    /// <summary>IDirect3DDevice9* — the game's active D3D device.</summary>
    public IntPtr D3DDevice;

    /// <summary>HWND of the game window.</summary>
    public IntPtr GameHwnd;

    /// <summary>Function pointer: void Log(const char* message)</summary>
    public IntPtr LogFn;

    /// <summary>Function pointer: void ProbeClientHooks()</summary>
    public IntPtr ProbeClientHooksFn;

    /// <summary>Function pointer: uint GetClientHookFlags()</summary>
    public IntPtr GetClientHookFlagsFn;

    /// <summary>Function pointer: int ChangeCombatMode(int combatMode)</summary>
    public IntPtr ChangeCombatModeFn;

    /// <summary>Function pointer: int CancelAttack()</summary>
    public IntPtr CancelAttackFn;

    /// <summary>Function pointer: int QueryHealth(uint targetId)</summary>
    public IntPtr QueryHealthFn;

    /// <summary>Function pointer: int MeleeAttack(uint targetId, int attackHeight, float powerLevel)</summary>
    public IntPtr MeleeAttackFn;

    /// <summary>Function pointer: int MissileAttack(uint targetId, int attackHeight, float accuracyLevel)</summary>
    public IntPtr MissileAttackFn;

    /// <summary>Function pointer: int DoMovement(uint motion, float speed, int holdKey)</summary>
    public IntPtr DoMovementFn;

    /// <summary>Function pointer: int StopMovement(uint motion, int holdKey)</summary>
    public IntPtr StopMovementFn;

    /// <summary>Function pointer: int JumpNonAutonomous(float extent)</summary>
    public IntPtr JumpNonAutonomousFn;

    /// <summary>Function pointer: int SetAutonomyLevel(uint level)</summary>
    public IntPtr SetAutonomyLevelFn;

    /// <summary>Function pointer: int SetAutoRun(int enabled)</summary>
    public IntPtr SetAutoRunFn;

    /// <summary>Function pointer: int TapJump()</summary>
    public IntPtr TapJumpFn;

    /// <summary>Function pointer: void SetIncomingChatSuppression(int enabled)</summary>
    public IntPtr SetIncomingChatSuppressionFn;

    /// <summary>Function pointer: int SelectItem(uint objectId)</summary>
    public IntPtr SelectItemFn;

    /// <summary>Function pointer: int SetSelectedObjectId(uint objectId)</summary>
    public IntPtr SetSelectedObjectIdFn;

    /// <summary>Function pointer: uint GetSelectedItemId()</summary>
    public IntPtr GetSelectedItemIdFn;

    /// <summary>Function pointer: uint GetPreviousSelectedItemId()</summary>
    public IntPtr GetPreviousSelectedItemIdFn;

    /// <summary>Function pointer: uint GetPlayerId()</summary>
    public IntPtr GetPlayerIdFn;

    /// <summary>Function pointer: uint GetGroundContainerId()</summary>
    public IntPtr GetGroundContainerIdFn;

    /// <summary>Function pointer: int GetNumContainedItems(uint objectId)</summary>
    public IntPtr GetNumContainedItemsFn;

    /// <summary>Function pointer: int GetNumContainedContainers(uint objectId)</summary>
    public IntPtr GetNumContainedContainersFn;

    /// <summary>Function pointer: int GetCurCoords(double* northSouth, double* eastWest)</summary>
    public IntPtr GetCurCoordsFn;

    /// <summary>Function pointer: int UseObject(uint objectId)</summary>
    public IntPtr UseObjectFn;

    /// <summary>Function pointer: int UseObjectOn(uint sourceObjectId, uint targetObjectId)</summary>
    public IntPtr UseObjectOnFn;

    /// <summary>Function pointer: int UseEquippedItem(uint sourceObjectId, uint targetObjectId)</summary>
    public IntPtr UseEquippedItemFn;

    /// <summary>Function pointer: int MoveItemExternal(uint objectId, uint targetContainerId, int amount)</summary>
    public IntPtr MoveItemExternalFn;

    /// <summary>Function pointer: int MoveItemInternal(uint objectId, uint targetContainerId, int slot, int amount)</summary>
    public IntPtr MoveItemInternalFn;

    /// <summary>Function pointer: int WriteToChat(const wchar_t* textUtf16, int chatType)</summary>
    public IntPtr WriteToChatFn;

    /// <summary>Function pointer: int GetPlayerPose(uint* objCellId, float* x, float* y, float* z, float* qw, float* qx, float* qy, float* qz)</summary>
    public IntPtr GetPlayerPoseFn;

    /// <summary>Function pointer: int IsPortaling() — returns 1 if SmartBox::teleport_in_progress, 0 otherwise</summary>
    public IntPtr IsPortalingFn;

    /// <summary>Function pointer: int SetMotion(uint motion, int enabled)</summary>
    public IntPtr SetMotionFn;

    /// <summary>Function pointer: int StopCompletely()</summary>
    public IntPtr StopCompletelyFn;

    /// <summary>Function pointer: int TurnToHeading(float headingDegrees)</summary>
    public IntPtr TurnToHeadingFn;

    /// <summary>Function pointer: int GetPlayerHeading(float* headingDegrees)</summary>
    public IntPtr GetPlayerHeadingFn;

    /// <summary>Function pointer: const char* GetObjectName(uint objectId)</summary>
    public IntPtr GetObjectNameFn;

    /// <summary>Function pointer: int GetPlayerVitals(uint* health, uint* maxHealth, uint* stamina, uint* maxStamina, uint* mana, uint* maxMana)</summary>
    public IntPtr GetPlayerVitalsFn;

    /// <summary>Function pointer: int GetObjectPosition(uint objectId, uint* objCellId, float* x, float* y, float* z)</summary>
    public IntPtr GetObjectPositionFn;

    /// <summary>Function pointer: int RequestId(uint objectId) — sends appraisal/identify request to server</summary>
    public IntPtr RequestIdFn;

    /// <summary>Function pointer: int GetTargetVitals(uint objectId, uint* health, uint* maxHealth, uint* stamina, uint* maxStamina, uint* mana, uint* maxMana)</summary>
    public IntPtr GetTargetVitalsFn;

    /// <summary>Function pointer: int CastSpell(uint targetId, int spellId)</summary>
    public IntPtr CastSpellFn;

    /// <summary>Function pointer: int GetItemType(uint objectId, uint* typeFlags)</summary>
    public IntPtr GetItemTypeFn;

    /// <summary>Function pointer: int GetObjectIntProperty(uint objectId, uint stype, int* value)</summary>
    public IntPtr GetObjectIntPropertyFn;

    /// <summary>Function pointer: int GetObjectBoolProperty(uint objectId, uint stype, int* value) — returns 1 if property exists, value is 0/1</summary>
    public IntPtr GetObjectBoolPropertyFn;

    /// <summary>Function pointer: int ObjectIsAttackable(uint objectId) — calls ClientCombatSystem::ObjectIsAttackable, returns 1 if attackable</summary>
    public IntPtr ObjectIsAttackableFn;

    /// <summary>Function pointer: int GetObjectSkill(uint objectId, uint skillStype, int* base, int* training)
    /// base = InitialLevel + LevelFromPracticePoints (no enchantments). training: 0=Unusable,1=Untrained,2=Trained,3=Specialized.</summary>
    public IntPtr GetObjectSkillFn;

    /// <summary>Function pointer: int IsSpellKnown(uint objectId, uint spellId) — returns 1 if in spell book</summary>
    public IntPtr IsSpellKnownFn;

    /// <summary>
    /// Function pointer: int ReadPlayerEnchantments(uint* spellIds, double* expiryTimes, int maxCount)
    /// Fills arrays with active player enchantments. expiryTimes are in server-time seconds.
    /// Returns number written, or -1 if not available (not logged in / no registry).
    /// </summary>
    public IntPtr ReadPlayerEnchantmentsFn;

    /// <summary>
    /// Function pointer: double GetServerTime()
    /// Returns estimated current server time in seconds. Returns 0 if no time sync received yet.
    /// </summary>
    public IntPtr GetServerTimeFn;

    /// <summary>
    /// Function pointer: int ReadObjectEnchantments(uint objectId, uint* spellIds, double* expiryTimes, int maxCount)
    /// Reads enchantments from any game object (armor, weapon, etc.) by ID.
    /// Returns number written, 0 if no enchantments, -1 if object not found or has no registry.
    /// </summary>
    public IntPtr ReadObjectEnchantmentsFn;

    /// <summary>
    /// Function pointer: int WorldToScreen(float worldX, float worldY, float worldZ, float* screenX, float* screenY)
    /// Projects a 3D game-world position to 2D screen coordinates.
    /// World coordinates are in the same space as GetObjectPosition returns.
    /// Returns 1 if on screen, 0 if behind camera or unavailable.
    /// </summary>
    public IntPtr WorldToScreenFn;

    /// <summary>
    /// Function pointer: int GetViewportSize(uint* width, uint* height)
    /// Returns the current D3D9 viewport dimensions.
    /// Returns 1 on success, 0 if not available yet.
    /// </summary>
    public IntPtr GetViewportSizeFn;

    /// <summary>
    /// Function pointer: void Nav3DClear()
    /// Clears the 3D nav marker submission buffer. Call once per frame before adding markers.
    /// </summary>
    public IntPtr Nav3DClearFn;

    /// <summary>
    /// Function pointer: void Nav3DAddRing(float wx, float wy, float wz, float radius, float thickness, uint colorArgb)
    /// Submits a flat 3D ring at the given world position. Coordinates are D3D: X=EW, Y=height, Z=NS.
    /// Color is ARGB (D3DCOLOR format).
    /// </summary>
    public IntPtr Nav3DAddRingFn;

    /// <summary>
    /// Function pointer: void Nav3DAddLine(float x1, float y1, float z1, float x2, float y2, float z2, float thickness, uint colorArgb)
    /// Submits a flat 3D line between two world positions. Coordinates are D3D: X=EW, Y=height, Z=NS.
    /// </summary>
    public IntPtr Nav3DAddLineFn;

    /// <summary>
    /// Function pointer: int InvokeChatParser(const wchar_t* textUtf16)
    /// Passes text to the AC outgoing chat parser as if the player typed it.
    /// Returns 1 on success, 0 if the hook is not installed yet.
    /// </summary>
    public IntPtr InvokeChatParserFn;

    /// <summary>
    /// Function pointer: int GetObjectDoubleProperty(uint objectId, uint stype, double* value)
    /// Reads an STypeFloat (double) property from any game object.
    /// Returns 1 on success, 0 if undefined.
    /// </summary>
    public IntPtr GetObjectDoublePropertyFn;

    /// <summary>
    /// Function pointer: int GetObjectQuadProperty(uint objectId, uint stype, __int64* value)
    /// Reads an STypeInt64 (quad) property from any game object.
    /// Returns 1 on success, 0 if undefined.
    /// </summary>
    public IntPtr GetObjectQuadPropertyFn;

    /// <summary>
    /// Function pointer: int GetObjectAttribute2ndBaseLevel(uint objectId, uint stype2nd, ulong* value)
    /// Reads the unbuffed base maximum vital via CACQualities::InqAttribute2ndBaseLevel.
    /// stype2nd: 1=MAX_HEALTH, 3=MAX_STAMINA, 5=MAX_MANA.
    /// Returns 1 on success, 0 if unavailable.
    /// </summary>
    public IntPtr GetObjectAttribute2ndBaseLevelFn;

    /// <summary>
    /// Function pointer: int GetPlayerBaseVitals(uint* baseMaxHp, uint* baseMaxStam, uint* baseMaxMana)
    /// Returns unbuffed base maximum vitals via InqAttribute2ndStruct(_initLevel + _levelFromCp).
    /// Excludes spell enchantments; includes base training, gear, and augmentations.
    /// Returns 1 on success, 0 if player qualities not yet available.
    /// </summary>
    public IntPtr GetPlayerBaseVitalsFn;

    /// <summary>
    /// Function pointer: IntPtr GetObjectStringProperty(uint objectId, uint stype)
    /// Returns pointer to ANSI string data, or IntPtr.Zero if undefined.
    /// </summary>
    public IntPtr GetObjectStringPropertyFn;

    /// <summary>
    /// Function pointer: int GetObjectWielderInfo(uint objectId, uint* wielderID, uint* location)
    /// Reads PublicWeenieDesc._wielderID and _location from the weenie struct.
    /// </summary>
    public IntPtr GetObjectWielderInfoFn;

    /// <summary>
    /// Function pointer: int NativeAttack(int attackHeight, float power)
    /// Uses the client's native combat pipeline (ClientCombatSystem) which handles
    /// turn-to-face and attack execution naturally. Requires target to be selected first.
    /// Returns 1 on success, 0 if not available.
    /// </summary>
    public IntPtr NativeAttackFn;

    /// <summary>
    /// Function pointer: int IsPlayerReady()
    /// Returns 1 if the player is in a ready position to begin an attack.
    /// </summary>
    public IntPtr IsPlayerReadyFn;

    /// <summary>
    /// Function pointer: void SetFpsLimit(int enabled, int focusedFps, int backgroundFps)
    /// Controls the engine-level EndScene frame governor.
    /// </summary>
    public IntPtr SetFpsLimitFn;

    /// <summary>
    /// Function pointer: int GetContainerContents(uint containerId, uint* itemIds, int maxCount)
    /// Writes contained item IDs into the provided buffer and returns the number written.
    /// </summary>
    public IntPtr GetContainerContentsFn;

    /// <summary>
    /// Function pointer: int GetObjectOwnershipInfo(uint objectId, uint* containerID, uint* wielderID, uint* location)
    /// Reads PublicWeenieDesc ownership fields from the weenie struct.
    /// </summary>
    public IntPtr GetObjectOwnershipInfoFn;

    /// <summary>
    /// Function pointer: int SplitStackInternal(uint objectId, uint targetContainerId, int slot, int amount)
    /// Moves a stack of items onto a specific slot in the target container, merging
    /// with any existing same-type stack at that slot. Unlike MoveItemInternal which
    /// sends opcode 0x19 (whole move, first empty slot), this sends opcode 0x55
    /// (StackableSplitToContainer) which honors the slot parameter.
    /// </summary>
    public IntPtr SplitStackInternalFn;

    /// <summary>
    /// Function pointer: int MergeStackInternal(uint sourceObjectId, uint targetObjectId)
    /// Merges two stacks of the same item type by sending opcode 0x1A (STACKABLE_MERGE).
    /// This is the real merge path used by drag-drop UI — opcode 0x55 (split-to-slot)
    /// creates new stacks instead of merging, so AutoStack must use this entry point.
    /// </summary>
    public IntPtr MergeStackInternalFn;

    /// <summary>
    /// Function pointer: int GetCurrentCombatMode()
    /// Reads the current combat mode directly from ClientCombatSystem in AC memory.
    /// Returns 1=NonCombat, 2=Melee, 4=Missile, 8=Magic.
    /// </summary>
    public IntPtr GetCurrentCombatModeFn;

    /// <summary>
    /// Function pointer: int SalvagePanelOpen(uint toolId)
    /// Calls CM_Inventory::SendNotice_OpenSalvagePanel to open the salvage panel
    /// for the given salvage tool. Returns 1 on success. The panel opens
    /// asynchronously — wait ~400 ms before calling SalvagePanelAddItem.
    /// </summary>
    public IntPtr SalvagePanelOpenFn;

    /// <summary>
    /// Function pointer: int SalvagePanelAddItem(uint itemId)
    /// Calls gmSalvageUI::AddNewItem to add an item to the open salvage panel.
    /// Requires the panel to have been opened at least once.
    /// Returns 1 on success, 0 if the gmSalvageUI instance is not yet captured.
    /// </summary>
    public IntPtr SalvagePanelAddItemFn;

    /// <summary>
    /// Function pointer: int SalvagePanelExecute()
    /// Calls gmSalvageUI::Salvage to execute the salvage operation.
    /// Requires the panel to have been opened at least once.
    /// Returns 1 on success, 0 if the gmSalvageUI instance is not yet captured.
    /// </summary>
    public IntPtr SalvagePanelExecuteFn;

    /// <summary>
    /// Function pointer: float GetVitae(uint playerId)
    /// Returns the player's vitae multiplier via CACQualities::GetVitaeValue.
    /// 1.0 = no vitae, 0.95 = 5% penalty. Returns 1.0 if unavailable.
    /// </summary>
    public IntPtr GetVitaeFn;

    /// <summary>
    /// Function pointer: const char* GetAccountName()
    /// Returns a pointer to the cached ANSI account name string, or IntPtr.Zero if not available.
    /// The pointer is valid until the next call.
    /// </summary>
    public IntPtr GetAccountNameFn;

    /// <summary>
    /// Function pointer: const char* GetWorldName()
    /// Returns a pointer to the cached UTF-8 world/server name string, or IntPtr.Zero if not available.
    /// The pointer is valid until the next call.
    /// </summary>
    public IntPtr GetWorldNameFn;

    /// <summary>
    /// Function pointer: uint GetObjectWcid(uint objectId)
    /// Returns the Weenie Class ID (WCID) from PublicWeenieDesc._wcid for the given object.
    /// Returns 0 if the object is not found or the phys_obj offset is not yet probed.
    /// </summary>
    public IntPtr GetObjectWcidFn;

    /// <summary>
    /// Function pointer: int HasAppraisalData(uint objectId)
    /// Returns 1 if a SendNotice_SetAppraiseInfo has been received for this guid this session, 0 otherwise.
    /// </summary>
    public IntPtr HasAppraisalDataFn;

    /// <summary>
    /// Function pointer: long GetLastIdTime(uint objectId)
    /// Returns the Unix timestamp (seconds) of the last appraisal receipt for this guid, or 0 if never.
    /// </summary>
    public IntPtr GetLastIdTimeFn;

    /// <summary>
    /// Function pointer: int GetObjectHeading(uint objectId, float* headingDegrees)
    /// Returns the object's facing direction (0–360°, clockwise, 0=North). Returns 1 on success, 0 on failure.
    /// </summary>
    public IntPtr GetObjectHeadingFn;

    /// <summary>
    /// Function pointer: int GetBusyState()
    /// Returns 0 if the character is idle, positive if a UI action is in progress.
    /// </summary>
    public IntPtr GetBusyStateFn;

    /// <summary>
    /// Function pointer: int GetObjectSpellIds(uint objectId, uint* spellIds, int maxCount)
    /// Fills spellIds with the spell book IDs from the last server appraisal for this object.
    /// Returns total count (may exceed maxCount), or -1 if no appraisal data is cached.
    /// Requires the player to have identified (RequestId) the object this session.
    /// </summary>
    public IntPtr GetObjectSpellIdsFn;

    /// <summary>Function pointer: int GetObjectSkillBuffed(uint objectId, uint skillStype, int* buffed)
    /// Returns the live buffed skill level (with spell enchantments) via InqSkill(raw=0).</summary>
    public IntPtr GetObjectSkillBuffedFn;

    /// <summary>Function pointer: int GetObjectAttribute(uint objectId, uint stype, int raw, uint* value)
    /// Reads a primary attribute via InqAttribute. raw=0→buffed, raw=1→base.
    /// stype: 1=Strength, 2=Endurance, 3=Quickness, 4=Coordination, 5=Focus, 6=Self.</summary>
    public IntPtr GetObjectAttributeFn;

    /// <summary>Function pointer: int GetObjectMotionOn(uint objectId, int* isOn)
    /// Returns 1 if a DoMotion On/Off state is known for the object; isOn is 1 if On (open), 0 if Off (closed).
    /// Returns 0 if no motion state has been observed for this object since injection.</summary>
    public IntPtr GetObjectMotionOnFn;

    /// <summary>Function pointer: int GetObjectState(uint objectId, uint* state)
    /// Returns 1 if a PhysicsState has been received from the server for this object; state is the raw bitfield.
    /// Returns 0 if no SetState message has been observed for this object since injection.</summary>
    public IntPtr GetObjectStateFn;

    /// <summary>Function pointer: uint GetObjectBitfield(uint objectId)
    /// Returns the PublicWeenieDesc._bitfield (ObjectDescriptionFlags) for the given object.
    /// BF_DOOR = 0x1000, BF_VENDOR = 0x200, BF_CORPSE = 0x2000, etc.
    /// Returns 0 if the object is not found.</summary>
    public IntPtr GetObjectBitfieldFn;

    /// <summary>Function pointer: void ForceResetBusyCount()
    /// Force-resets the client's ClientUISystem busy count to zero.
    /// Use after portal teleports that interrupt actions and leave the hourglass cursor stuck.</summary>
    public IntPtr ForceResetBusyCountFn;

    /// <summary>Function pointer: int GetObjectPalettes(uint objectId, uint* subIds, uint* offsets, int maxCount)
    /// Fills subIds and offsets with the item's ObjDesc subpalette data (sorted by offset).
    /// Returns total subpalette count, or -1 if no data was captured for this object.
    /// subIds[i] is the palette DID; offsets[i] is the range-start offset (used as slot index).</summary>
    public IntPtr GetObjectPalettesFn;

    /// <summary>Function pointer: int CommenceJump()
    /// Calls ClientCombatSystem::CommenceJump (spacebar key-down). Starts the
    /// power-bar charge cycle. Pair with DoJump(autonomous) to release.</summary>
    public IntPtr CommenceJumpFn;

    /// <summary>Function pointer: int DoJump(int autonomous)
    /// Calls ClientCombatSystem::DoJump (spacebar key-up). Releases a jump
    /// at the current power-bar level. Pass autonomous=1 for the normal
    /// player-driven jump (matches keyboard behavior).</summary>
    public IntPtr DoJumpFn;

    /// <summary>Function pointer: int LaunchJumpWithMotion(int shift, int w, int x, int z, int c)
    /// Writes motion vector directly into CMotionInterp, calls DoJump(1), then
    /// clears the motion. This is the only way to jump with forward/back/strafe
    /// momentum — SetMotion does not bake velocity into the physics sim in time
    /// for DoJump. Mirrors UB's UBHelper.Jumper algorithm.</summary>
    public IntPtr LaunchJumpWithMotionFn;

    /// <summary>Function pointer: int GetRadarRect(int* x0, int* y0, int* x1, int* y1)
    /// Returns the retail gmRadarUI element's current screen rect in pixels
    /// (x0,y0 top-left, x1,y1 bottom-right exclusive). Returns 1 on success, 0
    /// if the radar has not rendered yet this session. Requires API v53+.</summary>
    public IntPtr GetRadarRectFn;
}



/// <summary>Current API version. Bump when adding fields to RynthCoreAPI.</summary>
internal static class PluginContractVersion
{
    public const uint Current = 53;
}

internal static class ClientActionHookFlags
{
    public const uint CombatInitialized = 1u << 0;
    public const uint MovementInitialized = 1u << 1;
    public const uint MeleeAttack = 1u << 2;
    public const uint MissileAttack = 1u << 3;
    public const uint ChangeCombatMode = 1u << 4;
    public const uint CancelAttack = 1u << 5;
    public const uint QueryHealth = 1u << 6;
    public const uint DoMovement = 1u << 7;
    public const uint StopMovement = 1u << 8;
    public const uint JumpNonAutonomous = 1u << 9;
    public const uint SetAutonomyLevel = 1u << 10;
    public const uint SetAutoRun = 1u << 11;
    public const uint TapJump = 1u << 12;
    public const uint SetMotion = 1u << 13;
    public const uint StopCompletely = 1u << 14;
    public const uint TurnToHeading = 1u << 15;
    public const uint GetPlayerHeading = 1u << 16;
}

// ─── Delegate types matching the plugin's exported functions ────────────

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int PluginInitDelegate(ref RynthCoreAPI api);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginShutdownDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate IntPtr PluginNameDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate IntPtr PluginVersionDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginOnLoginCompleteDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginOnUIInitializedDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginOnBusyCountIncrementedDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginOnBusyCountDecrementedDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginOnSelectedTargetChangeDelegate(uint currentTargetId, uint previousTargetId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginOnCombatModeChangeDelegate(int currentCombatMode, int previousCombatMode);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginOnSmartBoxEventDelegate(uint opcode, uint blobSize, uint status);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginOnDeleteObjectDelegate(uint objectId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginOnCreateObjectDelegate(uint objectId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginOnUpdateObjectDelegate(uint objectId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginOnUpdateObjectInventoryDelegate(uint objectId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginOnViewObjectContentsDelegate(uint objectId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginOnStopViewingObjectContentsDelegate(uint objectId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginOnVendorOpenDelegate(uint vendorId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginOnVendorCloseDelegate(uint vendorId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginOnUpdateHealthDelegate(uint targetId, float healthRatio, uint currentHealth, uint maxHealth);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginOnChatWindowTextDelegate(IntPtr textUtf16, int chatType, IntPtr eatFlag);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginOnChatBarEnterDelegate(IntPtr textUtf16, IntPtr eatFlag);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginBarActionDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginTickDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginRenderDelegate();

// ─── Log callback that plugins can call ─────────────────────────────────

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void LogCallbackDelegate(IntPtr messageUtf8);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void ProbeClientHooksCallbackDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate uint GetClientHookFlagsCallbackDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int ChangeCombatModeCallbackDelegate(int combatMode);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int CancelAttackCallbackDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int QueryHealthCallbackDelegate(uint targetId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int MeleeAttackCallbackDelegate(uint targetId, int attackHeight, float powerLevel);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int MissileAttackCallbackDelegate(uint targetId, int attackHeight, float accuracyLevel);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int NativeAttackCallbackDelegate(int attackHeight, float power);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int IsPlayerReadyCallbackDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int IsPortalingCallbackDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void SetFpsLimitCallbackDelegate(int enabled, int focusedFps, int backgroundFps);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int DoMovementCallbackDelegate(uint motion, float speed, int holdKey);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int StopMovementCallbackDelegate(uint motion, int holdKey);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int JumpNonAutonomousCallbackDelegate(float extent);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int SetAutonomyLevelCallbackDelegate(uint level);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int SetAutoRunCallbackDelegate(int enabled);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int TapJumpCallbackDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int CommenceJumpCallbackDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int DoJumpCallbackDelegate(int autonomous);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int LaunchJumpWithMotionCallbackDelegate(int shift, int w, int x, int z, int c);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int SetMotionCallbackDelegate(uint motion, int enabled);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int StopCompletelyCallbackDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int TurnToHeadingCallbackDelegate(float headingDegrees);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetPlayerHeadingCallbackDelegate(float* headingDegrees);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void SetIncomingChatSuppressionCallbackDelegate(int enabled);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int SelectItemCallbackDelegate(uint objectId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int SetSelectedObjectIdCallbackDelegate(uint objectId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate uint GetItemIdCallbackDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetCurCoordsCallbackDelegate(double* northSouth, double* eastWest);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int UseObjectCallbackDelegate(uint objectId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int UseObjectOnCallbackDelegate(uint sourceObjectId, uint targetObjectId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int UseEquippedItemCallbackDelegate(uint sourceObjectId, uint targetObjectId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int MoveItemExternalCallbackDelegate(uint objectId, uint targetContainerId, int amount);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int MoveItemInternalCallbackDelegate(uint objectId, uint targetContainerId, int slot, int amount);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int SplitStackInternalCallbackDelegate(uint objectId, uint targetContainerId, int slot, int amount);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int MergeStackInternalCallbackDelegate(uint sourceObjectId, uint targetObjectId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int WriteToChatCallbackDelegate(IntPtr textUtf16, int chatType);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetPlayerPoseCallbackDelegate(
    uint* objCellId,
    float* x,
    float* y,
    float* z,
    float* qw,
    float* qx,
    float* qy,
    float* qz);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate IntPtr GetObjectNameCallbackDelegate(uint objectId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetPlayerVitalsCallbackDelegate(
    uint* health,
    uint* maxHealth,
    uint* stamina,
    uint* maxStamina,
    uint* mana,
    uint* maxMana);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetObjectPositionCallbackDelegate(
    uint objectId,
    uint* objCellId,
    float* x,
    float* y,
    float* z);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int RequestIdCallbackDelegate(uint objectId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetTargetVitalsCallbackDelegate(
    uint objectId,
    uint* health,
    uint* maxHealth,
    uint* stamina,
    uint* maxStamina,
    uint* mana,
    uint* maxMana);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int CastSpellCallbackDelegate(uint targetId, int spellId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetItemTypeCallbackDelegate(uint objectId, uint* typeFlags);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetObjectIntPropertyCallbackDelegate(uint objectId, uint stype, int* value);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetObjectBoolPropertyCallbackDelegate(uint objectId, uint stype, int* value);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int ObjectIsAttackableCallbackDelegate(uint objectId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetObjectSkillCallbackDelegate(uint objectId, uint skillStype, int* buffed, int* training);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int IsSpellKnownCallbackDelegate(uint objectId, uint spellId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginOnEnchantmentAddedDelegate(uint spellId, double durationSeconds);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginOnEnchantmentRemovedDelegate(uint enchantmentId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int ReadPlayerEnchantmentsCallbackDelegate(uint* spellIds, double* expiryTimes, int maxCount);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate double GetServerTimeCallbackDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int ReadObjectEnchantmentsCallbackDelegate(uint objectId, uint* spellIds, double* expiryTimes, int maxCount);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int WorldToScreenCallbackDelegate(float worldX, float worldY, float worldZ, float* screenX, float* screenY);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetViewportSizeCallbackDelegate(uint* width, uint* height);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void Nav3DClearCallbackDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void Nav3DAddRingCallbackDelegate(float wx, float wy, float wz, float radius, float thickness, uint colorArgb);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void Nav3DAddLineCallbackDelegate(float x1, float y1, float z1, float x2, float y2, float z2, float thickness, uint colorArgb);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int InvokeChatParserCallbackDelegate(IntPtr textUtf16);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetObjectDoublePropertyCallbackDelegate(uint objectId, uint stype, double* value);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetObjectQuadPropertyCallbackDelegate(uint objectId, uint stype, long* value);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetObjectAttribute2ndBaseLevelCallbackDelegate(uint objectId, uint stype2nd, uint* value);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetPlayerBaseVitalsCallbackDelegate(uint* baseMaxHp, uint* baseMaxStam, uint* baseMaxMana);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate IntPtr GetObjectStringPropertyCallbackDelegate(uint objectId, uint stype);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetObjectWielderInfoCallbackDelegate(uint objectId, uint* wielderID, uint* location);
internal unsafe delegate int GetContainerContentsCallbackDelegate(uint containerId, uint* itemIds, int maxCount);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetObjectOwnershipInfoCallbackDelegate(uint objectId, uint* containerID, uint* wielderID, uint* location);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int GetCurrentCombatModeCallbackDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int SalvagePanelOpenCallbackDelegate(uint toolId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int SalvagePanelAddItemCallbackDelegate(uint itemId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int SalvagePanelExecuteCallbackDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate float GetVitaeCallbackDelegate(uint playerId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate IntPtr GetAccountNameCallbackDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate IntPtr GetWorldNameCallbackDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate uint GetObjectWcidCallbackDelegate(uint objectId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int HasAppraisalDataCallbackDelegate(uint objectId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate long GetLastIdTimeCallbackDelegate(uint objectId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetObjectHeadingCallbackDelegate(uint objectId, float* headingDegrees);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int GetBusyStateCallbackDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetObjectSpellIdsCallbackDelegate(uint guid, uint* spellIds, int maxCount);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetObjectSkillLevelCallbackDelegate(uint objectId, uint skillStype, int raw, int* level);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetObjectAttributeCallbackDelegate(uint objectId, uint stype, int raw, uint* value);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetObjectMotionOnCallbackDelegate(uint objectId, int* isOn);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetObjectStateCallbackDelegate(uint objectId, uint* state);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate uint GetObjectBitfieldCallbackDelegate(uint objectId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void ForceResetBusyCountCallbackDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetObjectPalettesCallbackDelegate(uint objectId, uint* subIds, uint* offsets, int maxCount);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetRadarRectCallbackDelegate(int* x0, int* y0, int* x1, int* y1);
