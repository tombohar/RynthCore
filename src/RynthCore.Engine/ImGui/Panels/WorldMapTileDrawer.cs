// ============================================================================
//  RynthCore.Engine - ImGui/Panels/WorldMapTileDrawer.cs
//  Draws the World Map picture (WorldMapTiles' pyramid of tiles) into a rectangle, for any view that
//  can say where the picture sits: the World Map and Mini Map (WorldMapView) and the Radar's
//  "world map behind the radar" option. The view gives the cell at its centre, the zoom (screen px
//  per cell) and the rotation; this picks the sharpest tile level the screen can use, draws the
//  coarser levels under it (so whatever hasn't arrived shows blurry, not blank) and asks for the
//  tiles under the view, centre first. One drawer per view (it keeps a scratch list).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed class WorldMapTileDrawer
{
    private readonly List<(int Dist, int Col, int Row)> _tileOrder = new(128);

    /// <summary>
    /// Draws the tiles under the canvas. <paramref name="centre"/> is the screen point of map cell
    /// (<paramref name="ccx"/>, <paramref name="ccy"/>); <paramref name="scale"/> is pixels per cell; <paramref name="phi"/>
    /// the map's rotation in radians (0: north up). <paramref name="tint"/> is an ImGui colour
    /// (white = as is; its alpha fades the picture). <paramref name="mask"/>: only the part inside that circle is drawn
    /// (the round Radar); tiles across its edge are cut to it, texture coordinates and all. AC's render thread, inside a Draw.
    /// </summary>
    public void Draw(ImDrawListPtr dl, Vector2 canvasMin, Vector2 canvasMax, Vector2 centre,
        double ccx, double ccy, float scale, float phi, uint tint = 0xFFFFFFFF, RoundMask? mask = null)
    {
        var levels = WorldMapTiles.Levels;
        if (!WorldMapTiles.IsOpen || levels.Count == 0) return;
        int ppc0 = WorldMapTiles.PixelsPerCell, top = levels.Count - 1;
        double cellsHigh = WorldMapTiles.CellsHigh;
        // The map area in view: the canvas as it is, or (turned) the circle around it.
        double halfW = (canvasMax.X - canvasMin.X) * 0.5, halfH = (canvasMax.Y - canvasMin.Y) * 0.5;
        if (phi != 0f) { halfW = halfH = Math.Sqrt(halfW * halfW + halfH * halfH); }
        double cxMin = ccx - halfW / scale, cxMax = ccx + halfW / scale;
        double cyMin = ccy - halfH / scale, cyMax = ccy + halfH / scale;

        // The sharpest level whose pixels are at least as dense as the screen's; a coarser one if that is too many tiles.
        int L = Math.Clamp((int)Math.Floor(Math.Log2(ppc0 / (double)scale)), 0, top);
        while (L < top && VisibleTiles(L, ppc0, cellsHigh, cxMin, cxMax, cyMin, cyMax) > 80) L++;

        var proj = new MapProjection(centre, ccx, ccy, scale, phi);
        // Coarse to fine: the top level (always loaded) first, so whatever hasn't arrived shows blurry, not blank.
        for (int lv = top; lv >= L; lv--)
            DrawLevel(dl, lv, ppc0, cellsHigh, cxMin, cxMax, cyMin, cyMax, request: lv <= Math.Min(top, L + 1),
                centre, ccx, ccy, scale, phi != 0f, proj, tint, mask);
    }

    private static int VisibleTiles(int lv, int ppc0, double cellsHigh, double cxMin, double cxMax, double cyMin, double cyMax)
    {
        double cpt = WorldMapTiles.TileSize * (double)(1 << lv) / ppc0;
        int cols = (int)Math.Floor(Math.Min(cxMax, WorldMapTiles.CellsWide) / cpt) - (int)Math.Floor(Math.Max(cxMin, 0) / cpt) + 1;
        int rows = (int)Math.Floor((cellsHigh - Math.Max(cyMin, 0)) / cpt) - (int)Math.Floor((cellsHigh - Math.Min(cyMax, cellsHigh)) / cpt) + 1;
        return Math.Max(cols, 0) * Math.Max(rows, 0);
    }

    private void DrawLevel(ImDrawListPtr dl, int lv, int ppc0, double cellsHigh, double cxMin, double cxMax, double cyMin, double cyMax,
        bool request, Vector2 centre, double ccx, double ccy, float scale, bool turned, in MapProjection proj, uint tint, RoundMask? mask)
    {
        var lvl = WorldMapTiles.Levels[lv];
        double cpt = WorldMapTiles.TileSize * (double)(1 << lv) / ppc0;
        int c0 = Math.Max(0, (int)Math.Floor(cxMin / cpt)), c1 = Math.Min(lvl.Cols - 1, (int)Math.Floor(cxMax / cpt));
        int r0 = Math.Max(0, (int)Math.Floor((cellsHigh - cyMax) / cpt)), r1 = Math.Min(lvl.Rows - 1, (int)Math.Floor((cellsHigh - cyMin) / cpt));
        if (c1 < c0 || r1 < r0) return;

        // Centre-out, so the tiles you're looking at load first.
        _tileOrder.Clear();
        double ccol = ccx / cpt, crow = (cellsHigh - ccy) / cpt;
        for (int r = r0; r <= r1; r++)
            for (int c = c0; c <= c1; c++)
            {
                double dx = c + 0.5 - ccol, dy = r + 0.5 - crow;
                _tileOrder.Add(((int)((dx * dx + dy * dy) * 1000), c, r));
            }
        _tileOrder.Sort(static (a, b) => a.Dist.CompareTo(b.Dist));

        for (int i = _tileOrder.Count - 1; i >= 0; i--)      // draw far to near; nothing overlaps within a level
        {
            var (_, c, r) = _tileOrder[i];
            IntPtr tex;
            if (!(request ? WorldMapTiles.TryGet(lv, c, r, out tex) : WorldMapTiles.Peek(lv, c, r, out tex))) continue;
            double left = c * cpt, right = (c + 1) * cpt, topCell = cellsHigh - r * cpt, bottomCell = cellsHigh - (r + 1) * cpt;
            if (mask is RoundMask round)
            {
                DrawRound(dl, tex, round, proj, left, right, topCell, bottomCell, tint);
                continue;
            }
            if (!turned)
            {
                // Edges from the same formula for both neighbours, rounded to whole pixels: no seams, no gaps.
                float x0 = MathF.Round(centre.X + (float)((left - ccx) * scale)), x1 = MathF.Round(centre.X + (float)((right - ccx) * scale));
                float y0 = MathF.Round(centre.Y - (float)((topCell - ccy) * scale)), y1 = MathF.Round(centre.Y - (float)((bottomCell - ccy) * scale));
                dl.AddImage(tex, new Vector2(x0, y0), new Vector2(x1, y1), Vector2.Zero, Vector2.One, tint);
            }
            else
            {
                // Turned: a quad, grown by a pixel so neighbours overlap instead of showing a hairline.
                Vector2 tl = proj.Project(left, topCell), tr = proj.Project(right, topCell), br = proj.Project(right, bottomCell), bl = proj.Project(left, bottomCell);
                Vector2 mid = (tl + br) * 0.5f;
                tl = Grow(tl, mid); tr = Grow(tr, mid); br = Grow(br, mid); bl = Grow(bl, mid);
                dl.AddImageQuad(tex, tl, tr, br, bl, new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1), tint);
            }
        }
    }

    /// <summary>One tile inside a circle: whole when it is, skipped when none of it is, else cut to the circle.</summary>
    private static unsafe void DrawRound(ImDrawListPtr dl, IntPtr tex, in RoundMask mask, in MapProjection proj,
        double left, double right, double topCell, double bottomCell, uint tint)
    {
        Span<Vector2> q = stackalloc Vector2[4];
        q[0] = proj.Project(left, topCell); q[1] = proj.Project(right, topCell);
        q[2] = proj.Project(right, bottomCell); q[3] = proj.Project(left, bottomCell);
        Vector2 mid = (q[0] + q[2]) * 0.5f;
        for (int i = 0; i < 4; i++) q[i] = Grow(q[i], mid);          // a pixel of overlap, as the other path
        Vector2 min = Vector2.Min(Vector2.Min(q[0], q[1]), Vector2.Min(q[2], q[3]));
        Vector2 max = Vector2.Max(Vector2.Max(q[0], q[1]), Vector2.Max(q[2], q[3]));
        if (!mask.OverlapsBox(min, max)) return;
        Span<Vector2> uv = stackalloc Vector2[4];
        uv[0] = new Vector2(0, 0); uv[1] = new Vector2(1, 0); uv[2] = new Vector2(1, 1); uv[3] = new Vector2(0, 1);
        if (mask.ContainsBox(min, max))
        {
            dl.AddImageQuad(tex, q[0], q[1], q[2], q[3], uv[0], uv[1], uv[2], uv[3], tint);
            return;
        }
        Span<Vector2> p = stackalloc Vector2[CircleClip.MaxPoints], t = stackalloc Vector2[CircleClip.MaxPoints];
        int n = CircleClip.Polygon(mask, q, uv, p, t);
        if (n < 3) return;
        // A textured fan, written by hand (ImGui has no textured polygon call).
        dl.PushTextureID(tex);
        dl.PrimReserve((n - 2) * 3, n);
        uint first = dl.NativePtr->_VtxCurrentIdx;
        for (int i = 0; i < n; i++) dl.PrimWriteVtx(p[i], t[i], tint);
        for (int i = 1; i < n - 1; i++)
        {
            dl.PrimWriteIdx((ushort)first);
            dl.PrimWriteIdx((ushort)(first + (uint)i));
            dl.PrimWriteIdx((ushort)(first + (uint)i + 1));
        }
        dl.PopTextureID();
    }

    private static Vector2 Grow(Vector2 p, Vector2 mid)
    {
        Vector2 d = p - mid;
        float len = d.Length();
        return len < 1f ? p : p + d / len * 0.8f;
    }
}
