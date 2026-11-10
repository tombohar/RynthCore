using System;
using System.Runtime.InteropServices;

namespace RynthCore.PluginSdk;

[StructLayout(LayoutKind.Sequential)]
public struct RynthCoreApiNative
{
    public uint Version;
    public IntPtr ImGuiContext;
    public IntPtr D3DDevice;
    public IntPtr GameHwnd;
    public IntPtr LogFn;
    public IntPtr ProbeClientHooksFn;
    public IntPtr GetClientHookFlagsFn;
    public IntPtr ChangeCombatModeFn;
    public IntPtr CancelAttackFn;
    public IntPtr QueryHealthFn;
    public IntPtr MeleeAttackFn;
    public IntPtr MissileAttackFn;
    public IntPtr DoMovementFn;
    public IntPtr StopMovementFn;
    public IntPtr JumpNonAutonomousFn;
    public IntPtr SetAutonomyLevelFn;
    public IntPtr SetAutoRunFn;
    public IntPtr TapJumpFn;
    public IntPtr SetIncomingChatSuppressionFn;
    public IntPtr SelectItemFn;
    public IntPtr SetSelectedObjectIdFn;
    public IntPtr GetSelectedItemIdFn;
    public IntPtr GetPreviousSelectedItemIdFn;
    public IntPtr GetPlayerIdFn;
    public IntPtr GetGroundContainerIdFn;
    public IntPtr GetNumContainedItemsFn;
    public IntPtr GetNumContainedContainersFn;
    public IntPtr GetCurCoordsFn;
    public IntPtr UseObjectFn;
    public IntPtr UseObjectOnFn;
    public IntPtr UseEquippedItemFn;
    public IntPtr MoveItemExternalFn;
    public IntPtr MoveItemInternalFn;
    public IntPtr WriteToChatFn;
    public IntPtr GetPlayerPoseFn;
    public IntPtr IsPortalingFn;
    public IntPtr SetMotionFn;
    public IntPtr StopCompletelyFn;
    public IntPtr TurnToHeadingFn;
    public IntPtr GetPlayerHeadingFn;
    public IntPtr GetObjectNameFn;
    public IntPtr GetPlayerVitalsFn;
    public IntPtr GetObjectPositionFn;
    public IntPtr RequestIdFn;
    public IntPtr GetTargetVitalsFn;
    public IntPtr CastSpellFn;
    public IntPtr GetItemTypeFn;
    public IntPtr GetObjectIntPropertyFn;
    public IntPtr GetObjectBoolPropertyFn;
    public IntPtr ObjectIsAttackableFn;
    public IntPtr GetObjectSkillFn;
    public IntPtr IsSpellKnownFn;
    public IntPtr ReadPlayerEnchantmentsFn;
    public IntPtr GetServerTimeFn;
    public IntPtr ReadObjectEnchantmentsFn;
    public IntPtr WorldToScreenFn;
    public IntPtr GetViewportSizeFn;
    public IntPtr Nav3DClearFn;
    public IntPtr Nav3DAddRingFn;
    public IntPtr Nav3DAddLineFn;
    public IntPtr InvokeChatParserFn;
    public IntPtr GetObjectDoublePropertyFn;
    public IntPtr GetObjectQuadPropertyFn;
    public IntPtr GetObjectAttribute2ndBaseLevelFn;
    public IntPtr GetPlayerBaseVitalsFn;
    public IntPtr GetObjectStringPropertyFn;
    public IntPtr GetObjectWielderInfoFn;
    public IntPtr NativeAttackFn;
    public IntPtr IsPlayerReadyFn;
    public IntPtr SetFpsLimitFn;
    public IntPtr GetContainerContentsFn;
    public IntPtr GetObjectOwnershipInfoFn;
    public IntPtr SplitStackInternalFn;
    public IntPtr MergeStackInternalFn;
    public IntPtr GetCurrentCombatModeFn;
    public IntPtr SalvagePanelOpenFn;
    public IntPtr SalvagePanelAddItemFn;
    public IntPtr SalvagePanelExecuteFn;
    public IntPtr GetVitaeFn;
    public IntPtr GetAccountNameFn;
    public IntPtr GetWorldNameFn;
    public IntPtr GetObjectWcidFn;
    public IntPtr HasAppraisalDataFn;
    public IntPtr GetLastIdTimeFn;
    public IntPtr GetObjectHeadingFn;
    public IntPtr GetBusyStateFn;
    public IntPtr GetObjectSpellIdsFn;
    public IntPtr GetObjectSkillBuffedFn;
    public IntPtr GetObjectAttributeFn;
    public IntPtr GetObjectMotionOnFn;
    public IntPtr GetObjectStateFn;
    public IntPtr GetObjectBitfieldFn;
    public IntPtr ForceResetBusyCountFn;
    public IntPtr GetObjectPalettesFn;
    public IntPtr CommenceJumpFn;
    public IntPtr DoJumpFn;
    public IntPtr LaunchJumpWithMotionFn;
    public IntPtr GetRadarRectFn;
    public IntPtr SetRadarSuppressedFn;
    public IntPtr SetChatSuppressedFn;
    public IntPtr SetPowerbarSuppressedFn;
    public IntPtr GetCastBusyStateFn;
    public IntPtr ReadKnownSpellsFn;
    public IntPtr Nav3DAddTriangleFn;
    public IntPtr Nav3DAddRingExFn;
    // APPEND-AT-END (ABI): must mirror PluginContract.RynthCoreAPI order exactly. v62+.
    public IntPtr GiveObjectToFn;
    public IntPtr GetUseDoneSeqFn; // v63: monotonic count of inbound server UseDone (0x1C7)
    public IntPtr GetEngineStatusJsonFn;   // v64: engine-side status fields as ANSI JSON (excludes bot)
    public IntPtr GetPluginSnapshotJsonFn; // v64: broker a named plugin's RynthPluginGetSnapshotJson
    public IntPtr SendPluginCommandFn;     // v64: forward (action,value) to a named plugin's command export
    public IntPtr GetObjectDataIdPropertyFn; // v65: read a PWD DataID property (Icon=8 → _iconID) for inventory icons
    public IntPtr GetPluginExportJsonFn;     // v66: broker any RynthPluginGet*Json export on a named plugin
    // v67: vendor trading (see VendorTypes.cs / RynthCoreHost vendor section)
    public IntPtr GetVendorInfoFn;           // int  GetVendorInfo(VendorInfoNative*)
    public IntPtr GetVendorItemsFn;          // int  GetVendorItems(VendorItemNative*, int maxCount)
    public IntPtr VendorBuyFn;               // uint VendorBuy(uint vendorId, VendorTradeEntryNative*, int count)
    public IntPtr VendorSellFn;              // uint VendorSell(uint vendorId, uint* itemIds, int count)
    public IntPtr GetVendorTradeStatusFn;    // int  GetVendorTradeStatus(VendorTradeStatusNative*)
    public IntPtr GetPluginInterfaceFn;      // v68: void* GetPluginInterface(const char* plugin, const char* iface, uint version)
    public IntPtr GetLiveObjectIdsFn;        // v69: int GetLiveObjectIds(uint* buffer, int capacity) -> total count
    // v70: action outcomes + wield-to-slot (and the engine raises OnEnchantmentAdded/Removed)
    public IntPtr GetLastUseDoneFn;          // int GetLastUseDone(int* seq, uint* error)
    public IntPtr GetLastWeenieErrorFn;      // int GetLastWeenieError(int* seq, uint* error, uint* eventType, uint* objectId)
    public IntPtr WieldItemFn;               // int WieldItem(uint objectId, uint equipMask)
    // v71: script windows (display list in, events out; RynthSuite Docs/RYNTHLUA_WINDOWS_DESIGN.md §4)
    public IntPtr UiSubmitFn;                // int UiSubmit(const uint8_t* data, int length)
    public IntPtr UiPollEventsFn;            // int UiPollEvents(uint8_t* buffer, int capacity, int* remaining)
    public IntPtr UiGetInfoFn;               // int UiGetInfo(UiInfoNative* info)
    // v72: player-to-player trade (state polled; actions queued for AC's main thread)
    public IntPtr GetTradeStateFn;           // int GetTradeState(TradeStateNative* state)
    public IntPtr GetTradeItemsFn;           // int GetTradeItems(int side, uint* buffer, int capacity) -> total
    public IntPtr TradeOpenFn;               // int TradeOpen(uint targetId)
    public IntPtr TradeAddFn;                // int TradeAdd(uint itemId, uint slot)
    public IntPtr TradeAcceptFn;             // int TradeAccept(void)
    public IntPtr TradeDeclineFn;            // int TradeDecline(void)
    public IntPtr TradeResetFn;              // int TradeReset(void)
    public IntPtr TradeCloseFn;              // int TradeClose(void)
    // v73: every property type off the main thread
    public IntPtr GetObjectInstanceIdPropertyFn; // int GetObjectInstanceIdProperty(uint objectId, uint stype, uint* value)
    // v74: VTank's macro under the Decal bridge (one bot per client)
    public IntPtr GetVTankStateFn;           // int GetVTankState(int* sequence) -> flags (bit0 watching, bit1 running)
    // v75: the character's titles and which server this is
    public IntPtr GetCharacterTitlesFn;      // int GetCharacterTitles(uint* ids, int maxCount, uint* currentTitle) -> count, -1 unknown
    public IntPtr GetServerInfoFn;           // int GetServerInfo(byte* worldName, int capacity) -> flags (bit0 Aelrynth, bit1 staging, bit2 world name known)
    // v76: close an external container (corpse, chest) like the client's window close
    public IntPtr CloseContainerFn;          // int CloseContainer(uint containerId) -> 1 sent or queued
    // v77: the client's screen (UIFlow mode) and which UI hooks (screen, cleanup, tooltip, drag, drop) are live
    public IntPtr GetScreenModeFn;           // int GetScreenMode(int* previousMode) -> mode, 0 unknown
    public IntPtr GetUiHookFlagsFn;          // uint GetUiHookFlags(void) -> RynthUiHookFlags
    // v78: reassembled server messages (RynthPluginOnServerMessage export; see Net\ServerMessage.cs)
    public IntPtr SetServerMessageInterestFn; // int SetServerMessageInterest(uint* opcodes, int n, uint* gameEvents, int m) -> 1 streaming, 2 stream off, 0 no export, -1 unknown caller, -3 bad args
    // v79: spending experience like the character window's "+" (see TrainingTypes.cs)
    public IntPtr GetTrainingInfoFn;         // int GetTrainingInfo(TrainingInfoNative* info, TrainingEntryNative* entries, int max) -> entries there are
    public IntPtr RaiseFn;                   // int Raise(uint kind, uint stype, uint ranks, long expectedXp, long* xpSent) -> RaiseResult
    public IntPtr TrainSkillFn;              // int TrainSkill(uint stype, int expectedCredits) -> RaiseResult
    // v80: real slot order of a container's items, and moves that land at a slot
    public IntPtr GetContainerOrderFn;       // int GetContainerOrder(uint container, int slotKind, uint* ids, int max) -> count, -1 unknown
    public IntPtr PutItemInContainerFn;      // int PutItemInContainer(uint item, uint container, int placement) -> 1 sent, 0 refused, -1 unbound
    // v81: outcome of the latest merge-stack request (cdecl thunk); was v77 on QOL-items, v80 on sk
    public IntPtr GetMergeStackResultFn;     // int GetMergeStackResult(uint src, uint tgt, int* amount, int* ageMs)
    // v82: salvage without the salvage panel (the 0x027D game action, AC's own sender)
    public IntPtr SalvageDirectFn;           // int SalvageDirect(uint tool, uint* ids, int count) -> count sent, 0 refused, -1 unbound
    // v83: ghost creatures (objects the client draws but the server no longer has for this player)
    public IntPtr GetObjectServerAgeFn;      // int GetObjectServerAge(uint id) -> ms since the server's last message about it, -1 none
    public IntPtr RemoveClientObjectFn;      // int RemoveClientObject(uint id) -> 1 removed, 2 queued, 0 refused, -1 unbound
    // v84: every property of an object (/ub propertydump), one record (ObjectProperties.TryParse)
    public IntPtr GetObjectPropertiesFn;     // int GetObjectProperties(uint id, byte* buf, int size) -> record size (copied if <= size), 0 unknown, -1 bad args
    // v85: answer the server's yes/no prompt (craft chance) without the client's dialog
    public IntPtr ArmConfirmationFn;         // int ArmConfirmation(uint type, int minPercent, int timeoutMs) -> 1 armed, 0 unavailable, -3 bad args
    public IntPtr GetLastConfirmationFn;     // int GetLastConfirmation(uint* type, uint* ctx, int* percent, int* outcome, int* ageMs) -> seq
    // v86: which server this is, for every plugin (servers, verdicts, overrides, owning plugins)
    public IntPtr GetServerStateJsonFn;      // int GetServerStateJson(byte* buffer, int capacity) -> UTF-8 byte length (>= capacity: too small)
    // v87: what the player wields, from the server's messages (the client's own fields go stale)
    public IntPtr GetWieldedItemsFn;         // int GetWieldedItems(uint* ids, uint* locations, int max) -> count, -1 not ready
    // v89: plugin windows (RynthCore docs/PLUGIN_WINDOWS.md): the engine's look, and panels by key
    public IntPtr GetUiColorFn;              // uint GetUiColor(int token) -> ABGR, 0 unknown
    public IntPtr GetUiFontFn;               // void* GetUiFont(int font) -> ImFont*, null unknown / off AC's thread
    public IntPtr SetPanelOpenFn;            // int SetPanelOpen(const char* key, int mode) -> 1 open, 0 closed, -1 no such panel
    // v90: plugin windows that draw their own frame ("chrome": "none")
    public IntPtr PanelDragWithLastItemFn;   // void PanelDragWithLastItem(void): the panel being drawn follows a drag of ImGui's last item
    public IntPtr SetPanelPopOutFn;          // int SetPanelPopOut(const char* key, int mode) -> 1 popped out, 0 docked, -1 no such open panel
    public IntPtr PanelTextSizeFn;           // int PanelTextSize(const char* key, int delta) -> text size in % (100..250), -1 no such panel
    public IntPtr RenderSettingsDrawerFn;    // float RenderSettingsDrawer(const char* key, float width, float room, int flags) -> wanted height, -1 unknown
    public IntPtr GetShortcutsFn;            // int GetShortcuts(uint* objectIds, uint* spellIds, int max) -> 18, or 0 out of the world
    public IntPtr ShortcutActionFn;          // int ShortcutAction(int op, int slot, uint objectId) -> 1 queued, 0 refused
    public IntPtr GetActionKeysFn;           // int GetActionKeys(const char* action, char* buffer, int length) -> bytes needed, -1 bad call
    public IntPtr GetIconTextureFn;          // void* GetIconTexture(int kind, uint id) -> ImGui texture id, null while it is made
    public IntPtr KeyCaptureFn;              // int KeyCapture(int op, char* buffer, int length) -> 0 waiting, 1 key (name in buffer), 2 cancelled
    public IntPtr SetActionKeyFn;            // int SetActionKey(const char* action, const char* key, int op, char* message, int length); op 0 check, 1 bind, 2 unbind, 3 command hotkey only (after 2026.10.27.1)
    public IntPtr AcPanelFn;                 // int AcPanel(int id, int op) -> op 0: 1 open, 0 closed, -1 none; else 1 queued, -1 refused
    public IntPtr PanelScaleFn;              // int PanelScale(const char* key, int percent) -> percent now, 0 = follows the text size
    public IntPtr DropTargetFn;              // uint DropTarget(const char* window, float x0, y0, x1, y1, int flags, int* dragging) -> dropped object, 0 none
    // v91: a floor under the world's ambient light (drawing only, this client)
    public IntPtr SetAmbientLightFloorFn;    // int SetAmbientLightFloor(uint rgb) -> 1 hook live, 0 not; 0x00RRGGBB, 0 = none
    public IntPtr GetAmbientLightFn;         // int GetAmbientLight(uint* client, uint* applied) -> 1 hook live, 0 not
    // v93: the monster plates' last server health reading per object
    public IntPtr GetMonsterHealthFn;        // int GetMonsterHealth(uint id, float* ratio, uint* ageMs) -> 1 reading, 0 none
    // v94: a WebView2 web panel over the game window (APPEND-AT-END (ABI); number provisional)
    public IntPtr WebPanelOpenFn;            // int WebPanelOpen(WebPanelOpenNative*) -> handle > 0, or BadArgs/UrlNotAllowed/NoGameWindow/Unavailable/ShuttingDown
    public IntPtr WebPanelNavigateFn;        // int WebPanelNavigate(int handle, byte* url) -> 1 queued; -2 off-list; -6 stale
    public IntPtr WebPanelCloseFn;           // int WebPanelClose(int handle) -> 1 queued, 0 already closed, -6 stale
    public IntPtr WebPanelGetStateFn;        // int WebPanelGetState(int handle, WebPanelStateNative*) -> Status (non-live handle -> 6 Superseded), -1 bad args
}

