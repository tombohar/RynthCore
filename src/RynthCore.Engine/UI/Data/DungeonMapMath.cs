// ============================================================================
//  RynthCore.Engine - UI/Data/DungeonMapMath.cs
//  The Dungeon Map's coordinate spaces and the conversions between them, kept
//  free of ImGui and the data hub so Tools/DungeonMapNavTests can link it.
//
//  Spaces:
//    screen  - ImGui pixels. The player is drawn at the canvas centre + pan.
//    world   - what RynthAi's radar snapshot uses for walls, fills, markers and
//              the player: AC's global position, landblock X * 192 + local X
//              (east) and landblock Y * 192 + local Y (north). Z is the raw
//              world Z (LayerZs are cell floor heights in the same units).
//    nav     - RynthAi route coordinates: EW = (worldX / 24 - 1019.5) / 10,
//              NS = (worldY / 24 - 1019.5) / 10 (NavCoordinateHelper /
//              DungeonGeometry.WorldToNav), and nav Z = world Z / 240 (what
//              addWaypoint stores). Dungeon routes use the same formula; their
//              local coordinates can run outside 0..192, so NS/EW can sit
//              outside the landblock's outdoor square. That is fine: the map
//              works in world units throughout.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace RynthCore.Engine.UI.Data;

/// <summary>World (AC x east, y north) to screen around the player, north-up or rotated so facing is up; and back.</summary>
internal readonly struct DungeonMapProjection
{
    private readonly float _cx, _cy, _pwx, _pwy, _sin, _cos, _zoom;
    public readonly bool Rotated;

    /// <param name="playerAt">Where the player is drawn (canvas centre + pan).</param>
    /// <param name="headingDeg">AC heading as the radar sends it: 0 = east, 90 = north.</param>
    /// <param name="zoom">Pixels per world unit.</param>
    public DungeonMapProjection(Vector2 playerAt, float playerWorldX, float playerWorldY, float headingDeg, bool rotate, float zoom)
    {
        _cx = playerAt.X; _cy = playerAt.Y; _pwx = playerWorldX; _pwy = playerWorldY;
        float h = headingDeg * MathF.PI / 180f;
        _sin = MathF.Sin(h); _cos = MathF.Cos(h);
        Rotated = rotate; _zoom = zoom;
    }

    public float Sin => _sin;
    public float Cos => _cos;
    public float Zoom => _zoom;

    /// <summary>World to screen.</summary>
    public Vector2 P(float wx, float wy)
    {
        float dx = wx - _pwx, dy = wy - _pwy;
        if (Rotated)
        {
            float right = _sin * dx - _cos * dy;
            float forward = _cos * dx + _sin * dy;
            return new Vector2(_cx + right * _zoom, _cy - forward * _zoom);
        }
        return new Vector2(_cx + dx * _zoom, _cy - dy * _zoom);
    }

    /// <summary>Screen to world: the inverse of <see cref="P"/> (the rotation is orthonormal, so its transpose).</summary>
    public Vector2 World(Vector2 screen)
    {
        if (_zoom <= 0f) return new Vector2(_pwx, _pwy);
        float right = (screen.X - _cx) / _zoom;
        float forward = -(screen.Y - _cy) / _zoom;
        if (Rotated)
        {
            float dx = _sin * right + _cos * forward;
            float dy = -_cos * right + _sin * forward;
            return new Vector2(_pwx + dx, _pwy + dy);
        }
        return new Vector2(_pwx + right, _pwy + forward);
    }
}

internal static class DungeonMapMath
{
    /// <summary>Nav Z is world Z / this.</summary>
    public const double NavZScale = 240.0;

    /// <summary>A route point further than this (world Z) from every floor is on none of them.</summary>
    public const float MaxLayerGap = 12f;

    public static double NavToWorld(double nav) => (nav * 10.0 + 1019.5) * 24.0;
    public static double WorldToNav(double world) => (world / 24.0 - 1019.5) / 10.0;
    public static float NavZToWorld(double navZ) => (float)(navZ * NavZScale);
    public static double WorldZToNav(float worldZ) => worldZ / NavZScale;

