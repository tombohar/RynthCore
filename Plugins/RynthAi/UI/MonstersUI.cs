using System;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using Decal.Adapter;
using Decal.Adapter.Wrappers;

namespace NexSuite.Plugins.RynthAi
{
    public class MonstersUI
    {
        private readonly UISettings _settings;
        private readonly CoreManager _core;

        public Action OnSettingsChanged;

        // Local state just for this tab
        private string _newMonsterName = "";
        private readonly string[] _elements = { "Slash", "Pierce", "Bludgeon", "Fire", "Cold", "Lightning", "Acid", "Nether" };

        public MonstersUI(UISettings settings, CoreManager core)
        {
            _settings = settings;
            _core = core;
        }

        public void Render()
        {
            if (ImGui.BeginTable("MonstersGrid", 18, ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(0, 300)))
            {
                ImGui.TableSetupColumn("F", ImGuiTableColumnFlags.WidthFixed, 15);
                ImGui.TableSetupColumn("B", ImGuiTableColumnFlags.WidthFixed, 15);
                ImGui.TableSetupColumn("G", ImGuiTableColumnFlags.WidthFixed, 15);
                ImGui.TableSetupColumn("I", ImGuiTableColumnFlags.WidthFixed, 15);
                ImGui.TableSetupColumn("Y", ImGuiTableColumnFlags.WidthFixed, 15);
                ImGui.TableSetupColumn("V", ImGuiTableColumnFlags.WidthFixed, 15);
                ImGui.TableSetupColumn("A", ImGuiTableColumnFlags.WidthFixed, 15);
                ImGui.TableSetupColumn("Bl", ImGuiTableColumnFlags.WidthFixed, 15);
                ImGui.TableSetupColumn("R", ImGuiTableColumnFlags.WidthFixed, 15);
                ImGui.TableSetupColumn("S", ImGuiTableColumnFlags.WidthFixed, 15);
                ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("P", ImGuiTableColumnFlags.WidthFixed, 25);
                ImGui.TableSetupColumn("Dmg type", ImGuiTableColumnFlags.WidthFixed, 80);
                ImGui.TableSetupColumn("Ex Vuln", ImGuiTableColumnFlags.WidthFixed, 80);
                ImGui.TableSetupColumn("Weapon", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Offhand", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("PetDmg", ImGuiTableColumnFlags.WidthFixed, 70);
                ImGui.TableSetupColumn("Del", ImGuiTableColumnFlags.WidthFixed, 30);

                ImGui.TableHeadersRow();

                uint checkColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f, 1.0f, 0.2f, 1.0f));
                uint offColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 0.3f, 0.3f, 1.0f));

