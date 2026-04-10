using System;
using System.Collections.Generic;
using System.Linq;
using Decal.Adapter;
using Decal.Adapter.Wrappers;
using Decal.Filters;
using NexSuite.Plugins.RynthAi.Raycasting;

namespace NexSuite.Plugins.RynthAi
{
    public class CombatManager : IDisposable
    {
        private CoreManager _coreManager;
        private PluginHost _host;
        private RynthAiUI _ui;
        private UISettings _settings;
        private SpellManager _spellManager;

        private MainLogic _raycastSystem;
        public bool RaycastInitialized { get; private set; }

        public int activeTargetId = 0;
        private DateTime lastAttackCmd = DateTime.MinValue;
        private DateTime lastStanceAttempt = DateTime.MinValue;
        private DateTime _lastPeaceAttempt = DateTime.MinValue;
        private DateTime lastTargetSearchTime = DateTime.MinValue;
        private const int TARGET_SEARCH_INTERVAL_MS = 500;

        // --- Magic Combat ---
        private DateTime _lastSpellCast = DateTime.MinValue;
        private const double SPELL_CAST_COOLDOWN_MS = 1500.0;   // Debuff cast recovery
        private const double ATTACK_SPELL_COOLDOWN_MS = 100.0;   // War/void spam as fast as possible

        // Flag: when melee/missile needs debuffs, we temporarily switch to wand.
        // After debuffs are done, switch back.
        private bool _returnToPhysicalCombat = false;
        private int _savedWeaponId = 0;   // Weapon to re-equip after debuffs

