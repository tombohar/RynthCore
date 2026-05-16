using System.Collections.Generic;

namespace RynthCore.HookManifest.Schema;

public enum ResolutionStatus
{
    Resolved,
    Ambiguous,
    Unresolved,
    NotApplicable
}

public sealed record HookManifestEntry
{
    public required string SymbolId { get; init; }
    public required ResolutionStatus Status { get; init; }
    public ResolvedAddress? Address { get; init; }
    public string? PatternId { get; init; }
    public int MatchedCandidates { get; init; }
    public string? ChosenCandidateReason { get; init; }
    public IReadOnlyList<ValidationRecord> Validations { get; init; } = [];
    public IReadOnlyList<ValidationRecord> Warnings { get; init; } = [];
    public string? Reason { get; init; }
    public FunctionSnapshot? Snapshot { get; init; }
}
