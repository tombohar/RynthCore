using System;
using System.Runtime.InteropServices;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.HelloBox;

public sealed unsafe class HelloBoxPlugin : RynthPluginBase
{
    private const int UiSnapshotIntervalMs = 250;
    private const int ImGuiCondFirstUseEver = 1 << 2;
    private const int ImGuiWindowFlagsNone = 0;
    private const int ChatTypeWorldBroadcast = 1;
    private const uint HookCombatInitialized = 1u << 0;
    private const uint HookMovementInitialized = 1u << 1;
    private const uint HookMeleeAttack = 1u << 2;
    private const uint HookMissileAttack = 1u << 3;
    private const uint HookChangeCombatMode = 1u << 4;
    private const uint HookCancelAttack = 1u << 5;
    private const uint HookQueryHealth = 1u << 6;
    private const uint HookDoMovement = 1u << 7;
    private const uint HookStopMovement = 1u << 8;
    private const uint HookJumpNonAutonomous = 1u << 9;
    private const uint HookSetAutonomyLevel = 1u << 10;
    private const uint HookSetAutoRun = 1u << 11;
    private const uint HookTapJump = 1u << 12;

    internal static readonly IntPtr NamePointer = Marshal.StringToHGlobalAnsi("RynthCore Hello Box");
    internal static readonly IntPtr VersionPointer = Marshal.StringToHGlobalAnsi("0.8.32");

    private static readonly IntPtr WindowTitlePtr = Marshal.StringToHGlobalAnsi("RynthCore Hello Box##Plugin");
    private static readonly IntPtr GreetingPtr = Marshal.StringToHGlobalAnsi("Hello from the RynthCore test plugin.");
    private static readonly IntPtr StatusPtr = Marshal.StringToHGlobalAnsi("PluginCore is active. Hello Box now runs through RynthPluginRuntime while keeping the current compatibility and helper validation surface.");
    private static readonly IntPtr ProbeHooksButtonPtr = Marshal.StringToHGlobalAnsi("Probe Hooks");
    private static readonly IntPtr JumpButtonPtr = Marshal.StringToHGlobalAnsi("Jump");
    private static readonly IntPtr StartForwardButtonPtr = Marshal.StringToHGlobalAnsi("Start Forward");
    private static readonly IntPtr StopForwardButtonPtr = Marshal.StringToHGlobalAnsi("Stop Forward");
    private static readonly IntPtr ToggleIncomingEatButtonPtr = Marshal.StringToHGlobalAnsi("Toggle Eat Incoming");
    private static readonly IntPtr ToggleOutgoingEatButtonPtr = Marshal.StringToHGlobalAnsi("Toggle Eat Outgoing");
    private static readonly IntPtr GetPlayerIdButtonPtr = Marshal.StringToHGlobalAnsi("Get Player ID");
    private static readonly IntPtr GetSelectedIdButtonPtr = Marshal.StringToHGlobalAnsi("Get Selected ID");
    private static readonly IntPtr GetPreviousSelectedIdButtonPtr = Marshal.StringToHGlobalAnsi("Get Previous Selected");
    private static readonly IntPtr GetCoordsButtonPtr = Marshal.StringToHGlobalAnsi("Get Coords");
    private static readonly IntPtr GetGroundContainerButtonPtr = Marshal.StringToHGlobalAnsi("Get Ground Container");
    private static readonly IntPtr SelectSelfButtonPtr = Marshal.StringToHGlobalAnsi("Select Self");
    private static readonly IntPtr UseSelectedButtonPtr = Marshal.StringToHGlobalAnsi("Use Selected");
    private static readonly IntPtr WriteChatPingButtonPtr = Marshal.StringToHGlobalAnsi("Write Chat Ping");