        // Element vulnerability spell bases (used by V light)
        private static readonly Dictionary<string, string[]> VulnSpells = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "Fire",      new[] { "Fire Vulnerability Other" } },
            { "Cold",      new[] { "Cold Vulnerability Other" } },
            { "Lightning", new[] { "Lightning Vulnerability Other" } },
            { "Acid",      new[] { "Acid Vulnerability Other" } },
            { "Blade",     new[] { "Blade Vulnerability Other" } },
            { "Slash",     new[] { "Blade Vulnerability Other" } },
            { "Pierce",    new[] { "Piercing Vulnerability Other" } },
            { "Bludgeon",  new[] { "Bludgeoning Vulnerability Other" } },
        };

        private Dictionary<int, BlacklistedTarget> blacklistedTargets = new Dictionary<int, BlacklistedTarget>();

        // Configurable blacklist manager — uses UISettings.BlacklistAttempts/BlacklistTimeoutSec
        private Raycasting.BlacklistManager _blacklistManager = new Raycasting.BlacklistManager();
        private DateTime _lastAttackFeedbackCheck = DateTime.MinValue;
        private int _lastAttackedTargetId = 0;

        public int RaycastBlockCount { get; private set; }
        public int RaycastCheckCount { get; private set; }



        private class BlacklistedTarget
        {
            public int TargetId { get; set; }
            public DateTime BlacklistedTime { get; set; }
            public bool IsLosBlocked { get; set; }
            public bool IsExpired()
            {
                // LOS-blocked targets get a shorter blacklist (5s) since player moves
                double duration = IsLosBlocked ? 5000 : 20000;
                return (DateTime.Now - BlacklistedTime).TotalMilliseconds > duration;
            }
        }

        public CombatManager(CoreManager coreManager, UISettings settings, PluginHost host = null, SpellManager spellManager = null)
        {
            _coreManager = coreManager ?? throw new ArgumentNullException(nameof(coreManager));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _host = host;
            _spellManager = spellManager;
        }

        public void SetSpellManager(SpellManager spellManager)
        {
            _spellManager = spellManager;
        }

        // --- Monster Weaknesses (from monsters.json) ---
        private Dictionary<string, string> _monsterWeaknesses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public void SetMonsterWeaknesses(Dictionary<string, string> weaknesses)
        {
            _monsterWeaknesses = weaknesses ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        // --- Debuff tracking (chat-driven: spam until confirmed) ---
        // Tracks which debuffs have been CONFIRMED on the current target via chat
        // Key: "targetId_debuffKey" — only added on "You cast X" chat message
        private HashSet<string> _confirmedDebuffs = new HashSet<string>();
        private int _lastDebuffTargetId = 0;

        // Pending debuff: cast sent, waiting for chat result
        private string _pendingDebuffKey = null;
        private int _pendingDebuffTargetId = 0;
        private int _pendingDebuffTier = 0;
        private bool _waitingForDebuffResult = false;
        private DateTime _pendingDebuffCastTime = DateTime.MinValue;
        private const double DEBUFF_RESULT_TIMEOUT_MS = 3000.0; // Safety: assume success after 3s

        // Debuff spell definitions — base name for tiered resolution, plus optional lore name
        // Format: { key, (tieredBase, loreName) }
        // Resolver order: Incantation of [base] → [lore] → [base] VII → VI → ... → I
        private static readonly Dictionary<string, string[]> DebuffSpells = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            //                 { key,       { tieredBase,                          loreName } }
            { "Fester",    new[] { "Fester Other",                       "Decrepitude's Grasp" } },
            { "Broadside", new[] { "Missile Weapon Ineptitude Other",    "Broadside of a Barn" } },
            { "Gravity",   new[] { "Vulnerability Other",                "Gravity Well" } },
            { "Imperil",   new[] { "Imperil Other",                      "Gossamer Flesh" } },
            { "Yield",     new[] { "Magic Yield Other",                  "Yield" } },
        };

        // Spell shape base names for war/void
        // Arc spells: "X Arc" pattern
        // Ring spells: "X Ring" pattern (+ lore names for T6/T7/T8)  
        // Streak spells: "X Streak" pattern
        // Bolt spells: "X Bolt" pattern (default)
        private static readonly Dictionary<string, string[]> SpellShapes = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            // element → { Arc, Ring, Streak, Bolt } base patterns
            { "Fire",      new[] { "Flame Arc", "Ring of Fire", "Flame Streak", "Flame Bolt" } },
            { "Cold",      new[] { "Frost Arc", "Frost Ring", "Frost Streak", "Frost Bolt" } },
            { "Lightning", new[] { "Lightning Arc", "Shock Ring", "Lightning Streak", "Shock Wave" } },
            { "Acid",      new[] { "Acid Arc", "Acid Ring", "Acid Streak", "Acid Stream" } },
            { "Blade",     new[] { "Blade Arc", "Blade Ring", "Blade Streak", "Whirling Blade" } },
            { "Pierce",    new[] { "Force Arc", "Force Ring", "Force Streak", "Force Bolt" } },
            { "Bludgeon",  new[] { "Bludgeoning Arc", "Bludgeoning Ring", "Bludgeoning Streak", "Shock Wave" } },
            { "Slash",     new[] { "Blade Arc", "Blade Ring", "Blade Streak", "Whirling Blade" } },
        };

        // Ring spell LORE NAMES — AC ring spells use unique names at T6/T7
        // These don't follow the standard "Base VII" pattern
        // Format: element → { T6 lore, T7 lore }
        private static readonly Dictionary<string, string[]> RingLoreNames = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "Fire",      new[] { "Cassius' Ring of Fire", "Cassius' Ring of Fire II" } },
            { "Cold",      new[] { "Halo of Frost", "Halo of Frost II" } },
            { "Lightning", new[] { "Eye of the Storm", "Eye of the Storm II" } },
            { "Acid",      new[] { "Searing Disc", "Searing Disc II" } },
            { "Blade",     new[] { "Horizon's Blades", "Horizon's Blades II" } },
            { "Slash",     new[] { "Horizon's Blades", "Horizon's Blades II" } },
            { "Pierce",    new[] { "Nuhumudira's Spines", "Nuhumudira's Spines II" } },
            { "Bludgeon",  new[] { "Tectonic Rifts", "Tectonic Rifts II" } },
        };

        // Void Magic equivalents
        private static readonly Dictionary<string, string[]> VoidSpellShapes = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "Nether",    new[] { "Nether Arc", "Nether Ring", "Nether Streak", "Nether Bolt" } },
            { "Fire",      new[] { "Corrosion Arc", "Corrosion Ring", "Corrosion Streak", "Nether Bolt" } },
        };

        public void InitializeRaycasting(string acFolderPath = null)
        {
            try
            {
                _raycastSystem = new MainLogic();
                if (string.IsNullOrEmpty(acFolderPath)) acFolderPath = @"C:\Turbine\Asheron's Call";
                RaycastInitialized = _raycastSystem.Initialize(acFolderPath);
            }
            catch { RaycastInitialized = false; }
        }

        public void AttachUI(RynthAiUI ui) { _ui = ui; }

        /// <summary>
        /// Called by PluginCore when chat messages arrive. Tracks debuff results:
        /// "You cast X on Y" → SUCCESS → add to confirmed set, immediately ready for next
        /// "Your spell fizzled" → FIZZLE → clear pending, recast same debuff
        /// "Y resists your spell" → RESIST → clear pending, recast same debuff
        /// </summary>
        public void HandleChatForDebuffs(string text)
        {
            if (!_waitingForDebuffResult || string.IsNullOrEmpty(text)) return;

            // SUCCESS: "You cast Incantation of Imperil Other on Drudge Skulker"
            if (text.StartsWith("You cast ", StringComparison.OrdinalIgnoreCase))
            {
                string key = $"{_pendingDebuffTargetId}_{_pendingDebuffKey}";
                _confirmedDebuffs.Add(key);
                _waitingForDebuffResult = false;
                _lastSpellCast = DateTime.Now; // Reset cooldown from this moment
                return;
            }

            // FIZZLE: "Your spell fizzled."
            if (text.IndexOf("fizzle", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                try { _host?.Actions?.AddChatText($"[RynthAi] Fizzled: {_pendingDebuffKey} — recasting", 2); } catch { }
                _waitingForDebuffResult = false;
                _lastSpellCast = DateTime.Now; // Reset cooldown from this moment
                return;
            }

            // RESIST: "X resists your spell"
            if (text.IndexOf("resists your spell", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                try { _host?.Actions?.AddChatText($"[RynthAi] Resisted: {_pendingDebuffKey} — recasting", 2); } catch { }
                _waitingForDebuffResult = false;
                _lastSpellCast = DateTime.Now;
                return;
            }
        }

        /// <summary>
        /// Called by PluginCore when a health ratio update is received for a target.
        /// Clears the blacklist failure counter since the mob is taking damage.
        /// </summary>
        public void ReportDamageOnTarget(int targetId)
        {
            _blacklistManager.ClearFailure(targetId);
        }

        /// <summary>
        /// Called internally after an attack command is sent. If no damage feedback
        /// arrives within a window, the next attack attempt increments the failure count.
        /// </summary>
        private void TrackAttackAttempt(int targetId)
        {
            if (_lastAttackedTargetId != 0 && _lastAttackedTargetId == targetId)
            {
                // We attacked this same target last tick too — if no damage feedback
                // was received since last attack, count it as a failure
                if (_lastAttackFeedbackCheck != DateTime.MinValue && 
                    (DateTime.Now - _lastAttackFeedbackCheck).TotalMilliseconds > 2500)
                {
                    _blacklistManager.ReportFailure(targetId);
                    if (_blacklistManager.IsBlacklisted(targetId))
                    {
                        try { _host?.Actions?.AddChatText($"[RynthAi] Blacklisted mob (no damage feedback): id={targetId}", 2); } catch { }
                    }
                }
            }
            _lastAttackedTargetId = targetId;
            _lastAttackFeedbackCheck = DateTime.Now;
        }

        public bool Think()
        {
            if (!_settings.EnableCombat) return false;

            double acDistanceLimit = (_settings.MonsterRange) / 240.0;
            CleanupExpiredBlacklist();

            // Sync configurable blacklist settings from UI
            _blacklistManager.AttemptThreshold = _settings.BlacklistAttempts;
            _blacklistManager.TimeoutSeconds = _settings.BlacklistTimeoutSec;

            // Update raycast scan distance: MonsterRange + 40m buffer
            // MonsterRange is in yards ≈ meters. Convert to actual meters for the scan radius.
            if (_raycastSystem?.TargetingFSM != null)
                _raycastSystem.TargetingFSM.MaxScanDistanceMeters = _settings.MonsterRange + 40.0f;

            // Validate current target
            if (activeTargetId != 0)
            {
                var target = _coreManager.WorldFilter[activeTargetId];
                if (target == null || (int)target.ObjectClass != 5 || blacklistedTargets.ContainsKey(activeTargetId) || _blacklistManager.IsBlacklisted(activeTargetId))
                    activeTargetId = 0;
                // Target lock: don't drop a target we're actively fighting just because
                // it moved slightly. Use 1.5x range for disengage so we finish the kill.
                else if (_coreManager.WorldFilter.Distance(_coreManager.CharacterFilter.Id, activeTargetId) > acDistanceLimit * 1.5)
                    activeTargetId = 0;
            }

            // Find a new target if we don't have one
            if (activeTargetId == 0)
            {
                if ((DateTime.Now - lastTargetSearchTime).TotalMilliseconds > TARGET_SEARCH_INTERVAL_MS)
                {
                    HandleCombatTrigger();
                    lastTargetSearchTime = DateTime.Now;
                }

                if (activeTargetId == 0 && _settings.PeaceModeWhenIdle)
                {
                    if (_coreManager.Actions.CombatMode != Decal.Adapter.Wrappers.CombatState.Peace)
                    {
                        if ((DateTime.Now - _lastPeaceAttempt).TotalMilliseconds > 2000)
                        {
                            _coreManager.Actions.SetCombatMode(Decal.Adapter.Wrappers.CombatState.Peace);
                            _lastPeaceAttempt = DateTime.Now;
                        }
                    }
                }
                return true;
            }

            var targetObj = _coreManager.WorldFilter[activeTargetId];
            if (targetObj == null) { activeTargetId = 0; return true; }

            // If still in Peace mode, force combat mode based on what's equipped
            if (_coreManager.Actions.CombatMode == Decal.Adapter.Wrappers.CombatState.Peace)
            {
                if ((DateTime.Now - _lastPeaceAttempt).TotalMilliseconds > 1500)
                {
                    _lastPeaceAttempt = DateTime.Now;
                    EquipWeaponAndSetStance(targetObj, "Auto");
                    
                    // If STILL in peace, scan equipped items and force the right stance
                    if (_coreManager.Actions.CombatMode == Decal.Adapter.Wrappers.CombatState.Peace)
                    {
                        foreach (var inv in _coreManager.WorldFilter.GetInventory())
                        {
                            if (inv.Values(LongValueKey.EquippedSlots, 0) <= 0) continue;
                            if (inv.ObjectClass == ObjectClass.WandStaffOrb)
                            { _coreManager.Actions.SetCombatMode(CombatState.Magic); break; }
                            else if (inv.ObjectClass == ObjectClass.MeleeWeapon)
                            { _coreManager.Actions.SetCombatMode(CombatState.Melee); break; }
                            else if (inv.ObjectClass == ObjectClass.MissileWeapon)
                            { _coreManager.Actions.SetCombatMode(CombatState.Missile); break; }
                        }
                    }
                }
                return true; // Yield this tick — let stance change process
            }

            if ((DateTime.Now - lastAttackCmd).TotalMilliseconds >= 1000)
            {
                // LOS gate: verify EVERY shot
                if (_settings.EnableRaycasting && RaycastInitialized && _raycastSystem != null)
                {
                    // Determine attack type: weapon-based for melee/missile, shape-based for magic
                    TargetingFSM.AttackType attackType = DetermineAttackTypeForLOS(targetObj);

                    RaycastCheckCount++;
                    if (_raycastSystem.IsTargetBlocked(_coreManager, targetObj, attackType))
                    {
                        RaycastBlockCount++;
                        try { _host?.Actions?.AddChatText($"[RynthAi] LOS blocked: {targetObj.Name} — dropping", 1); } catch { }
                        activeTargetId = 0;
                        return true;
                    }
                }

                FaceTarget(targetObj);
                _coreManager.Actions.SelectItem(activeTargetId);

                // MAGIC COMBAT: If we're in magic mode with a wand, cast spells
                if (_coreManager.Actions.CombatMode == CombatState.Magic && _spellManager != null)
                {
                    AttackWithMagic(targetObj);
                    
                    // If we were in magic mode for debuffs only, check if done → switch back
                    if (_returnToPhysicalCombat)
                    {
                        var rule2 = GetRuleForTarget(targetObj);
                        string elem2 = GetPreferredElement(targetObj, rule2);
                        if (rule2 != null && !HasPendingDebuffs(rule2, elem2))
                        {
                            // All debuffs confirmed — switch back to physical weapon
                            _returnToPhysicalCombat = false;
                            if (_savedWeaponId != 0)
                            {
                                _coreManager.Actions.UseItem(_savedWeaponId, 0);
                                _savedWeaponId = 0;
                            }
                            EquipWeaponAndSetStance(targetObj, "Auto");
                        }
                    }
                }
                else
                {
                    // Melee / Missile mode
                    var rule = GetRuleForTarget(targetObj);
                    
                    // Check if debuffs need casting before physical attack
                    if (rule != null && _spellManager != null && !_returnToPhysicalCombat)
                    {
                        string elem = GetPreferredElement(targetObj, rule);
                        if (HasPendingDebuffs(rule, elem))
                        {
                            // Find wand in ItemRules to cast debuffs
                            int wandId = FindWandInItems();
                            if (wandId != 0)
                            {
                                // Save current weapon, switch to wand for debuffs
                                var equipped = _coreManager.WorldFilter.GetInventory()
                                    .FirstOrDefault(w => w.Values(LongValueKey.EquippedSlots, 0) > 0 &&
                                        (w.ObjectClass == ObjectClass.MeleeWeapon || w.ObjectClass == ObjectClass.MissileWeapon));
                                _savedWeaponId = equipped?.Id ?? 0;
                                _returnToPhysicalCombat = true;
                                _coreManager.Actions.UseItem(wandId, 0);
                                _coreManager.Actions.SetCombatMode(CombatState.Magic);
                                lastAttackCmd = DateTime.Now;
                                return true; // Yield — next tick will be in magic mode
                            }
                        }
                    }
                    
                    bool anyAttackLight = (rule == null) || rule.UseArc || rule.UseBolt || rule.UseRing || rule.UseStreak;
                    if (!anyAttackLight)
                    {
                        // No attack shape selected — do not attack in melee/missile mode
                    }
                    else
                    {
                        AttackTarget();
                    }
                }
                TrackAttackAttempt(activeTargetId);
                lastAttackCmd = DateTime.Now;
            }
            return true;
        }

        public void OnHeartbeat()
        {
            if (!_settings.IsMacroRunning) return;

            // === YIELD to any higher-priority system that has claimed the state ===
            if (_settings.CurrentState != "Idle" && _settings.CurrentState != "Combat") return;

            if (_settings.EnableCombat)
            {
                Think();

                // If Think() found or maintained a target, we are in combat
                if (activeTargetId != 0)
                {
                    _settings.CurrentState = "Combat";
                    var target = _coreManager.WorldFilter[activeTargetId];
                    if (target != null) EquipWeaponAndSetStance(target, "Auto");
                }
                // If no target, leave state as-is (dispatcher resets to Idle each frame)
            }
        }

        public void HandleCombatTrigger()
        {
            double acDistanceLimit = (_settings.MonsterRange) / 240.0;

            // For target SEARCH, use weapon/mode-based attack type (no specific rule yet)
            TargetingFSM.AttackType attackType = TargetingFSM.AttackType.Linear;
            if (RaycastInitialized && _raycastSystem?.TargetingFSM != null)
                attackType = _raycastSystem.GetAttackType(_coreManager);

            int targetId = FindBestVisibleTarget(acDistanceLimit, attackType);
            if (targetId != 0 && targetId != activeTargetId)
            {
                activeTargetId = targetId;

                var target = _coreManager.WorldFilter[targetId];
                if (target != null) EquipWeaponAndSetStance(target, "Auto");
            }
        }

        private int FindBestVisibleTarget(double maxDist, TargetingFSM.AttackType attackType)
        {
            int foundId = 0;
            int playerId = _coreManager.CharacterFilter.Id;
            var candidates = new List<KeyValuePair<int, double>>();

            foreach (var wo in _coreManager.WorldFilter.GetLandscape())
            {
                if ((int)wo.ObjectClass != 5 || blacklistedTargets.ContainsKey(wo.Id) || _blacklistManager.IsBlacklisted(wo.Id)) continue;
                double dist = _coreManager.WorldFilter.Distance(playerId, wo.Id);
                if (dist <= maxDist) candidates.Add(new KeyValuePair<int, double>(wo.Id, dist));
            }

            candidates.Sort((a, b) => a.Value.CompareTo(b.Value));

            foreach (var candidate in candidates)
            {
                int id = candidate.Key;
                if (_settings.EnableRaycasting && RaycastInitialized)
                {
                    RaycastCheckCount++;
                    if (_raycastSystem.IsTargetBlocked(_coreManager, _coreManager.WorldFilter[id], attackType))
                    {
                        RaycastBlockCount++;
                        continue;
                    }
                }
                foundId = id;
                break;
            }
            return foundId;
        }

        private DateTime _lastEquipTime = DateTime.MinValue;
        private DateTime _lastStanceTime = DateTime.MinValue;

        private void EquipWeaponAndSetStance(WorldObject target, string monsterWeakness = "Auto")
        {
            if (target == null) return;

            int targetWeaponId = 0;

            // 1. Target Resolution
            var rule = _settings.MonsterRules.FirstOrDefault(r => target.Name.IndexOf(r.Name, StringComparison.OrdinalIgnoreCase) >= 0);

            if (rule == null)
            {
                rule = _settings.MonsterRules.FirstOrDefault(m => m.Name.Equals("Default", StringComparison.OrdinalIgnoreCase));
            }

            string desired = (rule != null && rule.DamageType != "Auto") ? rule.DamageType : monsterWeakness;

            if (rule != null && rule.WeaponId != 0)
            {
                targetWeaponId = rule.WeaponId;
            }
            else
            {
                var bestWeapon = _settings.ItemRules.FirstOrDefault(i => i.Element.Equals(desired, StringComparison.OrdinalIgnoreCase))
                                 ?? _settings.ItemRules.FirstOrDefault();
                if (bestWeapon != null) targetWeaponId = bestWeapon.Id;
            }

            if (targetWeaponId == 0) return;

            var weaponObj = _coreManager.WorldFilter[targetWeaponId];
            if (weaponObj == null) return;

            // 2. Equipment Enforcement
            bool isEquipped = weaponObj.Values(LongValueKey.EquippedSlots, 0) > 0;

            if (!isEquipped)
            {
                // Reverted to 1000ms (1 second) to prevent bare-hand attacks during slow animations
                if ((DateTime.Now - _lastEquipTime).TotalMilliseconds > 1000)
                {
                    _coreManager.Actions.UseItem(targetWeaponId, 0);
                    _lastEquipTime = DateTime.Now;
                    _lastStanceTime = DateTime.Now; // Reset stance timer to sync with new equip
                }
                return;
            }

            // 3. Stance Enforcement
            // Increased to 1000ms to allow the server to finish the "Draw Weapon" animation
            if ((DateTime.Now - _lastStanceTime).TotalMilliseconds > 1000)
            {
                bool changedStance = false;

                if (weaponObj.ObjectClass == ObjectClass.MeleeWeapon && _coreManager.Actions.CombatMode != CombatState.Melee)
                {
                    _coreManager.Actions.SetCombatMode(CombatState.Melee);
                    changedStance = true;
                }
                else if (weaponObj.ObjectClass == ObjectClass.MissileWeapon && _coreManager.Actions.CombatMode != CombatState.Missile)
                {
                    _coreManager.Actions.SetCombatMode(CombatState.Missile);
                    changedStance = true;
                }
                else if (weaponObj.ObjectClass == ObjectClass.WandStaffOrb && _coreManager.Actions.CombatMode != CombatState.Magic)
                {
                    _coreManager.Actions.SetCombatMode(CombatState.Magic);
                    changedStance = true;
                }

                if (changedStance) _lastStanceTime = DateTime.Now;
            }
        }

        public string GetRaycastStatus()
        {
            if (_raycastSystem == null) return "Raycasting: NOT INITIALIZED";
            string status = _settings.EnableRaycasting ? "ACTIVE" : "DISABLED";
            return "Raycasting: " + status + "\n  Status: " + _raycastSystem.StatusMessage + "\n" +
                   "  Checks: " + RaycastCheckCount + ", Blocks: " + RaycastBlockCount;
        }

        public List<string> GetRaycastDiagLog()
        {
            var lines = new List<string>();
            if (_raycastSystem?.GeometryLoader?.DiagLog != null)
                foreach (var line in _raycastSystem.GeometryLoader.DiagLog) lines.Add(line);
            return lines;
        }

        /// <summary>
        /// Determines the correct LOS check type based on combat mode and spell shape.
        /// - Magic + Arc spell shape → MagicArc (same trajectory as missiles)
        /// - Magic + Bolt/Streak/Ring → Linear (straight line)
        /// - Missile mode → delegates to FSM (BowArc, ThrownArc, or Linear for crossbow)
        /// - Melee/Peace → Linear
        /// </summary>
        private TargetingFSM.AttackType DetermineAttackTypeForLOS(WorldObject target)
        {
            // Magic mode: check spell shape from monster rule
            if (_coreManager.Actions.CombatMode == CombatState.Magic)
            {
                var rule = GetRuleForTarget(target);
                if (rule != null && rule.UseArc)
                    return TargetingFSM.AttackType.MagicArc;
                
                // Bolts, Streaks, Rings = straight line
                return TargetingFSM.AttackType.Linear;
            }

            // Missile/Melee: use weapon-based detection
            if (_raycastSystem?.TargetingFSM != null)
                return _raycastSystem.GetAttackType(_coreManager);

            return TargetingFSM.AttackType.Linear;
        }

        private void FaceTarget(WorldObject target)
        {
            try
            {
                var p = _coreManager.WorldFilter[_coreManager.CharacterFilter.Id];
                double dx = target.Coordinates().EastWest - p.Coordinates().EastWest;
                double dy = target.Coordinates().NorthSouth - p.Coordinates().NorthSouth;
                double heading = Math.Atan2(dx, dy) * (180.0 / Math.PI);
                if (heading < 0) heading += 360.0;
                _coreManager.Actions.Heading = (float)heading;
            }
            catch { }
        }

        private void AttackTarget()
        {
            try
            {
                if (!CombatActionHelper.IsInitialized) return;

                bool isMissile = _coreManager.Actions.CombatMode == CombatState.Missile;
                uint targetId = (uint)activeTargetId;

                // Resolve attack power (0.0–1.0)
                int powerPct = isMissile ? _settings.MissileAttackPower : _settings.MeleeAttackPower;
                float power;
                if (powerPct < 0)
                {
                    // Auto mode: 100% unless Recklessness is enabled and trained
                    power = 1.0f;
                    if (_settings.UseRecklessness)
                    {
                        try
                        {
                            int reckTraining = (int)_coreManager.CharacterFilter
                                .Skills[CharFilterSkillType.Recklessness].Training;
                            if (reckTraining >= 2) // Trained or Specialized
                                power = 0.8f;
                        }
                        catch { }
                    }
                }
                else
                {
                    power = powerPct / 100f;
                }

                // Map UI height (0=Low,1=Med,2=High) → AC ATTACK_HEIGHT enum
                int uiHeight = isMissile ? _settings.MissileAttackHeight : _settings.MeleeAttackHeight;
                int acHeight = CombatActionHelper.MapAttackHeight(uiHeight);

                // Send attack directly via acclient.exe CM_Combat functions
                if (isMissile)
                    CombatActionHelper.MissileAttack(targetId, acHeight, power);
                else
                    CombatActionHelper.MeleeAttack(targetId, acHeight, power);
            }
            catch { }
        }

        // ══════════════════════════════════════════════════════════════════════════
        //  MAGIC COMBAT SYSTEM
        //  Supports: debuff lights (F/B/G/I/Y/V), spell shapes (A/R/S/B=default Bolt),
        //  monsters.json weakness lookup, War + Void magic
        //  If none of A/R/S/B are set → no attack (buff bot mode)
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Resolves the MonsterRule for a target (specific rule first, then Default).
        /// Cached per-target so we don't re-scan every frame.
        /// </summary>
        private MonsterRule GetRuleForTarget(WorldObject target)
        {
            if (target == null) return null;
            // Specific name match first
            var rule = _settings.MonsterRules.FirstOrDefault(
                r => !r.Name.Equals("Default", StringComparison.OrdinalIgnoreCase) &&
                     target.Name.IndexOf(r.Name, StringComparison.OrdinalIgnoreCase) >= 0);
            if (rule != null) return rule;
            // Fall back to Default
            return _settings.MonsterRules.FirstOrDefault(
                m => m.Name.Equals("Default", StringComparison.OrdinalIgnoreCase));
        }

        private void AttackWithMagic(WorldObject target)
        {
            if (_spellManager == null || target == null) return;
            if ((DateTime.Now - _lastSpellCast).TotalMilliseconds < SPELL_CAST_COOLDOWN_MS) return;

            // If waiting for debuff result, check timeout only
            if (_waitingForDebuffResult)
            {
                if ((DateTime.Now - _pendingDebuffCastTime).TotalMilliseconds > DEBUFF_RESULT_TIMEOUT_MS)
                {
                    // Timed out — assume success, add to confirmed
                    _confirmedDebuffs.Add($"{_pendingDebuffTargetId}_{_pendingDebuffKey}");
                    _waitingForDebuffResult = false;
                }
                else
                {
                    return; // Still waiting for chat — don't cast anything
                }
            }

            // Clear confirmed debuffs if target changed
            if (activeTargetId != _lastDebuffTargetId)
            {
                _confirmedDebuffs.Clear();
                _lastDebuffTargetId = activeTargetId;
            }

            var rule = GetRuleForTarget(target);
            string element = GetPreferredElement(target, rule);

            // ── Phase 1: Debuffs — spam until chat confirms each one ──
            if (rule != null)
            {
                var pendingDebuffs = BuildDebuffList(rule, element);
                
                foreach (var debuffKey in pendingDebuffs)
                {
                    string key = $"{activeTargetId}_{debuffKey}";
                    
                    if (_confirmedDebuffs.Contains(key))
                        continue; // Chat confirmed this one — move on
                    
                    int spellId = 0;
                    int castTier = 0;
                    if (debuffKey.StartsWith("Vuln:"))
                        spellId = FindBestVulnSpellWithTier(debuffKey.Substring(5), out castTier);
                    else
                        spellId = FindBestDebuffSpellWithTier(debuffKey, out castTier);

                    if (spellId == 0) continue;

                    try
                    {
                        _coreManager.Actions.CastSpell(spellId, activeTargetId);
                        _lastSpellCast = DateTime.Now;
                        _pendingDebuffKey = debuffKey;
                        _pendingDebuffTargetId = activeTargetId;
                        _pendingDebuffTier = castTier;
                        _waitingForDebuffResult = true;
                        _pendingDebuffCastTime = DateTime.Now;
                        try { _host?.Actions?.AddChatText($"[RynthAi] Casting: {debuffKey} (T{castTier}) on {target.Name}", 5); } catch { }
                    }
                    catch { }
                    return; // Wait for chat result
                }
            }

            // ── Phase 2: War/Void spell spam — faster cooldown ──
            if ((DateTime.Now - _lastSpellCast).TotalMilliseconds < ATTACK_SPELL_COOLDOWN_MS) return;

            if (rule != null && !rule.UseArc && !rule.UseRing && !rule.UseStreak && !rule.UseBolt)
                return;

            int offensiveSpellId = FindBestShapedSpell(element, rule);
            if (offensiveSpellId != 0)
            {
                try
                {
                    _coreManager.Actions.CastSpell(offensiveSpellId, activeTargetId);
                    _lastSpellCast = DateTime.Now;
                }
                catch { }
            }
            else
            {
                AttackTarget();
            }
        }

        private string GetPreferredElement(WorldObject target, MonsterRule rule)
        {
            // 1. Explicit element from rule
            if (rule != null && !string.IsNullOrEmpty(rule.DamageType) &&
                !rule.DamageType.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            {
                return rule.DamageType;
            }

            // 2. Auto: check monsters.json weakness database
            if (target != null && _monsterWeaknesses != null && _monsterWeaknesses.Count > 0)
            {
                foreach (var entry in _monsterWeaknesses)
                {
                    if (target.Name.IndexOf(entry.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return entry.Value;
                    }
                }
            }

            // 3. Ultimate fallback
            return "Fire";
        }

        // ── DEBUFF SYSTEM ────────────────────────────────────────────────────────

        /// <summary>
        /// Returns true if any debuff in the rule's list still needs casting on the active target.
        /// </summary>
        private bool HasPendingDebuffs(MonsterRule rule, string element)
        {
            if (rule == null) return false;
            var debuffs = BuildDebuffList(rule, element);
            foreach (var debuffKey in debuffs)
            {
                string key = $"{activeTargetId}_{debuffKey}";
                if (!_confirmedDebuffs.Contains(key))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Finds the first wand/staff/orb in ItemRules for debuff casting.
        /// </summary>
        private int FindWandInItems()
        {
            foreach (var item in _settings.ItemRules)
            {
                var wo = _coreManager.WorldFilter[item.Id];
                if (wo != null && wo.ObjectClass == ObjectClass.WandStaffOrb)
                    return item.Id;
            }
            return 0;
        }

        /// <summary>
        /// Builds the ordered debuff list from a MonsterRule's lights.
        /// </summary>
        private List<string> BuildDebuffList(MonsterRule rule, string element)
        {
            var list = new List<string>();
            if (rule.Imperil)    list.Add("Imperil");
            if (rule.Vuln)       list.Add("Vuln:" + element);
            if (!string.IsNullOrEmpty(rule.ExVuln) && !rule.ExVuln.Equals("None", StringComparison.OrdinalIgnoreCase))
                list.Add("Vuln:" + rule.ExVuln);
            if (rule.Fester)     list.Add("Fester");
            if (rule.Yield)      list.Add("Yield");
            if (rule.Broadside)  list.Add("Broadside");
            if (rule.GravityWell) list.Add("Gravity");
            return list;
        }

        // ── DEBUFF SPELL RESOLUTION (with tier output for duration tracking) ──

        private int FindBestDebuffSpellWithTier(string debuffType, out int tier)
        {
            tier = 0;
            if (_spellManager == null) return 0;

            string[] spellInfo;
            if (!DebuffSpells.TryGetValue(debuffType, out spellInfo) || spellInfo.Length < 2)
                return 0;

            string tieredBase = spellInfo[0]; // e.g., "Imperil Other"
            string loreName = spellInfo[1];   // e.g., "Gossamer Flesh"

            CharFilterSkillType skill = CharFilterSkillType.CreatureEnchantment;
            int maxTier = _spellManager.GetHighestSpellTier(skill);

            // 1. Tier 8: Incantation of [tieredBase]
            if (maxTier >= 8)
            {
                int id = TrySpellByName($"Incantation of {tieredBase}");
                if (id != 0) { tier = 8; return id; }
            }

            // 2. Tier 7: Lore name (direct lookup)
            if (maxTier >= 7 && !string.IsNullOrEmpty(loreName))
            {
                int id = TrySpellByName(loreName);
                if (id != 0) { tier = 7; return id; }
            }

            // 3. Tier 7 numbered fallback, then VI → I
            for (int t = Math.Min(maxTier, 7); t >= 1; t--)
            {
                string numeral = GetRomanNumeral(t);
                int id = TrySpellByName($"{tieredBase} {numeral}");
                if (id != 0) { tier = t; return id; }
            }

            return 0;
        }

        private int FindBestVulnSpellWithTier(string element, out int tier)
        {
            tier = 0;
            if (_spellManager == null) return 0;

            string[] vulnBases;
            if (!VulnSpells.TryGetValue(element, out vulnBases))
                return 0;

            CharFilterSkillType skill = CharFilterSkillType.CreatureEnchantment;
            int maxTier = _spellManager.GetHighestSpellTier(skill);

            foreach (string baseName in vulnBases)
            {
                if (maxTier >= 8)
                {
                    int id = TrySpellByName($"Incantation of {baseName}");
                    if (id != 0) { tier = 8; return id; }
                }

                for (int t = Math.Min(maxTier, 7); t >= 1; t--)
                {
                    int id = TrySpellByName($"{baseName} {GetRomanNumeral(t)}");
                    if (id != 0) { tier = t; return id; }
                }
            }
            return 0;
        }

        /// <summary>
        /// Tries to find a spell by exact name. Returns spell ID if known, 0 if not.
        /// </summary>
        private int TrySpellByName(string name)
        {
            int id;
            if (_spellManager.SpellDictionary.TryGetValue(name, out id))
            {
                if (_coreManager.CharacterFilter.IsSpellKnown(id))
                    return id;
            }
            return 0;
        }

        // ── SPELL SHAPE SELECTION (A/R/S/B lights) ──────────────────────────

        /// <summary>
        /// Counts monsters within the given AC distance (yards/240).
        /// </summary>
        private int CountMonstersInRange(double rangeYards)
        {
            double acDist = rangeYards / 240.0;
            int count = 0;
            int playerId = _coreManager.CharacterFilter.Id;
            foreach (var wo in _coreManager.WorldFilter.GetLandscape())
            {
                if ((int)wo.ObjectClass != 5) continue;
                if (_coreManager.WorldFilter.Distance(playerId, wo.Id) <= acDist)
                    count++;
            }
            return count;
        }

        /// <summary>
        /// Finds the best offensive spell using the rule's shape lights.
        /// A = Arc, R = Ring, S = Streak, B = Bolt.
        /// RING OVERRIDE: If >= MinRingTargets monsters are within RingRange, forces Ring spells.
        /// If none are set, returns 0 (no attack - buff bot mode).
        /// Uses Void Magic for Nether element, War Magic for everything else.
        /// </summary>
        private int FindBestShapedSpell(string element, MonsterRule rule)
        {
            if (_spellManager == null) return 0;

            // Determine skill: Void or War
            bool useVoid = element.Equals("Nether", StringComparison.OrdinalIgnoreCase);
            // Also check if character has Void trained but not War (or vice versa)
            bool warTrained = false, voidTrained = false;
            try { warTrained = (int)_coreManager.CharacterFilter.Skills[CharFilterSkillType.WarMagic].Training >= 2; } catch { }
            try { voidTrained = (int)_coreManager.CharacterFilter.Skills[CharFilterSkillType.VoidMagic].Training >= 2; } catch { }

            if (useVoid && !voidTrained && warTrained) useVoid = false; // fallback to war
            if (!useVoid && !warTrained && voidTrained) useVoid = true; // fallback to void

            CharFilterSkillType skill = useVoid ? CharFilterSkillType.VoidMagic : CharFilterSkillType.WarMagic;

            // Determine shape index: 0=Arc, 1=Ring, 2=Streak, 3=Bolt
            // Priority: A > R > S > B. If none set, caller already blocked attack.
            int shapeIdx = 3; // default = Bolt
            if (rule != null)
            {
                if (rule.UseArc) shapeIdx = 0;
                else if (rule.UseRing) shapeIdx = 1;
                else if (rule.UseStreak) shapeIdx = 2;
                else if (rule.UseBolt) shapeIdx = 3;
                else return 0; // No attack shape selected
            }

            // RING OVERRIDE: only if R light is on for this rule, and enough monsters nearby
            if (rule != null && rule.UseRing && _settings.MinRingTargets > 0 && _settings.RingRange > 0)
            {
                int nearbyCount = CountMonstersInRange(_settings.RingRange);
                if (nearbyCount >= _settings.MinRingTargets)
                    shapeIdx = 1; // Force Ring
            }

            // Look up shape patterns
            var shapes = useVoid ? VoidSpellShapes : SpellShapes;
            string[] elementShapes;
            if (!shapes.TryGetValue(element, out elementShapes))
            {
                // Fallback element
                if (!SpellShapes.TryGetValue("Fire", out elementShapes))
                    return 0;
                skill = CharFilterSkillType.WarMagic;
            }

            // Clamp index
            if (shapeIdx >= elementShapes.Length) shapeIdx = elementShapes.Length - 1;

            // Try preferred shape first
            // SPECIAL: Ring spells (shapeIdx==1) have unique lore names that don't follow "Base VII" pattern
            if (shapeIdx == 1)
            {
                int ringId = FindBestRingSpell(element, skill);
                if (ringId != 0) return ringId;
            }

            // Standard resolution: try preferred shape, then fall back to others
            int id = FindBestOffensiveSpellId(elementShapes[shapeIdx], skill);
            if (id != 0) return id;

            // Fallback: try bolt (index 3), then any shape that works
            for (int i = elementShapes.Length - 1; i >= 0; i--)
            {
                if (i == shapeIdx) continue;
                id = FindBestOffensiveSpellId(elementShapes[i], skill);
                if (id != 0) return id;
            }

            return 0;
        }

        // ── SPELL RESOLUTION ─────────────────────────────────────────────────────

        /// <summary>
        /// Resolves ring spells using AC's unique lore names.
        /// Order: Incantation of [lore] (T8) → [lore] II (T7) → [lore] (T6) → generic tiers V→I
        /// </summary>
        private int FindBestRingSpell(string element, CharFilterSkillType skill)
        {
            string[] loreNames;
            if (!RingLoreNames.TryGetValue(element, out loreNames) || loreNames.Length < 2)
                return 0;

            int maxTier = _spellManager.GetHighestSpellTier(skill);

            // T7: [lore name] II (try first — most likely to be known)
            if (maxTier >= 7)
            {
                int id = TrySpellByName(loreNames[1]);
                if (id != 0) return id;
                // Try with smart apostrophe variants
                id = TrySpellByName(loreNames[1].Replace("'", "\u2019"));
                if (id != 0) return id;
                id = TrySpellByName(loreNames[1].Replace("'", "`"));
                if (id != 0) return id;
            }

            // T6: [lore name]
            if (maxTier >= 6)
            {
                int id = TrySpellByName(loreNames[0]);
                if (id != 0) return id;
                id = TrySpellByName(loreNames[0].Replace("'", "\u2019"));
                if (id != 0) return id;
                id = TrySpellByName(loreNames[0].Replace("'", "`"));
                if (id != 0) return id;
            }

            // T8: Incantation of [lore name]
            if (maxTier >= 8)
            {
                int id = TrySpellByName($"Incantation of {loreNames[0]}");
                if (id != 0) return id;
            }

            // Fallback: try generic ring names (e.g., "Ring of Fire V")
            string[] genericRingBases;
            if (SpellShapes.TryGetValue(element, out genericRingBases) && genericRingBases.Length > 1)
            {
                string ringBase = genericRingBases[1]; // Index 1 = Ring
                for (int t = Math.Min(maxTier, 5); t >= 1; t--)
                {
                    int id = TrySpellByName($"{ringBase} {GetRomanNumeral(t)}");
                    if (id != 0) return id;
                }
            }

            // Debug: if we get here, log what we tried
            try { _host?.Actions?.AddChatText($"[RynthAi] Ring spell not found for {element} (tried: {loreNames[1]}, {loreNames[0]})", 2); } catch { }
            return 0;
        }

        /// <summary>
        /// Searches for the highest tier of a spell the character knows.
        /// Order: Incantation (8) → VII → VI → V → IV → III → II → I
        /// Respects skill level — won't try tiers the character can't cast.
        /// </summary>
        private int FindBestOffensiveSpellId(string baseName, CharFilterSkillType skill)
        {
            if (_spellManager == null) return 0;

            int maxTier = _spellManager.GetHighestSpellTier(skill);

            // Tier 8: Incantation
            if (maxTier >= 8)
            {
                int id = TrySpellByName($"Incantation of {baseName}");
                if (id != 0) return id;
                id = TrySpellByName(baseName + " VIII");
                if (id != 0) return id;
            }

            // Tiers 7 → 1
            for (int tier = Math.Min(maxTier, 7); tier >= 1; tier--)
            {
                string numeral = GetRomanNumeral(tier);
                int id = TrySpellByName($"{baseName} {numeral}");
                if (id != 0) return id;
            }

            return 0;
        }

        private static string GetRomanNumeral(int tier)
        {
            switch (tier)
            {
                case 1: return "I";
                case 2: return "II";
                case 3: return "III";
                case 4: return "IV";
                case 5: return "V";
                case 6: return "VI";
                case 7: return "VII";
                case 8: return "VIII";
                default: return "I";
            }
        }

        private void CleanupExpiredBlacklist()
        {
            var expired = new List<int>();
            foreach (var kvp in blacklistedTargets) if (kvp.Value.IsExpired()) expired.Add(kvp.Key);
            foreach (var id in expired) blacklistedTargets.Remove(id);
            // _confirmedDebuffs is cleared automatically when target changes
        }



        public void Dispose() { _raycastSystem?.Dispose(); }

        /// <summary>
        /// Diagnostic: runs a full LOS test against the currently selected target
        /// and reports all coordinates, geometry volumes, and intersection results.
        /// </summary>
        public List<string> RunLosTestDiag(CoreManager core)
        {
            var lines = new List<string>();
            try
            {
                if (_raycastSystem == null || !RaycastInitialized)
                {
                    lines.Add("Raycast system not initialized");
                    return lines;
                }

                int targetId = core.Actions.CurrentSelection;
                if (targetId == 0)
                {
                    lines.Add("No target selected — select a monster first");
                    return lines;
                }

                var target = core.WorldFilter[targetId];
                if (target == null)
                {
                    lines.Add($"Target {targetId} not found in WorldFilter");
                    return lines;
                }

                lines.Add($"Target: {target.Name} (id={targetId})");

                // Player position
                uint landcell = (uint)core.Actions.Landcell;
                uint blockX = (landcell >> 24) & 0xFF;
                uint blockY = (landcell >> 16) & 0xFF;
                float locX = (float)core.Actions.LocationX;
                float locY = (float)core.Actions.LocationY;
                float locZ = (float)core.Actions.LocationZ;
                float playerGX = blockX * 192.0f + locX;
                float playerGY = blockY * 192.0f + locY;
                lines.Add($"Player Landcell=0x{landcell:X8} Block=({blockX},{blockY})");
                lines.Add($"Player Local=({locX:F1},{locY:F1},{locZ:F1})");
                lines.Add($"Player Global=({playerGX:F1},{playerGY:F1},{locZ:F1})");

                // Target position
                var coords = target.Coordinates();
                double ew = coords.EastWest;
                double ns = coords.NorthSouth;
                float targetGX = (float)((ew * 10.0 + 1019.5) * 24.0);
                float targetGY = (float)((ns * 10.0 + 1019.5) * 24.0);
                float targetZ = locZ; // approximate
                try { targetZ = (float)target.RawCoordinates().Z; } catch { }
                lines.Add($"Target EW={ew:F3} NS={ns:F3}");
                lines.Add($"Target Global=({targetGX:F1},{targetGY:F1},{targetZ:F1})");

                float dx = targetGX - playerGX;
                float dy = targetGY - playerGY;
                float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                lines.Add($"Distance: {dist:F1}m, Delta=({dx:F1},{dy:F1})");

                // Load geometry
                var geometry = _raycastSystem.GeometryLoader.GetLandblockGeometry(landcell);
                lines.Add($"Geometry: {geometry?.Count ?? 0} volumes loaded");

                if (geometry != null && geometry.Count > 0)
                {
                    var origin = new Raycasting.Vector3(playerGX, playerGY, locZ + 1.0f);
                    var targetPos = new Raycasting.Vector3(targetGX, targetGY, targetZ + 1.0f);

                    var rayDir = targetPos - origin;
                    float rayLen = rayDir.Length();
                    if (rayLen > 0.001f) rayDir = rayDir / rayLen;

                    float pathMinX = Math.Min(playerGX, targetGX) - 15f;
                    float pathMaxX = Math.Max(playerGX, targetGX) + 15f;
                    float pathMinY = Math.Min(playerGY, targetGY) - 15f;
                    float pathMaxY = Math.Max(playerGY, targetGY) + 15f;

                    int nearCount = 0;
                    int hitCount = 0;
                    var hitVolumes = new List<string>();
                    var nearVolumes = new List<string>();

                    foreach (var vol in geometry)
                    {
                        bool nearPath = vol.Center.X >= pathMinX && vol.Center.X <= pathMaxX &&
                                        vol.Center.Y >= pathMinY && vol.Center.Y <= pathMaxY;

                        float volDist;
                        bool hit = vol.RayIntersect(origin, rayDir, rayLen, out volDist);

                        if (hit) hitCount++;
                        if (nearPath) nearCount++;

                        // ALWAYS capture hitting volumes
                        if (hit && hitVolumes.Count < 10)
                        {
                            hitVolumes.Add($"  ** HIT at {volDist:F2}yd: center=({vol.Center.X:F1},{vol.Center.Y:F1},{vol.Center.Z:F1}) " +
                                          $"dim=({vol.Dimensions.X:F1},{vol.Dimensions.Y:F1},{vol.Dimensions.Z:F1}) type={vol.Type}");
                        }

                        if (nearPath && !hit && nearVolumes.Count < 10)
                        {
                            nearVolumes.Add($"  NEAR: center=({vol.Center.X:F0},{vol.Center.Y:F0},{vol.Center.Z:F0}) " +
                                          $"r={vol.Dimensions.X:F1} type={vol.Type}");
                        }
                    }

                    // Show hits first
                    if (hitVolumes.Count > 0)
                    {
                        lines.Add("=== BLOCKING VOLUMES ===");
                        foreach (var hv in hitVolumes) lines.Add(hv);
                    }

                    if (nearVolumes.Count > 0)
                    {
                        lines.Add($"=== NEAR (non-hit, showing {nearVolumes.Count} of {nearCount}) ===");
                        foreach (var nv in nearVolumes) lines.Add(nv);
                    }

                    if (hitVolumes.Count == 0 && nearVolumes.Count == 0)
                        lines.Add("  NO volumes found near path!");

                    lines.Add($"Summary: {geometry.Count} total, {nearCount} near path, {hitCount} ray hits");

                    bool nearby = Raycasting.RaycastEngine.HasNearbyGeometry(origin, targetPos, geometry);
                    bool blocked = Raycasting.RaycastEngine.IsLinearPathBlocked(origin, targetPos, geometry);
                    lines.Add($"HasNearbyGeometry: {nearby}");
                    lines.Add($"IsLinearPathBlocked: {blocked}");
                }
            }
            catch (Exception ex)
            {
                lines.Add($"Error: {ex.Message}");
            }
            return lines;
        }
    }
}