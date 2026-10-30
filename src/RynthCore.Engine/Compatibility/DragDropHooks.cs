// ============================================================================
//  RynthCore.Engine - Compatibility/DragDropHooks.cs
//  Hooks UIElementManager::StartDragandDrop so the engine knows which item AC
//  is actually dragging. AC does not change the selected object when a drag
//  starts, so the selection is the previously clicked item, not the dragged one.
//
//  The dragged UI element is resolved to an object id with AC's own
//  UIElement_ItemList::InqDropIconInfo (the same call AC makes when the drop
//  lands), then handed to ItemDragBridge, which republishes it to ImGui.
//
//  Threads: AC's main thread (AC's input / UI processing).
// ============================================================================

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RynthCore.Engine.Hooking;
using RynthCore.Engine.ImGuiBackend;

namespace RynthCore.Engine.Compatibility;

internal static unsafe class DragDropHooks
{
    // bool __thiscall UIElementManager::StartDragandDrop(UIElementManager*, UIElement* elem, int clickX, int clickY)
    private const int StartDragandDropVa = 0x0045E120;
    // void __cdecl UIElement_ItemList::InqDropIconInfo(const UIElement* icon, uint& itemId, uint& spellId, DropItemFlags& flags)
    private const int InqDropIconInfoVa = 0x004E3380;

    // Both verified unique in the shipped client (sub esp,34h / mov edi,[esp+48h] / mov esi,ecx prologue;
    // InqDropIconInfo zeroes its out-params up front).
    private static readonly byte?[] StartDragandDropPattern =
    [
        0x83, 0xEC, 0x34, 0x53, 0x55, 0x56, 0x57, 0x8B, 0x7C, 0x24, 0x48, 0x85, 0xFF, 0x8B, 0xF1, 0x0F, 0x84,
        null, null, null, null, 0xA1, null, null, null, null, 0x85, 0xC0
    ];
    private static readonly byte?[] InqDropIconInfoPattern =
    [
        0x8B, 0x44, 0x24, 0x10, 0x83, 0xEC, 0x3C, 0x53, 0x55, 0x8B, 0x6C, 0x24, 0x50, 0x56, 0x8B, 0x74, 0x24, 0x4C,
        0x33, 0xDB, 0x3B, 0xF3, 0x57, 0x8B, 0x7C, 0x24, 0x54, 0x89, 0x1F
    ];

    private static IntPtr _originalStartDrag;
    private static delegate* unmanaged[Cdecl]<IntPtr, uint*, uint*, uint*, void> _inqDropIconInfo;
    private static int _logCount;

    /// <summary>The drag-start hook is live, so ItemDragBridge gets the real dragged item.</summary>
    public static bool IsInstalled { get; private set; }
    public static string StatusMessage { get; private set; } = "Not probed yet.";

    public static void Initialize()
    {
        if (IsInstalled) return;
        if (!AcClientModule.TryReadTextSection(out AcClientTextSection text))
        {
            StatusMessage = "acclient.exe not available.";
            return;
        }

        var inq = HookResolver.Resolve(text, "DragDrop.InqDropIconInfo", InqDropIconInfoPattern, InqDropIconInfoVa);
        var start = HookResolver.Resolve(text, "DragDrop.StartDragandDrop", StartDragandDropPattern, StartDragandDropVa);
        // A guessed address for either would mean calling or patching the wrong code: pattern hits only.
        if (inq.Source != HookResolver.ResolveSource.PatternScan || start.Source != HookResolver.ResolveSource.PatternScan)
        {
            StatusMessage = $"drag-start hook skipped (InqDropIconInfo {inq.Detail}, StartDragandDrop {start.Detail}).";
            RynthLog.Compat($"Compat: {StatusMessage}");
            return;
        }

        try
        {
            _inqDropIconInfo = (delegate* unmanaged[Cdecl]<IntPtr, uint*, uint*, uint*, void>)inq.Address;
            delegate* unmanaged[Thiscall]<IntPtr, IntPtr, int, int, byte> detour = &StartDragandDropDetour;
            MinHook.Hook(start.Address, (IntPtr)detour, out _originalStartDrag);
            IsInstalled = true;
            StatusMessage = $"Hooked UIElementManager::StartDragandDrop @ 0x{start.Address.ToInt32():X8}.";
            RynthLog.Info($"Compat: drag-start hook ready - StartDragandDrop=0x{start.Address.ToInt32():X8}, InqDropIconInfo=0x{inq.Address.ToInt32():X8}");
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            RynthLog.Compat($"Compat: drag-start hook failed - {ex.Message}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    private static byte StartDragandDropDetour(IntPtr manager, IntPtr element, int clickX, int clickY)
    {
        try
        {
            if (element != IntPtr.Zero && _inqDropIconInfo != null)
            {
                uint itemId = 0, spellId = 0, flags = 0;
                _inqDropIconInfo(element, &itemId, &spellId, &flags);
                // Spell-bar drags carry a spell id and no item.
                if (itemId != 0)
                {
                    if (_logCount < 20) { _logCount++; RynthLog.UI($"DragDropHooks: AC drag start item=0x{itemId:X8} flags=0x{flags:X}"); }
                    ItemDragBridge.OnNativeDragStart(itemId);
                }
            }
        }
        catch (Exception ex)
        {
            try { RynthLog.Compat($"Compat: drag-start detour error - {ex.GetType().Name}: {ex.Message}"); } catch { }
        }

        var original = (delegate* unmanaged[Thiscall]<IntPtr, IntPtr, int, int, byte>)_originalStartDrag;
        return original(manager, element, clickX, clickY);
    }
}
