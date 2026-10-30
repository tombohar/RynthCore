// ============================================================================
//  RynthCore.Engine - ImGui/Panels/LootEditorFace.cs
//  The in-game Loot Editor (docs/IMGUI_LOOT_EDITOR.md), replacing the external
//  Avalonia app RynthCore.LootEditor. Phase 1:
//
//   toolbar   profile picker (LootProfiles and AutoVendor), Reload, Save, and
//             "Open external editor"; unsaved / read-only / not-in-use chips.
//   List      search (names and conditions) and action filter; one row per
//             rule: enable, action badge, name, conditions in words, up/down,
//             duplicate, delete; click a row to edit it; Add rule at the bottom.
//   Rule      name, action, Keep # count, enabled; conditions: type picker,
//             typed editors for the common types, the raw data lines for the
//             rest (and on request for any), reorder, delete, add; Apply/Revert.
//             Long keys with named values (WieldSkilltype, MaterialType...) pick
//             the value by name when RynthAi sends LongValueTables; else a text box.
//
//  Data: UiSources.LootEdit (hub snapshot) and LootEditCommands. RynthAi owns
//  the profile, validates each edit and does the saving (no clobbering, hot
//  reload). The face only reads snapshots and posts commands (AC's thread:
//  no plugin calls, no file I/O). The rule view edits a private copy and sends
//  it whole with Apply; the plugin's answer replaces it.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Threading;
using ImGuiNET;
using RynthCore.Engine.UI;
using RynthCore.Engine.UI.Data;
using static RynthCore.Engine.ImGuiBackend.Panels.FaceKit;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed class LootEditorFace : IImGuiPanel
{
    public const string Title = "Loot Editor";

    /// <summary>Changes only on edits and saves: 4 Hz idle when popped out.</summary>
    public int PopOutIdleHz => 4;

    public static void Register() => ImGuiPanelHost.Register(Title,
        new PanelSpec(new Vector2(600, 560), new Vector2(440, 320), EdgeToEdge: true, GripInBody: true),
        () => new LootEditorFace());

    // ── Opening from the dashboard (any thread) ─────────────────────────

    private static string? _requestedPath;
    private static string _shownPath = string.Empty;

    /// <summary>
    /// The dashboard's Loot Editor button: opens the panel on <paramref name="path"/>
    /// (empty: RynthAi's loot profile). Open on that file already: closes it.
    /// </summary>
    public static void ToggleFor(string? path)
    {
        path = (path ?? string.Empty).Trim();
        if (ImGuiPanelHost.IsOpen(Title))
        {
            if (path.Length == 0 || SamePath(path, Volatile.Read(ref _shownPath)))
            {
                PanelRouter.Toggle(Title);
                return;
            }
            Volatile.Write(ref _requestedPath, path);
            return;
        }
        Volatile.Write(ref _requestedPath, path);
        PanelRouter.Toggle(Title);
    }

    /// <summary>Starts the external Avalonia editor on <paramref name="path"/> (a short STA thread, not AC's).</summary>
    public static void LaunchExternal(string? path)
    {
        var launch = new Thread(() => RynthCore.Engine.UI.Panels.RynthAiPanel.LaunchLootEditor(path))
        {
            IsBackground = true,
            Name = "RynthCore.LootEditorLaunch",
        };
        launch.SetApartmentState(ApartmentState.STA);
        launch.Start();
    }

    private static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try { return string.Equals(System.IO.Path.GetFullPath(a.Trim()), System.IO.Path.GetFullPath(b.Trim()), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    // ── Snapshot and caches ─────────────────────────────────────────────

    private long _seenVersion = -1;
    private bool _unavailable;
    private LootEditStateDto? _state;
    private LootEditVocabDto? _vocab;
    private LootEditRuleDto? _ruleSeen;
    private string[] _fileItems = Array.Empty<string>();
    private string _fileLabel = "Loading...";
    private string _countLabel = string.Empty;
    // Tooltips and titles built when the state changes, not every frame.
    private string _saveTip = string.Empty, _newTip = string.Empty, _notInUseTip = string.Empty;
    private string _ruleTitle = string.Empty;
    private (int Index, int Count, bool Dirty) _ruleTitleKey = (-2, -1, false);
    private string _keepText = "0";
    private int _keepTextValue;

    // Vocabulary as picker lists (built when it arrives).
    private string[] _actionItems = Array.Empty<string>();
    private int[] _actionIds = Array.Empty<int>();
    private string[] _nodeItems = Array.Empty<string>();
    private int[] _nodeIds = Array.Empty<int>();
    private readonly Dictionary<int, LootEditNodeTypeDto> _nodes = new();
    private (string[] Names, int[] Ids) _classes, _longKeys, _doubleKeys, _stringKeys, _skills;
    // Long keys whose value is picked by name (WieldSkilltype's skills...). Items end with
    // "Other number..." (Ids has one fewer entry), which opens the raw lines.
    private sealed record ValueTable(string[] Items, int[] Ids, bool Flags);
    private readonly Dictionary<int, ValueTable> _valueTables = new();
    private const string OtherNumber = "Other number...";

    private readonly Picker _picker = new("##loot_pick");
    // "Add selected item": a rule for the item selected in the game, into the open profile.
    private readonly LootAddDialog _lootAdd = new("##loot_additem");

    // ── List ─────────────────────────────────────────────────────────────

    private enum Mode { List, Rule }
    private Mode _mode = Mode.List;
    private readonly byte[] _search = new byte[128];
    private string _searchText = string.Empty;
    private int _actionFilter;                 // index into LootEditVocabulary.FilterActions
    private readonly List<int> _filtered = new();
    private int _scrollTo = -1;
    private const float RowH = 20;

    // ── Messages and confirmations ──────────────────────────────────────

    private string _msg = string.Empty;
    private bool _msgOk = true;
    private DateTime _msgAt;
    private enum Confirm { None, DiscardOpen, DiscardReload, LeaveRule }
    private Confirm _confirm;
    private string _confirmPath = string.Empty;
    private enum Await { None, OpenFocus, ScrollFocus, Apply }
    private Await _await;
    private long _awaitSeq = -1;

    // ── Rule view ────────────────────────────────────────────────────────

    private int _editIndex = -1;
    private LootEditRuleDto? _editBase, _edit;
    private string? _expect;
    private bool _takeNextRule;
    private readonly HashSet<int> _rawShown = new();
    private sealed class FieldBuffer
    {
        public readonly byte[] Bytes = new byte[512];
        public string? Shown;
        public bool Active;
    }
    private readonly Dictionary<(int Cond, int Line), FieldBuffer> _fields = new();
    private readonly FieldBuffer _nameField = new(), _keepField = new();

    public void OnShown()
    {
        UiSources.LootEdit.Subscribe();
        UiSources.LootEdit.RequestRefresh();
        UiSources.LootEdit.WantRule(_mode == Mode.Rule ? _editIndex : -1);
    }

    public void OnHidden()
    {
        UiSources.LootEdit.WantRule(-1);
        UiSources.LootEdit.Unsubscribe();
        _lootAdd.Cancel();
    }

    // =====================================================================
    //  Frame
    // =====================================================================

    public void Draw()
    {
        string? req = Interlocked.Exchange(ref _requestedPath, null);
        if (req != null) RequestOpen(req);
        TakeSnapshot();

        float w = Begin(out Vector2 origin, out Vector2 size);
        if (_unavailable)
        {
            Label("RynthAi isn't loaded, or this RynthAi is older than the in-game Loot Editor.", Amber);
            if (Button("##ext_only", PhosphorIcons.ArrowSquareOut + " Open external editor", ImGuiNET.ImGui.GetCursorScreenPos(),
                    new Vector2(ButtonWidth(PhosphorIcons.ArrowSquareOut + " Open external editor") + 4, 24), Text, BtnFill))
                LaunchExternal(null);
        }
        else if (_state == null)
            Label("Loading the loot profile...", Mute);
        else
        {
            Toolbar(w);
            StatusRow(w);
            ConfirmBar(w);
            if (_mode == Mode.List) ListView(origin, size, w);
            else RuleView(origin, size, w);
        }
        End(origin, size);
        AcceptAcDrops(origin, size);
        _lootAdd.Draw();
        // An item rule added to the open profile: show it (the state already carries it and its Focus).
        if (_lootAdd.TakeAdded() && _mode == Mode.List && _state != null && _state.Focus >= 0 && _state.Focus < _state.Rules.Count)
            _scrollTo = _state.Focus;
        _picker.Draw();
    }

    // ── Drop an item from AC's own inventory on the editor (2026-10-05) ──
    // Compatibility/UiElementHooks keeps a drop over this body from AC (AC's own
    // "not caught" path, the item stays put) and hands the item here; it opens the
    // same "Add to loot profile" popup as "Add selected item", for that item.

    private const string DropKey = "LootEditor";

    private void AcceptAcDrops(Vector2 origin, Vector2 size)
    {
        LootEditStateDto? s = _state;
        bool can = !_unavailable && s != null && s.Path.Length > 0 && !s.Dirty
            && (!s.ReadOnly || s.Format == "json") && !_lootAdd.IsOpen;
        UiDropTargets.Publish(DropKey, Title, origin, origin + size, can);

        if (UiDropTargets.TryTake(DropKey, out uint itemId) && can)
        {
            Compatibility.ClientObjectHooks.TryGetObjectName(itemId, out string name);
            _lootAdd.Open(itemId, name ?? string.Empty, toOpenProfile: true);
        }

        // While AC drags an item: show that letting go here adds a rule.
        if (!can || Compatibility.UiElementHooks.CurrentDragObject == 0)
            return;
        bool over = UiDropTargets.IsCursorOver(DropKey);
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        dl.AddRect(origin + new Vector2(1, 1), origin + size - new Vector2(1, 1), over ? Green : Teal, 4, ImDrawFlags.None, over ? 3f : 1.5f);
        string hint = over ? "Let go: add a loot rule for this item" : "Drop an item here to add a loot rule";
        Vector2 ts = ImGuiNET.ImGui.CalcTextSize(hint);
        Vector2 at = new(origin.X + (size.X - ts.X) * 0.5f, origin.Y + size.Y - ts.Y - 10);
        dl.AddRectFilled(at - new Vector2(6, 3), at + ts + new Vector2(6, 3), ShellBg, 4);
        dl.AddText(at, over ? Green : Teal, hint);
    }

    private void RequestOpen(string path)
    {
        if (_state != null && _state.Dirty && path.Length > 0 && !SamePath(path, _state.Path))
        {
            _confirm = Confirm.DiscardOpen;
            _confirmPath = path;
            return;
        }
        LeaveRule();
        LootEditCommands.Simple("open", path: path);
    }

    private void TakeSnapshot()
    {
        var snap = UiSources.LootEdit.Current;
        if (snap == null || snap.Version == _seenVersion) return;
        _seenVersion = snap.Version;
        LootEditView v = snap.Value;
        _unavailable = v.Unavailable;
        if (v.Vocab != null && !ReferenceEquals(v.Vocab, _vocab))
        {
            _vocab = v.Vocab;
            BuildVocab();
        }
        if (v.State != null && !ReferenceEquals(v.State, _state)) OnState(v.State);
        if (_mode == Mode.Rule) OnRule(v.Rule);
    }

    private void OnState(LootEditStateDto s)
    {
        LootEditStateDto? before = _state;
        _state = s;
        Volatile.Write(ref _shownPath, s.Path);
        if (before == null || s.MessageSeq != before.MessageSeq)
            if (s.Message.Length > 0) SetMsg(s.Message, s.MessageOk);

        _fileItems = new string[s.Files.Count];
        for (int i = 0; i < _fileItems.Length; i++)
            _fileItems[i] = s.Files[i].Display + (SamePath(s.Files[i].Path, s.InUsePath) ? "   (in use)" : string.Empty);
        _fileLabel = s.Path.Length == 0 ? "No loot profile open" : s.FileName + (s.InUse ? "   (in use)" : string.Empty);
        _saveTip = s.ReadOnly ? "Read-only: " + s.ReadOnlyReason
            : s.Dirty ? "Save to " + s.FileName + " (RynthAi reloads it)" : "No unsaved changes";
        _newTip = "Save creates " + s.Path;
        _notInUseTip = s.InUsePath.Length == 0 ? "RynthAi has no loot profile selected"
            : "RynthAi loots with " + System.IO.Path.GetFileName(s.InUsePath);
        RebuildFilter();

        // A different file (opened here, or RynthAi switched profiles): back to the list.
        if (before != null && !SamePath(before.Path, s.Path)) LeaveRule();

        if (_await != Await.None && s.MessageSeq > _awaitSeq)
        {
            Await kind = _await;
            _await = Await.None;
            if (s.MessageOk && s.Focus >= 0 && s.Focus < s.Rules.Count)
            {
                if (kind == Await.OpenFocus) OpenRule(s.Focus);
                else if (kind == Await.ScrollFocus) _scrollTo = s.Focus;
            }
            if (kind == Await.Apply && s.MessageOk) _takeNextRule = true;
        }
    }

    private void OnRule(LootEditRuleDto? rule)
    {
        if (rule == null || rule.Index != _editIndex || ReferenceEquals(rule, _ruleSeen)) return;
        _ruleSeen = rule;
        if (_edit != null && !_takeNextRule && EditDirty) return;   // keep unapplied edits
        _takeNextRule = false;
        _editBase = rule;
        _edit = rule.Clone();
        _expect = rule.Name;
        ClearFields();
    }

    private bool EditDirty => _edit != null && _editBase != null && !_edit.SameAs(_editBase);

    private void BuildVocab()
    {
        LootEditVocabDto v = _vocab!;
        _actionItems = new string[v.Actions.Count];
        _actionIds = new int[v.Actions.Count];
        for (int i = 0; i < v.Actions.Count; i++) { _actionItems[i] = v.Actions[i].Name; _actionIds[i] = v.Actions[i].Id; }
        _nodes.Clear();
        var names = new List<string>();
        var ids = new List<int>();
        foreach (LootEditNodeTypeDto n in v.NodeTypes)
        {
            _nodes[n.Id] = n;
            if (n.Id == LootEditVocabulary.DisabledRule) continue;
            names.Add(n.Name);
            ids.Add(n.Id);
        }
        _nodeItems = names.ToArray();
        _nodeIds = ids.ToArray();
        _classes = Table(v.ObjectClasses);
        _longKeys = Table(v.LongKeys);
        _doubleKeys = Table(v.DoubleKeys);
        _stringKeys = Table(v.StringKeys);
        _skills = Table(v.Skills);
        // Absent from an older RynthAi: no tables, every value stays a text box.
        _valueTables.Clear();
        foreach (LootEditValueTableDto t in v.LongValueTables ?? new List<LootEditValueTableDto>())
        {
            if (t?.Values == null || t.Values.Count == 0) continue;
            (string[] tn, int[] ti) = Table(t.Values);
            var items = new string[tn.Length + 1];
            Array.Copy(tn, items, tn.Length);
            items[tn.Length] = OtherNumber;
            _valueTables[t.Key] = new ValueTable(items, ti, t.Flags);
        }

        static (string[], int[]) Table(List<LootEditNameDto> list)
        {
            var n = new string[list.Count];
            var id = new int[list.Count];
            for (int i = 0; i < list.Count; i++) { n[i] = list[i].Name + "  (" + list[i].Id.ToString(CultureInfo.InvariantCulture) + ")"; id[i] = list[i].Id; }
            return (n, id);
        }
    }

    private void SetMsg(string text, bool ok)
    {
        _msg = text;
        _msgOk = ok;
        _msgAt = DateTime.Now;
    }

    // =====================================================================
    //  Toolbar, status, confirmations
    // =====================================================================

    private void Toolbar(float w)
    {
        LootEditStateDto s = _state!;
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        const float icon = 26, saveW = 64, h = 24;
        float pickW = w - icon * 2 - saveW - 12;

        if (Button("##file", _fileLabel, p, new Vector2(pickW, h), s.Dirty ? Amber : Text, BtnFill, leftAlign: true))
        {
            int sel = s.Files.FindIndex(f => SamePath(f.Path, s.Path));
            _picker.Open(new Vector2(p.X, p.Y + h), _fileItems, sel, i =>
            {
                if (i >= 0 && i < s.Files.Count) RequestOpen(s.Files[i].Path);
            }, Math.Max(260, pickW));
        }
        SafeImGui.SetItemTooltip(s.Path.Length == 0 ? "Pick a loot profile" : s.Path);
        PhosphorIcons.DrawCentered(ImGuiNET.ImGui.GetWindowDrawList(), ImGuiFonts.Get(UiFont.Ui11), PhosphorIcons.CaretDown,
            new Vector2(p.X + pickW - 18, p.Y), new Vector2(16, h), Mute);

        float x = p.X + pickW + 4;
        if (IconButton("##reload", PhosphorIcons.ArrowsClockwise, new Vector2(x, p.Y), new Vector2(icon, h), Text, BtnFill, s.Path.Length > 0))
        {
            if (s.Dirty) _confirm = Confirm.DiscardReload;
            else { LeaveRule(); LootEditCommands.Simple("reload"); }
        }
        ImGuiNET.ImGui.SetItemTooltip("Reload from disk");
        x += icon + 4;

        bool canSave = !s.ReadOnly && s.Path.Length > 0 && (s.Dirty || !s.Exists);
        if (Button("##save", PhosphorIcons.FloppyDisk + " Save", new Vector2(x, p.Y), new Vector2(saveW, h),
                canSave ? Green : Mute, canSave ? StartBg : BtnFill, canSave))
            Save(force: false);
        SafeImGui.SetItemTooltip(_saveTip);
        x += saveW + 4;

        if (IconButton("##external", PhosphorIcons.ArrowSquareOut, new Vector2(x, p.Y), new Vector2(icon, h), Text, BtnFill))
            LaunchExternal(s.Path.Length > 0 ? s.Path : s.InUsePath);
        SafeImGui.SetItemTooltip(s.Dirty
            ? "Open external editor (it reads the file on disk: save first to take your edits)"
            : "Open external editor");
        NextLine(p, h + 4);
    }

    private void Save(bool force)
    {
        if (_mode == Mode.Rule && EditDirty && !(_state?.ReadOnly ?? true))
        {
            SetMsg("Apply or Revert this rule's changes first, then Save.", ok: false);
            return;
        }
        LootEditCommands.Simple("save", force: force);
    }

    private void StatusRow(float w)
    {
        LootEditStateDto s = _state!;
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        const float h = 18;
        float ty = p.Y + (h - ImGuiNET.ImGui.GetFontSize()) * 0.5f;
        dl.AddText(new Vector2(p.X, ty), Mute, _countLabel);
        float x = p.X + ImGuiNET.ImGui.CalcTextSize(_countLabel).X + 10;
        if (s.Dirty) x = Chip(dl, x, p.Y, h, "unsaved", Amber, "Changes not saved to the file yet");
        if (s.ReadOnly) x = Chip(dl, x, p.Y, h, "read-only", Red, s.ReadOnlyReason);
        if (s.Path.Length > 0 && !s.Exists) x = Chip(dl, x, p.Y, h, "new file", Teal, _newTip);
        if (s.Path.Length > 0 && !s.InUse)
            Chip(dl, x, p.Y, h, "not in use", Mute, _notInUseTip);
        NextLine(p, h + 2);
    }

    private static float Chip(ImDrawListPtr dl, float x, float y, float h, string text, uint color, string tooltip)
    {
        Vector2 ts = ImGuiNET.ImGui.CalcTextSize(text);
        var a = new Vector2(x, y + 1);
        var b = new Vector2(x + ts.X + 12, y + h - 1);
        dl.AddRect(a, b, color, 8);
        dl.AddText(new Vector2(x + 6, y + (h - ts.Y) * 0.5f), color, text);
        if (ImGuiNET.ImGui.IsMouseHoveringRect(a, b) && tooltip.Length > 0) SafeImGui.SetTooltip(tooltip);
        return b.X + 6;
    }

    private void ConfirmBar(float w)
    {
        LootEditStateDto s = _state!;
        string text;
        if (s.ChangedOnDisk)
            text = s.FileName + " changed on disk while you have unsaved edits.";
        else if (_confirm == Confirm.DiscardOpen)
            text = "Unsaved changes in " + s.FileName + " will be lost.";
        else if (_confirm == Confirm.DiscardReload)
            text = "Reload " + s.FileName + " from disk and drop your edits?";
        else if (_confirm == Confirm.LeaveRule)
            text = "This rule has changes you haven't applied.";
        else return;

        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        const float h = 26;
        dl.AddRectFilled(p, p + new Vector2(w, h), RynthTheme.Argb(0xFF3A2A0A), 3);
        PhosphorIcons.DrawCentered(dl, ImGuiFonts.Get(UiFont.Ui11), PhosphorIcons.Warning, p, new Vector2(22, h), Amber);

        var labels = new List<string>(3);
        if (s.ChangedOnDisk) { labels.Add("Save anyway"); labels.Add("Reload"); }
        else if (_confirm == Confirm.DiscardOpen) { labels.Add("Discard and open"); labels.Add("Cancel"); }
        else if (_confirm == Confirm.DiscardReload) { labels.Add("Reload"); labels.Add("Cancel"); }
        else { labels.Add("Apply"); labels.Add("Discard"); labels.Add("Cancel"); }

        float bx = p.X + w - 4;
        int clicked = -1;
        for (int i = labels.Count - 1; i >= 0; i--)
        {
            float bw = ButtonWidth(labels[i]);
            bx -= bw;
            ImGuiNET.ImGui.PushID(900 + i);
            if (Button("##c", labels[i], new Vector2(bx, p.Y + 3), new Vector2(bw, h - 6), i == 0 ? Amber : Text, BtnFill)) clicked = i;
            ImGuiNET.ImGui.PopID();
            bx -= 4;
        }
        dl.PushClipRect(p, new Vector2(bx, p.Y + h), true);
        dl.AddText(new Vector2(p.X + 24, p.Y + (h - ImGuiNET.ImGui.GetFontSize()) * 0.5f), Amber, text);
        dl.PopClipRect();
        NextLine(p, h + 4);

        if (clicked < 0) return;
        if (s.ChangedOnDisk)
        {
            if (clicked == 0) Save(force: true);
            else { LeaveRule(); LootEditCommands.Simple("reload", force: true); }
            return;
        }
        Confirm c = _confirm;
        _confirm = Confirm.None;
        if (c == Confirm.DiscardOpen && clicked == 0) { LeaveRule(); LootEditCommands.Simple("open", path: _confirmPath, force: true); }
        else if (c == Confirm.DiscardReload && clicked == 0) { LeaveRule(); LootEditCommands.Simple("reload", force: true); }
        else if (c == Confirm.LeaveRule)
        {
            if (clicked == 0) { Apply(); LeaveRule(); }
            else if (clicked == 1) LeaveRule();
        }
    }

    // =====================================================================
    //  List view
    // =====================================================================

    private void RebuildFilter()
    {
        _filtered.Clear();
        LootEditStateDto? s = _state;
        if (s == null) { _countLabel = string.Empty; return; }
        int action = LootEditVocabulary.FilterActions[_actionFilter];
        string q = _searchText.Trim();
        for (int i = 0; i < s.Rules.Count; i++)
        {
            LootEditRowDto r = s.Rules[i];
            if (action != 0 && r.Action != action) continue;
            if (q.Length > 0 && !r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !r.Summary.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            _filtered.Add(i);
        }
        _countLabel = _filtered.Count == s.Rules.Count
            ? s.Rules.Count.ToString(CultureInfo.InvariantCulture) + " rules"
            : _filtered.Count.ToString(CultureInfo.InvariantCulture) + " of " + s.Rules.Count.ToString(CultureInfo.InvariantCulture) + " rules";
    }

    private bool Filtering => _actionFilter != 0 || _searchText.Trim().Length > 0;

    private void ListView(Vector2 origin, Vector2 size, float w)
    {
        LootEditStateDto s = _state!;
        FilterRow(w);
        Separator(w);

        const float bottomH = 24;
        float listH = Math.Max(40, Remaining(origin, size) - bottomH - 6);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ChildBg, PanelBg);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 0));
        ImGuiNET.ImGui.BeginChild("##loot_rules", new Vector2(w, listH));
        if (s.Path.Length == 0)
            Label("  No loot profile open. Pick one above, or choose one in the RynthAi dashboard.", Mute);
        else if (s.Rules.Count == 0)
            Label("  No rules yet. Add rule creates one.", Mute);
        else if (_filtered.Count == 0)
            Label("  No rules match the filter.", Mute);
        else
            RuleRows(s, ImGuiNET.ImGui.GetContentRegionAvail().X, listH);
        ImGuiNET.ImGui.EndChild();
        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.PopStyleColor();

        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
        BottomBar(w, s);
    }

    private void FilterRow(float w)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        const float h = 22;
        float searchW = Math.Min(220, w * 0.38f);
        if (TextBox("##search", _search, p, searchW, "Search names and conditions", out _, ImGuiInputTextFlags.None, h))
        {
            _searchText = Utf8(_search);
            RebuildFilter();
        }
        float x = p.X + searchW + 8;
        for (int i = 0; i < LootEditVocabulary.FilterLabels.Length; i++)
        {
            string label = LootEditVocabulary.FilterLabels[i];
            float bw = ButtonWidth(label);
            if (x + bw > p.X + w) break;
            bool on = _actionFilter == i;
            ImGuiNET.ImGui.PushID(800 + i);
            if (Button("##f", label, new Vector2(x, p.Y), new Vector2(bw, h), on ? Teal : Mute, on ? Selected : BtnFill, border: on ? Teal : 0))
            {
                _actionFilter = i;
                RebuildFilter();
            }
            ImGuiNET.ImGui.PopID();
            x += bw + 3;
        }
        NextLine(p, h + 4);
    }

    private void RuleRows(LootEditStateDto s, float w, float viewH)
    {
        if (_scrollTo >= 0)
        {
            int pos = _filtered.IndexOf(_scrollTo);
            if (pos >= 0)
            {
                float y = pos * RowH, top = ImGuiNET.ImGui.GetScrollY();
                if (y < top || y + RowH > top + viewH) ImGuiNET.ImGui.SetScrollY(Math.Max(0, y - viewH * 0.4f));
            }
            _scrollTo = -1;
        }

        // Only the visible rows are drawn (LootSnobV4 has 1637).
        int n = _filtered.Count;
        float scroll = ImGuiNET.ImGui.GetScrollY();
        int first = Math.Clamp((int)(scroll / RowH), 0, n);
        int last = Math.Min(n, first + (int)(viewH / RowH) + 2);
        if (first > 0) ImGuiNET.ImGui.Dummy(new Vector2(w, first * RowH));
        bool reorder = !Filtering && !s.ReadOnly;
        for (int k = first; k < last; k++)
        {
            int index = _filtered[k];
            if (index >= s.Rules.Count) break;
            // A click only posts a command (the list changes when the next snapshot comes), so
            // every row still draws: stopping early would shrink the scroll range for a frame.
            RuleRow(s, s.Rules[index], index, k, w, reorder);
        }
        if (last < n) ImGuiNET.ImGui.Dummy(new Vector2(w, (n - last) * RowH));
    }

    /// <summary>One rule row. True when a click sent a command that changes the list.</summary>
    private bool RuleRow(LootEditStateDto s, LootEditRowDto r, int index, int k, float w, bool reorder)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        bool ro = s.ReadOnly;
        dl.AddRectFilled(p, p + new Vector2(w, RowH), k % 2 == 0 ? PanelBg : RowAlt);
        ImGuiNET.ImGui.PushID(index);
        bool changed = false;

        // Enable
        float x = p.X + 2;
        if (IconButton("##en", r.Enabled ? PhosphorIcons.CheckSquare : PhosphorIcons.Square, new Vector2(x, p.Y + 2), new Vector2(16, 16),
                r.Enabled ? Green : Mute, BtnFill, !ro, font: UiFont.Ui11))
        {
            LootEditCommands.Simple("set_enabled", index, r.Enabled ? "false" : "true", expect: r.Name);
            changed = true;
        }
        SafeImGui.SetItemTooltip(r.Enabled ? "Enabled: click to disable" : "Disabled: click to enable");
        x += 20;

        // Action badge
        const float badgeW = 64;
        string badge = LootEditVocabulary.Badge(r.Action, r.KeepCount);
        uint badgeCol = RynthTheme.Argb(LootEditVocabulary.BadgeArgb(r.Action));
        dl.AddRectFilled(new Vector2(x, p.Y + 3), new Vector2(x + badgeW, p.Y + RowH - 3), r.Enabled ? badgeCol : Faded(badgeCol), 3);
        ImFontPtr f9 = ImGuiFonts.Get(UiFont.Ui9);
        ImGuiNET.ImGui.PushFont(f9);
        Vector2 bs = ImGuiNET.ImGui.CalcTextSize(badge);
        ImGuiNET.ImGui.PopFont();
        dl.AddText(f9, f9.FontSize, new Vector2(x + (badgeW - bs.X) * 0.5f, p.Y + (RowH - bs.Y) * 0.5f), Text, badge);
        x += badgeW + 6;

        // Buttons on the right: up, down, duplicate, delete.
        const float btns = 18 * 3 + 22 + 4;
        float right = p.X + w - btns;
        float bx = right;
        if (IconButton("##up", PhosphorIcons.ArrowUp, new Vector2(bx, p.Y + 2), new Vector2(16, 16), Mute, BtnFill, reorder && index > 0, font: UiFont.Ui11))
        {
            LootEditCommands.Simple("move", index, to: index - 1, expect: r.Name);
            Expect(Await.ScrollFocus);
            changed = true;
        }
        SafeImGui.SetItemTooltip(reorder ? "Move up" : Filtering ? "Clear the search and filter to reorder" : "Move up");
        bx += 18;
        if (IconButton("##dn", PhosphorIcons.ArrowDown, new Vector2(bx, p.Y + 2), new Vector2(16, 16), Mute, BtnFill,
                reorder && index < s.Rules.Count - 1, font: UiFont.Ui11))
        {
            LootEditCommands.Simple("move", index, to: index + 1, expect: r.Name);
            Expect(Await.ScrollFocus);
            changed = true;
        }
        SafeImGui.SetItemTooltip(reorder ? "Move down" : Filtering ? "Clear the search and filter to reorder" : "Move down");
        bx += 18;
        if (IconButton("##dup", PhosphorIcons.Copy, new Vector2(bx, p.Y + 2), new Vector2(16, 16), Mute, BtnFill, !ro, font: UiFont.Ui11))
        {
            LootEditCommands.Simple("duplicate", index, expect: r.Name);
            Expect(Await.ScrollFocus);
            changed = true;
        }
        ImGuiNET.ImGui.SetItemTooltip("Duplicate rule");
        bx += 18;
        if (IconButton("##del", PhosphorIcons.Trash, new Vector2(bx, p.Y + 2), new Vector2(20, 16), Text, DeleteBg, !ro, font: UiFont.Ui11))
        {
            LootEditCommands.Simple("delete", index, expect: r.Name);
            changed = true;
        }
        ImGuiNET.ImGui.SetItemTooltip("Delete rule (Reload undoes it until you Save)");

        // Name | conditions; click to edit.
        float textW = Math.Max(1, right - 4 - x);
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x, p.Y));
        if (ImGuiNET.ImGui.InvisibleButton("##edit", new Vector2(textW, RowH))) OpenRule(index);
        bool hot = ImGuiNET.ImGui.IsItemHovered();
        if (hot)
        {
            ImGuiNET.ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            dl.AddRect(new Vector2(x - 2, p.Y), new Vector2(x + textW, p.Y + RowH), BtnBord, 2);
            SafeImGui.SetTooltip((r.Name.Length == 0 ? "(no name)" : r.Name) + "\n" + r.Summary);
        }
        ImGuiNET.ImGui.PopID();

        float nameW = textW * 0.42f;
        float ty = p.Y + (RowH - ImGuiNET.ImGui.GetFontSize()) * 0.5f;
        uint nameCol = r.Enabled ? Text : Faded(Text), sumCol = r.Enabled ? Mute : Faded(Mute);
        dl.PushClipRect(new Vector2(x, p.Y), new Vector2(x + nameW - 6, p.Y + RowH), true);
        dl.AddText(new Vector2(x, ty), r.Name.Length == 0 ? Faded(Mute) : nameCol, r.Name.Length == 0 ? "(no name)" : r.Name);
        dl.PopClipRect();
        dl.PushClipRect(new Vector2(x + nameW, p.Y), new Vector2(x + textW, p.Y + RowH), true);
        dl.AddText(new Vector2(x + nameW, ty), sumCol, r.Summary);
        dl.PopClipRect();

        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + RowH));
        ImGuiNET.ImGui.Dummy(new Vector2(0, 0));
        return changed;
    }

    private void Expect(Await kind)
    {
        _await = kind;
        _awaitSeq = _state?.MessageSeq ?? 0;
    }

    private void BottomBar(float w, LootEditStateDto s)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        const float h = 24;
        float grip = ImGuiPanelHost.BodyGripReserve;
        string addLabel = PhosphorIcons.Plus + " Add rule";
        float addW = ButtonWidth(addLabel) + 6;
        bool canAdd = !s.ReadOnly && s.Path.Length > 0;
        if (Button("##add", addLabel, p, new Vector2(addW, h), canAdd ? Green : Mute, canAdd ? StartBg : BtnFill, canAdd))
        {
            LootEditCommands.Simple("add", -1);
            Expect(Await.OpenFocus);
        }
        ImGuiNET.ImGui.SetItemTooltip("Add a rule at the end and edit it");
        string itemLabel = PhosphorIcons.Plus + " Add selected item";
        float itemW = ButtonWidth(itemLabel) + 6;
        bool canItem = s.Path.Length > 0 && !s.Dirty && (!s.ReadOnly || s.Format == "json");
        if (Button("##add_item", itemLabel, new Vector2(p.X + addW + 6, p.Y), new Vector2(itemW, h), canItem ? Teal : Mute, BtnFill, canItem))
            _lootAdd.Open(0, string.Empty, toOpenProfile: true);
        SafeImGui.SetItemTooltip(canItem
            ? "A rule for the item selected in the game (click it first): preview, then add it here"
            : s.Dirty ? "Save or discard your edits first" : "This profile is read-only here");
        addW += itemW + 6;
        MessageText(new Vector2(p.X + addW + 8, p.Y), p.X + w - grip - 4, h);
        NextLine(p, h);
    }

    private void MessageText(Vector2 at, float right, float h)
    {
        if (_msg.Length == 0 || (_msgOk && (DateTime.Now - _msgAt).TotalSeconds > 10)) return;
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        dl.PushClipRect(at, new Vector2(Math.Max(at.X + 1, right), at.Y + h), true);
        dl.AddText(new Vector2(at.X, at.Y + (h - ImGuiNET.ImGui.GetFontSize()) * 0.5f), _msgOk ? Green : Red, _msg);
        dl.PopClipRect();
        if (ImGuiNET.ImGui.IsMouseHoveringRect(at, new Vector2(right, at.Y + h))) SafeImGui.SetTooltip(_msg);
    }

    // =====================================================================
    //  Rule view
    // =====================================================================

    private void OpenRule(int index)
    {
        _mode = Mode.Rule;
        _editIndex = index;
        _edit = _editBase = null;
        _ruleSeen = null;
        _takeNextRule = true;
        _rawShown.Clear();
        ClearFields();
        if (_confirm == Confirm.LeaveRule) _confirm = Confirm.None;
        UiSources.LootEdit.WantRule(index);
    }

    private void LeaveRule()
    {
        if (_mode == Mode.Rule && _editIndex >= 0) _scrollTo = _editIndex;
        _mode = Mode.List;
        _editIndex = -1;
        _edit = _editBase = null;
        if (_confirm == Confirm.LeaveRule) _confirm = Confirm.None;
        UiSources.LootEdit.WantRule(-1);
    }

    private void ClearFields()
    {
        _fields.Clear();
        _nameField.Shown = null;
        _keepField.Shown = null;
    }

    private void Apply()
    {
        if (_edit == null || _state == null || _state.ReadOnly) return;
        LootEditCommands.Send(new LootEditCmd { Op = "update_rule", Index = _editIndex, Expect = _expect, Rule = _edit.Clone() });
        Expect(Await.Apply);
    }

    private void RuleView(Vector2 origin, Vector2 size, float w)
    {
        LootEditStateDto s = _state!;
        bool ro = s.ReadOnly;
        bool dirty = EditDirty;

        // [< Back]  Rule 12 of 1637            [Apply] [Revert]
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        const float h = 24;
        string back = PhosphorIcons.ArrowLeft + " Back";
        float backW = ButtonWidth(back) + 4;
        if (Button("##back", back, p, new Vector2(backW, h), Text, BtnFill))
        {
            if (dirty && !ro) _confirm = Confirm.LeaveRule;
            else LeaveRule();
        }
        ImFontPtr bold = ImGuiFonts.Get(UiFont.UiBold11);
        if (_ruleTitleKey != (_editIndex, s.Rules.Count, dirty))
        {
            _ruleTitleKey = (_editIndex, s.Rules.Count, dirty);
            _ruleTitle = "Rule " + (_editIndex + 1).ToString(CultureInfo.InvariantCulture) + " of "
                         + s.Rules.Count.ToString(CultureInfo.InvariantCulture) + (dirty ? "   (changed)" : string.Empty);
        }
        string title = _ruleTitle;
        ImGuiNET.ImGui.GetWindowDrawList().AddText(bold, bold.FontSize, new Vector2(p.X + backW + 10, p.Y + (h - bold.FontSize) * 0.5f),
            dirty ? Amber : Teal, title);
        string applyL = PhosphorIcons.Check + " Apply", revertL = PhosphorIcons.ArrowCounterClockwise + " Revert";
        float applyW = ButtonWidth(applyL) + 6, revertW = ButtonWidth(revertL) + 6;
        float rx = p.X + w - revertW;
        if (Button("##revert", revertL, new Vector2(rx, p.Y), new Vector2(revertW, h), Text, BtnFill, dirty))
        {
            if (_editBase != null) { _edit = _editBase.Clone(); ClearFields(); }
        }
        ImGuiNET.ImGui.SetItemTooltip("Drop this rule's changes");
        rx -= applyW + 4;
        if (Button("##apply", applyL, new Vector2(rx, p.Y), new Vector2(applyW, h), dirty ? Green : Mute, dirty ? StartBg : BtnFill, dirty && !ro))
            Apply();
        ImGuiNET.ImGui.SetItemTooltip("Put these changes in the profile (then Save writes the file)");
        NextLine(p, h + 4);
        Separator(w);

        if (_edit == null)
        {
            Label("Loading the rule...", Mute);
            return;
        }

        float bodyH = Math.Max(40, Remaining(origin, size) - GripOverlap());
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ChildBg, 0);
        ImGuiNET.ImGui.BeginChild("##loot_rule", new Vector2(w, bodyH));
        float cw = ImGuiNET.ImGui.GetContentRegionAvail().X - 4;
        RuleBody(_edit, cw, ro);
        MessageRow(cw);
        ImGuiNET.ImGui.EndChild();
        ImGuiNET.ImGui.PopStyleColor();
    }

    private void MessageRow(float w)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        MessageText(new Vector2(p.X, p.Y + 4), p.X + w, 20);
        NextLine(p, 28);
    }

    private void RuleBody(LootEditRuleDto r, float w, bool ro)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        const float h = 22, labelW = 64;

        // Name
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        FieldLabel(dl, p, h, "Name");
        if (Field("##name", _nameField, r.Name, new Vector2(p.X + labelW, p.Y), w - labelW, "(no name)", ro, out string name, h))
            r.Name = name;
        NextLine(p, h + 6);

        // Action [Keep #: count]  Enabled
        p = ImGuiNET.ImGui.GetCursorScreenPos();
        FieldLabel(dl, p, h, "Action");
        int ai = Array.IndexOf(_actionIds, r.Action);
        string actionText = ai >= 0 ? _actionItems[ai] : "Action " + r.Action.ToString(CultureInfo.InvariantCulture);
        float x = p.X + labelW;
        if (PickerButton("##action", actionText, new Vector2(x, p.Y), 110, h, ro))
            _picker.Open(new Vector2(x, p.Y + h), _actionItems, ai, i =>
            {
                if (_edit == null) return;
                _edit.Action = _actionIds[i];
                if (_edit.Action == LootEditVocabulary.KeepUpTo && _edit.KeepCount <= 0) _edit.KeepCount = 1;
                _keepField.Shown = null;
            }, 160);
        x += 116;
        if (r.Action == LootEditVocabulary.KeepUpTo)
        {
            dl.AddText(new Vector2(x, p.Y + (h - ImGuiNET.ImGui.GetFontSize()) * 0.5f), Mute, "keep up to");
            x += ImGuiNET.ImGui.CalcTextSize("keep up to").X + 6;
            if (_keepTextValue != r.KeepCount || _keepText.Length == 0)
            {
                _keepTextValue = r.KeepCount;
                _keepText = r.KeepCount.ToString(CultureInfo.InvariantCulture);
            }
            string count = _keepText;
            if (IconButton("##kc_dec", PhosphorIcons.Minus, new Vector2(x, p.Y), new Vector2(h, h), Teal, BtnFill, !ro && r.KeepCount > 0, font: UiFont.Ui11))
            { r.KeepCount = Math.Max(0, r.KeepCount - 1); _keepField.Shown = null; }
            x += h + 2;
            if (Field("##kc", _keepField, count, new Vector2(x, p.Y), 56, "0", ro, out string typed, h)
                && int.TryParse(typed.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int kc) && kc >= 0)
                r.KeepCount = kc;
            x += 58;
            if (IconButton("##kc_inc", PhosphorIcons.Plus, new Vector2(x, p.Y), new Vector2(h, h), Teal, BtnFill, !ro, font: UiFont.Ui11))
            { r.KeepCount++; _keepField.Shown = null; }
            x += h + 10;
        }
        string en = (r.Enabled ? PhosphorIcons.CheckSquare : PhosphorIcons.Square) + " Enabled";
        float enW = ButtonWidth(en);
        if (Button("##enabled", en, new Vector2(Math.Max(x, p.X + w - enW), p.Y), new Vector2(enW, h), r.Enabled ? Green : Mute, BtnFill, !ro))
            r.Enabled = !r.Enabled;
        NextLine(p, h + 8);

        // Conditions
        ImFontPtr bold = ImGuiFonts.Get(UiFont.UiBold11);
        p = ImGuiNET.ImGui.GetCursorScreenPos();
        dl.AddText(bold, bold.FontSize, p, Amber, "Conditions (all must match)");
        NextLine(p, bold.FontSize + 6);

        int shown = 0;
        for (int i = 0; i < r.Conditions.Count; i++)
        {
            if (r.Conditions[i].NodeType == LootEditVocabulary.DisabledRule) continue;   // the Enabled switch owns these
            shown++;
            ImGuiNET.ImGui.PushID(i);
            int result = ConditionRow(r, i, w, ro);
            ImGuiNET.ImGui.PopID();
            if (result != 0) break;   // the list changed: the rest draws next frame
        }
        if (shown == 0) Label("No conditions: this rule matches every item.", Mute);

        ImGuiNET.ImGui.Dummy(new Vector2(0, 4));
        p = ImGuiNET.ImGui.GetCursorScreenPos();
        string addL = PhosphorIcons.Plus + " Add condition";
        if (Button("##add_cond", addL, p, new Vector2(ButtonWidth(addL) + 6, h), ro ? Mute : Teal, BtnFill, !ro && _nodeItems.Length > 0))
            _picker.Open(new Vector2(p.X, p.Y + h), _nodeItems, -1, i =>
            {
                if (_edit == null) return;
                // Before any DisabledRule node, so the rule's file layout stays VTank's.
                int at = _edit.Conditions.FindIndex(c => c.NodeType == LootEditVocabulary.DisabledRule);
                var c = new LootEditConditionDto { NodeType = _nodeIds[i], Lines = DefaultsFor(_nodeIds[i]) };
                if (at < 0) _edit.Conditions.Add(c); else _edit.Conditions.Insert(at, c);
                ClearFields();
            }, 260);
        NextLine(p, h + 4);
    }

    private static void FieldLabel(ImDrawListPtr dl, Vector2 p, float h, string text) =>
        dl.AddText(new Vector2(p.X, p.Y + (h - ImGuiNET.ImGui.GetFontSize()) * 0.5f), Mute, text);

    private List<string> DefaultsFor(int nodeType) =>
        _nodes.TryGetValue(nodeType, out LootEditNodeTypeDto? n) ? new List<string>(n.Defaults) : new List<string>();

    /// <summary>One condition: [type] [editor] [raw][^][v][x], raw lines below. Non-zero when the list changed.</summary>
    private int ConditionRow(LootEditRuleDto r, int i, float w, bool ro)
    {
        LootEditConditionDto c = r.Conditions[i];
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        const float h = 22, typeW = 150, tail = 18 * 3 + 22 + 8;
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        dl.AddRectFilled(p - new Vector2(2, 1), p + new Vector2(w + 2, h + 1), RowAlt, 3);

        _nodes.TryGetValue(c.NodeType, out LootEditNodeTypeDto? node);
        bool json = c.NodeType < 0;
        string typeName = json ? "JSON condition" : node?.Name ?? "Node " + c.NodeType.ToString(CultureInfo.InvariantCulture);
        bool canType = !ro && !json && node != null;
        if (PickerButton("##type", typeName, p, typeW, h, !canType))
            _picker.Open(new Vector2(p.X, p.Y + h), _nodeItems, Array.IndexOf(_nodeIds, c.NodeType), k =>
            {
                if (_edit == null || i >= _edit.Conditions.Count) return;
                LootEditConditionDto target = _edit.Conditions[i];
                if (target.NodeType == _nodeIds[k]) return;
                target.NodeType = _nodeIds[k];
                target.Lines = DefaultsFor(_nodeIds[k]);
                ClearFields();
            }, 260);

        float ex = p.X + typeW + 4;
        float editorW = Math.Max(60, w - typeW - 4 - tail);
        string editor = json ? "json" : node?.Editor ?? "raw";
        bool raw = editor == "raw" || _rawShown.Contains(i);
        if (!raw) TypedEditor(c, i, node!, editor, new Vector2(ex, p.Y), editorW, h, ro);
        else if (json)
        {
            dl.PushClipRect(new Vector2(ex, p.Y), new Vector2(ex + editorW, p.Y + h), true);
            dl.AddText(new Vector2(ex + 2, p.Y + (h - ImGuiNET.ImGui.GetFontSize()) * 0.5f), Text, c.Lines.Count > 0 ? c.Lines[0] : string.Empty);
            dl.PopClipRect();
        }
        else
            dl.AddText(new Vector2(ex + 2, p.Y + (h - ImGuiNET.ImGui.GetFontSize()) * 0.5f), Mute, "(raw data lines below)");

        // Tail: raw toggle, up, down, delete.
        float tx = p.X + w - tail + 8;
        bool rawToggle = !json && editor != "raw";
        if (IconButton("##raw", PhosphorIcons.Code, new Vector2(tx, p.Y + 3), new Vector2(16, 16), _rawShown.Contains(i) ? Teal : Mute, BtnFill,
                rawToggle, font: UiFont.Ui11))
        {
            if (!_rawShown.Remove(i)) _rawShown.Add(i);
        }
        SafeImGui.SetItemTooltip(rawToggle ? "Show the raw data lines (any key id, any value)" : "This condition is edited as raw lines");
        tx += 18;
        int result = 0;
        int prev = PrevVisible(r, i), next = NextVisible(r, i);
        if (IconButton("##cup", PhosphorIcons.ArrowUp, new Vector2(tx, p.Y + 3), new Vector2(16, 16), Mute, BtnFill, !ro && prev >= 0, font: UiFont.Ui11))
        { Swap(r, i, prev); result = 1; }
        ImGuiNET.ImGui.SetItemTooltip("Move up");
        tx += 18;
        if (IconButton("##cdn", PhosphorIcons.ArrowDown, new Vector2(tx, p.Y + 3), new Vector2(16, 16), Mute, BtnFill, !ro && next >= 0, font: UiFont.Ui11))
        { Swap(r, i, next); result = 1; }
        ImGuiNET.ImGui.SetItemTooltip("Move down");
        tx += 18;
        if (IconButton("##cdel", PhosphorIcons.X, new Vector2(tx, p.Y + 3), new Vector2(20, 16), Text, DeleteBg, !ro, font: UiFont.Ui11))
        {
            r.Conditions.RemoveAt(i);
            _rawShown.Clear();
            ClearFields();
            result = 1;
        }
        ImGuiNET.ImGui.SetItemTooltip("Remove condition");
        NextLine(p, h + 3);
        if (result != 0 || !raw || json) return result;

        // Raw lines, one text box each.
        List<string> labels = node?.Labels ?? new List<string>();
        for (int line = 0; line < c.Lines.Count; line++)
        {
            Vector2 rowStart = ImGuiNET.ImGui.GetCursorScreenPos();
            Vector2 lp = rowStart + new Vector2(typeW + 4, 0);
            string label = line < labels.Count ? labels[line] : "Line " + (line + 1).ToString(CultureInfo.InvariantCulture);
            float lw = 84;
            dl.PushClipRect(lp, lp + new Vector2(lw - 4, 20), true);
            dl.AddText(new Vector2(lp.X, lp.Y + (20 - ImGuiNET.ImGui.GetFontSize()) * 0.5f), Mute, label);
            dl.PopClipRect();
            ImGuiNET.ImGui.PushID(line);
            if (Field("##rawline", FieldFor(i, line), c.Lines[line], new Vector2(lp.X + lw, lp.Y),
                    Math.Max(60, w - typeW - 4 - lw - 26), string.Empty, ro, out string v, 20))
                c.Lines[line] = v;
            ImGuiNET.ImGui.PopID();
            NextLine(rowStart, 22);
        }
        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
        return 0;
    }

    private static int PrevVisible(LootEditRuleDto r, int i)
    {
        for (int j = i - 1; j >= 0; j--) if (r.Conditions[j].NodeType != LootEditVocabulary.DisabledRule) return j;
        return -1;
    }

    private static int NextVisible(LootEditRuleDto r, int i)
    {
        for (int j = i + 1; j < r.Conditions.Count; j++) if (r.Conditions[j].NodeType != LootEditVocabulary.DisabledRule) return j;
        return -1;
    }

    private void Swap(LootEditRuleDto r, int a, int b)
    {
        (r.Conditions[a], r.Conditions[b]) = (r.Conditions[b], r.Conditions[a]);
        bool ra = _rawShown.Remove(a), rb = _rawShown.Remove(b);
        if (ra) _rawShown.Add(b);
        if (rb) _rawShown.Add(a);
        ClearFields();
    }

    /// <summary>The typed editors: object class, key + value, key + pattern, one text, one number.</summary>
    private void TypedEditor(LootEditConditionDto c, int i, LootEditNodeTypeDto node, string editor, Vector2 p, float w, float h, bool ro)
    {
        while (c.Lines.Count < node.Lines) c.Lines.Add(node.Defaults.Count > c.Lines.Count ? node.Defaults[c.Lines.Count] : "0");
        switch (editor)
        {
            case "class":
                IdPicker("##class", c, 0, _classes, "class", p, w, h, ro);
                break;
            case "keyval":
            {
                ValueTable? named = NamedValueTable(node, c);
                float kw = w * (named != null ? 0.5f : 0.58f);
                IdPicker("##key", c, 1, KeyTable(node.KeyTable), node.KeyTable == "skill" ? "skill" : "key", p, kw - 2, h, ro);
                if (named != null)
                {
                    ValuePicker(c, i, named, new Vector2(p.X + kw + 2, p.Y), w - kw - 2, h, ro);
                    break;
                }
                string hint = node.Labels.Count > 0 ? node.Labels[0].ToLowerInvariant() : "value";
                if (Field("##val", FieldFor(i, 0), c.Lines[0], new Vector2(p.X + kw + 2, p.Y), w - kw - 2, hint, ro, out string v, h))
                    c.Lines[0] = v;
                SafeImGui.SetItemTooltip(node.Labels.Count > 0 ? node.Labels[0] : "Value");
                break;
            }
            case "keypattern":
            {
                float kw = w * 0.34f;
                IdPicker("##key", c, 1, KeyTable(node.KeyTable), "key", p, kw - 2, h, ro);
                if (Field("##pat", FieldFor(i, 0), c.Lines[0], new Vector2(p.X + kw + 2, p.Y), w - kw - 2, "pattern (regex)", ro, out string v, h))
                    c.Lines[0] = v;
                break;
            }
            case "text":
                if (Field("##text", FieldFor(i, 0), c.Lines[0], p, w, "pattern (regex)", ro, out string t, h)) c.Lines[0] = t;
                break;
            default:   // "value"
                if (Field("##value", FieldFor(i, 0), c.Lines[0], p, w, node.Labels.Count > 0 ? node.Labels[0].ToLowerInvariant() : "value", ro, out string n, h))
                    c.Lines[0] = n;
                break;
        }
    }

    private (string[] Names, int[] Ids) KeyTable(string table) => table switch
    {
        "long" => _longKeys,
        "double" => _doubleKeys,
        "string" => _stringKeys,
        "skill" => _skills,
        _ => (Array.Empty<string>(), Array.Empty<int>()),
    };

    /// <summary>A picker for an id line (object class, key, skill) showing the name; unknown ids show as "key N".</summary>
    private void IdPicker(string id, LootEditConditionDto c, int line, (string[] Names, int[] Ids) table, string what, Vector2 p, float w, float h, bool ro)
    {
        if (line >= c.Lines.Count) return;
        string raw = c.Lines[line].Trim();
        int sel = -1;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            sel = Array.IndexOf(table.Ids, value);
        string shown = sel >= 0 ? table.Names[sel] : what + " " + raw;
        if (PickerButton(id, shown, p, w, h, ro || table.Names.Length == 0))
        {
            LootEditConditionDto target = c;
            _picker.Open(new Vector2(p.X, p.Y + h), table.Names, sel, k =>
            {
                if (line < target.Lines.Count) target.Lines[line] = table.Ids[k].ToString(CultureInfo.InvariantCulture);
                ClearFields();
            }, Math.Max(220, w));
        }
        SafeImGui.SetItemTooltip(sel >= 0 ? table.Names[sel] : "Not in the list: use the raw lines button to type any id");
    }

    /// <summary>The value names for this condition's long key, or null (no table, or a RynthAi that sends none).</summary>
    private ValueTable? NamedValueTable(LootEditNodeTypeDto node, LootEditConditionDto c)
    {
        if (!node.NamedValues || node.KeyTable != "long" || c.Lines.Count < 2 || _valueTables.Count == 0) return null;
        return TryWhole(c.Lines[1], out int key) && _valueTables.TryGetValue(key, out ValueTable? t) ? t : null;
    }

    /// <summary>
    /// The value line picked by name ("Light Weapons (45)"); a number not in the
    /// list shows as "value N". "Other number..." opens the raw lines to type any value.
    /// </summary>
    private void ValuePicker(LootEditConditionDto c, int cond, ValueTable table, Vector2 p, float w, float h, bool ro)
    {
        if (c.Lines.Count == 0) return;
        string raw = c.Lines[0].Trim();
        int sel = TryWhole(raw, out int value) ? Array.IndexOf(table.Ids, value) : -1;
        string shown = sel >= 0 ? table.Items[sel] : "value " + raw;
        if (PickerButton("##valpick", shown, p, w, h, ro))
        {
            LootEditConditionDto target = c;
            _picker.Open(new Vector2(p.X, p.Y + h), table.Items, sel, k =>
            {
                if (k >= table.Ids.Length) { _rawShown.Add(cond); return; }   // Other number...
                if (target.Lines.Count > 0) target.Lines[0] = table.Ids[k].ToString(CultureInfo.InvariantCulture);
                ClearFields();
            }, Math.Max(220, w));
        }
        string tip = sel >= 0 ? table.Items[sel] : "Not in the list: pick \"" + OtherNumber + "\" to type any number";
        if (table.Flags) tip += "\nFlags: other combinations are typed as a number";
        SafeImGui.SetItemTooltip(tip);
    }

    /// <summary>A whole number: "45", "45.0" (a buffed-long value written as a double).</summary>
    private static bool TryWhole(string? raw, out int value)
    {
        string s = (raw ?? string.Empty).Trim();
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return true;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
            && d == Math.Floor(d) && d >= int.MinValue && d <= int.MaxValue)
        {
            value = (int)d;
            return true;
        }
        value = 0;
        return false;
    }

    /// <summary>A button that opens a picker (a caret on the right). True when clicked.</summary>
    private static bool PickerButton(string id, string text, Vector2 p, float w, float h, bool disabled)
    {
        bool clicked = Button(id, text, p, new Vector2(w, h), disabled ? Mute : Text, BtnFill, !disabled, leftAlign: true);
        if (!disabled)
            PhosphorIcons.DrawCentered(ImGuiNET.ImGui.GetWindowDrawList(), ImGuiFonts.Get(UiFont.Ui11), PhosphorIcons.CaretDown,
                new Vector2(p.X + w - 16, p.Y), new Vector2(14, h), Mute);
        return clicked;
    }

    private FieldBuffer FieldFor(int cond, int line)
    {
        if (!_fields.TryGetValue((cond, line), out FieldBuffer? buf))
            _fields[(cond, line)] = buf = new FieldBuffer();
        return buf;
    }

    /// <summary>A text box bound to <paramref name="value"/>. True (with the new text) when it was typed in.</summary>
    private static bool Field(string id, FieldBuffer buf, string value, Vector2 pos, float width, string hint, bool ro, out string typed, float height)
    {
        if (!buf.Active && !ReferenceEquals(buf.Shown, value))
        {
            WriteUtf8(buf.Bytes, value);
            buf.Shown = value;
        }
        bool edited = TextBox(id, buf.Bytes, pos, width, hint, out buf.Active,
            ro ? ImGuiInputTextFlags.ReadOnly : ImGuiInputTextFlags.None, height);
        if (edited && !ro)
        {
            typed = Utf8(buf.Bytes);
            buf.Shown = typed;
            return true;
        }
        typed = value;
        return false;
    }
}
