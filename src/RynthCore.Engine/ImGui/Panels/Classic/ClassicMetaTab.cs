// ============================================================================
//  RynthCore.Engine - ImGui/Panels/Classic/ClassicMetaTab.cs
//  The Classic look's Meta tab: RynthAi's meta (UiSources.Meta, the Meta
//  panel's source) in the retail style. Meta on/off, the meta file (loads it),
//  the current state (sets it), and the loaded meta's rules grouped by state
//  (the current state's group in gold): each rule's condition and action,
//  dimmed when the rule is off. Writing rules (the condition and action
//  editors, the source view, the Meta Manager) is the Meta panel's: Edit opens
//  it. Commands are the Meta panel's (MetaCommands). AC's render thread only.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.UI;
using RynthCore.Engine.UI.Data;
using static RynthCore.Engine.ImGuiBackend.Panels.Classic.ClassicKit;

namespace RynthCore.Engine.ImGuiBackend.Panels.Classic;

internal sealed class ClassicMetaTab : IClassicTab
{
    public string Name => "Meta";

    private const int VisibleRows = 17;

    // The list rows: a state heading (Rule < 0) or a rule (its index).
    private readonly struct ListRow
    {
        public readonly string State;
        public readonly int Rule;
        public readonly string Cond, Act;
        public ListRow(string state, int rule, string cond, string act) { State = state; Rule = rule; Cond = cond; Act = act; }
    }

    private MetaPayload? _builtFor;
    private readonly List<ListRow> _rows = new();
    private string[] _fileNames = Array.Empty<string>();
    private string[] _states = Array.Empty<string>();
    private int _first;
    private float _grab;

    public void Shown()
    {
        UiSources.Meta.Subscribe();
        UiSources.Meta.RequestRefresh();
    }

    public void Hidden() => UiSources.Meta.Unsubscribe();

    public float Draw(ClassicContext ctx, Vector2 o, float w)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        MetaPayload? d = UiSources.Meta.Current?.Value;
        if (d != null && !ReferenceEquals(d, _builtFor)) Build(d);
        float y = o.Y;

        string right = d == null ? "waiting for RynthAi" : "state " + d.CurrentState
            + (d.Snapshot is { } snap && snap.SecondsInState > 0 ? $" · {(int)snap.SecondsInState}s" : "");
        Header(dl, new Vector2(o.X, y), w, "Meta", right);
        y += Px(23);

        // On / off.
        {
            bool on = d?.EnableMeta ?? ctx.Raw.MetaEnabled;
            if (Gem("##cme_on", "Meta on", new Vector2(o.X, y), w / 2, on) && d != null)
                MetaCommands.Simple("set_enabled", value: on ? "false" : "true");
            SafeImGui.SetItemTooltip(on ? "The meta's rules run. Click to pause them." : "The meta's rules are paused. Click to run them.");
            y += Px(20);
        }

        // File and state.
        float rowH = Px(22), labelW = Px(52);
        {
            TextMid(dl, o.X + Px(3), y, rowH, "Meta", Text);
            var ddPos = new Vector2(o.X + labelW + Px(6), y + (rowH - Px(19)) * 0.5f);
            float ddW = w - labelW - Px(6) - Px(52);
            int cur = d != null ? MetaVocabulary.CurrentFileIndex(d) : -1;
            if (Dropdown("##cme_file", d != null ? MetaVocabulary.CurrentFileDisplay(d) : "None", ddPos, ddW, ctx.Popup.IsOpen) && d != null)
            {
                MetaPayload data = d;
                ctx.Popup.Open(ddPos + new Vector2(0, Px(19)), ddW, _fileNames, cur, i =>
                {
                    if (i < data.Files.Count) MetaCommands.Simple("load_file", path: data.Files[i].Path);
                });
            }
            SafeImGui.SetItemTooltip("Load a meta");
            if (Plate("##cme_edit", "Edit", new Vector2(o.X + w - Px(46), y + (rowH - Px(17)) * 0.5f)))
                PanelRouter.Toggle("Meta");
            SafeImGui.SetItemTooltip("The Meta panel: write and edit rules, the source view, the Meta Manager");
            y += rowH;
        }
        {
            TextMid(dl, o.X + Px(3), y, rowH, "State", Text);
            var ddPos = new Vector2(o.X + labelW + Px(6), y + (rowH - Px(19)) * 0.5f);
            float ddW = w - labelW - Px(6);
            string state = d?.CurrentState ?? ctx.Raw.CurrentState;
            if (Dropdown("##cme_state", state.Length > 0 ? state : "Default", ddPos, ddW, ctx.Popup.IsOpen) && d != null)
            {
                string[] states = _states;
                ctx.Popup.Open(ddPos + new Vector2(0, Px(19)), ddW, states, Array.IndexOf(states, state),
                    i => MetaCommands.Simple("set_state", value: states[i]));
            }
            SafeImGui.SetItemTooltip("Set the meta's state now");
            y += rowH;
        }

