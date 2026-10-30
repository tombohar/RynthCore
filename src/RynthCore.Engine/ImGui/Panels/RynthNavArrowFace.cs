// ============================================================================
//  RynthCore.Engine - ImGui/Panels/RynthNavArrowFace.cs
//  RynthNav's arrow: a small draggable overlay that points at RynthNav's arrow
//  target relative to the way you face, with the distance, the compass point
//  and the target's name (our take on GoArrow's arrow; Digero 2006, Virindi
//  2011, MIT). Shown and hidden from the RynthNav panel's Arrow tab; opens by
//  itself when a command sets the arrow (RynthNavArrowWatch). Works popped out.
//
//    arrow      turns with you every frame (the pose snapshot's heading); green
//               within 30 yd, amber while it points at a portal on a route; a
//               wand instead of the arrow when the next step is a recall.
//    text       name, distance and compass point, the route step (n/m).
//    row        a box for coordinates or a place (Plan: route only; Enter or Go: RynthNav goes
//               there), Plan, Go, Stop, and the caret: the route's steps open
//               under it, current one green (the window fits its height).
//    corner     small pop out and hide buttons. No resize grip: it sizes itself.
//
//  The target comes from RynthNav's status (UiSources.RynthNav); RynthNav owns
//  it and decides arrival. AC's render thread: snapshots only, no AC calls, and
//  strings are rebuilt only when what they show changes.
// ============================================================================

