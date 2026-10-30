using RynthCore.Engine.Compatibility;
using RynthCore.Engine.ImGuiBackend;
using RynthCore.Engine.UI.Sections;

namespace SectionsTests;

/// <summary>
/// The Mini Remote's sections, offline: per-character server overrides (server_force.txt),
/// plugin manifests (RynthPluginGetSectionsJson) and the section catalog built from them.
/// </summary>
internal static class Program
{
    private static int _checks, _failed;
    private static string _case = "";

    private static int Main()
    {
        ForceText();
        ForceFile();
        ManifestBasics();
        ManifestRejects();
        ManifestLimits();
        CatalogCore();
        CatalogServers();
        CatalogPluginSections();
        DeclaredServers();
        SelectionPlan();
        NotLoadedServerList();
        SelectionCurrentPick();
        SelectionActiveAndOwner();
        StateJson();
        ManifestWindows();
        WindowPlanChoices();
        WindowTakeOver();
        WindowFrameless();
        StackCheckAfterPluginDraw();

        Console.WriteLine();
        Console.WriteLine($"{_checks - _failed}/{_checks} checks passed.");
        if (_checks == 0) { Console.WriteLine("ABORT: no checks ran."); return 1; }
        Console.WriteLine(_failed == 0 ? "ALL SECTIONS TESTS PASSED." : $"{_failed} FAILED.");
        return _failed == 0 ? 0 : 1;
    }

    // ── Overrides ──────────────────────────────────────────────────────

    private static void ForceText()
    {
        Case("force text");
        Check(ServerForceText.TryParse("on", out ServerForce m) && m == ServerForce.On, "on");
        Check(ServerForceText.TryParse(" OFF ", out m) && m == ServerForce.Off, "OFF, padded");
        Check(ServerForceText.TryParse("auto", out m) && m == ServerForce.Auto, "auto");
        Check(!ServerForceText.TryParse("maybe", out _) && !ServerForceText.TryParse(null, out _), "junk and null");
        Check(ServerForceText.Name(ServerForce.On) == "on" && ServerForceText.Name(ServerForce.Auto) == "auto", "names");
        Check(ServerForceText.Apply(ServerForce.On, false), "On wins over not detected");
        Check(!ServerForceText.Apply(ServerForce.Off, true), "Off wins over detected");
        Check(ServerForceText.Apply(ServerForce.Auto, true) && !ServerForceText.Apply(ServerForce.Auto, false), "Auto follows detection");
    }

    private static void ForceFile()
    {
        Case("force file");
        var f = new ServerForceFile();
        Check(f.Set("Kelpie@ILT", "ilt", ServerForce.On), "set on");
        Check(!f.Set("Kelpie@ILT", "ilt", ServerForce.On), "same value again is no change");
        Check(f.Get("kelpie@ilt", "ILT") == ServerForce.On, "keys are case-insensitive");
        Check(f.Get("Other", "ilt") == ServerForce.Auto, "another character is Auto");
        Check(f.Set("Kelpie@ILT", "aelrynth", ServerForce.Off), "a second server");
        Check(f.For("Kelpie@ILT").Count == 2, "For lists both");

        var round = ServerForceFile.Parse(f.Write().Split('\n'));
        Check(round.Get("Kelpie@ILT", "ilt") == ServerForce.On && round.Get("Kelpie@ILT", "aelrynth") == ServerForce.Off, "round trip");

        Check(f.Set("Kelpie@ILT", "ilt", ServerForce.Auto) && f.Get("Kelpie@ILT", "ilt") == ServerForce.Auto, "Auto removes");
        Check(!f.Set("Kelpie@ILT", "ilt", ServerForce.Auto), "removing again is no change");
        Check(!f.Set("bad\tkey", "ilt", ServerForce.On) && !f.Set("k", "a\nb", ServerForce.On), "tabs and line breaks refused");
        Check(!f.Set(new string('x', ServerForceFile.MaxFieldLength + 1), "ilt", ServerForce.On), "overlong key refused");
        Check(!f.Set("", "ilt", ServerForce.On) && !f.Set("k", " ", ServerForce.On), "empty fields refused");

        var damaged = ServerForceFile.Parse(new[] { "# comment", "", "a\tilt", "a\tilt\tmaybe", "a\tilt\tauto", "b\tilt\ton\textra" });
        Check(damaged.For("a").Count == 0, "short, unknown and auto lines are skipped");
        Check(damaged.Get("b", "ilt") == ServerForce.On, "extra fields are ignored");
        Check(ServerForceFile.Parse(null).For("a").Count == 0, "no file");
    }

    // ── Manifests ──────────────────────────────────────────────────────

    private const string GoodManifest = """
    {
      "version": 1,
      "servers": [ { "id": "ilt", "name": "Infinite Leaftide", "worldNames": ["*Leaftide*"], "hosts": ["infiniteleaftide.online"] },
                   { "id": "Frost-Haven", "name": "Frosthaven", "worldNames": ["Frosthaven"], "hosts": [] } ],
      "sections": [ { "id": "quests", "label": "Quests", "order": 650 } ],
      "contributions": [
        { "section": "pets", "server": "", "label": "Pets", "surface": "section:pets", "primary": true, "width": 560, "height": 580 },
        { "section": "skills", "server": "ilt", "label": "Progression", "command": "/rc progression", "tooltip": "line one\nline two" },
        { "section": "quests", "label": "Quests", "surface": "section:quests", "primary": true },
        { "section": "char", "server": "ilt", "label": "Off now", "command": "/ra x", "enabled": false, "note": "Not reported." }
      ],
      "future": { "ignored": true }
    }
    """;

