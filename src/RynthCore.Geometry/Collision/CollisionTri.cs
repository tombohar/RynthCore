// ============================================================================
//  RynthCore.Geometry - Collision/CollisionTri.cs
//  One solid triangle of the outdoor world (a face of a static object, a
//  building shell, a building cell or a piece of scenery), landblock-local,
//  with its unit normal by vertex order and the id of what it came from; and
//  the segment-vs-triangle test the collision queries use.
//
//  Logic from RynthSuite Tools/RynthNav.Baker/CollisionGeometry.cs (d4ae46a),
//  Tri and Tri.Hit. Written for RynthCore (MIT).
// ============================================================================

using System.Numerics;
using RynthCore.Geometry.Cells;

namespace RynthCore.Geometry.Collision;

/// <summary>
/// A solid triangle, landblock-local (x, y in the frame of the landblock that was queried, z absolute).
/// <see cref="Normal"/> is the unit normal by vertex order (A, B, C counter-clockwise seen from the side it
/// points to; for a fanned polygon, the whole polygon's normal as ACE's Polygon.make_plane computes it).
/// <see cref="Source"/> is the model id (GfxObj 0x01.. / Setup 0x02..) it belongs to, or the EnvCell id
/// (0xXXYY01nn..) for a building cell's own walls and floors.
/// </summary>
public readonly record struct CollisionTri(Vector3 A, Vector3 B, Vector3 C, Vector3 Normal, uint Source)
{
    /// <summary>Can it be stood on: normal.Z at least ACE's FloorZ (0.66417414, about 48 degrees)?</summary>
    public bool Walkable => Normal.Z >= CellDat.FloorZ;

    public float MinX => MathF.Min(A.X, MathF.Min(B.X, C.X));
    public float MaxX => MathF.Max(A.X, MathF.Max(B.X, C.X));
    public float MinY => MathF.Min(A.Y, MathF.Min(B.Y, C.Y));
    public float MaxY => MathF.Max(A.Y, MathF.Max(B.Y, C.Y));
    public float MinZ => MathF.Min(A.Z, MathF.Min(B.Z, C.Z));
    public float MaxZ => MathF.Max(A.Z, MathF.Max(B.Z, C.Z));

    /// <summary>The same triangle moved by <paramref name="d"/>.</summary>
    public CollisionTri Shifted(Vector3 d) => this with { A = A + d, B = B + d, C = C + d };

    /// <summary>
    /// Segment <paramref name="p0"/> to <paramref name="p1"/> against the triangle, from either side
    /// (Moller-Trumbore). <paramref name="t"/> is the fraction along the segment, 0..1.
    /// </summary>
    public bool Hit(Vector3 p0, Vector3 p1, out float t)
    {
        t = 0;
        Vector3 d = p1 - p0, e1 = B - A, e2 = C - A;
        Vector3 h = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, h);
        if (MathF.Abs(det) < 1e-12f) return false;
        float inv = 1f / det;
        Vector3 s = p0 - A;
        float u = Vector3.Dot(s, h) * inv;
        if (u < 0 || u > 1) return false;
        Vector3 q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(d, q) * inv;
        if (v < 0 || u + v > 1) return false;
        t = Vector3.Dot(e2, q) * inv;
        return t >= 0 && t <= 1;
    }

    /// <summary>Is (x, y) inside the triangle seen from above (within <paramref name="eps"/> metres of it)?</summary>
    public bool ContainsXY(float x, float y, float eps = 0.001f)
    {
        float d1 = Edge(A, B, x, y), d2 = Edge(B, C, x, y), d3 = Edge(C, A, x, y);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
        if (!(neg && pos)) return true;
        return Geo.DistToSegXY(A.X, A.Y, B.X, B.Y, x, y) <= eps
            || Geo.DistToSegXY(B.X, B.Y, C.X, C.Y, x, y) <= eps
            || Geo.DistToSegXY(C.X, C.Y, A.X, A.Y, x, y) <= eps;
    }

    /// <summary>The height of the triangle's plane at (x, y); NaN for a vertical triangle.</summary>
    public float PlaneZ(float x, float y)
    {
        if (MathF.Abs(Normal.Z) < 1e-6f) return float.NaN;
        return A.Z - (Normal.X * (x - A.X) + Normal.Y * (y - A.Y)) / Normal.Z;
    }

    /// <summary>Distance seen from above from (x, y) to the triangle (0 inside).</summary>
    public float DistXY(float x, float y)
    {
        if (ContainsXY(x, y, 0)) return 0;
        return MathF.Min(Geo.DistToSegXY(A.X, A.Y, B.X, B.Y, x, y),
               MathF.Min(Geo.DistToSegXY(B.X, B.Y, C.X, C.Y, x, y), Geo.DistToSegXY(C.X, C.Y, A.X, A.Y, x, y)));
    }

    private static float Edge(Vector3 a, Vector3 b, float x, float y) => (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);
}
