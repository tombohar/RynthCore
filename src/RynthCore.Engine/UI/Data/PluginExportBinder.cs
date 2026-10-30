// ============================================================================
//  RynthCore.Engine - UI/Data/PluginExportBinder.cs
//  Resolves a plugin's C export for a UiSource. Pump thread only.
//
//  Plugin exports are [UnmanagedCallersOnly] cdecl functions; sources call
//  them through `delegate* unmanaged[Cdecl]<...>` pointers (no delegate
//  marshalling). Resolving on AC's render thread is refused and logged: plugin
//  code on AC's thread is the fail-fast crash class (4b70ef1).
// ============================================================================

using System;
using System.Runtime.InteropServices;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.UI.Data;

internal static class PluginExportBinder
{
    // Managed plugins (CoreCLR engine) answer from their export map; see ManagedPlugins.
    private static IntPtr GetProcAddress(IntPtr hModule, string procName) => Plugins.ManagedPlugins.GetProcAddress(hModule, procName);

    private static int _acThreadRefusals;

    /// <summary>
    /// The export <paramref name="exportName"/> of the first loaded plugin whose
    /// display name contains <paramref name="pluginNamePart"/>, or zero when the
    /// plugin isn't loaded (or doesn't have it).
    /// </summary>
    public static IntPtr Resolve(string pluginNamePart, string exportName)
    {
        if (MainThreadGuard.IsOnMainThread())
        {
            if (_acThreadRefusals++ < 5)
                RynthLog.UI($"PluginExportBinder: refused to bind {exportName} on AC's thread; plugin exports are called from UiDataHub only.");
            return IntPtr.Zero;
        }

        // PluginManager mutates its list on this (pump) thread, so reading it here is race-free.
        foreach (LoadedPlugin plugin in PluginManager.Plugins)
        {
            if (plugin.ModuleHandle == IntPtr.Zero) continue;
            if (!plugin.DisplayName.Contains(pluginNamePart, StringComparison.OrdinalIgnoreCase)) continue;
            return GetProcAddress(plugin.ModuleHandle, exportName);
        }
        return IntPtr.Zero;
    }
}
