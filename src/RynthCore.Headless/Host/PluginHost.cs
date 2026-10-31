// ============================================================================
//  RynthCore.Headless - Host/PluginHost.cs
//  Loads managed RynthCore plugins for one character and drives them the way the
//  engine's plugin pump does: Init with the API table, OnLoginComplete once the player
//  is in the world (with OnCreateObject for everything already known), then every host
//  tick: drain the queued events in the engine's order, then Tick. Every call runs with
//  this character's HostContext current. A plugin whose call throws is marked failed
//  and gets nothing more but Shutdown (as in the engine).
//
//  Plugins must be managed builds for this process's bitness (x64 here: built with
//  -p:PlatformTarget=x64 -p:RuntimeIdentifier=win-x64 -p:PublishAot=false).
// ============================================================================

using System.Reflection;
using System.Runtime.InteropServices;
using RynthCore.PluginSdk;

namespace RynthCore.Headless.Host;

public sealed unsafe class PluginHost : IDisposable
{
    public sealed class Plugin
    {
        public required string Path;
        public required string Name;
        public required Dictionary<string, IntPtr> Exports;
        internal PluginLoadContext? Context;
        public bool Initialized, Failed, LoginDelivered;
        public string? FailReason;

        public IntPtr Export(string name) => Exports.TryGetValue(name, out IntPtr p) ? p : IntPtr.Zero;
    }

    private readonly HostContext _ctx;
    private readonly List<Plugin> _plugins = new();
    private RynthCoreApiNative* _api;
    private bool _loginComplete;

    public IReadOnlyList<Plugin> Plugins => _plugins;
    public HostContext Context => _ctx;

    public PluginHost(HostContext ctx)
    {
        _ctx = ctx;
        _ctx.Plugins = this;
        AcclientShims.Install();
    }

    // ── loading ─────────────────────────────────────────────────────────────

    /// <summary>Loads a managed plugin DLL and initializes it. False (with the reason logged) on failure.</summary>
    public bool Load(string path, string? sharedDir = null)
    {
        path = System.IO.Path.GetFullPath(path);
        var alc = new PluginLoadContext("plugin " + System.IO.Path.GetFileName(path), System.IO.Path.GetDirectoryName(path)!, sharedDir);
        Assembly asm;
        var exports = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
        try
        {
            asm = alc.LoadPlugin(path);
            foreach (Type t in asm.GetTypes())
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    var uca = m.GetCustomAttribute<UnmanagedCallersOnlyAttribute>();
                    if (uca?.EntryPoint is { Length: > 0 } entry)
                        exports[entry] = m.MethodHandle.GetFunctionPointer();
                }
        }
        catch (Exception ex)
        {
            _ctx.Log($"[host] can't load {path}: {ex.GetType().Name}: {ex.Message}");
            alc.Unload();
            return false;
        }

        var p = new Plugin { Path = path, Name = System.IO.Path.GetFileNameWithoutExtension(path), Exports = exports, Context = alc };
        if (p.Export("RynthPluginInit") == IntPtr.Zero || p.Export("RynthPluginShutdown") == IntPtr.Zero)
        {
            _ctx.Log($"[host] {p.Name}: no RynthPluginInit/Shutdown export; skipped");
            alc.Unload();
            return false;
        }
        IntPtr nameFn = p.Export("RynthPluginName");
        if (nameFn != IntPtr.Zero)
            Call(p, () =>
            {
                IntPtr s = ((delegate* unmanaged[Cdecl]<IntPtr>)nameFn)();
                if (s != IntPtr.Zero) p.Name = Marshal.PtrToStringAnsi(s) ?? p.Name;
            });

