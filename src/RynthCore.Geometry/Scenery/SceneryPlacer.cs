// ============================================================================
//  RynthCore.Geometry - Scenery/SceneryPlacer.cs
//  The scenery (trees, rocks, bushes) of a landblock, placed as ACE's
//  Landblock.get_land_scenes places it on the server: per terrain vertex the
//  pseudo-random scene pick, each object's frequency and displacement, then
//  the rules that drop one: a weenie object, outside the landblock, on a
//  road (ACE OnRoad), in a 24 m land cell that holds a building, on a slope
//  outside the object's MinSlope..MaxSlope, or poking out of the landblock
//  (PhysicsObj.obj_within_block). Then the heading (slope-aligned or random),
//  and the object's scale (ObjectDesc.ScaleObj).
//
//  Logic from RynthSuite Tools/RynthNav.Baker/Scenery.cs (d4ae46a), on the
//  region tables of Plugins/RynthCore.Plugin.RynthAi/Raycasting/ScatterSystem.cs
//  (4b29610). Written for RynthCore (MIT).
// ============================================================================

using System.Collections.Concurrent;
using System.Numerics;
using RynthCore.Geometry.Dat;
using RynthCore.Geometry.Models;
using RynthCore.Geometry.Terrain;

namespace RynthCore.Geometry.Scenery;

/// <summary>
/// A piece of scenery placed in a landblock. <see cref="Position"/> is landblock-local (z on the terrain);
/// <see cref="Rotation"/> is the full placement rotation (from <see cref="Heading"/> when the object is
/// turned, else the Scene's base rotation); <see cref="Heading"/> is in AC degrees (0 = north, clockwise),
/// NaN when the object keeps its base rotation.
/// </summary>
public readonly record struct SceneryObject(uint ModelId, Vector3 Position, Quaternion Rotation, float Heading, float Scale);

/// <summary>Places a landblock's scenery. Thread-safe; nothing is cached per landblock (callers cache).</summary>
public sealed class SceneryPlacer
{
    private readonly DatDatabase _cell;
    private readonly DatDatabase _portal;
    private readonly TerrainSampler _terrain;
    private readonly ModelReader _models;
    private readonly Lazy<SceneryTables> _tables;
    private readonly ConcurrentDictionary<uint, ShapeInfo> _shapes = new();

