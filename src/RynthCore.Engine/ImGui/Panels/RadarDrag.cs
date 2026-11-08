// ============================================================================
//  RynthCore.Engine - ImGui/Panels/RadarDrag.cs
//  Whether a press on the radar may move it (2026-11-04). Docked click-through
//  ("Docked click-through (dots still work; Ctrl for the rest)") gives the game
//  every click, except on a dot or the buttons: there the radar's window takes
//  input for that press (RadarFace._hot / _pressHot) so the dot can be hovered and
//  clicked to select. The surface under the dot is the same InvisibleButton that
//  drags the panel (ImGuiPanelHost.DragWindowWithLastItem), so holding on a dot
//  and moving the mouse moved the radar, Ctrl or not (Discord, 2026-11-02). Now a
//  press moves the radar only when click-through is off, Ctrl was held when the
//  press began, or the radar is popped out (always interactive there). It is
//  decided when the press begins and holds until the button is up, so Ctrl let go
//  or pressed mid-drag neither stops a move nor starts one.
//
//  Pure (no ImGui), so tools\RadarSettingsTests links it.
// ============================================================================

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed class RadarDrag
{
    private bool _moves = true;

    /// <summary>A press with these settings may move the radar.</summary>
    public static bool PressMoves(bool clickThroughOn, bool ctrlHeld, bool poppedOut) =>
        !clickThroughOn || ctrlHeld || poppedOut;

    /// <summary>
    /// Call once a frame right after the surface item: <paramref name="pressBegan"/> is the surface's
    /// IsItemActivated. Answers whether this press may move the radar (as decided when it began).
    /// </summary>
    public bool Update(bool pressBegan, bool clickThroughOn, bool ctrlHeld, bool poppedOut)
    {
        if (pressBegan) _moves = PressMoves(clickThroughOn, ctrlHeld, poppedOut);
        return _moves;
    }
}
