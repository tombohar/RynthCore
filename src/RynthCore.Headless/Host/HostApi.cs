// ============================================================================
//  RynthCore.Headless - Host/HostApi.cs
//  The RynthCore plugin API table (RynthCoreApiNative, version 92) for headless
//  plugins. Each area fills its own slots from its own file (HostApi.Objects.cs,
//  HostApi.Player.cs, HostApi.Actions.cs, ...); a slot left empty reads as "not
//  available" to the SDK's Has* checks, which is the right answer for everything this
//  host has no counterpart for (ImGui, D3D, the window, retail panels, shortcuts).
//
//  Conventions follow the engine (its off-thread behaviour, since plugins never run on
//  AC's thread there either): 1/0 for true/false, out-pointers written only on success,
//  host-owned ANSI strings (HostContext.Ansi), UTF-16 chat text.
//  Every function finds its character through HostContext.Current.
// ============================================================================

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RynthCore.PluginSdk;

namespace RynthCore.Headless.Host;

internal static unsafe partial class HostApi
{
    public const uint ApiVersion = 92;

    // Client hook flags the engine reports (bits 0..16); everything this host implements.
    private const uint AllClientHooks = 0x1FFFF;

    public static RynthCoreApiNative* Build()
    {
        var api = (RynthCoreApiNative*)Marshal.AllocHGlobal(sizeof(RynthCoreApiNative));
        Unsafe.InitBlockUnaligned(api, 0, (uint)sizeof(RynthCoreApiNative));
        api->Version = ApiVersion;
        FillCore(api);
        FillObjects(api);
        FillPlayer(api);
        FillActions(api);
        return api;
    }

    static partial void FillObjects(RynthCoreApiNative* api);
    static partial void FillPlayer(RynthCoreApiNative* api);
    static partial void FillActions(RynthCoreApiNative* api);

    private static HostContext? Ctx => HostContext.Current;

    // ── core ────────────────────────────────────────────────────────────────

