// ============================================================================
//  RynthCore.Shim - ShimHost.cs
//
//  Called by the native loader (native/Loader/Clr.c) once CoreCLR is up. Loads each
//  engine generation into its own collectible AssemblyLoadContext, hands the loader
//  the engine's [UnmanagedCallersOnly(EntryPoint = ...)] methods in place of DLL
//  exports, and on reload unloads the context and waits until it is really gone.
//
//  Rules (docs/UNLOADABLE_ENGINE_PLAN.md): nothing here may hold an engine type,
//  delegate, MethodInfo or object across a generation; only raw function pointers,
//  which are dropped before the unload.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Threading;

namespace RynthCore.Shim;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct LoaderApi
{
    public int Size;
    public delegate* unmanaged[Stdcall]<byte*, byte*, void> Log;
    public delegate* unmanaged[Stdcall]<int*, int> FreeMb;
    public byte* LoaderDir;
    public delegate* unmanaged[Stdcall]<int> HookCount;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct ShimApi
{
    public int Size;
    public delegate* unmanaged[Stdcall]<int, byte*, int> LoadEngine;
    public delegate* unmanaged[Stdcall]<int> UnloadEngine;
    public delegate* unmanaged[Stdcall]<byte*, nint> EngineExport;
}

public static unsafe class ShimHost
{
    private const string EngineEntryType = "RynthCore.Engine.EntryPoint";
    private const int UnloadWaitMs = 5000;

    private static LoaderApi* s_api;
    private static string s_loaderDir = "";
    private static string s_runtimeDir = "";

