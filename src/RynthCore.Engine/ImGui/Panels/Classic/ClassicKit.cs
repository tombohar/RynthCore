// ============================================================================
//  RynthCore.Engine - ImGui/Panels/Classic/ClassicKit.cs
//  The retail pieces the RynthAi dashboard's Classic look is drawn from: the
//  client's own UI pictures (0x06 textures in client_portal.dat, through
//  RetailSprites) cut and stretched the way the retail windows use them, plus
//  the few widgets built on them (tabs, the red and blue buttons, the gem
//  check, the green drop-down, the gold plate, number boxes, the vitals
//  meters, the chain scrollbar, a popup list).
//
//  Where the ids come from: the retail layouts as OpenAC resolves them
//  (tests/AcDream.App.Tests/UI/Layout/fixtures: options 0x2100006E, social,
//  vitals 0x2100006C) and AcClientReborn's tools/tex.py renders of them. The
//  art is the player's own install, read at runtime; nothing ships with it.
//
//  Geometry is in retail pixels times S (set by the face each frame: the UI
//  scale times the dashboard's size). A picture that isn't in yet draws a flat
//  stand-in so the window never shows holes while RetailSprites decodes.
//
//  AC's render thread only (inside the dashboard face's Draw). No per-frame
//  allocation.
// ============================================================================

using System;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.UI;

namespace RynthCore.Engine.ImGuiBackend.Panels.Classic;

internal static class ClassicKit
{
    // ── Retail pictures ─────────────────────────────────────────────────
    internal static class Art
    {
        // Window frame (5 px gold edges and corners; RetailChromeSprites in OpenAC).
        public const uint EdgeTop = 0x060074BFu, EdgeLeft = 0x060074C0u, EdgeBottom = 0x060074C1u, EdgeRight = 0x060074C2u;
        public const uint CornerTL = 0x060074C3u, CornerTR = 0x060074C4u, CornerBL = 0x060074C5u, CornerBR = 0x060074C6u;
        public const uint Fill = 0x06004CC2u;          // 48x48 dark panel fill
        public const uint Stone = 0x06004D0Au;         // 300x362 stone backdrop, 25 px title band on top
        public const uint TitleBar = 0x06004CFAu;      // 276x25 gold-bevelled title bar
        public const uint Close = 0x06004D0Cu;         // 24x25; +1 = pressed
        // Tabs (options panel 0x2100006E): 17 | 12 (stretched) | 17, 25 high. Even = open, odd = closed.
        public const uint TabLeftOpen = 0x06005D92u, TabLeftClosed = 0x06005D93u;
        public const uint TabMidOpen = 0x06005D94u, TabMidClosed = 0x06005D95u;
        public const uint TabRightOpen = 0x06005D96u, TabRightClosed = 0x06005D97u;
        public const uint Header = 0x06000F93u;        // 282x20 black bar, gold border (a 3 px nine-slice)
        public const uint RowSelected = 0x06000F86u;   // 282x20 teal row (skill header, trained)
        // Buttons: 32 | 15 (stretched) | 32, 32 high. -1 = pressed, -2 = ghosted.
        public const uint RedLeft = 0x06004C96u, RedMid = 0x06004C99u, RedRight = 0x06004C9Cu;
        public const uint BlueLeft = 0x060074A9u, BlueMid = 0x060074ACu, BlueRight = 0x060074AFu;
        public const uint GemOff = 0x06004D15u, GemOn = 0x06004D17u;   // 13x13 checkbox lights
        public const uint DropField = 0x060012B3u;     // 100x18 green field (3 px nine-slice); +1 = pressed
        public const uint DropCap = 0x060012B1u;       // 17x19 arrow cap; +1 = open
        public const uint ListBack = 0x0600124Eu, ListPick = 0x0600124Du;   // 191x17 popup rows
        public const uint Plate = 0x06004D65u;         // 46x17 gold plate; +1 = pressed
        // Vitals meters (0x2100006C): 10 | 100 (stretched) | 10, 16 high.
        public const uint HpFill = 0x06007481u, StFill = 0x06007487u, MnFill = 0x0600748Du;
        public const uint MeterBack = 0x06007484u, MeterBackMana = 0x0600748Au;
        public const uint HeartHp = 0x06004C3Bu, HeartSt = 0x06004C3Cu, HeartMn = 0x06004C3Du;   // 25x25
        // Chain scrollbar (0x2100003E).
        public const uint Track = 0x06004C5Fu;          // 16x32, tiled
        public const uint ThumbTop = 0x06004C60u, ThumbMid = 0x06004C63u, ThumbBottom = 0x06004C66u;
        public const uint ArrowUp = 0x06004C6Cu, ArrowDown = 0x06004C69u;   // 16x16; +1 rollover, +2 pressed
    }

