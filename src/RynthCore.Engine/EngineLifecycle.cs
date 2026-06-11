// ============================================================================
//  RynthCore.Engine - EngineLifecycle.cs
//  Orderly engine teardown so the loader can FreeLibrary RynthCore.Engine.dll
//  without crashing acclient.exe.
//
//  Order matters. Each phase is wrapped so a failure does not abort the rest:
//
//   1. EndSceneHook.Uninstall — chains into EngineFrameController.Shutdown which
//      tears down PluginManager (RynthPluginShutdown + FreeLibrary plugins),
//      OverlayTextureRenderer, ViewportRendererBackend, ViewportPlatformBackend,
//      DX9Backend, Win32Backend (restores WndProc), and destroys ImGui context.
//   2. Drain — sleep so any in-flight EndScene call has returned through the
//      trampoline before we tear down anything else.
//   3. AvaloniaOverlay.Stop — Dispatcher shutdown, join STA thread.
//   4. PluginManager.ShutdownAll — defensive (no-op if step 1 already ran).
//   5. MH_DisableHook(ALL) + MH_Uninitialize — safety net for Compatibility/*
//      hooks that aren't in the ImGui chain.
//
//  After this returns, the engine has stopped reading game memory, stopped
//  drawing, and stopped servicing callbacks. The loader may now FreeLibrary
//  the engine module.
//
//  IMPORTANT: do not call from the AC render thread (an EndScene detour) —
//  the caller must be on a separate thread so the detour can return cleanly
//  before we disable the hook.
// ============================================================================

using System;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.D3D9;
using RynthCore.Engine.Hooking;
using RynthCore.Engine.Plugins;
using RynthCore.Engine.UI;

namespace RynthCore.Engine;

internal static class EngineLifecycle
{
    private static int _shuttingDown;
    private static int _hasShutDown;

    /// <summary>
    /// Named auto-reset event the loader (RynthCore.Loader.dll) waits on.
    /// Signaling it triggers shutdown + FreeLibrary + LoadLibrary + re-init.
    /// Must match the helper in RynthCore.Loader.EntryPoint.
    /// PID-suffixed so a reload click on one acclient.exe doesn't trigger
    /// reload in every other acclient.exe in the same login session
    /// (which is the broadcast scope of "Local\" without the suffix).
    /// </summary>
    private static string ReloadEventName => $"Local\\RynthCore.Engine.RequestReload.p{Environment.ProcessId}";

