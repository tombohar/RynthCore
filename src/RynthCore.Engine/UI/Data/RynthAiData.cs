// ============================================================================
//  RynthCore.Engine - UI/Data/RynthAiData.cs
//  The RynthAi dashboard's data (docs/IMGUI_PARITY_PLAN.md §2.1), shared by
//  its Avalonia face (UI/Panels/RynthAiPanel.cs) and ImGui face.
//
//  RynthAiSource polls RynthPluginGetSnapshotJson at 33 ms on the pump thread
//  (the same thread as RynthAi's tick, so no race with the plugin) and
//  publishes a RynthAiView: the raw fields plus every display string, sticky
//  vitals applied. RynthAiCommands runs the dashboard's mutating exports on
//  the pump too. PatrolSource fetches the patrol flyout's data on demand.
// ============================================================================

using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.UI.Data;

/// <summary>RynthPluginGetSnapshotJson's fields the dashboard uses.</summary>
internal sealed class RynthAiSnapshot
{
    [JsonPropertyName("macroRunning")]    public bool MacroRunning { get; set; }
    [JsonPropertyName("currentState")]    public string CurrentState { get; set; } = string.Empty;
    [JsonPropertyName("botAction")]       public string BotAction { get; set; } = string.Empty;
    [JsonPropertyName("selectedProfile")] public string SelectedProfile { get; set; } = "Default";
    [JsonPropertyName("profiles")]        public string[] Profiles { get; set; } = Array.Empty<string>();
    [JsonPropertyName("navProfiles")]     public string[] NavProfiles { get; set; } = Array.Empty<string>();
    [JsonPropertyName("lootProfiles")]    public string[] LootProfiles { get; set; } = Array.Empty<string>();
    [JsonPropertyName("metaProfiles")]    public string[] MetaProfiles { get; set; } = Array.Empty<string>();
    [JsonPropertyName("currentNavName")]  public string CurrentNavName { get; set; } = string.Empty;
    [JsonPropertyName("currentLootName")] public string CurrentLootName { get; set; } = string.Empty;
    [JsonPropertyName("currentLootPath")] public string CurrentLootPath { get; set; } = string.Empty;
    // Set while a vendor is open: the AutoVendor profile it would use (may not exist yet).
    [JsonPropertyName("vendorProfilePath")] public string VendorProfilePath { get; set; } = string.Empty;
    [JsonPropertyName("currentMetaName")] public string CurrentMetaName { get; set; } = string.Empty;
    [JsonPropertyName("selectedNavIdx")]  public int SelectedNavIdx { get; set; }
    [JsonPropertyName("selectedLootIdx")] public int SelectedLootIdx { get; set; }
    [JsonPropertyName("selectedMetaIdx")] public int SelectedMetaIdx { get; set; }
    [JsonPropertyName("selectedProfileIdx")] public int SelectedProfileIdx { get; set; }
    // Buff profiles (RynthAi 0.5.31+): "Built-in" first, then BuffProfiles\*.json. Older plugins
    // send none, and the Buffs picker then shows "Built-in" with nothing to pick.
    [JsonPropertyName("buffProfiles")]    public string[] BuffProfiles { get; set; } = Array.Empty<string>();
    [JsonPropertyName("currentBuffName")] public string CurrentBuffName { get; set; } = string.Empty;
    [JsonPropertyName("selectedBuffIdx")] public int SelectedBuffIdx { get; set; }
    [JsonPropertyName("combatEnabled")]     public bool CombatEnabled { get; set; }
    [JsonPropertyName("buffingEnabled")]    public bool BuffingEnabled { get; set; }
    [JsonPropertyName("navigationEnabled")] public bool NavigationEnabled { get; set; }
    [JsonPropertyName("lootingEnabled")]    public bool LootingEnabled { get; set; }
    [JsonPropertyName("metaEnabled")]       public bool MetaEnabled { get; set; }
    [JsonPropertyName("currentTargetId")]   public uint CurrentTargetId { get; set; }
    [JsonPropertyName("targetLabel")]       public string TargetLabel { get; set; } = "NO TARGET";
    [JsonPropertyName("targetHealthPercent")] public float TargetHealthPercent { get; set; }
    [JsonPropertyName("targetHealthDisplay")] public string TargetHealthDisplay { get; set; } = "0";
    [JsonPropertyName("targetHealth")]    public uint TargetHealth { get; set; }
    [JsonPropertyName("targetMaxHealth")] public uint TargetMaxHealth { get; set; }
    [JsonPropertyName("targetStamina")]   public uint TargetStamina { get; set; }
    [JsonPropertyName("targetMaxStamina")] public uint TargetMaxStamina { get; set; }
    [JsonPropertyName("targetMana")]      public uint TargetMana { get; set; }
    [JsonPropertyName("targetMaxMana")]   public uint TargetMaxMana { get; set; }
    [JsonPropertyName("playerHealth")]    public uint PlayerHealth { get; set; }
    [JsonPropertyName("playerMaxHealth")] public uint PlayerMaxHealth { get; set; }
    [JsonPropertyName("playerStamina")]   public uint PlayerStamina { get; set; }
    [JsonPropertyName("playerMaxStamina")] public uint PlayerMaxStamina { get; set; }
    [JsonPropertyName("playerMana")]      public uint PlayerMana { get; set; }
    [JsonPropertyName("playerMaxMana")]   public uint PlayerMaxMana { get; set; }
    [JsonPropertyName("showTargetStaminaMana")] public bool ShowTargetStaminaMana { get; set; }
    [JsonPropertyName("isLocked")]    public bool IsLocked { get; set; }
    [JsonPropertyName("isMinimized")] public bool IsMinimized { get; set; }
    [JsonPropertyName("bgOpacity")]   public float BgOpacity { get; set; } = 0.95f;
}

