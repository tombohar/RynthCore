// ============================================================================
//  RynthCore.Engine - Plugins/PluginFilePrefetch.cs
//  Opens the plugin files (PluginFilePrefetchList) on a few worker threads as the
//  init worker starts, seconds ahead of LoadPlugins. Nothing is loaded or kept open.
//
//  Why (2026-11-02): the first open of a new or changed file waits while the
//  antivirus (Windows Defender's real-time protection) scans all of it. After an
//  update every plugin file is new, and the one-file-at-a-time plugin load spent
//  15-20 s in those scans: 8 s reading the manifests, 12 s loading 14 plugins, all
//  before the EndScene hook went in. Measured offline on the same 116 files
//  (27 MB): opened one at a time 15.1 s, on 8 threads 4.6 s; loaded again after
//  that, the whole plugin set took 0.6 s instead of 15 s. Scans of different files
//  run at once, and a load that opens a file still being scanned just waits for
//  that scan, so nothing here has to finish before LoadPlugins starts.
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace RynthCore.Engine.Plugins;

internal static class PluginFilePrefetch
{
    private const int MaxThreads = 8;

    private static readonly ConcurrentQueue<string> Queue = new();
    private static readonly object SlowestLock = new();
    private static int _started;
    private static int _remaining;
    private static int _total;
    private static long _startTicks;
    private static double _slowestMs;
    private static string _slowest = "";

    /// <summary>Starts the workers (once per engine generation). Init worker.</summary>
    public static void Start(IReadOnlyList<string> pluginPaths)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        List<string> files;
        try
        {
#if ENGINE_CORECLR
            files = PluginFilePrefetchList.Build(pluginPaths, ManagedPlugins.SharedDir);
#else
            files = PluginFilePrefetchList.Build(pluginPaths, null);
#endif
        }
        catch (Exception ex)
        {
            RynthLog.Plugin($"PluginFilePrefetch: file list failed ({ex.GetType().Name}: {ex.Message}); the plugins load without it.");
            return;
        }
        if (files.Count == 0) return;

        foreach (string f in files) Queue.Enqueue(f);
        _total = _remaining = files.Count;
        _startTicks = Stopwatch.GetTimestamp();
        int threads = Math.Min(files.Count, Math.Clamp(Environment.ProcessorCount, 2, MaxThreads));
        RynthLog.Plugin($"PluginFilePrefetch: opening {files.Count} plugin file(s) on {threads} thread(s) ahead of the plugin load (antivirus scans of new files run at once).");
        for (int i = 0; i < threads; i++)
        {
            try { EngineThreads.Start($"PluginFilePrefetch.{i}", Worker); }
            catch (Exception ex)
            {
                RynthLog.Plugin($"PluginFilePrefetch: thread {i} did not start ({ex.GetType().Name}); the others go on.");
                break;
            }
        }
    }

    private static void Worker()
    {
        while (!EngineThreads.Stopping && Queue.TryDequeue(out string? path))
        {
            long t0 = Stopwatch.GetTimestamp();
            Touch(path);
            double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            lock (SlowestLock)
            {
                if (ms > _slowestMs) { _slowestMs = ms; _slowest = Path.GetFileName(path); }
            }
            if (Interlocked.Decrement(ref _remaining) == 0)
            {
                double total = Stopwatch.GetElapsedTime(_startTicks).TotalMilliseconds;
                RynthLog.Plugin($"PluginFilePrefetch: {_total} file(s) opened in {total:0} ms (slowest {_slowest} {_slowestMs:0} ms).");
            }
        }
    }

    private static void Touch(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            fs.ReadByte();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The load opens it again and reports what's wrong.
        }
    }
}
