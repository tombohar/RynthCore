// ============================================================================
//  rynth-headless - ScenarioP2.cs
//  Live check of P2 movement (run with --scenario p2, an admin character on a test
//  server, the AC dats present). Each step prints PASS / FAIL / SKIP:
//
//    dungeon   walk legs in the character's (indoor) spot: every leg ends in a cell the
//              cell index agrees with, the server never corrects us
//    teleport  to open ground outside Holtburg (@teleloc)
//    route     run a 4-leg loop of waypoints on the terrain: heights follow the ground,
//              land cells right, no corrections
//    building  walk in through the nearest outdoor doorway: the cell becomes the building's
//    portal    find the nearest portal object, use it: the server asks us over, we walk,
//              it teleports us (new landblock), LoginComplete goes out
//    return    teleport back to where the character was
// ============================================================================

using RynthCore.Geometry.Cells;
using RynthCore.Geometry.Walking;
using RynthCore.Headless.Messages;
using RynthCore.Headless.Movement;
using RynthCore.Headless.Net;
using RynthCore.Headless.Session;
using RynthCore.Headless.World;

namespace RynthCore.Headless.Cli;

internal sealed class ScenarioP2
{
    private const string OpenGround = "0xA9B40029 133.603592 17.391838 94.005005 1 0 0 0";

    private readonly ClientSession _s;
    private readonly UdpLink _link;
    private readonly GeometryWalk _geo;
    private readonly Action<string> _out;
    private readonly List<(string Step, string Result)> _results = new();
    private int _corrections;
    private (bool Ok, string Why)? _finished;
    private WorldState W => _s.World;

    public ScenarioP2(ClientSession s, UdpLink link, GeometryWalk geo, Action<string> output)
    {
        _s = s;
        _link = link;
        _geo = geo;
        _out = output;
        W.PlayerCorrected += p => { _corrections++; _out($"  << server moved us to 0x{p.Cell:X8} ({p.X:F1},{p.Y:F1},{p.Z:F2})"); };
        _s.MoveFinished += (ok, why) => _finished = (ok, why);
        W.Error += (c, t) => _out($"  << WeenieError 0x{c:X} {t}");
        W.TransientMessage += t => _out($"  << transient: {t}");
    }

    public bool Run()
    {
        Wait(() => W.PlayerObject?.Position != null, 5000);
        WorldPosition? home = W.PlayerObject?.Position;
        StepDungeon();
        Teleport(OpenGround, "teleport");
        StepRoute();
        StepBuilding();
        StepPortal();
        if (home is { } h) Teleport($"0x{h.Cell:X8} {h.X:R} {h.Y:R} {h.Z:R} {h.Qw:R} {h.Qx:R} {h.Qy:R} {h.Qz:R}", "return");

        _out("");
        _out("P2 scenario results:");
        foreach (var (step, result) in _results) _out($"  {step,-9} {result}");
        return _results.All(r => !r.Result.StartsWith("FAIL"));
    }

    // ── steps ───────────────────────────────────────────────────────────────

    private void StepDungeon()
    {
        if (W.PlayerObject?.Position is not { } start || !EnvCell.IsIndoor(start.Cell)) { Result("dungeon", "SKIP not indoors"); return; }
        int legs = 0, consistent = 0, arrived = 0, before = _corrections;
        var rng = new Random(5);
        for (int i = 0; i < 8; i++)
        {
            WorldPosition p = W.PlayerObject!.Position!.Value;
            float ang = (float)(rng.NextDouble() * Math.PI * 2);
            if (Walk(Offset(p, MathF.Cos(ang) * 6, MathF.Sin(ang) * 6), 10_000)) arrived++;
            legs++;
            WorldPosition q = W.PlayerObject!.Position!.Value;
            var found = _geo.Indoor.FindCell(q.Cell >> 16, q.X, q.Y, q.Z);
            bool ok = found?.cellId == q.Cell;
            if (ok) consistent++;
            _out($"  leg {i + 1}: {(_finished?.Ok == true ? "arrived" : _finished?.Why)} at 0x{q.Cell:X8} ({q.X:F1},{q.Y:F1},{q.Z:F2}){(ok ? "" : $" - cell index says {found?.cellId:X8}")}");
        }
        // Back to the start so the character ends where it was.
        Walk(start, 15_000);
        int corrected = _corrections - before;
        Result("dungeon", consistent == legs && corrected == 0
            ? $"PASS {legs} legs ({arrived} reached, the rest stopped by walls), all cells consistent, no corrections"
            : $"FAIL {consistent}/{legs} consistent cells, {corrected} corrections");
    }

