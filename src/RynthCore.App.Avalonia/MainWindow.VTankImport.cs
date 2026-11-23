using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using RynthCore.Install;
using static RynthCore.App.VTankFileImport;

namespace RynthCore.App.Avalonia;

/// <summary>
/// Runtime > "Coming from VirindiTank" > Import VirindiTank files: copies the player's VTank metas,
/// navs and loot profiles into RynthAi's folders. The search, the plan and the copy are
/// src\RynthCore.App\VTankFileImport.cs (shared with the installer's offer, tested by
/// tools\VTankFileImportTests); this is only the questions and the messages.
/// </summary>
internal partial class MainWindow
{
    private const string VTankImportTitle = "Import VirindiTank files";
    private bool _vtankImportBusy;

    private enum VTankChoice { Copy, KeepBoth, PickFolder, Cancel }

    private async Task ImportVTankFilesAsync()
    {
        if (_vtankImportBusy) return;
        _vtankImportBusy = true;
        ImportVTankFilesButton.IsEnabled = false;
        try
        {
            await ImportVTankFilesCoreAsync();
        }
        catch (Exception ex)
        {
            LauncherDiag.Info($"VTANKIMPORT: failed ({ex.GetType().Name}: {ex.Message})");
            AppendActivity($"Import VirindiTank files: failed ({ex.Message}).");
            await DecalBridgeDialog.InfoAsync(this, VTankImportTitle, $"The import stopped: {ex.Message}\n\nNothing in your VirindiTank folder was changed.");
        }
        finally
        {
            _vtankImportBusy = false;
            ImportVTankFilesButton.IsEnabled = true;
        }
    }

    private async Task ImportVTankFilesCoreAsync()
    {
        string ai = RynthInstallPaths.RynthAiDir;
        if (!Directory.Exists(ai))
        {
            AppendActivity($"Import VirindiTank files: RynthAi's folder {ai} doesn't exist; nothing copied.");
            await DecalBridgeDialog.InfoAsync(this, VTankImportTitle,
                $"RynthAi isn't installed here yet: {ai} doesn't exist. Install RynthSuite first, then import.");
            return;
        }

        ScanResult scan = await ScanVTankAsync(null, ai);
        ConflictMode mode;
        while (true)
        {
            if (scan.TotalFound == 0)
            {
                string why = scan.Roots.Count == 0
                    ? "RynthCore didn't find a VirindiTank folder in the usual places (Decal's plugin list, " +
                      @"C:\Games\VirindiPlugins\VirindiTank, Documents, Program Files)."
                    : "There are no VirindiTank metas (.met, .af), nav routes (.nav) or loot profiles (.utl) in " +
                      string.Join(", ", scan.Roots) + ".";
                int pick = await DecalBridgeDialog.ChooseAsync(this, VTankImportTitle,
                    why + "\n\nChoose the folder VirindiTank is installed in?", "Choose folder...", "Cancel");
                if (pick != 0 || !await RescanPickedFolderAsync(ai, s => scan = s))
                {
                    AppendActivity("Import VirindiTank files: cancelled; nothing copied.");
                    return;
                }
                continue;
            }

            var choices = new List<(string Label, VTankChoice Choice)>();
            if (scan.NewCount > 0) choices.Add(("Copy", VTankChoice.Copy));
            if (scan.ConflictCount > 0) choices.Add(("Copy, keep both", VTankChoice.KeepBoth));
            choices.Add(("Choose another folder...", VTankChoice.PickFolder));
            choices.Add((choices.Count == 1 ? "Close" : "Cancel", VTankChoice.Cancel));

            string plan = DescribePlan(scan);
            if (scan.NewCount == 0 && scan.ConflictCount == 0)
                plan += "\n\nEverything here is already in RynthAi.";
            int i = await DecalBridgeDialog.ChooseAsync(this, VTankImportTitle, plan, choices.Select(c => c.Label).ToArray());
            VTankChoice choice = i >= 0 && i < choices.Count ? choices[i].Choice : VTankChoice.Cancel;
            if (choice == VTankChoice.PickFolder)
            {
                if (!await RescanPickedFolderAsync(ai, s => scan = s))
                {
                    AppendActivity("Import VirindiTank files: cancelled; nothing copied.");
                    return;
                }
                continue;
            }
            if (choice == VTankChoice.Cancel)
            {
                AppendActivity("Import VirindiTank files: cancelled; nothing copied.");
                return;
            }
            mode = choice == VTankChoice.KeepBoth ? ConflictMode.KeepBoth : ConflictMode.Skip;
            break;
        }

        AppendActivity($"Import VirindiTank files: copying from {string.Join(", ", scan.Roots)}{(mode == ConflictMode.KeepBoth ? " (keep both)" : "")}...");
        CopyResult result = await Task.Run(() => Copy(scan, mode));
        foreach (string line in result.Log) LauncherDiag.Info("VTANKIMPORT: " + line);
        string summary = $"{result.Copied} copied ({result.KeptBoth} kept both), {result.Skipped} skipped, " +
                         $"{result.AlreadyThere} already there, {result.Failed} failed.";
        LauncherDiag.Info("VTANKIMPORT: (launcher) " + summary);
        AppendActivity("Import VirindiTank files: " + summary);
        await DecalBridgeDialog.InfoAsync(this, VTankImportTitle, DescribeResult(result, ai));
    }

    /// <summary>Scans <paramref name="roots"/> (null = this PC's usual VTank folders) off the UI thread; logs it.</summary>
    private static async Task<ScanResult> ScanVTankAsync(IEnumerable<string>? roots, string ai)
    {
        ScanResult scan = await Task.Run(() => Find(roots ?? DefaultRoots(), ai));
        foreach (string r in scan.Roots) LauncherDiag.Info("VTANKIMPORT: looked in " + r);
        foreach (string line in scan.Log) LauncherDiag.Info("VTANKIMPORT: " + line);
        LauncherDiag.Info($"VTANKIMPORT: scan: {scan.TotalFound} found, {scan.NewCount} new, {scan.ConflictCount} name conflicts, " +
                          $"{scan.SameCount} already there, {scan.StateFilesLeftOut} VTank state files left out.");
        return scan;
    }

    /// <summary>Folder picker, then a scan of that folder. False when the player cancelled the picker.</summary>
    private async Task<bool> RescanPickedFolderAsync(string ai, Action<ScanResult> set)
    {
        string? picked = await PickVTankFolderAsync();
        if (picked == null) return false;
        string root = ResolvePickedFolder(picked);
        set(await ScanVTankAsync(new[] { root }, ai));
        return true;
    }

    private async Task<string?> PickVTankFolderAsync()
    {
        var topLevel = GetTopLevel(this);
        if (topLevel == null) return null;
        IStorageFolder? start = null;
        try
        {
            string guess = Directory.Exists(@"C:\Games\VirindiPlugins") ? @"C:\Games\VirindiPlugins" : @"C:\Games";
            if (Directory.Exists(guess)) start = await topLevel.StorageProvider.TryGetFolderFromPathAsync(guess);
        }
        catch { }
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose your VirindiTank folder",
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        if (folders.Count == 0) return null;
        string? path = folders[0].TryGetLocalPath();
        return string.IsNullOrEmpty(path) ? null : path;
    }
}
