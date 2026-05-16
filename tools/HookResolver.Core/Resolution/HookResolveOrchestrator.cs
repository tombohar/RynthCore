using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using RynthCore.HookManifest.Scanning;
using RynthCore.HookManifest.Schema;
using RynthCore.HookResolver.Core.Disassembly;
using RynthCore.HookResolver.Core.Offline;
using RynthCore.HookResolver.Core.Symbols;
using RynthCore.HookResolver.Core.Validation;

namespace RynthCore.HookResolver.Core.Resolution;

public static class HookResolveOrchestrator
{
    public static async IAsyncEnumerable<HookManifestEntry> ResolveAsync(
        AcClientImage image,
        IReadOnlyList<HookSymbol> symbols,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var symbol in symbols)
        {
            ct.ThrowIfCancellationRequested();
            var entry = ResolveSymbol(image.View, symbol);
            await Task.Yield();
            yield return entry;
        }
    }

    private static HookManifestEntry ResolveSymbol(AcClientImageView image, HookSymbol symbol)
    {
        var candidates = FindCandidates(image, symbol).ToList();

        if (candidates.Count == 0)
        {
            return new HookManifestEntry
            {
                SymbolId = symbol.Id,
                Status = ResolutionStatus.Unresolved,
                PatternId = symbol.PatternId,
                MatchedCandidates = 0,
                Reason = "no pattern matches in .text"
            };
        }

        var passingCandidates = new List<(int Rva, int Va, List<ValidationRecord> Validations, List<ValidationRecord> Warnings)>();
        var rejectedCandidates = new List<(int Rva, int Va, List<ValidationRecord> Validations)>();

        foreach (var (rva, va) in candidates)
        {
            var ctx = new ValidationContext(image, rva, va, symbol);
            var validations = new List<ValidationRecord>();
            var warnings = new List<ValidationRecord>();
            bool requiredPassed = true;

            foreach (var rule in symbol.RequiredRules)
            {
                var outcome = rule.Evaluate(ctx);
                validations.Add(new ValidationRecord(outcome.RuleId, outcome.Passed, outcome.Detail));
                if (!outcome.Passed) requiredPassed = false;
            }

            foreach (var rule in symbol.AdvisoryRules)
            {
                var outcome = rule.Evaluate(ctx);
                var rec = new ValidationRecord(outcome.RuleId, outcome.Passed, outcome.Detail);
                validations.Add(rec);
                if (!outcome.Passed) warnings.Add(rec);
            }

            if (requiredPassed)
                passingCandidates.Add((rva, va, validations, warnings));
            else
                rejectedCandidates.Add((rva, va, validations));
        }

        if (passingCandidates.Count == 1)
        {
            var c = passingCandidates[0];
            return new HookManifestEntry
            {
                SymbolId = symbol.Id,
                Status = ResolutionStatus.Resolved,
                Address = new ResolvedAddress(c.Va, c.Rva),
                PatternId = symbol.PatternId,
                MatchedCandidates = candidates.Count,
                ChosenCandidateReason = candidates.Count == 1 ? "single-match" : "single-passing",
                Validations = c.Validations,
                Warnings = c.Warnings,
                Snapshot = IcedDisassembler.Snapshot(image, c.Rva)
            };
        }

        if (passingCandidates.Count > 1)
        {
            if (symbol.FallbackVa is { } fb)
            {
                var chosen = passingCandidates.OrderBy(c => Math.Abs(c.Va - fb)).First();
                return new HookManifestEntry
                {
                    SymbolId = symbol.Id,
                    Status = ResolutionStatus.Resolved,
                    Address = new ResolvedAddress(chosen.Va, chosen.Rva),
                    PatternId = symbol.PatternId,
                    MatchedCandidates = candidates.Count,
                    ChosenCandidateReason = $"multi-match nearest-fallback (of {passingCandidates.Count} passing)",
                    Validations = chosen.Validations,
                    Warnings = chosen.Warnings,
                    Snapshot = IcedDisassembler.Snapshot(image, chosen.Rva)
                };
            }

            var first = passingCandidates[0];
            return new HookManifestEntry
            {
                SymbolId = symbol.Id,
                Status = ResolutionStatus.Ambiguous,
                Address = new ResolvedAddress(first.Va, first.Rva),
                PatternId = symbol.PatternId,
                MatchedCandidates = candidates.Count,
                ChosenCandidateReason = "first-passing (no fallback)",
                Validations = first.Validations,
                Warnings = first.Warnings,
                Reason = $"{passingCandidates.Count} candidates passed; no fallback VA to disambiguate",
                Snapshot = IcedDisassembler.Snapshot(image, first.Rva)
            };
        }

        var firstReject = rejectedCandidates[0];
        return new HookManifestEntry
        {
            SymbolId = symbol.Id,
            Status = ResolutionStatus.Unresolved,
            PatternId = symbol.PatternId,
            MatchedCandidates = candidates.Count,
            Validations = firstReject.Validations,
            Reason = $"{candidates.Count} pattern hits, none passed required rules"
        };
    }

    private static IEnumerable<(int Rva, int Va)> FindCandidates(AcClientImageView image, HookSymbol symbol)
    {
        byte[] text = image.TextBytes;
        int start = 0;
        while (start < text.Length)
        {
            int hit = PatternScanner.FindPatternInRegion(text, symbol.Pattern, start, text.Length);
            if (hit < 0) yield break;

            int resolvedOff = hit;
            if (symbol.WalkBack is { } walk)
            {
                int prologueOff = PatternScanner.FindPrologueBefore(text, hit, walk.Prologue, walk.MaxDistance);
                if (prologueOff < 0)
                {
                    start = hit + 1;
                    continue;
                }
                resolvedOff = prologueOff;
            }

            int rva = image.TextRva + resolvedOff;
            int va = image.ImageBase + rva;
            yield return (rva, va);
            start = hit + 1;
        }
    }
}
