// ============================================================================
//  RynthCore.Geometry - Terrain/TerrainSampler.cs
//  Outdoor ground queries on AC's landscape mesh: terrain Z, triangle normal,
//  walkability, road test, terrain type and a ray-vs-terrain cast, all on the
//  triangle the game actually uses for each 24 m cell (the per-cell diagonal
//  split, ACE LandblockStruct.ConstructPolygons / FSplitNESW). Reads the land-
//  height table from RegionDesc 0x13000000 in the portal dat and keeps parsed
//  landblocks (cell dat 0xXXYYFFFF) in a small thread-safe LRU.
//
//  Moved from RynthSuite Shared/RynthCore.TerrainData/TerrainSampler.cs (8777fac),
//  with TerrainNormal and OnRoad from Tools/RynthNav.Baker/Scenery.cs (d4ae46a).
//  Changes on the move: built on caller-supplied (shareable) DatDatabase
//  instances instead of opening its own, System.Numerics.Vector3, the split-
//  aware raycast (the old LandblockData.RaycastTerrainLocal always split SW->NE)
//  now crosses into neighbouring landblocks.
// ============================================================================

using System.Numerics;
using RynthCore.Geometry.Dat;

namespace RynthCore.Geometry.Terrain;

/// <summary>
/// Terrain queries for AC's outdoor landscape.
///
/// Coordinates: a landblock is named by its 0xXXYY key, or by any 0xXXYYnnnn landblock/cell id
/// (the high word is used, see <see cref="LandblockData.KeyOf"/>). Positions are landblock-local:
/// X east and Y north in 0..192 from the landblock's SW corner, Z absolute (the same Z as
/// server positions). Each landblock is 8 x 8 cells of 24 m; each cell is two triangles, cut
/// along the diagonal <see cref="SwToNeCut"/> picks.
///
/// Thread-safety: all members can be called from any thread. The landblock cache is behind a
/// lock; the DatDatabase instances are thread-safe themselves.
/// </summary>
public sealed class TerrainSampler : IDisposable
{
    /// <summary>
    /// AC's walkable threshold (ACE PhysicsGlobals.FloorZ): a surface whose unit normal has Z
    /// below this is too steep to stand on (is_valid_walkable). About 48.4 degrees.
    /// </summary>
    public const float FloorZ = 0.66417414618662751f;

    /// <summary>RegionDesc record in the portal dat that holds the land-height table.</summary>
    public const uint RegionDescId = 0x13000000;

    private const float Cell = LandblockData.CellLength;
    private const float Block = LandblockData.BlockLength;

    private readonly DatDatabase _portal;
    private readonly DatDatabase _cell;
    private readonly bool _ownsDats;
    private readonly float[] _landHeightTable;

    // Parsed landblocks (null = not in the dat), least recently used out first. A load runs
    // outside the lock; two threads missing the same key both parse it and the later Set wins,
    // which is harmless.
    private readonly object _cacheGate = new();
    private readonly LruCache<uint, LandblockData?> _cache;

    /// <summary>
    /// A sampler over already-open dats, which stay the caller's (Dispose doesn't close them).
    /// Throws InvalidDataException if RegionDesc's land-height table can't be read.
    /// </summary>
    public TerrainSampler(DatDatabase portal, DatDatabase cell, int cacheCapacity = 64)
        : this(portal, cell, cacheCapacity, ownsDats: false) { }

    private TerrainSampler(DatDatabase portal, DatDatabase cell, int cacheCapacity, bool ownsDats)
    {
        ArgumentNullException.ThrowIfNull(portal);
        ArgumentNullException.ThrowIfNull(cell);
        _portal = portal;
        _cell = cell;
        _ownsDats = ownsDats;
        _landHeightTable = ReadLandHeightTable(portal)
            ?? throw new InvalidDataException($"RegionDesc 0x{RegionDescId:X8} land-height table not readable from {portal.FilePath}");
        // The default holds a 7x7-landblock window (an overlay of radius 3 landblocks) plus headroom.
        _cache = new LruCache<uint, LandblockData?>(cacheCapacity);
    }

