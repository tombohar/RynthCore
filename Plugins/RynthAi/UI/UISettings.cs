using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Newtonsoft.Json;

namespace NexSuite.Plugins.RynthAi
{
    public class UISettings
    {
        // Core macro state
        public bool IsMacroRunning = false;
        public string CurrentState = "Default";
        public bool IsRecordingNav = false;

        // Subsystem Toggles
        public bool EnableBuffing = true;
        public bool EnableCombat = false;
        public bool EnableNavigation = false;
        public bool EnableLooting = false;
        public bool EnableMeta = false;
        public bool EnableRaycasting = true;

        // Movement Mode: 0 = Legacy (SetAutorun/SetClientMotion), 
        //                1 = Tier1 (CM_Movement direct calls),
        //                2 = Tier2 (CPhysicsObj::MoveToPosition — planned),
        //                3 = Tier3 (MoveToManager queue — planned)
        public int MovementMode = 0;

        // ── Navigation Steering (all modes) ───────────────────────────────
        public float NavStopTurnAngle = 20f;    // Stop and turn in place when error > X°
        public float NavResumeTurnAngle = 10f;  // Resume running when error < X° (must be < StopTurn)
        public float NavDeadZone = 4f;          // Ignore corrections smaller than X°
        public float NavSweepMult = 2.5f;       // Closest-approach detect radius = ArrivalYards * this

        // ── Tier 2 Tuning ─────────────────────────────────────────────────
        public float T2Speed = 1.0f;            // Movement speed multiplier
        public float T2DistanceTo = 0.5f;       // Stop this far from target (yards)
        public float T2ReissueMs = 2000f;       // Re-issue MoveToPosition if no progress (ms)
        public float T2MaxRangeYd = 500f;       // Clamp waypoint distance to this (yards)
        public int   T2MaxLandblocks = 3;       // Skip if target is more than N landblocks away
        public float T2WalkWithinYd = 5f;       // Walk (not run) when within X yards of waypoint

        // Persistence
        public string CurrentNavPath = "";
        public string CurrentLootPath = "";
        public string CurrentMetaPath = "";


        // Profile Selection Indices
        public int MacroSettingsIdx = 1;
        public int NavProfileIdx = 1;
        public int LootProfileIdx = 0;
        public int MetaProfileIdx = 1;

        // Automation Toggles
        public bool EnableAutostack = true;
        public bool EnableAutocram = true;
        public bool EnableCombineSalvage = true;  // If true, combine salvage bags of same material

        // --- Missile Crafting ---
        public bool EnableMissileCrafting = false;     // Master toggle for auto-crafting ammo from bundles
        public int MissileCraftAmmoThreshold = 1000;   // Craft when ammo count drops below this

        // --- Loot Timing (ms) — tunable in Advanced Settings → Looting ---
        public int LootInterItemDelayMs = 100;      // Delay between picking up items
        public int LootContentSettleMs = 100;        // Wait for corpse contents to populate
        public int LootEmptyCorpseMs = 400;          // Wait before declaring corpse empty
        public int LootClosingDelayMs = 200;          // Delay before closing corpse
        public int LootAssessWindowMs = 800;          // Max time to wait for item ID data
        public int LootRetryTimeoutMs = 800;          // Retry pickup after this long
        public int LootOpenRetryMs = 3000;             // Retry opening corpse if no response

        // --- Salvage Timing (ms) — first item / subsequent item ---
        public int SalvageOpenDelayFirstMs = 400;    // Panel open delay (first item)
        public int SalvageOpenDelayFastMs = 50;      // Panel open delay (subsequent)
        public int SalvageAddDelayFirstMs = 600;     // Add item delay (first)
        public int SalvageAddDelayFastMs = 50;       // Add item delay (subsequent)
        public int SalvageSalvageDelayMs = 50;       // Click salvage button delay
        public int SalvageResultDelayFirstMs = 1000; // Wait for result (first)
        public int SalvageResultDelayFastMs = 250;   // Wait for result (subsequent)
        public bool UseDispelItems = false;
        public bool CastDispelSelf = false;
        public bool AutoFellowMgmt = false;
        public bool MChargesWhenOff = false;

