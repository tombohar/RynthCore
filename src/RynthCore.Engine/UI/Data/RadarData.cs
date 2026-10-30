// ============================================================================
//  RynthCore.Engine - UI/Data/RadarData.cs
//  The radar's data (docs/IMGUI_PARITY_PLAN.md §2.2), shared by its Avalonia
//  face (UI/Panels/RadarPanel.cs) and ImGui face (ImGui/Panels/RadarFace.cs).
//
//  RadarSource calls RynthPluginGetRadarSnapshot at 33 ms on the pump thread
//  while a radar face is open. Walls/fills only travel when the landblock
//  changes (the export skips them when told the cached MapVersion), so the
//  source keeps that cache - the logic moved here from the Avalonia panel's
//  timer - and publishes a RadarView with per-layer lookups and the current
//  layer's wall "visited" flags precomputed, so drawing does no dictionary
//  or point-in-strip work per frame.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace RynthCore.Engine.UI.Data;

// The snapshot DTOs (RadarSnapshot, RadarMarker, ...) are in RadarSnapshotJson.cs.

/// <summary>What either radar face draws: the live snapshot plus cached geometry and lookups.</summary>
internal sealed class RadarView
{
    public required RadarSnapshot Live { get; init; }
    public required List<RadarWallLayer> Walls { get; init; }
    public required List<RadarFillLayer> Fills { get; init; }

    /// <summary>Index into Live.LayerZs of the layer the player is on (-1: none).</summary>
    public required int CurrentLayer { get; init; }
    /// <summary>Per layer index (parallel to Live.LayerZs): its walls / fill strips / visited strips, or null.</summary>
    public required float[]?[] WallsByLayer { get; init; }
    public required float[]?[] FillsByLayer { get; init; }
    public required float[]?[] VisitedByLayer { get; init; }
    /// <summary>Per wall of the current layer: true when its midpoint is in a visited strip (drawn blue).</summary>
    public required bool[]? CurrentWallVisited { get; init; }
    /// <summary>"Matron Hive East  Z 12.3" in a named dungeon ("LB AABB" otherwise), "33.1N  44.8E  Z 12.3" outdoors.</summary>
    public required string CoordText { get; init; }
}

internal sealed unsafe class RadarSource : UiSource<RadarView>
{
    private delegate* unmanaged[Cdecl]<uint, IntPtr> _getRadarSnapshot;
    private uint _cachedMapVersion;
    private List<RadarWallLayer> _cachedWalls = new();
    private List<RadarFillLayer> _cachedFills = new();
    private string? _lastJson;

    // Wall "visited" flags depend only on the current layer's walls and visited
    // strips; recompute when either changes, not on every pose update.
    private float[]? _visitedKeyStrips;
    private float[]? _visitedKeyWalls;
    private bool[]? _visitedFlags;

    public RadarSource() : base("Radar", periodMs: 33) { }

    protected internal override void Poll()
    {
        if (_getRadarSnapshot == null)
            _getRadarSnapshot = (delegate* unmanaged[Cdecl]<uint, IntPtr>)PluginExportBinder.Resolve("RynthAi", "RynthPluginGetRadarSnapshot");
        if (_getRadarSnapshot == null) return;

        IntPtr ptr = _getRadarSnapshot(_cachedMapVersion);
        string? json = ptr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ptr); // copy now: freed on the next call
        if (string.IsNullOrEmpty(json) || json == "{}" || json == _lastJson) return;
        _lastJson = json;

        RadarSnapshot? fresh = RadarSnapshot.Parse(json);
        if (fresh == null) return;

        if (fresh.GeometryIncluded)
        {
            _cachedMapVersion = fresh.MapVersion;
            _cachedWalls = fresh.Walls;
            _cachedFills = fresh.Fills;
        }
        else if (fresh.MapVersion != _cachedMapVersion && fresh.MapVersion != 0 && !fresh.IsIndoor)
        {
            // Outdoor landblocks carry no geometry but still bump the version:
            // drop stale walls so they don't bleed through indoor -> outdoor.
            _cachedMapVersion = fresh.MapVersion;
            _cachedWalls = new List<RadarWallLayer>();
            _cachedFills = new List<RadarFillLayer>();
        }

