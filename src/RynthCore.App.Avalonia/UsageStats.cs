using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RynthCore.App.Avalonia;

/// <summary>
/// Opt-in, anonymous usage statistics ("Help improve RynthCore"). OFF unless the player turns it on.
///
/// When on, at most one small report a day goes to aelrynth.com (never to a game server), sent
/// after a successful update check. The report holds exactly:
///   - a random install ID (a GUID made when the player opts in, stored only in usage-stats.json,
///     tied to nothing else);
///   - the RynthCore version;
///   - the enabled plugins the update feed ships (RynthSuite), with their versions; other plugins
///     only as a count, never by name (a private plugin's name could identify its author);
///   - how many crash dumps were written since the last report (a count only): the engine's
///     Logs\dumps\crash_*.dmp and CoreCLR's Logs\dumps\clr_*.dmp;
///   - the Windows major version ("10" or "11").
/// Never: character, account or server names, file paths, or anything else identifying.
///
/// Turning it off deletes usage-stats.json (and with it the install ID) and cancels a report in
/// flight. Every failure is silent, and nothing here ever delays or fails the update check.
/// </summary>
internal sealed class UsageStats
{
    public const string DefaultEndpoint = "https://aelrynth.com/rynth/ping";

    /// <summary>Where the engine writes crash/hang minidumps (RynthCore.Engine CrashDump / LogPaths).</summary>
    public static readonly string DefaultDumpDirectory = Path.Combine(RynthCore.Install.RynthInstallPaths.CoreLogsDir, "dumps"); // C:\Games\RynthCore\Logs\dumps by default

    public const int Schema = 1;
    private const int MaxCrashes = 1000;
    private const int MaxPlugins = 32;

    public sealed record PluginInfo(string Name, string Version);

    private static readonly HttpClient Http = CreateHttp();
    private static readonly Regex ReleaseVersion = new(@"^\d{4}\.\d{1,2}\.\d{1,2}(\.\d{1,4})?$", RegexOptions.CultureInvariant);
    private static readonly Regex SafeName = new(@"^[A-Za-z0-9 ._-]{1,40}$", RegexOptions.CultureInvariant);

    private readonly string _endpoint;
    private readonly string _stateDir;
    private readonly string _dumpDir;
    private CancellationTokenSource _cts = new();
    private int _sending;

    public UsageStats(string endpoint = DefaultEndpoint, string? stateDir = null, string? dumpDir = null)
    {
        _endpoint = endpoint;
        _stateDir = stateDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RynthCore");
        _dumpDir = dumpDir ?? DefaultDumpDirectory;
    }

    public string StatePath => Path.Combine(_stateDir, "usage-stats.json");

    // ── Opt in / out ──────────────────────────────────────────────────────────

    /// <summary>Called when the player turns statistics on: makes the install ID if there is none.</summary>
    public void OptIn(DateTime utcNow)
    {
        if (ReadState() != null) return;
        // Crashes count from the moment of opting in, never from before.
        WriteState(new State(Guid.NewGuid().ToString("D"), "", utcNow));
    }

    /// <summary>Called when the player turns statistics off: cancels a report in flight and deletes the ID.</summary>
    public void OptOut()
    {
        try { _cts.Cancel(); } catch { }
        _cts = new CancellationTokenSource();
        try { File.Delete(StatePath); } catch { }
        try { File.Delete(StatePath + ".tmp"); } catch { }
    }

    /// <summary>The install ID, or null when the player hasn't opted in (or it was deleted).</summary>
    public string? InstallId => ReadState()?.InstallId;

    // ── Report ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sends today's report if one hasn't gone out yet (UTC day). <paramref name="stillEnabled"/> is
    /// asked again right before sending, so turning it off mid-way stops it. Returns true only
    /// when a report was accepted. Never throws.
    /// </summary>
    public async Task<bool> MaybeSendAsync(Func<bool> stillEnabled, string coreVersion,
        IReadOnlyList<PluginInfo> plugins, int otherPlugins, DateTime utcNow)
    {
        if (Interlocked.Exchange(ref _sending, 1) == 1) return false;
        try
        {
            if (!stillEnabled()) return false;
            State? state = ReadState();
            if (state == null)
            {
                OptIn(utcNow);   // enabled but the file is gone (deleted by hand): start a new ID
                state = ReadState();
                if (state == null) return false;
            }
            string today = utcNow.ToString("yyyy-MM-dd");
            if (state.LastPingDay == today) return false;

            int crashes = CountCrashDumps(_dumpDir, state.CrashesSinceUtc, utcNow);
            string json = BuildPayload(state.InstallId, coreVersion, plugins, otherPlugins, crashes, WindowsMajor());

            CancellationToken ct = _cts.Token;
            if (!stillEnabled() || ct.IsCancellationRequested) return false;
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(_endpoint, content, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;

            // Opted out while the request was out: the file is gone, keep it gone.
            if (!stillEnabled() || ct.IsCancellationRequested || ReadState()?.InstallId != state.InstallId) return true;
            WriteState(state with { LastPingDay = today, CrashesSinceUtc = utcNow });
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _sending, 0);
        }
    }

    /// <summary>The report that would go out now (for "Show what is sent"); nothing is sent or saved.</summary>
    public string Preview(string coreVersion, IReadOnlyList<PluginInfo> plugins, int otherPlugins, DateTime utcNow)
    {
        State? state = ReadState();
        string id = state?.InstallId ?? "(a random ID, made when you turn this on)";
        int crashes = state == null ? 0 : CountCrashDumps(_dumpDir, state.CrashesSinceUtc, utcNow);
        return BuildPayload(id, coreVersion, plugins, otherPlugins, crashes, WindowsMajor());
    }

