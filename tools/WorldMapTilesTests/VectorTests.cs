using System.Numerics;
using RynthCore.Engine.ImGuiBackend;

namespace WorldMapTilesTests
{
    /// <summary>worldmap.vec (RynthNav.Baker WorldMapVectors.Write), written here independently, and the engine's reader.</summary>
    internal static class VectorTests
    {
        private static void WaitDone()
        {
            for (int i = 0; i < 300 && WorldMapVectors.Current == null && !WorldMapVectors.Failed; i++) Thread.Sleep(10);
        }

        public static void Run(Action<bool, string> check, string dir, string? realPath)
        {
            string path = Path.Combine(dir, "synthetic.vec");
            using (var fs = new FileStream(path, FileMode.Create))
            using (var w = new BinaryWriter(fs))
            {
                w.Write(new byte[] { (byte)'R', (byte)'V', (byte)'E', (byte)'C' });
                w.Write(1u); w.Write(2u); w.Write(3u);
                w.Write((byte)3); foreach (var (x, y) in new[] { (10f, 20f), (14f, 20f), (12f, 25.5f) }) { w.Write(x); w.Write(y); }
                w.Write((byte)4); foreach (var (x, y) in new[] { (100f, 100f), (102f, 100f), (102f, 103f), (100f, 103f) }) { w.Write(x); w.Write(y); }
                foreach (ushort v in new ushort[] { 1, 2, 3, 4, 2000, 2001, 2002, 2003, 7, 7, 8, 8 }) w.Write(v);
            }
            WorldMapVectors.Open(path);
            WaitDone();
            var d = WorldMapVectors.Current;
            check(d != null && !WorldMapVectors.Failed, "vectors: the file opens");
            if (d != null)
            {
                check(d.Buildings.Length == 2 && d.Buildings[0].Length == 3 && d.Buildings[1].Length == 4, "vectors: two buildings with 3 and 4 corners");
                check(d.Buildings[0][2] == new Vector2(12f, 25.5f) && d.Buildings[1][3] == new Vector2(100f, 103f), "vectors: corners as written");
                check(d.BuildingBounds[0] == new Vector4(10f, 20f, 14f, 25.5f) && d.BuildingBounds[1] == new Vector4(100f, 100f, 102f, 103f), "vectors: bounds for culling");
                check(d.RoadCount == 3 && d.Roads[0] == 1 && d.Roads[3] == 4 && d.Roads[4] == 2000 && d.Roads[11] == 8, "vectors: three road segments as written");
            }
            WorldMapVectors.Shutdown();
            check(WorldMapVectors.Current == null, "vectors: shutdown drops the data");

            string junk = Path.Combine(dir, "junk.vec");
            File.WriteAllBytes(junk, new byte[40]);
            WorldMapVectors.Open(junk);
            WaitDone();
            check(WorldMapVectors.Current == null && WorldMapVectors.Failed, "vectors: a bad file is a failure, not a throw");
            WorldMapVectors.Shutdown();
            WorldMapVectors.Open(Path.Combine(dir, "nothere.vec"));
            WaitDone();
            check(WorldMapVectors.Current == null && WorldMapVectors.Failed, "vectors: a missing file is a failure, not a throw");
            WorldMapVectors.Shutdown();

            if (realPath == null) return;
            Console.WriteLine("real vectors: " + realPath);
            WorldMapVectors.Open(realPath);
            WaitDone();
            var r = WorldMapVectors.Current;
            check(r != null, "real vectors: opens");
            if (r == null) return;
            check(r.Buildings.Length > 5000 && r.RoadCount > 5000, $"real vectors: {r.Buildings.Length} buildings, {r.RoadCount} road segments");
            bool inRange = true, corners = true;
            foreach (Vector2[] b in r.Buildings)
            {
                if (b.Length < 3 || b.Length > 10) corners = false;
                foreach (Vector2 p in b) if (p.X < -10 || p.X > 2060 || p.Y < -10 || p.Y > 2060) inRange = false;
            }
            for (int i = 0; i < r.Roads.Length; i++) if (r.Roads[i] > 2048) inRange = false;
            check(corners, "real vectors: every outline has 3 to 10 corners");
            check(inRange, "real vectors: everything is inside the map");
            WorldMapVectors.Shutdown();
        }
    }
}