        // First polls after login can land before raycast is ready: an indoor
        // snapshot with no walls. Re-ask with version 0 until geometry arrives,
        // or the plugin keeps skipping it and the radar stays blank.
        if (fresh.IsIndoor && _cachedWalls.Count == 0)
            _cachedMapVersion = 0;

        Publish(BuildView(fresh));
    }

    protected internal override void Reset()
    {
        _getRadarSnapshot = null;
        _cachedMapVersion = 0;
        _cachedWalls = new List<RadarWallLayer>();
        _cachedFills = new List<RadarFillLayer>();
        _lastJson = null;
        _visitedKeyStrips = _visitedKeyWalls = null;
        _visitedFlags = null;
        ClearSnapshot();
    }

    private RadarView BuildView(RadarSnapshot live)
    {
        List<float> layers = live.LayerZs;
        int cur = -1;
        if (layers.Count > 0)
        {
            cur = 0;
            float best = MathF.Abs(layers[0] - live.CurrentLayerZ);
            for (int i = 1; i < layers.Count; i++)
            {
                float d = MathF.Abs(layers[i] - live.CurrentLayerZ);
                if (d < best) { best = d; cur = i; }
            }
        }

        var walls = new float[]?[layers.Count];
        var fills = new float[]?[layers.Count];
        var visited = new float[]?[layers.Count];
        for (int i = 0; i < layers.Count; i++)
        {
            float z = layers[i];
            foreach (RadarWallLayer w in _cachedWalls) if (w.Z == z) { walls[i] = w.Segments; break; }
            foreach (RadarFillLayer f in _cachedFills) if (f.Z == z) { fills[i] = f.Strips; break; }
            foreach (RadarVisitedLayer v in live.Visited) if (v.Z == z) { visited[i] = v.Strips; break; }
        }

        bool[]? wallVisited = null;
        if (cur >= 0 && walls[cur] is float[] curWalls && visited[cur] is float[] curVisited)
        {
            if (!ReferenceEquals(curWalls, _visitedKeyWalls) || _visitedKeyStrips == null
                || !curVisited.AsSpan().SequenceEqual(_visitedKeyStrips))
            {
                _visitedKeyWalls = curWalls;
                _visitedKeyStrips = curVisited;
                _visitedFlags = new bool[curWalls.Length / 6];
                for (int w = 0; w < _visitedFlags.Length; w++)
                {
                    float mx = (curWalls[w * 6] + curWalls[w * 6 + 2]) * 0.5f;
                    float my = (curWalls[w * 6 + 1] + curWalls[w * 6 + 3]) * 0.5f;
                    _visitedFlags[w] = PointInStrips(mx, my, curVisited);
                }
            }
            wallVisited = _visitedFlags;
        }

        return new RadarView
        {
            Live = live,
            Walls = _cachedWalls,
            Fills = _cachedFills,
            CurrentLayer = cur,
            WallsByLayer = walls,
            FillsByLayer = fills,
            VisitedByLayer = visited,
            CurrentWallVisited = wallVisited,
            CoordText = FormatCoords(live),
        };
    }

    private static bool PointInStrips(float x, float y, float[] strips)
    {
        for (int i = 0; i + 3 < strips.Length; i += 4)
            if (x >= strips[i] && x <= strips[i + 2] && y >= strips[i + 1] && y <= strips[i + 3])
                return true;
        return false;
    }

    // Same readout as the ImGui-era and Avalonia radars.
    private static string FormatCoords(RadarSnapshot s)
    {
        RadarPlayer p = s.Player;
        if (s.IsIndoor)
        {
            RynthNavAtlas.EnsureLoadedForNames();
            return $"{RynthNavAtlas.DungeonLabel(p.Landblock)}  Z {p.Z:0.0}";
        }
        if (!double.IsNaN(s.Ns) && !double.IsNaN(s.Ew))
            return $"{Math.Abs(s.Ns):0.0}{(s.Ns >= 0 ? "N" : "S")}  {Math.Abs(s.Ew):0.0}{(s.Ew >= 0 ? "E" : "W")}  Z {p.Z:0.0}";
        return $"Z {p.Z:0.0}";
    }
}