    private static void ManifestBasics()
    {
        Case("manifest basics");
        SectionManifest m = SectionManifestParser.Parse("RynthAi", GoodManifest, out string err);
        Check(err.Length == 0, "no error: " + err);
        Check(m.Plugin == "RynthAi", "plugin name kept");
        Check(m.Servers.Length == 2 && m.Servers[1].Id == "frost-haven", "server ids lower-cased");
        Check(m.Servers[0].WorldNames.SequenceEqual(new[] { "*Leaftide*" }) && m.Servers[0].Hosts.Length == 1, "server lists");
        Check(m.Sections.Length == 1 && m.Sections[0].Order == 650, "a plugin section with its order");
        Check(m.Contributions.Length == 4, "four contributions");
        SectionContribution pets = m.Contributions[0];
        Check(pets.IsSurface && pets.IsUniversal && pets.Primary && pets.Width == 560 && pets.Height == 580, "surface fields");
        SectionContribution prog = m.Contributions[1];
        Check(!prog.IsSurface && prog.Command == "/rc progression" && prog.Server == "ilt" && prog.Enabled, "command fields");
        Check(prog.Tooltip.Contains('\n'), "tooltips keep line breaks");
        Check(m.Contributions[2].Order == 1000, "order defaults to 1000");
        Check(!m.Contributions[3].Enabled && m.Contributions[3].Note == "Not reported.", "disabled with a note");
    }

    private static void ManifestRejects()
    {
        Case("manifest rejects");
        Check(SectionManifestParser.Parse("p", null, out string e).Contributions.Length == 0 && e == "empty", "null");
        Check(SectionManifestParser.Parse("p", "[1,2]", out e).Contributions.Length == 0 && e.Length > 0, "not an object");
        Check(SectionManifestParser.Parse("p", "{ bad", out e).Contributions.Length == 0 && e.StartsWith("bad JSON"), "bad JSON");
        Check(SectionManifestParser.Parse("p", """{"version":0}""", out e).Contributions.Length == 0 && e.Length > 0, "version 0");

        string deep = new string('[', 40) + new string(']', 40);
        Check(SectionManifestParser.Parse("p", "{\"x\":" + deep + "}", out e).Contributions.Length == 0 && e.Length > 0, "too deep");

        SectionManifest m = SectionManifestParser.Parse("p", """
        { "contributions": [
            { "section": "Bad Id!", "label": "x", "command": "/a" },
            { "section": "char", "label": "", "command": "/a" },
            { "section": "char", "label": "no action" },
            { "section": "char", "label": "not a command", "command": "say hi" },
            { "section": "char", "label": "two lines", "command": "/a\n/b" },
            { "section": "char", "label": "popout", "surface": "x:popout" },
            { "section": "char", "label": "odd surface", "surface": "a b" },
            { "section": "char", "label": "bad server", "server": "no way", "command": "/a" },
            { "section": "char", "label": "Line\nbreak", "command": "/ok", "width": -5, "height": 99999 }
        ] }
        """, out e);
        Check(e.Length == 0, "entries are skipped, not the manifest");
        Check(m.Contributions.Length == 1, "only the good entry survives");
        SectionContribution c = m.Contributions.Single();
        Check(c.Label == "Line break", "labels lose line breaks");
        Check(c.Width == 0 && c.Height == SectionManifestParser.MaxSurfaceSize, "sizes clamped");

        Check(SectionManifestParser.Id("ab_c-1") == "ab_c-1" && SectionManifestParser.Id("A B") == "", "ids");
        Check(SectionManifestParser.Id(new string('a', SectionManifestParser.MaxIdLength + 1)) == "", "long ids");
        Check(SectionManifestParser.Command("/x") == "/x" && SectionManifestParser.Command("/") == "", "commands");
        Check(SectionManifestParser.Surface("section:ilt-options") == "section:ilt-options", "a section surface");
    }

    private static void ManifestLimits()
    {
        Case("manifest limits");
        var items = string.Join(",", Enumerable.Range(0, 300).Select(i => $$"""{"section":"char","label":"L{{i}}","command":"/c {{i}}"}"""));
        SectionManifest m = SectionManifestParser.Parse("p", "{\"contributions\":[" + items + "]}", out _);
        Check(m.Contributions.Length == SectionManifestParser.MaxContributions, "contributions capped");

        var servers = string.Join(",", Enumerable.Range(0, 40).Select(i => $$"""{"id":"s{{i}}"}"""));
        m = SectionManifestParser.Parse("p", "{\"servers\":[" + servers + "]}", out _);
        Check(m.Servers.Length == SectionManifestParser.MaxServers, "servers capped");

        string big = "{\"x\":\"" + new string('a', SectionManifestParser.MaxJsonLength) + "\"}";
        Check(SectionManifestParser.Parse("p", big, out string e).Contributions.Length == 0 && e.StartsWith("too large"), "size capped");

        string longLabel = new string('L', 200);
        m = SectionManifestParser.Parse("p", $$"""{"contributions":[{"section":"char","label":"{{longLabel}}","command":"/a"}]}""", out _);
        Check(m.Contributions[0].Label.Length == SectionManifestParser.MaxLabelLength, "labels capped");
    }

    // ── Catalog ────────────────────────────────────────────────────────

    private static readonly ServerState IltOn = new("ilt", "Infinite Leaftide", true, true, ServerForce.Auto, true, "world");
    private static readonly ServerState IltOff = IltOn with { Active = false, Detected = false };
    private static readonly ServerState AelOff = new("aelrynth", "Aelrynth", false, false, ServerForce.Auto, true, "");

    private static bool Panels(string title) => title is "Radar" or "Skills" or "Nav" or "Inventory" or "Items" or "Chat";

