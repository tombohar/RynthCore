// ============================================================================
//  RynthCore.Engine - UI/Data/SettingsData.cs
//  RynthAi's Advanced Settings (docs/IMGUI_PARITY_PLAN.md §2.4): the payload,
//  its hub source and save command, and the ONE schema both faces draw from
//  (UI/Panels/SettingsPanel.cs, ImGui/Panels/SettingsFace.cs).
//
//  To add a setting: add the property to RynthAiSettings (it must match
//  RynthAi's SettingsBridgePayload) and a row to SettingsSchema.Tabs. Both
//  faces pick it up. The payload always carries every property, so a save
//  can't send a field missing (RynthAi applied missing fields as 0, 2026-09-27).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RynthCore.Engine.UI.Data;

/// <summary>Mirrors RynthAi's SettingsBridgePayload (RynthPluginGet/SetSettingsJson).</summary>
internal sealed class RynthAiSettings
{
    // Display
    public bool ShowTargetStaminaMana { get; set; }
    // UI
    public bool SuppressRetailRadar { get; set; }
    // No rows (RynthAi's plugin-drawn radar/chat windows are gone); kept so the payload
    // still carries them - a field missing from a save is applied as false.
    public bool ShowRynthRadar { get; set; }
    public bool RadarClickThrough { get; set; }
    // Retail-chatbox suppression is owned by the RynthChat plugin ("Hide retail chat"
    // in its gear menu); routing it through RynthAi's settings push fought that toggle.
    public bool ShowRynthChat { get; set; }
    public bool ChatClickThrough { get; set; }
    public bool SuppressRetailPowerbar { get; set; }
    // Misc
    public bool EnableFPSLimit { get; set; }
    public int TargetFPSFocused { get; set; } = 60;
    public int TargetFPSBackground { get; set; } = 30;
    public bool EnableAutocram { get; set; }
    public bool PeaceModeWhenIdle { get; set; }
    // RynthAi's Weapons Back After Heals. Nullable: an older RynthAi doesn't send it, and the
    // engine then sends none back (a false would turn it off).
    public bool? RearmAfterVitals { get; set; }
    public bool StartMacroOnLogin { get; set; }
    public bool PatrolOnLogin { get; set; }
    // RynthAi's Escape Acid and Lava. Default on; an older RynthAi doesn't send it, and a missing
    // field must not read as "off".
    public bool EscapeHazards { get; set; } = true;
    // Decal bridge: stop RynthAi's macro when VTank's starts (one bot per client). Default on;
    // an older RynthAi doesn't send it, and a missing field must not read as "off".
    public bool YieldToVTank { get; set; } = true;
    public bool EnableRaycasting { get; set; }
    public bool UseArcs { get; set; }
    public float BowArcVelocity { get; set; } = 25f;
    public float MissileArcClearance { get; set; } = 0.5f;
    // Vendoring (RynthAi AutoVendor; defaults match LegacyUiSettings)
    public bool AutoVendorEnabled { get; set; }
    public bool AutoVendorEnableBuying { get; set; } = true;
    public bool AutoVendorEnableSelling { get; set; } = true;
    public bool AutoVendorTestMode { get; set; } = true;
    public bool AutoVendorThink { get; set; }
    public bool AutoVendorShowMerchantInfo { get; set; } = true;
    public bool AutoVendorOnlyFromMainPack { get; set; }
    public int AutoVendorTries { get; set; } = 4;
    public int AutoVendorTriesTime { get; set; } = 5000;
    public bool LosDebugLog { get; set; }
    public float CrossbowArcVelocity { get; set; } = 40f;
    public float AtlatlArcVelocity { get; set; } = 22f;
    // Arc spells' horizontal speed: ACE flies every player arc at 40 m/s across the ground.
    public float MagicArcVelocity { get; set; } = 40f;
    public int BlacklistAttempts { get; set; } = 3;
    public int BlacklistTimeoutSec { get; set; } = 30;
    // Not shown in the panel, but carried so a save doesn't send them missing.
    public int BlacklistCastSettleMs { get; set; } = 1500;
    public int MonsterDisengageRange { get; set; }
    // Ranges > Combat Targeting. Null until RynthAi sends them; an older RynthAi leaves them alone.
    public int? TargetSelection { get; set; }
    public bool? TargetLock { get; set; }
    public int TargetNoProgressTimeoutSec { get; set; }
    public int GiveQueueIntervalMs { get; set; } = 150;
    // Recharge
    public int HealAt { get; set; } = 60;
    public int RestamAt { get; set; } = 30;
    // Once a recharge starts under Re-stam At, recharge to this % (0 = one recharge; an older RynthAi doesn't send it).
    public int RestamTo { get; set; } = 0;
    public int GetManaAt { get; set; } = 40;
    public int TopOffHP { get; set; } = 95;
    public int TopOffStam { get; set; } = 95;
    public int TopOffMana { get; set; } = 95;
    public int HealOthersAt { get; set; } = 50;
    public int RestamOthersAt { get; set; } = 10;
    public int InfuseOthersAt { get; set; } = 10;
    // Kits, potions and safety stops
    public bool UsePotions { get; set; } = true;
    public bool UseBuffItems { get; set; } = true;
    public int MakeRationsBelow { get; set; } = 5;
    public bool UseKitsInMagicMode { get; set; } = true;
    public bool PeaceModeForKits { get; set; }
    public int KitMinSuccessPct { get; set; } = 70;
    public int EmergencyHealAt { get; set; } = 30;
    public int StaminaToHealthAt { get; set; } = 30;
    public int StaminaToHealthMinStamina { get; set; } = 20;
    public bool StopMacroOnDeath { get; set; }
    // RynthAi Recall On Death: 0 off, 1 cast DeathRecallSpellId, 2 use the item named DeathRecallItem.
    // An older RynthAi ignores them.
    public int DeathRecallMode { get; set; }
    public int DeathRecallSpellId { get; set; } = 48;   // Primary Portal Recall
    public string DeathRecallItem { get; set; } = string.Empty;
    public bool StopMacroOnNoComponents { get; set; }
    // Recall When Low On Components (RynthAi's RynthAiPlugin.LowComponents.cs). An older RynthAi ignores them.
    public bool LowCompsRecall { get; set; }
    public int LowCompsTaperMin { get; set; } = 10;
    public int LowCompsScarabMin { get; set; } = 2;
    public bool StopLootingWhenPackFull { get; set; } = true;
    public bool StopMacroWhenPackFull { get; set; }
    // Melee Combat
    public bool UseRecklessness { get; set; }
    public int MeleeAttackPower { get; set; } = -1;
    public int MeleeAttackHeight { get; set; } = 1;
    public int MissileAttackPower { get; set; } = -1;
    public int MissileAttackHeight { get; set; } = 1;
    public bool UseNativeAttack { get; set; } = true;
    public bool SummonPets { get; set; }
    public int PetMinMonsters { get; set; } = 1;
    // RynthAi's Variable attack power, environment-hit throttle and pull-away kite (2026-10-12 and
    // 2026-10-16; Silentkelpie's). Mirrors of RynthAi's own settings, nullable like the Vaulted
    // switch below: a RynthAi that predates them omits them, the rows stay hidden, and a save
    // sends null, which RynthAi leaves alone. Their defaults here are only what a row shows for a
    // value that arrived.
    public int? VariablePowerMin { get; set; }
    public int? VariablePowerMax { get; set; }
    public int? MissileEnvironmentResponse { get; set; }
    public int? MagicEnvironmentResponse { get; set; }
    public int? EnvironmentImpactLimit { get; set; }
    public int? EnvironmentImpactWindowSec { get; set; }
    public int? EnvironmentApproachMeters { get; set; }
    public int? EnvironmentMinDistance { get; set; }
    public int? EnvironmentSkipSec { get; set; }
    public int? KitePullMeters { get; set; }
    public bool? KiteTagFirst { get; set; }
    // Spell Combat
    public int SpellCastIntervalMs { get; set; } = 400;
    public int AttackSpellIntervalMs { get; set; } = 1500;
    public bool CastDispelSelf { get; set; }
    public int MinRingTargets { get; set; } = 4;
    public int BlastRange { get; set; }
    public int MinBlastTargets { get; set; } = 3;
    // RynthAi's wall override (2026-10-08). Nullable in RynthAi's payload: an engine without these
    // rows leaves its values alone.
    public int WallRange { get; set; }
    public int MinWallTargets { get; set; } = 3;
    public int MinSkillLevelTier1 { get; set; } = 35;
    public int MinSkillLevelTier2 { get; set; } = 85;
    public int MinSkillLevelTier3 { get; set; } = 135;
    public int MinSkillLevelTier4 { get; set; } = 185;
    public int MinSkillLevelTier5 { get; set; } = 235;
    public int MinSkillLevelTier6 { get; set; } = 285;
    public int MinSkillLevelTier7 { get; set; } = 335;
    public int MinSkillLevelTier8 { get; set; } = 435;
    // Ranges
    public int MonsterRange { get; set; } = 50;
    public int RingRange { get; set; } = 5;
    public int ApproachRange { get; set; } = 4;
    public double CorpseApproachRangeMax { get; set; } = 10.0;
    public double CorpseApproachRangeMin { get; set; } = 2.0;
    // Navigation
    public bool BoostNavPriority { get; set; }
    /// <summary>
    /// RynthAi's "Vaulted Dungeons" setting: the master switch of RynthILT's Vaulted Dungeons keeper
    /// (Infinite Leaftide / ACECustom only), off by default. It lives in the RynthAi profile (this is
    /// only a mirror, so there is exactly one copy); RynthILT reads it from RynthAi. Nullable like
    /// the Nav panel's switches: a RynthAi that predates it omits it, the row then stays hidden and a
    /// save sends null, which RynthAi leaves alone.
    /// </summary>
    public bool? EnableVaultedDungeons { get; set; }
    public float FollowNavMin { get; set; } = 1.5f;
    public float NavRingThickness { get; set; } = 6f;
    public float NavLineThickness { get; set; } = 6f;
    public float NavHeightOffset { get; set; } = 0.05f;
    public float NavSlopeSink { get; set; } = 1.5f;
    public bool ShowTerrainPassability { get; set; } = true;
    // Nav3D extras (RynthAi NavOverlay), both off by default: 0 off, 1 numbers, 2 numbers and names.
    public int NavLabelMode { get; set; }
    public bool NavGuideLine { get; set; }
    // RynthAi NavResumeNearest: macro start, route loads and resuming after a pause that moved
    // the character head for the closest waypoint. On by default; an older plugin ignores it.
    public bool NavResumeNearest { get; set; } = true;
    // RynthAi NavOverlay.TrackBreadcrumbs / ShowRouteMarkers: the Nav panel's Breadcrumbs and Route
    // overlay switches, also on Settings > Navigation. Null = a RynthAi that doesn't send them: the
    // rows hide, and a save sends null back, which RynthAi reads as unchanged.
    public bool? TrackBreadcrumbs { get; set; }
    public bool? ShowRouteOverlay { get; set; }
    // RynthAi dungeon patrol (/ra dunnav-patrol; RynthAi DungeonPatrolOptions). The defaults are
    // the patrol as it was before these settings; an older plugin ignores them.
    public int DunPatrolCoverage { get; set; }               // 0 whole dungeon, 1 loops only, 2 everything, 3 sweep
    public int DunPatrolSkipDeadEnds { get; set; } = 10;     // dead-end areas of up to this many cells skipped (0 = none)
    public bool DunPatrolAvoidFloors { get; set; } = true;   // lava/acid floor cells kept out of the patrol
    public float DunPatrolWallDistance { get; set; } = 1f;   // every patrol point this far from walls where there is room
    public float DunPatrolLookInSize { get; set; } = 10f;    // rooms this big the patrol only skirts get a walk to their middle
    public float DunPatrolSightYd { get; set; } = 20f;       // sweep: how far a monster notices you (lane spacing)
    public bool OpenDoors { get; set; }
    public float OpenDoorRange { get; set; } = 5f;
    // RynthAi OpenDoorsOnPathOnly: Open Doors opens only a closed door on the path of travel. An older plugin ignores it.
    public bool OpenDoorsOnPathOnly { get; set; }
    public bool AutoUnlockDoors { get; set; }
    public int MovementMode { get; set; }
    public float NavStopTurnAngle { get; set; } = 20f;
    public float NavResumeTurnAngle { get; set; } = 10f;
    public float NavDeadZone { get; set; } = 4f;
    public float NavSweepMult { get; set; } = 2.5f;
    public float NavLookaheadYards { get; set; } = 4f;
    public float NavShortcutYards { get; set; } = 1f;
    public float NavTurnRateDegPerSec { get; set; } = 270f;
    public float NavTier1TurnSpeed { get; set; } = 3f;
    public float PostPortalDelaySec { get; set; } = 4f;
    public float T2Speed { get; set; } = 1f;
    public float T2WalkWithinYd { get; set; } = 5f;
    public float T2DistanceTo { get; set; } = 0.5f;
    public float T2ReissueMs { get; set; } = 2000f;
    public float T2MaxRangeYd { get; set; } = 500f;
    public int T2MaxLandblocks { get; set; } = 3;
    // Buffing
    public bool EnableBuffing { get; set; } = true;
    public bool RebuffWhenIdle { get; set; }
    public int RebuffSecondsRemaining { get; set; } = 300;
    public int RebuffTopOffSecondsRemaining { get; set; } = 1200;
    // Cast our own buff even when gear already gives it (servers with buff augments). Default
    // on; a RynthAi that predates it doesn't send it, and must not read as off.
    public bool CastBuffsOverItemBuffs { get; set; } = true;
    // RynthAi's unticked prots and banes (buff base names). Null = never received from RynthAi:
    // sent back as null, which RynthAi reads as "unchanged".
    public System.Collections.Generic.List<string>? DisabledBuffs { get; set; }
    public int BuffMinSkillLevelTier1 { get; set; } = 35;
    public int BuffMinSkillLevelTier2 { get; set; } = 85;
    public int BuffMinSkillLevelTier3 { get; set; } = 135;
    public int BuffMinSkillLevelTier4 { get; set; } = 185;
    public int BuffMinSkillLevelTier5 { get; set; } = 235;
    public int BuffMinSkillLevelTier6 { get; set; } = 285;
    public int BuffMinSkillLevelTier7 { get; set; } = 335;
    public int BuffMinSkillLevelTier8 { get; set; } = 435;
    // Crafting
    public bool EnableMissileCrafting { get; set; } = true;
    public string MissileCraftingState { get; set; } = string.Empty;
    public bool MissileCraftingActive { get; set; }
    public string MissileCraftingStatus { get; set; } = string.Empty;
    // Looting
    public bool EnableLooting { get; set; }
    public bool BoostLootPriority { get; set; }
    public bool LootOnlyRareCorpses { get; set; }
    public bool LootJumpEnabled { get; set; }
    public int LootJumpHeight { get; set; } = 10;
    public int LootOwnership { get; set; }
    // Recover your own death corpse whole (default on), and travel back to it after a death
    // (default off). A RynthAi that predates them doesn't send them; on must not read as off.
    public bool LootOwnCorpse { get; set; } = true;
    public bool TravelToOwnCorpse { get; set; }
    // Selecting an item prints /ra iteminfo (default on; an older RynthAi doesn't send it).
    public bool ItemInfoOnSelect { get; set; } = true;
    public bool EnableAutostack { get; set; } = true;
    public bool ReadUnknownScrolls { get; set; } = true;   // VTank's default
    public bool EnableCombineSalvage { get; set; } = true;
    public bool CombineBagsDuringSalvage { get; set; } = true;
    // Salvage with the 0x027D request sent straight to the server, no salvage panel on screen
    // (default on; used when the engine has API v81; an older RynthAi doesn't send it).
    public bool SalvageWithoutPanel { get; set; } = true;
    public int LootInterItemDelayMs { get; set; } = 50;
    public int LootContentSettleMs { get; set; } = 100;
    public int LootEmptyCorpseMs { get; set; } = 300;
    public int LootClosingDelayMs { get; set; } = 200;
    public int LootAssessWindowMs { get; set; } = 200;
    public int LootRetryTimeoutMs { get; set; } = 500;
    public int LootOpenRetryMs { get; set; } = 1500;
    public int LootCorpseTimeoutMs { get; set; } = 12000;
    public int SalvageOpenDelayFirstMs { get; set; } = 400;
    public int SalvageOpenDelayFastMs { get; set; } = 50;
    public int SalvageAddDelayFirstMs { get; set; } = 600;
    public int SalvageAddDelayFastMs { get; set; } = 50;
    public int SalvageSalvageDelayMs { get; set; } = 50;
    public int SalvageResultDelayFirstMs { get; set; } = 1000;
    public int SalvageResultDelayFastMs { get; set; } = 250;
    // Ground loot and item info
    public bool EnableGroundLoot { get; set; }
    // Diagnostics (RynthAi's RynthLog: per PC in Logs\Diagnostics\diagnostics.json, not the profile)
    public bool DiagDebugToChat { get; set; }
    public bool DiagFileLogAll { get; set; } = true;
    /// <summary>"Name=Level" per trace category, ','-joined; Level 0 Off, 1 Trace, 2 Info. See <see cref="TraceCategoryLevels"/>.</summary>
    public string DiagCategories { get; set; } = string.Empty;
    /// <summary>Read-only (RynthAi ignores it on save): the diagnostics folder.</summary>
    public string DiagFolder { get; set; } = string.Empty;