    // ── Colours (ABGR) ──────────────────────────────────────────────────
    public static readonly uint Gold = C(0xFFF0C850), GoldDim = C(0xFFBCAA7C), White = C(0xFFFFFFFF),
        Text = C(0xFFD8D8D8), Dim = C(0xFFA8A8A8), Faint = C(0xFF8F8F8F), Black = C(0xFF000000),
        Shadow = C(0xC0000000), Red = C(0xFFFF8A70), Green = C(0xFF86D686), Amber = C(0xFFE8BC52),
        Down = C(0xFFFF8070), Hover = C(0x14FFDC8C), StoneFallback = C(0xFF111111), Field = C(0xFF0B0B0B),
        AltRow = C(0x0DFFFFFF);

    private static uint C(uint argb) => RynthTheme.Argb(argb);

    /// <summary>Retail pixels to screen pixels this frame (UI scale x dashboard size). Set by the face.</summary>
    public static float S = 1f;

    public static float Px(float v) => v * S;

    // ── Pictures ────────────────────────────────────────────────────────

    /// <summary>One picture (or a sub-rectangle by uv) into min..max; false (nothing drawn) until it's in.</summary>
    public static bool Img(ImDrawListPtr dl, uint id, Vector2 min, Vector2 max, Vector2 uv0, Vector2 uv1)
    {
        if (!RetailSprites.TryGet(id, out IntPtr tex)) return false;
        dl.AddImage(tex, min, max, uv0, uv1);
        return true;
    }

    public static bool Img(ImDrawListPtr dl, uint id, Vector2 min, Vector2 max) => Img(dl, id, min, max, Vector2.Zero, Vector2.One);

    /// <summary>A picture with fixed-width ends (srcL / srcR retail px of a srcW-wide picture) and a stretched middle.</summary>
    public static bool HSlice(ImDrawListPtr dl, uint id, Vector2 min, Vector2 max, float srcW, float srcL, float srcR)
    {
        if (!RetailSprites.TryGet(id, out IntPtr tex)) return false;
        float w = max.X - min.X;
        float l = Math.Min(Px(srcL), w * 0.5f), r = Math.Min(Px(srcR), w * 0.5f);
        float ul = srcL / srcW, ur = 1f - srcR / srcW;
        dl.AddImage(tex, min, new Vector2(min.X + l, max.Y), Vector2.Zero, new Vector2(ul, 1));
        if (w - l - r > 0)
            dl.AddImage(tex, new Vector2(min.X + l, min.Y), new Vector2(max.X - r, max.Y), new Vector2(ul, 0), new Vector2(ur, 1));
        dl.AddImage(tex, new Vector2(max.X - r, min.Y), max, new Vector2(ur, 0), Vector2.One);
        return true;
    }

    /// <summary>Three pictures side by side: fixed left and right caps, the middle one stretched between them.</summary>
    public static bool Three(ImDrawListPtr dl, uint left, uint mid, uint right, Vector2 min, Vector2 max, float capW)
    {
        if (!RetailSprites.TryGet(left, out IntPtr l) || !RetailSprites.TryGet(mid, out IntPtr m)
            || !RetailSprites.TryGet(right, out IntPtr r)) return false;
        float cap = Math.Min(Px(capW), (max.X - min.X) * 0.5f);
        dl.AddImage(l, min, new Vector2(min.X + cap, max.Y));
        if (max.X - min.X > 2 * cap) dl.AddImage(m, new Vector2(min.X + cap, min.Y), new Vector2(max.X - cap, max.Y));
        dl.AddImage(r, new Vector2(max.X - cap, min.Y), max);
        return true;
    }

