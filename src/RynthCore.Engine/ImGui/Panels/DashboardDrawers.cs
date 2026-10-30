// ============================================================================
//  RynthCore.Engine - ImGui/Panels/DashboardDrawers.cs
//  The RynthAi dashboard's slide-out drawers (Tom 2026-10-05: drawers on the
//  dashboard's edges; only the vitals / target bars stay at its bottom). One
//  tab per drawer, stacked down the dashboard's left or right edge; a click
//  slides that drawer's panel out on its tab's side. One drawer is open per
//  side, so a left and a right drawer can be out together.
//
//  Sides (2026-10-14): each drawer starts on a side (StartsRight) and a
//  dashboard drawer can be moved to the other one from its tab's right-click
//  (saved with the dashboard's state). The tabs never switch sides on their
//  own any more; a panel with no room on its side stays on the screen over
//  the dashboard instead. A drawer can opt in to flipping (FlipsWhenNoRoom,
//  2026-10-15: RynthChat's Channels): with no room on its own side for its
//  panel and room on the other, it draws there until its side has room again.
//
//  The look: a closed tab is a button with its drawer's icon that peeks out a
//  little on hover; the open tab joins its panel (same background, a teal
//  edge, an accent bar against the dashboard), and the panel has a teal
//  outline. Panels slide out with an ease-out.
//
//  The mechanics live here (tabs, slide, side, placement, the windows); each
//  drawer only draws its panel (DashboardDrawer.DrawPanel) and says how big it
//  is. Drawers today: Ranges (RangesSlideOut.cs), Loaded files, Patrol, Mini
//  Remote (RynthAiFace.Drawers.cs) and My settings (QuickSettingsDrawer.cs);
//  RynthChat's Channels (ChatFace.cs) uses the same host.
//
//  One top-level ImGui window per side, begun from inside the dashboard's Draw
//  and placed against the dashboard's window every frame, so the drawers
//  follow the dashboard (and go away with it). Each drawer's open/closed is
//  saved with the dashboard's state (RynthAiDashboardState). Popped out the
//  tabs are hidden: a popped-out dashboard is its own small window and the
//  area around it lets clicks through to the game, so a drawer beside it
//  couldn't be used (the dashboard falls back to its inline files block and
//  patrol popup).
//
//  AC thread only (inside the dashboard's Draw).
// ============================================================================

using System;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.UI;

namespace RynthCore.Engine.ImGuiBackend.Panels;

/// <summary>One drawer on the dashboard's edge: its tab's look and its panel.</summary>
internal abstract class DashboardDrawer
{
    /// <summary>Set by the host: close this drawer from inside the panel (its close button).</summary>
    public DashboardDrawers? Host { get; set; }

    /// <summary>Unique among the drawers ("##drawer_tab_" + Key is the tab's id).</summary>
    public abstract string Key { get; }
    /// <summary>Phosphor glyph on the tab.</summary>
    public abstract string Icon { get; }
    /// <summary>Lower-case name for "Hide the …" tooltips.</summary>
    public abstract string Name { get; }
    /// <summary>The closed tab's tooltip; <paramref name="right"/>: the drawer is on the right.</summary>
    public abstract string TabTooltip(bool right);

    /// <summary>Panel size in pixels at text scale <paramref name="k"/>.</summary>
    public abstract float Width(float k);
    public abstract float Height(float k);

    /// <summary>Every frame before drawing: <paramref name="showing"/> while any of the panel is out (subscriptions, data).</summary>
    public virtual void Update(bool showing) { }

    /// <summary>Draws the whole panel at <paramref name="origin"/> (the host clips it to the part slid out).</summary>
    public abstract void DrawPanel(Vector2 origin, float width, float height, float k);

    /// <summary>The saved open/closed (RynthAiDashboardState).</summary>
    public abstract bool SavedOpen { get; }
    public abstract void SaveOpen(bool open);

