// ============================================================================
//  RynthCore.Engine - EngineLifecycle.cs
//  Orderly engine teardown so the loader can FreeLibrary RynthCore.Engine.dll
//  without crashing acclient.exe.
//
//  Order matters. Each phase is wrapped so a failure does not abort the rest:
//
//   0. TickPump.StopAndJoin — the plugin pump stops (and its frame in flight
//      finishes) before any plugin is shut down: no Tick alongside a Shutdown.
//   1. EndSceneHook.Uninstall — chains into EngineFrameController.Shutdown which
//      tears down PluginManager (RynthPluginShutdown + FreeLibrary plugins),
//      OverlayTextureRenderer, DX9Backend, and destroys the ImGui context.
//   2. Drain — sleep so any in-flight EndScene call has returned through the
//      trampoline before we tear down anything else.
//   3. AvaloniaOverlay.Stop — Dispatcher shutdown, join STA thread.
//   4. PluginManager.ShutdownAll — defensive (no-op if step 1 already ran; it
//      tries again if step 1 left the plugins alone because the pump was
//      still inside a frame).
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

    private static long _lastSignalReloadMs;
    /// <summary>Environment.TickCount64 of this engine's last SignalReload (0 = none).</summary>
    internal static long LastSignalReloadMs => Interlocked.Read(ref _lastSignalReloadMs);

    internal static bool IsShuttingDown => Volatile.Read(ref _shuttingDown) != 0;
    internal static bool HasShutDown => Volatile.Read(ref _hasShutDown) != 0;

    /// <summary>
    /// Asks the loader to reload the engine. Returns immediately; the loader's
    /// watcher thread runs the actual shutdown+reload sequence so the caller
    /// (typically the ImGui shell on the render thread) can finish its frame.
    /// </summary>
    public static bool SignalReload()
    {
        // Every caller is a player's request (RL, ERl, /rc rl): the reload cap's chat notice
        // answers those each time (EntryPoint.ReloadCapped).
        Interlocked.Exchange(ref _lastSignalReloadMs, Environment.TickCount64);
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

        // A reload can come while this generation is still initializing (two RL clicks,
        // or a file-watcher reload during login). Let InitWorker reach its next step and
        // stop before tearing anything down under it.
        Step("InitWorker join", () =>
        {
            if (!EntryPoint.WaitForInitWorker(15000))
                RynthLog.Info("EngineLifecycle: InitWorker still running after 15 s; tearing down anyway.");
        });

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

        // Same for queued panel commands (UI.Data.UiDataHub): never run a
        // plugin-mutating UI command while the plugins are shutting down.
        Step("UiDataHub.Disarm", () => UI.Data.UiDataHub.Disarm());

        // Stop the plugin pump (and wait for a frame in flight) BEFORE anything below can
        // shut a plugin down: EndSceneHook.Uninstall runs PluginManager.ShutdownAll (through
        // EngineFrameController.Shutdown), and a pump still running would call a plugin's Tick
        // and events on its own thread while that plugin's Shutdown frees what they use on
        // this one (audit 2026-10-21, 01 #1). It used to stop further down, after the plugins
        // were already gone; only the WM_CLOSE path (Win32Backend) stopped it first. Every
        // shutdown comes through here: the loader's hot reload, ExitProcess and
        // RtlExitUserProcess, WM_CLOSE, the bar's Sd button. If a frame is still running when
        // the wait ends, ShutdownAll leaves the plugins alone (PluginShutdownGate).
        Step("TickPump.StopAndJoin", () => EntryPoint.StopTickPumpAndJoin());

        // AC's UI-pass hook draws the world overlays mid-frame from the same render-side
        // state EndSceneHook.Uninstall tears down: stop it first (its 80 ms drain covers both).
        Step("AcUiPassHook.Uninstall", () => AcUiPassHook.Uninstall());
        // The video patch's scene hook passes straight to AC from here on (AC draws again).
        Step("VideoPatch.Uninstall", () => VideoPatch.Uninstall());
        // The ambient light floor's hook passes AC's own colour from here on (no floor).
        Step("AmbientLight.Uninstall", () => AmbientLight.Uninstall());
        Step("EndSceneHook.Uninstall", () => EndSceneHook.Uninstall());
        // The font atlas baked before login, when no ImGui context took it (EngineFrameController).
        Step("ImGui fonts without a context", () => ImGuiBackend.EngineFrameController.FreeFontsWithoutContext());

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
        Step("HotkeyService.Shutdown", () => Hotkeys.HotkeyService.Shutdown());

        // Stop the dispatch-file watcher and the auto-ID drain timer — both
        // otherwise keep firing in the old generation's (intentionally
        // still-mapped) module across hot-reloads.
        Step("ChatFileDispatcher.Stop", () => Compatibility.ChatFileDispatcher.Stop());
        Step("LogSettings.StopWatcher", () => LogSettings.StopWatcher());
        Step("AutoIdService.Stop", () => Compatibility.AutoIdService.Stop());

        // (The plugin pump stopped at the top, before the plugins were shut down.)
        // spike/decal-bridge: after the pump (its only reader) has stopped.
        Step("DecalBridgeHost.Shutdown", () => Compatibility.DecalBridgeHost.Shutdown());

        // The chat log is written on the tick pump (UiDataHub), now stopped. Left open,
        // it outlived this generation and locked the next one out of the file.
        Step("ChatModel.CloseLog", () => UI.Data.ChatModel.CloseLog());
        // Discord posts still in flight would finish in this dying generation's code.
        Step("ChatDiscord.Shutdown", () => UI.Data.Discord.ChatDiscord.Shutdown());
        Step("ServerMessageStream.Shutdown", () => Net.ServerMessageStream.Shutdown());
        Step("FlightRecorder.Shutdown", () => Net.FlightRecorder.Shutdown());
        // A report still being sent: cancel it and dispose its HttpClient (nothing may outlive this generation).
        Step("ProblemReport.Shutdown", () => Report.ProblemReport.Shutdown());

        Step("PluginManager.ShutdownAll (defensive)", () => PluginManager.ShutdownAll());

        // Stop HeartbeatLogger BEFORE MinHook teardown — it's a managed thread
        // that keeps running and would execute code pages the loader is about to
        // unmap (FreeLibrary on the engine module right after RynthCoreShutdown
        // returns). Without this, hot-reload fires a CLR exception in the dying
        // module's code at the moment of FreeLibrary.
        Step("HeartbeatLogger.StopAndJoin", () => Compatibility.HeartbeatLogger.StopAndJoin());

        // Every other engine thread (init, pumps, login helpers, D3D9 bootstrap...).
        // Their sleeps wake at once; a thread still running engine code afterwards
        // keeps this generation from unloading under the CoreCLR host.
        Step("EngineThreads.StopAndJoinAll", () => EngineThreads.StopAndJoinAll(3000));

        // Process-wide events: under CoreCLR they would hold this generation.
        Step("EntryPoint.UninstallManagedExceptionHandlers", EntryPoint.UninstallManagedExceptionHandlers);

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

        // Managed plugins' contexts go last: nothing in the engine may call into them
        // after this (every thread is joined, the hooks are off).
        Step("ManagedPlugins.UnloadRetired", ManagedPlugins.UnloadRetired);

        Volatile.Write(ref _hasShutDown, 1);
        RynthLog.Info("EngineLifecycle: Shutdown complete.");
        EntryPoint.CloseLogFile();   // the log's open handle (EntryPoint.AppendWithRetry)
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
