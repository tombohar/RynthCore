using System;
using System.Collections.Generic;
using System.IO;

namespace NexSuite.Plugins.RynthAi.Raycasting
{
    /// <summary>
    /// Extracts dungeon wall geometry from cell.dat Environment files for raycasting.
    /// 
    /// Pipeline:
    ///   EnvCell (0xXXYY01nn) → EnvironmentId (0x0Dxxxx) + CellStructure index
    ///   Environment (0x0Dxxxx) → CellStruct at index → Vertices + Polygons
    ///   Transform polygon vertices by cell position → BoundingVolumes for raycasting
    /// 
    /// Binary formats (from ACE source):
    ///   SWVertex: ushort numUVs, float3 Origin, float3 Normal, Vec2Duv[numUVs]
    ///   CVertexArray: int32 type=1, uint32 count, (ushort key + SWVertex)[count]
    ///   Polygon: byte numPts, byte stippling, int32 sidesType, short posSurf, short negSurf,
    ///            short[numPts] vertexIds, byte[numPts] posUV (if !NoPos), byte[numPts] negUV (if clockwise+!NoNeg)
    ///   CellStruct: uint32 numPolygons, uint32 numPhysicsPolygons, uint32 numPortals,
    ///               CVertexArray, Dictionary{ushort,Polygon}[numPolygons], ...
    /// </summary>
    public class DungeonLOS
    {
        private DatDatabase _portalDat;
        private DatDatabase _cellDat;

        // Cache: EnvironmentId → parsed CellStruct geometry per structure index
        private readonly Dictionary<uint, Dictionary<uint, CellGeometry>> _envCache =
            new Dictionary<uint, Dictionary<uint, CellGeometry>>();

        // Cache: landblock → dungeon wall volumes
        private readonly Dictionary<uint, List<BoundingVolume>> _wallCache =
            new Dictionary<uint, List<BoundingVolume>>();

        private const int MAX_ENV_CACHE = 50;
        private const int MAX_WALL_CACHE = 20;

        public void Initialize(DatDatabase portalDat, DatDatabase cellDat)
        {
            _portalDat = portalDat;
            _cellDat = cellDat;
        }

        /// <summary>
        /// Gets dungeon wall collision volumes for a landblock.
        /// Returns empty list for outdoor-only landblocks.
        /// </summary>
        public List<BoundingVolume> GetDungeonWalls(uint landblockKey)
        {
            if (_wallCache.TryGetValue(landblockKey, out var cached))
                return cached;

            var walls = LoadDungeonWalls(landblockKey);

            if (_wallCache.Count >= MAX_WALL_CACHE)
            {
                foreach (var key in _wallCache.Keys) { _wallCache.Remove(key); break; }
            }
            _wallCache[landblockKey] = walls;
            return walls;
        }

