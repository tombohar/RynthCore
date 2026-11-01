// ============================================================================
//  RynthCore.Engine - ImGui/EngineFrameController.cs
//  Top-level orchestrator: drives the per-frame engine pipeline from EndScene.
//
//  Per-frame work splits into two layers:
//    * Always-on (when EndScene fires): GameMatrixCapture, Nav3DRenderInjector
//      install/reset, PluginManager init/pending/delete/tick, DX9Backend.RenderNav3D
//      fallback. These run regardless of EngineSettings.EnableImGuiBackend so
//      world→screen, nav markers, and plugin lifecycle stay alive even when
//      the ImGui surface is disabled.
//    * ImGui-gated (only when EngineSettings.EnableImGuiBackend is true):
//      Win32Backend.NewFrame / DX9Backend.NewFrame, ImGui.NewFrame/EndFrame/
//      Render, RynthCoreShell + plugin RenderAll, DX9Backend.RenderDrawData,
//      mouse/keyboard capture flag updates.
//
//  Renamed 2026-05-10 from ImGuiController — the type now owns more than
//  ImGui's frame; the ImGui block is just one branch of its work.
// ============================================================================

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ImGuiNET;
using RynthCore.Engine.D3D9;
using RynthCore.Engine.Plugins;
using RynthCore.Install;

namespace RynthCore.Engine.ImGuiBackend;

internal static class EngineFrameController
{
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private static bool _imguiInitialized;
    private static bool _imguiInitFailed;
    private static bool _coreResolved;
    private static bool _pluginsInitialized;
    // Pump frames plugin init has waited for the ImGui context (PumpPluginFrame; ~16 ms each).
    private static int _pluginInitContextWaits;
    private const int PluginInitContextMaxWaits = 300;
    private static IntPtr _context;
    private static IntPtr _gameHwnd;

    /// <summary>
    /// Live D3D9 device pointer, published by <see cref="OnEndScene"/> on AC's
    /// render thread and consumed by <see cref="PumpPluginFrame"/> on the
    /// dedicated plugin pump thread. Zero until the first real backbuffer
    /// frame — the pump no-ops until then (we need the HWND/core resolved and
    /// a device to hand plugins). Volatile: written by render thread, read by
    /// pump thread; IntPtr writes are atomic on this platform.
    /// </summary>
    internal static volatile IntPtr CachedDevice;
    private static int _pluginPumpInFrame; // re-entrancy guard for the pump
    private static int _pumpFrameFaults;   // PumpPluginFrame throws caught (pump thread only)

    // ImGui pipeline diagnostics (render thread only): one-time lines plus a 30 s heartbeat so a
    // log shows whether frames start, which branch runs and whether draw data is submitted.
    private const long HeartbeatIntervalMs = 30_000;
    private static long _engineErrorCount;
    private static long _nextEngineErrorLogAt;
    private static bool _loggedFirstSubmit;
    private static int _loggedBranch = -1;
    private static string _initFailStep = "unknown";
    private static long _drawSubmits;
    private static long _nextHeartbeatAt;

    // Device for this frame's built ImGui draw data, submitted by RenderDeferredImGui after the
    // Avalonia blit. Render thread only; Zero = nothing pending. The draw data stays valid until
    // the next NewFrame on the main context.
    private static IntPtr _deferredSubmitDevice;
    // Build + pop-out time of the frame waiting in _deferredSubmitDevice (UiFrameStats adds the submit).
    private static long _deferredUiTicks;

    private static long _lastFrameTicks;
    private static int _frameCount;

    /// <summary>
    /// Resolves the AC game HWND (via the D3D9 device, falling back to
    /// EnumWindows) and seeds <see cref="EntryPoint.GameHwnd"/>. Required by
    /// the always-on path (Nav3DRenderInjector + DX9Backend core delegates)
    /// and by the ImGui init path. Idempotent — safe to call every frame.
    /// </summary>
    public static bool EnsureCore(IntPtr pDevice)
    {
        if (_coreResolved && _gameHwnd != IntPtr.Zero)
            return true;

        // Prefer the actual D3D device window so startup does not latch onto a splash/loading HWND.
        _gameHwnd = DX9Backend.GetDeviceWindow(pDevice);
        if (_gameHwnd == IntPtr.Zero)
        {
            RynthLog.Render("EngineFrameController: D3D device window unavailable, falling back to EnumWindows.");
            _gameHwnd = FindGameWindow();
        }

        if (_gameHwnd == IntPtr.Zero)
        {
            RynthLog.Render("EngineFrameController: Could not find game window.");
            return false;
        }

        RynthLog.Render($"EngineFrameController: Game HWND = 0x{_gameHwnd:X8}");
        EntryPoint.GameHwnd = _gameHwnd;
        _coreResolved = true;
        // Pop-outs hold D3DPOOL_DEFAULT render targets: release them before AC resets a lost device.
        D3D9.DeviceResetHook.InstallFromDevice(pDevice);
        return true;
    }

    // ImGui init runs in two parts since 2026-11-01: the first makes the context (handed to the
    // plugins at once, as before) and starts the font atlas bake on a worker (ImGuiFonts.
    // StartInitialBake); frames build nothing until the bake is done, and the frame that finds it
    // done swaps it in, sets up the Win32 backend and uploads the texture. The bake used to run
    // here, on AC's thread, for 2.5-5 s of every hot reload (the main-thread hangs of 10-31/11-01).
    private enum InitStep { Done, Baking, Failed }

    private static long _initStartedTicks;