    /// <summary>A nine-slice: a b-px border (retail px) kept, edges and centre stretched.</summary>
    public static bool Nine(ImDrawListPtr dl, uint id, Vector2 min, Vector2 max, float srcW, float srcH, float b)
    {
        if (!RetailSprites.TryGet(id, out IntPtr tex)) return false;
        float bx = Math.Min(Px(b), (max.X - min.X) * 0.5f), by = Math.Min(Px(b), (max.Y - min.Y) * 0.5f);
        float u1 = b / srcW, u2 = 1 - b / srcW, v1 = b / srcH, v2 = 1 - b / srcH;
        float x0 = min.X, x1 = min.X + bx, x2 = max.X - bx, x3 = max.X;
        float y0 = min.Y, y1 = min.Y + by, y2 = max.Y - by, y3 = max.Y;
        Span<float> xs = stackalloc float[] { x0, x1, x2, x3 };
        Span<float> ys = stackalloc float[] { y0, y1, y2, y3 };
        Span<float> us = stackalloc float[] { 0, u1, u2, 1 };
        Span<float> vs = stackalloc float[] { 0, v1, v2, 1 };
        for (int j = 0; j < 3; j++)
            for (int i = 0; i < 3; i++)
                if (xs[i + 1] > xs[i] && ys[j + 1] > ys[j])
                    dl.AddImage(tex, new Vector2(xs[i], ys[j]), new Vector2(xs[i + 1], ys[j + 1]),
                        new Vector2(us[i], vs[j]), new Vector2(us[i + 1], vs[j + 1]));
        return true;
    }

    // ── Window ──────────────────────────────────────────────────────────

    /// <summary>The 5 px gold frame around min..max (drawn inside it).</summary>
    public static void Frame(ImDrawListPtr dl, Vector2 min, Vector2 max)
    {
        float e = Px(5);
        if (!RetailSprites.TryGet(Art.EdgeTop, out IntPtr top) || !RetailSprites.TryGet(Art.EdgeBottom, out IntPtr bottom)
            || !RetailSprites.TryGet(Art.EdgeLeft, out IntPtr left) || !RetailSprites.TryGet(Art.EdgeRight, out IntPtr right))
        {
            dl.AddRect(min, max, Gold, 0, ImDrawFlags.None, Math.Max(1, Px(1.5f)));
            return;
        }
        dl.AddImage(top, new Vector2(min.X + e, min.Y), new Vector2(max.X - e, min.Y + e));
        dl.AddImage(bottom, new Vector2(min.X + e, max.Y - e), new Vector2(max.X - e, max.Y));
        dl.AddImage(left, new Vector2(min.X, min.Y + e), new Vector2(min.X + e, max.Y - e));
        dl.AddImage(right, new Vector2(max.X - e, min.Y + e), new Vector2(max.X, max.Y - e));
        Img(dl, Art.CornerTL, min, min + new Vector2(e, e));
        Img(dl, Art.CornerTR, new Vector2(max.X - e, min.Y), new Vector2(max.X, min.Y + e));
        Img(dl, Art.CornerBL, new Vector2(min.X, max.Y - e), new Vector2(min.X + e, max.Y));
        Img(dl, Art.CornerBR, max - new Vector2(e, e), max);
    }

    /// <summary>
    /// The body: the dark fill, with the stone picture over its top at the window's width (its 25 px
    /// title band left out, its height kept in proportion so the rock doesn't smear).
    /// </summary>
    public static void Backdrop(ImDrawListPtr dl, Vector2 min, Vector2 max, byte alpha)
    {
        uint tint = (uint)alpha << 24 | 0x00FFFFFF;
        float w = max.X - min.X, h = max.Y - min.Y;
        dl.AddRectFilled(min, max, (StoneFallback & 0x00FFFFFF) | ((uint)alpha << 24));
        if (!RetailSprites.TryGet(Art.Stone, out IntPtr stone)) return;
        const float SrcW = 300f, SrcH = 362f, Band = 25f;
        float stoneH = w * (SrcH - Band) / SrcW;
        float shown = Math.Min(h, stoneH);
        float v0 = Band / SrcH, v1 = v0 + (shown / stoneH) * (1 - v0);
        dl.AddImage(stone, min, new Vector2(max.X, min.Y + shown), new Vector2(0, v0), new Vector2(1, v1), tint);
    }

    /// <summary>The retail close button at pos (24x25 retail px); true when clicked.</summary>
    public static bool CloseButton(string id, Vector2 pos)
    {
        var size = new Vector2(Px(24), Px(25));
        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGuiNET.ImGui.InvisibleButton(id, size);
        bool down = ImGuiNET.ImGui.IsItemActive();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        if (!Img(dl, down ? Art.Close + 1 : Art.Close, pos, pos + size))
        {
            dl.AddRect(pos + new Vector2(Px(3), Px(3)), pos + size - new Vector2(Px(3), Px(3)), Gold, 2);
            dl.AddLine(pos + new Vector2(Px(7), size.Y * 0.5f), pos + new Vector2(size.X - Px(7), size.Y * 0.5f), Gold, 2);
        }
        return clicked;
    }

