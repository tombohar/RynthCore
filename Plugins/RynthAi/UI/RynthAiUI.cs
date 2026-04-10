using System;
using System.IO;
using System.Collections.Generic;
using System.Numerics;
using System.Linq;
using ImGuiNET;
using Decal.Adapter;
using Decal.Adapter.Wrappers;
using RynthAi.Meta;

namespace NexSuite.Plugins.RynthAi
{
    public static class DashWindows
    {
        public static bool ShowMacroRules = false;
        public static bool ShowMonsters = false;
        public static bool ShowNavigation = false;
        public static bool ShowWeapons = false;
        public static bool ShowLua = false;
    }

    public partial class RynthAiUI : IDisposable
    {
        public UISettings Settings { get; set; }
        public CombatManager CombatManager { get; set; }
        public CoreManager Core { get; set; }
        public MetaManager MetaManager { get; set; }

        public Action OnSettingsChanged;
        public Action OnForceRebuffClicked;
        public Action OnLootProfileSelected;
        public Action OnMetaProfileSelected;
        public Action OnMetaRulesUpdated;

        public LuaUI LuaTab;
        public MetaUI MetaTab;
        public MonstersUI MonstersTab;
        public RouteUI RouteTab;

        private readonly AdvancedSettingsUI _advancedUI;

        public List<string> Profiles = new List<string>();
        public List<string> NavFiles = new List<string>();
        public List<string> LootFiles = new List<string>();
        public List<string> MetaFiles = new List<string>();

        private int _selectedNavIdx = 0;
        private string _navFolder = @"C:\Games\DecalPlugins\NexSuite\RynthAi\NavProfiles";
        private string _lootFolder = @"C:\Games\DecalPlugins\NexSuite\RynthAi\LootProfiles";
        private string _metaFolder = @"C:\Games\DecalPlugins\NexSuite\RynthAi\MetaProfiles";

        private readonly string[] _elements = { "Slash", "Pierce", "Bludgeon", "Fire", "Cold", "Lightning", "Acid", "Nether" };

        // ── Colour palette ───────────────────────────────────────────────────
        private static readonly Vector4 ColTeal = new Vector4(0.15f, 0.85f, 0.90f, 1.00f);
        private static readonly Vector4 ColAmber = new Vector4(0.91f, 0.70f, 0.20f, 1.00f);
        private static readonly Vector4 ColRed = new Vector4(0.85f, 0.25f, 0.25f, 1.00f);
        private static readonly Vector4 ColGreen = new Vector4(0.25f, 0.85f, 0.45f, 1.00f);
        private static readonly Vector4 ColTextDim = new Vector4(0.85f, 0.90f, 0.95f, 1.00f);
        private static readonly Vector4 ColTextMute = new Vector4(0.55f, 0.65f, 0.75f, 1.00f);
        private static readonly Vector4 ColHp = new Vector4(0.85f, 0.20f, 0.20f, 1.00f);
        private static readonly Vector4 ColStam = new Vector4(0.20f, 0.80f, 0.35f, 1.00f);
        private static readonly Vector4 ColMana = new Vector4(0.15f, 0.55f, 0.95f, 1.00f);
        private static readonly Vector4 ColBarBg = new Vector4(0.08f, 0.12f, 0.16f, 1.00f);
        private static readonly Vector4 ColPanelBg = new Vector4(0.04f, 0.07f, 0.10f, 0.95f);

        private static readonly Vector4 ColBtnOn = new Vector4(0.15f, 0.30f, 0.35f, 1.00f);
        private static readonly Vector4 ColBtnFill = new Vector4(0.06f, 0.12f, 0.18f, 1.00f);
        private static readonly Vector4 ColBtnHov = new Vector4(0.10f, 0.18f, 0.25f, 1.00f);
        private static readonly Vector4 ColBtnAct = new Vector4(0.08f, 0.15f, 0.22f, 1.00f);
        private static readonly Vector4 ColBtnBord = new Vector4(0.15f, 0.25f, 0.35f, 1.00f);

        // ── DRAKHUD HEALTH ENGINE ────────────────────────────────────────────
        private Dictionary<int, float> _healthRatioTracker = new Dictionary<int, float>();
        private Dictionary<int, int> _maxHealthTracker = new Dictionary<int, int>();
        private int _lastTargetId = 0;
        private int _retryTargetId = 0;
        private int _retryCount = 0;
        private DateTime _lastRequestTime = DateTime.MinValue;

        // ── UI STATE ─────────────────────────────────────────────────────────
        private bool _isMinimized = false;
        private bool _isLocked = false;
        private float _bgOpacity = 0.95f;
        private Vector2 _expandedSize = new Vector2(430, 420);
        private bool _wasMinimized = false;

        public RynthAiUI(UISettings settings, CombatManager combatManager, CoreManager core)
        {
            Settings = settings;
            CombatManager = combatManager;
            Core = core;

            LuaTab = new LuaUI(settings, core);
            _advancedUI = new AdvancedSettingsUI(settings) { OnSettingsChanged = () => OnSettingsChanged?.Invoke() };
            MetaTab = new MetaUI(settings, NavFiles) { OnSettingsChanged = () => OnSettingsChanged?.Invoke(), OnMetaRulesUpdated = () => OnMetaRulesUpdated?.Invoke() };
            MonstersTab = new MonstersUI(settings, core) { OnSettingsChanged = () => OnSettingsChanged?.Invoke() };
            RouteTab = new RouteUI(settings, core) { OnSettingsChanged = () => OnSettingsChanged?.Invoke() };

            RefreshProfilesList(); RefreshNavFiles(); RefreshLootFiles(); RefreshMetaFiles();

            try { Core.EchoFilter.ServerDispatch += OnServerDispatch; } catch { }
        }

        public void Dispose()
        {
            try { Core.EchoFilter.ServerDispatch -= OnServerDispatch; } catch { }
        }

