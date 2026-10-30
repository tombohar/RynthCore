// ============================================================================
//  RynthCore.Engine - UI/Data/LuaData.cs
//  Lua scripts (state, script list, console, last loaded script) for both Lua
//  faces, from RynthLua (v2: several scripts, ScriptInfos / Selected / ...)
//  or an older RynthAi that still exports the single-script payload.
//
//  RynthPluginGetLuaJson frees its previous buffer on each call, so the hub is
//  its only caller. RynthPluginSendLuaCommand queues: the plugin runs the
//  command on its next tick, so the refresh waits for that tick. UTF-8 both
//  ways (scripts keep non-ASCII text).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.UI.Data;

internal sealed class LuaPayload
{
    public bool Running { get; set; }
    public string ScriptName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public List<string> Scripts { get; set; } = new();
    public string Console { get; set; } = string.Empty;
    public int ConsoleSeq { get; set; }
    /// <summary>Text of the last script loaded; an editor takes it when LoadSeq changes.</summary>
    public string LoadedText { get; set; } = string.Empty;
    public string LoadedName { get; set; } = string.Empty;
    public int LoadSeq { get; set; }

    // ── RynthLua v2 (several scripts at once). Null from the old RynthAi
    //    exports, which the faces then show the single-script way. ─────────
    /// <summary>Every script (files, plus running ones that aren't saved). Null = old single-script payload.</summary>
    public List<LuaScriptInfo>? ScriptInfos { get; set; }
    /// <summary>The script the panel shows: Console / Running / ScriptName / Status are its.</summary>
    public string? Selected { get; set; }
    public int RunningCount { get; set; }
    /// <summary>RynthAi's "RynthAi.Script" interface is there (macro, nav, meta features for scripts).</summary>
    public bool? RynthAi { get; set; }
    /// <summary>A character is logged in (per-character autostart needs one). Null from older plugins.</summary>
    public bool? LoggedIn { get; set; }
}

/// <summary>One script in the v2 list (RynthLua's LuaScriptInfo).</summary>
internal sealed class LuaScriptInfo
{
    public string Name { get; set; } = string.Empty;
    public bool Running { get; set; }
    /// <summary>"none", "global" or "character" (for the logged-in character).</summary>
    public string Autostart { get; set; } = "none";
    /// <summary>From script.json; "(not saved)" for a running script with no file (the editor, /lua exec).</summary>
    public string Description { get; set; } = string.Empty;
    /// <summary>A folder script (index.lua); false for a single Name.lua.</summary>
    public bool IsFolder { get; set; }
    /// <summary>No file behind it (the editor, /lua exec): no autostart, restart or open.</summary>
    public bool Transient { get; set; }
}

internal sealed class LuaCmd
{
    public string Cmd { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
}

[JsonSerializable(typeof(LuaPayload))]
[JsonSerializable(typeof(LuaCmd))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = false)]
internal partial class LuaJsonContext : JsonSerializerContext { }

/// <summary>RynthPluginGetLuaJson while a Lua face is open (500 ms, and after each command).</summary>
internal sealed unsafe class LuaSource : UiSource<LuaPayload>
{
    private delegate* unmanaged[Cdecl]<IntPtr> _getLuaJson;
    private string? _lastJson;

    public LuaSource() : base("Lua", periodMs: 500) { }

    /// <summary>
    /// Lua is the RynthLua plugin since 2026-09-29; before that the same exports were
    /// RynthAi's. Ask RynthLua first, then an older RynthAi that still has them.
    /// </summary>
    internal static IntPtr ResolveLua(string export)
    {
        IntPtr p = PluginExportBinder.Resolve("RynthLua", export);
        return p != IntPtr.Zero ? p : PluginExportBinder.Resolve("RynthAi", export);
    }

    protected internal override void Poll()
    {
        if (_getLuaJson == null)
            _getLuaJson = (delegate* unmanaged[Cdecl]<IntPtr>)ResolveLua("RynthPluginGetLuaJson");
        if (_getLuaJson == null) return;
        IntPtr ptr = _getLuaJson();
        string? json = ptr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(ptr);
        if (string.IsNullOrEmpty(json) || json == _lastJson) return;
        _lastJson = json;
        LuaPayload? parsed = JsonSerializer.Deserialize(json, LuaJsonContext.Default.LuaPayload);
        if (parsed != null) Publish(parsed);
    }

    protected internal override void Reset()
    {
        _getLuaJson = null;
        _lastJson = null;
        ClearSnapshot();
    }
}

/// <summary>
/// RynthPluginSendLuaCommand on the pump thread: run / stop / load / save / delete / clearConsole,
/// and RynthLua's select / start / restart / stopScript (Name), autostart (Code = none|global|character),
/// execin (Name, Code).
/// </summary>
internal static unsafe class LuaCommands
{
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _send;

    static LuaCommands()
    {
        PluginManager.PluginsUnloaded += () => _send = null;
    }

    public static void Send(string cmd, string name = "", string code = "")
    {
        var command = new LuaCmd { Cmd = cmd, Name = name, Code = code };
        UiDataHub.Post("Lua " + cmd, () =>
        {
            if (_send == null)
                _send = (delegate* unmanaged[Cdecl]<IntPtr, void>)LuaSource.ResolveLua("RynthPluginSendLuaCommand");
            if (_send == null) return;
            string json = JsonSerializer.Serialize(command, LuaJsonContext.Default.LuaCmd);
            IntPtr utf8 = Marshal.StringToCoTaskMemUTF8(json);
            try { _send(utf8); }
            finally { Marshal.FreeCoTaskMem(utf8); }
            UiSources.Lua.RequestRefreshAfterPluginTick();
        });
    }
}