    // ── Text ────────────────────────────────────────────────────────────

    public static ImFontPtr Font(bool bold = false) => ImGuiFonts.Get(bold ? UiFont.UiBold11 : UiFont.Ui11);

    /// <summary>Text size in screen px at retail size <paramref name="px"/>.</summary>
    public static Vector2 Measure(string text, float px, bool bold = false)
    {
        ImFontPtr f = Font(bold);
        ImGuiNET.ImGui.PushFont(f);
        Vector2 v = ImGuiNET.ImGui.CalcTextSize(text) * (Px(px) / f.FontSize);
        ImGuiNET.ImGui.PopFont();
        return v;
    }

    /// <summary>Retail text: a 1 px black shadow under it, like the client's.</summary>
    public static void Print(ImDrawListPtr dl, Vector2 pos, string text, uint color, float px = 11, bool bold = false)
    {
        ImFontPtr f = Font(bold);
        float size = Px(px);
        float o = Math.Max(1f, Px(1));
        dl.AddText(f, size, pos + new Vector2(o, o), Shadow, text);
        dl.AddText(f, size, pos, color, text);
    }

    /// <summary>Text centred vertically in a row of height h starting at y.</summary>
    public static void TextMid(ImDrawListPtr dl, float x, float y, float h, string text, uint color, float px = 11, bool bold = false)
        => Print(dl, new Vector2(x, y + (h - Px(px) * 1.15f) * 0.5f), text, color, px, bold);

    /// <summary>Right-aligned text ending at <paramref name="right"/>.</summary>
    public static void TextRight(ImDrawListPtr dl, float right, float y, float h, string text, uint color, float px = 11, bool bold = false)
    {
        float w = Measure(text, px, bold).X;
        TextMid(dl, right - w, y, h, text, color, px, bold);
    }

    /// <summary>Text clipped to [x, right).</summary>
    public static void TextClip(ImDrawListPtr dl, float x, float y, float h, float right, string text, uint color, float px = 11, bool bold = false)
    {
        dl.PushClipRect(new Vector2(x, y), new Vector2(Math.Max(x + 1, right), y + h), true);
        TextMid(dl, x, y, h, text, color, px, bold);
        dl.PopClipRect();
    }

    // ── Widgets ─────────────────────────────────────────────────────────

    /// <summary>A section header: the gold-bordered black bar with a title and an optional right note.</summary>
    public static void Header(ImDrawListPtr dl, Vector2 pos, float w, string title, string? right = null)
    {
        float h = Px(18);
        var max = new Vector2(pos.X + w, pos.Y + h);
        if (!Nine(dl, Art.Header, pos, max, 282, 20, 3))
        {
            dl.AddRectFilled(pos, max, Black);
            dl.AddRect(pos, max, Gold);
        }
        TextMid(dl, pos.X + Px(6), pos.Y, h, title, Gold);
        if (!string.IsNullOrEmpty(right)) TextRight(dl, max.X - Px(6), pos.Y, h, right!, GoldDim, 10);
    }

    /// <summary>One retail tab (25 px high); true when clicked.</summary>
    public static bool Tab(string id, string label, Vector2 pos, float w, bool open)
    {
        float h = Px(25);
        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGuiNET.ImGui.InvisibleButton(id, new Vector2(w, h));
        bool hot = ImGuiNET.ImGui.IsItemHovered();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        var max = new Vector2(pos.X + w, pos.Y + h);
        if (!Three(dl, open ? Art.TabLeftOpen : Art.TabLeftClosed, open ? Art.TabMidOpen : Art.TabMidClosed,
                open ? Art.TabRightOpen : Art.TabRightClosed, pos, max, 17))
        {
            dl.AddRectFilled(pos, max, open ? StoneFallback : Black);
            dl.AddRect(pos, max, Gold);
        }
        float tw = Measure(label, 10).X;
        Print(dl, new Vector2(pos.X + (w - tw) * 0.5f, pos.Y + Px(8)), label, open ? White : hot ? Gold : Dim, 10);
        return clicked;
    }

    public enum ButtonColor { Red, Blue }

