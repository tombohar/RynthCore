// ============================================================================
//  RynthCore.Headless - Movement/GeometryWalk.cs
//  IWalkGeometry on RynthCore.Geometry's Walker: terrain heights, indoor cells and
//  their walls, the outdoor world (buildings, statics, scenery) and closed doors.
//  One per process is enough; the dat readers and caches inside are thread-safe and
//  shared by every session (each session's doors: TrackDoors).
// ============================================================================

using System.Numerics;
using RynthCore.Geometry.Cells;
using RynthCore.Geometry.Collision;
using RynthCore.Geometry.Dat;
using RynthCore.Geometry.Models;
using RynthCore.Geometry.Terrain;
using RynthCore.Geometry.Walking;
using RynthCore.Headless.Messages;
using RynthCore.Headless.World;

namespace RynthCore.Headless.Movement;

public sealed class GeometryWalk : IWalkGeometry, IDisposable
{
    private readonly DatDatabase _portal, _cell;

    public TerrainSampler Terrain { get; }
    public IndoorWorld Indoor { get; }
    public ModelReader Models { get; }
    public Walker Walker { get; private set; }
    public OutdoorCollision World { get; }
    public DoorObstacles Doors { get; }

    private GeometryWalk(DatDatabase portal, DatDatabase cell)
    {
        _portal = portal;
        _cell = cell;
        Terrain = new TerrainSampler(portal, cell);
        Models = new ModelReader(portal);
        Indoor = new IndoorWorld(cell, portal, Models);
        World = new OutdoorCollision(cell, portal, Terrain, Models, Indoor);
        Doors = new DoorObstacles(Models, portal);
        Walker = new Walker(Terrain, Indoor, new CompositeObstacles(new StaticObstacles(World), Doors.Obstacles));
    }

    /// <summary>Opens the dats in <paramref name="acFolder"/> (read-only, shared). Null if they aren't there.</summary>
    public static GeometryWalk? Open(string acFolder)
    {
        var portal = new DatDatabase();
        var cell = new DatDatabase();
        if (!portal.Open(Path.Combine(acFolder, "client_portal.dat")) || !cell.Open(Path.Combine(acFolder, "client_cell_1.dat")))
        {
            portal.Dispose();
            cell.Dispose();
            return null;
        }
        return new GeometryWalk(portal, cell);
    }

    /// <summary>Keeps the closed doors of a session's world as obstacles.</summary>
    public void TrackDoors(WorldState world) => Doors.Track(world);

    public WalkResult Step(WorldPosition start, float dx, float dy)
    {
        StepResult r = Walker.Step(start.Cell, new Vector3(start.X, start.Y, start.Z), dx, dy);
        var p = start with { Cell = r.Cell, X = r.Position.X, Y = r.Position.Y, Z = r.Position.Z };
        return new WalkResult(p, r.Blocked, r.Why);
    }

    public IReadOnlyList<WorldPosition>? Plan(WorldPosition start, WorldPosition to, float stop) => Plan(start, to, stop, nearest: false);

    /// <summary>With <paramref name="nearest"/>, an unreachable goal gives a path to the closest reachable point.</summary>
    public IReadOnlyList<WorldPosition>? Plan(WorldPosition start, WorldPosition to, float stop, bool nearest)
    {
        // Don't search toward a spot nobody can stand on (inside a wall, off a ledge, in the void)
        // unless it's somewhere the server wants us near (its move-to: get as close as the walls allow).
        // An object to walk up to (stop >= 0.5) is itself solid, so that check is skipped then.
        if (!nearest && stop < 0.5f && !Walker.CanStand(to.Cell, new Vector3(to.X, to.Y, to.Z))) return null;
        var planner = new LocalPlanner(Walker);
        (float dx, float dy) = Session.ClientSession.Delta(start, to);
        var goal = new Vector2(start.X + dx, start.Y + dy);
        List<PathPoint>? path = planner.Find(new PathPoint(start.Cell, new Vector3(start.X, start.Y, start.Z)), goal, stop, nearest);
        return path?.Skip(1).Select(p => start with { Cell = p.Cell, X = p.Position.X, Y = p.Position.Y, Z = p.Position.Z }).ToList();
    }

    public void Dispose()
    {
        Terrain.Dispose();
        _portal.Dispose();
        _cell.Dispose();
    }
}