    /// <summary>The report body, exactly as sent. Public for the launcher's "Show what is sent" and the tests.</summary>
    public static string BuildPayload(string installId, string coreVersion, IReadOnlyList<PluginInfo> plugins,
        int otherPlugins, int crashes, string windows)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", Schema);
            w.WriteString("install", installId);
            w.WriteString("version", ShortVersion(coreVersion));
            w.WriteString("windows", windows);
            w.WriteNumber("crashes", Math.Clamp(crashes, 0, MaxCrashes));
            w.WriteStartArray("plugins");
            foreach (PluginInfo p in plugins
                         .Where(p => SafeName.IsMatch(p.Name))
                         .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                         .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                         .Take(MaxPlugins))
            {
                w.WriteStartObject();
                w.WriteString("name", p.Name);
                w.WriteString("version", ShortVersion(p.Version));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteNumber("otherPlugins", Math.Clamp(otherPlugins, 0, 100));
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// The enabled plugins to report: those whose file name the update feed ships (by the feed's name
    /// for them, with the DLL's own version), and a count of every other enabled plugin.
    /// </summary>
    public static (List<PluginInfo> Known, int Other) DescribePlugins(IEnumerable<string> enabledPaths,
        IReadOnlyDictionary<string, string> feedNameByFile)
    {
        var known = new List<PluginInfo>();
        int other = 0;
        foreach (string path in enabledPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string file = Path.GetFileName(path);
            if (feedNameByFile.TryGetValue(file, out string? name) && File.Exists(path))
            {
                string version;
                try { version = FileVersionInfo.GetVersionInfo(path).ProductVersion ?? ""; }
                catch { version = ""; }
                known.Add(new PluginInfo(name, version));
            }
            else
            {
                other++;
            }
        }
        return (known, other);
    }

    /// <summary>"2026.10.2.1" for a release build, "dev" for an unversioned build, else "unknown". Drops the commit.</summary>
    public static string ShortVersion(string? productVersion)
    {
        string v = (productVersion ?? "").Trim();
        int plus = v.IndexOf('+');
        if (plus >= 0) v = v[..plus];
        int space = v.IndexOf(' ');
        if (space >= 0) v = v[..space];
        if (v is "1.0.0" or "1.0.0.0" or "dev") return "dev";
        return ReleaseVersion.IsMatch(v) ? v : "unknown";
    }

    /// <summary>"11" (build 22000 and up), "10", or "other".</summary>
    public static string WindowsMajor()
    {
        Version os = Environment.OSVersion.Version;
        if (!OperatingSystem.IsWindows()) return "other";
        if (os.Major == 10) return os.Build >= 22000 ? "11" : "10";
        return "other";
    }

    /// <summary>
    /// Crash dump names counted: the engine's crash_*.dmp and the CoreCLR fatal-error dumps the
    /// loader asks for in the same folder (clr_*.dmp, Loader.c ConfigureCrashDumps).
    /// </summary>
    private static readonly string[] CrashDumpPatterns = { "crash_*.dmp", "clr_*.dmp" };

    /// <summary>
    /// Crash dumps (crash_*.dmp, clr_*.dmp) written after <paramref name="sinceUtc"/>. Hang dumps
    /// and the OUT OF MEMORY dumps are not crashes.
    /// </summary>
    public static int CountCrashDumps(string dir, DateTime sinceUtc, DateTime utcNow)
    {
        try
        {
            if (!Directory.Exists(dir)) return 0;
            int n = 0;
            foreach (string pattern in CrashDumpPatterns)
            {
                foreach (string f in Directory.EnumerateFiles(dir, pattern))
                {
                    DateTime t = File.GetLastWriteTimeUtc(f);
                    if (t > sinceUtc && t <= utcNow.AddMinutes(5)) n++;
                    if (n >= MaxCrashes) return n;
                }
            }
            return n;
        }
        catch { return 0; }
    }

    // ── State file ────────────────────────────────────────────────────────────

    private sealed record State(string InstallId, string LastPingDay, DateTime CrashesSinceUtc);

    private State? ReadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(StatePath));
            JsonElement r = doc.RootElement;
            string id = r.GetProperty("installId").GetString() ?? "";
            if (!Guid.TryParseExact(id, "D", out _)) return null;
            string last = r.TryGetProperty("lastPingDay", out JsonElement l) ? l.GetString() ?? "" : "";
            DateTime since = r.TryGetProperty("crashesSinceUtc", out JsonElement s) && s.TryGetDateTime(out DateTime d)
                ? d.ToUniversalTime() : DateTime.UtcNow;
            return new State(id, last, since);
        }
        catch { return null; }
    }

    private void WriteState(State s)
    {
        try
        {
            Directory.CreateDirectory(_stateDir);
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteString("about", "RynthCore usage statistics (opt-in). Deleted when you turn them off.");
                w.WriteString("installId", s.InstallId);
                w.WriteString("lastPingDay", s.LastPingDay);
                w.WriteString("crashesSinceUtc", DateTime.SpecifyKind(s.CrashesSinceUtc, DateTimeKind.Utc));
                w.WriteEndObject();
            }
            string tmp = StatePath + ".tmp";
            File.WriteAllBytes(tmp, ms.ToArray());
            File.Move(tmp, StatePath, overwrite: true);
        }
        catch { }
    }

    private static HttpClient CreateHttp()
    {
        // Short: a slow stats server must never hold anything up (and nothing awaits this anyway).
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RynthCore-Launcher");
        return http;
    }
}
