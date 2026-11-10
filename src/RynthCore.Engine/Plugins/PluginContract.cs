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
//    void        RynthPluginRender()         — per-frame ImGui drawing (ImGui shell on)
//    void        RynthPluginRenderOverlay()  — per-frame ImGui drawing while the ImGui shell is
//                                              off (Avalonia mode): only windows that have no
//                                              Avalonia panel. Gated by engine.json
//                                              "EnablePluginOverlayWindows" (default true).
//    int         RynthPluginRenderEmbed(const char* surface, float width, float height,
//                                       float* wantWidth, float* wantHeight)
//                                            — draws a named surface (UTF-8, e.g. RynthAi's
//                                              "miniremote") into the engine's current ImGui
//                                              window on AC's render thread, inside a
//                                              width x height child; reports the size it wants.
//                                              1 = drawn, 0 = not ready / unknown surface,
//                                              -2 = failed (it caught its own exception): its
//                                              surfaces go off for the session, as for a throw
//                                              (engines before 2026-10-21 read -2 as 0). Leave
//                                              ImGui's stacks as found; the engine puts back
//                                              what a plugin window leaves open, and logs it.
//    void        RynthPluginOnServerMessage(uint opcode, byte* data, int length)
//                                            — v78, reassembled server messages the plugin
//                                              asked for with SetServerMessageInterestFn
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

    /// <summary>Function pointer: void SetRadarSuppressed(int enabled)
    /// When enabled=1, the engine skips the original gmRadarUI::DrawObjects call
    /// so the vanilla radar stops rendering and a plugin can own that rect.
    /// Requires API v54+.</summary>
    public IntPtr SetRadarSuppressedFn;

    /// <summary>Function pointer: void SetChatSuppressed(int enabled)
    /// When enabled=1, the engine calls UIElement::SetVisible(false) on the
    /// gmMainChatUI each frame, hiding the retail chatbox entirely.
    /// Requires API v55+.</summary>
    public IntPtr SetChatSuppressedFn;

    /// <summary>Function pointer: void SetPowerbarSuppressed(int enabled)
    /// When enabled=1, the engine no-ops gmPowerbarUI's RecvNotice_(Begin/
    /// Level/Finish), so the retail attack/magic power bar never renders.
    /// Requires API v56+.</summary>
    public IntPtr SetPowerbarSuppressedFn;

    /// <summary>Function pointer: int GetCastBusyState()
    /// 0 = clear to cast, 1 = a cast/action gesture is animating. This is the
    /// REAL cast gate (CMotionInterp sequenced-motion queue), distinct from
    /// GetBusyState (the ClientUISystem hourglass, which reads 0 while AC still
    /// rejects a cast with "You're too busy!"). Sampled on the plugin pump; no
    /// acclient.exe hook. Returns 0 when not reachable. Requires API v58+.</summary>
    public IntPtr GetCastBusyStateFn;

    /// <summary>Function pointer: int ReadKnownSpells(uint* spellIds, int maxCount)
    /// Fills the array with the character's known spell ids from a main-thread
    /// spellbook snapshot. Returns count written, or -1 if unavailable (cold
    /// snapshot / not logged in). Requires API v59+.</summary>
    public IntPtr ReadKnownSpellsFn;

    /// <summary>Function pointer:
    /// void Nav3DAddTriangle(float x1, float y1, float z1,
    ///                       float x2, float y2, float z2,
    ///                       float x3, float y3, float z3,
    ///                       uint colorArgb)
    /// Submits a filled 3D triangle in world coordinates. Coordinates are D3D:
    /// X=EW, Y=height, Z=NS. Use this instead of three Nav3DAddLine calls when
    /// you want a face that conforms exactly to a terrain triangle (slope
    /// passability overlay). Requires API v60+.</summary>
    public IntPtr Nav3DAddTriangleFn;

    /// <summary>Function pointer:
    /// void Nav3DAddRingEx(float wx, float wy, float wz, float radius,
    ///                     float thickness, float height, uint colorArgb)
    /// Same as Nav3DAddRing but with an explicit cylinder-wall height in
    /// world units. Use when you want a tall but thin ring (radar range
    /// marker) without coupling visual thickness to wall height. Requires
    /// API v61+.</summary>
    public IntPtr Nav3DAddRingExFn;

    /// <summary>Function pointer: int GiveObjectTo(uint objectId, uint targetId, int amount)
    /// Gives an item to an NPC/player via CM_Inventory::Event_GiveObjectRequest
    /// (the F7B1 give GameAction). amount=0 gives the whole object. This is the
    /// correct give-to-NPC primitive; MoveItemExternal is move-to-container and
    /// does NOT give. Requires API v62+. APPENDED-AT-END for ABI safety.</summary>
    public IntPtr GiveObjectToFn;

    /// <summary>Function pointer: int GetUseDoneSeq()
    /// Monotonic count of inbound server UseDone (GameEvent 0x01C7) events. The
    /// server sends UseDone when it FINISHES an action (cast/use) — completed
    /// (WeenieError.None) or refused (e.g. YoureTooBusy). A plugin records this
    /// at cast time and watches for it to change to know the server resolved the
    /// cast, so combat casts can be paced on real completion instead of a blind
    /// interval (which re-fires into the deferred-windup window and orphans the
    /// cast). Read-only observation; never touches client m_cBusy. Returns 0 when
    /// unavailable. Requires API v63+. APPENDED-AT-END for ABI safety.</summary>
    public IntPtr GetUseDoneSeqFn;

    /// <summary>Function pointer: const char* GetEngineStatusJson()
    /// Returns the engine-side per-client status fields (host/pid/account/character/server/
    /// uptime/fps/pluginTicksPerSec/workingSet/inWorld/queueDropped/reconciles/forceClears/
    /// deaths/vitae/xp+lum rates/burden/area/lastIssue) as an ANSI JSON object, EXCLUDING the
    /// bot sub-object. A generic "here are my own metrics" accessor — benign, not a remote
    /// feature. The returned pointer is valid until the next call on the same thread. Returns
    /// IntPtr.Zero on failure. Requires API v64+. APPENDED-AT-END for ABI safety.</summary>
    public IntPtr GetEngineStatusJsonFn;

    /// <summary>Function pointer: const char* GetPluginSnapshotJson(const char* pluginName)
    /// Brokers the named plugin's RynthPluginGetSnapshotJson export and returns its ANSI JSON
    /// pointer (IntPtr.Zero if that plugin isn't loaded / produced no snapshot). Lets one plugin
    /// read another's snapshot without GetProcAddress-ing it directly — PluginManager owns the
    /// module handles. The returned buffer is owned by the target plugin (valid until its next
    /// snapshot call); copy it immediately. Requires API v64+. APPENDED-AT-END for ABI safety.</summary>
    public IntPtr GetPluginSnapshotJsonFn;

    /// <summary>Function pointer: int SendPluginCommand(const char* pluginName, const char* action, const char* value)
    /// Forwards a (action,value) command to the named plugin's RynthPluginApplyRemoteCommand
    /// export. Returns 1 if delivered, 0 otherwise. The receiving plugin copies the args and
    /// applies them on its OWN pump/main thread (never on the caller's thread). Requires API
    /// v64+. APPENDED-AT-END for ABI safety.</summary>
    public IntPtr SendPluginCommandFn;

    /// <summary>Function pointer: int GetObjectDataIdProperty(uint objectId, uint stype, uint* value)
    /// Reads a STypeDID property that lives in the object's PublicWeenieDesc (Icon=8 → _iconID;
    /// since 2026-09-30 also IconOverlay=50 and IconUnderlay=52, 0 = none). Read directly from the embedded PWD struct — network-populated, so it works on
    /// UNequipped/never-appraised pack items with no qualities pointer and no main-thread native
    /// call. Returns 1 on success (value = the DataID, e.g. 0x06xxxxxx), 0 otherwise. Requires API
    /// v65+. APPENDED-AT-END for ABI safety.</summary>
    public IntPtr GetObjectDataIdPropertyFn;

    /// <summary>Function pointer: const char* GetPluginExportJson(const char* pluginName, const char* exportName)
    /// Generic sibling of GetPluginSnapshotJson: brokers ANY parameterless JSON-getter export on the named
    /// plugin (by convention RynthPluginGet*Json — takes no args, returns const char*). Lets one plugin read
    /// another's secondary JSON surfaces (e.g. RynthRemote pulling RynthAi's RynthPluginGetInventoryJson)
    /// without GetProcAddress-ing it directly — PluginManager owns the module handles. The returned buffer is
    /// owned by the target plugin (valid until its next call on that export); copy it immediately. Requires
    /// API v66+. APPENDED-AT-END for ABI safety.</summary>
    public IntPtr GetPluginExportJsonFn;

    // ── Vendor trading (v67) ─────────────────────────────────────────────
    // Decal's WorldFilter.OpenVendor + Actions.VendorBuyAll/VendorSellAll, as
    // stateless batch calls. Reads come from a snapshot the engine takes on AC's
    // main thread when the server's vendor list (ApproachVendor 0x0062) is
    // opened; buy/sell are queued and sent on AC's main thread through the
    // client's own gmVendorUI::SendShopEvent (CM_Vendor::Event_Buy/Event_Sell).
    // See Compatibility/VendorTrade.cs. APPENDED-AT-END for ABI safety.

    /// <summary>Function pointer: int GetVendorInfo(VendorInfoNative* info)
    /// Fills <paramref>info</paramref> for the vendor that is open right now. Returns 1
    /// if a vendor is open, 0 if not (info is zeroed). Any thread. Requires API v67+.</summary>
    public IntPtr GetVendorInfoFn;

    /// <summary>Function pointer: int GetVendorItems(VendorItemNative* items, int maxCount)
    /// Copies up to maxCount of the open vendor's items. Returns the TOTAL item count
    /// (call with maxCount=0 to size a buffer), or -1 if no vendor is open. Any thread.
    /// Requires API v67+.</summary>
    public IntPtr GetVendorItemsFn;

    /// <summary>Function pointer: uint VendorBuy(uint vendorId, VendorTradeEntryNative* entries, int count)
    /// Buys (objectId, amount) pairs from the open vendor (vendorId 0 = whichever is open).
    /// Returns a request id (&gt;0) once the request passes the snapshot checks and is
    /// queued, or 0 if it was refused (reason in GetVendorTradeStatus and the log).
    /// Funds, pack slots and burden are checked again on AC's main thread before the
    /// packet goes out; poll GetVendorTradeStatus for the outcome. One transaction is in
    /// flight at a time. Requires API v67+.</summary>
    public IntPtr VendorBuyFn;

    /// <summary>Function pointer: uint VendorSell(uint vendorId, uint* itemIds, int count)
    /// Sells the player's own items (whole stacks) to the open vendor. Same return and
    /// status contract as VendorBuy. Requires API v67+.</summary>
    public IntPtr VendorSellFn;

    /// <summary>Function pointer: int GetVendorTradeStatus(VendorTradeStatusNative* status)
    /// Fills the state of the most recent VendorBuy/VendorSell request. Returns 1 if a
    /// request has been made this session, 0 if not. Any thread. Requires API v67+.</summary>
    public IntPtr GetVendorTradeStatusFn;

    /// <summary>Function pointer: void* GetPluginInterface(const char* pluginName, const char* iface, uint version)
    /// Asks a named plugin for a typed interface table (e.g. RynthAi's "RynthAi.Script" v1 for
    /// RynthLua) through the fixed-signature export
    /// <c>void* RynthPluginQueryInterface(const char* iface, uint version)</c>. Null when the plugin
    /// isn't loaded or doesn't offer it. The table belongs to the target plugin; call its functions
    /// only from the plugin pump thread. Requires API v68+.</summary>
    public IntPtr GetPluginInterfaceFn;

    /// <summary>v69: <c>int GetLiveObjectIds(uint* buffer, int capacity)</c>: copies up to
    /// <paramref name="capacity"/> ids of the objects in the client's object table (the engine's
    /// snapshot, refreshed on the game thread) and returns the total count, so a caller whose
    /// buffer was too small can retry with a bigger one. Requires API v69+.</summary>
    public IntPtr GetLiveObjectIdsFn;

    // ── v70: action outcomes and wield-to-slot ──────────────────────────
    // Since v70 the engine also raises RynthPluginOnEnchantmentAdded / Removed (the
    // player's enchantment GameEvents 0x02C2-0x02C8 and 0x0312; see SmartBoxHooks).

    /// <summary>v70: <c>int GetLastUseDone(int* seq, uint* error)</c>: the most recent server
    /// UseDone (GameEvent 0x01C7): its sequence number (the same count GetUseDoneSeq returns)
    /// and its WeenieError code (0 = the action completed; e.g. 0x1D YoureTooBusy, 0x400
    /// YouDontHaveAllTheComponents). Both come from one atomic snapshot, so they always belong
    /// together. Returns 1 when the engine watches UseDone (seq 0 = none yet), 0 when it can't.
    /// Any thread. Requires API v70+.</summary>
    public IntPtr GetLastUseDoneFn;

    /// <summary>v70: <c>int GetLastWeenieError(int* seq, uint* error, uint* eventType, uint* objectId)</c>:
    /// the most recent refusal the server sent: WeenieError (eventType 0x028A, e.g. 0x402
    /// YourSpellFizzled — a fizzled cast still ends with UseDone(0)), WeenieErrorWithString
    /// (0x028B) or InventoryServerSaveFailed (0x00A0, objectId = the item). seq counts them
    /// (0 = none yet). Returns 1 when the engine watches these, 0 when it can't. Any thread.
    /// Requires API v70+.</summary>
    public IntPtr GetLastWeenieErrorFn;

    /// <summary>v70: <c>int WieldItem(uint objectId, uint equipMask)</c>: wield the item into the
    /// given EquipMask slot(s) (the 0x001A GetAndWieldItem game action, as a paperdoll drag
    /// sends it; e.g. 0x00200000 Shield, 0x00100000 MeleeWeapon). Queued for AC's main thread
    /// when called from another thread. Returns 1 if sent or queued. The server answers with
    /// the wield or InventoryServerSaveFailed (see GetLastWeenieError). Requires API v70+.</summary>
    public IntPtr WieldItemFn;

    // ── v71: script windows (docs: RynthSuite Docs/RYNTHLUA_WINDOWS_DESIGN.md §4) ──
    // A plugin draws ImGui windows by pushing a display list; the engine replays it every
    // frame on AC's thread and sends input back as events. The engine never calls into the
    // plugin for this and never keeps `data` / `buffer`: bytes are copied during the call.

    /// <summary>v71: <c>int UiSubmit(const uint8_t* data, int length)</c>: the owner's complete
    /// window set (display-list format 1, UI/ScriptWindows/DisplayListParser.cs). Only from the
    /// plugin's own tick or event on the pump thread. Returns 0, or -1 malformed, -2 not called
    /// from a dispatch, -3 over a limit (nothing applied), -4 unsupported format version.
    /// Requires API v71+.</summary>
    public IntPtr UiSubmitFn;

    /// <summary>v71: <c>int UiPollEvents(uint8_t* buffer, int capacity, int* remaining)</c>:
    /// copies whole events (never a partial one) and returns the bytes written;
    /// <c>*remaining</c> = events still queued. Same thread rule as UiSubmit. Requires API v71+.</summary>
    public IntPtr UiPollEventsFn;

    /// <summary>v71: <c>int UiGetInfo(UiInfoNative* info)</c>: the caller sets info->Size to the
    /// size it knows; the engine fills up to that and returns the bytes written (0 when there is
    /// no ImGui). Any thread. Requires API v71+.</summary>
    public IntPtr UiGetInfoFn;

    // ── v72: player-to-player trade (docs: Compatibility/PlayerTrade.cs) ──
    // The state comes from the server's trade GameEvents, read in the existing game-event
    // detour on AC's main thread; plugins poll it (Generation / Sequence / counters tell them
    // what changed). The actions go through the client's own CM_Trade / ClientTradeSystem
    // functions and are queued for AC's main thread when called from another thread.

    /// <summary>v72: <c>int GetTradeState(TradeStateNative* state)</c>: the caller sets
    /// state->Size to the size it knows; the engine fills up to that and returns the bytes
    /// written (0 on a bad argument). Any thread. Requires API v72+.</summary>
    public IntPtr GetTradeStateFn;

    /// <summary>v72: <c>int GetTradeItems(int side, uint* buffer, int capacity)</c>: copies up to
    /// <paramref name="capacity"/> item ids from one side of the window (1 = yours, 2 = the
    /// partner's) and returns that side's total count; -1 for a bad side. Any thread.
    /// Requires API v72+.</summary>
    public IntPtr GetTradeItemsFn;

    /// <summary>v72: <c>int TradeOpen(uint targetId)</c>: ask a player to trade (0x01F6
    /// OpenTradeNegotiations; the server walks you into range). Returns 1 if sent or queued.
    /// Requires API v72+.</summary>
    public IntPtr TradeOpenFn;

    /// <summary>v72: <c>int TradeAdd(uint itemId, uint slot)</c>: put one of your items in the
    /// open trade window (0x01F8 AddToTrade; slot 0 = next free). The server answers with
    /// AddToTrade or TradeFailure (see GetTradeState). Returns 1 if sent or queued.
    /// Requires API v72+.</summary>
    public IntPtr TradeAddFn;

    /// <summary>v72: <c>int TradeAccept(void)</c>: accept the trade as it stands (the window's
    /// Accept button, 0x01FA). Returns 1 if sent or queued. Requires API v72+.</summary>
    public IntPtr TradeAcceptFn;

    /// <summary>v72: <c>int TradeDecline(void)</c>: withdraw your acceptance (0x01FB).
    /// Returns 1 if sent or queued. Requires API v72+.</summary>
    public IntPtr TradeDeclineFn;

    /// <summary>v72: <c>int TradeReset(void)</c>: empty both sides of the window (0x0204).
    /// Returns 1 if sent or queued. Requires API v72+.</summary>
    public IntPtr TradeResetFn;

    /// <summary>v72: <c>int TradeClose(void)</c>: end the trade (0x01F7 CloseTradeNegotiations).
    /// Returns 1 if sent or queued. Requires API v72+.</summary>
    public IntPtr TradeCloseFn;

    /// <summary>v73: <c>int GetObjectInstanceIdProperty(uint objectId, uint stype, uint* value)</c>:
    /// a PropertyInstanceId (another object's id: Container 2, Wielder 3, Monarch 26, HouseOwner 32,
    /// PetOwner 44, the player's Allegiance/Patron/..., anything an UpdatePropertyInstanceID set).
    /// Any thread; served from the engine's caches and PublicWeenieDesc snapshot. 1 = found (non-zero).
    /// Requires API v73+.</summary>
    public IntPtr GetObjectInstanceIdPropertyFn;

    /// <summary>v74: <c>int GetVTankState(int* sequence)</c>: VTank's macro, as the Decal bridge
    /// sees it (Compatibility/VTankWatch.cs; signal = VTank's documented /vt start and /vt stop
    /// through Decal's chat parser). Returns flags: bit0 = the engine watches (Decal bridge mode),
    /// bit1 = VTank's macro is running. 0 without Decal. <c>*sequence</c> (may be null) is bumped
    /// on every change. Any thread. Requires API v74+.</summary>
    public IntPtr GetVTankStateFn;

    /// <summary>v75: <c>int GetCharacterTitles(uint* ids, int maxCount, uint* currentTitle)</c>: the
    /// titles the player holds, from the server's title events (0x0029 at login, 0x002B per new
    /// title; Compatibility/CharacterTitles.cs). Copies up to maxCount ids (ids may be null to
    /// ask for the count) and returns how many the character holds, or -1 when the list isn't
    /// known for the current character. *currentTitle (may be null) = the displayed title.
    /// Any thread. Requires API v75+.</summary>
    public IntPtr GetCharacterTitlesFn;

    /// <summary>v75: <c>int GetServerInfo(byte* worldName, int capacity)</c>: which server this
    /// is (Compatibility/ServerInfo.cs). Returns flags: bit0 = Aelrynth (host, announced world
    /// name or the Bank mod's properties), bit1 = Aelrynth's staging world, bit2 = the server
    /// announced its world name, which is then copied to worldName (UTF-8, NUL-terminated, cut
    /// to capacity - 1; worldName may be null). Never the launcher's profile name. Any thread.
    /// Requires API v75+.</summary>
    public IntPtr GetServerInfoFn;

    /// <summary>v76: <c>int CloseContainer(uint containerId)</c>: close an external container (a
    /// corpse, a chest) as the client does when its window is closed: the 0x0195
    /// NoLongerViewingContents game action (CM_Inventory::Event_NoLongerViewingContents). Not an
    /// inventory request: no busy count, no pending-request slot. The server answers with
    /// CloseGroundContainer, which closes the window and raises StopViewingObjectContents. Runs on
    /// AC's main thread (queued in the action ring from other threads, ahead of anything queued
    /// after it). Returns 1 if sent or queued. Requires API v76+.</summary>
    public IntPtr CloseContainerFn;

    /// <summary>v77: <c>int GetScreenMode(int* previousMode)</c>: the client's screen (UIFlow mode,
    /// Compatibility/UiFlowHooks.cs): 0x10000001 intro, 0x10000002 disconnected, 0x10000008 the
    /// world, 0x10000009 epilogue, 0x1000000A character select, 0x1000000B character creation;
    /// 0 = not known yet. *previousMode (may be null) = the screen before. Any thread.
    /// Requires API v77+.</summary>
    public IntPtr GetScreenModeFn;

    /// <summary>v77: <c>uint GetUiHookFlags(void)</c>: which UI hooks are live, so a plugin knows
    /// which v77 callbacks can arrive. bit0 RynthPluginOnScreenChanged from the UseNewMode hook,
    /// bit1 ... from the +0x8C mode poll instead (one tick late), bit2 RynthPluginOnClientCleanup,
    /// bit3 RynthPluginOnTooltipShow, bit4 RynthPluginOnTooltipHide, bit5 RynthPluginOnDragStart,
    /// bit6 RynthPluginOnItemDropped, bit7 Client::Cleanup has started, bit8 (2026-10-30, not a hook)
    /// the Damage panel draws a RynthPluginGetMonsterDamageJson row's "mstats". Any thread.
    /// Requires API v77+.</summary>
    public IntPtr GetUiHookFlagsFn;

    /// <summary>v78: <c>int SetServerMessageInterest(uint* opcodes, int opcodeCount, uint* gameEvents,
    /// int eventCount)</c>: which reassembled server messages the calling plugin wants on its
    /// <c>void RynthPluginOnServerMessage(uint opcode, byte* data, int length)</c> export (Cdecl;
    /// data = the message after its u32 opcode, valid only during the call; for 0xF7B0 it starts
    /// with object id, sequence, event type). Replaces the plugin's previous set; both counts 0 =
    /// none. Opcodes are 16-bit; 0xF7B0 = every game event, 0xFFFFFFFF = every message;
    /// gameEvents lists single event types. Read-only: the client's packets are never changed.
    /// Call from the plugin's Init, Tick or an event (the engine finds the caller that way).
    /// Returns 1 = streaming, 2 = accepted but the stream is off (engine.json
    /// "ServerMessageStream": false or /rc netmsg off), 0 = the plugin has no
    /// RynthPluginOnServerMessage export, -1 = caller unknown (another thread), -3 = bad
    /// arguments. Requires API v78+.</summary>
    public IntPtr SetServerMessageInterestFn;

    /// <summary>v79: <c>int GetTrainingInfo(TrainingInfoNative* info, TrainingEntryNative* entries,
    /// int maxEntries)</c>: what spending experience would do (Compatibility/TrainingApi.cs): the
    /// unassigned XP, skill credits and level, and one entry per attribute (1..6), vital maximum
    /// (1 health, 3 stamina, 5 mana) and portal-SkillTable skill with ranks, XP spent, base and
    /// buffed value and the XP for +1 / +10 from the portal XpTable. Set info->Size to sizeof
    /// before the call (info may be null; entries may be null to ask for the count). Returns the
    /// number of entries there are (at most maxEntries written), 0 when there are no numbers yet
    /// (not in the world, or the first read hasn't happened: it is asked for by this call and
    /// comes within a couple of seconds). Asking keeps the numbers coming every 2 s for 5 s.
    /// Any thread. Requires API v79+.</summary>
    public IntPtr GetTrainingInfoFn;

    /// <summary>v79: <c>int Raise(uint kind, uint stype, uint ranks, long expectedXp, long* xpSent)</c>:
    /// spend unassigned XP like the character window's "+": kind 1 attribute (stype 1..6), 2 vital
    /// (1 health, 3 stamina, 5 mana), 3 trained or specialized skill; ranks 1..100. The engine works
    /// the XP out from the numbers GetTrainingInfo reports and sends it with the client's own
    /// CM_Train sender (0x0045 / 0x0044 / 0x0046) on AC's main thread. expectedXp &gt; 0 must equal
    /// that XP (the cost the user confirmed), else nothing is sent; 0 skips the comparison.
    /// *xpSent (may be null) = the XP sent. Returns 1 sent or queued, 0 the senders aren't bound,
    /// -1 not in the world, -2 bad arguments, -3 no numbers yet, -4 the numbers are over 3 s old
    /// (a read was asked for: call again shortly), -5 the cost changed, -6 not enough XP, -7 not
    /// raisable (top rank, untrained skill), -8 an earlier raise hasn't landed yet, -9 the send
    /// failed. Any thread. Requires API v79+.</summary>
    public IntPtr RaiseFn;

    /// <summary>v79: <c>int TrainSkill(uint stype, int expectedCredits)</c>: train an untrained
    /// skill with skill credits, as the skills window's train dialog does (0x0047, exactly the
    /// portal SkillTable's TrainedCost; the server refuses anything else). expectedCredits &gt; 0
    /// must equal that price; 0 skips the comparison. Same results and threading as Raise.
    /// Requires API v79+.</summary>
    public IntPtr TrainSkillFn;

    /// <summary>v80: <c>int GetContainerOrder(uint containerId, int slotKind, uint* ids, int maxIds)</c>:
    /// the items in one of the player's containers in their real slot order, as the server keeps it
    /// (Net/InventoryOrder: the login message, a pack's contents list and every item move), where
    /// GetContainerContents is in object-id order. slotKind 0 = the ordinary items (slot 0 first);
    /// 1 = the side packs and foci in the main pack (the pack column, top first). Writes at most
    /// maxIds ids (ids may be null with maxIds 0 to ask the count) and returns how many there are,
    /// or -1 when no full list for that container has been seen (a side pack the server hasn't
    /// listed yet): then the order is unknown, never guessed. Any thread. Requires API v80+.</summary>
    public IntPtr GetContainerOrderFn;

    /// <summary>v80: <c>int PutItemInContainer(uint itemId, uint containerId, int placement)</c>: move an
    /// item into a container at a slot (the 0x19 PutItemInContainer game action, the packet only:
    /// CM_Inventory::Event_PutItemInContainer), as a drop on a cell of the pack grid does. Within one
    /// container it reorders: placement is the index the item should end at, counted without it.
    /// MoveItemInternal ignores its slot argument (first empty slot); this doesn't. The server decides
    /// (full pack, busy) and answers as for any move. Queued to AC's main thread when called off it.
    /// Returns 1 sent or queued, 0 refused before sending (bad ids, negative placement), -1 the
    /// sender isn't bound on this client. Requires API v80+.</summary>
    public IntPtr PutItemInContainerFn;

    /// <summary>v81: <c>int GetMergeStackResult(uint sourceObjectId, uint targetObjectId, int* amount, int* ageMs)</c>:
    /// outcome of the latest MergeStackInternal(source, target) request: 0 none, 1 queued,
    /// 2 sent (amount = units sent), 3 skipped because the target was already full,
    /// 4 failed (invalid ids / AC rejected / threw), 5 dropped (main-thread queue full).
    /// ageMs = how long ago the outcome was recorded. Either out pointer may be null.
    /// The thunk is cdecl. Any thread. Requires API v81+ (it was v77 on QOL-items and v80 on sk
    /// before upstream's v77-v80 landed; plugins built against those SDKs must be rebuilt).</summary>
    public IntPtr GetMergeStackResultFn;
    /// <summary>v82: <c>int SalvageDirect(uint toolId, uint* itemIds, int count)</c>: salvage without the
    /// salvage panel. Sends the 0x027D CreateTinkeringTool game action (what the panel's Salvage button
    /// sends) through AC's own sender, CM_Inventory::Event_CreateTinkeringTool, with the UST and up to
    /// 100 item ids (salvage bags in the list are merged). The panel never opens. The server checks the
    /// UST is the player's, salvages what it can, answers with the items' removal, new salvage bags and a
    /// "You obtain ..." chat line, and skips the rest silently. Queued to AC's main thread when called off
    /// it (the list is copied). Returns the count sent or queued, 0 refused before sending (no tool, no
    /// items, more than 100, a zero id, the tool in the list, queue full), -1 the sender isn't bound on
    /// this client. Requires API v82+.</summary>
    public IntPtr SalvageDirectFn;

    /// <summary>v83: <c>int GetObjectServerAge(uint objectId)</c>: milliseconds since the server last
    /// sent a message about the object (create, position, motion, vector, state, parent, pickup,
    /// effects, object description, update object), capped at int.MaxValue; -1 when it sent none
    /// since the engine started watching (or the id was deleted). A creature with nothing from the
    /// server for a long time while it is attacked is a "ghost": the client draws it, the server no
    /// longer has it for this player. Any thread. Requires API v83+.</summary>
    public IntPtr GetObjectServerAgeFn;

    /// <summary>v83: <c>int RemoveClientObject(uint objectId)</c>: removes a ghost creature from the
    /// client the way a server DeleteObject would (AC's ACCObjectMaint::DeleteObject; plugins get
    /// OnDeleteObject). Refused for the player, players, anything that isn't a creature, and objects
    /// the server sent a message about in the last 5 s. If the object is real after all, the
    /// server's next CreateObject for it brings it back. Queued to AC's main thread when called off
    /// it (checked again there). Returns 1 removed, 2 queued, 0 refused, -1 the client's
    /// DeleteObject isn't bound yet (no delete seen since the hook went in). Requires API v83+.</summary>
    public IntPtr RemoveClientObjectFn;

    /// <summary>v84: <c>int GetObjectProperties(uint objectId, byte* buffer, int bufferSize)</c>: every
    /// property the engine knows for the object (int, int64, bool, float, string, data id, instance id
    /// and the spell book), merged from the client's CreateObject copy, its qualities tables (read on
    /// AC's main thread), the last identify and the server's property updates, as one record
    /// (Compatibility/ObjectPropertyRecord.cs; the SDK's ObjectProperties.TryParse reads it). Returns the
    /// record's size in bytes; it was copied only when that is &lt;= bufferSize (call again with a buffer
    /// that big). 0 when the engine knows nothing about the object, -1 a zero id or a negative size.
    /// Off AC's main thread a walk of the object's qualities tables is requested for the next tick
    /// (flag QualitiesPending): ask again a moment later for the full set. Any thread. Requires API v84+.</summary>
    public IntPtr GetObjectPropertiesFn;

    /// <summary>v85: <c>int ArmConfirmation(uint type, int minPercent, int timeoutMs)</c>: answer
    /// the next server yes/no prompt (ConfirmationRequest 0x0274) of this type within timeoutMs,
    /// without the client opening its dialog: Yes when the server's "N percent" is at least
    /// minPercent (or minPercent is 0), else No. One-shot. timeoutMs &lt;= 0 disarms.
    /// 1 armed/disarmed, 0 sender or game-event hook unavailable, -3 bad arguments
    /// (type 0, minPercent outside 0-100, timeout over 120 s). Any thread. Requires API v85+.</summary>
    public IntPtr ArmConfirmationFn;

    /// <summary>v85: <c>int GetLastConfirmation(uint* type, uint* contextId, int* percent, int* outcome, int* ageMs)</c>:
    /// the last prompt the server sent: returns its sequence number (0 = none yet); percent -1
    /// when the text has none; outcome 1 shown to the player, 2 answered Yes, 3 answered No,
    /// 4 the send failed (shown). Any out pointer may be null. Any thread. Requires API v85+.</summary>
    public IntPtr GetLastConfirmationFn;

    /// <summary>v86: <c>int GetServerStateJson(byte* buffer, int capacity)</c>: which server this is,
    /// for every plugin: each server the engine knows (built in, or declared by a loaded plugin)
    /// with its verdict, this character's override, why, and the plugin that owns its settings;
    /// plus the active server and the Diagnostics pick (UI/Sections/ServerStateJson.cs has the
    /// schema). Returns the UTF-8 byte length; when it is &gt;= capacity nothing was written (call
    /// again with a bigger buffer). -1 on failure. Any thread. Requires API v86+.</summary>
    public IntPtr GetServerStateJsonFn;
    /// <summary>v87: <c>int GetWieldedItems(uint* ids, uint* locations, int max)</c>: what the player
    /// wields and where (EquipMask), from the server's WieldObject / InventoryPutObjInContainer /
    /// InventoryPutObjectIn3D messages, started from the client's records where they agree
    /// (Compatibility/EquipmentTracker.cs). Returns the count written (at most max), or -1 while
    /// there is no player or nothing to start from. Any thread. Requires API v87+.</summary>
    public IntPtr GetWieldedItemsFn;

    // ── v89: plugin windows (docs/PLUGIN_WINDOWS.md): the engine provides ImGui, plugins draw ──

    /// <summary>v89: <c>uint GetUiColor(int token)</c>: the player's colour theme as ImGui's packed
    /// colour (ABGR) for a <see cref="UiColorToken"/>, so a plugin's own windows look like the
    /// engine's; 0 for an unknown token. Any thread (read every frame: it follows a theme change at
    /// once). Requires API v89+.</summary>
    public IntPtr GetUiColorFn;
    /// <summary>v89: <c>void* GetUiFont(int font)</c>: an <c>ImFont*</c> in the shared atlas for a
    /// <see cref="UiFontToken"/>, at the current panel's text size; null for an unknown font or off
    /// AC's render thread. Push it around the plugin's own text. Requires API v89+.</summary>
    public IntPtr GetUiFontFn;
    /// <summary>v89: <c>int SetPanelOpen(const char* key, int mode)</c>: a panel by its key (a plugin
    /// window's title, or an engine panel's): mode 1 open, 0 close, 2 toggle, -1 just ask. Returns
    /// 1 open / 0 closed (after the change, which lands on the next frame), -1 no such panel. Any
    /// thread. Requires API v89+.</summary>
    public IntPtr SetPanelOpenFn;

    // ── v90: plugin windows that draw their own frame ("chrome": "none"; docs/PLUGIN_WINDOWS.md) ──

    /// <summary>v90: <c>void PanelDragWithLastItem(void)</c>: while ImGui's last item is held and
    /// dragged, the panel being drawn follows the mouse (a frameless window's own title row; popped
    /// out, its OS window). AC's render thread, inside a window's RynthPluginRenderEmbed; anywhere
    /// else it does nothing. Requires API v90+.</summary>
    public IntPtr PanelDragWithLastItemFn;
    /// <summary>v90: <c>int SetPanelPopOut(const char* key, int mode)</c>: an open panel into its own
    /// window or back: mode 1 pop out, 0 dock, 2 toggle, -1 just ask. Returns 1 popped out / 0 docked
    /// (after the change, which lands on the next frame), -1 no such panel or not open. Any thread.
    /// Requires API v90+.</summary>
    public IntPtr SetPanelPopOutFn;
    /// <summary>v90: <c>int PanelTextSize(const char* key, int delta)</c>: steps a panel's text size
    /// by <c>delta</c> (0 = just ask; Ctrl+wheel and the size menu of a frameless window) and returns
    /// it as a percentage of normal (100, 110, ... 250), -1 for no such panel. The fonts re-bake a
    /// moment later. Any thread. Requires API v90+.</summary>
    public IntPtr PanelTextSizeFn;
    /// <summary>v90: <c>float RenderSettingsDrawer(const char* key, float width, float room, int flags)</c>:
    /// the body of a dashboard drawer that shows Settings rows, which are still RynthCore's (the
    /// Settings panel hasn't moved to RynthAi): "ranges" (the selected monster and the key ranges),
    /// "patrol" (the Dungeon Patrol settings, hazards and routes), "pinned" (My settings; flags 1 =
    /// open its list of settings to pin). Drawn at the cursor of the window being drawn, <c>width</c>
    /// pixels wide and at most <c>room</c> tall (0 = its own height). Returns the height it wants
    /// (pixels), -1 for another key. AC's render thread, inside a docked window's
    /// RynthPluginRenderEmbed. Requires API v90+. 2026-10-27 (engines with v91): also "nameplates"
    /// and "combattext", the Vision window's tabs that set RynthCore's nameplates and combat text,
    /// for RynthVision's own Vision window (it takes over the engine's); those draw popped out too.</summary>
    public IntPtr RenderSettingsDrawerFn;
    /// <summary>v90: <c>int GetShortcuts(uint* objectIds, uint* spellIds, int max)</c>: the player's
    /// 18 shortcut slots (AC's own, the retail toolbar's), object id 0 = empty. Returns 18, or 0 out
    /// of the world. Any thread. Requires API v90+.</summary>
    public IntPtr GetShortcutsFn;
    /// <summary>v90: <c>int ShortcutAction(int op, int slot, uint objectId)</c>: op 0 use the slot
    /// (as its key does), 1 put the object in it, 2 empty it; slot 0-17. Done on AC's game thread
    /// through the retail toolbar's own functions (saved to the server). 1 queued, 0 refused. Any
    /// thread. Requires API v90+.</summary>
    public IntPtr ShortcutActionFn;
    /// <summary>v90: <c>int GetActionKeys(const char* action, char* buffer, int length)</c>: AC's keys
    /// for a keymap action ("UseQuickSlot_1") as UTF-8 text ("1, Ctrl+1"). Returns the bytes needed
    /// (0 = unbound), -1 for a bad call. Any thread. Requires API v90+.</summary>
    public IntPtr GetActionKeysFn;
    /// <summary>v90: <c>void* GetIconTexture(int kind, uint id)</c>: kind 0 icon id, 1 object, 2
    /// spell, 3 a retail UI picture at its own size, 4 a retail font's glyph sheet as white + alpha
    /// (3 and 4 2026-11-07, no version bump): an ImGui texture
    /// id, or null while it is made. AC's render thread, in a docked window's draw. Requires API v90+.</summary>
    public IntPtr GetIconTextureFn;
    /// <summary>v90: <c>int KeyCapture(int op, char* buffer, int length)</c>: op 0 waits for the next key
    /// the player presses (it never reaches AC; Esc cancels), 1 asks (0 waiting, 1 pressed: its name
    /// "Ctrl+F5" in the buffer, 2 cancelled), 2 stops. AC's main thread. Requires API v90+.</summary>
    public IntPtr KeyCaptureFn;
    /// <summary>v90: <c>int SetActionKey(const char* action, const char* key, int op, char* message, int
    /// length)</c>: a keymap action ("UseQuickSlot_5") or engine command ("/rdef use 2") and a key: op 0
    /// checks (0 free, 1 taken from what the message names, -1 can't), 1 binds it (off everything else,
    /// every client follows), 2 removes it, 3 binds an engine command's key as its hotkey alone (AC's keymap
    /// untouched; engines after 2026-10-27, older ones refuse it) (1 queued, -1 refused). Any thread.
    /// Requires API v90+.</summary>
    public IntPtr SetActionKeyFn;
    /// <summary>v90: <c>int AcPanel(int id, int op)</c>: AC's own panel by the id its panel notice uses (7
    /// the pack, 12 fellowship/allegiance, 13 spellbook, 16 the map...): op 0 asks (1 open, 0 closed, -1
    /// none; AC's main thread), 1 toggles, 2 opens, 3 closes (1 queued, -1 refused). Requires API v90+.</summary>
    public IntPtr AcPanelFn;
    /// <summary>v90: <c>int PanelScale(const char* key, int percent)</c>: how big a frameless window draws
    /// itself, 30-250% of normal (0 asks, -1 = follow the text size again); returns it, 0 = follows the
    /// text size. Settings > UI > Toolbar sets it too. Any thread. Requires API v90+.</summary>
    public IntPtr PanelScaleFn;
    /// <summary>v90: <c>uint DropTarget(const char* window, float x0, float y0, float x1, float y1, int flags,
    /// int* dragging)</c>: the plugin window takes items dragged out of AC's UI and let go over that rect
    /// (ImGui screen coordinates; flags 1 = accepting; published each frame it draws). Returns the object
    /// dropped since the last call (0 = none); *dragging = 1 while an item drag is on. A claimed drop never
    /// reaches AC. AC's main thread, in the window's draw. Requires API v90+.</summary>
    public IntPtr DropTargetFn;

    // ── v91: a floor under the world's ambient light (D3D9/AmbientLight.cs) ──

    /// <summary>v91: <c>int SetAmbientLightFloor(uint rgb)</c>: the least ambient light AC may draw the
    /// world with, 0x00RRGGBB (alpha ignored; per channel the brighter of AC's and this), 0 = none
    /// (AC's own). Shows from the next frame, in this client only (drawing; nothing goes to the
    /// server). Returns 1 when the engine's hook is live, 0 when it isn't (not installed, or switched
    /// off: /rc hooks AmbientLight). One floor for all plugins (the last call wins); it goes back to 0
    /// when the plugins unload. Any thread. Requires API v91+.</summary>
    public IntPtr SetAmbientLightFloorFn;
    /// <summary>v91: <c>int GetAmbientLight(uint* client, uint* applied)</c>: the ambient colour AC last
    /// asked for and the one drawn (0xAARRGGBB; 0 before the first frame). Returns 1 when the hook is
    /// live, 0 when it isn't (both 0). Either pointer may be null. Any thread. Requires API v91+.</summary>
    public IntPtr GetAmbientLightFn;

    // ── v93: the monster plates' health readings (ImGui/MonsterHud/MonsterHudData.cs) ──

    /// <summary>v93: <c>int GetMonsterHealth(uint id, float* ratio, uint* ageMs)</c>: the last health ratio
    /// (0..1) the SERVER sent for the object, as the monster plates keep it, and how many ms ago. That
    /// includes the replies to the plates' own round-robin probes (HealthProbe), which plugins never get
    /// as UpdateHealth events, and never the plates' estimates from the player's hits. Returns 1 with a
    /// reading, 0 without (never read; *ratio = -1, *ageMs = 0xFFFFFFFF). Either pointer may be null.
    /// Read-only, any thread, no game calls. Requires API v93+.</summary>
    public IntPtr GetMonsterHealthFn;

    // ── v94: a WebView2 web panel over the game window (WebPanel\WebPanelApi.cs) ──
    // No call blocks its caller (AC's main thread, the plugin pump and ImGui draws all call these).

    /// <summary>v94: <c>int WebPanelOpen(WebPanelOpenNative* open)</c>: opens (or replaces) the web panel.
    /// Returns a handle &gt; 0, or BadArgs / UrlNotAllowed / NoGameWindow / Unavailable / ShuttingDown
    /// (WebPanelCode). Requires API v94+ (provisional number).</summary>
    public IntPtr WebPanelOpenFn;
    /// <summary>v94: <c>int WebPanelNavigate(int handle, byte* url)</c>: 1 queued; -2 off the allowlist
    /// (opened in the system browser when it is browser-safe); -6 stale handle.</summary>
    public IntPtr WebPanelNavigateFn;
    /// <summary>v94: <c>int WebPanelClose(int handle)</c>: 1 queued, 0 already closed, -6 stale handle.</summary>
    public IntPtr WebPanelCloseFn;
    /// <summary>v94: <c>int WebPanelGetState(int handle, WebPanelStateNative* state)</c>: the WebPanelStatusCode
    /// (a handle that is not the live one reads 6 Superseded), -1 bad args.</summary>
    public IntPtr WebPanelGetStateFn;
}

