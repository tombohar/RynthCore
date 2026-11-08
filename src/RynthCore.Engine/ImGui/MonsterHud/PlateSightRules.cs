// ============================================================================
//  RynthCore.Engine - ImGui/MonsterHud/PlateSightRules.cs
//  The pure half of PlateSight (2026-11-04, problem report
//  R-20261105-020337-67aa: "NPC and Mob labels show up through walls", and in a
//  town the labels caught the clicks meant for the shop): given what was read
//  out of AC's cells, can the player see this object, or is it behind building
//  or dungeon walls? No AC memory, no engine state: tools/PlateSightTests
//  compiles this file in and checks every case offline.
//
//  Cells: an id's low word is the cell in its landblock; 0x0001-0x0040 are the
//  outdoor land cells, 0x0100 and up the EnvCells (dungeon and building rooms).
//  "The viewer" is the cell AC renders from (the camera's; SmartBox's viewer
//  cell), which is what decides what is on screen.
//
//  Order of the answers, best first (AcCellSight.cs has the evidence):
//    1. AC's own draw stamps: an EnvCell AC drew this frame is on screen through
//       the portals from the viewer; one it didn't draw is behind walls. Used only
//       for objects that project onto the screen: AC also skips cells outside the
//       view, and a plate pinned to the screen edge ("Keep on screen") must not
//       vanish just because its room is off to the side.
//    2. AC's doorways: from inside, an outdoor object counts only where a doorway
//       to the outside that AC drew covers it on screen.
//    3. The cell lists: from an EnvCell, only its visible-cell list (and itself)
//       can ever be drawn, and the outdoors only when it sees outside; from
//       outdoors, an EnvCell that doesn't see outside (a dungeon, a sealed room)
//       can't be seen.
//  Anything unknown keeps today's behaviour: the plate shows.
// ============================================================================

using System;

namespace RynthCore.Engine.ImGuiBackend.Hud;

/// <summary>A yes / no that may not be known.</summary>
internal enum SightFact : byte { Unknown, No, Yes }

/// <summary>What is known about one object this frame (or this scan, with less filled in).</summary>
internal ref struct SightQuery
{
    /// <summary>The cell AC renders from (0: unknown).</summary>
    public uint ViewerCell;
    /// <summary>The player's own cell (0: unknown); objects in it always count.</summary>
    public uint PlayerCell;
    /// <summary>The viewer cell's seen_outside (outdoors: Yes).</summary>
    public SightFact ViewerSeesOutside;
    /// <summary>The viewer cell's visible-cell list; only read when <see cref="VisibleKnown"/>.</summary>
    public ReadOnlySpan<uint> ViewerVisible;
    public bool VisibleKnown;

    /// <summary>The object's cell (0: unknown).</summary>
    public uint ObjectCell;
    /// <summary>The object's head or feet projects inside the screen (not only pinned to its edge).</summary>
    public bool OnScreen;
    /// <summary>AC drew the object's EnvCell in the last frames (Unknown: no stamps, no cell pointer).</summary>
    public SightFact ObjectCellDrawn;
    /// <summary>The object's EnvCell sees outside (Unknown: not read).</summary>
    public SightFact ObjectSeesOutside;
    /// <summary>An outdoor object seen from inside: a doorway AC drew covers it on screen (Unknown: not worked out).</summary>
    public SightFact InDoorway;
}

internal static class PlateSightRules
{
    /// <summary>A cell id is an EnvCell (dungeon / building room): low word 0x0100 and up.</summary>
    public static bool IsIndoor(uint cellId) => (cellId & 0xFFFF) >= 0x0100;

    /// <summary>Does the plate or label for this object show?</summary>
    public static bool Show(in SightQuery q)
    {
        // Nothing to go on: today's behaviour.
        if ((q.ViewerCell >> 16) == 0 || (q.ObjectCell >> 16) == 0) return true;
        if (q.ObjectCell == q.ViewerCell || q.ObjectCell == q.PlayerCell) return true;

        bool viewerIndoor = IsIndoor(q.ViewerCell);
        if (IsIndoor(q.ObjectCell))
        {
            // 1. AC drew the object's room this frame, or it didn't.
            if (q.OnScreen && q.ObjectCellDrawn != SightFact.Unknown) return q.ObjectCellDrawn == SightFact.Yes;
            // 3. The cell lists.
            if (viewerIndoor) return !q.VisibleKnown || Contains(q.ViewerVisible, q.ObjectCell);
            return q.ObjectSeesOutside != SightFact.No;
        }

        // An outdoor object.
        if (!viewerIndoor) return true;                       // both outdoors: no change (no terrain or building occlusion)
        if (q.ViewerSeesOutside == SightFact.No) return false;     // a dungeon: AC draws nothing outdoors from here
        if (q.OnScreen && q.InDoorway != SightFact.Unknown) return q.InDoorway == SightFact.Yes;
        return true;
    }

    /// <summary>The 4 Hz scan's question: the cell lists only (no frame, no screen).</summary>
    public static bool ShowCoarse(uint viewerCell, uint playerCell, SightFact viewerSeesOutside, ReadOnlySpan<uint> viewerVisible,
        bool visibleKnown, uint objectCell)
    {
        var q = new SightQuery
        {
            ViewerCell = viewerCell, PlayerCell = playerCell, ViewerSeesOutside = viewerSeesOutside,
            ViewerVisible = viewerVisible, VisibleKnown = visibleKnown, ObjectCell = objectCell,
        };
        return Show(in q);
    }

    /// <summary>A stamp written at or after the sample <paramref name="since"/> was taken (wraps).</summary>
    public static bool DrawnSince(uint cellStamp, uint since) => unchecked((int)(cellStamp - since)) > 0;

    /// <summary>Two screen rects (x0, y0, x1, y1) overlap, the second grown by <paramref name="margin"/>.</summary>
    public static bool Overlaps(float ax0, float ay0, float ax1, float ay1, float bx0, float by0, float bx1, float by1, float margin) =>
        ax0 <= bx1 + margin && ax1 >= bx0 - margin && ay0 <= by1 + margin && ay1 >= by0 - margin;

    private static bool Contains(ReadOnlySpan<uint> cells, uint cell)
    {
        foreach (uint c in cells)
            if (c == cell) return true;
        return false;
    }
}