    /// <summary>
    /// Opens client_portal.dat (or portal.dat) and client_cell_1.dat (or cell.dat) in
    /// <paramref name="acFolder"/> read-only; the sampler owns them and closes them on Dispose.
    /// Throws FileNotFoundException / InvalidDataException with the reason on failure.
    /// </summary>
    public static TerrainSampler Open(string acFolder, int cacheCapacity = 64)
    {
        string portalPath = FindDat(acFolder, "client_portal.dat", "portal.dat")
            ?? throw new FileNotFoundException($"no client_portal.dat / portal.dat in {acFolder}");
        string cellPath = FindDat(acFolder, "client_cell_1.dat", "cell.dat")
            ?? throw new FileNotFoundException($"no client_cell_1.dat / cell.dat in {acFolder}");

        var portal = new DatDatabase();
        var cell = new DatDatabase();
        try
        {
            if (!portal.Open(portalPath)) throw new InvalidDataException($"can't open {portalPath}: {Last(portal)}");
            if (!cell.Open(cellPath)) throw new InvalidDataException($"can't open {cellPath}: {Last(cell)}");
            return new TerrainSampler(portal, cell, cacheCapacity, ownsDats: true);
        }
        catch
        {
            portal.Dispose();
            cell.Dispose();
            throw;
        }

        static string Last(DatDatabase d) => d.DiagLog.Count > 0 ? d.DiagLog[^1] : "unknown error";
    }

    /// <summary>The 256-entry land-height table (height index to world Z).</summary>
    public IReadOnlyList<float> LandHeightTable => _landHeightTable;

    public DatDatabase PortalDat => _portal;
    public DatDatabase CellDat => _cell;

    /// <summary>Parsed landblock (heights + terrain words), cached. Null if the dat doesn't have it.</summary>
    public LandblockData? LoadLandblock(uint landblock)
    {
        uint key = LandblockData.KeyOf(landblock);
        lock (_cacheGate)
        {
            if (_cache.TryGet(key, out var cached)) return cached;
        }
        LandblockData? data = LandblockData.Load(_cell, key, _landHeightTable);
        lock (_cacheGate) _cache.Set(key, data);
        return data;
    }

    // ------------------------------------------------------------------ height

    /// <summary>Terrain Z at landblock-local (lx, ly), clamped to the block. NaN if the landblock isn't in the dat.</summary>
    public float GetTerrainZ(uint landblock, float localX, float localY)
    {
        var lb = LoadLandblock(landblock);
        return lb == null ? float.NaN : GetTerrainZ(lb, localX, localY);
    }

    /// <summary>
    /// Terrain Z at landblock-local (lx, ly) in [0, 192], on the triangle AC uses for that cell
    /// (<see cref="SwToNeCut"/>). Coordinates are clamped to the block.
    /// </summary>
    public static float GetTerrainZ(LandblockData lb, float localX, float localY)
        => GetTerrainZ(lb, localX, localY, invertSplit: false);

    /// <summary>GetTerrainZ with the split polarity optionally flipped (the tests use it to check the polarity).</summary>
    internal static float GetTerrainZ(LandblockData lb, float localX, float localY, bool invertSplit)
    {
        Locate(localX, localY, out int cx, out int cy, out float u, out float v);
        float h00 = lb.GetVertexZ(cx, cy);         // SW
        float h10 = lb.GetVertexZ(cx + 1, cy);     // SE
        float h01 = lb.GetVertexZ(cx, cy + 1);     // NW
        float h11 = lb.GetVertexZ(cx + 1, cy + 1); // NE

        if (SwToNeCut(lb.LandblockKey, cx, cy) != invertSplit)
        {
            // SE half (SW,SE,NE) when v <= u, NW half (SW,NE,NW) otherwise.
            return v <= u
                ? h00 + u * (h10 - h00) + v * (h11 - h10)
                : h00 + v * (h01 - h00) + u * (h11 - h01);
        }
        // SW half (SW,SE,NW) when u + v <= 1, NE half (SE,NE,NW) otherwise.
        return u + v <= 1f
            ? h00 + u * (h10 - h00) + v * (h01 - h00)
            : h11 + (1f - u) * (h01 - h11) + (1f - v) * (h10 - h11);
    }

