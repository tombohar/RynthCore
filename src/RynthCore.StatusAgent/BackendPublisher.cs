using System.Net.Http;
using System.Net.Http.Headers;

namespace RynthCore.StatusAgent;

/// <summary>
/// POSTs the aggregate payload to the user-configured endpoint. If no endpoint
/// is configured it does nothing — the agent never opens a connection on its
/// own. Retries a few times with backoff on transient failures.
/// </summary>
internal sealed class BackendPublisher : IDisposable
{
    private readonly AgentConfig _cfg;
    private readonly HttpClient _http;
    private readonly bool _enabled;

    public bool Enabled => _enabled;

    public BackendPublisher(AgentConfig cfg)
    {
        _cfg = cfg;
        _enabled = !string.IsNullOrWhiteSpace(cfg.Endpoint);
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(1, cfg.TimeoutSeconds)) };
    }

    public async Task<bool> PublishAsync(byte[] jsonUtf8, int clientCount, CancellationToken ct)
    {
        if (!_enabled)
            return false;

        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, _cfg.Endpoint)
                {
                    Content = new ByteArrayContent(jsonUtf8)
                };
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                if (!string.IsNullOrWhiteSpace(_cfg.AuthHeaderName) &&
                    !string.IsNullOrWhiteSpace(_cfg.AuthHeaderValue))
                {
                    req.Headers.TryAddWithoutValidation(_cfg.AuthHeaderName, _cfg.AuthHeaderValue);
                }

                using HttpResponseMessage resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    AgentLog.Debug($"POST {(int)resp.StatusCode} {resp.ReasonPhrase} ({clientCount} clients).");
                    return true;
                }

                AgentLog.Warn($"POST attempt {attempt}/{maxAttempts} -> HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AgentLog.Warn($"POST attempt {attempt}/{maxAttempts} failed: {ex.GetType().Name}: {ex.Message}");
            }

            if (attempt < maxAttempts)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
            }
        }

        AgentLog.Error("POST failed after all retries; will try again next cycle.");
        return false;
    }

    public void Dispose() => _http.Dispose();
}
