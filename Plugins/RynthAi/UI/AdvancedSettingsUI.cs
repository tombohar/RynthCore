using System;
using System.Numerics;
using ImGuiNET;

namespace NexSuite.Plugins.RynthAi
{
    public class AdvancedSettingsUI
    {
        private readonly UISettings _settings;
        private MissileCraftingManager _missileCraftingManager;
        public Action OnSettingsChanged;

        private static readonly string[] _attackHeights = { "Low", "Medium", "High" };
        private static readonly string[] _lootOwnershipModes = { "My Kills Only", "Fellowship Kills", "All Corpses" };
        private static readonly string[] _movementModes = { "Legacy (Autorun)", "Tier 1 (CM_Movement)", "Tier 2 (MoveToPosition)" };

        public AdvancedSettingsUI(UISettings settings)
        {
            _settings = settings;
        }

        public void SetMissileCraftingManager(MissileCraftingManager mgr)
        {
            _missileCraftingManager = mgr;
        }

        public void Render()
        {
            if (!_settings.ShowAdvancedWindow) return;

            // Set a default size for the popup window
            ImGui.SetNextWindowSize(new Vector2(600, 400), ImGuiCond.FirstUseEver);

            if (ImGui.Begin("RynthAi Advanced Settings", ref _settings.ShowAdvancedWindow))
            {
                // --- LEFT COLUMN (Sidebar) ---
                ImGui.BeginChild("AdvancedSidebar", new Vector2(150, 0), true);
                for (int i = 0; i < _settings.AdvancedTabs.Length; i++)
                {
                    if (ImGui.Selectable(_settings.AdvancedTabs[i], _settings.SelectedAdvancedTab == i))
                    {
                        _settings.SelectedAdvancedTab = i;
                    }
                }
                ImGui.EndChild();

                ImGui.SameLine();

                // --- RIGHT COLUMN (Content) ---
                ImGui.BeginGroup();
                ImGui.BeginChild("AdvancedContent", new Vector2(0, -ImGui.GetFrameHeightWithSpacing()), true);

                RenderTabContent(_settings.SelectedAdvancedTab);

                ImGui.EndChild();

                // Bottom Buttons
                if (ImGui.Button("Close")) _settings.ShowAdvancedWindow = false;
                ImGui.EndGroup();
            }
            ImGui.End();
        }

        private void RenderTabContent(int tabIndex)
        {
            if (tabIndex < 0 || tabIndex >= _settings.AdvancedTabs.Length) return;

            string tabName = _settings.AdvancedTabs[tabIndex];
            ImGui.TextColored(new System.Numerics.Vector4(0.4f, 0.7f, 1.0f, 1.0f), $"Advanced Settings > {tabName}");
            ImGui.Separator();
            ImGui.Spacing();

            // Note: Ensure your AdvancedSettingsUI class has an 'Action OnSettingsChanged' 
            // property that you set in the RynthAiUI constructor to handle auto-saving.

            switch (tabName)
            {
                case "Misc":
                    // --- FPS THROTTLING ---
                    if (ImGui.Checkbox("Enable FPS Limit", ref _settings.EnableFPSLimit)) OnSettingsChanged?.Invoke();
                    if (_settings.EnableFPSLimit)
                    {
                        ImGui.Indent();
                        ImGui.SetNextItemWidth(150);
                        if (ImGui.SliderInt("Focused FPS", ref _settings.TargetFPSFocused, 10, 240)) OnSettingsChanged?.Invoke();
                        ImGui.SetNextItemWidth(150);
                        if (ImGui.SliderInt("Background FPS", ref _settings.TargetFPSBackground, 5, 60)) OnSettingsChanged?.Invoke();
                        ImGui.Unindent();
                    }

                    ImGui.Separator();
                    ImGui.Spacing();

                    // --- INVENTORY MANAGEMENT ---
                    // Uses EnableAutocram from UISettings
                    if (ImGui.Checkbox("Auto Cram", ref _settings.EnableAutocram)) OnSettingsChanged?.Invoke();
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("Automatically moves items from your main pack into side packs.\n" +
                                         "Note: Recently used weapons will stay in the main pack for quick access.");
                    }

                    ImGui.Spacing();

                    // --- GENERAL BEHAVIOR ---
                    if (ImGui.Checkbox("Peace Mode When Idle", ref _settings.PeaceModeWhenIdle)) OnSettingsChanged?.Invoke();
                    if (ImGui.Checkbox("Enable Raycasting", ref _settings.EnableRaycasting)) OnSettingsChanged?.Invoke();

                    ImGui.Separator();
                    ImGui.Spacing();

                    // --- COMBAT BLACKLIST ---
                    ImGui.Text("Monster Blacklist");
                    ImGui.SetNextItemWidth(150);
                    if (ImGui.SliderInt("Attempts Before Blacklist", ref _settings.BlacklistAttempts, 1, 20)) OnSettingsChanged?.Invoke();
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("How many failed attack attempts on a mob before it gets blacklisted.\n" +
                                         "Helps avoid getting stuck on mobs clipped into walls or geometry.");
                    }
                    ImGui.SetNextItemWidth(150);
                    if (ImGui.SliderInt("Blacklist Timeout (sec)", ref _settings.BlacklistTimeoutSec, 5, 120)) OnSettingsChanged?.Invoke();
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("How long (seconds) a blacklisted mob is ignored before re-trying.");
                    }
                    break;