    private static RynthCoreApiNative _api;
    private static RynthCoreHost _host;
    private static bool _initialized;
    private static bool _uiInitialized;
    private static bool _loginComplete;
    private static bool _windowVisible;
    private static bool _imguiBound;
    private static bool _eatIncomingChat;
    private static bool _eatOutgoingChat;
    private static int _busyCountIncrementedCount;
    private static int _busyCountDecrementedCount;
    private static int _incomingChatCount;
    private static int _outgoingChatCount;
    private static int _targetChangeCount;
    private static int _combatModeChangeCount;
    private static int _smartBoxEventCount;
    private static int _createObjectCount;
    private static int _deleteObjectCount;
    private static int _updateObjectCount;
    private static int _inventoryUpdateCount;
    private static int _viewContentsCount;
    private static int _stopViewContentsCount;
    private static string _lastAction = "Waiting for OnLoginComplete.";
    private static string _lastIncomingChat = "(none)";
    private static string _lastOutgoingChat = "(none)";
    private static string _lastBusyCountEvent = "(none)";
    private static string _lastTargetChange = "(none)";
    private static string _lastCombatModeChange = "(none)";
    private static bool _hasSmartBoxEvent;
    private static uint _lastSmartBoxOpcode;
    private static uint _lastSmartBoxBlobSize;
    private static uint _lastSmartBoxStatus;
    private static string _lastCreatedObject = "(none)";
    private static string _lastDeletedObject = "(none)";
    private static bool _hasUpdatedObject;
    private static uint _lastUpdatedObjectId;
    private static string _lastInventoryUpdate = "(none)";
    private static string _lastViewedContents = "(none)";
    private static string _lastClosedContents = "(none)";
    private static uint _currentTargetId;
    private static IntPtr[] _uiSnapshotBuffers = Array.Empty<IntPtr>();
    private static long _nextUiSnapshotTick;

    private static delegate* unmanaged[Cdecl]<IntPtr, void> _igSetCurrentContext;
    private static delegate* unmanaged[Cdecl]<ImVec2, int, ImVec2, void> _igSetNextWindowPos;
    private static delegate* unmanaged[Cdecl]<ImVec2, int, void> _igSetNextWindowSize;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, byte> _igBegin;
    private static delegate* unmanaged[Cdecl]<IntPtr, ImVec2, byte> _igButton;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> _igTextUnformatted;
    private static delegate* unmanaged[Cdecl]<void> _igSeparator;
    private static delegate* unmanaged[Cdecl]<void> _igEnd;

    public override int Initialize()
    {
        _api = Api;
        _host = Host;

        if (_api.ImGuiContext == IntPtr.Zero)
            return 11;

        if (!TryBindImGui(out string error))
        {
            Log($"Hello Box: failed to bind cimgui exports ({error}).");
            return 12;
        }

        _initialized = true;
        _uiInitialized = false;
        _loginComplete = false;
        _windowVisible = false;
        _eatIncomingChat = false;
        _eatOutgoingChat = false;
        _host.SetIncomingChatSuppression(false);
        _busyCountIncrementedCount = 0;
        _busyCountDecrementedCount = 0;
        _incomingChatCount = 0;
        _outgoingChatCount = 0;
        _targetChangeCount = 0;
        _combatModeChangeCount = 0;
        _smartBoxEventCount = 0;
        _createObjectCount = 0;
        _deleteObjectCount = 0;
        _updateObjectCount = 0;
        _inventoryUpdateCount = 0;
        _viewContentsCount = 0;
        _stopViewContentsCount = 0;
        _lastIncomingChat = "(none)";
        _lastOutgoingChat = "(none)";
        _lastBusyCountEvent = "(none)";
        _lastTargetChange = "(none)";
        _lastCombatModeChange = "(none)";
        _hasSmartBoxEvent = false;
        _lastSmartBoxOpcode = 0;
        _lastSmartBoxBlobSize = 0;
        _lastSmartBoxStatus = 0;
        _lastCreatedObject = "(none)";
        _lastDeletedObject = "(none)";
        _hasUpdatedObject = false;
        _lastUpdatedObjectId = 0;
        _lastInventoryUpdate = "(none)";
        _lastViewedContents = "(none)";
        _lastClosedContents = "(none)";
        ClearUiSnapshotBuffers();
        _nextUiSnapshotTick = 0;
        _lastAction = "Waiting for OnLoginComplete.";
        Log($"Hello Box: initialized via PluginCore. hookFlags=0x{_host.GetClientHookFlags():X8}");
        return 0;
    }