    private static InitStep Init(IntPtr pDevice)
    {
        if (_imguiInitialized)
            return InitStep.Done;
        bool begin = _context == IntPtr.Zero;

        if (begin)
        {
            if (!EnsureCore(pDevice))
            {
                _initFailStep = "EnsureCore (game window not found)";
                return InitStep.Failed;
            }

            // Before any context exists: the loaded cimgui.dll must be the exact
            // build ImGui.NET wraps, or every struct read below is at wrong offsets.
            MainThreadHangWatchdog.Step = "ImGui init: self-test";
            _initStartedTicks = Stopwatch.GetTimestamp();
            Compatibility.MemoryUse.Mark("ImGui init: before the self-test");
            if (!ImGuiSelfTest.Run(EntryPoint.ImGuiNativeHandle))
                return InitStep.Failed;
            Compatibility.MemoryUse.Mark("ImGui self-test done");
        }

        IntPtr previousContext = ImGuiNET.ImGui.GetCurrentContext();
        if (begin)
        {
            if (previousContext != IntPtr.Zero)
                RynthLog.Render($"EngineFrameController: Existing ImGui context detected (0x{previousContext:X8}) - isolating RynthCore context.");
            MainThreadHangWatchdog.Step = "ImGui init: context";
            _context = ImGuiNET.ImGui.CreateContext();
            Compatibility.MemoryUse.Mark("ImGui context created");
        }
        InitStep result = InitStep.Failed;
        try
        {
            ImGuiNET.ImGui.SetCurrentContext(_context);
            ImGuiIOPtr io = ImGuiNET.ImGui.GetIO();

            if (begin)
            {
                SetUpContext(io);
                MainThreadHangWatchdog.Step = "ImGui init: starting the font bake";
                if (ImGuiFonts.StartInitialBake(_uiScale))
                {
                    RynthLog.UI("EngineFrameController: the font atlas bakes on a worker; ImGui frames start once it is done.");
                    result = InitStep.Baking;
                    return result;
                }
                // No worker to be had: the old way, on this thread.
                MainThreadHangWatchdog.Step = "ImGui init: fonts on the render thread";
                ImGuiFonts.Build(io, _uiScale);
            }
            else
            {
                MainThreadHangWatchdog.Step = "ImGui init: swapping in the baked atlas";
                if (!ImGuiFonts.SwapFinished(io, _uiScale, out string baked))
                {
                    result = InitStep.Baking;   // still baking: no ImGui frame yet
                    return result;
                }
                double sinceMs = (Stopwatch.GetTimestamp() - _initStartedTicks) * 1000.0 / Stopwatch.Frequency;
                RynthLog.UI($"ImGuiFonts: first atlas {baked} ({sinceMs:0} ms after ImGui init began).");
                Compatibility.MemoryUse.Mark("ImGui fonts swapped in");
            }

            MainThreadHangWatchdog.Step = "ImGui init: Win32 backend";
            if (!Win32Backend.Init(_gameHwnd))
            {
                _initFailStep = "Win32Backend.Init";
                return result;
            }

            Compatibility.MemoryUse.Mark("Win32 backend ready");
            MainThreadHangWatchdog.Step = "ImGui init: font texture upload";
            if (!DX9Backend.InitImGui(pDevice))
            {
                _initFailStep = "DX9Backend.InitImGui";
                return result;
            }
            Compatibility.MemoryUse.Mark("DX9 backend ready (font texture)");

            _lastFrameTicks = Stopwatch.GetTimestamp();
            _imguiInitialized = true;
            result = InitStep.Done;

            RynthLog.UI("EngineFrameController: ImGui initialized.");
            return result;
        }
        finally
        {
            if (result == InitStep.Failed && _context != IntPtr.Zero)
            {
                // A bake may still be running: no context may be destroyed while it allocates.
                ImGuiFonts.WaitForWorkers();
                ImGuiNET.ImGui.DestroyContext(_context);
                _context = IntPtr.Zero;
            }

            ImGuiNET.ImGui.SetCurrentContext(previousContext);
            MainThreadHangWatchdog.Step = "EndScene: ImGui frame";
        }
    }

    /// <summary>The new context's ini handling, flags, DPI scale and theme (Init's first part). Context current.</summary>
    private static void SetUpContext(ImGuiIOPtr io)
    {
        // Panel layout per character (UI/PanelLayout): the engine loads and saves the
        // imgui.ini of the layout in use itself (the character's, or the PC-wide
        // default), so Dear ImGui gets no file name: it would load only the one it
        // started with and write it from the render thread, not atomically. The
        // context starts with the layout already applied, if there is one.
        if (UI.PanelLayout.Initialized)
        {
            unsafe { io.NativePtr->IniFilename = null; }
            UI.PanelLayout.OnContextCreated(LoadIniFn);
            RynthLog.Info("EngineFrameController: imgui.ini is loaded and saved by the panel layout (UI/PanelLayout).");
        }
        // Without it (an engine that didn't get that far), as before: pin imgui.ini to
        // a stable absolute path so window positions/sizes persist across plugin
        // reloads (RL) and AC restarts. The default ImGui behavior writes to the
        // working directory which can differ between launches. Allocated once,
        // kept alive for the process lifetime.
        else try
        {
            string iniPath = System.IO.Path.Combine(RynthCore.Install.RynthInstallPaths.RynthAiDir, "imgui.ini");
            System.IO.Directory.CreateDirectory(RynthCore.Install.RynthInstallPaths.RynthAiDir);
            IntPtr iniPtr = System.Runtime.InteropServices.Marshal.StringToHGlobalAnsi(iniPath);
            unsafe { io.NativePtr->IniFilename = (byte*)iniPtr; }
            RynthLog.Info($"EngineFrameController: imgui.ini pinned to {iniPath}");
        }
        catch (Exception ex)
        {
            RynthLog.Render($"EngineFrameController: failed to pin imgui.ini path - {ex.Message}");
        }

        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
        // Multi-viewport (ViewportsEnable) is deliberately never enabled: it
        // runs many native->managed platform/renderer callbacks per frame on
        // AC's thread and creates HWNDs from inside EndScene. Pop-out panels,
        // if built, are driven explicitly by the engine instead
        // (docs/IMGUI_PARITY_PLAN.md section 4.2).

        // DPI scale for fonts and style metrics. Logged next to Avalonia's
        // scale: docked positions are stored in Avalonia logical units, so
        // the two must agree for a panel to land in the same place in
        // either face (plan section 4.4).
        _uiScale = UiScale.ForWindow(_gameHwnd);
        RynthLog.UI($"EngineFrameController: ImGui scale {_uiScale:0.##} (Avalonia input scale {UI.AvaloniaOverlay.InputScale:0.##}).");
        RynthTheme.Apply(ImGuiNET.ImGui.GetStyle(), _uiScale);
        Compatibility.MemoryUse.Mark("ImGui theme applied");
    }

    public static void Shutdown()
    {
        // A context whose first font atlas is still baking (Init's two parts) goes too.
        if (!_imguiInitialized && _context == IntPtr.Zero)
            return;
        bool framesRan = _imguiInitialized;

        IntPtr previousContext = ImGuiNET.ImGui.GetCurrentContext();
        IntPtr contextToDestroy = _context;

        try
        {
            ImGuiNET.ImGui.SetCurrentContext(contextToDestroy);

            PluginManager.ShutdownAll();
            // No context may be destroyed while a font worker runs (ImGuiFonts, re-bake note).
            ImGuiFonts.WaitForWorkers();
            // Pop-outs share the main font atlas: their contexts go before the main one.
            ImGuiPopOuts.Shutdown();
            OverlayTextureRenderer.Shutdown();
            // Its lists are drawn by DX9Backend from the DIP hook, which is still live.
            UnderUiLayer.Shutdown();
            ScriptIcons.Shutdown();   // script window icon textures, before the backend goes
            WorldMapTiles.Shutdown(); // the world map's tile textures, likewise
            WorldMapVectors.Shutdown();
            RetailSprites.Shutdown(); // retail UI pictures (Inventory Classic view), likewise
            DX9Backend.Shutdown();
            // Do NOT call Win32Backend.Shutdown() here. EngineLifecycle.Shutdown
            // owns that step explicitly, AFTER AvaloniaOverlay.Stop — floating
            // panels can only be destroyed on the game thread by our subclass
            // WndProc (Win32Backend.Shutdown sweeps any still open before it
            // unhooks), so restoring AC's original WndProc this early sends
            // those destroys to AC's proc, which ignores them, and the orphaned
            // panel HWND outlives the engine load.
            // The windows' places go to the layout's imgui.ini now (with no IniFilename
            // DestroyContext doesn't save them; UI/PanelLayout).
            try
            {
                // Not before the first frame (the atlas still baking): nothing moved, nothing to save.
                if (UI.PanelLayout.Initialized && framesRan)
                    UI.PanelLayout.SaveIniOnShutdown(ImGuiNET.ImGui.SaveIniSettingsToMemory());
            }
            catch (Exception ex) { RynthLog.UI($"EngineFrameController: imgui.ini save at shutdown threw {ex.GetType().Name}: {ex.Message}"); }
            ImGuiNET.ImGui.DestroyContext(contextToDestroy);
            ImGuiFonts.Free(); // the atlas referenced these buffers; the context is gone now

            _imguiInitialized = false;
            _coreResolved = false;
            _context = IntPtr.Zero;
        }
        finally
        {
            ImGuiNET.ImGui.SetCurrentContext(previousContext == contextToDestroy ? IntPtr.Zero : previousContext);
        }
    }

