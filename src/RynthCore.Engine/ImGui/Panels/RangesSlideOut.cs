// ============================================================================
//  RynthCore.Engine - ImGui/Panels/RangesSlideOut.cs
//  The RynthAi dashboard's "Ranges" drawer (a player request, Tom 2026-10-04):
//  a small tab on the dashboard's left edge; clicking it slides a narrow panel
//  out to the left (to the right when the dashboard sits too close to the
//  screen's left edge for it). Top to bottom:
//    title       ruler + RANGES (yd) | close
//    monster     the selected creature: name, priority / damage type / weak
//                to, weapon; Edit opens its Monster Detail page, the skull its
//                row on the Damage panel (weapon, off hand, pet, debuffs).
//                "Select a monster" with nothing selected; "Not a monster" for
//                players, NPCs, pets and items (Edit off).
//    ranges      slider + number rows: Monster range, Nav point reach, Ring
//                range, Approach range, Blast range (0 = off), Corpse range
//                (max). Labels are short; min/max/step, tooltips and saving
//                are the Settings panel's rows (SettingsSchema.Find).
//  Values save exactly like the Settings face: a private copy of the hub's
//  settings, the row's Set, SettingsCommands.Save of the whole copy (RynthAi
//  saves it to the profile). A newer fetch replaces the copy while nothing here
//  is typed in or dragged, so Settings > Ranges and this drawer follow each
//  other. A slider saves when it's let go; a typed value on Enter or focus loss
//  (clamped; bad input reverts). Number boxes are FaceKit.TextBox, so typing
//  keeps keys out of AC (ImGuiTextFocus).
//
//  One of the dashboard's left-edge drawers (DashboardDrawers.cs: the tab, the
//  slide, the side, the window; one drawer open at a time). Open/closed is
//  saved with the dashboard's state (RynthAiDashboardState.RangesOpen). Popped
//  out the tabs are hidden (see DashboardDrawers); Settings > Ranges still
//  works there.
//
//  AC thread only (inside the dashboard's Draw). The selection is read once a
//  frame while the drawer shows and resolved only when it changes; display
//  strings are built when the selection or its data changes, not per frame.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using ImGuiNET;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.UI;
using RynthCore.Engine.UI.Data;
using Rule = RynthCore.Engine.UI.Panels.MonstersPanel.Rule;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed class RangesSlideOut : RynthAiDrawer
{
    // ── Palette (the dashboard's, ARGB) ────────────────────────────────
    private static readonly uint Gold = C(0xFFFFD16A);
    private static uint Teal => RynthTheme.Accent;
    private static uint TealSoft => RynthTheme.AccentFill;
    private static uint Amber => RynthTheme.Highlight;
    private static uint Mute => RynthTheme.TextMuted;
    private static uint White => RynthTheme.Text;
    private static uint BarBg => RynthTheme.Field;
    private static uint BtnFill => RynthTheme.Button;
    private static uint BtnBord => RynthTheme.Border;
    private static uint C(uint argb) => RynthTheme.Argb(argb);

    // ── Rows: (Settings row label, short label here) ────────────────────
    private static readonly (string Schema, string Short)[] Wanted =
    {
        ("Monster Range", "Monster range"),
        (SettingsSchema.NavPointReachLabel, "Nav point reach"),
        ("Ring Range", "Ring range"),
        ("Approach Range", "Approach range"),
        ("Blast Range (yd)", "Blast (0=off)"),
        ("Corpse Max (yd)", "Corpse range"),
    };

    private sealed class Row
    {
        public required SettingRow Setting;
        public required string Label;
        public required string Tip;
        public required bool IsInt;
        public readonly byte[] Buffer = new byte[24];
        public double Shown = double.NaN;
        public bool Typing, Dragging;
        public double DragValue;
    }

    private static Row[]? _rows;

    private static Row[] Rows => _rows ??= BuildRows();

    private static Row[] BuildRows()
    {
        var list = new List<Row>(Wanted.Length);
        foreach (var (schema, label) in Wanted)
        {
            SettingRow? s = SettingsSchema.Find(schema);
            if (s?.Get == null || s.Set == null) continue;   // renamed in the schema: leave it out
            string tip = s.Tooltip ?? schema;
            if (schema == "Corpse Max (yd)") tip = "Corpse Max on Settings > Ranges.\n" + tip;
            list.Add(new Row { Setting = s, Label = label, Tip = tip, IsInt = s.Kind == SettingKind.Int });
        }
        return list.ToArray();
    }

    // ── State ────────────────────────────────────────────────────────────
    private RynthAiSettings? _data;
    private long _seenVersion = -1;
    private bool _busy;                     // a row was typed in or dragged last frame
    private bool _subSettings, _subMonster;

    // The selection, resolved when it changes.
    private enum SelKind { None, Unknown, Monster, NotMonster }
    private uint _selId = uint.MaxValue;
    private long _selSince, _selChecked;
    private SelKind _kind = SelKind.None;
    private string _selName = "";
    private uint _selWcid;
    private long _damageSeen = -1, _rulesSeen = -1;
    private bool _factsDirty = true;
    private string _facts1 = "", _facts2 = "", _factsTip = "";
    private string _editTip = NoEditTip, _damageTip = NoDamageTip;
    private const string NoEditTip = "Select a monster to edit its rule.";
    private const string NoDamageTip = "Select a monster to see it on the Damage panel.";
    private bool _factsGold;

    private const uint TypeCreature = 0x10;   // ITEM_TYPE TYPE_CREATURE
    private const uint BfPlayer = 0x8;        // PublicWeenieDesc BF_PLAYER
    private const uint BfVendor = 0x200;      // PublicWeenieDesc BF_VENDOR
    private const uint PetOwnerIid = 44;      // PropertyInstanceId PetOwner (summoned pets)

    // ── Drawer ───────────────────────────────────────────────────────────

    public override string Key => "ranges";
    public override string Icon => PhosphorIcons.Ruler;
    public override string Name => "ranges";
    public override bool SavedOpen => RynthAiDashboardState.RangesOpen;
    public override void SaveOpen(bool open) => RynthAiDashboardState.SetRangesOpen(open);

    public override string TabTooltip(bool right) =>
        "Ranges: monster range, nav point reach and more, and the selected monster.\nClick to slide them out.";

    public override float Width(float k) => 250 * k;
    public override float Height(float k) => PanelHeight(k);

    /// <summary>While the panel is out: the selection, the hub subscriptions, a newer settings fetch.</summary>
    public override void Update(bool showing)
    {
        if (showing) ReadSelection();
        SetSubscriptions(showing, showing && _kind == SelKind.Monster);
        if (showing) TakeNewerSettings(force: false);
    }

    // ── Panel ────────────────────────────────────────────────────────────

    private const float LineH = 15, RowH = 20, RowGap = 3;

    private static float PanelHeight(float k) =>
        (Pad + TitleH + 4 + 3 * LineH + 6 + 3 + Math.Max(1, Rows.Length) * (RowH + RowGap) + Pad) * k;

    public override void DrawPanel(Vector2 origin, float panelW, float panelH, float k)
    {
        // Title row: ruler + RANGES (yards) | close.
        float y = PanelFrame(origin, panelW, panelH, k, PhosphorIcons.Ruler, "RANGES", "yards", "Hide the ranges");
        DrawBody(origin.X + Pad * k, y, panelW - 2 * Pad * k, k);
    }

    /// <summary>The body's height under the title row (pixels): RynthAi's own dashboard window draws the frame (SettingsDrawerBodies).</summary>
    public static float BodyHeight(float k) => (3 * LineH + 6 + 3 + Math.Max(1, Rows.Length) * (RowH + RowGap)) * k;

    /// <summary>The selected monster and the range rows at (<paramref name="x"/>, <paramref name="y"/>), <paramref name="w"/> wide.</summary>
    public void DrawBody(float x, float y, float w, float k)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        ImFontPtr f10 = ImGuiFonts.Get(UiFont.Dash10);

        MonsterBlock(dl, x, y, w, k);
        y += 3 * LineH * k + 6 * k;
        dl.AddLine(new Vector2(x, y), new Vector2(x + w, y), BtnBord);
        y += 3 * k;

        if (_data == null)
        {
            dl.AddText(f10, f10.FontSize, new Vector2(x, y + 4 * k), Mute,
                UiSources.Settings.Current == null ? "Waiting for RynthAi…" : "Loading…");
            _busy = false;
            return;
        }

        bool busy = false;
        Row[] rows = Rows;
        float labelW = 0;
        foreach (Row r in rows) labelW = MathF.Max(labelW, CalcWidth(f10, r.Label));
        labelW += 6 * k;
        for (int i = 0; i < rows.Length; i++)
        {
            ImGuiNET.ImGui.PushID(i);
            busy |= NumberRow(rows[i], x, y, w, labelW, k);
            ImGuiNET.ImGui.PopID();
            y += (RowH + RowGap) * k;
        }
        _busy = busy;
    }

    // ── Number rows: label | slider | box ────────────────────────────────

    /// <summary>Returns true while the row is typed in or dragged.</summary>
    private bool NumberRow(Row r, float x, float y, float w, float labelW, float k)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        SettingRow s = r.Setting;
        double value = Math.Clamp(s.Get!(_data!), s.Min, s.Max);
        double shownValue = r.Dragging ? r.DragValue : value;
        float h = RowH * k;

        // Label (tooltip: the Settings row's).
        float fs = ImGuiNET.ImGui.GetFontSize();
        dl.AddText(new Vector2(x, y + (h - fs) * 0.5f), White, r.Label);
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x, y));
        ImGuiNET.ImGui.InvisibleButton("##label", new Vector2(Math.Max(1, labelW - 2 * k), h));
        SafeImGui.SetItemTooltip(r.Tip);

        // Box at the right.
        float boxW = 46 * k;
        float boxX = x + w - boxW;
        if (!r.Typing && r.Shown != shownValue) WriteValue(r, shownValue);
        FaceKit.TextBox("##v", r.Buffer, new Vector2(boxX, y), boxW, "", out bool typing,
            ImGuiInputTextFlags.CharsDecimal | ImGuiInputTextFlags.AutoSelectAll, h);
        r.Typing = typing;
        if (ImGuiNET.ImGui.IsItemDeactivatedAfterEdit())
        {
            if (TryParse(r.Buffer, out double typed))
            {
                double clamped = Math.Clamp(r.IsInt ? Math.Round(typed) : typed, s.Min, s.Max);
                Changed(r, clamped);
            }
            else WriteValue(r, value);   // bad input reverts
        }
        SafeImGui.SetItemTooltip(r.Tip);

        // Slider between them: drag (or click) to pick, saved when let go.
        float sx = x + labelW, sw = boxX - 6 * k - sx;
        if (sw > 10 * k)
        {
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(sx, y));
            ImGuiNET.ImGui.InvisibleButton("##slider", new Vector2(sw, h));
            bool active = ImGuiNET.ImGui.IsItemActive();
            bool hot = active || ImGuiNET.ImGui.IsItemHovered();
            float knobR = 5 * k;
            float trackX0 = sx + knobR, trackX1 = sx + sw - knobR;
            if (active && trackX1 > trackX0)
            {
                float f = Math.Clamp((ImGuiNET.ImGui.GetMousePos().X - trackX0) / (trackX1 - trackX0), 0f, 1f);
                r.DragValue = Snap(s.Min + f * (s.Max - s.Min), s, r.IsInt);
                r.Dragging = true;
                shownValue = r.DragValue;
            }
            if (ImGuiNET.ImGui.IsItemDeactivated() && r.Dragging)
            {
                r.Dragging = false;
                if (r.DragValue != value) Changed(r, r.DragValue);
            }
            SafeImGui.SetItemTooltip(r.Tip);

            float cy = y + h * 0.5f;
            float frac = s.Max > s.Min ? (float)((shownValue - s.Min) / (s.Max - s.Min)) : 0f;
            float kx = trackX0 + (trackX1 - trackX0) * Math.Clamp(frac, 0f, 1f);
            dl.AddRectFilled(new Vector2(trackX0, cy - 2 * k), new Vector2(trackX1, cy + 2 * k), BarBg, 2 * k);
            dl.AddRect(new Vector2(trackX0, cy - 2 * k), new Vector2(trackX1, cy + 2 * k), BtnBord, 2 * k);
            if (kx > trackX0) dl.AddRectFilled(new Vector2(trackX0, cy - 2 * k), new Vector2(kx, cy + 2 * k), TealSoft, 2 * k);
            dl.AddCircleFilled(new Vector2(kx, cy), knobR, hot ? White : Teal);
        }
        return r.Typing || r.Dragging;
    }

    private static double Snap(double v, SettingRow s, bool isInt)
    {
        double step = s.Step > 0 ? s.Step : 1;
        v = s.Min + Math.Round((v - s.Min) / step) * step;
        v = isInt ? Math.Round(v) : Math.Round(v, 4);
        return Math.Clamp(v, s.Min, s.Max);
    }

    /// <summary>Saves a new value the way the Settings face does (the row's Set, the whole payload).</summary>
    private void Changed(Row r, double value)
    {
        TakeNewerSettings(force: true);
        if (_data == null) return;
        r.Setting.Set!(_data, value);
        SettingsCommands.Save(_data.Clone());
        WriteValue(r, value);
    }

    /// <summary>A newer fetch replaces the copy unless a row is in use (force: about to save anyway).</summary>
    private void TakeNewerSettings(bool force)
    {
        var snap = UiSources.Settings.Current;
        if (snap == null || snap.Version == _seenVersion || (_busy && !force)) return;
        _seenVersion = snap.Version;
        _data = snap.Value.Clone();
    }

    private static void WriteValue(Row r, double value)
    {
        string text = r.IsInt ? ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture)
                              : value.ToString("G4", CultureInfo.InvariantCulture);
        int n = Encoding.UTF8.GetBytes(text, 0, text.Length, r.Buffer, 0);
        r.Buffer[Math.Min(n, r.Buffer.Length - 1)] = 0;
        r.Shown = value;
    }

    private static bool TryParse(byte[] buffer, out double value)
    {
        int len = Array.IndexOf(buffer, (byte)0);
        if (len < 0) len = buffer.Length;
        return double.TryParse(Encoding.UTF8.GetString(buffer, 0, len), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    // ── Selected monster ─────────────────────────────────────────────────

    private void MonsterBlock(ImDrawListPtr dl, float x, float y, float w, float k)
    {
        ImFontPtr f11 = ImGuiFonts.Get(UiFont.Dash11), f10 = ImGuiFonts.Get(UiFont.Dash10);
        float lh = LineH * k;
        bool monster = _kind == SelKind.Monster;
        if (monster) BuildFacts();

        // Buttons at the right of the name line: Edit (Monster Detail), Damage panel row.
        Vector2 bsize = new(20 * k, lh);
        float bx = x + w - 2 * bsize.X - 2 * k;
        if (FaceKit.IconButton("##mon_edit", PhosphorIcons.PencilSimple, new Vector2(bx, y), bsize, monster ? Teal : Mute, BtnFill,
                enabled: monster, border: BtnBord, font: UiFont.Dash10))
            DamageDetailFace.Show(_selWcid, _selName);
        SafeImGui.SetItemTooltip(monster ? _editTip : NoEditTip);
        if (FaceKit.IconButton("##mon_damage", PhosphorIcons.Skull, new Vector2(bx + bsize.X + 2 * k, y), bsize, monster ? Teal : Mute, BtnFill,
                enabled: monster, border: BtnBord, font: UiFont.Dash10))
        {
            if (!ImGuiPanelHost.IsOpen(DamageFace.Title)) PanelRouter.ToggleMonsters();
        }
        SafeImGui.SetItemTooltip(monster ? _damageTip : NoDamageTip);

        // Name line.
        string name; uint nameColor;
        switch (_kind)
        {
            case SelKind.None: name = "Select a monster"; nameColor = Mute; break;
            case SelKind.Unknown: name = _selName.Length > 0 ? _selName : "Reading…"; nameColor = Mute; break;
            default: name = _selName.Length > 0 ? _selName : "(no name)"; nameColor = monster ? White : Mute; break;
        }
        float crossW = CalcWidth(f11, PhosphorIcons.Crosshair);
        dl.AddText(f11, f11.FontSize, new Vector2(x, y + (lh - f11.FontSize) * 0.5f), monster ? Teal : Mute, PhosphorIcons.Crosshair);
        dl.PushClipRect(new Vector2(x, y), new Vector2(bx - 4 * k, y + lh), true);
        dl.AddText(f11, f11.FontSize, new Vector2(x + crossW + 4 * k, y + (lh - f11.FontSize) * 0.5f), nameColor, name);
        dl.PopClipRect();

        // Two fact lines (one tooltip over both).
        float fy = y + lh;
        string l1, l2;
        uint c1 = Mute;
        switch (_kind)
        {
            case SelKind.None: l1 = "Click one to see its rule here."; l2 = ""; break;
            case SelKind.Unknown: l1 = ""; l2 = ""; break;
            case SelKind.NotMonster: l1 = "Not a monster"; l2 = ""; c1 = Amber; break;
            default: l1 = _facts1; l2 = _facts2; c1 = _factsGold ? Gold : Mute; break;
        }
        dl.PushClipRect(new Vector2(x, fy), new Vector2(x + w, fy + 2 * lh), true);
        if (l1.Length > 0) dl.AddText(f10, f10.FontSize, new Vector2(x, fy + (lh - f10.FontSize) * 0.5f), c1, l1);
        if (l2.Length > 0) dl.AddText(f10, f10.FontSize, new Vector2(x, fy + lh + (lh - f10.FontSize) * 0.5f), Mute, l2);
        dl.PopClipRect();
        if (monster && _factsTip.Length > 0)
        {
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x, fy));
            ImGuiNET.ImGui.InvisibleButton("##mon_facts", new Vector2(w, 2 * lh));
            SafeImGui.SetItemTooltip(_factsTip);
        }
    }

    /// <summary>Reads the selection (one read a frame) and classifies it when it changes.</summary>
    private void ReadSelection()
    {
        uint sel = ClientHelperHooks.GetSelectedItemId();
        long now = Environment.TickCount64;
        if (sel == _selId && (_kind != SelKind.Unknown || now - _selChecked < 250)) return;
        if (sel != _selId) { _selId = sel; _selSince = now; }
        _selChecked = now;
        _factsDirty = true;
        _selWcid = 0;
        if (sel == 0) { _kind = SelKind.None; _selName = ""; return; }

        _selName = ClientObjectHooks.TryGetSnapshotName(sel, out string n) ? n : "";
        SelKind kind = Classify(sel);
        // Not in the snapshots a second on: something that isn't out in the world (an item).
        if (kind == SelKind.Unknown && now - _selSince > 1000) kind = SelKind.NotMonster;
        if (kind == SelKind.Monster)
        {
            ClientObjectHooks.TryGetObjectWcid(sel, out _selWcid);   // 0: the rule alone (by name)
            _editTip = "Edit " + _selName + ": its Monster Detail page (priority, damage type, shapes).\n" +
                       "On Default, a change there gives it a rule of its own.";
            _damageTip = "Damage panel: " + _selName + "'s row (weapon, off hand, pet, debuffs).\n" +
                         "The selected monster is highlighted (and listed first with Nearby first on).";
        }
        _kind = kind;
    }

    private static SelKind Classify(uint id)
    {
        if (!ClientObjectHooks.TryGetSnapshotItemType(id, out uint type)) return SelKind.Unknown;
        if ((type & TypeCreature) == 0) return SelKind.NotMonster;
        if (!ClientObjectHooks.TryGetSnapshotPwdInfo(id, out uint bf, out _, out _)) return SelKind.Unknown;
        if ((bf & (BfPlayer | BfVendor)) != 0) return SelKind.NotMonster;
        if (ClientObjectHooks.TryGetObjectInstanceIdProperty(id, PetOwnerIid, out uint owner) && owner != 0) return SelKind.NotMonster;
        if (!ClientObjectHooks.TryGetSnapshotAttackable(id, out bool attackable)) return SelKind.Unknown;
        return attackable ? SelKind.Monster : SelKind.NotMonster;   // not attackable: an NPC
    }

    /// <summary>Priority / damage type / weak to and the weapon, rebuilt when the monster or its data changes.</summary>
    private void BuildFacts()
    {
        var dmg = UiSources.Damage.Current;
        var rules = UiSources.MonsterRules.Current;
        long dv = dmg?.Version ?? -1, rv = rules?.Version ?? -1;
        if (!_factsDirty && dv == _damageSeen && rv == _rulesSeen) return;
        _factsDirty = false;
        _damageSeen = dv;
        _rulesSeen = rv;

        string name = _selName;
        DamageRow? row = null;
        if (dmg != null)
            foreach (DamageRow r in dmg.Value.Rows)
                if (!r.IsDefault && (_selWcid != 0 ? r.Wcid == _selWcid : r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    row = r;
                    break;
                }

        var tip = new StringBuilder();
        string prio = "?", dtype = "?";
        _factsGold = false;
        if (rules != null)
        {
            MonsterRulesSnapshot rs = rules.Value;
            Rule? own = rs.FindRule(name);
            Rule? cover = own == null ? DamageCommands.CoveringRule(rs.Parsed.Rules, name) : null;
            Rule use = own ?? cover ?? rs.DefaultRule();
            prio = use.Priority.ToString(CultureInfo.InvariantCulture);
            dtype = string.IsNullOrEmpty(use.DamageType) ? "Auto" : use.DamageType;
            _factsGold = own != null;
            tip.Append("Priority ").Append(prio).Append(", damage type ").Append(dtype).Append(": ")
               .Append(own != null ? "its own rule." : cover != null ? "the name rule \"" + cover.Name + "\"." : "Default (no rule of its own).")
               .Append('\n');
        }
        else tip.Append("Monster rules loading…\n");

        string weakTop = "";
        if (row != null && row.Weak.Length > 0)
        {
            int comma = row.Weak.IndexOf(',');
            string first = comma > 0 ? row.Weak.Substring(0, comma) : row.Weak;
            int sp = first.IndexOf(' ');
            weakTop = sp > 0 ? first.Substring(0, sp) : first;
            tip.Append("Weak to: ").Append(row.Weak);
            if (row.WeakSrc.Length > 0) tip.Append(" (").Append(row.WeakSrc).Append(')');
            tip.Append('\n');
        }
        _facts1 = "Prio " + prio + " · " + dtype + (weakTop.Length > 0 ? " · weak " + weakTop : "");

        if (row == null)
        {
            _facts2 = dmg == null ? "Loading…" : "Not on the Damage panel yet";
            tip.Append(dmg == null ? "" : "Nothing learned about it yet: it joins the Damage panel once fought.\n");
        }
        else if (row.AssignedWid != 0)
        {
            string wname = row.AssignedWeapon.Length > 0 ? row.AssignedWeapon : "Weapon " + row.AssignedWid.ToString(CultureInfo.InvariantCulture);
            _facts2 = "Weapon " + wname;
            tip.Append("Weapon: ").Append(wname).Append(" (set for it).\n");
        }
        else
        {
            _facts2 = row.BestWid != 0 ? "Weapon Auto: " + row.BestWeapon : "Weapon Auto";
            tip.Append("Weapon: Auto").Append(row.BestWid != 0 ? " (learned best: " + row.BestWeapon + ")" : "").Append(".\n");
        }
        tip.Append("Gold = its own rule. Edit opens Monster Detail; the skull, the Damage panel.");
        _factsTip = tip.ToString();
    }

    // ── Hub subscriptions ────────────────────────────────────────────────

    private void SetSubscriptions(bool settings, bool monster)
    {
        if (settings != _subSettings)
        {
            _subSettings = settings;
            if (settings) { UiSources.Settings.Subscribe(); UiSources.Settings.RequestRefresh(); }
            else UiSources.Settings.Unsubscribe();
        }
        if (monster != _subMonster)
        {
            _subMonster = monster;
            if (monster)
            {
                UiSources.Damage.Subscribe();
                UiSources.MonsterRules.Subscribe();
                UiSources.Damage.RequestRefresh();
                UiSources.MonsterRules.RequestRefresh();
            }
            else
            {
                UiSources.Damage.Unsubscribe();
                UiSources.MonsterRules.Unsubscribe();
            }
        }
    }
}
