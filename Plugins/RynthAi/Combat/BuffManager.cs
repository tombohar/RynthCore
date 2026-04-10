using System;
using System.Collections.Generic;
using Decal.Adapter;
using Decal.Adapter.Wrappers;
using Decal.Filters;

namespace NexSuite.Plugins.RynthAi
{
    public class BuffManager : IDisposable
    {
        private CoreManager _core;
        private UISettings _settings;
        private SpellManager _spellManager;

        private DateTime _lastCastAttempt = DateTime.MinValue;
        private bool _isForceRebuffing = false;
        private int _pendingSpellId = 0;

        private class RamTimerInfo
        {
            public DateTime Expiration;
            public int SpellLevel;
            public string SpellName; // Added to make debug easier
        }
        private Dictionary<int, RamTimerInfo> _ramBuffTimers = new Dictionary<int, RamTimerInfo>();
        private string _buffTimerPath;

        private bool _isRechargingMana = false;
        private bool _isRechargingStamina = false;
        private bool _isHealingSelf = false;

        private readonly List<string> BaseCreatureBuffs = new List<string>
        {
            "Strength Self", "Endurance Self", "Coordination Self",
            "Quickness Self", "Focus Self", "Willpower Self",
            "Magic Resistance Self", "Invulnerability Self", "Impregnability Self"
        };

        private readonly Dictionary<CharFilterSkillType, string> CreatureSkillBuffs = new Dictionary<CharFilterSkillType, string>
        {
            { CharFilterSkillType.MeleeDefense, "Invulnerability Self" },
            { CharFilterSkillType.MissileDefense, "Impregnability Self" },
            { CharFilterSkillType.MagicDefense, "Magic Resistance Self" },
            { CharFilterSkillType.HeavyWeapons, "Heavy Weapon Mastery" },
            { CharFilterSkillType.LightWeapons, "Light Weapon Mastery" },
            { CharFilterSkillType.FinesseWeapons, "Finesse Weapon Mastery" },
            { CharFilterSkillType.TwoHandedCombat, "Two Handed Combat Mastery" },
            { CharFilterSkillType.Shield, "Shield Mastery" },
            { CharFilterSkillType.DualWield, "Dual Wield Mastery" },
            { CharFilterSkillType.Recklessness, "Recklessness Mastery" },
            { CharFilterSkillType.SneakAttack, "Sneak Attack Mastery" },
            { CharFilterSkillType.DirtyFighting, "Dirty Fighting Mastery" },
            { CharFilterSkillType.AssessCreature, "Monster Attunement" },
            { CharFilterSkillType.AssessPerson, "Person Attunement" },
            { CharFilterSkillType.ArcaneLore, "Arcane Enlightenment" },
            { CharFilterSkillType.ArmorTinkering, "Armor Tinkering Expertise" },
            { CharFilterSkillType.ItemTinkering, "Item Tinkering Expertise" },
            { CharFilterSkillType.MagicItemTinkering, "Magic Item Tinkering Expertise" },
            { CharFilterSkillType.WeaponTinkering, "Weapon Tinkering Expertise" },
            { CharFilterSkillType.Salvaging, "Arcanum Salvaging" },
            { CharFilterSkillType.Run, "Sprint" },
            { CharFilterSkillType.Jump, "Jumping Mastery" },
            { CharFilterSkillType.Loyalty, "Fealty" },
            { CharFilterSkillType.Leadership, "Leadership Mastery" },
            { CharFilterSkillType.Deception, "Deception Mastery" },
            { CharFilterSkillType.Healing, "Healing Mastery" },
            { CharFilterSkillType.Lockpick, "Lockpick Mastery" },
            { CharFilterSkillType.Cooking, "Cooking Mastery" },
            { CharFilterSkillType.Fletching, "Fletching Mastery" },
            { CharFilterSkillType.Alchemy, "Alchemy Mastery" },
            { CharFilterSkillType.ManaConversion, "Mana Conversion Mastery" },
            { CharFilterSkillType.CreatureEnchantment, "Creature Enchantment Mastery" },
            { CharFilterSkillType.ItemEnchantment, "Item Enchantment Mastery" },
            { CharFilterSkillType.LifeMagic, "Life Magic Mastery" },
            { CharFilterSkillType.WarMagic, "War Magic Mastery" },
            { CharFilterSkillType.VoidMagic, "Void Magic Mastery" },
            { CharFilterSkillType.Summoning, "Summoning Mastery" }
        };

