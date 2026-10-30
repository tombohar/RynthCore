// ============================================================================
//  RynthCore.Engine - Report/ProblemReport.cs
//  "Report a problem": the player writes what went wrong and it reaches a
//  private staff channel on Discord. The engine posts to our server
//  (ProblemReportCore.DefaultEndpoint, https://aelrynth.com/rynth/report); the
//  server holds the Discord webhook, rate-limits, keeps a copy and posts it
//  (C:\Aeshnidae\ops\report-endpoint). The client holds no secret.
//
//  Opened from the bar's grip menu, Settings (the sidebar's foot), "/rc report";
//  "/rc report <text>" sends at once (with the log, the default).
//
//  What is sent: the player's text; character, world, connect host, RynthCore /
//  RynthAi / plugin versions, landcell, pid, engine generation; and - ticked by
//  default, the player can untick it - the last 2,000 lines of this client's
//  RynthCore.<pid>.log (it holds character names and some chat; the account,
//  Windows user and PC names are hidden, ProblemReportCore.Redact; View opens
//  exactly what will be sent).
//
//  Threads:
//    Send / RefreshTail / ViewTail / HandleCommand   any thread (the panel calls them on AC's).
//    The request runs on its own EngineThreads thread with a short-lived HttpClient,
//    disposed when it finishes; Shutdown cancels and disposes one still in flight
//    (the engine hot-reloads into a collectible context: nothing may outlive it).
//    The log tail and the preview file are read / written on UiBackgroundWriter.
//  One report per 60 s per client (ProblemReportCore.CooldownMs), kept in a process
//  environment variable so an engine reload doesn't reset it.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using RynthCore.Engine.Compatibility;

namespace RynthCore.Engine.Report;

internal enum ReportState { Idle, Sending, Sent, Failed }

internal static class ProblemReport
{
    public const string PanelTitle = "Report a problem";

    // Process-wide, so a hot reload (a new engine generation) keeps the cooldown.
    private const string NextAllowedEnv = "RYNTHCORE_REPORT_NEXT_MS";

    private static readonly object Sync = new();
    private static int _state;                    // ReportState
    private static string _message = "";
    private static long _resultSeq;               // bumps on every finished send (the panel clears its text on success)
    private static long _messageSeq;              // bumps on every new message (the panel shows only ones newer than its opening)
    private static CancellationTokenSource _cts = new();
    private static HttpClient? _inFlight;
    private static volatile bool _shutdown;

    private static volatile LogTail? _tail;
    private static volatile string _tailError = "";
    private static int _tailLoading;

    public static ReportState State => (ReportState)Volatile.Read(ref _state);
    public static string Message { get { lock (Sync) return _message; } }
    public static long ResultSeq => Interlocked.Read(ref _resultSeq);
    public static long MessageSeq => Interlocked.Read(ref _messageSeq);

    /// <summary>The log tail the panel shows and sends (null until read, or when there's no log).</summary>
    public static LogTail? Tail => _tail;
    public static string TailError => _tailError;
    public static bool TailLoading => Volatile.Read(ref _tailLoading) != 0;

    /// <summary>Milliseconds until another report may be sent (0 = now).</summary>
    public static long CooldownLeftMs => ProblemReportCore.CooldownLeft(Environment.TickCount64, NextAllowedMs());

    // ── The log tail ─────────────────────────────────────────────────────

