// ============================================================================
//  RynthCore.Engine - Plugins/ManagedPlugins.cs
//
//  Managed plugins (docs/UNLOADABLE_ENGINE_PLAN.md, Phase 4). Under the CoreCLR engine
//  a plugin can be a plain managed assembly instead of a NativeAOT DLL: the same
//  source, built with -p:PublishAot=false. It keeps its C ABI: the
//  [UnmanagedCallersOnly(EntryPoint = "RynthPluginInit")] etc. methods the NativeAOT
//  build exports are found by reflection instead of GetProcAddress, so PluginLoader,
//  PluginManager and the panels' export lookups work unchanged through a stand-in
//  module handle.
//
//  Each plugin loads into its own collectible AssemblyLoadContext (its dependencies,
//  e.g. RynthCore.PluginSdk or MoonSharp, from its own folder), unloaded after its
//  RynthPluginShutdown. Unlike a NativeAOT plugin (its own runtime, never freed) it
//  shares the engine's runtime and GC and goes away with a reload.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
#if ENGINE_CORECLR
using System.Runtime.Loader;
#endif

namespace RynthCore.Engine.Plugins;

internal static class ManagedPlugins
{
    // Stand-in handles: real module bases are 64 KB aligned, these never are.
    private static int _nextHandle = 0x7F000001;
    private static readonly object Sync = new();
    private static readonly Dictionary<IntPtr, Entry> Loaded = new();

    private sealed class Entry
    {
        public required string Name;
        public required Dictionary<string, IntPtr> Exports;
#if ENGINE_CORECLR
        public required PluginLoadContext Context;
#endif
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true, ExactSpelling = true, EntryPoint = "GetProcAddress")]
    private static extern IntPtr Kernel32GetProcAddress(IntPtr hModule, string lpProcName);

    /// <summary>GetProcAddress for plugin handles: a managed plugin's export map, else kernel32.</summary>
    public static IntPtr GetProcAddress(IntPtr handle, string name)
    {
        lock (Sync)
        {
            if (Loaded.TryGetValue(handle, out Entry? e))
                return e.Exports.TryGetValue(name, out IntPtr fn) ? fn : IntPtr.Zero;
        }
        return Kernel32GetProcAddress(handle, name);
    }

    public static bool IsManagedHandle(IntPtr handle)
    {
        lock (Sync) return Loaded.ContainsKey(handle);
    }

    /// <summary>True when <paramref name="path"/> is a .NET assembly (PE with a CLR header).</summary>
    public static bool IsManagedAssembly(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var pe = new System.Reflection.PortableExecutable.PEReader(fs);
            return pe.HasMetadata && pe.PEHeaders.CorHeader != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Loads a managed plugin; returns its stand-in module handle, or zero.</summary>
    public static IntPtr Load(string path)
    {
        string fileName = Path.GetFileName(path);
#if ENGINE_CORECLR
        try
        {
            var context = new PluginLoadContext(fileName, Path.GetDirectoryName(path)!);
            Assembly assembly = context.LoadFromFile(path);
            var exports = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
            foreach (Type type in assembly.GetTypes())
            {
                foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (m.GetCustomAttribute<UnmanagedCallersOnlyAttribute>()?.EntryPoint is { Length: > 0 } entry)
                        exports[entry] = m.MethodHandle.GetFunctionPointer();
                }
            }
            IntPtr handle;
            lock (Sync)
            {
                handle = (IntPtr)_nextHandle;
                _nextHandle += 2;
                Loaded[handle] = new Entry { Name = fileName, Exports = exports, Context = context };
            }
            RynthLog.Plugin($"ManagedPlugins: {fileName} loaded into its own collectible context ({exports.Count} exports).");
            return handle;
        }
        catch (Exception ex)
        {
            RynthLog.Plugin($"ManagedPlugins: FAILED to load {fileName}: {ex}");
            return IntPtr.Zero;
        }
#else
        RynthLog.Plugin($"ManagedPlugins: {fileName} is a managed plugin; it needs the CoreCLR engine (this is the NativeAOT one). Skipping.");
        return IntPtr.Zero;
#endif
    }

    private static readonly List<Entry> PendingUnload = new();

    /// <summary>
    /// Retires a managed plugin (after its RynthPluginShutdown). Its context is only
    /// unloaded by <see cref="UnloadRetired"/> at the very end of the engine's shutdown:
    /// until then the plugin pump, the pop-outs' last frames and cached export pointers
    /// can still call into it, and once a context is unloaded any GC may free its code
    /// (2026-09-29: RynthAi's shutdown GC freed the others mid-teardown -> runtime abort).
    /// </summary>
    public static void Unload(IntPtr handle)
    {
        lock (Sync)
        {
            // Stays in Loaded (its exports still resolve) until UnloadRetired.
            if (Loaded.TryGetValue(handle, out Entry? e) && !PendingUnload.Contains(e))
                PendingUnload.Add(e);
        }
    }

    // How long the unload waits for the plugin contexts to be collected before naming the ones still held.
    private const long UnloadCheckMs = 1500;

    /// <summary>Unloads every retired plugin's context. Last step of EngineLifecycle.Shutdown.</summary>
    public static void UnloadRetired()
    {
        List<Entry> retired;
        lock (Sync)
        {
            retired = new List<Entry>(PendingUnload);
            PendingUnload.Clear();
            foreach (var kv in new List<KeyValuePair<IntPtr, Entry>>(Loaded))
                if (retired.Contains(kv.Value)) Loaded.Remove(kv.Key);
        }
#if ENGINE_CORECLR
        string vaBefore = retired.Count > 0 ? Compatibility.AddressSpace.Describe() : "";
        var refs = new List<(string Name, WeakReference Context)>(retired.Count);
        foreach (Entry e in retired)
        {
            e.Context.Unload();
            refs.Add((e.Name, new WeakReference(e.Context, trackResurrection: true)));
        }
        retired.Clear();   // drop our own strong references before checking
        // Context.Unload only starts the collection. A context still alive after a few collections
        // is held by something (a thread, a timer, an event, a cached type in a shared context) and
        // keeps everything the plugin loaded: every reload then loses that much address space for
        // good (300-480 MB on some reloads, 2026-10-19). Name the ones that stay, so the culprit shows.
        if (refs.Count > 0)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (refs.Exists(r => r.Context.IsAlive) && sw.ElapsedMilliseconds < UnloadCheckMs)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                if (refs.Exists(r => r.Context.IsAlive)) System.Threading.Thread.Sleep(20);
            }
            var alive = refs.FindAll(r => r.Context.IsAlive).ConvertAll(r => r.Name);
            RynthLog.Plugin($"ManagedPlugins: {refs.Count} plugin context(s) unloaded in {sw.ElapsedMilliseconds} ms (va before: {vaBefore}, after: {Compatibility.AddressSpace.Describe()})" +
                            (alive.Count == 0 ? "; all collected." : $"; STILL HELD after {UnloadCheckMs} ms (their memory stays): {string.Join(", ", alive)}."));
        }