    // ------------------------------------------------------------------ normal / slope

    /// <summary>Unit upward (Z &gt; 0) normal of the terrain triangle under (lx, ly). Zero vector if the landblock isn't in the dat.</summary>
    public Vector3 GetNormal(uint landblock, float localX, float localY)
    {
        var lb = LoadLandblock(landblock);
        return lb == null ? Vector3.Zero : GetNormal(lb, localX, localY);
    }

    /// <summary>
    /// Unit upward normal of the terrain triangle under landblock-local (lx, ly), on AC's per-cell
    /// split (Scenery.TerrainNormal in the RynthNav baker). Coordinates are clamped to the block.
    /// </summary>
    public static Vector3 GetNormal(LandblockData lb, float localX, float localY)
    {
        Locate(localX, localY, out int cx, out int cy, out float u, out float v);
        bool swToNe = SwToNeCut(lb.LandblockKey, cx, cy);
        int tri = swToNe ? (v <= u ? 0 : 1) : (u + v <= 1f ? 0 : 1);
        CellTriangle(lb, cx, cy, swToNe, tri, out var a, out var b, out var c);
        return UpNormal(a, b, c);
    }

    /// <summary>True if a surface with this unit normal is flat enough to stand on (normal.Z &gt;= <paramref name="floorZ"/>).</summary>
    public static bool IsWalkable(Vector3 normal, float floorZ = FloorZ) => normal.Z >= floorZ;

    /// <summary>
    /// Walkability of the two triangles of cell (cx, cy), 0..7. Which halves tri1/tri2 are depends
    /// on the split: swToNeCut true gives tri1 = SE half (SW,SE,NE), tri2 = NW half (SW,NE,NW);
    /// false gives tri1 = SW half (SW,SE,NW), tri2 = NE half (SE,NE,NW).
    /// </summary>
    public static void GetTrianglePassability(LandblockData lb, int cx, int cy, bool swToNeCut,
        out bool tri1Passable, out bool tri2Passable, float floorZ = FloorZ)
    {
        CellTriangle(lb, cx, cy, swToNeCut, 0, out var a, out var b, out var c);
        tri1Passable = UpNormal(a, b, c).Z >= floorZ;
        CellTriangle(lb, cx, cy, swToNeCut, 1, out a, out b, out c);
        tri2Passable = UpNormal(a, b, c).Z >= floorZ;
    }

    /// <summary>GetTrianglePassability on the cell's own split.</summary>
    public static void GetTrianglePassability(LandblockData lb, int cx, int cy,
        out bool tri1Passable, out bool tri2Passable, float floorZ = FloorZ)
        => GetTrianglePassability(lb, cx, cy, SwToNeCut(lb.LandblockKey, cx, cy),
            out tri1Passable, out tri2Passable, floorZ);

    // ------------------------------------------------------------------ split

    /// <summary>
    /// AC's per-cell diagonal split (ACE LandblockStruct.ConstructPolygons / FSplitNESW). True: the
    /// cell is cut SW to NE (triangles SW-SE-NE and SW-NE-NW); false: cut SE to NW (triangles
    /// SW-SE-NW and SE-NE-NW). Roughly half the cells each way.
    ///
    /// The PRNG inputs are the cell's vertex-grid coordinates (landblock index * 8 + cell index),
    /// matching ACE's blockid_to_lcoord (LandblockShift = 3), not raw landblock indices.
    /// <paramref name="landblock"/> is the 0xXXYY key or any 0xXXYYnnnn id.
    /// </summary>
    public static bool SwToNeCut(uint landblock, int cellX, int cellY)
    {
        uint key = LandblockData.KeyOf(landblock);
        int lbX = (int)((key >> 8) & 0xFF);
        int lbY = (int)(key & 0xFF);
        int vertexX = (lbX << 3) + cellX;
        int vertexY = (lbY << 3) + cellY;
        unchecked
        {
            uint magicA = (uint)vertexX * 214614067u + 1813693831u;
            uint magicB = (uint)vertexX * 1109124029u;
            uint splitDir = (uint)vertexY * magicA - magicB - 1369149221u;
            return (splitDir & 0x80000000u) != 0;
        }
    }

