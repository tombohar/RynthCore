using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RynthCore.StatusAgent;

/// <summary>
/// The phone's destination search (GET /nav/search) over RynthNav's own Atlas file, NavData\locations.json
/// (towns, dungeons, portals, lifestones, NPCs, vendors...), read here rather than copied: one file, the
/// one the in-game Atlas searches. Ranking is RynthNav's (Atlas.MatchScore / TypeRank, copied), so the phone
/// lists places in the order the in-game search does; "area" is the nearest town for a place on the map.
/// Reloaded when the file changes (NavData updates). The travel itself goes to RynthNav as a /rnav "go"
/// through the RynthRemote plugin - the agent never touches RynthNav.
/// </summary>
internal sealed class NavAtlasService
{
    /// <summary>RynthNav's NavCoords.UnitsPerDegree: one degree of N/S or E/W is 240 units ("yd").</summary>
    public const double UnitsPerDegree = 240.0;

    internal sealed record Place(int Id, string Name, string Type, string Where, bool OnMap, double Ns, double Ew, string Area,
                                 bool Closed, bool Quest, int MinLevel, string DestName);

    private readonly string _path;
    private readonly object _gate = new();
    private List<Place>? _places;
    private DateTime _loadedMtime;
    private string _generated = "";

    public NavAtlasService(string path) => _path = path;

    public bool Available => File.Exists(_path);

    // ── loading ─────────────────────────────────────────────────────────────

    private List<Place> Places()
    {
        lock (_gate)
        {
            DateTime mtime;
            try { mtime = File.GetLastWriteTimeUtc(_path); } catch { mtime = default; }
            if (_places != null && mtime == _loadedMtime) return _places;
            try
            {
                var (places, generated) = Parse(File.ReadAllText(_path));
                _places = places; _generated = generated; _loadedMtime = mtime;
                AgentLog.Info($"[nav] Atlas read: {places.Count:N0} places ({generated}).");
            }
            catch (Exception ex)
            {
                AgentLog.Warn($"[nav] Atlas unreadable ({_path}): {ex.GetType().Name}: {ex.Message}");
                _places ??= new List<Place>();
            }
            return _places;
        }
    }

    /// <summary>locations.json -> places, each on-map one with its nearest town as the area.</summary>
    internal static (List<Place> Places, string Generated) Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string generated = root.TryGetProperty("generated", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() ?? "" : "";
        var raw = new List<(int Id, string Name, string Type, string Where, bool OnMap, double Ns, double Ew, bool Closed, bool Quest, int MinLevel, string Dest)>();
        if (root.TryGetProperty("locations", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in arr.EnumerateArray())
            {
                bool onMap = TryNum(e, "ns", out double ns) & TryNum(e, "ew", out double ew);
                string dest = e.TryGetProperty("dest", out var d) && d.ValueKind == JsonValueKind.Object ? Str(d, "name") : "";
                raw.Add((Int(e, "id"), Str(e, "name"), Str(e, "type"), Str(e, "place"), onMap, ns, ew, Bool(e, "closed"), Bool(e, "quest"), Int(e, "minLevel"), dest));
            }
        }
        var towns = raw.Where(r => r.OnMap && r.Type == "Town").ToList();
        var places = new List<Place>(raw.Count);
        foreach (var r in raw)
        {
            string area = "";
            if (r.OnMap && towns.Count > 0)
            {
                var near = towns.MinBy(t => Distance(r.Ns, r.Ew, t.Ns, t.Ew));
                double dist = Distance(r.Ns, r.Ew, near.Ns, near.Ew);
                area = r.Type == "Town" && near.Name == r.Name ? "" : dist < 1200 ? "near " + near.Name : "";
            }
            places.Add(new Place(r.Id, r.Name, r.Type, r.Where, r.OnMap, r.Ns, r.Ew, area, r.Closed, r.Quest, r.MinLevel, r.Dest));
        }
        return (places, generated);
    }

    // ── search ──────────────────────────────────────────────────────────────

    /// <summary>RynthNav's Atlas.MatchScore: 0 = not a match.</summary>
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

    /// <summary>RynthNav's Atlas.TypeRank: places before people.</summary>
    public static int TypeRank(string type) => type switch
    {
        "Town" => 0, "Dungeon" => 1, "Landmark" => 2, "Lifestone" => 3, "Bindstone" => 4,
        "Portal" => 5, "Vendor" => 6, "NPC" => 7, _ => 8,
    };

    public static double Distance(double ns1, double ew1, double ns2, double ew2)
    {
        double dn = (ns2 - ns1) * UnitsPerDegree, de = (ew2 - ew1) * UnitsPerDegree;
        return Math.Sqrt(dn * dn + de * de);
    }

