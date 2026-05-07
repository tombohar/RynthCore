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
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.D3D9;
using RynthCore.Engine.Plugins;
using RynthCore.Engine.UI;
using RynthCore.Engine.UI.Panels;

namespace RynthCore.Engine;

public static class EntryPoint
{
    internal const string BuildStamp = "2026-03-30-v54-patternscan";
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
    private static readonly object LogLock = new();
    private static readonly Queue<string> RecentLogLines = new();

    /// <summary>Set true to enable verbose startup logging (hook ready messages, plugin lifecycle, etc.).</summary>
    internal static bool VerboseLogging = false;

    /// <summary>Set by ImGuiController once the game window is confirmed. Read by AvaloniaOverlay.</summary>
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
    [UnmanagedCallersOnly(EntryPoint = "RynthCoreShutdown")]
    public static uint Shutdown(IntPtr lpParam)
    {
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

    private static uint InitializeCore(IntPtr lpParam)
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0)
            return 1;

        try
        {
            _initCount = lpParam.ToInt32();

            // Set up the unified log sink BEFORE anything else so all
            // subsequent failures are captured in C:\Games\RynthCore\Logs.
            LogPaths.EnsureLogDirectory();
            if (_initCount <= 1)
                LogPaths.RotateAtStartup();

            InstallManagedExceptionHandlers();

            RynthLog.Info("================================================================");
            RynthLog.Info($"RynthCore.Engine init  build={BuildStamp}  initCount={_initCount}  pid={Environment.ProcessId}");
            RynthLog.Info($"  os={Environment.OSVersion}  clr={Environment.Version}  cwd={Environment.CurrentDirectory}");
            RynthLog.Info("================================================================");

            CrashLogger.Install();

            // MultiClientHooks.Initialize installs MinHook detours synchronously
            // here, BEFORE InitWorker spawns and runs the main PreloadNativeDll
            // pass. If we don't preload minhook.x86.dll first, the P/Invoke
            // into minhook fails with DllNotFoundException and the multi-
            // client hook silently skips — which means AC's
            // Client::IsAlreadyRunning check fires unpatched on the second-
            // and-later concurrent acclient.exe processes.
            string? earlyEngineDir = GetEngineDirectory();
            if (!string.IsNullOrEmpty(earlyEngineDir))
                PreloadNativeDll(earlyEngineDir, "minhook.x86.dll");
            else
                RynthLog.Info("Early-init: could not resolve engine directory — minhook may not be preloaded before MultiClientHooks.");

            RunInitStep("early multi-client hooks", MultiClientHooks.Initialize);

            var thread = new Thread(InitWorker)
            {
                Name = "RynthCore.Init",
                IsBackground = true
            };
            thread.Start();

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

    private static unsafe string? GetEngineDirectory()
    {
        // Resolve our module by passing the address of a static method
        // compiled into our DLL. `&StaticMethod` yields a direct pointer to
        // the AOT-emitted code in our image — unlike
        // Marshal.GetFunctionPointerForDelegate which hands back a runtime
        // thunk allocated outside our module.
        // GetModuleHandleA("RynthCore.Engine.dll") fails when the loader has
        // staged us under a unique filename (RynthCore.Engine.gen2.dll etc.)
        // for hot-reload, so this is the path that always works.
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

        return Path.GetDirectoryName(new string(buffer, 0, (int)length));
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

    private static void InitWorker()
    {
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

            if (!ConfigureImGuiNativeLibrary(engineDir))
            {
                RynthLog.Info("WARNING: cimgui runtime not found - ImGui will not be available.");
                RynthLog.Info("  Ship RynthCore.cimgui.dll (or cimgui.dll) alongside RynthCore.Engine.dll");
            }

            RynthLog.Info("InitWorker: starting hook init steps");
            RunInitStep("RynthAi action hooks", ClientActionHooks.Initialize);
            RunInitStep("client helper hooks", () => ClientHelperHooks.Probe());
            RunInitStep("login lifecycle hooks", LoginLifecycleHooks.Initialize);
            RunInitStep("logout lifecycle hooks", LogoutLifecycleHooks.Initialize);
            RunInitStep("session state registry", SessionStateRegistry.Initialize);
            RunInitStep("UI lifecycle hooks", UiLifecycleHooks.Initialize);
            RunInitStep("logo bypass", LogoBypassHooks.Start);
            RunInitStep("busy-count hooks", BusyCountHooks.Initialize);
            RunInitStep("combat-mode hooks", CombatModeHooks.Initialize);
            RunInitStep("teleport-state hooks", TeleportStateHooks.Initialize);
            RunInitStep("salvage hooks", SalvageHooks.Initialize);
            RunInitStep("radar hooks", RadarHooks.Initialize);
            RunInitStep("chat hooks", ChatHooks.Initialize);
            RunInitStep("powerbar hooks", PowerbarHooks.Initialize);
            RunInitStep("do-motion hooks", DoMotionHooks.Initialize);
            RunInitStep("smartbox-setstate hooks", SmartBoxSetStateHooks.Initialize);
            RunInitStep("appraisal hooks", AppraisalHooks.Initialize);
            RunInitStep("account hooks", AccountHooks.Initialize);
            RunInitStep("client combat hooks", () => ClientCombatHooks.Probe());
            RunInitStep("selected-target hooks", SelectedTargetHooks.Initialize);
            RunInitStep("smartbox hooks", SmartBoxHooks.Initialize);
            RunInitStep("player vitals hooks", PlayerVitalsHooks.Initialize);
            RunInitStep("enchantment hooks", () => EnchantmentHooks.Initialize());
            RunInitStep("time-sync hook", TimeSyncHooks.Initialize);
            RunInitStep("create-object hooks", CreateObjectHooks.Initialize);
            RunInitStep("delete-object hooks", DeleteObjectHooks.Initialize);
            RunInitStep("update-object hooks", UpdateObjectServerDispatchHooks.Initialize);
            RunInitStep("vector-update hooks", VectorUpdateServerDispatchHooks.Initialize);
            RunInitStep("update-object-inventory hooks", UpdateObjectInventoryHooks.Initialize);
            RunInitStep("view-object-contents hooks", ViewObjectContentsHooks.Initialize);
            RunInitStep("vendor hooks", VendorHooks.Initialize);
            RunInitStep("chat callback hooks", ChatCallbackHooks.Initialize);
            RunInitStep("raw packet hooks", RawPacketHooks.Initialize);
            RunInitStep("property-update hooks", PropertyUpdateHooks.Initialize);
            RunInitStep("auto-id service", AutoIdService.Start);
            PluginManager.LoadPlugins(engineDir);

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
            LoginLifecycleHooks.LoginComplete += () =>
            {
                if (DecalDetection.IsDecalLoaded)
                {
                    RynthLog.Info(
                        $"D3D9: Decal coexistence — '{DecalDetection.DetectedModule}' loaded, " +
                        "skipping EndScene hook and ImGui init. " +
                        "In-game overlay bars are disabled; Avalonia floating panels still work.");
                    InitPluginsForDecalCoexistence();
                    return;
                }

                RynthLog.Info("D3D9: Login complete — starting D3D9 bootstrapper.");
                D3D9Bootstrapper.Start();
            };

            RynthLog.Info($"InitWorker: post-init checkpoint, _initCount={_initCount}");

            // On a hot reload (initCount >= 2) the game's already past the
            // login gate, so SendLoginCompleteNotification won't fire again.
            // Start the bootstrapper directly — the device race the deferral
            // protects against can't happen post-login.
            if (_initCount >= 2)
            {
                if (DecalDetection.IsDecalLoaded)
                {
                    RynthLog.Info(
                        $"D3D9: Hot reload (initCount={_initCount}) — Decal coexistence " +
                        $"('{DecalDetection.DetectedModule}' loaded), skipping D3D9 bootstrapper.");
                    InitPluginsForDecalCoexistence();
                }
                else
                {
                    RynthLog.Info($"D3D9: Hot reload detected (initCount={_initCount}) — starting D3D9 bootstrapper directly.");
                    D3D9Bootstrapper.Start();
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

            PreloadNativeDll(engineDir, "libSkiaSharp.dll");
            PreloadNativeDll(engineDir, "libHarfBuzzSharp.dll");
            PreloadNativeDll(engineDir, "av_libglesv2.dll");
            OverlayHost.RegisterPanel("Status",  StatusPanel.Create);
            OverlayHost.RegisterPanel("Log",     LogPanel.Create);
            OverlayHost.RegisterPanel("RynthAi", RynthAiPanel.Create);
            OverlayHost.RegisterPanel("Monsters", MonstersPanel.Create);
            OverlayHost.RegisterPanel("Items",    ItemsPanel.Create);
            OverlayHost.RegisterPanel("Settings", SettingsPanel.Create);
            OverlayHost.RegisterPanel("Nav",      NavPanel.Create);
            OverlayHost.RegisterPanel("Meta",     MetaPanel.Create);
            OverlayHost.RegisterPanel("Radar", RadarPanel.Create);
            AvaloniaOverlay.Start();
            RynthLog.Info("RynthCore bootstrap initialized.");
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
    private static void InitPluginsForDecalCoexistence()
    {
        IntPtr hwnd = global::RynthCore.Engine.ImGuiBackend.ImGuiController.FindGameWindow();
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
                Thread.Sleep(DecalCoexistencePluginInitDelay);

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

                Plugins.PluginManager.InitPlugins(IntPtr.Zero, IntPtr.Zero, hwnd);

                // Drive the plugin tick loop. Without this, the macro
                // toggle flips but the scanner / buff manager / target
                // tracking never iterate, so panel data stays static and
                // commands like "start scanner" appear inert.
                RynthLog.Info($"DecalCoexistence: starting plugin tick pump at ~{1000 / DecalCoexistenceTickInterval.TotalMilliseconds:0} Hz.");
                int consecutiveFailures = 0;
                while (true)
                {
                    Thread.Sleep(DecalCoexistenceTickInterval);
                    try
                    {
                        Plugins.PluginManager.TickAll();
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
            }
            catch (Exception ex)
            {
                RynthLog.Plugin($"DecalCoexistence: deferred plugin init threw {ex.GetType().Name}: {ex.Message}");
            }
        })
        {
            Name = "RynthCore.DecalCoexistence.PluginInit",
            IsBackground = true,
        };
        worker.Start();
    }

    private static void RunInitStep(string name, Action action)
    {
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
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            var ex = e.ExceptionObject as Exception;
            RynthLog.Info("==== UNHANDLED MANAGED EXCEPTION ====");
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
            RynthLog.Info("==== UNOBSERVED TASK EXCEPTION ====");
            RynthLog.Info($"  {e.Exception}");
            RynthLog.Info("===================================");
            e.SetObserved();
        }
        catch
        {
        }
    }

    internal static void Log(string message) => LogTagged("engine", message);

    /// <summary>
    /// Write a tagged line to the unified log. Called by the loader, injector,
    /// plugin SDK, and CrashLogger so every line in the file identifies its
    /// origin. The lock here serializes writes from the engine module only;
    /// other processes (injector) and module instances (loader, hot-reloaded
    /// engines) use FileShare.ReadWrite + a brief retry to coexist.
    /// </summary>
    internal static void LogTagged(string tag, string message)
    {
        try
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] [pid:{Environment.ProcessId}] [{tag}] {message}";

            lock (LogLock)
            {
                RecentLogLines.Enqueue(line);
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
        if (VerboseLogging)
            Log(message);
    }

    internal static string[] GetRecentLogLines()
    {
        lock (LogLock)
            return RecentLogLines.ToArray();
    }
}
