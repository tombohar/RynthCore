// ============================================================================
//  RynthCore.Engine - UI/Panels/HelloBoxPanel.cs
//  Shows live status from the HelloBox plugin. Resolves the plugin's
//  RynthPluginGetStatusText export via GetProcAddress on the module handle —
//  no changes to the plugin contract needed.
// ============================================================================

using System;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.UI.Panels;

internal static class HelloBoxPanel
{
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetStatusTextFn();

    private static GetStatusTextFn? _getStatusText;

    internal static Control Create()
    {
        TryBindDelegate();

        var textBlock = new TextBlock
        {
            FontFamily  = new FontFamily("Consolas,Courier New,monospace"),
            FontSize    = 10,
            Foreground  = new SolidColorBrush(Color.FromArgb(200, 210, 210, 210)),
            TextWrapping = TextWrapping.Wrap,
            Margin      = new Avalonia.Thickness(8, 6)
        };

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = textBlock
        };

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            if (_getStatusText == null)
                TryBindDelegate();

            string text;
            if (_getStatusText != null)
            {
                try
                {
                    IntPtr ptr = _getStatusText();
                    text = Marshal.PtrToStringAnsi(ptr) ?? "(empty)";
                }
                catch (Exception ex)
                {
                    text = $"Error reading status: {ex.Message}";
                }
            }
            else
            {
                var plugin = PluginManager.Plugins.FirstOrDefault(p => p.DisplayName.Contains("Hello"));
                text = plugin == null
                    ? "Hello Box plugin not loaded."
                    : $"Hello Box loaded ({plugin.DisplayName}) but RynthPluginGetStatusText not found.";
            }

            if (textBlock.Text != text)
                textBlock.Text = text;
        };
        scroll.DetachedFromVisualTree += (_, _) => timer.Stop();
        timer.Start();

        return scroll;
    }

    private static void TryBindDelegate()
    {
        var plugin = PluginManager.Plugins.FirstOrDefault(p => p.DisplayName.Contains("Hello"));
        if (plugin == null)
            return;

        IntPtr fn = GetProcAddress(plugin.ModuleHandle, "RynthPluginGetStatusText");
        if (fn == IntPtr.Zero)
            return;

        if (_getStatusText != null)
            return;

        _getStatusText = Marshal.GetDelegateForFunctionPointer<GetStatusTextFn>(fn);
        RynthLog.UI($"HelloBoxPanel: bound RynthPluginGetStatusText for '{plugin.DisplayName}'.");
    }
}
