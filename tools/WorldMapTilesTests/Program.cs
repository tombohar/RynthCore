using System.IO.Compression;
using RynthCore.Engine.ImGuiBackend;

namespace RynthCore.Engine.ImGuiBackend
{
    /// <summary>Stands in for the engine's D3D9 backend: a "texture" is a counter, remembering its first pixel.</summary>
    internal static class DX9Backend
    {
        public static readonly Dictionary<IntPtr, (byte R, byte G, byte B, byte A)> Live = new();
        private static long _next = 0x1000;
        public static int Created, Released;

        public static IntPtr CreateTextureRgba(IntPtr device, int width, int height, ReadOnlySpan<byte> rgba)
        {
            if (width != 256 || height != 256 || rgba.Length != 256 * 256 * 4) return IntPtr.Zero;
            var p = new IntPtr(_next += 0x10);
            Live[p] = (rgba[0], rgba[1], rgba[2], rgba[3]);
            Created++;
            return p;
        }

        public static int DxtCreated;

        /// <summary>A DXT1 tile: the first two bytes (the first block's colour 0) say which tile it is.</summary>
        public static IntPtr CreateTextureDxt1(IntPtr device, int width, int height, ReadOnlySpan<byte> blocks)
        {
            if (width != 256 || height != 256 || blocks.Length != 256 * 256 / 2) return IntPtr.Zero;
            var p = new IntPtr(_next += 0x10);
            Live[p] = (blocks[0], blocks[1], 0, 255);
            Created++; DxtCreated++;
            return p;
        }

        public static void ReleaseTexture(IntPtr tex) { if (Live.Remove(tex)) Released++; }
    }
}

namespace WorldMapTilesTests
{
    /// <summary>The map file's layout (RynthNav.Baker WorldMapRender.WriteRmap), written here independently.</summary>
    internal static class Program
    {
        private static int _checks, _failed;
        private static readonly IntPtr Dev = new(1);

        private static int Main(string[] args)
        {
            string dir = Path.Combine(Path.GetTempPath(), "wmtiles-" + Environment.ProcessId);
            Directory.CreateDirectory(dir);
            try
            {
                ProjectionTests.Run(Check);
                Synthetic(Path.Combine(dir, "synthetic.rmap"));
                VectorTests.Run(Check, dir, args.Length > 1 ? args[1] : null);
                DxtTileTests.Run(Check, dir);
                if (args.Length > 0) Real(args[0]);
                if (args.Length > 2) DxtTileTests.RealDxt(args[2], Check);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
            Console.WriteLine(_failed == 0 ? $"OK: {_checks} checks" : $"FAILED: {_failed} of {_checks} checks");
            return _failed == 0 ? 0 : 1;
        }

        private static void Check(bool ok, string what)
        {
            _checks++;
            if (!ok) { _failed++; Console.WriteLine("FAIL: " + what); }
        }

        // A tile's colour from its level and position, so a read tile can be told from any other.
        private static (byte R, byte G, byte B) Colour(int level, int col, int row) =>
            ((byte)(40 + level * 60), (byte)(10 + col * 17), (byte)(10 + row * 17));

        private static void WriteSynthetic(string path, (int Cols, int Rows)[] levels)
        {
            var blobs = new List<byte[]>();
            var first = new int[levels.Length];
            for (int l = 0; l < levels.Length; l++)
            {
                first[l] = blobs.Count;
                for (int r = 0; r < levels[l].Rows; r++)
                    for (int c = 0; c < levels[l].Cols; c++)
                    {
                        var (cr, cg, cb) = Colour(l, c, r);
                        var raw = new byte[256 * 256 * 3];
                        for (int i = 0; i < raw.Length; i += 3) { raw[i] = cr; raw[i + 1] = cg; raw[i + 2] = cb; }
                        using var ms = new MemoryStream();
                        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true)) z.Write(raw);
                        blobs.Add(ms.ToArray());
                    }
            }
            long header = 4 + 6 * 4 + levels.Length * 20 + 4 + blobs.Count * 12L;
            using var fs = new FileStream(path, FileMode.Create);
            using var w = new BinaryWriter(fs);
            w.Write(new byte[] { (byte)'R', (byte)'M', (byte)'A', (byte)'P' });
            w.Write(1u); w.Write(256u); w.Write((uint)levels.Length); w.Write(2u);
            w.Write((uint)(levels[0].Cols * 256 / 2)); w.Write((uint)(levels[0].Rows * 256 / 2));
            for (int l = 0; l < levels.Length; l++)
            { w.Write((uint)(levels[l].Cols * 256)); w.Write((uint)(levels[l].Rows * 256)); w.Write((uint)levels[l].Cols); w.Write((uint)levels[l].Rows); w.Write((uint)first[l]); }
            w.Write((uint)blobs.Count);
            long off = header;
            foreach (byte[] b in blobs) { w.Write((ulong)off); w.Write((uint)b.Length); off += b.Length; }
            foreach (byte[] b in blobs) w.Write(b);
        }

