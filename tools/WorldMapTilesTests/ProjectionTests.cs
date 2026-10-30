using System.Numerics;
using RynthCore.Engine.ImGuiBackend.Panels;

namespace WorldMapTilesTests
{
    /// <summary>MapProjection: cells to screen and back, and the rotation that puts the way you face up.</summary>
    internal static class ProjectionTests
    {
        private static bool Near(Vector2 a, Vector2 b, float eps = 0.001f) => Vector2.Distance(a, b) < eps;

        public static void Run(Action<bool, string> check)
        {
            var centre = new Vector2(400, 300);
            var north = new MapProjection(centre, 1000, 1000, 2f, 0f);

            // Map-up: north is up the screen, east is right, 2 px per cell.
            check(Near(north.Project(1000, 1000), centre), "the view centre cell is at the screen centre");
            check(Near(north.Project(1010, 1000), centre + new Vector2(20, 0)), "east is to the right, scale px per cell");
            check(Near(north.Project(1000, 1010), centre + new Vector2(0, -20)), "north is up the screen");
            check(!north.Turned, "no rotation is not turned");
            check(MathF.Abs(MapProjection.PhiForHeading(90f)) < 1e-6f, "facing north needs no rotation");

            // Round trips, through every rotation.
            var rnd = new Random(7);
            bool roundTrip = true;
            for (int i = 0; i < 200; i++)
            {
                var p = new MapProjection(new Vector2(rnd.Next(100, 900), rnd.Next(100, 700)), rnd.NextDouble() * 2000, rnd.NextDouble() * 2000,
                    0.12f + (float)rnd.NextDouble() * 13f, (float)(rnd.NextDouble() * Math.PI * 2));
                double cx = rnd.NextDouble() * 2048, cy = rnd.NextDouble() * 2048;
                p.Unproject(p.Project(cx, cy), out double bx, out double by);
                if (Math.Abs(bx - cx) > 0.01 || Math.Abs(by - cy) > 0.01) roundTrip = false;
            }
            check(roundTrip, "unproject(project(cell)) is the cell, at any zoom and rotation");

            // Rotating with the player: the heading is straight up whatever it is.
            bool headingUp = true;
            foreach (float h in new[] { 0f, 30f, 45f, 90f, 135f, 180f, 225f, 270f, 359f })
            {
                var p = new MapProjection(centre, 1000, 1000, 3f, MapProjection.PhiForHeading(h));
                float r = h * MathF.PI / 180f;
                if (!Near(p.ScreenDir(MathF.Cos(r), MathF.Sin(r)), new Vector2(0, -1), 0.001f)) headingUp = false;
                // And a point ahead of the player, in the map, is above them on screen.
                Vector2 ahead = p.Project(1000 + 10 * Math.Cos(r), 1000 + 10 * Math.Sin(r));
                if (!(ahead.Y < centre.Y - 29f && MathF.Abs(ahead.X - centre.X) < 0.01f)) headingUp = false;
            }
            check(headingUp, "the way you face is up on screen at every heading");

            // Where the compass needle and your right hand go.
            var east = new MapProjection(centre, 0, 0, 1f, MapProjection.PhiForHeading(0f));    // facing east
            check(Near(east.ScreenDir(0, 1), new Vector2(-1, 0)), "facing east: north is on your left");
            var south = new MapProjection(centre, 0, 0, 1f, MapProjection.PhiForHeading(270f));
            check(Near(south.ScreenDir(0, 1), new Vector2(0, 1)), "facing south: north is behind you (down)");
            var facingNorth = new MapProjection(centre, 0, 0, 1f, MapProjection.PhiForHeading(90f));
            check(Near(facingNorth.ScreenDir(1, 0), new Vector2(1, 0)), "facing north: east is on your right");
            var facingWest = new MapProjection(centre, 0, 0, 1f, MapProjection.PhiForHeading(180f));
            check(Near(facingWest.ScreenDir(0, 1), new Vector2(1, 0)), "facing west: north is on your right");

            // Zoom about the cursor: the cell under the mouse stays under the mouse.
            bool zoomFixed = true;
            for (int i = 0; i < 100; i++)
            {
                var p = new MapProjection(new Vector2(300, 250), rnd.NextDouble() * 2000, rnd.NextDouble() * 2000, 0.3f + (float)rnd.NextDouble() * 6f,
                    (float)(rnd.NextDouble() * Math.PI * 2));
                var mouse = new Vector2(rnd.Next(0, 600), rnd.Next(0, 500));
                float next = p.Scale * (0.4f + (float)rnd.NextDouble() * 3f);
                p.Unproject(mouse, out double bx, out double by);
                var (nx, ny) = p.CentreForZoomAbout(mouse, next);
                var after = new MapProjection(p.Centre, nx, ny, next, MathF.Atan2(p.Sin, p.Cos));
                if (!Near(after.Project(bx, by), mouse, 0.05f)) zoomFixed = false;
            }
            check(zoomFixed, "zooming about the cursor keeps the cell under it");

            // Dragging: the cell that was under the mouse moves with it.
            bool dragFollows = true;
            for (int i = 0; i < 100; i++)
            {
                var p = new MapProjection(new Vector2(300, 250), rnd.NextDouble() * 2000, rnd.NextDouble() * 2000, 0.3f + (float)rnd.NextDouble() * 6f,
                    (float)(rnd.NextDouble() * Math.PI * 2));
                var mouse = new Vector2(rnd.Next(0, 600), rnd.Next(0, 500));
                var delta = new Vector2(rnd.Next(-80, 80), rnd.Next(-80, 80));
                p.Unproject(mouse, out double bx, out double by);
                var (nx, ny) = p.CentreAfterDrag(delta);
                var after = new MapProjection(p.Centre, nx, ny, p.Scale, MathF.Atan2(p.Sin, p.Cos));
                if (!Near(after.Project(bx, by), mouse + delta, 0.05f)) dragFollows = false;
            }
            check(dragFollows, "dragging moves the map with the mouse, at any rotation");

            // Dragging right moves the map right: the centre cell moves west (smaller x) when not rotated.
            var (dx, _) = north.CentreAfterDrag(new Vector2(20, 0));
            check(Math.Abs(dx - (1000 - 10)) < 1e-6, "dragging 20 px right at 2 px per cell moves the centre 10 cells west");
            var (_, dy) = north.CentreAfterDrag(new Vector2(0, 20));
            check(Math.Abs(dy - (1000 + 10)) < 1e-6, "dragging 20 px down moves the centre 10 cells north");
        }
    }
}
