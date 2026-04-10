using System;
using System.IO;

namespace NexSuite.Plugins.RynthAi
{
    /// <summary>
    /// Crash-safe file logger. Every write flushes immediately so the last
    /// line survives a hard client crash. Writes to RynthAi\Logs\tier2.log,
    /// rolls over at 1 MB.
    /// </summary>
    internal static class Tier2Log
    {
        private static readonly string LogDir =
            @"C:\Games\DecalPlugins\NexSuite\RynthAi\Logs";
        private static readonly string LogPath =
            Path.Combine(LogDir, "tier2.log");
        private const long MAX_SIZE = 1024 * 1024;  // 1 MB rollover

        private static readonly object _lock = new object();
        private static bool _initialized;

        /// <summary>
        /// Write a timestamped line to tier2.log.  Safe to call from any thread.
        /// </summary>
        public static void Log(string msg)
        {
            try
            {
                lock (_lock)
                {
                    EnsureDir();
                    Rollover();
                    using (var sw = new StreamWriter(LogPath, true))
                    {
                        sw.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
                        sw.Flush();
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Write a separator + header for a new session.
        /// </summary>
        public static void StartSession()
        {
            Log("════════════════════════════════════════════════════════════");
            Log($"Session start — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        }

        private static void EnsureDir()
        {
            if (!_initialized)
            {
                if (!Directory.Exists(LogDir))
                    Directory.CreateDirectory(LogDir);
                _initialized = true;
            }
        }

        private static void Rollover()
        {
            try
            {
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MAX_SIZE)
                {
                    string backup = LogPath + ".old";
                    if (File.Exists(backup)) File.Delete(backup);
                    File.Move(LogPath, backup);
                }
            }
            catch { }
        }
    }
}
