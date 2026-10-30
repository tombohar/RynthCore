// ============================================================================
//  RynthCore.Engine - UI/Data/Discord/DiscordClient.cs
//  Posts relay messages to Discord. NativeAOT-safe: request JSON is written
//  with Utf8JsonWriter and responses are read with JsonDocument (no
//  reflection serialization).
//
//    webhook  POST {webhook url}                     (no auth header; the URL is the secret)
//    bot      POST https://discord.com/api/v10/channels/{id}/messages   Authorization: Bot {token}
//
//  Every message sets allowed_mentions.parse = [] (nobody is pinged) and
//  SUPPRESS_EMBEDS (no link previews). 429 returns the wait Discord asks for.
//  400 / 401 / 403 / 404 are permanent: the relay pauses the route so a bad
//  credential isn't retried (Discord temporarily bans clients that keep
//  sending invalid requests).
// ============================================================================

using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RynthCore.Engine.UI.Data.Discord;

/// <summary>
/// Outcome of one post. <see cref="RetryAfterMs"/> is how long to wait before the next post to
/// this route (rate limit or bucket exhausted); <see cref="Permanent"/> means stop until the
/// settings change.
/// </summary>
internal readonly record struct DiscordSendResult(bool Ok, int Status, int RetryAfterMs, bool Permanent, string Error)
{
    public static DiscordSendResult Fail(string error, bool permanent = false, int status = 0, int retryAfterMs = 0)
        => new(false, status, retryAfterMs, permanent, error);
}

internal static class DiscordClient
{
    public const string ApiBase = "https://discord.com/api/v10";

    /// <summary>MessageFlags.SUPPRESS_EMBEDS: chat links don't unfurl into previews.</summary>
    private const int FlagSuppressEmbeds = 1 << 2;

    // Built on first post, so an engine that never relays never opens a socket. Disposed by
    // Shutdown (engine shutdown / hot reload): the engine loads into a collectible context, and a
    // live client (its handler's pool timer, open connections, pending callbacks) must not
    // outlive the generation that made it. After Shutdown no new client is built.
    private static readonly object HttpLock = new();
    private static HttpClient? _http;
    private static bool _closed;

    private static HttpClient Http()
    {
        lock (HttpLock)
        {
            if (_closed) throw new ObjectDisposedException(nameof(DiscordClient), "the relay is shutting down");
            return _http ??= CreateHttp();
        }
    }

    /// <summary>Disposes the HTTP client (cancelling anything still using it) and refuses new posts. Engine shutdown.</summary>
    public static void Shutdown()
    {
        HttpClient? http;
        lock (HttpLock)
        {
            _closed = true;
            http = _http;
            _http = null;
        }
        try { http?.Dispose(); } catch (Exception) { }
    }

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AllowAutoRedirect = false,
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        // Discord asks bots to send "DiscordBot (url, version)"; webhooks accept it too.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DiscordBot (https://aelrynth.com, 1.0) RynthChat-Relay");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    /// <summary>
    /// Posts <paramref name="content"/> through a webhook. <paramref name="webhookUrl"/> must
    /// already be validated by <see cref="DiscordFormat.TryNormalizeWebhookUrl"/>. Never throws.
    /// </summary>
    public static Task<DiscordSendResult> PostWebhookAsync(string webhookUrl, string content, string displayName, CancellationToken ct)
    {
        if (!DiscordFormat.TryNormalizeWebhookUrl(webhookUrl, out string url))
            return Task.FromResult(DiscordSendResult.Fail("the saved webhook URL is not a Discord webhook URL", permanent: true));
        string body = BuildBody(content, DiscordFormat.SafeWebhookName(displayName));
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        return SendAsync(req, url, ct);
    }

