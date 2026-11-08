// ============================================================================
//  RynthCore.Engine - ImGui/MonsterHud/PlateSight.cs
//  "Behind walls" for the nameplates (2026-11-04, problem report
//  R-20261105-020337-67aa, Conquest, indoors in a town shop: "NPC and Mob
//  labels show up through walls. When in a town I find it hard to even click
//  on interface items and buying things because my mouse wants to click on the
//  labels of the NPC's."). A plate or label for an object the player can't see
//  because of building or dungeon walls no longer draws, and so never takes a
//  click; MonsterHudSettings.HideBehindWalls (default on) turns it off.
//
//  Where the answers come from (AC memory: Compatibility/AcCellSight.cs; the
//  rules: PlateSightRules.cs):
//    - The viewer: the cell AC renders from (the camera's), its seen_outside
//      flag and its visible-cell list. Read when the scan or the frame asks;
//      the list (and the doorways below) is cached until the viewer changes
//      cell.
//    - Per frame: the render device's draw stamp, sampled once a frame. An
//      EnvCell whose stamp is newer than the sample taken two frames ago was
//      drawn by AC in the last frame or two, through the portals from the viewer.
//    - Per plate, per frame: the object's own cell (its CPhysicsObj's cell) and
//      that cell's stamp and seen_outside.
//    - The doorways: the portals to the outside of the cells the viewer can see,
//      cached as landblock-local polygons; each frame those of cells AC drew are
//      projected to screen rects (only while the viewer is indoors and an outdoor
//      object asks). A doorway partly behind the camera counts as the whole screen.
//  The 4 Hz scan (MonsterHud.Snapshot) skips objects the cell lists already rule
//  out, so a town's hidden NPCs no longer use up the label cap; the frame check
//  (MonsterHud.DrawPlates) does the rest. A plate that goes out of sight fades
//  out as one leaving the screen does, and takes no click from the first frame.
//
//  Cost: one viewer read per scan and per frame, one cell read per drawn plate,
//  the cached doorways projected while they matter (a handful of points); the
//  visible-cell list and the doorway polygons are read again only when the
//  viewer changes cell (or, while some visible rooms weren't reachable yet,
//  twice a second). Anything unreadable shows the plate (today's behaviour) and
//  is logged once.
//
//  AC's main thread only. Unload-safe: engine-owned statics, no hooks.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using RynthCore.Engine.Compatibility;
using RynthCore.Engine.D3D9;

namespace RynthCore.Engine.ImGuiBackend.Hud;

internal static class PlateSight
{
    private const int MaxVisible = 1024;          // AcCellSight's cap on a visible-cell list
    private const int MaxRooms = 96;              // rooms walked for doorways
    private const int MaxDoorwayRects = 24;
    private static readonly long RebuildIncompleteTicks = Stopwatch.Frequency / 2;
    private static readonly long RebuildAnywayTicks = Stopwatch.Frequency * 5;
    private const float DoorwayMarginPx = 6f;

    // ── The viewer (refreshed by the scan and every frame) ───────────────
    private static uint _viewerCell, _playerCell;
    private static IntPtr _viewerPtr;
    private static SightFact _viewerSeesOutside;

    // ── Cached for one viewer cell ───────────────────────────────────────
    private static uint _cacheCell;
    private static IntPtr _cachePtr;
    private static long _cacheTicks;
    private static bool _cacheIncomplete;
    private static readonly uint[] Visible = new uint[MaxVisible];
    private static int _visibleCount = -1;         // -1: the list couldn't be read
    private struct Doorway { public IntPtr Cell; public uint CellId; public Vector3[] World; }
    private static readonly List<Doorway> Doorways = new(16);
    private static bool _doorwaysKnown;
    private static readonly List<AcCellSight.PortalRead> PortalWork = new(16);
    private static readonly Queue<(IntPtr Cell, uint Id)> RoomQueue = new(32);
    private static readonly HashSet<uint> RoomSeen = new();

    // ── Per frame ────────────────────────────────────────────────────────
    private static uint _stampPrev1, _stampPrev2;  // the device stamp one and two frames ago
    private static int _stampSamples;              // consecutive good samples (2+: _since is usable)
    private static uint _since;
    private static bool _stampsOk;
    private static int _frame;
    private static int _rectsFrame = -1;
    private static readonly List<Vector4> Rects = new(MaxDoorwayRects);
    private static SightFact _rectsState;          // Unknown: the doorways couldn't be worked out this frame
    private static int _doorwaysDrawn;
    private static int _hiddenThisFrame, _hiddenLastFrame;

