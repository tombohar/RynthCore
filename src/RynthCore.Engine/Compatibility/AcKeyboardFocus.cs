// ============================================================================
//  RynthCore.Engine - Compatibility/AcKeyboardFocus.cs
//
//  "Is the player typing into one of AC's own text boxes?" - for the user
//  hotkeys (Hotkeys/HotkeyService.cs), which must never fire while the chat
//  entry (or a quantity box, a name field...) has the keyboard.
//
//  Evidence (AC client decompile, C:\Projects\GhidraTools\decompiled):
//    UIElementManager::SetFocusElement (0x0045B970) keeps the element with the
//      keyboard focus at manager +0x328 (tells the old one it lost focus with
//      element message 0x2F / vtable +0xB4, the new one with 0x2F / +0xB0).
//    UIElementManager::KeyPressEvent (0x0045C3E0) and ::ActionHandler (0x0045CD80)
//      offer every key to +0x328 first (then +0x32C) and only broadcast it to the
//      game (movement, shortcuts) when that element doesn't take it: a non-zero
//      +0x328 is the element typed keys go to.
//    UIElement::TakeFocus (0x004602C0) sets it; UIElement::Deactivate (0x00461E30)
//      and UIElement::RelinquishFocus (0x00461EC0) clear it to 0.
//  Only the pointer is read (never dereferenced on the key path). The manager is
//  UiElementHooks.Manager (the tooltip hooks' `this`, as AcMouseOver uses); no
//  manager = unknown, and the hotkeys then fire only for Ctrl/Alt/F-key combos.
//
//  Threads: AC's main thread (the WndProc and EndScene run there).
// ============================================================================

using System;

namespace RynthCore.Engine.Compatibility;

internal static unsafe class AcKeyboardFocus
{
    private const int MgrFocusElement = 0x328;
    private const int ElementIdOffset = 0x2E4;

    /// <summary>The element with AC's keyboard focus (0 = none), or null when AC's UI manager isn't readable.</summary>
    public static IntPtr? FocusElement()
    {
        try
        {
            if (!MainThreadGuard.IsOnMainThread()) return null;
            IntPtr mgr = UiElementHooks.Manager;
            if (mgr == IntPtr.Zero || !ClientObjectHooks.IsReadableSpan(mgr + MgrFocusElement, 4)) return null;
            return *(IntPtr*)(mgr + MgrFocusElement);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True: an AC text box has the keyboard; false: none; null: unknown.</summary>
    public static bool? HasTextFocus()
    {
        IntPtr? e = FocusElement();
        return e == null ? null : e.Value != IntPtr.Zero;
    }

    /// <summary>For /rc hotkey status: AC's focus element and its id.</summary>
    public static string Describe()
    {
        IntPtr? e = FocusElement();
        if (e == null)
            return UiElementHooks.Manager == IntPtr.Zero
                ? "AC keyboard focus: unknown (AC's UI manager not seen yet: the tooltip hooks are off or haven't run)."
                : "AC keyboard focus: unknown (not readable).";
        if (e.Value == IntPtr.Zero) return "AC keyboard focus: none (keys go to the game).";
        uint id = 0;
        try
        {
            if (ClientObjectHooks.IsReadableSpan(e.Value + ElementIdOffset, 4)) id = *(uint*)(e.Value + ElementIdOffset);
        }
        catch { }
        return $"AC keyboard focus: element 0x{e.Value.ToInt64():X8} id 0x{id:X8} (typing: hotkeys held back).";
    }
}
