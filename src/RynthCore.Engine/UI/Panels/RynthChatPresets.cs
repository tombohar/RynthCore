// ============================================================================
//  RynthCore.Engine — UI/Panels/RynthChatPresets.cs
//  Canned ("standard") RynthChat filters, modelled on UtilityBelt-IT's
//  ChatFilter / Mag ChatFilter categories. Each preset is a tick-box in the
//  ChatFilters panel that hides its lines or moves them to a tab.
//
//  Presets match the raw line text (no timestamp / sender prefix). Most are
//  limited to non-chat channels so a player typing the same words in Local or
//  a channel is never eaten. ChatRouter evaluates them on the plugin pump
//  (after the custom rules); editors change Enabled / Tab from any thread and
//  then call ChatModel.FiltersChanged so every kept line is routed again.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RynthCore.Engine.UI.Panels;

internal static class RynthChatPresets
{
    /// <summary>Which RynthChat channels a preset may match.</summary>
    internal enum Scope
    {
        /// <summary>Anything except Chat / Channels (player-typed text).</summary>
        NotPlayerChat,
        /// <summary>Only the Combat channel (spell incantations arrive as chat type 0x11 → Combat).</summary>
        CombatOnly,
        /// <summary>Only the Channels tab (General / Trade / LFG / Roleplay / Society).</summary>
        ChannelsOnly,
        /// <summary>Every channel.</summary>
        Any,
    }

    internal sealed class Preset
    {
        internal string Id          { get; }
        internal string Group       { get; }
        internal string Label       { get; }
        internal string Description { get; }
        internal string Example     { get; }
        internal Scope  Where       { get; }
        internal Regex  Pattern     { get; }

        // User state (persisted in rynthchat_settings.json "presets" by ChatModel).
        // Volatile: written by an editor, read by the pump's router.
        internal volatile bool   Enabled;
        internal volatile string Tab = "";   // "" = hide matching lines

        internal Preset(string id, string group, string label, string description, string example, Scope where, string pattern)
        {
            Id = id;
            Group = group;
            Label = label;
            Description = description;
            Example = example;
            Where = where;
            Pattern = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        }

        /// <summary>True when this preset applies to a line on <paramref name="channel"/> with raw <paramref name="text"/>.</summary>
        internal bool Matches(string text, string channel)
        {
            bool inScope = Where switch
            {
                Scope.NotPlayerChat => channel is not ("Chat" or "Channels"),
                Scope.CombatOnly    => channel == "Combat",
                Scope.ChannelsOnly  => channel == "Channels",
                _                   => true,
            };
            if (!inScope) return false;
            try { return Pattern.IsMatch(text); }
            catch (RegexMatchTimeoutException) { return false; }
        }
    }

    // Kill flavour text (retail/ACE death messages). Damage lines ("... for N points of ...")
    // share some verbs, so they are excluded up front.
    private const string KillPattern =
        @"^(?!.* for \d+ points? of )(?:" +
        @"You (?:flatten|knock|split|cleave|slay|bring|beat|run|suffocate|smite|killed) .+!" +
        @"|.+ is (?:reduced to cinders|torn to ribbons by your assault|shattered by your assault|incinerated by your assault|liquified by your attack|dessicated by your attack)!" +
        @"|.+ catches your attack, with dire consequences!" +
        @"|.+'s (?:seared corpse smolders before you|last strength dissolves before you)!" +
        @"|Electricity tears .+ apart!|Blistered by lightning, .+ falls!" +
        @"|Your killing blow nearly turns .+ inside-out!|The thunder of crushing .+ is followed by the deafening silence of death!" +
        @"|Your attack stops .+ cold!)";

    // Optional combat prefixes such as "Critical hit! " / "Sneak Attack! ".
    private const string HitPrefix = @"^(?:[A-Z][\w ]{2,20}! )*";

    // The new damage presets (melee_dealt ... magic_taken) name their prefixes: "Critical hit! ",
    // "Sneak Attack! ", "Recklessness! ". HitPrefix above is left as it was for the older presets.
    private const string KnownHitPrefix = @"^(?:(?:Critical [Hh]it|Sneak Attack|Recklessness)!\s+)*";

