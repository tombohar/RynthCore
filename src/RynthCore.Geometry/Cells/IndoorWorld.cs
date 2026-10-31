// ============================================================================
//  RynthCore.Geometry - Cells/IndoorWorld.cs
//  The indoor cell index: EnvCells loaded per landblock on first use and kept
//  (one load per landblock, thread-safe), with the queries a client needs to
//  keep its indoor cell id right:
//    FindCell   - which cell a landblock-local position stands in;
//    FloorZ     - the floor height of a cell at (x, y);
//    TrackCell  - the cell after moving a -> b, following cell portals;
//    Floors / Walls / ObjectPolygons - the cell's geometry for collision.
//
//  Why it matters: ACE trusts the client's cell id for indoor positions and
//  never corrects it, so a wrong cell id is not fixed up by the server.
//  Indoor cell ids are 0x0100..0xFFFD in the landblock's low word; a cell
//  portal whose OtherCell is 0xFFFF leads outdoors.
//
//  Written for RynthCore (MIT) on 2026-10-28, on the EnvCell readers and the
//  Trace walk moved from RynthSuite Tools/RynthNav.TownNet (3a8075a).
// ============================================================================

using System.Collections.Concurrent;
using System.Numerics;
using RynthCore.Geometry.Dat;
using RynthCore.Geometry.Models;

namespace RynthCore.Geometry.Cells;

/// <summary>
/// Indoor cells by landblock, loaded lazily from the dats and cached. All coordinates are landblock-local
/// (ACE position frame). Safe to share between threads: each landblock is loaded once (other callers for
/// the same landblock wait for that load) and the loaded cells are never changed afterwards.
/// </summary>
public sealed class IndoorWorld
{
    /// <summary>FindCell: a floor up to this far above the feet still counts (steps, rounding).</summary>
    public const float FindAbove = 0.5f;
    /// <summary>FindCell: floors further below the feet than this are ignored.</summary>
    public const float FindBelow = 3.0f;
    /// <summary>TrackCell: sample spacing along the move.</summary>
    public const float TrackStep = 0.05f;
    /// <summary>TrackCell: the move is tested this far above the feet, so it passes through doorways, not their sills.</summary>
    public const float PortalLift = 0.6f;

    private const float FloorEps = 0.02f;      // edge tolerance of floor point-in-polygon tests
    private const float TieZ = 0.02f;          // floors this close in height are the same level
    private const float InCellEps = 0.05f;     // TrackCell: still in the cell this close to its outline
    private const float SlopedDoorwayReach = 2.0f; // TrackCell: how far past a sloping doorway's plane it still counts

    private readonly DatDatabase _cellDat;
    private readonly DatDatabase _portalDat;
    private readonly ConcurrentDictionary<uint, Lazy<Landblock>> _landblocks = new();

    /// <summary>Models for the cells' static objects (shared cache).</summary>
    public ModelReader Models { get; }

    public IndoorWorld(DatDatabase cellDat, DatDatabase portalDat, ModelReader? models = null)
    {
        _cellDat = cellDat;
        _portalDat = portalDat;
        Models = models ?? new ModelReader(portalDat);
    }

    private sealed class Landblock
    {
        public readonly IReadOnlyList<EnvCell> Cells;
        public readonly Dictionary<uint, EnvCell> ById;
        public Landblock(List<EnvCell> cells)
        {
            Cells = cells;
            ById = cells.ToDictionary(c => c.Id);
        }
    }

    private static uint Key(uint landblockOrCell) => landblockOrCell > 0xFFFF ? landblockOrCell >> 16 : landblockOrCell;