using System;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.UI;
using RynthCore.Engine.UI.Data;
using static RynthCore.Engine.ImGuiBackend.Panels.FaceKit;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed class RynthNavArrowFace : IImGuiPanel
{
    public const string Title = "RynthNav Arrow";

    // Sized to its content (FitHeight): these are only the first-open size and the narrowest it goes.
    public static void Register() => ImGuiPanelHost.Register(Title,
        new PanelSpec(new Vector2(232, 68), new Vector2(232, 60), PanelChrome.None, Background: 0, Rounding: 6, EdgeToEdge: true,
            NoResize: true),
        () => new RynthNavArrowFace());

    /// <summary>Shows the overlay (any thread). Only where ImGui faces are drawn.</summary>
    public static void Show()
    {
        if (!ImGuiPanelHost.IsOpen(Title) && PanelRouter.ResolveDockedFace(Title) == PanelFace.ImGui)
            ImGuiPanelHost.Open(Title);
    }

    /// <summary>The arrow turns with you: redraw popped out often enough to look smooth.</summary>
    public int PopOutIdleHz => 20;

    private static readonly uint Ring = RynthTheme.Argb(0xFF1A2A39), InputBg = RynthTheme.Argb(0xFF0E1822),
        Purple = RynthTheme.Argb(0xFFB48CF0), Done = Faded(Mute);
    private static uint Glass => RynthTheme.WithAlpha(RynthTheme.Background, 0xDC);

    private const int MaxRouteRows = 8;

    private readonly ArrowReadout _read = new();
    // What to go to: coordinates ("42.1N 33.6E", "42.1, 33.6") or a place the Atlas knows.
    private readonly byte[] _target = new byte[128];
    private bool _showRoute;
    // Plan was pressed: open the steps when the planned route arrives (a new Route array).
    private RynthNavRouteStep[]? _openWhenNot;
    private int _scrolledTo = -1;
    private float _requestedH = -1;

    public void OnShown()
    {
        UiSources.RynthNav.Subscribe();
        UiSources.RynthNav.RequestRefresh();
        _showRoute = false;
        _requestedH = -1;
    }

    public void OnHidden() => UiSources.RynthNav.Unsubscribe();

    public void Draw()
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        Vector2 origin = ImGuiNET.ImGui.GetCursorScreenPos();
        Vector2 size = ImGuiNET.ImGui.GetContentRegionAvail();
        if (size.X < 40 || size.Y < 30) return;
        RynthNavStatus? st = UiSources.RynthNav.Current?.Value;
        _read.Update(st);

        ImFontPtr bold = ImGuiFonts.Get(UiFont.UiBold11), f10 = ImGuiFonts.Get(UiFont.Ui10), f9 = ImGuiFonts.Get(UiFont.Ui9);
        const float pad = 5, gap = 3;
        float b = MathF.Round(MathF.Max(20, f10.FontSize + 8));        // buttons and the input row
        float d = MathF.Round(bold.FontSize + f10.FontSize + 14);       // the arrow's ring
        float rowY = origin.Y + pad + d + 4;
        bool hasRoute = st != null && st.Route.Length > 0 && st.RouteLines.Length > 0;
        if (_openWhenNot != null && hasRoute && !ReferenceEquals(st!.Route, _openWhenNot)) { _showRoute = true; _scrolledTo = -1; _openWhenNot = null; }
        if (!hasRoute) _showRoute = false;
        float lineH = f10.FontSize + 3;
        int rows = hasRoute ? Math.Min(st!.Route.Length, MaxRouteRows) : 0;
        float listH = _showRoute ? MathF.Round(bold.FontSize + 4 + rows * lineH + 6) : 0;
        float wantH = pad + d + 4 + b + pad + (_showRoute ? 4 + listH : 0);
        FitHeight(wantH, size.Y);

        dl.AddRectFilled(origin, origin + size, Glass, 6);
        dl.AddRect(origin, origin + size, BtnBord, 6);

        // The arrow, in a ring on the left.
        Vector2 c = origin + new Vector2(pad + d * 0.5f, pad + d * 0.5f);
        dl.AddCircleFilled(c, d * 0.5f, ShellBg, 32);
        dl.AddCircle(c, d * 0.5f, Ring, 32, 1.5f);
        _read.DrawPointer(dl, c, d * 0.5f - 5);

        // Name, then distance and compass point (the route step on the right of it).
        float tx = origin.X + pad + d + 8, right = origin.X + size.X - pad;
        const float sb = 14;   // the corner buttons: small, for a small panel
        dl.PushClipRect(new Vector2(tx, origin.Y), new Vector2(right - 2 * sb - 4, origin.Y + size.Y), true);
        dl.AddText(bold, bold.FontSize, new Vector2(tx, origin.Y + pad + 2), Teal, _read.Line1);
        dl.PopClipRect();
        string step = _read.HasTarget && st?.Arrow is { Steps: > 1 } sa ? $"{sa.Step}/{sa.Steps}" : "";
        float stepW = step.Length > 0 ? f9.CalcTextSizeA(f9.FontSize, float.MaxValue, 0, step).X + 4 : 0;
        float line2Y = origin.Y + pad + 2 + bold.FontSize + 4;
        dl.PushClipRect(new Vector2(tx, origin.Y), new Vector2(right - stepW, origin.Y + size.Y), true);
        if (_read.HasTarget) dl.AddText(f10, f10.FontSize, new Vector2(tx, line2Y), Text, _read.Line2);
        else dl.AddText(f9, f9.FontSize, new Vector2(tx, line2Y + 1), Mute, _read.Line3.Length > 0 ? "Type coordinates or a place below" : "");
        dl.PopClipRect();
        if (step.Length > 0) dl.AddText(f9, f9.FontSize, new Vector2(right - stepW + 4, line2Y + 1), Mute, step);

        // Top-right corner: pop out, hide (small and flat).
        var closePos = new Vector2(origin.X + size.X - 3 - sb, origin.Y + 3);
        var popPos = closePos - new Vector2(sb + 2, 0);
        if (PanelRouter.CanPopOut)
        {
            if (IconButton("##rna_pop", ImGuiPanelHost.PopOutGlyph, popPos, new Vector2(sb, sb), Done, Glass, font: UiFont.Ui9))
                ImGuiPanelHost.TogglePopOut(Title);
            SafeImGui.SetItemTooltip(ImGuiPanelHost.PopOutTooltip);
        }
        if (IconButton("##rna_close", PhosphorIcons.X, closePos, new Vector2(sb, sb), Done, Glass, font: UiFont.Ui9))
            ImGuiPanelHost.Close(Title);
        ImGuiNET.ImGui.SetItemTooltip("Hide the arrow (the RynthNav panel's Arrow tab shows it again)");

        // Bottom row: where to go, Plan, Go, Stop, the route's steps.
        var routePos = new Vector2(right - b, rowY);
        var stopPos = routePos - new Vector2(b + gap, 0);
        var goPos = stopPos - new Vector2(b + gap, 0);
        var planPos = goPos - new Vector2(b + gap, 0);
        float inputX = origin.X + pad, inputW = planPos.X - gap - inputX;
        string typed = TypedTarget();
        bool enter = TargetInput(inputX, rowY, inputW, b, f10);
        bool canGo = typed.Length > 0 || _read.CanGo;
        if (IconButton("##rna_plan", PhosphorIcons.Path, planPos, new Vector2(b, b), Teal, BtnFill, enabled: canGo, font: UiFont.Ui11) && canGo)
        {
            _openWhenNot = st?.Route ?? Array.Empty<RynthNavRouteStep>();
            if (typed.Length > 0) RynthNavCommands.Command("route " + typed);
            else if (st?.Arrow is RynthNavArrow pa) RynthNavCommands.Route(pa.Final, pa.FinalNs, pa.FinalEw);
        }
        SafeImGui.SetItemTooltip(typed.Length > 0
            ? "Plan a route to " + typed + " without walking it (the steps open below)"
            : "Plan a route to the arrow's target without walking it (the steps open below)");
        bool go = IconButton("##rna_go", PhosphorIcons.Play, goPos, new Vector2(b, b), Green, BtnFill, enabled: canGo, font: UiFont.Ui11);
        SafeImGui.SetItemTooltip(typed.Length > 0
            ? "Go to " + typed + " (with portals and recalls when RynthNav's routing is on)"
            : "Go: RynthNav walks to the arrow's target (with portals and recalls when its routing is on)");
        if ((go || enter) && canGo)
        {
            if (typed.Length > 0) RynthNavCommands.Command("go " + typed);
            else if (st?.Arrow is RynthNavArrow ar) RynthNavCommands.Go(ar.Final, ar.FinalNs, ar.FinalEw);
        }
        if (IconButton("##rna_stop", PhosphorIcons.Stop, stopPos, new Vector2(b, b), Red, BtnFill, font: UiFont.Ui11))
            RynthNavCommands.Move(5);
        ImGuiNET.ImGui.SetItemTooltip("Stop walking");
        if (IconButton("##rna_route", _showRoute ? PhosphorIcons.CaretUp : PhosphorIcons.CaretDown, routePos, new Vector2(b, b), _showRoute ? Teal : Mute,
                _showRoute ? Selected : BtnFill, enabled: hasRoute, font: UiFont.Ui11))
        {
            _showRoute = !_showRoute;
            _scrolledTo = -1;
        }
        SafeImGui.SetItemTooltip(hasRoute ? (_showRoute ? "Hide the route's steps" : "Show the route's steps") : "No route yet: Plan or Go makes one");

        if (_showRoute && hasRoute)
            RouteList(st!, new Vector2(origin.X + pad, rowY + b + 4), new Vector2(size.X - 2 * pad, listH), bold, lineH);

        // Surface: drag moves the overlay (the OS window when popped out). The top part only:
        // the row below is the input box and buttons, the list scrolls.
        ImGuiNET.ImGui.SetCursorScreenPos(origin);
        ImGuiNET.ImGui.InvisibleButton("##rna_surface", new Vector2(size.X, rowY - origin.Y - 2));
        ImGuiPanelHost.DragWindowWithLastItem();
        if (ImGuiNET.ImGui.IsItemHovered() && _read.Tooltip.Length > 0)
            SafeImGui.SetItemTooltip(_read.Tooltip + (_read.Line3.Length > 0 && _read.Kind == "portal" ? "\n" + _read.Line3 : ""));
    }

    /// <summary>The coordinates or place typed in the box, trimmed.</summary>
    private string TypedTarget()
    {
        int n = Array.IndexOf(_target, (byte)0);
        return System.Text.Encoding.UTF8.GetString(_target, 0, n < 0 ? _target.Length : n).Trim();
    }

    /// <summary>The target box, with a grey hint while empty; true when Enter was pressed in it.</summary>
    private bool TargetInput(float x, float y, float w, float h, ImFontPtr font)
    {
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x, y));
        ImGuiNET.ImGui.SetNextItemWidth(w);
        ImGuiNET.ImGui.PushFont(font);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg, InputBg);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, BtnBord);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Text);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 3f);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(5, MathF.Max(0, (h - font.FontSize) * 0.5f)));
        bool enter = ImGuiNET.ImGui.InputText("##rna_target", _target, (uint)_target.Length, ImGuiInputTextFlags.EnterReturnsTrue);
        bool active = ImGuiNET.ImGui.IsItemActive();
        ImGuiNET.ImGui.SetItemTooltip("Coordinates (42.1N 33.6E) or a place the Atlas knows, then Enter or Go");
        ImGuiNET.ImGui.PopStyleVar(3);
        ImGuiNET.ImGui.PopStyleColor(3);
        if (_target[0] == 0 && !active)
            ImGuiNET.ImGui.GetWindowDrawList().AddText(font, font.FontSize, new Vector2(x + 6, y + (h - font.FontSize) * 0.5f), Done, "42.1N 33.6E or a place");
        ImGuiNET.ImGui.PopFont();
        return enter;
    }

    /// <summary>The route's steps under the row: the header, then one line each (done greyed, current green).</summary>
    private void RouteList(RynthNavStatus st, Vector2 pos, Vector2 listSize, ImFontPtr bold, float lineH)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        dl.AddRectFilled(pos, pos + listSize, ShellBg, 4);
        dl.PushClipRect(pos, pos + new Vector2(listSize.X, bold.FontSize + 4), true);
        dl.AddText(bold, bold.FontSize, pos + new Vector2(4, 2), Text, st.RouteHeader);
        dl.PopClipRect();
        float top = pos.Y + bold.FontSize + 4;
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(pos.X, top));
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 3));
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(4, 0));
        if (ImGuiNET.ImGui.BeginChild("##rna_steps", new Vector2(listSize.X, pos.Y + listSize.Y - top - 2), ImGuiChildFlags.None,
                ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.HorizontalScrollbar))
        {
            int n = Math.Min(st.Route.Length, st.RouteLines.Length);
            for (int i = 0; i < n; i++)
            {
                RynthNavRouteStep s = st.Route[i];
                bool current = st.RouteActive && i == st.RouteIdx, done = st.RouteActive && i < st.RouteIdx;
                uint col = current ? Green : done ? Done
                    : s.Kind == "recall" ? Amber : s.Kind == "portal" ? Purple : s.Kind == "town" ? Teal : Text;
                ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, col);
                ImGuiNET.ImGui.TextUnformatted(st.RouteLines[i]);
                ImGuiNET.ImGui.PopStyleColor();
                if (ImGuiNET.ImGui.IsItemHovered())
                    SafeImGui.SetTooltip(current && st.StepLeft.Length > 0 ? st.RouteLines[i] + "\n" + st.StepLeft : st.RouteLines[i]);
                if (current && _scrolledTo != i) { ImGuiNET.ImGui.SetScrollHereY(0.3f); _scrolledTo = i; }
            }
        }
        ImGuiNET.ImGui.EndChild();
        ImGuiNET.ImGui.PopStyleVar(2);
    }

    /// <summary>
    /// Fits the window's height to what it shows (the route list opens and closes it), docked.
    /// Popped out the OS window keeps its size, as other faces do (RynthAiFace).
    /// </summary>
    private void FitHeight(float wantPx, float havePx)
    {
        if (ImGuiPopOuts.InPopOutFrame) return;
        float place = ImGuiPanelHost.PlacementScale();
        float extra = ImGuiNET.ImGui.GetWindowHeight() - havePx;   // anything around the body
        float want = MathF.Ceiling((wantPx + extra) / place);
        if (MathF.Abs(havePx - wantPx) < 1.5f || MathF.Abs(want - _requestedH) < 0.5f) return;
        _requestedH = want;
        ImGuiPanelHost.RequestSize(Title, new Vector2(ImGuiNET.ImGui.GetWindowWidth() / place, want));
    }
}

