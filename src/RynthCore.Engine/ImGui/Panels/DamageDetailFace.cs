// ============================================================================
//  RynthCore.Engine - ImGui/Panels/DamageDetailFace.cs
//  "Monster Detail": everything the Damage panel knows about one monster,
//  opened by clicking its name there. The Damage panel keeps only what you
//  set and sort by; the numbers behind it live here:
//    header      HP (and where it came from), seconds per kill, hit rate
//    Weak to     all eight elements as damage taken (1.0 = full) + source
//    Accuracy    per weapon: hits, misses, hit %, seconds per kill
//    Damage      per weapon / element / spell tier: avg, crit, non-crit,
//                crit rate, casts per kill, kills
//    Summons     kills and seconds per kill per summon element (and none)
//    Damage taken  per element: hits, average, biggest
//    Rule        priority, damage type, extra vuln, match expression, spell
//                shapes (debuffs: the Damage panel's caret)
//  "Default" shows only Rule: the settings every monster on Default uses.
//  A name rule (wcid 0 + a rule name, from the Damage panel's Name rules,
//  e.g. "Olthoi") shows only Rule, plus its debuffs, off hand, pet, rename
//  and delete: it has no row in the Damage panel to hold them.
//
//  Data: UiSources.MonsterDetail (the selected wcid) and MonsterRules; edits
//  go through DamageCommands on the pump thread. No Avalonia twin; pops out as ImGui.
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

internal sealed class DamageDetailFace : IImGuiPanel
{
    public const string Title = "Monster Detail";

    public static void Register() => ImGuiPanelHost.Register(Title,
        new PanelSpec(new Vector2(560, 620), new Vector2(360, 240)),
        () => new DamageDetailFace());

    private static string _selectedName = "";

    /// <summary>
    /// Shows <paramref name="wcid"/> in the Monster Detail panel, opening it. wcid 0 shows a
    /// rule only: <paramref name="name"/>'s (a name rule, or a monster not met yet), or the
    /// Default settings when the name is empty or "Default".
    /// </summary>
    public static void Show(uint wcid, string name)
    {
        _selectedName = wcid == 0 && (string.IsNullOrWhiteSpace(name) || IsDefaultName(name)) ? "Default" : name;
        MonsterDetailSource.SelectedWcid = wcid;
        _showCount++;
        UiSources.MonsterDetail.RequestRefresh();
        ImGuiPanelHost.Open(Title);
    }

    private static uint C(uint argb) => RynthTheme.Argb(argb);
    private static readonly uint Gold = C(0xFFFFD16A), ToggleOn = C(0xFF33CC66), ToggleOff = C(0xFF4D4D4D),
        BarBg = C(0xFF16222E), Good = C(0xFF33CC66), Bad = C(0xFFE05A5A), DeleteBg = C(0xFF6E1E1E),
        DeleteBorder = C(0xFFFF8A8A);
    private static uint Teal => RynthTheme.Accent;
    private static uint Dim => RynthTheme.MuteAlt;
    private static uint White => RynthTheme.Text;
    private static uint EntryBg => RynthTheme.Button;
    private static uint EntryBorder => RynthTheme.Border;
    private static uint Mid => RynthTheme.Highlight;

    private MonsterDetail? _detail;
    private long _seenVersion = -1, _seenRulesVersion = -1;
    private MonsterRulesSnapshot? _rules;
    private bool _confirmRemove;
    private static int _showCount;
    private int _seenShow = -1;

    // The Rule section's edit boxes: whose rule they hold, the saved values they were
    // last loaded from, and whether one is being typed in.
    private string _bufFor = "";
    private int _prioSeen = int.MinValue;
    private string? _exprSeen;
    private bool _prioActive, _exprActive;
    private readonly byte[] _prioBuf = new byte[12], _exprBuf = new byte[512], _renameBuf = new byte[128];
    private string? _renaming;   // a rename just sent: "Loading…" until the rule comes back under it

    public void OnShown()
    {
        UiSources.MonsterDetail.Subscribe();
        UiSources.MonsterRules.Subscribe();
        UiSources.MonsterDetail.RequestRefresh();
        UiSources.MonsterRules.RequestRefresh();
    }

