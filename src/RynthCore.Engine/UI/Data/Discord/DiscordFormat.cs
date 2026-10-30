// ============================================================================
//  RynthCore.Engine - UI/Data/Discord/DiscordFormat.cs
//  Pure helpers for the Discord relay: Markdown escaping, line formatting,
//  message packing, credential-shape checks and the keyword filter.
//  No I/O; covered by
//  tools/ChatDiscordTests.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RynthCore.Engine.UI.Data.Discord;

internal static class DiscordFormat
{
    /// <summary>Discord rejects message content over 2000 characters.</summary>
    public const int MaxContent = 2000;

    /// <summary>Longest single relayed line; longer chat is cut with an ellipsis.</summary>
    public const int MaxLine = 1900;

    // discord.com / discordapp.com, optional ptb. / canary., optional /v{n}, then /webhooks/{id}/{token}.
    private static readonly Regex RxWebhook = new(
        @"^https://(?:(?:ptb|canary)\.)?discord(?:app)?\.com/api/(?:v\d{1,2}/)?webhooks/(?<id>\d{15,22})/(?<token>[A-Za-z0-9_\-]{30,100})/?$",
        RegexOptions.CultureInvariant);

    private static readonly Regex RxSnowflake = new(@"^\d{15,22}$", RegexOptions.CultureInvariant);

    // Bot tokens are three base64url segments separated by dots.
    private static readonly Regex RxBotToken = new(@"^[A-Za-z0-9_\-]{20,}\.[A-Za-z0-9_\-]{4,}\.[A-Za-z0-9_\-]{20,}$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Checks a pasted webhook URL. Only Discord's own webhook endpoints are accepted, so a typo
    /// can't send chat to some other server. Returns the URL without any query string.
    /// </summary>
    public static bool TryNormalizeWebhookUrl(string? url, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(url)) return false;
        string u = url.Trim();
        int q = u.IndexOfAny(new[] { '?', '#' });
        if (q >= 0) u = u.Substring(0, q);
        if (!RxWebhook.IsMatch(u)) return false;
        normalized = u.TrimEnd('/');
        return true;
    }

    public static bool IsSnowflake(string? id) => !string.IsNullOrWhiteSpace(id) && RxSnowflake.IsMatch(id.Trim());

    public static bool LooksLikeBotToken(string? token) => !string.IsNullOrWhiteSpace(token) && RxBotToken.IsMatch(token.Trim());

    /// <summary>
    /// Escapes inline Discord Markdown so chat text shows literally: emphasis, code, spoilers,
    /// quotes, masked links, and the &lt;@id&gt; / &lt;#id&gt; / &lt;t:..&gt; tags. Newlines become
    /// spaces. "@everyone" / "@here" also get a zero-width space; allowed_mentions already blocks
    /// the ping, this keeps the text from looking live.
    /// </summary>
    public static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(text.Length + 8);
        foreach (char c in text)
        {
            switch (c)
            {
                case '\\': case '*': case '_': case '~': case '`': case '|':
                case '>': case '<': case '[': case ']':
                    sb.Append('\\').Append(c);
                    break;
                case '\r': case '\n':
                    sb.Append(' ');
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.Replace("@everyone", "@\u200Beveryone").Replace("@here", "@\u200Bhere").ToString();
    }

    /// <summary>
    /// One relayed line: optional `HH:mm:ss`, optional [Character], **[Channel] Speaker:** message.
    /// A line with no channel or speaker is relayed as plain text.
    /// </summary>
    public static string FormatLine(DateTime time, string? channel, string? speaker, string message,
        bool timestamp, string? characterTag)
    {
        var sb = new StringBuilder(message.Length + 48);
        if (timestamp) sb.Append('`').Append(time.ToString("HH:mm:ss", CultureInfo.InvariantCulture)).Append("` ");
        if (!string.IsNullOrEmpty(characterTag)) sb.Append('\\').Append('[').Append(Escape(characterTag)).Append("\\] ");
        bool hasChannel = !string.IsNullOrEmpty(channel);
        bool hasSpeaker = !string.IsNullOrEmpty(speaker);
        if (hasChannel || hasSpeaker)
        {
            sb.Append("**");
            if (hasChannel) sb.Append("\\[").Append(Escape(channel)).Append("\\]");
            if (hasChannel && hasSpeaker) sb.Append(' ');
            if (hasSpeaker) sb.Append(Escape(speaker)).Append(':');
            sb.Append("** ");
        }
        string body = Escape(message);
        // Headings, lists and subtext only parse at the start of a line.
        if (sb.Length == 0) body = EscapeLineStart(body);
        sb.Append(body);
        return Truncate(sb.ToString(), MaxLine);
    }

    // "# x", "-# x", "- x", "+ x" and "1. x" are block syntax when they open a line.
    private static readonly Regex RxBlockStart = new(@"^(?:#{1,3}\s|-#\s|[-+]\s|\d+\.\s)", RegexOptions.CultureInvariant);

    private static string EscapeLineStart(string s) => RxBlockStart.IsMatch(s) ? "\\" + s : s;

    /// <summary>Cuts <paramref name="s"/> to <paramref name="max"/> characters, ending with an ellipsis.</summary>
    public static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        int cut = max - 1;
        // Don't leave a dangling escape backslash or split a surrogate pair at the cut.
        if (cut > 0 && char.IsHighSurrogate(s[cut - 1])) cut--;
        if (cut > 0 && s[cut - 1] == '\\') cut--;
        return s.Substring(0, cut) + "\u2026";
    }

