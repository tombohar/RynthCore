// ============================================================================
//  RynthCore.Engine - Plugins/PluginManager.cs
//  Orchestrates plugin lifecycle: load, init, login-ready callbacks, tick,
//  render, rescan, and shutdown.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.D3D9;

namespace RynthCore.Engine.Plugins;

internal static class PluginManager
{
    private readonly record struct PendingIncomingChat(string? Text, uint ChatType);
    private readonly record struct PendingBusyCountIncremented;
    private readonly record struct PendingBusyCountDecremented;
    private readonly record struct PendingTargetChange(uint CurrentTargetId, uint PreviousTargetId);
    private readonly record struct PendingCombatModeChange(int CurrentCombatMode, int PreviousCombatMode);
    private readonly record struct PendingSmartBoxEvent(uint Opcode, uint BlobSize, uint Status);
    private readonly record struct PendingCreateObject(uint ObjectId);
    private readonly record struct PendingDeleteObject(uint ObjectId);
    private readonly record struct PendingUpdateObject(uint ObjectId);
    private readonly record struct PendingUpdateObjectInventory(uint ObjectId);
    private readonly record struct PendingViewObjectContents(uint ObjectId);
    private readonly record struct PendingStopViewingObjectContents(uint ObjectId);
    private readonly record struct PendingVendorOpen(uint VendorId);
    private readonly record struct PendingVendorClose(uint VendorId);
    private readonly record struct PendingUpdateHealth(uint TargetId, float HealthRatio, uint CurrentHealth, uint MaxHealth);
    private readonly record struct PendingEnchantmentAdded(uint SpellId, double DurationSeconds);
    private readonly record struct PendingEnchantmentRemoved(uint EnchantmentId);

    private const int MaxPendingIncomingChats = 256;
    private const int MaxPendingBusyCountEvents = 128;
    private const int MaxPendingTargetChanges = 128;
    private const int MaxPendingCombatModeChanges = 128;
    private const int MaxPendingSmartBoxEvents = 1024;
    private const int MaxPendingCreateObjects = 2048;
    private const int MaxPendingDeleteObjects = 2048;
    private const int MaxPendingUpdateObjects = 512;
    private const int MaxDispatchedUpdateObjectsPerFrame = 16;
    private const int UpdateObjectDispatchIntervalMs = 50;
    private const int MaxPendingUpdateObjectInventory = 128;
    private const int MaxPendingViewObjectContents = 128;
    private const int MaxPendingStopViewingObjectContents = 128;
    private const int MaxPendingVendorOpen = 32;
    private const int MaxPendingVendorClose = 32;
    private const int MaxPendingUpdateHealth = 512;
    private const int MaxPendingEnchantmentEvents = 256;
    private const int MaxPrePluginCreateObjects = 4096;
    private static readonly Queue<uint> _prePluginCreateObjects = new();
    private static readonly object PrePluginCreateObjectsLock = new();
    private static readonly List<LoadedPlugin> _plugins = new();
    private static readonly Queue<PendingIncomingChat> _pendingIncomingChats = new();
    private static readonly Queue<PendingBusyCountIncremented> _pendingBusyCountIncremented = new();
    private static readonly Queue<PendingBusyCountDecremented> _pendingBusyCountDecremented = new();
    private static readonly Queue<PendingTargetChange> _pendingTargetChanges = new();
    private static readonly Queue<PendingCombatModeChange> _pendingCombatModeChanges = new();
    private static readonly Queue<PendingSmartBoxEvent> _pendingSmartBoxEvents = new();
    private static readonly Queue<PendingCreateObject> _pendingCreateObjects = new();
    private static readonly Queue<PendingDeleteObject> _pendingDeleteObjects = new();
    private static readonly Queue<PendingUpdateObject> _pendingUpdateObjects = new();
    private static readonly HashSet<uint> _pendingUpdateObjectIds = new();
    private static readonly Queue<PendingUpdateObjectInventory> _pendingUpdateObjectInventory = new();
    private static readonly Queue<PendingViewObjectContents> _pendingViewObjectContents = new();
    private static readonly Queue<PendingStopViewingObjectContents> _pendingStopViewingObjectContents = new();
    private static readonly Queue<PendingVendorOpen> _pendingVendorOpen = new();
    private static readonly Queue<PendingVendorClose> _pendingVendorClose = new();
    private static readonly Queue<PendingUpdateHealth> _pendingUpdateHealth = new();
    private static readonly Queue<PendingEnchantmentAdded> _pendingEnchantmentAdded = new();
    private static readonly Queue<PendingEnchantmentRemoved> _pendingEnchantmentRemoved = new();
    private static readonly object PendingIncomingChatsLock = new();
    private static readonly object PendingBusyCountIncrementedLock = new();
    private static readonly object PendingBusyCountDecrementedLock = new();
    private static readonly object PendingTargetChangesLock = new();
    private static readonly object PendingCombatModeChangesLock = new();
    private static readonly object PendingSmartBoxEventsLock = new();
    private static readonly object PendingCreateObjectsLock = new();
    private static readonly object PendingDeleteObjectsLock = new();
    private static readonly object PendingUpdateObjectsLock = new();
    private static readonly object PendingUpdateObjectInventoryLock = new();
    private static readonly object PendingViewObjectContentsLock = new();
    private static readonly object PendingStopViewingObjectContentsLock = new();
    private static readonly object PendingVendorOpenLock = new();
    private static readonly object PendingVendorCloseLock = new();
    private static readonly object PendingUpdateHealthLock = new();
    private static readonly object PendingEnchantmentAddedLock = new();
    private static readonly object PendingEnchantmentRemovedLock = new();
    private static bool _loaded;
    private static bool _initialized;
    private static bool _rescanRequested;
    private static bool _uiInitializedObserved;
    private static bool _uiDispatchPending;
    private static bool _loginCompleteObserved;
    private static bool _loginDispatchPending;
    private static long _nextUpdateObjectDispatchTick;
    private static int _loadGeneration;
    private static RynthCoreAPI _api;
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
    private static CommenceJumpCallbackDelegate? _commenceJumpCallback;
    private static DoJumpCallbackDelegate? _doJumpCallback;
    private static LaunchJumpWithMotionCallbackDelegate? _launchJumpWithMotionCallback;
    private static GetRadarRectCallbackDelegate? _getRadarRectCallback;
    private static SetMotionCallbackDelegate? _setMotionCallback;
    private static StopCompletelyCallbackDelegate? _stopCompletelyCallback;
    private static TurnToHeadingCallbackDelegate? _turnToHeadingCallback;
    private static GetPlayerHeadingCallbackDelegate? _getPlayerHeadingCallback;
    private static SetIncomingChatSuppressionCallbackDelegate? _setIncomingChatSuppressionCallback;
    private static SelectItemCallbackDelegate? _selectItemCallback;
    private static SetSelectedObjectIdCallbackDelegate? _setSelectedObjectIdCallback;
    private static GetItemIdCallbackDelegate? _getSelectedItemIdCallback;
    private static GetItemIdCallbackDelegate? _getPreviousSelectedItemIdCallback;
    private static GetItemIdCallbackDelegate? _getPlayerIdCallback;
    private static GetItemIdCallbackDelegate? _getGroundContainerIdCallback;
    private static QueryHealthCallbackDelegate? _getNumContainedItemsCallback;
    private static QueryHealthCallbackDelegate? _getNumContainedContainersCallback;
    private static GetCurCoordsCallbackDelegate? _getCurCoordsCallback;
    private static UseObjectCallbackDelegate? _useObjectCallback;
    private static UseObjectOnCallbackDelegate? _useObjectOnCallback;
    private static UseEquippedItemCallbackDelegate? _useEquippedItemCallback;
    private static MoveItemExternalCallbackDelegate? _moveItemExternalCallback;
    private static MoveItemInternalCallbackDelegate? _moveItemInternalCallback;
    private static SplitStackInternalCallbackDelegate? _splitStackInternalCallback;
    private static MergeStackInternalCallbackDelegate? _mergeStackInternalCallback;
    private static WriteToChatCallbackDelegate? _writeToChatCallback;
    private static GetPlayerPoseCallbackDelegate? _getPlayerPoseCallback;
    private static IsPortalingCallbackDelegate? _isPortalingCallback;
    private static GetObjectNameCallbackDelegate? _getObjectNameCallback;
    private static GetPlayerVitalsCallbackDelegate? _getPlayerVitalsCallback;
    private static GetObjectPositionCallbackDelegate? _getObjectPositionCallback;
    private static RequestIdCallbackDelegate? _requestIdCallback;
    private static GetTargetVitalsCallbackDelegate? _getTargetVitalsCallback;
    private static CastSpellCallbackDelegate? _castSpellCallback;
    private static GetItemTypeCallbackDelegate? _getItemTypeCallback;
    private static GetObjectIntPropertyCallbackDelegate? _getObjectIntPropertyCallback;
    private static GetObjectBoolPropertyCallbackDelegate? _getObjectBoolPropertyCallback;
    private static ObjectIsAttackableCallbackDelegate? _objectIsAttackableCallback;
    private static GetObjectSkillCallbackDelegate? _getObjectSkillCallback;
    private static IsSpellKnownCallbackDelegate? _isSpellKnownCallback;
    private static ReadPlayerEnchantmentsCallbackDelegate? _readPlayerEnchantmentsCallback;
    private static GetServerTimeCallbackDelegate? _getServerTimeCallback;
    private static ReadObjectEnchantmentsCallbackDelegate? _readObjectEnchantmentsCallback;
    private static WorldToScreenCallbackDelegate? _worldToScreenCallback;
    private static GetViewportSizeCallbackDelegate? _getViewportSizeCallback;
    private static Nav3DClearCallbackDelegate? _nav3DClearCallback;
    private static Nav3DAddRingCallbackDelegate? _nav3DAddRingCallback;
    private static Nav3DAddLineCallbackDelegate? _nav3DAddLineCallback;
    private static InvokeChatParserCallbackDelegate? _invokeChatParserCallback;
    private static GetObjectDoublePropertyCallbackDelegate? _getObjectDoublePropertyCallback;
    private static GetObjectQuadPropertyCallbackDelegate? _getObjectQuadPropertyCallback;
    private static GetObjectAttribute2ndBaseLevelCallbackDelegate? _getObjectAttribute2ndBaseLevelCallback;
    private static GetPlayerBaseVitalsCallbackDelegate? _getPlayerBaseVitalsCallback;
    private static GetObjectStringPropertyCallbackDelegate? _getObjectStringPropertyCallback;
    private static GetObjectWielderInfoCallbackDelegate? _getObjectWielderInfoCallback;
    private static NativeAttackCallbackDelegate? _nativeAttackCallback;
    private static IsPlayerReadyCallbackDelegate? _isPlayerReadyCallback;
    private static SetFpsLimitCallbackDelegate? _setFpsLimitCallback;
    private static GetContainerContentsCallbackDelegate? _getContainerContentsCallback;
    private static GetObjectOwnershipInfoCallbackDelegate? _getObjectOwnershipInfoCallback;
    private static GetCurrentCombatModeCallbackDelegate? _getCurrentCombatModeCallback;
    private static SalvagePanelOpenCallbackDelegate? _salvagePanelOpenCallback;
    private static SalvagePanelAddItemCallbackDelegate? _salvagePanelAddItemCallback;
    private static SalvagePanelExecuteCallbackDelegate? _salvagePanelExecuteCallback;
    private static GetVitaeCallbackDelegate? _getVitaeCallback;
    private static GetAccountNameCallbackDelegate? _getAccountNameCallback;
    private static GetWorldNameCallbackDelegate? _getWorldNameCallback;
    private static GetObjectWcidCallbackDelegate? _getObjectWcidCallback;
    private static HasAppraisalDataCallbackDelegate? _hasAppraisalDataCallback;
    private static GetLastIdTimeCallbackDelegate? _getLastIdTimeCallback;
    private static GetObjectHeadingCallbackDelegate? _getObjectHeadingCallback;
    private static GetBusyStateCallbackDelegate? _getBusyStateCallback;
    private static ForceResetBusyCountCallbackDelegate? _forceResetBusyCountCallback;
    private static GetObjectSpellIdsCallbackDelegate? _getObjectSpellIdsCallback;
    private static GetObjectSkillLevelCallbackDelegate? _getObjectSkillBuffedCallback;
    private static GetObjectAttributeCallbackDelegate? _getObjectAttributeCallback;
    private static GetObjectMotionOnCallbackDelegate? _getObjectMotionOnCallback;
    private static GetObjectStateCallbackDelegate? _getObjectStateCallback;
    private static GetObjectBitfieldCallbackDelegate? _getObjectBitfieldCallback;
    private static GetObjectPalettesCallbackDelegate? _getObjectPalettesCallback;
    private static IntPtr _accountNameScratchPtr;
    private static IntPtr _worldNameScratchPtr;
    [ThreadStatic] private static IntPtr _objectNameScratchPtr;
    private static string _pluginsDir = "";
    private static string _shadowRootDir = "";

