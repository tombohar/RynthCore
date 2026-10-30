// ============================================================================
//  RynthCore.Engine - Report/ProblemReportCore.cs
//  The pure half of "Report a problem" (ProblemReport.cs is the engine half):
//  the text limits, the log tail, the request body, the client's cooldown and
//  the reading of the server's answer. No engine state, no AC, no network, so
//  tools/ProblemReportTests compiles this file and checks it offline.
//
//  Wire format (POST https://aelrynth.com/rynth/report, application/json; the
//  server side is C:\Aeshnidae\ops\report-endpoint\app.py, which refuses any
//  other shape):
//    { "schema": 1,
//      "description": "...", "doing": "...",            the player's text
//      "character": "...", "world": "...", "connect": "host:port", "aelrynth": true,
//      "rynthcore": "2026.10.9.3 (194ea47)", "api": 81, "generation": 1,
//      "rynthai": "2026.10.9.3 (e6fffb0)" | "",
//      "plugins": [ { "name": "RynthAi", "version": "..." } ],
//      "landcell": "0x12340105" | "",
//      "pid": 1234, "clientTime": "2026-10-11T12:00:00Z",
//      "log": null | { "name": "RynthCore.1234.log", "lines": 2000, "bytes": 214000,
//                      "gzip": "<base64 of the gzipped tail>" } }
//  The server answers JSON: { "ok": true, "id": "R-..." } or { "error": "..." }.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RynthCore.Engine.Report;

/// <summary>What the report says about the client, gathered when it is sent.</summary>
internal sealed class ReportContext
{
    public string Character = "";
    public string World = "";
    public string Connect = "";
    public bool Aelrynth;
    public string RynthCoreVersion = "";
    public int ApiVersion;
    public int Generation;
    public string RynthAiVersion = "";
    public List<(string Name, string Version)> Plugins = new();
    public uint Landcell;
    public int Pid;
}

/// <summary>The end of a log file, exactly as it will be sent (and shown by View).</summary>
internal sealed class LogTail
{
    public string FileName = "";
    public byte[] Text = Array.Empty<byte>();
    public int Lines;
    /// <summary>The tail began in the rotated .old file (the current one was shorter than asked).</summary>
    public bool IncludesOld;
    public DateTime TakenLocal;
    private byte[]? _gzip;

    /// <summary>The gzipped text (made once).</summary>
    public byte[] Gzip => _gzip ??= ProblemReportCore.Gzip(Text);
}

internal static class ProblemReportCore
{
    public const string DefaultEndpoint = "https://aelrynth.com/rynth/report";

    // Text limits (UTF-8 bytes). The server takes no more (app.py MAX_DESCRIPTION / MAX_DOING).
    public const int MaxDescriptionBytes = 2500;
    public const int MaxDoingBytes = 1500;
    public const int MinDescriptionChars = 3;

    // The log: the last 2,000 lines, at most 1.5 MB of them (the end is kept). The server
    // takes 2 MB of gzip; 1.5 MB of log text gzips to a few hundred KB.
    public const int TailLines = 2000;
    public const int MaxTailBytes = 1_500_000;
    public const int MaxGzipBytes = 2_000_000;

    // Short strings the server checks the same way.
    public const int MaxShortField = 64;
    public const int MaxConnectField = 100;
    public const int MaxPlugins = 32;

    /// <summary>One report per minute per client; a send that never reached the server waits only this.</summary>
    public const int CooldownMs = 60_000;
    public const int RetryAfterFailureMs = 10_000;

    // ── Text ─────────────────────────────────────────────────────────────

