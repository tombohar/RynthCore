using System;
using System.IO;
using System.Linq;
using System.Text;
using RynthCore.Install;

namespace RynthCore.Injector;

internal static class Program
{
    // Unified log path — must stay in sync with RynthCore.Engine.LogPaths.
    private static readonly string UnifiedLogDirectory = RynthCore.Install.RynthInstallPaths.CoreLogsDir; // C:\Games\RynthCore\Logs by default
    private const string UnifiedLogFileName = "RynthCore.log";

    private static int Main(string[] args)
    {
        // Headless modes never prompt — safe to call from scripts / a test
        // harness with no interactive console.
        bool headless = args.Any(a =>
            string.Equals(a, "--launch", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "--decal-bridge", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "--no-prompt", StringComparison.OrdinalIgnoreCase));

        try
        {
            LogToFile("Injector starting.");

            if (args.Any(a => string.Equals(a, "--launch", StringComparison.OrdinalIgnoreCase)))
                return LaunchCommand.Run(args, LogToFile);

            if (args.Any(a => string.Equals(a, "--decal-bridge", StringComparison.OrdinalIgnoreCase)))
                return DecalBridgeCommand(args);

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

    /// <summary>
    /// --decal-bridge register [--dir &lt;folder&gt;] | check | status | unregister
    ///                | register-machine [--dir &lt;folder&gt;] | unregister-machine
    /// register: into Decal's machine-wide filter list (needs an administrator prompt; never
    /// creates a per-user copy), then the check. check: the launcher's "Check Decal bridge", read-only.
    /// The launcher does the same when an account is set to "Decal + RynthCore".
    /// See docs/DECAL_BRIDGE_PLAN.md.
    /// </summary>
    private static int DecalBridgeCommand(string[] args)
    {
        int i = Array.FindIndex(args, a => string.Equals(a, "--decal-bridge", StringComparison.OrdinalIgnoreCase));
        string verb = i + 1 < args.Length ? args[i + 1].ToLowerInvariant() : "status";
        int d = Array.FindIndex(args, a => string.Equals(a, "--dir", StringComparison.OrdinalIgnoreCase));
        string dir = d >= 0 && d + 1 < args.Length ? args[d + 1] : RynthCore.App.DecalBridgeRegistration.DefaultBridgeDirectory;
        string? acClient = RynthCore.App.DecalLocator.TryGetDecalAcClientPath();
        string engineJson = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RynthCore", "engine.json");
        bool ok;
        string report;
        switch (verb)
        {
            case "register":
            {
                var outcome = RynthCore.App.DecalBridgeRegistration.RegisterForClients(dir, acClient, engineJson, out report, out var check);
                ok = outcome == RynthCore.App.DecalBridgeRegistration.Outcome.Registered;
                if (outcome == RynthCore.App.DecalBridgeRegistration.Outcome.NeedsAdmin)
                    report += " Run this again from an administrator prompt: RynthCore.Injector --decal-bridge register-machine";
                if (check != null) report += Environment.NewLine + check.ToReport();
                break;
            }
            case "register-machine":
                ok = RynthCore.App.DecalBridgeRegistration.RegisterMachineWide(dir, out report) == RynthCore.App.DecalBridgeRegistration.Outcome.Registered;
                break;
            case "unregister-machine":
                ok = RynthCore.App.DecalBridgeRegistration.UnregisterMachineWide(out report) == RynthCore.App.DecalBridgeRegistration.Outcome.Registered;
                break;
            case "unregister":
                ok = RynthCore.App.DecalBridgeRegistration.Unregister(out report);
                if (RynthCore.App.DecalBridgeRegistration.MachineEntryIsOurs())
                    report += " A machine-wide entry is left: remove it with --decal-bridge unregister-machine from an administrator prompt.";
                break;
            case "check":
            {
                var check = RynthCore.App.RealDecalCheckHost.Check(dir, acClient, engineJson);
                ok = !check.Blocking;
                report = check.ToReport();
                break;
            }
            case "status":
                ok = true;
                report = RynthCore.App.DecalBridgeRegistration.GetStatus(acClient).ToString();
                break;
            default:
                Console.WriteLine("usage: --decal-bridge register [--dir <folder>] | check | status | unregister | register-machine [--dir <folder>] | unregister-machine");
                return 2;
        }
        Console.WriteLine(report);
        LogToFile($"[decal-bridge] {verb}: {report}");
        return ok ? 0 : 1;
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
