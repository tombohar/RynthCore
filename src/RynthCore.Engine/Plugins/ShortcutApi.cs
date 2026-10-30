// ============================================================================
//  RynthCore.Engine - Plugins/ShortcutApi.cs
//  Plugin API v90 (2026-10-22), for an item bar that stands in for AC's
//  toolbar (RynthDefective): the player's shortcut slots (AcShortcuts), their
//  keys in AC's keymap (AcKeymap) and changing them so every client follows
//  (AcKeymapLive, with the engine's "press a key" capture), and item / spell
//  pictures for ImGui windows (the script windows' icon cache, ScriptIcons).
//  Nothing here throws across the boundary.
// ============================================================================

using System;
using System.Runtime.InteropServices;
using System.Text;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.ImGuiBackend;
using RynthCore.Engine.UI.ScriptWindows;

namespace RynthCore.Engine.Plugins;

internal static unsafe class ShortcutApi
{
    /// <summary>
    /// v90 GetShortcuts: the 18 slots as last read (object id, 0 = empty; spell id) into the
    /// caller's arrays. Returns the slot count (18), or 0 out of the world. Any thread.
    /// </summary>
    public static int GetShortcuts(uint* objectIds, uint* spellIds, int max)
    {
        try
        {
            AcShortcuts.Slot[] slots = AcShortcuts.Slots;
            if (slots.Length == 0) return 0;
            int n = Math.Min(Math.Max(max, 0), slots.Length);
            for (int i = 0; i < n; i++)
            {
                if (objectIds != null) objectIds[i] = slots[i].ObjectId;
                if (spellIds != null) spellIds[i] = slots[i].SpellId;
            }
            return slots.Length;
        }
        catch { return 0; }
    }

    /// <summary>
    /// v90 ShortcutAction: op 0 use slot (as its key does), 1 put object <paramref name="objectId"/>
    /// in it, 2 empty it; slot 0-17. Done on AC's game thread through the retail toolbar's own
    /// functions (saved to the server). 1 queued, 0 refused. Any thread.
    /// </summary>
    public static int ShortcutAction(int op, int slot, uint objectId)
    {
        try
        {
            if (op is < 0 or > 2) return 0;
            return AcShortcuts.Enqueue((AcShortcuts.Op)op, slot, objectId) ? 1 : 0;
        }
        catch { return 0; }
    }

    /// <summary>
    /// v90 GetActionKeys: AC's keys for keymap action <paramref name="action"/> ("UseQuickSlot_1")
    /// as text ("1, Ctrl+1"), UTF-8 into <paramref name="buffer"/> (NUL-terminated when it fits).
    /// Returns the bytes the text needs ("" = 0: unbound), -1 for a bad call. Any thread.
    /// </summary>
    public static int GetActionKeys(byte* action, byte* buffer, int length)
    {
        try
        {
            if (action == null) return -1;
            string? name = Marshal.PtrToStringUTF8((IntPtr)action);
            if (string.IsNullOrWhiteSpace(name) || name.Length > 64) return -1;
            name = name.Trim();
            return Write(AcKeymapLive.IsCommand(name) ? AcKeymapLive.CommandKeysText(name) : AcKeymap.KeysText(name), buffer, length);
        }
        catch { return -1; }
    }

    /// <summary>
    /// v90 AcPanel: AC's own panel <paramref name="id"/> (the id AC's panel notice uses: 7 the pack, 12
    /// fellowship and allegiance...): op 0 asks (1 open, 0 closed, -1 no such panel; AC's main thread),
    /// 1 toggles, 2 opens, 3 closes it (1 queued for AC's game thread, -1 refused). See AcPanels.
    /// </summary>
    public static int AcPanel(int id, int op)
    {
        try
        {
            if (id <= 0 || id > 0xFFFF) return -1;
            if (op == 0) return MainThreadGuard.IsOnMainThread() ? AcPanels.State((uint)id) : -1;
            AcPanels.Op what = op switch { 1 => AcPanels.Op.Toggle, 2 => AcPanels.Op.Open, 3 => AcPanels.Op.Close, _ => (AcPanels.Op)(-1) };
            if ((int)what < 0) return -1;
            return AcPanels.Enqueue((uint)id, what) ? 1 : -1;
        }
        catch { return -1; }
    }

