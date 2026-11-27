// ============================================================================
//  RynthCore.Engine - UI/Data/NavmeshView.cs
//  Settings > Navigation > Navmesh View (2026-11-26): RynthNav's navmesh
//  drawing switches (Show navmesh, green floor, red walls, radius, detail)
//  next to the other navigation display settings. Until then they were only
//  a row on the RynthNav window's Go tab (ImGui/Panels/RynthNavFace.cs,
//  MeshRows), which is easy to miss.
//
//  These are RynthNav's settings (it saves them in rynthnav.json), not
//  RynthAi's: the rows ignore the RynthAi settings object, read RynthNav's
//  status (UiSources.RynthNav, the "mesh" block, RynthNav 0.6.7+) and send
//  the Go tab's /rnav mesh commands (RynthNavCommands.Command). A save of
//  the RynthAi copy after a click sends nothing (SettingsCommands.Save only
//  sends changed fields), so nothing reaches RynthAi's payload or profile.
//  Both places read the same status, so they stay in step.
//
//  Pure except the "live" members at the bottom (Status, Get*, Set*), so
//  tools\SettingsLayoutTests can check the rules by reflection.
// ============================================================================

using System;
using System.Globalization;

namespace RynthCore.Engine.UI.Data;

internal static class NavmeshView
{
    public const string SectionLabel = "Navmesh View";

    public const string ShowLabel = "Show Navmesh", FloorLabel = "Green Floor", WallsLabel = "Red Walls",
        RadiusLabel = "Mesh Radius (m)", DetailLabel = "Mesh Detail (triangles)";

    // The Go tab's steppers (RynthNavFace.MeshRows).
    public const int RadiusMin = 10, RadiusMax = 150, RadiusStep = 10;
    public const int DetailMin = 200, DetailMax = 7000, DetailStep = 500;

    // The Go tab's tooltips (RynthNavFace.MeshRows).
    public const string ShowTip =
        "Draw the navigation mesh in the world: green where nav can walk, red walls where the mesh ends\n"
        + "(buildings, trees, rocks, cliffs, steep ground and the margin round them). In a dungeon it draws the dungeon's\n"
        + "own mesh when RynthNav has one for it. It stays on (after a relog too) until you turn it off here (/rnav mesh off).";
    public const string FloorTip = "The walkable ground, in green.";
    public const string WallsTip = "Upright red strips along every edge nav will not cross.";
    public const string RadiusTip = "How far around you the mesh is drawn.";
    public const string DetailTip =
        "The most triangles the mesh may use. The game's 3D buffer holds 8192 for every plugin, so keep this under about 5000\n"
        + "when RynthVision's slope overlay is on. The nearest ground and walls are kept first.";

    public const string SameAsGoTab = "\n\nRynthNav's setting (saved by RynthNav, not in the RynthAi profile); the same switch as the RynthNav window's Go tab.";

    private static readonly string[] Labels = { ShowLabel, FloorLabel, WallsLabel, RadiusLabel, DetailLabel };

    /// <summary>True for one of this section's row labels (the My settings drawer polls RynthNav for a pinned one).</summary>
    public static bool IsMeshLabel(string? label) => label != null && Array.IndexOf(Labels, label) >= 0;

    /// <summary>RynthNav has sent a status with the mesh block (0.6.7+): the rows show.</summary>
    public static bool Available(RynthNavStatus? st) => st?.HasMesh == true;

    /// <summary>Why the rows are hidden (empty while they show).</summary>
    public static string Unavailable(RynthNavStatus? st) =>
        st == null ? "Needs the RynthNav plugin. Once it is loaded, its navmesh switches show here."
        : !st.HasMesh ? "This RynthNav can't draw the navmesh: it needs RynthNav 0.6.7 or newer."
        : string.Empty;

    /// <summary>"1840 triangles drawn now." while the mesh is on (empty otherwise).</summary>
    public static string Drawn(RynthNavStatus? st) =>
        st is { HasMesh: true, MeshOn: true } ? st.MeshDrawn.ToString(CultureInfo.InvariantCulture) + " triangles drawn now." : string.Empty;

    /// <summary>The /rnav command (without /rnav) a switch sends.</summary>
    public static string SwitchCommand(string label, bool on) => label switch
    {
        ShowLabel => on ? "mesh on" : "mesh off",
        FloorLabel => on ? "mesh floor on" : "mesh floor off",
        WallsLabel => on ? "mesh walls on" : "mesh walls off",
        _ => throw new ArgumentException("not a navmesh switch: " + label, nameof(label)),
    };

    /// <summary>The /rnav command (without /rnav) a stepper sends, the value kept in its range.</summary>
    public static string RadiusCommand(int metres) =>
        "mesh radius " + Math.Clamp(metres, RadiusMin, RadiusMax).ToString(CultureInfo.InvariantCulture);

    public static string DetailCommand(int triangles) =>
        "mesh triangles " + Math.Clamp(triangles, DetailMin, DetailMax).ToString(CultureInfo.InvariantCulture);

    public static bool GetSwitch(RynthNavStatus? st, string label) => st != null && label switch
    {
        ShowLabel => st.MeshOn,
        FloorLabel => st.MeshFloor,
        WallsLabel => st.MeshWalls,
        _ => false,
    };

    // ── Live: RynthNav's last status (Settings is drawn on AC's thread) ──────────

    public static RynthNavStatus? Status => UiSources.RynthNav.Current?.Value;

    public static bool AvailableNow => Available(Status);

    public static bool GetSwitchNow(string label) => GetSwitch(Status, label);

    public static int RadiusNow => Status?.MeshRadius ?? 40;

    public static int DetailNow => Status?.MeshTriangles ?? 3000;

    public static void SetSwitch(string label, bool on) => RynthNavCommands.Command(SwitchCommand(label, on));

    public static void SetRadius(int metres) => RynthNavCommands.Command(RadiusCommand(metres));

    public static void SetDetail(int triangles) => RynthNavCommands.Command(DetailCommand(triangles));
}