    public static void OnEndScene(IntPtr pDevice)
    {
        // ── Always-on engine pipeline ────────────────────────────────────
        // These run on every EndScene tick, regardless of
        // EngineSettings.EnableImGuiBackend, so world→screen capture, nav-
        // marker rendering, and plugin lifecycle stay alive when the ImGui
        // surface is disabled.
        // A frame that throws before its build must not resubmit last frame's draw data.
        _deferredSubmitDevice = IntPtr.Zero;
        try
        {
            // Resolve game HWND and seed EntryPoint.GameHwnd. Required even
            // in ImGui-off mode so Avalonia owner-window binding works.
            MainThreadHangWatchdog.Step = "EndScene: engine pipeline (layout, prefetches, queues)";
            EnsureCore(pDevice);

            // Settings > Display: borderless windowed + AC window placement. Throttled; reads only
            // here - the window itself is changed from the game WndProc (AcWindowChrome).
            try { UI.AcWindowChrome.Tick(_gameHwnd); }
            catch (Exception ex) { RynthLog.UI($"AcWindowChrome.Tick threw {ex.GetType().Name}: {ex.Message}"); }

            // Panel layout per character: switches to the character's layout (or back to the
            // PC-wide default) and applies a loaded one, before anything below draws a panel.
            // A layout waits for the ImGui context while the ImGui layer is in use.
            UI.PanelLayout.Tick(imguiInUse: Plugins.EngineSettings.EnableImGuiBackend && !_imguiInitFailed,
                imguiReady: _imguiInitialized, SnapshotIniFn, LoadIniFn);

            // Cache the D3D9 device function pointers used by Nav3DRenderInjector,
            // RenderNav3D, and the always-on render path. This is a strict subset
            // of what InitImGui needs and is safe to run without ImGui state.
            DX9Backend.InitCore(pDevice);

            // Install the DrawIndexedPrimitive hook that injects Nav3D markers at
            // the 3D→UI ZENABLE transition so they render BEHIND AC's UI. Idempotent
            // (installs once); placed here because its Detour reads the device
            // delegates InitCore (above) just cached. WITHOUT THIS the injector never
            // accumulates, RenderedThisFrame stays false on every frame, and the
            // end-of-frame fallback below paints markers OVER the UI. This call was
            // dropped in the v0.19 Nav3D double-buffer refactor — restored here.
            // Since 2026-10-04 this is the second choice: with AcUiPassHook live the
            // overlays draw at AC's UI pass and SyncHookState (end of frame) switches
            // this hook off; it comes back for /rc worldlayer transition or without it.
            D3D9.Nav3DRenderInjector.Install(pDevice);

            // Capture the game's View/Projection matrices before ImGui touches them
            GameMatrixCapture.CaptureFrame(pDevice);
            // ...and AC's 3D view viewport: at AC's UI pass (AcUiPassHook) the viewport is
            // full-screen, and the Nav3D markers draw in this one there.
            DX9Backend.CaptureSceneViewport(pDevice);

            // Refresh the player-skill snapshot on AC's main thread (this
            // EndScene runs on it). The plugin tick is pumped off-thread and
            // can't read skills live (TryGetObjectQualitiesPtr fail-closes);
            // without this it gets (0,0) → tier-1 casts and never buffs.
            // Throttled internally.
            Compatibility.ClientObjectHooks.PrefetchPlayerSkills();
            // Same rationale + same main-thread guard: snapshot the spellbook
            // so the off-thread pump can resolve tiers against spells the char
            // actually knows (throttled internally).
            Compatibility.ClientObjectHooks.PrefetchKnownSpells();
            // Same main-thread guard + rationale: snapshot AC's real
            // ObjectIsAttackable for every live object so the off-thread pump
            // can tell NPC/vendor (attackable=false) from monster. Without it
            // the pump got hardcoded true and the classifier promoted NPCs to
            // attackable creatures → war magic cast at NPCs (throttled
            // internally).
            Compatibility.ClientObjectHooks.PrefetchAttackable();
            // Same main-thread guard + rationale: snapshot object names and
            // item types for every live object so the off-thread plugin pump
            // can classify objects without walking AC's live CObjectMaint
            // table (0x0067E779 READ-AV class; throttled internally at 3 s).
            Compatibility.ClientObjectHooks.PrefetchObjectIdentity();
            // Same main-thread guard: snapshot every live object's POSITION so the
            // off-thread plugin pump reads position from cache instead of resolving
            // _getWeenieObject live — that resolution is AC's CObjectMaint walk and
            // is the dump-verified 0x0067E779 READ-AV when done on the pump thread.
            // Sampled every frame (positions are volatile), zero-alloc.
            Compatibility.ClientObjectHooks.PrefetchPositions();
            // Refresh the cached player id every frame (unthrottled: the native leaf is
            // two loads) and the other small main-thread snapshots (cast gate, pose,
            // ...). Off-thread callers are served from these instead of reading AC
            // memory from the pump thread. Also runs from the Client::UseTime drain.
            Compatibility.MainThreadSnapshots.Tick();
            // Same main-thread guard: snapshot XP/luminance/deaths/vitae (main-thread-only
            // Inq* reads) so the GetEngineStatusJson host bridge can serve them off-thread (throttled).
            Compatibility.ClientObjectHooks.PrefetchPlayerStats();
            Compatibility.ClientObjectHooks.ServeObjectQualitiesRequest();   // v84 GetObjectProperties (none asked: one read)

            // Cold-login object backfill on the SAME AC main thread as the
            // skill prefetch above: deliver objects already present at login
            // (the incremental CreateObject hook only catches NEW spawns, so
            // login-present mobs were never reaching the plugin — proven via
            // per-id ClassifyTrace). One-shot per login, IsOnMainThread-gated
            // and attempt-bounded internally; reuses the guarded
            // QueueCreateObject delivery path. EndScene is a build-independent
            // anchor (D3D9 hook, not string-resolved like SmartBox).
            try { Plugins.PluginManager.TrySeedLiveObjectsFromCObjectMaintOnce(); }
            catch (Exception ex) { RynthLog.Plugin($"CObjectMaint seed threw {ex.GetType().Name}: {ex.Message}"); }

            // P1: drain off-thread bot actions (UseObject / ChangeCombatMode / melee /
            // missile / movement / jump) here on AC's MAIN thread. The pump enqueues
            // them instead of calling AC directly, so AC mutates its single-threaded
            // object/animation (CSequence) state on the same thread that updates it —
            // closing the off-thread-mutation corruption class (the CSequence::
            // update_internal AV captured 2026-06-03, the 0x0055FA24 range-list AV, etc).
            // CastSpell is deliberately NOT routed here (stays off-thread + SEH).
            try { Compatibility.AcMainThreadQueue.Drain(); }
            catch (Exception ex) { RynthLog.Plugin($"AcMainThreadQueue.Drain threw {ex.GetType().Name}: {ex.Message}"); }

            // The world overlays (Nav3D markers, nameplates, combat text) normally drew
            // mid-frame, before this EndScene: at the start of AC's 2D UI pass
            // (AcUiPassHook), or without that hook at the 3D->UI ZENABLE transition
            // (Nav3DRenderInjector's DIP hook). A frame where neither fired (portal space,
            // a loading screen, no AC UI) draws them at EndScene instead - see
            // Nav3DRenderInjector.PresentAtEndScene below. ResetFrame at the END of
            // OnEndScene primes the next frame.

            // Publish the live device pointer for the off-render-thread plugin
            // pump (see PumpPluginFrame). The D3D9 device is a stable COM
            // pointer for the session, so caching it is safe; the pump only
            // passes it opaquely to plugins (RynthAi in Avalonia-only mode
            // never dereferences it). Atomic IntPtr write; volatile read.
            CachedDevice = pDevice;

            // NOTE (2026-05-16): PluginManager.InitPlugins / ProcessPendingActions
            // / FlushPendingDeletes / TickAll are NO LONGER called here. The
            // 2026-05-10 refactor put them on this EndScene reverse-P/Invoke
            // (AC's D3D9 render thread); a GC during the plugin tick on that
            // AC-owned thread fail-fasts in NativeAOT's
            // RhpReversePInvokeAttachOrTrapThread2 (proven from the
            // 2026-05-16 09:39 dump). They now run on the dedicated managed
            // plugin pump thread (EntryPoint.StartNormalPluginPump →
            // PumpPluginFrame), which mirrors the proven DecalCoexistence
            // pump. EXACTLY ONE TickAll driver: this render path no longer
            // calls it at all.

            // ── ImGui-gated work ─────────────────────────────────────────
            // The frame is BUILT here and SUBMITTED by RenderDeferredImGui, which
            // EndSceneHook calls after the Avalonia quad, so ImGui windows sit above
            // the Nav3D fallback, the vital HUD and the Avalonia panels. Input order
            // matches: Win32Backend gives ImGui the mouse while it wants it.
            bool imguiEnabled = Plugins.EngineSettings.EnableImGuiBackend;
            bool imguiBuilt = false;
            long uiTicks = 0;
            MainThreadHangWatchdog.Step = "EndScene: ImGui frame";
            if (!imguiEnabled)
            {
                _imguiIdle = true; // switched off in game: input state resets when it comes back
                UiCost.DiscardFrame();   // no UI frame: what the 3D pass timed isn't kept for one
            }
            else
            {
                long t0 = Stopwatch.GetTimestamp();
                imguiBuilt = BuildImGuiFrame(pDevice);
                uiTicks = Stopwatch.GetTimestamp() - t0;
            }

            // Fallback: the Nav3D markers and the world overlays built last frame
            // (nameplates, combat text), when neither the UI pass nor the transition
            // drew them this frame - still under the ImGui panels. No-op when they
            // already drew (one per-frame flag for every path; UnderUiLayer presents
            // a list once). _coreInitialized is enough for the markers.
            D3D9.Nav3DRenderInjector.PresentAtEndScene(pDevice);

            if (imguiEnabled)
            {
                long t0 = Stopwatch.GetTimestamp();
                // Popped-out panels: their own contexts, windows and render targets.
                if (_imguiInitialized)
                {
                    MainThreadHangWatchdog.Step = "EndScene: pop-out panels";
                    // UiCost: what RenderAll spends outside the pop-out windows it times itself.
                    long c0 = UiCost.Start(), mark = UiCost.Mark;
                    ImGuiPopOuts.RenderAll(pDevice, _uiScale, PanelsLive);
                    UiCost.StopExclusive(UiCost.PopOuts, c0, mark);
                }
                long ticks = uiTicks + (Stopwatch.GetTimestamp() - t0);
                if (imguiBuilt)
                {
                    _deferredUiTicks = ticks;
                    _deferredSubmitDevice = pDevice;
                }
                else
                {
                    UiFrameStats.Record(ticks);
                    UiCost.EndFrame(ticks);
                }
            }
        }
        catch (Exception ex)
        {
            // An exception here skips everything after it this frame (including RunImGuiFrame), so
            // the first one gets a stack trace; repeats are summarised every 30 s instead of per frame.
            _engineErrorCount++;
            long errNow = Environment.TickCount64;
            if (_engineErrorCount == 1)
                RynthLog.Error($"EngineFrameController: engine frame error (rest of this frame skipped): {ex}");
            else if (errNow >= _nextEngineErrorLogAt)
                RynthLog.Error($"EngineFrameController: engine frame errors so far={_engineErrorCount}, latest {ex.GetType().Name}: {ex.Message}");
            if (errNow >= _nextEngineErrorLogAt)
                _nextEngineErrorLogAt = errNow + HeartbeatIntervalMs;
        }
        finally
        {
            // Prime the injector for the next frame's DIP accumulation.
            // Without this, _markersRenderedThisFrame latches true after
            // the first 3D→UI transition in the session and the injector
            // never fires again — every subsequent frame falls back to the
            // end-of-frame draw (which paints over the UI).
            D3D9.Nav3DRenderInjector.ResetFrame();
            // The DIP hook only runs while the transition path is in use (no UI-pass
            // hook, or /rc worldlayer transition); switched here, between frames.
            try { D3D9.Nav3DRenderInjector.SyncHookState(); }
            catch (Exception ex) { RynthLog.D3D9($"Nav3DRenderInjector.SyncHookState threw {ex.GetType().Name}: {ex.Message}"); }
            // What this frame built for the world overlays is presented next frame.
            UnderUiLayer.EndFrame();
        }
    }

