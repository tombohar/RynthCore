// ============================================================================
//  RynthCore.Engine - ImGui/Panels/FaceKit.cs
//  Widgets the RynthAi-style ImGui faces share: the flat bordered button (and
//  its icon-only form), a text box with a grey hint, a picker (button + popup
//  list), wrapped button rows, and UTF-8 text buffers. Colours are the panels' palette (ABGR),
//  with text brighter than the Avalonia faces (Tom, 2026-09-28).
//
//  Everything runs on AC's render thread inside a face's Draw. No per-frame
//  allocation except what callers pass in.
// ============================================================================

using System;
using System.Numerics;
using System.Text;
using ImGuiNET;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal static class FaceKit
{
    private static uint C(uint argb) => RynthTheme.Argb(argb);

    public static readonly uint Green = C(0xFF33CC66), Red = C(0xFFE05A5A), StartBg = C(0xFF196119),
        StopBg = C(0xFF801919), DeleteBg = C(0xFF801A1A);
    public static uint Teal => RynthTheme.Accent;
    public static uint Amber => RynthTheme.Highlight;
    public static uint Mute => RynthTheme.MuteAlt;
    public static uint Text => RynthTheme.Text;
    public static uint ShellBg => RynthTheme.Background;
    public static uint PanelBg => RynthTheme.Field;
    public static uint RowAlt => RynthTheme.RowAlt;
    public static uint BtnFill => RynthTheme.Button;
    public static uint BtnBord => RynthTheme.Border;
    public static uint Selected => RynthTheme.Selected;

    // ── Frame ────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts an edge-to-edge face body: shell background, Ui10 font, a 6 px
    /// inset kept on every line (Indent). Returns the content width. Pair with End.
    /// </summary>
    public static float Begin(out Vector2 origin, out Vector2 size)
    {
        origin = ImGuiNET.ImGui.GetCursorScreenPos();
        size = ImGuiNET.ImGui.GetContentRegionAvail();
        ImGuiNET.ImGui.GetWindowDrawList().AddRectFilled(origin, origin + size, ShellBg, 4);
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui10));
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4, 4));
        ImGuiNET.ImGui.SetCursorScreenPos(origin + new Vector2(6, 6));
        ImGuiNET.ImGui.Indent(6);
        return size.X - 12;
    }

    public static void End(Vector2 origin, Vector2 size)
    {
        ImGuiNET.ImGui.Unindent(6);
        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.PopFont();
        ImGuiNET.ImGui.GetWindowDrawList().AddRect(origin, origin + size, BtnBord, 4);
    }

    /// <summary>
    /// A GripInBody face: how far the resize grip reaches above the 6 px bottom
    /// margin (0 for other faces). A scrolling child that runs to the margin ends
    /// this much higher so its scrollbar stays clear of the grip.
    /// </summary>
    public static float GripOverlap() => Math.Max(0f, ImGuiPanelHost.BodyGripReserve - 6f);

    /// <summary>Space left below the cursor down to the body's bottom (6 px margin).</summary>
    public static float Remaining(Vector2 origin, Vector2 size) =>
        origin.Y + size.Y - 6 - ImGuiNET.ImGui.GetCursorScreenPos().Y;

    /// <summary>Moves the layout cursor to the start of the line below a row drawn at <paramref name="rowStart"/>.</summary>
    public static void NextLine(Vector2 rowStart, float rowHeight)
    {
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(rowStart.X, rowStart.Y + rowHeight));
        ImGuiNET.ImGui.Dummy(new Vector2(0, 0));
    }

    public static void Separator(float w)
    {
        Vector2 p = ImGuiNET.ImGui.GetCursorScreenPos();
        ImGuiNET.ImGui.GetWindowDrawList().AddLine(p + new Vector2(0, 1), p + new Vector2(w, 1), BtnBord);
        ImGuiNET.ImGui.Dummy(new Vector2(w, 2));
    }

    public static void Label(string text, uint color)
    {
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGuiNET.ImGui.TextUnformatted(text);
        ImGuiNET.ImGui.PopStyleColor();
    }

    // ── Buttons ──────────────────────────────────────────────────────────

    /// <summary>A flat bordered button at <paramref name="pos"/>.</summary>
    public static bool Button(string id, string label, Vector2 pos, Vector2 size, uint fg, uint bg,
        bool enabled = true, bool leftAlign = false, uint border = 0)
    {
        bool clicked = ButtonFrame(id, pos, size, bg, enabled, border);
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        Vector2 ts = ImGuiNET.ImGui.CalcTextSize(label);
        float tx = leftAlign ? pos.X + 6 : pos.X + (size.X - ts.X) * 0.5f;
        dl.PushClipRect(pos, pos + size, true);
        dl.AddText(new Vector2(tx, pos.Y + (size.Y - ts.Y) * 0.5f), enabled ? fg : Faded(fg), label);
        dl.PopClipRect();
        return clicked;
    }

    /// <summary>
    /// Button's look with a Phosphor icon centred on it. <paramref name="font"/>
    /// sets the icon's size: Ui14 (about 13 px) for 20+ px buttons, Ui11 for smaller.
    /// </summary>
    public static bool IconButton(string id, string icon, Vector2 pos, Vector2 size, uint fg, uint bg,
        bool enabled = true, uint border = 0, UiFont font = UiFont.Ui14)
    {
        bool clicked = ButtonFrame(id, pos, size, bg, enabled, border);
        PhosphorIcons.DrawCentered(ImGuiNET.ImGui.GetWindowDrawList(), ImGuiFonts.Get(font), icon, pos, size, enabled ? fg : Faded(fg));
        return clicked;
    }

    private static bool ButtonFrame(string id, Vector2 pos, Vector2 size, uint bg, bool enabled, uint border)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGuiNET.ImGui.InvisibleButton(id, size) && enabled;
        bool hot = enabled && ImGuiNET.ImGui.IsItemHovered();
        uint fill = enabled && ImGuiNET.ImGui.IsItemActive() ? Darken(bg) : hot ? Lighten(bg) : bg;
        dl.AddRectFilled(pos, pos + size, fill, 3);
        dl.AddRect(pos, pos + size, border != 0 ? border : BtnBord, 3);
        return clicked;
    }

    /// <summary>Width a button needs for <paramref name="label"/> at the current font.</summary>
    public static float ButtonWidth(string label) => ImGuiNET.ImGui.CalcTextSize(label).X + 16;

    /// <summary>
    /// A draggable column edge at screen x (a 6 px handle, resize cursor on hover).
    /// Dragging changes <paramref name="width"/> (never below <paramref name="min"/>);
    /// <paramref name="onCommit"/> runs when the drag ends, to save it. Leaves the
    /// cursor where it was. Returns true while the width is changing.
    /// </summary>
    public static bool ColumnDivider(string id, float x, float top, float height, ref float width, float min,
        Action<float>? onCommit)
    {
        Vector2 saved = ImGuiNET.ImGui.GetCursorScreenPos();
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x - 3, top));
        ImGuiNET.ImGui.SetNextItemAllowOverlap();
        ImGuiNET.ImGui.InvisibleButton(id, new Vector2(6, height));
        bool active = ImGuiNET.ImGui.IsItemActive();
        bool hot = active || ImGuiNET.ImGui.IsItemHovered();
        if (hot) ImGuiNET.ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
        bool changed = false;
        if (active)
        {
            float dx = ImGuiNET.ImGui.GetIO().MouseDelta.X;
            if (dx != 0)
            {
                width = Math.Max(min, width + dx);
                changed = true;
            }
        }
        if (ImGuiNET.ImGui.IsItemDeactivated()) onCommit?.Invoke(width);
        ImGuiNET.ImGui.GetWindowDrawList().AddLine(new Vector2(x, top + 3), new Vector2(x, top + height - 3),
            hot ? Teal : BtnBord, hot ? 2f : 1f);
        ImGuiNET.ImGui.SetCursorScreenPos(saved);
        return changed;
    }

    /// <summary>
    /// A row of buttons that wraps to the width; returns the index clicked or -1.
    /// <paramref name="enabled"/> may be null (all enabled).
    /// </summary>
    public static int WrapButtons(string idPrefix, string[] labels, float w, float height = 24, bool[]? enabled = null)
    {
        Vector2 start = ImGuiNET.ImGui.GetCursorScreenPos();
        float x = start.X, y = start.Y;
        int clicked = -1;
        for (int i = 0; i < labels.Length; i++)
        {
            float bw = ButtonWidth(labels[i]);
            if (x > start.X && x + bw > start.X + w) { x = start.X; y += height + 4; }
            ImGuiNET.ImGui.PushID(idPrefix + i);
            if (Button("##b", labels[i], new Vector2(x, y), new Vector2(bw, height), Text, BtnFill, enabled == null || enabled[i]))
                clicked = i;
            ImGuiNET.ImGui.PopID();
            x += bw + 4;
        }
        NextLine(start, y - start.Y + height);
        return clicked;
    }

    // ── Text input ───────────────────────────────────────────────────────

    /// <summary>
    /// A bordered text box with a grey <paramref name="hint"/> while empty.
    /// Returns true when Enter was pressed (with EnterReturnsTrue) or the text changed.
    /// </summary>
    public static bool TextBox(string id, byte[] buffer, Vector2 pos, float width, string hint, out bool active,
        ImGuiInputTextFlags flags = ImGuiInputTextFlags.None, float height = 22)
    {
        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        ImGuiNET.ImGui.SetNextItemWidth(width);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg, BtnFill);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Text);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, BtnBord);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(4, (height - ImGuiNET.ImGui.GetFontSize()) * 0.5f));
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 3f);
        bool result = ImGuiNET.ImGui.InputText(id, buffer, (uint)buffer.Length, flags);
        active = ImGuiNET.ImGui.IsItemActive();
        if (active) ImGuiTextFocus.NoteActive(id);   // keys stay out of AC from this frame on
        ImGuiNET.ImGui.PopStyleVar(3);
        ImGuiNET.ImGui.PopStyleColor(3);
        if (buffer[0] == 0 && !active && hint.Length > 0)
            ImGuiNET.ImGui.GetWindowDrawList().AddText(pos + new Vector2(5, (height - ImGuiNET.ImGui.GetFontSize()) * 0.5f), Faded(Mute), hint);
        return result;
    }

    // ── Picker ───────────────────────────────────────────────────────────

    /// <summary>
    /// One popup list per face, opened from any button: call Open from the
    /// button's click and Draw once per frame at the end of the face's Draw
    /// (outside any child window, so the popup id always matches). Long lists
    /// (Slayer Type's creature names, materials, long keys) get a type-to-filter
    /// box so players can pick by name instead of scrolling.
    /// </summary>
    internal sealed class Picker
    {
        /// <summary>Show the filter box when the list is at least this long.</summary>
        private const int FilterAt = 12;
        private const float FilterH = 22f;
        private const float RowH = 20f;

        private readonly string _popupId;
        private readonly byte[] _filter = new byte[64];
        private string[] _items = Array.Empty<string>();
        private int _selected = -1;
        private Action<int>? _onPick;
        private Vector2 _pos;
        private float _width = 220;
        private bool _openRequested;
        private bool _focusFilter;

        public Picker(string popupId) => _popupId = popupId;

        public void Open(Vector2 below, string[] items, int selected, Action<int> onPick, float width = 220)
        {
            _items = items;
            _selected = selected;
            _onPick = onPick;
            _pos = below;
            _width = width;
            _openRequested = items.Length > 0;
            _filter[0] = 0;          // clear the previous open's filter
            _focusFilter = items.Length >= FilterAt;
        }

        public void Draw()
        {
            if (_openRequested)
            {
                _openRequested = false;
                Vector2 display = ImGuiNET.ImGui.GetIO().DisplaySize;
                ImGuiNET.ImGui.SetNextWindowPos(new Vector2(Math.Clamp(_pos.X, 0, Math.Max(0, display.X - _width)), _pos.Y));
                ImGuiNET.ImGui.OpenPopup(_popupId);
            }
            bool filterOn = _items.Length >= FilterAt;
            string filterText = filterOn ? Utf8(_filter) : string.Empty;
            // Count matches so the popup height tracks a short filtered list.
            int shown = 0;
            if (!filterOn) shown = _items.Length;
            else
            {
                for (int i = 0; i < _items.Length; i++)
                    if (FilterMatch(_items[i], filterText)) shown++;
            }
            float listH = Math.Min(Math.Max(shown, 1) * RowH + 4, 340);
            float totalH = listH + (filterOn ? FilterH + 4 : 0);

            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, ShellBg);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, Teal);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 4f);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(2, 2));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 1));
            ImGuiNET.ImGui.SetNextWindowSize(new Vector2(_width, totalH));
            bool open = ImGuiNET.ImGui.BeginPopup(_popupId);
            ImGuiNET.ImGui.PopStyleVar(3);
            ImGuiNET.ImGui.PopStyleColor(2);
            if (!open) return;

            if (filterOn)
            {
                Vector2 fp = ImGuiNET.ImGui.GetCursorScreenPos();
                if (_focusFilter)
                {
                    _focusFilter = false;
                    ImGuiNET.ImGui.SetKeyboardFocusHere(0);   // next widget = the filter box
                }
                TextBox("##pick_filter", _filter, fp, ImGuiNET.ImGui.GetContentRegionAvail().X, "Type to filter...", out _,
                    ImGuiInputTextFlags.None, FilterH);
                ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(fp.X, fp.Y + FilterH + 2));
                filterText = Utf8(_filter);
            }

            var dl = ImGuiNET.ImGui.GetWindowDrawList();
            float iw = ImGuiNET.ImGui.GetContentRegionAvail().X;
            for (int i = 0; i < _items.Length; i++)
            {
                if (filterOn && !FilterMatch(_items[i], filterText)) continue;
                ImGuiNET.ImGui.PushID(i);
                Vector2 ip = ImGuiNET.ImGui.GetCursorScreenPos();
                bool click = ImGuiNET.ImGui.InvisibleButton("##it", new Vector2(iw, RowH));
                bool hot = ImGuiNET.ImGui.IsItemHovered();
                ImGuiNET.ImGui.PopID();
                bool sel = i == _selected;
                dl.AddRectFilled(ip, ip + new Vector2(iw, RowH), sel ? Selected : hot ? Lighten(BtnFill) : BtnFill);
                dl.AddRect(ip, ip + new Vector2(iw, RowH), BtnBord);
                dl.AddText(ip + new Vector2(6, (RowH - ImGuiNET.ImGui.GetFontSize()) * 0.5f), sel ? Teal : Text, _items[i]);
                if (click)
                {
                    Action<int>? act = _onPick;
                    ImGuiNET.ImGui.CloseCurrentPopup();
                    act?.Invoke(i);   // still the original index for the caller
                }
            }
            ImGuiNET.ImGui.EndPopup();
        }

        /// <summary>Case-insensitive substring match; empty filter shows everything.</summary>
        private static bool FilterMatch(string item, string filter)
        {
            if (filter.Length == 0) return true;
            return item.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    // ── Text buffers ─────────────────────────────────────────────────────

    public static string Utf8(byte[] buffer)
    {
        int len = Array.IndexOf(buffer, (byte)0);
        return Encoding.UTF8.GetString(buffer, 0, len < 0 ? buffer.Length : len);
    }

    /// <summary>Writes <paramref name="text"/> NUL-terminated, cut at a character boundary if it doesn't fit.</summary>
    public static void WriteUtf8(byte[] buffer, string text)
    {
        byte[] all = Encoding.UTF8.GetBytes(text ?? string.Empty);
        int n = Math.Min(all.Length, buffer.Length - 1);
        while (n > 0 && n < all.Length && (all[n] & 0xC0) == 0x80) n--;
        Array.Copy(all, buffer, n);
        buffer[n] = 0;
    }

    // ── Colour helpers ───────────────────────────────────────────────────

    public static uint Lighten(uint abgr) => Scale(abgr, 1.25f, 12);
    public static uint Darken(uint abgr) => Scale(abgr, 0.8f, 0);
    public static uint Faded(uint abgr) => (abgr & 0x00FFFFFF) | ((abgr >> 25) << 24);

    private static uint Scale(uint abgr, float k, int add)
    {
        uint a = abgr & 0xFF000000;
        uint r = (uint)Math.Min(255, (int)((abgr & 0xFF) * k) + add);
        uint g = (uint)Math.Min(255, (int)(((abgr >> 8) & 0xFF) * k) + add);
        uint b = (uint)Math.Min(255, (int)(((abgr >> 16) & 0xFF) * k) + add);
        return a | (b << 16) | (g << 8) | r;
    }
}