    public void OnHidden()
    {
        UiSources.MonsterDetail.Unsubscribe();
        UiSources.MonsterRules.Unsubscribe();
    }

    public void Draw()
    {
        var snap = UiSources.MonsterDetail.Current;
        if (snap != null && snap.Version != _seenVersion) { _seenVersion = snap.Version; _detail = snap.Value; }
        var rules = UiSources.MonsterRules.Current;
        if (rules != null && rules.Version != _seenRulesVersion) { _seenRulesVersion = rules.Version; _rules = rules.Value; }

        if (_seenShow != _showCount) { _seenShow = _showCount; _confirmRemove = false; }   // another monster: no stale confirm
        uint wcid = MonsterDetailSource.SelectedWcid;
        bool isDefault = wcid == 0 && IsDefaultName(_selectedName);
        bool nameRule = wcid == 0 && !isDefault;
        MonsterDetail? d = _detail != null && _detail.Wcid == wcid && wcid != 0 ? _detail : null;

        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui11));
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6, 4));

        // ── Header ───────────────────────────────────────────────────────
        string name = d != null && d.Name.Length > 0 ? d.Name : _selectedName;
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.UiBold11));
        SafeImGui.TextColored(Vec(Teal), isDefault ? "DEFAULT" : name);
        ImGuiNET.ImGui.PopFont();
        if (isDefault)
        {
            ImGuiNET.ImGui.TextColored(Vec(Dim), "The spell settings every monster set to Default uses.");
        }
        else if (nameRule)
        {
            NameRuleHeader(name);
        }
        else if (d == null)
        {
            SafeImGui.TextColored(Vec(Dim), _detail != null && !_detail.Bound ? "Waiting for RynthAi…" : "Loading…");
        }
        else
        {
            ImGuiNET.ImGui.SameLine();
            SafeImGui.TextColored(Vec(Dim), "wcid " + wcid.ToString(CultureInfo.InvariantCulture));
            // Where the server plugin reports tiers: which difficulty tier these numbers are (the Damage panel's chip picks it).
            if (UiSources.Damage.Current?.Value.Tier is { Show: true } tier)
            {
                ImGuiNET.ImGui.SameLine();
                SafeImGui.TextColored(Vec(tier.View == tier.Current ? Teal : Gold),
                    tier.View <= 0 ? "· Tier 0 (unscaled)" : "· Tier " + tier.View.ToString(CultureInfo.InvariantCulture));
                ImGuiNET.ImGui.SetItemTooltip("HP, casts to kill, seconds per kill, hits and damage taken are this difficulty tier's.\n" +
                                              "Damage per cast and weaknesses are the same at every tier. Change it on the Damage panel.");
            }
            string hp = d.Hp > 0 ? $"HP {d.Hp:N0} ({d.HpSrc})" : "HP unknown";
            string sec = d.SecKill > 0 ? $"{d.SecKill:0.0} s per kill" : "no timed kills yet";
            string hit = d.HitRate >= 0 ? $"{d.HitRate * 100:0}% hits" : "no hits yet";
            SafeImGui.TextColored(Vec(White), $"{hp}   ·   {sec}   ·   {hit}");

            float bw = ImGuiNET.ImGui.CalcTextSize(RemoveLabel).X + 16;
            ImGuiNET.ImGui.SameLine();
            float room = ImGuiNET.ImGui.GetContentRegionAvail().X;
            if (room > bw) ImGuiNET.ImGui.SetCursorPosX(ImGuiNET.ImGui.GetCursorPosX() + room - bw);
            Vector2 bp = ImGuiNET.ImGui.GetCursorScreenPos();
            if (FaceKit.Button("##remove", RemoveLabel, bp, new Vector2(bw, ImGuiNET.ImGui.GetFrameHeight()), White, DeleteBg, border: DeleteBorder))
                _confirmRemove = true;
            ImGuiNET.ImGui.SetItemTooltip("Take this monster off the Damage panel: its learned numbers and your settings for it. It comes back if you meet it again.");
            if (_confirmRemove)
            {
                SafeImGui.TextColored(Vec(Gold), "Remove " + name + " and everything learned about it?");
                ImGuiNET.ImGui.SameLine();
                if (ImGuiNET.ImGui.SmallButton("Remove##yes"))
                {
                    DamageCommands.RemoveMonster(wcid);
                    _confirmRemove = false;
                    ImGuiPanelHost.Close(Title);
                }
                ImGuiNET.ImGui.SameLine();
                if (ImGuiNET.ImGui.SmallButton("Cancel##no")) _confirmRemove = false;
            }

            WeakSection(d);
            AccuracySection(d);
            DamageSection(d);
            SummonSection(d);
            TakenSection(d);
        }

        RuleSection(isDefault ? "Default" : name, nameRule);

        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.PopFont();
    }

    private static bool IsDefaultName(string name) => name.Equals("Default", StringComparison.OrdinalIgnoreCase);

    // A name rule's header: what it matches, and Delete rule (confirmed).
    private void NameRuleHeader(string name)
    {
        Rule? rule = _rules?.FindRule(name);
        if (rule != null) _renaming = null;
        ImGuiNET.ImGui.PushTextWrapPos(0);
        SafeImGui.TextColored(Vec(Dim), rule == null
            ? (_rules == null || _renaming == name ? "Loading…" : "There is no rule with this name (deleted?).")
            : "A name rule: used for every monster whose name contains \"" + name + "\""
              + (string.IsNullOrWhiteSpace(rule.MatchExpression) ? "" : " and its match expression is true")
              + ", unless a rule higher in the Name rules list matches it first.");
        ImGuiNET.ImGui.PopTextWrapPos();
        if (rule == null) return;

        Vector2 bp = ImGuiNET.ImGui.GetCursorScreenPos();
        float bw = ImGuiNET.ImGui.CalcTextSize(DeleteRuleLabel).X + 16;
        if (FaceKit.Button("##delrule", DeleteRuleLabel, bp, new Vector2(bw, ImGuiNET.ImGui.GetFrameHeight()), White, DeleteBg, border: DeleteBorder))
            _confirmRemove = true;
        ImGuiNET.ImGui.SetItemTooltip("Delete this rule. Monsters it covered go back to Default (or another rule that matches).");
        if (!_confirmRemove) return;
        SafeImGui.TextColored(Vec(Gold), "Delete the rule " + name + "?");
        ImGuiNET.ImGui.SameLine();
        if (ImGuiNET.ImGui.SmallButton("Delete##yes"))
        {
            DamageCommands.ResetRule(name);
            _confirmRemove = false;
            ImGuiPanelHost.Close(Title);
        }
        ImGuiNET.ImGui.SameLine();
        if (ImGuiNET.ImGui.SmallButton("Cancel##no")) _confirmRemove = false;
    }

    // ── Sections ─────────────────────────────────────────────────────────

    private static bool Section(string title)
    {
        ImGuiNET.ImGui.Dummy(new Vector2(0, 4));
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Header, EntryBg);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.HeaderHovered, FaceKit.Lighten(EntryBg));
        bool open = ImGuiNET.ImGui.CollapsingHeader(title, ImGuiTreeNodeFlags.DefaultOpen);
        ImGuiNET.ImGui.PopStyleColor(2);
        return open;
    }

    private static void WeakSection(MonsterDetail d)
    {
        if (!Section("Weak to")) return;
        if (d.Weak.Count == 0)
        {
            ImGuiNET.ImGui.TextColored(Vec(Dim), "Nothing known yet: not in the server data, no creature type, nothing learned.");
            return;
        }
        SafeImGui.TextColored(Vec(Dim), "Damage it takes from each element (1.0 = full, 0.5 = half), from " + d.WeakSrc + ".");
        float labelW = ImGuiNET.ImGui.CalcTextSize("Lightning  ").X;
        float barW = Math.Max(80, ImGuiNET.ImGui.GetContentRegionAvail().X - labelW - ImGuiNET.ImGui.CalcTextSize(" 0.00x").X - 8);
        double top = 1.0;
        foreach (var (_, m) in d.Weak) if (m > top) top = m;
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        float h = ImGuiNET.ImGui.GetTextLineHeight();
        foreach (var (e, m) in d.Weak)
        {
            Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
            dl.AddText(p, White, e);
            Vector2 b0 = p + new Vector2(labelW, 2), b1 = b0 + new Vector2(barW, h - 4);
            dl.AddRectFilled(b0, b1, BarBg, 2);
            if (m >= 0)
            {
                float f = (float)Math.Clamp(m / top, 0, 1);
                uint col = m >= 1.0 ? Good : m >= 0.6 ? Mid : Bad;
                dl.AddRectFilled(b0, b0 + new Vector2(barW * f, h - 4), col, 2);
                dl.AddText(new Vector2(b1.X + 6, p.Y), White, m.ToString("0.00", CultureInfo.InvariantCulture) + "x");
            }
            else
                dl.AddText(new Vector2(b1.X + 6, p.Y), Dim, "learned");
            ImGuiNET.ImGui.Dummy(new Vector2(labelW + barW + 60, h));
        }
    }

    private static void AccuracySection(MonsterDetail d)
    {
        if (!Section("Accuracy and speed")) return;
        if (d.Weapons.Count == 0) { ImGuiNET.ImGui.TextColored(Vec(Dim), "No attacks recorded yet."); return; }
        if (!BeginTable("##acc", "Weapon", "Hit %", "Hits", "Misses", "Sec/Kill", "Timed kills")) return;
        foreach (var (w, hits, misses, sec, secN) in d.Weapons)
        {
            ImGuiNET.ImGui.TableNextRow();
            Cell(w);
            Cell(hits + misses > 0 ? $"{100.0 * hits / (hits + misses):0}%" : "—");
            Cell(hits.ToString(CultureInfo.InvariantCulture));
            Cell(misses.ToString(CultureInfo.InvariantCulture));
            Cell(sec > 0 ? sec.ToString("0.0", CultureInfo.InvariantCulture) : "—");
            Cell(secN.ToString(CultureInfo.InvariantCulture));
        }
        ImGuiNET.ImGui.EndTable();
    }

    private static void DamageSection(MonsterDetail d)
    {
        if (!Section("Damage")) return;
        if (d.Casts.Count == 0) { ImGuiNET.ImGui.TextColored(Vec(Dim), "No damage recorded yet."); return; }
        if (!BeginTable("##dmg", "Weapon", "Element", "Tier", "Avg", "Crit", "Non-crit", "Crit %", "Casts/Kill", "Kills")) return;
        foreach (DetailCast c in d.Casts)
        {
            ImGuiNET.ImGui.TableNextRow();
            Cell(c.Weapon);
            Cell(c.Elem);
            Cell(DamageSource.FormatTier(c.Tier));
            Cell(c.Hits > 0 ? c.Avg.ToString("0", CultureInfo.InvariantCulture) : "—");
            Cell(c.CritN > 0 ? c.Crit.ToString("0", CultureInfo.InvariantCulture) : "—");
            Cell(c.NonCritN > 0 ? c.NonCrit.ToString("0", CultureInfo.InvariantCulture) : "—");
            Cell(c.CritN + c.NonCritN > 0 ? $"{100.0 * c.CritN / (c.CritN + c.NonCritN):0}%" : "—");
            Cell(c.Kills > 0 ? c.Casts.ToString("0.00", CultureInfo.InvariantCulture) : "—");
            Cell(c.Kills.ToString(CultureInfo.InvariantCulture));
        }
        ImGuiNET.ImGui.EndTable();
    }

    private static void SummonSection(MonsterDetail d)
    {
        if (!Section("Summons")) return;
        if (d.Summons.Count == 0)
        {
            ImGuiNET.ImGui.TextColored(Vec(Dim), "No timed kills yet. (The game doesn't report a summon's own damage, so this compares kill times.)");
            return;
        }
        if (!BeginTable("##pets", "Summon", "Kills", "Sec/Kill")) return;
        foreach (var (e, kills, sec) in d.Summons)
        {
            ImGuiNET.ImGui.TableNextRow();
            Cell(e);
            Cell(kills.ToString(CultureInfo.InvariantCulture));
            Cell(sec > 0 ? sec.ToString("0.0", CultureInfo.InvariantCulture) : "—");
        }
        ImGuiNET.ImGui.EndTable();
    }

    private static void TakenSection(MonsterDetail d)
    {
        if (!Section("Damage taken")) return;
        if (d.Taken.Count == 0) { ImGuiNET.ImGui.TextColored(Vec(Dim), "It hasn't hit you yet."); return; }
        if (!BeginTable("##taken", "Element", "Hits", "Average", "Biggest")) return;
        foreach (var (e, hits, avg, max) in d.Taken)
        {
            ImGuiNET.ImGui.TableNextRow();
            Cell(e);
            Cell(hits.ToString(CultureInfo.InvariantCulture));
            Cell(avg.ToString("0", CultureInfo.InvariantCulture));
            Cell(max.ToString("0", CultureInfo.InvariantCulture));
        }
        ImGuiNET.ImGui.EndTable();
    }

    // The rule a monster (or Default, or a name rule) uses: priority, damage type, extra vuln,
    // match expression, spell shapes; a name rule also debuffs, off hand, pet and its name.
    // A monster following Default gets its own rule (a copy of Default) on the first change.
    private void RuleSection(string name, bool nameRule)
    {
        if (!Section("Rule")) return;
        if (_rules == null || _rules.Parsed.Rules.Count == 0)
        {
            ImGuiNET.ImGui.TextColored(Vec(Dim), "Monster rules not loaded yet.");
            return;
        }
        bool isDefault = IsDefaultName(name);
        Rule? exact = isDefault ? null : _rules.FindRule(name);
        if (nameRule && exact == null) return;   // deleted: editing would make it again
        bool custom = exact != null;
        Rule view = isDefault ? _rules.DefaultRule() : exact ?? _rules.DefaultRule();

        if (!nameRule)
        {
            SafeImGui.TextColored(Vec(custom || isDefault ? Gold : Dim),
                isDefault ? "Applies to every monster set to Default." : custom ? "Custom settings for this monster." : "Following Default (a change here gives it its own settings).");
            SafeImGui.TextColored(Vec(Dim), DebuffsHint);
            if (custom)
            {
                ImGuiNET.ImGui.SameLine();
                if (ImGuiNET.ImGui.SmallButton("Back to Default")) DamageCommands.ResetRule(name);
            }
        }

        // The edit boxes reload from the rule when its saved value changes (not every frame,
        // so a value just entered stays while the plugin saves it).
        if (_bufFor != name)
        {
            _bufFor = name;
            _prioSeen = int.MinValue;
            _exprSeen = null;
            WriteUtf8(_renameBuf, name);
        }

        // Priority
        ImGuiNET.ImGui.AlignTextToFramePadding();
        ImGuiNET.ImGui.TextColored(Vec(Dim), "Priority");
        ImGuiNET.ImGui.SameLine();
        if (!_prioActive && _prioSeen != view.Priority)
        {
            WriteUtf8(_prioBuf, view.Priority.ToString(CultureInfo.InvariantCulture));
            _prioSeen = view.Priority;
        }
        ImGuiNET.ImGui.SetNextItemWidth(ImGuiNET.ImGui.CalcTextSize("0000").X + 12);
        bool enter = ImGuiNET.ImGui.InputText("##prio", _prioBuf, (uint)_prioBuf.Length,
            ImGuiInputTextFlags.CharsDecimal | ImGuiInputTextFlags.EnterReturnsTrue);
        _prioActive = ImGuiNET.ImGui.IsItemActive();
        if (enter || ImGuiNET.ImGui.IsItemDeactivatedAfterEdit())
        {
            if (int.TryParse(Utf8(_prioBuf).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int prio))
            {
                if (prio != view.Priority) DamageCommands.EditRule(name, r => r.Priority = prio);
            }
            else _prioSeen = int.MinValue;   // not a number: show the saved value again
        }
        ImGuiNET.ImGui.SetItemTooltip("Target priority: while a higher-priority monster is in range it is attacked first, always (1 = normal). Enter to save.");

        // Damage type and Extra vuln
        ImGuiNET.ImGui.SameLine(0, 14);
        Combo("Damage", "##dtype", DamageCommands.DamageTypes, string.IsNullOrEmpty(view.DamageType) ? "Auto" : view.DamageType,
            pick => DamageCommands.EditRule(name, r => r.DamageType = pick),
            "Element for spells and weapons against it. Auto = the one it's weakest to.");
        ImGuiNET.ImGui.SameLine(0, 14);
        Combo("Extra vuln", "##exvuln", DamageCommands.ExVulnTypes, string.IsNullOrEmpty(view.ExVuln) ? "None" : view.ExVuln,
            pick => DamageCommands.EditRule(name, r => r.ExVuln = pick),
            "A vulnerability of another element, on top of Vuln (which follows the attack's element).");

        // Melee/missile attack power against it
        string power = string.IsNullOrEmpty(view.AttackPower) ? "Default" : view.AttackPower;
        Combo("Power", "##power", DamageCommands.AttackPowerModes, power,
            pick => DamageCommands.EditRule(name, r => r.AttackPower = pick),
            (isDefault
                ? "Melee/missile attack power for every monster without its own. Default = the Power settings (Melee Combat).\n"
                : "Melee/missile attack power against the monsters it covers. Default = the DEFAULT line's.\n") +
            "High = 100%. Reckless = 80% (needs Recklessness trained, else 100%).\n" +
            "Variable = a new random power each attack, between Variable Min and Max (Settings).\n" +
            "Reckless + Variable = random, kept inside the Recklessness band (20-80%).");

        // Pull-away kite: tag then run until the mob is clear of its spawn pack
        ImGuiNET.ImGui.SameLine(0, 14);
        string kite = string.IsNullOrEmpty(view.Kite) ? "Default" : view.Kite;
        Combo("Kite", "##kite", DamageCommands.KiteModes, kite,
            pick => DamageCommands.EditRule(name, r => r.Kite = pick),
            (isDefault
                ? "Pull-away for every monster without its own. Default = Off (fight in place).\n"
                : "Pull-away for the monsters it covers. Default = the DEFAULT line's.\n") +
            "Off = kill where it stands. PullAway = tag, run until it has been dragged\n" +
            "Kite Pull Distance metres from its lock spot (Settings > Combat), then kill.");

        // The DEFAULT line's own choice is the Prefer chip in the Damage panel's top bar.
        if (!isDefault)
            Combo("Prefer", "##prefer", DamageCommands.PreferredDamages, string.IsNullOrEmpty(view.PreferredDamage) ? "Auto" : view.PreferredDamage,
                pick => DamageCommands.EditRule(name, r => r.PreferredDamage = pick),
                "The kind of damage to fight this monster with, for a character who uses more than one kind.\n" +
                "Auto = the Prefer chip's choice in the Damage panel's top bar.\n" +
                "Melee / Missile / War Magic / Void Magic = only weapons of that kind are wielded; with none\n" +
                "listed it falls back to Auto. A weapon you set for the monster in the Damage panel still wins.");

        // Match expression (Default never matches by name, so it has none)
        if (!isDefault)
        {
            ImGuiNET.ImGui.AlignTextToFramePadding();
            ImGuiNET.ImGui.TextColored(Vec(Dim), "Match");
            ImGuiNET.ImGui.SameLine();
            string expr = view.MatchExpression ?? "";
            if (!_exprActive && _exprSeen != expr)
            {
                WriteUtf8(_exprBuf, expr);
                _exprSeen = expr;
            }
            ImGuiNET.ImGui.SetNextItemWidth(-1);
            bool exprEnter = ImGuiNET.ImGui.InputText("##expr", _exprBuf, (uint)_exprBuf.Length, ImGuiInputTextFlags.EnterReturnsTrue);
            _exprActive = ImGuiNET.ImGui.IsItemActive();
            if (exprEnter || ImGuiNET.ImGui.IsItemDeactivatedAfterEdit())
            {
                string typed = Utf8(_exprBuf).Trim();
                if (typed != expr) DamageCommands.EditRule(name, r => r.MatchExpression = typed);
            }
            ImGuiNET.ImGui.SetItemTooltip("Only when this is true as well (empty = by name only). Enter to save.\n" +
                "Variables: name, range, typeid, maxhp, metastate, true, false\nExamples: range>5, maxhp>1000");
        }

        ToggleRow("Shapes", DamageCommands.ShapeDefs, view, name);
        string casts = DamageCommands.BaseShapeName(view);
        if (casts.Length > 0)
        {
            ImGuiNET.ImGui.SameLine(0, 10);
            ImGuiNET.ImGui.AlignTextToFramePadding();
            SafeImGui.TextColored(Vec(Dim), "casts " + casts + (view.UseRing ? ", Ring when enough are near" : ""));
            ImGuiNET.ImGui.SetItemTooltip("With several shapes on, the first of Arc, Streak, Blast, Bolt is cast.\n" +
                "Ring replaces it when at least Min Ring Targets monsters are within Ring Range (Settings).");
        }
        if (!nameRule) return;

        // A name rule has no Damage panel row: its debuffs, off hand and pet are set here.
        ToggleRow("Debuffs", DamageCommands.DebuffDefs, view, name);
        ToggleRow("Void", DamageCommands.VoidDebuffDefs, view, name);
        int off = view.OffhandId;
        string offText = off >= 0 && off < DamageCommands.OffhandModeNames.Length ? DamageCommands.OffhandModeNames[off] : "Item " + unchecked((uint)off);
        Combo("Off hand", "##offhand", DamageCommands.OffhandModeNames, offText,
            pick => DamageCommands.EditRule(name, r => r.OffhandId = Array.IndexOf(DamageCommands.OffhandModeNames, pick)),
            "Off hand against the monsters it covers. Default = the DEFAULT line's.\n" +
            "Auto = a listed shield; Shield; Dual wield (needs the skill); None = leave the off hand alone.");
        ImGuiNET.ImGui.SameLine(0, 14);
        string pet = string.IsNullOrEmpty(view.PetDamage) || view.PetDamage.Equals("PAuto", StringComparison.OrdinalIgnoreCase) ? "Auto" : view.PetDamage;
        Combo("Pet", "##pet", PetLabels, pet,
            pick => DamageCommands.EditRule(name, r => r.PetDamage = pick == "Auto" ? "PAuto" : pick),
            "Summon element against the monsters it covers. Auto = its Damage type, else the DEFAULT line's.");

        ImGuiNET.ImGui.AlignTextToFramePadding();
        ImGuiNET.ImGui.TextColored(Vec(Dim), "Name");
        ImGuiNET.ImGui.SameLine();
        ImGuiNET.ImGui.SetNextItemWidth(Math.Min(260, ImGuiNET.ImGui.GetContentRegionAvail().X));
        bool renamed = ImGuiNET.ImGui.InputText("##rename", _renameBuf, (uint)_renameBuf.Length, ImGuiInputTextFlags.EnterReturnsTrue);
        if (renamed || ImGuiNET.ImGui.IsItemDeactivatedAfterEdit())
        {
            string to = Utf8(_renameBuf).Trim();
            if (to.Length > 0 && to != name && !IsDefaultName(to) && (_rules.FindRule(to) == null || to.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                DamageCommands.EditRule(name, r => r.Name = to);
                _selectedName = to;
                _bufFor = to;
                _renaming = to;
            }
            else WriteUtf8(_renameBuf, name);
        }
        ImGuiNET.ImGui.SetItemTooltip("The part of a monster's name this rule looks for. Enter to rename.");
    }

    private static readonly string[] PetLabels =
        { "Auto", "Slash", "Pierce", "Bludgeon", "Fire", "Cold", "Lightning", "Acid", "Nether" };

    /// <summary>A label and a combo of <paramref name="items"/>; <paramref name="onPick"/> runs when another one is chosen.</summary>
    private static void Combo(string label, string id, string[] items, string current, Action<string> onPick, string tip)
    {
        ImGuiNET.ImGui.AlignTextToFramePadding();
        SafeImGui.TextColored(Vec(Dim), label);
        ImGuiNET.ImGui.SameLine();
        ImGuiNET.ImGui.SetNextItemWidth(ImGuiNET.ImGui.CalcTextSize("Dual wield").X + 34);
        if (ImGuiNET.ImGui.BeginCombo(id, current))
        {
            foreach (string v in items)
                if (ImGuiNET.ImGui.Selectable(v, v.Equals(current, StringComparison.OrdinalIgnoreCase))
                    && !v.Equals(current, StringComparison.OrdinalIgnoreCase))
                    onPick(v);
            ImGuiNET.ImGui.EndCombo();
        }
        SafeImGui.SetItemTooltip(tip);
    }

    private const string RemoveLabel = PhosphorIcons.Trash + " Remove monster";
    private const string DeleteRuleLabel = PhosphorIcons.Trash + " Delete rule";
    private const string DebuffsHint = "Debuffs are set with the " + PhosphorIcons.CaretDown + " in the Damage panel's Debuffs column.";

    private static void ToggleRow(string label, (string Field, string Label, string Tip)[] defs, Rule view, string name)
    {
        SafeImGui.TextColored(Vec(Dim), label);
        float h = ImGuiNET.ImGui.GetFrameHeight();
        foreach (var (field, text, tip) in defs)
        {
            ImGuiNET.ImGui.SameLine();
            bool on = view.GetToggle(field);
            float tw = ImGuiNET.ImGui.CalcTextSize(text).X;
            Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
            var size = new Vector2(tw + h + 8, h);
            if (ImGuiNET.ImGui.InvisibleButton("##t_" + field, size))
                DamageCommands.EditRule(name, rule => DamageCommands.FlipToggle(rule, field));
            bool hot = ImGuiNET.ImGui.IsItemHovered();
            SafeImGui.SetItemTooltip(tip);
            var dl = ImGuiNET.ImGui.GetWindowDrawList();
            dl.AddRectFilled(p, p + size, hot ? FaceKit.Lighten(EntryBg) : EntryBg, 2);
            dl.AddRect(p, p + size, EntryBorder, 2);
            float box = h - 8;
            dl.AddRectFilled(p + new Vector2(4, 4), p + new Vector2(4 + box, 4 + box), on ? ToggleOn : ToggleOff, 2);
            dl.AddText(p + new Vector2(box + 8, (h - ImGuiNET.ImGui.GetTextLineHeight()) * 0.5f), White, text);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static bool BeginTable(string id, params string[] headers)
    {
        if (!ImGuiNET.ImGui.BeginTable(id, headers.Length,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.PadOuterX))
            return false;
        foreach (string h in headers) ImGuiNET.ImGui.TableSetupColumn(h);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Teal);
        ImGuiNET.ImGui.TableHeadersRow();
        ImGuiNET.ImGui.PopStyleColor();
        return true;
    }

    private static void Cell(string text)
    {
        ImGuiNET.ImGui.TableNextColumn();
        SafeImGui.TextColored(Vec(White), text);
    }

    private static Vector4 Vec(uint abgr) => ImGuiNET.ImGui.ColorConvertU32ToFloat4(abgr);

    private static string Utf8(byte[] buffer)
    {
        int len = Array.IndexOf(buffer, (byte)0);
        return Encoding.UTF8.GetString(buffer, 0, len < 0 ? buffer.Length : len);
    }

    /// <summary>Writes <paramref name="text"/> NUL-terminated, cut at a character boundary if it doesn't fit.</summary>
    private static void WriteUtf8(byte[] buffer, string text)
    {
        byte[] all = Encoding.UTF8.GetBytes(text);
        int n = Math.Min(all.Length, buffer.Length - 1);
        while (n > 0 && n < all.Length && (all[n] & 0xC0) == 0x80) n--;   // don't split a sequence
        Array.Copy(all, buffer, n);
        buffer[n] = 0;
    }
}
