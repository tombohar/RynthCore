// ============================================================================
//  RynthCore.Engine - ImGui/Panels/Classic/ClassicLootTab.cs
//  The Classic look's Loot tab, in the retail style:
//    Looting        the subsystem gem and the loot profile RynthAi loots with
//                   (the dashboard's Loot picker)
//    Rules          the loot profile being edited (RynthAi's editing session,
//                   UiSources.LootEdit, shared with the Loot Editor): each rule
//                   with its on/off gem, its action (Keep, Keep #, Salvage,
//                   Sell, Read) and summary; move up / down, duplicate, delete,
//                   add; Save and Reload; RynthAi's last message.
//  A rule's conditions are written in the Loot Editor (the Editor plate opens it
//  on this file). Opening another file waits for unsaved changes to be saved or
//  reloaded (the Loot Editor asks about discarding them; this tab doesn't).
//  Commands are the Loot Editor's (LootEditCommands, each naming the rule it
//  expects at that index). AC's render thread only.
// ============================================================================

using System;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.UI;
using RynthCore.Engine.UI.Data;
using static RynthCore.Engine.ImGuiBackend.Panels.Classic.ClassicKit;

namespace RynthCore.Engine.ImGuiBackend.Panels.Classic;

internal sealed class ClassicLootTab : IClassicTab
{
    public string Name => "Loot";

    private const int VisibleRows = 14;

    private int _selected = -1, _first;
    private float _grab;
    private LootEditStateDto? _filesFor;
    private string[] _files = Array.Empty<string>();

    public void Shown()
    {
        UiSources.LootEdit.Subscribe();
        UiSources.LootEdit.RequestRefresh();
    }

    public void Hidden() => UiSources.LootEdit.Unsubscribe();

    public float Draw(ClassicContext ctx, Vector2 o, float w)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        RynthAiSnapshot raw = ctx.Raw;
        LootEditView? view = UiSources.LootEdit.Current?.Value;
        LootEditStateDto? s = view?.State;
        float y = o.Y;
        float rowH = Px(22), labelW = Px(52);

        // ── Looting ──
        Header(dl, new Vector2(o.X, y), w, "Looting", raw.LootingEnabled ? "on" : "off");
        y += Px(23);
        {
            bool on = raw.LootingEnabled;
            if (Gem("##cl_on", "Looting", new Vector2(o.X, y), w / 2, on))
                RynthAiCommands.SetSubsystemEnabled(3, !on);
            SafeImGui.SetItemTooltip(on ? "Looting on. Click to turn it off." : "Looting off. Click to turn it on.");
            y += Px(20);
            TextMid(dl, o.X + Px(3), y, rowH, "Loot with", Text);
            var ddPos = new Vector2(o.X + labelW + Px(14), y + (rowH - Px(19)) * 0.5f);
            float ddW = w - labelW - Px(14);
            if (Dropdown("##cl_use", ctx.View?.LootText ?? "None", ddPos, ddW, ctx.Popup.IsOpen) && raw.LootProfiles.Length > 0)
                ctx.Popup.Open(ddPos + new Vector2(0, Px(19)), ddW, raw.LootProfiles, raw.SelectedLootIdx, i => RynthAiCommands.SelectProfile(1, i));
            SafeImGui.SetItemTooltip("The loot profile RynthAi loots with");
            y += rowH;
        }

