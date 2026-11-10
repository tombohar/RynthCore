// Offline harness for the web panel host: see WebPanelHarness.csproj. Port of the 2026-11-07 spike's
// WebSpikeHost (spikes\webview2\WebSpikeHost), popup-free path only.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;

namespace WebPanelHarness;

internal static unsafe class Program
{
    private const string Secret = "SECRET123";
    private const string Allow = "panel.test\n*.panel.test";
    private const int StatusStarting = 1, StatusLoading = 2, StatusReady = 3, StatusClosed = 4, StatusFailed = 5, StatusSuperseded = 6;
    private const int CodeQueued = 1, CodeUrlNotAllowed = -2, CodeShuttingDown = -5, CodeStale = -6, CodeCreateFailed = -8, CodeProcessFailed = -9;
    private const uint FlagFallback = 1, FlagPageKeepsEscape = 2;
    private const int StateOpenedInBrowser = 4;

    private static readonly StringBuilder Out = new();
    private static readonly List<string> Results = new();
    private static int _fails, _unverified;

    private static readonly ConcurrentQueue<string> LogLines = new();
    private static readonly ConcurrentQueue<string> ChatLines = new();
    private static readonly ConcurrentQueue<string> BrowserCalls = new();

    private static readonly ConcurrentQueue<(Func<string> Fn, ManualResetEventSlim Done, string[] Result)> GameQ = new();
    private static IntPtr _game, _popOut;
    private static uint _gameTid;
    private static volatile int _gameKeyMsgs;
    private static bool _haveForeground, _allowForeground;

    private static volatile Session? _pumpSession;
    private static volatile int _pumpHandle;
    private static volatile bool _pumpStop;
    private static long _pumpCalls;

    private static string _root = "";

