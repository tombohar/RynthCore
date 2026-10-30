// ============================================================================
//  RynthCore.Engine - EntryPoint.cs
//  NativeAOT exported function. Called by RynthCore.Loader after LoadLibrary.
//  Spawns init on a background thread to avoid loader-lock issues.
//  Export name is RynthCoreEngineInit; the loader DLL owns RynthCoreInit.
// ============================================================================

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Collections.Generic;
using ImGuiNET;
using RynthCore;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.D3D9;
using RynthCore.Engine.Plugins;
using RynthCore.Engine.UI;
using RynthCore.Engine.UI.Panels;

namespace RynthCore.Engine;

public static class EntryPoint
{
    /// <summary>
    /// This engine's version — "2026.9.26.3 (8ca322f)", or "dev (8ca322f)" for an unstamped
    /// build — read from the DLL's own version resource at init (RynthCore.App.BuildVersion).
    /// Goes into every log header and crash line, and the in-game Status panel.
    /// </summary>
    internal static string BuildStamp { get; private set; } = "unknown";
    private const int MaxRecentLogLines = 256;
    private static int _initialized;
    /// <summary>Init counter the loader passes in lpParam. 1 = cold start,
    /// 2+ = hot reload (game is already past login, skip the LoginComplete gate).
    /// Read by PluginLoader to give each engine instance its own shadow-copy
    /// directory so a previous (zombie-loaded) engine's plugin DLL doesn't
    /// keep the same shadow path locked.</summary>
    internal static int InitCount => _initCount;
    private static int _initCount;
    private static bool _imGuiResolverConfigured;
    private static IntPtr _imGuiNativeHandle;
    /// <summary>Module handle of the loaded cimgui.dll (zero until preloaded).</summary>
    internal static IntPtr ImGuiNativeHandle => _imGuiNativeHandle;
    private static readonly object LogLock = new();
    private static readonly Queue<string> RecentLogLines = new();
    private static long _recentLogSeq;

    internal enum EngineLogLevel
    {
        /// <summary>As a global threshold: nothing but errors. As a category level: never written.</summary>
        Off = -1,
        Error = 0,
        Warning = 1,
        Info = 2,
        Debug = 3,
        Trace = 4
    }

    /// <summary>Active engine logging threshold loaded from engine settings.</summary>
    internal static EngineLogLevel LoggingLevel = EngineLogLevel.Info;

    /// <summary>Legacy convenience flag used by existing verbose call sites.</summary>
    internal static bool VerboseLogging = false;

    /// <summary>Set by EngineFrameController once the game window is confirmed. Read by AvaloniaOverlay.</summary>
    internal static volatile IntPtr GameHwnd;

    // Legacy export: kept so an old launcher / injector that still targets
    // RynthCore.Engine.dll directly (instead of going through RynthCore.Loader)
    // continues to work during the migration. Forwards to InitializeCore.
    [UnmanagedCallersOnly(EntryPoint = "RynthCoreInit")]
    public static uint InitializeLegacy(IntPtr lpParam) => InitializeCore(lpParam);

    [UnmanagedCallersOnly(EntryPoint = "RynthCoreEngineInit")]
    public static uint Initialize(IntPtr lpParam) => InitializeCore(lpParam);

    /// <summary>
    /// Tear down everything Initialize set up, in reverse order. Called by
    /// RynthCore.Loader before FreeLibrary'ing the engine. Must NOT be invoked
    /// from inside an EndScene detour — the caller should run on a background
    /// thread so the render thread's call into our hook can return cleanly.
    /// </summary>
    /// <summary>
    /// The loader refused a hot-reload because this client is short of address
    /// space: too little free in all, or (CoreCLR engine, 2026-10-15) no free block
    /// big enough. <paramref name="freeMb"/> is the figure that fell short (the loader
    /// log says which). Says so in chat. Loader thread.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "RynthCoreReloadRefused")]
    public static uint ReloadRefused(int freeMb)
    {
        try
        {
            RynthLog.Warn($"Hot-reload refused by the loader: short of address space ({freeMb} MB free in all, or in the largest free block; see the loader line).");
            Compatibility.AcMainThreadQueue.EnqueueWriteToChat(
                $"[RynthCore] A new RynthCore build is ready but wasn't loaded: this client is low on memory address space ({freeMb} MB usable), and reloading could crash it. Restart the client to use the new build.", 2);
        }
        catch { }
        return 0;
    }

    /// <summary>
    /// The reload cap (ReloadCapNotice): the loader asks this before every hot reload, and
    /// refuses the reload once this client has done that many. engine.json MaxHotReloads,
    /// default 20; negative = no cap. Loader thread.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "RynthCoreMaxHotReloads")]
    public static int MaxHotReloads()
    {
        try { return EngineSettings.MaxHotReloads; }
        catch { return ReloadCapNotice.DefaultMaxHotReloads; }
    }

    private static readonly ReloadCapNotice s_reloadCapNotice = new();

