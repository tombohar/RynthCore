// ============================================================================
//  RynthCore.Engine - ImGui/UiDropTargets.cs
//
//  AC items dropped on the engine's ImGui panels (2026-10-05). A face that takes
//  drops publishes its body rect every frame it draws (Publish); when the player
//  drags an item out of an AC item list (a pack, the main inventory) and lets go
//  over that rect, Compatibility/UiElementHooks' UIElement::CatchDroppedItem
//  detour asks TryClaim. A claimed drop never reaches AC: the detour answers
//  "not caught" (false), which is AC's own drop-rejected path, so the item stays
//  where it was. The face picks the item up with TryTake on its next draw.
//
//  All on AC's main thread: faces draw in EndScene, the detour runs inside AC's
//  StopDragandDrop. A target not published for StaleMs (panel closed, hidden,
//  between characters) never claims.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace RynthCore.Engine.ImGuiBackend;

internal static class UiDropTargets
{
    private const long StaleMs = 500;

    private sealed class Target
    {
        public required string Key;
        public required string PanelTitle;
        public bool Popped;
        public Vector2 Min, Max;   // ImGui coordinates of the game window's client area (docked)
        public long SeenMs;
        public bool Accepting;
        public bool HasDrop;
        public uint ItemId, SpellId;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);

    private static readonly Dictionary<string, Target> Targets = new(StringComparer.Ordinal);
    private static readonly object Sync = new();

    /// <summary>
    /// The item being dragged in the engine's own Inventory panel (0 = none). Set by
    /// InventoryFace each frame; a drop outside that panel is offered here with TryClaim.
    /// </summary>
    public static uint EngineDragObject
    {
        get => System.Threading.Volatile.Read(ref _engineDrag);
        set => System.Threading.Volatile.Write(ref _engineDrag, value);
    }
    private static uint _engineDrag;

    /// <summary>An item is being dragged, out of AC's UI or the Inventory panel (the faces' "drop here" look). AC thread.</summary>
    public static bool AnyDragInProgress => EngineDragObject != 0 || Compatibility.UiElementHooks.CurrentDragObject != 0;

    /// <summary>
    /// Once per frame from a face's Draw: <paramref name="min"/>..<paramref name="max"/> is the
    /// body in screen coordinates as ImGui sees them (FaceKit.Begin's origin and size).
    /// <paramref name="accepting"/> false: drawn, but a drop now wouldn't be used (it then
    /// goes to AC as usual). Popped-out faces hit-test against their own window.
    /// </summary>
    public static void Publish(string key, string panelTitle, Vector2 min, Vector2 max, bool accepting)
    {
        lock (Sync)
        {
            if (!Targets.TryGetValue(key, out Target? t))
                Targets[key] = t = new Target { Key = key, PanelTitle = panelTitle };
            t.Popped = ImGuiPopOuts.InPopOutFrame;
            t.Min = min;
            t.Max = max;
            t.Accepting = accepting;
            t.SeenMs = Environment.TickCount64;
        }
    }

    /// <summary>
    /// UIElement::CatchDroppedItem detour, AC's main thread: is the cursor over a live,
    /// accepting target? Then that target takes the item and the caller keeps it from AC.
    /// </summary>
    public static bool TryClaim(uint itemId, uint spellId, out string key)
    {
        key = string.Empty;
        if (itemId == 0)
            return false;
        long now = Environment.TickCount64;
        lock (Sync)
        {
            foreach (Target t in Targets.Values)
            {
                if (!t.Accepting || now - t.SeenMs > StaleMs || !IsCursorOver(t))
                    continue;
                t.HasDrop = true;
                t.ItemId = itemId;
                t.SpellId = spellId;
                key = t.Key;
                return true;
            }
        }
        return false;
    }

    /// <summary>The face's draw: an item dropped on it since the last call.</summary>
    public static bool TryTake(string key, out uint itemId)
    {
        itemId = 0;
        lock (Sync)
        {
            if (!Targets.TryGetValue(key, out Target? t) || !t.HasDrop)
                return false;
            t.HasDrop = false;
            itemId = t.ItemId;
            return itemId != 0;
        }
    }

    /// <summary>The cursor is over the target now (the face's "drop here" highlight).</summary>
    public static bool IsCursorOver(string key)
    {
        lock (Sync)
            return Targets.TryGetValue(key, out Target? t) && IsCursorOver(t);
    }

    private static bool IsCursorOver(Target t)
    {
        if (t.Popped)
            return ImGuiPopOuts.IsCursorOver(t.PanelTitle);
        if (!GetCursorPos(out POINT p))
            return false;
        Vector2 local = new Vector2(p.X, p.Y) - ImGuiPopOuts.ClientOriginOnScreen();
        return local.X >= t.Min.X && local.Y >= t.Min.Y && local.X < t.Max.X && local.Y < t.Max.Y;
    }

    public static string Describe()
    {
        long now = Environment.TickCount64;
        lock (Sync)
        {
            if (Targets.Count == 0)
                return "Drop targets: none drawn yet.";
            var parts = new List<string>();
            foreach (Target t in Targets.Values)
                parts.Add($"{t.Key} ({(now - t.SeenMs <= StaleMs ? (t.Accepting ? "live" : "drawn, not accepting") : "not drawn")}{(t.Popped ? ", popped out" : string.Empty)})");
            return "Drop targets: " + string.Join(", ", parts) + ".";
        }
    }
}