/// <summary>v94 WebPanelOpen argument (Pack 4; 48 bytes on x86). Mirrors RynthCore.PluginSdk/WebPanelTypes.cs.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal unsafe struct WebPanelOpenNative
{
    /// <summary>sizeof the caller's struct; the engine accepts Size &gt;= OffsetOf(Reserved) (32 on x86).</summary>
    public uint Size;
    /// <summary>WebPanelFlags; unknown bits ignored.</summary>
    public uint Flags;
    /// <summary>UTF-8 NUL-terminated, at most 4096 bytes, must pass the URL policy's IsAllowed.</summary>
    public byte* Url;
    /// <summary>UTF-8, '\n'-separated allowlist (WebPanelUrlPolicy.TryParseAllowlist).</summary>
    public byte* Allowlist;
    /// <summary>UTF-8, may be null; at most 256 bytes.</summary>
    public byte* ProfileKey;
    /// <summary>UTF-8, may be null; at most 128 bytes.</summary>
    public byte* Title;
    /// <summary>0 = 88, else 30..100 (only when no remembered layout).</summary>
    public int WidthPercent;
    public int HeightPercent;
    public fixed uint Reserved[4];
}

/// <summary>v94 WebPanelGetState result (Pack 4; 48 bytes). Mirrors RynthCore.PluginSdk/WebPanelTypes.cs.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal unsafe struct WebPanelStateNative
{
    public uint Size;
    public int Handle;
    /// <summary>WebPanelStatusCode.</summary>
    public int Status;
    /// <summary>1 Visible, 2 Focused, 4 OpenedInBrowser.</summary>
    public uint Flags;
    /// <summary>0 or a negative WebPanelCode (RuntimeMissing / CreateFailed / ProcessFailed).</summary>
    public int Error;
    public int LastHttpStatus;
    public uint NavigationCount;
    public fixed uint Reserved[5];
}

