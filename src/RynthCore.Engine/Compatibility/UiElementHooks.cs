// ============================================================================
//  RynthCore.Engine - Compatibility/UiElementHooks.cs
//
//  Tooltip and drag/drop hooks on AC's own UI (Chorizite comparison gap #5,
//  2026-10-05). Every address and offset below was checked offline against our
//  client (tools/ui_hooks_check.py; report in ops/overnight/2026-10-05-ui-hooks.md):
//
//  UIElementManager (the UI singleton; `this` of the first four):
//    StartTooltip(StringInfo*, UIElement* owner, uint, uint, uint)  0x0045DF70, ret 14h
//        stores the owner at +0x2F4, builds the tooltip element into +0x2F8.
//    ResetTooltip()   0x0045C440  frees +0x2F8, then tail-jumps into CheckTooltip.
//    CheckTooltip()   0x0045B7C0  per-frame tooltip timer (+0x325, +0x2D4, +0x24C).
//        Its `this` is also kept as Manager for AcMouseOver (AC's mouse-over, +0x244).
//    StartDragandDrop(UIElement*, int x, int y) -> bool  0x0045E120, ret 0Ch
//        on success the drag icon is at +0x31C and its owner at +0x320; it calls
//        itself once for a drag proxy (we report only the outermost call).
//  UIElement::CatchDroppedItem(DragDropInfo*) -> bool  0x00461860, ret 4
//        the catcher's virtual (vtable +0xD8), called from StopDragandDrop
//        (0x00459880). All 113 UIElement vtables in the client route it here:
//        36 directly, 77 through UIElement_Field::CatchDroppedItem, which calls
//        it. It broadcasts element message 0x15 (drop release) to the catcher;
//        that message is what drops an item on the ground (SmartBoxWrapper),
//        moves it into a pack (ItemList) and so on. DragDropInfo: +0x08 the
//        drag icon, +0x0C its owner, +0x10 the catcher, +0x14 success.
//  Not hooked, called: UIElement_ItemList::InqDropIconInfo(UIElement*, uint* item,
//        uint* spell, uint* flags), cdecl, 0x004E3380: AC's own "which object or
//        spell is this icon" (13 callers in the client; reads element properties
//        only, null-safe).
//  UIElement offsets: +0x2E4 element id (GetAncestorByID compares it), vtable
//        +0xA0 GetParent, +0x98 GetUIElementType (UIElement_ItemList: 0x10000031).
//
//  Events, on AC's main thread, queued to plugins (API v77):
//    tooltip shown (object id or spell id of the hovered icon), tooltip hidden,
//    drag started (object/spell id, icon), item dropped (object/spell id, the
//    catcher's element id).
//  Drop claims: an item dragged out of an AC item list and let go over an engine
//    panel that takes drops (ImGui/UiDropTargets: the Loot Editor) never reaches
//    AC: CatchDroppedItem answers false without running, which is AC's own
//    "not caught" path (StopDragandDrop then tells the source list the drop
//    failed; ItemList::HandleDropRelease ignores a failed drop whose catcher isn't
//    inside it). Not claimed when the catcher is inside the source list itself.
//
//  Not done (needs in-game checks): adding lines to AC's own tooltip text. That
//  means building an AC StringInfo/PStringBase and editing the tooltip element
//  AC just made, inside its allocator - see the report.
// ============================================================================
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace RynthCore.Engine.Compatibility;

internal static unsafe class UiElementHooks
{
    // Fallback VAs (4,841,472-byte client). Pattern-scan is the source of truth.
    private const int StartTooltipFallbackVa = 0x0045DF70;
    private const int ResetTooltipFallbackVa = 0x0045C440;
    private const int CheckTooltipFallbackVa = 0x0045B7C0;
    private const int StartDragandDropFallbackVa = 0x0045E120;
    private const int CatchDroppedItemFallbackVa = 0x00461860;
    private const int InqDropIconInfoFallbackVa = 0x004E3380;

