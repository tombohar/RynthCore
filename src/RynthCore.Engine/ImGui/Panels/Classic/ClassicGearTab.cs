// ============================================================================
//  RynthCore.Engine - ImGui/Panels/Classic/ClassicGearTab.cs
//  The Classic look's Gear tab: RynthAi's Items lists (UiSources.Items, the
//  Items panel's source) in the retail style. Weapons with their element (a
//  drop-down; a weapon no longer carried shows "missing"), consumables with
//  their kind, a remove mark on each row; add the item selected in game as a
//  weapon, a shield or a consumable; drop the missing ones; the shield and
//  mana-tap settings that ride the same list. Saved like the Items panel: the
//  whole payload (ItemsCommands.SetJson), older snapshots ignored after a save
//  so an edit doesn't flicker back. AC's render thread only.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using ImGuiNET;
using RynthCore.Engine.UI;
using RynthCore.Engine.UI.Data;
using RynthCore.Engine.UI.Panels;
using static RynthCore.Engine.ImGuiBackend.Panels.Classic.ClassicKit;

namespace RynthCore.Engine.ImGuiBackend.Panels.Classic;

internal sealed class ClassicGearTab : IClassicTab
{
    public string Name => "Gear";

    // ItemsPanel's choice lists (the plugin's names).
    private static readonly string[] Elements = { "Slash", "Pierce", "Bludgeon", "Fire", "Cold", "Lightning", "Acid", "Nether" };
    private static readonly string[] ConsumableTypes =
        { "General", "Lockpick", "HealthKit", "HealthPotion", "ManaPotion", "StaminaPotion", "ManaStone", "Stamina", "Pet" };

    private const int WeaponRows = 8, ConsumableRows = 6;

    private ItemsPanel.Payload _data = new();
    private long _seenVersion = -1, _savedAtVersion = -1;
    private readonly HashSet<int> _missing = new();
    private int _wFirst, _cFirst;
    private float _wGrab, _cGrab;
    private readonly byte[] _minMana = new byte[16], _keepStones = new byte[16];
    private int _minManaShown = int.MinValue, _keepShown = int.MinValue;
    private bool _typingMana, _typingKeep;

    public void Shown()
    {
        UiSources.Items.Subscribe();
        UiSources.Items.RequestRefresh();
    }

    public void Hidden() => UiSources.Items.Unsubscribe();

    private void TakeSnapshot()
    {
        var snap = UiSources.Items.Current;
        if (snap == null || snap.Version == _seenVersion || snap.Version <= _savedAtVersion || _typingMana || _typingKeep) return;
        _seenVersion = snap.Version;
        _data = snap.Value.ParseCopy();
        _missing.Clear();
        foreach (int id in _data.MissingIds) _missing.Add(id);
    }

    private void Save()
    {
        _savedAtVersion = UiSources.Items.Current?.Version ?? _savedAtVersion;
        ItemsCommands.SetJson(ItemsCommands.Serialize(_data));
    }

