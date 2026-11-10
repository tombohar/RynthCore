// The harness's way into the web panel host. Everything crossing into the harness is a framework
// type (int, string, IntPtr, arrays, Action<string>), so nothing of this collectible context is held
// by the default one. The calls mirror WebPanelApi (the engine's ABI glue): Open reads the native
// struct with WebPanelNativeArgs exactly as the engine does.
using System;
using System.IO;
using System.Threading;
using Microsoft.Web.WebView2.Core;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.WebPanel;

public static unsafe class PayloadEntry
{
    private static StubEnvironment? _env;
    private static WebPanelHost? _host;

    public static void Init(IntPtr gameHwnd, string runtimeDir, string localAppData, string panelDir, string otherDir,
        Action<string> log, Action<string> chat, Action<string> browser)
    {
        _env = new StubEnvironment(gameHwnd, runtimeDir, localAppData, panelDir, otherDir, log, chat, browser);
        _host = new WebPanelHost(_env);
    }

    public static void SetEnvironmentDelay(int ms) => _env!.DelayMs = ms;
    public static void SetBrowserFolder(string? folder) => _env!.BrowserFolder = folder;
    public static void SetBrowserFails(bool fails) => _env!.BrowserFails = fails;
    public static void SetBrowserDelay(int ms) => _env!.BrowserDelayMs = ms;
    public static void SetWorld(string world) => _env!.World = world;

    /// <summary>The engine's WebPanelApi.Open path: read the caller's struct, then the host.</summary>
    public static int Open(IntPtr nativeArgs)
    {
        int rc = WebPanelNativeArgs.TryBuildRequest((WebPanelOpenNative*)nativeArgs, _env!.LocalAppData, _env.WorldName, out WebPanelRequest? req);
        if (rc != 0) return rc;
        return _host!.Open(req!);
    }

    public static int Navigate(int handle, string url) => _host!.Navigate(handle, url);
    public static int Close(int handle) => _host!.Close(handle);

    /// <summary>[return, handle, status, flags, error, http, navigations].</summary>
    public static int[] GetState(int handle)
    {
        int r = _host!.GetState(handle, out WebPanelSnapshot s);
        return new[] { r, s.Handle, s.Status, (int)s.Flags, s.Error, s.LastHttpStatus, (int)s.NavigationCount };
    }

    public static void Shutdown(int budgetMs) => _host!.Shutdown(budgetMs);
    public static string Describe() => _host!.Describe();

    public static IntPtr HostHwnd => _host!.HostHwndForTests;
    public static int EnvironmentCount => _host!.EnvironmentCountForTests;
    public static int BrowserProcessId => (int)_host!.BrowserProcessIdForTests;
    public static int AcceleratorKeyEvents => _host!.AcceleratorKeyEventsForTests;
    public static string Source => _host!.SourceForTests;
    public static bool ThreadRunning => _host!.ThreadRunningForTests;
    public static bool PanelOpen => _host!.PanelOpenForTests;
    public static bool FailHandle(int handle) => _host!.FailForTests(handle);
    public static bool ReportUnresponsive() => _host!.UnresponsiveForTests();

    public static string ProfileFolder(string world, string key) => WebPanelProfile.Folder(_env!.LocalAppData, world, key);

    /// <summary>Runs script in the page on the panel thread; waits here (the harness thread), never there.</summary>
    public static string Eval(string js, int timeoutMs)
    {
        var done = new ManualResetEventSlim();
        string result = "TIMEOUT";
        bool posted = _host!.PostForTests(web =>
        {
            if (web == null) { result = "no webview"; done.Set(); return; }
            try
            {
                web.ExecuteScriptAsync(js).ContinueWith(t =>
                {
                    result = t.IsCompletedSuccessfully ? t.Result : "ERR " + t.Exception?.GetBaseException().Message;
                    done.Set();
                });
            }
            catch (Exception ex) { result = "ERR " + ex.Message; done.Set(); }
        });
        if (!posted) return "panel thread not running";
        done.Wait(timeoutMs);
        return result;
    }

    /// <summary>Drops this context's statics so nothing in it is reachable before the unload.</summary>
    public static void Release()
    {
        _host = null;
        _env = null;
    }

    private sealed class StubEnvironment : IWebPanelEnvironment
    {
        private readonly Action<string> _log, _chat, _browser;
        private readonly string _panelDir, _otherDir;

        public StubEnvironment(IntPtr game, string runtimeDir, string localAppData, string panelDir, string otherDir,
            Action<string> log, Action<string> chat, Action<string> browser)
        {
            GameHwnd = game;
            RuntimeDir = runtimeDir;
            LocalAppData = localAppData;
            _panelDir = panelDir;
            _otherDir = otherDir;
            _log = log;
            _chat = chat;
            _browser = browser;
        }

        public volatile int DelayMs;
        public volatile string? BrowserFolder;
        public volatile bool BrowserFails; // OpenInBrowser returns false without recording a call
        public volatile int BrowserDelayMs; // OpenInBrowser sleeps first (a slow ShellExecute)
        public volatile string World = "Harness World";

        public IntPtr GameHwnd { get; }
        public string RuntimeDir { get; }
        public string LocalAppData { get; }
        public string WorldName => World;
        public void Log(string line) => _log(line);
        public void Chat(string line) => _chat(line);
        public bool OpenInBrowser(string url)
        {
            if (!WebPanelUrlPolicy.IsBrowserSafe(url)) { _log("HARNESS: OpenInBrowser got an unsafe url"); return false; }
            if (BrowserDelayMs > 0) Thread.Sleep(BrowserDelayMs);
            if (BrowserFails) { _log("HARNESS: OpenInBrowser set to fail"); return false; }
            _browser(url);
            return true;
        }
        public Thread StartStaThread(string name, ThreadStart body)
        {
            var t = new Thread(body) { Name = name, IsBackground = true };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            return t;
        }
        public int EnvironmentDelayMsForTests => DelayMs;
        public string? BrowserExecutableFolderForTests => BrowserFolder;

        public Action<CoreWebView2>? ConfigureForTests => web =>
        {
            web.SetVirtualHostNameToFolderMapping("panel.test", _panelDir, CoreWebView2HostResourceAccessKind.Allow);
            web.SetVirtualHostNameToFolderMapping("other.test", _otherDir, CoreWebView2HostResourceAccessKind.Allow);
            // https://go.panel.test/redirect answers 302 to other.test. Not folder-mapped: WebResourceRequested
            // is not raised for a virtual host mapping's own requests (measured: the filter on panel.test/* never fired).
            web.AddWebResourceRequestedFilter("https://go.panel.test/*", CoreWebView2WebResourceContext.All);
            web.WebResourceRequested += (s, e) =>
            {
                if (!e.Request.Uri.StartsWith("https://go.panel.test/redirect", StringComparison.OrdinalIgnoreCase)) return;
                e.Response = web.Environment.CreateWebResourceResponse(null, 302, "Found",
                    "Location: https://other.test/landing.html?code=SECRET123\r\nCache-Control: no-store");
                _log("HARNESS: served the 302 for /redirect");
            };
        };
    }
}
