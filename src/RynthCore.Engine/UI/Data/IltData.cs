// ============================================================================
//  RynthCore.Engine - UI/Data/IltData.cs
//  The RynthILT plugin's side of the ILT section (Infinite Leaftide), apart from
//  the Bounties window (BountiesData.cs): whether RynthILT is loaded and what it
//  says about the world, and the commands the engine sends it.
//
//  Since 2026-10-13 every ILT feature is RynthILT's (the ILT Hub moved out of
//  RynthAi): the Guardian panel (RynthPluginRenderEmbed "guardian"), the Skills
//  panel's Progression tab (RynthILTGetProgressionJson, "prog ..." through
//  RynthILTCommand) and the Mini Remote's ILT tabs (RynthAi hosts those and asks
//  RynthILT itself).
//
//  IltStatusSource polls RynthILTGetStatusJson once a second on the pump while
//  RynthILT is installed (EntryPoint subscribes it). IltInfo reads the result:
//  RynthILT loaded, and RynthILT's own ILT verdict (its server list or the
//  character's "Treat this world as ILT"), which counts as ILT next to the
//  engine's host / world-name detection.
// ============================================================================

using System;
using System.Runtime.InteropServices;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.UI.Data;

/// <summary>RynthILTGetStatusJson's payload.</summary>
internal sealed class IltPluginStatus
{
    public bool Loaded { get; init; }
    public bool Ilt { get; init; }
    public bool Forced { get; init; }
    public bool Available { get; init; }
    public string World { get; init; } = "";

    public static readonly IltPluginStatus NotLoaded = new();
}

/// <summary>RynthILTGetStatusJson every second while subscribed (EntryPoint, with RynthILT installed).</summary>
internal sealed unsafe class IltStatusSource : UiSource<IltPluginStatus>
{
    public const string PluginName = "RynthILT";

    private delegate* unmanaged[Cdecl]<IntPtr> _get;
    private string? _lastJson;

    public IltStatusSource() : base("IltStatus", periodMs: 1000) { }

    protected internal override void Poll()
    {
        if (_get == null)
            _get = (delegate* unmanaged[Cdecl]<IntPtr>)PluginExportBinder.Resolve(PluginName, "RynthILTGetStatusJson");
        if (_get == null)
        {
            // Not loaded (or a RynthILT older than the move: bounties only).
            IltInfo.SetPluginStatus(loaded: PluginExportBinder.Resolve(PluginName, "RynthPluginTick") != IntPtr.Zero, ilt: false, reason: "");
            Publish(IltPluginStatus.NotLoaded);
            return;
        }
        IntPtr p = _get();
        string? json = p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
        if (string.IsNullOrEmpty(json) || json == _lastJson) return;
        _lastJson = json;
        if (!IltDetect.ParsePluginStatus(json, out bool loaded, out bool ilt, out bool forced, out bool available, out string world))
            return;
        var status = new IltPluginStatus { Loaded = loaded, Ilt = ilt, Forced = forced, Available = available, World = world };
        IltInfo.SetPluginStatus(status.Loaded, status.Ilt,
            status.Forced ? "RynthILT: \"Treat this world as ILT\" is ticked for this character" : $"RynthILT: world \"{status.World}\" is in its server list");
        Publish(status);
    }

    protected internal override void Reset()
    {
        _get = null;
        _lastJson = null;
        IltInfo.SetPluginStatus(loaded: false, ilt: false, reason: "");
        ClearSnapshot();
    }
}

/// <summary>Commands to RynthILT (run on the pump through UiDataHub).</summary>
internal static unsafe class IltCommands
{
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _command;

    static IltCommands()
    {
        PluginManager.PluginsUnloaded += () => _command = null;
    }

    /// <summary>
    /// "prog &lt;args&gt;" (the Skills panel's Progression tab). Goes to RynthILT; a RynthAi from before
    /// the move (no RynthILT command export) still takes it as its own remote command.
    /// </summary>
    public static void Progression(string value) => UiDataHub.Post("RynthILT prog", () =>
    {
        if (!Send("prog " + value)) RynthAiCommands.ApplyRemoteCommand("prog", value);
    });

    /// <summary>One RynthILTCommand call; false when RynthILT doesn't have it. Pump thread.</summary>
    private static bool Send(string text)
    {
        if (_command == null)
            _command = (delegate* unmanaged[Cdecl]<IntPtr, void>)PluginExportBinder.Resolve(IltStatusSource.PluginName, "RynthILTCommand");
        if (_command == null) return false;
        IntPtr utf8 = Marshal.StringToCoTaskMemUTF8(text);
        try { _command(utf8); }
        finally { Marshal.FreeCoTaskMem(utf8); }
        return true;
    }
}
