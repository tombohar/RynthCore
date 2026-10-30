// ============================================================================
//  RynthCore.Engine - UI/Data/VisionData.cs
//  The RynthVision plugin's settings (overlays, colours, tuning, water terrain
//  types) for both Vision faces.
//
//  RynthVisionGetSettingsJson frees its previous buffer on each call, so the
//  hub is its only caller (VisionSource, 5 s). Saving and "Log terrain types
//  here" run on the pump too. The JSON keys are the plugin's; a still-starting
//  plugin answers "{}", which is never published (a face would otherwise show
//  defaults and save them over the real settings).
//
//  Faces save one field at a time ({"key":value}; the plugin applies any
//  subset), so a save never writes back stale values over a change made
//  elsewhere (a /rv command, the other face). After every save the settings
//  are re-read and republished even if unchanged: the plugin may have
//  normalised the value (an empty water-type list resets to the defaults).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using RynthCore.Engine.Plugins;

namespace RynthCore.Engine.UI.Data;

internal sealed class VisionSettings
{
    public bool Radar, Slopes, Water, WaterAnyCorner, WaterImpassableOnly;
    public uint SlopeColor = 0x60FF2020, WaterColor = 0x600060FF, RadarColor = 0x80FFD000;
    public double RadarRange = 192, RingThick = 2, RingHeight = 3, SlopeFloorZ = 0.664, SlopeBias = 0.15;
    public int SlopeRadius = 12, WaterRadius = 12;
    public List<int> WaterTypes = new();

    public VisionSettings Clone()
    {
        var c = (VisionSettings)MemberwiseClone();
        c.WaterTypes = new List<int>(WaterTypes);
        return c;
    }

    /// <summary>Parses the plugin's JSON; false for "{}" or when no known field is present.</summary>
    public static bool TryParse(string json, out VisionSettings settings)
    {
        settings = new VisionSettings();
        if (string.IsNullOrEmpty(json) || json == "{}") return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement r = doc.RootElement;
            bool any = false;
            bool Int(string key, ref bool field) { if (!r.TryGetProperty(key, out var v)) return false; field = v.GetInt32() != 0; return true; }
            any |= Int("radar", ref settings.Radar);
            any |= Int("slopes", ref settings.Slopes);
            any |= Int("water", ref settings.Water);
            any |= Int("waterAnyCorner", ref settings.WaterAnyCorner);
            any |= Int("waterImpassableOnly", ref settings.WaterImpassableOnly);
            if (r.TryGetProperty("slopeColor", out var e)) { settings.SlopeColor = e.GetUInt32(); any = true; }
            if (r.TryGetProperty("waterColor", out e)) { settings.WaterColor = e.GetUInt32(); any = true; }
            if (r.TryGetProperty("radarColor", out e)) { settings.RadarColor = e.GetUInt32(); any = true; }
            if (r.TryGetProperty("radarRange", out e)) { settings.RadarRange = e.GetDouble(); any = true; }
            if (r.TryGetProperty("ringThick", out e)) { settings.RingThick = e.GetDouble(); any = true; }
            if (r.TryGetProperty("ringHeight", out e)) { settings.RingHeight = e.GetDouble(); any = true; }
            if (r.TryGetProperty("slopeRadius", out e)) { settings.SlopeRadius = e.GetInt32(); any = true; }
            if (r.TryGetProperty("slopeFloorZ", out e)) { settings.SlopeFloorZ = e.GetDouble(); any = true; }
            if (r.TryGetProperty("slopeBias", out e)) { settings.SlopeBias = e.GetDouble(); any = true; }
            if (r.TryGetProperty("waterRadius", out e)) { settings.WaterRadius = e.GetInt32(); any = true; }
            if (r.TryGetProperty("waterTypes", out e) && e.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement t in e.EnumerateArray()) settings.WaterTypes.Add(t.GetInt32());
                any = true;
            }
            return any;
        }
        catch { return false; }
    }

    /// <summary>One field as the plugin's JSON, e.g. {"radar":1}. Key: a <see cref="VisionKeys"/> constant.</summary>
    public string FieldJson(string key)
    {
        var ci = CultureInfo.InvariantCulture;
        string value = key switch
        {
            VisionKeys.Radar => Radar ? "1" : "0",
            VisionKeys.Slopes => Slopes ? "1" : "0",
            VisionKeys.Water => Water ? "1" : "0",
            VisionKeys.WaterAnyCorner => WaterAnyCorner ? "1" : "0",
            VisionKeys.WaterImpassableOnly => WaterImpassableOnly ? "1" : "0",
            VisionKeys.SlopeColor => SlopeColor.ToString(ci),
            VisionKeys.WaterColor => WaterColor.ToString(ci),
            VisionKeys.RadarColor => RadarColor.ToString(ci),
            VisionKeys.RadarRange => RadarRange.ToString("R", ci),
            VisionKeys.RingThick => RingThick.ToString("R", ci),
            VisionKeys.RingHeight => RingHeight.ToString("R", ci),
            VisionKeys.SlopeRadius => SlopeRadius.ToString(ci),
            VisionKeys.SlopeFloorZ => SlopeFloorZ.ToString("R", ci),
            VisionKeys.SlopeBias => SlopeBias.ToString("R", ci),
            VisionKeys.WaterRadius => WaterRadius.ToString(ci),
            VisionKeys.WaterTypes => "[" + string.Join(",", WaterTypes) + "]",
            _ => throw new ArgumentException($"Unknown Vision setting '{key}'", nameof(key)),
        };
        return "{\"" + key + "\":" + value + "}";
    }

    /// <summary>"18,19,20" -> the numbers in it (anything else is dropped).</summary>
    public static List<int> ParseCsv(string? csv)
    {
        var list = new List<int>();
        if (string.IsNullOrWhiteSpace(csv)) return list;
        foreach (string part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) list.Add(n);
        return list;
    }

    /// <summary>"AARRGGBB" or "RRGGBB" (opaque), with or without '#'.</summary>
    public static bool TryParseHex(string? s, out uint argb)
    {
        argb = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim().TrimStart('#');
        if (s.Length == 6) s = "FF" + s;
        return s.Length == 8 && uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out argb);
    }
}