        // ── The profile being edited ──
        y += Px(8);
        string right = view == null ? "waiting for RynthAi" : view.Unavailable ? "needs a newer RynthAi"
            : s == null ? "loading" : s.ReadOnly ? "read only" : s.Dirty ? "unsaved changes" : s.Rules.Count + " rules";
        Header(dl, new Vector2(o.X, y), w, "Rules", right);
        y += Px(23);
        {
            TextMid(dl, o.X + Px(3), y, rowH, "Editing", Text);
            var ddPos = new Vector2(o.X + labelW + Px(14), y + (rowH - Px(19)) * 0.5f);
            float ddW = w - labelW - Px(14) - Px(52);
            string[] files = Files(s);
            int cur = -1;
            if (s != null) for (int i = 0; i < s.Files.Count; i++) if (SamePath(s.Files[i].Path, s.Path)) { cur = i; break; }
            string shown = s == null || s.FileName.Length == 0 ? "None" : s.FileName;
            if (Dropdown("##cl_file", shown, ddPos, ddW, ctx.Popup.IsOpen) && s != null && !s.Dirty)
            {
                LootEditStateDto st = s;
                ctx.Popup.Open(ddPos + new Vector2(0, Px(19)), ddW, files, cur, i =>
                {
                    if (i < st.Files.Count) { _selected = -1; LootEditCommands.Simple("open", path: st.Files[i].Path); }
                });
            }
            SafeImGui.SetItemTooltip(s != null && s.Dirty ? "Save or reload this file before opening another." : "The loot profile to edit");
            if (Plate("##cl_editor", "Editor", new Vector2(o.X + w - Px(46), y + (rowH - Px(17)) * 0.5f)))
                LootEditorFace.ToggleFor(s?.Path.Length > 0 ? s.Path : raw.CurrentLootPath);
            SafeImGui.SetItemTooltip("The Loot Editor: write a rule's conditions");
            y += rowH + Px(2);
        }

        // The rules.
        float listW = w - Px(16), rh = Px(17), listH = rh * VisibleRows;
        float badgeW = Px(58), nameW = Px(130);
        var listMin = new Vector2(o.X, y);
        dl.AddRectFilled(listMin, listMin + new Vector2(listW, listH), RynthTheme.Argb(0x8C000000));
        int count = s?.Rules.Count ?? 0;
        _first = Wheel(listMin, listMin + new Vector2(w, listH), _first, count, VisibleRows);
        bool editable = s != null && !s.ReadOnly;
        for (int r = 0; r < VisibleRows && _first + r < count; r++)
        {
            int i = _first + r;
            LootEditRowDto rule = s!.Rules[i];
            var p = new Vector2(o.X, y + r * rh);
            bool sel = i == _selected;
            if (sel) RowSelected(dl, p, p + new Vector2(listW, rh));
            else if ((i & 1) == 1) dl.AddRectFilled(p, p + new Vector2(listW, rh), AltRow);
            ImGuiNET.ImGui.PushID(i);
            // The gem: the rule on or off.
            var gp = new Vector2(p.X + Px(3), p.Y);
            ImGuiNET.ImGui.SetCursorScreenPos(gp);
            if (ImGuiNET.ImGui.InvisibleButton("##en", new Vector2(Px(18), rh)) && editable)
                LootEditCommands.Simple("set_enabled", i, rule.Enabled ? "false" : "true", expect: rule.Name);
            SafeImGui.SetItemTooltip(rule.Enabled ? "On. Click to turn this rule off." : "Off. Click to turn this rule on.");
            GemPicture(dl, new Vector2(gp.X + Px(2), p.Y + (rh - Px(13)) * 0.5f), rule.Enabled);
            // The rest of the row selects it.
            var rp = new Vector2(p.X + Px(22), p.Y);
            ImGuiNET.ImGui.SetCursorScreenPos(rp);
            if (ImGuiNET.ImGui.InvisibleButton("##row", new Vector2(listW - Px(22), rh))) _selected = i;
            if (ImGuiNET.ImGui.IsItemHovered() && !sel) dl.AddRectFilled(rp, p + new Vector2(listW, rh), Hover);
            SafeImGui.SetItemTooltip(rule.Name + "\n" + rule.Summary + (rule.Conditions > 0 ? $"\n{rule.Conditions} conditions" : ""));
            ImGuiNET.ImGui.PopID();
            float x = rp.X;
            uint badge = RynthTheme.Argb(LootEditVocabulary.BadgeArgb(rule.Action));
            dl.AddRectFilled(new Vector2(x, p.Y + Px(2)), new Vector2(x + badgeW - Px(4), p.Y + rh - Px(2)), badge, Px(2));
            TextClip(dl, x + Px(4), p.Y, rh, x + badgeW - Px(6), LootEditVocabulary.Badge(rule.Action, rule.KeepCount), White, 9);
            x += badgeW;
            TextClip(dl, x, p.Y, rh, x + nameW - Px(4), rule.Name, rule.Enabled ? White : Faint, 10);
            x += nameW;
            TextClip(dl, x, p.Y, rh, p.X + listW - Px(4), rule.Summary, Dim, 10);
        }
        if (count == 0)
            TextMid(dl, o.X + Px(6), y, rh, s == null ? (view?.Unavailable == true ? "This RynthAi has no loot editor." : "Waiting for RynthAi…")
                : "No rules. Add one, then write it in the Loot Editor.", Dim, 10);
        _first = Scrollbar("##cl_sb", new Vector2(o.X + listW, y), listH, count, VisibleRows, _first, ref _grab);
        if (_selected >= count) _selected = count - 1;
        y += listH + Px(4);