    private const int MgrTooltipOwner = 0x2F4;
    private const int MgrTooltipElement = 0x2F8;
    private const int MgrDragIcon = 0x31C;
    private const int MgrDragOwner = 0x320;
    private const int ElementId = 0x2E4;
    private const int VtGetUIElementType = 0x98;
    private const int VtGetParent = 0xA0;
    private const int DdiElement = 0x08;
    private const int DdiOwner = 0x0C;
    private const int UIElementTypeItemList = 0x10000031;

    // StartTooltip: sub esp,10h / push ebp / push esi / mov esi,ecx / mov ecx,[esi+2F8h] /
    // xor ebp,ebp / cmp ecx,ebp / push edi / mov [esp+18h],esi / jz +0Bh / call rel32 /
    // mov [esi+2F8h],ebp - frees an old tooltip element first. Unique, lands at 0x0045DF70.
    private static readonly byte?[] StartTooltipPattern =
    [
        0x83, 0xEC, 0x10, 0x55, 0x56, 0x8B, 0xF1, 0x8B, 0x8E, 0xF8, 0x02, 0x00, 0x00,
        0x33, 0xED, 0x3B, 0xCD, 0x57, 0x89, 0x74, 0x24, 0x18, 0x74, 0x0B,
        0xE8, null, null, null, null, 0x89, 0xAE, 0xF8, 0x02, 0x00, 0x00
    ];

    // ResetTooltip: push esi / mov esi,ecx / mov ecx,[esi+2F8h] / test ecx,ecx / jz +0Fh /
    // call rel32 / mov dword [esi+2F8h],0 / mov al,[esi+325h]. Unique, lands at 0x0045C440.
    private static readonly byte?[] ResetTooltipPattern =
    [
        0x56, 0x8B, 0xF1, 0x8B, 0x8E, 0xF8, 0x02, 0x00, 0x00, 0x85, 0xC9, 0x74, 0x0F,
        0xE8, null, null, null, null, 0xC7, 0x86, 0xF8, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x8A, 0x86, 0x25, 0x03, 0x00, 0x00
    ];

    // CheckTooltip: sub esp,0Ch / mov eax,[imm32] / push esi / mov esi,ecx / mov ecx,[imm32] /
    // mov [esp+8],eax / mov al,[esi+325h] / test al,al / mov [esp+0Ch],ecx / jnz +79h /
    // mov al,[esi+2D4h]. The two timer globals wildcarded. Unique, lands at 0x0045B7C0.
    private static readonly byte?[] CheckTooltipPattern =
    [
        0x83, 0xEC, 0x0C, 0xA1, null, null, null, null, 0x56, 0x8B, 0xF1, 0x8B, 0x0D, null, null, null, null,
        0x89, 0x44, 0x24, 0x08, 0x8A, 0x86, 0x25, 0x03, 0x00, 0x00, 0x84, 0xC0, 0x89, 0x4C, 0x24, 0x0C,
        0x75, 0x79, 0x8A, 0x86, 0xD4, 0x02, 0x00, 0x00
    ];

    // StartDragandDrop: sub esp,34h / push ebx,ebp,esi,edi / mov edi,[esp+48h] / test edi,edi /
    // mov esi,ecx / jz rel32 / mov eax,[imm32] / test eax,eax / jz rel32 / mov ebx,[imm32] /
    // test ebx,ebx. Unique, lands at 0x0045E120.
    private static readonly byte?[] StartDragandDropPattern =
    [
        0x83, 0xEC, 0x34, 0x53, 0x55, 0x56, 0x57, 0x8B, 0x7C, 0x24, 0x48, 0x85, 0xFF, 0x8B, 0xF1,
        0x0F, 0x84, null, null, null, null, 0xA1, null, null, null, null, 0x85, 0xC0,
        0x0F, 0x84, null, null, null, null, 0x8B, 0x1D, null, null, null, null, 0x85, 0xDB
    ];

