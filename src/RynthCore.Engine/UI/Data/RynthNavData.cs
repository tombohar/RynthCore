// ============================================================================
//  RynthCore.Engine - UI/Data/RynthNavData.cs
//  The RynthNav plugin's status (pose, loaded navmesh tile, status line, last
//  path, run/turn state; and its travel side: the arrow, the last planned
//  route, the recalls this character can use, favorites and recent, where its
//  Atlas lives; and, from 0.6.1, the Go tab's status line, the current step's
//  distance left, portal ties and the Info section's NavData facts) for both
//  RynthNav faces, and its commands.
//
//  RynthNavGetStatusJson (and RynthNavGetStatusJsonUtf8, 0.6.1+, tried first)
//  frees its previous buffer on each call, so the hub is its only caller
//  (RynthNavSource, 250 ms). Load tile / test / preview / go /
//  move / command run on the pump too. RynthNavArrowWatch reads one int (how
//  many times a command asked to show the arrow) twice a second, always, so
//  "/rnav arrow Holtburg" opens the arrow overlay with every RynthNav face shut.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.UI.Data;

/// <summary>A place RynthNav keeps (favorites, recent) or can recall to.</summary>
internal sealed record RynthNavPlace(string Name, string Type, double Ns, double Ew, string Detail = "")
{
    /// <summary>"42.1N, 33.6E".</summary>
    public string CoordText { get; } = RynthNavStatus.Coord(Ns, Ew);
}

/// <summary>One leg of the last planned route.</summary>
internal sealed record RynthNavRouteStep(string Kind, string Label, double Ns, double Ew, bool HasLanding, double LandNs, double LandEw)
{
    /// <summary>What to do, as the route view shows it.</summary>
    public string Text { get; } = Kind switch
    {
        "recall" => "Cast " + Label + (HasLanding ? "  →  " + RynthNavStatus.Coord(LandNs, LandEw) : ""),
        "portal" => "Take portal " + Label + "  at " + RynthNavStatus.Coord(Ns, Ew) + (HasLanding ? "  →  " + RynthNavStatus.Coord(LandNs, LandEw) : ""),
        // Town Network legs (a later RynthNav): shown as they come; unknown kinds read as a leg to take.
        "town" => "Town Network: " + Label + (HasLanding ? "  →  " + RynthNavStatus.Coord(LandNs, LandEw) : ""),
        "walk" or "" => "Walk to " + RynthNavStatus.Coord(Ns, Ew),
        _ => (Label.Length > 0 ? Label : Kind) + "  at " + RynthNavStatus.Coord(Ns, Ew),
    };
}

/// <summary>A recall spot RynthNav learned for this character (portal ties, @lifestone, house...). RynthNav 0.6.1+.</summary>
internal sealed record RynthNavRecallSpot(string Slot, string Name, bool OnMap, double Ns, double Ew, string At)
{
    /// <summary>"Holtburg Lifestone, 42.1N, 33.6E" or "Matron Hive (a dungeon)".</summary>
    public string Where { get; } = OnMap
        ? (At.Length > 0 ? At + ", " : "") + RynthNavStatus.Coord(Ns, Ew)
        : (At.Length > 0 ? At : "somewhere") + " (a dungeon)";
}

/// <summary>What the arrow points at now (a place, a portal on the way, or a recall to cast).</summary>
internal sealed class RynthNavArrow
{
    public string Name = "", Type = "", Kind = "place", Hint = "", Final = "", FinalType = "";
    public bool OnMap;
    public double Ns, Ew, FinalNs, FinalEw, ArriveYards = 10;
    public int Step = 1, Steps = 1;
    public string StepText = "", CoordText = "", ThenText = "";
}

internal sealed class RynthNavStatus
{
    public bool HasPose, TileLoaded;
    public int PolyCount, RunState, TurnState;
    public string Landblock = "----", LoadedLb = "----", Status = "", LastPath = "";
    public double NS, EW;
    public bool Goto;

