// ============================================================================
//  RynthCore.Engine - ImGui/WorldMapTiles.cs
//  The World Map's picture: NavData\worldmap.rmap (RynthNav.Baker --map --map-tiles) is a pyramid of
//  256 px tiles, each a zlib stream of RGB pixels. The face asks for the tiles it is about to draw;
//  this reads and inflates them on a worker thread and makes D3D9 textures from the results on AC's
//  render thread (WorldMapFace.Draw is the only caller, plus the frame hooks), like ScriptIcons.
//
//  File layout (little-endian): "RMAP", u32 version (1), u32 tile size, u32 levels, u32 pixels per
//  cell at level 0, u32 cells wide, u32 cells high; per level u32 width, height, columns, rows,
//  first tile; u32 tile count; per tile u64 offset + u32 length; the tiles. Level 0 is the full
//  resolution, each level above half of it, the last one a single tile. Row 0 is the north edge.
//
//  Bounded: at most MaxTextures tiles alive (256 KB each, twice with the managed pool's system
//  copy), MaxPending reads in flight, MaxCreatesPerFrame textures made a frame. A tile drawn in the
//  last frame is never evicted, and the top level (the whole map in one tile) never is, so the face
//  always has something to show under the sharper tiles still loading.
//
//  Threads: AC's render thread owns the texture table and every D3D call (TryGet, BeginFrame,
//  Shutdown). One pool worker at a time reads and inflates; the two meet in two queues under Sync.
//  The header is read on the pool too (Open), so no file read happens on AC's thread.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;

namespace RynthCore.Engine.ImGuiBackend;

internal static class WorldMapTiles
{
    public const int TileSize = 256;
    private const int MaxTexturesRgba = 96, MaxTexturesDxt = 192, MaxCreatesPerFrame = 4, MaxPending = 24;
    // A DXT1 tile is 32 KB (RGBA 256 KB), so twice as many are kept.
    private static int MaxTextures => _header?.Dxt == true ? MaxTexturesDxt : MaxTexturesRgba;

    internal readonly record struct Level(int Width, int Height, int Cols, int Rows, int First);

    // ── The file's header: written once by the Open worker, then only read ─────────────────
    private static volatile Header? _header;
    private static volatile int _openState;          // 0 not tried, 1 opening, 2 open, 3 failed
    private static volatile string _status = "";
    private static string _path = "";

    private sealed class Header
    {
        public Level[] Levels = Array.Empty<Level>();
        public ulong[] Offsets = Array.Empty<ulong>();
        public uint[] Lengths = Array.Empty<uint>();
        public int PixelsPerCell, CellsWide, CellsHigh;
        public bool Dxt;               // version 2: each tile is DXT1 blocks (32 KB inflated), not RGB
    }

    public static bool IsOpen => _openState == 2;
    /// <summary>The file in use (or being opened); "" before the first Open.</summary>
    public static string CurrentPath => _path;
    /// <summary>True when the open file holds DXT1 tiles (a World Map HD / Ultra / textured Standard file), false for the flat RGB one.</summary>
    public static bool IsDxt => _header?.Dxt == true;
    private static volatile string? _switchTo;

    /// <summary>
    /// Changes to another map file at the start of the next frame (BeginFrame): the textures of the old one are
    /// released then, before anything draws with them. Any thread.
    /// </summary>
    public static void Switch(string path) { if (!string.Equals(path, _path, StringComparison.OrdinalIgnoreCase)) _switchTo = path; }
    public static string Status => _status;
    public static IReadOnlyList<Level> Levels => _header?.Levels ?? Array.Empty<Level>();
    /// <summary>Pixels per cell at level 0.</summary>
    public static int PixelsPerCell => _header?.PixelsPerCell ?? 2;
    public static int CellsWide => _header?.CellsWide ?? 2048;
    public static int CellsHigh => _header?.CellsHigh ?? 2048;

    /// <summary>Starts reading the header (once per path; a changed path starts over). Any thread.</summary>
    public static void Open(string path)
    {
        if (_openState == 1 || (_openState != 0 && path == _path)) return;
        _path = path;
        _openState = 1;
        _status = "Loading the map...";
        Task.Run(() =>
        {
            try { _header = ReadHeader(path); _openState = 2; _status = ""; }
            catch (FileNotFoundException) { _openState = 3; _status = "No world map file yet (NavData\\worldmap.rmap)."; }
            catch (Exception ex) { _openState = 3; _status = "The world map file didn't load: " + ex.Message; }
        });
    }

