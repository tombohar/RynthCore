// ============================================================================
//  RynthCore.Engine - Compatibility/AcCellSight.cs
//
//  What AC's own renderer knows about indoor visibility, read out of its cells:
//  which EnvCells (dungeon and building interior cells) it drew this frame,
//  whether a cell can see outside, the cell's visible-cell list and its doorways
//  to the outside. The nameplates (ImGui/MonsterHud/PlateSight) use it to hide
//  a plate or a name label for an object behind building and dungeon walls
//  (2026-11-04, problem report R-20261105-020337-67aa: "NPC and Mob labels show
//  up through walls", and in a town the labels caught the clicks meant for the
//  shop). Read only: nothing here writes AC memory or calls into AC.
//
//  Evidence (AC client decompile, C:\Projects\GhidraTools\decompiled; our client,
//  4,841,472 bytes, both copies; tools\plate_sight_check.py checks every fact):
//    Draw stamps. RenderDevice* lives in a global (0x00870340 here); its +0xB0 is
//      a frame stamp. PView::DrawCells (0x005A5950) adds 1 to it, then draws each
//      cell of the portal walk it just built (PView::ConstructView: the cells seen
//      through portals from the viewer, clipped) with RenderDeviceD3D::DrawEnvCell
//      (0x005A0280), which marks the cell with CEnvCell::SetDrawnThisFrame
//      (0x0052CCF0: cell+0x130 = stamp). RenderDeviceD3D::Flip (0x005A0F60, after
//      Present) adds 1 more. DrawCells runs from PView::DrawInside (the viewer is
//      indoors) and PView::DrawPortal (a building's doorway seen from outdoors),
//      so the stamp moves several times a frame: a cell AC drew since the stamp
//      we sampled one or two frames ago carries a newer stamp than that sample.
//      CEnvCell::GetDrawnThisFrame (0x0052CCD0) is the pattern below: it compares
//      cell+0x130 with [global]+0xB0, so the one match pins the global and both
//      offsets at once.
//    The cell an object is in: CPhysicsObj::enter_cell (0x005119A0) stores the
//      CObjCell* at CPhysicsObj+0x90 and the cell's id (cell+0x28, the DBObj id)
//      at +0x4C (m_position.objcell_id); leave_cell / change_cell clear both
//      (ClientObjectHooks.TryGetObjectCell reads them).
//    The viewer's cell: SmartBox+0x08 is the camera's Position (GameMatrixCapture
//      reads it; +0x0C its cell id) and +0x50 the CObjCell* AC renders from:
//      SmartBox::RenderNormalMode (0x00453B40) tests +0x0C < 0x100 (outdoors) and
//      [+0x50]+0xE8, then hands +0x50 to the device's DrawInside.
//    Sees outside: CObjCell+0xE8 (seen_outside; CEnvCell::UnPack 0x0052E0D0 sets
//      it from the dat flags & 1). CellManager::ChangePosition (0x00455AD0)
//      releases the whole landscape (LScape::release_all) for a cell without it:
//      from a dungeon cell AC draws nothing outdoors.
//    Visible cells: CObjCell+0xE0 count, +0xE4 uint[] of full cell ids (UnPack:
//      each dat ushort | the landblock); PView::DrawInside hands exactly these to
//      PView::add_views, so no cell outside the list is ever drawn from this one.
//    Doorways: CEnvCell+0x108 portal count (UnPack reads a byte), +0x10C the
//      CCellPortal array, 0x18 each: +0 other cell id (0xFFFFFFFF = outdoors,
//      CCellPortal::UnPack 0x0053C810), +4 the other CEnvCell* (filled by
//      PView::ClipPortals as the walk reaches it), +8 the CPolygon*. CPolygon:
//      +0 CVertex**, +0xE byte point count (D3DPolyRender::DrawPortalPolyInternal
//      0x0059CCA0); a CVertex starts with x, y, z in the cell's frame. The cell's
//      Position is at +0x54: +0x58 id, frame +0x5C (qw, qx, qy, qz; +0x90 the
//      origin, landblock-local), as CellManager::ChangePosition copies it.
//
//  Threads: AC's main thread only (EndScene and the MonsterHud update run there);
//  every read is probed first (MemoryProbe), a failed read is "unknown", never a
//  guess. No fixed address is used: no pattern match = no draw stamps (logged
//  once) and PlateSight falls back to the cell lists.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace RynthCore.Engine.Compatibility;

