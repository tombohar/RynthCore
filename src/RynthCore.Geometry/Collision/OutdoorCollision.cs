// ============================================================================
//  RynthCore.Geometry - Collision/OutdoorCollision.cs
//  What an outdoor walker collides with besides the terrain: per landblock,
//  the static objects and building shells of its LandblockInfo, the walls,
//  floors and objects of its building cells (EnvCells flagged SeenOutside),
//  and the scenery the server places. Each landblock's triangles are built
//  on first use, kept, and indexed on a 4 m grid; queries near a landblock
//  edge also see the neighbouring landblocks' triangles, shifted into the
//  queried landblock's frame.
//
//  Logic from RynthSuite Tools/RynthNav.Baker/CollisionGeometry.cs (d4ae46a):
//  AppendStatics, AppendBuildingCells, AppendScenery. RaiseIfLow is left out:
//  it raises low objects into walls for the navmesh baker's 1 m climb, which
//  a walker that steps like the client does not need.
//  Written for RynthCore (MIT).
// ============================================================================

using System.Collections.Concurrent;
using System.Numerics;
using RynthCore.Geometry.Cells;
using RynthCore.Geometry.Dat;
using RynthCore.Geometry.Models;
using RynthCore.Geometry.Scenery;
using RynthCore.Geometry.Terrain;

namespace RynthCore.Geometry.Collision;

/// <summary>Where a landblock's collision triangles came from (diagnostics).</summary>
public readonly record struct CollisionCounts(
    int StaticObjects, int StaticTris,
    int Buildings, int BuildingTris,
    int BuildingCells, int BuildingCellTris,
    int CellObjects, int CellObjectTris,
    int Scenery, int SceneryTris)
{
    public int Total => StaticTris + BuildingTris + BuildingCellTris + CellObjectTris + SceneryTris;
    public override string ToString() =>
        $"{Total} tris: statics {StaticObjects} ({StaticTris}), buildings {Buildings} ({BuildingTris}), " +
        $"building cells {BuildingCells} ({BuildingCellTris}), cell objects {CellObjects} ({CellObjectTris}), scenery {Scenery} ({SceneryTris})";
}

/// <summary>
/// Outdoor collision geometry (everything solid but the terrain), landblock by landblock.
/// <para>Coordinates: every query names a landblock (0xXXYY or any 0xXXYYnnnn id) and takes and returns
/// positions in that landblock's frame (x, y in 0..192 from its SW corner, z absolute). Near an edge the
/// neighbouring landblocks' triangles are included, moved into that frame.</para>
/// <para>Thread-safe: each landblock is built once (other callers wait for that build) and never changed.</para>
/// </summary>
public sealed class OutdoorCollision
{
    /// <summary>Grid bucket size, metres.</summary>
    public const float Bucket = 4f;
    /// <summary>How far a landblock's triangles may reach past its edge for a query to look at that neighbour.</summary>
    public const float MaxOverhang = 24f;
    /// <summary>ACE's FloorZ: a triangle with normal.Z at least this can be stood on.</summary>
    public const float FloorZ = CellDat.FloorZ;

    private const int Side = (int)(LandblockData.BlockLength / Bucket);   // 48 buckets a side

    private readonly DatDatabase _cell;
    private readonly TerrainSampler _terrain;
    private readonly IndoorWorld _indoor;
    private readonly ModelTriangles _shapes;
    private readonly ConcurrentDictionary<uint, Lazy<Block>> _blocks = new();

    /// <summary>The scenery placer this uses (its tables are shared).</summary>
    public SceneryPlacer Scenery { get; }

    public OutdoorCollision(DatDatabase cell, DatDatabase portal, TerrainSampler terrain, ModelReader models, IndoorWorld indoor)
    {
        _cell = cell;
        _terrain = terrain;
        _indoor = indoor;
        _shapes = new ModelTriangles(models, portal);
        Scenery = new SceneryPlacer(cell, portal, terrain, models);
    }

