// ============================================================================
//  GeometryTests - TerrainTests.cs
//  Terrain/TerrainSampler and Terrain/LandblockData against the real dats and
//  the outdoor ground truth in Vectors\outdoor-towns.txt (creatures standing in
//  five towns, positions from the local ACE server).
// ============================================================================

using System.Numerics;
using RynthCore.Geometry.Dat;
using RynthCore.Geometry.Terrain;

namespace GeometryTests;

internal static class TerrainTests
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Register() => Suites.Add("terrain", Run, order: 10);

    private static void Run()
    {
        using var portal = new DatDatabase();
        using var cell = new DatDatabase();
        T.Check(portal.Open(Dats.Portal), "portal dat opens");
        T.Check(cell.Open(Dats.Cell), "cell dat opens");
        using var ts = new TerrainSampler(portal, cell);

        var outdoor = Dats.Vectors("outdoor-towns.txt").Where(v => (v.Cell & 0xFFFF) < 0x100).ToList();
        T.Check(outdoor.Count > 50, $"outdoor vectors loaded ({outdoor.Count})");

        HeightTableAndLandblocks(ts, outdoor);
        TerrainZ(ts, outdoor);
        SplitPolarity(ts, outdoor);
        Normals(ts, outdoor);
        RaycastDown(ts, outdoor);
        RaycastOblique(ts, outdoor);
        Roads(ts, outdoor);
        OpenFromFolder();
    }

    private static void HeightTableAndLandblocks(TerrainSampler ts, List<(uint Cell, float X, float Y, float Z)> outdoor)
    {
        var table = ts.LandHeightTable;
        bool monotonic = true;
        for (int i = 1; i < table.Count; i++) if (table[i] < table[i - 1]) monotonic = false;
        T.Check(table.Count == 256 && monotonic, "land-height table: 256 entries, non-decreasing");
        T.Check(table[0] >= 0f && table[0] < 10f && table[255] > 500f && table[255] < 5000f,
            $"land-height table range plausible ({table[0]} .. {table[255]})");
        T.Info($"land-height table {table[0]} .. {table[255]}, [64]={table[64]} [128]={table[128]}");

        var keys = outdoor.Select(v => v.Cell >> 16).Distinct().ToList();
        foreach (uint key in keys)
        {
            var lb = ts.LoadLandblock(key);
            T.Check(lb != null, $"landblock {key:X4} loads");
            if (lb == null) continue;
            lb.GetHeightRange(out float min, out float max);
            T.Check(lb.LandblockKey == key && min >= table[0] && max <= table[255] && max - min < 400f,
                $"landblock {key:X4} heights plausible ({min:F1} .. {max:F1})");
            T.Check(lb.HasObjects, $"town landblock {key:X4} has objects");
            T.Check(ReferenceEquals(lb, ts.LoadLandblock(key)), $"landblock {key:X4} is cached");
        }
        T.Info($"town landblocks: {string.Join(" ", keys.Select(k => k.ToString("X4")))}");

        // Cell-id form and key form name the same landblock.
        uint k0 = keys[0];
        T.Check(ReferenceEquals(ts.LoadLandblock(k0), ts.LoadLandblock((k0 << 16) | 0x0019)), "0xXXYY and 0xXXYYnnnn give the same landblock");

        // Neighbouring landblocks share their edge vertices (checks the ix*9+iy indexing).
        int shared = 0, sharedMatch = 0;
        foreach (uint key in keys)
        {
            var a = ts.LoadLandblock(key);
            var east = ts.LoadLandblock(key + 0x0100);
            var north = ts.LoadLandblock(key + 0x0001);
            for (int i = 0; i <= 8; i++)
            {
                if (a != null && east != null) { shared++; if (a.HeightsZ[8, i] == east.HeightsZ[0, i]) sharedMatch++; }
                if (a != null && north != null) { shared++; if (a.HeightsZ[i, 8] == north.HeightsZ[i, 0]) sharedMatch++; }
            }
        }
        T.Check(shared > 0 && sharedMatch == shared, $"landblock edges match their neighbours ({sharedMatch}/{shared})");

        // 0xFFFF is not a landblock: everything answers "no terrain" rather than throwing.
        T.Check(ts.LoadLandblock(0xFFFF) == null && float.IsNaN(ts.GetTerrainZ(0xFFFF, 10, 10))
            && ts.GetNormal(0xFFFF, 10, 10) == Vector3.Zero && !ts.OnRoad(0xFFFF, 10, 10) && ts.TerrainType(0xFFFF, 10, 10) == -1,
            "landblock 0xFFFF: null / NaN / zero / false / -1");
    }

    private static void TerrainZ(TerrainSampler ts, List<(uint Cell, float X, float Y, float Z)> outdoor)
    {
        // Ground truth vs GetStandingZ (terrain mesh less the cell's water depth) and vs the bare mesh.
        // Creatures can also stand on porches, platforms and ramps; those come out above the ground
        // and can't be checked without the object geometry. Nothing may come out below it.
        var rows = new List<(float D, float DMesh, float Wd, bool Flat, uint Cell, float X, float Y, float Z, float Gz)>();
        foreach (var v in outdoor)
        {
            var lb = ts.LoadLandblock(v.Cell)!;
            float mesh = ts.GetTerrainZ(v.Cell, v.X, v.Y);
            float gz = ts.GetStandingZ(v.Cell, v.X, v.Y);
            int cx = Math.Min((int)(v.X / 24f), 7), cy = Math.Min((int)(v.Y / 24f), 7);
            float h = lb.GetVertexZ(cx, cy);
            bool flat = lb.GetVertexZ(cx + 1, cy) == h && lb.GetVertexZ(cx, cy + 1) == h && lb.GetVertexZ(cx + 1, cy + 1) == h;
            rows.Add((v.Z - gz, v.Z - mesh, ts.GetWaterDepth(v.Cell, v.X, v.Y), flat, v.Cell, v.X, v.Y, v.Z, gz));
        }
        int n = rows.Count;
        int mesh05 = rows.Count(r => MathF.Abs(r.DMesh) <= 0.05f), mesh25 = rows.Count(r => MathF.Abs(r.DMesh) <= 0.25f);
        int in05 = rows.Count(r => MathF.Abs(r.D) <= 0.05f), in25 = rows.Count(r => MathF.Abs(r.D) <= 0.25f);
        var above = rows.Where(r => r.D > 0.05f).ToList();
        var below = rows.Where(r => r.D < -0.05f).ToList();
        int wet = rows.Count(r => r.Wd > 0f), wetOk = rows.Count(r => r.Wd > 0f && MathF.Abs(r.D) <= 0.05f);
        int wetRaised = rows.Count(r => r.Wd > 0f && r.D > 0.05f);
        double pct = 100.0 * in05 / n;
        T.Info($"bare terrain mesh vs ACE: {mesh05}/{n} ({100.0 * mesh05 / n:F1}%) within 0.05 m, {mesh25}/{n} ({100.0 * mesh25 / n:F1}%) within 0.25 m");
        T.Info($"standing Z (mesh - water depth) vs ACE: {in05}/{n} ({pct:F1}%) within 0.05 m, {in25}/{n} ({100.0 * in25 / n:F1}%) within 0.25 m; " +
               $"off by > 0.05: {above.Count} above the ground ({above.Count(r => r.Flat)} of them on flat cells), {below.Count} below");
        T.Info($"water cells: {wet} vectors, {wetOk} sit exactly the water depth below the mesh, {wetRaised} higher (on an object)");
        foreach (var r in rows.OrderByDescending(r => MathF.Abs(r.D)).Take(6))
            T.Info($"  worst: {r.Cell:X8} ({r.X:F2}, {r.Y:F2}) z={r.Z:F3} ground={r.Gz:F3} off={r.D:+0.000;-0.000}{(r.Flat ? " (flat cell: on an object)" : "")}");

        T.Check(rows.All(r => !float.IsNaN(r.Gz)), "every outdoor vector's landblock has terrain");
        T.Check(below.Count == 0, $"no vector stands below the ground ({below.Count})");
        T.Check(wetOk > 0 && wetOk == wet - wetRaised, $"water depth explains every ground-level vector in a water cell ({wetOk}/{wet - wetRaised})");
        // The target was 85%, but about a fifth of the vectors (25 of 135 when written) stand on
        // objects above flat cells (equal corner heights, so neither the split nor the height
        // table can be at fault) where no terrain answer can match. Hence 80% overall, plus the
        // ground-level share below.
        T.Check(pct >= 80.0, $"at least 80% of outdoor vectors within 0.05 m of the standing Z (got {pct:F1}%)");
        int ground = n - above.Count(r => r.Flat);
        T.Check(in05 >= ground * 0.98, $"vectors not raised on objects match within 0.05 m ({in05}/{ground})");
    }

    private static void SplitPolarity(TerrainSampler ts, List<(uint Cell, float X, float Y, float Z)> outdoor)
    {
        double sumAsIs = 0, sumInv = 0;
        int okAsIs = 0, okInv = 0, differ = 0, differAsIs = 0, differInv = 0;
        foreach (var v in outdoor)
        {
            var lb = ts.LoadLandblock(v.Cell)!;
            float z = v.Z + TerrainSampler.GetWaterDepth(lb, v.X, v.Y);
            float a = MathF.Abs(z - TerrainSampler.GetTerrainZ(lb, v.X, v.Y, invertSplit: false));
            float b = MathF.Abs(z - TerrainSampler.GetTerrainZ(lb, v.X, v.Y, invertSplit: true));
            sumAsIs += a; sumInv += b;
            if (a <= 0.05f) okAsIs++;
            if (b <= 0.05f) okInv++;
            if (MathF.Abs(a - b) > 0.01f)
            {
                differ++;
                if (a <= 0.05f) differAsIs++;
                if (b <= 0.05f) differInv++;
                T.Info($"  split matters: {v.Cell:X8} ({v.X:F2}, {v.Y:F2}) z={v.Z:F3} err as implemented {a:F3}, inverted {b:F3}");
            }
        }
        int n = outdoor.Count;
        T.Info($"split polarity: as implemented mean err {sumAsIs / n:F4} m, {okAsIs}/{n} within 0.05; inverted mean err {sumInv / n:F4} m, {okInv}/{n} within 0.05");
        T.Info($"  on the {differ} vectors where the polarity matters: {differAsIs} match as implemented, {differInv} inverted");
        T.Check(differ >= 5, $"enough vectors sit where the split matters ({differ})");
        T.Check(sumAsIs < sumInv && differAsIs > differInv && differInv == 0, "implemented SwToNeCut polarity fits ACE, the inverse doesn't");

        // About half the cells each way.
        int sw = 0, total = 0;
        foreach (uint key in outdoor.Select(v => v.Cell >> 16).Distinct())
            for (int cx = 0; cx < 8; cx++)
                for (int cy = 0; cy < 8; cy++) { total++; if (TerrainSampler.SwToNeCut(key, cx, cy)) sw++; }
        T.Info($"SW->NE cut on {sw}/{total} cells of the town landblocks");
        T.Check(sw > total / 4 && sw < total * 3 / 4, "split is roughly 50/50");
    }

    private static void Normals(TerrainSampler ts, List<(uint Cell, float X, float Y, float Z)> outdoor)
    {
        int bad = 0, walkable = 0, consistent = 0, tested = 0;
        foreach (var v in outdoor)
        {
            var n = ts.GetNormal(v.Cell, v.X, v.Y);
            if (!(n.Z > 0f) || MathF.Abs(n.Length() - 1f) > 1e-4f) bad++;
            if (TerrainSampler.IsWalkable(n)) walkable++;

            // The normal is the gradient of GetTerrainZ: z = z0 - (nx*dx + ny*dy)/nz on the same triangle.
            // Step toward the triangle's interior along a tiny offset that stays on the triangle.
            var lb = ts.LoadLandblock(v.Cell)!;
            float z0 = TerrainSampler.GetTerrainZ(lb, v.X, v.Y);
            const float d = 0.01f;
            bool all = true;
            foreach (var (ox, oy) in new[] { (d, 0f), (0f, d), (-d, 0f), (0f, -d) })
            {
                if (TerrainSampler.GetNormal(lb, v.X + ox, v.Y + oy) != n) continue; // crossed onto another triangle
                float pred = z0 - (n.X * ox + n.Y * oy) / n.Z;
                if (MathF.Abs(pred - TerrainSampler.GetTerrainZ(lb, v.X + ox, v.Y + oy)) > 1e-3f) all = false;
            }
            tested++;
            if (all) consistent++;
        }
        T.Check(bad == 0, $"normals are unit length with Z > 0 ({bad} bad)");
        T.Check(consistent == tested, $"normal matches the GetTerrainZ gradient ({consistent}/{tested})");
        double pct = 100.0 * walkable / outdoor.Count;
        T.Info($"walkable ground under {walkable}/{outdoor.Count} ({pct:F1}%) town vectors");
        T.Check(pct >= 95.0, "town ground under creatures is walkable");
        T.Check(TerrainSampler.IsWalkable(Vector3.UnitZ) && TerrainSampler.IsWalkable(Vector3.Normalize(new Vector3(1, 0, 1))),
            "IsWalkable: flat and 45 degrees yes");
        T.Check(!TerrainSampler.IsWalkable(Vector3.Normalize(new Vector3(1.2f, 0, 1))), "IsWalkable: 50 degrees no");
    }

    private static void RaycastDown(TerrainSampler ts, List<(uint Cell, float X, float Y, float Z)> outdoor)
    {
        int hits = 0, good = 0;
        float worst = 0f;
        foreach (var v in outdoor)
        {
            float tz = ts.GetTerrainZ(v.Cell, v.X, v.Y);
            bool hit = ts.RaycastTerrain(v.Cell, new Vector3(v.X, v.Y, v.Z + 10f), -Vector3.UnitZ, 100f, out float dist, out var n);
            if (!hit) continue;
            hits++;
            float expect = 10f + (v.Z - tz);           // from z+10 down to the terrain
            float err = MathF.Max(MathF.Abs(dist - expect), MathF.Abs((v.Z + 10f - dist) - tz));
            worst = MathF.Max(worst, err);
            if (err <= 0.01f && Vector3.Distance(n, ts.GetNormal(v.Cell, v.X, v.Y)) < 1e-4f) good++;
        }
        T.Check(hits == outdoor.Count, $"vertical raycast hits terrain under every vector ({hits}/{outdoor.Count})");
        T.Check(good == hits, $"vertical raycast distance/normal agree with GetTerrainZ/GetNormal within 0.01 ({good}/{hits}, worst {worst:F5})");

        var v0 = outdoor[0];
        T.Check(!ts.RaycastTerrain(v0.Cell, new Vector3(v0.X, v0.Y, v0.Z + 10f), Vector3.UnitZ, 100f, out _, out _), "ray pointing up misses");
        T.Check(!ts.RaycastTerrain(v0.Cell, new Vector3(v0.X, v0.Y, v0.Z + 10f), -Vector3.UnitZ, 5f, out _, out _), "ray shorter than the drop misses");
    }

    private static void RaycastOblique(TerrainSampler ts, List<(uint Cell, float X, float Y, float Z)> outdoor)
    {
        // Rays from above the town vectors in random downward directions, long enough to cross
        // several cells and often a landblock edge. The hit must lie on the terrain, and the ray
        // must stay above the terrain before it.
        var rng = new Random(12345);
        int rays = 0, hits = 0, onGround = 0, clear = 0, crossed = 0;
        float worst = 0f;
        foreach (var v in outdoor)
        {
            for (int k = 0; k < 4; k++)
            {
                double ang = rng.NextDouble() * Math.PI * 2;
                float down = 0.05f + (float)rng.NextDouble() * 0.4f;   // shallow: long horizontal runs
                var dir = Vector3.Normalize(new Vector3((float)Math.Cos(ang), (float)Math.Sin(ang), -down));
                var o = new Vector3(v.X, v.Y, v.Z + 5f + (float)rng.NextDouble() * 20f);
                rays++;
                if (!ts.RaycastTerrain(v.Cell, o, dir, 600f, out float dist, out var n)) continue;
                hits++;
                var p = o + dir * dist;
                float z = WorldZ(ts, v.Cell, p.X, p.Y, out bool otherLb);
                if (otherLb) crossed++;
                float err = MathF.Abs(p.Z - z);
                worst = MathF.Max(worst, err);
                if (err <= 0.01f && n.Z > 0f && MathF.Abs(n.Length() - 1f) < 1e-4f) onGround++;

                bool above = true;
                for (float s = 0.25f; s < dist - 0.05f; s += 0.25f)
                {
                    var q = o + dir * s;
                    if (q.Z < WorldZ(ts, v.Cell, q.X, q.Y, out _) - 0.005f) { above = false; break; }
                }
                if (above) clear++;
            }
        }
        T.Info($"oblique rays: {hits}/{rays} hit, {crossed} hits in a neighbouring landblock, worst hit-point error {worst:F5} m");
        T.Check(hits > rays * 3 / 4, "most oblique rays hit the ground");
        T.Check(onGround == hits, $"oblique hit points lie on the terrain within 0.01 ({onGround}/{hits})");
        T.Check(clear == hits, $"oblique rays stay above the terrain before the hit ({clear}/{hits})");
        T.Check(crossed > 0, "some oblique hits land past the starting landblock");
    }

    /// <summary>Terrain Z at a point given relative to <paramref name="cell"/>'s landblock, which may lie in a neighbour.</summary>
    private static float WorldZ(TerrainSampler ts, uint cell, float lx, float ly, out bool otherLandblock)
    {
        uint key = cell >> 16;
        int bx = (int)((key >> 8) & 0xFF) + (int)MathF.Floor(lx / 192f);
        int by = (int)(key & 0xFF) + (int)MathF.Floor(ly / 192f);
        float x = lx - MathF.Floor(lx / 192f) * 192f, y = ly - MathF.Floor(ly / 192f) * 192f;
        uint nk = (uint)((bx << 8) | by);
        otherLandblock = nk != key;
        return ts.GetTerrainZ(nk, x, y);
    }

    private static void Roads(TerrainSampler ts, List<(uint Cell, float X, float Y, float Z)> outdoor)
    {
        int roadVerts = 0, roadOk = 0, noRoadCells = 0, noRoadOk = 0;
        foreach (uint key in outdoor.Select(v => v.Cell >> 16).Distinct())
        {
            var lb = ts.LoadLandblock(key)!;
            for (int ix = 0; ix < 8; ix++)
                for (int iy = 0; iy < 8; iy++)
                {
                    float x = ix * 24f + 0.1f, y = iy * 24f + 0.1f;
                    if (TerrainSampler.RoadBits(lb, ix, iy) != 0)
                    {
                        roadVerts++;
                        if (TerrainSampler.OnRoad(lb, x, y)) roadOk++;
                    }
                    if (TerrainSampler.RoadBits(lb, ix, iy) == 0 && TerrainSampler.RoadBits(lb, ix + 1, iy) == 0
                        && TerrainSampler.RoadBits(lb, ix, iy + 1) == 0 && TerrainSampler.RoadBits(lb, ix + 1, iy + 1) == 0)
                    {
                        noRoadCells++;
                        if (!ts.OnRoad(key, ix * 24f + 12f, iy * 24f + 12f)) noRoadOk++;
                    }
                }
        }
        int onRoad = outdoor.Count(v => ts.OnRoad(v.Cell, v.X, v.Y));
        T.Info($"roads: {roadVerts} road vertices in the town landblocks; {onRoad}/{outdoor.Count} vectors on a road");
        T.Check(roadVerts > 0, "town landblocks have roads");
        T.Check(roadOk == roadVerts, $"a point just inside a road vertex is on the road ({roadOk}/{roadVerts})");
        T.Check(noRoadOk == noRoadCells, $"cells with no road corners have no road ({noRoadOk}/{noRoadCells})");

        var types = outdoor.Select(v => ts.TerrainType(v.Cell, v.X, v.Y)).ToList();
        T.Check(types.All(t => t >= 0 && t < 32), "terrain types in 0..31");
        T.Info($"terrain types under the vectors: {string.Join(" ", types.GroupBy(t => t).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}x{g.Count()}"))}");
    }

    private static void OpenFromFolder()
    {
        using var ts = TerrainSampler.Open(Dats.Folder!);
        var v = Dats.Vectors("outdoor-towns.txt")[0];
        T.Check(MathF.Abs(ts.GetTerrainZ(v.Cell, v.X, v.Y) - v.Z) < 0.25f, "TerrainSampler.Open(folder) works");
        bool threw = false;
        try { TerrainSampler.Open(Path.GetTempPath()).Dispose(); } catch (FileNotFoundException) { threw = true; }
        T.Check(threw, "Open on a folder without dats throws FileNotFoundException");
    }
}
