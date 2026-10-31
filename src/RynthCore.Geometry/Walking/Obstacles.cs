// ============================================================================
//  RynthCore.Geometry - Walking/Obstacles.cs
//  IObstacles sources for the walker: the static outdoor world (OutdoorCollision:
//  buildings, statics, building cells, scenery), objects placed at run time (closed
//  doors and the like, given as placed models), and a composite of both.
// ============================================================================

using System.Numerics;
using RynthCore.Geometry.Collision;

namespace RynthCore.Geometry.Walking;

/// <summary>The dats' outdoor world as walker obstacles.</summary>
public sealed class StaticObstacles : IObstacles
{
    private readonly OutdoorCollision _world;

    public StaticObstacles(OutdoorCollision world) => _world = world;

    public IEnumerable<(Vector3 A, Vector3 B, Vector3 C, Vector3 Normal)> Near(uint landblock, float x, float y, float radius)
    {
        foreach (CollisionTri t in _world.Near(landblock, x, y, radius))
            yield return (t.A, t.B, t.C, t.Normal);
    }

    public float? SurfaceBelow(uint landblock, float x, float y, float zTop, float maxDrop)
        => _world.SurfaceBelow(landblock, x, y, zTop, maxDrop);
}

/// <summary>Several obstacle sources as one.</summary>
public sealed class CompositeObstacles : IObstacles
{
    private readonly IObstacles[] _parts;

    public CompositeObstacles(params IObstacles[] parts) => _parts = parts;

    public IEnumerable<(Vector3 A, Vector3 B, Vector3 C, Vector3 Normal)> Near(uint landblock, float x, float y, float radius)
        => _parts.SelectMany(p => p.Near(landblock, x, y, radius));

    public float? SurfaceBelow(uint landblock, float x, float y, float zTop, float maxDrop)
    {
        float? best = null;
        foreach (IObstacles p in _parts)
            if (p.SurfaceBelow(landblock, x, y, zTop, maxDrop) is { } z && (best == null || z > best)) best = z;
        return best;
    }
}

/// <summary>
/// Triangles that come and go (closed doors, ...), keyed by the object they belong to, in
/// landblock-local coordinates of the landblock given when set. Thread-safe.
/// </summary>
public sealed class DynamicObstacles : IObstacles
{
    private readonly object _gate = new();
    private readonly Dictionary<uint, (uint Landblock, (Vector3 A, Vector3 B, Vector3 C, Vector3 Normal)[] Tris)> _byObject = new();

    public int Count { get { lock (_gate) return _byObject.Count; } }

    public void Set(uint objectId, uint landblock, IEnumerable<(Vector3 A, Vector3 B, Vector3 C, Vector3 Normal)> tris)
    {
        var arr = tris.ToArray();
        lock (_gate) _byObject[objectId] = ((landblock >> 16) == 0 ? landblock : landblock >> 16, arr);
    }

    public void Remove(uint objectId)
    {
        lock (_gate) _byObject.Remove(objectId);
    }

    public IEnumerable<(Vector3 A, Vector3 B, Vector3 C, Vector3 Normal)> Near(uint landblock, float x, float y, float radius)
    {
        uint lb = (landblock >> 16) == 0 ? landblock : landblock >> 16;
        List<(Vector3, Vector3, Vector3, Vector3)> hits = new();
        lock (_gate)
        {
            foreach (var (olb, tris) in _byObject.Values)
            {
                // Shift a neighbouring landblock's triangles into this frame.
                float ox = ((int)(olb >> 8) - (int)(lb >> 8)) * 192f, oy = ((int)(olb & 0xFF) - (int)(lb & 0xFF)) * 192f;
                if (MathF.Abs(ox) > 192 || MathF.Abs(oy) > 192) continue;
                var off = new Vector3(ox, oy, 0);
                foreach (var t in tris)
                {
                    Vector3 a = t.A + off, b = t.B + off, c = t.C + off;
                    float minX = MathF.Min(a.X, MathF.Min(b.X, c.X)), maxX = MathF.Max(a.X, MathF.Max(b.X, c.X));
                    float minY = MathF.Min(a.Y, MathF.Min(b.Y, c.Y)), maxY = MathF.Max(a.Y, MathF.Max(b.Y, c.Y));
                    if (x < minX - radius || x > maxX + radius || y < minY - radius || y > maxY + radius) continue;
                    hits.Add((a, b, c, t.Normal));
                }
            }
        }
        return hits;
    }

    /// <summary>Dynamic objects are not stood on (doors are walls).</summary>
    public float? SurfaceBelow(uint landblock, float x, float y, float zTop, float maxDrop) => null;
}