    /// <summary>The side the drawer starts on (false: left).</summary>
    public virtual bool StartsRight => false;
    /// <summary>The tab's right-click can move it to the other side (and the move is saved).</summary>
    public virtual bool CanMove => false;
    /// <summary>Moved to the side it doesn't start on.</summary>
    public virtual bool Moved => false;
    public virtual void SaveMoved(bool moved) { }

    /// <summary>
    /// With no room on its side for the panel (the window against the screen's edge) and room
    /// on the other, the drawer draws on the other side until its own has room again. Not saved.
    /// </summary>
    public virtual bool FlipsWhenNoRoom => false;
    /// <summary>Set by the host while a <see cref="FlipsWhenNoRoom"/> drawer is on its other side.</summary>
    internal bool Flipped;

    /// <summary>The side it is on now.</summary>
    public bool OnRight => (StartsRight != Moved) != Flipped;

    private string? _tabId, _hideTip, _sideMenuId;
    /// <summary>The tab's ImGui id and open tooltip (built once).</summary>
    internal string TabId => _tabId ??= "##drawer_tab_" + Key;
    internal string HideTip => _hideTip ??= "Hide the " + Name + ".";
    internal string SideMenuId => _sideMenuId ??= "##drawer_side_" + Key;

    protected void Close() => Host?.Close(this);

    protected static float CalcWidth(ImFontPtr font, string text)
    {
        ImGuiNET.ImGui.PushFont(font);
        float w = ImGuiNET.ImGui.CalcTextSize(text).X;
        ImGuiNET.ImGui.PopFont();
        return w;
    }

    // ── The shared panel frame: background, title row (icon + TITLE + note | close) ──

    protected static uint DTeal => RynthTheme.Accent;
    protected static uint DMute => RynthTheme.TextMuted;
    protected static uint DWhite => RynthTheme.Text;
    protected static uint DBtnFill => RynthTheme.Button;
    protected static uint DBtnBord => RynthTheme.Border;
    protected const float Pad = 6, TitleH = 18;

    /// <summary>Paints the panel's background and title row; returns the y under the title (pixels).</summary>
    protected float PanelFrame(Vector2 origin, float panelW, float panelH, float k, string icon, string title, string? note, string closeTip)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        dl.AddRectFilled(origin, origin + new Vector2(panelW, panelH), RynthTheme.RynthAiBackground, RynthTheme.RynthAiRounding);
        dl.AddRect(origin, origin + new Vector2(panelW, panelH), DTeal, RynthTheme.RynthAiRounding);

        float x = origin.X + Pad * k, w = panelW - 2 * Pad * k, y = origin.Y + Pad * k;
        ImFontPtr f11 = ImGuiFonts.Get(UiFont.Dash11), f9 = ImGuiFonts.Get(UiFont.Dash9);
        float th = TitleH * k;
        float iconW = CalcWidth(f11, icon);
        dl.AddText(f11, f11.FontSize, new Vector2(x, y + (th - f11.FontSize) * 0.5f), DTeal, icon);
        dl.AddText(f11, f11.FontSize, new Vector2(x + iconW + 4 * k, y + (th - f11.FontSize) * 0.5f), DWhite, title);
        if (note != null)
        {
            float tw = CalcWidth(f11, title);
            dl.AddText(f9, f9.FontSize, new Vector2(x + iconW + 4 * k + tw + 4 * k, y + (th - f9.FontSize) * 0.5f + 1), DMute, note);
        }
        Vector2 closeSize = new(18 * k, 16 * k);
        if (FaceKit.IconButton("##drawer_close", PhosphorIcons.X, new Vector2(x + w - closeSize.X, y + (th - closeSize.Y) * 0.5f),
                closeSize, DMute, DBtnFill, border: DBtnBord, font: UiFont.Dash10))
            Close();
        SafeImGui.SetItemTooltip(closeTip);
        return y + th + 4 * k;
    }
}