    public SceneryPlacer(DatDatabase cell, DatDatabase portal, TerrainSampler terrain, ModelReader models)
    {
        _cell = cell;
        _portal = portal;
        _terrain = terrain;
        _models = models;
        _tables = new Lazy<SceneryTables>(() => new SceneryTables(portal), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The region's scene tables (read on first use).</summary>
    public SceneryTables Tables => _tables.Value;

    /// <summary>
    /// The scenery of landblock <paramref name="landblock"/> (0xXXYY or any 0xXXYYnnnn id); empty when the
    /// landblock or the region tables are missing.
    /// </summary>
    public List<SceneryObject> Place(uint landblock)
    {
        uint lb = LandblockData.KeyOf(landblock);
        var result = new List<SceneryObject>();
        var tables = Tables;
        var land = _terrain.LoadLandblock(lb);
        if (!tables.Ready || land == null) return result;

        var buildingCells = new HashSet<int>();
        var info = LandblockInfo.Load(_cell, lb);
        if (info != null)
            foreach (var b in info.Buildings)
                buildingCells.Add(Math.Clamp((int)(b.Origin.X / 24f), 0, 7) * 8 + Math.Clamp((int)(b.Origin.Y / 24f), 0, 7));

        ushort[] terrain = land.TerrainWords;
        uint blockX = ((lb >> 8) & 0xFF) * 8, blockY = (lb & 0xFF) * 8;
        unchecked
        {
            for (uint i = 0; i < 81; i++)
            {
                int terrainType = (terrain[i] >> 2) & 0x1F;
                int sceneType = terrain[i] >> 11;
                if (terrainType >= tables.TerrainTypes.Count) continue;
                var tt = tables.TerrainTypes[terrainType];
                if (sceneType >= tt.Count) continue;
                int sceneInfo = (int)tt[sceneType];
                if (sceneInfo < 0 || sceneInfo >= tables.SceneTypes.Count) continue;
                var scenes = tables.SceneTypes[sceneInfo];
                if (scenes.Count == 0) continue;

                uint cellX = i / 9, cellY = i % 9;
                uint gx = cellX + blockX, gy = cellY + blockY;
                uint cellMat = gy * (712977289u * gx + 1813693831u) - 1109124029u * gx + 2139937281u;
                double offset = cellMat * 2.3283064e-10;
                int sceneIdx = (int)(scenes.Count * offset);
                if (sceneIdx >= scenes.Count || sceneIdx < 0) sceneIdx = 0;
                var objs = tables.Scene(scenes[sceneIdx]);

                uint cellXMat = 0u - 1109124029u * gx;
                uint cellYMat = 1813693831u * gy;
                uint cellMat2 = 1360117743u * gx * gy + 1888038839u;
                for (uint j = 0; j < objs.Count; j++)
                {
                    var obj = objs[(int)j];
                    double noise = (uint)(cellXMat + cellYMat - cellMat2 * (23399u + j)) * 2.3283064e-10;
                    if (!(noise < obj.Freq) || obj.WeenieObj != 0) continue;

                    var pos = Displace(obj, gx, gy, j);
                    float lx = cellX * 24f + pos.X, ly = cellY * 24f + pos.Y;
                    if (lx < 0 || ly < 0 || lx >= 192f || ly >= 192f || TerrainSampler.OnRoad(land, lx, ly)) continue;
                    int cx = Math.Min((int)(lx / 24f), 7), cy = Math.Min((int)(ly / 24f), 7);
                    if (buildingCells.Contains(cx * 8 + cy)) continue;

                    Vector3 n = TerrainSampler.GetNormal(land, lx, ly);
                    if (!(n.Z >= obj.MinSlope && n.Z <= obj.MaxSlope)) continue;
                    float z = TerrainSampler.GetTerrainZ(land, lx, ly);

                    float heading = float.NaN;
                    if (obj.Align != 0) heading = Heading(-n.X, -n.Y);
                    else if (obj.MaxRotation > 0f)
                        heading = (float)((uint)(1813693831u * gy - (j + 63127u) * (1360117743u * gy * gx + 1888038839u) - 1109124029u * gx)
                                          * 2.3283064e-10 * obj.MaxRotation);
                    // AFrame.set_heading: heading 0 = north (+Y), clockwise: a rotation about Z by -heading.
                    Quaternion rot = float.IsNaN(heading)
                        ? obj.BaseRotation
                        : Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -heading * MathF.PI / 180f);
                    var origin = new Vector3(lx, ly, z);

                    if (!WithinBlock(origin, rot, Shape(obj.ObjId))) continue;
                    result.Add(new SceneryObject(obj.ObjId, origin, rot, heading, ScaleObj(obj, gx, gy, j)));
                }
            }
        }
        return result;
    }

    // ---- ACE ObjectDesc maths (uint arithmetic wraps, as in the client) -------------

    private static Vector3 Displace(SceneObjectDesc obj, uint ix, uint iy, uint iq)
    {
        unchecked
        {
            float x = obj.DisplaceX <= 0 ? obj.BaseOrigin.X
                : (float)((uint)(1813693831u * iy - (iq + 45773u) * (1360117743u * iy * ix + 1888038839u) - 1109124029u * ix) * 2.3283064e-10 * obj.DisplaceX + obj.BaseOrigin.X);
            float y = obj.DisplaceY <= 0 ? obj.BaseOrigin.Y
                : (float)((uint)(1813693831u * iy - (iq + 72719u) * (1360117743u * iy * ix + 1888038839u) - 1109124029u * ix) * 2.3283064e-10 * obj.DisplaceY + obj.BaseOrigin.Y);
            float z = obj.BaseOrigin.Z;
            double quadrant = (uint)(1813693831u * iy - ix * (1870387557u * iy + 1109124029u) - 402451965u) * 2.3283064e-10;
            if (quadrant >= 0.75) return new Vector3(y, -x, z);
            if (quadrant >= 0.5) return new Vector3(-x, -y, z);
            if (quadrant >= 0.25) return new Vector3(-y, x, z);
            return new Vector3(x, y, z);
        }
    }

    private static float ScaleObj(SceneObjectDesc obj, uint x, uint y, uint k)
    {
        if (obj.MinScale == obj.MaxScale) return obj.MaxScale;
        unchecked
        {
            double t = (uint)(1813693831u * y - (k + 32593u) * (1360117743u * y * x + 1888038839u) - 1109124029u * x) * 2.3283064e-10;
            return (float)(Math.Pow(obj.MaxScale / obj.MinScale, t) * obj.MinScale);
        }
    }

    /// <summary>Vector3.get_heading: degrees, 0 = +Y (north), clockwise.</summary>
    private static float Heading(float x, float y)
    {
        float len = MathF.Sqrt(x * x + y * y);
        if (len < 1e-6f) return 0;
        return (450f - MathF.Atan2(y / len, x / len) * 180f / MathF.PI) % 360f;
    }

    // ---- obj_within_block ---------------------------------------------------------

    /// <summary>What obj_within_block needs of a model: sorting sphere, physics BSP or not, cylinders, spheres.</summary>
    private readonly record struct ShapeInfo(Vector3 Center, float Radius, bool Bsp, (Vector3 Low, float R)[] Cylinders, bool Spheres);

    private ShapeInfo Shape(uint model) => _shapes.GetOrAdd(model, ReadShape);

    private ShapeInfo ReadShape(uint model)
    {
        var none = Array.Empty<(Vector3, float)>();
        var shape = _models.Get(model);
        if ((model >> 24) == 0x01)
        {
            if (shape.Polygons.Count == 0) return new ShapeInfo(Vector3.Zero, 0, false, none, false);
            Vector3 mn = new(float.MaxValue), mx = new(float.MinValue);
            foreach (var p in shape.Polygons) foreach (var v in p) { mn = Vector3.Min(mn, v); mx = Vector3.Max(mx, v); }
            return new ShapeInfo((mn + mx) / 2, (mx - mn).Length() / 2, true, none, false);
        }
        var b = SetupBounds.Read(_portal, model);
        if (b == null) return new ShapeInfo(Vector3.Zero, 0, shape.Polygons.Count > 0, none, false);
        return new ShapeInfo(b.SortCenter, b.SortRadius, shape.Polygons.Count > 0,
            b.Cylinders.Select(c => (c.Origin, c.Radius)).ToArray(), b.Spheres.Count > 0);
    }

    /// <summary>PhysicsObj.obj_within_block (ACE), before scaling, as the server checks it.</summary>
    private static bool WithinBlock(Vector3 origin, Quaternion rot, ShapeInfo s)
    {
        Vector3 G(Vector3 p) => Vector3.Transform(p, rot) + origin;
        bool Inside(Vector3 c, float r) => c.X >= r && c.Y >= r && c.X < 192f - r && c.Y < 192f - r;
        if (s.Bsp) return Inside(G(s.Center), s.Radius);
        if (s.Cylinders.Length > 0)
        {
            foreach (var (low, r) in s.Cylinders)
                if (!Inside(G(low), r)) return false;
            return true;
        }
        if (s.Spheres) return Inside(G(s.Center), s.Radius);
        return origin.X >= 0 && origin.Y >= 0 && origin.X < 192f && origin.Y < 192f;
    }
}