    // Travel (RynthNav 0.6+; absent from older plugins: Travel stays false).
    public bool Travel;
    public string AtlasPath = "", AtlasStatus = "", AtlasGenerated = "";
    public int AtlasCount;
    public bool PortalsOn, RecallsOn = true;
    // Avoidance settings (RynthNav 0.6.4+; HasAvoid false on older plugins).
    public bool HasAvoid, AvoidPortals, AvoidWalls;
    public double AvoidPortalM, AvoidWallM, CornerM;
    public int OpenArrow;
    public RynthNavArrow? Arrow;
    public string RouteFor = "", RouteSummary = "";
    /// <summary>The walking path RynthNav worked out for the trip: segments (one per walk), each a flat list of ns, ew pairs in degrees. Empty: none (older RynthNav, or no trip on the ground).</summary>
    public float[][] PathSegments = Array.Empty<float[]>();
    public bool RouteActive;
    public int RouteIdx = -1;
    public RynthNavRouteStep[] Route = Array.Empty<RynthNavRouteStep>();
    public RynthNavPlace[] Recalls = Array.Empty<RynthNavPlace>();
    public string[] RecallNotes = Array.Empty<string>();
    public RynthNavPlace[] Favorites = Array.Empty<RynthNavPlace>();
    public RynthNavPlace[] Recent = Array.Empty<RynthNavPlace>();
    // The route view's lines, built here (not per frame): the header, one per step, one per recall.
    public string RouteHeader = "", RecallHeader = "RECALLS FOR ROUTES (0)";
    public string[] RouteLines = Array.Empty<string>(), RecallLines = Array.Empty<string>();

    // Panel (RynthNav 0.6.1+; absent from older plugins: Panel, HasInfo and HasTies stay false).
    public bool Panel;
    public string Version = "";
    public string GoLine = "", GoPhase = "";
    public double GoLeft = -1;
    public bool HasInfo, GraphLoaded, Indoors;
    public int TileHere = -1, OnDisk = -1;
    public string NavDir = "", NavSource = "", GraphStatus = "";
    // The Town Network (RynthNav 0.6.2+): townnet.json loaded or not, and why.
    public bool HasTownNet, TownNetLoaded;
    public string TownNetStatus = "";
    // Path markers and "save as nav route" (RynthNav 0.6.6+; HasMarkers / HasSave false on older plugins).
    public bool HasMarkers, MarkersOn, MarkerLabels, MarkerGuide, MarkersShown;
    public string MarkerStatus = "";
    // The navmesh drawn in the world (/rnav mesh; RynthNav 0.6.7+ status "mesh"; HasMesh false on older plugins).
    public bool HasMesh, MeshOn, MeshFloor, MeshWalls;
    public int MeshRadius = 40, MeshTriangles = 3000, MeshDrawn;
    public bool HasSave, CanSave, SaveBusy;
    public string SaveFor = "", SaveName = "", SaveConflict = "", SaveLast = "";
    public bool HasTies;
    public int Tie1Known = -1, Tie2Known = -1;
    public string TiePending = "";
    public RynthNavRecallSpot[] Mine = Array.Empty<RynthNavRecallSpot>();
    // Built once per status for the Go tab.
    /// <summary>The status line: what RynthNav is doing, or how the last goto ended.</summary>
    public string StatusLine = "";
    /// <summary>The current route step's "340 yd left" (empty when not walking a step).</summary>
    public string StepLeft = "";
    public string Tie1Text = "", Tie2Text = "", TiePendingText = "";
    public string[] MineLines = Array.Empty<string>();
    public string InfoHere = "", InfoNavData = "", InfoTiles = "", InfoGraph = "", InfoTownNet = "", InfoAtlas = "", InfoStatus = "";

    // Display strings, built once per status.
    public string PlayerText = "— no pose —", MeshText = "not loaded", PathText = "";
    public bool OnMap = true;

