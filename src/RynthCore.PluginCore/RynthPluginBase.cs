using System;
using System.Runtime.InteropServices;
using RynthCore.PluginSdk;

namespace RynthCore.PluginCore;

public abstract class RynthPluginBase
{
    public virtual uint MinimumApiVersion => RynthCoreHost.CurrentApiVersion;

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

    protected static string? ReadWideString(IntPtr textUtf16)
    {
        return textUtf16 != IntPtr.Zero ? Marshal.PtrToStringUni(textUtf16) : null;
    }

    public virtual int Initialize() => 0;
    public virtual void Shutdown() { }
    public virtual void OnTick() { }
    public virtual void OnUIInitialized() { }
    public virtual void OnLoginComplete() { }
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
    public virtual void OnUpdateHealth(uint targetId, float healthRatio, uint currentHealth, uint maxHealth) { }
    public virtual void OnEnchantmentAdded(uint spellId, double durationSeconds) { }
    public virtual void OnEnchantmentRemoved(uint enchantmentId) { }
}