        // Rules by state.
        y += Px(8);
        Header(dl, new Vector2(o.X, y), w, "Rules", d == null ? null : d.Rules.Count + " rules · " + Math.Max(0, _states.Length) + " states");
        y += Px(23);
        float listW = w - Px(16), rh = Px(17), listH = rh * VisibleRows;
        float condW = (listW - Px(26)) * 0.5f;
        var listMin = new Vector2(o.X, y);
        dl.AddRectFilled(listMin, listMin + new Vector2(listW, listH), RynthTheme.Argb(0x8C000000));
        _first = Wheel(listMin, listMin + new Vector2(w, listH), _first, _rows.Count, VisibleRows);
        string current = d?.CurrentState ?? "";
        for (int r = 0; r < VisibleRows && _first + r < _rows.Count; r++)
        {
            ListRow row = _rows[_first + r];
            var p = new Vector2(o.X, y + r * rh);
            if (row.Rule < 0)
            {
                bool isCur = row.State.Equals(current, StringComparison.OrdinalIgnoreCase);
                dl.AddRectFilled(p, p + new Vector2(listW, rh), RynthTheme.Argb(isCur ? 0x40F0C850u : 0x22FFFFFFu));
                TextClip(dl, p.X + Px(4), p.Y, rh, p.X + listW - Px(4), row.State + (isCur ? "  (now)" : ""), isCur ? Gold : GoldDim, 10, bold: true);
                continue;
            }
            MetaRuleDto rule = d!.Rules[row.Rule];
            bool on = rule.Enabled;
            GemPicture(dl, new Vector2(p.X + Px(6), p.Y + (rh - Px(13)) * 0.5f), on);
            float cx = p.X + Px(24);
            TextClip(dl, cx, p.Y, rh, cx + condW - Px(4), row.Cond, on ? White : Faint, 10);
            TextClip(dl, cx + condW, p.Y, rh, p.X + listW - Px(4), row.Act, on ? Amber : Faint, 10);
            ImGuiNET.ImGui.SetCursorScreenPos(p);
            ImGuiNET.ImGui.PushID(_first + r);
            ImGuiNET.ImGui.InvisibleButton("##rule", new Vector2(listW, rh));
            SafeImGui.SetItemTooltip((rule.Name.Length > 0 ? rule.Name + "\n" : "") + "When: " + row.Cond + "\nDo: " + row.Act
                + (on ? "" : "\n(off)") + "\nEdit rules on the Meta panel.");
            ImGuiNET.ImGui.PopID();
        }
        if (_rows.Count == 0)
            TextMid(dl, o.X + Px(6), y, rh, d == null ? "Waiting for RynthAi…" : "No rules. Load a meta, or write one on the Meta panel.", Dim, 10);
        _first = Scrollbar("##cme_sb", new Vector2(o.X + listW, y), listH, _rows.Count, VisibleRows, _first, ref _grab);
        y += listH;
        return y - o.Y;
    }

    private void Build(MetaPayload d)
    {
        _builtFor = d;
        _rows.Clear();
        foreach (var (state, rules) in MetaVocabulary.GroupByState(d.Rules))
        {
            _rows.Add(new ListRow(state, -1, "", ""));
            foreach (int i in rules)
                _rows.Add(new ListRow(state, i, MetaVocabulary.RowConditionText(d.Rules[i]), MetaVocabulary.RowActionText(d.Rules[i])));
        }
        _fileNames = new string[d.Files.Count];
        for (int i = 0; i < d.Files.Count; i++) _fileNames[i] = d.Files[i].Display;
        _states = MetaVocabulary.StatePicks(d);
    }
}
