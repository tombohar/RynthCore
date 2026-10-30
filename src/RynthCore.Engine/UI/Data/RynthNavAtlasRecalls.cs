// ============================================================================
//  RynthCore.Engine - UI/Data/RynthNavAtlasRecalls.cs
//  NavPlace (one Atlas place) and the recall spells as Atlas places.
//
//  locations.json's "recalls" are the spells that land at a fixed spot. They are
//  searchable places of type "Recall" (2026-10-22): the world database names only
//  the /telepoi towns, so a place like Neftet had no entry and a search for "neft"
//  found nothing, while "Lost City of Neftet Recall" lands right there (22.2S, 6.2E).
//  The spell's name stays the place's name, so it reads as what it is.
//
//  Pure (no engine state), so tools\AtlasRecallsTests compiles it on its own.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace RynthCore.Engine.UI.Data;

/// <summary>One place from locations.json, with its display strings.</summary>
internal sealed class NavPlace
{
    public int Id;
    public string Name = "", Type = "", Desc = "", Place = "", Lb = "", Cell = "";
    public bool OnMap;
    public double Ns, Ew;
    public bool HasDest, DestOnMap;
    public double DestNs, DestEw;
    public string DestName = "";
    public int MinLevel, MaxLevel;
    public bool Quest, Closed;
    public string CoordText = "", DestText = "", LevelText = "";
}

internal static class RynthNavAtlasRecalls
{
    public const string Type = "Recall";

    /// <summary>
    /// The places <paramref name="root"/>'s "recalls" array names: each spell's landing spot,
    /// on the map when it has coordinates, else "in" its dungeon. A spell listed twice at the
    /// same cell is one place; an entry with no name is left out. <paramref name="coord"/>
    /// formats map coordinates (RynthNavStatus.Coord).
    /// </summary>
    public static List<NavPlace> Read(JsonElement root, Func<double, double, string> coord)
    {
        var places = new List<NavPlace>();
        if (!root.TryGetProperty("recalls", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array) return places;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            string name = Str(e, "name").Trim(), cell = Str(e, "cell"), dungeon = Str(e, "in");
            if (name.Length == 0 || !seen.Add(name + "|" + cell)) continue;
            // TryGetInt32 throws (not false) on a non-number: check the kind first.
            int spell = e.TryGetProperty("spell", out JsonElement s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out int id) ? id : 0;
            var p = new NavPlace
            {
                Id = -spell,   // locations have positive ids
                Name = name, Type = Type, Place = Str(e, "place"), Cell = cell,
                Desc = "Where this recall spell lands" + (dungeon.Length > 0 ? ", in " + dungeon : "") + ".",
            };
            if (cell.Length >= 4) p.Lb = cell.Substring(0, 4);
            p.OnMap = Num(e, "ns", out p.Ns) & Num(e, "ew", out p.Ew);
            p.CoordText = p.OnMap ? coord(p.Ns, p.Ew) : dungeon.Length > 0 ? "in " + dungeon : "in a dungeon";
            places.Add(p);
        }
        return places;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool Num(JsonElement e, string name, out double value)
    {
        value = double.NaN;
        return e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out value);
    }
}
