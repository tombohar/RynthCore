// ============================================================================
//  RynthCore.Engine - UI/Data/RynthNavAtlasData.cs
//  The Go and Atlas tabs' data: RynthNav's location database (NavData\locations.json,
//  made by tools\navdata\gen_locations.py from the world database), read here
//  read-only so thousands of places don't travel through the status JSON
//  (RynthNav itself owns favorites, recent, the arrow and routing). The file's
//  path comes from RynthNav's status, so both read the same file.
//
//    RynthNavAtlas         the loaded places (a background load, UiBackgroundWriter):
//                          the locations, then the recall spells' landing spots as
//                          places of type Recall (RynthNavAtlasRecalls.cs), and the
//                          player's map position from the pose snapshot.
//    RynthNavAtlasSource   the rows the face shows for its query (search by name
//                          and type, typed coordinates as the first row, Nearby,
//                          Favorites, Recent; Saved = favorites then recent, the Go
//                          tab's list while its box is empty), worked out on
//                          the pump thread twice a second and only when the query,
//                          the data or your position (by 3 yd) changed.
//
//  Map coordinates are the /loc numbers (north and east positive); distances are
//  in yards (world units, 240 per degree).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using RynthCore.Engine.Compatibility;

namespace RynthCore.Engine.UI.Data;

/// <summary>Saved: favorites, then recent places (the Go tab's list while its box is empty).</summary>
internal enum AtlasMode { Search, Nearby, Favorites, Recent, Saved }

internal sealed record AtlasQuery(string Text, string Type, AtlasMode Mode);

internal sealed class AtlasRow
{
    public required string Name, Type, Coord;
    public string Dist = "";
    /// <summary>"Town · 1.2k yd NE": the type and distance, for the Go tab's short list.</summary>
    public string Meta = "";
    public double Distance = double.PositiveInfinity;
    public bool OnMap, Favorite;
    public double Ns, Ew;
    /// <summary>The Atlas entry, when the row is one (a favorite or recent may not be).</summary>
    public NavPlace? Place;
}

internal sealed class AtlasView
{
    public static readonly AtlasView Empty = new(Array.Empty<AtlasRow>(), "");
    public AtlasView(AtlasRow[] rows, string summary) { Rows = rows; Summary = summary; }
    public AtlasRow[] Rows { get; }
    public string Summary { get; }
}

internal static class RynthNavAtlas
{
    public static readonly string[] Types =
        { "All", "Town", "Dungeon", "Portal", "Lifestone", "Bindstone", "Vendor", "NPC", "Landmark", RynthNavAtlasRecalls.Type };

    private static readonly object Sync = new();
    private static string _requested = "";
    private static volatile NavPlace[] _places = Array.Empty<NavPlace>();
    private static volatile HashSet<uint> _mapLandblocks = new();
    private static volatile Dictionary<uint, string> _dungeonNames = new();
    private static volatile string _status = "";
    private static long _version;

    public static NavPlace[] Places => _places;
    public static string Status => _status;
    public static long Version => System.Threading.Interlocked.Read(ref _version);

    /// <summary>Loads <paramref name="path"/> in the background unless it is already loaded or on its way. Any thread.</summary>
    public static void EnsureLoaded(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        lock (Sync)
        {
            if (path == _requested) return;
            _requested = path;
        }
        _status = "loading the Atlas…";
        UiBackgroundWriter.Enqueue("RynthNav atlas (load)", () => Load(path));
    }