    // UIElement::CatchDroppedItem, the whole function: mov eax,[esp+4] / push 0 / push eax /
    // push 15h / call BroadcastElementMessage / mov al,1 / ret 4. Unique, lands at 0x00461860.
    private static readonly byte?[] CatchDroppedItemPattern =
    [
        0x8B, 0x44, 0x24, 0x04, 0x6A, 0x00, 0x50, 0x6A, 0x15, 0xE8, null, null, null, null,
        0xB0, 0x01, 0xC2, 0x04, 0x00
    ];

    // UIElement_ItemList::InqDropIconInfo: mov eax,[esp+10h] / sub esp,3Ch / push ebx / push ebp /
    // mov ebp,[esp+50h] / push esi / mov esi,[esp+4Ch] / xor ebx,ebx / cmp esi,ebx / push edi /
    // mov edi,[esp+54h] / mov [edi],ebx / mov [ebp],ebx / mov [eax],ebx. Unique, lands at 0x004E3380.
    private static readonly byte?[] InqDropIconInfoPattern =
    [
        0x8B, 0x44, 0x24, 0x10, 0x83, 0xEC, 0x3C, 0x53, 0x55, 0x8B, 0x6C, 0x24, 0x50, 0x56,
        0x8B, 0x74, 0x24, 0x4C, 0x33, 0xDB, 0x3B, 0xF3, 0x57, 0x8B, 0x7C, 0x24, 0x54,
        0x89, 0x1F, 0x89, 0x5D, 0x00, 0x89, 0x18
    ];

    private static readonly UiHookSlot StartTooltipSlot =
        UiHookRegistry.Register("StartTooltip", "UIElementManager::StartTooltip - tooltip shown", StartTooltipFallbackVa);
    private static readonly UiHookSlot ResetTooltipSlot =
        UiHookRegistry.Register("ResetTooltip", "UIElementManager::ResetTooltip - tooltip reset", ResetTooltipFallbackVa);
    private static readonly UiHookSlot CheckTooltipSlot =
        UiHookRegistry.Register("CheckTooltip", "UIElementManager::CheckTooltip - tooltip expired", CheckTooltipFallbackVa);
    private static readonly UiHookSlot StartDragSlot =
        UiHookRegistry.Register("StartDragandDrop", "UIElementManager::StartDragandDrop - drag started", StartDragandDropFallbackVa);
    private static readonly UiHookSlot CatchDropSlot =
        UiHookRegistry.Register("CatchDroppedItem", "UIElement::CatchDroppedItem - item dropped", CatchDroppedItemFallbackVa);

    private static IntPtr _origStartTooltip, _origResetTooltip, _origCheckTooltip, _origStartDrag, _origCatchDrop;
    private static IntPtr _inqDropIconInfo;
    private static int _initialized;

    // AC's main thread only (every detour runs there; faces draw there).
    private static IntPtr _mgr;
    private static bool _dead;                 // Client::Cleanup started: touch nothing of AC's UI
    private static bool _tooltipShown;
    private static IntPtr _tooltipOwner;
    private static uint _tooltipObject, _tooltipSpell;
    private static int _dragDepth;
    private static uint _dragObject, _dragSpell;
    private static int _claimed;

