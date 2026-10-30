using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace RynthCore.Engine.Compatibility;

internal static class ClientHelperHooks
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetSelectedObjectDelegate(uint objectId, int reselect);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GetItemIdDelegate(out uint objectId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetAcPluginDelegate();

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void UseObjectOnDelegate(IntPtr acPlugin, uint sourceObjectId, uint targetObjectId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte UseWithTargetEventDelegate(uint objectId, uint targetId);

    // CM_Inventory::SendNotice_OpenSalvagePanel(uint toolId) — cdecl
    // Map: 002AC4F0 → live VA: 0x006AD4F0
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte SendNoticeOpenSalvagePanelDelegate(uint toolId);

    // gmSalvageUI::AddNewItem(uint itemId) — thiscall
    // Map: 000CB020 → live VA: 0x004CC020
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void GmSalvageUIAddNewItemDelegate(IntPtr thisPtr, uint itemId);

    // gmSalvageUI::Salvage(void) — thiscall
    // Map: 000CB430 → live VA: 0x004CC430
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void GmSalvageUISalvageDelegate(IntPtr thisPtr);

    // CM_Inventory::Event_CreateTinkeringTool(ulong toolId, PackableList<ulong> const& items) - cdecl,
    // returns bool. The 0x027D CreateTinkeringTool game action: what gmSalvageUI::Salvage (the panel's
    // Salvage button) sends, with the panel's tool (+0x608) and the ids of the items in its list. Chorizite
    // map 002AB790 -> live VA 0x006AC790; its one caller is gmSalvageUI::Salvage (call at 0x004CC4DB).
    // Same generated Event_* template as Event_DropItem etc., except the list is packed through its own
    // vtable (slot 3, PackableList<ulong>::Pack, called once with size 0 for the size, then to write):
    // wire = opcode, toolId, count, ids. ACE: GameActionCreateTinkeringTool -> Player.HandleSalvaging.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte EventCreateTinkeringToolDelegate(uint toolId, IntPtr packableList);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void UseEquippedItemDelegate(IntPtr acPlugin, uint sourceObjectId, uint targetObjectId);

    // NOTE: The stubs at 0x0055A9E0 / 0x0055AA00 are stdcall forwarders that
    // completely ignore their `this` parameter. They read args off the stack
    // starting at [ESP+0x08] and RET with full callee-cleanup (0x10 / 0x14).
    // We declare these stdcall so the caller pushes all 4/5 slots and the
    // callee cleanup matches — this lets us pass IntPtr.Zero as `this` and
    // avoid the DAT_00871054 global that's only set by the Decal plugin loader.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void MoveItemExternalDelegate(IntPtr acPlugin, uint objectId, uint targetContainerId, int amount);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void MoveItemInternalDelegate(IntPtr acPlugin, uint objectId, uint targetContainerId, int slot, int amount);

    // CM_Inventory::Event_StackableMerge — the canonical public API for
    // merging two stacks. Cdecl, returns bool. Confirmed via Chorizite map:
    //   002ABDD0 CM_Inventory::Event_StackableMerge(ulong,ulong,long)
    //   live VA: 0x006ACDD0
    // This is the correct entry point — sends opcode 0x1A AND wires up the
    // client-side action queue properly. The earlier FUN_006AC950 was an
    // inner helper (packet-only), and FUN_0058E3C0 is the wield wrapper
    // (NOT merge — it tried to equip Pyreals).
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte EventStackableMergeDelegate(uint mergeFromId, uint mergeToId, int amount);

    // CM_Inventory::Event_GiveObjectRequest — the public give-item-to-NPC API.
    // Byte-identical sibling of Event_StackableMerge (differs only in the action
    // opcode immediate: give writes 0xCD, merge 0x54). Cdecl, returns bool.
    // Chorizite map: 002ACA60 -> live VA 0x006ACA60. Sends the F7B1 give
    // GameAction. amount = stack count to give (ACE refuses amount <= 0).
    // Args: TARGET FIRST, then the item, then amount. The function writes 0xCD, then
    // [esp+0x1C], [esp+0x20], [esp+0x24] in that order, and ACE's GiveObjectRequest reads
    // targetGuid, objectGuid, amount. Its one caller, ACCWeenieObject::UIAttemptGive
    // (call at 0x0058E46F), pushes amount, this->id (the item, [esi+8]), then the target.
    // (Re-checked 2026-10-01; the old delegate had the two ids swapped.)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte EventGiveObjectRequestDelegate(uint targetId, uint objectId, int amount);

    // CM_Inventory::Event_PutItemInContainer(item, container, placement) @0x006ACC80 - the 0x19
    // PutItemInContainer game action, the packet only: item, container, then the SLOT in that
    // container (what a drop on a cell of the pack grid asks for; retail and OpenAC both send it).
    // MoveItemInternal (the native move) always lands an item in the first empty slot. Same
    // template as Event_GiveObjectRequest (0x10 after the first inner call, arguments written in
    // wire order) ending in the 0x19 opcode. Unique (1 match @0x006ACC80) in both acclient copies,
    // checked 2026-10-09 by deriving the pattern from the give pattern.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte EventPutItemInContainerDelegate(uint itemId, uint containerId, int placement);

    // CM_Inventory::Event_GetAndWieldItem(ulong itemId, ulong equipMask) — the 0x001A
    // GetAndWieldItem game action ("wield this item at these locations"). Same generated
    // shape as its two siblings above (cdecl, returns bool), opcode immediate 0x1A.
    // Chorizite map: 002AB950 -> live VA 0x006AC950 (the "FUN_006AC950" once mistaken for
    // the merge helper, which is why that attempt tried to equip Pyreals). Arg order from
    // the disassembly (itemId at [esp+0x1C], mask at [esp+0x20] after the prologue) and the
    // one caller, ACCWeenieObject::UIAttemptWield @0x0058E3C0: push mask; push this->id.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte EventGetAndWieldItemDelegate(uint objectId, uint equipMask);

    // CM_Inventory::Event_DropItem(ulong itemId) — the 0x001B DropItem game action (put the
    // item on the ground at the player's feet). Same generated shape as the three above
    // (cdecl, returns bool) with one argument, so +0x08 after the first inner call and the
    // 0x1B opcode as the last pattern byte. Chorizite map entrypoint 0x006AC880; its caller
    // is ACCWeenieObject::UIAttemptPutIn3D (call at 0x0058E545). Arg: itemId at [esp+0x1C]
    // after the prologue (sub esp,0Ch + three pushes).
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte EventDropItemDelegate(uint objectId);

    // CM_Inventory::Event_NoLongerViewingContents(ulong containerId) - the 0x0195
    // NoLongerViewingContents game action, the packet the client sends to close an external
    // container (corpse, chest). Same generated shape as Event_DropItem (cdecl, returns bool,
    // one argument) with opcode 0x0195. Entry 0x006ACBB0. Its one caller is
    // ClientUISystem::SetGroundContainer(newId, notifyServer) @0x005652B0, which sends it for
    // the old ground container when notifyServer is set: the container window's close and
    // opening a second container both go through there. ACE (Player.HandleActionNoLongerViewingContents)
    // closes the container if this player is its viewer and answers with CloseGroundContainer
    // (0x0052); the client's own handler for that tears the window down
    // (SetGroundContainer(0, false)), clears the pending-request slot for that container
    // (ACCWeenieObject::RecordResponse) and runs ACCObjectMaint::StopViewingObjectContents.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte EventNoLongerViewingContentsDelegate(uint containerId);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void UseObjectDelegate(IntPtr clientUiSystem, uint objectId);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate byte InqPlayerCoordsDelegate(IntPtr playerSystem, out double northSouth, out double eastWest);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint GetPlayerIdDelegate();

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int AddTextToScrollDelegate(
        IntPtr clientSystem,
        ref WidePString text,
        uint chatType,
        byte unknown,
        IntPtr stringInfo);

    private const int SetSelectedObjectVa = 0x0058D110;
    private const int SelectedIdVa = 0x00871E54;
    private const int PreviousSelectedIdVa = 0x00871E58;
    private const int GetAcPluginVa = 0x0055A740;
    private const int UseObjectOnVa = 0x0055A8C0;
    private const int UseEquippedItemVa = 0x0055A910;
    private const int MoveItemExternalVa = 0x0055A9E0;
    private const int MoveItemInternalVa = 0x0055AA00;
    // Globals read by FUN_00588f70 to decide whole-move (opcode 0x19) vs
    // split-to-slot (opcode 0x55). Normally set by UI drag-drop code; we
    // write them directly to force the split path for stack merges.
    private const int SplitAmountVa = 0x0081D7EC; // split_amount
    private const int TotalStackVa = 0x0081D7F0;  // total_stack
    // CM_Inventory::Event_StackableMerge — public merge-stack API (from Chorizite map).
    private const int EventStackableMergeVa = 0x006ACDD0;
    // CM_Inventory::Event_GiveObjectRequest — public give-to-NPC API (FALLBACK only; pattern-scanned).
    private const int EventGiveObjectRequestVa = 0x006ACA60;
    private const int EventPutItemInContainerVa = 0x006ACC80;
    // CM_Inventory::Event_GetAndWieldItem — wield into a chosen slot (FALLBACK only; pattern-scanned).
    private const int EventGetAndWieldItemVa = 0x006AC950;
    // CM_Inventory::Event_DropItem — drop an item on the ground (FALLBACK only; pattern-scanned).
    private const int EventDropItemVa = 0x006AC880;
    // CM_Inventory::Event_NoLongerViewingContents - close an external container (FALLBACK only; pattern-scanned).
    private const int EventNoLongerViewingContentsVa = 0x006ACBB0;
    private const int UseObjectVa = 0x00565750;
    private const int PlayerSystemVa = 0x0087119C;
    private const int InqPlayerCoordsVa = 0x00560E00;
    private const int UiSystemVa = 0x00871354;
    private const int GetPlayerIdVa = 0x0048E5F0;
    private const int CommunicationSystemVa = 0x00870BE4;
    private const int AddTextToScrollVa = 0x005649F0;
    private const int UseWithTargetEventVa = 0x006AD3E0;
    private const int SendNoticeOpenSalvagePanelVa = 0x006AD4F0;
    private const int GmSalvageUIAddNewItemVa = 0x004CC020;
    private const int GmSalvageUISalvageVa = 0x004CC430;
    // CM_Inventory::Event_CreateTinkeringTool (FALLBACK only; pattern-scanned).
    private const int EventCreateTinkeringToolVa = 0x006AC790;
    // PackableList<ulong>'s vtable (.rdata), FALLBACK only: read from the immediate gmSalvageUI::Salvage
    // writes into its local list (mov [esp+10h], offset vtable @0x004CC448).
    private const int PackableListULongVtableVa = 0x007A4ED0;

    private static SetSelectedObjectDelegate? _setSelectedObject;
    private static UseObjectDelegate? _useObject;
    private static GetAcPluginDelegate? _getAcPlugin;
    private static UseObjectOnDelegate? _useObjectOn;
    private static UseEquippedItemDelegate? _useEquippedItem;
    private static MoveItemExternalDelegate? _moveItemExternal;
    private static MoveItemInternalDelegate? _moveItemInternal;
    private static EventStackableMergeDelegate? _eventStackableMerge;
    private static EventGiveObjectRequestDelegate? _eventGiveObjectRequest;
    private static EventPutItemInContainerDelegate? _eventPutItemInContainer;
    private static EventGetAndWieldItemDelegate? _eventGetAndWieldItem;
    private static EventDropItemDelegate? _eventDropItem;
    private static EventNoLongerViewingContentsDelegate? _eventNoLongerViewingContents;
    private static InqPlayerCoordsDelegate? _inqPlayerCoords;
    private static GetPlayerIdDelegate? _getPlayerId;
    private static AddTextToScrollDelegate? _addTextToScroll;
    private static UseWithTargetEventDelegate? _useWithTargetEvent;
    private static SendNoticeOpenSalvagePanelDelegate? _sendNoticeOpenSalvagePanel;
    private static GmSalvageUIAddNewItemDelegate? _gmSalvageUIAddNewItem;
    private static GmSalvageUISalvageDelegate? _gmSalvageUISalvage;
    private static EventCreateTinkeringToolDelegate? _eventCreateTinkeringTool;
    private static IntPtr _packableListULongVtable;
    private static bool _initialized;
    private static string _statusMessage = "Not probed yet.";
    private static int _interactionLogCount;

    public static bool IsInitialized => _initialized;
    public static string StatusMessage => _statusMessage;
    public static bool HasSelectItem => _setSelectedObject != null;
    public static bool HasSetSelectedObjectId => _setSelectedObject != null;
    public static bool HasGetSelectedItemId => true;
    public static bool HasGetPreviousSelectedItemId => true;
    public static bool HasUseObject => _useObject != null;
    public static bool HasUseObjectOn => _useObjectOn != null;
    public static bool HasUseEquippedItem => _useEquippedItem != null;
    public static bool HasMoveItemExternal => _moveItemExternal != null;
    public static bool HasMoveItemInternal => _moveItemInternal != null;
    public static bool HasSplitStackInternal => _moveItemInternal != null;
    public static bool HasMergeStackInternal => _eventStackableMerge != null;
    public static bool HasGiveObjectTo => _eventGiveObjectRequest != null;
    public static bool HasPutItemInContainer => _eventPutItemInContainer != null;
    public static bool HasWieldItem => _eventGetAndWieldItem != null;
    public static bool HasDropItem => _eventDropItem != null;
    public static bool HasCloseContainer => _eventNoLongerViewingContents != null;
    public static bool HasGetCurCoords => _inqPlayerCoords != null;
    public static bool HasGetPlayerId => _getPlayerId != null;
    public static bool HasGetGroundContainerId => true;
    public static bool HasWriteToChat => _addTextToScroll != null;
    public static bool HasInvokeParser => true;
    public static bool HasSalvagePanel => _sendNoticeOpenSalvagePanel != null && _gmSalvageUIAddNewItem != null && _gmSalvageUISalvage != null;
    /// <summary>v82: <see cref="SalvageDirect"/> (the 0x027D salvage request without the panel).</summary>
    public static bool HasSalvageDirect => _eventCreateTinkeringTool != null && _packableListULongVtable != IntPtr.Zero;
    /// <summary>Most item ids one <see cref="SalvageDirect"/> request carries. ACE reads any count; 100 ids
    /// keep the game action (header + tool + count + ids, 420 bytes) inside one network fragment.</summary>
    public const int MaxSalvageDirectItems = 100;
    private static int _currentGroundContainerId;

    // ── Pattern-resolved binding (1a hardening, 2026-06-05) ─────────────
    // The *Va consts above are FALLBACKs; these signatures (verified unique + landing exactly
    // at the VA offline via tools/pe_pattern.py) are the source of truth, surviving AC-patch /
    // ACE-rebuild drift. UseObjectOn/UseEquippedItem are sibling templates differing only in a
    // trailing immediate (0x00 vs 0x01) — both run long to stay unique. null = wildcard (rel32).
    private static readonly byte?[] PatSetSelectedObject = [ 0x8B, 0x4C, 0x24, 0x08, 0x85, 0xC9, 0xA1, 0x54 ];
    private static readonly byte?[] PatUseObject = [ 0x8B, 0x44, 0x24, 0x04, 0x85, 0xC0, 0x56, 0x8B, 0xF1, 0x74, 0x11 ];
    private static readonly byte?[] PatGetAcPlugin = [ 0xA1, 0x54, 0x10, 0x87, 0x00, 0x85, 0xC0, 0x74, 0x03 ];
    private static readonly byte?[] PatUseObjectOn = [ 0x51, 0x56, 0x8B, 0x74, 0x24, 0x0C, 0x8B, 0x06, 0x8D, 0x4C, 0x24, 0x04, 0x51, 0x56, 0xC7, 0x44, 0x24, 0x0C, 0x00, 0x00, 0x00, 0x00, 0xFF, 0x50, 0x20, 0x8B, 0x44, 0x24, 0x14, 0x8B, 0x16, 0x50, 0x56, 0xFF, 0x52, 0x18, 0x8B, 0x4C, 0x24, 0x10, 0x6A, 0x00 ];
    private static readonly byte?[] PatUseEquippedItem = [ 0x51, 0x56, 0x8B, 0x74, 0x24, 0x0C, 0x8B, 0x06, 0x8D, 0x4C, 0x24, 0x04, 0x51, 0x56, 0xC7, 0x44, 0x24, 0x0C, 0x00, 0x00, 0x00, 0x00, 0xFF, 0x50, 0x20, 0x8B, 0x44, 0x24, 0x14, 0x8B, 0x16, 0x50, 0x56, 0xFF, 0x52, 0x18, 0x8B, 0x4C, 0x24, 0x10, 0x6A, 0x01 ];
    private static readonly byte?[] PatMoveItemExternal = [ 0x8B, 0x44, 0x24, 0x10, 0x8B, 0x4C, 0x24, 0x0C, 0x8B, 0x54, 0x24, 0x08, 0x50, 0x51, 0x52, 0xE8 ];
    private static readonly byte?[] PatMoveItemInternal = [ 0x8B, 0x44, 0x24, 0x10, 0x8B, 0x4C, 0x24, 0x14, 0x8B, 0x54, 0x24, 0x0C ];
    private static readonly byte?[] PatEventStackableMerge = [ 0x83, 0xEC, 0x0C, 0x53, 0x56, 0x57, 0xE8, null, null, null, null, 0x89, 0x44, 0x24, 0x14, 0x6A, 0x00, 0x8D, 0x44, 0x24, 0x10, 0x50, 0x8D, 0x4C, 0x24, 0x18, 0xC7, 0x44, 0x24, 0x18, 0x2C, 0x2C, 0x80, 0x00, 0xC7, 0x44, 0x24, 0x14, 0x00, 0x00, 0x00, 0x00, 0xE8, null, null, null, null, 0x8B, 0xF0, 0x83, 0xC6, 0x10, 0x56, 0xE8, null, null, null, null, 0x83, 0xC4, 0x04, 0x56, 0x8D, 0x4C, 0x24, 0x10, 0x51, 0x8D, 0x4C, 0x24, 0x18, 0x89, 0x44, 0x24, 0x14, 0x8B, 0xF8, 0xE8, null, null, null, null, 0x8B, 0x54, 0x24, 0x0C, 0x8B, 0x4C, 0x24, 0x1C, 0xC7, 0x02, 0x54 ];
    // CM_Inventory::Event_GiveObjectRequest @0x006ACA60. Identical to the merge
    // pattern except the trailing action-opcode write (give 0xCD vs merge 0x54) —
    // that last byte is the uniqueness discriminator vs the merge/usewith siblings,
    // so do NOT trim it. Generated + verified unique (1 match @0x006ACA60) via
    // pe_pattern.py GEN against both acclient copies.
    private static readonly byte?[] PatEventGiveObjectRequest = [ 0x83, 0xEC, 0x0C, 0x53, 0x56, 0x57, 0xE8, null, null, null, null, 0x89, 0x44, 0x24, 0x14, 0x6A, 0x00, 0x8D, 0x44, 0x24, 0x10, 0x50, 0x8D, 0x4C, 0x24, 0x18, 0xC7, 0x44, 0x24, 0x18, 0x2C, 0x2C, 0x80, 0x00, 0xC7, 0x44, 0x24, 0x14, 0x00, 0x00, 0x00, 0x00, 0xE8, null, null, null, null, 0x8B, 0xF0, 0x83, 0xC6, 0x10, 0x56, 0xE8, null, null, null, null, 0x83, 0xC4, 0x04, 0x56, 0x8D, 0x4C, 0x24, 0x10, 0x51, 0x8D, 0x4C, 0x24, 0x18, 0x89, 0x44, 0x24, 0x14, 0x8B, 0xF8, 0xE8, null, null, null, null, 0x8B, 0x54, 0x24, 0x0C, 0x8B, 0x4C, 0x24, 0x1C, 0xC7, 0x02, 0xCD ];
    private static readonly byte?[] PatEventPutItemInContainer = [  0x83, 0xEC, 0x0C, 0x53, 0x56, 0x57, 0xE8, null, null, null, null, 0x89, 0x44, 0x24, 0x14, 0x6A, 0x00, 0x8D, 0x44, 0x24, 0x10, 0x50, 0x8D, 0x4C, 0x24, 0x18, 0xC7, 0x44, 0x24, 0x18, 0x2C, 0x2C, 0x80, 0x00, 0xC7, 0x44, 0x24, 0x14, 0x00, 0x00, 0x00, 0x00, 0xE8, null, null, null, null, 0x8B, 0xF0, 0x83, 0xC6, 0x10, 0x56, 0xE8, null, null, null, null, 0x83, 0xC4, 0x04, 0x56, 0x8D, 0x4C, 0x24, 0x10, 0x51, 0x8D, 0x4C, 0x24, 0x18, 0x89, 0x44, 0x24, 0x14, 0x8B, 0xF8, 0xE8, null, null, null, null, 0x8B, 0x54, 0x24, 0x0C, 0x8B, 0x4C, 0x24, 0x1C, 0xC7, 0x02, 0x19 ];
    // CM_Inventory::Event_GetAndWieldItem @0x006AC950. Same template as the two above but with
    // one fewer argument (so +0x0C, not +0x10, after the first inner call) and the 0x1A opcode
    // as the last byte. Generated + verified unique (1 match @0x006AC950) via pe_pattern.py GEN
    // against both acclient copies (C:\Turbine and C:\Games\RynthCore\AcClient).
    private static readonly byte?[] PatEventGetAndWieldItem = [ 0x83, 0xEC, 0x0C, 0x53, 0x56, 0x57, 0xE8, null, null, null, null, 0x89, 0x44, 0x24, 0x14, 0x6A, 0x00, 0x8D, 0x44, 0x24, 0x10, 0x50, 0x8D, 0x4C, 0x24, 0x18, 0xC7, 0x44, 0x24, 0x18, 0x2C, 0x2C, 0x80, 0x00, 0xC7, 0x44, 0x24, 0x14, 0x00, 0x00, 0x00, 0x00, 0xE8, null, null, null, null, 0x8B, 0xF0, 0x83, 0xC6, 0x0C, 0x56, 0xE8, null, null, null, null, 0x83, 0xC4, 0x04, 0x56, 0x8D, 0x4C, 0x24, 0x10, 0x51, 0x8D, 0x4C, 0x24, 0x18, 0x89, 0x44, 0x24, 0x14, 0x8B, 0xF8, 0xE8, null, null, null, null, 0x8B, 0x54, 0x24, 0x0C, 0x8B, 0x4C, 0x24, 0x1C, 0xC7, 0x02, 0x1A ];
    // CM_Inventory::Event_DropItem @0x006AC880. The same template again with one argument
    // (+0x08 after the first inner call) and the 0x1B opcode as the last byte. Verified
    // unique (1 match @0x006AC880) over the engine's scan window against both acclient
    // copies (C:\Turbine and C:\Games\RynthCore\AcClient), 2026-09-30.
    private static readonly byte?[] PatEventDropItem = [ 0x83, 0xEC, 0x0C, 0x53, 0x56, 0x57, 0xE8, null, null, null, null, 0x89, 0x44, 0x24, 0x14, 0x6A, 0x00, 0x8D, 0x44, 0x24, 0x10, 0x50, 0x8D, 0x4C, 0x24, 0x18, 0xC7, 0x44, 0x24, 0x18, 0x2C, 0x2C, 0x80, 0x00, 0xC7, 0x44, 0x24, 0x14, 0x00, 0x00, 0x00, 0x00, 0xE8, null, null, null, null, 0x8B, 0xF0, 0x83, 0xC6, 0x08, 0x56, 0xE8, null, null, null, null, 0x83, 0xC4, 0x04, 0x56, 0x8D, 0x4C, 0x24, 0x10, 0x51, 0x8D, 0x4C, 0x24, 0x18, 0x89, 0x44, 0x24, 0x14, 0x8B, 0xF8, 0xE8, null, null, null, null, 0x8B, 0x54, 0x24, 0x0C, 0x8B, 0x4C, 0x24, 0x1C, 0xC7, 0x02, 0x1B ];
    // CM_Inventory::Event_NoLongerViewingContents @0x006ACBB0. Byte-identical to Event_DropItem
    // except the opcode immediate (0x0195, so the last two bytes are 0x95 0x01 where DropItem has
    // 0x1B). Verified unique (1 match @0x006ACBB0) over the engine's scan window against
    // C:\Games\RynthCore\AcClient\acclient.exe, 2026-10-04.
    private static readonly byte?[] PatEventNoLongerViewingContents = [ 0x83, 0xEC, 0x0C, 0x53, 0x56, 0x57, 0xE8, null, null, null, null, 0x89, 0x44, 0x24, 0x14, 0x6A, 0x00, 0x8D, 0x44, 0x24, 0x10, 0x50, 0x8D, 0x4C, 0x24, 0x18, 0xC7, 0x44, 0x24, 0x18, 0x2C, 0x2C, 0x80, 0x00, 0xC7, 0x44, 0x24, 0x14, 0x00, 0x00, 0x00, 0x00, 0xE8, null, null, null, null, 0x8B, 0xF0, 0x83, 0xC6, 0x08, 0x56, 0xE8, null, null, null, null, 0x83, 0xC4, 0x04, 0x56, 0x8D, 0x4C, 0x24, 0x10, 0x51, 0x8D, 0x4C, 0x24, 0x18, 0x89, 0x44, 0x24, 0x14, 0x8B, 0xF8, 0xE8, null, null, null, null, 0x8B, 0x54, 0x24, 0x0C, 0x8B, 0x4C, 0x24, 0x1C, 0xC7, 0x02, 0x95, 0x01 ];
    private static readonly byte?[] PatInqPlayerCoords =[ 0x83, 0xEC, 0x10, 0x53, 0x8B, 0x5C, 0x24, 0x1C, 0x55, 0x56 ];
    private static readonly byte?[] PatGetPlayerId = [ 0xA1, 0x58, 0xDA, 0x83, 0x00, 0x85, 0xC0, 0x74, 0x07 ];
    private static readonly byte?[] PatAddTextToScroll = [ 0x81, 0xEC, 0x48, 0x09, 0x00, 0x00, 0x8A, 0x84 ];
    private static readonly byte?[] PatUseWithTargetEvent = [ 0x83, 0xEC, 0x0C, 0x53, 0x56, 0x57, 0xE8, null, null, null, null, 0x89, 0x44, 0x24, 0x14, 0x6A, 0x00, 0x8D, 0x44, 0x24, 0x10, 0x50, 0x8D, 0x4C, 0x24, 0x18, 0xC7, 0x44, 0x24, 0x18, 0x2C, 0x2C, 0x80, 0x00, 0xC7, 0x44, 0x24, 0x14, 0x00, 0x00, 0x00, 0x00, 0xE8, null, null, null, null, 0x8B, 0xF0, 0x83, 0xC6, 0x0C, 0x56, 0xE8, null, null, null, null, 0x83, 0xC4, 0x04, 0x56, 0x8D, 0x4C, 0x24, 0x10, 0x51, 0x8D, 0x4C, 0x24, 0x18, 0x89, 0x44, 0x24, 0x14, 0x8B, 0xF8, 0xE8, null, null, null, null, 0x8B, 0x54, 0x24, 0x0C, 0x8B, 0x4C, 0x24, 0x1C, 0xC7, 0x02, 0x35 ];
    private static readonly byte?[] PatSendNoticeOpenSalvagePanel = [ 0xE8, null, null, null, null, 0x8B, 0x10, 0x68, 0x22 ];
    private static readonly byte?[] PatGmSalvageUIAddNewItem = [ 0x8B, 0x44, 0x24, 0x04, 0x56, 0x57, 0x50, 0x8B, 0xF9 ];
    private static readonly byte?[] PatGmSalvageUISalvage = [ 0x83, 0xEC, 0x14, 0x53, 0x56, 0x8B, 0xF1, 0x8B, 0x8E ];
    // CM_Inventory::Event_CreateTinkeringTool @0x006AC790. The Event_* template with the list argument:
    // `mov edi,[esp+20h]` (the list) right after the first call, `call [eax+0Ch]` (the list's Pack), and the
    // 0x027D opcode write (C7 01 7D 02 00 00) at the end. Verified unique (1 match @0x006AC790) via
    // pe_pattern.py against C:\Games\RynthCore\AcClient and C:\Turbine acclient.exe, 2026-10-12.
    private static readonly byte?[] PatEventCreateTinkeringTool = [ 0x83, 0xEC, 0x0C, 0x53, 0x56, 0x57, 0xE8, null, null, null, null, 0x8B, 0x7C, 0x24, 0x20, 0x6A, 0x00, 0x8D, 0x4C, 0x24, 0x10, 0x89, 0x44, 0x24, 0x18, 0x8B, 0x07, 0x51, 0x8B, 0xCF, 0xC7, 0x44, 0x24, 0x18, 0x2C, 0x2C, 0x80, 0x00, 0xC7, 0x44, 0x24, 0x14, 0x00, 0x00, 0x00, 0x00, 0xFF, 0x50, 0x0C, 0x6A, 0x00, 0x8D, 0x54, 0x24, 0x10, 0x52, 0x8D, 0x4C, 0x24, 0x18, 0x8B, 0xF0, 0xE8, null, null, null, null, 0x8D, 0x74, 0x06, 0x08, 0x56, 0xE8, null, null, null, null, 0x83, 0xC4, 0x04, 0x89, 0x44, 0x24, 0x0C, 0x8B, 0xD8, 0x56, 0x8D, 0x44, 0x24, 0x10, 0x50, 0x8D, 0x4C, 0x24, 0x18, 0xE8, null, null, null, null, 0x8B, 0x4C, 0x24, 0x0C, 0x8B, 0x54, 0x24, 0x1C, 0xC7, 0x01, 0x7D, 0x02 ];
    // Data xref for PackableList<ulong>'s vtable: gmSalvageUI::Salvage builds its local list with
    // `mov [esp+10h], vtable` then zeroes head/tail/count (`mov [esp+14h/18h/1Ch], ebx`) and calls
    // GetNumUIItems. Operand at +4. Unique (1 site @0x004CC448) in both acclient copies, 2026-10-12.
    private static readonly byte?[] PatXrefPackableListULongVtable = [ 0xC7, 0x44, 0x24, 0x10, null, null, null, null, 0x89, 0x5C, 0x24, 0x14, 0x89, 0x5C, 0x24, 0x18, 0x89, 0x5C, 0x24, 0x1C, 0xE8 ];
    // PackableList<ulong>::Pack (vtable slot 3) starts push esi; push edi; mov esi,ecx; call pack_size.
    private static readonly byte[] PackableListULongPackPrologue = [ 0x56, 0x57, 0x8B, 0xF1, 0xE8 ];

    private static T? Bind<T>(AcClientTextSection text, string name, byte?[] pattern, int fallbackVa) where T : Delegate
    {
        HookResolver.ResolveResult r = HookResolver.Resolve(text, name, pattern, fallbackVa);
        return r.Success ? Marshal.GetDelegateForFunctionPointer<T>(r.Address) : null;
    }

    // Phase B: data globals resolved by code-xref (operand at offset 2); the VA consts above
    // stay as fallbacks. The 7 read/write globals resolve in Probe (cached int); the WidePString
    // null-buffer resolves at struct static-init via ResolveDataVa.
    private static readonly byte?[] PatXrefSelectedId = [ 0xC3, 0xA1, null, null, null, null, 0x89, 0x87 ];
    private static readonly byte?[] PatXrefPrevSelectedId = [ 0x89, 0x3D, null, null, null, null, 0x74, 0x06 ];
    private static readonly byte?[] PatXrefUiSystem = [ 0x8B, 0x0D, null, null, null, null, 0x53, 0x6A ];
    private static readonly byte?[] PatXrefSplitAmount = [ 0x00, 0xA1, null, null, null, null, 0x57, 0x50 ];
    private static readonly byte?[] PatXrefTotalStack = [ 0x10, 0xA1, null, null, null, null, 0x3B, 0xD8 ];
    private static readonly byte?[] PatXrefPlayerSystem = [ 0xC7, 0x05, null, null, null, null, 0x00, 0x00, 0x00, 0x00, 0x83, 0xC6 ];
    private static readonly byte?[] PatXrefCommunicationSystem = [ 0x08, 0xA1, null, null, null, null, 0x56, 0xBE ];
    private static readonly byte?[] PatXrefWideNullBuffer = [ 0x3B, 0x05, null, null, null, null, 0x74, 0xE7 ];
    private static int _selectedIdAddr = SelectedIdVa;
    private static int _prevSelectedIdAddr = PreviousSelectedIdVa;
    private static int _uiSystemAddr = UiSystemVa;
    private static int _splitAmountAddr = SplitAmountVa;
    private static int _totalStackAddr = TotalStackVa;
    private static int _playerSystemAddr = PlayerSystemVa;
    private static int _commSystemAddr = CommunicationSystemVa;

    private static IntPtr ResolveDataVa(string name, byte?[] pattern, int operandOffset, int fallbackVa)
    {
        if (!AcClientModule.TryReadTextSection(out AcClientTextSection text))
            return new IntPtr(fallbackVa);
        return HookResolver.ResolveData(text, name, pattern, operandOffset, fallbackVa).Address;
    }

    public static bool Probe()
    {
        try
        {
            if (!AcClientModule.TryReadTextSection(out AcClientTextSection text))
            {
                _statusMessage = "acclient .text not readable for pattern resolve.";
                RynthLog.Compat($"Compat: helper hooks failed - {_statusMessage}");
                return false;
            }

            _setSelectedObject = Bind<SetSelectedObjectDelegate>(text, "ClientHelper.SetSelectedObject", PatSetSelectedObject, SetSelectedObjectVa);
            _useObject = Bind<UseObjectDelegate>(text, "ClientHelper.UseObject", PatUseObject, UseObjectVa);
            _getAcPlugin = Bind<GetAcPluginDelegate>(text, "ClientHelper.GetAcPlugin", PatGetAcPlugin, GetAcPluginVa);
            _useObjectOn = Bind<UseObjectOnDelegate>(text, "ClientHelper.UseObjectOn", PatUseObjectOn, UseObjectOnVa);
            _useEquippedItem = Bind<UseEquippedItemDelegate>(text, "ClientHelper.UseEquippedItem", PatUseEquippedItem, UseEquippedItemVa);
            _moveItemExternal = Bind<MoveItemExternalDelegate>(text, "ClientHelper.MoveItemExternal", PatMoveItemExternal, MoveItemExternalVa);
            _moveItemInternal = Bind<MoveItemInternalDelegate>(text, "ClientHelper.MoveItemInternal", PatMoveItemInternal, MoveItemInternalVa);
            _eventStackableMerge = Bind<EventStackableMergeDelegate>(text, "ClientHelper.Event_StackableMerge", PatEventStackableMerge, EventStackableMergeVa);
            _eventGiveObjectRequest = Bind<EventGiveObjectRequestDelegate>(text, "ClientHelper.Event_GiveObjectRequest", PatEventGiveObjectRequest, EventGiveObjectRequestVa);
            _eventPutItemInContainer = Bind<EventPutItemInContainerDelegate>(text, "ClientHelper.Event_PutItemInContainer", PatEventPutItemInContainer, EventPutItemInContainerVa);
            _eventGetAndWieldItem = Bind<EventGetAndWieldItemDelegate>(text, "ClientHelper.Event_GetAndWieldItem", PatEventGetAndWieldItem, EventGetAndWieldItemVa);
            _eventDropItem = Bind<EventDropItemDelegate>(text, "ClientHelper.Event_DropItem", PatEventDropItem, EventDropItemVa);
            _eventNoLongerViewingContents = Bind<EventNoLongerViewingContentsDelegate>(text, "ClientHelper.Event_NoLongerViewingContents", PatEventNoLongerViewingContents, EventNoLongerViewingContentsVa);
            _inqPlayerCoords = Bind<InqPlayerCoordsDelegate>(text, "ClientHelper.InqPlayerCoords", PatInqPlayerCoords, InqPlayerCoordsVa);
            _getPlayerId = Bind<GetPlayerIdDelegate>(text, "ClientHelper.GetPlayerId", PatGetPlayerId, GetPlayerIdVa);
            _addTextToScroll = Bind<AddTextToScrollDelegate>(text, "ClientHelper.AddTextToScroll", PatAddTextToScroll, AddTextToScrollVa);
            _useWithTargetEvent = Bind<UseWithTargetEventDelegate>(text, "ClientHelper.UseWithTargetEvent", PatUseWithTargetEvent, UseWithTargetEventVa);
            _sendNoticeOpenSalvagePanel = Bind<SendNoticeOpenSalvagePanelDelegate>(text, "ClientHelper.SendNotice_OpenSalvagePanel", PatSendNoticeOpenSalvagePanel, SendNoticeOpenSalvagePanelVa);
            _gmSalvageUIAddNewItem = Bind<GmSalvageUIAddNewItemDelegate>(text, "ClientHelper.gmSalvageUI_AddNewItem", PatGmSalvageUIAddNewItem, GmSalvageUIAddNewItemVa);
            _gmSalvageUISalvage = Bind<GmSalvageUISalvageDelegate>(text, "ClientHelper.gmSalvageUI_Salvage", PatGmSalvageUISalvage, GmSalvageUISalvageVa);
            BindSalvageDirect(text);
            _selectedIdAddr = HookResolver.ResolveData(text, "ClientHelper.s_selected_id", PatXrefSelectedId, 2, SelectedIdVa).Address.ToInt32();
            _prevSelectedIdAddr = HookResolver.ResolveData(text, "ClientHelper.s_previous_selected_id", PatXrefPrevSelectedId, 2, PreviousSelectedIdVa).Address.ToInt32();
            _uiSystemAddr = HookResolver.ResolveData(text, "ClientHelper.ClientUISystem", PatXrefUiSystem, 2, UiSystemVa).Address.ToInt32();
            _splitAmountAddr = HookResolver.ResolveData(text, "ClientHelper.split_amount", PatXrefSplitAmount, 2, SplitAmountVa).Address.ToInt32();
            _totalStackAddr = HookResolver.ResolveData(text, "ClientHelper.total_stack", PatXrefTotalStack, 2, TotalStackVa).Address.ToInt32();
            _playerSystemAddr = HookResolver.ResolveData(text, "ClientHelper.CPlayerSystem", PatXrefPlayerSystem, 2, PlayerSystemVa).Address.ToInt32();
            _commSystemAddr = HookResolver.ResolveData(text, "ClientHelper.CommunicationSystem", PatXrefCommunicationSystem, 2, CommunicationSystemVa).Address.ToInt32();
            // v72 player-to-player trade actions (CM_Trade / ClientTradeSystem).
            try { PlayerTrade.Probe(text); }
            catch (Exception ex) { RynthLog.Compat($"Compat: player trade binding failed - {ex.Message}"); }
            // Skills panel raises (CM_Train senders).
            try { PlayerTraining.Probe(text); }
            catch (Exception ex) { RynthLog.Compat($"Compat: raise senders binding failed - {ex.Message}"); }
            // v85 confirmation answers (CM_Character::Event_ConfirmationResponse).
            try { ConfirmationResponder.Probe(text); }
            catch (Exception ex) { RynthLog.Compat($"Compat: confirmation response binding failed - {ex.Message}"); }
            _initialized = true;
            _statusMessage = "Ready.";
            RynthLog.Verbose("Compat: helper hooks ready - validated select/state/chat helpers plus mapped interaction and inventory helpers.");
            return true;
        }
        catch (Exception ex)
        {
            Reset();
            _statusMessage = ex.Message;
            RynthLog.Compat($"Compat: helper hooks failed - {ex.Message}");
            return false;
        }
    }

    public static bool SelectItem(uint objectId)
    {
        return SetSelectedObjectId(objectId);
    }

    public static bool SetSelectedObjectId(uint objectId)
    {
        // Marshal onto AC's main thread. AC's SetSelectedObject (0x0058D110)
        // rewrites the selection/targeting UIElement subtree; off the pump thread
        // it raced AC's own main-thread UI walk and corrupted a UIElement ->
        // the deterministic write-AV at acclient+0x60D1D (UIElement smart-ptr
        // refcount writeback into read-only .text; native-crash.log captured it
        // on TWO threads at once during corpse-loot target selection, 2026-06-13).
        // This was the last AC-mutating helper still calling AC directly off-thread
        // (every UseObject/MoveItem/cast sibling already marshals). Drain re-invokes
        // this on the main thread, where the gate is satisfied and it runs directly.
        // SelectItem() funnels through here, so this one gate covers both.
        if (AcMainThreadQueue.MustQueue(teardown: false))
            return AcMainThreadQueue.EnqueueSetSelectedObject(objectId);

        if (_setSelectedObject == null)
            return false;

        try
        {
            _setSelectedObject(objectId, 1);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Click-to-select from an ImGui overlay (a Radar dot, a monster nameplate):
    /// selects <paramref name="objectId"/> the way a click on it in the 3D world
    /// does. The faces park the id with AcMainThreadQueue.EnqueueOverlaySelect;
    /// its drain lands here on AC's main thread, where the checks run right
    /// before the select: the client is in the world (logged in, no logout or
    /// DB-cache teardown under way, not portaling) and the object still exists
    /// (the client still resolves its weenie). Off-thread it marshals like
    /// every other mutator. False when nothing was selected.
    /// </summary>
    public static bool SelectFromOverlay(uint objectId)
    {
        if (AcMainThreadQueue.MustQueue(teardown: false))
            return AcMainThreadQueue.EnqueueOverlaySelect(objectId);

        if (objectId == 0 || _setSelectedObject == null)
            return false;
        if (!LoginLifecycleHooks.HasObservedLoginComplete || LogoutLifecycleHooks.HasObservedLogout
            || DbCacheTeardownHooks.TeardownActive || TeleportStateHooks.IsPortaling || GetPlayerId() == 0)
            return false;
        if (!ClientObjectHooks.TryGetWeenieObjectPtr(objectId, out _))
            return false;   // despawned / died / left range since the click
        if (GetSelectedItemId() == objectId)
            return true;    // already the selection: nothing to redo

        AcActionTrace.Record("OverlaySelect", objectId);
        return SetSelectedObjectId(objectId);
    }

    public static uint GetSelectedItemId()
    {
        return ReadUInt32(_selectedIdAddr);
    }

    public static uint GetPreviousSelectedItemId()
    {
        return ReadUInt32(_prevSelectedIdAddr);
    }

    public static bool UseObject(uint objectId)
    {
        // P1 marshalling: looting's open-corpse / pick-up use-action runs on AC's
        // main thread (via the OnEndScene drain), not off the pump thread. Off-thread
        // UseObject mutated AC's animation sequence (CSequence) and corrupted it ->
        // the CSequence::update_internal AV captured after looting was enabled.
        if (AcMainThreadQueue.MustQueue(teardown: false))
            return AcMainThreadQueue.EnqueueUseObject(objectId);

        if (_useObject == null || !IsValidObjectId(objectId))
            return false;

        try
        {
            IntPtr uiSystem = ReadPointer(_uiSystemAddr);
            if (uiSystem == IntPtr.Zero)
                return false;

            // Reconcile the busy this open/use leaks (same source-fix as casts):
            // UseObject bumps m_cBusy but no retail completion handler runs to
            // decrement it, so corpse-open grinds left the plugin force-clearing
            // every ~2s. Snapshot the delta; BusyCountHooks decrements it once
            // the use-reach gesture completes. Both run on AC's main thread here.
            int busyBefore = BusyCountHooks.CaptureRealBusyForCast();
            _useObject(uiSystem, objectId);
            BusyCountHooks.NoteDirectCastIssued(busyBefore);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool UseObjectOn(uint sourceObjectId, uint targetObjectId)
    {
        // Marshal onto AC's main thread (off-thread use-on-target mutates AC's
        // object/animation graph and corrupts it — see UseObject). Drain re-invokes
        // this on the main thread, where the gate is satisfied and it runs directly.
        if (AcMainThreadQueue.MustQueue(teardown: false))
            return AcMainThreadQueue.EnqueueUseObjectOn(sourceObjectId, targetObjectId);

        if (_useWithTargetEvent == null || !IsValidObjectId(sourceObjectId) || !IsValidObjectId(targetObjectId))
            return false;

        try
        {
            // Reconcile leaked busy (same source-fix as casts / UseObject) — a
            // use-on-target (e.g. PetManager essence refill) bumps m_cBusy with
            // no retail completion decrement.
            int busyBefore = BusyCountHooks.CaptureRealBusyForCast();
            _useWithTargetEvent(sourceObjectId, targetObjectId);
            BusyCountHooks.NoteDirectCastIssued(busyBefore);
            LogInteraction($"UseObjectOn invoked via Event_UseWithTargetEvent source=0x{sourceObjectId:X8} target=0x{targetObjectId:X8}");
            return true;
        }
        catch (Exception ex)
        {
            LogInteraction($"UseObjectOn exception for 0x{sourceObjectId:X8} -> 0x{targetObjectId:X8}: {ex.Message}");
            return false;
        }
    }

    public static bool UseEquippedItem(uint sourceObjectId, uint targetObjectId)
    {
        if (AcMainThreadQueue.MustQueue(teardown: false))
            return AcMainThreadQueue.EnqueueUseEquippedItem(sourceObjectId, targetObjectId);

        if (_useEquippedItem == null || !IsValidObjectId(sourceObjectId) || !IsValidObjectId(targetObjectId))
            return false;

        try
        {
            IntPtr acPlugin = GetAcPlugin();
            if (acPlugin == IntPtr.Zero)
                return false;

            _useEquippedItem(acPlugin, sourceObjectId, targetObjectId);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool MoveItemExternal(uint objectId, uint targetContainerId, int amount)
    {
        // Marshal onto AC's main thread — off-thread item moves race AC's per-tick
        // object/container bookkeeping (the looting null-deref AV class).
        if (AcMainThreadQueue.MustQueue(teardown: false))
            return AcMainThreadQueue.EnqueueMoveItemExternal(objectId, targetContainerId, amount);

        if (_moveItemExternal == null || !IsValidObjectId(objectId) || !IsValidContainerId(targetContainerId) || amount < 0)
            return false;

        // P0 (2026-06-24): if the target is one of THIS player's OWN containers and it
        // is full, the native PutItemInContainer slot-table walk (FUN_00588f70) AVs
        // instead of failing. Fail CLOSED here. Non-owned targets (trade/give to
        // another player, NPCs, world containers) are NOT gated — they pass through.
        // Runs on the main thread (marshalled above) so the ownership/capacity/count
        // reads are authoritative. See AutoCram crash deep-dive.
        if (IsFullOwnedContainer(targetContainerId))
            return false;

        try
        {
            // Stub ignores `this` — pass IntPtr.Zero to avoid depending on the
            // uninitialized Decal plugin pointer at DAT_00871054.
            _moveItemExternal(IntPtr.Zero, objectId, targetContainerId, amount);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool MoveItemInternal(uint objectId, uint targetContainerId, int slot, int amount)
    {
        if (AcMainThreadQueue.MustQueue(teardown: false))
            return AcMainThreadQueue.EnqueueMoveItemInternal(objectId, targetContainerId, slot, amount);

        if (_moveItemInternal == null) return false;
        if (!IsValidObjectId(objectId)) return false;
        if (!IsValidContainerId(targetContainerId)) return false;
        if (slot < 0) return false;
        if (amount <= 0) return false;

        // P0 (2026-06-24): fail CLOSED if the target is one of THIS player's OWN
        // containers and it is full — the native slot-table walk (FUN_00588f70) AVs
        // on a full owned pack rather than failing. Non-owned targets pass through.
        if (IsFullOwnedContainer(targetContainerId))
            return false;

        try
        {
            // P0 (2026-06-24): defensively restore the whole-move split globals to
            // their default 1/1 state before the send, so a leaked split state from a
            // prior SplitStackInternal can never force the 0x55 specific-slot path
            // (which honors `slot` and can target a populated slot). 1/1 == opcode 0x19
            // (PutItemInContainer, first-empty-slot), which is what MoveItemInternal
            // callers expect.
            Marshal.WriteInt32(new IntPtr(_splitAmountAddr), 1);
            Marshal.WriteInt32(new IntPtr(_totalStackAddr), 1);

            // Stub ignores `this` — pass IntPtr.Zero to avoid depending on the
            // uninitialized Decal plugin pointer at DAT_00871054.
            //
            // Note: the stub → FUN_00588f70 picks between opcode 0x19
            // (PutItemInContainer — whole move, first empty slot) and opcode
            // 0x55 (StackableSplitToContainer — targets a specific slot) based
            // on DAT_0081d7ec vs DAT_0081d7f0. Default state (1/1) takes the
            // whole-move path, which is what AutoCram wants. For stack merges
            // where the slot must be honored, callers should use
            // SplitStackInternal instead.
            _moveItemInternal(IntPtr.Zero, objectId, targetContainerId, slot, amount);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Move a stack of items onto a specific slot in the target container, merging
    /// with any existing same-type stack at that slot. Used by AutoStack.
    ///
    /// Implementation note: writes split_amount/total_stack globals (normally set
    /// by UI drag-drop code) so FUN_00588f70 takes the opcode 0x55 path
    /// (StackableSplitToContainer), which honors the slot parameter. The default
    /// MoveItemInternal path sends opcode 0x19 (PutItemInContainer) which ignores
    /// slot and lands the item in the first empty slot of the container.
    /// </summary>
    public static bool SplitStackInternal(uint objectId, uint targetContainerId, int slot, int amount)
    {
        if (AcMainThreadQueue.MustQueue(teardown: false))
            return AcMainThreadQueue.EnqueueSplitStackInternal(objectId, targetContainerId, slot, amount);

        if (_moveItemInternal == null) return false;
        if (!IsValidObjectId(objectId)) return false;
        if (!IsValidContainerId(targetContainerId)) return false;
        if (slot < 0) return false;
        if (amount <= 0) return false;

        try
        {
            // Force split_amount < total_stack so FUN_00588f70 takes the split
            // path. The actual values aren't sent on the wire — FUN_00588f70
            // only uses them to pick which sub-function to dispatch to.
            Marshal.WriteInt32(new IntPtr(_splitAmountAddr), amount);
            Marshal.WriteInt32(new IntPtr(_totalStackAddr), amount + 1);

            _moveItemInternal(IntPtr.Zero, objectId, targetContainerId, slot, amount);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            // P0 (2026-06-24): restore the split globals to their default whole-move
            // state (1/1). They were previously written above and never restored,
            // leaving a leaked state that could push a later plain MoveItemInternal
            // onto the 0x55 specific-slot path. DORMANT today (AutoStack uses
            // MergeStackInternal) but fixed defensively. Writes are individually
            // try/caught so a failed restore can't escape the finally.
            try { Marshal.WriteInt32(new IntPtr(_splitAmountAddr), 1); } catch { }
            try { Marshal.WriteInt32(new IntPtr(_totalStackAddr), 1); } catch { }
        }
    }

    /// <summary>
    /// Merge two stacks of the same item type by calling the canonical
    /// public API CM_Inventory::Event_StackableMerge (cdecl) at 0x006ACDD0.
    /// Args: (mergeFromId, mergeToId, amount). This is the same entry point
    /// the legacy drag-drop UI ultimately calls — it sends opcode 0x1A and
    /// wires up the client-side action queue properly.
    ///
    /// Supports split-merge: reads source.STACK_SIZE, target.STACK_SIZE, and
    /// MAX_STACK_SIZE from the PublicWeenieDesc fast-path and passes
    /// amount = min(sourceCount, max - targetCount). This lets a small chunk
    /// peel off a large source stack to top off a near-full target, instead
    /// of being limited to full-merges that fit entirely under the cap.
    /// </summary>
    public static bool MergeStackInternal(uint sourceObjectId, uint targetObjectId)
    {
        // Every exit records its outcome in MergeStackResults so the plugin can poll
        // GetMergeStackResult instead of guessing from a later inventory snapshot.
        if (AcMainThreadQueue.MustQueue(teardown: false))
        {
            bool queued = AcMainThreadQueue.EnqueueMergeStackInternal(sourceObjectId, targetObjectId);
            MergeStackResults.Record(sourceObjectId, targetObjectId,
                queued ? MergeStackResults.Queued : MergeStackResults.QueueFull);
            return queued;
        }

        if (_eventStackableMerge == null || !IsValidObjectId(sourceObjectId) || !IsValidObjectId(targetObjectId))
        {
            MergeStackResults.Record(sourceObjectId, targetObjectId, MergeStackResults.Failed);
            return false;
        }

        // Read counts via the PWD fast path (stypes 11/12 are served from
        // PublicWeenieDesc directly — no broken InqInt for inventory items).
        ClientObjectHooks.TryGetObjectIntProperty(sourceObjectId, 12 /* STACK_SIZE */, out int sourceCount);
        if (sourceCount <= 0) sourceCount = 1;

        ClientObjectHooks.TryGetObjectIntProperty(targetObjectId, 12 /* STACK_SIZE */, out int targetCount);
        if (targetCount < 0) targetCount = 0;

        ClientObjectHooks.TryGetObjectIntProperty(sourceObjectId, 11 /* MAX_STACK_SIZE */, out int maxStack);

        int amount;
        if (maxStack > 0)
        {
            int room = maxStack - targetCount;
            if (room <= 0)
            {
                RynthLog.Verbose($"Compat: Event_StackableMerge skipped - target 0x{targetObjectId:X8}({targetCount}) is full (max={maxStack})");
                MergeStackResults.Record(sourceObjectId, targetObjectId, MergeStackResults.TargetFull);
                return false;
            }
            amount = Math.Min(sourceCount, room);
        }
        else
        {
            // Couldn't read max — fall back to full source merge.
            amount = sourceCount;
        }

        if (amount <= 0)
        {
            MergeStackResults.Record(sourceObjectId, targetObjectId, MergeStackResults.Failed);
            return false;
        }

        try
        {
            byte rv = _eventStackableMerge(sourceObjectId, targetObjectId, amount);
            RynthLog.Verbose($"Compat: Event_StackableMerge from=0x{sourceObjectId:X8}({sourceCount}) to=0x{targetObjectId:X8}({targetCount}) amount={amount}/{maxStack} rv={rv}");
            MergeStackResults.Record(sourceObjectId, targetObjectId,
                rv != 0 ? MergeStackResults.Sent : MergeStackResults.Failed, rv != 0 ? amount : 0);
            return rv != 0;
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"Compat: Event_StackableMerge threw - {ex.Message}");
            MergeStackResults.Record(sourceObjectId, targetObjectId, MergeStackResults.Failed);
            return false;
        }
    }

    /// <summary>
    /// Put an item into a container at a given slot (CM_Inventory::Event_PutItemInContainer, the
    /// 0x19 game action): the drop on a cell of the pack grid, or a reorder inside one pack. The
    /// server decides (full pack, wrong kind of item) and answers as for any move; this is the
    /// packet only, so unlike <see cref="MoveItemInternal"/> it never walks the client's slot
    /// table. <paramref name="slot"/> is the index in the container (the server clamps it).
    /// </summary>
    public static bool PutItemInContainerAt(uint objectId, uint containerId, int slot)
    {
        if (AcMainThreadQueue.MustQueue(teardown: false))
            return AcMainThreadQueue.EnqueuePutItemInContainer(objectId, containerId, slot);

        if (_eventPutItemInContainer == null) return false;
        if (!IsValidObjectId(objectId)) return false;
        if (!IsValidContainerId(containerId)) return false;
        if (slot < 0) return false;

        try
        {
            byte rv = _eventPutItemInContainer(objectId, containerId, slot);
            RynthLog.Verbose($"Compat: Event_PutItemInContainer item=0x{objectId:X8} -> container=0x{containerId:X8} slot={slot} rv={rv}");
            return rv != 0;
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"Compat: Event_PutItemInContainer threw - {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Give an item to an NPC (or another player) via the canonical public API
    /// CM_Inventory::Event_GiveObjectRequest (cdecl) at 0x006ACA60 — the F7B1 give
    /// GameAction, the same path the drag-onto-NPC UI uses. This is the CORRECT
    /// give-to-NPC primitive; MoveItemExternal is move-to-container and does not
    /// give. amount=0 gives the whole object; positive = partial stack. The wire needs a
    /// real count (ACE answers "Give amount not valid!" to 0), so 0 is turned into the
    /// item's stack size here (1 for a single item).
    /// </summary>
    public static bool GiveObjectTo(uint objectId, uint targetId, int amount = 0)
    {
        // Marshal onto AC's main thread — off-thread F7B1 inventory sends race AC's
        // per-tick object/container bookkeeping (the looting null-deref AV class),
        // exactly like MoveItemExternal / MergeStackInternal.
        if (AcMainThreadQueue.MustQueue(teardown: false))
            return AcMainThreadQueue.EnqueueGiveObjectTo(objectId, targetId, amount);

        if (_eventGiveObjectRequest == null) return false;
        if (!IsValidObjectId(objectId)) return false;
        if (!IsValidObjectId(targetId)) return false;
        if (amount < 0) return false;

        try
        {
            if (amount == 0)
                amount = WholeStackAmount(objectId);
            byte rv = _eventGiveObjectRequest(targetId, objectId, amount);
            RynthLog.Verbose($"Compat: Event_GiveObjectRequest item=0x{objectId:X8} -> target=0x{targetId:X8} amount={amount} rv={rv}");
            return rv != 0;
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"Compat: Event_GiveObjectRequest threw - {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The count that gives a whole object: its stack size, or 1 for a single item (or
    /// when the item can't be read). Main thread (TryReadItemFields is false off it).
    /// </summary>
    internal static int WholeStackAmount(uint objectId) =>
        ClientObjectHooks.TryReadItemFields(objectId, out ClientObjectHooks.ItemPwdFields f) && f.StackSize > 1
            ? (int)Math.Min(f.StackSize, int.MaxValue)
            : 1;

    /// <summary>
    /// Wield an item into the given equip slot(s) through CM_Inventory::Event_GetAndWieldItem
    /// (cdecl) at 0x006AC950 — the 0x001A GetAndWieldItem game action, the packet the
    /// paperdoll drag sends (ACCWeenieObject::UIAttemptWield calls it). equipMask is an
    /// EquipMask / INVENTORY_LOC value (e.g. 0x00100000 MeleeWeapon, 0x00200000 Shield,
    /// 0x00400000 MissileWeapon, 0x01000000 Held, 0x02000000 TwoHanded; rings/bracelets
    /// have a left and a right bit). The server checks the slot against the item's
    /// ValidLocations and answers with the wield, or InventoryServerSaveFailed (0x00A0) plus
    /// a WeenieError. Unlike UIAttemptWield this sends the packet only: no client busy count,
    /// no pending-wield UI state — the same raw-send contract as GiveObjectTo.
    /// </summary>
    public static bool WieldItem(uint objectId, uint equipMask)
    {
        // Inventory sends run on AC's main thread, like MoveItemExternal / GiveObjectTo.
        if (AcMainThreadQueue.MustQueue(teardown: false))
            return _eventGetAndWieldItem != null && IsValidObjectId(objectId) && equipMask != 0
                && AcMainThreadQueue.EnqueueWieldItem(objectId, equipMask);

        if (_eventGetAndWieldItem == null) return false;
        if (!IsValidObjectId(objectId) || equipMask == 0) return false;

        try
        {
            byte rv = _eventGetAndWieldItem(objectId, equipMask);
            RynthLog.Verbose($"Compat: Event_GetAndWieldItem item=0x{objectId:X8} mask=0x{equipMask:X8} rv={rv}");
            return rv != 0;
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"Compat: Event_GetAndWieldItem threw - {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Drop an item on the ground through CM_Inventory::Event_DropItem (cdecl) at 0x006AC880,
    /// the 0x001B DropItem game action that ACCWeenieObject::UIAttemptPutIn3D sends. The
    /// server walks nothing (it drops at the player's feet) and answers with the item's new
    /// place, or InventoryServerSaveFailed (0x00A0). Packet only, like WieldItem: no client
    /// busy count, no pending-request UI state. Used by the Inventory panel.
    /// </summary>
    public static bool DropItem(uint objectId)
    {
        // Inventory sends run on AC's main thread, like WieldItem / GiveObjectTo.
        if (AcMainThreadQueue.MustQueue(teardown: false))
            return _eventDropItem != null && IsValidObjectId(objectId)
                && AcMainThreadQueue.EnqueueDropItem(objectId);

        if (_eventDropItem == null) return false;
        if (!IsValidObjectId(objectId)) return false;

        try
        {
            byte rv = _eventDropItem(objectId);
            RynthLog.Verbose($"Compat: Event_DropItem item=0x{objectId:X8} rv={rv}");
            return rv != 0;
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"Compat: Event_DropItem threw - {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Close an external container (a corpse, a chest) the way the client does when its window
    /// is closed: CM_Inventory::Event_NoLongerViewingContents (cdecl) at 0x006ACBB0, the 0x0195
    /// NoLongerViewingContents game action. Packet only, like DropItem: no busy count (m_cBusy),
    /// no pending-request slot (it is not an inventory request; RecordRequest is not called).
    /// The server closes the container if this player is viewing it and answers with
    /// CloseGroundContainer (0x0052), whose client handler closes the window and raises
    /// StopViewingObjectContents to the plugins. A container this player isn't viewing is
    /// ignored by the server (no answer). Runs on AC's main thread, in the action ring, so a
    /// close queued just before the next UseObject goes out first, in the same frame.
    /// </summary>
    public static bool CloseContainer(uint containerId)
    {
        // An inventory game-action send, like DropItem / WieldItem: main thread only.
        if (AcMainThreadQueue.MustQueue(teardown: true))
            return _eventNoLongerViewingContents != null && IsValidObjectId(containerId)
                && AcMainThreadQueue.EnqueueCloseContainer(containerId);

        if (_eventNoLongerViewingContents == null) return false;
        if (!IsValidObjectId(containerId)) return false;

        try
        {
            byte rv = _eventNoLongerViewingContents(containerId);
            RynthLog.Verbose($"Compat: Event_NoLongerViewingContents container=0x{containerId:X8} rv={rv}");
            return rv != 0;
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"Compat: Event_NoLongerViewingContents threw - {ex.Message}");
            return false;
        }
    }

    public static bool TryGetCurCoords(out double northSouth, out double eastWest)
    {
        northSouth = 0;
        eastWest = 0;

        if (_inqPlayerCoords == null)
            return false;

        // Off AC's main thread: no coords. The only caller (PluginManager.GetCurCoords,
        // on the plugin pump) reaches this as the fallback when the raw SmartBox pose
        // read fails, i.e. during login/portal/logout while AC's player state is
        // changing. Calling CPlayerSystem::InqPlayerCoords there runs AC code
        // off-thread and try/catch cannot stop a native AV. Plugins already handle
        // "no coords", and InqPlayerCoords only returns cached data anyway.
        if (!MainThreadGuard.IsOnMainThread())
            return false;

        try
        {
            IntPtr playerSystem = ReadPointer(_playerSystemAddr);
            if (playerSystem == IntPtr.Zero)
                return false;

            // The live helper returns EW first and NS second.
            // Normalize it here so plugins see the same NS/EW basis that Decal's Coordinates() exposed.
            bool success = _inqPlayerCoords(playerSystem, out double first, out double second) != 0;
            if (!success)
                return false;

            northSouth = second;
            eastWest = first;
            return true;
        }
        catch
        {
            northSouth = 0;
            eastWest = 0;
            return false;
        }
    }

    // Last player id read on AC's main thread. The native leaf (0x0048E5F0) reads
    // the SmartBox global and then a field of it; off-thread that dereferences an
    // AC object the main thread can free during logout/teardown, and try/catch
    // cannot stop a native AV. Off-thread callers (host GetPlayerIdFn on the pump,
    // snapshot owner checks, HUDs) get this value instead: at most one frame old,
    // refreshed every EndScene by EngineFrameController, 0 = no player / not ready.
    private static int _cachedPlayerId;

    /// <summary>The player id last read on the main thread (0 = none yet); no native call.</summary>
    public static uint CachedPlayerId => unchecked((uint)Volatile.Read(ref _cachedPlayerId));

    public static uint GetPlayerId()
    {
        if (_getPlayerId == null)
            return 0;

        // Main-thread callers (PrefetchPlayerStats, IsFullOwnedContainer, the EndScene
        // prefetches) keep the live native read exactly as before, and publish it.
        if (!MainThreadGuard.IsOnMainThread())
            return unchecked((uint)Volatile.Read(ref _cachedPlayerId));

        try
        {
            uint id = _getPlayerId();
            Volatile.Write(ref _cachedPlayerId, unchecked((int)id));
            return id;
        }
        catch
        {
            return 0;
        }
    }

    public static uint GetGroundContainerId()
    {
        return unchecked((uint)Volatile.Read(ref _currentGroundContainerId));
    }

    public static void NotifyViewObjectContents(uint objectId)
    {
        if (!IsValidObjectId(objectId))
            return;

        Interlocked.Exchange(ref _currentGroundContainerId, unchecked((int)objectId));
        AutoIdService.OnContainerOpened(objectId);
    }

    public static void NotifyStopViewingObjectContents(uint objectId)
    {
        if (!IsValidObjectId(objectId))
            return;

        int current = Volatile.Read(ref _currentGroundContainerId);
        if (unchecked((uint)current) == objectId)
            Interlocked.Exchange(ref _currentGroundContainerId, 0);
    }

    private static bool IsValidObjectId(uint objectId)
    {
        return objectId != 0;
    }

    private static bool IsValidContainerId(uint containerId)
    {
        return containerId != 0;
    }

    // P0 (2026-06-24): shared owned-container full-check used by MoveItemInternal AND
    // MoveItemExternal. Returns true ONLY when `target` is a container owned by the
    // local player (the main pack, or a sub-pack/foci that roots at the player) AND it
    // is full / its capacity-or-count can't be resolved (fail CLOSED).
    //
    // For ANY non-owned target — another player (trade/give), an NPC, a corpse, a
    // world/landscape container, or an un-resolvable object — this returns FALSE so the
    // caller passes the move through UNCHANGED. Trade/give is therefore never blocked.
    // The crash this guards (native PutItemInContainer slot-table walk FUN_00588f70
    // AVing on a full pack) only ever happens on the player's OWN pack.
    //
    // MUST be called only after the caller has marshalled onto AC's main thread (both
    // MoveItemInternal/External early-return-enqueue off-thread), so the PWD ownership
    // read, ITEMS_CAPACITY read, and GetNumContainedItems walk are all authoritative
    // and the off-thread read-AV class does not apply.
    /// <summary>
    /// The full-pack guard MoveItemInternal / MoveItemExternal apply, for callers that send
    /// through another path (the Inventory panel's split). Main thread.
    /// </summary>
    internal static bool IsOwnedContainerFull(uint target) => IsFullOwnedContainer(target);

    private static bool IsFullOwnedContainer(uint target)
    {
        uint player = GetPlayerId();
        if (player == 0)
            return false; // can't resolve self -> treat as NOT owned -> pass through (never block trade)

        bool isMainPack = target == player;
        bool owned = isMainPack;
        if (!owned)
        {
            // One-hop ownership: AC inventory is two levels deep — the player holds
            // loose items and sub-packs directly, so an owned sub-pack/foci has
            // _containerID == player (or _wielderID == player for a wielded
            // container). Read straight from the target's PublicWeenieDesc on the main
            // thread (page-probed + try/caught: returns false, never AVs).
            if (!ClientObjectHooks.TryGetObjectOwnershipInfo(target, out uint containerID, out uint wielderID, out _))
                return false; // can't resolve target ownership -> NOT provably owned -> pass through
            owned = containerID == player || wielderID == player;
        }

        if (!owned)
            return false; // non-owned target (trade/give/NPC/world) -> NEVER gate

        // Owned target: compute free = capacity - count, all on the main thread.
        if (!ClientObjectHooks.TryGetObjectIntProperty(target, 6 /* ITEMS_CAPACITY */, out int capacity))
            capacity = 0;
        if (isMainPack && capacity <= 0)
            capacity = 102; // main-pack PWD capacity may not reflect the 102 item-slot
                            // cap; mirror the proven plugin constant so a cold/unresolved
                            // read does NOT fail-close every main-pack move.
        if (capacity <= 0)
            return true; // sub-pack capacity unresolved -> fail CLOSED (don't risk the walk)

        int count = ClientObjectHooks.GetNumContainedItems(target); // AC's own walk; -1 if unavailable
        if (count < 0)
            return true; // count unresolved -> fail CLOSED

        // GetNumContainedItems can only ever OVER-count (if it includes nested packs)
        // -> free UNDER-stated -> conservative/safe. It never under-counts its own
        // container, so a genuinely full owned pack can never slip through.
        return (capacity - count) < 1;
    }

    // Rate-limit + reentry guard for WriteToChat. AC's AddTextToScroll
    // (0x005649F0) consistently AVs at 0x00460D1D after sustained re-entry
    // from inside its own chat-add path (observed when RynthAi's buff retry
    // loop spammed the same "Casting: X" message dozens of times). Plus we
    // also hook the same function in ChatCallbackHooks.IncomingChatAddTextDetour,
    // which fires when AC processes our own WriteToChat output — guard
    // against unbounded recursion into ourselves.
    // A burst of up to 12 lines, then one per 100 ms (ChatRateLimiter, 2026-10-28; it was one line
    // per 100 ms, which cut every multi-line command reply to its first line).
    private static readonly ChatRateLimiter _chatLimiter = new();
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void EnsureChatRateLimiterReady() { /* triggers static init */ }
    [ThreadStatic] private static int _writeToChatDepth;

    public static bool WriteToChat(string? text, int chatType)
    {
        if (_addTextToScroll == null || string.IsNullOrWhiteSpace(text))
            return false;

        // Off AC's main thread: rate-limit the spam, then marshal. Running
        // AddTextToScroll off-thread races AC's chat-scroll buffer and corrupts it ->
        // the recurring 0x00460D1D write-AV (killed a 5h+ session 2026-06-05). The
        // rate-limit lives HERE (not on the drain path) so bot retry bursts are
        // dropped before they queue, and drained messages are never re-dropped.
        if (!MainThreadGuard.IsOnMainThread())
        {
            if (!_chatLimiter.TryTake(Environment.TickCount64))
                return false;
            return AcMainThreadQueue.EnqueueWriteToChat(text!, chatType);
        }

        // On AC's main thread (drain, or a legit main-thread caller): execute directly.
        // Re-entry guard: AC's AddTextToScroll calls back into our hook chain, which
        // may re-enter WriteToChat. One level is fine; deeper risks the crash pattern.
        if (_writeToChatDepth > 0)
            return false;

        try
        {
            IntPtr communicationSystem = ReadPointer(_commSystemAddr);
            if (communicationSystem == IntPtr.Zero)
                return false;

            string line = text.TrimEnd('\r', '\n');
            if (line.Length == 0)
                return false;

            // Strip any control chars that might confuse AC's chat tokenizer.
            // The previous WriteToChat crashes correlated with bursts of
            // bot retries that may have left malformed text in flight.
            foreach (char c in line)
            {
                if (c < 0x20 && c != '\t')
                    return false; // bail rather than feed AC bad input.
            }

            ushort[] chars = new ushort[line.Length + 1]; // +1 for null terminator (wcslen requirement)
            for (int i = 0; i < line.Length; i++)
                chars[i] = line[i];

            var wide = WidePString.Create(chars);
            _writeToChatDepth++;
            try
            {
                return _addTextToScroll(
                    communicationSystem,
                    ref wide,
                    unchecked((uint)chatType),
                    0,
                    IntPtr.Zero) != 0;
            }
            finally
            {
                _writeToChatDepth--;
                wide.Dispose();
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Open the salvage panel for the given salvage tool.
    /// Calls CM_Inventory::SendNotice_OpenSalvagePanel (cdecl) which dispatches
    /// the UI notice asynchronously; allow ~400 ms before calling AddItem.
    /// </summary>
    public static bool SalvagePanelOpen(uint toolId)
    {
        if (_sendNoticeOpenSalvagePanel == null || toolId == 0)
            return false;

        // Off AC's main thread (RynthAi SalvageManager / Meta tick on the plugin
        // pump): marshal. The salvage trio posts a UI notice and mutates the live
        // gmSalvageUI UIElement tree; off-thread it races AC's own UI walk, the
        // same class as the SetSelectedObject 0x60D1D AV. The ring is FIFO with
        // one consumer, so open -> add -> execute keep their order. Drain calls
        // back into this method on the main thread, where the gate is satisfied.
        if (AcMainThreadQueue.MustQueue(teardown: false))
            return AcMainThreadQueue.EnqueueSalvageOpen(toolId);

        try
        {
            _sendNoticeOpenSalvagePanel(toolId);
            return true;
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"Compat: SalvagePanelOpen threw - {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Add an item to the salvage panel.
    /// Calls gmSalvageUI::AddNewItem (thiscall) via the captured singleton.
    /// Requires the panel to have been opened at least once so SalvageHooks
    /// has captured the gmSalvageUI instance pointer.
    /// </summary>
    public static bool SalvagePanelAddItem(uint itemId)
    {
        // Decal bridge mode: salvage is left to Decal (our OpenSalvagePanel hook isn't
        // installed, so the instance is never captured) - Decal's SalvagePanelAdd instead.
        if (DecalBridgeHost.Active && !SalvageHooks.IsInstalled)
            return DecalBridgeHost.TrySalvagePanelAdd(itemId);
        if (_gmSalvageUIAddNewItem == null || itemId == 0)
            return false;

        IntPtr inst = SalvageHooks.GmSalvageUIInstance;
        if (inst == IntPtr.Zero)
        {
            RynthLog.Compat("Compat: SalvagePanelAddItem - gmSalvageUI instance not captured yet");
            return false;
        }

        // Off-thread: enqueue AFTER the instance pre-check, so a false still means
        // "panel not captured" (SalvageManager re-queues the item on it). The
        // queued entry carries only the item id: on the main thread this method
        // runs again and re-reads the instance, since the panel may have closed
        // or been rebuilt in between.
        if (AcMainThreadQueue.MustQueue(teardown: false))
            return AcMainThreadQueue.EnqueueSalvageAddItem(itemId);

        try
        {
            _gmSalvageUIAddNewItem(inst, itemId);
            return true;
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"Compat: SalvagePanelAddItem threw - {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Execute the salvage operation (click Salvage button).
    /// Calls gmSalvageUI::Salvage (thiscall) via the captured singleton.
    /// </summary>
    public static bool SalvagePanelExecute()
    {
        if (DecalBridgeHost.Active && !SalvageHooks.IsInstalled)
            return DecalBridgeHost.TrySalvagePanelSalvage();   // bridge mode: Decal's SalvagePanelSalvage
        if (_gmSalvageUISalvage == null)
            return false;

        IntPtr inst = SalvageHooks.GmSalvageUIInstance;
        if (inst == IntPtr.Zero)
        {
            RynthLog.Compat("Compat: SalvagePanelExecute - gmSalvageUI instance not captured yet");
            return false;
        }

        // Off-thread: same shape as SalvagePanelAddItem (instance re-read on the
        // main thread when the entry drains).
        if (AcMainThreadQueue.MustQueue(teardown: false))
            return AcMainThreadQueue.EnqueueSalvageExecute();

        try
        {
            _gmSalvageUISalvage(inst);
            return true;
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"Compat: SalvagePanelExecute threw - {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Binds CM_Inventory::Event_CreateTinkeringTool and finds PackableList&lt;ulong&gt;'s vtable (the
    /// sender packs the item list through it). The vtable is accepted only when its slot 3 lands in the
    /// scanned window on PackableList&lt;ulong&gt;::Pack's prologue, so a wrong address leaves
    /// <see cref="HasSalvageDirect"/> false instead of handing AC a bad list.
    /// </summary>
    private static void BindSalvageDirect(AcClientTextSection text)
    {
        _eventCreateTinkeringTool = null;
        _packableListULongVtable = IntPtr.Zero;
        try
        {
            var send = Bind<EventCreateTinkeringToolDelegate>(text, "ClientHelper.Event_CreateTinkeringTool",
                PatEventCreateTinkeringTool, EventCreateTinkeringToolVa);
            int vtable = HookResolver.ResolveData(text, "ClientHelper.PackableList_ulong_vtable",
                PatXrefPackableListULongVtable, 4, PackableListULongVtableVa).Address.ToInt32();

            int slotOff = vtable + 3 * 4 - text.TextBaseVa;
            if (send == null || slotOff < 0 || slotOff + 4 > text.Bytes.Length)
            {
                RynthLog.Compat($"Compat: salvage without the panel unavailable (sender {(send == null ? "not found" : "ok")}, vtable 0x{vtable:X8}).");
                return;
            }
            int pack = BitConverter.ToInt32(text.Bytes, slotOff);
            int packOff = pack - text.TextBaseVa;
            bool prologueOk = packOff >= 0 && packOff + PackableListULongPackPrologue.Length <= text.Bytes.Length;
            for (int i = 0; prologueOk && i < PackableListULongPackPrologue.Length; i++)
                prologueOk = text.Bytes[packOff + i] == PackableListULongPackPrologue[i];
            if (!prologueOk)
            {
                RynthLog.Compat($"Compat: salvage without the panel unavailable - vtable 0x{vtable:X8} slot 3 (0x{pack:X8}) is not PackableList<ulong>::Pack.");
                return;
            }

            _packableListULongVtable = new IntPtr(vtable);
            _eventCreateTinkeringTool = send;
            RynthLog.Verbose($"Compat: salvage without the panel ready (PackableList<ulong> vtable 0x{vtable:X8}, Pack 0x{pack:X8}).");
        }
        catch (Exception ex)
        {
            _eventCreateTinkeringTool = null;
            _packableListULongVtable = IntPtr.Zero;
            RynthLog.Compat($"Compat: salvage-without-panel binding failed - {ex.Message}");
        }
    }

    /// <summary>
    /// Salvage without the salvage panel: sends the 0x027D CreateTinkeringTool game action (what the
    /// panel's Salvage button sends) through AC's own sender, CM_Inventory::Event_CreateTinkeringTool,
    /// with <paramref name="toolId"/> (the UST) and <paramref name="itemIds"/> (at most
    /// <see cref="MaxSalvageDirectItems"/>). The list is a PackableList&lt;ulong&gt; built on this
    /// stack frame (AC's layout: vtable, head, tail, count; nodes of value, next, prev) and only read by
    /// the sender, so nothing is allocated in AC's heap and nothing outlives the call. The panel is
    /// never opened, nothing in the UI changes, and the client's busy count is not touched.
    ///
    /// ACE (Player.HandleSalvaging) checks the tool is a UST (wcid 20646) the player owns (else
    /// "That is not a salvaging tool." / "You do not own that salvage tool!"), then for each id an item
    /// in the player's packs (not worn), with a material and workmanship and not retained, is consumed;
    /// ids that fail are skipped silently. Salvage bags in the list are merged with the new salvage.
    /// The answer: the items' removal, new salvage bags created in the pack, and one "You obtain ..."
    /// line per skill (SalvageOperationsResult, printed by ClientUISystem, not the panel).
    ///
    /// Off AC's main thread it is queued (the list is copied); true = sent, or queued.
    /// </summary>
    public static bool SalvageDirect(uint toolId, uint[] itemIds)
    {
        if (!HasSalvageDirect || toolId == 0 || itemIds == null || itemIds.Length == 0 || itemIds.Length > MaxSalvageDirectItems)
            return false;
        foreach (uint id in itemIds)
            if (id == 0 || id == toolId) return false;

        // PR #8's gate: also queued on the main thread while a death/portal transition holds work.
        if (AcMainThreadQueue.MustQueue(teardown: false))
            return AcMainThreadQueue.EnqueueSalvageDirect(toolId, itemIds);

        return SendCreateTinkeringTool(toolId, itemIds);
    }

    private static unsafe bool SendCreateTinkeringTool(uint toolId, uint[] itemIds)
    {
        // Main thread only (SalvageDirect marshals; this is its main-thread half).
        if (!MainThreadGuard.IsOnMainThread()) return false;
        IntPtr vtable = _packableListULongVtable;
        if (_eventCreateTinkeringTool == null || vtable == IntPtr.Zero) return false;

        int n = itemIds.Length;
        // Header (4 dwords) + n nodes of 3 dwords, all on this frame (n <= 100: at most 1216 bytes).
        uint* buf = stackalloc uint[4 + 3 * MaxSalvageDirectItems];
        uint* nodes = buf + 4;
        for (int i = 0; i < n; i++)
        {
            uint* node = nodes + 3 * i;
            node[0] = itemIds[i];                                                     // value
            node[1] = i + 1 < n ? (uint)(nint)(nodes + 3 * (i + 1)) : 0u;            // next
            node[2] = i > 0 ? (uint)(nint)(nodes + 3 * (i - 1)) : 0u;                // prev
        }
        buf[0] = (uint)vtable.ToInt32();                                             // vtable
        buf[1] = (uint)(nint)nodes;                                                  // head
        buf[2] = (uint)(nint)(nodes + 3 * (n - 1));                                  // tail
        buf[3] = (uint)n;                                                            // count

        try
        {
            byte rv = _eventCreateTinkeringTool(toolId, (IntPtr)buf);
            RynthLog.Verbose($"Compat: Event_CreateTinkeringTool tool=0x{toolId:X8} items={n} rv={rv}");
            return rv != 0;
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"Compat: Event_CreateTinkeringTool threw - {ex.Message}");
            return false;
        }
    }

    public static bool InvokeParser(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        try
        {
            string line = text.TrimEnd('\r', '\n');
            if (line.Length == 0)
                return false;

            RynthLog.Verbose($"Compat: InvokeParser text='{line}'");
            LogoffOriginProbe.RecordChat("InvokeChatParser", line);
            return ChatCommandDispatcher.Dispatch(line);
        }
        catch (Exception ex)
        {
            try { RynthLog.Compat($"Compat: InvokeParser failed - {ex.GetType().Name}: {ex.Message}"); } catch { }
            return false;
        }
    }

    private static void Reset()
    {
        _setSelectedObject = null;
        _useObject = null;
        _getAcPlugin = null;
        _useObjectOn = null;
        _useEquippedItem = null;
        _moveItemExternal = null;
        _moveItemInternal = null;
        _eventStackableMerge = null;
        _eventGiveObjectRequest = null;
        _eventPutItemInContainer = null;
        _eventGetAndWieldItem = null;
        _eventDropItem = null;
        _eventNoLongerViewingContents = null;
        _inqPlayerCoords = null;
        _getPlayerId = null;
        Volatile.Write(ref _cachedPlayerId, 0);
        _addTextToScroll = null;
        _useWithTargetEvent = null;
        _sendNoticeOpenSalvagePanel = null;
        _gmSalvageUIAddNewItem = null;
        _gmSalvageUISalvage = null;
        _eventCreateTinkeringTool = null;
        _packableListULongVtable = IntPtr.Zero;
        _initialized = false;
    }

    private static IntPtr GetAcPlugin()
    {
        if (_getAcPlugin == null)
            return IntPtr.Zero;

        try
        {
            return _getAcPlugin();
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static void LogInteraction(string message)
    {
        if (_interactionLogCount >= 24)
            return;

        _interactionLogCount++;
        RynthLog.Verbose($"Compat: {message}");
    }

    private static uint ReadUInt32(int address)
    {
        return unchecked((uint)Marshal.ReadInt32(new IntPtr(address)));
    }

    private static IntPtr ReadPointer(int address)
    {
        return Marshal.ReadIntPtr(new IntPtr(address));
    }

    /// <summary>The ClientUISystem singleton, or Zero before the UI exists. Read-only (diagnostics).</summary>
    internal static IntPtr ReadUiSystemPtr()
    {
        IntPtr slot = new IntPtr(_uiSystemAddr);
        return ClientObjectHooks.IsReadablePointer(slot) ? Marshal.ReadIntPtr(slot) : IntPtr.Zero;
    }

    // WidePString (AC1Legacy::PStringBase<ushort>) helper fns — pattern-resolved (1a, 2026-06-05).
    private static readonly byte?[] PatWidePStringCtor = [ 0x56, 0x57, 0x8B, 0x7C, 0x24, 0x0C, 0x85, 0xFF, 0x8B, 0xF1, 0x74, 0x2C ];
    private static readonly byte?[] PatWidePStringDtor = [ 0x56, 0x8B, 0x31, 0x83, 0xEE, 0x14, 0x8D, 0x46 ];

    // Resolve a fn-ptr for static-init field use; returns the fallback VA (never null) on a
    // total miss so the WidePString call sites keep their non-null assumption (old behavior).
    private static unsafe void* ResolveFnPtr(string name, byte?[] pattern, int fallbackVa)
    {
        if (!AcClientModule.TryReadTextSection(out AcClientTextSection text))
            return (void*)fallbackVa;
        HookResolver.ResolveResult r = HookResolver.Resolve(text, name, pattern, fallbackVa);
        return (void*)(r.Success ? r.Address : new IntPtr(fallbackVa));
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct WidePString
    {
        private static readonly IntPtr NullWideBufferVa = ResolveDataVa("ClientHelper.PStringWide_NullBuffer", PatXrefWideNullBuffer, 2, 0x00818340);
        private static readonly delegate* unmanaged[Thiscall]<WidePString*, ushort*, void> Ctor = (delegate* unmanaged[Thiscall]<WidePString*, ushort*, void>)ResolveFnPtr("ClientHelper.PStringBaseW_ctor", PatWidePStringCtor, 0x00402730);
        private static readonly delegate* unmanaged[Thiscall]<WidePString*, void> Dtor = (delegate* unmanaged[Thiscall]<WidePString*, void>)ResolveFnPtr("ClientHelper.PStringBaseW_dtor", PatWidePStringDtor, 0x004011B0);

        public IntPtr CharBuffer;

        public static WidePString Create(ushort[] chars)
        {
            var value = new WidePString
            {
                CharBuffer = Marshal.ReadIntPtr(NullWideBufferVa)
            };

            fixed (ushort* pChars = chars)
                Ctor(&value, pChars);
            return value;
        }

        public void Dispose()
        {
            fixed (WidePString* ptr = &this)
                Dtor(ptr);
        }
    }
}
