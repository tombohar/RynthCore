// ============================================================================
//  RynthCore.Geometry - Scenery/SceneryTables.cs
//  The tables scenery placement reads from the portal dat: the region's
//  scene types (each a list of Scene files 0x12..) and terrain types (each a
//  list of scene-type indices), from RegionDesc 0x13000000, and the object
//  descriptions (ObjectDesc) of each Scene file.
//
//  RegionDesc layout as in ACE (RegionDesc.Unpack): Id, RegionNumber, Version,
//  RegionName (PString), LandDefs (8 values + 256-float height table),
//  GameTime, PartsMask, (0x10) SkyDesc, (0x01) SoundDesc, (0x02) SceneDesc,
//  TerrainDesc, ... A PString is a ushort length + bytes, aligned to 4.
//  If the walk through it fails a sanity check, the scene and terrain tables
//  are found by scanning (as RynthSuite's ScatterSystem does).
//
//  Logic from RynthSuite Plugins/RynthCore.Plugin.RynthAi/Raycasting/
//  ScatterSystem.cs (4b29610: ParseRegionDesc, ParseSceneDesc, ParseTerrainDesc,
//  ScanForTables) and Tools/RynthNav.Baker/Scenery.cs (d4ae46a: Scene).
//  Written for RynthCore (MIT).
// ============================================================================

using System.Collections.Concurrent;
using System.Numerics;
using RynthCore.Geometry.Dat;

namespace RynthCore.Geometry.Scenery;

/// <summary>One object of a Scene file (ACE ObjectDesc): what to place, where in the cell, how often, how big.</summary>
public sealed record SceneObjectDesc(
    uint ObjId, Vector3 BaseOrigin, Quaternion BaseRotation, float Freq, float DisplaceX, float DisplaceY,
    float MinScale, float MaxScale, float MaxRotation, float MinSlope, float MaxSlope, uint Align, uint Orient, uint WeenieObj);

/// <summary>The region's terrain -> scene-type -> Scene tables, and the Scene files' objects (cached, thread-safe).</summary>
public sealed class SceneryTables
{
    public const uint RegionDescId = 0x13000000;

    private readonly DatDatabase _portal;
    private readonly ConcurrentDictionary<uint, IReadOnlyList<SceneObjectDesc>> _scenes = new();

    /// <summary>Per terrain type (0..31): indices into <see cref="SceneTypes"/>.</summary>
    public IReadOnlyList<IReadOnlyList<uint>> TerrainTypes { get; private set; } = Array.Empty<IReadOnlyList<uint>>();
    /// <summary>Per scene type: its Scene file ids (0x12..).</summary>
    public IReadOnlyList<IReadOnlyList<uint>> SceneTypes { get; private set; } = Array.Empty<IReadOnlyList<uint>>();
    /// <summary>Both tables were read.</summary>
    public bool Ready => TerrainTypes.Count > 0 && SceneTypes.Count > 0;
    /// <summary>The tables came from the scan fallback, not the structured walk (diagnostics).</summary>
    public bool Scanned { get; private set; }

    public SceneryTables(DatDatabase portal)
    {
        _portal = portal;
        byte[]? data = portal.GetFileData(RegionDescId);
        if (data == null) return;
        if (!Walk(data)) Scanned = Scan(data);
    }

    /// <summary>The objects of Scene file <paramref name="sceneId"/> (empty when missing).</summary>
    public IReadOnlyList<SceneObjectDesc> Scene(uint sceneId) => _scenes.GetOrAdd(sceneId, ReadScene);