    public static IReadOnlyList<LoadedPlugin> Plugins => _plugins;
    public static bool IsRescanQueued => _rescanRequested;
    public static string PluginDirectory => _pluginsDir;
    public static IReadOnlyList<string> ExtraPluginPaths => EngineSettings.PluginPaths;
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

        string normalizedEngineDir = Path.GetFullPath(engineDir);
        bool engineDirIsRuntime = string.Equals(
            Path.GetFileName(normalizedEngineDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            "Runtime",
            StringComparison.OrdinalIgnoreCase);

        _pluginsDir = engineDirIsRuntime
            ? Path.Combine(normalizedEngineDir, "Plugins")
            : Path.Combine(normalizedEngineDir, "Runtime", "Plugins");
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

        RynthLog.Plugin($"PluginManager: InitPlugins — {_plugins.Count} plugin(s) loaded, api version={PluginContractVersion.Current}");
        InitializeLoadedPlugins();
        DispatchUIInitializedToLoadedPlugins();
        DispatchLoginCompleteToLoadedPlugins();
        ReplayPrePluginCreateObjects();
    }

    private static void ReplayPrePluginCreateObjects()
    {
        uint[] prePending;
        lock (PrePluginCreateObjectsLock)
        {
            if (_prePluginCreateObjects.Count == 0) return;
            prePending = _prePluginCreateObjects.ToArray();
            // Do NOT clear — keep accumulating so hot-reloads can replay the full snapshot.
        }

        RynthLog.Plugin($"PluginManager: Replaying {prePending.Length} pre-plugin CreateObject event(s).");
        lock (PendingCreateObjectsLock)
        {
            foreach (uint objectId in prePending)
            {
                if (_pendingCreateObjects.Count >= MaxPendingCreateObjects)
                    _pendingCreateObjects.Dequeue();
                _pendingCreateObjects.Enqueue(new PendingCreateObject(objectId));
            }
        }
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
        SessionStateRegistry.Poll();
        DispatchQueuedBusyCountDecremented();
        DispatchQueuedBusyCountIncremented();
        DispatchQueuedCombatModeChange();
        DispatchQueuedSmartBoxEvent();
        DispatchQueuedStopViewingObjectContents();
        DispatchQueuedViewObjectContents();
        DispatchQueuedVendorOpen();
        DispatchQueuedVendorClose();
        DispatchQueuedUpdateObjectInventory();
        DispatchQueuedCreateObject();
        DispatchQueuedUpdateObject();
        DispatchQueuedDeleteObject();
        DispatchQueuedSelectedTargetChange();
        DispatchQueuedUpdateHealth();
        DispatchQueuedEnchantmentAdded();
        DispatchQueuedEnchantmentRemoved();
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

        RynthLog.Verbose($"PluginManager: DispatchChatBarEnter text='{text ?? "<null>"}'");

        IntPtr textPtr = text != null ? Marshal.StringToHGlobalUni(text) : IntPtr.Zero;
        try
        {
            unsafe
            {
                int eat = 0;
                IntPtr eatPtr = new(&eat);

                for (int i = 0; i < _plugins.Count; i++)
                {
                    var plugin = _plugins[i];
                    if (!plugin.Initialized || plugin.Failed || plugin.OnChatBarEnter == null)
                        continue;

                    try
                    {
                        plugin.OnChatBarEnter(textPtr, eatPtr);
                    }
                    catch (Exception ex)
                    {
                        plugin.Failed = true;
                        RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnChatBarEnter threw {ex.GetType().Name}: {ex.Message}");
                    }
                }

                bool eaten = eat != 0;
                RynthLog.Verbose($"PluginManager: DispatchChatBarEnter eaten={eaten}");
                return eaten;
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

    public static void QueueBusyCountIncremented()
    {
        if (!_initialized || _plugins.Count == 0)
            return;

        lock (PendingBusyCountIncrementedLock)
        {
            if (_pendingBusyCountIncremented.Count >= MaxPendingBusyCountEvents)
                _pendingBusyCountIncremented.Dequeue();

            _pendingBusyCountIncremented.Enqueue(new PendingBusyCountIncremented());
        }
    }

    public static void QueueBusyCountDecremented()
    {
        if (!_initialized || _plugins.Count == 0)
            return;

        lock (PendingBusyCountDecrementedLock)
        {
            if (_pendingBusyCountDecremented.Count >= MaxPendingBusyCountEvents)
                _pendingBusyCountDecremented.Dequeue();

            _pendingBusyCountDecremented.Enqueue(new PendingBusyCountDecremented());
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

    public static void QueueCombatModeChange(int currentCombatMode, int previousCombatMode)
    {
        if (!_initialized || _plugins.Count == 0)
            return;

        lock (PendingCombatModeChangesLock)
        {
            if (_pendingCombatModeChanges.Count >= MaxPendingCombatModeChanges)
                _pendingCombatModeChanges.Dequeue();

            _pendingCombatModeChanges.Enqueue(new PendingCombatModeChange(currentCombatMode, previousCombatMode));
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
        if (_plugins.Count == 0)
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
        if (_plugins.Count == 0 || objectId == 0)
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
        // Always buffer for replay — covers the race between hook install and plugin load
        lock (PrePluginCreateObjectsLock)
        {
            if (_prePluginCreateObjects.Count >= MaxPrePluginCreateObjects)
                _prePluginCreateObjects.Dequeue();
            _prePluginCreateObjects.Enqueue(objectId);
        }

        if (_plugins.Count == 0)
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
        if (_plugins.Count == 0)
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
        if (_plugins.Count == 0)
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
        if (_plugins.Count == 0)
            return;

        lock (PendingStopViewingObjectContentsLock)
        {
            if (_pendingStopViewingObjectContents.Count >= MaxPendingStopViewingObjectContents)
                _pendingStopViewingObjectContents.Dequeue();

            _pendingStopViewingObjectContents.Enqueue(new PendingStopViewingObjectContents(objectId));
        }
    }

    public static void QueueVendorOpen(uint vendorId)
    {
        if (_plugins.Count == 0)
            return;

        lock (PendingVendorOpenLock)
        {
            if (_pendingVendorOpen.Count >= MaxPendingVendorOpen)
                _pendingVendorOpen.Dequeue();

            _pendingVendorOpen.Enqueue(new PendingVendorOpen(vendorId));
        }
    }

    public static void QueueVendorClose(uint vendorId)
    {
        if (_plugins.Count == 0)
            return;

        lock (PendingVendorCloseLock)
        {
            if (_pendingVendorClose.Count >= MaxPendingVendorClose)
                _pendingVendorClose.Dequeue();

            _pendingVendorClose.Enqueue(new PendingVendorClose(vendorId));
        }
    }

    public static void QueueUpdateHealth(uint targetId, float healthRatio, uint currentHealth, uint maxHealth)
    {
        if (_plugins.Count == 0)
            return;

        lock (PendingUpdateHealthLock)
        {
            if (_pendingUpdateHealth.Count >= MaxPendingUpdateHealth)
                _pendingUpdateHealth.Dequeue();

            _pendingUpdateHealth.Enqueue(new PendingUpdateHealth(targetId, healthRatio, currentHealth, maxHealth));
        }
    }

    public static void QueueEnchantmentAdded(uint spellId, double durationSeconds)
    {
        if (_plugins.Count == 0)
            return;

        lock (PendingEnchantmentAddedLock)
        {
            if (_pendingEnchantmentAdded.Count >= MaxPendingEnchantmentEvents)
                _pendingEnchantmentAdded.Dequeue();

            _pendingEnchantmentAdded.Enqueue(new PendingEnchantmentAdded(spellId, durationSeconds));
        }
    }

    public static void QueueEnchantmentRemoved(uint enchantmentId)
    {
        if (_plugins.Count == 0)
            return;

        lock (PendingEnchantmentRemovedLock)
        {
            if (_pendingEnchantmentRemoved.Count >= MaxPendingEnchantmentEvents)
                _pendingEnchantmentRemoved.Dequeue();

            _pendingEnchantmentRemoved.Enqueue(new PendingEnchantmentRemoved(enchantmentId));
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

                for (int i = 0; i < _plugins.Count; i++)
                {
                    var plugin = _plugins[i];
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
                        RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnChatWindowText threw {ex.GetType().Name}: {ex.Message}");
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
                RynthLog.Plugin($"PluginManager: {plugin.DisplayName} Tick threw {ex.GetType().Name}: {ex.Message} - disabled.");
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
                RynthLog.Plugin($"PluginManager: {plugin.DisplayName} Render threw {ex.GetType().Name}: {ex.Message} - disabled.");
            }
        }
    }

    public static void ShutdownAll()
    {
        RynthLog.Plugin($"PluginManager: Shutting down {_plugins.Count} plugin(s)...");
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
        lock (PendingBusyCountIncrementedLock)
            _pendingBusyCountIncremented.Clear();
        lock (PendingBusyCountDecrementedLock)
            _pendingBusyCountDecremented.Clear();
        lock (PendingTargetChangesLock)
            _pendingTargetChanges.Clear();
        lock (PendingCombatModeChangesLock)
            _pendingCombatModeChanges.Clear();
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
        lock (PendingUpdateHealthLock)
            _pendingUpdateHealth.Clear();
        lock (PendingEnchantmentAddedLock)
            _pendingEnchantmentAdded.Clear();
        lock (PendingEnchantmentRemovedLock)
            _pendingEnchantmentRemoved.Clear();
    }

    private static void LogFromPlugin(IntPtr messageUtf8)
    {
        string? msg = Marshal.PtrToStringAnsi(messageUtf8);
        if (msg != null)
            RynthLog.Plugin($"[Plugin] {msg}");
    }

    private static void OnUIInitializedObserved()
    {
        if (_uiInitializedObserved)
            return;

        _uiInitializedObserved = true;
        _uiDispatchPending = true;
        RynthLog.Plugin("PluginManager: OnUIInitialized observed - queued UI lifecycle callbacks.");
    }

    private static void OnLoginCompleteObserved()
    {
        if (_loginCompleteObserved)
            return;

        _loginCompleteObserved = true;
        _loginDispatchPending = true;
        RynthLog.Plugin("PluginManager: OnLoginComplete observed - queued login lifecycle callbacks.");
    }

    private static void RescanPlugins(IntPtr imguiContext, IntPtr d3dDevice, IntPtr gameHwnd)
    {
        if (string.IsNullOrWhiteSpace(_pluginsDir))
        {
            RynthLog.Plugin("PluginManager: Rescan requested before plugin directories were configured.");
            return;
        }

        RynthLog.Plugin("PluginManager: Rescanning plugins...");

        UnloadAllPlugins();
        PluginLoader.CleanupShadowCopies(_shadowRootDir);
        LoadPluginsFromDisk();

        _api.ImGuiContext = imguiContext;
        _api.D3DDevice = d3dDevice;
        _api.GameHwnd = gameHwnd;

        InitializeLoadedPlugins();
        DispatchUIInitializedToLoadedPlugins();
        DispatchLoginCompleteToLoadedPlugins();
        ReplayPrePluginCreateObjects();
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

                    for (int i = 0; i < _plugins.Count; i++)
                    {
                        var plugin = _plugins[i];
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
                            RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnChatWindowText threw {ex.GetType().Name}: {ex.Message}");
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
            for (int i = 0; i < _plugins.Count; i++)
            {
                var plugin = _plugins[i];
                if (!plugin.Initialized || plugin.Failed || plugin.OnSelectedTargetChange == null)
                    continue;

                try
                {
                    plugin.OnSelectedTargetChange(evt.CurrentTargetId, evt.PreviousTargetId);
                }
                catch (Exception ex)
                {
                    plugin.Failed = true;
                    RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnSelectedTargetChange threw {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    private static void DispatchQueuedBusyCountIncremented()
    {
        if (!_initialized || _plugins.Count == 0)
            return;

        PendingBusyCountIncremented[] pending;
        lock (PendingBusyCountIncrementedLock)
        {
            if (_pendingBusyCountIncremented.Count == 0)
                return;

            pending = _pendingBusyCountIncremented.ToArray();
            _pendingBusyCountIncremented.Clear();
        }

        foreach (PendingBusyCountIncremented _ in pending)
        {
            for (int i = 0; i < _plugins.Count; i++)
            {
                var plugin = _plugins[i];
                if (!plugin.Initialized || plugin.Failed || plugin.OnBusyCountIncremented == null)
                    continue;

                try
                {
                    plugin.OnBusyCountIncremented();
                }
                catch (Exception ex)
                {
                    plugin.Failed = true;
                    RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnBusyCountIncremented threw {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    private static void DispatchQueuedBusyCountDecremented()
    {
        if (!_initialized || _plugins.Count == 0)
            return;

        PendingBusyCountDecremented[] pending;
        lock (PendingBusyCountDecrementedLock)
        {
            if (_pendingBusyCountDecremented.Count == 0)
                return;

            pending = _pendingBusyCountDecremented.ToArray();
            _pendingBusyCountDecremented.Clear();
        }

        foreach (PendingBusyCountDecremented _ in pending)
        {
            for (int i = 0; i < _plugins.Count; i++)
            {
                var plugin = _plugins[i];
                if (!plugin.Initialized || plugin.Failed || plugin.OnBusyCountDecremented == null)
                    continue;

                try
                {
                    plugin.OnBusyCountDecremented();
                }
                catch (Exception ex)
                {
                    plugin.Failed = true;
                    RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnBusyCountDecremented threw {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    private static void DispatchQueuedCombatModeChange()
    {
        if (!_initialized || _plugins.Count == 0)
            return;

        PendingCombatModeChange[] pending;
        lock (PendingCombatModeChangesLock)
        {
            if (_pendingCombatModeChanges.Count == 0)
                return;

            pending = _pendingCombatModeChanges.ToArray();
            _pendingCombatModeChanges.Clear();
        }

        foreach (PendingCombatModeChange evt in pending)
        {
            for (int i = 0; i < _plugins.Count; i++)
            {
                var plugin = _plugins[i];
                if (!plugin.Initialized || plugin.Failed || plugin.OnCombatModeChange == null)
                    continue;

                try
                {
                    plugin.OnCombatModeChange(evt.CurrentCombatMode, evt.PreviousCombatMode);
                }
                catch (Exception ex)
                {
                    plugin.Failed = true;
                    RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnCombatModeChange threw {ex.GetType().Name}: {ex.Message}");
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
            for (int i = 0; i < _plugins.Count; i++)
            {
                var plugin = _plugins[i];
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
                    RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnSmartBoxEvent threw {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    private static void DispatchQueuedUpdateObject()
    {
        if (!_initialized || _plugins.Count == 0)
            return;

        long now = Environment.TickCount64;
        if (now < _nextUpdateObjectDispatchTick)
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

        _nextUpdateObjectDispatchTick = now + UpdateObjectDispatchIntervalMs;

        foreach (PendingUpdateObject evt in pending)
        {
            for (int i = 0; i < _plugins.Count; i++)
            {
                var plugin = _plugins[i];
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
                    RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnUpdateObject threw {ex.GetType().Name}: {ex.Message}");
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
            for (int i = 0; i < _plugins.Count; i++)
            {
                var plugin = _plugins[i];
                if (!plugin.Initialized || plugin.Failed || plugin.OnDeleteObject == null)
                    continue;

                try
                {
                    plugin.OnDeleteObject(evt.ObjectId);
                }
                catch (Exception ex)
                {
                    plugin.Failed = true;
                    RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnDeleteObject threw {ex.GetType().Name}: {ex.Message}");
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
            for (int i = 0; i < _plugins.Count; i++)
            {
                var plugin = _plugins[i];
                if (!plugin.Initialized || plugin.Failed || plugin.OnCreateObject == null)
                    continue;

                try
                {
                    plugin.OnCreateObject(evt.ObjectId);
                }
                catch (Exception ex)
                {
                    plugin.Failed = true;
                    RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnCreateObject threw {ex.GetType().Name}: {ex.Message}");
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
            for (int i = 0; i < _plugins.Count; i++)
            {
                var plugin = _plugins[i];
                if (!plugin.Initialized || plugin.Failed || plugin.OnUpdateObjectInventory == null)
                    continue;

                try
                {
                    plugin.OnUpdateObjectInventory(evt.ObjectId);
                }
                catch (Exception ex)
                {
                    plugin.Failed = true;
                    RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnUpdateObjectInventory threw {ex.GetType().Name}: {ex.Message}");
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
            for (int i = 0; i < _plugins.Count; i++)
            {
                var plugin = _plugins[i];
                if (!plugin.Initialized || plugin.Failed || plugin.OnViewObjectContents == null)
                    continue;

                try
                {
                    plugin.OnViewObjectContents(evt.ObjectId);
                }
                catch (Exception ex)
                {
                    plugin.Failed = true;
                    RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnViewObjectContents threw {ex.GetType().Name}: {ex.Message}");
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
            for (int i = 0; i < _plugins.Count; i++)
            {
                var plugin = _plugins[i];
                if (!plugin.Initialized || plugin.Failed || plugin.OnStopViewingObjectContents == null)
                    continue;

                try
                {
                    plugin.OnStopViewingObjectContents(evt.ObjectId);
                }
                catch (Exception ex)
                {
                    plugin.Failed = true;
                    RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnStopViewingObjectContents threw {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    private static void DispatchQueuedVendorOpen()
    {
        if (!_initialized || _plugins.Count == 0)
            return;

        PendingVendorOpen[] pending;
        lock (PendingVendorOpenLock)
        {
            if (_pendingVendorOpen.Count == 0)
                return;

            pending = _pendingVendorOpen.ToArray();
            _pendingVendorOpen.Clear();
        }

        foreach (PendingVendorOpen evt in pending)
        {
            for (int i = 0; i < _plugins.Count; i++)
            {
                var plugin = _plugins[i];
                if (!plugin.Initialized || plugin.Failed || plugin.OnVendorOpen == null)
                    continue;

                try
                {
                    plugin.OnVendorOpen(evt.VendorId);
                }
                catch (Exception ex)
                {
                    plugin.Failed = true;
                    RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnVendorOpen threw {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    private static void DispatchQueuedVendorClose()
    {
        if (!_initialized || _plugins.Count == 0)
            return;

        PendingVendorClose[] pending;
        lock (PendingVendorCloseLock)
        {
            if (_pendingVendorClose.Count == 0)
                return;

            pending = _pendingVendorClose.ToArray();
            _pendingVendorClose.Clear();
        }

        foreach (PendingVendorClose evt in pending)
        {
            for (int i = 0; i < _plugins.Count; i++)
            {
                var plugin = _plugins[i];
                if (!plugin.Initialized || plugin.Failed || plugin.OnVendorClose == null)
                    continue;

                try
                {
                    plugin.OnVendorClose(evt.VendorId);
                }
                catch (Exception ex)
                {
                    plugin.Failed = true;
                    RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnVendorClose threw {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    private static void DispatchQueuedUpdateHealth()
    {
        if (!_initialized || _plugins.Count == 0)
            return;

        PendingUpdateHealth[] pending;
        lock (PendingUpdateHealthLock)
        {
            if (_pendingUpdateHealth.Count == 0)
                return;

            pending = _pendingUpdateHealth.ToArray();
            _pendingUpdateHealth.Clear();
        }

        foreach (PendingUpdateHealth evt in pending)
        {
            for (int i = 0; i < _plugins.Count; i++)
            {
                var plugin = _plugins[i];
                if (!plugin.Initialized || plugin.Failed)
                    continue;

                try
                {
                    unsafe
                    {
                        if (plugin.OnUpdateHealthPtr != IntPtr.Zero)
                        {
                            ((delegate* unmanaged[Cdecl]<uint, float, uint, uint, void>)plugin.OnUpdateHealthPtr)(evt.TargetId, evt.HealthRatio, evt.CurrentHealth, evt.MaxHealth);
                        }
                        else if (plugin.OnUpdateHealth != null)
                        {
                            plugin.OnUpdateHealth(evt.TargetId, evt.HealthRatio, evt.CurrentHealth, evt.MaxHealth);
                        }
                    }
                }
                catch (Exception ex)
                {
                    plugin.Failed = true;
                    RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnUpdateHealth threw {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    private static void DispatchQueuedEnchantmentAdded()
    {
        if (!_initialized || _plugins.Count == 0)
            return;

        PendingEnchantmentAdded[] pending;
        lock (PendingEnchantmentAddedLock)
        {
            if (_pendingEnchantmentAdded.Count == 0)
                return;

            pending = _pendingEnchantmentAdded.ToArray();
            _pendingEnchantmentAdded.Clear();
        }

        foreach (PendingEnchantmentAdded evt in pending)
        {
            for (int i = 0; i < _plugins.Count; i++)
            {
                var plugin = _plugins[i];
                if (!plugin.Initialized || plugin.Failed || plugin.OnEnchantmentAdded == null)
                    continue;

                try
                {
                    plugin.OnEnchantmentAdded(evt.SpellId, evt.DurationSeconds);
                }
                catch (Exception ex)
                {
                    plugin.Failed = true;
                    RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnEnchantmentAdded threw {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    private static void DispatchQueuedEnchantmentRemoved()
    {
        if (!_initialized || _plugins.Count == 0)
            return;

        PendingEnchantmentRemoved[] pending;
        lock (PendingEnchantmentRemovedLock)
        {
            if (_pendingEnchantmentRemoved.Count == 0)
                return;

            pending = _pendingEnchantmentRemoved.ToArray();
            _pendingEnchantmentRemoved.Clear();
        }

        foreach (PendingEnchantmentRemoved evt in pending)
        {
            for (int i = 0; i < _plugins.Count; i++)
            {
                var plugin = _plugins[i];
                if (!plugin.Initialized || plugin.Failed || plugin.OnEnchantmentRemoved == null)
                    continue;

                try
                {
                    plugin.OnEnchantmentRemoved(evt.EnchantmentId);
                }
                catch (Exception ex)
                {
                    plugin.Failed = true;
                    RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnEnchantmentRemoved threw {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    private static void LoadPluginsFromDisk()
    {
        _loadGeneration++;

        // Default directory
        var loaded = PluginLoader.LoadAll(_pluginsDir, _shadowRootDir, _loadGeneration);
        _plugins.AddRange(loaded);

        // Extra DLL paths from engine settings
        var extraPaths = EngineSettings.PluginPaths;
        for (int i = 0; i < extraPaths.Count; i++)
        {
            string dllPath = extraPaths[i];
            if (!File.Exists(dllPath))
            {
                RynthLog.Plugin($"PluginManager: Extra plugin not found: {dllPath}");
                continue;
            }
            RynthLog.Plugin($"PluginManager: Loading extra plugin: {dllPath}");
            var plugin = PluginLoader.LoadSingle(dllPath, _shadowRootDir, _loadGeneration);
            if (plugin != null)
                _plugins.Add(plugin);
        }
    }

    private static void InitializeLoadedPlugins()
    {
        if (_plugins.Count == 0)
        {
            RynthLog.Plugin("PluginManager: No plugins to initialize.");
            return;
        }

        EnsureHostCallbacks();
        RynthLog.Plugin($"PluginManager: Initializing {_plugins.Count} plugin(s)...");

        for (int i = 0; i < _plugins.Count; i++)
        {
            var plugin = _plugins[i];
            try
            {
                int result = plugin.Init!(ref _api);
                if (result == 0)
                {
                    plugin.Initialized = true;
                    plugin.Failed = false;
                    plugin.UIInitializedDispatched = false;
                    plugin.LoginCompleteDispatched = false;
                    RynthLog.Plugin($"PluginManager: {plugin.DisplayName} initialized OK (api v{PluginContractVersion.Current}).");
                }
                else
                {
                    plugin.Failed = true;
                    RynthLog.Plugin($"PluginManager: {plugin.DisplayName} Init returned {result} - marked as failed. (engine api version={PluginContractVersion.Current})");
                }
            }
            catch (Exception ex)
            {
                plugin.Failed = true;
                RynthLog.Plugin($"PluginManager: {plugin.DisplayName} Init threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (!_uiInitializedObserved)
            RynthLog.Plugin("PluginManager: Waiting for OnUIInitialized before starting UI-gated plugins.");

        if (!_loginCompleteObserved)
            RynthLog.Plugin("PluginManager: Waiting for OnLoginComplete before starting login-gated plugins.");
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
                    RynthLog.Plugin($"PluginManager: {plugin.DisplayName} shut down.");
                }
                catch (Exception ex)
                {
                    RynthLog.Plugin($"PluginManager: {plugin.DisplayName} Shutdown threw: {ex.Message}");
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

        for (int i = 0; i < _plugins.Count; i++)
        {
            var plugin = _plugins[i];
            if (!plugin.Initialized || plugin.Failed || plugin.OnUIInitialized == null || plugin.UIInitializedDispatched)
                continue;

            try
            {
                plugin.OnUIInitialized();
                plugin.UIInitializedDispatched = true;
                RynthLog.Plugin($"PluginManager: {plugin.DisplayName} received OnUIInitialized.");
            }
            catch (Exception ex)
            {
                plugin.Failed = true;
                RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnUIInitialized threw {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static void DispatchLoginCompleteToLoadedPlugins()
    {
        if (!_loginCompleteObserved || !_initialized)
            return;

        _loginDispatchPending = false;

        for (int i = 0; i < _plugins.Count; i++)
        {
            var plugin = _plugins[i];
            if (!plugin.Initialized || plugin.Failed || plugin.OnLoginComplete == null || plugin.LoginCompleteDispatched)
                continue;

            try
            {
                plugin.OnLoginComplete();
                plugin.LoginCompleteDispatched = true;
                RynthLog.Plugin($"PluginManager: {plugin.DisplayName} received OnLoginComplete.");
            }
            catch (Exception ex)
            {
                plugin.Failed = true;
                RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnLoginComplete threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Auto-identify the player to get exact max vitals from the CreatureProfile
        uint playerId = ClientHelperHooks.GetPlayerId();
        if (playerId != 0 && CombatActionHooks.HasRequestId)
        {
            CombatActionHooks.RequestId(playerId);
            RynthLog.Plugin($"PluginManager: sent self-identify for player 0x{playerId:X8} to seed max vitals.");
        }

        // Sync the actual current combat mode to newly initialized plugins.
        // Reads directly from ClientCombatSystem so this is accurate even at first inject.
        int actualCombatMode = CombatModeHooks.ReadCurrentCombatMode();
        QueueCombatModeChange(actualCombatMode, CombatActionHooks.CombatModeNonCombat);
        RynthLog.Plugin($"PluginManager: synced combat mode {actualCombatMode} to plugins.");
    }

    private static unsafe void EnsureHostCallbacks()
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
        _commenceJumpCallback ??= CommenceJump;
        _doJumpCallback ??= DoJump;
        _launchJumpWithMotionCallback ??= LaunchJumpWithMotion;
        _getRadarRectCallback ??= GetRadarRect;
        _setMotionCallback ??= SetMotion;
        _stopCompletelyCallback ??= StopCompletely;
        _turnToHeadingCallback ??= TurnToHeading;
        _getPlayerHeadingCallback ??= GetPlayerHeading;
        _setIncomingChatSuppressionCallback ??= SetIncomingChatSuppression;
        _selectItemCallback ??= SelectItem;
        _setSelectedObjectIdCallback ??= SetSelectedObjectId;
        _getSelectedItemIdCallback ??= GetSelectedItemId;
        _getPreviousSelectedItemIdCallback ??= GetPreviousSelectedItemId;
        _getPlayerIdCallback ??= GetPlayerId;
        _getGroundContainerIdCallback ??= GetGroundContainerId;
        _getNumContainedItemsCallback ??= GetNumContainedItemsAction;
        _getNumContainedContainersCallback ??= GetNumContainedContainersAction;
        _getCurCoordsCallback ??= GetCurCoords;
        _useObjectCallback ??= UseObject;
        _useObjectOnCallback ??= UseObjectOn;
        _useEquippedItemCallback ??= UseEquippedItem;
        _moveItemExternalCallback ??= MoveItemExternal;
        _moveItemInternalCallback ??= MoveItemInternal;
        _splitStackInternalCallback ??= SplitStackInternal;
        _mergeStackInternalCallback ??= MergeStackInternal;
        _writeToChatCallback ??= WriteToChat;
        _getPlayerPoseCallback ??= GetPlayerPose;
        _isPortalingCallback ??= IsPortalingCallback;
        _getObjectNameCallback ??= GetObjectName;
        _getPlayerVitalsCallback ??= GetPlayerVitals;
        _getObjectPositionCallback ??= GetObjectPosition;
        _requestIdCallback ??= RequestIdAction;
        _getTargetVitalsCallback ??= GetTargetVitals;
        _castSpellCallback ??= CastSpellAction;
        _getItemTypeCallback ??= GetItemTypeAction;
        _getObjectIntPropertyCallback ??= GetObjectIntPropertyAction;
        _getObjectBoolPropertyCallback ??= GetObjectBoolPropertyAction;
        _objectIsAttackableCallback ??= ObjectIsAttackableAction;
        _getObjectSkillCallback ??= GetObjectSkillAction;
        _isSpellKnownCallback ??= IsSpellKnownAction;
        _readPlayerEnchantmentsCallback ??= ReadPlayerEnchantmentsAction;
        _getServerTimeCallback ??= GetServerTimeAction;
        _readObjectEnchantmentsCallback ??= ReadObjectEnchantmentsAction;
        _worldToScreenCallback ??= D3D9.GameMatrixCapture.WorldToScreenCallback;
        _getViewportSizeCallback ??= D3D9.GameMatrixCapture.GetViewportSizeCallback;
        _nav3DClearCallback ??= D3D9.Nav3DRenderer.Nav3DClearCallback;
        _nav3DAddRingCallback ??= D3D9.Nav3DRenderer.Nav3DAddRingCallback;
        _nav3DAddLineCallback ??= D3D9.Nav3DRenderer.Nav3DAddLineCallback;
        _invokeChatParserCallback ??= InvokeChatParser;
        _getObjectDoublePropertyCallback ??= GetObjectDoublePropertyAction;
        _getObjectQuadPropertyCallback ??= GetObjectQuadPropertyAction;
        _getObjectAttribute2ndBaseLevelCallback ??= GetObjectAttribute2ndBaseLevelAction;
        _getPlayerBaseVitalsCallback ??= GetPlayerBaseVitalsAction;
        _getObjectStringPropertyCallback ??= GetObjectStringPropertyAction;
        _getObjectWielderInfoCallback ??= GetObjectWielderInfoAction;
        _nativeAttackCallback ??= NativeAttackAction;
        _isPlayerReadyCallback ??= IsPlayerReadyAction;
        _setFpsLimitCallback ??= SetFpsLimitAction;
        _getContainerContentsCallback ??= GetContainerContentsAction;
        _getObjectOwnershipInfoCallback ??= GetObjectOwnershipInfoAction;
        _getCurrentCombatModeCallback ??= GetCurrentCombatModeAction;
        _salvagePanelOpenCallback ??= SalvagePanelOpenAction;
        _salvagePanelAddItemCallback ??= SalvagePanelAddItemAction;
        _salvagePanelExecuteCallback ??= SalvagePanelExecuteAction;
        _getVitaeCallback ??= GetVitaeAction;
        _getAccountNameCallback ??= GetAccountNameAction;
        _getWorldNameCallback ??= GetWorldNameAction;
        _getObjectWcidCallback ??= GetObjectWcidAction;
        _hasAppraisalDataCallback ??= HasAppraisalDataAction;
        _getLastIdTimeCallback ??= GetLastIdTimeAction;
        _getObjectHeadingCallback ??= GetObjectHeadingAction;
        _getBusyStateCallback ??= GetBusyStateAction;
        _forceResetBusyCountCallback ??= ForceResetBusyCountAction;
        _getObjectSpellIdsCallback ??= GetObjectSpellIdsAction;
        _getObjectSkillBuffedCallback ??= GetObjectSkillLevelAction;
        _getObjectAttributeCallback ??= GetObjectAttributeAction;
        _getObjectMotionOnCallback ??= GetObjectMotionOnAction;
        _getObjectStateCallback ??= GetObjectStateAction;
        _getObjectBitfieldCallback ??= GetObjectBitfieldAction;

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
        _api.SetMotionFn = Marshal.GetFunctionPointerForDelegate(_setMotionCallback);
        _api.StopCompletelyFn = Marshal.GetFunctionPointerForDelegate(_stopCompletelyCallback);
        _api.TurnToHeadingFn = Marshal.GetFunctionPointerForDelegate(_turnToHeadingCallback);
        _api.GetPlayerHeadingFn = Marshal.GetFunctionPointerForDelegate(_getPlayerHeadingCallback);
        _api.SetIncomingChatSuppressionFn = Marshal.GetFunctionPointerForDelegate(_setIncomingChatSuppressionCallback);
        _api.SelectItemFn = Marshal.GetFunctionPointerForDelegate(_selectItemCallback);
        _api.SetSelectedObjectIdFn = Marshal.GetFunctionPointerForDelegate(_setSelectedObjectIdCallback);
        _api.GetSelectedItemIdFn = Marshal.GetFunctionPointerForDelegate(_getSelectedItemIdCallback);
        _api.GetPreviousSelectedItemIdFn = Marshal.GetFunctionPointerForDelegate(_getPreviousSelectedItemIdCallback);
        _api.GetPlayerIdFn = Marshal.GetFunctionPointerForDelegate(_getPlayerIdCallback);
        _api.GetGroundContainerIdFn = Marshal.GetFunctionPointerForDelegate(_getGroundContainerIdCallback);
        _api.GetNumContainedItemsFn = Marshal.GetFunctionPointerForDelegate(_getNumContainedItemsCallback);
        _api.GetNumContainedContainersFn = Marshal.GetFunctionPointerForDelegate(_getNumContainedContainersCallback);
        _api.GetCurCoordsFn = Marshal.GetFunctionPointerForDelegate(_getCurCoordsCallback);
        _api.UseObjectFn = Marshal.GetFunctionPointerForDelegate(_useObjectCallback);
        _api.UseObjectOnFn = Marshal.GetFunctionPointerForDelegate(_useObjectOnCallback);
        _api.UseEquippedItemFn = Marshal.GetFunctionPointerForDelegate(_useEquippedItemCallback);
        _api.MoveItemExternalFn = Marshal.GetFunctionPointerForDelegate(_moveItemExternalCallback);
        _api.MoveItemInternalFn = Marshal.GetFunctionPointerForDelegate(_moveItemInternalCallback);
        _api.WriteToChatFn = Marshal.GetFunctionPointerForDelegate(_writeToChatCallback);
        _api.GetPlayerPoseFn = Marshal.GetFunctionPointerForDelegate(_getPlayerPoseCallback);
        _api.IsPortalingFn = Marshal.GetFunctionPointerForDelegate(_isPortalingCallback);
        _api.GetObjectNameFn = Marshal.GetFunctionPointerForDelegate(_getObjectNameCallback);
        _api.GetPlayerVitalsFn = Marshal.GetFunctionPointerForDelegate(_getPlayerVitalsCallback);
        _api.GetObjectPositionFn = Marshal.GetFunctionPointerForDelegate(_getObjectPositionCallback);
        _api.RequestIdFn = Marshal.GetFunctionPointerForDelegate(_requestIdCallback);
        _api.GetTargetVitalsFn = Marshal.GetFunctionPointerForDelegate(_getTargetVitalsCallback);
        _api.CastSpellFn = Marshal.GetFunctionPointerForDelegate(_castSpellCallback);
        _api.GetItemTypeFn = Marshal.GetFunctionPointerForDelegate(_getItemTypeCallback);
        _api.GetObjectIntPropertyFn = Marshal.GetFunctionPointerForDelegate(_getObjectIntPropertyCallback);
        _api.GetObjectBoolPropertyFn = Marshal.GetFunctionPointerForDelegate(_getObjectBoolPropertyCallback);
        _api.ObjectIsAttackableFn = Marshal.GetFunctionPointerForDelegate(_objectIsAttackableCallback);
        _api.GetObjectSkillFn = Marshal.GetFunctionPointerForDelegate(_getObjectSkillCallback);
        _api.IsSpellKnownFn = Marshal.GetFunctionPointerForDelegate(_isSpellKnownCallback);
        _api.ReadPlayerEnchantmentsFn = Marshal.GetFunctionPointerForDelegate(_readPlayerEnchantmentsCallback);
        _api.GetServerTimeFn = Marshal.GetFunctionPointerForDelegate(_getServerTimeCallback);
        _api.ReadObjectEnchantmentsFn = Marshal.GetFunctionPointerForDelegate(_readObjectEnchantmentsCallback);
        _api.WorldToScreenFn = Marshal.GetFunctionPointerForDelegate(_worldToScreenCallback);
        _api.GetViewportSizeFn = Marshal.GetFunctionPointerForDelegate(_getViewportSizeCallback);
        _api.Nav3DClearFn = Marshal.GetFunctionPointerForDelegate(_nav3DClearCallback);
        _api.Nav3DAddRingFn = Marshal.GetFunctionPointerForDelegate(_nav3DAddRingCallback);
        _api.Nav3DAddLineFn = Marshal.GetFunctionPointerForDelegate(_nav3DAddLineCallback);
        _api.InvokeChatParserFn = Marshal.GetFunctionPointerForDelegate(_invokeChatParserCallback);
        _api.GetObjectDoublePropertyFn = Marshal.GetFunctionPointerForDelegate(_getObjectDoublePropertyCallback);
        _api.GetObjectQuadPropertyFn = Marshal.GetFunctionPointerForDelegate(_getObjectQuadPropertyCallback);
        _api.GetObjectAttribute2ndBaseLevelFn = Marshal.GetFunctionPointerForDelegate(_getObjectAttribute2ndBaseLevelCallback);
        _api.GetPlayerBaseVitalsFn = Marshal.GetFunctionPointerForDelegate(_getPlayerBaseVitalsCallback);
        _api.GetObjectStringPropertyFn = Marshal.GetFunctionPointerForDelegate(_getObjectStringPropertyCallback);
        _api.GetObjectWielderInfoFn = Marshal.GetFunctionPointerForDelegate(_getObjectWielderInfoCallback);
        _api.NativeAttackFn = Marshal.GetFunctionPointerForDelegate(_nativeAttackCallback);
        _api.IsPlayerReadyFn = Marshal.GetFunctionPointerForDelegate(_isPlayerReadyCallback);
        _api.SetFpsLimitFn = Marshal.GetFunctionPointerForDelegate(_setFpsLimitCallback);
        _api.GetContainerContentsFn = Marshal.GetFunctionPointerForDelegate(_getContainerContentsCallback);
        _api.GetObjectOwnershipInfoFn = Marshal.GetFunctionPointerForDelegate(_getObjectOwnershipInfoCallback);
        _api.SplitStackInternalFn = Marshal.GetFunctionPointerForDelegate(_splitStackInternalCallback);
        _api.MergeStackInternalFn = Marshal.GetFunctionPointerForDelegate(_mergeStackInternalCallback);
        _api.GetCurrentCombatModeFn = Marshal.GetFunctionPointerForDelegate(_getCurrentCombatModeCallback);
        _api.SalvagePanelOpenFn = Marshal.GetFunctionPointerForDelegate(_salvagePanelOpenCallback);
        _api.SalvagePanelAddItemFn = Marshal.GetFunctionPointerForDelegate(_salvagePanelAddItemCallback);
        _api.SalvagePanelExecuteFn = Marshal.GetFunctionPointerForDelegate(_salvagePanelExecuteCallback);
        _api.GetVitaeFn = Marshal.GetFunctionPointerForDelegate(_getVitaeCallback);
        _api.GetAccountNameFn = Marshal.GetFunctionPointerForDelegate(_getAccountNameCallback);
        _api.GetWorldNameFn = Marshal.GetFunctionPointerForDelegate(_getWorldNameCallback);
        _api.GetObjectWcidFn = Marshal.GetFunctionPointerForDelegate(_getObjectWcidCallback);
        _api.HasAppraisalDataFn = Marshal.GetFunctionPointerForDelegate(_hasAppraisalDataCallback);
        _api.GetLastIdTimeFn = Marshal.GetFunctionPointerForDelegate(_getLastIdTimeCallback);
        _api.GetObjectHeadingFn = Marshal.GetFunctionPointerForDelegate(_getObjectHeadingCallback);
        _api.GetBusyStateFn = Marshal.GetFunctionPointerForDelegate(_getBusyStateCallback);
        _api.GetObjectSpellIdsFn = Marshal.GetFunctionPointerForDelegate(_getObjectSpellIdsCallback);
        _api.GetObjectSkillBuffedFn = Marshal.GetFunctionPointerForDelegate(_getObjectSkillBuffedCallback);
        _api.GetObjectAttributeFn = Marshal.GetFunctionPointerForDelegate(_getObjectAttributeCallback);
        _api.GetObjectMotionOnFn = Marshal.GetFunctionPointerForDelegate(_getObjectMotionOnCallback);
        _api.GetObjectStateFn = Marshal.GetFunctionPointerForDelegate(_getObjectStateCallback);
        _api.GetObjectBitfieldFn = Marshal.GetFunctionPointerForDelegate(_getObjectBitfieldCallback);
        _api.ForceResetBusyCountFn = Marshal.GetFunctionPointerForDelegate(_forceResetBusyCountCallback);
        _getObjectPalettesCallback ??= GetObjectPalettesAction;
        _api.GetObjectPalettesFn = Marshal.GetFunctionPointerForDelegate(_getObjectPalettesCallback);
        _api.CommenceJumpFn = Marshal.GetFunctionPointerForDelegate(_commenceJumpCallback);
        _api.DoJumpFn = Marshal.GetFunctionPointerForDelegate(_doJumpCallback);
        _api.LaunchJumpWithMotionFn = Marshal.GetFunctionPointerForDelegate(_launchJumpWithMotionCallback);
        _api.GetRadarRectFn = Marshal.GetFunctionPointerForDelegate(_getRadarRectCallback);
    }

    private static void ProbeClientHooks()
    {
        ClientActionHooks.Probe();
        // ClientCombatHooks.Probe() is called during engine bootstrap (EntryPoint.cs)
        // — do NOT re-probe here, it uses GetDelegateForFunctionPointer (no MinHook).
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

        if (status.SetMotionAvailable)
            flags |= ClientActionHookFlags.SetMotion;

        if (status.GetPlayerHeadingAvailable)
            flags |= ClientActionHookFlags.GetPlayerHeading;

        if (status.StopCompletelyAvailable)
            flags |= ClientActionHookFlags.StopCompletely;

        if (status.TurnToHeadingAvailable)
            flags |= ClientActionHookFlags.TurnToHeading;

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

    private static int RequestIdAction(uint objectId)
    {
        return ToAbiBool(ClientActionHooks.RequestId(objectId));
    }

    private static int CastSpellAction(uint targetId, int spellId)
    {
        return ToAbiBool(ClientActionHooks.CastSpell(targetId, spellId));
    }

    private static unsafe int GetItemTypeAction(uint objectId, uint* typeFlags)
    {
        if (!ClientObjectHooks.TryGetItemType(objectId, out uint flags))
            return 0;

        *typeFlags = flags;
        return 1;
    }

    private static unsafe int GetObjectIntPropertyAction(uint objectId, uint stype, int* value)
    {
        if (!ClientObjectHooks.TryGetObjectIntProperty(objectId, stype, out int v))
            return 0;

        *value = v;
        return 1;
    }

    private static unsafe int GetObjectDoublePropertyAction(uint objectId, uint stype, double* value)
    {
        if (!ClientObjectHooks.TryGetObjectDoubleProperty(objectId, stype, out double v))
            return 0;

        *value = v;
        return 1;
    }

    private static unsafe int GetObjectQuadPropertyAction(uint objectId, uint stype, long* value)
    {
        if (!ClientObjectHooks.TryGetObjectQuadProperty(objectId, stype, out long v))
            return 0;

        *value = v;
        return 1;
    }

    private static unsafe int GetObjectAttribute2ndBaseLevelAction(uint objectId, uint stype2nd, uint* value)
    {
        if (!ClientObjectHooks.TryGetObjectAttribute2ndBaseLevel(objectId, stype2nd, out uint v))
            return 0;

        *value = v;
        return 1;
    }

    private static unsafe int GetPlayerBaseVitalsAction(uint* baseMaxHp, uint* baseMaxStam, uint* baseMaxMana)
    {
        if (!PlayerVitalsHooks.TryGetPlayerBaseVitals(out uint hp, out uint stam, out uint mana))
            return 0;

        *baseMaxHp = hp;
        *baseMaxStam = stam;
        *baseMaxMana = mana;
        return 1;
    }

    private static unsafe int GetObjectBoolPropertyAction(uint objectId, uint stype, int* value)
    {
        if (!ClientObjectHooks.TryGetObjectBoolProperty(objectId, stype, out bool v))
            return 0;

        *value = v ? 1 : 0;
        return 1;
    }

    [ThreadStatic] private static IntPtr _stringPropertyScratchPtr;

    private static IntPtr GetObjectStringPropertyAction(uint objectId, uint stype)
    {
        if (!ClientObjectHooks.TryGetObjectStringProperty(objectId, stype, out string v) || string.IsNullOrEmpty(v))
            return IntPtr.Zero;

        if (_stringPropertyScratchPtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_stringPropertyScratchPtr);
            _stringPropertyScratchPtr = IntPtr.Zero;
        }

        _stringPropertyScratchPtr = Marshal.StringToHGlobalAnsi(v);
        return _stringPropertyScratchPtr;
    }

    private static int ObjectIsAttackableAction(uint objectId)
    {
        return ClientObjectHooks.ObjectIsAttackable(objectId) ? 1 : 0;
    }

    private static unsafe int GetObjectWielderInfoAction(uint objectId, uint* wielderID, uint* location)
    {
        if (!ClientObjectHooks.TryGetObjectWielderInfo(objectId, out uint wid, out uint loc))
            return 0;
        *wielderID = wid;
        *location = loc;
        return 1;
    }

    private static unsafe int GetObjectOwnershipInfoAction(uint objectId, uint* containerID, uint* wielderID, uint* location)
    {
        if (containerID == null || wielderID == null || location == null)
            return 0;

        if (!ClientObjectHooks.TryGetObjectOwnershipInfo(objectId, out uint cid, out uint wid, out uint loc))
            return 0;

        *containerID = cid;
        *wielderID = wid;
        *location = loc;
        return 1;
    }

    private static int GetCurrentCombatModeAction() => CombatModeHooks.ReadCurrentCombatMode();

    private static int SalvagePanelOpenAction(uint toolId)
        => ToAbiBool(ClientHelperHooks.SalvagePanelOpen(toolId));

    private static int SalvagePanelAddItemAction(uint itemId)
        => ToAbiBool(ClientHelperHooks.SalvagePanelAddItem(itemId));

    private static int SalvagePanelExecuteAction()
        => ToAbiBool(ClientHelperHooks.SalvagePanelExecute());

    private static unsafe int GetContainerContentsAction(uint containerId, uint* itemIds, int maxCount)
    {
        RynthLog.Verbose($"Compat: GetContainerContentsAction ENTRY id=0x{containerId:X8} itemIds=0x{(int)itemIds:X8} maxCount={maxCount}");
        if (itemIds == null || maxCount <= 0)
            return 0;

        return UpdateObjectInventoryHooks.GetContainerContents(containerId, itemIds, maxCount);
    }

    private static unsafe int GetObjectSkillAction(uint objectId, uint skillStype, int* buffed, int* training)
    {
        if (!ClientObjectHooks.TryGetObjectSkill(objectId, skillStype, out int b, out int t))
            return 0;

        *buffed = b;
        *training = t;
        return 1;
    }

    private static unsafe int GetObjectSkillLevelAction(uint objectId, uint skillStype, int raw, int* level)
    {
        if (!ClientObjectHooks.TryGetObjectSkillLevel(objectId, skillStype, raw, out int v))
            return 0;
        *level = v;
        return 1;
    }

    private static unsafe int GetObjectAttributeAction(uint objectId, uint stype, int raw, uint* value)
    {
        if (!ClientObjectHooks.TryGetObjectAttribute(objectId, stype, raw, out uint v))
            return 0;
        *value = v;
        return 1;
    }

    private static unsafe int GetObjectMotionOnAction(uint objectId, int* isOn)
    {
        if (!DoMotionHooks.TryGetObjectMotionOn(objectId, out bool v))
            return 0;
        *isOn = v ? 1 : 0;
        return 1;
    }

    private static unsafe int GetObjectStateAction(uint objectId, uint* state)
    {
        if (!ClientObjectHooks.TryGetObjectPhysicsState(objectId, out uint s))
            return 0;
        *state = s;
        return 1;
    }

    private static unsafe int ReadPlayerEnchantmentsAction(uint* spellIds, double* expiryTimes, int maxCount)
    {
        return EnchantmentHooks.ReadPlayerEnchantments(spellIds, expiryTimes, maxCount);
    }

    private static unsafe int ReadObjectEnchantmentsAction(uint objectId, uint* spellIds, double* expiryTimes, int maxCount)
    {
        return EnchantmentHooks.ReadObjectEnchantments(objectId, spellIds, expiryTimes, maxCount);
    }

    private static double GetServerTimeAction()
    {
        return TimeSyncHooks.GetCurrentServerTime();
    }

    private static int IsSpellKnownAction(uint objectId, uint spellId)
    {
        if (!ClientObjectHooks.TryIsSpellKnown(objectId, spellId, out bool known))
            return -1; // API unavailable — caller should treat as unknown
        return known ? 1 : 0;
    }

    private static int MeleeAttack(uint targetId, int attackHeight, float powerLevel)
    {
        return ToAbiBool(ClientActionHooks.MeleeAttack(targetId, attackHeight, powerLevel));
    }

    private static int MissileAttack(uint targetId, int attackHeight, float accuracyLevel)
    {
        return ToAbiBool(ClientActionHooks.MissileAttack(targetId, attackHeight, accuracyLevel));
    }

    private static int NativeAttackAction(int attackHeight, float power)
    {
        return ToAbiBool(ClientCombatHooks.NativeAttack(attackHeight, power));
    }

    private static int IsPlayerReadyAction()
    {
        return ToAbiBool(ClientCombatHooks.IsPlayerReady());
    }

    private static void SetFpsLimitAction(int enabled, int focusedFps, int backgroundFps)
    {
        EndSceneHook.FpsLimitEnabled = enabled != 0;
        EndSceneHook.FpsTargetFocused = Math.Clamp(focusedFps, 1, 240);
        EndSceneHook.FpsTargetBackground = Math.Clamp(backgroundFps, 1, 240);
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

    private static int CommenceJump()
    {
        return ToAbiBool(ClientActionHooks.CommenceJump());
    }

    private static int DoJump(int autonomous)
    {
        return ToAbiBool(ClientActionHooks.DoJump(autonomous != 0));
    }

    private static int LaunchJumpWithMotion(int shift, int w, int x, int z, int c)
    {
        return ToAbiBool(ClientActionHooks.LaunchJumpWithMotion(shift != 0, w != 0, x != 0, z != 0, c != 0));
    }

    private static unsafe int GetRadarRect(int* x0, int* y0, int* x1, int* y1)
    {
        if (x0 == null || y0 == null || x1 == null || y1 == null)
            return 0;

        if (!RadarHooks.TryGetRadarRect(out int rx0, out int ry0, out int rx1, out int ry1))
            return 0;

        *x0 = rx0; *y0 = ry0; *x1 = rx1; *y1 = ry1;
        return 1;
    }

    private static int SetMotion(uint motion, int enabled)
    {
        return ToAbiBool(ClientActionHooks.SetMotion(motion, enabled != 0));
    }

    private static int StopCompletely()
    {
        return ToAbiBool(ClientActionHooks.StopCompletely());
    }

    private static int TurnToHeading(float headingDegrees)
    {
        return ToAbiBool(ClientActionHooks.TurnToHeading(headingDegrees));
    }

    private static unsafe int GetPlayerHeading(float* headingDegrees)
    {
        if (headingDegrees == null)
            return 0;

        bool success = ClientActionHooks.TryGetPlayerHeading(out float value);
        if (success)
            *headingDegrees = value;

        return ToAbiBool(success);
    }

    private static void SetIncomingChatSuppression(int enabled)
    {
        ChatCallbackHooks.SetIncomingChatSuppression(enabled != 0);
    }

    private static int SelectItem(uint objectId)
    {
        return ToAbiBool(ClientHelperHooks.SelectItem(objectId));
    }

    private static int SetSelectedObjectId(uint objectId)
    {
        return ToAbiBool(ClientHelperHooks.SetSelectedObjectId(objectId));
    }

    private static uint GetSelectedItemId()
    {
        return ClientHelperHooks.GetSelectedItemId();
    }

    private static uint GetPreviousSelectedItemId()
    {
        return ClientHelperHooks.GetPreviousSelectedItemId();
    }

    private static uint GetPlayerId()
    {
        return ClientHelperHooks.GetPlayerId();
    }

    private static uint GetGroundContainerId()
    {
        return ClientHelperHooks.GetGroundContainerId();
    }

    private static int GetNumContainedItemsAction(uint objectId)
    {
        return ClientObjectHooks.GetNumContainedItems(objectId);
    }

    private static int GetNumContainedContainersAction(uint objectId)
    {
        return ClientObjectHooks.GetNumContainedContainers(objectId);
    }

    private static unsafe int GetCurCoords(double* northSouth, double* eastWest)
    {
        if (northSouth == null || eastWest == null)
            return 0;

        // Prefer live coordinates from SmartBox physics pose — InqPlayerCoords
        // returns stale/cached data that doesn't update while running.
        if (PlayerPhysicsHooks.TryGetLiveCoords(out *northSouth, out *eastWest))
            return 1;

        return ToAbiBool(ClientHelperHooks.TryGetCurCoords(out *northSouth, out *eastWest));
    }

    private static int UseObject(uint objectId)
    {
        return ToAbiBool(ClientHelperHooks.UseObject(objectId));
    }

    private static int UseObjectOn(uint sourceObjectId, uint targetObjectId)
    {
        return ToAbiBool(ClientHelperHooks.UseObjectOn(sourceObjectId, targetObjectId));
    }

    private static int UseEquippedItem(uint sourceObjectId, uint targetObjectId)
    {
        return ToAbiBool(ClientHelperHooks.UseEquippedItem(sourceObjectId, targetObjectId));
    }

    private static int MoveItemExternal(uint objectId, uint targetContainerId, int amount)
    {
        return ToAbiBool(ClientHelperHooks.MoveItemExternal(objectId, targetContainerId, amount));
    }

    private static int MoveItemInternal(uint objectId, uint targetContainerId, int slot, int amount)
    {
        return ToAbiBool(ClientHelperHooks.MoveItemInternal(objectId, targetContainerId, slot, amount));
    }

    private static int SplitStackInternal(uint objectId, uint targetContainerId, int slot, int amount)
    {
        return ToAbiBool(ClientHelperHooks.SplitStackInternal(objectId, targetContainerId, slot, amount));
    }

    private static int MergeStackInternal(uint sourceObjectId, uint targetObjectId)
    {
        return ToAbiBool(ClientHelperHooks.MergeStackInternal(sourceObjectId, targetObjectId));
    }

    private static int WriteToChat(IntPtr textUtf16, int chatType)
    {
        string? text = textUtf16 != IntPtr.Zero ? Marshal.PtrToStringUni(textUtf16) : null;
        return ToAbiBool(ClientHelperHooks.WriteToChat(text, chatType));
    }

    private static int InvokeChatParser(IntPtr textUtf16)
    {
        string? text = textUtf16 != IntPtr.Zero ? Marshal.PtrToStringUni(textUtf16) : null;
        return ToAbiBool(ClientHelperHooks.InvokeParser(text));
    }

    private static int IsPortalingCallback()
    {
        return TeleportStateHooks.IsPortaling ? 1 : 0;
    }

    private static float GetVitaeAction(uint playerId)
    {
        return ClientObjectHooks.TryGetVitae(playerId, out float v) ? v : 1.0f;
    }

    private static IntPtr GetAccountNameAction()
    {
        if (!AccountHooks.TryGetAccountName(out string name) || string.IsNullOrEmpty(name))
            return IntPtr.Zero;

        if (_accountNameScratchPtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_accountNameScratchPtr);
            _accountNameScratchPtr = IntPtr.Zero;
        }

        _accountNameScratchPtr = Marshal.StringToHGlobalAnsi(name);
        return _accountNameScratchPtr;
    }

    private static IntPtr GetWorldNameAction()
    {
        if (!AccountHooks.TryGetWorldName(out string name) || string.IsNullOrEmpty(name))
            return IntPtr.Zero;

        if (_worldNameScratchPtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_worldNameScratchPtr);
            _worldNameScratchPtr = IntPtr.Zero;
        }

        _worldNameScratchPtr = Marshal.StringToHGlobalAnsi(name);
        return _worldNameScratchPtr;
    }

    private static uint GetObjectWcidAction(uint objectId)
    {
        return ClientObjectHooks.TryGetObjectWcid(objectId, out uint wcid) ? wcid : 0u;
    }

    private static uint GetObjectBitfieldAction(uint objectId)
    {
        return ClientObjectHooks.TryGetObjectBitfield(objectId, out uint bitfield) ? bitfield : 0u;
    }

    private static unsafe int GetObjectPalettesAction(uint objectId, uint* subIds, uint* offsets, int maxCount)
    {
        if (subIds == null || offsets == null || maxCount <= 0) return -1;
        return PaletteCache.Fill(objectId, subIds, offsets, maxCount);
    }

    private static int HasAppraisalDataAction(uint objectId)
    {
        return AppraisalHooks.HasAppraisalData(objectId) ? 1 : 0;
    }

    private static long GetLastIdTimeAction(uint objectId)
    {
        return AppraisalHooks.GetLastIdTime(objectId);
    }

    private static unsafe int GetObjectHeadingAction(uint objectId, float* headingDegrees)
    {
        if (!ClientObjectHooks.TryGetObjectHeading(objectId, out float h))
            return 0;
        *headingDegrees = h;
        return 1;
    }

    private static int GetBusyStateAction() => BusyCountHooks.GetBusyState();

    private static void ForceResetBusyCountAction() => BusyCountHooks.ForceResetBusyCount();

    private static unsafe int GetObjectSpellIdsAction(uint guid, uint* spellIds, int maxCount)
    {
        if (spellIds == null || maxCount <= 0)
            return -1;
        var output = new uint[maxCount];
        int result = AppraisalHooks.GetObjectSpellIds(guid, output, maxCount);
        if (result > 0)
        {
            int count = Math.Min(result, maxCount);
            for (int i = 0; i < count; i++)
                spellIds[i] = output[i];
        }
        return result;
    }

    private static unsafe int GetPlayerPose(
        uint* objCellId,
        float* x,
        float* y,
        float* z,
        float* qw,
        float* qx,
        float* qy,
        float* qz)
    {
        if (objCellId == null || x == null || y == null || z == null || qw == null || qx == null || qy == null || qz == null)
            return 0;

        bool success = PlayerPhysicsHooks.TryGetPlayerPose(
            out *objCellId,
            out *x,
            out *y,
            out *z,
            out *qw,
            out *qx,
            out *qy,
            out *qz);

        return ToAbiBool(success);
    }

    private static IntPtr GetObjectName(uint objectId)
    {
        if (!ClientActionHooks.TryGetObjectName(objectId, out string name) || string.IsNullOrWhiteSpace(name))
            return IntPtr.Zero;

        if (_objectNameScratchPtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_objectNameScratchPtr);
            _objectNameScratchPtr = IntPtr.Zero;
        }

        _objectNameScratchPtr = Marshal.StringToHGlobalAnsi(name);
        return _objectNameScratchPtr;
    }

    private static unsafe int GetPlayerVitals(
        uint* health,
        uint* maxHealth,
        uint* stamina,
        uint* maxStamina,
        uint* mana,
        uint* maxMana)
    {
        if (health == null || maxHealth == null || stamina == null || maxStamina == null || mana == null || maxMana == null)
            return 0;

        bool success = PlayerVitalsHooks.TryGetSnapshot(out PlayerVitalsSnapshot snapshot);
        if (success)
        {
            *health = snapshot.Health;
            *maxHealth = snapshot.MaxHealth;
            *stamina = snapshot.Stamina;
            *maxStamina = snapshot.MaxStamina;
            *mana = snapshot.Mana;
            *maxMana = snapshot.MaxMana;
        }

        return ToAbiBool(success);
    }

    private static unsafe int GetTargetVitals(
        uint objectId,
        uint* health,
        uint* maxHealth,
        uint* stamina,
        uint* maxStamina,
        uint* mana,
        uint* maxMana)
    {
        if (health == null || maxHealth == null || stamina == null || maxStamina == null || mana == null || maxMana == null)
            return 0;

        bool success = ObjectQualityCache.TryGetCreatureVitals(objectId, out CreatureVitals vitals);
        if (success)
        {
            *health = vitals.Health;
            *maxHealth = vitals.MaxHealth;
            *stamina = vitals.Stamina;
            *maxStamina = vitals.MaxStamina;
            *mana = vitals.Mana;
            *maxMana = vitals.MaxMana;
        }

        return ToAbiBool(success);
    }

    private static unsafe int GetObjectPosition(
        uint objectId,
        uint* objCellId,
        float* x,
        float* y,
        float* z)
    {
        if (objCellId == null || x == null || y == null || z == null)
            return 0;

        bool success = ClientObjectHooks.TryGetObjectPosition(objectId, out uint cell, out float ox, out float oy, out float oz);
        if (success)
        {
            *objCellId = cell;
            *x = ox;
            *y = oy;
            *z = oz;
        }

        return ToAbiBool(success);
    }

    private static int ToAbiBool(bool value)
    {
        return value ? 1 : 0;
    }
}
