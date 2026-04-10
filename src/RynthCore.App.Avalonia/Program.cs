using System;
using Avalonia;
using Avalonia.Win32;

namespace RynthCore.App.Avalonia;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions
            {
                RenderingMode =
                [
                    Win32RenderingMode.Software
                ],
                CompositionMode =
                [
                    Win32CompositionMode.RedirectionSurface
                ]
            })
            .WithInterFont()
            .LogToTrace();
}
