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
//      mouse/keyboard capture flag updates, multi-viewport platform pump.
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

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out ClientRect lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref ClientPoint lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct ClientRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ClientPoint { public int X, Y; }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private static bool _imguiInitialized;
    private static bool _imguiInitFailed;
    private static bool _coreResolved;
    private static bool _pluginsInitialized;
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
        return true;
    }

    public static bool Init(IntPtr pDevice)
    {
        if (_imguiInitialized)
            return true;

        if (!EnsureCore(pDevice))
            return false;

        IntPtr previousContext = ImGuiNET.ImGui.GetCurrentContext();
        if (previousContext != IntPtr.Zero)
            RynthLog.Render($"EngineFrameController: Existing ImGui context detected (0x{previousContext:X8}) - isolating RynthCore context.");

        _context = ImGuiNET.ImGui.CreateContext();
        bool initSucceeded = false;
        try
        {
            ImGuiNET.ImGui.SetCurrentContext(_context);
            ImGuiIOPtr io = ImGuiNET.ImGui.GetIO();

            // Pin imgui.ini to a stable absolute path so window positions/sizes
            // persist across plugin reloads (RL) and AC restarts. The default
            // ImGui behavior writes to the working directory which can differ
            // between launches. Allocated once, kept alive for the process lifetime.
            try
            {
                const string iniPath = @"C:\Games\RynthSuite\RynthAi\imgui.ini";
                System.IO.Directory.CreateDirectory(@"C:\Games\RynthSuite\RynthAi");
                IntPtr iniPtr = System.Runtime.InteropServices.Marshal.StringToHGlobalAnsi(iniPath);
                unsafe { io.NativePtr->IniFilename = (byte*)iniPtr; }
                RynthLog.Render($"EngineFrameController: imgui.ini pinned to {iniPath}");
            }
            catch (Exception ex)
            {
                RynthLog.Render($"EngineFrameController: failed to pin imgui.ini path - {ex.Message}");
            }

            io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
            // ViewportsEnable was enabling ImGui multi-viewport platform/renderer
            // callbacks against a cimgui.dll that is NOT built with
            // IMGUI_ENABLE_VIEWPORTS. The platform/renderer callback chain then
            // recursed (Platform → Renderer → Platform → ...) on AC's render
            // thread, eating the 1MB stack in ~17s and AV'ing in ntdll mid-push.
            // CLAUDE.md flags this: Multi-viewport "requires cimgui built with
            // IMGUI_ENABLE_VIEWPORTS" — flag re-introduced this in the engine
            // and triggered the silent stack-overflow on every login.
            // io.ConfigFlags |= ImGuiConfigFlags.ViewportsEnable;
            // Popped-out plugin windows use WS_EX_TOOLWINDOW so they don't show
            // up as extra "Asheron's Call" entries in the taskbar — critical for
            // users running many clients side by side.
            io.ConfigViewportsNoTaskBarIcon = true;
            ImGuiNET.ImGui.StyleColorsDark();

            // One-shot offset probe for diagnostics — safe to keep installed.
            ViewportProbe.Run();

            if (!Win32Backend.Init(_gameHwnd))
                return false;

            if (!DX9Backend.InitImGui(pDevice))
                return false;

            // Viewport backends only run when ViewportsEnable is on. Installing
            // their callbacks on PlatformIO without the flag has no upside and
            // just expands the crash surface.
            if ((io.ConfigFlags & ImGuiConfigFlags.ViewportsEnable) != 0)
            {
                bool platformOk = ViewportPlatformBackend.Init(_gameHwnd);
                bool rendererOk = platformOk && ViewportRendererBackend.Init(pDevice);
                if (!platformOk || !rendererOk)
                {
                    RynthLog.Render($"EngineFrameController: viewport init failed (platform={platformOk}, renderer={rendererOk}) — disabling ViewportsEnable.");
                    io.ConfigFlags &= ~ImGuiConfigFlags.ViewportsEnable;
                }
                ViewportPlatformBackend.ImGuiContext = _context;
            }

            _lastFrameTicks = Stopwatch.GetTimestamp();
            _imguiInitialized = true;
            initSucceeded = true;

            RynthLog.Render("EngineFrameController: Fully initialized - RynthCore shell active.");
            return true;
        }
        finally
        {
            if (!initSucceeded && _context != IntPtr.Zero)
            {
                ImGuiNET.ImGui.DestroyContext(_context);
                _context = IntPtr.Zero;
            }

            ImGuiNET.ImGui.SetCurrentContext(previousContext);
        }
    }

    public static void Shutdown()
    {
        if (!_imguiInitialized)
            return;

        IntPtr previousContext = ImGuiNET.ImGui.GetCurrentContext();
        IntPtr contextToDestroy = _context;

        try
        {
            ImGuiNET.ImGui.SetCurrentContext(contextToDestroy);

            // Zero the WndProc context guard BEFORE DestroyContext so any
            // deferred OS messages that arrive at viewport HWNDs during or
            // after teardown don't try to call into freed ImGui memory.
            ViewportPlatformBackend.ImGuiContext = IntPtr.Zero;
            PluginManager.ShutdownAll();
            OverlayTextureRenderer.Shutdown();
            ViewportRendererBackend.Shutdown();
            ViewportPlatformBackend.Shutdown();
            DX9Backend.Shutdown();
            Win32Backend.Shutdown();
            ImGuiNET.ImGui.DestroyContext(contextToDestroy);

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
        try
        {
            // Resolve game HWND and seed EntryPoint.GameHwnd. Required even
            // in ImGui-off mode so Avalonia owner-window binding works.
            EnsureCore(pDevice);

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
            D3D9.Nav3DRenderInjector.Install(pDevice);

            // Capture the game's View/Projection matrices before ImGui touches them
            GameMatrixCapture.CaptureFrame(pDevice);

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

            // Nav3DRenderInjector fires mid-frame at the 3D→UI ZENABLE
            // transition — that's the only pipeline position where markers
            // can render BEHIND AC's UI (the UI pass draws after ZENABLE
            // turns off, and the end-of-frame fallback is past it entirely).
            //
            // The injector accumulates state per-frame via DrawIndexedPrimitive
            // callbacks during AC's draws (which fire before this EndScene),
            // so by the time we check RenderedThisFrame here, its decision
            // for the current frame is final. ResetFrame at the END of
            // OnEndScene primes it for the next frame's accumulation.
            //
            // Historically the engine forced the end-of-frame fallback ONLY
            // because the injector's hit-rate varied across motion, producing
            // flicker when some frames painted at the 3D→UI boundary and
            // others at end-of-frame. We accept that risk here: the user
            // wants markers behind the UI, and a frame the injector misses
            // simply doesn't render markers (less jarring than a Z-order flip).
            bool nav3DAlreadyRendered = D3D9.Nav3DRenderInjector.RenderedThisFrame;

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
            if (Plugins.EngineSettings.EnableImGuiBackend)
                RunImGuiFrame(pDevice);

            // Fallback render — only fires when the injector missed this
            // frame's 3D→UI transition. _coreInitialized is enough — this
            // does not touch font / vertex buffer state.
            if (!nav3DAlreadyRendered)
                DX9Backend.RenderNav3D(pDevice);
        }
        catch (Exception ex)
        {
            RynthLog.Info($"EngineFrameController: frame {_frameCount} engine error: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // Prime the injector for the next frame's DIP accumulation.
            // Without this, _markersRenderedThisFrame latches true after
            // the first 3D→UI transition in the session and the injector
            // never fires again — every subsequent frame falls back to the
            // end-of-frame draw (which paints over the UI).
            D3D9.Nav3DRenderInjector.ResetFrame();
        }
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
            if (!_pluginsInitialized)
            {
                _pluginsInitialized = true;
                PluginManager.InitPlugins(_context, device, _gameHwnd);
            }

            PluginManager.ProcessPendingActions(_context, device, _gameHwnd);
            PluginManager.FlushPendingDeletes(); // closes the create→delete race window
            PluginManager.TickAll();
        }
        catch (Exception ex)
        {
            RynthLog.Plugin($"EngineFrameController.PumpPluginFrame: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _pluginPumpInFrame, 0);
        }
    }

    /// <summary>
    /// Runs the ImGui-specific portion of the per-frame pipeline:
    /// backend NewFrame plumbing, ImGui frame open/close, RynthCore shell +
    /// plugin RenderAll, draw-data submission, capture flag update, and the
    /// multi-viewport platform pump. Bypassed entirely when
    /// EngineSettings.EnableImGuiBackend is false.
    /// </summary>
    private static void RunImGuiFrame(IntPtr pDevice)
    {
        if (_imguiInitFailed) return;

        if (!_imguiInitialized)
        {
            if (!Init(pDevice))
            {
                _imguiInitFailed = true;
                return;
            }
        }

        IntPtr previousContext = ImGuiNET.ImGui.GetCurrentContext();
        ImGuiNET.ImGui.SetCurrentContext(_context);
        bool frameStarted = false;
        bool frameEnded = false;

        try
        {
            _frameCount++;
            DX9Backend.NewFrame();
            Win32Backend.NewFrame();

            ImGuiIOPtr io = ImGuiNET.ImGui.GetIO();

            // Fallback: if Win32Backend could not determine display size, read from the D3D9 viewport.
            if (io.DisplaySize.X <= 1 || io.DisplaySize.Y <= 1)
            {
                DX9Backend.GetViewportSize(pDevice, out int vpW, out int vpH);
                if (vpW > 1 && vpH > 1)
                    io.DisplaySize = new System.Numerics.Vector2(vpW, vpH);
            }

            long now = Stopwatch.GetTimestamp();
            float dt = (float)(now - _lastFrameTicks) / Stopwatch.Frequency;
            _lastFrameTicks = now;
            io.DeltaTime = dt > 0f ? dt : 1f / 60f;

            // With ViewportsEnable on, ImGui routes mouse and window positions in
            // absolute screen coords. The main viewport's Pos must equal the game
            // client's screen origin so clicks over the main viewport map to the
            // correct windows, and popped-out viewports share a coord system with
            // the main one.
            if ((io.ConfigFlags & ImGuiConfigFlags.ViewportsEnable) != 0)
            {
                int screenX = 0, screenY = 0;
                if (_gameHwnd != IntPtr.Zero)
                {
                    ClientPoint p = new ClientPoint { X = 0, Y = 0 };
                    if (ClientToScreen(_gameHwnd, ref p)) { screenX = p.X; screenY = p.Y; }
                }
                ImGuiViewportPtr mainVp = ImGuiNET.ImGui.GetMainViewport();
                mainVp.Pos = new System.Numerics.Vector2(screenX, screenY);
                mainVp.Size = io.DisplaySize;
                mainVp.WorkPos = new System.Numerics.Vector2(screenX, screenY);
                mainVp.WorkSize = io.DisplaySize;
            }

            ImGuiNET.ImGui.NewFrame();
            frameStarted = true;
            // EnableImGuiShell gates the in-AC ImGui surface as a whole: both the
            // RynthCore overlay bar and any plugin-drawn ImGui windows. Plugins
            // still load, init, and tick when this is off — they just don't
            // draw, so Avalonia panels can drive them via the plugin's C exports.
            if (Plugins.EngineSettings.EnableImGuiShell)
            {
                RynthCoreShell.Render(_frameCount);
                PluginManager.RenderAll();
            }

            bool captureMouse =
                ImGuiNET.ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow) ||
                ImGuiNET.ImGui.IsAnyItemActive();
            bool captureKeyboard = ImGuiNET.ImGui.IsAnyItemActive();

            ImGuiNET.ImGui.EndFrame();
            frameEnded = true;
            ImGuiNET.ImGui.Render();
            Win32Backend.UpdateCaptureFlags(captureMouse, captureKeyboard);

            ImDrawDataPtr drawData = ImGuiNET.ImGui.GetDrawData();

            DX9Backend.RenderDrawData(drawData, pDevice);
            // OverlayTextureRenderer.Render is now driven from EndSceneHook.EndSceneDetour
            // directly so the Avalonia compositor runs even when EnableImGuiBackend=false.
            // Leaving it out of this call site avoids double-blit when ImGui is also active.

            if ((io.ConfigFlags & ImGuiConfigFlags.ViewportsEnable) != 0)
            {
                ImGuiNET.ImGui.UpdatePlatformWindows();
                ImGuiNET.ImGui.RenderPlatformWindowsDefault();
            }
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

            RynthLog.Info($"EngineFrameController: frame {_frameCount} ImGui error: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            ImGuiNET.ImGui.SetCurrentContext(previousContext);
        }
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
