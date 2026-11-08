// ============================================================================
//  RynthCore.Engine - UI/Panels/RadarSettingsStore.cs
//  The radar's settings (zoom, opacity, rotate, per-kind show boxes and
//  colours), shared by the ImGui face (ImGui/Panels/RadarFace.cs), the Dungeon
//  Map and the Avalonia face (RadarPanel.cs). Split out of RadarPanel.cs so
//  tools/RadarSettingsTests can compile it without Avalonia.
// ============================================================================

using System;
using System.Globalization;
using System.IO;
using RynthCore.Engine.UI.Data;

namespace RynthCore.Engine.UI.Panels;

/// <summary>
/// Disk-backed persistence for radar settings (zoom, opacity, rotate, marker
/// visibility). Settings live under %LocalAppData%\RynthCore\radar_settings.txt
/// so they survive engine reloads, plugin reloads, and AC restarts.
/// </summary>
internal static class RadarSettingsStore
{
    private static readonly object _sync = new();
    private static bool _loaded;

    public static float Zoom = 1.5f;
    public static float Opacity = 1.0f;
    public static bool RotateWithPlayer;
    public static bool ShowGoldRim = true;
    /// <summary>ImGui face: a round radar (everything cut to a circle) instead of the square one.</summary>
    public static bool RoundRadar;
    public static bool CtrlGatedClickThrough = false;
    /// <summary>ImGui face, with click-through on: the mouse wheel over the docked radar still zooms it
    /// (and doesn't reach the game); clicks still go through. Off by default (2026-10-29).</summary>
    public static bool ClickThroughWheelZoom = false;
    public static bool ShowMonsters = true;
    public static bool ShowNpcs = true;
    public static bool ShowPortals = true;
    public static bool ShowDoors = true;
    // Kinds added 2026-10-04 (RadarKind 4..11). Items on the ground start hidden: noisy.
    public static bool ShowPlayers = true;
    public static bool ShowFellows = true;
    public static bool ShowPets = true;
    public static bool ShowVendors = true;
    public static bool ShowCorpses = true;
    public static bool ShowOwnCorpses = true;
    public static bool ShowLifestones = true;
    public static bool ShowGroundItems = false;
    /// <summary>ImGui face: a left-click on a dot selects that object in game.</summary>
    public static bool ClickToSelect = true;

    /// <summary>ImGui face: the object selected in game gets a ring round its marker (and an edge arrow
    /// when it is off the picture). On by default (2026-10-31).</summary>
    public static bool HighlightSelected = true;
    /// <summary>...and the hover label (name, distance, health) beside it. Off by default: the box covers
    /// the radar, and the ring alone says which one is selected (2026-10-31).</summary>
    public static bool ShowSelectedLabel = false;
    /// <summary>The ring's colour (ARGB): magenta, a hue none of the kinds use by default.</summary>
    public const uint DefaultSelectedColor = 0xFFFF2ED9;
    public static uint SelectedColor = DefaultSelectedColor;

    /// <summary>ImGui face: the World Map's picture drawn behind the radar while you're outdoors (off by default).</summary>
    public static bool ShowWorldMap;
    /// <summary>How strongly that picture shows (the radar's own Opacity applies on top).</summary>
    public static float WorldMapOpacity = 0.8f;

    /// <summary>ImGui face: a ring round you at RynthAi's Monster Range, the distance the bot attacks
    /// from (2026-11-06). On by default; nothing is drawn until RynthAi has sent its settings.</summary>
    public static bool ShowMonsterRangeRing = true;
    /// <summary>...and one at its Ring Range (ring spells). Off by default.</summary>
    public static bool ShowRingRangeRing;

    // Text sizes of the compass letters and the coordinate strip (px at 100% UI scale).
    public const float DefaultCompassSize = 14f, DefaultCoordSize = 11f, MinText = 8f, MaxText = 28f;
    public static float CompassSize = DefaultCompassSize;
    public static float CoordSize = DefaultCoordSize;

