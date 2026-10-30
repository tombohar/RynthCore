using System;

namespace RynthCore.Engine.Compatibility;

/// <summary>
/// The engine's two heading bases. Measured on Freya 2026-10-20, standing still: the yaw
/// quaternion said 13.9 (NNE) while GetPlayerHeading said 76.1, which is 90 - 13.9.
/// <list type="bullet">
/// <item>Compass: 0 = North, clockwise. Map bearings (RynthNavAtlas.Bearing, the Sense and
/// Characters rows), TurnToHeading (SetPlayerHeadingDirect writes the quaternion this way)
/// and every nav and combat steer, which read the quaternion.</item>
/// <item>GetPlayerHeading (AC's get_heading, a compass value, through NormalizeDecalHeading):
/// 0 = East, counter-clockwise. The radar and world map draw with it as that.</item>
/// </list>
/// Compare a bearing with <see cref="CompassFromYaw"/>, never with GetPlayerHeading.
/// </summary>
internal static class HeadingMath
{
    /// <summary>The compass heading of a yaw-only quaternion (AC's physics yaw is counter-clockwise from North).</summary>
    public static float CompassFromYaw(float qw, float qz)
    {
        double yaw = 2.0 * Math.Atan2(qz, qw) * (180.0 / Math.PI);
        return (float)((-yaw % 360.0 + 720.0) % 360.0);
    }
}