/// <summary>v94 return codes of the web panel calls. Append only.</summary>
internal enum WebPanelCode
{
    Queued = 1, AlreadyClosed = 0, BadArgs = -1, UrlNotAllowed = -2, NoGameWindow = -3,
    Unavailable = -4, ShuttingDown = -5, StaleHandle = -6, RuntimeMissing = -7, CreateFailed = -8, ProcessFailed = -9,
}

/// <summary>v94 WebPanelStateNative.Status values. Append only.</summary>
internal enum WebPanelStatusCode
{
    None = 0, Starting = 1, Loading = 2, Ready = 3, Closed = 4, Failed = 5, Superseded = 6,
}

/// <summary>v89 GetUiColor tokens. Append only: plugins compiled against these numbers keep them.</summary>
internal enum UiColorToken
{
    Text = 0, TextMuted = 1, Accent = 2, Highlight = 3, Button = 4, Border = 5, Field = 6, Selected = 7,
    RowAlt = 8, Background = 9, PanelGlass = 10, AccentFill = 11, BoxBg = 12, FrameBg = 13, FrameHover = 14,
    FrameActive = 15, Green = 16, Red = 17, TitleBar = 18, WindowBorder = 19,
    // v90: the theme's own muted text (TextMuted is a lighter mix) and the instrument look's shades
    // worked out from the eight colours (ThemePalette.DeriveInstrument). 0 on a v89 engine.
    Muted = 20, KeyFace = 21, Hairline = 22, Inset = 23, Faint = 24,
}