internal static unsafe class AcCellSight
{
    // CEnvCell::GetDrawnThisFrame on the 4,841,472-byte client. Pattern is the source of truth.
    private const int GetDrawnThisFrameFallbackVa = 0x0052CCD0;

    // mov eax,[ecx+130h] (the cell's draw stamp); mov ecx,[render device global];
    // cmp eax,[ecx+0B0h] (the device's frame stamp); sete al; ret.
    // Unique in the scanned window on both client copies (C:\Turbine and
    // C:\Games\RynthCore\AcClient, 2026-11-04); CPhysicsPart's twin (0x0050DFA0) reads +0DCh.
    // The global (bytes 8-11) is wildcarded and read back from the match.
    private static readonly byte?[] GetDrawnThisFramePattern =
    [
        0x8B, 0x81, 0x30, 0x01, 0x00, 0x00,
        0x8B, 0x0D, null, null, null, null,
        0x3B, 0x81, 0xB0, 0x00, 0x00, 0x00,
        0x0F, 0x94, 0xC0,
        0xC3,
    ];
    private const int PatternGlobalOperand = 8;

    // ── Layout (see the header for where each comes from) ────────────────
    private const int DeviceFrameStamp = 0xB0;
    internal const int CellId = 0x28;
    private const int CellFrameQw = 0x5C;        // qw, qx, qy, qz
    private const int CellFrameOrigin = 0x90;    // x, y, z (landblock-local)
    private const int CellNumStabs = 0xE0;
    private const int CellStabList = 0xE4;
    private const int CellSeenOutside = 0xE8;
    private const int EnvCellNumPortals = 0x108;
    private const int EnvCellPortals = 0x10C;
    private const int EnvCellDrawStamp = 0x130;
    private const int PortalSize = 0x18;
    private const int PortalOtherId = 0x00;
    private const int PortalOtherPtr = 0x04;
    private const int PortalPolygon = 0x08;
    private const int PolygonVertices = 0x00;
    private const int PolygonNumPts = 0x0E;
    private const int SmartBoxViewerCellId = 0x0C;
    private const int SmartBoxViewerCell = 0x50;

    /// <summary>CCellPortal other cell id of a doorway to the outside.</summary>
    public const uint OutdoorsId = 0xFFFFFFFF;

    // Sanity caps: a real cell has a handful of portals and at most a few hundred visible cells.
    private const int MaxPortals = 64, MaxStabs = 1024, MaxPolygonPoints = 32;

    private static int _initialized;
    private static IntPtr _deviceGlobal;   // address of the RenderDevice* global (0 = not resolved)
    private static string _status = "not initialised";
    private static int _stampFailLogged;

    /// <summary>The draw stamps can be read (the pattern matched).</summary>
    public static bool StampsAvailable => _deviceGlobal != IntPtr.Zero;

    /// <summary>One line for "/rv plates status".</summary>
    public static string Status => _status;