    /// <summary>
    /// The main-thread prefetches of the always-on pipeline above, for a client without
    /// the EndScene hook (Decal in the process): called from the Client::UseTime detour
    /// (AC's main thread) once GameTickHooks.HeadlessPrefetch is set. Same calls, same
    /// order, each throttled and main-thread-gated internally. The EndScene path doesn't
    /// use this (it runs the calls inline, unchanged).
    /// </summary>
    internal static void RunHeadlessPrefetch()
    {
        Compatibility.ClientObjectHooks.PrefetchPlayerSkills();
        Compatibility.ClientObjectHooks.PrefetchKnownSpells();
        Compatibility.ClientObjectHooks.PrefetchAttackable();
        Compatibility.ClientObjectHooks.PrefetchObjectIdentity();
        Compatibility.ClientObjectHooks.PrefetchPositions();
        Compatibility.ClientObjectHooks.PrefetchPlayerStats();
        Compatibility.ClientObjectHooks.ServeObjectQualitiesRequest();   // v84 GetObjectProperties (none asked: one read)
        try { Plugins.PluginManager.TrySeedLiveObjectsFromCObjectMaintOnce(); }
        catch (Exception ex) { RynthLog.Plugin($"CObjectMaint seed threw {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>
    /// The plugin lifecycle/tick work that used to run inline in
    /// <see cref="OnEndScene"/>. Driven by the dedicated managed plugin pump
    /// thread (EntryPoint.StartNormalPluginPump) — NOT AC's render thread —
    /// so a GC during the plugin tick can't fail-fast NativeAOT's reverse-
    /// P/Invoke transition on an AC-owned thread (the 2026-05-16 root cause).
    ///
    /// No-ops until the render path has resolved the game HWND and published
    /// a device (CachedDevice != 0). Single-threaded by contract: only the
    /// one pump thread calls this; the render path no longer touches
    /// PluginManager at all, so there is exactly one TickAll driver. The
    /// re-entrancy guard is belt-and-braces against an accidental second
    /// caller.
    /// </summary>
    public static void PumpPluginFrame()
    {
        IntPtr device = CachedDevice;
        if (device == IntPtr.Zero || _gameHwnd == IntPtr.Zero)
            return; // render path hasn't established core state yet

        if (System.Threading.Interlocked.Exchange(ref _pluginPumpInFrame, 1) != 0)
            return; // never re-enter PluginManager from two stacks
        try
        {
            // Plugins only get the ImGui context when they may draw with it: the
            // ImGui shell (EnableImGuiShell, FORCE-gated) or their own overlay
            // windows (EnablePluginOverlayWindows: RynthAi's Mini Remote, HUDs, Item
            // Info; RynthPluginRenderOverlay). RynthAi only reads the context for
            // those windows, so this does not switch it into a legacy-ImGui setup.
            bool pluginsDraw = Plugins.EngineSettings.EnableImGuiShell
                || (Plugins.EngineSettings.EnablePluginOverlayWindows && Plugins.EngineSettings.EnableImGuiBackend);
            // _context is written by the render thread (Init); this is the pump thread.
            IntPtr pluginContext = pluginsDraw ? System.Threading.Volatile.Read(ref _context) : IntPtr.Zero;
            if (!_pluginsInitialized)
            {
                // The plugin host copies the context once at init, so wait for the
                // render path's ImGui init (first ImGui frame) rather than hand over
                // zero for the whole session. Bounded: a failed or stalled ImGui init
                // must not hold plugins (and the bot) back.
                if (pluginsDraw && pluginContext == IntPtr.Zero && !_imguiInitFailed
                    && ++_pluginInitContextWaits <= PluginInitContextMaxWaits)
                    return;
                if (pluginsDraw && pluginContext == IntPtr.Zero)
                    RynthLog.Warn($"EngineFrameController: plugins initialised without an ImGui context (imguiInitFailed={_imguiInitFailed}, waits={_pluginInitContextWaits}) - plugin overlay windows will not draw this session.");
                _pluginsInitialized = true;
                PluginManager.InitPlugins(pluginContext, device, _gameHwnd);
            }

            // Decal bridge mode with the in-game renderer (DecalInGameImGui): chat and
            // chat-bar lines come from the bridge. One flag check in every other client.
            if (Compatibility.DecalBridgeHost.Active)
                Compatibility.DecalBridgeHost.Drain();
            PluginManager.ProcessPendingActions(pluginContext, device, _gameHwnd);
            // Client::Cleanup started (UiFlowHooks): the call above told the plugins; from
            // here on no plugin tick and no plugin export for the panels' data.
            if (Compatibility.UiFlowHooks.ClientCleanupStarted)
                return;
            PluginManager.FlushPendingDeletes(); // closes the create→delete race window
            PluginManager.TickAll();
            // Panel data: plugin exports for the UI are called here, on the same
            // thread as the plugin tick, and published as immutable snapshots.
            UI.Data.UiDataHub.Step();
        }
        catch (Exception ex)
        {
            // The first one with its stack, then counted (a step throwing every frame
            // would otherwise write a line per pump tick).
            int n = ++_pumpFrameFaults;
            if (n == 1)
                RynthLog.Plugin($"EngineFrameController.PumpPluginFrame: {ex.GetType().Name}: {ex.Message} (caught; repeats are counted){Environment.NewLine}{ex}");
            else if (n % StepFaultLog.RepeatEvery == 0)
                RynthLog.Plugin($"EngineFrameController.PumpPluginFrame: {ex.GetType().Name}: {ex.Message} - {n} times so far, still caught.");
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _pluginPumpInFrame, 0);
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the calling thread while no plugin pump frame runs,
    /// holding the pump's frame guard so it can't start one meanwhile. False (not run) when a
    /// frame is running. For Client::Cleanup (UiFlowHooks): AC's main thread tells the
    /// plugins itself when the pump is idle or no longer running frames.
    /// </summary>
    internal static bool TryRunOutsidePumpFrame(Action action)
    {
        if (System.Threading.Interlocked.CompareExchange(ref _pluginPumpInFrame, 1, 0) != 0)
            return false;
        try { action(); }
        finally { System.Threading.Interlocked.Exchange(ref _pluginPumpInFrame, 0); }
        return true;
    }

    /// <summary>True when anything ImGui should be drawn this frame.</summary>
    private static bool HasImGuiContent()
    {
        if (Plugins.EngineSettings.EnableImGuiShell || ImGuiFontTest.Enabled) return true;
        if (!PanelsLive) return false;
        ImGuiPanelHost.RestoreOnce();
        return ImGuiPanelHost.NeedsFrame || ImGuiBar.Visible || ImGuiBar.PoppedOut || Hud.MonsterHud.WantsFrame
            || D3D9.VideoPatch.WantsBanner || PluginOverlayWindowsLive;
    }

    /// <summary>
    /// In the world, with the shell off, and some loaded plugin exports
    /// RynthPluginRenderOverlay: its windows need a frame every frame.
    /// </summary>
    private static bool PluginOverlayWindowsLive =>
        PanelsLive && Plugins.EngineSettings.EnablePluginOverlayWindows && PluginManager.HasOverlayPlugins;

    /// <summary>
    /// Panel faces show only in the world, like the Avalonia overlay (hidden
    /// between characters, not closed). Also off as soon as AC leaves the world screen
    /// (UiFlowHooks, before the logout dispatch catches up) and for good once
    /// Client::Cleanup starts. And after a login (or a hot reload) not before that
    /// character's panel layout is in place (UI/PanelLayout), so nothing draws at the
    /// default places first.
    /// </summary>
    private static bool PanelsLive => Compatibility.LoginLifecycleHooks.HasObservedLoginComplete
        && Compatibility.UiFlowHooks.InWorldOrUnknown
        && !Compatibility.UiFlowHooks.ClientCleanupStarted
        && UI.PanelLayout.ReadyToDraw;

    // The ImGui windows' saved text, read from and loaded into the main context for UI/PanelLayout
    // (render thread, between frames). Cached so the per-frame Tick allocates nothing.
    private static readonly Func<string?> SnapshotIniFn = SnapshotIni;
    private static readonly Action<string> LoadIniFn = LoadIni;

    private static string? SnapshotIni()
    {
        if (!_imguiInitialized || _context == IntPtr.Zero) return null;
        IntPtr previous = ImGuiNET.ImGui.GetCurrentContext();
        ImGuiNET.ImGui.SetCurrentContext(_context);
        try { return ImGuiNET.ImGui.SaveIniSettingsToMemory(); }
        finally { ImGuiNET.ImGui.SetCurrentContext(previous); }
    }

    /// <summary>Loads ini text into the main context: Dear ImGui applies it to windows that already exist too.</summary>
    private static void LoadIni(string text)
    {
        if (_context == IntPtr.Zero) return;
        IntPtr previous = ImGuiNET.ImGui.GetCurrentContext();
        ImGuiNET.ImGui.SetCurrentContext(_context);
        try { ImGuiNET.ImGui.LoadIniSettingsFromMemory(text); }
        finally { ImGuiNET.ImGui.SetCurrentContext(previous); }
    }

    /// <summary>DPI scale the fonts and style were built for (see Init).</summary>
    private static float _uiScale = 1f;
    private static int _themeApplied;   // RynthTheme.Version the base style was coloured at

    /// <summary>The scale the fonts and style were built for.</summary>
    internal static float FontScale => _uiScale;

    /// <summary>A popped-out panel was clicked: the main context's text edit ends next frame. AC thread.</summary>
    internal static void DropMainTextFocus() => _dropImGuiTextFocus = true;

    // ── Diagnostics (/rc imgui diag) ─────────────────────────────────
    private static long _framesBuilt;
    private static System.Numerics.Vector2 _lastDisplaySize;

    /// <summary>One line per fact about the ImGui layer's state. Game thread (reads plain fields).</summary>
    internal static System.Collections.Generic.List<string> DescribeState()
    {
        var lines = new System.Collections.Generic.List<string>
        {
            $"layer={(Plugins.EngineSettings.EnableImGuiBackend ? "on" : "off")} initialized={_imguiInitialized} initFailed={_imguiInitFailed} selfTest={ImGuiSelfTest.LastResult}",
            $"inWorld={PanelsLive} content={HasImGuiContentNoRestore()} idle={_imguiIdle} framesBuilt={_framesBuilt} uiScale={_uiScale:0.##} avaloniaScale={UI.AvaloniaOverlay.InputScale:0.##} display={_lastDisplaySize.X:0}x{_lastDisplaySize.Y:0}",
            DX9Backend.DescribeLastFrame(),
            UnderUiLayer.Describe(),
            D3D9.Nav3DRenderInjector.Describe(),
            $"capture: {Win32Backend.DescribeCapture()}",
        };
        lines.AddRange(ImGuiPopOuts.Describe());
        return lines;
    }

    private static bool HasImGuiContentNoRestore() =>
        Plugins.EngineSettings.EnableImGuiShell || ImGuiFontTest.Enabled || (PanelsLive && (ImGuiPanelHost.NeedsFrame || ImGuiBar.Visible || ImGuiBar.PoppedOut || Hud.MonsterHud.WantsFrame || D3D9.VideoPatch.WantsBanner))
        || PluginOverlayWindowsLive;

    // Keyboard owner between the stacks (plan §3.2 d): whichever side started
    // text entry most recently keeps the keyboard.
    private static bool _prevImGuiText, _prevAvaloniaText, _dropImGuiTextFocus;

    /// <summary>True while frames are being skipped because nothing ImGui is open.</summary>
    private static bool _imguiIdle = true;

    /// <summary>
    /// Builds the ImGui frame (NewFrame ... Render) and publishes the capture
    /// flags. Returns true when there is draw data for <see cref="SubmitImGuiFrame"/>.
    /// Only called while EngineSettings.EnableImGuiBackend is true.
    ///
    /// Early-out: with no ImGui content there is no ImGui frame at all. Queued
    /// input is dropped (nothing would drain it) and capture flags are cleared,
    /// so the idle cost is one GetClientRect.
    /// </summary>
    private static bool BuildImGuiFrame(IntPtr pDevice)
    {
        if (_imguiInitFailed) return false;

        if (!_imguiInitialized)
        {
            // Initialised as soon as the backend is enabled (not lazily on first
            // content), so the cimgui self-test result is in the log at startup.
            InitStep step = Init(pDevice);
            if (step == InitStep.Failed)
            {
                _imguiInitFailed = true;
                RynthLog.Error($"EngineFrameController: ImGui init FAILED at step '{_initFailStep}' - ImGui frames disabled for this session.");
                return false;
            }
            if (step == InitStep.Baking)
                return false;   // the first font atlas is still baking on its worker: no ImGui frame yet
        }

        Win32Backend.UpdateClientMetrics(out int clientW, out int clientH);

        // Monster nameplates: pick the monsters (throttled) before deciding
        // whether there is a frame to build, so plates alone can ask for one.
        long cHud = UiCost.Start();
        Hud.MonsterHud.Update(PanelsLive);
        UiCost.Stop(UiCost.HudUpdate, cHud);

        if (!HasImGuiContent())
        {
            _imguiIdle = true;
            Win32Backend.DiscardQueuedInput();
            Win32Backend.ClearCaptureFlags();
            ImGuiTextFocus.ClearMain();
            return false;
        }

        IntPtr previousContext = ImGuiNET.ImGui.GetCurrentContext();
        ImGuiNET.ImGui.SetCurrentContext(_context);
        bool frameStarted = false;
        bool frameEnded = false;
        try
        {
            ImGuiIOPtr io = ImGuiNET.ImGui.GetIO();
            long now = Stopwatch.GetTimestamp();
            if (_imguiIdle)
            {
                // Resuming: keys/buttons released while idle must not read as held,
                // and the idle gap must not become one huge DeltaTime.
                _imguiIdle = false;
                Win32Backend.ResetImGuiInputState(io);
                _lastFrameTicks = now;
            }

            _frameCount++;
            // UiCost (ImGui/UiCost.cs): each part of the frame below is timed, by name.
            long cSetup = UiCost.Start();
            // A panel's text size changed: re-bake the fonts (only the sizes in
            // use) and replace the font texture, between frames.
            // The new atlas is baked on a worker (ImGuiFonts.PumpRebuild), once per run of
            // changes (RequestRebuild debounces); here it is swapped in and its texture made.
            DX9Backend.ReleaseRetiredFontTexture();   // the texture a rebuild replaced last frame
            long rebuildStart = Stopwatch.GetTimestamp();
            if (ImGuiFonts.PumpRebuild(io, _uiScale, out string rebuilt))
            {
                DX9Backend.RebuildFontTexture(pDevice);
                double rebuildMs = (Stopwatch.GetTimestamp() - rebuildStart) * 1000.0 / Stopwatch.Frequency;
                RynthLog.UI($"ImGuiFonts: text size change: {rebuilt}; render thread {rebuildMs:0.0} ms (swap + texture).");
            }
            // Script window icons decoded since the last frame become textures, and the least
            // recently drawn go, before anything draws (no draw list points at them any more).
            ScriptIcons.BeginFrame(pDevice);
            WorldMapTiles.BeginFrame(pDevice);   // world map tiles read since the last frame become textures
            RetailSprites.BeginFrame(pDevice);   // retail UI pictures decoded since the last frame (kept, never evicted)
            DX9Backend.NewFrame();
            Win32Backend.NewFrame(clientW, clientH);

            // Fallback: if Win32Backend could not determine display size, read from the D3D9 viewport.
            if (io.DisplaySize.X <= 1 || io.DisplaySize.Y <= 1)
            {
                DX9Backend.GetViewportSize(pDevice, out int vpW, out int vpH);
                if (vpW > 1 && vpH > 1)
                    io.DisplaySize = new System.Numerics.Vector2(vpW, vpH);
            }

            float dt = (float)(now - _lastFrameTicks) / Stopwatch.Frequency;
            _lastFrameTicks = now;
            io.DeltaTime = dt > 0f ? dt : 1f / 60f;

            RynthTheme.RefreshStyle(ref _themeApplied);   // a theme change re-colours the base style
            UiCost.Stop(UiCost.FrameSetup, cSetup);
            long cNew = UiCost.Start();
            ImGuiNET.ImGui.NewFrame();
            UiCost.Stop(UiCost.ImGuiNewFrame, cNew);
            frameStarted = true;
            // Before any window: a drag from a popped-out Inventory or AC's inventory becomes
            // a payload the client's drop targets (Mini Remote slots) see this frame.
            ItemDragBridge.SubmitMainFrame(io);
            ImGuiTextFocus.BeginFrame();
            if (_dropImGuiTextFocus)
            {
                // An Avalonia TextBox took the keyboard: end ImGui's text edit.
                _dropImGuiTextFocus = false;
                ImGuiNET.ImGui.SetWindowFocus(null);
            }
            // EnableImGuiShell gates the old diagnostic shell and plugin-drawn
            // ImGui windows (plugin code on AC's thread: FORCE-gated, stays off).
            int branch;
            if (Plugins.EngineSettings.EnableImGuiShell)
            {
                branch = 1;
                long cShell = UiCost.Start();
                RynthCoreShell.Render(_frameCount);
                UiCost.Stop(UiCost.Shell, cShell);
                PluginManager.RenderAll();   // times each plugin's Render (UiCost)
            }
            else if (PluginOverlayWindowsLive)
            {
                branch = 2;
                // Only the opt-in extra windows (RynthPluginRenderOverlay), never the
                // plugins' full ImGui UIs.
                PluginManager.RenderOverlayAll();   // times each plugin's RenderOverlay (UiCost)
            }
            else
            {
                branch = 0;
            }
            if (branch != _loggedBranch)
            {
                _loggedBranch = branch;
                RynthLog.Info($"EngineFrameController: ImGui plugin branch = {(branch == 1 ? "shell (RenderAll)" : branch == 2 ? "overlay windows (RenderOverlayAll)" : "none")}.");
            }
            if (PanelsLive)
            {
                // World overlays: built into UnderUiLayer's list, drawn next frame at
                // AC's 3D->UI transition (under AC's UI and every panel), no input.
                // Video patch: with no 3D scene only the screen-fixed player plate draws.
                // Timed from here, as one part (plates, the player plate, combat text): the HUD's own
                // code isn't instrumented.
                long cPlates = UiCost.Start();
                Hud.MonsterHud.Draw(_uiScale, worldVisible: !D3D9.VideoPatch.SkipWorldThisFrame);
                UiCost.Stop(UiCost.MonsterHud, cPlates, cPlates != 0 ? Hud.MonsterHud.PlateCount : -1);
                // Video patch "all": the "rendering paused" banner (background list, under the panels).
                long cPart = UiCost.Start();
                VideoPatchBanner.Draw();
                UiCost.Stop(UiCost.VideoBanner, cPart);
                // Server verdicts, the plugins' section manifests and the servermenu push. Every
                // frame, bar or not: with the bar hidden (the rail instead) nothing else ticks them,
                // so Options > Diagnostics listed no servers and no server plugin was told it is active.
                cPart = UiCost.Start();
                RynthCore.Engine.UI.ServerMenus.Tick();
                UiCost.Stop(UiCost.ServerMenus, cPart);
                cPart = UiCost.Start();
                if (ImGuiBar.Visible)
                    ImGuiBar.Draw(_uiScale);
                UiCost.Stop(UiCost.Bar, cPart);
                ImGuiPanelHost.DrawAll(_uiScale);   // times each panel by title, and its own upkeep (UiCost)
                // Settings drawer bodies RynthAi's window didn't draw this frame let their sources go.
                cPart = UiCost.Start();
                Panels.SettingsDrawerBodies.EndFrame();
                UiCost.Stop(UiCost.DrawerBodies, cPart);
                cPart = UiCost.Start();
                ImGuiBar.SyncPopOut(_uiScale);
                UiCost.Stop(UiCost.Bar, cPart);
            }
            if (ImGuiFontTest.Enabled)
                ImGuiFontTest.Draw(_uiScale);

            long cRender = UiCost.Start();
            ImGuiNET.ImGui.EndFrame();
            frameEnded = true;
            ImGuiNET.ImGui.Render();
            UiCost.Stop(UiCost.ImGuiRender, cRender);
            long cFinish = UiCost.Start();
            ImGuiAssertLog.Poll();
            _framesBuilt++;
            _lastDisplaySize = io.DisplaySize;
            // Windows moved a while ago (Dear ImGui's dirty timer; no IniFilename, so it asks):
            // saved to the panel layout's imgui.ini in the background (UI/PanelLayout).
            if (io.WantSaveIniSettings)
            {
                io.WantSaveIniSettings = false;
                UI.PanelLayout.QueueIniSave(ImGuiNET.ImGui.SaveIniSettingsToMemory(), explicitSave: false);
            }

            // io.WantCaptureMouse already applies ImGui's own button-ownership
            // rule; io.WantTextInput is true only while a text field is edited.
            // MonsterHud.WantsMouse: the cursor is on a clickable nameplate (or a
            // press that began on one is held), so that click is the plate's.
            // Keyboard: io.WantTextInput lags the box by a frame (ImGui publishes it from
            // the previous frame's widgets), so a box that went active this frame counts
            // too (ImGuiTextFocus): the keys typed right after the click stay out of AC.
            ImGuiTextFocus.EndMainFrame(io.WantTextInput);
            bool typing = io.WantTextInput || ImGuiTextFocus.MainBoxActive;
            // A plate that is only hovered lets the mouse moves through, so AC's own
            // mouse-over stays current (MonsterHud.MouseMovesToGame, Compatibility/AcMouseOver).
            // A docked click-through panel under the cursor that zooms with the wheel (the radar's
            // "Mouse wheel still zooms"): the wheel is its own, clicks and moves still the game's.
            Win32Backend.UpdateCaptureFlags(io.WantCaptureMouse || Hud.MonsterHud.WantsMouse, typing,
                mouseMovesToGame: !io.WantCaptureMouse && Hud.MonsterHud.MouseMovesToGame,
                wheelToUi: !io.WantCaptureMouse && PanelsLive && ImGuiPanelHost.WheelWanted);
            Compatibility.AcMouseOver.Tick();   // /rc acmouse watch, when on
            // The UI is released (Insert): no text box keeps the keyboard. Keys are not
            // gated by the toggle any more (Win32Backend.UpdateCaptureFlags), so the edit
            // ends instead and the next keys go to the game.
            if (typing && !Win32Backend.UiCaptureEnabled)
                _dropImGuiTextFocus = true;
            ArbitrateTextFocus(io.WantTextInput);
            UiCost.Stop(UiCost.FrameFinish, cFinish);
            return true;
        }
        catch (Exception ex)
        {
            try
            {
                // Keep Dear ImGui's frame state balanced so the next frame does not assert.
                if (frameStarted && !frameEnded && ImGuiNET.ImGui.GetCurrentContext() == _context)
                    ImGuiNET.ImGui.EndFrame();
            }
            catch
            {
            }

            Win32Backend.ClearCaptureFlags();
            ImGuiTextFocus.ClearMain();
            RynthLog.Info($"EngineFrameController: frame {_frameCount} ImGui error: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            ImGuiNET.ImGui.SetCurrentContext(previousContext);
        }
    }

    private static void ArbitrateTextFocus(bool imguiText)
    {
        bool avaloniaText = Win32Backend.AvaloniaTextInputActive;
        if (imguiText && !_prevImGuiText && avaloniaText)
        {
            // An ImGui field just started typing: Avalonia gives up the keyboard.
            Win32Backend.AvaloniaTextInputActive = false;
            UI.AvaloniaOverlay.ClearTextFocus();
            avaloniaText = false;
        }
        else if (avaloniaText && !_prevAvaloniaText && imguiText)
        {
            _dropImGuiTextFocus = true;
        }
        _prevImGuiText = imguiText;
        _prevAvaloniaText = avaloniaText;
    }

    /// <summary>
    /// Submits this frame's ImGui draw data. EndSceneHook calls it after
    /// OverlayTextureRenderer.Render so ImGui windows draw on top of the Avalonia layer.
    /// Runs whether or not the Avalonia blit ran (character select, Avalonia off), and is
    /// a no-op when <see cref="OnEndScene"/> built nothing this frame. Never throws.
    /// </summary>
    public static void RenderDeferredImGui()
    {
        IntPtr device = _deferredSubmitDevice;
        if (device == IntPtr.Zero)
            return;
        _deferredSubmitDevice = IntPtr.Zero; // one submission per frame, even if it throws

        long t0 = Stopwatch.GetTimestamp();
        SubmitImGuiFrame(device);
        long ticks = _deferredUiTicks + (Stopwatch.GetTimestamp() - t0);
        UiFrameStats.Record(ticks);
        UiCost.EndFrame(ticks);
    }

    /// <summary>Draws the frame <see cref="BuildImGuiFrame"/> produced.</summary>
    private static void SubmitImGuiFrame(IntPtr pDevice)
    {
        IntPtr previousContext = ImGuiNET.ImGui.GetCurrentContext();
        ImGuiNET.ImGui.SetCurrentContext(_context);
        try
        {
            ImDrawDataPtr drawData = ImGuiNET.ImGui.GetDrawData();
            long c0 = UiCost.Start();
            DX9Backend.RenderDrawData(drawData, pDevice);
            if (c0 != 0)
            {
                int vertices = 0;
                // ImDrawData +12 TotalVtxCount (as LogHeartbeatIfDue).
                unsafe { byte* p = (byte*)drawData.NativePtr; if (p != null) vertices = *(int*)(p + 12); }
                UiCost.Stop(UiCost.Submit, c0, vertices);
                UiCost.SetSubmit(DX9Backend.LastSubmit);   // draws, texture switches, vertices copied
            }
            _drawSubmits++;
            if (!_loggedFirstSubmit)
            {
                _loggedFirstSubmit = true;
                RynthLog.Info("EngineFrameController: first ImGui draw submitted.");
            }
            LogHeartbeatIfDue(drawData);
        }
        catch (Exception ex)
        {
            RynthLog.Info($"EngineFrameController: frame {_frameCount} ImGui submit error: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            ImGuiNET.ImGui.SetCurrentContext(previousContext);
        }
    }

    /// <summary>
    /// Every 30 s: built ImGui frames, submits, draw list / vertex counts of the
    /// latest frame and the overlay-window stats from PluginManager. Zero draw lists means no
    /// ImGui window was drawn that frame. One string per interval; no per-frame allocation.
    /// </summary>
    private static void LogHeartbeatIfDue(ImDrawDataPtr drawData)
    {
        long now = Environment.TickCount64;
        if (now < _nextHeartbeatAt) return;
        bool first = _nextHeartbeatAt == 0;
        _nextHeartbeatAt = now + HeartbeatIntervalMs;
        if (first) return; // first interval starts now; the first-frame line already covers t=0

        int lists = 0, vertices = 0;
        unsafe
        {
            // ImDrawData: +4 CmdListsCount, +8 TotalIdxCount, +12 TotalVtxCount (see CLAUDE.md offsets).
            byte* p = (byte*)drawData.NativePtr;
            if (p != null) { lists = *(int*)(p + 4); vertices = *(int*)(p + 12); }
        }
        RynthLog.Info($"EngineFrameController: ImGui heartbeat frames={_framesBuilt} submits={_drawSubmits} lastFrame drawLists={lists} vertices={vertices} {PluginManager.DescribeOverlayState()}.");
    }

    internal static IntPtr FindGameWindow()
    {
        uint pid = GetCurrentProcessId();
        IntPtr found = IntPtr.Zero;

        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out uint windowPid);
            if (windowPid == pid && IsWindowVisible(hWnd))
            {
                found = hWnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }
}
