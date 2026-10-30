// ============================================================================
//  RynthCore.Engine - UI/Data/RadarSnapshotJson.cs
//  The radar snapshot's JSON shape, as RynthAi's RynthPluginGetRadarSnapshot
//  writes it (mirrors RynthCore.Plugin.RynthAi.LegacyUi.RadarSnapshotPayload).
//  Split out of RadarData.cs so it stays self-contained: RynthSuite's
//  RynthAiHostTests links this file to check the plugin's output parses here.
//
//  Compatibility: System.Text.Json skips properties it doesn't know (the
//  default, never set to Disallow here), and a missing property keeps its
//  default. So fields can be added on either side: an older engine ignores
//  a newer plugin's extra fields, and a newer engine reads an older plugin's
//  snapshot with the new fields at their defaults (marker Id = 0: unknown).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RynthCore.Engine.UI.Data;

internal sealed class RadarSnapshot
{
    [JsonPropertyName("mapVersion")]       public uint MapVersion       { get; set; }
    [JsonPropertyName("geometryIncluded")] public bool GeometryIncluded { get; set; }
    [JsonPropertyName("isIndoor")]         public bool IsIndoor         { get; set; }
    [JsonPropertyName("player")]           public RadarPlayer Player    { get; set; } = new();
    [JsonPropertyName("ns")]               public double Ns             { get; set; } = double.NaN;
    [JsonPropertyName("ew")]               public double Ew             { get; set; } = double.NaN;
    [JsonPropertyName("layerZs")]          public List<float> LayerZs   { get; set; } = new();
    [JsonPropertyName("currentLayerZ")]    public float CurrentLayerZ   { get; set; }
    [JsonPropertyName("walls")]            public List<RadarWallLayer> Walls { get; set; } = new();
    [JsonPropertyName("fills")]            public List<RadarFillLayer> Fills { get; set; } = new();
    [JsonPropertyName("visited")]          public List<RadarVisitedLayer> Visited { get; set; } = new();
    [JsonPropertyName("markers")]          public List<RadarMarker> Markers { get; set; } = new();

    /// <summary>Parses one RynthPluginGetRadarSnapshot reply; null for "null". Throws on malformed JSON.</summary>
    public static RadarSnapshot? Parse(string json) =>
        JsonSerializer.Deserialize(json, RadarJsonContext.Default.RadarSnapshot);
}

internal sealed class RadarPlayer
{
    [JsonPropertyName("cellId")]    public uint  CellId    { get; set; }
    [JsonPropertyName("landblock")] public uint  Landblock { get; set; }
    [JsonPropertyName("x")]         public float X         { get; set; }
    [JsonPropertyName("y")]         public float Y         { get; set; }
    [JsonPropertyName("z")]         public float Z         { get; set; }
    [JsonPropertyName("worldX")]    public float WorldX    { get; set; }
    [JsonPropertyName("worldY")]    public float WorldY    { get; set; }
    [JsonPropertyName("heading")]   public float Heading   { get; set; }
}

internal sealed class RadarWallLayer
{
    [JsonPropertyName("z")]        public float   Z        { get; set; }
    /// <summary>6 floats per wall: ax, ay, bx, by, cellX, cellY.</summary>
    [JsonPropertyName("segments")] public float[] Segments { get; set; } = Array.Empty<float>();
}

internal sealed class RadarFillLayer
{
    [JsonPropertyName("z")]      public float   Z      { get; set; }
    /// <summary>5 floats per strip: x0, y0, x1, y1, type (0 flat, 1 slope up, 2 slope down).</summary>
    [JsonPropertyName("strips")] public float[] Strips { get; set; } = Array.Empty<float>();
}

internal sealed class RadarVisitedLayer
{
    [JsonPropertyName("z")]      public float   Z      { get; set; }
    /// <summary>4 floats per strip: x0, y0, x1, y1.</summary>
    [JsonPropertyName("strips")] public float[] Strips { get; set; } = Array.Empty<float>();
}

/// <summary>
/// Marker kinds (RadarMarker.Kind). 0-3 are the original four; 4 and up were added
/// 2026-10-04 and are only sent by RynthAi builds from then on, so a newer engine
/// reading an older RynthAi simply never sees them. An older engine drops kinds it
/// doesn't know (its ShowKind returns false). Numbers never change meaning: new kinds
/// are appended.
/// </summary>
internal static class RadarKind
{
    public const byte Monster    = 0;
    public const byte Npc        = 1;
    public const byte Portal     = 2;
    public const byte Door       = 3;
    public const byte Player     = 4;   // another player
    public const byte Fellow     = 5;   // a player in your fellowship
    public const byte Pet        = 6;   // your own combat pet / summon
    public const byte Vendor     = 7;
    public const byte Corpse     = 8;   // someone else's corpse (or not yet identified)
    public const byte OwnCorpse  = 9;   // a corpse you may loot: your kill, a fellow's kill, or your own
    public const byte Lifestone  = 10;
    public const byte GroundItem = 11;  // an item lying on the ground
    public const int Count = 12;

    /// <summary>A creature (has health, can be hovered for a health reading).</summary>
    public static bool IsCreature(byte kind) =>
        kind is Monster or Npc or Player or Fellow or Pet or Vendor;

    /// <summary>Singular name, for the hover tooltip when a marker has no label.</summary>
    public static string Name(byte kind) => kind switch
    {
        Monster => "Monster",
        Npc => "NPC",
        Portal => "Portal",
        Door => "Door",
        Player => "Player",
        Fellow => "Fellow",
        Pet => "Your pet",
        Vendor => "Vendor",
        Corpse => "Corpse",
        OwnCorpse => "Your corpse",
        Lifestone => "Lifestone",
        GroundItem => "Item",
        _ => "Object",
    };
}

internal sealed class RadarMarker
{
    /// <summary>A <see cref="RadarKind"/>: 0 monster, 1 NPC, 2 portal, 3 door, 4 player, 5 fellow,
    /// 6 your pet, 7 vendor, 8 corpse, 9 your corpse, 10 lifestone, 11 item on the ground.</summary>
    [JsonPropertyName("kind")]  public byte    Kind  { get; set; }
    [JsonPropertyName("x")]     public float   X     { get; set; }
    [JsonPropertyName("y")]     public float   Y     { get; set; }
    [JsonPropertyName("z")]     public float   Z     { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }
    /// <summary>The object's id (RynthAi 2026-10-01 and later); 0 from older RynthAi builds.</summary>
    [JsonPropertyName("id")]    public uint    Id    { get; set; }
}

[JsonSerializable(typeof(RadarSnapshot))]
[JsonSourceGenerationOptions(WriteIndented = false)]
internal partial class RadarJsonContext : JsonSerializerContext { }