    private static int _hideLogged, _viewerFailLogged;

    /// <summary>The plates in the last frame that were out of sight (diagnostics).</summary>
    public static int HiddenCount => _hiddenLastFrame;

    /// <summary>Drops every cache (logout, character switch). The next read starts again.</summary>
    public static void Reset()
    {
        _viewerCell = _playerCell = 0;
        _viewerPtr = IntPtr.Zero;
        _viewerSeesOutside = SightFact.Unknown;
        _cacheCell = 0;
        _cachePtr = IntPtr.Zero;
        _visibleCount = -1;
        Doorways.Clear();
        _doorwaysKnown = false;
        _stampSamples = 0;
        _stampsOk = false;
        _rectsFrame = -1;
        _hiddenThisFrame = _hiddenLastFrame = 0;
    }

    /// <summary>
    /// Reads the viewer (camera) cell and, when it changed, its visible-cell list and doorways.
    /// <paramref name="playerCell"/>: the player's own cell. AC's main thread.
    /// </summary>
    public static void Refresh(uint playerCell)
    {
        _playerCell = playerCell;
        if (!AcCellSight.TryGetViewer(out uint cell, out IntPtr ptr))
        {
            // No SmartBox viewer: the player's own cell stands in (no lists, no doorways from it).
            if (_viewerFailLogged == 0)
            {
                _viewerFailLogged = 1;
                RynthLog.UI("MonsterHud: can't read AC's viewer cell - plates behind walls go by your own cell only.");
            }
            cell = playerCell;
            ptr = IntPtr.Zero;
        }
        _viewerCell = cell;
        _viewerPtr = ptr;
        bool? outside = PlateSightRules.IsIndoor(cell) ? AcCellSight.SeesOutside(ptr, cell) : true;
        _viewerSeesOutside = outside == null ? SightFact.Unknown : outside.Value ? SightFact.Yes : SightFact.No;

        long now = Stopwatch.GetTimestamp();
        bool rebuild = cell != _cacheCell || ptr != _cachePtr ||
                       (_cacheIncomplete && now - _cacheTicks >= RebuildIncompleteTicks) ||
                       (PlateSightRules.IsIndoor(cell) && now - _cacheTicks >= RebuildAnywayTicks);
        if (rebuild) Rebuild(cell, ptr, now);
    }

    private static void Rebuild(uint cell, IntPtr ptr, long now)
    {
        _cacheCell = cell;
        _cachePtr = ptr;
        _cacheTicks = now;
        _cacheIncomplete = false;
        Doorways.Clear();
        _rectsFrame = -1;
        if (!PlateSightRules.IsIndoor(cell))
        {
            // Outdoors: no list and no doorways are needed.
            _visibleCount = 0;
            _doorwaysKnown = true;
            return;
        }
        _visibleCount = ptr == IntPtr.Zero ? -1 : AcCellSight.ReadVisibleCells(ptr, cell, Visible);
        if (_viewerSeesOutside == SightFact.No)
        {
            // A dungeon: AC draws nothing outdoors from here, so there are no doorways to look through.
            _doorwaysKnown = true;
            return;
        }
        _doorwaysKnown = ptr != IntPtr.Zero && _visibleCount >= 0 && WalkRooms(cell, ptr);
    }

    /// <summary>
    /// The doorways to the outside of the viewer's room and of every room it can see that AC's
    /// portal walk has reached (the portal's other-cell pointer is filled as AC walks into it).
    /// </summary>
    private static bool WalkRooms(uint cell, IntPtr ptr)
    {
        RoomQueue.Clear();
        RoomSeen.Clear();
        RoomQueue.Enqueue((ptr, cell));
        RoomSeen.Add(cell);
        int rooms = 0;
        while (RoomQueue.Count > 0 && rooms < MaxRooms)
        {
            (IntPtr c, uint id) = RoomQueue.Dequeue();
            rooms++;
            PortalWork.Clear();
            if (!AcCellSight.ReadPortals(c, id, PortalWork))
            {
                if (id == cell) return false;   // the viewer's own room unreadable: no doorway answer
                _cacheIncomplete = true;
                continue;
            }
            foreach (AcCellSight.PortalRead pr in PortalWork)
            {
                if (pr.OtherId == AcCellSight.OutdoorsId)
                {
                    if (pr.World != null) Doorways.Add(new Doorway { Cell = c, CellId = id, World = pr.World });
                    else return false;   // a doorway we can't place: no doorway answer (plates show)
                    continue;
                }
                if (!RoomSeen.Add(pr.OtherId) || !InVisible(pr.OtherId)) continue;
                if (pr.OtherCell == IntPtr.Zero || !AcCellSight.IsCell(pr.OtherCell, pr.OtherId))
                {
                    _cacheIncomplete = true;   // AC hasn't walked in yet: look again shortly
                    continue;
                }
                RoomQueue.Enqueue((pr.OtherCell, pr.OtherId));
            }
        }
        if (RoomQueue.Count > 0) _cacheIncomplete = true;
        return true;
    }