    /// <summary>A route point's world position (x east, y north) from its nav NS / EW.</summary>
    public static Vector2 NavToWorld(double ns, double ew) => new((float)NavToWorld(ew), (float)NavToWorld(ns));

    /// <summary>The layer (index into LayerZs) nearest <paramref name="worldZ"/>, or -1 when none is within <paramref name="maxGap"/>.</summary>
    public static int NearestLayer(IReadOnlyList<float> layerZs, float worldZ, float maxGap = MaxLayerGap)
    {
        if (layerZs.Count == 0 || !float.IsFinite(worldZ)) return -1;
        int best = 0;
        float bestD = MathF.Abs(layerZs[0] - worldZ);
        for (int i = 1; i < layerZs.Count; i++)
        {
            float d = MathF.Abs(layerZs[i] - worldZ);
            if (d < bestD) { bestD = d; best = i; }
        }
        return bestD <= maxGap ? best : -1;
    }

    /// <summary>
    /// The order floors are tried for a click: the player's floor first (when shown), then the
    /// other shown floors nearest it first. Returns how many indices were written.
    /// </summary>
    public static int LayerOrder(int current, ICollection<int> visible, int layers, Span<int> dst)
    {
        int n = 0;
        if (current < 0 || current >= layers)
        {
            // No current floor: the shown ones in ascending order.
            for (int i = 0; i < layers && n < dst.Length; i++)
                if (visible.Contains(i)) dst[n++] = i;
            return n;
        }
        if (visible.Contains(current) && n < dst.Length) dst[n++] = current;
        for (int d = 1; d < layers; d++)
        {
            int below = current - d, above = current + d;
            if (below >= 0 && visible.Contains(below) && n < dst.Length) dst[n++] = below;
            if (above < layers && visible.Contains(above) && n < dst.Length) dst[n++] = above;
        }
        return n;
    }

    /// <summary>True when (x, y) is inside one of the fill strips (5 floats each: x0, y0, x1, y1, type).</summary>
    public static bool InFill(float x, float y, float[]? strips)
    {
        if (strips == null) return false;
        for (int i = 0; i + 4 < strips.Length; i += 5)
            if (x >= strips[i] && x <= strips[i + 2] && y >= strips[i + 1] && y <= strips[i + 3])
                return true;
        return false;
    }

    /// <summary>The first floor in <paramref name="order"/> whose fill covers (x, y), or -1 (off every floor shown).</summary>
    public static int FloorAt(float x, float y, float[]?[] fillsByLayer, ReadOnlySpan<int> order)
    {
        foreach (int layer in order)
            if (layer >= 0 && layer < fillsByLayer.Length && InFill(x, y, fillsByLayer[layer]))
                return layer;
        return -1;
    }

    /// <summary>The bounding box of every floor's fill (min x, min y, max x, max y); false when there are none.</summary>
    public static bool FillBounds(float[]?[] fillsByLayer, out Vector4 bounds)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (float[]? s in fillsByLayer)
        {
            if (s == null) continue;
            for (int i = 0; i + 4 < s.Length; i += 5)
            {
                minX = MathF.Min(minX, s[i]); minY = MathF.Min(minY, s[i + 1]);
                maxX = MathF.Max(maxX, s[i + 2]); maxY = MathF.Max(maxY, s[i + 3]);
            }
        }
        bounds = new Vector4(minX, minY, maxX, maxY);
        return minX <= maxX;
    }

    /// <summary>The index of the entry nearest <paramref name="mouse"/> within <paramref name="radius"/> pixels, or -1.</summary>
    public static int Pick(Vector2 mouse, IReadOnlyList<(int Idx, Vector2 At)> drawn, float radius)
    {
        int best = -1;
        float bestD = radius * radius;
        for (int i = 0; i < drawn.Count; i++)
        {
            float d = Vector2.DistanceSquared(mouse, drawn[i].At);
            if (d <= bestD) { bestD = d; best = drawn[i].Idx; }
        }
        return best;
    }
}
