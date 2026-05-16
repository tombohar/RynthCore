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
                int chosen = firstMatch;
                string detail;

                int next = PatternScanner.FindPatternInRegion(
                    text.Bytes, pattern, firstMatch + 1, text.Bytes.Length);
                if (next < 0)
                {
                    detail = "pattern-unique";
                }
                else
                {
                    // Multiple matches — pick the one nearest the fallback VA.
                    // If the fallback VA itself is one of the matches, prefer it;
                    // otherwise our recorded VA is the best disambiguator.
                    int fallbackOff = fallbackVa - text.TextBaseVa;
                    int distFirst = Math.Abs(firstMatch - fallbackOff);
                    int distNext = Math.Abs(next - fallbackOff);
                    chosen = distFirst <= distNext ? firstMatch : next;
                    detail = $"pattern-multimatch(near-fallback {chosen:X})";
                    RynthLog.Compat(
                        $"HookResolver[{functionName}]: pattern matched MULTIPLE locations " +
                        $"(first=0x{text.TextBaseVa + firstMatch:X8}, next=0x{text.TextBaseVa + next:X8}) — " +
                        $"chose nearest to fallback 0x{fallbackVa:X8}.");
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
}