    /// <summary>
    /// The loader refused a hot reload because this client has done <paramref name="reloads"/>
    /// of them (the cap is <paramref name="cap"/>). The client stays on this engine. Logs it,
    /// and says so in chat once, and again when the player asked for the reload by hand.
    /// Loader thread.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "RynthCoreReloadCapped")]
    public static uint ReloadCapped(int reloads, int cap)
    {
        try
        {
            bool byHand = ReloadCapNotice.IsRecentManualSignal(EngineLifecycle.LastSignalReloadMs, Environment.TickCount64);
            RynthLog.Warn(ReloadCapNotice.LogText(reloads, cap, byHand));
            bool chat;
            lock (s_reloadCapNotice) chat = s_reloadCapNotice.ShouldChat(byHand);
            if (chat)
                Compatibility.AcMainThreadQueue.EnqueueWriteToChat(ReloadCapNotice.ChatText(reloads), 2);
        }
        catch { }
        return 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthCoreShutdown")]
    public static uint Shutdown(IntPtr lpParam)
    {
        // Only the loader's hot reload calls this export (process exit and the engine's own
        // teardowns call EngineLifecycle.Shutdown directly), on its reload thread: wait here,
        // with the game running on this engine, while a plugin says a reload now would hurt.
        try { DeferReloadWhilePluginsObject(); } catch { }
        try
        {
            EngineLifecycle.Shutdown();
            // Reset the init guard so a fresh RynthCoreEngineInit call (after
            // the loader reloads us) can run again.
            Interlocked.Exchange(ref _initialized, 0);
            return 0;
        }
        catch (Exception ex)
        {
            RynthLog.Info($"FATAL in RynthCoreShutdown: {ex}");
            return 1;
        }
    }

    // The shell's ERl button: a reload asked for by hand isn't held for the plugins. Only the
    // reload that follows within ManualReloadWindowMs counts (a refused or coalesced signal
    // must not exempt the next file-watcher reload).
    private static long _manualReloadAtMs;
    private const long ManualReloadWindowMs = 10_000;
    internal static void NoteManualReload() => Interlocked.Exchange(ref _manualReloadAtMs, Environment.TickCount64);

    /// <summary>True when ERl / the bar RL / /rc rl / the hotkey marked a hand reload within the window
    /// (does not clear the mark).</summary>
    private static bool IsRecentManualReload()
    {
        long manualAt = Volatile.Read(ref _manualReloadAtMs);
        return manualAt != 0 && Environment.TickCount64 - manualAt < ManualReloadWindowMs;
    }

    /// <summary>
    /// In the world after a successful login, and not yet logged out: with
    /// engine.json HoldReloadInWorld on, an automatic reload waits. Character select /
    /// disconnected / never logged in are safe to reload. (From SilentKelpie's PR 6.)
    /// </summary>
    private static bool IsInWorldForAutoReloadDeferral()
    {
        try
        {
            if (!Compatibility.LoginLifecycleHooks.HasObservedLoginComplete) return false;
            if (Compatibility.LogoutLifecycleHooks.HasObservedLogout) return false;
            int mode = Compatibility.UiFlowHooks.CurrentMode;
            // Explicit non-world screens: safe for an automatic reload.
            if (mode == Compatibility.UiFlowHooks.ModeCharacterManagement
                || mode == Compatibility.UiFlowHooks.ModeDisconnected
                || mode == Compatibility.UiFlowHooks.ModeIntro
                || mode == Compatibility.UiFlowHooks.ModeCharGen
                || mode == Compatibility.UiFlowHooks.ModeCredits
                || mode == Compatibility.UiFlowHooks.ModeDataPatch
                || mode == Compatibility.UiFlowHooks.ModeEpilogue)
                return false;
            // ModeGamePlay, or ModeNone before the first mode sample after login: hold.
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// The opt-in in-world hold (engine.json HoldReloadInWorld, default off; ReloadInWorldGate). Only
    /// called with the setting on. Waits, with the game running on this engine, while the player is in
    /// the world and the reload is an automatic one, for at most HoldReloadInWorldMaxMinutes; a hand
    /// reload that arrives meanwhile (ERl, RL, /rc rl, hotkey) or switching the setting off ends the
    /// wait. Returns true when a hand reload ended it, so the caller skips the plugin deferral too.
    /// Never throws; any failure fails open to "reload now".
    /// </summary>
    private static bool HoldReloadWhileInWorld()
    {
        try
        {
            var maxWait = TimeSpan.FromMinutes(ReloadInWorldGate.ClampMaxMinutes(EngineSettings.HoldReloadInWorldMaxMinutes));
            ReloadInWorldGate.Outcome outcome = ReloadInWorldGate.Run(
                // Re-read each poll: a hand reload or the setting turned off in the meantime ends the hold.
                stillHold: () => ReloadInWorldGate.ShouldHold(IsRecentManualReload(), EngineSettings.HoldReloadInWorld, IsInWorldForAutoReloadDeferral()),
                log: msg => RynthLog.Info(msg),
                notify: why => Compatibility.AcMainThreadQueue.EnqueueWriteToChat(
                    $"[RynthCore] A new RynthCore build is ready. It loads when you leave the world ({why}; at most {maxWait.TotalMinutes:0.#} minutes).", 1),
                maxWait: maxWait,
                waited: out TimeSpan waited);
            if (outcome != ReloadInWorldGate.Outcome.Clear)
                RynthLog.Info($"engine reload: in-world hold {outcome} after {waited.TotalSeconds:0.0} s.");
        }
        catch (Exception ex)
        {
            RynthLog.Warn($"engine reload: in-world hold failed ({ex.GetType().Name}: {ex.Message}); reloading now.");
        }
        // A hand reload that came in during the wait set the mark again: it counts as by hand.
        return IsRecentManualReload();
    }

    /// <summary>
    /// Reload deferral (2026-10-05, see ReloadDeferral.cs): asks the plugins
    /// (RynthPluginReloadBlocker) and waits up to ReloadDeferral.DefaultCap while one objects,
    /// e.g. "engine reload waiting: RynthAi in combat (a monster engaged, ...)". Says so in chat
    /// once. Never throws; a query that fails counts as "nothing objects".
    /// With engine.json HoldReloadInWorld on (default off), an automatic reload first waits until
    /// the player leaves the world (HoldReloadWhileInWorld); a hand reload is never held.
    /// </summary>
    private static void DeferReloadWhilePluginsObject()
    {
        long manualAt = Interlocked.Exchange(ref _manualReloadAtMs, 0);
        bool byHand = manualAt != 0 && Environment.TickCount64 - manualAt < ManualReloadWindowMs;

        bool hold = false;
        try { hold = EngineSettings.HoldReloadInWorld; } catch { }   // opt-in, default off: any failure = no hold
        if (hold && ReloadInWorldGate.ShouldHold(byHand, hold, IsInWorldForAutoReloadDeferral()))
        {
            if (HoldReloadWhileInWorld())
            {
                Interlocked.Exchange(ref _manualReloadAtMs, 0);
                byHand = true;
            }
        }

        if (byHand)
        {
            RynthLog.Info("engine reload: asked for by hand (ERl), so it isn't deferred for the plugins.");
            return;
        }
        var deferral = new ReloadDeferral
        {
            Query = PluginManager.QueryReloadBlockers,
            Log = msg => RynthLog.Info(msg),
            Notify = why => Compatibility.AcMainThreadQueue.EnqueueWriteToChat(
                $"[RynthCore] A new RynthCore build is ready. It loads when this is clear: {why} (at most {ReloadDeferral.DefaultCap.TotalMinutes:0} minutes).", 1),
        };
        ReloadDeferral.Outcome outcome = deferral.Run(out TimeSpan waited);
        if (outcome != ReloadDeferral.Outcome.Clear)
            RynthLog.Info($"engine reload: deferral {outcome} after {waited.TotalSeconds:0.0} s.");
    }

    private static uint InitializeCore(IntPtr lpParam)
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0)
            return 1;

        try
        {
            _initCount = lpParam.ToInt32();
            try
            {
                string version = RynthCore.App.BuildVersion.OfFile(GetEngineModulePath() ?? "");
                if (version.Length > 0) BuildStamp = version;
            }
            catch { }

            // engine.json "LoggingLevel" + "LogCategories" (launcher Logging card) set the threshold
            // and per-category levels first so every line written below honours them. The watcher
            // started later re-reads them whenever the launcher edits the file.
            string logSummary = LogSettings.Reload();

            // Set up the unified log sink BEFORE anything else so all
            // subsequent failures are captured in <CoreDir>\Logs (installer-chosen;
            // resolved here on the init worker, never under the loader lock).
            LogPaths.EnsureLogDirectory();
            if (_initCount <= 1)
            {
                LogPaths.RotateAtStartup();
                LogPaths.PruneOldLogs();
                // Index this client's session in the shared RynthCore.log so
                // "start at RynthCore.log" still points the way after the
                // per-PID split (engine/loader/plugin now write RynthCore.<pid>.log).
                LogPaths.WriteSessionPointer(
                    $"[{DateTime.Now:HH:mm:ss.fff}] [pid:{Environment.ProcessId}] [INF] [engine] session start build={BuildStamp} -> {LogPaths.LogFileName}");
            }

            InstallManagedExceptionHandlers();

            // The banner bypasses the category levels: it must be in every log, whatever engine.json says.
            LogTagged("engine", "================================================================", "INF");
            LogTagged("engine", $"RynthCore.Engine init  build={BuildStamp}  initCount={_initCount}  pid={Environment.ProcessId}", "INF");
            LogTagged("engine", $"  os={Environment.OSVersion}  clr={Environment.Version}  cwd={Environment.CurrentDirectory}", "INF");
            LogTagged("engine", $"  logging {logSummary}", "INF");
            LogTagged("engine", "================================================================", "INF");
            string hiddenCategories = LogSettings.HiddenCategoriesWarning();
            if (hiddenCategories.Length > 0) RynthLog.Warn(hiddenCategories);

            // Live logging config: launcher edits to engine.json apply without a client restart.
            // Stopped in EngineLifecycle.Shutdown before the module can be unloaded.
            LogSettings.StartWatcher();

            CrashLogger.Install();

            // Diagnostic: catch the "client frozen, bot keeps running" hangs by
            // logging AC's main-thread native stack when its EndScene beat stalls.
            MainThreadHangWatchdog.Start();

            // MultiClientHooks installs MinHook-based detours immediately. P/Invoke resolves
            // minhook.x86.dll before InitWorker runs, so preload it from the engine directory;
            // otherwise Client::IsAlreadyRunning fires unpatched on second-and-later clients.
            TryPreloadMinHookForEarlyInit();

            RunInitStep("early multi-client hooks", MultiClientHooks.Initialize);
            // Before AC's main thread resumes, so no dat or network unpack runs the stock
            // signed padding; a hot reload finds every site already patched.
            RunInitStep("large-address alignment fix", LargeAddressAlignmentPatch.Initialize);
            // DatFileShareHooks force-shares AC's data files at the CreateFile
            // layer so we can coexist with Decal-injected clients that other
            // launchers (Thwargle etc.) have already opened against the same
            // install. Must run BEFORE AC's main thread resumes — same window
            // as MultiClientHooks. Self-skips when AllowMultipleClients is off.
            RunInitStep("early DAT share hooks", DatFileShareHooks.Initialize);
            // ProcessExitHooks captures the exact native call site of any
            // ExitProcess/TerminateProcess on this PID — the only way to see
            // who killed us when the kill bypasses managed exception handlers
            // and our VEH (NativeAOT __fastfail, AC self-exit on disconnect,
            // etc.). Install early so we catch even very-early-startup kills.
            RunInitStep("early process-exit hooks", ProcessExitHooks.Initialize);
            // Logs the text of any message box (AC shows some errors in one on
            // its main thread, which then looks like a wedge to the watchdog).
            RunInitStep("message-box hooks", MessageBoxHooks.Initialize);
            // Heartbeat: one log line per second so we have a hard upper-bound
            // timestamp for when AC went silent if it dies via a path our
            // termination hooks don't catch (kernel-level kill, int 0x29 not
            // routed through RtlFailFast, hardware fault, etc.).
            RunInitStep("heartbeat logger", HeartbeatLogger.Start);

            _initThread = EngineThreads.Start("RynthCore.Init", InitWorker);

            return 0;
        }
        catch (Exception ex)
        {
            RynthLog.Info($"FATAL in RynthCoreEngineInit: {ex}");
            return 2;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetModuleHandleA(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetModuleFileNameW(IntPtr hModule, char[] lpFilename, uint nSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string lpLibFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetModuleHandleExW(uint dwFlags, IntPtr lpModuleName, out IntPtr phModule);

    private const uint GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS = 0x00000004;
    private const uint GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT = 0x00000002;

    private static string? GetEngineDirectory() =>
        GetEngineModulePath() is { } path ? Path.GetDirectoryName(path) : null;

    /// <summary>Folder of the loaded engine DLL (Runtime\, or Runtime\.engine_loads\ on a staged load).</summary>
    internal static string? EngineDirectory => GetEngineDirectory();

    /// <summary>Full path of the engine DLL actually loaded (the loader's staged copy on hot reload).</summary>
    private static unsafe string? GetEngineModulePath()
    {
        // Resolve our module by passing the address of a static method
        // compiled into our DLL. `&StaticMethod` yields a direct pointer to
        // the AOT-emitted code in our image — unlike
        // Marshal.GetFunctionPointerForDelegate which hands back a runtime
        // thunk allocated outside our module.
        // GetModuleHandleA("RynthCore.Engine.dll") fails when the loader has
        // staged us under a unique filename (RynthCore.Engine.gen2.dll etc.)
        // for hot-reload, so this is the path that always works.
#if ENGINE_CORECLR
        // A managed engine is an assembly, not a module: the loader staged it and the
        // Shim loaded it from that path.
        string location = typeof(EntryPoint).Assembly.Location;
        if (!string.IsNullOrEmpty(location))
            return location;
#endif
        IntPtr hEngine = IntPtr.Zero;
        try
        {
            delegate*<void> anchor = &EngineDirAnchor;
            if (!GetModuleHandleExW(
                    GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                    (IntPtr)anchor,
                    out hEngine))
            {
                hEngine = IntPtr.Zero;
            }
        }
        catch
        {
            hEngine = IntPtr.Zero;
        }

        // Fallback for the legacy load path (loader bypassed, engine loaded
        // directly under its canonical filename).
        if (hEngine == IntPtr.Zero)
            hEngine = GetModuleHandleA("RynthCore.Engine.dll");

        if (hEngine == IntPtr.Zero)
            return null;

        var buffer = new char[512];
        uint length = GetModuleFileNameW(hEngine, buffer, (uint)buffer.Length);
        if (length == 0)
            return null;

        return new string(buffer, 0, (int)length);
    }

    /// <summary>
    /// Loads MinHook on the RynthCoreInit thread so <see cref="MultiClientHooks.Initialize"/> can
    /// call <see cref="Hooking.MinHook"/> P/Invokes without relying on the default DLL search path
    /// (which often resolves against the AC client directory, not <c>Runtime\</c>).
    /// </summary>
    private static void TryPreloadMinHookForEarlyInit()
    {
        try
        {
            string? engineDir = GetEngineDirectory();
            if (string.IsNullOrEmpty(engineDir))
            {
                RynthLog.Info("Early MinHook preload skipped: could not resolve engine directory.");
                return;
            }

            if (PreloadNativeDll(engineDir, "minhook.x86.dll"))
                RynthLog.Verbose($"Early MinHook preload OK (engine dir: {engineDir})");
            else
                RynthLog.Info($"Early MinHook preload failed: minhook.x86.dll not found beside engine ({engineDir}).");
        }
        catch (Exception ex)
        {
            RynthLog.Info($"Early MinHook preload exception: {ex.Message}");
        }
    }

    /// <summary>
    /// No-op anchor whose address is taken via `&EngineDirAnchor` to give us a
    /// stable pointer inside the engine's own module image — used by
    /// GetEngineDirectory's GetModuleHandleExW(FROM_ADDRESS) lookup.
    /// </summary>
    private static void EngineDirAnchor() { }

    private static bool PreloadNativeDll(string engineDir, string dllName)
    {
        foreach (string path in GetNativeDllCandidates(engineDir, dllName))
        {
            if (!File.Exists(path))
                continue;

            long fileSize = TryGetFileSize(path);
            RynthLog.Verbose(fileSize > 0
                ? $"Preload: Loading {path} ({fileSize} bytes)"
                : $"Preload: Loading {path}");

            IntPtr handle = LoadLibraryW(path);
            if (handle != IntPtr.Zero)
            {
                RynthLog.Verbose($"Preload: {dllName} OK (0x{handle:X8})");
                return true;
            }

            RynthLog.Info($"Preload: FAILED to load {path} (error {Marshal.GetLastWin32Error()})");
        }

        RynthLog.Info($"Preload: FAILED to find/load {dllName} from RynthCore directories.");
        return false;
    }

    private static bool ConfigureImGuiNativeLibrary(string engineDir)
    {
        if (_imGuiResolverConfigured)
            return _imGuiNativeHandle != IntPtr.Zero;

        foreach (string path in GetImGuiNativeCandidates(engineDir))
        {
            if (!File.Exists(path))
                continue;

            long fileSize = TryGetFileSize(path);
            RynthLog.Verbose(fileSize > 0
                ? $"ImGuiNative: Loading {path} ({fileSize} bytes)"
                : $"ImGuiNative: Loading {path}");

            _imGuiNativeHandle = LoadLibraryW(path);
            if (_imGuiNativeHandle == IntPtr.Zero)
            {
                RynthLog.Info($"ImGuiNative: FAILED to load {path} (error {Marshal.GetLastWin32Error()})");
                continue;
            }

            try
            {
                NativeLibrary.SetDllImportResolver(typeof(ImGui).Assembly, ResolveImGuiNativeLibrary);
                RynthLog.Verbose($"ImGuiNative: Resolver configured (0x{_imGuiNativeHandle:X8})");
            }
            catch (InvalidOperationException ex)
            {
                RynthLog.Info($"ImGuiNative: Resolver already set - {ex.Message}");
            }

            _imGuiResolverConfigured = true;
            return true;
        }

        RynthLog.Info("ImGuiNative: FAILED to find/load a private cimgui runtime.");
        return false;
    }

    private static IntPtr ResolveImGuiNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, "cimgui", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(libraryName, "cimgui.dll", StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        return _imGuiNativeHandle;
    }

    /// <summary>
    /// Full path of <paramref name="dllName"/> in the engine's own folders (the ones
    /// <see cref="PreloadNativeDll"/> searches), or null. Never Windows' search order.
    /// </summary>
    internal static string? FindNativeDll(string dllName)
    {
        string? engineDir = GetEngineDirectory();
        if (string.IsNullOrEmpty(engineDir)) return null;
        foreach (string path in GetNativeDllCandidates(engineDir, dllName))
            if (File.Exists(path)) return path;
        return null;
    }

    private static string[] GetNativeDllCandidates(string engineDir, string dllName)
    {
        var candidates = new List<string>();
        foreach (string directory in GetEngineSearchDirectories(engineDir))
            AddCandidate(candidates, Path.Combine(directory, dllName));

        return candidates.ToArray();
    }

    private static string[] GetImGuiNativeCandidates(string engineDir)
    {
        var candidates = new List<string>();
        foreach (string directory in GetEngineSearchDirectories(engineDir))
        {
            AddCandidate(candidates, Path.Combine(directory, "RynthCore.cimgui.dll"));
            AddCandidate(candidates, Path.Combine(directory, "cimgui.dll"));
        }

        return candidates.ToArray();
    }

    private static IEnumerable<string> GetEngineSearchDirectories(string engineDir)
    {
        var directories = new List<string>();
        AddCandidate(directories, engineDir);

        string normalizedEngineDir = Path.GetFullPath(engineDir);
        bool engineDirIsRuntime = string.Equals(
            Path.GetFileName(Path.TrimEndingDirectorySeparator(normalizedEngineDir)),
            "Runtime",
            StringComparison.OrdinalIgnoreCase);

        if (engineDirIsRuntime)
        {
            string? rootDir = Directory.GetParent(normalizedEngineDir)?.FullName;
            AddCandidate(directories, Path.Combine(normalizedEngineDir, "Native"));
            AddCandidate(directories, rootDir);
            if (!string.IsNullOrWhiteSpace(rootDir))
                AddCandidate(directories, Path.Combine(rootDir, "Native"));
        }
        else
        {
            string runtimeDir = Path.Combine(normalizedEngineDir, "Runtime");
            AddCandidate(directories, runtimeDir);
            AddCandidate(directories, Path.Combine(normalizedEngineDir, "Native"));
            AddCandidate(directories, Path.Combine(runtimeDir, "Native"));

            // When loaded from a reload-staging subdir (e.g.
            // Runtime/.engine_loads/RynthCore.Engine.gen2.dll), the canonical
            // Runtime dir holding minhook/cimgui/skia is the parent. Walk up
            // a couple of levels so PreloadNativeDll can still resolve them.
            string? parent = Directory.GetParent(normalizedEngineDir)?.FullName;
            if (!string.IsNullOrWhiteSpace(parent))
            {
                AddCandidate(directories, parent);
                AddCandidate(directories, Path.Combine(parent, "Native"));

                string? grand = Directory.GetParent(parent)?.FullName;
                if (!string.IsNullOrWhiteSpace(grand))
                    AddCandidate(directories, grand);
            }
        }

        return directories;
    }

    private static void AddCandidate(List<string> candidates, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        string fullPath = Path.GetFullPath(path);
        if (!candidates.Exists(existing => string.Equals(existing, fullPath, StringComparison.OrdinalIgnoreCase)))
            candidates.Add(fullPath);
    }

    private static long TryGetFileSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Returns true if engine.json PluginPaths contains a path whose filename
    /// matches <paramref name="dllFileName"/> (case-insensitive). Used by
    /// InitWorker to gate plugin-paired Avalonia panel registrations
    /// (RynthAi/RynthChat/RynthVision) on the user's launcher selection.
    /// Filename match (not full-path) so users can keep the DLL anywhere.
    /// </summary>
    private static bool _serverMenusRegistered;

    /// <summary>
    /// The engine's own server items for the Mini Remote's sections and the menus
    /// (UI/Sections/SectionRegistry): its windows that belong to one server, added only when
    /// the plugin behind each is installed. Plugins add their own through their manifests.
    /// </summary>
    private static void RegisterServerMenus(bool rynthAi, bool rynthIlt)
    {
        if (_serverMenusRegistered) return;
        _serverMenusRegistered = true;

        // Infinite Leaftide (IltInfo: host / world-name lists, RynthILT's own verdict, or the
        // per-character "/rc ilt on"). Every ILT window is RynthILT's (since 2026-10-13), so the
        // engine adds them only when RynthILT is installed.
        const string ilt = IltInfo.ServerId;
        if (rynthIlt && rynthAi)
        {
            // RynthILT's ILT tabs in RynthAi's Mini Remote (shown once the server reports each on).
            // A RynthAi that doesn't publish sections gets these stand-ins; one that does lists them itself.
            // Character (the generic overview) is RynthOracle's Character tab since 2026.10.5.54;
            // the Leaftide tabs (Charms, Banking, Gear, Games) stay RynthILT's.
            foreach ((string label, string tab, string section) in new[]
            {
                ("Charms", "charms", "inventory"), ("Banking", "banking", "inventory"),
                ("Gear", "gear", "inventory"), ("Games", "games", "fellowship"),
            })
            {
                UI.Sections.SectionRegistry.AddEngineItem(section, ilt, label, "/rc miniremote tab " + tab,
                    $"The Mini Remote's {label} tab (RynthILT).", unlessPlugin: "RynthAi");
            }
        }
        if (rynthIlt)
        {
            // The engine's ILT panels, fed by RynthILT.
            // Guardian sits with Progression (Temple of Enlightenment): Session Stats is session stats only.
            UI.Sections.SectionRegistry.AddEngineItem("skills", ilt, "Guardian", "/rc guardian toggle",
                "Temple of Enlightenment guardians: riddle answers and Give (RynthILT).");
            UI.Sections.SectionRegistry.AddEngineItem("skills", ilt, "Progression", "/rc progression",
                "XP planner, augmentations and enlightenment (the Skills panel's Progression tab; RynthILT).");
            UI.Sections.SectionRegistry.AddEngineItem("combat", ilt, "Bounties", "/rc bounties toggle",
                "ACECustom bounties and your progress on each (RynthILT).");
        }
        UI.ServerMenus.PushToRynthAi = rynthAi;
        UI.ServerMenus.PushToRynthIlt = rynthIlt;
    }

    private static bool HasPluginDll(string dllFileName)
    {
        var paths = Plugins.EngineSettings.PluginPaths;
        for (int i = 0; i < paths.Count; i++)
        {
            if (string.Equals(Path.GetFileName(paths[i]), dllFileName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Wall-clock UTC time at which the current engine generation's InitWorker
    /// thread started. Re-stamped on every hot-reload (each generation gets a
    /// fresh InitWorker call), so reading "uptime = now - InitStartedUtc" gives
    /// the current engine generation's lifetime — exactly what the RynthAi
    /// footer wants to display alongside FPS. Default value (DateTime.MinValue)
    /// means "not yet initialised" so panels can show "—" until first frame.
    /// </summary>
    internal static DateTime InitStartedUtc { get; private set; } = DateTime.MinValue;

    private static void InitWorker()
    {
        InitStartedUtc = DateTime.UtcNow;
        try
        {
            RynthLog.Info($"InitWorker: thread started, _initCount={_initCount}, LogoutHooks.IsInstalled={LogoutLifecycleHooks.IsInstalled}, SmartBoxHooks.IsInstalled={SmartBoxHooks.IsInstalled}");

            string? engineDir = GetEngineDirectory();
            if (engineDir == null)
            {
                RynthLog.Info("FATAL: Could not determine engine directory.");
                return;
            }

            RynthLog.Info($"InitWorker: Engine directory: {engineDir}");

            if (!PreloadNativeDll(engineDir, "minhook.x86.dll"))
            {
                RynthLog.Info("FATAL: minhook.x86.dll required - aborting.");
                return;
            }

            // Non-fatal: if missing, dangerous AC API calls run without SEH protection.
            if (!PreloadNativeDll(engineDir, "RynthCore.SehTrampoline.dll"))
                RynthLog.Info("WARNING: RynthCore.SehTrampoline.dll not found — object-teardown AVs will not be caught.");
            // Probe the NEWEST export before declaring the trampoline available:
            // a successful load only proves SOME build of the hand-built DLL is
            // present. A stale binary missing a newer wrapper would leave
            // IsAvailable=true with every guarded call to that wrapper throwing
            // EntryPointNotFoundException inside a detour — silent feature death
            // plus per-event exception churn. Keep the probe name in sync with
            // the most recently added SEH_* export.
            // ⚠ Probe by MODULE NAME, not a path: PreloadNativeDll has its own
            // multi-location resolution and the DLL is ALREADY loaded at this
            // point — a path-based TryLoad against engineDir fails for
            // shadow-copied engine generations (engineDir = .engine_loads\,
            // where native DLLs don't live), which on 2026-06-11 false-flagged
            // a healthy trampoline as STALE and silently fail-closed every
            // targeted combat cast ("bot stands there, activity says Combat").
            else if (!System.Runtime.InteropServices.NativeLibrary.TryLoad(
                         "RynthCore.SehTrampoline.dll", out IntPtr sehModule)
                     || !System.Runtime.InteropServices.NativeLibrary.TryGetExport(sehModule, "SEH_ThiscallIntUintPtr", out _))
            {
                RynthLog.Warn("WARNING: RynthCore.SehTrampoline.dll is STALE (missing SEH_ThiscallIntUintPtr) — SEH wrappers DISABLED; rebuild with native\\SehTrampoline\\Build-SehTrampoline.ps1 and redeploy.");
            }
            else
            {
                SehTrampoline.MarkAvailable();
                // Native VEH crash logger (lives in the trampoline DLL — pure native, no
                // reverse-P/Invoke, so it can't re-trigger a NativeAOT fail-fast like the
                // removed managed CrashLogger VEH did). Captures the faulting context +
                // a module-resolved stack sweep for the AV (0xC0000005) / fail-fast
                // (0xC0000602) classes that bypass managed handlers, into a dedicated
                // native-crash.log so the next crash self-reports instead of going dark.
                // The shared file has one fixed name, so no per-PID rotation covers it: move a big one
                // aside on the first generation, before the native side opens it. (Later generations
                // keep the handle the first one opened, so rotating then would only be ignored.)
                if (_initCount <= 1 && LogPaths.RotateNativeCrashLogAtStartup())
                    RynthLog.Compat("native-crash.log was over 1 MB at startup: moved to native-crash.log.old");
                SehTrampoline.InstallCrashLogger(System.IO.Path.Combine(LogPaths.LogDirectory, LogPaths.NativeCrashLogName));
            }

            if (!ConfigureImGuiNativeLibrary(engineDir))
            {
                RynthLog.Info("WARNING: cimgui runtime not found - ImGui will not be available.");
                RynthLog.Info("  Ship RynthCore.cimgui.dll (or cimgui.dll) alongside RynthCore.Engine.dll");
            }
            else if (Plugins.EngineSettings.EnableImGuiBackend)
            {
                // Read the ImGui font files and the panel state here, off AC's
                // render thread (the ImGui faces read both from memory).
                ImGuiBackend.ImGuiFonts.Preload();
                UI.PanelStateStore.TryGetPanel("", out _);
                UI.PanelFaceStore.TryGet("", out _);
            }

            if (!Plugins.EngineSettings.EnableEngine)
            {
                RynthLog.Info("InitWorker: ENGINE DISABLED via engine.json (EnableEngine=false). Skipping all Compatibility/* hook installs, all OverlayHost panels, AvaloniaOverlay, D3D9Bootstrapper. Heartbeat + ProcessExitHooks + MultiClientHooks + DatFileShareHooks (already running) remain.");
                return;
            }

            // One-shot read-only string-anchor diagnostic. Runs only if the
            // user has dropped a candidate-string list at
            // %APPDATA%\RynthCore\string_anchors.txt — otherwise no-ops.
            // No hooks installed, no AC code modified; pure pattern read.
            // A diagnostic (runs when its config file exists); ~0.8 s of scanning, so the
            // first load does it and a hot reload skips it.
            if (_initCount < 2)
                StringAnchorDiagnostic.RunIfConfigured();

            int hookBudget = Plugins.EngineSettings.EngineHookCount;
            int hookIndex = 0;
            // Decal bridge mode (docs/DECAL_BRIDGE_PLAN.md) is decided before any hook goes
            // in. Without Decal in the process this is two GetModuleHandle calls, Active
            // stays false and no step is skipped.
            DecalBridgeHost.Initialize();
            // Decal stand-down (2026-10-03, docs/DECAL_BRIDGE_PLAN.md "Where Decal reads its
            // filters"): Decal is in this client but the bridge isn't (not registered as this
            // client sees the registry, protocol mismatch, mapping failure). The old coexistence
            // path that used to run here never worked - no overlay, then a crash. Stand down
            // instead: install only what DecalBridgeHost's "the Decal bridge isn't loaded" chat
            // line needs - the login event, AddTextToScroll's address (a probe, no hook) and the
            // Client::UseTime drain that runs queued chat writes on AC's main thread - and load
            // no plugins. None of those is a function Decal patches. The client then runs as a
            // plain Decal client. DecalBridge=Off (tests) and engine.json "DecalStandDown": false
            // keep the old path. Without Decal this is false (InactiveReason stays null).
            bool decalStandDown = DecalBridgeHost.ShouldStandDown;
            if (decalStandDown)
                RynthLog.Warn($"InitWorker: Decal without the bridge ({DecalBridgeHost.InactiveReason}) - standing down: " +
                    "only the login, chat-address and UseTime-drain steps install; no plugins, no overlay.");
            List<(string Name, Action Action)>? bridgeSkipped = null;
            RynthLog.Info($"InitWorker: starting hook init steps (budget={hookBudget})");
            void Step(string name, Action action)
            {
                if (InitAborted()) return;
                if (decalStandDown && name is not ("client helper hooks" or "login lifecycle hooks" or "game-tick cast drain hook"))
                    return;   // stand-down (above): logged once there
                if (DecalBridgeHost.SkipsInitStep(name))
                {
                    RynthLog.Info($"InitWorker: '{name}' skipped - bridge mode leaves this to Decal.");
                    (bridgeSkipped ??= new()).Add((name, action));
                    return;
                }
                int idx = hookIndex++;
                if (idx >= hookBudget)
                {
                    if (idx == hookBudget)
                        RynthLog.Info($"InitWorker: hook budget exhausted at index {idx} — stopping at '{name}'.");
                    return;
                }
                RunInitStep(name, action);
            }
            Step("RynthAi action hooks", ClientActionHooks.Initialize);
            Step("client helper hooks", () => ClientHelperHooks.Probe());
            Step("login lifecycle hooks", LoginLifecycleHooks.Initialize);
            Step("OnLogin command runner", OnLoginCommandRunner.Initialize);
            // File-driven chat command dispatcher: edit
            // %APPDATA%\RynthCore\dispatch.txt to fire any /command without
            // needing AC chat input. Critical for RDP / post-hot-reload
            // scenarios where keyboard input isn't reaching AC's chat box.
            Step("chat file dispatcher", ChatFileDispatcher.Start);
            Step("logout lifecycle hooks", LogoutLifecycleHooks.Initialize);
            Step("logoff origin probe", LogoffOriginProbe.Initialize);
            Step("session state registry", SessionStateRegistry.Initialize);
            Step("UI lifecycle hooks", UiLifecycleHooks.Initialize);
            Step("logo bypass", LogoBypassHooks.Start);
            Step("poll-driven auto-login", CharacterCaptureHooks.Initialize);
            Step("busy-count hooks", BusyCountHooks.Initialize);
            Step("combat-mode hooks", CombatModeHooks.Initialize);
            Step("teleport-state hooks", TeleportStateHooks.Initialize);
            Step("salvage hooks", SalvageHooks.Initialize);
            Step("radar hooks", RadarHooks.Initialize);
            Step("chat hooks", ChatHooks.Initialize);
            Step("powerbar hooks", PowerbarHooks.Initialize);
            Step("do-motion hooks", DoMotionHooks.Initialize);
            Step("smartbox-setstate hooks", SmartBoxSetStateHooks.Initialize);
            Step("appraisal hooks", AppraisalHooks.Initialize);
            Step("account hooks", AccountHooks.Initialize);
            Step("client combat hooks", () => ClientCombatHooks.Probe());
            Step("selected-target hooks", SelectedTargetHooks.Initialize);
            // Which item AC is dragging (AC keeps the old selection during a drag).
            Step("drag-start hook", DragDropHooks.Initialize);
            Step("smartbox hooks", SmartBoxHooks.Initialize);
            Step("player vitals hooks", PlayerVitalsHooks.Initialize);
            Step("enchantment hooks", () => EnchantmentHooks.Initialize());
            Step("time-sync hook", TimeSyncHooks.Initialize);
            Step("create-object hooks", CreateObjectHooks.Initialize);
            Step("delete-object hooks", DeleteObjectHooks.Initialize);
            // DB-cache teardown guard — hooks DBCache::DestroyObjectCaches entry so we
            // quiesce the off-thread plugin pump + drop the cached qualities ptr on AC's
            // MAIN thread, BEFORE AC frees its DB objects on client close. Fix for the
            // every-close 0x00416C86 (DBOCache::DestroyObj) AV that the ExitProcess /
            // WM_CLOSE quiesce is too late to prevent. Port of RynthCore2's
            // DestroyObjectCachesDetour (RynthSuite2 f8a291e).
            Step("db-cache teardown hook", DbCacheTeardownHooks.Initialize);
            // Game-logic tick hook (Client::UseTime) — drains the marshalled CAST queue
            // on AC's MAIN thread BEFORE _originalUseTime runs (drain-before variant,
            // 2026-06-03 attempt 2), so the SAME tick processes the selection + cast.
            // Re-enabled after the P1 soak: the off-thread cast races AC and AVs uncaught
            // (0xC0000005 WRITE in UIElement::GetAttribute_Bool, off the main thread).
            // See CombatActionHooks.CastSpell + GameTickHooks.UseTimeDetour for rationale.
            Step("game-tick cast drain hook", GameTickHooks.Initialize);
            // 2026-05-25 — TextParserGuardHooks DISABLED pending investigation.
            // The first deploy of this hook (12:12 build) was followed by a
            // pid 20852 crash within 7s of launch — kernel32+0x1EB0B AV WRITE
            // to 0xF08BD6EF (heap-poison address). Previous engine without this
            // hook ran ≥30min before AV. Revert to confirm; if reverted engine
            // is stable, the hook (or its MinHook installation) is the cause.
            // File remains in place as dead code for re-investigation.
            //
            // RE-ENABLED 2026-06-03 as a PURE NATIVE detour. The 7s crash above was the
            // MANAGED detour incurring a NativeAOT reverse-P/Invoke per parsed tag on
            // AC's main thread. The detour body now lives in RynthCore.SehTrampoline.dll
            // (RC_TagParserGuard — no managed transition), so that failure mode is gone.
            // Fixes the text-parser singleton race captured 2026-06-03 (0xC0000005 at
            // acclient.exe+0x27D3DA, [null+0xC]). Self-gates on the SEH trampoline.
            Step("text-parser guard hook", TextParserGuardHooks.Initialize);
            Step("update-object hooks", UpdateObjectServerDispatchHooks.Initialize);
            Step("vector-update hooks", VectorUpdateServerDispatchHooks.Initialize);
            Step("update-object-inventory hooks", UpdateObjectInventoryHooks.Initialize);
            Step("view-object-contents hooks", ViewObjectContentsHooks.Initialize);
            // RenderUI::RenderObjects: the world overlays (nameplates, Nav3D markers) draw at
            // the start of AC's 2D UI pass, after all 3D incl. foliage (2026-10-04).
            Step("UI-pass overlay hook", AcUiPassHook.Initialize);
            // SmartBox::RenderNormalMode: the video patch (/rc video) skips AC's 3D scene on
            // request; off until asked. Kill switch: engine.json DisabledUiHooks ["VideoPatch"].
            Step("video patch hook", VideoPatch.Initialize);
            // RenderDeviceD3D::SetFFAmbientColor32: a floor under AC's world ambient light that a
            // plugin can set (API v91; RynthVision's dungeon light). No floor until one is set.
            // Kill switch: engine.json DisabledUiHooks ["AmbientLight"].
            Step("ambient light hook", AmbientLight.Initialize);
            Step("vendor hooks", VendorHooks.Initialize);
            Step("chat callback hooks", ChatCallbackHooks.Initialize);
            // Flight recorder (Net/FlightRecorder.cs): before the packet hooks that feed it.
            Step("flight recorder", Net.FlightRecorder.Initialize);
            Step("raw packet hooks", RawPacketHooks.Initialize);
            Step("property-update hooks", PropertyUpdateHooks.Initialize);
            Step("auto-id service", AutoIdService.Start);
            // Screen-mode + client-teardown hooks (UIFlow::UseNewMode, Client::Cleanup) and
            // AC's tooltip / drag / drop (2026-10-05, Chorizite gaps #4-#5). Each hook has its
            // own switch (engine.json DisabledUiHooks, /rc hooks); one that doesn't resolve
            // stays out and says why in /rc hooks.
            Step("UI flow hooks", UiFlowHooks.Initialize);
            Step("UI element hooks", UiElementHooks.Initialize);
            // Bridge mode: wait (bounded) for the bridge's hello. In Auto mode a bridge that
            // never shows up means the old coexistence mode: install what was skipped.
            if (bridgeSkipped != null && !DecalBridgeHost.WaitForBridge(InitAborted))
            {
                // No hello: Decal didn't load the bridge. Stand down (see above) rather than
                // install the chat/salvage hooks over Decal's own patches. The other native
                // hooks went in during the 20 s wait and stay; nothing loads plugins to use them.
                if (DecalBridgeHost.ShouldStandDown)
                {
                    decalStandDown = true;
                    RynthLog.Warn($"InitWorker: Decal without the bridge ({DecalBridgeHost.InactiveReason}) - standing down: " +
                        "the chat and salvage hooks stay out; no plugins, no overlay.");
                }
                else
                {
                    foreach ((string name, Action action) in bridgeSkipped)
                        RunInitStep(name + " (bridge fallback)", action);
                }
            }
            if (InitAborted()) return;
            if (decalStandDown)
            {
                // No LoadPlugins, no RunPostLoginBootstrap (EndScene hook, plugin pump), and no
                // hot-reload bootstrap below: a reload of this client stands down again (the
                // registry hasn't changed). DecalBridgeHost posts the chat line at login.
                RynthLog.Info("InitWorker: stand-down complete - this client runs as a Decal client; RynthCore stays idle.");
                return;
            }
            if (Plugins.EngineSettings.EnablePlugins)
            {
                RynthLog.Info("InitWorker DIAG — calling PluginManager.LoadPlugins.");
                PluginManager.LoadPlugins(engineDir);
                RynthLog.Info("InitWorker DIAG — PluginManager.LoadPlugins returned.");
            }
            else
                RynthLog.Info("InitWorker: plugin loading disabled via engine.json (EnablePlugins=false).");

            // Defer D3D9 hooking until after character login. By that point
            // the game's device is fully initialized and stable, avoiding the
            // race condition that intermittently crashes NULLREF device creation
            // on Win11's d3d9-on-d3d12 wrapper.
            //
            // When Decal is loaded into the same acclient.exe, both we and
            // Decal install EndScene detours and modify D3D9 render state
            // each frame; symmetric save/restore between two independent
            // hookers is impossible, so the second hook leaves garbage state
            // behind and AC + Decal both stop rendering. In that case we
            // skip the entire D3D9 path: no EndScene hook, no ImGui, no
            // viewport platform/renderer. Avalonia floating panels still
            // work because they ride a separate LayeredWindow (GDI), not
            // the D3D9 swap chain.
            if (InitAborted()) return;
            LoginLifecycleHooks.LoginComplete += RunPostLoginBootstrap;

            // ── Fast-login race guard (first launch, initCount==1) ───────────
            // AC's SendLoginCompleteNotification can fire DURING the ~9 s
            // hook-init / plugin-load window above — i.e. BEFORE the line above
            // subscribed RunPostLoginBootstrap. SignalLoginComplete raises the
            // event exactly once and does NOT replay for late subscribers, so a
            // fast login leaves RunPostLoginBootstrap never called: no EndScene
            // hook, no NormalPluginPump, plugins loaded-but-never-initialized
            // (macro/raycast/radar all dead — observed live pid 29888 2026-06-13:
            // login at +9.3 s, this subscription ~0.4 s later). If login already
            // fired, run the bootstrap now. The initCount>=2 hot-reload path
            // below handles the analogous "already in-world" case via
            // MarkAlreadyComplete. RunPostLoginBootstrap is once-guarded so the
            // event and this direct call can never both execute the body.
            if (_initCount < 2 && LoginLifecycleHooks.HasObservedLoginComplete)
            {
                RynthLog.Info("D3D9: LoginComplete already observed before the bootstrap handler subscribed (fast-login race) — starting bootstrapper directly.");
                RunPostLoginBootstrap();
            }

            RynthLog.Info($"InitWorker: post-init checkpoint, _initCount={_initCount}");
            if (InitAborted()) return;

            // On a hot reload (initCount >= 2) the game's already past the
            // login gate, so SendLoginCompleteNotification won't fire again.
            // Start the bootstrapper directly — the device race the deferral
            // protects against can't happen post-login.
            if (_initCount >= 2)
            {
                if (UseDecalCoexistencePath())
                {
                    RynthLog.Info(
                        $"D3D9: Hot reload (initCount={_initCount}) — Decal coexistence " +
                        $"('{DecalDetection.DetectedModule}' loaded), skipping D3D9 bootstrapper.");
                    if (Plugins.EngineSettings.EnablePlugins)
                        InitPluginsForDecalCoexistence();
                    else
                        RynthLog.Info("DecalCoexistence: plugin pump disabled via engine.json (EnablePlugins=false).");
                }
                else if (!Plugins.EngineSettings.EnableD3D9Hook)
                {
                    RynthLog.Info($"D3D9: Hot reload (initCount={_initCount}) — D3D9Bootstrapper disabled via engine.json (EnableD3D9Hook=false).");
                }
                else
                {
                    RynthLog.Info($"D3D9: Hot reload detected (initCount={_initCount}) — starting D3D9 bootstrapper directly.");
                    D3D9Bootstrapper.Start();
                    // Non-Decal hot-reload: drive the plugin tick off the
                    // render thread (see the StartNormalPluginPump rationale).
                    if (Plugins.EngineSettings.EnablePlugins)
                        StartNormalPluginPump();
                }

                // Synthesize the LoginComplete signal so subscribers
                // (PluginManager and others) finish wiring up their post-login
                // state — the AC client won't fire the notification again until
                // the player logs out and back in.
                RynthLog.Info("LoginLifecycle: hot reload — marking login already complete.");
                LoginLifecycleHooks.MarkAlreadyComplete("hot-reload synthesis");

                // SendNoticePlayerDescReceived also won't re-fire, so the
                // PlayerVitalsHooks qualities ptr stays at zero and the buffed
                // max never refreshes — vitals fall back to "highest seen"
                // tracked across UpdateAttribute2nd events. Pull the qualities
                // ptr from the live player object so the buffed-max read works
                // immediately after the reload.
                if (PlayerVitalsHooks.TryReseedFromCurrentPlayer())
                    RynthLog.Info("PlayerVitals: hot reload — qualities ptr re-seeded from live player.");
                else
                    RynthLog.Info("PlayerVitals: hot reload — could not derive qualities ptr (will fall back to event-driven path).");
            }

            // Avalonia (the older panel UI, now only a fallback) and the ImGui faces are
            // set up separately: the ImGui faces, the chat settings and the dashboard state
            // don't depend on Avalonia, and the CoreCLR engine runs without it (Avalonia
            // can't be unloaded; docs/UNLOADABLE_ENGINE_PLAN.md).
            bool avalonia = Plugins.EngineSettings.EnableAvaloniaOverlay;
            bool dcompTest = avalonia && UI.Dcomp.DcompOverlayBootstrap.IsEnabled;
            if (avalonia)
            {
                PreloadNativeDll(engineDir, "libSkiaSharp.dll");
                PreloadNativeDll(engineDir, "libHarfBuzzSharp.dll");
                PreloadNativeDll(engineDir, "av_libglesv2.dll");

                // Parallel test harness — when env var RYNTHCORE_DCOMP_OVERLAY=1,
                // skip the production AvaloniaOverlay and start the DComp test
                // path instead. Both paths cannot coexist in one process (Avalonia
                // is single-app-per-process). Default behavior is unchanged.
                if (dcompTest)
                {
                    RynthLog.Info("InitWorker: RYNTHCORE_DCOMP_OVERLAY=1 — starting DComp test overlay instead of production AvaloniaOverlay.");
                    UI.Dcomp.DcompOverlayBootstrap.Start();
                }
            }
            else
            {
                RynthLog.Info("InitWorker: AvaloniaOverlay disabled (engine.json EnableAvaloniaOverlay=false, or the CoreCLR engine). ImGui faces only; no Skia, no offscreen window.");
            }

            if (!dcompTest)
            {
                // The panel list (titles + Avalonia view factories). Registering creates no
                // Avalonia object and starts nothing; the ImGui bar builds its buttons from it
                // and PanelRouter resolves titles with it, Avalonia on or off.
                // Engine-builtin panels (no plugin DLL pairing) — always register.
                OverlayHost.RegisterPanel("Status",  StatusPanel.Create);
                OverlayHost.RegisterPanel("Log",     LogPanel.Create);
                OverlayHost.RegisterPanel("Monsters", MonstersPanel.Create);
                OverlayHost.RegisterPanel("Items",    ItemsPanel.Create);
                OverlayHost.RegisterPanel("Settings", SettingsPanel.Create);
                OverlayHost.RegisterPanel("Nav",      NavPanel.Create);
                OverlayHost.RegisterPanel("Meta",     MetaPanel.Create);
                OverlayHost.RegisterPanel("Radar", RadarPanel.Create);

                // Plugin-paired panels: register only if the matching plugin DLL is
                // listed in engine.json PluginPaths (controlled by the launcher Plugins
                // tab). The panel UI is engine-side per the Avalonia-overlay design
                // (plugin DLLs feed data via C exports) — unchecking a plugin in the
                // launcher must take its entire surface area out of process so the
                // diagnostic "is plugin X the off-thread caller?" question is testable.
                if (HasPluginDll("RynthCore.Plugin.RynthAi.dll"))
                {
                    OverlayHost.RegisterPanel("RynthAi", RynthAiPanel.Create);
                    OverlayHost.RegisterPanel("Damage", MonsterDamagePanel.Create);
                }
                // Lua: the RynthLua plugin (or an older RynthAi that still carries it).
                if (HasPluginDll("RynthCore.Plugin.RynthLua.dll") || HasPluginDll("RynthCore.Plugin.RynthAi.dll"))
                    OverlayHost.RegisterPanel("Lua", LuaPanel.Create);
                else
                    RynthLog.Info("InitWorker: RynthAi panel skipped — DLL not in engine.json PluginPaths.");

                if (HasPluginDll("RynthCore.Plugin.RynthChat.dll"))
                {
                    OverlayHost.RegisterPanel("Chat", RynthChatPanel.Create);
                    // Regex filter-rule editor — its own panel (opened from
                    // the chat panel's Filters button or the bar).
                    OverlayHost.RegisterPanel("ChatFilters", RynthChatFiltersPanel.Create);
                }
                else
                    RynthLog.Info("InitWorker: RynthChat panel skipped — DLL not in engine.json PluginPaths.");

                if (HasPluginDll("RynthCore.Plugin.RynthVision.dll"))
                    OverlayHost.RegisterPanel("Vision", RynthVisionPanel.Create);
                else
                    RynthLog.Info("InitWorker: RynthVision panel skipped — DLL not in engine.json PluginPaths.");

                if (HasPluginDll("RynthCore.Plugin.RynthTracker.dll"))
                    OverlayHost.RegisterPanel("Tracker", RynthTrackerPanel.Create);
                else
                    RynthLog.Info("InitWorker: RynthTracker panel skipped — DLL not in engine.json PluginPaths.");

                if (HasPluginDll("RynthCore.Plugin.RynthNav.dll"))
                    OverlayHost.RegisterPanel("RynthNav", RynthNavPanel.Create);
                else
                    RynthLog.Info("InitWorker: RynthNav panel skipped — DLL not in engine.json PluginPaths.");

                // Apply persisted RynthChat settings (incl. "Hide retail chat" ->
                // ChatHooks.SuppressOriginalChat) at init so suppression takes effect
                // on login without the user having to open the Chat panel first; the
                // chat keys feed ChatModel's input line whichever chat face is up.
                //
                // ⚠ INVARIANT: this runs BEFORE AvaloniaOverlay.Start() (below) sets up
                // the Win32 platform, on THIS (InitWorker) thread. Nothing here may run a
                // panel's static ctor that constructs Avalonia objects (SolidColorBrush
                // etc.): that creates Dispatcher.UIThread with the wrong impl → the
                // overlay thread's Dispatcher.MainLoop throws PlatformNotSupportedException
                // → the ENTIRE overlay dies (bit us 2026-06-30). Hence ChatModel
                // (UI/Data/ChatData.cs), which touches no Avalonia type, not the panel
                // class. OverlayHost.RegisterPanel above is safe: a method group does NOT
                // trigger the static ctor. See rynthcore memory "overlay no-UI = dispatcher poison".
                if (HasPluginDll("RynthCore.Plugin.RynthChat.dll"))
                {
                    UI.Data.ChatModel.EnsureSettingsLoaded();
                    UI.Data.ChatModel.InstallInput();
                    // Discord relay settings (file read here, not on AC's thread); a relay left
                    // on keeps the chat feed running with the Chat panel closed.
                    UI.Data.Discord.ChatDiscord.EnsureLoaded();
                }
                // ImGui faces for panels docked in the client (UI.PanelRouter
                // picks the face). Registered even while the ImGui layer is off,
                // so /rc imgui on can use them without a reload.
                bool trackerPresent = HasPluginDll("RynthCore.Plugin.RynthTracker.dll");
                ImGuiBackend.Panels.P1Faces.Register(trackerPresent);
                if (HasPluginDll("RynthCore.Plugin.RynthVision.dll"))
                    ImGuiBackend.Panels.VisionFace.Register();
                if (HasPluginDll("RynthCore.Plugin.RynthNav.dll"))
                    ImGuiBackend.Panels.RynthNavFace.Register();
                if (HasPluginDll("RynthCore.Plugin.RynthNav.dll"))
                    ImGuiBackend.Panels.WorldMapFace.Register();
                // Characters is RynthNet's own window since 2026-10-21: its manifest declares it and the
                // engine hosts it (UI/Sections/PluginWindows.cs, docs/PLUGIN_WINDOWS.md).
                // The ILT section (Infinite Leaftide): its faces exist only with RynthILT installed
                // (Bounties, Guardian), and the bar's ILT button shows them only on ILT with RynthILT
                // loaded (IltInfo; IltStatusSource asks RynthILT once a second).
                if (HasPluginDll("RynthCore.Plugin.RynthILT.dll"))
                {
                    ImGuiBackend.Panels.BountiesFace.Register();
                    ImGuiBackend.Panels.GuardianFace.Register();
                    UI.Data.UiSources.IltStatus.Subscribe();
                }
                if (HasPluginDll("RynthCore.Plugin.RynthChat.dll"))
                {
                    ImGuiBackend.Panels.ChatFace.Register();
                    ImGuiBackend.Panels.ChatFiltersFace.Register();
                    ImGuiBackend.Panels.ChatDiscordFace.Register();
                }
                ImGuiBackend.Panels.RadarFace.Register();
                ImGuiBackend.Panels.SettingsFace.Register();
                ImGuiBackend.Panels.MetaFace.Register();
                RadarSettingsStore.Load(); // file read here, not on AC's thread
                if (HasPluginDll("RynthCore.Plugin.RynthLua.dll") || HasPluginDll("RynthCore.Plugin.RynthAi.dll"))
                    ImGuiBackend.Panels.LuaFace.Register();
                if (HasPluginDll("RynthCore.Plugin.RynthAi.dll"))
                {
                    ImGuiBackend.Panels.RynthAiFace.Register();
                    ImGuiBackend.Panels.DamageFace.Register();
                    ImGuiBackend.Panels.DamageDetailFace.Register();
                    ImGuiBackend.Panels.MonstersFace.Register();
                    ImGuiBackend.Panels.NavFace.Register();
                    ImGuiBackend.Panels.ItemsFace.Register();
                    // Load the dashboard state file here, not on AC's thread. NOT through
                    // RynthAiPanel: see the dispatcher invariant above ChatModel.EnsureSettingsLoaded.
                    RynthAiDashboardState.EnsureLoaded();
                }
                // The colour theme (theme.txt), here rather than on AC's thread; every ImGui face reads it.
                ImGuiBackend.RynthTheme.EnsureLoaded();
                RegisterServerMenus(HasPluginDll("RynthCore.Plugin.RynthAi.dll"), HasPluginDll("RynthCore.Plugin.RynthILT.dll"));
                if (trackerPresent)
                    TrackerSettings.EnsureLoaded();
                // Hide the docked Avalonia bar before it is built if the ImGui bar stands in.
                ImGuiBackend.ImGuiBar.ApplyFace();
            }

            if (avalonia && !dcompTest)
                AvaloniaOverlay.Start();
            RynthLog.Info("RynthCore bootstrap initialized.");
            Compatibility.MemoryUse.Mark("bootstrap initialized");
        }
        catch (Exception ex)
        {
            RynthLog.Info($"FATAL in InitWorker: {ex}");
        }
    }

    // In the no-Decal flow, plugin init happens on the first ImGui EndScene
    // frame, which empirically lands ~17s after LoginComplete (D3D9 bootstrap
    // → device discovery → first frame). Those seconds matter: AC fills the
    // live-object cache (148 objects in our reference run) and the player
    // qualities pointer gets seeded via SendNoticePlayerDescReceived. If we
    // call InitPlugins immediately at LoginComplete in coexistence mode,
    // the plugin loads into an empty world and never gets a replay.
    //
    // Mirror the natural latency with an explicit delay so the plugin sees
    // the same warmed-up state it would get under D3D9.
    private static readonly TimeSpan DecalCoexistencePluginInitDelay = TimeSpan.FromSeconds(10);

    // PluginManager.TickAll() is normally pumped from inside the ImGui
    // EndScene frame loop. With ImGui disabled in coexistence mode, the
    // plugin's per-frame tick (combat scanner, buff manager, target
    // tracking, snapshot publishing, ...) never iterates — the macro flag
    // flips on click but the work that flag controls never runs. Drive it
    // from a worker thread instead. 30 Hz matches normal AC framerate
    // closely enough for combat reactivity without burning cycles.
    private static readonly TimeSpan DecalCoexistenceTickInterval = TimeSpan.FromMilliseconds(33);

    /// <summary>
    /// In Decal coexistence mode the D3D9 bootstrapper never runs, so the
    /// per-frame ImGui path that normally calls
    /// <see cref="Plugins.PluginManager.InitPlugins"/> never fires. Plugins
    /// would then load but never receive Initialize / OnUIInitialized /
    /// OnLoginComplete and the world-state replay, leaving their UI alive
    /// but lifeless. Wire that init explicitly here, with IntPtr.Zero for
    /// the ImGui context and D3D device since we have neither — plugins
    /// that need them already null-check, and headless plugins (Avalonia-
    /// rendered like RynthAi) work fine without them.
    /// </summary>
    private static int _decalCoexistenceStarted;

    private static void InitPluginsForDecalCoexistence()
    {
        // Once per generation. A hot reload reaches this twice - from the initCount>=2
        // branch in InitWorker and again from RunPostLoginBootstrap via
        // MarkAlreadyComplete - and used to start TWO tick pumps (two concurrent
        // PluginManager.TickAll drivers; _tickPumpThread only remembered the second).
        // Measured on a Decal test client 2026-09-29 (spike/decal-bridge).
        if (Interlocked.CompareExchange(ref _decalCoexistenceStarted, 1, 0) != 0)
        {
            RynthLog.Info("DecalCoexistence: plugin init/pump already started in this generation - not starting a second pump.");
            return;
        }
        IntPtr hwnd = global::RynthCore.Engine.ImGuiBackend.EngineFrameController.FindGameWindow();
        if (hwnd != IntPtr.Zero)
        {
            GameHwnd = hwnd;
            RynthLog.Info($"DecalCoexistence: AC game HWND = 0x{hwnd.ToInt64():X8}");
        }
        else
        {
            RynthLog.Info("DecalCoexistence: AC game HWND not found — plugins will init without owner HWND.");
        }

        // Run the delay + init on a worker so we don't block the login
        // lifecycle callback chain.
        RynthLog.Info($"DecalCoexistence: deferring plugin init by {DecalCoexistencePluginInitDelay.TotalSeconds:0}s so AC can populate world state.");
        var worker = new Thread(() =>
        {
            try
            {
                // In short steps that watch the pump's stop flag: a teardown in these seconds
                // (a reload right after login) then finds this thread gone at once rather than
                // after the delay, and plugins are never initialised for a dying generation
                // (StopTickPumpAndJoin, PluginShutdownGate).
                if (!SleepUnlessPumpStopRequested(DecalCoexistencePluginInitDelay))
                    return;

                // Reseed the qualities pointer from the live player object
                // before plugins look at vitals. Auto-inject lands AFTER AC
                // is already past login, so SendNoticePlayerDescReceived may
                // have fired before our hook was armed; without an explicit
                // reseed the buffed max falls back to "highest seen" and the
                // panel reports wrong max HP/Stam/Mana.
                if (Compatibility.PlayerVitalsHooks.TryReseedFromCurrentPlayer())
                    RynthLog.Info("DecalCoexistence: qualities ptr re-seeded from live player.");
                else
                    RynthLog.Info("DecalCoexistence: could not derive qualities ptr (will fall back to event-driven path).");

                // Surface hook + cache state right before plugin init so the
                // log answers "did our CreateObject hook actually fire?"
                // instead of relying on absence-of-replay-line as the only
                // signal. Zero dispatches with IsInstalled=true is the
                // smoking gun for Decal having intercepted upstream and
                // not chained to our detour.
                RynthLog.Info(
                    $"DecalCoexistence: hook diagnostics: " +
                    $"CreateObject installed={Compatibility.CreateObjectHooks.IsInstalled} " +
                    $"dispatchCount={Compatibility.CreateObjectHooks.DispatchCount}, " +
                    $"liveObjects={Plugins.PluginManager.LiveObjectCount}");

                // Bridge mode: queue what the bridge buffered before InitPlugins, so its
                // replay includes it.
                if (DecalBridgeHost.Active)
                    RynthLog.Info($"DecalBridge: drained {DecalBridgeHost.Drain()} buffered record(s) before plugin init.");

                // No EndScene hook in this mode, so the per-frame main-thread prefetches the
                // pump reads (skills, spells, attackable, names/types, positions, stats, the
                // cold-login object seed) run from the Client::UseTime detour instead.
                Compatibility.GameTickHooks.HeadlessPrefetch = true;
                RynthLog.Info("DecalCoexistence: main-thread prefetches now run from Client::UseTime (no EndScene hook).");

                Plugins.PluginManager.InitPlugins(IntPtr.Zero, IntPtr.Zero, hwnd);

                // Drive the plugin tick loop. Without this, the macro
                // toggle flips but the scanner / buff manager / target
                // tracking never iterate, so panel data stays static and
                // commands like "start scanner" appear inert.
                RynthLog.Info($"DecalCoexistence: starting plugin tick pump at ~{1000 / DecalCoexistenceTickInterval.TotalMilliseconds:0} Hz.");
                int consecutiveFailures = 0;
                while (Volatile.Read(ref _tickPumpStopRequested) == 0)
                {
                    if (!EngineThreads.Sleep(DecalCoexistenceTickInterval))
                        break;
                    if (Volatile.Read(ref _tickPumpStopRequested) != 0)
                        break;
                    try
                    {
                        // ProcessPendingActions drains ALL the engine→plugin event queues
                        // (selected-target change, health update, enchantment add/remove,
                        // create/delete object, combat mode, etc.). Without this call the
                        // queues fill up but never reach the plugin: target panel stays
                        // NO TARGET, BuffManager never sees enchantments land, combat
                        // events evaporate. Historically driven from EngineFrameController's
                        // per-frame loop; this is the headless equivalent.
                        if (DecalBridgeHost.Active)
                            DecalBridgeHost.Drain();   // bridge events into the same queues
                        Plugins.PluginManager.ProcessPendingActions(IntPtr.Zero, IntPtr.Zero, hwnd);
                        Plugins.PluginManager.FlushPendingDeletes(); // as PumpPluginFrame: closes the create->delete race
                        Plugins.PluginManager.TickAll();
                        // Panel data (UiDataHub): published on the plugin-tick thread, as
                        // PumpPluginFrame does in the normal pump. Without it the panels'
                        // snapshots never refreshed with Decal loaded.
                        UI.Data.UiDataHub.Step();
                        consecutiveFailures = 0;
                    }
                    catch (Exception tickEx)
                    {
                        consecutiveFailures++;
                        // First failure logs full details; after that
                        // throttle to avoid log floods if tick keeps
                        // crashing. Bail entirely after sustained failure
                        // so we don't burn CPU pumping a broken plugin.
                        if (consecutiveFailures == 1)
                            RynthLog.Plugin($"DecalCoexistence: TickAll threw {tickEx.GetType().Name}: {tickEx.Message}");
                        else if (consecutiveFailures == 50)
                        {
                            RynthLog.Plugin("DecalCoexistence: TickAll has thrown 50 times in a row — stopping the tick pump. Plugin will be inert until reload.");
                            break;
                        }
                    }
                }
                RynthLog.Plugin("DecalCoexistence: tick pump exiting (stop requested).");
            }
            catch (Exception ex)
            {
                RynthLog.Plugin($"DecalCoexistence: deferred plugin init threw {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _tickPumpExited, 1);
            }
        })
        {
            Name = "RynthCore.DecalCoexistence.PluginInit",
            IsBackground = true,
        };
        _tickPumpThread = worker;
        EngineThreads.Track(worker);
        worker.Start();
    }

    private static int _normalPumpStarted;
    // 16 ms ≈ 60 Hz. Matches typical AC render rate so per-tick re-submitted
    // Nav3D markers (slope/water overlays) refresh in step with frames; at
    // 30 Hz the markers visibly "stepped" forward every other frame during
    // player/camera motion (the cell set shifted at half render rate).
    private static readonly TimeSpan NormalPluginPumpInterval = TimeSpan.FromMilliseconds(16);

    /// <summary>
    /// Starts the normal-mode (D3D9-hooked) plugin pump on a dedicated managed
    /// thread. Delegates to EngineFrameController.PumpPluginFrame(), which
    /// carries the device/hwnd/context the render path publishes. This
    /// replaces the 2026-05-10 design that ticked plugins inline on AC's
    /// EndScene render thread — the NativeAOT reverse-P/Invoke fail-fast root
    /// cause fixed 2026-05-16.
    ///
    /// MUST stay mutually exclusive with InitPluginsForDecalCoexistence — only
    /// one TickAll driver may exist (PluginManager.TickAll is not thread-safe).
    /// Callers reach this only on the non-Decal path (the Decal branch returns
    /// earlier). Reuses _tickPump* so EngineLifecycle.Shutdown's
    /// StopTickPumpAndJoin already tears it down before plugin Shutdown /
    /// FreeLibrary.
    /// </summary>
    // Once-guard for the post-login D3D9 + plugin-pump bootstrap. The event
    // subscription and the first-launch fast-login direct call (InitWorker) can
    // race; first one wins. (D3D9Bootstrapper.Start and StartNormalPluginPump are
    // each independently idempotent too, but guarding here keeps the body single-run
    // and the log clean.)
    private static int _postLoginBootstrapStarted;

    /// <summary>
    /// Installs the D3D9 EndScene hook and starts the off-render-thread plugin
    /// pump after AC signals login complete. Invoked from the
    /// <see cref="LoginLifecycleHooks.LoginComplete"/> event AND directly from
    /// InitWorker when login already fired before the subscription (fast-login
    /// race). Once-guarded.
    /// </summary>
    private static void RunPostLoginBootstrap()
    {
        if (Interlocked.CompareExchange(ref _postLoginBootstrapStarted, 1, 0) != 0)
            return;
        if (EngineLifecycle.IsShuttingDown)
            return;   // no EndScene hook / plugin pump for a generation being torn down

        if (UseDecalCoexistencePath())
        {
            RynthLog.Info(
                $"D3D9: Decal coexistence — '{DecalDetection.DetectedModule}' loaded, " +
                "skipping EndScene hook and ImGui init. " +
                "In-game overlay bars are disabled; Avalonia floating panels still work.");
            if (Plugins.EngineSettings.EnablePlugins)
                InitPluginsForDecalCoexistence();
            else
                RynthLog.Info("DecalCoexistence: plugin pump disabled via engine.json (EnablePlugins=false).");
            return;
        }

        if (!Plugins.EngineSettings.EnableD3D9Hook)
        {
            RynthLog.Info("D3D9: Login complete — D3D9Bootstrapper disabled via engine.json (EnableD3D9Hook=false). No EndScene hook will be installed.");
            return;
        }
        RynthLog.Info("D3D9: Login complete — starting D3D9 bootstrapper.");
        D3D9Bootstrapper.Start();

        // 2026-05-16: the plugin lifecycle no longer ticks from the EndScene
        // path. The 2026-05-10 design ran InitPlugins/ProcessPendingActions/
        // TickAll inline on AC's D3D9 render thread (the EndScene reverse-
        // P/Invoke); a GC during the plugin tick on that AC-owned thread
        // fail-fasts NativeAOT's RhpReversePInvokeAttachOrTrapThread2 (root
        // cause proven from the 2026-05-16 09:39 dump — see crash memory). The
        // tick now runs on a dedicated managed pump thread, off the render
        // thread, mirroring the proven DecalCoexistence pump.
        //
        // Still EXACTLY ONE TickAll driver: non-Decal → this normal pump;
        // Decal → InitPluginsForDecalCoexistence (the Decal branch above
        // returns before reaching here). Never both.
        if (Plugins.EngineSettings.EnablePlugins)
            StartNormalPluginPump();
        else
            RynthLog.Info("NormalPluginPump: disabled via engine.json (EnablePlugins=false).");
    }

    private static int _decalInGameLogged;

    /// <summary>
    /// True: Decal is in this client and the old coexistence path runs (no EndScene hook, no
    /// ImGui, the 30 Hz Decal pump). False without Decal, and with Decal when engine.json
    /// DecalInGameImGui (or RYNTHCORE_DECAL_IMGUI=1) asks for the normal EndScene hook and
    /// ImGui renderer there (docs/DECAL_BRIDGE_PLAN.md, step 3): the bootstrapper then finds
    /// AC's device without creating one (Decal detours Direct3DCreate9 and CreateDevice).
    /// Without Decal the setting is never read.
    /// </summary>
    private static bool UseDecalCoexistencePath()
    {
        if (!DecalDetection.IsDecalLoaded && !DecalBridgeHost.Active)
            return false;
        if (!Plugins.EngineSettings.DecalInGameImGui)
            return true;
        // AC's device must be known through the bridge first (bridge mode only); otherwise
        // the old coexistence path, so plugins still get their pump.
        if (!DecalBridgeHost.Active || !D3D9.DecalD3D9.TryResolve())
        {
            if (Interlocked.Exchange(ref _decalInGameLogged, 1) == 0)
                RynthLog.Info($"D3D9: DecalInGameImGui is on, but AC's device isn't known through the Decal bridge (bridge mode={DecalBridgeHost.Active}) - Decal coexistence path instead.");
            return true;
        }
        D3D9Bootstrapper.DecalInProcess = true;
        if (Interlocked.Exchange(ref _decalInGameLogged, 1) == 0)
            RynthLog.Info($"D3D9: Decal in the process ('{DecalDetection.DetectedModule ?? "Decal"}', bridge mode={DecalBridgeHost.Active}) and DecalInGameImGui is on - " +
                "installing the normal EndScene hook and ImGui renderer (device found without creating one).");
        return false;
    }

    private static void StartNormalPluginPump()
    {
        if (Interlocked.CompareExchange(ref _normalPumpStarted, 1, 0) != 0)
            return; // login + hot-reload paths can both reach here; start once

        var worker = new Thread(() =>
        {
            try
            {
                RynthLog.Info($"NormalPluginPump: starting at ~{1000 / NormalPluginPumpInterval.TotalMilliseconds:0} Hz (plugin tick OFF AC's render thread).");
                int consecutiveFailures = 0;
                while (Volatile.Read(ref _tickPumpStopRequested) == 0)
                {
                    if (!EngineThreads.Sleep(NormalPluginPumpInterval))
                        break;
                    if (Volatile.Read(ref _tickPumpStopRequested) != 0)
                        break;
                    try
                    {
                        global::RynthCore.Engine.ImGuiBackend.EngineFrameController.PumpPluginFrame();
                        consecutiveFailures = 0;
                    }
                    catch (Exception tickEx)
                    {
                        consecutiveFailures++;
                        if (consecutiveFailures == 1)
                            RynthLog.Plugin($"NormalPluginPump: PumpPluginFrame threw {tickEx.GetType().Name}: {tickEx.Message}");
                        else if (consecutiveFailures == 50)
                        {
                            RynthLog.Plugin("NormalPluginPump: 50 consecutive failures — stopping pump. Plugin inert until reload.");
                            break;
                        }
                    }
                }
                RynthLog.Plugin("NormalPluginPump: exiting (stop requested).");
            }
            catch (Exception ex)
            {
                RynthLog.Plugin($"NormalPluginPump: pump thread threw {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _tickPumpExited, 1);
            }
        })
        {
            Name = "RynthCore.NormalPluginPump",
            IsBackground = true,
        };
        _tickPumpThread = worker;
        EngineThreads.Track(worker);
        worker.Start();
    }

    /// Signaled by EngineLifecycle.Shutdown to stop the Decal-coexistence
    /// tick pump before plugin Shutdown / FreeLibrary so the pump can't
    /// be mid-call into a plugin whose code pages are being unmapped.
    private static int _tickPumpStopRequested;
    private static int _tickPumpExited;
    private static Thread? _tickPumpThread;

    /// <summary>
    /// Stops the headless tick pump and waits up to <paramref name="timeoutMs"/>
    /// for it to exit. Returns true if the pump exited cleanly. Called from
    /// EngineLifecycle.Shutdown before PluginManager.ShutdownAll.
    /// </summary>
    internal static bool StopTickPumpAndJoin(int timeoutMs = 2000)
    {
        Compatibility.GameTickHooks.HeadlessPrefetch = false;   // no more prefetch work once teardown starts
        // Before the null check: a pump that starts after this (a login bootstrap racing the
        // teardown) leaves its loop at once instead of ticking a generation being torn down.
        Interlocked.Exchange(ref _tickPumpStopRequested, 1);
        if (_tickPumpThread == null)
            return true;
        if (_tickPumpThread == Thread.CurrentThread)
            return Volatile.Read(ref _tickPumpExited) != 0;   // never wait for itself

        long deadline = Environment.TickCount64 + timeoutMs;
        while (Volatile.Read(ref _tickPumpExited) == 0 && Environment.TickCount64 < deadline)
            Thread.Sleep(10);

        bool exited = Volatile.Read(ref _tickPumpExited) != 0;
        if (!exited)
            RynthLog.Plugin($"Plugin tick pump did NOT exit within {timeoutMs}ms{DescribePumpInside()}.");
        return exited;
    }

    private static string DescribePumpInside()
    {
        try { string? inside = Plugins.PluginManager.DescribePumpDispatch(); return inside != null ? " (" + inside + ")" : ""; }
        catch { return ""; }
    }

    /// <summary>
    /// For PluginManager.ShutdownAll: stops the plugin pump if it is still running, waits up
    /// to <paramref name="timeoutMs"/> for its frame in flight, and says whether the plugins
    /// may be shut down now (PluginShutdownGate). At once when the pump already stopped (every
    /// EngineLifecycle.Shutdown stops it first) or never started.
    /// </summary>
    internal static Plugins.PluginShutdownGate.Verdict StopTickPumpForPluginShutdown(int timeoutMs)
    {
        Thread? pump = _tickPumpThread;
        bool onPump = pump != null && pump == Thread.CurrentThread;
        if (pump != null && !onPump && Volatile.Read(ref _tickPumpExited) == 0)
            StopTickPumpAndJoin(timeoutMs);
        return Plugins.PluginShutdownGate.Decide(pump != null, Volatile.Read(ref _tickPumpExited) != 0, onPump);
    }

    /// <summary>
    /// Non-blocking request for the headless plugin pump (NormalPluginPump /
    /// DecalCoexistence) to stop. Sets the same flag as <see cref="StopTickPumpAndJoin"/>
    /// but does NOT wait/join — safe to call from AC's main thread inside a detour
    /// (e.g. DbCacheTeardownHooks at DestroyObjectCaches entry) where joining a managed
    /// pump thread could stall AC's close or deadlock. The pump checks the flag once per
    /// iteration and parks; the heavier join still happens later via
    /// EngineLifecycle.Shutdown -> StopTickPumpAndJoin.
    /// </summary>
    internal static void RequestTickPumpStop()
    {
        Interlocked.Exchange(ref _tickPumpStopRequested, 1);
    }

    /// <summary>
    /// Sleeps <paramref name="total"/> in steps of at most 100 ms. False (stop now) as soon as
    /// the pump's stop is requested or the engine's threads are stopped.
    /// </summary>
    private static bool SleepUnlessPumpStopRequested(TimeSpan total)
    {
        long end = Environment.TickCount64 + (long)total.TotalMilliseconds;
        while (Volatile.Read(ref _tickPumpStopRequested) == 0)
        {
            long left = end - Environment.TickCount64;
            if (left <= 0) return true;
            if (!EngineThreads.Sleep((int)Math.Min(left, 100))) return false;
        }
        return false;
    }

    private static Thread? _initThread;
    private static int _initAbortLogged;

    /// <summary>
    /// Waits for InitWorker to finish. A reload can arrive while it is still installing
    /// hooks and loading plugins; teardown must not run under it (it would keep
    /// installing hooks and starting the D3D9 bootstrap for a generation being torn
    /// down). InitWorker stops at its next step once shutdown has begun.
    /// </summary>
    internal static bool WaitForInitWorker(int timeoutMs)
    {
        Thread? t = _initThread;
        if (t == null || t == Thread.CurrentThread) return true;
        return t.Join(timeoutMs);
    }

    /// <summary>True (and logged once) when shutdown began while InitWorker was running.</summary>
    private static bool InitAborted()
    {
        if (!EngineLifecycle.IsShuttingDown) return false;
        if (Interlocked.Exchange(ref _initAbortLogged, 1) == 0)
            RynthLog.Info("InitWorker: engine shutting down — stopping init here.");
        return true;
    }

    private static void RunInitStep(string name, Action action)
    {
        if (InitAborted()) return;
        try
        {
            action();
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"Compat: {name} failed during init - {ex}");
        }
    }

    private static int _managedHandlersInstalled;

    /// <summary>
    /// Install AppDomain.UnhandledException + TaskScheduler.UnobservedTaskException.
    /// Note: in NativeAOT, AccessViolationException is a Corrupted State Exception
    /// and bypasses managed handlers — those land in CrashLogger's VEH. These
    /// handlers cover normal managed exceptions that escape worker threads and
    /// background-task fault paths.
    /// </summary>
    private static void InstallManagedExceptionHandlers()
    {
        if (Interlocked.CompareExchange(ref _managedHandlersInstalled, 1, 0) != 0)
            return;

        try
        {
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        }
        catch (Exception ex)
        {
            RynthLog.Info($"InstallManagedExceptionHandlers: AppDomain hook failed - {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }
        catch (Exception ex)
        {
            RynthLog.Info($"InstallManagedExceptionHandlers: TaskScheduler hook failed - {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            // First-chance hook fires before any catch block runs. We use it
            // to capture the stack of "process-killer" exceptions (AVs, null
            // derefs from native code, stack overflows) BEFORE the runtime's
            // FailFast tears the process down. CSEs from native code (e.g.
            // calling _inqType on a weenie with null qualities) bypass our
            // VEH and managed catch blocks entirely — this is the only
            // chance to log them. Filter aggressively: every IO/parse/etc.
            // exception fires this event even when caught.
            AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
        }
        catch (Exception ex)
        {
            RynthLog.Info($"InstallManagedExceptionHandlers: FirstChance hook failed - {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes the handlers above. They are process-wide events, so under the CoreCLR
    /// host they would keep this engine generation alive after a reload (and keep its
    /// handlers running next to the new generation's).
    /// </summary>
    internal static void UninstallManagedExceptionHandlers()
    {
        if (Interlocked.CompareExchange(ref _managedHandlersInstalled, 0, 1) != 1)
            return;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        AppDomain.CurrentDomain.FirstChanceException -= OnFirstChanceException;
    }

    [ThreadStatic] private static bool _inFirstChanceHandler;

    /// <summary>
    /// Wires <see cref="AppDomain.FirstChanceException"/> using the runtime's
    /// own subscription mechanism (a separate static method so the
    /// recursion-guard ThreadStatic can be referenced without capturing
    /// closure state from the installer).
    /// </summary>
    private static void OnFirstChanceException(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
    {
        if (_inFirstChanceHandler) return;
        _inFirstChanceHandler = true;
        try
        {
            Exception? ex = e.Exception;
            if (ex == null) return;
            if (!IsProcessKillerException(ex)) return;

            RynthLog.Error("==== FIRST-CHANCE PROCESS-KILLER EXCEPTION ====");
            RynthLog.Info($"  type:    {ex.GetType().FullName}");
            RynthLog.Info($"  message: {ex.Message}");
            RynthLog.Info($"  hresult: 0x{ex.HResult:X8}");
            RynthLog.Info($"  thread:  {Thread.CurrentThread.ManagedThreadId}");
            string st = ex.StackTrace ?? "<no stack>";
            RynthLog.Info($"  stack:\r\n{st}");
            RynthLog.Info("  NOTE: NativeAOT marks AVs from native code as Corrupted State Exceptions");
            RynthLog.Info("        which bypass managed catch blocks and FailFast the process.");
            RynthLog.Info("===============================================");
        }
        catch
        {
            // Logging path must not throw — we're already on the path to FailFast.
        }
        finally
        {
            _inFirstChanceHandler = false;
        }
    }

    /// <summary>
    /// True for exception types the runtime is likely to FailFast on (AVs from
    /// native code, stack overflows, OOM). Skips routine exceptions that get
    /// caught elsewhere — IOException, FormatException, JSON parse errors etc.
    /// fire FirstChance constantly during normal operation.
    /// </summary>
    private static bool IsProcessKillerException(Exception ex)
    {
        return ex is AccessViolationException
            || ex is StackOverflowException
            || ex is OutOfMemoryException
            || ex is System.Runtime.InteropServices.SEHException
            || ex is AppDomainUnloadedException
            || ex is BadImageFormatException
            || ex is ExecutionEngineException;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            var ex = e.ExceptionObject as Exception;
            RynthLog.Error("==== UNHANDLED MANAGED EXCEPTION ====");
            RynthLog.Info($"  terminating={e.IsTerminating}  type={ex?.GetType().FullName ?? "<non-Exception>"}");
            if (ex != null)
            {
                RynthLog.Info($"  message: {ex.Message}");
                RynthLog.Info($"  stack:\r\n{ex}");
            }
            else
            {
                RynthLog.Info($"  raw: {e.ExceptionObject}");
            }
            RynthLog.Info("=====================================");
        }
        catch
        {
            // Logging path must not throw from inside an unhandled handler.
        }
    }

    private static void OnUnobservedTaskException(object? sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            RynthLog.Warn("==== UNOBSERVED TASK EXCEPTION ====");
            RynthLog.Info($"  {e.Exception}");
            RynthLog.Info("===================================");
            e.SetObserved();
        }
        catch
        {
        }
    }

    internal static void Log(string message) => LogTagged("engine", message, "INF");

    /// <summary>Back-compat: untyped lines default to INFO severity.</summary>
    internal static void LogTagged(string tag, string message) => LogTagged(tag, message, "INF");

    /// <summary>
    /// Write a tagged, leveled line to this client's per-PID session log
    /// (RynthCore.&lt;pid&gt;.log). <paramref name="level"/> is a 3-char severity
    /// (INF/WRN/ERR) so triage is `grep "[ERR]"` and the in-AC Log panel can
    /// filter. <paramref name="tag"/> identifies the origin (engine/plugin/...).
    /// The lock serializes writes from the engine module only; other module
    /// instances (loader, hot-reloaded engines) in the same process use
    /// FileShare.ReadWrite + a brief retry to coexist.
    /// </summary>
    internal static void LogTagged(string tag, string message, string level)
    {
        try
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] [pid:{Environment.ProcessId}] [{level}] [{tag}] {message}";

            lock (LogLock)
            {
                RecentLogLines.Enqueue(line);
                System.Threading.Interlocked.Increment(ref _recentLogSeq); // 64-bit: atomic for the lock-free RecentLogSeq read
                while (RecentLogLines.Count > MaxRecentLogLines)
                    RecentLogLines.Dequeue();

                AppendWithRetry(line + "\r\n");
            }
        }
        catch
        {
        }
    }

    private static void AppendWithRetry(string line)
    {
        // Retry briefly to tolerate concurrent writers (loader DLL, injector
        // process). FileShare.ReadWrite on each open lets multiple writers
        // append; the small delays smooth over rare lock collisions.
        const int MaxAttempts = 4;
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try
            {
                using var fs = new FileStream(
                    LogPaths.LogFilePath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite);
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(line);
                fs.Write(bytes, 0, bytes.Length);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                LogPaths.EnsureLogDirectory();
            }
            catch (IOException)
            {
                Thread.Sleep(5);
            }
            catch
            {
                return;
            }
        }
    }

    internal static void LogVerbose(string message)
    {
        if (ShouldLog(EngineLogLevel.Debug))
            Log(message);
    }

    /// <summary>
    /// True when a line at <paramref name="level"/> passes the global threshold. Off-level lines
    /// never pass; an Off threshold blocks everything (errors bypass this gate).
    /// </summary>
    internal static bool ShouldLog(EngineLogLevel level)
        => level != EngineLogLevel.Off && level <= LoggingLevel;

    /// <summary>Parses the global level: Off, Error, Warning/Warn, Info, Debug/Verbose, Trace. Unknown → Info.</summary>
    internal static EngineLogLevel ParseLoggingLevel(string? configuredLevel)
    {
        if (string.IsNullOrWhiteSpace(configuredLevel))
            return EngineLogLevel.Info;

        string normalized = configuredLevel.Trim();
        if (string.Equals(normalized, "Verbose", StringComparison.OrdinalIgnoreCase))
            return EngineLogLevel.Debug;
        if (string.Equals(normalized, "Warn", StringComparison.OrdinalIgnoreCase))
            return EngineLogLevel.Warning;

        return Enum.TryParse(normalized, ignoreCase: true, out EngineLogLevel parsed) && Enum.IsDefined(parsed)
            ? parsed
            : EngineLogLevel.Info;
    }

    internal static string[] GetRecentLogLines()
    {
        lock (LogLock)
            return RecentLogLines.ToArray();
    }

    /// <summary>
    /// The recent-log ring plus the sequence number of its last line (lines are
    /// numbered from 1 since load). Lets a viewer "clear" by remembering a
    /// sequence number, which keeps working after the ring wraps.
    /// </summary>
    internal static string[] GetRecentLogLines(out long lastSeq)
    {
        lock (LogLock)
        {
            lastSeq = _recentLogSeq;
            return RecentLogLines.ToArray();
        }
    }

    /// <summary>Sequence number of the newest log line (0 before any). Lock-free read.</summary>
    internal static long RecentLogSeq => System.Threading.Interlocked.Read(ref _recentLogSeq);
}