    private const uint EVENT_MODIFY_STATE = 0x0002;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenEventW(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetEvent(IntPtr hEvent);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    internal static bool IsShuttingDown => Volatile.Read(ref _shuttingDown) != 0;
    internal static bool HasShutDown => Volatile.Read(ref _hasShutDown) != 0;

    /// <summary>
    /// Asks the loader to reload the engine. Returns immediately; the loader's
    /// watcher thread runs the actual shutdown+reload sequence so the caller
    /// (typically the ImGui shell on the render thread) can finish its frame.
    /// </summary>
    public static bool SignalReload()
    {
        IntPtr hEvent = OpenEventW(EVENT_MODIFY_STATE, false, ReloadEventName);
        if (hEvent == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            RynthLog.Info($"SignalReload: OpenEventW failed (error {err}). Loader watcher may not be running.");
            return false;
        }

        try
        {
            bool ok = SetEvent(hEvent);
            if (!ok)
                RynthLog.Info($"SignalReload: SetEvent failed (error {Marshal.GetLastWin32Error()}).");
            else
                RynthLog.Info("SignalReload: reload event signaled — loader will run reload sequence.");
            return ok;
        }
        finally
        {
            CloseHandle(hEvent);
        }
    }

    public static void Shutdown()
    {
        if (Interlocked.CompareExchange(ref _shuttingDown, 1, 0) != 0)
        {
            RynthLog.Info("EngineLifecycle: Shutdown already in progress — ignoring repeat call.");
            return;
        }

        RynthLog.Info("EngineLifecycle: Shutdown beginning.");

        // Stop the hang watchdog BEFORE its beat source (the EndScene detour)
        // is uninstalled. A still-running old-generation watchdog sees the beat
        // go silent, declares a false hang ~4s later, suspends AC's healthy main
        // thread for stack samples, and writes a spurious minidump — all while
        // the next engine generation is initializing.
        Step("MainThreadHangWatchdog.Stop", () => MainThreadHangWatchdog.Stop());

        // Disarm the marshalled-action queue BEFORE anything else tears down.
        // The UseTime/EndScene detours stay live until MH_DisableHook(ALL), so
        // without this a queued plugin mutation (SetAutoRun etc.) executes on
        // AC's main thread mid-teardown against state the plugin Shutdowns are
        // concurrently freeing — a heap-corruption window adjacent to the
        // 2026-06-11 DINPUT8 reload wedge.
        Step("AcMainThreadQueue.Disarm", () => Compatibility.AcMainThreadQueue.Disarm());

        Step("EndSceneHook.Uninstall", () => EndSceneHook.Uninstall());

        // Give in-flight EndScene calls a chance to drain. AC's render thread
        // may still be inside our detour right now; sleeping a few frames is
        // a cheap way to ensure it has returned through the trampoline before
        // we tear down anything else.
        Thread.Sleep(80);

        Step("AvaloniaOverlay.Stop", () => AvaloniaOverlay.Stop());

        // Restore AC's original window WndProc. EngineFrameController.Shutdown
        // also calls Win32Backend.Shutdown, but it early-returns when ImGui
        // never initialized (EnableImGuiBackend=false) — and in that mode the
        // subclass is installed by AvaloniaOverlay.Start()'s GameHwnd poller,
        // so it would otherwise never be removed. It then leaks across hot
        // reloads (each gen stacks on the last; the frozen gen swallows WM_CHAR
        // → AC chat box stops receiving input after a reload). Idempotent —
        // no-ops if Init never ran or the ImGui path already tore it down.
        Step("Win32Backend.Shutdown (input subclass)", () => ImGuiBackend.Win32Backend.Shutdown());

        // Stop the dispatch-file watcher and the auto-ID drain timer before the
        // tick pump and plugins go away — both otherwise keep firing in the old
        // generation's (intentionally still-mapped) module across hot-reloads.
        Step("ChatFileDispatcher.Stop", () => Compatibility.ChatFileDispatcher.Stop());
        Step("AutoIdService.Stop", () => Compatibility.AutoIdService.Stop());

        // Stop the headless tick pump BEFORE plugin shutdown / FreeLibrary.
        // Otherwise the pump's TickAll/ProcessPendingActions can be mid-call
        // into a plugin whose code pages we're about to unmap → AV.
        Step("TickPump.StopAndJoin", () => EntryPoint.StopTickPumpAndJoin());

        Step("PluginManager.ShutdownAll (defensive)", () => PluginManager.ShutdownAll());

        // Stop HeartbeatLogger BEFORE MinHook teardown — it's a managed thread
        // that keeps running and would execute code pages the loader is about to
        // unmap (FreeLibrary on the engine module right after RynthCoreShutdown
        // returns). Without this, hot-reload fires a CLR exception in the dying
        // module's code at the moment of FreeLibrary.
        Step("HeartbeatLogger.StopAndJoin", () => Compatibility.HeartbeatLogger.StopAndJoin());

        Step("MH_DisableHook(ALL)", () =>
        {
            // DisableHook restores the original code at every hooked function — that's
            // what we actually need for safety. Do NOT call MH_Uninitialize: it frees
            // the trampoline pool memory, and AC's CRT atexit chain (or cached engine
            // delegates) can still reference those addresses during the rest of
            // shutdown → AV at the trampoline block (e.g. 0x04CE0F60). Leaving the
            // trampoline memory allocated for the remainder of the process lifetime
            // is harmless; OS reclaims it on actual exit. On hot-reload, the new
            // engine re-Initializes MinHook; existing process-wide state is fine.
            int disable = MinHook.MH_DisableHook(MinHook.MH_ALL_HOOKS);
            RynthLog.Info($"MH_DisableHook(ALL) = {MinHook.StatusString(disable)}");
        });

        Volatile.Write(ref _hasShutDown, 1);
        RynthLog.Info("EngineLifecycle: Shutdown complete.");
    }

    private static void Step(string name, Action action)
    {
        try
        {
            RynthLog.Info($"EngineLifecycle: -> {name}");
            long t0 = Environment.TickCount64;
            action();
            RynthLog.Info($"EngineLifecycle:    {name} ok ({Environment.TickCount64 - t0} ms)");
        }
        catch (Exception ex)
        {
            RynthLog.Info($"EngineLifecycle:    {name} FAILED: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
