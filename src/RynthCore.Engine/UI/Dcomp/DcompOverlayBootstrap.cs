// Entry point for the DComp parallel overlay system. Activated by:
//   1. Setting "EnableDcompOverlay": true in %APPDATA%\RynthCore\engine.json
//   2. OR setting env var RYNTHCORE_DCOMP_OVERLAY=1 in the launcher's env
// Either trips the gate; engine.json is the recommended path because it
// doesn't depend on env-var propagation through the injector chain.
//
// Production AvaloniaOverlay.Start() is skipped when active (see EntryPoint.cs).
//
// Spawns a dedicated STA thread, brings up Avalonia configured for
// CompositionMode = [WinUIComposition, LowLatencyDxgiSwapChain, RedirectionSurface]
// and default rendering (Avalonia's own ANGLE — no custom Skia bridge, no
// software path). Creates the test window. Spawns a GameHwnd poller so the
// overlay can attach to AC even when ImGui and the production AvaloniaOverlay
// are both skipped.

using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using Avalonia;

namespace RynthCore.Engine.UI.Dcomp;

internal static class DcompOverlayBootstrap
{
    private const string EnvVarName = "RYNTHCORE_DCOMP_OVERLAY";
    private const string EngineJsonField = "EnableDcompOverlay";

    private static Thread? _thread;
    private static bool _started;

    public static bool IsEnabled
    {
        get
        {
            // Env var path — keeps a fast escape hatch for power users.
            if (string.Equals(Environment.GetEnvironmentVariable(EnvVarName), "1", StringComparison.Ordinal))
                return true;

            // engine.json path — recommended. Survives env-var-propagation
            // through the launcher → injector → acclient.exe chain.
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "RynthCore", "engine.json");
                if (!File.Exists(path)) return false;

                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty(EngineJsonField, out var el) &&
                    el.ValueKind == JsonValueKind.True)
                    return true;
            }
            catch
            {
                // Best-effort; default to false on parse error.
            }

            return false;
        }
    }

    public static void Start()
    {
        if (_started) return;
        _started = true;

        RynthLog.UI("DcompOverlay: starting — IsEnabled=true (env=" +
            (Environment.GetEnvironmentVariable(EnvVarName) ?? "<unset>") + ").");

        // Spawn the GameHwnd poller before the Avalonia thread so the window
        // can attach as soon as it's shown. Production AvaloniaOverlay's poller
        // doesn't run in this path (we skipped Start()), and ImGui's path may
        // also be skipped. Pattern mirrors AvaloniaOverlay.Start lines 410-440.
        new Thread(GameHwndPollerLoop)
        {
            Name = "RynthCore.Dcomp.GameHwndPoller",
            IsBackground = true,
        }.Start();

        // Spawn the AC client-rect tracker. It fires ClientRectChanged whenever
        // AC moves, resizes, hides, or shows. The overlay window subscribes
        // to keep its position+size locked to AC's client area.
        AcWindowTracker.Start();

        _thread = new Thread(ThreadMain)
        {
            Name = "RynthCore.Avalonia.Dcomp",
            IsBackground = true,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private static void GameHwndPollerLoop()
    {
        // Polls every 100ms for up to 60s. Once AC's window is found, populates
        // ImGuiBackend.Win32Backend.GameHwnd which DcompOverlayWindow reads.
        long deadline = Environment.TickCount64 + 60_000;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                if (ImGuiBackend.Win32Backend.GameHwnd != IntPtr.Zero)
                {
                    IntPtr existingHwnd = ImGuiBackend.Win32Backend.GameHwnd;
                    RynthLog.UI($"DcompOverlay: GameHwnd already populated externally = 0x{existingHwnd.ToInt64():X}.");
                    if (ImGuiBackend.Win32Backend.Init(existingHwnd))
                        RynthLog.UI("DcompOverlay: WndProc subclassed (externally-set HWND).");
                    else
                        RynthLog.UI("DcompOverlay: WndProc subclass FAILED (externally-set HWND).");
                    return;
                }

                IntPtr hwnd = ImGuiBackend.EngineFrameController.FindGameWindow();
                if (hwnd != IntPtr.Zero)
                {
                    ImGuiBackend.Win32Backend.SetGameHwndExternal(hwnd);
                    EntryPoint.GameHwnd = hwnd;

                    // Subclass AC's WndProc so Win32Backend.WndProcHook intercepts
                    // WM_ACTIVATE(WA_INACTIVE) when our DComp overlay takes focus.
                    // Without this the subclass is never installed in the DComp path
                    // (EngineFrameController.Init, which normally calls Win32Backend.Init,
                    // is skipped when EnableImGuiBackend=false), so AC receives
                    // WA_INACTIVE on every click in the panel and throttles to ~8fps.
                    if (ImGuiBackend.Win32Backend.Init(hwnd))
                        RynthLog.UI("DcompOverlay: WndProc subclassed for WM_ACTIVATE interception.");
                    else
                        RynthLog.UI("DcompOverlay: WndProc subclass FAILED — AC may throttle on overlay interaction.");

                    RynthLog.UI($"DcompOverlay: GameHwnd populated via EnumWindows = 0x{hwnd.ToInt64():X}.");
                    return;
                }
            }
            catch (Exception ex)
            {
                RynthLog.UI($"DcompOverlay: GameHwnd poller swallowed {ex.GetType().Name}: {ex.Message}");
            }
            Thread.Sleep(100);
        }
        RynthLog.UI("DcompOverlay: GameHwnd poller timed out without finding AC's window.");
    }

    private static void ThreadMain()
    {
        try
        {
            // Software rendering + RedirectionSurface: same pipeline as the
            // production overlay. WinUIComposition and LowLatencyDxgiSwapChain
            // both use DirectComposition and contend with AC's D3D9 Present on
            // DWM's VSync budget — the result is AC dropping to ~8fps whenever
            // Avalonia's DComp visual tree is dirty (any interaction). The unique
            // value of this path (separate per-client HWND, multi-client
            // visibility when cascaded) doesn't depend on GPU compositing.
            // RedirectionSurface is also the only mode that works reliably in
            // RDP sessions, where WinUIComposition support is limited.
            var platformOptions = new Avalonia.Win32PlatformOptions
            {
                CompositionMode = new[]
                {
                    Avalonia.Win32CompositionMode.RedirectionSurface,
                },
                RenderingMode = new[]
                {
                    Avalonia.Win32RenderingMode.Software,
                },
                ShouldRenderOnUIThread = true,
            };

            AppBuilder.Configure<DcompOverlayApp>()
                .UsePlatformDetect()
                .With(platformOptions)
                .StartWithClassicDesktopLifetime(Array.Empty<string>());
        }
        catch (Exception ex)
        {
            RynthLog.UI($"DcompOverlay: ThreadMain crashed — {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
    }
}
