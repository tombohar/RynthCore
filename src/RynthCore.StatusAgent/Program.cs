using System.Reflection;
using System.Text.Json;
using RynthCore.StatusAgent;

// ────────────────────────────────────────────────────────────────────────────
//  RynthCore.StatusAgent — optional, runs on the operator's own PC.
//
//  Reads the LOCAL per-client status files RynthCore writes and forwards a
//  rolled-up status payload to a USER-CONFIGURED endpoint. Sends nothing until
//  you put an "Endpoint" URL in %APPDATA%\RynthCore\statusagent.json.
//
//  Flags:
//    --once         run a single cycle and exit (handy for testing)
//    --dry-run      read + print, never POST (even if an endpoint is set)
//    --config PATH  use an alternate config file
//    --interval N   override the post interval (seconds)
//    --verbose      verbose logging
//    --help         show this help
// ────────────────────────────────────────────────────────────────────────────

var opts = CliOptions.Parse(args);
if (opts.ShowHelp)
{
    CliOptions.PrintHelp();
    return 0;
}

string configPath = opts.ConfigPath ?? AgentConfig.DefaultConfigPath;
AgentConfig cfg = AgentConfig.LoadOrCreate(configPath, out bool created);
if (opts.IntervalOverride is int iv && iv > 0) cfg.IntervalSeconds = iv;

AgentLog.Verbose = opts.Verbose;
AgentLog.Init(cfg.LogDirectory);

string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
AgentLog.Info($"RynthCore.StatusAgent v{version} starting.");
AgentLog.Info($"Config: {configPath}");
AgentLog.Info($"Status dir: {cfg.StatusDirectory}");

// Per-session run history (kills/XP/etc.), captured as clients exit. Persisted to runs.json so it
// survives agent restarts; served at GET /runs for the app's Archive tab.
var runArchive = new RunArchive(Path.Combine(cfg.StatusDirectory, "runs.json"), cfg.EffectiveHost);

if (created)
{
    AgentLog.Info("A fresh config template was written. The agent will run in");
    AgentLog.Info("LOCAL PRINT-ONLY mode (no network) until you set \"Endpoint\".");
}

using var publisher = new BackendPublisher(cfg);
if (publisher.Enabled)
    AgentLog.Info($"Forwarding to endpoint: {cfg.Endpoint} every {cfg.IntervalSeconds}s.");
else
    AgentLog.Info("No endpoint configured -> LOCAL PRINT-ONLY mode (nothing is sent anywhere).");

// Opt-in pull endpoint: an app can fetch status with no backend.
LocalStatusServer? server = null;
WebRtcVideoService? video = null;   // HD WebRTC video mode (opt-in); disposed in finally
IconService? icons = null;          // item-icon decoder (GET /icon); disposed in finally
if (cfg.ServeHttp)
{
    string? commandDir = cfg.EnableRemoteControl ? Path.Combine(cfg.StatusDirectory, "commands") : null;
    VideoSocketService? videoSocket = null;
    if (cfg.EnableVideoStream)
    {
        video = new WebRtcVideoService(cfg.VideoMaxWidth, cfg.VideoFps, cfg.VideoBitrateKbps);
        videoSocket = new VideoSocketService(cfg.VideoMaxWidth, cfg.VideoFps, cfg.VideoBitrateKbps);
    }
    // Item-icon decoder (lazy-opens portal.dat on the first /icon request; harmless if the file is absent).
    icons = new IconService(cfg.IconDatPath);
    server = new LocalStatusServer(cfg.ServePrefix, cfg.ServeToken, commandDir,
                                   cfg.EnableScreenStream, cfg.StreamQuality, cfg.StreamIntervalMs, video, videoSocket,
                                   runArchive, cfg.StatusDirectory, icons);
    if (server.TryStart(out string serveErr))
    {
        AgentLog.Info($"Serving status at {cfg.ServePrefix}status (GET){(string.IsNullOrEmpty(cfg.ServeToken) ? "" : ", token required")}.");
        if (commandDir != null)
            AgentLog.Info($"Remote control ENABLED: POST {cfg.ServePrefix}command -> {commandDir}.");
        if (cfg.EnableScreenStream)
            AgentLog.Info($"Screen stream ENABLED: GET {cfg.ServePrefix}stream?pid=N (MJPEG) / frame?pid=N.");
        if (cfg.EnableVideoStream)
            AgentLog.Info($"HD video ENABLED: GET {cfg.ServePrefix}video?pid=N (WS H.264 client-server) + POST webrtc/offer (same-LAN). {(cfg.VideoMaxWidth == 0 ? "native" : cfg.VideoMaxWidth + "w")}@{cfg.VideoFps}fps.");
    }
    else
    {
        AgentLog.Warn($"Could not start status server on {cfg.ServePrefix} ({serveErr}).");
        AgentLog.Warn("For a LAN/Tailscale bind (http://+:PORT/) run elevated once, or add a netsh urlacl.");
        server.Dispose();
        server = null;
    }
}

