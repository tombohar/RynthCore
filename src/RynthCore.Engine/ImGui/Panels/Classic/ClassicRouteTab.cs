// ============================================================================
//  RynthCore.Engine - ImGui/Panels/Classic/ClassicRouteTab.cs
//  The Classic look's Route tab: RynthAi's nav route (UiSources.Nav, the Nav
//  panel's source) in the retail style. The route picker loads a route; the
//  four gems set the route type; the list shows each point (kind, where, and
//  how far it is from you) with the point being walked to in gold; a click
//  selects a point, a double-click makes it the one to walk to. Add here (a
//  point, a portal or a pause, at the end or around the selected point),
//  Remove, and the overlay switches. Recalls, chat points, editing and the
//  trail tools are on the Nav panel (the "Nav panel" plate).
//  Commands are the Nav panel's (NavCommands). AC's render thread only.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.UI;
using RynthCore.Engine.UI.Data;
using static RynthCore.Engine.ImGuiBackend.Panels.Classic.ClassicKit;

namespace RynthCore.Engine.ImGuiBackend.Panels.Classic;

internal sealed class ClassicRouteTab : IClassicTab
{
    public string Name => "Route";

    private const int VisibleRows = 14;
    private static readonly string[] AddKinds = { "Point", "Portal", "Pause 5 s" };
    private static readonly string[] AddWhere = { "At the end", "Above selected", "Below selected" };

    private int _selected = -1, _first, _addKind, _addMode;
    private float _grab;

    public void Shown()
    {
        UiSources.Nav.Subscribe();
        UiSources.Nav.RequestRefresh();
    }

    public void Hidden() => UiSources.Nav.Unsubscribe();

