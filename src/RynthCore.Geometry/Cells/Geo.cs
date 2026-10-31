// ============================================================================
//  RynthCore.Geometry - Cells/Geo.cs
//  Small geometry helpers for the indoor queries: 2D tests on the floor plan
//  (point in polygon seen from above, distances, plane height) and the 3D
//  segment-through-polygon test used to cross cell portals.
//
//  Moved from RynthSuite Tools/RynthNav.TownNet/Geometry.cs (3a8075a);
//  SegmentCrossesToward, DistToPolygon and SphereRestZ are new (2026-10-28).
//  Written for RynthCore (MIT).
// ============================================================================

using System.Numerics;

namespace RynthCore.Geometry.Cells;

/// <summary>Small geometry helpers: 2D tests on the floor plan, 3D segment-through-polygon.</summary>
public static class Geo
{
    /// <summary>Is (x, y) inside the polygon's outline seen from above (edges count as inside within eps)?</summary>
    public static bool InsideXY(IReadOnlyList<Vector3> poly, float x, float y, float eps = 0.01f)
    {
        bool inside = false;
        int n = poly.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            Vector3 a = poly[i], b = poly[j];
            if ((a.Y > y) != (b.Y > y))
            {
                float xc = (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X;
                if (x < xc) inside = !inside;
            }
        }
        if (inside || eps <= 0) return inside;
        return DistToOutlineXY(poly, x, y) <= eps;
    }

    /// <summary>Distance from (x, y) to the polygon's outline seen from above (0 inside when filled).</summary>
    public static float DistXY(IReadOnlyList<Vector3> poly, float x, float y, bool filled)
    {
        if (filled && poly.Count >= 3 && AreaXY(poly) > 1e-4f && InsideXY(poly, x, y, 0)) return 0;
        return DistToOutlineXY(poly, x, y);
    }

    public static float DistToOutlineXY(IReadOnlyList<Vector3> poly, float x, float y)
    {
        float best = float.MaxValue;
        int n = poly.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
            best = MathF.Min(best, DistToSegXY(poly[j].X, poly[j].Y, poly[i].X, poly[i].Y, x, y));
        return best;
    }