    private static void CatalogCore()
    {
        Case("catalog core");
        List<SectionView> views = SectionCatalog.Build(Array.Empty<SectionManifest>(), Panels, new[] { IltOff, AelOff });
        string[] ids = views.Select(v => v.Id).ToArray();
        Check(ids.SequenceEqual(new[] { "char", "map", "skills", "combat", "nav", "inventory", "crafting", "spells", "chat", "friends", "fellowship", "pets" }),
            "the twelve core sections, in order: " + string.Join(",", ids));
        Check(views.All(v => v.Core), "all core");
        Check(views[0].Panes.Single().Kind == SectionPaneKind.Overview, "Char starts with the overview");
        Check(views[0].Label == SectionCatalog.SessionStatsLabel && views[0].Label == "Session Stats", "the char section is labelled Session Stats");

        // A plugin's universal primary surface in char (RynthAi's session stats) replaces the overview.
        var session = SectionManifestParser.Parse("RynthAi", """
        { "contributions": [ { "section": "char", "label": "Session stats", "surface": "section:session", "primary": true } ] }
        """, out _);
        SectionView chr = SectionCatalog.Build(new[] { session }, Panels, new[] { IltOff }).Single(v => v.Id == "char");
        Check(chr.Panes.Single().Kind == SectionPaneKind.Surface && chr.Panes[0].Contribution!.Surface == "section:session",
            "a universal primary surface drops the overview");
        // A server-scoped primary surface doesn't: the overview stays beside it.
        var scoped = SectionManifestParser.Parse("p", """
        { "contributions": [ { "section": "char", "server": "ilt", "label": "ILT", "surface": "section:ilt", "primary": true } ] }
        """, out _);
        chr = SectionCatalog.Build(new[] { scoped }, Panels, new[] { IltOn }).Single(v => v.Id == "char");
        Check(chr.Panes.Any(p => p.Kind == SectionPaneKind.Overview), "a server-scoped surface keeps the overview");
        Check(views[1].Panes.Select(p => p.Panel).SequenceEqual(new[] { "Radar" }), "Map has only the panels that exist");
        Check(views[5].Panes.Select(p => p.Panel).SequenceEqual(new[] { "Inventory", "Items" }), "Inventory keeps panel order");
        Check(!views[3].HasContent && !views[11].HasContent, "Combat (no panels here) and Pets are empty");
        Check(SectionCatalog.FindCore("PETS")?.Id == "pets" && SectionCatalog.FindCore("remote") == null, "FindCore");
        Check(SectionCatalog.Build(Array.Empty<SectionManifest>(), _ => throw new InvalidOperationException(), new[] { IltOff })
            .All(v => v.Panes.All(p => p.Kind != SectionPaneKind.CorePanel)), "a throwing panel check means no panels");
    }

    private static void CatalogServers()
    {
        Case("catalog servers");
        SectionManifest m = SectionManifestParser.Parse("RynthAi", GoodManifest, out _);

        List<SectionView> off = SectionCatalog.Build(new[] { m }, Panels, new[] { IltOff, AelOff });
        SectionView pets = off.Single(v => v.Id == "pets");
        Check(pets.Panes.Single().Kind == SectionPaneKind.Surface && pets.Panes[0].Contribution!.Surface == "section:pets", "a primary surface is a pane");
        SectionView skills = off.Single(v => v.Id == "skills");
        Check(skills.Groups.Length == 0, "ILT items hidden while ILT is off");

        List<SectionView> on = SectionCatalog.Build(new[] { m }, Panels, new[] { IltOn, AelOff });
        skills = on.Single(v => v.Id == "skills");
        Check(skills.Groups.Length == 1 && skills.Groups[0].Title == "Server: Infinite Leaftide" && skills.Groups[0].Server == IltOn, "ILT group while active");
        Check(skills.Groups[0].Items.Single().Command == "/rc progression", "its command");
        SectionView chr = on.Single(v => v.Id == "char");
        Check(chr.Groups.Single().Items.Single().Label == "Off now", "disabled items still listed (with their note)");

        var universal = SectionManifestParser.Parse("Other", """
        { "contributions": [ { "section": "skills", "label": "Z", "command": "/z", "order": 5 },
                             { "section": "skills", "label": "A", "command": "/a", "order": 5 } ] }
        """, out _);
        skills = SectionCatalog.Build(new[] { m, universal }, Panels, new[] { IltOn }).Single(v => v.Id == "skills");
        Check(skills.Groups.Select(g => g.Title).SequenceEqual(new[] { "Server: Infinite Leaftide", "Plugins" }), "server groups first, then Plugins");
        Check(skills.Groups[1].Items.Select(i => i.Label).SequenceEqual(new[] { "A", "Z" }), "equal orders sort by label");
    }

    private static void CatalogPluginSections()
    {
        Case("catalog plugin sections");
        SectionManifest m = SectionManifestParser.Parse("RynthAi", GoodManifest, out _);
        List<SectionView> views = SectionCatalog.Build(new[] { m }, Panels, new[] { IltOff });
        int quests = views.FindIndex(v => v.Id == "quests");
        Check(quests >= 0 && !views[quests].Core, "the plugin's Quests section shows");
        Check(views[quests - 1].Id == "crafting" && views[quests + 1].Id == "spells", "order 650 puts it between Crafting (600) and Spells (700)");
        var empty = SectionManifestParser.Parse("p", """{ "sections": [ { "id": "empty", "label": "Empty" } ] }""", out _);
        Check(SectionCatalog.Build(new[] { empty }, Panels, new[] { IltOff }).All(v => v.Id != "empty"), "an empty plugin section is hidden");
        var hijack = SectionManifestParser.Parse("p", """
        { "sections": [ { "id": "pets", "label": "Mine" }, { "id": "remote", "label": "Mine" } ],
          "contributions": [ { "section": "remote", "label": "x", "command": "/x" } ] }
        """, out _);
        views = SectionCatalog.Build(new[] { hijack }, Panels, new[] { IltOff });
        Check(views.Count(v => v.Id == "pets") == 1 && views.Single(v => v.Id == "pets").Label == "Pets", "core ids can't be redeclared");
        Check(views.All(v => v.Id != "remote"), "the remote id can't be declared");
        var dup = SectionManifestParser.Parse("q", """
        { "sections": [ { "id": "quests", "label": "Other quests", "order": 1 } ],
          "contributions": [ { "section": "quests", "label": "x", "command": "/x" } ] }
        """, out _);
        views = SectionCatalog.Build(new[] { m, dup }, Panels, new[] { IltOff });
        SectionView q = views.Single(v => v.Id == "quests");
        Check(q.Label == "Quests" && q.Order == 650, "the first declaration wins");
        Check(q.Panes.Length == 1 && q.Groups.Single().Items.Single().Plugin == "q", "both plugins' contributions land in it");
    }

