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
    private const int UseObjectVa = 0x00565750;
    private const int PlayerSystemVa = 0x0087119C;
    private const int InqPlayerCoordsVa = 0x00560E00;
    private const int UiSystemVa = 0x00871354;
    private const int GetPlayerIdVa = 0x0048E5F0;
    private const int CommunicationSystemVa = 0x00870BE4;
    private const int AddTextToScrollVa = 0x005649F0;
    private const int UseWithTargetEventVa = 0x006AD3E0;

    private static SetSelectedObjectDelegate? _setSelectedObject;
    private static UseObjectDelegate? _useObject;
    private static GetAcPluginDelegate? _getAcPlugin;
    private static UseObjectOnDelegate? _useObjectOn;
    private static UseEquippedItemDelegate? _useEquippedItem;
    private static MoveItemExternalDelegate? _moveItemExternal;
    private static MoveItemInternalDelegate? _moveItemInternal;
    private static EventStackableMergeDelegate? _eventStackableMerge;
    private static InqPlayerCoordsDelegate? _inqPlayerCoords;
    private static GetPlayerIdDelegate? _getPlayerId;
    private static AddTextToScrollDelegate? _addTextToScroll;
    private static UseWithTargetEventDelegate? _useWithTargetEvent;
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
    public static bool HasGetCurCoords => _inqPlayerCoords != null;
    public static bool HasGetPlayerId => _getPlayerId != null;
    public static bool HasGetGroundContainerId => true;
    public static bool HasWriteToChat => _addTextToScroll != null;
    public static bool HasInvokeParser => true;
    private static int _currentGroundContainerId;

    public static bool Probe()
    {
        try
        {
            _setSelectedObject = Marshal.GetDelegateForFunctionPointer<SetSelectedObjectDelegate>(new IntPtr(SetSelectedObjectVa));
            _useObject = Marshal.GetDelegateForFunctionPointer<UseObjectDelegate>(new IntPtr(UseObjectVa));
            _getAcPlugin = Marshal.GetDelegateForFunctionPointer<GetAcPluginDelegate>(new IntPtr(GetAcPluginVa));
            _useObjectOn = Marshal.GetDelegateForFunctionPointer<UseObjectOnDelegate>(new IntPtr(UseObjectOnVa));
            _useEquippedItem = Marshal.GetDelegateForFunctionPointer<UseEquippedItemDelegate>(new IntPtr(UseEquippedItemVa));
            _moveItemExternal = Marshal.GetDelegateForFunctionPointer<MoveItemExternalDelegate>(new IntPtr(MoveItemExternalVa));
            _moveItemInternal = Marshal.GetDelegateForFunctionPointer<MoveItemInternalDelegate>(new IntPtr(MoveItemInternalVa));
            _eventStackableMerge = Marshal.GetDelegateForFunctionPointer<EventStackableMergeDelegate>(new IntPtr(EventStackableMergeVa));
            _inqPlayerCoords = Marshal.GetDelegateForFunctionPointer<InqPlayerCoordsDelegate>(new IntPtr(InqPlayerCoordsVa));
            _getPlayerId = Marshal.GetDelegateForFunctionPointer<GetPlayerIdDelegate>(new IntPtr(GetPlayerIdVa));
            _addTextToScroll = Marshal.GetDelegateForFunctionPointer<AddTextToScrollDelegate>(new IntPtr(AddTextToScrollVa));
            _useWithTargetEvent = Marshal.GetDelegateForFunctionPointer<UseWithTargetEventDelegate>(new IntPtr(UseWithTargetEventVa));
            _initialized = true;
            _statusMessage = "Ready.";
            RynthLog.Compat("Compat: helper hooks ready - validated select/state/chat helpers plus mapped interaction and inventory helpers.");
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
        return ReadUInt32(SelectedIdVa);
    }

    public static uint GetPreviousSelectedItemId()
    {
        return ReadUInt32(PreviousSelectedIdVa);
    }

    public static bool UseObject(uint objectId)
    {
        if (_useObject == null || !IsValidObjectId(objectId))
            return false;

        try
        {
            IntPtr uiSystem = ReadPointer(UiSystemVa);
            if (uiSystem == IntPtr.Zero)
                return false;

            _useObject(uiSystem, objectId);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool UseObjectOn(uint sourceObjectId, uint targetObjectId)
    {
        if (_useWithTargetEvent == null || !IsValidObjectId(sourceObjectId) || !IsValidObjectId(targetObjectId))
            return false;

        try
        {
            _useWithTargetEvent(sourceObjectId, targetObjectId);
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
        if (_moveItemExternal == null || !IsValidObjectId(objectId) || !IsValidContainerId(targetContainerId) || amount < 0)
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
        if (_moveItemInternal == null) return false;
        if (!IsValidObjectId(objectId)) return false;
        if (!IsValidContainerId(targetContainerId)) return false;
        if (slot < 0) return false;
        if (amount <= 0) return false;

        try
        {
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
            Marshal.WriteInt32(new IntPtr(SplitAmountVa), amount);
            Marshal.WriteInt32(new IntPtr(TotalStackVa), amount + 1);

            _moveItemInternal(IntPtr.Zero, objectId, targetContainerId, slot, amount);
            return true;
        }
        catch
        {
            return false;
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
                RynthLog.Compat($"Compat: Event_StackableMerge skipped - target 0x{targetObjectId:X8}({targetCount}) is full (max={maxStack})");
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
            RynthLog.Compat($"Compat: Event_StackableMerge from=0x{sourceObjectId:X8}({sourceCount}) to=0x{targetObjectId:X8}({targetCount}) amount={amount}/{maxStack} rv={rv}");
            return rv != 0;
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"Compat: Event_StackableMerge threw - {ex.Message}");
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
            IntPtr playerSystem = ReadPointer(PlayerSystemVa);
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

    public static bool WriteToChat(string? text, int chatType)
    {
        if (_addTextToScroll == null || string.IsNullOrWhiteSpace(text))
            return false;

        try
        {
            IntPtr communicationSystem = ReadPointer(CommunicationSystemVa);
            if (communicationSystem == IntPtr.Zero)
                return false;

            string line = text.TrimEnd('\r', '\n');
            if (line.Length == 0)
                return false;

            ushort[] chars = new ushort[line.Length + 1]; // +1 for null terminator (wcslen requirement)
            for (int i = 0; i < line.Length; i++)
                chars[i] = line[i];

            var wide = WidePString.Create(chars);
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
                wide.Dispose();
            }
        }
        catch
        {
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

            RynthLog.Compat($"Compat: InvokeParser text='{line}'");
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
        _inqPlayerCoords = null;
        _getPlayerId = null;
        _addTextToScroll = null;
        _useWithTargetEvent = null;
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
        RynthLog.Compat($"Compat: {message}");
    }

    private static uint ReadUInt32(int address)
    {
        return unchecked((uint)Marshal.ReadInt32(new IntPtr(address)));
    }

    private static IntPtr ReadPointer(int address)
    {
        return Marshal.ReadIntPtr(new IntPtr(address));
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct WidePString
    {
        private static readonly IntPtr NullWideBufferVa = new(0x00818340);
        private static readonly delegate* unmanaged[Thiscall]<WidePString*, ushort*, void> Ctor = (delegate* unmanaged[Thiscall]<WidePString*, ushort*, void>)0x00402730;
        private static readonly delegate* unmanaged[Thiscall]<WidePString*, void> Dtor = (delegate* unmanaged[Thiscall]<WidePString*, void>)0x004011B0;

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