    private static void Load(string path)
    {
        try
        {
            if (!File.Exists(path)) { _status = "No Atlas: " + path + " is missing."; return; }
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var list = new List<NavPlace>();
            var lbs = new HashSet<uint>();
            var dungeons = new Dictionary<uint, string>();
            if (doc.RootElement.TryGetProperty("locations", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement e in arr.EnumerateArray())
                {
                    var p = new NavPlace
                    {
                        Id = Int(e, "id"), Name = Str(e, "name"), Type = Str(e, "type"), Desc = Str(e, "desc"),
                        Place = Str(e, "place"), Lb = Str(e, "lb"), Cell = Str(e, "cell"),
                        MinLevel = Int(e, "minLevel"), MaxLevel = Int(e, "maxLevel"),
                        Quest = e.TryGetProperty("quest", out var q) && q.ValueKind == JsonValueKind.True,
                        Closed = e.TryGetProperty("closed", out var c) && c.ValueKind == JsonValueKind.True,
                    };
                    p.OnMap = TryNum(e, "ns", out p.Ns) & TryNum(e, "ew", out p.Ew);
                    if (e.TryGetProperty("dest", out var d) && d.ValueKind == JsonValueKind.Object)
                    {
                        p.HasDest = true;
                        p.DestOnMap = TryNum(d, "ns", out p.DestNs) & TryNum(d, "ew", out p.DestEw);
                        p.DestName = Str(d, "name");
                        p.DestText = p.DestOnMap ? RynthNavStatus.Coord(p.DestNs, p.DestEw)
                            : p.DestName.Length > 0 ? p.DestName + " (a dungeon)" : "a dungeon";
                    }
                    p.CoordText = p.OnMap ? RynthNavStatus.Coord(p.Ns, p.Ew) : "in a dungeon";
                    p.LevelText = p.MinLevel > 0 && p.MaxLevel > 0 ? $"levels {p.MinLevel}–{p.MaxLevel}"
                        : p.MinLevel > 0 ? $"level {p.MinLevel}+" : p.MaxLevel > 0 ? $"up to level {p.MaxLevel}" : "";
                    if (p.OnMap && p.Cell.Length == 8 && uint.TryParse(p.Cell.AsSpan(0, 4), NumberStyles.HexNumber, null, out uint lb))
                        lbs.Add(lb);
                    if (p.Type == "Dungeon" && p.Name.Length > 0
                        && uint.TryParse(p.Lb, NumberStyles.HexNumber, null, out uint dlb))
                        dungeons.TryAdd(dlb, p.Name);
                    list.Add(p);
                }
            }
            // The recall spells' landing spots, as places of type Recall (RynthNavAtlasRecalls).
            foreach (NavPlace p in RynthNavAtlasRecalls.Read(doc.RootElement, RynthNavStatus.Coord))
            {
                if (p.OnMap && p.Cell.Length == 8 && uint.TryParse(p.Cell.AsSpan(0, 4), NumberStyles.HexNumber, null, out uint lb))
                    lbs.Add(lb);
                list.Add(p);
            }
            _places = list.ToArray();
            _mapLandblocks = lbs;
            _dungeonNames = dungeons;
            string generated = doc.RootElement.TryGetProperty("generated", out var g) ? g.GetString() ?? "" : "";
            _status = $"{list.Count} places" + (generated.Length >= 10 ? ", from " + generated.Substring(0, 10) : "");
            System.Threading.Interlocked.Increment(ref _version);
        }
        catch (Exception ex)
        {
            _status = "The Atlas didn't load: " + ex.Message;
            lock (Sync) _requested = "";   // try again on the next request
        }
    }

    /// <summary>
    /// The Atlas's name for the dungeon on landblock <paramref name="lb"/> (a cell's top 16 bits),
    /// or "" when it has none or isn't loaded yet. Any thread.
    /// </summary>
    public static string DungeonName(uint lb) =>
        _dungeonNames.TryGetValue(lb & 0xFFFF, out string? name) ? name : "";

    /// <summary>
    /// Loads the Atlas for readers that don't own the Go tab (the radar, Sense): RynthNav's
    /// path when it has reported one, else the default NavData folder. Any thread.
    /// </summary>
    public static void EnsureLoadedForNames()
    {
        string path = UiSources.RynthNav.Current?.Value?.AtlasPath ?? "";
        if (path.Length == 0)
            path = Path.Combine(RynthCore.Install.RynthInstallPaths.CoreDir, "NavData", "locations.json");
        EnsureLoaded(path);
    }

    /// <summary>
    /// True when the Atlas has a surface (on-map) place in landblock <paramref name="lb"/> (a cell's
    /// top 16 bits): its indoor cells are buildings in the overworld, not a dungeon. Any thread.
    /// </summary>
    public static bool IsMapLandblock(uint lb) => _mapLandblocks.Contains(lb & 0xFFFF);

