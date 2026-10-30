// ============================================================================
//  RynthCore.Engine - UI/Data/ArgbOpacity.cs
//  An AARRGGBB colour's alpha as an opacity percentage, for the Vision face's
//  overlay opacity sliders (slopes, water, radar ring). The opacity lives in the
//  colour the RynthVision plugin already saves, so the slider needs no new
//  setting and no plugin change: moving it rewrites the colour's alpha byte.
//
//  Percent is a float so an untouched value round-trips exactly
//  (0x60 -> 37.65 % -> 0x60); only a slider move changes the byte.
// ============================================================================

using System;

namespace RynthCore.Engine.UI.Data;

internal static class ArgbOpacity
{
    /// <summary>The colour's alpha as 0..100 %.</summary>
    public static float Percent(uint argb) => (argb >> 24) * 100f / 255f;

    /// <summary>The colour with its alpha set from <paramref name="percent"/> (clamped to 0..100); RGB kept.</summary>
    public static uint WithPercent(uint argb, float percent)
    {
        float p = float.IsNaN(percent) ? 0f : Math.Clamp(percent, 0f, 100f);
        uint a = (uint)Math.Clamp((int)MathF.Round(p * 255f / 100f), 0, 255);
        return (argb & 0x00FFFFFFu) | (a << 24);
    }
}