    // ------------------------------------------------------------------ roads / terrain type

    /// <summary>True if (lx, ly) is on a road (ACE Landblock.OnRoad). False if the landblock isn't in the dat.</summary>
    public bool OnRoad(uint landblock, float localX, float localY)
    {
        var lb = LoadLandblock(landblock);
        return lb != null && OnRoad(lb, localX, localY);
    }

    /// <summary>
    /// ACE Landblock.OnRoad: from the road bits of the four corners of the 24 m cell under
    /// (lx, ly), with a 5 m road width. Used by the server to keep scenery off roads.
    /// </summary>
    public static bool OnRoad(LandblockData lb, float localX, float localY)
    {
        const float rMin = 5f, rMax = Cell - rMin;
        int x = (int)(localX / Cell), y = (int)(localY / Cell);
        uint r0 = RoadBits(lb, x, y), r1 = RoadBits(lb, x, y + 1), r2 = RoadBits(lb, x + 1, y), r3 = RoadBits(lb, x + 1, y + 1);
        if (r0 == 0 && r1 == 0 && r2 == 0 && r3 == 0) return false;
        float dx = localX - x * Cell, dy = localY - y * Cell;
        if (r0 > 0)
        {
            if (r1 > 0)
            {
                if (r2 > 0) return r3 > 0 || dx < rMin || dy < rMin;
                return r3 > 0 ? (dx < rMin || dy > rMax) : dx < rMin;
            }
            if (r2 > 0) return r3 > 0 ? (dx > rMax || dy < rMin) : dy < rMin;
            return r3 > 0 ? MathF.Abs(dx - dy) < rMin : dx + dy < rMin;
        }
        if (r1 > 0)
        {
            if (r2 > 0) return r3 > 0 ? (dx > rMax || dy > rMax) : MathF.Abs(dx + dy - Cell) < rMin;
            return r3 > 0 ? dy > rMax : Cell + dx - dy < rMin;
        }
        if (r2 > 0) return r3 > 0 ? dx > rMax : Cell - dx + dy < rMin;
        return r3 > 0 && Cell * 2f - dx - dy < rMin;
    }

    /// <summary>Road bits (terrain word bits 0-1) of vertex (ix, iy); 0 outside 0..8.</summary>
    public static uint RoadBits(LandblockData lb, int ix, int iy) => (uint)(lb.GetTerrainWord(ix, iy) & 3);

    /// <summary>
    /// Terrain type (0-31: grass, water types, ...) of the grid vertex nearest (lx, ly).
    /// -1 if the landblock isn't in the dat.
    /// </summary>
    public int TerrainType(uint landblock, float localX, float localY)
    {
        var lb = LoadLandblock(landblock);
        if (lb == null) return -1;
        int ix = Math.Clamp((int)MathF.Round(localX / Cell), 0, LandblockData.BlockSide);
        int iy = Math.Clamp((int)MathF.Round(localY / Cell), 0, LandblockData.BlockSide);
        return TerrainType(lb, ix, iy);
    }

    /// <summary>Terrain type (terrain word bits 2-6) of vertex (ix, iy), 0..8; -1 outside the grid.</summary>
    public static int TerrainType(LandblockData lb, int ix, int iy)
    {
        if ((uint)ix > LandblockData.BlockSide || (uint)iy > LandblockData.BlockSide) return -1;
        return (lb.GetTerrainWord(ix, iy) >> 2) & 0x1F;
    }

    // ------------------------------------------------------------------ water

    /// <summary>True for the water terrain types: WaterRunning (16) .. WaterDeepSea (20).</summary>
    public static bool IsWaterTerrain(int terrainType) => terrainType >= 16 && terrainType <= 20;

