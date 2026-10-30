using System;

namespace RynthCore.PluginSdk;

// ─── The client's screens and UI hooks (engine API v77) ─────────────────────
// RynthCoreHost.GetScreenMode / GetUiHookFlags, and the callbacks
// RynthPluginBase.OnScreenChanged, OnClientCleanup, OnTooltipShow, OnTooltipHide,
// OnDragStart and OnItemDropped. Check RynthCoreHost.HasUiHooks first: an engine
// before v77 has neither the functions nor the callbacks.

/// <summary>The client's screen: its UIFlow mode (the client's own ids).</summary>
public static class RynthScreenMode
{
    /// <summary>Not known yet (or an engine before v77).</summary>
    public const int Unknown = 0;
    public const int Intro = 0x10000001;
    public const int Disconnected = 0x10000002;
    public const int DataPatch = 0x10000003;
    public const int Credits = 0x10000005;
    /// <summary>The world: the character is logged in.</summary>
    public const int World = 0x10000008;
    public const int Epilogue = 0x10000009;
    public const int CharacterSelect = 0x1000000A;
    public const int CharacterCreation = 0x1000000B;

    /// <summary>A short name for logs.</summary>
    public static string Name(int mode) => mode switch
    {
        Unknown => "unknown",
        Intro => "Intro",
        Disconnected => "Disconnected",
        DataPatch => "DataPatch",
        Credits => "Credits",
        World => "World",
        Epilogue => "Epilogue",
        CharacterSelect => "CharacterSelect",
        CharacterCreation => "CharacterCreation",
        _ => $"0x{mode:X8}",
    };
}

/// <summary>Which UI hooks are live (RynthCoreHost.GetUiHookFlags), so which callbacks can arrive.</summary>
[Flags]
public enum RynthUiHookFlags : uint
{
    None = 0,
    /// <summary>OnScreenChanged comes from the screen-change hook, as it happens.</summary>
    ScreenHook = 1u << 0,
    /// <summary>OnScreenChanged comes from the engine's mode poll instead (a tick late).</summary>
    ScreenPoll = 1u << 1,
    /// <summary>OnClientCleanup will come when the client shuts down.</summary>
    ClientCleanup = 1u << 2,
    TooltipShow = 1u << 3,
    TooltipHide = 1u << 4,
    DragStart = 1u << 5,
    ItemDropped = 1u << 6,
    /// <summary>The client's cleanup has started: no more ticks or events.</summary>
    CleanupStarted = 1u << 7,
}
