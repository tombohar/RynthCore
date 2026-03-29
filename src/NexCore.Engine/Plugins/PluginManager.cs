// ============================================================================
//  NexCore.Engine - Plugins/PluginManager.cs
//  Orchestrates plugin lifecycle: load, init, login-ready callbacks, tick,
//  render, rescan, and shutdown.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using NexCore.Engine.Compatibility;

namespace NexCore.Engine.Plugins;

internal static class PluginManager
{
    private readonly record struct PendingIncomingChat(string? Text, uint ChatType);
    private readonly record struct PendingTargetChange(uint CurrentTargetId, uint PreviousTargetId);
    private readonly record struct PendingSmartBoxEvent(uint Opcode, uint BlobSize, uint Status);
    private readonly record struct PendingCreateObject(uint ObjectId);
    private readonly record struct PendingDeleteObject(uint ObjectId);
    private readonly record struct PendingUpdateObject(uint ObjectId);
    private readonly record struct PendingUpdateObjectInventory(uint ObjectId);
    private readonly record struct PendingViewObjectContents(uint ObjectId);
    private readonly record struct PendingStopViewingObjectContents(uint ObjectId);

    private const int MaxPendingIncomingChats = 256;
    private const int MaxPendingTargetChanges = 128;
    private const int MaxPendingSmartBoxEvents = 1024;
    private const int MaxPendingCreateObjects = 128;
    private const int MaxPendingDeleteObjects = 128;
    private const int MaxPendingUpdateObjects = 512;
    private const int MaxDispatchedUpdateObjectsPerFrame = 64;
    private const int MaxPendingUpdateObjectInventory = 128;
    private const int MaxPendingViewObjectContents = 128;
    private const int MaxPendingStopViewingObjectContents = 128;
    private static readonly List<LoadedPlugin> _plugins = new();
    private static readonly Queue<PendingIncomingChat> _pendingIncomingChats = new();
    private static readonly Queue<PendingTargetChange> _pendingTargetChanges = new();
    private static readonly Queue<PendingSmartBoxEvent> _pendingSmartBoxEvents = new();
    private static readonly Queue<PendingCreateObject> _pendingCreateObjects = new();
    private static readonly Queue<PendingDeleteObject> _pendingDeleteObjects = new();
    private static readonly Queue<PendingUpdateObject> _pendingUpdateObjects = new();
    private static readonly HashSet<uint> _pendingUpdateObjectIds = new();
    private static readonly Queue<PendingUpdateObjectInventory> _pendingUpdateObjectInventory = new();
    private static readonly Queue<PendingViewObjectContents> _pendingViewObjectContents = new();
    private static readonly Queue<PendingStopViewingObjectContents> _pendingStopViewingObjectContents = new();
    private static readonly object PendingIncomingChatsLock = new();
    private static readonly object PendingTargetChangesLock = new();
    private static readonly object PendingSmartBoxEventsLock = new();
    private static readonly object PendingCreateObjectsLock = new();
    private static readonly object PendingDeleteObjectsLock = new();
    private static readonly object PendingUpdateObjectsLock = new();
    private static readonly object PendingUpdateObjectInventoryLock = new();
    private static readonly object PendingViewObjectContentsLock = new();
    private static readonly object PendingStopViewingObjectContentsLock = new();
    private static bool _loaded;
    private static bool _initialized;
    private static bool _rescanRequested;
    private static bool _uiInitializedObserved;
    private static bool _uiDispatchPending;
    private static bool _loginCompleteObserved;
    private static bool _loginDispatchPending;
    private static int _loadGeneration;
    private static NexCoreAPI _api;
    private static LogCallbackDelegate? _logCallback;
    private static ProbeClientHooksCallbackDelegate? _probeClientHooksCallback;
    private static GetClientHookFlagsCallbackDelegate? _getClientHookFlagsCallback;
    private static ChangeCombatModeCallbackDelegate? _changeCombatModeCallback;
    private static CancelAttackCallbackDelegate? _cancelAttackCallback;
    private static QueryHealthCallbackDelegate? _queryHealthCallback;
    private static MeleeAttackCallbackDelegate? _meleeAttackCallback;
    private static MissileAttackCallbackDelegate? _missileAttackCallback;
    private static DoMovementCallbackDelegate? _doMovementCallback;
    private static StopMovementCallbackDelegate? _stopMovementCallback;
    private static JumpNonAutonomousCallbackDelegate? _jumpNonAutonomousCallback;
    private static SetAutonomyLevelCallbackDelegate? _setAutonomyLevelCallback;
    private static SetAutoRunCallbackDelegate? _setAutoRunCallback;
    private static TapJumpCallbackDelegate? _tapJumpCallback;
    private static SetIncomingChatSuppressionCallbackDelegate? _setIncomingChatSuppressionCallback;
    private static string _pluginsDir = "";
    private static string _shadowRootDir = "";

