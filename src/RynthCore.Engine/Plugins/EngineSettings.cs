using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RynthCore.Engine.Plugins;

internal static class EngineSettings
{
    // RYNTHCORE_ENGINE_SETTINGS points a test client at its own engine.json (its own
    // plugin list, no Avalonia, ...) without touching the one real clients share.
    private static readonly string SettingsPath =
        Environment.GetEnvironmentVariable("RYNTHCORE_ENGINE_SETTINGS") is { Length: > 0 } overridePath
            ? overridePath
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RynthCore",
                "engine.json");

    private static List<string> _pluginPaths = new();
    private static bool _enableImGuiShell = true;
    private static bool _enablePlugins = true;
    private static bool _enableDatShareHook = true;
    // Owned since f81fc8d: Save() writes it only when false (a hand-set false survives saves).
    private static bool _enableLargeAddressAlignmentFix = true;
    private static bool _enableAvaloniaOverlay = true;
    private static bool _enableD3D9Hook = true;
    private static bool _enableEngine = true;
    private static int _engineHookCount = int.MaxValue;
    private static bool _enableImGuiBackend = true;
    private static bool _enablePluginOverlayWindows = true;
    private static bool _enableHangMinidump = true;
    private static bool _preventIdleLogoff = true;
    private static bool _closeOnServerDisconnect = true;
    private static string _loggingLevel = "Info";
    private static DecalBridgeMode _decalBridgeMode = DecalBridgeMode.Auto;
    private static bool _decalInGameImGui = true;
    private static bool _decalStandDown = true;
    // "DisabledUiHooks": the UI hooks (Compatibility/UiHookRegistry) switched off by name.
    private static readonly HashSet<string> _disabledUiHooks = new(StringComparer.OrdinalIgnoreCase);
    private static bool _serverMessageStream = true;
    private static bool _flightRecorder = true;
    private static bool _insertReleasesUi;
    // Settings > Display: strip AC window chrome while windowed (default off).
    private static bool _borderlessWindowed;
    // Settings > Display: reopen Rynth panels where they were (default on: panels have always been remembered).
    private static bool _rememberWindowLayout = true;
    // Settings > Display: save and restore the AC window's own position and size (default off, opt-in).
    private static bool _rememberAcWindowPosition;
    // "LoadAllServerPlugins": hand-set only, written back only when true (PluginManifestGate).
    private static bool _loadAllServerPlugins;
    // Read-only, like EnableLargeAddressAlignmentFix: only a hand-set value is written back.
    private static int _maxHotReloads = ReloadCapNotice.DefaultMaxHotReloads;
    // "HoldReloadInWorld": opt-in (default off). On = an automatic engine reload waits until the player
    // leaves the world, for at most _holdReloadInWorldMaxMinutes (ReloadInWorldGate).
    private static bool _holdReloadInWorld;
    private static int _holdReloadInWorldMaxMinutes = ReloadInWorldGate.DefaultMaxMinutes;
    // "IltServerNames" / "IltHosts": what counts as Infinite Leaftide (Compatibility/IltInfo.cs).
    // null = not in the file: IltDetect's defaults.
    private static List<string>? _iltServerNames;
    private static List<string>? _iltHosts;
    // "ReportEndpoint": where "Report a problem" posts (Report/ProblemReport.cs); null = the built-in URL.
    // For testing only (https, or http to this PC); read only, like MaxHotReloads.
    private static string? _reportEndpoint;
    private static Compatibility.AutoIdMode _autoIdMode = Compatibility.AutoIdPolicy.DefaultMode;
    // "TransitionGate": default true. False = queued game actions are never held for a death / portal.
    private static bool _transitionGate = true;
    private static bool _loaded;

    public static IReadOnlyList<string> PluginPaths
    {
        get
        {
            EnsureLoaded();
            return _pluginPaths;
        }
    }

    /// <summary>Force-disabled in code: RynthCore's in-AC UI is the Avalonia overlay, so the
    /// ImGui shell (RynthCoreShell bar + plugin ImGui windows) never renders. engine.json and
    /// the launcher Plugins tab can no longer toggle this; the persisted field is now a no-op.
    /// A developer can opt back in for debugging by setting env var RYNTHCORE_FORCE_IMGUI=1
    /// before launching AC — normal users never set this.</summary>
    public static bool EnableImGuiShell
        => string.Equals(
               System.Environment.GetEnvironmentVariable("RYNTHCORE_FORCE_IMGUI"),
               "1", System.StringComparison.Ordinal);

    /// <summary>When false, PluginManager.LoadPlugins is skipped so no plugin DLLs load,
    /// init, tick, or render. Decal-coexistence plugin pump is also skipped. Default true.</summary>
    public static bool EnablePlugins
    {
        get
        {
            EnsureLoaded();
            return _enablePlugins;
        }
    }

    /// <summary>When false, DatFileShareHooks.Initialize is skipped so kernel32!CreateFileA/W
    /// are NOT detoured and no DAT-extension access/share rewriting happens. Multi-client DAT
    /// coexistence with Decal-injected clients sharing the same install will fall back to the
    /// stricter share semantics those clients negotiated. Default true. Use to isolate the
    /// hook for crash diagnostics.</summary>
    public static bool EnableDatShareHook
    {
        get
        {
            EnsureLoaded();
            return _enableDatShareHook;
        }
    }

    /// <summary>When false, LargeAddressAlignmentPatch leaves acclient's signed "pointer % 4"
    /// padding code alone (stock AC behaviour, which corrupts unpacks of buffers above 2 GB).
    /// Default true. Diagnostic kill-switch only: set "EnableLargeAddressAlignmentFix": false
    /// in engine.json by hand.</summary>
    public static bool EnableLargeAddressAlignmentFix
    {
        get
        {
            EnsureLoaded();
            return _enableLargeAddressAlignmentFix;
        }
    }

    /// <summary>
    /// True: every installed server plugin (RynthColdeve, RynthILT, ...) loads, whatever server the
    /// client connects to; each still shows only on its own server. Default false: the connect host
    /// picks the one plugin for this server (PluginManifestGate.SelectServerPlugins). Set
    /// "LoadAllServerPlugins": true in engine.json by hand (testing a plugin with /rc server ... on).
    /// </summary>
    public static bool LoadAllServerPlugins
    {
        get
        {
            EnsureLoaded();
            return _loadAllServerPlugins;
        }
    }

    /// <summary>When false, AvaloniaOverlay.Start() is skipped so the Avalonia STA thread,
    /// the offscreen overlay window, and all OverlayHost-registered panels never instantiate.
    /// Eliminates the entire Avalonia/Skia surface from the process. Compatibility hooks and
    /// (when enabled) plugins still load. Default true. Use to confirm whether the engine
    /// runs cleanly without Avalonia.</summary>
    public static bool EnableAvaloniaOverlay
    {
        get
        {
            EnsureLoaded();
#if ENGINE_CORECLR
            // Avalonia can't live in an unloadable engine (docs/UNLOADABLE_ENGINE_PLAN.md,
            // Phase 5): Skia/HarfBuzz keep strong GC handles for their native callbacks, so
            // the generation never unloads, and the next generation's copy finds the old
            // one's assemblies ("Call from invalid thread"). The in-game panels are ImGui;
            // pop-outs return as ImGui windows (IMGUI_PARITY_PLAN P5).
            if (_enableAvaloniaOverlay && System.Threading.Interlocked.Exchange(ref _avaloniaOffLogged, 1) == 0)
                RynthLog.Info("EngineSettings: EnableAvaloniaOverlay ignored - the CoreCLR engine runs without Avalonia (it can't be unloaded).");
            return false;
#else
            return _enableAvaloniaOverlay;
#endif
        }
    }