    /// <summary>A private copy for a face to edit (all members are values or strings).</summary>
    // VTank's options page (the Classic dashboard's Main tab): RynthAi stores them with the profile and metas
    // read them (UseDispelItems, AutoFellowMgmt); CustomPetRange is the pet manager's range. Nullable, like
    // EnableVaultedDungeons: a RynthAi that predates them (before 2026-10-19) omits them, their rows stay
    // hidden, and a save sends null, which RynthAi leaves alone.
    public bool? UseDispelItems { get; set; }
    public bool? AutoFellowMgmt { get; set; }
    public bool? MChargesWhenOff { get; set; }
    public int? CustomPetRange { get; set; }

    public RynthAiSettings Clone() => (RynthAiSettings)MemberwiseClone();
}

[JsonSerializable(typeof(RynthAiSettings))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = false, IncludeFields = false)]
internal partial class RynthAiSettingsJsonContext : JsonSerializerContext { }

/// <summary>RynthPluginGetSettingsJson while a Settings face is open (5 s, and right after each save).</summary>
internal sealed unsafe class SettingsSource : UiSource<RynthAiSettings>
{
    private delegate* unmanaged[Cdecl]<IntPtr> _getSettingsJson;
    private string? _lastJson;

    public SettingsSource() : base("Settings", periodMs: 5000) { }

    protected internal override void Poll()
    {
        if (_getSettingsJson == null)
            _getSettingsJson = (delegate* unmanaged[Cdecl]<IntPtr>)PluginExportBinder.Resolve("RynthAi", "RynthPluginGetSettingsJson");
        if (_getSettingsJson == null) return;
        IntPtr ptr = _getSettingsJson();
        string? json = ptr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ptr);
        if (string.IsNullOrEmpty(json) || json == _lastJson) return;
        _lastJson = json;
        RynthAiSettings? parsed = JsonSerializer.Deserialize(json, RynthAiSettingsJsonContext.Default.RynthAiSettings);
        if (parsed != null) Publish(parsed);
    }

    protected internal override void Reset()
    {
        _getSettingsJson = null;
        _lastJson = null;
        ClearSnapshot();
    }
}

/// <summary>Saves the whole settings payload through RynthPluginSetSettingsJson, on the pump thread.</summary>
internal static unsafe class SettingsCommands
{
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _setSettingsJson;

    static SettingsCommands()
    {
        Plugins.PluginManager.PluginsUnloaded += () => _setSettingsJson = null;
    }

    /// <summary>
    /// <paramref name="settings"/> must be a copy the caller no longer changes
    /// (it is serialized later, on the pump thread). Any thread.
    /// </summary>
    public static void Save(RynthAiSettings settings)
    {
        UiDataHub.Post("RynthAi settings save", () =>
        {
            if (_setSettingsJson == null)
                _setSettingsJson = (delegate* unmanaged[Cdecl]<IntPtr, void>)PluginExportBinder.Resolve("RynthAi", "RynthPluginSetSettingsJson");
            if (_setSettingsJson == null) return;
            string json = JsonSerializer.Serialize(settings, RynthAiSettingsJsonContext.Default.RynthAiSettings);
            IntPtr ansi = Marshal.StringToHGlobalAnsi(json);
            try { _setSettingsJson(ansi); }
            finally { Marshal.FreeHGlobal(ansi); }
            UiSources.Settings.RequestRefresh();
            UiSources.Nav.RequestRefresh();   // the Nav panel's Breadcrumbs / Route overlay switches
        });
    }
}

/// <summary>
/// The optimized loot timer defaults, in ms: the same values as RynthSuite's LootBaseline.cs
/// (RynthAi), measured from live corpse looting on ACE (226 corpses, 39 pickups; from
/// Silentkelpie's PR 6). Keep the two in step. The "Use optimized defaults" button on Settings
/// writes them; the properties of <see cref="RynthAiSettings"/> keep their own initial values.
/// </summary>
internal static class LootTimerDefaults
{
    public const int ContentSettleMs = 100;
    public const int EmptyCorpseMs = 300;
    /// <summary>Above the 256 ms 95th percentile of the measured appraisal replies.</summary>
    public const int AssessWindowMs = 300;
    /// <summary>No pickup needed a retry at 1000 ms (it counts from when the character is still).</summary>
    public const int RetryTimeoutMs = 1000;
    public const int OpenRetryMs = 1500;
    public const int CorpseTimeoutMs = 12000;

    /// <summary>Writes the six optimized defaults into <paramref name="s"/>.</summary>
    public static void Apply(RynthAiSettings s)
    {
        s.LootContentSettleMs = ContentSettleMs;
        s.LootEmptyCorpseMs = EmptyCorpseMs;
        s.LootAssessWindowMs = AssessWindowMs;
        s.LootRetryTimeoutMs = RetryTimeoutMs;
        s.LootOpenRetryMs = OpenRetryMs;
        s.LootCorpseTimeoutMs = CorpseTimeoutMs;
    }
}

// ── Schema ─────────────────────────────────────────────────────────────────

internal enum SettingKind { Bool, Int, Float, Double, Combo, Section, Spacer, Note, CraftingStatus, Button, TraceCategories, Text, Status,
    /// <summary>The user hotkeys editor (ImGui/Panels/HotkeysSettingsUi.cs; stored by the engine, not RynthAi).</summary>
    Hotkeys,
    /// <summary>A colour (#AARRGGBB in Get/Set): a swatch that opens a picker. The theme's colours (engine-stored).</summary>
    Color,
    /// <summary>
    /// The server picker and the server list (Diagnostics): which server this character counts
    /// as, from the servers the engine and the loaded plugins know (UI/Sections/ServerSelection.cs).
    /// Stored by the engine per character (server_force.txt), not RynthAi.
    /// </summary>
    ServerPick }

/// <summary>
/// Reads and edits <see cref="RynthAiSettings.DiagCategories"/> ("Name=Level,..."). The category
/// list comes from RynthAi, so a category it adds shows up without an engine change.
/// </summary>
internal static class TraceCategoryLevels
{
    public const int Off = 0, Trace = 1, Info = 2;

    /// <summary>The categories in RynthAi's order; malformed entries are skipped.</summary>
    public static List<(string Name, int Level)> Parse(string? levels)
    {
        var list = new List<(string, int)>();
        if (string.IsNullOrWhiteSpace(levels)) return list;
        foreach (string entry in levels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = entry.IndexOf('=');
            if (eq <= 0 || !int.TryParse(entry[(eq + 1)..], out int level)) continue;
            list.Add((entry[..eq], Math.Clamp(level, Off, Info)));
        }
        return list;
    }

    private static string Format(List<(string Name, int Level)> list)
    {
        var parts = new string[list.Count];
        for (int i = 0; i < list.Count; i++) parts[i] = list[i].Name + "=" + list[i].Level;
        return string.Join(",", parts);
    }

    /// <summary>
    /// Turns tracing on or off for one category (empty <paramref name="name"/> with a
    /// <paramref name="prefix"/>: every category starting with it; both empty: all). On keeps
    /// a category already at Info at Info, the same rule as /ra trace.
    /// </summary>
    public static string Set(string? levels, string name, bool on, string prefix = "")
    {
        List<(string Name, int Level)> list = Parse(levels);
        for (int i = 0; i < list.Count; i++)
        {
            var (n, level) = list[i];
            bool match = name.Length > 0
                ? string.Equals(n, name, StringComparison.OrdinalIgnoreCase)
                : n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            if (!match) continue;
            list[i] = (n, !on ? Off : level == Off ? Trace : level);
        }
        return Format(list);
    }

    /// <summary>Checkbox label: the name, plus " +log" at Info (trace lines also go to the normal log).</summary>
    public static string Label(string name, int level) => level == Info ? name + " +log" : name;
}

/// <summary>
/// One row of a settings tab. Numeric values go through double (bools as
/// 0/1, combos as the index). <see cref="Gates"/> marks rows whose value
/// shows or hides other rows (the Avalonia face rebuilds the tab on change).
/// </summary>
internal sealed record SettingRow(
    SettingKind Kind,
    string Label,
    string? Tooltip = null,
    Func<RynthAiSettings, double>? Get = null,
    Action<RynthAiSettings, double>? Set = null,
    double Min = 0,
    double Max = 0,
    double Step = 1,
    string[]? Items = null,
    Func<RynthAiSettings, bool>? VisibleWhen = null,
    bool Gates = false,
    // Button rows: the button's caption (Label is the row label) and its action. Any thread.
    string? ButtonText = null,
    Action? Click = null,
    // Text rows: the string value (committed on Enter / focus loss); Status rows: GetText only.
    Func<RynthAiSettings, string>? GetText = null,
    Action<RynthAiSettings, string>? SetText = null,
    // Text rows: greyed hint shown while the box is empty.
    string? Hint = null,
    // Button rows that change settings: applied to the panel's copy, which is then saved like
    // any other edit (Click, when also set, runs after).
    Action<RynthAiSettings>? Edit = null,
    // Combo rows whose list changes while the game runs (the Theme picker's saved schemes):
    // asked each time the row draws, in place of Items.
    Func<string[]>? ItemsNow = null)
{
    public bool IsVisible(RynthAiSettings s) => VisibleWhen == null || VisibleWhen(s);

    /// <summary>A Combo row's list now: ItemsNow's when it has one, else Items.</summary>
    public string[] CurrentItems => ItemsNow?.Invoke() ?? Items ?? Array.Empty<string>();
}

internal sealed record SettingsTab(string Name, SettingRow[] Rows);

internal static class SettingsSchema
{
    // Also drawn on the Nav panel (ImGui/Panels/NavFace.cs).
    public const string NavPointReachLabel = "Nav point reach (yd)";
    private static readonly string[] DashboardLooks = { "Modern (compact)", "Classic (retail)" };
    /// <summary>The Navigation row of RynthAi's Vaulted Dungeons master switch (<see cref="RynthAiSettings.EnableVaultedDungeons"/>).</summary>
    public const string VaultedDungeonsLabel = "Vaulted Dungeons";
    public const string NavPointReachTip =
        "How close to get to each nav point before moving on to the next, in yards. VTank calls it " +
        "Follow/Nav Min Distance (metas: FollowNavMin, or VTank's NavCloseStopRange). Follow stops this " +
        "close to its leader, and the nav marker ring shows it. Dungeon doorways use at most 1 yd. Up to 2 yd, " +
        "nav walks right up to each point before turning (tight dungeons); bigger values round corners.";

