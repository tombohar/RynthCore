// ============================================================================
//  RynthCore.Geometry - Walking/Walker.cs
//  Where a walking step lands: a kinematic walker over the dats. Not ACE's physics
//  (no sphere sweep / BSP transitions); what a bot needs to move in ways the server and
//  other players find sensible:
//
//    - the body is the human Setup's two collision spheres (r 0.48 at 0.475 and 1.35 m);
//      walls block a step that moves a sphere into them (moving away is always allowed);
//      the lower test sits high enough (bottom at StepUp) that stairs and kerbs don't block;
//    - a move is cut into sub-steps of at most SubStep metres, so StepUp (0.45 m) and the
//      walkable slope (FloorZ) agree: a 0.4 m step on the steepest walkable ground rises
//      ~0.45 m;
//    - indoors: the cell follows the doorways crossed (IndoorWorld.TrackCell); leaving a
//      cell's floor plan without a doorway is a wall; the feet height is where the body's
//      sphere rests on the floor (StandZ), which is what ACE reports;
//    - outdoors: the feet are on the terrain (less water depth) or on a walkable object
//      surface (porches, platforms) within StepUp; terrain steeper than walkable can't be
//      climbed; deep water (all four corners water) is not entered; crossing a building's
//      outdoor doorway inward enters that EnvCell;
//    - drops of more than MaxDrop are refused (a bot shouldn't walk off cliffs).
//  When a step is blocked the walker tries sliding: the same step turned by 25, 50 and
//  75 degrees either way (shortened by the cosine).
//
//  Coordinates: positions are landblock-local (x, y in 0..192 of the cell's landblock,
//  z absolute). An outdoor step may leave the landblock; the result is re-expressed in
//  the new landblock with the new cell id.
// ============================================================================

using System.Numerics;
using RynthCore.Geometry.Cells;
using RynthCore.Geometry.Terrain;

namespace RynthCore.Geometry.Walking;

/// <summary>Static and dynamic obstacles outdoors (buildings, statics, scenery, doors), landblock-local.</summary>
public interface IObstacles
{
    /// <summary>Triangles within <paramref name="radius"/> of (x, y) on the ground plane, in <paramref name="landblock"/>'s frame.</summary>
    IEnumerable<(Vector3 A, Vector3 B, Vector3 C, Vector3 Normal)> Near(uint landblock, float x, float y, float radius);

    /// <summary>The highest walkable object surface under (x, y) between zTop - maxDrop and zTop, not terrain.</summary>
    float? SurfaceBelow(uint landblock, float x, float y, float zTop, float maxDrop);
}

public readonly record struct StepResult(uint Cell, Vector3 Position, bool Blocked, string? Why = null, bool Slid = false);

public sealed class Walker
{
    public const float FloorZ = 0.66417414618662751f;

    public float Radius { get; init; } = 0.48f;
    /// <summary>Sphere centres above the feet used for wall tests.</summary>
    public float LowSphere { get; init; } = 0.93f;
    public float HighSphere { get; init; } = 1.35f;
    public float StepUp { get; init; } = 0.45f;
    public float MaxDrop { get; init; } = 2.5f;
    public float SubStep { get; init; } = 0.4f;

    private readonly TerrainSampler _terrain;
    private readonly IndoorWorld _indoor;
    private readonly IObstacles? _obstacles;

    public Walker(TerrainSampler terrain, IndoorWorld indoor, IObstacles? obstacles = null)
    {
        _terrain = terrain;
        _indoor = indoor;
        _obstacles = obstacles;
    }