                case "Recharge":
                    ImGui.Text("Self Vitals (%)");
                    ImGui.SetNextItemWidth(120);
                    if (ImGui.SliderInt("Heal At", ref _settings.HealAt, 0, 100)) OnSettingsChanged?.Invoke();
                    ImGui.SetNextItemWidth(120);
                    if (ImGui.SliderInt("Re-stam At", ref _settings.RestamAt, 0, 100)) OnSettingsChanged?.Invoke();
                    ImGui.SetNextItemWidth(120);
                    if (ImGui.SliderInt("Get Mana At", ref _settings.GetManaAt, 0, 100)) OnSettingsChanged?.Invoke();

                    ImGui.Spacing();
                    ImGui.Text("Top-Off Vitals (%)");
                    ImGui.SetNextItemWidth(120);
                    if (ImGui.SliderInt("Top HP", ref _settings.TopOffHP, 0, 100)) OnSettingsChanged?.Invoke();
                    ImGui.SetNextItemWidth(120);
                    if (ImGui.SliderInt("Top Stam", ref _settings.TopOffStam, 0, 100)) OnSettingsChanged?.Invoke();
                    ImGui.SetNextItemWidth(120);
                    if (ImGui.SliderInt("Top Mana", ref _settings.TopOffMana, 0, 100)) OnSettingsChanged?.Invoke();

                    ImGui.Spacing();
                    ImGui.Text("Helper Settings (%)");
                    ImGui.SetNextItemWidth(120);
                    if (ImGui.SliderInt("Heal Others", ref _settings.HealOthersAt, 0, 100)) OnSettingsChanged?.Invoke();
                    ImGui.SetNextItemWidth(120);
                    if (ImGui.SliderInt("Re-stam Others", ref _settings.RestamOthersAt, 0, 100)) OnSettingsChanged?.Invoke();
                    ImGui.SetNextItemWidth(120);
                    if (ImGui.SliderInt("Infuse Others", ref _settings.InfuseOthersAt, 0, 100)) OnSettingsChanged?.Invoke();
                    break;

                case "Melee Combat":
                    ImGui.Text("Attack Power & Height");
                    ImGui.Separator();

                    // --- USE RECKLESSNESS ---
                    if (ImGui.Checkbox("Use Recklessness", ref _settings.UseRecklessness)) OnSettingsChanged?.Invoke();
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("When enabled and Recklessness is trained,\nauto power is set to 80% instead of 100%.\nThis activates the Recklessness damage bonus.");

                    ImGui.Spacing();