    public override void Shutdown()
    {
        Log("Hello Box: shutting down.");
        _initialized = false;
        _uiInitialized = false;
        _loginComplete = false;
        _windowVisible = false;
        ClearUiSnapshotBuffers();
        _host.SetIncomingChatSuppression(false);
    }

    public override void OnUIInitialized()
    {
        if (!_initialized)
            return;

        _uiInitialized = true;
        SetLastAction("OnUIInitialized received.");
    }

    public override void OnBusyCountIncremented()
    {
        if (!_initialized)
            return;

        _busyCountIncrementedCount++;
        _lastBusyCountEvent = "incremented";
    }

    public override void OnBusyCountDecremented()
    {
        if (!_initialized)
            return;

        _busyCountDecrementedCount++;
        _lastBusyCountEvent = "decremented";
    }

    public override void OnLoginComplete()
    {
        if (!_initialized)
            return;

        _loginComplete = true;
        SetLastAction("OnLoginComplete received. Open from the bar when needed.");
    }

    public override void OnBarAction()
    {
        if (!_initialized || !_loginComplete)
            return;

        _windowVisible = !_windowVisible;
        SetLastAction(_windowVisible ? "Window opened from bar." : "Window hidden from bar.");
    }

    public override void OnChatWindowText(string? text, int chatType, ref int eat)
    {
        if (!_initialized)
            return;

        _incomingChatCount++;
        _lastIncomingChat = text is not null
            ? $"[{chatType}] {TrimForUi(text)}"
            : $"[{chatType}] (queued stability path)";
    }

    public override void OnChatBarEnter(string? text, ref int eat)
    {
        if (!_initialized)
            return;

        _outgoingChatCount++;
        _lastOutgoingChat = TrimForUi(text ?? string.Empty);
        if (_eatOutgoingChat)
            eat = 1;
    }

    public override void OnSelectedTargetChange(uint currentTargetId, uint previousTargetId)
    {
        if (!_initialized)
            return;

        _targetChangeCount++;
        _currentTargetId = currentTargetId;
        _lastTargetChange = $"0x{previousTargetId:X8} -> 0x{currentTargetId:X8}";
    }

    public override void OnCombatModeChange(int currentCombatMode, int previousCombatMode)
    {
        if (!_initialized)
            return;

        _combatModeChangeCount++;
        _lastCombatModeChange = $"{FormatCombatMode(previousCombatMode)} -> {FormatCombatMode(currentCombatMode)}";
    }

    public override void OnSmartBoxEvent(uint opcode, uint blobSize, uint status)
    {
        if (!_initialized)
            return;

        _smartBoxEventCount++;
        _hasSmartBoxEvent = true;
        _lastSmartBoxOpcode = opcode;
        _lastSmartBoxBlobSize = blobSize;
        _lastSmartBoxStatus = status;
    }

    public override void OnDeleteObject(uint objectId)
    {
        if (!_initialized)
            return;

        _deleteObjectCount++;
        _lastDeletedObject = $"0x{objectId:X8}";
    }

    public override void OnCreateObject(uint objectId)
    {
        if (!_initialized)
            return;

        _createObjectCount++;
        _lastCreatedObject = $"0x{objectId:X8}";
    }

    public override void OnUpdateObject(uint objectId)
    {
        if (!_initialized)
            return;

        _updateObjectCount++;
        _hasUpdatedObject = true;
        _lastUpdatedObjectId = objectId;
    }

    public override void OnUpdateObjectInventory(uint objectId)
    {
        if (!_initialized)
            return;

        _inventoryUpdateCount++;
        _lastInventoryUpdate = $"0x{objectId:X8}";
    }

    public override void OnViewObjectContents(uint objectId)
    {
        if (!_initialized)
            return;

        _viewContentsCount++;
        _lastViewedContents = $"0x{objectId:X8}";
    }