    private void StepRoute()
    {
        if (W.PlayerObject?.Position is not { } p0 || EnvCell.IsIndoor(p0.Cell)) { Result("route", "SKIP not outdoors"); return; }
        // An open 25 m square near here: every leg walkable in a straight line (checked with the same
        // geometry, offline). Walk to its first corner (the planner finds the way), then run the loop.
        List<WorldPosition>? points = null;
        for (float ring = 0; ring <= 160 && points == null; ring += 16)
        {
            int around = ring == 0 ? 1 : (int)(ring / 4);
            for (int k = 0; k < around && points == null; k++)
            {
                float a = k * MathF.PI * 2 / around;
                WorldPosition c = Offset(p0, MathF.Cos(a) * ring, MathF.Sin(a) * ring);
                c = c with { Z = _geo.Terrain.GetStandingZ(c.Cell >> 16, c.X, c.Y) };
                var cand = new List<WorldPosition>();
                WorldPosition cur = c;
                bool ok = _geo.Walker.CanStand(c.Cell, new System.Numerics.Vector3(c.X, c.Y, c.Z));
                foreach (var (e, n) in new[] { (0f, 25f), (25f, 0f), (0f, -25f), (-25f, 0f) })
                {
                    if (!ok) break;
                    var r = _geo.Walker.Step(cur.Cell, new System.Numerics.Vector3(cur.X, cur.Y, cur.Z), e, n);
                    if (r.Blocked || r.Slid || EnvCell.IsIndoor(r.Cell)) { ok = false; break; }
                    cur = cur with { Cell = r.Cell, X = r.Position.X, Y = r.Position.Y, Z = r.Position.Z };
                    cand.Add(cur);
                }
                if (ok)
                {
                    _out($">> open loop {ring:F0} m away at 0x{c.Cell:X8} ({c.X:F0},{c.Y:F0}); walking there");
                    if (!Walk(c, 60_000)) { _out($"  could not reach it ({_finished?.Why})"); continue; }
                    points = cand;
                }
            }
        }
        if (points == null) { Result("route", "SKIP no open 25 m loop within 160 m"); return; }
        p0 = W.PlayerObject!.Position!.Value;
        int before = _corrections, samples = 0, onGround = 0;
        _finished = null;
        _s.WalkPath(points);
        long end = Environment.TickCount64 + 60_000;
        while (_finished == null && Environment.TickCount64 < end && !_s.Finished)
        {
            Pump(100);
            if (W.PlayerObject?.Position is { } q && !EnvCell.IsIndoor(q.Cell))
            {
                samples++;
                float g = _geo.Terrain.GetStandingZ(q.Cell >> 16, q.X, q.Y);
                if (q.Z >= g - 0.01f && q.Z < g + 3f && q.Cell == Walker.OutdoorCell(q.Cell >> 16, q.X, q.Y)) onGround++;
            }
        }
        WorldPosition last = W.PlayerObject!.Position!.Value;
        float back = Distance(last, p0);
        int corrected = _corrections - before;
        Result("route", _finished?.Ok == true && corrected == 0 && onGround == samples
            ? $"PASS 100 m loop, {samples} samples on the ground with the right land cells, ended {back:F1} m from the start"
            : $"FAIL finished {_finished?.Ok} ({_finished?.Why}), {onGround}/{samples} on ground, {corrected} corrections");
    }

