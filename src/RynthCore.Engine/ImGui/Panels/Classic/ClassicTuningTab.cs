// ============================================================================
//  RynthCore.Engine - ImGui/Panels/Classic/ClassicTuningTab.cs
//  The Classic look's Tuning tab: every setting of the Settings panel, one of
//  its tabs at a time (the green drop-down) or all of them filtered by a
//  search. Rows are the Settings schema's (UI/Data/SettingsData.cs) on
//  SettingsRows' copy of the settings, so a change here is the same change
//  the Settings panel makes and saves the same way. Toggles, numbers, choices,
//  section headers and notes are drawn retail (gems, number boxes, drop-downs,
//  header bars); the few special rows (buttons, hotkeys, colours, the server
//  picker, status lines) use the Settings panel's own drawing.
//  AC's render thread only.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using ImGuiNET;
using RynthCore.Engine.UI;
using RynthCore.Engine.UI.Data;
using static RynthCore.Engine.ImGuiBackend.Panels.Classic.ClassicKit;

namespace RynthCore.Engine.ImGuiBackend.Panels.Classic;

internal sealed class ClassicTuningTab : IClassicTab
{
    public string Name => "Tuning";

    private const float ListH = 440;

    private readonly SettingsRows _rows = new();
    private readonly Dictionary<SettingRow, ClassicSettings.NumberRow> _numbers = new();
    private readonly byte[] _search = new byte[64];
    private string _query = string.Empty;
    private int _tab;
    private string[]? _tabNames;
    private bool _busy;

    public void Shown()
    {
        UiSources.Settings.Subscribe();
        UiSources.Settings.RequestRefresh();
    }

    public void Hidden()
    {
        UiSources.Settings.Unsubscribe();
        _rows.LeaveHotkeys();
    }

    private string[] TabNames
    {
        get
        {
            if (_tabNames != null) return _tabNames;
            var t = SettingsSchema.Tabs;
            _tabNames = new string[t.Length];
            for (int i = 0; i < t.Length; i++) _tabNames[i] = t[i].Name;
            return _tabNames;
        }
    }

