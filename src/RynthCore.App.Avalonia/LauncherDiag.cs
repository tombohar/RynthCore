using System;
using System.IO;
using System.Text;
using System.Threading;

namespace RynthCore.App.Avalonia;

/// <summary>
/// Tiny file-append logger for the launcher. Mirrors the pattern used by
/// RynthCore.Loader and RynthCore.Engine so all three components write to
/// the same unified log (C:\Games\RynthCore\Logs\RynthCore.log) and entries
/// can be correlated across processes.
///
/// Used for diagnostic instrumentation that needs to survive a launcher
/// restart — AppendActivity only feeds the in-memory UI panel and is lost
/// when the launcher exits, which makes it useless for diagnosing exactly
/// the kind of "restart fixes it" launcher-state bugs we're chasing.
/// </summary>
internal static class LauncherDiag
{
    private const string UnifiedLogDirectory = @"C:\Games\RynthCore\Logs";
    private const string UnifiedLogFileName = "RynthCore.log";

    private static readonly object LogLock = new();

    public static void Info(string message)
    {
        try
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] [pid:{Environment.ProcessId}] [launcher] {message}\r\n";
            byte[] bytes = Encoding.UTF8.GetBytes(line);

            lock (LogLock)
            {
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    try
                    {
                        Directory.CreateDirectory(UnifiedLogDirectory);
                        using var fs = new FileStream(
                            Path.Combine(UnifiedLogDirectory, UnifiedLogFileName),
                            FileMode.Append,
                            FileAccess.Write,
                            FileShare.ReadWrite);
                        fs.Write(bytes, 0, bytes.Length);
                        return;
                    }
                    catch (IOException)
                    {
                        Thread.Sleep(5);
                    }
                    catch
                    {
                        return;
                    }
                }
            }
        }
        catch
        {
        }
    }
}
