using System;
using System.Runtime.InteropServices;

namespace NexCore.Plugin.HelloBox;

public static unsafe class PluginExports
{
    private const uint ExpectedApiVersion = 4;
    private const int ImGuiCondFirstUseEver = 1 << 2;
    private const int ImGuiWindowFlagsNone = 0;
    private const uint MotionWalkForward = 0x45000005;
    private const int HoldKeyNone = 0;
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
    private static readonly IntPtr NamePtr = Marshal.StringToHGlobalAnsi("NexCore Hello Box");
    private static readonly IntPtr VersionPtr = Marshal.StringToHGlobalAnsi("0.8.26");
    private static readonly IntPtr WindowTitlePtr = Marshal.StringToHGlobalAnsi("NexCore Hello Box##Plugin");
    private static readonly IntPtr GreetingPtr = Marshal.StringToHGlobalAnsi("Hello from the NexCore test plugin.");
    private static readonly IntPtr StatusPtr = Marshal.StringToHGlobalAnsi("Plugin ABI v4 is active. Incoming chat text is sourced from NexCore's current compatibility path, and incoming suppression is handled by a host-side flag for stability.");
    private static readonly IntPtr ProbeHooksButtonPtr = Marshal.StringToHGlobalAnsi("Probe Hooks");
    private static readonly IntPtr JumpButtonPtr = Marshal.StringToHGlobalAnsi("Jump");
    private static readonly IntPtr StartForwardButtonPtr = Marshal.StringToHGlobalAnsi("Start Forward");
    private static readonly IntPtr StopForwardButtonPtr = Marshal.StringToHGlobalAnsi("Stop Forward");
    private static readonly IntPtr ToggleIncomingEatButtonPtr = Marshal.StringToHGlobalAnsi("Toggle Eat Incoming");
    private static readonly IntPtr ToggleOutgoingEatButtonPtr = Marshal.StringToHGlobalAnsi("Toggle Eat Outgoing");

    private static NexCoreApi _api;
    private static IntPtr _logFn;
    private static bool _initialized;
    private static bool _uiInitialized;
    private static bool _loginComplete;
    private static bool _windowVisible;
    private static bool _imguiBound;
    private static bool _eatIncomingChat;
    private static bool _eatOutgoingChat;
    private static int _incomingChatCount;
    private static int _outgoingChatCount;
    private static int _targetChangeCount;
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
    private static string _lastTargetChange = "(none)";
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

    private static delegate* unmanaged[Cdecl]<IntPtr, void> _igSetCurrentContext;
    private static delegate* unmanaged[Cdecl]<ImVec2, int, ImVec2, void> _igSetNextWindowPos;
    private static delegate* unmanaged[Cdecl]<ImVec2, int, void> _igSetNextWindowSize;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, byte> _igBegin;
    private static delegate* unmanaged[Cdecl]<IntPtr, ImVec2, byte> _igButton;
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> _igTextUnformatted;
    private static delegate* unmanaged[Cdecl]<void> _igSeparator;
    private static delegate* unmanaged[Cdecl]<void> _igEnd;
    private static delegate* unmanaged[Cdecl]<void> _probeClientHooks;
    private static delegate* unmanaged[Cdecl]<uint> _getClientHookFlags;
    private static delegate* unmanaged[Cdecl]<int, int> _changeCombatMode;
    private static delegate* unmanaged[Cdecl]<int> _cancelAttack;
    private static delegate* unmanaged[Cdecl]<uint, int> _queryHealth;
    private static delegate* unmanaged[Cdecl]<uint, int, float, int> _meleeAttack;
    private static delegate* unmanaged[Cdecl]<uint, int, float, int> _missileAttack;
    private static delegate* unmanaged[Cdecl]<uint, float, int, int> _doMovement;
    private static delegate* unmanaged[Cdecl]<uint, int, int> _stopMovement;
    private static delegate* unmanaged[Cdecl]<float, int> _jumpNonAutonomous;
    private static delegate* unmanaged[Cdecl]<uint, int> _setAutonomyLevel;
    private static delegate* unmanaged[Cdecl]<int, int> _setAutoRun;
    private static delegate* unmanaged[Cdecl]<int> _tapJump;
    private static delegate* unmanaged[Cdecl]<int, void> _setIncomingChatSuppression;

