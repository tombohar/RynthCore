using System.Numerics;
using RynthCore.Engine.UI.Data;

namespace DungeonMapNavTests;

/// <summary>
/// The Dungeon Map's coordinate mapping (DungeonMapMath): what a click on the map turns into as
/// a RynthAi nav point, and where a nav point is drawn, through pan, zoom and rotation.
/// </summary>
internal static class Program
{
    private static int _checks, _failed;

    private static int Main()
    {
        NavWorldMatchesRynthAi();
        NorthUpProjection();
        RotatedProjection();
        InverseRoundTrips();
        ClickToNavAndBack();
        LayerForZ();
        FloorUnderClick();
        Picking();

        Console.WriteLine($"{_checks} checks, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (ok) return;
        _failed++;
        Console.WriteLine("FAIL: " + what);
    }

    private static bool Near(double a, double b, double tol) => Math.Abs(a - b) <= tol;
    private static bool Near(Vector2 a, Vector2 b, float tol) => Vector2.Distance(a, b) <= tol;

    /// <summary>
    /// RynthAi's own formula (NavCoordinateHelper.TryConvertPoseToCoords): EW = (lbX * 8 + x / 24
    /// - 1019.5) / 10 from the cell id and local x; the radar's world X is lbX * 192 + x. A patrol
    /// log waypoint: (18665.0, 13300.0, -24.0) in cell 0x61460127 (local 41, -140).
    /// </summary>
    private static void NavWorldMatchesRynthAi()
    {
        uint cell = 0x61460127;
        int lbX = (int)(cell >> 24), lbY = (int)((cell >> 16) & 0xFF);
        float lx = 41f, ly = -140f, z = -24f;
        double ew = (lbX * 8.0 + lx / 24.0 - 1019.5) / 10.0;
        double ns = (lbY * 8.0 + ly / 24.0 - 1019.5) / 10.0;
        float wx = lbX * 192f + lx, wy = lbY * 192f + ly;
        Check(Near(wx, 18665.0, 1e-3) && Near(wy, 13300.0, 1e-3), "world from cell + local");
        Check(Near(DungeonMapMath.WorldToNav(wx), ew, 1e-9), "WorldToNav = RynthAi EW");
        Check(Near(DungeonMapMath.WorldToNav(wy), ns, 1e-9), "WorldToNav = RynthAi NS");
        Vector2 back = DungeonMapMath.NavToWorld(ns, ew);
        Check(Near(back, new Vector2(wx, wy), 0.01f), "NavToWorld(NS, EW) = (world x, world y)");
        Check(Near(DungeonMapMath.NavZToWorld(z / 240.0), z, 1e-4), "nav Z = world Z / 240 (addWaypoint)");
        Check(Near(DungeonMapMath.WorldZToNav(z), -0.1, 1e-9), "WorldZToNav");
        // One nav unit is 240 world units (yards): 0.01 nav = 2.4.
        Check(Near(DungeonMapMath.NavToWorld(10.01) - DungeonMapMath.NavToWorld(10.0), 2.4, 1e-6), "0.01 nav = 2.4 world");
    }

    private static void NorthUpProjection()
    {
        var at = new Vector2(400, 300);
        var p = new DungeonMapProjection(at, 1000f, 2000f, 37f, rotate: false, zoom: 5f);
        Check(Near(p.P(1000f, 2000f), at, 1e-4f), "player at the centre");
        Check(Near(p.P(1010f, 2000f), at + new Vector2(50, 0), 1e-3f), "east is right");
        Check(Near(p.P(1000f, 2010f), at + new Vector2(0, -50), 1e-3f), "north is up");
    }

    private static void RotatedProjection()
    {
        var at = new Vector2(400, 300);
        // Heading 90 = facing north: north is up, east right (same as north-up).
        var n = new DungeonMapProjection(at, 0f, 0f, 90f, rotate: true, zoom: 2f);
        Check(Near(n.P(0f, 10f), at + new Vector2(0, -20), 1e-3f), "facing north: ahead (north) is up");
        Check(Near(n.P(10f, 0f), at + new Vector2(20, 0), 1e-3f), "facing north: east is right");
        // Heading 0 = facing east: east is up, south is right.
        var e = new DungeonMapProjection(at, 0f, 0f, 0f, rotate: true, zoom: 2f);
        Check(Near(e.P(10f, 0f), at + new Vector2(0, -20), 1e-3f), "facing east: east is up");
        Check(Near(e.P(0f, -10f), at + new Vector2(20, 0), 1e-3f), "facing east: south is right");
        Check(Near(e.P(0f, 10f), at + new Vector2(-20, 0), 1e-3f), "facing east: north is left");
    }

    private static void InverseRoundTrips()
    {
        var rng = new Random(12345);
        foreach (bool rotate in new[] { false, true })
            for (int t = 0; t < 400; t++)
            {
                var at = new Vector2(rng.Next(0, 1200), rng.Next(0, 900));
                float pwx = 18000f + (float)rng.NextDouble() * 1000f, pwy = 13000f + (float)rng.NextDouble() * 1000f;
                float heading = (float)rng.NextDouble() * 360f, zoom = 1f + (float)rng.NextDouble() * 19f;
                var p = new DungeonMapProjection(at, pwx, pwy, heading, rotate, zoom);
                var w = new Vector2(pwx + (float)(rng.NextDouble() * 200 - 100), pwy + (float)(rng.NextDouble() * 200 - 100));
                Vector2 s = p.P(w.X, w.Y);
                if (!Near(p.World(s), w, 0.01f)) { Check(false, $"World(P(w)) = w (rotate {rotate}, h {heading}, z {zoom})"); return; }
                var screen = new Vector2(rng.Next(0, 1200), rng.Next(0, 900));
                if (!Near(p.P(p.World(screen).X, p.World(screen).Y), screen, 0.05f)) { Check(false, $"P(World(s)) = s (rotate {rotate})"); return; }
            }
        Check(true, "projection round trips");
    }

    /// <summary>A click on the map, as the face sends it (addPointAt), lands back on the pixel clicked.</summary>
    private static void ClickToNavAndBack()
    {
        var canvasCentre = new Vector2(500, 400);
        var pan = new Vector2(-37, 22);
        foreach (bool rotate in new[] { false, true })
        {
            var p = new DungeonMapProjection(canvasCentre + pan, 18665f, 13300f, 123.4f, rotate, 6.5f);
            var click = new Vector2(610, 333);
            Vector2 w = p.World(click);
            double ns = DungeonMapMath.WorldToNav(w.Y), ew = DungeonMapMath.WorldToNav(w.X);
            Vector2 w2 = DungeonMapMath.NavToWorld(ns, ew);
            Check(Near(p.P(w2.X, w2.Y), click, 0.05f), $"click -> nav -> screen is the same pixel (rotate {rotate})");
        }
        // Panning moves the map, not the mapping: the same world spot moves by the pan.
        var a = new DungeonMapProjection(canvasCentre, 100f, 100f, 45f, true, 4f);
        var b = new DungeonMapProjection(canvasCentre + pan, 100f, 100f, 45f, true, 4f);
        Check(Near(b.P(120f, 90f) - a.P(120f, 90f), pan, 1e-3f), "pan shifts the drawing by the pan");
    }

    private static void LayerForZ()
    {
        var layers = new List<float> { -24f, -12f, 0f, 6f };
        Check(DungeonMapMath.NearestLayer(layers, -23.995f) == 0, "a point recorded on the floor (feet just above)");
        Check(DungeonMapMath.NearestLayer(layers, 5f) == 3, "nearest floor");
        Check(DungeonMapMath.NearestLayer(layers, -0.1f * 240f) == 0, "nav Z -0.1 = world -24");
        Check(DungeonMapMath.NearestLayer(layers, 60f) == -1, "far above every floor: none");
        Check(DungeonMapMath.NearestLayer(new List<float>(), 0f) == -1, "no floors");
        Check(DungeonMapMath.NearestLayer(layers, float.NaN) == -1, "NaN Z");
    }

    private static void FloorUnderClick()
    {
        // Three floors: 0 covers x 0..10, 1 covers x 5..20, 2 covers x 30..40 (y 0..10 each).
        var fills = new float[]?[]
        {
            new float[] { 0, 0, 10, 10, 0 },
            new float[] { 5, 0, 20, 10, 1 },
            new float[] { 30, 0, 40, 10, 0 },
        };
        var visible = new HashSet<int> { 0, 1, 2 };
        Span<int> order = stackalloc int[3];
        int n = DungeonMapMath.LayerOrder(1, visible, 3, order);
        Check(n == 3 && order[0] == 1, "the player's floor is tried first");
        Check(DungeonMapMath.FloorAt(7, 5, fills, order[..n]) == 1, "overlap: the player's floor wins");
        Check(DungeonMapMath.FloorAt(2, 5, fills, order[..n]) == 0, "only floor 0 there");
        Check(DungeonMapMath.FloorAt(35, 5, fills, order[..n]) == 2, "floor 2");
        Check(DungeonMapMath.FloorAt(25, 5, fills, order[..n]) == -1, "between floors: none");
        Check(DungeonMapMath.FloorAt(7, 11, fills, order[..n]) == -1, "off every floor: none");

        // Hidden floors don't count.
        var only0 = new HashSet<int> { 0 };
        n = DungeonMapMath.LayerOrder(1, only0, 3, order);
        Check(n == 1 && order[0] == 0, "the player's floor hidden: only the shown one");
        Check(DungeonMapMath.FloorAt(15, 5, fills, order[..n]) == -1, "a hidden floor isn't clickable");

        // Order: nearest the player's floor first.
        var all5 = new HashSet<int> { 0, 1, 2, 3, 4 };
        Span<int> o5 = stackalloc int[5];
        n = DungeonMapMath.LayerOrder(2, all5, 5, o5);
        Check(n == 5 && o5[0] == 2 && o5[1] == 1 && o5[2] == 3 && o5[3] == 0 && o5[4] == 4, "2, 1, 3, 0, 4");
        n = DungeonMapMath.LayerOrder(-1, all5, 5, o5);
        Check(n == 5 && o5[0] == 0 && o5[4] == 4, "no current floor: ascending");

        Check(DungeonMapMath.FillBounds(fills, out Vector4 bb) && bb == new Vector4(0, 0, 40, 10), "fill bounds");
        Check(!DungeonMapMath.FillBounds(new float[]?[] { null }, out _), "no fills: no bounds");
    }

    private static void Picking()
    {
        var drawn = new List<(int, Vector2)> { (0, new Vector2(10, 10)), (3, new Vector2(20, 10)), (7, new Vector2(100, 100)) };
        Check(DungeonMapMath.Pick(new Vector2(12, 11), drawn, 8f) == 0, "nearest within the radius");
        Check(DungeonMapMath.Pick(new Vector2(17, 10), drawn, 8f) == 3, "the closer of two");
        Check(DungeonMapMath.Pick(new Vector2(50, 50), drawn, 8f) == -1, "nothing near");
    }
}
