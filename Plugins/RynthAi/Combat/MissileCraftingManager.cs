using System;
using System.Collections.Generic;
using System.Linq;
using Decal.Adapter;
using Decal.Adapter.Wrappers;
using Decal.Filters;

namespace NexSuite.Plugins.RynthAi
{
    /// <summary>
    /// Automates missile ammunition management.
    ///
    /// Triggers when ammo slot is empty:
    ///   1. Can craft better ammo than loose inventory? → Craft (up to 2x), then equip
    ///   2. Loose ammo available? → Equip best one
    ///   3. Can craft anything? → Craft, then equip
    ///
    /// Crafting is chat-driven: issues ApplyItem, waits for "You make" chat message,
    /// then advances to next combine or equips. Retries every 2s if no response.
    /// </summary>
    public class MissileCraftingManager
    {
        private CoreManager _core;
        private UISettings _settings;
        private PluginHost _host;

        public enum CraftState
        {
            Idle,
            Evaluating,
            Combining,       // ApplyItem issued, waiting for "You make" in chat
            EquippingAmmo
        }

        public CraftState State { get; private set; } = CraftState.Idle;
        public bool IsCrafting => State != CraftState.Idle;
        public string StatusMessage { get; private set; } = "";

        // ── Timing ─────────────────────────────────────────────────────────
        private DateTime _phaseStart = DateTime.MinValue;
        private DateTime _lastAmmoCheck = DateTime.MinValue;
        private DateTime _lastApplyAttempt = DateTime.MinValue;
        private const int AMMO_CHECK_INTERVAL_MS = 5000;
        private const int APPLY_RETRY_MS = 2000;
        private const int CRAFT_TIMEOUT_MS = 15000;
        private const int EQUIP_DELAY_MS = 500;

        // ── Current craft job ──────────────────────────────────────────────
        private int _headBundleId = 0;
        private int _shaftBundleId = 0;
        private AmmoRecipe _currentRecipe = null;
        private int _equipTargetId = 0;
        private bool _allCombinesDone = false;

        // Pre-identified bundle pairs (up to 2). Populated before first combine.
        private Queue<int[]> _pendingBundlePairs = new Queue<int[]>();
        private int _totalCombines = 0;
        private int _combinesCompleted = 0;

        // ══════════════════════════════════════════════════════════════════
        //  RECIPE DATABASE
        // ══════════════════════════════════════════════════════════════════

        public enum WeaponCategory { Bow, Crossbow, Atlatl }

        public class AmmoRecipe
        {
            public string HeadBundleName;
            public string ShaftBundleName;
            public string OutputName;
            public WeaponCategory Category;
            public bool RequiresSpecialized;
            public int Priority;
        }