    // The Nav panel's two overlay switches (ImGui/Panels/NavFace.cs), also on Settings > Navigation.
    public const string BreadcrumbsTip =
        "Record the trail you walk and draw it on the ground (/ra navtrail on|off).\n" +
        "Turning it off hides the trail; /ra navtrail clear wipes it.";
    public const string RouteOverlayTip =
        "Draw the route in the world: waypoint rings and lines, waypoint labels and the guide line\n" +
        "to the active waypoint (/ra navoverlay on|off). The waypoint HUD window is separate.";
    private const string SameAsNavPanel = "\nThe same switch as on the Nav panel.";

    // Also shown by the dashboard's Ranges slide-out (ImGui/Panels/RangesSlideOut.cs), which
    // edits these same rows (found by label with Find).
    public const string MonsterRangeTip =
        "How far away a monster can be for the bot to attack it, in yards. Monsters inside it also\n" +
        "count as \"in combat\" for buffing and the In Combat heal/stamina/mana thresholds.";
    public const string RingRangeTip =
        "Ring spells replace a monster's other shape when at least Min Ring Targets monsters are\n" +
        "within this many yards (the monster's rule needs Ring on).";
    public const string ApproachRangeTip =
        "VTank's Approach Distance, in yards (metas: ApproachRange or ApproachDistance). Saved with\n" +
        "the profile for metas; RynthAi's own combat doesn't use it at the moment.";
    public const string CorpseMaxTip =
        "Corpses within this many yards are walked to and looted (Looting on). Further ones are left.";

    /// <summary>The first row with <paramref name="label"/> on any tab (null if none). Not for per-frame use.</summary>
    public static SettingRow? Find(string label)
    {
        foreach (SettingsTab tab in Tabs)
            foreach (SettingRow row in tab.Rows)
                if (row.Label == label) return row;
        return null;
    }

    public static readonly string[] AttackHeights = { "Low", "Medium", "High" };
    // RynthAi's EnvironmentResponse, in its order (0 Off .. 3).
    public static readonly string[] EnvironmentResponses = { "Off", "Move closer", "New target", "Move closer, then new target" };
    // The rows below show only once RynthAi has sent the value (an older RynthAi omits them).
    private static bool HasVariablePower(RynthAiSettings s) => s.VariablePowerMin.HasValue;
    private static bool HasEnvironmentThrottle(RynthAiSettings s) => s.EnvironmentImpactLimit.HasValue;
    private static bool HasKite(RynthAiSettings s) => s.KitePullMeters.HasValue;
    public static readonly string[] LootOwnershipModes = { "My Kills Only", "Fellowship Kills", "All Corpses" };
    public static readonly string[] NavLabelModes = { "Off", "Numbers", "Numbers and names" };
    public static readonly string[] DunPatrolCoverages = { "Whole dungeon", "Loops only", "Everything", "Sweep (by sight)" };
    public static readonly string[] DeathRecallModes = { "Off", "Cast a recall spell", "Use a recall item" };

    // The nav editor's recall spells. Filled with "Spell N (N)" and named from the client's spell
    // table as it loads: the combo reads this array every frame, and RecallSpellIndex refreshes it.
    private static readonly string[] RecallSpellItems = Array.ConvertAll(NavCommands.RecallIds, id => $"Spell {id} ({id})");
    private static bool _recallSpellItemsNamed;

    private static int RecallSpellIndex(RynthAiSettings s)
    {
        if (!_recallSpellItemsNamed)
        {
            string[] labels = NavCommands.RecallLabels;
            Array.Copy(labels, RecallSpellItems, Math.Min(labels.Length, RecallSpellItems.Length));
            _recallSpellItemsNamed = Array.TrueForAll(RecallSpellItems, l => !l.StartsWith("Spell ", StringComparison.Ordinal));
        }
        return Math.Max(0, Array.IndexOf(NavCommands.RecallIds, s.DeathRecallSpellId));
    }

    private static SettingRow Bool(string label, Func<RynthAiSettings, bool> get, Action<RynthAiSettings, bool> set,
        string? tip = null, Func<RynthAiSettings, bool>? when = null, bool gates = false) =>
        new(SettingKind.Bool, label, tip, s => get(s) ? 1 : 0, (s, v) => set(s, v != 0), VisibleWhen: when, Gates: gates);

    private static SettingRow Int(string label, Func<RynthAiSettings, int> get, Action<RynthAiSettings, int> set,
        int min, int max, int step, string? tip = null, Func<RynthAiSettings, bool>? when = null) =>
        new(SettingKind.Int, label, tip, s => get(s), (s, v) => set(s, (int)Math.Round(v)), min, max, step, VisibleWhen: when);

    private static SettingRow Float(string label, Func<RynthAiSettings, float> get, Action<RynthAiSettings, float> set,
        float min, float max, float step, string? tip = null, Func<RynthAiSettings, bool>? when = null) =>
        new(SettingKind.Float, label, tip, s => get(s), (s, v) => set(s, (float)v), min, max, step, VisibleWhen: when);

    private static SettingRow Double(string label, Func<RynthAiSettings, double> get, Action<RynthAiSettings, double> set,
        double min, double max, double step, string? tip = null) =>
        new(SettingKind.Double, label, tip, get, set, min, max, step);

    private static SettingRow Combo(string label, string[] items, Func<RynthAiSettings, int> get, Action<RynthAiSettings, int> set,
        string? tip = null, Func<RynthAiSettings, bool>? when = null) =>
        new(SettingKind.Combo, label, tip, s => get(s), (s, v) => set(s, (int)v), 0, items.Length - 1, 1, items, VisibleWhen: when);

    // Text size rows are stored by the engine (PanelTextScale), not in the
    // RynthAi profile, so the settings object is ignored here.
    // RynthDefective's window titles (its sections manifest).
    private const string ShortcutsWindow = "Shortcuts", GamePanelsWindow = "Game Panels", SelectionWindow = "Selection";

    // A plugin window shown or not; the row shows only while a plugin hosts it.
    private static SettingRow WindowShown(string label, string title, string tip) =>
        Bool(label, _ => ImGuiBackend.ImGuiPanelHost.IsOpen(title),
            (_, v) => { if (v) ImGuiBackend.ImGuiPanelHost.Open(title); else ImGuiBackend.ImGuiPanelHost.Close(title); },
            tip, when: _ => ImGuiBackend.ImGuiPanelHost.HasFace(title));

    // Its size: how big it draws itself, 30% to 250% of normal, or with its text size (PanelDrawScale).
    private static SettingRow WindowSize(string label, string title) =>
        Combo("   " + label, PanelDrawScale.Names, _ => PanelDrawScale.ToIndex(PanelDrawScale.Get(title)),
            (_, v) => PanelDrawScale.Set(title, PanelDrawScale.FromIndex(v)),
            "How big it is: 30% to 250% of normal. \"Text size\" = as big as the panels' text (Text Size above).\nAlso: Ctrl+wheel over it.",
            when: _ => ImGuiBackend.ImGuiPanelHost.HasFace(title));

    // RynthVision's player plate lock (MonsterHudSettings, nameplate_settings.txt): locked unless it is fixed on
    // screen and unlocked (a plate following the character can't be dragged, so it reads as locked).
    private static bool SelfPlateLocked()
    {
        if (!ImGuiBackend.Hud.MonsterHudSettings.Loaded) { ImGuiBackend.Hud.MonsterHudSettings.EnsureLoadQueued(); return true; }
        return !ImGuiBackend.Hud.MonsterHudSettings.SelfMovable;
    }

    // Unlocking a following plate fixes it on screen at its saved spot (SetSelfLocked); saved at once.
    private static void SetSelfPlateLocked(bool locked)
    {
        if (!ImGuiBackend.Hud.MonsterHudSettings.Loaded) { ImGuiBackend.Hud.MonsterHudSettings.EnsureLoadQueued(); return; }
        ImGuiBackend.Hud.MonsterHudSettings.SetSelfLocked(locked);
        ImGuiBackend.Hud.MonsterHudSettings.Save();
    }

    private static SettingRow TextSize(string label, string panelTitle) =>
        Combo("   " + label, PanelTextScale.SizeNames, _ => PanelTextScale.GetOwn(panelTitle), (_, v) => PanelTextScale.Set(panelTitle, v),
            "This panel's size: 100% = normal, 10% a step. Also: Ctrl+wheel over the panel.")
        with { VisibleWhen = _ => PanelTextScale.PerPanel };

    // One of the theme's eight colours (ImGuiBackend.RynthTheme); the settings object is ignored.
    private static SettingRow Colour(int index, string tip) =>
        new(SettingKind.Color, ImGuiBackend.ThemePalette.PrimaryNames[index], tip,
            _ => ImGuiBackend.RynthTheme.GetPrimary(index), (_, v) => ImGuiBackend.RynthTheme.SetPrimary(index, (uint)v));

    private static SettingRow Section(string text, Func<RynthAiSettings, bool>? when = null) =>
        new(SettingKind.Section, text, VisibleWhen: when);

    private static SettingRow Spacer(Func<RynthAiSettings, bool>? when = null) =>
        new(SettingKind.Spacer, "", VisibleWhen: when);

    private static SettingRow Note(string text, Func<RynthAiSettings, bool>? when = null) =>
        new(SettingKind.Note, text, VisibleWhen: when);

    private static SettingRow Button(string label, string buttonText, Action click, string? tip = null) =>
        new(SettingKind.Button, label, tip, ButtonText: buttonText, Click: click);

    // A button that edits the settings (the panel saves the result like a typed value).
    private static SettingRow EditButton(string label, string buttonText, Action<RynthAiSettings> edit, string? tip = null) =>
        new(SettingKind.Button, label, tip, ButtonText: buttonText, Edit: edit);

    private static SettingRow Text(string label, Func<RynthAiSettings, string> get, Action<RynthAiSettings, string> set,
        string? tip = null, string? hint = null) =>
        new(SettingKind.Text, label, tip, GetText: get, SetText: set, Hint: hint);

    // A one-line status RynthAi computes (read-only; empty = hidden).
    private static SettingRow Status(Func<RynthAiSettings, string> get) =>
        new(SettingKind.Status, "", GetText: get, VisibleWhen: s => !string.IsNullOrEmpty(get(s)));

    private static SettingRow[] Tiers(Func<RynthAiSettings, int>[] get, Action<RynthAiSettings, int>[] set)
    {
        var rows = new SettingRow[get.Length];
        for (int i = 0; i < rows.Length; i++)
            rows[i] = Int($"Level {i + 1}", get[i], set[i], 1, 500, 5);
        return rows;
    }

    // The prots and banes RynthAi lets you untick (its BuffManager.ToggleProtections/ToggleBanes):
    // (buff base name, label). The names must match RynthAi's.
    private static readonly (string Buff, string Label)[] ProtToggles =
    {
        ("Armor Self", "Armor"), ("Fire Protection Self", "Fire Protection"), ("Cold Protection Self", "Cold Protection"),
        ("Acid Protection Self", "Acid Protection"), ("Lightning Protection Self", "Lightning Protection"),
        ("Blade Protection Self", "Blade Protection"), ("Piercing Protection Self", "Piercing Protection"),
        ("Bludgeoning Protection Self", "Bludgeoning Protection"),
    };

    private static readonly (string Buff, string Label)[] BaneToggles =
    {
        ("Impenetrability", "Impenetrability"), ("Flame Bane", "Flame Bane"), ("Frost Bane", "Frost Bane"),
        ("Acid Bane", "Acid Bane"), ("Lightning Bane", "Lightning Bane"),
        ("Blade Bane", "Blade Bane"), ("Piercing Bane", "Piercing Bane"),
        ("Bludgeoning Bane", "Bludgeoning Bane"),
    };

    private static SettingRow[] BuffToggles((string Buff, string Label)[] toggles)
    {
        var rows = new SettingRow[toggles.Length];
        for (int i = 0; i < toggles.Length; i++)
        {
            string buff = toggles[i].Buff;
            rows[i] = Bool(toggles[i].Label, s => BuffOn(s, buff), (s, v) => SetBuffOn(s, buff, v),
                "Ticked: RynthAi casts it in the buff cycle. Unticked: it is skipped and never waited on.");
        }
        return rows;
    }

    private static bool BuffOn(RynthAiSettings s, string buff)
        => s.DisabledBuffs == null || !s.DisabledBuffs.Exists(b => string.Equals(b, buff, StringComparison.OrdinalIgnoreCase));

    /// <summary>A new list each time (RynthAi replaces its list rather than editing it).</summary>
    private static void SetBuffOn(RynthAiSettings s, string buff, bool on)
    {
        var list = s.DisabledBuffs == null ? new System.Collections.Generic.List<string>() : new System.Collections.Generic.List<string>(s.DisabledBuffs);
        list.RemoveAll(b => string.Equals(b, buff, StringComparison.OrdinalIgnoreCase));
        if (!on) list.Add(buff);
        s.DisabledBuffs = list;
    }

    private static SettingRow[] Concat(params object[] parts)
    {
        var list = new System.Collections.Generic.List<SettingRow>();
        foreach (object p in parts)
        {
            if (p is SettingRow r) list.Add(r);
            else if (p is SettingRow[] rs) list.AddRange(rs);
        }
        return list.ToArray();
    }

