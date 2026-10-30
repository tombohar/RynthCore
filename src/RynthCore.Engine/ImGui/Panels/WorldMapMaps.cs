// ============================================================================
//  RynthCore.Engine - ImGui/Panels/WorldMapMaps.cs
//  Which World Map picture to use. NavData and MapPacks can hold up to four, each a pyramid of tiles at its own
//  resolution (RynthNav.Baker --map-world): worldmap-p64.rmap (Maximum, 64 px per cell), worldmap-p32.rmap (Extreme, 32), worldmap-ultra.rmap (16), worldmap-hd.rmap (8) and
//  worldmap.rmap (Standard: the one everybody gets). The launcher offers the bigger ones as optional
//  downloads, so a PC may have any of them: by default the best installed is used, and the Show menu's Detail
//  list lets a player pick another that is installed. The choice is remembered (WorldMapSettings.Detail).
//  The folder is scanned on a pool thread (no file access on AC's thread); a change of file is made by
//  WorldMapTiles.Switch at the start of the next frame.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using RynthCore.Engine.UI.Data;

namespace RynthCore.Engine.ImGuiBackend.Panels;

internal static class WorldMapMaps
{
    internal readonly record struct Tier(string Key, string Label, string File);

    /// <summary>Best first.</summary>
    public static readonly Tier[] Tiers =
    {
        new("maximum", "Maximum", "worldmap-p64.rmap"),
        new("extreme", "Extreme", "worldmap-p32.rmap"),
        new("ultra", "Ultra", "worldmap-ultra.rmap"),
        new("hd", "HD", "worldmap-hd.rmap"),
        new("standard", "Standard", "worldmap.rmap"),
    };

    private static volatile Tier[] _installed = Array.Empty<Tier>();
    private static volatile bool _scanned;
    private static volatile Dictionary<string, string> _paths = new();     // tier key -> the file found

    /// <summary>RynthNav's data folder: where the plugin says it is, else the default. Any thread.</summary>
    public static string Dir
    {
        get
        {
            string dir = UiSources.RynthNav.Current?.Value?.NavDir ?? "";
            return dir.Length > 0 && Path.IsPathFullyQualified(dir) ? dir.TrimEnd((char)92, (char)47) : Path.Combine(RynthCore.Install.RynthInstallPaths.CoreDir, "NavData");
        }
    }

    /// <summary>Where the launcher puts the optional sizes: MapPacks, beside the NavData folder.</summary>
    public static string PacksDir => Path.Combine(Path.GetDirectoryName(Dir) ?? Dir, "MapPacks");

    /// <summary>The tiers found on the last scan, best first (empty until the first scan finishes).</summary>
    public static Tier[] Installed => _installed;
    public static bool Scanned => _scanned;

    /// <summary>The file found for a tier (the NavData name when none was found, so a status can name it).</summary>
    public static string PathOf(Tier t) => _paths.TryGetValue(t.Key, out string? p) ? p : Path.Combine(Dir, t.File);

    /// <summary>The tier the setting calls for: that one if installed, else the best installed ("" and null when none).</summary>
    public static Tier? Resolve(string preference)
    {
        Tier[] have = _installed;
        if (have.Length == 0) return null;
        foreach (Tier t in have) if (t.Key == preference) return t;
        return have[0];
    }

    /// <summary>Scans the folder and (re)opens the chosen file, without touching AC's thread. Any thread.</summary>
    public static void Refresh()
    {
        Task.Run(() =>
        {
            var found = new List<Tier>();
            var paths = new Dictionary<string, string>();
            string[] dirs = { PacksDir, Dir };                  // a pack the launcher installed, else the one with the RynthNav data
            foreach (Tier t in Tiers)
            {
                foreach (string d in dirs)
                {
                    string f = Path.Combine(d, t.File);
                    try { if (File.Exists(f)) { found.Add(t); paths[t.Key] = f; break; } } catch { }
                }
            }
            _paths = paths;
            _installed = found.ToArray();
            _scanned = true;
            Apply();
        });
    }

    /// <summary>Opens (or switches to) the file the current setting resolves to.</summary>
    public static void Apply()
    {
        Tier? t = Resolve(WorldMapSettings.Detail);
        // Nothing installed: ask for the Standard name, so the faces say "No world map file yet".
        string path = t is Tier tier ? PathOf(tier) : PathOf(Tiers[^1]);
        if (WorldMapTiles.CurrentPath.Length == 0) WorldMapTiles.Open(path);
        else WorldMapTiles.Switch(path);
    }

    /// <summary>The label of the tier in use ("" until a file is open).</summary>
    public static string CurrentLabel
    {
        get
        {
            string cur = WorldMapTiles.CurrentPath;
            foreach (Tier t in Tiers) if (cur.EndsWith(t.File, StringComparison.OrdinalIgnoreCase)) return t.Label;
            return "";
        }
    }
}