        public BuffManager(CoreManager core, UISettings settings, SpellManager spellManager)
        {
            _core = core;
            _settings = settings;
            _spellManager = spellManager;
            _core.ChatBoxMessage += OnChatMsg;
        }

        /// <summary>
        /// Set the path for persisting buff timers. Call after settings path is known.
        /// Automatically loads any existing timers from disk.
        /// </summary>
        public void SetTimerPath(string charFolder)
        {
            _buffTimerPath = System.IO.Path.Combine(charFolder, "bufftimers.txt");
            LoadBuffTimers();
        }

        public void SaveBuffTimers()
        {
            if (string.IsNullOrEmpty(_buffTimerPath)) return;
            try
            {
                var lines = new System.Collections.Generic.List<string>();
                foreach (var kvp in _ramBuffTimers)
                {
                    var info = kvp.Value;
                    // Format: Family|Expiration(ticks)|SpellLevel|SpellName
                    lines.Add($"{kvp.Key}|{info.Expiration.Ticks}|{info.SpellLevel}|{info.SpellName}");
                }
                System.IO.File.WriteAllLines(_buffTimerPath, lines);
            }
            catch { }
        }

        public void LoadBuffTimers()
        {
            if (string.IsNullOrEmpty(_buffTimerPath)) return;
            if (!System.IO.File.Exists(_buffTimerPath)) return;
            try
            {
                _ramBuffTimers.Clear();
                foreach (string line in System.IO.File.ReadAllLines(_buffTimerPath))
                {
                    string[] parts = line.Split('|');
                    if (parts.Length < 4) continue;

                    int family = int.Parse(parts[0]);
                    DateTime expiration = new DateTime(long.Parse(parts[1]));
                    int level = int.Parse(parts[2]);
                    string name = parts[3];

                    // Only load if not expired
                    if (expiration > DateTime.Now)
                    {
                        _ramBuffTimers[family] = new RamTimerInfo
                        {
                            Expiration = expiration,
                            SpellLevel = level,
                            SpellName = name
                        };
                    }
                }
                if (_ramBuffTimers.Count > 0)
                    _core.Actions.AddChatText($"[RynthAi] Restored {_ramBuffTimers.Count} buff timer(s) from last session.", 1);
            }
            catch { }
        }

        public void Dispose() => _core.ChatBoxMessage -= OnChatMsg;

        public void ForceFullRebuff()
        {
            _isForceRebuffing = true;
            _ramBuffTimers.Clear();
            SaveBuffTimers();
            _core.Actions.AddChatText("[RynthAi] Starting Force Rebuff...", 5);
        }

        public void CancelBuffing()
        {
            _isForceRebuffing = false;
            _isRechargingMana = false;
            _isRechargingStamina = false;
            _isHealingSelf = false;
            _pendingSpellId = 0;
            _core.Actions.AddChatText("[RynthAi] Sequence cancelled.", 5);
        }