    private static bool InVisible(uint cell)
    {
        for (int i = 0; i < _visibleCount; i++)
            if (Visible[i] == cell) return true;
        return false;
    }

    private static ReadOnlySpan<uint> VisibleSpan => _visibleCount > 0 ? Visible.AsSpan(0, _visibleCount) : ReadOnlySpan<uint>.Empty;

    /// <summary>The scan's question for an object in <paramref name="objectCell"/>: the cell lists only.</summary>
    public static bool ShowCoarse(uint objectCell) =>
        PlateSightRules.ShowCoarse(_viewerCell, _playerCell, _viewerSeesOutside, VisibleSpan, _visibleCount >= 0, objectCell);

    /// <summary>Once a frame, before the plates ask: the viewer and the draw stamp.</summary>
    public static void BeginFrame(uint playerCell)
    {
        _frame++;
        _hiddenLastFrame = _hiddenThisFrame;
        _hiddenThisFrame = 0;
        Refresh(playerCell);
        if (AcCellSight.TryReadFrameStamp(out uint stamp))
        {
            _since = _stampPrev2;
            _stampsOk = _stampSamples >= 2;
            _stampPrev2 = _stampPrev1;
            _stampPrev1 = stamp;
            if (_stampSamples < 2) _stampSamples++;
        }
        else
        {
            _stampSamples = 0;
            _stampsOk = false;
        }
    }

    /// <summary>
    /// Does this object's plate or label show this frame? <paramref name="objectCell"/>: its live cell.
    /// <paramref name="headFront"/>: the head point is in front of the camera, at <paramref name="head"/>
    /// (ImGui pixels, not pinned); <paramref name="feetWx"/>.. the feet in the projection's frame
    /// (D3D axes, as Plate.LastW*).
    /// </summary>
    public static bool Show(ref MonsterHud.Frame f, uint id, uint objectCell, bool headFront, Vector2 head,
        float feetWx, float feetWy, float feetWz)
    {
        bool feetFront = GameMatrixCapture.WorldToScreen(feetWx, feetWy, feetWz, out float fsx, out float fsy);
        var feet = new Vector2(fsx * f.MapX, fsy * f.MapY);
        bool onScreen = (headFront && Inside(ref f, head)) || (feetFront && Inside(ref f, feet));

        var q = new SightQuery
        {
            ViewerCell = _viewerCell, PlayerCell = _playerCell, ViewerSeesOutside = _viewerSeesOutside,
            ViewerVisible = VisibleSpan, VisibleKnown = _visibleCount >= 0,
            ObjectCell = objectCell, OnScreen = onScreen,
        };
        bool viewerIndoor = PlateSightRules.IsIndoor(_viewerCell);
        if (PlateSightRules.IsIndoor(objectCell) && objectCell != _viewerCell && objectCell != _playerCell)
        {
            // The object's room: AC's draw stamp on it, and whether it sees outside.
            if (ClientObjectHooks.TryGetObjectCell(id, out IntPtr cell, out uint cellId) && cellId == objectCell && cell != IntPtr.Zero)
            {
                if (_stampsOk && AcCellSight.TryReadDrawStamp(cell, cellId, out uint st))
                    q.ObjectCellDrawn = PlateSightRules.DrawnSince(st, _since) ? SightFact.Yes : SightFact.No;
                bool? so = AcCellSight.SeesOutside(cell, cellId);
                q.ObjectSeesOutside = so == null ? SightFact.Unknown : so.Value ? SightFact.Yes : SightFact.No;
            }
        }
        else if (!PlateSightRules.IsIndoor(objectCell) && viewerIndoor && onScreen && _viewerSeesOutside != SightFact.No)
        {
            q.InDoorway = InDoorway(ref f, headFront ? head : feet, feetFront ? feet : head);
        }

        bool show = PlateSightRules.Show(in q);
        if (!show)
        {
            _hiddenThisFrame++;
            if (_hideLogged < 3)
            {
                _hideLogged++;
                RynthLog.UI($"MonsterHud: a plate is behind walls - hidden (viewer cell 0x{_viewerCell:X8}, sees outside {_viewerSeesOutside}, " +
                    $"object cell 0x{objectCell:X8}, on screen {onScreen}, drawn {q.ObjectCellDrawn}, doorway {q.InDoorway}, " +
                    $"{_visibleCount} visible cells, {Doorways.Count} doorways).");
            }
        }
        return show;
    }