/// <summary>A RynthAi dashboard drawer: movable to the other side, the side saved with the dashboard's state.</summary>
internal abstract class RynthAiDrawer : DashboardDrawer
{
    public override bool CanMove => true;
    public override bool Moved => RynthAiDashboardState.DrawerMoved(Key);
    public override void SaveMoved(bool moved) => RynthAiDashboardState.SetDrawerMoved(Key, moved);
}

/// <summary>The tabs and the open drawers beside the RynthAi dashboard (one open per side).</summary>
internal sealed class DashboardDrawers
{
    private readonly string[] _windowIds;   // left, right ("##rynthai_drawers" for the dashboard; the chat has its own)
    private const float SlideSeconds = 0.16f;

    private static uint Teal => RynthTheme.Accent;
    private static uint Mute => RynthTheme.TextMuted;
    private static uint White => RynthTheme.Text;
    private static uint BtnFill => RynthTheme.Button;
    private static uint BtnBord => RynthTheme.Border;
    private static uint ShellBg => RynthTheme.Background;

    private readonly DashboardDrawer[] _drawers;

    /// <summary>One edge's drawers: which is open, which draws, how far it has slid.</summary>
    private sealed class Side
    {
        public int Open = -1;    // the drawer open (or opening); -1 none
        public int Shown = -1;   // the drawer whose panel draws (a closing one until it has slid in)
        public float T;          // slide position of Shown, 0 closed .. 1 open
    }
    private readonly Side[] _sides = { new(), new() };
    private bool _loaded;

    public DashboardDrawers(params DashboardDrawer[] drawers) : this("##rynthai_drawers", drawers) { }

    /// <summary>A drawer set beside another panel (RynthChat's channels): its own window id.</summary>
    public DashboardDrawers(string windowId, params DashboardDrawer[] drawers)
    {
        _windowIds = new[] { windowId, windowId + "_right" };
        _drawers = drawers;
        foreach (DashboardDrawer d in drawers) d.Host = this;
    }

    private Side SideOf(DashboardDrawer d) => _sides[d.OnRight ? 1 : 0];

    public bool IsOpen(DashboardDrawer d)
    {
        Side s = SideOf(d);
        return s.Open >= 0 && _drawers[s.Open] == d;
    }

    /// <summary>Opens <paramref name="d"/> (closing the one open on its side), or closes it when it is open.</summary>
    public void Toggle(DashboardDrawer d)
    {
        if (IsOpen(d)) Close(d);
        else Open(d);
    }

    public void Open(DashboardDrawer d)
    {
        EnsureLoaded();
        int i = Array.IndexOf(_drawers, d);
        Side s = SideOf(d);
        if (i < 0 || i == s.Open) return;
        if (s.Open >= 0) _drawers[s.Open].SaveOpen(false);
        s.Open = i;
        d.SaveOpen(true);
        if (s.Shown != i)
        {
            // Another drawer was out on this side (or sliding in): this one slides out from closed.
            s.Shown = i;
            s.T = 0f;
        }
    }

    public void Close(DashboardDrawer d)
    {
        if (!IsOpen(d)) return;
        SideOf(d).Open = -1;
        d.SaveOpen(false);
    }

    /// <summary>Moves <paramref name="d"/> to the other side; an open drawer opens again there.</summary>
    public void Move(DashboardDrawer d)
    {
        if (!d.CanMove) return;
        EnsureLoaded();
        bool wasOpen = IsOpen(d);
        int i = Array.IndexOf(_drawers, d);
        Side from = SideOf(d);
        if (wasOpen) from.Open = -1;
        if (from.Shown == i) { from.Shown = -1; from.T = 0f; }
        d.Update(false);
        d.SaveMoved(!d.Moved);
        if (wasOpen) { d.SaveOpen(false); Open(d); }
    }

    /// <summary>The dashboard closed: every drawer drops its subscriptions. AC thread.</summary>
    public void Detach()
    {
        foreach (DashboardDrawer d in _drawers) d.Update(false);
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        // The first drawer saved open on each side wins (one per side); any other saved open is closed.
        for (int i = 0; i < _drawers.Length; i++)
        {
            if (!_drawers[i].SavedOpen) continue;
            Side s = SideOf(_drawers[i]);
            if (s.Open < 0) { s.Open = i; s.Shown = i; s.T = 1f; }
            else _drawers[i].SaveOpen(false);
        }
    }