    public float Draw(ClassicContext ctx, Vector2 o, float w)
    {
        TakeSnapshot();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        float y = o.Y;
        bool loaded = UiSources.Items.Current != null;

        // ── Weapons ──
        Header(dl, new Vector2(o.X, y), w, "Weapons", loaded ? _data.Weapons.Count + " listed" : "waiting for RynthAi");
        y += Px(23);
        y = WeaponList(ctx, dl, new Vector2(o.X, y), w);

        // ── Consumables ──
        y += Px(8);
        Header(dl, new Vector2(o.X, y), w, "Consumables", loaded ? _data.Consumables.Count + " listed" : null);
        y += Px(23);
        y = ConsumableList(ctx, dl, new Vector2(o.X, y), w);

        // ── Add the selected item ──
        y += Px(6);
        {
            float bw = (w - Px(12)) / 3;
            if (Button("##cg_addw", "Add weapon", new Vector2(o.X, y), bw, ButtonColor.Blue, loaded))
                ItemsCommands.AddSelectedWeapon();
            SafeImGui.SetItemTooltip("Add the item selected in game as a weapon.");
            if (Button("##cg_adds", "Add shield", new Vector2(o.X + bw + Px(6), y), bw, ButtonColor.Blue, loaded))
                ItemsCommands.AddSelectedShield();
            SafeImGui.SetItemTooltip("Add the item selected in game as a shield (worn with one-handed weapons).");
            if (Button("##cg_addc", "Add consumable", new Vector2(o.X + 2 * (bw + Px(6)), y), bw, ButtonColor.Blue, loaded))
                ItemsCommands.AddSelectedConsumable();
            SafeImGui.SetItemTooltip("Add the item selected in game as a consumable (kits, potions, food, stones).");
            y += Px(36);
        }

        // ── Shield, mana tapping, missing ──
        if (loaded)
        {
            float colW = (w - Px(8)) / 2;
            if (_data.AutoEquipShield is bool shield)
            {
                if (Gem("##cg_shield", "Equip a shield", new Vector2(o.X, y), colW, shield))
                {
                    _data.AutoEquipShield = !shield;
                    Save();
                }
                SafeImGui.SetItemTooltip("Wear a listed shield with one-handed weapons.");
            }
            if (Gem("##cg_tap", "Mana tapping", new Vector2(o.X + colW + Px(8), y), colW, _data.EnableManaTapping))
            {
                _data.EnableManaTapping = !_data.EnableManaTapping;
                Save();
            }
            SafeImGui.SetItemTooltip("Use mana stones on your gear's mana when it runs low.");
            y += Px(20);
            NumberField("##cg_minmana", "Tap below mana", _minMana, ref _minManaShown, ref _typingMana, _data.ManaTapMinMana, 0, 100000,
                v => { _data.ManaTapMinMana = v; Save(); }, new Vector2(o.X, y), colW);
            NumberField("##cg_keep", "Keep stones", _keepStones, ref _keepShown, ref _typingKeep, _data.ManaStoneKeepCount, 0, 100,
                v => { _data.ManaStoneKeepCount = v; Save(); }, new Vector2(o.X + colW + Px(8), y), colW);
            y += Px(22);

            y += Px(4);
            if (Plate("##cg_missing", "Clean", new Vector2(o.X, y)))
                ItemsCommands.CleanMissing();
            SafeImGui.SetItemTooltip("Remove listed weapons and consumables that are no longer in your inventory.\n" +
                "An entry whose item is gone but with an unlisted item of the same name in your pack is pointed at that item.");
            int missing = _missing.Count;
            TextMid(dl, o.X + Px(52), y, Px(17), missing > 0 ? missing + " listed item" + (missing == 1 ? " is" : "s are") + " missing" : "Nothing missing",
                missing > 0 ? Down : Faint, 10);
            y += Px(17);
        }
        return y - o.Y;
    }

    // ── Lists ───────────────────────────────────────────────────────────

    private float WeaponList(ClassicContext ctx, ImDrawListPtr dl, Vector2 o, float w)
    {
        List<ItemsPanel.WeaponEntry> list = _data.Weapons;
        float rowH = Px(19), listW = w - Px(16), listH = rowH * WeaponRows;
        float ddW = Px(92), xW = Px(16), kindW = Px(70);
        dl.AddRectFilled(o, o + new Vector2(listW, listH), RynthTheme.Argb(0x8C000000));
        _wFirst = Wheel(o, o + new Vector2(w, listH), _wFirst, list.Count, WeaponRows);
        int remove = -1;
        for (int r = 0; r < WeaponRows && _wFirst + r < list.Count; r++)
        {
            int i = _wFirst + r;
            ItemsPanel.WeaponEntry e = list[i];
            var p = new Vector2(o.X, o.Y + r * rowH);
            if ((i & 1) == 1) dl.AddRectFilled(p, p + new Vector2(listW, rowH), AltRow);
            bool missing = _missing.Contains(e.Id);
            string kind = e.IsShield ? "Shield" : e.Kind.Length > 0 ? e.Kind : "";
            float nameRight = p.X + listW - xW - ddW - kindW - Px(8);
            TextClip(dl, p.X + Px(4), p.Y, rowH, nameRight, e.Name.Length > 0 ? e.Name : "#" + e.Id, missing ? Faint : White, 10);
            TextClip(dl, nameRight + Px(4), p.Y, rowH, nameRight + kindW, missing ? "missing" : kind, missing ? Down : Dim, 10);
            ImGuiNET.ImGui.PushID(i);
            if (!e.IsShield)
            {
                var ddPos = new Vector2(p.X + listW - xW - ddW - Px(2), p.Y);
                int el = Math.Max(0, Array.IndexOf(Elements, e.Element));
                if (Dropdown("##el", Elements[el], ddPos, ddW))
                {
                    int idx = i;
                    ctx.Popup.Open(ddPos + new Vector2(0, Px(19)), ddW, Elements, el, k =>
                    {
                        if (idx < _data.Weapons.Count) { _data.Weapons[idx].Element = Elements[k]; Save(); }
                    });
                }
                SafeImGui.SetItemTooltip("The damage this weapon deals (picks it for monsters weak to it)");
            }
            if (RemoveMark("##x", new Vector2(p.X + listW - xW, p.Y), rowH)) remove = i;
            SafeImGui.SetItemTooltip("Remove " + e.Name + " from the list");
            ImGuiNET.ImGui.PopID();
        }
        if (list.Count == 0)
            TextMid(dl, o.X + Px(6), o.Y, rowH, "No weapons. Select one in game and press Add weapon.", Dim, 10);
        _wFirst = Scrollbar("##cg_wsb", new Vector2(o.X + listW, o.Y), listH, list.Count, WeaponRows, _wFirst, ref _wGrab);
        if (remove >= 0) { list.RemoveAt(remove); Save(); }
        return o.Y + listH;
    }

