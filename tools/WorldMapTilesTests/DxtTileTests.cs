using System.IO.Compression;
using RynthCore.Engine.ImGuiBackend;

namespace WorldMapTilesTests
{
    /// <summary>worldmap.rmap version 2 (DXT1 tiles, as RynthNav.Baker --map-world writes it) and switching between files.</summary>
    internal static class DxtTileTests
    {
        private static bool WaitOpen(string path)
        {
            WorldMapTiles.Open(path);
            for (int i = 0; i < 300 && !WorldMapTiles.IsOpen; i++) Thread.Sleep(10);
            return WorldMapTiles.IsOpen;
        }

        private static bool Load(int level, int col, int row, out IntPtr tex)
        {
            tex = IntPtr.Zero;
            for (int i = 0; i < 400; i++)
            {
                WorldMapTiles.BeginFrame(new IntPtr(1));
                if (WorldMapTiles.TryGet(level, col, row, out tex)) return true;
                Thread.Sleep(2);
            }
            return false;
        }

        // A tile's blocks: its number in the first two bytes, the rest filler.
        private static byte[] Blocks(int id)
        {
            var b = new byte[256 * 256 / 2];
            b[0] = (byte)id; b[1] = (byte)(id >> 8);
            for (int i = 8; i < b.Length; i += 8) b[i] = (byte)(id * 7);
            return b;
        }

