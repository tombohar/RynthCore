// ============================================================================
//  RynthCore.Engine - UI/Data/RynthSortData.cs
//  The RynthSort plugin's auto-sort switch, for the RynthAi dashboard's Sort
//  toggle (shown only while RynthSort is loaded and its character is known).
//
//  RynthSortAutoState is one int (-1 not logged in, 0 off, 1 on), polled twice
//  a second while the dashboard is open. RynthSortCommand queues an /rsort
//  command (without "/rsort") for the plugin's tick; the toggle sends
//  "on quiet" / "off quiet", so the chat stays clean.
// ============================================================================

using System;
using System.Runtime.InteropServices;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.UI.Data;

internal sealed unsafe class RynthSortWatch : UiSource<object>
{
    private delegate* unmanaged[Cdecl]<int> _get;
    private long _retryAtMs;
    private static volatile int _state = -1;

    /// <summary>-1: RynthSort isn't loaded or its character isn't known yet; 0 auto-sort off; 1 on. Any thread.</summary>
    public static int State => _state;

    public RynthSortWatch() : base("RynthSortWatch", periodMs: 500) { }

    protected internal override void Poll()
    {
        if (_get == null)
        {
            // Not loaded, or a RynthSort before 0.3.1 (no toggle): look again later.
            long now = Environment.TickCount64;
            if (now < _retryAtMs) return;
            _get = (delegate* unmanaged[Cdecl]<int>)PluginExportBinder.Resolve("RynthSort", "RynthSortAutoState");
            if (_get == null) { _state = -1; _retryAtMs = now + 10000; return; }
        }
        _state = _get();
    }

    protected internal override void Reset()
    {
        _get = null;
        _retryAtMs = 0;
        _state = -1;
    }

    /// <summary>Shows the new state at once (the next poll confirms it).</summary>
    internal static void Assume(bool on) => _state = on ? 1 : 0;
}

internal static unsafe class RynthSortCommands
{
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _command;

    static RynthSortCommands()
    {
        PluginManager.PluginsUnloaded += () => _command = null;
    }

    /// <summary>Turns RynthSort's auto-sort on or off for this character. AC thread or pump.</summary>
    public static void SetAuto(bool on)
    {
        RynthSortWatch.Assume(on);
        Command(on ? "on quiet" : "off quiet");
    }

    /// <summary>
    /// Flips auto-sort from RynthSort's state right now (a hotkey: the dashboard that polls it may be
    /// closed). Says so in chat when RynthSort isn't loaded or doesn't know the character yet.
    /// </summary>
    public static void ToggleAuto() => UiDataHub.Post("RynthSort toggle", () =>
    {
        UiSources.RynthSortWatch.Poll();   // pump thread, like the hub's own polls
        int state = RynthSortWatch.State;
        if (state < 0)
        {
            Compatibility.AcMainThreadQueue.EnqueueWriteToChat("[RynthCore] Auto-sort: RynthSort isn't loaded, or doesn't know this character yet.", 1);
            return;
        }
        SetAuto(state != 1);
    });

    /// <summary>An /rsort command without the "/rsort" ("on", "now", "tidy"...); runs on RynthSort's tick.</summary>
    public static void Command(string text) => UiDataHub.Post("RynthSort command", () =>
    {
        if (_command == null) _command = (delegate* unmanaged[Cdecl]<IntPtr, void>)PluginExportBinder.Resolve("RynthSort", "RynthSortCommand");
        if (_command == null) return;
        IntPtr p = Marshal.StringToCoTaskMemUTF8(text);
        try { _command(p); }
        finally { Marshal.FreeCoTaskMem(p); }
        UiSources.RynthSortWatch.RequestRefresh();
    });
}