#endif
    }

#if ENGINE_CORECLR
    /// <summary>
    /// Runtime\PluginShared: the RynthSuite plugins' common dependencies (RynthCore.PluginSdk,
    /// PluginCore, MoonSharp, ...), installed with RynthCore so each plugin in the update feed stays
    /// one DLL (v88, 2026-10-19). Each plugin still loads its own copy into its own collectible
    /// context, so it unloads with the plugin. Null when the folder isn't there.
    /// </summary>
    internal static readonly string? SharedDir = FindSharedDir();

    private static string? FindSharedDir()
    {
        try
        {
            // The shim sits in Runtime\ (the engine itself may run from Runtime\.engine_loads\).
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.GetName().Name != "RynthCore.Shim" || string.IsNullOrEmpty(a.Location)) continue;
                string dir = Path.Combine(Path.GetDirectoryName(a.Location)!, "PluginShared");
                return Directory.Exists(dir) ? dir : null;
            }
        }
        catch { }
        return null;
    }

    /// <summary>A plugin and its private dependencies (its own folder, then Runtime\PluginShared).</summary>
    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly string _dir;

        public PluginLoadContext(string name, string dir) : base("plugin " + name, isCollectible: true) => _dir = dir;

        public Assembly LoadFromFile(string path)
        {
            // From streams, so the plugin's folder stays unlocked (rebuilds overwrite it).
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            string pdb = Path.ChangeExtension(path, ".pdb");
            if (!File.Exists(pdb)) return LoadFromStream(fs);
            using var ps = new FileStream(pdb, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return LoadFromStream(fs, ps);
        }

        private static readonly HashSet<string> PrivateFramework = new(StringComparer.OrdinalIgnoreCase)
        {
            "System.Text.Json", "System.Text.Encodings.Web", "System.IO.Pipelines",
        };

        protected override Assembly? Load(AssemblyName name)
        {
            // The plugin's own dependencies (SDK, MoonSharp...) load privately; so do the
            // framework libraries that cache the types they see process-wide
            // (System.Text.Json roots a collectible context from the default one: Phase 0).
            // The rest of the framework comes from the default context.
            if (PrivateFramework.Contains(name.Name!))
            {
                string fx = Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, name.Name + ".dll");
                return File.Exists(fx) ? LoadFromAssemblyPath(fx) : null;
            }
            string? path = Pick(name.Name + ".dll");
            return path != null ? LoadFromFile(path) : null;
        }

        // The plugin's own copy or the shared one; with both, the newer file (a dev build carries
        // fresh dependencies beside it; a launcher update leaves stale ones beside a new plugin).
        private string? Pick(string fileName)
        {
            string own = Path.Combine(_dir, fileName);
            string? shared = SharedDir != null ? Path.Combine(SharedDir, fileName) : null;
            bool hasOwn = File.Exists(own), hasShared = shared != null && File.Exists(shared);
            if (hasOwn && hasShared)
                return File.GetLastWriteTimeUtc(shared!) > File.GetLastWriteTimeUtc(own) ? shared : own;
            return hasOwn ? own : hasShared ? shared : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            // ImGui.NET's "cimgui" is the engine's own module (RynthCore.cimgui.dll), so a plugin draws in
            // the engine's ImGui and whatever context it has current, a pop-out's included. Each plugin's
            // own ImGui.NET used to reach a second cimgui.dll whose current context stayed the main one:
            // a section drawn in a popped-out Mini Remote ran outside any frame and faulted in native code,
            // killing the client (RynthDreamWeave's server card, 2026-10-19). Found and fixed by
            // SilentKelpie (rynth PR #6); re-implemented here.
            if (EntryPoint.ImGuiNativeHandle != IntPtr.Zero
                && (unmanagedDllName.Equals("cimgui", StringComparison.OrdinalIgnoreCase)
                    || unmanagedDllName.Equals("cimgui.dll", StringComparison.OrdinalIgnoreCase)))
                return EntryPoint.ImGuiNativeHandle;
            string? path = Pick(unmanagedDllName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? unmanagedDllName
                : unmanagedDllName + ".dll");
            return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
        }
    }
#endif
}
