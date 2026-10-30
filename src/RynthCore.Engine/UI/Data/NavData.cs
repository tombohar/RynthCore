// ============================================================================
//  RynthCore.Engine - UI/Data/NavData.cs
//  RynthAi's nav route (active nav, status, route type, waypoints, nav files)
//  for both Nav faces.
//
//  RynthPluginGetNavJson frees its previous buffer on each call, so the hub is
//  its only caller (NavSource, 1 s). Commands go through
//  RynthAiCommands.SendNavCommand on the pump; the plugin applies them on the
//  spot, so the next poll (requested right after) shows the result. ANSI JSON.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using RynthCore.Engine.ImGuiBackend.Hud;
using System.Text.Json.Serialization;

namespace RynthCore.Engine.UI.Data;

internal sealed class NavPoint
{
    public int Idx { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Desc { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;   // Chat points: the command (editable)
    public double NS { get; set; }
    public double EW { get; set; }
    public double Z { get; set; }
}

internal sealed class NavPayload
{
    public string ActiveNavName { get; set; } = string.Empty;
    public string NavStatusLine { get; set; } = string.Empty;
    public bool NavIsStuck { get; set; }
    public bool MacroRunning { get; set; }
    public bool NavigationEnabled { get; set; }
    public int RouteType { get; set; }
    public int ActiveNavIndex { get; set; }
    public List<string> NavFiles { get; set; } = new();
    public List<NavPoint> Points { get; set; } = new();
    /// <summary>RynthAi records (and draws) the walked breadcrumb trail. Older plugins omit it (false).</summary>
    public bool TrackBreadcrumbs { get; set; }
    /// <summary>RynthAi draws the route overlay (rings / lines, waypoint labels, guide line).</summary>
    public bool ShowRouteOverlay { get; set; }
    /// <summary>Route recording is on (points are added as you walk). Older plugins omit it.</summary>
    public bool IsRecording { get; set; }
    /// <summary>Breadcrumb trail size, for "Trail to route" / "Backtrack".</summary>
    public int TrailPoints { get; set; }
    public double TrailYards { get; set; }
    /// <summary>The last route edit's result, for a few seconds after it (empty otherwise).</summary>
    public string EditStatus { get; set; } = string.Empty;
    /// <summary>Map editing commands RynthAi understands (addPointAt, movePointTo, setActive): 1 from
    /// RynthAi 2026-10-12. Older plugins omit it (0) and the Dungeon Map doesn't offer editing.</summary>
    public int MapEdit { get; set; }

    /// <summary>A copy a face may change (its own point list); the points themselves are shared.</summary>
    public NavPayload Clone()
    {
        var c = (NavPayload)MemberwiseClone();
        c.Points = new List<NavPoint>(Points);
        return c;
    }
}

internal sealed class NavCmd
{
    public string Cmd { get; set; } = string.Empty;
    public int SpellId { get; set; }
    public int Index { get; set; }
    public int RouteType { get; set; }
    public int AddMode { get; set; }
    public int InsertAt { get; set; } = -1;
    public string NavName { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;   // addChat
    public bool On { get; set; }                       // setBreadcrumbs / setRouteOverlay / setRecording
    public List<int>? Indices { get; set; }            // movePoints / duplicatePoints / deletePoints
    public int Delta { get; set; }                     // movePoints: -1 up, +1 down
    public double Seconds { get; set; }                // addPause
    public double Yards { get; set; }                  // simplifyRoute tolerance
    public bool Reverse { get; set; }                  // trailToRoute: backtrack
    public double NS { get; set; }                     // addPointAt / movePointTo: nav coordinates
    public double EW { get; set; }
    public double Z { get; set; }                      // nav Z (world Z / 240)
}

[JsonSerializable(typeof(NavPayload))]
[JsonSerializable(typeof(NavCmd))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = false, IncludeFields = false)]
internal partial class NavJsonContext : JsonSerializerContext { }

/// <summary>RynthPluginGetNavJson while a Nav face is open (1 s, and after each command).</summary>
internal sealed unsafe class NavSource : UiSource<NavPayload>
{
    private delegate* unmanaged[Cdecl]<IntPtr> _getNavJson;
    private string? _lastJson;

    public NavSource() : base("Nav", periodMs: 1000) { }

    protected internal override void Poll()
    {
        if (_getNavJson == null)
            _getNavJson = (delegate* unmanaged[Cdecl]<IntPtr>)PluginExportBinder.Resolve("RynthAi", "RynthPluginGetNavJson");
        if (_getNavJson == null) return;
        IntPtr ptr = _getNavJson();
        string? json = ptr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ptr);
        if (string.IsNullOrEmpty(json) || json == _lastJson) return;
        _lastJson = json;
        NavPayload? parsed = JsonSerializer.Deserialize(json, NavJsonContext.Default.NavPayload);
        if (parsed != null) Publish(parsed);
    }

    protected internal override void Reset()
    {
        _getNavJson = null;
        _lastJson = null;
        ClearSnapshot();
    }
}

/// <summary>Nav panel commands and vocabulary, shared by both faces.</summary>
internal static class NavCommands
{
    public static void Send(NavCmd cmd) =>
        RynthAiCommands.SendNavCommand(JsonSerializer.Serialize(cmd, NavJsonContext.Default.NavCmd));

