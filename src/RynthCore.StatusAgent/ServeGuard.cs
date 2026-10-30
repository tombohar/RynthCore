using System.Text;

namespace RynthCore.StatusAgent;

/// <summary>
/// The status server's guards for a bind other PCs can reach (LAN, Tailscale): a token long
/// enough to resist guessing, a lockout for an address that keeps guessing, bounded request
/// bodies, and client text made safe for the log. Pure code; compiled into tools/StatusAgentTests.
/// </summary>
internal static class ServeGuard
{
    /// <summary>Shortest ServeToken accepted when the prefix isn't loopback-only.</summary>
    public const int MinRemoteTokenLength = 16;

    // Request bodies: a command is a few hundred bytes, a WebRTC offer a few KB.
    public const int MaxCommandBodyBytes = 64 * 1024;
    public const int MaxOfferBodyBytes = 256 * 1024;
    public const int MaxClientLogBodyBytes = 16 * 1024;

    /// <summary>
    /// True when <paramref name="prefix"/> (an HttpListener prefix, "http://host:port/") only
    /// listens on this PC: 127.x.x.x, localhost or [::1]. "+", "*", a LAN address or a name = false.
    /// </summary>
    public static bool IsLoopbackPrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return false;
        int scheme = prefix.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0) return false;
        string rest = prefix.Substring(scheme + 3);
        string host;
        if (rest.StartsWith('['))
        {
            int close = rest.IndexOf(']');
            if (close < 0) return false;
            host = rest.Substring(1, close - 1);
        }
        else
        {
            int end = rest.IndexOfAny(new[] { ':', '/' });
            host = end < 0 ? rest : rest.Substring(0, end);
        }
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "::1") return true;
        return System.Net.IPAddress.TryParse(host, out var ip)
            && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            && System.Net.IPAddress.IsLoopback(ip);
    }

    /// <summary>Why the server must not start with this token on this prefix, or null when it may.</summary>
    public static string? TokenProblem(string prefix, string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return "no ServeToken set";
        if (!IsLoopbackPrefix(prefix) && token.Trim().Length < MinRemoteTokenLength)
            return $"ServeToken must be at least {MinRemoteTokenLength} characters when ServePrefix isn't 127.0.0.1/localhost";
        return null;
    }

    /// <summary>
    /// Reads a request body of at most <paramref name="maxBytes"/>. False (nothing read past the
    /// limit) when the declared or actual length is over it.
    /// </summary>
    public static bool TryReadBounded(Stream input, long declaredLength, int maxBytes, Encoding? encoding, out string text)
    {
        text = "";
        if (declaredLength > maxBytes) return false;
        var buffer = new byte[Math.Min(maxBytes + 1, 64 * 1024)];
        using var collected = new MemoryStream();
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (collected.Length + read > maxBytes) return false;
            collected.Write(buffer, 0, read);
        }
        text = (encoding ?? Encoding.UTF8).GetString(collected.GetBuffer(), 0, (int)collected.Length);
        return true;
    }

    /// <summary>Client-supplied text for one log line: control characters (CR/LF included) become spaces, capped at <paramref name="max"/>.</summary>
    public static string LogSafe(string? text, int max = 200)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(Math.Min(text.Length, max) + 3);
        foreach (char c in text)
        {
            if (sb.Length >= max) { sb.Append("..."); break; }
            sb.Append(char.IsControl(c) ? ' ' : c);
        }
        return sb.ToString();
    }
}

/// <summary>
/// Locks out an address after too many failed token checks: <see cref="MaxFailures"/> within
/// <see cref="Window"/> blocks it for <see cref="Lockout"/> (even with the right token, so a
/// guesser learns nothing). Loopback is never locked out (the owner's own tools). Thread-safe.
/// </summary>
internal sealed class AuthThrottle
{
    public const int MaxFailures = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan Lockout = TimeSpan.FromMinutes(5);
    private const int MaxTracked = 4096;

    private sealed class Entry { public int Failures; public DateTime WindowStart; public DateTime BlockedUntil; }

    private readonly Dictionary<string, Entry> _byAddress = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>True while <paramref name="address"/> is locked out.</summary>
    public bool IsBlocked(System.Net.IPAddress? address, DateTime nowUtc)
    {
        if (address == null || System.Net.IPAddress.IsLoopback(address)) return false;
        lock (_gate)
            return _byAddress.TryGetValue(address.ToString(), out Entry? e) && e.BlockedUntil > nowUtc;
    }

    /// <summary>Counts a failed check; true when this one started a lockout (log it once).</summary>
    public bool Fail(System.Net.IPAddress? address, DateTime nowUtc)
    {
        if (address == null || System.Net.IPAddress.IsLoopback(address)) return false;
        lock (_gate)
        {
            string key = address.ToString();
            if (!_byAddress.TryGetValue(key, out Entry? e))
            {
                if (_byAddress.Count >= MaxTracked) Prune(nowUtc);
                e = new Entry { WindowStart = nowUtc };
                _byAddress[key] = e;
            }
            if (nowUtc - e.WindowStart > Window) { e.WindowStart = nowUtc; e.Failures = 0; }
            if (++e.Failures < MaxFailures) return false;
            e.Failures = 0;
            e.WindowStart = nowUtc;
            e.BlockedUntil = nowUtc + Lockout;
            return true;
        }
    }

    /// <summary>A good token clears the address's count.</summary>
    public void Succeed(System.Net.IPAddress? address)
    {
        if (address == null) return;
        lock (_gate) _byAddress.Remove(address.ToString());
    }

    private void Prune(DateTime nowUtc)
    {
        foreach (var key in _byAddress.Where(kv => kv.Value.BlockedUntil <= nowUtc && nowUtc - kv.Value.WindowStart > Window)
                                      .Select(kv => kv.Key).ToList())
            _byAddress.Remove(key);
        // Still full (a flood of blocked addresses): drop the oldest windows rather than grow without bound.
        if (_byAddress.Count >= MaxTracked)
            foreach (var key in _byAddress.OrderBy(kv => kv.Value.WindowStart).Take(_byAddress.Count / 4).Select(kv => kv.Key).ToList())
                _byAddress.Remove(key);
    }
}