    private static Header ReadHeader(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new BinaryReader(fs);
        if (r.ReadUInt32() != 0x5041_4D52u) throw new InvalidDataException("not a map file");   // "RMAP"
        uint version = r.ReadUInt32();
        if (version != 1u && version != 2u) throw new InvalidDataException("a newer map file version");
        if (r.ReadUInt32() != TileSize) throw new InvalidDataException("unexpected tile size");
        int levels = checked((int)r.ReadUInt32());
        if (levels < 1 || levels > 14) throw new InvalidDataException("bad level count");
        var h = new Header { Dxt = version == 2u, PixelsPerCell = (int)r.ReadUInt32(), CellsWide = (int)r.ReadUInt32(), CellsHigh = (int)r.ReadUInt32() };
        h.Levels = new Level[levels];
        for (int i = 0; i < levels; i++)
            h.Levels[i] = new Level((int)r.ReadUInt32(), (int)r.ReadUInt32(), (int)r.ReadUInt32(), (int)r.ReadUInt32(), (int)r.ReadUInt32());
        int count = checked((int)r.ReadUInt32());
        if (count < 1 || count > 4000000) throw new InvalidDataException("bad tile count");
        h.Offsets = new ulong[count];
        h.Lengths = new uint[count];
        for (int i = 0; i < count; i++) { h.Offsets[i] = r.ReadUInt64(); h.Lengths[i] = r.ReadUInt32(); }
        foreach (Level l in h.Levels)
            if (l.First < 0 || l.First + l.Cols * l.Rows > count) throw new InvalidDataException("bad level table");
        return h;
    }

    // ── AC's render thread ─────────────────────────────────────────────────────────────────
    private sealed class Tile
    {
        public IntPtr Ptr;
        public int LastUsed;
        public bool Pending;
        public int Failures, RetryAt;     // a tile that didn't load is asked for again at frame RetryAt, three times at most
    }

    private static readonly Dictionary<int, Tile> Tiles = new(MaxTexturesDxt + 8);
    private static readonly List<int> EvictScratch = new(MaxTexturesDxt + 8);
    private static int _frame;
    private static int _pending;

    // ── Shared with the worker, under Sync ─────────────────────────────────────────────────
    private static readonly object Sync = new();
    private static readonly Queue<int> Requests = new();
    private static readonly Queue<(int Index, int Generation, byte[]? Rgba)> Results = new();
    private static bool _draining;
    private static int _generation;

    public static int LiveTextures { get; private set; }

    /// <summary>
    /// The texture of tile (col, row) at <paramref name="level"/> if it is ready; otherwise false and, if
    /// it isn't already, it is queued. AC's render thread, during a draw.
    /// </summary>
    public static bool TryGet(int level, int col, int row, out IntPtr texture)
    {
        texture = IntPtr.Zero;
        Header? h = _header;
        if (h == null || (uint)level >= (uint)h.Levels.Length) return false;
        Level l = h.Levels[level];
        if ((uint)col >= (uint)l.Cols || (uint)row >= (uint)l.Rows) return false;
        int index = l.First + row * l.Cols + col;

        if (Tiles.TryGetValue(index, out Tile? t))
        {
            t.LastUsed = _frame;
            if (t.Ptr != IntPtr.Zero) { texture = t.Ptr; return true; }
            if (!t.Pending && t.Failures > 0 && t.Failures < 3 && _frame >= t.RetryAt && _pending < MaxPending)
            {
                t.Pending = true;
                _pending++;
                Enqueue(h, index);
            }
            return false;
        }
        if (_pending >= MaxPending) return false;
        Tiles[index] = new Tile { LastUsed = _frame, Pending = true };
        _pending++;
        Enqueue(h, index);
        return false;
    }

    /// <summary>A tile that is already loaded (a coarse one under a sharper level), without asking for it. AC's render thread.</summary>
    public static bool Peek(int level, int col, int row, out IntPtr texture)
    {
        texture = IntPtr.Zero;
        Header? h = _header;
        if (h == null || (uint)level >= (uint)h.Levels.Length) return false;
        Level l = h.Levels[level];
        if ((uint)col >= (uint)l.Cols || (uint)row >= (uint)l.Rows) return false;
        if (!Tiles.TryGetValue(l.First + row * l.Cols + col, out Tile? t) || t.Ptr == IntPtr.Zero) return false;
        t.LastUsed = _frame;
        texture = t.Ptr;
        return true;
    }

    private static void Enqueue(Header h, int index)
    {
        lock (Sync)
        {
            Requests.Enqueue(index);
            if (_draining) return;
            _draining = true;
        }
        int gen = _generation;
        Task.Run(() => Drain(h, gen));
    }