string aggregatePath = Path.Combine(cfg.StatusDirectory, "aggregate.json");

if (opts.DryRun)
    AgentLog.Info("--dry-run: status will be printed but never POSTed.");

// Ctrl+C / SIGTERM -> graceful stop. Not disposed: ProcessExit fires during
// shutdown and would otherwise hit a disposed CTS (ObjectDisposedException ->
// nonzero exit). The process is ending anyway, so there's nothing to leak.
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; try { cts.Cancel(); } catch (ObjectDisposedException) { } };
AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { cts.Cancel(); } catch (ObjectDisposedException) { } };

// ── Event-driven status pump ────────────────────────────────────────────────
// A FileSystemWatcher on the status dir wakes the loop within ~DebounceMs of a client writing its
// snapshot, instead of waiting a fixed poll. IntervalSeconds is now the SAFETY FALLBACK (the max time
// between cycles when no file events arrive) — it still drives the time-based work: dead-PID liveness +
// DropDeadAfterSeconds pruning and the heartbeat-log fallback. Net: phone-visible status latency drops
// from up to IntervalSeconds to ~DebounceMs after the write. Agent-side only — no game-client risk.
const int DebounceMs = 50;
int fallbackMs = Math.Max(1, cfg.IntervalSeconds) * 1000;
using var dirty = new SemaphoreSlim(0, 1);
FileSystemWatcher? watcher = opts.Once ? null : TryArmStatusWatcher(cfg.StatusDirectory, dirty);

try
{
    do
    {
        try { await RunCycleAsync(); }
        catch (OperationCanceledException) { break; }
        catch (Exception ex) { AgentLog.Error($"cycle failed: {ex.GetType().Name}: {ex.Message}"); }

        if (opts.Once) break;

        try
        {
            // Wake on a status-file change OR the safety-fallback interval, then debounce the burst of
            // events that one atomic write (tmp create + rename + size change) produces into a single cycle.
            await dirty.WaitAsync(fallbackMs, cts.Token);
            await Task.Delay(DebounceMs, cts.Token);
            while (dirty.Wait(0)) { }   // drain extra signals accrued during the debounce window
        }
        catch (OperationCanceledException) { break; }
    }
    while (!cts.IsCancellationRequested);
}
finally
{
    watcher?.Dispose();
    server?.Dispose();
    video?.Dispose();
    icons?.Dispose();
    AgentLog.Info("RynthCore.StatusAgent stopped.");
}

return 0;

// One status cycle: read all client status, roll it up, and serve / file / POST it.
async Task RunCycleAsync()
{
    List<ClientStatus> clients = StatusReader.ReadAll(cfg);
    runArchive.Observe(clients);   // fold this cycle into per-session run history (started/updated/finished)
    var payload = new AggregatePayload
    {
        Host = cfg.EffectiveHost,
        AgentVersion = version,
        GeneratedAtUtc = DateTimeOffset.UtcNow,
        ClientCount = clients.Count,
        Clients = clients,
    };

    // Serialize once; reuse the bytes for serve / file / POST.
    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, AgentJsonContext.Default.AggregatePayload);

    AgentLog.Info(Summarize(clients));
    if (opts.Verbose)
        AgentLog.Debug(System.Text.Encoding.UTF8.GetString(bytes));

    server?.UpdateLatest(bytes);

    if (cfg.WriteAggregateFile)
    {
        try
        {
            Directory.CreateDirectory(cfg.StatusDirectory);
            File.WriteAllBytes(aggregatePath, bytes);
        }
        catch (Exception ex) { AgentLog.Debug($"aggregate.json write failed: {ex.Message}"); }
    }

    if (publisher.Enabled && !opts.DryRun)
        await publisher.PublishAsync(bytes, payload.ClientCount, cts.Token);
}

