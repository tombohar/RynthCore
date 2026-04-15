using System;
using System.Runtime.InteropServices;
using RynthCore.PluginSdk;

namespace RynthCore.PluginCore;

public unsafe sealed class RynthPluginRuntime<TPlugin>
    where TPlugin : RynthPluginBase, new()
{
    private TPlugin? _plugin;
    private bool _initialized;

    public TPlugin? Plugin => _plugin;
    public bool IsInitialized => _initialized && _plugin != null;

    public int Init(RynthCoreApiNative* api)
    {
        if (api == null)
            return 9;

        var plugin = new TPlugin();
        RynthCoreApiNative hostApi = *api;

        if (hostApi.Version < plugin.MinimumApiVersion)
            return 10;

        plugin.Attach(hostApi);
        int result = plugin.Initialize();
        if (result != 0)
            return result;

        _plugin = plugin;
        _initialized = true;
        return 0;
    }

    public void Shutdown()
    {
        if (!IsInitialized)
            return;

        try
        {
            _plugin!.Shutdown();
        }
        finally
        {
            _plugin = null;
            _initialized = false;
        }
    }

    public void OnTick()
    {
        if (IsInitialized)
            _plugin!.OnTick();
    }

    public void OnUIInitialized()
    {
        if (IsInitialized)
            _plugin!.OnUIInitialized();
    }

    public void OnLoginComplete()
    {
        if (IsInitialized)
            _plugin!.OnLoginComplete();
    }

    public void OnBarAction()
    {
        if (IsInitialized)
            _plugin!.OnBarAction();
    }

    public void OnRender()
    {
        if (IsInitialized)
            _plugin!.OnRender();
    }

    public void OnChatWindowText(IntPtr textUtf16, int chatType, IntPtr eatFlag)
    {
        if (!IsInitialized)
            return;

        int eat = eatFlag != IntPtr.Zero ? Marshal.ReadInt32(eatFlag) : 0;
        _plugin!.OnChatWindowText(textUtf16 != IntPtr.Zero ? Marshal.PtrToStringUni(textUtf16) : null, chatType, ref eat);
        if (eatFlag != IntPtr.Zero)
            Marshal.WriteInt32(eatFlag, eat);
    }

    public void OnChatBarEnter(IntPtr textUtf16, IntPtr eatFlag)
    {
        if (!IsInitialized)
            return;

        int eat = eatFlag != IntPtr.Zero ? Marshal.ReadInt32(eatFlag) : 0;
        _plugin!.OnChatBarEnter(textUtf16 != IntPtr.Zero ? Marshal.PtrToStringUni(textUtf16) : null, ref eat);
        if (eatFlag != IntPtr.Zero)
            Marshal.WriteInt32(eatFlag, eat);
    }

    public void OnBusyCountIncremented()
    {
        if (IsInitialized)
            _plugin!.OnBusyCountIncremented();
    }

    public void OnBusyCountDecremented()
    {
        if (IsInitialized)
            _plugin!.OnBusyCountDecremented();
    }

    public void OnSelectedTargetChange(uint currentTargetId, uint previousTargetId)
    {
        if (IsInitialized)
            _plugin!.OnSelectedTargetChange(currentTargetId, previousTargetId);
    }

    public void OnCombatModeChange(int currentCombatMode, int previousCombatMode)
    {
        if (IsInitialized)
            _plugin!.OnCombatModeChange(currentCombatMode, previousCombatMode);
    }

    public void OnSmartBoxEvent(uint opcode, uint blobSize, uint status)
    {
        if (IsInitialized)
            _plugin!.OnSmartBoxEvent(opcode, blobSize, status);
    }

    public void OnCreateObject(uint objectId)
    {
        if (IsInitialized)
            _plugin!.OnCreateObject(objectId);
    }

    public void OnDeleteObject(uint objectId)
    {
        if (IsInitialized)
            _plugin!.OnDeleteObject(objectId);
    }

    public void OnUpdateObject(uint objectId)
    {
        if (IsInitialized)
            _plugin!.OnUpdateObject(objectId);
    }

    public void OnUpdateObjectInventory(uint objectId)
    {
        if (IsInitialized)
            _plugin!.OnUpdateObjectInventory(objectId);
    }

    public void OnViewObjectContents(uint objectId)
    {
        if (IsInitialized)
            _plugin!.OnViewObjectContents(objectId);
    }

    public void OnStopViewingObjectContents(uint objectId)
    {
        if (IsInitialized)
            _plugin!.OnStopViewingObjectContents(objectId);
    }

    public void OnVendorOpen(uint vendorId)
    {
        if (IsInitialized)
            _plugin!.OnVendorOpen(vendorId);
    }

    public void OnVendorClose(uint vendorId)
    {
        if (IsInitialized)
            _plugin!.OnVendorClose(vendorId);
    }

    public void OnUpdateHealth(uint targetId, float healthRatio, uint currentHealth, uint maxHealth)
    {
        if (IsInitialized)
            _plugin!.OnUpdateHealth(targetId, healthRatio, currentHealth, maxHealth);
    }

    public void OnEnchantmentAdded(uint spellId, double durationSeconds)
    {
        if (IsInitialized)
            _plugin!.OnEnchantmentAdded(spellId, durationSeconds);
    }

    public void OnEnchantmentRemoved(uint enchantmentId)
    {
        if (IsInitialized)
            _plugin!.OnEnchantmentRemoved(enchantmentId);
    }
}