[JsonSerializable(typeof(RynthAiSnapshot))]
internal sealed partial class RynthAiSnapshotJsonContext : JsonSerializerContext { }

/// <summary>A vital bar: fill fraction and its text ("HP: 92% (314/341)").</summary>
internal readonly record struct VitalView(float Fraction, string Text);

/// <summary>Everything either dashboard face draws, formatted on the pump thread.</summary>
internal sealed class RynthAiView
{
    public required RynthAiSnapshot Raw { get; init; }
    public required string Version { get; init; }
    public required string MetaStateText { get; init; }
    public required string BotActivityText { get; init; }
    public required string ProfileText { get; init; }
    public required string NavText { get; init; }
    public required string LootText { get; init; }
    public required string MetaText { get; init; }
    /// <summary>Buff profile buffing uses ("Built-in" without one).</summary>
    public required string BuffText { get; init; }
    public required string TargetHeadline { get; init; }
    public required int TargetSegmentsLit { get; init; }
    public required bool ShowTargetSubBars { get; init; }
    public required VitalView TargetStamina { get; init; }
    public required VitalView TargetMana { get; init; }
    public required VitalView Health { get; init; }
    public required VitalView Stamina { get; init; }
    public required VitalView Mana { get; init; }
    /// <summary>Combat box background alpha 0-255 (the header +/- chips; floor 10%).</summary>
    public required byte PanelAlpha { get; init; }

    public const int Segments = 15;
}

/// <summary>RynthPluginGetSnapshotJson at 33 ms while a dashboard face is open.</summary>
internal sealed unsafe class RynthAiSource : UiSource<RynthAiView>
{
    private delegate* unmanaged[Cdecl]<IntPtr> _getSnapshotJson;
    private string? _lastJson;
    private string _version = "";
    private long _versionTicks;

    public RynthAiSource() : base("RynthAi", periodMs: 33) { }