        if (_api == null) _api = HostApi.Build();
        int rc = -1;
        Call(p, () => rc = ((delegate* unmanaged[Cdecl]<RynthCoreApiNative*, int>)p.Export("RynthPluginInit"))(_api));
        if (rc != 0)
        {
            p.Failed = true;
            p.FailReason ??= $"Init returned {rc}";
            _ctx.Log($"[host] {p.Name}: {p.FailReason}");
            _plugins.Add(p);
            return false;
        }
        p.Initialized = true;
        _plugins.Add(p);
        _ctx.Log($"[host] loaded {p.Name} ({exports.Count} exports)");
        Call(p, "RynthPluginOnUIInitialized");
        if (_loginComplete) DeliverLogin(p);
        return true;
    }

    // ── driving ─────────────────────────────────────────────────────────────

    /// <summary>The player is in the world: OnLoginComplete (once per login) with the objects already known.</summary>
    public void LoginComplete()
    {
        if (_loginComplete) return;
        _loginComplete = true;
        foreach (Plugin p in Live()) DeliverLogin(p);
    }

    private void DeliverLogin(Plugin p)
    {
        if (p.LoginDelivered) return;
        p.LoginDelivered = true;
        // Objects created before the plugin could hear about them (the engine's replay).
        foreach (uint id in _ctx.World.Objects.Keys.ToList())
            Call(p, "RynthPluginOnCreateObject", id);
        Call(p, "RynthPluginOnLoginComplete");
        int mode = _ctx.CurrentCombatMode;
        CallCombatMode(p, mode, 1);
    }

    public void Logout()
    {
        if (!_loginComplete) return;
        _loginComplete = false;
        foreach (Plugin p in Live())
        {
            p.LoginDelivered = false;
            Call(p, "RynthPluginOnLogout");
        }
    }

    /// <summary>One host frame: deliver queued events, then Tick every plugin.</summary>
    public void Tick()
    {
        if (!_loginComplete) { _ctx.Events.Drain(); return; }
        foreach (PluginEvent e in _ctx.Events.Drain())
            foreach (Plugin p in Live())
                Deliver(p, e);
        foreach (Plugin p in Live())
            Call(p, "RynthPluginTick");
    }

    /// <summary>
    /// A typed or invoked chat line, offered to every plugin's OnChatBarEnter (shared eat flag).
    /// True when a plugin consumed it.
    /// </summary>
    public bool ChatBarEnter(string text)
    {
        int* eat = (int*)NativeMemory.AllocZeroed(sizeof(int));
        IntPtr s = Marshal.StringToHGlobalUni(text);
        try
        {
            foreach (Plugin p in Live())
            {
                IntPtr fn = p.Export("RynthPluginOnChatBarEnter");
                if (fn == IntPtr.Zero) continue;
                Call(p, () => ((delegate* unmanaged[Cdecl]<IntPtr, int*, void>)fn)(s, eat));
                if (*eat != 0) return true;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(s);
            NativeMemory.Free(eat);
        }
        return false;
    }

    /// <summary>A plugin's export by name (first live plugin whose name matches), for brokers and bridges.</summary>
    public IntPtr FindExport(string pluginName, string export)
    {
        Plugin? p = Live().FirstOrDefault(x => string.Equals(x.Name, pluginName, StringComparison.OrdinalIgnoreCase))
                    ?? Live().FirstOrDefault(x => x.Name.Contains(pluginName, StringComparison.OrdinalIgnoreCase));
        return p?.Export(export) ?? IntPtr.Zero;
    }

    private void Deliver(Plugin p, PluginEvent e)
    {
        switch (e)
        {
            case PluginEvent.BusyIncremented: Call(p, "RynthPluginOnBusyCountIncremented"); break;
            case PluginEvent.BusyDecremented: Call(p, "RynthPluginOnBusyCountDecremented"); break;
            case PluginEvent.CombatModeChange c: CallCombatMode(p, c.Current, c.Previous); break;
            case PluginEvent.ViewContents v: Call(p, "RynthPluginOnViewObjectContents", v.Container); break;
            case PluginEvent.StopViewingContents v: Call(p, "RynthPluginOnStopViewingObjectContents", v.Container); break;
            case PluginEvent.VendorOpen v: Call(p, "RynthPluginOnVendorOpen", v.Vendor); break;
            case PluginEvent.VendorClose v: Call(p, "RynthPluginOnVendorClose", v.Vendor); break;
            case PluginEvent.UpdateObjectInventory u: Call(p, "RynthPluginOnUpdateObjectInventory", u.Container); break;
            case PluginEvent.CreateObject c: Call(p, "RynthPluginOnCreateObject", c.Id); break;
            case PluginEvent.UpdateObject u: Call(p, "RynthPluginOnUpdateObject", u.Id); break;
            case PluginEvent.DeleteObject d: Call(p, "RynthPluginOnDeleteObject", d.Id); break;
            case PluginEvent.SelectedTargetChange s:
            {
                IntPtr fn = p.Export("RynthPluginOnSelectedTargetChange");
                if (fn != IntPtr.Zero) Call(p, () => ((delegate* unmanaged[Cdecl]<uint, uint, void>)fn)(s.Current, s.Previous));
                break;
            }
            case PluginEvent.UpdateHealth h:
            {
                IntPtr fn = p.Export("RynthPluginOnUpdateHealth");
                if (fn != IntPtr.Zero) Call(p, () => ((delegate* unmanaged[Cdecl]<uint, float, uint, uint, void>)fn)(h.Target, h.Ratio, h.CurrentHealth, h.MaxHealth));
                break;
            }
            case PluginEvent.CombatDamage d:
            {
                IntPtr fn = p.Export("RynthPluginOnCombatDamage");
                if (fn != IntPtr.Zero) Call(p, () => ((delegate* unmanaged[Cdecl]<uint, uint, uint, uint, void>)fn)(d.Damage, d.DamageType, d.Critical ? 1u : 0u, d.IsAttacker ? 1u : 0u));
                break;
            }
            case PluginEvent.KillNotification k: CallText(p, "RynthPluginOnKillNotification", k.Text); break;
            case PluginEvent.EnchantmentAdded a:
            {
                IntPtr fn = p.Export("RynthPluginOnEnchantmentAdded");
                if (fn != IntPtr.Zero) Call(p, () => ((delegate* unmanaged[Cdecl]<uint, double, void>)fn)(a.SpellId, a.Duration));
                break;
            }
            case PluginEvent.EnchantmentRemoved r: Call(p, "RynthPluginOnEnchantmentRemoved", r.SpellId); break;
            case PluginEvent.ChatWindowText c:
            {
                IntPtr fn = p.Export("RynthPluginOnChatWindowText");
                if (fn == IntPtr.Zero) break;
                IntPtr s = Marshal.StringToHGlobalUni(c.Text);
                int* eat = (int*)NativeMemory.AllocZeroed(sizeof(int));
                try
                {
                    Call(p, () => ((delegate* unmanaged[Cdecl]<IntPtr, int, int*, void>)fn)(s, c.ChatType, eat));
                }
                finally
                {
                    Marshal.FreeHGlobal(s);
                    NativeMemory.Free(eat);
                }
                break;
            }
        }
    }

    private void CallCombatMode(Plugin p, int cur, int prev)
    {
        IntPtr fn = p.Export("RynthPluginOnCombatModeChange");
        if (fn != IntPtr.Zero) Call(p, () => ((delegate* unmanaged[Cdecl]<int, int, void>)fn)(cur, prev));
    }

    private void CallText(Plugin p, string export, string text)
    {
        IntPtr fn = p.Export(export);
        if (fn == IntPtr.Zero) return;
        IntPtr s = Marshal.StringToHGlobalUni(text);
        try { Call(p, () => ((delegate* unmanaged[Cdecl]<IntPtr, void>)fn)(s)); }
        finally { Marshal.FreeHGlobal(s); }
    }

    private void Call(Plugin p, string export)
    {
        IntPtr fn = p.Export(export);
        if (fn != IntPtr.Zero) Call(p, () => ((delegate* unmanaged[Cdecl]<void>)fn)());
    }

    private void Call(Plugin p, string export, uint arg)
    {
        IntPtr fn = p.Export(export);
        if (fn != IntPtr.Zero) Call(p, () => ((delegate* unmanaged[Cdecl]<uint, void>)fn)(arg));
    }

    private void Call(Plugin p, Action call)
    {
        using HostContext.Scope _ = _ctx.Enter();
        try
        {
            call();
        }
        catch (Exception ex)
        {
            p.Failed = true;
            p.FailReason = $"{ex.GetType().Name}: {ex.Message}";
            _ctx.Log($"[host] {p.Name} failed: {p.FailReason}");
        }
    }

    private IEnumerable<Plugin> Live() => _plugins.Where(p => p.Initialized && !p.Failed).ToList();

    public void Dispose()
    {
        foreach (Plugin p in _plugins.AsEnumerable().Reverse().Where(p => p.Initialized).ToList())
        {
            Call(p, "RynthPluginShutdown");
            p.Initialized = false;
            p.Context?.Unload();
        }
        _plugins.Clear();
        if (_api != null)
        {
            Marshal.FreeHGlobal((IntPtr)_api);
            _api = null;
        }
    }
}

public sealed partial class HostContext
{
    /// <summary>The plugin host driving this context's plugins (set by PluginHost).</summary>
    public PluginHost? Plugins { get; internal set; }

    /// <summary>The player's combat mode (PropertyInt 40), NonCombat (1) when unknown.</summary>
    public int CurrentCombatMode => World.PlayerObject is { } me && me.TryGetInt(40, out int m) && m != 0 ? m : 1;
}

/// <summary>
/// Stand-ins for the few fixed acclient addresses plugins read directly. RynthAi's meta
/// expressions read the game clock at 0x008379A8 without probing (getgameyear, ...); in this
/// process that address is unmapped, so a zeroed page is committed there (if free).
/// </summary>
internal static class AcclientShims
{
    private static bool _done;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint type, uint protect);

    public static void Install()
    {
        if (_done || !OperatingSystem.IsWindows()) return;
        _done = true;
        const uint MemCommitReserve = 0x3000, PageReadWrite = 0x04;
        VirtualAlloc((IntPtr)0x00837000, (UIntPtr)0x1000, MemCommitReserve, PageReadWrite);
    }
}
