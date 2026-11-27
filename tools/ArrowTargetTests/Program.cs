using RynthCore.Engine.UI.Data;

namespace ArrowTargetTests;

/// <summary>
/// The RynthNav Arrow window's one target (ArrowTarget). 11-24, a player: clicked chat coordinates
/// went to the top of the window while the route list under them was for another place.
/// </summary>
internal static class Program
{
    private static int _checks, _failed;

    private const double HoltNs = 42.08, HoltEw = 33.60, FarNs = -12.3, FarEw = 45.6;

    private static int Main()
    {
        Sources();
        RouteBelongs();
        ChatNote();
        Labels();

        Console.WriteLine(_failed == 0 ? $"PASS ({_checks} checks)" : $"FAIL ({_failed} of {_checks})");
        return _failed == 0 && _checks > 0 ? 0 : 1;
    }

    private static ArrowTargetInput Arrow(string final, double ns, double ew, int steps = 1) =>
        new(true, final, ns, ew, steps, false, "", false, 0, 0, false, 0, 0);

    private static ArrowTargetInput WithRoute(ArrowTargetInput a, string routeFor, double ns, double ew, bool goal = true) =>
        a with { HasRoute = true, RouteFor = routeFor, RouteHasGoal = goal, RouteNs = goal ? ns : 0, RouteEw = goal ? ew : 0,
                 RouteHasLast = true, LastNs = ns, LastEw = ew };

    private static readonly ArrowTargetInput Nothing = new(false, "", 0, 0, 0, false, "", false, 0, 0, false, 0, 0);

    private static void Sources()
    {
        Check(ArrowTarget.Pick(Nothing, chat: false).Source == ArrowTargetSource.None, "nothing: no target");
        Check(ArrowTarget.Pick(Arrow("Holtburg", HoltNs, HoltEw), chat: false).Source == ArrowTargetSource.Arrow, "a place: Arrow");
        Check(ArrowTarget.Pick(Arrow("42.1N, 33.6E", 42.1, 33.6), chat: true).Source == ArrowTargetSource.Chat, "a chat link: Chat coords");
        Check(ArrowTarget.Pick(Arrow("Holtburg", HoltNs, HoltEw, steps: 3), chat: false).Source == ArrowTargetSource.Route,
            "an arrow along a route's portals: Route");
        // A route to the arrow's own place makes it a route, even from chat ("Route here").
        var routed = WithRoute(Arrow("42.1N, 33.6E", 42.1, 33.6), "42.1N, 33.6E", 42.1, 33.6);
        Check(ArrowTarget.Pick(routed, chat: true).Source == ArrowTargetSource.Route, "chat coords, then Plan: Route");
        // No arrow, a route shown (an older RynthNav after arrow off, or a trip): the route is the target.
        var onlyRoute = WithRoute(Nothing, "Rithwic", 10, 59);
        Check(ArrowTarget.Pick(onlyRoute, chat: false).Source == ArrowTargetSource.Route, "only a route: Route");
    }