    [UnmanagedCallersOnly(EntryPoint = "NexPluginInit")]
    private static int Init(NexCoreApi* api)
    {
        if (api == null)
            return 9;

        NexCoreApi hostApi = *api;

        if (hostApi.Version < ExpectedApiVersion)
            return 10;

        if (hostApi.ImGuiContext == IntPtr.Zero)
            return 11;

        _api = hostApi;
        _logFn = hostApi.LogFn;
        BindHostApi(hostApi);

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
        if (_setIncomingChatSuppression != null)
            _setIncomingChatSuppression(0);
        _incomingChatCount = 0;
        _outgoingChatCount = 0;
        _targetChangeCount = 0;
        _smartBoxEventCount = 0;
        _createObjectCount = 0;
        _deleteObjectCount = 0;
        _updateObjectCount = 0;
        _inventoryUpdateCount = 0;
        _viewContentsCount = 0;
        _stopViewContentsCount = 0;
        _lastIncomingChat = "(none)";
        _lastOutgoingChat = "(none)";
        _lastTargetChange = "(none)";
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
        _lastAction = "Waiting for OnLoginComplete.";
        Log($"Hello Box: initialized. hookFlags=0x{GetClientHookFlags():X8}");
        return 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "NexPluginShutdown")]
    public static void Shutdown()
    {
        Log("Hello Box: shutting down.");
        _initialized = false;
        _uiInitialized = false;
        _loginComplete = false;
        _windowVisible = false;
        if (_setIncomingChatSuppression != null)
            _setIncomingChatSuppression(0);
    }

    [UnmanagedCallersOnly(EntryPoint = "NexPluginOnUIInitialized")]
    public static void OnUIInitialized()
    {
        if (!_initialized)
            return;

        _uiInitialized = true;
        SetLastAction("OnUIInitialized received.");
    }

    [UnmanagedCallersOnly(EntryPoint = "NexPluginOnLoginComplete")]
    public static void OnLoginComplete()
    {
        if (!_initialized)
            return;

        _loginComplete = true;
        SetLastAction("OnLoginComplete received. Open from the bar when needed.");
    }

    [UnmanagedCallersOnly(EntryPoint = "NexPluginOnBarAction")]
    public static void OnBarAction()
    {
        if (!_initialized || !_loginComplete)
            return;

        _windowVisible = !_windowVisible;
        SetLastAction(_windowVisible ? "Window opened from bar." : "Window hidden from bar.");
    }

    [UnmanagedCallersOnly(EntryPoint = "NexPluginOnChatWindowText")]
    public static void OnChatWindowText(IntPtr textUtf16, int chatType, IntPtr eatFlag)
    {
        if (!_initialized)
            return;

        _incomingChatCount++;
        _lastIncomingChat = textUtf16 != IntPtr.Zero
            ? $"[{chatType}] {TrimForUi(ReadWideString(textUtf16))}"
            : $"[{chatType}] (queued stability path)";
    }

    [UnmanagedCallersOnly(EntryPoint = "NexPluginOnChatBarEnter")]
    public static void OnChatBarEnter(IntPtr textUtf16, IntPtr eatFlag)
    {
        if (!_initialized)
            return;

        _outgoingChatCount++;
        _lastOutgoingChat = TrimForUi(ReadWideString(textUtf16));
        if (_eatOutgoingChat && eatFlag != IntPtr.Zero)
            Marshal.WriteInt32(eatFlag, 1);
    }

    [UnmanagedCallersOnly(EntryPoint = "NexPluginOnSelectedTargetChange")]
    public static void OnSelectedTargetChange(uint currentTargetId, uint previousTargetId)
    {
        if (!_initialized)
            return;

        _targetChangeCount++;
        _lastTargetChange = $"0x{previousTargetId:X8} -> 0x{currentTargetId:X8}";
    }

    [UnmanagedCallersOnly(EntryPoint = "NexPluginOnSmartBoxEvent")]
    public static void OnSmartBoxEvent(uint opcode, uint blobSize, uint status)
    {
        if (!_initialized)
            return;

        _smartBoxEventCount++;
        _hasSmartBoxEvent = true;
        _lastSmartBoxOpcode = opcode;
        _lastSmartBoxBlobSize = blobSize;
        _lastSmartBoxStatus = status;
    }

    [UnmanagedCallersOnly(EntryPoint = "NexPluginOnDeleteObject")]
    public static void OnDeleteObject(uint objectId)
    {
        if (!_initialized)
            return;

        _deleteObjectCount++;
        _lastDeletedObject = $"0x{objectId:X8}";
    }

    [UnmanagedCallersOnly(EntryPoint = "NexPluginOnCreateObject")]
    public static void OnCreateObject(uint objectId)
    {
        if (!_initialized)
            return;

        _createObjectCount++;
        _lastCreatedObject = $"0x{objectId:X8}";
    }

    [UnmanagedCallersOnly(EntryPoint = "NexPluginOnUpdateObject")]
    public static void OnUpdateObject(uint objectId)
    {
        if (!_initialized)
            return;

        _updateObjectCount++;
        _hasUpdatedObject = true;
        _lastUpdatedObjectId = objectId;
    }

    [UnmanagedCallersOnly(EntryPoint = "NexPluginOnUpdateObjectInventory")]
    public static void OnUpdateObjectInventory(uint objectId)
    {
        if (!_initialized)
            return;

        _inventoryUpdateCount++;
        _lastInventoryUpdate = $"0x{objectId:X8}";
    }

    [UnmanagedCallersOnly(EntryPoint = "NexPluginOnViewObjectContents")]
    public static void OnViewObjectContents(uint objectId)
    {
        if (!_initialized)
            return;

        _viewContentsCount++;
        _lastViewedContents = $"0x{objectId:X8}";
    }

    [UnmanagedCallersOnly(EntryPoint = "NexPluginOnStopViewingObjectContents")]
    public static void OnStopViewingObjectContents(uint objectId)
    {
        if (!_initialized)
            return;

        _stopViewContentsCount++;
        _lastClosedContents = $"0x{objectId:X8}";
    }

    [UnmanagedCallersOnly(EntryPoint = "NexPluginName")]
    public static IntPtr GetName() => NamePtr;

    [UnmanagedCallersOnly(EntryPoint = "NexPluginVersion")]
    public static IntPtr GetVersion() => VersionPtr;

    [UnmanagedCallersOnly(EntryPoint = "NexPluginRender")]
    public static void Render()
    {
        if (!_initialized || !_loginComplete || !_windowVisible || !_imguiBound || _api.ImGuiContext == IntPtr.Zero)
            return;

        uint hookFlags = GetClientHookFlags();

        _igSetCurrentContext(_api.ImGuiContext);
        _igSetNextWindowPos(new ImVec2(48f, 140f), ImGuiCondFirstUseEver, default);
        _igSetNextWindowSize(new ImVec2(448f, 360f), ImGuiCondFirstUseEver);

        bool open = _igBegin(WindowTitlePtr, IntPtr.Zero, ImGuiWindowFlagsNone) != 0;
        if (open)
        {
            _igTextUnformatted(GreetingPtr, IntPtr.Zero);
            _igSeparator();
            _igTextUnformatted(StatusPtr, IntPtr.Zero);
            Text($"Host API version: {_api.Version}");
            Text($"Lifecycle: UI {OnOff(_uiInitialized)}  Login {OnOff(_loginComplete)}");
            Text($"Hook flags: 0x{hookFlags:X8}");
            Text($"Surfaces: combat {OnOff(hookFlags, HookCombatInitialized)}, movement {OnOff(hookFlags, HookMovementInitialized)}");
            Text($"Combat actions: melee {OnOff(hookFlags, HookMeleeAttack)}, missile {OnOff(hookFlags, HookMissileAttack)}, mode {OnOff(hookFlags, HookChangeCombatMode)}, cancel {OnOff(hookFlags, HookCancelAttack)}, health {OnOff(hookFlags, HookQueryHealth)}");
            Text($"Movement actions: move {OnOff(hookFlags, HookDoMovement)}, stop {OnOff(hookFlags, HookStopMovement)}, jump {OnOff(hookFlags, HookJumpNonAutonomous)}, autonomy {OnOff(hookFlags, HookSetAutonomyLevel)}");
            Text($"Local actions: autorun {OnOff(hookFlags, HookSetAutoRun)}, tap-jump {OnOff(hookFlags, HookTapJump)}");
            _igSeparator();

            Text($"Incoming chat callbacks: {_incomingChatCount}  Suppress {OnOff(_eatIncomingChat)} (host-side stable suppression)");
            Text($"Outgoing chat callbacks: {_outgoingChatCount}  Eat {OnOff(_eatOutgoingChat)}");
            Text($"Target change callbacks: {_targetChangeCount}");
            Text($"SmartBox callbacks: {_smartBoxEventCount}");
            Text($"Create object callbacks: {_createObjectCount}");
            Text($"Delete object callbacks: {_deleteObjectCount}");
            Text($"Update object callbacks: {_updateObjectCount}");
            Text($"Inventory update callbacks: {_inventoryUpdateCount}");
            Text($"View contents callbacks: {_viewContentsCount}");
            Text($"Close contents callbacks: {_stopViewContentsCount}");
            Text($"Last incoming: {_lastIncomingChat}");
            Text($"Last outgoing: {_lastOutgoingChat}");
            Text($"Last target: {_lastTargetChange}");
            Text(_hasSmartBoxEvent
                ? $"Last SmartBox: op=0x{_lastSmartBoxOpcode:X8} size={_lastSmartBoxBlobSize} status={_lastSmartBoxStatus}"
                : "Last SmartBox: (none)");
            Text($"Last created: {_lastCreatedObject}");
            Text($"Last deleted: {_lastDeletedObject}");
            Text(_hasUpdatedObject ? $"Last updated: 0x{_lastUpdatedObjectId:X8}" : "Last updated: (none)");
            Text($"Last inventory update: {_lastInventoryUpdate}");
            Text($"Last viewed contents: {_lastViewedContents}");
            Text($"Last closed contents: {_lastClosedContents}");

            if (_igButton(ToggleIncomingEatButtonPtr, default) != 0)
            {
                _eatIncomingChat = !_eatIncomingChat;
                if (_setIncomingChatSuppression != null)
                    _setIncomingChatSuppression(_eatIncomingChat ? 1 : 0);
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

            if (_probeClientHooks != null && _igButton(ProbeHooksButtonPtr, default) != 0)
            {
                _probeClientHooks();
                SetLastAction($"Probe requested. hookFlags=0x{GetClientHookFlags():X8}");
            }

            if (_tapJump != null && _igButton(JumpButtonPtr, default) != 0)
            {
                bool ok = _tapJump() != 0;
                SetLastAction(ok ? "Jump requested." : "Jump request failed.");
            }

            if (_setAutoRun != null && _igButton(StartForwardButtonPtr, default) != 0)
            {
                bool ok = _setAutoRun(1) != 0;
                SetLastAction(ok ? "Forward movement started." : "Forward start failed.");
            }

            if (_setAutoRun != null && _igButton(StopForwardButtonPtr, default) != 0)
            {
                bool ok = _setAutoRun(0) != 0;
                SetLastAction(ok ? "Forward movement stop requested." : "Forward stop failed.");
            }

            _igSeparator();
            Text($"Last action: {_lastAction}");
        }

        _igEnd();
    }

    private static bool TryBindImGui(out string error)
    {
        if (_imguiBound)
        {
            error = string.Empty;
            return true;
        }

        IntPtr module = GetModuleHandleA("NexCore.cimgui.dll");
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

    private static void BindHostApi(NexCoreApi hostApi)
    {
        _probeClientHooks = hostApi.ProbeClientHooksFn != IntPtr.Zero
            ? (delegate* unmanaged[Cdecl]<void>)hostApi.ProbeClientHooksFn
            : null;
        _getClientHookFlags = hostApi.GetClientHookFlagsFn != IntPtr.Zero
            ? (delegate* unmanaged[Cdecl]<uint>)hostApi.GetClientHookFlagsFn
            : null;
        _changeCombatMode = hostApi.ChangeCombatModeFn != IntPtr.Zero
            ? (delegate* unmanaged[Cdecl]<int, int>)hostApi.ChangeCombatModeFn
            : null;
        _cancelAttack = hostApi.CancelAttackFn != IntPtr.Zero
            ? (delegate* unmanaged[Cdecl]<int>)hostApi.CancelAttackFn
            : null;
        _queryHealth = hostApi.QueryHealthFn != IntPtr.Zero
            ? (delegate* unmanaged[Cdecl]<uint, int>)hostApi.QueryHealthFn
            : null;
        _meleeAttack = hostApi.MeleeAttackFn != IntPtr.Zero
            ? (delegate* unmanaged[Cdecl]<uint, int, float, int>)hostApi.MeleeAttackFn
            : null;
        _missileAttack = hostApi.MissileAttackFn != IntPtr.Zero
            ? (delegate* unmanaged[Cdecl]<uint, int, float, int>)hostApi.MissileAttackFn
            : null;
        _doMovement = hostApi.DoMovementFn != IntPtr.Zero
            ? (delegate* unmanaged[Cdecl]<uint, float, int, int>)hostApi.DoMovementFn
            : null;
        _stopMovement = hostApi.StopMovementFn != IntPtr.Zero
            ? (delegate* unmanaged[Cdecl]<uint, int, int>)hostApi.StopMovementFn
            : null;
        _jumpNonAutonomous = hostApi.JumpNonAutonomousFn != IntPtr.Zero
            ? (delegate* unmanaged[Cdecl]<float, int>)hostApi.JumpNonAutonomousFn
            : null;
        _setAutonomyLevel = hostApi.SetAutonomyLevelFn != IntPtr.Zero
            ? (delegate* unmanaged[Cdecl]<uint, int>)hostApi.SetAutonomyLevelFn
            : null;
        _setAutoRun = hostApi.SetAutoRunFn != IntPtr.Zero
            ? (delegate* unmanaged[Cdecl]<int, int>)hostApi.SetAutoRunFn
            : null;
        _tapJump = hostApi.TapJumpFn != IntPtr.Zero
            ? (delegate* unmanaged[Cdecl]<int>)hostApi.TapJumpFn
            : null;
        _setIncomingChatSuppression = hostApi.SetIncomingChatSuppressionFn != IntPtr.Zero
            ? (delegate* unmanaged[Cdecl]<int, void>)hostApi.SetIncomingChatSuppressionFn
            : null;
    }

    private static uint GetClientHookFlags()
    {
        return _getClientHookFlags != null ? _getClientHookFlags() : 0;
    }

    private static string OnOff(bool value)
    {
        return value ? "ON" : "OFF";
    }

    private static string OnOff(uint flags, uint flag)
    {
        return (flags & flag) != 0 ? "ON" : "OFF";
    }

    private static string ReadWideString(IntPtr textUtf16)
    {
        return textUtf16 != IntPtr.Zero ? Marshal.PtrToStringUni(textUtf16) ?? string.Empty : string.Empty;
    }

    private static string TrimForUi(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "(empty)";

        string singleLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= 96 ? singleLine : singleLine[..96] + "...";
    }

    private static void Text(string message)
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

    private static void Log(string message)
    {
        if (_logFn == IntPtr.Zero)
            return;

        IntPtr buffer = Marshal.StringToHGlobalAnsi(message);
        try
        {
            ((delegate* unmanaged[Cdecl]<IntPtr, void>)_logFn)(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void SetLastAction(string message)
    {
        _lastAction = message;
        Log($"Hello Box: {message}");
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

    [StructLayout(LayoutKind.Sequential)]
    private struct NexCoreApi
    {
        public uint Version;
        public IntPtr ImGuiContext;
        public IntPtr D3DDevice;
        public IntPtr GameHwnd;
        public IntPtr LogFn;
        public IntPtr ProbeClientHooksFn;
        public IntPtr GetClientHookFlagsFn;
        public IntPtr ChangeCombatModeFn;
        public IntPtr CancelAttackFn;
        public IntPtr QueryHealthFn;
        public IntPtr MeleeAttackFn;
        public IntPtr MissileAttackFn;
        public IntPtr DoMovementFn;
        public IntPtr StopMovementFn;
        public IntPtr JumpNonAutonomousFn;
        public IntPtr SetAutonomyLevelFn;
        public IntPtr SetAutoRunFn;
        public IntPtr TapJumpFn;
        public IntPtr SetIncomingChatSuppressionFn;
    }
}
