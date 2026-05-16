using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using RynthCore.HookResolver.Core.Diff;
using RynthCore.HookResolver.Core.Discovery;

namespace RynthCore.HookResolver.UI;

public sealed class MainViewModel : INotifyPropertyChanged
{
    public const string DefaultAcClientPath = @"C:\Games\RynthCore\AcClient\acclient.exe";
    public const string DefaultManifestPath = @"C:\Games\RynthCore\hooks.json";

    private string _acClientPath = DefaultAcClientPath;
    private string _manifestPath = DefaultManifestPath;
    private string _logPath = EngineLogParser.DefaultLogPath;
    private string _statusText = "Ready.";
    private string _sha256 = "—";
    private int _resolvedCount;
    private int _ambiguousCount;
    private int _unresolvedCount;
    private int _matchCount;
    private int _mismatchCount;
    private int _totalCount;
    private bool _isResolving;
    private bool _canSave;
    private HookRow? _selectedRow;

    public ObservableCollection<HookRow> Hooks { get; } = new();

    public string AcClientPath
    {
        get => _acClientPath;
        set
        {
            if (_acClientPath == value) return;
            _acClientPath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanResolve));
        }
    }

    public string ManifestPath
    {
        get => _manifestPath;
        set { if (_manifestPath != value) { _manifestPath = value; OnPropertyChanged(); } }
    }

    public string LogPath
    {
        get => _logPath;
        set { if (_logPath != value) { _logPath = value; OnPropertyChanged(); } }
    }

    public string StatusText
    {
        get => _statusText;
        set { if (_statusText != value) { _statusText = value; OnPropertyChanged(); } }
    }

    public string Sha256Display
    {
        get => _sha256;
        set { if (_sha256 != value) { _sha256 = value; OnPropertyChanged(); } }
    }

    public string CountsDisplay => _totalCount == 0
        ? ""
        : $"resolved {_resolvedCount}   ambiguous {_ambiguousCount}   unresolved {_unresolvedCount}   ({Hooks.Count}/{_totalCount})";

    public string DiffDisplay => (_matchCount + _mismatchCount) == 0
        ? ""
        : $"vs engine log:   match {_matchCount}   mismatch {_mismatchCount}";

    public bool IsResolving
    {
        get => _isResolving;
        set
        {
            if (_isResolving == value) return;
            _isResolving = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanResolve));
        }
    }

    public bool CanResolve => !_isResolving && File.Exists(_acClientPath);

    public bool CanSave
    {
        get => _canSave;
        set { if (_canSave != value) { _canSave = value; OnPropertyChanged(); } }
    }

    public HookRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (ReferenceEquals(_selectedRow, value)) return;
            _selectedRow = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelection));
        }
    }

    public bool HasSelection => _selectedRow is not null;

    // ── Discovery tab state ──────────────────────────────────────────────

    private string _searchLiteral = "ChatBarEnter";
    private ProloguePreset _selectedPreset = StringDiscoveryService.Presets[0];
    private int _maxBackward = 0x2000;
    private bool _looseMatch;
    private StringReferenceMode _referenceMode = StringReferenceMode.PushImm32;
    private string _discoveryStatus = "Enter a string and click Search.";
    private DiscoveryCandidateRow? _selectedCandidate;
    private bool _isDiscovering;

    public ObservableCollection<DiscoveryCandidateRow> DiscoveryCandidates { get; } = new();
    public IReadOnlyList<ProloguePreset> Presets => StringDiscoveryService.Presets;

    public string SearchLiteral
    {
        get => _searchLiteral;
        set
        {
            if (_searchLiteral == value) return;
            _searchLiteral = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanSearch));
        }
    }

    public ProloguePreset SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (ReferenceEquals(_selectedPreset, value)) return;
            _selectedPreset = value;
            OnPropertyChanged();
        }
    }

    public int MaxBackward
    {
        get => _maxBackward;
        set { if (_maxBackward != value) { _maxBackward = value; OnPropertyChanged(); } }
    }

    public bool LooseMatch
    {
        get => _looseMatch;
        set { if (_looseMatch != value) { _looseMatch = value; OnPropertyChanged(); } }
    }

    public StringReferenceMode[] ReferenceModes { get; } =
        (StringReferenceMode[])System.Enum.GetValues(typeof(StringReferenceMode));

    public StringReferenceMode SelectedReferenceMode
    {
        get => _referenceMode;
        set { if (_referenceMode != value) { _referenceMode = value; OnPropertyChanged(); } }
    }

    public string DiscoveryStatus
    {
        get => _discoveryStatus;
        set { if (_discoveryStatus != value) { _discoveryStatus = value; OnPropertyChanged(); } }
    }

    public DiscoveryCandidateRow? SelectedCandidate
    {
        get => _selectedCandidate;
        set
        {
            if (ReferenceEquals(_selectedCandidate, value)) return;
            _selectedCandidate = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasDiscoverySelection));
        }
    }

    public bool HasDiscoverySelection => _selectedCandidate is not null;

    public bool IsDiscovering
    {
        get => _isDiscovering;
        set
        {
            if (_isDiscovering == value) return;
            _isDiscovering = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanSearch));
        }
    }

    public bool CanSearch => !_isDiscovering && !string.IsNullOrWhiteSpace(_searchLiteral);

    // ── Class/vtable discovery state ─────────────────────────────────────

    private string _searchClass = "gmRadarUI";
    private string _classDiscoveryStatus = "Enter a class name (e.g. gmRadarUI) and click Find.";
    private VtableMethodRow? _selectedVtableMethod;
    private bool _isVtableDiscovering;

    public ObservableCollection<VtableMethodRow> VtableMethods { get; } = new();

    public string SearchClass
    {
        get => _searchClass;
        set
        {
            if (_searchClass == value) return;
            _searchClass = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanSearchClass));
        }
    }

    public string ClassDiscoveryStatus
    {
        get => _classDiscoveryStatus;
        set { if (_classDiscoveryStatus != value) { _classDiscoveryStatus = value; OnPropertyChanged(); } }
    }

    public VtableMethodRow? SelectedVtableMethod
    {
        get => _selectedVtableMethod;
        set
        {
            if (ReferenceEquals(_selectedVtableMethod, value)) return;
            _selectedVtableMethod = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasVtableSelection));
        }
    }

    public bool HasVtableSelection => _selectedVtableMethod is not null;

    public bool IsVtableDiscovering
    {
        get => _isVtableDiscovering;
        set
        {
            if (_isVtableDiscovering == value) return;
            _isVtableDiscovering = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanSearchClass));
        }
    }

    public bool CanSearchClass => !_isVtableDiscovering && !string.IsNullOrWhiteSpace(_searchClass);

    public void BeginResolve(int totalSymbols)
    {
        _resolvedCount = _ambiguousCount = _unresolvedCount = 0;
        _matchCount = _mismatchCount = 0;
        _totalCount = totalSymbols;
        Hooks.Clear();
        CanSave = false;
        IsResolving = true;
        StatusText = $"Resolving {totalSymbols} symbol(s)…";
        Sha256Display = "—";
        OnPropertyChanged(nameof(CountsDisplay));
        OnPropertyChanged(nameof(DiffDisplay));
    }

    public void AddRow(HookRow row)
    {
        Hooks.Add(row);
        switch (row.Entry.Status)
        {
            case RynthCore.HookManifest.Schema.ResolutionStatus.Resolved: _resolvedCount++; break;
            case RynthCore.HookManifest.Schema.ResolutionStatus.Ambiguous: _ambiguousCount++; break;
            case RynthCore.HookManifest.Schema.ResolutionStatus.Unresolved: _unresolvedCount++; break;
        }
        switch (row.DiffSymbol)
        {
            case "✓": _matchCount++; break;
            case "✗": _mismatchCount++; break;
        }
        OnPropertyChanged(nameof(CountsDisplay));
        OnPropertyChanged(nameof(DiffDisplay));
    }

    public void EndResolve(bool success, string message, string? sha256 = null)
    {
        IsResolving = false;
        StatusText = message;
        if (sha256 is not null) Sha256Display = sha256;
        CanSave = success && Hooks.Count > 0;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