    public static void Initialize()
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0)
            return;

        if (!AcClientModule.TryReadTextSection(out AcClientTextSection text))
        {
            foreach (UiHookSlot s in new[] { StartTooltipSlot, ResetTooltipSlot, CheckTooltipSlot, StartDragSlot, CatchDropSlot })
                s.Reason = "acclient.exe not available";
            RynthLog.Compat("UiElementHooks: acclient.exe not available - tooltip and drag/drop hooks left out.");
            return;
        }

        // Every event names its object through InqDropIconInfo: without it, none goes in.
        HookResolver.ResolveResult inq = HookResolver.Resolve(text, "UiElement.InqDropIconInfo", InqDropIconInfoPattern, InqDropIconInfoFallbackVa);
        if (!inq.Success)
        {
            foreach (UiHookSlot s in new[] { StartTooltipSlot, ResetTooltipSlot, CheckTooltipSlot, StartDragSlot, CatchDropSlot })
                s.Reason = "UIElement_ItemList::InqDropIconInfo not found (names the objects)";
            RynthLog.Compat("UiElementHooks: InqDropIconInfo not found - tooltip and drag/drop hooks left out.");
            return;
        }
        _inqDropIconInfo = inq.Address;

        if (UiHookRegistry.SwitchedOn(StartTooltipSlot))
        {
            HookResolver.ResolveResult r = HookResolver.Resolve(text, "UiElement.StartTooltip", StartTooltipPattern, StartTooltipFallbackVa);
            delegate* unmanaged[Thiscall]<IntPtr, IntPtr, IntPtr, uint, uint, uint, void> d = &StartTooltipDetour;
            if (UiHookRegistry.TryInstall(StartTooltipSlot, r, (IntPtr)d, out IntPtr t)) { _origStartTooltip = t; Thread.MemoryBarrier(); UiHookRegistry.Enable(StartTooltipSlot); }
        }
        if (UiHookRegistry.SwitchedOn(ResetTooltipSlot))
        {
            HookResolver.ResolveResult r = HookResolver.Resolve(text, "UiElement.ResetTooltip", ResetTooltipPattern, ResetTooltipFallbackVa);
            delegate* unmanaged[Thiscall]<IntPtr, void> d = &ResetTooltipDetour;
            if (UiHookRegistry.TryInstall(ResetTooltipSlot, r, (IntPtr)d, out IntPtr t)) { _origResetTooltip = t; Thread.MemoryBarrier(); UiHookRegistry.Enable(ResetTooltipSlot); }
        }
        if (UiHookRegistry.SwitchedOn(CheckTooltipSlot))
        {
            HookResolver.ResolveResult r = HookResolver.Resolve(text, "UiElement.CheckTooltip", CheckTooltipPattern, CheckTooltipFallbackVa);
            delegate* unmanaged[Thiscall]<IntPtr, void> d = &CheckTooltipDetour;
            if (UiHookRegistry.TryInstall(CheckTooltipSlot, r, (IntPtr)d, out IntPtr t)) { _origCheckTooltip = t; Thread.MemoryBarrier(); UiHookRegistry.Enable(CheckTooltipSlot); }
        }
        if (UiHookRegistry.SwitchedOn(StartDragSlot))
        {
            HookResolver.ResolveResult r = HookResolver.Resolve(text, "UiElement.StartDragandDrop", StartDragandDropPattern, StartDragandDropFallbackVa);
            delegate* unmanaged[Thiscall]<IntPtr, IntPtr, int, int, byte> d = &StartDragandDropDetour;
            if (UiHookRegistry.TryInstall(StartDragSlot, r, (IntPtr)d, out IntPtr t)) { _origStartDrag = t; Thread.MemoryBarrier(); UiHookRegistry.Enable(StartDragSlot); }
        }
        if (UiHookRegistry.SwitchedOn(CatchDropSlot))
        {
            HookResolver.ResolveResult r = HookResolver.Resolve(text, "UiElement.CatchDroppedItem", CatchDroppedItemPattern, CatchDroppedItemFallbackVa);
            delegate* unmanaged[Thiscall]<IntPtr, IntPtr, byte> d = &CatchDroppedItemDetour;
            if (UiHookRegistry.TryInstall(CatchDropSlot, r, (IntPtr)d, out IntPtr t)) { _origCatchDrop = t; Thread.MemoryBarrier(); UiHookRegistry.Enable(CatchDropSlot); }
        }

        RynthLog.Compat($"UiElementHooks: tooltip {On(StartTooltipSlot)}/{On(ResetTooltipSlot)}/{On(CheckTooltipSlot)}, " +
                        $"drag {On(StartDragSlot)}, drop {On(CatchDropSlot)} (InqDropIconInfo @ 0x{_inqDropIconInfo.ToInt32():X8}).");
    }

    private static string On(UiHookSlot s) => s.Installed ? "hooked" : "out";

    // ── What plugins and faces read ─────────────────────────────────────

    /// <summary>Tooltip/drag/drop hooks a plugin can rely on (GetUiHookFlags bits 3-6).</summary>
    public static bool TooltipShowLive => StartTooltipSlot.Live;
    public static bool TooltipHideLive => ResetTooltipSlot.Live || CheckTooltipSlot.Live;
    public static bool DragStartLive => StartDragSlot.Live;
    public static bool DropLive => CatchDropSlot.Live;

    /// <summary>
    /// AC's main thread: the object being dragged out of AC's UI right now, or 0. Checks
    /// the UI manager's drag icon (+0x31C) so a drag that ended without a catch (let go
    /// outside every element) clears too.
    /// </summary>
    public static uint CurrentDragObject
    {
        get
        {
            if (_dragObject == 0)
                return 0;
            if (_dead || _mgr == IntPtr.Zero || !MainThreadGuard.IsOnMainThread())
                return _dead ? 0 : _dragObject;
            if (*(IntPtr*)(_mgr + MgrDragIcon) == IntPtr.Zero)
            {
                _dragObject = _dragSpell = 0;
                return 0;
            }
            return _dragObject;
        }
    }

    /// <summary>The object whose tooltip AC shows, or 0 (any thread; a plain field).</summary>
    public static uint CurrentTooltipObject => _tooltipShown ? _tooltipObject : 0;

    /// <summary>
    /// AC's main thread: the UIElementManager singleton (`this` of the detours; CheckTooltip
    /// runs every frame from UseTime), or zero before the first call and after Client::Cleanup.
    /// AcMouseOver reads AC's mouse-over element through it.
    /// </summary>
    public static IntPtr Manager => _dead ? IntPtr.Zero : _mgr;

    /// <summary>UiFlowHooks, leaving the world (main thread): forget tooltip/drag state; no events.</summary>
    public static void OnScreenLeaving()
    {
        _tooltipShown = false;
        _tooltipOwner = IntPtr.Zero;
        _tooltipObject = _tooltipSpell = 0;
        _dragObject = _dragSpell = 0;
    }

    /// <summary>UiFlowHooks, Client::Cleanup (main thread): AC's UI is about to go; never touch it again.</summary>
    public static void OnClientCleanup()
    {
        _dead = true;
        _mgr = IntPtr.Zero;
        OnScreenLeaving();
    }

    // ── Detours ──────────────────────────────────────────────────────────

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    private static void StartTooltipDetour(IntPtr mgr, IntPtr stringInfo, IntPtr owner, uint idTooltip, uint idLayout, uint idText)
    {
        ((delegate* unmanaged[Thiscall]<IntPtr, IntPtr, IntPtr, uint, uint, uint, void>)_origStartTooltip)(mgr, stringInfo, owner, idTooltip, idLayout, idText);
        if (!StartTooltipSlot.Enabled || _dead || mgr == IntPtr.Zero)
            return;
        try
        {
            _mgr = mgr;
            if (*(IntPtr*)(mgr + MgrTooltipElement) == IntPtr.Zero)
                return;   // AC decided not to show one
            IntPtr shownOwner = *(IntPtr*)(mgr + MgrTooltipOwner);
            if (shownOwner == IntPtr.Zero) shownOwner = owner;
            Identify(shownOwner, 2, out uint obj, out uint spell);
            bool same = _tooltipShown && shownOwner == _tooltipOwner && obj == _tooltipObject && spell == _tooltipSpell;
            _tooltipShown = true;
            _tooltipOwner = shownOwner;
            _tooltipObject = obj;
            _tooltipSpell = spell;
            if (same)
                return;
            StartTooltipSlot.Note(obj != 0 ? $"object 0x{obj:X8}" : spell != 0 ? $"spell {spell}" : "a non-item element");
            Plugins.PluginManager.QueueTooltipShown(obj, spell);
        }
        catch (Exception ex)
        {
            LogOnce("StartTooltip", ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    private static void ResetTooltipDetour(IntPtr mgr)
    {
        ((delegate* unmanaged[Thiscall]<IntPtr, void>)_origResetTooltip)(mgr);
        if (!_dead && mgr != IntPtr.Zero) _mgr = mgr;
        if (ResetTooltipSlot.Enabled)
            AfterTooltipCheck(mgr, ResetTooltipSlot);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    private static void CheckTooltipDetour(IntPtr mgr)
    {
        ((delegate* unmanaged[Thiscall]<IntPtr, void>)_origCheckTooltip)(mgr);
        if (!_dead && mgr != IntPtr.Zero) _mgr = mgr;   // the manager for AcMouseOver, every frame
        if (CheckTooltipSlot.Enabled)
            AfterTooltipCheck(mgr, CheckTooltipSlot);
    }

    // A tooltip we reported is gone when AC's tooltip element pointer is clear again.
    private static void AfterTooltipCheck(IntPtr mgr, UiHookSlot slot)
    {
        if (!_tooltipShown || _dead || mgr == IntPtr.Zero)
            return;
        try
        {
            if (*(IntPtr*)(mgr + MgrTooltipElement) != IntPtr.Zero)
                return;
            uint obj = _tooltipObject;
            _tooltipShown = false;
            _tooltipOwner = IntPtr.Zero;
            _tooltipObject = _tooltipSpell = 0;
            slot.Note(obj != 0 ? $"hid object 0x{obj:X8}" : "hid");
            Plugins.PluginManager.QueueTooltipHidden();
        }
        catch (Exception ex)
        {
            LogOnce(slot.Name, ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    private static byte StartDragandDropDetour(IntPtr mgr, IntPtr element, int x, int y)
    {
        var original = (delegate* unmanaged[Thiscall]<IntPtr, IntPtr, int, int, byte>)_origStartDrag;
        byte result;
        _dragDepth++;
        try { result = original(mgr, element, x, y); }
        finally { _dragDepth--; }

        // The original calls itself once for a drag proxy: report the outermost call only.
        if (result == 0 || _dragDepth > 0 || !StartDragSlot.Enabled || _dead || mgr == IntPtr.Zero)
            return result;
        try
        {
            _mgr = mgr;
            IntPtr icon = *(IntPtr*)(mgr + MgrDragIcon);
            if (icon == IntPtr.Zero)
                return result;
            Identify(icon, 0, out uint obj, out uint spell);
            if (obj == 0 && spell == 0)
                Identify(element, 2, out obj, out spell);
            _dragObject = obj;
            _dragSpell = spell;
            uint iconId = 0;
            if (obj != 0)
                ClientObjectHooks.TryGetObjectDataIdProperty(obj, 8 /* Icon */, out iconId);
            StartDragSlot.Note(obj != 0 ? $"object 0x{obj:X8}" : spell != 0 ? $"spell {spell}" : "a non-item element");
            Plugins.PluginManager.QueueDragStarted(obj, spell, iconId);
        }
        catch (Exception ex)
        {
            LogOnce("StartDragandDrop", ex);
        }
        return result;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    private static byte CatchDroppedItemDetour(IntPtr catcher, IntPtr dragDropInfo)
    {
        var original = (delegate* unmanaged[Thiscall]<IntPtr, IntPtr, byte>)_origCatchDrop;
        if (!CatchDropSlot.Enabled || _dead || dragDropInfo == IntPtr.Zero)
            return original(catcher, dragDropInfo);

        uint obj = 0, spell = 0;
        try
        {
            IntPtr dragged = *(IntPtr*)(dragDropInfo + DdiElement);
            IntPtr owner = *(IntPtr*)(dragDropInfo + DdiOwner);
            if (dragged != IntPtr.Zero)
                Identify(dragged, 0, out obj, out spell);

            // An item out of an AC item list, let go over an engine panel that takes it.
            if (obj != 0 && IsItemList(owner) && !IsWithin(catcher, owner)
                && ImGuiBackend.UiDropTargets.TryClaim(obj, spell, out string target))
            {
                Interlocked.Increment(ref _claimed);
                _dragObject = _dragSpell = 0;
                CatchDropSlot.Note($"object 0x{obj:X8} -> RynthCore {target} (kept from AC)");
                RynthLog.Compat($"UiElementHooks: object 0x{obj:X8} dropped on RynthCore {target}; AC's drop skipped (not caught).");
                return 0;
            }
        }
        catch (Exception ex)
        {
            LogOnce("CatchDroppedItem", ex);
        }

        byte result = original(catcher, dragDropInfo);
        try
        {
            _dragObject = _dragSpell = 0;
            uint targetId = catcher != IntPtr.Zero ? *(uint*)(catcher + ElementId) : 0;
            CatchDropSlot.Note($"{(obj != 0 ? $"object 0x{obj:X8}" : spell != 0 ? $"spell {spell}" : "no object")} on element 0x{targetId:X8}");
            if (obj != 0 || spell != 0)
                Plugins.PluginManager.QueueItemDropped(obj, spell, targetId);
        }
        catch (Exception ex)
        {
            LogOnce("CatchDroppedItem", ex);
        }
        return result;
    }

    // ── AC UI helpers (main thread) ──────────────────────────────────────

    /// <summary>AC's InqDropIconInfo on <paramref name="element"/>, then up to <paramref name="parents"/> parents.</summary>
    private static void Identify(IntPtr element, int parents, out uint objectId, out uint spellId)
    {
        objectId = spellId = 0;
        var inq = (delegate* unmanaged[Cdecl]<IntPtr, uint*, uint*, uint*, void>)_inqDropIconInfo;
        for (int i = 0; i <= parents && element != IntPtr.Zero; i++)
        {
            uint item = 0, spell = 0, flags = 0;
            inq(element, &item, &spell, &flags);
            if (item != 0 || spell != 0)
            {
                objectId = item;
                spellId = spell;
                return;
            }
            element = Parent(element);
        }
    }

    private static IntPtr Parent(IntPtr element)
    {
        IntPtr vtable = *(IntPtr*)element;
        var getParent = (delegate* unmanaged[Thiscall]<IntPtr, IntPtr>)(*(IntPtr*)(vtable + VtGetParent));
        return getParent(element);
    }

    private static bool IsItemList(IntPtr element)
    {
        if (element == IntPtr.Zero)
            return false;
        IntPtr vtable = *(IntPtr*)element;
        var getType = (delegate* unmanaged[Thiscall]<IntPtr, int>)(*(IntPtr*)(vtable + VtGetUIElementType));
        return getType(element) == UIElementTypeItemList;
    }

    private static bool IsWithin(IntPtr element, IntPtr ancestor)
    {
        for (int i = 0; i < 32 && element != IntPtr.Zero; i++)
        {
            if (element == ancestor)
                return true;
            element = Parent(element);
        }
        return false;
    }

    private static int _loggedErrors;

    private static void LogOnce(string where, Exception ex)
    {
        if (Interlocked.Increment(ref _loggedErrors) <= 5)
            try { RynthLog.Compat($"UiElementHooks: {where} threw {ex.GetType().Name}: {ex.Message}"); } catch { }
    }

    public static string DescribeState()
        => $"UI elements: tooltip {(_tooltipShown ? (_tooltipObject != 0 ? $"on object 0x{_tooltipObject:X8}" : "shown") : "none")}, " +
           $"drag {(_dragObject != 0 ? $"object 0x{_dragObject:X8}" : "none")}, {Volatile.Read(ref _claimed)} drop(s) kept for RynthCore. " +
           ImGuiBackend.UiDropTargets.Describe();
}