    public override void OnStopViewingObjectContents(uint objectId)
    {
        if (!_initialized)
            return;

        _stopViewContentsCount++;
        _lastClosedContents = $"0x{objectId:X8}";
    }

    public override void OnRender()
    {
        if (!_initialized || !_loginComplete || !_windowVisible || !_imguiBound || _api.ImGuiContext == IntPtr.Zero)
            return;

        uint hookFlags = _host.GetClientHookFlags();
        RefreshUiSnapshot(hookFlags);

        _igSetCurrentContext(_api.ImGuiContext);
        _igSetNextWindowPos(new ImVec2(48f, 140f), ImGuiCondFirstUseEver, default);
        _igSetNextWindowSize(new ImVec2(448f, 360f), ImGuiCondFirstUseEver);

        bool open = _igBegin(WindowTitlePtr, IntPtr.Zero, ImGuiWindowFlagsNone) != 0;
        if (open)
        {
            _igTextUnformatted(GreetingPtr, IntPtr.Zero);
            _igSeparator();
            _igTextUnformatted(StatusPtr, IntPtr.Zero);
            foreach (IntPtr line in _uiSnapshotBuffers)
                _igTextUnformatted(line, IntPtr.Zero);

            if (_igButton(ToggleIncomingEatButtonPtr, default) != 0)
            {
                _eatIncomingChat = !_eatIncomingChat;
                _host.SetIncomingChatSuppression(_eatIncomingChat);
                SetLastAction(_eatIncomingChat
                    ? "Incoming chat suppression enabled."
                    : "Incoming chat suppression disabled.");
            }

            if (_igButton(ToggleOutgoingEatButtonPtr, default) != 0)
            {
                _eatOutgoingChat = !_eatOutgoingChat;
                SetLastAction(_eatOutgoingChat ? "Outgoing chat suppression enabled." : "Outgoing chat suppression disabled.");
            }

            _igSeparator();

            if (_host.HasProbeClientHooks && _igButton(ProbeHooksButtonPtr, default) != 0)
            {
                _host.ProbeClientHooks();
                SetLastAction($"Probe requested. hookFlags=0x{_host.GetClientHookFlags():X8}");
            }

            if (_host.HasTapJump && _igButton(JumpButtonPtr, default) != 0)
            {
                bool ok = _host.TapJump();
                SetLastAction(ok ? "Jump requested." : "Jump request failed.");
            }

            if (_host.HasSetAutoRun && _igButton(StartForwardButtonPtr, default) != 0)
            {
                bool ok = _host.SetAutoRun(true);
                SetLastAction(ok ? "Forward movement started." : "Forward start failed.");
            }

            if (_host.HasSetAutoRun && _igButton(StopForwardButtonPtr, default) != 0)
            {
                bool ok = _host.SetAutoRun(false);
                SetLastAction(ok ? "Forward movement stop requested." : "Forward stop failed.");
            }

            if (_host.HasGetPlayerId && _igButton(GetPlayerIdButtonPtr, default) != 0)
            {
                uint playerId = _host.GetPlayerId();
                SetLastAction(playerId != 0
                    ? $"GetPlayerId -> 0x{playerId:X8}"
                    : "GetPlayerId returned 0.");
            }

            if (_host.HasGetSelectedItemId && _igButton(GetSelectedIdButtonPtr, default) != 0)
            {
                uint selectedId = _host.GetSelectedItemId();
                SetLastAction(selectedId != 0
                    ? $"GetSelectedItemId -> 0x{selectedId:X8}"
                    : "GetSelectedItemId returned 0.");
            }

            if (_host.HasGetPreviousSelectedItemId && _igButton(GetPreviousSelectedIdButtonPtr, default) != 0)
            {
                uint previousSelectedId = _host.GetPreviousSelectedItemId();
                SetLastAction(previousSelectedId != 0
                    ? $"GetPreviousSelectedItemId -> 0x{previousSelectedId:X8}"
                    : "GetPreviousSelectedItemId returned 0.");
            }

            if (_host.HasGetCurCoords && _igButton(GetCoordsButtonPtr, default) != 0)
            {
                bool ok = _host.TryGetCurCoords(out double northSouth, out double eastWest);
                SetLastAction(ok
                    ? $"GetCurCoords -> NS={northSouth:F3} EW={eastWest:F3}"
                    : "GetCurCoords failed.");
            }

            if (_host.HasGetGroundContainerId && _igButton(GetGroundContainerButtonPtr, default) != 0)
            {
                uint groundContainerId = _host.GetGroundContainerId();
                SetLastAction(groundContainerId != 0
                    ? $"GetGroundContainerId -> 0x{groundContainerId:X8}"
                    : "GetGroundContainerId returned 0.");
            }

            if (_host.HasSelectItem && _host.HasGetPlayerId && _igButton(SelectSelfButtonPtr, default) != 0)
            {
                uint playerId = _host.GetPlayerId();
                if (playerId == 0)
                {
                    SetLastAction("SelectSelf aborted: GetPlayerId returned 0.");
                }
                else
                {
                    bool ok = _host.SelectItem(playerId);
                    SetLastAction(ok
                        ? $"SelectItem(self) -> 0x{playerId:X8}"
                        : $"SelectItem(self) failed for 0x{playerId:X8}.");
                }
            }

            if (_host.HasUseObject && _host.HasGetSelectedItemId && _igButton(UseSelectedButtonPtr, default) != 0)
            {
                uint selectedId = _host.GetSelectedItemId();
                if (selectedId == 0)
                {
                    SetLastAction("UseSelected aborted: no selected object.");
                }
                else
                {
                    bool ok = _host.UseObject(selectedId);
                    SetLastAction(ok
                        ? $"UseObject(selected) -> 0x{selectedId:X8}"
                        : $"UseObject(selected) failed for 0x{selectedId:X8}.");
                }
            }

            if (_host.HasWriteToChat && _igButton(WriteChatPingButtonPtr, default) != 0)
            {
                bool ok = _host.WriteToChat("RynthCore helper ping", ChatTypeWorldBroadcast);
                SetLastAction(ok
                    ? "WriteToChat sent helper ping."
                    : "WriteToChat failed.");
            }

            _igSeparator();
            TextOneOff($"Last action: {_lastAction}");
        }

        _igEnd();
    }

