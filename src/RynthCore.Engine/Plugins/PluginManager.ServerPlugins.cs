// ============================================================================
//  RynthCore.Engine - Plugins/PluginManager.ServerPlugins.cs
//  Server plugins the connect host left out (PluginManifestGate.SelectServerPlugins):
//  kept with their manifests so Options > Diagnostics lists their servers, and
//  loaded one at a time when a server is forced On (the Diagnostics picker,
//  "/rc server <id> on", or a character's saved override), without a rescan.
//
//  Threads: the list is published as an immutable array (any thread reads it);
//  a load request is queued from any thread and served on the pump thread
//  (ProcessPendingActions), where every other plugin load happens.
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using RynthCore.Engine.Compatibility;
using RynthCore.PluginSdk.Manifest;

namespace RynthCore.Engine.Plugins;

/// <summary>An installed server plugin that is not loaded because the client connects to another server.</summary>
internal sealed record SkippedServerPlugin(string Path, RynthPluginManifest Manifest)
{
    public string ServerId => Manifest.Server!.Id;
    public string ServerName => Manifest.Server!.Name;
    public string PluginName => Manifest.Name;
}

internal static partial class PluginManager
{
    private static SkippedServerPlugin[] _skippedServerPlugins = Array.Empty<SkippedServerPlugin>();
    private static readonly ConcurrentQueue<string> _serverPluginLoadRequests = new();

    /// <summary>Installed server plugins not loaded for this connect host. Any thread.</summary>
    public static IReadOnlyList<SkippedServerPlugin> SkippedServerPlugins => System.Threading.Volatile.Read(ref _skippedServerPlugins);

    /// <summary>The skipped server plugin for <paramref name="serverId"/>, or null. Any thread.</summary>
    public static SkippedServerPlugin? FindSkippedServerPlugin(string serverId)
    {
        foreach (SkippedServerPlugin s in SkippedServerPlugins)
            if (s.ServerId.Equals(serverId, StringComparison.OrdinalIgnoreCase)) return s;
        return null;
    }

    /// <summary>
    /// Loads the plugin for <paramref name="serverId"/> on the next pump tick, if the connect host
    /// left it out. Nothing happens for a server whose plugin is loaded already. Any thread.
    /// </summary>
    public static void RequestServerPluginLoad(string serverId)
    {
        if (string.IsNullOrWhiteSpace(serverId) || FindSkippedServerPlugin(serverId) == null) return;
        _serverPluginLoadRequests.Enqueue(serverId.Trim());
    }

    /// <summary>LoadPluginsFromDisk: the gate's skipped server plugins (replaces the list). Pump thread.</summary>
    private static void RememberSkippedServerPlugins(List<(string Path, RynthPluginManifest Manifest)> skipped)
    {
        var list = new List<SkippedServerPlugin>(skipped.Count);
        foreach ((string path, RynthPluginManifest manifest) in skipped)
            if (manifest.Server != null) list.Add(new SkippedServerPlugin(path, manifest));
        System.Threading.Volatile.Write(ref _skippedServerPlugins, list.ToArray());
    }

    /// <summary>Serves the queued requests (ProcessPendingActions). Pump thread.</summary>
    private static void LoadRequestedServerPlugins()
    {
        while (_serverPluginLoadRequests.TryDequeue(out string? serverId))
        {
            SkippedServerPlugin? s = FindSkippedServerPlugin(serverId);
            if (s == null) continue;   // loaded by an earlier request, or a rescan changed the list
            SkippedServerPlugin[] rest = Array.FindAll(System.Threading.Volatile.Read(ref _skippedServerPlugins), x => !ReferenceEquals(x, s));
            System.Threading.Volatile.Write(ref _skippedServerPlugins, rest);
            LoadSkippedServerPlugin(s);
        }
    }

    /// <summary>Loads, starts and catches up one server plugin, as if it had loaded with the others. Pump thread.</summary>
    private static void LoadSkippedServerPlugin(SkippedServerPlugin s)
    {
        string file = Path.GetFileName(s.Path);
        if (FindPlugin(s.PluginName) != null) return;
        if (!File.Exists(s.Path))
        {
            RynthLog.Warn($"PluginManager: {file} for {s.ServerName} is gone from disk - not loaded.");
            return;
        }

        // The checks the gate skipped for it: engine API and required plugins.
        PluginResolution res = PluginManifestResolver.Resolve(
            new[] { new PluginCandidate(s.Path, s.Manifest, RawFileVersion(s.Path)) }, PluginContractVersion.Current);
        foreach (PluginRefusal refusal in res.Refused)
        {
            RefusePlugin(file, refusal.Reason);
            return;
        }

        RynthLog.Plugin($"PluginManager: {s.ServerName} ({s.ServerId}) is this server (its world name, or forced On) - loading {file} (va before: {Compatibility.AddressSpace.Describe()})");
        LoadedPlugin? plugin = PluginLoader.LoadSingle(s.Path, _shadowRootDir, _loadGeneration);
        RynthLog.Plugin($"PluginManager: loaded {file}{(plugin == null ? " (FAILED)" : "")} (va after: {Compatibility.AddressSpace.Describe()})");
        if (plugin == null)
        {
            AcMainThreadQueue.EnqueueWriteToChat($"[RynthCore] {s.PluginName} for {s.ServerName} failed to load - see the RynthCore log.", 1);
            return;
        }

        plugin.Manifest = s.Manifest;
        _plugins.Add(plugin);
        PublishPluginsRenderSnapshot();
        EnsureHostCallbacks();
        InitializePlugin(plugin);
        DispatchLifecycleToLatePlugin(plugin);
        UI.Sections.SectionRegistry.RefreshManifests();

        if (plugin.Initialized && !plugin.Failed)
            AcMainThreadQueue.EnqueueWriteToChat($"[RynthCore] Loaded {s.PluginName} for {s.ServerName} (its world name, or picked in Options > Diagnostics).", 1);
    }

    /// <summary>
    /// OnUIInitialized and OnLoginComplete for one plugin started after the others, when the engine
    /// has seen them. Unlike the full dispatchers it leaves the other plugins and the login-time
    /// engine work (self-identify, combat mode and target sync) alone. Pump thread.
    /// </summary>
    private static void DispatchLifecycleToLatePlugin(LoadedPlugin plugin)
    {
        if (!plugin.Initialized || plugin.Failed) return;
        LoadedPlugin? outer = EnterDispatch(plugin);
        try
        {
            if (_uiInitializedObserved && plugin.OnUIInitialized != null && !plugin.UIInitializedDispatched)
            {
                plugin.OnUIInitialized();
                plugin.UIInitializedDispatched = true;
                RynthLog.Plugin($"PluginManager: {plugin.DisplayName} received OnUIInitialized (late load).");
            }
            if (_loginCompleteObserved && plugin.OnLoginComplete != null && !plugin.LoginCompleteDispatched)
            {
                plugin.OnLoginComplete();
                plugin.LoginCompleteDispatched = true;
                RynthLog.Plugin($"PluginManager: {plugin.DisplayName} received OnLoginComplete (late load).");
            }
        }
        catch (Exception ex)
        {
            plugin.Failed = true;
            RynthLog.Plugin($"PluginManager: {plugin.DisplayName} lifecycle (late load) threw {ex.GetType().Name}: {ex.Message}{PluginTrace(ex)}");
        }
        finally
        {
            LeaveDispatch(outer);
        }
    }
}