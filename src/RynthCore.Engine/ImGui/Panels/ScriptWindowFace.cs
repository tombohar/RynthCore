// ============================================================================
//  RynthCore.Engine - ImGui/Panels/ScriptWindowFace.cs
//  Script windows (API v71): the panel face of one window a plugin submitted
//  (UI/ScriptWindows). Hosted by ImGuiPanelHost like any panel (frame, saved
//  placement, error isolation, input capture); Draw is the replay.
//  Design: RynthSuite Docs/RYNTHLUA_WINDOWS_DESIGN.md §3, §2.5 (Geometry).
//
//  Before the replay, inside the host's body child, the face reports the
//  window's geometry (position, size, content region, cursor start, focused,
//  hovered) in logical units at UI scale 1, as a Geometry event when a value
//  moved by more than half a unit or a flag changed.
// ============================================================================

using System;
using System.Numerics;
using ImGuiNET;
using RynthCore.Engine.UI.ScriptWindows;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal sealed unsafe class ScriptWindowFace : IImGuiPanel
{
    private const float GeometryEpsilon = 0.5f;

    private readonly ScriptWindow _window;

    public ScriptWindowFace(ScriptWindow window) => _window = window;

    public bool ClickThrough => (_window.Flags & WindowFlags.ClickThrough) != 0;

    /// <summary>
    /// Opened. Unless the owner asked for it (ScriptWindow.ExpectOpen), the player
    /// opened it (bar, /rc ui open): the owner hears it once so its own visible flag
    /// follows, and its flag stays stale until it has seen this event (§3.3). AC thread.
    /// </summary>
    public void OnShown() => NoteVisibility(visible: true);

    /// <summary>Closed: as OnShown; a close the owner didn't ask for is the player's X unless the bar said otherwise.</summary>
    public void OnHidden()
    {
        // Leaving Oracle hides the Skills embed (its face's OnHidden runs once, releasing the
        // Progression subscription) so a later non-Oracle script window never inherits ActiveTab.
        if (OracleSkillsEmbed.IsOraclePanel(_window.PanelKey))
            OracleSkillsEmbed.Clear();
        NoteVisibility(visible: false);
    }

    private void NoteVisibility(bool visible)
    {
        ScriptWindow w = _window;
        bool expected;
        if (visible) { expected = w.ExpectOpen; w.ExpectOpen = false; }
        else { expected = w.ExpectClose; w.ExpectClose = false; }
        int reason = w.PendingReason;
        w.PendingReason = 0;
        if (w.Removed || expected) return;
        byte why = reason != 0 ? (byte)reason
            : visible ? VisibilityReason.BarOrCommand : VisibilityReason.PlayerClose;
        w.PlayerVisibilitySeq = w.Owner.Events.Visibility(w.Hash, visible, why);
    }

    public void Draw()
    {
        ImGuiNET.ImGui.PushFont(ImGuiFonts.Get(UiFont.Ui11));
        try
        {
            ScriptWindowMetrics.Refresh();
            ReportGeometry();
            ScriptWindowReplay.Draw(_window);
            // Oracle Skills / Attributes / Progress: after the script tab strip, draw the
            // engine SkillsFace body (raise/train/progression) into the remaining space.
            if (OracleSkillsEmbed.IsOraclePanel(_window.PanelKey))
            {
                int tab = OracleSkillsEmbed.ActiveTab;
                if (tab >= 0) SkillsFace.DrawEmbedded(tab);
            }
        }
        finally
        {
            ImGuiNET.ImGui.PopFont();
        }
    }

    /// <summary>Queues a Geometry event when the body's geometry or focus/hover changed. AC thread.</summary>
    private void ReportGeometry()
    {
        float s = EngineFrameController.FontScale;
        if (!(s > 0f) || !float.IsFinite(s)) s = 1f;
        Vector2 pos, size, avail, cursor;
        ImGuiNative.igGetWindowPos(&pos);
        ImGuiNative.igGetWindowSize(&size);
        ImGuiNative.igGetContentRegionAvail(&avail);
        ImGuiNative.igGetCursorPos(&cursor);
        uint flags = 0;
        if (ImGuiNative.igIsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) != 0) flags |= ScriptGeometry.Focused;
        if (ImGuiNative.igIsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows) != 0) flags |= ScriptGeometry.Hovered;
        var g = new ScriptGeometry
        {
            Pos = pos / s,
            Size = size / s,
            Avail = avail / s,
            Cursor = cursor / s,
            Flags = flags,
        };
        ScriptWindow w = _window;
        if (w.GeometrySent && g.Flags == w.LastGeometry.Flags
            && !Moved(g.Pos, w.LastGeometry.Pos) && !Moved(g.Size, w.LastGeometry.Size)
            && !Moved(g.Avail, w.LastGeometry.Avail) && !Moved(g.Cursor, w.LastGeometry.Cursor))
            return;
        w.LastGeometry = g;
        w.GeometrySent = true;
        w.Owner.Events.Geometry(w.Hash, g);
    }

    private static bool Moved(Vector2 a, Vector2 b) =>
        MathF.Abs(a.X - b.X) > GeometryEpsilon || MathF.Abs(a.Y - b.Y) > GeometryEpsilon;
}
