using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading.Tasks;
using RynthCore.Install;

namespace RynthCore.App.Avalonia;

/// <summary>
/// Recovery Actions > Fix folder permissions, and the start-up check that suggests it
/// (src\RynthCore.App\FolderAccess.cs, 2026-10-13). Players reported RynthAi metas and navs not
/// saving: the game client (never elevated) couldn't write in C:\Games\RynthSuite.
/// </summary>
internal partial class MainWindow
{
    private const string FixFoldersTitle = "Fix folder permissions";
    private bool _folderCheckShown;

    /// <summary>The RynthSuite and RynthCore folders, as resolved for this account.</summary>
    private static List<string> SuiteAndCoreDirs() =>
        new[] { RynthInstallPaths.SuiteDir, RynthInstallPaths.CoreDir }
            .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static Task<(List<FolderAccess.FolderStatus> Folders, FolderAccess.CfaInfo Cfa)> CheckFoldersAsync() =>
        Task.Run(() =>
        {
            var folders = FolderAccess.CheckAll(FolderAccess.FoldersToCheck(RynthInstallPaths.SuiteDir, RynthInstallPaths.CoreDir));
            var cfa = FolderAccess.QueryControlledFolderAccess();
            return (folders, cfa);
        });

    /// <summary>The Controlled folder access part of a message ("" when it isn't in the way).</summary>
    private static string DescribeCfa(FolderAccess.CfaInfo cfa)
    {
        var mine = cfa.GameClientBlocks(DateTime.UtcNow);
        if (mine.Count > 0)
        {
            var last = mine[0];
            return $"Windows Security's Controlled folder access blocked the game client {mine.Count} time(s) recently " +
                   $"(last: {last.Time.ToLocalTime():g}, {last.Path}). Permissions can't fix that; allow the client instead:\n" +
                   FolderAccess.CfaAllowHowTo;
        }
        if (cfa.On)
            return "Controlled folder access (Windows Security ransomware protection) is on. If RynthSuite is in a protected " +
                   "folder (Documents, Desktop, or one you added), it blocks acclient.exe from saving there. To allow it: " +
                   FolderAccess.CfaAllowHowTo;
        return string.Empty;
    }

    /// <summary>At start: say so (Activity, log, one dialog) when the client won't be able to save.</summary>
    private async Task CheckFolderAccessAtStartupAsync()
    {
        try
        {
            var (folders, cfa) = await CheckFoldersAsync();
            List<string> problems = FolderAccess.DescribeProblems(folders);
            string cfaText = DescribeCfa(cfa);
            bool cfaBlocks = cfa.BlocksGameClient;
            foreach (string p in problems)
            {
                LauncherDiag.Info("FOLDERS: " + p);
                AppendActivity("Folders: " + p);
            }
            if (cfaText.Length > 0)
            {
                LauncherDiag.Info("FOLDERS: " + cfaText.Replace('\n', ' '));
                if (cfaBlocks || problems.Count > 0) AppendActivity("Folders: " + cfaText.Replace('\n', ' '));
            }
            if ((problems.Count == 0 && !cfaBlocks) || _folderCheckShown) return;
            _folderCheckShown = true;

            if (problems.Count > 0)
            {
                string msg = "RynthAi won't be able to save metas, navs, loot or settings profiles here:\n\n" +
                             string.Join("\n", problems.Take(8)) +
                             (problems.Count > 8 ? $"\n... and {problems.Count - 8} more (Activity tab)." : "") +
                             "\n\nFix folder permissions gives your Windows account write access to the RynthSuite and RynthCore " +
                             "folders and clears read-only on their files (one administrator prompt)." +
                             (cfaText.Length > 0 ? "\n\n" + cfaText : "");
                if (await DecalBridgeDialog.AskAsync(this, FixFoldersTitle, msg, "Fix now", "Not now"))
                    await FixFolderPermissionsAsync(ask: false);
            }
            else
            {
                await DecalBridgeDialog.InfoAsync(this, "Saves blocked by Windows Security", cfaText);
            }
        }
        catch (Exception ex)
        {
            LauncherDiag.Info($"FOLDERS: start-up check failed ({ex.GetType().Name}: {ex.Message})");
        }
    }