        private List<BoundingVolume> LoadDungeonWalls(uint landblockKey)
        {
            var volumes = new List<BoundingVolume>();
            if (_cellDat == null || _portalDat == null) return volumes;

            uint baseId = landblockKey << 16;
            float globalOffsetX = ((landblockKey >> 8) & 0xFF) * 192.0f;
            float globalOffsetY = (landblockKey & 0xFF) * 192.0f;

            int consecutiveMisses = 0;
            for (uint cellNum = 0x0100; cellNum <= 0xFFFD; cellNum++)
            {
                uint cellId = baseId | cellNum;
                byte[] cellData = _cellDat.GetFileData(cellId);
                if (cellData == null)
                {
                    consecutiveMisses++;
                    if (consecutiveMisses > 10) break;
                    continue;
                }
                consecutiveMisses = 0;
                if (cellData.Length < 20) continue;

                try
                {
                    var envCell = ParseEnvCellHeader(cellData);
                    if (envCell == null) continue;

                    var cellGeo = GetCellGeometry(envCell.EnvironmentId, envCell.CellStructureIndex);
                    if (cellGeo == null || cellGeo.Polygons.Count == 0) continue;

                    foreach (var poly in cellGeo.Polygons)
                    {
                        if (poly.Vertices.Count < 3) continue;

                        // Transform vertices to world space
                        var worldVerts = new List<Vector3>();
                        foreach (var localVert in poly.Vertices)
                            worldVerts.Add(TransformVertex(localVert, envCell, globalOffsetX, globalOffsetY));

                        // Compute AABB from all vertices of the polygon
                        var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                        var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                        foreach (var wv in worldVerts)
                        {
                            min = Vector3.Min(min, wv);
                            max = Vector3.Max(max, wv);
                        }

                        var size = max - min;
                        if (size.X < 0.01f && size.Y < 0.01f && size.Z < 0.01f) continue;

                        // Thicken walls to seal corner gaps. Where two perpendicular walls meet,
                        // we may be missing one wall's geometry (unparsed CellStruct).
                        // 0.5 yards ensures the existing walls overlap at corners.
                        // Portal polygons are excluded so doorways stay open.
                        // 1.0 yard skin on thin axes — creates 2 yard total thickness.
                        // Seals corner jut-outs where perpendicular walls meet.
                        // Portal polygons are excluded so doorways stay open.
                        float wallSkin = 0.5f;
                        if (size.X < wallSkin) { min.X -= wallSkin; max.X += wallSkin; }
                        if (size.Y < wallSkin) { min.Y -= wallSkin; max.Y += wallSkin; }

                        volumes.Add(new BoundingVolume
                        {
                            Type = BoundingVolume.VolumeType.AxisAlignedBox,
                            Center = (min + max) * 0.5f,
                            Dimensions = max - min,
                            Min = min,
                            Max = max,
                            IsDoor = false
                        });
                    }
                }
                catch { }
            }

            if (volumes.Count > 0)
                Log($"Landblock 0x{landblockKey:X4}: {volumes.Count} dungeon wall volumes");

            return volumes;
        }

        private Vector3 TransformVertex(Vector3 local, EnvCellHeader cell, float gx, float gy)
        {
            Vector3 rotated = RotateByQuat(local, cell.RotW, cell.RotX, cell.RotY, cell.RotZ);
            return new Vector3(
                rotated.X + cell.PosX + gx,
                rotated.Y + cell.PosY + gy,
                rotated.Z + cell.PosZ
            );
        }

        private Vector3 RotateByQuat(Vector3 v, float qw, float qx, float qy, float qz)
        {
            float cx = qy * v.Z - qz * v.Y;
            float cy = qz * v.X - qx * v.Z;
            float cz = qx * v.Y - qy * v.X;
            float cx2 = qy * cz - qz * cy;
            float cy2 = qz * cx - qx * cz;
            float cz2 = qx * cy - qy * cx;
            return new Vector3(
                v.X + 2f * (qw * cx + cx2),
                v.Y + 2f * (qw * cy + cy2),
                v.Z + 2f * (qw * cz + cz2)
            );
        }

        // ===================================================================
        // EnvCell header parsing
        // ===================================================================

        private class EnvCellHeader
        {
            public uint EnvironmentId;
            public uint CellStructureIndex;
            public float PosX, PosY, PosZ;
            public float RotW, RotX, RotY, RotZ;
        }

        private EnvCellHeader ParseEnvCellHeader(byte[] data)
        {
            try
            {
                using (var ms = new MemoryStream(data))
                using (var reader = new BinaryReader(ms))
                {
                    uint id = reader.ReadUInt32();
                    uint flags = reader.ReadUInt32();
                    reader.ReadUInt32(); // duplicate CellId

                    byte numSurfaces = reader.ReadByte();
                    byte numPortals = reader.ReadByte();
                    ushort numVisibleCells = reader.ReadUInt16();

                    ms.Seek(numSurfaces * 2, SeekOrigin.Current);

                    ushort envIdShort = reader.ReadUInt16();
                    ushort cellStructure = reader.ReadUInt16();

                    return new EnvCellHeader
                    {
                        EnvironmentId = 0x0D000000u | envIdShort,
                        CellStructureIndex = cellStructure,
                        PosX = reader.ReadSingle(),
                        PosY = reader.ReadSingle(),
                        PosZ = reader.ReadSingle(),
                        RotW = reader.ReadSingle(),
                        RotX = reader.ReadSingle(),
                        RotY = reader.ReadSingle(),
                        RotZ = reader.ReadSingle()
                    };
                }
            }
            catch { return null; }
        }

