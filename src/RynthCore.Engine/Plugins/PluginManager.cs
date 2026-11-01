// ============================================================================
//  RynthCore.Engine - Plugins/PluginManager.cs
//  Orchestrates plugin lifecycle: load, init, login-ready callbacks, tick,
//  render, rescan, and shutdown.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.UI.ScriptWindows;
using RynthCore.Engine.D3D9;
using RynthCore.PluginSdk.Manifest;

namespace RynthCore.Engine.Plugins;

internal static partial class PluginManager
{
    // Held: set when AC hasn't printed the line yet (ChatCallbackHooks holds it until the
    // plugins' verdict is in; see IncomingChatHold.cs). Null for lines AC already printed.
    private readonly record struct PendingIncomingChat(string? Text, uint ChatType, HeldChatLine? Held);
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
    private readonly record struct PendingCombatDamage(uint Damage, uint DamageType, uint Crit, uint IsAttacker);
    private readonly record struct PendingKillNotification(string? DeathMessage);
    private readonly record struct PendingEnchantmentAdded(uint SpellId, double DurationSeconds);
    private readonly record struct PendingEnchantmentRemoved(uint EnchantmentId);

    // A /myquests reply is one line per quest flag (hundreds on a played character) and arrives
    // in one burst; a slow pump tick (a busy plugin window) must not drop its head. Lines are short
    // strings, so a deep queue costs little. Drops are counted and logged (DispatchQueuedChatWindowText).
    private const int MaxPendingIncomingChats = 4096;
    private static long _droppedIncomingChats;          // lines dropped unheard since the last log
    private static long _droppedIncomingChatsLoggedMs;  // when that was last logged
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
    private const int MaxPendingCombatDamage = 256;
    private const int MaxPendingKillNotifications = 64;
    private const int MaxPendingEnchantmentEvents = 256;
    private const int MaxPrePluginCreateObjects = 4096;
    private static readonly Queue<uint> _prePluginCreateObjects = new();
    private static readonly object PrePluginCreateObjectsLock = new();
    private static readonly HashSet<uint> _prePluginDeleteObjects = new();
    private static readonly object PrePluginDeleteObjectsLock = new();
    // Always-current set of live object IDs: create adds, delete removes.
    // Used by ReplayPrePluginCreateObjects so hot-reloads never replay stale/deleted objects.
    private static readonly HashSet<uint> _liveObjects = new();
    private static readonly object LiveObjectsLock = new();
    private static readonly List<LoadedPlugin> _plugins = new();
    // Deep-audit finding #20 (2026-06-18): RenderAll (AC render thread)
    // iterates _plugins directly while RescanPlugins -> UnloadAllPlugins/
    // LoadPluginsFromDisk (pump thread, via the RL/rescan button) does
    // Clear()/Add() with no lock — a torn List can throw
    // ArgumentOutOfRangeException mid-ImGui-frame. TickAll and every other
    // _plugins.Count/indexer call site run serialized with rescan on the
    // SAME pump thread, so only RenderAll (the one cross-thread reader)
    // needs this. Mirrors Nav3DRenderer's atomic double-buffer: mutators
    // publish a fresh array via Volatile.Write after every Clear()/Add(),
    // RenderAll snapshots the reference once and iterates that.
    private static LoadedPlugin[] _pluginsRenderSnapshot = Array.Empty<LoadedPlugin>();
    private static void PublishPluginsRenderSnapshot() => System.Threading.Volatile.Write(ref _pluginsRenderSnapshot, _plugins.ToArray());
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
    private static readonly Queue<PendingCombatDamage> _pendingCombatDamage = new();
    private static readonly Queue<PendingKillNotification> _pendingKillNotifications = new();
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
    private static readonly object PendingCombatDamageLock = new();
    private static readonly object PendingKillNotificationsLock = new();
    private static readonly object PendingEnchantmentAddedLock = new();
    private static readonly object PendingEnchantmentRemovedLock = new();
    private static bool _loaded;
    private static bool _initialized;
    private static bool _rescanRequested;
    private static bool _uiInitializedObserved;
    private static bool _uiDispatchPending;
    private static bool _loginCompleteObserved;

    // Delayed login self-identify (crash fix 2026-06-09). Sending RequestId(player)
    // at LoginComplete makes AC re-process the full player qualities and re-run its
    // vital-UI update path (gmVitalsUI::Update) while that UI may still be
    // initialising → an intermittent null-deref AV (39076/13648). Arm a deadline
    // here and let TickAll fire it once AC has settled past the init window.
    private static long _selfIdentifyDueTick;
    private const long SelfIdentifyDelayMs = 15_000;
    private static bool _loginDispatchPending;
    private static bool _logoutDispatchPending;
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
    private static SetRadarSuppressedCallbackDelegate? _setRadarSuppressedCallback;
    private static SetChatSuppressedCallbackDelegate? _setChatSuppressedCallback;
    private static SetPowerbarSuppressedCallbackDelegate? _setPowerbarSuppressedCallback;
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
    private static GiveObjectToCallbackDelegate? _giveObjectToCallback;
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
    private static ReadKnownSpellsCallbackDelegate? _readKnownSpellsCallback;
    private static GetServerTimeCallbackDelegate? _getServerTimeCallback;
    private static ReadObjectEnchantmentsCallbackDelegate? _readObjectEnchantmentsCallback;
    private static WorldToScreenCallbackDelegate? _worldToScreenCallback;
    private static GetViewportSizeCallbackDelegate? _getViewportSizeCallback;
    private static Nav3DClearCallbackDelegate? _nav3DClearCallback;
    private static Nav3DAddRingCallbackDelegate? _nav3DAddRingCallback;
    private static Nav3DAddLineCallbackDelegate? _nav3DAddLineCallback;
    private static Nav3DAddTriangleCallbackDelegate? _nav3DAddTriangleCallback;
    private static Nav3DAddRingExCallbackDelegate? _nav3DAddRingExCallback;
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
    private static GetCastBusyStateCallbackDelegate? _getCastBusyStateCallback;
    private static GetUseDoneSeqCallbackDelegate? _getUseDoneSeqCallback;
    private static GetEngineStatusJsonCallbackDelegate? _getEngineStatusJsonCallback;
    private static GetPluginSnapshotJsonCallbackDelegate? _getPluginSnapshotJsonCallback;
    private static SendPluginCommandCallbackDelegate? _sendPluginCommandCallback;
    private static GetObjectDataIdPropertyCallbackDelegate? _getObjectDataIdPropertyCallback;
    private static GetPluginExportJsonCallbackDelegate? _getPluginExportJsonCallback;
    private static GetPluginInterfaceCallbackDelegate? _getPluginInterfaceCallback;
    private static GetLiveObjectIdsCallbackDelegate? _getLiveObjectIdsCallback;
    private static GetLastUseDoneCallbackDelegate? _getLastUseDoneCallback;
    private static GetLastWeenieErrorCallbackDelegate? _getLastWeenieErrorCallback;
    private static WieldItemCallbackDelegate? _wieldItemCallback;
    private static UiSubmitCallbackDelegate? _uiSubmitCallback;
    private static UiPollEventsCallbackDelegate? _uiPollEventsCallback;
    private static UiGetInfoCallbackDelegate? _uiGetInfoCallback;
    private static GetTradeStateCallbackDelegate? _getTradeStateCallback;
    private static GetTradeItemsCallbackDelegate? _getTradeItemsCallback;
    private static TradeOpenCallbackDelegate? _tradeOpenCallback;
    private static TradeAddCallbackDelegate? _tradeAddCallback;
    private static TradeSimpleCallbackDelegate? _tradeAcceptCallback;
    private static TradeSimpleCallbackDelegate? _tradeDeclineCallback;
    private static TradeSimpleCallbackDelegate? _tradeResetCallback;
    private static TradeSimpleCallbackDelegate? _tradeCloseCallback;
    private static GetObjectDataIdPropertyCallbackDelegate? _getObjectInstanceIdPropertyCallback;   // v73 (same shape)
    private static GetVTankStateCallbackDelegate? _getVTankStateCallback;   // v74
    private static GetCharacterTitlesCallbackDelegate? _getCharacterTitlesCallback;   // v75
    private static GetServerInfoCallbackDelegate? _getServerInfoCallback;             // v75
    private static CloseContainerCallbackDelegate? _closeContainerCallback;           // v76
    private static SetServerMessageInterestCallbackDelegate? _setServerMessageInterestCallback; // v78
    private static GetTrainingInfoCallbackDelegate? _getTrainingInfoCallback;       // v79
    private static RaiseCallbackDelegate? _raiseCallback;                           // v79
    private static TrainSkillCallbackDelegate? _trainSkillCallback;                 // v79
    private static GetContainerOrderCallbackDelegate? _getContainerOrderCallback;   // v80
    private static PutItemInContainerCallbackDelegate? _putItemInContainerCallback; // v80
    private static SalvageDirectCallbackDelegate? _salvageDirectCallback;           // v82
    private static GetObjectServerAgeCallbackDelegate? _getObjectServerAgeCallback; // v83
    private static RemoveClientObjectCallbackDelegate? _removeClientObjectCallback; // v83
    private static GetObjectPropertiesCallbackDelegate? _getObjectPropertiesCallback; // v84
    private static GetVendorInfoCallbackDelegate? _getVendorInfoCallback;
    private static GetVendorItemsCallbackDelegate? _getVendorItemsCallback;
    private static VendorBuyCallbackDelegate? _vendorBuyCallback;
    private static VendorSellCallbackDelegate? _vendorSellCallback;
    private static GetVendorTradeStatusCallbackDelegate? _getVendorTradeStatusCallback;
    private static GetMergeStackResultCallbackDelegate? _getMergeStackResultCallback;
    private static ArmConfirmationCallbackDelegate? _armConfirmationCallback;             // v85
    private static GetLastConfirmationCallbackDelegate? _getLastConfirmationCallback;     // v85
    private static GetServerStateJsonCallbackDelegate? _getServerStateJsonCallback;       // v86
    private static GetWieldedItemsCallbackDelegate? _getWieldedItemsCallback;             // v87
    private static GetUiColorCallbackDelegate? _getUiColorCallback;                       // v89
    private static GetUiFontCallbackDelegate? _getUiFontCallback;                         // v89
    private static SetPanelOpenCallbackDelegate? _setPanelOpenCallback;                   // v89
    private static PanelDragWithLastItemCallbackDelegate? _panelDragWithLastItemCallback; // v90
    private static SetPanelPopOutCallbackDelegate? _setPanelPopOutCallback;               // v90
    private static PanelTextSizeCallbackDelegate? _panelTextSizeCallback;                 // v90
    private static RenderSettingsDrawerCallbackDelegate? _renderSettingsDrawerCallback;   // v90
    private static GetShortcutsCallbackDelegate? _getShortcutsCallback;                   // v90
    private static ShortcutActionCallbackDelegate? _shortcutActionCallback;               // v90
    private static GetActionKeysCallbackDelegate? _getActionKeysCallback;                 // v90
    private static GetIconTextureCallbackDelegate? _getIconTextureCallback;               // v90
    private static KeyCaptureCallbackDelegate? _keyCaptureCallback;                       // v90
    private static SetActionKeyCallbackDelegate? _setActionKeyCallback;                   // v90
    private static AcPanelCallbackDelegate? _acPanelCallback;                             // v90
    private static PanelScaleCallbackDelegate? _panelScaleCallback;                       // v90
    private static DropTargetCallbackDelegate? _dropTargetCallback;                       // v90
    private static SetAmbientLightFloorCallbackDelegate? _setAmbientLightFloorCallback;   // v91
    private static GetAmbientLightCallbackDelegate? _getAmbientLightCallback;             // v91
    private static ForceResetBusyCountCallbackDelegate? _forceResetBusyCountCallback;
    private static GetObjectSpellIdsCallbackDelegate? _getObjectSpellIdsCallback;
    private static GetObjectSkillLevelCallbackDelegate? _getObjectSkillBuffedCallback;
    private static GetObjectAttributeCallbackDelegate? _getObjectAttributeCallback;
    private static GetObjectMotionOnCallbackDelegate? _getObjectMotionOnCallback;
    private static GetObjectStateCallbackDelegate? _getObjectStateCallback;
    private static GetObjectBitfieldCallbackDelegate? _getObjectBitfieldCallback;
    private static GetObjectPalettesCallbackDelegate? _getObjectPalettesCallback;
    // [ThreadStatic] like the sibling scratch pointers: these are host pulls
    // callable from any plugin/UI thread with a free-then-realloc pattern —
    // as plain statics, two concurrent callers could FreeHGlobal the same
    // pointer (double-free → the LFH heap-corruption class).
    [ThreadStatic] private static IntPtr _accountNameScratchPtr;
    [ThreadStatic] private static IntPtr _worldNameScratchPtr;
    [ThreadStatic] private static IntPtr _objectNameScratchPtr;
    [ThreadStatic] private static IntPtr _engineStatusJsonScratchPtr;
    private static string _pluginsDir = "";
    private static string _shadowRootDir = "";

    public static IReadOnlyList<LoadedPlugin> Plugins => _plugins;

    /// <summary>
    /// Raised after every plugin has been shut down and removed (RL / rescan, engine
    /// shutdown). Unload never FreeLibrary's a NativeAOT plugin, so an export bound
    /// before a reload stays callable — into the shut-down copy. Panels that cache
    /// plugin exports drop them here; their next poll re-binds to the fresh copy.
    /// Raised on the pump thread.
    /// </summary>
    public static event Action? PluginsUnloaded;
    public static bool IsRescanQueued => _rescanRequested;
    public static string PluginDirectory => _pluginsDir;
    public static IReadOnlyList<string> ExtraPluginPaths => EngineSettings.PluginPaths;

    /// <summary>Diagnostic: how many live objects have we observed via the
    /// CreateObject hook. Zero at plugin-init means our hook never caught
    /// the initial world load (likely either the hook didn't install or
    /// Decal intercepted the call without chaining).</summary>
    public static int LiveObjectCount
    {
        get
        {
            lock (LiveObjectsLock) return _liveObjects.Count;
        }
    }
    public static bool HasObservedUIInitialized => _uiInitializedObserved;
    public static bool HasObservedLoginComplete => _loginCompleteObserved;