    /// <summary>
    /// Water type of land cell (cx, cy), 0..7, from its four corner vertices (ACE
    /// LandblockStruct.CalcCellWater): all four water terrain = EntirelyWater, some = PartiallyWater.
    /// </summary>
    public static WaterType GetWaterType(LandblockData lb, int cx, int cy)
    {
        int water = 0;
        if (IsWaterTerrain(TerrainType(lb, cx, cy))) water++;
        if (IsWaterTerrain(TerrainType(lb, cx + 1, cy))) water++;
        if (IsWaterTerrain(TerrainType(lb, cx, cy + 1))) water++;
        if (IsWaterTerrain(TerrainType(lb, cx + 1, cy + 1))) water++;
        return water == 0 ? WaterType.NotWater : water == 4 ? WaterType.EntirelyWater : WaterType.PartiallyWater;
    }

    /// <summary>
    /// How far an object standing in the land cell under (lx, ly) sinks below the terrain mesh
    /// (ACE ObjCell.get_water_depth): 0 on dry cells, 0.45 m in a partially-water cell, 0.9 m in
    /// an entirely-water cell. 0 if the landblock isn't in the dat.
    /// </summary>
    public float GetWaterDepth(uint landblock, float localX, float localY)
    {
        var lb = LoadLandblock(landblock);
        return lb == null ? 0f : GetWaterDepth(lb, localX, localY);
    }

    /// <inheritdoc cref="GetWaterDepth(uint, float, float)"/>
    public static float GetWaterDepth(LandblockData lb, float localX, float localY)
    {
        Locate(localX, localY, out int cx, out int cy, out _, out _);
        return GetWaterType(lb, cx, cy) switch
        {
            WaterType.EntirelyWater => 0.9f,
            WaterType.PartiallyWater => 0.45f,
            _ => 0f,
        };
    }

    /// <summary>
    /// Z at which an object stands on open ground at (lx, ly): the terrain mesh Z less the cell's
    /// water depth. (Buildings, porches and other objects can hold it higher; they aren't tested.)
    /// NaN if the landblock isn't in the dat.
    /// </summary>
    public float GetStandingZ(uint landblock, float localX, float localY)
    {
        var lb = LoadLandblock(landblock);
        return lb == null ? float.NaN : GetTerrainZ(lb, localX, localY) - GetWaterDepth(lb, localX, localY);
    }

    // ------------------------------------------------------------------ raycast