        private static bool WaitOpen(string path)
        {
            WorldMapTiles.Open(path);
            for (int i = 0; i < 300 && !WorldMapTiles.IsOpen; i++) Thread.Sleep(10);
            return WorldMapTiles.IsOpen;
        }

        private static void WaitStatus()
        {
            for (int i = 0; i < 300 && (WorldMapTiles.Status.Length == 0 || WorldMapTiles.Status == "Loading the map..."); i++) Thread.Sleep(10);
        }

        /// <summary>Asks for a tile each frame, as a face does, until it's there or the time is up.</summary>
        private static bool Load(int level, int col, int row, out IntPtr tex)
        {
            tex = IntPtr.Zero;
            for (int i = 0; i < 400; i++)
            {
                WorldMapTiles.BeginFrame(Dev);
                if (WorldMapTiles.TryGet(level, col, row, out tex)) return true;
                Thread.Sleep(2);
            }
            return false;
        }

        private static void Synthetic(string path)
        {
            // 12x12 tiles at level 0 (144, more than the 96-texture cap), 6x6, then 3x3.
            WriteSynthetic(path, new[] { (12, 12), (6, 6), (3, 3) });
            Check(WaitOpen(path), "the file opens");
            Check(WorldMapTiles.Levels.Count == 3, "three levels");
            Check(WorldMapTiles.Levels.Count == 3 && WorldMapTiles.Levels[0].Cols == 12 && WorldMapTiles.Levels[1].First == 144 && WorldMapTiles.Levels[2].First == 144 + 36, "level table");
            Check(WorldMapTiles.PixelsPerCell == 2 && WorldMapTiles.CellsWide == 1536 && WorldMapTiles.CellsHigh == 1536, "pixels per cell and size in cells");

            // A tile arrives with its own pixels, and Peek sees it only once loaded.
            Check(!WorldMapTiles.Peek(1, 4, 5, out _), "peek: not loaded yet");
            bool got = Load(1, 4, 5, out IntPtr t);
            Check(got, "a tile loads");
            var (er, eg, eb) = Colour(1, 4, 5);
            Check(got && DX9Backend.Live.TryGetValue(t, out var px) && px.R == er && px.G == eg && px.B == eb && px.A == 255, "its pixels are that tile's, opaque");
            Check(WorldMapTiles.Peek(1, 4, 5, out IntPtr again) && again == t, "peek finds it");
            Check(!WorldMapTiles.TryGet(1, 6, 0, out _) && !WorldMapTiles.TryGet(1, 0, 6, out _) && !WorldMapTiles.TryGet(3, 0, 0, out _) && !WorldMapTiles.TryGet(-1, 0, 0, out _), "out of range: nothing");

            // Tiles by column and row: the colours say which one came back.
            Check(Load(0, 0, 0, out IntPtr a) && Load(0, 11, 11, out IntPtr b) && a != b
                && DX9Backend.Live[a].G == Colour(0, 0, 0).G && DX9Backend.Live[b].G == Colour(0, 11, 11).G && DX9Backend.Live[b].B == Colour(0, 11, 11).B, "tiles by column and row");

            // The cap: 120 tiles of level 0, each loaded once and left; the table never holds more than 96 and the oldest go.
            Check(Load(2, 0, 0, out IntPtr topFirst), "the first tile of the top level loads");
            int loaded = 0;
            for (int i = 0; i < 120; i++) if (Load(0, i % 12, i / 12, out _)) loaded++;
            for (int i = 0; i < 4; i++) WorldMapTiles.BeginFrame(Dev);
            Check(loaded == 120, "120 tiles loaded in turn (" + loaded + ")");
            Check(WorldMapTiles.LiveTextures <= 96 && DX9Backend.Live.Count <= 96, $"never over 96 textures (engine {WorldMapTiles.LiveTextures}, backend {DX9Backend.Live.Count})");
            Check(DX9Backend.Created - DX9Backend.Released == DX9Backend.Live.Count && WorldMapTiles.LiveTextures == DX9Backend.Live.Count, "every texture that left the table was released");
            Check(!WorldMapTiles.Peek(0, 0, 0, out _), "the oldest was evicted");
            Check(WorldMapTiles.Peek(0, 119 % 12, 119 / 12, out _), "the newest is kept");
            Check(WorldMapTiles.Peek(2, 0, 0, out IntPtr topAgain) && topAgain == topFirst, "the top level's first tile is never evicted");

            // Shutdown: nothing left alive, and it can open again.
            WorldMapTiles.Shutdown();
            Check(DX9Backend.Live.Count == 0 && WorldMapTiles.LiveTextures == 0, "shutdown released every texture");
            Check(!WorldMapTiles.IsOpen && WorldMapTiles.Levels.Count == 0, "shutdown closed the file");
            Check(WaitOpen(path) && Load(1, 0, 0, out _), "it opens again after shutdown");
            WorldMapTiles.Shutdown();

            // A file that isn't a map, and a missing one: a status, no throw.
            string dir = Path.GetDirectoryName(path)!;
            string junk = Path.Combine(dir, "junk.rmap");
            File.WriteAllBytes(junk, new byte[64]);
            WorldMapTiles.Open(junk);
            WaitStatus();
            Check(!WorldMapTiles.IsOpen && WorldMapTiles.Status.Contains("didn't load"), "a bad file says so: " + WorldMapTiles.Status);
            WorldMapTiles.Shutdown();
            WorldMapTiles.Open(Path.Combine(dir, "nothere.rmap"));
            WaitStatus();
            Check(!WorldMapTiles.IsOpen && WorldMapTiles.Status.Contains("No world map file"), "a missing file says so: " + WorldMapTiles.Status);
            WorldMapTiles.Shutdown();
        }

