// ============================================================================
//  RynthCore.Engine - ImGui/Panels/ItemsFace.cs
//  ImGui face of the Items panel (UI/Panels/ItemsPanel.cs): weapons (name,
//  element, Del), consumables (name, type, Del), each with "Add Selected",
//  and mana stone tapping (on/off, keep count, min max-mana to tap). A listed
//  weapon the character no longer carries (RynthAi's missingIds) shows in amber
//  with a warning icon and "(missing)", and says what to do in its tooltip.
//
//  Edits change a private copy and save the whole payload (ItemsCommands,
//  pump thread). A newer snapshot replaces the copy; snapshots from before
//  the last save are ignored so an edit never flickers back.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.UI.Data;
using RynthCore.Engine.UI.Panels;
using static RynthCore.Engine.ImGuiBackend.Panels.FaceKit;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed class ItemsFace : IImGuiPanel
{
    public const string Title = "Items";

    public static void Register() => ImGuiPanelHost.Register(Title,
        new PanelSpec(new Vector2(420, 520), new Vector2(340, 260), EdgeToEdge: true),
        () => new ItemsFace());

    private static readonly uint OnBg = RynthTheme.Argb(0xFF104010), OnText = RynthTheme.Argb(0xFF33FF33);

    private ItemsPanel.Payload _data = new();
    private long _seenVersion = -1, _savedAtVersion = -1;
    private readonly Picker _picker = new("##items_pick");

    public void OnShown()
    {
        UiSources.Items.Subscribe();
        UiSources.Items.RequestRefresh();
    }

    public void OnHidden() => UiSources.Items.Unsubscribe();

    public void Draw()
    {
        TakeSnapshot();
        float w = Begin(out Vector2 origin, out Vector2 size);

        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ChildBg, PanelBg);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 0));
        ImGuiNET.ImGui.BeginChild("##items_body", new Vector2(w, Math.Max(60, Remaining(origin, size))));
        float bw = ImGuiNET.ImGui.GetContentRegionAvail().X;

        // Above the lists, which can run long: drops entries for items sold, dropped, used up or traded.
        AddSelected("##clean_missing", PhosphorIcons.Eraser + " Remove Missing", "(weapons and consumables no longer in your inventory)", ItemsCommands.CleanMissing,
            "Removes listed weapons and consumables that are no longer in your inventory.\n" +
            "An entry whose item is gone but with an unlisted item of the same name in your pack\n" +
            "(a new stack of kits, a replacement weapon) is pointed at that item instead.\n" +
            "Does nothing while your inventory is still loading. Also: /ra items clean.");

        // Weapons
        Section("Weapons", bw);
        if (_data.Weapons.Count == 0) Empty("No weapons configured", bw);
        else
        {
            TableHeader("Element", bw);
            // Grouped by kind (the order only matters inside a group); with a single group the
            // sub-headings would say nothing, so they are left out.
            var groups = new List<(string Name, List<int> Idx)>();
            foreach (string kind in KindOrder)
            {
                var idx = new List<int>();
                for (int i = 0; i < _data.Weapons.Count; i++)
                    if (KindGroup(_data.Weapons[i].Kind) == kind) idx.Add(i);
                if (idx.Count > 0) groups.Add((kind, idx));
            }
            bool removed = false;
            int shown = 0;
            foreach (var (groupName, idx) in groups)
            {
                if (groups.Count > 1) KindHeading(groupName, idx.Count, bw);
                for (int k = 0; k < idx.Count; k++)
                {
                    int i = idx[k];
                    ItemsPanel.WeaponEntry wpn = _data.Weapons[i];
                    ImGuiNET.ImGui.PushID("w" + i);
                    int r = Row(wpn.Name, wpn.Element, shown++, bw, _data.MissingIds.Contains(wpn.Id), k > 0, k < idx.Count - 1);
                    ImGuiNET.ImGui.PopID();
                    // Up / down inside its group: the order is RynthAi's preference (the buff wand, the
                    // first weapon of a kind), and it couldn't be changed but by removing and re-adding.
                    if (r == 3 || r == 4)
                    {
                        int j = idx[r == 3 ? k - 1 : k + 1];
                        (_data.Weapons[i], _data.Weapons[j]) = (_data.Weapons[j], _data.Weapons[i]);
                        Save();
                        removed = true;   // the indexes moved: draw the rest next frame
                        break;
                    }
                    if (r == 1)
                    {
                        Vector2 at = ImGuiNET.ImGui.GetIO().MousePos;
                        _picker.Open(at, ItemsCommands.Elements, Array.IndexOf(ItemsCommands.Elements, wpn.Element), e =>
                        {
                            wpn.Element = ItemsCommands.Elements[e];
                            Save();
                        }, 160);
                    }
                    else if (r == 2)
                    {
                        _data.Weapons.RemoveAt(i);
                        Save();
                        removed = true;
                        break;
                    }
                }
                if (removed) break;
            }
        }
        AddSelected("##add_weapon", PhosphorIcons.Plus + " Add Selected Weapon", "(select a weapon in inventory first)", ItemsCommands.AddSelectedWeapon);
        Divider(bw);

        // Consumables
        Section("Consumable Items", bw);
        if (_data.Consumables.Count == 0) Empty("No consumables configured", bw);
        else
        {
            TableHeader("Type", bw);
            for (int i = 0; i < _data.Consumables.Count; i++)
            {
                ItemsPanel.ConsumableEntry c = _data.Consumables[i];
                ImGuiNET.ImGui.PushID("c" + i);
                int r = Row(c.Name, c.Type, i, bw, missing: false, canUp: i > 0, canDown: i < _data.Consumables.Count - 1);
                ImGuiNET.ImGui.PopID();
                if (r == 3 || r == 4)
                {
                    int j = r == 3 ? i - 1 : i + 1;
                    (_data.Consumables[i], _data.Consumables[j]) = (_data.Consumables[j], _data.Consumables[i]);
                    Save();
                    break;
                }
                if (r == 1)
                {
                    Vector2 at = ImGuiNET.ImGui.GetIO().MousePos;
                    _picker.Open(at, ItemsCommands.ConsumableTypes, Array.IndexOf(ItemsCommands.ConsumableTypes, c.Type), t =>
                    {
                        c.Type = ItemsCommands.ConsumableTypes[t];
                        Save();
                    }, 160);
                }
                else if (r == 2)
                {
                    _data.Consumables.RemoveAt(i);
                    Save();
                    break;
                }
            }
        }
        AddSelected("##add_consumable", PhosphorIcons.Plus + " Add Selected Consumable", "(select an item in inventory first)", ItemsCommands.AddSelectedConsumable);
        Divider(bw);

        // Mana stone tapping
        Section("Mana Stone Tapping", bw);
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos() + new Vector2(4, 4);
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        float ty = p.Y + (22 - ImGuiNET.ImGui.GetFontSize()) * 0.5f;
        dl.AddText(new Vector2(p.X, ty), Text, "Enable Tapping");
        float x = p.X + ImGuiNET.ImGui.CalcTextSize("Enable Tapping").X + 6;
        bool on = _data.EnableManaTapping;
        if (Button("##tap", on ? "ON" : "OFF", new Vector2(x, p.Y), new Vector2(50, 22), on ? OnText : Mute, on ? OnBg : BtnFill))
        {
            _data.EnableManaTapping = !on;
            Save();
        }
        x += 66;
        dl.AddText(new Vector2(x, ty), Text, "Keep count:");
        x += ImGuiNET.ImGui.CalcTextSize("Keep count:").X + 6;
        int keep = Spinner("##keep", _data.ManaStoneKeepCount, 1, 999, new Vector2(x, p.Y));
        if (keep != _data.ManaStoneKeepCount) { _data.ManaStoneKeepCount = keep; Save(); }
        NextLine(new Vector2(p.X - 4, p.Y - 4), 30);
        if (_data.EnableManaTapping)
        {
            p = ImGuiNET.ImGui.GetCursorScreenPos() + new Vector2(4, 0);
            dl.AddText(new Vector2(p.X, p.Y + (22 - ImGuiNET.ImGui.GetFontSize()) * 0.5f), Text, "Min MaxMana to tap:");
            float sx = p.X + ImGuiNET.ImGui.CalcTextSize("Min MaxMana to tap:").X + 6;
            int min = Spinner("##minmana", _data.ManaTapMinMana, 100, 99999, new Vector2(sx, p.Y));
            if (min != _data.ManaTapMinMana) { _data.ManaTapMinMana = min; Save(); }
            NextLine(new Vector2(p.X - 4, p.Y), 28);
        }

        ImGuiNET.ImGui.EndChild();
        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.PopStyleColor();
        End(origin, size);
        _picker.Draw();
    }

    private void TakeSnapshot()
    {
        var snap = UiSources.Items.Current;
        if (snap == null || snap.Version == _seenVersion || snap.Version <= _savedAtVersion) return;
        _seenVersion = snap.Version;
        _data = snap.Value.ParseCopy();
    }

    private void Save()
    {
        _savedAtVersion = UiSources.Items.Current?.Version ?? _savedAtVersion;
        ItemsCommands.SetJson(ItemsCommands.Serialize(_data));
    }

    // ── Pieces ───────────────────────────────────────────────────────────

    private static void Section(string title, float w)
    {
        ImGuiNET.ImGui.Dummy(new Vector2(0, 4));
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        ImFontPtr bold = ImGuiFonts.Get(UiFont.UiBold11);
        float h = bold.FontSize + 8;
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        dl.AddRectFilled(p, p + new Vector2(w, h), Selected);
        dl.AddText(bold, bold.FontSize, p + new Vector2(8, 4), Amber, title);
        ImGuiNET.ImGui.Dummy(new Vector2(w, h));
    }

    // The weapon groups, in the order shown. "Other" holds items RynthAi doesn't know right now (not carried).
    private static readonly string[] KindOrder = { "Melee", "Missile", "War Magic", "Void Magic", "Shield", "Ammo", "Other" };

    private static string KindGroup(string kind)
    {
        foreach (string k in KindOrder)
            if (k.Equals(kind, StringComparison.OrdinalIgnoreCase)) return k;
        return "Other";
    }

    private static void KindHeading(string title, int count, float w)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        ImFontPtr f = ImGuiFonts.Get(UiFont.UiBold11);
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        dl.AddRectFilled(p, p + new Vector2(w, 20), PanelBg);
        dl.AddText(f, f.FontSize, p + new Vector2(8, (20 - f.FontSize) * 0.5f), Teal, title + "  (" + count + ")");
        ImGuiNET.ImGui.Dummy(new Vector2(w, 20));
    }

    private static void TableHeader(string column, float w)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        dl.AddRectFilled(p, p + new Vector2(w, 22), Selected);
        ImFontPtr bold = ImGuiFonts.Get(UiFont.UiBold11);
        float ty = p.Y + (22 - bold.FontSize) * 0.5f;
        dl.AddText(bold, bold.FontSize, new Vector2(p.X + 6, ty), Text, "Item Name");
        dl.AddText(bold, bold.FontSize, new Vector2(p.X + w - 146 + 6, ty), Text, column);
        ImGuiNET.ImGui.Dummy(new Vector2(w, 22));
    }

    private static void Empty(string message, float w)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        ImGuiNET.ImGui.GetWindowDrawList().AddRectFilled(p, p + new Vector2(w, 22), RowAlt);
        ImGuiNET.ImGui.GetWindowDrawList().AddText(p + new Vector2(8, (22 - ImGuiNET.ImGui.GetFontSize()) * 0.5f), Mute, message);
        ImGuiNET.ImGui.Dummy(new Vector2(w, 22));
    }

    private const string MissingSuffix = " (missing)";
    // The up / down buttons' width in a row (two 20 px buttons and their gaps).
    private const float MoveW = 46;

    /// <summary>
    /// Name | up | down | [value picker] | Del. Returns 1 when the picker was clicked, 2 for Del,
    /// 3 for up, 4 for down, else 0. Up / down are drawn dim and do nothing at the ends.
    /// <paramref name="missing"/>: the item isn't carried any more (amber, a warning icon,
    /// "(missing)" and a tooltip saying what to do).
    /// </summary>
    private static int Row(string name, string value, int index, float w, bool missing, bool canUp = false, bool canDown = false)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        dl.AddRectFilled(p, p + new Vector2(w, 24), index % 2 == 0 ? RowAlt : PanelBg);
        float nameW = w - 146 - MoveW;
        float ty = p.Y + (24 - ImGuiNET.ImGui.GetFontSize()) * 0.5f;
        dl.PushClipRect(p, p + new Vector2(nameW, 24), true);
        if (missing)
        {
            float x = p.X + 6;
            dl.AddText(new Vector2(x, ty), Amber, PhosphorIcons.Warning);
            x += ImGuiNET.ImGui.CalcTextSize(PhosphorIcons.Warning).X + 4;
            dl.AddText(new Vector2(x, ty), Amber, name);
            x += ImGuiNET.ImGui.CalcTextSize(name).X;
            dl.AddText(new Vector2(x, ty), Amber, MissingSuffix);
        }
        else
        {
            dl.AddText(new Vector2(p.X + 6, ty), Text, name);
        }
        dl.PopClipRect();
        if (missing && ImGuiNET.ImGui.IsWindowHovered() && ImGuiNET.ImGui.IsMouseHoveringRect(p, p + new Vector2(nameW, 24)))
            ImGuiNET.ImGui.SetTooltip("Not in your pack any more - select the item in your inventory and click\n"
                + "Add Selected Weapon again, or remove this entry.");
        int result = 0;
        float bx = p.X + nameW + 2;
        if (IconButton("##up", PhosphorIcons.CaretUp, new Vector2(bx, p.Y + 2), new Vector2(20, 20), canUp ? Teal : Mute, BtnFill) && canUp) result = 3;
        ImGuiNET.ImGui.SetItemTooltip("Move up");
        if (IconButton("##down", PhosphorIcons.CaretDown, new Vector2(bx + 22, p.Y + 2), new Vector2(20, 20), canDown ? Teal : Mute, BtnFill) && canDown) result = 4;
        ImGuiNET.ImGui.SetItemTooltip("Move down");
        bx += MoveW;
        if (Button("##pick", value, new Vector2(bx, p.Y + 2), new Vector2(96, 20), Text, BtnFill)) result = 1;
        if (IconButton("##del", PhosphorIcons.Trash, new Vector2(bx + 100, p.Y + 2), new Vector2(42, 20), Red, BtnFill)) result = 2;
        ImGuiNET.ImGui.SetItemTooltip("Remove");
        NextLine(p, 24);
        return result;
    }

    private static void AddSelected(string id, string label, string hint, Action onClick, string? tooltip = null)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos() + new Vector2(4, 4);
        float bw = ButtonWidth(label) + 4;
        if (Button(id, label, p, new Vector2(bw, 24), Teal, BtnFill)) onClick();
        if (tooltip != null) SafeImGui.SetItemTooltip(tooltip);
        ImFontPtr f9 = ImGuiFonts.Get(UiFont.Ui9);
        ImGuiNET.ImGui.GetWindowDrawList().AddText(f9, f9.FontSize, new Vector2(p.X + bw + 8, p.Y + (24 - f9.FontSize) * 0.5f), Mute, hint);
        NextLine(new Vector2(p.X - 4, p.Y - 4), 32);
    }

    private static void Divider(float w)
    {
        ImGuiNET.ImGui.Dummy(new Vector2(0, 6));
        Separator(w);
    }

    /// <summary>[-] value [+]; returns the new value (same when nothing was clicked).</summary>
    private static int Spinner(string id, int value, int min, int max, Vector2 pos)
    {
        int v = value;
        ImGuiNET.ImGui.PushID(id);
        if (IconButton("##dec", PhosphorIcons.Minus, pos, new Vector2(22, 22), Teal, BtnFill)) v = Math.Max(min, v - 1);
        string text = v.ToString(CultureInfo.InvariantCulture);
        float tw = ImGuiNET.ImGui.CalcTextSize(text).X;
        ImGuiNET.ImGui.GetWindowDrawList().AddText(new Vector2(pos.X + 24 + (60 - tw) * 0.5f, pos.Y + (22 - ImGuiNET.ImGui.GetFontSize()) * 0.5f), Text, text);
        if (IconButton("##inc", PhosphorIcons.Plus, new Vector2(pos.X + 86, pos.Y), new Vector2(22, 22), Teal, BtnFill)) v = Math.Min(max, v + 1);
        ImGuiNET.ImGui.PopID();
        return v;
    }
}