                    // --- MELEE ATTACK POWER ---
                    bool meleeAuto = _settings.MeleeAttackPower < 0;
                    if (ImGui.Checkbox("Melee Auto Power", ref meleeAuto))
                    {
                        _settings.MeleeAttackPower = meleeAuto ? -1 : 100;
                        OnSettingsChanged?.Invoke();
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Auto = 100%, or 80% if Use Recklessness is on.");
                    if (!meleeAuto)
                    {
                        ImGui.Indent();
                        int mPwr = _settings.MeleeAttackPower;
                        ImGui.SetNextItemWidth(150);
                        if (ImGui.SliderInt("Melee Power %", ref mPwr, 0, 100))
                        {
                            _settings.MeleeAttackPower = mPwr;
                            OnSettingsChanged?.Invoke();
                        }
                        ImGui.Unindent();
                    }

                    // --- MELEE ATTACK HEIGHT ---
                    ImGui.SetNextItemWidth(120);
                    if (ImGui.Combo("Melee Attack Height", ref _settings.MeleeAttackHeight, _attackHeights, _attackHeights.Length))
                        OnSettingsChanged?.Invoke();
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Low = Delete, Medium = End, High = Page Down");

                    ImGui.Spacing();

                    // --- MISSILE ATTACK POWER ---
                    bool missileAuto = _settings.MissileAttackPower < 0;
                    if (ImGui.Checkbox("Missile Auto Power", ref missileAuto))
                    {
                        _settings.MissileAttackPower = missileAuto ? -1 : 100;
                        OnSettingsChanged?.Invoke();
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Auto = 100%, or 80% if Use Recklessness is on.");
                    if (!missileAuto)
                    {
                        ImGui.Indent();
                        int rPwr = _settings.MissileAttackPower;
                        ImGui.SetNextItemWidth(150);
                        if (ImGui.SliderInt("Missile Power %", ref rPwr, 0, 100))
                        {
                            _settings.MissileAttackPower = rPwr;
                            OnSettingsChanged?.Invoke();
                        }
                        ImGui.Unindent();
                    }

                    // --- MISSILE ATTACK HEIGHT ---
                    ImGui.SetNextItemWidth(120);
                    if (ImGui.Combo("Missile Attack Height", ref _settings.MissileAttackHeight, _attackHeights, _attackHeights.Length))
                        OnSettingsChanged?.Invoke();
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Low = Delete, Medium = End, High = Page Down");

                    ImGui.Separator();
                    ImGui.Spacing();

                    // You can add weapon-specific toggles here later
                    if (ImGui.Checkbox("Summon Pets", ref _settings.SummonPets)) OnSettingsChanged?.Invoke();
                    ImGui.SetNextItemWidth(120);
                    if (ImGui.InputInt("Pet Min Monsters", ref _settings.PetMinMonsters)) OnSettingsChanged?.Invoke();
                    break;

                case "Spell Combat":
                    ImGui.Text("War/Void Casting Settings");
                    ImGui.Separator();
                    if (ImGui.Checkbox("Cast Dispel Self", ref _settings.CastDispelSelf)) OnSettingsChanged?.Invoke();

                    ImGui.Spacing();
                    ImGui.Text("Ring Spell Override");
                    ImGui.SetNextItemWidth(120);
                    if (ImGui.InputInt("Min Ring Targets", ref _settings.MinRingTargets)) OnSettingsChanged?.Invoke();
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("If this many monsters are within Ring Range,\nring spells are used instead of arc/bolt/streak.\nSet to 0 to disable the override.\nDefault: 4");
                    }
                    break;

                case "Ranges":
                    ImGui.Text("Standard Ranges (Yards)");
                    ImGui.SetNextItemWidth(120);
                    if (ImGui.InputInt("Monster Range", ref _settings.MonsterRange)) OnSettingsChanged?.Invoke();
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("Detection range in yards. Same units as VTank.\n" +
                                         "Max bow range ≈ 70 yards. Magic ≈ 68 yards.\n" +
                                         "AC internal = yards / 240");
                    }
                    ImGui.SetNextItemWidth(120);
                    if (ImGui.InputInt("Ring Range", ref _settings.RingRange)) OnSettingsChanged?.Invoke();
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("Distance at which Ring spells are used instead of other shapes.");
                    }
                    ImGui.SetNextItemWidth(120);
                    if (ImGui.InputInt("Approach Range", ref _settings.ApproachRange)) OnSettingsChanged?.Invoke();

                    ImGui.Spacing();
                    ImGui.Text("Corpse Acquisition (AC Units)");
                    ImGui.Separator();
                    // MOVED FROM MAIN TAB
                    ImGui.SetNextItemWidth(180);
                    if (ImGui.InputDouble("Corpse Max", ref _settings.CorpseApproachRangeMax, 0.005, 0.01, "%.6f")) OnSettingsChanged?.Invoke();
                    ImGui.SetNextItemWidth(180);
                    if (ImGui.InputDouble("Corpse Min", ref _settings.CorpseApproachRangeMin, 0.005, 0.01, "%.6f")) OnSettingsChanged?.Invoke();
                    break;

                case "Navigation":
                    if (ImGui.Checkbox("Boost Nav Priority", ref _settings.BoostNavPriority)) OnSettingsChanged?.Invoke();
                    ImGui.SetNextItemWidth(120);
                    if (ImGui.InputFloat("Follow/Nav Min", ref _settings.FollowNavMin, 0.1f, 1.0f, "%.1f")) OnSettingsChanged?.Invoke();

                    ImGui.Spacing();
                    ImGui.Text("Movement Engine");
                    ImGui.SetNextItemWidth(200);
                    if (ImGui.Combo("Mode", ref _settings.MovementMode, _movementModes, _movementModes.Length))
                        OnSettingsChanged?.Invoke();
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(
                            "Legacy: SetAutorun + TurnLeft/TurnRight (VTank-style)\n" +
                            "Tier 1: Direct CM_Movement server events\n" +
                            "Tier 2: Client physics MoveToPosition (smooth arcs, experimental)");
                    if (_settings.MovementMode == 2 && !Tier2MovementHelper.IsInitialized)
                    {
                        ImGui.SameLine();
                        ImGui.TextColored(new Vector4(1, 0.4f, 0.4f, 1), "(not initialized)");
                    }

                    // ── Steering (all modes) ─────────────────────────────────
                    ImGui.Spacing();
                    ImGui.Separator();
                    ImGui.Text("Steering");

                    ImGui.SetNextItemWidth(80);
                    if (ImGui.InputFloat("Stop & Turn Angle", ref _settings.NavStopTurnAngle, 1f, 5f, "%.0f")) OnSettingsChanged?.Invoke();
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Stop forward motion and turn in place when heading error exceeds this many degrees.\nLower = more precise but stop-start. Higher = wider arcs.");

                    ImGui.SetNextItemWidth(80);
                    if (ImGui.InputFloat("Resume Run Angle", ref _settings.NavResumeTurnAngle, 1f, 5f, "%.0f")) OnSettingsChanged?.Invoke();
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Resume running once the turn-in-place error drops below this.\nMust be less than Stop & Turn Angle.");

                    ImGui.SetNextItemWidth(80);
                    if (ImGui.InputFloat("Dead Zone", ref _settings.NavDeadZone, 0.5f, 1f, "%.1f")) OnSettingsChanged?.Invoke();
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Ignore heading corrections smaller than this.\nSmaller = tighter line but more twitchy. Larger = smoother but wider path.");

                    ImGui.SetNextItemWidth(80);
                    if (ImGui.InputFloat("Sweep Detect Mult", ref _settings.NavSweepMult, 0.5f, 1f, "%.1f")) OnSettingsChanged?.Invoke();
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Closest-approach detection radius = Arrival Radius * this.\nIf distance was decreasing and starts increasing within this radius, advance to next waypoint.\nHigher = more forgiving on tight turns. Lower = must pass closer.");

                    // ── Tier 2 tuning (only visible when mode 2 selected) ────
                    if (_settings.MovementMode == 2)
                    {
                        ImGui.Spacing();
                        ImGui.Separator();
                        ImGui.Text("Tier 2 Tuning");

                        ImGui.SetNextItemWidth(100);
                        if (ImGui.InputFloat("Speed##t2", ref _settings.T2Speed, 0.1f, 0.5f, "%.1f")) OnSettingsChanged?.Invoke();
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Movement speed multiplier (1.0 = normal run speed)");

                        ImGui.SetNextItemWidth(100);
                        if (ImGui.InputFloat("Walk Within (yd)", ref _settings.T2WalkWithinYd, 1f, 5f, "%.0f")) OnSettingsChanged?.Invoke();
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Switch from run to walk when within this many yards of the waypoint.\n0 = always run.");

                        ImGui.SetNextItemWidth(100);
                        if (ImGui.InputFloat("Stop Distance (yd)", ref _settings.T2DistanceTo, 0.1f, 0.5f, "%.1f")) OnSettingsChanged?.Invoke();
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Stop this far from the target position (yards)");

                        ImGui.SetNextItemWidth(100);
                        if (ImGui.InputFloat("Reissue Timeout (ms)", ref _settings.T2ReissueMs, 100f, 500f, "%.0f")) OnSettingsChanged?.Invoke();
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Re-send MoveToPosition if no progress after this many ms");

                        ImGui.SetNextItemWidth(100);
                        if (ImGui.InputFloat("Max Range (yd)", ref _settings.T2MaxRangeYd, 50f, 100f, "%.0f")) OnSettingsChanged?.Invoke();
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Clamp waypoint distance to this. Will re-issue as you get closer.");

                        ImGui.SetNextItemWidth(100);
                        if (ImGui.InputInt("Max Landblock Dist", ref _settings.T2MaxLandblocks, 1)) OnSettingsChanged?.Invoke();
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Skip MoveToPosition if target is more than N landblocks away");
                    }
                    break;

                case "Buffing":
                    if (ImGui.Checkbox("Enable Buffing", ref _settings.EnableBuffing)) OnSettingsChanged?.Invoke();
                    if (ImGui.Checkbox("Rebuff When Idle", ref _settings.RebuffWhenIdle)) OnSettingsChanged?.Invoke();
                    break;

                case "Looting":
                    if (ImGui.Checkbox("Enable Looting", ref _settings.EnableLooting)) OnSettingsChanged?.Invoke();
                    if (ImGui.Checkbox("Boost Loot Priority", ref _settings.BoostLootPriority)) OnSettingsChanged?.Invoke();
                    if (ImGui.Checkbox("Loot Only Rare Corpses", ref _settings.LootOnlyRareCorpses)) OnSettingsChanged?.Invoke();

                    ImGui.Spacing();
                    ImGui.Text("Corpse Ownership");
                    ImGui.SetNextItemWidth(180);
                    if (ImGui.Combo("Loot From", ref _settings.LootOwnership, _lootOwnershipModes, _lootOwnershipModes.Length))
                        OnSettingsChanged?.Invoke();
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("My Kills Only: Only loot corpses you killed.\nFellowship Kills: Loot kills by any fellowship member.\nAll Corpses: Loot everything within range.");

                    ImGui.Spacing();
                    ImGui.Text("Inventory Management");
                    if (ImGui.Checkbox("Enable Autostack", ref _settings.EnableAutostack)) OnSettingsChanged?.Invoke();
                    if (ImGui.Checkbox("Enable Autocram", ref _settings.EnableAutocram)) OnSettingsChanged?.Invoke();
                    if (ImGui.Checkbox("Combine Salvage Bags", ref _settings.EnableCombineSalvage)) OnSettingsChanged?.Invoke();
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("When enabled, salvage bags of the same material type\nare automatically combined after salvaging.");
                    }

                    ImGui.Spacing();
                    ImGui.Separator();
                    ImGui.Text("Loot Timers (ms)");
                    ImGui.SetNextItemWidth(120);
                    if (ImGui.InputInt("Inter-Item Delay", ref _settings.LootInterItemDelayMs)) OnSettingsChanged?.Invoke();
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Delay between picking up each item from a corpse.");
                    ImGui.SetNextItemWidth(120);
                    if (ImGui.InputInt("Content Settle", ref _settings.LootContentSettleMs)) OnSettingsChanged?.Invoke();
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Wait for corpse contents to populate after opening.");
                    ImGui.SetNextItemWidth(120);
                    if (ImGui.InputInt("Empty Corpse Wait", ref _settings.LootEmptyCorpseMs)) OnSettingsChanged?.Invoke();
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("How long to wait before declaring a corpse empty.");
                    ImGui.SetNextItemWidth(120);
                    if (ImGui.InputInt("Closing Delay", ref _settings.LootClosingDelayMs)) OnSettingsChanged?.Invoke();
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Delay before closing corpse after all items looted.");
                    ImGui.SetNextItemWidth(120);
                    if (ImGui.InputInt("Assess Window", ref _settings.LootAssessWindowMs)) OnSettingsChanged?.Invoke();
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Max time to wait for item ID data from server.");
                    ImGui.SetNextItemWidth(120);
                    if (ImGui.InputInt("Loot Retry Timeout", ref _settings.LootRetryTimeoutMs)) OnSettingsChanged?.Invoke();
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Retry picking up an item after this many ms.");
                    ImGui.SetNextItemWidth(120);
                    if (ImGui.InputInt("Corpse Open Retry", ref _settings.LootOpenRetryMs)) OnSettingsChanged?.Invoke();
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("If corpse doesn't open, retry UseItem after this many ms.\nLower = faster but may toggle-close an already-opening corpse.");

                    ImGui.Spacing();
                    ImGui.Separator();
                    ImGui.Text("Salvage Timers (ms) — First / Fast");
                    ImGui.SetNextItemWidth(120);
                    if (ImGui.InputInt("Open (First)", ref _settings.SalvageOpenDelayFirstMs)) OnSettingsChanged?.Invoke();
                    ImGui.SetNextItemWidth(120);
                    if (ImGui.InputInt("Open (Fast)", ref _settings.SalvageOpenDelayFastMs)) OnSettingsChanged?.Invoke();
                    ImGui.SetNextItemWidth(120);
                    if (ImGui.InputInt("Add Item (First)", ref _settings.SalvageAddDelayFirstMs)) OnSettingsChanged?.Invoke();
                    ImGui.SetNextItemWidth(120);
                    if (ImGui.InputInt("Add Item (Fast)", ref _settings.SalvageAddDelayFastMs)) OnSettingsChanged?.Invoke();
                    ImGui.SetNextItemWidth(120);
                    if (ImGui.InputInt("Salvage Click", ref _settings.SalvageSalvageDelayMs)) OnSettingsChanged?.Invoke();
                    ImGui.SetNextItemWidth(120);
                    if (ImGui.InputInt("Result (First)", ref _settings.SalvageResultDelayFirstMs)) OnSettingsChanged?.Invoke();
                    ImGui.SetNextItemWidth(120);
                    if (ImGui.InputInt("Result (Fast)", ref _settings.SalvageResultDelayFastMs)) OnSettingsChanged?.Invoke();
                    break;

                case "Crafting":
                    ImGui.Text("Missile Ammo Crafting");
                    ImGui.Separator();
                    ImGui.Spacing();

                    if (ImGui.Checkbox("Enable Missile Crafting", ref _settings.EnableMissileCrafting)) OnSettingsChanged?.Invoke();
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Auto-manage missile ammo when the ammo slot is empty:\n\n1. If better ammo can be crafted from bundles → craft it\n2. Otherwise equip the best loose ammo in inventory\n3. If no ammo exists → craft from bundles, then equip\n\nRequires Fletching skill (Trained or Specialized).\nEach combine: 1 head bundle + 1 shaft bundle = 1000 ammo.");

                    if (!_settings.EnableMissileCrafting) { ImGui.TextDisabled("(Disabled)"); break; }

                    ImGui.Spacing();
                    ImGui.Separator();
                    ImGui.Spacing();

                    if (_missileCraftingManager != null)
                    {
                        string stateLabel = _missileCraftingManager.State.ToString();
                        Vector4 stateColor = _missileCraftingManager.IsCrafting
                            ? new Vector4(0.2f, 1.0f, 0.4f, 1.0f)
                            : new Vector4(0.6f, 0.6f, 0.6f, 1.0f);

                        ImGui.TextColored(stateColor, "State: " + stateLabel);

                        if (!string.IsNullOrEmpty(_missileCraftingManager.StatusMessage))
                            ImGui.TextWrapped(_missileCraftingManager.StatusMessage);
                    }
                    else
                    {
                        ImGui.TextDisabled("Crafting manager not initialized.");
                    }

                    ImGui.Spacing();
                    ImGui.Separator();
                    ImGui.Spacing();

                    ImGui.TextDisabled("Ammo Types (by priority, highest first):");
                    ImGui.BulletText("Lethal Prismatic (Specialized)");
                    ImGui.BulletText("Deadly Prismatic (Specialized)");
                    ImGui.BulletText("Greater Prismatic (Trained)");
                    ImGui.BulletText("Prismatic (Trained)");
                    ImGui.BulletText("Armor Piercing, Broad, Blunt, etc. (Trained)");
                    ImGui.Spacing();
                    ImGui.TextDisabled("Weapon → Ammo:");
                    ImGui.BulletText("Bow → Arrows (arrowheads + arrowshafts)");
                    ImGui.BulletText("Crossbow → Quarrels (quarrelheads + quarrelshafts)");
                    ImGui.BulletText("Atlatl → Darts (dart heads + dart shafts)");
                    break;

                default:
                    ImGui.Text($"Settings for {tabName} are currently under development.");
                    break;
            }
        }
    }
}