    private static bool Inside(ref MonsterHud.Frame f, Vector2 p) =>
        p.X >= 0f && p.Y >= 0f && p.X <= f.Display.X && p.Y <= f.Display.Y;

    /// <summary>An outdoor object seen from inside: does a doorway AC drew cover its head-to-feet line on screen?</summary>
    private static SightFact InDoorway(ref MonsterHud.Frame f, Vector2 a, Vector2 b)
    {
        if (_rectsFrame != _frame) BuildRects(ref f);
        if (_rectsState != SightFact.Yes) return SightFact.Unknown;
        float x0 = MathF.Min(a.X, b.X), x1 = MathF.Max(a.X, b.X), y0 = MathF.Min(a.Y, b.Y), y1 = MathF.Max(a.Y, b.Y);
        float margin = DoorwayMarginPx * f.UiScale;
        foreach (Vector4 r in Rects)
            if (PlateSightRules.Overlaps(x0, y0, x1, y1, r.X, r.Y, r.Z, r.W, margin)) return SightFact.Yes;
        return SightFact.No;
    }

    /// <summary>This frame's screen rects of the doorways to the outside in rooms AC drew.</summary>
    private static void BuildRects(ref MonsterHud.Frame f)
    {
        _rectsFrame = _frame;
        Rects.Clear();
        _doorwaysDrawn = 0;
        if (!_stampsOk || !_doorwaysKnown) { _rectsState = SightFact.Unknown; return; }
        var full = new Vector4(0f, 0f, f.Display.X, f.Display.Y);
        foreach (Doorway d in Doorways)
        {
            if (!AcCellSight.TryReadDrawStamp(d.Cell, d.CellId, out uint st)) { _rectsState = SightFact.Unknown; return; }
            if (!PlateSightRules.DrawnSince(st, _since)) continue;   // AC didn't draw that room: its doorway isn't in view
            _doorwaysDrawn++;
            if (!MonsterHud.TryOffset(f.FrameCell, d.CellId, out float ox, out float oy)) continue;
            int front = 0, behind = 0;
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (Vector3 v in d.World)
            {
                if (!GameMatrixCapture.WorldToScreen(v.X + ox, v.Z, v.Y + oy, out float sx, out float sy)) { behind++; continue; }
                front++;
                sx *= f.MapX; sy *= f.MapY;
                x0 = MathF.Min(x0, sx); x1 = MathF.Max(x1, sx);
                y0 = MathF.Min(y0, sy); y1 = MathF.Max(y1, sy);
            }
            if (front == 0) continue;                                    // wholly behind the camera
            Vector4 r = behind > 0 ? full : new Vector4(x0, y0, x1, y1);   // straddling the camera: anywhere
            if (Rects.Count < MaxDoorwayRects) Rects.Add(r);
            else { Rects[0] = full; break; }                             // too many to keep apart: the whole screen
        }
        _rectsState = SightFact.Yes;
    }

    /// <summary>One line for "/rv plates status".</summary>
    public static string Describe()
    {
        bool indoor = PlateSightRules.IsIndoor(_viewerCell);
        return $"Behind walls: {(MonsterHudSettings.HideBehindWalls ? "hidden" : "shown (setting off)")}; " +
               $"viewer cell 0x{_viewerCell:X8} ({(_viewerCell == 0 ? "unknown" : indoor ? "indoors" : "outdoors")}, sees outside {_viewerSeesOutside}), " +
               $"{(_visibleCount < 0 ? "visible cells unreadable" : $"{_visibleCount} visible cells")}, " +
               $"{(_doorwaysKnown ? $"{Doorways.Count} doorways ({_doorwaysDrawn} drawn)" : "doorways unknown")}" +
               $"{(_cacheIncomplete ? ", some rooms not reached yet" : "")}; " +
               $"{AcCellSight.Status}{(_stampsOk ? "" : " (no stamp window yet)")}; {_hiddenLastFrame} out of sight last frame.";
    }
}
