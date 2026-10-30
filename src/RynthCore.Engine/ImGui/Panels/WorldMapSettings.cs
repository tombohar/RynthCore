// ============================================================================
//  RynthCore.Engine - ImGui/Panels/WorldMapSettings.cs
//  What the World Map and the Mini Map remember: each view's zoom, Follow and Rotate, and the layer
//  switches they share (which kinds of place, labels, route, path, grid, other characters).
//  Engine-wide, in %LOCALAPPDATA%\RynthCore\world_map_settings.txt, like dungeon_map_settings.txt.
//  Zoom changes a lot (every wheel tick), so a change only marks the settings dirty; Draw calls
//  FlushIfDue and the file is written (in the background) once nothing has changed for 1.5 s.
// ============================================================================

using System;
using System.Globalization;
using System.IO;
using System.Text;
using RynthCore.Engine.UI;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal static class WorldMapSettings
{
    internal sealed class ViewState
    {
        public float Scale = 1.5f;          // screen px per cell
        public bool Follow = true, Rotate;
    }

    public static readonly ViewState Full = new() { Scale = 1.5f, Follow = true, Rotate = false };
    public static readonly ViewState Mini = new() { Scale = 2.5f, Follow = true, Rotate = true };

    /// <summary>Which kinds of place show (WorldMapView.Kind order).</summary>
    public static readonly bool[] Show = { true, true, true, true, true, true, true, true };
    /// <summary>Which map picture: a WorldMapMaps tier key ("ultra", "hd", "standard"), or "" for the best installed.</summary>
    public static string Detail = "";
    public static bool Labels = true, Route = true, WalkPath = true, Grid = true, Characters = true, Buildings = true;

    private static readonly object Sync = new();
    private static bool _loaded;
    private static long _dirtyAtMs;           // 0: nothing to write

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RynthCore", "world_map_settings.txt");

    /// <summary>Reads the file once. Engine init thread or any pool thread (not AC's).</summary>
    public static void Load()
    {
        lock (Sync)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (string raw in File.ReadAllLines(FilePath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line[..eq].Trim(), val = line[(eq + 1)..].Trim();
                    bool on = val == "1";
                    switch (key)
                    {
                        case "full.scale": Full.Scale = ParseScale(val, Full.Scale); break;
                        case "full.follow": Full.Follow = on; break;
                        case "full.rotate": Full.Rotate = on; break;
                        case "mini.scale": Mini.Scale = ParseScale(val, Mini.Scale); break;
                        case "mini.follow": Mini.Follow = on; break;
                        case "mini.rotate": Mini.Rotate = on; break;
                        case "detail": Detail = val is "maximum" or "extreme" or "ultra" or "hd" or "standard" ? val : ""; break;
                        case "labels": Labels = on; break;
                        case "route": Route = on; break;
                        case "path": WalkPath = on; break;
                        case "grid": Grid = on; break;
                        case "characters": Characters = on; break;
                        case "buildings": Buildings = on; break;
                        default:
                            if (key.StartsWith("show.", StringComparison.Ordinal)
                                && int.TryParse(key.AsSpan(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out int k) && (uint)k < (uint)Show.Length)
                                Show[k] = on;
                            break;
                    }
                }
            }
            catch { /* best-effort: the defaults are in place */ }
        }
    }

    private static float ParseScale(string v, float fallback) =>
        float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) && float.IsFinite(f) ? Math.Clamp(f, 0.12f, 14f) : fallback;

    /// <summary>Something changed: the file is written when it has been quiet for a moment. Any thread.</summary>
    public static void MarkDirty() => _dirtyAtMs = Math.Max(1, Environment.TickCount64);

    /// <summary>AC's render thread, once a frame: writes the file if a change has settled.</summary>
    public static void FlushIfDue()
    {
        long at = _dirtyAtMs;
        if (at == 0 || Environment.TickCount64 - at < 1500) return;
        _dirtyAtMs = 0;
        var sb = new StringBuilder("# RynthCore world map settings - auto-generated, hand-edits OK.\n");
        void Flag(string k, bool v) => sb.Append(k).Append('=').Append(v ? '1' : '0').Append('\n');
        sb.Append(CultureInfo.InvariantCulture, $"full.scale={Full.Scale:0.###}\n"); Flag("full.follow", Full.Follow); Flag("full.rotate", Full.Rotate);
        sb.Append(CultureInfo.InvariantCulture, $"mini.scale={Mini.Scale:0.###}\n"); Flag("mini.follow", Mini.Follow); Flag("mini.rotate", Mini.Rotate);
        for (int i = 0; i < Show.Length; i++) Flag("show." + i, Show[i]);
        sb.Append("detail=").Append(Detail).Append((char)10);
        Flag("labels", Labels); Flag("route", Route); Flag("path", WalkPath); Flag("grid", Grid); Flag("characters", Characters); Flag("buildings", Buildings);
        string text = sb.ToString();
        UiBackgroundWriter.Enqueue("world map settings", () =>
        {
            lock (Sync)
            {
                try
                {
                    string? dir = Path.GetDirectoryName(FilePath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(FilePath, text);
                }
                catch { /* non-fatal */ }
            }
        });
    }
}