        public void SetMissileCraftingManager(MissileCraftingManager mgr) => _advancedUI.SetMissileCraftingManager(mgr);

        public void Render()
        {
            UpdateTargetAppraisal();

            if (RouteTab != null && RouteTab.NeedsRouteGraphicsRefresh)
            { RouteTab.NeedsRouteGraphicsRefresh = false; RouteTab.UpdateRouteGraphics(); }

            RenderDashboard();

            // Render all active popup windows based on the button toggles
            try { if (Settings.ShowAdvancedWindow) _advancedUI?.Render(); } catch { Settings.ShowAdvancedWindow = false; }
            try { if (DashWindows.ShowMacroRules) MetaTab?.RenderAsWindow(ref DashWindows.ShowMacroRules); } catch { DashWindows.ShowMacroRules = false; }
            try { if (DashWindows.ShowMonsters) MonstersTab?.RenderAsWindow(ref DashWindows.ShowMonsters); } catch { DashWindows.ShowMonsters = false; }
            try { if (DashWindows.ShowWeapons) RenderWeaponsWindow(); } catch { DashWindows.ShowWeapons = false; }
            try { if (DashWindows.ShowNavigation) RenderNavigationWindow(); } catch { DashWindows.ShowNavigation = false; }
            try { if (DashWindows.ShowLua) RenderLuaWindow(); } catch { DashWindows.ShowLua = false; }
        }

