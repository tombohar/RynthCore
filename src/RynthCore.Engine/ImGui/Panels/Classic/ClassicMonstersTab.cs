// ============================================================================
//  RynthCore.Engine - ImGui/Panels/Classic/ClassicMonstersTab.cs
//  The Classic look's Monsters tab: RynthAi's monster rules (UiSources.
//  MonsterRules, the Damage panel's source) in the retail style. The list
//  (Default first): monster, weapon, damage, priority, debuff marks; a click
//  selects. The selected rule: what RynthAi has learned about it (max health,
//  armour, weakest damage), its weapon, damage and priority, the six debuffs
//  and the six spell shapes as gems. Add target makes a rule for the monster
//  selected in game; Remove drops a rule (it follows Default again). Edits go
//  through DamageCommands.EditRule, which re-reads the latest rules on the
//  pump thread first, like the Damage panel; the rest (off hand, pet, kite,
//  attack power, match expressions) is on the Damage panel (the plate).
//  AC's render thread only.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.UI;
using RynthCore.Engine.UI.Data;
using RynthCore.Engine.UI.Panels;
using static RynthCore.Engine.ImGuiBackend.Panels.Classic.ClassicKit;

namespace RynthCore.Engine.ImGuiBackend.Panels.Classic;

internal sealed class ClassicMonstersTab : IClassicTab
{
    public string Name => "Monsters";

    private const int VisibleRows = 10;

    private string _selName = "Default";
    private int _first;
    private float _grab;
    private readonly byte[] _priority = new byte[8];
    private int _priorityShown = int.MinValue;
    private string _priorityFor = string.Empty;
    private bool _typingPriority;

    // Weapon picker items, rebuilt when the rules snapshot changes.
    private MonsterRulesSnapshot? _itemsFor;
    private string[] _weaponNames = Array.Empty<string>();
    private int[] _weaponIds = Array.Empty<int>();
    private readonly Dictionary<int, string> _weaponById = new();

    public void Shown()
    {
        UiSources.MonsterRules.Subscribe();
        UiSources.MonsterRules.RequestRefresh();
    }

    public void Hidden() => UiSources.MonsterRules.Unsubscribe();

    public float Draw(ClassicContext ctx, Vector2 o, float w)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        MonsterRulesSnapshot? snap = UiSources.MonsterRules.Current?.Value;
        MonstersPanel.Payload? data = snap?.Parsed;
        List<MonstersPanel.Rule> rules = data?.Rules ?? new List<MonstersPanel.Rule>();
        if (snap != null && !ReferenceEquals(snap, _itemsFor)) BuildWeapons(snap);
        float y = o.Y;

        Header(dl, new Vector2(o.X, y), w, "Monster rules",
            data == null ? "waiting for RynthAi" : rules.Count + " rules · Default catches the rest");
        y += Px(23);

        // Columns and the list.
        float listW = w - Px(16);
        float cWeapon = Px(118), cDmg = Px(56), cPri = Px(26), cMarks = Px(40);
        float cName = listW - cWeapon - cDmg - cPri - cMarks - Px(8);
        {
            float hh = Px(14);
            float x = o.X + Px(4);
            TextMid(dl, x, y, hh, "Monster", Dim, 10); x += cName;
            TextMid(dl, x, y, hh, "Weapon", Dim, 10); x += cWeapon;
            TextMid(dl, x, y, hh, "Damage", Dim, 10); x += cDmg;
            TextMid(dl, x, y, hh, "Pri", Dim, 10); x += cPri;
            TextMid(dl, x, y, hh, "Debuffs", Dim, 10);
            y += hh;
        }
        float rowH = Px(17), listH = rowH * VisibleRows;
        var listMin = new Vector2(o.X, y);
        dl.AddRectFilled(listMin, listMin + new Vector2(listW, listH), RynthTheme.Argb(0x8C000000));
        _first = Wheel(listMin, listMin + new Vector2(w, listH), _first, rules.Count, VisibleRows);
        MonstersPanel.Rule? sel = null;
        foreach (MonstersPanel.Rule r in rules)
            if (r.Name.Equals(_selName, StringComparison.OrdinalIgnoreCase)) { sel = r; break; }
        if (sel == null && rules.Count > 0) { sel = rules[0]; _selName = sel.Name; }
        for (int row = 0; row < VisibleRows && _first + row < rules.Count; row++)
        {
            int i = _first + row;
            MonstersPanel.Rule r = rules[i];
            var p = new Vector2(o.X, y + row * rowH);
            bool isSel = ReferenceEquals(r, sel);
            if (isSel) RowSelected(dl, p, p + new Vector2(listW, rowH));
            else if ((i & 1) == 1) dl.AddRectFilled(p, p + new Vector2(listW, rowH), AltRow);
            ImGuiNET.ImGui.SetCursorScreenPos(p);
            ImGuiNET.ImGui.PushID(i);
            if (ImGuiNET.ImGui.InvisibleButton("##rule", new Vector2(listW, rowH))) { _selName = r.Name; _priorityShown = int.MinValue; }
            if (ImGuiNET.ImGui.IsItemHovered() && !isSel) dl.AddRectFilled(p, p + new Vector2(listW, rowH), Hover);
            ImGuiNET.ImGui.PopID();
            float x = p.X + Px(4);
            bool isDefault = r.Name.Equals("Default", StringComparison.OrdinalIgnoreCase);
            TextClip(dl, x, p.Y, rowH, x + cName - Px(4), r.Name, isDefault ? Gold : White, 10); x += cName;
            TextClip(dl, x, p.Y, rowH, x + cWeapon - Px(4), WeaponName(r.WeaponId), Text, 10); x += cWeapon;
            TextClip(dl, x, p.Y, rowH, x + cDmg - Px(4), r.DamageType, Amber, 10); x += cDmg;
            TextClip(dl, x, p.Y, rowH, x + cPri - Px(4), r.Priority.ToString(CultureInfo.InvariantCulture), Text, 10); x += cPri;
            TextClip(dl, x, p.Y, rowH, x + cMarks, DamageCommands.DebuffMarks(r), Dim, 10);
        }
        if (rules.Count == 0)
            TextMid(dl, o.X + Px(6), y, rowH, data == null ? "Waiting for RynthAi…" : "No rules.", Dim, 10);
        _first = Scrollbar("##cmo_sb", new Vector2(o.X + listW, y), listH, rules.Count, VisibleRows, _first, ref _grab);
        y += listH;

