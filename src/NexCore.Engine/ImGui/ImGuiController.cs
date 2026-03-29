// ============================================================================
//  NexCore.Engine - ImGui/ImGuiController.cs
//  Top-level orchestrator: creates context, initializes backends,
//  and drives the NewFrame -> UI -> Render cycle from EndScene.
// ============================================================================

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ImGuiNET;
using NexCore.Engine.Plugins;

namespace NexCore.Engine.ImGuiBackend;

internal static class ImGuiController
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

    private static bool _initialized;
    private static bool _initFailed;
    private static bool _pluginsInitialized;
    private static IntPtr _context;
    private static IntPtr _gameHwnd;
    private static long _lastFrameTicks;
    private static int _frameCount;

    public static bool Init(IntPtr pDevice)
    {
        if (_initialized)
            return true;

        // Prefer the actual D3D device window so startup does not latch onto a splash/loading HWND.
        _gameHwnd = DX9Backend.GetDeviceWindow(pDevice);
        if (_gameHwnd == IntPtr.Zero)
        {
            EntryPoint.Log("ImGuiController: D3D device window unavailable, falling back to EnumWindows.");
            _gameHwnd = FindGameWindow();
        }

        if (_gameHwnd == IntPtr.Zero)
        {
            EntryPoint.Log("ImGuiController: Could not find game window.");
            return false;
        }

        EntryPoint.Log($"ImGuiController: Game HWND = 0x{_gameHwnd:X8}");

        IntPtr previousContext = ImGuiNET.ImGui.GetCurrentContext();
        if (previousContext != IntPtr.Zero)
            EntryPoint.Log($"ImGuiController: Existing ImGui context detected (0x{previousContext:X8}) - isolating NexCore context.");

        _context = ImGuiNET.ImGui.CreateContext();
        bool initSucceeded = false;
        try
        {
            ImGuiNET.ImGui.SetCurrentContext(_context);
            ImGuiIOPtr io = ImGuiNET.ImGui.GetIO();

            io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
            ImGuiNET.ImGui.StyleColorsDark();

            if (!Win32Backend.Init(_gameHwnd))
                return false;

            if (!DX9Backend.Init(pDevice))
                return false;

            _lastFrameTicks = Stopwatch.GetTimestamp();
            _initialized = true;
            initSucceeded = true;

            EntryPoint.Log("ImGuiController: Fully initialized - NexCore shell active.");
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
        if (!_initialized)
            return;

        IntPtr previousContext = ImGuiNET.ImGui.GetCurrentContext();
        IntPtr contextToDestroy = _context;

        try
        {
            ImGuiNET.ImGui.SetCurrentContext(contextToDestroy);

            PluginManager.ShutdownAll();
            DX9Backend.Shutdown();
            Win32Backend.Shutdown();
            ImGuiNET.ImGui.DestroyContext(contextToDestroy);

            _initialized = false;
            _context = IntPtr.Zero;
        }
        finally
        {
            ImGuiNET.ImGui.SetCurrentContext(previousContext == contextToDestroy ? IntPtr.Zero : previousContext);
        }
    }

    public static void OnEndScene(IntPtr pDevice)
    {
        if (_initFailed)
            return;

        if (!_initialized)
        {
            if (!Init(pDevice))
            {
                _initFailed = true;
                return;
            }
        }

        IntPtr previousContext = ImGuiNET.ImGui.GetCurrentContext();
        ImGuiNET.ImGui.SetCurrentContext(_context);
        bool frameStarted = false;
        bool frameEnded = false;

        try
        {
            // Initialize plugins once ImGui is fully ready
            if (!_pluginsInitialized)
            {
                _pluginsInitialized = true;
                PluginManager.InitPlugins(_context, pDevice, _gameHwnd);
            }

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

            // Drain queued host/plugin callbacks before opening the Dear ImGui frame.
            // This keeps heavy world-update bursts from stretching an already-active frame.
            PluginManager.ProcessPendingActions(_context, pDevice, _gameHwnd);

            // Plugin tick (per-frame logic, before ImGui drawing)
            PluginManager.TickAll();

            ImGuiNET.ImGui.NewFrame();
            frameStarted = true;
            NexCoreShell.Render(_frameCount);

            // Plugin render (ImGui draw calls)
            PluginManager.RenderAll();

            bool captureMouse =
                ImGuiNET.ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow) ||
                ImGuiNET.ImGui.IsAnyItemActive();
            bool captureKeyboard = ImGuiNET.ImGui.IsAnyItemActive();

            ImGuiNET.ImGui.EndFrame();
            frameEnded = true;
            ImGuiNET.ImGui.Render();
            Win32Backend.UpdateCaptureFlags(captureMouse, captureKeyboard);

            ImDrawDataPtr drawData = ImGuiNET.ImGui.GetDrawData();
            if (_frameCount < 3)
                EntryPoint.Log($"ImGui frame {_frameCount}: io.DisplaySize={io.DisplaySize}");

            DX9Backend.RenderDrawData(drawData, pDevice);

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

            EntryPoint.Log($"ImGuiController: frame {_frameCount} error: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            ImGuiNET.ImGui.SetCurrentContext(previousContext);
        }
    }

    private static IntPtr FindGameWindow()
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
