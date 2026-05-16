// Minimal Avalonia App for the DComp test harness. Single window, no theme
// content beyond what's needed for transparency to render correctly.

using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Simple;

namespace RynthCore.Engine.UI.Dcomp;

internal sealed class DcompOverlayApp : Application
{
    public override void Initialize()
    {
        // Use Avalonia.Themes.Simple — already trim-rooted in the engine csproj
        // (same as the production overlay path). Don't pull Themes.Fluent which
        // would add another ~MB of resources we don't need for the test panel.
        Styles.Add(new SimpleTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new DcompOverlayWindow();
            desktop.MainWindow = window;
            window.Show();

            // Once the HWND exists, parent it to AC's HWND for owner Z-order.
            // This must happen after Show() because Avalonia creates the native
            // HWND lazily on first show.
            window.AttachToGameWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