    protected internal override void Poll()
    {
        if (_getSnapshotJson == null)
            _getSnapshotJson = (delegate* unmanaged[Cdecl]<IntPtr>)PluginExportBinder.Resolve("RynthAi", "RynthPluginGetSnapshotJson");
        if (_getSnapshotJson == null) return;

        long now = Environment.TickCount64;
        bool versionChanged = false;
        if (now - _versionTicks > 2000)
        {
            _versionTicks = now;
            string ver = "";
            foreach (LoadedPlugin p in PluginManager.Plugins)
                if (p.DisplayName.Contains("RynthAi", StringComparison.OrdinalIgnoreCase)) { ver = p.VersionString; break; }
            versionChanged = ver != _version;
            _version = ver;
        }

        IntPtr ptr = _getSnapshotJson();
        string? json = ptr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ptr);
        if (string.IsNullOrWhiteSpace(json)) return;
        if (json == _lastJson && !versionChanged) return;
        _lastJson = json;

        RynthAiSnapshot snap = JsonSerializer.Deserialize(json, RynthAiSnapshotJsonContext.Default.RynthAiSnapshot) ?? new RynthAiSnapshot();
        Publish(BuildView(snap, _version));
    }

    protected internal override void Reset()
    {
        _getSnapshotJson = null;
        _lastJson = null;
        ClearSnapshot();
    }

    private static RynthAiView BuildView(RynthAiSnapshot s, string version)
    {
        StickyVitals.Update(s);
        string label = string.IsNullOrWhiteSpace(s.TargetLabel) ? "NO TARGET" : s.TargetLabel.ToUpperInvariant();
        bool showSub = s.ShowTargetStaminaMana && s.TargetMaxStamina > 0;
        float pct = Math.Clamp(s.TargetHealthPercent, 0f, 1f);
        int lit = 0;
        for (int i = 0; i < RynthAiView.Segments; i++)
            if ((float)i / RynthAiView.Segments <= pct) lit++;

        return new RynthAiView
        {
            Raw = s,
            Version = version,
            MetaStateText = string.IsNullOrWhiteSpace(s.CurrentState) ? "Default" : s.CurrentState,
            BotActivityText = string.IsNullOrWhiteSpace(s.BotAction) || s.BotAction == "Default" ? "Idle" : s.BotAction,
            ProfileText = Truncate(s.SelectedProfile, 16),
            NavText = Truncate(s.CurrentNavName, 16),
            LootText = Truncate(s.CurrentLootName, 16),
            MetaText = Truncate(s.CurrentMetaName, 16),
            BuffText = Truncate(string.IsNullOrWhiteSpace(s.CurrentBuffName) ? "Built-in" : s.CurrentBuffName, 16),
            TargetHeadline = Truncate(label, 32),
            TargetSegmentsLit = lit,
            ShowTargetSubBars = showSub,
            TargetStamina = showSub ? Compact("ST", s.TargetStamina, s.TargetMaxStamina) : default,
            TargetMana = showSub ? Compact("MN", s.TargetMana, s.TargetMaxMana) : default,
            Health = Vital("HP", StickyVitals.Hp, StickyVitals.MaxHp),
            Stamina = Vital("ST", StickyVitals.St, StickyVitals.MaxSt),
            Mana = Vital("MN", StickyVitals.Mn, StickyVitals.MaxMn),
            PanelAlpha = (byte)Math.Clamp(Math.Round(s.BgOpacity * 255f), 25.5, 255),
        };
    }

    private static VitalView Vital(string label, uint v, uint max)
    {
        float pct = max == 0 ? 0 : Math.Clamp((float)v / max, 0, 1);
        string display = max == 0 ? (v == 0 ? "--/--" : $"{v}/--") : $"{v}/{max}";
        return new VitalView(pct, $"{label}: {(int)(pct * 100)}% ({display})");
    }

    private static VitalView Compact(string prefix, uint v, uint max) =>
        new(max == 0 ? 0 : Math.Clamp((float)v / max, 0, 1), $"{prefix} {v}/{max}");

    internal static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return "None";
        return value.Length > max ? value[..(max - 1)] + "…" : value;
    }
}

