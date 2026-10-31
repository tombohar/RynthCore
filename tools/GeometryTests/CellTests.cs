// ============================================================================
//  GeometryTests - CellTests.cs
//  Indoor cells (RynthCore.Geometry.Cells) and models (RynthCore.Geometry.Models)
//  against the dats and the creature positions captured from ACE in dungeon
//  landblock 0x6146 (Vectors\dungeon-6146.txt).
// ============================================================================

using System.Numerics;
using RynthCore.Geometry.Cells;
using RynthCore.Geometry.Dat;
using RynthCore.Geometry.Models;

namespace GeometryTests;

internal static class CellTests
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Register() => Suites.Add("cells", Run, order: 20);

    private const uint Dungeon = 0x6146;
    private const uint Town = 0xA9B4;

    private static void Run()
    {
        using var cellDat = new DatDatabase();
        using var portalDat = new DatDatabase();
        T.Check(cellDat.Open(Dats.Cell), "cell dat opens");
        T.Check(portalDat.Open(Dats.Portal), "portal dat opens");
        var world = new IndoorWorld(cellDat, portalDat);

        LoadsLandblock(world);
        PortalSides(world);
        var vectors = Dats.Vectors("dungeon-6146.txt");
        FindsCells(world, vectors);
        TestSpot(world);
        TracksCells(world, vectors);
        TracksOutdoors(world);
        ReadsModels(world, cellDat);
    }

    private static void LoadsLandblock(IndoorWorld world)
    {
        var cells = world.LoadLandblock(Dungeon);
        int bad = cells.Count(c => !c.GeometryOk);
        int portals = cells.Sum(c => c.Portals.Count), noPoly = cells.Sum(c => c.Portals.Count(p => p.Polygon.Length < 3));
        T.Info($"landblock {Dungeon:X4}: {cells.Count} EnvCells, {bad} without geometry, {cells.Sum(c => c.Floors.Count)} floors, " +
               $"{cells.Sum(c => c.Walls.Count)} walls, {portals} portals ({noPoly} without a polygon)");
        T.Check(cells.Count > 0, $"landblock {Dungeon:X4} has EnvCells");
        T.Check(bad == 0, $"{bad} cells of {Dungeon:X4} failed to parse geometry: " +
                          string.Join(" ", cells.Where(c => !c.GeometryOk).Take(10).Select(c => c.Id.ToString("X8"))));
        T.Check(noPoly == 0, $"{noPoly} portals of {Dungeon:X4} have no doorway polygon");
        T.Check(ReferenceEquals(cells, world.LoadLandblock(Dungeon << 16 | 0x100)), "landblock cells are cached (same list by key or cell id)");
        T.Check(cells.All(c => EnvCell.IsIndoor(c.Id) && c.Landblock == Dungeon), "every cell id is an indoor cell of the landblock");
    }

    // CellPortal.PortalSide says which side of the doorway polygon the cell behind it is on (TrackCell
    // relies on it). Check it against the cells' plans: just ahead of every wall doorway, on the side it
    // names, the neighbour covers the floor plan, and just behind it this cell does. And both cells of a
    // doorway must agree (one says front, the other back of the same polygon).
    private static void PortalSides(IndoorWorld world)
    {
        int tested = 0, ok = 0, pairs = 0, pairsOk = 0, shown = 0;
        foreach (var c in world.LoadLandblock(Dungeon))
            foreach (var p in c.Portals)
            {
                if (p.Polygon.Length < 3 || p.LeadsOutdoors) continue;
                var other = world.GetCell(c.Neighbour(p));
                if (other == null) continue;
                Vector3 n = CellDat.Normal(p.Polygon);
                // The neighbour's portal back to this cell: same polygon, so the opposite side.
                var back = other.Portals.FirstOrDefault(q => q.OtherCell == c.Short && q.Polygon.Length >= 3
                    && Vector3.Distance(q.Polygon.Aggregate(Vector3.Zero, (s, v) => s + v) / q.Polygon.Length,
                                        p.Polygon.Aggregate(Vector3.Zero, (s, v) => s + v) / p.Polygon.Length) < 0.05f);
                if (back != null)
                {
                    pairs++;
                    bool sameNormal = Vector3.Dot(CellDat.Normal(back.Polygon), n) > 0;
                    if ((back.PortalSide == p.PortalSide) != sameNormal) pairsOk++;
                }
                if (MathF.Abs(n.Z) > 0.5f) continue;
                Vector3 centre = p.Polygon.Aggregate(Vector3.Zero, (s, v) => s + v) / p.Polygon.Length;
                Vector3 h = Vector3.Normalize(new Vector3(n.X, n.Y, 0)) * (p.PortalSide ? 0.25f : -0.25f);
                tested++;
                bool ahead = other.CoversXY(centre.X + h.X, centre.Y + h.Y, 0), behind = c.CoversXY(centre.X - h.X, centre.Y - h.Y, 0);
                if (ahead && behind) ok++;
                else if (shown++ < 5)
                    T.Info($"portal {c.Id:X8} -> {other.Id:X8} flags {p.Flags:X}: neighbour covers ahead {ahead}, this cell behind {behind}");
            }
        T.Info($"PortalSide: {ok}/{tested} wall doorways have the neighbour ahead and this cell behind; {pairsOk}/{pairs} doorway pairs agree");
        T.Check(tested > 100 && ok >= tested * 0.98, $"PortalSide matches the cells' plans ({ok}/{tested})");
        T.Check(pairs > 100 && pairsOk == pairs, $"both cells of a doorway agree on its sides ({pairsOk}/{pairs})");
    }

    private static void FindsCells(IndoorWorld world, List<(uint Cell, float X, float Y, float Z)> vectors)
    {
        // Creatures touch the floor with their lowest collision sphere: on a slope ACE's z (the feet) is
        // above the floor plane, so the floor plane matches z on flat floor and StandZ matches it everywhere.
        var human = world.Models.Get(ModelReader.HumanSetup);
        float radius = human.Spheres.Count > 0 ? human.Spheres.Min(s => s.Radius) : 0.48f;
        int cellOk = 0, findZOk = 0, floorOk = 0, standOk = 0, none = 0, flat = 0, flatOk = 0, shown = 0;
        float worstStand = 0;
        foreach (var v in vectors)
        {
            var found = world.FindCell(Dungeon, v.X, v.Y, v.Z);
            float? fz = world.FloorZ(v.Cell, v.X, v.Y, v.Z);
            float? sz = world.StandZ(v.Cell, v.X, v.Y, radius, v.Z);
            bool cOk = found?.cellId == v.Cell;
            bool zOk = fz != null && MathF.Abs(fz.Value - v.Z) <= 0.05f;
            bool sOk = sz != null && MathF.Abs(sz.Value - v.Z) <= 0.05f;
            if (cOk) cellOk++;
            if (found == null) none++;
            if (found != null && MathF.Abs(found.Value.floorZ - v.Z) <= 0.05f) findZOk++;
            if (zOk) floorOk++;
            if (sOk) standOk++;
            if (sz != null) worstStand = MathF.Max(worstStand, MathF.Abs(sz.Value - v.Z));
            var cell = world.GetCell(v.Cell);
            if (cell != null && cell.Floors.Where(f => Geo.InsideXY(f, v.X, v.Y, 0.6f)).All(f => CellDat.Normal(f).Z > 0.999f))
            {
                flat++;
                if (zOk) flatOk++;
            }
            if ((!cOk || !sOk) && shown++ < 15)
                T.Info($"mismatch {v.Cell:X8} ({v.X:F3},{v.Y:F3},{v.Z:F3}): FindCell " +
                       (found == null ? "none" : $"{found.Value.cellId:X8} floor {found.Value.floorZ:F3}") +
                       $", FloorZ(own cell) {(fz == null ? "none" : fz.Value.ToString("F3"))}, StandZ {(sz == null ? "none" : sz.Value.ToString("F3"))}");
        }
        int n = vectors.Count;
        double cellRate = (double)cellOk / n, floorRate = (double)floorOk / n, standRate = (double)standOk / n;
        double flatRate = flat == 0 ? 0 : (double)flatOk / flat;
        T.Info($"FindCell: {cellOk}/{n} cells match ({cellRate:P2}), {none} found nothing, {findZOk}/{n} with the floor plane within 0.05 m of z");
        T.Info($"FloorZ(own cell, floor plane): {floorOk}/{n} within 0.05 m ({floorRate:P2}); on flat floor (no slope within 0.6 m) {flatOk}/{flat} ({flatRate:P2})");
        T.Info($"StandZ(own cell, sphere r {radius:F2}): {standOk}/{n} within 0.05 m ({standRate:P2}), worst {worstStand:F3} m");
        T.Check(n > 1000, $"dungeon vectors loaded ({n})");
        T.Check(cellRate >= 0.95, $"FindCell matches the captured cell for >= 95% ({cellRate:P2})");
        T.Check(flatRate >= 0.95, $"FloorZ on flat floor within 0.05 m for >= 95% ({flatRate:P2})");
        T.Check(standRate >= 0.95, $"StandZ within 0.05 m for >= 95% ({standRate:P2})");
    }

    private static void TestSpot(IndoorWorld world)
    {
        var found = world.FindCell(Dungeon, 91.617874f, -157.55351f, -11.9885f);
        T.Info($"the test character's spot (91.618,-157.554,-11.989): {(found == null ? "none" : $"{found.Value.cellId:X8} floor {found.Value.floorZ:F3}")}");
        T.Check(found?.cellId == 0x61460249, "the test character's spot is in cell 61460249");
    }

    private static void TracksCells(IndoorWorld world, List<(uint Cell, float X, float Y, float Z)> vectors)
    {
        // Pairs of captured positions in portal-connected cells, a few metres apart: walking straight from
        // one to the other should end in the other's cell (unless the straight line goes through a wall).
        int pairs = 0, ok = 0, stayed = 0, wrong = 0, shown = 0;
        int same = 0, sameOk = 0;
        for (int i = 0; i < vectors.Count; i++)
            for (int j = 0; j < vectors.Count; j++)
            {
                if (i == j) continue;
                var p = vectors[i]; var q = vectors[j];
                var a = new Vector3(p.X, p.Y, p.Z); var b = new Vector3(q.X, q.Y, q.Z);
                float d = Vector3.Distance(a, b);
                if (d > 4f) continue;
                if (p.Cell == q.Cell)
                {
                    if (same >= 2000 || d < 1f) continue;
                    same++;
                    uint s = world.TrackCell(p.Cell, a, b);
                    if (s == p.Cell) sameOk++;
                    else if (same - sameOk <= 5)
                        T.Info($"track within {p.Cell:X8} ({p.X:F2},{p.Y:F2},{p.Z:F2}) -> ({q.X:F2},{q.Y:F2},{q.Z:F2}) ended in {s:X8}");
                    continue;
                }
                if (!world.Neighbours(p.Cell).Contains(q.Cell)) continue;
                pairs++;
                uint got = world.TrackCell(p.Cell, a, b);
                if (got == q.Cell) ok++;
                else if (got == p.Cell)
                {
                    stayed++;
                    if (stayed <= 5)
                        T.Info($"track {p.Cell:X8} ({p.X:F2},{p.Y:F2},{p.Z:F2}) -> {q.Cell:X8} ({q.X:F3},{q.Y:F3},{q.Z:F3}) stayed in the start cell");
                }
                else
                {
                    wrong++;
                    if (shown++ < 8)
                        T.Info($"track {p.Cell:X8} ({p.X:F2},{p.Y:F2},{p.Z:F2}) -> {q.Cell:X8} ({q.X:F2},{q.Y:F2},{q.Z:F2}) ended in {got:X8}");
                }
            }
        double rate = pairs == 0 ? 0 : (double)ok / pairs;
        T.Info($"TrackCell, adjacent cells within 4 m: {ok}/{pairs} reach the other cell ({rate:P2}), {stayed} stayed in the start cell (blocked, or the end is on its side of the doorway), {wrong} in a third cell");
        T.Info($"TrackCell, same cell 1-4 m apart: {sameOk}/{same} stay in the cell");
        T.Check(pairs >= 50, $"enough adjacent-cell pairs ({pairs})");
        T.Check(rate >= 0.99, $"TrackCell reaches the neighbour cell for >= 99% of pairs ({rate:P2})");
        // Through a wall: from the middle of the junction cell 1F0 (floor -18, an octagon around (60,-160)
        // with doorways on its four straight sides) diagonally out through its south-east chamfer wall.
        uint walled = world.TrackCell(0x614601F0, new Vector3(60, -160, -18), new Vector3(66, -166, -18));
        T.Check(walled == 0x614601F0, $"TrackCell through a wall keeps the start cell (got {walled:X8})");
        T.Check(wrong == 0, $"TrackCell never ends in a third cell ({wrong})");
        T.Check(same > 0 && sameOk == same, $"TrackCell inside one cell stays there ({sameOk}/{same})");
    }

    // Walk out of a town building through a doorway that leads outdoors: TrackCell gives 0.
    private static void TracksOutdoors(IndoorWorld world)
    {
        var cells = world.LoadLandblock(Town);
        int tried = 0, ok = 0;
        foreach (var c in cells)
            foreach (var p in c.Portals.Where(p => p.LeadsOutdoors && p.Polygon.Length >= 3))
            {
                Vector3 centre = p.Polygon.Aggregate(Vector3.Zero, (s, v) => s + v) / p.Polygon.Length;
                Vector3 n = CellDat.Normal(p.Polygon);
                if (MathF.Abs(n.Z) > 0.5f) continue;                  // a doorway in a wall
                float floor = p.Polygon.Min(v => v.Z);
                Vector3 h = Vector3.Normalize(new Vector3(n.X, n.Y, 0));
                // Inside is the side over the cell's floor.
                Vector3 inA = centre + h * 0.75f, inB = centre - h * 0.75f;
                bool aIn = IndoorWorld.CellFloorZ(c, inA.X, inA.Y) != null, bIn = IndoorWorld.CellFloorZ(c, inB.X, inB.Y) != null;
                if (aIn == bIn) continue;
                Vector3 from = aIn ? inA : inB, to = aIn ? inB : inA;
                from.Z = to.Z = floor;
                tried++;
                uint got = world.TrackCell(c.Id, from, to);
                if (got == 0) ok++;
                else if (tried - ok <= 5) T.Info($"walking out of {c.Id:X8} at ({centre.X:F2},{centre.Y:F2}) gave {got:X8}");
            }
        T.Info($"TrackCell out through outdoor doorways of {Town:X4}: {ok}/{tried} give 0 (outdoors)");
        T.Check(tried > 0 && ok == tried, $"walking out through an outdoor doorway gives 0 ({ok}/{tried})");
    }

    private static void ReadsModels(IndoorWorld world, DatDatabase cellDat)
    {
        var human = world.Models.Get(ModelReader.HumanSetup);
        T.Info($"setup {ModelReader.HumanSetup:X8}: parsed {human.Parsed}, radius {human.Radius:F3}, height {human.Height:F3}, " +
               $"{human.Cylinders.Count} cylinders [{string.Join(", ", human.Cylinders.Select(c => $"r {c.Radius:F3} h {c.Height:F3}"))}], " +
               $"{human.Spheres.Count} spheres [{string.Join(", ", human.Spheres.Select(s => $"r {s.Radius:F3} at z {s.Center.Z:F3}"))}], {human.Polygons.Count} polygons");
        T.Check(human.Parsed, "human setup parses");
        T.Check(human.Spheres.Count + human.Cylinders.Count > 0, "human setup has collision spheres or cylinders");

        var info = LandblockInfo.Load(cellDat, Town);
        T.Check(info != null && info.Complete, $"LandblockInfo {Town:X4}FFFE reads completely");
        if (info == null) return;
        T.Info($"LandblockInfo {info.Id:X8}: {info.NumCells} cells, {info.Objects.Count} objects, {info.Buildings.Count} buildings");
        int withPolys = 0;
        foreach (var b in info.Buildings.Take(8))
        {
            var shape = world.Models.Get(b.ModelId);
            if (shape.Polygons.Count > 0) withPolys++;
            var placed = shape.Place(b.Origin, b.Rotation).ToList();
            string span = placed.Count == 0 ? "" :
                $", z {placed.Min(p => p.Min(v => v.Z)):F1}..{placed.Max(p => p.Max(v => v.Z)):F1} at ({b.Origin.X:F1},{b.Origin.Y:F1})";
            T.Info($"  building {b.ModelId:X8}: parsed {shape.Parsed}, {shape.Polygons.Count} physics polygons, " +
                   $"portals to [{string.Join(" ", b.PortalCells.Select(c => c.ToString("X4")))}]{span}");
        }
        T.Check(info.Buildings.Count > 0 && withPolys > 0, "town buildings have physics polygons");
        // Building portals open into the landblock's own cells.
        var ids = world.LoadLandblock(Town).Select(c => c.Short).ToHashSet();
        int portalCells = info.Buildings.Sum(b => b.PortalCells.Count), known = info.Buildings.Sum(b => b.PortalCells.Count(ids.Contains));
        T.Check(portalCells > 0 && known == portalCells, $"building portals lead to cells of the landblock ({known}/{portalCells})");
    }
}