    /// <summary>"Matron Hive East" for a named dungeon, else "LB 6146".</summary>
    public static string DungeonLabel(uint lb)
    {
        string name = DungeonName(lb);
        return name.Length > 0 ? name : "LB " + (lb & 0xFFFF).ToString("X4", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Your map position from the pose snapshot (no AC call). <paramref name="onMap"/> is
    /// false in a dungeon, where the numbers aren't real map coordinates.
    /// </summary>
    public static bool TryPlayerPos(out double ns, out double ew, out bool onMap, out uint cell)
    {
        ns = ew = 0; onMap = false;
        if (!PlayerPhysicsHooks.TryGetPlayerPoseSnapshot(out cell, out float x, out float y, out _, out _, out _, out _, out _)
            || (cell >> 16) == 0)
            return false;
        int lbX = (int)((cell >> 24) & 0xFF), lbY = (int)((cell >> 16) & 0xFF);
        ew = (lbX * 8.0 + x / 24.0 - 1019.5) / 10.0;
        ns = (lbY * 8.0 + y / 24.0 - 1019.5) / 10.0;
        onMap = (cell & 0xFFFF) < 0x100 || _mapLandblocks.Contains(cell >> 16);
        return true;
    }

    public static double Distance(double ns1, double ew1, double ns2, double ew2)
    {
        double dn = (ns2 - ns1) * 240.0, de = (ew2 - ew1) * 240.0;
        return Math.Sqrt(dn * dn + de * de);
    }

    /// <summary>Compass bearing: 0 = north, clockwise.</summary>
    public static double Bearing(double fromNs, double fromEw, double toNs, double toEw)
    {
        double b = Math.Atan2(toEw - fromEw, toNs - fromNs) * 180.0 / Math.PI;
        return b < 0 ? b + 360.0 : b;
    }

    private static readonly string[] Compass16 =
        { "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW" };

    public static string CompassPoint(double bearing) => Compass16[(int)Math.Round(((bearing % 360.0) + 360.0) % 360.0 / 22.5) & 15];

    /// <summary>How well a name matches a search (RynthNav's Atlas.MatchScore): 0 = not at all.</summary>
    public static int MatchScore(string name, string q)
    {
        if (q.Length == 0) return 1;
        if (name.Equals(q, StringComparison.OrdinalIgnoreCase)) return 100;
        int at = name.IndexOf(q, StringComparison.OrdinalIgnoreCase);
        if (at == 0) return 80;
        if (at > 0) return char.IsLetterOrDigit(name[at - 1]) ? 40 : 60;
        string[] words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2) return 0;
        foreach (string w in words)
            if (name.IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0) return 0;
        return 30;
    }

    public static int TypeRank(string type) => type switch
    {
        "Town" => 0, "Dungeon" => 1, "Landmark" => 2, RynthNavAtlasRecalls.Type => 2, "Lifestone" => 3, "Bindstone" => 4,
        "Portal" => 5, "Vendor" => 6, "NPC" => 7, _ => 8,
    };

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : 0;

    private static bool TryNum(JsonElement e, string name, out double d)
    {
        d = 0;
        return e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out d);
    }
}

/// <summary>The Go or Atlas tab's rows for <see cref="Query"/> (pump thread, 500 ms, while either tab is open).</summary>
internal sealed class RynthNavAtlasSource : UiSource<AtlasView>
{
    private const int MaxRows = 300;

    /// <summary>What the Go or Atlas tab asks for (the one showing). Set by the face (AC's thread), read here.</summary>
    public static volatile AtlasQuery Query = new("", "All", AtlasMode.Search);

    private AtlasQuery? _seenQuery;
    private long _seenAtlas = -1, _seenStatus = -1;
    private double _seenNs = double.NaN, _seenEw = double.NaN;

    public RynthNavAtlasSource() : base("RynthNavAtlas", periodMs: 500) { }

