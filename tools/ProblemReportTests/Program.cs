using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RynthCore.Engine.Report;

namespace ProblemReportTests;

internal static class Program
{
    private static int _checks, _failed;
    private static string _case = "";
    private static string _tmp = "";

    private static int Main(string[] args)
    {
        _tmp = Path.Combine(Path.GetTempPath(), "ProblemReportTests-" + Environment.ProcessId);
        Directory.CreateDirectory(_tmp);
        try
        {
            TextCleaning();
            Utf8Limits();
            Validation();
            PayloadShape();
            PayloadLimits();
            PayloadNoLog();
            TailLastLines();
            TailByteCap();
            TailPartialLastLine();
            TailCrlfAndEdges();
            TailTopsUpFromOld();
            TailWhileWriting();
            AccountHidden();
            Cooldown();
            Endpoint();
            Responses();
            Sizes();

            int at = Array.IndexOf(args, "server");
            if (at >= 0 && at + 1 < args.Length) AgainstServer(args[at + 1]).GetAwaiter().GetResult();
        }
        finally
        {
            try { Directory.Delete(_tmp, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine($"{_checks - _failed}/{_checks} checks passed.");
        Console.WriteLine(_failed == 0 ? "ALL PROBLEM REPORT TESTS PASSED." : $"{_failed} FAILED.");
        return _failed == 0 ? 0 : 1;
    }

    private static void Case(string name) { _case = name; Console.WriteLine("-- " + name); }

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (ok) return;
        _failed++;
        Console.WriteLine($"FAIL [{_case}] {what}");
    }

    private static void Eq<T>(T actual, T expected, string what) =>
        Check(EqualityComparer<T>.Default.Equals(actual, expected), $"{what}: expected <{expected}>, got <{actual}>");

    // ── Text ─────────────────────────────────────────────────────────────

    private static void TextCleaning()
    {
        Case("text cleaning");
        Eq(ProblemReportCore.CleanText("  hello\r\nworld\r  ", 100), "hello\nworld", "CRLF/CR to LF, trimmed");
        Eq(ProblemReportCore.CleanText("a\u0000b\u0007c\td", 100), "abc\td", "control chars dropped, tab kept");
        Eq(ProblemReportCore.CleanText(null, 100), "", "null");
        Eq(ProblemReportCore.CleanShort(" Tom\nthe\tGreat ", 64), "TomtheGreat", "one-line field drops newline and tab");
        Eq(ProblemReportCore.CleanShort(new string('x', 70), 64).Length, 64, "short field cut to 64");
        string emoji = new string('a', 63) + "\U0001F600";
        Check(!char.IsHighSurrogate(ProblemReportCore.CleanShort(emoji, 64)[^1]), "short field never ends on a lone high surrogate");
        Eq(ProblemReportCore.PluginName("Rynth<Ai>!"), "RynthAi", "plugin name cleaned");
        Eq(ProblemReportCore.PluginName(new string('p', 50)).Length, 40, "plugin name max 40");
    }

    private static void Utf8Limits()
    {
        Case("UTF-8 limits");
        Eq(ProblemReportCore.TrimUtf8("abcdef", 4), "abcd", "ascii cut");
        Eq(ProblemReportCore.TrimUtf8("aé", 2), "a", "2-byte char not split");
        Eq(ProblemReportCore.TrimUtf8("a\U0001F600", 4), "a", "4-byte char not split");
        Eq(ProblemReportCore.TrimUtf8("a\U0001F600", 5), "a\U0001F600", "4-byte char fits");
        string big = string.Concat(Enumerable.Repeat("ü", 3000));   // 6000 bytes
        string cut = ProblemReportCore.CleanText(big, ProblemReportCore.MaxDescriptionBytes);
        Check(Encoding.UTF8.GetByteCount(cut) <= ProblemReportCore.MaxDescriptionBytes, "description <= 2500 bytes");
        Eq(cut.Length, 1250, "1250 two-byte chars");
    }

    private static void Validation()
    {
        Case("validation");
        Check(ProblemReportCore.ValidateDescription("").Length > 0, "empty refused");
        Check(ProblemReportCore.ValidateDescription("   \r\n ").Length > 0, "blank refused");
        Check(ProblemReportCore.ValidateDescription("ab").Length > 0, "too short refused");
        Eq(ProblemReportCore.ValidateDescription("bot stuck"), "", "ok accepted");
    }

    // ── Payload ──────────────────────────────────────────────────────────

    private static ReportContext SampleContext() => new()
    {
        Character = "Drakkon",
        World = "Aelrynth",
        Connect = "aelrynth.com:9000",
        Aelrynth = true,
        RynthCoreVersion = "2026.10.9.3 (194ea47)",
        ApiVersion = 79,
        Generation = 2,
        RynthAiVersion = "2026.10.9.3 (e6fffb0)",
        Plugins = { ("RynthAi", "2026.10.9.3 (e6fffb0)"), ("RynthNav", "dev (abc1234)"), ("rynthai", "dupe") },
        Landcell = 0x12340105,
        Pid = 4242,
    };

    private static readonly string[] TopKeys =
    {
        "schema", "description", "doing", "character", "world", "connect", "aelrynth", "rynthcore", "api",
        "generation", "rynthai", "plugins", "landcell", "pid", "clientTime", "log",
    };

    private static void PayloadShape()
    {
        Case("payload shape");
        string logText = string.Join("\n", Enumerable.Range(1, 50).Select(i => $"[12:00:{i:00}.000] [pid:4242] [INF] [engine] line {i}")) + "\n";
        var tail = new LogTail { FileName = "RynthCore.4242.log", Text = Encoding.UTF8.GetBytes(logText), Lines = 50 };
        byte[] body = ProblemReportCore.BuildPayload("Bot <@everyone> stuck **here**", "hunting\r\nwith bot", SampleContext(), tail,
            new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc));
        using var doc = JsonDocument.Parse(body);
        JsonElement r = doc.RootElement;
        var keys = r.EnumerateObject().Select(p => p.Name).ToArray();
        Check(keys.OrderBy(k => k).SequenceEqual(TopKeys.OrderBy(k => k)), "exactly the schema's keys: " + string.Join(",", keys));
        Eq(r.GetProperty("schema").GetInt32(), 1, "schema");
        Eq(r.GetProperty("description").GetString(), "Bot <@everyone> stuck **here**", "description kept verbatim (the server escapes)");
        Eq(r.GetProperty("doing").GetString(), "hunting\nwith bot", "doing CRLF -> LF");
        Eq(r.GetProperty("character").GetString(), "Drakkon", "character");
        Eq(r.GetProperty("aelrynth").GetBoolean(), true, "aelrynth");
        Eq(r.GetProperty("api").GetInt32(), 79, "api");
        Eq(r.GetProperty("landcell").GetString(), "0x12340105", "landcell hex");
        Eq(r.GetProperty("pid").GetInt32(), 4242, "pid");
        Eq(r.GetProperty("clientTime").GetString(), "2026-10-11T12:00:00Z", "clientTime");
        Eq(r.GetProperty("plugins").GetArrayLength(), 2, "plugin dupes (any case) dropped");
        Eq(r.GetProperty("plugins")[1].GetProperty("name").GetString(), "RynthNav", "second plugin");
        JsonElement log = r.GetProperty("log");
        Eq(log.GetProperty("name").GetString(), "RynthCore.4242.log", "log name");
        Eq(log.GetProperty("lines").GetInt32(), 50, "log lines");
        Eq(log.GetProperty("bytes").GetInt32(), tail.Text.Length, "log bytes");
        byte[] gz = log.GetProperty("gzip").GetBytesFromBase64();
        using var unz = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress);
        using var back = new MemoryStream();
        unz.CopyTo(back);
        Eq(Encoding.UTF8.GetString(back.ToArray()), logText, "log round-trips through gzip + base64");
        Check(gz.Length < tail.Text.Length, "gzip is smaller");
    }