        public static readonly List<AmmoRecipe> AllRecipes = new List<AmmoRecipe>
        {
            // ── ARROWS (Bow) ───────────────────────────────────────────
            new AmmoRecipe { HeadBundleName = "Wrapped Bundle of Lethal Prismatic Arrowheads", ShaftBundleName = "Wrapped Bundle of Arrowshafts", OutputName = "Lethal Prismatic Arrow", Category = WeaponCategory.Bow, RequiresSpecialized = true, Priority = 40 },
            new AmmoRecipe { HeadBundleName = "Wrapped Bundle of Deadly Prismatic Arrowheads", ShaftBundleName = "Wrapped Bundle of Arrowshafts", OutputName = "Deadly Prismatic Arrow", Category = WeaponCategory.Bow, RequiresSpecialized = true, Priority = 30 },
            new AmmoRecipe { HeadBundleName = "Wrapped Bundle of Greater Prismatic Arrowheads", ShaftBundleName = "Wrapped Bundle of Arrowshafts", OutputName = "Greater Prismatic Arrow", Category = WeaponCategory.Bow, RequiresSpecialized = false, Priority = 20 },
            new AmmoRecipe { HeadBundleName = "Wrapped Bundle of Prismatic Arrowheads", ShaftBundleName = "Wrapped Bundle of Arrowshafts", OutputName = "Prismatic Arrow", Category = WeaponCategory.Bow, RequiresSpecialized = false, Priority = 10 },
            new AmmoRecipe { HeadBundleName = "Wrapped Bundle of Armor Piercing Arrowheads", ShaftBundleName = "Wrapped Bundle of Arrowshafts", OutputName = "Armor Piercing Arrow", Category = WeaponCategory.Bow, RequiresSpecialized = false, Priority = 6 },
            new AmmoRecipe { HeadBundleName = "Wrapped Bundle of Broad Arrowheads", ShaftBundleName = "Wrapped Bundle of Arrowshafts", OutputName = "Broad Head Arrow", Category = WeaponCategory.Bow, RequiresSpecialized = false, Priority = 5 },
            new AmmoRecipe { HeadBundleName = "Wrapped Bundle of Blunt Arrowheads", ShaftBundleName = "Wrapped Bundle of Arrowshafts", OutputName = "Blunt Arrow", Category = WeaponCategory.Bow, RequiresSpecialized = false, Priority = 4 },
            new AmmoRecipe { HeadBundleName = "Wrapped Bundle of Frog Crotch Arrowheads", ShaftBundleName = "Wrapped Bundle of Arrowshafts", OutputName = "Frog Crotch Arrow", Category = WeaponCategory.Bow, RequiresSpecialized = false, Priority = 3 },
            new AmmoRecipe { HeadBundleName = "Wrapped Bundle of Arrowheads", ShaftBundleName = "Wrapped Bundle of Arrowshafts", OutputName = "Arrow", Category = WeaponCategory.Bow, RequiresSpecialized = false, Priority = 1 },

            // ── QUARRELS (Crossbow) ────────────────────────────────────
            new AmmoRecipe { HeadBundleName = "Wrapped Bundle of Lethal Prismatic Quarrelheads", ShaftBundleName = "Wrapped Bundle of Quarrelshafts", OutputName = "Lethal Prismatic Quarrel", Category = WeaponCategory.Crossbow, RequiresSpecialized = true, Priority = 40 },
            new AmmoRecipe { HeadBundleName = "Wrapped Bundle of Deadly Prismatic Quarrelheads", ShaftBundleName = "Wrapped Bundle of Quarrelshafts", OutputName = "Deadly Prismatic Quarrel", Category = WeaponCategory.Crossbow, RequiresSpecialized = true, Priority = 30 },
            new AmmoRecipe { HeadBundleName = "Wrapped Bundle of Greater Prismatic Quarrelheads", ShaftBundleName = "Wrapped Bundle of Quarrelshafts", OutputName = "Greater Prismatic Quarrel", Category = WeaponCategory.Crossbow, RequiresSpecialized = false, Priority = 20 },
            new AmmoRecipe { HeadBundleName = "Wrapped Bundle of Prismatic Quarrelheads", ShaftBundleName = "Wrapped Bundle of Quarrelshafts", OutputName = "Prismatic Quarrel", Category = WeaponCategory.Crossbow, RequiresSpecialized = false, Priority = 10 },
            new AmmoRecipe { HeadBundleName = "Wrapped Bundle of Armor Piercing Quarrelheads", ShaftBundleName = "Wrapped Bundle of Quarrelshafts", OutputName = "Armor Piercing Quarrel", Category = WeaponCategory.Crossbow, RequiresSpecialized = false, Priority = 6 },
            new AmmoRecipe { HeadBundleName = "Wrapped Bundle of Broad Quarrelheads", ShaftBundleName = "Wrapped Bundle of Quarrelshafts", OutputName = "Broad Head Quarrel", Category = WeaponCategory.Crossbow, RequiresSpecialized = false, Priority = 5 },
            new AmmoRecipe { HeadBundleName = "Wrapped Bundle of Blunt Quarrelheads", ShaftBundleName = "Wrapped Bundle of Quarrelshafts", OutputName = "Blunt Quarrel", Category = WeaponCategory.Crossbow, RequiresSpecialized = false, Priority = 4 },
            new AmmoRecipe { HeadBundleName = "Wrapped Bundle of Quarrelheads", ShaftBundleName = "Wrapped Bundle of Quarrelshafts", OutputName = "Quarrel", Category = WeaponCategory.Crossbow, RequiresSpecialized = false, Priority = 1 },

            // ── DARTS (Atlatl) ─────────────────────────────────────────
            new AmmoRecipe { HeadBundleName = "Wrapped Bundle of Lethal Prismatic Atlatl Dart Heads", ShaftBundleName = "Wrapped Bundle of Atlatl Dart Shafts", OutputName = "Lethal Prismatic Atlatl Dart", Category = WeaponCategory.Atlatl, RequiresSpecialized = true, Priority = 40 },
            new AmmoRecipe { HeadBundleName = "Wrapped Bundle of Deadly Prismatic Atlatl Dart Heads", ShaftBundleName = "Wrapped Bundle of Atlatl Dart Shafts", OutputName = "Deadly Prismatic Atlatl Dart", Category = WeaponCategory.Atlatl, RequiresSpecialized = true, Priority = 30 },
            new AmmoRecipe { HeadBundleName = "Wrapped Bundle of Greater Prismatic Atlatl Dart Heads", ShaftBundleName = "Wrapped Bundle of Atlatl Dart Shafts", OutputName = "Greater Prismatic Atlatl Dart", Category = WeaponCategory.Atlatl, RequiresSpecialized = false, Priority = 20 },
            new AmmoRecipe { HeadBundleName = "Wrapped Bundle of Prismatic Atlatl Dart Heads", ShaftBundleName = "Wrapped Bundle of Atlatl Dart Shafts", OutputName = "Prismatic Atlatl Dart", Category = WeaponCategory.Atlatl, RequiresSpecialized = false, Priority = 10 },
            new AmmoRecipe { HeadBundleName = "Wrapped Bundle of Armor Piercing Atlatl Dart Heads", ShaftBundleName = "Wrapped Bundle of Atlatl Dart Shafts", OutputName = "Armor Piercing Atlatl Dart", Category = WeaponCategory.Atlatl, RequiresSpecialized = false, Priority = 6 },
            new AmmoRecipe { HeadBundleName = "Wrapped Bundle of Broad Atlatl Dart Heads", ShaftBundleName = "Wrapped Bundle of Atlatl Dart Shafts", OutputName = "Broad Head Atlatl Dart", Category = WeaponCategory.Atlatl, RequiresSpecialized = false, Priority = 5 },
            new AmmoRecipe { HeadBundleName = "Wrapped Bundle of Blunt Atlatl Dart Heads", ShaftBundleName = "Wrapped Bundle of Atlatl Dart Shafts", OutputName = "Blunt Atlatl Dart", Category = WeaponCategory.Atlatl, RequiresSpecialized = false, Priority = 4 },
            new AmmoRecipe { HeadBundleName = "Wrapped Bundle of Atlatl Dart Heads", ShaftBundleName = "Wrapped Bundle of Atlatl Dart Shafts", OutputName = "Atlatl Dart", Category = WeaponCategory.Atlatl, RequiresSpecialized = false, Priority = 1 },
        };