        // ===================================================================
        // Environment + CellStruct parsing
        // ===================================================================

        private class CellGeometry
        {
            public List<CellPolygon> Polygons = new List<CellPolygon>();
        }

        private class CellPolygon
        {
            public List<Vector3> Vertices = new List<Vector3>();
        }

        private CellGeometry GetCellGeometry(uint environmentId, uint cellStructIndex)
        {
            if (_envCache.TryGetValue(environmentId, out var envCells))
            {
                if (envCells.TryGetValue(cellStructIndex, out var cached))
                    return cached;
            }

            byte[] envData = _portalDat.GetFileData(environmentId);
            if (envData == null || envData.Length < 8) return null;

            if (envCells == null)
            {
                envCells = ParseEnvironment(envData);
                if (_envCache.Count >= MAX_ENV_CACHE)
                {
                    foreach (var key in _envCache.Keys) { _envCache.Remove(key); break; }
                }
                _envCache[environmentId] = envCells;
            }

            envCells.TryGetValue(cellStructIndex, out var result);
            return result;
        }

        private Dictionary<uint, CellGeometry> ParseEnvironment(byte[] data)
        {
            var result = new Dictionary<uint, CellGeometry>();

            try
            {
                using (var ms = new MemoryStream(data))
                using (var reader = new BinaryReader(ms))
                {
                    uint id = reader.ReadUInt32();

                    // Dictionary<uint, CellStruct>
                    uint numCells = reader.ReadUInt32();
                    if (numCells > 200) return result;

                    for (uint i = 0; i < numCells; i++)
                    {
                        if (ms.Position + 4 > ms.Length) break;
                        uint key = reader.ReadUInt32();

                        var geo = ParseCellStruct(reader, ms);
                        if (geo != null)
                            result[key] = geo;
                        else
                        {
                            Log($"Environment 0x{id:X8}: failed parsing CellStruct {i} (key={key}), stopping");
                            break; // Stream position unknown — can't continue
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Environment parse error: {ex.Message}");
            }

            if (result.Count > 0)
                Log($"Environment: parsed {result.Count} CellStructs successfully");

            return result;
        }

        /// <summary>
        /// Parses CellStruct: vertices + rendering polygons + portal indices.
        /// NOW ALSO SKIPS BSP trees so we can continue to the next CellStruct.
        /// </summary>
        private CellGeometry ParseCellStruct(BinaryReader reader, MemoryStream ms)
        {
            var geo = new CellGeometry();

            try
            {
                uint numPolygons = reader.ReadUInt32();
                uint numPhysicsPolygons = reader.ReadUInt32();
                uint numPortals = reader.ReadUInt32();

                if (numPolygons > 5000 || numPhysicsPolygons > 5000) return null;

                var vertices = ParseVertexArray(reader, ms);
                if (vertices == null) return null;

                var polygons = ParsePolygons(reader, ms, numPolygons);
                if (polygons == null) return null;

                // Read portal polygon indices
                var portalIndices = new HashSet<ushort>();
                for (uint p = 0; p < numPortals && ms.Position + 2 <= ms.Length; p++)
                    portalIndices.Add(reader.ReadUInt16());

                // Align to 4-byte boundary
                long aligned = (ms.Position + 3) & ~3L;
                if (aligned <= ms.Length) ms.Position = aligned;

                // BUILD polygon geometry NOW, before BSP skip attempt.
                // If BSP skip fails we still have the wall data.
                for (int i = 0; i < polygons.Count; i++)
                {
                    var poly = polygons[i];
                    if (portalIndices.Contains(poly.Key)) continue;
                    if (poly.VertexIds == null || poly.VertexIds.Count < 3) continue;

                    var cellPoly = new CellPolygon();
                    foreach (var vid in poly.VertexIds)
                    {
                        if (vertices.ContainsKey(vid))
                            cellPoly.Vertices.Add(vertices[vid]);
                    }

                    if (cellPoly.Vertices.Count >= 3)
                        geo.Polygons.Add(cellPoly);
                }

                // Now attempt to skip BSP trees so ParseEnvironment can read the next CellStruct.
                // If this fails, we still have this CellStruct's geometry — we just can't parse further.
                bool canContinue = true;

                // Skip CellBSP
                if (!SkipBSPTree(reader, ms, BSPTreeType.Cell))
                    canContinue = false;

                // Skip PhysicsPolygons
                if (canContinue && !SkipPolygons(reader, ms, numPhysicsPolygons))
                    canContinue = false;

                // Skip PhysicsBSP
                if (canContinue && !SkipBSPTree(reader, ms, BSPTreeType.Physics))
                    canContinue = false;

                // Skip optional DrawingBSP
                if (canContinue && ms.Position + 4 <= ms.Length)
                {
                    uint hasDrawingBSP = reader.ReadUInt32();
                    if (hasDrawingBSP != 0)
                    {
                        if (!SkipBSPTree(reader, ms, BSPTreeType.Drawing))
                            canContinue = false;
                    }
                }

                // Final alignment
                if (canContinue)
                {
                    aligned = (ms.Position + 3) & ~3L;
                    if (aligned <= ms.Length) ms.Position = aligned;
                }

                // Return geometry. If canContinue is false, ParseEnvironment will break
                // at the next iteration (stream position wrong), but we keep this CellStruct's data.
                return geo;
            }
            catch
            {
                return geo.Polygons.Count > 0 ? geo : null;
            }
        }

        // ===================================================================
        // BSP Tree Skipping — matches ACE BSPNode/BSPLeaf exactly
        // ===================================================================

        private enum BSPTreeType { Cell, Physics, Drawing }

        // ACE's uint32 constants for BSP node types (read as LE uint32, stored reversed in file)
        private const uint BSP_PORT = 0x504F5254;
        private const uint BSP_LEAF = 0x4C454146;
        private const uint BSP_BPnn = 0x42506E6E;
        private const uint BSP_BPIn = 0x4250496E;
        private const uint BSP_BpIN = 0x4270494E;
        private const uint BSP_BpnN = 0x42706E4E;
        private const uint BSP_BPIN = 0x4250494E;
        private const uint BSP_BPnN = 0x42506E4E;

        private bool SkipBSPTree(BinaryReader reader, MemoryStream ms, BSPTreeType treeType)
        {
            return SkipBSPNode(reader, ms, treeType);
        }

        private bool SkipBSPNode(BinaryReader reader, MemoryStream ms, BSPTreeType treeType)
        {
            if (ms.Position + 4 > ms.Length) return false;
            uint typeTag = reader.ReadUInt32();

            if (typeTag == BSP_LEAF)
            {
                return SkipBSPLeaf(reader, ms, treeType);
            }

            if (typeTag == BSP_PORT)
            {
                // Portal node — same as leaf for our purposes
                return SkipBSPLeaf(reader, ms, treeType);
            }

            // Internal node: read splitting plane (float3 normal + float dist = 16 bytes)
            if (ms.Position + 16 > ms.Length) return false;
            ms.Seek(16, SeekOrigin.Current);

            // Read children based on type — matches ACE's switch exactly
            switch (typeTag)
            {
                case BSP_BPnn:
                case BSP_BPIn:
                    // Pos child only
                    if (!SkipBSPNode(reader, ms, treeType)) return false;
                    break;
                case BSP_BpIN:
                case BSP_BpnN:
                    // Neg child only
                    if (!SkipBSPNode(reader, ms, treeType)) return false;
                    break;
                case BSP_BPIN:
                case BSP_BPnN:
                    // Both children: Pos then Neg
                    if (!SkipBSPNode(reader, ms, treeType)) return false;
                    if (!SkipBSPNode(reader, ms, treeType)) return false;
                    break;
                default:
                    return false; // Unknown type
            }

            // Cell BSP internal nodes have NO sphere — done
            if (treeType == BSPTreeType.Cell)
                return true;

            // Physics and Drawing: read Sphere (float3 center + float radius = 16 bytes)
            if (ms.Position + 16 > ms.Length) return false;
            ms.Seek(16, SeekOrigin.Current);

            // Physics: done after sphere
            if (treeType == BSPTreeType.Physics)
                return true;

            // Drawing: also read InPolys (uint32 count + ushort[count])
            if (ms.Position + 4 > ms.Length) return false;
            uint numPolys = reader.ReadUInt32();
            if (numPolys > 100000) return false;
            long skip = numPolys * 2L; // ushort per poly
            if (ms.Position + skip > ms.Length) return false;
            ms.Seek(skip, SeekOrigin.Current);

            return true;
        }

        /// <summary>
        /// BSPLeaf: uint32 type + int32 leafIndex + (Physics only: int32 solid + Sphere(16) + uint32 numPolys + ushort[numPolys])
        /// Note: Physics leaf ALWAYS reads Sphere even when solid=0 (per ACE source).
        /// Cell and Drawing leaves are just type + leafIndex.
        /// </summary>
        private bool SkipBSPLeaf(BinaryReader reader, MemoryStream ms, BSPTreeType treeType)
        {
            // Type tag already read by caller. Read leafIndex.
            if (ms.Position + 4 > ms.Length) return false;
            reader.ReadInt32(); // leafIndex

            if (treeType == BSPTreeType.Physics)
            {
                // int32 Solid
                if (ms.Position + 4 > ms.Length) return false;
                reader.ReadInt32();

                // Sphere: ALWAYS read (16 bytes), even when solid=0
                if (ms.Position + 16 > ms.Length) return false;
                ms.Seek(16, SeekOrigin.Current);

                // uint32 numPolys + ushort[numPolys]
                if (ms.Position + 4 > ms.Length) return false;
                uint numPolys = reader.ReadUInt32();
                if (numPolys > 100000) return false;
                long skip = numPolys * 2L;
                if (ms.Position + skip > ms.Length) return false;
                ms.Seek(skip, SeekOrigin.Current);
            }
            // Cell and Drawing leaves: nothing more after leafIndex

            return true;
        }

        /// <summary>
        /// Skips a Dictionary{ushort, Polygon} block — same format as ParsePolygons
        /// but doesn't store results.
        /// </summary>
        private bool SkipPolygons(BinaryReader reader, MemoryStream ms, uint count)
        {
            for (uint i = 0; i < count; i++)
            {
                if (ms.Position + 12 > ms.Length) return false;
                ushort polyKey = reader.ReadUInt16();
                byte numPts = reader.ReadByte();
                byte stippling = reader.ReadByte();
                int sidesType = reader.ReadInt32();
                ms.Seek(4, SeekOrigin.Current); // posSurf + negSurf

                if (numPts == 0 || numPts > 50) return false;

                // Vertex IDs
                long vertBytes = numPts * 2L;
                if (ms.Position + vertBytes > ms.Length) return false;
                ms.Seek(vertBytes, SeekOrigin.Current);

                // PosUV
                if ((stippling & 0x04) == 0)
                {
                    if (ms.Position + numPts > ms.Length) return false;
                    ms.Seek(numPts, SeekOrigin.Current);
                }

                // NegUV
                if (sidesType == 2 && (stippling & 0x08) == 0)
                {
                    if (ms.Position + numPts > ms.Length) return false;
                    ms.Seek(numPts, SeekOrigin.Current);
                }
            }
            return true;
        }

        /// <summary>
        /// CVertexArray: int32 type(=1) + uint32 count + (ushort key + SWVertex)[count]
        /// SWVertex: ushort numUVs + float3 Origin(12) + float3 Normal(12) + numUVs × 8 bytes
        /// </summary>
        private Dictionary<ushort, Vector3> ParseVertexArray(BinaryReader reader, MemoryStream ms)
        {
            var verts = new Dictionary<ushort, Vector3>();

            int vertexType = reader.ReadInt32();
            if (vertexType != 1) return null;

            uint numVerts = reader.ReadUInt32();
            if (numVerts > 50000) return null;

            for (uint i = 0; i < numVerts; i++)
            {
                if (ms.Position + 28 > ms.Length) break; // key(2) + numUVs(2) + origin(12) + normal(12)
                ushort key = reader.ReadUInt16();
                ushort numUVs = reader.ReadUInt16();

                float ox = reader.ReadSingle();
                float oy = reader.ReadSingle();
                float oz = reader.ReadSingle();
                reader.ReadSingle(); reader.ReadSingle(); reader.ReadSingle(); // normal

                long uvBytes = numUVs * 8L;
                if (uvBytes > 0 && ms.Position + uvBytes <= ms.Length)
                    ms.Seek(uvBytes, SeekOrigin.Current);

                verts[key] = new Vector3(ox, oy, oz);
            }

            return verts.Count > 0 ? verts : null;
        }

        /// <summary>
        /// Dictionary{ushort, Polygon}: count × (ushort key + Polygon)
        /// Polygon: byte numPts, byte stippling, int32 sidesType, short posSurf, short negSurf,
        ///          short[numPts] vertexIds, optional UV index arrays
        /// </summary>
        private List<ParsedPolygon> ParsePolygons(BinaryReader reader, MemoryStream ms, uint count)
        {
            var polys = new List<ParsedPolygon>();

            for (uint i = 0; i < count; i++)
            {
                if (ms.Position + 12 > ms.Length) return polys; // key(2) + header(10 min)
                ushort polyKey = reader.ReadUInt16();

                byte numPts = reader.ReadByte();
                byte stippling = reader.ReadByte();
                int sidesType = reader.ReadInt32();
                short posSurf = reader.ReadInt16();
                short negSurf = reader.ReadInt16();

                if (numPts == 0 || numPts > 50) return polys;

                var poly = new ParsedPolygon { Key = polyKey, VertexIds = new List<ushort>() };

                for (int v = 0; v < numPts; v++)
                {
                    if (ms.Position + 2 > ms.Length) return polys;
                    poly.VertexIds.Add((ushort)reader.ReadInt16());
                }

                // Skip PosUVIndices (byte per point) unless NoPos flag (0x04)
                if ((stippling & 0x04) == 0)
                {
                    if (ms.Position + numPts > ms.Length) return polys;
                    ms.Seek(numPts, SeekOrigin.Current);
                }

                // Skip NegUVIndices if Clockwise(2) and not NoNeg(0x08)
                if (sidesType == 2 && (stippling & 0x08) == 0)
                {
                    if (ms.Position + numPts > ms.Length) return polys;
                    ms.Seek(numPts, SeekOrigin.Current);
                }

                polys.Add(poly);
            }

            return polys;
        }

        private class ParsedPolygon
        {
            public ushort Key;
            public List<ushort> VertexIds;
        }

        public void FlushCache()
        {
            _wallCache.Clear();
            _envCache.Clear();
        }

        private static void Log(string msg)
        {
            System.Diagnostics.Debug.WriteLine($"[DungeonLOS] {msg}");
        }
    }
}