    private static void FillCore(RynthCoreApiNative* api)
    {
        api->LogFn = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, void>)&Log;
        api->ProbeClientHooksFn = (IntPtr)(delegate* unmanaged[Cdecl]<void>)&Nop;
        api->GetClientHookFlagsFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint>)&GetClientHookFlags;
        api->GetPlayerIdFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint>)&GetPlayerId;
        api->GetAccountNameFn = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr>)&GetAccountName;
        api->GetWorldNameFn = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr>)&GetWorldName;
        api->GetScreenModeFn = (IntPtr)(delegate* unmanaged[Cdecl]<int*, int>)&GetScreenMode;
        api->GetUiHookFlagsFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint>)&Zero;
        api->SetFpsLimitFn = (IntPtr)(delegate* unmanaged[Cdecl]<int, int, int, void>)&SetFpsLimit;
        api->SetRadarSuppressedFn = (IntPtr)(delegate* unmanaged[Cdecl]<int, void>)&NopInt;
        api->SetPowerbarSuppressedFn = (IntPtr)(delegate* unmanaged[Cdecl]<int, void>)&NopInt;
        api->SetChatSuppressedFn = (IntPtr)(delegate* unmanaged[Cdecl]<int, void>)&NopInt;
        api->IsPortalingFn = (IntPtr)(delegate* unmanaged[Cdecl]<int>)&IsPortaling;
        api->GetServerInfoFn = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, int, int>)&GetServerInfo;
        api->GetEngineStatusJsonFn = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr>)&GetEngineStatusJson;
        api->SendPluginCommandFn = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int>)&SendPluginCommand;
        api->GetPluginSnapshotJsonFn = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)&GetPluginSnapshotJson;
        api->GetPluginInterfaceFn = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, IntPtr>)&GetPluginInterface;
        api->GetPluginExportJsonFn = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)&GetPluginExportJson;
    }

    /// <summary>A named plugin's parameterless RynthPluginGet*Json export (its buffer), as the engine brokers it.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr GetPluginExportJson(IntPtr plugin, IntPtr export)
    {
        try
        {
            HostContext? c = Ctx;
            string? name = plugin != IntPtr.Zero ? Marshal.PtrToStringAnsi(plugin) : null;
            string? exp = export != IntPtr.Zero ? Marshal.PtrToStringAnsi(export) : null;
            if (c?.Plugins == null || name == null || exp == null) return IntPtr.Zero;
            if (!exp.StartsWith("RynthPluginGet", StringComparison.Ordinal) || !exp.EndsWith("Json", StringComparison.Ordinal)) return IntPtr.Zero;
            IntPtr fn = c.Plugins.FindExport(name, exp);
            return fn == IntPtr.Zero ? IntPtr.Zero : ((delegate* unmanaged[Cdecl]<IntPtr>)fn)();
        }
        catch { return IntPtr.Zero; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void Log(IntPtr message)
    {
        string? s = message != IntPtr.Zero ? Marshal.PtrToStringAnsi(message) : null;
        if (s != null) Ctx?.Log("[Plugin] " + s);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void Nop() { }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void NopInt(int _) { }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint Zero() => 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void SetFpsLimit(int a, int b, int c) { }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint GetClientHookFlags() => AllClientHooks;

    /// <summary>The player's id while in the world; 0 otherwise (plugins tear their session down on 0).</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint GetPlayerId()
    {
        HostContext? c = Ctx;
        return c != null && c.Session.State == Session.SessionState.InWorld ? c.World.Player.Id : 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr GetAccountName() => HostContext.Ansi(1, Ctx?.AccountName);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr GetWorldName() => HostContext.Ansi(2, Ctx?.Session.WorldName);

    /// <summary>0x10000008 = in the world (the engine's value), 0 otherwise.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetScreenMode(int* mode)
    {
        if (mode == null) return 0;
        HostContext? c = Ctx;
        *mode = c != null && c.Session.State == Session.SessionState.InWorld ? 0x10000008 : 0;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int IsPortaling() => Ctx?.Portaling == true ? 1 : 0;

    /// <summary>Flags: bit0 Aelrynth, bit1 staging, bit2 world name known (copied UTF-8 into the buffer).</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetServerInfo(byte* name, int capacity)
    {
        HostContext? c = Ctx;
        string world = c?.Session.WorldName ?? "";
        int flags = 0;
        if (world.Length > 0 && HostContext.Utf8(world, name, capacity) >= 0) flags |= 4;
        return flags;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr GetEngineStatusJson()
    {
        HostContext? c = Ctx;
        string json = c == null ? "{}" :
            $"{{\"host\":\"headless\",\"inWorld\":{(c.Session.State == Session.SessionState.InWorld ? "true" : "false")},\"playerId\":{c.World.Player.Id},\"objects\":{c.World.Objects.Count}}}";
        return HostContext.Ansi(3, json);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int SendPluginCommand(IntPtr plugin, IntPtr action, IntPtr value)
    {
        HostContext? c = Ctx;
        string? name = plugin != IntPtr.Zero ? Marshal.PtrToStringAnsi(plugin) : null;
        if (c?.Plugins == null || name == null) return 0;
        IntPtr fn = c.Plugins.FindExport(name, "RynthPluginApplyRemoteCommand");
        if (fn == IntPtr.Zero) return 0;
        ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)fn)(action, value);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr GetPluginSnapshotJson(IntPtr plugin)
    {
        HostContext? c = Ctx;
        string? name = plugin != IntPtr.Zero ? Marshal.PtrToStringAnsi(plugin) : null;
        if (c?.Plugins == null || name == null) return IntPtr.Zero;
        IntPtr fn = c.Plugins.FindExport(name, "RynthPluginGetSnapshotJson");
        return fn == IntPtr.Zero ? IntPtr.Zero : ((delegate* unmanaged[Cdecl]<IntPtr>)fn)();
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr GetPluginInterface(IntPtr plugin, IntPtr iface, uint version)
    {
        HostContext? c = Ctx;
        string? name = plugin != IntPtr.Zero ? Marshal.PtrToStringAnsi(plugin) : null;
        if (c?.Plugins == null || name == null) return IntPtr.Zero;
        IntPtr fn = c.Plugins.FindExport(name, "RynthPluginQueryInterface");
        return fn == IntPtr.Zero ? IntPtr.Zero : ((delegate* unmanaged[Cdecl]<IntPtr, uint, IntPtr>)fn)(iface, version);
    }
}

public sealed partial class HostContext
{
    /// <summary>Between a PlayerTeleport and the LoginComplete we answer it with.</summary>
    public bool Portaling { get; internal set; }
}