    public static IReadOnlyList<LoadedPlugin> Plugins => _plugins;
    public static bool IsRescanQueued => _rescanRequested;
    public static string PluginDirectory => _pluginsDir;
    public static bool HasObservedUIInitialized => _uiInitializedObserved;
    public static bool HasObservedLoginComplete => _loginCompleteObserved;

    public static void LoadPlugins(string engineDir)
    {
        if (_loaded)
            return;

        _loaded = true;
        UiLifecycleHooks.UiInitialized -= OnUIInitializedObserved;
        UiLifecycleHooks.UiInitialized += OnUIInitializedObserved;
        _uiInitializedObserved = UiLifecycleHooks.HasObservedUiInitialized;
        _uiDispatchPending = _uiInitializedObserved;

        LoginLifecycleHooks.LoginComplete -= OnLoginCompleteObserved;
        LoginLifecycleHooks.LoginComplete += OnLoginCompleteObserved;
        _loginCompleteObserved = LoginLifecycleHooks.HasObservedLoginComplete;
        _loginDispatchPending = _loginCompleteObserved;

        _pluginsDir = Path.Combine(engineDir, "Plugins");
        _shadowRootDir = Path.Combine(_pluginsDir, ".runtime");

        PluginLoader.CleanupShadowCopies(_shadowRootDir);
        LoadPluginsFromDisk();
    }

    public static void InitPlugins(IntPtr imguiContext, IntPtr d3dDevice, IntPtr gameHwnd)
    {
        if (_initialized)
            return;

        _initialized = true;
        EnsureHostCallbacks();
        _api.ImGuiContext = imguiContext;
        _api.D3DDevice = d3dDevice;
        _api.GameHwnd = gameHwnd;

        InitializeLoadedPlugins();
        DispatchUIInitializedToLoadedPlugins();
        DispatchLoginCompleteToLoadedPlugins();
    }

    public static void RequestRescan()
    {
        if (string.IsNullOrWhiteSpace(_pluginsDir))
            return;

        _rescanRequested = true;
    }

    public static void ProcessPendingActions(IntPtr imguiContext, IntPtr d3dDevice, IntPtr gameHwnd)
    {
        UiLifecycleHooks.Poll();
        LoginLifecycleHooks.Poll();
        DispatchQueuedSmartBoxEvent();
        DispatchQueuedStopViewingObjectContents();
        DispatchQueuedViewObjectContents();
        DispatchQueuedUpdateObjectInventory();
        DispatchQueuedCreateObject();
        DispatchQueuedUpdateObject();
        DispatchQueuedDeleteObject();
        DispatchQueuedSelectedTargetChange();
        DispatchQueuedChatWindowText();

        if (_rescanRequested)
        {
            _rescanRequested = false;
            RescanPlugins(imguiContext, d3dDevice, gameHwnd);
        }

        if (_uiDispatchPending && _initialized)
            DispatchUIInitializedToLoadedPlugins();

        if (_loginDispatchPending && _initialized)
            DispatchLoginCompleteToLoadedPlugins();
    }

