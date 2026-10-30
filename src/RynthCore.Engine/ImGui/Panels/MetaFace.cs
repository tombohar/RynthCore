// ============================================================================
//  RynthCore.Engine - ImGui/Panels/MetaFace.cs
//  ImGui face of the Meta panel (docs/IMGUI_PARITY_PLAN.md §2.13), with the
//  Avalonia face's look and three views:
//
//   List    (MetaFace.List.cs) file picker / refresh / Save As / "Saved"
//           indicator; Visual|Source; Meta, Debug; a filter; rules grouped
//           by state, each rule in full (every condition and action on its
//           own line, or one line in Compact) with lines between rules and
//           columns; move / duplicate / delete on the hovered row and on
//           right-click; click a row to edit it; State picker and New Rule
//           at the bottom.
//   Editor  (MetaFace.Editor.cs) state; Conditions and Actions side by side
//           (stacked when the panel is narrow), each with "+ Add"; Set current
//           for landblock / landcell; every change saves on its own.
//   Source  the .af text in a coloured editor (CodeEditor) with Ctrl+Space
//           completion (and after "IF: " etc.); Apply shows the plugin's real
//           result; unapplied edits survive switching views; Revert.
//
//  Every state picker ends with "+ New state...", which turns it into a name
//  box (Enter = OK, Esc = Cancel); see MetaFace.Editor.cs for how a new state
//  is kept (a state exists while a rule uses it).
//
//  Saving (2026-10-05): there is no Save button. RynthAi writes the meta file
//  after every rule command (TryAutoSaveMetaCmd); the editor sends its rule
//  0.75 s after the last change (MetaAutosave), and at once on Back, close or
//  a meta switch. The first save of a session first copies the file to
//  <file>.bak (MetaBackupPlan, MetaCommands.Backup).
//
//  Data: UiSources.Meta (hub snapshot) and MetaCommands. List edits change a
//  private copy at once and the next snapshot (taken after the plugin applied
//  the command) replaces it.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using ImGuiNET;
using RynthCore.Engine.UI.Data;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed partial class MetaFace : IImGuiPanel
{
    /// <summary>A large face that changes on its own only when the meta state moves: 4 Hz idle when popped out.</summary>
    public int PopOutIdleHz => 4;

    public const string Title = "Meta";

    public static void Register() => ImGuiPanelHost.Register(Title,
        new PanelSpec(new Vector2(440, 540), new Vector2(380, 300), EdgeToEdge: true, GripInBody: true),
        () => new MetaFace());

    // ── Palette (MetaPanel's, ARGB) ─────────────────────────────────────
    private static readonly uint Green = C(0xFF33CC66), Red = C(0xFFE04848), Fired = C(0xFF3A1212),
        BtnOn = C(0xFF0E2E3A), StateHdrA = C(0xFF0E1E30), DelFill = C(0xFF7A1414), NewFill = C(0xFF0E2E1A);
    private static uint Teal => RynthTheme.Accent;
    private static uint Amber => RynthTheme.Highlight;
    private static uint Mute => RynthTheme.MuteAlt;
    private static uint TextDim => RynthTheme.Text;
    private static uint ShellBg => RynthTheme.Background;
    private static uint PanelBg => RynthTheme.Field;
    private static uint RowAlt => RynthTheme.RowAlt;
    private static uint BtnFill => RynthTheme.Button;
    private static uint BtnBord => RynthTheme.Border;
    private static uint PickSel => RynthTheme.Selected;
    private static uint C(uint argb) => RynthTheme.Argb(argb);
    private static uint Faded(uint abgr) => (abgr & 0x00FFFFFF) | ((abgr >> 25) << 24);   // half alpha

    private enum View { List, Editor, Source, Schedule }

    // ── Snapshot copy + caches rebuilt when it changes ───────────────────
    private MetaPayload _data = new();
    private long _seenVersion = -1;
    private List<(string State, List<int> Rules)> _groups = new();
    private bool[] _groupFiring = Array.Empty<bool>();
    private string[] _statePicks = Array.Empty<string>(), _statesOnly = Array.Empty<string>();
    private string[] _routeLabels = Array.Empty<string>(), _routeValues = Array.Empty<string>(), _fileDisplays = Array.Empty<string>();
    private string _fileDisplay = "-- None --", _sourceFileLabel = "File: Unsaved";
    private IReadOnlyList<string> _completionNavs = Array.Empty<string>();

    // Ellipsized text per (text, width), so one text shown at two widths doesn't recompute each frame.
    private readonly Dictionary<(string Text, int Width), string> _fit = new();

    private View _view = View.List;
    private readonly MetaStateClock _stateClock = new();
    private readonly HashSet<string> _collapsed = new();
    private bool _saveAs, _saveAsFocus;
    private readonly byte[] _saveName = new byte[128];

    // States made with "+ New state..." that the snapshot doesn't list yet (MetaFace.Editor.cs).
    private readonly HashSet<string> _localStates = new(StringComparer.OrdinalIgnoreCase);
    private string _statesPath = string.Empty;   // the meta _localStates belong to

    // Delete on a rule row: the first click arms it (3 s), the second deletes.
    private MetaRuleDto? _confirmDelete;
    private int _confirmDeleteIndex = -1;
    private long _confirmDeleteUntil;

    // ── Saving ───────────────────────────────────────────────────────────
    private readonly MetaAutosave _autosave = new();
    private readonly MetaBackupPlan _backup = new();
    private long _saveSentAt = -1;        // ms (TickCount64) of the last rule command, for "Saving..."
    private long _saveSentVersion;        // the snapshot version then
    private static long NowMs => Environment.TickCount64;

    // ── Rule editor ──────────────────────────────────────────────────────
    private int _editIndex = -1;
    private MetaRuleDto _editRule = new() { Action = 1 };
    private sealed class FieldBuffer
    {
        public readonly byte[] Bytes = new byte[2048];
        public string? Shown;
        public bool Active;     // being typed in (last frame)
    }
    private readonly Dictionary<(MetaRuleDto Rule, bool Action), FieldBuffer> _fields = new();

    // ── Source ───────────────────────────────────────────────────────────
    private CodeEditor? _editor;
    private string _sourceBaseline = string.Empty;   // snapshot text the editor was last loaded with
    private bool _sourceStale;                        // the meta changed under unapplied edits
    private (int Line, int Column) _cursor = (-1, -1);
    private string _cursorLabel = string.Empty;
    private uint _lastChange;
    private long _applySeqBefore = -2;                // -2: no apply pending
    private DateTime _applySentAt, _msgAt;
    private string _msg = string.Empty;
    private bool _msgOk = true;

    // Completion list.
    private enum SuggestionKind { Struct, Cond, Action, State, Nav }
    private readonly List<(string Text, SuggestionKind Kind)> _compItems = new();
    private readonly List<int> _compShown = new();
    private bool _compOpen;
    private int _compSel, _compTop, _compLine;
    private string _compAnchor = string.Empty;         // the line up to where the completed word starts
    private string _compWord = string.Empty;
    private const int CompRows = 10;

    public void OnShown()
    {
        UiSources.Meta.Subscribe();
        UiSources.Meta.RequestRefresh();
        CodeEditor.SetMetaVocabulary(MetaVocabulary.StructKeywords, MetaVocabulary.ConditionKeywords, MetaVocabulary.ActionKeywords);
        _backup.NewSession();   // a new editing session: the first save backs the file up again
    }

    public void OnHidden()
    {
        FlushEdit();            // closing saves the rule being edited at once
        CancelNaming();
        UiSources.Meta.Unsubscribe();
        _editor?.Dispose();
        _editor = null;
    }

    // =====================================================================
    //  Frame
    // =====================================================================

    public void Draw()
    {
        TakeSnapshot();
        if (_view == View.Editor) EditorGuard();
        // A new rule waits until it is filled in (MetaRuleEdit.ReadyToAdd); Back / close add it anyway.
        if (_view == View.Editor && _autosave.Due(NowMs, _editTyping) && (_editIndex >= 0 || MetaRuleEdit.ReadyToAdd(_editRule)))
            CommitEdit();

        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        Vector2 origin = ImGuiNET.ImGui.GetCursorScreenPos();
        Vector2 size = ImGuiNET.ImGui.GetContentRegionAvail();
        dl.AddRectFilled(origin, origin + size, ShellBg, 4);

        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui10));
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4, 4));
        Vector2 inner = origin + new Vector2(6, 6);
        Vector2 innerSize = size - new Vector2(12, 12);
        // The body has no padding (EdgeToEdge): indent so every new line, not
        // just the first, starts 6 px in from the border.
        ImGuiNET.ImGui.SetCursorScreenPos(inner);
        ImGuiNET.ImGui.Indent(6);
        switch (_view)
        {
            case View.List: ListView(inner, innerSize); break;
            // The editor scrolls: it ends above the resize grip (GripInBody).
            case View.Editor: EditorView(innerSize - new Vector2(0, FaceKit.GripOverlap())); break;
            case View.Source: SourceView(inner, innerSize); break;
            case View.Schedule: ScheduleView(inner, innerSize); break;
        }
        ImGuiNET.ImGui.Unindent(6);
        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.PopFont();

        dl.AddRect(origin, origin + size, BtnBord, 4);
    }

    private void TakeSnapshot()
    {
        var snap = UiSources.Meta.Current;
        if (snap == null || snap.Version == _seenVersion) return;
        _seenVersion = snap.Version;
        MetaPayload fresh = snap.Value;
        _stateClock.Snapshot(fresh.Snapshot, NowMs);
        CheckApplyResult(fresh);
        _data = fresh.Clone();
        RebuildCaches();

        // Keep the source editor on the plugin's text unless it has unapplied edits.
        if (_editor != null && fresh.SourceText != _sourceBaseline)
        {
            if (_editor.IsDirty) _sourceStale = true;
            else LoadSource(fresh.SourceText);
        }
    }

    private void RebuildCaches()
    {
        List<MetaRuleDto> rules = _data.Rules;
        _groups = MetaVocabulary.GroupByState(rules);
        _groupFiring = new bool[_groups.Count];
        for (int g = 0; g < _groups.Count; g++)
        {
            bool firing = false;
            foreach (int i in _groups[g].Rules) firing |= rules[i].LastFiredMs < 1500;
            _groupFiring[g] = firing;
        }
        // States made here that the snapshot doesn't list yet (dropped once it does, or on a meta switch).
        if (!string.Equals(_statesPath, _data.CurrentMetaPath, StringComparison.OrdinalIgnoreCase))
        {
            _statesPath = _data.CurrentMetaPath;
            _localStates.Clear();
        }
        foreach (string s in _data.States) _localStates.Remove(s);
        _statePicks = MetaVocabulary.StatePicks(_data);
        _statesOnly = _data.States.ToArray();
        foreach (string s in _localStates)
        {
            _statePicks = With(_statePicks, s);
            _statesOnly = With(_statesOnly, s);
        }
        (_routeLabels, _routeValues) = MetaVocabulary.RoutePicks(_data);
        _fileDisplays = new string[_data.Files.Count];
        for (int i = 0; i < _fileDisplays.Length; i++) _fileDisplays[i] = _data.Files[i].Display;
        _fileDisplay = MetaVocabulary.CurrentFileDisplay(_data);
        _sourceFileLabel = "File: " + (string.IsNullOrEmpty(_data.CurrentMetaPath) ? "Unsaved" : System.IO.Path.GetFileName(_data.CurrentMetaPath));
        _completionNavs = MetaVocabulary.CompletionNavs(_data);
        _fit.Clear();
        RebuildListText(rules);   // after the states: a Set State to a missing state shows in red
    }

    // =====================================================================
    //  File bar and mode row (the list itself: MetaFace.List.cs)
    // =====================================================================

    private void LoadSaveBar(float w)
    {
        if (_saveAs)
        {
            float btn = BtnW(SaveLabel, 52), sx = ImGuiNET.ImGui.GetCursorScreenPos().X, y = ImGuiNET.ImGui.GetCursorScreenPos().Y;
            float boxW = w - btn - 26 - 8;
            if (_saveAsFocus) { ImGuiNET.ImGui.SetKeyboardFocusHere(); _saveAsFocus = false; }
            bool enter = TextBox("##save_name", _saveName, boxW, "Filename (no ext)...", ImGuiInputTextFlags.EnterReturnsTrue, out _);
            if (Button(SaveLabel + "##save_as_go", new Vector2(sx + boxW + 4, y), new Vector2(btn, 22), TextDim, BtnFill, BtnBord) || enter)
            {
                string n = Utf8(_saveName).Trim();
                if (n.Length > 0)
                {
                    string target = System.IO.Path.Combine(MetaVocabulary.MetaFolder, n + ".af");
                    // Saving over another meta keeps that one's old copy as <file>.bak.
                    MetaCommands.Backup(target, MetaBackupPlan.BackupOf(target));
                    MetaCommands.Simple("save_file", path: target);
                    _saveSentAt = NowMs;
                    _saveSentVersion = _seenVersion;
                }
                _saveAs = false;
            }
            if (IconButton("##save_as_x", PhosphorIcons.X, new Vector2(sx + boxW + 8 + btn, y), new Vector2(26, 22), TextDim, BtnFill, BtnBord,
                    font: UiFont.Ui14))
                _saveAs = false;
            ImGuiNET.ImGui.SetItemTooltip("Cancel");
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(sx, y + 22));
            ImGuiNET.ImGui.Dummy(new Vector2(0, 0));
            return;
        }

        // [file v][refresh][Save As]  Saved   (no Save button: every change saves on its own)
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        const float refreshW = 26;
        float saveAsW = BtnW("Save As", 60), indW = SaveIndicatorWidth();
        float pickW = Math.Max(60, w - refreshW - saveAsW - indW - 12);
        int picked = Picker("##file", _fileDisplay, _fileDisplays, MetaVocabulary.CurrentFileIndex(_data), p, pickW);
        if (picked >= 0 && picked < _data.Files.Count)
        {
            FlushEdit();   // a meta switch saves a pending edit first
            MetaCommands.Simple("load_file", path: _data.Files[picked].Path);
        }

        float x = p.X + pickW + 4;
        if (IconButton("##refresh", PhosphorIcons.ArrowsClockwise, new Vector2(x, p.Y), new Vector2(refreshW, 22), TextDim, BtnFill, BtnBord,
                font: UiFont.Ui14))
            UiSources.Meta.RequestRefresh();
        ImGuiNET.ImGui.SetItemTooltip("Refresh file list");
        x += refreshW + 4;
        bool noFile = string.IsNullOrEmpty(_data.CurrentMetaPath);
        if (Button("Save As##save_as", new Vector2(x, p.Y), new Vector2(saveAsW, 22), TextDim, noFile ? NewFill : BtnFill, noFile ? Green : BtnBord))
            BeginSaveAs();
        SafeImGui.SetItemTooltip(noFile ? "Name a file for this meta; after that every change saves on its own."
            : "Save a copy under a new name (and keep editing that one).");
        x += saveAsW + 4;
        SaveIndicator(new Vector2(x, p.Y), p.X + w - x, 22);
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + 22));
        ImGuiNET.ImGui.Dummy(new Vector2(0, 0));
    }

    // ── "Saved" indicator ────────────────────────────────────────────────

    private const string SavedText = PhosphorIcons.Check + " Saved", SavingText = PhosphorIcons.FloppyDisk + " Saving...",
        EditingText = PhosphorIcons.PencilSimple + " Editing", NoFileText = PhosphorIcons.Warning + " Not saved",
        DraftText = PhosphorIcons.PencilSimple + " Not added yet";

    private static float SaveIndicatorWidth(bool editor = false)
    {
        float m = Math.Max(ImGuiNET.ImGui.CalcTextSize(SavedText).X, ImGuiNET.ImGui.CalcTextSize(SavingText).X);
        m = Math.Max(m, Math.Max(ImGuiNET.ImGui.CalcTextSize(EditingText).X, ImGuiNET.ImGui.CalcTextSize(NoFileText).X));
        if (editor) m = Math.Max(m, ImGuiNET.ImGui.CalcTextSize(DraftText).X);
        return m + 6;
    }

    private (string Text, uint Color, string Tip) SaveStatus()
    {
        string file = System.IO.Path.GetFileName(_data.CurrentMetaPath ?? "");
        if (string.IsNullOrEmpty(file))
            return (NoFileText, Amber, "This meta has no file yet. Save As names one; after that every change saves on its own.");
        if (_view == View.Editor && _autosave.Dirty && _editIndex < 0 && !_editTyping && !MetaRuleEdit.ReadyToAdd(_editRule))
            return (DraftText, Amber, "A new rule is added once its condition and action are filled in, or when you go Back.");
        if (_view == View.Editor && _autosave.Dirty)
            return _editTyping
                ? (EditingText, Amber, "Saves when you leave the box (Enter, Tab or click elsewhere).")
                : (SavingText, Amber, "Saving in a moment...");
        if (_saveSentAt >= 0 && _seenVersion == _saveSentVersion && NowMs - _saveSentAt < 2500)
            return (SavingText, Amber, "Saving " + file + "...");
        string bak = System.IO.Path.GetFileName(MetaBackupPlan.BackupOf(MetaBackupPlan.WrittenFile(_data.CurrentMetaPath!)));
        return (SavedText, Green, $"Every change saves to {file} on its own. The first change after opening this panel keeps the file as it was in {bak}.");
    }

    /// <summary>The save status, right-aligned in (pos, width); hover for details.</summary>
    private void SaveIndicator(Vector2 pos, float width, float height)
    {
        if (width < 8) return;
        (string text, uint color, string tip) = SaveStatus();
        float tw = ImGuiNET.ImGui.CalcTextSize(text).X;
        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        ImGuiNET.ImGui.InvisibleButton("##save_ind", new Vector2(width, height));
        SafeImGui.SetItemTooltip(tip);
        ImGuiNET.ImGui.GetWindowDrawList().AddText(new Vector2(pos.X + Math.Max(0, width - tw), pos.Y + (height - ImGuiNET.ImGui.GetFontSize()) * 0.5f),
            color, text);
    }

    /// <summary>
    /// Every command that changes the meta's rules goes through here: before the first one of this
    /// session the file is backed up, and the indicator shows "Saving..." until the next snapshot.
    /// (RynthAi writes the file after each such command.)
    /// </summary>
    private void SendEdit(MetaCmd cmd)
    {
        if (_backup.Before(_data.CurrentMetaPath) is { } b) MetaCommands.Backup(b.File, b.Backup);
        MetaCommands.Send(cmd);
        _saveSentAt = NowMs;
        _saveSentVersion = _seenVersion;
    }

    private void BeginSaveAs()
    {
        _saveAs = true;
        _saveAsFocus = true;
        Array.Clear(_saveName);
    }

    // [Visual][Source][Schedule] | [x Meta][x Debug] (check-box icons; the toggles only in the list view)
    private void ModeRow(float w, bool source, bool schedule = false)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        float x = p.X;
        if (Toggle("Visual##mode_v", ref x, p.Y, !source && !schedule)) ShowList();
        if (Toggle("Source##mode_s", ref x, p.Y, source)) ShowSource();
        if (Toggle(ScheduleLabel, ref x, p.Y, schedule)) ShowSchedule();

        if (!source && !schedule)
        {
            ImGuiNET.ImGui.GetWindowDrawList().AddLine(new Vector2(x + 2, p.Y + 2), new Vector2(x + 2, p.Y + 20), BtnBord);
            x += 8;
            if (Toggle(_data.EnableMeta ? MetaOn : MetaOff, ref x, p.Y, _data.EnableMeta))
            {
                _data.EnableMeta = !_data.EnableMeta;
                MetaCommands.Simple("set_enabled", value: _data.EnableMeta ? "true" : "false");
            }
            if (Toggle(_data.MetaDebug ? DebugOn : DebugOff, ref x, p.Y, _data.MetaDebug))
            {
                _data.MetaDebug = !_data.MetaDebug;
                MetaCommands.Simple("set_debug", value: _data.MetaDebug ? "true" : "false");
            }
        }
        else if (source && _editor != null)
        {
            // Ln/Col and the unapplied-edits marker, right-aligned.
            string right = _editor.IsDirty ? "● unapplied   " + _cursorLabel : _cursorLabel;
            float tw = ImGuiNET.ImGui.CalcTextSize(right).X;
            ImGuiNET.ImGui.GetWindowDrawList().AddText(new Vector2(p.X + w - tw, p.Y + (22 - ImGuiNET.ImGui.GetFontSize()) * 0.5f),
                _editor.IsDirty ? Amber : Mute, right);
        }
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + 22));
        ImGuiNET.ImGui.Dummy(new Vector2(0, 0));
    }

    private void ShowList() => _view = View.List;

    private void ShowSource()
    {
        if (_view == View.Source) return;
        _view = View.Source;
        if (_editor == null)
        {
            _editor = new CodeEditor("##meta_source", CodeLanguage.Meta, CodeEditor.RynthPalette());
            LoadSource(_data.SourceText);
        }
        else if (!_editor.IsDirty && _sourceBaseline != _data.SourceText)
            LoadSource(_data.SourceText);
        _editor.RequestFocus();
    }

    // Condition/action split of the rule list (dragged in its header; saved per PC).
    private static float _condFrac = Math.Clamp(RynthCore.Engine.UI.PanelColumnStore.Get("Meta.condFrac", 0.5f), 0.15f, 0.85f);

    // =====================================================================
    //  Source view
    // =====================================================================

    private void LoadSource(string text)
    {
        _editor?.SetText(text);
        _sourceBaseline = text;
        _sourceStale = false;
        _lastChange = _editor?.ChangeCount ?? 0;
        CloseCompletion();
    }

    private void SourceView(Vector2 origin, Vector2 size)
    {
        float w = size.X;
        var dl = ImGuiNET.ImGui.GetWindowDrawList();

        // File: name                Saved   (Apply saves; no Save button)
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        float indW = SaveIndicatorWidth();
        dl.AddText(new Vector2(p.X, p.Y + (22 - ImGuiNET.ImGui.GetFontSize()) * 0.5f), Amber, Fit(_sourceFileLabel, Math.Max(10, w - indW - 8)));
        SaveIndicator(new Vector2(p.X + w - indW, p.Y), indW, 22);
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + 22));
        ImGuiNET.ImGui.Dummy(new Vector2(0, 0));
        Separator(w);
        ModeRow(w, source: true);
        Separator(w);

        UpdateApplyTimeout();
        bool showMsg = _msg.Length > 0 && (_applySeqBefore != -2 || (DateTime.Now - _msgAt).TotalSeconds < 8);
        if (showMsg)
        {
            ImGuiNET.ImGui.PushTextWrapPos(ImGuiNET.ImGui.GetCursorPosX() + w);
            SafeImGui.TextColored(RynthTheme.Vec(_msgOk ? 0xFF33CC66 : 0xFFE04848), _msg);
            ImGuiNET.ImGui.PopTextWrapPos();
        }
        if (_sourceStale)
            ImGuiNET.ImGui.TextColored(RynthTheme.VecOf(RynthTheme.Highlight), "The meta changed since you started editing. Revert loads it.");

        if (_editor == null || !CodeEditor.Available)
        {
            ImGuiNET.ImGui.TextColored(RynthTheme.Vec(0xFFCC3333),
                "The source editor needs the new cimgui.dll (a full RynthCore deploy).");
            return;
        }

        const float buttonsH = 26;
        float editorH = Math.Max(60, origin.Y + size.Y - ImGuiNET.ImGui.GetCursorScreenPos().Y - buttonsH - 4);
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Code12));
        bool focused = _editor.Render(new Vector2(w, editorH));
        ImGuiNET.ImGui.PopFont();
        SourceAfterRender(focused);

        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
        if (FlowButton(PhosphorIcons.Check + " Apply##apply", TextDim, NewFill, Green)) Apply();
        ImGuiNET.ImGui.SameLine(0, 6);
        if (FlowButton(PhosphorIcons.ArrowCounterClockwise + " Revert##revert", Mute, BtnFill, BtnBord))
        {
            LoadSource(_data.SourceText);
            _editor.RequestFocus();
        }
    }

    private void Apply()
    {
        if (_editor == null) return;
        _applySeqBefore = _data.ApplyResult?.Seq ?? -1;
        _applySentAt = DateTime.Now;
        // Apply replaces every rule and re-enters the current state (RynthAi's set_source); it is
        // the one explicit "reload", never sent by autosave.
        SendEdit(new MetaCmd { Op = "set_source", Text = _editor.GetText() });
        SetMsg("Applying…", ok: true);
    }

    /// <summary>The first snapshot with a newer ApplyResult carries this apply's result (Appendix A5).</summary>
    private void CheckApplyResult(MetaPayload fresh)
    {
        if (_applySeqBefore == -2) return;
        MetaApplyResult? r = fresh.ApplyResult;
        if (r == null || r.Seq <= _applySeqBefore) return;
        _applySeqBefore = -2;
        SetMsg(r.Text, r.Ok);
        if (r.Ok && _editor != null)
        {
            // Applied: the editor's text is now the meta. Keep the caret and
            // scroll; Revert shows the plugin's own formatting.
            _editor.MarkClean();
            _sourceBaseline = fresh.SourceText;
            _sourceStale = false;
        }
    }

    private void UpdateApplyTimeout()
    {
        if (_applySeqBefore == -2 || (DateTime.Now - _applySentAt).TotalSeconds <= 3) return;
        _applySeqBefore = -2;
        SetMsg("Sent. This RynthAi version reports the result in chat.", ok: true);
    }

    private void SetMsg(string text, bool ok)
    {
        _msg = text;
        _msgOk = ok;
        _msgAt = DateTime.Now;
    }

    // ── Completion ───────────────────────────────────────────────────────

    private void SourceAfterRender(bool focused)
    {
        CodeEditor ed = _editor!;
        (int Line, int Column) cursor = ed.Cursor;
        bool moved = cursor != _cursor;
        if (moved)
        {
            _cursor = cursor;
            _cursorLabel = string.Create(CultureInfo.InvariantCulture, $"Ln {cursor.Line + 1}, Col {cursor.Column + 1}");
        }
        uint change = ed.ChangeCount;
        bool typed = change != _lastChange;
        _lastChange = change;

        if (focused && ImGuiNET.ImGui.GetIO().KeyCtrl && ImGuiNET.ImGui.IsKeyPressed(ImGuiKey.Space))
            OpenCompletion(auto: false);
        else if (typed && focused && !_compOpen)
        {
            // "IF: " (a space right after a colon) lists what can follow.
            string prefix = ed.LinePrefix();
            if (prefix.EndsWith(": ", StringComparison.Ordinal)) OpenCompletion(auto: true, prefix);
        }
        else if (_compOpen && (typed || moved))
            UpdateCompletion();

        if (_compOpen) DrawCompletion(focused);
        ed.CompletionKeysHeld = _compOpen;
    }

    private void OpenCompletion(bool auto, string? prefix = null)   // auto: after ": " (else Ctrl+Space)
    {
        CodeEditor ed = _editor!;
        prefix ??= ed.LinePrefix();
        int start = prefix.Length;
        while (start > 0 && (char.IsLetterOrDigit(prefix[start - 1]) || prefix[start - 1] == '_')) start--;
        _compAnchor = prefix.Substring(0, start);
        _compWord = prefix.Substring(start);
        _compLine = ed.Cursor.Line;

        BuildSuggestions(prefix);
        if (_compItems.Count == 0) { CloseCompletion(); return; }
        _compOpen = true;
        Filter();
        if (_compShown.Count == 0) { CloseCompletion(); return; }
        _compSel = 0;
        _compTop = 0;
    }

    private void UpdateCompletion()
    {
        CodeEditor ed = _editor!;
        string prefix = ed.LinePrefix();
        if (ed.Cursor.Line != _compLine || !prefix.StartsWith(_compAnchor, StringComparison.Ordinal))
        {
            CloseCompletion();
            return;
        }
        _compWord = prefix.Substring(_compAnchor.Length);
        Filter();
        if (_compShown.Count == 0) { CloseCompletion(); return; }
        _compSel = Math.Clamp(_compSel, 0, _compShown.Count - 1);
    }

    private void CloseCompletion()
    {
        _compOpen = false;
        _compItems.Clear();
        _compShown.Clear();
    }

    /// <summary>Prefix matches first, then matches anywhere (case-insensitive).</summary>
    private void Filter()
    {
        _compShown.Clear();
        for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < _compItems.Count; i++)
            {
                string t = _compItems[i].Text;
                bool prefix = t.StartsWith(_compWord, StringComparison.OrdinalIgnoreCase);
                if (pass == 0 ? prefix : !prefix && t.Contains(_compWord, StringComparison.OrdinalIgnoreCase))
                    _compShown.Add(i);
            }
    }

    // MetaSourceEditor.BuildSuggestionsForContext.
    private void BuildSuggestions(string prefix)
    {
        _compItems.Clear();
        string t = prefix.TrimStart().TrimEnd();
        if (StartsWith(t, "IF:", out _))
        {
            foreach (string s in MetaVocabulary.ConditionKeywords) _compItems.Add((s, SuggestionKind.Cond));
            return;
        }
        if (StartsWith(t, "DO:", out string rest))
        {
            rest = rest.TrimStart();
            if (StartsWith(rest, "SetState", out _) || StartsWith(rest, "CallState", out _))
                foreach (string s in _data.States) _compItems.Add((s, SuggestionKind.State));
            else if (StartsWith(rest, "EmbedNav", out _))
                foreach (string s in _completionNavs) _compItems.Add((s, SuggestionKind.Nav));
            else
                foreach (string s in MetaVocabulary.ActionKeywords) _compItems.Add((s, SuggestionKind.Action));
            return;
        }
        if (StartsWith(t, "STATE:", out _))
        {
            foreach (string s in _data.States) _compItems.Add((s, SuggestionKind.State));
            return;
        }
        if (StartsWith(t, "NAV:", out _))
        {
            foreach (string s in _completionNavs) _compItems.Add((s, SuggestionKind.Nav));
            return;
        }
        _compItems.Add(("STATE:", SuggestionKind.Struct));
        _compItems.Add(("IF:", SuggestionKind.Struct));
        _compItems.Add(("DO:", SuggestionKind.Struct));
        _compItems.Add(("NAV:", SuggestionKind.Struct));
    }

    private static bool StartsWith(string text, string keyword, out string rest)
    {
        if (text.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
        {
            rest = text.Substring(keyword.Length);
            return true;
        }
        rest = string.Empty;
        return false;
    }

    private void Accept(int shownIndex)
    {
        string text = _compItems[_compShown[shownIndex]].Text;
        _editor!.ReplaceBeforeCursor(Encoding.UTF8.GetByteCount(_compWord), text);
        CloseCompletion();
        _lastChange = _editor.ChangeCount;   // don't re-trigger on our own insert
        _editor.RequestFocus();
    }

    private void DrawCompletion(bool editorFocused)
    {
        int n = _compShown.Count;
        int rows = Math.Min(n, CompRows);
        const float rowH = 18, width = 260;
        Vector2 pos = _editor!.CursorScreenPos + new Vector2(0, 2);
        Vector2 size = new(width, rows * rowH + 6);
        Vector2 display = ImGuiNET.ImGui.GetIO().DisplaySize;
        if (pos.Y + size.Y > display.Y) pos.Y -= size.Y + 20;       // flip above the line
        pos.X = Math.Clamp(pos.X, 0, Math.Max(0, display.X - width));

        // Keys (the editor ignores these while the list is open).
        if (editorFocused)
        {
            if (ImGuiNET.ImGui.IsKeyPressed(ImGuiKey.DownArrow)) _compSel = Math.Min(n - 1, _compSel + 1);
            if (ImGuiNET.ImGui.IsKeyPressed(ImGuiKey.UpArrow)) _compSel = Math.Max(0, _compSel - 1);
            if (ImGuiNET.ImGui.IsKeyPressed(ImGuiKey.PageDown)) _compSel = Math.Min(n - 1, _compSel + CompRows);
            if (ImGuiNET.ImGui.IsKeyPressed(ImGuiKey.PageUp)) _compSel = Math.Max(0, _compSel - CompRows);
            if (ImGuiNET.ImGui.IsKeyPressed(ImGuiKey.Escape)) { CloseCompletion(); return; }
            if (ImGuiNET.ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGuiNET.ImGui.IsKeyPressed(ImGuiKey.KeypadEnter)
                || ImGuiNET.ImGui.IsKeyPressed(ImGuiKey.Tab))
            {
                Accept(_compSel);
                return;
            }
        }
        if (_compSel < _compTop) _compTop = _compSel;
        if (_compSel >= _compTop + CompRows) _compTop = _compSel - CompRows + 1;
        _compTop = Math.Clamp(_compTop, 0, Math.Max(0, n - CompRows));

        // A tooltip-class window: drawn above the panels, doesn't grab focus on appearing.
        const ImGuiWindowFlags flags = ImGuiWindowFlags.Tooltip | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing
            | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoDocking;
        ImGuiNET.ImGui.SetNextWindowPos(pos);
        ImGuiNET.ImGui.SetNextWindowSize(size);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.WindowBg, ShellBg);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, Teal);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(3, 3));
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 4f);
        ImGuiNET.ImGui.Begin("##meta_completion", flags);
        ImGuiNET.ImGui.PopStyleVar(3);
        ImGuiNET.ImGui.PopStyleColor(2);

        bool hovered = ImGuiNET.ImGui.IsWindowHovered();
        if (hovered)
        {
            float wheel = ImGuiNET.ImGui.GetIO().MouseWheel;
            if (wheel != 0) _compTop = Math.Clamp(_compTop - Math.Sign(wheel) * 3, 0, Math.Max(0, n - CompRows));
        }

        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        ImFontPtr code = ImGuiFonts.Get(UiFont.Code12);
        ImFontPtr small = ImGuiFonts.Get(UiFont.Ui9);
        float iw = ImGuiNET.ImGui.GetContentRegionAvail().X;
        int accept = -1;
        for (int r = 0; r < rows; r++)
        {
            int k = _compTop + r;
            if (k >= n) break;
            (string text, SuggestionKind kind) = _compItems[_compShown[k]];
            Vector2 ip = ImGuiNET.ImGui.GetCursorScreenPos();
            ImGuiNET.ImGui.PushID(r);
            if (ImGuiNET.ImGui.InvisibleButton("##c", new Vector2(iw, rowH))) accept = k;
            bool ih = ImGuiNET.ImGui.IsItemHovered();
            ImGuiNET.ImGui.PopID();
            bool sel = k == _compSel;
            if (sel || ih) dl.AddRectFilled(ip, ip + new Vector2(iw, rowH), sel ? PickSel : BtnFill, 2);
            dl.AddText(code, code.FontSize, ip + new Vector2(4, (rowH - code.FontSize) * 0.5f), KindColor(kind), text);
            string desc = KindLabel(kind);
            ImGuiNET.ImGui.PushFont(small);
            float dw = ImGuiNET.ImGui.CalcTextSize(desc).X;
            ImGuiNET.ImGui.PopFont();
            dl.AddText(small, small.FontSize, ip + new Vector2(iw - dw - 4, (rowH - small.FontSize) * 0.5f), Mute, desc);
        }
        ImGuiNET.ImGui.End();

        if (accept >= 0) Accept(accept);
        else if (!editorFocused && !hovered && !ImGuiNET.ImGui.IsMouseDown(ImGuiMouseButton.Left)) CloseCompletion();
    }

    private static uint KindColor(SuggestionKind kind) => kind switch
    {
        SuggestionKind.Struct => Teal,
        SuggestionKind.Cond => Amber,
        SuggestionKind.Action => C(0xFF7BB8FF),
        _ => TextDim,
    };

    private static string KindLabel(SuggestionKind kind) => kind switch
    {
        SuggestionKind.Struct => "structural keyword",
        SuggestionKind.Cond => "condition",
        SuggestionKind.Action => "action",
        SuggestionKind.State => "meta state",
        _ => "nav route",
    };

    // =====================================================================
    //  Widgets
    // =====================================================================

    /// <summary>A flat bordered button at <paramref name="pos"/>; the text after "##" is the id.</summary>
    private static bool Button(string labelId, Vector2 pos, Vector2 size, uint fg, uint bg, uint border, bool enabled = true)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGuiNET.ImGui.InvisibleButton(labelId, size) && enabled;
        bool hot = enabled && ImGuiNET.ImGui.IsItemHovered();
        uint fill = enabled && ImGuiNET.ImGui.IsItemActive() ? Darken(bg) : hot ? Lighten(bg) : bg;
        dl.AddRectFilled(pos, pos + size, fill, 3);
        dl.AddRect(pos, pos + size, border, 3);
        int cut = labelId.IndexOf("##", StringComparison.Ordinal);
        string label = cut >= 0 ? labelId.Substring(0, cut) : labelId;
        Vector2 ts = ImGuiNET.ImGui.CalcTextSize(label);
        dl.AddText(pos + (size - ts) * 0.5f, enabled ? fg : Faded(fg), label);
        return clicked;
    }

    private const string SaveLabel = PhosphorIcons.FloppyDisk + " Save";
    private const string MetaOn = PhosphorIcons.CheckSquare + " Meta##meta_on", MetaOff = PhosphorIcons.Square + " Meta##meta_on";
    private const string DebugOn = PhosphorIcons.CheckSquare + " Debug##meta_dbg", DebugOff = PhosphorIcons.Square + " Debug##meta_dbg";

    /// <summary>
    /// Button's look with a Phosphor icon centred on it; <paramref name="id"/> is
    /// the "##" id. Ui11 (about 10 px) suits the 14-20 px row buttons.
    /// </summary>
    private static bool IconButton(string id, string icon, Vector2 pos, Vector2 size, uint fg, uint bg, uint border,
        bool enabled = true, UiFont font = UiFont.Ui11)
    {
        bool clicked = Button(id, pos, size, fg, bg, border, enabled);
        PhosphorIcons.DrawCentered(ImGuiNET.ImGui.GetWindowDrawList(), ImGuiFonts.Get(font), icon, pos, size, enabled ? fg : Faded(fg));
        return clicked;
    }

    /// <summary>A button sized to its label, at the layout cursor.</summary>
    private static bool FlowButton(string labelId, uint fg, uint bg, uint border, float height = 22)
    {
        int cut = labelId.IndexOf("##", StringComparison.Ordinal);
        float tw = ImGuiNET.ImGui.CalcTextSize(cut >= 0 ? labelId.Substring(0, cut) : labelId).X;
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        Vector2 size = new(tw + 20, height);
        bool clicked = Button(labelId, p, size, fg, bg, border);
        ImGuiNET.ImGui.SetCursorScreenPos(p);
        ImGuiNET.ImGui.Dummy(size);
        return clicked;
    }

    /// <summary>Visual/Source-style toggle at (x, y); advances x.</summary>
    private static bool Toggle(string labelId, ref float x, float y, bool on)
    {
        int cut = labelId.IndexOf("##", StringComparison.Ordinal);
        float tw = ImGuiNET.ImGui.CalcTextSize(labelId.Substring(0, cut)).X;
        var size = new Vector2(tw + 16, 22);
        bool clicked = Button(labelId, new Vector2(x, y), size, on ? Teal : Mute, on ? BtnOn : BtnFill, on ? Teal : BtnBord);
        x += size.X + 4;
        return clicked;
    }

    /// <summary>
    /// A picker button showing <paramref name="shown"/> (cut with "..." before the arrow); click opens
    /// a list at least 240 px wide (current item highlighted). <paramref name="extra"/>, when given, is
    /// one more entry after the items ("+ New state..."): picking it returns items.Length. Returns the
    /// picked index or -1.
    /// </summary>
    private int Picker(string id, string shown, string[] items, int selected, Vector2 pos, float width, float height = 22,
        string? extra = null, string? badge = null)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        ImGuiNET.ImGui.PushID(id);
        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        int rows = items.Length + (extra != null ? 1 : 0);
        if (ImGuiNET.ImGui.InvisibleButton("##btn", new Vector2(Math.Max(1, width), height)) && rows > 0)
        {
            float listW = Math.Max(240, width);
            Vector2 display = ImGuiNET.ImGui.GetIO().DisplaySize;
            ImGuiNET.ImGui.SetNextWindowPos(new Vector2(Math.Clamp(pos.X, 0, Math.Max(0, display.X - listW)), pos.Y + height));
            ImGuiNET.ImGui.OpenPopup("##list");
        }
        bool hot = ImGuiNET.ImGui.IsItemHovered();
        dl.AddRectFilled(pos, pos + new Vector2(width, height), hot ? Lighten(BtnFill) : BtnFill, 3);
        dl.AddRect(pos, pos + new Vector2(width, height), BtnBord, 3);
        // The arrow has its own strip on the right; the text is cut before it (2026-10-05: long
        // state names ran under the arrow).
        const float caretW = 14;
        PhosphorIcons.DrawCentered(dl, ImGuiFonts.Get(UiFont.Ui11), PhosphorIcons.CaretDown,
            new Vector2(pos.X + width - caretW - 3, pos.Y), new Vector2(caretW, height), Mute);
        // A badge (seconds in state) sits right-aligned before the arrow; the text is cut before it.
        float badgeW = 0;
        if (!string.IsNullOrEmpty(badge))
        {
            badgeW = ImGuiNET.ImGui.CalcTextSize(badge).X + 8;
            if (badgeW > width * 0.5f) badgeW = 0;
            else dl.AddText(new Vector2(pos.X + width - caretW - 3 - badgeW + 2, pos.Y + (height - ImGuiNET.ImGui.GetFontSize()) * 0.5f),
                Faded(Teal), badge);
        }
        dl.AddText(pos + new Vector2(6, (height - ImGuiNET.ImGui.GetFontSize()) * 0.5f), TextDim, Fit(shown, width - 6 - caretW - 6 - badgeW));

        int picked = -1;
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, ShellBg);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, Teal);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 4f);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(2, 2));
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 1));
        ImGuiNET.ImGui.SetNextWindowSize(new Vector2(Math.Max(240, width), Math.Min(rows * 21 + 4, 320)));
        bool open = ImGuiNET.ImGui.BeginPopup("##list");
        ImGuiNET.ImGui.PopStyleVar(3);
        ImGuiNET.ImGui.PopStyleColor(2);
        if (open)
        {
            var pdl = ImGuiNET.ImGui.GetWindowDrawList();
            float iw = ImGuiNET.ImGui.GetContentRegionAvail().X;
            for (int i = 0; i < rows; i++)
            {
                ImGuiNET.ImGui.PushID(i);
                Vector2 ip = ImGuiNET.ImGui.GetCursorScreenPos();
                bool click = ImGuiNET.ImGui.InvisibleButton("##item", new Vector2(iw, 20));
                bool ih = ImGuiNET.ImGui.IsItemHovered();
                ImGuiNET.ImGui.PopID();
                bool isExtra = i == items.Length;
                bool sel = !isExtra && i == selected;
                pdl.AddRectFilled(ip, ip + new Vector2(iw, 20), sel ? PickSel : ih ? Lighten(BtnFill) : isExtra ? NewFill : BtnFill);
                pdl.AddRect(ip, ip + new Vector2(iw, 20), isExtra ? Green : BtnBord);
                pdl.AddText(ip + new Vector2(6, (20 - ImGuiNET.ImGui.GetFontSize()) * 0.5f), isExtra ? Green : sel ? Teal : TextDim,
                    Fit(isExtra ? extra! : items[i], iw - 12));
                if (click)
                {
                    picked = i;
                    ImGuiNET.ImGui.CloseCurrentPopup();
                }
            }
            ImGuiNET.ImGui.EndPopup();
        }
        ImGuiNET.ImGui.PopID();
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(pos.X, pos.Y + height));
        ImGuiNET.ImGui.Dummy(new Vector2(0, 0));
        return picked;
    }

    /// <summary>A button width that fits its label at the current font size (labels overflowed fixed
    /// widths at larger panel text sizes, 2026-10-05); never under <paramref name="min"/>.</summary>
    internal static float BtnW(string label, float min)
    {
        int cut = label.IndexOf("##", StringComparison.Ordinal);
        return Math.Max(min, ImGuiNET.ImGui.CalcTextSize(cut >= 0 ? label[..cut] : label).X + 18);
    }

    private static string[] With(string[] items, string added)
    {
        var grown = new string[items.Length + 1];
        items.CopyTo(grown, 0);
        grown[^1] = added;
        return grown;
    }

    /// <summary>A full-width picker at the layout cursor.</summary>
    private int FlowPicker(string id, string shown, string[] items, int selected, float width)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        int picked = Picker(id, shown, items, selected, p, width);
        ImGuiNET.ImGui.SetCursorScreenPos(p);
        ImGuiNET.ImGui.Dummy(new Vector2(width, 22));
        return picked;
    }

    /// <summary>A text box bound to a rule's ConditionData or ActionData, with a grey hint while empty.</summary>
    private void DataBox(string id, MetaRuleDto rule, bool action, string hint, float width, float height = 22)
    {
        if (!_fields.TryGetValue((rule, action), out FieldBuffer? buf))
            _fields[(rule, action)] = buf = new FieldBuffer();
        string value = action ? rule.ActionData : rule.ConditionData;
        if (!buf.Active && !ReferenceEquals(buf.Shown, value))
        {
            WriteUtf8(buf.Bytes, value);
            buf.Shown = value;
        }
        bool edited = TextBox(id, buf.Bytes, width, hint, ImGuiInputTextFlags.None, out buf.Active, height);
        _typingNow |= buf.Active;   // autosave waits until the box is left
        if (edited)
        {
            string typed = Utf8(buf.Bytes);
            if (action) rule.ActionData = typed; else rule.ConditionData = typed;
            buf.Shown = typed;
            _editChanged = true;
        }
    }

    /// <summary>A bordered InputText with a hint drawn while it is empty. Returns InputText's result.</summary>
    private static bool TextBox(string id, byte[] buffer, float width, string hint, ImGuiInputTextFlags flags, out bool active, float height = 22)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        ImGuiNET.ImGui.SetNextItemWidth(width);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg, BtnFill);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, TextDim);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, BtnBord);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(4, (height - ImGuiNET.ImGui.GetFontSize()) * 0.5f));
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 3f);
        bool result = ImGuiNET.ImGui.InputText(id, buffer, (uint)buffer.Length, flags);
        active = ImGuiNET.ImGui.IsItemActive();
        ImGuiNET.ImGui.PopStyleVar(3);
        ImGuiNET.ImGui.PopStyleColor(3);
        if (buffer[0] == 0 && !active && hint.Length > 0)
            ImGuiNET.ImGui.GetWindowDrawList().AddText(p + new Vector2(5, (height - ImGuiNET.ImGui.GetFontSize()) * 0.5f), Faded(Mute), hint);
        return result;
    }

    private static void FieldLabel(string text)
    {
        ImGuiNET.ImGui.Dummy(new Vector2(0, 1));
        SafeImGui.TextColored(RynthTheme.VecOf(RynthTheme.MuteAlt), text);
    }

    private static void Separator(float w)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        ImGuiNET.ImGui.GetWindowDrawList().AddLine(p + new Vector2(0, 2), p + new Vector2(w, 2), BtnBord);
        ImGuiNET.ImGui.Dummy(new Vector2(w, 5));
    }

    /// <summary><paramref name="text"/> cut to <paramref name="width"/> with "…", cached per text and width.</summary>
    private string Fit(string text, float width)
    {
        var key = (text, (int)MathF.Round(width));
        if (_fit.TryGetValue(key, out string? hit)) return hit;
        string shown = text;
        if (ImGuiNET.ImGui.CalcTextSize(text).X > width)
        {
            // The longest start that fits with "…" (binary search: long expressions are cut often,
            // and the cache empties with every snapshot while rules fire).
            int lo = 0, hi = text.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (ImGuiNET.ImGui.CalcTextSize(text.Substring(0, mid) + "…").X <= width) lo = mid; else hi = mid - 1;
            }
            shown = text.Substring(0, lo) + "…";
        }
        if (_fit.Count > 4000) _fit.Clear();   // widths change while a panel is resized
        _fit[key] = shown;
        return shown;
    }

    private static string Utf8(byte[] buffer)
    {
        int len = Array.IndexOf(buffer, (byte)0);
        return Encoding.UTF8.GetString(buffer, 0, len < 0 ? buffer.Length : len);
    }

    private static void WriteUtf8(byte[] buffer, string text)
    {
        int max = buffer.Length - 1;
        int n = Encoding.UTF8.GetByteCount(text) <= max ? Encoding.UTF8.GetBytes(text, 0, text.Length, buffer, 0) : TruncatedCopy(buffer, text, max);
        buffer[n] = 0;
    }

    private static int TruncatedCopy(byte[] buffer, string text, int max)
    {
        byte[] all = Encoding.UTF8.GetBytes(text);
        int n = max;
        while (n > 0 && (all[n] & 0xC0) == 0x80) n--;   // don't split a UTF-8 sequence
        Array.Copy(all, buffer, n);
        return n;
    }

    private static uint Lighten(uint abgr) => Scale(abgr, 1.25f, 12);
    private static uint Darken(uint abgr) => Scale(abgr, 0.8f, 0);

    private static uint Scale(uint abgr, float k, int add)
    {
        uint a = abgr & 0xFF000000;
        uint r = (uint)Math.Min(255, (int)((abgr & 0xFF) * k) + add);
        uint g = (uint)Math.Min(255, (int)(((abgr >> 8) & 0xFF) * k) + add);
        uint b = (uint)Math.Min(255, (int)(((abgr >> 16) & 0xFF) * k) + add);
        return a | (b << 16) | (g << 8) | r;
    }
}
