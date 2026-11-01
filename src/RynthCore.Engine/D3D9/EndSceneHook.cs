using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.Hooking;
using RynthCore.Engine.ImGuiBackend;

namespace RynthCore.Engine.D3D9;

internal static class EndSceneHook
{
    private const int MaxOffscreenSkipLogs = 3;
    private const int MaxOffscreenSkipsBeforeFallback = 120;
    private const int OffscreenFallbackDelayMs = 3000;
    private const int UiInitWarmupFrames = 180;
    // After a hot reload the device and the game have been running all along: a few
    // frames is enough (180 was ~1 s more of no overlay on every reload).
    private const int UiInitWarmupFramesReload = 5;
    private static int WarmupFrames => EntryPoint.InitCount >= 2 ? UiInitWarmupFramesReload : UiInitWarmupFrames;

    // ── FPS Governor — set by plugins via API ───────────────────────
    internal static bool FpsLimitEnabled;
    internal static int FpsTargetFocused = 60;
    internal static int FpsTargetBackground = 30;
    private static readonly Stopwatch _fpsTimer = Stopwatch.StartNew();
    // Video patch: the cap while AC's 3D scene isn't drawn (VideoPatch.FrameCapThisFrame).
    private static readonly Stopwatch _videoPatchTimer = Stopwatch.StartNew();
    private static bool _fpsLimiterLoggedOnce;
    private static int _fpsDebugCounter;
    private static long _fpsCounterStart;
    private static int _fpsCounterFrames;