    private static void DeclaredServers()
    {
        Case("declared servers");
        var decl = new SectionServerDecl("frosthaven", "Frosthaven", new[] { "Frosthaven*" }, new[] { "frosthaven.example" });
        Check(SectionCatalog.DetectDeclared(decl, "play.frosthaven.example", null, out string why) && why.Contains("host"), "by host");
        Check(SectionCatalog.DetectDeclared(decl, null, "Frosthaven PvE", out why) && why.Contains("world"), "by world name");
        Check(!SectionCatalog.DetectDeclared(decl, "infiniteleaftide.online", "InfiniteLeaftide", out _), "not another server");
        var none = new SectionServerDecl("x", "X", Array.Empty<string>(), Array.Empty<string>());
        Check(!SectionCatalog.DetectDeclared(none, "a", "b", out why) && why.StartsWith("no host or world-name list"), "no lists");
    }

    // -- Server selection (Diagnostics picker, "/rc server pick", plugin API v86) --

    /// <summary>A server row as SectionRegistry builds it (Active = override applied to detection).</summary>
    private static ServerState Srv(string id, bool detected, ServerForce force, bool builtIn = false)
        => new(id, id.ToUpperInvariant(), ServerForceText.Apply(force, detected), detected, force, builtIn, detected ? "host matches" : "");

    private static void SelectionPlan()
    {
        Case("selection plan");
        var servers = new[] { Srv("ilt", true, ServerForce.Auto, true), Srv("aelrynth", false, ServerForce.Off, true), Srv("frost", false, ServerForce.Auto) };

        var pick = ServerSelection.Plan(servers, "FROST");
        Check(pick != null && pick.Count == 3, "a known id plans every server");
        Check(pick!.Single(p => p.Id == "frost").Force == ServerForce.On, "the pick is forced On (case-insensitive)");
        Check(pick.Where(p => p.Id != "frost").All(p => p.Force == ServerForce.Off), "every other server is forced Off");

        var auto = ServerSelection.Plan(servers, " Auto ");
        Check(auto != null && auto.All(p => p.Force == ServerForce.Auto), "auto resets every override");
        Check(ServerSelection.Plan(servers, "")!.All(p => p.Force == ServerForce.Auto), "empty means auto");
        Check(ServerSelection.Plan(servers, null)!.All(p => p.Force == ServerForce.Auto), "null means auto");
        Check(ServerSelection.Plan(servers, "nowhere") == null, "an unknown id is refused");
        Check(ServerSelection.Plan(Array.Empty<ServerState>(), "auto")!.Count == 0, "no servers, empty plan");
    }

    private static void NotLoadedServerList()
    {
        Case("not-loaded server plugins");
        var forces = new Dictionary<string, ServerForce>(StringComparer.OrdinalIgnoreCase) { ["coldeve"] = ServerForce.On };
        ServerForce Force(string id) => forces.TryGetValue(id, out ServerForce f) ? f : ServerForce.Auto;
        var notLoaded = new[]
        {
            new NotLoadedServer("frost", "Frosthaven", "RynthFrostACE"),
            new NotLoadedServer("coldeve", "Coldeve", "RynthColdeve"),
            new NotLoadedServer("ilt", "Infinite Leaftide", "RynthILT"),   // a built-in id: the built-in row wins
            new NotLoadedServer("", "No id", "RynthBroken"),
        };
        var list = new List<ServerState> { Srv("ilt", true, ServerForce.Auto, true) };
        NotLoadedServers.Append(list, notLoaded, Force);
        Check(list.Count == 3, "each new id added once, the built-in and the blank id skipped");
        ServerState frost = list.Single(s => s.Id == "frost");
        Check(!frost.Active && !frost.Detected && !frost.BuiltIn && frost.Name == "Frosthaven", "not loaded: listed, not detected, not active");
        Check(frost.Reason.Contains("RynthFrostACE") && frost.Reason.Contains("pick it"), "the reason names the plugin and how to load it");
        ServerState coldeve = list.Single(s => s.Id == "coldeve");
        Check(coldeve.Active && coldeve.Force == ServerForce.On && coldeve.Reason.Contains("loading RynthColdeve"), "forced On: active, loading its plugin");
        Check(list.Single(s => s.Id == "ilt").BuiltIn, "the built-in ilt row is kept");
        Check(ServerSelection.Plan(list, "frost") != null, "a not-loaded server can be picked");

        var load = NotLoadedServers.ForcedOn(notLoaded, Force);
        Check(load.Count == 1 && load[0] == "coldeve", "only the server forced On asks for its plugin");
        forces["coldeve"] = ServerForce.Off;
        Check(NotLoadedServers.ForcedOn(notLoaded, Force).Count == 0, "Off loads nothing");

        // The connect host is nobody's (none load at start); the world name the server announces
        // loads its plugin late, unless that server is forced Off.
        var byWorld = new[]
        {
            new NotLoadedServer("frost", "Frosthaven", "RynthFrostACE", new[] { "Frosthaven*" }),
            new NotLoadedServer("coldeve", "Coldeve", "RynthColdeve", new[] { "Coldeve" }),
        };
        Check(NotLoadedServers.ToLoad(byWorld, Force, null).Count == 0, "before login: nothing loads by world name");
        var hit = NotLoadedServers.ToLoad(byWorld, Force, "Frosthaven PvE");
        Check(hit.Count == 1 && hit[0] == "frost", "the announced world name loads its server's plugin");
        Check(NotLoadedServers.ToLoad(byWorld, Force, "Coldeve").Count == 0, "a server forced Off isn't loaded by its world name");
        Check(NotLoadedServers.ToLoad(byWorld, Force, "Somewhere Else").Count == 0, "a world nobody lists loads nothing");
    }

