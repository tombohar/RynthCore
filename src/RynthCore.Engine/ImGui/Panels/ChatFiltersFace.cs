// ============================================================================
//  RynthCore.Engine - ImGui/Panels/ChatFiltersFace.cs
//  ImGui face of the RynthChat rule editor (the old Avalonia face is
//  UI/Panels/RynthChatFiltersPanel.cs). One card per rule:
//
//    [#] [on] [Contains v] [pattern ...................] [up] [down] [delete]
//         When [Any line v]  [Move to v] [Junk v]  [Colour line v] [swatch]
//         (colour: No colour / Colour line / Colour match / Highlight line)
//         (a regex error, a timed-out rule, a missing tab, or its hit count)
//
//  The order and what each action does are spelled out in the header lines
//  (and in UI/Data/ChatData.cs). A pattern is plain text (Contains, Starts
//  with, Ends with) or a regex (ChatRuleMatching.cs). Below the rules: Add,
//  Standard filters (ready-made Hide rules, ChatFilterPresets.cs), Import /
//  Export (the clipboard, JSON), and a test box - paste a line, pick where it
//  came from, and see which rules match and where it ends up, which standard
//  filter would catch it, or make a rule from it. The test runs on an edit,
//  never per frame; the chat itself is routed on the pump.
//
//  The rules live in ChatModel. Each edit applies (the pump re-routes the
//  kept lines within 100 ms) and saves at once.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.UI.Data;
using static RynthCore.Engine.ImGuiBackend.Panels.FaceKit;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed class ChatFiltersFace : IImGuiPanel
{
    public const string Title = "ChatFilters";

    public static void Register() => ImGuiPanelHost.Register(Title,
        new PanelSpec(new Vector2(600, 460), new Vector2(440, 260), EdgeToEdge: true, GripInBody: true),
        () => new ChatFiltersFace());

    private static readonly uint CardBg = RynthTheme.Argb(0xFF0E1822), FocusBorder = RynthTheme.Argb(0xFFFFD700);
    private static uint AddBg => RynthTheme.AccentFill;

    private sealed class Buffers
    {
        public readonly byte[] Pattern = new byte[1024];
        public bool Active;
    }

    private readonly Dictionary<ChatFilterRule, Buffers> _bufs = new();
    private long _seenVersion = -1;
    private readonly Picker _picker = new("##chatrule_pick");
    private string? _flash;
    private double _flashUntil;
    private ChatFilterRule? _focus;
    private double _focusUntil;
    private bool _scrollToFocus;
    private ChatFilterRule? _newTabFor;
    private bool _openNewTab, _focusNewTab;
    private readonly byte[] _newTab = new byte[64];

    // Test box: re-evaluated when the text, its source or the rules change.
    private readonly byte[] _test = new byte[512];
    private int _testSource = 1;   // index into ChatModel.Conditions (not 0, "any")
    private string _testSeen = "";
    private int _testSourceSeen = -1;
    private long _testVersion = -1;
    private readonly List<(string Text, uint Color)> _testResult = new();
    private string _testLine = "";   // the tested text, timestamp removed ("Make rule" uses it)

    public void OnShown() => ChatModel.EnsureSettingsLoaded();

    private const string AddLabel = PhosphorIcons.Plus + " Add rule";
    private const string ImportLabel = PhosphorIcons.ClipboardText + " Import";
    private const string ExportLabel = PhosphorIcons.Copy + " Export";
    private const string PresetsLabel = PhosphorIcons.ListChecks + " Standard filters";
    private const string MakeRuleLabel = PhosphorIcons.Plus + " Make rule";
    private const string Caret = " " + PhosphorIcons.CaretDown;

    private static readonly string[] ActionLabels = { "Move to", "Copy to", "Hide", "Colour only" };
    private static readonly string[] ActionHelp =
    {
        "Move: the line shows ONLY in the chosen tab (it leaves All and its channel tab).",
        "Copy: the line also shows in the chosen tab (it stays where it was).",
        "Hide: the line is not shown anywhere (the chat log still has it).",
        "Colour only: no routing; the rule just colours.",
    };
    // Indexed by ChatColorMode.
    private static readonly string[] ColorLabels = { "No colour", "Colour line", "Colour match", "Highlight line" };
    private static readonly string[] ColorHelp =
    {
        "No colour: the rule doesn't colour the line.",
        "Colour line: the whole line's text in this colour.",
        "Colour match: just the text the rule matched, in this colour.",
        "Highlight line: a band in this colour behind the whole line; the text keeps its colour.",
    };
    private static readonly string[] ModeHelp =
    {
        "Regex: a case-insensitive regular expression (^ start, $ end, \\d+ a number...).",
        "Contains: the text anywhere in the line. Any case; . ( * \" are just characters.",
        "Starts with: the line begins with the text. Any case.",
        "Ends with: the line ends with the text. Any case.",
    };
    private static readonly string[] ModeHints =
    {
        "regex, e.g. ^You have been healed",
        "text, e.g. evaded your attack",
        "text, e.g. You say, \"",
        "text, e.g. has expired.",
    };

    public void Draw()
    {
        float w = Begin(out Vector2 origin, out Vector2 size);
        ChatFilterRule[] rules = ChatModel.Filters;
        SyncBuffers(rules);
        if (ChatModel.FocusRule is { } focus)
        {
            ChatModel.FocusRule = null;
            _focus = focus;
            _focusUntil = ImGuiNET.ImGui.GetTime() + 2.5;
            _scrollToFocus = true;
        }

        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui9));
        Label("Rules run top to bottom (" + PhosphorIcons.ArrowUp + PhosphorIcons.ArrowDown + " to reorder) on each line's text: plain text (any case) or a regex.", Mute);
        Label("Where it shows: Copy rules add a tab and go on; the first Move or Hide that matches decides.", Mute);
        Label("Colour: the first \"Colour line\" wins, and the first \"Highlight line\"; \"Colour match\" colours every match (a higher rule wins an overlap).", Mute);
        ImGuiNET.ImGui.PopFont();
        ImGuiNET.ImGui.Dummy(new Vector2(0, 1));

        float testH = TestAreaHeight();
        Action? edit = null;
        float listH = Math.Max(60, Remaining(origin, size) - 30 - testH);
        ImGuiNET.ImGui.BeginChild("##filter_rows", new Vector2(w, listH));
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg, BtnFill);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.CheckMark, Teal);
        float rowW = ImGuiNET.ImGui.GetContentRegionAvail().X - 2;
        for (int i = 0; i < rules.Length; i++)
        {
            ImGuiNET.ImGui.PushID(i);
            DrawRule(rules, i, rowW, ref edit);
            ImGuiNET.ImGui.PopID();
        }
        if (rules.Length == 0)
        {
            Label("No rules yet. Click \"Add rule\", or right-click a line in the chat", Mute);
            Label("and pick \"Move them to\", \"Hide them\" or \"Colour them\".", Mute);
        }
        DrawStandardFilters(rowW);
        ImGuiNET.ImGui.PopStyleColor(2);
        ImGuiNET.ImGui.EndChild();

        // Add / Import / Export
        Vector2 ap = ImGuiNET.ImGui.GetCursorScreenPos();
        float bx = ap.X;
        if (Button("##add", AddLabel, new Vector2(bx, ap.Y), new Vector2(ButtonWidth(AddLabel) + 4, 24), Text, AddBg))
        {
            // An empty pattern matches nothing until typed; saved now so the row survives a relaunch mid-edit.
            // New rules are plain text (Contains); rules made from a chat line stay regexes.
            var rule = new ChatFilterRule { Mode = ChatMatchMode.Contains };
            edit = () => ChatModel.AddRule(rule);
        }
        bx += ButtonWidth(AddLabel) + 4;
        if (Button("##presets", PresetsLabel, new Vector2(bx, ap.Y), new Vector2(ButtonWidth(PresetsLabel), 24), Text, BtnFill))
            OpenPresetPicker(new Vector2(bx, ap.Y));
        ImGuiNET.ImGui.SetItemTooltip("Ready-made rules that hide common game spam (spell words, evades, busy...).\nPicking one adds it as a Hide rule you can edit or delete.");
        bx += ButtonWidth(PresetsLabel) + 8;
        if (Button("##import", ImportLabel, new Vector2(bx, ap.Y), new Vector2(ButtonWidth(ImportLabel), 24), Text, BtnFill))
            Flash(ChatModel.ImportRules(ChatModel.GetClipboardText()));
        ImGuiNET.ImGui.SetItemTooltip("Add the rules on the clipboard (an Export, or a rynthchat_settings.json) after these.");
        bx += ButtonWidth(ImportLabel) + 4;
        if (Button("##export", ExportLabel, new Vector2(bx, ap.Y), new Vector2(ButtonWidth(ExportLabel), 24), Text, BtnFill, rules.Length > 0))
            Flash(ChatModel.SetClipboardText(ChatModel.ExportRules())
                ? $"Copied {rules.Length} rule{(rules.Length == 1 ? "" : "s")} to the clipboard"
                : "Clipboard busy - try again");
        ImGuiNET.ImGui.SetItemTooltip("Copy every rule and your tabs to the clipboard as JSON, to share or keep.");
        bx += ButtonWidth(ExportLabel) + 8;
        if (_flash != null)
        {
            if (ImGuiNET.ImGui.GetTime() > _flashUntil) _flash = null;
            else ImGuiNET.ImGui.GetWindowDrawList().AddText(new Vector2(bx, ap.Y + (24 - ImGuiNET.ImGui.GetFontSize()) * 0.5f), Amber, _flash);
        }
        NextLine(ap, 28);

        DrawTest(w);

        End(origin, size);
        _picker.Draw();
        DrawNewTabPopup();
        edit?.Invoke();   // after drawing: the rows above drew from the old list
    }

    // ── Standard filters (RynthChatPresets) ──────────────────────────────

    /// <summary>
    /// The canned filters, by group: tick to hide, or pick a tab to move the lines there.
    /// The router checks them after the rules, for lines no rule moved or hid.
    /// </summary>
    private void DrawStandardFilters(float rowW)
    {
        ImGuiNET.ImGui.Dummy(new Vector2(0, 6));
        if (!ImGuiNET.ImGui.CollapsingHeader("Standard filters##std")) return;
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui9));
        Label("Checked after the rules above, only for lines no rule moved or hid.", Mute);
        Label("Tick to hide the lines; pick a tab to move them there instead.", Mute);
        ImGuiNET.ImGui.PopFont();
        bool retail = ChatModel.HideInRetailChat;
        if (ImGuiNET.ImGui.Checkbox("Hidden lines are hidden in the retail chat too##hideretail", ref retail))
        {
            ChatModel.HideInRetailChat = retail;
            ChatModel.SaveSettings();
        }
        SafeImGui.SetItemTooltip("On: a line a rule or standard filter hides doesn't print in AC's own chat window either.\n" +
            "Off: hiding only applies to RynthChat; the retail chat shows everything.\n" +
            "Moved / copied lines still show in the retail chat either way.");
        foreach (string group in UI.Panels.RynthChatPresets.Groups)
        {
            int on = 0, total = 0;
            foreach (var p in UI.Panels.RynthChatPresets.All)
                if (p.Group == group) { total++; if (p.Enabled) on++; }
            if (!ImGuiNET.ImGui.TreeNode($"{group} ({on}/{total})###grp_{group}")) continue;
            foreach (var p in UI.Panels.RynthChatPresets.All)
            {
                if (p.Group != group) continue;
                ImGuiNET.ImGui.PushID(p.Id);
                bool enabled = p.Enabled;
                if (ImGuiNET.ImGui.Checkbox(p.Label, ref enabled)) { p.Enabled = enabled; ChatModel.FiltersChanged(); }
                SafeImGui.SetItemTooltip(p.Description + "\nExample: " + p.Example);
                ImGuiNET.ImGui.SameLine(Math.Max(ImGuiNET.ImGui.GetCursorPosX() + 8, rowW * 0.6f));
                string tabText = (p.Tab.Length == 0 ? "hide" : "to " + p.Tab) + Caret;
                if (ImGuiNET.ImGui.SmallButton(tabText))
                    OpenPresetTabPicker(p, new Vector2(ImGuiNET.ImGui.GetItemRectMin().X, ImGuiNET.ImGui.GetItemRectMax().Y + 2));
                ImGuiNET.ImGui.SetItemTooltip("Where its lines go: hidden, or moved to a tab");
                ImGuiNET.ImGui.PopID();
            }
            ImGuiNET.ImGui.TreePop();
        }
    }

    private void OpenPresetTabPicker(UI.Panels.RynthChatPresets.Preset preset, Vector2 below)
    {
        string[] tabs = ChatModel.TargetTabs();
        var items = new string[tabs.Length + 1];
        items[0] = "Hide";
        Array.Copy(tabs, 0, items, 1, tabs.Length);
        int selected = preset.Tab.Length == 0 ? 0 : Math.Max(0, Array.FindIndex(tabs, t => string.Equals(t, preset.Tab, StringComparison.OrdinalIgnoreCase)) + 1);
        _picker.Open(below, items, selected, k =>
        {
            preset.Tab = k <= 0 ? "" : items[k];
            ChatModel.FiltersChanged();
        }, 160);
    }

    private void Flash(string message)
    {
        _flash = message;
        _flashUntil = ImGuiNET.ImGui.GetTime() + 3;
    }

    // ── One rule ─────────────────────────────────────────────────────────

    private void DrawRule(ChatFilterRule[] rules, int i, float rowW, ref Action? edit)
    {
        ChatFilterRule r = rules[i];
        Buffers b = _bufs[r];
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        Vector2 card = ImGuiNET.ImGui.GetCursorScreenPos();
        dl.ChannelsSplit(2);
        dl.ChannelsSetCurrent(1);
        const float rowH = 22;

        // Row 1: number, on, pattern, up, down, delete.
        Vector2 p = card + new Vector2(4, 4);
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui9));
        dl.AddText(new Vector2(p.X, p.Y + 5), Faded(Mute), (i + 1).ToString());
        ImGuiNET.ImGui.PopFont();
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X + 16, p.Y + 1));
        bool on = r.Enabled;
        if (ImGuiNET.ImGui.Checkbox("##on", ref on)) { r.Enabled = on; ChatModel.FiltersChanged(); }
        SafeImGui.SetItemTooltip(on ? "Rule on (click to turn off)" : "Rule off (click to turn on)");

        const float btn = 22;
        float x = p.X + 42;
        int modeIdx = (int)r.Mode;
        string modeText = ChatRuleMatching.Labels[modeIdx] + Caret;
        float modeW = ButtonWidth(ChatRuleMatching.Labels[2] + Caret);   // the widest, so patterns line up
        ChatFilterRule moded = r;
        if (Button("##mode", modeText, new Vector2(x, p.Y), new Vector2(modeW, rowH), Text, BtnFill, leftAlign: true))
            _picker.Open(new Vector2(x, p.Y + rowH + 2), ChatRuleMatching.Labels, modeIdx, k =>
            {
                moded.Mode = (ChatMatchMode)k;
                moded.Recompile();
                ChatModel.FiltersChanged();
            }, 120);
        SafeImGui.SetItemTooltip(ModeHelp[modeIdx]);
        x += modeW + 4;
        float patW = Math.Max(80, card.X + rowW - 4 - 3 * (btn + 2) - 4 - x);
        if (TextBox("##pat", b.Pattern, new Vector2(x, p.Y), patW, ModeHints[modeIdx], out bool patActive))
        {
            r.Pattern = Utf8(b.Pattern);
            r.Recompile();
            ChatModel.FiltersChanged();
        }
        b.Active = patActive;
        if (r.Invalid && r.Pattern.Length > 0)
            dl.AddRect(new Vector2(x, p.Y), new Vector2(x + patW, p.Y + rowH), Red, 3);
        else if (r.Slow)
            dl.AddRect(new Vector2(x, p.Y), new Vector2(x + patW, p.Y + rowH), Amber, 3);
        x += patW + 4;
        ChatFilterRule rule = r;
        if (IconButton("##up", PhosphorIcons.ArrowUp, new Vector2(x, p.Y), new Vector2(btn, rowH), Text, BtnFill, i > 0))
            edit = () => ChatModel.MoveFilter(rule, -1);
        ImGuiNET.ImGui.SetItemTooltip("Move up (runs earlier)");
        x += btn + 2;
        if (IconButton("##down", PhosphorIcons.ArrowDown, new Vector2(x, p.Y), new Vector2(btn, rowH), Text, BtnFill, i < rules.Length - 1))
            edit = () => ChatModel.MoveFilter(rule, +1);
        ImGuiNET.ImGui.SetItemTooltip("Move down (runs later)");
        x += btn + 2;
        if (IconButton("##del", PhosphorIcons.Trash, new Vector2(x, p.Y), new Vector2(btn, rowH), Red, BtnFill))
            edit = () => ChatModel.EditFilters(list => list.Remove(rule));
        ImGuiNET.ImGui.SetItemTooltip("Delete this rule");

        // Row 2 (wraps when narrow): When, action, tab, colour mode, swatch.
        float left = p.X + 42, right = card.X + rowW - 4;
        var flow = new Flow(left, p.Y + rowH + 4, right, rowH);

        string whenText = "When: " + ChatModel.ConditionLabel(r.When) + Caret;
        Vector2 wp = flow.Place(Math.Min(ButtonWidth(whenText), 240));
        if (Button("##when", whenText, wp, new Vector2(Math.Min(ButtonWidth(whenText), 240), rowH), Text, BtnFill, leftAlign: true))
            OpenWhenPicker(rule, wp);
        ImGuiNET.ImGui.SetItemTooltip("Only lines from this channel or chat type (Any line: every line)");

        int action = (int)r.Action;
        string actText = ActionLabels[action] + Caret;
        Vector2 aPos = flow.Place(ButtonWidth(actText));
        if (Button("##act", actText, aPos, new Vector2(ButtonWidth(actText), rowH), Text, BtnFill))
            _picker.Open(aPos + new Vector2(0, rowH + 2), ActionLabels, action, k =>
            {
                rule.Action = (ChatRuleAction)k;
                if (rule.Action == ChatRuleAction.Color && rule.ColorMode == ChatColorMode.None) rule.ColorMode = ChatColorMode.Line;
                ChatModel.FiltersChanged();
            }, 140);
        SafeImGui.SetItemTooltip(ActionHelp[action]);

        if (r.Action is ChatRuleAction.Move or ChatRuleAction.Copy)
        {
            bool noTab = r.Tab.Length == 0;
            string tabText = (noTab ? "pick a tab" : r.Tab) + Caret;
            Vector2 tPos = flow.Place(ButtonWidth(tabText));
            if (Button("##tab", tabText, tPos, new Vector2(ButtonWidth(tabText), rowH), noTab ? Amber : Teal, BtnFill))
                OpenTabPicker(rule, tPos);
            ImGuiNET.ImGui.SetItemTooltip("The tab it goes to (pick \"New tab…\" to make one)");
        }

        if (r.Action != ChatRuleAction.Hide)
        {
            int mode = Math.Clamp((int)r.ColorMode, 0, ColorLabels.Length - 1);
            string colText = ColorLabels[mode] + Caret;
            Vector2 cPos = flow.Place(ButtonWidth(colText));
            if (Button("##colmode", colText, cPos, new Vector2(ButtonWidth(colText), rowH), Text, BtnFill))
                _picker.Open(cPos + new Vector2(0, rowH + 2), ColorLabels, mode, k =>
                {
                    rule.ColorMode = (ChatColorMode)k;
                    ChatModel.FiltersChanged();
                }, 140);
            SafeImGui.SetItemTooltip(ColorHelp[mode]);
            if (r.ColorMode != ChatColorMode.None)
            {
                Vector2 sPos = flow.Place(rowH);
                ImGuiNET.ImGui.SetCursorScreenPos(sPos + new Vector2(2, 2));
                Vector4 v = RynthTheme.Vec(r.ColorArgb);
                var rgb = new Vector3(v.X, v.Y, v.Z);
                ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(4, 3));
                if (ImGuiNET.ImGui.ColorEdit3("##col", ref rgb, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel))
                {
                    r.ColorArgb = 0xFF000000
                        | ((uint)Math.Clamp((int)MathF.Round(rgb.X * 255), 0, 255) << 16)
                        | ((uint)Math.Clamp((int)MathF.Round(rgb.Y * 255), 0, 255) << 8)
                        | (uint)Math.Clamp((int)MathF.Round(rgb.Z * 255), 0, 255);
                    ChatModel.FiltersChanged();
                }
                ImGuiNET.ImGui.PopStyleVar();
            }
        }

        // Row 3: what's wrong, or what it's doing.
        float y = flow.Bottom + 3;
        (string? note, uint noteColor) = RuleNote(r);
        if (note != null)
        {
            ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui9));
            ImGuiNET.ImGui.PushClipRect(new Vector2(left, y), new Vector2(right, y + 40), true);
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(left, y));
            ImGuiNET.ImGui.PushTextWrapPos(right - ImGuiNET.ImGui.GetWindowPos().X + ImGuiNET.ImGui.GetScrollX());
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, noteColor);
            ImGuiNET.ImGui.TextUnformatted(note);
            ImGuiNET.ImGui.PopStyleColor();
            ImGuiNET.ImGui.PopTextWrapPos();
            y = ImGuiNET.ImGui.GetCursorScreenPos().Y;
            ImGuiNET.ImGui.PopClipRect();
            ImGuiNET.ImGui.PopFont();
        }
        float bottom = Math.Max(y, flow.Bottom + 2) + 4;

        // Card background under everything (channel 0), dimmed while off.
        dl.ChannelsSetCurrent(0);
        bool focused = ReferenceEquals(r, _focus) && ImGuiNET.ImGui.GetTime() < _focusUntil;
        dl.AddRectFilled(card, new Vector2(card.X + rowW, bottom), r.Enabled ? CardBg : RowAlt, 4);
        dl.AddRect(card, new Vector2(card.X + rowW, bottom), focused ? FocusBorder : BtnBord, 4);
        dl.ChannelsMerge();
        if (!r.Enabled)
            dl.AddRectFilled(card, new Vector2(card.X + rowW, bottom), RynthTheme.Argb(0x55000000), 4);

        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(card.X, bottom));
        ImGuiNET.ImGui.Dummy(new Vector2(rowW, 4));
        if (_scrollToFocus && ReferenceEquals(r, _focus))
        {
            _scrollToFocus = false;
            ImGuiNET.ImGui.SetScrollHereY(1f);   // bring the new rule into view
        }
    }

    private static (string? Note, uint Color) RuleNote(ChatFilterRule r)
    {
        if (r.Pattern.Trim().Length == 0) return ("Type a pattern: the rule matches nothing until then.", Faded(Mute));
        if (r.Invalid) return ("Regex error: " + (r.Error ?? "doesn't compile"), Red);
        if (r.Slow) return ("Too slow: matching it took too long (see the RynthCore log), so the rule is skipped. Edit the pattern to try again.", Amber);
        if (r.Action is ChatRuleAction.Move or ChatRuleAction.Copy && r.Tab.Length == 0)
            return ("Pick a tab: until then it only colours.", Amber);
        if (!r.Enabled) return ("Off.", Faded(Mute));
        int hits = System.Threading.Volatile.Read(ref r.Hits);
        return (hits == 0 ? "No recent line matches." : $"Matches {hits} recent line{(hits == 1 ? "" : "s")}.", Faded(Mute));
    }

    /// <summary>Left-to-right placement that wraps to a new row when the next item doesn't fit.</summary>
    private struct Flow
    {
        private readonly float _left, _right, _h;
        private float _x, _y;
        public Flow(float left, float top, float right, float h) { _left = _x = left; _right = right; _y = top; _h = h; }
        public float Bottom => _y + _h;

        public Vector2 Place(float w)
        {
            if (_x > _left && _x + w > _right) { _x = _left; _y += _h + 3; }
            var at = new Vector2(_x, _y);
            _x += w + 4;
            return at;
        }
    }

    /// <summary>The standard filters; a tick marks the ones already in the rules (picking one again turns it on).</summary>
    private void OpenPresetPicker(Vector2 at)
    {
        ChatFilterPreset[] presets = ChatFilterPresets.All;
        var labels = new string[presets.Length];
        for (int k = 0; k < presets.Length; k++)
        {
            ChatFilterRule? have = ChatModel.FindPresetRule(presets[k]);
            string mark = have == null ? "   " : have.Enabled ? PhosphorIcons.Check + " " : "(off) ";
            labels[k] = mark + presets[k].PickerLabel;
        }
        _picker.Open(at + new Vector2(0, 26), labels, -1, k => Flash(ChatModel.AddPresetRule(presets[k])), 300);
    }

    private void OpenWhenPicker(ChatFilterRule rule, Vector2 at)
    {
        var labels = new string[ChatModel.Conditions.Length];
        int sel = 0;
        for (int k = 0; k < labels.Length; k++)
        {
            labels[k] = ChatModel.Conditions[k].Label;
            if (string.Equals(ChatModel.Conditions[k].Value, rule.When, StringComparison.OrdinalIgnoreCase)) sel = k;
        }
        _picker.Open(at + new Vector2(0, 24), labels, sel, k =>
        {
            rule.When = ChatModel.Conditions[k].Value;
            ChatModel.FiltersChanged();
        }, 260);
    }

    private void OpenTabPicker(ChatFilterRule rule, Vector2 at)
    {
        string[] tabs = ChatModel.TargetTabs();
        var items = new string[tabs.Length + 1];
        Array.Copy(tabs, items, tabs.Length);
        items[^1] = PhosphorIcons.Plus + " New tab…";
        int sel = Array.FindIndex(tabs, t => string.Equals(t, rule.Tab, StringComparison.OrdinalIgnoreCase));
        _picker.Open(at + new Vector2(0, 24), items, sel, k =>
        {
            if (k == tabs.Length)
            {
                _newTabFor = rule;
                WriteUtf8(_newTab, "");
                _openNewTab = _focusNewTab = true;
                return;
            }
            rule.Tab = tabs[k];
            ChatModel.FiltersChanged();
        }, 180);
    }

    private void DrawNewTabPopup()
    {
        if (_openNewTab) { _openNewTab = false; ImGuiNET.ImGui.OpenPopup("##chatrule_newtab"); }
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, ShellBg);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, Teal);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8, 6));
        bool open = ImGuiNET.ImGui.BeginPopup("##chatrule_newtab");
        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.PopStyleColor(2);
        if (!open) return;
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui10));
        if (_focusNewTab) { ImGuiNET.ImGui.SetKeyboardFocusHere(); _focusNewTab = false; }
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        bool enter = TextBox("##ruletab_name", _newTab, p, 140, "new tab name", out _, ImGuiInputTextFlags.EnterReturnsTrue, 20);
        bool add = Button("##ruletab_add", "Add", new Vector2(p.X + 144, p.Y), new Vector2(ButtonWidth("Add"), 20), Text, AddBg);
        NextLine(p, 20);
        if (enter || add)
        {
            string? name = ChatModel.AddCustomTab(Utf8(_newTab), select: false);
            if (name != null && _newTabFor != null)
            {
                _newTabFor.Tab = name;
                ChatModel.FiltersChanged();
            }
            _newTabFor = null;
            ImGuiNET.ImGui.CloseCurrentPopup();
        }
        else if (ImGuiNET.ImGui.IsKeyPressed(ImGuiKey.Escape))
            ImGuiNET.ImGui.CloseCurrentPopup();
        ImGuiNET.ImGui.PopFont();
        ImGuiNET.ImGui.EndPopup();
    }

    // ── Test box ─────────────────────────────────────────────────────────

    private float TestAreaHeight()
    {
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui9));
        float lh = ImGuiNET.ImGui.GetTextLineHeightWithSpacing();
        ImGuiNET.ImGui.PopFont();
        return 16 + 26 + Math.Max(1, _testResult.Count) * lh + 4;
    }

    private void DrawTest(float w)
    {
        Label("Test a line - paste one from the chat (right-click it, Copy line):", Mute);
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        string srcLabel = "from: " + ChatModel.Conditions[_testSource].Label + Caret;
        float srcW = Math.Min(ButtonWidth(srcLabel), 230);
        float makeW = ButtonWidth(MakeRuleLabel);
        TextBox("##test", _test, p, Math.Max(80, w - srcW - makeW - 8), "e.g. Bob tells you, \"hi\"", out _);
        var mp = new Vector2(p.X + w - srcW - makeW - 4, p.Y);
        if (Button("##makerule", MakeRuleLabel, mp, new Vector2(makeW, 22), Text, AddBg, _testLine.Length > 0))
        {
            // Plain text, held to where the line came from; pick what it does on the new card.
            string when = ChatModel.Conditions[_testSource].Value;
            string pattern = _testLine.Length > 140 ? _testLine[..140] : _testLine;
            ChatModel.AddRule(new ChatFilterRule { Mode = ChatMatchMode.Contains, Pattern = pattern, When = when });
            Flash("Made a Contains rule from the test line: trim it down and pick what it does.");
        }
        ImGuiNET.ImGui.SetItemTooltip("Add a rule that matches this line as plain text (Contains), for lines from the source picked here.");
        var sp = new Vector2(p.X + w - srcW, p.Y);
        if (Button("##testsrc", srcLabel, sp, new Vector2(srcW, 22), Text, BtnFill, leftAlign: true))
        {
            var labels = new string[ChatModel.Conditions.Length - 1];
            for (int k = 1; k < ChatModel.Conditions.Length; k++) labels[k - 1] = ChatModel.Conditions[k].Label;
            _picker.Open(sp + new Vector2(0, 24), labels, _testSource - 1, k => _testSource = k + 1, 260);
        }
        ImGuiNET.ImGui.SetItemTooltip("Where the test line came from (for rules with a When)");
        NextLine(p, 26);

        string text = Utf8(_test);
        long version = ChatModel.FiltersVersion;
        if (text != _testSeen || _testSource != _testSourceSeen || version != _testVersion)
        {
            _testSeen = text;
            _testSourceSeen = _testSource;
            _testVersion = version;
            RunTest(text);
        }
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui9));
        // The result lines reach the body's bottom edge: a long one is cut short of
        // the resize grip (GripInBody) instead of running under it.
        float grip = ImGuiPanelHost.BodyGripReserve;
        if (grip > 0)
        {
            Vector2 at = ImGuiNET.ImGui.GetCursorScreenPos();
            ImGuiNET.ImGui.PushClipRect(at, new Vector2(at.X + w - grip, at.Y + 4096), true);
        }
        foreach ((string line, uint color) in _testResult) Label(line, color);
        if (grip > 0) ImGuiNET.ImGui.PopClipRect();
        ImGuiNET.ImGui.PopFont();
    }

    /// <summary>Runs the rules over the test line. On an edit only (UI thread; each regex has its timeout).</summary>
    private void RunTest(string text)
    {
        _testResult.Clear();
        text = text.Trim();
        // A line copied from the chat starts with its timestamp; the rules never see that.
        if (text.Length > 9 && text[2] == ':' && text[5] == ':' && text[8] == ' ' && char.IsDigit(text[0])) text = text[9..];
        _testLine = text;
        if (text.Length == 0) { _testResult.Add(("Paste a line to see which rules match and where it goes.", Faded(Mute))); return; }

        string when = ChatModel.Conditions[_testSource].Value;
        string channel = "Chat";
        int type = -1;
        if (when.StartsWith("chan:", StringComparison.Ordinal)) channel = when[5..];
        else if (when.StartsWith("type:", StringComparison.Ordinal) && int.TryParse(when[5..], out int t))
        {
            type = t;
            channel = ChatModel.ChannelForType(t);
        }
        if (text.StartsWith("[Rynth", StringComparison.OrdinalIgnoreCase)) channel = "Rynth";

        var trace = new List<ChatRouter.Step>();
        ChatRoute route = ChatModel.TestRouter().Evaluate(text, channel, type, trace);
        foreach (ChatRouter.Step step in trace)
            _testResult.Add((step.Rule switch
            {
                ChatRouter.StandardFilterStep => "Standard",
                < 0 => "Your name",
                _ => $"Rule {step.Rule + 1}",
            } + ": " + step.What, Text));
        if (trace.Count == 0) _testResult.Add(("No rule matches.", Faded(Mute)));

        string where;
        if (route.Hidden) where = "Hidden: shown nowhere.";
        else
        {
            var tabs = new List<string>();
            if (route.InHome) { tabs.Add("All"); tabs.Add(channel); }
            if (route.Tabs != null) tabs.AddRange(route.Tabs);
            where = tabs.Count == 0 ? "Shown nowhere." : "Shows in: " + string.Join(", ", tabs) + ".";
        }
        uint color = RynthTheme.Argb(ChatLineColors.Resolve(route.LineArgb, ChatLineColors.KindOf(type, channel, text), channel, type));
        _testResult.Add((where + (route.LineArgb != null ? "  (this is its colour)" : ""), route.Hidden ? Faded(Mute) : color));
        if (!route.Hidden && route.HighlightArgb is uint band)
            _testResult.Add(($"Highlighted: a {ChatModel.FormatColor(band | 0xFF000000)} band behind the line (this colour).",
                RynthTheme.Argb(band | 0xFF000000)));

        // Not hidden: name a standard filter that would hide it (one not in the rules yet).
        if (!route.Hidden)
            foreach (ChatFilterPreset preset in ChatFilterPresets.All)
                if (preset.Matches(text, channel, type) && ChatModel.FindPresetRule(preset) == null)
                {
                    _testResult.Add(($"Standard filter \"{preset.PickerLabel}\" would hide it (Standard filters, above).", Teal));
                    break;
                }
    }

    /// <summary>Buffers for each rule; refilled from the rules when another face changed them,
    /// except the row being typed in.</summary>
    private void SyncBuffers(ChatFilterRule[] rules)
    {
        long version = ChatModel.FiltersVersion;
        bool changed = version != _seenVersion;
        _seenVersion = version;
        foreach (ChatFilterRule r in rules)
        {
            if (!_bufs.TryGetValue(r, out Buffers? b))
            {
                _bufs[r] = b = new Buffers();
                WriteUtf8(b.Pattern, r.Pattern);
            }
            else if (changed && !b.Active)
                WriteUtf8(b.Pattern, r.Pattern);
        }
        if (_bufs.Count > rules.Length)
        {
            var live = new HashSet<ChatFilterRule>(rules);
            foreach (ChatFilterRule gone in new List<ChatFilterRule>(_bufs.Keys))
                if (!live.Contains(gone)) _bufs.Remove(gone);
        }
    }
}
