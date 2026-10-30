// ============================================================================
//  RynthCore.Engine - ImGui/Panels/QuickSettingsDrawer.cs
//  The dashboard's My settings drawer (Tom 2026-10-14): the settings the player
//  pinned, drawn with the Settings panel's own rows (SettingsRows.cs), so a
//  setting changed here is the same setting, saved the same way.
//
//  Pin from any setting's right-click (the Settings panel, the Patrol drawer,
//  this drawer) or from the + button here, a searchable list of every setting
//  that can be pinned (UI/QuickSettings.cs keeps the pins and their order).
//  Here a row's right-click also moves it up or down. Starts on the
//  dashboard's right edge; its tab's right-click moves it to the left.
//  AC thread only (inside the dashboard's Draw).
// ============================================================================

using System;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.UI;
using RynthCore.Engine.UI.Data;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed class QuickSettingsDrawer : RynthAiDrawer
{
    private readonly SettingsRows _rows = new();
    private bool _sub;
    private string _filter = string.Empty;
    private const string AddPopupId = "##quick_add";

    // Rows are drawn at the Settings panel's fixed sizes: about this tall each (pixels).
    private const float RowH = 28, EmptyH = 70, MinBodyH = 60;

    public override string Key => "quick";
    public override string Icon => PhosphorIcons.SlidersHorizontal;
    public override string Name => "My settings drawer";
    public override bool StartsRight => true;
    public override bool SavedOpen => RynthAiDashboardState.QuickOpen;
    public override void SaveOpen(bool open) => RynthAiDashboardState.SetQuickOpen(open);

    public override string TabTooltip(bool right) =>
        "My settings: the settings you pinned, to change them without opening Settings.\n" +
        "Pin one by right-clicking it in Settings, or with + in the drawer. Click to slide it out.";

    public override float Width(float k) => 300 * k;

    public override float Height(float k)
    {
        int pins = 0;
        foreach (string key in QuickSettings.Pins)
            if (QuickSettings.RowOf(key) != null) pins++;
        float body = pins == 0 ? EmptyH : MathF.Max(MinBodyH, pins * RowH + 6);
        float frame = (Pad + TitleH + 4 + Pad) * k;
        float screen = ImGuiNET.ImGui.GetIO().DisplaySize.Y;
        float max = screen > 1 ? MathF.Max(120 * k, screen - 8 * k) : 900 * k;
        return MathF.Min(frame + body, max);
    }

    public override void Update(bool showing)
    {
        if (showing == _sub) return;
        _sub = showing;
        if (showing) { UiSources.Settings.Subscribe(); UiSources.Settings.RequestRefresh(); }
        else UiSources.Settings.Unsubscribe();
    }

    public override void DrawPanel(Vector2 origin, float panelW, float panelH, float k)
    {
        float y = PanelFrame(origin, panelW, panelH, k, PhosphorIcons.SlidersHorizontal, "MY SETTINGS", null, "Hide My settings");
        float x = origin.X + Pad * k, w = panelW - 2 * Pad * k;

        // + beside the close button: pick settings to pin.
        Vector2 btn = new(18 * k, 16 * k);
        Vector2 addPos = new(x + w - 2 * btn.X - 3 * k, origin.Y + Pad * k + (TitleH * k - btn.Y) * 0.5f);
        if (FaceKit.IconButton("##quick_plus", PhosphorIcons.Plus, addPos, btn, DTeal, DBtnFill, border: DBtnBord, font: UiFont.Dash10))
        {
            _filter = string.Empty;
            ImGuiNET.ImGui.SetNextWindowPos(new Vector2(addPos.X + btn.X, addPos.Y + btn.Y + 2 * k), ImGuiCond.Always, new Vector2(1, 0));
            ImGuiNET.ImGui.OpenPopup(AddPopupId);
        }
        ImGuiNET.ImGui.SetItemTooltip("Pin or unpin settings");
        DrawBody(x, y, w, MathF.Max(1, origin.Y + panelH - Pad * k - y), k, openPicker: false);
    }

    /// <summary>
    /// The pinned rows at (<paramref name="x"/>, <paramref name="y"/>) in a <paramref name="w"/> x
    /// <paramref name="h"/> child that scrolls (RynthAi's own dashboard window draws the frame and the
    /// + button: <paramref name="openPicker"/> opens the pin list at the mouse, SettingsDrawerBodies).
    /// Returns the rows' height (pixels).
    /// </summary>
    public float DrawBody(float x, float y, float w, float h, float k, bool openPicker)
    {
        if (openPicker)
        {
            _filter = string.Empty;
            ImGuiNET.ImGui.SetNextWindowPos(ImGuiNET.ImGui.GetMousePos() + new Vector2(0, 8 * k), ImGuiCond.Always, new Vector2(1, 0));
            ImGuiNET.ImGui.OpenPopup(AddPopupId);
        }
        AddPopup(k);

        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x, y));
        Vector2 size = new(w, MathF.Max(1, h));
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        bool open = ImGuiNET.ImGui.BeginChild("##quick_body", size, ImGuiChildFlags.None, ImGuiWindowFlags.NoBackground);
        ImGuiNET.ImGui.PopStyleVar();
        float contentH = EmptyH * k;
        try
        {
            if (!open) return contentH;
            _rows.Sync();
            ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui11));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4, 0));
            try
            {
                float rw = ImGuiNET.ImGui.GetContentRegionAvail().X - 2 * k;
                string[] pins = QuickSettings.Pins;
                int drawn = 0;
                for (int i = 0; i < pins.Length; i++)
                {
                    SettingRow? row = QuickSettings.RowOf(pins[i]);
                    if (row == null) continue;
                    drawn++;
                    if (!row.IsVisible(_rows.Data)) continue;
                    ImGuiNET.ImGui.PushID(pins[i]);
                    _rows.DrawRow(row, rw, extraMenu: MoveItems);
                    ImGuiNET.ImGui.PopID();
                }
                if (drawn == 0)
                {
                    ImGuiNET.ImGui.Dummy(new Vector2(0, 4));
                    ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, DMute);
                    ImGuiNET.ImGui.PushTextWrapPos(ImGuiNET.ImGui.GetCursorPosX() + rw);
                    ImGuiNET.ImGui.TextUnformatted("Pin the settings you change most. Right-click a setting in Settings (or in the Patrol drawer) " +
                                                   "and pick Pin to My settings, or click + above.");
                    ImGuiNET.ImGui.PopTextWrapPos();
                    ImGuiNET.ImGui.PopStyleColor();
                }
                // What the rows took (unscrolled), for a host that fits its height to them.
                contentH = ImGuiNET.ImGui.GetCursorPosY() + 2 * k;   // content space: the scroll is in it
            }
            finally
            {
                ImGuiNET.ImGui.PopStyleVar();
                ImGuiNET.ImGui.PopFont();
            }
        }
        finally { ImGuiNET.ImGui.EndChild(); }
        return contentH;
    }

    /// <summary>A pinned row's right-click, after Remove: Move up / Move down.</summary>
    private static void MoveItems(string key)
    {
        string[] pins = QuickSettings.Pins;
        int i = Array.IndexOf(pins, key);
        if (ImGuiNET.ImGui.MenuItem(PhosphorIcons.CaretUp + "  Move up", null, false, i > 0)) QuickSettings.Move(key, -1);
        if (ImGuiNET.ImGui.MenuItem(PhosphorIcons.CaretDown + "  Move down", null, false, i >= 0 && i < pins.Length - 1)) QuickSettings.Move(key, +1);
    }

    /// <summary>Every setting that can be pinned, by tab, with a search box; a click pins or unpins it.</summary>
    private void AddPopup(float k)
    {
        ImGuiNET.ImGui.SetNextWindowSize(new Vector2(320 * k, 420 * k));
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, RynthTheme.Background);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, DTeal);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8 * k, 6 * k));
        bool open = ImGuiNET.ImGui.BeginPopup(AddPopupId);
        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.PopStyleColor(2);
        if (!open) return;
        try
        {
            ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui11));
            ImGuiNET.ImGui.SetNextItemWidth(-1);
            if (ImGuiNET.ImGui.IsWindowAppearing()) ImGuiNET.ImGui.SetKeyboardFocusHere();
            ImGuiNET.ImGui.InputTextWithHint("##quick_search", "Search settings", ref _filter, 64);
            ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
            ImGuiNET.ImGui.BeginChild("##quick_list", new Vector2(0, 0));
            string filter = _filter.Trim();
            foreach (SettingsTab tab in SettingsSchema.Tabs)
            {
                bool header = false;
                string section = "";
                foreach (SettingRow row in tab.Rows)
                {
                    if (row.Kind == SettingKind.Section) { section = row.Label.Trim(); continue; }
                    string? key = QuickSettings.KeyOf(row);
                    if (key == null) continue;
                    string label = row.Label.Trim();
                    if (filter.Length > 0 && label.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0
                        && section.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0
                        && tab.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    if (!header)
                    {
                        header = true;
                        ImGuiNET.ImGui.Dummy(new Vector2(0, 3));
                        SafeImGui.TextColored(RynthTheme.VecOf(RynthTheme.Highlight), tab.Name);
                    }
                    bool pinned = QuickSettings.IsPinned(key);
                    ImGuiNET.ImGui.PushID(key);
                    // Stays open, so several can be pinned in one go; the section shows dimmed at the right.
                    if (ImGuiNET.ImGui.Selectable((pinned ? PhosphorIcons.Check : "    ") + "  " + label, pinned, ImGuiSelectableFlags.NoAutoClosePopups))
                        QuickSettings.SetPinned(key, !pinned);
                    if (row.Tooltip != null) SafeImGui.SetItemTooltip(row.Tooltip);
                    if (section.Length > 0)
                    {
                        Vector2 max = ImGuiNET.ImGui.GetItemRectMax(), min = ImGuiNET.ImGui.GetItemRectMin();
                        float sw = ImGuiNET.ImGui.CalcTextSize(section).X;
                        float sx = MathF.Max(min.X + ImGuiNET.ImGui.CalcTextSize("    " + label).X + 24, max.X - sw - 4);
                        if (sx + sw <= max.X)
                            ImGuiNET.ImGui.GetWindowDrawList().AddText(new Vector2(sx, min.Y), RynthTheme.Argb(0xFF6E8197), section);
                    }
                    ImGuiNET.ImGui.PopID();
                }
            }
            ImGuiNET.ImGui.EndChild();
            ImGuiNET.ImGui.PopFont();
        }
        finally { ImGuiNET.ImGui.EndPopup(); }
    }
}