    private static void SelectionCurrentPick()
    {
        Case("selection current pick");
        Check(ServerSelection.CurrentPick(new[] { Srv("ilt", true, ServerForce.Auto), Srv("ael", false, ServerForce.Auto) }) == ServerSelection.Auto, "no overrides is auto");
        Check(ServerSelection.CurrentPick(new[] { Srv("ilt", true, ServerForce.Off), Srv("ael", false, ServerForce.On) }) == "ael", "one On, rest Off is that pick");
        Check(ServerSelection.CurrentPick(new[] { Srv("ilt", true, ServerForce.On), Srv("ael", false, ServerForce.On) }) == null, "two On is a mix");
        Check(ServerSelection.CurrentPick(new[] { Srv("ilt", true, ServerForce.On), Srv("ael", false, ServerForce.Auto) }) == null, "one On, another Auto is a mix");
        Check(ServerSelection.CurrentPick(new[] { Srv("ilt", true, ServerForce.Off), Srv("ael", false, ServerForce.Auto) }) == null, "only Offs is a mix");
        Check(ServerSelection.CurrentPick(Array.Empty<ServerState>()) == ServerSelection.Auto, "no servers is auto");

        // A plan applied gives back the same pick.
        var servers = new[] { Srv("ilt", true, ServerForce.Auto), Srv("ael", false, ServerForce.Auto) };
        var applied = ServerSelection.Plan(servers, "ael")!.Select(p => Srv(p.Id, servers.Single(s => s.Id == p.Id).Detected, p.Force)).ToArray();
        Check(ServerSelection.CurrentPick(applied) == "ael", "plan round trip");
    }

    private static void SelectionActiveAndOwner()
    {
        Case("selection active and owner");
        Check(ServerSelection.ActiveServer(new[] { Srv("ilt", true, ServerForce.Auto), Srv("frost", true, ServerForce.Auto) })!.Id == "ilt", "first detected wins");
        Check(ServerSelection.ActiveServer(new[] { Srv("ilt", true, ServerForce.Auto), Srv("frost", false, ServerForce.On) })!.Id == "frost", "forced On beats detection");
        Check(ServerSelection.ActiveServer(new[] { Srv("ilt", true, ServerForce.Off) }) == null, "Off hides a detected server");
        Check(ServerSelection.ActiveServer(Array.Empty<ServerState>()) == null, "none");

        var pack = SectionManifestParser.Parse("RynthServerPack", """
        { "servers": [ { "id": "frost", "name": "Frosthaven", "hosts": [ "frost.example" ] } ] }
        """, out _);
        var ilt = SectionManifestParser.Parse("RynthILT", """
        { "contributions": [ { "section": "skills", "server": "ilt", "label": "A", "command": "/a" },
                             { "section": "char", "server": "ilt", "label": "B", "command": "/b" } ] }
        """, out _);
        var ai = SectionManifestParser.Parse("RynthAi", """
        { "contributions": [ { "section": "skills", "server": "ilt", "label": "C", "command": "/c" },
                             { "section": "skills", "server": "frost", "label": "D", "command": "/d" } ] }
        """, out _);
        var engine = SectionManifestParser.Parse("RynthCore", """
        { "servers": [ { "id": "ilt", "name": "Infinite Leaftide" } ] }
        """, out _);
        var all = new[] { engine, ai, ilt, pack };

        Check(ServerSelection.OwnerPlugin(all, "frost", "RynthCore") == "RynthServerPack", "the declaring plugin owns a server");
        Check(ServerSelection.OwnerPlugin(all, "ILT", "RynthCore") == "RynthILT", "else the most contributions (engine skipped)");
        Check(ServerSelection.OwnerPlugin(all, "nowhere", "RynthCore") == "", "nobody");
        var tie = SectionManifestParser.Parse("Alpha", """
        { "contributions": [ { "section": "skills", "server": "x", "label": "A", "command": "/a" } ] }
        """, out _);
        var tie2 = SectionManifestParser.Parse("Beta", """
        { "contributions": [ { "section": "skills", "server": "x", "label": "A", "command": "/a" } ] }
        """, out _);
        Check(ServerSelection.OwnerPlugin(new[] { tie2, tie }, "x", "RynthCore") == "Alpha", "ties go to the name that sorts first");
    }

    private static unsafe void StateJson()
    {
        Case("state json");
        var ilt = SectionManifestParser.Parse("RynthILT", """
        { "contributions": [ { "section": "skills", "server": "ilt", "label": "A", "command": "/a" } ] }
        """, out _);
        var servers = new[] { Srv("ilt", true, ServerForce.Auto, true), Srv("aelrynth", false, ServerForce.Auto, true) };
        string json = ServerStateJson.Build(servers, new[] { ilt }, 7, "infiniteleaftide.online", "Infinite \"Leaf\"", "RynthCore");

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        Check(root.GetProperty("schema").GetInt32() == ServerStateJson.Schema && root.GetProperty("revision").GetInt32() == 7, "schema and revision");
        Check(root.GetProperty("host").GetString() == "infiniteleaftide.online", "host");
        Check(root.GetProperty("world").GetString() == "Infinite \"Leaf\"", "world (quotes escaped)");
        Check(root.GetProperty("pick").GetString() == "auto", "pick auto");
        Check(root.GetProperty("active").GetString() == "ilt" && root.GetProperty("activeName").GetString() == "ILT", "active server");
        Check(root.GetProperty("activePlugin").GetString() == "RynthILT", "active server's owner plugin");
        var rows = root.GetProperty("servers");
        Check(rows.GetArrayLength() == 2, "every server listed");
        var first = rows[0];
        Check(first.GetProperty("id").GetString() == "ilt" && first.GetProperty("active").GetBoolean() && first.GetProperty("detected").GetBoolean(), "row state");
        Check(first.GetProperty("force").GetString() == "auto" && first.GetProperty("builtIn").GetBoolean(), "row override and built-in");
        Check(rows[1].GetProperty("plugin").GetString() == "" && rows[1].GetProperty("reason").GetString() == "", "row with no owner");

        var mixed = new[] { Srv("ilt", true, ServerForce.On), Srv("ael", false, ServerForce.On) };
        using (var m = System.Text.Json.JsonDocument.Parse(ServerStateJson.Build(mixed, Array.Empty<SectionManifest>(), 1, null, null, "RynthCore")))
            Check(m.RootElement.GetProperty("pick").GetString() == "mixed" && m.RootElement.GetProperty("host").GetString() == "", "mixed pick, null host");
        using (var n = System.Text.Json.JsonDocument.Parse(ServerStateJson.Build(new[] { Srv("ilt", false, ServerForce.Auto) }, Array.Empty<SectionManifest>(), 1, "h", "w", "RynthCore")))
            Check(n.RootElement.GetProperty("active").GetString() == "" && n.RootElement.GetProperty("activePlugin").GetString() == "", "no active server");

        // Write: the plugin API's buffer convention (length returned; nothing written when too small).
        int length = System.Text.Encoding.UTF8.GetByteCount(json);
        byte[] small = new byte[length];
        fixed (byte* p = small)
            Check(ServerStateJson.Write(json, p, small.Length) == length && small.All(b => b == 0), "too small: length only, nothing written");
        Check(ServerStateJson.Write(json, null, 0) == length, "null buffer: length only");
        byte[] big = new byte[length + 1];
        fixed (byte* p = big)
            Check(ServerStateJson.Write(json, p, big.Length) == length, "fits: length");
        Check(System.Text.Encoding.UTF8.GetString(big, 0, length) == json && big[length] == 0, "fits: text and NUL");
    }

