using System;
using System.IO;
using System.Reflection;
using System.Threading;
using Microsoft.Web.WebView2.Core;

namespace RynthCore.Engine.WebPanel;

/// <summary>The web panel host's surroundings inside acclient.exe (CoreCLR engine only).</summary>
internal sealed class EngineWebPanelEnvironment : IWebPanelEnvironment
{
    private readonly string _runtimeDir = FindRuntimeDir();

    public IntPtr GameHwnd
    {
        get
        {
            IntPtr h = ImGuiBackend.Win32Backend.GameHwnd;
            return h != IntPtr.Zero ? h : EntryPoint.GameHwnd;
        }
    }

    /// <summary>
    /// Runtime\: the folder of the loaded RynthCore.Shim assembly, as ManagedPlugins.FindSharedDir finds it.
    /// Never this assembly's Location: the engine is stream-loaded, so that is empty.
    /// </summary>
    public string RuntimeDir => _runtimeDir;

    public string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public string WorldName => Compatibility.AccountHooks.TryGetWorldName(out string name) ? name : "";

    public void Log(string line) => RynthLog.Info("WebPanel: " + line);

    public void Chat(string line) => Compatibility.ClientHelperHooks.WriteToChat(line, 1);

    public bool OpenInBrowser(string url)
    {
        if (!WebPanelUrlPolicy.IsBrowserSafe(url)) return false;
        IntPtr r = WebPanelNative.ShellExecuteW(IntPtr.Zero, "open", url, null, null, WebPanelNative.SW_SHOWNORMAL);
        return r.ToInt64() > 32;
    }

    public Thread StartStaThread(string name, ThreadStart body) => EngineThreads.Start(name, body, ApartmentState.STA);

    public Action<CoreWebView2>? ConfigureForTests => null;
    public int EnvironmentDelayMsForTests => 0;
    public string? BrowserExecutableFolderForTests => null;

    private static string FindRuntimeDir()
    {
        try
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.GetName().Name != "RynthCore.Shim" || string.IsNullOrEmpty(a.Location)) continue;
                return Path.GetDirectoryName(a.Location) ?? "";
            }
        }
        catch { }
        return "";
    }
}
