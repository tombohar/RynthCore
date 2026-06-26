using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

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
    private readonly string? _commandDir;   // null = remote control disabled (no POST /command)
    private readonly bool _enableStream;    // GET /frame + /stream (screen capture)
    private readonly int _streamQuality;
    private readonly int _streamIntervalMs;
    private readonly WebRtcVideoService? _video;   // POST /webrtc/* (HD WebRTC mode; same-LAN); null = disabled
    private readonly VideoSocketService? _videoSocket;   // GET /video (WS, H.264 client-server; works remotely)
    private readonly RunArchive? _runArchive;      // GET /runs (per-session play history); null = disabled
    private readonly string? _statusDir;           // GET /inventory reads RynthCore.<pid>.inventory.json from here
    private readonly IconService? _icons;          // GET /icon?did=N (portal.dat → PNG); null = disabled
    private readonly MapService? _maps;            // GET /maps + /map?lb&layer (baked dungeon .bin → PNG); null = disabled
    private volatile byte[] _latest =
        Encoding.UTF8.GetBytes("{\"schema\":\"rynthcore.status-agent/1\",\"clientCount\":0,\"clients\":[]}");
    private CancellationTokenSource? _cts;
    private Task? _loop;
    // Connected /statusfeed push clients — each value is a per-socket wake signal. UpdateLatest pulses
    // them all; each socket's loop then sends the freshest _latest (coalescing rapid updates).
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _feedSignals = new();
    private const int FeedKeepAliveMs = 20000;   // re-send _latest at least this often (keepalive + missed-pulse cover)

    public bool Running { get; private set; }

    public LocalStatusServer(string prefix, string? token, string? commandDir = null,
                             bool enableStream = false, int streamQuality = 55, int streamIntervalMs = 400,
                             WebRtcVideoService? video = null, VideoSocketService? videoSocket = null,
                             RunArchive? runArchive = null, string? statusDirectory = null,
                             IconService? icons = null, MapService? maps = null)
    {
        _prefix = prefix.EndsWith('/') ? prefix : prefix + "/";
        _token = string.IsNullOrWhiteSpace(token) ? null : token;
        _commandDir = string.IsNullOrWhiteSpace(commandDir) ? null : commandDir;
        _enableStream = enableStream;
        _streamQuality = Math.Clamp(streamQuality, 1, 100);
        _streamIntervalMs = Math.Clamp(streamIntervalMs, 100, 2000);
        _video = video;
        _videoSocket = videoSocket;
        _runArchive = runArchive;
        _statusDir = string.IsNullOrWhiteSpace(statusDirectory) ? null : statusDirectory;
        _icons = icons;
        _maps = maps;
        _listener.Prefixes.Add(_prefix);
    }

    /// <summary>Replace the payload future requests return AND push it to any connected /statusfeed
    /// sockets (thread-safe).</summary>
    public void UpdateLatest(byte[] jsonUtf8)
    {
        _latest = jsonUtf8;
        // Pulse every feed loop; each sends the freshest _latest when it wakes (so rapid updates coalesce
        // and a slow client can't back up the agent). A SemaphoreSlim(1,1) means extra pulses are no-ops.
        foreach (var kv in _feedSignals)
        {
            try { if (kv.Value.CurrentCount == 0) kv.Value.Release(); } catch (SemaphoreFullException) { }
        }
    }

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
            string method = req.HttpMethod ?? "GET";

            // Diagnostic: log every /webrtc request + every CORS preflight, at the door (before any
            // auth/route processing), so we can see whether the app's offer POST even reaches us.
            if (path.StartsWith("/webrtc") || string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
                AgentLog.Info($"[video] HTTP {method} {path} ua=\"{req.UserAgent}\"");

            // CORS preflight for the POST /command path (a browser-hosted client may send it).
            if (string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                res.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
                res.Headers["Access-Control-Allow-Headers"] = "Authorization, Content-Type";
                Write(res, 204, "text/plain", Array.Empty<byte>());
                return;
            }

            if (path == "/healthz")
            {
                Write(res, 200, "text/plain", "ok"u8.ToArray());
                return;
            }

            // ── Screen stream: GET /frame?pid=N (single JPEG) or /stream?pid=N (MJPEG) ──
            if (path == "/frame" || path == "/stream")
            {
                if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    Write(res, 405, "application/json", "{\"error\":\"method not allowed\"}"u8.ToArray());
                    return;
                }
                if (_token != null && !Authorized(req))
                {
                    Write(res, 401, "application/json", "{\"error\":\"unauthorized\"}"u8.ToArray());
                    return;
                }
                if (!_enableStream)
                {
                    Write(res, 503, "application/json", "{\"error\":\"screen stream disabled\"}"u8.ToArray());
                    return;
                }
                int pid = int.TryParse(req.QueryString["pid"], out int p) ? p : 0;
                if (pid <= 0)
                {
                    Write(res, 400, "application/json", "{\"error\":\"pid required\"}"u8.ToArray());
                    return;
                }
                int quality = ClampQuery(req, "q", _streamQuality, 1, 100);
                int maxWidth = ClampQuery(req, "w", 0, 0, 4096);                       // 0 = native size
                int fps = ClampQuery(req, "fps", 0, 0, 30);
                int intervalMs = fps > 0 ? Math.Clamp(1000 / fps, 33, 2000) : _streamIntervalMs;
                if (path == "/frame") HandleFrame(res, pid, quality, maxWidth);
                else HandleStream(res, pid, quality, maxWidth, intervalMs);
                return;
            }

            // ── HD video (WebSocket H.264, client-server, works remotely): GET /video?pid=N (ws upgrade) ──
            if (path == "/video")
            {
                if (_token != null && !Authorized(req)) { Write(res, 401, "application/json", "{\"error\":\"unauthorized\"}"u8.ToArray()); return; }
                if (_videoSocket == null) { Write(res, 503, "application/json", "{\"error\":\"video stream disabled\"}"u8.ToArray()); return; }
                int vpid = int.TryParse(req.QueryString["pid"], out int vv) ? vv : 0;
                if (vpid <= 0) { Write(res, 400, "application/json", "{\"error\":\"pid required\"}"u8.ToArray()); return; }
                if (!req.IsWebSocketRequest) { Write(res, 426, "application/json", "{\"error\":\"websocket required\"}"u8.ToArray()); return; }
                AgentLog.Info($"[vsock] HTTP WS /video pid={vpid} ua=\"{req.UserAgent}\"");
                _ = HandleVideoSocketAsync(ctx, vpid);
                return;
            }

            // ── Status push (WebSocket): GET /statusfeed — push the latest aggregate on every change ──
            if (path == "/statusfeed")
            {
                if (_token != null && !Authorized(req)) { Write(res, 401, "application/json", "{\"error\":\"unauthorized\"}"u8.ToArray()); return; }
                if (!req.IsWebSocketRequest) { Write(res, 426, "application/json", "{\"error\":\"websocket required\"}"u8.ToArray()); return; }
                AgentLog.Info($"[feed] WS /statusfeed ua=\"{req.UserAgent}\"");
                _ = HandleStatusFeedAsync(ctx);
                return;
            }

            // ── HD video (WebRTC): POST /webrtc/offer?pid=N {sdp} -> {sdp:answer}; POST /webrtc/stop?pid=N ──
            if (path == "/webrtc/offer" || path == "/webrtc/stop" || path == "/webrtc/clientlog")
            {
                if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    Write(res, 405, "application/json", "{\"error\":\"method not allowed\"}"u8.ToArray());
                    return;
                }
                if (_token != null && !Authorized(req))
                {
                    Write(res, 401, "application/json", "{\"error\":\"unauthorized\"}"u8.ToArray());
                    return;
                }
                if (_video == null)
                {
                    Write(res, 503, "application/json", "{\"error\":\"video stream disabled\"}"u8.ToArray());
                    return;
                }
                int pid = int.TryParse(req.QueryString["pid"], out int vp) ? vp : 0;
                if (pid <= 0)
                {
                    Write(res, 400, "application/json", "{\"error\":\"pid required\"}"u8.ToArray());
                    return;
                }
                if (path == "/webrtc/stop") { _video.Stop(pid); Write(res, 200, "application/json", "{\"ok\":true}"u8.ToArray()); return; }
                if (path == "/webrtc/clientlog")   // diagnostic: WKWebView reports its WebRTC errors/states here
                {
                    try { using var sr = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8); AgentLog.Info($"[video] CLIENT pid={pid}: {sr.ReadToEnd()}"); } catch { }
                    Write(res, 200, "application/json", "{\"ok\":true}"u8.ToArray());
                    return;
                }
                HandleWebRtcOffer(req, res, pid);
                return;
            }

            // ── Remote control: POST /command writes a command file the engine-plugin polls ──
            if (path == "/command")
            {
                if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    Write(res, 405, "application/json", "{\"error\":\"method not allowed\"}"u8.ToArray());
                    return;
                }
                if (_token != null && !Authorized(req))
                {
                    Write(res, 401, "application/json", "{\"error\":\"unauthorized\"}"u8.ToArray());
                    return;
                }
                if (_commandDir == null)
                {
                    Write(res, 503, "application/json", "{\"error\":\"remote control disabled\"}"u8.ToArray());
                    return;
                }
                HandleCommand(req, res);
                return;
            }

            // ── Run history: GET /runs — per-session play stats (kills/XP/etc.), newest first ──
            if (path == "/runs" || path == "/runs.json")
            {
                if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    Write(res, 405, "application/json", "{\"error\":\"method not allowed\"}"u8.ToArray());
                    return;
                }
                if (_token != null && !Authorized(req))
                {
                    Write(res, 401, "application/json", "{\"error\":\"unauthorized\"}"u8.ToArray());
                    return;
                }
                byte[] runs = _runArchive?.BuildJsonBytes()
                    ?? "{\"schema\":\"rynthcore.runs/1\",\"count\":0,\"runs\":[]}"u8.ToArray();
                Write(res, 200, "application/json", runs);
                return;
            }

            // ── Dungeon maps: GET /maps — list baked floor-plan maps (landblock + layer + raster bounds) ──
            if (path == "/maps" || path == "/maps.json")
            {
                if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                { Write(res, 405, "application/json", "{\"error\":\"method not allowed\"}"u8.ToArray()); return; }
                if (_token != null && !Authorized(req))
                { Write(res, 401, "application/json", "{\"error\":\"unauthorized\"}"u8.ToArray()); return; }
                var entries = _maps?.List() ?? (IReadOnlyList<MapService.MapEntry>)Array.Empty<MapService.MapEntry>();
                var payload = new MapsListPayload { Count = entries.Count };
                foreach (var e in entries)
                    payload.Maps.Add(new MapEntryDto
                    {
                        Landblock = e.Landblock.ToString("X8"),
                        Layer = e.Layer, Bytes = e.Bytes, Mtime = e.MtimeUtc,
                        W = e.W, H = e.H, XMin = e.XMin, YMin = e.YMin,
                        Name = "Dungeon " + (e.Landblock & 0xFFFF).ToString("X4"),
                    });
                Write(res, 200, "application/json",
                    JsonSerializer.SerializeToUtf8Bytes(payload, AgentJsonContext.Default.MapsListPayload));
                return;
            }

            // ── Inventory: GET /inventory?pid=N — that client's full read-only inventory (icons/slots/appraisal) ──
            if (path == "/inventory" || path == "/inventory.json")
            {
                if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    Write(res, 405, "application/json", "{\"error\":\"method not allowed\"}"u8.ToArray());
                    return;
                }
                if (_token != null && !Authorized(req))
                {
                    Write(res, 401, "application/json", "{\"error\":\"unauthorized\"}"u8.ToArray());
                    return;
                }
                int ipid = int.TryParse(req.QueryString["pid"], out int ip) ? ip : 0;
                if (ipid <= 0)
                {
                    Write(res, 400, "application/json", "{\"error\":\"pid required\"}"u8.ToArray());
                    return;
                }
                // Serve the plugin-written file verbatim (already in the app's schema); empty payload if
                // that client isn't exporting inventory yet, so the app can always parse a response.
                Write(res, 200, "application/json", ReadInventoryBytes(ipid));
                return;
            }

            // ── Item icon: GET /icon?did=N — decode a portal.dat texture to PNG (read-only inventory view) ──
            if (path == "/icon")
            {
                if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    Write(res, 405, "application/json", "{\"error\":\"method not allowed\"}"u8.ToArray());
                    return;
                }
                if (_token != null && !Authorized(req))
                {
                    Write(res, 401, "application/json", "{\"error\":\"unauthorized\"}"u8.ToArray());
                    return;
                }
                uint did = uint.TryParse(req.QueryString["did"], out uint dv) ? dv : 0u;
                if (did == 0)
                {
                    Write(res, 400, "application/json", "{\"error\":\"did required\"}"u8.ToArray());
                    return;
                }
                // Icons are immutable per DID → strong ETag + long immutable cache; honour If-None-Match.
                string etag = "\"" + did.ToString("X8") + "\"";
                if (string.Equals(req.Headers["If-None-Match"], etag, StringComparison.Ordinal))
                {
                    res.Headers["ETag"] = etag;
                    Write(res, 304, "image/png", Array.Empty<byte>());
                    return;
                }
                byte[]? png = _icons?.GetPng(did);
                if (png == null)
                {
                    Write(res, 404, "application/json", "{\"error\":\"icon unavailable\"}"u8.ToArray());
                    return;
                }
                res.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
                res.Headers["ETag"] = etag;
                Write(res, 200, "image/png", png);
                return;
            }

            // ── Dungeon map image: GET /map?lb=XXXXXXXX&layer=N — baked .bin re-encoded to PNG ──
            if (path == "/map")
            {
                if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                { Write(res, 405, "application/json", "{\"error\":\"method not allowed\"}"u8.ToArray()); return; }
                if (_token != null && !Authorized(req))
                { Write(res, 401, "application/json", "{\"error\":\"unauthorized\"}"u8.ToArray()); return; }
                if (!uint.TryParse(req.QueryString["lb"], System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out uint lb)
                    || !int.TryParse(req.QueryString["layer"], out int layer))
                { Write(res, 400, "application/json", "{\"error\":\"lb (hex) and layer required\"}"u8.ToArray()); return; }
                var r = _maps?.GetPng(lb, layer);
                if (r == null)
                { Write(res, 404, "application/json", "{\"error\":\"map unavailable\"}"u8.ToArray()); return; }
                var (mapPng, meta) = r.Value;
                // Re-baked as the bot explores, so the ETag tracks file mtime+size (NOT immutable like /icon).
                string mapEtag = "\"" + meta.MtimeUtc.Ticks + "-" + meta.Bytes + "\"";
                if (string.Equals(req.Headers["If-None-Match"], mapEtag, StringComparison.Ordinal))
                { res.Headers["ETag"] = mapEtag; Write(res, 304, "image/png", Array.Empty<byte>()); return; }
                res.Headers["Cache-Control"] = "public, max-age=60";
                res.Headers["ETag"] = mapEtag;
                res.Headers["X-Map-XMin"] = meta.XMin.ToString();
                res.Headers["X-Map-YMin"] = meta.YMin.ToString();
                res.Headers["X-Map-W"] = meta.W.ToString();
                res.Headers["X-Map-H"] = meta.H.ToString();
                Write(res, 200, "image/png", mapPng);
                return;
            }

            if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
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

    // Plugin actions (written as a command file the plugin polls). "closeClient" is handled by the
    // agent directly (process kill), not the plugin — see HandleCommand.
    private static readonly HashSet<string> KnownActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "macro", "combat", "buffing", "navigation", "looting", "meta",
        "navProfile", "lootProfile", "metaProfile", "settingsProfile",
        "forceRebuff", "cancelRebuff", "clearBusy", "hideUi", "sendChat",
        "moveStart", "moveStop",
        "assess",   // request an Assess/Identify of one item (value = item id) — fills inventory appraisal
        "closeClient",
    };

    private void HandleCommand(HttpListenerRequest req, HttpListenerResponse res)
    {
        try
        {
            string body;
            using (var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
                body = reader.ReadToEnd();

            int pid; string action; string value;
            using (var doc = JsonDocument.Parse(body))
            {
                var root = doc.RootElement;
                pid = root.TryGetProperty("pid", out var p) && p.TryGetInt32(out int pv) ? pv : 0;
                action = root.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String
                    ? (a.GetString() ?? "") : "";
                value = root.TryGetProperty("value", out var v)
                    ? (v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : v.ToString()) : "";
            }

            if (pid <= 0 || string.IsNullOrEmpty(action) || !KnownActions.Contains(action))
            {
                Write(res, 400, "application/json", "{\"error\":\"bad command\"}"u8.ToArray());
                return;
            }

            // Close-client is an AGENT action (kill the process), not a plugin command — handle + return.
            if (string.Equals(action, "closeClient", StringComparison.OrdinalIgnoreCase))
            {
                CloseClient(pid);
                Write(res, 202, "application/json", "{\"ok\":true}"u8.ToArray());
                return;
            }

            Directory.CreateDirectory(_commandDir!);
            PruneStaleCommands();

            // Ticks-prefixed name so the plugin applies commands in submit order; guid avoids collisions.
            string name = $"RynthCore.{pid}.{DateTime.UtcNow.Ticks:D19}-{Guid.NewGuid():N}.cmd.json";
            string dest = Path.Combine(_commandDir!, name);
            string tmp = dest + ".tmp";
            string payload = JsonSerializer.Serialize(
                new CommandFile { Pid = pid, Action = action, Value = value, Ts = DateTimeOffset.UtcNow },
                AgentJsonContext.Default.CommandFile);
            File.WriteAllText(tmp, payload, Encoding.UTF8);
            File.Move(tmp, dest, overwrite: true);

            AgentLog.Info($"command queued: pid={pid} {action}={value}");
            Write(res, 202, "application/json", "{\"ok\":true}"u8.ToArray());
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"POST /command failed: {ex.GetType().Name}: {ex.Message}");
            Write(res, 400, "application/json", "{\"error\":\"invalid request\"}"u8.ToArray());
        }
    }

    // Drop any command file a client never consumed within 60s (e.g. issued to a box that's now down).
    private void PruneStaleCommands()
    {
        try
        {
            DateTime cutoff = DateTime.UtcNow.AddSeconds(-60);
            foreach (string f in Directory.GetFiles(_commandDir!, "*.cmd.json"))
            {
                try { if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    /// Close one AC client: try a graceful WM_CLOSE first (clean logout / save), and hard-kill after a
    /// few seconds if it's still alive (covers a hung client whose message loop is wedged).
    private static void CloseClient(int pid)
    {
        try
        {
            var proc = System.Diagnostics.Process.GetProcessById(pid);
            // Defence-in-depth for a destructive op: only ever close an AC client, never some other
            // process if a stale pid were somehow recycled between the feed and this call.
            if (!string.Equals(proc.ProcessName, "acclient", StringComparison.OrdinalIgnoreCase))
            {
                AgentLog.Warn($"close client pid={pid} refused: not an acclient process ({proc.ProcessName}).");
                try { proc.Dispose(); } catch { }
                return;
            }
            try { proc.CloseMainWindow(); } catch { }
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(6000); if (!proc.HasExited) proc.Kill(); } catch { }
                finally { try { proc.Dispose(); } catch { } }
            });
            AgentLog.Info($"close client requested: pid={pid} (graceful, kill fallback in 6s)");
        }
        catch (Exception ex) { AgentLog.Warn($"close client pid={pid} failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static readonly byte[] EmptyInventory =
        "{\"schema\":\"rynthcore.inventory/1\",\"version\":0,\"itemCount\":0,\"containers\":[],\"items\":[]}"u8.ToArray();

    /// Read RynthCore.<pid>.inventory.json (written atomically by the RynthRemote plugin) and return its
    /// bytes. Returns an empty-but-valid payload when the file is absent (client not exporting yet) or on
    /// any read error — so the app always gets parseable JSON. The plugin's atomic write means a concurrent
    /// read never sees a torn file.
    private byte[] ReadInventoryBytes(int pid)
    {
        if (_statusDir == null) return EmptyInventory;
        try
        {
            string path = Path.Combine(_statusDir, $"RynthCore.{pid}.inventory.json");
            if (!File.Exists(path)) return EmptyInventory;
            byte[] bytes = File.ReadAllBytes(path);
            return bytes.Length >= 2 ? bytes : EmptyInventory;
        }
        catch { return EmptyInventory; }
    }

    private static int ClampQuery(HttpListenerRequest req, string key, int def, int lo, int hi)
        => int.TryParse(req.QueryString[key], out int v) ? Math.Clamp(v, lo, hi) : def;

    private void HandleFrame(HttpListenerResponse res, int pid, int quality, int maxWidth)
    {
        var r = ScreenCapture.TryCaptureJpeg(pid, quality, maxWidth, out byte[] jpeg);
        if (r == ScreenCapture.Result.Ok)
            Write(res, 200, "image/jpeg", jpeg);
        else
            Write(res, 503, "application/json",
                Encoding.ASCII.GetBytes($"{{\"error\":\"{r.ToString().ToLowerInvariant()}\"}}"));
    }

    private static readonly byte[] CrLf = { 13, 10 };

    /// MJPEG: push JPEG frames as multipart/x-mixed-replace until the client disconnects (the <img>
    /// is removed app-side) or the window closes. Capture only happens while a client is connected.
    private void HandleStream(HttpListenerResponse res, int pid, int quality, int maxWidth, int intervalMs)
    {
        try
        {
            res.StatusCode = 200;
            res.SendChunked = true;
            res.ContentType = "multipart/x-mixed-replace; boundary=frame";
            res.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            var os = res.OutputStream;
            int misses = 0;
            var sw = new System.Diagnostics.Stopwatch();
            while (true)
            {
                sw.Restart();
                var r = ScreenCapture.TryCaptureJpeg(pid, quality, maxWidth, out byte[] jpeg);
                if (r != ScreenCapture.Result.Ok)
                {
                    // window gone for a while -> end the stream; minimized/transient -> wait + retry.
                    if (r == ScreenCapture.Result.NotFound && ++misses > 6) break;
                    Thread.Sleep(500);
                    continue;
                }
                misses = 0;
                byte[] header = Encoding.ASCII.GetBytes(
                    $"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {jpeg.Length}\r\n\r\n");
                os.Write(header, 0, header.Length);
                os.Write(jpeg, 0, jpeg.Length);
                os.Write(CrLf, 0, CrLf.Length);
                os.Flush();                       // throws once the client (phone) disconnects
                // Pace to the TARGET interval: sleep only the time left after capture+encode+send, so the
                // achieved fps actually hits the target (up to the encode ceiling) instead of interval+capture.
                int left = intervalMs - (int)sw.ElapsedMilliseconds;
                if (left > 1) Thread.Sleep(left);
            }
        }
        catch { /* client disconnected / write failed — stop capturing for this stream */ }
        finally { try { res.OutputStream.Close(); } catch { } }
    }

    private async Task HandleVideoSocketAsync(HttpListenerContext ctx, int pid)
    {
        HttpListenerWebSocketContext wsCtx;
        try { wsCtx = await ctx.AcceptWebSocketAsync(null); }
        catch (Exception ex) { AgentLog.Warn($"[vsock] ws accept failed pid {pid}: {ex.Message}"); try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { } return; }
        using var cts = new CancellationTokenSource();
        try { await _videoSocket!.StreamAsync(wsCtx.WebSocket, pid, cts.Token); }
        catch (Exception ex) { AgentLog.Debug($"[vsock] pid {pid}: {ex.Message}"); }
        finally { try { wsCtx.WebSocket.Dispose(); } catch { } }
    }

    /// One /statusfeed client: send the current snapshot on connect, then push the freshest _latest
    /// whenever UpdateLatest pulses us (and at least every FeedKeepAliveMs). Sends are serial on this
    /// socket (no overlap); a slow/dead client only affects its own loop. The agent's shutdown token
    /// ends every feed loop on Dispose.
    private async Task HandleStatusFeedAsync(HttpListenerContext ctx)
    {
        HttpListenerWebSocketContext wsCtx;
        try { wsCtx = await ctx.AcceptWebSocketAsync(null); }
        catch (Exception ex) { AgentLog.Warn($"[feed] ws accept failed: {ex.Message}"); try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { } return; }

        WebSocket ws = wsCtx.WebSocket;
        CancellationToken ct = _cts?.Token ?? CancellationToken.None;
        var id = Guid.NewGuid();
        var signal = new SemaphoreSlim(1, 1);   // start signaled -> push the current snapshot immediately on connect
        _feedSignals[id] = signal;
        AgentLog.Info($"[feed] client connected ({_feedSignals.Count} total).");
        try
        {
            Task recv = DrainReceiveAsync(ws, ct);   // background: notice the client closing
            while (ws.State == WebSocketState.Open && !recv.IsCompleted)
            {
                await signal.WaitAsync(FeedKeepAliveMs, ct);   // wake on a pulse, the keepalive timeout, or shutdown
                if (ws.State != WebSocketState.Open) break;
                byte[] snapshot = _latest;                      // freshest at send time (coalesces missed pulses)
                await ws.SendAsync(new ArraySegment<byte>(snapshot), WebSocketMessageType.Text, endOfMessage: true, ct);
            }
        }
        catch (OperationCanceledException) { /* agent stopping */ }
        catch { /* client gone / send failed */ }
        finally
        {
            _feedSignals.TryRemove(id, out _);
            try { signal.Dispose(); } catch { }
            try { ws.Dispose(); } catch { }
            AgentLog.Info($"[feed] client disconnected ({_feedSignals.Count} total).");
        }
    }

    // Pump the receive side so we notice a client close/ping; payloads are ignored (the feed is server->client).
    private static async Task DrainReceiveAsync(WebSocket ws, CancellationToken ct)
    {
        var buf = new byte[256];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                WebSocketReceiveResult r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                if (r.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch { /* socket closed / cancelled */ }
    }

    private void HandleWebRtcOffer(HttpListenerRequest req, HttpListenerResponse res, int pid)
    {
        string body;
        using (var sr = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
            body = sr.ReadToEnd();
        string? offer = null;
        try { offer = JsonDocument.Parse(body).RootElement.GetProperty("sdp").GetString(); } catch { }
        if (string.IsNullOrEmpty(offer))
        {
            Write(res, 400, "application/json", "{\"error\":\"sdp required\"}"u8.ToArray());
            return;
        }
        // Diagnostic: confirm the offer arrived + from which client (Safari vs Chrome), and what H.264
        // profile-level-id(s) it can RECEIVE.
        AgentLog.Info($"[video] OFFER pid={pid} ua=\"{req.UserAgent}\"");
        foreach (var ln in offer.Split('\n'))
            if (ln.StartsWith("m=video", StringComparison.Ordinal) || ln.Contains("profile-level-id", StringComparison.OrdinalIgnoreCase) || ln.Contains("candidate", StringComparison.OrdinalIgnoreCase))
                AgentLog.Info($"[video] offer> {ln.Trim()}");
        string? answer;
        try { answer = _video!.CreateAnswerAsync(pid, offer).GetAwaiter().GetResult(); }
        catch (Exception ex) { AgentLog.Warn($"[video] offer failed pid {pid}: {ex.Message}"); answer = null; }
        if (answer == null)
        {
            Write(res, 409, "application/json", "{\"error\":\"no renderable window for that pid (minimized?) or negotiation failed\"}"u8.ToArray());
            return;
        }
        // Diagnostic: log the answer's H.264 negotiation so we can compare payload type + profile-level-id
        // against the client's offer (a mismatch makes Safari reject setRemoteDescription -> black).
        foreach (var ln in answer.Split('\n'))
            if (ln.StartsWith("m=video", StringComparison.Ordinal) || ln.Contains("profile-level-id", StringComparison.OrdinalIgnoreCase) || (ln.Contains("rtpmap", StringComparison.OrdinalIgnoreCase) && ln.Contains("H264", StringComparison.OrdinalIgnoreCase)))
                AgentLog.Info($"[video] answer> {ln.Trim()}");
        Write(res, 200, "application/json", JsonSerializer.SerializeToUtf8Bytes(new { sdp = answer }));
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