/// <summary>
/// What the arrow shows this frame: the target from RynthNav's status, your position and
/// heading from the pose snapshot. Shared by the overlay and the panel's Arrow tab. AC's
/// render thread; the text is rebuilt only when what it shows changes.
/// </summary>
internal sealed class ArrowReadout
{
    public string Line1 = "No arrow", Line2 = "", Line3 = "", Tooltip = "";
    public bool HasTarget, CanGo, Pointing;
    public float Relative;            // degrees clockwise from straight ahead
    public double Distance = double.NaN;
    public string Kind = "";

    private RynthNavArrow? _arrow;
    private int _distKey = int.MinValue, _compassKey = -1;
    private bool _wasInDungeon, _wasAvailable = true;
    private string _compass = "";

    private static readonly uint NearCol = RynthTheme.Argb(0xFF40D973);

    public void Update(RynthNavStatus? st)
    {
        RynthNavArrow? a = st?.Arrow;
        bool available = st == null || st.Travel;
        if (!ReferenceEquals(a, _arrow) || available != _wasAvailable)
        {
            _arrow = a;
            _wasAvailable = available;
            _distKey = int.MinValue;
            _compassKey = -1;
            HasTarget = a != null;
            Kind = a?.Kind ?? "";
            CanGo = a != null && (a.FinalNs != 0 || a.FinalEw != 0);
            Line1 = a == null ? (available ? "No arrow" : "RynthNav is too old for the arrow") : a.Name;
            Line3 = a == null ? (available ? "Atlas, chat coords or /rnav arrow" : "")
                : a.Kind == "recall" ? (a.StepText.Length > 0 ? a.StepText : "cast the recall")
                : a.Kind == "portal" ? "walk into the portal" + (a.Steps > 1 ? $" · step {a.Step}/{a.Steps}" : "")
                : a.Steps > 1 ? $"step {a.Step}/{a.Steps}" : a.Type;
            Tooltip = a == null ? "" : (a.Kind == "recall" ? "Cast " + a.Name : a.Name + "  " + a.CoordText)
                + (a.Steps > 1 ? "\nOn the way to " + a.Final : "");
            Line2 = a == null ? "" : a.Kind == "recall" ? "Cast " + a.Name : a.CoordText;
        }
        Pointing = false;
        if (a == null || a.Kind == "recall" || !a.OnMap) return;
        if (!RynthNavAtlas.TryPlayerPos(out double ns, out double ew, out bool onMap, out _)) return;
        if (!onMap)
        {
            if (!_wasInDungeon) { Line2 = "in a dungeon: no direction"; _wasInDungeon = true; _distKey = int.MinValue; }
            return;
        }
        _wasInDungeon = false;
        Distance = RynthNavAtlas.Distance(ns, ew, a.Ns, a.Ew);
        double bearing = RynthNavAtlas.Bearing(ns, ew, a.Ns, a.Ew);
        int compassKey = (int)Math.Round(((bearing % 360.0) + 360.0) % 360.0 / 22.5) & 15;
        if (compassKey != _compassKey) { _compassKey = compassKey; _compass = RynthNavAtlas.CompassPoint(bearing); _distKey = int.MinValue; }
        int key = Distance < 1000 ? (int)Math.Round(Distance) : 100000 + (int)Math.Round(Distance / 100);
        if (key != _distKey)
        {
            _distKey = key;
            Line2 = RynthNavStatus.Yards(Distance) + "  " + _compass;
        }
        if (PlayerPhysicsHooks.TryGetPlayerCompassSnapshot(out float heading))
        {
            Relative = (float)bearing - heading;
            Pointing = true;
        }
    }