    private IReadOnlyList<SceneObjectDesc> ReadScene(uint sceneId)
    {
        var list = new List<SceneObjectDesc>();
        byte[]? data = _portal.GetFileData(sceneId);
        if (data == null || data.Length < 8) return list;
        var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);
        r.ReadUInt32();
        uint n = r.ReadUInt32();
        for (uint i = 0; i < n && ms.Position + 76 <= ms.Length; i++)
        {
            uint obj = r.ReadUInt32();
            var o = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            float w = r.ReadSingle(), x = r.ReadSingle(), y = r.ReadSingle(), z = r.ReadSingle();
            list.Add(new SceneObjectDesc(obj, o, new Quaternion(x, y, z, w),
                r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(),
                r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32()));
        }
        return list;
    }

    // ---- structured walk -----------------------------------------------------

    private bool Walk(byte[] data)
    {
        try
        {
            var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32();     // id, region number, version
            PString(r, ms);                                     // region name
            ms.Seek(8 * 4 + 256 * 4, SeekOrigin.Current);       // LandDefs
            // GameTime
            r.ReadDouble(); r.ReadUInt32(); r.ReadSingle(); r.ReadUInt32();
            PString(r, ms);
            uint n = Count(r, 100);
            for (uint i = 0; i < n; i++) { r.ReadSingle(); r.ReadUInt32(); PString(r, ms); }
            n = Count(r, 100);
            for (uint i = 0; i < n; i++) PString(r, ms);
            n = Count(r, 100);
            for (uint i = 0; i < n; i++) { r.ReadUInt32(); PString(r, ms); }
            uint parts = r.ReadUInt32();
            if ((parts & 0x10) != 0)                            // SkyDesc
            {
                r.ReadDouble(); r.ReadDouble();
                uint groups = Count(r, 1000);
                for (uint g = 0; g < groups; g++)
                {
                    r.ReadSingle();
                    PString(r, ms);
                    ms.Seek(Count(r, 10000) * 36L, SeekOrigin.Current);        // SkyObjects
                    uint times = Count(r, 10000);
                    for (uint t = 0; t < times; t++)
                    {
                        ms.Seek(44, SeekOrigin.Current);
                        ms.Seek(Count(r, 10000) * 24L, SeekOrigin.Current);    // SkyObjectReplace
                    }
                }
            }
            if ((parts & 0x01) != 0)                            // SoundDesc
            {
                uint stbs = Count(r, 10000);
                for (uint s = 0; s < stbs; s++)
                {
                    r.ReadUInt32();
                    ms.Seek(Count(r, 10000) * 20L, SeekOrigin.Current);
                }
            }
            if ((parts & 0x02) == 0) return false;
            var scenes = ReadSceneTypes(r);
            if (scenes == null) return false;
            var terrain = ReadTerrainTypes(r, ms);
            if (terrain == null) return false;
            SceneTypes = scenes;
            TerrainTypes = terrain;
            return true;
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException or IOException or ArgumentException)
        {
            return false;
        }
    }

    private static List<IReadOnlyList<uint>>? ReadSceneTypes(BinaryReader r)
    {
        uint n = Count(r, 1000);
        if (n == 0) return null;
        var list = new List<IReadOnlyList<uint>>();
        for (uint i = 0; i < n; i++)
        {
            r.ReadUInt32();                                     // STB index
            uint k = Count(r, 1000);
            var ids = new uint[k];
            for (uint s = 0; s < k; s++)
            {
                ids[s] = r.ReadUInt32();
                if ((ids[s] >> 24) != 0x12) return null;
            }
            list.Add(ids);
        }
        return list;
    }

    private static List<IReadOnlyList<uint>>? ReadTerrainTypes(BinaryReader r, MemoryStream ms)
    {
        uint n = Count(r, 100);
        if (n < 5) return null;
        var list = new List<IReadOnlyList<uint>>();
        for (uint i = 0; i < n; i++)
        {
            ushort len = r.ReadUInt16();
            if (len > 200) return null;
            byte[] name = r.ReadBytes(len);
            if (name.Any(b => b < 32 || b > 126)) return null;
            Align(ms);
            r.ReadUInt32();                                     // terrain colour
            uint k = Count(r, 200);
            var idx = new uint[k];
            for (uint s = 0; s < k; s++) idx[s] = r.ReadUInt32();
            list.Add(idx);
        }
        return list;
    }

    // ---- scan fallback ----------------------------------------------------------

    private bool Scan(byte[] data)
    {
        for (int pos = 100; pos + 8 < data.Length; pos += 4)
        {
            uint count = BitConverter.ToUInt32(data, pos);
            if (count < 20 || count > 200) continue;
            try
            {
                var ms = new MemoryStream(data) { Position = pos };
                using var r = new BinaryReader(ms);
                var scenes = ReadSceneTypes(r);
                if (scenes == null || scenes.Sum(s => s.Count) < 10) continue;
                var terrain = ReadTerrainTypes(r, ms);
                if (terrain == null) continue;
                SceneTypes = scenes;
                TerrainTypes = terrain;
                return true;
            }
            catch (Exception e) when (e is EndOfStreamException or InvalidDataException or IOException or ArgumentException) { }
        }
        return false;
    }

    // ---- helpers ------------------------------------------------------------------

    private static uint Count(BinaryReader r, uint max)
    {
        uint n = r.ReadUInt32();
        if (n > max) throw new InvalidDataException("bad list count");
        return n;
    }

    private static void PString(BinaryReader r, MemoryStream ms)
    {
        ushort len = r.ReadUInt16();
        if (ms.Position + len > ms.Length) throw new EndOfStreamException();
        ms.Seek(len, SeekOrigin.Current);
        Align(ms);
    }

    private static void Align(MemoryStream ms)
    {
        long a = (ms.Position + 3) & ~3L;
        if (a > ms.Length) throw new EndOfStreamException();
        ms.Position = a;
    }
}