    // ─── Avalonia status text export ──────────────────────────────────────
    private static IntPtr _statusTextPtr = IntPtr.Zero;

    /// <summary>
    /// Called by the engine's HelloBoxPanel every 250 ms to get a snapshot of
    /// current plugin state as a newline-delimited ANSI string. The returned
    /// pointer is valid until the next call to this method.
    /// </summary>
    internal static IntPtr GetStatusTextPtr()
    {
        if (_statusTextPtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_statusTextPtr);
            _statusTextPtr = IntPtr.Zero;
        }

        if (!_initialized)
        {
            _statusTextPtr = Marshal.StringToHGlobalAnsi("Hello Box: not initialized.\nWaiting for OnLoginComplete...");
            return _statusTextPtr;
        }

        uint hookFlags = _host.GetClientHookFlags();

        string text = string.Join("\n", new[]
        {
            $"Hello Box v{Marshal.PtrToStringAnsi(VersionPointer)}  — RynthPluginRuntime",
            $"Lifecycle:  UI={OnOff(_uiInitialized)}  Login={OnOff(_loginComplete)}",
            $"Hook flags: 0x{hookFlags:X8}",
            $"  combat {OnOff(hookFlags, HookCombatInitialized)}  movement {OnOff(hookFlags, HookMovementInitialized)}",
            $"  melee {OnOff(hookFlags, HookMeleeAttack)}  missile {OnOff(hookFlags, HookMissileAttack)}  mode {OnOff(hookFlags, HookChangeCombatMode)}  cancel {OnOff(hookFlags, HookCancelAttack)}",
            $"  move {OnOff(hookFlags, HookDoMovement)}  stop {OnOff(hookFlags, HookStopMovement)}  jump {OnOff(hookFlags, HookTapJump)}  autorun {OnOff(hookFlags, HookSetAutoRun)}",
            string.Empty,
            $"Chat in:  {_incomingChatCount:D6}  eat={OnOff(_eatIncomingChat)}",
            $"Chat out: {_outgoingChatCount:D6}  eat={OnOff(_eatOutgoingChat)}",
            $"Busy:     +{_busyCountIncrementedCount:D6} / -{_busyCountDecrementedCount:D6}",
            $"Target changes:  {_targetChangeCount:D6}",
            $"Combat changes:  {_combatModeChangeCount:D6}",
            $"SmartBox events: {_smartBoxEventCount:D6}",
            $"Create/Delete/Update: {_createObjectCount}/{_deleteObjectCount}/{_updateObjectCount}",
            $"Inventory updates: {_inventoryUpdateCount:D6}",
            $"View/Close contents: {_viewContentsCount}/{_stopViewContentsCount}",
            string.Empty,
            $"Last incoming:   {_lastIncomingChat}",
            $"Last outgoing:   {_lastOutgoingChat}",
            $"Last busy event: {_lastBusyCountEvent}",
            $"Last target:     {_lastTargetChange}",
            $"Last combat:     {_lastCombatModeChange}",
            _hasSmartBoxEvent
                ? $"Last SmartBox: op=0x{_lastSmartBoxOpcode:X8} size={_lastSmartBoxBlobSize} status={_lastSmartBoxStatus}"
                : "Last SmartBox: (none)",
            $"Last created: {_lastCreatedObject}",
            $"Last deleted: {_lastDeletedObject}",
            _hasUpdatedObject ? $"Last updated: 0x{_lastUpdatedObjectId:X8}" : "Last updated: (none)",
            $"Last inventory: {_lastInventoryUpdate}",
            $"Last viewed:    {_lastViewedContents}",
            $"Last closed:    {_lastClosedContents}",
            string.Empty,
            $"Status: {_lastAction}"
        });