/// <summary>v89 GetUiFont fonts. Append only.</summary>
internal enum UiFontToken
{
    Ui9 = 0, Ui10 = 1, Ui11 = 2, Ui14 = 3, Ui18 = 4, UiBold11 = 5, UiBold14 = 6,
    Mono9 = 7, Mono10 = 8, Mono12 = 9, Chat10 = 10, Chat12 = 11, Chat14 = 12,
    // v90: Cascadia Mono (Consolas without it), the dashboard's numbers, and the narrow labels
    // (Barlow Semi Condensed SemiBold 9/10/11, Bold 11/13). Null on a v89 engine.
    Dash9 = 13, Dash10 = 14, Dash11 = 15, Dash14 = 16,
    Narrow9 = 17, Narrow10 = 18, Narrow11 = 19, NarrowBold11 = 20, NarrowBold13 = 21,
}

/// <summary>v79 <see cref="TrainingInfoNative.Flags"/> bits.</summary>
internal static class TrainingInfoFlags
{
    public const uint InWorld = 1u << 0;
    /// <summary>The attribute, vital and skill raise senders are bound on this client.</summary>
    public const uint RaiseBound = 1u << 1;
    /// <summary>The train-with-credits sender is bound.</summary>
    public const uint TrainBound = 1u << 2;
    /// <summary>The player's numbers have been read (entries follow).</summary>
    public const uint HaveNumbers = 1u << 3;
    /// <summary>The portal SkillTable and XpTable are loaded.</summary>
    public const uint HaveTables = 1u << 4;
    /// <summary>A raise or train from the API hasn't shown up in the numbers yet.</summary>
    public const uint Busy = 1u << 5;
}