    private static void PayloadLimits()
    {
        Case("payload limits");
        var ctx = SampleContext();
        ctx.Character = new string('C', 200);
        ctx.Connect = new string('h', 300);
        ctx.Landcell = 0;
        ctx.Plugins.Clear();
        for (int i = 0; i < 50; i++) ctx.Plugins.Add(($"Plugin{i}", "1"));
        byte[] body = ProblemReportCore.BuildPayload(new string('d', 5000), new string('w', 5000), ctx, null, DateTime.UtcNow);
        using var doc = JsonDocument.Parse(body);
        JsonElement r = doc.RootElement;
        Eq(r.GetProperty("description").GetString()!.Length, ProblemReportCore.MaxDescriptionBytes, "description capped");
        Eq(r.GetProperty("doing").GetString()!.Length, ProblemReportCore.MaxDoingBytes, "doing capped");
        Eq(r.GetProperty("character").GetString()!.Length, ProblemReportCore.MaxShortField, "character capped");
        Eq(r.GetProperty("connect").GetString()!.Length, ProblemReportCore.MaxConnectField, "connect capped");
        Eq(r.GetProperty("plugins").GetArrayLength(), ProblemReportCore.MaxPlugins, "plugins capped at 32");
        Eq(r.GetProperty("landcell").GetString(), "", "no landcell = empty");
        Check(body.Length < 8192, $"no-log body small ({body.Length} bytes)");
    }