    /// <summary>
    /// Takes lines from the front of <paramref name="lines"/> while they fit one message (newline
    /// separated, at most <see cref="MaxContent"/> characters). Always takes at least one line.
    /// </summary>
    public static string Pack(List<string> lines, out int taken)
    {
        taken = 0;
        if (lines.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        foreach (string line in lines)
        {
            string l = Truncate(line, MaxContent);
            int add = l.Length + (sb.Length > 0 ? 1 : 0);
            if (taken > 0 && sb.Length + add > MaxContent) break;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(l);
            taken++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Webhook display name: Discord refuses names containing "discord" or "clyde" and limits them
    /// to 1-80 characters. Returns empty (use the webhook's own name) when the name can't be used.
    /// </summary>
    public static string SafeWebhookName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        string n = name.Trim().TrimStart('+');
        if (n.Contains("discord", StringComparison.OrdinalIgnoreCase) || n.Contains("clyde", StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        if (n.Equals("everyone", StringComparison.OrdinalIgnoreCase) || n.Equals("here", StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        return n.Length > 80 ? n.Substring(0, 80) : n;
    }

    /// <summary>Splits a comma-separated list into trimmed, non-empty entries.</summary>
    public static List<string> SplitList(string? csv)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(csv)) return list;
        foreach (string part in csv.Split(','))
        {
            string p = part.Trim();
            if (p.Length > 0) list.Add(p);
        }
        return list;
    }

    /// <summary>True when <paramref name="text"/> contains any keyword (case-insensitive), or there are none.</summary>
    public static bool MatchesKeywords(string text, List<string> keywords)
    {
        if (keywords.Count == 0) return true;
        foreach (string k in keywords)
            if (text.Contains(k, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // Unanchored: a webhook's token after /webhooks/{id}/, and anything shaped like a bot token.
    private static readonly Regex RxWebhookTokenAnywhere = new(
        @"(?<=webhooks/\d{15,22}/)[A-Za-z0-9_\-]{30,100}", RegexOptions.CultureInvariant);
    private static readonly Regex RxBotTokenAnywhere = new(
        @"(?<![A-Za-z0-9_\-])[A-Za-z0-9_\-]{20,}\.[A-Za-z0-9_\-]{4,}\.[A-Za-z0-9_\-]{20,}(?![A-Za-z0-9_\-])", RegexOptions.CultureInvariant);

    /// <summary>
    /// Removes credentials from an error text before it is shown or logged: <paramref name="secret"/>
    /// (the webhook URL or bot token in use) whole, the webhook's bare token on its own, and anything
    /// else shaped like a webhook token or a bot token.
    /// </summary>
    public static string Redact(string text, string? secret)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        string t = text;
        if (!string.IsNullOrEmpty(secret))
        {
            string sec = secret.Trim();
            if (sec.Length > 0) t = t.Replace(sec, "***", StringComparison.Ordinal);
            Match m = RxWebhook.Match(sec.TrimEnd('/'));
            if (m.Success) t = t.Replace(m.Groups["token"].Value, "***", StringComparison.Ordinal);
        }
        t = RxWebhookTokenAnywhere.Replace(t, "***");
        return RxBotTokenAnywhere.Replace(t, "***");
    }
}