    /// <summary>
    /// v90 DropTarget: the plugin window <paramref name="window"/> takes items dragged out of AC's UI
    /// (a pack, the inventory) and let go over x0,y0-x1,y1 (ImGui screen coordinates, this frame's):
    /// published every frame it draws, as the engine's own panels do (UiDropTargets), flags bit 0 =
    /// a drop would be used now. A claimed drop never reaches AC (the item stays where it was, as on
    /// the retail toolbar). Returns the object dropped since the last call (0 = none); *dragging = 1
    /// while an item is being dragged (for a "drop here" look). AC's main thread, in the window's draw.
    /// </summary>
    public static uint DropTarget(byte* window, float x0, float y0, float x1, float y1, int flags, int* dragging)
    {
        try
        {
            if (dragging != null) *dragging = 0;
            if (window == null || !MainThreadGuard.IsOnMainThread()) return 0;
            string? title = Marshal.PtrToStringUTF8((IntPtr)window);
            if (string.IsNullOrWhiteSpace(title) || title.Length > 64) return 0;
            string key = "plugin:" + title;
            UiDropTargets.Publish(key, title, new System.Numerics.Vector2(x0, y0), new System.Numerics.Vector2(x1, y1), (flags & 1) != 0);
            if (dragging != null) *dragging = UiDropTargets.AnyDragInProgress ? 1 : 0;
            return UiDropTargets.TryTake(key, out uint item) ? item : 0;
        }
        catch { return 0; }
    }

    /// <summary>UTF-8 into the caller's buffer (NUL-terminated when it fits); the bytes the text needs.</summary>
    private static int Write(string s, byte* buffer, int length)
    {
        byte[] text = Encoding.UTF8.GetBytes(s);
        if (buffer != null && length > 0)
        {
            int n = Math.Min(text.Length, length - 1);
            for (int i = 0; i < n; i++) buffer[i] = text[i];
            buffer[n] = 0;
        }
        return text.Length;
    }

    /// <summary>
    /// v90 KeyCapture: op 0 starts waiting for the next key the player presses (it never reaches AC; Esc
    /// cancels), 1 asks: 0 still waiting, 1 pressed (its name, "Ctrl+F5", in the buffer), 2 cancelled or
    /// timed out (why, if anything, in the buffer); 2 stops waiting. AC's main thread (the render).
    /// </summary>
    public static int KeyCapture(int op, byte* buffer, int length)
    {
        try
        {
            switch (op)
            {
                case 0:
                    Hotkeys.HotkeyService.BeginCapture();
                    return 0;
                case 2:
                    if (Hotkeys.HotkeyService.Capturing) Hotkeys.HotkeyService.CancelCapture();
                    Hotkeys.HotkeyService.TryTakeCapture(out _);
                    return 0;
                case 1:
                    string note = Hotkeys.HotkeyService.CaptureNote;   // a key that can't be one ("Numpad Enter..."): still waiting
                    if (!Hotkeys.HotkeyService.TryTakeCapture(out Hotkeys.KeyCombo? combo)) { Write(note, buffer, length); return 0; }
                    if (combo is { } c) { Write(c.ToString(), buffer, length); return 1; }
                    Write(note, buffer, length);
                    return 2;
                default:
                    return -1;
            }
        }
        catch { return -1; }
    }

    /// <summary>
    /// v90 SetActionKey: for a keymap action ("UseQuickSlot_5") or an engine command ("/rdef use 2"), key
    /// "Ctrl+F5" (or a key as GetActionKeys wrote it, to remove): op 0 checks (0 free, 1 it would be taken
    /// from what the message names, -1 it can't be, why in the message), 1 binds it (it comes off
    /// everything else; every client follows within a second), 2 removes it (an engine command's: its hotkey
    /// only, as ever), 3 binds an engine command's key as its hotkey alone (off RynthCore's other hotkeys; AC's
    /// keymap untouched, nothing reloaded anywhere; 2026-10-27, an engine before that refuses op 3 as unknown).
    /// 1-3 return 1 queued (done on AC's game thread; the result goes to chat), -1 refused (why in the
    /// message). Any thread.
    /// </summary>
    public static int SetActionKey(byte* action, byte* key, int op, byte* message, int length)
    {
        try
        {
            if (action == null || key == null) return -1;
            string a = Marshal.PtrToStringUTF8((IntPtr)action) ?? string.Empty;
            string k = Marshal.PtrToStringUTF8((IntPtr)key) ?? string.Empty;
            string msg;
            int result;
            if (op == 0) result = AcKeymapLive.Check(a, k, out msg);
            else if (op is 1 or 2 or 3) result = AcKeymapLive.Enqueue(a, k, (AcKeymapLive.Op)op, out msg) ? 1 : -1;
            else { result = -1; msg = "Unknown op."; }
            Write(msg, message, length);
            return result;
        }
        catch { return -1; }
    }

    /// <summary>
    /// v90 GetIconTexture: kind 0 an icon (0x06) id, 1 an object (its icon with underlay and
    /// overlay), 2 a spell. An ImGui texture id for ImGui.Image, or 0 while it is being made (ask
    /// again next frame). AC's render thread, inside a docked window's draw.
    /// </summary>
    public static IntPtr GetIconTexture(int kind, uint id)
    {
        try
        {
            if (kind is < 0 or > 2 || id == 0 || !MainThreadGuard.IsOnMainThread() || ImGuiPopOuts.InPopOutFrame) return IntPtr.Zero;
            return ScriptIcons.TryGet((ScriptIconKind)kind, id, out IntPtr texture) ? texture : IntPtr.Zero;
        }
        catch { return IntPtr.Zero; }
    }
}
