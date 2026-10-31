// ============================================================================
//  RynthCore.Geometry - Scenery/SetupBounds.cs
//  The tail of a Setup (0x02..) record: its collision cylinders and spheres,
//  height and radius, and its sorting sphere (the sphere obj_within_block
//  checks). ModelReader stops reading a Setup whose placement frames carry
//  animation hooks (variable size); this reader finds the tail from the end
//  of the record instead, so those Setups keep their cylinders.
//
//  Setup layout as in ACE (SetupModel.Unpack):
//    Id, Flags, NumParts, Parts[], (Flags&1) Parents[], (Flags&2) Scales[],
//    HoldingLocations, ConnectionPoints (count + 36-byte entries),
//    PlacementFrames (count + key, numParts frames, numHooks, hooks[]),
//    CylSpheres (count + 20 bytes), Spheres (count + 16 bytes),
//    Height, Radius, StepUpHeight, StepDownHeight,
//    SortingSphere (16), SelectionSphere (16),
//    Lights (count + key 4 + LightInfo 44), 5 default ids (20).
//
//  Written for RynthCore (MIT) on 2026-10-28; the tail scan follows RynthSuite
//  Plugins/RynthCore.Plugin.RynthAi/Raycasting/LandblockStructs.cs SetupInfo
//  (4b29610).
// ============================================================================

using System.Numerics;
using RynthCore.Geometry.Dat;

namespace RynthCore.Geometry.Scenery;

/// <summary>A Setup's collision cylinders and spheres, height, radius and sorting sphere, in model space.</summary>
internal sealed class SetupBounds
{
    public readonly List<(Vector3 Origin, float Radius, float Height)> Cylinders = new();
    public readonly List<(Vector3 Center, float Radius)> Spheres = new();
    public float Height, Radius, StepUp, StepDown;
    public Vector3 SortCenter;
    public float SortRadius;
    /// <summary>Found from the end of the record (the placement frames had animation hooks).</summary>
    public bool FromTail;

    private const int LightBytes = 4 + 44;

    /// <summary>The bounds of Setup <paramref name="id"/>, or null when the record is missing or does not parse.</summary>
    public static SetupBounds? Read(DatDatabase portal, uint id)
    {
        if ((id >> 24) != 0x02) return null;
        byte[]? data = portal.GetFileData(id);
        if (data == null || data.Length < 16) return null;
        try { return Forward(data) ?? FromEnd(data); }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException or IOException or ArgumentException)
        {
            return FromEnd(data);
        }
    }

    /// <summary>Reads a Setup record front to back (null when it has animation hooks), or from its end (tests compare the two).</summary>
    internal static SetupBounds? Parse(byte[] data, bool fromEnd) => fromEnd ? FromEnd(data) : Forward(data);

    // Reads front to back; null when the placement frames have hooks (variable size, not skipped).
    private static SetupBounds? Forward(byte[] data)
    {
        var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);
        r.ReadUInt32();
        uint flags = r.ReadUInt32();
        uint numParts = r.ReadUInt32();
        if (numParts > 1000) throw new InvalidDataException("bad part count");
        ms.Seek(numParts * 4L, SeekOrigin.Current);
        if ((flags & 1) != 0) ms.Seek(numParts * 4L, SeekOrigin.Current);
        if ((flags & 2) != 0) ms.Seek(numParts * 12L, SeekOrigin.Current);
        for (int t = 0; t < 2; t++)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 1000) throw new InvalidDataException("bad location table");
            ms.Seek(n * 36L, SeekOrigin.Current);
        }
        int placements = r.ReadInt32();
        if (placements < 0 || placements > 1000) throw new InvalidDataException("bad placement count");
        for (int p = 0; p < placements; p++)
        {
            r.ReadInt32();
            ms.Seek(numParts * 28L, SeekOrigin.Current);
            if (r.ReadUInt32() != 0) return null;
        }
        var b = new SetupBounds();
        uint numCyl = r.ReadUInt32();
        if (numCyl > 256) throw new InvalidDataException("bad cylinder count");
        for (uint i = 0; i < numCyl; i++)
            b.Cylinders.Add((new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()), r.ReadSingle(), r.ReadSingle()));
        uint numSph = r.ReadUInt32();
        if (numSph > 256) throw new InvalidDataException("bad sphere count");
        for (uint i = 0; i < numSph; i++)
            b.Spheres.Add((new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()), r.ReadSingle()));
        b.Height = r.ReadSingle(); b.Radius = r.ReadSingle(); b.StepUp = r.ReadSingle(); b.StepDown = r.ReadSingle();
        b.SortCenter = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        b.SortRadius = r.ReadSingle();
        return b;
    }

    // The record ends: sorting sphere, selection sphere, light count + lights, 5 ids. Find the light count
    // that fits, then walk back over Height..StepDown, the sphere list and the cylinder list.
    private static SetupBounds? FromEnd(byte[] data)
    {
        for (int lights = 0; lights <= 16; lights++)
        {
            int countAt = data.Length - 20 - lights * LightBytes - 4;
            if (countAt < 16 + 32 + 16 + 8) break;
            if (BitConverter.ToUInt32(data, countAt) != (uint)lights) continue;
            int sortAt = countAt - 32, heightAt = sortAt - 16;
            float sortR = BitConverter.ToSingle(data, sortAt + 12);
            if (!float.IsFinite(sortR) || sortR < 0 || sortR > 10000) continue;
            for (int k = 0; k <= 64; k++)
            {
                int sphCountAt = heightAt - 16 * k - 4;
                if (sphCountAt < 16) break;
                if (BitConverter.ToUInt32(data, sphCountAt) != (uint)k) continue;
                for (int m = 0; m <= 64; m++)
                {
                    int cylCountAt = sphCountAt - 20 * m - 4;
                    if (cylCountAt < 16) break;
                    if (BitConverter.ToUInt32(data, cylCountAt) != (uint)m) continue;
                    var b = new SetupBounds { FromTail = true };
                    for (int i = 0; i < m; i++)
                    {
                        int o = cylCountAt + 4 + 20 * i;
                        b.Cylinders.Add((V(data, o), F(data, o + 12), F(data, o + 16)));
                    }
                    for (int i = 0; i < k; i++)
                    {
                        int o = sphCountAt + 4 + 16 * i;
                        b.Spheres.Add((V(data, o), F(data, o + 12)));
                    }
                    b.Height = F(data, heightAt); b.Radius = F(data, heightAt + 4);
                    b.StepUp = F(data, heightAt + 8); b.StepDown = F(data, heightAt + 12);
                    b.SortCenter = V(data, sortAt); b.SortRadius = sortR;
                    if (Plausible(b)) return b;
                }
            }
        }
        return null;
    }

    private static bool Plausible(SetupBounds b)
    {
        static bool Ok(float f) => float.IsFinite(f) && MathF.Abs(f) < 10000;
        if (!Ok(b.Height) || !Ok(b.Radius) || b.Height < 0 || b.Radius < 0) return false;
        foreach (var c in b.Cylinders) if (!Ok(c.Origin.Length()) || !Ok(c.Radius) || !Ok(c.Height) || c.Radius < 0 || c.Height < 0) return false;
        foreach (var s in b.Spheres) if (!Ok(s.Center.Length()) || !Ok(s.Radius) || s.Radius < 0) return false;
        return Ok(b.SortCenter.Length());
    }

    private static float F(byte[] d, int o) => BitConverter.ToSingle(d, o);
    private static Vector3 V(byte[] d, int o) => new(F(d, o), F(d, o + 4), F(d, o + 8));
}