    /// <summary>
    /// Draws the tabs and the open drawers. Call at the end of the dashboard's Draw, inside its
    /// body child (whose rect is the dashboard's: the face is chromeless).
    /// </summary>
    public void Draw()
    {
        if (ImGuiPopOuts.InPopOutFrame)
        {
            // Popped out: no tabs (see the header). Nothing polled meanwhile.
            Detach();
            return;
        }
        EnsureLoaded();

        Vector2 dashPos = ImGuiNET.ImGui.GetWindowPos(), dashSize = ImGuiNET.ImGui.GetWindowSize();
        float k = MathF.Max(1f, ImGuiFonts.Get(UiFont.Dash11).FontSize / 11f);
        UpdateFlips(dashPos, dashSize, k);
        DrawSide(0, dashPos, dashSize, k);
        DrawSide(1, dashPos, dashSize, k);
    }

    /// <summary>
    /// Puts each <see cref="DashboardDrawer.FlipsWhenNoRoom"/> drawer on the side its whole panel
    /// fits: its own side when it fits there, else the other side when that fits, else its own.
    /// </summary>
    private void UpdateFlips(Vector2 dashPos, Vector2 dashSize, float k)
    {
        float displayW = ImGuiNET.ImGui.GetIO().DisplaySize.X;
        if (displayW <= 1) return;
        float tabW = 17 * k;
        foreach (DashboardDrawer d in _drawers)
        {
            if (!d.FlipsWhenNoRoom) continue;
            bool homeRight = d.StartsRight != d.Moved;
            float need = tabW + d.Width(k);
            bool fitsLeft = dashPos.X - need >= 0;
            bool fitsRight = dashPos.X + dashSize.X + need <= displayW;
            bool fitsHome = homeRight ? fitsRight : fitsLeft;
            bool fitsOther = homeRight ? fitsLeft : fitsRight;
            SetFlipped(d, !fitsHome && fitsOther);
        }
    }

    /// <summary>Moves a drawer to its flipped side or back; an open drawer stays open (no slide).</summary>
    private void SetFlipped(DashboardDrawer d, bool flipped)
    {
        if (d.Flipped == flipped) return;
        int i = Array.IndexOf(_drawers, d);
        Side from = SideOf(d);
        bool wasOpen = from.Open == i;
        if (wasOpen) from.Open = -1;
        if (from.Shown == i) { from.Shown = -1; from.T = 0f; }
        d.Flipped = flipped;
        if (!wasOpen) return;
        Side to = SideOf(d);
        if (to.Open >= 0 && to.Open != i) _drawers[to.Open].SaveOpen(false);   // one open per side
        to.Open = i;
        to.Shown = i;
        to.T = 1f;
    }

