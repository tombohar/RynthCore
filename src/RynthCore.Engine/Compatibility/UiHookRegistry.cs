// ============================================================================
//  RynthCore.Engine - Compatibility/UiHookRegistry.cs
//
//  Status and kill switches for the UI hooks of 2026-10-05 (Chorizite gaps #4
//  and #5): UiFlowHooks (UIFlow::UseNewMode, Client::Cleanup) and UiElementHooks
//  (tooltip start/reset/check, drag start, item drop).
//
//  Every hook has its own switch, two ways:
//    - engine.json "DisabledUiHooks": ["StartTooltip", ...]  - not installed at
//      the next start (EngineSettings.IsUiHookEnabled);
//    - /rc hooks off <name>  - the detour turns into a plain pass-through at
//      once (it still calls AC's original), and the name is saved to
//      engine.json for the next start. /rc hooks on <name> undoes both.
//  /rc hooks (alone) lists every hook: address, how it was found, on/off,
//  how many times it fired and the last thing it saw.
//
//  A hook that fails to resolve or install logs once (HookResolver /
//  TryInstall) and stays out: its slot says why, and nothing else changes.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.Compatibility;

internal sealed class UiHookSlot
{
    public UiHookSlot(string name, string what, int fallbackVa)
    {
        Name = name;
        What = what;
        FallbackVa = fallbackVa;
    }

    /// <summary>The switch name (engine.json DisabledUiHooks, /rc hooks on|off).</summary>
    public string Name { get; }
    /// <summary>The AC function, for people.</summary>
    public string What { get; }
    public int FallbackVa { get; }

    public IntPtr Address;
    /// <summary>"pattern", "fallback", or empty when not installed.</summary>
    public string Source = string.Empty;
    public bool Installed;
    /// <summary>Why it isn't installed ("off in engine.json", "pattern and fallback both failed", ...).</summary>
    public string Reason = "not probed yet";
    public int Fired;
    public string LastNote = string.Empty;

    private int _enabled = 1;
    /// <summary>Runtime switch: false = the detour only calls AC's original.</summary>
    public bool Enabled
    {
        get => Volatile.Read(ref _enabled) != 0;
        set => Volatile.Write(ref _enabled, value ? 1 : 0);
    }

    /// <summary>Installed and switched on: the detour does its work.</summary>
    public bool Live => Installed && Enabled;

    public void Note(string note)
    {
        Interlocked.Increment(ref Fired);
        LastNote = note;
    }

    public string Describe()
    {
        string state = Installed
            ? (Enabled ? "on" : "OFF (pass-through)")
            : "not installed: " + Reason;
        string addr = Installed ? $"0x{Address.ToInt32():X8} via {Source}" : $"(fallback 0x{FallbackVa:X8})";
        string last = LastNote.Length > 0 ? $" last: {LastNote}" : string.Empty;
        return $"{Name} [{What}] {addr} - {state}; fired {Fired}x.{last}";
    }
}

internal static class UiHookRegistry
{
    private static readonly List<UiHookSlot> Slots = new();
    private static readonly object Sync = new();

    public static UiHookSlot Register(string name, string what, int fallbackVa)
    {
        lock (Sync)
        {
            foreach (UiHookSlot s in Slots)
                if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                    return s;
            var slot = new UiHookSlot(name, what, fallbackVa);
            Slots.Add(slot);
            return slot;
        }
    }

    public static UiHookSlot? Find(string name)
    {
        lock (Sync)
        {
            foreach (UiHookSlot s in Slots)
                if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                    return s;
        }
        return null;
    }

    public static List<UiHookSlot> All()
    {
        lock (Sync) return new List<UiHookSlot>(Slots);
    }

    /// <summary>
    /// The engine.json switch, checked before resolving (so a switched-off hook leaves
    /// no HookResolver line): false = leave the hook out; the slot says why.
    /// </summary>
    public static bool SwitchedOn(UiHookSlot slot)
    {
        if (EngineSettings.IsUiHookEnabled(slot.Name))
            return true;
        slot.Reason = "off in engine.json (DisabledUiHooks)";
        slot.Enabled = false;
        RynthLog.Compat($"UiHooks: {slot.Name} switched off in engine.json - not installed.");
        return false;
    }