        // ══════════════════════════════════════════════════════════════════
        //  CONSTRUCTOR
        // ══════════════════════════════════════════════════════════════════

        public MissileCraftingManager(CoreManager core, UISettings settings, PluginHost host)
        {
            _core = core;
            _settings = settings;
            _host = host;
        }

        // ══════════════════════════════════════════════════════════════════
        //  MAIN TICK
        // ══════════════════════════════════════════════════════════════════

        public void ProcessCrafting()
        {
            if (!_settings.EnableMissileCrafting) return;
            if (_core.Actions.BusyState != 0) return;

            switch (State)
            {
                case CraftState.Idle:        ProcessIdle(); break;
                case CraftState.Evaluating:  ProcessEvaluating(); break;
                case CraftState.Combining:   ProcessCombining(); break;
                case CraftState.EquippingAmmo: ProcessEquippingAmmo(); break;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  CHAT HANDLER — called from PluginCore.OnChatBoxMessage
        //  Listens for "You make" to confirm a successful combine.
        // ══════════════════════════════════════════════════════════════════

        public void HandleChat(string text)
        {
            if (State != CraftState.Combining) return;
            if (string.IsNullOrEmpty(text)) return;

            // AC craft success: "You make a big bundle of deadly prismatic arrows."
            if (text.IndexOf("You make", StringComparison.OrdinalIgnoreCase) < 0) return;

            _combinesCompleted++;
            ChatLog($"Craft confirmed ({_combinesCompleted}/{_totalCombines})");

            // More pairs to combine?
            if (_pendingBundlePairs.Count > 0)
            {
                var pair = _pendingBundlePairs.Dequeue();
                _headBundleId = pair[0];
                _shaftBundleId = pair[1];
                _lastApplyAttempt = DateTime.MinValue; // Force immediate ApplyItem next tick
                StatusMessage = $"Crafting {_currentRecipe.OutputName} (batch {_combinesCompleted + 1})...";
            }
            else
            {
                // All combines done — signal ProcessCombining to transition
                _allCombinesDone = true;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  IDLE — only triggers when ammo slot is empty
        // ══════════════════════════════════════════════════════════════════

        private void ProcessIdle()
        {
            if ((DateTime.Now - _lastAmmoCheck).TotalMilliseconds < AMMO_CHECK_INTERVAL_MS) return;
            _lastAmmoCheck = DateTime.Now;

            WeaponCategory? category = GetEquippedMissileCategory();
            if (category == null) return;
            if (HasWieldedAmmo(category.Value)) return;

            ChatLog($"No {GetCategoryLabel(category.Value)}s equipped. Evaluating...");
            SetState(CraftState.Evaluating);
        }

        // ══════════════════════════════════════════════════════════════════
        //  EVALUATING — decide: craft or equip existing?
        // ══════════════════════════════════════════════════════════════════

        private void ProcessEvaluating()
        {
            WeaponCategory? category = GetEquippedMissileCategory();
            if (category == null) { Reset("No missile weapon equipped"); return; }

            int trainingLevel = 0;
            try { trainingLevel = (int)_core.CharacterFilter.Skills[CharFilterSkillType.Fletching].Training; } catch { }

            var inv = _core.WorldFilter.GetInventory().ToList();

            // Find best craftable recipe
            AmmoRecipe bestCraftable = null;
            foreach (var recipe in AllRecipes
                .Where(r => r.Category == category.Value)
                .OrderByDescending(r => r.Priority))
            {
                if (recipe.RequiresSpecialized && trainingLevel < 3) continue;
                if (!recipe.RequiresSpecialized && trainingLevel < 2) continue;

                bool hasHead = inv.Any(i => i.Name != null && i.Name.Equals(recipe.HeadBundleName, StringComparison.OrdinalIgnoreCase));
                bool hasShaft = inv.Any(i => i.Name != null && i.Name.Equals(recipe.ShaftBundleName, StringComparison.OrdinalIgnoreCase));

                if (hasHead && hasShaft) { bestCraftable = recipe; break; }
            }

            // Find best loose ammo
            int bestExistingPriority = 0;
            WorldObject bestExistingAmmo = null;
            foreach (var item in inv)
            {
                if (!IsLooseAmmo(item, category.Value)) continue;
                int pri = GetAmmoPriority(item, category.Value);
                if (pri > bestExistingPriority) { bestExistingPriority = pri; bestExistingAmmo = item; }
            }

            // Can craft better? → Craft.
            if (bestCraftable != null && bestCraftable.Priority > bestExistingPriority)
            { StartCrafting(bestCraftable, inv); return; }

            // Loose ammo? → Equip.
            if (bestExistingAmmo != null)
            {
                _equipTargetId = bestExistingAmmo.Id;
                ChatLog($"Equipping {bestExistingAmmo.Name} (x{bestExistingAmmo.Values(LongValueKey.StackCount, 1)})");
                SetState(CraftState.EquippingAmmo);
                return;
            }

            // Can craft anything? → Craft.
            if (bestCraftable != null)
            { StartCrafting(bestCraftable, inv); return; }

            Reset("No ammo and no bundles available");
        }

        private void StartCrafting(AmmoRecipe recipe, List<WorldObject> inv)
        {
            _currentRecipe = recipe;
            _pendingBundlePairs.Clear();
            _combinesCompleted = 0;
            _allCombinesDone = false;

            // Enter Peace mode so the game client doesn't auto-fire arrows mid-craft
            try { _core.Actions.SetCombatMode(CombatState.Peace); } catch { }

            // Find head and shaft bundles. Bundles are stackable — a single WorldObject
            // can have StackCount=2+, meaning multiple combines from the same item ID.
            var headItems = inv.Where(i => i.Name != null && i.Name.Equals(recipe.HeadBundleName, StringComparison.OrdinalIgnoreCase)).ToList();
            var shaftItems = inv.Where(i => i.Name != null && i.Name.Equals(recipe.ShaftBundleName, StringComparison.OrdinalIgnoreCase)).ToList();

            // Sum total available across all stacks
            int totalHeads = 0;
            foreach (var h in headItems) totalHeads += h.Values(LongValueKey.StackCount, 1);
            int totalShafts = 0;
            foreach (var s in shaftItems) totalShafts += s.Values(LongValueKey.StackCount, 1);

            int pairCount = Math.Min(2, Math.Min(totalHeads, totalShafts));
            if (pairCount <= 0) { Reset("No bundles available"); return; }

            // Queue up pairs. Each combine uses the same IDs if stacked — the server
            // decrements the stack. Only need different IDs if we exhaust one stack.
            int headIdx = 0, shaftIdx = 0;
            int headRemaining = headItems[0].Values(LongValueKey.StackCount, 1);
            int shaftRemaining = shaftItems[0].Values(LongValueKey.StackCount, 1);

            for (int i = 0; i < pairCount; i++)
            {
                // Advance to next stack if current one is exhausted
                while (headRemaining <= 0 && headIdx < headItems.Count - 1)
                { headIdx++; headRemaining = headItems[headIdx].Values(LongValueKey.StackCount, 1); }
                while (shaftRemaining <= 0 && shaftIdx < shaftItems.Count - 1)
                { shaftIdx++; shaftRemaining = shaftItems[shaftIdx].Values(LongValueKey.StackCount, 1); }

                _pendingBundlePairs.Enqueue(new[] { headItems[headIdx].Id, shaftItems[shaftIdx].Id });
                headRemaining--;
                shaftRemaining--;
            }

            _totalCombines = pairCount;

            // Pop first pair
            var first = _pendingBundlePairs.Dequeue();
            _headBundleId = first[0];
            _shaftBundleId = first[1];

            ChatLog($"Crafting {recipe.OutputName} x{_totalCombines}");
            StatusMessage = $"Crafting {recipe.OutputName}...";
            _lastApplyAttempt = DateTime.MinValue;
            SetState(CraftState.Combining);
        }

        // ══════════════════════════════════════════════════════════════════
        //  COMBINING — issues ApplyItem, retries until "You make" in chat.
        //  HandleChat advances to next pair or signals done.
        // ══════════════════════════════════════════════════════════════════

        private void ProcessCombining()
        {
            // HandleChat flagged all combines done → equip
            if (_allCombinesDone)
            {
                StatusMessage = $"Equipping {_currentRecipe.OutputName}...";
                _equipTargetId = 0;
                SetState(CraftState.EquippingAmmo);
                return;
            }

            // Timeout safety
            if ((DateTime.Now - _phaseStart).TotalMilliseconds > CRAFT_TIMEOUT_MS)
            {
                if (_combinesCompleted > 0)
                {
                    ChatLog($"Timeout after {_combinesCompleted} combine(s). Equipping what we have.");
                    _equipTargetId = 0;
                    SetState(CraftState.EquippingAmmo);
                }
                else
                {
                    Reset("Craft timed out — no response from server");
                }
                return;
            }

            // Issue / retry ApplyItem every APPLY_RETRY_MS
            if ((DateTime.Now - _lastApplyAttempt).TotalMilliseconds >= APPLY_RETRY_MS)
            {
                try
                {
                    _host.Actions.ApplyItem(_headBundleId, _shaftBundleId);
                    _lastApplyAttempt = DateTime.Now;
                    StatusMessage = $"Combining → {_currentRecipe.OutputName} ({_combinesCompleted + 1}/{_totalCombines})...";
                }
                catch (Exception ex)
                {
                    Reset($"Combine failed: {ex.Message}");
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  EQUIPPING — UseItem on the ammo to wield it
        // ══════════════════════════════════════════════════════════════════

        private void ProcessEquippingAmmo()
        {
            if ((DateTime.Now - _phaseStart).TotalMilliseconds < EQUIP_DELAY_MS) return;

            try
            {
                int targetId = _equipTargetId;

                if (targetId == 0)
                {
                    WeaponCategory? cat = GetEquippedMissileCategory();
                    if (cat != null)
                    {
                        var ammo = FindBestAmmoInInventory(cat.Value);
                        if (ammo != null) targetId = ammo.Id;
                    }
                }

                if (targetId != 0)
                {
                    var ammoItem = _core.WorldFilter[targetId];
                    if (ammoItem != null)
                    {
                        if (ammoItem.Values((LongValueKey)10, 0) > 0)
                        {
                            ChatLog($"Already equipped: {ammoItem.Name}");
                        }
                        else
                        {
                            _host.Actions.UseItem(targetId, 0);
                            ChatLog($"Equipped {ammoItem.Name} (x{ammoItem.Values(LongValueKey.StackCount, 1)})");
                        }
                    }
                    else
                    {
                        ChatLog("Ammo item no longer exists.");
                    }
                }
                else
                {
                    ChatLog("Could not find ammo to equip.");
                }
            }
            catch (Exception ex)
            {
                ChatLog($"Equip error: {ex.Message}");
            }

            Reset($"Done: {_currentRecipe?.OutputName ?? "ammo"}");
        }

        // ══════════════════════════════════════════════════════════════════
        //  HELPERS
        // ══════════════════════════════════════════════════════════════════

        private bool HasWieldedAmmo(WeaponCategory category)
        {
            try
            {
                foreach (var item in _core.WorldFilter.GetInventory())
                {
                    if (item.Values((LongValueKey)10, 0) <= 0) continue;
                    if (IsLooseAmmo(item, category)) return true;
                }
            }
            catch { }
            return false;
        }

        private bool IsLooseAmmo(WorldObject item, WeaponCategory category)
        {
            if (item.Name == null) return false;
            string n = item.Name;

            if (n.Contains("Bundle") || n.Contains("Wrapped")) return false;
            if (n.Contains("Arrowhead") || n.Contains("Arrowshaft")) return false;
            if (n.Contains("Quarrelhead") || n.Contains("Quarrelshaft")) return false;
            if (n.Contains("Dart Head") || n.Contains("Dart Shaft")) return false;

            switch (category)
            {
                case WeaponCategory.Bow: return n.Contains("Arrow");
                case WeaponCategory.Crossbow: return n.Contains("Quarrel") || n.Contains("Bolt");
                case WeaponCategory.Atlatl: return n.Contains("Dart");
                default: return false;
            }
        }

        private int GetAmmoPriority(WorldObject item, WeaponCategory category)
        {
            if (item.Name == null) return 0;
            foreach (var recipe in AllRecipes.Where(r => r.Category == category).OrderByDescending(r => r.Priority))
            {
                if (item.Name.Equals(recipe.OutputName, StringComparison.OrdinalIgnoreCase))
                    return recipe.Priority;
            }
            return 1;
        }

        private WorldObject FindBestAmmoInInventory(WeaponCategory category)
        {
            int bestPri = 0;
            WorldObject best = null;
            foreach (var item in _core.WorldFilter.GetInventory())
            {
                if (!IsLooseAmmo(item, category)) continue;
                int pri = GetAmmoPriority(item, category);
                if (pri > bestPri) { bestPri = pri; best = item; }
            }
            return best;
        }

        private WeaponCategory? GetEquippedMissileCategory()
        {
            try
            {
                foreach (var wo in _core.WorldFilter.GetInventory())
                {
                    if (wo.Values((LongValueKey)10, 0) <= 0) continue;
                    if (wo.ObjectClass != ObjectClass.MissileWeapon) continue;
                    string name = wo.Name?.ToLower() ?? "";
                    if (name.Contains("crossbow")) return WeaponCategory.Crossbow;
                    if (name.Contains("atlatl")) return WeaponCategory.Atlatl;
                    return WeaponCategory.Bow;
                }
            }
            catch { }
            return null;
        }

        private string GetCategoryLabel(WeaponCategory cat)
        {
            switch (cat)
            {
                case WeaponCategory.Bow: return "Arrow";
                case WeaponCategory.Crossbow: return "Quarrel";
                case WeaponCategory.Atlatl: return "Dart";
                default: return "Ammo";
            }
        }

        private void SetState(CraftState newState)
        {
            State = newState;
            _phaseStart = DateTime.Now;
        }

        private void Reset(string reason)
        {
            StatusMessage = reason;
            State = CraftState.Idle;
            _currentRecipe = null;
            _headBundleId = 0;
            _shaftBundleId = 0;
            _equipTargetId = 0;
            _allCombinesDone = false;
            _combinesCompleted = 0;
            _totalCombines = 0;
            _pendingBundlePairs.Clear();
        }

        private void ChatLog(string msg)
        {
            try { _host.Actions.AddChatText($"[RynthAi Craft] {msg}", 1); } catch { }
        }
    }
}
