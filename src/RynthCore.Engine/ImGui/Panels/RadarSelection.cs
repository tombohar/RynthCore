// ============================================================================
//  RynthCore.Engine - ImGui/Panels/RadarSelection.cs
//  The object selected in game, highlighted on the radar (2026-10-31, a player's
//  request: "Can the hover icon stay over whichever mob/door/etc. you have
//  selected?"). RadarFace reads AC's current selection each frame (the global the
//  SetSelectedObject hook reads, SelectedTargetHooks.ReadCurrentSelectedId: no new
//  hook) and draws a ring in its own colour round that object's marker, with the
//  hover label (name, distance, health) beside it. Off the radar's picture, a small
//  arrow on the edge points the way to it.
//
//  The rules, pure (no ImGui, no AC), so tools\RadarSettingsTests links this file:
//    - which marker is the selected one: the marker with the selected object's id;
//      none when nothing is selected or the marker has no id (RynthAi builds before
//      2026-10-01 send none);
//    - it clears when the selection clears, or the object is no longer one of the
//      radar's markers (gone, out of the client's view, an item in a pack);
//    - the selected object is shown even when its kind's show box is off (items on
//      the ground start hidden, and the player picked this one);
//    - where the edge arrow goes, and where the label sits.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using RynthCore.Engine.UI.Data;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal static class RadarSelection
{
    /// <summary>
    /// The marker of the selected object, or null: nothing selected (id 0), the object isn't one of
    /// the markers, or the markers carry no ids. A kind this engine doesn't know (a newer RynthAi's) is
    /// never highlighted: it has no shape or colour here. A List, not an interface, so the loop allocates nothing.
    /// </summary>
    public static RadarMarker? Find(List<RadarMarker>? markers, uint selectedId)
    {
        if (selectedId == 0 || markers == null) return null;
        foreach (RadarMarker m in markers)
            if (m.Id == selectedId)
                return m.Kind < RadarKind.Count ? m : null;
        return null;
    }

    /// <summary>Whether a marker is highlighted this frame: the setting is on and it is the selected object's.</summary>
    public static RadarMarker? Highlighted(bool settingOn, List<RadarMarker>? markers, uint selectedId) =>
        settingOn ? Find(markers, selectedId) : null;

    /// <summary>
    /// The edge arrow toward a marker that is off the radar's picture. <paramref name="target"/> is the
    /// marker's screen point; the picture is the box <paramref name="min"/>..<paramref name="max"/>, or the
    /// circle <paramref name="round"/> on the round radar. False when the marker is on the picture (it is
    /// drawn where it is) or is at the centre. Otherwise <paramref name="tip"/> is the arrow's point,
    /// <paramref name="inset"/> px inside the edge on the line from <paramref name="centre"/> to the marker,
    /// and <paramref name="dir"/> the unit direction it points.
    /// </summary>
    public static bool EdgeArrow(Vector2 centre, Vector2 target, Vector2 min, Vector2 max, RoundMask? round, float inset,
        out Vector2 tip, out Vector2 dir)
    {
        tip = default;
        dir = default;
        if (round is RoundMask circle)
        {
            if (circle.Contains(target)) return false;
        }
        else if (target.X >= min.X && target.X <= max.X && target.Y >= min.Y && target.Y <= max.Y) return false;

        Vector2 d = target - centre;
        float len = d.Length();
        if (!(len > 1e-4f) || !float.IsFinite(len)) return false;
        dir = d / len;
        float t;
        if (round is RoundMask c)
            t = MathF.Max(0f, c.Radius - inset);
        else
        {
            // Onto the box: the nearer of the two sides the ray meets.
            float hx = MathF.Max(0f, MathF.Min(centre.X - min.X, max.X - centre.X) - inset);
            float hy = MathF.Max(0f, MathF.Min(centre.Y - min.Y, max.Y - centre.Y) - inset);
            float tx = MathF.Abs(dir.X) > 1e-6f ? hx / MathF.Abs(dir.X) : float.MaxValue;
            float ty = MathF.Abs(dir.Y) > 1e-6f ? hy / MathF.Abs(dir.Y) : float.MaxValue;
            t = MathF.Min(tx, ty);
        }
        tip = centre + dir * t;
        return true;
    }

    /// <summary>
    /// The edge arrow's three corners: the point at <paramref name="tip"/>, the base
    /// <paramref name="length"/> px back toward the centre and <paramref name="halfWidth"/> px to each side.
    /// </summary>
    public static void ArrowCorners(Vector2 tip, Vector2 dir, float length, float halfWidth,
        out Vector2 a, out Vector2 b, out Vector2 c)
    {
        var side = new Vector2(-dir.Y, dir.X);
        Vector2 back = tip - dir * length;
        a = tip;
        b = back + side * halfWidth;
        c = back - side * halfWidth;
    }

    /// <summary>
    /// Where the label's box (<paramref name="size"/>) goes: to the right of a marker at
    /// <paramref name="anchor"/>, <paramref name="gap"/> px clear of it, vertically centred; on the left when
    /// it would run past <paramref name="boundsMax"/>; then kept inside the bounds as far as it fits.
    /// </summary>
    public static Vector2 LabelPos(Vector2 anchor, float gap, Vector2 size, Vector2 boundsMin, Vector2 boundsMax)
    {
        float x = anchor.X + gap;
        if (x + size.X > boundsMax.X) x = anchor.X - gap - size.X;
        float y = anchor.Y - size.Y * 0.5f;
        x = MathF.Max(boundsMin.X, MathF.Min(x, boundsMax.X - size.X));
        y = MathF.Max(boundsMin.Y, MathF.Min(y, boundsMax.Y - size.Y));
        return new Vector2(x, y);
    }
}