    public static readonly string[] RouteTypes = { "Once", "Circular", "Linear", "Follow" };
    public static readonly string[] AddModes = { "End", "Above", "Below" };

    public static readonly double[] PauseSeconds = { 1, 2, 3, 5, 10, 15, 30, 60 };
    public static readonly string[] PauseLabels = Array.ConvertAll(PauseSeconds, s => $"Pause {s:0} s");

    public static readonly double[] SimplifyYards = { 0.5, 1, 2, 3, 5 };
    public static readonly string[] SimplifyLabels =
        Array.ConvertAll(SimplifyYards, y => $"Drop points within {y:0.#} yd of a straight line");

    public static readonly string[] TrailChoices =
    {
        "Replace route with the trail (walk it again)",
        "Replace route with a backtrack (walk it back)",
    };

    /// <summary>Route picker index for the plugin's RouteType (1 Circular, 2 Linear, 3 Follow, else Once).</summary>
    public static int RouteIndex(int routeType) => routeType switch { 1 => 1, 2 => 2, 3 => 3, _ => 0 };

    /// <summary>The plugin's RouteType for a route picker index (Once = 4).</summary>
    public static int RouteTypeFor(int index) => index switch { 1 => 1, 2 => 2, 3 => 3, _ => 4 };

    public static readonly int[] RecallIds =
    {
        48, 2645, 2647,
        1635, 1636,
        157, 158, 1637,
        2648, 2649, 2650,
        2931, 2023, 2041, 2358, 2813, 2941, 2943,
        3865, 3929, 3930, 4084, 4198, 4213,
        4907, 4908, 4909,
        5175, 5330, 5541, 6150, 6321, 6322,
    };

    /// <summary>
    /// "Lifestone Recall (1635)": the recall spells by name (2026-10-08: they showed as
    /// "Spell 1635 (1635)"). The names come from the client's spell table, which loads in the
    /// background, so the list is rebuilt until every name is known.
    /// </summary>
    public static string[] RecallLabels
    {
        get
        {
            if (_recallLabels == null || !_recallLabelsNamed)
            {
                PortalSpellTable.EnsureLoadQueued();
                _recallLabels = BuildRecallLabels(out _recallLabelsNamed);
            }
            return _recallLabels;
        }
    }

    private static string[]? _recallLabels;
    private static bool _recallLabelsNamed;

    private static string[] BuildRecallLabels(out bool allNamed)
    {
        allNamed = true;
        var labels = new string[RecallIds.Length];
        for (int i = 0; i < labels.Length; i++)
        {
            if (PortalSpellTable.TryGetName((uint)RecallIds[i], out string name) && name.Length > 0)
                labels[i] = $"{name} ({RecallIds[i]})";
            else
            {
                labels[i] = $"Spell {RecallIds[i]} ({RecallIds[i]})";
                allNamed = false;
            }
        }
        return labels;
    }

    /// <summary>"[3] Recall ..." or "[3] Point (12.34N, 56.78E, 12.3)".</summary>
    public static string PointText(NavPoint pt, int i) =>
        !string.IsNullOrEmpty(pt.Desc)
            ? $"[{i}] {pt.Desc}"
            : $"[{i}] {pt.Type} ({pt.NS:F2}N, {pt.EW:F2}E, {pt.Z:F1})";
}
