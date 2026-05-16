using System.Collections.Generic;
using RynthCore.HookResolver.Core.Validation;

namespace RynthCore.HookResolver.Core.Symbols;

public enum SymbolAnchor
{
    AnchorOnPrologue,
    AnchorOnBody,
    AnchorOnCallSite,
    AnchorOnStringRef
}

public sealed record PrologueWalkBack(byte[] Prologue, int MaxDistance = 0x100);

public sealed record HookSymbol
{
    public required string Id { get; init; }
    public required string PatternId { get; init; }
    public required byte?[] Pattern { get; init; }
    public int? FallbackVa { get; init; }
    public required SymbolAnchor Anchor { get; init; }
    public PrologueWalkBack? WalkBack { get; init; }
    public IReadOnlyList<IValidationRule> RequiredRules { get; init; } = [];
    public IReadOnlyList<IValidationRule> AdvisoryRules { get; init; } = [];
}