/// <summary>
/// Last-known good vitals, so a hot reload (when the plugin's vital cache is
/// cold for a tick or two) doesn't show 0/0. Persisted to vitals.cache, at
/// most once a second, off the pump thread.
/// </summary>
internal static class StickyVitals
{
    public static uint Hp, MaxHp, St, MaxSt, Mn, MaxMn;
    private static bool _loaded;
    private static long _lastSaved;

    private static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RynthCore", "vitals.cache");

    /// <summary>Pump thread (RynthAiSource.Poll).</summary>
    public static void Update(RynthAiSnapshot s)
    {
        if (!_loaded) Load();
        bool changed = false;
        if (s.PlayerMaxHealth != 0)  { changed |= Hp != s.PlayerHealth  || MaxHp != s.PlayerMaxHealth;  Hp = s.PlayerHealth;  MaxHp = s.PlayerMaxHealth; }
        if (s.PlayerMaxStamina != 0) { changed |= St != s.PlayerStamina || MaxSt != s.PlayerMaxStamina; St = s.PlayerStamina; MaxSt = s.PlayerMaxStamina; }
        if (s.PlayerMaxMana != 0)    { changed |= Mn != s.PlayerMana    || MaxMn != s.PlayerMaxMana;    Mn = s.PlayerMana;    MaxMn = s.PlayerMaxMana; }
        if (!changed) return;

        long now = Environment.TickCount64;
        if (now - _lastSaved < 1000) return;
        _lastSaved = now;
        string text = $"{Hp},{MaxHp},{St},{MaxSt},{Mn},{MaxMn}";
        UiBackgroundWriter.Enqueue("vitals cache", () =>
        {
            try
            {
                string path = CachePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
            }
            catch { }
        });
    }

    private static void Load()
    {
        _loaded = true;
        try
        {
            string path = CachePath;
            if (!File.Exists(path)) return;
            string[] p = File.ReadAllText(path).Split(',');
            if (p.Length < 6) return;
            CultureInfo ic = CultureInfo.InvariantCulture;
            Hp = uint.Parse(p[0], ic); MaxHp = uint.Parse(p[1], ic);
            St = uint.Parse(p[2], ic); MaxSt = uint.Parse(p[3], ic);
            Mn = uint.Parse(p[4], ic); MaxMn = uint.Parse(p[5], ic);
        }
        catch { }
    }
}

/// <summary>
/// The dashboard's mutating exports, run on the pump thread (UiDataHub.Post).
/// Any thread may call these; each refreshes the snapshot afterwards.
/// </summary>
internal static unsafe class RynthAiCommands
{
    private static delegate* unmanaged[Cdecl]<void> _toggleMacro;
    private static delegate* unmanaged[Cdecl]<int, int, void> _setSubsystemEnabled;
    private static delegate* unmanaged[Cdecl]<int, int, void> _selectProfile;
    private static delegate* unmanaged[Cdecl]<void> _forceRebuff;
    private static delegate* unmanaged[Cdecl]<void> _cancelForceRebuff;
    private static delegate* unmanaged[Cdecl]<float, void> _adjustOpacity;
    private static delegate* unmanaged[Cdecl]<void> _togglePanelLock;
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _sendNavCommand;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> _applyRemoteCommand;

    static RynthAiCommands()
    {
        PluginManager.PluginsUnloaded += () =>
        {
            _toggleMacro = null; _setSubsystemEnabled = null; _selectProfile = null; _forceRebuff = null;
            _cancelForceRebuff = null; _adjustOpacity = null; _togglePanelLock = null; _sendNavCommand = null;
            _applyRemoteCommand = null;
        };
    }

    private static IntPtr Export(string name) => PluginExportBinder.Resolve("RynthAi", name);

    private static void Run(string label, Action action)
    {
        UiDataHub.Post("RynthAi " + label, () =>
        {
            action();
            UiSources.RynthAi.RequestRefresh();
        });
    }

