// ============================================================================
//  RynthCore.Engine - LogPaths.cs
//  Single source of truth for the unified log path. Engine, plugin SDK,
//  injector, and CrashLogger all funnel into the same file under
//  C:\Games\RynthCore\Logs\RynthCore.log so a crash investigation only ever
//  needs to look at one file.
// ============================================================================

using System;
using System.IO;

namespace RynthCore.Engine;

internal static class LogPaths
{
    /// <summary>Unified log directory for all RynthCore components.</summary>
    internal const string LogDirectory = @"C:\Games\RynthCore\Logs";

    /// <summary>Unified log filename. Engine, loader, plugins, and injector all append here.</summary>
    internal const string LogFileName = "RynthCore.log";

    /// <summary>Rotated-out previous log when the active log exceeds <see cref="MaxLogBytes"/> at startup.</summary>
    internal const string RotatedLogFileName = "RynthCore.log.old";

    /// <summary>Rotate the active log when it grows beyond this size at engine startup.</summary>
    internal const long MaxLogBytes = 5L * 1024 * 1024;

    /// <summary>Full path to the unified log file.</summary>
    internal static string LogFilePath => Path.Combine(LogDirectory, LogFileName);

    /// <summary>Full path to the rotated previous log.</summary>
    internal static string RotatedLogFilePath => Path.Combine(LogDirectory, RotatedLogFileName);

    /// <summary>
    /// Best-effort: create the log directory. Safe to call repeatedly. Never throws.
    /// </summary>
    internal static void EnsureLogDirectory()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
        }
        catch
        {
            // No fallback — if C:\Games\RynthCore\Logs can't be created the
            // sink will silently swallow writes. We deliberately do NOT fall
            // back to Desktop because that scatters logs across machines.
        }
    }

    /// <summary>
    /// Engine-startup rotation: if the active log exceeds <see cref="MaxLogBytes"/>,
    /// move it to RynthCore.log.old (overwriting any previous rotation). Only one
    /// process should call this — the engine, on init.
    /// </summary>
    internal static void RotateAtStartup()
    {
        try
        {
            EnsureLogDirectory();
            string active = LogFilePath;
            if (!File.Exists(active)) return;

            long size;
            try { size = new FileInfo(active).Length; }
            catch { return; }

            if (size < MaxLogBytes) return;

            string rotated = RotatedLogFilePath;
            try { if (File.Exists(rotated)) File.Delete(rotated); } catch { }

            try { File.Move(active, rotated); }
            catch
            {
                // Could be locked (concurrent loader/injector writer). Best-
                // effort truncate so the active file doesn't grow without bound.
                try { File.WriteAllText(active, string.Empty); } catch { }
            }
        }
        catch
        {
        }
    }
}
