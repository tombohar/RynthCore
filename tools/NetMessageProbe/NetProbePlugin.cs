// NetProbe - a test plugin for the v78 server-message stream (see the csproj). It asks for
// every message the SDK parses, parses what arrives and logs the first few of each kind,
// plus a count every 30 s, through the host log ("NetProbe:" lines in the RynthCore log).
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RynthCore.PluginCore;
using RynthCore.PluginSdk.Net;

namespace RynthCore.Plugin.NetProbe;

public sealed class NetProbePlugin : RynthPluginBase
{
    internal static readonly IntPtr NamePointer = Marshal.StringToHGlobalAnsi("NetProbe");
    internal static readonly IntPtr VersionPointer = Marshal.StringToHGlobalAnsi("0.1.0");
    internal static NetProbePlugin? Instance;

    private const int FirstN = 5;
    private readonly Dictionary<string, (int Ok, int Failed, int Leftover)> _kinds = new();
    private long _messages, _lastSummary;

    private static readonly uint[] Opcodes =
    {
        ServerOpcode.HearSpeech, ServerOpcode.HearRangedSpeech, ServerOpcode.HearEmote, ServerOpcode.HearSoulEmote,
    };

    private static readonly uint[] GameEvents =
    {
        GameEventType.PopUpString, GameEventType.FriendsUpdate, GameEventType.FellowshipQuit, GameEventType.FellowshipDismiss,
        GameEventType.ChannelBroadcast, GameEventType.ConfirmationRequest, GameEventType.AllegianceLoginNotification,
        GameEventType.HearDirectSpeech, GameEventType.FellowshipFullUpdate, GameEventType.FellowshipDisband,
        GameEventType.FellowshipUpdateFellow, GameEventType.TransientString,
    };

    public override int Initialize()
    {
        Instance = this;
        if (!Host.HasServerMessages)
        {
            Log($"NetProbe: engine API v{Host.Version} has no server messages (needs v78).");
            return 0;
        }
        int r = Host.SetServerMessageInterest(Opcodes, GameEvents);
        Log($"NetProbe: SetServerMessageInterest -> {r} (1 streaming, 2 stream off, 0 no export, -1 unknown caller).");
        return 0;
    }

    public override void Shutdown() => Instance = null;

    internal unsafe void OnServerMessage(uint opcode, byte* data, int length)
    {
        _messages++;
        NetMessageInfo info = NetMessages.Inspect(ServerMessage.FromCallback(opcode, data, length));
        string kind = info.Known ? info.Kind : $"unasked {info.Kind}";
        var k = _kinds.TryGetValue(kind, out var v) ? v : default;
        int seen = k.Ok + k.Failed;
        _kinds[kind] = (k.Ok + (info.Parsed ? 1 : 0), k.Failed + (info.Parsed ? 0 : 1), k.Leftover + (info.Parsed && info.Leftover != 0 ? 1 : 0));
        if (seen < FirstN || !info.Parsed || info.Leftover != 0)
            Log($"NetProbe: {kind} len={length} {(info.Parsed ? "ok" : "FAILED")}{(info.Leftover != 0 ? $" leftover={info.Leftover}" : "")} {info.Summary}");
    }

    public override void OnTick()
    {
        long now = Environment.TickCount64;
        if (now - _lastSummary < 30000) return;
        _lastSummary = now;
        if (_messages == 0) return;
        var parts = new List<string>();
        foreach (var (kind, c) in _kinds)
            parts.Add($"{kind} {c.Ok}" + (c.Failed > 0 ? $"/{c.Failed} failed" : "") + (c.Leftover > 0 ? $"/{c.Leftover} leftover" : ""));
        Log($"NetProbe: {_messages} messages: {string.Join(", ", parts)}");
    }
}

public static unsafe class PluginExports
{
    private static readonly RynthPluginRuntime<NetProbePlugin> Runtime = new();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginInit", CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Init(RynthCore.PluginSdk.RynthCoreApiNative* api) => Runtime.Init(api);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginShutdown", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Shutdown() => Runtime.Shutdown();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginName", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetName() => NetProbePlugin.NamePointer;

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginVersion", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetVersion() => NetProbePlugin.VersionPointer;

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginTick", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Tick() => Runtime.OnTick();

    /// <summary>v78: data = the message after its opcode, valid only during the call.</summary>
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnServerMessage", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnServerMessage(uint opcode, byte* data, int length)
    {
        try { NetProbePlugin.Instance?.OnServerMessage(opcode, data, length); }
        catch { }   // never let an exception cross the native boundary
    }
}
