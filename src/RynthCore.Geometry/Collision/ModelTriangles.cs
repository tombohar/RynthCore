// ============================================================================
//  RynthCore.Geometry - Collision/ModelTriangles.cs
//  A placed model as collision triangles, with the shape ACE's
//  PhysicsObj.FindObjCollisions collides with: the physics polygons of its
//  parts when any part has a physics BSP; otherwise a Setup's collision
//  cylinders, or failing those its spheres, each as a closed 8-sided prism.
//  Also: a polygon (an EnvCell wall or floor) fanned into triangles.
//
//  Logic from RynthSuite Tools/RynthNav.Baker/CollisionGeometry.cs (d4ae46a),
//  AppendModel / AddPolys / AddPrism. Written for RynthCore (MIT).
// ============================================================================

using System.Collections.Concurrent;
using System.Numerics;
using RynthCore.Geometry.Cells;
using RynthCore.Geometry.Dat;
using RynthCore.Geometry.Models;
using RynthCore.Geometry.Scenery;

namespace RynthCore.Geometry.Collision;

internal sealed class ModelTriangles
{
    private readonly ModelReader _models;
    private readonly DatDatabase _portal;
    private readonly ConcurrentDictionary<uint, SetupBounds?> _bounds = new();

    public ModelTriangles(ModelReader models, DatDatabase portal)
    {
        _models = models;
        _portal = portal;
    }

    /// <summary>Setups that collide with cylinders or spheres read from the end of their record (diagnostics).</summary>
    public int TailReads;

    /// <summary>Adds model <paramref name="model"/> placed at <paramref name="origin"/> / <paramref name="rotation"/> / <paramref name="scale"/>.</summary>
    public void Append(uint model, Vector3 origin, Quaternion rotation, float scale, List<CollisionTri> into)
    {
        var shape = _models.Get(model);
        if (shape.Polygons.Count > 0)
        {
            foreach (var poly in shape.Place(origin, rotation, scale)) AppendPolygon(poly, model, into);
            return;
        }
        if ((model >> 24) != 0x02) return;
        var b = _bounds.GetOrAdd(model, id => SetupBounds.Read(_portal, id));
        if (b == null) return;
        if (b.FromTail && (b.Cylinders.Count > 0 || b.Spheres.Count > 0)) Interlocked.Increment(ref TailReads);
        Vector3 World(Vector3 p) => Vector3.Transform(p, rotation) + origin;
        if (b.Cylinders.Count > 0)
            foreach (var c in b.Cylinders)
                AppendPrism(c.Origin * scale, c.Radius * scale, 0, c.Height * scale, World, model, into);
        else
            foreach (var s in b.Spheres)
                AppendPrism(s.Center * scale, s.Radius * scale, -s.Radius * scale, s.Radius * scale, World, model, into);
    }

    /// <summary>A convex polygon fanned into triangles that share its normal (by vertex order); degenerate ones are dropped.</summary>
    public static void AppendPolygon(IReadOnlyList<Vector3> poly, uint source, List<CollisionTri> into)
    {
        if (poly.Count < 3) return;
        Vector3 n = CellDat.Normal(poly);
        if (n == Vector3.Zero || !float.IsFinite(n.X)) return;
        Vector3 a = poly[0];
        for (int i = 1; i < poly.Count - 1; i++)
        {
            Vector3 b = poly[i], c = poly[i + 1];
            if (Vector3.Cross(b - a, c - a).LengthSquared() < 1e-10f) continue;
            into.Add(new CollisionTri(a, b, c, n, source));
        }
    }

    /// <summary>A closed 8-sided prism (sides, top and bottom), outward normals, standing in for a cylinder or sphere.</summary>
    private static void AppendPrism(Vector3 center, float radius, float z0, float z1, Func<Vector3, Vector3> world, uint source, List<CollisionTri> into)
    {
        if (!(radius > 0.01f) || !(z1 > z0)) return;
        const int N = 8;
        var lo = new Vector3[N];
        var hi = new Vector3[N];
        for (int i = 0; i < N; i++)
        {
            float a = i * MathF.PI * 2 / N;
            float x = center.X + radius * MathF.Cos(a), y = center.Y + radius * MathF.Sin(a);
            lo[i] = world(new Vector3(x, y, center.Z + z0));
            hi[i] = world(new Vector3(x, y, center.Z + z1));
        }
        Vector3 cl = world(new Vector3(center.X, center.Y, center.Z + z0)), ch = world(new Vector3(center.X, center.Y, center.Z + z1));
        for (int i = 0; i < N; i++)
        {
            int j = (i + 1) % N;
            AppendPolygon(new[] { lo[i], lo[j], hi[j], hi[i] }, source, into);
            AppendPolygon(new[] { ch, hi[i], hi[j] }, source, into);
            AppendPolygon(new[] { cl, lo[j], lo[i] }, source, into);
        }
    }
}