        public void OnHeartbeat()
        {
            if (!_settings.IsMacroRunning) return;
            if (_core.CharacterFilter.LoginStatus != 3) return;

            // 1. Are we locked in a cast/equip animation?
            if ((DateTime.Now - _lastCastAttempt).TotalMilliseconds < 1500)
            {
                _settings.CurrentState = "Buffing";
                return;
            }

            // 2. Do we need to heal/recharge?
            if (CheckVitals())
            {
                _settings.CurrentState = "Buffing";
                return;
            }

            // 3. Do we need to buff?
            if (_settings.EnableBuffing)
            {
                if (CheckAndCastSelfBuffs())
                {
                    _settings.CurrentState = "Buffing";
                    return;
                }

                if (_isForceRebuffing)
                {
                    _isForceRebuffing = false;
                    _core.Actions.AddChatText("[RynthAi] Force Rebuff Complete.", 1);
                }
            }

            // === THE FIX ===
            // 4. Only release the traffic light if WE were the ones holding it!
            if (_settings.CurrentState == "Buffing")
            {
                _settings.CurrentState = "Idle";
            }
        }

        private bool CheckVitals()
        {
            double maxHealth = _core.CharacterFilter.EffectiveVital[CharFilterVitalType.Health];
            double maxMana = _core.CharacterFilter.EffectiveVital[CharFilterVitalType.Mana];
            double maxStam = _core.CharacterFilter.EffectiveVital[CharFilterVitalType.Stamina];

            int curHealthPct = maxHealth > 0 ? (int)((_core.CharacterFilter.Health / maxHealth) * 100) : 100;
            int curManaPct = maxMana > 0 ? (int)((_core.CharacterFilter.Mana / maxMana) * 100) : 100;
            int curStamPct = maxStam > 0 ? (int)((_core.CharacterFilter.Stamina / maxStam) * 100) : 100;

            if (curHealthPct <= 30 && curStamPct > 20) return AttemptVitalCast("Stamina to Health Self");

            if (curHealthPct <= _settings.HealAt) _isHealingSelf = true;
            if (curHealthPct >= _settings.TopOffHP) _isHealingSelf = false;
            if (curManaPct <= _settings.GetManaAt) _isRechargingMana = true;
            if (curManaPct >= _settings.TopOffMana) _isRechargingMana = false;
            if (curStamPct <= _settings.RestamAt) _isRechargingStamina = true;
            if (curStamPct >= _settings.TopOffStam) _isRechargingStamina = false;

            if (_isHealingSelf) return AttemptVitalCast("Heal Self");
            if (_isRechargingMana && curStamPct > 15) return AttemptVitalCast("Stamina to Mana Self");
            if (_isRechargingStamina) return AttemptVitalCast("Revitalize Self");

            return false;
        }

        private bool AttemptVitalCast(string baseName)
        {
            int spellId = FindBestSpellId(baseName, CharFilterSkillType.LifeMagic);
            if (spellId == 0) return false;

            // Use our new wand-equipping check
            if (!EnsureMagicMode()) return true;

            _core.Actions.CastSpell(spellId, _core.CharacterFilter.Id);
            _lastCastAttempt = DateTime.Now;
            return true;
        }

        private bool CheckAndCastSelfBuffs()
        {
            List<string> desiredBuffs = BuildDynamicBuffList();

            foreach (string buffBaseName in desiredBuffs)
            {
                CharFilterSkillType castSkill = SkillForBuff(buffBaseName);
                if (!IsSkillUsable(castSkill)) continue;

                int spellId = FindBestSpellId(buffBaseName, castSkill);
                if (spellId == 0) continue;

                if (!IsBuffActive(spellId))
                {
                    // Use our new wand-equipping check
                    if (!EnsureMagicMode()) return true;

                    _pendingSpellId = spellId;

                    var fs = _core.Filter<FileService>();
                    var spell = fs.SpellTable.GetById(spellId);
                    if (spell != null) _core.Actions.AddChatText($"[RynthAi] Casting: {spell.Name}", 5);

                    _core.Actions.CastSpell(spellId, _core.CharacterFilter.Id);
                    _lastCastAttempt = DateTime.Now;
                    return true;
                }
            }
            return false;
        }

