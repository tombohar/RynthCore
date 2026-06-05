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
    internal const string BuildStamp = "2026-06-05-usetime-retval-fix";
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

            // Diagnostic: catch the "client frozen, bot keeps running" hangs by
            // logging AC's main-thread native stack when its EndScene beat stalls.
            MainThreadHangWatchdog.Start();

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
            // Heartbeat: one log line per second so we have a hard upper-bound
            // timestamp for when AC went silent if it dies via a path our
            // termination hooks don't catch (kernel-level kill, int 0x29 not
            // routed through RtlFailFast, hardware fault, etc.).
            RunInitStep("heartbeat logger", HeartbeatLogger.Start);

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

    /// <summary>
    /// Returns true if engine.json PluginPaths contains a path whose filename
    /// matches <paramref name="dllFileName"/> (case-insensitive). Used by
    /// InitWorker to gate plugin-paired Avalonia panel registrations
    /// (RynthAi/RynthChat/RynthVision) on the user's launcher selection.
    /// Filename match (not full-path) so users can keep the DLL anywhere.
    /// </summary>
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
            else
            {
                SehTrampoline.MarkAvailable();
                // Native VEH crash logger (lives in the trampoline DLL — pure native, no
                // reverse-P/Invoke, so it can't re-trigger a NativeAOT fail-fast like the
                // removed managed CrashLogger VEH did). Captures the faulting context +
                // a module-resolved stack sweep for the AV (0xC0000005) / fail-fast
                // (0xC0000602) classes that bypass managed handlers, into a dedicated
                // native-crash.log so the next crash self-reports instead of going dark.
                SehTrampoline.InstallCrashLogger(System.IO.Path.Combine(LogPaths.LogDirectory, "native-crash.log"));
            }

            if (!ConfigureImGuiNativeLibrary(engineDir))
            {
                RynthLog.Info("WARNING: cimgui runtime not found - ImGui will not be available.");
                RynthLog.Info("  Ship RynthCore.cimgui.dll (or cimgui.dll) alongside RynthCore.Engine.dll");
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
            StringAnchorDiagnostic.RunIfConfigured();

            int hookBudget = Plugins.EngineSettings.EngineHookCount;
            int hookIndex = 0;
            RynthLog.Info($"InitWorker: starting hook init steps (budget={hookBudget})");
            void Step(string name, Action action)
            {
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
            Step("vendor hooks", VendorHooks.Initialize);
            Step("chat callback hooks", ChatCallbackHooks.Initialize);
            Step("raw packet hooks", RawPacketHooks.Initialize);
            Step("property-update hooks", PropertyUpdateHooks.Initialize);
            Step("auto-id service", AutoIdService.Start);
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
            LoginLifecycleHooks.LoginComplete += () =>
            {
                if (DecalDetection.IsDecalLoaded)
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

                // 2026-05-16: the plugin lifecycle no longer ticks from the
                // EndScene path. The 2026-05-10 design ran InitPlugins/
                // ProcessPendingActions/TickAll inline on AC's D3D9 render
                // thread (the EndScene reverse-P/Invoke); a GC during the
                // plugin tick on that AC-owned thread fail-fasts NativeAOT's
                // RhpReversePInvokeAttachOrTrapThread2 (root cause proven from
                // the 2026-05-16 09:39 dump — see crash memory). The tick now
                // runs on a dedicated managed pump thread, off the render
                // thread, mirroring the proven DecalCoexistence pump.
                //
                // Still EXACTLY ONE TickAll driver: non-Decal → this normal
                // pump; Decal → InitPluginsForDecalCoexistence (the Decal
                // branch above returns before reaching here). Never both.
                if (Plugins.EngineSettings.EnablePlugins)
                    StartNormalPluginPump();
                else
                    RynthLog.Info("NormalPluginPump: disabled via engine.json (EnablePlugins=false).");
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

            if (Plugins.EngineSettings.EnableAvaloniaOverlay)
            {
                PreloadNativeDll(engineDir, "libSkiaSharp.dll");
                PreloadNativeDll(engineDir, "libHarfBuzzSharp.dll");
                PreloadNativeDll(engineDir, "av_libglesv2.dll");

                // Parallel test harness — when env var RYNTHCORE_DCOMP_OVERLAY=1,
                // skip the production AvaloniaOverlay and start the DComp test
                // path instead. Both paths cannot coexist in one process (Avalonia
                // is single-app-per-process). Default behavior is unchanged.
                if (UI.Dcomp.DcompOverlayBootstrap.IsEnabled)
                {
                    RynthLog.Info("InitWorker: RYNTHCORE_DCOMP_OVERLAY=1 — starting DComp test overlay instead of production AvaloniaOverlay.");
                    UI.Dcomp.DcompOverlayBootstrap.Start();
                }
                else
                {
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
                        OverlayHost.RegisterPanel("RynthAi", RynthAiPanel.Create);
                    else
                        RynthLog.Info("InitWorker: RynthAi panel skipped — DLL not in engine.json PluginPaths.");

                    if (HasPluginDll("RynthCore.Plugin.RynthChat.dll"))
                        OverlayHost.RegisterPanel("Chat", RynthChatPanel.Create);
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

                    AvaloniaOverlay.Start();
                }
            }
            else
            {
                RynthLog.Info("InitWorker: AvaloniaOverlay disabled via engine.json (EnableAvaloniaOverlay=false). No panels, no Skia, no offscreen window.");
            }
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
                while (Volatile.Read(ref _tickPumpStopRequested) == 0)
                {
                    Thread.Sleep(DecalCoexistenceTickInterval);
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
                        Plugins.PluginManager.ProcessPendingActions(IntPtr.Zero, IntPtr.Zero, hwnd);
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
                    Thread.Sleep(NormalPluginPumpInterval);
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
        if (_tickPumpThread == null)
            return true;

        Interlocked.Exchange(ref _tickPumpStopRequested, 1);

        long deadline = Environment.TickCount64 + timeoutMs;
        while (Volatile.Read(ref _tickPumpExited) == 0 && Environment.TickCount64 < deadline)
            Thread.Sleep(10);

        bool exited = Volatile.Read(ref _tickPumpExited) != 0;
        if (!exited)
            RynthLog.Plugin($"DecalCoexistence: tick pump did NOT exit within {timeoutMs}ms.");
        return exited;
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

            RynthLog.Info("==== FIRST-CHANCE PROCESS-KILLER EXCEPTION ====");
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