    /// <summary>
    /// The best matches for <paramref name="query"/> (at least 2 characters), optionally of one type: best name
    /// match, then type, then nearest to (<paramref name="fromNs"/>, <paramref name="fromEw"/>) when given,
    /// then name. At most <paramref name="max"/>.
    /// </summary>
    internal static List<(Place Place, double? Distance)> Search(IReadOnlyList<Place> places, string query, string? type, double? fromNs, double? fromEw, int max)
    {
        string q = (query ?? "").Trim();
        var hits = new List<(Place P, int S, double? D)>();
        if (q.Length < 2) return new();
        foreach (var p in places)
        {
            if (!string.IsNullOrEmpty(type) && !p.Type.Equals(type, StringComparison.OrdinalIgnoreCase)) continue;
            int s = MatchScore(p.Name, q);
            if (s == 0) continue;
            double? d = p.OnMap && fromNs is double fn && fromEw is double fe ? Distance(fn, fe, p.Ns, p.Ew) : null;
            hits.Add((p, s, d));
        }
        hits.Sort((a, b) =>
        {
            int c = b.S.CompareTo(a.S);
            if (c != 0) return c;
            c = TypeRank(a.P.Type).CompareTo(TypeRank(b.P.Type));
            if (c != 0) return c;
            c = (a.D ?? double.MaxValue).CompareTo(b.D ?? double.MaxValue);
            return c != 0 ? c : string.Compare(a.P.Name, b.P.Name, StringComparison.OrdinalIgnoreCase);
        });
        return hits.Take(Math.Clamp(max, 1, 100)).Select(h => (h.P, h.D)).ToList();
    }

    /// <summary>GET /nav/search's body (schema rynthcore.nav-search/1).</summary>
    public byte[] SearchJson(string query, string? type, double? fromNs, double? fromEw, int max)
    {
        var places = Places();
        var hits = Search(places, query, type, fromNs, fromEw, max);
        return Encoding.UTF8.GetBytes(SearchJson(hits, query, places.Count, _generated));
    }

    internal static string SearchJson(List<(Place Place, double? Distance)> hits, string query, int atlasCount, string generated)
    {
        var ci = CultureInfo.InvariantCulture;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("schema", "rynthcore.nav-search/1");
            w.WriteString("query", query ?? "");
            w.WriteNumber("atlasCount", atlasCount);
            w.WriteString("generated", generated);
            w.WriteNumber("count", hits.Count);
            w.WriteStartArray("results");
            foreach (var (p, d) in hits)
            {
                w.WriteStartObject();
                w.WriteNumber("id", p.Id);
                w.WriteString("name", p.Name);
                w.WriteString("type", p.Type);
                w.WriteString("area", p.Area);
                w.WriteString("where", p.Where);
                w.WriteBoolean("onMap", p.OnMap);
                if (p.OnMap)
                {
                    w.WriteNumber("ns", Math.Round(p.Ns, 2));
                    w.WriteNumber("ew", Math.Round(p.Ew, 2));
                    w.WriteString("coords", Fmt(p.Ns, p.Ew));
                }
                if (d is double dist) w.WriteNumber("distance", Math.Round(dist));
                if (p.DestName.Length > 0) w.WriteString("dest", p.DestName);
                if (p.MinLevel > 0) w.WriteNumber("minLevel", p.MinLevel);
                if (p.Quest) w.WriteBoolean("quest", true);
                if (p.Closed) w.WriteBoolean("closed", true);
                // RynthNav walks only to places with a map position; a place inside a dungeon has none.
                w.WriteBoolean("canTravel", p.OnMap && !p.Closed);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>RynthNav's NavCoords.Fmt: "42.1N, 33.6E".</summary>
    public static string Fmt(double ns, double ew)
    {
        var ci = CultureInfo.InvariantCulture;
        return Math.Abs(ns).ToString("F1", ci) + (ns >= 0 ? "N" : "S") + ", " + Math.Abs(ew).ToString("F1", ci) + (ew >= 0 ? "E" : "W");
    }

    /// <summary>The status frame's absolute world units to map coordinates (RynthNav's own conversion).</summary>
    public static (double Ns, double Ew) FromWorld(double wx, double wy) => ((wy / 24.0 - 1019.5) / 10.0, (wx / 24.0 - 1019.5) / 10.0);

    private static string Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static int Int(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : 0;
    private static bool Bool(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
    private static bool TryNum(JsonElement e, string n, out double d)
    {
        d = 0;
        return e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out d);
    }
}