    private static void Drain(Header h, int generation)
    {
        try
        {
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            while (true)
            {
                int index;
                lock (Sync)
                {
                    if (generation != _generation || Requests.Count == 0) { _draining = false; return; }
                    index = Requests.Dequeue();
                }
                byte[]? rgba = null;
                try { rgba = Inflate(fs, h, index); } catch { }
                lock (Sync) Results.Enqueue((index, generation, rgba));
            }
        }
        catch
        {
            lock (Sync)
            {
                // The file couldn't be opened: answer what's queued as failed so the tiles retry later.
                _draining = false;
                while (Requests.Count > 0) Results.Enqueue((Requests.Dequeue(), generation, null));
            }
        }
    }

    private static byte[] Inflate(FileStream fs, Header h, int index)
    {
        var comp = new byte[h.Lengths[index]];
        fs.Seek((long)h.Offsets[index], SeekOrigin.Begin);
        fs.ReadExactly(comp);
        if (h.Dxt)
        {
            var blocks = new byte[TileSize / 4 * (TileSize / 4) * 8];           // 32 KB: what the GPU holds as it is
            using var zb = new ZLibStream(new MemoryStream(comp), CompressionMode.Decompress);
            zb.ReadExactly(blocks);
            return blocks;
        }
        var rgb = new byte[TileSize * TileSize * 3];
        using (var z = new ZLibStream(new MemoryStream(comp), CompressionMode.Decompress))
            z.ReadExactly(rgb);
        var rgba = new byte[TileSize * TileSize * 4];
        for (int i = 0, o = 0; i < rgb.Length; i += 3, o += 4)
        {
            rgba[o] = rgb[i]; rgba[o + 1] = rgb[i + 1]; rgba[o + 2] = rgb[i + 2]; rgba[o + 3] = 255;
        }
        return rgba;
    }

    /// <summary>
    /// Makes textures from the tiles read since the last frame (at most MaxCreatesPerFrame) and evicts the
    /// least recently drawn over the cap, before anything draws. AC's render thread, once a frame.
    /// </summary>
    public static void BeginFrame(IntPtr device)
    {
        _frame++;
        if (_switchTo is string next) { _switchTo = null; Shutdown(); Open(next); return; }
        if (_pending == 0 && Tiles.Count <= MaxTextures) return;
        int made = 0;
        while (made < MaxCreatesPerFrame)
        {
            (int Index, int Generation, byte[]? Rgba) r;
            lock (Sync)
            {
                if (Results.Count == 0) break;
                r = Results.Dequeue();
            }
            if (r.Generation != _generation || !Tiles.TryGetValue(r.Index, out Tile? t) || !t.Pending) continue;
            t.Pending = false;
            _pending = Math.Max(0, _pending - 1);
            IntPtr ptr = IntPtr.Zero;
            if (r.Rgba != null)
            {
                try { ptr = _header?.Dxt == true ? DX9Backend.CreateTextureDxt1(device, TileSize, TileSize, r.Rgba) : DX9Backend.CreateTextureRgba(device, TileSize, TileSize, r.Rgba); } catch { }
                made++;
            }
            if (ptr != IntPtr.Zero) { t.Ptr = ptr; LiveTextures++; }
            else { t.Failures++; t.RetryAt = _frame + 300; }  // an empty slot: retried in ~5 s, dropped when it ages out
        }
        if (Tiles.Count > MaxTextures) Evict();
    }

    private static void Evict()
    {
        Header? h = _header;
        int top = h == null ? -1 : h.Levels[^1].First;     // the single top tile is never evicted
        EvictScratch.Clear();
        foreach (var kv in Tiles)
            if (!kv.Value.Pending && kv.Key != top && kv.Value.LastUsed < _frame - 1) EvictScratch.Add(kv.Key);
        EvictScratch.Sort((a, b) => Tiles[a].LastUsed.CompareTo(Tiles[b].LastUsed));
        int excess = Tiles.Count - MaxTextures;
        for (int i = 0; i < EvictScratch.Count && excess > 0; i++, excess--)
        {
            Tile t = Tiles[EvictScratch[i]];
            if (t.Ptr != IntPtr.Zero) { try { DX9Backend.ReleaseTexture(t.Ptr); } catch { } LiveTextures--; }
            Tiles.Remove(EvictScratch[i]);
        }
        EvictScratch.Clear();
    }

    /// <summary>
    /// Engine shutdown or ImGui teardown (hot reload, unload): releases every texture and drops the
    /// queues; a worker still running finishes into a generation nobody reads. AC's render thread.
    /// </summary>
    public static void Shutdown()
    {
        lock (Sync)
        {
            _generation++;
            Requests.Clear();
            Results.Clear();
        }
        foreach (Tile t in Tiles.Values)
            if (t.Ptr != IntPtr.Zero) { try { DX9Backend.ReleaseTexture(t.Ptr); } catch { } t.Ptr = IntPtr.Zero; }
        Tiles.Clear();
        EvictScratch.Clear();
        LiveTextures = 0;
        _pending = 0;
        _openState = 0;
        _header = null;
    }
}