    private static void PayloadNoLog()
    {
        Case("payload without log");
        byte[] body = ProblemReportCore.BuildPayload("x went wrong", "", SampleContext(), null, DateTime.UtcNow);
        using var doc = JsonDocument.Parse(body);
        Eq(doc.RootElement.GetProperty("log").ValueKind, JsonValueKind.Null, "log null when unticked");
        var empty = new LogTail { FileName = "a.log", Text = Array.Empty<byte>() };
        body = ProblemReportCore.BuildPayload("x went wrong", "", SampleContext(), empty, DateTime.UtcNow);
        using var doc2 = JsonDocument.Parse(body);
        Eq(doc2.RootElement.GetProperty("log").ValueKind, JsonValueKind.Null, "empty log sent as null");
    }

    // ── Log tail ─────────────────────────────────────────────────────────

    private static string WriteLines(string name, int count, string newline = "\n", bool trailing = true, int pad = 0)
    {
        string path = Path.Combine(_tmp, name);
        var sb = new StringBuilder();
        for (int i = 1; i <= count; i++)
        {
            sb.Append("line ").Append(i).Append(new string('.', pad));
            if (i < count || trailing) sb.Append(newline);
        }
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    private static string[] LinesOf(LogTail t) =>
        Encoding.UTF8.GetString(t.Text).Split('\n', StringSplitOptions.None).Where(s => s.Length > 0).ToArray();

    private static void TailLastLines()
    {
        Case("tail: last N lines");
        string path = WriteLines("a.log", 5000);
        LogTail t = ProblemReportCore.ReadTail(path, null, 2000, 10_000_000)!;
        string[] lines = LinesOf(t);
        Eq(t.Lines, 2000, "2000 lines");
        Eq(lines.Length, 2000, "2000 lines in text");
        Eq(lines[0], "line 3001", "first kept line");
        Eq(lines[^1], "line 5000", "last line");
        Check(t.Text[^1] == (byte)'\n', "ends with the newline");
        Eq(t.FileName, "a.log", "file name");

        LogTail small = ProblemReportCore.ReadTail(WriteLines("b.log", 10), null, 2000, 10_000_000)!;
        Eq(small.Lines, 10, "short file: all lines");
        Eq(LinesOf(small)[0], "line 1", "short file from the start");

        // Exactly the chunk size boundary: 64 KB lines.
        string big = WriteLines("c.log", 3000, pad: 60);
        LogTail tb = ProblemReportCore.ReadTail(big, null, 2000, 10_000_000)!;
        Eq(tb.Lines, 2000, "multi-chunk read: 2000 lines");
        Eq(LinesOf(tb)[0], "line 1001" + new string('.', 60), "multi-chunk first line whole");
    }

    private static void TailByteCap()
    {
        Case("tail: byte cap keeps the end");
        string path = WriteLines("cap.log", 2000, pad: 90);   // ~100 bytes a line, ~200 KB
        LogTail t = ProblemReportCore.ReadTail(path, null, 2000, 50_000)!;
        Check(t.Text.Length <= 50_000, $"<= cap ({t.Text.Length})");
        string[] lines = LinesOf(t);
        Check(lines[0].StartsWith("line ") && lines[0].EndsWith(new string('.', 90)), "first line whole: " + lines[0][..Math.Min(20, lines[0].Length)]);
        Eq(lines[^1], "line 2000" + new string('.', 90), "last line kept");
        Eq(t.Lines, lines.Length, "line count matches");
        Check(t.Lines < 2000 && t.Lines > 400, $"about 490 lines ({t.Lines})");
    }

    private static void TailPartialLastLine()
    {
        Case("tail: last line still being written");
        string path = WriteLines("p.log", 3000, trailing: false);
        LogTail t = ProblemReportCore.ReadTail(path, null, 2000, 10_000_000)!;
        string[] lines = LinesOf(t);
        Eq(t.Lines, 2000, "2000 lines incl. the unfinished one");
        Eq(lines[^1], "line 3000", "unfinished last line kept");
        Eq(lines[0], "line 1001", "first");
    }

    private static void TailCrlfAndEdges()
    {
        Case("tail: CRLF, empty, missing");
        string path = WriteLines("crlf.log", 2500, newline: "\r\n");
        LogTail t = ProblemReportCore.ReadTail(path, null, 2000, 10_000_000)!;
        Eq(t.Lines, 2000, "CRLF lines");
        Check(Encoding.UTF8.GetString(t.Text).StartsWith("line 501\r\n"), "CRLF first line whole");
        File.WriteAllText(Path.Combine(_tmp, "empty.log"), "");
        LogTail e = ProblemReportCore.ReadTail(Path.Combine(_tmp, "empty.log"), null)!;
        Eq(e.Lines, 0, "empty file: no lines");
        Eq(e.Text.Length, 0, "empty file: no bytes");
        Check(ProblemReportCore.ReadTail(Path.Combine(_tmp, "nope.log"), null) == null, "missing file: null");
        Eq(ProblemReportCore.CountLines("a\nb"u8), 2, "count: unfinished last line");
        Eq(ProblemReportCore.CountLines("a\nb\n"u8), 2, "count: trailing newline");
        Eq(ProblemReportCore.CountLines(""u8), 0, "count: empty");
    }

    private static void TailTopsUpFromOld()
    {
        Case("tail: rotated .old tops up a short log");
        string old = WriteLines("r.log.old", 3000, trailing: false);   // the old file's last line lacks a newline
        File.WriteAllText(Path.Combine(_tmp, "r.log"), "new 1\nnew 2\nnew 3\n");
        LogTail t = ProblemReportCore.ReadTail(Path.Combine(_tmp, "r.log"), old, 2000, 10_000_000)!;
        string[] lines = LinesOf(t);
        Eq(t.Lines, 2000, "2000 lines in all");
        Check(t.IncludesOld, "marked as including .old");
        Eq(lines[0], "line 1004", "old part starts 1997 lines back");
        Eq(lines[1996], "line 3000", "old part's last line");
        Eq(lines[1997], "new 1", "then the current log");
        Eq(lines[^1], "new 3", "ends with the current log");

        LogTail full = ProblemReportCore.ReadTail(WriteLines("f.log", 2500), old, 2000, 10_000_000)!;
        Check(!full.IncludesOld, "a full current log doesn't touch .old");
    }

    private static void TailWhileWriting()
    {
        Case("tail: file being appended to");
        string path = Path.Combine(_tmp, "live.log");
        using var stop = new CancellationTokenSource();
        long written = 0;
        // The engine's writer: open, append one line, close, with FileShare.ReadWrite (EntryPoint.AppendWithRetry).
        var writer = new Thread(() =>
        {
            int i = 0;
            while (!stop.IsCancellationRequested)
            {
                i++;
                string line = $"[12:00:00.000] [pid:1] [INF] [engine] hb #{i} up={i}s fps=60 plug=30/s ws=400MB login=1\r\n";
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    try
                    {
                        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                        byte[] b = Encoding.UTF8.GetBytes(line);
                        // Two writes, so a reader can land between them (a half-written line).
                        fs.Write(b, 0, b.Length / 2);
                        fs.Flush();
                        fs.Write(b, b.Length / 2, b.Length - b.Length / 2);
                        Interlocked.Increment(ref written);
                        break;
                    }
                    catch (IOException) { Thread.Sleep(1); }
                }
            }
        }) { IsBackground = true };
        writer.Start();
        while (Interlocked.Read(ref written) < 3000) Thread.Sleep(5);

        int reads = 0, bad = 0;
        var until = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < until)
        {
            LogTail t = ProblemReportCore.ReadTail(path, null, 2000, 10_000_000)!;
            reads++;
            string text = Encoding.UTF8.GetString(t.Text);
            string[] lines = text.Split("\r\n");
            // Whole lines, except possibly the very last one (being written now).
            for (int k = 0; k < lines.Length - 1; k++)
                if (!lines[k].StartsWith("[12:00:00.000]") || !lines[k].EndsWith("login=1")) { bad++; break; }
            if (t.Lines != 2000) bad++;
        }
        stop.Cancel();
        writer.Join();
        Check(reads > 5, $"read repeatedly while writing ({reads} reads)");
        Eq(bad, 0, "every read: 2000 lines, all whole but the last");
    }

    private static void AccountHidden()
    {
        Case("tail: account, Windows user and PC names hidden");
        string path = Path.Combine(_tmp, "acct.log");
        File.WriteAllText(path,
            "[12:00:00.000] [pid:1] [INF] [engine] SessionState: recorded login session for PID 1 account='MyLogin42' character='Drakkon' (IsLoggedIn=true).\n" +
            "[12:00:01.000] [pid:1] [INF] [engine] launch context for mylogin42 read\n" +
            "[12:00:02.000] [pid:1] [INF] [engine] account='' (empty field)\n" +
            "[12:00:03.000] [pid:1] [INF] [engine] watching 'C:\\Users\\Jane Q. Doe\\AppData\\Roaming\\RynthCore\\dispatch.txt'.\n" +
            "[12:00:04.000] [pid:1] [INF] [engine] d:/users/jdoe/x.txt on JANES-PC as jdoe; Drakkon says hi\n");
        var hide = new[] { ("MyLogin42", ProblemReportCore.HiddenAccount), ("jdoe", ProblemReportCore.HiddenUser), ("JANES-PC", "<pc>") };
        LogTail t = ProblemReportCore.ReadTail(path, null, hide: hide)!;
        string text = Encoding.UTF8.GetString(t.Text);
        Check(!text.Contains("MyLogin42", StringComparison.OrdinalIgnoreCase), "login name gone (any case)");
        Check(text.Contains("account='<account>' character='Drakkon'"), "account field hidden, character kept");
        Check(text.Contains("launch context for <account> read"), "free-text mention hidden");
        Check(text.Contains("account='<account>' (empty field)"), "every account= field hidden");
        Check(text.Contains(@"'C:\Users\<user>\AppData\Roaming\RynthCore\dispatch.txt'"), "profile folder hidden (with spaces): " + text.Split('\n')[3]);
        Check(text.Contains("d:/users/<user>/x.txt on <pc> as <user>; Drakkon says hi"), "forward-slash path, user and PC names hidden: " + text.Split('\n')[4]);
        Check(!text.Contains("Jane") && !text.Contains("jdoe") && !text.Contains("JANES"), "nothing of the names left");
        Eq(t.Lines, 5, "line count unchanged");
        Check(t.Gzip.Length > 0 && t.Text.Length == Encoding.UTF8.GetByteCount(text), "size and gzip are of the hidden text");

        LogTail noHide = ProblemReportCore.ReadTail(path, null, hide: new[] { ("", "<x>"), ("ab", "<x>") })!;
        Check(Encoding.UTF8.GetString(noHide.Text).Contains("for mylogin42 read"), "short/empty hide words ignored (only the field and paths are hidden)");
        byte[] plain = Encoding.UTF8.GetBytes("nothing to hide\n");
        Check(ReferenceEquals(ProblemReportCore.Redact(plain, hide), plain), "unchanged text is the same array");
        byte[] dollar = Encoding.UTF8.GetBytes("user bob here\n");
        Eq(Encoding.UTF8.GetString(ProblemReportCore.Redact(dollar, new[] { ("bob", "$1<x>") })), "user $1<x> here\n", "replacement text taken literally");
    }

    // ── Cooldown, endpoint, answers ──────────────────────────────────────

    private static void Cooldown()
    {
        Case("cooldown");
        long now = 1_000_000;
        long next = now + ProblemReportCore.CooldownMs;
        Eq(ProblemReportCore.CooldownLeft(now, 0), 0L, "never sent: now");
        Eq(ProblemReportCore.CooldownLeft(now, next), 60_000L, "just sent: 60 s");
        Eq(ProblemReportCore.CooldownLeft(now + 59_999, next), 1L, "1 ms left");
        Eq(ProblemReportCore.CooldownLeft(now + 60_000, next), 0L, "after 60 s: now");
        Eq(ProblemReportCore.CooldownLeft(now + 90_000, next), 0L, "long after: now");
        Eq(ProblemReportCore.FormatWait(42_000), "42 s", "42 s");
        Eq(ProblemReportCore.FormatWait(41_001), "42 s", "rounds up");
        Eq(ProblemReportCore.FormatWait(65_000), "1 min 5 s", "over a minute");
        Check(ProblemReportCore.RetryAfterFailureMs < ProblemReportCore.CooldownMs, "a failed send waits less");
    }

    private static void Endpoint()
    {
        Case("endpoint override");
        Eq(ProblemReportCore.ResolveEndpoint(null, out var r0), ProblemReportCore.DefaultEndpoint, "none: default");
        Check(r0 == null, "none: nothing rejected");
        Eq(ProblemReportCore.ResolveEndpoint("https://staging.example.org/rynth/report", out _), "https://staging.example.org/rynth/report", "https ok");
        Eq(ProblemReportCore.ResolveEndpoint("http://127.0.0.1:18097/report", out _), "http://127.0.0.1:18097/report", "http loopback ok");
        Eq(ProblemReportCore.ResolveEndpoint("http://localhost:18097/report", out _), "http://localhost:18097/report", "http localhost ok");
        Eq(ProblemReportCore.ResolveEndpoint("http://evil.example.org/report", out var r1), ProblemReportCore.DefaultEndpoint, "plain http elsewhere refused");
        Check(r1 != null, "refusal reported");
        Eq(ProblemReportCore.ResolveEndpoint("https://user:pw@example.org/", out _), ProblemReportCore.DefaultEndpoint, "credentials in URL refused");
        Eq(ProblemReportCore.ResolveEndpoint("file:///c:/x", out _), ProblemReportCore.DefaultEndpoint, "file: refused");
        Eq(ProblemReportCore.ResolveEndpoint("not a url", out _), ProblemReportCore.DefaultEndpoint, "garbage refused");
    }

    private static void Responses()
    {
        Case("server answers");
        Check(ProblemReportCore.ReadResponse(202, "{\"ok\":true,\"id\":\"R-20261011-ab12cd\"}", out string m, out string id), "202 ok");
        Eq(id, "R-20261011-ab12cd", "id read");
        Check(m.Contains("R-20261011-ab12cd") && m.StartsWith("Sent"), "message names the id: " + m);
        Check(ProblemReportCore.ReadResponse(200, "", out m, out id) && id == "", "200 without body ok");
        Check(!ProblemReportCore.ReadResponse(429, "{\"error\":\"Too many reports from this address; try again in 40 minutes.\"}", out m, out _), "429 fails");
        Check(m.Contains("40 minutes"), "429 shows the server's reason: " + m);
        Check(!ProblemReportCore.ReadResponse(429, "<html>nginx</html>", out m, out _) && m.Contains("Too many"), "nginx 429 page: generic text");
        Check(!ProblemReportCore.ReadResponse(400, "{\"error\":\"description\"}", out m, out _) && m.Contains("refused") && m.Contains("description"), "400 refusal: " + m);
        Check(!ProblemReportCore.ReadResponse(413, "", out m, out _) && m.Contains("Untick"), "413 suggests unticking the log");
        Check(!ProblemReportCore.ReadResponse(502, "<html>", out m, out _) && m.Contains("502"), "502 says so");
        Check(!ProblemReportCore.ReadResponse(404, "", out m, out _) && m.Contains("404"), "404 says so");
        Check(!ProblemReportCore.ReadResponse(400, "{not json", out m, out _), "bad json doesn't throw");
        string longErr = "{\"error\":\"" + new string('e', 500) + "\"}";
        ProblemReportCore.ReadResponse(400, longErr, out m, out _);
        Check(m.Length < 260, "server text capped");
    }

    private static void Sizes()
    {
        Case("sizes");
        Eq(ProblemReportCore.FormatSize(812), "812 bytes", "bytes");
        Eq(ProblemReportCore.FormatSize(214_000), "209 KB", "KB");
        Eq(ProblemReportCore.FormatSize(1_468_006), "1.4 MB", "MB");
        var t = new LogTail { FileName = "RynthCore.1.log", Text = new byte[2048], Lines = 1987, TakenLocal = new DateTime(2026, 10, 11, 14, 2, 11) };
        string d = ProblemReportCore.DescribeTail(t);
        Check(d.StartsWith("RynthCore.1.log: 1,987 lines, 2 KB (") && d.EndsWith("compressed), taken 14:02:11"), d);
    }

    // ── Optional: against a local copy of the server ─────────────────────

    private static async Task AgainstServer(string url)
    {
        Case("against " + url);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        string logPath = WriteLines("server.log", 2600, pad: 30);
        LogTail tail = ProblemReportCore.ReadTail(logPath, null)!;
        var ctx = SampleContext();
        ctx.Character = "Test Character " + Environment.ProcessId;   // its own per-character bucket
        async Task<(int, string)> Post(byte[] body)
        {
            using var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var resp = await http.PostAsync(url, content);
            return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
        }

        var (s1, b1) = await Post(ProblemReportCore.BuildPayload("Bot stuck at the portal @everyone **bold** `code`", "hunting", ctx, tail, DateTime.UtcNow));
        Check(ProblemReportCore.ReadResponse(s1, b1, out string m1, out string id1), $"with log taken: {s1} {b1}");
        Check(id1.StartsWith("R-"), "id returned: " + id1);
        Console.WriteLine("   " + m1);

        var (s2, b2) = await Post(ProblemReportCore.BuildPayload("No log this time", "", ctx, null, DateTime.UtcNow));
        Check(ProblemReportCore.ReadResponse(s2, b2, out _, out _), $"without log taken: {s2} {b2}");

        // A body the client would never build: refused.
        var (s3, _) = await Post(Encoding.UTF8.GetBytes("{\"schema\":1}"));
        Eq(s3, 400, "malformed refused");
    }
}
