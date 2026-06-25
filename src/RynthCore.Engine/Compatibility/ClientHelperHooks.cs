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
    // GameAction. amount = stack count to give (0 = whole object).
    // Args (live-disasm + call-site UIAttemptGive confirmed): item, targetNpc, amount.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte EventGiveObjectRequestDelegate(uint objectId, uint targetId, int amount);

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

    private static SetSelectedObjectDelegate? _setSelectedObject;
    private static UseObjectDelegate? _useObject;
    private static GetAcPluginDelegate? _getAcPlugin;
    private static UseObjectOnDelegate? _useObjectOn;
    private static UseEquippedItemDelegate? _useEquippedItem;
    private static MoveItemExternalDelegate? _moveItemExternal;
    private static MoveItemInternalDelegate? _moveItemInternal;
    private static EventStackableMergeDelegate? _eventStackableMerge;
    private static EventGiveObjectRequestDelegate? _eventGiveObjectRequest;
    private static InqPlayerCoordsDelegate? _inqPlayerCoords;
    private static GetPlayerIdDelegate? _getPlayerId;
    private static AddTextToScrollDelegate? _addTextToScroll;
    private static UseWithTargetEventDelegate? _useWithTargetEvent;
    private static SendNoticeOpenSalvagePanelDelegate? _sendNoticeOpenSalvagePanel;
    private static GmSalvageUIAddNewItemDelegate? _gmSalvageUIAddNewItem;
    private static GmSalvageUISalvageDelegate? _gmSalvageUISalvage;
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
    public static bool HasGetCurCoords => _inqPlayerCoords != null;
    public static bool HasGetPlayerId => _getPlayerId != null;
    public static bool HasGetGroundContainerId => true;
    public static bool HasWriteToChat => _addTextToScroll != null;
    public static bool HasInvokeParser => true;
    public static bool HasSalvagePanel => _sendNoticeOpenSalvagePanel != null && _gmSalvageUIAddNewItem != null && _gmSalvageUISalvage != null;
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
    private static readonly byte?[] PatInqPlayerCoords = [ 0x83, 0xEC, 0x10, 0x53, 0x8B, 0x5C, 0x24, 0x1C, 0x55, 0x56 ];
    private static readonly byte?[] PatGetPlayerId = [ 0xA1, 0x58, 0xDA, 0x83, 0x00, 0x85, 0xC0, 0x74, 0x07 ];
    private static readonly byte?[] PatAddTextToScroll = [ 0x81, 0xEC, 0x48, 0x09, 0x00, 0x00, 0x8A, 0x84 ];
    private static readonly byte?[] PatUseWithTargetEvent = [ 0x83, 0xEC, 0x0C, 0x53, 0x56, 0x57, 0xE8, null, null, null, null, 0x89, 0x44, 0x24, 0x14, 0x6A, 0x00, 0x8D, 0x44, 0x24, 0x10, 0x50, 0x8D, 0x4C, 0x24, 0x18, 0xC7, 0x44, 0x24, 0x18, 0x2C, 0x2C, 0x80, 0x00, 0xC7, 0x44, 0x24, 0x14, 0x00, 0x00, 0x00, 0x00, 0xE8, null, null, null, null, 0x8B, 0xF0, 0x83, 0xC6, 0x0C, 0x56, 0xE8, null, null, null, null, 0x83, 0xC4, 0x04, 0x56, 0x8D, 0x4C, 0x24, 0x10, 0x51, 0x8D, 0x4C, 0x24, 0x18, 0x89, 0x44, 0x24, 0x14, 0x8B, 0xF8, 0xE8, null, null, null, null, 0x8B, 0x54, 0x24, 0x0C, 0x8B, 0x4C, 0x24, 0x1C, 0xC7, 0x02, 0x35 ];
    private static readonly byte?[] PatSendNoticeOpenSalvagePanel = [ 0xE8, null, null, null, null, 0x8B, 0x10, 0x68, 0x22 ];
    private static readonly byte?[] PatGmSalvageUIAddNewItem = [ 0x8B, 0x44, 0x24, 0x04, 0x56, 0x57, 0x50, 0x8B, 0xF9 ];
    private static readonly byte?[] PatGmSalvageUISalvage = [ 0x83, 0xEC, 0x14, 0x53, 0x56, 0x8B, 0xF1, 0x8B, 0x8E ];

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
            _inqPlayerCoords = Bind<InqPlayerCoordsDelegate>(text, "ClientHelper.InqPlayerCoords", PatInqPlayerCoords, InqPlayerCoordsVa);
            _getPlayerId = Bind<GetPlayerIdDelegate>(text, "ClientHelper.GetPlayerId", PatGetPlayerId, GetPlayerIdVa);
            _addTextToScroll = Bind<AddTextToScrollDelegate>(text, "ClientHelper.AddTextToScroll", PatAddTextToScroll, AddTextToScrollVa);
            _useWithTargetEvent = Bind<UseWithTargetEventDelegate>(text, "ClientHelper.UseWithTargetEvent", PatUseWithTargetEvent, UseWithTargetEventVa);
            _sendNoticeOpenSalvagePanel = Bind<SendNoticeOpenSalvagePanelDelegate>(text, "ClientHelper.SendNotice_OpenSalvagePanel", PatSendNoticeOpenSalvagePanel, SendNoticeOpenSalvagePanelVa);
            _gmSalvageUIAddNewItem = Bind<GmSalvageUIAddNewItemDelegate>(text, "ClientHelper.gmSalvageUI_AddNewItem", PatGmSalvageUIAddNewItem, GmSalvageUIAddNewItemVa);
            _gmSalvageUISalvage = Bind<GmSalvageUISalvageDelegate>(text, "ClientHelper.gmSalvageUI_Salvage", PatGmSalvageUISalvage, GmSalvageUISalvageVa);
            _selectedIdAddr = HookResolver.ResolveData(text, "ClientHelper.s_selected_id", PatXrefSelectedId, 2, SelectedIdVa).Address.ToInt32();
            _prevSelectedIdAddr = HookResolver.ResolveData(text, "ClientHelper.s_previous_selected_id", PatXrefPrevSelectedId, 2, PreviousSelectedIdVa).Address.ToInt32();
            _uiSystemAddr = HookResolver.ResolveData(text, "ClientHelper.ClientUISystem", PatXrefUiSystem, 2, UiSystemVa).Address.ToInt32();
            _splitAmountAddr = HookResolver.ResolveData(text, "ClientHelper.split_amount", PatXrefSplitAmount, 2, SplitAmountVa).Address.ToInt32();
            _totalStackAddr = HookResolver.ResolveData(text, "ClientHelper.total_stack", PatXrefTotalStack, 2, TotalStackVa).Address.ToInt32();
            _playerSystemAddr = HookResolver.ResolveData(text, "ClientHelper.CPlayerSystem", PatXrefPlayerSystem, 2, PlayerSystemVa).Address.ToInt32();
            _commSystemAddr = HookResolver.ResolveData(text, "ClientHelper.CommunicationSystem", PatXrefCommunicationSystem, 2, CommunicationSystemVa).Address.ToInt32();
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
        if (!MainThreadGuard.IsOnMainThread())
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
        if (!MainThreadGuard.IsOnMainThread())
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
        if (!MainThreadGuard.IsOnMainThread())
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
        if (!MainThreadGuard.IsOnMainThread())
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
        if (!MainThreadGuard.IsOnMainThread())
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
        if (!MainThreadGuard.IsOnMainThread())
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
        if (!MainThreadGuard.IsOnMainThread())
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
        if (!MainThreadGuard.IsOnMainThread())
            return AcMainThreadQueue.EnqueueMergeStackInternal(sourceObjectId, targetObjectId);

        if (_eventStackableMerge == null) return false;
        if (!IsValidObjectId(sourceObjectId)) return false;
        if (!IsValidObjectId(targetObjectId)) return false;

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
                return false;
            }
            amount = Math.Min(sourceCount, room);
        }
        else
        {
            // Couldn't read max — fall back to full source merge.
            amount = sourceCount;
        }

        if (amount <= 0) return false;

        try
        {
            byte rv = _eventStackableMerge(sourceObjectId, targetObjectId, amount);
            RynthLog.Verbose($"Compat: Event_StackableMerge from=0x{sourceObjectId:X8}({sourceCount}) to=0x{targetObjectId:X8}({targetCount}) amount={amount}/{maxStack} rv={rv}");
            return rv != 0;
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"Compat: Event_StackableMerge threw - {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Give an item to an NPC (or another player) via the canonical public API
    /// CM_Inventory::Event_GiveObjectRequest (cdecl) at 0x006ACA60 — the F7B1 give
    /// GameAction, the same path the drag-onto-NPC UI uses. This is the CORRECT
    /// give-to-NPC primitive; MoveItemExternal is move-to-container and does not
    /// give. amount=0 gives the whole object; positive = partial stack.
    /// </summary>
    public static bool GiveObjectTo(uint objectId, uint targetId, int amount = 0)
    {
        // Marshal onto AC's main thread — off-thread F7B1 inventory sends race AC's
        // per-tick object/container bookkeeping (the looting null-deref AV class),
        // exactly like MoveItemExternal / MergeStackInternal.
        if (!MainThreadGuard.IsOnMainThread())
            return AcMainThreadQueue.EnqueueGiveObjectTo(objectId, targetId, amount);

        if (_eventGiveObjectRequest == null) return false;
        if (!IsValidObjectId(objectId)) return false;
        if (!IsValidObjectId(targetId)) return false;
        if (amount < 0) return false;

        try
        {
            byte rv = _eventGiveObjectRequest(objectId, targetId, amount);
            RynthLog.Verbose($"Compat: Event_GiveObjectRequest item=0x{objectId:X8} -> target=0x{targetId:X8} amount={amount} rv={rv}");
            return rv != 0;
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"Compat: Event_GiveObjectRequest threw - {ex.Message}");
            return false;
        }
    }

    public static bool TryGetCurCoords(out double northSouth, out double eastWest)
    {
        northSouth = 0;
        eastWest = 0;

        if (_inqPlayerCoords == null)
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

    public static uint GetPlayerId()
    {
        if (_getPlayerId == null)
            return 0;

        try
        {
            return _getPlayerId();
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
    private const int WriteToChatMinIntervalMs = 100;
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void EnsureChatRateLimiterReady() { /* triggers static init */ }
    private static long _lastWriteToChatTick;
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
            long now = Environment.TickCount64;
            long last = System.Threading.Interlocked.Read(ref _lastWriteToChatTick);
            if (last != 0 && now - last < WriteToChatMinIntervalMs)
                return false;
            System.Threading.Interlocked.Exchange(ref _lastWriteToChatTick, now);
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
        if (_gmSalvageUIAddNewItem == null || itemId == 0)
            return false;

        IntPtr inst = SalvageHooks.GmSalvageUIInstance;
        if (inst == IntPtr.Zero)
        {
            RynthLog.Compat("Compat: SalvagePanelAddItem - gmSalvageUI instance not captured yet");
            return false;
        }

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
        if (_gmSalvageUISalvage == null)
            return false;

        IntPtr inst = SalvageHooks.GmSalvageUIInstance;
        if (inst == IntPtr.Zero)
        {
            RynthLog.Compat("Compat: SalvagePanelExecute - gmSalvageUI instance not captured yet");
            return false;
        }

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
        _inqPlayerCoords = null;
        _getPlayerId = null;
        _addTextToScroll = null;
        _useWithTargetEvent = null;
        _sendNoticeOpenSalvagePanel = null;
        _gmSalvageUIAddNewItem = null;
        _gmSalvageUISalvage = null;
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
