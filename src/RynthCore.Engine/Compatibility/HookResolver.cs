// ============================================================================
//  RynthCore.Engine - Compatibility/HookResolver.cs
//
//  Shared helper that resolves an acclient.exe function address by:
//    1. Pattern scanning the .text section for a unique byte signature.
//    2. Falling back to a hardcoded VA only if the pattern misses, with loud
//       logging so we know the next time the binary diverges.
//    3. Reporting failure with full diagnostics so the failing hook can be
//       identified from the log alone.
//
//  Every site that previously hardcoded `int funcOff = TargetVa - text.TextBaseVa`
//  should funnel through Resolve() so the install log line tells us exactly
//  how the address was found — pattern (good), fallback (suspect), or
//  unavailable (definitely the wrong build).
// ============================================================================
using System;

namespace RynthCore.Engine.Compatibility;

internal static class HookResolver
{
    public enum ResolveSource { Failed, PatternScan, FallbackVa }

    public readonly struct ResolveResult
    {
        public ResolveResult(IntPtr address, ResolveSource source, string detail)
        {
            Address = address;
            Source = source;
            Detail = detail;
        }

        public IntPtr Address { get; }
        public ResolveSource Source { get; }
        public string Detail { get; }
        public bool Success => Source != ResolveSource.Failed;
    }