        _statusTextPtr = Marshal.StringToHGlobalAnsi(text);
        return _statusTextPtr;
    }

    private static bool TryBindImGui(out string error)
    {
        if (_imguiBound)
        {
            error = string.Empty;
            return true;
        }

        IntPtr module = GetModuleHandleA("RynthCore.cimgui.dll");
        if (module == IntPtr.Zero)
            module = GetModuleHandleA("cimgui.dll");

        if (module == IntPtr.Zero)
        {
            error = "no loaded cimgui module";
            return false;
        }

        if (!TryGetProc(module, "igSetCurrentContext", out IntPtr setCurrentContext))
        {
            error = "missing igSetCurrentContext";
            return false;
        }

        if (!TryGetProc(module, "igSetNextWindowPos", out IntPtr setNextWindowPos))
        {
            error = "missing igSetNextWindowPos";
            return false;
        }

        if (!TryGetProc(module, "igSetNextWindowSize", out IntPtr setNextWindowSize))
        {
            error = "missing igSetNextWindowSize";
            return false;
        }

        if (!TryGetProc(module, "igBegin", out IntPtr begin))
        {
            error = "missing igBegin";
            return false;
        }

        if (!TryGetProc(module, "igButton", out IntPtr button))
        {
            error = "missing igButton";
            return false;
        }

        if (!TryGetProc(module, "igTextUnformatted", out IntPtr textUnformatted))
        {
            error = "missing igTextUnformatted";
            return false;
        }

        if (!TryGetProc(module, "igSeparator", out IntPtr separator))
        {
            error = "missing igSeparator";
            return false;
        }

        if (!TryGetProc(module, "igEnd", out IntPtr end))
        {
            error = "missing igEnd";
            return false;
        }

        _igSetCurrentContext = (delegate* unmanaged[Cdecl]<IntPtr, void>)setCurrentContext;
        _igSetNextWindowPos = (delegate* unmanaged[Cdecl]<ImVec2, int, ImVec2, void>)setNextWindowPos;
        _igSetNextWindowSize = (delegate* unmanaged[Cdecl]<ImVec2, int, void>)setNextWindowSize;
        _igBegin = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, byte>)begin;
        _igButton = (delegate* unmanaged[Cdecl]<IntPtr, ImVec2, byte>)button;
        _igTextUnformatted = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)textUnformatted;
        _igSeparator = (delegate* unmanaged[Cdecl]<void>)separator;
        _igEnd = (delegate* unmanaged[Cdecl]<void>)end;

        _imguiBound = true;
        error = string.Empty;
        return true;
    }

    private static bool TryGetProc(IntPtr module, string exportName, out IntPtr address)
    {
        address = GetProcAddress(module, exportName);
        return address != IntPtr.Zero;
    }

    private static string OnOff(bool value) => value ? "ON" : "OFF";

    private static string OnOff(uint flags, uint flag) => (flags & flag) != 0 ? "ON" : "OFF";

    private static string TrimForUi(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "(empty)";

        string singleLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= 96 ? singleLine : singleLine[..96] + "...";
    }

    private static string FormatCombatMode(int combatMode)
    {
        return combatMode switch
        {
            1 => "noncombat(1)",
            2 => "melee(2)",
            4 => "missile(4)",
            8 => "magic(8)",
            _ => combatMode.ToString()
        };
    }

    private static void RefreshUiSnapshot(uint hookFlags)
    {
        long now = Environment.TickCount64;
        if (_uiSnapshotBuffers.Length != 0 && now < _nextUiSnapshotTick)
            return;

        string[] lines =
        [
            $"Host API version: {_api.Version}",
            $"Lifecycle: UI {OnOff(_uiInitialized)}  Login {OnOff(_loginComplete)}",
            $"Hook flags: 0x{hookFlags:X8}",
            $"Surfaces: combat {OnOff(hookFlags, HookCombatInitialized)}, movement {OnOff(hookFlags, HookMovementInitialized)}",
            $"Combat actions: melee {OnOff(hookFlags, HookMeleeAttack)}, missile {OnOff(hookFlags, HookMissileAttack)}, mode {OnOff(hookFlags, HookChangeCombatMode)}, cancel {OnOff(hookFlags, HookCancelAttack)}, health {OnOff(hookFlags, HookQueryHealth)}",
            $"Movement actions: move {OnOff(hookFlags, HookDoMovement)}, stop {OnOff(hookFlags, HookStopMovement)}, jump {OnOff(hookFlags, HookJumpNonAutonomous)}, autonomy {OnOff(hookFlags, HookSetAutonomyLevel)}",
            $"Local actions: autorun {OnOff(hookFlags, HookSetAutoRun)}, tap-jump {OnOff(hookFlags, HookTapJump)}",
            $"Helpers: player {OnOff(_host.HasGetPlayerId)}, selection {OnOff(_host.HasGetSelectedItemId && _host.HasSetSelectedObjectId)}, coords {OnOff(_host.HasGetCurCoords)}, chat {OnOff(_host.HasWriteToChat)}",
            $"Helpers 2: ground {OnOff(_host.HasGetGroundContainerId)}, use-on {OnOff(_host.HasUseObjectOn)}, equipped {OnOff(_host.HasUseEquippedItem)}, move ext/int {OnOff(_host.HasMoveItemExternal && _host.HasMoveItemInternal)}",
            string.Empty,
            FormatPositionDebug(),
            string.Empty,
            $"Incoming chat callbacks: {_incomingChatCount:D6}  Suppress {OnOff(_eatIncomingChat)} (host-side stable suppression)",
            $"Outgoing chat callbacks: {_outgoingChatCount:D6}  Eat {OnOff(_eatOutgoingChat)}",
            $"Busy callbacks: +{_busyCountIncrementedCount:D6}  -{_busyCountDecrementedCount:D6}",
            $"Target change callbacks: {_targetChangeCount:D6}",
            $"Combat mode callbacks: {_combatModeChangeCount:D6}",
            $"SmartBox callbacks: {_smartBoxEventCount:D6}",
            $"Create object callbacks: {_createObjectCount:D6}",
            $"Delete object callbacks: {_deleteObjectCount:D6}",
            $"Update object callbacks: {_updateObjectCount:D6}",
            $"Inventory update callbacks: {_inventoryUpdateCount:D6}",
            $"View contents callbacks: {_viewContentsCount:D6}",
            $"Close contents callbacks: {_stopViewContentsCount:D6}",
            $"Last incoming: {_lastIncomingChat}",
            $"Last outgoing: {_lastOutgoingChat}",
            $"Last busy event: {_lastBusyCountEvent}",
            $"Last target: {_lastTargetChange}",
            $"Last combat mode: {_lastCombatModeChange}",
            _hasSmartBoxEvent
                ? $"Last SmartBox: op=0x{_lastSmartBoxOpcode:X8} size={_lastSmartBoxBlobSize} status={_lastSmartBoxStatus}"
                : "Last SmartBox: (none)",
            $"Last created: {_lastCreatedObject}",
            $"Last deleted: {_lastDeletedObject}",
            _hasUpdatedObject ? $"Last updated: 0x{_lastUpdatedObjectId:X8}" : "Last updated: (none)",
            $"Last inventory update: {_lastInventoryUpdate}",
            $"Last viewed contents: {_lastViewedContents}",
            $"Last closed contents: {_lastClosedContents}"
        ];

        ClearUiSnapshotBuffers();
        _uiSnapshotBuffers = new IntPtr[lines.Length];
        for (int i = 0; i < lines.Length; i++)
            _uiSnapshotBuffers[i] = Marshal.StringToHGlobalAnsi(lines[i]);

        _nextUiSnapshotTick = now + UiSnapshotIntervalMs;
    }

    private static void ClearUiSnapshotBuffers()
    {
        for (int i = 0; i < _uiSnapshotBuffers.Length; i++)
        {
            if (_uiSnapshotBuffers[i] != IntPtr.Zero)
                Marshal.FreeHGlobal(_uiSnapshotBuffers[i]);
        }

        _uiSnapshotBuffers = Array.Empty<IntPtr>();
    }

    private static void TextOneOff(string message)
    {
        IntPtr buffer = Marshal.StringToHGlobalAnsi(message);
        try
        {
            _igTextUnformatted(buffer, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string FormatPositionDebug()
    {
        string playerLine;
        float px = 0, py = 0, pz = 0;
        uint pCell = 0;
        bool hasPlayer = false;

        if (_host.HasGetPlayerPose &&
            _host.TryGetPlayerPose(out pCell, out px, out py, out pz, out _, out _, out _, out _))
        {
            hasPlayer = true;
            playerLine = $"Player pos: cell=0x{pCell:X8} x={px:F2} y={py:F2} z={pz:F2}";
        }
        else
        {
            playerLine = "Player pos: unavailable";
        }

        string targetLine;
        if (_currentTargetId != 0 && _host.HasGetObjectPosition &&
            _host.TryGetObjectPosition(_currentTargetId, out uint tCell, out float tx, out float ty, out float tz))
        {
            _host.TryGetObjectName(_currentTargetId, out string name);
            targetLine = $"Target pos: cell=0x{tCell:X8} x={tx:F2} y={ty:F2} z={tz:F2}  [{name}]";

            if (hasPlayer)
            {
                float dx = tx - px;
                float dy = ty - py;
                float dz = tz - pz;
                float dist = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                targetLine += $"  dist={dist:F1}m";
            }
        }
        else if (_currentTargetId != 0)
        {
            targetLine = $"Target pos: FAILED for 0x{_currentTargetId:X8} (HasAPI={_host.HasGetObjectPosition})";
        }
        else
        {
            targetLine = "Target pos: no target selected";
        }

        return $"{playerLine}\n{targetLine}";
    }

    private static void SetLastAction(string message)
    {
        _lastAction = message;
        _host.Log($"Hello Box: {message}");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr GetModuleHandleA(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ImVec2
    {
        public readonly float X;
        public readonly float Y;

        public ImVec2(float x, float y)
        {
            X = x;
            Y = y;
        }
    }
}
