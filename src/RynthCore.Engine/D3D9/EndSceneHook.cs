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

    // ── FPS Governor — set by plugins via API ───────────────────────
    internal static bool FpsLimitEnabled;
    internal static int FpsTargetFocused = 60;
    internal static int FpsTargetBackground = 30;
    private static readonly Stopwatch _fpsTimer = Stopwatch.StartNew();
    private static bool _fpsLimiterLoggedOnce;
    private static int _fpsDebugCounter;
    private static long _fpsCounterStart;
    private static int _fpsCounterFrames;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EndSceneDelegate(IntPtr pDevice);

    private static EndSceneDelegate? _originalEndScene;
    private static EndSceneDelegate? _hookDelegate;
    private static IntPtr _endSceneAddr;
    private static bool _installed;
    private static int _frameCount;
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

        ImGuiController.Shutdown();

        int status = MinHook.MH_DisableHook(_endSceneAddr);
        RynthLog.D3D9($"EndSceneHook: Disable = {MinHook.StatusString(status)}");

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

    private static int EndSceneDetour(IntPtr pDevice)
    {
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
                if (_skipCount < MaxOffscreenSkipsBeforeFallback && offscreenElapsedMs < OffscreenFallbackDelayMs)
                    return _originalEndScene!(pDevice);

                _offscreenFilterDisabled = true;
                RynthLog.D3D9($"EndSceneHook: Falling back to unfiltered EndScene after {_skipCount} skipped offscreen pass(es) over {offscreenElapsedMs}ms.");
            }

            _renderCount++;
            _frameCount++;

            // Per-frame chatbox visibility assertion (no-op unless plugin enables suppression).
            try { ChatHooks.TickHide(); } catch { /* never let this bring down EndScene */ }

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

                if (!match && actualEndScene != IntPtr.Zero)
                {
                    RynthLog.D3D9("EndSceneHook: Rehooking at the device's actual EndScene address.");
                    MinHook.MH_DisableHook(_endSceneAddr);
                    MinHook.MH_RemoveHook(_endSceneAddr);
                    _installed = false;

                    InstallFromEndSceneAddress(actualEndScene);
                    if (_installed)
                        return _originalEndScene!(pDevice);

                    RynthLog.D3D9("EndSceneHook: Rehook failed — continuing with original hook.");
                    // Restore state so we don't keep trying
                    _installed = true;
                }
            }

            if (!_uiActivated)
            {
                if (!_warmupLogged)
                {
                    RynthLog.D3D9($"EndSceneHook: Backbuffer detected. Warming up {UiInitWarmupFrames} frame(s) before UI init.");
                    _warmupLogged = true;
                }

                if (_renderCount < UiInitWarmupFrames)
                    return _originalEndScene!(pDevice);

                _uiActivated = true;
                RynthLog.D3D9("EndSceneHook: Warmup complete - initializing ImGui.");
            }

            ImGuiController.OnEndScene(pDevice);
            _uiFrameCount++;

            if (_uiFrameCount == 60)
                RynthLog.D3D9("EndSceneHook: 60 UI frames - ImGui is stable.");
        }
        catch (Exception ex)
        {
            if (_uiFrameCount < 30)
                RynthLog.D3D9($"EndSceneHook: Frame {_frameCount} error: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }

        // ── FPS Governor (matches proven NexSuite2 pattern) ─────────
        if (FpsLimitEnabled)
        {
            IntPtr fgWnd = GetForegroundWindow();
            // Only "focused" when the AC game window itself is foreground — not viewport popups.
            bool isFocused = fgWnd != IntPtr.Zero && fgWnd == Win32Backend.GameHwnd;
            int targetFps = isFocused ? FpsTargetFocused : FpsTargetBackground;
            double minFrameMs = 1000.0 / Math.Max(targetFps, 1);

            while (_fpsTimer.Elapsed.TotalMilliseconds < minFrameMs)
                Thread.Sleep(isFocused ? 0 : 1);
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

        return _originalEndScene!(pDevice);
    }
}