/// <summary>v79 <see cref="TrainingEntryNative.Flags"/> bits.</summary>
internal static class TrainingEntryFlags
{
    /// <summary>Can be raised with XP (an attribute, a vital, a trained or specialized skill below the top rank).</summary>
    public const uint Raisable = 1u << 0;
    public const uint AtTop = 1u << 1;
    /// <summary>Skills: usable untrained (the SkillTable's min level 1).</summary>
    public const uint UsableUntrained = 1u << 2;
    /// <summary>Skills: untrained, and the SkillTable prices training it (TrainCredits).</summary>
    public const uint Trainable = 1u << 3;
}

/// <summary>
/// v79 <c>GetTrainingInfo</c> header. Pack 4, 64 bytes; later versions only append.
/// Mirrored by RynthCore.PluginSdk (TrainingInfoNative in RynthCoreApiNative.cs).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal unsafe struct TrainingInfoNative
{
    /// <summary>In: the caller's sizeof. Out: bytes written.</summary>
    public uint Size;
    /// <summary><see cref="TrainingInfoFlags"/>.</summary>
    public uint Flags;
    /// <summary>Changes whenever any number changes.</summary>
    public uint SnapshotVersion;
    /// <summary>Milliseconds since the numbers were read; 0xFFFFFFFF = never.</summary>
    public uint AgeMs;
    /// <summary>-1 when unknown.</summary>
    public long UnassignedXp;
    /// <summary>-1 when unknown.</summary>
    public long TotalXp;
    public int Level;
    /// <summary>-1 when unknown.</summary>
    public int SkillCredits;
    public int EntryCount;
    /// <summary>Raise refuses numbers older than this (Stale).</summary>
    public int StaleAfterMs;
    public fixed uint Reserved[4];
}

