// ============================================================================
//  RynthCore.Engine - Plugins/PluginManager.UiHooks.cs
//
//  API v77 (2026-10-05): the plugin side of Compatibility/UiFlowHooks and
//  Compatibility/UiElementHooks.
//
//  Callbacks (optional plugin exports, cdecl; looked up here on first use, not by
//  PluginLoader):
//    RynthPluginOnScreenChanged(int oldMode, int newMode)
//    RynthPluginOnClientCleanup()
//    RynthPluginOnTooltipShow(uint objectId, uint spellId)
//    RynthPluginOnTooltipHide()
//    RynthPluginOnDragStart(uint objectId, uint spellId, uint iconId)
//    RynthPluginOnItemDropped(uint objectId, uint spellId, uint targetElementId)
//  The hooks queue them from AC's main thread; ProcessPendingActions delivers them
//  on the plugin pump in the order they happened (one queue), like every other
//  event. OnClientCleanup is the exception: it must arrive while AC's UI still
//  stands, so the Client::Cleanup detour (AC's main thread) waits, bounded, for the
//  pump to deliver it, and delivers it itself (under the pump's frame guard) if the
//  pump doesn't come. After it the pump delivers no more events and TickAll stops
//  ticking. Plugins: keep OnClientCleanup short and never wait on AC's main thread
//  inside it (it may be running on it, or waiting for you).
//
//  Table functions: GetScreenModeFn, GetUiHookFlagsFn (PluginContract.cs).
//  Older engines have neither the table fields nor the callbacks: a plugin checks
//  RynthCoreAPI.Version >= 77 before reading the fields (RynthCoreHost.HasUiHooks).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using RynthCore.Engine.Compatibility;

namespace RynthCore.Engine.Plugins;

internal static partial class PluginManager
{
    private static GetScreenModeCallbackDelegate? _getScreenModeCallback;     // v77
    private static GetUiHookFlagsCallbackDelegate? _getUiHookFlagsCallback;   // v77

    /// <summary>The thread that ran TickAll last (the plugin pump).</summary>
    private static int _tickThread;

    private enum UiEventKind : byte { ScreenChanged, TooltipShow, TooltipHide, DragStart, ItemDropped }
    private readonly record struct PendingUiEvent(UiEventKind Kind, uint A, uint B, uint C);

    private const int MaxPendingUiEvents = 256;
    private static readonly Queue<PendingUiEvent> _pendingUiEvents = new();
    private static readonly object PendingUiEventsLock = new();

    // Client::Cleanup: 0 nothing, 1 waiting for the pump, 2 delivered (or nobody to tell).
    private static int _cleanupState;

    // ── Queueing (AC's main thread) ──────────────────────────────────────

    public static void QueueScreenChanged(int oldMode, int newMode) => QueueUiEvent(UiEventKind.ScreenChanged, (uint)oldMode, (uint)newMode, 0);
    public static void QueueTooltipShown(uint objectId, uint spellId) => QueueUiEvent(UiEventKind.TooltipShow, objectId, spellId, 0);
    public static void QueueTooltipHidden() => QueueUiEvent(UiEventKind.TooltipHide, 0, 0, 0);
    public static void QueueDragStarted(uint objectId, uint spellId, uint iconId) => QueueUiEvent(UiEventKind.DragStart, objectId, spellId, iconId);
    public static void QueueItemDropped(uint objectId, uint spellId, uint targetElementId) => QueueUiEvent(UiEventKind.ItemDropped, objectId, spellId, targetElementId);

    private static void QueueUiEvent(UiEventKind kind, uint a, uint b, uint c)
    {
        if (!_initialized || _plugins.Count == 0 || Volatile.Read(ref _cleanupState) != 0)
            return;
        lock (PendingUiEventsLock)
        {
            if (_pendingUiEvents.Count >= MaxPendingUiEvents)
                _pendingUiEvents.Dequeue();
            _pendingUiEvents.Enqueue(new PendingUiEvent(kind, a, b, c));
        }
    }

    // ── Delivery (plugin pump) ───────────────────────────────────────────