    /// <summary>Posts <paramref name="content"/> as a bot to <paramref name="channelId"/>. Never throws.</summary>
    public static Task<DiscordSendResult> PostBotAsync(string botToken, string channelId, string content, CancellationToken ct)
    {
        if (!DiscordFormat.LooksLikeBotToken(botToken))
            return Task.FromResult(DiscordSendResult.Fail("the saved bot token doesn't look like a Discord bot token", permanent: true));
        if (!DiscordFormat.IsSnowflake(channelId))
            return Task.FromResult(DiscordSendResult.Fail("the channel ID must be the channel's number (Developer Mode > Copy Channel ID)", permanent: true));
        string body = BuildBody(content, string.Empty);
        var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/channels/{channelId.Trim()}/messages")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Authorization", "Bot " + botToken.Trim());
        return SendAsync(req, botToken.Trim(), ct);
    }

    /// <summary>{"content":..., "username":..., "allowed_mentions":{"parse":[]}, "flags":4}</summary>
    internal static string BuildBody(string content, string username)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("content", content);
            if (username.Length > 0) w.WriteString("username", username);
            w.WriteStartObject("allowed_mentions");
            w.WriteStartArray("parse");
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteNumber("flags", FlagSuppressEmbeds);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static async Task<DiscordSendResult> SendAsync(HttpRequestMessage req, string secret, CancellationToken ct)
    {
        try
        {
            using (req)
            using (var resp = await Http().SendAsync(req, ct).ConfigureAwait(false))
            {
                int status = (int)resp.StatusCode;
                string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                int bucketWait = BucketWaitMs(resp.Headers);

                if (resp.IsSuccessStatusCode) return new DiscordSendResult(true, status, bucketWait, false, string.Empty);

                if (status == 429)
                {
                    int wait = ParseRetryAfterMs(json, HeaderValue(resp.Headers, "Retry-After"));
                    return DiscordSendResult.Fail($"rate limited, waiting {wait / 1000.0:0.0}s", status: status, retryAfterMs: Math.Max(wait, bucketWait));
                }

                string msg = DiscordFormat.Redact(ErrorText(status, json), secret);
                return DiscordSendResult.Fail(msg, permanent: IsPermanent(status), status: status, retryAfterMs: bucketWait);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return DiscordSendResult.Fail("request timed out");
        }
        catch (OperationCanceledException)
        {
            return DiscordSendResult.Fail("cancelled");
        }
        catch (HttpRequestException ex)
        {
            return DiscordSendResult.Fail("network error: " + DiscordFormat.Redact(ex.Message, secret));
        }
        catch (Exception ex)
        {
            return DiscordSendResult.Fail($"{ex.GetType().Name}: {DiscordFormat.Redact(ex.Message, secret)}");
        }
    }

    /// <summary>
    /// 400 (bad body), 401 (bad token), 403 (no permission), 404 (webhook / channel deleted):
    /// retrying the same request can't succeed.
    /// </summary>
    internal static bool IsPermanent(int status) => status is 400 or 401 or 403 or 404;

    /// <summary>
    /// Wait for a 429, in ms: the JSON "retry_after" (seconds, may be fractional), else the
    /// Retry-After header (seconds), else 5 s. Clamped to 0.25 s .. 10 min.
    /// </summary>
    internal static int ParseRetryAfterMs(string? json, string? header)
    {
        double seconds = -1;
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("retry_after", out var ra)
                    && ra.ValueKind == JsonValueKind.Number)
                    seconds = ra.GetDouble();
            }
            catch (JsonException) { }
        }
        if (seconds < 0 && double.TryParse(header, NumberStyles.Float, CultureInfo.InvariantCulture, out double h)) seconds = h;
        if (seconds < 0) seconds = 5;
        return (int)Math.Clamp(seconds * 1000.0, 250.0, 600_000.0);
    }

    /// <summary>When the route's bucket is empty (X-RateLimit-Remaining: 0), ms until it resets.</summary>
    private static int BucketWaitMs(HttpResponseHeaders headers)
    {
        if (HeaderValue(headers, "X-RateLimit-Remaining") != "0") return 0;
        return double.TryParse(HeaderValue(headers, "X-RateLimit-Reset-After"), NumberStyles.Float, CultureInfo.InvariantCulture, out double s)
            ? (int)Math.Clamp(s * 1000.0, 0.0, 600_000.0)
            : 0;
    }

    private static string? HeaderValue(HttpResponseHeaders headers, string name)
    {
        if (!headers.TryGetValues(name, out var values)) return null;
        foreach (string v in values) return v.Trim();
        return null;
    }

    private static string ErrorText(int status, string json)
    {
        string detail = string.Empty;
        try
        {
            if (!string.IsNullOrWhiteSpace(json))
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("message", out var m))
                    detail = m.GetString() ?? string.Empty;
            }
        }
        catch (JsonException) { }

        string hint = status switch
        {
            401 => " (the token or webhook is invalid)",
            403 => " (the bot can't post in that channel; check its permissions)",
            404 => " (the webhook or channel no longer exists)",
            _ => string.Empty,
        };
        return $"HTTP {status}{(detail.Length > 0 ? ": " + detail : string.Empty)}{hint}";
    }
}