    /// <summary>
    /// <paramref name="text"/> trimmed, CR LF made LF, other control characters (except tab)
    /// dropped, then cut to <paramref name="maxBytes"/> of UTF-8 at a character boundary.
    /// </summary>
    public static string CleanText(string? text, int maxBytes)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (char c in text.Replace("\r\n", "\n").Replace('\r', '\n'))
        {
            if (c == '\n' || c == '\t' || !char.IsControl(c)) sb.Append(c);
        }
        return TrimUtf8(sb.ToString().Trim(), maxBytes);
    }

    /// <summary>A one-line field: control characters dropped, trimmed, cut to <paramref name="maxChars"/>.</summary>
    public static string CleanShort(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
            if (!char.IsControl(c)) sb.Append(c);
        string s = sb.ToString().Trim();
        if (s.Length > maxChars)
        {
            int n = maxChars;
            if (char.IsHighSurrogate(s[n - 1])) n--;
            s = s.Substring(0, n);
        }
        return s;
    }

    /// <summary>Cuts <paramref name="s"/> to at most <paramref name="maxBytes"/> of UTF-8, never inside a character.</summary>
    public static string TrimUtf8(string s, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(s) <= maxBytes) return s;
        int bytes = 0, i = 0;
        while (i < s.Length)
        {
            int len = char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
            int cb = Encoding.UTF8.GetByteCount(s.AsSpan(i, len));
            if (bytes + cb > maxBytes) break;
            bytes += cb;
            i += len;
        }
        return s.Substring(0, i);
    }

    /// <summary>Why <paramref name="description"/> can't be sent, or "" when it can.</summary>
    public static string ValidateDescription(string description)
    {
        string d = CleanText(description, MaxDescriptionBytes);
        if (d.Length == 0) return "Write what went wrong first.";
        if (d.Length < MinDescriptionChars) return "Say a little more about what went wrong.";
        return "";
    }

    // ── The log tail ─────────────────────────────────────────────────────

    /// <summary>
    /// The last <paramref name="maxLines"/> lines of <paramref name="path"/> (at most
    /// <paramref name="maxBytes"/>; a cut first line is dropped), read while the engine
    /// may be appending to it: the file is opened for shared read/write and read up to the
    /// length it had when opened. When it holds fewer lines than asked and
    /// <paramref name="olderPath"/> exists (the rotated .old), the rest comes from there.
    /// Null when the file doesn't exist. Throws on other I/O errors.
    /// </summary>
    public static LogTail? ReadTail(string path, string? olderPath, int maxLines = TailLines, int maxBytes = MaxTailBytes,
        DateTime? now = null, IReadOnlyList<(string Name, string Replacement)>? hide = null)
    {
        if (!File.Exists(path)) return null;
        byte[] current = TailBytes(path, maxLines, maxBytes, out int lines);
        bool includesOld = false;
        if (lines < maxLines && current.Length < maxBytes && !string.IsNullOrEmpty(olderPath) && File.Exists(olderPath))
        {
            byte[] older = TailBytes(olderPath, maxLines - lines, maxBytes - current.Length, out int olderLines);
            if (older.Length > 0)
            {
                // The older file's last line may lack its newline; keep the lines apart.
                bool needNewline = older[^1] != (byte)'\n';
                var merged = new byte[older.Length + (needNewline ? 1 : 0) + current.Length];
                Buffer.BlockCopy(older, 0, merged, 0, older.Length);
                if (needNewline) merged[older.Length] = (byte)'\n';
                Buffer.BlockCopy(current, 0, merged, merged.Length - current.Length, current.Length);
                current = merged;
                lines += olderLines;
                includesOld = true;
            }
        }
        current = Redact(current, hide);
        return new LogTail
        {
            FileName = Path.GetFileName(path),
            Text = current,
            Lines = lines,
            IncludesOld = includesOld,
            TakenLocal = now ?? DateTime.Now,
        };
    }

    /// <summary>The last <paramref name="maxLines"/> whole lines of a file, at most <paramref name="maxBytes"/>.</summary>
    internal static byte[] TailBytes(string path, int maxLines, int maxBytes, out int lines)
    {
        lines = 0;
        if (maxLines <= 0 || maxBytes <= 0) return Array.Empty<byte>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1, FileOptions.None);
        long length = fs.Length;
        if (length == 0) return Array.Empty<byte>();

        // Read backwards in chunks until there are enough newlines or the byte cap is reached.
        const int Chunk = 64 * 1024;
        long start = length;
        var chunks = new List<byte[]>();
        int newlines = 0;
        long have = 0;
        // A last line still being written (no newline yet) counts as a line; a trailing newline doesn't start one.
        while (start > 0 && have < maxBytes)
        {
            int size = (int)Math.Min(Chunk, Math.Min(start, maxBytes - have + 1));
            start -= size;
            var buf = new byte[size];
            fs.Position = start;
            int read = 0;
            while (read < size)
            {
                int n = fs.Read(buf, read, size - read);
                if (n <= 0) break;   // the file shrank under us (rotated): use what came
                read += n;
            }
            if (read < size) Array.Resize(ref buf, read);
            chunks.Insert(0, buf);
            have += buf.Length;
            foreach (byte b in buf) if (b == (byte)'\n') newlines++;
            if (newlines > maxLines) break;
        }

        var all = new byte[have];
        int at = 0;
        foreach (byte[] c in chunks) { Buffer.BlockCopy(c, 0, all, at, c.Length); at += c.Length; }

        int end = all.Length;
        int scanEnd = end > 0 && all[end - 1] == (byte)'\n' ? end - 1 : end;
        // Walk back over maxLines newlines (from the end, not counting the trailing one).
        int from = 0;
        int seen = 0;
        bool cut = false;
        for (int i = scanEnd - 1; i >= 0; i--)
        {
            if (all[i] != (byte)'\n') continue;
            seen++;
            if (seen == maxLines) { from = i + 1; cut = true; break; }
        }
        if (!cut && start > 0)
        {
            // Stopped by the byte cap mid-file: drop the partial first line.
            int nl = Array.IndexOf(all, (byte)'\n');
            from = nl < 0 ? end : nl + 1;
        }
        // The byte cap: keep the end, starting at a line.
        if (end - from > maxBytes)
        {
            int nl = Array.IndexOf(all, (byte)'\n', end - maxBytes);
            from = nl < 0 ? end : nl + 1;
        }
        byte[] result = from == 0 ? all : all.AsSpan(from, end - from).ToArray();
        lines = CountLines(result);
        return result;
    }

    // SessionStateRegistry logs "account='<login name>'" at login; the login name is half a credential.
    private static readonly Regex AccountField = new(@"account='[^'\r\n]*'", RegexOptions.CultureInvariant);
    // Paths under the Windows profile (C:\Users\<name>\AppData\...) carry the Windows user name.
    private static readonly Regex ProfilePath = new(@"(?i)\b([A-Z]:[\\/]Users[\\/])[^\\/\r\n'""<>|]+", RegexOptions.CultureInvariant);
    public const string HiddenAccount = "<account>";
    public const string HiddenUser = "<user>";

    /// <summary>
    /// The log text with private names hidden: every account='...' field and every occurrence (any
    /// case) of each name in <paramref name="hide"/> (3 characters or more) become its replacement
    /// (&lt;account&gt;, &lt;user&gt;, ...), and the folder after X:\Users\ becomes &lt;user&gt;.
    /// Done before the size is shown and before View, so View shows what is sent.
    /// </summary>
    public static byte[] Redact(byte[] text, IReadOnlyList<(string Name, string Replacement)>? hide)
    {
        if (text.Length == 0) return text;
        string s = Encoding.UTF8.GetString(text);
        string r = AccountField.Replace(s, "account='" + HiddenAccount + "'");
        r = ProfilePath.Replace(r, "$1" + HiddenUser);
        if (hide != null)
        {
            foreach (var (name, replacement) in hide)
            {
                string w = (name ?? "").Trim();
                if (w.Length < 3) continue;
                r = Regex.Replace(r, Regex.Escape(w), replacement.Replace("$", "$$"), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
        }
        return string.Equals(r, s, StringComparison.Ordinal) ? text : Encoding.UTF8.GetBytes(r);
    }

    /// <summary>Lines in a text: newlines, plus one for a last line without one.</summary>
    public static int CountLines(ReadOnlySpan<byte> text)
    {
        if (text.Length == 0) return 0;
        int n = 0;
        foreach (byte b in text) if (b == (byte)'\n') n++;
        return text[^1] == (byte)'\n' ? n : n + 1;
    }

    public static byte[] Gzip(byte[] data)
    {
        using var ms = new MemoryStream(data.Length / 4 + 64);
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    // ── The request body ─────────────────────────────────────────────────

    /// <summary>The JSON body (see the file header). <paramref name="log"/> null = not attached.</summary>
    public static byte[] BuildPayload(string description, string doing, ReportContext ctx, LogTail? log, DateTime utcNow)
    {
        using var ms = new MemoryStream(1024 + (log == null ? 0 : log.Gzip.Length * 4 / 3 + 256));
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", 1);
            w.WriteString("description", CleanText(description, MaxDescriptionBytes));
            w.WriteString("doing", CleanText(doing, MaxDoingBytes));
            w.WriteString("character", CleanShort(ctx.Character, MaxShortField));
            w.WriteString("world", CleanShort(ctx.World, MaxShortField));
            w.WriteString("connect", CleanShort(ctx.Connect, MaxConnectField));
            w.WriteBoolean("aelrynth", ctx.Aelrynth);
            w.WriteString("rynthcore", CleanShort(ctx.RynthCoreVersion, MaxShortField));
            w.WriteNumber("api", Math.Clamp(ctx.ApiVersion, 0, 1_000_000));
            w.WriteNumber("generation", Math.Clamp(ctx.Generation, 0, 1_000_000));
            w.WriteString("rynthai", CleanShort(ctx.RynthAiVersion, MaxShortField));
            w.WriteStartArray("plugins");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, version) in ctx.Plugins)
            {
                string n = PluginName(name);
                if (n.Length == 0 || !seen.Add(n)) continue;
                if (seen.Count > MaxPlugins) break;
                w.WriteStartObject();
                w.WriteString("name", n);
                w.WriteString("version", CleanShort(version, MaxShortField));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteString("landcell", ctx.Landcell != 0 ? "0x" + ctx.Landcell.ToString("X8", CultureInfo.InvariantCulture) : "");
            w.WriteNumber("pid", Math.Max(0, ctx.Pid));
            w.WriteString("clientTime", utcNow.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            if (log == null || log.Text.Length == 0)
            {
                w.WriteNull("log");
            }
            else
            {
                w.WriteStartObject("log");
                w.WriteString("name", CleanShort(log.FileName, MaxShortField));
                w.WriteNumber("lines", log.Lines);
                w.WriteNumber("bytes", log.Text.Length);
                w.WriteBase64String("gzip", log.Gzip);
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    /// <summary>A plugin name as the server accepts it: letters, digits, space . _ - (40 at most).</summary>
    public static string PluginName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var sb = new StringBuilder();
        foreach (char c in name)
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or ' ' or '.' or '_' or '-')
                sb.Append(c);
        string s = sb.ToString().Trim();
        return s.Length > 40 ? s.Substring(0, 40).Trim() : s;
    }

    // ── The endpoint ─────────────────────────────────────────────────────

    /// <summary>
    /// The URL reports go to: <paramref name="overrideUrl"/> (engine.json "ReportEndpoint", for
    /// testing) when it is https, or http to this PC; otherwise the built-in one.
    /// </summary>
    public static string ResolveEndpoint(string? overrideUrl, out string? rejected)
    {
        rejected = null;
        if (string.IsNullOrWhiteSpace(overrideUrl)) return DefaultEndpoint;
        if (Uri.TryCreate(overrideUrl.Trim(), UriKind.Absolute, out Uri? u) && string.IsNullOrEmpty(u.UserInfo)
            && (u.Scheme == Uri.UriSchemeHttps
                || (u.Scheme == Uri.UriSchemeHttp && (u.IsLoopback || u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)))))
            return u.AbsoluteUri;
        rejected = overrideUrl;
        return DefaultEndpoint;
    }

    // ── The answer ───────────────────────────────────────────────────────

    /// <summary>
    /// What the player is told about the server's answer. True when the report was taken;
    /// <paramref name="id"/> is its id (may be empty). Never throws.
    /// </summary>
    public static bool ReadResponse(int status, string? body, out string message, out string id)
    {
        id = "";
        string serverError = "";
        try
        {
            if (!string.IsNullOrWhiteSpace(body) && body.TrimStart().StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                        id = CleanShort(idEl.GetString(), 40);
                    if (root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String)
                        serverError = CleanShort(errEl.GetString(), 200);
                }
            }
        }
        catch (JsonException) { }

        if (status is >= 200 and < 300)
        {
            message = id.Length > 0 ? $"Sent. Thank you! (report {id})" : "Sent. Thank you!";
            return true;
        }
        message = status switch
        {
            429 => serverError.Length > 0 ? serverError : "Too many reports from here right now. Please try again later.",
            413 => "The report is too large to send. Untick the log and try again.",
            400 or 415 or 422 => serverError.Length > 0 ? "The server refused the report: " + serverError : "The server refused the report.",
            404 => "The report service isn't available yet (HTTP 404).",
            >= 500 => "The report service has a problem right now (HTTP " + status + "). Please try again later.",
            _ => serverError.Length > 0 ? $"HTTP {status}: {serverError}" : $"The report service answered HTTP {status}.",
        };
        return false;
    }

    // ── The cooldown ─────────────────────────────────────────────────────

    /// <summary>Milliseconds until the next report may be sent (0 = now). <paramref name="nextAllowedMs"/> is a TickCount64 value.</summary>
    public static long CooldownLeft(long nowMs, long nextAllowedMs) => Math.Max(0, nextAllowedMs - nowMs);

    /// <summary>"42 s" / "1 min 5 s".</summary>
    public static string FormatWait(long ms)
    {
        long s = (ms + 999) / 1000;
        return s < 60 ? $"{s} s" : $"{s / 60} min {s % 60} s";
    }

    /// <summary>"812 bytes" / "214 KB" / "1.4 MB".</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " bytes";
        if (bytes < 1024 * 1024) return ((bytes + 512) / 1024).ToString(CultureInfo.InvariantCulture) + " KB";
        return (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
    }

    /// <summary>The panel's line about the log: "RynthCore.1234.log: 1,987 lines, 214 KB (31 KB compressed), taken 14:02:11".</summary>
    public static string DescribeTail(LogTail t) =>
        $"{t.FileName}{(t.IncludesOld ? " (+ .old)" : "")}: {t.Lines.ToString("N0", CultureInfo.InvariantCulture)} lines, " +
        $"{FormatSize(t.Text.Length)} ({FormatSize(t.Gzip.Length)} compressed), taken {t.TakenLocal:HH:mm:ss}";
}
