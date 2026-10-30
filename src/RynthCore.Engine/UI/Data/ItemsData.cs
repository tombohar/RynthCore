// ============================================================================
//  RynthCore.Engine - UI/Data/ItemsData.cs
//  RynthAi's item rules (weapons with their element, consumables with their
//  type, mana stone tapping) for both Items faces.
//
//  RynthPluginGetItemsJson frees its previous buffer on each call, so the hub
//  is its only caller (ItemsSource, 1 s). RynthPluginSetItemsJson and the two
//  Add Selected exports apply on the spot; they run on the pump (they read
//  and change plugin state the tick also uses) and refresh the snapshot.
// ============================================================================

using System;
using System.Runtime.InteropServices;
using System.Text.Json;
using RynthCore.Engine.Plugins;
using RynthCore.Engine.UI.Panels;

namespace RynthCore.Engine.UI.Data;

/// <summary>The items JSON and its parse. Parsed is shared: copy before changing (ParseCopy).</summary>
internal sealed class ItemsSnapshot
{
    public ItemsSnapshot(string json, ItemsPanel.Payload parsed) { Json = json; Parsed = parsed; }
    public string Json { get; }
    public ItemsPanel.Payload Parsed { get; }

    public ItemsPanel.Payload ParseCopy() =>
        JsonSerializer.Deserialize(Json, ItemsPanelJsonContext.Default.Payload) ?? new ItemsPanel.Payload();
}

/// <summary>RynthPluginGetItemsJson while an Items face is open (1 s, and after each change).</summary>
internal sealed unsafe class ItemsSource : UiSource<ItemsSnapshot>
{
    private delegate* unmanaged[Cdecl]<IntPtr> _get;
    private string? _lastJson;

    public ItemsSource() : base("Items", periodMs: 1000) { }

    protected internal override void Poll()
    {
        if (_get == null)
            _get = (delegate* unmanaged[Cdecl]<IntPtr>)PluginExportBinder.Resolve("RynthAi", "RynthPluginGetItemsJson");
        if (_get == null) return;
        IntPtr ptr = _get();
        string? json = ptr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ptr);
        if (string.IsNullOrEmpty(json) || json == _lastJson) return;
        ItemsPanel.Payload? parsed;
        try { parsed = JsonSerializer.Deserialize(json, ItemsPanelJsonContext.Default.Payload); }
        catch { return; }
        if (parsed == null) return;
        _lastJson = json;
        Publish(new ItemsSnapshot(json, parsed));
    }

    protected internal override void Reset()
    {
        _get = null;
        _lastJson = null;
        ClearSnapshot();
    }
}

internal static unsafe class ItemsCommands
{
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _set;
    private static delegate* unmanaged[Cdecl]<void> _addWeapon, _addShield, _addConsumable, _clean;

    /// <summary>ItemRule.Action value the plugin uses for off-hand shields.</summary>
    public const string ShieldAction = "Shield";

    static ItemsCommands()
    {
        PluginManager.PluginsUnloaded += () => { _set = null; _addWeapon = null; _addShield = null; _addConsumable = null; _clean = null; };
    }

    public static readonly string[] Elements = { "Slash", "Pierce", "Bludgeon", "Fire", "Cold", "Lightning", "Acid", "Nether" };
    public static readonly string[] ConsumableTypes = { "General", "Lockpick", "HealthKit", "HealthPotion", "ManaPotion", "StaminaPotion", "ManaStone", "Stamina", "Pet" };

    private static IntPtr Export(string name) => PluginExportBinder.Resolve("RynthAi", name);

    /// <summary>Saves the whole items payload, already serialized (a copy the caller no longer changes).</summary>
    public static void SetJson(string json) => UiDataHub.Post("Items save", () =>
    {
        if (_set == null) _set = (delegate* unmanaged[Cdecl]<IntPtr, void>)Export("RynthPluginSetItemsJson");
        if (_set == null) return;
        IntPtr ansi = Marshal.StringToHGlobalAnsi(json);
        try { _set(ansi); }
        finally { Marshal.FreeHGlobal(ansi); }
        UiSources.Items.RequestRefresh();
    });

    /// <summary>Adds the item selected in the inventory as a weapon.</summary>
    public static void AddSelectedWeapon() => UiDataHub.Post("Items add weapon", () =>
    {
        if (_addWeapon == null) _addWeapon = (delegate* unmanaged[Cdecl]<void>)Export("RynthPluginAddSelectedWeapon");
        if (_addWeapon != null) _addWeapon();
        UiSources.Items.RequestRefresh();
    });

    /// <summary>
    /// Adds the item selected in the inventory as an off-hand shield. RynthAi older than
    /// 0.6.20 has no such export: the click is logged and dropped.
    /// </summary>
    public static void AddSelectedShield() => UiDataHub.Post("Items add shield", () =>
    {
        if (_addShield == null) _addShield = (delegate* unmanaged[Cdecl]<void>)Export("RynthPluginAddSelectedShield");
        if (_addShield != null) _addShield();
        else RynthLog.UI("ItemsCommands: Add Selected Shield unavailable - RynthAi 0.6.20 or newer is required.");
        UiSources.Items.RequestRefresh();
    });

    /// <summary>Adds the item selected in the inventory as a consumable.</summary>
    public static void AddSelectedConsumable() => UiDataHub.Post("Items add consumable", () =>
    {
        if (_addConsumable == null) _addConsumable = (delegate* unmanaged[Cdecl]<void>)Export("RynthPluginAddSelectedConsumable");
        if (_addConsumable != null) _addConsumable();
        UiSources.Items.RequestRefresh();
    });

    /// <summary>
    /// Removes weapons and consumables no longer in the inventory; RynthAi reports what it did
    /// in chat. RynthAi older than 0.5.43 has no such export: the click is logged and dropped.
    /// </summary>
    public static void CleanMissing() => UiDataHub.Post("Items clean", () =>
    {
        if (_clean == null) _clean = (delegate* unmanaged[Cdecl]<void>)Export("RynthPluginCleanItems");
        if (_clean != null) _clean();
        else RynthLog.UI("ItemsCommands: Remove Missing unavailable - RynthAi 0.5.43 or newer is required.");
        UiSources.Items.RequestRefresh();
    });

    public static string Serialize(ItemsPanel.Payload payload) =>
        JsonSerializer.Serialize(payload, ItemsPanelJsonContext.Default.Payload);
}