    protected internal override void Poll()
    {
        UiSnapshot<RynthNavStatus>? stSnap = UiSources.RynthNav.Current;
        RynthNavStatus? st = stSnap?.Value;
        if (st != null && st.AtlasPath.Length > 0) RynthNavAtlas.EnsureLoaded(st.AtlasPath);

        AtlasQuery q = Query;
        bool hasPos = RynthNavAtlas.TryPlayerPos(out double pns, out double pew, out bool onMap, out _);
        hasPos &= onMap;
        long atlasV = RynthNavAtlas.Version, statusV = stSnap?.Version ?? -1;
        bool moved = hasPos != !double.IsNaN(_seenNs)
            || (hasPos && RynthNavAtlas.Distance(pns, pew, _seenNs, _seenEw) > 3);
        // The status moves with favorites (the stars) and recent, so it counts for every mode.
        if (q == _seenQuery && atlasV == _seenAtlas && !moved && statusV == _seenStatus && Current != null)
            return;
        _seenQuery = q; _seenAtlas = atlasV; _seenStatus = statusV;
        _seenNs = hasPos ? pns : double.NaN; _seenEw = hasPos ? pew : double.NaN;

        var favorites = st?.Favorites ?? Array.Empty<RynthNavPlace>();
        NavPlace[] places = RynthNavAtlas.Places;
        var rows = new List<AtlasRow>();
        string summary;
        switch (q.Mode)
        {
            case AtlasMode.Saved:
            {
                // Favorites first, then recent places that aren't favorites (each place once).
                foreach (RynthNavPlace s in favorites) rows.Add(SavedRow(places, s));
                foreach (RynthNavPlace s in st?.Recent ?? Array.Empty<RynthNavPlace>())
                {
                    bool fav = false;
                    foreach (RynthNavPlace f in favorites)
                        if (SamePlace(f.Name, f.Ns, f.Ew, s.Name, s.Ns, s.Ew)) { fav = true; break; }
                    if (!fav) rows.Add(SavedRow(places, s));
                }
                summary = st == null ? "RynthNav isn't answering."
                    : rows.Count == 0 ? "Type a place, or coordinates like 42.1N, 33.6E. Favorites and recent places show here."
                    : $"{favorites.Length} favorite{(favorites.Length == 1 ? "" : "s")}, {rows.Count - favorites.Length} recent";
                break;
            }
            case AtlasMode.Favorites:
            case AtlasMode.Recent:
            {
                RynthNavPlace[] saved = q.Mode == AtlasMode.Favorites ? favorites : st?.Recent ?? Array.Empty<RynthNavPlace>();
                foreach (RynthNavPlace s in saved)
                {
                    if (!TypeMatches(q.Type, s.Type) || RynthNavAtlas.MatchScore(s.Name, q.Text.Trim()) == 0) continue;
                    rows.Add(SavedRow(places, s));
                }
                summary = st == null ? "RynthNav isn't answering."
                    : rows.Count == 0 ? (q.Mode == AtlasMode.Favorites ? "No favorites yet: the star on a place adds it." : "Nothing yet: places you point the arrow at show here.")
                    : $"{rows.Count} {(q.Mode == AtlasMode.Favorites ? "favorite" : "recent place")}{(rows.Count == 1 ? "" : "s")}";
                break;
            }
            case AtlasMode.Nearby:
            {
                if (!hasPos) { summary = places.Length == 0 ? RynthNavAtlas.Status : "Nearby needs you on the map (not in a dungeon)."; break; }
                var hits = new List<(NavPlace P, double D)>();
                string text = q.Text.Trim();
                foreach (NavPlace p in places)
                {
                    if (!p.OnMap || !TypeMatches(q.Type, p.Type) || RynthNavAtlas.MatchScore(p.Name, text) == 0) continue;
                    hits.Add((p, RynthNavAtlas.Distance(pns, pew, p.Ns, p.Ew)));
                }
                hits.Sort((a, b) => a.D.CompareTo(b.D));
                for (int i = 0; i < hits.Count && rows.Count < MaxRows; i++) rows.Add(Row(hits[i].P));
                summary = places.Length == 0 ? RynthNavAtlas.Status : $"{hits.Count} on the map, nearest first";
                break;
            }
            default:
            {
                string text = q.Text.Trim();
                var hits = new List<(NavPlace P, int S)>();
                foreach (NavPlace p in places)
                {
                    if (!TypeMatches(q.Type, p.Type)) continue;
                    int sc = RynthNavAtlas.MatchScore(p.Name, text);
                    if (sc > 0) hits.Add((p, sc));
                }
                hits.Sort((a, b) =>
                {
                    int c = b.S.CompareTo(a.S);
                    if (c != 0) return c;
                    c = RynthNavAtlas.TypeRank(a.P.Type).CompareTo(RynthNavAtlas.TypeRank(b.P.Type));
                    if (c != 0) return c;
                    c = b.P.OnMap.CompareTo(a.P.OnMap);
                    return c != 0 ? c : string.Compare(a.P.Name, b.P.Name, StringComparison.OrdinalIgnoreCase);
                });
                for (int i = 0; i < hits.Count && rows.Count < MaxRows; i++) rows.Add(Row(hits[i].P));
                summary = places.Length == 0 ? RynthNavAtlas.Status
                    : hits.Count > rows.Count ? $"{hits.Count} matches, the first {rows.Count} shown" : $"{hits.Count} of {places.Length} places";
                break;
            }
        }

        // Coordinates typed ("42.1N, 33.6E", a name after them is kept): the first row.
        if (q.Mode == AtlasMode.Search && CoordRow(q.Text) is AtlasRow coordRow)
        {
            rows.Insert(0, coordRow);
            if (rows.Count == 1) summary = "Coordinates: " + coordRow.Coord;
        }

        foreach (AtlasRow r in rows)
        {
            if (hasPos && r.OnMap)
            {
                r.Distance = RynthNavAtlas.Distance(pns, pew, r.Ns, r.Ew);
                r.Dist = RynthNavStatus.Yards(r.Distance) + " " + RynthNavAtlas.CompassPoint(RynthNavAtlas.Bearing(pns, pew, r.Ns, r.Ew));
            }
            r.Meta = r.Dist.Length > 0 ? r.Type + "  ·  " + r.Dist : r.OnMap ? r.Type : r.Type + "  ·  in a dungeon";
            foreach (RynthNavPlace f in favorites)
                if (SamePlace(f.Name, f.Ns, f.Ew, r.Name, r.Ns, r.Ew)) { r.Favorite = true; break; }
        }
        Publish(new AtlasView(rows.ToArray(), summary));
    }

