// ============================================================================
//  RynthCore.Engine - ImGui/Panels/SettingsFace.cs
//  ImGui face of RynthAi's Advanced Settings (docs/IMGUI_PARITY_PLAN.md §2.4).
//
//  Draws SettingsSchema (UI/Data/SettingsData.cs), the same list the Avalonia
//  face builds from, with that face's look: 150 px tab sidebar, "Advanced
//  Settings > Tab" header, then the tab's rows (SettingsRows.cs draws and
//  edits them; right-click a row to pin it to the dashboard's My settings
//  drawer). Rows that show or hide other rows need nothing special:
//  visibility is re-evaluated every frame.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using ImGuiNET;
using RynthCore.Engine.UI.Data;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed class SettingsFace : IImGuiPanel
{
    public const string Title = "Settings";

    public static void Register() => ImGuiPanelHost.Register(Title,
        new PanelSpec(new Vector2(400, 500), new Vector2(320, 240), EdgeToEdge: true),
        () => new SettingsFace());

    // ── Palette (SettingsPanel's, ARGB) ─────────────────────────────────
    private static readonly uint ToggleOn = C(0xFF33FF33), ToggleOff = C(0xFF334455);
    private static uint Teal => RynthTheme.Accent;
    private static uint Amber => RynthTheme.Highlight;
    private static uint Mute => RynthTheme.TextMuted;
    private static uint TextDim => RynthTheme.Text;
    private static uint ShellBg => RynthTheme.Background;
    private static uint PanelBg => RynthTheme.Field;
    private static uint BtnFill => RynthTheme.Button;
    private static uint BtnBord => RynthTheme.Border;
    private static uint TabActive => RynthTheme.Selected;
    private static uint C(uint argb) => RynthTheme.Argb(argb);

    private static readonly string[] Headers = BuildHeaders();

    private int _tab;
    // The rows (SettingsRows.cs: the edited copy, typed values, the hotkeys editor).
    private readonly SettingsRows _rows = new();

    // A tab asked for from outside (/rc hotkeys), taken on the next draw.
    private static volatile string? _requestedTab;

    /// <summary>Shows tab <paramref name="name"/> (a SettingsSchema tab name) the next time Settings draws. Any thread.</summary>
    public static void RequestTab(string name) => _requestedTab = name;

    public void OnShown()
    {
        UiSources.Settings.Subscribe();
        UiSources.Settings.RequestRefresh();
    }

    public void OnHidden()
    {
        UiSources.Settings.Unsubscribe();
        _rows.LeaveHotkeys();
    }

    public void Draw()
    {
        // Take a newer fetch unless a value is being typed in.
        _rows.Sync();

        if (_requestedTab is { } wanted)
        {
            _requestedTab = null;
            int idx = Array.FindIndex(SettingsSchema.Tabs, t => t.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) _tab = idx;
        }

        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        Vector2 origin = ImGuiNET.ImGui.GetCursorScreenPos();
        Vector2 size = ImGuiNET.ImGui.GetContentRegionAvail();
        dl.AddRectFilled(origin, origin + size, ShellBg, 4);

        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui11));
        Sidebar(size.Y);
        ImGuiNET.ImGui.SetCursorScreenPos(origin + new Vector2(150, 0));
        Content(new Vector2(size.X - 150, size.Y));
        ImGuiNET.ImGui.PopFont();

        dl.AddRect(origin, origin + size, BtnBord, 4);
    }

    // ── Sidebar: one button per tab ────────────────────────────────────────

    private void Sidebar(float height)
    {
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ChildBg, PanelBg);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(2, 2));
        ImGuiNET.ImGui.BeginChild("##settings_tabs", new Vector2(150, height), ImGuiChildFlags.AlwaysUseWindowPadding);
        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.PopStyleColor();

        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        float w = ImGuiNET.ImGui.GetContentRegionAvail().X;
        SettingsTab[] tabs = SettingsSchema.Tabs;
        for (int i = 0; i < tabs.Length; i++)
        {
            string tabName = tabs[i].Name;
            ImGuiNET.ImGui.Dummy(new Vector2(0, 1));
            Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
            ImGuiNET.ImGui.PushID(i);
            if (ImGuiNET.ImGui.InvisibleButton("##tab", new Vector2(w, 26))) _tab = i;
            bool hot = ImGuiNET.ImGui.IsItemHovered();
            ImGuiNET.ImGui.PopID();
            bool sel = i == _tab;
            dl.AddRectFilled(p, p + new Vector2(w, 26), sel ? TabActive : hot ? Lighten(BtnFill) : BtnFill);
            dl.AddRect(p, p + new Vector2(w, 26), BtnBord);
            float ty = p.Y + (26 - ImGuiNET.ImGui.GetFontSize()) * 0.5f;
            dl.AddText(new Vector2(p.X + 8, ty), sel ? Teal : Mute, TabLabel(tabName));
        }
        ReportButton(w);
        ImGuiNET.ImGui.EndChild();
    }

    // At the sidebar's foot (below the tabs when they fill it): opens Report a problem.
    private static void ReportButton(float w)
    {
        if (!ImGuiPanelHost.HasFace(ReportFace.Title)) return;
        const float h = 26;
        float bottom = ImGuiNET.ImGui.GetWindowHeight() - ImGuiNET.ImGui.GetStyle().WindowPadding.Y - h;
        ImGuiNET.ImGui.Dummy(new Vector2(0, 6));
        if (ImGuiNET.ImGui.GetCursorPosY() < bottom) ImGuiNET.ImGui.SetCursorPosY(bottom);
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        if (ImGuiNET.ImGui.InvisibleButton("##report", new Vector2(w, h)))
            Report.ProblemReport.OpenPanel();
        bool hot = ImGuiNET.ImGui.IsItemHovered();
        ImGuiNET.ImGui.SetItemTooltip("Tell the staff what went wrong (also /rc report).");
        dl.AddRectFilled(p, p + new Vector2(w, h), hot ? Lighten(BtnFill) : BtnFill);
        dl.AddRect(p, p + new Vector2(w, h), BtnBord);
        dl.AddText(new Vector2(p.X + 8, p.Y + (h - ImGuiNET.ImGui.GetFontSize()) * 0.5f), hot ? Amber : Mute,
            PhosphorIcons.Warning + "  Report a problem");
    }

    // ── Content: header + the tab's rows ───────────────────────────────────

    private void Content(Vector2 size)
    {
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ChildBg, 0);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8, 6));
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4, 0));
        ImGuiNET.ImGui.BeginChild("##settings_content", size, ImGuiChildFlags.AlwaysUseWindowPadding);
        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.PopStyleColor();

        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        float w = ImGuiNET.ImGui.GetContentRegionAvail().X;

        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.UiBold11));
        if (_tab >= SettingsSchema.Tabs.Length) _tab = 0;
        SafeImGui.TextColored(RynthTheme.VecOf(RynthTheme.Accent), Headers[_tab]);
        ImGuiNET.ImGui.PopFont();
        ImGuiNET.ImGui.Dummy(new Vector2(0, 4));
        Vector2 lp = ImGuiNET.ImGui.GetCursorScreenPos();
        dl.AddLine(lp, lp + new Vector2(w, 0), BtnBord);
        ImGuiNET.ImGui.Dummy(new Vector2(0, 6));

        SettingRow[] rows = SettingsSchema.Tabs[_tab].Rows;
        if (SettingsSchema.Tabs[_tab].Name != HotkeysSettingsUi.TabName) _rows.LeaveHotkeys();
        for (int i = 0; i < rows.Length; i++)
        {
            SettingRow row = rows[i];
            if (!row.IsVisible(_rows.Data)) continue;
            ImGuiNET.ImGui.PushID(i);
            _rows.DrawRow(row, w);
            ImGuiNET.ImGui.PopID();
        }

        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.EndChild();
    }

    // Tab icons by tab name (SettingsSchema); a tab not listed shows its name alone.
    private static readonly System.Collections.Generic.Dictionary<string, string> TabIcons = new()
    {
        ["Display"] = PhosphorIcons.Monitor,
        ["UI"] = PhosphorIcons.AppWindow,
        ["Hotkeys"] = PhosphorIcons.Keyboard,
        ["Misc"] = PhosphorIcons.DotsThree,
        ["Recharge"] = PhosphorIcons.BatteryCharging,
        ["Melee Combat"] = PhosphorIcons.Sword,
        ["Spell Combat"] = PhosphorIcons.MagicWand,
        ["Ranges"] = PhosphorIcons.Ruler,
        ["Navigation"] = PhosphorIcons.SneakerMove,
        ["Buffing"] = PhosphorIcons.Sparkle,
        ["Crafting"] = PhosphorIcons.Hammer,
        ["Looting"] = PhosphorIcons.Bag,
        ["Vendoring"] = PhosphorIcons.Storefront,
        ["Diagnostics"] = PhosphorIcons.Wrench,
    };
    private static readonly System.Collections.Generic.Dictionary<string, string> TabLabels = new();

    /// <summary>"icon  Name", built once per tab.</summary>
    private static string TabLabel(string name)
    {
        if (TabLabels.TryGetValue(name, out string? label)) return label;
        label = TabIcons.TryGetValue(name, out string? icon) ? icon + "  " + name : name;
        TabLabels[name] = label;
        return label;
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

    private static string[] BuildHeaders()
    {
        var headers = new string[SettingsSchema.Tabs.Length];
        for (int i = 0; i < headers.Length; i++)
            headers[i] = "Advanced Settings > " + SettingsSchema.Tabs[i].Name;
        return headers;
    }
}
