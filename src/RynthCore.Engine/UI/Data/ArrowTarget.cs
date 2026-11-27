// ============================================================================
//  RynthCore.Engine - UI/Data/ArrowTarget.cs
//  What the RynthNav Arrow window shows as its one target, and where it came
//  from (11-24, a player: "You'll notice the top and bottom have different
//  targets": the arrow pointed at clicked chat coordinates while the route list
//  under it was an older trip somewhere else).
//
//    source     Chat coords (a chat link clicked, noted here by the chat window),
//               Route (the arrow follows a route, or the route shown goes where
//               the arrow points), Arrow (a place set any other way), or none.
//    route      the steps list opens only when the route goes where the arrow
//               points; an older route for somewhere else stays out of the window.
//
//  RynthNav owns the target (one at a time, the newest wins: TargetSwitch); this
//  is the window's side, so an older RynthNav that kept a stale route still shows
//  one target. Pure: no engine types, so tools/ArrowTargetTests compiles it.
// ============================================================================

using System;
using System.Globalization;

namespace RynthCore.Engine.UI.Data;

internal enum ArrowTargetSource { None, Chat, Arrow, Route }

/// <summary>
/// RynthNav's arrow and route, as the window needs them. Final: where the arrow ends up (the place
/// itself on a chained route). Route goal: RynthNav's "gns"/"gew" when it sends them, else the
/// route's last step.
/// </summary>
internal readonly record struct ArrowTargetInput(
    bool HasArrow, string Final, double FinalNs, double FinalEw, int Steps,
    bool HasRoute, string RouteFor, bool RouteHasGoal, double RouteNs, double RouteEw,
    bool RouteHasLast, double LastNs, double LastEw);

/// <summary>The one target: where it came from, the line that says so, and whether the route list belongs to it.</summary>
internal readonly record struct ArrowTargetPick(ArrowTargetSource Source, string Label, bool RouteBelongs);

internal static class ArrowTarget
{
    /// <summary>Two spots this close (degrees, about 14 u) are the same place: chat coordinates have one decimal.</summary>
    public const double SameDegrees = 0.06;

    // The last coordinates a chat link pointed the arrow at (the chat window, AC's render thread).
    private static bool _chat;
    private static double _chatNs, _chatEw;

    /// <summary>Bumped on every chat note, so the window rebuilds its label.</summary>
    public static int ChatVersion { get; private set; }

    /// <summary>The chat window pointed the arrow at these coordinates (a link clicked, or Arrow to / Route to).</summary>
    public static void NoteChat(double ns, double ew)
    {
        _chat = true; _chatNs = ns; _chatEw = ew;
        ChatVersion++;
    }

    public static void ForgetChat() { _chat = false; ChatVersion++; }

    /// <summary>The arrow's place is where a chat link last pointed it (RynthNav's commands carry two decimals).</summary>
    public static bool FromChat(double ns, double ew) =>
        _chat && Math.Abs(ns - _chatNs) < 0.006 && Math.Abs(ew - _chatEw) < 0.006;

    public static bool SamePlace(double nsA, double ewA, double nsB, double ewB) =>
        !double.IsNaN(nsA) && !double.IsNaN(ewA) && !double.IsNaN(nsB) && !double.IsNaN(ewB)
        && Math.Abs(nsA - nsB) <= SameDegrees && Math.Abs(ewA - ewB) <= SameDegrees;

    /// <summary>The route shown goes where the arrow points (or there is no arrow: the route is the target).</summary>
    public static bool RouteBelongs(in ArrowTargetInput t)
    {
        if (!t.HasRoute) return false;
        if (!t.HasArrow) return true;
        // RynthNav says where its route goes: that decides (two places can share a name).
        if (t.RouteHasGoal) return SamePlace(t.RouteNs, t.RouteEw, t.FinalNs, t.FinalEw);
        if (t.RouteFor.Length > 0 && string.Equals(t.RouteFor, t.Final, StringComparison.OrdinalIgnoreCase)) return true;
        return t.RouteHasLast && SamePlace(t.LastNs, t.LastEw, t.FinalNs, t.FinalEw);
    }

    /// <param name="chat">The arrow came from a chat link (<see cref="FromChat"/>).</param>
    public static ArrowTargetPick Pick(in ArrowTargetInput t, bool chat)
    {
        bool belongs = RouteBelongs(t);
        if (!t.HasArrow)
            return belongs ? new(ArrowTargetSource.Route, "Route: " + Name(t.RouteFor, t.RouteNs, t.RouteEw, t.RouteHasGoal), true)
                : new(ArrowTargetSource.None, "No target", false);
        string final = Name(t.Final, t.FinalNs, t.FinalEw, true);
        if (t.Steps > 1 || belongs) return new(ArrowTargetSource.Route, "Route: " + final, belongs);
        if (chat) return new(ArrowTargetSource.Chat, "Chat coords " + Coord(t.FinalNs, t.FinalEw), false);
        return new(ArrowTargetSource.Arrow, "Arrow: " + final, false);
    }

    private static string Name(string name, double ns, double ew, bool hasCoords) =>
        name.Length > 0 ? name : hasCoords ? Coord(ns, ew) : "a place";

    /// <summary>"12.3N, 45.6E" (RynthNavStatus.Coord's format).</summary>
    public static string Coord(double ns, double ew) => Loc(ns, 'N', 'S') + ", " + Loc(ew, 'E', 'W');

    private static string Loc(double v, char pos, char neg) =>
        v.ToString("F1", CultureInfo.InvariantCulture).TrimStart('-') + (v >= 0 ? pos : neg);
}
