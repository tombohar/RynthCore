using System.Net;
using System.Text;

namespace RynthCore.StatusAgent;

/// <summary>
/// Opt-in (default OFF) read-only HTTP endpoint so an app can PULL the latest
/// status with no backend. Serves the most recent aggregate JSON at GET /status
/// (and /, /status.json); GET /healthz returns "ok". Only listens when
/// <see cref="AgentConfig.ServeHttp"/> is true — nothing is opened otherwise.
/// </summary>
internal sealed class LocalStatusServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string? _token;
    private readonly string _prefix;
    private volatile byte[] _latest =
        Encoding.UTF8.GetBytes("{\"schema\":\"rynthcore.status-agent/1\",\"clientCount\":0,\"clients\":[]}");
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public bool Running { get; private set; }

    public LocalStatusServer(string prefix, string? token)
    {
        _prefix = prefix.EndsWith('/') ? prefix : prefix + "/";
        _token = string.IsNullOrWhiteSpace(token) ? null : token;
        _listener.Prefixes.Add(_prefix);
    }

    /// <summary>Replace the payload future requests will return (thread-safe).</summary>
    public void UpdateLatest(byte[] jsonUtf8) => _latest = jsonUtf8;

    public bool TryStart(out string error)
    {
        try
        {
            _listener.Start();
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
            Running = true;
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Running = false;
            return false;
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch { break; }                       // listener stopped / disposed
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            HttpListenerRequest req = ctx.Request;
            HttpListenerResponse res = ctx.Response;
            res.Headers["Access-Control-Allow-Origin"] = "*";
            string path = req.Url?.AbsolutePath?.TrimEnd('/').ToLowerInvariant() ?? "";

            if (path == "/healthz")
            {
                Write(res, 200, "text/plain", "ok"u8.ToArray());
                return;
            }
            if (!string.Equals(req.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            {
                Write(res, 405, "application/json", "{\"error\":\"method not allowed\"}"u8.ToArray());
                return;
            }
            if (path is not ("" or "/status" or "/status.json"))
            {
                Write(res, 404, "application/json", "{\"error\":\"not found\"}"u8.ToArray());
                return;
            }
            if (_token != null && !Authorized(req))
            {
                Write(res, 401, "application/json", "{\"error\":\"unauthorized\"}"u8.ToArray());
                return;
            }

            Write(res, 200, "application/json", _latest);
        }
        catch { /* a broken client connection must not take the agent down */ }
    }

    private bool Authorized(HttpListenerRequest req)
    {
        string? header = req.Headers["Authorization"];
        if (header != null && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(header.Substring("Bearer ".Length).Trim(), _token, StringComparison.Ordinal))
            return true;

        string? q = req.QueryString["token"];
        return q != null && string.Equals(q, _token, StringComparison.Ordinal);
    }

    private static void Write(HttpListenerResponse res, int status, string contentType, byte[] body)
    {
        try
        {
            res.StatusCode = status;
            res.ContentType = contentType;
            res.ContentLength64 = body.Length;
            res.OutputStream.Write(body, 0, body.Length);
        }
        finally { try { res.OutputStream.Close(); } catch { } }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { if (_listener.IsListening) _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        try { _loop?.Wait(500); } catch { }
        Running = false;
    }
}