/// <summary>
/// v72 GetTradeState result (Pack 4, 96 bytes; later versions only append).
/// Mirrors RynthCore.Engine/Plugins/PluginContract.cs TradeStateNative exactly.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public unsafe struct TradeStateNative
{
    /// <summary>In: sizeof(TradeStateNative). Out: bytes the engine wrote.</summary>
    public uint Size;
    /// <summary>bit0 open, bit1 you accepted, bit2 partner accepted, bit3 engine sees trade
    /// events, bit4 all trade actions bound.</summary>
    public uint Flags;
    /// <summary>+1 for every trade that opens.</summary>
    public uint Generation;
    /// <summary>+1 for every trade event.</summary>
    public uint Sequence;
    public uint PartnerId;
    public uint InitiatorId;
    public int SelfItemCount;
    public int PartnerItemCount;
    public uint LastEventType;
    public uint FailureCount;
    public uint LastFailureItemId;
    public uint LastFailureReason;
    public uint LastCloseReason;
    public uint LastAcceptedBy;
    public uint LastDeclinedBy;
    public uint LastResetBy;
    public uint CompletedCount;
    public uint PartnerAcceptCount;
    public fixed uint Reserved[6];
}

/// <summary>
/// v71 UiGetInfo result (Pack 4, 480 bytes in format 1; later versions only append).
/// Mirrors RynthCore.Engine/Plugins/PluginContract.cs UiInfoNative exactly.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public unsafe struct UiInfoNative
{
    /// <summary>In: sizeof(UiInfoNative). Out: bytes the engine wrote.</summary>
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