    public float Draw(ClassicContext ctx, Vector2 o, float w)
    {
        if (!_busy) _rows.Sync();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        float y = o.Y;
        string[] names = TabNames;
        _tab = Math.Clamp(_tab, 0, Math.Max(0, names.Length - 1));

        // ── Tab picker | search ──
        float rowH = Px(22), ddW = Px(150);
        var ddPos = new Vector2(o.X, y + (rowH - Px(19)) * 0.5f);
        bool searching = _query.Length > 0;
        if (Dropdown("##ct_tab", searching ? "All (search)" : names.Length > 0 ? names[_tab] : "", ddPos, ddW, ctx.Popup.IsOpen))
            ctx.Popup.Open(ddPos + new Vector2(0, Px(19)), ddW, names, _tab, i => { _tab = i; ClearSearch(); });
        SafeImGui.SetItemTooltip("Which group of settings (the Settings panel's tabs)");
        SearchBox(new Vector2(o.X + ddW + Px(8), y + (rowH - Px(17)) * 0.5f), w - ddW - Px(8));
        y += rowH + Px(6);

        // ── The rows: a scrolling area with a gold scrollbar ──
        float listH = Px(ListH);
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(o.X, y));
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ChildBg, 0u);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, RynthTheme.Argb(0x80000000));
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, RynthTheme.Argb(0xFF7A6230));
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, RynthTheme.Argb(0xFFB08A3C));
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, Gold);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, Px(12));
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        bool visible = ImGuiNET.ImGui.BeginChild("##ct_rows", new Vector2(w, listH), ImGuiChildFlags.None, ImGuiWindowFlags.None);
        ImGuiNET.ImGui.PopStyleVar(2);
        ImGuiNET.ImGui.PopStyleColor(5);
        bool busy = false;
        if (visible)
        {
            float cw = ImGuiNET.ImGui.GetContentRegionAvail().X - Px(4);
            RynthAiSettings data = _rows.Data;
            if (UiSources.Settings.Current == null)
            {
                Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
                TextMid(ImGuiNET.ImGui.GetWindowDrawList(), p.X + Px(3), p.Y, Px(20), "Waiting for RynthAi…", Dim);
            }
            else if (searching)
            {
                int found = 0;
                var tabs = SettingsSchema.Tabs;
                for (int t = 0; t < tabs.Length; t++)
                {
                    bool shownHeader = false;
                    foreach (SettingRow row in tabs[t].Rows)
                    {
                        if (!Matches(row) || !row.IsVisible(data)) continue;
                        if (!shownHeader)
                        {
                            shownHeader = true;
                            SectionRow(tabs[t].Name, cw);
                        }
                        busy |= DrawRow(ctx, row, cw, t);
                        found++;
                    }
                }
                if (found == 0)
                {
                    Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
                    TextMid(ImGuiNET.ImGui.GetWindowDrawList(), p.X + Px(3), p.Y, Px(20), "No setting matches \"" + _query + "\".", Dim);
                    ImGuiNET.ImGui.Dummy(new Vector2(cw, Px(20)));
                }
            }
            else if (names.Length > 0)
            {
                SettingRow[] rows = SettingsSchema.Tabs[_tab].Rows;
                for (int i = 0; i < rows.Length; i++)
                    if (rows[i].IsVisible(data))
                        busy |= DrawRow(ctx, rows[i], cw, i);
            }
            ImGuiNET.ImGui.Dummy(new Vector2(cw, Px(6)));
        }
        ImGuiNET.ImGui.EndChild();
        _busy = busy;
        y += listH;
        return y - o.Y;
    }

    // ── Search ──────────────────────────────────────────────────────────

    private void SearchBox(Vector2 pos, float w)
    {
        float h = Px(17);
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        FieldFrame(dl, pos, pos + new Vector2(w, h));
        ImFontPtr f = Font();
        ImGuiNET.ImGui.PushFont(f);
        ImGuiNET.ImGui.SetWindowFontScale(Px(10) / f.FontSize);
        float fs = ImGuiNET.ImGui.GetFontSize();
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg, 0u);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, 0u);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBgActive, 0u);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, White);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(Px(5), Math.Max(0, (h - fs) * 0.5f)));
        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        ImGuiNET.ImGui.SetNextItemWidth(w);
        if (ImGuiNET.ImGui.InputText("##ct_search", _search, (uint)_search.Length))
            _query = FaceKit.Utf8(_search).Trim();
        bool active = ImGuiNET.ImGui.IsItemActive();
        if (active) ImGuiTextFocus.NoteActive("##ct_search");
        ImGuiNET.ImGui.PopStyleVar(2);
        ImGuiNET.ImGui.PopStyleColor(4);
        ImGuiNET.ImGui.SetWindowFontScale(1f);
        ImGuiNET.ImGui.PopFont();
        if (_search[0] == 0 && !active)
            TextMid(dl, pos.X + Px(6), pos.Y, h, "Search every setting", Faint, 10);
        SafeImGui.SetItemTooltip("Type part of a setting's name or description: every tab is searched.");
    }

    private void ClearSearch()
    {
        _search[0] = 0;
        _query = string.Empty;
    }

    private bool Matches(SettingRow row)
    {
        if (row.Kind is SettingKind.Section or SettingKind.Spacer) return false;
        return row.Label.Contains(_query, StringComparison.OrdinalIgnoreCase)
            || (row.Tooltip != null && row.Tooltip.Contains(_query, StringComparison.OrdinalIgnoreCase));
    }

    // ── Rows ────────────────────────────────────────────────────────────

    /// <summary>One schema row at the child's cursor; true while it's typed in.</summary>
    private bool DrawRow(ClassicContext ctx, SettingRow row, float w, int index)
    {
        ImGuiNET.ImGui.PushID(row.Label + "#" + index);
        try
        {
            switch (row.Kind)
            {
                case SettingKind.Section: SectionRow(row.Label, w); return false;
                case SettingKind.Spacer: ImGuiNET.ImGui.Dummy(new Vector2(w, Px(6))); return false;
                case SettingKind.Note: NoteRow(row.Label, w); return false;
                case SettingKind.Bool: BoolRow(row, w); return false;
                case SettingKind.Int:
                case SettingKind.Float:
                case SettingKind.Double: return NumberRow(row, w);
                case SettingKind.Combo when row.CurrentItems.Length > 0: ComboRow(ctx, row, w); return false;
                default:
                    // The special rows (buttons, hotkeys, colours, server picker, status): the Settings panel's drawing.
                    _rows.DrawRow(row, w);
                    return false;
            }
        }
        finally
        {
            ImGuiNET.ImGui.PopID();
        }
    }

    private static void SectionRow(string title, float w)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        ImGuiNET.ImGui.Dummy(new Vector2(w, Px(4)));
        Header(ImGuiNET.ImGui.GetWindowDrawList(), new Vector2(p.X, p.Y + Px(4)), w, title);
        ImGuiNET.ImGui.Dummy(new Vector2(w, Px(23)));
    }

    private static void NoteRow(string text, float w)
    {
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Dim);
        ImGuiNET.ImGui.PushTextWrapPos(ImGuiNET.ImGui.GetCursorPosX() + w);
        ImGuiNET.ImGui.TextUnformatted(text);
        ImGuiNET.ImGui.PopTextWrapPos();
        ImGuiNET.ImGui.PopStyleColor();
    }

    private void BoolRow(SettingRow row, float w)
    {
        RynthAiSettings data = _rows.Data;
        bool on = row.Get != null && row.Get(data) != 0;
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        if (Gem("##b", row.Label, p, w, on, 20) && row.Set != null)
        {
            row.Set(data, on ? 0 : 1);
            SettingsCommands.Save(data.Clone());
        }
        SafeImGui.SetItemTooltip(row.Tooltip ?? row.Label);
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + Px(20)));
        ImGuiNET.ImGui.Dummy(new Vector2(w, 0));
    }

    private bool NumberRow(SettingRow row, float w)
    {
        RynthAiSettings data = _rows.Data;
        if (!_numbers.TryGetValue(row, out ClassicSettings.NumberRow? n))
        {
            n = new ClassicSettings.NumberRow(row.Label, row.Label);
            _numbers[row] = n;
        }
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        float rh = Px(22), boxW = Px(64);
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        double value = row.Get != null ? Math.Clamp(row.Get(data), row.Min, row.Max) : 0;
        TextClip(dl, p.X + Px(3), p.Y, rh, p.X + w - boxW - Px(8), row.Label, Text);
        if (!n.Typing && n.Shown != value) WriteNumber(n, row, value);
        bool done = NumberBox("##n", n.Buffer, new Vector2(p.X + w - boxW, p.Y + (rh - Px(17)) * 0.5f), boxW, out bool active);
        n.Typing = active;
        SafeImGui.SetItemTooltip((row.Tooltip ?? row.Label) + $"\n({Fmt(row, row.Min)} to {Fmt(row, row.Max)})");
        if (done)
        {
            if (ClassicSettings.TryParse(n.Buffer, out double typed) && row.Set != null)
            {
                double v = Math.Clamp(row.Kind == SettingKind.Int ? Math.Round(typed) : typed, row.Min, row.Max);
                row.Set(data, v);
                SettingsCommands.Save(data.Clone());
                WriteNumber(n, row, v);
            }
            else WriteNumber(n, row, value);
        }
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + rh));
        ImGuiNET.ImGui.Dummy(new Vector2(w, 0));
        return n.Typing;
    }

    private static string Fmt(SettingRow row, double v) =>
        row.Kind == SettingKind.Int ? ((long)Math.Round(v)).ToString() : v.ToString("G4", System.Globalization.CultureInfo.InvariantCulture);

    private static void WriteNumber(ClassicSettings.NumberRow n, SettingRow row, double value)
    {
        string text = Fmt(row, value);
        int len = Encoding.UTF8.GetBytes(text, 0, text.Length, n.Buffer, 0);
        n.Buffer[Math.Min(len, n.Buffer.Length - 1)] = 0;
        n.Shown = value;
    }

    private void ComboRow(ClassicContext ctx, SettingRow row, float w)
    {
        RynthAiSettings data = _rows.Data;
        string[] items = row.CurrentItems;
        int cur = row.Get != null ? Math.Clamp((int)Math.Round(row.Get(data)), 0, items.Length - 1) : 0;
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        float rh = Px(22), ddW = Math.Min(Px(170), w * 0.55f);
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        TextClip(dl, p.X + Px(3), p.Y, rh, p.X + w - ddW - Px(8), row.Label, Text);
        var ddPos = new Vector2(p.X + w - ddW, p.Y + (rh - Px(19)) * 0.5f);
        if (Dropdown("##c", items[cur], ddPos, ddW))
        {
            Vector2 screen = ddPos + new Vector2(0, Px(19));
            ctx.Popup.Open(screen, ddW, items, cur, i =>
            {
                if (row.Set == null) return;
                row.Set(_rows.Data, i);
                SettingsCommands.Save(_rows.Data.Clone());
            });
        }
        SafeImGui.SetItemTooltip(row.Tooltip ?? row.Label);
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + rh));
        ImGuiNET.ImGui.Dummy(new Vector2(w, 0));
    }
}
