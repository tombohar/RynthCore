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
    public const string LogDirectory = @"C:\Games\RynthCore\Logs";
    public const string DefaultLogPath = LogDirectory + @"\RynthCore.log";

    /// <summary>
    /// HookResolver lines are engine-origin, so since the per-client log split
    /// they land in RynthCore.&lt;pid&gt;.log, not the shared RynthCore.log. Pick the
    /// newest per-PID session log if any exist; otherwise fall back to the shared
    /// file (and legacy single-file layouts).
    /// </summary>
    public static string ResolveDefaultLogPath()
    {
        try
        {
            if (Directory.Exists(LogDirectory))
            {
                string? newest = null;
                System.DateTime newestTime = System.DateTime.MinValue;
                foreach (string f in Directory.GetFiles(LogDirectory, "RynthCore.*.log"))
                {
                    var t = File.GetLastWriteTime(f);
                    if (t > newestTime) { newestTime = t; newest = f; }
                }
                if (newest != null) return newest;
            }
        }
        catch { /* fall through to the shared path */ }
        return DefaultLogPath;
    }

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
        path ??= ResolveDefaultLogPath();
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
