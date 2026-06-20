using System;
using System.IO;
using System.Linq;
using System.Text;

namespace RynthCore.Injector;

internal static class Program
{
    // Unified log path — must stay in sync with RynthCore.Engine.LogPaths.
    private const string UnifiedLogDirectory = @"C:\Games\RynthCore\Logs";
    private const string UnifiedLogFileName = "RynthCore.log";

    private static int Main(string[] args)
    {
        // Headless modes never prompt — safe to call from scripts / a test
        // harness with no interactive console.
        bool headless = args.Any(a =>
            string.Equals(a, "--launch", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "--no-prompt", StringComparison.OrdinalIgnoreCase));

        try
        {
            LogToFile("Injector starting.");

            if (args.Any(a => string.Equals(a, "--launch", StringComparison.OrdinalIgnoreCase)))
                return LaunchCommand.Run(args, LogToFile);

            return Run(args);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine();
            Console.WriteLine($"FATAL EXCEPTION: {ex}");
            Console.ResetColor();
            LogToFile($"FATAL: {ex}");
            return 99;
        }
        finally
        {
            if (!headless)
            {
                Console.WriteLine();
                Console.WriteLine("Press any key to exit...");
                try { Console.ReadKey(true); } catch { /* no interactive console */ }
            }
        }
    }

    private static int Run(string[] args)
    {
        var service = new EngineInjectionService();

        Console.WriteLine("========================================");
        Console.WriteLine("        RynthCore Injector Console        ");
        Console.WriteLine("========================================");
        Console.WriteLine();

        string? enginePath = service.TryResolveEnginePath(args.Length > 0 ? args[0] : null);
        if (enginePath == null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Could not locate {EngineInjectionService.EngineDllName}.");
            Console.WriteLine("Copy it next to the injector or pass the full path as the first argument.");
            Console.ResetColor();
            LogToFile($"FAIL: could not locate {EngineInjectionService.EngineDllName}");
            return 1;
        }

        LogToFile($"Resolved engine path: {enginePath}");

        InjectionResult result = service.InjectFirstRunning(
            enginePath,
            line =>
            {
                Console.WriteLine(line);
                LogToFile(line);
            });
        Console.WriteLine();

        if (result.Success)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(result.Summary);
            Console.WriteLine($"Check {Path.Combine(UnifiedLogDirectory, UnifiedLogFileName)} for in-process status.");
            Console.ResetColor();
            LogToFile($"SUCCESS: {result.Summary}");
            return 0;
        }

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(result.Summary);
        Console.ResetColor();
        LogToFile($"FAIL: {result.Summary}  exit={result.ExitCode}");
        return result.ExitCode;
    }

    /// <summary>
    /// Append a line to the unified RynthCore log so injector and in-process
    /// engine activity show up in one timeline. FileShare.ReadWrite lets the
    /// engine (running inside acclient.exe) keep writing concurrently.
    /// </summary>
    private static void LogToFile(string message)
    {
        try
        {
            Directory.CreateDirectory(UnifiedLogDirectory);
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] [pid:{Environment.ProcessId}] [injector] {message}\r\n";
            byte[] bytes = Encoding.UTF8.GetBytes(line);

            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
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
                    System.Threading.Thread.Sleep(5);
                }
                catch
                {
                    return;
                }
            }
        }
        catch
        {
        }
    }
}