/// <summary>
/// v79 <c>GetTrainingInfo</c> entry: one attribute, vital or skill. Pack 4, 64 bytes; later
/// versions only append. Mirrored by RynthCore.PluginSdk (TrainingEntryNative).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct TrainingEntryNative
{
    /// <summary>1 attribute, 2 vital, 3 skill.</summary>
    public uint Kind;
    /// <summary>Attribute 1..6, vital maximum 1 / 3 / 5, skill id.</summary>
    public uint Stype;
    /// <summary>Skills: 0 not in the table, 1 untrained, 2 trained, 3 specialized. 0 otherwise.</summary>
    public uint Class;
    public uint Ranks;
    public uint XpSpent;
    /// <summary>The innate (starting) level.</summary>
    public uint Innate;
    /// <summary>Without enchantments (a vital's maximum).</summary>
    public int Base;
    public int Buffed;
    /// <summary>XP for one more rank; -1 at the top or not raisable.</summary>
    public long CostOne;
    /// <summary>XP for ten more ranks; -1 when fewer than ten are left or not raisable.</summary>
    public long CostTen;
    public int RanksLeft;
    /// <summary>Ranks in a row the unassigned XP buys now.</summary>
    public int Affordable;
    /// <summary>Untrained skills: the credits to train it (0 = can't be trained from the client).</summary>
    public int TrainCredits;
    /// <summary><see cref="TrainingEntryFlags"/>.</summary>
    public uint Flags;
}

