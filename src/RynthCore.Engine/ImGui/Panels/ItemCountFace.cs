// ============================================================================
//  RynthCore.Engine - ImGui/Panels/ItemCountFace.cs
//  The Item Count panel: chosen items by name, each with how many the character
//  carries (every stack with that name, whole stacks summed; worn ones too, so
//  wielded arrows count). Engine only, ImGui only. (An idea from Silentkelpie's
//  rynth PR #1, done as an engine face.)
//
//    add row   a text box (Enter adds), Add, and "add the selected item"
//    rows      icon, name, count (amber when none carried); X removes
//    drop      drag an item from the game's inventory (or the Inventory panel)
//              onto the panel to add its name (UiDropTargets)
//
//  Data: InventoryModel's published view (the face subscribes while open) and
//  ItemHudStore (item_huds.txt, per character). Draw reads snapshots and posts
//  edits; no AC calls except a dropped item's name when it isn't carried (the
//  Loot Editor's way); no file I/O; counts are worked out when the view or the
//  list changes, not per frame.
// ============================================================================

using System;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.UI.Data;
using RynthCore.Engine.UI.ScriptWindows;
using static RynthCore.Engine.ImGuiBackend.Panels.FaceKit;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed class ItemCountFace : IImGuiPanel
{
    public const string Title = "Item Count";
    private const string DropKey = "ItemCount";

    public static void Register()
    {
        ImGuiPanelHost.Register(Title,
            new PanelSpec(new Vector2(250, 230), new Vector2(170, 110), EdgeToEdge: true, GripInBody: true),
            () => new ItemCountFace());
        ItemHudStore.EnsureLoadQueued();
    }

    private const float RowH = 22f, Icon = 18f, XW = 18f;

    private readonly byte[] _input = new byte[ItemHudCharacter.MaxNameLength + 1];
    private readonly HudItemCache _items = new();
    private string _message = "";
    private uint _messageColor;
    private long _messageUntilMs;

    // Counts for the shown list, rebuilt when the carried items or the list change.
    private int _countsItemsVersion = -1;
    private ItemHudCharacter? _countsFor;
    private string[] _countText = Array.Empty<string>();
    private bool[] _none = Array.Empty<bool>();
    private uint[] _iconId = Array.Empty<uint>();

    public void OnShown()
    {
        ItemHudStore.EnsureLoadQueued();
        InventoryModel.Subscribe();
    }

    public void OnHidden() => InventoryModel.Unsubscribe();

    public void Draw()
    {
        float w = Begin(out Vector2 origin, out Vector2 size);
        InventoryView view = InventoryModel.Current;
        HudItem[] items = _items.Get(view);
        ItemHudCharacter data = ItemHudStore.Current;

        DrawAddRow(w, view, items);
        DrawStatus(view);
        float bodyH = Math.Max(30, Remaining(origin, size) - GripOverlap());
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ChildBg, PanelBg);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 0));
        ImGuiNET.ImGui.BeginChild("##itemcount_rows", new Vector2(w, bodyH));
        DrawRows(data, items);
        ImGuiNET.ImGui.EndChild();
        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.PopStyleColor();
        End(origin, size);

        AcceptDrops(origin, size, items);
    }

    // ── Add row ─────────────────────────────────────────────────────────

    private void DrawAddRow(float w, InventoryView view, HudItem[] items)
    {
        Vector2 start = ImGuiNET.ImGui.GetCursorScreenPos();
        const float h = 22, iconW = 22;
        string addLabel = PhosphorIcons.Plus + " Add";
        float addW = ButtonWidth(addLabel);
        float boxW = Math.Max(50, w - addW - iconW - 8);
        bool ready = ItemHudStore.Loaded && ItemHudStore.HasCharacter;

        bool enter = TextBox("##itemcount_input", _input, start, boxW, "Item name", out _, ImGuiInputTextFlags.EnterReturnsTrue, h);
        bool add = Button("##itemcount_add", addLabel, new Vector2(start.X + boxW + 4, start.Y), new Vector2(addW, h), Text, BtnFill, enabled: ready);
        ImGuiNET.ImGui.SetItemTooltip("Count this item (Enter in the box does the same). The name must match the item's name; any case.");
        if (enter || add)
        {
            string text = Utf8(_input);
            if (Add(text)) WriteUtf8(_input, "");
        }

        uint sel = InventoryModel.SelectedItem;
        bool haveSel = ready && sel != 0 && ItemHudMath.TryFind(items, sel, out _);
        if (IconButton("##itemcount_sel", PhosphorIcons.Crosshair, new Vector2(start.X + boxW + 4 + addW + 4, start.Y), new Vector2(iconW, h), Text, BtnFill, enabled: haveSel))
        {
            if (ItemHudMath.TryFind(items, sel, out HudItem it)) Add(it.Name);
        }
        SafeImGui.SetItemTooltip(haveSel ? "Count the selected item" : "Select an item in your inventory to count it (or drag one onto this panel)");
        NextLine(start, h + 4);
    }

    private bool Add(string name)
    {
        string msg = "";
        bool ok = ItemHudStore.Update(d => d.WithCount(name, out msg), out string why);
        if (!ok) { ShowMessage(why, Amber); return false; }
        bool added = msg.StartsWith("Counting", StringComparison.Ordinal);
        ShowMessage(msg, added ? Mute : Amber);
        return added;
    }

    private void ShowMessage(string text, uint color)
    {
        _message = text;
        _messageColor = color;
        _messageUntilMs = Environment.TickCount64 + 3000;
    }

    private void DrawStatus(InventoryView view)
    {
        if (_message.Length > 0 && Environment.TickCount64 < _messageUntilMs) { Label(_message, _messageColor); return; }
        if (!ItemHudStore.Loaded) Label("Loading...", Mute);
        else if (!ItemHudStore.HasCharacter || view.PlayerId == 0) Label("Waiting for your character...", Mute);
        else Label("Carried (all stacks):", Faded(Mute));
    }

    // ── Rows ────────────────────────────────────────────────────────────

    private void RefreshCounts(ItemHudCharacter data, HudItem[] items)
    {
        if (ReferenceEquals(data, _countsFor) && _countsItemsVersion == _items.Version) return;
        _countsFor = data;
        _countsItemsVersion = _items.Version;
        int n = data.Counts.Length;
        _countText = new string[n];
        _none = new bool[n];
        _iconId = new uint[n];
        for (int i = 0; i < n; i++)
        {
            long c = ItemHudMath.Count(items, data.Counts[i]);
            _countText[i] = c.ToString("N0", CultureInfo.InvariantCulture);
            _none[i] = c == 0;
            _iconId[i] = _items.IdOf(data.Counts[i]);
        }
    }

    private void DrawRows(ItemHudCharacter data, HudItem[] items)
    {
        float bw = ImGuiNET.ImGui.GetContentRegionAvail().X;
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        if (data.Counts.Length == 0)
        {
            Vector2 at = ImGuiNET.ImGui.GetCursorScreenPos() + new Vector2(6, 6);
            dl.AddText(at, Faded(Mute), "Nothing counted yet: type a name above,\nor drag an item here.");
            return;
        }
        RefreshCounts(data, items);

        Vector2 start = ImGuiNET.ImGui.GetCursorScreenPos();
        float fontH = ImGuiNET.ImGui.GetFontSize();
        int remove = -1;
        for (int i = 0; i < data.Counts.Length; i++)
        {
            var p = new Vector2(start.X, start.Y + i * RowH);
            ImGuiNET.ImGui.SetCursorScreenPos(p);
            ImGuiNET.ImGui.PushID(i);
            ImGuiNET.ImGui.InvisibleButton("##row", new Vector2(bw - XW, RowH));
            bool hot = ImGuiNET.ImGui.IsItemHovered();
            if (hot) SafeImGui.SetItemTooltip($"{data.Counts[i]}: {_countText[i]} carried");
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X + bw - XW, p.Y));
            if (ImGuiNET.ImGui.InvisibleButton("##x", new Vector2(XW, RowH))) remove = i;
            bool hotX = ImGuiNET.ImGui.IsItemHovered();
            if (hotX) ImGuiNET.ImGui.SetItemTooltip("Stop counting");
            ImGuiNET.ImGui.PopID();

            uint bg = hot ? Lighten(PanelBg) : (i & 1) == 1 ? RowAlt : 0;
            if (bg != 0) dl.AddRectFilled(p, p + new Vector2(bw, RowH), bg);
            Vector2 ip = new(p.X + 3, p.Y + (RowH - Icon) * 0.5f);
            if (_iconId[i] != 0 && ScriptIcons.TryGet(ScriptIconKind.Object, _iconId[i], out IntPtr tex))
                dl.AddImage(tex, ip, ip + new Vector2(Icon, Icon));
            else
                dl.AddRect(ip, ip + new Vector2(Icon, Icon), Faded(BtnBord), 2);

            float ty = p.Y + (RowH - fontH) * 0.5f;
            Vector2 cs = ImGuiNET.ImGui.CalcTextSize(_countText[i]);
            float countX = p.X + bw - XW - 6 - cs.X;
            dl.PushClipRect(new Vector2(p.X + Icon + 8, p.Y), new Vector2(countX - 6, p.Y + RowH), true);
            dl.AddText(new Vector2(p.X + Icon + 8, ty), _none[i] ? Faded(Text) : Text, data.Counts[i]);
            dl.PopClipRect();
            dl.AddText(new Vector2(countX, ty), _none[i] ? Amber : Teal, _countText[i]);
            PhosphorIcons.DrawCentered(dl, ImGuiFonts.Get(UiFont.Ui11), PhosphorIcons.X, new Vector2(p.X + bw - XW, p.Y), new Vector2(XW, RowH),
                hotX ? Red : Faded(Mute));
        }
        ImGuiNET.ImGui.SetCursorScreenPos(start);
        ImGuiNET.ImGui.Dummy(new Vector2(bw, data.Counts.Length * RowH));

        if (remove >= 0)
        {
            string name = data.Counts[remove];
            if (ItemHudStore.Update(d => d.WithoutCount(d.IndexOfCount(name)), out string why))
                ShowMessage($"Stopped counting \"{name}\".", Mute);
            else ShowMessage(why, Amber);
        }
    }

    // ── Drops (an item dragged from the game's inventory or the Inventory panel) ──

    private void AcceptDrops(Vector2 origin, Vector2 size, HudItem[] items)
    {
        bool can = ItemHudStore.Loaded && ItemHudStore.HasCharacter;
        UiDropTargets.Publish(DropKey, Title, origin, origin + size, can);
        if (UiDropTargets.TryTake(DropKey, out uint itemId) && can)
        {
            string name = NameOf(itemId, items);
            if (name.Length > 0) Add(name);
            else ShowMessage("That item's name isn't known yet; try again in a moment.", Amber);
        }

        if (!can || !UiDropTargets.AnyDragInProgress) return;
        bool over = UiDropTargets.IsCursorOver(DropKey);
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        dl.AddRect(origin + new Vector2(1, 1), origin + size - new Vector2(1, 1), over ? Green : Teal, 4, ImDrawFlags.None, over ? 3f : 1.5f);
    }

    /// <summary>A dropped item's name: from the carried items, else the client's (a corpse's or chest's item).</summary>
    internal static string NameOf(uint itemId, HudItem[] items)
    {
        if (ItemHudMath.TryFind(items, itemId, out HudItem it) && it.Name.Length > 0) return it.Name;
        if (ClientObjectHooks.TryGetSnapshotName(itemId, out string n) && n.Length > 0) return n;
        return ClientObjectHooks.TryGetObjectName(itemId, out n) ? n ?? string.Empty : string.Empty;
    }
}
