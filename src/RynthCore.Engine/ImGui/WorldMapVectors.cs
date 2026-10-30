// ============================================================================
//  RynthCore.Engine - ImGui/WorldMapVectors.cs
//  The World Map's vector layers: NavData\worldmap.vec (RynthNav.Baker --map-vectors), the outlines of
//  Dereth's buildings and its roads, drawn over the tiles when you zoom in. About 7,000 buildings and
//  9,000 road segments, so the whole lot is held in memory (a few hundred KB) and culled per frame.
//
//  File layout (little-endian): "RVEC", u32 version (1), u32 building count, u32 road segment count;
//  per building u8 corner count and that many (f32 x, f32 y) in map cells; per road segment u16 x0,
//  y0, x1, y1 in cells. A cell is 24 yards; y is north.
//
//  Read once on a pool thread (Open); after that the arrays are immutable and any thread may read.
// ============================================================================

using System;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;

namespace RynthCore.Engine.ImGuiBackend;

internal static class WorldMapVectors
{
    internal sealed class Data
    {
        /// <summary>Each building's outline corners (cells), with the outline's bounds for culling.</summary>
        public Vector2[][] Buildings = Array.Empty<Vector2[]>();
        public Vector4[] BuildingBounds = Array.Empty<Vector4>();     // minX, minY, maxX, maxY
        /// <summary>Road segments: x0, y0, x1, y1 in cells.</summary>
        public ushort[] Roads = Array.Empty<ushort>();
        public int RoadCount => Roads.Length / 4;
    }

    private static volatile Data? _data;
    private static volatile int _state;               // 0 not tried, 1 opening, 2 open, 3 failed
    private static string _path = "";

    public static Data? Current => _state == 2 ? _data : null;
    public static bool Failed => _state == 3;

    /// <summary>Starts reading the file (once per path). Any thread.</summary>
    public static void Open(string path)
    {
        if (_state == 1 || (_state != 0 && path == _path)) return;
        _path = path;
        _state = 1;
        Task.Run(() =>
        {
            try { _data = Read(path); _state = 2; }
            catch { _state = 3; }              // no file or a bad one: the layer is simply absent
        });
    }

    public static void Shutdown() { _state = 0; _data = null; }

    internal static Data Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new BinaryReader(fs);
        if (r.ReadUInt32() != 0x4345_5652u) throw new InvalidDataException("not a vector file");     // "RVEC"
        if (r.ReadUInt32() != 1u) throw new InvalidDataException("a newer vector file version");
        int buildings = checked((int)r.ReadUInt32()), roads = checked((int)r.ReadUInt32());
        if (buildings < 0 || buildings > 500000 || roads < 0 || roads > 5000000) throw new InvalidDataException("bad counts");
        var d = new Data { Buildings = new Vector2[buildings][], BuildingBounds = new Vector4[buildings], Roads = new ushort[roads * 4] };
        for (int i = 0; i < buildings; i++)
        {
            int n = r.ReadByte();
            var pts = new Vector2[n];
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int k = 0; k < n; k++)
            {
                float x = r.ReadSingle(), y = r.ReadSingle();
                pts[k] = new Vector2(x, y);
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
            }
            d.Buildings[i] = pts;
            d.BuildingBounds[i] = new Vector4(minX, minY, maxX, maxY);
        }
        for (int i = 0; i < roads * 4; i++) d.Roads[i] = r.ReadUInt16();
        return d;
    }
}
