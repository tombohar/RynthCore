using System;
using System.Threading;

namespace RynthCore.Engine.WebPanel;

/// <summary>
/// What the web panel host needs from its surroundings: the engine (EngineWebPanelEnvironment) or the
/// offline harness (tools\WebPanelHarness.Payload). CoreCLR engine only.
/// </summary>
internal interface IWebPanelEnvironment
{
    IntPtr GameHwnd { get; }
    /// <summary>Folder holding WebView2Loader.dll and Microsoft.Web.WebView2.Core.dll.</summary>
    string RuntimeDir { get; }
    /// <summary>Profile and layout root (the harness: a temp folder).</summary>
    string LocalAppData { get; }
    /// <summary>"" when unknown.</summary>
    string WorldName { get; }
    /// <summary>Only ever given WebPanelUrlPolicy.Redact()ed URLs or hosts.</summary>
    void Log(string line);
    /// <summary>Any thread.</summary>
    void Chat(string line);
    /// <summary>IsBrowserSafe URLs only; the harness records instead of launching.</summary>
    bool OpenInBrowser(string url);
    Thread StartStaThread(string name, ThreadStart body);
    /// <summary>Null in the engine.</summary>
    Action<Microsoft.Web.WebView2.Core.CoreWebView2>? ConfigureForTests { get; }
    /// <summary>0 in the engine.</summary>
    int EnvironmentDelayMsForTests { get; }
    /// <summary>Null in the engine (the installed Evergreen runtime); the harness forces a creation failure with it.</summary>
    string? BrowserExecutableFolderForTests { get; }
}