        // Vitals (0-100)
        public int HealAt = 60;
        public int RestamAt = 30;
        public int GetManaAt = 40;
        public int TopOffHP = 95;
        public int TopOffStam = 95;
        public int TopOffMana = 95;
        public int HealOthersAt = 50;
        public int RestamOthersAt = 10;
        public int InfuseOthersAt = 10;

        // Ranges & Misc
        public int MonsterRange = 50;
        public int RingRange = 5;
        public int ApproachRange = 4;
        public int MinRingTargets = 4;  // Minimum monsters in ring range to use ring spells
        public float FollowNavMin = 1.5f;
        public double MaxMonRange = 12.0;
        public bool SummonPets = false;
        public int CustomPetRange = 5;
        public int PetMinMonsters = 1;
        public bool AdvancedOptions = false;
        public bool MineOnly = true;
        public bool ShowEditor = false;
        public double CorpseApproachRangeMax = 0.04166667; // ~10 yards (10 / 240)
        public double CorpseApproachRangeMin = 0.00833333; // ~2 yards (2 / 240)

        // Advanced Toggles
        public bool BoostNavPriority = false;
        public bool BoostLootPriority = false;
        // Loot Ownership: 0=MyKillsOnly (default), 1=FellowshipKills, 2=AllCorpses
        public int LootOwnership = 0;
        public bool LootOnlyRareCorpses = false;
        public bool PeaceModeWhenIdle = true;
        public bool RebuffWhenIdle = false;

        // Blacklist Settings (Combat)
        public int BlacklistAttempts = 3;        // How many failed attacks before blacklisting
        public int BlacklistTimeoutSec = 30;     // How long a mob stays blacklisted (seconds)

        // Attack Power (0-100 percent, or -1 for auto)
        // Auto: 100% power, or 80% if UseRecklessness is enabled and Recklessness is trained
        public int MeleeAttackPower = -1;        // -1 = auto, 0-100 = manual percent
        public int MissileAttackPower = -1;      // -1 = auto, 0-100 = manual percent
        public bool UseRecklessness = false;     // When true + Recklessness trained, auto power = 80%

        // Attack Height: 0=Low (Delete), 1=Medium (End), 2=High (Page Down)
        public int MeleeAttackHeight = 1;        // Default: Medium
        public int MissileAttackHeight = 1;      // Default: Medium

        //FPS Limits
        public bool EnableFPSLimit = true;
        public int TargetFPSFocused = 60;
        public int TargetFPSBackground = 30;

        // Data Lists
        public List<MonsterRule> MonsterRules = new List<MonsterRule>();
        public List<ItemRule> ItemRules = new List<ItemRule>();
        public List<BuffRule> BuffRules = new List<BuffRule>();
        public List<MetaRule> MetaRules = new List<MetaRule>(); // <--- NEW META LIST
        public string LuaScript = "-- Enter your Lua script here\nprint('RynthAi Lua Loaded')";
        public string LuaConsoleOutput = "--- RynthAi Lua Console ---";

        public string SelectedProfile = "Default";
        public VTankNavParser CurrentRoute = new VTankNavParser(); // Uncomment if you still use this
        public int ActiveNavIndex = 0;

        // Advanced Settings Popup
        // Inside UISettings.cs
        public bool ShowAdvancedWindow = false; // Toggle this with your button
        public int SelectedAdvancedTab = 0;    // Tracks the left-hand selection

        // Define your tabs in an array for easy rendering
        [JsonIgnore]
        public readonly string[] AdvancedTabs = {
            "Misc", "Recharge", "Melee Combat", "Spell Combat",
            "Ranges", "Navigation", "Buffing", "Crafting", "Looting"
};

        //Refresh state when selected
        [JsonIgnore]
        public bool ForceStateReset { get; set; } = false;

        public UISettings()
        {
            // REMOVED EnsureDefaultRule() from here to stop JSON deserialization from duplicating it.
        }