    /// <summary>
    /// Per-kind marker size and name label (added 2026-10-15). Names start on for the kinds you
    /// look for by name - monsters, NPCs, vendors, players, fellows, portals - and off for the
    /// rest, which would only add clutter.
    /// </summary>
    public static readonly MarkerStyleSettings Markers = new(new[]
    {
        true,  // monster
        true,  // NPC
        true,  // portal
        false, // door
        true,  // player
        true,  // fellow
        false, // your pet
        true,  // vendor
        false, // corpse
        false, // your corpse
        false, // lifestone
        false, // item on the ground
    });

    /// <summary>
    /// Default marker colour per <see cref="RadarKind"/> (ARGB). Chosen to stay apart for
    /// red-green colour blindness as well: the kinds that share a hue family also differ
    /// in shape (see RadarMarkerStyle) or brightness.
    /// </summary>
    public static readonly uint[] DefaultKindColors =
    {
        0xFFFF3333, // monster: red
        0xFFFFD933, // NPC: yellow
        0xFFBF4DFF, // portal: purple
        0xFFFF8C1A, // door: orange
        0xFF33C7FF, // player: cyan-blue
        0xFF4DE673, // fellow: green
        0xFF26BFA6, // your pet: teal
        0xFFFFB31A, // vendor: gold
        0xFF8C9399, // corpse: grey
        0xFFF2F2F2, // your corpse: near white
        0xFF99D1FF, // lifestone: light blue
        0xFFD9C78C, // item on the ground: pale tan
    };

    /// <summary>Marker colour per <see cref="RadarKind"/> (ARGB); the user's picks, else the defaults.</summary>
    public static readonly uint[] KindColors = (uint[])DefaultKindColors.Clone();

    // Settings-file keys per kind (color.<key>=AARRGGBB).
    private static readonly string[] KindKeys = MarkerStyleSettings.Keys;

    /// <summary>The show/hide flag of a kind (the radar's per-kind check boxes).</summary>
    public static ref bool ShowFlag(byte kind)
    {
        switch (kind)
        {
            case RadarKind.Monster:    return ref ShowMonsters;
            case RadarKind.Npc:        return ref ShowNpcs;
            case RadarKind.Portal:     return ref ShowPortals;
            case RadarKind.Door:       return ref ShowDoors;
            case RadarKind.Player:     return ref ShowPlayers;
            case RadarKind.Fellow:     return ref ShowFellows;
            case RadarKind.Pet:        return ref ShowPets;
            case RadarKind.Vendor:     return ref ShowVendors;
            case RadarKind.Corpse:     return ref ShowCorpses;
            case RadarKind.OwnCorpse:  return ref ShowOwnCorpses;
            case RadarKind.Lifestone:  return ref ShowLifestones;
            default:                   return ref ShowGroundItems;
        }
    }

    /// <summary>Whether markers of this kind are drawn; false for kinds this engine doesn't know.</summary>
    /// "Corpses" off hides every corpse, the lootable ones ("Your corpses") too: those are a
    /// separate kind, so with only Corpses off your own kills still showed (2026-10-12).
    public static bool ShowKind(byte kind) =>
        kind < RadarKind.Count && ShowFlag(kind) && (kind != RadarKind.OwnCorpse || ShowCorpses);

    /// <summary>The kind's colour (ARGB); white for a kind this engine doesn't know.</summary>
    public static uint KindColor(byte kind) => kind < KindColors.Length ? KindColors[kind] : 0xFFFFFFFF;

    /// <summary>Every kind's colour, and the selection ring's, back to the defaults.</summary>
    public static void ResetKindColors()
    {
        Array.Copy(DefaultKindColors, KindColors, KindColors.Length);
        SelectedColor = DefaultSelectedColor;
    }