    // ── Plugin windows (v89, docs/PLUGIN_WINDOWS.md) ───────────────────

    private const string WindowsManifest = """
    { "version": 1,
      "windows": [
        { "id": "characters", "title": "Characters", "surface": "window:characters", "icon": "\uE68E",
          "width": 420, "height": 260, "minWidth": 280, "minHeight": 140, "bar": true, "edgeToEdge": true },
        { "id": "bank", "title": "Bank", "surface": "window:bank", "server": "ilt" },
        { "id": "tiny", "title": "Tiny", "surface": "window:tiny", "width": 10, "height": 99999, "minWidth": 5000 },
        { "id": "badicon", "title": "Bad Icon", "surface": "window:x", "icon": "AB" },
        { "id": "characters", "title": "Again", "surface": "window:again" },
        { "id": "dupe", "title": "characters", "surface": "window:dupe" },
        { "id": "Bad Id", "title": "No", "surface": "window:no" },
        { "id": "nosurface", "title": "No surface" },
        { "id": "popout", "title": "Popout", "surface": "window:x:popout" },
        { "id": "srv", "title": "Bad server", "surface": "window:s", "server": "Not An Id" }
      ] }
    """;

    private static void ManifestWindows()
    {
        Case("manifest: windows a plugin draws");
        SectionManifest m = SectionManifestParser.Parse("RynthNet", WindowsManifest, out string err);
        Check(err.Length == 0, "parses: " + err);
        Check(m.Windows.Length == 4, $"four kept (got {m.Windows.Length}: {string.Join(", ", m.Windows.Select(w => w.Title))})");
        SectionWindowDecl c = m.Windows[0];
        Check(c.Plugin == "RynthNet" && c.Id == "characters" && c.Title == "Characters" && c.Surface == "window:characters", "identity");
        Check(c.Icon == "\uE68E" && c.Bar && c.EdgeToEdge && c.IsUniversal, "icon, bar, edge to edge, every world");
        Check(c.Width == 420 && c.Height == 260 && c.MinWidth == 280 && c.MinHeight == 140, "sizes as given");
        SectionWindowDecl bank = m.Windows[1];
        Check(bank.Server == "ilt" && !bank.IsUniversal && !bank.Bar && bank.Icon == "", "a server's window, no bar, no icon");
        Check(bank.Width == SectionManifestParser.DefaultWindowWidth && bank.Height == SectionManifestParser.DefaultWindowHeight, "default size");
        SectionWindowDecl tiny = m.Windows[2];
        Check(tiny.Width == SectionManifestParser.MinWindowSize && tiny.Height == SectionManifestParser.MaxWindowSize, "sizes clamped");
        Check(tiny.MinWidth <= tiny.Width && tiny.MinHeight <= tiny.Height, "the minimum never exceeds the size");
        Check(m.Windows[3].Title == "Bad Icon" && m.Windows[3].Icon == "", "an icon outside the icon font is dropped, the window kept");
        Check(SectionManifestParser.Icon("\uE68E\uE4D6") == "\uE68E\uE4D6" && SectionManifestParser.Icon("\uE68E\uE4D6\uE000") == "", "one or two glyphs");
        // An old manifest has none; an engine before v89 just ignores the key.
        Check(SectionManifestParser.Parse("p", GoodManifest, out _).Windows.Length == 0, "no windows key: none");
        var many = "{\"windows\":[" + string.Join(",", Enumerable.Range(0, 20).Select(i => $"{{\"id\":\"w{i}\",\"title\":\"W{i}\",\"surface\":\"window:w{i}\"}}")) + "]}";
        Check(SectionManifestParser.Parse("p", many, out _).Windows.Length == SectionManifestParser.MaxWindows, "capped at MaxWindows");
    }