    /// <summary>The pointer at <paramref name="c"/>: an arrow, a wand for a recall, a dot when there's nothing.</summary>
    public void DrawPointer(ImDrawListPtr dl, Vector2 c, float r)
    {
        if (!HasTarget) { dl.AddCircleFilled(c, 3, FaceKit.Mute); return; }
        if (Kind == "recall")
        {
            PhosphorIcons.DrawCentered(dl, ImGuiFonts.Get(UiFont.Ui14), PhosphorIcons.MagicWand, c - new Vector2(r, r), new Vector2(2 * r, 2 * r), FaceKit.Amber);
            return;
        }
        if (!Pointing) { PhosphorIcons.DrawCentered(dl, ImGuiFonts.Get(UiFont.Ui14), PhosphorIcons.Compass, c - new Vector2(r, r), new Vector2(2 * r, 2 * r), FaceKit.Mute); return; }
        uint col = Kind == "portal" ? FaceKit.Amber : Distance <= 30 ? NearCol : FaceKit.Teal;
        float rad = Relative * (MathF.PI / 180f);
        float s = MathF.Sin(rad), co = MathF.Cos(rad);
        Vector2 tip = Rot(c, 0, -r, s, co), left = Rot(c, -0.62f * r, 0.78f * r, s, co),
            notch = Rot(c, 0, 0.36f * r, s, co), right = Rot(c, 0.62f * r, 0.78f * r, s, co);
        dl.AddTriangleFilled(tip, left, notch, col);
        dl.AddTriangleFilled(tip, notch, right, col);
    }

    private static Vector2 Rot(Vector2 c, float x, float y, float s, float co) =>
        new(c.X + x * co - y * s, c.Y + x * s + y * co);
}
