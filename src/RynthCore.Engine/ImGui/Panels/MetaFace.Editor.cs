// ============================================================================
//  RynthCore.Engine - ImGui/Panels/MetaFace.Editor.cs
//  The Meta panel's rule editor and its state pickers (2026-10-05 clean-up):
//
//    [<- Back]  Edit rule                                    Saved
//    State: [Combat                                       v]
//    Name:  [Optional: a name to find this rule by         ]
//    +- Conditions ------ [+ Add] -+  +- Actions --------- [+ Add] -+
//    | [Landblock ==            v] |  | [Chat Command           v] |
//    | [A9B40000     ][Set current] |  | [/say hello               ] |
//    +-----------------------------+  +-----------------------------+
//    [Revert]                                          [Delete rule]
//
//  Conditions and Actions sit side by side, each scrolling on its own; under
//  SideBySideMinWidth they stack. "+ Add" turns a single condition into the
//  first of an All (a single action into the first of a DoAll), or adds one
//  more to an All / Any / DoAll.
//
//  Groups (2026-10-11): an All / Any / Not, nested or the column's own, and a
//  DoAll is a box (tint, border and left bar in its colour: All teal, Any
//  amber, Not rose) around its picker and everything in it, so a box inside
//  a box shows what belongs where; a nested group's line says what it means
//  ("any of the conditions inside"). Rows alternate a light shade; a DoAll's
//  actions are numbered in the order they run.
//
//  Saving: every change marks the rule dirty (MetaAutosave); 0.75 s after the
//  last change, and never while a text box is being typed in, the rule goes
//  to RynthAi as add_rule (a new rule, the first time, and only once its
//  condition and action are filled in: MetaRuleEdit.ReadyToAdd) or
//  update_rule, and RynthAi writes the file. Back, closing the panel and switching metas save
//  at once. update_rule keeps the running rule's fired latch (RynthAi
//  MetaRuleEdits), so a save never re-fires a rule, restarts the state's
//  clocks or changes the current state.
//
//  New states: every state picker (the rule's State, Set / Call Meta State
//  targets, the bottom bar) ends with "+ New state...". Picking it turns the
//  picker into a name box with OK / Cancel (Enter / Esc); the name is trimmed,
//  must not be empty, contain { } or match an existing state (ignoring case,
//  as RynthAi does). A state exists only while a rule uses it (RynthAi lists
//  its rules' states; the .af file has a STATE: block per state with rules),
//  so a new state is never left empty:
//   - the rule's own State picker moves this rule into it;
//   - a Set / Call Meta State target and the bottom bar add an empty
//     Never -> None rule in it at once (it never fires; edit or delete it).
//     The bottom bar also makes it the current state, like any state picked
//     there, and opens that rule.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.UI.Data;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed partial class MetaFace
{
    /// <summary>Under this width Conditions and Actions stack instead of sitting side by side.</summary>
    private const float SideBySideMinWidth = 400;

    private MetaRuleDto? _editOriginal;   // as opened (Revert); null for a new rule
    private MetaRuleDto? _lastSaved;      // as last sent to RynthAi; finds the rule again by content
    private bool _editChanged;            // set by this frame's widgets
    private bool _typingNow, _editTyping; // a data box is being typed in (this frame / last frame)
    private bool _editConfirmDelete;
    private string _editFile = string.Empty;   // the meta file the open rule belongs to
    private string _listNote = string.Empty;   // shown over the rule list for a few seconds
    private long _listNoteAt;

    // The one state picker turned into a name box (ImGui id, 0 = none).
    private uint _namingKey;
    private readonly byte[] _nameBuf = new byte[64];
    private bool _nameFocus, _nameWasActive;
    private string? _nameProblem;
    private const string NewStateEntry = PhosphorIcons.Plus + " New state...";

    // The rule's Name box (MetaRuleDto.Name; one line, MaxNameLength bytes of UTF-8).
    private readonly byte[] _ruleNameBytes = new byte[MetaRuleEdit.MaxNameLength + 1];
    private string? _ruleNameShown;     // the name the box was last filled from
    private bool _ruleNameActive, _focusRuleName;

    private const string BackLabel = PhosphorIcons.ArrowLeft + " Back";
    private const string AddLabel = PhosphorIcons.Plus + " Add";
    private const string SetCurrentLabel = PhosphorIcons.MapPin + " Set current";

    // =====================================================================
    //  Open / save / leave
    // =====================================================================

    private void OpenEditor(int index, MetaRuleDto rule)
    {
        FlushEdit();
        _editIndex = index;
        _editFile = EditFileOf(_data.CurrentMetaPath);
        _editRule = index >= 0 ? rule.Clone() : rule;
        _editOriginal = index >= 0 ? rule.Clone() : null;
        _lastSaved = index >= 0 ? rule.Clone() : null;
        _autosave.Clear();
        _fields.Clear();
        _ruleNameShown = null;
        _editConfirmDelete = false;
        _editTyping = false;
        CancelNaming();
        _view = View.Editor;
    }

    /// <summary>
    /// Another meta was loaded while a rule was open (the Meta Manager, a chat command): the rule's
    /// index means nothing in that meta, so the editor closes without saving into it. RynthAi
    /// saving a .met as its .af is the same meta.
    /// </summary>
    private void EditorGuard()
    {
        if (string.Equals(_editFile, EditFileOf(_data.CurrentMetaPath), StringComparison.OrdinalIgnoreCase)) return;
        bool lost = _autosave.Dirty;
        _autosave.Clear();
        CancelNaming();
        _view = View.List;
        _listNote = lost
            ? "Another meta was loaded while you edited a rule; your last change to it was not saved."
            : "Another meta was loaded, so the rule editor closed.";
        _listNoteAt = NowMs;
    }

    private static string EditFileOf(string? metaPath) =>
        string.IsNullOrEmpty(metaPath) ? string.Empty : MetaBackupPlan.WrittenFile(metaPath);

    private void LeaveEditor()
    {
        FlushEdit();
        CancelNaming();
        _view = View.List;
    }

    /// <summary>Saves a pending edit now (Back, panel closed, meta switch).</summary>
    private void FlushEdit()
    {
        if (_view == View.Editor && _autosave.Dirty) CommitEdit();
    }

    /// <summary>Sends the edited rule: add_rule the first time for a new rule, then update_rule.</summary>
    private void CommitEdit()
    {
        if (!string.Equals(_editFile, EditFileOf(_data.CurrentMetaPath), StringComparison.OrdinalIgnoreCase))
        {
            EditorGuard();   // never save into a meta loaded meanwhile
            return;
        }
        if (!_autosave.Take()) return;
        MetaRuleDto send = _editRule.Clone();
        send.Name = send.Name.Trim();   // the box keeps a space you just typed; the rule doesn't
        if (_editIndex < 0)
        {
            _editIndex = _data.Rules.Count;   // add_rule appends
            SendEdit(new MetaCmd { Op = "add_rule", Rule = send });
            _data.Rules.Add(send.Clone());
        }
        else
        {
            // Commands address rules by index: find the rule again in case the snapshot moved it.
            if (_lastSaved != null) _editIndex = MetaRuleEdit.Locate(_data.Rules, _lastSaved, _editIndex);
            SendEdit(new MetaCmd { Op = "update_rule", Index = _editIndex, Rule = send });
            if (_editIndex < _data.Rules.Count) _data.Rules[_editIndex] = send.Clone();
        }
        _lastSaved = send.Clone();
        RebuildCaches();
    }

    private void DeleteEditedRule()
    {
        _autosave.Clear();
        if (_editIndex >= 0)
        {
            int index = _lastSaved != null ? MetaRuleEdit.Locate(_data.Rules, _lastSaved, _editIndex) : _editIndex;
            if (index >= 0 && index < _data.Rules.Count)
            {
                SendEdit(new MetaCmd { Op = "delete_rule", Index = index });
                _data.Rules.RemoveAt(index);
                RebuildCaches();
            }
        }
        CancelNaming();
        _view = View.List;
    }

    // =====================================================================
    //  Editor view
    // =====================================================================

    private void EditorView(Vector2 size)
    {
        _editChanged = false;
        _typingNow = false;
        MetaRuleDto r = _editRule;
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        Vector2 top = ImGuiNET.ImGui.GetCursorScreenPos();
        float w = size.X;

        // [<- Back]  New rule / Edit rule                Saved
        float backW = BtnW(BackLabel, 60);
        if (Button(BackLabel + "##back", top, new Vector2(backW, 22), Mute, BtnFill, BtnBord))
        {
            LeaveEditor();
            return;
        }
        ImGuiNET.ImGui.SetItemTooltip("Back to the rule list (saves this rule)");
        float indW = SaveIndicatorWidth(editor: true);
        ImFontPtr bold = ImGuiFonts.Get(UiFont.UiBold11);
        float tx = top.X + backW + 8;
        string title = _editIndex < 0 ? "New rule" : r.Name.Trim().Length > 0 ? r.Name.Trim() : "Edit rule";
        ImGuiNET.ImGui.PushFont(bold);
        dl.AddText(new Vector2(tx, top.Y + (22 - bold.FontSize) * 0.5f), Teal, Fit(title, Math.Max(10, top.X + w - indW - 8 - tx)));
        ImGuiNET.ImGui.PopFont();
        SaveIndicator(new Vector2(top.X + w - indW, top.Y), indW, 22);

        // State: [picker]
        float y = top.Y + 26;
        float lw = Math.Max(ImGuiNET.ImGui.CalcTextSize("State:").X, ImGuiNET.ImGui.CalcTextSize("Name:").X) + 6;
        dl.AddText(new Vector2(top.X, y + (22 - ImGuiNET.ImGui.GetFontSize()) * 0.5f), Mute, "State:");
        string? state = StatePicker("##ed_state", r.State, _statePicks, IndexOf(_statePicks, r.State),
            new Vector2(top.X + lw, y), w - lw, out _);
        if (state != null && state != r.State)
        {
            r.State = state;   // a new state is made by moving this rule into it
            _editChanged = true;
        }
        y += 22 + ProblemLine("##ed_state", new Vector2(top.X + lw, y + 22), w - lw) + 4;

        // Name: [optional]
        dl.AddText(new Vector2(top.X, y + (22 - ImGuiNET.ImGui.GetFontSize()) * 0.5f), Mute, "Name:");
        NameBox(new Vector2(top.X + lw, y), w - lw);
        y += 22 + 6;

        // Conditions | Actions
        const float footerH = 24;
        float colsH = Math.Max(60, top.Y + size.Y - y - footerH - 6);
        if (w >= SideBySideMinWidth)
        {
            float cw = (w - 6) * 0.5f;
            Column("##ed_conds", new Vector2(top.X, y), new Vector2(cw, colsH), actions: false);
            Column("##ed_acts", new Vector2(top.X + cw + 6, y), new Vector2(w - cw - 6, colsH), actions: true);
        }
        else
        {
            float ch = (colsH - 6) * 0.5f;
            Column("##ed_conds", new Vector2(top.X, y), new Vector2(w, ch), actions: false);
            Column("##ed_acts", new Vector2(top.X, y + ch + 6), new Vector2(w, colsH - ch - 6), actions: true);
        }

        EditorFooter(new Vector2(top.X, y + colsH + 6), w, footerH);
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(top.X, y + colsH + 6 + footerH));
        ImGuiNET.ImGui.Dummy(new Vector2(0, 0));

        if (_editChanged)
        {
            _autosave.Changed(NowMs);
            _editConfirmDelete = false;
        }
        _editTyping = _typingNow;
    }

    /// <summary>A bordered column: a fixed header (title, "+ Add") over a body that scrolls on its own.</summary>
    private void Column(string id, Vector2 pos, Vector2 size, bool actions)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        MetaRuleDto r = _editRule;
        const float hh = 24;
        dl.AddRectFilled(pos, pos + size, PanelBg, 3);
        dl.AddRectFilled(pos, pos + new Vector2(size.X, hh), StateHdrA, 3);
        dl.AddRect(pos, pos + size, BtnBord, 3);
        ImFontPtr bold = ImGuiFonts.Get(UiFont.UiBold11);
        dl.AddText(bold, bold.FontSize, pos + new Vector2(7, (hh - bold.FontSize) * 0.5f), Amber, actions ? "Actions" : "Conditions");

        bool canAdd = actions || MetaRuleEdit.CanAddCondition(r);
        float addW = BtnW(AddLabel, 52);
        if (Button(AddLabel + id + "_add", new Vector2(pos.X + size.X - addW - 3, pos.Y + 2), new Vector2(addW, hh - 4), Teal, BtnFill, BtnBord,
                enabled: canAdd))
        {
            if (actions) MetaRuleEdit.AddAction(r); else MetaRuleEdit.AddCondition(r);
            _editChanged = true;
        }
        SafeImGui.SetItemTooltip(actions
            ? (r.Action == MetaRuleEdit.DoAll ? "Add one more action" : "Add another action: this one and the new one run in order (All)")
            : !canAdd ? "A Not has one condition"
            : r.Condition is MetaRuleEdit.All or MetaRuleEdit.Any or MetaRuleEdit.Not ? "Add one more condition"
            : "Add another condition: the rule fires when all of them are true (All)");

        ImGuiNET.ImGui.SetCursorScreenPos(pos + new Vector2(5, hh + 4));
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ChildBg, 0);
        ImGuiNET.ImGui.BeginChild(id, new Vector2(Math.Max(10, size.X - 10), Math.Max(10, size.Y - hh - 8)));
        ImGuiNET.ImGui.PopStyleColor();
        float cw = Math.Max(40, ImGuiNET.ImGui.GetContentRegionAvail().X - 2);
        if (actions) ActionsBody(r, cw); else ConditionsBody(r, cw);
        ImGuiNET.ImGui.EndChild();
    }

    private void ConditionsBody(MetaRuleDto r, float w)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        BeginBacks(dl);
        try
        {
            // An All / Any / Not is a box around its picker and everything in it.
            bool card = MetaVocabulary.IsCompositeCondition(r.Condition);
            Vector2 top = CardOpen(card);
            float iw = card ? w - CardInset - CardPadR : w;
            int picked = FlowPicker("##ed_cond", MetaVocabulary.ConditionName(r.Condition), MetaVocabulary.ConditionNames, r.Condition, iw);
            if (picked >= 0 && picked != r.Condition)
            {
                SetCondition(r, picked);
                _editChanged = true;
            }
            if (!card || !MetaVocabulary.IsCompositeCondition(r.Condition))
            {
                if (!MetaVocabulary.IsCompositeCondition(r.Condition)) ConditionField("##ed_cdata", r, iw, 22);
                CardClose(card, top, w, GroupColor(r.Condition, false), GroupTint(r.Condition, false), 0);
                return;
            }
            GroupLabel(r.Condition switch
            {
                MetaRuleEdit.Not => "Not: true while the condition inside is false",
                MetaRuleEdit.All => "All of these:",
                _ => "Any of these:",
            }, GroupColor(r.Condition, false));
            if (r.Children.Count == 0) Note("None yet: + Add (top right).");
            SubRules(r.Children, action: false, 0, r.Condition == MetaRuleEdit.Not ? 1 : int.MaxValue, iw);
            CardClose(card, top, w, GroupColor(r.Condition, false), GroupTint(r.Condition, false), 0);
        }
        finally
        {
            EndBacks(dl);
        }
    }

    private void ActionsBody(MetaRuleDto r, float w)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        BeginBacks(dl);
        try
        {
            bool card = MetaVocabulary.IsAllAction(r.Action);   // a DoAll is a box around its actions
            Vector2 top = CardOpen(card);
            float iw = card ? w - CardInset - CardPadR : w;
            int picked = FlowPicker("##ed_act", MetaVocabulary.ActionName(r.Action), MetaVocabulary.ActionNames, r.Action, iw);
            if (picked >= 0 && picked != r.Action)
            {
                r.Action = picked;
                if (!MetaVocabulary.IsAllAction(picked)) r.ActionChildren.Clear();
                _editChanged = true;
            }
            if (!card || !MetaVocabulary.IsAllAction(r.Action))
            {
                if (!MetaVocabulary.IsAllAction(r.Action)) ActionField("##ed_adata", r, iw, 22, problemLine: true);
                CardClose(card, top, w, GroupColor(MetaRuleEdit.DoAll, true), GroupTint(MetaRuleEdit.DoAll, true), 0);
                return;
            }
            GroupLabel("All of these, in order:", GroupColor(MetaRuleEdit.DoAll, true));
            if (r.ActionChildren.Count == 0) Note("None yet: + Add (top right).");
            SubRules(r.ActionChildren, action: true, 0, int.MaxValue, iw);
            CardClose(card, top, w, GroupColor(MetaRuleEdit.DoAll, true), GroupTint(MetaRuleEdit.DoAll, true), 0);
        }
        finally
        {
            EndBacks(dl);
        }
    }

    // =====================================================================
    //  Group boxes and row stripes (2026-10-11: "hard to see what's what in All and Any")
    // =====================================================================
    //
    //  An All / Any / Not (and a DoAll) is a box: a tint and a border in the group's colour (All
    //  teal, Any amber, Not rose), an accent bar down its left, around its picker and everything in
    //  it; a box inside a box is another, so what belongs where shows at a glance. Rows of a list
    //  alternate a light shade. A box's height is known only after its contents are laid out, so
    //  boxes and stripes are recorded while the rows are placed and drawn at the end on a channel
    //  under them (outer boxes first, each list's stripes over its box).

    private const float CardInset = 9, CardPadR = 5, CardPadT = 5, CardPadB = 4;
    private static readonly uint Rose = C(0xFFE07A9A), StripeOver = C(0x12FFFFFF);
    private readonly List<(Vector2 Min, Vector2 Max, uint Fill, uint Border, uint Accent, int Order)> _backs = new();

    private void BeginBacks(ImDrawListPtr dl)
    {
        _backs.Clear();
        dl.ChannelsSplit(2);
        dl.ChannelsSetCurrent(1);   // widgets over the boxes
    }

    private void EndBacks(ImDrawListPtr dl)
    {
        dl.ChannelsSetCurrent(0);
        // Outer before inner (Order = 2 x depth for a box, + 1 for its list's stripes).
        _backs.Sort((x, y) => x.Order.CompareTo(y.Order));
        foreach (var b in _backs)
        {
            if (b.Fill != 0) dl.AddRectFilled(b.Min, b.Max, b.Fill, b.Border != 0 ? 4f : 0f);
            if (b.Accent != 0) dl.AddRectFilled(b.Min, new Vector2(b.Min.X + 3, b.Max.Y), b.Accent, 2f);
            if (b.Border != 0) dl.AddRect(b.Min, b.Max, b.Border, 4f);
        }
        _backs.Clear();
        dl.ChannelsMerge();
    }

    /// <summary>Starts a box at the cursor when <paramref name="card"/> (its contents indented); returns its top-left.</summary>
    private static Vector2 CardOpen(bool card)
    {
        Vector2 top = ImGuiNET.ImGui.GetCursorScreenPos();
        if (!card) return top;
        ImGuiNET.ImGui.Indent(CardInset);   // moves the cursor in; the box's rows keep to it
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(ImGuiNET.ImGui.GetCursorScreenPos().X, top.Y + CardPadT));
        return top;
    }

    /// <summary>Ends a box opened at <paramref name="top"/>, <paramref name="w"/> wide, and records it.</summary>
    private void CardClose(bool card, Vector2 top, float w, uint color, uint tint, int depth)
    {
        if (!card) return;
        float endY = ImGuiNET.ImGui.GetCursorScreenPos().Y - ImGuiNET.ImGui.GetStyle().ItemSpacing.Y + CardPadB;
        ImGuiNET.ImGui.Unindent(CardInset);
        _backs.Add((top, new Vector2(top.X + w, endY), tint, (color & 0x00FFFFFF) | 0x80000000u, color, depth * 2));
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(top.X, endY + 3));
        ImGuiNET.ImGui.Dummy(new Vector2(0, 0));
    }

    /// <summary>A group's colour: All (and DoAll) teal, Any amber, Not rose.</summary>
    private static uint GroupColor(int kind, bool action) => action ? Teal : kind switch
    {
        MetaRuleEdit.Any => Amber,
        MetaRuleEdit.Not => Rose,
        _ => Teal,
    };

    /// <summary>A group box's fill: its colour, faint, over the column.</summary>
    private static uint GroupTint(int kind, bool action) => action ? C(0xFF10262E) : kind switch
    {
        MetaRuleEdit.Any => C(0xFF2A2412),
        MetaRuleEdit.Not => C(0xFF2C1622),
        _ => C(0xFF10262E),
    };

    /// <summary>What a nested group's line says after its picker.</summary>
    private static string GroupWords(int kind) => kind switch
    {
        MetaRuleEdit.All => "all of the conditions inside",
        MetaRuleEdit.Any => "any of the conditions inside",
        _ => "true while the condition inside is false",
    };

    private static void GroupLabel(string text, uint color)
    {
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui9));
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGuiNET.ImGui.TextUnformatted(text);
        ImGuiNET.ImGui.PopStyleColor();
        ImGuiNET.ImGui.PopFont();
    }

    /// <summary>The rule's name: optional, one line; saves like every other change.</summary>
    private void NameBox(Vector2 pos, float width)
    {
        if (!_ruleNameActive && !ReferenceEquals(_ruleNameShown, _editRule.Name))
        {
            WriteUtf8(_ruleNameBytes, _editRule.Name);
            _ruleNameShown = _editRule.Name;
        }
        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        if (_focusRuleName)
        {
            ImGuiNET.ImGui.SetKeyboardFocusHere();
            _focusRuleName = false;
        }
        bool edited = TextBox("##ed_name", _ruleNameBytes, width, "Optional: a name to find this rule by",
            ImGuiInputTextFlags.None, out _ruleNameActive);
        ImGuiNET.ImGui.SetItemTooltip("Shown in bold over the rule in the list (and instead of its condition in Compact).");
        _typingNow |= _ruleNameActive;   // autosave waits until the box is left
        if (edited)
        {
            string typed = MetaRuleEdit.CleanName(Utf8(_ruleNameBytes));
            _editRule.Name = typed;
            _ruleNameShown = typed;
            _editChanged = true;
        }
    }

    private static void SetCondition(MetaRuleDto c, int picked)
    {
        c.Condition = picked;
        if (!MetaVocabulary.IsCompositeCondition(picked)) c.Children.Clear();
        else if (picked == MetaRuleEdit.Not && c.Children.Count > 1) c.Children.RemoveRange(1, c.Children.Count - 1);
    }

    /// <summary>A condition's value at the cursor: a text box (with Set current for landblock / landcell),
    /// or a note when the condition takes nothing. Leaves the cursor under it.</summary>
    private void ConditionField(string id, MetaRuleDto c, float width, float height)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        if (!MetaRuleEdit.ConditionTakesData(c.Condition) && c.ConditionData.Length == 0)
            NoteAt(p, "Nothing to set.", height);
        else if (MetaLocation.TakesPosition(c.Condition))
        {
            // "Set current" fills in where the player is, from the engine's per-frame pose copy
            // (no call into AC from here), in the 8-digit form RynthAi and VTank both read.
            float bw = Math.Min(BtnW(SetCurrentLabel, 70), width * 0.5f);
            float boxW = Math.Max(30, width - bw - 4);
            ImGuiNET.ImGui.SetCursorScreenPos(p);
            DataBox(id, c, action: false, MetaVocabulary.ConditionHint(c.Condition), boxW, height);
            string? here = PlayerPhysicsHooks.TryGetPlayerPoseSnapshot(out uint cell, out _, out _, out _, out _, out _, out _, out _) && cell != 0
                ? MetaLocation.Text(c.Condition, cell) : null;
            if (Button((bw >= BtnW(SetCurrentLabel, 0) ? SetCurrentLabel : PhosphorIcons.MapPin) + id + "_here",
                    new Vector2(p.X + width - bw, p.Y), new Vector2(bw, height), Teal, BtnFill, BtnBord, enabled: here != null) && here != null)
            {
                c.ConditionData = here;
                _editChanged = true;
            }
            SafeImGui.SetItemTooltip(here != null
                ? $"Set to where you are now: {here}"
                : "Your position isn't known right now (portal space, or not in the world).");
        }
        else
        {
            ImGuiNET.ImGui.SetCursorScreenPos(p);
            DataBox(id, c, action: false, MetaVocabulary.ConditionHint(c.Condition), width, height);
            if (c.Condition == MetaRuleEdit.NoKillsWithinMinutes)
                SafeImGui.SetItemTooltip(MetaRuleEdit.NoKillsTooltip(c.ConditionData));
        }
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + height));
        ImGuiNET.ImGui.Dummy(new Vector2(0, 0));
    }

    /// <summary>An action's value at the cursor: a state picker (Set / Call Meta State), the route picker
    /// (Embedded Nav Route), a text box, or a note. Leaves the cursor under it.</summary>
    private void ActionField(string id, MetaRuleDto a, float width, float height, bool problemLine)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        float extra = 0;
        if (a.Action is 2 or 5)   // Set / Call Meta State
        {
            string? target = StatePicker(id, a.ActionData.Length == 0 ? "Select state..." : a.ActionData, _statesOnly,
                IndexOf(_statesOnly, a.ActionData), p, width, out bool isNew, height);
            if (target != null)
            {
                if (isNew) CreateState(target, openIt: false);   // an empty first rule keeps it
                if (target != a.ActionData)
                {
                    a.ActionData = target;
                    _editChanged = true;
                }
            }
            if (problemLine) extra = ProblemLine(id, new Vector2(p.X, p.Y + height), width);
        }
        else if (a.Action == 3)   // Embedded Nav Route: Patrol, nav files, the meta's routes
        {
            int sel = IndexOf(_routeValues, a.ActionData);
            string shown = a.ActionData.Length == 0 ? "Select route..." : sel >= 0 ? _routeLabels[sel] : a.ActionData;
            int picked = Picker(id, shown, _routeLabels, sel, p, width, height);
            if (picked >= 0 && _routeValues[picked] != a.ActionData)
            {
                a.ActionData = _routeValues[picked];   // the name RynthAi looks up, not the list's label
                _editChanged = true;
            }
            // (The picker's last item is a spacer, so the hover is checked on its rectangle.)
            if (MetaVocabulary.PatrolRoute.Equals(a.ActionData, StringComparison.OrdinalIgnoreCase)
                    && !MetaVocabulary.HasRoute(_data, a.ActionData)
                    && ImGuiNET.ImGui.IsWindowHovered() && ImGuiNET.ImGui.IsMouseHoveringRect(p, p + new Vector2(width, height)))
                ImGuiNET.ImGui.SetTooltip("RynthAi's dungeon patrol (/ra dunnav-patrol): a circular hunt through every room of the dungeon you're in. Needs no nav file; only works inside a dungeon.");
        }
        else if (!MetaRuleEdit.ActionTakesData(a.Action) && a.ActionData.Length == 0)
            NoteAt(p, "Nothing to set.", height);
        else
        {
            ImGuiNET.ImGui.SetCursorScreenPos(p);
            DataBox(id, a, action: true, MetaVocabulary.ActionHint(a.Action), width, height);
        }
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + height + extra));
        ImGuiNET.ImGui.Dummy(new Vector2(0, 0));
    }

    // Sub-conditions nest (All/Any/Not under All/Any/Not, MaxSubConditionDepth levels; a Not takes
    // one); DoAll's sub-actions are a flat list that runs in order, so each is numbered and has up /
    // down. A row: [type][value]([up][down])[x]; every other row a light shade; in a narrow column the
    // value goes on a second line. A nested All / Any / Not is a box (its picker, what it means, then
    // its conditions inside, indented). The top level's "+ Add" is in the column header.
    private void SubRules(List<MetaRuleDto> subs, bool action, int depth, int maxCount, float w)
    {
        string[] names = action ? MetaVocabulary.ActionNames : MetaVocabulary.ConditionNames;
        bool narrow = w < 300;
        const float rh = 20, xw = 20, aw = 16, childIndent = 10;
        for (int i = 0; i < subs.Count; i++)
        {
            MetaRuleDto sub = subs[i];
            ImGuiNET.ImGui.PushID(i);
            bool nests = !action && MetaVocabulary.IsCompositeCondition(sub.Condition) && depth < MetaVocabulary.MaxSubConditionDepth;
            Vector2 cardTop = CardOpen(nests);
            float rw = nests ? w - CardInset - CardPadR : w;   // the row's width (inside its box)
            float tools = xw + (action ? 2 * (aw + 2) : 0);    // [up][down][x] or [x]
            float lead = action ? 16 : 0;                      // a DoAll's step number
            Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
            float pickW = narrow ? rw - tools - lead - 4 : (rw - tools - lead - 8) * 0.45f;

            if (action)
            {
                string step = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                ImGuiNET.ImGui.GetWindowDrawList().AddText(
                    new Vector2(p.X + lead - 4 - ImGuiNET.ImGui.CalcTextSize(step).X, p.Y + (rh - ImGuiNET.ImGui.GetFontSize()) * 0.5f),
                    Faded(Mute), step);
            }
            int current = action ? sub.Action : sub.Condition;
            string shown = action ? MetaVocabulary.ActionName(sub.Action) : MetaVocabulary.ConditionName(sub.Condition);
            int picked = Picker("##type", shown, names, current, new Vector2(p.X + lead, p.Y), pickW, rh);
            if (picked >= 0 && picked != current)
            {
                if (action) sub.Action = picked; else SetCondition(sub, picked);
                _editChanged = true;
            }

            bool removed = IconButton("##rm", PhosphorIcons.X, new Vector2(p.X + rw - xw, p.Y), new Vector2(xw, rh), TextDim, DelFill, BtnBord);
            SafeImGui.SetItemTooltip(action ? "Remove this action" : nests ? "Remove this group and what is in it" : "Remove this condition");
            int moveTo = -1;
            if (action)
            {
                float ax = p.X + rw - xw - 2 - 2 * (aw + 2);
                if (IconButton("##sub_up", PhosphorIcons.ArrowUp, new Vector2(ax, p.Y), new Vector2(aw, rh), Mute, BtnFill, BtnBord, enabled: i > 0))
                    moveTo = i - 1;
                ImGuiNET.ImGui.SetItemTooltip("Run this action earlier");
                if (IconButton("##sub_dn", PhosphorIcons.ArrowDown, new Vector2(ax + aw + 2, p.Y), new Vector2(aw, rh), Mute, BtnFill, BtnBord,
                        enabled: i < subs.Count - 1))
                    moveTo = i + 1;
                ImGuiNET.ImGui.SetItemTooltip("Run this action later");
            }

            float rowH = rh;
            if (nests)
            {
                if (!narrow)
                    NoteAt(new Vector2(p.X + pickW + 6, p.Y), GroupWords(sub.Condition), rh, GroupColor(sub.Condition, false));
            }
            else
            {
                Vector2 vp = narrow ? new Vector2(p.X + lead + 12, p.Y + rh + 2) : new Vector2(p.X + lead + pickW + 4, p.Y);
                float vw = narrow ? rw - lead - 12 : rw - lead - pickW - 4 - tools - 4;
                ImGuiNET.ImGui.SetCursorScreenPos(vp);
                if (action) ActionField("##data", sub, vw, rh, problemLine: false);
                else ConditionField("##data", sub, vw, rh);
                if (narrow) rowH = rh * 2 + 2;
                // Every other row a light shade, reaching halfway into the gaps so the shades meet.
                if (i % 2 == 1)
                    _backs.Add((new Vector2(p.X - 4, p.Y - 4.5f), new Vector2(p.X + rw + 3, p.Y + rowH + 4.5f), StripeOver, 0, 0, depth * 2 + 1));
            }
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + rowH + 5));
            ImGuiNET.ImGui.Dummy(new Vector2(0, 0));

            if (!removed && nests)
            {
                ImGuiNET.ImGui.Indent(childIndent);
                if (sub.Children.Count == 0) Note("None yet: + Add below.");
                SubRules(sub.Children, action: false, depth + 1, sub.Condition == MetaRuleEdit.Not ? 1 : int.MaxValue, rw - childIndent);
                ImGuiNET.ImGui.Unindent(childIndent);
            }
            CardClose(nests, cardTop, w, GroupColor(sub.Condition, false), GroupTint(sub.Condition, false), depth + 1);
            ImGuiNET.ImGui.PopID();
            if (removed)
            {
                subs.RemoveAt(i);
                _editChanged = true;
                break;   // indices shifted: the rest draws next frame
            }
            if (moveTo >= 0)
            {
                (subs[i], subs[moveTo]) = (subs[moveTo], subs[i]);
                _editChanged = true;
                break;
            }
        }

        // Nested groups get their own Add; the top level's is in the column header.
        if (depth == 0 || subs.Count >= maxCount) return;
        ImGuiNET.ImGui.PushID(depth * 1000 + subs.Count);
        if (FlowButton(AddLabel + "##add_sub", Teal, BtnFill, BtnBord, height: 20))
        {
            subs.Add(new MetaRuleDto { State = _editRule.State, Condition = MetaRuleEdit.Never });
            _editChanged = true;
        }
        ImGuiNET.ImGui.PopID();
    }

    /// <summary>[Revert] on the left (once something changed); Delete on its own at the far right.</summary>
    private void EditorFooter(Vector2 p, float w, float h)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        bool canRevert = _editOriginal != null && !MetaRuleEdit.SameContent(_editRule, _editOriginal);
        if (canRevert)
        {
            string label = PhosphorIcons.ArrowCounterClockwise + " Revert";
            if (Button(label + "##ed_revert", p, new Vector2(BtnW(label, 60), h), Mute, BtnFill, BtnBord))
            {
                _editRule = _editOriginal!.Clone();
                _fields.Clear();
                _editChanged = true;
            }
            ImGuiNET.ImGui.SetItemTooltip("Undo every change since you opened this rule (and save that)");
        }
        else
        {
            ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui9));
            dl.AddText(new Vector2(p.X, p.Y + (h - ImGuiNET.ImGui.GetFontSize()) * 0.5f), Faded(Mute),
                _editIndex < 0 ? "Added once it is filled in, or on Back." : "Changes save on their own.");
            ImGuiNET.ImGui.PopFont();
        }

        bool draft = _editIndex < 0;
        if (!_editConfirmDelete)
        {
            string label = PhosphorIcons.Trash + (draft ? " Discard" : " Delete rule");
            float dw = BtnW(label, 70);
            if (Button(label + "##ed_del", new Vector2(p.X + w - dw, p.Y), new Vector2(dw, h), TextDim, DelFill, BtnBord))
            {
                if (draft) { _autosave.Clear(); LeaveEditor(); }
                else _editConfirmDelete = true;
            }
            SafeImGui.SetItemTooltip(draft ? "Drop this new rule (it was never added)" : "Delete this rule (asks first)");
        }
        else
        {
            float kw = BtnW("Keep", 50), yw = BtnW("Delete", 56);
            dl.AddText(new Vector2(p.X + w - kw - yw - 8 - ImGuiNET.ImGui.CalcTextSize("Delete this rule?").X - 6,
                p.Y + (h - ImGuiNET.ImGui.GetFontSize()) * 0.5f), Red, "Delete this rule?");
            if (Button("Delete##ed_del_yes", new Vector2(p.X + w - kw - yw - 4, p.Y), new Vector2(yw, h), TextDim, DelFill, Red))
            {
                DeleteEditedRule();
                return;
            }
            if (Button("Keep##ed_del_no", new Vector2(p.X + w - kw, p.Y), new Vector2(kw, h), Mute, BtnFill, BtnBord))
                _editConfirmDelete = false;
        }
    }

    // =====================================================================
    //  State pickers and new states
    // =====================================================================

    /// <summary>
    /// A state picker ending with "+ New state...". Returns the picked state, or a new state's name
    /// (<paramref name="isNew"/> true) once its name box is confirmed; else null.
    /// </summary>
    private string? StatePicker(string id, string shown, string[] items, int selected, Vector2 pos, float width, out bool isNew,
        float height = 22, string? badge = null)
    {
        isNew = false;
        uint key = ImGuiNET.ImGui.GetID(id);
        if (_namingKey == key) return NamingBox(id, pos, width, height, out isNew);
        int picked = Picker(id, shown, items, selected, pos, width, height, NewStateEntry, badge);
        if (picked == items.Length)
        {
            _namingKey = key;
            Array.Clear(_nameBuf);
            _nameFocus = true;
            _nameWasActive = false;
            _nameProblem = null;
            return null;
        }
        return picked >= 0 ? items[picked] : null;
    }

    /// <summary>The name box a state picker turns into: [name][OK][Cancel]; Enter = OK, Esc = Cancel.</summary>
    private string? NamingBox(string id, Vector2 pos, float width, float height, out bool isNew)
    {
        isNew = false;
        float bw = height + 4;
        float boxW = Math.Max(30, width - 2 * (bw + 3));
        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        if (_nameFocus)
        {
            ImGuiNET.ImGui.SetKeyboardFocusHere();
            _nameFocus = false;
        }
        bool enter = TextBox(id + "_name", _nameBuf, boxW, "New state name...", ImGuiInputTextFlags.EnterReturnsTrue, out bool active, height);
        // Esc deactivates the box in the same frame, so "was it being typed in" is last frame's.
        bool esc = (active || _nameWasActive) && ImGuiNET.ImGui.IsKeyPressed(ImGuiKey.Escape, false);
        _nameWasActive = active;

        string typed = Utf8(_nameBuf);
        _nameProblem = MetaStateName.Problem(typed, _statePicks, out string name);
        if (_nameProblem != null && name.Length > 0)
            ImGuiNET.ImGui.GetWindowDrawList().AddRect(pos, pos + new Vector2(boxW, height), Red, 3);
        bool ok = IconButton(id + "_ok", PhosphorIcons.Check, new Vector2(pos.X + boxW + 3, pos.Y), new Vector2(bw, height), Green, NewFill, Green,
            enabled: _nameProblem == null);
        SafeImGui.SetItemTooltip(_nameProblem ?? $"Make the state '{name}' (Enter)");
        bool cancel = IconButton(id + "_cancel", PhosphorIcons.X, new Vector2(pos.X + boxW + bw + 6, pos.Y), new Vector2(bw, height),
            TextDim, BtnFill, BtnBord);
        ImGuiNET.ImGui.SetItemTooltip("Cancel (Esc)");
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(pos.X, pos.Y + height));
        ImGuiNET.ImGui.Dummy(new Vector2(0, 0));

        if ((enter || ok) && _nameProblem == null)
        {
            CancelNaming();
            AddLocalState(name);
            isNew = true;
            return name;
        }
        if (enter) _nameFocus = true;   // a bad name: keep typing (the reason shows under it)
        if (cancel || esc) CancelNaming();
        return null;
    }

    /// <summary>The name box's problem under a picker in the editor's flow; its height (0 when none).</summary>
    private float ProblemLine(string id, Vector2 pos, float width)
    {
        if (_namingKey == 0 || _namingKey != ImGuiNET.ImGui.GetID(id) || _nameProblem == null || Utf8(_nameBuf).Trim().Length == 0)
            return 0;
        ImFontPtr small = ImGuiFonts.Get(UiFont.Ui9);
        ImGuiNET.ImGui.PushFont(small);
        ImGuiNET.ImGui.GetWindowDrawList().AddText(pos + new Vector2(2, 1), Red, Fit(_nameProblem, width - 4));
        ImGuiNET.ImGui.PopFont();
        return small.FontSize + 3;
    }

    private void CancelNaming()
    {
        _namingKey = 0;
        _nameProblem = null;
    }

    /// <summary>Offers <paramref name="name"/> in every state picker until the snapshot lists it.</summary>
    private void AddLocalState(string name)
    {
        _localStates.Add(name);
        if (IndexOf(_statePicks, name) < 0) _statePicks = With(_statePicks, name);
        if (IndexOf(_statesOnly, name) < 0) _statesOnly = With(_statesOnly, name);
    }

    /// <summary>
    /// Makes state <paramref name="name"/> with its first rule, an empty Never -> None (it never fires),
    /// added at once: a state exists only while a rule uses it, so this is what keeps it through a
    /// save and reload. <paramref name="openIt"/> opens that rule in the editor.
    /// </summary>
    private void CreateState(string name, bool openIt)
    {
        AddLocalState(name);
        MetaRuleDto first = MetaRuleEdit.Placeholder(name);
        int index = _data.Rules.Count;   // add_rule appends
        SendEdit(new MetaCmd { Op = "add_rule", Rule = first.Clone() });
        _data.Rules.Add(first.Clone());
        RebuildCaches();
        if (openIt) OpenEditor(index, first);
    }

    // ── Small helpers ────────────────────────────────────────────────────

    private static int IndexOf(string[] items, string value)
    {
        for (int i = 0; i < items.Length; i++)
            if (string.Equals(items[i], value, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }


    private static void Note(string text)
    {
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui9));
        SafeImGui.TextColored(RynthTheme.VecOf(RynthTheme.MuteAlt), text);
        ImGuiNET.ImGui.PopFont();
    }

    private static void NoteAt(Vector2 p, string text, float height, uint color = 0)
    {
        ImGuiNET.ImGui.GetWindowDrawList().AddText(new Vector2(p.X + 4, p.Y + (height - ImGuiNET.ImGui.GetFontSize()) * 0.5f),
            color != 0 ? color : Faded(Mute), text);
    }
}