        private void RenderDashboard()
        {
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 10));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6.0f);

            ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.04f, 0.06f, 0.08f, _bgOpacity));

            ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse;

            if (_isMinimized)
            {
                flags |= ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoResize;
                _wasMinimized = true;
            }
            else
            {
                if (_isLocked) flags |= ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove;

                if (_wasMinimized)
                {
                    ImGui.SetNextWindowSize(_expandedSize, ImGuiCond.Always);
                    _wasMinimized = false;
                }
            }

            ImGui.SetNextWindowSizeConstraints(new Vector2(400, 0), new Vector2(1200, 2000));

            if (!_wasMinimized && !_isMinimized && !_isLocked)
            {
                ImGui.SetNextWindowSize(_expandedSize, ImGuiCond.FirstUseEver);
            }

            if (ImGui.Begin("RynthAi Dashboard##Main", flags))
            {
                if (!_isMinimized && !_isLocked && !_wasMinimized)
                {
                    _expandedSize = ImGui.GetWindowSize();
                }

                RenderDashHeader();

                ImGui.PushStyleColor(ImGuiCol.ChildBg, ColPanelBg);
                ImGui.PushStyleColor(ImGuiCol.Border, ColBtnBord);
                ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 1.0f);

                if (ImGui.BeginChild("CombatPanel", new Vector2(-1, 154), true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
                {
                    RenderCombatPanel();
                    ImGui.Dummy(new Vector2(0, 4));
                }
                ImGui.EndChild();
                ImGui.PopStyleVar();
                ImGui.PopStyleColor(2);

                if (!_isMinimized)
                {
                    ImGui.Spacing(); ImGui.Spacing();
                    RenderLauncherGrid();
                }
            }
            ImGui.End();
            ImGui.PopStyleColor();
            ImGui.PopStyleVar(3);
        }

        private void RenderDashHeader()
        {
            float W = ImGui.GetContentRegionAvail().X;
            float startY = ImGui.GetCursorPosY();

            ImGui.SetWindowFontScale(1.4f);
            ImGui.TextColored(ColTeal, "N"); ImGui.SameLine(0, 2);
            ImGui.TextColored(new Vector4(1, 1, 1, 1), "NEXAI DASHBOARD");
            ImGui.SetWindowFontScale(1.0f);

            ImGui.SameLine();
            ImGui.SetCursorPosY(startY + 5);
            ImGui.TextColored(ColTextMute, "v4.0");

            ImGui.SameLine(W - 130);
            ImGui.SetCursorPosY(startY + 2);

            if (ImGui.SmallButton(_isLocked ? "Unlk" : "Lock")) { _isLocked = !_isLocked; }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(_isLocked ? "Unlock Window" : "Lock Window");

            ImGui.SameLine();
            if (ImGui.SmallButton("-")) _bgOpacity = Math.Max(0.1f, _bgOpacity - 0.1f);

            ImGui.SameLine();
            if (ImGui.SmallButton("+")) _bgOpacity = Math.Min(1.0f, _bgOpacity + 0.1f);

            ImGui.SameLine();
            if (ImGui.SmallButton(_isMinimized ? "^" : "_")) { _isMinimized = !_isMinimized; }

            ImGui.SameLine();
            if (ImGui.SmallButton("X")) { Settings.IsMacroRunning = false; }

            ImGui.Dummy(new Vector2(0, 2));

            if (!_isMinimized)
            {
                if (ImGui.BeginTable("HeaderGrid", 2))
                {
                    ImGui.TableSetupColumn("Left", ImGuiTableColumnFlags.WidthFixed, W * 0.40f);
                    ImGui.TableSetupColumn("Right", ImGuiTableColumnFlags.WidthStretch);

                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.SetWindowFontScale(1.3f);
                    ImGui.TextColored(ColTextMute, "Macro Status:");
                    ImGui.SetWindowFontScale(1.0f);

                    ImGui.TableNextColumn();
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 2);
                    ImGui.TextColored(ColTextMute, "Profile:");
                    ImGui.SameLine(60);
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.BeginCombo("##ProfCombo", TruncateName(Settings.SelectedProfile, 16)))
                    {
                        foreach (var p in Profiles)
                        {
                            if (ImGui.Selectable(p, p == Settings.SelectedProfile))
                            {
                                Settings.SelectedProfile = p; OnSettingsChanged?.Invoke();
                            }
                        }
                        ImGui.EndCombo();
                    }

                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    var pos = ImGui.GetCursorScreenPos();
                    var dl = ImGui.GetWindowDrawList();
                    uint color = Settings.IsMacroRunning ? ImGui.ColorConvertFloat4ToU32(ColGreen) : ImGui.ColorConvertFloat4ToU32(ColTextMute);

                    dl.AddCircleFilled(pos + new Vector2(8, 12), 6, color);
                    if (Settings.IsMacroRunning) dl.AddCircle(pos + new Vector2(8, 12), 9, color, 12, 1.5f);

                    ImGui.SetCursorScreenPos(pos + new Vector2(24, 0));
                    if (ImGui.InvisibleButton("ToggleMacro", new Vector2(100, 24)))
                    {
                        Settings.IsMacroRunning = !Settings.IsMacroRunning;
                        OnSettingsChanged?.Invoke();
                    }

                    ImGui.SetCursorScreenPos(pos + new Vector2(26, 2));
                    ImGui.SetWindowFontScale(1.3f);
                    ImGui.TextColored(Settings.IsMacroRunning ? ColGreen : ColTextMute, Settings.IsMacroRunning ? "RUNNING" : "STOPPED");
                    ImGui.SetWindowFontScale(1.0f);

                    ImGui.TableNextColumn();
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 4);
                    ImGui.TextColored(ColTextMute, "Nav:");
                    ImGui.SameLine(60);
                    ImGui.SetNextItemWidth(-1);
                    string navName = string.IsNullOrEmpty(Settings.CurrentNavPath) ? "None" : Path.GetFileNameWithoutExtension(Settings.CurrentNavPath);
                    if (ImGui.BeginCombo("##NavCombo", TruncateName(navName, 16)))
                    {
                        for (int i = 0; i < NavFiles.Count; i++)
                        {
                            if (ImGui.Selectable(NavFiles[i], _selectedNavIdx == i))
                            {
                                _selectedNavIdx = i; LoadSelectedNav();
                            }
                        }
                        ImGui.EndCombo();
                    }

                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 4);
                    ImGui.TextColored(ColTextMute, "State:"); ImGui.SameLine(0, 8);
                    ImGui.TextColored(ColAmber, Settings.CurrentState ?? "Idle");

                    ImGui.TableNextColumn();
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 6);
                    ImGui.TextColored(ColTextMute, "Loot:");
                    ImGui.SameLine(60);
                    ImGui.SetNextItemWidth(-1);
                    string lootName = string.IsNullOrEmpty(Settings.CurrentLootPath) ? "None" : Path.GetFileNameWithoutExtension(Settings.CurrentLootPath);
                    if (ImGui.BeginCombo("##LootCombo", TruncateName(lootName, 16)))
                    {
                        for (int i = 0; i < LootFiles.Count; i++)
                        {
                            if (ImGui.Selectable(LootFiles[i], Settings.LootProfileIdx == i))
                            {
                                Settings.LootProfileIdx = i;
                                Settings.CurrentLootPath = (i == 0) ? "" : Path.Combine(_lootFolder, LootFiles[i] + ".utl");
                                OnSettingsChanged?.Invoke(); OnLootProfileSelected?.Invoke();
                            }
                        }
                        ImGui.EndCombo();
                    }

                    ImGui.EndTable();
                }
                ImGui.Spacing();
            }
        }

        private void RenderCombatPanel()
        {
            if (ImGui.BeginTable("CombatInnerTable", 2, ImGuiTableFlags.None))
            {
                ImGui.TableSetupColumn("Toggles", ImGuiTableColumnFlags.WidthFixed, 68);
                ImGui.TableSetupColumn("Vitals", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableNextRow();

                // --- COLUMN 1: TOGGLES ---
                ImGui.TableNextColumn();
                var tPos = ImGui.GetCursorScreenPos() + new Vector2(2, 28);

                // 2x2 Grid
                DrawSquareToggle("sword", ref Settings.EnableCombat, tPos, "CombatTgl");
                DrawSquareToggle("buff", ref Settings.EnableBuffing, tPos + new Vector2(34, 0), "BuffTgl");
                DrawSquareToggle("shoe", ref Settings.EnableNavigation, tPos + new Vector2(0, 34), "NavTgl");
                DrawSquareToggle("bag", ref Settings.EnableLooting, tPos + new Vector2(34, 34), "LootTgl");

                // Wide MACRO Button
                DrawWideToggle("MACRO", "gear", ref Settings.EnableMeta, tPos + new Vector2(0, 68), "MetaTgl", 64f, 20f);

                // --- COLUMN 2: MOB + PLAYER VITALS ---
                ImGui.TableNextColumn();

                string targetName = "NO TARGET";
                float targetHpPct = 0f;
                string mobHpDisplay = "0%";
                int currentTarget = Core.Actions.CurrentSelection;

                if (currentTarget != 0)
                {
                    try
                    {
                        var wo = Core.WorldFilter[currentTarget];
                        if (wo != null)
                        {
                            targetName = wo.Name;

                            if (_healthRatioTracker.ContainsKey(currentTarget))
                            {
                                targetHpPct = _healthRatioTracker[currentTarget];
                            }
                            else
                            {
                                targetHpPct = (float)wo.Values((DoubleValueKey)0x92, 0.0);
                            }

                            if (_maxHealthTracker.ContainsKey(currentTarget) && _maxHealthTracker[currentTarget] > 0)
                            {
                                int maxHp = _maxHealthTracker[currentTarget];
                                int curHp = (int)Math.Round(targetHpPct * maxHp);
                                mobHpDisplay = $"{(int)(targetHpPct * 100)}% ({curHp}/{maxHp})";
                            }
                            else
                            {
                                mobHpDisplay = $"{(int)(targetHpPct * 100)}%";
                            }
                        }
                    }
                    catch { }
                }

                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 4);
                ImGui.TextColored(ColTextDim, TruncateName(targetName, 32).ToUpper());

                float textWidth = ImGui.CalcTextSize(mobHpDisplay).X;
                ImGui.SameLine(ImGui.GetContentRegionAvail().X - textWidth);
                ImGui.TextColored(new Vector4(1, 1, 1, 1), mobHpDisplay);

                DrawSegmentedBar(targetHpPct, ImGui.GetContentRegionAvail().X - 4);

                ImGui.Spacing();
                ImGui.TextColored(ColTextMute, "PLAYER VITALS");

                int hp = 0, hpMax = 1, st = 0, stMax = 1, mn = 0, mnMax = 1;
                try
                {
                    if (Core?.CharacterFilter != null)
                    {
                        hp = Core.CharacterFilter.Health; hpMax = Math.Max(1, (int)Core.CharacterFilter.EffectiveVital[CharFilterVitalType.Health]);
                        st = Core.CharacterFilter.Stamina; stMax = Math.Max(1, (int)Core.CharacterFilter.EffectiveVital[CharFilterVitalType.Stamina]);
                        mn = Core.CharacterFilter.Mana; mnMax = Math.Max(1, (int)Core.CharacterFilter.EffectiveVital[CharFilterVitalType.Mana]);
                    }
                }
                catch { }

                float hpPct = (float)hp / hpMax; float stPct = (float)st / stMax; float mnPct = (float)mn / mnMax;

                DrawVitalRow("heart", "HP", hpPct, ColHp, $"{hp}/{hpMax}");
                DrawVitalRow("run", "ST", stPct, ColGreen, $"{st}/{stMax}");
                DrawVitalRow("drop", "MN", mnPct, ColMana, $"{mn}/{mnMax}");

                ImGui.EndTable();
            }
        }

        private void RenderLauncherGrid()
        {
            if (ImGui.BeginTable("LauncherGridTable", 3, ImGuiTableFlags.SizingStretchSame))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); GridBtn("Macro Rules", "gear", ref DashWindows.ShowMacroRules);
                ImGui.TableNextColumn(); GridBtn("Monsters", "target", ref DashWindows.ShowMonsters);
                ImGui.TableNextColumn(); GridBtn("Settings", "wrench", ref Settings.ShowAdvancedWindow);

                ImGui.TableNextRow();
                ImGui.TableNextColumn(); GridBtn("Navigation", "map", ref DashWindows.ShowNavigation);
                ImGui.TableNextColumn(); GridBtn("Weapons", "shield", ref DashWindows.ShowWeapons);
                ImGui.TableNextColumn(); GridBtn("Lua Scripts", "code", ref DashWindows.ShowLua);

                ImGui.EndTable();
            }
        }

        private void GridBtn(string label, string icon, ref bool flag)
        {
            bool wasOn = flag;
            float bH = 30f;
            float bW = -1f;

            ImGui.PushStyleColor(ImGuiCol.Button, ColBtnFill);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColBtnHov);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, ColBtnAct);
            ImGui.PushStyleColor(ImGuiCol.Border, wasOn ? ColTeal : ColBtnBord);

            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);

            var startPos = ImGui.GetCursorScreenPos();
            if (ImGui.Button($"##{label}", new Vector2(bW, bH))) flag = !flag;

            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(4);

            float actualWidth = ImGui.GetItemRectSize().X;
            var textSize = ImGui.CalcTextSize(label);

            DrawIcon(icon, startPos + new Vector2(8, 6), wasOn ? ColTeal : ColTextMute, 16f);

            ImGui.SetCursorScreenPos(startPos + new Vector2(30, (bH - textSize.Y) / 2));
            ImGui.TextColored(wasOn ? ColTeal : ColTextDim, label);

            ImGui.SetCursorScreenPos(startPos + new Vector2(0, bH + 2));
            ImGui.Dummy(new Vector2(actualWidth, 0));
        }

        // ═════════════════════════════════════════════════════════════════════
        //  WEAPONS & ITEMS WINDOW
        // ═════════════════════════════════════════════════════════════════════
        private void RenderWeaponsWindow()
        {
            ImGui.SetNextWindowSize(new Vector2(480, 300), ImGuiCond.FirstUseEver);
            if (ImGui.Begin("Weapons & Items##RynthAiWeapons", ref DashWindows.ShowWeapons))
            {
                ImGui.TextColored(ColAmber, "Weapons / Wands / Shields / Pets");

                if (ImGui.BeginTable("ItemsTable", 3,
                    ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
                {
                    ImGui.TableSetupColumn("Item Name", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("Element", ImGuiTableColumnFlags.WidthFixed, 100);
                    ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 60);
                    ImGui.TableHeadersRow();

                    for (int i = 0; i < Settings.ItemRules.Count; i++)
                    {
                        var rule = Settings.ItemRules[i];
                        ImGui.TableNextRow();

                        ImGui.TableNextColumn();
                        var wo = Core?.WorldFilter[rule.Id];
                        if (wo != null)
                            ImGui.Text(rule.Name);
                        else
                        {
                            ImGui.TextColored(ColRed, rule.Name);
                            ImGui.SameLine();
                            ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 0.8f), "(Gone)");
                            if (ImGui.IsItemHovered())
                                ImGui.SetTooltip("Weapon not in inventory!\nBot cannot equip this item.\nRemove and re-add the correct weapon.");
                        }

                        ImGui.TableNextColumn();
                        ImGui.SetNextItemWidth(-1);
                        if (ImGui.BeginCombo($"##Elem{i}", rule.Element, ImGuiComboFlags.NoArrowButton))
                        {
                            foreach (var el in _elements)
                                if (ImGui.Selectable(el, rule.Element == el))
                                { rule.Element = el; OnSettingsChanged?.Invoke(); }
                            ImGui.EndCombo();
                        }

                        ImGui.TableNextColumn();
                        if (ImGui.Button($"Del##{i}", new Vector2(-1, 0)))
                        {
                            foreach (var mr in Settings.MonsterRules)
                            {
                                if (mr.WeaponId == rule.Id) mr.WeaponId = 0;
                                if (mr.OffhandId == rule.Id) mr.OffhandId = 0;
                            }
                            Settings.ItemRules.RemoveAt(i);
                            OnSettingsChanged?.Invoke();
                        }
                    }
                    ImGui.EndTable();
                }

                ImGui.Separator();
                if (ImGui.Button("Add Selected Item", new Vector2(150, 24)))
                {
                    int sel = Core.Actions.CurrentSelection;
                    if (sel != 0)
                    {
                        var item = Core.WorldFilter[sel];
                        if (item != null)
                        {
                            if (IsValidEquipment(item))
                            {
                                if (!Settings.ItemRules.Any(x => x.Id == item.Id))
                                {
                                    Settings.ItemRules.Add(new ItemRule
                                    {
                                        Id = item.Id,
                                        Name = item.Name,
                                        Element = GetElement(item),
                                        KeepBuffed = true
                                    });
                                    OnSettingsChanged?.Invoke();
                                }
                                else Core.Actions.AddChatText($"[RynthAi] Item ID {item.Id} already in list.", 1);
                            }
                            else Core.Actions.AddChatText("[RynthAi] Selected item must be a weapon, wand, or shield.", 1);
                        }
                    }
                }
                ImGui.SameLine();
                ImGui.TextDisabled("(Select item in-game first)");
            }
            ImGui.End();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  NAVIGATION WINDOW
        // ═════════════════════════════════════════════════════════════════════
        private void RenderNavigationWindow()
        {
            ImGui.SetNextWindowSize(new Vector2(360, 400), ImGuiCond.FirstUseEver);
            if (ImGui.Begin("Navigation##RynthAiNav", ref DashWindows.ShowNavigation))
                RouteTab?.Render();
            ImGui.End();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  LUA SCRIPTS WINDOW
        // ═════════════════════════════════════════════════════════════════════
        private void RenderLuaWindow()
        {
            ImGui.SetNextWindowSize(new Vector2(400, 300), ImGuiCond.FirstUseEver);
            if (ImGui.Begin("Lua Scripts##RynthAiLua", ref DashWindows.ShowLua))
            {
                if (LuaTab != null)
                {
                    LuaTab.Render();
                }
                else
                {
                    ImGui.TextColored(ColTextDim, "Lua Engine is currently unavailable.");
                }
            }
            ImGui.End();
        }

        // ── DRAKHUD LOGIC (Embedded) ──────────────────────────────────────────
        private void UpdateTargetAppraisal()
        {
            int currentTarget = Core.Actions.CurrentSelection;
            bool isMonster = false;

            if (currentTarget != 0)
            {
                try
                {
                    WorldObject wo = Core.WorldFilter[currentTarget];
                    if (wo != null && wo.ObjectClass == ObjectClass.Monster)
                        isMonster = true;
                }
                catch { }
            }

            if (currentTarget != _lastTargetId)
            {
                _lastTargetId = currentTarget;
                _retryTargetId = currentTarget;
                _retryCount = 0;
                if (currentTarget != 0 && isMonster)
                {
                    try { Core.Actions.RequestId(currentTarget); _lastRequestTime = DateTime.Now; } catch { }
                }
            }

            if (currentTarget != 0 && isMonster && currentTarget == _retryTargetId && _retryCount < 3 &&
                !_maxHealthTracker.ContainsKey(currentTarget) && (DateTime.Now - _lastRequestTime).TotalSeconds >= 0.75)
            {
                try { Core.Actions.RequestId(currentTarget); _lastRequestTime = DateTime.Now; _retryCount++; } catch { }
            }
        }

        private void OnServerDispatch(object sender, NetworkMessageEventArgs e)
        {
            try
            {
                if (e.Message.Type == 0xF7B0)
                {
                    int eventType = (int)e.Message[2];
                    if (eventType == 0x1C0)
                    {
                        int targetId = (int)e.Message[3];
                        object rawHealth = e.Message[4];
                        float ratio = 1.0f;
                        if (rawHealth is float) ratio = (float)rawHealth;
                        else if (rawHealth is double) ratio = (float)(double)rawHealth;
                        else if (rawHealth is Single) ratio = (Single)rawHealth;
                        _healthRatioTracker[targetId] = ratio;
                    }
                    else if (eventType == 0xC9)
                    {
                        TryExtractVitals(e.Message);
                    }
                }
            }
            catch { }
        }

        private void TryExtractVitals(Decal.Adapter.Message msg)
        {
            try
            {
                Decal.Adapter.MessageStruct eventStruct = msg.Struct("event");
                if (eventStruct != null)
                {
                    int health = 0, healthMax = 0;
                    try { health = eventStruct.Value<int>("health"); } catch { }
                    try { healthMax = eventStruct.Value<int>("healthMax"); } catch { }

                    if (healthMax > 0)
                    {
                        int objectId = 0;
                        try { objectId = eventStruct.Value<int>("object"); } catch { }
                        if (objectId == 0) objectId = Core.Actions.CurrentSelection;

                        if (objectId != 0)
                        {
                            _maxHealthTracker[objectId] = healthMax;
                            if (healthMax > 0) _healthRatioTracker[objectId] = (float)health / (float)healthMax;
                            return;
                        }
                    }
                }
            }
            catch { }

            try
            {
                int health = 0, healthMax = 0;
                try { health = msg.Value<int>("health"); } catch { }
                try { healthMax = msg.Value<int>("healthMax"); } catch { }

                if (healthMax > 0)
                {
                    int objectId = Core.Actions.CurrentSelection;
                    if (objectId != 0)
                    {
                        _maxHealthTracker[objectId] = healthMax;
                        _healthRatioTracker[objectId] = (float)health / (float)healthMax;
                        return;
                    }
                }
            }
            catch { }
        }

        // ── Drawing Math & Custom Shapes ──────────────────────────────────────
        private void DrawIcon(string type, Vector2 pos, Vector4 color, float s)
        {
            var dl = ImGui.GetWindowDrawList();
            uint col = ImGui.ColorConvertFloat4ToU32(color);

            switch (type)
            {
                case "gear":
                    dl.AddCircle(pos + new Vector2(s / 2, s / 2), s / 3.5f, col, 12, 1.5f);
                    for (int i = 0; i < 8; i++)
                    {
                        float a = i * ((float)Math.PI * 2f / 8f);
                        dl.AddLine(pos + new Vector2(s / 2 + (float)Math.Cos(a) * s / 3, s / 2 + (float)Math.Sin(a) * s / 3),
                                   pos + new Vector2(s / 2 + (float)Math.Cos(a) * s / 2, s / 2 + (float)Math.Sin(a) * s / 2), col, 2f);
                    }
                    break;
                case "target":
                    dl.AddCircle(pos + new Vector2(s / 2, s / 2), s / 2.5f, col, 12, 1.5f);
                    dl.AddLine(pos + new Vector2(s / 2, 0), pos + new Vector2(s / 2, s), col, 1.5f);
                    dl.AddLine(pos + new Vector2(0, s / 2), pos + new Vector2(s, s / 2), col, 1.5f);
                    break;
                case "wrench":
                    dl.AddCircle(pos + new Vector2(s * 0.3f, s * 0.3f), s * 0.25f, col, 8, 1.5f);
                    dl.AddLine(pos + new Vector2(s * 0.4f, s * 0.4f), pos + new Vector2(s * 0.9f, s * 0.9f), col, 2f);
                    break;
                case "map":
                    dl.AddRect(pos, pos + new Vector2(s, s), col, 1.5f);
                    dl.AddLine(pos + new Vector2(s / 2, 2), pos + new Vector2(s / 2, s - 2), col, 1.5f);
                    dl.AddLine(pos + new Vector2(2, s / 2), pos + new Vector2(s - 2, s / 2), col, 1.5f);
                    break;
                case "bag":
                    dl.AddRectFilled(pos + new Vector2(2, s / 3), pos + new Vector2(s - 2, s), col, 2f);
                    dl.AddCircle(pos + new Vector2(s / 2, s / 4), s / 5, col, 8, 1.5f);
                    break;
                case "shield":
                    dl.AddLine(pos + new Vector2(s * 0.2f, 0), pos + new Vector2(s * 0.8f, 0), col, 1.5f);
                    dl.AddLine(pos + new Vector2(s * 0.8f, 0), pos + new Vector2(s * 0.8f, s * 0.6f), col, 1.5f);
                    dl.AddLine(pos + new Vector2(s * 0.8f, s * 0.6f), pos + new Vector2(s * 0.5f, s), col, 1.5f);
                    dl.AddLine(pos + new Vector2(s * 0.5f, s), pos + new Vector2(s * 0.2f, s * 0.6f), col, 1.5f);
                    dl.AddLine(pos + new Vector2(s * 0.2f, s * 0.6f), pos + new Vector2(s * 0.2f, 0), col, 1.5f);
                    break;
                case "code":
                    dl.AddLine(pos + new Vector2(s * 0.3f, 2), pos + new Vector2(0, s / 2), col, 1.5f);
                    dl.AddLine(pos + new Vector2(0, s / 2), pos + new Vector2(s * 0.3f, s - 2), col, 1.5f);
                    dl.AddLine(pos + new Vector2(s * 0.7f, 2), pos + new Vector2(s, s / 2), col, 1.5f);
                    dl.AddLine(pos + new Vector2(s, s / 2), pos + new Vector2(s * 0.7f, s - 2), col, 1.5f);
                    break;
                case "heart":
                    dl.AddCircleFilled(pos + new Vector2(s * 0.3f, s * 0.3f), s * 0.25f, col);
                    dl.AddCircleFilled(pos + new Vector2(s * 0.7f, s * 0.3f), s * 0.25f, col);
                    dl.AddTriangleFilled(pos + new Vector2(s * 0.05f, s * 0.4f), pos + new Vector2(s * 0.95f, s * 0.4f), pos + new Vector2(s * 0.5f, s * 0.9f), col);
                    break;
                case "run":
                    dl.AddCircle(pos + new Vector2(s * 0.5f, s * 0.2f), s * 0.15f, col, 8, 1.5f);
                    dl.AddLine(pos + new Vector2(s * 0.5f, s * 0.35f), pos + new Vector2(s * 0.5f, s * 0.65f), col, 1.5f);
                    dl.AddLine(pos + new Vector2(s * 0.5f, s * 0.65f), pos + new Vector2(s * 0.2f, s * 0.9f), col, 1.5f);
                    dl.AddLine(pos + new Vector2(s * 0.5f, s * 0.65f), pos + new Vector2(s * 0.8f, s * 0.9f), col, 1.5f);
                    dl.AddLine(pos + new Vector2(s * 0.2f, s * 0.4f), pos + new Vector2(s * 0.8f, s * 0.4f), col, 1.5f);
                    break;
                case "drop":
                    dl.AddCircleFilled(pos + new Vector2(s * 0.5f, s * 0.7f), s * 0.25f, col);
                    dl.AddTriangleFilled(pos + new Vector2(s * 0.25f, s * 0.65f), pos + new Vector2(s * 0.75f, s * 0.65f), pos + new Vector2(s * 0.5f, s * 0.1f), col);
                    break;
                case "sword":
                    dl.AddLine(pos + new Vector2(s * 0.3f, s * 0.7f), pos + new Vector2(s * 0.9f, s * 0.1f), col, 2f);
                    dl.AddLine(pos + new Vector2(s * 0.2f, s * 0.6f), pos + new Vector2(s * 0.4f, s * 0.8f), col, 2f);
                    dl.AddLine(pos + new Vector2(s * 0.1f, s * 0.9f), pos + new Vector2(s * 0.3f, s * 0.7f), col, 2f);
                    break;
                case "shoe":
                    dl.AddLine(pos + new Vector2(s * 0.3f, s * 0.2f), pos + new Vector2(s * 0.3f, s * 0.8f), col, 2f);
                    dl.AddLine(pos + new Vector2(s * 0.3f, s * 0.8f), pos + new Vector2(s * 0.8f, s * 0.8f), col, 2f);
                    dl.AddLine(pos + new Vector2(s * 0.8f, s * 0.8f), pos + new Vector2(s * 0.8f, s * 0.6f), col, 2f);
                    dl.AddLine(pos + new Vector2(s * 0.8f, s * 0.6f), pos + new Vector2(s * 0.5f, s * 0.5f), col, 2f);
                    dl.AddLine(pos + new Vector2(s * 0.5f, s * 0.5f), pos + new Vector2(s * 0.5f, s * 0.2f), col, 2f);
                    dl.AddLine(pos + new Vector2(s * 0.5f, s * 0.2f), pos + new Vector2(s * 0.3f, s * 0.2f), col, 2f);
                    break;
                case "buff":
                    dl.AddLine(pos + new Vector2(s * 0.5f, s * 0.2f), pos + new Vector2(s * 0.5f, s * 0.8f), col, 2f);
                    dl.AddLine(pos + new Vector2(s * 0.2f, s * 0.5f), pos + new Vector2(s * 0.8f, s * 0.5f), col, 2f);
                    dl.AddLine(pos + new Vector2(s * 0.3f, s * 0.3f), pos + new Vector2(s * 0.5f, s * 0.1f), col, 1.5f);
                    dl.AddLine(pos + new Vector2(s * 0.5f, s * 0.1f), pos + new Vector2(s * 0.7f, s * 0.3f), col, 1.5f);
                    break;
            }
        }

        private void DrawSquareToggle(string icon, ref bool state, Vector2 pos, string id)
        {
            var dl = ImGui.GetWindowDrawList();
            float s = 30f;

            Vector4 bgColor = state ? ColBtnOn : ColBarBg;
            Vector4 iconColor = state ? ColTeal : ColTextMute;

            dl.AddRectFilled(pos, pos + new Vector2(s, s), ImGui.ColorConvertFloat4ToU32(bgColor), 4f);
            if (state) dl.AddRect(pos, pos + new Vector2(s, s), ImGui.ColorConvertFloat4ToU32(ColTeal), 4f, ImDrawFlags.None, 1f);

            DrawIcon(icon, pos + new Vector2(6, 6), iconColor, 18f);

            ImGui.SetCursorScreenPos(pos);
            if (ImGui.InvisibleButton($"##{id}", new Vector2(s, s)))
            {
                state = !state;
                OnSettingsChanged?.Invoke();
            }
        }

        private void DrawWideToggle(string label, string icon, ref bool state, Vector2 pos, string id, float width, float height)
        {
            var dl = ImGui.GetWindowDrawList();

            Vector4 bgColor = state ? ColBtnOn : ColBarBg;
            Vector4 iconColor = state ? ColTeal : ColTextMute;

            dl.AddRectFilled(pos, pos + new Vector2(width, height), ImGui.ColorConvertFloat4ToU32(bgColor), 4f);
            if (state) dl.AddRect(pos, pos + new Vector2(width, height), ImGui.ColorConvertFloat4ToU32(ColTeal), 4f, ImDrawFlags.None, 1f);

            DrawIcon(icon, pos + new Vector2(4, (height - 12) / 2), iconColor, 12f);

            var textSize = ImGui.CalcTextSize(label);
            dl.AddText(pos + new Vector2(18, (height - textSize.Y) / 2), ImGui.ColorConvertFloat4ToU32(state ? ColTeal : ColTextDim), label);

            ImGui.SetCursorScreenPos(pos);
            if (ImGui.InvisibleButton($"##{id}", new Vector2(width, height)))
            {
                state = !state;
                OnSettingsChanged?.Invoke();
            }
        }

        private Vector4 GetGradientCol(float t)
        {
            if (t < 0.33f) { float f = t / 0.33f; return new Vector4(1f, f, 0f, 1f); }
            else if (t < 0.66f) { float f = (t - 0.33f) / 0.33f; return new Vector4(1f - f, 1f, 0f, 1f); }
            else { float f = (t - 0.66f) / 0.34f; return new Vector4(0f, 1f, f, 1f); }
        }

        private void DrawSegmentedBar(float pct, float width)
        {
            var pos = ImGui.GetCursorScreenPos();
            var dl = ImGui.GetWindowDrawList();
            float h = 10f; int segments = 15; float gap = 2f;
            float segW = (width - (segments - 1) * gap) / segments;

            for (int i = 0; i < segments; i++)
            {
                float t = (float)i / (segments - 1);
                Vector4 drawCol = (i / (float)segments <= pct) ? GetGradientCol(t) : ColBarBg;
                dl.AddRectFilled(pos + new Vector2(i * (segW + gap), 0), pos + new Vector2(i * (segW + gap) + segW, h), ImGui.ColorConvertFloat4ToU32(drawCol), 1f);
            }
            ImGui.Dummy(new Vector2(width, h + 2));
        }

        private void DrawVitalRow(string icon, string label, float pct, Vector4 color, string valText)
        {
            float width = ImGui.GetContentRegionAvail().X - 4f;
            var pos = ImGui.GetCursorScreenPos();
            var dl = ImGui.GetWindowDrawList();
            float h = 16f;

            dl.AddRectFilled(pos, pos + new Vector2(width, h), ImGui.ColorConvertFloat4ToU32(ColBarBg), 4f);
            float safePct = Math.Max(0, Math.Min(1, pct));
            dl.AddRectFilled(pos, pos + new Vector2(width * safePct, h), ImGui.ColorConvertFloat4ToU32(color), 4f);

            DrawIcon(icon, pos + new Vector2(4, 1), ColTextDim, 14f);
            string display = $"{label}: {(int)(safePct * 100)}% ({valText})";
            dl.AddText(pos + new Vector2(24, 0), ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 1)), display);

            ImGui.Dummy(new Vector2(width, h));
            ImGui.Spacing();
        }

        private static string TruncateName(string s, int max) => s != null && s.Length > max ? s.Substring(0, max - 1) + "..." : (s ?? "");

        // ── Backend Helpers ───────────────────────────────────────
        public void LoadSelectedNav()
        {
            if (_selectedNavIdx < 0 || _selectedNavIdx >= NavFiles.Count) return;
            string sel = NavFiles[_selectedNavIdx];
            if (sel == "None") { Settings.CurrentNavPath = ""; Settings.CurrentRoute.Points.Clear(); Settings.ActiveNavIndex = 0; }
            else
            {
                string fp = Path.Combine(_navFolder, sel + ".nav");
                Settings.CurrentNavPath = fp; Settings.CurrentRoute = VTankNavParser.Load(fp);
                Settings.ActiveNavIndex = (Settings.CurrentRoute.RouteType == NavRouteType.Once || Settings.CurrentRoute.RouteType == NavRouteType.Follow) ? 0 : FindNearestWaypoint(Settings.CurrentRoute);
            }
            if (RouteTab != null) RouteTab.NeedsRouteGraphicsRefresh = true; OnSettingsChanged?.Invoke();
        }

        private int FindNearestWaypoint(VTankNavParser route)
        {
            if (route?.Points == null || route.Points.Count == 0) return 0;
            try
            {
                var me = Core.WorldFilter[Core.CharacterFilter.Id]; if (me == null) return 0;
                var pos = me.Coordinates(); int best = 0; double bestD = double.MaxValue;
                for (int i = 0; i < route.Points.Count; i++)
                {
                    var pt = route.Points[i]; if (pt.Type != NavPointType.Point) continue;
                    double d = Math.Sqrt(Math.Pow(pt.NS - pos.NorthSouth, 2) + Math.Pow(pt.EW - pos.EastWest, 2));
                    if (d < bestD) { bestD = d; best = i; }
                }
                return best;
            }
            catch { return 0; }
        }

        private bool IsValidEquipment(WorldObject item)
        {
            if (item.ObjectClass == ObjectClass.MeleeWeapon ||
                item.ObjectClass == ObjectClass.MissileWeapon ||
                item.ObjectClass == ObjectClass.WandStaffOrb) return true;
            if (item.ObjectClass == ObjectClass.Armor &&
                (item.Name.IndexOf("Shield", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 item.Name.IndexOf("Defender", StringComparison.OrdinalIgnoreCase) >= 0)) return true;
            return false;
        }

        private string GetElement(WorldObject item)
        {
            switch (item.Values(LongValueKey.DamageType, 0))
            {
                case 1: return "Slash";
                case 2: return "Pierce";
                case 4: return "Bludgeon";
                case 8: return "Cold";
                case 16: return "Fire";
                case 32: return "Acid";
                case 64: return "Lightning";
                default: return "Slash";
            }
        }

        public void RefreshNavFiles()
        {
            NavFiles.Clear(); NavFiles.Add("None"); _selectedNavIdx = 0;
            if (!Directory.Exists(_navFolder)) return;
            foreach (var f in Directory.GetFiles(_navFolder, "*.nav"))
            {
                NavFiles.Add(Path.GetFileNameWithoutExtension(f));
                if (f.Equals(Settings.CurrentNavPath, StringComparison.OrdinalIgnoreCase)) _selectedNavIdx = NavFiles.Count - 1;
            }
        }

        public void RefreshLootFiles()
        {
            LootFiles.Clear(); LootFiles.Add("None");
            if (!Directory.Exists(_lootFolder)) return;
            foreach (var f in Directory.GetFiles(_lootFolder, "*.utl"))
            {
                LootFiles.Add(Path.GetFileNameWithoutExtension(f));
                if (f.Equals(Settings.CurrentLootPath, StringComparison.OrdinalIgnoreCase)) Settings.LootProfileIdx = LootFiles.Count - 1;
            }
        }

        public void RefreshMetaFiles()
        {
            try
            {
                MetaFiles.Clear(); MetaFiles.Add("None");
                if (!Directory.Exists(_metaFolder)) Directory.CreateDirectory(_metaFolder);
                foreach (var f in Directory.GetFiles(_metaFolder, "*.met")) MetaFiles.Add(Path.GetFileNameWithoutExtension(f));
            }
            catch { }
        }

        private void RefreshProfilesList()
        {
            try
            {
                Profiles.Clear();
                string srv = Core.CharacterFilter.Server ?? "UnknownServer";
                string chr = Core.CharacterFilter.Name ?? "UnknownCharacter";
                string path = Path.Combine(@"C:\Games\DecalPlugins\NexSuite\RynthAi\SettingsProfiles", srv, chr);
                Directory.CreateDirectory(path);
                foreach (string f in Directory.GetFiles(path, "*.json"))
                {
                    string n = Path.GetFileNameWithoutExtension(f);
                    if (n.ToLower() != "settings") Profiles.Add(n);
                }
                if (Profiles.Count == 0) Profiles.Add("Default");
            }
            catch { Profiles.Add("Default"); }
        }
    }
}