    private float ConsumableList(ClassicContext ctx, ImDrawListPtr dl, Vector2 o, float w)
    {
        List<ItemsPanel.ConsumableEntry> list = _data.Consumables;
        float rowH = Px(19), listW = w - Px(16), listH = rowH * ConsumableRows;
        float ddW = Px(110), xW = Px(16);
        dl.AddRectFilled(o, o + new Vector2(listW, listH), RynthTheme.Argb(0x8C000000));
        _cFirst = Wheel(o, o + new Vector2(w, listH), _cFirst, list.Count, ConsumableRows);
        int remove = -1;
        for (int r = 0; r < ConsumableRows && _cFirst + r < list.Count; r++)
        {
            int i = _cFirst + r;
            ItemsPanel.ConsumableEntry e = list[i];
            var p = new Vector2(o.X, o.Y + r * rowH);
            if ((i & 1) == 1) dl.AddRectFilled(p, p + new Vector2(listW, rowH), AltRow);
            bool missing = _missing.Contains(e.Id);
            float nameRight = p.X + listW - xW - ddW - Px(8);
            TextClip(dl, p.X + Px(4), p.Y, rowH, nameRight, (e.Name.Length > 0 ? e.Name : "#" + e.Id) + (missing ? "  (missing)" : ""),
                missing ? Faint : White, 10);
            ImGuiNET.ImGui.PushID(1000 + i);
            var ddPos = new Vector2(p.X + listW - xW - ddW - Px(2), p.Y);
            int t = Math.Max(0, Array.IndexOf(ConsumableTypes, e.Type));
            if (Dropdown("##ty", ConsumableTypes[t], ddPos, ddW))
            {
                int idx = i;
                ctx.Popup.Open(ddPos + new Vector2(0, Px(19)), ddW, ConsumableTypes, t, k =>
                {
                    if (idx < _data.Consumables.Count) { _data.Consumables[idx].Type = ConsumableTypes[k]; Save(); }
                });
            }
            SafeImGui.SetItemTooltip("What it's used for");
            if (RemoveMark("##x", new Vector2(p.X + listW - xW, p.Y), rowH)) remove = i;
            SafeImGui.SetItemTooltip("Remove " + e.Name + " from the list");
            ImGuiNET.ImGui.PopID();
        }
        if (list.Count == 0)
            TextMid(dl, o.X + Px(6), o.Y, rowH, "No consumables. Select one in game and press Add consumable.", Dim, 10);
        _cFirst = Scrollbar("##cg_csb", new Vector2(o.X + listW, o.Y), listH, list.Count, ConsumableRows, _cFirst, ref _cGrab);
        if (remove >= 0) { list.RemoveAt(remove); Save(); }
        return o.Y + listH;
    }

    /// <summary>A small gold × that removes its row; true when clicked.</summary>
    private static bool RemoveMark(string id, Vector2 pos, float h)
    {
        float s = Px(16);
        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGuiNET.ImGui.InvisibleButton(id, new Vector2(s, h));
        bool hot = ImGuiNET.ImGui.IsItemHovered();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        var c = new Vector2(pos.X + s * 0.5f, pos.Y + h * 0.5f);
        float r = Px(3.5f);
        uint col = hot ? Down : GoldDim;
        dl.AddLine(c - new Vector2(r, r), c + new Vector2(r, r), col, Math.Max(1, Px(1.5f)));
        dl.AddLine(c + new Vector2(-r, r), c + new Vector2(r, -r), col, Math.Max(1, Px(1.5f)));
        return clicked;
    }

    private static void NumberField(string id, string label, byte[] buffer, ref int shown, ref bool typing, int value, int min, int max,
        Action<int> commit, Vector2 pos, float w)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        float rh = Px(22), boxW = Px(56);
        TextClip(dl, pos.X + Px(3), pos.Y, rh, pos.X + w - boxW - Px(6), label, Text, 10);
        if (!typing && shown != value) { Write(buffer, value); shown = value; }
        bool done = NumberBox(id, buffer, new Vector2(pos.X + w - boxW, pos.Y + (rh - Px(17)) * 0.5f), boxW, out bool active);
        typing = active;
        if (!done) return;
        if (ClassicSettings.TryParse(buffer, out double v))
        {
            int clamped = (int)Math.Clamp(Math.Round(v), min, max);
            Write(buffer, clamped);
            shown = clamped;
            if (clamped != value) commit(clamped);
        }
        else { Write(buffer, value); shown = value; }
    }

    private static void Write(byte[] buffer, int value)
    {
        string text = value.ToString(CultureInfo.InvariantCulture);
        int n = Encoding.UTF8.GetBytes(text, 0, text.Length, buffer, 0);
        buffer[Math.Min(n, buffer.Length - 1)] = 0;
    }
}