    /// <summary>A retail button (32 px high, any width); true when clicked.</summary>
    public static bool Button(string id, string label, Vector2 pos, float w, ButtonColor color, bool enabled = true)
    {
        float h = Px(32);
        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGuiNET.ImGui.InvisibleButton(id, new Vector2(w, h)) && enabled;
        bool down = enabled && ImGuiNET.ImGui.IsItemActive();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        var max = new Vector2(pos.X + w, pos.Y + h);
        uint l = color == ButtonColor.Red ? Art.RedLeft : Art.BlueLeft;
        uint m = color == ButtonColor.Red ? Art.RedMid : Art.BlueMid;
        uint r = color == ButtonColor.Red ? Art.RedRight : Art.BlueRight;
        uint d = !enabled ? 2u : down ? 1u : 0u;
        if (!Three(dl, l - d, m - d, r - d, pos, max, 32))
        {
            dl.AddRectFilled(pos, max, color == ButtonColor.Red ? C(0xFF6A1010) : C(0xFF10206A), Px(3));
            dl.AddRect(pos, max, Gold, Px(3));
        }
        float tw = Measure(label, 11).X;
        TextMid(dl, pos.X + (w - tw) * 0.5f, pos.Y, h, label, enabled ? White : Dim);
        return clicked;
    }

    /// <summary>
    /// A gem check with its label: the whole row is the button. Returns true when clicked (the caller flips
    /// the value). <paramref name="w"/> is the row's width.
    /// </summary>
    public static bool Gem(string id, string label, Vector2 pos, float w, bool on, float h = 18)
    {
        float rh = Px(h);
        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGuiNET.ImGui.InvisibleButton(id, new Vector2(w, rh));
        bool hot = ImGuiNET.ImGui.IsItemHovered();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        if (hot) dl.AddRectFilled(pos, pos + new Vector2(w, rh), Hover);
        GemPicture(dl, new Vector2(pos.X + Px(3), pos.Y + (rh - Px(13)) * 0.5f), on);
        TextClip(dl, pos.X + Px(22), pos.Y, rh, pos.X + w, label, on ? White : Text);
        return clicked;
    }

    public static void GemPicture(ImDrawListPtr dl, Vector2 pos, bool on)
    {
        var size = new Vector2(Px(13), Px(13));
        if (!Img(dl, on ? Art.GemOn : Art.GemOff, pos, pos + size))
            dl.AddCircleFilled(pos + size * 0.5f, size.X * 0.45f, on ? C(0xFF9BE65A) : C(0xFF3A3328));
    }

    /// <summary>The green drop-down (field + arrow cap) showing <paramref name="text"/>; true when clicked.</summary>
    public static bool Dropdown(string id, string text, Vector2 pos, float w, bool open = false)
    {
        float h = Px(19), capW = Px(17);
        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGuiNET.ImGui.InvisibleButton(id, new Vector2(w, h));
        bool down = ImGuiNET.ImGui.IsItemActive() || open;
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        var fieldMax = new Vector2(pos.X + w - capW, pos.Y + h);
        if (!Nine(dl, down ? Art.DropField + 1 : Art.DropField, pos, fieldMax, 100, 18, 3))
        {
            dl.AddRectFilled(pos, fieldMax, C(0xFF0E2A12));
            dl.AddRect(pos, fieldMax, C(0xFF3C7A44));
        }
        if (!Img(dl, down ? Art.DropCap + 1 : Art.DropCap, new Vector2(fieldMax.X, pos.Y), new Vector2(pos.X + w, pos.Y + h)))
            dl.AddTriangleFilled(new Vector2(fieldMax.X + Px(4), pos.Y + Px(7)), new Vector2(pos.X + w - Px(4), pos.Y + Px(7)),
                new Vector2(fieldMax.X + capW * 0.5f, pos.Y + Px(13)), C(0xFF3CC85A));
        TextClip(dl, pos.X + Px(5), pos.Y, h, fieldMax.X - Px(3), text, White, 10);
        return clicked;
    }

    /// <summary>The small gold plate button (46x17 retail px); true when clicked.</summary>
    public static bool Plate(string id, string label, Vector2 pos)
    {
        var size = new Vector2(Px(46), Px(17));
        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGuiNET.ImGui.InvisibleButton(id, size);
        bool down = ImGuiNET.ImGui.IsItemActive();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        if (!Img(dl, down ? Art.Plate + 1 : Art.Plate, pos, pos + size))
        {
            dl.AddRectFilled(pos, pos + size, C(0xFFB08A3C), Px(2));
            dl.AddRect(pos, pos + size, Gold, Px(2));
        }
        float tw = Measure(label, 10).X;
        ImFontPtr f = Font();
        dl.AddText(f, Px(10), new Vector2(pos.X + Px(7) + (size.X - Px(7) - tw) * 0.5f, pos.Y + (size.Y - Px(10) * 1.15f) * 0.5f),
            C(0xFF3A2200), label);
        return clicked;
    }

