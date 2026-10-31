// ============================================================================
//  RynthCore.Headless - Movement/IWalkGeometry.cs
//  How a step lands in the world: ground height, walls, and which cell the player is
//  in afterwards (ACE trusts the client's cell id, so it has to be right). The real
//  implementation sits on RynthCore.Geometry (terrain, indoor cells, models); FlatWalk
//  is the fallback without dats: straight line, height kept, outdoor cell recomputed.
// ============================================================================

using RynthCore.Headless.Messages;
using RynthCore.Headless.Session;

namespace RynthCore.Headless.Movement;

/// <summary>Where a step ended. Blocked = it could not go (all) the way.</summary>
public readonly record struct WalkResult(WorldPosition Position, bool Blocked, string? Why = null);

public interface IWalkGeometry
{
    /// <summary>
    /// Moves from <paramref name="from"/> by (dx, dy) metres on the ground plane, in the frame
    /// of from's landblock (so x/y may leave 0..192 outdoors). Returns the resolved position:
    /// height on the ground or floor, cell id, and whether a wall or ledge stopped it.
    /// The rotation is copied from <paramref name="from"/>.
    /// </summary>
    WalkResult Step(WorldPosition from, float dx, float dy);

    /// <summary>
    /// A short walkable detour from <paramref name="from"/> to within <paramref name="stop"/> of
    /// <paramref name="to"/> (turning points after the start), or null when there is none or no planner.
    /// </summary>
    IReadOnlyList<WorldPosition>? Plan(WorldPosition from, WorldPosition to, float stop) => null;

    /// <summary>As <see cref="Plan(WorldPosition, WorldPosition, float)"/>; with <paramref name="nearest"/> an unreachable goal gives a path to the closest reachable point.</summary>
    IReadOnlyList<WorldPosition>? Plan(WorldPosition from, WorldPosition to, float stop, bool nearest) => Plan(from, to, stop);
}

public sealed class FlatWalk : IWalkGeometry
{
    public WalkResult Step(WorldPosition from, float dx, float dy)
        => new(ClientSession.Relocate(from, from.X + dx, from.Y + dy), false);
}