                for (int i = 0; i < _settings.MonsterRules.Count; i++)
                {
                    var rule = _settings.MonsterRules[i];
                    bool isDefault = rule.Name.Equals("Default", StringComparison.OrdinalIgnoreCase);

                    ImGui.TableNextRow();

                    // --- 9 Toggle Lights ---
                    ImGui.TableNextColumn();
                    if (DrawToggleLight($"##F{i}", rule.Fester, checkColor, offColor, "F: Fester (Decrepitude's Grasp / Fester Other I-VIII)"))
                    { rule.Fester = !rule.Fester; OnSettingsChanged?.Invoke(); }

                    ImGui.TableNextColumn();
                    if (DrawToggleLight($"##B{i}", rule.Broadside, checkColor, offColor, "B: Broadside (Broadside of a Barn / Missile Weapons Ineptitude I-VIII)"))
                    { rule.Broadside = !rule.Broadside; OnSettingsChanged?.Invoke(); }

                    ImGui.TableNextColumn();
                    if (DrawToggleLight($"##G{i}", rule.GravityWell, checkColor, offColor, "G: Gravity Well (Vulnerability Other I-VIII)"))
                    { rule.GravityWell = !rule.GravityWell; OnSettingsChanged?.Invoke(); }

                    ImGui.TableNextColumn();
                    if (DrawToggleLight($"##I{i}", rule.Imperil, checkColor, offColor, "I: Imperil (Gossamer Flesh / Imperil Other I-VIII)"))
                    { rule.Imperil = !rule.Imperil; OnSettingsChanged?.Invoke(); }

                    ImGui.TableNextColumn();
                    if (DrawToggleLight($"##Y{i}", rule.Yield, checkColor, offColor, "Y: Yield (Magic Yield Other I-VIII)"))
                    { rule.Yield = !rule.Yield; OnSettingsChanged?.Invoke(); }

                    ImGui.TableNextColumn();
                    if (DrawToggleLight($"##V{i}", rule.Vuln, checkColor, offColor, "V: Vulnerability (element-matched)"))
                    { rule.Vuln = !rule.Vuln; OnSettingsChanged?.Invoke(); }

                    ImGui.TableNextColumn();
                    if (DrawToggleLight($"##A{i}", rule.UseArc, checkColor, offColor, "A: Arc spells (War/Void magic)\nFor Melee/Missile: Attack toggle"))
                    { rule.UseArc = !rule.UseArc; OnSettingsChanged?.Invoke(); }

                    ImGui.TableNextColumn();
                    if (DrawToggleLight($"##Bl{i}", rule.UseBolt, checkColor, offColor, "Bl: Bolt spells (War/Void magic, default)\nIf none of A/Bl/R/S set in magic = no attack (buff bot)"))
                    { rule.UseBolt = !rule.UseBolt; OnSettingsChanged?.Invoke(); }

                    ImGui.TableNextColumn();
                    if (DrawToggleLight($"##R{i}", rule.UseRing, checkColor, offColor, "R: Ring spells (uses Ring Range)"))
                    { rule.UseRing = !rule.UseRing; OnSettingsChanged?.Invoke(); }

                    ImGui.TableNextColumn();
                    if (DrawToggleLight($"##S{i}", rule.UseStreak, checkColor, offColor, "S: Streak spells (War/Void magic)"))
                    { rule.UseStreak = !rule.UseStreak; OnSettingsChanged?.Invoke(); }

                    // --- Name ---
                    ImGui.TableNextColumn();
                    if (isDefault) ImGui.TextColored(new Vector4(1, 1, 0, 1), rule.Name);
                    else ImGui.Text(rule.Name);

                    // --- Priority ---
                    ImGui.TableNextColumn();
                    ImGui.SetNextItemWidth(25);
                    int p = rule.Priority;
                    if (ImGui.InputInt($"##P{i}", ref p, 0)) { rule.Priority = p; OnSettingsChanged?.Invoke(); }

                    // --- Dmg type ---
                    ImGui.TableNextColumn();
                    ImGui.SetNextItemWidth(80);
                    if (ImGui.BeginCombo($"##Dmg{i}", rule.DamageType, ImGuiComboFlags.NoArrowButton))
                    {
                        if (ImGui.Selectable("Auto", rule.DamageType == "Auto")) { rule.DamageType = "Auto"; OnSettingsChanged?.Invoke(); }
                        foreach (var el in _elements)
                        {
                            if (ImGui.Selectable(el, rule.DamageType == el))
                            { rule.DamageType = el; OnSettingsChanged?.Invoke(); }
                        }
                        ImGui.EndCombo();
                    }

                    // --- Ex Vuln (extra vulnerability element) ---
                    ImGui.TableNextColumn();
                    ImGui.SetNextItemWidth(80);
                    if (ImGui.BeginCombo($"##Vuln{i}", rule.ExVuln, ImGuiComboFlags.NoArrowButton))
                    {
                        if (ImGui.Selectable("None", rule.ExVuln == "None")) { rule.ExVuln = "None"; OnSettingsChanged?.Invoke(); }
                        foreach (var el in _elements)
                        {
                            if (ImGui.Selectable(el, rule.ExVuln == el))
                            { rule.ExVuln = el; OnSettingsChanged?.Invoke(); }
                        }
                        ImGui.EndCombo();
                    }

                    // --- Weapon Dropdown ---
                    ImGui.TableNextColumn();
                    ImGui.SetNextItemWidth(-1);
                    string currentWeaponName = "<AUTO>";
                    if (rule.WeaponId != 0)
                    {
                        var matchedWep = _settings.ItemRules.FirstOrDefault(x => x.Id == rule.WeaponId);
                        if (matchedWep != null) currentWeaponName = matchedWep.Name;
                    }
                    if (ImGui.BeginCombo($"##Wep{i}", currentWeaponName, ImGuiComboFlags.NoArrowButton))
                    {
                        if (ImGui.Selectable("<AUTO>", rule.WeaponId == 0))
                        { rule.WeaponId = 0; OnSettingsChanged?.Invoke(); }
                        foreach (var itemRule in _settings.ItemRules)
                        {
                            if (ImGui.Selectable(itemRule.Name, rule.WeaponId == itemRule.Id))
                            { rule.WeaponId = itemRule.Id; OnSettingsChanged?.Invoke(); }
                        }
                        ImGui.EndCombo();
                    }

                    // --- Offhand Dropdown (shield or dual-wield weapon from ItemRules) ---
                    ImGui.TableNextColumn();
                    ImGui.SetNextItemWidth(-1);
                    string currentOffhandName = "<AUTO>";
                    if (rule.OffhandId != 0)
                    {
                        var matchedOff = _settings.ItemRules.FirstOrDefault(x => x.Id == rule.OffhandId);
                        if (matchedOff != null) currentOffhandName = matchedOff.Name;
                    }
                    if (ImGui.BeginCombo($"##Off{i}", currentOffhandName, ImGuiComboFlags.NoArrowButton))
                    {
                        if (ImGui.Selectable("<AUTO>", rule.OffhandId == 0))
                        { rule.OffhandId = 0; OnSettingsChanged?.Invoke(); }
                        foreach (var itemRule in _settings.ItemRules)
                        {
                            if (ImGui.Selectable(itemRule.Name + "##off", rule.OffhandId == itemRule.Id))
                            { rule.OffhandId = itemRule.Id; OnSettingsChanged?.Invoke(); }
                        }
                        ImGui.EndCombo();
                    }

                    // --- PetDmg (pet damage element) ---
                    ImGui.TableNextColumn();
                    ImGui.SetNextItemWidth(70);
                    if (ImGui.BeginCombo($"##PetDmg{i}", rule.PetDamage, ImGuiComboFlags.NoArrowButton))
                    {
                        if (ImGui.Selectable("PAuto", rule.PetDamage == "PAuto")) { rule.PetDamage = "PAuto"; OnSettingsChanged?.Invoke(); }
                        foreach (var el in _elements)
                        {
                            if (ImGui.Selectable(el, rule.PetDamage == el))
                            { rule.PetDamage = el; OnSettingsChanged?.Invoke(); }
                        }
                        ImGui.EndCombo();
                    }

                    // --- Delete ---
                    ImGui.TableNextColumn();
                    if (isDefault)
                    {
                        ImGui.TextDisabled("-");
                    }
                    else
                    {
                        if (ImGui.Button($"X##del{i}"))
                        {
                            _settings.MonsterRules.RemoveAt(i);
                            OnSettingsChanged?.Invoke();
                        }
                    }
                }
                ImGui.EndTable();
            }

            ImGui.Separator();

            if (ImGui.BeginTable("AddTable", 3))
            {
                ImGui.TableSetupColumn("NameIn", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("AddBtn", ImGuiTableColumnFlags.WidthFixed, 60);
                ImGui.TableSetupColumn("AddSelBtn", ImGuiTableColumnFlags.WidthFixed, 80);

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##NewMonName", "Enter monster name to add...", ref _newMonsterName, 64);

                ImGui.TableNextColumn();
                if (ImGui.Button("Add", new Vector2(60, 22)))
                {
                    if (!string.IsNullOrWhiteSpace(_newMonsterName) && !_settings.MonsterRules.Any(m => m.Name.Equals(_newMonsterName, StringComparison.OrdinalIgnoreCase)))
                    {
                        _settings.MonsterRules.Add(new MonsterRule { Name = _newMonsterName, DamageType = "Auto", WeaponId = 0 });
                        _newMonsterName = "";
                        OnSettingsChanged?.Invoke();
                    }
                }

                ImGui.TableNextColumn();
                if (ImGui.Button("Add Sel", new Vector2(80, 22)))
                {
                    int selectedId = _core.Actions.CurrentSelection;
                    if (selectedId != 0)
                    {
                        var obj = _core.WorldFilter[selectedId];
                        if (obj != null && !_settings.MonsterRules.Any(m => m.Name.Equals(obj.Name, StringComparison.OrdinalIgnoreCase)))
                        {
                            _settings.MonsterRules.Add(new MonsterRule { Name = obj.Name, DamageType = "Auto", WeaponId = 0 });
                            OnSettingsChanged?.Invoke();
                        }
                    }
                }

                ImGui.EndTable();
            }
        }

        // ── Floating window wrapper (called from dashboard) ──────────────────
        public void RenderAsWindow(ref bool open)
        {
            ImGui.SetNextWindowSize(new Vector2(700, 400), ImGuiCond.FirstUseEver);
            if (ImGui.Begin("Monster Config##RynthAiMonsters", ref open))
                Render();
            ImGui.End();
        }

        private bool DrawToggleLight(string id, bool isOn, uint onColor, uint offColor, string tooltip = null)
        {
            var pos = ImGui.GetCursorScreenPos();
            ImGui.GetWindowDrawList().AddRectFilled(
                pos + new Vector2(2, 2),
                pos + new Vector2(12, 12),
                isOn ? onColor : offColor, 10.0f
            );
            ImGui.InvisibleButton(id, new Vector2(14, 14));
            if (tooltip != null && ImGui.IsItemHovered())
                ImGui.SetTooltip(tooltip);
            return ImGui.IsItemClicked();
        }
    }
}