    public static RynthNavStatus? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement r = doc.RootElement;
            var s = new RynthNavStatus();
            if (r.TryGetProperty("hasPose", out var v)) s.HasPose = v.GetInt32() != 0;
            if (r.TryGetProperty("landblock", out v)) s.Landblock = v.GetString() ?? "----";
            if (r.TryGetProperty("ns", out v)) s.NS = v.GetDouble();
            if (r.TryGetProperty("ew", out v)) s.EW = v.GetDouble();
            if (r.TryGetProperty("tileLoaded", out v)) s.TileLoaded = v.GetInt32() != 0;
            if (r.TryGetProperty("loadedLb", out v)) s.LoadedLb = v.GetString() ?? "----";
            if (r.TryGetProperty("polyCount", out v)) s.PolyCount = v.GetInt32();
            if (r.TryGetProperty("status", out v)) s.Status = v.GetString() ?? "";
            if (r.TryGetProperty("lastPath", out v)) s.LastPath = v.GetString() ?? "";
            if (r.TryGetProperty("runState", out v)) s.RunState = v.GetInt32();
            if (r.TryGetProperty("turnState", out v)) s.TurnState = v.GetInt32();
            if (r.TryGetProperty("goto", out v)) s.Goto = v.GetInt32() != 0;
            // RynthNav 0.6.3+: "onMap" 0 in a dungeon, where ns/ew aren't map coordinates.
            s.OnMap = !r.TryGetProperty("onMap", out v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != 0;
            s.PlayerText = !s.HasPose ? "— no pose —"
                : s.OnMap ? $"0x{s.Landblock}   {Loc(s.NS, 'N', 'S')}  {Loc(s.EW, 'E', 'W')}"
                : $"0x{s.Landblock}   in a dungeon (no map position: routes start with a recall)";
            // polyCount is RynthNav's loaded TILE count (a window of landblocks around
            // the player), not a polygon count; it was labelled "polys".
            s.MeshText = s.TileLoaded ? $"{s.PolyCount} tiles around 0x{s.LoadedLb}" : "not loaded";
            s.PathText = string.IsNullOrEmpty(s.LastPath) ? "" : "↳ " + s.LastPath;
            ParseTravel(r, s);
            ParsePanel(r, s);
            return s;
        }
        catch { return null; }   // transient partial JSON
    }

    private static void ParseTravel(JsonElement r, RynthNavStatus s)
    {
        if (!r.TryGetProperty("atlas", out var atlas) || atlas.ValueKind != JsonValueKind.Object) return;
        s.Travel = true;
        s.AtlasPath = Str(atlas, "path");
        s.AtlasStatus = Str(atlas, "status");
        s.AtlasGenerated = Str(atlas, "generated");
        s.AtlasCount = Int(atlas, "count");
        s.PortalsOn = Int(r, "portalsOn") != 0;
        s.RecallsOn = Int(r, "recallsOn") != 0;
        s.HasAvoid = r.TryGetProperty("avoidPortals", out _);
        s.AvoidPortals = Int(r, "avoidPortals") != 0;
        s.AvoidWalls = Int(r, "avoidWalls") != 0;
        s.AvoidPortalM = Num(r, "avoidPortalM");
        s.AvoidWallM = Num(r, "avoidWallM");
        s.CornerM = Num(r, "cornerM");
        s.OpenArrow = Int(r, "openArrow");

        if (r.TryGetProperty("arrow", out var a) && a.ValueKind == JsonValueKind.Object)
        {
            var ar = new RynthNavArrow
            {
                Name = Str(a, "name"), Type = Str(a, "type"), Kind = Str(a, "kind"), Hint = Str(a, "hint"),
                Final = Str(a, "final"), FinalType = Str(a, "finalType"),
                OnMap = Int(a, "onMap") != 0, Ns = Num(a, "ns"), Ew = Num(a, "ew"),
                FinalNs = Num(a, "finalNs"), FinalEw = Num(a, "finalEw"),
                Step = Int(a, "step"), Steps = Int(a, "steps"), ArriveYards = Num(a, "arrive"),
            };
            ar.CoordText = ar.OnMap ? Coord(ar.Ns, ar.Ew) : "";
            ar.StepText = ar.Steps > 1 ? $"step {ar.Step} of {ar.Steps}, to {ar.Final}" : "";
            ar.ThenText = ar.Steps > 1 ? "then " + ar.Final + "  " + Coord(ar.FinalNs, ar.FinalEw) : "";
            s.Arrow = ar;
        }

        if (r.TryGetProperty("route", out var route) && route.ValueKind == JsonValueKind.Object)
        {
            s.RouteFor = Str(route, "for");
            s.RouteActive = Int(route, "active") != 0;
            s.RouteIdx = Int(route, "idx", -1);
            var steps = new List<RynthNavRouteStep>();
            int teleports = 0;
            if (route.TryGetProperty("steps", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (JsonElement e in arr.EnumerateArray())
                {
                    bool land = e.TryGetProperty("lns", out _);
                    var step = new RynthNavRouteStep(Str(e, "k"), Str(e, "l"), Num(e, "ns"), Num(e, "ew"), land, Num(e, "lns"), Num(e, "lew"));
                    if (step.Kind != "walk") teleports++;
                    steps.Add(step);
                }
            s.Route = steps.ToArray();
            double est = Num(route, "est");
            s.RouteSummary = teleports == 0
                ? $"walk, about {Yards(est)}"
                : $"{teleports} teleport{(teleports == 1 ? "" : "s")}, about {Yards(est)} of effort";
            s.RouteHeader = "To " + s.RouteFor + ": " + s.RouteSummary + (s.RouteActive ? "  (walking)" : "");
            var lines = new string[s.Route.Length];
            for (int i = 0; i < lines.Length; i++)
            {
                RynthNavRouteStep st = s.Route[i];
                string icon = st.Kind == "recall" ? StepIconRecall : st.Kind == "portal" ? StepIconPortal
                    : st.Kind == "town" ? StepIconTown : StepIconWalk;
                lines[i] = icon + "  " + (i + 1).ToString(CultureInfo.InvariantCulture) + ". " + st.Text;
            }
            s.RouteLines = lines;
        }

        s.Recalls = Places(r, "recalls", recall: true);
        s.RecallHeader = "RECALLS FOR ROUTES (" + s.Recalls.Length.ToString(CultureInfo.InvariantCulture) + ")";
        var recallLines = new string[s.Recalls.Length];
        for (int i = 0; i < recallLines.Length; i++)
            recallLines[i] = StepIconRecall + "  " + s.Recalls[i].Name + "  →  " + s.Recalls[i].CoordText + "   (" + s.Recalls[i].Detail + ")";
        s.RecallLines = recallLines;
        if (r.TryGetProperty("recallNotes", out var notes) && notes.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (JsonElement n in notes.EnumerateArray()) list.Add(n.GetString() ?? "");
            s.RecallNotes = list.ToArray();
        }
        s.Favorites = Places(r, "favs", recall: false);
        s.Recent = Places(r, "recent", recall: false);
    }

    /// <summary>
    /// The Go tab's fields (RynthNav 0.6.1+): the status line, the current step's distance
    /// left, the Info section (NavData, route graph, the tile here) and the Recalls section's
    /// portal ties and learned recall spots. Older RynthNavs: the status line falls back to
    /// the technical status, and the sections say what an update adds.
    /// </summary>
    private static void ParsePanel(JsonElement r, RynthNavStatus s)
    {
        s.Version = Str(r, "v");
        if (r.TryGetProperty("go", out var go) && go.ValueKind == JsonValueKind.Object)
        {
            s.Panel = true;
            s.GoLine = Str(go, "line");
            s.GoPhase = Str(go, "phase");
            s.GoLeft = go.TryGetProperty("left", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetDouble() : -1;
        }
        s.StatusLine = s.Panel ? (s.GoLine.Length > 0 ? s.GoLine : s.Goto ? "walking" : "ready") : s.Status;
        // "town": walking inside the Town Network (RynthNav 0.6.2+).
        s.StepLeft = s.Goto && (s.GoPhase == "walk" || s.GoPhase == "town") && s.GoLeft >= 0 ? Yards(s.GoLeft) + " left" : "";

        if (r.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object)
        {
            s.HasInfo = true;
            s.NavDir = Str(info, "dir");
            s.NavSource = Str(info, "source");
            s.GraphLoaded = Int(info, "graph") != 0;
            s.GraphStatus = Str(info, "graphStatus");
            s.TileHere = Int(info, "tileHere", -1);
            s.Indoors = Int(info, "indoors") != 0;
            s.OnDisk = Int(info, "onDisk", -1);
            s.HasTownNet = info.TryGetProperty("townNet", out _);
            s.TownNetLoaded = Int(info, "townNet") != 0;
            s.TownNetStatus = Str(info, "townNetStatus");
        }
        if (r.TryGetProperty("markers", out var mk) && mk.ValueKind == JsonValueKind.Object)
        {
            s.HasMarkers = true;
            s.MarkersOn = Int(mk, "on") != 0;
            s.MarkerLabels = Int(mk, "labels") != 0;
            s.MarkerGuide = Int(mk, "guide") != 0;
            s.MarkersShown = Int(mk, "shown") != 0;
            s.MarkerStatus = Str(mk, "status");
        }
        if (r.TryGetProperty("path", out var pa) && pa.ValueKind == JsonValueKind.Array)
        {
            var segs = new List<float[]>();
            foreach (JsonElement seg in pa.EnumerateArray())
            {
                if (seg.ValueKind != JsonValueKind.Array) continue;
                var arr = new float[seg.GetArrayLength() & ~1];     // whole ns, ew pairs
                int i = 0;
                foreach (JsonElement v in seg.EnumerateArray())
                {
                    if (i >= arr.Length) break;
                    arr[i++] = v.ValueKind == JsonValueKind.Number ? (float)v.GetDouble() : 0f;
                }
                if (arr.Length >= 4) segs.Add(arr);
            }
            s.PathSegments = segs.ToArray();
        }
        if (r.TryGetProperty("mesh", out var ms) && ms.ValueKind == JsonValueKind.Object)
        {
            s.HasMesh = true;
            s.MeshOn = Int(ms, "on") != 0;
            s.MeshFloor = Int(ms, "floor", 1) != 0;
            s.MeshWalls = Int(ms, "walls", 1) != 0;
            s.MeshRadius = Int(ms, "radius", 40);
            s.MeshTriangles = Int(ms, "triangles", 3000);
            s.MeshDrawn = Int(ms, "drawn");
        }
        if (r.TryGetProperty("save", out var sv) && sv.ValueKind == JsonValueKind.Object)
        {
            s.HasSave = true;
            s.CanSave = Int(sv, "can") != 0;
            s.SaveBusy = Int(sv, "busy") != 0;
            s.SaveFor = Str(sv, "for");
            s.SaveName = Str(sv, "name");
            s.SaveConflict = Str(sv, "conflict");
            s.SaveLast = Str(sv, "last");
        }
        string meshHere = !s.HasInfo ? (s.TileLoaded ? "loaded" : "not loaded")
            : s.TileHere == 1 ? "yes" : s.TileHere == 0 ? "no (no tile for this landblock)" : "not checked yet";
        s.InfoHere = s.HasPose
            ? $"0x{s.Landblock}{(s.Indoors ? " (indoors)" : "")}  \u00B7  navmesh here: {meshHere}  \u00B7  {s.PolyCount} tile{(s.PolyCount == 1 ? "" : "s")} loaded"
            : "no position yet";
        s.InfoNavData = s.HasInfo ? s.NavDir + (s.NavSource.Length > 0 ? "  (" + s.NavSource + ")" : "") : "update RynthNav for the NavData folder";
        s.InfoTiles = !s.HasInfo ? "" : s.OnDisk >= 0 ? $"{s.OnDisk} tile files in the folder" : "tile files: counting\u2026";
        s.InfoGraph = s.HasInfo ? (s.GraphLoaded ? "loaded: " : "not loaded: ") + s.GraphStatus : "update RynthNav for the route graph";
        s.InfoTownNet = s.HasTownNet ? (s.TownNetLoaded ? "loaded: " : "not loaded: ") + s.TownNetStatus : "update RynthNav (0.6.2) for Town Network routes";
        s.InfoAtlas = !s.Travel ? "update RynthNav for the Atlas"
            : $"{s.AtlasCount} places" + (s.AtlasStatus.Length > 0 && !s.AtlasStatus.EndsWith(" places", StringComparison.Ordinal) ? "  (" + s.AtlasStatus + ")" : "")
              + (s.AtlasGenerated.Length >= 10 ? ", from " + s.AtlasGenerated.Substring(0, 10) : "");
        s.InfoStatus = s.Status + (s.LastPath.Length > 0 ? "  \u00B7  " + s.LastPath : "");

        if (r.TryGetProperty("mine", out var mine) && mine.ValueKind == JsonValueKind.Array)
        {
            s.HasTies = true;
            var spots = new List<RynthNavRecallSpot>();
            foreach (JsonElement e in mine.EnumerateArray())
                spots.Add(new RynthNavRecallSpot(Str(e, "s"), Str(e, "n"), Int(e, "on") != 0, Num(e, "ns"), Num(e, "ew"), Str(e, "at")));
            s.Mine = spots.ToArray();
            if (r.TryGetProperty("ties", out var ties) && ties.ValueKind == JsonValueKind.Object)
            {
                s.Tie1Known = Int(ties, "t1", -1);
                s.Tie2Known = Int(ties, "t2", -1);
            }
            s.TiePending = Str(r, "tiePending");
            s.Tie1Text = TieText("Primary", "Tie1", s.Tie1Known, s.Mine);
            s.Tie2Text = TieText("Secondary", "Tie2", s.Tie2Known, s.Mine);
            s.TiePendingText = s.TiePending.Length > 0 ? "Portal tie noted: " + s.TiePending + ". Which tie was it?" : "";
            var lines = new List<string>();
            foreach (RynthNavRecallSpot m in s.Mine)
                if (m.Slot != "Tie1" && m.Slot != "Tie2") lines.Add(StepIconRecall + "  " + m.Name + ":  " + m.Where);
            s.MineLines = lines.ToArray();
        }
    }

    private static string TieText(string which, string slot, int known, RynthNavRecallSpot[] mine)
    {
        foreach (RynthNavRecallSpot m in mine)
            if (m.Slot == slot) return which + " tie:  " + m.Where;
        return which + " tie:  " + (known == 0 ? "you don't know " + which + " Portal Tie" : "not tied yet (cast " + which + " Portal Tie on a portal)");
    }

    // Phosphor glyphs (ImGui/PhosphorIcons.cs: MagicWand, Spiral, Footprints, CastleTurret), kept here
    // so this data class needs nothing from the ImGui layer.
    private const string StepIconRecall = "\uE6B6", StepIconPortal = "\uE9FA", StepIconWalk = "\uEA88", StepIconTown = "\uE9D0";

    private static RynthNavPlace[] Places(JsonElement r, string key, bool recall)
    {
        if (!r.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array) return Array.Empty<RynthNavPlace>();
        var list = new List<RynthNavPlace>();
        foreach (JsonElement e in arr.EnumerateArray())
            list.Add(new RynthNavPlace(Str(e, "n"), recall ? "Recall" : Str(e, "t"), Num(e, "ns"), Num(e, "ew"),
                recall ? Str(e, "how") + (Int(e, "learned") != 0 ? ", learned" : "") : ""));
        return list.ToArray();
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement e, string name, int fallback = 0) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : fallback;

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) ? d : 0;

    private static string Loc(double v, char pos, char neg) =>
        v.ToString("F1", CultureInfo.InvariantCulture).TrimStart('-') + (v >= 0 ? pos : neg);

    /// <summary>"42.1N, 33.6E".</summary>
    public static string Coord(double ns, double ew) => Loc(ns, 'N', 'S') + ", " + Loc(ew, 'E', 'W');

    /// <summary>What RynthNav's commands take: "42.08N, 33.60E" (two decimals, so a place's own spot).</summary>
    public static string CommandCoord(double ns, double ew) =>
        Math.Abs(ns).ToString("F2", CultureInfo.InvariantCulture) + (ns >= 0 ? "N" : "S") + ", "
        + Math.Abs(ew).ToString("F2", CultureInfo.InvariantCulture) + (ew >= 0 ? "E" : "W");

    public static string Yards(double units) =>
        units < 1000 ? units.ToString("0", CultureInfo.InvariantCulture) + " yd"
                     : (units / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "k yd";
}

/// <summary>RynthNavGetStatusJson while a RynthNav face is open (250 ms).</summary>
internal sealed unsafe class RynthNavSource : UiSource<RynthNavStatus>
{
    // RynthNav 0.6.1+ also has a UTF-8 getter (names with characters the ANSI code page lacks);
    // older ones only the ANSI one. Each frees its own previous buffer; only one is called.
    private delegate* unmanaged[Cdecl]<IntPtr> _get;
    private bool _utf8;
    private string? _lastJson;

    public RynthNavSource() : base("RynthNav", periodMs: 250) { }

    protected internal override void Poll()
    {
        if (_get == null)
        {
            _get = (delegate* unmanaged[Cdecl]<IntPtr>)PluginExportBinder.Resolve("RynthNav", "RynthNavGetStatusJsonUtf8");
            _utf8 = _get != null;
            if (_get == null)
                _get = (delegate* unmanaged[Cdecl]<IntPtr>)PluginExportBinder.Resolve("RynthNav", "RynthNavGetStatusJson");
        }
        if (_get == null) return;
        IntPtr ptr = _get();
        string? json = ptr == IntPtr.Zero ? null : _utf8 ? Marshal.PtrToStringUTF8(ptr) : Marshal.PtrToStringAnsi(ptr);
        if (string.IsNullOrEmpty(json) || json == _lastJson) return;
        RynthNavStatus? status = RynthNavStatus.Parse(json);
        if (status == null) return;
        _lastJson = json;
        Publish(status);
    }

    protected internal override void Reset()
    {
        _get = null;
        _utf8 = false;
        _lastJson = null;
        ClearSnapshot();
    }
}

internal static unsafe class RynthNavCommands
{
    private static delegate* unmanaged[Cdecl]<void> _loadTile, _testQuery;
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _preview, _goto, _command;
    private static delegate* unmanaged[Cdecl]<int, void> _move;

    static RynthNavCommands()
    {
        PluginManager.PluginsUnloaded += () => { _loadTile = null; _testQuery = null; _preview = null; _goto = null; _move = null; _command = null; };
    }

    private static IntPtr Export(string name) => PluginExportBinder.Resolve("RynthNav", name);

    private static void Run(string label, Action action) => UiDataHub.Post("RynthNav " + label, () =>
    {
        action();
        UiSources.RynthNav.RequestRefresh();
    });

    public static void LoadTile() => Run("load tile", () =>
    {
        if (_loadTile == null) _loadTile = (delegate* unmanaged[Cdecl]<void>)Export("RynthNavLoadTile");
        if (_loadTile != null) _loadTile();
    });

    public static void TestQuery() => Run("test", () =>
    {
        if (_testQuery == null) _testQuery = (delegate* unmanaged[Cdecl]<void>)Export("RynthNavTestQuery");
        if (_testQuery != null) _testQuery();
    });

    /// <summary>Preview a route to a /loc coordinate ("42.5N, 33.6E").</summary>
    public static void Preview(string coord) => Run("preview", () =>
    {
        if (_preview == null) _preview = (delegate* unmanaged[Cdecl]<IntPtr, void>)Export("RynthNavPreviewPath");
        if (_preview == null) return;
        IntPtr p = Marshal.StringToHGlobalAnsi(coord);
        try { _preview(p); }
        finally { Marshal.FreeHGlobal(p); }
    });

    /// <summary>Walk the navmesh path to a /loc coordinate.</summary>
    public static void Goto(string coord) => Run("go", () =>
    {
        if (_goto == null) _goto = (delegate* unmanaged[Cdecl]<IntPtr, void>)Export("RynthNavGoto");
        if (_goto == null) return;
        IntPtr p = Marshal.StringToHGlobalAnsi(coord);
        try { _goto(p); }
        finally { Marshal.FreeHGlobal(p); }
    });

    /// <summary>1 forward, 2 back, 3 turn left, 4 turn right, 5 stop.</summary>
    public static void Move(int cmd) => Run("move", () =>
    {
        if (_move == null) _move = (delegate* unmanaged[Cdecl]<int, void>)Export("RynthNavMove");
        if (_move != null) _move(cmd);
    });

    /// <summary>
    /// A /rnav command without the "/rnav" ("arrow 42.08N, 33.60E Holtburg", "go Holtburg",
    /// "fav ...", "portals on"). RynthNav 0.6+ (RynthNavCommand); RynthNav queues it for its
    /// tick, so the status shows the result a poll or two later.
    /// </summary>
    public static void Command(string text) => Run("command", () =>
    {
        if (_command == null) _command = (delegate* unmanaged[Cdecl]<IntPtr, void>)Export("RynthNavCommand");
        if (_command == null) return;
        IntPtr p = Marshal.StringToCoTaskMemUTF8(text);
        try { _command(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    });

    public static void Arrow(string name, double ns, double ew) =>
        Command("arrow " + RynthNavStatus.CommandCoord(ns, ew) + " " + name);

    public static void Go(string name, double ns, double ew) =>
        Command("go " + RynthNavStatus.CommandCoord(ns, ew) + " " + name);

    public static void Route(string name, double ns, double ew) =>
        Command("route " + RynthNavStatus.CommandCoord(ns, ew) + " " + name);

    public static void ToggleFavorite(string name, double ns, double ew) =>
        Command("fav " + RynthNavStatus.CommandCoord(ns, ew) + " " + name);
}

/// <summary>
/// Opens the arrow overlay when a RynthNav command asks to show the arrow (any arrow
/// set: /rnav arrow, go, the Atlas, the chat menu), even with every RynthNav face shut.
/// RynthNavArrowSeq is one int; polled twice a second while RynthNav is loaded.
/// </summary>
internal sealed unsafe class RynthNavArrowWatch : UiSource<object>
{
    private delegate* unmanaged[Cdecl]<int> _get;
    private int _last = int.MinValue;
    private long _retryAtMs;

    /// <summary>Set by the face registration: what to call when the arrow should show. Pump thread.</summary>
    public static Action? ShowArrow;

    public RynthNavArrowWatch() : base("RynthNavArrowWatch", periodMs: 500) { }

    protected internal override void Poll()
    {
        if (_get == null)
        {
            // Not loaded yet, or an older RynthNav (no overlay auto-open): look again later.
            long now = Environment.TickCount64;
            if (now < _retryAtMs) return;
            _get = (delegate* unmanaged[Cdecl]<int>)PluginExportBinder.Resolve("RynthNav", "RynthNavArrowSeq");
            if (_get == null) { _retryAtMs = now + 10000; return; }
        }
        int seq = _get();
        if (_last != int.MinValue && seq != _last) ShowArrow?.Invoke();
        _last = seq;
    }

    protected internal override void Reset()
    {
        _get = null;
        _retryAtMs = 0;
        _last = int.MinValue;
    }
}
