// ============================================================================
//  RynthCore.Engine - ImGui/Panels/MiniRemoteFace.cs
//  The Mini Remote: one window for the quick-use item slots, whoever draws them,
//  and the sections (MiniRemoteFace.Sections.cs): a rail down the left with
//  Remote (this file) and the core sections Char, Map, Skills, Combat, Nav,
//  Inventory, Crafting, Spells, Chat, Friends, Fellowship and Pets, plus any a
//  plugin adds, each showing its engine panels or plugin views and the active
//  servers' options (UI/Sections).
//
//  With RynthAi loaded it hosts RynthAi's Mini Remote (session rates, target,
//  summon, item slots, toggles, bank, rebuff, Translate, Guardian and its
//  Options menu, its Quests and Pets tabs, plus on ILT worlds the RynthILT
//  plugin's tabs Charms, Banking, Gear and Games (Character moved to RynthOracle, 2026.10.5.54), which RynthAi asks
//  RynthILT to draw), drawn by the plugin through
//  RynthPluginRenderEmbed ("miniremote-window"). The window fits itself to what
//  the plugin asks for whenever that changes (another tab, a section shown or
//  hidden, bigger or smaller boxes);
//  a resize by hand holds until then. While this window shows it, RynthAi keeps
//  its own floating copy hidden, so there is one Mini Remote, not two.
//
//  Without RynthAi (not loaded, too old for the surface, not logged in yet,
//  popped out with a RynthAi older than 0.5.38, which can't draw in a pop-out's
//  context, or its embed failed) it is the engine's own grid (an idea from Silentkelpie's
//  rynth PR #1, done as an engine face):
//    slots     the item's icon and carried count; an empty slot shows +
//    click     uses the slot's item (the same object if still carried, else a
//              stack with its name); on an empty slot, puts the selected
//              inventory item there
//    right     Set to the selected item, Clear, More / Fewer slots,
//              Bigger / Smaller boxes
//    ctrl+wheel over the slots: bigger / smaller boxes
//    drop      drag an item from the game's inventory (or the Inventory panel)
//              onto a slot to put it there (UiDropTargets, one target a slot)
//    status    the use waiting for the server, or how the last one ended
//
//  A click never calls AC from the render thread: it posts an InventoryModel
//  Use request, which AC's main thread sends at its next tick behind the
//  client's own item gate (one item action at a time; a click while one waits
//  is refused, not queued). Slots and box size are saved per character
//  (ItemHudStore).
// ============================================================================