        // Rule tools: up, down, duplicate, delete, add.
        {
            bool has = editable && _selected >= 0 && _selected < count;
            string name = has ? s!.Rules[_selected].Name : "";
            float x = o.X;
            if (Plate("##cl_up", "Up", new Vector2(x, y)) && has && _selected > 0)
            { LootEditCommands.Simple("move", _selected, to: _selected - 1, expect: name); _selected--; }
            SafeImGui.SetItemTooltip("Move the selected rule up (rules are checked top to bottom)");
            x += Px(50);
            if (Plate("##cl_down", "Down", new Vector2(x, y)) && has && _selected < count - 1)
            { LootEditCommands.Simple("move", _selected, to: _selected + 1, expect: name); _selected++; }
            SafeImGui.SetItemTooltip("Move the selected rule down");
            x += Px(50);
            if (Plate("##cl_dup", "Copy", new Vector2(x, y)) && has)
                LootEditCommands.Simple("duplicate", _selected, expect: name);
            SafeImGui.SetItemTooltip("Duplicate the selected rule");
            x += Px(50);
            if (Plate("##cl_del", "Delete", new Vector2(x, y)) && has)
            { LootEditCommands.Simple("delete", _selected, expect: name); _selected = -1; }
            SafeImGui.SetItemTooltip("Delete the selected rule");
            x += Px(50);
            if (Plate("##cl_add", "Add", new Vector2(x, y)) && editable)
                LootEditCommands.Simple("add", -1);
            SafeImGui.SetItemTooltip("Add a new rule at the end (write it in the Loot Editor)");
            y += Px(21);
        }

        // Save / reload, and RynthAi's last message.
        {
            float bw = Px(96);
            bool dirty = s != null && s.Dirty;
            if (Button("##cl_save", "Save", new Vector2(o.X, y), bw, ButtonColor.Blue, editable && dirty))
                LootEditCommands.Simple("save");
            SafeImGui.SetItemTooltip(dirty ? "Save the changes to " + s!.FileName : "Nothing to save");
            if (Button("##cl_reload", "Reload", new Vector2(o.X + bw + Px(6), y), bw, ButtonColor.Red, s != null))
            { _selected = -1; LootEditCommands.Simple("reload", force: true); }
            SafeImGui.SetItemTooltip(dirty ? "Throw away the unsaved changes and read the file again" : "Read the file again");
            if (s != null && s.Message.Length > 0)
                TextClip(dl, o.X + 2 * bw + Px(18), y, Px(32), o.X + w, s.Message, s.MessageOk ? Green : Down, 10);
            y += Px(32);
        }
        return y - o.Y;
    }

    private string[] Files(LootEditStateDto? s)
    {
        if (s == null) return Array.Empty<string>();
        if (!ReferenceEquals(s, _filesFor))
        {
            _filesFor = s;
            _files = new string[s.Files.Count];
            for (int i = 0; i < s.Files.Count; i++)
                _files[i] = s.Files[i].Display + (SamePath(s.Files[i].Path, s.InUsePath) ? "  (in use)" : "");
        }
        return _files;
    }

    private static bool SamePath(string a, string b) => a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