    public float Draw(ClassicContext ctx, Vector2 o, float w)
    {
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        NavPayload? nav = UiSources.Nav.Current?.Value;
        float y = o.Y;
        List<NavPoint> points = nav?.Points ?? new List<NavPoint>();
        int typeIdx = NavCommands.RouteIndex(nav?.RouteType ?? 0);

        string right = nav == null ? "waiting for RynthAi"
            : points.Count == 0 ? "no points"
            : $"{points.Count} points · {NavCommands.RouteTypes[typeIdx].ToLowerInvariant()} · at {nav.ActiveNavIndex + 1}";
        Header(dl, new Vector2(o.X, y), w, "Route", right);
        y += Px(23);

        // Route file.
        {
            float fileH = Px(22), labelW = Px(52);
            TextMid(dl, o.X + Px(3), y, fileH, "Route", Text);
            var ddPos = new Vector2(o.X + labelW + Px(6), y + (fileH - Px(19)) * 0.5f);
            float ddW = w - labelW - Px(6) - Px(52);
            string[] files = Files(nav);
            string current = nav == null || nav.ActiveNavName.Length == 0 ? "None" : nav.ActiveNavName;
            if (Dropdown("##cr_file", current, ddPos, ddW, ctx.Popup.IsOpen))
                ctx.Popup.Open(ddPos + new Vector2(0, Px(19)), ddW, files, Array.IndexOf(files, current),
                    i => NavCommands.Send(new NavCmd { Cmd = "loadNav", NavName = files[i] }));
            SafeImGui.SetItemTooltip("Load a route");
            if (Plate("##cr_navpanel", "Nav", new Vector2(o.X + w - Px(46), y + (fileH - Px(17)) * 0.5f)))
                PanelRouter.Toggle("Nav");
            SafeImGui.SetItemTooltip("The Nav panel: recalls, chat points, editing points, the trail tools");
            y += fileH;
        }

        // Route type.
        {
            float gw = w / 4;
            for (int i = 0; i < 4; i++)
            {
                if (Gem("##cr_type" + i, NavCommands.RouteTypes[i], new Vector2(o.X + i * gw, y), gw, i == typeIdx, 20) && nav != null)
                    NavCommands.Send(new NavCmd { Cmd = "setRouteType", RouteType = NavCommands.RouteTypeFor(i) });
                SafeImGui.SetItemTooltip(RouteTypeTip(i));
            }
            y += Px(22);
        }

        // Columns and the list.
        float listW = w - Px(16);
        float cn = Px(24), ck = Px(62), cd = Px(58);
        float cw = listW - cn - ck - cd - Px(8);
        {
            float hh = Px(14);
            TextMid(dl, o.X + Px(4), y, hh, "#", Dim, 10);
            TextMid(dl, o.X + Px(4) + cn, y, hh, "Kind", Dim, 10);
            TextMid(dl, o.X + Px(4) + cn + ck, y, hh, "Where", Dim, 10);
            TextRight(dl, o.X + listW - Px(4), y, hh, "Distance", Dim, 10);
            y += hh;
        }
        float rowH = Px(17), listH = rowH * VisibleRows;
        var listMin = new Vector2(o.X, y);
        dl.AddRectFilled(listMin, listMin + new Vector2(listW, listH), RynthTheme.Argb(0x8C000000));
        _first = Wheel(listMin, listMin + new Vector2(w, listH), _first, points.Count, VisibleRows);
        bool havePos = TryPlayerCoords(out double myNs, out double myEw);
        for (int r = 0; r < VisibleRows; r++)
        {
            int i = _first + r;
            var p = new Vector2(o.X, y + r * rowH);
            if (i >= points.Count) break;
            NavPoint pt = points[i];
            bool sel = i == _selected, cur = nav != null && i == nav.ActiveNavIndex;
            if (sel) RowSelected(dl, p, p + new Vector2(listW, rowH));
            else if ((i & 1) == 1) dl.AddRectFilled(p, p + new Vector2(listW, rowH), AltRow);
            ImGuiNET.ImGui.SetCursorScreenPos(p);
            ImGuiNET.ImGui.PushID(i);
            if (ImGuiNET.ImGui.InvisibleButton("##pt", new Vector2(listW, rowH))) _selected = i;
            if (ImGuiNET.ImGui.IsItemHovered() && ImGuiNET.ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                NavCommands.Send(new NavCmd { Cmd = "setActive", Index = i });
            if (ImGuiNET.ImGui.IsItemHovered()) dl.AddRectFilled(p, p + new Vector2(listW, rowH), Hover);
            SafeImGui.SetItemTooltip(NavCommands.PointText(pt, i + 1) + "\nDouble-click: walk to this point next.");
            ImGuiNET.ImGui.PopID();
            uint col = cur ? Gold : Text;
            TextRight(dl, p.X + cn - Px(4), p.Y, rowH, (i + 1).ToString(), cur ? Gold : Dim, 10);
            TextClip(dl, p.X + Px(4) + cn, p.Y, rowH, p.X + cn + ck, pt.Type, col, 10);
            string where = pt.Desc.Length > 0 ? pt.Desc : pt.Text.Length > 0 ? pt.Text : $"{Fmt(pt.NS, 'N', 'S')}, {Fmt(pt.EW, 'E', 'W')}";
            TextClip(dl, p.X + Px(4) + cn + ck, p.Y, rowH, p.X + cn + ck + cw, where, col, 10);
            if (havePos && HasPosition(pt))
            {
                double yd = Math.Sqrt((pt.NS - myNs) * (pt.NS - myNs) + (pt.EW - myEw) * (pt.EW - myEw)) * 240.0;
                TextRight(dl, p.X + listW - Px(4), p.Y, rowH, cur && yd < 1 ? "here" : yd < 1000 ? $"{yd:0} yd" : $"{yd / 1000:0.0}k yd", col, 10);
            }
        }
        if (points.Count == 0)
            TextMid(dl, o.X + Px(6), y, rowH, nav == null ? "Waiting for RynthAi…" : "No points. Load a route, or Add here.", Dim, 10);
        _first = Scrollbar("##cr_sb", new Vector2(o.X + listW, y), listH, points.Count, VisibleRows, _first, ref _grab);
        if (_selected >= points.Count) _selected = points.Count - 1;
        y += listH + Px(6);

        // Add here / kind / where / Remove.
        {
            float addW = Px(96), remW = Px(86);
            if (Button("##cr_add", "Add here", new Vector2(o.X, y), addW, ButtonColor.Blue, nav != null))
                Add();
            SafeImGui.SetItemTooltip("Add a " + AddKinds[_addKind].ToLowerInvariant() + " where you stand, " + AddWhere[_addMode].ToLowerInvariant() + ".");
            float ddW = (w - addW - remW - Px(18)) / 2;
            var kindPos = new Vector2(o.X + addW + Px(6), y + Px(6.5f));
            if (Dropdown("##cr_kind", AddKinds[_addKind], kindPos, ddW, false))
                ctx.Popup.Open(kindPos + new Vector2(0, Px(19)), ddW, AddKinds, _addKind, i => _addKind = i);
            var wherePos = new Vector2(kindPos.X + ddW + Px(6), kindPos.Y);
            if (Dropdown("##cr_where", AddWhere[_addMode], wherePos, ddW, false))
                ctx.Popup.Open(wherePos + new Vector2(0, Px(19)), ddW, AddWhere, _addMode, i => _addMode = i);
            if (Button("##cr_remove", "Remove", new Vector2(o.X + w - remW, y), remW, ButtonColor.Red, _selected >= 0 && _selected < points.Count))
                NavCommands.Send(new NavCmd { Cmd = "deletePoint", Index = _selected });
            SafeImGui.SetItemTooltip(_selected >= 0 ? $"Remove point {_selected + 1}" : "Select a point to remove it");
            y += Px(36);
        }

        // Overlay switches.
        if (nav != null)
        {
            float gw = w / 3;
            if (Gem("##cr_path", "Show path", new Vector2(o.X, y), gw, nav.ShowRouteOverlay))
                NavCommands.Send(new NavCmd { Cmd = "setRouteOverlay", On = !nav.ShowRouteOverlay });
            SafeImGui.SetItemTooltip(SettingsSchema.RouteOverlayTip);
            if (Gem("##cr_trail", "Trail", new Vector2(o.X + gw, y), gw, nav.TrackBreadcrumbs))
                NavCommands.Send(new NavCmd { Cmd = "setBreadcrumbs", On = !nav.TrackBreadcrumbs });
            SafeImGui.SetItemTooltip(SettingsSchema.BreadcrumbsTip);
            if (Gem("##cr_rec", "Record", new Vector2(o.X + 2 * gw, y), gw, nav.IsRecording))
                NavCommands.Send(new NavCmd { Cmd = "setRecording", On = !nav.IsRecording });
            SafeImGui.SetItemTooltip("Add a point as you walk.");
            y += Px(18);
        }
        return y - o.Y;
    }

    private NavPayload? _filesFor;
    private string[] _files = Array.Empty<string>();

    /// <summary>The route files as an array, rebuilt only when a new payload arrives.</summary>
    private string[] Files(NavPayload? nav)
    {
        if (nav == null) return Array.Empty<string>();
        if (!ReferenceEquals(nav, _filesFor)) { _filesFor = nav; _files = nav.NavFiles.ToArray(); }
        return _files;
    }

    private void Add()
    {
        int at = _addMode == 0 ? -1 : _selected;
        switch (_addKind)
        {
            case 0: NavCommands.Send(new NavCmd { Cmd = "addWaypoint", AddMode = _addMode, InsertAt = at }); break;
            case 1: NavCommands.Send(new NavCmd { Cmd = "addPortal", AddMode = _addMode, InsertAt = at }); break;
            default: NavCommands.Send(new NavCmd { Cmd = "addPause", Seconds = 5, AddMode = _addMode, InsertAt = at }); break;
        }
    }

    private static string RouteTypeTip(int i) => i switch
    {
        1 => "Circular: walk the route round and round.",
        2 => "Linear: walk to the end, then back.",
        3 => "Follow: follow a player.",
        _ => "Once: walk the route one time.",
    };

    private static string Fmt(double v, char pos, char neg) => $"{Math.Abs(v):0.00}{(v >= 0 ? pos : neg)}";

    private static bool HasPosition(NavPoint pt) => pt.NS != 0 || pt.EW != 0;

    /// <summary>Your map coordinates, from the per-frame pose copy (RynthAi's NavCoordinateHelper formula).</summary>
    private static bool TryPlayerCoords(out double ns, out double ew)
    {
        ns = ew = 0;
        if (!PlayerPhysicsHooks.TryGetPlayerPoseSnapshot(out uint cell, out float x, out float y, out _, out _, out _, out _, out _)
            || (cell >> 16) == 0) return false;
        ew = (((cell >> 24) & 0xFF) * 8.0 + x / 24.0 - 1019.5) / 10.0;
        ns = (((cell >> 16) & 0xFF) * 8.0 + y / 24.0 - 1019.5) / 10.0;
        return double.IsFinite(ns) && double.IsFinite(ew);
    }
}