using System;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.Plugins;
using RynthCore.Engine.UI.Data;
using RynthCore.Engine.UI.ScriptWindows;
using RynthCore.Engine.UI.Sections;
using static RynthCore.Engine.ImGuiBackend.Panels.FaceKit;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed partial class MiniRemoteFace : IImGuiPanel
{
    public const string Title = "Mini Remote";

    public static void Register()
    {
        ImGuiPanelHost.Register(Title,
            new PanelSpec(new Vector2(184, 150), new Vector2(110, 96), EdgeToEdge: true, GripInBody: true),
            () => new MiniRemoteFace());
        ItemHudStore.EnsureLoadQueued();
    }

    /// <summary>
    /// The RynthAi surface for this window (NUL-terminated for the export): "miniremote-remote"
    /// (the remote alone: its tabs are sections here) from a RynthAi that publishes sections
    /// while the sections rail shows, else "miniremote-window" (the remote with its own tab bar).
    /// With the rail hidden the tab bar is the only way to RynthILT's tabs (Gear, Games).
    /// </summary>
    private static ReadOnlySpan<byte> Surface =>
        RailShown && RynthAiPublishesSections() ? "miniremote-remote\0"u8 : "miniremote-window\0"u8;

    private static bool RynthAiPublishesSections()
    {
        foreach (UI.Sections.SectionManifest m in UI.Sections.SectionRegistry.Current.Manifests)
            if (m.Plugin.Contains("RynthAi", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private const float Gap = 4f, StatusH = 18f;
    /// <summary>Pixels a Ctrl+wheel notch or a Bigger / Smaller menu click changes the box edge by.</summary>
    private const int SlotSizeStep = 4;
    private const string MenuId = "##miniremote_menu";
    private static readonly string[] DropKeys = BuildDropKeys();

    private static string[] BuildDropKeys()
    {
        var keys = new string[ItemHudCharacter.MaxSlots];
        for (int i = 0; i < keys.Length; i++) keys[i] = "MiniRemote#" + i.ToString(CultureInfo.InvariantCulture);
        return keys;
    }

    private readonly HudItemCache _items = new();
    private string _message = "";
    private uint _messageColor;
    private long _messageUntilMs;
    private int _menuSlot = -1;
    private bool _openMenu;

    // Per-slot resolved item and count text, rebuilt when the carried items or the slots change.
    private int _resolvedItemsVersion = -1;
    private ItemHudCharacter? _resolvedFor;
    private uint[] _slotItem = Array.Empty<uint>();
    private string[] _slotCount = Array.Empty<string>();

    // RynthAi hosting: it drew last frame; its export failed this session; the size it last asked for.
    private bool _hosted;
    private bool _embedOff;
    private Vector2 _lastWant;
    // The engine grid: the box size it last fitted the window to (0 = fit on the next frame).
    private int _fittedSlotSize;
    // The window size last requested (logical units), so the same request isn't repeated.
    private Vector2 _requested;

    public void OnShown()
    {
        ItemHudStore.EnsureLoadQueued();
        InventoryModel.Subscribe();
        _lastWant = Vector2.Zero;
        _fittedSlotSize = 0;
        _requested = Vector2.Zero;
    }

    public void OnHidden()
    {
        InventoryModel.Unsubscribe();
        _menuSlot = -1;
        _hosted = false;
    }

    public void Draw()
    {
        float w = Begin(out Vector2 origin, out Vector2 size);
        UI.Sections.SectionRegistry.Snapshot snap = UI.Sections.SectionRegistry.Current;
        TakeRequestedSection(snap);

        // Safety: rail-off + non-Remote draws only that section's body (no hub tabs). Snap
        // back to Remote so undocked/docked Mini Remote keeps Pets/Crafting/Gear/Games/Vaulted.
        if (!RailShown && !IsRemote)
            SelectSection(SectionCatalog.RemoteId);

        // The rail, then everything else to its right (the indent keeps each line there).
        float railW = RailShown ? DrawRail(snap, origin, size) : 0f;
        if (railW > 0)
        {
            ImGuiNET.ImGui.SetCursorScreenPos(origin + new Vector2(6 + railW, 6));
            ImGuiNET.ImGui.Indent(railW);
        }
        float cw = Math.Max(40f, w - railW);
        try
        {
            if (!IsRemote)
            {
                // This window is still the Mini Remote while it shows another section: RynthAi
                // keeps its floating copy hidden while it hears from it each frame.
                if (!_embedOff) PingRynthAiHost();
                DrawSection(snap, cw, origin, size);
            }
            else if (!DrawRynthAi(cw, origin, size)) DrawEngineGrid(cw, origin, size);
        }
        finally
        {
            if (railW > 0) ImGuiNET.ImGui.Unindent(railW);
        }
        End(origin, size);
        DrawMenu();
    }

    /// <summary>The engine's own slot grid (no RynthAi to draw the remote).</summary>
    private void DrawEngineGrid(float w, Vector2 origin, Vector2 size)
    {
        InventoryView view = InventoryModel.Current;
        HudItem[] items = _items.Get(view);
        ItemHudCharacter data = ItemHudStore.Current;
        bool ready = ItemHudStore.Loaded && ItemHudStore.HasCharacter && view.PlayerId != 0;
        Resolve(data, items);

        float haveH = Remaining(origin, size);
        float gridH = Math.Max(data.SlotSize, haveH - StatusH - GripOverlap());
        DrawGrid(data, items, w, gridH, ready);
        DrawStatus(ready);
        FitGrid(data, w, haveH);
    }

    // ── RynthAi's Mini Remote ───────────────────────────────────────────

    /// <summary>
    /// Lets RynthAi draw its Mini Remote here. True when it did (or is mid-layout after
    /// drawing last frame); false means the engine grid draws instead. The plugin draws
    /// nothing when it declines, and the auto-height child it would have filled stays
    /// empty, so the grid can take the space the same frame.
    /// </summary>
    private bool DrawRynthAi(float w, Vector2 origin, Vector2 size)
    {
        if (_embedOff) return false;
        float haveH = Remaining(origin, size);

        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        bool open = ImGuiNET.ImGui.BeginChild("##mr_rynthai", new Vector2(w, 0), ImGuiChildFlags.AutoResizeY,
            ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar);
        ImGuiNET.ImGui.PopStyleVar();
        int result = PluginManager.EmbedNotReady;
        Vector2 want = Vector2.Zero;
        try
        {
            if (open)
            {
                // The plugin's widgets use the default UI font, as in its floating Mini Remote.
                ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui11));
                try { result = PluginManager.RenderEmbed("RynthAi", Surface, new Vector2(w, Math.Max(1f, haveH)), out want); }
                finally { ImGuiNET.ImGui.PopFont(); }
            }
        }
        finally { ImGuiNET.ImGui.EndChild(); }

        if (result == PluginManager.EmbedFailed) _embedOff = true;
        // A clipped child (open == false) draws nothing; keep the last frame's answer so the
        // window doesn't flick to the engine grid for that frame.
        _hosted = open ? result == PluginManager.EmbedDrawn : _hosted && !_embedOff;
        if (!_hosted) { _lastWant = Vector2.Zero; return false; }

        if (result == PluginManager.EmbedDrawn && want.X > 0 && want.Y > 0
            && Vector2.DistanceSquared(want, _lastWant) > 1f)
        {
            _lastWant = want;
            // + the grip's reach, so the plugin's last row isn't under the resize grip.
            FitWindow(new Vector2(want.X, want.Y + GripOverlap()), w, haveH);
        }
        return true;
    }

    /// <summary>"miniremote-host": tells RynthAi this window is open on a section; it draws nothing.</summary>
    private void PingRynthAiHost()
    {
        int result;
        try { result = PluginManager.RenderEmbed("RynthAi", "miniremote-host\0"u8, Vector2.Zero, out _); }
        catch { result = PluginManager.EmbedFailed; }
        if (result == PluginManager.EmbedFailed) _embedOff = true;
    }

    // ── Window fit ──────────────────────────────────────────────────────

    /// <summary>
    /// Sizes the window so the body area (<paramref name="haveW"/> × <paramref name="haveH"/>
    /// now) becomes <paramref name="wantPx"/>; never taller than the screen. Docked only:
    /// popped out the OS window keeps its size, as other faces do (RynthNavArrowFace).
    /// </summary>
    private void FitWindow(Vector2 wantPx, float haveW, float haveH)
    {
        if (ImGuiPopOuts.InPopOutFrame) return;
        float place = ImGuiPanelHost.PlacementScale();
        float extraW = ImGuiNET.ImGui.GetWindowWidth() - haveW;    // anything around the body
        float extraH = ImGuiNET.ImGui.GetWindowHeight() - haveH;
        var want = new Vector2(MathF.Ceiling((wantPx.X + extraW) / place), MathF.Ceiling((wantPx.Y + extraH) / place));
        float screenH = ImGuiNET.ImGui.GetIO().DisplaySize.Y / place;
        if (screenH > 1) want.Y = MathF.Min(want.Y, screenH - 8);
        if (Vector2.DistanceSquared(want, _requested) < 1f) return;
        _requested = want;
        ImGuiPanelHost.RequestSize(Title, want);
    }

    /// <summary>
    /// The engine grid: after the box size changes (and on the first frame shown), fits the
    /// window to the slots at the same number of columns, plus the status line.
    /// </summary>
    private void FitGrid(ItemHudCharacter data, float w, float haveH)
    {
        if (_fittedSlotSize == data.SlotSize || !ItemHudStore.Loaded) return;
        bool first = _fittedSlotSize == 0;
        int oldSlot = first ? data.SlotSize : _fittedSlotSize;
        _fittedSlotSize = data.SlotSize;
        // First show: keep the window the player left (only a box-size change refits it).
        if (first) return;
        int cols = Math.Max(1, (int)((w + Gap) / (oldSlot + Gap)));
        int rows = (data.Slots.Length + cols - 1) / cols;
        float slot = data.SlotSize;
        var body = new Vector2(cols * (slot + Gap) - Gap, rows * (slot + Gap) - Gap + StatusH + GripOverlap());
        FitWindow(body, w, haveH);
    }

    // ── Engine grid ─────────────────────────────────────────────────────

    private void Resolve(ItemHudCharacter data, HudItem[] items)
    {
        if (ReferenceEquals(data, _resolvedFor) && _resolvedItemsVersion == _items.Version) return;
        _resolvedFor = data;
        _resolvedItemsVersion = _items.Version;
        int n = data.Slots.Length;
        _slotItem = new uint[n];
        _slotCount = new string[n];
        for (int i = 0; i < n; i++)
        {
            RemoteSlot? s = data.Slots[i];
            if (s == null) { _slotCount[i] = ""; continue; }
            _slotItem[i] = ItemHudMath.Resolve(items, s);
            long c = s.Name.Length > 0 ? ItemHudMath.Count(items, s.Name) : _slotItem[i] != 0 ? 1 : 0;
            _slotCount[i] = c > 1 ? c.ToString(CultureInfo.InvariantCulture) : c == 0 ? "0" : "";
        }
    }

    private void DrawGrid(ItemHudCharacter data, HudItem[] items, float w, float h, bool ready)
    {
        Vector2 start = ImGuiNET.ImGui.GetCursorScreenPos();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        float slot = data.SlotSize;
        float iconSize = slot - 4f;
        int cols = Math.Max(1, (int)((w + Gap) / (slot + Gap)));
        bool dragging = ready && UiDropTargets.AnyDragInProgress;
        int clicked = -1;

        for (int i = 0; i < data.Slots.Length; i++)
        {
            var p = new Vector2(start.X + (i % cols) * (slot + Gap), start.Y + (i / cols) * (slot + Gap));
            var max = p + new Vector2(slot, slot);
            RemoteSlot? s = data.Slots[i];

            // One drop target a slot, so the item lands where it was let go.
            UiDropTargets.Publish(DropKeys[i], Title, p, max, ready);
            if (UiDropTargets.TryTake(DropKeys[i], out uint dropped) && ready)
                SetSlot(i, dropped, items);

            ImGuiNET.ImGui.SetCursorScreenPos(p);
            ImGuiNET.ImGui.PushID(i);
            if (ImGuiNET.ImGui.InvisibleButton("##slot", new Vector2(slot, slot))) clicked = i;
            bool hot = ImGuiNET.ImGui.IsItemHovered();
            if (ImGuiNET.ImGui.IsItemClicked(ImGuiMouseButton.Right)) { _menuSlot = i; _openMenu = true; }
            ImGuiNET.ImGui.PopID();

            bool dropHot = dragging && UiDropTargets.IsCursorOver(DropKeys[i]);
            dl.AddRectFilled(p, max, dropHot ? Green : hot ? Lighten(BtnFill) : BtnFill, 3);
            dl.AddRect(p, max, dropHot || dragging ? Teal : BtnBord, 3);

            uint id = s != null && i < _slotItem.Length ? _slotItem[i] : 0;
            Vector2 ip = p + new Vector2((slot - iconSize) * 0.5f, (slot - iconSize) * 0.5f);
            if (s == null)
            {
                PhosphorIcons.DrawCentered(dl, ImGuiFonts.Get(UiFont.Ui14), PhosphorIcons.Plus, p, new Vector2(slot, slot), Faded(Mute));
                if (hot) ImGuiNET.ImGui.SetItemTooltip("Empty slot: click to put the selected inventory item here, or drag an item onto it.\nCtrl+wheel: bigger / smaller boxes.");
                continue;
            }
            if (id != 0 && ScriptIcons.TryGet(ScriptIconKind.Object, id, out IntPtr tex))
                dl.AddImage(tex, ip, ip + new Vector2(iconSize, iconSize));
            else
            {
                string initial = s.Name.Length > 0 ? s.Name[..1].ToUpperInvariant() : "?";
                Vector2 ts = ImGuiNET.ImGui.CalcTextSize(initial);
                dl.AddText(p + (new Vector2(slot, slot) - ts) * 0.5f, Faded(Text), initial);
            }
            string count = _slotCount[i];
            if (count.Length > 0)
            {
                Vector2 cs = ImGuiNET.ImGui.CalcTextSize(count);
                Vector2 cp = max - cs - new Vector2(3, 1);
                dl.AddText(cp + new Vector2(1, 1), 0xE0000000, count);
                dl.AddText(cp, id == 0 ? Amber : Text, count);
            }
            if (hot)
                SafeImGui.SetItemTooltip(id == 0
                    ? $"{s.Name}: none carried.\nRight-click for options."
                    : $"{s.Name} ({count switch { "" => "1", _ => count }} carried)\nClick to use. Right-click for options.");
        }
        int rows = (data.Slots.Length + cols - 1) / cols;
        float shownH = Math.Min(h, rows * (slot + Gap));
        ImGuiNET.ImGui.SetCursorScreenPos(start);
        ImGuiNET.ImGui.Dummy(new Vector2(w, shownH));

        // Ctrl+wheel over the slots: bigger / smaller boxes (saved per character).
        ImGuiIOPtr io = ImGuiNET.ImGui.GetIO();
        if (ready && io.KeyCtrl && io.MouseWheel != 0
            && ImGuiNET.ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows)
            && ImGuiNET.ImGui.IsMouseHoveringRect(start, start + new Vector2(w, shownH)))
        {
            ResizeSlots(io.MouseWheel > 0 ? SlotSizeStep : -SlotSizeStep);
            io.MouseWheel = 0f;   // the boxes took it: not the panel's size too
        }

        if (clicked >= 0 && ready) Click(data, items, clicked);
        else if (clicked >= 0) ShowMessage(ItemHudStore.Loaded ? "Waiting for your character..." : "Loading...", Amber);
    }

    /// <summary>Grows (+) or shrinks (-) the boxes by <paramref name="delta"/> pixels, within the saved range.</summary>
    private void ResizeSlots(int delta)
    {
        if (!ItemHudStore.Update(d => d.WithSlotSize(d.SlotSize + delta), out string why))
            ShowMessage(why, Amber);
        else
            ShowMessage($"Box size {ItemHudStore.Current.SlotSize} px.", Mute);
    }

    private void Click(ItemHudCharacter data, HudItem[] items, int i)
    {
        RemoteSlot? s = data.Slots[i];
        if (s == null)
        {
            uint sel = InventoryModel.SelectedItem;
            if (sel != 0) SetSlot(i, sel, items);
            else ShowMessage("Select an inventory item (or drag one here) to fill this slot.", Amber);
            return;
        }
        uint id = i < _slotItem.Length ? _slotItem[i] : 0;
        if (id == 0) { ShowMessage($"No {s.Name} carried.", Amber); return; }
        // Posted to AC's main thread (InventoryModel's tick), never sent from here.
        var request = new InventoryRequest { Kind = InventoryActionKind.Use, ItemId = id, ItemName = s.Name };
        if (!InventoryModel.Request(request, out string why)) ShowMessage(why, Amber);
    }

    private void SetSlot(int i, uint itemId, HudItem[] items)
    {
        string name = ItemCountFace.NameOf(itemId, items);
        if (name.Length == 0) { ShowMessage("That item's name isn't known yet; try again in a moment.", Amber); return; }
        if (!ItemHudMath.TryFind(items, itemId, out _))
            ShowMessage($"{name} isn't in your inventory; the slot uses one once you carry it.", Mute);
        else ShowMessage($"Slot {i + 1}: {name}.", Mute);
        if (!ItemHudStore.Update(d => d.WithSlot(i, new RemoteSlot(itemId, name)), out string why)) ShowMessage(why, Amber);
    }

    // ── Status ──────────────────────────────────────────────────────────

    private void ShowMessage(string text, uint color)
    {
        _message = text;
        _messageColor = color;
        _messageUntilMs = Environment.TickCount64 + 3500;
    }

    private void DrawStatus(bool ready)
    {
        InventoryPending? pending = InventoryModel.Pending;
        InventoryResult? last = InventoryModel.LastResult;
        long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        string text;
        uint color;
        if (_message.Length > 0 && Environment.TickCount64 < _messageUntilMs) { text = _message; color = _messageColor; }
        else if (pending != null) { text = pending.WaitingForGame ? "Waiting for the game: " + pending.Label : pending.Label + "..."; color = Teal; }
        else if (last != null && nowTicks - last.AtTicks < System.Diagnostics.Stopwatch.Frequency * 4) { text = last.Text; color = last.Ok ? Green : Amber; }
        else if (!ready) { text = ItemHudStore.Loaded ? "Waiting for your character..." : "Loading..."; color = Mute; }
        else { text = "Click: use. Right-click: options. Ctrl+wheel: box size."; color = Faded(Mute); }
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        float w = ImGuiNET.ImGui.GetContentRegionAvail().X - BodyGripRoom();
        dl.PushClipRect(p, p + new Vector2(Math.Max(10, w), StatusH), true);
        dl.AddText(p + new Vector2(0, 2), color, text);
        dl.PopClipRect();
        ImGuiNET.ImGui.Dummy(new Vector2(Math.Max(10, w), StatusH));
    }

    private static float BodyGripRoom() => ImGuiPanelHost.BodyGripReserve;

    // ── Right-click menu ────────────────────────────────────────────────

    /// <summary>
    /// The right-click menu: a slot's options (the engine grid), then the sections, the active
    /// servers' items and the rail switch. Opened from a slot or from the rail.
    /// </summary>
    private void DrawMenu()
    {
        if (_openMenu)
        {
            _openMenu = false;
            ImGuiNET.ImGui.OpenPopup(MenuId);
        }
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, ShellBg);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, Teal);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Text);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8, 6));
        bool open = ImGuiNET.ImGui.BeginPopup(MenuId);
        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.PopStyleColor(3);
        if (!open) return;

        try
        {
            if (_menuSlot >= 0 && IsRemote)
            {
                DrawSlotMenu(ItemHudStore.Current, _items.Get(InventoryModel.Current));
                ImGuiNET.ImGui.Separator();
            }
            DrawSectionsMenuItems();
        }
        finally
        {
            ImGuiNET.ImGui.EndPopup();
        }
    }

    private void DrawSlotMenu(ItemHudCharacter data, HudItem[] items)
    {
        int i = _menuSlot;
        RemoteSlot? s = i >= 0 && i < data.Slots.Length ? data.Slots[i] : null;
        Label(i >= 0 ? $"Slot {i + 1}{(s != null ? ": " + s.Name : " (empty)")}" : "Mini Remote", Mute);
        uint sel = InventoryModel.SelectedItem;
        string selName = sel != 0 && ItemHudMath.TryFind(items, sel, out HudItem it) ? it.Name : "";
        if (ImGuiNET.ImGui.MenuItem(selName.Length > 0 ? "Set to " + selName : "Set to the selected item", "", false, i >= 0 && selName.Length > 0))
            SetSlot(i, sel, items);
        if (ImGuiNET.ImGui.MenuItem("Clear slot", "", false, s != null))
            if (!ItemHudStore.Update(d => d.WithSlot(i, null), out string why)) ShowMessage(why, Amber);
        ImGuiNET.ImGui.Separator();
        int n = data.Slots.Length;
        if (ImGuiNET.ImGui.MenuItem($"More slots ({n} now)", "", false, n < ItemHudCharacter.MaxSlots))
            ItemHudStore.Update(d => d.WithSlotCount(d.Slots.Length + 4), out _);
        if (ImGuiNET.ImGui.MenuItem("Fewer slots (drops the last 4)", "", false, n > ItemHudCharacter.MinSlots))
            ItemHudStore.Update(d => d.WithSlotCount(d.Slots.Length - 4), out _);
        ImGuiNET.ImGui.Separator();
        int px = data.SlotSize;
        if (ImGuiNET.ImGui.MenuItem($"Bigger boxes ({px} px now)", "Ctrl+wheel up", false, px < ItemHudCharacter.MaxSlotSize))
            ResizeSlots(SlotSizeStep);
        if (ImGuiNET.ImGui.MenuItem("Smaller boxes", "Ctrl+wheel down", false, px > ItemHudCharacter.MinSlotSize))
            ResizeSlots(-SlotSizeStep);
        if (ImGuiNET.ImGui.MenuItem("Default box size", "", false, px != ItemHudCharacter.DefaultSlotSize))
            ResizeSlots(ItemHudCharacter.DefaultSlotSize - px);
    }
}
