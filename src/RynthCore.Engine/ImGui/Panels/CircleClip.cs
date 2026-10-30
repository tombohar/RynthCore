// ============================================================================
//  RynthCore.Engine - ImGui/Panels/CircleClip.cs
//  The round Radar's geometry, with no ImGui in it (tools\RadarSettingsTests links this file):
//  ImGui only clips to rectangles, so everything the radar draws that could cross the circle's
//  edge is cut to it by hand - line segments exactly, convex polygons (floor strips, map
//  tiles, with their texture coordinates carried along) against a 48-sided polygon inscribed
//  in the circle (Sutherland-Hodgman; the circle's edge is within 0.2% of true).
// ============================================================================

using System;
using System.Numerics;

namespace RynthCore.Engine.ImGuiBackend.Panels;

/// <summary>A circle on the screen: where the round radar's picture shows.</summary>
internal readonly struct RoundMask
{
    public readonly Vector2 Centre;
    public readonly float Radius;

    public RoundMask(Vector2 centre, float radius) { Centre = centre; Radius = radius; }

    public bool Contains(Vector2 p) => (p - Centre).LengthSquared() <= Radius * Radius;

    /// <summary>The whole box is inside the circle.</summary>
    public bool ContainsBox(Vector2 min, Vector2 max) =>
        Contains(min) && Contains(max) && Contains(new Vector2(min.X, max.Y)) && Contains(new Vector2(max.X, min.Y));

    /// <summary>Part of the box may be inside the circle (false: none of it is).</summary>
    public bool OverlapsBox(Vector2 min, Vector2 max)
    {
        Vector2 nearest = Vector2.Clamp(Centre, min, max);
        return (nearest - Centre).LengthSquared() <= Radius * Radius;
    }
}

internal static class CircleClip
{
    private const int Sides = 48;
    private static readonly Vector2[] Normals = BuildNormals();
    private static readonly float PlaneDist = MathF.Cos(MathF.PI / Sides);       // x radius: an edge's distance from the centre

    private static Vector2[] BuildNormals()
    {
        var n = new Vector2[Sides];
        for (int k = 0; k < Sides; k++)
        {
            float a = (k + 0.5f) * (2f * MathF.PI / Sides);
            n[k] = new Vector2(MathF.Cos(a), MathF.Sin(a));
        }
        return n;
    }

    /// <summary>The most vertices a clipped polygon can have: its own plus one per edge of the 48-gon.</summary>
    public const int MaxPoints = 8 + Sides;

    /// <summary>
    /// Cuts the segment a-b to the circle. False when no part of it is inside (a and b are then undefined).
    /// </summary>
    public static bool Segment(in RoundMask m, ref Vector2 a, ref Vector2 b)
    {
        Vector2 d = b - a, f = a - m.Centre;
        float A = Vector2.Dot(d, d);
        float C = Vector2.Dot(f, f) - m.Radius * m.Radius;
        if (A < 1e-9f) return C <= 0;
        float B = 2f * Vector2.Dot(f, d);
        float disc = B * B - 4f * A * C;
        if (disc <= 0) return false;
        float root = MathF.Sqrt(disc);
        float t0 = (-B - root) / (2f * A), t1 = (-B + root) / (2f * A);
        if (t1 < 0f || t0 > 1f) return false;
        t0 = MathF.Max(t0, 0f); t1 = MathF.Min(t1, 1f);
        Vector2 na = a + d * t0, nb = a + d * t1;
        a = na; b = nb;
        return true;
    }

    /// <summary>
    /// Cuts the convex polygon <paramref name="pts"/> (with a texture coordinate per point in
    /// <paramref name="uvs"/>, or empty for none) to the circle. Returns the number of points left
    /// in <paramref name="outPts"/> / <paramref name="outUvs"/> (each at least <see cref="MaxPoints"/> long;
    /// outUvs is untouched without uvs): 0 when nothing is inside.
    /// </summary>
    public static int Polygon(in RoundMask m, ReadOnlySpan<Vector2> pts, ReadOnlySpan<Vector2> uvs, Span<Vector2> outPts, Span<Vector2> outUvs)
    {
        bool hasUv = uvs.Length >= pts.Length;
        Span<Vector2> aP = stackalloc Vector2[MaxPoints], aU = stackalloc Vector2[MaxPoints];
        Span<Vector2> bP = stackalloc Vector2[MaxPoints], bU = stackalloc Vector2[MaxPoints];
        int n = pts.Length;
        if (n < 3 || n > 8) return 0;
        pts.CopyTo(aP);
        if (hasUv) uvs[..n].CopyTo(aU);

        float dist = m.Radius * PlaneDist;
        Span<Vector2> curP = aP, curU = aU, nxtP = bP, nxtU = bU;
        for (int k = 0; k < Sides && n >= 3; k++)
        {
            Vector2 nk = Normals[k];
            int cnt = 0;
            for (int i = 0; i < n; i++)
            {
                Vector2 p = curP[i], q = curP[(i + 1) % n];
                float sp = Vector2.Dot(p - m.Centre, nk) - dist, sq = Vector2.Dot(q - m.Centre, nk) - dist;
                bool pin = sp <= 0, qin = sq <= 0;
                if (pin) { nxtP[cnt] = p; if (hasUv) nxtU[cnt] = curU[i]; cnt++; }
                if (pin != qin)
                {
                    float t = sp / (sp - sq);
                    nxtP[cnt] = p + (q - p) * t;
                    if (hasUv) nxtU[cnt] = curU[i] + (curU[(i + 1) % n] - curU[i]) * t;
                    cnt++;
                }
            }
            n = cnt;
            Span<Vector2> tp = curP, tu = curU;
            curP = nxtP; curU = nxtU;
            nxtP = tp; nxtU = tu;
        }
        if (n < 3) return 0;
        curP[..n].CopyTo(outPts);
        if (hasUv) curU[..n].CopyTo(outUvs);
        return n;
    }
}