    public static void ToggleMacro() => Run("toggle macro", () =>
    {
        if (_toggleMacro == null) _toggleMacro = (delegate* unmanaged[Cdecl]<void>)Export("RynthPluginToggleMacro");
        if (_toggleMacro != null) _toggleMacro();
    });

    /// <summary>0 Combat, 1 Buff, 2 Nav, 3 Loot, 4 Meta.</summary>
    public static void SetSubsystemEnabled(int id, bool on) => Run("subsystem", () =>
    {
        if (_setSubsystemEnabled == null) _setSubsystemEnabled = (delegate* unmanaged[Cdecl]<int, int, void>)Export("RynthPluginSetSubsystemEnabled");
        if (_setSubsystemEnabled != null) _setSubsystemEnabled(id, on ? 1 : 0);
    });

    /// <summary>
    /// Flips subsystem <paramref name="id"/> (0 Combat, 1 Buff, 2 Nav, 3 Loot, 4 Meta) from RynthAi's
    /// state right now (a hotkey: the dashboard may be closed, so its snapshot is read fresh here).
    /// </summary>
    public static void ToggleSubsystem(int id) => Run("toggle subsystem", () =>
    {
        UiSources.RynthAi.Poll();   // pump thread, like the hub's own polls
        RynthAiSnapshot? raw = UiSources.RynthAi.Current?.Value.Raw;
        if (raw == null) return;
        bool on = id switch
        {
            0 => raw.CombatEnabled,
            1 => raw.BuffingEnabled,
            2 => raw.NavigationEnabled,
            3 => raw.LootingEnabled,
            _ => raw.MetaEnabled,
        };
        if (_setSubsystemEnabled == null) _setSubsystemEnabled = (delegate* unmanaged[Cdecl]<int, int, void>)Export("RynthPluginSetSubsystemEnabled");
        if (_setSubsystemEnabled != null) _setSubsystemEnabled(id, on ? 0 : 1);
    });

    /// <summary>kind: 0 nav, 1 loot, 2 meta, 3 profile.</summary>
    public static void SelectProfile(int kind, int index) => Run("select profile", () =>
    {
        if (_selectProfile == null) _selectProfile = (delegate* unmanaged[Cdecl]<int, int, void>)Export("RynthPluginSelectProfile");
        if (_selectProfile != null) _selectProfile(kind, index);
    });

    public static void ForceRebuff() => Run("force rebuff", () =>
    {
        if (_forceRebuff == null) _forceRebuff = (delegate* unmanaged[Cdecl]<void>)Export("RynthPluginForceRebuff");
        if (_forceRebuff != null) _forceRebuff();
    });

    public static void CancelForceRebuff() => Run("cancel force rebuff", () =>
    {
        if (_cancelForceRebuff == null) _cancelForceRebuff = (delegate* unmanaged[Cdecl]<void>)Export("RynthPluginCancelForceRebuff");
        if (_cancelForceRebuff != null) _cancelForceRebuff();
    });

    public static void AdjustOpacity(float delta) => Run("opacity", () =>
    {
        if (_adjustOpacity == null) _adjustOpacity = (delegate* unmanaged[Cdecl]<float, void>)Export("RynthPluginAdjustOpacity");
        if (_adjustOpacity != null) _adjustOpacity(delta);
    });

    public static void TogglePanelLock() => Run("lock", () =>
    {
        if (_togglePanelLock == null) _togglePanelLock = (delegate* unmanaged[Cdecl]<void>)Export("RynthPluginTogglePanelLock");
        if (_togglePanelLock != null) _togglePanelLock();
    });

