// ============================================================================
//  RynthCore.Engine - ImGui/Panels/RadarWheel.cs
//  Whether the mouse wheel belongs to a click-through radar (2026-10-29). Docked
//  click-through makes the radar's window take no input, so ImGui never sees it
//  hovered and the wheel went to the game (AC's camera zoom) instead of zooming the
//  radar. With "Mouse wheel still zooms" on, a wheel over the radar's picture is the
//  radar's: RadarFace zooms, and the engine keeps that wheel from the game
//  (IImGuiPanel.ClickThroughWantsWheel, Win32Backend). Clicks and moves still go
//  through to the game.
//
//  Pure (no ImGui), so tools\RadarSettingsTests links it.
// ============================================================================

using System;
using System.Numerics;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal static class RadarWheel
{
    public const float MinZoom = 0.5f, MaxZoom = 6.0f, StepPerNotch = 0.15f;

    /// <summary>
    /// The wheel is the click-through radar's: the option is on, click-through holds the window this
    /// frame (docked; Ctrl or a dot under the mouse makes it interactive, and then the wheel is its
    /// own anyway), no other RynthCore window has the mouse, and the mouse is on the radar's picture
    /// (inside the circle for the round radar).
    /// </summary>
    public static bool Owns(bool optionOn, bool clickThroughNow, bool poppedOut, bool uiWantsMouse,
        Vector2 mouse, Vector2 min, Vector2 max, RoundMask? round)
    {
        if (!optionOn || !clickThroughNow || poppedOut || uiWantsMouse) return false;
        if (mouse.X < min.X || mouse.Y < min.Y || mouse.X > max.X || mouse.Y > max.Y) return false;
        return round is not { } circle || circle.Contains(mouse);
    }

    /// <summary>The zoom after <paramref name="notches"/> of wheel (up = in), kept to the slider's range.</summary>
    public static float Zoomed(float zoom, float notches) => Math.Clamp(zoom + notches * StepPerNotch, MinZoom, MaxZoom);
}
