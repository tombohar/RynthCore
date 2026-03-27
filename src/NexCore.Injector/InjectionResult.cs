using System;

namespace NexCore.Injector;

public sealed class InjectionResult
{
    public bool Success { get; init; }
    public int ExitCode { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string EnginePath { get; init; } = string.Empty;
    public int? ProcessId { get; init; }
    public uint? InitResult { get; init; }

    public static InjectionResult Failure(int exitCode, string summary, string enginePath = "", int? processId = null)
    {
        return new InjectionResult
        {
            Success = false,
            ExitCode = exitCode,
            Summary = summary,
            EnginePath = enginePath,
            ProcessId = processId
        };
    }

    public static InjectionResult SuccessResult(string summary, string enginePath, int processId, uint initResult)
    {
        return new InjectionResult
        {
            Success = true,
            ExitCode = 0,
            Summary = summary,
            EnginePath = enginePath,
            ProcessId = processId,
            InitResult = initResult
        };
    }
}