    /// <summary>
    /// Installs one UI hook from its HookResolver result: the Decal guard, then MinHook
    /// (created, not yet enabled: the caller stores the trampoline, then calls Enable).
    /// Never throws; on any failure the slot says why and the caller leaves the hook out.
    /// <paramref name="trampoline"/> is AC's original.
    /// </summary>
    public static bool TryInstall(UiHookSlot slot, HookResolver.ResolveResult resolved, IntPtr detour, out IntPtr trampoline)
    {
        trampoline = IntPtr.Zero;
        try
        {
            if (!resolved.Success)
            {
                slot.Reason = "pattern and fallback address both failed (see the HookResolver line)";
                return false;
            }

            // A Decal client on its first engine load: a pattern miss there means someone
            // else (Decal, a Decal plugin) already patched the prologue. Don't stack on it.
            // (A hot reload misses the pattern too, on our own stub, and installs as usual.)
            if (resolved.Source == HookResolver.ResolveSource.FallbackVa
                && DecalBridgeHost.Active && EntryPoint.InitCount < 2)
            {
                slot.Reason = "pattern missed in a Decal client (prologue already patched?) - left out";
                RynthLog.Compat($"UiHooks: {slot.Name} - pattern missed with Decal in the process; not hooking over someone else's patch.");
                return false;
            }

            trampoline = Hooking.MinHook.HookCreate(resolved.Address, detour);
            Thread.MemoryBarrier();
            slot.Address = resolved.Address;
            slot.Source = resolved.Source == HookResolver.ResolveSource.PatternScan ? "pattern" : "fallback";
            slot.Installed = true;
            slot.Reason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            trampoline = IntPtr.Zero;
            slot.Installed = false;
            slot.Reason = $"install threw {ex.GetType().Name}: {ex.Message}";
            RynthLog.Compat($"UiHooks: {slot.Name} install threw {ex.GetType().Name}: {ex.Message} - left out.");
            return false;
        }
    }

    /// <summary>Enables a hook TryInstall created (after the caller stored the trampoline).</summary>
    public static bool Enable(UiHookSlot slot)
    {
        try
        {
            Hooking.MinHook.Enable(slot.Address);
            RynthLog.Compat($"UiHooks: {slot.Name} ready @ 0x{slot.Address.ToInt32():X8} ({slot.Source}).");
            return true;
        }
        catch (Exception ex)
        {
            slot.Installed = false;
            slot.Reason = $"enable threw {ex.GetType().Name}: {ex.Message}";
            RynthLog.Compat($"UiHooks: {slot.Name} enable threw {ex.GetType().Name}: {ex.Message} - left out.");
            return false;
        }
    }

    /// <summary>/rc hooks on|off &lt;name&gt;: runtime pass-through plus engine.json for the next start.</summary>
    public static string SetEnabled(string name, bool enabled)
    {
        UiHookSlot? slot = Find(name);
        if (slot == null)
            return $"No UI hook called '{name}'. Names: {string.Join(", ", All().ConvertAll(s => s.Name))}.";
        EngineSettings.SetUiHookEnabled(slot.Name, enabled);
        slot.Enabled = enabled;
        if (enabled && !slot.Installed)
            return $"{slot.Name}: switched on in engine.json; it installs at the next client start.";
        return enabled
            ? $"{slot.Name}: on (now, and at the next start)."
            : $"{slot.Name}: off - passes straight to AC now, and isn't installed at the next start.";
    }

    public static List<string> DescribeLines()
    {
        var lines = new List<string>();
        foreach (UiHookSlot s in All())
            lines.Add(s.Describe());
        lines.Add(UiFlowHooks.DescribeState());
        lines.Add(UiElementHooks.DescribeState());
        return lines;
    }
}
