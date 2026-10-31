// ============================================================================
//  RynthCore.Headless - Session/ClientSession.Movement.cs
//  Walking: to a point, to an object, or along a list of waypoints; turning.
//
//  What ACE needs (non-PK players): the position the client reports is taken as given,
//  cell id included (only >50 m jumps across landblocks, dungeon-to-dungeon moves and
//  rising >10 m without a jump are refused), so the walk geometry decides ground height,
//  walls and the cell. Two ways of moving:
//
//    - our own walk (WalkTo / WalkPath / Approach): a MoveToState "run forward" at the
//      start (the server animates us for everyone and echoes our run rate), an
//      AutonomousPosition every ReportMs while moving, and a MoveToState "stop" plus a
//      final position at the end;
//    - the server's move-to (it wants us next to something before a use/pickup/give
//      completes): AutonomousPosition only, every ServerMoveReportMs, because a
//      MoveToState would cancel the server's pending action.
//
//  Speed is 4 m/s x the run rate the server echoed (1.0 until it has), stepped every
//  StepMs. Turning is instant (the new heading goes out with the next report).
// ============================================================================

using RynthCore.Headless.Messages;
using RynthCore.Headless.Movement;
using RynthCore.Headless.World;

namespace RynthCore.Headless.Session;

public sealed partial class ClientSession
{
    public const long StepMs = 100;
    public const long ReportMs = 500;
    public const long ServerMoveReportMs = 200;

    private readonly Queue<WorldPosition> _waypoints = new();
    private readonly Queue<WorldPosition> _detour = new();
    private int _plans;
    public const int MaxPlans = 3;
    private uint _moveTarget;
    private float _moveStop;
    private long _moveDeadline, _lastStepMs, _lastReportMs, _lastProgressMs;
    private bool _serverMove, _announced;
    private float _bestDistance;

    /// <summary>How steps meet the world. FlatWalk until a geometry-backed walker is set.</summary>
    public IWalkGeometry Walker { get; set; } = new FlatWalk();

    /// <summary>True while the player is walking.</summary>
    public bool Moving => _moveTarget != 0 || _waypoints.Count > 0;

    /// <summary>A walk ended: arrived, or why not.</summary>
    public event Action<bool, string>? MoveFinished;

    /// <summary>Ground speed in m/s.</summary>
    public float MoveSpeed => MotionCommand.RunSpeed * (World.PlayerRunRate ?? 1f);

    /// <summary>Walks to within <paramref name="stop"/> metres of an object.</summary>
    public void Approach(uint target, float stop = 0.5f, long timeoutMs = 30_000)
        => BeginMove(target, Array.Empty<WorldPosition>(), stop, timeoutMs, serverMove: false);

    /// <summary>Walks to a point.</summary>
    public void WalkTo(WorldPosition point, float stop = 0.2f, long timeoutMs = 60_000)
        => BeginMove(0, new[] { point }, stop, timeoutMs, serverMove: false);

    /// <summary>Walks through each waypoint in turn (a route from a navigator).</summary>
    public void WalkPath(IEnumerable<WorldPosition> waypoints, float stop = 0.3f, long timeoutMs = 300_000)
        => BeginMove(0, waypoints, stop, timeoutMs, serverMove: false);

    /// <summary>Stops walking (and tells the server, if this was our own walk).</summary>
    public void StopMoving() => EndMove(false, "stopped");

    /// <summary>Faces a heading (degrees, 0 = north, clockwise) and reports it.</summary>
    public void TurnTo(float headingDegrees)
    {
        if (World.PlayerObject?.Position is not { } p) return;
        SendPosition(WithHeading(p, headingDegrees * MathF.PI / 180f));
    }

    /// <summary>Faces an object.</summary>
    public void TurnToward(uint obj)
    {
        if (World.PlayerObject?.Position is not { } p || World.Get(obj)?.Position is not { } t) return;
        (float dx, float dy) = Delta(p, t);
        SendPosition(WithHeading(p, MathF.Atan2(dx, dy)));
    }