    private static AtlasRow SavedRow(NavPlace[] places, RynthNavPlace s) => new()
    {
        Name = s.Name, Type = s.Type.Length > 0 ? s.Type : "Place", Coord = s.CoordText, OnMap = true, Ns = s.Ns, Ew = s.Ew,
        Place = Find(places, s.Name, s.Ns, s.Ew),
    };

    private static bool SamePlace(string n1, double ns1, double ew1, string n2, double ns2, double ew2) =>
        n1.Equals(n2, StringComparison.OrdinalIgnoreCase) && Math.Abs(ns1 - ns2) < 0.05 && Math.Abs(ew1 - ew2) < 0.05;

    /// <summary>A row for typed coordinates (the whole text, or coordinates then a name), else null.</summary>
    internal static AtlasRow? CoordRow(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        var found = ChatCoords.Find(text);
        if (found.Count == 0 || found[0].Start != 0) return null;
        ChatCoord c = found[0];
        string name = text.Substring(c.Length).Trim().TrimStart(',', ';').Trim();
        return new AtlasRow
        {
            Name = name.Length > 0 ? name : c.Text, Type = "Coordinates", Coord = c.Text, OnMap = true, Ns = c.Ns, Ew = c.Ew,
        };
    }

    private static AtlasRow Row(NavPlace p) => new()
    {
        Name = p.Name, Type = p.Type, Coord = p.CoordText, OnMap = p.OnMap, Ns = p.Ns, Ew = p.Ew, Place = p,
    };

    private static bool TypeMatches(string filter, string type) =>
        filter.Length == 0 || filter == "All" || filter.Equals(type, StringComparison.OrdinalIgnoreCase);

    private static NavPlace? Find(NavPlace[] places, string name, double ns, double ew)
    {
        foreach (NavPlace p in places)
            if (p.OnMap && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && Math.Abs(p.Ns - ns) < 0.05 && Math.Abs(p.Ew - ew) < 0.05)
                return p;
        return null;
    }

    protected internal override void Reset()
    {
        _seenQuery = null;
        ClearSnapshot();
    }
}