        private static void Real(string path)
        {
            Console.WriteLine("real file: " + path);
            Check(WaitOpen(path), "real: opens (" + WorldMapTiles.Status + ")");
            if (!WorldMapTiles.IsOpen) return;
            var lv = WorldMapTiles.Levels;
            Check(lv.Count == 5 && lv[0].Width == 4096 && lv[4].Cols == 1 && lv[4].Rows == 1, "real: 5 levels, 4096 px down to one tile");
            Check(WorldMapTiles.PixelsPerCell == 2 && WorldMapTiles.CellsWide == 2048, "real: 2 px per cell over 2048 cells");
            // The top tile is the whole map: mostly deep sea (blue), so its corner pixel is the sea colour.
            Check(Load(4, 0, 0, out IntPtr top) && DX9Backend.Live[top].B > DX9Backend.Live[top].R, "real: the top tile loads and its corner is sea");
            // Every tile of every level inflates to a full opaque tile.
            int bad = 0, total = 0;
            for (int l = 0; l < lv.Count; l++)
                for (int r = 0; r < lv[l].Rows; r++)
                    for (int c = 0; c < lv[l].Cols; c++)
                    {
                        total++;
                        if (!Load(l, c, r, out _)) bad++;
                    }
            Check(bad == 0, $"real: all {total} tiles load ({bad} failed)");
            WorldMapTiles.Shutdown();
            Check(DX9Backend.Live.Count == 0, "real: shutdown released all");
        }
    }
}