    /// <summary>Reports the player's position (AutonomousPosition).</summary>
    public void SendPosition(WorldPosition p)
    {
        WorldObject? me = World.PlayerObject;
        if (me == null) return;
        me.Position = p;
        SendAction(ActionType.AutonomousPosition,
            GameActions.AutonomousPosition(p, me.InstanceSeq, me.ServerControlSeq, me.TeleportSeq, me.ForcePositionSeq, onGround: true));
        _lastReportMs = _now();
    }

    private void SendMotion(RawMotion m)
    {
        WorldObject? me = World.PlayerObject;
        if (me?.Position is not { } p) return;
        SendAction(ActionType.MoveToState,
            MotionMessages.MoveToState(m, p, me.InstanceSeq, me.ServerControlSeq, me.TeleportSeq, me.ForcePositionSeq, onGround: true));
    }

    private void BeginMove(uint target, IEnumerable<WorldPosition> points, float stop, long timeoutMs, bool serverMove)
    {
        if (Moving) EndMove(false, "replaced");
        _moveTarget = target;
        _waypoints.Clear();
        _detour.Clear();
        _plans = 0;
        foreach (WorldPosition p in points) _waypoints.Enqueue(p);
        _moveStop = stop;
        _serverMove = serverMove;
        _announced = false;
        long now = _now();
        _moveDeadline = now + timeoutMs;
        _lastStepMs = now;
        _lastProgressMs = now;
        _bestDistance = float.MaxValue;
    }

    private void EndMove(bool arrived, string why)
    {
        if (!Moving) return;
        bool tell = _announced && !_serverMove;
        _moveTarget = 0;
        _waypoints.Clear();
        _detour.Clear();
        if (tell)
        {
            SendMotion(RawMotion.Stop);
            if (World.PlayerObject?.Position is { } p) SendPosition(p);
        }
        MoveFinished?.Invoke(arrived, why);
    }

    private void OnServerMoveTo(MotionUpdate m)
    {
        float stop = Math.Max(0.3f, m.DistanceToObject * 0.8f);
        if (m.MovementType == 6 && m.TargetId != 0 && World.Get(m.TargetId)?.Position != null)
        {
            Info($"server asks to move to 0x{m.TargetId:X8} (within {m.DistanceToObject:F1} m)");
            BeginMove(m.TargetId, Array.Empty<WorldPosition>(), stop, 15_000, serverMove: true);
        }
        else if (m.Origin is { } o)
        {
            Info($"server asks to move to a point in 0x{o.Cell:X8}");
            BeginMove(0, new[] { o }, stop, 15_000, serverMove: true);
        }
    }

