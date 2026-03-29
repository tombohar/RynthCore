// ============================================================================
//  NexCore.Engine - Plugins/PluginContract.cs
//  Defines the C ABI contract between NexCore and plugin DLLs.
//
//  A plugin DLL must export at least:
//    int  NexPluginInit(NexCoreAPI* api)   — return 0 on success
//    void NexPluginShutdown()
//
//  Optional exports:
//    const char* NexPluginName()           — human-readable name
//    const char* NexPluginVersion()        — version string (e.g. "1.0.0")
//    void        NexPluginTick()           — per-frame logic (before render)
//    void        NexPluginRender()         — per-frame ImGui drawing
// ============================================================================

using System;
using System.Runtime.InteropServices;

namespace NexCore.Engine.Plugins;

/// <summary>
/// The host API struct passed to every plugin at init time.
/// Plugins receive a pointer to this and can call back into NexCore.
/// Layout must stay ABI-stable — append new fields at the end only.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NexCoreAPI
{
    /// <summary>API version. Plugins should check this before using later fields.</summary>
    public uint Version;

    /// <summary>ImGui context pointer — plugin must call igSetCurrentContext before drawing.</summary>
    public IntPtr ImGuiContext;

    /// <summary>IDirect3DDevice9* — the game's active D3D device.</summary>
    public IntPtr D3DDevice;

    /// <summary>HWND of the game window.</summary>
    public IntPtr GameHwnd;

    /// <summary>Function pointer: void Log(const char* message)</summary>
    public IntPtr LogFn;

    /// <summary>Function pointer: void ProbeClientHooks()</summary>
    public IntPtr ProbeClientHooksFn;

    /// <summary>Function pointer: uint GetClientHookFlags()</summary>
    public IntPtr GetClientHookFlagsFn;

    /// <summary>Function pointer: int ChangeCombatMode(int combatMode)</summary>
    public IntPtr ChangeCombatModeFn;

    /// <summary>Function pointer: int CancelAttack()</summary>
    public IntPtr CancelAttackFn;

    /// <summary>Function pointer: int QueryHealth(uint targetId)</summary>
    public IntPtr QueryHealthFn;

    /// <summary>Function pointer: int MeleeAttack(uint targetId, int attackHeight, float powerLevel)</summary>
    public IntPtr MeleeAttackFn;

    /// <summary>Function pointer: int MissileAttack(uint targetId, int attackHeight, float accuracyLevel)</summary>
    public IntPtr MissileAttackFn;

    /// <summary>Function pointer: int DoMovement(uint motion, float speed, int holdKey)</summary>
    public IntPtr DoMovementFn;

    /// <summary>Function pointer: int StopMovement(uint motion, int holdKey)</summary>
    public IntPtr StopMovementFn;

    /// <summary>Function pointer: int JumpNonAutonomous(float extent)</summary>
    public IntPtr JumpNonAutonomousFn;

    /// <summary>Function pointer: int SetAutonomyLevel(uint level)</summary>
    public IntPtr SetAutonomyLevelFn;

    /// <summary>Function pointer: int SetAutoRun(int enabled)</summary>
    public IntPtr SetAutoRunFn;

    /// <summary>Function pointer: int TapJump()</summary>
    public IntPtr TapJumpFn;

    /// <summary>Function pointer: void SetIncomingChatSuppression(int enabled)</summary>
    public IntPtr SetIncomingChatSuppressionFn;
}

/// <summary>Current API version. Bump when adding fields to NexCoreAPI.</summary>
internal static class PluginContractVersion
{
    public const uint Current = 4;
}

internal static class ClientActionHookFlags
{
    public const uint CombatInitialized = 1u << 0;
    public const uint MovementInitialized = 1u << 1;
    public const uint MeleeAttack = 1u << 2;
    public const uint MissileAttack = 1u << 3;
    public const uint ChangeCombatMode = 1u << 4;
    public const uint CancelAttack = 1u << 5;
    public const uint QueryHealth = 1u << 6;
    public const uint DoMovement = 1u << 7;
    public const uint StopMovement = 1u << 8;
    public const uint JumpNonAutonomous = 1u << 9;
    public const uint SetAutonomyLevel = 1u << 10;
    public const uint SetAutoRun = 1u << 11;
    public const uint TapJump = 1u << 12;
}

// ─── Delegate types matching the plugin's exported functions ────────────

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int PluginInitDelegate(ref NexCoreAPI api);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginShutdownDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate IntPtr PluginNameDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate IntPtr PluginVersionDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginOnLoginCompleteDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginOnUIInitializedDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginOnSelectedTargetChangeDelegate(uint currentTargetId, uint previousTargetId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginOnSmartBoxEventDelegate(uint opcode, uint blobSize, uint status);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginOnDeleteObjectDelegate(uint objectId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginOnCreateObjectDelegate(uint objectId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginOnUpdateObjectDelegate(uint objectId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginOnUpdateObjectInventoryDelegate(uint objectId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginOnViewObjectContentsDelegate(uint objectId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginOnStopViewingObjectContentsDelegate(uint objectId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginOnChatWindowTextDelegate(IntPtr textUtf16, int chatType, IntPtr eatFlag);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginOnChatBarEnterDelegate(IntPtr textUtf16, IntPtr eatFlag);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginBarActionDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginTickDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PluginRenderDelegate();

// ─── Log callback that plugins can call ─────────────────────────────────

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void LogCallbackDelegate(IntPtr messageUtf8);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void ProbeClientHooksCallbackDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate uint GetClientHookFlagsCallbackDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int ChangeCombatModeCallbackDelegate(int combatMode);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int CancelAttackCallbackDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int QueryHealthCallbackDelegate(uint targetId);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int MeleeAttackCallbackDelegate(uint targetId, int attackHeight, float powerLevel);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int MissileAttackCallbackDelegate(uint targetId, int attackHeight, float accuracyLevel);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int DoMovementCallbackDelegate(uint motion, float speed, int holdKey);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int StopMovementCallbackDelegate(uint motion, int holdKey);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int JumpNonAutonomousCallbackDelegate(float extent);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int SetAutonomyLevelCallbackDelegate(uint level);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int SetAutoRunCallbackDelegate(int enabled);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int TapJumpCallbackDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void SetIncomingChatSuppressionCallbackDelegate(int enabled);
