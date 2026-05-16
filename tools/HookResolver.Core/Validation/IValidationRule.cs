using RynthCore.HookManifest.Scanning;
using RynthCore.HookResolver.Core.Symbols;

namespace RynthCore.HookResolver.Core.Validation;

public readonly record struct ValidationContext(
    AcClientImageView Image,
    int CandidateRva,
    int CandidateVa,
    HookSymbol Symbol);

public readonly record struct ValidationOutcome(bool Passed, string RuleId, string Detail);

public interface IValidationRule
{
    string RuleId { get; }
    ValidationOutcome Evaluate(in ValidationContext ctx);
}