        private int FindBestSpellId(string baseName, CharFilterSkillType skill)
        {
            // GetDynamicSelfBuffId handles the correct priority:
            // Incantation → Lore name → VII → VI → ... → I
            return _spellManager.GetDynamicSelfBuffId(baseName, skill);
        }

        private bool IsArmorEnchantment(string name)
        {
            string[] armorSpells = {
                "Impenetrability", "Brogard's Defiance", "Acid Bane", "Olthoi's Bane",
                "Blade Bane", "Swordsman's Bane", "Swordman's Bane", "Bludgeoning Bane", "Tusker's Bane",
                "Flame Bane", "Inferno's Bane", "Frost Bane", "Gelidite's Bane",
                "Lightning Bane", "Astyrrian's Bane", "Piercing Bane", "Archer's Bane"
            };

            foreach (string s in armorSpells)
            {
                if (name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private bool IsBuffActive(int spellId)
        {
            var fs = _core.Filter<FileService>();
            var targetSpell = fs.SpellTable.GetById(spellId);
            if (targetSpell == null) return false;

            int targetLevel = GetSpellLevel(targetSpell);

            if (_ramBuffTimers.TryGetValue(targetSpell.Family, out RamTimerInfo timer))
            {
                if (DateTime.Now < timer.Expiration.AddMinutes(-5))
                {
                    if (timer.SpellLevel >= targetLevel) return true;
                }
            }

            if (_isForceRebuffing) return false;

            if (IsArmorEnchantment(targetSpell.Name))
            {
                return false;
            }

            foreach (var ench in _core.CharacterFilter.Enchantments)
            {
                var activeSpell = fs.SpellTable.GetById(ench.SpellId);
                if (activeSpell != null && activeSpell.Family == targetSpell.Family)
                {
                    if (GetSpellLevel(activeSpell) < targetLevel) continue;
                    return ench.TimeRemaining > 300;
                }
            }
            return false;
        }

        private int GetSpellLevel(Spell spell)
        {
            if (spell == null) return 0;
            string n = spell.Name;
            if (n.StartsWith("Incantation")) return 8;
            if (n.Contains(" VII")) return 7;
            if (n.Contains(" VI")) return 6;
            if (n.Contains(" V")) return 5;
            if (n.Contains(" IV")) return 4;
            if (n.Contains(" III")) return 3;
            if (n.Contains(" II")) return 2;
            if (n.EndsWith(" I") || n.Contains(" I ")) return 1;

            if (n.Contains("Mastery") || n.Contains("Blessing") || n.Contains("Aura of") ||
                n.Contains("Intervention") || n.Contains("Trance") || n.Contains("Recovery") ||
                n.Contains("Robustify") || n.Contains("Persistence") || n.Contains("Robustification") ||
                n.Contains("Might of the Lugians") || n.Contains("Preservance") || n.Contains("Perseverance") ||
                n.Contains("Honed Control") || n.Contains("Hastening") || n.Contains("Inner Calm") ||
                n.Contains("Mind Blossom") || n.Contains("Infected Caress") || n.Contains("Elysa's Sight") ||
                n.Contains("Infected Spirit") || n.Contains("Atlan's Alacrity") || n.Contains("Cragstone's Will") ||
                n.Contains("Brogard's Defiance") || n.Contains("Olthoi's Bane") || n.Contains("Swordsman's Bane") ||
                n.Contains("Swordman's Bane") || n.Contains("Tusker's Bane") || n.Contains("Inferno's Bane") ||
                n.Contains("Gelidite's Bane") || n.Contains("Astyrrian's Bane") || n.Contains("Archer's Bane"))
                return 7;

            return 1;
        }

        private int GetArchmageEnduranceCount()
        {
            try
            {
                var me = _core.WorldFilter[_core.CharacterFilter.Id];
                if (me != null)
                {
                    int count = me.Values((LongValueKey)238, 0);
                    return Math.Min(Math.Max(count, 0), 5);
                }
            }
            catch { }
            return 0;
        }

        private double GetCustomSpellDuration(int spellLevel)
        {
            double baseSeconds = 1800;
            if (spellLevel == 6) baseSeconds = 2700;
            else if (spellLevel == 7) baseSeconds = 3600;
            else if (spellLevel == 8) baseSeconds = 5400;

            int augs = GetArchmageEnduranceCount();
            return baseSeconds * (1.0 + (augs * 0.20));
        }

        public void PrintBuffDebug()
        {
            int augs = GetArchmageEnduranceCount();
            _core.Actions.AddChatText($"[RynthAi] Archmage endurance count: {augs}", 1);

            if (_ramBuffTimers.Count == 0)
            {
                _core.Actions.AddChatText("[RynthAi] No RAM timers active.", 1);
                return;
            }

            foreach (var kvp in _ramBuffTimers)
            {
                var info = kvp.Value;
                double total = GetCustomSpellDuration(info.SpellLevel);
                TimeSpan left = info.Expiration - DateTime.Now;
                double passed = total - left.TotalSeconds;

                _core.Actions.AddChatText($"[RynthAi] {info.SpellName} (Lvl {info.SpellLevel}): {Math.Round(passed / 60, 1)}m passed, {Math.Round(left.TotalMinutes, 1)}m left. Total: {total / 60}m", 1);
            }
        }

        private List<string> BuildDynamicBuffList()
        {
            List<string> step1_CreatureMastery = new List<string>();
            List<string> step2_Focus = new List<string>();
            List<string> step3_Willpower = new List<string>();
            List<string> step4_OtherCreature = new List<string>();
            List<string> step5_LifeAndItem = new List<string>();
            List<string> step6_WeaponAuras = new List<string>();
            List<string> step7_ArmorBanes = new List<string>();

            foreach (string attr in BaseCreatureBuffs)
            {
                if (attr == "Focus Self") step2_Focus.Add(attr);
                else if (attr == "Willpower Self") step3_Willpower.Add(attr);
                else step4_OtherCreature.Add(attr);
            }

            foreach (var kvp in CreatureSkillBuffs)
            {
                if (IsSkillUsable(kvp.Key))
                {
                    if (kvp.Value.Contains("Creature Enchantment Mastery"))
                        step1_CreatureMastery.Add(kvp.Value);
                    else if (!BaseCreatureBuffs.Contains(kvp.Value))
                        step4_OtherCreature.Add(kvp.Value);
                }
            }

            step5_LifeAndItem.AddRange(new List<string> {
                "Regeneration Self", "Rejuvenation Self", "Mana Renewal Self",
                "Armor Self", "Acid Protection Self", "Fire Protection Self",
                "Cold Protection Self", "Lightning Protection Self",
                "Blade Protection Self", "Piercing Protection Self",
                "Bludgeoning Protection Self", "Impregnability Self"
            });

            step6_WeaponAuras.AddRange(new List<string> {
                "Blood Drinker Self", "Hermetic Link Self", "Heart Seeker Self",
                "Spirit Drinker Self", "Swift Killer Self", "Defender Self"
            });

            step7_ArmorBanes.AddRange(new List<string> {
                "Impenetrability", "Acid Bane", "Blade Bane", "Bludgeoning Bane",
                "Flame Bane", "Frost Bane", "Lightning Bane", "Piercing Bane"
            });

            List<string> final = new List<string>();
            final.AddRange(step1_CreatureMastery);
            final.AddRange(step2_Focus);
            final.AddRange(step3_Willpower);
            final.AddRange(step4_OtherCreature);
            final.AddRange(step5_LifeAndItem);
            final.AddRange(step6_WeaponAuras);
            final.AddRange(step7_ArmorBanes);
            return final;
        }

        private void OnChatMsg(object sender, ChatTextInterceptEventArgs e)
        {
            string text = e.Text.ToLower();
            if (text.Contains("you cast ") || text.Contains("you say, "))
            {
                if (_pendingSpellId != 0)
                {
                    var fs = _core.Filter<FileService>();
                    var spellInfo = fs.SpellTable.GetById(_pendingSpellId);
                    if (spellInfo != null)
                    {
                        int level = GetSpellLevel(spellInfo);
                        double dur = GetCustomSpellDuration(level);

                        _ramBuffTimers[spellInfo.Family] = new RamTimerInfo
                        {
                            Expiration = DateTime.Now.AddSeconds(dur),
                            SpellLevel = level,
                            SpellName = spellInfo.Name
                        };
                        SaveBuffTimers();
                    }
                    _pendingSpellId = 0;
                }
            }
            else if (text.Contains("fizzle") || text.Contains("fail") || text.Contains("component") || text.Contains("lack the mana"))
            {
                _lastCastAttempt = DateTime.MinValue;
                _pendingSpellId = 0;
            }
        }

        private bool IsSkillUsable(CharFilterSkillType s) => (int)_core.CharacterFilter.Skills[s].Training >= 2;

        private CharFilterSkillType SkillForBuff(string name)
        {
            if (name.Contains("Impregnability") || name.Contains("Blood Drinker") ||
                name.Contains("Hermetic Link") || name.Contains("Heart Seeker") ||
                name.Contains("Spirit Drinker") || name.Contains("Swift Killer") ||
                name.Contains("Defender") || name.Contains("Impenetrability") ||
                name.Contains("Bane"))
                return CharFilterSkillType.ItemEnchantment;

            if (name.Contains("Protection") || name.Contains("Armor") ||
                name.Contains("Regeneration") || name.Contains("Rejuvenation") ||
                name.Contains("Renewal") || name == "Harlune's Blessing" ||
                name.Contains("Stamina to Mana") || name.Contains("Revitalize") ||
                name.Contains("Stamina to Health") || name == "Heal Self")
                return CharFilterSkillType.LifeMagic;

            return CharFilterSkillType.CreatureEnchantment;
        }

        private bool EnsureMagicMode()
        {
            if (_core.Actions.CombatMode == CombatState.Magic)
                return true; // We are in magic mode and ready to cast

            bool wandEquipped = false;
            int wandToEquip = 0;

            // 1. Check ItemRules for a preferred wand
            foreach (var rule in _settings.ItemRules)
            {
                var ruleItem = _core.WorldFilter[rule.Id];
                if (ruleItem != null && ruleItem.ObjectClass == ObjectClass.WandStaffOrb)
                {
                    wandToEquip = ruleItem.Id;
                    if (ruleItem.Values(LongValueKey.EquippedSlots, 0) > 0)
                    {
                        wandEquipped = true;
                    }
                    break; // Found the preferred wand
                }
            }

            // 2. Fallback: Find any wand in inventory if no rule matches
            if (wandToEquip == 0 && !wandEquipped)
            {
                foreach (WorldObject wo in _core.WorldFilter.GetInventory())
                {
                    if (wo.ObjectClass == ObjectClass.WandStaffOrb)
                    {
                        if (wo.Values(LongValueKey.EquippedSlots, 0) > 0)
                        {
                            wandEquipped = true;
                            break;
                        }
                        if (wandToEquip == 0) wandToEquip = wo.Id;
                    }
                }
            }

            // 3. Equip the wand if we aren't holding one
            if (!wandEquipped && wandToEquip != 0)
            {
                _core.Actions.UseItem(wandToEquip, 0);
                _lastCastAttempt = DateTime.Now;
                return false; // Yield tick to let the server equip the item
            }

            // 4. Safe to switch modes (we either have a wand equipped, or are empty-handed)
            _core.Actions.SetCombatMode(CombatState.Magic);
            _lastCastAttempt = DateTime.Now;
            return false; // Yield tick to let the stance animation finish
        }
    }
}