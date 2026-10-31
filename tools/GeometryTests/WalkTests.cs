// ============================================================================
//  WalkTests - the kinematic walker (RynthCore.Geometry/Walking/Walker.cs) on the real dats,
//  checked against positions ACE reported (Vectors\).
// ============================================================================

using System.Numerics;
using RynthCore.Geometry.Cells;
using RynthCore.Geometry.Collision;
using RynthCore.Geometry.Dat;
using RynthCore.Geometry.Models;
using RynthCore.Geometry.Terrain;
using RynthCore.Geometry.Walking;

namespace GeometryTests;

internal static class WalkTests
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Register() => Suites.Add("walk", Run, order: 40);

    private static void Run()
    {
        using var portal = new DatDatabase();
        using var cell = new DatDatabase();
        T.Check(portal.Open(Dats.Portal) && cell.Open(Dats.Cell), "dats open");
        var terrain = new TerrainSampler(portal, cell);
        var indoor = new IndoorWorld(cell, portal, new ModelReader(portal));
        var walker = new Walker(terrain, indoor);
        var world = new OutdoorCollision(cell, portal, terrain, indoor.Models, indoor);
        var withObstacles = new Walker(terrain, indoor, new StaticObstacles(world));

        Dungeon(walker, indoor);
        Outdoors(walker, terrain, indoor);
        ObstacleWalks(withObstacles, terrain, indoor, world);
        IntoBuilding(withObstacles, indoor, terrain);
        Planner(withObstacles, terrain);
    }

    /// <summary>Pairs of town points whose straight walk is blocked: the local planner finds a way and walking it arrives.</summary>
    private static void Planner(Walker walker, TerrainSampler terrain)
    {
        var v = Dats.Vectors("outdoor-towns.txt")
            .Where(p => MathF.Abs(terrain.GetStandingZ(p.Cell >> 16, p.X, p.Y) - p.Z) < 0.05f).ToList();
        var planner = new LocalPlanner(walker);
        int blockedPairs = 0, planned = 0, walked = 0;
        long ms = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var a in v)
        {
            foreach (var b in v.Where(b => b.Cell >> 16 == a.Cell >> 16 && Dist2(a, b) is > 15 and < 40))
            {
                var start = new PathPoint(a.Cell, new Vector3(a.X, a.Y, a.Z));
                var straight = WalkTo(walker, a.Cell, start.Position, new Vector2(b.X, b.Y));
                if (Vector2.Distance(new Vector2(straight.Position.X, straight.Position.Y), new Vector2(b.X, b.Y)) < 0.5f) continue;
                blockedPairs++;
                sw.Restart();
                var path = planner.Find(start, new Vector2(b.X, b.Y), 0.5f);
                ms += sw.ElapsedMilliseconds;
                if (path == null) continue;
                planned++;
                // Follow the path with the walker.
                var r = new StepResult(a.Cell, start.Position, false);
                foreach (var pt in path.Skip(1))
                {
                    var target = LocalPlanner.InFrame(a.Cell >> 16, pt.Cell, pt.Position);
                    var here = LocalPlanner.InFrame(a.Cell >> 16, r.Cell, r.Position);
                    r = walker.Step(r.Cell, r.Position, target.X - here.X, target.Y - here.Y);
                    if (r.Blocked) break;
                }
                if (!r.Blocked && Vector2.Distance(LocalPlanner.InFrame(a.Cell >> 16, r.Cell, r.Position), new Vector2(b.X, b.Y)) < 0.6f) walked++;
                if (blockedPairs >= 25) break;
            }
            if (blockedPairs >= 25) break;
        }
        T.Info($"planner: {blockedPairs} blocked pairs, {planned} planned ({(planned > 0 ? ms / planned : 0)} ms each), {walked} walked to the goal");
        T.Check(blockedPairs >= 5, "there are blocked pairs to plan around");
        T.Check(planned >= blockedPairs * 0.7, $"most blocked pairs get a path ({planned}/{blockedPairs})");
        T.Check(walked == planned, $"every planned path walks to the goal ({walked}/{planned})");
    }

    private static void Dungeon(Walker walker, IndoorWorld indoor)
    {
        var v = Dats.Vectors("dungeon-6146.txt");
        var byCell = v.GroupBy(p => p.Cell).ToDictionary(g => g.Key, g => g.ToList());

        // Walk from a captured position to one in a neighbouring cell (up to 4 m away, roughly level).
        int pairs = 0, arrived = 0, rightCell = 0, rightZ = 0;
        var misses = new List<string>();
        var rng = new Random(7);
        foreach (var a in v.OrderBy(_ => rng.Next()).Take(600))
        {
            foreach (uint n in indoor.Neighbours(a.Cell))
            {
                if (!byCell.TryGetValue(n, out var list)) continue;
                var b = list.FirstOrDefault(p => Dist2(a, p) < 4 && Dist2(a, p) > 0.5f && MathF.Abs(p.Z - a.Z) < 0.3f);
                if (b.Cell == 0) continue;
                pairs++;
                var r = WalkTo(walker, a.Cell, new Vector3(a.X, a.Y, a.Z), new Vector2(b.X, b.Y));
                bool there = Vector2.Distance(new Vector2(r.Position.X, r.Position.Y), new Vector2(b.X, b.Y)) < 0.05f;
                if (there) arrived++;
                if (there && r.Cell == b.Cell) rightCell++;
                if (there && MathF.Abs(r.Position.Z - b.Z) < 0.15f) rightZ++;
                if ((!there || r.Cell != b.Cell) && misses.Count < 8)
                    misses.Add($"{a.Cell:X8} ({a.X:F2},{a.Y:F2}) -> {b.Cell:X8} ({b.X:F2},{b.Y:F2}): ended {r.Cell:X8} ({r.Position.X:F2},{r.Position.Y:F2}) {r.Why}");
                break;
            }
        }
        T.Info($"dungeon neighbour walks: {pairs} pairs, {arrived} arrived, {rightCell} in the right cell, {rightZ} at ACE's height (0.15 m)");
        foreach (string m in misses) T.Info("  " + m);
        T.Check(pairs > 100, "enough neighbour pairs");
        T.Check(arrived >= pairs * 0.85, $"most straight walks between neighbouring cells get there ({arrived}/{pairs})");
        T.Check(rightCell >= arrived * 0.97, $"and end in ACE's cell ({rightCell}/{arrived})");
        T.Check(rightZ >= arrived * 0.9, $"at ACE's height ({rightZ}/{arrived})");

        // Long walks in random directions: never end outside a cell, the cell agrees with FindCell.
        int walks = 0, consistent = 0, blocked = 0;
        foreach (var a in v.OrderBy(_ => rng.Next()).Take(200))
        {
            float ang = (float)(rng.NextDouble() * Math.PI * 2);
            var r = walker.Step(a.Cell, new Vector3(a.X, a.Y, a.Z), MathF.Cos(ang) * 30, MathF.Sin(ang) * 30);
            walks++;
            if (r.Blocked) blocked++;
            var found = indoor.FindCell(0x6146, r.Position.X, r.Position.Y, r.Position.Z);
            if (r.Cell >> 16 == 0x6146 && indoor.GetCell(r.Cell)?.CoversXY(r.Position.X, r.Position.Y) == true && found?.cellId == r.Cell) consistent++;
        }
        T.Info($"30 m walks: {walks}, {blocked} stopped by walls, {consistent} end in a consistent cell");
        T.Check(consistent >= walks * 0.97, $"walks end where FindCell agrees ({consistent}/{walks})");

        // The test character's spot.
        var start = new Vector3(91.617874f, -157.55351f, -11.9885f);
        var rb = walker.Step(0x61460249, start, 1.5f, 0);
        T.Check(!rb.Blocked || rb.Why != null, "a step from the test character's spot resolves");

        SlopedDoorway(walker);
    }

    /// <summary>
    /// The Matron hive (0x6346): tunnel 0x01A5 opens into room 0x01A6 through a doorway that slopes up over the
    /// tunnel's end, so the doorway is crossed above the feet before the room's floor begins. A 4Hives
    /// route walks straight through it (2026-10-29: the walker stopped at the tunnel's end until TrackCell
    /// learned sloping doorways).
    /// </summary>
    private static void SlopedDoorway(Walker walker)
    {
        var east = WalkTo(walker, 0x634601A5, new Vector3(136f, -10f, -24f), new Vector2(151.5f, -10f));
        T.Check(east.Cell == 0x634601B1 && MathF.Abs(east.Position.X - 151.5f) < 0.3f,
            $"hive tunnel -> room -> loot room through the sloping doorway (ended {east.Cell:X8} x {east.Position.X:F1})");
        var west = WalkTo(walker, 0x634601B1, new Vector3(150f, -10f, -24f), new Vector2(136f, -10f));
        T.Check(west.Cell == 0x634601A5 && MathF.Abs(west.Position.X - 136f) < 0.3f,
            $"and back out under it (ended {west.Cell:X8} x {west.Position.X:F1})");
        var beside = WalkTo(walker, 0x634601A6, new Vector3(142f, -6f, -24f), new Vector2(136f, -6f));
        T.Check(beside.Cell != 0x634601A5, $"beside the tunnel mouth the room's ramp is not the tunnel (ended {beside.Cell:X8})");
    }

    private static void Outdoors(Walker walker, TerrainSampler terrain, IndoorWorld indoor)
    {
        var v = Dats.Vectors("outdoor-towns.txt")
            .Where(p => MathF.Abs(terrain.GetStandingZ(p.Cell >> 16, p.X, p.Y) - p.Z) < 0.05f).ToList();
        int walks = 0, onGround = 0, cellOk = 0;
        var rng = new Random(11);
        foreach (var a in v.Take(60))
        {
            float ang = (float)(rng.NextDouble() * Math.PI * 2);
            var r = walker.Step(a.Cell, new Vector3(a.X, a.Y, a.Z), MathF.Cos(ang) * 10, MathF.Sin(ang) * 10);
            walks++;
            uint lb = r.Cell >> 16;
            if (EnvCell.IsIndoor(r.Cell))
            {
                // Walked in through a ground-level doorway: fine if the cell agrees.
                bool ok = indoor.FindCell(lb, r.Position.X, r.Position.Y, r.Position.Z)?.cellId == r.Cell;
                if (ok) { onGround++; cellOk++; }
                else T.Info($"  entered {r.Cell:X8} but FindCell disagrees");
                continue;
            }
            float g = terrain.GetStandingZ(lb, r.Position.X, r.Position.Y);
            if (MathF.Abs(g - r.Position.Z) < 0.01f) onGround++;
            if (r.Cell == Walker.OutdoorCell(lb, r.Position.X, r.Position.Y)) cellOk++;
            else T.Info($"  land cell miss: from {a.Cell:X8} ({a.X:F2},{a.Y:F2}) ended {r.Cell:X8} ({r.Position.X:F2},{r.Position.Y:F2}) blocked {r.Blocked} {r.Why}");
        }
        T.Info($"outdoor 10 m walks (no obstacles loaded, ground-level doors count when the cell agrees): {walks}, {onGround} on the terrain, {cellOk} with the right land cell");
        T.Check(onGround == walks && cellOk == walks, "outdoor walks follow the terrain and land cells");

        // Across a landblock edge: Holtburg 0xA9B4 east edge -> 0xAAB4.
        float z0 = terrain.GetStandingZ(0xA9B4, 190f, 100f);
        var e = walker.Step(Walker.OutdoorCell(0xA9B4, 190f, 100f), new Vector3(190f, 100f, z0), 5f, 0f);
        T.Check(!e.Blocked && e.Cell >> 16 == 0xAAB4 && MathF.Abs(e.Position.X - 3f) < 0.01f,
            $"crossing east into 0xAAB4 (ended 0x{e.Cell:X8} x {e.Position.X:F2}{(e.Blocked ? ", " + e.Why : "")})");
        T.Check(MathF.Abs(e.Position.Z - terrain.GetStandingZ(0xAAB4, e.Position.X, e.Position.Y)) < 0.01f, "and on its terrain");
    }

    /// <summary>Outdoor walks among buildings, statics and scenery: feet on terrain or a walkable surface, never in a wall.</summary>
    private static void ObstacleWalks(Walker walker, TerrainSampler terrain, IndoorWorld indoor, OutdoorCollision world)
    {
        var v = Dats.Vectors("outdoor-towns.txt")
            .Where(p => MathF.Abs(terrain.GetStandingZ(p.Cell >> 16, p.X, p.Y) - p.Z) < 0.05f).ToList();
        int walks = 0, grounded = 0, clear = 0, blocked = 0;
        var rng = new Random(13);
        foreach (var a in v.Take(70))
        {
            float ang = (float)(rng.NextDouble() * Math.PI * 2);
            var r = walker.Step(a.Cell, new Vector3(a.X, a.Y, a.Z), MathF.Cos(ang) * 15, MathF.Sin(ang) * 15);
            walks++;
            if (r.Blocked) blocked++;
            uint lb = r.Cell >> 16;
            var q = r.Position;
            bool ok;
            if (EnvCell.IsIndoor(r.Cell))
            {
                // Inside: the cell index agrees, or we stopped on the threshold just past the doorway's
                // plane (ACE changes cells at that plane too) before the cell's floor starts.
                ok = indoor.FindCell(lb, q.X, q.Y, q.Z)?.cellId == r.Cell
                     || (indoor.GetCell(r.Cell)?.Portals.Any(cp => cp.LeadsOutdoors && cp.Polygon.Length >= 3
                         && RynthCore.Geometry.Cells.Geo.DistToPolygon(cp.Polygon, q + new Vector3(0, 0, 0.6f)) < 1.0f) ?? false);
            }
            else
            {
                float g = terrain.GetStandingZ(lb, q.X, q.Y);
                float? s = world.SurfaceBelow(lb, q.X, q.Y, q.Z + 0.01f, 0.05f);
                ok = MathF.Abs(g - q.Z) < 0.01f || (s is { } sz && MathF.Abs(sz - q.Z) < 0.01f);
            }
            if (ok) grounded++;
            else T.Info($"  not grounded: from {a.Cell:X8} ({a.X:F2},{a.Y:F2},{a.Z:F2}) ended {r.Cell:X8} ({q.X:F2},{q.Y:F2},{q.Z:F2}) terrain {terrain.GetStandingZ(lb, q.X, q.Y):F2} surface {world.SurfaceBelow(lb, q.X, q.Y, q.Z + 1, 3)?.ToString("F2") ?? "-"} {r.Why}");
            bool inWall = !EnvCell.IsIndoor(r.Cell) && world.Near(lb, q.X, q.Y, 0.3f)
                .Any(t => !t.Walkable && RynthCore.Geometry.Cells.Geo.DistToPolygon(new[] { t.A, t.B, t.C }, q + new Vector3(0, 0, 1.0f)) < 0.3f);
            if (!inWall) clear++;
        }
        T.Info($"15 m walks among obstacles: {walks}, {blocked} stopped, {grounded} standing on terrain or a surface, {clear} clear of walls");
        T.Check(grounded == walks, $"feet always on terrain, an object surface or a cell floor ({grounded}/{walks})");
        T.Check(clear == walks, $"never ends inside a wall ({clear}/{walks})");
    }

    private static void IntoBuilding(Walker walker, IndoorWorld indoor, TerrainSampler terrain)
    {
        int tried = 0, entered = 0, windows = 0;
        var misses = new List<string>();
        foreach (EnvCell c in indoor.LoadLandblock(0xA9B4))
        {
            if ((c.Flags & 1) == 0) continue;
            foreach (CellPortal p in c.Portals.Where(p => p.LeadsOutdoors && p.Polygon.Length >= 3))
            {
                Vector3 centre = p.Polygon.Aggregate(Vector3.Zero, (s, q) => s + q) / p.Polygon.Length;
                Vector3 n = CellDat.Normal(p.Polygon);
                var flat = Vector2.Normalize(new Vector2(n.X, n.Y));
                if (float.IsNaN(flat.X)) continue;
                // Outward = toward the side PortalSide names (the way TrackCell leaves the cell).
                Vector2 outward = p.PortalSide ? flat : -flat;
                var outside = new Vector2(centre.X, centre.Y) + outward * 3f;
                var inside = new Vector2(centre.X, centre.Y) - outward * 1.2f;
                float z = terrain.GetStandingZ(0xA9B4, outside.X, outside.Y);
                // Windows are outdoor portals too: skip openings whose sill is more than a step above the ground.
                float sill = p.Polygon.Min(q => q.Z);
                float ground = terrain.GetStandingZ(0xA9B4, centre.X + outward.X * 0.6f, centre.Y + outward.Y * 0.6f);
                if (sill - ground > walker.StepUp + 0.1f) { windows++; continue; }
                tried++;
                var r = WalkTo(walker, Walker.OutdoorCell(0xA9B4, outside.X, outside.Y), new Vector3(outside, z), inside);
                if (r.Cell == c.Id || (EnvCell.IsIndoor(r.Cell) && r.Cell >> 16 == 0xA9B4)) entered++;
                else if (misses.Count < 6) misses.Add($"door of {c.Id:X8}: ended {r.Cell:X8} ({r.Position.X:F1},{r.Position.Y:F1},{r.Position.Z:F2}) {r.Why}");
            }
        }
        T.Info($"Holtburg doorways: {tried} tried, {entered} walked inside ({windows} windows skipped)");
        foreach (string m in misses) T.Info("  " + m);
        T.Check(tried > 5, "Holtburg has buildings with outdoor doorways");
        T.Check(entered >= tried * 0.8, $"walking through a doorway enters the building ({entered}/{tried})");
    }

    /// <summary>Steps toward a point in 0.4 m pieces until there, blocked, or 100 steps.</summary>
    private static StepResult WalkTo(Walker w, uint cell, Vector3 pos, Vector2 goal)
    {
        var r = new StepResult(cell, pos, false);
        for (int i = 0; i < 100; i++)
        {
            var d = goal - new Vector2(r.Position.X, r.Position.Y);
            float len = d.Length();
            if (len < 0.01f) break;
            float s = Math.Min(len, 0.4f);
            var n = w.Step(r.Cell, r.Position, d.X / len * s, d.Y / len * s);
            if (n.Blocked) return n;
            r = n;
        }
        return r;
    }

    private static float Dist2((uint Cell, float X, float Y, float Z) a, (uint Cell, float X, float Y, float Z) b)
        => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
