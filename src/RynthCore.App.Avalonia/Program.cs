using System;
using Avalonia;
using Avalonia.Win32;

namespace RynthCore.App.Avalonia;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // The uninstaller removes the Decal bridge registration (docs/DECAL_BRIDGE_PLAN.md):
        // no window. Per-user: our entry in a per-user copy of Decal's filter list, and a copy an
        // earlier RynthCore created (backed up to %APPDATA%\RynthCore\decal-backup-<date>.reg first;
        // kept, with the reason logged, when it holds entries that aren't ours or in HKLM).
        if (Array.Exists(args, a => string.Equals(a, "--decal-bridge-unregister", StringComparison.OrdinalIgnoreCase)))
        {
            bool ok = RynthCore.App.DecalBridgeRegistration.Unregister(out string report);
            LauncherDiag.Info($"DECALBRIDGE: unregister (command line): {report}");
            // The machine-wide entry for THIS install goes too: the uninstaller removes the DLL it
            // points at. Ask for the rights only when there is one (another install's entry stays).
            string dir = RynthCore.App.DecalBridgeRegistration.DefaultBridgeDirectory;
            if (RynthCore.App.DecalBridgeRegistration.MachineEntryIsOurs(dir))
            {
                var outcome = RynthCore.App.DecalBridgeRegistration.UnregisterMachineWide(out string mReport, dir);
                if (outcome == RynthCore.App.DecalBridgeRegistration.Outcome.NeedsAdmin && Environment.ProcessPath is string self)
                    outcome = RynthCore.App.DecalBridgeRegistration.RunElevated(self, $"--decal-bridge-unregister-machine \"{dir}\"", out mReport);
                LauncherDiag.Info($"DECALBRIDGE: unregister machine-wide (command line): {mReport}");
                ok &= outcome == RynthCore.App.DecalBridgeRegistration.Outcome.Registered;
            }
            return ok ? 0 : 1;
        }
        // --decal-bridge-unregister-machine ["<bridge folder>"]: the elevated step of the above and
        // of the launcher's "no account uses Decal + RynthCore any more". With a folder, only an
        // entry for that folder (or a dangling one) is removed.
        int um = Array.FindIndex(args, a => string.Equals(a, "--decal-bridge-unregister-machine", StringComparison.OrdinalIgnoreCase));
        if (um >= 0)
        {
            string? onlyFor = um + 1 < args.Length && !args[um + 1].StartsWith("--", StringComparison.Ordinal) ? args[um + 1] : null;
            var outcome = RynthCore.App.DecalBridgeRegistration.UnregisterMachineWide(out string report, onlyFor);
            LauncherDiag.Info($"DECALBRIDGE: unregister machine-wide (elevated step): {report}");
            return outcome == RynthCore.App.DecalBridgeRegistration.Outcome.Registered ? 0 : 1;
        }
        // The launcher's (and the installer's) elevated step: the bridge into Decal's filter list
        // for all users (MainWindow.EnsureDecalBridgeAsync): --decal-bridge-register-machine "<bridge folder>".
        int rm = Array.FindIndex(args, a => string.Equals(a, "--decal-bridge-register-machine", StringComparison.OrdinalIgnoreCase));
        if (rm >= 0)
        {
            string dir = rm + 1 < args.Length ? args[rm + 1] : RynthCore.App.DecalBridgeRegistration.DefaultBridgeDirectory;
            var outcome = RynthCore.App.DecalBridgeRegistration.RegisterMachineWide(dir, out string report);
            LauncherDiag.Info($"DECALBRIDGE: register machine-wide (elevated step): {report}");
            return outcome == RynthCore.App.DecalBridgeRegistration.Outcome.Registered ? 0 : 1;
        }
        // The installer's per-user half (installer\RynthCore.iss, as the installing user, on install
        // and update, only for a user with a Decal + RynthCore account): an earlier RynthCore's
        // per-user copy of Decal's filter list removed (after a backup), our entry into a per-user
        // copy that exists anyway. Never creates one.
        int iu = Array.FindIndex(args, a => string.Equals(a, "--decal-bridge-install-user", StringComparison.OrdinalIgnoreCase));
        if (iu >= 0)
        {
            string dir = iu + 1 < args.Length ? args[iu + 1] : RynthCore.App.DecalBridgeRegistration.DefaultBridgeDirectory;
            bool ok = RynthCore.App.DecalBridgeRegistration.InstallPerUser(dir, out string report);
            LauncherDiag.Info($"DECALBRIDGE: install per-user (installer): {report}");
            return ok ? 0 : 1;
        }
        // Recovery Actions > Fix folder permissions, the elevated step (MainWindow.FolderAccess.cs):
        // --fix-folder-permissions <user SID> [--report "<file>"] "<folder>" ["<folder>"...]. Never
        // falls through to the window: a malformed command exits with 2.
        if (Array.Exists(args, a => string.Equals(a, RynthCore.App.FolderAccess.RepairArgument, StringComparison.OrdinalIgnoreCase)))
        {
            if (!RynthCore.App.FolderAccess.TryParseRepairArguments(args, out string sid, out var roots, out string? reportPath))
            {
                LauncherDiag.Info("FOLDERS: fix folder permissions (elevated step): malformed command line; nothing changed.");
                return 2;
            }
            var report = RynthCore.App.FolderAccess.Repair(roots, new System.Security.Principal.SecurityIdentifier(sid));
            LauncherDiag.Info("FOLDERS: fix folder permissions (elevated step): " + report.ToString().Replace(Environment.NewLine, " | "));
            // The report goes back to the launcher that asked, only as a new file of the expected name.
            if (reportPath != null && System.IO.Path.GetFileName(reportPath).StartsWith("rynthcore-folderfix-", StringComparison.OrdinalIgnoreCase)
                && reportPath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && !System.IO.File.Exists(reportPath))
            {
                try { System.IO.File.WriteAllText(reportPath, report.ToString()); } catch { }
            }
            return report.Ok ? 0 : 1;
        }
        if (Array.Exists(args, a => string.Equals(a, "--decal-bridge-status", StringComparison.OrdinalIgnoreCase)))
        {
            LauncherDiag.Info($"DECALBRIDGE: status (command line): {RynthCore.App.DecalBridgeRegistration.GetStatus()}");
            return 0;
        }
        if (Array.Exists(args, a => string.Equals(a, "--decal-bridge-check", StringComparison.OrdinalIgnoreCase)))
        {
            var check = RynthCore.App.RealDecalCheckHost.Check(RynthCore.App.DecalBridgeRegistration.DefaultBridgeDirectory,
                RynthCore.App.DecalLocator.TryGetDecalAcClientPath(), EngineJsonStore.Path);
            LauncherDiag.Info($"DECALBRIDGE: check (command line):{Environment.NewLine}{check.ToReport()}");
            return check.Blocking ? 1 : 0;
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

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
