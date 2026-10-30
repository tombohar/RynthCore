using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RynthCore.App.Avalonia;

namespace UsageStatsTests;

/// <summary>
/// Checks for UsageStats.cs (the launcher's opt-in usage statistics).
///   dotnet run -c Release             offline checks, in a temp folder against a local test server
///   dotnet run -c Release -- live     then ONE real report to https://aelrynth.com/rynth/ping, built from
///                                     this PC's enabled plugins (engine.json, read only) and the live
///                                     update feed's plugin names, with a throwaway install ID (printed)
/// </summary>
internal static class Program
{
    private static int _checks, _failed;

    private static readonly HashSet<string> AllowedKeys = new() { "schema", "install", "version", "windows", "crashes", "plugins", "otherPlugins" };

    private static async Task<int> Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "rc-usage-stats-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Versions();
            Payload();
            Plugins(root);
            Crashes(root);
            OptInOut(root);
            await Sending(root);
            await FailuresAreSilent(root);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine($"{_checks - _failed}/{_checks} checks passed");
        if (_failed > 0) return 1;

        if (args.Any(a => a.Equals("live", StringComparison.OrdinalIgnoreCase)))
            return await Live();
        return 0;
    }

    private static void Versions()
    {
        Check(UsageStats.ShortVersion("2026.10.2.1+abcdef0123") == "2026.10.2.1", "release version keeps the number, drops the commit");
        Check(UsageStats.ShortVersion("1.0.0+abc") == "dev", "unversioned build is dev");
        Check(UsageStats.ShortVersion("2026.9.28.10 (4e4c4b7)") == "2026.9.28.10", "formatted version");
        Check(UsageStats.ShortVersion(@"C:\Users\Tom\x.dll") == "unknown", "anything else is unknown (no free text)");
        Check(UsageStats.ShortVersion(null) == "unknown", "null is unknown");
        string w = UsageStats.WindowsMajor();
        Check(w is "10" or "11" or "other", $"windows major is 10/11/other (got {w})");
    }

    private static void Payload()
    {
        var plugins = new List<UsageStats.PluginInfo>
        {
            new("RynthAi", "2026.10.2.1+abc"),
            new("RynthAi", "2026.10.2.1+abc"),            // duplicate copy: reported once
            new(@"C:\secret\Name", "1.0.0"),               // not a safe name: dropped
            new("RynthNav", "1.0.0+dev"),
        };
        string json = UsageStats.BuildPayload(Guid.NewGuid().ToString("D"), "2026.10.3.1+abc", plugins, 3, 2, "10");
        using var doc = JsonDocument.Parse(json);
        var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Check(keys.All(AllowedKeys.Contains) && keys.Count == AllowedKeys.Count, "payload has exactly the approved keys: " + string.Join(",", keys));
        Check(doc.RootElement.GetProperty("plugins").GetArrayLength() == 2, "plugins deduplicated, unsafe names dropped");
        Check(doc.RootElement.GetProperty("plugins")[0].EnumerateObject().Select(p => p.Name).SequenceEqual(new[] { "name", "version" }), "plugin entries are name+version only");
        Check(!json.Contains('\\') && !json.Contains("C:"), "no file paths in the payload");
        Check(Encoding.UTF8.GetByteCount(json) < 2048, $"payload is small ({Encoding.UTF8.GetByteCount(json)} bytes)");
        string clamped = UsageStats.BuildPayload(Guid.NewGuid().ToString("D"), "dev", Array.Empty<UsageStats.PluginInfo>(), 5000, 99999, "11");
        using var c = JsonDocument.Parse(clamped);
        Check(c.RootElement.GetProperty("crashes").GetInt32() == 1000 && c.RootElement.GetProperty("otherPlugins").GetInt32() == 100, "counts are clamped");
    }

    private static void Plugins(string root)
    {
        string dir = Path.Combine(root, "plugins");
        Directory.CreateDirectory(dir);
        string ai = Path.Combine(dir, "RynthCore.Plugin.RynthAi.dll");
        string mine = Path.Combine(dir, "MyPrivatePlugin.dll");
        File.Copy(typeof(Program).Assembly.Location, ai);
        File.Copy(typeof(Program).Assembly.Location, mine);
        var feed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["RynthCore.Plugin.RynthAi.dll"] = "RynthAi" };
        var (known, other) = UsageStats.DescribePlugins(new[] { ai, mine, ai.ToUpperInvariant(), Path.Combine(dir, "Missing.dll") }, feed);
        Check(known.Count == 1 && known[0].Name == "RynthAi", "a feed plugin is reported by its feed name");
        Check(other == 2, $"other plugins (incl. a missing file) only counted (got {other})");
    }

    private static void Crashes(string root)
    {
        string dumps = Path.Combine(root, "dumps");
        Directory.CreateDirectory(dumps);
        DateTime now = DateTime.UtcNow;
        void Make(string name, DateTime t) { string p = Path.Combine(dumps, name); File.WriteAllText(p, "x"); File.SetLastWriteTimeUtc(p, t); }
        Make("crash_1_old.dmp", now.AddDays(-2));
        Make("crash_2_new.dmp", now.AddMinutes(-10));
        Make("crash_3_new.dmp", now.AddMinutes(-5));
        Make("hang_4_new.dmp", now.AddMinutes(-5));
        Check(UsageStats.CountCrashDumps(dumps, now.AddDays(-1), now) == 2, "only crash_*.dmp newer than the last report count (hangs don't)");
        Check(UsageStats.CountCrashDumps(Path.Combine(root, "nope"), now.AddDays(-1), now) == 0, "missing dump folder = 0");

        // CoreCLR fatal-error dumps (the loader's clr_<pid>_<time>.dmp) land in the same folder and
        // count; the OUT OF MEMORY dumps do not. A folder of its own: the report tests below
        // count the dumps made above.
        string clrDumps = Path.Combine(root, "clr-dumps");
        Directory.CreateDirectory(clrDumps);
        void MakeIn(string name, DateTime t) { string p = Path.Combine(clrDumps, name); File.WriteAllText(p, "x"); File.SetLastWriteTimeUtc(p, t); }
        MakeIn("crash_5_new.dmp", now.AddMinutes(-4));
        MakeIn("clr_100_1760138700.dmp", now.AddMinutes(-3));
        MakeIn("clr_101_1759917600.dmp", now.AddDays(-2));
        MakeIn("acclient_oom_102_20261010_181816.dmp", now.AddMinutes(-3));
        Check(UsageStats.CountCrashDumps(clrDumps, now.AddDays(-1), now) == 2, "clr_*.dmp counts beside crash_*.dmp; old ones and OOM dumps don't");
    }

    private static void OptInOut(string root)
    {
        string state = Path.Combine(root, "state-optin");
        var s = new UsageStats("http://127.0.0.1:9/", state, Path.Combine(root, "dumps"));
        Check(s.InstallId == null, "no install ID before opting in");
        s.OptIn(DateTime.UtcNow);
        string? id = s.InstallId;
        Check(id != null && Guid.TryParseExact(id, "D", out _), "opting in makes a random GUID");
        s.OptIn(DateTime.UtcNow);
        Check(s.InstallId == id, "opting in again keeps the same ID");
        string text = File.ReadAllText(s.StatePath);
        Check(!text.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase) && !text.Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase),
            "the state file holds no user or machine name");
        s.OptOut();
        Check(!File.Exists(s.StatePath) && s.InstallId == null, "opting out deletes the file and the ID");
        s.OptIn(DateTime.UtcNow);
        Check(s.InstallId != null && s.InstallId != id, "opting in again later makes a NEW ID");
    }

    private static async Task Sending(string root)
    {
        using var server = new TestServer();
        string state = Path.Combine(root, "state-send");
        string dumps = Path.Combine(root, "dumps");
        var s = new UsageStats(server.Url, state, dumps);
        var plugins = new List<UsageStats.PluginInfo> { new("RynthAi", "2026.10.3.1") };
        DateTime day1 = DateTime.UtcNow;

        bool enabled = false;
        Check(!await s.MaybeSendAsync(() => enabled, "2026.10.3.1", plugins, 0, day1) && server.Count == 0, "off: nothing is sent");

        enabled = true;
        s.OptIn(day1.AddDays(-1));   // so the two "new" crash dumps from Crashes() count
        Check(await s.MaybeSendAsync(() => enabled, "2026.10.3.1", plugins, 1, day1) && server.Count == 1, "on: one report is sent");
        using (var doc = JsonDocument.Parse(server.LastBody!))
        {
            Check(doc.RootElement.GetProperty("install").GetString() == s.InstallId, "the report carries the install ID");
            Check(doc.RootElement.GetProperty("crashes").GetInt32() == 2, "crashes since opting in are counted");
        }
        Check(server.LastContentType?.StartsWith("application/json") == true, "sent as application/json");
        Check(server.LastUserAgent == "RynthCore-Launcher", "user agent is the launcher's, nothing more");

        // Later the same UTC day: halfway to midnight (day1 + 1 h crossed into the next day when the
        // tests ran after 23:00 UTC, 2026-10-28).
        DateTime laterSameDay = day1 + (day1.Date.AddDays(1) - day1) / 2;
        Check(!await s.MaybeSendAsync(() => enabled, "2026.10.3.1", plugins, 1, laterSameDay) && server.Count == 1, "a second check the same day sends nothing");

        DateTime day2 = day1.Date.AddDays(1).AddHours(1);
        Check(await s.MaybeSendAsync(() => enabled, "2026.10.3.1", plugins, 1, day2) && server.Count == 2, "next day: one more");
        using (var doc = JsonDocument.Parse(server.LastBody!))
            Check(doc.RootElement.GetProperty("crashes").GetInt32() == 0, "crashes reset after a report");

        s.OptOut();
        enabled = false;
        Check(!await s.MaybeSendAsync(() => enabled, "2026.10.3.1", plugins, 1, day2.AddDays(1)) && server.Count == 2, "after opting out nothing is sent");
        Check(!File.Exists(s.StatePath), "and no ID comes back");

        // Turned off between building the report and sending it.
        enabled = true;
        s.OptIn(day2);
        int calls = 0;
        Check(!await s.MaybeSendAsync(() => ++calls < 2, "2026.10.3.1", plugins, 0, day2.AddDays(2)) && server.Count == 2, "switched off mid-way: not sent");

        // Server refuses: nothing recorded, so a later check tries again.
        server.Status = 400;
        DateTime day4 = day2.AddDays(3);
        Check(!await s.MaybeSendAsync(() => true, "2026.10.3.1", plugins, 0, day4), "a refused report returns false");
        server.Status = 204;
        Check(await s.MaybeSendAsync(() => true, "2026.10.3.1", plugins, 0, day4.AddHours(6)), "and is retried at the next check");
    }

    private static async Task FailuresAreSilent(string root)
    {
        // A port nothing listens on, and a server that never answers.
        var s = new UsageStats("http://127.0.0.1:1/ping", Path.Combine(root, "state-fail"), Path.Combine(root, "dumps"));
        s.OptIn(DateTime.UtcNow);
        bool threw = false;
        try { await s.MaybeSendAsync(() => true, "dev", Array.Empty<UsageStats.PluginInfo>(), 0, DateTime.UtcNow); }
        catch { threw = true; }
        Check(!threw, "connection refused: no exception");

        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        var s2 = new UsageStats($"http://127.0.0.1:{port}/ping", Path.Combine(root, "state-hang"), Path.Combine(root, "dumps"));
        s2.OptIn(DateTime.UtcNow);
        var sw = Stopwatch.StartNew();
        bool ok = await s2.MaybeSendAsync(() => true, "dev", Array.Empty<UsageStats.PluginInfo>(), 0, DateTime.UtcNow);
        sw.Stop();
        tcp.Stop();
        Check(!ok && sw.Elapsed < TimeSpan.FromSeconds(15), $"a server that never answers gives up quietly ({sw.Elapsed.TotalSeconds:0.0} s)");
    }

    // ── Live ──────────────────────────────────────────────────────────────────

    private static async Task<int> Live()
    {
        Console.WriteLine();
        Console.WriteLine("LIVE: one report to https://aelrynth.com/rynth/ping");

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var http = new HttpClient())
        {
            byte[] env = await http.GetByteArrayAsync("https://aelrynth.com/downloads/rynth/update.json");
            using var e = JsonDocument.Parse(env);
            byte[] payload = Convert.FromBase64String(e.RootElement.GetProperty("payload").GetString()!);
            using var m = JsonDocument.Parse(payload);
            foreach (JsonElement p in m.RootElement.GetProperty("plugins").EnumerateArray())
                names.TryAdd(p.GetProperty("file").GetString()!, p.GetProperty("name").GetString()!);
        }

        var enabled = new List<string>();
        string engineJson = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RynthCore", "engine.json");
        if (File.Exists(engineJson))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(engineJson));
            if (doc.RootElement.TryGetProperty("PluginPaths", out JsonElement paths))
                foreach (JsonElement p in paths.EnumerateArray())
                    if (p.GetString() is { Length: > 0 } s) enabled.Add(s);
        }
        var (known, other) = UsageStats.DescribePlugins(enabled, names);

        // The launcher build in this checkout (dotnet build -c Release of RynthCore.App.Avalonia).
        string bin = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "RynthCore.App.Avalonia", "bin", "Release"));
        string? launcher = Directory.Exists(bin)
            ? Directory.EnumerateFiles(bin, "RynthCore.App.Avalonia.dll", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        string version = launcher != null ? FileVersionInfo.GetVersionInfo(launcher).ProductVersion ?? "" : "";
        Console.WriteLine($"Launcher build: {(launcher ?? "not found")} ({version})");

        string state = Path.Combine(Path.GetTempPath(), "rc-usage-stats-live-" + Guid.NewGuid().ToString("N"));
        try
        {
            var s = new UsageStats(UsageStats.DefaultEndpoint, state);
            s.OptIn(DateTime.UtcNow);
            Console.WriteLine("Test install ID: " + s.InstallId);
            Console.WriteLine(s.Preview(version, known, other, DateTime.UtcNow));
            bool sent = await s.MaybeSendAsync(() => true, version, known, other, DateTime.UtcNow);
            Console.WriteLine(sent ? "LIVE: accepted" : "LIVE: NOT accepted");
            return sent ? 0 : 1;
        }
        finally
        {
            try { Directory.Delete(state, true); } catch { }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (!ok) _failed++;
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}");
    }

    /// <summary>A one-route HTTP server on 127.0.0.1 that records each request and answers Status.</summary>
    private sealed class TestServer : IDisposable
    {
        private readonly TcpListener _tcp = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        public int Count;
        public int Status = 204;
        public string? LastBody, LastContentType, LastUserAgent;

        public TestServer()
        {
            _tcp.Start();
            _ = Task.Run(Loop);
        }

        public string Url => $"http://127.0.0.1:{((IPEndPoint)_tcp.LocalEndpoint).Port}/rynth/ping";

        private async Task Loop()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await _tcp.AcceptTcpClientAsync(_cts.Token); } catch { return; }
                using (c)
                {
                    var stream = c.GetStream();
                    var buf = new List<byte>();
                    var one = new byte[4096];
                    int headerEnd = -1, length = 0;
                    while (true)
                    {
                        int n = await stream.ReadAsync(one);
                        if (n <= 0) break;
                        buf.AddRange(one.AsSpan(0, n).ToArray());
                        string sofar = Encoding.ASCII.GetString(buf.ToArray());
                        if (headerEnd < 0 && (headerEnd = sofar.IndexOf("\r\n\r\n", StringComparison.Ordinal)) >= 0)
                        {
                            foreach (string line in sofar[..headerEnd].Split("\r\n"))
                            {
                                int colon = line.IndexOf(':');
                                if (colon < 0) continue;
                                string k = line[..colon].Trim(), v = line[(colon + 1)..].Trim();
                                if (k.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) length = int.Parse(v);
                                if (k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) LastContentType = v;
                                if (k.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) LastUserAgent = v;
                            }
                        }
                        if (headerEnd >= 0 && buf.Count >= headerEnd + 4 + length) break;
                    }
                    LastBody = Encoding.UTF8.GetString(buf.ToArray(), headerEnd + 4, length);
                    if (Status < 300) Interlocked.Increment(ref Count);
                    string reason = Status < 300 ? "No Content" : "Bad Request";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {Status} {reason}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _tcp.Stop();
        }
    }
}