/// <summary>v72 <see cref="TradeStateNative.Flags"/> bits.</summary>
internal static class TradeStateFlags
{
    public const uint Open = 1u << 0;
    public const uint YouAccepted = 1u << 1;
    public const uint PartnerAccepted = 1u << 2;
    /// <summary>The engine sees the trade GameEvents (its game-event hook is installed).</summary>
    public const uint Watching = 1u << 3;
    /// <summary>All six trade actions are bound on this client.</summary>
    public const uint ActionsAvailable = 1u << 4;
}

/// <summary>
/// v72 <c>GetTradeState</c> result. Pack 4, 96 bytes; later versions only append.
/// Mirrored by RynthCore.PluginSdk (TradeStateNative in RynthCoreApiNative.cs).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal unsafe struct TradeStateNative
{
    /// <summary>In: the caller's sizeof. Out: bytes written.</summary>
    public uint Size;
    /// <summary><see cref="TradeStateFlags"/>.</summary>
    public uint Flags;
    /// <summary>+1 for every trade that opens (RegisterTrade).</summary>
    public uint Generation;
    /// <summary>+1 for every trade event (and TradeComplete).</summary>
    public uint Sequence;
    /// <summary>The other player; 0 when no trade is open.</summary>
    public uint PartnerId;
    /// <summary>Who opened the trade.</summary>
    public uint InitiatorId;
    public int SelfItemCount;
    public int PartnerItemCount;
    /// <summary>The last trade GameEvent type (0x01FD-0x0208).</summary>
    public uint LastEventType;
    /// <summary>TradeFailure events so far; the last one's item and WeenieError follow.</summary>
    public uint FailureCount;
    public uint LastFailureItemId;
    public uint LastFailureReason;
    /// <summary>EndTradeReason of the last CloseTrade: 1 normal, 2 entered combat, 0x51 cancelled.</summary>
    public uint LastCloseReason;
    public uint LastAcceptedBy;
    public uint LastDeclinedBy;
    public uint LastResetBy;
    /// <summary>Trades completed so far (WeenieError 0x0529 TradeComplete).</summary>
    public uint CompletedCount;
    /// <summary>Times the partner has accepted so far (AcceptTrade from the partner).</summary>
    public uint PartnerAcceptCount;
    public fixed uint Reserved[6];
}

/// <summary>
/// v71 <c>UiGetInfo</c> result. Pack 4, 480 bytes in format 1; later versions only append.
/// Mirrored by RynthCore.PluginSdk (UiInfoNative in RynthCoreApiNative.cs).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal unsafe struct UiInfoNative
{
    /// <summary>In: the caller's sizeof. Out: bytes written.</summary>
    public uint Size;
    /// <summary>bit0 ImGui available, bit1 in world, bit2 pop-outs available.</summary>
    public uint Flags;
    /// <summary>Highest display-list format the engine replays.</summary>
    public ushort MaxFormatVersion;
    /// <summary>The ops replayed within MaxFormatVersion: 2 = + Image, ImageButton, InputInt, InputFloat,
    /// DragInt, DragFloat (2026-09-30). 0 on older engines (the field was reserved) = Phase 1 ops only.</summary>
    public ushort OpLevel;
    public uint MaxOpsPerWindow;
    public uint MaxBytesPerWindow;
    public uint MaxWindowsPerOwner;
    public uint MaxBytesPerSubmit;
    public float DisplayWidth;
    public float DisplayHeight;
    public float UiScale;
    public float TextLineHeight;
    public float FrameHeight;
    public float ItemSpacingX;
    public float ItemSpacingY;
    public float FramePaddingX;
    public float FramePaddingY;
    /// <summary>Default font advance of ' '..'~' (95), pixels at UiScale.</summary>
    public fixed float AsciiAdvance[95];
    public uint FrameCounter;
    public fixed uint Reserved[8];
}

// ─── Vendor trading ABI structs (v67) ───────────────────────────────────
// Blittable, Pack=4, 4-byte fields + fixed ANSI buffers only. Mirrored exactly
// by RynthCore.PluginSdk/VendorTypes.cs. Never reorder or resize; add new calls
// instead of growing these.

/// <summary>The open vendor (VendorProfile from ApproachVendor 0x0062). 192 bytes.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal unsafe struct VendorInfoNative
{
    public uint VendorId;
    /// <summary>Bumps every time the server (re)sends the vendor list: on open and after
    /// each accepted buy/sell. Compare before/after a trade to see the refresh.</summary>
    public uint Generation;
    /// <summary>ShopMode passed to gmVendorUI::OpenVendor (raw client value).</summary>
    public int ShopMode;
    /// <summary>ITEM_TYPE mask of what the vendor buys.</summary>
    public uint ItemTypes;
    public int MinValue;
    public int MaxValue;
    public int DealsMagic;
    /// <summary>Vendor pays value * BuyRate when you sell.</summary>
    public float BuyRate;
    /// <summary>Vendor charges value * SellRate when you buy.</summary>
    public float SellRate;
    /// <summary>WCID of the vendor's currency; 0 = pyreals.</summary>
    public uint AltCurrencyWcid;
    /// <summary>Alt currency count as the server reported it in the vendor list (-1 if none).</summary>
    public int AltCurrencyServerCount;
    /// <summary>Alt currency the engine counted in the player's packs (-1 = not counted yet).</summary>
    public int AltCurrencyHave;
    /// <summary>Player's pyreals (CoinValue), refreshed on AC's main thread; -1 unknown.</summary>
    public int PlayerCoins;
    public int ItemCount;
    /// <summary>bit0 = trading available (client functions resolved and layout verified),
    /// bit1 = a transaction is in flight.</summary>
    public uint Flags;
    public uint Reserved0;
    public fixed byte Name[64];
    public fixed byte AltCurrencyName[64];
}

/// <summary>One item on the vendor's list (ItemProfile + its PublicWeenieDesc). 120 bytes.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal unsafe struct VendorItemNative
{
    public uint ObjectId;
    public uint Wcid;
    public uint ItemType;
    public uint IconId;
    /// <summary>How many the vendor has; -1 = unlimited.</summary>
    public int Amount;
    public int StackSize;
    public int MaxStackSize;
    /// <summary>PWD value of the listed stack.</summary>
    public int Value;
    /// <summary>Value of one unit (Value / StackSize for stacks).</summary>
    public int UnitValue;
    /// <summary>What one unit costs the player here (server rounding; notes at 1.15x).</summary>
    public int UnitPrice;
    public int Burden;
    /// <summary>bit0 unlimited, bit1 stackable, bit2 needs a container slot (pack/foci).</summary>
    public uint Flags;
    public uint Reserved0;
    public uint Reserved1;
    public fixed byte Name[64];
}

/// <summary>One buy line: vendor object id + how many.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct VendorTradeEntryNative
{
    public uint ObjectId;
    public int Amount;
}