    private Landblock Get(uint landblockOrCell) =>
        _landblocks.GetOrAdd(Key(landblockOrCell), lb => new Lazy<Landblock>(
            () => new Landblock(CellDat.LoadLandblock(_cellDat, _portalDat, lb)),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    /// <summary>
    /// The EnvCells of a landblock (0xXXYY, or any cell id 0xXXYYnnnn of it), ascending by id; empty for a
    /// landblock with no indoor cells. Loaded on first use and cached.
    /// </summary>
    public IReadOnlyList<EnvCell> LoadLandblock(uint landblock) => Get(landblock).Cells;

    /// <summary>Forgets a landblock's cells (they are loaded again on next use).</summary>
    public void Unload(uint landblock) => _landblocks.TryRemove(Key(landblock), out _);

    /// <summary>The cell with full id <paramref name="cellId"/>, or null when it is not an indoor cell in the dats.</summary>
    public EnvCell? GetCell(uint cellId)
    {
        if (!EnvCell.IsIndoor(cellId)) return null;
        return Get(cellId).ById.TryGetValue(cellId, out var c) ? c : null;
    }

    /// <summary>
    /// The indoor cell a landblock-local position (x, y, z = feet) stands in, and its floor height there.
    /// <para>
    /// Every cell floor polygon whose outline (seen from above) contains (x, y) is a candidate if its height
    /// there is at most <see cref="FindAbove"/> above z and at most <see cref="FindBelow"/> below it; the
    /// highest candidate wins (the floor nearest at-or-below the feet). Floors at the same height (within
    /// 2 cm, e.g. two cells meeting at a doorway) are settled by the point being strictly inside, then by
    /// its distance from the polygon's edge.
    /// </para>
    /// <para>
    /// Overhangs: where cells are stacked (a bridge or upper floor above a lower room), a floor more than
    /// 0.5 m above the feet is ignored, so someone under a bridge stays in the lower cell, and someone on
    /// the bridge gets the bridge cell because it is the highest floor at or below their feet. A floor less
    /// than 0.5 m above the feet (a thin overhang right above someone's head is never that low; a step is)
    /// would win, and someone falling past several stacked floors gets the highest one at or below them.
    /// More than 3 m above any floor (high in the air, or off the floor plan) gives null.
    /// </para>
    /// </summary>
    public (uint cellId, float floorZ)? FindCell(uint landblock, float x, float y, float z)
    {
        EnvCell? best = null;
        float bestZ = 0, bestMargin = 0;
        foreach (var c in Get(landblock).Cells)
        {
            if (!c.BoundsContain(x, y, FloorEps)) continue;
            foreach (var f in c.Floors)
            {
                if (!Geo.InsideXY(f, x, y, FloorEps)) continue;
                float fz = Geo.PlaneZ(f, x, y);
                if (fz > z + FindAbove || fz < z - FindBelow) continue;
                float edge = Geo.DistToOutlineXY(f, x, y);
                float margin = Geo.InsideXY(f, x, y, 0) ? edge : -edge;
                bool better = best == null
                    || fz > bestZ + TieZ
                    || (fz >= bestZ - TieZ && margin > bestMargin);
                if (better) { best = c; bestZ = fz; bestMargin = margin; }
            }
        }
        return best == null ? null : (best.Id, bestZ);
    }

    /// <summary>
    /// The floor height of cell <paramref name="cellId"/> at (x, y), or null when (x, y) is not over its floor
    /// (or the cell is unknown). Where the cell has several floors over (x, y), the one nearest
    /// <paramref name="near"/>, or the highest when <paramref name="near"/> is null.
    /// </summary>
    public float? FloorZ(uint cellId, float x, float y, float? near = null)
    {
        var c = GetCell(cellId);
        return c == null ? null : CellFloorZ(c, x, y, near, FloorEps);
    }

    /// <summary><see cref="FloorZ(uint, float, float, float?)"/> for a cell already in hand.</summary>
    public static float? CellFloorZ(EnvCell c, float x, float y, float? near = null, float eps = FloorEps)
    {
        float? best = null;
        foreach (var f in c.Floors)
        {
            if (!Geo.InsideXY(f, x, y, eps)) continue;
            float z = Geo.PlaneZ(f, x, y);
            bool better = best == null || (near != null
                ? MathF.Abs(z - near.Value) < MathF.Abs(best.Value - near.Value)
                : z > best.Value);
            if (better) best = z;
        }
        return best;
    }

    /// <summary>
    /// The height of the feet of someone standing in cell <paramref name="cellId"/> at (x, y), whose body
    /// touches the floor with a sphere of radius <paramref name="radius"/> (the human Setup 0x02000001 uses
    /// 0.48 m; see <see cref="ModelReader"/>). This is the z ACE reports for a grounded creature: equal to
    /// <see cref="FloorZ"/> on flat floor, but above the floor plane on a slope (radius * (1 / n.Z - 1),
    /// about 0.12 m on a 37 degree ramp) and where a ramp meets a flat floor. Null when (x, y) is not over
    /// the cell's floor.
    /// <para>The sphere rests on the highest floor polygon under it among those at the level picked by
    /// <see cref="FloorZ"/> (with <paramref name="near"/>): from 0.5 m below to <see cref="FindAbove"/>
    /// above that floor.</para>
    /// </summary>
    public float? StandZ(uint cellId, float x, float y, float radius, float? near = null)
    {
        var c = GetCell(cellId);
        return c == null ? null : CellStandZ(c, x, y, radius, near);
    }

    /// <summary><see cref="StandZ"/> for a cell already in hand.</summary>
    public static float? CellStandZ(EnvCell c, float x, float y, float radius, float? near = null)
    {
        float? level = CellFloorZ(c, x, y, near);
        if (level == null) return null;
        float best = level.Value;
        foreach (var f in c.Floors)
        {
            float? rz = Geo.SphereRestZ(f, x, y, radius);
            if (rz == null || rz.Value < level.Value - 0.5f || rz.Value > level.Value + FindAbove) continue;
            best = MathF.Max(best, rz.Value);
        }
        return best;
    }

    /// <summary>
    /// The cell someone is in after moving in a straight line from <paramref name="a"/> to <paramref name="b"/>
    /// (landblock-local feet positions), starting in indoor cell <paramref name="fromCell"/>.
    /// <para>
    /// The move is sampled every <see cref="TrackStep"/> and tested <see cref="PortalLift"/> above the feet.
    /// Each step that passes through one of the current cell's portal polygons outward (toward the side
    /// <see cref="CellPortal.PortalSide"/> gives the other cell) moves into the cell behind it, any number
    /// of cells along the way. A point exactly on a doorway's plane (within 0.5 mm) stays in the cell it
    /// came from, so a stop right in a doorway does not flip cells.
    /// </para>
    /// <para>
    /// Against ACE (dungeon 0x6146): for 19476 pairs of captured positions in neighbouring cells up to 4 m
    /// apart, 19473 end in the other position's cell; the 3 others end at one position that lies 2 mm on
    /// the near side of a doorway's plane although ACE puts it in the far cell.
    /// </para>
    /// Returns:
    /// <list type="bullet">
    /// <item>the final cell's id (<paramref name="fromCell"/> itself when no portal was crossed);</item>
    /// <item>0 when the move goes out through a portal whose OtherCell is 0xFFFF: the walker is outdoors and
    /// the caller computes the outdoor land cell from the position;</item>
    /// <item><paramref name="fromCell"/> when the move cannot be followed: a sample leaves the current cell's
    /// floor plan (its physics polygons seen from above) without passing a portal - through a wall, or
    /// geometry missing - or <paramref name="fromCell"/> is not a known indoor cell. Treat that as "blocked or
    /// unknown": keep the old cell, and re-check with <see cref="FindCell"/> if in doubt.</item>
    /// </list>
    /// </summary>
    public uint TrackCell(uint fromCell, Vector3 a, Vector3 b)
    {
        if (!EnvCell.IsIndoor(fromCell)) return fromCell;
        var lb = Get(fromCell);
        if (!lb.ById.TryGetValue(fromCell, out var cur)) return fromCell;

        float len = Vector3.Distance(a, b);
        int n = Math.Max(1, (int)MathF.Ceiling(len / TrackStep));
        var lift = new Vector3(0, 0, PortalLift);
        Vector3 prev = a;
        for (int i = 1; i <= n; i++)
        {
            Vector3 p = Vector3.Lerp(a, b, i / (float)n);
            EnvCell? next = null;
            foreach (var cp in cur.Portals)
            {
                if (cp.Polygon.Length < 3) continue;
                if (!Geo.SegmentCrossesToward(cp.Polygon, prev + lift, p + lift, cp.PortalSide)) continue;
                if (cp.LeadsOutdoors) return 0;
                if (lb.ById.TryGetValue(cur.Neighbour(cp), out var other)) { next = other; break; }
            }
            if (next != null)
            {
                // A sloping doorway (a tunnel mouth under a slanted roof, dungeon 0x6346) is crossed this high
                // above the feet before the floor beyond it starts: until it does, stay on this cell's floor.
                if (next.CoversXY(p.X, p.Y, InCellEps) || !cur.CoversXY(p.X, p.Y, InCellEps)) cur = next;
            }
            else if (!cur.CoversXY(p.X, p.Y, InCellEps))
            {
                // Off this floor: into the cell behind a sloping doorway passed (above the feet) earlier.
                EnvCell? beyond = BeyondSlopedDoorway(lb, cur, p, p + lift);
                if (beyond == null) return fromCell;
                cur = beyond;
            }
            prev = p;
        }
        return cur.Id;
    }

    // The neighbour whose floor plan covers p, behind one of cur's doorways that the feet or the lifted point
    // are already through: beyond the doorway's plane (by less than a step) and over the doorway itself, not
    // the wall beside it. Walking down under a sloping doorway the lifted point passes it first; walking up
    // out from under it, the feet do.
    private static EnvCell? BeyondSlopedDoorway(Landblock lb, EnvCell cur, Vector3 p, Vector3 lifted)
    {
        foreach (var cp in cur.Portals)
        {
            if (cp.Polygon.Length < 3 || cp.LeadsOutdoors) continue;
            if (!lb.ById.TryGetValue(cur.Neighbour(cp), out var other)) continue;
            if (!other.CoversXY(p.X, p.Y, InCellEps)) continue;
            if (Geo.BeyondThrough(cp.Polygon, lifted, cp.PortalSide, SlopedDoorwayReach)
                || Geo.BeyondThrough(cp.Polygon, p, cp.PortalSide, SlopedDoorwayReach))
                return other;
        }
        return null;
    }

    /// <summary>The cell's floor polygons (walkable physics polygons), landblock-local; empty for an unknown cell.</summary>
    public IReadOnlyList<Vector3[]> Floors(uint cellId) => GetCell(cellId)?.Floors ?? (IReadOnlyList<Vector3[]>)Array.Empty<Vector3[]>();

    /// <summary>The cell's other physics polygons (walls, ceilings, steep slopes), landblock-local; empty for an unknown cell.</summary>
    public IReadOnlyList<Vector3[]> Walls(uint cellId) => GetCell(cellId)?.Walls ?? (IReadOnlyList<Vector3[]>)Array.Empty<Vector3[]>();

    /// <summary>The physics polygons of the cell's static objects, placed landblock-local (objects without physics give none).</summary>
    public IEnumerable<Vector3[]> ObjectPolygons(uint cellId)
    {
        var c = GetCell(cellId);
        if (c == null) yield break;
        foreach (var s in c.Stabs)
            foreach (var p in Models.Get(s.Id).Place(s.Origin, s.Rotation))
                yield return p;
    }

    /// <summary>The cells next to <paramref name="cellId"/> through its portals (full ids; outdoors not included).</summary>
    public IEnumerable<uint> Neighbours(uint cellId)
    {
        var c = GetCell(cellId);
        if (c == null) yield break;
        foreach (var p in c.Portals)
            if (!p.LeadsOutdoors) yield return c.Neighbour(p);
    }
}