    /// <summary>
    /// Recovery Actions > Fix folder permissions: one administrator prompt; the elevated launcher
    /// (--fix-folder-permissions) grants this account Modify on the RynthSuite and RynthCore
    /// folders and clears read-only on their files; then the check runs again.
    /// </summary>
    private async Task FixFolderPermissionsAsync(bool ask = true)
    {
        List<string> roots = SuiteAndCoreDirs();
        if (roots.Count == 0)
        {
            await DecalBridgeDialog.InfoAsync(this, FixFoldersTitle, $"Neither {RynthInstallPaths.SuiteDir} nor {RynthInstallPaths.CoreDir} exists.");
            return;
        }
        if (ask)
        {
            bool yes = await DecalBridgeDialog.AskAsync(this, FixFoldersTitle,
                "Gives your Windows account write access (Modify) to these folders and everything in them, and clears " +
                "read-only on their files, so RynthAi can save metas, navs, loot and settings profiles:\n\n" +
                string.Join("\n", roots) + "\n\nWindows asks for administrator permission once. Nothing else is changed.",
                "Fix", "Cancel");
            if (!yes)
            {
                AppendActivity("Fix folder permissions: cancelled; nothing changed.");
                return;
            }
        }

        string? sid = WindowsIdentity.GetCurrent().User?.Value;
        string? self = Environment.ProcessPath;
        if (sid == null || self == null)
        {
            AppendActivity("Fix folder permissions: couldn't tell who you are or where the launcher is; nothing changed.");
            return;
        }
        string reportPath = Path.Combine(Path.GetTempPath(), $"rynthcore-folderfix-{Guid.NewGuid():N}.txt");
        string args = FolderAccess.BuildRepairArguments(sid, roots, reportPath);
        LauncherDiag.Info($"FOLDERS: fix folder permissions: {args}");
        AppendActivity("Fix folder permissions: waiting for the Windows administrator prompt...");
        (DecalBridgeRegistration.Outcome outcome, string runReport) = await Task.Run(() =>
        {
            var o = DecalBridgeRegistration.RunElevated(self, args, out string r);
            return (o, r);
        });

        string details = string.Empty;
        try
        {
            if (File.Exists(reportPath))
            {
                details = File.ReadAllText(reportPath).Trim();
                File.Delete(reportPath);
            }
        }
        catch { }
        LauncherDiag.Info($"FOLDERS: fix folder permissions: {outcome}: {runReport} {details.Replace(Environment.NewLine, " | ")}");

        var (folders, cfa) = await CheckFoldersAsync();
        List<string> left = FolderAccess.DescribeProblems(folders);
        string cfaText = DescribeCfa(cfa);
        string summary = left.Count == 0
            ? "Done: every RynthSuite / RynthCore folder can be written now."
            : "Still a problem:\n" + string.Join("\n", left.Take(8));
        if (outcome == DecalBridgeRegistration.Outcome.NeedsAdmin)
            summary = "Cancelled at the Windows administrator prompt; nothing changed.";
        AppendActivity("Fix folder permissions: " + summary.Replace('\n', ' '));
        string text = summary +
                      (details.Length > 0 && outcome != DecalBridgeRegistration.Outcome.NeedsAdmin ? "\n\n" + LastLines(details, 6) : "") +
                      (cfaText.Length > 0 ? "\n\n" + cfaText : "") +
                      (left.Count == 0 ? "\n\nRestart the game clients that were running so RynthAi checks again." : "");
        await DecalBridgeDialog.InfoAsync(this, FixFoldersTitle, text);
    }

    private static string LastLines(string text, int n)
    {
        string[] lines = text.Split('\n');
        return string.Join("\n", lines.Skip(Math.Max(0, lines.Length - n)).Select(l => l.TrimEnd('\r')));
    }
}