    private static EngineLoadContext? s_alc;
    private static readonly Dictionary<string, nint> s_exports = new(StringComparer.Ordinal);
    private static int s_generation;
    private static int s_leakedGenerations;
    // Generations that outlived their unload wait (e.g. AC's main thread was inside one of
    // their hook detours behind a modal dialog). They are still unloading and usually go
    // later; checked, and reported, at every reload.
    private static readonly List<(int Generation, WeakReference Context)> s_pending = new();

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    public static int Init(LoaderApi* api, ShimApi* shim)
    {
        try
        {
            s_api = api;
            s_loaderDir = Marshal.PtrToStringUTF8((nint)api->LoaderDir) ?? "";
            s_runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location) ?? "";
            shim->LoadEngine = &LoadEngine;
            shim->UnloadEngine = &UnloadEngine;
            shim->EngineExport = &EngineExport;
            Log("INF", $"Shim up: CoreCLR {Environment.Version}, {RuntimeInformation.ProcessArchitecture}, " +
                       $"runtime {s_runtimeDir}, GC {(GCSettings.IsServerGC ? "server" : "workstation")}.");
            return 0;
        }
        catch (Exception ex)
        {
            Log("ERR", "Shim init failed: " + ex);
            return 1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int LoadEngine(int generation, byte* stagedPathUtf8)
    {
        try
        {
            string path = Marshal.PtrToStringUTF8((nint)stagedPathUtf8) ?? "";
            var sw = Stopwatch.StartNew();
            LoadGeneration(generation, path);
            Log("INF", $"Engine gen {generation} loaded into a collectible context in {sw.ElapsedMilliseconds} ms " +
                       $"({s_exports.Count} exports: {string.Join(", ", s_exports.Keys)}).");
            return 0;
        }
        catch (Exception ex)
        {
            Log("ERR", $"Loading engine gen {generation} failed: {ex}");
            return 1;
        }
    }

    // Kept out of LoadEngine so no local of the unmanaged entry frame holds the context.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LoadGeneration(int generation, string stagedPath)
    {
        var alc = new EngineLoadContext($"RynthCore.Engine gen{generation}", s_loaderDir, s_runtimeDir);
        Assembly engine = alc.LoadFromAssemblyPath(stagedPath);
        Type entry = engine.GetType(EngineEntryType, throwOnError: true)!;
        s_exports.Clear();
        foreach (MethodInfo m in entry.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
        {
            var uco = m.GetCustomAttribute<UnmanagedCallersOnlyAttribute>();
            if (uco?.EntryPoint is { Length: > 0 } name)
                s_exports[name] = m.MethodHandle.GetFunctionPointer();
        }
        s_alc = alc;
        s_generation = generation;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static nint EngineExport(byte* nameUtf8)
    {
        string name = Marshal.PtrToStringUTF8((nint)nameUtf8) ?? "";
        return s_exports.TryGetValue(name, out nint fn) ? fn : 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int UnloadEngine()
    {
        try
        {
            s_exports.Clear();
            ReportLateUnloads();
            if (s_alc == null) return 1;
            int gen = s_generation;
            int freeBefore = FreeMb(out _);
            var sw = Stopwatch.StartNew();
            WeakReference alcRef = StartUnload();
            int collections = 0;
            while (alcRef.IsAlive && sw.ElapsedMilliseconds < UnloadWaitMs)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                collections++;
                if (alcRef.IsAlive) Thread.Sleep(20);
            }
            GC.Collect();
            int freeAfter = FreeMb(out int largest);
            if (!alcRef.IsAlive)
            {
                Log("INF", $"Engine gen {gen} unloaded in {sw.ElapsedMilliseconds} ms ({collections} GCs): " +
                           $"{freeAfter} MB free ({freeAfter - freeBefore:+0;-0} MB), largest block {largest} MB.");
                return 1;
            }
            s_leakedGenerations++;
            s_pending.Add((gen, alcRef));
            Log("WRN", $"Engine gen {gen} NOT unloaded after {UnloadWaitMs} ms ({collections} GCs): something still " +
                       $"references it (a thread still running engine code, an event, a static in the default " +
                       $"context...). {s_leakedGenerations} generation(s) leaked so far; {freeAfter} MB free.");
            return 0;
        }
        catch (Exception ex)
        {
            Log("ERR", "Unload failed: " + ex);
            return 0;
        }
    }

    private static void ReportLateUnloads()
    {
        for (int i = s_pending.Count - 1; i >= 0; i--)
        {
            if (s_pending[i].Context.IsAlive) continue;
            s_leakedGenerations--;
            Log("INF", $"Engine gen {s_pending[i].Generation} has unloaded since (late). {s_leakedGenerations} generation(s) still held.");
            s_pending.RemoveAt(i);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference StartUnload()
    {
        EngineLoadContext alc = s_alc!;
        s_alc = null;
        alc.Unload();
        return new WeakReference(alc, trackResurrection: true);
    }

    private static int FreeMb(out int largest)
    {
        int l = 0;
        int free = s_api->FreeMb(&l);
        largest = l;
        return free;
    }

    internal static void Log(string level, string message)
    {
        if (s_api == null) return;
        byte[] lvl = Encoding.ASCII.GetBytes(level + "\0");
        byte[] msg = Encoding.UTF8.GetBytes(message + "\0");
        fixed (byte* pl = lvl)
        fixed (byte* pm = msg)
            s_api->Log(pl, pm);
    }
}

/// <summary>
/// One engine generation. The engine's own dependencies (Avalonia, ImGui.NET, SkiaSharp...)
/// load here from Runtime\ so they go away with it; so do the framework libraries that keep
/// process-wide caches of the types they see (System.Text.Json: Phase 0 found it roots a
/// collectible context from the default one). Everything else comes from the default context.
/// </summary>
internal sealed class EngineLoadContext : AssemblyLoadContext
{
    private static readonly HashSet<string> PrivateFramework = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.Text.Json", "System.Text.Encodings.Web", "System.IO.Pipelines",
    };

    private readonly string _engineDir;
    private readonly string _runtimeDir;

    public EngineLoadContext(string name, string engineDir, string runtimeDir) : base(name, isCollectible: true)
    {
        _engineDir = engineDir;
        _runtimeDir = runtimeDir;
    }

    protected override Assembly? Load(AssemblyName name)
    {
        string? simple = name.Name;
        if (string.IsNullOrEmpty(simple) || simple == "RynthCore.Shim") return null;

        if (PrivateFramework.Contains(simple))
        {
            string fx = Path.Combine(_runtimeDir, simple + ".dll");
            return File.Exists(fx) ? LoadFromAssemblyPath(fx) : null;
        }

        string local = Path.Combine(_engineDir, simple + ".dll");
        if (!File.Exists(local)) return null;
        // From a stream, so Runtime\ stays unlocked (a deploy can overwrite it while clients run).
        using var fs = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        string pdb = Path.ChangeExtension(local, ".pdb");
        if (!File.Exists(pdb)) return LoadFromStream(fs);
        using var ps = new FileStream(pdb, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return LoadFromStream(fs, ps);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        // Native libraries (cimgui, libSkiaSharp, minhook...) are process-wide and stay
        // loaded; resolve them from Runtime\ first.
        string candidate = Path.Combine(_engineDir, unmanagedDllName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? unmanagedDllName
            : unmanagedDllName + ".dll");
        return File.Exists(candidate) ? LoadUnmanagedDllFromPath(candidate) : 0;
    }
}
