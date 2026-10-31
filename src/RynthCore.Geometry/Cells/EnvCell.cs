// ============================================================================
//  RynthCore.Geometry - Cells/EnvCell.cs
//  The indoor cell model: an EnvCell (dungeon / building interior cell) with
//  its frame, cell portals (doorway polygons and the cell beyond), static
//  objects, and its physics polygons split into floors and walls, all in
//  landblock-local coordinates. Filled by CellDat.LoadLandblock.
//
//  Moved from RynthSuite Tools/RynthNav.TownNet/CellDat.cs (3a8075a).
//  Written for RynthCore (MIT).
// ============================================================================

using System.Numerics;

namespace RynthCore.Geometry.Cells;

/// <summary>A doorway of an EnvCell: the polygon you walk through and the cell on its other side.</summary>
public sealed class CellPortal
{
    /// <summary>Raw portal flags (0x2 = PortalSide).</summary>
    public ushort Flags;
    /// <summary>Key into the CellStruct's render polygons.</summary>
    public ushort PolygonId;
    /// <summary>Low 16 bits of the cell on the other side; <see cref="Outdoors"/> (0xFFFF) = leads outdoors.</summary>
    public ushort OtherCell;
    public ushort OtherPortal;
    /// <summary>The doorway polygon, landblock-local (empty when the CellStruct did not have it).</summary>
    public Vector3[] Polygon = Array.Empty<Vector3>();

    /// <summary><see cref="OtherCell"/> value of a portal that leads outdoors.</summary>
    public const ushort Outdoors = 0xFFFF;

    public bool LeadsOutdoors => OtherCell == Outdoors;

    /// <summary>
    /// ACE's CellPortal.PortalSide (flag 0x2 clear): the cell behind the portal is on the front of
    /// <see cref="Polygon"/> (the side its vertex-order normal, <see cref="CellDat.Normal"/>, points to) and
    /// this cell on its back; when false, the other way round. Checked against the cells' floor plans for
    /// every doorway of dungeon 0x6146 (GeometryTests "cells").
    /// </summary>
    public bool PortalSide => (Flags & 0x2) == 0;
}

/// <summary>A static object placed in a cell: a GfxObj (0x01..) or a Setup (0x02..).</summary>
public sealed class Stab
{
    public uint Id;
    /// <summary>Landblock-local (like the cell's own frame, not relative to the cell).</summary>
    public Vector3 Origin;
    public Quaternion Rotation;
}

/// <summary>
/// One indoor cell (0xLLLL0100..0xLLLLFFFD). Every position and polygon is landblock-local
/// (the same frame as ACE positions: x, y in 0..192 for the landblock, z absolute).
/// </summary>
public sealed class EnvCell
{
    /// <summary>Full cell id 0xLLLLCCCC.</summary>
    public uint Id;
    public uint Flags;
    public uint EnvironmentId;
    public ushort CellStructure;
    /// <summary>The cell frame, landblock-local; the CellStruct's vertices are rotated by
    /// <see cref="Rotation"/> then offset by <see cref="Origin"/>.</summary>
    public Vector3 Origin;
    public Quaternion Rotation;
    public List<CellPortal> Portals = new();
    public List<ushort> VisibleCells = new();
    public List<Stab> Stabs = new();
    /// <summary>Physics polygons facing up (normal.Z &gt;= <see cref="CellDat.FloorZ"/>), landblock-local.</summary>
    public List<Vector3[]> Floors = new();
    /// <summary>Every other physics polygon (walls, ceilings, steep slopes), landblock-local.</summary>
    public List<Vector3[]> Walls = new();
    /// <summary>The Environment's CellStruct for this cell was found and has physics polygons.</summary>
    public bool GeometryOk;

    /// <summary>XY bounds of <see cref="Floors"/> (empty bounds when there are no floors).</summary>
    public float MinX = float.MaxValue, MinY = float.MaxValue, MaxX = float.MinValue, MaxY = float.MinValue;

    /// <summary>The low word: the cell number inside its landblock.</summary>
    public ushort Short => (ushort)(Id & 0xFFFF);
    /// <summary>The landblock key 0xXXYY.</summary>
    public uint Landblock => Id >> 16;

    /// <summary>The full id of the cell behind <paramref name="p"/> (meaningless when it leads outdoors).</summary>
    public uint Neighbour(CellPortal p) => (Id & 0xFFFF0000u) | p.OtherCell;

    /// <summary>Is a cell low word an indoor (EnvCell) number?</summary>
    public static bool IsIndoor(uint cellId)
    {
        uint low = cellId & 0xFFFF;
        return low >= 0x0100 && low <= 0xFFFD;
    }

    internal bool BoundsContain(float x, float y, float margin) =>
        x >= MinX - margin && x <= MaxX + margin && y >= MinY - margin && y <= MaxY + margin;

    /// <summary>
    /// Is (x, y) inside the cell's plan seen from above: over one of its floors, or under one of its other
    /// physics polygons that has area from above (ceilings, steep slopes), within <paramref name="eps"/>?
    /// </summary>
    public bool CoversXY(float x, float y, float eps = 0.05f)
    {
        foreach (var f in Floors)
            if (Geo.InsideXY(f, x, y, eps)) return true;
        foreach (var w in Walls)
            if (Geo.AreaXY(w) > 0.01f && Geo.InsideXY(w, x, y, eps)) return true;
        return false;
    }

    internal void ComputeBounds()
    {
        MinX = MinY = float.MaxValue; MaxX = MaxY = float.MinValue;
        foreach (var f in Floors)
            foreach (var v in f)
            {
                MinX = MathF.Min(MinX, v.X); MaxX = MathF.Max(MaxX, v.X);
                MinY = MathF.Min(MinY, v.Y); MaxY = MathF.Max(MaxY, v.Y);
            }
    }

    public override string ToString() => $"EnvCell {Id:X8} ({Floors.Count} floors, {Walls.Count} walls, {Portals.Count} portals)";
}