    public static float DistToSegXY(float ax, float ay, float bx, float by, float px, float py)
    {
        float dx = bx - ax, dy = by - ay;
        float len2 = dx * dx + dy * dy;
        float t = len2 < 1e-12f ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len2, 0, 1);
        float ex = ax + t * dx - px, ey = ay + t * dy - py;
        return MathF.Sqrt(ex * ex + ey * ey);
    }

    public static float AreaXY(IReadOnlyList<Vector3> poly)
    {
        float a = 0;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            a += poly[j].X * poly[i].Y - poly[i].X * poly[j].Y;
        return MathF.Abs(a) * 0.5f;
    }

    /// <summary>Height of a (non-vertical) planar polygon's plane at (x, y).</summary>
    public static float PlaneZ(IReadOnlyList<Vector3> poly, float x, float y)
    {
        Vector3 n = CellDat.Normal(poly);
        if (MathF.Abs(n.Z) < 1e-4f) return poly[0].Z;
        Vector3 p = poly[0];
        return p.Z - (n.X * (x - p.X) + n.Y * (y - p.Y)) / n.Z;
    }

    /// <summary>Does the segment a-b pass through the planar polygon (edges included within eps)?</summary>
    public static bool SegmentCrosses(IReadOnlyList<Vector3> poly, Vector3 a, Vector3 b, float eps = 0.02f)
    {
        if (poly.Count < 3) return false;
        Vector3 n = CellDat.Normal(poly);
        if (n == Vector3.Zero) return false;
        float da = Vector3.Dot(n, a - poly[0]), db = Vector3.Dot(n, b - poly[0]);
        if ((da > 0 && db > 0) || (da < 0 && db < 0)) return false;
        float denom = da - db;
        if (MathF.Abs(denom) < 1e-7f) return false;                 // in the plane: not a crossing
        return HitsInside(poly, n, a + (b - a) * (da / denom), eps);
    }

    /// <summary>
    /// Does the segment a-b go through the planar polygon in one direction: from its back to its front
    /// (<paramref name="towardFront"/>; the front is the side the vertex-order normal points to, see
    /// <see cref="CellDat.Normal"/>) or front to back? A point within <paramref name="planeTol"/> of the
    /// plane still counts as on the side it is coming from, so stepping a path sample by sample, each
    /// crossing is seen once, by the step that ends clearly beyond the plane.
    /// </summary>
    public static bool SegmentCrossesToward(IReadOnlyList<Vector3> poly, Vector3 a, Vector3 b, bool towardFront,
                                            float eps = 0.02f, float planeTol = 0.0005f)
    {
        if (poly.Count < 3) return false;
        Vector3 n = CellDat.Normal(poly);
        if (n == Vector3.Zero) return false;
        float s = towardFront ? 1f : -1f;
        float da = s * Vector3.Dot(n, a - poly[0]), db = s * Vector3.Dot(n, b - poly[0]);
        if (da > planeTol || db <= planeTol) return false;
        float t = da >= 0 ? 0 : da / (da - db);                     // where it meets the plane
        return HitsInside(poly, n, a + (b - a) * t, eps);
    }

    /// <summary>
    /// Is <paramref name="p"/> clearly on one side of the planar polygon (its front when
    /// <paramref name="front"/>, else its back), at most <paramref name="maxDist"/> from the plane, and does it
    /// drop onto the polygon itself (not beside it) along the normal?
    /// </summary>
    public static bool BeyondThrough(IReadOnlyList<Vector3> poly, Vector3 p, bool front, float maxDist,
                                     float eps = 0.02f, float planeTol = 0.0005f)
    {
        if (poly.Count < 3) return false;
        Vector3 n = CellDat.Normal(poly);
        if (n == Vector3.Zero) return false;
        float d = Vector3.Dot(n, p - poly[0]);
        float side = front ? d : -d;
        if (side <= planeTol || side > maxDist) return false;
        return HitsInside(poly, n, p - n * d, eps);
    }

    // Is p (on the polygon's plane) inside it? Tested in the plane, dropping the normal's dominant axis.
    private static bool HitsInside(IReadOnlyList<Vector3> poly, Vector3 n, Vector3 p, float eps)
    {
        int drop = DominantAxis(n);
        var flat = new Vector3[poly.Count];
        for (int i = 0; i < flat.Length; i++) flat[i] = Flatten(poly[i], drop);
        Vector3 fp = Flatten(p, drop);
        return InsideXY(flat, fp.X, fp.Y, eps);
    }

    /// <summary>3D distance from <paramref name="p"/> to the planar polygon (its surface, edges and corners).</summary>
    public static float DistToPolygon(IReadOnlyList<Vector3> poly, Vector3 p)
    {
        Vector3 n = CellDat.Normal(poly);
        if (n != Vector3.Zero)
        {
            float d = Vector3.Dot(n, p - poly[0]);
            Vector3 q = p - n * d;
            int drop = DominantAxis(n);
            var flat = new Vector3[poly.Count];
            for (int i = 0; i < flat.Length; i++) flat[i] = Flatten(poly[i], drop);
            Vector3 fq = Flatten(q, drop);
            if (InsideXY(flat, fq.X, fq.Y, 0)) return MathF.Abs(d);
        }
        float best = float.MaxValue;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            best = MathF.Min(best, DistToSegment(poly[j], poly[i], p));
        return best;
    }

    public static float DistToSegment(Vector3 a, Vector3 b, Vector3 p)
    {
        Vector3 ab = b - a;
        float len2 = ab.LengthSquared();
        float t = len2 < 1e-12f ? 0 : Math.Clamp(Vector3.Dot(p - a, ab) / len2, 0, 1);
        return Vector3.Distance(a + ab * t, p);
    }

    /// <summary>
    /// Where a sphere of radius <paramref name="radius"/> above (x, y) comes to rest on the polygon when
    /// lowered onto it: the height of the sphere's bottom (centre - radius), or null when the polygon is not
    /// under the sphere. On a flat floor that is the floor height; on a slope with normal n it is
    /// radius * (1 / n.Z - 1) above the plane (the sphere touches uphill of its centre); near an edge or a
    /// corner the sphere rests on that.
    /// </summary>
    public static float? SphereRestZ(IReadOnlyList<Vector3> poly, float x, float y, float radius)
    {
        if (poly.Count < 3) return null;
        // A height at which the sphere's centre is within radius of the polygon: the polygon's own height
        // at the point of it nearest (x, y) seen from above.
        float lo;
        if (InsideXY(poly, x, y, 0)) lo = PlaneZ(poly, x, y);
        else
        {
            float best = float.MaxValue; lo = 0;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                Vector3 a = poly[j], b = poly[i];
                float dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
                float t = len2 < 1e-12f ? 0 : Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / len2, 0, 1);
                float ex = a.X + t * dx - x, ey = a.Y + t * dy - y, d = MathF.Sqrt(ex * ex + ey * ey);
                if (d < best) { best = d; lo = a.Z + t * (b.Z - a.Z); }
            }
            if (best > radius) return null;
        }
        // Distance from the centre to the polygon is convex in the centre's height, so the heights within
        // radius form an interval holding lo; its top is where the sphere rests.
        float hi = float.MinValue;
        foreach (var v in poly) hi = MathF.Max(hi, v.Z);
        hi += radius + 0.01f;
        for (int it = 0; it < 40 && hi - lo > 1e-4f; it++)
        {
            float mid = 0.5f * (lo + hi);
            if (DistToPolygon(poly, new Vector3(x, y, mid)) <= radius) lo = mid; else hi = mid;
        }
        return lo - radius;
    }

    private static int DominantAxis(Vector3 n) =>
        MathF.Abs(n.X) > MathF.Abs(n.Y) ? (MathF.Abs(n.X) > MathF.Abs(n.Z) ? 0 : 2) : (MathF.Abs(n.Y) > MathF.Abs(n.Z) ? 1 : 2);

    private static Vector3 Flatten(Vector3 v, int drop) => drop switch
    {
        0 => new Vector3(v.Y, v.Z, 0),
        1 => new Vector3(v.X, v.Z, 0),
        _ => new Vector3(v.X, v.Y, 0),
    };
}