    /// <summary>RynthPluginSendNavCommand with a JSON command ({"Cmd":"dunPatrol"} ...).</summary>
    public static void SendNavCommand(string json, bool refreshPatrol = false) => Run("nav command", () =>
    {
        if (_sendNavCommand == null) _sendNavCommand = (delegate* unmanaged[Cdecl]<IntPtr, void>)Export("RynthPluginSendNavCommand");
        if (_sendNavCommand == null) return;
        IntPtr p = Marshal.StringToHGlobalAnsi(json);
        try { _sendNavCommand(p); }
        finally { Marshal.FreeHGlobal(p); }
        if (refreshPatrol) UiSources.Patrol.RequestRefresh();
        UiSources.Nav.RequestRefresh();   // the plugin applied it: the Nav panels show it next poll
        UiSources.Settings.RequestRefresh();   // Breadcrumbs / Route overlay are on Settings > Navigation too
    });

    /// <summary>
    /// RynthPluginApplyRemoteCommand(action, value) - the same (action, value) pairs as the
    /// plugin's /ra chat commands ("huds","show"; "remote","toggle"; "hub","show" ...).
    /// Older RynthAi builds without the export drop the command (logged once per bind miss).
    /// </summary>
    public static void ApplyRemoteCommand(string action, string value) => Run("remote command " + action, () =>
    {
        if (_applyRemoteCommand == null) _applyRemoteCommand = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)Export("RynthPluginApplyRemoteCommand");
        if (_applyRemoteCommand == null)
        {
            RynthLog.UI($"RynthAiCommands: RynthPluginApplyRemoteCommand not exported; dropped '{action} {value}'.");
            return;
        }
        IntPtr a = Marshal.StringToHGlobalAnsi(action);
        IntPtr b = Marshal.StringToHGlobalAnsi(value);
        try { _applyRemoteCommand(a, b); }
        finally
        {
            Marshal.FreeHGlobal(a);
            Marshal.FreeHGlobal(b);
        }
    });
}

/// <summary>RynthPluginGetPatrolInfoJson's payload (patrol flyout).</summary>
internal sealed class PatrolInfo
{
    [JsonPropertyName("inDungeon")]        public bool InDungeon { get; set; }
    [JsonPropertyName("currentLandblock")] public string CurrentLandblock { get; set; } = "0000";
    [JsonPropertyName("currentHazards")]   public int CurrentHazards { get; set; }
    [JsonPropertyName("liveHazards")]      public int LiveHazards { get; set; }
    [JsonPropertyName("dungeons")]         public DungeonHazards[] Dungeons { get; set; } = Array.Empty<DungeonHazards>();
    [JsonPropertyName("routes")]           public string[] Routes { get; set; } = Array.Empty<string>();
}

internal sealed class DungeonHazards
{
    [JsonPropertyName("landblock")] public string Landblock { get; set; } = "0000";
    [JsonPropertyName("cells")]     public int Cells { get; set; }
}

[JsonSerializable(typeof(PatrolInfo))]
internal sealed partial class PatrolInfoJsonContext : JsonSerializerContext { }

/// <summary>
/// RynthPluginGetPatrolInfoJson (single static buffer: one caller only) while
/// the patrol flyout is open, and right after each patrol action.
/// </summary>
internal sealed unsafe class PatrolSource : UiSource<PatrolInfo>
{
    private delegate* unmanaged[Cdecl]<IntPtr> _getPatrolInfoJson;

    public PatrolSource() : base("Patrol", periodMs: 2000) { }

    protected internal override void Poll()
    {
        if (_getPatrolInfoJson == null)
            _getPatrolInfoJson = (delegate* unmanaged[Cdecl]<IntPtr>)PluginExportBinder.Resolve("RynthAi", "RynthPluginGetPatrolInfoJson");
        if (_getPatrolInfoJson == null) return;
        IntPtr p = _getPatrolInfoJson();
        string json = p == IntPtr.Zero ? "{}" : Marshal.PtrToStringAnsi(p) ?? "{}";
        Publish(JsonSerializer.Deserialize(json, PatrolInfoJsonContext.Default.PatrolInfo) ?? new PatrolInfo());
    }

    protected internal override void Reset()
    {
        _getPatrolInfoJson = null;
        ClearSnapshot();
    }
}
