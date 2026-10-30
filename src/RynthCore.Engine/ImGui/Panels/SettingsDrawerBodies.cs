// ============================================================================
//  RynthCore.Engine - ImGui/Panels/SettingsDrawerBodies.cs
//  The dashboard drawers that show Settings rows, drawn into RynthAi's own
//  dashboard window (2026-10-21, plugin API v90 RenderSettingsDrawer). RynthAi
//  draws its dashboard itself now, its drawers' tabs and frames included, but
//  three drawers show the Settings panel's rows, and the Settings panel is still
//  RynthCore's: until it moves to RynthAi, RynthAi's drawer windows ask for
//  these bodies here (the frame and the title row are RynthAi's):
//    ranges   the selected monster and the key ranges (RangesSlideOut.cs)
//    patrol   Start patrol, the Dungeon Patrol settings, this dungeon's hazard
//             cells, the recorded hazards and the saved routes (the Patrol
//             drawer, RynthAiFace.Drawers.cs)
//    pinned   My settings, the rows the player pinned (QuickSettingsDrawer.cs)
//  Each is the same drawer the engine's own dashboard shows, so a value changed
//  here is saved the same way and a pin made here is the same pin. A drawer
//  not drawn for 2 s drops its hub subscriptions (EndFrame). Not "this frame":
//  a popped-out dashboard draws at its own rate (30 Hz at most, 10 idle), so a
//  drawer there missed most game frames, let its sources go each time and came
//  back empty (2026-10-24).
//
//  2026-10-27: RynthVision draws the whole Vision window itself (it takes over
//  the engine's VisionFace, "takeOver"), but two of its tabs set things that are
//  still RynthCore's: the nameplates and the combat text (ImGui/MonsterHud,
//  MonsterHudSettings) are drawn by the engine until they move to RynthVision.
//  So RynthVision's window asks for those two tab bodies here, the same rows the
//  engine's Vision panel shows (MonsterHudSettingsUi), saved the same way:
//    nameplates   the "Nameplates" tab
//    combattext   the "Combat text" tab
//  They draw popped out too (the engine's Vision panel did), as the three
//  drawers above do since 2026-10-24.
//
//  AC thread only (inside RynthAi's or RynthVision's window, and EndFrame after
//  the panels).
// ============================================================================

using System;
using System.Numerics;
using RynthCore.Engine.UI;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal static class SettingsDrawerBodies
{
    private static RangesSlideOut? _ranges;
    private static RynthAiFace.PatrolDrawer? _patrol;
    private static QuickSettingsDrawer? _pinned;

    // When each was last drawn (Environment.TickCount64).
    private static long _rangesDrawn = long.MinValue / 2, _patrolDrawn = long.MinValue / 2, _pinnedDrawn = long.MinValue / 2;
    private const long KeepMs = 2000;

    /// <summary>
    /// Draws <paramref name="key"/>'s body at the cursor, <paramref name="width"/> pixels wide, in at most
    /// <paramref name="room"/> pixels of height (0: as tall as it wants); <paramref name="openPicker"/>:
    /// open My settings' list of settings to pin. Returns the height it wants (pixels), or -1 for a key
    /// that isn't one of these.
    /// </summary>
    public static float Draw(string key, float width, float room, bool openPicker)
    {
        Vector2 at = ImGuiNET.ImGui.GetCursorScreenPos();
        float w = MathF.Max(1, width);
        if (key is "nameplates" or "combattext") return DrawVisionTab(key, at, w);
        float k = MathF.Max(1f, ImGuiFonts.Get(UiFont.Dash11).FontSize / 11f);
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Dash10));
        try
        {
            switch (key)
            {
                case "ranges":
                {
                    _ranges ??= new RangesSlideOut();
                    _rangesDrawn = Environment.TickCount64;
                    _ranges.Update(true);
                    float h = RangesSlideOut.BodyHeight(k);
                    ImGuiNET.ImGui.PushID("##sdb_ranges");
                    try { _ranges.DrawBody(at.X, at.Y, w, k); }
                    finally { ImGuiNET.ImGui.PopID(); }
                    ImGuiNET.ImGui.SetCursorScreenPos(at);
                    ImGuiNET.ImGui.Dummy(new Vector2(w, h));
                    return h;
                }
                case "patrol":
                {
                    _patrol ??= new RynthAiFace.PatrolDrawer();
                    _patrolDrawn = Environment.TickCount64;
                    _patrol.Update(true);
                    RynthAiFace.UseScale(k);
                    float h = room > 0 ? room : 400 * k;
                    float want;
                    ImGuiNET.ImGui.PushID("##sdb_patrol");
                    try { want = _patrol.DrawBody(at.X, at.Y, w, h, k); }
                    finally { ImGuiNET.ImGui.PopID(); }
                    ImGuiNET.ImGui.SetCursorScreenPos(at);
                    ImGuiNET.ImGui.Dummy(new Vector2(w, h));
                    return want;
                }
                case "pinned":
                {
                    _pinned ??= new QuickSettingsDrawer();
                    _pinnedDrawn = Environment.TickCount64;
                    _pinned.Update(true);
                    float h = room > 0 ? room : 120 * k;
                    float want;
                    ImGuiNET.ImGui.PushID("##sdb_pinned");
                    try { want = _pinned.DrawBody(at.X, at.Y, w, h, k, openPicker); }
                    finally { ImGuiNET.ImGui.PopID(); }
                    ImGuiNET.ImGui.SetCursorScreenPos(at);
                    ImGuiNET.ImGui.Dummy(new Vector2(w, h));
                    return want;
                }
                default:
                    return -1f;
            }
        }
        finally
        {
            ImGuiNET.ImGui.PopFont();
        }
    }

    /// <summary>
    /// A Vision tab RynthVision's window shows (the engine's rows: MonsterHudSettingsUi), in Ui11 as the
    /// engine's Vision panel drew them. Returns the height drawn.
    /// </summary>
    private static float DrawVisionTab(string key, Vector2 at, float w)
    {
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui11));
        ImGuiNET.ImGui.PushID(key == "nameplates" ? "##sdb_plates" : "##sdb_ctext");
        try
        {
            if (key == "nameplates") Hud.MonsterHudSettingsUi.DrawNameplates(w);
            else Hud.MonsterHudSettingsUi.DrawCombatText(w);
        }
        finally
        {
            ImGuiNET.ImGui.PopID();
            ImGuiNET.ImGui.PopFont();
        }
        return MathF.Max(0f, ImGuiNET.ImGui.GetCursorScreenPos().Y - at.Y);
    }

    /// <summary>After the panels each frame: a body not drawn for <see cref="KeepMs"/> lets its hub sources go.</summary>
    public static void EndFrame()
    {
        long now = Environment.TickCount64;
        if (_ranges != null && now - _rangesDrawn > KeepMs) _ranges.Update(false);
        if (_patrol != null && now - _patrolDrawn > KeepMs) _patrol.Update(false);
        if (_pinned != null && now - _pinnedDrawn > KeepMs) _pinned.Update(false);
    }
}