    /// <summary>
    /// Nearest hit of a ray with the terrain mesh. <paramref name="localOrigin"/> is relative to
    /// <paramref name="landblock"/>'s SW corner (X/Y local, normally 0..192 but may lie outside;
    /// Z absolute). <paramref name="dir"/> needn't be unit length; <paramref name="dist"/> is in
    /// metres along it. The ray walks the 24 m cell grid (2D DDA) and tests the two triangles of
    /// each cell on the cell's real split (Moller-Trumbore, both faces). It is not limited to the
    /// starting landblock: cells past the edge are looked up in the neighbouring landblocks, and
    /// a landblock missing from the dat (or off the world) is passed over as having no ground.
    /// <paramref name="normal"/> is the unit upward normal of the triangle hit.
    /// Terrain only: buildings, objects and dungeon cells are not tested.
    /// </summary>
    public bool RaycastTerrain(uint landblock, Vector3 localOrigin, Vector3 dir, float maxDist,
        out float dist, out Vector3 normal)
    {
        dist = 0f;
        normal = Vector3.Zero;
        float len = dir.Length();
        if (!(len > 1e-12f) || !(maxDist > 0f) || !float.IsFinite(localOrigin.X + localOrigin.Y + localOrigin.Z)) return false;
        dir /= len;

        uint key = LandblockData.KeyOf(landblock);
        int lbX = (int)((key >> 8) & 0xFF), lbY = (int)(key & 0xFF);

        float px = localOrigin.X, py = localOrigin.Y;
        int cx = (int)MathF.Floor(px / Cell);
        int cy = (int)MathF.Floor(py / Cell);
        int stepX = dir.X > 0 ? 1 : (dir.X < 0 ? -1 : 0);
        int stepY = dir.Y > 0 ? 1 : (dir.Y < 0 ? -1 : 0);

        float tMaxX = float.PositiveInfinity, tDeltaX = float.PositiveInfinity;
        float tMaxY = float.PositiveInfinity, tDeltaY = float.PositiveInfinity;
        if (stepX != 0)
        {
            tMaxX = ((stepX > 0 ? cx + 1 : cx) * Cell - px) / dir.X;
            tDeltaX = Cell / MathF.Abs(dir.X);
        }
        if (stepY != 0)
        {
            tMaxY = ((stepY > 0 ? cy + 1 : cy) * Cell - py) / dir.Y;
            tDeltaY = Cell / MathF.Abs(dir.Y);
        }

        // Cells a ray of this length can cross, plus slack for float error at the boundaries.
        float span = maxDist * (MathF.Abs(dir.X) + MathF.Abs(dir.Y)) / Cell;
        int iterLimit = (int)MathF.Min(span, 1_000_000f) + 4;

        float bestT = maxDist;
        bool found = false;
        LandblockData? lb = null;
        int lbCurX = int.MinValue, lbCurY = int.MinValue;

        while (iterLimit-- > 0)
        {
            int dlx = FloorDiv(cx, LandblockData.BlockSide), dly = FloorDiv(cy, LandblockData.BlockSide);
            int nx = lbX + dlx, ny = lbY + dly;
            if (nx != lbCurX || ny != lbCurY)
            {
                lbCurX = nx;
                lbCurY = ny;
                lb = (uint)nx <= 0xFF && (uint)ny <= 0xFF ? LoadLandblock((uint)((nx << 8) | ny)) : null;
            }

            if (lb != null)
            {
                int lcx = cx - dlx * LandblockData.BlockSide, lcy = cy - dly * LandblockData.BlockSide;
                bool swToNe = SwToNeCut(lb.LandblockKey, lcx, lcy);
                // Cell-local frame (SW vertex at the origin) keeps the float math small.
                float baseZ = lb.GetVertexZ(lcx, lcy);
                var o = new Vector3(px - cx * Cell, py - cy * Cell, localOrigin.Z - baseZ);
                for (int tri = 0; tri < 2; tri++)
                {
                    CellTriangle(lb, lcx, lcy, swToNe, tri, out var a, out var b, out var c);
                    a.Z -= baseZ; b.Z -= baseZ; c.Z -= baseZ;
                    if (RayTriangle(o, dir, a, b, c, out float t) && t <= bestT)
                    {
                        bestT = t;
                        normal = UpNormal(a, b, c);
                        found = true;
                    }
                }
            }

            float tNext = MathF.Min(tMaxX, tMaxY);
            // A hit in this cell lies before the ray leaves it, so it's the nearest one.
            if (found && bestT <= tNext + 1e-4f) break;
            if (tNext > maxDist) break;
            if (stepX == 0 && stepY == 0) break; // vertical ray: one cell

            if (tMaxX < tMaxY) { tMaxX += tDeltaX; cx += stepX; }
            else { tMaxY += tDeltaY; cy += stepY; }
        }

        if (!found) { normal = Vector3.Zero; return false; }
        dist = bestT;
        return true;
    }

    // ------------------------------------------------------------------ height table

    /// <summary>
    /// The 256-entry land-height table from RegionDesc 0x13000000: header (id, region number,
    /// version), the region name (PString), the eight LandDefs scalars, then 256 floats.
    /// Null if the record is missing or short.
    /// </summary>
    public static float[]? ReadLandHeightTable(DatDatabase portal)
    {
        byte[]? data = portal.GetFileData(RegionDescId);
        // 12 (header) + >=2 (PString) + 32 (LandDefs prefix) + 256*4 (table)
        if (data == null || data.Length < 12 + 2 + 32 + 256 * 4) return null;

        int pos = 12;                                          // id, regionNumber, version
        int nameLen = BitConverter.ToUInt16(data, pos);        // RegionName: ushort length + bytes,
        pos = (pos + 2 + nameLen + 3) & ~3;                    // padded to 4 bytes
        pos += 32;   // LandDefs: NumBlockLength, NumBlockWidth, SquareLength, LBlockLength,
                     // VertexPerCell, MaxObjHeight, SkyHeight, RoadWidth
        if (pos + 256 * 4 > data.Length) return null;

        var table = new float[256];
        for (int i = 0; i < 256; i++) table[i] = BitConverter.ToSingle(data, pos + i * 4);
        return table;
    }

