// ============================================================================
//  RynthCore.Engine - ImGui/Panels/DamageFace.cs
//  ImGui face of the Damage panel (UI/Panels/MonsterDamagePanel.cs): RynthAi's
//  learned per-monster combat data and the monster rules; the dashboard's
//  Monsters button opens it. (The basic Monsters panel was retired
//  2026-10-01; its rule editing moved here and into Monster Detail.)
//
//  Top bar: weapon filter, monster/wcid search, Reset stats (confirmed),
//  status. Name rules (folded): every rule but Default, by name (one can
//  cover many monsters, e.g. Olthoi, or one not met yet): priority,
//  expression, open, delete; and an add line. Table: a DEFAULT line (default
//  weapon, offhand, pet, Default's priority; its name opens the Default
//  settings), then one row per monster: D Monster | Priority | M HP | Weak to |
//  Weapon | Offhand | Pet | Kills | Sec/Kill | Debuffs | x. Priority edits the
//  monster's rule (- / + or type a number). Click a header to sort, a monster's name for its
//  Monster Detail panel (weaknesses, accuracy, damage, summons, damage taken,
//  its rule). x removes the monster (confirmed). Gold = set by you, dim =
//  automatic.
//  Under a monster's row, when the plugin sends them (the row's "mstats",
//  UI/Data/DamageRowStats.cs, 2026-10-30): how often it evades and resists you
//  and you it since login, the full numbers on hover.
//  Where the server plugin reports difficulty tiers: a tier chip after Reset stats
//  (DamageView.Tier) - which tier the numbers are for, and a picker; "~" marks an
//  HP estimated from tier 0's. Hidden elsewhere.
//
//  Data: UiSources.Damage and UiSources.MonsterRules (hub); every change goes
//  through DamageCommands on the pump thread. Rows are drawn only while on
//  screen; their display strings are built once per snapshot.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using ImGuiNET;
using RynthCore.Engine.UI.Data;
using Rule = RynthCore.Engine.UI.Panels.MonstersPanel.Rule;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed class DamageFace : IImGuiPanel
{
    public const string Title = "Damage";

    public static void Register() => ImGuiPanelHost.Register(Title,
        new PanelSpec(new Vector2(1060, 460), new Vector2(480, 200), EdgeToEdge: true),
        () => new DamageFace());

    // ── Palette (MonsterDamagePanel's, a little brighter) ───────────────
    private static readonly uint Gold = C(0xFFFFD16A), HeaderBg = C(0xFF121C26), PanelBg = C(0xF0081018),
        RowBg = C(0x40203040), PickerBg = C(0xFF0A141E), SearchBg = C(0xFF16222E), ToggleOn = C(0xFF33CC66),
        ToggleOff = C(0xFF4D4D4D), DeleteRed = C(0xFFFF8A8A), FilterBg = C(0xFF203A52), NearbyBg = C(0x3833CC66),
        NearbyBar = C(0xFF33CC66), ResetBg = C(0xFF5A2020), ResetBorder = C(0xFFFF8A8A), ClearBg = C(0xFF6E1E1E);
    private static uint Teal => RynthTheme.Accent;
    private static uint Dim => RynthTheme.MuteAlt;
    private static uint White => RynthTheme.Text;
    private static uint PickSel => RynthTheme.Selected;
    private static uint EntryBg => RynthTheme.Button;
    private static uint EntryBorder => RynthTheme.Border;
    private static uint TargetBg => RynthTheme.WithAlpha(RynthTheme.Accent, 0x50);
    private static uint C(uint argb) => RynthTheme.Argb(argb);

    // Column widths (the Avalonia grid's) and their left edges.
    // Widths are the user's (dragged in the header, saved per PC), else these defaults.
    private static readonly float[] DefaultColW = { 210, 72, 84, 92, 150, 130, 120, 84, 56, 70, 96, 26 };
    // Saved width keys: the columns keep the keys they had before Priority was added (2026-10-03),
    // so a saved layout still fits them; Priority has its own.
    private static readonly string[] ColKeys =
        { "col0", "colPrio", "col1", "col2", "col3", "col4", "col5", "colPrefer", "col6", "col7", "col8", "col9" };
    private static readonly float[] ColW = LoadColW();
    private static float[] ColX = BuildColX();

    private static float[] LoadColW()
    {
        var w = (float[])DefaultColW.Clone();
        for (int i = 0; i < w.Length; i++)
            w[i] = RynthCore.Engine.UI.PanelColumnStore.Get("Damage2." + ColKeys[i], w[i]);
        return w;
    }
    private static readonly string[] Headers =
        { "Monster", "Priority", "HP", "Weak to", "Weapon", "Offhand", "Pet", "Prefer", "Kills", "Sec/Kill", "Debuffs", "" };
    private const int ColMonster = 0, ColPrio = 1, ColHp = 2, ColWeak = 3, ColWeapon = 4, ColOffhand = 5, ColPet = 6, ColPrefer = 7, ColKills = 8,
        ColSec = 9, ColDebuffs = 10, ColDel = 11;
    private const float RowH = 22;
    private static float TableW => ColX[^1] + ColW[^1];

    private static float[] BuildColX()
    {
        var x = new float[ColW.Length];
        for (int i = 1; i < x.Length; i++) x[i] = x[i - 1] + ColW[i - 1];
        return x;
    }

    // ── Snapshot + per-row display strings ───────────────────────────────
    private sealed class RowText
    {
        public string Wcid = "", Tier = "", Crit = "", NonCrit = "", Casts = "", Kills = "";
        /// <summary>The name as shown: "Rampager (2 kinds)" for a row of several wcids.</summary>
        public string NameShown = "", NameTip = "";
        public string Weapon = "", Offhand = "", Pet = "", WeakTop = "", Sec = "", SecTip = "";
        public string Prefer = "Auto";
        /// <summary>The line under the row (the plugin's evades and resists since login) and its tooltip; "" for none.</summary>
        public string Stats = "", StatsTip = "";
        public bool CritSet, NonCritSet, WeaponGold, OffhandGold, PetGold, PreferGold;
        public string[] TierLines = Array.Empty<string>();
        /// <summary>Priority shown (int.MinValue = rules not loaded), whether it's the monster's own
        /// (gold), the rule an edit copies when it has none (a name rule covering it), and the tip.</summary>
        public int Prio = int.MinValue;
        public bool PrioGold;
        public string? PrioCopyFrom;
        public string PrioTip = "";
    }

    private DamageView? _view;
    private long _seenVersion = -1, _seenRulesVersion = -1;
    private MonsterRulesSnapshot? _rules;
    private readonly List<DamageRow> _shown = new();
    private readonly Dictionary<DamageRow, RowText> _text = new();
    private string _status = "Binding to RynthAi…";

    // Filters.
    private uint _filterWid;
    private string _filterLabel = "Weapon: All " + PhosphorIcons.CaretDown;
    private readonly byte[] _search = new byte[64];
    private string _searchText = string.Empty;

    // Sorting (click a header): column, direction. Default is by name. Nearby first puts the
    // targeted monster, then the ones on this landblock, above the rest (each in sort order).
    private int _sortCol = ColMonster;
    private bool _sortDesc;
    private static bool _nearbyFirst = true;

    // Debuffs popup: whose rule, where, and the typed-in debuffs being edited.
    private string _debuffFor = "";
    private bool _debuffOpen;
    private Vector2 _debuffPos;
    private readonly byte[] _customBuf = new byte[256];

    // Remove-monster confirmation.
    private uint _removeWcid;
    private string _removeName = "";
    private bool _removeOpen;

    // HP boxes, per row key.
    private sealed class HpEdit
    {
        public readonly byte[] Buffer = new byte[16];
        public int Shown = int.MinValue;
        public bool Active, ForceManual, FocusNext;
    }
    private readonly Dictionary<string, HpEdit> _hp = new();

    // Priority boxes, per row key ("default" for the DEFAULT line).
    private sealed class PrioEdit
    {
        public readonly byte[] Buffer = new byte[8];
        public int Shown = int.MinValue;
        public bool Active;
    }
    private readonly Dictionary<string, PrioEdit> _prio = new();

    // The one picker popup (weapon filter, per-row weapon/offhand/pet, extra vuln).
    private readonly List<(uint Id, string Name)> _pickItems = new();
    private uint _pickSel;
    private Action<uint>? _pickAction;
    private Vector2 _pickPos;
    private bool _pickOpen, _resetOpen;

    // Ellipsized text cache (per text and width).
    private readonly Dictionary<string, (float Width, string Shown)> _fit = new();

    public void OnShown()
    {
        UiSources.Damage.Subscribe();
        UiSources.MonsterRules.Subscribe();
        UiSources.Damage.RequestRefresh();
        UiSources.MonsterRules.RequestRefresh();
    }

    public void OnHidden()
    {
        UiSources.Damage.Unsubscribe();
        UiSources.MonsterRules.Unsubscribe();
    }

    // =====================================================================
    //  Frame
    // =====================================================================

    public void Draw()
    {
        TakeSnapshots();

        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        Vector2 origin = ImGuiNET.ImGui.GetCursorScreenPos();
        Vector2 size = ImGuiNET.ImGui.GetContentRegionAvail();
        dl.AddRectFilled(origin, origin + size, PanelBg, 4);

        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui11));
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6, 4));
        ImGuiNET.ImGui.SetCursorScreenPos(origin + new Vector2(6, 5));
        ImGuiNET.ImGui.Indent(6);

        TopBar();
        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
        NameRules(origin.X + size.X - 4 - ImGuiNET.ImGui.GetCursorScreenPos().X);
        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));

        Vector2 tableAt = ImGuiNET.ImGui.GetCursorScreenPos();
        Vector2 tableSize = new(origin.X + size.X - 4 - tableAt.X, origin.Y + size.Y - 4 - tableAt.Y);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ChildBg, 0);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 1));
        ImGuiNET.ImGui.BeginChild("##dmg_table", Vector2.Max(tableSize, new Vector2(50, 40)), ImGuiChildFlags.None,
            ImGuiWindowFlags.HorizontalScrollbar);
        Table();
        ImGuiNET.ImGui.EndChild();
        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.PopStyleColor();

        ImGuiNET.ImGui.Unindent(6);
        Popups();
        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.PopFont();
    }

    private void TakeSnapshots()
    {
        bool dirty = false;
        var rules = UiSources.MonsterRules.Current;
        if (rules != null && rules.Version != _seenRulesVersion)
        {
            _seenRulesVersion = rules.Version;
            _rules = rules.Value;
            dirty = true;   // the Offhand cells show the monsters' own rules
        }
        var snap = UiSources.Damage.Current;
        if (snap != null && snap.Version != _seenVersion)
        {
            _seenVersion = snap.Version;
            _view = snap.Value;
            dirty = true;
        }
        if (dirty) Rebuild();
    }

    /// <summary>The filtered row list, the status line and every row's display strings. Once per snapshot/filter change.</summary>
    private void Rebuild()
    {
        _shown.Clear();
        _text.Clear();
        _fit.Clear();
        _metByName.Clear();
        if (_view == null) { _status = "Binding to RynthAi…"; return; }
        if (!_view.Bound) { _status = "Waiting for RynthAi plugin…"; return; }
        foreach (DamageRow r in _view.Rows)
            if (!r.IsDefault) _metByName.TryAdd(r.Name, r.Wcid);

        if (_filterWid != 0 && !_view.LearnedWeapons.Exists(w => w.Id == _filterWid)) SetFilter(0, "All weapons");
        foreach (DamageRow r in _view.Rows)
        {
            if (!r.IsDefault)
            {
                if (_filterWid != 0 && r.Wid != _filterWid) continue;
                if (_searchText.Length > 0
                    && r.Name.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) < 0
                    && !r.Wcid.ToString(CultureInfo.InvariantCulture).Contains(_searchText, StringComparison.Ordinal)
                    && !Array.Exists(r.Wcids, w => w.ToString(CultureInfo.InvariantCulture).Contains(_searchText, StringComparison.Ordinal)))
                    continue;
            }
            _shown.Add(r);
            _text[r] = BuildText(r);
        }
        SortShown();
        _status = _view.Rows.Count == 0 ? "No kills recorded yet." : $"{_view.Rows.Count} rows · {_view.LearnedWeapons.Count} weapon(s)";
    }

    private void SortShown()
    {
        int col = _sortCol;
        Comparison<DamageRow> cmp = col switch
        {
            ColPrio => (a, b) => _text[a].Prio.CompareTo(_text[b].Prio),
            ColHp => (a, b) => a.Hp.CompareTo(b.Hp),
            ColWeak => (a, b) => string.Compare(_text[a].WeakTop, _text[b].WeakTop, StringComparison.OrdinalIgnoreCase),
            ColWeapon => (a, b) => string.Compare(_text[a].Weapon, _text[b].Weapon, StringComparison.OrdinalIgnoreCase),
            ColOffhand => (a, b) => string.Compare(_text[a].Offhand, _text[b].Offhand, StringComparison.OrdinalIgnoreCase),
            ColPet => (a, b) => string.Compare(_text[a].Pet, _text[b].Pet, StringComparison.OrdinalIgnoreCase),
            ColPrefer => (a, b) => string.Compare(_text[a].Prefer, _text[b].Prefer, StringComparison.OrdinalIgnoreCase),
            ColKills => (a, b) => a.Kills.CompareTo(b.Kills),
            ColSec => (a, b) => (a.SecKill > 0 ? a.SecKill : double.MaxValue).CompareTo(b.SecKill > 0 ? b.SecKill : double.MaxValue),
            _ => (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
        };
        bool desc = _sortDesc, nearbyFirst = _nearbyFirst;
        static int Rank(DamageRow r) => r.Targeted ? 0 : r.Nearby ? 1 : 2;
        _shown.Sort((a, b) =>
        {
            if (a.IsDefault != b.IsDefault) return a.IsDefault ? -1 : 1;   // Default stays on top
            if (nearbyFirst && Rank(a) != Rank(b)) return Rank(a).CompareTo(Rank(b));
            int c = cmp(a, b);
            if (desc) c = -c;
            return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
    }

    private RowText BuildText(DamageRow r)
    {
        var inv = CultureInfo.InvariantCulture;
        var t = new RowText();
        if (r.IsDefault)
        {
            t.WeaponGold = r.AssignedWid != 0;
            t.Weapon = t.WeaponGold ? Trunc(r.AssignedWeapon.Length > 0 ? r.AssignedWeapon : "Weapon " + r.AssignedWid, 16) : "Auto (per monster)";
            if (_rules != null && _rules.Parsed.Rules.Count > 0)
            {
                t.Prio = _rules.DefaultRule().Priority;
                t.PrioGold = true;
            }
            t.PrioTip = "Default's priority: a monster or name rule you add starts with it.\n" +
                "Targeting adds a bonus for priority above 1 (+5 a step) to monsters with a rule of their own or a name rule.";
            return t;
        }
        BuildPrio(r.Name, t);
        t.Wcid = r.Wcid.ToString(inv);
        const string DetailTip = "Details: weaknesses, accuracy, damage, summons, damage taken and spell settings (wcid ";
        if (r.Wcids.Length > 1)
        {
            // Monsters of one name with several weenies (retail has five Rampagers): one row.
            t.NameShown = r.Name + " (" + r.Wcids.Length.ToString(inv) + " kinds)";
            t.NameTip = r.Wcids.Length.ToString(inv) + " kinds of " + r.Name + ": wcids " + string.Join(", ", r.Wcids) + ".\n"
                + "Kills and fights are added up; a weapon, offhand, pet or HP set here applies to all of them.\n"
                + DetailTip + t.Wcid + ", the one with the most kills).";
        }
        else
        {
            t.NameShown = r.Name;
            t.NameTip = DetailTip + t.Wcid + ").";
        }
        t.Tier = DamageSource.FormatTier(r.Tier);
        t.CritSet = r.CritN > 0;
        t.Crit = t.CritSet ? r.Crit.ToString("0", inv) : "—";
        t.NonCritSet = r.NonCritN > 0;
        t.NonCrit = t.NonCritSet ? r.NonCrit.ToString("0", inv) : "—";
        t.Casts = r.Kills > 0 ? r.Casts.ToString("0.00", inv) : "—";
        t.Kills = r.Kills.ToString(inv);
        t.WeaponGold = r.AssignedWid != 0;
        t.Weapon = t.WeaponGold
            ? Trunc(r.AssignedWeapon.Length > 0 ? r.AssignedWeapon : "Weapon " + r.AssignedWid, 16)
            : r.BestWid != 0 ? "Auto: " + Trunc(r.BestWeapon, 12) : "Auto";
        // What applies: the item picked here, else the monster's own rule (a mode, or an old item pick), else Auto.
        int ruleOff = _rules?.FindRule(r.Name)?.OffhandId ?? 0;
        t.OffhandGold = r.AssignedOff != 0 || ruleOff != 0;
        t.Offhand = r.AssignedOff != 0 ? Trunc(r.AssignedOffName.Length > 0 ? r.AssignedOffName : "Offhand " + r.AssignedOff, 16)
            : ruleOff is >= 1 and <= 4 ? DamageCommands.OffhandModeNames[ruleOff]
            : ruleOff != 0 ? Trunc(WeaponName(unchecked((uint)ruleOff)), 16)
            : "Auto";
        string pref = RuleFor(r.IsDefault ? "Default" : r.Name, out _)?.PreferredDamage ?? "";
        t.PreferGold = pref.Length > 0 && !pref.Equals("Auto", StringComparison.OrdinalIgnoreCase);
        t.Prefer = t.PreferGold ? pref : "Auto";
        t.PetGold = r.Pet.Length > 0;
        t.Pet = Trunc(t.PetGold ? r.PetLabel : "Auto", 16);
        int comma = r.Weak.IndexOf(',');
        string firstWeak = comma > 0 ? r.Weak.Substring(0, comma) : r.Weak;   // "Bludgeon 1.0"
        int sp = firstWeak.IndexOf(' ');
        t.WeakTop = sp > 0 ? firstWeak.Substring(0, sp) : firstWeak;
        t.Sec = r.SecKill > 0 ? r.SecKill.ToString("0.0", inv) : "—";
        t.SecTip = (r.SecKill > 0 ? $"{r.SecKill:0.0} s from engaging to the kill (average of timed kills)." : "No timed kills yet.")
            + (r.HitRate >= 0 ? $"\nHits: {r.HitRate * 100:0}% of attacks and casts landed." : "");
        if (r.Stats != null)
        {
            t.Stats = r.Stats.Line();
            t.StatsTip = r.Stats.Tip();
        }
        t.TierLines = new string[r.Tiers.Count];
        for (int i = 0; i < r.Tiers.Count; i++)
        {
            DamageTierStat s = r.Tiers[i];
            string crit = s.CritN > 0 ? s.Crit.ToString("0", inv) : "—";
            string nc = s.NonCritN > 0 ? s.NonCrit.ToString("0", inv) : "—";
            string ck = s.Kills > 0 ? s.Casts.ToString("0.00", inv) : "—";
            t.TierLines[i] = $"{DamageSource.FormatTier(s.Tier),-4} {s.Elem,-8} kills {s.Kills,-4} crit {crit,-5} non-crit {nc,-5} casts/kill {ck}";
        }
        return t;
    }

    // =====================================================================
    //  Top bar
    // =====================================================================

    private void TopBar()
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        float x = p.X;

        float fw = Math.Max(120, ImGuiNET.ImGui.CalcTextSize(_filterLabel).X + 18);
        if (Button("##filter", _filterLabel, new Vector2(x, p.Y), new Vector2(fw, 22), White, FilterBg, FilterBg) && _view != null)
        {
            _pickItems.Clear();
            _pickItems.Add((0, "All weapons"));
            _pickItems.AddRange(_view.LearnedWeapons);
            OpenPicker(new Vector2(x, p.Y + 22), _filterWid, id =>
            {
                string name = id == 0 ? "All weapons" : "Weapon " + id;
                foreach (var w in _view!.LearnedWeapons) if (w.Id == id) name = w.Name;
                SetFilter(id, name);
                Rebuild();
            });
        }
        x += fw + 6;

        // Search (monster name or wcid).
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x, p.Y));
        ImGuiNET.ImGui.SetNextItemWidth(160);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg, SearchBg);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, EntryBorder);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, White);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(4, (22 - ImGuiNET.ImGui.GetFontSize()) * 0.5f));
        if (ImGuiNET.ImGui.InputText("##dmg_search", _search, (uint)_search.Length))
        {
            string t = Utf8(_search).Trim();
            if (t != _searchText) { _searchText = t; Rebuild(); }
        }
        bool searching = ImGuiNET.ImGui.IsItemActive();
        ImGuiNET.ImGui.PopStyleVar(2);
        ImGuiNET.ImGui.PopStyleColor(3);
        if (_search[0] == 0 && !searching)
            dl.AddText(new Vector2(x + 5, p.Y + (22 - ImGuiNET.ImGui.GetFontSize()) * 0.5f), Dim, SearchHint);
        x += 166;

        float nw = ImGuiNET.ImGui.CalcTextSize("Nearby first").X + 26;
        Vector2 np = new(x, p.Y);
        if (Button("##nearby", "", np, new Vector2(nw, 22), White, EntryBg, _nearbyFirst ? ToggleOn : EntryBorder))
        {
            _nearbyFirst = !_nearbyFirst;
            SortShown();
        }
        dl.AddRectFilled(np + new Vector2(5, 6), np + new Vector2(15, 16), _nearbyFirst ? ToggleOn : ToggleOff, 2);
        dl.AddText(np + new Vector2(20, (22 - ImGuiNET.ImGui.GetFontSize()) * 0.5f), White, "Nearby first");
        ImGuiNET.ImGui.SetItemTooltip("Keep the monster you're fighting (teal) and the ones on this landblock (green) at the top.");
        x += nw + 6;

        x = PreferChip(x, p.Y);

        if (Button("##reset", ResetLabel, new Vector2(x, p.Y), new Vector2(84, 22), White, ResetBg, ResetBorder))
            _resetOpen = true;
        ImGuiNET.ImGui.SetItemTooltip("Clear all learned damage statistics (keeps monster names + your manual settings).");
        x += 90;

        x = TierChip(x, p.Y);

        dl.AddText(new Vector2(x + 4, p.Y + (22 - ImGuiNET.ImGui.GetFontSize()) * 0.5f), Dim, _status);
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + 22));
        ImGuiNET.ImGui.Dummy(new Vector2(0, 0));
    }

    // ── Preferred damage (2026-10-16) ────────────────────────────────────
    // The Default rule's PreferredDamage: the kind of damage RynthAi fights with when the character
    // uses more than one (Auto = the first listed weapon's kind). A monster's own rule can differ
    // (Rule section of its detail drawer); one left on Auto follows this.

    /// <summary>The "Prefer" chip at <paramref name="x"/>; returns the x after it.</summary>
    private float PreferChip(float x, float y)
    {
        string current = _rules?.DefaultRule()?.PreferredDamage ?? "Auto";
        int cur = Math.Max(0, Array.FindIndex(DamageCommands.PreferredDamages, v => v.Equals(current, StringComparison.OrdinalIgnoreCase)));
        string label = "Prefer: " + DamageCommands.PreferredDamages[cur] + " " + PhosphorIcons.CaretDown;
        float w = ImGuiNET.ImGui.CalcTextSize(label).X + 16;
        if (Button("##prefer", label, new Vector2(x, y), new Vector2(w, 22), cur > 0 ? Gold : White, FilterBg, cur > 0 ? Gold : EntryBorder))
        {
            _pickItems.Clear();
            for (int i = 0; i < DamageCommands.PreferredDamages.Length; i++)
                _pickItems.Add(((uint)i, DamageCommands.PreferredDamages[i]));
            OpenPicker(new Vector2(x, y + 22), (uint)cur,
                id => DamageCommands.EditRule("Default", r => r.PreferredDamage = DamageCommands.PreferredDamages[Math.Min((int)id, DamageCommands.PreferredDamages.Length - 1)]));
        }
        SafeImGui.SetItemTooltip(
            "The kind of damage to fight with, for a character who uses more than one kind.\n" +
            "Auto = the kind of the first weapon in the Items list.\n" +
            "Melee / Missile / War Magic / Void Magic = only weapons of that kind are wielded;\n" +
            "with none listed (or usable) it falls back to Auto.\n" +
            "A monster with its own Prefer setting (its drawer) keeps it; a weapon you set for it still wins.");
        return x + w + 6;
    }

    // ── Difficulty tier (2026-10-03; any server whose plugin reports tiers, 2026-10-21) ──
    // Some servers scale the same monsters by a percent a tier, and RynthAi keeps each tier's
    // numbers apart. The chip says which tier the table shows and opens a picker; it is drawn only
    // when the data source says so (the server plugin reports a tier).

    private const uint FollowTier = uint.MaxValue;   // the picker's "where I am" entry

    private static string TierName(int tier) => tier <= 0 ? "Tier 0 (unscaled)" : "Tier " + tier.ToString(CultureInfo.InvariantCulture);

    /// <summary>The tier chip at <paramref name="x"/>; returns the x after it (unchanged when hidden).</summary>
    private float TierChip(float x, float y)
    {
        DifficultyTierView? t = _view?.Tier;
        if (t == null || !t.Show) return x;
        string label = TierName(t.View) + (t.View == t.Current ? " " + PhosphorIcons.MapPin : "") + " " + PhosphorIcons.CaretDown;
        float w = ImGuiNET.ImGui.CalcTextSize(label).X + 16;
        bool here = t.View == t.Current;
        if (Button("##tier", label, new Vector2(x, y), new Vector2(w, 22), here ? Teal : Gold, FilterBg, here ? EntryBorder : Gold))
        {
            _pickItems.Clear();
            _pickItems.Add((FollowTier, "Where I am: " + TierName(t.Current)));
            foreach (int tier in t.Tiers)
                _pickItems.Add(((uint)tier, tier <= 0 ? TierName(0)
                    : $"{TierName(tier)} (+{(tier * t.Percent).ToString("0", CultureInfo.InvariantCulture)}%)"));
            OpenPicker(new Vector2(x, y + 22), t.Following ? FollowTier : (uint)t.View, id =>
                DamageCommands.SetViewTier(id == FollowTier ? -1 : (int)id));
        }
        SafeImGui.SetItemTooltip(
            "This server scales its monsters by difficulty tier: the same monsters, with " +
            $"{t.Percent.ToString("0.#", CultureInfo.InvariantCulture)}% more health per tier.\n" +
            "HP, kills, casts to kill, seconds per kill and hit rate here are this tier's; damage per cast,\n" +
            "weaknesses and your settings are the same at every tier.\n" +
            (here ? "Showing the tier where you are (pin)." : $"You are in {TierName(t.Current)}; pick \"Where I am\" to follow it."));
        return x + w + 6;
    }

    private void SetFilter(uint wid, string name)
    {
        _filterWid = wid;
        _filterLabel = "Weapon: " + Trunc(name, 22) + " " + PhosphorIcons.CaretDown;
    }

    private const string SearchHint = PhosphorIcons.MagnifyingGlass + " Search monster / wcid…";
    private const string RulesHint = "one rule for many monsters (e.g. Olthoi), or for one you haven't met yet";
    private const string ResetLabel = PhosphorIcons.ArrowCounterClockwise + " Reset stats";
    private const string SortDown = " " + PhosphorIcons.CaretDown, SortUp = " " + PhosphorIcons.CaretUp;

    // =====================================================================
    //  Name rules (folded by default): every rule but Default, and an add line
    // =====================================================================

    private static bool _rulesOpen;
    private readonly byte[] _newRuleName = new byte[128], _newRuleExpr = new byte[512];
    private string _armedDelete = "";   // a rule whose x was clicked once: the second click deletes it
    // Monsters met (rows in the table) by name: their rule opens their full Monster Detail.
    private readonly Dictionary<string, uint> _metByName = new(StringComparer.OrdinalIgnoreCase);

    private static bool IsDefaultRule(Rule r) => r.Name.Equals("Default", StringComparison.OrdinalIgnoreCase);

    private void NameRules(float w)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        List<Rule>? all = _rules?.Parsed.Rules;
        int count = 0;
        if (all != null) foreach (Rule r in all) if (!IsDefaultRule(r)) count++;

        string label = (_rulesOpen ? PhosphorIcons.CaretDown : PhosphorIcons.CaretRight) + "  Name rules (" + count + ")";
        if (Button("##rules", label, p, new Vector2(w, 22), White, HeaderBg, EntryBorder, leftAlign: true))
        {
            _rulesOpen = !_rulesOpen;
            _armedDelete = "";
        }
        ImGuiNET.ImGui.SetItemTooltip("Rules by name: a rule covers every monster whose name contains it, met or not.\n" +
            "The first one that matches, top to bottom, is used; a monster's own settings are rules here too.");
        float lw = ImGuiNET.ImGui.CalcTextSize(label).X;
        if (w - lw - 30 > 40)
            dl.AddText(new Vector2(p.X + lw + 20, p.Y + (22 - ImGuiNET.ImGui.GetFontSize()) * 0.5f), Dim, Fit(RulesHint, w - lw - 30));
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + 22));
        ImGuiNET.ImGui.Dummy(new Vector2(0, 0));
        if (!_rulesOpen) return;

        float listH = Math.Min(Math.Max(1, count), 6) * (RowH + 1) + 2;
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ChildBg, 0);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 1));
        ImGuiNET.ImGui.BeginChild("##name_rules", new Vector2(w, listH));
        if (all == null || count == 0)
        {
            ImGuiNET.ImGui.Dummy(new Vector2(0, 3));
            SafeImGui.TextColored(RynthTheme.VecOf(RynthTheme.MuteAlt), all == null
                ? "  Monster rules not loaded yet." : "  No name rules yet: add one below.");
        }
        else
        {
            int i = 0;
            foreach (Rule r in all)
                if (!IsDefaultRule(r)) NameRuleRow(r, i++);
        }
        ImGuiNET.ImGui.EndChild();
        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.PopStyleColor();
        AddRuleLine(w);
    }

    // Name | P | expression | edit | x. The name (or the edit button) opens its Monster Detail.
    private void NameRuleRow(Rule r, int index)
    {
        const float prioW = 34, btnW = 24;
        float iw = ImGuiNET.ImGui.GetContentRegionAvail().X;
        float exprW = Math.Min(240, iw * 0.35f);
        float nameW = Math.Max(60, iw - prioW - exprW - 2 * btnW - 4);
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        float ty = p.Y + (RowH - ImGuiNET.ImGui.GetFontSize()) * 0.5f;
        dl.AddRectFilled(p, p + new Vector2(iw, RowH), index % 2 == 0 ? RowBg : HeaderBg);
        ImGuiNET.ImGui.PushID(r.Name);

        bool met = _metByName.TryGetValue(r.Name, out uint wcid);
        string name = r.Name;
        if (NameLink("##rn", name, new Vector2(p.X + 4, p.Y), nameW - 8, met ? White : Gold, ImGuiFonts.Get(UiFont.Ui11)))
            OpenRule(name);
        SafeImGui.SetItemTooltip(met
            ? "This monster's own settings (wcid " + wcid.ToString(CultureInfo.InvariantCulture) + "). Click for its details."
            : "Click to edit this rule.");
        float x = p.X + nameW;

        dl.AddText(new Vector2(x + 4, ty), White, r.Priority.ToString(CultureInfo.InvariantCulture));
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x, p.Y));
        ImGuiNET.ImGui.InvisibleButton("##rp", new Vector2(prioW, RowH));
        ImGuiNET.ImGui.SetItemTooltip("Target priority (higher is attacked first)");
        x += prioW;

        bool hasExpr = !string.IsNullOrWhiteSpace(r.MatchExpression);
        dl.AddText(new Vector2(x + 4, ty), hasExpr ? White : Dim, hasExpr ? Fit(r.MatchExpression, exprW - 8) : "by name only");
        if (hasExpr)
        {
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x, p.Y));
            ImGuiNET.ImGui.InvisibleButton("##rx", new Vector2(exprW, RowH));
            SafeImGui.SetItemTooltip("Match expression: " + r.MatchExpression);
        }
        x += exprW;

        if (IconButton("##ro", PhosphorIcons.PencilSimple, new Vector2(x + 2, p.Y + 2), new Vector2(btnW - 4, 18), White, EntryBg, EntryBorder))
            OpenRule(name);
        ImGuiNET.ImGui.SetItemTooltip("Edit: priority, damage, expression, spells");
        x += btnW;

        bool armed = _armedDelete.Equals(name, StringComparison.OrdinalIgnoreCase);
        if (IconButton("##rd", armed ? PhosphorIcons.Trash : PhosphorIcons.X, new Vector2(x + 2, p.Y + 2), new Vector2(btnW - 4, 18),
                armed ? White : DeleteRed, armed ? ClearBg : 0, armed ? ResetBorder : 0))
        {
            if (armed) { DamageCommands.ResetRule(name); _armedDelete = ""; }
            else _armedDelete = name;
        }
        SafeImGui.SetItemTooltip(armed ? "Click again to delete " + name : "Delete this rule (click twice)");

        ImGuiNET.ImGui.PopID();
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + RowH));
        ImGuiNET.ImGui.Dummy(new Vector2(iw, 0));
    }

    /// <summary>A rule's Monster Detail: the monster's full page when it was met, else the rule alone.</summary>
    private void OpenRule(string name)
    {
        if (_metByName.TryGetValue(name, out uint wcid)) DamageDetailFace.Show(wcid, name);
        else DamageDetailFace.Show(0, name);
    }

    // Name | Match expression | Target | Add
    private void AddRuleLine(float w)
    {
        ImGuiNET.ImGui.Dummy(new Vector2(0, 1));
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        const float tgtW = 74, addW = 64;
        float nameW = Math.Clamp((w - tgtW - addW - 12) * 0.4f, 80, 240);
        float exprW = Math.Max(60, w - nameW - tgtW - addW - 12);
        bool enter = HintInput("##newrule", _newRuleName, p.X, p.Y, nameW, "Name part, e.g. Olthoi…");
        ImGuiNET.ImGui.SetItemTooltip("Every monster whose name contains this. Enter to add.");
        enter |= HintInput("##newexpr", _newRuleExpr, p.X + nameW + 4, p.Y, exprW, "Match expression (optional)…");
        ImGuiNET.ImGui.SetItemTooltip("Only when this is true as well. Variables: name, range, typeid, maxhp, metastate\nExamples: range>5, maxhp>1000");
        float x = p.X + nameW + exprW + 8;
        if (Button("##ruletarget", PhosphorIcons.Crosshair + " Target", new Vector2(x, p.Y), new Vector2(tgtW, 22), White, EntryBg, EntryBorder))
        {
            string target = _rules?.Parsed.CurrentTargetName ?? "";
            if (target.Length > 0) WriteUtf8(_newRuleName, target);
        }
        ImGuiNET.ImGui.SetItemTooltip("Fill in the monster you have selected");
        x += tgtW + 4;
        if (Button("##ruleadd", PhosphorIcons.Plus + " Add", new Vector2(x, p.Y), new Vector2(addW, 22), Teal, EntryBg, Teal) | enter)
        {
            string name = Utf8(_newRuleName).Trim();
            if (name.Length > 0)
            {
                DamageCommands.AddRule(name, Utf8(_newRuleExpr));
                Array.Clear(_newRuleName);
                Array.Clear(_newRuleExpr);
            }
        }
        ImGuiNET.ImGui.SetItemTooltip("Add the rule. It starts as a copy of Default: click its name to set it up.");
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + 22));
        ImGuiNET.ImGui.Dummy(new Vector2(0, 0));
    }

    /// <summary>An edit box with a grey hint while empty; true when Enter was pressed in it.</summary>
    private static bool HintInput(string id, byte[] buffer, float x, float y, float width, string hint)
    {
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x, y));
        ImGuiNET.ImGui.SetNextItemWidth(width);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg, SearchBg);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, EntryBorder);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, White);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(4, (22 - ImGuiNET.ImGui.GetFontSize()) * 0.5f));
        bool enter = ImGuiNET.ImGui.InputText(id, buffer, (uint)buffer.Length, ImGuiInputTextFlags.EnterReturnsTrue);
        bool active = ImGuiNET.ImGui.IsItemActive();
        ImGuiNET.ImGui.PopStyleVar(2);
        ImGuiNET.ImGui.PopStyleColor(3);
        if (buffer[0] == 0 && !active)
            ImGuiNET.ImGui.GetWindowDrawList().AddText(new Vector2(x + 5, y + (22 - ImGuiNET.ImGui.GetFontSize()) * 0.5f), Dim, hint);
        return enter;
    }

    // =====================================================================
    //  Table
    // =====================================================================

    private void Table()
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        Vector2 clipMin = ImGuiNET.ImGui.GetWindowPos();
        Vector2 clipMax = clipMin + ImGuiNET.ImGui.GetWindowSize();

        // Header.
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        dl.AddRectFilled(p, p + new Vector2(TableW, RowH), HeaderBg);
        ImFontPtr bold = ImGuiFonts.Get(UiFont.UiBold11);
        for (int c = 0; c < Headers.Length; c++)
        {
            if (Headers[c].Length == 0) continue;
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X + ColX[c], p.Y));
            ImGuiNET.ImGui.SetNextItemAllowOverlap();
            if (ImGuiNET.ImGui.InvisibleButton("##sort" + c, new Vector2(Math.Max(1, ColW[c] - 4), RowH)))
            {
                if (_sortCol == c) _sortDesc = !_sortDesc;
                else { _sortCol = c; _sortDesc = c is ColKills or ColHp or ColPrio; }   // numbers: biggest first
                SortShown();
            }
            if (ImGuiNET.ImGui.IsItemHovered()) ImGuiNET.ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            SafeImGui.SetItemTooltip("Sort by " + Headers[c]);
            string label = Headers[c] + (_sortCol == c ? (_sortDesc ? SortDown : SortUp) : "");
            dl.AddText(bold, bold.FontSize, new Vector2(p.X + ColX[c] + 4, p.Y + (RowH - bold.FontSize) * 0.5f),
                _sortCol == c ? Teal : White, label);
        }
        ImGuiNET.ImGui.SetCursorScreenPos(p);
        // Drag a column's right edge in the header to resize it.
        for (int c = 0; c < ColW.Length - 1; c++)
        {
            int col = c;
            if (FaceKit.ColumnDivider($"##dcol{c}", p.X + ColX[c] + ColW[c], p.Y, RowH, ref ColW[c], 18,
                    v => RynthCore.Engine.UI.PanelColumnStore.Set("Damage2." + ColKeys[col], v)))
                ColX = BuildColX();
        }
        ImGuiNET.ImGui.Dummy(new Vector2(TableW, RowH));

        bool anyMonster = false;
        foreach (DamageRow r in _shown)
        {
            anyMonster |= !r.IsDefault;
            DrawRow(r, clipMin, clipMax);
        }

        if (!anyMonster && _view != null && _view.Bound)
        {
            bool filtering = _filterWid != 0 || _searchText.Length > 0;
            bool anyData = _view.Rows.Exists(r => !r.IsDefault);
            ImGuiNET.ImGui.Dummy(new Vector2(0, 4));
            SafeImGui.TextColored(RynthTheme.VecOf(RynthTheme.MuteAlt), anyData && filtering
                ? "  No monsters match the current filter."
                : "  No monsters yet: fight some and they show up here.");
        }
    }

    // The line under a monster's row: the plugin's evades and resists since login (DamageRowStats).
    private const float StatsH = 16;

    private void DrawRow(DamageRow r, Vector2 clipMin, Vector2 clipMax)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        RowText t = _text[r];
        float h = RowH + (t.Stats.Length > 0 ? StatsH : 0);
        if (p.Y + h < clipMin.Y || p.Y > clipMax.Y)
        {
            ImGuiNET.ImGui.Dummy(new Vector2(TableW, h));   // off screen: keep the layout, skip the drawing
            return;
        }
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        dl.AddRectFilled(p, p + new Vector2(TableW, h), r.IsDefault ? HeaderBg : RowBg);
        if (r.Targeted)
        {
            // The monster being fought (or selected): teal wash, a bar on the left, an outline.
            dl.AddRectFilled(p, p + new Vector2(TableW, h), TargetBg);
            dl.AddRectFilled(p, p + new Vector2(3, h), Teal);
            dl.AddRect(p, p + new Vector2(TableW, h), Teal);
        }
        else if (r.Nearby)
        {
            // On the player's landblock: a green wash and bar.
            dl.AddRectFilled(p, p + new Vector2(TableW, h), NearbyBg);
            dl.AddRectFilled(p, p + new Vector2(3, h), NearbyBar);
        }
        ImGuiNET.ImGui.PushID(r.IsDefault ? "default" : r.Key);
        float ty = p.Y + (RowH - ImGuiNET.ImGui.GetFontSize()) * 0.5f;

        if (r.IsDefault)
        {
            ImFontPtr bold = ImGuiFonts.Get(UiFont.UiBold11);
            if (NameLink("##dname", "DEFAULT", new Vector2(p.X + ColX[ColMonster] + 4, p.Y), ColW[ColMonster] - 8, Teal, bold))
                DamageDetailFace.Show(0, "Default");
            ImGuiNET.ImGui.SetItemTooltip("Default spell settings (debuffs, shapes): every monster set to Default uses them.");
            PrioCell("default", "Default", null, t, p);
            dl.AddText(new Vector2(p.X + ColX[ColWeak] + 4, ty), Dim, "weapon →");
            if (CellPicker("##dw", t.Weapon, t.WeaponGold, p, ColWeapon))
            {
                _pickItems.Clear();
                _pickItems.Add((0, "Auto (per monster)"));
                _pickItems.AddRange(_view!.WeaponChoices);
                OpenPicker(new Vector2(p.X + ColX[ColWeapon], p.Y + RowH), r.AssignedWid, DamageCommands.SetDefaultWeapon);
            }
            ImGuiNET.ImGui.SetItemTooltip("Default weapon: used by every monster that has no weapon of its own.");
            DefaultOffhandAndPet(p);
            PreferCell("Default", true, t, p);
            DebuffCell("Default", p, ty);
            ImGuiNET.ImGui.PopID();
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + RowH));
            ImGuiNET.ImGui.Dummy(new Vector2(TableW, 0));
            return;
        }

        // Monster: D (follows Default), then the name, which opens Monster Detail.
        bool custom = _rules?.HasCustomRule(r.Name) ?? false;
        bool onDefault = !custom && r.AssignedWid == 0;
        if (Button("##d", "D", new Vector2(p.X + ColX[ColMonster] + 2, p.Y + 2), new Vector2(16, 18), onDefault ? ToggleOn : Dim, EntryBg,
                onDefault ? ToggleOn : EntryBorder))
        {
            if (onDefault) DamageCommands.EditRule(r.Name, static _ => { });   // off Default: an editable rule of its own
            else
            {
                DamageCommands.ResetRule(r.Name);                             // back on Default
                DamageCommands.SetWeapon(r.Wcid, 0);
            }
        }
        ImGuiNET.ImGui.SetItemTooltip("Follow the Default config (spells, weapon). Uncheck to customize this monster.");
        if (NameLink("##name", t.NameShown, new Vector2(p.X + ColX[ColMonster] + 22, p.Y), ColW[ColMonster] - 24, custom ? Gold : White,
                ImGuiFonts.Get(UiFont.Ui11)))
            DamageDetailFace.Show(r.Wcid, r.Name);
        SafeImGui.SetItemTooltip(t.NameTip);

        PrioCell(r.Key, r.Name, t.PrioCopyFrom, t, p);
        HpCell(r, p);

        if (t.WeakTop.Length > 0)
        {
            dl.AddText(new Vector2(p.X + ColX[ColWeak] + 4, ty), White, Fit(t.WeakTop, ColW[ColWeak] - 8));
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X + ColX[ColWeak], p.Y));
            ImGuiNET.ImGui.InvisibleButton("##weak", new Vector2(ColW[ColWeak], RowH));
            SafeImGui.SetItemTooltip(r.Weak + "\nDamage taken: 1.0 = full, 0.5 = half. From " + r.WeakSrc
                + ".\nAuto spells, weapons and summons use the first element this character has.");
        }
        else
            dl.AddText(new Vector2(p.X + ColX[ColWeak] + 4, ty), Dim, "—");

        if (CellPicker("##w", t.Weapon, t.WeaponGold, p, ColWeapon))
        {
            _pickItems.Clear();
            _pickItems.Add((0, "Auto"));
            _pickItems.AddRange(_view!.WeaponChoices);
            uint wcid = r.Wcid;
            OpenPicker(new Vector2(p.X + ColX[ColWeapon], p.Y + RowH), r.AssignedWid, id => DamageCommands.SetWeapon(wcid, id));
        }
        ImGuiNET.ImGui.SetItemTooltip("Weapon to use on this monster. Auto = its rule's Damage type (Monster Detail), else an Items weapon of the element it's weakest to, else the learned best.");

        if (CellPicker("##o", t.Offhand, t.OffhandGold, p, ColOffhand))
            OpenOffhandPicker(r, new Vector2(p.X + ColX[ColOffhand], p.Y + RowH));
        ImGuiNET.ImGui.SetItemTooltip("Off hand against this monster. Auto = the DEFAULT line's.\n" +
            "Shield; Dual wield (needs the skill); None = leave the off hand alone; or a listed item.");

        if (CellPicker("##p", t.Pet, t.PetGold, p, ColPet))
        {
            List<(string Key, string Name)> pets = _view!.PetChoices;
            _pickItems.Clear();
            uint sel = 0;
            for (int i = 0; i < pets.Count; i++)
            {
                _pickItems.Add(((uint)i, pets[i].Name));
                if (pets[i].Key == r.Pet) sel = (uint)i;
            }
            uint wcid = r.Wcid;
            OpenPicker(new Vector2(p.X + ColX[ColPet], p.Y + RowH), sel, i =>
            {
                if (i < pets.Count) DamageCommands.SetPet(wcid, pets[(int)i].Key);
            });
        }
        ImGuiNET.ImGui.SetItemTooltip("Summon for this monster. Auto = an essence of the element it takes most damage from (Weak to); or pick an element or a specific essence.");

        PreferCell(r.Name, false, t, p);

        dl.AddText(new Vector2(p.X + ColX[ColKills] + 4, ty), White, t.Kills);
        dl.AddText(new Vector2(p.X + ColX[ColSec] + 4, ty), r.SecKill > 0 ? White : Dim, t.Sec);
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X + ColX[ColSec], p.Y));
        ImGuiNET.ImGui.InvisibleButton("##sec", new Vector2(ColW[ColSec], RowH));
        SafeImGui.SetItemTooltip(t.SecTip);

        DebuffCell(r.Name, p, ty);

        if (IconButton("##del", PhosphorIcons.X, new Vector2(p.X + ColX[ColDel] + 2, p.Y + 2), new Vector2(22, 18), DeleteRed, 0, 0))
        {
            _removeWcid = r.Wcid;
            _removeName = r.Name;
            _removeOpen = true;
        }
        SafeImGui.SetItemTooltip("Remove " + r.Name + " from this list (asks first).");

        if (t.Stats.Length > 0)
        {
            // Under the monster: its evades and resists both ways since login, the full numbers on hover.
            ImFontPtr small = ImGuiFonts.Get(UiFont.Ui10);
            var at = new Vector2(p.X + ColX[ColMonster] + 22, p.Y + RowH + (StatsH - small.FontSize) * 0.5f - 2);
            dl.PushClipRect(new Vector2(p.X, p.Y + RowH), new Vector2(p.X + TableW, p.Y + h), true);
            dl.AddText(small, small.FontSize, at, Dim, t.Stats);
            dl.PopClipRect();
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X + ColX[ColMonster] + 22, p.Y + RowH));
            ImGuiNET.ImGui.InvisibleButton("##stats", new Vector2(Math.Max(1, TableW - ColX[ColMonster] - 26), StatsH));
            SafeImGui.SetItemTooltip(t.StatsTip);
        }

        ImGuiNET.ImGui.PopID();
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + h));
        ImGuiNET.ImGui.Dummy(new Vector2(TableW, 0));
    }

    // The DEFAULT line's Offhand and Pet: the Default monster rule's OffhandId (1-4 a mode, else an
    // item id; 0 = Auto) and PetDamage ("PAuto" or an element). Every monster without its own uses them.
    private static readonly (uint Id, string Name)[] OffhandModeChoices =
        { (1, "Auto"), (2, "Shield"), (3, "Dual wield"), (4, "None") };
    private static readonly string[] PetElements = { "Fire", "Cold", "Lightning", "Acid", "Nether", "Slash", "Pierce", "Bludgeon" };

    /// <summary>The Prefer cell: Auto, Melee, Missile, War Magic or Void Magic for a monster's rule (or the DEFAULT line's).</summary>
    private void PreferCell(string name, bool isDefault, RowText t, Vector2 p)
    {
        if (CellPicker("##pref", t.Prefer, t.PreferGold, p, ColPrefer))
        {
            string[] choices = DamageCommands.PreferredDamages;
            _pickItems.Clear();
            for (int i = 0; i < choices.Length; i++) _pickItems.Add(((uint)i, choices[i]));
            int cur = Math.Max(0, Array.FindIndex(choices, v => v.Equals(t.Prefer, StringComparison.OrdinalIgnoreCase)));
            bool hasOwn = isDefault || (_rules?.FindRule(name) != null);
            OpenPicker(new Vector2(p.X + ColX[ColPrefer], p.Y + RowH), (uint)cur, id =>
            {
                string pick = choices[Math.Min((int)id, choices.Length - 1)];
                // Auto on a monster with no rule of its own is already what it does: don't make one.
                if (!hasOwn && pick == "Auto") return;
                DamageCommands.EditRule(name, r => r.PreferredDamage = pick);
            });
        }
        ImGuiNET.ImGui.SetItemTooltip(isDefault
            ? "Default preferred damage: the kind of damage used against every monster without its own.\nAuto = the kind of the first weapon in the Items list. Same as the Prefer chip above."
            : "The kind of damage to fight this monster with (Melee, Missile, War Magic, Void Magic).\nAuto = the DEFAULT line's. A weapon picked in the Weapon column still wins.");
    }

    private void DefaultOffhandAndPet(Vector2 p)
    {
        Rule? def = _rules?.DefaultRule();
        int offId = def?.OffhandId ?? 0;
        string offText = offId is 0 or 1 ? "Auto" : offId is >= 2 and <= 4 ? OffhandModeChoices[offId - 1].Name : "";
        if (offText.Length == 0)
        {
            foreach (var c in _view!.WeaponChoices)
                if (c.Id == unchecked((uint)offId)) { offText = Trunc(c.Name, 16); break; }
            if (offText.Length == 0) offText = "Offhand " + unchecked((uint)offId);
        }
        if (CellPicker("##doff", offText, offId > 1, p, ColOffhand))
        {
            _pickItems.Clear();
            _pickItems.AddRange(OffhandModeChoices);
            _pickItems.AddRange(_view!.WeaponChoices);
            OpenPicker(new Vector2(p.X + ColX[ColOffhand], p.Y + RowH), offId == 0 ? 1u : unchecked((uint)offId),
                id => DamageCommands.EditRule("Default", r => r.OffhandId = unchecked((int)id)));
        }
        ImGuiNET.ImGui.SetItemTooltip("Default offhand: used by every monster without its own.\n" +
            "Auto = a listed shield (dual wield only with Prefer dual wield on); Shield; Dual wield (needs the skill);\n" +
            "None = leave the off hand alone; or a specific item. Thrown weapons get a shield too.");

        string pet = def?.PetDamage ?? "PAuto";
        int petIdx = Array.FindIndex(PetElements, e => e.Equals(pet, StringComparison.OrdinalIgnoreCase));
        if (CellPicker("##dpet", petIdx >= 0 ? PetElements[petIdx] : "Auto", petIdx >= 0, p, ColPet))
        {
            _pickItems.Clear();
            _pickItems.Add((0, "Auto"));
            for (int i = 0; i < PetElements.Length; i++) _pickItems.Add(((uint)(i + 1), PetElements[i]));
            OpenPicker(new Vector2(p.X + ColX[ColPet], p.Y + RowH), (uint)(petIdx + 1),
                i => DamageCommands.EditRule("Default", r => r.PetDamage = i == 0 || i > PetElements.Length ? "PAuto" : PetElements[i - 1]));
        }
        ImGuiNET.ImGui.SetItemTooltip("Default pet element: used by every monster without its own.\nAuto = an essence of the element the monster takes most damage from.");
    }

    // A monster's Offhand picker: Auto, the modes, then the listed items. Modes go in the monster's
    // own rule (made if needed) and drop the item picked here; Auto drops both, so the DEFAULT line
    // applies; an item is kept per monster (wcid), and lifts a None in its rule (None ignores items).
    private static readonly (uint Id, string Name)[] RowOffhandModes =
        { (0, "Auto"), (2, "Shield"), (3, "Dual wield"), (4, "None") };

    private void OpenOffhandPicker(DamageRow r, Vector2 at)
    {
        Rule? own = _rules?.FindRule(r.Name);
        int ruleOff = own?.OffhandId ?? 0;
        uint sel = r.AssignedOff != 0 ? r.AssignedOff : ruleOff is 0 or 1 ? 0u : unchecked((uint)ruleOff);
        _pickItems.Clear();
        _pickItems.AddRange(RowOffhandModes);
        _pickItems.AddRange(_view!.WeaponChoices);
        uint wcid = r.Wcid, item = r.AssignedOff;
        string name = r.Name;
        OpenPicker(at, sel, id =>
        {
            if (id is >= 2 and <= 4)
            {
                DamageCommands.EditRule(name, rule => rule.OffhandId = (int)id);
                if (item != 0) DamageCommands.SetOffhand(wcid, 0);
            }
            else if (id == 0)
            {
                if (item != 0) DamageCommands.SetOffhand(wcid, 0);
                if (own != null && ruleOff != 0) DamageCommands.EditRule(name, rule => rule.OffhandId = 0);
            }
            else
            {
                DamageCommands.SetOffhand(wcid, id);
                if (own != null && ruleOff == 4) DamageCommands.EditRule(name, rule => rule.OffhandId = 0);
            }
        });
    }

    private string WeaponName(uint id)
    {
        if (_view != null)
            foreach (var c in _view.WeaponChoices)
                if (c.Id == id) return c.Name;
        return "Offhand " + id;
    }

    /// <summary>The rule a monster uses: its own, else Default. custom = it has its own.</summary>
    private Rule? RuleFor(string name, out bool custom)
    {
        custom = false;
        if (_rules == null || _rules.Parsed.Rules.Count == 0) return null;
        if (name.Equals("Default", StringComparison.OrdinalIgnoreCase)) return _rules.DefaultRule();
        Rule? exact = _rules.FindRule(name);
        custom = exact != null;
        return exact ?? _rules.DefaultRule();
    }

    /// <summary>Debuffs: a caret that opens the debuff popup, then the active ones as letters.</summary>
    private void DebuffCell(string name, Vector2 p, float ty)
    {
        Rule? view = RuleFor(name, out bool custom);
        var bp = new Vector2(p.X + ColX[ColDebuffs] + 2, p.Y + 2);
        if (IconButton("##debuffs", PhosphorIcons.CaretDown, bp, new Vector2(18, 18), White, EntryBg, EntryBorder) && view != null)
        {
            _debuffFor = name;
            _debuffPos = new Vector2(bp.X, bp.Y + 20);
            WriteUtf8(_customBuf, view.CustomDebuffs ?? "");
            _debuffOpen = true;
        }
        SafeImGui.SetItemTooltip("Debuffs to cast on " + name);
        if (view == null) return;
        string marks = DamageCommands.DebuffMarks(view);
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        dl.AddText(new Vector2(bp.X + 24, ty), custom || name == "Default" ? Gold : Dim, marks.Length > 0 ? Fit(marks, ColW[ColDebuffs] - 30) : "—");
        if (marks.Length > 0)
        {
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(bp.X + 22, p.Y));
            ImGuiNET.ImGui.InvisibleButton("##dmarks", new Vector2(Math.Max(1, ColW[ColDebuffs] - 26), RowH));
            SafeImGui.SetItemTooltip(DebuffTip(view));
        }
    }

    private static string DebuffTip(Rule v)
    {
        var parts = new List<string>();
        foreach (var (field, label, _) in DamageCommands.DebuffDefs) if (v.GetToggle(field)) parts.Add(label);
        foreach (var (field, _, tip) in DamageCommands.VoidDebuffDefs) if (v.GetToggle(field)) parts.Add(tip[..tip.IndexOf(':')]);
        if (!string.IsNullOrEmpty(v.ExVuln) && !v.ExVuln.Equals("None", StringComparison.OrdinalIgnoreCase)) parts.Add("Extra Vuln: " + v.ExVuln);
        if (!string.IsNullOrWhiteSpace(v.CustomDebuffs)) parts.Add("Yours: " + v.CustomDebuffs);
        return string.Join("\n", parts);
    }

    private void DebuffPopup()
    {
        if (_debuffOpen)
        {
            _debuffOpen = false;
            Vector2 display = ImGuiNET.ImGui.GetIO().DisplaySize;
            ImGuiNET.ImGui.SetNextWindowPos(new Vector2(Math.Clamp(_debuffPos.X, 0, Math.Max(0, display.X - 300)), _debuffPos.Y));
            ImGuiNET.ImGui.OpenPopup("##dmg_debuffs");
        }
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, PickerBg);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, Teal);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 5f);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 8));
        ImGuiNET.ImGui.SetNextWindowSize(new Vector2(300, 0));
        bool open = ImGuiNET.ImGui.BeginPopup("##dmg_debuffs");
        ImGuiNET.ImGui.PopStyleVar(2);
        ImGuiNET.ImGui.PopStyleColor(2);
        if (!open) return;

        string name = _debuffFor;
        Rule? view = RuleFor(name, out bool custom);
        bool isDefault = name.Equals("Default", StringComparison.OrdinalIgnoreCase);
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.UiBold11));
        SafeImGui.TextColored(RynthTheme.VecOf(RynthTheme.Accent), "Debuffs · " + name);
        ImGuiNET.ImGui.PopFont();
        if (view == null)
        {
            ImGuiNET.ImGui.TextColored(RynthTheme.VecOf(RynthTheme.MuteAlt), "Monster rules not loaded yet.");
            ImGuiNET.ImGui.EndPopup();
            return;
        }
        ImGuiNET.ImGui.PushTextWrapPos(0);
        SafeImGui.TextColored((custom || isDefault ? RynthTheme.Vec(0xFFFFD16A) : RynthTheme.VecOf(RynthTheme.MuteAlt)),
            isDefault ? "Every monster set to Default uses these." : custom ? "This monster's own debuffs." : "Following Default: a change here gives it its own.");
        ImGuiNET.ImGui.PopTextWrapPos();

        foreach (var (field, label, tip) in DamageCommands.DebuffDefs)
        {
            bool on = view.GetToggle(field);
            if (ImGuiNET.ImGui.Checkbox(label + "##db_" + field, ref on))
            {
                bool v = on;
                string f = field;
                DamageCommands.EditRule(name, rule => rule.SetToggle(f, v));
            }
            SafeImGui.SetItemTooltip(tip);
        }

        // The Void debuffs, two to a row.
        ImGuiNET.ImGui.TextColored(RynthTheme.VecOf(RynthTheme.MuteAlt), "Void Magic");
        for (int i = 0; i < DamageCommands.VoidDebuffDefs.Length; i++)
        {
            var (field, label, tip) = DamageCommands.VoidDebuffDefs[i];
            if (i % 2 == 1) ImGuiNET.ImGui.SameLine(140);
            bool on = view.GetToggle(field);
            if (ImGuiNET.ImGui.Checkbox(label + "##db_" + field, ref on))
            {
                bool v = on;
                string f = field;
                DamageCommands.EditRule(name, rule => rule.SetToggle(f, v));
            }
            SafeImGui.SetItemTooltip(tip);
        }

        ImGuiNET.ImGui.TextColored(RynthTheme.VecOf(RynthTheme.MuteAlt), "Extra Vuln");
        ImGuiNET.ImGui.SameLine();
        string ex = string.IsNullOrEmpty(view.ExVuln) ? "None" : view.ExVuln;
        ImGuiNET.ImGui.SetNextItemWidth(ImGuiNET.ImGui.CalcTextSize("Lightning").X + 40);
        if (ImGuiNET.ImGui.BeginCombo("##db_exvuln", ex))
        {
            foreach (string v in DamageCommands.ExVulnTypes)
                if (ImGuiNET.ImGui.Selectable(v, v == ex))
                {
                    string pick = v;
                    DamageCommands.EditRule(name, rule => rule.ExVuln = pick);
                }
            ImGuiNET.ImGui.EndCombo();
        }
        ImGuiNET.ImGui.SetItemTooltip("A vulnerability of another element, on top of Vuln (which follows the attack's element).");

        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
        ImGuiNET.ImGui.TextColored(RynthTheme.VecOf(RynthTheme.MuteAlt), "Your own (comma separated):");
        ImGuiNET.ImGui.SetNextItemWidth(-1);
        bool enter = ImGuiNET.ImGui.InputText("##db_custom", _customBuf, (uint)_customBuf.Length, ImGuiInputTextFlags.EnterReturnsTrue);
        if (enter || ImGuiNET.ImGui.IsItemDeactivatedAfterEdit())
        {
            string typed = Utf8(_customBuf).Trim().Trim(',').Trim();
            DamageCommands.EditRule(name, rule => rule.CustomDebuffs = typed);
        }
        ImGuiNET.ImGui.PushTextWrapPos(0);
        ImGuiNET.ImGui.TextColored(RynthTheme.Vec(0xFF8FA3B5),
            "A spell's base name casts the best tier you know (e.g. Corrosion Vulnerability Other); a full name casts that spell. Enter to save.");
        ImGuiNET.ImGui.PopTextWrapPos();

        if (custom)
        {
            ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
            if (ImGuiNET.ImGui.SmallButton("Back to Default")) { DamageCommands.ResetRule(name); ImGuiNET.ImGui.CloseCurrentPopup(); }
            ImGuiNET.ImGui.SetItemTooltip("Drop this monster's own settings (debuffs and spell shapes) and follow Default again.");
        }
        ImGuiNET.ImGui.EndPopup();
    }

    /// <summary>Text that opens something: hand cursor and underline on hover.</summary>
    private bool NameLink(string id, string text, Vector2 pos, float width, uint color, ImFontPtr font)
    {
        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGuiNET.ImGui.InvisibleButton(id, new Vector2(Math.Max(1, width), RowH));
        bool hot = ImGuiNET.ImGui.IsItemHovered();
        if (hot) ImGuiNET.ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        string shown = Fit(text, width);
        float ty = pos.Y + (RowH - font.FontSize) * 0.5f;
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        dl.AddText(font, font.FontSize, new Vector2(pos.X, ty), hot ? Teal : color, shown);
        if (hot)
        {
            float w = ImGuiNET.ImGui.CalcTextSize(shown).X;
            dl.AddLine(new Vector2(pos.X, ty + font.FontSize), new Vector2(pos.X + w, ty + font.FontSize), Teal);
        }
        return clicked;
    }

    /// <summary>
    /// A monster's Priority cell text: its own rule's priority (gold); else the name rule that
    /// covers it (as targeting finds it; an edit copies that rule, so its spells stay); else
    /// Default's (dim, like the Monster Detail page shows it).
    /// </summary>
    private void BuildPrio(string monster, RowText t)
    {
        if (_rules == null || _rules.Parsed.Rules.Count == 0) return;
        Rule? own = _rules.FindRule(monster);
        if (own != null)
        {
            t.Prio = own.Priority;
            t.PrioGold = true;
            t.PrioTip = "This monster's own priority: higher is attacked first (1 = normal). While a higher-priority monster is in range, lower ones wait.";
            return;
        }
        Rule? cover = DamageCommands.CoveringRule(_rules.Parsed.Rules, monster);
        if (cover != null)
        {
            t.Prio = cover.Priority;
            // A rule with a match expression only sometimes sets the spells, so a copy starts from Default.
            t.PrioCopyFrom = string.IsNullOrWhiteSpace(cover.MatchExpression) ? cover.Name : null;
            t.PrioTip = "From the name rule \"" + cover.Name + "\". A change here gives this monster its own rule, copied from "
                + (t.PrioCopyFrom != null ? "that one." : "Default.");
            return;
        }
        t.Prio = _rules.DefaultRule().Priority;
        t.PrioTip = "Following Default. A change here gives this monster its own rule (a copy of Default).\nHigher is attacked first (1 = normal).";
    }

    /// <summary>
    /// Priority: - / + and the number between them (type one, Enter to save). Edits the rule through
    /// DamageCommands.EditRule, like the Monster Detail page. Gold = the monster's own (or Default's).
    /// </summary>
    private void PrioCell(string key, string ruleName, string? copyFrom, RowText t, Vector2 p)
    {
        if (t.Prio == int.MinValue)
        {
            ImGuiNET.ImGui.GetWindowDrawList().AddText(new Vector2(p.X + ColX[ColPrio] + 4, p.Y + (RowH - ImGuiNET.ImGui.GetFontSize()) * 0.5f), Dim, "—");
            return;
        }
        if (!_prio.TryGetValue(key, out PrioEdit? e))
            _prio[key] = e = new PrioEdit();
        int prio = t.Prio;
        float x0 = p.X + ColX[ColPrio], w = ColW[ColPrio];
        const float bw = 16;
        bool buttons = w >= 2 * bw + 22;   // a narrow column keeps just the box

        if (buttons)
        {
            if (IconButton("##pdn", PhosphorIcons.Minus, new Vector2(x0 + 1, p.Y + 2), new Vector2(bw, 18), prio > 1 ? White : Dim, EntryBg, EntryBorder)
                && prio > 1)
                SetPriority(ruleName, copyFrom, prio - 1, e);
            ImGuiNET.ImGui.SetItemTooltip("Lower priority");
            if (IconButton("##pup", PhosphorIcons.Plus, new Vector2(x0 + w - bw - 1, p.Y + 2), new Vector2(bw, 18), White, EntryBg, EntryBorder)
                && prio < MaxPriority)
                SetPriority(ruleName, copyFrom, prio + 1, e);
            ImGuiNET.ImGui.SetItemTooltip("Higher priority");
        }

        if (!e.Active && e.Shown != prio)
        {
            WriteUtf8(e.Buffer, prio.ToString(CultureInfo.InvariantCulture));
            e.Shown = prio;
        }
        float fx = buttons ? x0 + bw + 3 : x0 + 1;
        float fw = buttons ? w - 2 * bw - 6 : w - 2;
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(fx, p.Y + 2));
        ImGuiNET.ImGui.SetNextItemWidth(Math.Max(8, fw));
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg, SearchBg);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, t.PrioGold ? Gold : Dim);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(2, (18 - ImGuiNET.ImGui.GetFontSize()) * 0.5f));
        bool enter = ImGuiNET.ImGui.InputText("##prio", e.Buffer, (uint)e.Buffer.Length,
            ImGuiInputTextFlags.CharsDecimal | ImGuiInputTextFlags.EnterReturnsTrue);
        e.Active = ImGuiNET.ImGui.IsItemActive();
        bool commit = enter || ImGuiNET.ImGui.IsItemDeactivatedAfterEdit();
        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.PopStyleColor(2);
        SafeImGui.SetItemTooltip(t.PrioTip + "\nType a number, Enter to save.");
        if (!commit) return;

        if (int.TryParse(Utf8(e.Buffer).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int typed))
        {
            typed = Math.Clamp(typed, 0, MaxPriority);
            if (typed != prio) SetPriority(ruleName, copyFrom, typed, e);
            else e.Shown = int.MinValue;
        }
        else e.Shown = int.MinValue;   // not a number: show the saved value again
    }

    private const int MaxPriority = 999;

    private static void SetPriority(string ruleName, string? copyFrom, int value, PrioEdit e)
    {
        DamageCommands.EditRule(ruleName, r => r.Priority = value, copyFrom);
        e.Shown = int.MinValue;   // show the plugin's value when it comes back
    }

    // "M" (manual HP on/off) + the HP box: gold and editable when manual, read-only otherwise.
    private void HpCell(DamageRow r, Vector2 p)
    {
        if (!_hp.TryGetValue(r.Key, out HpEdit? e))
            _hp[r.Key] = e = new HpEdit();
        bool manual = r.HpManual || e.ForceManual;
        if (r.HpManual) e.ForceManual = false;

        if (Button("##m", "M", new Vector2(p.X + ColX[ColHp] + 1, p.Y + 2), new Vector2(16, 18), manual ? Gold : Dim, EntryBg,
                manual ? Gold : EntryBorder))
        {
            if (r.HpManual) { DamageCommands.SetHp(r.Wcid, 0); e.ForceManual = false; }   // manual → auto
            else
            {
                if (r.Hp > 0) DamageCommands.SetHp(r.Wcid, r.Hp);                     // auto → manual, seeded
                e.ForceManual = true;
                e.FocusNext = true;
            }
        }
        ImGuiNET.ImGui.SetItemTooltip("Manual max-HP for this monster (off = auto)");

        // A tier not appraised yet shows tier 0's HP scaled by the tier, marked "~".
        bool estimate = r.HpEst && !manual;
        int shownKey = estimate ? -r.Hp - 1 : r.Hp;   // re-written when the estimate becomes a real value
        if (!e.Active && e.Shown != shownKey)
        {
            WriteUtf8(e.Buffer, r.Hp > 0 ? (estimate ? "~" : "") + r.Hp.ToString(CultureInfo.InvariantCulture) : "");
            e.Shown = shownKey;
        }
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X + ColX[ColHp] + 19, p.Y + 2));
        ImGuiNET.ImGui.SetNextItemWidth(ColW[ColHp] - 21);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg, SearchBg);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, manual ? Gold : estimate ? Dim : White);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(2, (18 - ImGuiNET.ImGui.GetFontSize()) * 0.5f));
        if (e.FocusNext) { ImGuiNET.ImGui.SetKeyboardFocusHere(); e.FocusNext = false; }
        ImGuiInputTextFlags flags = ImGuiInputTextFlags.CharsDecimal | ImGuiInputTextFlags.EnterReturnsTrue;
        if (!manual) flags |= ImGuiInputTextFlags.ReadOnly;
        bool enter = ImGuiNET.ImGui.InputText("##hp", e.Buffer, (uint)e.Buffer.Length, flags);
        e.Active = ImGuiNET.ImGui.IsItemActive();
        if (estimate)
            ImGuiNET.ImGui.SetItemTooltip("Estimated: tier 0's HP plus this tier's scaling. The first fight at this tier\n" +
                                          "reads the real number from the game and replaces it.");
        bool commit = manual && (enter || ImGuiNET.ImGui.IsItemDeactivatedAfterEdit());
        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.PopStyleColor(2);
        if (!commit) return;

        string typed = Utf8(e.Buffer).Trim();
        if (typed.Length == 0) DamageCommands.SetHp(r.Wcid, 0);
        else if (int.TryParse(typed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v > 0)
            DamageCommands.SetHp(r.Wcid, v);
        e.Shown = int.MinValue;   // show the plugin's value when it comes back
    }

    // =====================================================================
    //  Popups (the picker and the reset confirmation), in the panel window
    // =====================================================================

    private void OpenPicker(Vector2 pos, uint selected, Action<uint> onPick)
    {
        _pickPos = pos;
        _pickSel = selected;
        _pickAction = onPick;
        _pickOpen = true;
    }

    private void Popups()
    {
        DebuffPopup();
        if (_pickOpen)
        {
            _pickOpen = false;
            Vector2 display = ImGuiNET.ImGui.GetIO().DisplaySize;
            ImGuiNET.ImGui.SetNextWindowPos(new Vector2(Math.Clamp(_pickPos.X, 0, Math.Max(0, display.X - 240)), _pickPos.Y));
            ImGuiNET.ImGui.OpenPopup("##dmg_pick");
        }
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, PickerBg);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, Teal);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 4f);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(2, 2));
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 1));
        ImGuiNET.ImGui.SetNextWindowSize(new Vector2(240, Math.Min(_pickItems.Count * 23 + 4, 360)));
        bool open = ImGuiNET.ImGui.BeginPopup("##dmg_pick");
        ImGuiNET.ImGui.PopStyleVar(3);
        ImGuiNET.ImGui.PopStyleColor(2);
        if (open)
        {
            var pdl = ImGuiNET.ImGui.GetWindowDrawList();
            float iw = ImGuiNET.ImGui.GetContentRegionAvail().X;
            for (int i = 0; i < _pickItems.Count; i++)
            {
                (uint id, string name) = _pickItems[i];
                ImGuiNET.ImGui.PushID(i);
                Vector2 ip = ImGuiNET.ImGui.GetCursorScreenPos();
                bool click = ImGuiNET.ImGui.InvisibleButton("##it", new Vector2(iw, 22));
                bool hot = ImGuiNET.ImGui.IsItemHovered();
                ImGuiNET.ImGui.PopID();
                bool sel = id == _pickSel;
                pdl.AddRectFilled(ip, ip + new Vector2(iw, 22), sel ? PickSel : hot ? Lighten(EntryBg) : EntryBg);
                pdl.AddRect(ip, ip + new Vector2(iw, 22), EntryBorder);
                pdl.AddText(ip + new Vector2(6, (22 - ImGuiNET.ImGui.GetFontSize()) * 0.5f), sel ? Teal : White, name);
                if (click)
                {
                    Action<uint>? act = _pickAction;
                    ImGuiNET.ImGui.CloseCurrentPopup();
                    act?.Invoke(id);
                }
            }
            ImGuiNET.ImGui.EndPopup();
        }

        if (_removeOpen)
        {
            _removeOpen = false;
            Vector2 wp0 = ImGuiNET.ImGui.GetWindowPos(), ws0 = ImGuiNET.ImGui.GetWindowSize();
            ImGuiNET.ImGui.SetNextWindowPos(new Vector2(wp0.X + Math.Max(4, (ws0.X - 360) / 2), wp0.Y + Math.Max(8, (ws0.Y - 130) / 3)));
            ImGuiNET.ImGui.OpenPopup("##dmg_remove");
        }
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, PickerBg);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, Teal);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 6f);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14, 12));
        ImGuiNET.ImGui.SetNextWindowSize(new Vector2(360, 0));
        bool ropen = ImGuiNET.ImGui.BeginPopup("##dmg_remove");
        ImGuiNET.ImGui.PopStyleVar(2);
        ImGuiNET.ImGui.PopStyleColor(2);
        if (ropen)
        {
            ImGuiNET.ImGui.PushTextWrapPos(0);
            SafeImGui.TextColored(RynthTheme.VecOf(RynthTheme.Text), "Remove " + _removeName + "?");
            ImGuiNET.ImGui.Dummy(new Vector2(0, 4));
            ImGuiNET.ImGui.TextColored(RynthTheme.VecOf(RynthTheme.Text),
                "Its learned numbers and your settings for it (weapon, offhand, pet, HP) are deleted. It comes back if you meet it again.");
            ImGuiNET.ImGui.PopTextWrapPos();
            ImGuiNET.ImGui.Dummy(new Vector2(0, 6));
            float rw = ImGuiNET.ImGui.GetContentRegionAvail().X;
            Vector2 rp = ImGuiNET.ImGui.GetCursorScreenPos();
            if (Button("##remove_no", "Cancel", new Vector2(rp.X + rw - 176, rp.Y), new Vector2(80, 24), White, EntryBg, EntryBorder))
                ImGuiNET.ImGui.CloseCurrentPopup();
            if (Button("##remove_yes", PhosphorIcons.Trash + " Remove", new Vector2(rp.X + rw - 88, rp.Y), new Vector2(88, 24), White, ClearBg, ResetBorder))
            {
                DamageCommands.RemoveMonster(_removeWcid);
                ImGuiNET.ImGui.CloseCurrentPopup();
            }
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(rp.X, rp.Y + 24));
            ImGuiNET.ImGui.Dummy(new Vector2(0, 0));
            ImGuiNET.ImGui.EndPopup();
        }

        if (_resetOpen)
        {
            _resetOpen = false;
            Vector2 wp = ImGuiNET.ImGui.GetWindowPos(), ws = ImGuiNET.ImGui.GetWindowSize();
            ImGuiNET.ImGui.SetNextWindowPos(new Vector2(wp.X + Math.Max(4, (ws.X - 360) / 2), wp.Y + Math.Max(8, (ws.Y - 150) / 3)));
            ImGuiNET.ImGui.OpenPopup("##dmg_reset");
        }
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, PickerBg);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, Teal);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 6f);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14, 12));
        ImGuiNET.ImGui.SetNextWindowSize(new Vector2(360, 0));
        open = ImGuiNET.ImGui.BeginPopup("##dmg_reset");
        ImGuiNET.ImGui.PopStyleVar(2);
        ImGuiNET.ImGui.PopStyleColor(2);
        if (open)
        {
            ImGuiNET.ImGui.PushTextWrapPos(0);
            ImGuiNET.ImGui.TextColored(RynthTheme.VecOf(RynthTheme.Text), "Reset ALL learned damage statistics?");
            ImGuiNET.ImGui.Dummy(new Vector2(0, 4));
            ImGuiNET.ImGui.TextColored(RynthTheme.VecOf(RynthTheme.Text),
                "Monster names and your settings (manual HP, weapon/offhand picks, debuffs) are KEPT — only the learned damage, crit, casts-to-kill, kills and HP are cleared, and the evades and resists under each monster start over. This cannot be undone.");
            ImGuiNET.ImGui.PopTextWrapPos();
            ImGuiNET.ImGui.Dummy(new Vector2(0, 6));
            float w = ImGuiNET.ImGui.GetContentRegionAvail().X;
            Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
            if (Button("##reset_no", "Cancel", new Vector2(p.X + w - 176, p.Y), new Vector2(80, 24), White, EntryBg, EntryBorder))
                ImGuiNET.ImGui.CloseCurrentPopup();
            if (Button("##reset_yes", "Clear stats", new Vector2(p.X + w - 88, p.Y), new Vector2(88, 24), White, ClearBg, ResetBorder))
            {
                DamageCommands.ClearStats();
                ImGuiNET.ImGui.CloseCurrentPopup();
            }
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + 24));
            ImGuiNET.ImGui.Dummy(new Vector2(0, 0));
            ImGuiNET.ImGui.EndPopup();
        }
    }

    // =====================================================================
    //  Widgets
    // =====================================================================

    private static bool Button(string id, string label, Vector2 pos, Vector2 size, uint fg, uint bg, uint border,
        bool enabled = true, bool leftAlign = false)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGuiNET.ImGui.InvisibleButton(id, size) && enabled;
        bool hot = enabled && ImGuiNET.ImGui.IsItemHovered();
        if (bg != 0 || hot)
            dl.AddRectFilled(pos, pos + size, enabled && ImGuiNET.ImGui.IsItemActive() ? Darken(bg) : hot ? Lighten(bg == 0 ? EntryBg : bg) : bg, 3);
        if (border != 0) dl.AddRect(pos, pos + size, border, 3);
        Vector2 ts = ImGuiNET.ImGui.CalcTextSize(label);
        float x = leftAlign ? pos.X + 5 : pos.X + (size.X - ts.X) * 0.5f;
        dl.PushClipRect(pos, pos + size, true);
        dl.AddText(new Vector2(x, pos.Y + (size.Y - ts.Y) * 0.5f), enabled ? fg : Dim, label);
        dl.PopClipRect();
        return clicked;
    }

    /// <summary>Button's look with a Phosphor icon centred on it (Ui11: about 10 px, for the table's 18 px buttons).</summary>
    private static bool IconButton(string id, string icon, Vector2 pos, Vector2 size, uint fg, uint bg, uint border)
    {
        bool clicked = Button(id, string.Empty, pos, size, fg, bg, border);
        PhosphorIcons.DrawCentered(ImGuiNET.ImGui.GetWindowDrawList(), ImGuiFonts.Get(UiFont.Ui11), icon, pos, size, fg);
        return clicked;
    }

    private static bool FlowButton(string id, string label, uint fg, uint bg, uint border, float height, bool enabled = true)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        var size = new Vector2(ImGuiNET.ImGui.CalcTextSize(label).X + 12, height);
        bool clicked = Button(id, label, p, size, fg, bg, border, enabled);
        ImGuiNET.ImGui.SetCursorScreenPos(p);
        ImGuiNET.ImGui.Dummy(size);
        return clicked;
    }

    /// <summary>A Weapon/Offhand/Pet cell button: gold when set by you, dim when automatic.</summary>
    private static bool CellPicker(string id, string text, bool gold, Vector2 rowPos, int col)
    {
        var pos = new Vector2(rowPos.X + ColX[col] + 1, rowPos.Y + 2);
        return Button(id, text, pos, new Vector2(ColW[col] - 2, 18), gold ? Gold : Dim, EntryBg, EntryBorder, leftAlign: true);
    }

    private string Fit(string text, float width)
    {
        if (_fit.TryGetValue(text, out var hit) && Math.Abs(hit.Width - width) < 0.5f) return hit.Shown;
        string shown = text;
        if (ImGuiNET.ImGui.CalcTextSize(text).X > width)
        {
            int n = text.Length;
            while (n > 0 && ImGuiNET.ImGui.CalcTextSize(text.Substring(0, n) + "…").X > width) n--;
            shown = text.Substring(0, n) + "…";
        }
        _fit[text] = (width, shown);
        return shown;
    }

    private static string Trunc(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s.Substring(0, max - 1) + "…";

    private static string Utf8(byte[] buffer)
    {
        int len = Array.IndexOf(buffer, (byte)0);
        return Encoding.UTF8.GetString(buffer, 0, len < 0 ? buffer.Length : len);
    }

    private static void WriteUtf8(byte[] buffer, string text)
    {
        int n = Encoding.UTF8.GetBytes(text, 0, Math.Min(text.Length, buffer.Length - 1), buffer, 0);
        buffer[Math.Min(n, buffer.Length - 1)] = 0;
    }

    private static uint Lighten(uint abgr) => Scale(abgr, 1.25f, 12);
    private static uint Darken(uint abgr) => Scale(abgr, 0.8f, 0);

    private static uint Scale(uint abgr, float k, int add)
    {
        uint a = abgr & 0xFF000000;
        uint r = (uint)Math.Min(255, (int)((abgr & 0xFF) * k) + add);
        uint g = (uint)Math.Min(255, (int)(((abgr >> 8) & 0xFF) * k) + add);
        uint b = (uint)Math.Min(255, (int)(((abgr >> 16) & 0xFF) * k) + add);
        return a | (b << 16) | (g << 8) | r;
    }
}
