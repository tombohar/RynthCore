using System;

namespace RynthCore.Injector;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine();
            Console.WriteLine($"FATAL EXCEPTION: {ex}");
            Console.ResetColor();
            return 99;
        }
        finally
        {
            Console.WriteLine();
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey(true);
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
            return 1;
        }

        InjectionResult result = service.InjectFirstRunning(enginePath, Console.WriteLine);
        Console.WriteLine();

        if (result.Success)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(result.Summary);
            Console.WriteLine("Check Desktop\\RynthCore.log for in-process status.");
            Console.ResetColor();
            return 0;
        }

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(result.Summary);
        Console.ResetColor();
        return result.ExitCode;
    }
}
