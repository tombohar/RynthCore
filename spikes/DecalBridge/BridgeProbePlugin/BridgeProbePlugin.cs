// BridgeProbe - a trivial RynthCore plugin for the Decal-bridge spike. It logs the first
// few of every event it receives, and a count per event every 15 s, through the host log,
// so the engine log shows what reached a plugin in bridge mode.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RynthCore.PluginCore;

namespace RynthCore.Plugin.BridgeProbe;

public sealed class BridgeProbePlugin : RynthPluginBase
{
    internal static readonly IntPtr NamePointer = Marshal.StringToHGlobalAnsi("BridgeProbe");
    internal static readonly IntPtr VersionPointer = Marshal.StringToHGlobalAnsi("0.1.0");

    private int _raLines;
    private int _chat, _chatBar, _create, _delete, _update, _health, _damage, _enchAdd, _enchRem, _kill, _ticks;
    private long _lastSummary;
    private const int FirstN = 5;
    // Every chat line, for reading command output on a test client: BRIDGEPROBE_ALLCHAT=1, or a
    // file "bridgeprobe-allchat.flag" next to the client's dispatch file (read at plugin load).
    private static readonly bool AllChat = Environment.GetEnvironmentVariable("BRIDGEPROBE_ALLCHAT") == "1" || FlagFileExists();

    private static bool FlagFileExists()
    {
        try
        {
            string? dispatch = Environment.GetEnvironmentVariable("RYNTHCORE_DISPATCH_FILE");
            string? dir = string.IsNullOrEmpty(dispatch) ? null : System.IO.Path.GetDirectoryName(dispatch);
            return dir != null && System.IO.File.Exists(System.IO.Path.Combine(dir, "bridgeprobe-allchat.flag"));
        }
        catch { return false; }
    }

    public override int Initialize()
    {
        Log("BridgeProbe: initialized.");
        return 0;
    }

    public override void OnLoginComplete() => Log("BridgeProbe: OnLoginComplete");

    public override void OnChatWindowText(string? text, int chatType, ref int eat)
    {
        // BRIDGEPROBE_ALLCHAT=1: log every chat line (reading command output on a test client).
        if (_chat++ < FirstN || AllChat) Log($"BridgeProbe: OnChatWindowText type={chatType} '{text?.TrimEnd('\n')}'");
        // RynthAi's answers to the harness's "/ra cache": its object cache, as seen in chat.
        else if (text != null && text.Contains("[RynthAi]") && _raLines++ < 30)
            Log($"BridgeProbe: RynthAi said: '{text.TrimEnd('\n')}'");
    }

    public override void OnChatBarEnter(string? text, ref int eat)
    {
        if (_chatBar++ < FirstN) Log($"BridgeProbe: OnChatBarEnter '{text}'");
    }

    public override void OnCreateObject(uint objectId)
    {
        if (_create++ < FirstN) Log($"BridgeProbe: OnCreateObject 0x{objectId:X8}");
    }

    public override void OnDeleteObject(uint objectId)
    {
        if (_delete++ < FirstN) Log($"BridgeProbe: OnDeleteObject 0x{objectId:X8}");
    }

    public override void OnUpdateObject(uint objectId) => _update++;

    public override void OnUpdateHealth(uint targetId, float healthRatio, uint currentHealth, uint maxHealth)
    {
        if (_health++ < FirstN) Log($"BridgeProbe: OnUpdateHealth 0x{targetId:X8} ratio={healthRatio:0.00} {currentHealth}/{maxHealth}");
    }

    public override void OnCombatDamage(uint damage, uint damageType, bool crit, bool isAttacker)
    {
        if (_damage++ < FirstN) Log($"BridgeProbe: OnCombatDamage {damage} type={damageType} crit={crit} attacker={isAttacker}");
    }

    public override void OnKillNotification(string? deathMessage)
    {
        if (_kill++ < FirstN) Log($"BridgeProbe: OnKillNotification '{deathMessage}'");
    }

    public override void OnEnchantmentAdded(uint spellId, double durationSeconds)
    {
        if (_enchAdd++ < FirstN) Log($"BridgeProbe: OnEnchantmentAdded spell={spellId} dur={durationSeconds:0}");
    }

    public override void OnEnchantmentRemoved(uint enchantmentId)
    {
        if (_enchRem++ < FirstN) Log($"BridgeProbe: OnEnchantmentRemoved {enchantmentId}");
    }

    public override void OnTick()
    {
        _ticks++;
        long now = Environment.TickCount64;
        if (now - _lastSummary < 15000) return;
        _lastSummary = now;
        Log($"BridgeProbe: counts ticks={_ticks} chat={_chat} chatBar={_chatBar} create={_create} delete={_delete} update={_update} " +
            $"health={_health} damage={_damage} kill={_kill} enchAdd={_enchAdd} enchRem={_enchRem}");
    }
}

public static unsafe class PluginExports
{
    private static readonly RynthPluginRuntime<BridgeProbePlugin> Runtime = new();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginInit", CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Init(RynthCore.PluginSdk.RynthCoreApiNative* api) => Runtime.Init(api);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginShutdown", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Shutdown() => Runtime.Shutdown();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginName", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetName() => BridgeProbePlugin.NamePointer;

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginVersion", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetVersion() => BridgeProbePlugin.VersionPointer;

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginTick", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Tick() => Runtime.OnTick();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnLoginComplete", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnLoginComplete() => Runtime.OnLoginComplete();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnChatWindowText", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnChatWindowText(IntPtr textUtf16, int chatType, IntPtr eatFlag) => Runtime.OnChatWindowText(textUtf16, chatType, eatFlag);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnChatBarEnter", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnChatBarEnter(IntPtr textUtf16, IntPtr eatFlag) => Runtime.OnChatBarEnter(textUtf16, eatFlag);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnCreateObject", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnCreateObject(uint objectId) => Runtime.OnCreateObject(objectId);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnDeleteObject", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnDeleteObject(uint objectId) => Runtime.OnDeleteObject(objectId);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnUpdateObject", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnUpdateObject(uint objectId) => Runtime.OnUpdateObject(objectId);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnUpdateHealth", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnUpdateHealth(uint targetId, float healthRatio, uint currentHealth, uint maxHealth)
        => Runtime.OnUpdateHealth(targetId, healthRatio, currentHealth, maxHealth);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnCombatDamage", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnCombatDamage(uint damage, uint damageType, uint crit, uint isAttacker) => Runtime.OnCombatDamage(damage, damageType, crit, isAttacker);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnKillNotification", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnKillNotification(IntPtr textUtf16) => Runtime.OnKillNotification(textUtf16);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnEnchantmentAdded", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnEnchantmentAdded(uint spellId, double durationSeconds) => Runtime.OnEnchantmentAdded(spellId, durationSeconds);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnEnchantmentRemoved", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnEnchantmentRemoved(uint enchantmentId) => Runtime.OnEnchantmentRemoved(enchantmentId);
}
