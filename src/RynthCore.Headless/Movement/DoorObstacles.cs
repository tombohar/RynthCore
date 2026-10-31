// ============================================================================
//  RynthCore.Headless - Movement/DoorObstacles.cs
//  Doors are server objects, not in the dats. A door blocks exactly while its physics
//  state lacks Ethereal (0x4): ACE makes it ethereal (SetState) as it opens and solid
//  again when the close animation ends. This keeps the walker's dynamic obstacles in step
//  with the doors the world model knows (CreateObject / SetState / delete).
// ============================================================================

using System.Numerics;
using RynthCore.Geometry.Collision;
using RynthCore.Geometry.Dat;
using RynthCore.Geometry.Models;
using RynthCore.Geometry.Walking;
using RynthCore.Headless.World;

namespace RynthCore.Headless.Movement;

public sealed class DoorObstacles
{
    private const uint DoorFlag = 0x1000;          // ObjectDescriptionFlag.Door
    private const uint Ethereal = 0x4;             // PhysicsState.Ethereal

    private readonly ModelTriangles _models;
    public DynamicObstacles Obstacles { get; } = new();

    public DoorObstacles(ModelReader models, DatDatabase portal) => _models = new ModelTriangles(models, portal);

    /// <summary>Follows one session's world model.</summary>
    public void Track(WorldState world)
    {
        world.ObjectCreated += Update;
        world.ObjectUpdated += Update;
        world.ObjectDeleted += id => Obstacles.Remove(id);
        world.ObjectStateChanged += Update;
    }

    public void Update(WorldObject o)
    {
        if ((o.Desc.DescriptionFlags & DoorFlag) == 0) return;
        if ((o.PhysicsState & Ethereal) != 0 || o.Position is not { } p || o.Desc.SetupId == 0)
        {
            Obstacles.Remove(o.Id);
            return;
        }
        var tris = new List<CollisionTri>();
        _models.Append(o.Desc.SetupId, new Vector3(p.X, p.Y, p.Z), new Quaternion(p.Qx, p.Qy, p.Qz, p.Qw), o.Desc.Scale ?? 1f, tris);
        Obstacles.Set(o.Id, p.Cell >> 16, tris.Select(t => (t.A, t.B, t.C, t.Normal)));
    }
}