    private static void ResolveUiExports(LoadedPlugin plugin)
    {
        if (plugin.UiExportsResolved || plugin.ModuleHandle == IntPtr.Zero)
            return;
        plugin.UiExportsResolved = true;
        plugin.OnScreenChangedPtr = GetProcAddress(plugin.ModuleHandle, "RynthPluginOnScreenChanged");
        plugin.OnClientCleanupPtr = GetProcAddress(plugin.ModuleHandle, "RynthPluginOnClientCleanup");
        plugin.OnTooltipShowPtr = GetProcAddress(plugin.ModuleHandle, "RynthPluginOnTooltipShow");
        plugin.OnTooltipHidePtr = GetProcAddress(plugin.ModuleHandle, "RynthPluginOnTooltipHide");
        plugin.OnDragStartPtr = GetProcAddress(plugin.ModuleHandle, "RynthPluginOnDragStart");
        plugin.OnItemDroppedPtr = GetProcAddress(plugin.ModuleHandle, "RynthPluginOnItemDropped");
        int count = (plugin.OnScreenChangedPtr != IntPtr.Zero ? 1 : 0) + (plugin.OnClientCleanupPtr != IntPtr.Zero ? 1 : 0)
                  + (plugin.OnTooltipShowPtr != IntPtr.Zero ? 1 : 0) + (plugin.OnTooltipHidePtr != IntPtr.Zero ? 1 : 0)
                  + (plugin.OnDragStartPtr != IntPtr.Zero ? 1 : 0) + (plugin.OnItemDroppedPtr != IntPtr.Zero ? 1 : 0);
        if (count > 0)
            RynthLog.Plugin($"PluginManager: {plugin.DisplayName} takes {count} v77 UI callback(s).");
    }

