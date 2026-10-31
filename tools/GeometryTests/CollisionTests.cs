// ============================================================================
//  GeometryTests - CollisionTests.cs
//  Collision/OutdoorCollision and Scenery/SceneryPlacer against the real dats
//  and the creature positions captured from ACE in five towns
//  (Vectors\outdoor-towns.txt): creatures standing on porches and platforms
//  must have a walkable object surface at their feet, creatures on the ground
//  must have none just above it, and nobody stands inside a wall.
// ============================================================================

using System.Diagnostics;
using System.Numerics;
using RynthCore.Geometry.Cells;
using RynthCore.Geometry.Collision;
using RynthCore.Geometry.Dat;
using RynthCore.Geometry.Models;
using RynthCore.Geometry.Scenery;
using RynthCore.Geometry.Terrain;

namespace GeometryTests;

internal static class CollisionTests
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Register() => Suites.Add("collision", Run, order: 30);

    private const uint Holtburg = 0xA9B4;

    private static void Run()
    {
        using var cellDat = new DatDatabase();
        using var portalDat = new DatDatabase();
        T.Check(cellDat.Open(Dats.Cell), "cell dat opens");
        T.Check(portalDat.Open(Dats.Portal), "portal dat opens");
        using var terrain = new TerrainSampler(portalDat, cellDat);
        var models = new ModelReader(portalDat);
        var indoor = new IndoorWorld(cellDat, portalDat, models);
        var oc = new OutdoorCollision(cellDat, portalDat, terrain, models, indoor);

        var outdoor = Dats.Vectors("outdoor-towns.txt").Where(v => (v.Cell & 0xFFFF) < 0x100).ToList();
        T.Check(outdoor.Count > 50, $"outdoor vectors loaded ({outdoor.Count})");

        BuildsHoltburg(oc);
        BuildsInParallel(oc, outdoor);
        RaisedSurfaces(oc, terrain, outdoor);
        NotInsideSolids(oc, outdoor);
        Segments(oc, cellDat, outdoor);
        NeighbourEdges(oc, outdoor.Select(v => v.Cell >> 16).Distinct());
        SceneryRules(oc, terrain, cellDat, indoor, models);
        SetupTails(portalDat);
    }

    private static void BuildsHoltburg(OutdoorCollision oc)
    {
        var sw = Stopwatch.StartNew();
        var c = oc.Counts(Holtburg);
        T.Info($"landblock {Holtburg:X4} built in {sw.ElapsedMilliseconds} ms: {c}");
        T.Info($"region scene tables: {oc.Scenery.Tables.SceneTypes.Count} scene types, {oc.Scenery.Tables.TerrainTypes.Count} terrain types" +
               (oc.Scenery.Tables.Scanned ? " (found by scanning)" : " (structured read)"));
        T.Check(oc.Scenery.Tables.Ready, "region scene tables read");
        T.Check(c.StaticObjects > 0 && c.StaticTris > 0, "Holtburg has static objects with collision");
        T.Check(c.Buildings > 0 && c.BuildingTris > 0, "Holtburg has building shells with collision");
        T.Check(c.BuildingCells > 0 && c.BuildingCellTris > 0, "Holtburg has building cells seen from outside");
        T.Check(c.Total == oc.Count(Holtburg), "Count equals the per-kind total");
        T.Check(ReferenceEquals(oc.Triangles(Holtburg), oc.Triangles(Holtburg << 16 | 0x0021)), "landblock is cached (same array by key or cell id)");

        var tris = oc.Triangles(Holtburg);
        int badNormal = tris.Count(t => MathF.Abs(t.Normal.Length() - 1) > 1e-3f);
        int disagree = tris.Count(t =>
        {
            var n = Vector3.Cross(t.B - t.A, t.C - t.A);
            return n.LengthSquared() > 1e-8f && Vector3.Dot(Vector3.Normalize(n), t.Normal) < 0.99f;
        });
        T.Check(badNormal == 0, $"{badNormal} triangles without a unit normal");
        T.Check(disagree == 0, $"{disagree} triangles whose normal disagrees with their vertex order");
        T.Info($"{tris.Count(t => t.Walkable)} of {tris.Count} triangles are walkable");
    }

    // Several landblocks built from parallel threads: each is built once and the results agree.
    private static void BuildsInParallel(OutdoorCollision oc, List<(uint Cell, float X, float Y, float Z)> outdoor)
    {
        var lbs = outdoor.Select(v => v.Cell >> 16).Distinct().ToArray();
        var sw = Stopwatch.StartNew();
        var arrays = new IReadOnlyList<CollisionTri>[lbs.Length * 4];
        Parallel.For(0, arrays.Length, i => arrays[i] = oc.Triangles(lbs[i % lbs.Length]));
        bool same = true;
        for (int i = lbs.Length; i < arrays.Length; i++) same &= ReferenceEquals(arrays[i], arrays[i % lbs.Length]);
        T.Check(same, "parallel callers get the same cached triangles");
        T.Info($"{lbs.Length} landblocks built in parallel in {sw.ElapsedMilliseconds} ms: " +
               string.Join(", ", lbs.Select(lb => $"{lb:X4} {oc.Count(lb)}")));

        // How far a landblock's triangles reach past its edge (queries look MaxOverhang into neighbours).
        float over = 0; uint worst = 0;
        foreach (uint lb in lbs)
            foreach (var t in oc.Triangles(lb))
            {
                float o = MathF.Max(MathF.Max(-t.MinX, t.MaxX - 192), MathF.Max(-t.MinY, t.MaxY - 192));
                if (o > over) { over = o; worst = t.Source; }
            }
        T.Info($"largest overhang past a landblock edge: {over:F1} m (source {worst:X8})");
        T.Check(over <= OutdoorCollision.MaxOverhang, $"overhang {over:F1} m is within MaxOverhang");
    }

    // Creatures above the terrain stand on an object's walkable surface; creatures on the terrain have no
    // walkable surface just above the ground (a phantom floor would lift the walker).
    private static void RaisedSurfaces(OutdoorCollision oc, TerrainSampler terrain, List<(uint Cell, float X, float Y, float Z)> outdoor)
    {
        int raised = 0, found = 0, ground = 0, phantom = 0, water = 0;
        foreach (var (cell, x, y, z) in outdoor)
        {
            uint lb = cell >> 16;
            float standZ = terrain.GetStandingZ(lb, x, y), meshZ = terrain.GetTerrainZ(lb, x, y);
            if (z - standZ > 0.1f && z - meshZ <= 0.1f)
            {
                // in water: above the standing Z (mesh less water depth) but not above the terrain mesh
                water++;
                T.Info($"in water {cell:X8} ({x:F2}, {y:F2}, {z:F3}): terrain mesh {meshZ:F3}, standing {standZ:F3} (not an object surface)");
            }
            if (z - standZ > 0.1f && z - meshZ > 0.1f)
            {
                raised++;
                float? s = oc.SurfaceBelow(lb, x, y, z + 0.5f, 3f);
                if (s != null && MathF.Abs(s.Value - z) <= 0.15f) found++;
                else
                {
                    var near = oc.Near(lb, x, y, 1f);
                    T.Info($"raised miss {cell:X8} ({x:F2}, {y:F2}, {z:F3}) terrain {standZ:F3}: surface {(s == null ? "none" : s.Value.ToString("F3"))}; " +
                           $"{near.Count} tris within 1 m ({near.Count(t => t.Walkable)} walkable" +
                           (near.Count > 0 ? ", sources " + string.Join(" ", near.Select(t => t.Source.ToString("X8")).Distinct().Take(6)) : "") + ")");
                }
            }
            else if (z - standZ <= 0.1f)
            {
                ground++;
                float top = z + 0.3f, lo = standZ + 0.1f;
                float? s = top > lo ? oc.SurfaceBelow(lb, x, y, top, top - lo) : null;
                if (s != null)
                {
                    phantom++;
                    T.Info($"phantom floor {cell:X8} ({x:F2}, {y:F2}, {z:F3}) terrain {standZ:F3}: surface {s.Value:F3}");
                }
            }
        }
        float rate = raised == 0 ? 1 : found / (float)raised;
        T.Info($"raised vectors: {found}/{raised} have a walkable surface within 0.15 m of their z ({rate:P0}); " +
               $"ground vectors: {phantom}/{ground} have a walkable surface 0.1..0.3 m above the ground; {water} in water");
        // Known misses (2026-10-28): two creatures in C6A9 at (89.47, 45.20) and (89.39, 52.51) stand 0.22 and
        // 0.26 m above the top of a crate (static Setup 0x02000275, a 0.88 x 0.88 x 1 m physics box, top
        // 43.025) with nothing else under them: most likely spawn positions that never settled.
        T.Check(raised >= 15, $"enough raised vectors ({raised})");
        T.Check(rate >= 0.9f, $"raised vectors find their surface ({found}/{raised})");
        T.Check(phantom <= ground / 20, $"few phantom floors ({phantom}/{ground})");
    }

    // A body (radius 0.3 m, from 0.5 to 1.5 m above the feet) at a captured position touches no wall.
    private static void NotInsideSolids(OutdoorCollision oc, List<(uint Cell, float X, float Y, float Z)> outdoor)
    {
        int touching = 0, within10 = 0, within20 = 0;
        foreach (var (cell, x, y, z) in outdoor)
        {
            uint lb = cell >> 16;
            float best = float.MaxValue;
            CollisionTri hit = default;
            foreach (var t in oc.Near(lb, x, y, 0.3f))
            {
                if (t.Walkable || t.MaxZ < z + 0.5f - 0.3f || t.MinZ > z + 1.5f + 0.3f) continue;
                var poly = new[] { t.A, t.B, t.C };
                for (float h = 0.5f; h <= 1.501f; h += 0.1f)
                {
                    float d = Geo.DistToPolygon(poly, new Vector3(x, y, z + h));
                    if (d < best) { best = d; hit = t; }
                }
            }
            if (best >= 0.3f) continue;
            touching++;
            if (best < 0.1f) within10++;
            if (best < 0.2f) within20++;
            T.Info($"near a wall {cell:X8} ({x:F2}, {y:F2}, {z:F3}): {best:F2} m from source {hit.Source:X8}, " +
                   $"normal ({hit.Normal.X:F2}, {hit.Normal.Y:F2}, {hit.Normal.Z:F2}), z {hit.MinZ:F2}..{hit.MaxZ:F2}");
        }
        T.Info($"{outdoor.Count - touching}/{outdoor.Count} vectors are clear of walls (0.3 m body, 0.5..1.5 m up); " +
               $"{touching} within 0.3 m, {within20} within 0.2 m, {within10} within 0.1 m");
        T.Check(touching <= outdoor.Count / 20, $"few vectors within 0.3 m of a wall ({touching}/{outdoor.Count})");
        T.Check(within10 == 0, $"no vector within 0.1 m of a wall ({within10})");
    }

    // A horizontal segment from a creature through the middle of a nearby building hits it; a short one in
    // open ground hits nothing.
    private static void Segments(OutdoorCollision oc, DatDatabase cellDat, List<(uint Cell, float X, float Y, float Z)> outdoor)
    {
        var info = LandblockInfo.Load(cellDat, Holtburg)!;
        var here = outdoor.Where(v => v.Cell >> 16 == Holtburg).ToList();
        int tried = 0, hits = 0;
        foreach (var b in info.Buildings)
        {
            var v = here.Select(p => (p, d: Vector2.Distance(new(p.X, p.Y), new(b.Origin.X, b.Origin.Y))))
                        .Where(q => q.d > 4 && q.d < 40).OrderBy(q => q.d).Select(q => q.p).FirstOrDefault();
            if (v.Cell == 0) continue;
            float z = MathF.Max(v.Z, b.Origin.Z) + 1f;
            var a = new Vector3(v.X, v.Y, z);
            var c = new Vector3(b.Origin.X, b.Origin.Y, z);
            var end = c + Vector3.Normalize(c - a) * 6f;
            tried++;
            if (oc.SegmentHit(Holtburg, a, end, out float t, out var tri))
            {
                hits++;
                var p = Vector3.Lerp(a, end, t);
                float dist = Geo.DistToPolygon(new[] { tri.A, tri.B, tri.C }, p);
                T.Check(dist < 0.01f, $"segment hit point lies on the triangle it names ({dist:F4} m)");
            }
            else T.Info($"segment to building {b.ModelId:X8} at ({b.Origin.X:F1}, {b.Origin.Y:F1}) from ({v.X:F1}, {v.Y:F1}) hits nothing");
        }
        T.Info($"segments into Holtburg buildings: {hits}/{tried} hit");
        T.Check(tried >= 3, $"enough buildings near a vector ({tried})");
        T.Check(hits >= tried - 1, $"segments into buildings hit ({hits}/{tried})");

        // open ground: a vector with nothing within 3 m
        int open = 0, openHits = 0;
        foreach (var (cell, x, y, z) in outdoor)
        {
            uint lb = cell >> 16;
            if (oc.Near(lb, x, y, 3f).Count > 0) continue;
            open++;
            if (oc.SegmentHit(lb, new Vector3(x - 1, y, z + 1), new Vector3(x + 1, y + 0.5f, z + 1), out _, out _)) openHits++;
        }
        T.Info($"{open} vectors in open ground (nothing within 3 m)");
        T.Check(open > 0, "some vectors stand in open ground");
        T.Check(openHits == 0, $"short segments in open ground hit nothing ({openHits}/{open})");
        T.Check(!oc.SegmentHit(Holtburg, new Vector3(96, 96, 2000), new Vector3(97, 97, 2000), out _, out _), "a segment high in the air hits nothing");
    }

    // Triangles near a landblock edge come from the neighbour too, shifted into the queried frame.
    private static void NeighbourEdges(OutdoorCollision oc, IEnumerable<uint> landblocks)
    {
        // a triangle within 6 m of a landblock edge, seen from the landblock across that edge
        int tried = 0, seen = 0;
        foreach (uint lb in landblocks)
            foreach (var (dx, dy) in new[] { (0, -1), (0, 1), (-1, 0), (1, 0) })
            {
                var t = oc.Triangles(lb).FirstOrDefault(q =>
                    dy == -1 ? q.MaxY < 6 : dy == 1 ? q.MinY > 186 : dx == -1 ? q.MaxX < 6 : q.MinX > 186);
                if (t == default) continue;
                uint other = (uint)((int)(lb >> 8) + dx) << 8 | (uint)((int)(lb & 0xFF) + dy);
                var shift = new Vector3(-dx * 192f, -dy * 192f, 0);           // lb's frame -> other's frame
                float cx = (t.A.X + t.B.X + t.C.X) / 3 + shift.X, cy = (t.A.Y + t.B.Y + t.C.Y) / 3 + shift.Y;
                tried++;
                if (oc.Near(other, cx, cy, 0.5f).Any(q => q.Source == t.Source && Vector3.Distance(q.A, t.A + shift) < 1e-3f)) seen++;
                else T.Info($"triangle of {lb:X4} at ({cx - shift.X:F1}, {cy - shift.Y:F1}) not seen from {other:X4}");
            }
        T.Info($"edge triangles seen from the neighbouring landblock: {seen}/{tried}");
        T.Check(tried >= 4 && seen == tried, $"edge triangles are seen from across the edge, shifted ({seen}/{tried})");
        T.Check(oc.Near(Holtburg, 96, 96, 2f).All(q => q.MinX > -1 && q.MaxX < 193 && q.MinY > -1 && q.MaxY < 193),
            "a query in the middle of a landblock sees no neighbour triangles");
    }

    // Scenery: some per landblock, none on a road or inside a building's footprint (under a building shell
    // polygon, or on the floor of a building cell at about the scenery's height; buildings' cellars run
    // under the ground and trees above them are fine).
    private static void SceneryRules(OutdoorCollision oc, TerrainSampler terrain, DatDatabase cellDat, IndoorWorld indoor, ModelReader models)
    {
        uint[] lbs = { 0xA9B4, 0xA9B3, 0xC6A9, 0xDA55, 0x7D64, 0xC98C, 0xA9B6, 0x8A8A, 0x5C9E };
        int total = 0, onRoad = 0, inBuilding = 0;
        var counts = new List<string>();
        foreach (uint lb in lbs)
        {
            var sc = oc.SceneryOf(lb);
            total += sc.Count;
            counts.Add($"{lb:X4} {sc.Count} ({oc.Counts(lb).SceneryTris} tris)");
            var footprints = new List<Vector3[]>();
            var info = LandblockInfo.Load(cellDat, lb);
            if (info != null)
                foreach (var b in info.Buildings) footprints.AddRange(models.Get(b.ModelId).Place(b.Origin, b.Rotation));
            foreach (var c in indoor.LoadLandblock(lb))
                if ((c.Flags & 1) != 0) footprints.AddRange(c.Floors);
            foreach (var o in sc)
            {
                if (terrain.OnRoad(lb, o.Position.X, o.Position.Y)) { onRoad++; T.Info($"scenery {o.ModelId:X8} on a road in {lb:X4} at ({o.Position.X:F1}, {o.Position.Y:F1})"); }
                if (footprints.Any(f => Geo.AreaXY(f) > 0.01f && Geo.InsideXY(f, o.Position.X, o.Position.Y, 0)
                                        && MathF.Abs(Geo.PlaneZ(f, o.Position.X, o.Position.Y) - o.Position.Z) < 3f))
                {
                    inBuilding++;
                    T.Info($"scenery {o.ModelId:X8} inside a building footprint in {lb:X4} at ({o.Position.X:F1}, {o.Position.Y:F1})");
                }
                T.Check(o.Position.X >= 0 && o.Position.X < 192 && o.Position.Y >= 0 && o.Position.Y < 192, "scenery inside its landblock");
            }
        }
        T.Info($"scenery: {string.Join(", ", counts)}");
        T.Check(total > 50, $"scenery is placed ({total} objects in {lbs.Length} landblocks)");
        T.Check(onRoad == 0, $"{onRoad} scenery objects on a road");
        T.Check(inBuilding == 0, $"{inBuilding} scenery objects inside a building footprint");
        T.Info($"Setups whose cylinders/spheres were read from the end of their record (animation hooks): {oc.TailReads} placements");
    }
    // SetupBounds reads a Setup's cylinders, spheres and sorting sphere from the end of the record when its
    // placement frames have animation hooks. Check that tail reading against the front-to-back read on every
    // Setup without hooks, and count how many with hooks it reads.
    private static void SetupTails(DatDatabase portalDat)
    {
        int plain = 0, agree = 0, hooked = 0, hookedRead = 0, broken = 0;
        var disagree = new List<uint>();
        foreach (var e in portalDat.EnumerateEntries())
        {
            if ((e.ObjectId >> 24) != 0x02) continue;
            byte[]? data = portalDat.GetFileData(e.ObjectId);
            if (data == null) continue;
            SetupBounds? fwd;
            try { fwd = SetupBounds.Parse(data, fromEnd: false); }
            catch (Exception) { broken++; continue; }
            var tail = SetupBounds.Parse(data, fromEnd: true);
            if (fwd == null) { hooked++; if (tail != null) hookedRead++; continue; }
            plain++;
            bool same = tail != null && tail.Cylinders.SequenceEqual(fwd.Cylinders) && tail.Spheres.SequenceEqual(fwd.Spheres)
                        && tail.Height == fwd.Height && tail.Radius == fwd.Radius && tail.SortCenter == fwd.SortCenter && tail.SortRadius == fwd.SortRadius;
            if (same) agree++; else if (disagree.Count < 10) disagree.Add(e.ObjectId);
        }
        T.Info($"Setups: {plain} without hooks ({agree} read the same from the end), {hooked} with hooks ({hookedRead} read from the end), {broken} unreadable" +
               (disagree.Count > 0 ? "; disagree: " + string.Join(" ", disagree.Select(i => i.ToString("X8"))) : ""));
        T.Check(plain > 1000, $"many Setups in the portal dat ({plain})");
        T.Check(agree >= plain * 0.99, $"reading from the end agrees with the front-to-back read ({agree}/{plain})");
        T.Check(hookedRead >= hooked * 0.95, $"Setups with animation hooks read from the end ({hookedRead}/{hooked})");
    }
}
