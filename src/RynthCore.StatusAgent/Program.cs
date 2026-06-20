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

if (opts.DryRun)
    AgentLog.Info("--dry-run: status will be printed but never POSTed.");

// Ctrl+C / SIGTERM -> graceful stop.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

try
{
    do
    {
        try
        {
            List<ClientStatus> clients = StatusReader.ReadAll(cfg);
            var payload = new AggregatePayload
            {
                Host = cfg.EffectiveHost,
                AgentVersion = version,
                GeneratedAtUtc = DateTimeOffset.UtcNow,
                ClientCount = clients.Count,
                Clients = clients,
            };

            AgentLog.Info(Summarize(clients));
            if (opts.Verbose)
                AgentLog.Debug(JsonSerializer.Serialize(payload, AgentJsonContext.Default.AggregatePayload));

            if (publisher.Enabled && !opts.DryRun)
                await publisher.PublishAsync(payload, cts.Token);
        }
        catch (OperationCanceledException) { break; }
        catch (Exception ex)
        {
            AgentLog.Error($"cycle failed: {ex.GetType().Name}: {ex.Message}");
        }

        if (opts.Once) break;

        try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, cfg.IntervalSeconds)), cts.Token); }
        catch (OperationCanceledException) { break; }
    }
    while (!cts.IsCancellationRequested);
}
finally
{
    AgentLog.Info("RynthCore.StatusAgent stopped.");
}

return 0;

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