    public static void LoadPlugins(string engineDir)
    {
        // Where the memory goes while the plugins start (Compatibility/MemoryUse.cs).
        Compatibility.MemoryUse.Mark("before the plugins load");
        Compatibility.MemoryUse.StartBurst(60, "plugins starting");
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

        LogoutLifecycleHooks.LogoutComplete -= OnLogoutObserved;
        LogoutLifecycleHooks.LogoutComplete += OnLogoutObserved;

        string normalizedEngineDir = Path.GetFullPath(engineDir);
        bool engineDirIsRuntime = string.Equals(
            Path.GetFileName(normalizedEngineDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            "Runtime",
            StringComparison.OrdinalIgnoreCase);

        _pluginsDir = engineDirIsRuntime
            ? Path.Combine(normalizedEngineDir, "Plugins")
            : Path.Combine(normalizedEngineDir, "Runtime", "Plugins");

        // Per-PID shadow root: each acclient.exe owns its own subtree under
        // .runtime/p<PID>/. Eliminates the cross-process FS contention that
        // produced multi-client load-time LFH AVs (failed Directory.Delete on
        // another client's mapped plugin DLL → heap-pressure → AC main thread
        // AV at ~5-6s). Also avoids the same-process hot-reload variant where
        // gen2's cleanup attempts to delete gen1's still-mapped DLL.
        string baseShadowRoot = Path.Combine(_pluginsDir, ".runtime");
        _shadowRootDir = Path.Combine(baseShadowRoot, $"p{Environment.ProcessId}");

        // Sweep orphans from previous-process runs (mutex-serialized across
        // processes so concurrent clients don't race on the same dead-PID
        // dirs). NOTE: intentionally do NOT call CleanupShadowCopies on
        // _shadowRootDir from here — on hot-reload the still-mapped previous-
        // gen plugin DLL would trip the same heap-hammer. Per-PID dir
        // contents accumulate during process lifetime; OS reclaims at exit.
        PluginLoader.CleanupOrphanShadowRoots(baseShadowRoot, Environment.ProcessId);
        RynthLog.Plugin("PluginManager.LoadPlugins DIAG — CleanupOrphanShadowRoots returned, calling LoadPluginsFromDisk.");
        LoadPluginsFromDisk();
        RynthLog.Plugin("PluginManager.LoadPlugins DIAG — LoadPluginsFromDisk returned, exiting LoadPlugins.");
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
        // SeedLiveObjectsFromCObjectMaint() disabled 2026-05-14.
        // The walk reads AC's CObjectMaint::weenie_object_table — a hash
        // bucket array AC's main thread also mutates in response to
        // CreateObject/DeleteObject packets. Even with our IsMemoryReadable
        // guards, a torn-read can pick up a node pointer that AC has just
        // freed, store its id in _liveObjects, and later the plugin queries
        // that id and reaches a now-recycled object whose vtable points
        // somewhere weird (.text address showing up as a "live" pointer in
        // AC's render iteration — exact pattern of the 5-min crash at
        // 0x00460C71 writing into .text). On hot reload PluginManager
        // misses the pre-load object stream but that's a smaller cost than
        // the long-run crash. ReplayPrePluginCreateObjects below still
        // covers events we did capture.
        // SeedLiveObjectsFromCObjectMaint();
        ReplayPrePluginCreateObjects();
    }

    /// <summary>
    /// Seed _liveObjects from AC's authoritative CObjectMaint::weenie_object_table.
    ///
    /// On hot-reload the new engine module's static state is empty — without this
    /// step ReplayPrePluginCreateObjects has nothing to send the freshly loaded
    /// plugin and combat/inventory/loot all start blind to the existing world.
    /// On cold-start it also catches any CreateObject events that fired before
    /// the plugin was ready or were dropped by queue overflow.
    /// </summary>
    private static void SeedLiveObjectsFromCObjectMaint()
    {
        var collected = new HashSet<uint>();
        int visited = CObjectMaintHooks.EnumerateLiveWeenieObjectIds(id => collected.Add(id));
        if (visited <= 0)
        {
            RynthLog.Plugin("PluginManager: CObjectMaint enumerate unavailable (maintainer not ready or table empty).");
            return;
        }

        int added = 0;
        lock (LiveObjectsLock)
        {
            foreach (uint id in collected)
                if (_liveObjects.Add(id)) added++;
        }
        RynthLog.Plugin($"PluginManager: Seeded {added} new live object id(s) from CObjectMaint (visited {visited}).");
    }

    private static int _cObjectMaintSeedDone;     // 0 = pending this login, 1 = done/given-up
    private static int _cObjectMaintSeedAttempts;
    private static long _cObjectMaintSeedLastMs;
    private static int _cObjectMaintSeedNotMainLogged;

    /// <summary>
    /// Cold-login object backfill. The incremental CreateObject hook only
    /// catches objects created AFTER it installed, so mobs already present at
    /// login are never delivered and the plugin's WorldObjectCache never sees
    /// them (the proven "bot ignores login mobs" bug — those ids show ZERO
    /// classify activity and only enter combat via manual select).
    /// SeedLiveObjectsFromCObjectMaint was disabled 2026-05-14 because walking
    /// AC's weenie_object_table OFF AC's thread torn-reads concurrently-freed
    /// nodes (the 5-min AV). Fix: the SAME walk, but ONLY on AC's main thread —
    /// AC mutates that table on its own main thread, so a same-thread walk is
    /// consistent — delivering ids through the existing guarded
    /// QueueCreateObject path (→ _liveObjects + _pendingCreateObjects →
    /// DispatchQueuedCreateObject → guarded plugin.OnCreateObject → guarded
    /// TryClassify). One-shot per login, throttled + attempt-bounded. Called
    /// from SmartBoxHooks.DispatchGameEventDetour (AC main thread, fires every
    /// server event in-world) exactly like BusyCountHooks.CheckWatchdog.
    /// </summary>
    public static void TrySeedLiveObjectsFromCObjectMaintOnce()
    {
        if (System.Threading.Volatile.Read(ref _cObjectMaintSeedDone) != 0) return;
        if (!_initialized || !_loginCompleteObserved || _plugins.Count == 0) return;
        // CRITICAL: never walk AC's object table off AC's main thread — that
        // off-thread torn read is the exact 5-min AV the 2026-05-14 disable
        // was protecting against. Called from OnEndScene, which the engine
        // already treats as AC's main thread (PrefetchPlayerSkills runs there
        // under the same guard).
        if (!MainThreadGuard.IsOnMainThread())
        {
            // One-shot breadcrumb: if this is the ONLY thing logged, the guard
            // never identifies the main thread on this acclient build → the
            // anchor problem is MainThreadGuard, not the seed logic.
            if (System.Threading.Interlocked.Exchange(ref _cObjectMaintSeedNotMainLogged, 1) == 0)
                RynthLog.Plugin("PluginManager: CObjectMaint seed gated — MainThreadGuard not yet on main thread at the EndScene anchor.");
            return;
        }

        long now = Environment.TickCount64;
        if (_cObjectMaintSeedLastMs != 0 && now - _cObjectMaintSeedLastMs < 2000) return;
        _cObjectMaintSeedLastMs = now;
        int attempt = ++_cObjectMaintSeedAttempts;
        if (attempt > 8)
        {
            System.Threading.Volatile.Write(ref _cObjectMaintSeedDone, 1);
            RynthLog.Plugin("PluginManager: CObjectMaint seed gave up after 8 main-thread attempts (maintainer/table never ready).");
            return;
        }

        int delivered = 0;
        int visited = CObjectMaintHooks.EnumerateLiveWeenieObjectIds(id =>
        {
            QueueCreateObject(id); // existing safe path; dedups via _liveObjects, plugin TryClassify is guarded
            delivered++;
        });
        RynthLog.Plugin($"PluginManager: CObjectMaint seed attempt {attempt}/8 — visited={visited} delivered={delivered}.");
        if (visited > 0)
            System.Threading.Volatile.Write(ref _cObjectMaintSeedDone, 1);
        // visited <= 0 → maintainer/table not ready yet; retry next frame (bounded by attempts)
    }

    private static void ReplayPrePluginCreateObjects()
    {
        // Replay the live-object set rather than the raw create queue.
        // _liveObjects is always current (create adds, delete removes), so it is correct
        // for both the first plugin load and every hot-reload — no stale pointer risk.
        uint[] live;
        lock (LiveObjectsLock)
        {
            if (_liveObjects.Count == 0) return;
            live = new uint[_liveObjects.Count];
            _liveObjects.CopyTo(live);
        }

        lock (PendingCreateObjectsLock)
        {
            foreach (uint objectId in live)
            {
                if (_pendingCreateObjects.Count >= MaxPendingCreateObjects)
                    _pendingCreateObjects.Dequeue();
                _pendingCreateObjects.Enqueue(new PendingCreateObject(objectId));
            }
        }

        RynthLog.Plugin($"PluginManager: Replaying {live.Length} live object(s) to new plugin.");
    }

    public static void RequestRescan()
    {
        if (string.IsNullOrWhiteSpace(_pluginsDir))
            return;

        _rescanRequested = true;
    }

    public static void ProcessPendingActions(IntPtr imguiContext, IntPtr d3dDevice, IntPtr gameHwnd)
    {
        // v77: Client::Cleanup started (UiFlowHooks): tell the plugins once, then no more events.
        if (ProcessClientCleanup())
            return;

        UiLifecycleHooks.Poll();
        LoginLifecycleHooks.Poll();
        SessionStateRegistry.Poll();

        // Drain logout BEFORE any per-frame plugin activity so plugin teardown
        // happens this frame and Tick/Render skip stale state.
        if (_logoutDispatchPending && _initialized)
            DispatchPendingLogout();
        // ⚠ Ordering matters for causality-paired event types (per-type queues
        // can't preserve true interleaving, but the common real pattern is
        // bump-then-clear / open-then-close, so drain the "first half" first):
        //   • Increment BEFORE Decrement — AC bumps busy then clears it; draining
        //     all-Decr-then-all-Incr made a same-frame incr→decr land as
        //     decr→incr and a zero-clamped plugin mirror net +1 (the busy-leak
        //     desync the reconciler/watchdogs then fought).
        //   • View BEFORE Stop — a container open-then-close in one frame must
        //     end "closed"; draining Stop-then-View left the plugin believing the
        //     container was still open.
        // v78 server messages first: the wire comes before the client's handling of it,
        // which is what the queued events below report. Cheap when nobody asked for any.
        try { Net.ServerMessageStream.Pump(_initialized && ServerMessageSubscriberCount() > 0); }
        catch (Exception ex) { RynthLog.Compat($"PluginManager: server message pump failed - {ex.GetType().Name}: {ex.Message}"); }

        DispatchQueuedBusyCountIncremented();
        DispatchQueuedBusyCountDecremented();
        DispatchQueuedCombatModeChange();
        DispatchQueuedSmartBoxEvent();
        DispatchQueuedViewObjectContents();
        DispatchQueuedStopViewingObjectContents();
        DispatchQueuedVendorOpen();
        DispatchQueuedVendorClose();
        DispatchQueuedUpdateObjectInventory();
        DispatchQueuedCreateObject();
        DispatchQueuedUpdateObject();
        DispatchQueuedDeleteObject();
        DispatchQueuedSelectedTargetChange();
        DispatchQueuedUpdateHealth();
        DispatchQueuedCombatDamage();
        DispatchQueuedKillNotifications();
        DispatchQueuedEnchantmentAdded();
        DispatchQueuedEnchantmentRemoved();
        DispatchQueuedChatWindowText();
        DispatchQueuedUiEvents();   // v77 screen / tooltip / drag / drop (PluginManager.UiHooks.cs)

        if (_rescanRequested)
        {
            _rescanRequested = false;
            RescanPlugins(imguiContext, d3dDevice, gameHwnd);
        }

        // A server forced On whose plugin the connect host left out: load just that plugin.
        if (_initialized)
            LoadRequestedServerPlugins();

        if (_uiDispatchPending && _initialized)
            DispatchUIInitializedToLoadedPlugins();

        if (_loginDispatchPending && _initialized)
            DispatchLoginCompleteToLoadedPlugins();

        // v71 script windows: a plugin that failed in one of the events above (or in Init,
        // via a rescan) loses its windows now rather than after the next tick.
        ScriptWindowRegistry.DropFailedOwners();
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
                        RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnChatBarEnter threw {ex.GetType().Name}: {ex.Message}{PluginTrace(ex)}");
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

    public static void QueueChatWindowText(string? text, uint chatType) => QueueChatWindowText(text, chatType, null);

    /// <summary>
    /// Queues an incoming chat line for the plugins (dispatched on the pump). With
    /// <paramref name="held"/>, AC hasn't printed the line yet and the plugins' verdict is
    /// written to it. Returns false when no plugin is there to hear it (then a held line must
    /// be printed by the caller).
    /// </summary>
    public static bool QueueChatWindowText(string? text, uint chatType, HeldChatLine? held)
    {
        // RynthVision overlays (debuffs, combat text, gains) read chat engine-side, plugins or not.
        ImGuiBackend.Hud.HudFeed.OnChat(text);
        // Who to /reply and /retell to (ChatCommandDispatcher rewrites those to a /tell).
        Compatibility.TellMemory.Shared.OnChatLine(text);

        if (!_initialized || _plugins.Count == 0)
            return false;

        lock (PendingIncomingChatsLock)
        {
            if (_pendingIncomingChats.Count >= MaxPendingIncomingChats)
            {
                _pendingIncomingChats.Dequeue().Held?.Decide(eat: false); // dropped unheard: it shows
                _droppedIncomingChats++;
            }

            _pendingIncomingChats.Enqueue(new PendingIncomingChat(text, chatType, held));
        }
        return true;
    }

    /// <summary>
    /// When the pump last looked at the incoming-chat queue (Environment.TickCount64, 0 = never).
    /// ChatCallbackHooks holds lines for a verdict only while this is recent.
    /// </summary>
    internal static long ChatPumpBeatMs => System.Threading.Interlocked.Read(ref _chatPumpBeatMs);
    private static long _chatPumpBeatMs;

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
        ImGuiBackend.Hud.HudFeed.OnSelection(currentTargetId);   // debuff target matching
        // A selection makes the client query its health: the server's health stream follows it (HealthProbe).
        ImGuiBackend.Hud.HealthProbe.NoteQuery(currentTargetId);

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
        ImGuiBackend.Hud.HudFeed.OnDeleted(objectId);   // nameplate debuffs / kill burst fallback
        lock (LiveObjectsLock) _liveObjects.Remove(objectId);

        // Track deletes that arrive before plugin init so we can filter stale creates from the replay.
        if (!_initialized)
        {
            lock (PrePluginDeleteObjectsLock)
                _prePluginDeleteObjects.Add(objectId);
        }

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
        lock (LiveObjectsLock) _liveObjects.Add(objectId);

        // Also buffer for replay — covers the race between hook install and plugin load
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

    /// <summary>
    /// One health observation. <paramref name="healthReply"/>: it is a 0x01C0 UpdateHealth (a QueryHealth
    /// reply or the server's stream); one that answers the nameplates' own probe (HealthProbe) is the
    /// HUD's alone - plugins never asked for it, and a bot must not read someone else's hit on a monster
    /// it isn't fighting as its own.
    /// </summary>
    public static void QueueUpdateHealth(uint targetId, float healthRatio, uint currentHealth, uint maxHealth, bool healthReply = false)
    {
        // Monster nameplates keep the last ratio per object (engine-side, plugins or not).
        ImGuiBackend.Hud.MonsterHudData.RecordHealth(targetId, healthRatio, currentHealth, maxHealth);

        if (_plugins.Count == 0)
            return;
        if (healthReply && ImGuiBackend.Hud.HealthProbe.IsProbeReply(targetId, Environment.TickCount64))
            return;

        lock (PendingUpdateHealthLock)
        {
            if (_pendingUpdateHealth.Count >= MaxPendingUpdateHealth)
                _pendingUpdateHealth.Dequeue();

            _pendingUpdateHealth.Enqueue(new PendingUpdateHealth(targetId, healthRatio, currentHealth, maxHealth));
        }
    }

    public static void QueueCombatDamage(uint damage, uint damageType, bool crit, bool isAttacker)
    {
        if (_plugins.Count == 0)
            return;

        lock (PendingCombatDamageLock)
        {
            if (_pendingCombatDamage.Count >= MaxPendingCombatDamage)
                _pendingCombatDamage.Dequeue();

            _pendingCombatDamage.Enqueue(new PendingCombatDamage(damage, damageType, crit ? 1u : 0u, isAttacker ? 1u : 0u));
        }
    }

    // KillerNotification (GameEvent 0x01AD) — "you killed something". Queued
    // from SmartBoxHooks on AC's main thread; dispatched to plugins on the
    // pump. The death message carries the victim name so the plugin can match
    // its active target.
    public static void QueueKillNotification(string? deathMessage)
    {
        if (_plugins.Count == 0)
            return;

        lock (PendingKillNotificationsLock)
        {
            if (_pendingKillNotifications.Count >= MaxPendingKillNotifications)
                _pendingKillNotifications.Dequeue();

            _pendingKillNotifications.Enqueue(new PendingKillNotification(deathMessage));
        }
    }

    public static void QueueEnchantmentAdded(uint spellId, double durationSeconds)
    {
        if (_plugins.Count == 0)
            return;

        if (System.Threading.Interlocked.Increment(ref _enchantmentAddLogCount) <= 5)
            RynthLog.Info($"PluginManager: QueueEnchantmentAdded #{_enchantmentAddLogCount} spellId={spellId} dur={durationSeconds:F1}s");

        lock (PendingEnchantmentAddedLock)
        {
            if (_pendingEnchantmentAdded.Count >= MaxPendingEnchantmentEvents)
                _pendingEnchantmentAdded.Dequeue();

            _pendingEnchantmentAdded.Enqueue(new PendingEnchantmentAdded(spellId, durationSeconds));
        }
    }
    private static int _enchantmentAddLogCount;

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
                        RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnChatWindowText threw {ex.GetType().Name}: {ex.Message}{PluginTrace(ex)}");
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

    /// <summary>Monotonic count of TickAll invocations — read by HeartbeatLogger
    /// to report the plugin pump's tick rate (a flatlining rate = wedged pump).
    /// int (atomic read/write on x86) updated by the single TickAll driver.</summary>
    internal static int TickCount;

    // TickAll's own steps are caught one by one (PumpGuards.cs): nothing outside a plugin's
    // own Tick may take the pump down. Pump thread only.
    private static readonly StepFaultLog TickStepFaults = new((int)TickStep.Count);

    private static void TickStepFailed(TickStep step, Exception ex)
    {
        try
        {
            if (!TickStepFaults.Record((int)step, out int count))
                return;
            string line = StepFaultLog.Describe(step, ex.GetType().Name, ex.Message, count);
            RynthLog.Error(count == 1 ? line + Environment.NewLine + ex : line);
        }
        catch { /* logging must not be the thing that throws out of TickAll */ }
    }

    public static void TickAll()
    {
        TickCount++;
        Volatile.Write(ref _tickThread, Environment.CurrentManagedThreadId);
        // v77: after Client::Cleanup no plugin ticks (AC is tearing its UI down); Shutdown still comes.
        if (UiFlowHooks.ClientCleanupStarted)
            return;

        // AC's main thread is frozen (a message box open on it, or no beat for the hang
        // watchdog's threshold): no plugin Tick this pump tick, so plugins neither decide nor
        // act against a client that cannot run anything (MainThreadFreezeRule.cs). Events keep
        // flowing (ProcessPendingActions), and the Nav3D frame is left as it was.
        bool paused;
        try { paused = MainThreadFreeze.ShouldPausePluginTick(); }
        catch (Exception ex) { TickStepFailed(TickStep.FreezeCheck, ex); paused = false; }
        if (paused)
            return;

        // Fire the delayed login self-identify once AC's vitals UI has settled
        // (armed in DispatchLoginCompleteToLoadedPlugins; deferred past the
        // early-init window where AC's gmVitalsUI::Update AV'd — 2026-06-09).
        // Gated on _loginCompleteObserved: a logout within the 15s window
        // otherwise let this fire at char-select — exactly the vitals-teardown
        // window the delay exists to avoid (the due-tick is also cleared in
        // DispatchPendingLogout; this is the belt to that suspender).
        if (_selfIdentifyDueTick != 0 && _loginCompleteObserved && Environment.TickCount64 >= _selfIdentifyDueTick)
        {
            _selfIdentifyDueTick = 0;
            try
            {
                uint pid = ClientHelperHooks.GetPlayerId();
                if (pid != 0 && CombatActionHooks.HasRequestId)
                {
                    using (Net.EngineSendMarker.Enter(Net.IdRequester.Self))
                        CombatActionHooks.RequestId(pid);
                    RynthLog.Plugin($"PluginManager: sent (delayed) self-identify for player 0x{pid:X8} to seed max vitals.");
                }
            }
            catch (Exception ex) { TickStepFailed(TickStep.SelfIdentify, ex); }
        }

        // The cast gate is sampled on AC's main thread now (MainThreadSnapshots.Tick,
        // from the Client::UseTime drain and EndScene); this pump only reads the
        // published value through the GetCastBusyState host pull. Sampling it here
        // walked SmartBox -> player -> MovementManager -> CMotionInterp off-thread.

        // Clear the Nav3D submission buffer once per tick so plugins always
        // submit into a fresh frame. Previously each plugin was expected to
        // call Host.Nav3DClear() itself, which broke if two plugins both used
        // Nav3D — whichever ticked last clobbered the other's submissions, and
        // a Nav3D-only plugin with no clear caller would fill the 512-line
        // buffer and freeze stale geometry in the world.
        //
        // The renderer is double-buffered: ClearFrame resets the PENDING
        // buffer, plugins fill it, and CommitFrame at the end of this method
        // atomically swaps it into "ready" so the render thread sees a
        // complete frame instead of mid-tick partial state.
        try { D3D9.Nav3DRenderer.ClearFrame(); }
        catch (Exception ex) { TickStepFailed(TickStep.Nav3DClear, ex); }

        try
        {
            for (int i = 0; i < _plugins.Count; i++)
            {
                var plugin = _plugins[i];
                if (!plugin.Initialized || plugin.Failed || plugin.Tick == null)
                    continue;

                LoadedPlugin? outer = EnterDispatch(plugin, "Tick");
                try
                {
                    plugin.Tick();
                }
                catch (Exception ex)
                {
                    plugin.Failed = true;
                    RynthLog.Error($"PluginManager: {plugin.DisplayName} Tick threw {ex.GetType().Name}: {ex.Message}{PluginTrace(ex)} - disabled.");
                }
                finally
                {
                    LeaveDispatch(outer);
                }
            }
        }
        catch (Exception ex)
        {
            // The loop's own bookkeeping (not a plugin's Tick, which is caught above).
            TickStepFailed(TickStep.PluginLoop, ex);
        }
        finally
        {
            // v71 script windows: a plugin that failed (this tick or in an event) loses its windows.
            try { ScriptWindowRegistry.DropFailedOwners(); }
            catch (Exception ex) { TickStepFailed(TickStep.DropFailedOwners, ex); }

            // Atomically publish this tick's Nav3D submissions to the render thread.
            try { D3D9.Nav3DRenderer.CommitFrame(); }
            catch (Exception ex) { TickStepFailed(TickStep.Nav3DCommit, ex); }
        }
    }

    // The first PluginTraceLimit plugin exceptions also log their stack trace: "Tick threw
    // IndexOutOfRangeException" alone left a crash undiagnosable (2026-10-04). Capped so a plugin
    // failing in an event every tick can't flood the log.
    private const int PluginTraceLimit = 20;
    private static int _pluginTracesLogged;

    private static string PluginTrace(Exception ex)
    {
        if (string.IsNullOrEmpty(ex.StackTrace) || Interlocked.Increment(ref _pluginTracesLogged) > PluginTraceLimit)
            return string.Empty;
        string inner = ex.InnerException is { } ie ? $"\n  inner {ie.GetType().Name}: {ie.Message}\n{ie.StackTrace}" : string.Empty;
        return "\n" + ex.StackTrace + inner;
    }

    public static void RenderAll()
    {
        // Suppress plugin rendering while we are not in-world. The window between
        // RecvNotice_Logoff and the next SendLoginCompleteNotification is the
        // crash zone: plugin draw paths reach into world data the AC client is
        // simultaneously tearing down.
        if (!_loginCompleteObserved)
            return;

        // Snapshot once (finding #20) instead of iterating _plugins directly —
        // this runs on AC's render thread while RescanPlugins mutates _plugins
        // on the pump thread.
        LoadedPlugin[] plugins = System.Threading.Volatile.Read(ref _pluginsRenderSnapshot);
        for (int i = 0; i < plugins.Length; i++)
        {
            var plugin = plugins[i];
            if (!plugin.Initialized || plugin.Failed || plugin.Render == null)
                continue;

            long cost = ImGuiBackend.UiCost.Start();   // UiCost: each plugin's Render, by name
            try
            {
                plugin.Render();
            }
            catch (Exception ex)
            {
                plugin.Failed = true;
                RynthLog.Error($"PluginManager: {plugin.DisplayName} Render threw {ex.GetType().Name}: {ex.Message}{PluginTrace(ex)} - disabled.");
            }
            ImGuiBackend.UiCost.StopPluginRender(plugin.DisplayName, cost);
        }
    }

    /// <summary>
    /// ImGui-shell-off counterpart of <see cref="RenderAll"/>: calls the optional
    /// RynthPluginRenderOverlay export, where a plugin draws only the extra windows that have
    /// no Avalonia panel (e.g. RynthAi's Mini Remote, RynthILT's Games HUD). They float beside the Avalonia UI and add to it.
    /// A throw switches off just that plugin's overlay windows; the plugin is NOT marked Failed,
    /// so its tick/automation and Avalonia panels keep running.
    /// </summary>
    public static void RenderOverlayAll()
    {
        _overlayCalls++;
        if (!_loginCompleteObserved)
        {
            if (!_loggedOverlayWaitingForLogin)
            {
                _loggedOverlayWaitingForLogin = true;
                RynthLog.Info("PluginManager: RenderOverlayAll called - waiting for login before drawing overlay windows.");
            }
            return; // same not-in-world crash zone guard as RenderAll
        }

        LoadedPlugin[] plugins = System.Threading.Volatile.Read(ref _pluginsRenderSnapshot);

        // Count plugins that will actually be called; log whenever that set changes.
        int bound = 0;
        for (int i = 0; i < plugins.Length; i++)
        {
            var p = plugins[i];
            if (p.Initialized && !p.Failed && p.RenderOverlay != null) bound++;
        }
        _overlayBound = bound;
        if (bound != _loggedOverlayBound || plugins.Length != _loggedOverlayTotal)
        {
            _loggedOverlayBound = bound;
            _loggedOverlayTotal = plugins.Length;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < plugins.Length; i++)
            {
                var p = plugins[i];
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(p.DisplayName).Append('(')
                  .Append(p.RenderOverlay != null ? "overlay" : "no-overlay")
                  .Append(p.Initialized ? "" : ",not-init")
                  .Append(p.Failed ? ",failed" : "")
                  .Append(')');
            }
            RynthLog.Info($"PluginManager: RenderOverlayAll login seen, {bound} of {plugins.Length} plugin(s) will draw overlay windows: {sb}.");
        }

        for (int i = 0; i < plugins.Length; i++)
        {
            var plugin = plugins[i];
            var overlay = plugin.RenderOverlay;
            if (!plugin.Initialized || plugin.Failed || overlay == null)
                continue;

            if (!plugin.RenderOverlayLogged)
            {
                // One line per plugin per session: proves the engine is actually driving the overlay export.
                plugin.RenderOverlayLogged = true;
                RynthLog.Plugin($"PluginManager: first RenderOverlay call for {plugin.DisplayName}.");
            }

            long cost = ImGuiBackend.UiCost.Start();   // UiCost: each plugin's RenderOverlay, by name
            try
            {
                overlay();
            }
            catch (Exception ex)
            {
                plugin.RenderOverlay = null;
                RynthLog.Error($"PluginManager: {plugin.DisplayName} RenderOverlay threw {ex.GetType().Name}: {ex.Message} - overlay windows off for this session (plugin keeps running).");
            }
            ImGuiBackend.UiCost.StopPluginOverlay(plugin.DisplayName, cost);
        }
    }

    // RenderOverlayAll diagnostics (render thread only).
    private static long _overlayCalls;
    private static int _overlayBound;
    private static bool _loggedOverlayWaitingForLogin;
    private static int _loggedOverlayBound = -1;
    private static int _loggedOverlayTotal = -1;

    /// <summary>
    /// True when an initialized, healthy plugin exports RynthPluginRenderOverlay. Reads the
    /// render snapshot only (no lock, no allocation): safe every frame on the render thread.
    /// </summary>
    internal static bool HasOverlayPlugins
    {
        get
        {
            LoadedPlugin[] plugins = System.Threading.Volatile.Read(ref _pluginsRenderSnapshot);
            for (int i = 0; i < plugins.Length; i++)
            {
                var p = plugins[i];
                if (p.Initialized && !p.Failed && p.RenderOverlay != null) return true;
            }
            return false;
        }
    }

    /// <summary>Overlay stats for the ImGui heartbeat line.</summary>
    internal static string DescribeOverlayState()
        => $"overlayCalls={_overlayCalls} loginSeen={_loginCompleteObserved} overlayPlugins={_overlayBound}";

    /// <summary>RenderEmbed results.</summary>
    internal const int EmbedDrawn = 1, EmbedNotReady = 0, EmbedNoPlugin = -1, EmbedFailed = -2;

    /// <summary>
    /// Appended to an embed surface's name when the caller draws in another ImGui context than
    /// the one plugins were given (a popped-out panel's own context). Only a plugin that knows
    /// the suffix draws then, into the current context; an older one switches to the main
    /// context, which is not mid-frame there, and faults inside cimgui (an access violation no
    /// try/catch stops: 2026-10-11, the popped-out Mini Remote), so it must see a surface name
    /// it doesn't know and decline.
    /// </summary>
    private static ReadOnlySpan<byte> PopOutSurfaceSuffix => ":popout"u8;

    /// <summary>
    /// Lets the plugin named <paramref name="pluginName"/> (display name contains it) draw the
    /// surface <paramref name="surfaceUtf8"/> (NUL-terminated) into the current ImGui window,
    /// through its optional RynthPluginRenderEmbed export. The caller has begun a child of
    /// <paramref name="size"/>; the plugin draws only inside it and reports the content size it
    /// wants in <paramref name="want"/> (zero when it didn't say). In a context other than the
    /// plugins' (a pop-out), the surface goes out as "name:popout" (see
    /// <see cref="PopOutSurfaceSuffix"/>).
    /// Render thread only. Returns <see cref="EmbedDrawn"/>, <see cref="EmbedNotReady"/> (the
    /// plugin isn't ready: before login, no ImGui context, or too old to draw in a pop-out),
    /// <see cref="EmbedNoPlugin"/> (not loaded, or too old to have the export) or
    /// <see cref="EmbedFailed"/> (it threw, or returned -2 to say it caught its own exception:
    /// the export is switched off for this session; the plugin keeps running).
    /// </summary>
    internal static unsafe int RenderEmbed(string pluginName, ReadOnlySpan<byte> surfaceUtf8, System.Numerics.Vector2 size, out System.Numerics.Vector2 want)
    {
        want = System.Numerics.Vector2.Zero;
        if (!_loginCompleteObserved) return EmbedNotReady;   // same not-in-world guard as RenderAll
        IntPtr current = ImGuiNET.ImGui.GetCurrentContext();
        if (current == IntPtr.Zero) return EmbedNotReady;
        if (current != _api.ImGuiContext)
        {
            // "name\0" -> "name:popout\0" on the stack; surface names are a few bytes.
            int nameLen = surfaceUtf8.IndexOf((byte)0);
            if (nameLen < 0) nameLen = surfaceUtf8.Length;
            int total = nameLen + PopOutSurfaceSuffix.Length + 1;
            if (total > 128) return EmbedNotReady;
            Span<byte> popOut = stackalloc byte[total];
            surfaceUtf8[..nameLen].CopyTo(popOut);
            PopOutSurfaceSuffix.CopyTo(popOut[nameLen..]);
            popOut[^1] = 0;
            // A plugin on a second cimgui copy must see the pop-out's context there too (StrayImGuiContext).
            StrayImGuiContext.Scope stray = StrayImGuiContext.Enter(current);
            try { return RenderEmbedCore(pluginName, popOut, size, out want); }
            finally { StrayImGuiContext.Restore(stray); }
        }
        return RenderEmbedCore(pluginName, surfaceUtf8, size, out want);
    }

    /// <summary>The export call behind <see cref="RenderEmbed"/>, with the surface name already final.</summary>
    private static unsafe int RenderEmbedCore(string pluginName, ReadOnlySpan<byte> surfaceUtf8, System.Numerics.Vector2 size, out System.Numerics.Vector2 want)
    {
        want = System.Numerics.Vector2.Zero;
        LoadedPlugin[] plugins = System.Threading.Volatile.Read(ref _pluginsRenderSnapshot);
        LoadedPlugin? p = FindRenderPlugin(plugins, pluginName);
        if (p == null) return EmbedNoPlugin;
        IntPtr fn = p.RenderEmbedPtr;
        if (fn == IntPtr.Zero) return EmbedNoPlugin;
        float w = 0, h = 0;
        try
        {
            int r;
            fixed (byte* s = surfaceUtf8)
                r = ((delegate* unmanaged[Cdecl]<byte*, float, float, float*, float*, int>)fn)(s, size.X, size.Y, &w, &h);
            want = new System.Numerics.Vector2(float.IsFinite(w) ? MathF.Max(0, w) : 0, float.IsFinite(h) ? MathF.Max(0, h) : 0);
            if (r == EmbedFailed)
            {
                // The plugin caught its own exception and says so (an exception can't leave a
                // managed plugin's [UnmanagedCallersOnly] export, so plugins catch everything; with
                // 0 the window would say "Waiting..." for ever): the same as a throw.
                p.RenderEmbedPtr = IntPtr.Zero;
                RynthLog.Error($"PluginManager: {p.DisplayName} RenderEmbed reported a failure (-2) - embedded surfaces off for this session (plugin keeps running).");
                return EmbedFailed;
            }
            return r == 1 ? EmbedDrawn : EmbedNotReady;
        }
        catch (Exception ex)
        {
            p.RenderEmbedPtr = IntPtr.Zero;
            RynthLog.Error($"PluginManager: {p.DisplayName} RenderEmbed threw {ex.GetType().Name}: {ex.Message} - embedded surfaces off for this session (plugin keeps running).");
            return EmbedFailed;
        }
    }

    /// <summary>A running plugin in the render snapshot by name: exact first (see ResolvePluginExport), else contains.</summary>
    private static LoadedPlugin? FindRenderPlugin(LoadedPlugin[] plugins, string pluginName)
    {
        for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < plugins.Length; i++)
            {
                LoadedPlugin p = plugins[i];
                if (p.Initialized && !p.Failed && NameMatches(p.DisplayName, pluginName, exact: pass == 0))
                    return p;
            }
        return null;
    }