    private void StepBuilding()
    {
        if (W.PlayerObject?.Position is not { } p || EnvCell.IsIndoor(p.Cell)) { Result("building", "SKIP not outdoors"); return; }
        uint lb = p.Cell >> 16;
        // The nearest outdoor doorway whose threshold the walker can reach from the ground.
        var doors = new List<(EnvCell Cell, System.Numerics.Vector3 Centre, System.Numerics.Vector2 Out, float D)>();
        foreach (EnvCell c in _geo.Indoor.LoadLandblock(lb))
        {
            if ((c.Flags & 1) == 0) continue;
            foreach (CellPortal cp in c.Portals.Where(x => x.LeadsOutdoors && x.Polygon.Length >= 3))
            {
                var centre = cp.Polygon.Aggregate(System.Numerics.Vector3.Zero, (s, q) => s + q) / cp.Polygon.Length;
                var n = CellDat.Normal(cp.Polygon);
                var flat = System.Numerics.Vector2.Normalize(new System.Numerics.Vector2(n.X, n.Y));
                if (float.IsNaN(flat.X)) continue;
                var outward = cp.PortalSide ? flat : -flat;
                float d = MathF.Sqrt((centre.X - p.X) * (centre.X - p.X) + (centre.Y - p.Y) * (centre.Y - p.Y));
                // Skip windows (sill more than a step above the ground outside).
                float ground = _geo.Terrain.GetStandingZ(lb, centre.X + outward.X * 0.6f, centre.Y + outward.Y * 0.6f);
                if (cp.Polygon.Min(q => q.Z) - ground > 0.55f) continue;
                doors.Add((c, centre, outward, d));
            }
        }
        foreach (var door in doors.OrderBy(d => d.D).Take(6))
        {
            var outside = door.Centre with { X = door.Centre.X + door.Out.X * 3, Y = door.Centre.Y + door.Out.Y * 3 };
            var inside = door.Centre with { X = door.Centre.X - door.Out.X * 1.5f, Y = door.Centre.Y - door.Out.Y * 1.5f };
            _out($">> try the doorway of 0x{door.Cell.Id:X8}, {door.D:F0} m away");
            Walk(Local(lb, outside.X, outside.Y, p), 20_000);
            Walk(Local(lb, inside.X, inside.Y, p), 10_000);
            WorldPosition q = W.PlayerObject!.Position!.Value;
            if (EnvCell.IsIndoor(q.Cell))
            {
                bool agrees = _geo.Indoor.FindCell(lb, q.X, q.Y, q.Z)?.cellId == q.Cell;
                Walk(Local(lb, outside.X, outside.Y, p), 10_000);   // and out again
                WorldPosition o = W.PlayerObject!.Position!.Value;
                Result("building", agrees && !EnvCell.IsIndoor(o.Cell)
                    ? $"PASS walked into 0x{q.Cell:X8} and back out to 0x{o.Cell:X8}"
                    : $"FAIL inside 0x{q.Cell:X8} (index agrees {agrees}), after leaving 0x{o.Cell:X8}");
                return;
            }
            _out($"  stayed outside ({_finished?.Why})");
        }
        Result("building", doors.Count == 0 ? "SKIP no doorways here" : "FAIL could not walk into any of the nearest doorways");
    }

    private void StepPortal()
    {
        if (W.PlayerObject?.Position is not { } p) { Result("portal", "SKIP no position"); return; }
        WorldObject? portal = W.Objects.Values
            .Where(o => (o.Desc.DescriptionFlags & 0x40000) != 0 && o.Position != null && o.Position.Value.Cell >> 16 == p.Cell >> 16)
            .OrderBy(o => Distance(o.Position!.Value, p)).FirstOrDefault();
        if (portal == null) { Result("portal", "SKIP no portal in this landblock"); return; }
        uint lb0 = p.Cell >> 16;
        _out($">> use {portal} at {Distance(portal.Position!.Value, p):F0} m");
        int before = _corrections;
        _s.Use(portal.Id);
        bool moved = Wait(() => W.PlayerObject?.Position is { } q && q.Cell >> 16 != lb0, 30_000);
        Wait(() => false, 3000);
        WorldPosition dest = W.PlayerObject!.Position!.Value;
        Result("portal", moved
            ? $"PASS {portal.Name} took us to 0x{dest.Cell:X8} ({_corrections - before} position updates from the server)"
            : $"FAIL still in 0x{lb0:X4}xxxx after 30 s ({_finished?.Why})");
    }

    private void Teleport(string where, string step)
    {
        uint lb = Convert.ToUInt32(where.Split(' ')[0], 16) >> 16;
        _out($">> @teleloc {where}");
        _s.Say($"@teleloc {where}");
        bool moved = Wait(() => W.PlayerObject?.Position is { } p && p.Cell >> 16 == lb, 15_000);
        Wait(() => false, 3000);
        Result(step, moved ? $"PASS at 0x{W.PlayerObject!.Position!.Value.Cell:X8}" : "FAIL no position update");
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private bool Walk(WorldPosition to, int timeoutMs)
    {
        _finished = null;
        _s.WalkTo(to);
        Wait(() => _finished != null, timeoutMs);
        return _finished?.Ok == true;
    }

    private static WorldPosition Offset(WorldPosition p, float east, float north)
        => EnvCell.IsIndoor(p.Cell) ? p with { X = p.X + east, Y = p.Y + north } : ClientSession.Relocate(p, p.X + east, p.Y + north);

    private static WorldPosition Local(uint lb, float x, float y, WorldPosition like)
        => like with { Cell = Walker.OutdoorCell(lb, x, y), X = x, Y = y };

    private static float Distance(WorldPosition a, WorldPosition b)
    {
        (float dx, float dy) = ClientSession.Delta(a, b);
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private void Result(string step, string result)
    {
        _results.Add((step, result));
        _out($"== {step}: {result}");
    }

    private void Pump(int ms)
    {
        long end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end && !_s.Finished)
        {
            _link.Wait(15);
            _s.Tick();
        }
    }

    private bool Wait(Func<bool> done, int timeoutMs)
    {
        long end = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < end && !_s.Finished)
        {
            if (done()) return true;
            _link.Wait(15);
            _s.Tick();
        }
        return done();
    }
}
