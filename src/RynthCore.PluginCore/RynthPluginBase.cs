using System;
using System.Runtime.InteropServices;
using RynthCore.PluginSdk;

namespace RynthCore.PluginCore;

public abstract class RynthPluginBase
{
    /// <summary>
    /// The oldest engine API the plugin starts on (RynthPluginRuntime refuses older ones). Defaults to
    /// the plugin's manifest minimum (RynthPluginMinEngineApi in its csproj, compiled in as
    /// [assembly: RynthPluginManifest]) so the plugin's own check and the engine's/launcher's manifest
    /// check agree; without a manifest, the SDK baseline.
    /// </summary>
    public virtual uint MinimumApiVersion => ManifestMinimum(GetType()) ?? RynthCoreHost.BaselineApiVersion;

    private static uint? ManifestMinimum(Type pluginType)
    {
        try
        {
            return System.Reflection.CustomAttributeExtensions.GetCustomAttribute<RynthPluginManifestAttribute>(pluginType.Assembly)?.MinEngineApi;
        }
        catch
        {
            return null;
        }
    }

    protected RynthCoreApiNative Api { get; private set; }
    protected RynthCoreHost Host { get; private set; }
    protected bool IsAttached { get; private set; }

    internal void Attach(RynthCoreApiNative api)
    {
        Api = api;
        Host = new RynthCoreHost(api);
        IsAttached = true;
    }

    protected void Log(string message)
    {
        if (!IsAttached)
            return;

        Host.Log(message);
    }

    internal void LogInternal(string message)
    {
        if (!IsAttached)
            return;

        Host.Log(message);
    }

    protected static string? ReadWideString(IntPtr textUtf16)
    {
        return textUtf16 != IntPtr.Zero ? Marshal.PtrToStringUni(textUtf16) : null;
    }

    public virtual int Initialize() => 0;
    public virtual void Shutdown() { }
    public virtual void OnTick() { }
    public virtual void OnUIInitialized() { }
    public virtual void OnLoginComplete() { }
    public virtual void OnLogout() { }
    public virtual void OnBarAction() { }
    public virtual void OnRender() { }
    public virtual void OnChatWindowText(string? text, int chatType, ref int eat) { }
    public virtual void OnChatBarEnter(string? text, ref int eat) { }
    public virtual void OnBusyCountIncremented() { }
    public virtual void OnBusyCountDecremented() { }
    public virtual void OnSelectedTargetChange(uint currentTargetId, uint previousTargetId) { }
    public virtual void OnCombatModeChange(int currentCombatMode, int previousCombatMode) { }
    public virtual void OnSmartBoxEvent(uint opcode, uint blobSize, uint status) { }
    public virtual void OnCreateObject(uint objectId) { }
    public virtual void OnDeleteObject(uint objectId) { }
    public virtual void OnUpdateObject(uint objectId) { }
    public virtual void OnUpdateObjectInventory(uint objectId) { }
    public virtual void OnViewObjectContents(uint objectId) { }
    public virtual void OnStopViewingObjectContents(uint objectId) { }
    public virtual void OnVendorOpen(uint vendorId) { }
    public virtual void OnVendorClose(uint vendorId) { }
    public virtual void OnUpdateHealth(uint targetId, float healthRatio, uint currentHealth, uint maxHealth) { }
    public virtual void OnCombatDamage(uint damage, uint damageType, bool crit, bool isAttacker) { }
    public virtual void OnKillNotification(string? deathMessage) { }
    public virtual void OnEnchantmentAdded(uint spellId, double durationSeconds) { }
    public virtual void OnEnchantmentRemoved(uint enchantmentId) { }

    // v77 (RynthCoreHost.HasUiHooks): the client's screens and AC's own tooltips and drag/drop.
    // The plugin's exports call these through RynthPluginRuntime; an older engine never calls them.

    /// <summary>The client changed screens (<see cref="RynthScreenMode"/>): e.g. World to CharacterSelect at logout.</summary>
    public virtual void OnScreenChanged(int oldMode, int newMode) { }
    /// <summary>The client is shutting down and about to tear its UI down. Last chance to read AC
    /// state; no ticks or events follow (Shutdown still does).</summary>
    public virtual void OnClientCleanup() { }
    /// <summary>AC shows a tooltip for an item (objectId) or a spell icon (spellId); both 0 for any other element.</summary>
    public virtual void OnTooltipShow(uint objectId, uint spellId) { }
    /// <summary>The tooltip from <see cref="OnTooltipShow"/> is gone.</summary>
    public virtual void OnTooltipHide() { }
    /// <summary>The player started dragging an item (objectId, its icon) or a spell (spellId) in AC's UI.</summary>
    public virtual void OnDragStart(uint objectId, uint spellId, uint iconId) { }
    /// <summary>AC took a dropped item or spell; targetElementId is the AC UI element it landed on.</summary>
    public virtual void OnItemDropped(uint objectId, uint spellId, uint targetElementId) { }
}