    /// <summary>
    /// Locate <paramref name="functionName"/> in the loaded acclient.exe text
    /// section. Pattern is the source of truth; <paramref name="fallbackVa"/>
    /// is a last-resort safety net if pattern matching fails (e.g. a tiny
    /// build delta we haven't anticipated).
    ///
    /// Logs at every outcome so the install-time log line names which hook
    /// resolved how — when a crash happens, the *previous* successful resolve
    /// log entry tells you which hook was the most recent thing patched.
    /// </summary>
    public static ResolveResult Resolve(
        AcClientTextSection text,
        string functionName,
        byte?[] pattern,
        int fallbackVa)
    {
        try
        {
            int firstMatch = PatternScanner.FindPattern(text.Bytes, pattern);
            if (firstMatch >= 0)
            {
                // Deep-audit finding #30 (2026-06-18): this used to find only
                // the first and the immediately-next match, so a 3rd+
                // occurrence was never considered and the multimatch log
                // claimed to have examined only 2 of N. Not reachable on the
                // current shipped binary (all patterns verified unique) —
                // this is a defensive fallback for future binary drift, but
                // "examined 2 of N and picked wrong" is exactly the failure
                // mode a defensive fallback shouldn't have. Enumerate every
                // match and keep the global-nearest to the fallback VA.
                int fallbackOff = fallbackVa - text.TextBaseVa;
                int chosen = firstMatch;
                int bestDist = Math.Abs(firstMatch - fallbackOff);
                int matchCount = 1;
                string detail;

                if (bestDist != 0) // exact-VA short-circuit — no other match can beat this
                {
                    int searchFrom = firstMatch + 1;
                    while (true)
                    {
                        int next = PatternScanner.FindPatternInRegion(text.Bytes, pattern, searchFrom, text.Bytes.Length);
                        if (next < 0) break;
                        matchCount++;
                        int dist = Math.Abs(next - fallbackOff);
                        if (dist < bestDist) { bestDist = dist; chosen = next; }
                        if (bestDist == 0) break;
                        searchFrom = next + 1;
                    }
                }

                if (matchCount == 1)
                {
                    detail = "pattern-unique";
                }
                else
                {
                    detail = $"pattern-multimatch(near-fallback {chosen:X}, {matchCount} matches)";
                    RynthLog.Compat(
                        $"HookResolver[{functionName}]: pattern matched {matchCount} locations — " +
                        $"chose 0x{text.TextBaseVa + chosen:X8} as nearest to fallback 0x{fallbackVa:X8}.");
                }

                IntPtr addr = new IntPtr(text.TextBaseVa + chosen);
                RynthLog.Info(
                    $"HookResolver[{functionName}]: RESOLVED via {detail} @ 0x{addr.ToInt32():X8} " +
                    $"(fallbackVa=0x{fallbackVa:X8}, delta={(addr.ToInt32() - fallbackVa):+#;-#;0}).");
                return new ResolveResult(addr, ResolveSource.PatternScan, detail);
            }

            // Pattern miss — try the hardcoded VA, but only if the bytes there
            // look plausible (not padding / interrupt fill / a bare ret).
            int vaOff = fallbackVa - text.TextBaseVa;
            if (vaOff >= 0 && vaOff < text.Bytes.Length)
            {
                byte firstByte = text.Bytes[vaOff];
                bool prologueLooksValid = firstByte is not (0x00 or 0xCC or 0xC3);

                if (prologueLooksValid)
                {
                    IntPtr addr = new IntPtr(fallbackVa);
                    RynthLog.Compat(
                        $"HookResolver[{functionName}]: PATTERN MISS — falling back to hardcoded VA 0x{fallbackVa:X8} " +
                        $"(prologue byte=0x{firstByte:X2}). The binary may have shifted; install proceeds but is RISKY.");
                    return new ResolveResult(addr, ResolveSource.FallbackVa, $"fallback-va(prologue=0x{firstByte:X2})");
                }

                RynthLog.Compat(
                    $"HookResolver[{functionName}]: UNAVAILABLE — pattern not found AND fallback VA 0x{fallbackVa:X8} " +
                    $"prologue is invalid (byte=0x{firstByte:X2}).");
            }
            else
            {
                RynthLog.Compat(
                    $"HookResolver[{functionName}]: UNAVAILABLE — pattern not found AND fallback VA 0x{fallbackVa:X8} " +
                    $"is outside the readable .text window.");
            }

            return new ResolveResult(IntPtr.Zero, ResolveSource.Failed, "unavailable");
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"HookResolver[{functionName}]: UNAVAILABLE — exception during resolve: {ex.GetType().Name}: {ex.Message}");
            return new ResolveResult(IntPtr.Zero, ResolveSource.Failed, $"exception:{ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Resolve a DATA/global address (.data/.rdata — singleton ptr, selection id, string
    /// null-buffer, fn-ptr slot, …) by CODE-XREF: pattern-scan .text for a unique instruction
    /// that references the address as an absolute operand (the 4 operand bytes wildcarded in
    /// <paramref name="codePattern"/>), then read the address back from that operand at
    /// <paramref name="operandOffset"/>. Robust if the global relocates across builds.
    ///
    /// Unlike function resolution this does NOT fail closed: the data VA is read/written by the
    /// caller, which assumes it always has one, so a miss falls back to the hardcoded VA (== old
    /// behavior) with a loud log rather than returning Zero.
    /// </summary>
    public static ResolveResult ResolveData(
        AcClientTextSection text, string name, byte?[] codePattern, int operandOffset, int fallbackVa)
    {
        try
        {
            int first = PatternScanner.FindPattern(text.Bytes, codePattern);
            if (first >= 0 && first + operandOffset + 4 <= text.Bytes.Length)
            {
                int next = PatternScanner.FindPatternInRegion(text.Bytes, codePattern, first + 1, text.Bytes.Length);
                if (next < 0)
                {
                    int operand = BitConverter.ToInt32(text.Bytes, first + operandOffset);
                    RynthLog.Info(
                        $"HookResolver[{name}]: RESOLVED via data-xref @ 0x{operand:X8} " +
                        $"(fallbackVa=0x{fallbackVa:X8}, delta={(operand - fallbackVa):+#;-#;0}).");
                    return new ResolveResult(new IntPtr(operand), ResolveSource.PatternScan, "data-xref");
                }
                RynthLog.Compat(
                    $"HookResolver[{name}]: data-xref matched MULTIPLE .text sites — falling back to hardcoded VA 0x{fallbackVa:X8} (RISKY).");
            }
            else
            {
                RynthLog.Compat(
                    $"HookResolver[{name}]: data-xref pattern not found — falling back to hardcoded VA 0x{fallbackVa:X8} (RISKY).");
            }
            return new ResolveResult(new IntPtr(fallbackVa), ResolveSource.FallbackVa, "data-fallback");
        }
        catch (Exception ex)
        {
            RynthLog.Compat($"HookResolver[{name}]: data-xref exception {ex.GetType().Name}: {ex.Message} — fallback VA 0x{fallbackVa:X8}.");
            return new ResolveResult(new IntPtr(fallbackVa), ResolveSource.FallbackVa, "data-fallback-exc");
        }
    }
}
