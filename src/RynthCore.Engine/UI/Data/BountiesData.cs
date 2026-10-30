// ============================================================================
//  RynthCore.Engine - UI/Data/BountiesData.cs
//  The RynthILT plugin's ACECustom bounty tracker for the Bounties face (the
//  ILT section; ported from SilentKelpie's rynth/RynthCore PR #5, where it
//  read RynthAi).
//
//  RynthILTGetBountiesJson frees its previous buffer on each call, so the hub
//  is its only caller (BountiesSource, 1 s). The plugin also reads those polls
//  as "the window is open": its quiet /bounty list reads run only while this
//  source is subscribed AND polling. The source polls only on Infinite
//  Leaftide (IltInfo.IsIlt), so a Bounties window left open anywhere else
//  never makes RynthILT send anything. RynthILTBountyCommand runs on the pump
//  (it changes state the plugin's tick uses) and refreshes the snapshot.
// ============================================================================

using System;
using System.Runtime.InteropServices;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.UI.Data;

/// <summary>RynthILTGetBountiesJson while the Bounties face is open on ILT (1 s, and after each command).</summary>
internal sealed unsafe class BountiesSource : UiSource<BountiesSnapshot>
{
    /// <summary>The plugin's display name (PluginExportBinder matches on it).</summary>
    public const string PluginName = "RynthILT";

    private delegate* unmanaged[Cdecl]<IntPtr> _get;
    private string? _lastJson;
    private volatile bool _exportMissing;
    private volatile bool _notIlt;

    public BountiesSource() : base("Bounties", periodMs: 1000) { }

    /// <summary>True when RynthILT is loaded but has no RynthILTGetBountiesJson (an older or broken build).</summary>
    public bool ExportMissing => _exportMissing;

    /// <summary>True when the last poll was skipped because this isn't Infinite Leaftide.</summary>
    public bool NotIlt => _notIlt;

    protected internal override void Poll()
    {
        // Off ILT nothing is polled, so the plugin never thinks the window is open there.
        if (!IltInfo.IsIlt)
        {
            _notIlt = true;
            return;
        }
        _notIlt = false;
        if (_get == null)
            _get = (delegate* unmanaged[Cdecl]<IntPtr>)PluginExportBinder.Resolve(PluginName, "RynthILTGetBountiesJson");
        _exportMissing = _get == null && PluginExportBinder.Resolve(PluginName, "RynthPluginTick") != IntPtr.Zero;
        if (_get == null) return;
        IntPtr ptr = _get();
        string? json = ptr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(ptr);
        if (string.IsNullOrEmpty(json) || json == _lastJson) return;
        BountiesSnapshot? parsed;
        try { parsed = BountiesSnapshot.Parse(json); }
        catch { return; }
        if (parsed == null) return;
        _lastJson = json;
        Publish(parsed);
    }

    protected internal override void Reset()
    {
        _get = null;
        _lastJson = null;
        _exportMissing = false;
        ClearSnapshot();
    }
}

internal static unsafe class BountiesCommands
{
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _command;

    static BountiesCommands()
    {
        PluginManager.PluginsUnloaded += () => _command = null;
    }

    /// <summary>Re-reads /bounty list (the plugin keeps its own minimum interval).</summary>
    public static void Refresh() => Send("refresh");

    /// <summary>Sets one tracker option (refreshonkills, minrefreshseconds, periodicminutes, hideqblocked, sortbyprogress).</summary>
    public static void Set(string option, string value) => Send($"set {option} {value}");

    public static void Set(string option, bool on) => Send($"set {option} {(on ? "1" : "0")}");

    private static void Send(string text) => UiDataHub.Post("Bounties " + text, () =>
    {
        if (!IltInfo.IsIlt) return;
        if (_command == null)
            _command = (delegate* unmanaged[Cdecl]<IntPtr, void>)PluginExportBinder.Resolve(BountiesSource.PluginName, "RynthILTBountyCommand");
        if (_command == null) return;
        IntPtr utf8 = Marshal.StringToCoTaskMemUTF8(text);
        try { _command(utf8); }
        finally { Marshal.FreeCoTaskMem(utf8); }
        UiSources.Bounties.RequestRefresh();
    });
}