    // Outgoing physical (melee / missile): "... for N points of X damage!"
    private const string DealtMelee =
        KnownHitPrefix + @"You \S+ .+ for \d+ points? of .+ damage";
    // Outgoing war / void magic: "... for N points with SpellName."
    private const string DealtMagic =
        KnownHitPrefix + @"You \S+ .+ for \d+ points? with ";
    // Incoming melee / missile: body-part hits ("... your foot for N points of ...").
    private const string TakenMelee =
        KnownHitPrefix + @"(?!You ).+ your [a-z ]+ for \d+ points? of .+ damage";
    // Incoming spell damage: "... you for N points with SpellName."
    private const string TakenMagic =
        KnownHitPrefix + @"(?!You ).+ you for \d+ points? with ";

    private static readonly Preset[] _all =
    {
        // ── Combat ────────────────────────────────────────────────────────
        new("combat.attack_evades", "Combat", "Your attacks evaded",
            "A monster evades your melee or missile attack.",
            "Drudge Slinker evaded your attack.", Scope.NotPlayerChat, @" evaded your attack\.\s*$"),
        new("combat.defense_evades", "Combat", "You evade attacks",
            "You evade a monster's attack.",
            "You evaded Drudge Slinker!", Scope.NotPlayerChat, @"^You evaded "),
        // Prefix-tagged and melee / missile / magic damage, each its own switch (the older
        // "Damage you deal / take" below keep their meaning). Specific before umbrella: FirstMatch order.
        new("combat.recklessness", "Combat", "Recklessness hits",
            "Lines prefixed with \"Recklessness!\" (your reckless swings).",
            "Recklessness! You slash Drudge Slinker for 54 points of slashing damage!", Scope.NotPlayerChat,
            @"^((?:Critical [Hh]it!|Sneak Attack!)\s+)*Recklessness!"),
        new("combat.sneak_attack", "Combat", "Sneak Attack hits",
            "Lines prefixed with \"Sneak Attack!\" (your sneak swings / shots).",
            "Sneak Attack! You slash Drudge Slinker for 54 points of slashing damage!", Scope.NotPlayerChat,
            @"^((?:Critical [Hh]it!|Recklessness!)\s+)*Sneak Attack!"),
        new("combat.critical", "Combat", "Critical hits",
            "Lines prefixed with \"Critical hit!\" / \"Critical Hit!\".",
            "Critical hit! You slash Drudge Slinker for 100 points of slashing damage!", Scope.NotPlayerChat,
            @"^Critical [Hh]it!"),
        new("combat.melee_dealt", "Combat", "Melee / missile damage you deal",
            "Your weapon hits: \"You slash … for N points of … damage!\" (missile uses the same form).",
            "You slash Drudge Slinker for 54 points of slashing damage!", Scope.NotPlayerChat, DealtMelee),
        new("combat.magic_dealt", "Combat", "Magic damage you deal",
            "Your war / void hits: \"You … for N points with SpellName.\"",
            "You freeze Drudge Slinker for 120 points with Frost Bolt VI.", Scope.NotPlayerChat, DealtMagic),
        new("combat.melee_taken", "Combat", "Melee / missile damage you take",
            "Monster weapon hits on a body part (\"… grazes your foot for N points of …\").",
            "Drudge Slinker grazes your foot for 12 points of slashing damage!", Scope.NotPlayerChat, TakenMelee),
        new("combat.magic_taken", "Combat", "Magic damage you take",
            "Spell damage on you: \"… you for N points with SpellName.\"",
            "Drudge Sorcerer scorches you for 40 points with Flame Bolt III.", Scope.NotPlayerChat, TakenMagic),

        new("combat.damage_dealt", "Combat", "Damage you deal",
            "Your melee, missile and war-spell hits (including critical / sneak attack prefixes).",
            "You slash Drudge Slinker for 54 points of slashing damage!", Scope.NotPlayerChat, HitPrefix + @"You .+ for \d+ points? of "),
        new("combat.damage_taken", "Combat", "Damage you take",
            "Monster hits and spell damage landing on you.",
            "Drudge Slinker slashes you for 12 points of slashing damage!", Scope.NotPlayerChat, HitPrefix + @"(?!You ).+ you (?:with .+ )?for \d+ points? of "),
        new("combat.attack_resists", "Combat", "Your spells resisted",
            "A target resists one of your spells.",
            "Drudge Slinker resists your spell", Scope.NotPlayerChat, @" resists your spell"),
        new("combat.defense_resists", "Combat", "You resist spells",
            "You resist a spell, or a caster was out of range / had no valid target.",
            "You resist the spell cast by Drudge Sorcerer", Scope.NotPlayerChat,
            @"^You resist the spell cast by |^You have no appropriate target.*spell|^You are an invalid target for the spell|tried to cast a spell on you, but was too far away!"),
        new("combat.npk", "Combat", "Not-a-PK failures",
            "\"You fail to affect X, you are not a player killer!\" and the mirror message.",
            "You fail to affect Bob because you are not a player killer!", Scope.NotPlayerChat,
            @"^You fail to affect .+ you are not a player killer!|fails to affect you.+ is not a player killer!"),
        new("combat.dirty_fighting", "Combat", "Dirty Fighting procs",
            "Dirty Fighting assault announcements.",
            "Dirty Fighting! Bob delivers a Traumatic Assault to Drudge!", Scope.NotPlayerChat, @"^Dirty Fighting! .+ delivers a "),
        new("combat.monster_deaths", "Combat", "Monster death messages",
            "Kill flavour text (\"You flatten…\", \"…is reduced to cinders!\").",
            "Drudge Slinker is reduced to cinders!", Scope.NotPlayerChat, KillPattern),
        new("combat.missile_miss", "Combat", "Missile hit the environment",
            "\"Your missile attack hit the environment.\" (classic UB ChatFilter).",
            "Your missile attack hit the environment.", Scope.NotPlayerChat,
            @"^Your missile attack hit the environment\.\s*$"),
        new("combat.periodic_damage", "Combat", "Periodic damage ticks",
            "\"You receive N points of periodic … damage.\" DoTs on you.",
            "You receive 8 points of periodic acid damage.", Scope.NotPlayerChat,
            @"^You receive \d+ points of periodic .+damage"),
        new("combat.stamina", "Combat", "Stamina restore / spend",
            "Lines that restore or spend stamina (kits, consumables, S2H/S2M, Heal Self).",
            "You cast Revitalize Self V and restore 40 points of your stamina.", Scope.NotPlayerChat,
            @"(?:restore|restores|regain|regains|lose|loses) \d+ points? of (?:your )?stamina\b|and lose \d+ points of stamina"),
        new("combat.mana", "Combat", "Mana restore / spend",
            "Lines that restore or spend mana (consumables, S2M, mana stones' give line is under Items).",
            "You cast Stamina to Mana Self V on yourself and lose 17 points of stamina and also gain 23 points of mana", Scope.NotPlayerChat,
            @"(?:restore|restores|regain|regains|gain|gains|lose|loses) \d+ points? of (?:your )?mana\b|and also gain \d+ points of mana"),
        new("combat.health", "Combat", "Health restore",
            "Lines that restore health points (kits, heals, consumables) — not damage taken.",
            "You heal yourself for 80 Health points.", Scope.NotPlayerChat,
            @"(?:heal yourself for \d+|restore|restores|regain|regains) \d+ points? of (?:your )?health\b|heal yourself for \d+ Health"),
        new("combat.fail_affect_you", "Combat", "Fails to affect you",
            "\"… fails to affect you…\" (broader than the NPK-only filter).",
            "Bob fails to affect you because he is not a player killer!", Scope.NotPlayerChat,
            @" fails to affect you"),
        new("combat.you_fail_affect", "Combat", "You fail to affect",
            "\"You failed to affect…\" / \"You fail to affect…\" (non-PK and other).",
            "You fail to affect Bob because you are not a player killer!", Scope.NotPlayerChat,
            @"^You fail(?:ed)? to affect "),

        // ── Casting ───────────────────────────────────────────────────────
        new("cast.mine", "Casting", "Your spell words",
            "Your own incantations (\"You say, \\\"Zojak…\\\"\"). Real Local chat is untouched.",
            "You say, \"Zojak Equin\"", Scope.CombatOnly, @"^You say, """),
        new("cast.others", "Casting", "Others' spell words",
            "Incantations from other players and monsters.",
            "Bob says, \"Cruath Quareth\"", Scope.CombatOnly, @"^(?!You say).+ says, """),
        new("cast.fizzle", "Casting", "Spell fizzles",
            "\"Your spell fizzled.\"",
            "Your spell fizzled.", Scope.NotPlayerChat, @"^Your spell fizzled\."),
        new("cast.components", "Casting", "Component usage",
            "\"The spell consumed the following components: …\"",
            "The spell consumed the following components: Lead Scarab", Scope.NotPlayerChat, @"^The spell consumed the following components"),
        new("cast.self_buffs", "Casting", "Your buff casts",
            "\"You cast X on yourself…\" confirmations.",
            "You cast Strength Self VII on yourself", Scope.NotPlayerChat, @"^You cast .+ on yourself"),
        new("cast.buffs_on_you", "Casting", "Buffs others cast on you",
            "\"Bob cast X on you…\" lines from buff bots and fellows.",
            "Bob cast Strength Other VII on you", Scope.NotPlayerChat, @"^(?!You ).+ cast .+ on you\b"),
        new("cast.expires", "Casting", "Spell expiry",
            "\"… has expired.\" — rares (Brilliance / Prodigal / Spectral) are kept.",
            "Strength Self VII has expired.", Scope.NotPlayerChat, @"^(?!.*(?:Brilliance|Prodigal|Spectral)).*\b(?:has|have) expired\."),
        new("cast.give_take", "Casting", "Stamina / health / mana transfers",
            "Self casts that spend one vital and restore another (S2M, S2H, …).",
            "You cast Stamina to Mana Self V on yourself and lose 17 points of stamina and also gain 23 points of mana", Scope.NotPlayerChat,
            @"^You cast .+ on yourself and lose \d+ points of .+ and also gain \d+ points of "),
        new("cast.heal_self", "Casting", "Your heal / restore casts",
            "\"You cast … and restore N points of your …\" (spell heals, not kits).",
            "You cast Revitalize Self V and restore 40 points of your stamina.", Scope.NotPlayerChat,
            @"^You cast .+ and restore \d+ points of your "),

        // ── Items & utility ───────────────────────────────────────────────
        new("item.heal_ok", "Items", "Healing kit success",
            "\"You heal yourself for N Health points…\"",
            "You heal yourself for 80 Health points.", Scope.NotPlayerChat, @"^You .*heal yourself for "),
        new("item.heal_fail", "Items", "Healing kit failure",
            "\"You fail to heal yourself.\"",
            "You fail to heal yourself. ", Scope.NotPlayerChat, @"^You fail to heal yourself\."),
        new("item.healed_by_other", "Items", "Healed by another",
            "\"X casts Y and restores N points of your …\"",
            "Bob casts Heal Other VI and restores 80 points of your health.", Scope.NotPlayerChat,
            @"^.+ casts .+ and restores \d+ points of your "),
        new("item.consumable", "Items", "Food / potion restores",
            "\"The X restores N points of your …\"",
            "The Health Tonic restores 50 points of your health.", Scope.NotPlayerChat,
            @"^The .+ restores \d+ points of your "),
        new("item.salvage", "Items", "Salvage results",
            "\"You obtain … using your knowledge of …\"",
            "You obtain 12 Steel (ws 7.00) using your knowledge of Weapon Tinkering.", Scope.NotPlayerChat, @"^You obtain .+ using your knowledge of "),
        new("item.salvage_fail", "Items", "Salvage failures",
            "\"Salvaging Failed!\" and unsuitable-item notices.",
            "Salvaging Failed!", Scope.NotPlayerChat, @"^Salvaging Failed!|The following were not suitable for salvaging: "),
        new("item.craftsman", "Items", "Aura of the Craftsman",
            "The +5 skill augmentation notice on every craft.",
            "Your Aura of the Craftman augmentation increased your skill by 5!", Scope.NotPlayerChat, @"^Your Aura of the Craftman augmentation increased your skill"),
        new("item.mana_stone", "Items", "Mana stone usage",
            "Mana stone give / drain / destroyed lines.",
            "The Mana Stone gives 4,500 points of mana to the following items:", Scope.NotPlayerChat,
            @"^The Mana Stone (?:gives|drains) |^You need .+ more mana to fully charge your items\.\s*$|^The .+ is destroyed\.\s*$"),
        new("item.regen", "Items", "Periodic healing ticks",
            "\"You receive N points of periodic healing.\"",
            "You receive 12 points of periodic healing.", Scope.NotPlayerChat, @"^You receive .+ points of periodic healing"),
        new("item.aetheria_surge", "Items", "Aetheria surges",
            "\"Aetheria surges on…\" (classic UB Misc filter).",
            "Aetheria surges on Bob, increasing his damage.", Scope.NotPlayerChat, @"^Aetheria surges on"),
        new("item.cloak", "Items", "Cloak procs",
            "\"The cloak of…\" cloak cast lines (classic UB Misc filter).",
            "The cloak of Bob weaves a protective spell.", Scope.NotPlayerChat, @"^The cloak of"),
        new("item.banked_lum", "Items", "Banked luminance",
            "\"You've banked N Luminance.\" (classic UB Misc filter).",
            "You've banked 1,250 Luminance.", Scope.NotPlayerChat, @"^You've banked [\d,]+ Luminance\.\s*$"),

        // ── Social & trade ────────────────────────────────────────────────
        new("social.bot_spam", "Social", "Trade / buff bot spam",
            "Lines ending in the bot tags -t- or -b-.",
            "BuffBot says, \"Buffs at the Holtburg lifestone -b-\"", Scope.Any, @"-[tb]-""?\s*$"),
        new("social.failed_assess", "Social", "Failed assess on you",
            "\"Someone tried and failed to assess you!\"",
            "Bob tried and failed to assess you!", Scope.NotPlayerChat, @"tried and failed to assess you!\s*$"),
        new("social.kill_task", "Social", "Kill task complete",
            "\"You have killed N Xs! Your task is complete!\"",
            "You have killed 50 Drudges! Your task is complete!", Scope.NotPlayerChat, @"^You have killed \d+ .+Your task is complete!\s*$"),
        new("social.kill_task_progress", "Social", "Kill task progress",
            "\"You have killed N Xs! You must kill M to complete your task.\" (ACECustom / UB-IT).",
            "You have killed 12 Drudges! You must kill 50 to complete your task.", Scope.NotPlayerChat,
            @"^You have killed \d+ .+! You must kill \d+ to complete your task\.\s*$"),
        new("social.too_busy", "Social", "You're too busy",
            "\"You're too busy!\" (also Mag StatusTextYoureTooBusy when it lands in chat).",
            "You're too busy!", Scope.NotPlayerChat, @"^You're too busy!\s*$"),
        new("social.casting_status", "Social", "Casting status text",
            "\"Casting …\" overlay lines when they appear in chat (Mag StatusTextCasting).",
            "Casting Focus Self VII", Scope.NotPlayerChat, @"^Casting "),
        new("social.general", "Social", "General channel",
            "Everything said on General.",
            "[General] Bob says, \"hi\"", Scope.ChannelsOnly, @"^\[General\]|says on the General channel"),
        new("social.trade", "Social", "Trade channel",
            "Everything said on Trade.",
            "[Trade] Bob says, \"WTS\"", Scope.ChannelsOnly, @"^\[Trade\]|says on the Trade channel"),
        new("social.lfg", "Social", "LFG channel",
            "Everything said on LFG.",
            "[LFG] Bob says, \"LF fellow\"", Scope.ChannelsOnly, @"^\[LFG\]|says on the LFG channel"),
        new("social.roleplay", "Social", "Roleplay channel",
            "Everything said on Roleplay.",
            "[Roleplay] Bob says, \"…\"", Scope.ChannelsOnly, @"^\[Roleplay\]|says on the Roleplay channel"),
        new("social.society", "Social", "Society channel",
            "Everything said on your society channel.",
            "[Society] Bob says, \"…\"", Scope.ChannelsOnly, @"^\[(?:Society|Celestial Hand|Eldrytch Web|Radiant Blood)\]|says on the (?:Society|Celestial Hand|Eldrytch Web|Radiant Blood) channel"),

        // ── NPCs ──────────────────────────────────────────────────────────
        new("npc.arbitrator_spam", "NPCs", "Master Arbitrator spam",
            "Selected Colosseum spam (title / key / arena instructions); keeps \"Well done!\" / \"Good Luck!\".",
            "Master Arbitrator tells you, \"You shall be known…\"", Scope.Any,
            @"(?:^Master Arbitrator tells you,.+(?:""You shall be known|""Take this knowledge|""Use the the key)|(?:\[Fellowship\] )?Master Arbitrator says,.+(?:""Your fellowship|""Use one of the|""Don't forget))"),
        new("npc.arbitrator", "NPCs", "Master Arbitrator (Colosseum)",
            "All Master Arbitrator speech and the fellowship-lock notice.",
            "Master Arbitrator tells you, \"You shall be known…\"", Scope.Any,
            @"^Master Arbitrator (?:says|tells you)|^Your fellowship is now locked\..*you have 15 minutes to be recruited"),

        // ── Pets (ACE custom combat pets) ─────────────────────────────────
        new("pet.hit", "Pets", "Pet hits",
            "\"[Pet] … hit …\" outgoing damage.",
            "[Pet] Fire Banshee hit Drudge for 40 damage.", Scope.NotPlayerChat, @"^\[Pet\] .+ hit "),
        new("pet.evade", "Pets", "Pet attacks evaded",
            "\"[Pet] … evaded by …\"",
            "[Pet] Fire Banshee's attack was evaded by Drudge.", Scope.NotPlayerChat, @"^\[Pet\] .+ evaded by "),
        new("pet.damage_taken", "Pets", "Pet damage taken",
            "\"[Pet] … took …\" incoming damage.",
            "[Pet] Fire Banshee took 25 damage from Drudge.", Scope.NotPlayerChat, @"^\[Pet\] .+ took "),
        new("pet.death", "Pets", "Pet died",
            "\"[Pet] Your combat pet … has died.\"",
            "[Pet] Your combat pet Fire Banshee has died.", Scope.NotPlayerChat, @"^\[Pet\] Your combat pet .+ has died\.\s*$"),
        new("pet.recall_block", "Pets", "Pet recall blocked",
            "\"Your pet was in combat recently; you cannot recall it…\"",
            "Your pet was in combat recently; you cannot recall it for about 10 seconds.", Scope.NotPlayerChat,
            @"^Your pet was in combat recently; you cannot recall it"),
        new("pet.overflow", "Pets", "Pet heal overflow",
            "\"… receives N excess … from …\" boost overflow.",
            "Fire Banshee receives 20 excess health from Heal Other.", Scope.NotPlayerChat, @" receives .+ excess .+ from "),
    };

    private static readonly Dictionary<string, Preset> _byId =
        _all.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every preset in display order.</summary>
    internal static IReadOnlyList<Preset> All => _all;

    /// <summary>Group names in display order.</summary>
    internal static IEnumerable<string> Groups => _all.Select(p => p.Group).Distinct();

    internal static Preset? Get(string id) => _byId.TryGetValue(id, out var p) ? p : null;

    /// <summary>First enabled preset that matches the line, or null.</summary>
    internal static Preset? FirstMatch(string text, string channel)
    {
        foreach (var p in _all)
            if (p.Enabled && p.Matches(text, channel))
                return p;
        return null;
    }

    /// <summary>Tabs named by enabled presets (tab names create tabs implicitly, like custom rules).</summary>
    internal static IEnumerable<string> TargetTabs()
        => _all.Where(p => p.Enabled && p.Tab.Length > 0).Select(p => p.Tab);
}
