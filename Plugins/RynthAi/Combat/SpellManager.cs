using System;
using System.Collections.Generic;
using Decal.Adapter;
using Decal.Adapter.Wrappers;
using Decal.Filters;

namespace NexSuite.Plugins.RynthAi
{
    public class SpellManager
    {
        private CoreManager _core;

        public Dictionary<string, int> SpellDictionary { get; private set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Map base spell names to their exact Level 7 Lore counterparts
        private readonly Dictionary<string, string[]> LoreNames = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            // Attributes (Using the exact AC names you provided)
            { "Strength", new[] { "Might of the Lugians" } },
            { "Endurance", new[] { "Preservance", "Perseverance" } },
            { "Coordination", new[] { "Honed Control" } },
            { "Quickness", new[] { "Hastening" } },
            { "Focus", new[] { "Inner Calm" } },
            { "Willpower", new[] { "Mind Blossom" } },

            // Defenses
            { "Invulnerability", new[] { "Aura of Defense" } },
            { "Impregnability", new[] { "Aura of Deflection" } },
            { "Magic Resistance", new[] { "Aura of Resistance" } },

            // Life Magic
            { "Armor", new[] { "Executor's Blessing" } },
            { "Acid Protection", new[] { "Caustic Blessing" } },
            { "Blade Protection", new[] { "Blessing of the Blade Turner" } },
            { "Bludgeoning Protection", new[] { "Blessing of the Mace Turner" } },
            { "Cold Protection", new[] { "Icy Blessing" } },
            { "Fire Protection", new[] { "Fiery Blessing" } },
            { "Lightning Protection", new[] { "Storm's Blessing" } },
            { "Mana Renewal", new[] { "Battlemage's Blessing" } },
            { "Piercing Protection", new[] { "Blessing of the Arrow Turner" } },
            { "Regeneration", new[] { "Robustify" } },
            { "Rejuvenation", new[] { "Unflinching Persistence", "Unfinching Persistance" } },

            // Vitals
            { "Heal", new[] { "Adja's Intervention" } },
            { "Revitalize", new[] { "Robustification" } },
            { "Stamina to Mana", new[] { "Meditative Trance" } },
            { "Stamina to Health", new[] { "Rushed Recovery" } },

            // Skills & Masteries
            { "Monster Attunement", new[] { "Topheron's Blessing" } },
            { "Person Attunement", new[] { "Kaluhc's Blessing" } },
            { "Arcane Enlightenment", new[] { "Aliester's Blessing" } },
            { "Armor Tinkering Expertise", new[] { "Jibril's Blessing" } },
            { "Item Tinkering Expertise", new[] { "Yoshi's Blessing" } },
            { "Weapon Tinkering Expertise", new[] { "Koga's Blessing" } },
            { "Mana Conversion Mastery", new[] { "Nuhmidura's Blessing" } },
            { "Sprint", new[] { "Saladur's Blessing" } },
            { "Jumping Mastery", new[] { "Jahannan's Blessing" } },
            { "Fealty", new[] { "Odif's Blessing", "Odif's Boon" } },
            { "Leadership Mastery", new[] { "Ar-Pei's Blessing" } },
            { "Deception Mastery", new[] { "Ketnan's Blessing" } },
            { "Healing Mastery", new[] { "Avalenne's Blessing" } },
            { "Lockpick Mastery", new[] { "Oswald's Blessing" } },
            { "Cooking Mastery", new[] { "Morimoto's Blessing" } },
            { "Fletching Mastery", new[] { "Lilitha's Blessing" } },
            { "Alchemy Mastery", new[] { "Silencia's Blessing" } },
            { "Creature Enchantment Mastery", new[] { "Adja's Blessing" } },
            { "Item Enchantment Mastery", new[] { "Celcynd's Blessing" } },
            { "Life Magic Mastery", new[] { "Harlune's Blessing" } },
            { "War Magic Mastery", new[] { "Hieromancer's Blessing" } },

            // Weapon Auras (Item Enchantment)
            { "Blood Drinker", new[] { "Aura of Infected Caress" } },
            { "Hermetic Link", new[] { "Aura of Mystic's Blessing" } },
            { "Heart Seeker", new[] { "Aura of Elysa's Sight" } },
            { "Spirit Drinker", new[] { "Aura of Infected Spirit Carress", "Aura of Infected Spirit Caress" } },
            { "Swift Killer", new[] { "Aura of Atlan's Alacrity" } },
            { "Defender", new[] { "Aura of Cragstone's Will" } },

            // Armor Banes (Item Enchantment)
            { "Impenetrability", new[] { "Brogard's Defiance" } },
            { "Acid Bane", new[] { "Olthoi's Bane" } },
            { "Blade Bane", new[] { "Swordsman's Bane", "Swordman's Bane" } },
            { "Bludgeoning Bane", new[] { "Tusker's Bane" } },
            { "Flame Bane", new[] { "Inferno's Bane" } },
            { "Frost Bane", new[] { "Gelidite's Bane" } },
            { "Lightning Bane", new[] { "Astyrrian's Bane" } },
            { "Piercing Bane", new[] { "Archer's Bane" } }
        };