    public static bool DispatchChatBarEnter(string? text)
    {
        if (!_initialized || _plugins.Count == 0)
            return false;

        IntPtr textPtr = text != null ? Marshal.StringToHGlobalUni(text) : IntPtr.Zero;
        try
        {
            unsafe
            {
                int eat = 0;
                IntPtr eatPtr = new(&eat);

                foreach (var plugin in _plugins)
                {
                    if (!plugin.Initialized || plugin.Failed || plugin.OnChatBarEnter == null)
                        continue;

                    try
                    {
                        plugin.OnChatBarEnter(textPtr, eatPtr);
                    }
                    catch (Exception ex)
                    {
                        plugin.Failed = true;
                        EntryPoint.Log($"PluginManager: {plugin.DisplayName} OnChatBarEnter threw {ex.GetType().Name}: {ex.Message}");
                    }
                }

                return eat != 0;
            }
        }
        finally
        {
            if (textPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(textPtr);
        }
    }

    public static void QueueChatWindowText(string? text, uint chatType)
    {
        if (!_initialized || _plugins.Count == 0)
            return;

        lock (PendingIncomingChatsLock)
        {
            if (_pendingIncomingChats.Count >= MaxPendingIncomingChats)
                _pendingIncomingChats.Dequeue();

            _pendingIncomingChats.Enqueue(new PendingIncomingChat(text, chatType));
        }
    }

    public static void QueueSelectedTargetChange(uint currentTargetId, uint previousTargetId)
    {
        if (!_initialized || _plugins.Count == 0)
            return;

        lock (PendingTargetChangesLock)
        {
            if (_pendingTargetChanges.Count >= MaxPendingTargetChanges)
                _pendingTargetChanges.Dequeue();

            _pendingTargetChanges.Enqueue(new PendingTargetChange(currentTargetId, previousTargetId));
        }
    }

    public static void QueueSmartBoxEvent(uint opcode, uint blobSize, uint status)
    {
        if (!_initialized || _plugins.Count == 0)
            return;

        lock (PendingSmartBoxEventsLock)
        {
            if (_pendingSmartBoxEvents.Count >= MaxPendingSmartBoxEvents)
                _pendingSmartBoxEvents.Dequeue();

            _pendingSmartBoxEvents.Enqueue(new PendingSmartBoxEvent(opcode, blobSize, status));
        }
    }

    public static void QueueDeleteObject(uint objectId)
    {
        if (!_initialized || _plugins.Count == 0)
            return;

        lock (PendingDeleteObjectsLock)
        {
            if (_pendingDeleteObjects.Count >= MaxPendingDeleteObjects)
                _pendingDeleteObjects.Dequeue();

            _pendingDeleteObjects.Enqueue(new PendingDeleteObject(objectId));
        }
    }

    public static void QueueUpdateObject(uint objectId)
    {
        if (!_initialized || _plugins.Count == 0 || objectId == 0)
            return;

        lock (PendingUpdateObjectsLock)
        {
            if (!_pendingUpdateObjectIds.Add(objectId))
                return;

            if (_pendingUpdateObjects.Count >= MaxPendingUpdateObjects)
            {
                PendingUpdateObject dropped = _pendingUpdateObjects.Dequeue();
                _pendingUpdateObjectIds.Remove(dropped.ObjectId);
            }

            _pendingUpdateObjects.Enqueue(new PendingUpdateObject(objectId));
        }
    }

    public static void QueueCreateObject(uint objectId)
    {
        if (!_initialized || _plugins.Count == 0)
            return;

        lock (PendingCreateObjectsLock)
        {
            if (_pendingCreateObjects.Count >= MaxPendingCreateObjects)
                _pendingCreateObjects.Dequeue();

            _pendingCreateObjects.Enqueue(new PendingCreateObject(objectId));
        }
    }

    public static void QueueUpdateObjectInventory(uint objectId)
    {
        if (!_initialized || _plugins.Count == 0)
            return;

        lock (PendingUpdateObjectInventoryLock)
        {
            if (_pendingUpdateObjectInventory.Count >= MaxPendingUpdateObjectInventory)
                _pendingUpdateObjectInventory.Dequeue();

            _pendingUpdateObjectInventory.Enqueue(new PendingUpdateObjectInventory(objectId));
        }
    }

    public static void QueueViewObjectContents(uint objectId)
    {
        if (!_initialized || _plugins.Count == 0)
            return;

        lock (PendingViewObjectContentsLock)
        {
            if (_pendingViewObjectContents.Count >= MaxPendingViewObjectContents)
                _pendingViewObjectContents.Dequeue();

            _pendingViewObjectContents.Enqueue(new PendingViewObjectContents(objectId));
        }
    }

    public static void QueueStopViewingObjectContents(uint objectId)
    {
        if (!_initialized || _plugins.Count == 0)
            return;

        lock (PendingStopViewingObjectContentsLock)
        {
            if (_pendingStopViewingObjectContents.Count >= MaxPendingStopViewingObjectContents)
                _pendingStopViewingObjectContents.Dequeue();

            _pendingStopViewingObjectContents.Enqueue(new PendingStopViewingObjectContents(objectId));
        }
    }

    public static bool DispatchChatWindowTextImmediate(string? text, int chatType)
    {
        if (!_initialized || _plugins.Count == 0)
            return false;

        IntPtr textPtr = text != null ? Marshal.StringToHGlobalUni(text) : IntPtr.Zero;
        try
        {
            unsafe
            {
                int eat = 0;
                IntPtr eatPtr = new(&eat);

                foreach (var plugin in _plugins)
                {
                    if (!plugin.Initialized || plugin.Failed)
                        continue;

                    try
                    {
                        if (plugin.OnChatWindowTextPtr != IntPtr.Zero)
                        {
                            ((delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr, void>)plugin.OnChatWindowTextPtr)(textPtr, chatType, eatPtr);
                        }
                        else if (plugin.OnChatWindowText != null)
                        {
                            plugin.OnChatWindowText(textPtr, chatType, eatPtr);
                        }
                    }
                    catch (Exception ex)
                    {
                        plugin.Failed = true;
                        EntryPoint.Log($"PluginManager: {plugin.DisplayName} OnChatWindowText threw {ex.GetType().Name}: {ex.Message}");
                    }
                }

                return eat != 0;
            }
        }
        finally
        {
            if (textPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(textPtr);
        }
    }

    public static void UpdateDevice(IntPtr d3dDevice)
    {
        _api.D3DDevice = d3dDevice;
    }

    public static void TickAll()
    {
        for (int i = 0; i < _plugins.Count; i++)
        {
            var plugin = _plugins[i];
            if (!plugin.Initialized || plugin.Failed || plugin.Tick == null)
                continue;

            try
            {
                plugin.Tick();
            }
            catch (Exception ex)
            {
                plugin.Failed = true;
                EntryPoint.Log($"PluginManager: {plugin.DisplayName} Tick threw {ex.GetType().Name}: {ex.Message} - disabled.");
            }
        }
    }

    public static void RenderAll()
    {
        for (int i = 0; i < _plugins.Count; i++)
        {
            var plugin = _plugins[i];
            if (!plugin.Initialized || plugin.Failed || plugin.Render == null)
                continue;

            try
            {
                plugin.Render();
            }
            catch (Exception ex)
            {
                plugin.Failed = true;
                EntryPoint.Log($"PluginManager: {plugin.DisplayName} Render threw {ex.GetType().Name}: {ex.Message} - disabled.");
            }
        }
    }

    public static void ShutdownAll()
    {
        EntryPoint.Log($"PluginManager: Shutting down {_plugins.Count} plugin(s)...");
        UiLifecycleHooks.UiInitialized -= OnUIInitializedObserved;
        LoginLifecycleHooks.LoginComplete -= OnLoginCompleteObserved;
        UnloadAllPlugins();
        PluginLoader.CleanupShadowCopies(_shadowRootDir);
        _initialized = false;
        _loaded = false;
        _rescanRequested = false;
        _uiDispatchPending = false;
        _uiInitializedObserved = false;
        _loginDispatchPending = false;
        _loginCompleteObserved = false;
        _pluginsDir = "";
        _shadowRootDir = "";

        lock (PendingIncomingChatsLock)
            _pendingIncomingChats.Clear();
        lock (PendingTargetChangesLock)
            _pendingTargetChanges.Clear();
        lock (PendingSmartBoxEventsLock)
            _pendingSmartBoxEvents.Clear();
        lock (PendingCreateObjectsLock)
            _pendingCreateObjects.Clear();
        lock (PendingDeleteObjectsLock)
            _pendingDeleteObjects.Clear();
        lock (PendingUpdateObjectsLock)
        {
            _pendingUpdateObjects.Clear();
            _pendingUpdateObjectIds.Clear();
        }
        lock (PendingUpdateObjectInventoryLock)
            _pendingUpdateObjectInventory.Clear();
        lock (PendingViewObjectContentsLock)
            _pendingViewObjectContents.Clear();
        lock (PendingStopViewingObjectContentsLock)
            _pendingStopViewingObjectContents.Clear();
    }

    private static void LogFromPlugin(IntPtr messageUtf8)
    {
        string? msg = Marshal.PtrToStringAnsi(messageUtf8);
        if (msg != null)
            EntryPoint.Log($"[Plugin] {msg}");
    }

    private static void OnUIInitializedObserved()
    {
        if (_uiInitializedObserved)
            return;

        _uiInitializedObserved = true;
        _uiDispatchPending = true;
        EntryPoint.Log("PluginManager: OnUIInitialized observed - queued UI lifecycle callbacks.");
    }

    private static void OnLoginCompleteObserved()
    {
        if (_loginCompleteObserved)
            return;

        _loginCompleteObserved = true;
        _loginDispatchPending = true;
        EntryPoint.Log("PluginManager: OnLoginComplete observed - queued login lifecycle callbacks.");
    }

    private static void RescanPlugins(IntPtr imguiContext, IntPtr d3dDevice, IntPtr gameHwnd)
    {
        if (string.IsNullOrWhiteSpace(_pluginsDir))
        {
            EntryPoint.Log("PluginManager: Rescan requested before plugin directories were configured.");
            return;
        }

        EntryPoint.Log("PluginManager: Rescanning plugins...");

        UnloadAllPlugins();
        PluginLoader.CleanupShadowCopies(_shadowRootDir);
        LoadPluginsFromDisk();

        _api.ImGuiContext = imguiContext;
        _api.D3DDevice = d3dDevice;
        _api.GameHwnd = gameHwnd;

        InitializeLoadedPlugins();
        DispatchUIInitializedToLoadedPlugins();
        DispatchLoginCompleteToLoadedPlugins();
    }

    private static void DispatchQueuedChatWindowText()
    {
        if (!_initialized || _plugins.Count == 0)
            return;

        PendingIncomingChat[] pending;
        lock (PendingIncomingChatsLock)
        {
            if (_pendingIncomingChats.Count == 0)
                return;

            pending = _pendingIncomingChats.ToArray();
            _pendingIncomingChats.Clear();
        }

        unsafe
        {
            foreach (PendingIncomingChat evt in pending)
            {
                IntPtr textPtr = evt.Text != null ? Marshal.StringToHGlobalUni(evt.Text) : IntPtr.Zero;
                try
                {
                    int eat = 0;
                    IntPtr eatPtr = new(&eat);

                    foreach (var plugin in _plugins)
                    {
                        if (!plugin.Initialized || plugin.Failed)
                            continue;

                        try
                        {
                            if (plugin.OnChatWindowTextPtr != IntPtr.Zero)
                            {
                                ((delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr, void>)plugin.OnChatWindowTextPtr)(textPtr, unchecked((int)evt.ChatType), eatPtr);
                            }
                            else if (plugin.OnChatWindowText != null)
                            {
                                plugin.OnChatWindowText(textPtr, unchecked((int)evt.ChatType), eatPtr);
                            }
                        }
                        catch (Exception ex)
                        {
                            plugin.Failed = true;
                            EntryPoint.Log($"PluginManager: {plugin.DisplayName} OnChatWindowText threw {ex.GetType().Name}: {ex.Message}");
                        }
                    }
                }
                finally
                {
                    if (textPtr != IntPtr.Zero)
                        Marshal.FreeHGlobal(textPtr);
                }
            }
        }
    }

    private static void DispatchQueuedSelectedTargetChange()
    {
        if (!_initialized || _plugins.Count == 0)
            return;

        PendingTargetChange[] pending;
        lock (PendingTargetChangesLock)
        {
            if (_pendingTargetChanges.Count == 0)
                return;

            pending = _pendingTargetChanges.ToArray();
            _pendingTargetChanges.Clear();
        }

        foreach (PendingTargetChange evt in pending)
        {
            foreach (var plugin in _plugins)
            {
                if (!plugin.Initialized || plugin.Failed || plugin.OnSelectedTargetChange == null)
                    continue;

                try
                {
                    plugin.OnSelectedTargetChange(evt.CurrentTargetId, evt.PreviousTargetId);
                }
                catch (Exception ex)
                {
                    plugin.Failed = true;
                    EntryPoint.Log($"PluginManager: {plugin.DisplayName} OnSelectedTargetChange threw {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    private static void DispatchQueuedSmartBoxEvent()
    {
        if (!_initialized || _plugins.Count == 0)
            return;

        PendingSmartBoxEvent[] pending;
        lock (PendingSmartBoxEventsLock)
        {
            if (_pendingSmartBoxEvents.Count == 0)
                return;

            pending = _pendingSmartBoxEvents.ToArray();
            _pendingSmartBoxEvents.Clear();
        }

        foreach (PendingSmartBoxEvent evt in pending)
        {
            foreach (var plugin in _plugins)
            {
                if (!plugin.Initialized || plugin.Failed)
                    continue;

                try
                {
                    unsafe
                    {
                        if (plugin.OnSmartBoxEventPtr != IntPtr.Zero)
                        {
                            ((delegate* unmanaged[Cdecl]<uint, uint, uint, void>)plugin.OnSmartBoxEventPtr)(evt.Opcode, evt.BlobSize, evt.Status);
                        }
                        else if (plugin.OnSmartBoxEvent != null)
                        {
                            plugin.OnSmartBoxEvent(evt.Opcode, evt.BlobSize, evt.Status);
                        }
                        else
                        {
                            continue;
                        }
                    }
                }
                catch (Exception ex)
                {
                    plugin.Failed = true;
                    EntryPoint.Log($"PluginManager: {plugin.DisplayName} OnSmartBoxEvent threw {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    private static void DispatchQueuedUpdateObject()
    {
        if (!_initialized || _plugins.Count == 0)
            return;

        PendingUpdateObject[] pending;
        lock (PendingUpdateObjectsLock)
        {
            if (_pendingUpdateObjects.Count == 0)
                return;

            int dispatchCount = Math.Min(_pendingUpdateObjects.Count, MaxDispatchedUpdateObjectsPerFrame);
            pending = new PendingUpdateObject[dispatchCount];

            for (int i = 0; i < dispatchCount; i++)
            {
                PendingUpdateObject evt = _pendingUpdateObjects.Dequeue();
                _pendingUpdateObjectIds.Remove(evt.ObjectId);
                pending[i] = evt;
            }
        }

        foreach (PendingUpdateObject evt in pending)
        {
            foreach (var plugin in _plugins)
            {
                if (!plugin.Initialized || plugin.Failed)
                    continue;

                try
                {
                    unsafe
                    {
                        if (plugin.OnUpdateObjectPtr != IntPtr.Zero)
                        {
                            ((delegate* unmanaged[Cdecl]<uint, void>)plugin.OnUpdateObjectPtr)(evt.ObjectId);
                        }
                        else if (plugin.OnUpdateObject != null)
                        {
                            plugin.OnUpdateObject(evt.ObjectId);
                        }
                        else
                        {
                            continue;
                        }
                    }
                }
                catch (Exception ex)
                {
                    plugin.Failed = true;
                    EntryPoint.Log($"PluginManager: {plugin.DisplayName} OnUpdateObject threw {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    private static void DispatchQueuedDeleteObject()
    {
        if (!_initialized || _plugins.Count == 0)
            return;

        PendingDeleteObject[] pending;
        lock (PendingDeleteObjectsLock)
        {
            if (_pendingDeleteObjects.Count == 0)
                return;

            pending = _pendingDeleteObjects.ToArray();
            _pendingDeleteObjects.Clear();
        }

        foreach (PendingDeleteObject evt in pending)
        {
            foreach (var plugin in _plugins)
            {
                if (!plugin.Initialized || plugin.Failed || plugin.OnDeleteObject == null)
                    continue;

                try
                {
                    plugin.OnDeleteObject(evt.ObjectId);
                }
                catch (Exception ex)
                {
                    plugin.Failed = true;
                    EntryPoint.Log($"PluginManager: {plugin.DisplayName} OnDeleteObject threw {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    private static void DispatchQueuedCreateObject()
    {
        if (!_initialized || _plugins.Count == 0)
            return;

        PendingCreateObject[] pending;
        lock (PendingCreateObjectsLock)
        {
            if (_pendingCreateObjects.Count == 0)
                return;

            pending = _pendingCreateObjects.ToArray();
            _pendingCreateObjects.Clear();
        }

        foreach (PendingCreateObject evt in pending)
        {
            foreach (var plugin in _plugins)
            {
                if (!plugin.Initialized || plugin.Failed || plugin.OnCreateObject == null)
                    continue;

                try
                {
                    plugin.OnCreateObject(evt.ObjectId);
                }
                catch (Exception ex)
                {
                    plugin.Failed = true;
                    EntryPoint.Log($"PluginManager: {plugin.DisplayName} OnCreateObject threw {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    private static void DispatchQueuedUpdateObjectInventory()
    {
        if (!_initialized || _plugins.Count == 0)
            return;

        PendingUpdateObjectInventory[] pending;
        lock (PendingUpdateObjectInventoryLock)
        {
            if (_pendingUpdateObjectInventory.Count == 0)
                return;

            pending = _pendingUpdateObjectInventory.ToArray();
            _pendingUpdateObjectInventory.Clear();
        }

        foreach (PendingUpdateObjectInventory evt in pending)
        {
            foreach (var plugin in _plugins)
            {
                if (!plugin.Initialized || plugin.Failed || plugin.OnUpdateObjectInventory == null)
                    continue;

                try
                {
                    plugin.OnUpdateObjectInventory(evt.ObjectId);
                }
                catch (Exception ex)
                {
                    plugin.Failed = true;
                    EntryPoint.Log($"PluginManager: {plugin.DisplayName} OnUpdateObjectInventory threw {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    private static void DispatchQueuedViewObjectContents()
    {
        if (!_initialized || _plugins.Count == 0)
            return;

        PendingViewObjectContents[] pending;
        lock (PendingViewObjectContentsLock)
        {
            if (_pendingViewObjectContents.Count == 0)
                return;

            pending = _pendingViewObjectContents.ToArray();
            _pendingViewObjectContents.Clear();
        }

        foreach (PendingViewObjectContents evt in pending)
        {
            foreach (var plugin in _plugins)
            {
                if (!plugin.Initialized || plugin.Failed || plugin.OnViewObjectContents == null)
                    continue;

                try
                {
                    plugin.OnViewObjectContents(evt.ObjectId);
                }
                catch (Exception ex)
                {
                    plugin.Failed = true;
                    EntryPoint.Log($"PluginManager: {plugin.DisplayName} OnViewObjectContents threw {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    private static void DispatchQueuedStopViewingObjectContents()
    {
        if (!_initialized || _plugins.Count == 0)
            return;

        PendingStopViewingObjectContents[] pending;
        lock (PendingStopViewingObjectContentsLock)
        {
            if (_pendingStopViewingObjectContents.Count == 0)
                return;

            pending = _pendingStopViewingObjectContents.ToArray();
            _pendingStopViewingObjectContents.Clear();
        }

        foreach (PendingStopViewingObjectContents evt in pending)
        {
            foreach (var plugin in _plugins)
            {
                if (!plugin.Initialized || plugin.Failed || plugin.OnStopViewingObjectContents == null)
                    continue;

                try
                {
                    plugin.OnStopViewingObjectContents(evt.ObjectId);
                }
                catch (Exception ex)
                {
                    plugin.Failed = true;
                    EntryPoint.Log($"PluginManager: {plugin.DisplayName} OnStopViewingObjectContents threw {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    private static void LoadPluginsFromDisk()
    {
        _loadGeneration++;
        var loaded = PluginLoader.LoadAll(_pluginsDir, _shadowRootDir, _loadGeneration);
        _plugins.AddRange(loaded);
    }

    private static void InitializeLoadedPlugins()
    {
        if (_plugins.Count == 0)
        {
            EntryPoint.Log("PluginManager: No plugins to initialize.");
            return;
        }

        EnsureHostCallbacks();
        EntryPoint.Log($"PluginManager: Initializing {_plugins.Count} plugin(s)...");

        foreach (var plugin in _plugins)
        {
            try
            {
                int result = plugin.Init!(ref _api);
                if (result == 0)
                {
                    plugin.Initialized = true;
                    plugin.Failed = false;
                    plugin.UIInitializedDispatched = false;
                    plugin.LoginCompleteDispatched = false;
                    EntryPoint.Log($"PluginManager: {plugin.DisplayName} initialized OK.");
                }
                else
                {
                    plugin.Failed = true;
                    EntryPoint.Log($"PluginManager: {plugin.DisplayName} Init returned {result} - marked as failed.");
                }
            }
            catch (Exception ex)
            {
                plugin.Failed = true;
                EntryPoint.Log($"PluginManager: {plugin.DisplayName} Init threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (!_uiInitializedObserved)
            EntryPoint.Log("PluginManager: Waiting for OnUIInitialized before starting UI-gated plugins.");

        if (!_loginCompleteObserved)
            EntryPoint.Log("PluginManager: Waiting for OnLoginComplete before starting login-gated plugins.");
    }

    private static void UnloadAllPlugins()
    {
        for (int i = _plugins.Count - 1; i >= 0; i--)
        {
            var plugin = _plugins[i];
            if (plugin.Initialized && !plugin.Failed)
            {
                try
                {
                    plugin.Shutdown!();
                    EntryPoint.Log($"PluginManager: {plugin.DisplayName} shut down.");
                }
                catch (Exception ex)
                {
                    EntryPoint.Log($"PluginManager: {plugin.DisplayName} Shutdown threw: {ex.Message}");
                }
            }

            PluginLoader.Unload(plugin);
        }

        _plugins.Clear();
    }

    private static void DispatchUIInitializedToLoadedPlugins()
    {
        if (!_uiInitializedObserved || !_initialized)
            return;

        _uiDispatchPending = false;

        foreach (var plugin in _plugins)
        {
            if (!plugin.Initialized || plugin.Failed || plugin.OnUIInitialized == null || plugin.UIInitializedDispatched)
                continue;

            try
            {
                plugin.OnUIInitialized();
                plugin.UIInitializedDispatched = true;
                EntryPoint.Log($"PluginManager: {plugin.DisplayName} received OnUIInitialized.");
            }
            catch (Exception ex)
            {
                plugin.Failed = true;
                EntryPoint.Log($"PluginManager: {plugin.DisplayName} OnUIInitialized threw {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static void DispatchLoginCompleteToLoadedPlugins()
    {
        if (!_loginCompleteObserved || !_initialized)
            return;

        _loginDispatchPending = false;

        foreach (var plugin in _plugins)
        {
            if (!plugin.Initialized || plugin.Failed || plugin.OnLoginComplete == null || plugin.LoginCompleteDispatched)
                continue;

            try
            {
                plugin.OnLoginComplete();
                plugin.LoginCompleteDispatched = true;
                EntryPoint.Log($"PluginManager: {plugin.DisplayName} received OnLoginComplete.");
            }
            catch (Exception ex)
            {
                plugin.Failed = true;
                EntryPoint.Log($"PluginManager: {plugin.DisplayName} OnLoginComplete threw {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static void EnsureHostCallbacks()
    {
        _logCallback ??= LogFromPlugin;
        _probeClientHooksCallback ??= ProbeClientHooks;
        _getClientHookFlagsCallback ??= GetClientHookFlags;
        _changeCombatModeCallback ??= ChangeCombatMode;
        _cancelAttackCallback ??= CancelAttack;
        _queryHealthCallback ??= QueryHealth;
        _meleeAttackCallback ??= MeleeAttack;
        _missileAttackCallback ??= MissileAttack;
        _doMovementCallback ??= DoMovement;
        _stopMovementCallback ??= StopMovement;
        _jumpNonAutonomousCallback ??= JumpNonAutonomous;
        _setAutonomyLevelCallback ??= SetAutonomyLevel;
        _setAutoRunCallback ??= SetAutoRun;
        _tapJumpCallback ??= TapJump;
        _setIncomingChatSuppressionCallback ??= SetIncomingChatSuppression;

        _api.Version = PluginContractVersion.Current;
        _api.LogFn = Marshal.GetFunctionPointerForDelegate(_logCallback);
        _api.ProbeClientHooksFn = Marshal.GetFunctionPointerForDelegate(_probeClientHooksCallback);
        _api.GetClientHookFlagsFn = Marshal.GetFunctionPointerForDelegate(_getClientHookFlagsCallback);
        _api.ChangeCombatModeFn = Marshal.GetFunctionPointerForDelegate(_changeCombatModeCallback);
        _api.CancelAttackFn = Marshal.GetFunctionPointerForDelegate(_cancelAttackCallback);
        _api.QueryHealthFn = Marshal.GetFunctionPointerForDelegate(_queryHealthCallback);
        _api.MeleeAttackFn = Marshal.GetFunctionPointerForDelegate(_meleeAttackCallback);
        _api.MissileAttackFn = Marshal.GetFunctionPointerForDelegate(_missileAttackCallback);
        _api.DoMovementFn = Marshal.GetFunctionPointerForDelegate(_doMovementCallback);
        _api.StopMovementFn = Marshal.GetFunctionPointerForDelegate(_stopMovementCallback);
        _api.JumpNonAutonomousFn = Marshal.GetFunctionPointerForDelegate(_jumpNonAutonomousCallback);
        _api.SetAutonomyLevelFn = Marshal.GetFunctionPointerForDelegate(_setAutonomyLevelCallback);
        _api.SetAutoRunFn = Marshal.GetFunctionPointerForDelegate(_setAutoRunCallback);
        _api.TapJumpFn = Marshal.GetFunctionPointerForDelegate(_tapJumpCallback);
        _api.SetIncomingChatSuppressionFn = Marshal.GetFunctionPointerForDelegate(_setIncomingChatSuppressionCallback);
    }

    private static void ProbeClientHooks()
    {
        ClientActionHooks.Probe();
    }

    private static uint GetClientHookFlags()
    {
        ClientActionHookStatus status = ClientActionHooks.GetStatus();
        uint flags = 0;

        if (status.CombatInitialized)
            flags |= ClientActionHookFlags.CombatInitialized;

        if (status.MovementInitialized)
            flags |= ClientActionHookFlags.MovementInitialized;

        if (status.MeleeAvailable)
            flags |= ClientActionHookFlags.MeleeAttack;

        if (status.MissileAvailable)
            flags |= ClientActionHookFlags.MissileAttack;

        if (status.ChangeCombatModeAvailable)
            flags |= ClientActionHookFlags.ChangeCombatMode;

        if (status.CancelAttackAvailable)
            flags |= ClientActionHookFlags.CancelAttack;

        if (status.QueryHealthAvailable)
            flags |= ClientActionHookFlags.QueryHealth;

        if (status.DoMovementAvailable)
            flags |= ClientActionHookFlags.DoMovement;

        if (status.StopMovementAvailable)
            flags |= ClientActionHookFlags.StopMovement;

        if (status.JumpNonAutonomousAvailable)
            flags |= ClientActionHookFlags.JumpNonAutonomous;

        if (status.AutonomyLevelAvailable)
            flags |= ClientActionHookFlags.SetAutonomyLevel;

        if (status.SetAutoRunAvailable)
            flags |= ClientActionHookFlags.SetAutoRun;

        if (status.TapJumpAvailable)
            flags |= ClientActionHookFlags.TapJump;

        return flags;
    }

    private static int ChangeCombatMode(int combatMode)
    {
        return ToAbiBool(ClientActionHooks.ChangeCombatMode(combatMode));
    }

    private static int CancelAttack()
    {
        return ToAbiBool(ClientActionHooks.CancelAttack());
    }

    private static int QueryHealth(uint targetId)
    {
        return ToAbiBool(ClientActionHooks.QueryHealth(targetId));
    }

    private static int MeleeAttack(uint targetId, int attackHeight, float powerLevel)
    {
        return ToAbiBool(ClientActionHooks.MeleeAttack(targetId, attackHeight, powerLevel));
    }

    private static int MissileAttack(uint targetId, int attackHeight, float accuracyLevel)
    {
        return ToAbiBool(ClientActionHooks.MissileAttack(targetId, attackHeight, accuracyLevel));
    }

    private static int DoMovement(uint motion, float speed, int holdKey)
    {
        return ToAbiBool(ClientActionHooks.DoMovement(motion, speed, holdKey));
    }

    private static int StopMovement(uint motion, int holdKey)
    {
        return ToAbiBool(ClientActionHooks.StopMovement(motion, holdKey));
    }

    private static int JumpNonAutonomous(float extent)
    {
        return ToAbiBool(ClientActionHooks.JumpNonAutonomous(extent));
    }

    private static int SetAutonomyLevel(uint level)
    {
        return ToAbiBool(ClientActionHooks.SetAutonomyLevel(level));
    }

    private static int SetAutoRun(int enabled)
    {
        return ToAbiBool(ClientActionHooks.SetAutoRun(enabled != 0));
    }

    private static int TapJump()
    {
        return ToAbiBool(ClientActionHooks.TapJump());
    }

    private static void SetIncomingChatSuppression(int enabled)
    {
        ChatCallbackHooks.SetIncomingChatSuppression(enabled != 0);
    }

    private static int ToAbiBool(bool value)
    {
        return value ? 1 : 0;
    }
}
