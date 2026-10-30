using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using RynthCore;

namespace RynthCore.App.Avalonia;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        try
        {
            string t = DateTime.Now.ToString("O");
            DesktopRollingLog.AppendLine(DesktopRollingLog.StemLauncher,
                $"[{t}] [pid:{Environment.ProcessId}] RynthCore.App.Avalonia started (launcher).");
            // Detailed host + sidecar file layout (see LauncherHostDiagnostics) for runtime mis-detection triage.
            LauncherHostDiagnostics.AppendDesktopLogBlock(t);
        }
        catch
        {
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();

        base.OnFrameworkInitializationCompleted();
    }
}