    /// <summary>A selected list row's teal highlight.</summary>
    public static void RowSelected(ImDrawListPtr dl, Vector2 min, Vector2 max)
    {
        if (!Nine(dl, Art.RowSelected, min, max, 282, 20, 2))
            dl.AddRectFilled(min, max, C(0xFF1D5A63));
    }

    /// <summary>The frame of a number box (the header bar's gold border, small); the caller draws the input over it.</summary>
    public static void FieldFrame(ImDrawListPtr dl, Vector2 min, Vector2 max)
    {
        if (!Nine(dl, Art.Header, min, max, 282, 20, 3))
        {
            dl.AddRectFilled(min, max, Field);
            dl.AddRect(min, max, Gold);
        }
    }

    /// <summary>
    /// A retail number box: the gold-bordered field with an ImGui text input over it (keys kept out of AC
    /// while it's typed in, like FaceKit's boxes). Returns true when an edit was finished (Enter or focus
    /// loss) with <paramref name="buffer"/> holding the text.
    /// </summary>
    public static bool NumberBox(string id, byte[] buffer, Vector2 pos, float w, out bool active)
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
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, 0u);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, White);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.TextSelectedBg, C(0x66F0C850));
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(Px(4), Math.Max(0, (h - fs) * 0.5f)));
        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        ImGuiNET.ImGui.SetNextItemWidth(w);
        ImGuiNET.ImGui.InputText(id, buffer, (uint)buffer.Length, ImGuiInputTextFlags.CharsDecimal | ImGuiInputTextFlags.AutoSelectAll);
        active = ImGuiNET.ImGui.IsItemActive();
        if (active) ImGuiTextFocus.NoteActive(id);   // keys stay out of AC from this frame on
        bool done = ImGuiNET.ImGui.IsItemDeactivatedAfterEdit();
        ImGuiNET.ImGui.PopStyleVar(2);
        ImGuiNET.ImGui.PopStyleColor(6);
        ImGuiNET.ImGui.SetWindowFontScale(1f);
        ImGuiNET.ImGui.PopFont();
        return done;
    }

    public enum Vital { Health, Stamina, Mana }

    /// <summary>A retail vitals meter (16 px high): the empty bar, the filled part clipped to <paramref name="frac"/>.</summary>
    public static void Meter(ImDrawListPtr dl, Vector2 pos, float w, Vital vital, float frac)
    {
        float h = Px(16);
        var max = new Vector2(pos.X + w, pos.Y + h);
        uint back = vital == Vital.Mana ? Art.MeterBackMana : Art.MeterBack;
        uint fill = vital switch { Vital.Health => Art.HpFill, Vital.Stamina => Art.StFill, _ => Art.MnFill };
        if (!Three(dl, back, back + 1, back + 2, pos, max, 10))
            dl.AddRectFilled(pos, max, C(0xFF161210), Px(3));
        float fx = pos.X + w * Math.Clamp(frac, 0f, 1f);
        if (fx <= pos.X) return;
        dl.PushClipRect(pos, new Vector2(fx, max.Y), true);
        if (!Three(dl, fill, fill + 1, fill + 2, pos, max, 10))
        {
            uint col = vital switch { Vital.Health => C(0xFFC81E1E), Vital.Stamina => C(0xFFD8A21E), _ => C(0xFF1E78DC) };
            dl.AddRectFilled(pos, max, col, Px(3));
        }
        dl.PopClipRect();
    }

    public static void Heart(ImDrawListPtr dl, Vector2 pos, Vital vital)
    {
        uint id = vital switch { Vital.Health => Art.HeartHp, Vital.Stamina => Art.HeartSt, _ => Art.HeartMn };
        var size = new Vector2(Px(25), Px(25));
        if (!Img(dl, id, pos, pos + size))
            dl.AddCircleFilled(pos + size * 0.5f, Px(8), vital switch
                { Vital.Health => C(0xFFC81E1E), Vital.Stamina => C(0xFFD8C21E), _ => C(0xFF1E5ADC) });
    }

    /// <summary>
    /// The chain scrollbar beside a list (16 px wide, <paramref name="h"/> high): arrows step one row, the
    /// thumb drags, the track pages. Returns the first visible row.
    /// </summary>
    public static int Scrollbar(string id, Vector2 pos, float h, int total, int visible, int first, ref float grab)
    {
        int maxFirst = Math.Max(0, total - visible);
        first = Math.Clamp(first, 0, maxFirst);
        float w = Px(16), arrow = Px(16);
        var dl = ImGuiNET.ImGui.GetWindowDrawList();

        // The chain, tiled (32 px a link pattern).
        if (RetailSprites.TryGet(Art.Track, out IntPtr track))
        {
            float tile = Px(32);
            for (float y = arrow; y < h - arrow; y += tile)
            {
                float th = Math.Min(tile, h - arrow - y);
                dl.AddImage(track, new Vector2(pos.X, pos.Y + y), new Vector2(pos.X + w, pos.Y + y + th),
                    Vector2.Zero, new Vector2(1, th / tile));
            }
        }
        else dl.AddRectFilled(pos, pos + new Vector2(w, h), C(0xFF2A2214));

        ImGuiNET.ImGui.PushID(id);
        // Arrows.
        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        if (ImGuiNET.ImGui.InvisibleButton("##up", new Vector2(w, arrow))) first = Math.Max(0, first - 1);
        uint upState = ImGuiNET.ImGui.IsItemActive() ? 2u : ImGuiNET.ImGui.IsItemHovered() ? 1u : 0u;
        if (!Img(dl, Art.ArrowUp + upState, pos, pos + new Vector2(w, arrow)))
            dl.AddTriangleFilled(pos + new Vector2(w * 0.5f, Px(4)), pos + new Vector2(Px(3), arrow - Px(4)), pos + new Vector2(w - Px(3), arrow - Px(4)), Gold);
        var dn = new Vector2(pos.X, pos.Y + h - arrow);
        ImGuiNET.ImGui.SetCursorScreenPos(dn);
        if (ImGuiNET.ImGui.InvisibleButton("##down", new Vector2(w, arrow))) first = Math.Min(maxFirst, first + 1);
        uint dnState = ImGuiNET.ImGui.IsItemActive() ? 2u : ImGuiNET.ImGui.IsItemHovered() ? 1u : 0u;
        if (!Img(dl, Art.ArrowDown + dnState, dn, dn + new Vector2(w, arrow)))
            dl.AddTriangleFilled(dn + new Vector2(Px(3), Px(4)), dn + new Vector2(w - Px(3), Px(4)), dn + new Vector2(w * 0.5f, arrow - Px(4)), Gold);

        // Thumb: its height is the visible share of the track, at least 24 px.
        float trackY = pos.Y + arrow, trackH = h - 2 * arrow;
        if (maxFirst > 0 && trackH > Px(24))
        {
            float thumbH = Math.Max(Px(24), trackH * visible / Math.Max(1, total));
            float travel = trackH - thumbH;
            float ty = trackY + travel * first / maxFirst;
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(pos.X, trackY));
            ImGuiNET.ImGui.InvisibleButton("##track", new Vector2(w, trackH));
            if (ImGuiNET.ImGui.IsItemActivated())
            {
                float my = ImGuiNET.ImGui.GetMousePos().Y;
                if (my >= ty && my <= ty + thumbH) grab = my - ty;
                else { grab = -1; first = Math.Clamp(first + (my < ty ? -visible : visible), 0, maxFirst); }
            }
            if (ImGuiNET.ImGui.IsItemActive() && grab >= 0 && travel > 0)
            {
                float y = ImGuiNET.ImGui.GetMousePos().Y - grab - trackY;
                first = (int)MathF.Round(Math.Clamp(y / travel, 0f, 1f) * maxFirst);
                ty = trackY + travel * first / maxFirst;
            }
            var t0 = new Vector2(pos.X, ty);
            float cap = Px(3);
            if (!(Img(dl, Art.ThumbTop, t0, t0 + new Vector2(w, cap))
                  & Img(dl, Art.ThumbMid, t0 + new Vector2(0, cap), t0 + new Vector2(w, thumbH - cap))
                  & Img(dl, Art.ThumbBottom, t0 + new Vector2(0, thumbH - cap), t0 + new Vector2(w, thumbH))))
                dl.AddRectFilled(t0, t0 + new Vector2(w, thumbH), C(0xFF203C8C), Px(2));
        }
        ImGuiNET.ImGui.PopID();
        return first;
    }

    /// <summary>Mouse wheel over min..max scrolls the list (one row a notch).</summary>
    public static int Wheel(Vector2 min, Vector2 max, int first, int total, int visible)
    {
        if (!ImGuiNET.ImGui.IsWindowHovered() || !ImGuiNET.ImGui.IsMouseHoveringRect(min, max)) return first;
        float wheel = ImGuiNET.ImGui.GetIO().MouseWheel;
        if (wheel == 0) return first;
        return Math.Clamp(first - (int)MathF.Round(wheel * 3), 0, Math.Max(0, total - visible));
    }

    // ── Popup list (one per face) ───────────────────────────────────────

    /// <summary>
    /// A retail popup list under a drop-down: Open from the drop-down's click, Draw once a frame at the
    /// end of the face's Draw. Rows are the retail list rows; the picked one is the lit row.
    /// </summary>
    internal sealed class Popup
    {
        private readonly string _id;
        private string[] _items = Array.Empty<string>();
        private int _selected = -1, _first;
        private Action<int>? _onPick;
        private Vector2 _pos;
        private float _w;
        private bool _openRequested;
        private float _grab;

        public Popup(string id) => _id = id;

        public bool IsOpen => ImGuiNET.ImGui.IsPopupOpen(_id);

        public void Open(Vector2 below, float w, string[] items, int selected, Action<int> onPick)
        {
            _items = items;
            _selected = selected;
            _onPick = onPick;
            _pos = below;
            _w = w;
            _first = Math.Max(0, selected - 4);
            _openRequested = items.Length > 0;
        }

        public void Draw()
        {
            if (_openRequested)
            {
                _openRequested = false;
                ImGuiNET.ImGui.SetNextWindowPos(_pos);
                ImGuiNET.ImGui.OpenPopup(_id);
            }
            const int MaxRows = 12;
            float rowH = Px(17);
            int rows = Math.Min(MaxRows, Math.Max(1, _items.Length));
            ImGuiNET.ImGui.SetNextWindowSize(new Vector2(_w, rows * rowH + Px(4)));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, C(0xF2080808));
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, Gold);
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Px(2), Px(2)));
            ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 0f);
            bool open = ImGuiNET.ImGui.BeginPopup(_id, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
            ImGuiNET.ImGui.PopStyleVar(2);
            ImGuiNET.ImGui.PopStyleColor(2);
            if (!open) return;
            try
            {
                var dl = ImGuiNET.ImGui.GetWindowDrawList();
                Vector2 o = ImGuiNET.ImGui.GetCursorScreenPos();
                bool scroll = _items.Length > MaxRows;
                float listW = _w - Px(4) - (scroll ? Px(16) : 0);
                _first = Wheel(o, o + new Vector2(_w, rows * rowH), _first, _items.Length, rows);
                for (int r = 0; r < rows && _first + r < _items.Length; r++)
                {
                    int i = _first + r;
                    var p = new Vector2(o.X, o.Y + r * rowH);
                    ImGuiNET.ImGui.SetCursorScreenPos(p);
                    ImGuiNET.ImGui.PushID(i);
                    bool clicked = ImGuiNET.ImGui.InvisibleButton("##row", new Vector2(listW, rowH));
                    bool hot = ImGuiNET.ImGui.IsItemHovered();
                    ImGuiNET.ImGui.PopID();
                    if (i == _selected || hot)
                    {
                        if (!HSlice(dl, i == _selected ? Art.ListPick : Art.ListBack, p, p + new Vector2(listW, rowH), 191, 20, 20))
                            dl.AddRectFilled(p, p + new Vector2(listW, rowH), C(0xFF6A3A18));
                    }
                    TextClip(dl, p.X + Px(i == _selected ? 22 : 6), p.Y, rowH, p.X + listW - Px(4), _items[i], i == _selected ? White : Text, 10);
                    if (clicked)
                    {
                        _onPick?.Invoke(i);
                        ImGuiNET.ImGui.CloseCurrentPopup();
                    }
                }
                if (scroll)
                    _first = Scrollbar("##popsb", new Vector2(o.X + listW, o.Y), rows * rowH, _items.Length, rows, _first, ref _grab);
            }
            finally
            {
                ImGuiNET.ImGui.EndPopup();
            }
        }
    }
}