    /// <summary>InitWorker step: finds the render-device global from CEnvCell::GetDrawnThisFrame. Idempotent.</summary>
    public static void Initialize()
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0)
            return;
        try
        {
            if (!AcClientModule.TryReadTextSection(out AcClientTextSection text))
            {
                _status = "acclient.exe not available";
                RynthLog.Compat("AcCellSight: acclient.exe not available - nameplates use the cell lists only.");
                return;
            }
            HookResolver.ResolveResult r = HookResolver.Resolve(text, "AcCellSight.CEnvCell::GetDrawnThisFrame", GetDrawnThisFramePattern, GetDrawnThisFrameFallbackVa);
            // Only a real match: a fallback VA on another build could read some other global as the stamp.
            if (r.Source != HookResolver.ResolveSource.PatternScan)
            {
                _status = "draw stamps unavailable (pattern not found)";
                RynthLog.Compat("AcCellSight: CEnvCell::GetDrawnThisFrame not found - nameplates use the cell lists only.");
                return;
            }
            int off = r.Address.ToInt32() - text.TextBaseVa + PatternGlobalOperand;
            int global = BitConverter.ToInt32(text.Bytes, off);
            long moduleBase = text.ModuleBase.ToInt64();
            if (global < moduleBase || global >= moduleBase + text.ImageSize)
            {
                _status = $"draw stamps unavailable (global 0x{global:X8} outside the client)";
                RynthLog.Compat($"AcCellSight: render device global 0x{global:X8} is outside acclient.exe - nameplates use the cell lists only.");
                return;
            }
            _deviceGlobal = new IntPtr(global);
            _status = $"draw stamps from the render device at [0x{global:X8}]+0x{DeviceFrameStamp:X}";
            RynthLog.Compat($"AcCellSight: ready (render device global 0x{global:X8}, CEnvCell::GetDrawnThisFrame @ 0x{r.Address.ToInt32():X8}).");
        }
        catch (Exception ex)
        {
            _deviceGlobal = IntPtr.Zero;
            _status = $"draw stamps unavailable ({ex.GetType().Name})";
            RynthLog.Compat($"AcCellSight: init failed - {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>The render device's frame stamp now. False when it can't be read (logged once).</summary>
    public static bool TryReadFrameStamp(out uint stamp)
    {
        stamp = 0;
        IntPtr g = _deviceGlobal;
        if (g == IntPtr.Zero || !MainThreadGuard.IsOnMainThread()) return false;
        if (!MemoryProbe.IsReadable(g, 4)) return Fail("the render device global is unreadable");
        IntPtr dev = *(IntPtr*)g;
        if (dev == IntPtr.Zero) return false;   // no device yet (login): not worth a log line
        if (!MemoryProbe.IsReadable(dev + DeviceFrameStamp, 4)) return Fail("the render device is unreadable");
        stamp = *(uint*)(dev + DeviceFrameStamp);
        return true;
    }

    private static bool Fail(string why)
    {
        if (Interlocked.Exchange(ref _stampFailLogged, 1) == 0)
            RynthLog.Compat($"AcCellSight: {why} - nameplates use the cell lists only while it lasts.");
        return false;
    }

    /// <summary>The cell AC renders the world from (the camera's): its id and CObjCell*. Main thread.</summary>
    public static bool TryGetViewer(out uint cellId, out IntPtr cell)
    {
        cellId = 0;
        cell = IntPtr.Zero;
        if (!MainThreadGuard.IsOnMainThread()) return false;
        if (!SmartBoxLocator.TryGetSmartBox(out IntPtr sb, out _, out _)) return false;
        if (!MemoryProbe.IsReadable(sb + SmartBoxViewerCellId, SmartBoxViewerCell + 4 - SmartBoxViewerCellId)) return false;
        cellId = *(uint*)(sb + SmartBoxViewerCellId);
        cell = *(IntPtr*)(sb + SmartBoxViewerCell);
        if ((cellId >> 16) == 0) return false;
        // The pointer must be that cell (it lags a frame across a cell change; outdoors it is a land cell).
        if (cell != IntPtr.Zero && !IsCell(cell, cellId)) cell = IntPtr.Zero;
        return true;
    }

    /// <summary>A CObjCell* that is readable and holds <paramref name="cellId"/>.</summary>
    public static bool IsCell(IntPtr cell, uint cellId) =>
        cell != IntPtr.Zero && MemoryProbe.IsReadable(cell + CellId, 4) && *(uint*)(cell + CellId) == cellId;

    /// <summary>The cell's seen_outside flag (outdoor land cells are outside). Null when unreadable.</summary>
    public static bool? SeesOutside(IntPtr cell, uint cellId)
    {
        if ((cellId & 0xFFFF) < 0x100) return true;
        if (!IsCell(cell, cellId) || !MemoryProbe.IsReadable(cell + CellSeenOutside, 4)) return null;
        return *(int*)(cell + CellSeenOutside) != 0;
    }

    /// <summary>An EnvCell's draw stamp (cell+0x130). False when unreadable or not an EnvCell.</summary>
    public static bool TryReadDrawStamp(IntPtr cell, uint cellId, out uint stamp)
    {
        stamp = 0;
        if ((cellId & 0xFFFF) < 0x100 || !IsCell(cell, cellId)) return false;
        if (!MemoryProbe.IsReadable(cell + EnvCellDrawStamp, 4)) return false;
        stamp = *(uint*)(cell + EnvCellDrawStamp);
        return true;
    }

    /// <summary>
    /// The cell's visible-cell list (full ids) into <paramref name="into"/>; the count, or -1 when
    /// it can't be read (or doesn't fit).
    /// </summary>
    public static int ReadVisibleCells(IntPtr cell, uint cellId, uint[] into)
    {
        if ((cellId & 0xFFFF) < 0x100 || !IsCell(cell, cellId)) return -1;
        if (!MemoryProbe.IsReadable(cell + CellNumStabs, 8)) return -1;
        int n = *(int*)(cell + CellNumStabs);
        IntPtr list = *(IntPtr*)(cell + CellStabList);
        if (n == 0) return 0;
        if (n < 0 || n > MaxStabs || n > into.Length || list == IntPtr.Zero || !MemoryProbe.IsReadable(list, n * 4)) return -1;
        uint* p = (uint*)list;
        for (int i = 0; i < n; i++) into[i] = p[i];
        return n;
    }

    /// <summary>One CCellPortal of an EnvCell.</summary>
    public struct PortalRead
    {
        public uint OtherId;        // OutdoorsId: a doorway to the outside
        public IntPtr OtherCell;    // 0 until AC's portal walk reached it
        /// <summary>Outside doorways only: the polygon, landblock-local (x east, y north, z up). Null when unreadable.</summary>
        public Vector3[]? World;
    }

    /// <summary>
    /// An EnvCell's portals into <paramref name="into"/> (appended), with the outside doorways' polygons
    /// moved into the landblock's frame. False when the cell or its portal array can't be read.
    /// Allocates for the polygons: call it when the viewer changes cell, not every frame.
    /// </summary>
    public static bool ReadPortals(IntPtr cell, uint cellId, List<PortalRead> into)
    {
        if ((cellId & 0xFFFF) < 0x100 || !IsCell(cell, cellId)) return false;
        if (!MemoryProbe.IsReadable(cell + EnvCellNumPortals, 8) || !MemoryProbe.IsReadable(cell + CellFrameQw, CellFrameOrigin + 12 - CellFrameQw))
            return false;
        int n = *(int*)(cell + EnvCellNumPortals);
        IntPtr arr = *(IntPtr*)(cell + EnvCellPortals);
        if (n == 0) return true;
        if (n < 0 || n > MaxPortals || arr == IntPtr.Zero || !MemoryProbe.IsReadable(arr, n * PortalSize)) return false;

        float* q = (float*)(cell + CellFrameQw);
        float* o = (float*)(cell + CellFrameOrigin);
        var rot = new Quaternion(q[1], q[2], q[3], q[0]);
        var origin = new Vector3(o[0], o[1], o[2]);
        for (int i = 0; i < n; i++)
        {
            IntPtr e = arr + i * PortalSize;
            var pr = new PortalRead { OtherId = *(uint*)(e + PortalOtherId), OtherCell = *(IntPtr*)(e + PortalOtherPtr) };
            if (pr.OtherId == OutdoorsId) pr.World = ReadPolygon(*(IntPtr*)(e + PortalPolygon), rot, origin);
            into.Add(pr);
        }
        return true;
    }

    private static Vector3[]? ReadPolygon(IntPtr poly, Quaternion rot, Vector3 origin)
    {
        if (poly == IntPtr.Zero || !MemoryProbe.IsReadable(poly, PolygonNumPts + 1)) return null;
        int n = *(byte*)(poly + PolygonNumPts);
        IntPtr verts = *(IntPtr*)(poly + PolygonVertices);
        if (n < 3 || n > MaxPolygonPoints || verts == IntPtr.Zero || !MemoryProbe.IsReadable(verts, n * 4)) return null;
        var world = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            IntPtr v = *(IntPtr*)(verts + i * 4);
            if (v == IntPtr.Zero || !MemoryProbe.IsReadable(v, 12)) return null;
            float* f = (float*)v;
            if (!float.IsFinite(f[0]) || !float.IsFinite(f[1]) || !float.IsFinite(f[2])) return null;
            world[i] = Vector3.Transform(new Vector3(f[0], f[1], f[2]), rot) + origin;
        }
        return world;
    }
}
