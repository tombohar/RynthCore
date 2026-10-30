// ============================================================================
//  RynthCore.Engine - ImGui/Panels/MapProjection.cs
//  The World Map's geometry, with no ImGui in it (tools\WorldMapTilesTests checks it offline):
//  map cells (x east, y north, one cell = 24 yards) to screen pixels and back, through the view
//  centre, the zoom (pixels per cell) and a rotation.
//
//    screen = centre + R(phi) * (dx * scale, -dy * scale),   dx, dy = cell - view centre cell
//    R(phi) = [cos -sin; sin cos] on a y-down screen (so a positive phi turns the map clockwise)
//
//  Rotating with the player: heading is in degrees, 0 = east, 90 = north (AC), so the heading
//  direction is (cos h, sin h) in the map; phi = heading - 90 degrees turns it to straight up.
// ============================================================================

using System;
using System.Numerics;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal readonly struct MapProjection
{
    public readonly Vector2 Centre;       // screen position of the view centre cell
    public readonly double CentreX, CentreY;   // the view centre, in cells
    public readonly float Scale;          // screen pixels per cell
    public readonly float Cos, Sin;       // of the rotation

    public MapProjection(Vector2 centre, double centreX, double centreY, float scale, float phi)
    {
        Centre = centre; CentreX = centreX; CentreY = centreY; Scale = scale;
        Cos = MathF.Cos(phi); Sin = MathF.Sin(phi);
    }

    public bool Turned => Sin != 0f;

    /// <summary>The rotation that turns the way you face to up, for a heading in degrees (0 = east, 90 = north).</summary>
    public static float PhiForHeading(float headingDegrees) => (headingDegrees - 90f) * MathF.PI / 180f;

    public Vector2 Project(double cellX, double cellY)
    {
        float sx = (float)((cellX - CentreX) * Scale), sy = (float)(-(cellY - CentreY) * Scale);
        return new Vector2(Centre.X + sx * Cos - sy * Sin, Centre.Y + sx * Sin + sy * Cos);
    }

    public void Unproject(Vector2 p, out double cellX, out double cellY) => Unproject(p, Scale, out cellX, out cellY);

    public void Unproject(Vector2 p, float scale, out double cellX, out double cellY)
    {
        Vector2 d = p - Centre;
        float ux = d.X * Cos + d.Y * Sin, uy = -d.X * Sin + d.Y * Cos;
        cellX = CentreX + ux / scale;
        cellY = CentreY - uy / scale;
    }

    /// <summary>A map direction (east, north) as a screen direction.</summary>
    public Vector2 ScreenDir(float east, float north)
    {
        float sx = east, sy = -north;
        return new Vector2(sx * Cos - sy * Sin, sx * Sin + sy * Cos);
    }

    /// <summary>
    /// The view centre (cells) that keeps the cell under <paramref name="screenPoint"/> where it is
    /// when the zoom changes from this projection's scale to <paramref name="newScale"/>.
    /// </summary>
    public (double X, double Y) CentreForZoomAbout(Vector2 screenPoint, float newScale)
    {
        Unproject(screenPoint, out double mx, out double my);
        Vector2 d = screenPoint - Centre;
        float ux = d.X * Cos + d.Y * Sin, uy = -d.X * Sin + d.Y * Cos;
        return (mx - ux / newScale, my + uy / newScale);
    }

    /// <summary>The view centre (cells) after dragging the map by <paramref name="screenDelta"/> pixels.</summary>
    public (double X, double Y) CentreAfterDrag(Vector2 screenDelta)
    {
        float ux = screenDelta.X * Cos + screenDelta.Y * Sin, uy = -screenDelta.X * Sin + screenDelta.Y * Cos;
        return (CentreX - ux / Scale, CentreY + uy / Scale);
    }
}