        // The selected rule.
        if (sel != null && data != null)
        {
            y += Px(8);
            string learned = Learned(data, sel.Name);
            Header(dl, new Vector2(o.X, y), w, sel.Name, learned);
            y += Px(23);
            y = RuleEditor(ctx, sel, new Vector2(o.X, y), w);
        }

        // Add / remove / the Damage panel.
        y += Px(6);
        {
            string target = data?.CurrentTargetName ?? string.Empty;
            float addW = Px(118), remW = Px(90);
            bool canAdd = target.Length > 0 && snap != null && !snap.HasCustomRule(target);
            if (Button("##cmo_add", "Add target", new Vector2(o.X, y), addW, ButtonColor.Blue, canAdd))
            {
                DamageCommands.AddRule(target, string.Empty);
                _selName = target;
            }
            SafeImGui.SetItemTooltip(target.Length == 0 ? "Select a monster in game to make a rule for it."
                : canAdd ? "Make a rule for " + target + " (a copy of Default)." : target + " already has a rule.");
            bool canRemove = sel != null && !sel.Name.Equals("Default", StringComparison.OrdinalIgnoreCase);
            if (Button("##cmo_remove", "Remove", new Vector2(o.X + addW + Px(6), y), remW, ButtonColor.Red, canRemove) && sel != null)
            {
                DamageCommands.ResetRule(sel.Name);
                _selName = "Default";
            }
            SafeImGui.SetItemTooltip(canRemove ? "Remove " + sel!.Name + "'s rule: it follows Default again." : "Default can't be removed.");
            var plate = new Vector2(o.X + w - Px(46), y + Px(7.5f));
            if (Plate("##cmo_damage", "More", plate))
                PanelRouter.ToggleMonsters();
            SafeImGui.SetItemTooltip("The Damage panel: off hand, pet, kite, attack power, match expressions, learned damage");
            TextClip(dl, o.X + addW + remW + Px(18), y, Px(32), plate.X - Px(6),
                target.Length > 0 ? "Target: " + target : "No target", target.Length > 0 ? Gold : Faint, 10);
            y += Px(32);
        }
        return y - o.Y;
    }

    private float RuleEditor(ClassicContext ctx, MonstersPanel.Rule sel, Vector2 o, float w)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        float y = o.Y, rowH = Px(22), labelW = Px(56);
        string name = sel.Name;

        // Weapon.
        TextMid(dl, o.X + Px(3), y, rowH, "Weapon", Text);
        var wPos = new Vector2(o.X + labelW, y + (rowH - Px(19)) * 0.5f);
        float wW = w - labelW;
        int wIdx = Array.IndexOf(_weaponIds, sel.WeaponId);
        if (Dropdown("##cmo_weapon", WeaponName(sel.WeaponId), wPos, wW))
        {
            int[] ids = _weaponIds;
            ctx.Popup.Open(wPos + new Vector2(0, Px(19)), wW, _weaponNames, wIdx, k =>
                DamageCommands.EditRule(name, r => r.WeaponId = ids[k]));
        }
        SafeImGui.SetItemTooltip("The weapon or caster RynthAi fights this monster with (the Gear tab's weapons).");
        y += rowH;

        // Damage | priority.
        TextMid(dl, o.X + Px(3), y, rowH, "Damage", Text);
        float dW = Px(120);
        var dPos = new Vector2(o.X + labelW, y + (rowH - Px(19)) * 0.5f);
        int dIdx = Math.Max(0, Array.IndexOf(DamageCommands.DamageTypes, sel.DamageType));
        if (Dropdown("##cmo_dmg", DamageCommands.DamageTypes[dIdx], dPos, dW))
            ctx.Popup.Open(dPos + new Vector2(0, Px(19)), dW, DamageCommands.DamageTypes, dIdx, k =>
                DamageCommands.EditRule(name, r => r.DamageType = DamageCommands.DamageTypes[k]));
        SafeImGui.SetItemTooltip("Auto: picked from what the monster is weakest to.");
        float px = dPos.X + dW + Px(16);
        TextMid(dl, px, y, rowH, "Priority", Text);
        if (!_typingPriority && (_priorityShown != sel.Priority || _priorityFor != name))
        {
            WriteInt(_priority, sel.Priority);
            _priorityShown = sel.Priority;
            _priorityFor = name;
        }
        float boxW = Px(40);
        bool done = NumberBox("##cmo_pri", _priority, new Vector2(o.X + w - boxW, y + (rowH - Px(17)) * 0.5f), boxW, out bool typing);
        _typingPriority = typing;
        SafeImGui.SetItemTooltip("Higher priority monsters are attacked first.");
        if (done && ClassicSettings.TryParse(_priority, out double pv))
        {
            int v = (int)Math.Clamp(Math.Round(pv), 0, 99);
            WriteInt(_priority, v);
            if (v != sel.Priority) DamageCommands.EditRule(name, r => r.Priority = v);
        }
        y += rowH + Px(2);

        // Debuffs and shapes: three gems a row.
        float gw = w / 3;
        y = GemGrid("Debuffs", DamageCommands.DebuffDefs, sel, name, new Vector2(o.X, y), gw);
        y = GemGrid("Void", DamageCommands.VoidDebuffDefs, sel, name, new Vector2(o.X, y), gw);
        y = GemGrid("Spells", DamageCommands.ShapeDefs, sel, name, new Vector2(o.X, y), gw);
        return y;
    }

    private static float GemGrid(string title, (string Field, string Label, string Tip)[] defs, MonstersPanel.Rule sel, string name, Vector2 o, float gw)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        TextMid(dl, o.X + Px(3), o.Y, Px(14), title, GoldDim, 10);
        float y = o.Y + Px(14);
        for (int i = 0; i < defs.Length; i++)
        {
            var (field, label, tip) = defs[i];
            bool on = sel.GetToggle(field);
            if (Gem("##g_" + field, label, new Vector2(o.X + (i % 3) * gw, y + (i / 3) * Px(18)), gw, on))
                DamageCommands.EditRule(name, r => DamageCommands.FlipToggle(r, field));
            SafeImGui.SetItemTooltip(tip);
        }
        return y + ((defs.Length + 2) / 3) * Px(18) + Px(2);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private void BuildWeapons(MonsterRulesSnapshot snap)
    {
        _itemsFor = snap;
        List<MonstersPanel.ItemRef> items = snap.Parsed.Items;
        _weaponNames = new string[items.Count + 1];
        _weaponIds = new int[items.Count + 1];
        _weaponNames[0] = "None (Default's)";
        _weaponById.Clear();
        for (int i = 0; i < items.Count; i++)
        {
            _weaponNames[i + 1] = items[i].Name;
            _weaponIds[i + 1] = items[i].Id;
            _weaponById[items[i].Id] = items[i].Name;
        }
    }

    // A weapon id the Gear list doesn't have (sold, traded, or listed on another character).
    private string WeaponName(int id) => id == 0 ? "—" : _weaponById.TryGetValue(id, out string? n) ? n : "not on the Gear list";

    private static string Learned(MonstersPanel.Payload data, string name)
    {
        if (!data.Captured.TryGetValue(name, out MonstersPanel.CapturedInfo? c) || c.Samples == 0) return "nothing learned yet";
        string weak = c.WeakestType.Length > 0 ? " · weak to " + c.WeakestType.ToLowerInvariant() : "";
        return $"HP {c.MaxHealth:N0} · armor {c.ArmorLevel}{weak}";
    }

    private static void WriteInt(byte[] buffer, int value)
    {
        string text = value.ToString(CultureInfo.InvariantCulture);
        int n = System.Text.Encoding.UTF8.GetBytes(text, 0, text.Length, buffer, 0);
        buffer[Math.Min(n, buffer.Length - 1)] = 0;
    }
}