    /// <summary>
    /// Last measured EndScene frames-per-second, refreshed once per second.
    /// Updated whether or not the FPS governor is enabled — UI panels (the
    /// RynthAi footer) read this for a live frame-rate display. Volatile
    /// because writes happen on the AC pump thread and reads happen on the
    /// Avalonia dispatcher thread (10 Hz panel tick); double-word atomic on
    /// x86 and we don't need a tear-free guarantee on this value.
    /// </summary>
    internal static volatile float MeasuredFps;
    private static long _liveFpsCounterStart;
    private static int _liveFpsCounterFrames;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint ms);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint ms);

    private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2;
    private const uint TIMER_ALL_ACCESS = 0x1F0003;
    private static IntPtr _frameTimer;
    private static bool _frameTimerTried;

    /// <summary>
    /// Waits <paramref name="ms"/> without burning the CPU. The governor used to wait with
    /// Sleep(0) in a loop when focused (Sleep(0) returns at once when nothing else is ready,
    /// so a "capped" focused client spun a whole core) and Sleep(1) in the background (15.6 ms
    /// timer steps, so a 30 fps cap gave 19 fps, 2026-10-03). A high-resolution waitable timer
    /// (Windows 10 1803+) wakes within about half a millisecond; without one, timeBeginPeriod(1)
    /// makes Sleep(1) a 1 ms sleep. The last 0.3 ms is a yield loop for accuracy.
    /// </summary>
    private static void WaitFrame(double ms)
    {
        if (!_frameTimerTried)
        {
            _frameTimerTried = true;
            try { _frameTimer = CreateWaitableTimerExW(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS); } catch { }
            if (_frameTimer == IntPtr.Zero) { try { timeBeginPeriod(1); } catch { } }
            RynthLog.D3D9($"EndSceneHook: FPS governor waits with {(_frameTimer != IntPtr.Zero ? "a high-resolution timer" : "1 ms sleeps")}.");
        }

        long start = Stopwatch.GetTimestamp();
        double waitMs = ms - 0.3;
        if (waitMs > 0.5)
        {
            if (_frameTimer != IntPtr.Zero)
            {
                long due = -(long)(waitMs * 10_000);   // negative = relative, in 100 ns units
                if (SetWaitableTimer(_frameTimer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                    WaitForSingleObject(_frameTimer, (uint)Math.Ceiling(waitMs) + 50);
            }
            else
            {
                while ((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency < waitMs - 1.0)
                    Thread.Sleep(1);
            }
        }
        while ((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency < ms)
            Thread.Yield();
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EndSceneDelegate(IntPtr pDevice);

    private static EndSceneDelegate? _originalEndScene;
    private static EndSceneDelegate? _hookDelegate;
    private static IntPtr _endSceneAddr;
    private static bool _installed;
    private static int _frameCount;
    /// <summary>Back-buffer frames drawn (offscreen passes - Decal views, VVS, UB - not counted). Resets on reinstall.</summary>
    internal static int BackBufferFrames => Volatile.Read(ref _frameCount);
    private static int _renderCount;
    private static int _skipCount;
    private static int _uiFrameCount;
    private static bool _offscreenFilterDisabled;
    private static bool _uiActivated;
    private static bool _warmupLogged;
    private static long _installTick;
    private static long _firstOffscreenTick;
    private static string _installSource = "uninitialized";

    public static void Install()
    {
        Install("fallback-vtable");
    }

    public static void Install(string installSource)
    {
        if (_installed)
            return;

        ResetInstallState(installSource);

        // Vtable discovery uses a NULLREF device to avoid GPU contention
        // with the game's render thread. Retry once on failure.
        const int maxAttempts = 2;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            RynthLog.D3D9($"EndSceneHook: Discovering D3D9 vtable (attempt {attempt}/{maxAttempts})...");

            if (attempt > 1)
                System.Threading.Thread.Sleep(500);

            IntPtr[]? vtable = D3D9VTable.GetDeviceVTable();
            if (vtable != null)
            {
                IntPtr endSceneAddress = vtable[DeviceVTableIndex.EndScene];
                RynthLog.D3D9($"EndSceneHook: EndScene @ 0x{endSceneAddress:X8}");
                InstallFromEndSceneAddress(endSceneAddress);
                return;
            }

            RynthLog.D3D9($"EndSceneHook: Vtable discovery returned null on attempt {attempt}.");
        }

        RynthLog.D3D9("EndSceneHook: FAILED - could not discover vtable after all attempts.");
    }

    /// <summary>
    /// Decal clients (D3D9Bootstrapper.DecalInProcess): no D3D9 object is created (Decal
    /// detours Direct3DCreate9 and CreateDevice). AC's device comes from the Decal bridge
    /// (Decal's public IDecalCore.GetD3DDevice); Decal has swapped its vtable for a copy, so
    /// d3d9.dll's original vtable is found by matching and d3d9's own EndScene is hooked
    /// (DecalD3D9.TryResolve). A heap or image scan is not used: it can't tell AC's device
    /// from the ones Decal plugins create (measured: it picked another device).
    /// </summary>
    public static void InstallWithoutDevice(string installSource)
    {
        if (_installed)
            return;
        ResetInstallState(installSource);
        DecalD3D9.Enabled = true;
        // Resolved (read-only) by EntryPoint before it chose this path; never Decal's code.
        if (!DecalD3D9.TryResolve())
        {
            RynthLog.D3D9("EndSceneHook: FAILED - AC's device couldn't be identified through the Decal bridge; no in-game UI in this client.");
            return;
        }
        RynthLog.D3D9($"EndSceneHook: hooking d3d9's own EndScene @ 0x{DecalD3D9.ResolvedEndScene.ToInt32():X8} ({DecalD3D9.Describe(DecalD3D9.ResolvedEndScene)}).");
        InstallFromEndSceneAddress(DecalD3D9.ResolvedEndScene);
    }

    public static void InstallFromDevice(IntPtr pDevice)
    {
        if (_installed)
            return;

        if (pDevice == IntPtr.Zero)
        {
            RynthLog.D3D9("EndSceneHook: Real-device install skipped because the device pointer was null.");
            return;
        }

        ResetInstallState("real-device");

        IntPtr deviceVTable = Marshal.ReadIntPtr(pDevice);
        IntPtr endSceneAddress = Marshal.ReadIntPtr(deviceVTable, DeviceVTableIndex.EndScene * IntPtr.Size);
        RynthLog.D3D9($"EndSceneHook: Using real D3D9 device 0x{pDevice:X8}; EndScene @ 0x{endSceneAddress:X8}");
        InstallFromEndSceneAddress(endSceneAddress);
    }

    public static void InstallFromAddress(IntPtr endSceneAddress, string installSource)
    {
        if (_installed)
            return;

        if (endSceneAddress == IntPtr.Zero)
        {
            RynthLog.D3D9("EndSceneHook: Address install skipped because EndScene was null.");
            return;
        }

        ResetInstallState(installSource);
        RynthLog.D3D9($"EndSceneHook: Using explicit EndScene address 0x{endSceneAddress:X8} from {installSource}.");
        InstallFromEndSceneAddress(endSceneAddress);
    }

    public static bool IsInstalled()
    {
        return _installed;
    }

    public static int GetRenderCount()
    {
        return _renderCount;
    }

    public static void Uninstall()
    {
        if (!_installed)
            return;

        // Order is critical: disable the detour FIRST so AC's render thread
        // stops entering our code, then drain in-flight calls, THEN tear down
        // ImGui. If we destroyed the context first, an in-flight EndScene call
        // would assert on `GImGui != NULL`.
        int status = MinHook.MH_DisableHook(_endSceneAddr);
        RynthLog.D3D9($"EndSceneHook: Disable = {MinHook.StatusString(status)}");

        // Let the render thread return through the trampoline.
        Thread.Sleep(80);

        DeviceResetHook.Uninstall();
        EngineFrameController.Shutdown();

        status = MinHook.MH_RemoveHook(_endSceneAddr);
        RynthLog.D3D9($"EndSceneHook: Remove = {MinHook.StatusString(status)}");

        _installed = false;
        _originalEndScene = null;
        _offscreenFilterDisabled = false;
    }

    private static void ResetInstallState(string installSource)
    {
        _installSource = installSource;
        _frameCount = 0;
        _renderCount = 0;
        _skipCount = 0;
        _uiFrameCount = 0;
        _offscreenFilterDisabled = false;
        _uiActivated = false;
        _warmupLogged = false;
        _installTick = Environment.TickCount64;
        _firstOffscreenTick = 0;
    }

    private static void InstallFromEndSceneAddress(IntPtr endSceneAddress)
    {
        _endSceneAddr = endSceneAddress;

        _hookDelegate = new EndSceneDelegate(EndSceneDetour);
        IntPtr hookPtr = Marshal.GetFunctionPointerForDelegate(_hookDelegate);

        try
        {
            _originalEndScene = Marshal.GetDelegateForFunctionPointer<EndSceneDelegate>(MinHook.HookCreate(_endSceneAddr, hookPtr));
            Thread.MemoryBarrier();
            MinHook.Enable(_endSceneAddr);
            _installed = true;

            RynthLog.D3D9($"EndSceneHook: INSTALLED successfully via {_installSource}.");
        }
        catch (Exception ex)
        {
            RynthLog.D3D9($"EndSceneHook: MinHook failed - {ex.Message}");
        }
    }

    // AC's own EndScene (and whatever AC does until the next frame): the hang watchdog's
    // step goes back to "outside the engine" first.
    private static int CallOriginalEndScene(IntPtr pDevice)
    {
        MainThreadHangWatchdog.Step = null;
        return _originalEndScene!(pDevice);
    }

    private static int EndSceneDetour(IntPtr pDevice)
    {
        // Liveness beacon for MainThreadHangWatchdog — this detour runs on AC's
        // main/render thread every frame (even with the ImGui backend disabled),
        // so a stalled beat means the main thread is wedged. Must never throw.
        try { MainThreadHangWatchdog.MainThreadBeat(); } catch { }
        MainThreadHangWatchdog.Step = "EndScene: engine frame";
        try
        {
            if (!_offscreenFilterDisabled && !DX9Backend.IsRenderingToBackBuffer(pDevice))
            {
                if (_firstOffscreenTick == 0)
                    _firstOffscreenTick = Environment.TickCount64;

                if (_skipCount < MaxOffscreenSkipLogs)
                    RynthLog.D3D9("EndSceneHook: Skipping offscreen EndScene pass.");
                _skipCount++;

                long offscreenElapsedMs = Environment.TickCount64 - _firstOffscreenTick;
                // Decal clients: Decal's views, VVS and UB render into their own targets every
                // frame, so an offscreen EndScene is normal there and the fallback below would
                // draw our UI (and nav rings) into their textures (measured 2026-09-30). Never
                // fall back with Decal; without Decal nothing changes.
                if (DecalD3D9.Enabled ||
                    (_skipCount < MaxOffscreenSkipsBeforeFallback && offscreenElapsedMs < OffscreenFallbackDelayMs))
                    return CallOriginalEndScene(pDevice);

                _offscreenFilterDisabled = true;
                RynthLog.D3D9($"EndSceneHook: Falling back to unfiltered EndScene after {_skipCount} skipped offscreen pass(es) over {offscreenElapsedMs}ms.");
            }

            _renderCount++;
            _frameCount++;

            // These four call into gameplay-UI panels they cached (gmMainChatUI, the radar, the
            // powerbar, the vitals bars). AC frees those when it leaves the world; outside the
            // world (UiFlowHooks: a screen change seen by the UseNewMode hook or the mode poll)
            // they don't run. Mode not known yet = as before.
            if (UiFlowHooks.InWorldOrUnknown && !UiFlowHooks.ClientCleanupStarted)
            {
                // Per-frame chatbox visibility assertion (no-op unless plugin enables suppression).
                try { ChatHooks.TickHide(); } catch { /* never let this bring down EndScene */ }
                // Per-frame retail-radar visibility assertion (no-op unless plugin enables suppression).
                try { RadarHooks.TickHide(); } catch { /* never let this bring down EndScene */ }
                // Per-frame retail-powerbar visibility assertion (no-op unless plugin enables suppression).
                try { PowerbarHooks.TickHide(); } catch { /* never let this bring down EndScene */ }
                // Per-frame retail health/stamina/mana bars visibility ("Hide retail vitals").
                try { RetailVitalsHooks.TickHide(); } catch { /* never let this bring down EndScene */ }
                // Per-frame retail toolbar + shortcut bar visibility ("Hide retail toolbar").
                try { RetailToolbarHooks.TickHide(); } catch { /* never let this bring down EndScene */ }
            }

            if (_renderCount == 1 && _offscreenFilterDisabled && _skipCount > 0)
            {
                long installElapsedMs = Environment.TickCount64 - _installTick;
                RynthLog.D3D9($"EndSceneHook: First render after offscreen fallback ({_skipCount} skip(s), {installElapsedMs}ms since install).");
            }

            if (_renderCount == 1 && _skipCount > 0)
                RynthLog.D3D9($"EndSceneHook: First backbuffer frame after skipping {_skipCount} offscreen pass(es).");

            // On first backbuffer frame, verify we hooked the right function by
            // reading the device's own vtable.  A mismatch means the module scan
            // found an internal/proxy vtable, not the one the game's device uses.
            if (_renderCount == 1)
            {
                IntPtr deviceVtable = Marshal.ReadIntPtr(pDevice);
                IntPtr actualEndScene = Marshal.ReadIntPtr(deviceVtable, DeviceVTableIndex.EndScene * IntPtr.Size);
                bool match = actualEndScene == _endSceneAddr;
                RynthLog.D3D9($"EndSceneHook: Device vtable EndScene=0x{actualEndScene:X8}, hooked=0x{_endSceneAddr:X8} — {(match ? "MATCH" : "MISMATCH")}");
                if (DecalD3D9.Enabled)
                    DecalD3D9.FirstFrame(pDevice);

                if (!match && DecalD3D9.Enabled)
                {
                    // Decal client: the device's slot is Decal's wrapper (we hooked d3d9's own
                    // EndScene, which that wrapper calls). Never rehook onto Decal's code.
                    RynthLog.D3D9($"EndSceneHook: mismatch expected with Decal (slot is {DecalD3D9.Describe(actualEndScene)}) - keeping the d3d9 hook.");
                }
                else if (!match && actualEndScene != IntPtr.Zero)
                {
                    // Deep-audit finding #8 (2026-06-18): the old sequence
                    // disabled+REMOVED the old hook (freeing the trampoline
                    // _originalEndScene points at) BEFORE attempting the new
                    // install. If the new MH_CreateHook then threw,
                    // _originalEndScene was never reassigned — it still bound
                    // the now-freed trampoline — yet the code force-set
                    // _installed=true and this function's bottom called
                    // through it: a use-after-free. Fixed order: only remove
                    // the OLD hook after the NEW one is confirmed installed;
                    // on failure, re-enable the OLD hook and keep using its
                    // still-valid trampoline instead of a dangling one.
                    RynthLog.D3D9("EndSceneHook: Rehooking at the device's actual EndScene address.");
                    IntPtr oldAddr = _endSceneAddr;
                    EndSceneDelegate? oldOriginal = _originalEndScene;
                    MinHook.MH_DisableHook(oldAddr);

                    InstallFromEndSceneAddress(actualEndScene);
                    if (_installed)
                    {
                        // New hook confirmed live — safe to free the old
                        // trampoline now.
                        MinHook.MH_RemoveHook(oldAddr);
                        return CallOriginalEndScene(pDevice);
                    }

                    RynthLog.D3D9("EndSceneHook: Rehook failed — restoring original hook.");
                    // New install failed (InstallFromEndSceneAddress leaves
                    // _installed=false on failure). Restore the OLD hook
                    // rather than trust a dangling delegate.
                    _endSceneAddr = oldAddr;
                    _originalEndScene = oldOriginal;
                    MinHook.Enable(oldAddr);
                    _installed = true;
                }
            }

            if (!_uiActivated)
            {
                if (!_warmupLogged)
                {
                    RynthLog.D3D9($"EndSceneHook: Backbuffer detected. Warming up {WarmupFrames} frame(s) before UI init.");
                    _warmupLogged = true;
                }

                if (_renderCount < WarmupFrames)
                    return CallOriginalEndScene(pDevice);

                _uiActivated = true;
                RynthLog.D3D9("EndSceneHook: Warmup complete - initializing ImGui.");
            }

            // EngineFrameController.OnEndScene runs the always-on engine work
            // (matrix capture, plugin tick, nav rendering, pending action drains)
            // every frame. The EnableImGuiBackend gate moved INSIDE the
            // controller so it scopes only the ImGui-specific block; this call
            // site no longer needs to gate it.
            EngineFrameController.OnEndScene(pDevice);
            _uiFrameCount++;

            if (_uiFrameCount == 60 && RynthCore.Engine.Plugins.EngineSettings.EnableImGuiBackend)
                RynthLog.D3D9("EndSceneHook: 60 UI frames - ImGui is stable.");
            if (_renderCount == WarmupFrames && !RynthCore.Engine.Plugins.EngineSettings.EnableImGuiBackend)
                RynthLog.D3D9("EndSceneHook: ImGui backend disabled via engine.json (EnableImGuiBackend=false). Always-on engine work still runs; only ImGui draw calls are skipped.");

            // Avalonia compositor: independent of ImGui. Reads the latest
            // Avalonia surface from OverlaySurfaceBridge and blits it as a
            // fullscreen alpha-blended quad onto AC's back buffer. Runs every
            // frame regardless of EnableImGuiBackend so the Avalonia overlay
            // is visible even when ImGui per-frame work is disabled. Driven
            // here exclusively — EngineFrameController.OnEndScene intentionally
            // does NOT call it (avoids double-blit / TryConsume races).
            // The UI hides between characters: nothing is drawn at character select or
            // on the way out, and it's back the moment login completes (an engine
            // reload synthesises login for a player already in the world).
            MainThreadHangWatchdog.Step = "EndScene: overlay blit and ImGui submit";
            try
            {
                if (Compatibility.LoginLifecycleHooks.HasObservedLoginComplete)
                    OverlayTextureRenderer.Render(pDevice);
            }
            catch (Exception ovEx)
            {
                if (_uiFrameCount < 30)
                    RynthLog.D3D9($"EndSceneHook: OverlayTextureRenderer.Render error: {ovEx.GetType().Name}: {ovEx.Message}");
            }

            // ImGui draw data (in-client panels, plugin overlay windows such as the Mini Remote)
            // is submitted LAST so it sits on top of the Avalonia layer. Built in OnEndScene;
            // guarded internally, so an ImGui draw failure can't skip the original EndScene.
            EngineFrameController.RenderDeferredImGui();
        }
        catch (Exception ex)
        {
            if (_uiFrameCount < 30)
                RynthLog.D3D9($"EndSceneHook: Frame {_frameCount} error: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        // ── Always-on FPS measurement for UI footer ──────────────────
        // Runs whether or not the governor is enabled so the RynthAi
        // footer can show a live FPS even when the user hasn't capped.
        // Cheap: one increment + a once-per-second division.
        _liveFpsCounterFrames++;
        long liveNow = Environment.TickCount64;
        if (_liveFpsCounterStart == 0) _liveFpsCounterStart = liveNow;
        long liveElapsed = liveNow - _liveFpsCounterStart;
        if (liveElapsed >= 1000)
        {
            MeasuredFps = (float)(_liveFpsCounterFrames * 1000.0 / liveElapsed);
            _liveFpsCounterFrames = 0;
            _liveFpsCounterStart = liveNow;
        }

        // ── FPS Governor (matches proven NexSuite2 pattern) ─────────
        if (FpsLimitEnabled)
        {
            IntPtr fgWnd = GetForegroundWindow();
            // Focused when AC itself is foreground, OR when any window belonging
            // to our process is foreground (covers DComp overlay, any Avalonia
            // child window, etc.). PID comparison is reliable regardless of how
            // the owner chain was established (GWL_HWNDPARENT vs CreateWindow).
            GetWindowThreadProcessId(fgWnd, out uint fgPid);
            bool isFocused = fgWnd != IntPtr.Zero &&
                (fgWnd == Win32Backend.GameHwnd || fgPid == GetCurrentProcessId());
            int targetFps = isFocused ? FpsTargetFocused : FpsTargetBackground;
            double minFrameMs = 1000.0 / Math.Max(targetFps, 1);

            double leftMs = minFrameMs - _fpsTimer.Elapsed.TotalMilliseconds;
            if (leftMs > 0) WaitFrame(leftMs);
            _fpsTimer.Restart();

            if (!_fpsLimiterLoggedOnce)
            {
                _fpsLimiterLoggedOnce = true;
                RynthLog.D3D9($"EndSceneHook: FPS governor active — max={FpsTargetFocused} focused, max={FpsTargetBackground} background, gameHwnd=0x{Win32Backend.GameHwnd.ToInt64():X}");
            }

            // Periodic FPS measurement — log every 60 seconds
            _fpsCounterFrames++;
            long now = Environment.TickCount64;
            if (_fpsCounterStart == 0) _fpsCounterStart = now;
            long elapsedMs = now - _fpsCounterStart;
            if (elapsedMs >= 60_000)
            {
                double measuredFps = _fpsCounterFrames * 1000.0 / elapsedMs;
                RynthLog.D3D9($"EndSceneHook: FPS={measuredFps:F1} (target={targetFps}, {(isFocused ? "focused" : "background")})");
                _fpsCounterFrames = 0;
                _fpsCounterStart = now;
            }
        }

        // ── Video patch frame cap ───────────────────────────────────
        // While the video patch skips AC's 3D scene, the loop would just spin faster on
        // an empty frame: cap it (default 15 fps) so the time is slept. Stacks with the
        // governor above (the lower rate wins); 0 = no cap (off, focused in "unfocused",
        // outside the world, or /rc video fps 0). Game logic still runs every frame.
        try
        {
            int vpCap = VideoPatch.FrameCapThisFrame();
            if (vpCap > 0)
            {
                double leftMs = 1000.0 / vpCap - _videoPatchTimer.Elapsed.TotalMilliseconds;
                if (leftMs > 0) WaitFrame(Math.Min(leftMs, 1000.0 / VideoPatchPolicy.MinFrameCapFps));
            }
        }
        catch { /* never let the cap take EndScene down */ }
        _videoPatchTimer.Restart();

        return CallOriginalEndScene(pDevice);
    }
}
