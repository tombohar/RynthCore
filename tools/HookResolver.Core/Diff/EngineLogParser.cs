using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RynthCore.HookResolver.Core.Diff;

public enum EngineResolutionKind
{
    PatternResolved,
    FallbackResolved,
    Unavailable
}

public sealed record EngineResolution(
    string SymbolId,
    int? Va,
    EngineResolutionKind Kind,
    string Detail);

public static class EngineLogParser
{
    public const string DefaultLogPath = @"C:\Games\RynthCore\Logs\RynthCore.log";

    private static readonly Regex Resolved = new(
        @"HookResolver\[(?<sym>[^\]]+)\]:\s+RESOLVED\s+via\s+(?<detail>\S+)\s+@\s+0x(?<addr>[0-9A-Fa-f]+)",
        RegexOptions.Compiled);

    private static readonly Regex Fallback = new(
        @"HookResolver\[(?<sym>[^\]]+)\]:\s+PATTERN MISS.*?VA\s+0x(?<addr>[0-9A-Fa-f]+)",
        RegexOptions.Compiled);

    private static readonly Regex Unavailable = new(
        @"HookResolver\[(?<sym>[^\]]+)\]:\s+UNAVAILABLE\b",
        RegexOptions.Compiled);

    public static async Task<IReadOnlyDictionary<string, EngineResolution>> ParseAsync(
        string? path = null, CancellationToken ct = default)
    {
        path ??= DefaultLogPath;
        var result = new Dictionary<string, EngineResolution>();
        if (!File.Exists(path)) return result;

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (TryMatch(Resolved, line, out var sym, out var addr, out var detail))
            {
                result[sym] = new EngineResolution(sym, addr, EngineResolutionKind.PatternResolved, detail);
                continue;
            }

            if (TryMatch(Fallback, line, out sym, out addr, out _))
            {
                result[sym] = new EngineResolution(sym, addr, EngineResolutionKind.FallbackResolved, "fallback-va");
                continue;
            }

            var unavailableMatch = Unavailable.Match(line);
            if (unavailableMatch.Success)
            {
                string s = unavailableMatch.Groups["sym"].Value;
                result[s] = new EngineResolution(s, null, EngineResolutionKind.Unavailable, "unavailable");
            }
        }

        return result;
    }

    private static bool TryMatch(Regex rx, string line, out string symbol, out int va, out string detail)
    {
        symbol = string.Empty;
        detail = string.Empty;
        va = 0;
        var m = rx.Match(line);
        if (!m.Success) return false;
        symbol = m.Groups["sym"].Value;
        va = int.Parse(m.Groups["addr"].Value, System.Globalization.NumberStyles.HexNumber);
        if (m.Groups["detail"].Success) detail = m.Groups["detail"].Value;
        return true;
    }
}