    private static unsafe void DispatchQueuedUiEvents()
    {
        if (!_initialized || _plugins.Count == 0)
            return;

        PendingUiEvent[] pending;
        lock (PendingUiEventsLock)
        {
            if (_pendingUiEvents.Count == 0)
                return;
            pending = _pendingUiEvents.ToArray();
            _pendingUiEvents.Clear();
        }

        foreach (PendingUiEvent evt in pending)
        {
            for (int i = 0; i < _plugins.Count; i++)
            {
                var plugin = _plugins[i];
                if (!plugin.Initialized || plugin.Failed)
                    continue;
                ResolveUiExports(plugin);
                IntPtr fn = evt.Kind switch
                {
                    UiEventKind.ScreenChanged => plugin.OnScreenChangedPtr,
                    UiEventKind.TooltipShow => plugin.OnTooltipShowPtr,
                    UiEventKind.TooltipHide => plugin.OnTooltipHidePtr,
                    UiEventKind.DragStart => plugin.OnDragStartPtr,
                    _ => plugin.OnItemDroppedPtr,
                };
                if (fn == IntPtr.Zero)
                    continue;

                LoadedPlugin? outer = EnterDispatch(plugin);
                try
                {
                    switch (evt.Kind)
                    {
                        case UiEventKind.ScreenChanged:
                            ((delegate* unmanaged[Cdecl]<int, int, void>)fn)((int)evt.A, (int)evt.B);
                            break;
                        case UiEventKind.TooltipShow:
                            ((delegate* unmanaged[Cdecl]<uint, uint, void>)fn)(evt.A, evt.B);
                            break;
                        case UiEventKind.TooltipHide:
                            ((delegate* unmanaged[Cdecl]<void>)fn)();
                            break;
                        case UiEventKind.DragStart:
                        case UiEventKind.ItemDropped:
                            ((delegate* unmanaged[Cdecl]<uint, uint, uint, void>)fn)(evt.A, evt.B, evt.C);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    plugin.Failed = true;
                    RynthLog.Plugin($"PluginManager: {plugin.DisplayName} On{evt.Kind} threw {ex.GetType().Name}: {ex.Message}{PluginTrace(ex)}");
                }
                finally
                {
                    LeaveDispatch(outer);
                }
            }
        }
    }

    // ── Client::Cleanup ──────────────────────────────────────────────────

    /// <summary>
    /// AC's main thread, inside the Client::Cleanup detour before AC tears anything down.
    /// Plugins are called from the plugin pump (a managed thread: a NativeAOT engine must not
    /// run plugin code on AC's threads, the 2026-05-16 fail-fast), never from two threads at
    /// once. So: the pump delivers RynthPluginOnClientCleanup at the top of its next frame
    /// (ProcessClientCleanup, every ~16 ms) while this waits for up to half of
    /// <paramref name="timeoutMs"/>. A pump that doesn't come (no frames: the device is
    /// gone) leaves it to this thread, which delivers it holding the pump's frame guard
    /// (EngineFrameController.TryRunOutsidePumpFrame). Exactly once either way; after the
    /// full timeout AC's cleanup goes ahead regardless.
    /// </summary>
    public static void DispatchClientCleanup(int timeoutMs)
    {
        if (!_initialized || _plugins.Count == 0)
        {
            Volatile.Write(ref _cleanupState, 2);
            return;
        }
        if (Interlocked.CompareExchange(ref _cleanupState, 1, 0) != 0)
            return;

        int pump = Volatile.Read(ref _tickThread);
        if (pump == 0 || pump == Environment.CurrentManagedThreadId)
        {
            TakeAndDeliverClientCleanup("the calling thread (no separate pump)");
            return;
        }

        long start = Environment.TickCount64;
        long pumpUntil = start + timeoutMs / 2, until = start + timeoutMs;
        while (Volatile.Read(ref _cleanupState) != 2)
        {
            long now = Environment.TickCount64;
            if (now >= pumpUntil
                && ImGuiBackend.EngineFrameController.TryRunOutsidePumpFrame(() => TakeAndDeliverClientCleanup("AC's main thread (the pump didn't come)")))
                return;
            if (now >= until)
            {
                RynthLog.Plugin($"PluginManager: OnClientCleanup not delivered within {timeoutMs} ms (a pump frame is running); AC's cleanup goes ahead, the pump delivers it at its next frame.");
                return;
            }
            Thread.Sleep(5);
        }
    }

    /// <summary>
    /// Plugin pump, top of ProcessPendingActions. True once Client::Cleanup started: the
    /// rest of the frame's events are dropped (AC is tearing down what they describe).
    /// </summary>
    private static bool ProcessClientCleanup()
    {
        if (Volatile.Read(ref _cleanupState) == 0)
            return UiFlowHooks.ClientCleanupStarted;
        TakeAndDeliverClientCleanup("the plugin pump");
        return true;
    }

    // 1 -> 2 exactly once, whoever gets there first.
    private static void TakeAndDeliverClientCleanup(string where)
    {
        if (Interlocked.CompareExchange(ref _cleanupState, 2, 1) != 1)
            return;
        DeliverClientCleanup(where);
    }

    private static unsafe void DeliverClientCleanup(string where)
    {
        lock (PendingUiEventsLock)
            _pendingUiEvents.Clear();
        int told = 0;
        for (int i = 0; i < _plugins.Count; i++)
        {
            var plugin = _plugins[i];
            if (!plugin.Initialized || plugin.Failed)
                continue;
            ResolveUiExports(plugin);
            if (plugin.OnClientCleanupPtr == IntPtr.Zero)
                continue;
            LoadedPlugin? outer = EnterDispatch(plugin);
            try
            {
                ((delegate* unmanaged[Cdecl]<void>)plugin.OnClientCleanupPtr)();
                told++;
            }
            catch (Exception ex)
            {
                plugin.Failed = true;
                RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnClientCleanup threw {ex.GetType().Name}: {ex.Message}{PluginTrace(ex)}");
            }
            finally
            {
                LeaveDispatch(outer);
            }
        }
        RynthLog.Plugin($"PluginManager: OnClientCleanup delivered to {told} plugin(s) from {where}; no more plugin ticks or events.");
    }

    // ── Table functions ──────────────────────────────────────────────────

    /// <summary>v77: see PluginContract.GetScreenModeFn.</summary>
    private static unsafe int GetScreenModeAction(int* previousMode)
    {
        if (previousMode != null)
            *previousMode = UiFlowHooks.PreviousMode;
        return UiFlowHooks.CurrentMode;
    }

    /// <summary>v77: see PluginContract.GetUiHookFlagsFn.</summary>
    private static uint GetUiHookFlagsAction()
    {
        uint flags = 0;
        if (UiFlowHooks.ScreenHookLive) flags |= 1u << 0;
        else flags |= 1u << 1;   // the +0x8C poll drives OnScreenChanged
        if (UiFlowHooks.CleanupHookLive) flags |= 1u << 2;
        if (UiElementHooks.TooltipShowLive) flags |= 1u << 3;
        if (UiElementHooks.TooltipHideLive) flags |= 1u << 4;
        if (UiElementHooks.DragStartLive) flags |= 1u << 5;
        if (UiElementHooks.DropLive) flags |= 1u << 6;
        if (UiFlowHooks.ClientCleanupStarted) flags |= 1u << 7;
        return flags;
    }
}