    private static void RouteBelongs()
    {
        // The report: chat coords far away, the route list still for Holtburg.
        var stale = WithRoute(Arrow("12.3S, 45.6E", FarNs, FarEw), "Holtburg", HoltNs, HoltEw);
        ArrowTargetPick p = ArrowTarget.Pick(stale, chat: true);
        Check(!p.RouteBelongs, "a route for another place stays out of the window");
        Check(p.Source == ArrowTargetSource.Chat, "...and the target is the chat coords");

        var same = WithRoute(Arrow("Holtburg", HoltNs, HoltEw), "Holtburg", HoltNs, HoltEw);
        Check(ArrowTarget.RouteBelongs(same), "a route to the arrow's place belongs");

        // Two places with one name: RynthNav's goal decides.
        var namesake = WithRoute(Arrow("Lifestone", HoltNs, HoltEw), "Lifestone", FarNs, FarEw);
        Check(!ArrowTarget.RouteBelongs(namesake), "same name, another place: not this target's route");

        // An older RynthNav (no goal sent): the name, else the route's last step.
        var byName = WithRoute(Arrow("Holtburg", HoltNs, HoltEw), "holtburg", FarNs, FarEw, goal: false);
        Check(ArrowTarget.RouteBelongs(byName), "older RynthNav: the same name (any case) belongs");
        var byLast = WithRoute(Arrow("42.1N, 33.6E", 42.1, 33.6), "Holtburg Lifestone", HoltNs, HoltEw, goal: false);
        Check(ArrowTarget.RouteBelongs(byLast), "older RynthNav: a route ending at the arrow's spot belongs");
        var neither = WithRoute(Arrow("Rithwic", 10, 59), "Holtburg", HoltNs, HoltEw, goal: false);
        Check(!ArrowTarget.RouteBelongs(neither), "older RynthNav: another name ending elsewhere doesn't");

        Check(!ArrowTarget.RouteBelongs(Arrow("Holtburg", HoltNs, HoltEw)), "no route: nothing belongs");
        Check(ArrowTarget.SamePlace(42.1, 33.6, HoltNs, HoltEw), "chat coords' one decimal is the same place");
        Check(!ArrowTarget.SamePlace(42.2, 33.6, HoltNs, HoltEw), "0.12 degrees off is not");
        Check(!ArrowTarget.SamePlace(double.NaN, 33.6, HoltNs, HoltEw), "an unknown spot is never the same place");
    }

    private static void ChatNote()
    {
        ArrowTarget.ForgetChat();
        Check(!ArrowTarget.FromChat(42.1, 33.6), "nothing noted: not from chat");
        int v = ArrowTarget.ChatVersion;
        ArrowTarget.NoteChat(42.1, 33.6);
        Check(ArrowTarget.ChatVersion != v, "a note bumps the version (the window rebuilds its line)");
        // RynthNav gets the coordinates with two decimals and sends them back as numbers.
        Check(ArrowTarget.FromChat(42.10, 33.60), "the arrow at the noted spot came from chat");
        Check(!ArrowTarget.FromChat(HoltNs, HoltEw), "Holtburg's own spot (0.02 off) did not: the Atlas set it");
        ArrowTarget.NoteChat(-12.3, 45.6);
        Check(!ArrowTarget.FromChat(42.1, 33.6), "a newer chat link replaces the note");
        ArrowTarget.ForgetChat();
        Check(!ArrowTarget.FromChat(-12.3, 45.6), "cleared: not from chat any more");
    }

    private static void Labels()
    {
        Check(ArrowTarget.Pick(Arrow("42.1N, 33.6E", 42.1, 33.6), chat: true).Label == "Chat coords 42.1N, 33.6E", "chat label");
        Check(ArrowTarget.Pick(Arrow("12.3S, 45.6W", -12.3, -45.6), chat: true).Label == "Chat coords 12.3S, 45.6W", "south and west");
        Check(ArrowTarget.Pick(Arrow("Holtburg", HoltNs, HoltEw, steps: 2), chat: false).Label == "Route: Holtburg", "route label");
        Check(ArrowTarget.Pick(Arrow("Holtburg", HoltNs, HoltEw), chat: false).Label == "Arrow: Holtburg", "arrow label");
        Check(ArrowTarget.Pick(Arrow("", HoltNs, HoltEw), chat: false).Label == "Arrow: 42.1N, 33.6E", "no name: the coordinates");
        Check(ArrowTarget.Pick(WithRoute(Nothing, "Rithwic", 10, 59), chat: false).Label == "Route: Rithwic", "a route alone");
        Check(ArrowTarget.Pick(Nothing, chat: false).Label == "No target", "nothing");
    }

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (ok) return;
        _failed++;
        Console.WriteLine("  [FAIL] " + what);
    }
}