#if ENGINE_CORECLR
    private static int _avaloniaOffLogged;
#endif

    /// <summary>When false, D3D9Bootstrapper.Start() is skipped at LoginComplete so neither
    /// Direct3DCreate9/CreateDevice nor IDirect3DDevice9::EndScene get hooked. Eliminates the
    /// entire D3D9 detour path, ImGui per-frame work, and the in-AC overlay bar. Compatibility
    /// hooks and Avalonia (when enabled) still run. Default true. Use to confirm whether the
    /// engine runs cleanly without the EndScene detour.</summary>
    public static bool EnableD3D9Hook
    {
        get
        {
            EnsureLoaded();
            return _enableD3D9Hook;
        }
    }

    /// <summary>When false, InitWorker installs no Compatibility/* hooks, no
    /// LoginLifecycleHooks subscriber chain, no OverlayHost panels, no Avalonia,
    /// no D3D9. Only the early items (CrashLogger, ProcessExitHooks,
    /// MultiClientHooks, DatFileShareHooks, HeartbeatLogger) that fire before
    /// InitWorker stay active. Use to confirm whether the recursive AV is in
    /// the engine's main hook path or in something earlier (loader, NativeAOT
    /// runtime, MultiClientHooks/DatFileShareHooks).</summary>
    public static bool EnableEngine
    {
        get
        {
            EnsureLoaded();
            return _enableEngine;
        }
    }

    /// <summary>Cap on how many of InitWorker's RunInitStep calls actually fire.
    /// Default = int.MaxValue (= install all 36). Set to 18 to install only the
    /// first half, 9 for the first quarter, etc — use for binary-search
    /// bisection of which Compatibility hook is causing the recursive AV.
    /// Has no effect if EnableEngine is false.</summary>
    public static int EngineHookCount
    {
        get
        {
            EnsureLoaded();
            return _engineHookCount;
        }
    }

    /// <summary>When false, EngineFrameController.OnEndScene skips its
    /// ImGui-specific block (Win32Backend.NewFrame, DX9Backend.NewFrame,
    /// ImGui.NewFrame/EndFrame/Render, RynthCoreShell + plugin RenderAll,
    /// DX9Backend.RenderDrawData, viewport pump). The always-on engine work
    /// (GameMatrixCapture, Nav3DRenderInjector, PluginManager init/tick/
    /// pending-action drains, DX9Backend.RenderNav3D fallback) still runs
    /// every frame. Diagnostic for whether the per-frame ImGui path is the
    /// recurser.</summary>
    public static bool EnableImGuiBackend
    {
        get
        {
            EnsureLoaded();
            return _enableImGuiBackend;
        }
        set
        {
            // Set in game with /rc imgui on|off; persisted to engine.json.
            EnsureLoaded();   // populate _pluginPaths before Save() rewrites the file
            if (_enableImGuiBackend == value) return;
            _enableImGuiBackend = value;
            Save("EnableImGuiBackend");
        }
    }

    /// <summary>When true (default) and the ImGui shell is off, plugins that export
    /// RynthPluginRenderOverlay draw their extra ImGui windows (RynthAi: Mini Remote, Item Info; RynthILT: Games HUD)
    /// beside the Avalonia UI. engine.json "EnablePluginOverlayWindows": false hides them.
    /// Needs <see cref="EnableImGuiBackend"/>.</summary>
    public static bool EnablePluginOverlayWindows
    {
        get
        {
            EnsureLoaded();
            return _enablePluginOverlayWindows;
        }
    }

    /// <summary>When true, MainThreadHangWatchdog writes a minidump to
    /// Logs\dumps\ the first time AC's main thread is confirmed permanently
    /// wedged — a targeted post-mortem to replace always-on procdump (which
    /// dumped on every clean exit too). Default true.</summary>
    public static bool EnableHangMinidump
    {
        get
        {
            EnsureLoaded();
            return _enableHangMinidump;
        }
    }

    /// <summary>When true (default), the engine keeps the retail client's idle clock fresh
    /// while in the world, so ClientUISystem::UseTime's 20-minute idle auto-logoff
    /// (InactiveTimeBeforeLogout) can't log off a character a bot is playing: the bot's
    /// actions aren't keyboard/mouse input, so the client counted it as idle. Set false in
    /// engine.json to get the retail behaviour back.</summary>
    public static bool PreventIdleLogoff
    {
        get
        {
            EnsureLoaded();
            return _preventIdleLogoff;
        }
    }

    /// <summary>"CloseOnServerDisconnect" in engine.json (default true, 2026-10-11): when AC shows
    /// its "The server has disconnected" screen (UIFlow mode Disconnected, only a Quit Game
    /// button), the engine closes the client a few seconds later the way the window's close
    /// button does (Compatibility/ServerDisconnectClose.cs), so a launcher's auto-relaunch can
    /// bring it back. Off: the client stays on that screen. Set in game in Settings > Misc.</summary>
    public static bool CloseOnServerDisconnect
    {
        get
        {
            EnsureLoaded();
            return _closeOnServerDisconnect;
        }
        set
        {
            EnsureLoaded();   // populate _pluginPaths before Save() rewrites the file
            if (_closeOnServerDisconnect == value) return;
            _closeOnServerDisconnect = value;
            Save("CloseOnServerDisconnect");
        }
    }

    /// <summary>Engine log threshold from engine.json "LoggingLevel", written by the launcher's
    /// Logging level box: Error, Warning, Info (default), Debug/Verbose or Trace. Parsed by
    /// EntryPoint.ParseLoggingLevel at init; unknown values fall back to Info.</summary>
    public static string LoggingLevel
    {
        get
        {
            EnsureLoaded();
            return _loggingLevel;
        }
    }

    /// <summary>"DecalBridge" in engine.json: Auto (default), On or Off. Only matters when
    /// Decal is loaded in the same client (docs/DECAL_BRIDGE_PLAN.md):
    /// Auto - bridge mode when the RynthCore Decal bridge is registered and says hello
    ///        within a short wait; otherwise the old coexistence mode.
    /// On   - bridge mode whenever Decal is loaded (no fallback; for testing).
    /// Off  - never bridge mode (the old coexistence mode).
    /// Without Decal the setting is never read past this flag.</summary>
    public static DecalBridgeMode DecalBridgeMode
    {
        get
        {
            EnsureLoaded();
            return _decalBridgeMode;
        }
    }

    /// <summary>"DecalInGameImGui" in engine.json (default true since 2026-09-30, after the
    /// Reset, UB and soak checks in docs/DECAL_BRIDGE_PLAN.md). Only read when Decal is loaded
    /// in the same client and bridge mode is on: true installs the normal EndScene hook and
    /// ImGui renderer there (the device is found without creating one); false keeps the old
    /// Decal-coexistence "no D3D9" path. RYNTHCORE_DECAL_IMGUI=1/0 overrides it for one client.
    /// Without Decal it is never read.</summary>
    public static bool DecalInGameImGui
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable("RYNTHCORE_DECAL_IMGUI");
            if (env == "1") return true;
            if (env == "0") return false;
            EnsureLoaded();
            return _decalInGameImGui;
        }
    }

    /// <summary>"DecalStandDown" in engine.json (default true, 2026-10-03). Only read when
    /// Decal is loaded in the same client and the Decal bridge is NOT active (not registered
    /// as the client sees it, no hello, protocol mismatch): true makes the engine stand down
    /// (EntryPoint.InitWorker) - no plugins, no overlay, none of the hooks that collide with
    /// Decal; only what the "bridge isn't loaded" chat line needs. The old coexistence path
    /// (no overlay, then crashes) ran there before. false brings that path back.
    /// DecalBridge=Off always keeps the old path (tests). Without Decal it is never read.</summary>
    public static bool DecalStandDown
    {
        get
        {
            EnsureLoaded();
            return _decalStandDown;
        }
    }

    /// <summary>"DisabledUiHooks" in engine.json: an array of UI hook names
    /// (Compatibility/UiHookRegistry: UseNewMode, ClientCleanup, StartTooltip, ResetTooltip,
    /// CheckTooltip, StartDragandDrop, CatchDroppedItem). A listed hook is not installed at
    /// the next start. Missing = every hook on. /rc hooks on|off &lt;name&gt; edits it.</summary>
    public static bool IsUiHookEnabled(string name)
    {
        EnsureLoaded();
        return !_disabledUiHooks.Contains(name);
    }

    /// <summary>Switches a UI hook on or off for the next start, and saves engine.json.</summary>
    public static void SetUiHookEnabled(string name, bool enabled)
    {
        EnsureLoaded();
        bool changed = enabled ? _disabledUiHooks.Remove(name) : _disabledUiHooks.Add(name);
        if (changed)
            Save("DisabledUiHooks");
    }

    /// <summary>"ServerMessageStream" in engine.json (default true): the kill switch for the
    /// reassembled server-message stream (Net/ServerMessageStream.cs, plugin API v78). Even when
    /// true, nothing is copied off the receive hook until a plugin asks for messages (or
    /// /rc netmsg log|capture is on). false = the RecvFrom detour does exactly what it did
    /// before the stream existed; /rc netmsg on can't override it.</summary>
    public static bool ServerMessageStream
    {
        get
        {
            EnsureLoaded();
            return _serverMessageStream;
        }
    }

    /// <summary>"FlightRecorder" in engine.json (default true): the always-on flight recorder
    /// (Net/FlightRecorder.cs, /rc flightdump): a sendto hook plus a ring of the last minutes of
    /// client/server traffic and engine markers. false = no sendto hook and nothing recorded,
    /// from the next client start.</summary>
    public static bool FlightRecorder
    {
        get
        {
            EnsureLoaded();
            return _flightRecorder;
        }
    }

    /// <summary>"InsertReleasesUi" in engine.json (default false, 2026-10-08): Insert hands the
    /// mouse to the game (the UI/GAME toggle, Win32Backend), for clicking through a Rynth window.
    /// Off, Insert does nothing: a stray press left every Rynth text box unable to keep the
    /// keyboard, which read as "can't type in Rynth". Set in game in Settings > Display.</summary>
    public static bool InsertReleasesUi
    {
        get
        {
            EnsureLoaded();
            return _insertReleasesUi;
        }
        set
        {
            EnsureLoaded();   // populate _pluginPaths before Save() rewrites the file
            if (_insertReleasesUi == value) return;
            _insertReleasesUi = value;
            Save("InsertReleasesUi");
        }
    }

    /// <summary>"BorderlessWindowed" in engine.json (default false, 2026-10-18): strip the AC
    /// window caption and thick frame while staying windowed. Exclusive fullscreen is left alone,
    /// and chrome is only ever given back to a window this setting stripped (AcWindowChrome).
    /// Set in game in Settings > Display. From SilentKelpie's PR 6.</summary>
    public static bool BorderlessWindowed
    {
        get
        {
            EnsureLoaded();
            return _borderlessWindowed;
        }
        set
        {
            EnsureLoaded();
            if (_borderlessWindowed == value) return;
            _borderlessWindowed = value;
            Save("BorderlessWindowed");
            // AcWindowChrome applies the style from the game thread; keep this file free of
            // HWND / UI refs so EngineSettingsTests can compile it alone.
        }
    }

    /// <summary>"RememberWindowLayout" in engine.json (default true: Rynth panels have always been
    /// remembered). Off: panel_state.txt is not overwritten and open panels are not reopened from
    /// the last session. Does not touch the AC window itself (RememberAcWindowPosition).
    /// Set in game in Settings > Display.</summary>
    public static bool RememberWindowLayout
    {
        get
        {
            EnsureLoaded();
            return _rememberWindowLayout;
        }
        set
        {
            EnsureLoaded();
            if (_rememberWindowLayout == value) return;
            _rememberWindowLayout = value;
            Save("RememberWindowLayout");
        }
    }

    /// <summary>"RememberAcWindowPosition" in engine.json (default false, opt-in): save the Asheron's
    /// Call window's position and size per character and put it back at the next login.
    /// Set in game in Settings > Display.</summary>
    public static bool RememberAcWindowPosition
    {
        get
        {
            EnsureLoaded();
            return _rememberAcWindowPosition;
        }
        set
        {
            EnsureLoaded();
            if (_rememberAcWindowPosition == value) return;
            _rememberAcWindowPosition = value;
            Save("RememberAcWindowPosition");
        }
    }

    /// <summary>"MaxHotReloads" in engine.json (default 20 since 2026-10-15; 3 from 10-11): how many
    /// engine hot reloads one client takes; the loader asks before each one (RynthCoreMaxHotReloads)
    /// and refuses the rest, so a newer engine then needs a client restart. A backstop: the 0x80131506
    /// deaths after 4+ reloads that set it at 3 were the v1 crash logger (fixed 10-12). Negative = no
    /// cap (test clients soaking reloads), 0 = no hot reloads at all. Read at each engine init; edited
    /// in the file.</summary>
    public static int MaxHotReloads
    {
        get
        {
            EnsureLoaded();
            return _maxHotReloads;
        }
    }

    /// <summary>"HoldReloadInWorld" in engine.json (default false, opt-in; from SilentKelpie's PR 6):
    /// when true, an automatic engine reload (the loader's file watcher, an engine deploy) waits until
    /// the player logs out / reaches character select instead of reloading mid-session, for at most
    /// <see cref="HoldReloadInWorldMaxMinutes"/>. A reload asked for by hand (ERl, the bar RL button,
    /// /rc rl, the hotkey) is never held. A workaround for CoreCLR re-init crashes on stacked
    /// mid-session reloads. Only a true is written to the file. Set in game in Settings > Misc.</summary>
    public static bool HoldReloadInWorld
    {
        get
        {
            EnsureLoaded();
            return _holdReloadInWorld;
        }
        set
        {
            EnsureLoaded();
            if (_holdReloadInWorld == value) return;
            _holdReloadInWorld = value;
            Save("HoldReloadInWorld");
        }
    }

    /// <summary>"HoldReloadInWorldMaxMinutes" in engine.json (default 10, valid 1-240): how long
    /// <see cref="HoldReloadInWorld"/> holds a reload before it gives up, logs once, and reloads
    /// anyway. The hold never lasts forever. Out-of-range values are clamped; only a non-default
    /// value is written to the file.</summary>
    public static int HoldReloadInWorldMaxMinutes
    {
        get
        {
            EnsureLoaded();
            return _holdReloadInWorldMaxMinutes;
        }
        set
        {
            EnsureLoaded();
            int v = ReloadInWorldGate.ClampMaxMinutes(value);
            if (_holdReloadInWorldMaxMinutes == v) return;
            _holdReloadInWorldMaxMinutes = v;
            Save("HoldReloadInWorldMaxMinutes");
        }
    }

    /// <summary>"ReportEndpoint" in engine.json: a test URL for "Report a problem" instead of the
    /// built-in https://aelrynth.com/rynth/report (ProblemReportCore.ResolveEndpoint accepts https,
    /// or http to this PC; anything else is ignored). Null when the file has none. Edited in the file.</summary>
    public static string? ReportEndpoint
    {
        get
        {
            EnsureLoaded();
            return _reportEndpoint;
        }
    }

    /// <summary>"IltServerNames" in engine.json: world-name patterns ('*' wildcard, case-insensitive)
    /// treated as Infinite Leaftide; null when the file has none (IltDetect.DefaultServerNames).
    /// An empty array turns the world-name signal off. Edited in the file, or with
    /// "/rc server names ilt add|remove" (EditIltServerName).</summary>
    public static IReadOnlyList<string>? IltServerNames
    {
        get
        {
            EnsureLoaded();
            return _iltServerNames;
        }
    }

    /// <summary>Longest world-name pattern EditIltServerName takes.</summary>
    public const int MaxIltServerNameLength = 64;

    /// <summary>
    /// Adds (<paramref name="add"/>) or removes a world-name pattern in "IltServerNames" and saves
    /// engine.json. With none in the file the list starts from IltDetect.DefaultServerNames, so
    /// adding one keeps the defaults. False when nothing changed (already there, not there, or
    /// an empty / too long pattern).
    /// </summary>
    public static bool EditIltServerName(string pattern, bool add)
    {
        EnsureLoaded();
        string p = (pattern ?? string.Empty).Trim();
        if (p.Length == 0 || p.Length > MaxIltServerNameLength) return false;
        var list = new List<string>(_iltServerNames ?? (IEnumerable<string>)Compatibility.IltDetect.DefaultServerNames);
        int at = list.FindIndex(x => x.Equals(p, StringComparison.OrdinalIgnoreCase));
        if (add == (at >= 0)) return false;
        if (add) list.Add(p);
        else list.RemoveAt(at);
        // Replaced, never edited in place: IltInfo reads the old list on other threads.
        _iltServerNames = list;
        Save("IltServerNames");
        return true;
    }

    /// <summary>"IltHosts" in engine.json: launch hosts (a domain covers its subdomains, '*' allowed)
    /// treated as Infinite Leaftide; null when the file has none (IltDetect.DefaultHosts).</summary>
    public static IReadOnlyList<string>? IltHosts
    {
        get
        {
            EnsureLoaded();
            return _iltHosts;
        }
    }

    /// <summary>"AutoIdMode" in engine.json: "All", "Limited" (default) or "Off".
    /// Which background appraisals the engine's AutoId sends (Compatibility/AutoIdPolicy.cs);
    /// plugins' own RequestId calls are never limited by it. Set live with /rc autoid.</summary>
    public static Compatibility.AutoIdMode AutoIdMode
    {
        get
        {
            EnsureLoaded();
            return _autoIdMode;
        }
        set
        {
            EnsureLoaded();   // populate _pluginPaths before Save() rewrites the file
            if (_autoIdMode == value) return;
            _autoIdMode = value;
            Save("AutoIdMode");
        }
    }

    /// <summary>"TransitionGate" in engine.json (default true): the world-transition gate
    /// (Compatibility/WorldTransitionState.cs) holds the engine's queued game actions (casts, uses,
    /// appraisals, chat commands...) while the character is dead or in portal space, and for 5 s
    /// after. False turns the gate off entirely: nothing is held. Even with it on, a gate held
    /// continuously for WorldTransitionState.MaxHoldMs opens by itself (logged once, counted in the
    /// heartbeat as gateov=N) in case a revived character reads as dead or the portal flag sticks.</summary>
    public static bool TransitionGate
    {
        get
        {
            EnsureLoaded();
            return _transitionGate;
        }
        set
        {
            EnsureLoaded();
            if (_transitionGate == value) return;
            _transitionGate = value;
            Save("TransitionGate");
        }
    }

    public static void AddPluginPath(string path)
    {
        EnsureLoaded();
        string full = Path.GetFullPath(path);
        for (int i = 0; i < _pluginPaths.Count; i++)
        {
            if (string.Equals(_pluginPaths[i], full, StringComparison.OrdinalIgnoreCase))
                return;
        }
        _pluginPaths.Add(full);
        Save("PluginPaths");
    }

    public static void RemovePluginPath(int index)
    {
        EnsureLoaded();
        if (index >= 0 && index < _pluginPaths.Count)
        {
            _pluginPaths.RemoveAt(index);
            Save("PluginPaths");
        }
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            if (!File.Exists(SettingsPath)) return;
            // ReadAllText detects + strips the UTF-8/UTF-16 BOM that some editors
            // (PowerShell 5.1 Set-Content -Encoding utf8, Notepad) silently prepend.
            // ReadAllBytes + JsonDocument.Parse trips on a leading BOM with
            // "'0xEF' is an invalid start of a value", which then sends every
            // EngineSettings flag back to its default — silently re-enables
            // EnableImGuiBackend etc. with no surfaced error.
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            if (doc.RootElement.TryGetProperty("PluginPaths", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    string? val = el.GetString();
                    if (!string.IsNullOrWhiteSpace(val))
                        _pluginPaths.Add(val);
                }
            }

            if (doc.RootElement.TryGetProperty("EnableImGuiShell", out var shellEl) &&
                (shellEl.ValueKind == JsonValueKind.True || shellEl.ValueKind == JsonValueKind.False))
            {
                _enableImGuiShell = shellEl.GetBoolean();
            }

            if (doc.RootElement.TryGetProperty("EnablePlugins", out var pluginsEl) &&
                (pluginsEl.ValueKind == JsonValueKind.True || pluginsEl.ValueKind == JsonValueKind.False))
            {
                _enablePlugins = pluginsEl.GetBoolean();
            }

            if (doc.RootElement.TryGetProperty("EnableDatShareHook", out var datEl) &&
                (datEl.ValueKind == JsonValueKind.True || datEl.ValueKind == JsonValueKind.False))
            {
                _enableDatShareHook = datEl.GetBoolean();
            }

            if (doc.RootElement.TryGetProperty("EnableLargeAddressAlignmentFix", out var laaEl) &&
                (laaEl.ValueKind == JsonValueKind.True || laaEl.ValueKind == JsonValueKind.False))
            {
                _enableLargeAddressAlignmentFix = laaEl.GetBoolean();
            }

            if (doc.RootElement.TryGetProperty("LoadAllServerPlugins", out var lasEl) &&
                (lasEl.ValueKind == JsonValueKind.True || lasEl.ValueKind == JsonValueKind.False))
            {
                _loadAllServerPlugins = lasEl.GetBoolean();
            }

            if (doc.RootElement.TryGetProperty("EnableAvaloniaOverlay", out var avEl) &&
                (avEl.ValueKind == JsonValueKind.True || avEl.ValueKind == JsonValueKind.False))
            {
                _enableAvaloniaOverlay = avEl.GetBoolean();
            }

            if (doc.RootElement.TryGetProperty("EnableD3D9Hook", out var d3dEl) &&
                (d3dEl.ValueKind == JsonValueKind.True || d3dEl.ValueKind == JsonValueKind.False))
            {
                _enableD3D9Hook = d3dEl.GetBoolean();
            }

            if (doc.RootElement.TryGetProperty("EnableEngine", out var engEl) &&
                (engEl.ValueKind == JsonValueKind.True || engEl.ValueKind == JsonValueKind.False))
            {
                _enableEngine = engEl.GetBoolean();
            }

            if (doc.RootElement.TryGetProperty("EngineHookCount", out var ehcEl) &&
                ehcEl.ValueKind == JsonValueKind.Number &&
                ehcEl.TryGetInt32(out int ehc))
            {
                _engineHookCount = ehc;
            }

            if (doc.RootElement.TryGetProperty("EnableImGuiBackend", out var ibEl) &&
                (ibEl.ValueKind == JsonValueKind.True || ibEl.ValueKind == JsonValueKind.False))
            {
                _enableImGuiBackend = ibEl.GetBoolean();
            }

            if (doc.RootElement.TryGetProperty("EnablePluginOverlayWindows", out var powEl) &&
                (powEl.ValueKind == JsonValueKind.True || powEl.ValueKind == JsonValueKind.False))
            {
                _enablePluginOverlayWindows = powEl.GetBoolean();
            }

            if (doc.RootElement.TryGetProperty("EnableHangMinidump", out var hmEl) &&
                (hmEl.ValueKind == JsonValueKind.True || hmEl.ValueKind == JsonValueKind.False))
            {
                _enableHangMinidump = hmEl.GetBoolean();
            }


            if (doc.RootElement.TryGetProperty("DecalBridge", out var dbEl))
            {
                if (dbEl.ValueKind == JsonValueKind.True) _decalBridgeMode = DecalBridgeMode.On;
                else if (dbEl.ValueKind == JsonValueKind.False) _decalBridgeMode = DecalBridgeMode.Off;
                else if (dbEl.ValueKind == JsonValueKind.String &&
                         Enum.TryParse(dbEl.GetString(), ignoreCase: true, out DecalBridgeMode dbm))
                    _decalBridgeMode = dbm;
            }

            if (doc.RootElement.TryGetProperty("DecalInGameImGui", out var dimEl) &&
                (dimEl.ValueKind == JsonValueKind.True || dimEl.ValueKind == JsonValueKind.False))
                _decalInGameImGui = dimEl.GetBoolean();

            if (doc.RootElement.TryGetProperty("DecalStandDown", out var dsdEl) &&
                (dsdEl.ValueKind == JsonValueKind.True || dsdEl.ValueKind == JsonValueKind.False))
                _decalStandDown = dsdEl.GetBoolean();

            if (doc.RootElement.TryGetProperty("ServerMessageStream", out var smsEl) &&
                (smsEl.ValueKind == JsonValueKind.True || smsEl.ValueKind == JsonValueKind.False))
                _serverMessageStream = smsEl.GetBoolean();

            if (doc.RootElement.TryGetProperty("FlightRecorder", out var frEl) &&
                (frEl.ValueKind == JsonValueKind.True || frEl.ValueKind == JsonValueKind.False))
                _flightRecorder = frEl.GetBoolean();

            if (doc.RootElement.TryGetProperty("InsertReleasesUi", out var iruEl) &&
                (iruEl.ValueKind == JsonValueKind.True || iruEl.ValueKind == JsonValueKind.False))
                _insertReleasesUi = iruEl.GetBoolean();

            if (doc.RootElement.TryGetProperty("BorderlessWindowed", out var blwEl) &&
                (blwEl.ValueKind == JsonValueKind.True || blwEl.ValueKind == JsonValueKind.False))
                _borderlessWindowed = blwEl.GetBoolean();

            if (doc.RootElement.TryGetProperty("RememberWindowLayout", out var rwlEl) &&
                (rwlEl.ValueKind == JsonValueKind.True || rwlEl.ValueKind == JsonValueKind.False))
                _rememberWindowLayout = rwlEl.GetBoolean();

            if (doc.RootElement.TryGetProperty("RememberAcWindowPosition", out var rapEl) &&
                (rapEl.ValueKind == JsonValueKind.True || rapEl.ValueKind == JsonValueKind.False))
                _rememberAcWindowPosition = rapEl.GetBoolean();

            if (doc.RootElement.TryGetProperty("MaxHotReloads", out var mhrEl) &&
                mhrEl.ValueKind == JsonValueKind.Number &&
                mhrEl.TryGetInt32(out int mhr))
                _maxHotReloads = mhr;

            if (doc.RootElement.TryGetProperty("HoldReloadInWorld", out var hriwEl) &&
                (hriwEl.ValueKind == JsonValueKind.True || hriwEl.ValueKind == JsonValueKind.False))
                _holdReloadInWorld = hriwEl.GetBoolean();

            if (doc.RootElement.TryGetProperty("HoldReloadInWorldMaxMinutes", out var hrimEl) &&
                hrimEl.ValueKind == JsonValueKind.Number &&
                hrimEl.TryGetInt32(out int hrim))
                _holdReloadInWorldMaxMinutes = ReloadInWorldGate.ClampMaxMinutes(hrim);

            if (doc.RootElement.TryGetProperty("ReportEndpoint", out var repEl) &&
                repEl.ValueKind == JsonValueKind.String &&
                repEl.GetString() is { Length: > 0 } rep)
                _reportEndpoint = rep.Trim();

            if (doc.RootElement.TryGetProperty("AutoIdMode", out var aimEl) &&
                aimEl.ValueKind == JsonValueKind.String &&
                Compatibility.AutoIdPolicy.TryParseMode(aimEl.GetString(), out Compatibility.AutoIdMode aim))
                _autoIdMode = aim;

            if (doc.RootElement.TryGetProperty("TransitionGate", out var tgEl) &&
                (tgEl.ValueKind == JsonValueKind.True || tgEl.ValueKind == JsonValueKind.False))
                _transitionGate = tgEl.GetBoolean();

            if (doc.RootElement.TryGetProperty("PreventIdleLogoff", out var pilEl) &&
                (pilEl.ValueKind == JsonValueKind.True || pilEl.ValueKind == JsonValueKind.False))
            {
                _preventIdleLogoff = pilEl.GetBoolean();
            }

            if (doc.RootElement.TryGetProperty("CloseOnServerDisconnect", out var cosdEl) &&
                (cosdEl.ValueKind == JsonValueKind.True || cosdEl.ValueKind == JsonValueKind.False))
                _closeOnServerDisconnect = cosdEl.GetBoolean();

            if (doc.RootElement.TryGetProperty("IltServerNames", out var ilnEl) &&
                ilnEl.ValueKind == JsonValueKind.Array)
                _iltServerNames = ReadStringList(ilnEl);

            if (doc.RootElement.TryGetProperty("IltHosts", out var ilhEl) &&
                ilhEl.ValueKind == JsonValueKind.Array)
                _iltHosts = ReadStringList(ilhEl);

            if (doc.RootElement.TryGetProperty("DisabledUiHooks", out var duhEl) &&
                duhEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in duhEl.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 } hookName)
                        _disabledUiHooks.Add(hookName.Trim());
                }
            }

            if (doc.RootElement.TryGetProperty("LoggingLevel", out var logLevelEl) &&
                logLevelEl.ValueKind == JsonValueKind.String)
            {
                string? configuredLevel = logLevelEl.GetString();
                if (!string.IsNullOrWhiteSpace(configuredLevel))
                    _loggingLevel = configuredLevel;
            }
        }
        catch (Exception ex)
        {
            RynthLog.Plugin($"EngineSettings: Failed to load {SettingsPath}: {ex.Message}");
        }
    }

    private static List<string> ReadStringList(JsonElement arr)
    {
        // Trimmed, blanks dropped, case-insensitive duplicates dropped (the file stays as the player wrote it).
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.String) continue;
            string v = (el.GetString() ?? "").Trim();
            if (v.Length > 0 && seen.Add(v)) list.Add(v);
        }
        return list;
    }

    /// <summary>Every engine.json key EnsureLoaded reads or Save writes. A save writes only the keys
    /// this client changed (Save(key); one written only when not the default is left out, which
    /// removes it) and keeps every other key as the file had it. A new engine.json setting goes
    /// here too, and its setter calls Save("Key") (tools/EngineSettingsTests checks this list and
    /// the Save calls against this file).</summary>
    internal static readonly HashSet<string> OwnedKeys = new(StringComparer.Ordinal)
    {
        "PluginPaths", "EnableImGuiShell", "EnablePlugins", "EnableDatShareHook",
        "EnableAvaloniaOverlay", "EnableD3D9Hook", "EnableEngine", "EngineHookCount",
        "EnableImGuiBackend", "EnableHangMinidump", "PreventIdleLogoff", "DecalBridge",
        "DecalInGameImGui", "DecalStandDown", "DisabledUiHooks", "ServerMessageStream",
        "FlightRecorder", "InsertReleasesUi", "BorderlessWindowed", "RememberWindowLayout",
        "RememberAcWindowPosition", "IltServerNames", "IltHosts", "EnableLargeAddressAlignmentFix",
        "MaxHotReloads", "ReportEndpoint", "CloseOnServerDisconnect", "AutoIdMode", "EnablePluginOverlayWindows", "LoggingLevel",
        "LoadAllServerPlugins", "TransitionGate",
        "HoldReloadInWorld", "HoldReloadInWorldMaxMinutes",
    };

    // The engine.json keys this client changed this session. Every client keeps the whole settings
    // in memory, so writing all of the engine's keys back let a client that never touched a setting
    // put its stale value over another client's change (last writer won). Only these keys are
    // written now; every other key keeps what the file holds. Idea from Silentkelpie (rynth PR #6).
    private static readonly HashSet<string> _changedKeys = new(StringComparer.Ordinal);
    private static readonly object _changedLock = new();

    /// <summary>Saves engine.json after <paramref name="key"/> changed (only changed keys are written).</summary>
    private static void Save(string key)
    {
        lock (_changedLock) _changedKeys.Add(key);
        Save();
    }

    private static void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(SettingsPath);
            if (dir != null) Directory.CreateDirectory(dir);

            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteStartArray("PluginPaths");
                foreach (string p in _pluginPaths)
                    w.WriteStringValue(p);
                w.WriteEndArray();
                w.WriteBoolean("EnableImGuiShell", _enableImGuiShell);
                w.WriteBoolean("EnablePlugins", _enablePlugins);
                w.WriteBoolean("EnableDatShareHook", _enableDatShareHook);
                w.WriteBoolean("EnableAvaloniaOverlay", _enableAvaloniaOverlay);
                w.WriteBoolean("EnableD3D9Hook", _enableD3D9Hook);
                w.WriteBoolean("EnableEngine", _enableEngine);
                w.WriteNumber("EngineHookCount", _engineHookCount);
                w.WriteBoolean("EnableImGuiBackend", _enableImGuiBackend);
                w.WriteBoolean("EnablePluginOverlayWindows", _enablePluginOverlayWindows);
                w.WriteBoolean("EnableHangMinidump", _enableHangMinidump);
                w.WriteBoolean("PreventIdleLogoff", _preventIdleLogoff);
                // Preserve the launcher-chosen level; dropping it here would reset logging to Info.
                w.WriteString("LoggingLevel", _loggingLevel);
                if (_decalBridgeMode != DecalBridgeMode.Auto)   // default: leave the file as it was
                    w.WriteString("DecalBridge", _decalBridgeMode.ToString());
                if (!_decalInGameImGui)   // default true: leave the file as it was
                    w.WriteBoolean("DecalInGameImGui", false);
                if (!_decalStandDown)     // default true: leave the file as it was
                    w.WriteBoolean("DecalStandDown", false);
                if (_disabledUiHooks.Count > 0)   // default none: leave the file as it was
                {
                    w.WriteStartArray("DisabledUiHooks");
                    foreach (string hookName in _disabledUiHooks)
                        w.WriteStringValue(hookName);
                    w.WriteEndArray();
                }
                if (!_serverMessageStream) // default true: leave the file as it was
                    w.WriteBoolean("ServerMessageStream", false);
                if (!_flightRecorder)      // default true: leave the file as it was
                    w.WriteBoolean("FlightRecorder", false);
                if (!_enableLargeAddressAlignmentFix)   // default true: only a hand-set false is written
                    w.WriteBoolean("EnableLargeAddressAlignmentFix", false);
                if (_insertReleasesUi)     // default false: leave the file as it was
                    w.WriteBoolean("InsertReleasesUi", true);
                if (_borderlessWindowed)   // default false: leave the file as it was
                    w.WriteBoolean("BorderlessWindowed", true);
                if (!_rememberWindowLayout) // default true: leave the file as it was
                    w.WriteBoolean("RememberWindowLayout", false);
                if (_rememberAcWindowPosition) // default false: leave the file as it was
                    w.WriteBoolean("RememberAcWindowPosition", true);
                if (!_closeOnServerDisconnect)   // default true: leave the file as it was
                    w.WriteBoolean("CloseOnServerDisconnect", false);
                if (!_transitionGate)      // default true: only a hand-set false is written
                    w.WriteBoolean("TransitionGate", false);
                if (_loadAllServerPlugins) // default false: only a hand-set true is written
                    w.WriteBoolean("LoadAllServerPlugins", true);
                if (_maxHotReloads != ReloadCapNotice.DefaultMaxHotReloads)   // only a hand-set value is written
                    w.WriteNumber("MaxHotReloads", _maxHotReloads);
                if (_holdReloadInWorld)    // default false: only a true is written
                    w.WriteBoolean("HoldReloadInWorld", true);
                if (_holdReloadInWorldMaxMinutes != ReloadInWorldGate.DefaultMaxMinutes)   // only a non-default value is written
                    w.WriteNumber("HoldReloadInWorldMaxMinutes", _holdReloadInWorldMaxMinutes);
                if (_reportEndpoint != null)   // only a hand-set value is written
                    w.WriteString("ReportEndpoint", _reportEndpoint);
                WriteStringList(w, "IltServerNames", _iltServerNames);   // null (not in the file): left out
                WriteStringList(w, "IltHosts", _iltHosts);
                if (_autoIdMode != Compatibility.AutoIdPolicy.DefaultMode)   // default: leave the file as it was
                    w.WriteString("AutoIdMode", _autoIdMode.ToString());
                w.WriteEndObject();
            }

            // Keep the keys others write (the launcher's), and swap the file in atomically.
            string? existing = File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath) : null;
            string[] changed;
            lock (_changedLock)
            {
                changed = new string[_changedKeys.Count];
                _changedKeys.CopyTo(changed);
            }
            byte[]? merged = EngineSettingsFile.Merge(existing, ms.ToArray(), OwnedKeys, out string? readError, changed);
            if (merged == null)
            {
                RynthLog.Warn($"EngineSettings: not saving - {SettingsPath} can't be read ({readError}), and rewriting it would lose what it holds. The change holds until the client closes; fix or delete the file to save again.");
                return;
            }
            EngineSettingsFile.WriteAtomic(SettingsPath, merged);
        }
        catch (Exception ex)
        {
            RynthLog.Plugin($"EngineSettings: Failed to save: {ex.Message}");
        }
    }

    private static void WriteStringList(Utf8JsonWriter w, string name, List<string>? list)
    {
        if (list == null) return;
        w.WriteStartArray(name);
        foreach (string v in list)
            w.WriteStringValue(v);
        w.WriteEndArray();
    }
}

/// <summary>engine.json "DecalBridge" (see <see cref="EngineSettings.DecalBridgeMode"/>).</summary>
internal enum DecalBridgeMode { Auto = 0, On = 1, Off = 2 }
