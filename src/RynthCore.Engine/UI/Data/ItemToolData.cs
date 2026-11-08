// ============================================================================
//  RynthCore.Engine - UI/Data/ItemToolData.cs
//  The Item Tool panel's data (ImGui/Panels/ItemToolFace.cs): RynthAi's chest
//  looter and Quick give, as RynthAi's RynthPluginGetItemToolJson reports them
//  (picked chest/key/item/target and a status line each), and the panel's
//  buttons as words posted to RynthPluginItemToolCommand (the same words as
//  "/ra itemtool ..."; RynthAi's RynthAiPlugin.ItemTool.cs runs them on its tick).
//
//  RynthPluginGetItemToolJson frees its previous buffer on each call, so the hub
//  is its only caller (ItemToolSource, 250 ms while the panel is open). The
//  command export is the panel's alone: Quick give starts only from a button
//  pressed here or a line the player types, never from a remote command.
// ============================================================================

using System;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.UI.Data;

internal sealed class ItemToolSnapshot
{
    [JsonPropertyName("chest")] public ChestToolState Chest { get; set; } = new();
    [JsonPropertyName("give")]  public GiveToolState Give { get; set; } = new();
}

internal sealed class ChestToolState
{
    [JsonPropertyName("name")]    public string Name { get; set; } = string.Empty;
    [JsonPropertyName("key")]     public string Key { get; set; } = string.Empty;
    [JsonPropertyName("status")]  public string Status { get; set; } = string.Empty;
    [JsonPropertyName("running")] public bool Running { get; set; }
    [JsonPropertyName("looted")]  public int Looted { get; set; }
    // "Chest re-lock wait (s)" (2026-11-05). 0 = a RynthAi that predates it: the panel hides the control.
    [JsonPropertyName("relockSec")]   public int RelockSec { get; set; }
    [JsonPropertyName("relockMin")]   public int RelockMin { get; set; } = 5;
    [JsonPropertyName("relockMax")]   public int RelockMax { get; set; } = 600;
    // False until the character's settings profile loaded (RynthAi refuses the change then).
    [JsonPropertyName("relockReady")] public bool RelockReady { get; set; }
}

internal sealed class GiveToolState
{
    [JsonPropertyName("item")]    public string Item { get; set; } = string.Empty;
    [JsonPropertyName("target")]  public string Target { get; set; } = string.Empty;
    [JsonPropertyName("status")]  public string Status { get; set; } = string.Empty;
    [JsonPropertyName("running")] public bool Running { get; set; }
    [JsonPropertyName("given")]   public int Given { get; set; }
}

[JsonSerializable(typeof(ItemToolSnapshot))]
internal sealed partial class ItemToolJsonContext : JsonSerializerContext { }

/// <summary>RynthPluginGetItemToolJson while the Item Tool panel is open (250 ms, and after each button).</summary>
internal sealed unsafe class ItemToolSource : UiSource<ItemToolSnapshot>
{
    private delegate* unmanaged[Cdecl]<IntPtr> _get;
    private string? _lastJson;

    public ItemToolSource() : base("ItemTool", periodMs: 250) { }

    protected internal override void Poll()
    {
        if (_get == null)
            _get = (delegate* unmanaged[Cdecl]<IntPtr>)PluginExportBinder.Resolve("RynthAi", "RynthPluginGetItemToolJson");
        if (_get == null) return;
        IntPtr ptr = _get();
        string? json = ptr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ptr);
        if (string.IsNullOrEmpty(json) || json == _lastJson) return;
        ItemToolSnapshot? parsed;
        try { parsed = JsonSerializer.Deserialize(json, ItemToolJsonContext.Default.ItemToolSnapshot); }
        catch { return; }
        if (parsed == null) return;
        _lastJson = json;
        Publish(parsed);
    }

    protected internal override void Reset()
    {
        _get = null;
        _lastJson = null;
        ClearSnapshot();
    }
}

internal static unsafe class ItemToolCommands
{
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _command;

    static ItemToolCommands()
    {
        PluginManager.PluginsUnloaded += () => _command = null;
    }

    /// <summary>
    /// Posts one Item Tool command ("chest set", "chest key", "chest start", "chest stop", "relock N",
    /// "giveitem", "givetarget", "give", "givestop"). Runs on the pump; RynthAi older than this build has no such
    /// export: the click is logged and dropped.
    /// </summary>
    public static void Send(string words) => UiDataHub.Post("Item Tool " + words, () =>
    {
        if (_command == null)
            _command = (delegate* unmanaged[Cdecl]<IntPtr, void>)PluginExportBinder.Resolve("RynthAi", "RynthPluginItemToolCommand");
        if (_command == null)
        {
            RynthLog.UI($"ItemToolCommands: RynthPluginItemToolCommand not exported; dropped '{words}' (RynthAi is older than the Item Tool).");
            return;
        }
        IntPtr ansi = Marshal.StringToHGlobalAnsi(words);
        try { _command(ansi); }
        finally { Marshal.FreeHGlobal(ansi); }
        UiSources.ItemTool.RequestRefresh();
    });
}