    private void TickMovement()
    {
        if (!Moving || State != SessionState.InWorld) return;
        long now = _now();
        if (now - _lastStepMs < StepMs) return;
        WorldObject? me = World.PlayerObject;
        if (me?.Position is not { } from) { EndMove(false, "no position"); return; }
        if (now > _moveDeadline) { EndMove(false, "timed out"); return; }

        WorldPosition? goal = _moveTarget != 0 ? World.Get(_moveTarget)?.Position : _waypoints.Count > 0 ? _waypoints.Peek() : null;
        if (goal is not { } to) { EndMove(false, "target gone"); return; }
        bool lastLeg = _moveTarget != 0 || _waypoints.Count <= 1;
        float stopAt = lastLeg ? _moveStop : 0.3f;

        if (!SameFrame(from, to)) { EndMove(false, "target is in another dungeon / indoor landblock"); return; }
        WorldPosition finalGoal = to;
        float finalStop = stopAt;
        if (_detour.Count > 0)
        {
            // Around an obstacle first: the detour's points, then on to the goal.
            if (Distance2D(from, _detour.Peek()) <= 0.3f) { _detour.Dequeue(); return; }
            to = _detour.Peek();
            stopAt = 0.05f;
        }
        (float dx, float dy) = Delta(from, to);
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        if (dist <= stopAt + 0.05f)
        {
            if (_detour.Count > 0) { _detour.Dequeue(); return; }
            if (!lastLeg) { _waypoints.Dequeue(); return; }
            EndMove(true, "arrived");
            return;
        }

        if (!_announced && !_serverMove)
        {
            SendMotion(RawMotion.RunForward);
            _announced = true;
        }

        float dt = Math.Min(now - _lastStepMs, 3 * StepMs) / 1000f;
        _lastStepMs = now;
        float move = Math.Min(MoveSpeed * dt, dist - stopAt);
        float heading = MathF.Atan2(dx, dy);
        WalkResult r = Walker.Step(WithHeading(from, heading), dx / dist * move, dy / dist * move);
        me.Position = r.Position;

        float left = Distance2D(r.Position, finalGoal);
        if (left < _bestDistance - 0.05f) { _bestDistance = left; _lastProgressMs = now; }
        IReadOnlyList<WorldPosition>? detour = null;
        if (r.Blocked && _plans < MaxPlans)
        {
            _plans++;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // The server's move-to only needs us within its use range: get as close as the walls allow.
            detour = Walker.Plan(r.Position, finalGoal, finalStop, nearest: _serverMove);
            Info($"blocked ({r.Why}) at 0x{r.Position.Cell:X8} ({r.Position.X:F1},{r.Position.Y:F1}); planned a detour to 0x{finalGoal.Cell:X8} ({finalGoal.X:F1},{finalGoal.Y:F1}): " +
                 $"{(detour == null ? "none" : detour.Count + " points")} in {sw.ElapsedMilliseconds} ms");
        }
        if (detour is { Count: 0 })
        {
            // The planner says we're already as close as the walls allow.
            SendPosition(r.Position);
            EndMove(true, "as close as it gets");
            return;
        }
        if (detour is { Count: > 0 })
        {
            // Blocked: walk around (a short planned detour), then carry on.
            _detour.Clear();
            foreach (WorldPosition d in detour) _detour.Enqueue(d);
            _lastProgressMs = now;
        }
        else if (r.Blocked && now - _lastProgressMs > 1500)
        {
            SendPosition(r.Position);
            EndMove(false, $"blocked{(r.Why != null ? ": " + r.Why : "")}");
            return;
        }

        long every = _serverMove ? ServerMoveReportMs : ReportMs;
        if (now - _lastReportMs >= every || Distance2D(r.Position, to) <= stopAt + 0.05f) SendPosition(r.Position);
    }

    // ── frames ──────────────────────────────────────────────────────────────

    /// <summary>Two positions can be walked between: outdoors anywhere, indoors within one landblock.</summary>
    private static bool SameFrame(WorldPosition a, WorldPosition b)
        => a.Cell >> 16 == b.Cell >> 16 || ((a.Cell & 0xFFFF) < 0x100 && (b.Cell & 0xFFFF) < 0x100);

    /// <summary>(dx, dy) from a to b in metres (landblocks are 192 m apart).</summary>
    internal static (float, float) Delta(WorldPosition a, WorldPosition b)
    {
        float dx = b.X - a.X + ((int)(b.Cell >> 24) - (int)(a.Cell >> 24)) * 192f;
        float dy = b.Y - a.Y + ((int)((b.Cell >> 16) & 0xFF) - (int)((a.Cell >> 16) & 0xFF)) * 192f;
        return (dx, dy);
    }

    private static float Distance2D(WorldPosition a, WorldPosition b)
    {
        (float dx, float dy) = Delta(a, b);
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Rotation about Z for a heading in radians (0 = north/+Y, clockwise). AC's forward is +Y.</summary>
    internal static WorldPosition WithHeading(WorldPosition p, float heading)
    {
        float yaw = -heading;
        return p with { Qw = MathF.Cos(yaw / 2), Qx = 0, Qy = 0, Qz = MathF.Sin(yaw / 2) };
    }
}
