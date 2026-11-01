// ============================================================================
//  RynthCore.Engine - ImGui/Panels/SettingsRows.cs
//  Draws rows of the Settings schema (UI/Data/SettingsData.cs) with the
//  Settings panel's look: square toggle dots, - [value] + steppers (typed
//  values commit on Enter or when the field loses focus, then clamp; bad
//  input reverts), combo pickers, amber section headers, tooltips. Moved out
//  of SettingsFace (2026-10-14) so the dashboard's drawers draw the same rows:
//  My settings (QuickSettingsDrawer.cs) and the Patrol drawer's settings.
//
//  Each user keeps a private copy of the hub's settings and saves the whole
//  copy (SettingsCommands.Save) on each change; a newer fetch replaces the
//  copy only while none of its fields is being typed in (Sync, once a frame).
//  The owner subscribes UiSources.Settings while it shows.
//
//  Right-click a row: Pin to / Remove from My settings (UI/QuickSettings.cs),
//  plus whatever the owner adds (the drawer's Move up / down).
//  AC thread only.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using ImGuiNET;
using RynthCore.Engine.UI;
using RynthCore.Engine.UI.Data;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed class SettingsRows
{
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

    private RynthAiSettings _data = new();
    private long _seenVersion = -1;
    private bool _editing;

    // Typed-value buffers, one per numeric row (UTF-8, NUL-terminated).
    private sealed class NumberEdit
    {
        public readonly byte[] Buffer = new byte[24];
        public double Shown = double.NaN;
        public bool Active;     // the box had keyboard focus last frame
    }
    private readonly Dictionary<SettingRow, NumberEdit> _edits = new();
    // Text rows being typed in (the saved value shows again once the box loses focus).
    private readonly Dictionary<SettingRow, string> _textEdits = new();
    // The Hotkeys tab (engine-side; not part of RynthAi's settings).
    private readonly HotkeysSettingsUi _hotkeys = new();

    /// <summary>The copy being edited (the last fetch, plus this view's own changes).</summary>
    public RynthAiSettings Data => _data;

    /// <summary>Once a frame, before the rows: takes a newer fetch unless a value is being typed in.</summary>
    public void Sync()
    {
        var snap = UiSources.Settings.Current;
        if (snap != null && snap.Version != _seenVersion && !_editing)
        {
            _seenVersion = snap.Version;
            _data = snap.Value.Clone();
        }
        _editing = false;
    }

    /// <summary>The hotkeys editor's tab was left (or the panel closed).</summary>
    public void LeaveHotkeys() => _hotkeys.Leave();

    /// <summary>
    /// One row at the cursor, <paramref name="w"/> wide. The caller pushes an ID per row.
    /// <paramref name="pinMenu"/>: right-click offers Pin to / Remove from My settings;
    /// <paramref name="extraMenu"/> adds items to that menu (given the row's pin key).
    /// </summary>
    public void DrawRow(SettingRow row, float w, bool pinMenu = true, Action<string>? extraMenu = null)
    {
        Vector2 top = ImGuiNET.ImGui.GetCursorScreenPos();
        DrawRowBody(row, w);
        if (!pinMenu) return;
        string? key = QuickSettings.KeyOf(row);
        if (key == null) return;
        Vector2 bottom = ImGuiNET.ImGui.GetCursorScreenPos();
        if (ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Right)
            && ImGuiNET.ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem)
            && ImGuiNET.ImGui.IsMouseHoveringRect(top, new Vector2(top.X + w, MathF.Max(bottom.Y, top.Y + 4))))
            ImGuiNET.ImGui.OpenPopup("##pin_menu");

        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, ShellBg);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, BtnBord);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8, 6));
        bool open = ImGuiNET.ImGui.BeginPopup("##pin_menu");
        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.PopStyleColor(2);
        if (!open) return;
        try
        {
            bool pinned = QuickSettings.IsPinned(key);
            if (ImGuiNET.ImGui.MenuItem(pinned ? PhosphorIcons.X + "  Remove from My settings" : PhosphorIcons.PushPin + "  Pin to My settings"))
                QuickSettings.SetPinned(key, !pinned);
            SafeImGui.SetItemTooltip(pinned
                ? "Take it out of the My settings drawer on the dashboard (the setting itself doesn't change)."
                : "Add it to the My settings drawer on the dashboard, for quick changes.");
            extraMenu?.Invoke(key);
        }
        finally { ImGuiNET.ImGui.EndPopup(); }
    }

    private void DrawRowBody(SettingRow row, float w)
    {
        switch (row.Kind)
        {
            case SettingKind.Bool: BoolRow(row, w); break;
            case SettingKind.Int:
            case SettingKind.Float:
            case SettingKind.Double: NumberRow(row, w); break;
            case SettingKind.Combo: ComboRow(row, w); break;
            case SettingKind.Section: Section(row.Label, w); break;
            case SettingKind.Spacer: ImGuiNET.ImGui.Dummy(new Vector2(0, 6)); break;
            case SettingKind.Note: Note(row.Label, w); break;
            case SettingKind.CraftingStatus: CraftingStatus(row); break;
            case SettingKind.Button: ButtonRow(row, w); break;
            case SettingKind.TraceCategories: TraceCategoriesRow(row, w); break;
            case SettingKind.Text: TextRow(row, w); break;
            case SettingKind.Status: StatusRow(row, w); break;
            case SettingKind.Hotkeys: _editing |= _hotkeys.Draw(w); break;
            case SettingKind.ServerPick: ServerPickSettingsUi.Draw(w, row.Tooltip); break;
            case SettingKind.Color: ColorRow(row, w); break;
        }
    }

    // [All on] [All off], then a 3-column grid of square toggles, one per
    // RynthAi trace category. Edits the copy's DiagCategories and saves like any other row.
    private void TraceCategoriesRow(SettingRow row, float w)
    {
        var cats = TraceCategoryLevels.Parse(_data.DiagCategories);
        if (cats.Count == 0)
        {
            Note("Waiting for RynthAi...", w);
            return;
        }

        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
        if (ImGuiNET.ImGui.Button("All on")) SaveCategories(TraceCategoryLevels.Set(_data.DiagCategories, "", true));
        ImGuiNET.ImGui.SameLine(0, 6);
        if (ImGuiNET.ImGui.Button("All off")) SaveCategories(TraceCategoryLevels.Set(_data.DiagCategories, "", false));
        Tooltip(row);
        ImGuiNET.ImGui.Dummy(new Vector2(0, 4));

        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        float colW = Math.Max(80f, w / 3f);
        int perRow = Math.Max(1, (int)(w / colW));
        Vector2 start = ImGuiNET.ImGui.GetCursorScreenPos();
        for (int i = 0; i < cats.Count; i++)
        {
            var (name, level) = cats[i];
            bool on = level != TraceCategoryLevels.Off;
            string label = TraceCategoryLevels.Label(name, level);
            var p = start + new Vector2(i % perRow * colW, i / perRow * 18f);
            ImGuiNET.ImGui.SetCursorScreenPos(p);
            ImGuiNET.ImGui.PushID(name);
            if (ImGuiNET.ImGui.InvisibleButton("##cat", new Vector2(colW - 4, 16)))
                SaveCategories(TraceCategoryLevels.Set(_data.DiagCategories, name, !on));
            if (ImGuiNET.ImGui.IsItemHovered()) ImGuiNET.ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ImGuiNET.ImGui.PopID();
            dl.AddRectFilled(p + new Vector2(0, 2), p + new Vector2(12, 14), on ? ToggleOn : ToggleOff, 2);
            dl.PushClipRect(p, p + new Vector2(colW - 4, 16), true);
            dl.AddText(p + new Vector2(18, (16 - ImGuiNET.ImGui.GetFontSize()) * 0.5f), TextDim, label);
            dl.PopClipRect();
        }
        int rowsUsed = (cats.Count + perRow - 1) / perRow;
        ImGuiNET.ImGui.SetCursorScreenPos(start + new Vector2(0, rowsUsed * 18f));
        ImGuiNET.ImGui.Dummy(new Vector2(0, 4));
    }

    private void SaveCategories(string levels)
    {
        _data.DiagCategories = levels;
        SettingsCommands.Save(_data.Clone());
    }

    // Label, then a full-width box; the value commits on Enter or focus loss (like the number boxes).
    private void TextRow(SettingRow row, float w)
    {
        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, TextDim);
        ImGuiNET.ImGui.TextUnformatted(row.Label);
        ImGuiNET.ImGui.PopStyleColor();
        Tooltip(row);

        string saved = row.GetText?.Invoke(_data) ?? string.Empty;
        string text = _textEdits.TryGetValue(row, out string? typing) ? typing : saved;
        ImGuiNET.ImGui.SetNextItemWidth(Math.Max(80, w - 4));
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg, PanelBg);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Amber);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, BtnBord);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0f);
        bool enter = ImGuiNET.ImGui.InputTextWithHint("##text", row.Hint ?? string.Empty, ref text, 512u,
            ImGuiInputTextFlags.EnterReturnsTrue);
        bool active = ImGuiNET.ImGui.IsItemActive();
        bool committed = enter || ImGuiNET.ImGui.IsItemDeactivatedAfterEdit();
        ImGuiNET.ImGui.PopStyleVar(2);
        ImGuiNET.ImGui.PopStyleColor(3);
        Tooltip(row);
        _editing |= active;

        if (committed)
        {
            _textEdits.Remove(row);
            if (!string.Equals(text, saved, StringComparison.Ordinal))
            {
                row.SetText?.Invoke(_data, text);
                SettingsCommands.Save(_data.Clone());
            }
        }
        else if (active) _textEdits[row] = text;
        else _textEdits.Remove(row);
        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
    }

    // RynthAi's one-line status (teal), wrapped to the column.
    private void StatusRow(SettingRow row, float w)
    {
        string text = row.GetText?.Invoke(_data) ?? string.Empty;
        if (text.Length == 0) return;
        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Teal);
        ImGuiNET.ImGui.PushTextWrapPos(ImGuiNET.ImGui.GetCursorPosX() + w);
        ImGuiNET.ImGui.TextUnformatted(text);
        ImGuiNET.ImGui.PopTextWrapPos();
        ImGuiNET.ImGui.PopStyleColor();
    }

    // Label | [caption]; Click posts its own work (never runs plugin code on AC's thread).
    private void ButtonRow(SettingRow row, float w)
    {
        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, TextDim);
        ImGuiNET.ImGui.AlignTextToFramePadding();
        ImGuiNET.ImGui.TextUnformatted(row.Label);
        ImGuiNET.ImGui.PopStyleColor();
        ImGuiNET.ImGui.SameLine(Math.Max(ImGuiNET.ImGui.GetCursorPosX() + 8, w * 0.45f));
        bool clicked = ImGuiNET.ImGui.Button(row.ButtonText ?? row.Label);
        Tooltip(row);
        if (row.Confirm != null)
        {
            // Asks first: the question, then the button's own caption to go ahead, or Cancel.
            if (clicked)
            {
                _confirmText = row.Confirm();
                ImGuiNET.ImGui.OpenPopup("##confirm");
            }
            clicked = ConfirmPopup(row.ButtonText ?? row.Label);
        }
        if (clicked)
        {
            // A settings edit is applied to this copy and saved like a typed value (the number
            // boxes re-sync from the copy next frame); Click, when also set, runs after.
            if (row.Edit != null)
            {
                row.Edit(_data);
                SettingsCommands.Save(_data.Clone());
            }
            row.Click?.Invoke();
        }
    }

    private string _confirmText = string.Empty;

    /// <summary>The open confirmation (the row's ID scope): true when the player went ahead.</summary>
    private bool ConfirmPopup(string caption)
    {
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, ShellBg);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, Teal);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12, 10));
        bool open = ImGuiNET.ImGui.BeginPopup("##confirm");
        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.PopStyleColor(2);
        if (!open) return false;
        bool go = false;
        try
        {
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, TextDim);
            ImGuiNET.ImGui.TextUnformatted(_confirmText);
            ImGuiNET.ImGui.PopStyleColor();
            ImGuiNET.ImGui.Dummy(new Vector2(0, 4));
            if (ImGuiNET.ImGui.Button(caption.TrimEnd('.') + "##confirm_go")) { go = true; ImGuiNET.ImGui.CloseCurrentPopup(); }
            ImGuiNET.ImGui.SameLine(0, 8);
            if (ImGuiNET.ImGui.Button("Cancel##confirm_no")) ImGuiNET.ImGui.CloseCurrentPopup();
        }
        finally { ImGuiNET.ImGui.EndPopup(); }
        return go;
    }

    private void Changed(SettingRow row, double value)
    {
        row.Set!(_data, value);
        SettingsCommands.Save(_data.Clone());
    }

    // Square dot + label; the whole row toggles.
    private void BoolRow(SettingRow row, float w)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        bool on = row.Get!(_data) != 0;
        float labelW = ImGuiNET.ImGui.CalcTextSize(row.Label).X;
        if (ImGuiNET.ImGui.InvisibleButton("##b", new Vector2(Math.Min(w, 18 + labelW), 16)))
            Changed(row, on ? 0 : 1);
        if (ImGuiNET.ImGui.IsItemHovered()) ImGuiNET.ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        Tooltip(row);
        dl.AddRectFilled(p + new Vector2(0, 2), p + new Vector2(12, 14), on ? ToggleOn : ToggleOff, 2);
        dl.AddText(p + new Vector2(18, (16 - ImGuiNET.ImGui.GetFontSize()) * 0.5f), TextDim, row.Label);
        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
    }

    // Label | [-] [value] [+]; the value commits on Enter or focus loss.
    private void NumberRow(SettingRow row, float w)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        bool isInt = row.Kind == SettingKind.Int;
        float boxW = isInt ? 52 : 64, boxCol = isInt ? 56 : 68;
        double value = Math.Clamp(row.Get!(_data), row.Min, row.Max);

        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        float right = p.X + w;
        dl.AddText(p + new Vector2(0, (22 - ImGuiNET.ImGui.GetFontSize()) * 0.5f), TextDim, row.Label);
        ImGuiNET.ImGui.InvisibleButton("##label", new Vector2(Math.Max(1, w - 40 - boxCol), 22));
        Tooltip(row);

        float plusX = right - 20, boxX = plusX - boxCol + (boxCol - boxW) * 0.5f, minusX = right - 20 - boxCol - 20;
        if (StepButton("##minus", new Vector2(minusX + 1, p.Y + 1), PhosphorIcons.Minus))
            Changed(row, Math.Clamp(value - row.Step, row.Min, row.Max));

        // Typed value (amber on the panel colour).
        NumberEdit edit = EditFor(row);
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(boxX, p.Y + 1));
        ImGuiNET.ImGui.SetNextItemWidth(boxW);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg, PanelBg);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Amber);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, BtnBord);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(4, (20 - ImGuiNET.ImGui.GetFontSize()) * 0.5f));
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0f);
        if (!edit.Active && edit.Shown != value)
            WriteValue(edit, value, isInt);
        ImGuiNET.ImGui.InputText("##v", edit.Buffer, (uint)edit.Buffer.Length,
            ImGuiInputTextFlags.CharsDecimal | ImGuiInputTextFlags.AutoSelectAll);
        bool active = ImGuiNET.ImGui.IsItemActive();
        edit.Active = active;
        _editing |= active;
        if (ImGuiNET.ImGui.IsItemDeactivatedAfterEdit())
        {
            if (TryParse(edit.Buffer, out double typed))
            {
                double clamped = Math.Clamp(isInt ? Math.Round(typed) : typed, row.Min, row.Max);
                Changed(row, clamped);
                WriteValue(edit, clamped, isInt);
            }
            else WriteValue(edit, value, isInt); // bad input reverts
        }
        ImGuiNET.ImGui.PopStyleVar(3);
        ImGuiNET.ImGui.PopStyleColor(3);

        if (StepButton("##plus", new Vector2(plusX + 1, p.Y + 1), PhosphorIcons.Plus))
            Changed(row, Math.Clamp(value + row.Step, row.Min, row.Max));

        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + 22));
        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
    }

    // Label | [swatch]; a click opens a colour picker. These are the theme's colours (engine-stored): each
    // change applies at once and is saved by the theme, so the profile isn't saved for it.
    private void ColorRow(SettingRow row, float w)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        dl.AddText(p + new Vector2(0, (22 - ImGuiNET.ImGui.GetFontSize()) * 0.5f), TextDim, row.Label);
        const float swatchW = 52;
        ImGuiNET.ImGui.InvisibleButton("##label", new Vector2(Math.Max(1, w - swatchW - 8), 22));
        Tooltip(row);

        uint argb = (uint)row.Get!(_data);
        var col = new Vector4(((argb >> 16) & 0xFF) / 255f, ((argb >> 8) & 0xFF) / 255f, (argb & 0xFF) / 255f, 1f);
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X + w - swatchW, p.Y + 1));
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, BtnBord);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        if (ImGuiNET.ImGui.ColorButton("##swatch", col, ImGuiColorEditFlags.NoAlpha | ImGuiColorEditFlags.NoTooltip, new Vector2(swatchW, 20)))
            ImGuiNET.ImGui.OpenPopup("##picker");
        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.PopStyleColor();
        ImGuiNET.ImGui.SetItemTooltip("Click to pick a colour.");

        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, ShellBg);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, BtnBord);
        bool open = ImGuiNET.ImGui.BeginPopup("##picker");
        ImGuiNET.ImGui.PopStyleColor(2);
        if (open)
        {
            var rgb = new Vector3(col.X, col.Y, col.Z);
            ImGuiNET.ImGui.SetNextItemWidth(200);
            if (ImGuiNET.ImGui.ColorPicker3("##pick", ref rgb, ImGuiColorEditFlags.NoAlpha | ImGuiColorEditFlags.NoSidePreview
                    | ImGuiColorEditFlags.PickerHueWheel | ImGuiColorEditFlags.DisplayHex))
            {
                uint packed = 0xFF000000 | ((uint)MathF.Round(Math.Clamp(rgb.X, 0f, 1f) * 255) << 16)
                              | ((uint)MathF.Round(Math.Clamp(rgb.Y, 0f, 1f) * 255) << 8) | (uint)MathF.Round(Math.Clamp(rgb.Z, 0f, 1f) * 255);
                row.Set!(_data, packed);
            }
            ImGuiNET.ImGui.EndPopup();
        }

        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + 22));
        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
    }

    private NumberEdit EditFor(SettingRow row)
    {
        if (!_edits.TryGetValue(row, out NumberEdit? e))
            _edits[row] = e = new NumberEdit();
        return e;
    }

    private static void WriteValue(NumberEdit edit, double value, bool isInt)
    {
        string text = isInt ? ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture)
                            : value.ToString("G4", CultureInfo.InvariantCulture);
        int n = Encoding.UTF8.GetBytes(text, 0, text.Length, edit.Buffer, 0);
        edit.Buffer[Math.Min(n, edit.Buffer.Length - 1)] = 0;
        edit.Shown = value;
    }

    private static bool TryParse(byte[] buffer, out double value)
    {
        int len = Array.IndexOf(buffer, (byte)0);
        if (len < 0) len = buffer.Length;
        return double.TryParse(Encoding.UTF8.GetString(buffer, 0, len), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    // Label | [current item]; click opens a 220 px list with the current item highlighted.
    private void ComboRow(SettingRow row, float w)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        string[] items = row.CurrentItems;
        if (items.Length == 0) return;
        int current = Math.Clamp((int)row.Get!(_data), 0, items.Length - 1);
        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        dl.AddText(p + new Vector2(0, (22 - ImGuiNET.ImGui.GetFontSize()) * 0.5f), TextDim, row.Label);

        ImFontPtr f10 = ImGuiFonts.Get(UiFont.Ui10);
        const float btnW = 160;
        var bp = new Vector2(p.X + w - btnW, p.Y + 1);
        ImGuiNET.ImGui.SetCursorScreenPos(bp);
        if (ImGuiNET.ImGui.InvisibleButton("##combo", new Vector2(btnW, 20)))
        {
            ImGuiNET.ImGui.SetNextWindowPos(new Vector2(Math.Max(bp.X + btnW - 220, 0), bp.Y + 20));
            ImGuiNET.ImGui.OpenPopup("##combo_list");
        }
        bool hot = ImGuiNET.ImGui.IsItemHovered();
        Tooltip(row);
        dl.AddRectFilled(bp, bp + new Vector2(btnW, 20), hot ? Lighten(BtnFill) : BtnFill);
        dl.AddRect(bp, bp + new Vector2(btnW, 20), BtnBord);
        dl.PushClipRect(bp, bp + new Vector2(btnW - 16, 20), true);
        dl.AddText(f10, f10.FontSize, bp + new Vector2(4, (20 - f10.FontSize) * 0.5f), TextDim, items[current]);
        dl.PopClipRect();
        PhosphorIcons.DrawCentered(dl, f10, PhosphorIcons.CaretDown, bp + new Vector2(btnW - 16, 0), new Vector2(14, 20), Mute);

        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, ShellBg);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, Teal);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 4f);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(2, 2));
        ImGuiNET.ImGui.SetNextWindowSize(new Vector2(220, items.Length * 21 + 4));
        bool open = ImGuiNET.ImGui.BeginPopup("##combo_list");
        ImGuiNET.ImGui.PopStyleVar(2);
        ImGuiNET.ImGui.PopStyleColor(2);
        if (open)
        {
            var pdl = ImGuiNET.ImGui.GetWindowDrawList();
            float iw = ImGuiNET.ImGui.GetContentRegionAvail().X;
            for (int i = 0; i < items.Length; i++)
            {
                ImGuiNET.ImGui.PushID(i);
                Vector2 ip = ImGuiNET.ImGui.GetCursorScreenPos();
                bool picked = ImGuiNET.ImGui.InvisibleButton("##item", new Vector2(iw, 20));
                bool ih = ImGuiNET.ImGui.IsItemHovered();
                ImGuiNET.ImGui.PopID();
                bool sel = i == current;
                pdl.AddRectFilled(ip, ip + new Vector2(iw, 20), sel ? TabActive : ih ? Lighten(BtnFill) : BtnFill);
                pdl.AddRect(ip, ip + new Vector2(iw, 20), BtnBord);
                pdl.AddText(f10, f10.FontSize, ip + new Vector2(6, (20 - f10.FontSize) * 0.5f), sel ? Teal : TextDim, items[i]);
                ImGuiNET.ImGui.Dummy(new Vector2(0, 1));
                if (picked)
                {
                    Changed(row, i);
                    ImGuiNET.ImGui.CloseCurrentPopup();
                }
            }
            ImGuiNET.ImGui.EndPopup();
        }

        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + 22));
        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
    }

    private static void Section(string text, float w)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        ImGuiNET.ImGui.Dummy(new Vector2(0, 4));
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        ImFontPtr bold = ImGuiFonts.Get(UiFont.UiBold11);
        float h = bold.FontSize + 4;
        dl.AddRectFilled(p, p + new Vector2(w, h), TabActive);
        dl.AddText(bold, bold.FontSize, p + new Vector2(4, 2), Amber, text);
        ImGuiNET.ImGui.Dummy(new Vector2(w, h));
        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
    }

    private static void Note(string text, float w)
    {
        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
        bool small = !text.StartsWith('(');
        if (small) ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui10));
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Mute);
        ImGuiNET.ImGui.PushTextWrapPos(ImGuiNET.ImGui.GetCursorPosX() + w);
        ImGuiNET.ImGui.TextUnformatted(text);
        ImGuiNET.ImGui.PopTextWrapPos();
        ImGuiNET.ImGui.PopStyleColor();
        if (small) ImGuiNET.ImGui.PopFont();
    }

    private void CraftingStatus(SettingRow row)
    {
        ImGuiNET.ImGui.Dummy(new Vector2(0, 2));
        SafeImGui.TextColored(RynthTheme.VecOf(RynthTheme.TextMuted), row.Label);
        ImGuiNET.ImGui.SameLine(0, 6);
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.UiBold11));
        SafeImGui.TextColored(RynthTheme.VecOf(_data.MissileCraftingActive ? RynthTheme.Highlight : RynthTheme.TextMuted), _data.MissileCraftingState);
        ImGuiNET.ImGui.PopFont();
        if (!string.IsNullOrEmpty(_data.MissileCraftingStatus))
        {
            ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui10));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, TextDim);
            SafeImGui.TextWrapped(_data.MissileCraftingStatus);
            ImGuiNET.ImGui.PopStyleColor();
            ImGuiNET.ImGui.PopFont();
        }
    }

    private static bool StepButton(string id, Vector2 pos, string glyph)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGuiNET.ImGui.InvisibleButton(id, new Vector2(18, 20));
        uint bg = ImGuiNET.ImGui.IsItemActive() ? Darken(BtnFill) : ImGuiNET.ImGui.IsItemHovered() ? Lighten(BtnFill) : BtnFill;
        dl.AddRectFilled(pos, pos + new Vector2(18, 20), bg);
        dl.AddRect(pos, pos + new Vector2(18, 20), BtnBord);
        PhosphorIcons.DrawCentered(dl, ImGuiFonts.Get(UiFont.Ui11), glyph, pos, new Vector2(18, 20), TextDim);
        return clicked;
    }

    private static void Tooltip(SettingRow row)
    {
        if (row.Tooltip != null) SafeImGui.SetItemTooltip(row.Tooltip);
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