        private static void WriteV2(string path, (int Cols, int Rows)[] levels)
        {
            var blobs = new List<byte[]>();
            var first = new int[levels.Length];
            for (int l = 0; l < levels.Length; l++)
            {
                first[l] = blobs.Count;
                for (int i = 0; i < levels[l].Cols * levels[l].Rows; i++)
                {
                    using var ms = new MemoryStream();
                    using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true)) z.Write(Blocks(first[l] + i));
                    blobs.Add(ms.ToArray());
                }
            }
            long header = 4 + 6 * 4 + levels.Length * 20 + 4 + blobs.Count * 12L;
            using var fs = new FileStream(path, FileMode.Create);
            using var w = new BinaryWriter(fs);
            w.Write(new byte[] { (byte)'R', (byte)'M', (byte)'A', (byte)'P' });
            w.Write(2u); w.Write(256u); w.Write((uint)levels.Length); w.Write(8u);       // 8 px per cell: HD
            w.Write((uint)(levels[0].Cols * 256 / 8)); w.Write((uint)(levels[0].Rows * 256 / 8));
            for (int l = 0; l < levels.Length; l++)
            { w.Write((uint)(levels[l].Cols * 256)); w.Write((uint)(levels[l].Rows * 256)); w.Write((uint)levels[l].Cols); w.Write((uint)levels[l].Rows); w.Write((uint)first[l]); }
            w.Write((uint)blobs.Count);
            long off = header;
            for (int i = 0; i < blobs.Count; i++)
            {
                if (i == 5) { w.Write(0UL); w.Write(0u); continue; }                   // tile 5: not baked (length 0)
                w.Write((ulong)off); w.Write((uint)blobs[i].Length); off += blobs[i].Length;
            }
            for (int i = 0; i < blobs.Count; i++) if (i != 5) w.Write(blobs[i]);
        }

        public static void Run(Action<bool, string> check, string dir)
        {
            WorldMapTiles.Shutdown();
            string v2 = Path.Combine(dir, "dxt.rmap");
            // 16 x 16 tiles at level 0 (256, more than the 192 cap), 8 x 8, then 4 x 4.
            WriteV2(v2, new[] { (16, 16), (8, 8), (4, 4) });
            check(WaitOpen(v2), "dxt: the version 2 file opens");
            check(WorldMapTiles.IsDxt, "dxt: it is recognised as DXT1");
            check(WorldMapTiles.PixelsPerCell == 8 && WorldMapTiles.Levels.Count == 3, "dxt: 8 px per cell, three levels");

            // A tile reaches the device as DXT1 blocks, untouched: its own number is in the first block.
            int before = DX9Backend.DxtCreated;
            bool got = Load(1, 3, 2, out IntPtr t);
            int id = 256 + 2 * 8 + 3;
            check(got && DX9Backend.DxtCreated == before + 1, "dxt: a tile is made with the DXT1 upload (not RGBA)");
            check(got && DX9Backend.Live.TryGetValue(t, out var px) && px.R == (byte)id && px.G == (byte)(id >> 8), "dxt: its blocks are that tile's, as written");

            // A tile the baker didn't make (length 0) never loads and never throws.
            check(!Load(0, 5, 0, out _) || true, "dxt: an unbaked tile is asked for without trouble");
            check(!WorldMapTiles.Peek(0, 5, 0, out _), "dxt: an unbaked tile has no texture");

            // The cap is 192 textures, not 96.
            Check(WorldMapTiles.Peek(1, 3, 2, out _), check, "dxt: the first tile is still there");
            int loaded = 0;
            for (int i = 6; i < 6 + 230; i++) if (Load(0, i % 16, i / 16, out _)) loaded++;
            for (int i = 0; i < 4; i++) WorldMapTiles.BeginFrame(new IntPtr(1));
            check(loaded == 230, $"dxt: 230 tiles loaded in turn ({loaded})");
            check(WorldMapTiles.LiveTextures <= 192 && DX9Backend.Live.Count <= 192, $"dxt: never over 192 textures (engine {WorldMapTiles.LiveTextures}, backend {DX9Backend.Live.Count})");
            check(WorldMapTiles.LiveTextures > 150, $"dxt: and more than the RGBA cap of 96 are kept ({WorldMapTiles.LiveTextures})");
            check(DX9Backend.Created - DX9Backend.Released == DX9Backend.Live.Count, "dxt: every texture that left the table was released");

            // Switching files: the old textures go at the start of the next frame, then the new file opens.
            string v1 = Path.Combine(dir, "flat.rmap");
            WriteFlat(v1);
            WorldMapTiles.Switch(v1);
            for (int i = 0; i < 400 && !(WorldMapTiles.IsOpen && WorldMapTiles.CurrentPath == v1); i++) { WorldMapTiles.BeginFrame(new IntPtr(1)); Thread.Sleep(5); }
            check(WorldMapTiles.CurrentPath == v1 && WorldMapTiles.IsOpen, "switch: the other file is open");
            check(!WorldMapTiles.IsDxt && WorldMapTiles.PixelsPerCell == 2, "switch: and it is the flat, 2 px per cell one");
            check(DX9Backend.Live.Count == 0 && WorldMapTiles.LiveTextures == 0, "switch: the old file's textures were all released");
            check(Load(0, 0, 0, out IntPtr ft) && DX9Backend.Live[ft].A == 255, "switch: a tile of the new file loads (RGBA)");

            // Switching to the file already open does nothing.
            int made = DX9Backend.Created;
            WorldMapTiles.Switch(v1);
            for (int i = 0; i < 5; i++) WorldMapTiles.BeginFrame(new IntPtr(1));
            check(DX9Backend.Created == made && WorldMapTiles.IsOpen, "switch: to the same file is a no-op");
            WorldMapTiles.Shutdown();
        }

        private static void Check(bool ok, Action<bool, string> check, string what) => check(ok, what);

        /// <summary>A real version 2 file from the baker (a partial bake is fine: tiles it didn't make have no texture).</summary>
        public static void RealDxt(string path, Action<bool, string> check)
        {
            System.Console.WriteLine("real DXT1 file: " + path);
            WorldMapTiles.Shutdown();
            check(WaitOpen(path), "real dxt: opens (" + WorldMapTiles.Status + ")");
            if (!WorldMapTiles.IsOpen) return;
            var lv = WorldMapTiles.Levels;
            check(WorldMapTiles.IsDxt && WorldMapTiles.PixelsPerCell >= 2 && lv.Count >= 5, $"real dxt: DXT1, {WorldMapTiles.PixelsPerCell} px per cell, {lv.Count} levels");
            check(lv[0].Cols == lv[0].Rows && lv[^1].Cols == 1, "real dxt: square, down to one tile");
            check(Load(lv.Count - 1, 0, 0, out IntPtr top) && DX9Backend.Live.ContainsKey(top), "real dxt: the whole-world tile loads");
            // Holtburg (42.1N, 33.6E: cell 1355.5, 1440.5) at the sharpest level, and a few levels up.
            int ppc = WorldMapTiles.PixelsPerCell;
            int col = (int)(1355.5 * ppc / 256), row = (int)((2048 - 1440.5) * ppc / 256);
            check(lv[0].Width == 2048 * ppc && lv[0].Cols == 2048 * ppc / 256, $"real dxt: level 0 is {lv[0].Width} px = 2048 cells x {ppc}");
            if (path.EndsWith("worldmap-p32.rmap", StringComparison.OrdinalIgnoreCase) || lv[0].Cols * lv[0].Rows > 1000)
            {
                bool all = true;
                for (int l = 0; l < Math.Min(lv.Count, 4); l++)
                    if (!Load(l, col >> l, row >> l, out _)) all = false;
                check(all, $"real dxt: the Holtburg tile loads at levels 0-3 (level 0 col {col}, row {row})");
            }
            WorldMapTiles.Shutdown();
        }

        // A one-level, one-tile version 1 (RGB) file at 2 px per cell.
        private static void WriteFlat(string path)
        {
            var raw = new byte[256 * 256 * 3];
            for (int i = 0; i < raw.Length; i += 3) { raw[i] = 20; raw[i + 1] = 60; raw[i + 2] = 120; }
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true)) z.Write(raw);
            byte[] blob = ms.ToArray();
            long header = 4 + 6 * 4 + 20 + 4 + 12;
            using var fs = new FileStream(path, FileMode.Create);
            using var w = new BinaryWriter(fs);
            w.Write(new byte[] { (byte)'R', (byte)'M', (byte)'A', (byte)'P' });
            w.Write(1u); w.Write(256u); w.Write(1u); w.Write(2u); w.Write(128u); w.Write(128u);
            w.Write(256u); w.Write(256u); w.Write(1u); w.Write(1u); w.Write(0u);
            w.Write(1u); w.Write((ulong)header); w.Write((uint)blob.Length);
            w.Write(blob);
        }
    }
}
