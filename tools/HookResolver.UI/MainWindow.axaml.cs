using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RynthCore.HookManifest.Schema;
using RynthCore.HookResolver.Core.Diff;
using RynthCore.HookResolver.Core.Discovery;
using RynthCore.HookResolver.Core.Offline;
using RynthCore.HookResolver.Core.Resolution;
using RynthCore.HookResolver.Core.Symbols;
using Manifest = RynthCore.HookManifest.Schema.HookManifest;

namespace RynthCore.HookResolver.UI;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly List<HookManifestEntry> _promotedEntries = new();
    private Manifest? _lastManifest;
    private AcClientImage? _cachedImage;
    private CancellationTokenSource? _cts;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    private async void OnBrowseAcClient(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose acclient.exe",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("AC client executable") { Patterns = ["acclient.exe", "*.exe"] }
            ]
        });
        if (files.Count > 0)
        {
            string? path = files[0].TryGetLocalPath();
            if (!string.IsNullOrEmpty(path)) _vm.AcClientPath = path;
        }
    }

    private async void OnBrowseLog(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose RynthCore.log",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("RynthCore log") { Patterns = ["*.log", "*.*"] }
            ]
        });
        if (files.Count > 0)
        {
            string? path = files[0].TryGetLocalPath();
            if (!string.IsNullOrEmpty(path)) _vm.LogPath = path;
        }
    }

    private async void OnResolveClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var symbols = AllSymbols.Default;
        _vm.BeginResolve(symbols.Count);

        AcClientImage? image = null;
        try
        {
            image = await AcClientImageLoader.LoadAsync(_vm.AcClientPath, ct);
            _cachedImage = image;
            _vm.Sha256Display = image.Input.Sha256;
        }
        catch (Exception ex)
        {
            _vm.EndResolve(false, $"Load failed: {ex.Message}");
            return;
        }

        IReadOnlyDictionary<string, EngineResolution> engineLog;
        try
        {
            engineLog = await EngineLogParser.ParseAsync(_vm.LogPath, ct);
        }
        catch (Exception ex)
        {
            engineLog = new Dictionary<string, EngineResolution>();
            _vm.StatusText = $"Log parse warning: {ex.Message}";
        }

        try
        {
            await foreach (var entry in HookResolveOrchestrator.ResolveAsync(image, symbols, ct))
            {
                engineLog.TryGetValue(entry.SymbolId, out var engineRes);
                await Dispatcher.UIThread.InvokeAsync(() =>
                    _vm.AddRow(new HookRow(entry, engineRes)));
            }

            // Re-attach anything promoted from the Discover tabs so a re-resolve
            // doesn't drop it.
            foreach (var promoted in _promotedEntries)
                _vm.AddRow(new HookRow(promoted, null));

            _lastManifest = ManifestBuilder.Compose(image.Input, BuildEntries());
            string logNote = engineLog.Count > 0
                ? $" • {engineLog.Count} engine log entries compared."
                : " • (no engine log found at the configured path)";
            _vm.EndResolve(true, $"Done. Manifest ready ({image.Input.Sha256[..12]}…).{logNote}");
        }
        catch (OperationCanceledException)
        {
            _vm.EndResolve(false, "Cancelled.");
        }
        catch (Exception ex)
        {
            _vm.EndResolve(false, $"Resolve failed: {ex.Message}");
        }
    }

    private IEnumerable<HookManifestEntry> BuildEntries()
    {
        foreach (var row in _vm.Hooks)
            yield return row.Entry;
    }

    private void PromoteEntry(string symbolId, int va, int rva, FunctionSnapshot snap, string reason)
    {
        if (_promotedEntries.Exists(e => e.Address?.Va == va))
        {
            _vm.StatusText = $"Already promoted: 0x{va:X8}.";
            return;
        }

        var entry = new HookManifestEntry
        {
            SymbolId = symbolId,
            Status = ResolutionStatus.Resolved,
            Address = new ResolvedAddress(va, rva),
            PatternId = "promoted",
            MatchedCandidates = 1,
            ChosenCandidateReason = reason,
            Snapshot = snap
        };

        _promotedEntries.Add(entry);
        _vm.AddRow(new HookRow(entry, null));

        if (_cachedImage is not null)
        {
            _lastManifest = ManifestBuilder.Compose(_cachedImage.Input, BuildEntries());
            _vm.CanSave = true;
        }

        _vm.StatusText = $"Promoted {symbolId} → manifest now has {_vm.Hooks.Count} entries (Save to write).";
    }

    private void OnPromoteStringCandidate(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Control { DataContext: DiscoveryCandidateRow row })
        {
            string literal = _vm.SearchLiteral.Trim();
            var c = row.Candidate;
            PromoteEntry(
                $"discovered.str:{literal}@0x{c.Va:X8}",
                c.Va, c.Rva, c.Snapshot,
                $"promoted from string-ref '{literal}' (push @ 0x{c.PushSiteVa:X8})");
        }
    }

    private void OnPromoteVtableMethod(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Control { DataContext: VtableMethodRow row })
        {
            string cls = _vm.SearchClass.Trim();
            var c = row.Candidate;
            int rva = c.Va - (_cachedImage?.View.ImageBase ?? 0x00400000);
            PromoteEntry(
                $"discovered.vt:{cls}[{c.SlotIndex}]@0x{c.Va:X8}",
                c.Va, rva, c.Snapshot,
                $"promoted from {cls} vtable slot {c.SlotIndex} (vtable 0x{c.VtableVa:X8})");
        }
    }

    private async void OnSearchClassClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _vm.IsVtableDiscovering = true;
        _vm.VtableMethods.Clear();
        _vm.SelectedVtableMethod = null;
        _vm.ClassDiscoveryStatus = $"Loading acclient.exe and walking RTTI for '{_vm.SearchClass}'…";

        try
        {
            if (_cachedImage is null || _cachedImage.Input.Path != _vm.AcClientPath)
            {
                _cachedImage = await AcClientImageLoader.LoadAsync(_vm.AcClientPath);
                _vm.Sha256Display = _cachedImage.Input.Sha256;
            }

            string className = _vm.SearchClass.Trim();
            var result = await Task.Run(() => VtableDiscoveryService.Discover(_cachedImage!, className));

            _vm.ClassDiscoveryStatus = result.Summary;
            foreach (var method in result.Methods)
                _vm.VtableMethods.Add(new VtableMethodRow(method));
        }
        catch (Exception ex)
        {
            _vm.ClassDiscoveryStatus = $"Search failed: {ex.Message}";
        }
        finally
        {
            _vm.IsVtableDiscovering = false;
        }
    }

    private async void OnSearchClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _vm.IsDiscovering = true;
        _vm.DiscoveryCandidates.Clear();
        _vm.SelectedCandidate = null;
        _vm.DiscoveryStatus = $"Loading acclient.exe and searching for '{_vm.SearchLiteral}'…";

        try
        {
            if (_cachedImage is null || _cachedImage.Input.Path != _vm.AcClientPath)
            {
                _cachedImage = await AcClientImageLoader.LoadAsync(_vm.AcClientPath);
                _vm.Sha256Display = _cachedImage.Input.Sha256;
            }

            var request = new StringDiscoveryRequest(
                _vm.SearchLiteral.Trim(),
                _vm.SelectedPreset.Bytes,
                _vm.MaxBackward,
                _vm.LooseMatch,
                _vm.SelectedReferenceMode);

            var result = await Task.Run(() => StringDiscoveryService.Discover(_cachedImage!, request));

            _vm.DiscoveryStatus = result.Summary;
            foreach (var candidate in result.Candidates)
                _vm.DiscoveryCandidates.Add(new DiscoveryCandidateRow(candidate));
        }
        catch (Exception ex)
        {
            _vm.DiscoveryStatus = $"Search failed: {ex.Message}";
        }
        finally
        {
            _vm.IsDiscovering = false;
        }
    }

    private async void OnSaveClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_lastManifest is null)
        {
            _vm.StatusText = "Nothing to save — run Resolve first.";
            return;
        }

        try
        {
            await ManifestBuilder.WriteAsync(_lastManifest, _vm.ManifestPath);
            _vm.StatusText = $"Saved → {_vm.ManifestPath}";
        }
        catch (Exception ex)
        {
            _vm.StatusText = $"Save failed: {ex.Message}";
        }
    }
}
