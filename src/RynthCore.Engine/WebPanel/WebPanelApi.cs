using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.WebPanel;

#if ENGINE_CORECLR
/// <summary>
/// The engine side of the v94 web panel calls (PluginContract.cs WebPanelOpenFn..WebPanelGetStateFn): reads
/// the native arguments (every string copied before the call returns) and hands them to the one
/// WebPanelHost. No call here blocks its caller (AC's main thread, the plugin pump and ImGui draws all call
/// these). engine.json "WebPanel": false makes Open answer Unavailable (plugins fall back to the browser).
/// </summary>
internal static unsafe class WebPanelApi
{
    private const int ShutdownBudgetMs = 2000;
    private static readonly object Sync = new();
    private static WebPanelHost? s_host;
    private static EngineWebPanelEnvironment? s_env;
    private static volatile bool s_shutdown;

    internal static int Open(WebPanelOpenNative* open)
    {
        try
        {
            int refusal = WebPanelApiRules.OpenRefusal(s_shutdown || EngineLifecycle.IsShuttingDown, EngineSettings.WebPanel);
            if (refusal != 0) return refusal;
            EngineWebPanelEnvironment env;
            WebPanelHost host;
            lock (Sync)
            {
                if (s_shutdown) return (int)WebPanelCode.ShuttingDown;
                env = s_env ??= new EngineWebPanelEnvironment();
                host = s_host ??= new WebPanelHost(env);
            }
            int rc = WebPanelNativeArgs.TryBuildRequest(open, env.LocalAppData, env.WorldName, out WebPanelRequest? request);
            if (rc != 0) return rc;
            return host.Open(request!);
        }
        catch (System.Exception ex)
        {
            RynthLog.Info("WebPanel: Open failed: " + ex.GetType().Name);
            return (int)WebPanelCode.Unavailable;
        }
    }

    internal static int Navigate(int handle, byte* urlUtf8)
    {
        try
        {
            int refusal = WebPanelApiRules.CallRefusal(s_shutdown);
            if (refusal != 0) return refusal;
            if (!WebPanelNativeArgs.TryReadUtf8(urlUtf8, WebPanelUrlPolicy.MaxUrlBytes, true, out string url)) return (int)WebPanelCode.BadArgs;
            WebPanelHost? host = s_host;
            return host == null ? (int)WebPanelCode.StaleHandle : host.Navigate(handle, url);
        }
        catch (System.Exception ex)
        {
            RynthLog.Info("WebPanel: Navigate failed: " + ex.GetType().Name);
            return (int)WebPanelCode.Unavailable;
        }
    }

    internal static int Close(int handle)
    {
        try
        {
            int refusal = WebPanelApiRules.CallRefusal(s_shutdown);
            if (refusal != 0) return refusal;
            WebPanelHost? host = s_host;
            return host == null ? (int)WebPanelCode.StaleHandle : host.Close(handle);
        }
        catch (System.Exception ex)
        {
            RynthLog.Info("WebPanel: Close failed: " + ex.GetType().Name);
            return (int)WebPanelCode.Unavailable;
        }
    }

    internal static int GetState(int handle, WebPanelStateNative* state)
    {
        try
        {
            if (!WebPanelApiRules.StateArgsOk(state)) return (int)WebPanelCode.BadArgs;
            int refusal = WebPanelApiRules.CallRefusal(s_shutdown);
            if (refusal != 0) return refusal;
            WebPanelHost? host = s_host;
            WebPanelSnapshot s;
            int r;
            if (host == null)
            {
                s = WebPanelApiRules.NoPanel(handle);
                r = s.Status;
            }
            else
            {
                r = host.GetState(handle, out s);
            }
            WebPanelApiRules.WriteState(state, s);
            return r;
        }
        catch (System.Exception ex)
        {
            RynthLog.Info("WebPanel: GetState failed: " + ex.GetType().Name);
            return (int)WebPanelCode.Unavailable;
        }
    }

    /// <summary>EngineLifecycle, after the plugins' own Shutdowns (they may still Close). Idempotent.</summary>
    internal static void Shutdown()
    {
        WebPanelHost? host;
        lock (Sync)
        {
            s_shutdown = true;
            host = s_host;
        }
        host?.Shutdown(ShutdownBudgetMs);
    }

    /// <summary>The live panel handle, 0 when none was opened: lets the engine close it (WebPanelApi.Close(LiveHandle)).</summary>
    internal static int LiveHandle => s_host?.LiveHandle ?? 0;

    /// <summary>Redacted one-liner (no query, fragment or userinfo); includes the WebView2 browser pid.</summary>
    internal static string Describe()
    {
        WebPanelHost? host = s_host;
        string enabled = EngineSettings.WebPanel ? "on" : "off (engine.json WebPanel)";
        return host == null ? "web panel: " + enabled + ", not started" : host.Describe() + "; setting " + enabled;
    }
}
#else
/// <summary>The NativeAOT engine has no web panel: PluginManager leaves the v94 slots zero (HasWebPanel false).</summary>
internal static unsafe class WebPanelApi
{
    internal static int Open(WebPanelOpenNative* open) => (int)WebPanelCode.Unavailable;
    internal static int Navigate(int handle, byte* url) => (int)WebPanelCode.Unavailable;
    internal static int Close(int handle) => (int)WebPanelCode.Unavailable;
    internal static int GetState(int handle, WebPanelStateNative* state) => (int)WebPanelCode.Unavailable;
    internal static void Shutdown() { }
    internal static int LiveHandle => 0;
    internal static string Describe() => "web panel: not available in this engine build";
}
#endif