    private static void WindowPlanChoices()
    {
        Case("plugin windows: which are hosted");
        SectionManifest net = SectionManifestParser.Parse("RynthNet", WindowsManifest, out _);
        SectionManifest other = SectionManifestParser.Parse("Other",
            "{\"windows\":[{\"id\":\"c\",\"title\":\"Characters\",\"surface\":\"window:c\"},{\"id\":\"inv\",\"title\":\"Inventory\",\"surface\":\"window:i\"}]}", out _);
        var offIlt = new[] { new ServerState("ilt", "Infinite Leaftide", false, false, ServerForce.Auto, true, "") };
        var onIlt = new[] { new ServerState("ilt", "Infinite Leaftide", true, true, ServerForce.Auto, true, "") };
        var refused = new List<string>();
        var takeOvers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plan = WindowPlan.Build(new[] { net, other }, offIlt, t => t == "Inventory" ? TitleUse.EnginePanel : TitleUse.Free, refused, takeOvers);
        Check(plan.ContainsKey("Characters") && plan["Characters"].Plugin == "RynthNet", "the first plugin keeps a title");
        Check(!plan.ContainsKey("Bank"), "a server's window waits for its server");
        Check(!plan.ContainsKey("Inventory"), "an engine panel's title is refused without takeOver");
        Check(refused.Count == 2 && refused.Exists(r => r.Contains("Other") && r.Contains("RynthNet")) && refused.Exists(r => r.Contains("engine panel")),
            "both refusals say why: " + string.Join(" | ", refused));
        Check(takeOvers.Count == 0, "nothing taken over");
        var on = WindowPlan.Build(new[] { net }, onIlt, _ => TitleUse.Free, new List<string>(), new HashSet<string>());
        Check(on.ContainsKey("Bank"), "and shows while its server is active");
        Check(WindowPlan.Signature(net.Windows[0]) != WindowPlan.Signature(net.Windows[1]), "signatures differ per window");
    }

    // A plugin window that declares "takeOver" draws in an engine panel's place (same title, same key).
    private const string TakeOverManifest = """
    { "windows": [
        { "id": "inv", "title": "Inventory", "surface": "window:inv", "bar": true, "takeOver": true },
        { "id": "chat", "title": "Chat", "surface": "window:chat", "takeOver": true },
        { "id": "lua", "title": "Lua", "surface": "window:lua", "takeOver": "yes" },
        { "id": "notes", "title": "Notes", "surface": "window:notes", "takeOver": true },
        { "id": "sort", "title": "RynthSort", "surface": "window:sort", "takeOver": true }
      ] }
    """;

    private static void WindowTakeOver()
    {
        Case("plugin windows: taking over an engine panel");
        SectionManifest m = SectionManifestParser.Parse("RynthAi", TakeOverManifest, out string err);
        Check(err.Length == 0 && m.Windows.Length == 5, "parses: " + err);
        Check(m.Windows[0].TakeOver && m.Windows[1].TakeOver && m.Windows[3].TakeOver, "\"takeOver\": true read");
        Check(!m.Windows[2].TakeOver, "not a boolean: off");
        Check(!SectionManifestParser.Parse("RynthNet", WindowsManifest, out _).Windows[0].TakeOver, "absent: off (as before)");
        Check(WindowPlan.Signature(m.Windows[0]) != WindowPlan.Signature(m.Windows[0] with { TakeOver = false }),
            "takeOver is in the signature (a change re-registers)");

        // Inventory, Chat and Lua are engine panels; RynthSort is a script window's key; Notes is free.
        TitleUse Use(string t) => t switch
        {
            "Inventory" or "Chat" or "Lua" => TitleUse.EnginePanel,
            "RynthSort" => TitleUse.Other,
            _ => TitleUse.Free,
        };
        var refused = new List<string>();
        var takeOvers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var none = Array.Empty<ServerState>();
        var plan = WindowPlan.Build(new[] { m }, none, Use, refused, takeOvers);
        Check(plan.ContainsKey("Inventory") && plan.ContainsKey("Chat") && takeOvers.SetEquals(new[] { "Inventory", "Chat" }),
            "declared takeovers of engine panels are hosted and marked: " + string.Join(", ", takeOvers));
        Check(!plan.ContainsKey("Lua") && refused.Exists(r => r.Contains("'Lua'") && r.Contains("takeOver")),
            "an engine panel's title without takeOver is still refused, and the line says how");
        Check(plan.ContainsKey("Notes") && !takeOvers.Contains("Notes"), "takeOver on a free title: an ordinary window (the engine face is gone)");
        Check(!plan.ContainsKey("RynthSort") && refused.Exists(r => r.Contains("RynthSort") && r.Contains("another panel")),
            "never another panel's (a script window's) title, takeOver or not");

        // Two plugins taking over the same panel: the first keeps it.
        SectionManifest second = SectionManifestParser.Parse("RynthInventory",
            "{\"windows\":[{\"id\":\"inv\",\"title\":\"inventory\",\"surface\":\"window:i\",\"takeOver\":true}]}", out _);
        var refused2 = new List<string>();
        var taken2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plan2 = WindowPlan.Build(new[] { m, second }, none, Use, refused2, taken2);
        Check(plan2["Inventory"].Plugin == "RynthAi" && refused2.Exists(r => r.Contains("RynthInventory") && r.Contains("RynthAi")),
            "the first plugin to take a panel over keeps it (titles match without case)");

        // The plugin goes: its manifest is gone, so the panel isn't in the plan (the engine's face comes back).
        var after = WindowPlan.Build(Array.Empty<SectionManifest>(), none, Use, new List<string>(), taken2 = new HashSet<string>());
        Check(after.Count == 0 && taken2.Count == 0, "no manifest: nothing hosted, nothing taken over");
    }

    // v90: a window that draws its own frame ("chrome": "none") and fits its height to its content.
    private const string FramelessManifest = """
        { "version": 1, "windows": [
        { "id": "dashboard", "title": "RynthAi", "surface": "window:dashboard", "takeOver": true,
          "chrome": "none", "fitHeight": true, "width": 296, "height": 240, "minWidth": 280, "minHeight": 54 },
        { "id": "plain", "title": "Notes", "surface": "window:notes" },
        { "id": "odd", "title": "Odd", "surface": "window:odd", "chrome": " NONE ", "fitHeight": "yes" },
        { "id": "framed", "title": "Framed", "surface": "window:framed", "chrome": "standard" } ] }
        """;

