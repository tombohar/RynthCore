// ============================================================================
//  RynthCore.Geometry - Terrain/LandblockData.cs
//  One outdoor landblock's terrain as the cell dat stores it: the 9x9 vertex
//  grid of terrain words (road bits, terrain type, scene type) and height
//  indices, plus the heights decoded through the region's land-height table.
//  Pure data: the triangle math (split-aware Z, normals, raycast, roads) lives
//  in TerrainSampler so there is exactly one implementation of it.
//
//  Moved from RynthSuite Shared/RynthCore.TerrainData/LandblockData.cs (72b9e26).
//  Changes on the move: namespace, nullable annotations, System.Numerics; the
//  old GetTerrainZLocal / RaycastTerrainLocal, which always split cells SW->NE
//  (wrong on about half the cells), were dropped in favour of the split-aware
//  TerrainSampler.GetTerrainZ / RaycastTerrain.
// ============================================================================

using RynthCore.Geometry.Dat;

namespace RynthCore.Geometry.Terrain;

/// <summary>
/// Parsed CellLandblock (cell dat file 0xXXYYFFFF): the terrain heightmap and terrain words of
/// one 192 x 192 m landblock.
///
/// File layout:
///   uint   Id              (0xXXYYFFFF)
///   uint   HasObjects      (nonzero if the matching 0xXXYYFFFE LandblockInfo exists)
///   ushort Terrain[81]     9x9 grid: roadBits (bits 0-1) | terrainType &lt;&lt; 2 (bits 2-6) | sceneType &lt;&lt; 11
///   byte   Height[81]      9x9 grid, indices into RegionDesc's LandHeightTable
///
/// Vertex indexing: idx = ix * 9 + iy, ix = 0..8 west to east, iy = 0..8 south to north.
/// Vertex (ix, iy) sits at landblock-local (ix * 24, iy * 24). Heights are absolute world Z.
/// </summary>
public sealed class LandblockData
{
    public const int VertexDim = 9;
    public const int BlockSide = 8;
    public const float CellLength = 24.0f;
    public const float BlockLength = 192.0f;

    /// <summary>0xXXYY landblock key (no cell word).</summary>
    public uint LandblockKey { get; }

    /// <summary>The landblock has a 0xXXYYFFFE LandblockInfo (buildings / static objects).</summary>
    public bool HasObjects { get; }

    /// <summary>81 terrain words, idx = ix * 9 + iy.</summary>
    public ushort[] TerrainWords { get; }

    /// <summary>81 raw height indices (into the land-height table), idx = ix * 9 + iy.</summary>
    public byte[] HeightIndices { get; }

    /// <summary>Decoded world Z of every vertex, HeightsZ[ix, iy].</summary>
    public float[,] HeightsZ { get; }

    /// <summary>World X of vertex (0, 0), the SW corner of the landblock (landblock X * 192).</summary>
    public float WorldOriginX => ((LandblockKey >> 8) & 0xFF) * BlockLength;

    /// <summary>World Y of vertex (0, 0), the SW corner of the landblock (landblock Y * 192).</summary>
    public float WorldOriginY => (LandblockKey & 0xFF) * BlockLength;

    private LandblockData(uint key, bool hasObjects, ushort[] terrain, byte[] heights, float[,] heightsZ)
    {
        LandblockKey = key;
        HasObjects = hasObjects;
        TerrainWords = terrain;
        HeightIndices = heights;
        HeightsZ = heightsZ;
    }

    /// <summary>
    /// The 0xXXYY key of any landblock or cell id: 0xXXYYnnnn gives 0xXXYY, a value that already
    /// fits in 16 bits is taken as the key itself. (So cell ids of landblock 0x0000, open ocean
    /// at the world corner, can't be passed this way; pass the key 0 instead.)
    /// </summary>
    public static uint KeyOf(uint landblockOrCellId) => landblockOrCellId > 0xFFFF ? landblockOrCellId >> 16 : landblockOrCellId;

    /// <summary>
    /// Loads and parses the 0xXXYYFFFF CellLandblock. <paramref name="landblock"/> is the 0xXXYY key
    /// or any 0xXXYYnnnn id (see <see cref="KeyOf"/>). Returns null if the file is missing or short.
    /// </summary>
    public static LandblockData? Load(DatDatabase cellDat, uint landblock, float[] landHeightTable)
    {
        ArgumentNullException.ThrowIfNull(cellDat);
        ArgumentNullException.ThrowIfNull(landHeightTable);

        uint key = KeyOf(landblock);
        byte[]? data = cellDat.GetFileData((key << 16) | 0xFFFF);
        return data == null ? null : Parse(key, data, landHeightTable);
    }

    /// <summary>
    /// Parses a CellLandblock record (the bytes of 0xXXYYFFFF). Returns null if it's too short.
    /// </summary>
    public static LandblockData? Parse(uint landblockKey, ReadOnlySpan<byte> data, float[] landHeightTable)
    {
        // 4 (Id) + 4 (HasObjects) + 81*2 (terrain) + 81 (heights) = 251
        const int Needed = 4 + 4 + 81 * 2 + 81;
        if (data.Length < Needed) return null;

        bool hasObjects = BitConverter.ToUInt32(data.Slice(4, 4)) != 0;
        var terrain = new ushort[81];
        for (int i = 0; i < 81; i++)
            terrain[i] = BitConverter.ToUInt16(data.Slice(8 + i * 2, 2));
        var heights = data.Slice(8 + 81 * 2, 81).ToArray();

        var heightsZ = new float[VertexDim, VertexDim];
        for (int ix = 0; ix < VertexDim; ix++)
            for (int iy = 0; iy < VertexDim; iy++)
            {
                byte h = heights[ix * VertexDim + iy];
                heightsZ[ix, iy] = h < landHeightTable.Length ? landHeightTable[h] : 0f;
            }

        return new LandblockData(KeyOf(landblockKey), hasObjects, terrain, heights, heightsZ);
    }

    /// <summary>Vertex Z at grid coords (0..8, 0..8); 0 outside the grid.</summary>
    public float GetVertexZ(int ix, int iy)
    {
        if ((uint)ix > BlockSide || (uint)iy > BlockSide) return 0f;
        return HeightsZ[ix, iy];
    }

    /// <summary>Terrain word at grid coords (0..8, 0..8); 0 outside the grid.</summary>
    public ushort GetTerrainWord(int ix, int iy)
    {
        if ((uint)ix > BlockSide || (uint)iy > BlockSide) return 0;
        return TerrainWords[ix * VertexDim + iy];
    }

    /// <summary>
    /// Z of the nearest grid vertex for a landblock-local position; coordinates outside the block
    /// are clamped to the edge. (A coarse lookup; TerrainSampler.GetTerrainZ is the exact one.)
    /// </summary>
    public float GetNearestVertexHeightLocal(float lx, float ly)
    {
        int ix = Math.Clamp((int)MathF.Round(lx / CellLength), 0, BlockSide);
        int iy = Math.Clamp((int)MathF.Round(ly / CellLength), 0, BlockSide);
        return HeightsZ[ix, iy];
    }

    /// <summary>Min and max vertex Z across the landblock.</summary>
    public void GetHeightRange(out float min, out float max)
    {
        min = float.PositiveInfinity;
        max = float.NegativeInfinity;
        foreach (float z in HeightsZ)
        {
            if (z < min) min = z;
            if (z > max) max = z;
        }
    }
}