/// <summary>State of the most recent trade request. 160 bytes.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal unsafe struct VendorTradeStatusNative
{
    public uint RequestId;
    /// <summary>1 queued, 2 sent (waiting for the server), 3 done, 4 refused (never sent).</summary>
    public int State;
    /// <summary>With State 3: 1 = server accepted (vendor list re-sent), 2 = server answered
    /// without re-sending the list (refused: funds/busy/space), 3 = no answer (timed out).</summary>
    public int Result;
    public int IsBuy;
    public uint VendorId;
    public int EntryCount;
    /// <summary>Buy: estimated cost (pyreals or alt currency). Sell: estimated payout.</summary>
    public int Estimate;
    public uint Reserved0;
    public fixed byte Message[128];
}



// PluginContractVersion (the current API version) lives in PluginContractVersion.cs, which
// the SDK and the launcher compile in too, so their copies can't drift from the engine's.

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
internal delegate void PluginOnLogoutDelegate();

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
internal delegate void PluginOnCombatDamageDelegate(uint damage, uint damageType, uint crit, uint isAttacker);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginOnKillNotificationDelegate(IntPtr textUtf16);

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

// v77 callbacks (exports resolved by PluginManager.ResolveUiExports, not PluginLoader):
//   void RynthPluginOnScreenChanged(int oldMode, int newMode)    - UIFlow mode change (GetScreenModeFn values)
//   void RynthPluginOnClientCleanup(void)                        - Client::Cleanup starting; no ticks after it
//   void RynthPluginOnTooltipShow(uint objectId, uint spellId)   - AC shows a tooltip (0/0: not an item or spell)
//   void RynthPluginOnTooltipHide(void)                          - that tooltip is gone
//   void RynthPluginOnDragStart(uint objectId, uint spellId, uint iconId) - a drag out of AC's UI began
//   void RynthPluginOnItemDropped(uint objectId, uint spellId, uint targetElementId) - AC caught a drop
// All cdecl, all on the plugin pump like the other events, except OnClientCleanup (see
// PluginManager.DispatchClientCleanup).


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
internal delegate int GiveObjectToCallbackDelegate(uint objectId, uint targetId, int amount);

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
internal unsafe delegate int ReadKnownSpellsCallbackDelegate(uint* spellIds, int maxCount);

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
internal delegate void Nav3DAddTriangleCallbackDelegate(float x1, float y1, float z1, float x2, float y2, float z2, float x3, float y3, float z3, uint colorArgb);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void Nav3DAddRingExCallbackDelegate(float wx, float wy, float wz, float radius, float thickness, float height, uint colorArgb);

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

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
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
internal delegate int GetCastBusyStateCallbackDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int GetUseDoneSeqCallbackDelegate();

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

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void SetRadarSuppressedCallbackDelegate(int enabled);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void SetPowerbarSuppressedCallbackDelegate(int enabled);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void SetChatSuppressedCallbackDelegate(int enabled);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate IntPtr GetEngineStatusJsonCallbackDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate IntPtr GetPluginSnapshotJsonCallbackDelegate(IntPtr pluginNameAnsi);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int SendPluginCommandCallbackDelegate(IntPtr pluginNameAnsi, IntPtr actionAnsi, IntPtr valueAnsi);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetObjectDataIdPropertyCallbackDelegate(uint objectId, uint stype, uint* value);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate IntPtr GetPluginExportJsonCallbackDelegate(IntPtr pluginNameAnsi, IntPtr exportNameAnsi);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate IntPtr GetPluginInterfaceCallbackDelegate(IntPtr pluginNameAnsi, IntPtr ifaceAnsi, uint version);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetLiveObjectIdsCallbackDelegate(uint* buffer, int capacity);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetLastUseDoneCallbackDelegate(int* seq, uint* error);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetLastWeenieErrorCallbackDelegate(int* seq, uint* error, uint* eventType, uint* objectId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int WieldItemCallbackDelegate(uint objectId, uint equipMask);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int UiSubmitCallbackDelegate(byte* data, int length);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int UiPollEventsCallbackDelegate(byte* buffer, int capacity, int* remaining);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int UiGetInfoCallbackDelegate(UiInfoNative* info);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetTradeStateCallbackDelegate(TradeStateNative* state);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetTradeItemsCallbackDelegate(int side, uint* buffer, int capacity);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int TradeOpenCallbackDelegate(uint targetId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int TradeAddCallbackDelegate(uint itemId, uint slot);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int TradeSimpleCallbackDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetVendorInfoCallbackDelegate(VendorInfoNative* info);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetVendorItemsCallbackDelegate(VendorItemNative* items, int maxCount);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate uint GetUiColorCallbackDelegate(int token);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate IntPtr GetUiFontCallbackDelegate(int font);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int SetPanelOpenCallbackDelegate(byte* key, int mode);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PanelDragWithLastItemCallbackDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int SetPanelPopOutCallbackDelegate(byte* key, int mode);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int PanelTextSizeCallbackDelegate(byte* key, int delta);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate float RenderSettingsDrawerCallbackDelegate(byte* key, float width, float room, int flags);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetShortcutsCallbackDelegate(uint* objectIds, uint* spellIds, int max);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int ShortcutActionCallbackDelegate(int op, int slot, uint objectId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetActionKeysCallbackDelegate(byte* action, byte* buffer, int length);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate IntPtr GetIconTextureCallbackDelegate(int kind, uint id);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int KeyCaptureCallbackDelegate(int op, byte* buffer, int length);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int SetActionKeyCallbackDelegate(byte* action, byte* key, int op, byte* message, int length);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int AcPanelCallbackDelegate(int id, int op);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int PanelScaleCallbackDelegate(byte* key, int percent);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate uint DropTargetCallbackDelegate(byte* window, float x0, float y0, float x1, float y1, int flags, int* dragging);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int SetAmbientLightFloorCallbackDelegate(uint rgb);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetAmbientLightCallbackDelegate(uint* client, uint* applied);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetMonsterHealthCallbackDelegate(uint id, float* ratio, uint* ageMs);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int WebPanelOpenCallbackDelegate(WebPanelOpenNative* open);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int WebPanelNavigateCallbackDelegate(int handle, byte* url);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int WebPanelCloseCallbackDelegate(int handle);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int WebPanelGetStateCallbackDelegate(int handle, WebPanelStateNative* state);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate uint VendorBuyCallbackDelegate(uint vendorId, VendorTradeEntryNative* entries, int count);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate uint VendorSellCallbackDelegate(uint vendorId, uint* itemIds, int count);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetVendorTradeStatusCallbackDelegate(VendorTradeStatusNative* status);
// Cdecl is mandatory on every API delegate: without it x86 marshals a stdcall thunk, the callee and
// the cdecl caller both pop the args, and the plugin's stack drifts on every call.
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetMergeStackResultCallbackDelegate(uint sourceObjectId, uint targetObjectId, int* amount, int* ageMs);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int ArmConfirmationCallbackDelegate(uint type, int minPercent, int timeoutMs);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetLastConfirmationCallbackDelegate(uint* type, uint* contextId, int* percent, int* outcome, int* ageMs);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetVTankStateCallbackDelegate(int* sequence);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetCharacterTitlesCallbackDelegate(uint* ids, int maxCount, uint* currentTitle);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetServerInfoCallbackDelegate(byte* worldName, int capacity);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetServerStateJsonCallbackDelegate(byte* buffer, int capacity);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetWieldedItemsCallbackDelegate(uint* ids, uint* locations, int max);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int CloseContainerCallbackDelegate(uint containerId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetScreenModeCallbackDelegate(int* previousMode);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate uint GetUiHookFlagsCallbackDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int SetServerMessageInterestCallbackDelegate(uint* opcodes, int opcodeCount, uint* gameEvents, int eventCount);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetTrainingInfoCallbackDelegate(TrainingInfoNative* info, TrainingEntryNative* entries, int maxEntries);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int RaiseCallbackDelegate(uint kind, uint stype, uint ranks, long expectedXp, long* xpSent);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int TrainSkillCallbackDelegate(uint stype, int expectedCredits);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetContainerOrderCallbackDelegate(uint containerId, int slotKind, uint* ids, int maxIds);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int PutItemInContainerCallbackDelegate(uint itemId, uint containerId, int placement);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int SalvageDirectCallbackDelegate(uint toolId, uint* itemIds, int count);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int GetObjectServerAgeCallbackDelegate(uint objectId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int RemoveClientObjectCallbackDelegate(uint objectId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate int GetObjectPropertiesCallbackDelegate(uint objectId, byte* buffer, int bufferSize);