    /// <summary>
    /// Each running plugin's Mini Remote sections manifest (its optional
    /// RynthPluginGetSectionsJson export), copied out as (display name, JSON). A plugin whose
    /// export throws has it switched off for the session. Render thread (UI/Sections).
    /// </summary>
    internal static unsafe List<(string Plugin, string Json)> CollectSectionManifests()
    {
        var result = new List<(string, string)>();
        if (!_loginCompleteObserved) return result;   // plugins build these from in-world state
        LoadedPlugin[] plugins = System.Threading.Volatile.Read(ref _pluginsRenderSnapshot);
        for (int i = 0; i < plugins.Length; i++)
        {
            LoadedPlugin p = plugins[i];
            IntPtr fn = p.SectionsJsonPtr;
            if (!p.Initialized || p.Failed || fn == IntPtr.Zero) continue;
            try
            {
                IntPtr text = ((delegate* unmanaged[Cdecl]<IntPtr>)fn)();
                if (text == IntPtr.Zero) continue;
                string? json = Marshal.PtrToStringUTF8(text);
                if (!string.IsNullOrEmpty(json)) result.Add((p.DisplayName, json));
            }
            catch (Exception ex)
            {
                p.SectionsJsonPtr = IntPtr.Zero;
                RynthLog.Error($"PluginManager: {p.DisplayName} RynthPluginGetSectionsJson threw {ex.GetType().Name}: {ex.Message} - its sections are off for this session.");
            }
        }
        return result;
    }

    public static void ShutdownAll()
    {
        // A plugin's Shutdown never runs alongside its Tick on the pump thread (audit
        // 2026-10-21, 01 #1). EngineLifecycle.Shutdown stops the pump before anything reaches
        // here, so this returns at once; a frame still running gets ShutdownWaitMs more, and
        // if it outlasts that the plugins are left alone. Nothing below changes then: the
        // teardown's defensive ShutdownAll asks again after its other steps.
        PluginShutdownGate.Verdict gate = EntryPoint.StopTickPumpForPluginShutdown(PluginShutdownGate.ShutdownWaitMs);
        if (!PluginShutdownGate.MayShutDownPlugins(gate))
        {
            RynthLog.Error(PluginShutdownGate.DescribeRefusal(_plugins.Count, DescribePumpDispatch()));
            return;
        }
        RynthLog.Plugin($"PluginManager: Shutting down {_plugins.Count} plugin(s)...");
        UiLifecycleHooks.UiInitialized -= OnUIInitializedObserved;
        LoginLifecycleHooks.LoginComplete -= OnLoginCompleteObserved;
        LogoutLifecycleHooks.LogoutComplete -= OnLogoutObserved;
        UnloadAllPlugins();
        ScriptWindowRegistry.Reset();
        RynthLog.Info("ShutdownAll: post-UnloadAllPlugins");
        // SKIP CleanupShadowCopies during shutdown — it walks the (still-mapped)
        // plugin DLL directory and was a heavy heap-IO step during the 1.4s
        // crash window. Old shadow copies are cleaned up on next launch via
        // CleanupShadowCopies in LoadPlugins anyway.
        // PluginLoader.CleanupShadowCopies(_shadowRootDir);
        RynthLog.Info("ShutdownAll: post-CleanupShadowCopies (skipped)");
        _initialized = false;
        _loaded = false;
        _rescanRequested = false;
        _uiDispatchPending = false;
        _uiInitializedObserved = false;
        _loginDispatchPending = false;
        _loginCompleteObserved = false;
        _logoutDispatchPending = false;
        _pluginsDir = "";
        _shadowRootDir = "";

        lock (LiveObjectsLock)
            _liveObjects.Clear();
        lock (PrePluginCreateObjectsLock)
            _prePluginCreateObjects.Clear();
        lock (PrePluginDeleteObjectsLock)
            _prePluginDeleteObjects.Clear();
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
        lock (PendingCombatDamageLock)
            _pendingCombatDamage.Clear();
        lock (PendingKillNotificationsLock)
            _pendingKillNotifications.Clear();
        lock (PendingEnchantmentAddedLock)
            _pendingEnchantmentAdded.Clear();
        lock (PendingEnchantmentRemovedLock)
            _pendingEnchantmentRemoved.Clear();
        RynthLog.Info("ShutdownAll: post-queue-clears");
    }

    // Managed plugins (CoreCLR engine) answer from their export map; see ManagedPlugins.
    private static IntPtr GetProcAddress(IntPtr hModule, string lpProcName) => ManagedPlugins.GetProcAddress(hModule, lpProcName);

