// ============================================================================
//  RynthCore.Engine - ImGui/Panels/MetaFace.List.cs
//  The Meta panel's rule list (readability pass, 2026-10-11):
//
//    [file v][refresh][Save As]                                   Saved
//    [Visual][Source][Schedule] | [x Meta][x Debug]
//    [Filter rules...                   ][Collapse rules][collapse states]
//         Condition                     | Action
//    v Hunting  7 rules                                        current
//      [x] 1  Always                     | None
//    ----------------------------------------------------------------
//    v [x] 2  Landblock 5C45             | /vt opt set enablecombat true
//                                        | Nav Tusker_Abode
//    ----------------------------------------------------------------
//    > [x] 3  Expr getcharintprop[25]<30 and not Spell 512 >= 305... | -> ReBuff
//    State: [Hunting                    v][target][+ New Rule]
//
//  - A rule shows all of itself: every condition of an All / Any (joined by
//    "and" / "or", nested lists indented under "and any of"), every action of
//    a DoAll, one per line; words, values, states and problems in their own
//    colours (MetaRowText).
//  - Rules fold: a rule of more than one line has a caret in its gutter that
//    folds it to one line (conditions joined by and / or, actions by " · ";
//    hovering shows all of it). A state's right-click menu folds or opens its
//    rules; "Collapse rules" / "Expand rules" in the toolbar does every rule.
//    A rule's fold follows its content (state + text), so it keeps through
//    snapshots and moves; editing a rule shows it as the toolbar has rules.
//  - Lines between rules and down the Condition | Action split (drag it in
//    the header); every other rule a different shade. The fold caret, the
//    enable box and the rule's number (its place in the state: rules are
//    checked in order) sit in a narrow gutter.
//  - Move up / down, duplicate and delete appear on the row under the mouse
//    (delete takes a second click), and on right-click with Edit and Enable.
//    Click a rule to edit it; drag it to move it within its state (a teal
//    line shows where it lands; the list scrolls at its edges; Esc cancels).
//    A drop is the same move_up / move_down commands the arrows send.
//  - A named rule (MetaRuleDto.Name, set in the editor) shows its name in
//    bold on a line of its own over its condition and actions; folded, the
//    name stands in for the condition. Unnamed rules show as before.
//  - The current state's header is marked with how long the meta has been in
//    it ("current · 42 s", RynthAi's seconds in state, counted on between
//    snapshots) and, while one is set, the Watchdog's countdown; the state
//    picker shows the seconds too. The target button scrolls to the state.
//    A current state with no rules gets a note (the meta can't do anything).
//  - The filter matches the rules' text and state names; moving rules waits
//    until it is cleared (a hidden neighbour would make a move look like
//    nothing happened).
//  - Only rows on screen are drawn, so a meta of hundreds of rules scrolls
//    as cheaply as a small one.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.ImGuiBackend.Hud;
using RynthCore.Engine.UI.Data;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed partial class MetaFace
{
    // ── Rule text colours (MetaTone) and list lines ──────────────────────
    private static readonly uint ToneLabel = C(0xFF8FA9C2), ToneCode = C(0xFF9CC8FF), ToneJoin = C(0xFF4FC3CF), CurrentHdr = C(0xFF0D2B36), // Every other rule a clearly different shade (Tom, 2026-10-11: the old pair was hard to tell apart).
        StripeA = C(0xFF121B25), StripeB = C(0xFF1C2B3A);
    private static uint RowLine => RynthTheme.WithAlpha(RynthTheme.Border, 0x70);
    private static uint ColLine => RynthTheme.WithAlpha(RynthTheme.Border, 0x58);
    private static uint GuideLine => RynthTheme.WithAlpha(RynthTheme.Border, 0x60);

    private static uint ToneColor(MetaTone tone) => tone switch
    {
        MetaTone.Label => ToneLabel,
        MetaTone.State => Amber,
        MetaTone.Code => ToneCode,
        MetaTone.Join => ToneJoin,
        MetaTone.Dim => Faded(Mute),
        MetaTone.Warn => Red,
        _ => TextDim,
    };

    // ── Per-rule text, rebuilt with the snapshot (RebuildListText) ───────
    private MetaLine[][] _condLines = Array.Empty<MetaLine[]>(), _actLines = Array.Empty<MetaLine[]>();
    private MetaSeg[][] _condCompact = Array.Empty<MetaSeg[]>(), _actCompact = Array.Empty<MetaSeg[]>();
    private string[] _searchText = Array.Empty<string>();
    private bool _spellNamesPending;   // a spell showed as a number while the spell table loads

    // ── List state ───────────────────────────────────────────────────────
    // Folded rules: _compact is the toolbar's choice for every rule (saved per PC); _ruleFlip holds the
    // rules folded the other way (a rule's caret, a state's menu), by _ruleKey (state + text).
    private bool _compact = RynthCore.Engine.UI.PanelColumnStore.Get("Meta.compact", 0f) > 0.5f;
    private readonly HashSet<string> _ruleFlip = new();
    private string[] _ruleKey = Array.Empty<string>();
    private int[] _fullLines = Array.Empty<int>();   // a rule's lines unfolded (its name's line included)
    private readonly byte[] _filterBuf = new byte[128];
    private string _filter = string.Empty;           // lower case, trimmed
    private bool[] _filterHit = Array.Empty<bool>();
    private string? _scrollToState;                  // the target button: scroll to this state's header
    private int _ctxRule = -1;                       // right-clicked rule (its menu)

    // Dragging a rule within its state: the rule (index) and its place when picked up, the gap it
    // would land in (0..count), and this frame's rows of that state (place, top, bottom on screen).
    private int _dragIndex = -1, _dragFromGi, _dragGap = -1, _dragStartFrame;
    private string _dragState = string.Empty;
    private bool _dragSwallowClick;   // the release that ends a drag isn't a click (no editor)
    private readonly List<(int Gi, float Top, float Bottom)> _dragSlots = new();
    private string? _ctxState;                       // right-clicked state header (its menu)
    private bool _openRuleMenu, _openStateMenu;

    // Layout of the frame's rows (LayoutRows), and the list's inner width last frame (less its
    // scrollbar), which the column header above the list lines up with.
    private float _lineH, _slotW, _gutterW, _numW, _listInnerW;
    private const float RowPad = 3;

    private void RebuildListText(List<MetaRuleDto> rules)
    {
        var ctx = new MetaTextContext
        {
            SpellName = id => PortalSpellTable.TryGetName(id, out string n) ? n : null,
            States = _statesOnly,
            RouteExists = name => MetaVocabulary.HasRoute(_data, name),
        };
        int n = rules.Count;
        _condLines = new MetaLine[n][];
        _actLines = new MetaLine[n][];
        _condCompact = new MetaSeg[n][];
        _actCompact = new MetaSeg[n][];
        _searchText = new string[n];
        _ruleKey = new string[n];
        _fullLines = new int[n];
        for (int i = 0; i < n; i++)
        {
            MetaRuleDto r = rules[i];
            _condLines[i] = MetaRowText.ConditionLines(r, ctx);
            _actLines[i] = MetaRowText.ActionLines(r, ctx);
            _condCompact[i] = MetaRowText.ConditionCompact(r, ctx);
            _actCompact[i] = MetaRowText.ActionCompact(r, ctx);
            _searchText[i] = (r.Name + "\n" + MetaRowText.Plain(_condCompact[i]) + "\n" + MetaRowText.Plain(_actCompact[i])).ToLowerInvariant();
            _ruleKey[i] = r.State + "\u0001" + _searchText[i];
            _fullLines[i] = Math.Max(1, Math.Max(_condLines[i].Length, _actLines[i].Length)) + (r.Name.Length > 0 ? 1 : 0);
        }
        // Names appear once the client's spell table has been read (in the background).
        _spellNamesPending = ctx.MissedSpellName && !PortalSpellTable.Ready;
        if (_spellNamesPending) PortalSpellTable.EnsureLoadQueued();
        RefilterRules();
    }

    private void RefilterRules()
    {
        List<MetaRuleDto> rules = _data.Rules;
        if (_filterHit.Length != rules.Count) _filterHit = new bool[rules.Count];
        for (int i = 0; i < rules.Count && i < _searchText.Length; i++)
            _filterHit[i] = _filter.Length == 0
                || _searchText[i].Contains(_filter, StringComparison.Ordinal)
                || rules[i].State.Contains(_filter, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsCurrent(string state) => string.Equals(state, _data.CurrentState, StringComparison.OrdinalIgnoreCase);

    // ── Folded rules ─────────────────────────────────────────────────────

    /// <summary>A rule of more than one line can fold to one.</summary>
    private bool Folds(int i) => i >= 0 && i < _fullLines.Length && _fullLines[i] > 1;

    private bool IsFolded(int i) => Folds(i) && (_compact ^ _ruleFlip.Contains(_ruleKey[i]));

    private void SetFolded(int i, bool folded)
    {
        if (!Folds(i)) return;
        if (folded == _compact) _ruleFlip.Remove(_ruleKey[i]);
        else _ruleFlip.Add(_ruleKey[i]);
    }

    /// <summary>The toolbar: every rule folded or every rule open.</summary>
    private void SetAllFolded(bool folded)
    {
        _compact = folded;
        _ruleFlip.Clear();
        RynthCore.Engine.UI.PanelColumnStore.Set("Meta.compact", folded ? 1f : 0f);
    }

    private bool AnyRuleOpen()
    {
        for (int i = 0; i < _fullLines.Length; i++)
            if (Folds(i) && !IsFolded(i)) return true;
        return false;
    }

    private void SetStateFolded(string state, bool folded)
    {
        foreach ((string s, List<int> members) in _groups)
            if (string.Equals(s, state, StringComparison.Ordinal))
                foreach (int i in members) SetFolded(i, folded);
    }

    // =====================================================================
    //  List view
    // =====================================================================

    private void ListView(Vector2 origin, Vector2 size)
    {
        if (_spellNamesPending && PortalSpellTable.Ready) RebuildCaches();
        float w = size.X;
        LoadSaveBar(w);
        Separator(w);
        ModeRow(w, source: false);
        Separator(w);
        FilterRow(w);

        // A note over the list: the editor closed under a meta switch, or the meta can't do anything.
        string note = string.Empty;
        if (_listNote.Length > 0 && NowMs - _listNoteAt < 8000) note = _listNote;
        else if (_data.Rules.Count > 0 && !HasRulesIn(_data.CurrentState))
            note = $"The current state, {_data.CurrentState}, has no rules: the meta does nothing until something changes the state.";
        if (note.Length > 0)
        {
            ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui9));
            ImGuiNET.ImGui.PushTextWrapPos(ImGuiNET.ImGui.GetCursorPosX() + w);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Amber);
            ImGuiNET.ImGui.TextUnformatted(note);
            ImGuiNET.ImGui.PopStyleColor();
            ImGuiNET.ImGui.PopTextWrapPos();
            ImGuiNET.ImGui.PopFont();
        }

        // The Condition | Action header stays put above the scrolling list.
        LayoutRows();
        if (_groups.Count > 0) ColumnHeader(w, TextWidth(_listInnerW > 0 ? _listInnerW : w));

        const float bottomH = 24;
        float listH = Math.Max(40, origin.Y + size.Y - ImGuiNET.ImGui.GetCursorScreenPos().Y - bottomH - 9);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ChildBg, 0);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 0));
        ImGuiNET.ImGui.BeginChild("##meta_rules", new Vector2(w, listH));
        RuleList(ImGuiNET.ImGui.GetContentRegionAvail().X);
        RuleMenus();
        ImGuiNET.ImGui.EndChild();
        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.PopStyleColor();

        Separator(w);
        BottomBar(w);
    }

    private bool HasRulesIn(string state)
    {
        foreach ((string s, List<int> _) in _groups)
            if (string.Equals(s, state, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // [Filter rules...      x][Compact][collapse / expand all]
    private void FilterRow(float w)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        bool anyRuleOpen = AnyRuleOpen();
        string compactLabel = (anyRuleOpen ? "Collapse rules" : "Expand rules") + "##meta_compact";
        float compactW = Math.Max(ImGuiNET.ImGui.CalcTextSize("Collapse rules").X, ImGuiNET.ImGui.CalcTextSize("Expand rules").X) + 16;
        const float iconW = 26;
        float boxW = Math.Max(60, w - compactW - iconW - 8);

        ImGuiNET.ImGui.SetCursorScreenPos(p);
        ImGuiNET.ImGui.SetNextItemAllowOverlap();   // the clear button sits inside the box
        if (TextBox("##meta_filter", _filterBuf, boxW, PhosphorIcons.MagnifyingGlass + "  Filter rules...",
                ImGuiInputTextFlags.EscapeClearsAll, out _))
            SetFilter(Utf8(_filterBuf));
        if (_filter.Length > 0)
        {
            if (GlyphButton("##filter_x", PhosphorIcons.X, new Vector2(p.X + boxW - 20, p.Y + 2), new Vector2(18, 18), Mute, TextDim))
            {
                Array.Clear(_filterBuf);
                SetFilter(string.Empty);
            }
            ImGuiNET.ImGui.SetItemTooltip("Clear the filter (Esc in the box)");
        }

        float x = p.X + boxW + 4;
        float toggleX = x;
        if (Toggle(compactLabel, ref x, p.Y, !anyRuleOpen)) SetAllFolded(anyRuleOpen);
        x = toggleX + compactW + 4;   // the button keeps one width whichever label it shows
        SafeImGui.SetItemTooltip(anyRuleOpen
            ? "Fold every rule to one line (hover one to see all of it; its caret opens it again)."
            : "Show every rule in full: each condition and action on its own line.");

        bool anyOpen = false;
        foreach ((string s, List<int> _) in _groups) anyOpen |= !_collapsed.Contains(s);
        if (IconButton("##collapse_all", anyOpen ? PhosphorIcons.ArrowsInLineVertical : PhosphorIcons.ArrowsOutLineVertical,
                new Vector2(x, p.Y), new Vector2(iconW, 22), TextDim, BtnFill, BtnBord, font: UiFont.Ui14))
        {
            if (anyOpen) foreach ((string s, List<int> _) in _groups) _collapsed.Add(s);
            else _collapsed.Clear();
        }
        SafeImGui.SetItemTooltip(anyOpen ? "Collapse every state" : "Expand every state");

        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + 22));
        ImGuiNET.ImGui.Dummy(new Vector2(0, 4));
    }

    private void SetFilter(string text)
    {
        _filter = text.Trim().ToLowerInvariant();
        RefilterRules();
    }

    // =====================================================================
    //  Rules
    // =====================================================================

    private void RuleList(float w)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        if (_groups.Count == 0)
        {
            ImGuiNET.ImGui.Dummy(new Vector2(0, 4));
            ImGuiNET.ImGui.TextColored(RynthTheme.VecOf(RynthTheme.MuteAlt), "  No rules. Click New Rule to add one.");
            return;
        }

        List<MetaRuleDto> rules = _data.Rules;
        bool filtering = _filter.Length > 0;
        _dragSlots.Clear();
        // While a rule is dragged, nothing else in the list highlights or shows its tools.
        bool listHot = ImGuiNET.ImGui.IsWindowHovered() && _dragIndex < 0;
        _listInnerW = w;
        float textW = TextWidth(w);

        bool first = true;
        int shown = 0;
        for (int g = 0; g < _groups.Count; g++)
        {
            (string state, List<int> members) = _groups[g];
            int hits = members.Count;
            if (filtering)
            {
                hits = 0;
                foreach (int i in members) if (i < _filterHit.Length && _filterHit[i]) hits++;
                if (hits == 0) continue;
            }
            if (_scrollToState != null && string.Equals(_scrollToState, state, StringComparison.OrdinalIgnoreCase))
                _collapsed.Remove(state);
            bool collapsed = !filtering && _collapsed.Contains(state);

            if (!first) ImGuiNET.ImGui.Dummy(new Vector2(0, 6));
            first = false;
            StateHeader(g, state, members.Count, hits, filtering, collapsed, w, listHot);
            if (collapsed) continue;

            int stripe = 0;   // counts the rows shown, so a filter never puts two of a shade together
            for (int gi = 0; gi < members.Count; gi++)
            {
                int index = members[gi];
                if (index >= rules.Count) break;
                if (filtering && !(index < _filterHit.Length && _filterHit[index])) continue;
                shown++;
                if (RuleRow(rules[index], index, gi, members.Count, stripe++ % 2 == 1, w, textW, listHot, filtering)) return;   // the list changed: redraw next frame
            }
        }
        _scrollToState = null;   // its header wasn't drawn (filtered out): don't jump there later
        DragUpdate();
        if (filtering && shown == 0 && first)
        {
            ImGuiNET.ImGui.Dummy(new Vector2(0, 4));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Mute);
            ImGuiNET.ImGui.TextUnformatted($"  No rule or state matches \"{Utf8(_filterBuf).Trim()}\".");
            ImGuiNET.ImGui.PopStyleColor();
        }
        ImGuiNET.ImGui.Dummy(new Vector2(0, 6));
    }

    /// <summary>This frame's row layout: line height, the and / or slot, the gutter (enable box + rule number).</summary>
    private void LayoutRows()
    {
        _lineH = ImGuiNET.ImGui.GetTextLineHeight() + 2;
        _slotW = ImGuiNET.ImGui.CalcTextSize("and ").X + 2;
        int most = 1;
        foreach ((string _, List<int> m) in _groups) most = Math.Max(most, m.Count);
        _numW = ImGuiNET.ImGui.CalcTextSize(most >= 100 ? "999" : most >= 10 ? "99" : "9").X;
        _gutterW = 36 + _numW + 6;   // fold caret, enable box, number
    }

    /// <summary>The condition + action text width of a row <paramref name="rowW"/> wide.</summary>
    private float TextWidth(float rowW) => Math.Max(40, rowW - _gutterW - 4);

    // Condition | Action, over the text columns; drag the line between them (saved per PC).
    private void ColumnHeader(float w, float textW)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        ImFontPtr bold = ImGuiFonts.Get(UiFont.UiBold11);
        Vector2 hp = ImGuiNET.ImGui.GetCursorScreenPos();
        const float hh = 18;
        float tx = hp.X + _gutterW;
        float split = textW * _condFrac;
        float hy = hp.Y + (hh - bold.FontSize) * 0.5f;
        dl.AddText(bold, bold.FontSize, new Vector2(tx + 4, hy), Mute, "Condition");
        dl.AddText(bold, bold.FontSize, new Vector2(tx + split + 6, hy), Mute, "Action");
        if (FaceKit.ColumnDivider("##meta_split", tx + split, hp.Y, hh, ref split, 40,
                _ => RynthCore.Engine.UI.PanelColumnStore.Set("Meta.condFrac", _condFrac)))
            _condFrac = Math.Clamp(split / textW, 0.15f, 0.85f);
        ImGuiNET.ImGui.Dummy(new Vector2(w, hh));
        dl.AddLine(new Vector2(hp.X, hp.Y + hh - 0.5f), new Vector2(hp.X + w, hp.Y + hh - 0.5f), BtnBord);
    }

    /// <summary>A state's header band: caret, name, rule count; "current" when it is the meta's state;
    /// red while one of its rules fires. Click folds it; right-click for its menu; + adds a rule.</summary>
    private void StateHeader(int g, string state, int count, int hits, bool filtering, bool collapsed, float w, bool listHot)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        ImFontPtr bold = ImGuiFonts.Get(UiFont.UiBold11);
        bool current = IsCurrent(state);
        bool firing = _groupFiring[g];
        float hh = bold.FontSize + 9;
        Vector2 hp = ImGuiNET.ImGui.GetCursorScreenPos();
        if (_scrollToState != null && string.Equals(_scrollToState, state, StringComparison.OrdinalIgnoreCase))
        {
            ImGuiNET.ImGui.SetScrollHereY(0f);
            _scrollToState = null;
        }

        ImGuiNET.ImGui.PushID(-1 - g);   // rows use their rule index
        ImGuiNET.ImGui.SetNextItemAllowOverlap();
        if (ImGuiNET.ImGui.InvisibleButton("##hdr", new Vector2(w, hh)) && !filtering)
        {
            if (collapsed) _collapsed.Remove(state); else _collapsed.Add(state);
        }
        if (ImGuiNET.ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            _ctxState = state;
            _openStateMenu = true;
        }
        bool hot = listHot && ImGuiNET.ImGui.IsMouseHoveringRect(hp, hp + new Vector2(w, hh));
        if (ImGuiNET.ImGui.IsItemHovered()) ImGuiNET.ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        uint bg = current ? CurrentHdr : StateHdrA;
        dl.AddRectFilled(hp, hp + new Vector2(w, hh), hot ? Lighten(bg) : bg, 3);
        if (current) dl.AddRectFilled(hp, hp + new Vector2(3, hh), Teal, 1);
        uint nameColor = firing ? Red : Amber;
        float ty = hp.Y + (hh - bold.FontSize) * 0.5f;
        dl.AddText(bold, bold.FontSize, new Vector2(hp.X + 7, ty), Faded(nameColor),
            collapsed ? PhosphorIcons.CaretRight : PhosphorIcons.CaretDown);
        float nx = hp.X + 22;
        dl.AddText(bold, bold.FontSize, new Vector2(nx, ty), nameColor, state);
        nx += bold.CalcTextSizeA(bold.FontSize, float.MaxValue, 0, state).X + 8;
        string countText = filtering ? $"{hits} of {count}" : count == 1 ? "1 rule" : $"{count} rules";
        if (firing) countText += "  • firing";
        float fy = hp.Y + (hh - ImGuiNET.ImGui.GetFontSize()) * 0.5f;
        dl.AddText(new Vector2(nx, fy), firing ? Red : Faded(Mute), countText);

        // Right: [watchdog] [current · 42 s] [+]
        float rx = hp.X + w - 4;
        if (hot)
        {
            const float pw = 20;
            rx -= pw;
            if (GlyphButton("##add", PhosphorIcons.Plus, new Vector2(rx, hp.Y + (hh - 18) * 0.5f), new Vector2(pw, 18), Green, Lighten(Green)))
                OpenEditor(-1, new MetaRuleDto { State = state, Action = 1 });
            SafeImGui.SetItemTooltip($"New rule in {state}");
            rx -= 4;
        }
        if (current)
        {
            string tag = _stateClock.Known ? "current  \u00B7  " + MetaStateClock.Format(_stateClock.SecondsInState(NowMs)) : "current";
            ImFontPtr small = ImGuiFonts.Get(UiFont.Ui9);
            Vector2 ts = small.CalcTextSizeA(small.FontSize, float.MaxValue, 0, tag);
            Vector2 a = new(rx - ts.X - 10, hp.Y + (hh - ts.Y - 4) * 0.5f), b = new(rx, a.Y + ts.Y + 4);
            dl.AddRectFilled(a, b, BtnOn, 8);
            dl.AddRect(a, b, Teal, 8);
            dl.AddText(small, small.FontSize, new Vector2(a.X + 5, a.Y + 2), Teal, tag);
            if (_stateClock.Known && ImGuiNET.ImGui.IsMouseHoveringRect(a, b))
                ImGuiNET.ImGui.SetTooltip("The meta's current state, and how long it has been in it (what \"In state >= N s\" rules count).");
            rx = a.X - 6;

            // The Watchdog, while one is set: it sends the meta to its state when the time runs out
            // (unless you've moved far enough).
            double wd = _stateClock.WatchdogLeft(NowMs, out string wdState);
            if (wd >= 0)
            {
                string wtag = PhosphorIcons.Hourglass + " " + MetaStateClock.Format(wd) + (wdState.Length > 0 ? "  \u2192 " + wdState : "");
                Vector2 ws = small.CalcTextSizeA(small.FontSize, float.MaxValue, 0, wtag);
                Vector2 wa = new(rx - ws.X - 10, a.Y), wb = new(rx, b.Y);
                if (wa.X > nx + ImGuiNET.ImGui.CalcTextSize(countText).X + 8)   // only when it doesn't cover the count
                {
                    dl.AddRectFilled(wa, wb, C(0xFF2A2210), 8);
                    dl.AddRect(wa, wb, Amber, 8);
                    dl.AddText(small, small.FontSize, new Vector2(wa.X + 5, wa.Y + 2), Amber, wtag);
                    if (ImGuiNET.ImGui.IsMouseHoveringRect(wa, wb))
                        SafeImGui.SetTooltip("Watchdog: unless you move far enough, the meta goes to " +
                            (wdState.Length > 0 ? wdState : "its state") + " when this runs out.");
                }
            }
        }
        ImGuiNET.ImGui.PopID();
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(hp.X, hp.Y + hh));
        ImGuiNET.ImGui.Dummy(new Vector2(0, 0));
    }

    /// <summary>One rule. True when a click changed the list (the caller stops drawing this frame).</summary>
    private bool RuleRow(MetaRuleDto rule, int index, int gi, int groupCount, bool odd, float w, float textW, bool listHot, bool filtering)
    {
        bool named = rule.Name.Length > 0;
        bool folded = IsFolded(index);
        int lineCount = folded ? 1 : index < _fullLines.Length ? _fullLines[index] : 1;   // unfolded: the name has a line of its own
        float h = RowPad * 2 + lineCount * _lineH;
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        if (_dragIndex >= 0 && string.Equals(rule.State, _dragState, StringComparison.Ordinal))
            _dragSlots.Add((gi, p.Y, p.Y + h));   // on screen or not: the drop gap needs every row of the state
        bool dragged = _dragIndex == index;
        bool arming = _confirmDeleteIndex == index && _confirmDelete != null && NowMs < _confirmDeleteUntil
                      && MetaRuleEdit.SameContent(_confirmDelete, rule);
        if (!arming && !ImGuiNET.ImGui.IsRectVisible(p, p + new Vector2(w, h)))
        {
            ImGuiNET.ImGui.Dummy(new Vector2(w, h));   // off screen: keep its space, draw nothing
            return false;
        }

        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        bool firing = rule.LastFiredMs < 1500;
        bool hot = listHot && ImGuiNET.ImGui.IsMouseHoveringRect(p, p + new Vector2(w, h));
        uint bg = firing ? Fired : odd ? StripeB : StripeA;
        if (hot) bg = Lighten(bg);
        dl.AddRectFilled(p, p + new Vector2(w, h), bg);
        if (firing) dl.AddRectFilled(p, p + new Vector2(3, h), Red);

        ImGuiNET.ImGui.PushID(index);
        bool changed = false;

        // The whole row: click edits, right-click opens its menu. The buttons below sit on top of it.
        ImGuiNET.ImGui.SetCursorScreenPos(p);
        ImGuiNET.ImGui.SetNextItemAllowOverlap();
        bool edit = ImGuiNET.ImGui.InvisibleButton("##row", new Vector2(w, h));
        bool rowTip = ImGuiNET.ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip) && _dragIndex < 0;
        // Pressed and pulled a few pixels: a drag (moves stay off while filtering, like the arrows).
        if (_dragIndex < 0 && !filtering && ImGuiNET.ImGui.IsItemActive() && ImGuiNET.ImGui.IsMouseDragging(ImGuiMouseButton.Left, 6f))
        {
            _dragIndex = index;
            _dragState = rule.State;
            _dragFromGi = gi;
            _dragGap = gi;
            _dragSwallowClick = true;
            _dragStartFrame = ImGuiNET.ImGui.GetFrameCount();   // rows above it weren't recorded this frame
            dragged = true;
        }
        if (ImGuiNET.ImGui.IsItemHovered()) ImGuiNET.ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (ImGuiNET.ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            _ctxRule = index;
            _openRuleMenu = true;
        }

        // Gutter: the fold caret (rules of more than one line), the enable box, the rule's place in its state.
        float firstLineY = p.Y + RowPad;
        float textY = firstLineY + (_lineH - ImGuiNET.ImGui.GetFontSize()) * 0.5f;
        bool caretHot = false;
        if (Folds(index))
        {
            if (GlyphButton("##fold", folded ? PhosphorIcons.CaretRight : PhosphorIcons.CaretDown,
                    new Vector2(p.X + 2, firstLineY + (_lineH - 16) * 0.5f), new Vector2(14, 16), Faded(TextDim), Teal))
                SetFolded(index, !folded);
            caretHot = ImGuiNET.ImGui.IsItemHovered();
            SafeImGui.SetItemTooltip(folded ? "Show all of this rule" : "Fold this rule to one line");
        }
        if (GlyphButton("##en", rule.Enabled ? PhosphorIcons.CheckSquare : PhosphorIcons.Square,
                new Vector2(p.X + 16, firstLineY + (_lineH - 16) * 0.5f), new Vector2(16, 16), rule.Enabled ? Green : Mute, TextDim))
        {
            rule.Enabled = !rule.Enabled;
            SendEdit(new MetaCmd { Op = "set_rule_enabled", Index = index, Value = rule.Enabled ? "true" : "false" });
        }
        SafeImGui.SetItemTooltip(rule.Enabled ? "Enabled: click to disable" : "Disabled: click to enable");
        if ((hot && !filtering) || dragged)   // on hover the number becomes a grip: the row drags
            PhosphorIcons.DrawCentered(dl, ImGuiFonts.Get(UiFont.Ui11), PhosphorIcons.DotsSixVertical,
                new Vector2(p.X + 34, firstLineY), new Vector2(_numW + 4, _lineH), dragged ? Teal : Mute);
        else
        {
            string num = (gi + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            float numX = p.X + 36 + _numW - ImGuiNET.ImGui.CalcTextSize(num).X;
            dl.AddText(new Vector2(numX, textY), Faded(Mute), num);
        }

        // Name (its own line), condition | action text, and the line between them.
        float tx = p.X + _gutterW;
        float split = tx + textW * _condFrac;
        bool fade = !rule.Enabled || dragged;
        bool cut = false;
        float condX = tx + 4, condMax = split - 6, actX = split + 6, actMax = p.X + w - 6;
        float bodyY = textY;
        if (named && !folded)
        {
            cut |= DrawName(dl, condX, firstLineY, actMax, rule.Name, fade);
            bodyY += _lineH;
        }
        float lineTop = named && !folded ? p.Y + RowPad + _lineH : p.Y;
        dl.AddLine(new Vector2(split, lineTop), new Vector2(split, p.Y + h), ColLine);
        if (folded)
        {
            if (named) cut |= DrawName(dl, condX, firstLineY, condMax, rule.Name, fade);
            else if (index < _condCompact.Length) cut |= DrawSegs(dl, condX, textY, condMax, _condCompact[index], fade);
            if (index < _actCompact.Length) cut |= DrawSegs(dl, actX, textY, actMax, _actCompact[index], fade);
        }
        else
        {
            if (index < _condLines.Length)
                for (int i = 0; i < _condLines[index].Length; i++)
                    cut |= DrawLine(dl, condX, bodyY + i * _lineH, condMax, _condLines[index][i], fade);
            if (index < _actLines.Length)
                for (int i = 0; i < _actLines[index].Length; i++)
                    cut |= DrawLine(dl, actX, bodyY + i * _lineH, actMax, _actLines[index][i], fade);
        }

        // Row tools on the row under the mouse (and while delete waits for its second click).
        bool toolHot = false;
        if (hot || arming)
        {
            const float bs = 18, gap = 2;
            float delW = arming ? BtnW("Delete?", 50) : bs;
            float toolsW = bs * 3 + gap * 3 + delW;
            float bx = p.X + w - 4 - toolsW;
            float by = p.Y + Math.Max(1, RowPad + (_lineH - bs) * 0.5f);
            dl.AddRectFilled(new Vector2(bx - 4, by - 1), new Vector2(p.X + w - 1, by + bs + 1), bg, 3);
            bool canMove = !filtering;
            if (IconButton("##up", PhosphorIcons.ArrowUp, new Vector2(bx, by), new Vector2(bs, bs), Mute, BtnFill, BtnBord, enabled: canMove && gi > 0))
                changed |= Move(index, -1);
            toolHot |= ImGuiNET.ImGui.IsItemHovered();
            SafeImGui.SetItemTooltip(canMove ? "Move up" : "Clear the filter to move rules");
            bx += bs + gap;
            if (IconButton("##dn", PhosphorIcons.ArrowDown, new Vector2(bx, by), new Vector2(bs, bs), Mute, BtnFill, BtnBord,
                    enabled: canMove && gi < groupCount - 1))
                changed |= Move(index, +1);
            toolHot |= ImGuiNET.ImGui.IsItemHovered();
            SafeImGui.SetItemTooltip(canMove ? "Move down" : "Clear the filter to move rules");
            bx += bs + gap;
            if (IconButton("##dup", PhosphorIcons.Copy, new Vector2(bx, by), new Vector2(bs, bs), Mute, BtnFill, BtnBord))
                changed |= Duplicate(index, rule);
            toolHot |= ImGuiNET.ImGui.IsItemHovered();
            ImGuiNET.ImGui.SetItemTooltip("Duplicate rule");
            bx += bs + gap;
            if (arming
                    ? Button("Delete?##del", new Vector2(bx, by), new Vector2(delW, bs), TextDim, DelFill, Red)
                    : IconButton("##del", PhosphorIcons.Trash, new Vector2(bx, by), new Vector2(bs, bs), TextDim, DelFill, BtnBord))
            {
                // A rule with anything in it takes a second click within 3 s; an empty Never -> None one goes at once.
                if (arming || MetaRuleEdit.IsPlaceholder(rule)) changed |= Delete(index);
                else
                {
                    _confirmDelete = rule.Clone();
                    _confirmDeleteIndex = index;
                    _confirmDeleteUntil = NowMs + 3000;
                }
            }
            toolHot |= ImGuiNET.ImGui.IsItemHovered();
            SafeImGui.SetItemTooltip(arming ? "Click again to delete this rule" : "Delete rule");
        }

        dl.AddLine(new Vector2(p.X, p.Y + h - 0.5f), new Vector2(p.X + w, p.Y + h - 0.5f), RowLine);
        if (dragged) dl.AddRect(p + new Vector2(1, 1), p + new Vector2(w - 1, h - 1), Teal, 2f, ImDrawFlags.None, 1.5f);
        ImGuiNET.ImGui.PopID();
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + h));
        ImGuiNET.ImGui.Dummy(new Vector2(0, 0));

        if (changed)
        {
            RebuildCaches();
            return true;
        }
        if (rowTip && !toolHot && !caretHot && (folded || cut)) RuleTooltip(rule, index, gi);
        if (edit && !_dragSwallowClick) OpenEditor(index, rule);
        return false;
    }

    /// <summary>A rule's name in bold on the line at <paramref name="lineY"/>, cut with "…" at maxX.
    /// True when it didn't fit.</summary>
    private static bool DrawName(ImDrawListPtr dl, float x, float lineY, float maxX, string name, bool fade)
    {
        ImFontPtr bold = ImGuiFonts.Get(UiFont.UiBold11);
        float y = lineY + (ImGuiNET.ImGui.GetTextLineHeight() + 2 - bold.FontSize) * 0.5f;
        uint col = fade ? Faded(TextDim) : TextDim;
        float room = maxX - x;
        if (room <= 10) return true;
        if (bold.CalcTextSizeA(bold.FontSize, float.MaxValue, 0, name).X <= room)
        {
            dl.AddText(bold, bold.FontSize, new Vector2(x, y), col, name);
            return false;
        }
        // Too long: the longest start that fits with "…" (names are short; only cut rows on screen get here).
        int lo = 0, hi = name.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (bold.CalcTextSizeA(bold.FontSize, float.MaxValue, 0, name.Substring(0, mid) + "…").X <= room) lo = mid; else hi = mid - 1;
        }
        dl.AddText(bold, bold.FontSize, new Vector2(x, y), col, name.Substring(0, lo) + "…");
        return true;
    }

    /// <summary>Segments from x, cut with "…" at maxX. True when something didn't fit.</summary>
    private bool DrawSegs(ImDrawListPtr dl, float x, float y, float maxX, MetaSeg[] segs, bool fade)
    {
        foreach (MetaSeg s in segs)
        {
            if (s.Text.Length == 0) continue;
            uint col = ToneColor(s.Tone);
            if (fade) col = Faded(col);
            float tw = ImGuiNET.ImGui.CalcTextSize(s.Text).X;
            if (x + tw > maxX)
            {
                if (maxX - x > 10) dl.AddText(new Vector2(x, y), col, Fit(s.Text, maxX - x));
                return true;
            }
            dl.AddText(new Vector2(x, y), col, s.Text);
            x += tw;
        }
        return false;
    }

    /// <summary>One full-form line: guides for its nesting, its and / or slot, its text.</summary>
    private bool DrawLine(ImDrawListPtr dl, float colX, float y, float maxX, MetaLine line, bool fade)
    {
        for (int k = 0; k < line.Depth; k++)
        {
            float gx = colX + k * _slotW + 4;
            dl.AddLine(new Vector2(gx, y - 1), new Vector2(gx, y + _lineH - 1), GuideLine);
        }
        float x = colX + line.Depth * _slotW;
        if (line.Slot)
        {
            if (line.Join.Length > 0)
            {
                float jw = ImGuiNET.ImGui.CalcTextSize(line.Join).X;
                dl.AddText(new Vector2(x + _slotW - 4 - jw, y), fade ? Faded(ToneJoin) : ToneJoin, line.Join);
            }
            x += _slotW;
        }
        return DrawSegs(dl, x, y, maxX, line.Segs, fade);
    }

    /// <summary>All of a rule (it didn't fit, or the list is compact), as the full form draws it.</summary>
    private void RuleTooltip(MetaRuleDto rule, int index, int gi)
    {
        if (!ImGuiNET.ImGui.BeginTooltip()) return;
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4, 1));
        if (rule.Name.Length > 0)
        {
            ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.UiBold11));
            Colored(rule.Name, TextDim);
            ImGuiNET.ImGui.PopFont();
        }
        Colored($"{rule.State}  ·  rule {gi + 1}{(rule.Enabled ? "" : "  ·  disabled")}", Faded(Mute));
        ImGuiNET.ImGui.Dummy(new Vector2(0, 3));
        Colored("If", Amber);
        if (index < _condLines.Length) foreach (MetaLine l in _condLines[index]) TipLine(l);
        ImGuiNET.ImGui.Dummy(new Vector2(0, 3));
        Colored("Do", Amber);
        if (index < _actLines.Length) foreach (MetaLine l in _actLines[index]) TipLine(l);
        ImGuiNET.ImGui.Dummy(new Vector2(0, 3));
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui9));
        Colored("Click to edit; drag to move; right-click for more.", Faded(Mute));
        ImGuiNET.ImGui.PopFont();
        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.EndTooltip();
    }

    private void TipLine(MetaLine l)
    {
        float x0 = ImGuiNET.ImGui.GetCursorPosX() + 12 + l.Depth * _slotW;
        if (l.Slot)
        {
            if (l.Join.Length > 0)
            {
                ImGuiNET.ImGui.SetCursorPosX(x0 + _slotW - 4 - ImGuiNET.ImGui.CalcTextSize(l.Join).X);
                Colored(l.Join, ToneJoin);
                ImGuiNET.ImGui.SameLine(0, 0);
            }
            ImGuiNET.ImGui.SetCursorPosX(x0 + _slotW);
        }
        else ImGuiNET.ImGui.SetCursorPosX(x0);
        for (int i = 0; i < l.Segs.Length; i++)
        {
            if (i > 0) ImGuiNET.ImGui.SameLine(0, 0);
            Colored(l.Segs[i].Text, ToneColor(l.Segs[i].Tone));
        }
        if (l.Segs.Length == 0) ImGuiNET.ImGui.NewLine();
    }

    /// <summary>Unformatted text in a colour (rule text may hold '%').</summary>
    private static void Colored(string text, uint color)
    {
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGuiNET.ImGui.TextUnformatted(text);
        ImGuiNET.ImGui.PopStyleColor();
    }

    // =====================================================================
    //  Dragging a rule
    // =====================================================================

    /// <summary>
    /// After the rows are drawn: the gap under the mouse among the dragged rule's state, the teal
    /// line there, a label by the mouse, scrolling at the list's edges; on release the move.
    /// </summary>
    private void DragUpdate()
    {
        if (_dragIndex < 0)
        {
            if (!ImGuiNET.ImGui.IsMouseDown(ImGuiMouseButton.Left)) _dragSwallowClick = false;
            return;
        }
        if (ImGuiNET.ImGui.IsKeyPressed(ImGuiKey.Escape) || _dragIndex >= _data.Rules.Count || _filter.Length > 0)
        {
            _dragIndex = -1;   // cancelled: the release still isn't a click
            return;
        }
        ImGuiNET.ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNS);
        if (ImGuiNET.ImGui.GetFrameCount() == _dragStartFrame) return;   // its state's rows are all in from next frame
        float my = ImGuiNET.ImGui.GetIO().MousePos.Y;
        int count = _dragSlots.Count;
        if (count > 0)
        {
            int gap = count;
            for (int k = 0; k < count; k++)
                if (my < (_dragSlots[k].Top + _dragSlots[k].Bottom) * 0.5f) { gap = k; break; }
            _dragGap = gap;
        }
        int to = _dragGap > _dragFromGi ? _dragGap - 1 : _dragGap;   // its place once taken out of the list

        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        Vector2 wp = ImGuiNET.ImGui.GetWindowPos();
        float ww = ImGuiNET.ImGui.GetWindowWidth();
        if (count > 0 && to != _dragFromGi)
        {
            float y = _dragGap < count ? _dragSlots[_dragGap].Top : _dragSlots[count - 1].Bottom;
            dl.AddLine(new Vector2(wp.X + 2, y), new Vector2(wp.X + ww - 2, y), Teal, 2.5f);
            dl.AddCircleFilled(new Vector2(wp.X + 6, y), 4f, Teal);
        }

        MetaRuleDto rule = _data.Rules[_dragIndex];
        if (ImGuiNET.ImGui.BeginTooltip())
        {
            string what = rule.Name.Length > 0 ? rule.Name
                : _dragIndex < _condCompact.Length ? MetaRowText.Plain(_condCompact[_dragIndex]) : "Rule";
            Colored(Fit(what, 320), TextDim);
            Colored(count == 0 || to == _dragFromGi ? $"Drop to leave it at {_dragFromGi + 1}"
                : $"Move to {to + 1} of {count} in {_dragState}", to == _dragFromGi ? Mute : Teal);
            ImGuiNET.ImGui.EndTooltip();
        }

        // Near the top or bottom of the list: scroll, faster the closer to the edge.
        float top = wp.Y, bottom = wp.Y + ImGuiNET.ImGui.GetWindowHeight();
        const float edge = 28;
        if (my < top + edge)
            ImGuiNET.ImGui.SetScrollY(Math.Max(0, ImGuiNET.ImGui.GetScrollY() - (top + edge - my) * 0.4f));
        else if (my > bottom - edge)
            ImGuiNET.ImGui.SetScrollY(Math.Min(ImGuiNET.ImGui.GetScrollMaxY(), ImGuiNET.ImGui.GetScrollY() + (my - (bottom - edge)) * 0.4f));

        if (!ImGuiNET.ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            int from = _dragIndex;
            _dragIndex = -1;
            _dragSwallowClick = false;   // the rows have had this frame's release
            if (count > 0 && to != _dragFromGi) MoveBy(from, to - _dragFromGi);
        }
    }

    /// <summary>Moves a rule <paramref name="steps"/> places within its state (move_up / move_down,
    /// one swap each, as the arrows do).</summary>
    private void MoveBy(int index, int steps)
    {
        int dir = Math.Sign(steps);
        for (int k = 0; k < Math.Abs(steps); k++)
        {
            int partner = MetaVocabulary.SwapPartner(_data.Rules, index, dir);
            if (partner < 0 || !Move(index, dir)) break;
            index = partner;
        }
        RebuildCaches();
    }

    // =====================================================================
    //  Row and header menus (right-click)
    // =====================================================================

    private void RuleMenus()
    {
        if (_openRuleMenu) { ImGuiNET.ImGui.OpenPopup("##meta_rule_menu"); _openRuleMenu = false; }
        if (_openStateMenu) { ImGuiNET.ImGui.OpenPopup("##meta_state_menu"); _openStateMenu = false; }

        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, ShellBg);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, Teal);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.HeaderHovered, PickSel);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, TextDim);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(6, 6));
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6, 4));
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 4f);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 1f);

        if (ImGuiNET.ImGui.BeginPopup("##meta_rule_menu"))
        {
            int i = _ctxRule;
            if (i < 0 || i >= _data.Rules.Count) ImGuiNET.ImGui.CloseCurrentPopup();
            else
            {
                MetaRuleDto rule = _data.Rules[i];
                bool canMove = _filter.Length == 0;
                if (ImGuiNET.ImGui.MenuItem(PhosphorIcons.PencilSimple + "  Edit rule")) OpenEditor(i, rule);
                if (ImGuiNET.ImGui.MenuItem(PhosphorIcons.TextAa + (rule.Name.Length > 0 ? "  Rename..." : "  Name...")))
                {
                    OpenEditor(i, rule);
                    _focusRuleName = true;
                }
                if (Folds(i) && ImGuiNET.ImGui.MenuItem(IsFolded(i) ? PhosphorIcons.CaretDown + "  Show all of it" : PhosphorIcons.CaretRight + "  Fold to one line"))
                    SetFolded(i, !IsFolded(i));
                if (ImGuiNET.ImGui.MenuItem((rule.Enabled ? PhosphorIcons.Square + "  Disable" : PhosphorIcons.CheckSquare + "  Enable")))
                {
                    rule.Enabled = !rule.Enabled;
                    SendEdit(new MetaCmd { Op = "set_rule_enabled", Index = i, Value = rule.Enabled ? "true" : "false" });
                }
                ImGuiNET.ImGui.Separator();
                if (ImGuiNET.ImGui.MenuItem(PhosphorIcons.ArrowUp + "  Move up", "", false,
                        canMove && MetaVocabulary.SwapPartner(_data.Rules, i, -1) >= 0) && Move(i, -1))
                    RebuildCaches();
                if (ImGuiNET.ImGui.MenuItem(PhosphorIcons.ArrowDown + "  Move down", "", false,
                        canMove && MetaVocabulary.SwapPartner(_data.Rules, i, +1) >= 0) && Move(i, +1))
                    RebuildCaches();
                if (ImGuiNET.ImGui.MenuItem(PhosphorIcons.Copy + "  Duplicate") && Duplicate(i, rule)) RebuildCaches();
                ImGuiNET.ImGui.Separator();
                ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Red);
                bool del = ImGuiNET.ImGui.MenuItem(PhosphorIcons.Trash + "  Delete rule");
                ImGuiNET.ImGui.PopStyleColor();
                if (del && Delete(i)) RebuildCaches();
            }
            ImGuiNET.ImGui.EndPopup();
        }

        if (ImGuiNET.ImGui.BeginPopup("##meta_state_menu"))
        {
            string? s = _ctxState;
            if (s == null) ImGuiNET.ImGui.CloseCurrentPopup();
            else
            {
                if (ImGuiNET.ImGui.MenuItem(PhosphorIcons.Play + "  Make current state", "", false, !IsCurrent(s)))
                {
                    _data.CurrentState = s;
                    MetaCommands.Simple("set_state", value: s);
                    _stateClock.StateSet(NowMs);
                }
                if (ImGuiNET.ImGui.MenuItem(PhosphorIcons.Plus + "  New rule in " + s))
                    OpenEditor(-1, new MetaRuleDto { State = s, Action = 1 });
                ImGuiNET.ImGui.Separator();
                if (ImGuiNET.ImGui.MenuItem(PhosphorIcons.CaretRight + "  Fold its rules")) SetStateFolded(s, true);
                if (ImGuiNET.ImGui.MenuItem(PhosphorIcons.CaretDown + "  Show its rules in full")) SetStateFolded(s, false);
                ImGuiNET.ImGui.Separator();
                if (ImGuiNET.ImGui.MenuItem(PhosphorIcons.ArrowsInLineVertical + "  Collapse the others"))
                {
                    foreach ((string st, List<int> _) in _groups) if (!string.Equals(st, s, StringComparison.Ordinal)) _collapsed.Add(st);
                    _collapsed.Remove(s);
                }
                if (ImGuiNET.ImGui.MenuItem(PhosphorIcons.ArrowsOutLineVertical + "  Expand all states")) _collapsed.Clear();
            }
            ImGuiNET.ImGui.EndPopup();
        }

        ImGuiNET.ImGui.PopStyleVar(4);
        ImGuiNET.ImGui.PopStyleColor(4);
    }

    // =====================================================================
    //  Rule commands
    // =====================================================================

    private bool Move(int index, int direction)
    {
        int other = MetaVocabulary.SwapPartner(_data.Rules, index, direction);
        if (other < 0) return false;
        SendEdit(new MetaCmd { Op = direction < 0 ? "move_up" : "move_down", Index = index });
        (_data.Rules[index], _data.Rules[other]) = (_data.Rules[other], _data.Rules[index]);
        return true;
    }

    private bool Duplicate(int index, MetaRuleDto rule)
    {
        SendEdit(new MetaCmd { Op = "duplicate_rule", Index = index, Rule = rule.Clone() });
        _data.Rules.Insert(Math.Min(index + 1, _data.Rules.Count), rule.Clone());
        return true;
    }

    private bool Delete(int index)
    {
        if (index < 0 || index >= _data.Rules.Count) return false;
        SendEdit(new MetaCmd { Op = "delete_rule", Index = index });
        _data.Rules.RemoveAt(index);
        _confirmDelete = null;
        return true;
    }

    // =====================================================================
    //  Bottom bar
    // =====================================================================

    // State: [current state v][target][+ New Rule]
    private void BottomBar(float w)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        float labelW = ImGuiNET.ImGui.CalcTextSize("State:").X;
        dl.AddText(new Vector2(p.X, p.Y + (24 - ImGuiNET.ImGui.GetFontSize()) * 0.5f), Mute, "State:");
        // Buttons sized from their text, so a larger panel text size can't overflow them
        // (2026-10-05: "+ New Rule" spilled past its fixed 72 px button).
        string ruleLabel = PhosphorIcons.Plus + " New Rule";
        float newW = ImGuiNET.ImGui.CalcTextSize(ruleLabel).X + 18;
        // The bar sits on the body's bottom edge: it stops short of the resize grip (GripInBody).
        float grip = ImGuiPanelHost.BodyGripReserve;
        float ruleX = p.X + w - grip - newW;
        const float goW = 24;
        float pickX = p.X + labelW + 4;
        float pickW = Math.Max(40, ruleX - 4 - goW - 4 - pickX);

        // The meta's current state. "+ New state..." at the end of the list makes one: it starts
        // with an empty Never -> None rule (that rule is what keeps it), becomes the current state
        // like any state picked here, and that rule opens in the editor.
        string? inState = _stateClock.Known ? MetaStateClock.Format(_stateClock.SecondsInState(NowMs)) : null;
        string? chosen = StatePicker("##cur_state", _data.CurrentState, _statePicks, Array.IndexOf(_statePicks, _data.CurrentState),
            new Vector2(pickX, p.Y + 1), pickW, out bool isNew, badge: inState);
        if (chosen != null)
        {
            if (isNew) CreateState(chosen, openIt: true);
            _data.CurrentState = chosen;
            MetaCommands.Simple("set_state", value: chosen);
            _stateClock.StateSet(NowMs);   // picking a state enters it afresh (RynthAi resets its clock)
        }
        if (inState != null && ImGuiNET.ImGui.IsMouseHoveringRect(new Vector2(pickX, p.Y + 1), new Vector2(pickX + pickW, p.Y + 23)))
            SafeImGui.SetTooltip($"The meta's current state; in it for {inState}.");
        bool canGo = HasRulesIn(_data.CurrentState);
        if (IconButton("##goto_current", PhosphorIcons.Crosshair, new Vector2(pickX + pickW + 4, p.Y + 1), new Vector2(goW, 22),
                Teal, BtnFill, BtnBord, enabled: canGo, font: UiFont.Ui14))
        {
            _scrollToState = _data.CurrentState;
            if (_filter.Length > 0 && !StateHasHits(_data.CurrentState))
            {
                Array.Clear(_filterBuf);
                SetFilter(string.Empty);
            }
        }
        SafeImGui.SetItemTooltip(canGo ? "Show the current state in the list" : "The current state has no rules");
        if (Button(ruleLabel + "##new", new Vector2(ruleX, p.Y), new Vector2(newW, 24), TextDim, NewFill, Green))
            OpenEditor(-1, new MetaRuleDto { State = _data.CurrentState, Action = 1 });
        ImGuiNET.ImGui.SetItemTooltip("A new rule in the current state (added once its condition and action are filled in)");
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + 24));
        ImGuiNET.ImGui.Dummy(new Vector2(0, 0));
    }

    private bool StateHasHits(string state)
    {
        foreach ((string s, List<int> members) in _groups)
        {
            if (!string.Equals(s, state, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (int i in members) if (i < _filterHit.Length && _filterHit[i]) return true;
        }
        return false;
    }

    // =====================================================================
    //  Widgets
    // =====================================================================

    /// <summary>An icon with no frame (the enable box, the header's +, the filter's clear): brighter on hover.</summary>
    private static bool GlyphButton(string id, string icon, Vector2 pos, Vector2 size, uint color, uint hoverColor)
    {
        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGuiNET.ImGui.InvisibleButton(id, size);
        bool hot = ImGuiNET.ImGui.IsItemHovered();
        PhosphorIcons.DrawCentered(ImGuiNET.ImGui.GetWindowDrawList(), ImGuiFonts.Get(UiFont.Ui14), icon, pos, size, hot ? hoverColor : color);
        return clicked;
    }
}
