using System.Globalization;
using System.Text.Json;
using RynthCore.Engine.UI.Data;

namespace AtlasRecallsTests;

// Offline tests for the recall spells as Atlas places. See the csproj.
internal static class Program
{
    private static int _fails, _asserts;
    private static void Check(bool cond, string msg)
    {
        _asserts++;
        if (!cond) { _fails++; Console.WriteLine($"  [FAIL] {msg}"); }
    }
    private static void Eq<T>(T actual, T expected, string msg) =>
        Check(EqualityComparer<T>.Default.Equals(actual, expected), $"{msg}: expected '{expected}', got '{actual}'");

    // Stands in for RynthNavStatus.Coord (the engine's "22.2S, 6.2E").
    private static string Coord(double ns, double ew) =>
        string.Format(CultureInfo.InvariantCulture, "{0:0.0}{1}, {2:0.0}{3}", Math.Abs(ns), ns < 0 ? "S" : "N", Math.Abs(ew), ew < 0 ? "W" : "E");

    private static int Main()
    {
        Sample();
        Malformed();
        ShippedAtlas();
        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? $"PASS ({_asserts} checks)" : $"FAIL ({_fails} of {_asserts} checks failed)");
        return _fails == 0 ? 0 : 1;
    }

    // Shaped like tools\navdata\gen_locations.py's "recalls" (spell, name, cell, place, ns/ew or in).
    private const string Json = """
    {"locations":[{"id":1,"name":"Holtburg","type":"Town","cell":"A9B40019","place":"outdoor","ns":42.1,"ew":33.6}],
     "recalls":[
      {"spell":5541,"name":"Lost City of Neftet Recall","cell":"8763000E","place":"outdoor","ns":-22.21,"ew":6.22},
      {"spell":2479,"name":"Advance Camp Recall","cell":"027B0200","place":"dungeon","in":"Vile-Smelling Refuse"},
      {"spell":1635,"name":"Ulgrim's Recall","cell":"01960118","place":"dungeon"},
      {"spell":1636,"name":"Ulgrim's Recall","cell":"01960118","place":"dungeon"},
      {"spell":2023,"name":"Recall the Sanctuary","cell":"F4180104","place":"building","ns":-82.55,"ew":93.4}
     ]}
    """;

    private static void Sample()
    {
        using var doc = JsonDocument.Parse(Json);
        List<NavPlace> places = RynthNavAtlasRecalls.Read(doc.RootElement, Coord);
        Eq(places.Count, 4, "four places (Ulgrim's Recall twice at one cell is one)");
        Check(places.All(p => p.Type == "Recall"), "all of type Recall");
        Check(places.All(p => p.Id < 0), "negative ids (locations have positive ones)");

        NavPlace neftet = places.Single(p => p.Name == "Lost City of Neftet Recall");
        Check(neftet.OnMap, "Neftet's recall is on the map");
        Check(Math.Abs(neftet.Ns + 22.21) < 1e-9 && Math.Abs(neftet.Ew - 6.22) < 1e-9, "at 22.21S, 6.22E");
        Eq(neftet.CoordText, "22.2S, 6.2E", "its coordinates as the engine shows them");
        Eq(neftet.Id, -5541, "id = -spell");
        Eq(neftet.Lb, "8763", "landblock from the cell");
        Check(neftet.Name.Contains("neft", StringComparison.OrdinalIgnoreCase), "\"neft\" is in its name (Skippy's search)");

        NavPlace camp = places.Single(p => p.Name == "Advance Camp Recall");
        Check(!camp.OnMap, "a dungeon recall is off the map");
        Eq(camp.CoordText, "in Vile-Smelling Refuse", "named for its dungeon");
        Check(camp.Desc.Contains("Vile-Smelling Refuse"), "and its description says so");

        Eq(places.Single(p => p.Name == "Ulgrim's Recall").CoordText, "in a dungeon", "a dungeon recall without a name");
        Check(places.Single(p => p.Name == "Recall the Sanctuary").OnMap, "a building on the map is on the map");
    }

    private static void Malformed()
    {
        using (var none = JsonDocument.Parse("""{"locations":[]}"""))
            Eq(RynthNavAtlasRecalls.Read(none.RootElement, Coord).Count, 0, "no \"recalls\": none");
        using (var wrong = JsonDocument.Parse("""{"recalls":{"name":"x"}}"""))
            Eq(RynthNavAtlasRecalls.Read(wrong.RootElement, Coord).Count, 0, "\"recalls\" not an array: none");
        using var bad = JsonDocument.Parse("""
        {"recalls":[ 7, null, {"spell":1}, {"name":"  "}, {"spell":"x","name":"Odd Recall","ns":"north","ew":3},
                     {"name":"No Spell Recall","ns":1.5,"ew":-2.5} ]}
        """);
        List<NavPlace> places = RynthNavAtlasRecalls.Read(bad.RootElement, Coord);
        Eq(places.Count, 2, "non-objects and unnamed entries left out");
        NavPlace odd = places.Single(p => p.Name == "Odd Recall");
        Check(!odd.OnMap, "a non-number coordinate: off the map");
        Eq(odd.Id, 0, "a non-number spell: id 0");
        Eq(places.Single(p => p.Name == "No Spell Recall").CoordText, "1.5N, 2.5W", "no spell, still placed");
    }

    // The NavData the release ships (read only), when this PC has it.
    private static void ShippedAtlas()
    {
        string path = @"C:\Games\RynthCore\NavData\locations.json";
        if (!File.Exists(path)) { Console.WriteLine("  (skipped the shipped Atlas: no " + path + ")"); return; }
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        List<NavPlace> places = RynthNavAtlasRecalls.Read(doc.RootElement, Coord);
        Check(places.Count >= 20, $"the shipped Atlas has recall places ({places.Count})");
        Check(places.Any(p => p.OnMap && p.Name.Contains("neft", StringComparison.OrdinalIgnoreCase)),
              "\"neft\" finds a place on the map (Lost City of Neftet Recall)");
        var ids = new HashSet<int>();
        foreach (JsonElement e in doc.RootElement.GetProperty("locations").EnumerateArray())
            if (e.TryGetProperty("id", out JsonElement id) && id.TryGetInt32(out int n)) ids.Add(n);
        Check(places.All(p => !ids.Contains(p.Id)), "no recall id collides with a location's");
    }
}
