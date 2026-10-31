// ============================================================================
//  RynthCore.Geometry - Walking/LocalPlanner.cs
//  A short-range path around obstacles (a building corner, a fence, a doorway), for
//  when a straight walk is blocked. Not a navigator: long routes come from RynthNav.
//
//  A* over a grid (Grid metres) anchored at the start, where an edge exists only if
//  the Walker itself can make that step (so walls, slopes, drops, doors, deep water and
//  cell changes are all respected, indoors and out) and ends where it was aimed. Search
//  is bounded (MaxNodes). The raw grid path is then shortened by string-pulling: from
//  each kept point, the farthest later point the walker reaches in a straight line.
//
//  Positions are in the start's landblock frame; a step into a neighbouring landblock
//  is shifted back into that frame for the grid key, and every returned point carries
//  its real cell and landblock-local position.
// ============================================================================

using System.Numerics;
using RynthCore.Geometry.Cells;

namespace RynthCore.Geometry.Walking;

public readonly record struct PathPoint(uint Cell, Vector3 Position);

public sealed class LocalPlanner
{
    public float Grid { get; init; } = 1.0f;
    public int MaxNodes { get; init; } = 6000;

    private readonly Walker _walker;

    public LocalPlanner(Walker walker) => _walker = walker;

    private sealed class Node
    {
        public required (int X, int Y) Key;
        public required PathPoint At;
        public Vector2 Frame;           // position in the start landblock's frame
        public float G;
        public Node? Parent;
    }

    private static readonly (int X, int Y)[] Dirs = { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1) };

    /// <summary>
    /// A walkable path from <paramref name="start"/> to within <paramref name="stop"/> of
    /// <paramref name="goal"/> (goal's x/y in the start landblock's frame), or null.
    /// The first point is the start; the last is where the path ends. With
    /// <paramref name="bestEffort"/>, an unreachable goal gives the path to the reachable point
    /// nearest it instead of null.
    /// </summary>
    public List<PathPoint>? Find(PathPoint start, Vector2 goal, float stop, bool bestEffort = false)
    {
        uint lb0 = start.Cell >> 16;
        var origin = new Vector2(start.Position.X, start.Position.Y);
        var startNode = new Node { Key = (0, 0), At = start, Frame = origin, G = 0 };
        var best = new Dictionary<(int, int), Node> { [(0, 0)] = startNode };
        var open = new PriorityQueue<Node, float>();
        open.Enqueue(startNode, Vector2.Distance(origin, goal));
        float reach = Math.Max(stop, Grid * 0.75f);
        int expanded = 0;
        Node? found = null, closest = startNode;
        float closestD = Vector2.Distance(origin, goal);

        while (open.TryDequeue(out Node? n, out _))
        {
            if (best[n.Key] != n) continue;          // stale entry
            float dn = Vector2.Distance(n.Frame, goal);
            if (dn <= reach) { found = n; break; }
            if (dn < closestD) { closestD = dn; closest = n; }
            if (++expanded > MaxNodes) break;

            foreach (var (dx, dy) in Dirs)
            {
                var key = (n.Key.X + dx, n.Key.Y + dy);
                var target = origin + new Vector2(key.Item1, key.Item2) * Grid;
                Vector2 step = target - n.Frame;
                StepResult r = _walker.Step(n.At.Cell, n.At.Position, step.X, step.Y);
                if (r.Blocked) continue;
                Vector2 landed = InFrame(lb0, r.Cell, r.Position);
                if (Vector2.Distance(landed, target) > 0.1f) continue;   // slid somewhere else
                float g = n.G + step.Length() + MathF.Abs(r.Position.Z - n.At.Position.Z) * 0.5f;
                if (best.TryGetValue(key, out Node? old) && old.G <= g) continue;
                var m = new Node { Key = key, At = new PathPoint(r.Cell, r.Position), Frame = landed, G = g, Parent = n };
                best[key] = m;
                open.Enqueue(m, g + Vector2.Distance(landed, goal));
            }
        }
        if (found == null)
        {
            // Best effort: the reachable point nearest the goal, if it is clearly nearer than the start.
            if (!bestEffort || closest == startNode || closestD > Vector2.Distance(origin, goal) - 0.5f) return null;
            found = closest;
        }

        var raw = new List<PathPoint>();
        for (Node? p = found; p != null; p = p.Parent) raw.Add(p.At);
        raw.Reverse();

        // The last bit, from the final grid point toward the goal itself, if it can be walked.
        PathPoint last = raw[^1];
        Vector2 lastFrame = InFrame(lb0, last.Cell, last.Position);
        Vector2 toGoal = goal - lastFrame;
        float left = toGoal.Length() - stop;
        if (left > 0.05f)
        {
            Vector2 d = toGoal / toGoal.Length() * left;
            StepResult r = _walker.Step(last.Cell, last.Position, d.X, d.Y);
            if (!r.Blocked) raw.Add(new PathPoint(r.Cell, r.Position));
        }
        return Smooth(raw, lb0);
    }

    /// <summary>Keeps only the turning points: each kept point walks straight to the next.</summary>
    private List<PathPoint> Smooth(List<PathPoint> raw, uint lb0)
    {
        var path = new List<PathPoint> { raw[0] };
        int i = 0;
        while (i < raw.Count - 1)
        {
            int j = raw.Count - 1;
            for (; j > i + 1; j--)
                if (StraightReaches(raw[i], raw[j], lb0)) break;
            path.Add(raw[j]);
            i = j;
        }
        return path;
    }

    private bool StraightReaches(PathPoint a, PathPoint b, uint lb0)
    {
        Vector2 fa = InFrame(lb0, a.Cell, a.Position), fb = InFrame(lb0, b.Cell, b.Position);
        Vector2 d = fb - fa;
        StepResult r = _walker.Step(a.Cell, a.Position, d.X, d.Y);
        if (r.Blocked || r.Slid) return false;
        return Vector2.Distance(InFrame(lb0, r.Cell, r.Position), fb) < 0.1f && MathF.Abs(r.Position.Z - b.Position.Z) < 0.3f
               && (EnvCell.IsIndoor(b.Cell) == EnvCell.IsIndoor(r.Cell));
    }

    /// <summary>A landblock-local position expressed in landblock lb0's frame (0xXXYY).</summary>
    public static Vector2 InFrame(uint lb0, uint cell, Vector3 p)
    {
        uint lb = cell >> 16;
        float ox = ((int)(lb >> 8) - (int)(lb0 >> 8)) * 192f, oy = ((int)(lb & 0xFF) - (int)(lb0 & 0xFF)) * 192f;
        return new Vector2(p.X + ox, p.Y + oy);
    }
}