    /// <summary>Moves from <paramref name="pos"/> in <paramref name="cell"/> by (dx, dy) on the ground plane.</summary>
    public StepResult Step(uint cell, Vector3 pos, float dx, float dy)
    {
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1e-5f) return new StepResult(cell, pos, false);
        int n = Math.Max(1, (int)MathF.Ceiling(len / SubStep));
        float sx = dx / n, sy = dy / n;
        bool slid = false;
        for (int i = 0; i < n; i++)
        {
            StepResult r = SubStepWithSlide(cell, pos, sx, sy);
            if (r.Blocked) return r with { Slid = slid || r.Slid };
            slid |= r.Slid;
            cell = r.Cell;
            pos = r.Position;
        }
        return new StepResult(cell, pos, false, null, slid);
    }

    private static readonly float[] SlideAngles = { 25, -25, 50, -50, 75, -75 };

    private StepResult SubStepWithSlide(uint cell, Vector3 pos, float dx, float dy)
    {
        StepResult r = TryMove(cell, pos, dx, dy);
        if (!r.Blocked) return r;
        foreach (float deg in SlideAngles)
        {
            float a = deg * MathF.PI / 180f, c = MathF.Cos(a), s = MathF.Sin(a);
            float rx = (dx * c - dy * s) * c, ry = (dx * s + dy * c) * c;
            StepResult t = TryMove(cell, pos, rx, ry);
            if (!t.Blocked) return t with { Slid = true };
        }
        return r;
    }

    /// <summary>One straight sub-step, no sliding.</summary>
    public StepResult TryMove(uint cell, Vector3 pos, float dx, float dy)
    {
        var to = new Vector3(pos.X + dx, pos.Y + dy, pos.Z);
        return EnvCell.IsIndoor(cell) ? MoveIndoor(cell, pos, to) : MoveOutdoor(cell, pos, to);
    }

    // ── indoors ─────────────────────────────────────────────────────────────

    private StepResult MoveIndoor(uint cell, Vector3 from, Vector3 to)
    {
        EnvCell? cur = _indoor.GetCell(cell);
        if (cur == null) return Blocked(cell, from, "unknown cell");
        uint next = _indoor.TrackCell(cell, from, to);
        if (next == 0)
        {
            // Out through a doorway to the outdoors.
            uint lb = cell >> 16;
            float gz = OutdoorGround(lb, to.X, to.Y, from.Z, out string? why);
            if (why != null) return Blocked(cell, from, why);
            var outside = new Vector3(to.X, to.Y, gz);
            if (BlockedByObstacles(lb, from, outside)) return Blocked(cell, from, "obstacle");
            return Relocated(lb, outside);
        }
        if (next == cell && !cur.CoversXY(to.X, to.Y)) return Blocked(cell, from, "wall");

        float? z = _indoor.StandZ(next, to.X, to.Y, Radius, from.Z);
        if (z is not { } fz) return Blocked(cell, from, "no floor");
        if (fz - from.Z > StepUp) return Blocked(cell, from, "step too high");
        if (from.Z - fz > MaxDrop) return Blocked(cell, from, "drop");
        var p = new Vector3(to.X, to.Y, fz);
        if (BlockedIndoors(next, from, p)) return Blocked(cell, from, "wall");
        return new StepResult(next, p, false);
    }

    private bool BlockedIndoors(uint cell, Vector3 from, Vector3 to)
    {
        foreach (uint c in CellAndNeighbours(cell))
        {
            foreach (Vector3[] wall in _indoor.Walls(c))
                if (IntoPolygon(wall, from, to)) return true;
            foreach (Vector3[] obj in _indoor.ObjectPolygons(c))
                if (CellDat.Normal(obj).Z < FloorZ && IntoPolygon(obj, from, to)) return true;
        }
        return false;
    }

    private IEnumerable<uint> CellAndNeighbours(uint cell)
    {
        yield return cell;
        foreach (uint n in _indoor.Neighbours(cell)) yield return n;
    }

    // ── outdoors ────────────────────────────────────────────────────────────

    private StepResult MoveOutdoor(uint cell, Vector3 from, Vector3 to)
    {
        uint lb = cell >> 16;

        // Into a building through one of its outdoor doorways?
        foreach (EnvCell c in _indoor.LoadLandblock(lb))
        {
            if ((c.Flags & 1) == 0) continue;   // only cells seen from outside have outdoor doorways
            foreach (CellPortal p in c.Portals)
            {
                if (!p.LeadsOutdoors || p.Polygon.Length < 3) continue;
                var lift = new Vector3(0, 0, IndoorWorld.PortalLift);
                if (!Geo.SegmentCrossesToward(p.Polygon, from + lift, to + lift, !p.PortalSide)) continue;
                // Just past the threshold the floor plan may not start yet: keep the height until it does.
                float fz = _indoor.StandZ(c.Id, to.X, to.Y, Radius, from.Z) ?? from.Z;
                if (fz - from.Z > StepUp) return Blocked(cell, from, "step too high");
                var inside = new Vector3(to.X, to.Y, fz);
                if (BlockedIndoors(c.Id, from, inside)) return Blocked(cell, from, "wall");
                return new StepResult(c.Id, inside, false);
            }
        }

        float gz = OutdoorGround(lb, to.X, to.Y, from.Z, out string? why);
        if (why != null) return Blocked(cell, from, why);
        var at = new Vector3(to.X, to.Y, gz);
        if (BlockedByObstacles(lb, from, at)) return Blocked(cell, from, "obstacle");
        return Relocated(lb, at);
    }

    /// <summary>Feet height outdoors at (x, y) in landblock lb's frame (x/y may be outside 0..192). why != null = not allowed.</summary>
    private float OutdoorGround(uint lb, float x, float y, float fromZ, out string? why)
    {
        why = null;
        (uint tlb, float lx, float ly) = Normalize(lb, x, y);
        LandblockData? land = _terrain.LoadLandblock(tlb);
        if (land == null) { why = "no terrain"; return fromZ; }
        int cx = Math.Clamp((int)(lx / 24), 0, 7), cy = Math.Clamp((int)(ly / 24), 0, 7);
        if (TerrainSampler.GetWaterType(land, cx, cy) == WaterType.EntirelyWater) { why = "deep water"; return fromZ; }

        float terrainZ = TerrainSampler.GetTerrainZ(land, lx, ly) - TerrainSampler.GetWaterDepth(land, lx, ly);
        float z = terrainZ;
        if (_obstacles?.SurfaceBelow(tlb, lx, ly, fromZ + StepUp, MaxDrop + StepUp) is { } s && s > z) z = s;

        if (z - fromZ > StepUp)
        {
            // Climbing: allowed on walkable terrain (sub-steps keep the rise per step small), not on objects.
            bool onTerrain = z == terrainZ;
            if (!onTerrain || !TerrainSampler.IsWalkable(TerrainSampler.GetNormal(land, lx, ly))) { why = onTerrain ? "too steep" : "step too high"; return fromZ; }
        }
        if (fromZ - z > MaxDrop) { why = "drop"; return fromZ; }
        return z;
    }

    private bool BlockedByObstacles(uint lb, Vector3 from, Vector3 to)
    {
        if (_obstacles == null) return false;
        foreach (var t in _obstacles.Near(lb, to.X, to.Y, Radius + 0.5f))
        {
            if (t.Normal.Z >= FloorZ) continue;
            if (IntoPolygon(new[] { t.A, t.B, t.C }, from, to)) return true;
        }
        return false;
    }

    /// <summary>
    /// Could the body stand at <paramref name="p"/> (landblock-local, in <paramref name="cell"/>)?
    /// Indoors: the cell has a floor there. Outdoors: there is ground (not deep water, not too
    /// far below or above p.Z) and no wall within the body's radius.
    /// </summary>
    public bool CanStand(uint cell, Vector3 p)
    {
        if (EnvCell.IsIndoor(cell))
            return _indoor.StandZ(cell, p.X, p.Y, Radius, p.Z) is { } z && MathF.Abs(z - p.Z) < 2f;
        uint lb = cell >> 16;
        float gz = OutdoorGround(lb, p.X, p.Y, p.Z + 1f, out string? why);
        if (why != null || MathF.Abs(gz - p.Z) > 2f) return false;
        if (_obstacles == null) return true;
        var at = new Vector3(p.X, p.Y, gz);
        foreach (var t in _obstacles.Near(lb, p.X, p.Y, Radius + 0.5f))
        {
            if (t.Normal.Z >= FloorZ) continue;
            var tri = new[] { t.A, t.B, t.C };
            if (Geo.DistToPolygon(tri, at + new Vector3(0, 0, LowSphere)) < Radius * 0.8f
                || Geo.DistToPolygon(tri, at + new Vector3(0, 0, HighSphere)) < Radius * 0.8f) return false;
        }
        return true;
    }

    // ── shared ──────────────────────────────────────────────────────────────

    /// <summary>Does moving the body from -> to push one of its spheres into the polygon?</summary>
    private bool IntoPolygon(IReadOnlyList<Vector3> poly, Vector3 from, Vector3 to)
    {
        foreach (float h in new[] { LowSphere, HighSphere })
        {
            var b = new Vector3(to.X, to.Y, to.Z + h);
            float after = Geo.DistToPolygon(poly, b);
            if (after >= Radius) continue;
            float before = Geo.DistToPolygon(poly, new Vector3(from.X, from.Y, from.Z + h));
            if (after < before - 1e-4f) return true;
        }
        return false;
    }

    private static StepResult Blocked(uint cell, Vector3 at, string why) => new(cell, at, true, why);

    /// <summary>An outdoor position given in landblock lb's frame (0xXXYY), re-expressed in the landblock that contains it.</summary>
    private static StepResult Relocated(uint lb, Vector3 p)
    {
        (uint l, float x, float y) = Normalize(lb, p.X, p.Y);
        return new StepResult(OutdoorCell(l, x, y), new Vector3(x, y, p.Z), false);
    }

    private static (uint lb, float x, float y) Normalize(uint lb, float x, float y)
    {
        int bx = (int)(lb >> 8) & 0xFF, by = (int)lb & 0xFF;
        while (x < 0 && bx > 0) { x += 192; bx--; }
        while (x >= 192 && bx < 254) { x -= 192; bx++; }
        while (y < 0 && by > 0) { y += 192; by--; }
        while (y >= 192 && by < 254) { y -= 192; by++; }
        return ((uint)((bx << 8) | by), Math.Clamp(x, 0, 191.999f), Math.Clamp(y, 0, 191.999f));
    }

    /// <summary>The outdoor land cell (1..64) containing (x, y) of landblock lb (x/y may be outside 0..192).</summary>
    public static uint OutdoorCell(uint lb, float x, float y)
    {
        (uint l, float lx, float ly) = Normalize(lb, x, y);
        return (l << 16) | (uint)((int)(lx / 24) * 8 + (int)(ly / 24) + 1);
    }
}
