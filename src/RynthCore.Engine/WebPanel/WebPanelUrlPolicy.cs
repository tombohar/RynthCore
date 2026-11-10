using System;
using System.Collections.Generic;
using System.Text;

namespace RynthCore.Engine.WebPanel;

/// <summary>
/// Pure URL rules for the web panel: the navigation allowlist, the "safe to hand to the system
/// browser" test, and log redaction. No Win32, no WebView2.
/// </summary>
internal static class WebPanelUrlPolicy
{
    public const int MaxEntries = 16, MaxEntryLength = 253, MaxUrlBytes = 4096;

    /// <summary>
    /// Parses a '\n'-separated allowlist (blank lines ignored, '\r' tolerated). An entry is
    /// "host" (exact) or "*.suffix" (any subdomain depth, not the suffix itself; the suffix must
    /// contain a dot). No scheme, path, port or other '*'. False on 0 or more than 16 entries or
    /// any bad entry.
    /// </summary>
    public static bool TryParseAllowlist(string? text, out string[] entries)
    {
        entries = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(text)) return false;

        var list = new List<string>();
        foreach (string raw in text.Split('\n'))
        {
            string e = raw.Trim().ToLowerInvariant();
            if (e.Length == 0) continue;
            if (list.Count >= MaxEntries) return false;
            if (!IsValidEntry(e)) return false;
            list.Add(e);
        }
        if (list.Count == 0) return false;
        entries = list.ToArray();
        return true;
    }

    private static bool IsValidEntry(string e)
    {
        if (e.Length > MaxEntryLength) return false;
        string host = e;
        if (e.StartsWith("*.", StringComparison.Ordinal))
        {
            host = e.Substring(2);
            if (host.IndexOf('.') < 0) return false; // "*.com" would allow a whole TLD
        }
        return IsValidHostName(host);
    }

    private static bool IsValidHostName(string host)
    {
        if (host.Length == 0) return false;
        foreach (string label in host.Split('.'))
        {
            if (label.Length == 0 || label.Length > 63) return false;
            if (label[0] == '-' || label[^1] == '-') return false;
            foreach (char c in label)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-';
                if (!ok) return false;
            }
        }
        return true;
    }

    /// <summary>
    /// https only, no userinfo, default port; host compared via Uri.IdnHost, case-insensitive,
    /// one trailing dot stripped. "about:blank" is allowed.
    /// </summary>
    public static bool IsAllowed(string? url, IReadOnlyList<string> entries)
    {
        if (string.IsNullOrEmpty(url)) return false;
        if (Encoding.UTF8.GetByteCount(url) > MaxUrlBytes) return false;
        if (string.Equals(url, "about:blank", StringComparison.OrdinalIgnoreCase)) return true;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        if (uri.UserInfo.Length != 0) return false;
        if (!uri.IsDefaultPort) return false;

        string host;
        try { host = uri.IdnHost.ToLowerInvariant(); }
        catch (UriFormatException) { return false; } // invalid IDN characters: fail closed
        if (host.EndsWith('.')) host = host.Substring(0, host.Length - 1);
        if (host.Length == 0) return false;

        foreach (string entry in entries)
        {
            if (entry.StartsWith("*.", StringComparison.Ordinal))
            {
                string suffix = entry.Substring(1); // ".example.com"
                if (host.Length > suffix.Length && host.EndsWith(suffix, StringComparison.Ordinal)) return true;
            }
            else if (host == entry)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Absolute http/https without userinfo: the only URLs that may reach ShellExecute.</summary>
    public static bool IsBrowserSafe(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        if (uri.UserInfo.Length != 0) return false;
        return uri.Host.Length != 0;
    }

    /// <summary>"scheme://host[:port]/path" with no userinfo, query or fragment; "&lt;invalid url&gt;" if unparseable.</summary>
    public static string Redact(string? url)
    {
        const string Invalid = "<invalid url>";
        if (string.IsNullOrEmpty(url)) return Invalid;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return Invalid;
        if (uri.Host.Length == 0) return Invalid;
        string port = uri.IsDefaultPort ? "" : ":" + uri.Port;
        return uri.Scheme + "://" + uri.Host + port + uri.AbsolutePath;
    }
}