    /// <summary>Setups whose cylinders/spheres had to be read from the end of their record (diagnostics).</summary>
    public int TailReads => _shapes.TailReads;

    // ---- per-landblock build ----------------------------------------------------------

    private sealed class Block
    {
        public CollisionTri[] Tris = Array.Empty<CollisionTri>();
        public float[] MinX = Array.Empty<float>(), MaxX = Array.Empty<float>(), MinY = Array.Empty<float>(), MaxY = Array.Empty<float>();
        public int[][] Grid = Array.Empty<int[]>();
        public float BMinX, BMinY, BMaxX, BMaxY;     // XY bounds of all triangles (empty: Min > Max)
        public CollisionCounts Counts;
        public IReadOnlyList<SceneryObject> Scenery = Array.Empty<SceneryObject>();
    }

    private static uint Key(uint landblock) => LandblockData.KeyOf(landblock);

    private Block Get(uint landblock) =>
        _blocks.GetOrAdd(Key(landblock), k => new Lazy<Block>(() => Build(k), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    /// <summary>Forgets a landblock's triangles (built again on next use).</summary>
    public void Unload(uint landblock) => _blocks.TryRemove(Key(landblock), out _);

    private Block Build(uint lb)
    {
        var tris = new List<CollisionTri>();
        int statics = 0, staticTris = 0, buildings = 0, buildingTris = 0, cells = 0, cellTris = 0, cellObjs = 0, cellObjTris = 0;

        var info = LandblockInfo.Load(_cell, lb);
        if (info != null)
        {
            foreach (var s in info.Objects)
            {
                int n0 = tris.Count;
                _shapes.Append(s.Id, s.Origin, s.Rotation, 1f, tris);
                statics++; staticTris += tris.Count - n0;
            }
            foreach (var b in info.Buildings)
            {
                int n0 = tris.Count;
                _shapes.Append(b.ModelId, b.Origin, b.Rotation, 1f, tris);
                buildings++; buildingTris += tris.Count - n0;
            }
        }

        // Building interiors: the EnvCells seen from outside (dungeon cells are not).
        if (info == null || info.NumCells > 0)
        {
            foreach (var c in _indoor.LoadLandblock(lb))
            {
                if ((c.Flags & 0x01) == 0) continue;
                cells++;
                int n0 = tris.Count;
                foreach (var f in c.Floors) ModelTriangles.AppendPolygon(f, c.Id, tris);
                foreach (var w in c.Walls) ModelTriangles.AppendPolygon(w, c.Id, tris);
                cellTris += tris.Count - n0;
                foreach (var s in c.Stabs)
                {
                    n0 = tris.Count;
                    _shapes.Append(s.Id, s.Origin, s.Rotation, 1f, tris);
                    cellObjs++; cellObjTris += tris.Count - n0;
                }
            }
        }

        var scenery = Scenery.Place(lb);
        int sceneryTris0 = tris.Count;
        foreach (var o in scenery) _shapes.Append(o.ModelId, o.Position, o.Rotation, o.Scale, tris);

        var block = new Block
        {
            Tris = tris.ToArray(),
            Scenery = scenery,
            Counts = new CollisionCounts(statics, staticTris, buildings, buildingTris, cells, cellTris, cellObjs, cellObjTris,
                scenery.Count, tris.Count - sceneryTris0),
        };
        Index(block);
        return block;
    }

    private static int Cell(float v) => Math.Clamp((int)MathF.Floor(v / Bucket), 0, Side - 1);

    private static void Index(Block b)
    {
        int n = b.Tris.Length;
        b.MinX = new float[n]; b.MaxX = new float[n]; b.MinY = new float[n]; b.MaxY = new float[n];
        b.BMinX = b.BMinY = float.MaxValue; b.BMaxX = b.BMaxY = float.MinValue;
        var lists = new List<int>?[Side * Side];
        for (int i = 0; i < n; i++)
        {
            var t = b.Tris[i];
            b.MinX[i] = t.MinX; b.MaxX[i] = t.MaxX; b.MinY[i] = t.MinY; b.MaxY[i] = t.MaxY;
            b.BMinX = MathF.Min(b.BMinX, t.MinX); b.BMaxX = MathF.Max(b.BMaxX, t.MaxX);
            b.BMinY = MathF.Min(b.BMinY, t.MinY); b.BMaxY = MathF.Max(b.BMaxY, t.MaxY);
            for (int gx = Cell(t.MinX); gx <= Cell(t.MaxX); gx++)
                for (int gy = Cell(t.MinY); gy <= Cell(t.MaxY); gy++)
                    (lists[gx * Side + gy] ??= new List<int>()).Add(i);
        }
        b.Grid = lists.Select(l => l?.ToArray() ?? Array.Empty<int>()).ToArray();
    }

    /// <summary>
    /// Calls <paramref name="visit"/> once for every triangle of <paramref name="b"/> whose XY bounds overlap
    /// the box (in <paramref name="b"/>'s own frame). Each triangle is reported from one bucket only: the first
    /// bucket of the overlap of its bounds and the box.
    /// </summary>
    private static void Visit(Block b, float x0, float y0, float x1, float y1, Action<int> visit)
    {
        if (b.Tris.Length == 0 || x1 < b.BMinX || x0 > b.BMaxX || y1 < b.BMinY || y0 > b.BMaxY) return;
        int gx0 = Cell(x0), gx1 = Cell(x1), gy0 = Cell(y0), gy1 = Cell(y1);
        for (int gx = gx0; gx <= gx1; gx++)
            for (int gy = gy0; gy <= gy1; gy++)
                foreach (int i in b.Grid[gx * Side + gy])
                {
                    if (b.MaxX[i] < x0 || b.MinX[i] > x1 || b.MaxY[i] < y0 || b.MinY[i] > y1) continue;
                    if (Math.Max(Cell(b.MinX[i]), gx0) != gx || Math.Max(Cell(b.MinY[i]), gy0) != gy) continue;
                    visit(i);
                }
    }

    /// <summary>
    /// Runs <paramref name="each"/> for the landblock itself and every neighbour whose triangles may reach the
    /// box (given in <paramref name="landblock"/>'s frame), with that block and the shift from its frame into
    /// the landblock's (neighbour position + shift = position in the queried frame).
    /// </summary>
    private void ForBlocks(uint landblock, float x0, float y0, float x1, float y1, Action<Block, Vector3> each)
    {
        uint key = Key(landblock);
        int lx = (int)(key >> 8), ly = (int)(key & 0xFF);
        const float L = LandblockData.BlockLength;
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                int nx = lx + dx, ny = ly + dy;
                if (nx < 0 || ny < 0 || nx > 0xFF || ny > 0xFF) continue;
                float sx = dx * L, sy = dy * L;
                if (dx != 0 || dy != 0)
                {
                    // the box in the neighbour's frame must come within MaxOverhang of its square
                    float bx0 = x0 - sx, bx1 = x1 - sx, by0 = y0 - sy, by1 = y1 - sy;
                    if (bx1 < -MaxOverhang || bx0 > L + MaxOverhang || by1 < -MaxOverhang || by0 > L + MaxOverhang) continue;
                }
                each(Get((uint)(nx << 8 | ny)), new Vector3(sx, sy, 0));
            }
    }

    // ---- queries ----------------------------------------------------------------------

    /// <summary>The triangles of a landblock and its neighbours whose XY bounds come within <paramref name="radius"/> of (x, y).</summary>
    public IReadOnlyList<CollisionTri> Near(uint landblock, float x, float y, float radius)
    {
        var result = new List<CollisionTri>();
        float r2 = radius * radius;
        ForBlocks(landblock, x - radius, y - radius, x + radius, y + radius, (b, shift) =>
        {
            float qx = x - shift.X, qy = y - shift.Y;
            Visit(b, qx - radius, qy - radius, qx + radius, qy + radius, i =>
            {
                float ex = MathF.Max(0, MathF.Max(b.MinX[i] - qx, qx - b.MaxX[i]));
                float ey = MathF.Max(0, MathF.Max(b.MinY[i] - qy, qy - b.MaxY[i]));
                if (ex * ex + ey * ey > r2) return;
                result.Add(shift == Vector3.Zero ? b.Tris[i] : b.Tris[i].Shifted(shift));
            });
        });
        return result;
    }

    /// <summary>
    /// The nearest triangle the segment <paramref name="a"/> to <paramref name="b"/> hits (either face), and
    /// where: <paramref name="t"/> is the fraction along the segment (0..1). False when it hits nothing.
    /// </summary>
    public bool SegmentHit(uint landblock, Vector3 a, Vector3 b, out float t, out CollisionTri tri)
    {
        float bestT = float.MaxValue;
        CollisionTri best = default;
        float x0 = MathF.Min(a.X, b.X), x1 = MathF.Max(a.X, b.X), y0 = MathF.Min(a.Y, b.Y), y1 = MathF.Max(a.Y, b.Y);
        float zLo = MathF.Min(a.Z, b.Z), zHi = MathF.Max(a.Z, b.Z);
        ForBlocks(landblock, x0, y0, x1, y1, (blk, shift) =>
        {
            Vector3 pa = a - shift, pb = b - shift;
            Visit(blk, x0 - shift.X, y0 - shift.Y, x1 - shift.X, y1 - shift.Y, i =>
            {
                var tr = blk.Tris[i];
                if (tr.MaxZ < zLo || tr.MinZ > zHi) return;
                if (tr.Hit(pa, pb, out float ht) && ht < bestT)
                {
                    bestT = ht;
                    best = shift == Vector3.Zero ? tr : tr.Shifted(shift);
                }
            });
        });
        t = bestT == float.MaxValue ? 0 : bestT;
        tri = best;
        return bestT != float.MaxValue;
    }

    /// <summary>
    /// The highest walkable (normal.Z &gt;= FloorZ) object or building triangle under (x, y) whose height
    /// there is at most <paramref name="zTop"/> and above <paramref name="zTop"/> - <paramref name="maxDrop"/>;
    /// null when there is none. The terrain is not included (take the max with the terrain yourself).
    /// </summary>
    public float? SurfaceBelow(uint landblock, float x, float y, float zTop, float maxDrop)
    {
        float? best = null;
        float lo = zTop - maxDrop;
        ForBlocks(landblock, x, y, x, y, (b, shift) =>
        {
            float qx = x - shift.X, qy = y - shift.Y;
            Visit(b, qx, qy, qx, qy, i =>
            {
                var tr = b.Tris[i];
                if (!tr.Walkable || tr.MinZ > zTop || tr.MaxZ <= lo) return;
                if (!tr.ContainsXY(qx, qy)) return;
                float z = tr.PlaneZ(qx, qy);
                if (z > zTop || z <= lo) return;
                if (best == null || z > best.Value) best = z;
            });
        });
        return best;
    }

    /// <summary>The number of collision triangles of the landblock itself (no neighbours).</summary>
    public int Count(uint landblock) => Get(landblock).Tris.Length;

    /// <summary>Where the landblock's triangles came from.</summary>
    public CollisionCounts Counts(uint landblock) => Get(landblock).Counts;

    /// <summary>The landblock's own triangles, in its frame (no neighbours).</summary>
    public IReadOnlyList<CollisionTri> Triangles(uint landblock) => Get(landblock).Tris;

    /// <summary>The scenery placed in the landblock (as used for its collision).</summary>
    public IReadOnlyList<SceneryObject> SceneryOf(uint landblock) => Get(landblock).Scenery;
}