    /// <summary>Reads this client's log tail again (background). Any thread.</summary>
    public static void RefreshTail()
    {
        if (Interlocked.Exchange(ref _tailLoading, 1) != 0) return;
        UI.UiBackgroundWriter.Enqueue("report log tail", static () =>
        {
            try
            {
                LogTail? t = ReadTailNow();
                _ = t?.Gzip;   // compressed here, not on AC's thread when the panel first shows the size
                _tail = t;
                _tailError = _tail == null ? "There's no RynthCore log for this client yet." : "";
            }
            catch (Exception ex)
            {
                _tail = null;
                _tailError = "Couldn't read the log: " + ex.Message;
                RynthLog.Warn($"ProblemReport: reading the log tail failed - {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref _tailLoading, 0);
            }
        });
    }

    // The account (login) name, the Windows user name and the PC's name are hidden from the tail
    // (ProblemReportCore.Redact), so neither View nor the report shows them.
    private static LogTail? ReadTailNow() => ProblemReportCore.ReadTail(LogPaths.LogFilePath, LogPaths.RotatedLogFilePath,
        hide: new[]
        {
            (SessionStateRegistry.LastAccountName ?? "", ProblemReportCore.HiddenAccount),
            (SafeEnv(() => Environment.UserName), ProblemReportCore.HiddenUser),
            (SafeEnv(() => Environment.MachineName), "<pc>"),
        });

    private static string SafeEnv(Func<string> read)
    {
        try { return read() ?? ""; }
        catch { return ""; }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ShellExecuteW")]
    private static extern IntPtr ShellExecuteW(IntPtr hwnd, string lpOperation, string lpFile,
        string? lpParameters, string? lpDirectory, int nShowCmd);

    /// <summary>
    /// Writes <paramref name="tail"/> - exactly the bytes a report would carry - to
    /// Logs\report-preview-&lt;pid&gt;.txt and opens it (the default text editor). Any thread.
    /// ShellExecute, not Process.Start (the Process API is an AV hazard inside acclient.exe).
    /// </summary>
    public static void ViewTail(LogTail tail)
    {
        UI.UiBackgroundWriter.Enqueue("report log preview", () =>
        {
            try
            {
                string path = Path.Combine(LogPaths.LogDirectory, $"report-preview-{Environment.ProcessId}.txt");
                File.WriteAllBytes(path, tail.Text);
                IntPtr r = ShellExecuteW(IntPtr.Zero, "open", path, null, LogPaths.LogDirectory, 1 /* SW_SHOWNORMAL */);
                if (r.ToInt64() <= 32)
                    SetMessage(State, $"Couldn't open the preview (code {r.ToInt64()}); it is at {path}");
            }
            catch (Exception ex)
            {
                RynthLog.Warn($"ProblemReport: preview failed - {ex.GetType().Name}: {ex.Message}");
                SetMessage(State, "Couldn't write the preview: " + ex.Message);
            }
        });
    }

    // ── Sending ──────────────────────────────────────────────────────────

    /// <summary>
    /// Starts sending a report. <paramref name="tail"/> is the log to attach (null = none;
    /// <paramref name="readTailIfNull"/> reads it fresh on the send thread, for /rc report text).
    /// False with <paramref name="error"/> when it can't start (no text, cooldown, already
    /// sending). <paramref name="announceInChat"/> = also say the result in chat. Any thread.
    /// </summary>
    public static bool Send(string description, string doing, LogTail? tail, bool readTailIfNull, bool announceInChat, out string error)
    {
        error = ProblemReportCore.ValidateDescription(description);
        if (error.Length > 0) return false;
        if (_shutdown) { error = "RynthCore is shutting down."; return false; }

        ReportContext ctx;
        try { ctx = GatherContext(); }
        catch (Exception ex)
        {
            RynthLog.Warn($"ProblemReport: gathering the client details failed - {ex.GetType().Name}: {ex.Message}");
            ctx = new ReportContext { Pid = Environment.ProcessId, RynthCoreVersion = EntryPoint.BuildStamp };
        }

        CancellationToken token;
        lock (Sync)
        {
            if ((ReportState)_state == ReportState.Sending) { error = "A report is already being sent."; return false; }
            long left = CooldownLeftMs;
            if (left > 0) { error = $"You can send another report in {ProblemReportCore.FormatWait(left)}."; return false; }
            _state = (int)ReportState.Sending;
            _message = "Sending...";
            Interlocked.Increment(ref _messageSeq);
            SetNextAllowed(Environment.TickCount64 + ProblemReportCore.CooldownMs);
            token = _cts.Token;
        }

        string endpoint = ProblemReportCore.ResolveEndpoint(Plugins.EngineSettings.ReportEndpoint, out string? rejected);
        if (rejected != null)
            RynthLog.Warn($"ProblemReport: engine.json ReportEndpoint '{rejected}' ignored (https only, or http to this PC); using {endpoint}.");

        try
        {
            EngineThreads.Start("ProblemReport send", () => SendWorker(endpoint, description, doing, ctx, tail, readTailIfNull, announceInChat, token));
        }
        catch (Exception ex)
        {
            Finish(false, "Couldn't start sending: " + ex.Message, announceInChat, reachedServer: false);
            error = "Couldn't start sending: " + ex.Message;
            return false;
        }
        return true;
    }

    private static void SendWorker(string endpoint, string description, string doing, ReportContext ctx, LogTail? tail,
        bool readTailIfNull, bool announceInChat, CancellationToken token)
    {
        HttpClient? http = null;
        try
        {
            if (tail == null && readTailIfNull)
            {
                try { tail = ReadTailNow(); }
                catch (Exception ex) { RynthLog.Warn($"ProblemReport: reading the log tail failed - {ex.GetType().Name}: {ex.Message}; sending without it."); }
            }
            if (tail != null && tail.Gzip.Length > ProblemReportCore.MaxGzipBytes)
            {
                RynthLog.Warn($"ProblemReport: the log is {tail.Gzip.Length} bytes gzipped (limit {ProblemReportCore.MaxGzipBytes}); sending without it.");
                tail = null;
            }
            byte[] body = ProblemReportCore.BuildPayload(description, doing, ctx, tail, DateTime.UtcNow);

            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                PooledConnectionLifetime = TimeSpan.FromSeconds(30),
            };
            http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(45) };
            lock (Sync)
            {
                if (_shutdown) return;
                _inFlight = http;
            }

            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(body) };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            req.Headers.UserAgent.ParseAdd("RynthCore-Report/1");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using HttpResponseMessage resp = http.Send(req, HttpCompletionOption.ResponseHeadersRead, token);
            int status = (int)resp.StatusCode;
            string answer = ReadSmall(resp, token);
            bool ok = ProblemReportCore.ReadResponse(status, answer, out string message, out string id);
            RynthLog.Info($"ProblemReport: HTTP {status} from {endpoint} - {(ok ? "taken" + (id.Length > 0 ? " as " + id : "") : message)} " +
                          $"(body {body.Length} bytes, log {(tail == null ? "not attached" : $"{tail.Lines} lines")}).");
            Finish(ok, message, announceInChat, reachedServer: true);
        }
        catch (OperationCanceledException) when (_shutdown || token.IsCancellationRequested)
        {
            Finish(false, "Cancelled (RynthCore was reloading).", announceInChat, reachedServer: false);
        }
        catch (OperationCanceledException)
        {
            Finish(false, "The report service didn't answer in time. Please try again in a moment.", announceInChat, reachedServer: false);
        }
        catch (HttpRequestException ex)
        {
            RynthLog.Warn($"ProblemReport: network error - {ex.Message}");
            Finish(false, "Couldn't reach the report service (" + ex.Message + "). Check your connection and try again.", announceInChat, reachedServer: false);
        }
        catch (Exception ex)
        {
            RynthLog.Warn($"ProblemReport: send failed - {ex.GetType().Name}: {ex.Message}");
            Finish(false, $"Couldn't send the report ({ex.GetType().Name}).", announceInChat, reachedServer: false);
        }
        finally
        {
            lock (Sync)
            {
                if (ReferenceEquals(_inFlight, http)) _inFlight = null;
            }
            try { http?.Dispose(); } catch { }
        }
    }

    /// <summary>The answer's first 8 KB as text (the server's answers are tiny).</summary>
    private static string ReadSmall(HttpResponseMessage resp, CancellationToken token)
    {
        try
        {
            using Stream s = resp.Content.ReadAsStream(token);
            var buf = new byte[8192];
            int total = 0, n;
            while (total < buf.Length && (n = s.Read(buf, total, buf.Length - total)) > 0) total += n;
            return Encoding.UTF8.GetString(buf, 0, total);
        }
        catch
        {
            return "";
        }
    }

    private static void Finish(bool ok, string message, bool announceInChat, bool reachedServer)
    {
        lock (Sync)
        {
            _state = (int)(ok ? ReportState.Sent : ReportState.Failed);
            _message = message;
            // A send that never reached the server may be retried soon; one that did keeps the full minute.
            if (!ok && !reachedServer)
                SetNextAllowed(Environment.TickCount64 + ProblemReportCore.RetryAfterFailureMs);
            Interlocked.Increment(ref _resultSeq);
            Interlocked.Increment(ref _messageSeq);
        }
        if (announceInChat && !_shutdown)
            AcMainThreadQueue.EnqueueWriteToChat("[RynthCore] Report: " + message, 1);
    }

    private static void SetMessage(ReportState state, string message)
    {
        lock (Sync)
        {
            _state = (int)state;
            _message = message;
            Interlocked.Increment(ref _messageSeq);
        }
    }

    // Read from the process environment once per engine generation; the panel reads the field each frame.
    private static long _nextAllowedMs = ReadNextAllowedEnv();

    private static long NextAllowedMs() => Interlocked.Read(ref _nextAllowedMs);

    private static long ReadNextAllowedEnv()
    {
        try
        {
            return long.TryParse(Environment.GetEnvironmentVariable(NextAllowedEnv), out long v) ? v : 0;
        }
        catch { return 0; }
    }

    private static void SetNextAllowed(long tickMs)
    {
        Interlocked.Exchange(ref _nextAllowedMs, tickMs);
        try { Environment.SetEnvironmentVariable(NextAllowedEnv, tickMs.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        catch { }
    }

    // ── What the report says about the client ───────────────────────────

    /// <summary>Character, world, versions, landcell, pid. Any thread (cached reads only).</summary>
    public static ReportContext GatherContext()
    {
        var ctx = new ReportContext
        {
            Pid = Environment.ProcessId,
            RynthCoreVersion = EntryPoint.BuildStamp,
            ApiVersion = (int)Plugins.PluginContractVersion.Current,
            Generation = EntryPoint.InitCount,
        };

        string character = "";
        uint player = ClientHelperHooks.GetPlayerId();
        if (player != 0 && ClientObjectHooks.TryGetSnapshotName(player, out string live)) character = live;
        if (string.IsNullOrWhiteSpace(character)) character = SessionStateRegistry.LastCharacterName ?? "";
        ctx.Character = character;

        string world = AccountHooks.AnnouncedWorldName ?? "";
        if (string.IsNullOrWhiteSpace(world)) world = SessionStateRegistry.LastServerName ?? "";
        ctx.World = world;
        string host = ServerInfo.HostDescription;
        ctx.Connect = host == "(none)" ? "" : host;
        ctx.Aelrynth = ServerInfo.IsAelrynth;

        try
        {
            // A copy (the list changes only on load / unload / RL; a copy taken mid-change just lists less).
            Plugins.LoadedPlugin[] plugins = System.Linq.Enumerable.ToArray(Plugins.PluginManager.Plugins);
            foreach (Plugins.LoadedPlugin p in plugins)
            {
                string name = p.DisplayName.Length > 0 ? p.DisplayName : Path.GetFileNameWithoutExtension(p.FileName);
                string version = p.VersionString + (p.Failed ? " (failed)" : p.NotStartedReason.Length > 0 ? " (not started)" : "");
                ctx.Plugins.Add((name, version));
                if (ctx.RynthAiVersion.Length == 0 && !p.Failed
                    && (name.Equals("RynthAi", StringComparison.OrdinalIgnoreCase)
                        || p.FileName.Equals("RynthCore.Plugin.RynthAi.dll", StringComparison.OrdinalIgnoreCase)))
                    ctx.RynthAiVersion = p.VersionString.Length > 0 ? p.VersionString : "loaded";
            }
        }
        catch (Exception ex)
        {
            RynthLog.Verbose($"ProblemReport: plugin list unavailable - {ex.Message}");
        }

        if (ClientObjectHooks.TryGetPlayerStats(out _, out _, out _, out _, out _, out _, out uint landcell, out _, out _, out _))
            ctx.Landcell = landcell;
        return ctx;
    }

    // ── /rc report ───────────────────────────────────────────────────────

    /// <summary>"/rc report" opens the panel; "/rc report &lt;text&gt;" sends it now (with the log). Lines for chat.</summary>
    public static IEnumerable<string> HandleCommand(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            if (!ImGuiBackend.ImGuiPanelHost.HasFace(PanelTitle))
                return new[] { "The Report a problem panel isn't available (the ImGui layer is off?). Use /rc report <what went wrong> to send one from chat." };
            OpenPanel();
            return Array.Empty<string>();
        }
        if (!Send(args, "", tail: null, readTailIfNull: true, announceInChat: true, out string error))
            return new[] { "Report not sent: " + error };
        return new[] { "Sending your report (with your recent RynthCore log; use the panel, /rc report, to leave the log out)..." };
    }

    /// <summary>Opens the panel (it reads the log tail when it opens). Any thread.</summary>
    public static void OpenPanel()
    {
        if (!ImGuiBackend.ImGuiPanelHost.IsOpen(PanelTitle))
            UI.PanelRouter.Toggle(PanelTitle);
    }

    // ── Shutdown ─────────────────────────────────────────────────────────

    /// <summary>Cancels a send in flight and disposes its HttpClient (engine shutdown / hot reload).</summary>
    public static void Shutdown()
    {
        HttpClient? http;
        lock (Sync)
        {
            _shutdown = true;
            http = _inFlight;
            _inFlight = null;
        }
        try { _cts.Cancel(); } catch { }
        try { http?.Dispose(); } catch { }
        _tail = null;
    }
}
