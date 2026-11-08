// ============================================================================
//  RynthCore.Engine - ImGui/Panels/RadarRangeRing.cs
//  The range rings round the player on the Radar (2026-11-06, player request): RynthAi's
//  Monster Range (how far away a monster can be for the bot to attack it) and, optionally,
//  its Ring Range (ring spells), drawn as circles at that many yards. The radar's world is
//  in yards (192 per landblock, the same units RynthAi measures in) and its zoom is pixels
//  per yard, so the ring's radius on the screen is range x zoom.
//
//  Pure (no ImGui), so tools\RadarSettingsTests links it.
// ============================================================================

using System;
using System.Numerics;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal static class RadarRangeRing
{
    /// <summary>Rings smaller than this (px) aren't drawn: they would only blot out your own arrow.</summary>
    public const float MinRadiusPx = 4f;

    /// <summary>
    /// The ring's radius in pixels for a range of <paramref name="yards"/> at the radar's
    /// <paramref name="zoom"/> (px per yard, at least 0.5 as the radar's projection keeps it).
    /// 0 for no range (0, negative or not a number).
    /// </summary>
    public static float RadiusPx(double yards, float zoom)
    {
        if (!(yards > 0) || !double.IsFinite(yards)) return 0f;
        float z = float.IsFinite(zoom) ? MathF.Max(0.5f, zoom) : 0.5f;
        return (float)yards * z;
    }

    /// <summary>
    /// Whether any of a ring of <paramref name="radius"/> round <paramref name="centre"/> (the player)
    /// shows on the picture: not too small, and inside the circle for the round radar (the two share
    /// a centre), or reaching no further than the farthest corner of the square one.
    /// </summary>
    public static bool Shows(float radius, Vector2 centre, Vector2 min, Vector2 max, RoundMask? round)
    {
        if (!(radius >= MinRadiusPx)) return false;
        if (round is { } circle) return radius <= circle.Radius;
        float fx = MathF.Max(centre.X - min.X, max.X - centre.X);
        float fy = MathF.Max(centre.Y - min.Y, max.Y - centre.Y);
        return radius * radius <= fx * fx + fy * fy;
    }
}