    /// <summary>Every marker size, name box and text size back to its default (show boxes and colours stay).</summary>
    public static void ResetSizes()
    {
        Markers.Reset();
        CompassSize = DefaultCompassSize;
        CoordSize = DefaultCoordSize;
    }

    // RYNTHCORE_RADAR_SETTINGS points a test at its own file instead of the real one.
    private static string FilePath
    {
        get
        {
            if (Environment.GetEnvironmentVariable("RYNTHCORE_RADAR_SETTINGS") is { Length: > 0 } overridePath)
                return overridePath;
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(root, "RynthCore", "radar_settings.txt");
        }
    }

    public static void Load()
    {
        lock (_sync)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                string path = FilePath;
                if (!File.Exists(path)) return;
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith('#')) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line[..eq].Trim();
                    string val = line[(eq + 1)..].Trim();
                    switch (key)
                    {
                        case "zoom":
                            if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                                Zoom = Math.Clamp(z, 0.5f, 6.0f);
                            break;
                        case "opacity":
                            if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float o))
                                Opacity = Math.Clamp(o, 0.05f, 1.0f);
                            break;
                        case "rotate":           RotateWithPlayer        = val == "1"; break;
                        case "showGoldRim":      ShowGoldRim             = val == "1"; break;
                        case "round":            RoundRadar              = val == "1"; break;
                        case "ctrlClickThrough": CtrlGatedClickThrough   = val == "1"; break;
                        case "clickThroughWheel": ClickThroughWheelZoom  = val == "1"; break;
                        case "showMonsters":     ShowMonsters            = val == "1"; break;
                        case "showNpcs":     ShowNpcs         = val == "1"; break;
                        case "showPortals":  ShowPortals      = val == "1"; break;
                        case "showDoors":    ShowDoors        = val == "1"; break;
                        case "showPlayers":     ShowPlayers     = val == "1"; break;
                        case "showFellows":     ShowFellows     = val == "1"; break;
                        case "showPets":        ShowPets        = val == "1"; break;
                        case "showVendors":     ShowVendors     = val == "1"; break;
                        case "showCorpses":     ShowCorpses     = val == "1"; break;
                        case "showOwnCorpses":  ShowOwnCorpses  = val == "1"; break;
                        case "showLifestones":  ShowLifestones  = val == "1"; break;
                        case "showGroundItems": ShowGroundItems = val == "1"; break;
                        case "clickSelect":  ClickToSelect    = val == "1"; break;
                        case "selectHighlight": HighlightSelected = val == "1"; break;
                        case "selectLabel":     ShowSelectedLabel = val == "1"; break;
                        case "selectedColor":
                            if (uint.TryParse(val, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint sc))
                                SelectedColor = sc;
                            break;
                        case "worldMap":     ShowWorldMap     = val == "1"; break;
                        case "monsterRangeRing": ShowMonsterRangeRing = val == "1"; break;
                        case "ringRangeRing":    ShowRingRangeRing    = val == "1"; break;
                        case "worldMapOpacity":
                            if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float wo) && float.IsFinite(wo))
                                WorldMapOpacity = Math.Clamp(wo, 0.1f, 1.0f);
                            break;
                        case "compassSize":
                            if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float cs) && float.IsFinite(cs))
                                CompassSize = Math.Clamp(cs, MinText, MaxText);
                            break;
                        case "coordSize":
                            if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float ds) && float.IsFinite(ds))
                                CoordSize = Math.Clamp(ds, MinText, MaxText);
                            break;
                        default:
                            if (Markers.TryLoad(key, val)) break;
                            if (key.StartsWith("color.", StringComparison.Ordinal))
                            {
                                int k = Array.IndexOf(KindKeys, key["color.".Length..]);
                                if (k >= 0 && uint.TryParse(val, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint argb))
                                    KindColors[k] = argb;
                            }
                            break;
                    }
                }
            }
            catch { /* best-effort: defaults already in place */ }
        }
    }

    /// <summary>Writes the settings in the background (callers include AC's render thread).</summary>
    public static void Save() => UiBackgroundWriter.Enqueue("radar settings", SaveNow);

    private static void SaveNow()
    {
        lock (_sync)
        {
            try
            {
                string path = FilePath;
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                using var sw = new StreamWriter(path, append: false);
                sw.WriteLine("# RynthCore radar settings — auto-generated, hand-edits OK.");
                sw.WriteLine(FormattableString.Invariant($"zoom={Zoom:0.##}"));
                sw.WriteLine(FormattableString.Invariant($"opacity={Opacity:0.##}"));
                sw.WriteLine($"rotate={(RotateWithPlayer ? "1" : "0")}");
                sw.WriteLine($"showGoldRim={(ShowGoldRim ? "1" : "0")}");
                sw.WriteLine($"round={(RoundRadar ? "1" : "0")}");
                sw.WriteLine($"ctrlClickThrough={(CtrlGatedClickThrough ? "1" : "0")}");
                sw.WriteLine($"clickThroughWheel={(ClickThroughWheelZoom ? "1" : "0")}");
                sw.WriteLine($"showMonsters={(ShowMonsters ? "1" : "0")}");
                sw.WriteLine($"showNpcs={(ShowNpcs ? "1" : "0")}");
                sw.WriteLine($"showPortals={(ShowPortals ? "1" : "0")}");
                sw.WriteLine($"showDoors={(ShowDoors ? "1" : "0")}");
                sw.WriteLine($"showPlayers={(ShowPlayers ? "1" : "0")}");
                sw.WriteLine($"showFellows={(ShowFellows ? "1" : "0")}");
                sw.WriteLine($"showPets={(ShowPets ? "1" : "0")}");
                sw.WriteLine($"showVendors={(ShowVendors ? "1" : "0")}");
                sw.WriteLine($"showCorpses={(ShowCorpses ? "1" : "0")}");
                sw.WriteLine($"showOwnCorpses={(ShowOwnCorpses ? "1" : "0")}");
                sw.WriteLine($"showLifestones={(ShowLifestones ? "1" : "0")}");
                sw.WriteLine($"showGroundItems={(ShowGroundItems ? "1" : "0")}");
                sw.WriteLine($"clickSelect={(ClickToSelect ? "1" : "0")}");
                sw.WriteLine($"selectHighlight={(HighlightSelected ? "1" : "0")}");
                sw.WriteLine($"selectLabel={(ShowSelectedLabel ? "1" : "0")}");
                // Like the kind colours: only when it differs from the default.
                if (SelectedColor != DefaultSelectedColor)
                    sw.WriteLine(FormattableString.Invariant($"selectedColor={SelectedColor:X8}"));
                sw.WriteLine($"worldMap={(ShowWorldMap ? "1" : "0")}");
                sw.WriteLine(FormattableString.Invariant($"worldMapOpacity={WorldMapOpacity:0.##}"));
                sw.WriteLine($"monsterRangeRing={(ShowMonsterRangeRing ? "1" : "0")}");
                sw.WriteLine($"ringRangeRing={(ShowRingRangeRing ? "1" : "0")}");
                sw.WriteLine(FormattableString.Invariant($"compassSize={CompassSize:0.##}"));
                sw.WriteLine(FormattableString.Invariant($"coordSize={CoordSize:0.##}"));
                var styleLines = new System.Collections.Generic.List<string>();
                Markers.AppendLines(styleLines);
                foreach (string line in styleLines) sw.WriteLine(line);
                // Only colours that differ from the default, so a later default change still lands.
                for (int k = 0; k < KindColors.Length; k++)
                    if (KindColors[k] != DefaultKindColors[k])
                        sw.WriteLine(FormattableString.Invariant($"color.{KindKeys[k]}={KindColors[k]:X8}"));
            }
            catch { /* non-fatal */ }
        }
    }
}