        public void EnsureDefaultRule()
        {
            // Find any existing Default rules
            var existingDefaults = MonsterRules.Where(m => m.Name.Equals("Default", StringComparison.OrdinalIgnoreCase)).ToList();
            MonsterRule trueDefault;

            if (existingDefaults.Count > 0)
            {
                // Keep the first one (it contains your saved weapon settings)
                trueDefault = existingDefaults.First();
                // Erase all of them from the list to clean up any duplicates
                MonsterRules.RemoveAll(m => m.Name.Equals("Default", StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                // If it truly doesn't exist, make a brand new one
                trueDefault = new MonsterRule { Name = "Default", Priority = 1, DamageType = "Auto", WeaponId = 0 };
            }

            // Insert the one true Default safely at the very top
            MonsterRules.Insert(0, trueDefault);
        }
    }

    public class MonsterRule
    {
        public string Name { get; set; } = "New Monster";
        public int Priority { get; set; } = 1;
        public string DamageType { get; set; } = "Auto";
        public int WeaponId { get; set; } = 0;

        // Debuff Lights (cast on target before attacking)
        public bool Fester { get; set; } = false;        // F - Festering Curse
        public bool Broadside { get; set; } = false;     // B - Broadside of a Barn
        public bool GravityWell { get; set; } = false;   // G - Gravity Well
        public bool Imperil { get; set; } = false;       // I - Imperil
        public bool Yield { get; set; } = false;         // Y - Yield
        public bool Vuln { get; set; } = false;          // V - Vulnerability (element-matched)

        // Spell Type Lights (how to attack)
        public bool UseArc { get; set; } = false;        // A - Arc spells (war/void)
        public bool UseRing { get; set; } = false;       // R - Ring spells (uses RingRange)
        public bool UseStreak { get; set; } = false;     // S - Streak spells (war/void)
        public bool UseBolt { get; set; } = true;        // B - Bolt spells (default on)
        // If none of A/R/S/B are set in magic mode = don't attack (buff bot)

        // Extra Vulnerability element (cast in addition to the V light)
        public string ExVuln { get; set; } = "None";
        // Offhand weapon/shield ID (from ItemRules, for dual wield or shield)
        public int OffhandId { get; set; } = 0;
        // Pet damage element
        public string PetDamage { get; set; } = "PAuto";
    }

    public class BuffRule { public string SpellName { get; set; } public int SpellId { get; set; } public bool Enabled { get; set; } = true; }

    public class ItemRule
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Action { get; set; } = "Loot";
        public string Element { get; set; } = "Slash";
        public bool KeepBuffed { get; set; } = true;
    }

    // ====================================================================
    // NEW META SYSTEM MODELS
    // ====================================================================
    public enum MetaConditionType
    {
        Never, 
        Always, 
        All, 
        Any, 
        ChatMessage, 
        PackSlots_LE,
        SecondsInState_GE, 
        CharacterDeath, 
        AnyVendorOpen,
        VendorClosed, 
        InventoryItemCount_LE, 
        InventoryItemCount_GE,
        MonsterNameCountWithinDistance, 
        MonsterPriorityCountWithinDistance,
        NeedToBuff, 
        NoMonstersWithinDistance, 
        Landblock_EQ,
        Landcell_EQ, 
        PortalspaceEntered, 
        PortalspaceExited, 
        Not,
        SecondsInStateP_GE, 
        TimeLeftOnSpell_GE, 
        TimeLeftOnSpell_LE,
        BurdenPercentage_GE, 
        DistAnyRoutePT_GE, 
        Expression,
        ChatMessageCapture, 
        NavrouteEmpty,
        MainHealthLE,
        MainHealthPHE,
        MainManaLE,
        MainManaPHE,
        MainStamLE
    }

    public enum MetaActionType
    {
        None,
        ChatCommand,
        SetMetaState,
        EmbeddedNavRoute,
        All,
        CallMetaState,      // New
        ReturnFromCall,     // New
        ExpressionAction,   // New
        ChatExpression,     // New
        SetWatchdog,        // New
        ClearWatchdog,      // New
        GetNTOption,        // New
        SetNTOption,        // New
        CreateView,         // New
        DestroyView,        // New
        DestroyAllViews     // New
    }

    public class MetaRule
    {
        public string State { get; set; } = "Default";
        public MetaConditionType Condition { get; set; }
        public string ConditionData { get; set; } = "";
        public MetaActionType Action { get; set; }
        public string ActionData { get; set; } = "";
        public List<MetaRule> Children { get; set; } = new List<MetaRule>();

        // NEW: Temporary flag for the UI. JsonIgnore prevents it from saving to the file.
        [JsonIgnore]
        public bool HasFired { get; set; } = false;
    }
}