// ============================================================================
//  RynthCore.Geometry - Models/LandblockInfo.cs
//  The outdoor objects of a landblock, from its LandblockInfo record in the
//  cell dat (0xXXYYFFFE): static objects (Stabs) and buildings, each a model
//  id with a landblock-local frame, plus each building's portals (the indoor
//  cells its doorways open into).
//
//  Format as in ACE (LandblockInfo.Unpack, BuildInfo.Unpack, CBldPortal.Unpack):
//    uint Id, uint NumCells, uint count + Stab[] (uint id, frame),
//    ushort numBuildings, ushort packMask,
//    BuildInfo[]: uint ModelId, frame, uint NumLeaves, uint count + CBldPortal[]
//      (ushort flags, otherCell, otherPortal, numStabs, ushort[numStabs], align 4),
//    (packMask & 1) restriction table (not read).
//  A frame is origin x, y, z then rotation w, x, y, z.
//
//  Written for RynthCore (MIT) on 2026-10-28.
// ============================================================================

using System.Numerics;
using RynthCore.Geometry.Cells;
using RynthCore.Geometry.Dat;

namespace RynthCore.Geometry.Models;

/// <summary>A building placed on a landblock, and the cells its doorways open into.</summary>
public sealed class BuildingInfo
{
    /// <summary>Its model: a GfxObj (0x01..) or a Setup (0x02..).</summary>
    public uint ModelId;
    /// <summary>Landblock-local.</summary>
    public Vector3 Origin;
    public Quaternion Rotation;
    public uint NumLeaves;
    /// <summary>Low words of the indoor cells behind the building's doorways.</summary>
    public List<ushort> PortalCells = new();
}

/// <summary>A landblock's LandblockInfo (0xXXYYFFFE): its outdoor static objects and buildings.</summary>
public sealed class LandblockInfo
{
    public uint Id;
    public uint NumCells;
    /// <summary>Static objects, landblock-local frames.</summary>
    public List<Stab> Objects = new();
    public List<BuildingInfo> Buildings = new();
    /// <summary>The whole record (objects and buildings) was read.</summary>
    public bool Complete;

    /// <summary>
    /// Reads landblock <paramref name="landblock"/>'s LandblockInfo (0xXXYY, or any cell id 0xXXYYnnnn of it);
    /// null when the record is missing. A damaged record returns what parsed, with <see cref="Complete"/> false.
    /// </summary>
    public static LandblockInfo? Load(DatDatabase cellDat, uint landblock)
    {
        if (landblock > 0xFFFF) landblock >>= 16;
        byte[]? data = cellDat.GetFileData((landblock << 16) | 0xFFFE);
        return data == null ? null : Parse(data);
    }

    public static LandblockInfo Parse(byte[] data)
    {
        var info = new LandblockInfo();
        var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);
        try
        {
            info.Id = r.ReadUInt32();
            info.NumCells = r.ReadUInt32();
            uint numObjects = r.ReadUInt32();
            for (uint i = 0; i < numObjects && i < 10000; i++)
            {
                var s = new Stab { Id = r.ReadUInt32() };
                (s.Origin, s.Rotation) = ReadFrame(r);
                info.Objects.Add(s);
            }
            ushort numBuildings = r.ReadUInt16();
            r.ReadUInt16();                                            // pack mask
            for (int b = 0; b < numBuildings; b++)
            {
                var bi = new BuildingInfo { ModelId = r.ReadUInt32() };
                (bi.Origin, bi.Rotation) = ReadFrame(r);
                bi.NumLeaves = r.ReadUInt32();
                uint numPortals = r.ReadUInt32();
                if (numPortals > 1000) throw new InvalidDataException("bad building portal count");
                for (uint p = 0; p < numPortals; p++)
                {
                    r.ReadUInt16();                                    // flags
                    bi.PortalCells.Add(r.ReadUInt16());               // other cell
                    r.ReadUInt16();                                    // other portal
                    ushort numStabs = r.ReadUInt16();
                    ms.Seek(numStabs * 2L, SeekOrigin.Current);
                    long a = (ms.Position + 3) & ~3L;
                    if (a > ms.Length) throw new EndOfStreamException();
                    ms.Position = a;
                }
                info.Buildings.Add(bi);
            }
            info.Complete = true;
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException or IOException) { }
        return info;
    }

    private static (Vector3, Quaternion) ReadFrame(BinaryReader r)
    {
        var o = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        float w = r.ReadSingle(), x = r.ReadSingle(), y = r.ReadSingle(), z = r.ReadSingle();
        return (o, new Quaternion(x, y, z, w));
    }
}