    private void DrawSide(int sideIdx, Vector2 dashPos, Vector2 dashSize, float k)
    {
        bool right = sideIdx == 1;
        Side side = _sides[sideIdx];
        int n = 0;
        foreach (DashboardDrawer d in _drawers) if (d.OnRight == right) n++;
        if (n == 0) { side.Open = side.Shown = -1; return; }

        // Slide.
        float target = side.Open >= 0 && side.Open == side.Shown ? 1f : 0f;
        if (side.T != target)
        {
            float step = ImGuiNET.ImGui.GetIO().DeltaTime / SlideSeconds;
            if (step <= 0 || float.IsNaN(step)) step = 1f;
            side.T = target > side.T ? MathF.Min(1f, side.T + step) : MathF.Max(0f, side.T - step);
        }
        if (side.T <= 0f && side.Open < 0) side.Shown = -1;
        float u = 1f - side.T;
        float ease = 1f - u * u * u;   // ease-out: quick to start, settling at the end
        bool showing = side.Shown >= 0 && ease > 0.001f;
        for (int i = 0; i < _drawers.Length; i++)
            if (_drawers[i].OnRight == right) _drawers[i].Update(showing && i == side.Shown);

        // Geometry (pixels; k follows the dashboard's text size).
        Vector2 display = ImGuiNET.ImGui.GetIO().DisplaySize;
        float tabW = 17 * k, tabGap = 3 * k, tabTop = 4 * k, peek = 3 * k;
        // Tabs share the dashboard's height (40 tall at most, 20 at least: they may run past a minimized dashboard).
        float tabH = MathF.Max(20 * k, MathF.Min(40 * k, (dashSize.Y - 2 * tabTop - (n - 1) * tabGap) / n));
        float tabsH = n * tabH + (n - 1) * tabGap;
        float panelW = showing ? _drawers[side.Shown].Width(k) : 0;
        float panelH = showing ? _drawers[side.Shown].Height(k) : 0;
        float shown = showing ? panelW * ease : 0;
        float outer = MathF.Max(shown, peek);   // room beyond the tabs: the panel, or a hovered tab's peek

        float dashRight = dashPos.X + dashSize.X;
        float tabX = right ? dashRight : dashPos.X - tabW;
        float winX = right ? tabX : tabX - outer;
        float winW = tabW + outer;
        // No room on this side (the dashboard against the screen's edge): stay on the screen, over the dashboard.
        float shift = 0;
        if (winX < 0) shift = -winX;
        else if (display.X > 1 && winX + winW > display.X) shift = display.X - (winX + winW);
        winX += shift;
        tabX += shift;

        float winY = dashPos.Y;
        float tabsBottom = tabTop + tabsH;   // relative to the dashboard's top
        float winH = showing ? MathF.Max(panelH, tabsBottom) : tabsBottom;
        if (display.Y > 1 && winY + winH > display.Y) winY = MathF.Max(0, display.Y - winH);
        float tabY = dashPos.Y + tabTop;
        winH = MathF.Max(winH, tabY + tabsH - winY);

        ImGuiNET.ImGui.SetNextWindowPos(new Vector2(winX, winY), ImGuiCond.Always);
        ImGuiNET.ImGui.SetNextWindowSize(new Vector2(winW, winH), ImGuiCond.Always);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        bool visible = ImGuiNET.ImGui.Begin(_windowIds[sideIdx], WindowFlags);
        ImGuiNET.ImGui.PopStyleVar(4);
        try
        {
            if (!visible) return;
            ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Dash10));
            try
            {
                if (showing)
                {
                    // The panel's visible part; its content slides with the open edge.
                    float visX0 = right ? tabX + tabW : tabX - shown;
                    float contentX = right ? tabX + tabW + shown - panelW : tabX - shown;
                    Vector2 vMin = new(visX0, winY), vMax = new(visX0 + shown, winY + panelH);
                    DashboardDrawer d = _drawers[side.Shown];
                    ImGuiNET.ImGui.PushClipRect(vMin, vMax, true);
                    ImGuiNET.ImGui.PushID(d.Key);
                    try { d.DrawPanel(new Vector2(contentX, winY), panelW, panelH, k); }
                    finally
                    {
                        ImGuiNET.ImGui.PopID();
                        ImGuiNET.ImGui.PopClipRect();
                    }
                }
                int slot = 0;
                for (int i = 0; i < _drawers.Length; i++)
                {
                    if (_drawers[i].OnRight != right) continue;
                    DrawTab(_drawers[i], new Vector2(tabX, tabY + slot * (tabH + tabGap)), new Vector2(tabW, tabH), right,
                        joined: showing && i == side.Shown && side.Open == i, canPeek: !showing, k);
                    slot++;
                }
            }
            finally { ImGuiNET.ImGui.PopFont(); }
        }
        finally
        {
            ImGuiNET.ImGui.End();
        }
    }

    private const ImGuiWindowFlags WindowFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
        | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
        | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking
        | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav;

    /// <param name="joined">The open drawer's tab, drawn as part of its panel.</param>
    /// <param name="canPeek">No panel is out on this side, so a hovered tab may peek out.</param>
    private void DrawTab(DashboardDrawer d, Vector2 pos, Vector2 size, bool right, bool joined, bool canPeek, float k)
    {
        bool open = IsOpen(d);
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGuiNET.ImGui.InvisibleButton(d.TabId, size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight)
                       && ImGuiNET.ImGui.IsMouseReleased(ImGuiMouseButton.Left);
        bool hot = ImGuiNET.ImGui.IsItemHovered();
        bool rightClicked = d.CanMove && ImGuiNET.ImGui.IsItemClicked(ImGuiMouseButton.Right);
        // Like SetItemTooltip (same delay), but the closed tooltip is only built while it shows.
        if (ImGuiNET.ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip))
            SafeImGui.SetTooltip(open ? d.HideTip : d.TabTooltip(right) + (d.CanMove ? MoveHint : ""));
        if (clicked) Toggle(d);
        if (rightClicked) ImGuiNET.ImGui.OpenPopup(d.SideMenuId);

        ImFontPtr f10 = ImGuiFonts.Get(UiFont.Dash10);
        float edge = 2 * k;   // the accent bar against the dashboard
        if (joined)
        {
            // Part of the panel: its background, reaching over the panel's edge so there's no seam,
            // a teal outline on the three other sides and an accent bar against the dashboard.
            float over = 1.5f * k;
            Vector2 a = right ? pos : pos - new Vector2(over, 0), b = right ? pos + size + new Vector2(over, 0) : pos + size;
            dl.AddRectFilled(a, b, RynthTheme.RynthAiBackground);
            float inner = right ? pos.X : pos.X + size.X;
            float outerX = right ? pos.X + size.X + over : pos.X - over;
            dl.AddLine(new Vector2(inner, pos.Y), new Vector2(outerX, pos.Y), Teal);
            dl.AddLine(new Vector2(inner, pos.Y + size.Y - 1), new Vector2(outerX, pos.Y + size.Y - 1), Teal);
            dl.AddRectFilled(new Vector2(right ? inner : inner - edge, pos.Y), new Vector2(right ? inner + edge : inner, pos.Y + size.Y), Teal);
            PhosphorIcons.DrawCentered(dl, f10, d.Icon, pos, size, Teal);
        }
        else
        {
            // A hovered tab peeks out a little, as if it pulls.
            float peek = canPeek && hot ? 2 * k : 0;
            Vector2 p = pos + new Vector2(right ? peek : -peek, 0);
            uint fill = ImGuiNET.ImGui.IsItemActive() ? FaceKit.Darken(BtnFill) : hot ? FaceKit.Lighten(BtnFill) : BtnFill;
            ImDrawFlags corners = right ? ImDrawFlags.RoundCornersRight : ImDrawFlags.RoundCornersLeft;
            dl.AddRectFilled(p, p + size, fill, 4 * k, corners);
            dl.AddRect(p, p + size, open ? Teal : BtnBord, 4 * k, corners);
            PhosphorIcons.DrawCentered(dl, f10, d.Icon, p, size, open ? Teal : hot ? White : Mute);
        }

        // Right-click: move the drawer to the other side.
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, ShellBg);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, BtnBord);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8 * k, 6 * k));
        bool menu = ImGuiNET.ImGui.BeginPopup(d.SideMenuId);
        ImGuiNET.ImGui.PopStyleVar();
        ImGuiNET.ImGui.PopStyleColor(2);
        if (!menu) return;
        try
        {
            if (ImGuiNET.ImGui.MenuItem(right ? PhosphorIcons.CaretLeft + "  Move to the left side" : PhosphorIcons.CaretRight + "  Move to the right side"))
                Move(d);
        }
        finally { ImGuiNET.ImGui.EndPopup(); }
    }

    private const string MoveHint = "\nRight-click: move it to the other side of the dashboard.";
}
