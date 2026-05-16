using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.HookResolver.Core.Disassembly;

namespace RynthCore.HookResolver.Core.Validation.Rules;

/// <summary>
/// Decodes the candidate function with Iced and asserts that at least
/// <see cref="_minMatches"/> of the <see cref="_expectedTargets"/> VAs appear
/// as CALL targets in its body. This is the rule that distinguishes a real
/// combat-action wrapper (which calls the shared dispatcher trio) from a
/// look-alike the bare pattern happened to match.
/// </summary>
public sealed class CallTargetsRule : IValidationRule
{
    private readonly int[] _expectedTargets;
    private readonly int _minMatches;
    private readonly int _scanBytes;

    public CallTargetsRule(int[] expectedTargets, int minMatches, int scanBytes = 0x400)
    {
        _expectedTargets = expectedTargets;
        _minMatches = minMatches;
        _scanBytes = scanBytes;
        RuleId = "calltargets:[" +
                 string.Join(",", expectedTargets.Select(v => $"0x{v:X8}")) +
                 $"]>={minMatches}";
    }

    public string RuleId { get; }

    public ValidationOutcome Evaluate(in ValidationContext ctx)
    {
        var snap = IcedDisassembler.Snapshot(ctx.Image, ctx.CandidateRva, maxBytes: _scanBytes);
        var present = new HashSet<int>(snap.CallTargets.Select(c => c.Va));

        var matched = _expectedTargets.Where(present.Contains).ToArray();
        bool pass = matched.Length >= _minMatches;

        string matchedHex = matched.Length == 0
            ? "none"
            : string.Join(",", matched.Select(v => $"0x{v:X8}"));

        return new ValidationOutcome(
            pass,
            RuleId,
            $"{matched.Length}/{_expectedTargets.Length} expected dispatchers present " +
            $"({matchedHex}); needed >= {_minMatches}");
    }
}