    // ------------------------------------------------------------------ helpers

    private static void Locate(float lx, float ly, out int cx, out int cy, out float u, out float v)
    {
        lx = float.IsNaN(lx) ? 0f : Math.Clamp(lx, 0f, Block);
        ly = float.IsNaN(ly) ? 0f : Math.Clamp(ly, 0f, Block);
        cx = Math.Min((int)(lx / Cell), LandblockData.BlockSide - 1);
        cy = Math.Min((int)(ly / Cell), LandblockData.BlockSide - 1);
        u = (lx - cx * Cell) / Cell;
        v = (ly - cy * Cell) / Cell;
    }

    /// <summary>
    /// Triangle <paramref name="tri"/> (0 or 1) of cell (cx, cy), in the cell's local XY (0 or 24)
    /// with absolute Z, wound counter-clockwise seen from above. swToNe: 0 = SE half (SW,SE,NE),
    /// 1 = NW half (SW,NE,NW); otherwise 0 = SW half (SW,SE,NW), 1 = NE half (SE,NE,NW).
    /// </summary>
    private static void CellTriangle(LandblockData lb, int cx, int cy, bool swToNe, int tri,
        out Vector3 a, out Vector3 b, out Vector3 c)
    {
        var sw = new Vector3(0f, 0f, lb.GetVertexZ(cx, cy));
        var se = new Vector3(Cell, 0f, lb.GetVertexZ(cx + 1, cy));
        var nw = new Vector3(0f, Cell, lb.GetVertexZ(cx, cy + 1));
        var ne = new Vector3(Cell, Cell, lb.GetVertexZ(cx + 1, cy + 1));
        if (swToNe)
        {
            if (tri == 0) { a = sw; b = se; c = ne; } else { a = sw; b = ne; c = nw; }
        }
        else
        {
            if (tri == 0) { a = sw; b = se; c = nw; } else { a = se; b = ne; c = nw; }
        }
    }

    private static Vector3 UpNormal(Vector3 a, Vector3 b, Vector3 c)
    {
        var n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        return n.Z < 0 ? -n : n;
    }

    // Moller-Trumbore, both faces; t along a unit direction.
    private static bool RayTriangle(Vector3 origin, Vector3 dir, Vector3 v0, Vector3 v1, Vector3 v2, out float t)
    {
        t = 0f;
        const float Epsilon = 1e-7f;
        Vector3 edge1 = v1 - v0;
        Vector3 edge2 = v2 - v0;
        Vector3 h = Vector3.Cross(dir, edge2);
        float a = Vector3.Dot(edge1, h);
        if (a > -Epsilon && a < Epsilon) return false;
        float f = 1f / a;
        Vector3 s = origin - v0;
        float u = f * Vector3.Dot(s, h);
        if (u < 0f || u > 1f) return false;
        Vector3 q = Vector3.Cross(s, edge1);
        float v = f * Vector3.Dot(dir, q);
        if (v < 0f || u + v > 1f) return false;
        t = f * Vector3.Dot(edge2, q);
        return t >= 0f;
    }

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

    private static string? FindDat(string folder, params string[] names)
    {
        foreach (var n in names)
        {
            string p = Path.Combine(folder, n);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Drops the cache; closes the dats only if <see cref="Open"/> opened them.</summary>
    public void Dispose()
    {
        lock (_cacheGate) _cache.Clear();
        if (_ownsDats)
        {
            _portal.Dispose();
            _cell.Dispose();
        }
    }
}

/// <summary>Water in a 24 m land cell (ACE LandDefs.WaterType).</summary>
public enum WaterType
{
    NotWater = 0,
    PartiallyWater = 1,
    EntirelyWater = 2,
}
