using System;
using RynthCore.HookManifest.Scanning;

namespace RynthCore.HookResolver.Core.Validation.Rules;

public sealed class PrologueBytesRule : IValidationRule
{
    private readonly byte?[] _expected;

    public PrologueBytesRule(byte?[] expected)
    {
        _expected = expected;
        RuleId = "prologue:" + FormatPattern(expected);
    }

    public string RuleId { get; }

    public ValidationOutcome Evaluate(in ValidationContext ctx)
    {
        int offset = ctx.CandidateRva - ctx.Image.TextRva;
        bool ok = PatternScanner.VerifyPattern(ctx.Image.TextBytes, offset, _expected);
        if (ok)
            return new ValidationOutcome(true, RuleId, "prologue matches");

        int len = Math.Min(_expected.Length, ctx.Image.TextBytes.Length - offset);
        string actual = "";
        if (offset >= 0 && len > 0)
        {
            Span<byte> slice = ctx.Image.TextBytes.AsSpan(offset, len);
            actual = Convert.ToHexString(slice);
        }

        return new ValidationOutcome(false, RuleId,
            $"prologue mismatch (saw {actual}, expected {FormatPattern(_expected)})");
    }

    private static string FormatPattern(byte?[] pattern)
    {
        var sb = new System.Text.StringBuilder(pattern.Length * 2);
        foreach (var b in pattern)
            sb.Append(b.HasValue ? b.Value.ToString("X2") : "??");
        return sb.ToString();
    }
}