    private static void WindowFrameless()
    {
        Case("plugin windows: frameless and fitted (v90)");
        SectionManifest m = SectionManifestParser.Parse("RynthAi", FramelessManifest, out string err);
        Check(err.Length == 0 && m.Windows.Length == 4, "parses: " + err);
        Check(m.Windows[0].Frameless && m.Windows[0].FitHeight && m.Windows[0].TakeOver, "\"chrome\": \"none\" and \"fitHeight\": true read");
        Check(!m.Windows[1].Frameless && !m.Windows[1].FitHeight, "absent: framed and sized by the player (as before)");
        Check(m.Windows[2].Frameless && !m.Windows[2].FitHeight, "\"none\" in any case, padded; fitHeight not a boolean: off");
        Check(!m.Windows[3].Frameless, "any other chrome: the engine's frame");
        Check(m.Windows[0].Width == 296 && m.Windows[0].MinWidth == 280, "sizes kept (the minimum height has the parser's floor)");
        Check(WindowPlan.Signature(m.Windows[0]) != WindowPlan.Signature(m.Windows[0] with { Frameless = false })
              && WindowPlan.Signature(m.Windows[0]) != WindowPlan.Signature(m.Windows[0] with { FitHeight = false }),
            "chrome and fitHeight are in the signature (a change re-registers)");
    }

    // ImGui 1.91.6's ImGuiErrorRecoveryState, in field order: windows, IDs, tree nodes, colours,
    // style vars, fonts, focus scopes, groups, item flags, popups, disabled. The base values are
    // the ones a 32-bit probe of our cimgui.dll read inside a panel's body child (2026-10-21).
    private static short[] Rec(params short[] f) => f;

    private static void StackCheckAfterPluginDraw()
    {
        Case("plugin windows: ImGui stacks after a plugin's draw");
        short[] given = Rec(3, 2, 0, 0, 0, 1, 3, 0, 1, 0, 0);
        Check(ImGuiStackState.Fields == 11 && ImGuiStackState.RecordBytes == 22 && ImGuiStackState.Names.Length == 11,
            "11 shorts, 22 bytes, a name each");
        Check(ImGuiStackState.Compare(given, (short[])given.Clone()) == StackCheck.Balanced, "as found: balanced");
        Check(ImGuiStackState.IdsAtBase(Rec(3, 1, 0, 0, 0, 0, 3, 0, 1, 0, 0)), "the body's start (one ID) is a base to recover to");
        Check(!ImGuiStackState.IdsAtBase(given), "inside the face's PushID it isn't (the recovery would pop the caller's IDs)");

        // Probe frame 1: two colours, a style var, two IDs, a font, a group, a disabled block, a tree node.
        short[] leaked = Rec(3, 4, 1, 2, 1, 2, 3, 1, 1, 0, 1);
        Check(ImGuiStackState.Compare(given, leaked) == StackCheck.LeftOpen, "pushes left open: ImGui unwinds them");
        string what = ImGuiStackState.Describe(given, leaked);
        Check(what == "IDs +2, tree nodes +1, colours +2, style vars +1, fonts +1, groups +1, disabled +1",
            "the log says what was left: " + what);
        Check(ImGuiStackState.CanRecover(given, leaked), "same window depth: recoverable");

        // A child window left open (with a table inside): deeper, still recoverable.
        short[] child = Rec(4, 1, 0, 1, 0, 1, 5, 0, 1, 0, 0);
        Check(ImGuiStackState.Compare(given, child) == StackCheck.LeftOpen,
            "a child window left open (its own IDs aren't compared with the body's)");
        Check(ImGuiStackState.Describe(given, child) == "windows +1, colours +1, focus scopes +2",
            "the log leaves out the other window's IDs: " + ImGuiStackState.Describe(given, child));
        Check(ImGuiStackState.CanRecover(given, child), "a deeper window stack is recoverable");
        short[] childPopped = Rec(4, 1, 0, -1, 0, 1, 5, 0, 1, 0, 0);
        Check(ImGuiStackState.Compare(given, childPopped) == StackCheck.ClosedTooMuch, "a context stack below, even in a deeper window");

        // The plugin ended the host's body child (one End too many) and left a colour and a font.
        short[] ended = Rec(2, 1, 0, 1, 0, 2, 2, 0, 1, 0, 0);
        Check(ImGuiStackState.Compare(given, ended) == StackCheck.ClosedTooMuch, "ended a window it didn't begin");
        Check(!ImGuiStackState.CanRecover(given, ended), "ImGui's recovery can't go back up to the host's window");
        Check(ImGuiStackState.GlobalPops(given, ended) == (1, 0, 1), "only the context's colours and fonts are popped by hand");
        Check(ImGuiStackState.Describe(given, ended) == "windows -1, colours +1, fonts +1, focus scopes -1",
            "negative changes are shown with their sign: " + ImGuiStackState.Describe(given, ended));

        // Popped the host's colours: below the record, nothing to pop.
        short[] overPopped = Rec(3, 2, 0, -2, 0, 1, 3, 0, 1, 0, 0);
        Check(ImGuiStackState.Compare(given, overPopped) == StackCheck.ClosedTooMuch, "popped what it didn't push");
        Check(ImGuiStackState.GlobalPops(given, overPopped) == (0, 0, 0), "never pops below the record");
        Check(ImGuiStackState.Describe(given, given) == "nothing", "no change: nothing");

        // The first-use size check (the record filled with 0xCC, then written by ImGui).
        byte[] rec = Enumerable.Repeat((byte)0xCC, 64).ToArray();
        for (int i = 0; i < 22; i++) rec[i] = (byte)(i % 2 == 0 ? 1 : 0);
        Check(ImGuiStackState.WroteExpectedSize(rec, 0xCC), "22 bytes written: the layout expected");
        rec[30] = 0;
        Check(!ImGuiStackState.WroteExpectedSize(rec, 0xCC), "more written: a different layout, the guard stays off");
        Check(!ImGuiStackState.WroteExpectedSize(Enumerable.Repeat((byte)0xCC, 64).ToArray(), 0xCC), "nothing written: off");
    }

    // ── Harness ────────────────────────────────────────────────────────

    private static void Case(string name)
    {
        _case = name;
        Console.WriteLine($"-- {name}");
    }

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (ok) return;
        _failed++;
        Console.WriteLine($"FAIL [{_case}] {what}");
    }
}