    private static void W(string s)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] {s}";
        Console.WriteLine(line);
        lock (Out) Out.AppendLine(line);
    }

    private static void Ok(string name) { Results.Add("ok " + name); W("ok " + name); }
    private static void Fail(string name, string detail) { _fails++; Results.Add($"FAIL {name}: {detail}"); W($"FAIL {name}: {detail}"); }
    private static void Unverified(string name, string detail) { _unverified++; Results.Add($"UNVERIFIED {name}: {detail}"); W($"UNVERIFIED {name}: {detail}"); }

    private static int Main(string[] args)
    {
        W($"harness pid={Environment.ProcessId} IntPtr.Size={IntPtr.Size} is64={Environment.Is64BitProcess} runtime={RuntimeInformation.FrameworkDescription} " +
          $"invariantGlobalization={AppContext.TryGetSwitch("System.Globalization.Invariant", out bool inv) && inv}");
        if (IntPtr.Size != 4) { Fail("Bitness", $"IntPtr.Size={IntPtr.Size}; the harness must run 32-bit like acclient"); return Finish(); }

        string payloadDir = Path.Combine(AppContext.BaseDirectory, "payload");
        foreach (string a in args)
        {
            if (a.StartsWith("--payload=", StringComparison.Ordinal)) payloadDir = a.Substring(10);
            // Opt in: the harness itself asks for the foreground (a plain SetForegroundWindow; Windows refuses it while
            // the user is active elsewhere). Without it the foreground-dependent checks report UNVERIFIED.
            if (a == "--foreground") _allowForeground = true;
        }
        if (Environment.GetEnvironmentVariable("WEBPANEL_HARNESS_FOREGROUND") == "1") _allowForeground = true;
        _root = Path.Combine(Path.GetTempPath(), "rynth-webpanel-harness", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        string lad = Path.Combine(_root, "LocalAppData"), panelDir = Path.Combine(_root, "panel"), otherDir = Path.Combine(_root, "other");
        WriteSite(panelDir, otherDir);
        W($"payload {payloadDir}; temp root {_root}");

        var up = new ManualResetEventSlim();
        var gt = new Thread(() => GameThread(up)) { IsBackground = true, Name = "FakeAcMain" };
        gt.SetApartmentState(ApartmentState.STA);
        gt.Start();
        up.Wait();
        new Thread(PumpLoop) { IsBackground = true, Name = "FakeNormalPluginPump" }.Start();

        // Foreground: only taken if this process may (it never forces it; no SendInput, no AttachThreadInput).
        _haveForeground = EnsureForeground(_game);
        W($"game window 0x{_game.ToInt64():X} tid={_gameTid}; harness may ask for the foreground (--foreground): {_allowForeground}; holds it: {_haveForeground} ({FgDesc()})");

        // ---------------- session 1: the behaviour checks ----------------
        var s = new Session(payloadDir);
        s.Call("Init", _game, payloadDir, lad, panelDir, otherDir,
            (Action<string>)(l => { LogLines.Enqueue(l); W("  engine log: " + l); }),
            (Action<string>)(c => { ChatLines.Enqueue(c); W("  chat: " + c); }),
            (Action<string>)(u => { BrowserCalls.Enqueue(u); W("  browser call recorded"); }));
        _pumpSession = s;

        int handle = 0;
        Guard("OpenIsNonBlocking", () => handle = CheckOpenIsNonBlocking(s));
        Guard("CyclesReuseEnvironment", () => handle = CheckCycles(s, handle));
        Guard("KeysStayInPage", () => handle = CheckKeys(s));
        Guard("EscapeClosesAndRefocusesGame", () => CheckEscape(s, handle));
        Guard("PageKeepsEscapeFlag", () => handle = CheckPageKeepsEscape(s));
        Guard("OffListRedirectGoesToBrowser", () => CheckRedirect(s, handle));
        Guard("WindowOpenNeverMakesAWindow", () => CheckWindowOpen(s, handle));
        Guard("CustomSchemeNeverLaunched", () => CheckCustomScheme(s, handle));
        Guard("StaleHandles", () => handle = CheckStaleHandles(s, handle));
        Guard("ProfileSwitchRecreates", () => handle = CheckProfileSwitch(s));
        Guard("NoBrowserFallbackAfterNavigation", () => CheckNoFallbackAfterNavigation(s, handle));
        Guard("GpuProcessCrashKeepsPanel", () => handle = CheckGpuCrash(s));
        Guard("LateFailureForSupersededHandle", () => handle = CheckLateFailure(s));
        Guard("UnresponsiveCountResetsPerOpen", () => handle = CheckUnresponsiveReset(s));
        Guard("CreateFailureFallsBack", () => CheckCreateFailure(s));
        Guard("CreateFailureBrowserFails", () => CheckCreateFailureBrowserFails(s));
        Guard("NoFocusFromAnotherProcess", () => { int r = CheckNoFocusFromAnotherProcess(s); if (r > 0) handle = r; });
        Guard("VisibilitySameProcess", () => handle = CheckVisibilitySameProcess(s));
        Unverified("VisibilityOtherProcess", "needs another process to take the foreground (alt-tab); the harness never forces it. Owner in-game check.");
        Guard("LayoutRemembered", () => handle = CheckLayout(s, lad));
        Guard("UnloadAfterCloseCollected", () => CheckUnloadAfterClose(ref s, handle));

        // ---------------- session 2: unload with the panel up ----------------
        Guard("UnloadWhileVisibleCollected", () => CheckUnloadWhileVisible(payloadDir, lad, panelDir, otherDir));

        Guard("NoSecretInLog", () =>
        {
            int hits = LogLines.Count(l => l.Contains(Secret)) + ChatLines.Count(l => l.Contains(Secret));
            if (hits != 0) Fail("NoSecretInLog", $"{hits} log/chat line(s) contain {Secret}");
            else if (LogLines.IsEmpty) Fail("NoSecretInLog", "no engine log captured at all (the check would prove nothing)");
            else Ok("NoSecretInLog");
        });

        _pumpStop = true;
        W($"pump GetState calls from the fake pump thread: {Interlocked.Read(ref _pumpCalls)}");
        W($"fake game key messages total: {_gameKeyMsgs}");
        return Finish();
    }

    private static int Finish()
    {
        W("===== summary =====");
        foreach (string r in Results) W(r);
        int exit = _fails > 0 ? 1 : _unverified > 0 ? 2 : 0;
        W($"{Results.Count} checks: {Results.Count - _fails - _unverified} ok, {_fails} FAIL, {_unverified} UNVERIFIED -> exit {exit}");
        try { File.WriteAllText(Path.Combine(_root, "harness-output.txt"), Out.ToString()); } catch { }
        return exit;
    }

    private static void Guard(string name, Action check)
    {
        W($"----- {name} -----");
        try { check(); }
        catch (Exception ex) { Fail(name, "threw " + (ex is TargetInvocationException t ? t.InnerException : ex)); }
    }

    // ============================== checks ==============================

    private static int CheckOpenIsNonBlocking(Session s)
    {
        s.Call("SetEnvironmentDelay", 2000);
        // Warm the argument reader and the host's validation once (an off-list URL never starts the thread).
        var (warm, warmMs) = Open(s, "https://elsewhere.invalid/", Allow);
        W($"warm-up open (off-list url) -> {warm} in {warmMs:F2} ms");
        bool threadBefore = (bool)s.Call("get_ThreadRunning")!;
        string url = "https://panel.test/launch.html?code=" + Secret;
        var (h, ms) = Open(s, url, Allow, title: "Harness launch");
        int[] st = State(s, h);
        W($"first open -> handle {h} in {ms:F2} ms (thread running before: {threadBefore}); status right after: {st[2]}");
        bool ready = WaitStatus(s, h, StatusReady, 60000, out int[] fin);
        string src = (string)s.Call("get_Source")!;
        s.Call("SetEnvironmentDelay", 0);
        if (h <= 0) { Fail("OpenIsNonBlocking", "open returned " + h); return h; }
        if (threadBefore) { Fail("OpenIsNonBlocking", "the panel thread ran before the first open"); return h; }
        if (ms >= 20) { Fail("OpenIsNonBlocking", $"open took {ms:F2} ms (>= 20)"); return h; }
        if (st[2] != StatusStarting) { Fail("OpenIsNonBlocking", $"status right after open {st[2]}, want Starting"); return h; }
        if (!ready) { Fail("OpenIsNonBlocking", $"never Ready: state [{string.Join(",", fin)}]"); return h; }
        Ok("OpenIsNonBlocking");
        // The caller's buffers were overwritten with junk and freed right after Open returned (every Open does).
        if (src != url) Fail("OpenCopiesCallerStrings", "page source is not the url passed in (redacted): " + Redact(src));
        else Ok("OpenCopiesCallerStrings");
        return h;
    }

    private static int CheckCycles(Session s, int handle)
    {
        int envsBefore = (int)s.Call("get_EnvironmentCount")!;
        int closeRc = (int)s.Call("Close", handle)!;
        if (closeRc != CodeQueued || !WaitStatus(s, handle, StatusClosed, 5000, out _)) { Fail("CyclesReuseEnvironment", $"first close rc={closeRc} never Closed"); return handle; }
        int modsAfter1 = -1, modsNow = -1;
        var seq = new List<string>();
        int h = handle;
        for (int c = 1; c <= 5; c++)
        {
            (h, _) = Open(s, "https://panel.test/index.html", Allow, title: "Cycle " + c);
            bool r = WaitStatus(s, h, StatusReady, 30000, out int[] a);
            int rc = (int)s.Call("Close", h)!;
            bool cl = WaitStatus(s, h, StatusClosed, 5000, out int[] b);
            seq.Add($"{c}:{(r ? "Ready" : "st" + a[2])}/{(cl ? "Closed" : "st" + b[2])}(rc {rc})");
            modsNow = WebViewModuleCount();
            if (c == 1) modsAfter1 = modsNow;
        }
        int envs = (int)s.Call("get_EnvironmentCount")!;
        W($"cycles {string.Join(" ", seq)}; environments {envsBefore} -> {envs}; webview modules after cycle 1 = {modsAfter1}, after cycle 5 = {modsNow}");
        if (seq.Any(x => !x.Contains("Ready/Closed"))) Fail("CyclesReuseEnvironment", "a cycle missed Ready or Closed: " + string.Join(" ", seq));
        else if (envs != 1 || envsBefore != 1) Fail("CyclesReuseEnvironment", $"environments created {envs} (want exactly 1)");
        else if (modsNow != modsAfter1) Fail("CyclesReuseEnvironment", $"webview module count changed {modsAfter1} -> {modsNow}");
        else Ok("CyclesReuseEnvironment");
        return h;
    }

    /// <summary>
    /// The host moves keyboard focus into the page only while this process holds the foreground (never from
    /// another program), so the posted-key checks are meaningful only then.
    /// </summary>
    private static bool ThisProcessHoldsForeground()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        GetWindowThreadProcessId(fg, out uint pid);
        return pid == Environment.ProcessId;
    }

    private static int CheckKeys(Session s)
    {
        bool inProcess = ThisProcessHoldsForeground();
        var (h, _) = Open(s, "https://panel.test/index.html", Allow, title: "Keys");
        if (!WaitStatus(s, h, StatusReady, 30000, out int[] a)) { Fail("KeysStayInPage", $"not Ready [{string.Join(",", a)}]"); return h; }
        Thread.Sleep(500);
        W("focus -> " + s.Call("Eval", "document.getElementById('t').focus(); JSON.stringify({active: document.activeElement && document.activeElement.id, hasFocus: document.hasFocus()})", 5000));
        IntPtr widget = RenderWidget(s, out string tree);
        W("panel window tree: " + tree);
        if (widget == IntPtr.Zero) { Fail("KeysStayInPage", "no Chrome_RenderWidgetHostHWND under the panel window"); return h; }
        int g0 = _gameKeyMsgs, k0 = (int)s.Call("get_AcceleratorKeyEvents")!;
        foreach (char ch in "x1q") PostKey(widget, char.ToUpperInvariant(ch), ch);
        Thread.Sleep(1200);
        string value = (string)s.Call("Eval", "document.getElementById('t').value", 5000)!;
        int g = _gameKeyMsgs - g0;
        W($"page input value {value}; fake game key messages during the test: {g}; AcceleratorKeyPressed events {(int)s.Call("get_AcceleratorKeyEvents")! - k0}; panel visible={IsWindowVisible((IntPtr)s.Call("get_HostHwnd")!)}; {FgDesc()}");
        Snap((IntPtr)s.Call("get_HostHwnd")!, Path.Combine(_root, "panel-keys.png"));
        // The game-side count is informational only: the keys are posted straight to the Chromium window, so the
        // game could not see them here whatever the host did. Input isolation (unowned popup, separate queue) is an
        // owner in-game check: type in the panel, nothing reaches AC's chat.
        W($"(informational, cannot fail: game key messages {g})");
        if (value.Contains("x1q")) Ok("KeysStayInPage");
        else if (!inProcess) Unverified("KeysStayInPage", $"page input is {value}: another process held the foreground at Open ({FgDesc()}), so by design the panel did not focus the page; owner in-game check (type in the panel)");
        else Fail("KeysStayInPage", $"page input is {value}, want x1q");
        return h;
    }

    private static void CheckEscape(Session s, int handle)
    {
        IntPtr widget = RenderWidget(s, out _);
        if (widget == IntPtr.Zero) { Fail("EscapeClosesAndRefocusesGame", "no render widget to post to"); return; }
        IntPtr host = (IntPtr)s.Call("get_HostHwnd")!;
        bool panelFg = EnsureForeground(host);
        s.Call("Eval", "document.getElementById('t').focus(); 1", 5000);
        W($"before Escape: panel holds the foreground {panelFg} ({FgDesc()}), panel visible {IsWindowVisible(host)}");
        int acc0 = (int)s.Call("get_AcceleratorKeyEvents")!;
        int g0 = _gameKeyMsgs;
        PostKey(widget, 0x1B, '\x1b');
        bool closed = WaitStatus(s, handle, StatusClosed, 3000, out int[] st);
        int acc = (int)s.Call("get_AcceleratorKeyEvents")! - acc0;
        int g = _gameKeyMsgs - g0;
        Thread.Sleep(200);
        IntPtr fg = GetForegroundWindow();
        W($"after posted Escape: status {st[2]}, AcceleratorKeyPressed events {acc}, game key messages {g}, foreground {FgDesc()} (game 0x{_game.ToInt64():X})");
        if (acc == 0)
        {
            Unverified("EscapeClosesAndRefocusesGame", $"AcceleratorKeyPressed did not fire for a POSTED Escape (status {st[2]}, panel held the foreground: {panelFg}; the page itself gets posted keys, see KeysStayInPage); Escape-to-close needs the owner's in-game check");
            if (!closed) { s.Call("Close", handle); WaitStatus(s, handle, StatusClosed, 3000, out _); }
            return;
        }
        if (!closed) { Fail("EscapeClosesAndRefocusesGame", $"AcceleratorKeyPressed fired but the panel is {st[2]}, not Closed"); return; }
        if (!panelFg) { Unverified("EscapeClosesAndRefocusesGame", $"closed (game key messages {g}, informational only); the foreground hand-back could not be checked (the panel did not hold the foreground)"); return; }
        if (fg != _game) { Fail("EscapeClosesAndRefocusesGame", "closed, but the foreground is not the game"); return; }
        Ok("EscapeClosesAndRefocusesGame");
    }

    private static int CheckPageKeepsEscape(Session s)
    {
        bool inProcess = ThisProcessHoldsForeground();
        var (h, _) = Open(s, "https://panel.test/index.html", Allow, title: "Keeps Escape", flags: FlagFallback | FlagPageKeepsEscape);
        if (!WaitStatus(s, h, StatusReady, 30000, out int[] a)) { Fail("PageKeepsEscapeFlag", $"not Ready [{string.Join(",", a)}]"); return h; }
        Thread.Sleep(400);
        s.Call("Eval", "window.keys = []; document.getElementById('t').focus(); 1", 5000);
        IntPtr widget = RenderWidget(s, out _);
        int acc0 = (int)s.Call("get_AcceleratorKeyEvents")!;
        PostKey(widget, 0x1B, '\x1b');
        Thread.Sleep(1200);
        int acc = (int)s.Call("get_AcceleratorKeyEvents")! - acc0;
        string keys = (string)s.Call("Eval", "JSON.stringify(window.keys)", 5000)!;
        int[] st = State(s, h);
        W($"flag 2: page keys {keys}, status {st[2]}, AcceleratorKeyPressed events {acc}");
        if (st[2] != StatusReady) Fail("PageKeepsEscapeFlag", $"panel status {st[2]}, want Ready (open)");
        else if (!keys.Contains("Escape") && !inProcess) Unverified("PageKeepsEscapeFlag", $"the page did not see Escape ({keys}): another process held the foreground at Open ({FgDesc()}), so by design the panel did not focus the page; owner in-game check");
        else if (!keys.Contains("Escape")) Fail("PageKeepsEscapeFlag", "the page keydown listener did not see Escape: " + keys);
        else if (acc == 0) Unverified("PageKeepsEscapeFlag", "page saw Escape and stayed open, but AcceleratorKeyPressed never fired for posted input, so the flag path itself was not exercised");
        else Ok("PageKeepsEscapeFlag");
        return h;
    }

    private static void CheckRedirect(Session s, int h)
    {
        Drain(BrowserCalls);
        string before = (string)s.Call("get_Source")!;
        int rc = (int)s.Call("Navigate", h, "https://go.panel.test/redirect?code=" + Secret)!;
        Thread.Sleep(2500);
        string after = (string)s.Call("get_Source")!;
        string[] calls = BrowserCalls.ToArray();
        string want = "https://other.test/landing.html";
        W($"navigate rc {rc}; source {Redact(before)} -> {Redact(after)}; browser calls {calls.Length}: {string.Join(" | ", calls.Select(Redact))}");
        if (rc != CodeQueued) Fail("OffListRedirectGoesToBrowser", "navigate rc " + rc);
        else if (calls.Length != 1) Fail("OffListRedirectGoesToBrowser", $"{calls.Length} browser call(s), want 1 (served-302 log lines: {LogLines.Count(l => l.Contains("served the 302"))})");
        else if (Redact(calls[0]) != want) Fail("OffListRedirectGoesToBrowser", "browser got " + Redact(calls[0]));
        else if (!after.StartsWith("https://panel.test/", StringComparison.Ordinal)) Fail("OffListRedirectGoesToBrowser", "panel left panel.test: " + Redact(after));
        else Ok("OffListRedirectGoesToBrowser");
    }

    private static void CheckWindowOpen(Session s, int h)
    {
        Drain(BrowserCalls);
        uint bpid = (uint)(int)s.Call("get_BrowserProcessId")!;
        HashSet<IntPtr> before = TopLevelWindows();
        string r = (string)s.Call("Eval", "String(window.open('https://other.test/x.html?code=" + Secret + "'))", 5000)!;
        Thread.Sleep(1500);
        var fresh = TopLevelWindows().Where(w => !before.Contains(w) && IsWindowVisible(w)).Where(w => OwnedByUsOrWebView(w)).ToList();
        int calls = BrowserCalls.Count;
        W($"window.open(other.test) -> {r}; new visible top-level windows of this process or msedgewebview2: {fresh.Count}; browser calls {calls}");
        string r2 = (string)s.Call("Eval", "String(window.open('https://panel.test/b.html'))", 5000)!;
        bool onB = WaitFor(() => ((string)s.Call("get_Source")!).EndsWith("/b.html", StringComparison.Ordinal), 5000);
        W($"window.open(panel.test/b.html) -> {r2}; panel on b.html: {onB}");
        if (fresh.Count != 0) Fail("WindowOpenNeverMakesAWindow", $"{fresh.Count} new top-level window(s)");
        else if (calls != 1) Fail("WindowOpenNeverMakesAWindow", $"{calls} browser call(s) for the off-list window.open, want 1");
        else if (!onB) Fail("WindowOpenNeverMakesAWindow", "window.open to an allowed url did not navigate the panel");
        else Ok("WindowOpenNeverMakesAWindow");
        WaitStatus(s, h, StatusReady, 5000, out _);
    }

    private static void CheckCustomScheme(Session s, int h)
    {
        Drain(BrowserCalls);
        string src0 = (string)s.Call("get_Source")!;
        int[] st0 = State(s, h);
        int logs0 = LogLines.Count;
        s.Call("Eval", "location = 'ms-settings:'; 1", 5000);
        Thread.Sleep(1500);
        s.Call("Eval", "location = 'file:///C:/'; 1", 5000);
        Thread.Sleep(1500);
        string src = (string)s.Call("get_Source")!;
        int[] st = State(s, h);
        var newLogs = LogLines.Skip(logs0).ToList();
        W($"after ms-settings: and file:///C:/ -> source {Redact(src0)} -> {Redact(src)}; navigations {st0[6]} -> {st[6]}; browser calls {BrowserCalls.Count}; new log lines: {string.Join(" | ", newLogs)}");
        if (BrowserCalls.Count != 0) Fail("CustomSchemeNeverLaunched", "a browser call was made");
        else if (src != src0) Fail("CustomSchemeNeverLaunched", "the panel navigated away");
        else if (st[6] != st0[6]) Fail("CustomSchemeNeverLaunched", "the navigation count moved");
        else if (!newLogs.Any(l => l.Contains("(ms-settings)"))) Fail("CustomSchemeNeverLaunched", "no blocked-navigation log line for ms-settings:");
        else Ok("CustomSchemeNeverLaunched");
    }

    private static int CheckStaleHandles(Session s, int h)
    {
        var (h2, _) = Open(s, "https://panel.test/index.html", Allow, title: "Supersede");
        int[] old = State(s, h);
        int nav = (int)s.Call("Navigate", h, "https://panel.test/b.html")!;
        int close = (int)s.Call("Close", h)!;
        bool ready = WaitStatus(s, h2, StatusReady, 30000, out _);
        int offList = (int)s.Call("Navigate", h2, "https://other.test/x.html?code=" + Secret)!;
        W($"old handle {h}: GetState {old[0]} status {old[2]}, Navigate {nav}, Close {close}; new handle {h2} = old + 1: {h2 == h + 1}; new Ready {ready}; off-list Navigate {offList}");
        Thread.Sleep(300);
        if (old[0] != StatusSuperseded || old[2] != StatusSuperseded) Fail("StaleHandles", "old handle does not read Superseded");
        else if (nav != CodeStale || close != CodeStale) Fail("StaleHandles", $"old handle Navigate {nav} / Close {close}, want {CodeStale}");
        else if (h2 != h + 1) Fail("StaleHandles", "handles do not count up by one");
        else if (!ready) Fail("StaleHandles", "new handle never Ready");
        else if (offList != CodeUrlNotAllowed) Fail("StaleHandles", "off-list Navigate returned " + offList);
        else Ok("StaleHandles");
        return h2;
    }

    private static int CheckProfileSwitch(Session s)
    {
        int e0 = (int)s.Call("get_EnvironmentCount")!;
        var (h1, _) = Open(s, "https://panel.test/index.html", Allow, key: "Alpha");
        bool r1 = WaitStatus(s, h1, StatusReady, 30000, out _);
        int e1 = (int)s.Call("get_EnvironmentCount")!;
        var (h2, _) = Open(s, "https://panel.test/b.html", Allow, key: "alpha ");
        bool r2 = WaitStatus(s, h2, StatusReady, 30000, out _);
        int e2 = (int)s.Call("get_EnvironmentCount")!;
        string fDefault = (string)s.Call("ProfileFolder", "Harness World", "")!, fAlpha = (string)s.Call("ProfileFolder", "Harness World", "Alpha")!;
        W($"environments {e0} -> {e1} (key Alpha) -> {e2} (key 'alpha ', same profile); folders default {Directory.Exists(fDefault)}, alpha {Directory.Exists(fAlpha)} ({fAlpha})");
        if (!r1 || !r2) Fail("ProfileSwitchRecreates", $"not Ready ({r1}, {r2})");
        else if (e1 != e0 + 1) Fail("ProfileSwitchRecreates", $"a new key did not create a new environment ({e0} -> {e1})");
        else if (e2 != e1) Fail("ProfileSwitchRecreates", $"the same key made another environment ({e1} -> {e2})");
        else if (fDefault == fAlpha || !Directory.Exists(fAlpha)) Fail("ProfileSwitchRecreates", "the alpha profile folder is missing or not separate");
        else Ok("ProfileSwitchRecreates");
        return h2;
    }

    private static void CheckNoFallbackAfterNavigation(Session s, int h)
    {
        Drain(BrowserCalls);
        int pid = (int)s.Call("get_BrowserProcessId")!;
        if (pid == 0) { Fail("NoBrowserFallbackAfterNavigation", "no browser process id"); return; }
        string name;
        try { using var p = Process.GetProcessById(pid); name = p.ProcessName; if (!name.StartsWith("msedgewebview2", StringComparison.OrdinalIgnoreCase)) { Fail("NoBrowserFallbackAfterNavigation", "browser pid is " + name); return; } p.Kill(); }
        catch (Exception ex) { Fail("NoBrowserFallbackAfterNavigation", "could not kill the panel's browser process: " + ex.Message); return; }
        bool failed = WaitStatus(s, h, StatusFailed, 10000, out int[] st);
        Thread.Sleep(500);
        W($"killed browser pid {pid} ({name}); state [{string.Join(",", st)}]; browser calls {BrowserCalls.Count}");
        if (!failed) Fail("NoBrowserFallbackAfterNavigation", "status " + st[2] + ", want Failed");
        else if (st[4] != CodeProcessFailed) Fail("NoBrowserFallbackAfterNavigation", "error " + st[4] + ", want ProcessFailed");
        else if (BrowserCalls.Count != 0 || (st[3] & StateOpenedInBrowser) != 0) Fail("NoBrowserFallbackAfterNavigation", "fell back to the browser after a navigation");
        else Ok("NoBrowserFallbackAfterNavigation");
        // The next open recreates.
        var (h2, _) = Open(s, "https://panel.test/index.html", Allow);
        if (WaitStatus(s, h2, StatusReady, 30000, out int[] st2)) Ok("OpenAfterProcessFailedRecreates");
        else Fail("OpenAfterProcessFailedRecreates", $"[{string.Join(",", st2)}]");
    }

    private static int CheckGpuCrash(Session s)
    {
        var (h, _) = Open(s, "https://panel.test/index.html", Allow, title: "GPU crash");
        if (!WaitStatus(s, h, StatusReady, 30000, out int[] a)) { Fail("GpuProcessCrashKeepsPanel", $"not Ready [{string.Join(",", a)}]"); return h; }
        uint browser = (uint)(int)s.Call("get_BrowserProcessId")!;
        int children = 0, readable = 0, gpu = 0;
        foreach (Process p in Process.GetProcessesByName("msedgewebview2"))
        {
            using (p)
            {
                if (ParentPid(p.Id) != browser) continue;
                children++;
                string? cmd = CommandLine(p.Id);
                if (cmd == null) continue;
                readable++;
                if (gpu == 0 && cmd.Contains("--type=gpu-process", StringComparison.Ordinal)) gpu = p.Id;
            }
        }
        W($"browser pid {browser}: {children} child process(es), {readable} command line(s) readable, gpu-process pid {gpu}");
        if (gpu == 0) { Unverified("GpuProcessCrashKeepsPanel", $"no msedgewebview2 child of browser pid {browser} with --type=gpu-process ({children} children, {readable} command lines read)"); return h; }
        int logs0 = LogLines.Count;
        try { using var p = Process.GetProcessById(gpu); p.Kill(); }
        catch (Exception ex) { Unverified("GpuProcessCrashKeepsPanel", "could not kill the gpu process: " + ex.Message); return h; }
        Thread.Sleep(3000);
        int[] st = State(s, h);
        string alive = (string)s.Call("Eval", "document.title", 5000)!;
        bool open = (bool)s.Call("get_PanelOpen")!;
        var newLogs = LogLines.Skip(logs0).ToList();
        W($"killed gpu pid {gpu}: state [{string.Join(",", st)}], panel open {open}, page title {alive}; log: {string.Join(" | ", newLogs)}");
        if (st[2] != StatusReady || st[4] != 0) Fail("GpuProcessCrashKeepsPanel", $"status {st[2]} error {st[4]}, want Ready with no error");
        else if (!open) Fail("GpuProcessCrashKeepsPanel", "the panel closed");
        else if (!newLogs.Any(l => l.Contains("GpuProcessExited"))) Unverified("GpuProcessCrashKeepsPanel", "panel stayed Ready, but no GpuProcessExited ProcessFailed event arrived within 3 s, so the handler path was not shown");
        else Ok("GpuProcessCrashKeepsPanel");
        return h;
    }

    private static int CheckLateFailure(Session s)
    {
        var (a, _) = Open(s, "https://panel.test/index.html", Allow, title: "Old");
        bool ra = WaitStatus(s, a, StatusReady, 30000, out _);
        var (b, _) = Open(s, "https://panel.test/b.html", Allow, title: "New");
        bool rb = WaitStatus(s, b, StatusReady, 30000, out _);
        bool open0 = (bool)s.Call("get_PanelOpen")!;
        bool posted = (bool)s.Call("FailHandle", a)!;
        Thread.Sleep(600);
        int[] st = State(s, b);
        bool open = (bool)s.Call("get_PanelOpen")!;
        W($"late synthetic failure for superseded handle {a} (posted {posted}): newer handle {b} state [{string.Join(",", st)}], panel open before {open0} after {open}");
        if (!ra || !rb || !open0) Fail("LateFailureForSupersededHandle", "setup: panels not Ready/open");
        else if (st[2] != StatusReady) Fail("LateFailureForSupersededHandle", $"newer panel status {st[2]}, want Ready");
        else if (!open) Fail("LateFailureForSupersededHandle", "the late failure hid the newer panel");
        else Ok("LateFailureForSupersededHandle");
        return b;
    }

    // Two unresponsive reports, a new Open, one more report: the count starts again per panel, so the new one
    // stays Ready (the limit is 3 within 30 s). Reports are injected through the host's own handler.
    private static int CheckUnresponsiveReset(Session s)
    {
        var (a, _) = Open(s, "https://panel.test/index.html", Allow, title: "Hung A");
        bool ra = WaitStatus(s, a, StatusReady, 30000, out _);
        s.Call("ReportUnresponsive"); s.Call("ReportUnresponsive");
        Thread.Sleep(300);
        int[] sa = State(s, a);
        var (b, _) = Open(s, "https://panel.test/b.html", Allow, title: "Hung B");
        bool rb = WaitStatus(s, b, StatusReady, 30000, out _);
        s.Call("ReportUnresponsive");
        Thread.Sleep(500);
        int[] sb = State(s, b);
        W($"two reports on {a}: state [{string.Join(",", sa)}]; then Open {b} Ready {rb}, one more report: state [{string.Join(",", sb)}]");
        if (!ra || !rb) Fail("UnresponsiveCountResetsPerOpen", "setup: not Ready");
        else if (sa[2] != StatusReady) Fail("UnresponsiveCountResetsPerOpen", "two reports already failed the panel (limit is 3)");
        else if (sb[2] != StatusReady) Fail("UnresponsiveCountResetsPerOpen", $"the third report overall failed the new panel: status {sb[2]} error {sb[4]}");
        else Ok("UnresponsiveCountResetsPerOpen");
        return b;
    }

    private static uint ParentPid(int pid)
    {
        IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return 0;
        try
        {
            PROCESS_BASIC_INFORMATION pbi;
            return NtQueryInformationProcess(h, 0, &pbi, sizeof(PROCESS_BASIC_INFORMATION), out _) == 0 ? (uint)pbi.InheritedFromUniqueProcessId.ToInt64() : 0;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>ProcessCommandLineInformation (60): a UNICODE_STRING followed by its text, in our buffer.</summary>
    private static string? CommandLine(int pid)
    {
        IntPtr h = OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            byte[] buf = new byte[65536];
            fixed (byte* b = buf)
            {
                if (NtQueryInformationProcess(h, 60, b, buf.Length, out _) != 0) return null;
                int len = *(ushort*)b;
                char* text = *(char**)(b + IntPtr.Size);
                return text == null ? null : new string(text, 0, len / 2);
            }
        }
        finally { CloseHandle(h); }
    }

    private static void CheckCreateFailure(Session s)
    {
        Drain(BrowserCalls);
        Drain(ChatLines);
        s.Call("SetBrowserFolder", Path.Combine(_root, "no-such-browser"));
        s.Call("SetBrowserDelay", 300); // a slow browser launch widens any Failed-before-flag window
        int samples = 0, failedNoFlag = 0;
        bool failed = false;
        int[] st;
        try
        {
            var (h, _) = Open(s, "https://panel.test/launch.html?code=" + Secret, Allow, key: "beta");
            // Poll continuously: a plugin that sees Failed without OpenedInBrowser would open the page a second time.
            var sw = Stopwatch.StartNew();
            st = State(s, h);
            while (sw.ElapsedMilliseconds < 20000)
            {
                st = State(s, h);
                samples++;
                if (st[2] == StatusFailed && (st[3] & StateOpenedInBrowser) == 0) failedNoFlag++;
                if (st[2] == StatusFailed && (st[3] & StateOpenedInBrowser) != 0) { failed = true; break; }
                if (st[2] == StatusFailed && failedNoFlag > 400) break; // the flag is not coming
                Thread.Sleep(1);
            }
            if (!failed) failed = st[2] == StatusFailed;
        }
        finally { s.Call("SetBrowserDelay", 0); s.Call("SetBrowserFolder", (string?)null); }
        string[] calls = BrowserCalls.ToArray();
        W($"forced create failure: state [{string.Join(",", st)}]; {samples} samples, {failedNoFlag} showed Failed without OpenedInBrowser; browser calls {calls.Length}; chat: {string.Join(" | ", ChatLines)}");
        if (!failed) Fail("CreateFailureFallsBack", "status " + st[2]);
        else if (failedNoFlag > 0) Fail("CreateFailureFallsBack", $"{failedNoFlag} of {samples} samples showed Failed without OpenedInBrowser (a polling plugin would open the page twice)");
        else if (st[4] != CodeCreateFailed) Fail("CreateFailureFallsBack", "error " + st[4] + ", want CreateFailed");
        else if ((st[3] & StateOpenedInBrowser) == 0) Fail("CreateFailureFallsBack", "OpenedInBrowser flag not set");
        else if (calls.Length != 1 || Redact(calls[0]) != "https://panel.test/launch.html") Fail("CreateFailureFallsBack", $"{calls.Length} browser call(s)");
        else if (!ChatLines.Any(c => c.Contains("could not start (it failed to start)"))) Fail("CreateFailureFallsBack", "no fallback chat line");
        else Ok("CreateFailureFallsBack");
    }

    private static void CheckCreateFailureBrowserFails(Session s)
    {
        Drain(BrowserCalls);
        Drain(ChatLines);
        s.Call("SetBrowserFolder", Path.Combine(_root, "no-such-browser"));
        s.Call("SetBrowserFails", true);
        int[] st;
        bool failed;
        try
        {
            var (h, _) = Open(s, "https://panel.test/launch.html?code=" + Secret, Allow, key: "beta");
            failed = WaitStatus(s, h, StatusFailed, 20000, out st);
            WaitFor(() => ChatLines.Any(c => c.Contains("could not start")), 3000);
        }
        finally { s.Call("SetBrowserFails", false); s.Call("SetBrowserFolder", (string?)null); }
        string[] chat = ChatLines.ToArray();
        W($"create failure, browser fails: state [{string.Join(",", st)}]; browser calls {BrowserCalls.Count}; chat: {string.Join(" | ", chat)}");
        if (!failed) Fail("CreateFailureBrowserFails", "status " + st[2]);
        else if (st[4] != CodeCreateFailed) Fail("CreateFailureBrowserFails", "error " + st[4] + ", want CreateFailed");
        else if ((st[3] & StateOpenedInBrowser) != 0) Fail("CreateFailureBrowserFails", "OpenedInBrowser flag set although the browser did not open");
        else if (chat.Any(c => c.Contains("opened the page"))) Fail("CreateFailureBrowserFails", "chat claims the page opened");
        else if (!chat.Any(c => c.Contains("could not start (it failed to start), and your web browser did not open"))) Fail("CreateFailureBrowserFails", "no browser-did-not-open chat line");
        else Ok("CreateFailureBrowserFails");
    }

    /// <summary>
    /// Only when the foreground already belongs to another process (the harness never arranges it): opening a
    /// panel must leave that process in front. When this harness holds the foreground the check is UNVERIFIED.
    /// </summary>
    private static int CheckNoFocusFromAnotherProcess(Session s)
    {
        IntPtr fg0 = GetForegroundWindow();
        uint pid0 = 0;
        if (fg0 != IntPtr.Zero) GetWindowThreadProcessId(fg0, out pid0);
        int bpid = (int)s.Call("get_BrowserProcessId")!;
        if (fg0 == IntPtr.Zero || pid0 == Environment.ProcessId || (bpid != 0 && pid0 == bpid))
        {
            Unverified("NoFocusFromAnotherProcess", $"the foreground is not another process ({FgDesc()}); the harness never forces it there. Owner in-game check (alt-tab away, open the panel from a plugin)");
            return 0;
        }
        var (h, _) = Open(s, "https://panel.test/index.html", Allow, title: "No focus");
        bool ready = WaitStatus(s, h, StatusReady, 30000, out int[] st);
        Thread.Sleep(500);
        IntPtr fg1 = GetForegroundWindow();
        uint pid1 = 0;
        if (fg1 != IntPtr.Zero) GetWindowThreadProcessId(fg1, out pid1);
        W($"foreground before pid {pid0}, after open pid {pid1} ({FgDesc()}); state [{string.Join(",", st)}]");
        if (!ready) Fail("NoFocusFromAnotherProcess", "not Ready");
        else if (pid1 == Environment.ProcessId || (bpid != 0 && pid1 == bpid)) Fail("NoFocusFromAnotherProcess", "the panel took the foreground from another process");
        else Ok("NoFocusFromAnotherProcess");
        return h;
    }

    private static int CheckVisibilitySameProcess(Session s)
    {
        var (h, _) = Open(s, "https://panel.test/index.html", Allow, title: "Visibility");
        if (!WaitStatus(s, h, StatusReady, 30000, out int[] a)) { Fail("VisibilitySameProcess", $"not Ready [{string.Join(",", a)}]"); return h; }
        IntPtr host = (IntPtr)s.Call("get_HostHwnd")!;
        if (!EnsureForeground(host))
        {
            Unverified("VisibilitySameProcess", $"the harness does not hold the foreground (--foreground {_allowForeground}; {FgDesc()}), so no in-process window can take it; owner in-game check (pop out a RynthCore window, panel stays)");
            return h;
        }
        Thread.Sleep(300);
        bool visBefore = IsWindowVisible(host);
        Game(() => { _popOut = CreateWindowExW(0, "FakeAcClient", "Fake pop-out", 0x00CF0000, 300, 300, 400, 300, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(IntPtr.Zero), IntPtr.Zero); ShowWindow(_popOut, 5); SetForegroundWindow(_popOut); return ""; });
        Thread.Sleep(600);
        bool fgIsPop = GetForegroundWindow() == _popOut;
        bool vis = IsWindowVisible(host);
        Game(() => { DestroyWindow(_popOut); SetForegroundWindow(_game); return ""; });
        Thread.Sleep(300);
        W($"panel visible before {visBefore}; pop-out foreground {fgIsPop}; panel visible with the pop-out in front {vis}; now {FgDesc()}");
        if (!visBefore) Fail("VisibilitySameProcess", "panel not visible before the pop-out");
        else if (!fgIsPop) Unverified("VisibilitySameProcess", "the pop-out could not take the foreground");
        else if (!vis) Fail("VisibilitySameProcess", "panel hid when another window of this process took the foreground");
        else Ok("VisibilitySameProcess");
        return h;
    }

    private static int CheckLayout(Session s, string lad)
    {
        string path = Path.Combine(lad, "RynthCore", "WebPanel", "layout.json");
        IntPtr host = (IntPtr)s.Call("get_HostHwnd")!;
        // A drag is a modal loop inside DefWindowProc; WM_EXITSIZEMOVE is what the host saves on. Post the
        // pair around a move made with SetWindowPos (no input involved).
        GetClientRect(_game, out RECT gc);
        var p = new POINT();
        ClientToScreen(_game, ref p);
        PostMessageW(host, 0x0231 /* WM_ENTERSIZEMOVE */, IntPtr.Zero, IntPtr.Zero);
        Thread.Sleep(150);
        SetWindowPos(host, IntPtr.Zero, p.X + 20, p.Y + 30, gc.Right / 2, gc.Bottom / 2, 0x0004 | 0x0010);
        PostMessageW(host, 0x0232 /* WM_EXITSIZEMOVE */, IntPtr.Zero, IntPtr.Zero);
        bool saved = WaitFor(() => File.Exists(path), 3000);
        string json = saved ? File.ReadAllText(path) : "";
        var (h, _) = Open(s, "https://panel.test/index.html", Allow, w: 40, hgt: 40);
        WaitStatus(s, h, StatusReady, 30000, out _);
        Thread.Sleep(300);
        GetWindowRect(host, out RECT r);
        W($"layout.json saved {saved}: {json}; panel after reopen at 40% request: [{r.Left - p.X},{r.Top - p.Y},{r.Right - r.Left}x{r.Bottom - r.Top}] client {gc.Right}x{gc.Bottom}");
        bool remembered = Math.Abs(r.Left - p.X - 20) <= 2 && Math.Abs(r.Top - p.Y - 30) <= 2 && Math.Abs((r.Right - r.Left) - gc.Right / 2) <= 2;
        if (!saved) Fail("LayoutRemembered", "no layout.json after WM_EXITSIZEMOVE");
        else if (!json.Contains("\"x\"") || !json.Contains("\"h\"")) Fail("LayoutRemembered", "layout.json has no x/y/w/h: " + json);
        else if (!remembered) Fail("LayoutRemembered", "the reopened panel did not take the stored layout over the 40% request");
        else Ok("LayoutRemembered");
        try { File.Delete(path); } catch { }
        return h;
    }

    private static void CheckUnloadAfterClose(ref Session s, int handle)
    {
        int[] st = State(s, handle);
        int live = st[1];
        s.Call("Close", live);
        WaitStatus(s, live, StatusClosed, 5000, out _);
        IntPtr host = (IntPtr)s.Call("get_HostHwnd")!;
        var sd = Stopwatch.StartNew();
        s.Call("Shutdown", 2000);
        W($"Shutdown(2000) returned in {sd.ElapsedMilliseconds} ms (panel closed)");
        bool stopped = !(bool)s.Call("get_ThreadRunning")!;
        var (afterOpen, _) = Open(s, "https://panel.test/index.html", Allow);
        int afterState = State(s, live)[0];
        int afterClose = (int)s.Call("Close", live)!;
        W($"after Shutdown: thread stopped {stopped}, host window alive {IsWindow(host)}, Open {afterOpen}, GetState {afterState}, Close {afterClose}");
        if (!stopped || IsWindow(host) || afterOpen != CodeShuttingDown || afterState != CodeShuttingDown || afterClose != CodeShuttingDown)
            Fail("ShutdownRefusesCalls", "thread or window survived, or a call did not return ShuttingDown");
        else Ok("ShutdownRefusesCalls");
        UnloadAndJudge("UnloadAfterCloseCollected", ref s);
    }

    private static void CheckUnloadWhileVisible(string payloadDir, string lad, string panelDir, string otherDir)
    {
        var s = new Session(payloadDir);
        s.Call("Init", _game, payloadDir, lad, panelDir, otherDir,
            (Action<string>)(l => { LogLines.Enqueue(l); W("  engine log (2): " + l); }),
            (Action<string>)(c => ChatLines.Enqueue(c)),
            (Action<string>)(u => BrowserCalls.Enqueue(u)));
        _pumpSession = s;
        var (h, _) = Open(s, "https://panel.test/launch.html?code=" + Secret, Allow);
        if (!WaitStatus(s, h, StatusReady, 30000, out int[] st)) { Fail("UnloadWhileVisibleCollected", $"not Ready [{string.Join(",", st)}]"); }
        bool visible = IsWindowVisible((IntPtr)s.Call("get_HostHwnd")!);
        W($"session 2 open: state [{string.Join(",", State(s, h))}] host visible {visible}");
        var sd = Stopwatch.StartNew();
        s.Call("Shutdown", 2000);
        W($"Shutdown(2000) returned in {sd.ElapsedMilliseconds} ms (panel open; window shown only if the harness held the foreground)");
        UnloadAndJudge("UnloadWhileVisibleCollected", ref s, visible ? null : "collected, but the panel window was not visible (the PID rule hid it: the harness did not hold the foreground), so the visible-teardown path was not shown");
    }

    private static void UnloadAndJudge(string name, ref Session s, string? unverifiedIfCollected = null)
    {
        _pumpSession = null;
        _pumpHandle = 0;
        Thread.Sleep(100);
        s.Call("Release");
        WeakReference weak = s.Unload();
        s = null!;
        var sw = Stopwatch.StartNew();
        while (weak.IsAlive && sw.ElapsedMilliseconds < 3000) { GC.Collect(); GC.WaitForPendingFinalizers(); if (weak.IsAlive) Thread.Sleep(20); }
        W($"{name}: context {(weak.IsAlive ? "STILL HELD" : "collected")} after {sw.ElapsedMilliseconds} ms");
        if (weak.IsAlive) Fail(name, "the payload context was not collected within 3000 ms");
        else if (unverifiedIfCollected != null) Unverified(name, unverifiedIfCollected);
        else Ok(name);
    }

    // ============================== helpers ==============================

    private static (int Rc, double Ms) Open(Session s, string url, string allow, string key = "", string title = "",
        uint flags = FlagFallback, int w = 0, int hgt = 0)
    {
        IntPtr pUrl = Utf8(url), pAllow = Utf8(allow), pKey = key.Length == 0 ? IntPtr.Zero : Utf8(key), pTitle = title.Length == 0 ? IntPtr.Zero : Utf8(title);
        IntPtr pArgs = Marshal.AllocHGlobal(sizeof(OpenArgs));
        var a = (OpenArgs*)pArgs;
        *a = default;
        a->Size = (uint)sizeof(OpenArgs);
        a->Flags = flags;
        a->Url = (byte*)pUrl; a->Allowlist = (byte*)pAllow; a->ProfileKey = (byte*)pKey; a->Title = (byte*)pTitle;
        a->WidthPercent = w; a->HeightPercent = hgt;
        double ms = 0;
        string r = Game(() =>
        {
            long t0 = Stopwatch.GetTimestamp();
            int rc = (int)s.Call("Open", pArgs)!;
            ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            // The SDK's buffers are stack or pooled: gone the moment the call returns. Junk them, then free.
            Junk(pUrl); Junk(pAllow); Junk(pKey); Junk(pTitle);
            new Span<byte>((void*)pArgs, sizeof(OpenArgs)).Fill(0xCD);
            return rc.ToString();
        });
        foreach (IntPtr p in new[] { pUrl, pAllow, pKey, pTitle, pArgs }) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
        int code = int.TryParse(r, out int v) ? v : int.MinValue;
        if (code > 0) _pumpHandle = code;
        return (code, ms);
    }

    private static readonly Dictionary<IntPtr, int> Sizes = new();
    private static IntPtr Utf8(string s)
    {
        byte[] b = Encoding.UTF8.GetBytes(s + "\0");
        IntPtr p = Marshal.AllocHGlobal(b.Length);
        Marshal.Copy(b, 0, p, b.Length);
        lock (Sizes) Sizes[p] = b.Length;
        return p;
    }
    private static void Junk(IntPtr p)
    {
        if (p == IntPtr.Zero) return;
        int n; lock (Sizes) n = Sizes[p];
        new Span<byte>((void*)p, n - 1).Fill((byte)'Z');
    }

    private static int[] State(Session s, int h) => (int[])s.Call("GetState", h)!;

    private static bool WaitStatus(Session s, int h, int want, int ms, out int[] last)
    {
        var sw = Stopwatch.StartNew();
        last = State(s, h);
        while (sw.ElapsedMilliseconds < ms)
        {
            last = State(s, h);
            if (last[2] == want) return true;
            if (want != StatusFailed && last[2] == StatusFailed) return false;
            Thread.Sleep(25);
        }
        return false;
    }

    private static bool WaitFor(Func<bool> f, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (f()) return true; Thread.Sleep(25); }
        return f();
    }

    /// <summary>A plain SetForegroundWindow from the game thread (no input, no AttachThreadInput): Windows grants it or not.</summary>
    private static bool EnsureForeground(IntPtr target)
    {
        if (IsOurs(GetForegroundWindow(), target)) return true;
        if (!_allowForeground) return false;
        Game(() => SetForegroundWindow(target) ? "1" : "0");
        Thread.Sleep(200);
        return IsOurs(GetForegroundWindow(), target);
    }

    private static bool IsOurs(IntPtr fg, IntPtr target) => fg != IntPtr.Zero && (fg == target || GetAncestor(fg, 2 /* GA_ROOT */) == target);

    private static string FgDesc()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return "foreground none";
        GetWindowThreadProcessId(fg, out uint pid);
        return $"foreground 0x{fg.ToInt64():X} {Cls(fg)} pid {pid}{(pid == Environment.ProcessId ? " (this harness)" : "")}";
    }

    private static void Drain<T>(ConcurrentQueue<T> q) { while (q.TryDequeue(out _)) { } }

    private static string Redact(string? url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? u) || u.Host.Length == 0) return "<invalid url>";
        return u.Scheme + "://" + u.Host + (u.IsDefaultPort ? "" : ":" + u.Port) + u.AbsolutePath;
    }

    private static void PostKey(IntPtr h, int vk, char ch)
    {
        uint scan = MapVirtualKeyW((uint)vk, 0);
        IntPtr down = (IntPtr)(int)(1 | (scan << 16)), up = unchecked((IntPtr)(int)(1 | (scan << 16) | 0xC0000000));
        // Down and up only: the browser's own TranslateMessage makes the WM_CHAR (posting one too typed every key twice).
        PostMessageW(h, 0x0100, (IntPtr)vk, down);
        PostMessageW(h, 0x0101, (IntPtr)vk, up);
        Thread.Sleep(60);
    }

    private static IntPtr RenderWidget(Session s, out string tree)
    {
        IntPtr host = (IntPtr)s.Call("get_HostHwnd")!;
        var all = new List<IntPtr>();
        Collect(host, all);
        var sb = new StringBuilder();
        IntPtr widget = IntPtr.Zero;
        foreach (IntPtr c in all)
        {
            string cls = Cls(c);
            GetWindowThreadProcessId(c, out uint pid);
            sb.Append($"[0x{c.ToInt64():X} {cls} pid {pid}] ");
            if (widget == IntPtr.Zero && cls == "Chrome_RenderWidgetHostHWND") widget = c;
        }
        tree = sb.ToString();
        return widget;
    }

    private static void Collect(IntPtr h, List<IntPtr> into)
    {
        for (IntPtr c = GetWindow(h, 5 /* GW_CHILD */); c != IntPtr.Zero; c = GetWindow(c, 2 /* GW_HWNDNEXT */))
        {
            into.Add(c);
            Collect(c, into);
        }
    }

    private static List<IntPtr>? _enumInto;
    private static HashSet<IntPtr> TopLevelWindows()
    {
        _enumInto = new List<IntPtr>();
        EnumWindows(&EnumProc, IntPtr.Zero);
        var set = new HashSet<IntPtr>(_enumInto);
        _enumInto = null;
        return set;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int EnumProc(IntPtr h, IntPtr l) { _enumInto?.Add(h); return 1; }

    private static bool OwnedByUsOrWebView(IntPtr w)
    {
        GetWindowThreadProcessId(w, out uint pid);
        if (pid == Environment.ProcessId) return true;
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName.StartsWith("msedgewebview2", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static int WebViewModuleCount()
    {
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        int n = 0;
        foreach (ProcessModule m in p.Modules)
            if (m.ModuleName.Contains("WebView", StringComparison.OrdinalIgnoreCase) || m.ModuleName.Contains("EmbeddedBrowser", StringComparison.OrdinalIgnoreCase)) n++;
        return n;
    }

    // ---- screenshot (PrintWindow PW_RENDERFULLCONTENT -> PNG), from the spike ----
    private static void Snap(IntPtr h, string path)
    {
        try
        {
            GetWindowRect(h, out RECT r);
            int w = r.Right - r.Left, ht = r.Bottom - r.Top;
            if (w <= 0 || ht <= 0) { W("screenshot skipped: empty window rect"); return; }
            IntPtr sdc = GetDC(IntPtr.Zero), mdc = CreateCompatibleDC(sdc), bmp = CreateCompatibleBitmap(sdc, w, ht);
            IntPtr old = SelectObject(mdc, bmp);
            bool ok = PrintWindow(h, mdc, 2);
            var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -ht, biPlanes = 1, biBitCount = 32 };
            byte[] px = new byte[w * ht * 4];
            fixed (byte* p = px) GetDIBits(mdc, bmp, 0, (uint)ht, p, ref bi, 0);
            SelectObject(mdc, old); DeleteObject(bmp); DeleteDC(mdc); ReleaseDC(IntPtr.Zero, sdc);
            WritePng(path, w, ht, px);
            W($"screenshot {path} ({w}x{ht}, PrintWindow ok={ok}, window visible={IsWindowVisible(h)})");
        }
        catch (Exception ex) { W("screenshot failed: " + ex.Message); }
    }

    private static void WritePng(string path, int w, int h, byte[] bgra)
    {
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, true))
            for (int y = 0; y < h; y++)
            {
                z.WriteByte(0);
                for (int x = 0; x < w; x++) { int i = (y * w + x) * 4; z.WriteByte(bgra[i + 2]); z.WriteByte(bgra[i + 1]); z.WriteByte(bgra[i]); }
            }
        using var f = File.Create(path);
        f.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        BE(ihdr, 0, w); BE(ihdr, 4, h); ihdr[8] = 8; ihdr[9] = 2;
        Chunk(f, "IHDR", ihdr); Chunk(f, "IDAT", raw.ToArray()); Chunk(f, "IEND", Array.Empty<byte>());
    }

    private static void BE(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    private static uint[]? _crc;
    private static void Chunk(Stream s, string type, byte[] data)
    {
        if (_crc == null) { _crc = new uint[256]; for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; _crc[n] = c; } }
        var len = new byte[4]; BE(len, 0, data.Length); s.Write(len);
        var td = new byte[4 + data.Length]; Encoding.ASCII.GetBytes(type).CopyTo(td, 0); data.CopyTo(td, 4); s.Write(td);
        uint crc = 0xFFFFFFFF; foreach (byte b in td) crc = _crc[(crc ^ b) & 0xFF] ^ (crc >> 8); crc ^= 0xFFFFFFFF;
        var cb = new byte[4]; BE(cb, 0, (int)crc); s.Write(cb);
    }

    private static string Cls(IntPtr h) { char* b = stackalloc char[128]; int n = GetClassNameW(h, b, 128); return new string(b, 0, n); }

    private static void WriteSite(string panelDir, string otherDir)
    {
        Directory.CreateDirectory(panelDir);
        Directory.CreateDirectory(otherDir);
        string Page(string title) =>
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>" + title + "</title></head>" +
            "<body style=\"background:#202024;color:#ddd;font:18px sans-serif\"><p>" + title + "</p><input id=\"t\" autofocus style=\"font-size:20px\">" +
            "<script>window.keys=[];document.addEventListener('keydown',function(e){window.keys.push(e.key);});</script></body></html>";
        File.WriteAllText(Path.Combine(panelDir, "index.html"), Page("panel index"));
        File.WriteAllText(Path.Combine(panelDir, "launch.html"), Page("panel launch"));
        File.WriteAllText(Path.Combine(panelDir, "b.html"), Page("panel b"));
        File.WriteAllText(Path.Combine(otherDir, "landing.html"), Page("other landing"));
        File.WriteAllText(Path.Combine(otherDir, "x.html"), Page("other x"));
    }

    // ============================== fake game ==============================

    private static string Game(Func<string> fn)
    {
        var done = new ManualResetEventSlim();
        var res = new string[1];
        GameQ.Enqueue((fn, done, res));
        PostThreadMessageW(_gameTid, 0x8000 + 7, IntPtr.Zero, IntPtr.Zero);
        if (!done.Wait(20000)) return "GAME THREAD TIMEOUT";
        return res[0];
    }

    private static void GameThread(ManualResetEventSlim up)
    {
        _gameTid = GetCurrentThreadId();
        string cls = "FakeAcClient";
        fixed (char* pc = cls)
        {
            var wc = new WNDCLASSEXW { cbSize = (uint)sizeof(WNDCLASSEXW), lpfnWndProc = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&GameWndProc, hInstance = GetModuleHandleW(IntPtr.Zero), hbrBackground = (IntPtr)(1 + 1), lpszClassName = (IntPtr)pc };
            RegisterClassExW(ref wc);
        }
        _game = CreateWindowExW(0, cls, "Fake acclient (web panel harness)", 0x00CF0000 /* WS_OVERLAPPEDWINDOW */, 40, 40, 1100, 750, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(IntPtr.Zero), IntPtr.Zero);
        ShowWindow(_game, 4 /* SW_SHOWNOACTIVATE */);
        up.Set();
        while (true)
        {
            while (PeekMessageW(out MSG m, IntPtr.Zero, 0, 0, 1))
            {
                if (m.hwnd == IntPtr.Zero && m.message == 0x8000 + 7) continue;
                TranslateMessage(ref m);
                DispatchMessageW(ref m);
            }
            while (GameQ.TryDequeue(out var job))
            {
                try { job.Result[0] = job.Fn(); } catch (Exception ex) { job.Result[0] = "THREW " + (ex.InnerException ?? ex); }
                job.Done.Set();
            }
            MsgWaitForMultipleObjects(0, IntPtr.Zero, false, 10, 0x04FF);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static IntPtr GameWndProc(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (m is 0x0100 or 0x0101 or 0x0102 or 0x0104 or 0x0105) Interlocked.Increment(ref _gameKeyMsgs);
        return DefWindowProcW(h, m, w, l);
    }

    private static void PumpLoop()
    {
        while (!_pumpStop)
        {
            Thread.Sleep(16);
            PumpOnce();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void PumpOnce()
    {
        Session? s = _pumpSession;
        int h = _pumpHandle;
        if (s == null || h == 0) return;
        try { s.Call("GetState", h); Interlocked.Increment(ref _pumpCalls); } catch { }
    }

    // ============================== payload context ==============================

    /// <summary>The payload in a collectible context (the shim's EngineLoadContext, reproduced).</summary>
    private sealed class Session
    {
        private PayloadContext? _alc;
        private Type? _type;

        public Session(string dir)
        {
            _alc = new PayloadContext(dir);
            Assembly asm = _alc.LoadFromStreamOf(Path.Combine(dir, "WebPanelHarness.Payload.dll"));
            _type = asm.GetType("RynthCore.Engine.WebPanel.PayloadEntry", throwOnError: true)!;
            W($"payload loaded into a collectible context (IsCollectible={_alc.IsCollectible}, location '{asm.Location}')");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public object? Call(string name, params object?[] args)
        {
            Type t = _type ?? throw new InvalidOperationException("payload unloaded");
            MethodInfo m = t.GetMethod(name, BindingFlags.Public | BindingFlags.Static)
                           ?? throw new MissingMethodException(name);
            return m.Invoke(null, args);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public WeakReference Unload()
        {
            PayloadContext alc = _alc!;
            _alc = null;
            _type = null;
            alc.Unload();
            return new WeakReference(alc, trackResurrection: true);
        }
    }

    private sealed class PayloadContext : AssemblyLoadContext
    {
        private static readonly HashSet<string> PrivateFramework = new(StringComparer.OrdinalIgnoreCase) { "System.Text.Json", "System.Text.Encodings.Web", "System.IO.Pipelines" };
        private readonly string _dir;
        public PayloadContext(string dir) : base("web panel payload", isCollectible: true) => _dir = dir;

        public Assembly LoadFromStreamOf(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            string pdb = Path.ChangeExtension(path, ".pdb");
            if (!File.Exists(pdb)) return LoadFromStream(fs);
            using var ps = new FileStream(pdb, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return LoadFromStream(fs, ps);
        }

        protected override Assembly? Load(AssemblyName name)
        {
            string? simple = name.Name;
            if (string.IsNullOrEmpty(simple)) return null;
            if (PrivateFramework.Contains(simple))
            {
                string fx = Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, simple + ".dll");
                return File.Exists(fx) ? LoadFromAssemblyPath(fx) : null;
            }
            string local = Path.Combine(_dir, simple + ".dll");
            return File.Exists(local) ? LoadFromStreamOf(local) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string candidate = Path.Combine(_dir, unmanagedDllName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? unmanagedDllName : unmanagedDllName + ".dll");
            return File.Exists(candidate) ? LoadUnmanagedDllFromPath(candidate) : IntPtr.Zero;
        }
    }

    // ============================== interop ==============================

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct OpenArgs
    {
        public uint Size, Flags;
        public byte* Url, Allowlist, ProfileKey, Title;
        public int WidthPercent, HeightPercent;
        public fixed uint Reserved[4];
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFOHEADER { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
    [StructLayout(LayoutKind.Sequential)] private struct WNDCLASSEXW { public uint cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground, lpszMenuName, lpszClassName, hIconSm; }

    [DllImport("user32.dll")] private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out MSG m, IntPtr h, uint a, uint b, uint r);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint t, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern uint MsgWaitForMultipleObjects(uint n, IntPtr h, bool all, uint ms, uint mask);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint c);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint f);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr h, char* b, int n);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint code, uint type);
    [DllImport("user32.dll")] private static extern bool EnumWindows(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int> fn, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte* bits, ref BITMAPINFOHEADER bi, uint usage);
    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_BASIC_INFORMATION { public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId; }
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr h, int cls, void* info, int len, out int ret);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandleW(IntPtr n);
}