    /// <summary>
    /// Resolve a named export on a loaded plugin's module, matched by display
    /// name (substring, case-insensitive). Brokers cross-plugin calls so one
    /// plugin never GetProcAddress-es a sibling directly — PluginManager owns the
    /// module handles. Returns IntPtr.Zero if the plugin isn't loaded/ready or
    /// doesn't export the symbol. Index-based scan; safe to call re-entrantly from
    /// within TickAll (no enumerator, no mutation).
    /// </summary>
    private static IntPtr ResolvePluginExport(string pluginName, string exportName)
    {
        if (string.IsNullOrEmpty(pluginName) || string.IsNullOrEmpty(exportName))
            return IntPtr.Zero;

        // Exact name first: "RynthLeafDawn" is inside "RynthLeafdawning".
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < _plugins.Count; i++)
            {
                var p = _plugins[i];
                if (p.ModuleHandle == IntPtr.Zero || !p.Initialized || p.Failed)
                    continue;
                if (!NameMatches(p.DisplayName, pluginName, exact: pass == 0))
                    continue;
                return GetProcAddress(p.ModuleHandle, exportName);
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>The plugin-name test of the by-name lookups: exact (any case), else "display name contains it".</summary>
    private static bool NameMatches(string displayName, string pluginName, bool exact) =>
        exact ? string.Equals(displayName, pluginName, StringComparison.OrdinalIgnoreCase)
              : displayName.Contains(pluginName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reload deferral (2026-10-05, ReloadDeferral): asks every running plugin's optional
    /// <c>const char* RynthPluginReloadBlocker(void)</c> export whether a hot reload now would
    /// hurt. Null or "" = safe; a plugin without the export never objects. Fixed signature, so no
    /// mismatch can reach the stack; the plugin owns the buffer and it is copied at once. Called
    /// on the loader's reload thread before the engine tears down, while every plugin is loaded.
    /// </summary>
    internal static unsafe IReadOnlyList<(string Plugin, string Reason)> QueryReloadBlockers()
    {
        var result = new List<(string, string)>();
        LoadedPlugin[] plugins;
        try { plugins = _plugins.ToArray(); }
        catch { return result; }   // the list changed under us (a plugin loading): don't hold the reload on it
        foreach (var p in plugins)
        {
            try
            {
                if (p.ModuleHandle == IntPtr.Zero || !p.Initialized || p.Failed) continue;
                IntPtr fn = GetProcAddress(p.ModuleHandle, "RynthPluginReloadBlocker");
                if (fn == IntPtr.Zero) continue;
                IntPtr text = ((delegate* unmanaged[Cdecl]<IntPtr>)fn)();
                string? reason = text != IntPtr.Zero ? Marshal.PtrToStringAnsi(text) : null;
                if (!string.IsNullOrWhiteSpace(reason)) result.Add((p.DisplayName, reason.Trim()));
            }
            catch { /* a plugin that can't answer doesn't hold the reload */ }
        }
        return result;
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
        // Re-arm the cold-login CObjectMaint backfill for this fresh login.
        System.Threading.Volatile.Write(ref _cObjectMaintSeedDone, 0);
        _cObjectMaintSeedAttempts = 0;
        _cObjectMaintSeedLastMs = 0;
        System.Threading.Volatile.Write(ref _cObjectMaintSeedNotMainLogged, 0);
        RynthLog.Plugin("PluginManager: OnLoginComplete observed - queued login lifecycle callbacks.");
    }

    /// <summary>
    /// Hooked to <see cref="LogoutLifecycleHooks.LogoutComplete"/>. Runs on AC's UI
    /// thread INSIDE <c>gmGamePlayUI::RecvNotice_Logoff</c>, so it must do as little
    /// work as possible — anything heavier than flipping a flag will stall AC during
    /// a critical state transition. The actual plugin teardown happens on the next
    /// EndScene tick via <see cref="ProcessPendingActions"/>.
    /// </summary>
    private static void OnLogoutObserved()
    {
        if (_logoutDispatchPending)
            return;

        _logoutDispatchPending = true;
        RynthLog.Plugin("PluginManager: OnLogout observed - queued for next EndScene frame.");
    }

    /// <summary>
    /// Drained from <see cref="ProcessPendingActions"/> on the EndScene thread.
    /// </summary>
    private static void DispatchPendingLogout()
    {
        if (!_logoutDispatchPending)
            return;

        _logoutDispatchPending = false;

        RynthLog.Plugin("PluginManager: dispatching OnLogout to plugins.");
        DispatchLogoutToLoadedPlugins();

        // Drop pointers to AC objects that the engine has cached and that AC
        // frees during the world-to-charselect transition. ChatHooks caches
        // gmMainChatUI for per-frame visibility assertion; using that pointer
        // after AC has freed the widget AVs inside UIElement::IsVisible. The
        // instance gets re-captured on the next ListenToElementMessage call
        // after the next login completes.
        Compatibility.ChatHooks.ResetCachedInstance();
        Compatibility.RadarHooks.ResetCachedInstance();
        Compatibility.PowerbarHooks.ResetCachedInstance();
        Compatibility.RetailVitalsHooks.ResetCachedInstance();
        Compatibility.RetailToolbarHooks.ResetCachedInstance();
        Compatibility.AcShortcuts.Reset();
        Compatibility.AcKeymapLive.Reset();
        Compatibility.AcPanels.Reset();
        Compatibility.ChatCallbackHooks.ResetOutgoingTarget();

        // Drop the cached PlayerDesc/CACQualities pointer so the next call into the
        // buffed-max inq function can't AV on a freed allocation. Re-seeded on the next
        // SendNoticePlayerDescReceived after the next login completes.
        Compatibility.PlayerVitalsHooks.ResetSession();

        // Drop the cached ClientUISystem pointer for the same reason — the busy
        // watchdog/force-clear must not touch the freed singleton at char-select.
        Compatibility.BusyCountHooks.ResetSession();

        // Clear the per-guid appraisal caches (finding #33) — guids don't
        // survive a session, so leaving these unbounded across relogs is a
        // slow but real leak on this stack's 32-bit VA budget.
        Compatibility.AppraisalHooks.ClearSession();
        // The update caches and the player's PlayerDescription record likewise (2026-09-30).
        Compatibility.PropertyUpdateHooks.ClearSession();

        // Reset login observation so the next SendLoginCompleteNotification kicks
        // off a fresh login-complete cycle.
        for (int i = 0; i < _plugins.Count; i++)
            _plugins[i].LoginCompleteDispatched = false;

        _loginCompleteObserved = false;
        _loginDispatchPending = false;
        // Disarm the delayed self-identify: if the player logs out inside the
        // 15s arming window, firing RequestId at char-select hits AC mid
        // vitals-UI teardown — the gmVitalsUI::Update AV the delay was built
        // to dodge. Re-login re-arms it in DispatchLoginCompleteToLoadedPlugins.
        _selfIdentifyDueTick = 0;
        LoginLifecycleHooks.ResetObservation();
        LogoutLifecycleHooks.ResetObservation();
    }

    private static void DispatchLogoutToLoadedPlugins()
    {
        for (int i = 0; i < _plugins.Count; i++)
        {
            var plugin = _plugins[i];
            if (!plugin.Initialized || plugin.Failed || plugin.OnLogout == null)
                continue;

            LoadedPlugin? outer = EnterDispatch(plugin);
            try
            {
                plugin.OnLogout();
                RynthLog.Plugin($"PluginManager: {plugin.DisplayName} received OnLogout.");
            }
            catch (Exception ex)
            {
                plugin.Failed = true;
                RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnLogout threw {ex.GetType().Name}: {ex.Message}{PluginTrace(ex)}");
            }
            finally
            {
                LeaveDispatch(outer);
            }
        }
    }

    private static void RescanPlugins(IntPtr imguiContext, IntPtr d3dDevice, IntPtr gameHwnd)
    {
        if (string.IsNullOrWhiteSpace(_pluginsDir))
        {
            RynthLog.Plugin("PluginManager: Rescan requested before plugin directories were configured.");
            return;
        }

        RynthLog.Plugin("PluginManager: Rescanning plugins...");
        Net.FlightRecorder.Mark(Net.FlightMark.PluginReload, text: "all plugins unloaded and loaded again (rescan)");

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
        // Plugins are here and hearing chat: ChatCallbackHooks may hold lines for their verdict.
        System.Threading.Interlocked.Exchange(ref _chatPumpBeatMs, Environment.TickCount64);

        PendingIncomingChat[] pending;
        long dropped = 0;
        lock (PendingIncomingChatsLock)
        {
            // Report drops at most every 10 s (a flood would otherwise flood the log too).
            long nowMs = Environment.TickCount64;
            if (_droppedIncomingChats > 0 && nowMs - _droppedIncomingChatsLoggedMs >= 10_000)
            {
                dropped = _droppedIncomingChats;
                _droppedIncomingChats = 0;
                _droppedIncomingChatsLoggedMs = nowMs;
            }

            pending = _pendingIncomingChats.Count == 0 ? Array.Empty<PendingIncomingChat>() : _pendingIncomingChats.ToArray();
            _pendingIncomingChats.Clear();
        }
        // Logged outside the lock: AC's main thread queues chat under it.
        if (dropped > 0)
            RynthLog.Plugin($"PluginManager: {dropped} incoming chat line(s) dropped before plugins heard them (queue full at {MaxPendingIncomingChats}).");
        if (pending.Length == 0)
            return;

        unsafe
        {
            foreach (PendingIncomingChat evt in pending)
            {
                IntPtr textPtr = IntPtr.Zero;
                bool eaten = false;   // the verdict for a held line: AC prints it unless this ends true
                try
                {
                    textPtr = evt.Text != null ? Marshal.StringToHGlobalUni(evt.Text) : IntPtr.Zero;
                    int eat = 0;
                    IntPtr eatPtr = new(&eat);
                    bool threw = false;

                    for (int i = 0; i < _plugins.Count; i++)
                    {
                        var plugin = _plugins[i];
                        if (!plugin.Initialized || plugin.Failed)
                            continue;

                        LoadedPlugin? outer = EnterDispatch(plugin);
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
                            threw = true;
                            plugin.Failed = true;
                            RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnChatWindowText threw {ex.GetType().Name}: {ex.Message}{PluginTrace(ex)}");
                        }
                        finally
                        {
                            LeaveDispatch(outer);
                        }
                    }

                    eaten = HeldChatQueue.PluginsAteIt(eat, threw);
                    // RynthChat's Hide rules / standard filters live in the engine, not in a plugin, so
                    // without this a hidden line still printed in AC's own chat window. Only a held line
                    // can still be kept out (one already printed has gone); a failure lets it show.
                    if (!eaten && evt.Held != null)
                    {
                        try { eaten = UI.Data.ChatModel.ShouldHideInRetail(evt.Text, unchecked((int)evt.ChatType)); }
                        catch (Exception ex) { RynthLog.Plugin($"PluginManager: retail chat filter check failed ({ex.GetType().Name}: {ex.Message})."); }
                    }
                }
                finally
                {
                    // Always give a held line its verdict, so AC prints it at once rather than
                    // after the hold timeout; a line nobody could judge shows.
                    evt.Held?.Decide(eaten);
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

                LoadedPlugin? outer = EnterDispatch(plugin);
                try
                {
                    plugin.OnSelectedTargetChange(evt.CurrentTargetId, evt.PreviousTargetId);
                }
                catch (Exception ex)
                {
                    plugin.Failed = true;
                    RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnSelectedTargetChange threw {ex.GetType().Name}: {ex.Message}{PluginTrace(ex)}");
                }
                finally
                {
                    LeaveDispatch(outer);
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

                LoadedPlugin? outer = EnterDispatch(plugin);
                try
                {
                    plugin.OnBusyCountIncremented();
                }
                catch (Exception ex)
                {
                    plugin.Failed = true;
                    RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnBusyCountIncremented threw {ex.GetType().Name}: {ex.Message}{PluginTrace(ex)}");
                }
                finally
                {
                    LeaveDispatch(outer);
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

                LoadedPlugin? outer = EnterDispatch(plugin);
                try
                {
                    plugin.OnBusyCountDecremented();
                }
                catch (Exception ex)
                {
                    plugin.Failed = true;
                    RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnBusyCountDecremented threw {ex.GetType().Name}: {ex.Message}{PluginTrace(ex)}");
                }
                finally
                {
                    LeaveDispatch(outer);
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

                LoadedPlugin? outer = EnterDispatch(plugin);
                try
                {
                    plugin.OnCombatModeChange(evt.CurrentCombatMode, evt.PreviousCombatMode);
                }
                catch (Exception ex)
                {
                    plugin.Failed = true;
                    RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnCombatModeChange threw {ex.GetType().Name}: {ex.Message}{PluginTrace(ex)}");
                }
                finally
                {
                    LeaveDispatch(outer);
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

                LoadedPlugin? outer = EnterDispatch(plugin);
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
                    RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnSmartBoxEvent threw {ex.GetType().Name}: {ex.Message}{PluginTrace(ex)}");
                }
                finally
                {
                    LeaveDispatch(outer);
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

                LoadedPlugin? outer = EnterDispatch(plugin);
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
                    RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnUpdateObject threw {ex.GetType().Name}: {ex.Message}{PluginTrace(ex)}");
                }
                finally
                {
                    LeaveDispatch(outer);
                }
            }
        }
    }

    // Second delete drain — called after ProcessPendingActions to close the race window
    // between DispatchQueuedDeleteObject and TickAll where a delete could arrive untracked.
    public static void FlushPendingDeletes() => DispatchQueuedDeleteObject();

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

                LoadedPlugin? outer = EnterDispatch(plugin);
                try
                {
                    plugin.OnDeleteObject(evt.ObjectId);
                }
                catch (Exception ex)
                {
                    plugin.Failed = true;
                    RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnDeleteObject threw {ex.GetType().Name}: {ex.Message}{PluginTrace(ex)}");
                }
                finally
                {
                    LeaveDispatch(outer);
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
            // Skip objects that were deleted before we could dispatch them — avoids
            // queuing stale IDs into the plugin's pending classification.
            bool isLive;
            lock (LiveObjectsLock) isLive = _liveObjects.Contains(evt.ObjectId);
            if (!isLive) continue;

            for (int i = 0; i < _plugins.Count; i++)
            {
                var plugin = _plugins[i];
                if (!plugin.Initialized || plugin.Failed || plugin.OnCreateObject == null)
                    continue;

                LoadedPlugin? outer = EnterDispatch(plugin);
                try
                {
                    plugin.OnCreateObject(evt.ObjectId);
                }
                catch (Exception ex)
                {
                    plugin.Failed = true;
                    RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnCreateObject threw {ex.GetType().Name}: {ex.Message}{PluginTrace(ex)}");
                }
                finally
                {
                    LeaveDispatch(outer);
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

                LoadedPlugin? outer = EnterDispatch(plugin);
                try
                {
                    plugin.OnUpdateObjectInventory(evt.ObjectId);
                }
                catch (Exception ex)
                {
                    plugin.Failed = true;
                    RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnUpdateObjectInventory threw {ex.GetType().Name}: {ex.Message}{PluginTrace(ex)}");
                }
                finally
                {
                    LeaveDispatch(outer);
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

                LoadedPlugin? outer = EnterDispatch(plugin);
                try
                {
                    plugin.OnViewObjectContents(evt.ObjectId);
                }
                catch (Exception ex)
                {
                    plugin.Failed = true;
                    RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnViewObjectContents threw {ex.GetType().Name}: {ex.Message}{PluginTrace(ex)}");
                }
                finally
                {
                    LeaveDispatch(outer);
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

                LoadedPlugin? outer = EnterDispatch(plugin);
                try
                {
                    plugin.OnStopViewingObjectContents(evt.ObjectId);
                }
                catch (Exception ex)
                {
                    plugin.Failed = true;
                    RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnStopViewingObjectContents threw {ex.GetType().Name}: {ex.Message}{PluginTrace(ex)}");
                }
                finally
                {
                    LeaveDispatch(outer);
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

                LoadedPlugin? outer = EnterDispatch(plugin);
                try
                {
                    plugin.OnVendorOpen(evt.VendorId);
                }
                catch (Exception ex)
                {
                    plugin.Failed = true;
                    RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnVendorOpen threw {ex.GetType().Name}: {ex.Message}{PluginTrace(ex)}");
                }
                finally
                {
                    LeaveDispatch(outer);
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

                LoadedPlugin? outer = EnterDispatch(plugin);
                try
                {
                    plugin.OnVendorClose(evt.VendorId);
                }
                catch (Exception ex)
                {
                    plugin.Failed = true;
                    RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnVendorClose threw {ex.GetType().Name}: {ex.Message}{PluginTrace(ex)}");
                }
                finally
                {
                    LeaveDispatch(outer);
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

                LoadedPlugin? outer = EnterDispatch(plugin);
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
                    RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnUpdateHealth threw {ex.GetType().Name}: {ex.Message}{PluginTrace(ex)}");
                }
                finally
                {
                    LeaveDispatch(outer);
                }
            }
        }
    }

    private static void DispatchQueuedCombatDamage()
    {
        if (!_initialized || _plugins.Count == 0)
            return;

        PendingCombatDamage[] pending;
        lock (PendingCombatDamageLock)
        {
            if (_pendingCombatDamage.Count == 0)
                return;

            pending = _pendingCombatDamage.ToArray();
            _pendingCombatDamage.Clear();
        }

        foreach (PendingCombatDamage evt in pending)
        {
            for (int i = 0; i < _plugins.Count; i++)
            {
                var plugin = _plugins[i];
                if (!plugin.Initialized || plugin.Failed)
                    continue;

                LoadedPlugin? outer = EnterDispatch(plugin);
                try
                {
                    unsafe
                    {
                        if (plugin.OnCombatDamagePtr != IntPtr.Zero)
                        {
                            ((delegate* unmanaged[Cdecl]<uint, uint, uint, uint, void>)plugin.OnCombatDamagePtr)(evt.Damage, evt.DamageType, evt.Crit, evt.IsAttacker);
                        }
                        else if (plugin.OnCombatDamage != null)
                        {
                            plugin.OnCombatDamage(evt.Damage, evt.DamageType, evt.Crit, evt.IsAttacker);
                        }
                    }
                }
                catch (Exception ex)
                {
                    plugin.Failed = true;
                    RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnCombatDamage threw {ex.GetType().Name}: {ex.Message}{PluginTrace(ex)}");
                }
                finally
                {
                    LeaveDispatch(outer);
                }
            }
        }
    }

    private static void DispatchQueuedKillNotifications()
    {
        if (!_initialized || _plugins.Count == 0)
            return;

        PendingKillNotification[] pending;
        lock (PendingKillNotificationsLock)
        {
            if (_pendingKillNotifications.Count == 0)
                return;

            pending = _pendingKillNotifications.ToArray();
            _pendingKillNotifications.Clear();
        }

        foreach (PendingKillNotification evt in pending)
        {
            IntPtr textPtr = evt.DeathMessage != null ? Marshal.StringToHGlobalUni(evt.DeathMessage) : IntPtr.Zero;
            try
            {
                for (int i = 0; i < _plugins.Count; i++)
                {
                    var plugin = _plugins[i];
                    if (!plugin.Initialized || plugin.Failed)
                        continue;

                    LoadedPlugin? outer = EnterDispatch(plugin);
                    try
                    {
                        unsafe
                        {
                            if (plugin.OnKillNotificationPtr != IntPtr.Zero)
                            {
                                ((delegate* unmanaged[Cdecl]<IntPtr, void>)plugin.OnKillNotificationPtr)(textPtr);
                            }
                            else
                            {
                                plugin.OnKillNotification?.Invoke(textPtr);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        plugin.Failed = true;
                        RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnKillNotification threw {ex.GetType().Name}: {ex.Message}{PluginTrace(ex)}");
                    }
                    finally
                    {
                        LeaveDispatch(outer);
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

                LoadedPlugin? outer = EnterDispatch(plugin);
                try
                {
                    plugin.OnEnchantmentAdded(evt.SpellId, evt.DurationSeconds);
                }
                catch (Exception ex)
                {
                    plugin.Failed = true;
                    RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnEnchantmentAdded threw {ex.GetType().Name}: {ex.Message}{PluginTrace(ex)}");
                }
                finally
                {
                    LeaveDispatch(outer);
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

                LoadedPlugin? outer = EnterDispatch(plugin);
                try
                {
                    plugin.OnEnchantmentRemoved(evt.EnchantmentId);
                }
                catch (Exception ex)
                {
                    plugin.Failed = true;
                    RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnEnchantmentRemoved threw {ex.GetType().Name}: {ex.Message}{PluginTrace(ex)}");
                }
                finally
                {
                    LeaveDispatch(outer);
                }
            }
        }
    }

    private static void LoadPluginsFromDisk()
    {
        _loadGeneration++;

        // Plugin discovery is opt-in via the launcher UI: users add specific
        // DLL paths through "Add Plugin DLL", which the launcher persists to
        // %APPDATA%\RynthCore\engine.json. The engine never auto-scans a
        // bundled Plugins\ folder — that historically caused stray DLLs to
        // load on next start (or after hot-reload) without the user knowing.
        //
        // Track DLL file names and canonical paths already staged this generation. engine.json can
        // list the same plugin twice (e.g. a stale copy in another folder); a second File.Copy to the
        // same shadow file name fails while the first copy is still mapped ("used by another process")
        // and breaks hot-reload ordering for later DLLs.
        var loadedBasenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var loadedCanonicalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var extraPaths = EngineSettings.PluginPaths;
        var present = new List<string>(extraPaths.Count);
        for (int i = 0; i < extraPaths.Count; i++)
        {
            string dllPath = extraPaths[i];
            if (!File.Exists(dllPath))
            {
                RynthLog.Plugin($"PluginManager: Extra plugin not found: {dllPath}");
                continue;
            }
            present.Add(dllPath);
        }

        // One plugin per file name, before any shadow copy: every shadow copy shares the session
        // folder, so a second DLL of the same name would overwrite a mapped copy. The same file
        // listed twice loads once; the same name from two folders is a conflict - the first wins
        // (PluginPathDedupe).
        var loadedSources = new List<string>(_plugins.Count);
        for (int j = 0; j < _plugins.Count; j++)
            loadedSources.Add(_plugins[j].SourceFilePath);
        PluginPathDedupe.Result deduped = PluginPathDedupe.Dedupe(present, loadedSources);
        foreach (PluginPathDedupe.Skipped skipped in deduped.Skipped)
        {
            if (skipped.SameFile)
                RynthLog.Plugin($"PluginManager: {skipped.Message}");
            else
                RynthLog.Warn($"PluginManager: {skipped.Message}");
        }
        List<string> candidates = deduped.Load;

        // Manifests (embedded in the DLLs, read without loading them): refuse a plugin that needs
        // a newer engine API or a missing plugin, and put required plugins first. Without
        // manifests the list loads exactly as before. Server plugins: the connect host picks this
        // server's (the others stay unloaded; their runtimes would only take address space).
        PluginManifestGate.LoadPlan plan = PluginManifestGate.Plan(candidates, PluginContractVersion.Current, RawFileVersion,
            Compatibility.ServerInfo.LaunchHost, EngineSettings.LoadAllServerPlugins);
        foreach (string line in plan.Log)
            RynthLog.Plugin($"PluginManager: {line}");
        if (plan.SkippedForServer.Count > 0)
            RynthLog.Info($"PluginManager: {plan.SkippedForServer.Count} server plugin(s) for other servers not loaded (host {Compatibility.ServerInfo.HostDescription}).");
        RememberSkippedServerPlugins(plan.SkippedServerPlugins);
        foreach (PluginRefusal refusal in plan.Refused)
            RefusePlugin(Path.GetFileName(refusal.Plugin.Key), refusal.Reason);

        foreach ((string dllPath, RynthPluginManifest? manifest) in plan.Load)
        {
            // A required plugin that failed to load (PluginLoader said no) takes its dependents with it.
            string? missing = PluginManifestGate.CheckRequiredStarted(manifest, name => FindPlugin(name) != null ? true : null);
            if (missing != null)
            {
                RefusePlugin(Path.GetFileName(dllPath), missing);
                continue;
            }

            // Address space before and after each load: a 32-bit client runs out on the largest
            // free block, and a reload stacks copies (Compatibility/AddressSpace.cs).
            RynthLog.Plugin($"PluginManager: Loading extra plugin: {dllPath} (va before: {Compatibility.AddressSpace.Describe()})");
            var plugin = PluginLoader.LoadSingle(dllPath, _shadowRootDir, _loadGeneration);
            RynthLog.Plugin($"PluginManager: loaded {Path.GetFileName(dllPath)}{(plugin == null ? " (FAILED)" : "")} (va after: {Compatibility.AddressSpace.Describe()})");
            Compatibility.MemoryUse.Mark($"{Path.GetFileNameWithoutExtension(dllPath)} loaded");
            if (plugin != null)
            {
                plugin.Manifest = manifest;
                if (manifest != null && !string.Equals(manifest.Name, plugin.DisplayName, StringComparison.OrdinalIgnoreCase))
                    RynthLog.Plugin($"PluginManager: {plugin.FileName}: manifest name '{manifest.Name}' differs from RynthPluginName '{plugin.DisplayName}'; dependencies use the manifest's.");
                _plugins.Add(plugin);
            }
        }

        // RenderAll / RenderOverlayAll iterate only this snapshot; without it they see no plugins.
        PublishPluginsRenderSnapshot();
        RynthLog.Plugin($"PluginManager: render snapshot published ({_plugins.Count} plugin(s)).");
    }

    private static void TryAddCanonicalPath(HashSet<string> set, string path)
    {
        if (TryGetCanonicalPath(path, out string canon))
            set.Add(canon);
    }

    private static bool TryGetCanonicalPath(string path, out string canonical)
    {
        try
        {
            canonical = Path.GetFullPath(path);
            return true;
        }
        catch
        {
            canonical = string.Empty;
            return false;
        }
    }

    // ── Plugin manifests ─────────────────────────────────────────────────

    /// <summary>Refusals not yet said in chat; said once each, at login (see AnnouncePluginRefusals).</summary>
    private static readonly List<string> _pendingRefusalNotices = new();
    private static readonly HashSet<string> _announcedRefusals = new(StringComparer.Ordinal);

    private static void RefusePlugin(string fileName, string reason)
    {
        RynthLog.Plugin($"PluginManager: NOT starting {fileName}: {reason} (this RynthCore is plugin API {PluginContractVersion.Current}).");
        lock (_pendingRefusalNotices)
        {
            if (!_announcedRefusals.Contains(reason) && !_pendingRefusalNotices.Contains(reason))
                _pendingRefusalNotices.Add(reason);
        }
        if (_loginCompleteObserved)
            AnnouncePluginRefusals();
    }

    /// <summary>Says each refusal once in chat ("[RynthCore] RynthOracle needs a newer RynthCore (API 77) - not started."). Pump thread.</summary>
    private static void AnnouncePluginRefusals()
    {
        List<string> lines;
        lock (_pendingRefusalNotices)
        {
            if (_pendingRefusalNotices.Count == 0) return;
            lines = new List<string>(_pendingRefusalNotices);
            _pendingRefusalNotices.Clear();
            foreach (string l in lines) _announcedRefusals.Add(l);
        }
        foreach (string line in lines)
            AcMainThreadQueue.EnqueueWriteToChat($"[RynthCore] {line} - not started.", 1);
    }

    /// <summary>The loaded plugin going by <paramref name="name"/>: manifest name, else its file's name, else RynthPluginName.</summary>
    private static LoadedPlugin? FindPlugin(string name)
    {
        for (int i = 0; i < _plugins.Count; i++)
        {
            LoadedPlugin p = _plugins[i];
            if (p.Manifest != null && string.Equals(p.Manifest.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
        }
        for (int i = 0; i < _plugins.Count; i++)
        {
            LoadedPlugin p = _plugins[i];
            if (string.Equals(RynthPluginManifest.NameFromFile(p.SourceFilePath), name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.DisplayName, name, StringComparison.OrdinalIgnoreCase))
                return p;
        }
        return null;
    }

    private static string RawFileVersion(string path)
    {
        try { return System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductVersion ?? ""; }
        catch { return ""; }
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
            InitializePlugin(_plugins[i]);

        if (!_uiInitializedObserved)
            RynthLog.Plugin("PluginManager: Waiting for OnUIInitialized before starting UI-gated plugins.");

        if (!_loginCompleteObserved)
            RynthLog.Plugin("PluginManager: Waiting for OnLoginComplete before starting login-gated plugins.");
    }

    /// <summary>Calls one loaded plugin's Init (after checking its required plugins started). Pump thread.</summary>
    private static void InitializePlugin(LoadedPlugin plugin)
    {
        // Manifest: a plugin whose required plugin didn't start isn't started either (they
        // come first: LoadPluginsFromDisk ordered them). No manifest, no check.
        string? blocked = PluginManifestGate.CheckRequiredStarted(plugin.Manifest, name =>
            FindPlugin(name) is { } dep ? dep.Initialized && !dep.Failed : null);
        if (blocked != null)
        {
            plugin.Failed = true;
            plugin.NotStartedReason = blocked;
            RefusePlugin(plugin.FileName, blocked);
            return;
        }
        plugin.NotStartedReason = "";

        LoadedPlugin? outer = EnterDispatch(plugin);
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
                Compatibility.MemoryUse.Mark($"{plugin.DisplayName} Init");
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
            RynthLog.Plugin($"PluginManager: {plugin.DisplayName} Init threw {ex.GetType().Name}: {ex.Message}{PluginTrace(ex)}");
        }
        finally
        {
            LeaveDispatch(outer);
        }
    }

    private static void UnloadAllPlugins()
    {
        for (int i = _plugins.Count - 1; i >= 0; i--)
        {
            var plugin = _plugins[i];
            // Shutdown runs for ANY Initialized plugin, including Failed ones.
            // Failed means "a callback threw once" — the plugin's threads,
            // timers, and handles are still live, and skipping its Shutdown
            // export leaked them into the next engine generation (which loads
            // a FRESH copy of the same plugin → double-instance after reload).
            if (plugin.Initialized && plugin.Shutdown != null)
            {
                LoadedPlugin? outer = EnterDispatch(plugin);
                try
                {
                    plugin.Shutdown();
                    RynthLog.Plugin($"PluginManager: {plugin.DisplayName} shut down{(plugin.Failed ? " (was Failed)" : "")}.");
                }
                catch (Exception ex)
                {
                    RynthLog.Plugin($"PluginManager: {plugin.DisplayName} Shutdown threw: {ex.Message}");
                }
                finally
                {
                    LeaveDispatch(outer);
                }
            }
            // v71: its script windows close (their placement is kept).
            ScriptWindowRegistry.DropOwner(plugin);

            string vaBefore = Compatibility.AddressSpace.Describe();
            PluginLoader.Unload(plugin);
            RynthLog.Plugin($"PluginManager: unloaded {plugin.DisplayName} (va before: {vaBefore}, after: {Compatibility.AddressSpace.Describe()})");
        }

        _plugins.Clear();
        PublishPluginsRenderSnapshot();

        try { PluginsUnloaded?.Invoke(); }
        catch (Exception ex) { RynthLog.Plugin($"PluginManager: PluginsUnloaded handler threw: {ex.Message}"); }
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

            LoadedPlugin? outer = EnterDispatch(plugin);
            try
            {
                plugin.OnUIInitialized();
                plugin.UIInitializedDispatched = true;
                RynthLog.Plugin($"PluginManager: {plugin.DisplayName} received OnUIInitialized.");
            }
            catch (Exception ex)
            {
                plugin.Failed = true;
                RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnUIInitialized threw {ex.GetType().Name}: {ex.Message}{PluginTrace(ex)}");
            }
            finally
            {
                LeaveDispatch(outer);
            }
        }
    }

    private static void DispatchLoginCompleteToLoadedPlugins()
    {
        if (!_loginCompleteObserved || !_initialized)
            return;

        _loginDispatchPending = false;
        AnnouncePluginRefusals();   // plugins the manifests kept out, once each, now that chat is up

        for (int i = 0; i < _plugins.Count; i++)
        {
            var plugin = _plugins[i];
            if (!plugin.Initialized || plugin.Failed || plugin.OnLoginComplete == null || plugin.LoginCompleteDispatched)
                continue;

            LoadedPlugin? outer = EnterDispatch(plugin);
            try
            {
                plugin.OnLoginComplete();
                plugin.LoginCompleteDispatched = true;
                RynthLog.Plugin($"PluginManager: {plugin.DisplayName} received OnLoginComplete.");
                Compatibility.MemoryUse.Mark($"{plugin.DisplayName} OnLoginComplete");
            }
            catch (Exception ex)
            {
                plugin.Failed = true;
                RynthLog.Plugin($"PluginManager: {plugin.DisplayName} OnLoginComplete threw {ex.GetType().Name}: {ex.Message}{PluginTrace(ex)}");
            }
            finally
            {
                LeaveDispatch(outer);
            }
        }

        // Auto-identify the player to seed exact max vitals from the CreatureProfile.
        // DELAYED, not sent here: firing it at LoginComplete made AC re-process the
        // full player qualities and re-run its vital-UI update path (gmVitalsUI::Update)
        // while that UI may still be initialising — an intermittent null-deref AV
        // (crash investigation 2026-06-09, 39076/13648). Arm it for SelfIdentifyDelayMs
        // later; TickAll sends it once AC's UI has settled.
        _selfIdentifyDueTick = Environment.TickCount64 + SelfIdentifyDelayMs;

        // Sync the actual current combat mode to newly initialized plugins.
        // Reads directly from ClientCombatSystem so this is accurate even at first inject.
        int actualCombatMode = CombatModeHooks.ReadCurrentCombatMode();
        QueueCombatModeChange(actualCombatMode, CombatActionHooks.CombatModeNonCombat);
        RynthLog.Plugin($"PluginManager: synced combat mode {actualCombatMode} to plugins.");

        // Sync the currently-selected target id. Without this, a plugin that
        // initializes after the user has already picked a target (deferred-init
        // path, hot-reload, etc.) never learns the target — the SetSelectedObject
        // detour only fires on *changes*, and QueueSelectedTargetChange drops
        // events that arrive before _initialized=true.
        uint currentTargetId = SelectedTargetHooks.ReadCurrentSelectedId();
        if (currentTargetId != 0)
        {
            QueueSelectedTargetChange(currentTargetId, 0);
            RynthLog.Plugin($"PluginManager: synced current target 0x{currentTargetId:X8} to plugins.");
        }
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
        _setRadarSuppressedCallback ??= SetRadarSuppressed;
        _setChatSuppressedCallback ??= SetChatSuppressed;
        _setPowerbarSuppressedCallback ??= SetPowerbarSuppressed;
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
        _giveObjectToCallback ??= GiveObjectTo;
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
        _readKnownSpellsCallback ??= ReadKnownSpellsAction;
        _getServerTimeCallback ??= GetServerTimeAction;
        _readObjectEnchantmentsCallback ??= ReadObjectEnchantmentsAction;
        _worldToScreenCallback ??= D3D9.GameMatrixCapture.WorldToScreenCallback;
        _getViewportSizeCallback ??= D3D9.GameMatrixCapture.GetViewportSizeCallback;
        _nav3DClearCallback ??= D3D9.Nav3DRenderer.Nav3DClearCallback;
        _nav3DAddRingCallback ??= D3D9.Nav3DRenderer.Nav3DAddRingCallback;
        _nav3DAddLineCallback ??= D3D9.Nav3DRenderer.Nav3DAddLineCallback;
        _nav3DAddTriangleCallback ??= D3D9.Nav3DRenderer.Nav3DAddTriangleCallback;
        _nav3DAddRingExCallback ??= D3D9.Nav3DRenderer.Nav3DAddRingExCallback;
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
        _getCastBusyStateCallback ??= GetCastBusyStateAction;
        _getUseDoneSeqCallback ??= GetUseDoneSeqAction;
        _forceResetBusyCountCallback ??= ForceResetBusyCountAction;
        _getObjectSpellIdsCallback ??= GetObjectSpellIdsAction;
        _getObjectSkillBuffedCallback ??= GetObjectSkillLevelAction;
        _getObjectAttributeCallback ??= GetObjectAttributeAction;
        _getObjectMotionOnCallback ??= GetObjectMotionOnAction;
        _getObjectStateCallback ??= GetObjectStateAction;
        _getObjectBitfieldCallback ??= GetObjectBitfieldAction;
        _getEngineStatusJsonCallback ??= GetEngineStatusJsonAction;
        _getPluginSnapshotJsonCallback ??= GetPluginSnapshotJsonAction;
        _sendPluginCommandCallback ??= SendPluginCommandAction;
        _getObjectDataIdPropertyCallback ??= GetObjectDataIdPropertyAction;
        _getPluginExportJsonCallback ??= GetPluginExportJsonAction;
        _getPluginInterfaceCallback ??= GetPluginInterfaceAction;
        unsafe { _getLiveObjectIdsCallback ??= GetLiveObjectIdsAction; }
        unsafe { _getLastUseDoneCallback ??= GetLastUseDoneAction; }
        unsafe { _getLastWeenieErrorCallback ??= GetLastWeenieErrorAction; }
        _wieldItemCallback ??= WieldItemAction;
        unsafe { _uiSubmitCallback ??= UiSubmitAction; }
        unsafe { _uiPollEventsCallback ??= UiPollEventsAction; }
        unsafe { _uiGetInfoCallback ??= UiGetInfoAction; }
        unsafe { _getTradeStateCallback ??= GetTradeStateAction; }
        unsafe { _getTradeItemsCallback ??= GetTradeItemsAction; }
        _tradeOpenCallback ??= TradeOpenAction;
        _tradeAddCallback ??= TradeAddAction;
        _tradeAcceptCallback ??= TradeAcceptAction;
        _tradeDeclineCallback ??= TradeDeclineAction;
        _tradeResetCallback ??= TradeResetAction;
        _tradeCloseCallback ??= TradeCloseAction;
        unsafe { _getObjectInstanceIdPropertyCallback ??= GetObjectInstanceIdPropertyAction; }
        unsafe { _getVTankStateCallback ??= GetVTankStateAction; }
        unsafe { _getCharacterTitlesCallback ??= GetCharacterTitlesAction; }
        unsafe { _getServerInfoCallback ??= GetServerInfoAction; }
        _closeContainerCallback ??= CloseContainerAction;
        unsafe { _getScreenModeCallback ??= GetScreenModeAction; }   // v77 (PluginManager.UiHooks.cs)
        _getUiHookFlagsCallback ??= GetUiHookFlagsAction;
        unsafe { _setServerMessageInterestCallback ??= SetServerMessageInterestAction; }
        unsafe { _getTrainingInfoCallback ??= GetTrainingInfoAction; }   // v79
        unsafe { _raiseCallback ??= RaiseAction; }
        _trainSkillCallback ??= TrainSkillAction;
        unsafe { _getContainerOrderCallback ??= GetContainerOrderAction; }   // v80
        _putItemInContainerCallback ??= PutItemInContainerAction;
        unsafe { _salvageDirectCallback ??= SalvageDirectAction; }   // v82
        _getObjectServerAgeCallback ??= GetObjectServerAgeAction;    // v83
        _removeClientObjectCallback ??= RemoveClientObjectAction;    // v83
        unsafe { _getObjectPropertiesCallback ??= GetObjectPropertiesAction; }   // v84
        _getVendorInfoCallback ??= GetVendorInfoAction;
        _getVendorItemsCallback ??= GetVendorItemsAction;
        _vendorBuyCallback ??= VendorBuyAction;
        _vendorSellCallback ??= VendorSellAction;
        _getVendorTradeStatusCallback ??= GetVendorTradeStatusAction;
        _getMergeStackResultCallback ??= GetMergeStackResultAction;
        _armConfirmationCallback ??= ArmConfirmationAction;
        unsafe { _getLastConfirmationCallback ??= GetLastConfirmationAction; }
        unsafe { _getServerStateJsonCallback ??= GetServerStateJsonAction; }
        unsafe { _getWieldedItemsCallback ??= GetWieldedItemsAction; }
        _getUiColorCallback ??= UiKit.GetUiColor;
        _getUiFontCallback ??= UiKit.GetUiFont;
        unsafe { _setPanelOpenCallback ??= UiKit.SetPanelOpen; }
        _panelDragWithLastItemCallback ??= UiKit.PanelDragWithLastItem;   // v90
        unsafe { _setPanelPopOutCallback ??= UiKit.SetPanelPopOut; }
        unsafe { _panelTextSizeCallback ??= UiKit.PanelTextSize; }
        unsafe { _renderSettingsDrawerCallback ??= UiKit.RenderSettingsDrawer; }
        unsafe { _getShortcutsCallback ??= ShortcutApi.GetShortcuts; }
        _shortcutActionCallback ??= ShortcutApi.ShortcutAction;
        unsafe { _getActionKeysCallback ??= ShortcutApi.GetActionKeys; }
        _getIconTextureCallback ??= ShortcutApi.GetIconTexture;
        unsafe { _keyCaptureCallback ??= ShortcutApi.KeyCapture; }
        unsafe { _setActionKeyCallback ??= ShortcutApi.SetActionKey; }
        _acPanelCallback ??= ShortcutApi.AcPanel;
        unsafe { _panelScaleCallback ??= UiKit.PanelScale; }
        unsafe { _dropTargetCallback ??= ShortcutApi.DropTarget; }
        _setAmbientLightFloorCallback ??= AmbientLight.SetFloor;      // v91
        unsafe { _getAmbientLightCallback ??= AmbientLight.Get; }

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
        _api.ReadKnownSpellsFn = Marshal.GetFunctionPointerForDelegate(_readKnownSpellsCallback);
        _api.GetServerTimeFn = Marshal.GetFunctionPointerForDelegate(_getServerTimeCallback);
        _api.ReadObjectEnchantmentsFn = Marshal.GetFunctionPointerForDelegate(_readObjectEnchantmentsCallback);
        _api.WorldToScreenFn = Marshal.GetFunctionPointerForDelegate(_worldToScreenCallback);
        _api.GetViewportSizeFn = Marshal.GetFunctionPointerForDelegate(_getViewportSizeCallback);
        _api.Nav3DClearFn = Marshal.GetFunctionPointerForDelegate(_nav3DClearCallback);
        _api.Nav3DAddRingFn = Marshal.GetFunctionPointerForDelegate(_nav3DAddRingCallback);
        _api.Nav3DAddLineFn = Marshal.GetFunctionPointerForDelegate(_nav3DAddLineCallback);
        _api.Nav3DAddTriangleFn = Marshal.GetFunctionPointerForDelegate(_nav3DAddTriangleCallback);
        _api.Nav3DAddRingExFn = Marshal.GetFunctionPointerForDelegate(_nav3DAddRingExCallback);
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
        _api.GiveObjectToFn = Marshal.GetFunctionPointerForDelegate(_giveObjectToCallback);
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
        _api.GetCastBusyStateFn = Marshal.GetFunctionPointerForDelegate(_getCastBusyStateCallback);
        _api.GetUseDoneSeqFn = Marshal.GetFunctionPointerForDelegate(_getUseDoneSeqCallback);
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
        _api.SetRadarSuppressedFn = Marshal.GetFunctionPointerForDelegate(_setRadarSuppressedCallback);
        _api.SetChatSuppressedFn = Marshal.GetFunctionPointerForDelegate(_setChatSuppressedCallback);
        _api.SetPowerbarSuppressedFn = Marshal.GetFunctionPointerForDelegate(_setPowerbarSuppressedCallback);
        _api.GetEngineStatusJsonFn = Marshal.GetFunctionPointerForDelegate(_getEngineStatusJsonCallback);
        _api.GetPluginSnapshotJsonFn = Marshal.GetFunctionPointerForDelegate(_getPluginSnapshotJsonCallback);
        _api.SendPluginCommandFn = Marshal.GetFunctionPointerForDelegate(_sendPluginCommandCallback);
        _api.GetObjectDataIdPropertyFn = Marshal.GetFunctionPointerForDelegate(_getObjectDataIdPropertyCallback);
        _api.GetPluginExportJsonFn = Marshal.GetFunctionPointerForDelegate(_getPluginExportJsonCallback);
        _api.GetPluginInterfaceFn = Marshal.GetFunctionPointerForDelegate(_getPluginInterfaceCallback);
        _api.GetLiveObjectIdsFn = Marshal.GetFunctionPointerForDelegate(_getLiveObjectIdsCallback);
        _api.GetLastUseDoneFn = Marshal.GetFunctionPointerForDelegate(_getLastUseDoneCallback);
        _api.GetLastWeenieErrorFn = Marshal.GetFunctionPointerForDelegate(_getLastWeenieErrorCallback);
        _api.WieldItemFn = Marshal.GetFunctionPointerForDelegate(_wieldItemCallback);
        _api.UiSubmitFn = Marshal.GetFunctionPointerForDelegate(_uiSubmitCallback);
        _api.UiPollEventsFn = Marshal.GetFunctionPointerForDelegate(_uiPollEventsCallback);
        _api.UiGetInfoFn = Marshal.GetFunctionPointerForDelegate(_uiGetInfoCallback);
        _api.GetTradeStateFn = Marshal.GetFunctionPointerForDelegate(_getTradeStateCallback);
        _api.GetTradeItemsFn = Marshal.GetFunctionPointerForDelegate(_getTradeItemsCallback);
        _api.TradeOpenFn = Marshal.GetFunctionPointerForDelegate(_tradeOpenCallback);
        _api.TradeAddFn = Marshal.GetFunctionPointerForDelegate(_tradeAddCallback);
        _api.TradeAcceptFn = Marshal.GetFunctionPointerForDelegate(_tradeAcceptCallback);
        _api.TradeDeclineFn = Marshal.GetFunctionPointerForDelegate(_tradeDeclineCallback);
        _api.TradeResetFn = Marshal.GetFunctionPointerForDelegate(_tradeResetCallback);
        _api.TradeCloseFn = Marshal.GetFunctionPointerForDelegate(_tradeCloseCallback);
        _api.GetObjectInstanceIdPropertyFn = Marshal.GetFunctionPointerForDelegate(_getObjectInstanceIdPropertyCallback);
        _api.GetVTankStateFn = Marshal.GetFunctionPointerForDelegate(_getVTankStateCallback);
        _api.GetCharacterTitlesFn = Marshal.GetFunctionPointerForDelegate(_getCharacterTitlesCallback);
        _api.GetServerInfoFn = Marshal.GetFunctionPointerForDelegate(_getServerInfoCallback);
        _api.CloseContainerFn = Marshal.GetFunctionPointerForDelegate(_closeContainerCallback);
        _api.GetScreenModeFn = Marshal.GetFunctionPointerForDelegate(_getScreenModeCallback);
        _api.GetUiHookFlagsFn = Marshal.GetFunctionPointerForDelegate(_getUiHookFlagsCallback);
        _api.SetServerMessageInterestFn = Marshal.GetFunctionPointerForDelegate(_setServerMessageInterestCallback);
        _api.GetTrainingInfoFn = Marshal.GetFunctionPointerForDelegate(_getTrainingInfoCallback);
        _api.RaiseFn = Marshal.GetFunctionPointerForDelegate(_raiseCallback);
        _api.TrainSkillFn = Marshal.GetFunctionPointerForDelegate(_trainSkillCallback);
        _api.GetContainerOrderFn = Marshal.GetFunctionPointerForDelegate(_getContainerOrderCallback);
        _api.PutItemInContainerFn = Marshal.GetFunctionPointerForDelegate(_putItemInContainerCallback);
        _api.SalvageDirectFn = Marshal.GetFunctionPointerForDelegate(_salvageDirectCallback);
        _api.GetObjectServerAgeFn = Marshal.GetFunctionPointerForDelegate(_getObjectServerAgeCallback);
        _api.RemoveClientObjectFn = Marshal.GetFunctionPointerForDelegate(_removeClientObjectCallback);
        _api.GetObjectPropertiesFn = Marshal.GetFunctionPointerForDelegate(_getObjectPropertiesCallback);
        _api.GetVendorInfoFn = Marshal.GetFunctionPointerForDelegate(_getVendorInfoCallback);
        _api.GetVendorItemsFn = Marshal.GetFunctionPointerForDelegate(_getVendorItemsCallback);
        _api.VendorBuyFn = Marshal.GetFunctionPointerForDelegate(_vendorBuyCallback);
        _api.VendorSellFn = Marshal.GetFunctionPointerForDelegate(_vendorSellCallback);
        _api.GetVendorTradeStatusFn = Marshal.GetFunctionPointerForDelegate(_getVendorTradeStatusCallback);
        _api.GetMergeStackResultFn = Marshal.GetFunctionPointerForDelegate(_getMergeStackResultCallback);
        _api.ArmConfirmationFn = Marshal.GetFunctionPointerForDelegate(_armConfirmationCallback);
        _api.GetLastConfirmationFn = Marshal.GetFunctionPointerForDelegate(_getLastConfirmationCallback);
        _api.GetServerStateJsonFn = Marshal.GetFunctionPointerForDelegate(_getServerStateJsonCallback);
        _api.GetWieldedItemsFn = Marshal.GetFunctionPointerForDelegate(_getWieldedItemsCallback);
        _api.GetUiColorFn = Marshal.GetFunctionPointerForDelegate(_getUiColorCallback);
        _api.GetUiFontFn = Marshal.GetFunctionPointerForDelegate(_getUiFontCallback);
        _api.SetPanelOpenFn = Marshal.GetFunctionPointerForDelegate(_setPanelOpenCallback);
        _api.PanelDragWithLastItemFn = Marshal.GetFunctionPointerForDelegate(_panelDragWithLastItemCallback);
        _api.SetPanelPopOutFn = Marshal.GetFunctionPointerForDelegate(_setPanelPopOutCallback);
        _api.PanelTextSizeFn = Marshal.GetFunctionPointerForDelegate(_panelTextSizeCallback);
        _api.RenderSettingsDrawerFn = Marshal.GetFunctionPointerForDelegate(_renderSettingsDrawerCallback);
        _api.GetShortcutsFn = Marshal.GetFunctionPointerForDelegate(_getShortcutsCallback);
        _api.ShortcutActionFn = Marshal.GetFunctionPointerForDelegate(_shortcutActionCallback);
        _api.GetActionKeysFn = Marshal.GetFunctionPointerForDelegate(_getActionKeysCallback);
        _api.GetIconTextureFn = Marshal.GetFunctionPointerForDelegate(_getIconTextureCallback);
        _api.KeyCaptureFn = Marshal.GetFunctionPointerForDelegate(_keyCaptureCallback);
        _api.SetActionKeyFn = Marshal.GetFunctionPointerForDelegate(_setActionKeyCallback);
        _api.AcPanelFn = Marshal.GetFunctionPointerForDelegate(_acPanelCallback);
        _api.PanelScaleFn = Marshal.GetFunctionPointerForDelegate(_panelScaleCallback);
        _api.DropTargetFn = Marshal.GetFunctionPointerForDelegate(_dropTargetCallback);
        _api.SetAmbientLightFloorFn = Marshal.GetFunctionPointerForDelegate(_setAmbientLightFloorCallback);
        _api.GetAmbientLightFn = Marshal.GetFunctionPointerForDelegate(_getAmbientLightCallback);
        RouteApiThroughLoader();
    }

    /// <summary>
    /// Plugins copy the API table and can never give it back, so under the native loader
    /// every function pointer in it becomes one of the loader's permanent stubs
    /// (Hooking/LoaderServices): after this engine generation shuts down, a plugin thread
    /// that still calls the host waits for the next generation instead of jumping into
    /// unloaded code. Fields 0-3 (Version, ImGuiContext, D3DDevice, GameHwnd) are data.
    /// </summary>
    private static unsafe void RouteApiThroughLoader()
    {
        if (!Hooking.LoaderServices.Available)
            return;
        fixed (RynthCoreAPI* api = &_api)
        {
            IntPtr* slots = (IntPtr*)api;
            int count = sizeof(RynthCoreAPI) / sizeof(IntPtr);
            for (int i = 4; i < count; i++)
                if (slots[i] != IntPtr.Zero)
                    slots[i] = Hooking.LoaderServices.ApiStub(i, slots[i]);
        }
    }

    private static int _loggedOffThreadProbe;

    private static void ProbeClientHooks()
    {
        // Host ProbeClientHooksFn is called from the plugin pump. A probe Resets and
        // rebinds the combat/movement/object delegates the main-thread drain is using
        // and re-scans AC memory, so it must not run there: off the main thread,
        // ask MainThreadSnapshots.Tick to re-probe on AC's main thread (only if the
        // action hooks are not initialized; bootstrap already probed them).
        // Fire-and-forget: plugins read readiness through GetClientHookFlags.
        if (!MainThreadGuard.IsOnMainThread())
        {
            if (!ClientActionHooks.GetStatus().CombatInitialized)
                MainThreadSnapshots.RequestClientHookProbe();
            if (System.Threading.Interlocked.Exchange(ref _loggedOffThreadProbe, 1) == 0)
                RynthLog.Compat("ProbeClientHooks: called off AC's main thread - "
                    + (ClientActionHooks.GetStatus().CombatInitialized
                        ? "hooks already initialized, nothing to do."
                        : "re-probe queued for AC's main thread."));
            return;
        }
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
        Compatibility.AcActionTrace.Record("ChangeCombatMode", (uint)combatMode);
        return ToAbiBool(ClientActionHooks.ChangeCombatMode(combatMode));
    }

    private static int CancelAttack()
    {
        Compatibility.AcActionTrace.Record("CancelAttack");
        return ToAbiBool(ClientActionHooks.CancelAttack());
    }

    private static int QueryHealth(uint targetId)
    {
        return ToAbiBool(ClientActionHooks.QueryHealth(targetId));
    }

    private static int RequestIdAction(uint objectId)
    {
        // A plugin's identify: never limited by AutoIdMode; its 0xC8 is tagged [eng:plugin].
        using (Net.EngineSendMarker.Enter(Net.IdRequester.Plugin))
            return ToAbiBool(ClientActionHooks.RequestId(objectId));
    }

    private static int CastSpellAction(uint targetId, int spellId)
    {
        Compatibility.AcActionTrace.Record("CastSpell", targetId, (uint)spellId);
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

    private static unsafe int GetObjectDataIdPropertyAction(uint objectId, uint stype, uint* value)
    {
        if (!ClientObjectHooks.TryGetObjectDataIdProperty(objectId, stype, out uint v))
            return 0;

        *value = v;
        return 1;
    }

    private static unsafe int GetVTankStateAction(int* sequence)
    {
        try
        {
            uint flags = Compatibility.VTankWatch.GetPluginState(out int seq);
            if (sequence != null)
                *sequence = seq;
            return (int)flags;
        }
        catch
        {
            return 0;
        }
    }

    private static unsafe int GetCharacterTitlesAction(uint* ids, int maxCount, uint* currentTitle)
    {
        try
        {
            return Compatibility.CharacterTitles.Copy(ClientHelperHooks.GetPlayerId(), ids, Math.Max(0, maxCount), currentTitle);
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// v86: the server list as JSON (UI/Sections/ServerStateJson.cs) from the published snapshot.
    /// Any thread: the snapshot is immutable and the strings are volatile reads. -1 on failure.
    /// </summary>
    /// <summary>v87: what the player wields (EquipmentTracker). -1 while it can't start.</summary>
    private static unsafe int GetWieldedItemsAction(uint* ids, uint* locations, int max)
    {
        try
        {
            if (ids == null || locations == null || max <= 0) return -1;
            return Compatibility.EquipmentTracker.Copy(new Span<uint>(ids, max), new Span<uint>(locations, max));
        }
        catch
        {
            return -1;
        }
    }

    private static unsafe int GetServerStateJsonAction(byte* buffer, int capacity)
    {
        try
        {
            UI.Sections.SectionRegistry.Snapshot snap = UI.Sections.SectionRegistry.Current;
            string json = UI.Sections.ServerStateJson.Build(snap.Servers, snap.Manifests, snap.Revision,
                ServerInfo.LaunchHost, AccountHooks.AnnouncedWorldName, UI.Sections.SectionRegistry.EnginePlugin);
            return UI.Sections.ServerStateJson.Write(json, buffer, capacity);
        }
        catch
        {
            return -1;
        }
    }

    private static unsafe int GetServerInfoAction(byte* worldName, int capacity)
    {
        try
        {
            ServerVerdict v = ServerInfo.Current;
            int flags = (v.IsAelrynth ? 1 : 0) | (v.IsStaging ? 2 : 0);
            string? world = AccountHooks.AnnouncedWorldName;
            if (!string.IsNullOrEmpty(world))
            {
                flags |= 4;
                if (worldName != null && capacity > 0)
                {
                    byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(world);
                    int n = Math.Min(utf8.Length, capacity - 1);
                    while (n > 0 && n < utf8.Length && (utf8[n] & 0xC0) == 0x80) n--;
                    for (int i = 0; i < n; i++) worldName[i] = utf8[i];
                    worldName[n] = 0;
                }
            }
            else if (worldName != null && capacity > 0)
            {
                worldName[0] = 0;
            }
            return flags;
        }
        catch
        {
            return 0;
        }
    }

    private static unsafe int GetObjectInstanceIdPropertyAction(uint objectId, uint stype, uint* value)
    {
        if (!ClientObjectHooks.TryGetObjectInstanceIdProperty(objectId, stype, out uint v))
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

    private static unsafe int ReadKnownSpellsAction(uint* spellIds, int maxCount)
    {
        return ClientObjectHooks.CopyCachedKnownSpells(spellIds, maxCount);
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
        Compatibility.AcActionTrace.Record("MeleeAttack", targetId, (uint)attackHeight);
        return ToAbiBool(ClientActionHooks.MeleeAttack(targetId, attackHeight, powerLevel));
    }

    private static int MissileAttack(uint targetId, int attackHeight, float accuracyLevel)
    {
        Compatibility.AcActionTrace.Record("MissileAttack", targetId, (uint)attackHeight);
        return ToAbiBool(ClientActionHooks.MissileAttack(targetId, attackHeight, accuracyLevel));
    }

    private static int NativeAttackAction(int attackHeight, float power)
    {
        Compatibility.AcActionTrace.Record("NativeAttack", 0, (uint)attackHeight);
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

    private static void SetRadarSuppressed(int enabled)
    {
        RadarHooks.SuppressOriginalDraw = enabled != 0;
    }

    private static void SetChatSuppressed(int enabled)
    {
        ChatHooks.SuppressOriginalChat = enabled != 0;
    }

    private static void SetPowerbarSuppressed(int enabled)
    {
        bool was = PowerbarHooks.SuppressOriginalDraw;
        bool now = enabled != 0;
        PowerbarHooks.SuppressOriginalDraw = now;
        if (was != now)
            RynthLog.Compat($"Compat: powerbar suppress -> {now}");
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

    // ── SelectItem dedupe window ─────────────────────────────────────────
    // Coalesces repeat-select spam (RynthAi historically issues 3-8 calls
    // for the same target within 100-400 ms) into a single AC SetSelectedObject
    // invocation. Diagnoses match the "too many switches" hypothesis: the
    // gen1 cold-launch crash at 13 min ended in AV inside
    // acclient.exe!PackableHashTable<ulong,Skill>::EmptyContents — AC teardown
    // walking a Skill quality table whose entries were already freed by a
    // back-to-back SelectItem that re-triggered teardown before the first
    // finished. 150 ms is short enough that user-initiated target switches
    // feel instant (well below the per-frame budget for either AC's 33 Hz or
    // RynthAi's 10 Hz polling) and long enough to swallow the burst pattern.
    // Per-target state (no global lock) — racy reads on x86 are acceptable
    // because false-positives just delay one extra select by <150 ms.
    private const int SelectItemDedupeMs = 150;
    private static uint _lastSelectItemTarget;
    private static long _lastSelectItemTicks;

    private static int SelectItem(uint objectId)
    {
        long nowTicks = Environment.TickCount64;
        uint prevTarget = _lastSelectItemTarget;
        long prevTicks = System.Threading.Volatile.Read(ref _lastSelectItemTicks);

        if (objectId == prevTarget && objectId != 0 && (nowTicks - prevTicks) < SelectItemDedupeMs)
        {
            // Suppress: same target seen within the window. Record under a
            // distinct trace name so post-crash analysis shows the dedupe is
            // actually firing in the wild. Return success because the prior
            // call already established the selection AC needs.
            Compatibility.AcActionTrace.Record("SelectItem-dedup", objectId);
            return 1;
        }

        _lastSelectItemTarget = objectId;
        System.Threading.Volatile.Write(ref _lastSelectItemTicks, nowTicks);
        Compatibility.AcActionTrace.Record("SelectItem", objectId);
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
        Compatibility.AcActionTrace.Record("UseObject", objectId);
        LogPluginUse("UseObject", objectId, 0);
        return ToAbiBool(ClientHelperHooks.UseObject(objectId));
    }

    private static int UseObjectOn(uint sourceObjectId, uint targetObjectId)
    {
        Compatibility.AcActionTrace.Record("UseObjectOn", sourceObjectId, targetObjectId);
        LogPluginUse("UseObjectOn", sourceObjectId, targetObjectId);
        return ToAbiBool(ClientHelperHooks.UseObjectOn(sourceObjectId, targetObjectId));
    }

    // ── Use backstop log (2026-10-04) ─────────────────────────────────────────
    // One line per plugin use, whichever plugin asked, so a use nobody explains
    // shows up in the log (doors and corpses opening "on their own" went
    // unexplained for months because no use was logged). The plugins log the
    // why next to it ("[Use] RynthAi/..."); this line is the engine's record
    // that the use was sent. The same object (and target) is logged at most
    // once per UseLogRepeatMs: repeats in between are counted and reported on
    // the next line for it, so a fight's weapon swaps don't flood the log.
    private const long UseLogRepeatMs = 3000;
    private static readonly object UseLogGate = new();
    private static readonly Dictionary<ulong, (long LastMs, int Suppressed)> UseLogSeen = new();

    private static void LogPluginUse(string what, uint objectId, uint targetId)
    {
        try
        {
            long now = Environment.TickCount64;
            ulong key = ((ulong)targetId << 32) | objectId;
            int suppressed;
            lock (UseLogGate)
            {
                if (UseLogSeen.TryGetValue(key, out var seen) && now - seen.LastMs < UseLogRepeatMs)
                {
                    UseLogSeen[key] = (seen.LastMs, seen.Suppressed + 1);
                    return;
                }
                suppressed = UseLogSeen.TryGetValue(key, out seen) ? seen.Suppressed : 0;
                if (UseLogSeen.Count > 256) UseLogSeen.Clear();
                UseLogSeen[key] = (now, 0);
            }
            string name = ClientActionHooks.TryGetObjectName(objectId, out string n) && !string.IsNullOrWhiteSpace(n) ? n : "?";
            string target = "";
            if (targetId != 0)
            {
                string tn = ClientActionHooks.TryGetObjectName(targetId, out string t) && !string.IsNullOrWhiteSpace(t) ? t : "?";
                target = $" on 0x{targetId:X8} '{tn}'";
            }
            string more = suppressed > 0 ? $" (+{suppressed} repeat(s) since its last line)" : "";
            RynthLog.Info($"[Use] engine: {what} 0x{objectId:X8} '{name}'{target}{more}");
        }
        catch { }
    }

    private static int UseEquippedItem(uint sourceObjectId, uint targetObjectId)
    {
        Compatibility.AcActionTrace.Record("UseEquippedItem", sourceObjectId, targetObjectId);
        return ToAbiBool(ClientHelperHooks.UseEquippedItem(sourceObjectId, targetObjectId));
    }

    private static int MoveItemExternal(uint objectId, uint targetContainerId, int amount)
    {
        Compatibility.AcActionTrace.Record("MoveItemExternal", objectId, targetContainerId);
        return ToAbiBool(ClientHelperHooks.MoveItemExternal(objectId, targetContainerId, amount));
    }

    private static int MoveItemInternal(uint objectId, uint targetContainerId, int slot, int amount)
    {
        Compatibility.AcActionTrace.Record("MoveItemInternal", objectId, targetContainerId);
        return ToAbiBool(ClientHelperHooks.MoveItemInternal(objectId, targetContainerId, slot, amount));
    }

    private static int SplitStackInternal(uint objectId, uint targetContainerId, int slot, int amount)
    {
        Compatibility.AcActionTrace.Record("SplitStackInternal", objectId, targetContainerId);
        return ToAbiBool(ClientHelperHooks.SplitStackInternal(objectId, targetContainerId, slot, amount));
    }

    private static int MergeStackInternal(uint sourceObjectId, uint targetObjectId)
    {
        Compatibility.AcActionTrace.Record("MergeStackInternal", sourceObjectId, targetObjectId);
        return ToAbiBool(ClientHelperHooks.MergeStackInternal(sourceObjectId, targetObjectId));
    }

    // v68: outcome of the latest MergeStackInternal(source, target) (MergeStackResults codes).
    private static unsafe int GetMergeStackResultAction(uint sourceObjectId, uint targetObjectId, int* amount, int* ageMs)
    {
        try
        {
            int status = MergeStackResults.Get(sourceObjectId, targetObjectId, out int sent, out int age);
            if (amount != null) *amount = sent;
            if (ageMs != null) *ageMs = age;
            return status;
        }
        catch
        {
            return MergeStackResults.None;
        }
    }

    // v85: arm one answer for the next server yes/no prompt (ConfirmationResponder).
    private static int ArmConfirmationAction(uint type, int minPercent, int timeoutMs)
    {
        try { return ConfirmationResponder.Arm(type, minPercent, timeoutMs); }
        catch { return 0; }
    }

    // v85: the last server yes/no prompt and what happened to it.
    private static unsafe int GetLastConfirmationAction(uint* type, uint* contextId, int* percent, int* outcome, int* ageMs)
    {
        try
        {
            int seq = ConfirmationResponder.GetLast(out uint t, out uint ctx, out int pct, out int oc, out int age);
            if (type != null) *type = t;
            if (contextId != null) *contextId = ctx;
            if (percent != null) *percent = pct;
            if (outcome != null) *outcome = oc;
            if (ageMs != null) *ageMs = age;
            return seq;
        }
        catch
        {
            return 0;
        }
    }

    private static int GiveObjectTo(uint objectId, uint targetId, int amount)
    {
        Compatibility.AcActionTrace.Record("GiveObjectTo", objectId, targetId);
        return ToAbiBool(ClientHelperHooks.GiveObjectTo(objectId, targetId, amount));
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

    // ─── Status export / cross-plugin bridges (v64) ─────────────────────────
    // Additive, benign accessors so a plugin (RynthRemote) can own the remote
    // status export + command drain while the public engine ships no remote
    // feature itself. None of these network or mutate AC state.

    private static IntPtr GetEngineStatusJsonAction()
    {
        try
        {
            string json = Compatibility.EngineStatusMetrics.BuildEngineStatusJson();
            if (string.IsNullOrEmpty(json))
                return IntPtr.Zero;

            // [ThreadStatic] free-then-realloc, same discipline as the account/
            // world-name pulls: the returned pointer is valid until this thread
            // calls again; the consumer copies it immediately.
            if (_engineStatusJsonScratchPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_engineStatusJsonScratchPtr);
                _engineStatusJsonScratchPtr = IntPtr.Zero;
            }
            _engineStatusJsonScratchPtr = Marshal.StringToHGlobalAnsi(json);
            return _engineStatusJsonScratchPtr;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static unsafe IntPtr GetPluginSnapshotJsonAction(IntPtr pluginNameAnsi)
    {
        try
        {
            string? name = pluginNameAnsi != IntPtr.Zero ? Marshal.PtrToStringAnsi(pluginNameAnsi) : null;
            if (string.IsNullOrEmpty(name))
                return IntPtr.Zero;

            IntPtr export = ResolvePluginExport(name, "RynthPluginGetSnapshotJson");
            if (export == IntPtr.Zero)
                return IntPtr.Zero;

            // The target plugin owns the returned buffer (kept valid until its
            // next snapshot call); pass it straight through — the caller copies it.
            return ((delegate* unmanaged[Cdecl]<IntPtr>)export)();
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static unsafe IntPtr GetPluginExportJsonAction(IntPtr pluginNameAnsi, IntPtr exportNameAnsi)
    {
        try
        {
            string? name = pluginNameAnsi != IntPtr.Zero ? Marshal.PtrToStringAnsi(pluginNameAnsi) : null;
            string? export = exportNameAnsi != IntPtr.Zero ? Marshal.PtrToStringAnsi(exportNameAnsi) : null;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(export))
                return IntPtr.Zero;

            // Safety: only broker the parameterless JSON-getter convention (no-arg, returns const char*).
            // Calling an export with a different signature as <IntPtr>() would corrupt the stack.
            if (!export.StartsWith("RynthPluginGet", StringComparison.Ordinal) ||
                !export.EndsWith("Json", StringComparison.Ordinal))
                return IntPtr.Zero;

            IntPtr fn = ResolvePluginExport(name, export);
            if (fn == IntPtr.Zero)
                return IntPtr.Zero;

            // The target plugin owns the returned buffer (valid until its next call on that export);
            // pass it straight through — the caller copies it immediately.
            return ((delegate* unmanaged[Cdecl]<IntPtr>)fn)();
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// v68: a named plugin's typed interface table, through its fixed-signature
    /// <c>void* RynthPluginQueryInterface(const char* iface, uint version)</c> export (never
    /// an arbitrary export, so no signature mismatch can reach the stack). Null if missing.
    /// </summary>
    private static unsafe IntPtr GetPluginInterfaceAction(IntPtr pluginNameAnsi, IntPtr ifaceAnsi, uint version)
    {
        try
        {
            string? name = pluginNameAnsi != IntPtr.Zero ? Marshal.PtrToStringAnsi(pluginNameAnsi) : null;
            if (string.IsNullOrEmpty(name) || ifaceAnsi == IntPtr.Zero)
                return IntPtr.Zero;
            IntPtr fn = ResolvePluginExport(name, "RynthPluginQueryInterface");
            if (fn == IntPtr.Zero)
                return IntPtr.Zero;
            return ((delegate* unmanaged[Cdecl]<IntPtr, uint, IntPtr>)fn)(ifaceAnsi, version);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>v69: the engine's live object id snapshot (see PluginContract.GetLiveObjectIdsFn).</summary>
    private static unsafe int GetLiveObjectIdsAction(uint* buffer, int capacity)
    {
        try
        {
            uint[] ids = ClientObjectHooks.LiveObjectIds;
            if (buffer != null && capacity > 0)
            {
                int n = Math.Min(capacity, ids.Length);
                for (int i = 0; i < n; i++)
                    buffer[i] = ids[i];
            }
            return ids.Length;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>v70: the last UseDone's seq + error code (see PluginContract.GetLastUseDoneFn).</summary>
    private static unsafe int GetLastUseDoneAction(int* seq, uint* error)
    {
        try
        {
            bool ok = SmartBoxHooks.TryGetLastUseDone(out int s, out uint e);
            if (seq != null) *seq = s;
            if (error != null) *error = e;
            return ok ? 1 : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>v70: the last server refusal (see PluginContract.GetLastWeenieErrorFn).</summary>
    private static unsafe int GetLastWeenieErrorAction(int* seq, uint* error, uint* eventType, uint* objectId)
    {
        try
        {
            bool ok = SmartBoxHooks.TryGetLastWeenieError(out int s, out uint e, out uint t, out uint o);
            if (seq != null) *seq = s;
            if (error != null) *error = e;
            if (eventType != null) *eventType = t;
            if (objectId != null) *objectId = o;
            return ok ? 1 : 0;
        }
        catch
        {
            return 0;
        }
    }

    // ── v71: script windows ─────────────────────────────────────────────

    // The plugin being called on this thread (Tick, Init, lifecycle events, Shutdown), so
    // UiSubmit / UiPollEvents know their owner. Written only by the dispatching thread; a
    // call from any other thread (a plugin's own thread, AC's thread) sees no owner.
    private static LoadedPlugin? _dispatchPlugin;
    private static int _dispatchThread;

    // What the dispatch in progress calls and since when (PumpDispatchInfo), for the hang
    // watchdog: a plugin export that faults natively ends the process with only TickAll in
    // the event log, so the watchdog's report during the crash-dump freeze names the plugin.
    private static string? _dispatchExport;
    private static long _dispatchSinceMs;

    private static LoadedPlugin? EnterDispatch(LoadedPlugin plugin, string export = "event")
    {
        int thread = Environment.CurrentManagedThreadId;
        LoadedPlugin? outer = _dispatchThread == thread ? _dispatchPlugin : null;
        _dispatchThread = thread;
        _dispatchExport = export;
        _dispatchSinceMs = Environment.TickCount64;
        _dispatchPlugin = plugin;
        return outer;
    }

    private static void LeaveDispatch(LoadedPlugin? outer)
    {
        _dispatchPlugin = outer;
        if (outer == null)
            _dispatchSinceMs = 0;
    }

    /// <summary>
    /// "inside RynthAi Tick for 12375 ms" while a plugin call has been running for a second
    /// or more, else null. Any thread (the hang watchdog); racy by design.
    /// </summary>
    internal static string? DescribePumpDispatch()
    {
        LoadedPlugin? plugin = _dispatchPlugin;
        return PumpDispatchInfo.Describe(plugin?.DisplayName, _dispatchExport, Volatile.Read(ref _dispatchSinceMs), Environment.TickCount64);
    }

    /// <summary>The plugin whose tick, event, Init or Shutdown is running on the calling thread, or null.</summary>
    internal static LoadedPlugin? CurrentDispatch
    {
        get
        {
            LoadedPlugin? plugin = _dispatchPlugin;
            return plugin != null && _dispatchThread == Environment.CurrentManagedThreadId ? plugin : null;
        }
    }

    /// <summary>v71: see PluginContract.UiSubmitFn.</summary>
    private static unsafe int UiSubmitAction(byte* data, int length)
    {
        try
        {
            LoadedPlugin? owner = CurrentDispatch;
            if (owner == null) return -2;
            return ScriptWindowRegistry.Submit(owner, data, length);
        }
        catch (Exception ex)
        {
            RynthLog.Plugin($"PluginManager: UiSubmit threw {ex.GetType().Name}: {ex.Message}{PluginTrace(ex)}");
            return -1;
        }
    }

    /// <summary>v71: see PluginContract.UiPollEventsFn.</summary>
    private static unsafe int UiPollEventsAction(byte* buffer, int capacity, int* remaining)
    {
        try
        {
            if (remaining != null) *remaining = 0;
            LoadedPlugin? owner = CurrentDispatch;
            if (owner == null) return -2;
            return ScriptWindowRegistry.PollEvents(owner, buffer, capacity, remaining);
        }
        catch (Exception ex)
        {
            RynthLog.Plugin($"PluginManager: UiPollEvents threw {ex.GetType().Name}: {ex.Message}{PluginTrace(ex)}");
            return 0;
        }
    }

    /// <summary>v71: see PluginContract.UiGetInfoFn.</summary>
    private static unsafe int UiGetInfoAction(UiInfoNative* info)
    {
        try
        {
            return ScriptWindowRegistry.GetInfo(info);
        }
        catch
        {
            return 0;
        }
    }

    // ── v72: player-to-player trade ────────────────────────────────────

    /// <summary>v72: see PluginContract.GetTradeStateFn.</summary>
    private static unsafe int GetTradeStateAction(TradeStateNative* state)
    {
        try
        {
            return PlayerTrade.GetState(state);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>v72: see PluginContract.GetTradeItemsFn.</summary>
    private static unsafe int GetTradeItemsAction(int side, uint* buffer, int capacity)
    {
        try
        {
            return PlayerTrade.GetItems(side, buffer, capacity);
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>v72: see PluginContract.TradeOpenFn.</summary>
    private static int TradeOpenAction(uint targetId) => ToAbiBool(PlayerTrade.Open(targetId));

    /// <summary>v72: see PluginContract.TradeAddFn.</summary>
    private static int TradeAddAction(uint itemId, uint slot) => ToAbiBool(PlayerTrade.Add(itemId, slot));

    /// <summary>v72: see PluginContract.TradeAcceptFn.</summary>
    private static int TradeAcceptAction() => ToAbiBool(PlayerTrade.Accept());

    /// <summary>v72: see PluginContract.TradeDeclineFn.</summary>
    private static int TradeDeclineAction() => ToAbiBool(PlayerTrade.Decline());

    /// <summary>v72: see PluginContract.TradeResetFn.</summary>
    private static int TradeResetAction() => ToAbiBool(PlayerTrade.Reset());

    /// <summary>v72: see PluginContract.TradeCloseFn.</summary>
    private static int TradeCloseAction() => ToAbiBool(PlayerTrade.Close());

    // ── v79: spending experience (Compatibility/TrainingApi.cs) ─────────

    /// <summary>v79: see PluginContract.GetTrainingInfoFn.</summary>
    private static unsafe int GetTrainingInfoAction(TrainingInfoNative* info, TrainingEntryNative* entries, int maxEntries)
    {
        try
        {
            return TrainingApi.GetInfo(info, entries, maxEntries);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>v79: see PluginContract.RaiseFn.</summary>
    private static unsafe int RaiseAction(uint kind, uint stype, uint ranks, long expectedXp, long* xpSent)
    {
        try
        {
            RaiseStatus r = TrainingApi.Raise(kind, stype, ranks, expectedXp, out long sent);
            if (xpSent != null) *xpSent = sent;
            return (int)r;
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"Raise failed - {ex.GetType().Name}: {ex.Message}");
            return (int)RaiseStatus.SendFailed;
        }
    }

    /// <summary>v79: see PluginContract.TrainSkillFn.</summary>
    private static int TrainSkillAction(uint stype, int expectedCredits)
    {
        try
        {
            return (int)TrainingApi.TrainSkill(stype, expectedCredits);
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"TrainSkill failed - {ex.GetType().Name}: {ex.Message}");
            return (int)RaiseStatus.SendFailed;
        }
    }

    // ── v80: slot order and placed moves (Net/InventoryOrder, ClientHelperHooks) ──

    /// <summary>v80: see PluginContract.GetContainerOrderFn.</summary>
    private static unsafe int GetContainerOrderAction(uint containerId, int slotKind, uint* ids, int maxIds)
    {
        try
        {
            if (containerId == 0 || slotKind is < 0 or > 1 || maxIds < 0 || (ids == null && maxIds > 0)) return -1;
            if (!Net.InventoryOrder.TryGetOrder(containerId, slotKind == 1, out uint[] order)) return -1;
            int n = Math.Min(order.Length, maxIds);
            for (int i = 0; i < n; i++) ids[i] = order[i];
            return order.Length;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>v80: see PluginContract.PutItemInContainerFn.</summary>
    private static int PutItemInContainerAction(uint itemId, uint containerId, int placement)
    {
        try
        {
            if (!ClientHelperHooks.HasPutItemInContainer) return -1;
            Compatibility.AcActionTrace.Record("PutItemInContainer", itemId, containerId);
            return ToAbiBool(ClientHelperHooks.PutItemInContainerAt(itemId, containerId, placement));
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"PutItemInContainer failed - {ex.GetType().Name}: {ex.Message}");
            return 0;
        }
    }

    // ── v84: every property of an object (ObjectPropertyDump) ──

    /// <summary>v84: see PluginContract.GetObjectPropertiesFn.</summary>
    private static unsafe int GetObjectPropertiesAction(uint objectId, byte* buffer, int bufferSize)
    {
        try
        {
            if (objectId == 0 || bufferSize < 0 || (buffer == null && bufferSize > 0)) return -1;
            byte[]? rec = Compatibility.ObjectPropertyDump.Collect(objectId);
            if (rec == null) return 0;
            if (rec.Length <= bufferSize)
                rec.AsSpan().CopyTo(new Span<byte>(buffer, bufferSize));
            return rec.Length;
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"GetObjectProperties 0x{objectId:X8} failed - {ex.GetType().Name}: {ex.Message}");
            return -1;
        }
    }

    // ── v83: ghost creatures (ObjectFreshness, DeleteObjectHooks.RemoveClientObject) ──

    /// <summary>v83: see PluginContract.GetObjectServerAgeFn.</summary>
    private static int GetObjectServerAgeAction(uint objectId)
    {
        try
        {
            long age = Compatibility.ObjectFreshness.AgeMs(objectId, Environment.TickCount64);
            return age < 0 ? -1 : (int)Math.Min(age, int.MaxValue);
        }
        catch { return -1; }
    }

    /// <summary>v83: see PluginContract.RemoveClientObjectFn.</summary>
    private static int RemoveClientObjectAction(uint objectId)
    {
        try
        {
            var r = Compatibility.DeleteObjectHooks.RemoveClientObject(objectId, out string? why);
            if (r == Compatibility.DeleteObjectHooks.RemoveResult.Refused && why != null)
                RynthLog.Compat($"RemoveClientObject 0x{objectId:X8}: refused - {why}.");
            return (int)r;
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"RemoveClientObject failed - {ex.GetType().Name}: {ex.Message}");
            return 0;
        }
    }

    // ── v82: salvage without the panel (ClientHelperHooks.SalvageDirect) ──

    /// <summary>v82: see PluginContract.SalvageDirectFn.</summary>
    private static unsafe int SalvageDirectAction(uint toolId, uint* itemIds, int count)
    {
        try
        {
            if (!ClientHelperHooks.HasSalvageDirect) return -1;
            if (toolId == 0 || itemIds == null || count <= 0 || count > ClientHelperHooks.MaxSalvageDirectItems) return 0;
            var ids = new uint[count];
            for (int i = 0; i < count; i++) ids[i] = itemIds[i];
            Compatibility.AcActionTrace.Record("SalvageDirect", toolId, (uint)count);
            return ClientHelperHooks.SalvageDirect(toolId, ids) ? count : 0;
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"SalvageDirect failed - {ex.GetType().Name}: {ex.Message}");
            return 0;
        }
    }

    /// <summary>v78: see PluginContract.SetServerMessageInterestFn.</summary>
    private static unsafe int SetServerMessageInterestAction(uint* opcodes, int opcodeCount, uint* gameEvents, int eventCount)
    {
        try
        {
            LoadedPlugin? owner = CurrentDispatch;
            if (owner == null)
                return -1;
            if (opcodeCount == 0 && eventCount == 0)
            {
                owner.ServerMessageInterest?.Clear();
                RynthLog.Plugin($"PluginManager: {owner.DisplayName} stopped server messages.");
                return 1;
            }
            if (owner.OnServerMessagePtr == IntPtr.Zero)
                owner.OnServerMessagePtr = GetProcAddress(owner.ModuleHandle, "RynthPluginOnServerMessage");
            if (owner.OnServerMessagePtr == IntPtr.Zero)
                return 0;
            var interest = owner.ServerMessageInterest ??= new Net.ServerMessageInterest();
            if (!interest.Set(opcodes, opcodeCount, gameEvents, eventCount))
                return -3;
            RynthLog.Plugin($"PluginManager: {owner.DisplayName} wants server messages: " +
                (interest.All ? "all" : $"{interest.OpcodeCount} opcode(s), {interest.EventCount} game event(s){(interest.AllGameEvents ? " + all game events" : "")}"));
            return EngineSettings.ServerMessageStream ? 1 : 2;
        }
        catch (Exception ex)
        {
            RynthLog.Plugin($"PluginManager: SetServerMessageInterest failed - {ex.GetType().Name}: {ex.Message}");
            return -3;
        }
    }

    /// <summary>v78: initialized, healthy plugins with a non-empty server-message interest.</summary>
    internal static int ServerMessageSubscriberCount()
    {
        int n = 0;
        for (int i = 0; i < _plugins.Count; i++)
        {
            var p = _plugins[i];
            if (p.Initialized && !p.Failed && p.OnServerMessagePtr != IntPtr.Zero &&
                p.ServerMessageInterest is { IsEmpty: false })
                n++;
        }
        return n;
    }

    /// <summary>
    /// v78: one reassembled server message to the plugins that asked for it (called from
    /// ServerMessageStream.Pump, on this pump). <paramref name="body"/> is the message after
    /// its opcode, valid only during the call. Returns how many plugins were called.
    /// </summary>
    internal static unsafe int DispatchServerMessage(uint opcode, uint eventType, byte* body, int length)
    {
        int called = 0;
        for (int i = 0; i < _plugins.Count; i++)
        {
            var plugin = _plugins[i];
            if (!plugin.Initialized || plugin.Failed || plugin.OnServerMessagePtr == IntPtr.Zero)
                continue;
            var interest = plugin.ServerMessageInterest;
            if (interest == null || !interest.Wants(opcode, eventType))
                continue;

            LoadedPlugin? outer = EnterDispatch(plugin);
            try
            {
                ((delegate* unmanaged[Cdecl]<uint, byte*, int, void>)plugin.OnServerMessagePtr)(opcode, body, length);
                called++;
            }
            catch (Exception ex)
            {
                plugin.Failed = true;
                RynthLog.Error($"PluginManager: {plugin.DisplayName} OnServerMessage threw {ex.GetType().Name}: {ex.Message}{PluginTrace(ex)} - disabled.");
            }
            finally
            {
                LeaveDispatch(outer);
            }
        }
        return called;
    }

    /// <summary>v76: close an external container (see PluginContract.CloseContainerFn).</summary>
    private static int CloseContainerAction(uint containerId)
    {
        Compatibility.AcActionTrace.Record("CloseContainer", containerId);
        return ToAbiBool(ClientHelperHooks.CloseContainer(containerId));
    }

    /// <summary>v70: wield into a chosen slot (see PluginContract.WieldItemFn).</summary>
    private static int WieldItemAction(uint objectId, uint equipMask)
    {
        Compatibility.AcActionTrace.Record("WieldItem", objectId, equipMask);
        return ToAbiBool(ClientHelperHooks.WieldItem(objectId, equipMask));
    }

    /// <summary>
    /// The engine's own (action, value) to a loaded plugin's optional RynthPluginApplyRemoteCommand
    /// (the plugin copies the strings and queues them). False when the plugin isn't loaded and
    /// ready or has no such export. Same thread rules as ResolvePluginExport.
    /// </summary>
    internal static unsafe bool SendPluginCommand(string pluginName, string action, string value)
    {
        IntPtr export = ResolvePluginExport(pluginName, "RynthPluginApplyRemoteCommand");
        if (export == IntPtr.Zero || string.IsNullOrEmpty(action)) return false;
        IntPtr a = Marshal.StringToHGlobalAnsi(action);
        IntPtr v = Marshal.StringToHGlobalAnsi(value ?? string.Empty);
        try
        {
            ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)export)(a, v);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(a);
            Marshal.FreeHGlobal(v);
        }
    }

    private static unsafe int SendPluginCommandAction(IntPtr pluginNameAnsi, IntPtr actionAnsi, IntPtr valueAnsi)
    {
        try
        {
            string? name = pluginNameAnsi != IntPtr.Zero ? Marshal.PtrToStringAnsi(pluginNameAnsi) : null;
            if (string.IsNullOrEmpty(name))
                return 0;

            // The flight recorder answers to a reserved name (no plugin export, no API bump):
            // an engine without it finds no such plugin and returns 0, so a plugin can call it
            // on any engine.
            if (name == Net.FlightRecorder.PluginCommandTarget)
            {
                string? action = actionAnsi != IntPtr.Zero ? Marshal.PtrToStringAnsi(actionAnsi) : null;
                string? value = valueAnsi != IntPtr.Zero ? Marshal.PtrToStringAnsi(valueAnsi) : null;
                return Net.FlightRecorder.HandlePluginCommand(action, value) ? 1 : 0;
            }

            IntPtr export = ResolvePluginExport(name, "RynthPluginApplyRemoteCommand");
            if (export == IntPtr.Zero)
                return 0;

            // Forward the raw ANSI arg pointers; the receiving plugin copies them
            // and enqueues for its own pump thread (never applies on this thread).
            ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)export)(actionAnsi, valueAnsi);
            return 1;
        }
        catch
        {
            return 0;
        }
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

    private static int GetCastBusyStateAction() => CastGate.GetCastBusyState();

    private static int GetUseDoneSeqAction() => SmartBoxHooks.GetUseDoneSeq();

    // ── Vendor trading (v67) — see Compatibility/VendorTrade.cs ─────────────
    // Reads are served from the main-thread snapshot; buy/sell only queue here
    // (checked against the snapshot) and go out on AC's main thread.
    private static unsafe int GetVendorInfoAction(VendorInfoNative* info)
    {
        try { return VendorTrade.FillInfo(info); }
        catch { return 0; }
    }

    private static unsafe int GetVendorItemsAction(VendorItemNative* items, int maxCount)
    {
        try { return VendorTrade.FillItems(items, maxCount); }
        catch { return -1; }
    }

    private static unsafe uint VendorBuyAction(uint vendorId, VendorTradeEntryNative* entries, int count)
    {
        try
        {
            if (entries == null || count <= 0 || count > 1000)
                return VendorTrade.RefuseRequest(true, vendorId, count, entries == null ? "null entry list" : $"bad line count {count}");
            var ids = new uint[count];
            var amounts = new int[count];
            for (int i = 0; i < count; i++)
            {
                ids[i] = entries[i].ObjectId;
                amounts[i] = entries[i].Amount;
            }
            return VendorTrade.RequestBuy(vendorId, ids, amounts);
        }
        catch { return 0; }
    }

    private static unsafe uint VendorSellAction(uint vendorId, uint* itemIds, int count)
    {
        try
        {
            if (itemIds == null || count <= 0 || count > 1000)
                return VendorTrade.RefuseRequest(false, vendorId, count, itemIds == null ? "null item list" : $"bad line count {count}");
            return VendorTrade.RequestSell(vendorId, new ReadOnlySpan<uint>(itemIds, count));
        }
        catch { return 0; }
    }

    private static unsafe int GetVendorTradeStatusAction(VendorTradeStatusNative* status)
    {
        try { return VendorTrade.FillStatus(status); }
        catch { return 0; }
    }

    private static void ForceResetBusyCountAction()
    {
        Net.FlightRecorder.Mark(Net.FlightMark.BusyForceClearAsk, text: "a plugin (host ForceResetBusyCount)");
        BusyCountHooks.ForceResetBusyCount();
    }

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