    public static readonly SettingsTab[] Tabs =
    {
        new("Display", new[]
        {
            Bool("Show Target Stamina / Mana", s => s.ShowTargetStaminaMana, (s, v) => s.ShowTargetStaminaMana = v,
                "When enabled, displays stamina and mana bars for the selected target (requires appraisal data)."),
            // Stored with the dashboard's state (RynthAiDashboardState), not in the RynthAi profile.
            Combo("RynthAi Dashboard Look", DashboardLooks, _ => RynthAiDashboardState.Classic ? 1 : 0,
                (_, v) => ImGuiBackend.Panels.RynthAiFace.SwitchLook(v == 1),
                "Modern: the compact dashboard. Classic: a retail-style window with tabs (Main, Monsters, Gear, Buffs,\nRoute, Loot, Meta, Tuning), drawn with the game's own pictures. Also: the button on either look's title bar."),
            // Stored in engine.json for every client (EngineSettings), not in the RynthAi profile.
            Bool("Insert Key Releases the UI", _ => Plugins.EngineSettings.InsertReleasesUi,
                (_, v) => { Plugins.EngineSettings.InsertReleasesUi = v; if (!v) ImGuiBackend.Win32Backend.ReclaimUi(); },
                "On: Insert switches between UI and GAME (shown on the RynthCore bar). In GAME, clicks pass through\nRynth windows to the game and Rynth text boxes can't be typed in. Press Insert again to get the UI back.\nOff (default): Insert does nothing. Applies to all your clients; others pick it up when they start."),
            Bool("Borderless Windowed", _ => Plugins.EngineSettings.BorderlessWindowed,
                (_, v) =>
                {
                    Plugins.EngineSettings.BorderlessWindowed = v;
                    AcWindowChrome.RequestApply();   // applied from the game thread, not inside this frame
                },
                "On: hide the Asheron's Call window title bar and borders while staying windowed (exclusive fullscreen is left alone).\nOff (default): normal window chrome. Turning it off only gives back the chrome this setting removed.\nApplies to all your clients; others pick it up when they start."),
            Spacer(),
            Section("Window and HUD Layout"),
            Bool("Remember Open Windows and Positions", _ => Plugins.EngineSettings.RememberWindowLayout,
                (_, v) => Plugins.EngineSettings.RememberWindowLayout = v,
                "On (default): reopen Rynth panels where you left them.\nOff: do not overwrite the saved panel layout or reopen panels from the last session. Applies to all your clients."),
            Bool("Remember Game Window Position", _ => Plugins.EngineSettings.RememberAcWindowPosition,
                (_, v) =>
                {
                    Plugins.EngineSettings.RememberAcWindowPosition = v;
                    AcWindowChrome.RequestApply();
                },
                "On: save the Asheron's Call window's position and size for each character and put it back after you log in.\nOff (default): the game window is never moved. A saved spot that is off-screen (a monitor that is gone) is ignored."),
            Button("Save Layout Now", "Save", () => AcWindowChrome.SaveLayoutNow(),
                "Write the open panels, the game window position (when Remember Game Window Position is on),\nthe Vision HUD and the ImGui window positions to disk now."),
            Spacer(),
            // The colour theme is stored by the engine (theme.txt, ImGuiBackend.RynthTheme), not in the RynthAi profile.
            Section("Colour Theme"),
            // The picker's list changes while the game runs (saved schemes), so it is asked each draw.
            new SettingRow(SettingKind.Combo, "Theme",
                "The colours of every RynthCore window: RynthCore's own, a preset, a scheme you saved, or Custom (your own colours).\n" +
                "Changing any colour below makes the theme Custom. Your own colours are kept whichever theme you pick: Custom brings them back.\n" +
                "Applies to all your clients as they start.",
                _ => ImGuiBackend.RynthTheme.PickerIndex, (_, v) => ImGuiBackend.RynthTheme.SetPicker((int)v), 0, 1000, 1,
                ItemsNow: ImGuiBackend.RynthTheme.PickerNames),
            Text("Save colours as", _ => string.Empty, (_, name) => ImGuiBackend.RynthTheme.SaveScheme(name),
                "Saves the colours in use under a name, as a scheme in the Theme list (the same name replaces it).\n" +
                "Saved for this PC (%AppData%\\RynthCore\\themes), so every character and client can pick it.",
                hint: "A name for these colours, then Enter"),
            Button("Saved scheme", "Delete it", () => ImGuiBackend.RynthTheme.DeleteCurrentScheme(),
                "Deletes the saved scheme in use. Its colours stay on screen as your own (Custom).")
                with { VisibleWhen = _ => ImGuiBackend.RynthTheme.SchemeInUse },
            Colour(0, "The main highlight: outlines, selected tabs and buttons, toggles that are on, the dashboard's accent line."),
            Colour(1, "Values, section headings and the bot's busy states (amber in RynthCore's colours)."),
            Colour(2, "Window and drawer backgrounds. The fields, rows and popups are shades of it."),
            Colour(3, "Panel title bars and table headers."),
            Colour(4, "Button and tab backgrounds."),
            Colour(5, "Button and field outlines; the dashboard's frame is a shade of it."),
            Colour(6, "Main text."),
            Colour(7, "Labels and quieter text."),
            Button("Reset Colours", "Use RynthCore's", () => ImGuiBackend.RynthTheme.SetPreset(0),
                "Back to RynthCore's own colours (the RynthCore theme)."),
            Spacer(),
            Section("Text Size (in-game panels)"),
            Combo("All Panels", PanelTextScale.SizeNames, _ => PanelTextScale.GetAll(), (_, v) => PanelTextScale.SetAll(v),
                "The size of every in-game panel: 100% = normal, 10% a step.\nOne panel on its own: Ctrl+wheel over it (it then keeps its own size)."),
            Combo("Toolbar", PanelTextScale.SizeNames, _ => PanelTextScale.GetChrome(), (_, v) => PanelTextScale.SetChrome(v),
                "Size of the RynthCore icon bar (its icons and tooltips), separate from the panels: 0 = normal, each step 10% bigger.\nAlso: right-click the bar's grip, Ctrl+wheel over the bar, or /rc ui barsize."),
            // The bar's rows are stored by the engine (panel_state.txt, ImGuiBar), not in the RynthAi profile.
            Combo("Toolbar Rows", BarLayout.RowNames, _ => ImGuiBackend.ImGuiBar.Rows - BarLayout.MinRows,
                (_, v) => ImGuiBackend.ImGuiBar.SetRowLayout(v + BarLayout.MinRows, ImGuiBackend.ImGuiBar.MaxPerRow),
                "How many rows the RynthCore icon bar spreads its icons over (Icons Per Row can add more).\nAlso: right-click the bar's grip, or /rc ui barrows."),
            Combo("Toolbar Icons Per Row", BarLayout.MaxPerRowNames, _ => BarLayout.MaxPerRowToIndex(ImGuiBackend.ImGuiBar.MaxPerRow),
                (_, v) => ImGuiBackend.ImGuiBar.SetRowLayout(ImGuiBackend.ImGuiBar.Rows, BarLayout.IndexToMaxPerRow(v)),
                "The most icons one row of the bar holds; more icons start another row. No limit: the rows share them evenly.\nAlso: right-click the bar's grip, or /rc ui baricons."),
            Bool("Size Each Panel Separately", _ => PanelTextScale.PerPanel, (_, v) => PanelTextScale.PerPanel = v,
                "Off: every panel uses All Panels. On: set each panel's text size below.", gates: true),
            TextSize("RynthAi Dashboard", "RynthAi"),
            TextSize("Meta", "Meta"),
            TextSize("Damage", "Damage"),
            TextSize("Settings", "Settings"),
            TextSize("Monsters", "Monsters"),
            TextSize("Nav", "Nav"),
            TextSize("Items", "Items"),
            TextSize("Lua", "Lua"),
            TextSize("Radar", "Radar"),
            TextSize("Chat Filters", "ChatFilters"),
            TextSize("RynthNav", "RynthNav"),
            TextSize("Vision", "Vision"),
            TextSize("Status", "Status"),
            TextSize("Log", "Log"),
            TextSize("Tracker", "Tracker"),
            Spacer(),
            Section("Item Info"),
            Bool("Describe Items When Selected", s => s.ItemInfoOnSelect, (s, v) => s.ItemInfoOnSelect = v,
                "Print a Mag-style description of an item in chat when you select it, identifying it\nfirst when needed. Items the bot selects while looting are skipped.\nAlso: /ra iteminfo on|off."),
            Button("Item Info Options", "Open...", () => RynthAiCommands.ApplyRemoteCommand("iteminfo", "window"),
                "Which fields the description shows and which item types are described on select (/ra iteminfo window)."),
        }),

        new("UI", new[]
        {
            Section("Radar"),
            Bool("Hide retail radar", s => s.SuppressRetailRadar, (s, v) => s.SuppressRetailRadar = v,
                "Suppress the game's built-in radar (bezel, compass, coords, blips)."),
            // "Show RynthRadar" / "Radar Click-Through" and "Show RynthChat" / "Chat Click-Through"
            // drove RynthAi's plugin-drawn radar and chat windows, which the engine no longer
            // draws (plugin ImGui rendering is RYNTHCORE_FORCE_IMGUI only). The Radar and Chat
            // panels open from the bar and have their own click-through (hold Ctrl) in their
            // gear menus. "Hide retail chat" lives in the Chat panel's gear menu
            // ("Hide retail chat"). The four fields stay in the payload so RynthAi and the
            // engine keep round-tripping them whichever is newer.
            Spacer(),
            Section("Power Bar"),
            Bool("Hide retail power bar", s => s.SuppressRetailPowerbar, (s, v) => s.SuppressRetailPowerbar = v,
                "Hide the vanilla attack/magic power bar that appears under the cursor while charging.\nThe bar's underlying combat state still works — only the on-screen widget is hidden."),
            Spacer(),
            Section("Vitals"),
            // Stored by the engine (RetailVitalsHooks, %LOCALAPPDATA%\RynthCore\retail_ui.txt),
            // not in the RynthAi profile, so the settings object is ignored here.
            Bool("Hide retail vitals bar", _ => Compatibility.RetailVitalsHooks.HideRetailVitals,
                (_, v) => Compatibility.RetailVitalsHooks.HideRetailVitals = v,
                "Hide the game's own health/stamina/mana bars; the RynthVision player plate and the RynthAi dashboard show them.\nSaved for this PC. Also: /rc ui retailvitals on|off."),
            // RynthVision's own plate (2026-10-27: players couldn't find how to move it). The same setting as
            // Vision > Nameplates > Your plate > Lock position, stored by the engine (nameplate_settings.txt).
            Bool("Lock your vitals plate", _ => SelfPlateLocked(), (_, v) => SetSelfPlateLocked(v),
                "Your RynthVision plate (the health, stamina and mana bars). Untick it and the plate shows a frame:\n" +
                "drag it where you want it, then tick this again to lock it there. A plate that follows your character\n" +
                "is fixed on screen when you untick this. Same as Vision > Nameplates > Your plate > Lock position.\n" +
                "Also: /rv plates self lock on|off.",
                when: _ => ImGuiBackend.Hud.HudFeed.VisionInstalled),
            Note("To move it: untick, drag the plate itself (not where a panel covers it), then tick to lock it.",
                when: _ => ImGuiBackend.Hud.HudFeed.VisionInstalled && ImGuiBackend.Hud.MonsterHudSettings.SelfPlate),
            Note("Your plate is turned off: Vision > Nameplates > Your plate > Show your plate.",
                when: _ => ImGuiBackend.Hud.HudFeed.VisionInstalled && ImGuiBackend.Hud.MonsterHudSettings.Loaded
                           && !ImGuiBackend.Hud.MonsterHudSettings.SelfPlate),
            Spacer(),
            Section("Toolbar"),
            // Stored by the engine (RetailToolbarHooks, retail_ui.txt beside the vitals), not in the profile.
            Bool("Hide retail toolbar", _ => Compatibility.RetailToolbarHooks.HideRetailToolbar,
                (_, v) => Compatibility.RetailToolbarHooks.HideRetailToolbar = v,
                "Hide the game's toolbar panel: the combat button, the panel buttons, the selection's health, examine, the backpack and the shortcut slots.\nThe shortcut keys keep working while it's hidden. Saved for this PC. Also: /rc ui retailtoolbar on|off."),
            // RynthDefective's windows in its place (plugin windows the engine hosts, API v90): shown or
            // not (the panel's open state, remembered with the layout) and their size (the window's own
            // text size, as Ctrl+wheel over it sets). Only while a plugin hosts them.
            WindowShown("Shortcut bar", ShortcutsWindow,
                "RynthDefective's bar of your shortcut slots, in place of the retail toolbar's: the game's 18 with their keys, then RynthCore's.\nAlso: /rdef."),
            WindowSize("Shortcut bar size", ShortcutsWindow),
            WindowShown("Game panel buttons", GamePanelsWindow,
                "Small buttons for the game's panels (pack, attributes, spellbook, fellowship, map...), in place of the retail toolbar's.\nAlso: /rdef panels."),
            WindowSize("Game panel buttons size", GamePanelsWindow),
            WindowShown("Selection", SelectionWindow,
                "What you have selected, as the retail toolbar showed it: its name, how far and which way it is, a creature's health, and Use.\nAlso: /rdef selection."),
            WindowSize("Selection size", SelectionWindow),
        }),

        // The user hotkeys: stored by the engine for the PC (Hotkeys/HotkeyService.cs, hotkeys.txt),
        // not in the RynthAi profile; the ImGui face draws the whole editor for this one row.
        new("Hotkeys", new[] { new SettingRow(SettingKind.Hotkeys, "Hotkeys") }),

        new("Misc", new[]
        {
            Bool("Enable FPS Limit", s => s.EnableFPSLimit, (s, v) => s.EnableFPSLimit = v, gates: true),
            Int("Focused FPS", s => s.TargetFPSFocused, (s, v) => s.TargetFPSFocused = v, 10, 240, 1, when: s => s.EnableFPSLimit),
            Int("Background FPS", s => s.TargetFPSBackground, (s, v) => s.TargetFPSBackground = v, 5, 60, 1, when: s => s.EnableFPSLimit),
            // Stored by the engine (D3D9/VideoPatch, %LOCALAPPDATA%\RynthCore\video_patch.txt, per
            // character), not in the RynthAi profile, so the settings object is ignored here.
            Combo("Video patch", D3D9.VideoPatchPolicy.ComboItems, _ => (int)D3D9.VideoPatch.Mode,
                (_, v) => D3D9.VideoPatch.SetMode((D3D9.VideoPatchMode)Math.Clamp(v, 0, 3), save: true, "Settings"),
                "Stop drawing the game to save CPU and GPU (game logic, the bot and the plugins keep running).\n" +
                "World when unfocused: the 3D world isn't drawn while another window has the focus.\n" +
                "World: the 3D world is never drawn; AC's windows and these panels still are.\n" +
                "All: AC's windows aren't drawn either (this session only; /rc video off restores).\n" +
                "Saved for this character. The phone live view shows what the client draws. Also: /rc video."),
            Int("   Frame cap while patched", _ => D3D9.VideoPatch.FrameCapFps, (_, v) => D3D9.VideoPatch.SetFrameCap(v), 0, 60, 1,
                "While the 3D world isn't drawn the client runs at most this many frames a second, so the\n" +
                "saved time is slept (0 = no extra cap). Saved for this PC. Also: /rc video fps <n>.",
                when: _ => D3D9.VideoPatch.Mode != D3D9.VideoPatchMode.Off),
            // Stored in engine.json for every client (EngineSettings), not in the RynthAi profile.
            Bool("Close Client on Server Disconnect", _ => Plugins.EngineSettings.CloseOnServerDisconnect,
                (_, v) => Plugins.EngineSettings.CloseOnServerDisconnect = v,
                "On (default): when the game shows \"The server has disconnected\" (only a Quit Game button),\n" +
                "the client closes itself 5 seconds later, so the launcher's auto-relaunch can start it again.\n" +
                "A logout or a quit never shows that screen. Applies to all your clients; others pick it up when they start."),
            // Stored in engine.json for every client (EngineSettings.AutoIdMode, AutoIdPolicy).
            Combo("Background Identify", new[] { "All", "Limited", "Off" }, _ => (int)Plugins.EngineSettings.AutoIdMode,
                (_, v) => Plugins.EngineSettings.AutoIdMode = (Compatibility.AutoIdMode)Math.Clamp(v, 0, 2),
                "How much the client identifies (appraises) on its own, in the background.\n" +
                "All: everything in view, as before (monster levels on every nameplate; the most traffic).\n" +
                "Limited (default): monsters, ground items and other players only when selected; your pack\n" +
                "items after 30 s, at most 5 a second. Unselected monsters' nameplates show no level.\n" +
                "Off: nothing in the background.\n" +
                "RynthAi's own identifies (loot, monster weaknesses) are never limited. Applies to all your clients. Also: /rc autoid."),
            // Stored in engine.json for every client (EngineSettings.HoldReloadInWorld, ReloadInWorldGate); opt-in.
            Bool("Hold reloads while in the world", _ => Plugins.EngineSettings.HoldReloadInWorld,
                (_, v) => Plugins.EngineSettings.HoldReloadInWorld = v,
                "On: when a new RynthCore build is deployed (the automatic engine reload), the reload waits until you\n" +
                "log out or reach character select instead of reloading mid-session. A workaround for client crashes\n" +
                "when reloads stack up while you are in the world (SilentKelpie's).\n" +
                "It never waits forever: after the time below it reloads anyway. A reload you ask for yourself\n" +
                "(the RL button, ERl, /rc rl, the hotkey) is never held.\n" +
                "Off (default): the engine reloads as soon as the new build is there. Applies to all your clients.", gates: true),
            Int("   Give up holding after (min)", _ => Plugins.EngineSettings.HoldReloadInWorldMaxMinutes,
                (_, v) => Plugins.EngineSettings.HoldReloadInWorldMaxMinutes = v, 1, 240, 1,
                "How long a held reload waits for you to leave the world before it reloads anyway (default 10).",
                when: _ => Plugins.EngineSettings.HoldReloadInWorld),
            Spacer(),
            Bool("Auto Cram", s => s.EnableAutocram, (s, v) => s.EnableAutocram = v,
                "Automatically moves items from your main pack into side packs.\nNote: recently used weapons stay in the main pack."),
            Bool("Peace Mode When Idle", s => s.PeaceModeWhenIdle, (s, v) => s.PeaceModeWhenIdle = v),
            Bool("Weapons Back After Heals", s => s.RearmAfterVitals == true, (s, v) => s.RearmAfterVitals = v,
                "After a heal, restam or remana spell with nothing to fight, put the last fight's weapons\n(main hand and off hand) back on instead of waiting for the next monster."),
            Bool("Start Macro On Login", s => s.StartMacroOnLogin, (s, v) => s.StartMacroOnLogin = v,
                "Automatically starts the macro when RynthAi loads."),
            Bool("Patrol On Login", s => s.PatrolOnLogin, (s, v) => s.PatrolOnLogin = v,
                "Automatically starts dungeon patrol when RynthAi loads."),
            Bool("Escape Acid and Lava", s => s.EscapeHazards, (s, v) => s.EscapeHazards = v,
                "When acid or lava hurts you, stop everything (combat, buffs, healing, looting)\nand walk back out the way you came. The spot is remembered so the patrol avoids it.\nTurn off for a character that walks through acid on purpose."),
            Bool("Yield to VTank", s => s.YieldToVTank, (s, v) => s.YieldToVTank = v,
                "With Decal: when VTank's macro starts (/vt start), RynthAi's macro stops, and it won't start while VTank runs.\nOne bot per client. Without Decal this does nothing. Also: /ra vtankyield on|off."),
            Bool("Enable Raycasting", s => s.EnableRaycasting, (s, v) => s.EnableRaycasting = v),
            // The Server Features (ILT) rows went with the ILT Hub to RynthILT (2026-10-13): its world
            // list and "Treat this world as ILT" are on the Mini Remote's Character tab (/ra hub servers).
            Spacer(),
            Section("Missile Arc Velocities (m/s)"),
            Bool("Use Arcs for Missile LoS", s => s.UseArcs, (s, v) => s.UseArcs = v,
                "A missile target must pass both the straight line and the real arrow arc, ceilings included (dungeons too).\nWhen off, all missile LoS checks are linear (eye-to-eye).", gates: true),
            Float("Bow", s => s.BowArcVelocity, (s, v) => s.BowArcVelocity = v, 10f, 60f, 0.5f,
                "Bow projectile speed (m/s). Lower = higher arc.", when: s => s.UseArcs),
            Float("Crossbow", s => s.CrossbowArcVelocity, (s, v) => s.CrossbowArcVelocity = v, 10f, 80f, 0.5f, when: s => s.UseArcs),
            Float("Atlatl", s => s.AtlatlArcVelocity, (s, v) => s.AtlatlArcVelocity = v, 10f, 60f, 0.5f, when: s => s.UseArcs),
            Float("Magic Arc", s => s.MagicArcVelocity, (s, v) => s.MagicArcVelocity = v, 10f, 60f, 0.5f,
                "Arc spells' horizontal speed (m/s). ACE: 40. Lower = higher arc.\nA monster rule with Arc on casts an arc only when this path reaches the target (walls, and ceilings in dungeons); otherwise its other shape (Streak, Blast) or a bolt. Needs Enable Raycasting. /ra lostest magic shows the arc to the selected mob."),
            Float("Arc Clearance (m)", s => s.MissileArcClearance, (s, v) => s.MissileArcClearance = MathF.Max(0f, v), 0f, 3f, 0.1f,
                "Extra headroom the arc must have at mid-flight. Raise it if arrows still hit ceilings; lower it if reachable mobs get skipped.", when: s => s.UseArcs),
            Bool("LoS Debug Log", s => s.LosDebugLog, (s, v) => s.LosDebugLog = v,
                "Log each blocked missile target and why (arc peak, where it hits) to the RynthCore log. /ra lostest bow tests the selected mob."),
            Spacer(),
            Section("Monster Blacklist"),
            Int("Attempts Before Blacklist", s => s.BlacklistAttempts, (s, v) => s.BlacklistAttempts = v, 1, 20, 1,
                "How many failed attack attempts on a mob before it gets blacklisted."),
            Int("Blacklist Timeout (sec)", s => s.BlacklistTimeoutSec, (s, v) => s.BlacklistTimeoutSec = v, 5, 120, 5,
                "How long a blacklisted mob is ignored before re-trying."),
            Int("No Progress Timeout (sec)", s => s.TargetNoProgressTimeoutSec, (s, v) => s.TargetNoProgressTimeoutSec = v, 0, 300, 10,
                "Blacklist a target after being engaged this many seconds without dealing damage.\n0 = disabled. Default 60s."),
            Spacer(),
            Section("Give Queue"),
            Int("Give Interval (ms)", s => s.GiveQueueIntervalMs, (s, v) => s.GiveQueueIntervalMs = v, 50, 2000, 50,
                "Minimum delay between each item sent by /ra givea commands."),
        }),

        new("Recharge", new[]
        {
            Section("In Combat — target within Monster Range (%)"),
            Int("Heal At", s => s.HealAt, (s, v) => s.HealAt = v, 0, 100, 1,
                "While a target is within Monster Range, cast Heal Self when HP < this %."),
            Int("Re-stam At", s => s.RestamAt, (s, v) => s.RestamAt = v, 0, 100, 1,
                "While a target is within Monster Range, cast Revitalize Self when Stamina < this %."),
            Int("Re-stam To", s => s.RestamTo, (s, v) => s.RestamTo = v, 0, 100, 1,
                "Once a recharge starts under Re-stam At, keep recharging until Stamina reaches this %\n(one Revitalize often doesn't get far). 0 = one recharge, as before."),
            Int("Get Mana At", s => s.GetManaAt, (s, v) => s.GetManaAt = v, 0, 100, 1,
                "While a target is within Monster Range, cast Stamina to Mana Self when Mana < this % (needs stam > 15%)."),
            Spacer(),
            Section("Idle Top-Off — no targets in range (%)"),
            Int("Top HP", s => s.TopOffHP, (s, v) => s.TopOffHP = v, 0, 100, 1,
                "When no targets are within Monster Range, heal up to this HP %. Usually set higher than Heal At."),
            Int("Top Stam", s => s.TopOffStam, (s, v) => s.TopOffStam = v, 0, 100, 1,
                "When no targets are within Monster Range, re-stam up to this %."),
            Int("Top Mana", s => s.TopOffMana, (s, v) => s.TopOffMana = v, 0, 100, 1,
                "When no targets are within Monster Range, recharge mana up to this %."),
            Spacer(),
            Section("Kits & Potions"),
            Bool("Use Potions", s => s.UsePotions, (s, v) => s.UsePotions = v,
                "Drink health, mana and stamina potions from the Items panel when a vital is low\nand no spell can fix it (no spell, Life Magic untrained, out of components)."),
            Bool("Use Buff Items", s => s.UseBuffItems, (s, v) => s.UseBuffItems = v,
                "Use Asheron's Benediction, Asheron's Lesser Benediction and Blackmoor's Favor\nfrom your pack whenever their buff isn't on you (they're never used up)."),
            Int("Make Field Rations Below", s => s.MakeRationsBelow, (s, v) => s.MakeRationsBelow = v, 0, 100, 1,
                "Cook field rations (Cooking Pot on Dried Rations, 25 at a time) when you have fewer than this.\n0 = off. Rations restore stamina and are eaten like stamina potions."),
            Bool("Use Kits In Magic Mode", s => s.UseKitsInMagicMode, (s, v) => s.UseKitsInMagicMode = v,
                "Allow healing kits while in magic mode."),
            Bool("Peace Mode For Kits", s => s.PeaceModeForKits, (s, v) => s.PeaceModeForKits = v,
                "Switch to peace mode before using a healing kit."),
            Int("Emergency Heal At", s => s.EmergencyHealAt, (s, v) => s.EmergencyHealAt = v, 0, 100, 1,
                "At or under this health %, out of Magic mode a healing kit is used ahead of the\nnormal Heal At chain (kit, Heal Self, potion), and while a spell cast is pending a kit\nor potion is used. Stamina to Health has its own line below. 0 = off. Starts at 30."),
            Int("Stamina To Health At", s => s.StaminaToHealthAt, (s, v) => s.StaminaToHealthAt = v, 0, 100, 1,
                "At or under this health %, cast Stamina to Health Self ahead of the normal\nHeal At chain (out of Magic mode a kit is tried first). If it can't be cast,\nthe normal chain (kit, Heal Self, potion) runs. 0 = never cast it. Starts at 30."),
            Int("Stamina To Health Min Stamina", s => s.StaminaToHealthMinStamina, (s, v) => s.StaminaToHealthMinStamina = v, 0, 100, 1,
                "Cast Stamina to Health only while stamina is over this %, so it doesn't\ndrain you to nothing. Starts at 20."),
            Int("Kit Min Success %", s => s.KitMinSuccessPct, (s, v) => s.KitMinSuccessPct = v, 0, 100, 1,
                "Use a healing or stamina kit only when its chance to work is at least this %,\nworked out like the server does: Healing skill + the kit's bonus vs. what's missing.\nBelow it, the spell is cast instead. 0 = always use kits. Starts at 70."),
            Spacer(),
            Section("Safety Stops"),
            Bool("Stop Macro On Death", s => s.StopMacroOnDeath, (s, v) => s.StopMacroOnDeath = v,
                "Stop the macro when you die. Off by default: most metas run back on their own."),
            Bool("Stop Macro On No Components", s => s.StopMacroOnNoComponents, (s, v) => s.StopMacroOnNoComponents = v,
                "Stop the macro after 3 \"missing components\" failures within a minute."),
            Bool("Recall When Low On Components", s => s.LowCompsRecall, (s, v) => s.LowCompsRecall = v,
                "With the macro running: when your tapers (all kinds together) drop to Low Tapers At, or any one kind of\n" +
                "scarab (not Diamond) drops to Low Scarabs At, stop the macro and recall to the lifestone, and say in chat\n" +
                "which ran low. Only kinds you've carried this session count, so a character with no scarabs isn't 'low'.\n" +
                "Off by default.", gates: true),
            Int("Low Tapers At", s => s.LowCompsTaperMin, (s, v) => s.LowCompsTaperMin = v, 0, 500, 5,
                "Recall when your tapers, all kinds together, are at or under this many. 0 = don't check tapers. Default 10.",
                when: s => s.LowCompsRecall),
            Int("Low Scarabs At", s => s.LowCompsScarabMin, (s, v) => s.LowCompsScarabMin = v, 0, 50, 1,
                "Recall when any one kind of scarab (Lead, Iron, Copper, Silver, Gold, Pyreal, Platinum, Mana; not Diamond)\n" +
                "is at or under this many. 0 = don't check scarabs. Default 2.",
                when: s => s.LowCompsRecall),
            Bool("Stop Macro When Pack Full", s => s.StopMacroWhenPackFull, (s, v) => s.StopMacroWhenPackFull = v,
                "Stop the macro when no pack has a free slot."),
            Spacer(),
            Section("On Death"),
            Combo("Recall On Death", DeathRecallModes, s => Math.Clamp(s.DeathRecallMode, 0, DeathRecallModes.Length - 1), (s, v) => s.DeathRecallMode = v,
                "After you die with the macro running, once you've respawned: cast a recall spell or use\n" +
                "a recall item (a gem, scroll or token) from your pack. Nothing else runs until the teleport\n" +
                "lands; a fizzle is retried (5 tries, 60 s at most). Then nav rejoins the route at the closest\n" +
                "waypoint, and Travel Back To My Corpse walks to the corpse from there. Stop Macro On Death\n" +
                "still recalls first. Off by default: most metas handle a death themselves."),
            Combo("Recall Spell", RecallSpellItems, RecallSpellIndex, (s, v) => s.DeathRecallSpellId = NavCommands.RecallIds[Math.Clamp(v, 0, NavCommands.RecallIds.Length - 1)],
                "The recall spell to cast after a death (you must know it). Primary Portal Recall takes you\n" +
                "to where you last tied a primary portal, often next to your hunting ground.")
                with { VisibleWhen = s => s.DeathRecallMode == 1 },
            Text("Recall Item", s => s.DeathRecallItem, (s, v) => s.DeathRecallItem = v.Trim(),
                "Name of the item to use after a death, as it shows in your inventory. An exact match wins;\n" +
                "otherwise the first item whose name contains it.", hint: "item name, as in your inventory")
                with { VisibleWhen = s => s.DeathRecallMode == 2 },
            Spacer(),
            Section("Helper Settings (%) — NOT WIRED YET"),
            Int("Heal Others", s => s.HealOthersAt, (s, v) => s.HealOthersAt = v, 0, 100, 1,
                "Intended: cast Heal Other on a fellow when their HP < this %. Not implemented yet — slider is inert."),
            Int("Re-stam Others", s => s.RestamOthersAt, (s, v) => s.RestamOthersAt = v, 0, 100, 1,
                "Intended: cast Revitalize Other on a fellow when their Stamina < this %. Not implemented yet — slider is inert."),
            Int("Infuse Others", s => s.InfuseOthersAt, (s, v) => s.InfuseOthersAt = v, 0, 100, 1,
                "Intended: cast Infuse Mana Other on a fellow when their Mana < this %. Not implemented yet — slider is inert."),
        }),

        new("Melee Combat", new[]
        {
            Section("Attack Power & Height"),
            Bool("Use Recklessness", s => s.UseRecklessness, (s, v) => s.UseRecklessness = v,
                "When enabled and Recklessness is trained, auto power uses 80% instead of 100%."),
            Spacer(),
            Bool("Melee Auto Power", s => s.MeleeAttackPower < 0, (s, v) => s.MeleeAttackPower = v ? -1 : 100, gates: true),
            Int("Melee Power %", s => s.MeleeAttackPower, (s, v) => s.MeleeAttackPower = v, 0, 100, 5, when: s => s.MeleeAttackPower >= 0),
            Combo("Melee Attack Height", AttackHeights, s => s.MeleeAttackHeight, (s, v) => s.MeleeAttackHeight = v),
            Spacer(),
            Bool("Missile Auto Power", s => s.MissileAttackPower < 0, (s, v) => s.MissileAttackPower = v ? -1 : 100, gates: true),
            Int("Missile Power %", s => s.MissileAttackPower, (s, v) => s.MissileAttackPower = v, 0, 100, 5, when: s => s.MissileAttackPower >= 0),
            Combo("Missile Attack Height", AttackHeights, s => s.MissileAttackHeight, (s, v) => s.MissileAttackHeight = v),
            Spacer(),
            Section("Variable Power (monster rules)", HasVariablePower),
            Int("Variable Min %", s => s.VariablePowerMin ?? 50, (s, v) => s.VariablePowerMin = v, 0, 100, 5,
                "A monster rule's Power picks per monster (Damage panel, a monster's detail):\n" +
                "Default (the settings above), High (100%), Reckless (80%, needs Recklessness trained),\n" +
                "Variable (a new random power each attack, between these two), or Reckless + Variable\n" +
                "(random, kept inside the Recklessness band 20-80%).",
                when: HasVariablePower),
            Int("Variable Max %", s => s.VariablePowerMax ?? 100, (s, v) => s.VariablePowerMax = v, 0, 100, 5,
                when: HasVariablePower),
            Spacer(HasVariablePower),
            Section("Hitting the Environment", HasEnvironmentThrottle),
            Combo("Missile Response", EnvironmentResponses, s => s.MissileEnvironmentResponse ?? 0, (s, v) => s.MissileEnvironmentResponse = v,
                "When arrows or bolts keep hitting walls or objects instead of the target\n" +
                "(\"Your missile attack hit the environment.\"), stop shooting and:\n" +
                "  Move closer - run toward the target for a clearer shot\n" +
                "  New target - skip this one for a while and fight another\n" +
                "  Move closer, then new target - one approach, then switch\n" +
                "The only monster in reach is never skipped. Off by default. Magic: Spell Combat tab.",
                when: HasEnvironmentThrottle),
            Int("Hits to trip", s => s.EnvironmentImpactLimit ?? 2, (s, v) => s.EnvironmentImpactLimit = v, 1, 10, 1,
                "Environment hits on one target, within the window, before combat reacts (missiles and magic).",
                when: HasEnvironmentThrottle),
            Int("Window (s)", s => s.EnvironmentImpactWindowSec ?? 20, (s, v) => s.EnvironmentImpactWindowSec = v, 5, 120, 5,
                when: HasEnvironmentThrottle),
            Int("Move closer by (m)", s => s.EnvironmentApproachMeters ?? 6, (s, v) => s.EnvironmentApproachMeters = v, 1, 30, 1,
                when: HasEnvironmentThrottle),
            Int("Closest (m)", s => s.EnvironmentMinDistance ?? 3, (s, v) => s.EnvironmentMinDistance = v, 1, 30, 1,
                "Never moves nearer than this. Already this close, a new target is picked instead.",
                when: HasEnvironmentThrottle),
            Int("Skip target (s)", s => s.EnvironmentSkipSec ?? 30, (s, v) => s.EnvironmentSkipSec = v, 5, 300, 5,
                when: HasEnvironmentThrottle),
            Spacer(HasEnvironmentThrottle),
            Section("Pull Away (Kite)", HasKite),
            Int("Pull Distance (m)", s => s.KitePullMeters ?? 20, (s, v) => s.KitePullMeters = v, 3, 100, 1,
                "When a monster rule's Kite is PullAway (or Default follows the DEFAULT row set to PullAway),\n" +
                "tag the mob then run until it has been dragged this many metres from where it stood, then kill it.\n" +
                "Set per mob on the Damage panel (Kite). Off for every monster until a rule asks for it.",
                when: HasKite),
            Bool("Tag Before Fleeing", s => s.KiteTagFirst ?? true, (s, v) => s.KiteTagFirst = v,
                "Land a hit before running so aggro sticks. Off = flee as soon as locked.",
                when: HasKite),
            Spacer(HasKite),
            Bool("Use Native Attack", s => s.UseNativeAttack, (s, v) => s.UseNativeAttack = v,
                "Uses the client's combat pipeline for attacks.\nThe client handles turn-to-face naturally (no backwards arrows)."),
            Spacer(),
            Bool("Summon Pets", s => s.SummonPets, (s, v) => s.SummonPets = v),
            Int("Pet Min Monsters", s => s.PetMinMonsters, (s, v) => s.PetMinMonsters = v, 1, 20, 1),
            new(SettingKind.Int, "Pet Range",
                "A pet is summoned when at least Pet Min Monsters attackable monsters are this close (yards).",
                s => s.CustomPetRange ?? 5, (s, v) => s.CustomPetRange = (int)Math.Round(v), 1, 50, 1,
                VisibleWhen: s => s.CustomPetRange.HasValue),
        }),

        new("Spell Combat", Concat(
            Section("War/Void Casting Settings"),
            Int("Buff Spell Interval (ms)", s => s.SpellCastIntervalMs, (s, v) => s.SpellCastIntervalMs = v, 100, 1500, 50,
                "Delay between BUFF / utility spell casts (not combat).\nLower = faster buff chains. 400ms is a good balance.\nBelow 200ms may cause fizzles or dropped casts on laggy servers."),
            Int("Attack Spell Delay (ms)", s => s.AttackSpellIntervalMs, (s, v) => s.AttackSpellIntervalMs = v, 0, 5000, 50,
                "Least time between offensive (war/void) COMBAT casts at one target.\nThe next cast also waits until the last one is finished (the server's\nanswer and the casting animation), so 0 = cast again as soon as it is.\nDoes NOT affect buffing speed."),
            Bool("Cast Dispel Self", s => s.CastDispelSelf, (s, v) => s.CastDispelSelf = v),
            Bool("Use Dispel Items", s => s.UseDispelItems ?? false, (s, v) => s.UseDispelItems = v,
                "VTank's Use Dispel Items, saved with the profile. Metas read it (UseDispelItems).",
                when: s => s.UseDispelItems.HasValue),
            Bool("Auto Fellowship Management", s => s.AutoFellowMgmt ?? false, (s, v) => s.AutoFellowMgmt = v,
                "VTank's Auto Fellow Mgmt., saved with the profile. Metas read it (AutoFellowMgmt).",
                when: s => s.AutoFellowMgmt.HasValue),
            Bool("M. Charges When Off", s => s.MChargesWhenOff ?? false, (s, v) => s.MChargesWhenOff = v,
                "VTank's option of this name, saved with the profile for VTank users. RynthAi doesn't act on it yet.",
                when: s => s.MChargesWhenOff.HasValue),
            Spacer(),
            Section("Ring Spell Override"),
            Int("Min Ring Targets", s => s.MinRingTargets, (s, v) => s.MinRingTargets = v, 1, 20, 1,
                "If this many monsters are within ring range, ring spells are used instead of arc/bolt/streak."),
            Spacer(),
            Section("Blast Spell Override"),
            Int("Blast Range (yd)", s => s.BlastRange, (s, v) => s.BlastRange = v, 0, 100, 1,
                "0 = off: monsters with Blast on always cast blasts.\nAbove 0: a blast is cast only when Min Blast Targets monsters are\nwithin this range in the blast's fan toward the target (it fires 3\nprojectiles over 90 degrees); otherwise the monster's other shape, or a bolt."),
            Int("Min Blast Targets", s => s.MinBlastTargets, (s, v) => s.MinBlastTargets = v, 1, 20, 1,
                "How many monsters (the target included) must be in the blast's fan, within Blast Range."),
            Spacer(),
            Section("Wall Spell Override"),
            Int("Wall Range (yd)", s => s.WallRange, (s, v) => s.WallRange = v, 0, 100, 1,
                "0 = off: walls are never cast.\nAbove 0: monsters with Wall on cast a wall when Min Wall Targets monsters stand in its\npath toward the target, within this range. RynthAi turns to face the target first\n(a wall flies straight ahead of the caster)."),
            Int("Min Wall Targets", s => s.MinWallTargets, (s, v) => s.MinWallTargets = v, 1, 20, 1,
                "How many monsters (the target included) must be in the wall's path, within Wall Range."),
            Spacer(),
            Section("Hitting the Environment", HasEnvironmentThrottle),
            Combo("Magic Response", EnvironmentResponses, s => s.MagicEnvironmentResponse ?? 0, (s, v) => s.MagicEnvironmentResponse = v,
                "When bolts, streaks, arcs or blasts keep stopping short of the target\n" +
                "(a wall, a pillar, the ceiling) with no hit or resist, stop casting and:\n" +
                "  Move closer - run toward the target for a clearer line\n" +
                "  New target - skip this one for a while and fight another\n" +
                "  Move closer, then new target - one approach, then switch\n" +
                "Rings and walls aren't judged. The only monster in reach is never skipped. Off by default.\n" +
                "Hits to trip, the window, distances and the skip time: Melee Combat tab.",
                when: HasEnvironmentThrottle),
            Spacer(HasEnvironmentThrottle),
            Section("Spell Difficulty (Min Buffed Skill)"),
            Tiers(
                new Func<RynthAiSettings, int>[] { s => s.MinSkillLevelTier1, s => s.MinSkillLevelTier2, s => s.MinSkillLevelTier3, s => s.MinSkillLevelTier4,
                                                   s => s.MinSkillLevelTier5, s => s.MinSkillLevelTier6, s => s.MinSkillLevelTier7, s => s.MinSkillLevelTier8 },
                new Action<RynthAiSettings, int>[] { (s, v) => s.MinSkillLevelTier1 = v, (s, v) => s.MinSkillLevelTier2 = v, (s, v) => s.MinSkillLevelTier3 = v, (s, v) => s.MinSkillLevelTier4 = v,
                                                     (s, v) => s.MinSkillLevelTier5 = v, (s, v) => s.MinSkillLevelTier6 = v, (s, v) => s.MinSkillLevelTier7 = v, (s, v) => s.MinSkillLevelTier8 = v }))),

        new("Ranges", new[]
        {
            Section("Standard Ranges (Yards)"),
            Int("Monster Range", s => s.MonsterRange, (s, v) => s.MonsterRange = v, 1, 200, 1, MonsterRangeTip),
            Int("Ring Range", s => s.RingRange, (s, v) => s.RingRange = v, 1, 50, 1, RingRangeTip),
            Int("Approach Range", s => s.ApproachRange, (s, v) => s.ApproachRange = v, 1, 50, 1, ApproachRangeTip),
            Int("Disengage Range", s => s.MonsterDisengageRange, (s, v) => s.MonsterDisengageRange = v, 0, 200, 1,
                "Drop a locked target only once it is this far away (yards).\n0, or anything up to Monster Range, = automatic (Monster Range + 3)."),
            Spacer(),
            Section("Combat Targeting"),
            Combo("Targeting", new[] { "Lowest health", "Closest", "Farthest", "Least turning" },
                s => Math.Clamp(s.TargetSelection ?? 0, 0, 3), (s, v) => s.TargetSelection = Math.Clamp(v, 0, 3),
                "Which monster to attack, among those of the highest priority inside Monster Range.\n" +
                "Least turning: the one needing the smallest turn to face. Unknown health counts as full."),
            Bool("Target Lock", s => s.TargetLock ?? true, (s, v) => s.TargetLock = v,
                "On: keeps the target it picked until it dies, leaves range (Disengage Range), or a higher-priority monster shows up.\n" +
                "Off: switches whenever another monster is clearly better by the Targeting rule."),
            Spacer(),
            Section("Corpse Acquisition (Yards)"),
            Double("Corpse Max (yd)", s => s.CorpseApproachRangeMax, (s, v) => s.CorpseApproachRangeMax = v, 0.5, 50.0, 0.5, CorpseMaxTip),
            Double("Corpse Min (yd)", s => s.CorpseApproachRangeMin, (s, v) => s.CorpseApproachRangeMin = v, 0.5, 20.0, 0.5,
                "How close to walk to a corpse before opening it, in yards (never more than Corpse Max)."),
        }),

        new("Navigation", new[]
        {
            Bool("Boost Nav Priority", s => s.BoostNavPriority, (s, v) => s.BoostNavPriority = v),
            Float(NavPointReachLabel, s => s.FollowNavMin, (s, v) => s.FollowNavMin = v, 0.5f, 20f, 0.1f, NavPointReachTip),
            Spacer(),
            Section("Infinite Leaftide"),
            Bool(VaultedDungeonsLabel, s => s.EnableVaultedDungeons ?? false, (s, v) => s.EnableVaultedDungeons = v,
                "Master switch of the Vaulted Dungeons keeper (Infinite Leaftide / ACECustom only). Off by default.\n" +
                "While off the keeper does nothing: no recalls, no /rc quit, no Mini Remote tab, and a saved On or\n" +
                "Auto Start is ignored. Turning it off while it runs stops it at once.\n" +
                "On: the Mini Remote shows the \"Vaulted Dungeons\" tab on those worlds, where you pick a dungeon and a\n" +
                "tied Portal Recall Gem and start it yourself (/ilt vaulted on); it never resumes by itself after a\n" +
                "relog unless Auto Start On Arrival is ticked. Recalls wait while RynthAi is in combat or buffing.\n" +
                "Kill Client (/rc quit after N minutes without a kill) restarts on luminance, your killing blows and\n" +
                "pet captures; kills by pets or fellows that pay you no luminance do NOT.\n" +
                "Needs RynthILT and a RynthAi that has this setting; the other settings are per character in RynthILT.",
                when: s => s.EnableVaultedDungeons.HasValue),
            Spacer(),
            Section("Nav Marker Display"),
            Bool("Breadcrumbs", s => s.TrackBreadcrumbs ?? true, (s, v) => s.TrackBreadcrumbs = v,
                BreadcrumbsTip + SameAsNavPanel, when: s => s.TrackBreadcrumbs.HasValue),
            Bool("Route Overlay", s => s.ShowRouteOverlay ?? true, (s, v) => s.ShowRouteOverlay = v,
                RouteOverlayTip + SameAsNavPanel, when: s => s.ShowRouteOverlay.HasValue),
            Float("Ring Thickness", s => s.NavRingThickness, (s, v) => s.NavRingThickness = v, 1f, 16f, 1f),
            Float("Line Thickness", s => s.NavLineThickness, (s, v) => s.NavLineThickness = v, 1f, 16f, 1f),
            Float("Height Offset", s => s.NavHeightOffset, (s, v) => s.NavHeightOffset = v, -5f, 5f, 0.05f,
                "Vertical offset for nav markers above the ground. Negative = lower."),
            Float("Slope Sink", s => s.NavSlopeSink, (s, v) => s.NavSlopeSink = v, 0f, 8f, 0.1f,
                "Extra downward offset on slopes only, per unit of terrain steepness. 0 = off; flat ground is unaffected. ~1.5 cancels the float for a default-radius ring."),
            Bool("Show Terrain Passability", s => s.ShowTerrainPassability, (s, v) => s.ShowTerrainPassability = v,
                "Highlight impassable terrain triangles in red."),
            Combo("Waypoint Labels", NavLabelModes, s => Math.Clamp(s.NavLabelMode, 0, NavLabelModes.Length - 1), (s, v) => s.NavLabelMode = v,
                "Text over the route's waypoints within 60 yd. Numbers: each point's number (as in the Nav panel).\nNumbers and names: also checkpoints and jumps, and what happens at a point\n(recall, chat, pause, portal, NPC, vendor) above it."),
            Bool("Guide Line", s => s.NavGuideLine, (s, v) => s.NavGuideLine = v,
                "A line from your character to the waypoint nav is heading for."),
            Spacer(),
            Section("Doors"),
            Bool("Open Doors While Navigating", s => s.OpenDoors, (s, v) => s.OpenDoors = v,
                "Automatically open doors encountered during navigation.", gates: true),
            Float("Door Detection Range (yd)", s => s.OpenDoorRange, (s, v) => s.OpenDoorRange = v, 0.1f, 70f, 1f, when: s => s.OpenDoors),
            Bool("Only Doors On My Path", s => s.OpenDoorsOnPathOnly, (s, v) => s.OpenDoorsOnPathOnly = v,
                "Open only a closed door that is on the way: within the range along the route's next legs (or straight ahead when not on a route).\nOff: every closed door within the range is opened.", when: s => s.OpenDoors),
            Bool("Auto-Unlock Doors", s => s.AutoUnlockDoors, (s, v) => s.AutoUnlockDoors = v,
                "If a door is locked, try to use a lockpick from your Consumable Items list.", when: s => s.OpenDoors),
            Spacer(),
            Section("Getting Back On Route"),
            Bool("Go To Closest Waypoint", s => s.NavResumeNearest, (s, v) => s.NavResumeNearest = v,
                "On: starting the macro, loading a Circular or Linear route, and carrying on after a fight, loot, buff or door\n" +
                "that moved you 10 yd or more all head for the closest waypoint on the route. A waypoint you have already\n" +
                "walked past is skipped for the one after it, so nav doesn't turn back. Once routes only look ahead.\n" +
                "Off: nav carries on to the waypoint it was heading for, and a loaded route starts at its first point."),
            Spacer(),
            Section("Dungeon Patrol"),
            Combo("Coverage", DunPatrolCoverages, s => Math.Clamp(s.DunPatrolCoverage, 0, DunPatrolCoverages.Length - 1), (s, v) => s.DunPatrolCoverage = v,
                "Which parts of a dungeon the Dungeon Patrol (/ra dunnav-patrol) walks.\n" +
                "Whole dungeon: every room and corridor it can reach, except small dead ends (Skip Dead Ends Up To).\n" +
                "Loops only: just the corridors that go round in a loop or join two junctions. Every dead-end branch is left\n" +
                "out, however long: quicker laps, but in a hive most of the dungeon is never visited.\n" +
                "Everything: every dead end too, closets and all.\n" +
                "Sweep: the same parts as Whole dungeon, but walked only close enough that every spot is within Sight\n" +
                "Distance of the route with no wall in between. For open halls and pillar rooms, where Whole dungeon walks\n" +
                "every lane: monsters a few lanes over notice you anyway. Corridors and crypts change little.\n" +
                "A dungeon that is one long corridor is walked end to end whatever this says."),
            Float("Sight Distance (yd)", s => s.DunPatrolSightYd, (s, v) => s.DunPatrolSightYd = Math.Clamp(v, 5f, 60f), 5f, 60f, 1f,
                "Sweep: how far away a monster notices you, so how far apart the patrol's lanes may be. Bigger means wider\n" +
                "lanes and a shorter lap; too big and monsters out of sight around pillars get missed. Default 20.",
                when: s => s.DunPatrolCoverage == 3),
            Int("Skip Dead Ends Up To (cells)", s => s.DunPatrolSkipDeadEnds, (s, v) => s.DunPatrolSkipDeadEnds = v, 0, 30, 1,
                "Dead-end areas this small are not walked into: a closet, a short corridor with a small room at its end,\n" +
                "a little loop off a corridor. Counted in dungeon cells (the pieces a dungeon is built from: a stretch of\n" +
                "corridor is one, a small room a few), the whole dead end together. In a longer dead end the patrol goes\n" +
                "in and turns round where only this much is left ahead. A room at least as big as Walk Into Rooms From\n" +
                "is never skipped. 0 = walk into every dead end. Default 10.",
                when: s => s.DunPatrolCoverage != 2),
            Bool("Avoid Lava/Acid Floors", s => s.DunPatrolAvoidFloors, (s, v) => s.DunPatrolAvoidFloors = v,
                "On: lava and acid floors (cells whose floor texture is in HazardTextures.txt) are left out of the patrol;\n" +
                "it turns round before them. Off: the patrol walks over them, for characters that don't mind the damage.\n" +
                "Pools seen as objects, and cells you marked as hazards, are avoided either way. Default on."),
            Float("Distance From Walls (yd)", s => s.DunPatrolWallDistance, (s, v) => s.DunPatrolWallDistance = Math.Clamp(v, 0.5f, 2f), 0.5f, 2f, 0.25f,
                "How far every patrol waypoint keeps from walls and ledges, where there is room for it. More keeps the\n" +
                "character off walls and corners in wide rooms and corridors; less lets the route hug the walls. In a\n" +
                "corridor narrower than twice this, the waypoints go down its middle. Default 1."),
            Float("Walk Into Rooms From (yd)", s => s.DunPatrolLookInSize, (s, v) => s.DunPatrolLookInSize = Math.Clamp(v, 3f, 40f), 3f, 40f, 1f,
                "A room the patrol would only pass the edge of (in and out by the same doorway, or by two doorways close\n" +
                "together) gets a walk to its middle and back, so monsters in it are seen, if it is at least this big\n" +
                "(across its corners). Smaller rooms are only looked into from the doorway. Once per room per lap. Default 10."),
            Note("Changing these rebuilds a running patrol from where you stand; the macro keeps running."),
            Spacer(),
            Section("Steering"),
            Float("Stop & Turn Angle", s => s.NavStopTurnAngle, (s, v) => s.NavStopTurnAngle = v, 1f, 90f, 1f,
                "Stop forward motion and turn in place when heading error exceeds this."),
            Float("Resume Run Angle", s => s.NavResumeTurnAngle, (s, v) => s.NavResumeTurnAngle = v, 1f, 45f, 1f,
                "Resume running once the turn-in-place error drops below this."),
            Float("Dead Zone", s => s.NavDeadZone, (s, v) => s.NavDeadZone = v, 0.5f, 20f, 0.5f,
                "Ignore heading corrections smaller than this."),
            Float("Sweep Detect Mult", s => s.NavSweepMult, (s, v) => s.NavSweepMult = v, 0.5f, 10f, 0.1f,
                "Closest-approach detection radius multiplier."),
            Float("Lookahead (yd)", s => s.NavLookaheadYards, (s, v) => s.NavLookaheadYards = MathF.Max(0f, v), 0f, 30f, 0.5f,
                "Within this distance of a waypoint, blend the aim point toward the next one to round corners. The nav point reach limits it: 2 yd per yard of reach over 1 yd (none up to a 2 yd reach). 0 = off."),
            Float("Turn Rate (deg/s)", s => s.NavTurnRateDegPerSec, (s, v) => s.NavTurnRateDegPerSec = v, 30f, 720f, 15f,
                "Max turn speed while navigating. Higher = snappier turns, lower = gentler."),
            Float("Post-Portal Delay (s)", s => s.PostPortalDelaySec, (s, v) => s.PostPortalDelaySec = MathF.Max(0f, v), 0f, 30f, 0.25f,
                "Seconds to settle after any portal/recall teleport before nav resumes."),
            Spacer(when: s => s.MovementMode == 2),
            Section("Tier 2 Tuning", when: s => s.MovementMode == 2),
            Float("Speed", s => s.T2Speed, (s, v) => s.T2Speed = v, 0.1f, 5f, 0.1f, when: s => s.MovementMode == 2),
            Float("Walk Within (yd)", s => s.T2WalkWithinYd, (s, v) => s.T2WalkWithinYd = v, 1f, 50f, 1f, when: s => s.MovementMode == 2),
            Float("Stop Distance (yd)", s => s.T2DistanceTo, (s, v) => s.T2DistanceTo = v, 0.1f, 10f, 0.1f, when: s => s.MovementMode == 2),
            Float("Reissue Timeout (ms)", s => s.T2ReissueMs, (s, v) => s.T2ReissueMs = v, 100f, 10000f, 100f, when: s => s.MovementMode == 2),
            Float("Max Range (yd)", s => s.T2MaxRangeYd, (s, v) => s.T2MaxRangeYd = v, 50f, 2000f, 50f, when: s => s.MovementMode == 2),
            Int("Max Landblock Dist", s => s.T2MaxLandblocks, (s, v) => s.T2MaxLandblocks = v, 1, 20, 1, when: s => s.MovementMode == 2),
        }),

        new("Buffing", Concat(
            Bool("Enable Buffing", s => s.EnableBuffing, (s, v) => s.EnableBuffing = v),
            Button("Buff Profiles", "Spells...", () => RynthAiCommands.ApplyRemoteCommand("spells", "show"),
                "Open the RynthAi Spells window: browse the spell list by school, level and target,\nand build the buff profile buffing uses (pick it under Loaded Files > Buffs).\nAlso: /ra spells, /ra buffprofile <name>|none."),
            Bool("Rebuff When Idle", s => s.RebuffWhenIdle, (s, v) => s.RebuffWhenIdle = v),
            Int("Rebuff With (seconds left)", s => s.RebuffSecondsRemaining, (s, v) => s.RebuffSecondsRemaining = v, 30, 1800, 30,
                "Recast a self buff when its remaining duration drops below this value.\nDefault 300 (5 minutes). Lower values rebuff more eagerly."),
            Int("Also Refresh Under (seconds left)", s => s.RebuffTopOffSecondsRemaining, (s, v) => s.RebuffTopOffSecondsRemaining = v, 30, 3600, 60,
                "When a buff is due, also recast every other buff with less than this much time left,\nso they land together. Buffs with more time are left alone. Default 1200 (20 minutes).\nAt or below 'Rebuff With', only the expiring buff is recast."),
            Bool("Cast buffs even when an item gives them", s => s.CastBuffsOverItemBuffs, (s, v) => s.CastBuffsOverItemBuffs = v,
                "Cast your own buff even when worn gear already gives that buff.\nFor servers with buff augments, where your own cast is stronger or lasts longer.\nOn by default: on retail it costs a few extra casts."),
            Spacer(),
            Section("Protections (untick to skip)"),
            BuffToggles(ProtToggles),
            Spacer(),
            Section("Banes (untick to skip)"),
            BuffToggles(BaneToggles),
            Spacer(),
            Section("Buff Difficulty (Min Buffed Skill)"),
            Tiers(
                new Func<RynthAiSettings, int>[] { s => s.BuffMinSkillLevelTier1, s => s.BuffMinSkillLevelTier2, s => s.BuffMinSkillLevelTier3, s => s.BuffMinSkillLevelTier4,
                                                   s => s.BuffMinSkillLevelTier5, s => s.BuffMinSkillLevelTier6, s => s.BuffMinSkillLevelTier7, s => s.BuffMinSkillLevelTier8 },
                new Action<RynthAiSettings, int>[] { (s, v) => s.BuffMinSkillLevelTier1 = v, (s, v) => s.BuffMinSkillLevelTier2 = v, (s, v) => s.BuffMinSkillLevelTier3 = v, (s, v) => s.BuffMinSkillLevelTier4 = v,
                                                     (s, v) => s.BuffMinSkillLevelTier5 = v, (s, v) => s.BuffMinSkillLevelTier6 = v, (s, v) => s.BuffMinSkillLevelTier7 = v, (s, v) => s.BuffMinSkillLevelTier8 = v }))),

        new("Crafting", new[]
        {
            Section("Missile Ammo Crafting"),
            Bool("Enable Missile Crafting", s => s.EnableMissileCrafting, (s, v) => s.EnableMissileCrafting = v,
                "Auto-manage missile ammo when the ammo slot is empty or low.", gates: true),
            Note("(Disabled)", when: s => !s.EnableMissileCrafting),
            Spacer(when: s => s.EnableMissileCrafting),
            new SettingRow(SettingKind.CraftingStatus, "State:", VisibleWhen: s => s.EnableMissileCrafting && !string.IsNullOrEmpty(s.MissileCraftingState)),
        }),

        new("Looting", new[]
        {
            Bool("Enable Looting", s => s.EnableLooting, (s, v) => s.EnableLooting = v),
            Bool("Boost Loot Priority", s => s.BoostLootPriority, (s, v) => s.BoostLootPriority = v),
            Bool("Loot Only Rare Corpses", s => s.LootOnlyRareCorpses, (s, v) => s.LootOnlyRareCorpses = v),
            Bool("Learn Unknown Spells", s => s.ReadUnknownScrolls, (s, v) => s.ReadUnknownScrolls = v,
                "Loots scrolls of spells you don't know and can learn (magic school trained or\nspecialized, skill high enough), and reads them when it's safe. Scrolls already\nin your pack are read too. Needs Enable Looting for corpses."),
            Bool("Stop Looting When Pack Full", s => s.StopLootingWhenPackFull, (s, v) => s.StopLootingWhenPackFull = v,
                "Don't start on another corpse when no pack has a free slot (says so once in chat)."),
            Bool("Jump When Looting", s => s.LootJumpEnabled, (s, v) => s.LootJumpEnabled = v, gates: true),
            Int("Jump Height", s => s.LootJumpHeight, (s, v) => s.LootJumpHeight = v, 1, 100, 5, when: s => s.LootJumpEnabled),
            Bool("Loot Items On Ground", s => s.EnableGroundLoot, (s, v) => s.EnableGroundLoot = v,
                "Pick up loose items on the ground that match your loot profile, within Corpse Max range.\nCorpses always come first. Status and rescan: /ra groundloot."),
            Spacer(),
            Section("Corpse Ownership"),
            Combo("Loot From", LootOwnershipModes, s => s.LootOwnership, (s, v) => s.LootOwnership = v),
            Bool("Recover My Corpse", s => s.LootOwnCorpse, (s, v) => s.LootOwnCorpse = v,
                "When your own corpse (\"Corpse of <you>\") is within loot range, open it and take every\nitem on it, whatever the loot profile and Loot From say. Needs Enable Looting.\n/ra owncorpse says where your last corpse is."),
            Bool("Travel Back To My Corpse", s => s.TravelToOwnCorpse, (s, v) => s.TravelToOwnCorpse = v,
                "After a death, walk back to where you died with RynthNav (outdoor deaths only),\nfighting on the way as usual, recover the corpse, then carry on. Needs the macro\nrunning and RynthNav loaded. /ra owncorpse stop cancels."),
            Spacer(),
            Section("Inventory Management"),
            Bool("Enable Autostack", s => s.EnableAutostack, (s, v) => s.EnableAutostack = v),
            Bool("Enable Autocram", s => s.EnableAutocram, (s, v) => s.EnableAutocram = v,
                "Automatically moves items from your main pack into side packs.\nNote: recently used weapons stay in the main pack."),
            Bool("Combine Salvage Bags", s => s.EnableCombineSalvage, (s, v) => s.EnableCombineSalvage = v,
                "After the salvage queue empties, move same-name bags together so the server merges them."),
            Bool("Combine Bags During Salvage", s => s.CombineBagsDuringSalvage, (s, v) => s.CombineBagsDuringSalvage = v,
                "When salvaging an item, also add any under-full salvage bag of the same material to the salvage panel."),
            Button("Floating HUDs", "Inventory HUDs...", () => RynthAiCommands.ApplyRemoteCommand("huds", "show"),
                "Opens the floating pack item count HUD / Mini Remote setup (/ra huds).\nThe Item Count panel is separate (/rc itemcount; Inventory's Count button)."),
            Bool("Salvage Without The Panel", s => s.SalvageWithoutPanel, (s, v) => s.SalvageWithoutPanel = v,
                "Salvage (and combine bags) by sending the salvage request to the server directly:\nAC's salvage panel never opens. When a request changes nothing, that batch is tried once\nthrough the panel. Needs an engine with plugin API v82; otherwise the panel is used."),
            Spacer(),
            Section("Loot Timers (ms)"),
            EditButton("Optimized defaults", "Use optimized defaults", LootTimerDefaults.Apply,
                "Set the six loot timers RynthAi's corpse looting reads (everything here except Inter-Item\n" +
                "Delay and Closing Delay) to the optimized defaults, measured from live looting on ACE\n" +
                "(226 corpses): Content Settle 100, Empty Corpse Wait 300, Assess Window 300,\n" +
                "Loot Retry Timeout 1000, Corpse Open Retry 1500, Corpse Timeout 12000."),
            Int("Inter-Item Delay", s => s.LootInterItemDelayMs, (s, v) => s.LootInterItemDelayMs = v, 0, 5000, 25),
            Int("Content Settle", s => s.LootContentSettleMs, (s, v) => s.LootContentSettleMs = v, 0, 5000, 25,
                "Wait after an open for the container's items. Only used when the items\naren't already known; on ACE they always were. Optimized default: 100."),
            Int("Empty Corpse Wait", s => s.LootEmptyCorpseMs, (s, v) => s.LootEmptyCorpseMs = v, 0, 5000, 25,
                "Wait for a corpse's contents before probing it or calling it empty. Optimized default: 300."),
            Int("Closing Delay", s => s.LootClosingDelayMs, (s, v) => s.LootClosingDelayMs = v, 0, 5000, 25),
            Int("Assess Window", s => s.LootAssessWindowMs, (s, v) => s.LootAssessWindowMs = v, 0, 5000, 25,
                "Hold items for their appraisal. Measured replies: 127 ms typical,\n256 ms at the 95th percentile, 350 ms at most. Optimized default: 300."),
            Int("Loot Retry Timeout", s => s.LootRetryTimeoutMs, (s, v) => s.LootRetryTimeoutMs = v, 0, 10000, 100,
                "Wait, once the character is still, before resending a pickup that hasn't\nlanded. Too short resends pickups the server is still doing. Optimized default: 1000."),
            Int("Corpse Open Retry", s => s.LootOpenRetryMs, (s, v) => s.LootOpenRetryMs = v, 0, 10000, 100,
                "Wait before sending another open to a corpse that hasn't opened. Optimized default: 1500."),
            Int("Corpse Timeout", s => s.LootCorpseTimeoutMs, (s, v) => s.LootCorpseTimeoutMs = v, 0, 60000, 500,
                "Give up on a claimed corpse after this long. The clock stops during combat\nand buffing. Optimized default: 12000."),
            Spacer(),
            Section("Salvage Timers (ms) - First / Fast"),
            Int("Open (First)", s => s.SalvageOpenDelayFirstMs, (s, v) => s.SalvageOpenDelayFirstMs = v, 0, 5000, 50),
            Int("Open (Fast)", s => s.SalvageOpenDelayFastMs, (s, v) => s.SalvageOpenDelayFastMs = v, 0, 2000, 25),
            Int("Add Item (First)", s => s.SalvageAddDelayFirstMs, (s, v) => s.SalvageAddDelayFirstMs = v, 0, 5000, 50),
            Int("Add Item (Fast)", s => s.SalvageAddDelayFastMs, (s, v) => s.SalvageAddDelayFastMs = v, 0, 2000, 25),
            Int("Salvage Click", s => s.SalvageSalvageDelayMs, (s, v) => s.SalvageSalvageDelayMs = v, 0, 2000, 25),
            Int("Result (First)", s => s.SalvageResultDelayFirstMs, (s, v) => s.SalvageResultDelayFirstMs = v, 0, 5000, 50),
            Int("Result (Fast)", s => s.SalvageResultDelayFastMs, (s, v) => s.SalvageResultDelayFastMs = v, 0, 2000, 25),
        }),

        new("Vendoring", new[]
        {
            Section("AutoVendor"),
            Bool("Enabled", s => s.AutoVendorEnabled, (s, v) => s.AutoVendorEnabled = v,
                "Buy and sell by a loot profile when a vendor opens (and allow /ub autovendor).\nProfile: <Vendor Name>.utl, else default.utl, in the AutoVendor folder."),
            Bool("Test Mode (only print what it would do)", s => s.AutoVendorTestMode, (s, v) => s.AutoVendorTestMode = v,
                "Lists what would be bought and sold without trading. Leave this on until the lists look right."),
            Bool("Buy", s => s.AutoVendorEnableBuying, (s, v) => s.AutoVendorEnableBuying = v),
            Bool("Sell", s => s.AutoVendorEnableSelling, (s, v) => s.AutoVendorEnableSelling = v),
            Bool("Only Sell From Main Pack", s => s.AutoVendorOnlyFromMainPack, (s, v) => s.AutoVendorOnlyFromMainPack = v),
            Bool("Show Merchant Info", s => s.AutoVendorShowMerchantInfo, (s, v) => s.AutoVendorShowMerchantInfo = v,
                "Print the vendor's buy/sell rates and max value when it opens."),
            Bool("Think When Finished", s => s.AutoVendorThink, (s, v) => s.AutoVendorThink = v,
                "Send 'AutoVendor finished: <vendor>' (and failures) as a /tell to yourself, for metas."),
            Spacer(),
            Section("/ub vendor open"),
            Int("Tries", s => s.AutoVendorTries, (s, v) => s.AutoVendorTries = v, 1, 20, 1),
            Int("Time Between Tries (ms)", s => s.AutoVendorTriesTime, (s, v) => s.AutoVendorTriesTime = v, 500, 30000, 250),
            Spacer(),
            Note("Never sold: equipped, attuned, bonded, retained, tinkered, imbued, inscribed, rare, zero value, packs, or anything a Keep rule could match."),
        }),

        new("Diagnostics", new[]
        {
            Section("Logging & Debugging"),
            Bool("Debug to chat", s => s.DiagDebugToChat, (s, v) => s.DiagDebugToChat = v,
                "Echo trace lines and errors from the categories ticked below into the chat window.\nAlso: /ra debug on|off."),
            Bool("Daily RynthAi log file", s => s.DiagFileLogAll, (s, v) => s.DiagFileLogAll = v,
                "Mirror every RynthAi log line to rynthai_<date>.txt in the log folder (kept 7 days).\nAlso: /ra logs file on|off."),
            Button("Log Folder", "Open", () => RynthAiCommands.ApplyRemoteCommand("logs", "open"),
                "Open RynthAi's Logs\\Diagnostics folder in Explorer (/ra logs open)."),
            Button("Old Log Files", "Prune", () => RynthAiCommands.ApplyRemoteCommand("logs", "prune"),
                "Delete diagnostics files past the retention limit now (/ra logs prune)."),
            Spacer(),
            Section("Trace Per Category"),
            new SettingRow(SettingKind.TraceCategories, "Categories",
                "Ticked categories write their trace lines to Trace\\<Category>_<date>.txt (/ra trace <category> on|off)."),
            Note("+log = the category is at Info: its trace lines also go to the normal log. The launcher's Logging page sets Off, Trace or Info."),
            Note("Exceptions always go to exceptions_<date>.txt with full stack traces (throttled). Settings here are for this PC, not the profile."),
            Spacer(),
            Section("Server"),
            new SettingRow(SettingKind.ServerPick, "Server",
                "Which server this character counts as. Auto detects it (the connect host, the world name the server announces, RynthILT).\n"
                + "Picking a server forces it on and every other server off, for this character; every plugin follows at once.\n"
                + "Also: /rc server pick <id>|auto, and /rc server for the list."),
            Note("The list is every server the engine knows: the built-in ones and those declared by the loaded server plugins. \"Settings from\" is the plugin the others ask for that server's settings."),
        }),
    };
}