        public SpellManager(CoreManager core)
        {
            _core = core;
        }

        public void InitializeNatively()
        {
            try
            {
                var fs = _core.Filter<FileService>();
                if (fs == null) return;

                for (int i = 1; i <= 40000; i++)
                {
                    try
                    {
                        var spell = fs.SpellTable.GetById(i);
                        if (spell != null && !string.IsNullOrWhiteSpace(spell.Name))
                        {
                            SpellDictionary[spell.Name] = spell.Id;
                        }
                    }
                    catch { }
                }

                _core.Actions.AddChatText($"[RynthAi] Magic System Online: {SpellDictionary.Count} spells loaded.", 1);
            }
            catch (Exception ex) { _core.Actions.AddChatText("[RynthAi] Native load error: " + ex.Message, 2); }
        }

        public int GetHighestSpellTier(CharFilterSkillType skill)
        {
            try
            {
                int buffedSkill = _core.CharacterFilter.Skills[skill].Buffed;
                if (buffedSkill >= 400) return 8;
                if (buffedSkill >= 315) return 7; // Adjusted to common AC skill requirements
                if (buffedSkill >= 250) return 6;
                if (buffedSkill >= 200) return 5;
                if (buffedSkill >= 150) return 4;
                if (buffedSkill >= 100) return 3;
                if (buffedSkill >= 50) return 2;
                return 1;
            }
            catch { return 1; }
        }

        private string GetRomanNumeral(int tier)
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

        // Spell resolution for self-buffs. Capped by skill level.
        // Within each tier tries standard names first, then "Aura of" prefixed variants
        // (weapon enchantments like Blood Drinker, Heart Seeker use "Aura of" prefix).
        public int GetDynamicSelfBuffId(string baseSpellName, CharFilterSkillType magicSkill)
        {
            int maxTier = GetHighestSpellTier(magicSkill);
            string cleanBase = baseSpellName.Replace(" Self", "").Trim();

            for (int tier = maxTier; tier >= 1; tier--)
            {
                if (tier == 8)
                {
                    // Standard: "Incantation of Focus Self"
                    if (TryGetId($"Incantation of {cleanBase} Self", out int id1)) return id1;
                    if (TryGetId($"Incantation of {cleanBase}", out int id2)) return id2;
                    // Aura variant: "Aura of Incantation of Blood Drinker Self"
                    if (TryGetId($"Aura of Incantation of {cleanBase} Self", out int id3)) return id3;
                    if (TryGetId($"Aura of Incantation of {cleanBase}", out int id4)) return id4;
                }
                else if (tier == 7)
                {
                    // Lore names first (already includes "Aura of" where needed)
                    if (LoreNames.TryGetValue(cleanBase, out string[] lores))
                    {
                        foreach (string lore in lores)
                        {
                            if (TryGetId(lore, out int idL)) return idL;
                        }
                    }
                    // Standard VII
                    if (TryGetId($"{cleanBase} Self VII", out int id7a)) return id7a;
                    if (TryGetId($"{cleanBase} VII", out int id7b)) return id7b;
                    // Aura variant VII
                    if (TryGetId($"Aura of {cleanBase} Self VII", out int id7c)) return id7c;
                    if (TryGetId($"Aura of {cleanBase} VII", out int id7d)) return id7d;
                }
                else
                {
                    // Standard: "Focus Self VI"
                    string numeral = GetRomanNumeral(tier);
                    if (TryGetId($"{cleanBase} Self {numeral}", out int idNum1)) return idNum1;
                    if (TryGetId($"{cleanBase} {numeral}", out int idNum2)) return idNum2;
                    // Aura variant: "Aura of Blood Drinker Self VI"
                    if (TryGetId($"Aura of {cleanBase} Self {numeral}", out int idNum3)) return idNum3;
                    if (TryGetId($"Aura of {cleanBase} {numeral}", out int idNum4)) return idNum4;
                }
            }
            return 0;
        }

        private bool TryGetId(string exactName, out int spellId)
        {
            if (SpellDictionary.TryGetValue(exactName, out spellId))
            {
                if (_core.CharacterFilter.IsSpellKnown(spellId)) return true;
            }
            spellId = 0;
            return false;
        }
    }
}