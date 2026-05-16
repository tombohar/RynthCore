using System.Collections.Generic;

namespace RynthCore.HookManifest.Schema;

public sealed record CallTarget(int Va, int Offset);

public sealed record FunctionSnapshot
{
    public required int Rva { get; init; }
    public required int Va { get; init; }
    public required int LengthBytes { get; init; }
    public required int InstructionCount { get; init; }
    public required bool EndsAtReturn { get; init; }
    public required string PrologueHex { get; init; }
    public IReadOnlyList<CallTarget> CallTargets { get; init; } = [];
    public string? Note { get; init; }
}