/// <summary>The plugin's settings JSON keys.</summary>
internal static class VisionKeys
{
    public const string Radar = "radar", Slopes = "slopes", Water = "water";
    public const string WaterAnyCorner = "waterAnyCorner", WaterImpassableOnly = "waterImpassableOnly";
    public const string SlopeColor = "slopeColor", WaterColor = "waterColor", RadarColor = "radarColor";
    public const string RadarRange = "radarRange", RingThick = "ringThick", RingHeight = "ringHeight";
    public const string SlopeRadius = "slopeRadius", SlopeFloorZ = "slopeFloorZ", SlopeBias = "slopeBias";
    public const string WaterRadius = "waterRadius", WaterTypes = "waterTypes";
}

internal sealed class VisionSnapshot
{
    public VisionSnapshot(string json, VisionSettings settings) { Json = json; Settings = settings; }
    public string Json { get; }
    /// <summary>Shared: copy before changing (Clone).</summary>
    public VisionSettings Settings { get; }
}

/// <summary>RynthVisionGetSettingsJson while a Vision face is open (5 s, and after each save).</summary>
internal sealed unsafe class VisionSource : UiSource<VisionSnapshot>
{
    private delegate* unmanaged[Cdecl]<IntPtr> _get;
    private string? _lastJson;

    public VisionSource() : base("Vision", periodMs: 5000) { }

    protected internal override void Poll()
    {
        if (_get == null)
            _get = (delegate* unmanaged[Cdecl]<IntPtr>)PluginExportBinder.Resolve("RynthVision", "RynthVisionGetSettingsJson");
        if (_get == null) return;
        IntPtr ptr = _get();
        string? json = ptr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ptr);
        if (string.IsNullOrEmpty(json) || json == _lastJson) return;
        if (!VisionSettings.TryParse(json, out VisionSettings settings)) return;
        _lastJson = json;
        Publish(new VisionSnapshot(json, settings));
    }

    protected internal override void Reset()
    {
        _get = null;
        _lastJson = null;
        ClearSnapshot();
    }

    /// <summary>Publish on the next poll even if the JSON is unchanged. Pump thread.</summary>
    internal void RequestRepublish()
    {
        _lastJson = null;
        RequestRefresh();
    }
}

internal static unsafe class VisionCommands
{
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _set;
    private static delegate* unmanaged[Cdecl]<void> _inspect;

    static VisionCommands()
    {
        PluginManager.PluginsUnloaded += () => { _set = null; _inspect = null; };
    }

    /// <summary>Applies and saves one field: <paramref name="key"/>'s value from <paramref name="settings"/>.</summary>
    public static void SaveField(VisionSettings settings, string key) => Save(settings.FieldJson(key));

    /// <summary>Applies and saves the settings in <paramref name="json"/> (any subset of the keys).</summary>
    public static void Save(string json) => UiDataHub.Post("Vision save", () =>
    {
        if (_set == null) _set = (delegate* unmanaged[Cdecl]<IntPtr, void>)PluginExportBinder.Resolve("RynthVision", "RynthVisionSetSettings");
        if (_set == null) return;
        IntPtr p = Marshal.StringToHGlobalAnsi(json);
        try { _set(p); }
        finally { Marshal.FreeHGlobal(p); }
        UiSources.Vision.RequestRepublish();
    });

    /// <summary>Logs the terrain types under the player (RynthVision's diagnostic).</summary>
    public static void Inspect() => UiDataHub.Post("Vision inspect", () =>
    {
        if (_inspect == null) _inspect = (delegate* unmanaged[Cdecl]<void>)PluginExportBinder.Resolve("RynthVision", "RynthVisionInspectTerrain");
        if (_inspect != null) _inspect();
    });
}