// Arm a FileSystemWatcher that pulses `signal` whenever a per-client status file lands. Returns null
// (and the loop falls back to interval polling) if the watcher can't be created.
static FileSystemWatcher? TryArmStatusWatcher(string statusDir, SemaphoreSlim signal)
{
    try
    {
        Directory.CreateDirectory(statusDir);   // ensure it exists so the watcher can arm (the engine creates it too)
        var w = new FileSystemWatcher(statusDir, "RynthCore.*.status.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };
        // FSW dispatches events sequentially on its own thread; a SemaphoreSlim(0,1) coalesces a burst
        // into one pending signal (extra Releases are swallowed).
        void Pulse(object? _, FileSystemEventArgs __) { try { if (signal.CurrentCount == 0) signal.Release(); } catch (SemaphoreFullException) { } }
        w.Created += Pulse;
        w.Changed += Pulse;
        w.Deleted += Pulse;
        w.Renamed += (_, __) => { try { if (signal.CurrentCount == 0) signal.Release(); } catch (SemaphoreFullException) { } };
        w.EnableRaisingEvents = true;
        AgentLog.Info($"Status watcher armed on {statusDir} (event-driven; IntervalSeconds is the safety fallback).");
        return w;
    }
    catch (Exception ex)
    {
        AgentLog.Warn($"Status watcher unavailable ({ex.GetType().Name}: {ex.Message}); falling back to interval polling.");
        return null;
    }
}

static string Summarize(List<ClientStatus> clients)
{
    if (clients.Count == 0)
        return "no RynthCore clients detected.";

    var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    foreach (ClientStatus c in clients)
        counts[c.State] = counts.GetValueOrDefault(c.State) + 1;

    string breakdown = string.Join(", ", counts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Value} {kv.Key}"));
    string detail = string.Join(" | ", clients.Select(c =>
    {
        string who = !string.IsNullOrEmpty(c.Character) ? c.Character : $"pid {c.Pid}";
        return $"{who}: {c.State} (fps {c.Fps}, plug {c.PluginTicksPerSec}/s)";
    }));
    return $"{clients.Count} client(s) [{breakdown}] -> {detail}";
}

internal sealed class CliOptions
{
    public bool Once;
    public bool DryRun;
    public bool Verbose;
    public bool ShowHelp;
    public string? ConfigPath;
    public int? IntervalOverride;

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--once":    o.Once = true; break;
                case "--dry-run": o.DryRun = true; break;
                case "--verbose": o.Verbose = true; break;
                case "-h":
                case "--help":    o.ShowHelp = true; break;
                case "--config":  if (i + 1 < args.Length) o.ConfigPath = args[++i]; break;
                case "--interval":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int n)) o.IntervalOverride = n;
                    break;
                default:
                    Console.WriteLine($"Unknown argument: {args[i]}");
                    o.ShowHelp = true;
                    break;
            }
        }
        return o;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("RynthCore.StatusAgent — forwards local RynthCore client status to your endpoint.");
        Console.WriteLine();
        Console.WriteLine("Usage: RynthCore.StatusAgent [options]");
        Console.WriteLine("  --once          run one cycle and exit");
        Console.WriteLine("  --dry-run       read + print, never POST");
        Console.WriteLine("  --config PATH   alternate config file");
        Console.WriteLine("  --interval N    override post interval (seconds)");
        Console.WriteLine("  --verbose       verbose logging (prints full payload)");
        Console.WriteLine("  --help          show this help");
        Console.WriteLine();
        Console.WriteLine($"Config: {AgentConfig.DefaultConfigPath}");
        Console.WriteLine("Nothing is sent until you set \"Endpoint\" in the config.");
    }
}
