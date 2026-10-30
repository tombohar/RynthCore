using System;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.PluginSdk;

namespace RynthCore.PluginCore;

public unsafe sealed class RynthPluginRuntime<TPlugin>
    where TPlugin : RynthPluginBase, new()
{
    private TPlugin? _plugin;
    private bool _initialized;
    private int _exceptionCount;
    private const int MaxLoggedExceptions = 20;

    // Init results besides the plugin's own Initialize() return value. The
    // engine treats any non-zero result as "Init failed" (PluginManager marks
    // the plugin failed, logs the code, and never ticks or shuts it down).
    public const int InitNullApi = 9;
    public const int InitApiTooOld = 10;
    public const int InitThrew = 11;

    public TPlugin? Plugin => _plugin;
    public bool IsInitialized => _initialized && _plugin != null;

    // Init and Shutdown are called straight from [UnmanagedCallersOnly]
    // exports: an exception escaping them takes acclient.exe down (NativeAOT
    // fails fast), so both catch everything and log.
    public int Init(RynthCoreApiNative* api)
    {
        if (api == null)
            return InitNullApi;

        TPlugin? plugin = null;
        try
        {
            plugin = new TPlugin();
            RynthCoreApiNative hostApi = *api;

            if (hostApi.Version < plugin.MinimumApiVersion)
                return InitApiTooOld;

            plugin.Attach(hostApi);
            int result = plugin.Initialize();
            if (result != 0)
                return result;

            _plugin = plugin;
            _initialized = true;
            return 0;
        }
        catch (Exception ex)
        {
            // Shutdown isn't called on a plugin whose Init failed, and isn't
            // attempted here either: on a half-initialized plugin it could
            // do more harm (e.g. save settings it never loaded).
            try { plugin?.LogInternal($"[RynthCore] Initialize threw {ex.GetType().Name}: {ex.Message} - plugin not started."); }
            catch { }
            return InitThrew;
        }
    }

    public void Shutdown()
    {
        if (!IsInitialized)
            return;

        try
        {
            _plugin!.Shutdown();
        }
        catch (Exception ex)
        {
            try { _plugin?.LogInternal($"[RynthCore] Shutdown threw {ex.GetType().Name}: {ex.Message}"); }
            catch { }
        }
        finally
        {
            _plugin = null;
            _initialized = false;
        }
    }

    public void OnTick()
    {
        if (!IsInitialized) return;
        try { _plugin!.OnTick(); }
        catch (Exception ex) { LogException("OnTick", ex); }
    }

    public void OnUIInitialized()
    {
        if (!IsInitialized) return;
        try { _plugin!.OnUIInitialized(); }
        catch (Exception ex) { LogException("OnUIInitialized", ex); }
    }

    public void OnLoginComplete()
    {
        if (!IsInitialized) return;
        try { _plugin!.OnLoginComplete(); }
        catch (Exception ex) { LogException("OnLoginComplete", ex); }
    }

    public void OnLogout()
    {
        if (!IsInitialized) return;
        try { _plugin!.OnLogout(); }
        catch (Exception ex) { LogException("OnLogout", ex); }
    }

    public void OnBarAction()
    {
        if (!IsInitialized) return;
        try { _plugin!.OnBarAction(); }
        catch (Exception ex) { LogException("OnBarAction", ex); }
    }

    public void OnRender()
    {
        if (!IsInitialized) return;
        try { _plugin!.OnRender(); }
        catch (Exception ex) { LogException("OnRender", ex); }
    }

    public void OnChatWindowText(IntPtr textUtf16, int chatType, IntPtr eatFlag)
    {
        if (!IsInitialized)
            return;

        try
        {
            int eat = eatFlag != IntPtr.Zero ? Marshal.ReadInt32(eatFlag) : 0;
            _plugin!.OnChatWindowText(textUtf16 != IntPtr.Zero ? Marshal.PtrToStringUni(textUtf16) : null, chatType, ref eat);
            if (eatFlag != IntPtr.Zero)
                Marshal.WriteInt32(eatFlag, eat);
        }
        catch (Exception ex) { LogException("OnChatWindowText", ex); }
    }

    public void OnChatBarEnter(IntPtr textUtf16, IntPtr eatFlag)
    {
        if (!IsInitialized)
            return;

        try
        {
            int eat = eatFlag != IntPtr.Zero ? Marshal.ReadInt32(eatFlag) : 0;
            _plugin!.OnChatBarEnter(textUtf16 != IntPtr.Zero ? Marshal.PtrToStringUni(textUtf16) : null, ref eat);
            if (eatFlag != IntPtr.Zero)
                Marshal.WriteInt32(eatFlag, eat);
        }
        catch (Exception ex) { LogException("OnChatBarEnter", ex); }
    }

    public void OnBusyCountIncremented()
    {
        if (!IsInitialized) return;
        try { _plugin!.OnBusyCountIncremented(); }
        catch (Exception ex) { LogException("OnBusyCountIncremented", ex); }
    }

    public void OnBusyCountDecremented()
    {
        if (!IsInitialized) return;
        try { _plugin!.OnBusyCountDecremented(); }
        catch (Exception ex) { LogException("OnBusyCountDecremented", ex); }
    }

    public void OnSelectedTargetChange(uint currentTargetId, uint previousTargetId)
    {
        if (!IsInitialized) return;
        try { _plugin!.OnSelectedTargetChange(currentTargetId, previousTargetId); }
        catch (Exception ex) { LogException("OnSelectedTargetChange", ex); }
    }

    public void OnCombatModeChange(int currentCombatMode, int previousCombatMode)
    {
        if (!IsInitialized) return;
        try { _plugin!.OnCombatModeChange(currentCombatMode, previousCombatMode); }
        catch (Exception ex) { LogException("OnCombatModeChange", ex); }
    }

    public void OnSmartBoxEvent(uint opcode, uint blobSize, uint status)
    {
        if (!IsInitialized) return;
        try { _plugin!.OnSmartBoxEvent(opcode, blobSize, status); }
        catch (Exception ex) { LogException("OnSmartBoxEvent", ex); }
    }

    public void OnCreateObject(uint objectId)
    {
        if (!IsInitialized) return;
        try { _plugin!.OnCreateObject(objectId); }
        catch (Exception ex) { LogException("OnCreateObject", ex); }
    }

    public void OnDeleteObject(uint objectId)
    {
        if (!IsInitialized) return;
        try { _plugin!.OnDeleteObject(objectId); }
        catch (Exception ex) { LogException("OnDeleteObject", ex); }
    }

    public void OnUpdateObject(uint objectId)
    {
        if (!IsInitialized) return;
        try { _plugin!.OnUpdateObject(objectId); }
        catch (Exception ex) { LogException("OnUpdateObject", ex); }
    }

    public void OnUpdateObjectInventory(uint objectId)
    {
        if (!IsInitialized) return;
        try { _plugin!.OnUpdateObjectInventory(objectId); }
        catch (Exception ex) { LogException("OnUpdateObjectInventory", ex); }
    }

    public void OnViewObjectContents(uint objectId)
    {
        if (!IsInitialized) return;
        try { _plugin!.OnViewObjectContents(objectId); }
        catch (Exception ex) { LogException("OnViewObjectContents", ex); }
    }

    public void OnStopViewingObjectContents(uint objectId)
    {
        if (!IsInitialized) return;
        try { _plugin!.OnStopViewingObjectContents(objectId); }
        catch (Exception ex) { LogException("OnStopViewingObjectContents", ex); }
    }

    public void OnVendorOpen(uint vendorId)
    {
        if (!IsInitialized) return;
        try { _plugin!.OnVendorOpen(vendorId); }
        catch (Exception ex) { LogException("OnVendorOpen", ex); }
    }

    public void OnVendorClose(uint vendorId)
    {
        if (!IsInitialized) return;
        try { _plugin!.OnVendorClose(vendorId); }
        catch (Exception ex) { LogException("OnVendorClose", ex); }
    }

    public void OnUpdateHealth(uint targetId, float healthRatio, uint currentHealth, uint maxHealth)
    {
        if (!IsInitialized) return;
        try { _plugin!.OnUpdateHealth(targetId, healthRatio, currentHealth, maxHealth); }
        catch (Exception ex) { LogException("OnUpdateHealth", ex); }
    }

    public void OnCombatDamage(uint damage, uint damageType, uint crit, uint isAttacker)
    {
        if (!IsInitialized) return;
        try { _plugin!.OnCombatDamage(damage, damageType, crit != 0, isAttacker != 0); }
        catch (Exception ex) { LogException("OnCombatDamage", ex); }
    }

    public void OnKillNotification(IntPtr textUtf16)
    {
        if (!IsInitialized) return;
        try { _plugin!.OnKillNotification(textUtf16 != IntPtr.Zero ? Marshal.PtrToStringUni(textUtf16) : null); }
        catch (Exception ex) { LogException("OnKillNotification", ex); }
    }

    public void OnEnchantmentAdded(uint spellId, double durationSeconds)
    {
        if (!IsInitialized) return;
        try { _plugin!.OnEnchantmentAdded(spellId, durationSeconds); }
        catch (Exception ex) { LogException("OnEnchantmentAdded", ex); }
    }

    public void OnEnchantmentRemoved(uint enchantmentId)
    {
        if (!IsInitialized) return;
        try { _plugin!.OnEnchantmentRemoved(enchantmentId); }
        catch (Exception ex) { LogException("OnEnchantmentRemoved", ex); }
    }

    // ── v77 UI callbacks ─────────────────────────────────────────────────
    // Exports: RynthPluginOnScreenChanged(int, int), RynthPluginOnClientCleanup(),
    // RynthPluginOnTooltipShow(uint, uint), RynthPluginOnTooltipHide(),
    // RynthPluginOnDragStart(uint, uint, uint), RynthPluginOnItemDropped(uint, uint, uint); all cdecl.

    public void OnScreenChanged(int oldMode, int newMode)
    {
        if (!IsInitialized) return;
        try { _plugin!.OnScreenChanged(oldMode, newMode); }
        catch (Exception ex) { LogException("OnScreenChanged", ex); }
    }

    public void OnClientCleanup()
    {
        if (!IsInitialized) return;
        try { _plugin!.OnClientCleanup(); }
        catch (Exception ex) { LogException("OnClientCleanup", ex); }
    }

    public void OnTooltipShow(uint objectId, uint spellId)
    {
        if (!IsInitialized) return;
        try { _plugin!.OnTooltipShow(objectId, spellId); }
        catch (Exception ex) { LogException("OnTooltipShow", ex); }
    }

    public void OnTooltipHide()
    {
        if (!IsInitialized) return;
        try { _plugin!.OnTooltipHide(); }
        catch (Exception ex) { LogException("OnTooltipHide", ex); }
    }

    public void OnDragStart(uint objectId, uint spellId, uint iconId)
    {
        if (!IsInitialized) return;
        try { _plugin!.OnDragStart(objectId, spellId, iconId); }
        catch (Exception ex) { LogException("OnDragStart", ex); }
    }

    public void OnItemDropped(uint objectId, uint spellId, uint targetElementId)
    {
        if (!IsInitialized) return;
        try { _plugin!.OnItemDropped(objectId, spellId, targetElementId); }
        catch (Exception ex) { LogException("OnItemDropped", ex); }
    }

    private void LogException(string method, Exception ex)
    {
        if (Interlocked.Increment(ref _exceptionCount) > MaxLoggedExceptions)
            return;

        try { _plugin?.LogInternal($"[RynthCore] Unhandled exception in {method}: {ex.GetType().Name}: {ex.Message}"); }
        catch { }
    }
}
