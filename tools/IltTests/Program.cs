using RynthCore.Engine.Compatibility;

namespace IltTests;

/// <summary>
/// Infinite Leaftide detection, offline. The host is the public AC server list's
/// (acresources/serverslist Servers.xml: InfiniteLeaftide, game.infiniteleaftide.online:9000);
/// the world name is the one UtilityBelt's Leaftide tools and the ILT Hub check for.
/// </summary>
internal static class Program
{
    private static int _checks, _failed;
    private static string _case = "";

    private static int Main()
    {
        Hosts();
        WorldNames();
        Decisions();
        ConfiguredLists();
        FromCommandLines();
        Wildcards();
        PluginVerdict();

        Console.WriteLine();
        Console.WriteLine($"{_checks - _failed}/{_checks} checks passed.");
        if (_checks == 0) { Console.WriteLine("ABORT: no checks ran."); return 1; }
        Console.WriteLine(_failed == 0 ? "ALL ILT TESTS PASSED." : $"{_failed} FAILED.");
        return _failed == 0 ? 0 : 1;
    }

    // RynthILT's own verdict (2026-10-13: every ILT feature is RynthILT's, which keeps its own world
    // list and a per-character "Treat this world as ILT"): read from RynthILTGetStatusJson, added to
    // the engine's.
    private static void PluginVerdict()
    {
        Case("RynthILT's verdict");
        Check(IltDetect.ParsePluginStatus("{\"loaded\":true,\"ilt\":true,\"forced\":true,\"available\":false,\"world\":\"MyShard\"}",
                  out bool loaded, out bool ilt, out bool forced, out bool available, out string world)
              && loaded && ilt && forced && !available && world == "MyShard", "the status JSON parses");
        Check(IltDetect.ParsePluginStatus("{\"loaded\":true}", out loaded, out ilt, out forced, out available, out world)
              && loaded && !ilt && !forced && !available && world == "", "missing fields read false / empty");
        Check(!IltDetect.ParsePluginStatus("not json", out _, out _, out _, out _, out _), "garbage is refused");
        Check(!IltDetect.ParsePluginStatus("[]", out _, out _, out _, out _, out _), "not an object is refused");
        Check(!IltDetect.ParsePluginStatus(null, out _, out _, out _, out _, out _), "nothing is refused");

        var no = IltDetect.Decide(null, "Aeshnidae", null, null);
        var yes = IltDetect.Decide("game.infiniteleaftide.online", null, null, null);
        var forcedHere = IltDetect.Combine(no, true, "RynthILT: \"Treat this world as ILT\" is ticked for this character");
        Check(forcedHere.IsIlt && forcedHere.Reason.Contains("Treat this world"), "RynthILT's override makes it ILT: " + forcedHere.Reason);
        Check(!IltDetect.Combine(no, false, "x").IsIlt, "RynthILT saying no changes nothing");
        Check(ReferenceEquals(IltDetect.Combine(yes, false, ""), yes), "RynthILT can't take ILT away from the engine's verdict");
        Check(ReferenceEquals(IltDetect.Combine(yes, true, "x"), yes), "the engine's own reason is kept when both agree");
        Check(IltDetect.Combine(no, true, null).Reason.Contains("RynthILT"), "a missing reason still names RynthILT");
    }

    private static void Hosts()
    {
        Case("hosts");
        var d = IltDetect.DefaultHosts;
        Check(IltDetect.HostMatches("game.infiniteleaftide.online", d, out string m) && m == "infiniteleaftide.online", "the server list's host");
        Check(IltDetect.HostMatches("infiniteleaftide.online", d, out _), "the bare domain");
        Check(IltDetect.HostMatches("GAME.InfiniteLeaftide.Online.", d, out _), "case and a trailing dot");
        Check(!IltDetect.HostMatches("notinfiniteleaftide.online", d, out _), "a look-alike domain");
        Check(!IltDetect.HostMatches("infiniteleaftide.online.evil.net", d, out _), "the domain as a prefix");
        Check(!IltDetect.HostMatches("leafdawning.duckdns.org", d, out _), "Leafdawning (another ACE server)");
        Check(!IltDetect.HostMatches("aelrynth.com", d, out _), "Aelrynth");
        Check(!IltDetect.HostMatches("", d, out _) && !IltDetect.HostMatches(null, d, out _), "no host");
        Check(!IltDetect.HostMatches("game.infiniteleaftide.online", null, out _), "no list");
        Check(IltDetect.HostMatches("10.0.0.5", new[] { "10.0.0.5" }, out _), "an IP listed matches itself");
        Check(!IltDetect.HostMatches("1.10.0.0.5", new[] { "10.0.0.5" }, out _), "an IP never matches as a suffix");
    }

    private static void WorldNames()
    {
        Case("world names");
        var d = IltDetect.DefaultServerNames;
        Check(IltDetect.WorldNameMatches("InfiniteLeaftide", d, out string m) && m == "InfiniteLeaftide", "InfiniteLeaftide");
        Check(IltDetect.WorldNameMatches("infiniteleaftide", d, out _), "case-insensitive");
        Check(IltDetect.WorldNameMatches("  InfiniteLeaftide  ", d, out _), "trimmed");
        Check(IltDetect.WorldNameMatches("Leaftide Test", d, out m) && m == "*Leaftide*", "anything with Leaftide in it");
        Check(!IltDetect.WorldNameMatches("Leafdawning", d, out _), "Leafdawning");
        Check(!IltDetect.WorldNameMatches("Infinite Frosthaven", d, out _), "another Infinite server");
        Check(!IltDetect.WorldNameMatches("ACEmulator", d, out _), "ACE's default name");
        Check(!IltDetect.WorldNameMatches("Aeshnidae", d, out _), "Aelrynth's world");
        Check(!IltDetect.WorldNameMatches("", d, out _) && !IltDetect.WorldNameMatches(null, d, out _), "no name");
    }

    private static void Decisions()
    {
        Case("decisions (defaults)");
        IltVerdict v = IltDetect.Decide("game.infiniteleaftide.online", null, null, null);
        Check(v.IsIlt, "by host before the world name arrives: " + v.Reason);
        v = IltDetect.Decide("game.infiniteleaftide.online", "InfiniteLeaftide", null, null);
        Check(v.IsIlt && v.Reason.Contains("and"), "both signals: " + v.Reason);
        v = IltDetect.Decide("", "InfiniteLeaftide", null, null);
        Check(v.IsIlt, "by world name with no host (another launcher): " + v.Reason);
        v = IltDetect.Decide("localhost", "InfiniteLeaftide", null, null);
        Check(v.IsIlt, "a local ACECustom copy that calls itself InfiniteLeaftide");
        v = IltDetect.Decide("play.coldeve.ac", "Coldeve", null, null);
        Check(!v.IsIlt && v.Reason.StartsWith("not"), "someone else's server: " + v.Reason);
        v = IltDetect.Decide("aelrynth.com", "Aeshnidae", null, null);
        Check(!v.IsIlt, "Aelrynth is not ILT");
        v = IltDetect.Decide(null, null, null, null);
        Check(!v.IsIlt && v.Reason.Length > 0, "nothing known: " + v.Reason);
        Check(!IltVerdict.Unknown.IsIlt, "the starting verdict is not ILT");
    }

    private static void ConfiguredLists()
    {
        Case("engine.json lists");
        IltVerdict v = IltDetect.Decide("ilt.example.net", "MyShard", new[] { "MyShard" }, Array.Empty<string>());
        Check(v.IsIlt, "a world name added in engine.json");
        v = IltDetect.Decide("game.infiniteleaftide.online", "InfiniteLeaftide", Array.Empty<string>(), Array.Empty<string>());
        Check(!v.IsIlt, "empty lists turn both signals off");
        v = IltDetect.Decide("ilt.example.net", null, Array.Empty<string>(), new[] { "example.net" });
        Check(v.IsIlt, "a host added in engine.json covers its subdomains");
        v = IltDetect.Decide("game.infiniteleaftide.online", null, null, new[] { "example.net" });
        Check(!v.IsIlt, "an IltHosts list replaces the default host");

        var list = IltDetect.ParseList(new[] { " InfiniteLeaftide ", "infiniteleaftide", "*Leaftide*; Other", "", null });
        Check(list != null && list.Count == 3 && list[0] == "InfiniteLeaftide" && list[1] == "*Leaftide*" && list[2] == "Other",
              "lists are trimmed, split on ; and , and de-duplicated: " + (list == null ? "null" : string.Join("|", list)));
        Check(IltDetect.ParseList(null) == null, "no list stays null (the defaults apply)");
    }

    private static void FromCommandLines()
    {
        Case("launch command lines");
        // The RynthCore launcher's ACE form and the GDLE form, as ServerInfo reads them.
        Check(ServerDetect.TryParseLaunchHost("acclient.exe -h game.infiniteleaftide.online:9000 -a tom -v secret", out string h, out int p)
              && IltDetect.Decide(h, null, null, null).IsIlt && p == 9000, "-h host:port");
        Check(ServerDetect.TryParseLaunchHost("acclient.exe -h Game.InfiniteLeaftide.Online -p 9000", out h, out _)
              && IltDetect.Decide(h, null, null, null).IsIlt, "-h host -p port, mixed case");
        Check(ServerDetect.TryParseLaunchHost("acclient.exe -h aelrynth.com:9020", out h, out _)
              && !IltDetect.Decide(h, null, null, null).IsIlt, "Aelrynth's command line");
    }

    private static void Wildcards()
    {
        Case("wildcards");
        Check(IltDetect.Matches("InfiniteLeaftide", "Infinite*"), "prefix");
        Check(IltDetect.Matches("InfiniteLeaftide", "*Leaftide"), "suffix");
        Check(IltDetect.Matches("InfiniteLeaftide", "I*Leaf*e"), "several stars");
        Check(!IltDetect.Matches("Leaftide", "Infinite*"), "missing prefix");
        Check(!IltDetect.Matches("Leaf", "*Leaftide*"), "too short");
        Check(!IltDetect.Matches("anything", ""), "empty pattern never matches");
        Check(IltDetect.HostMatches("eu.game.infiniteleaftide.online", new[] { "*.infiniteleaftide.online" }, out _), "host wildcard");
    }

    // ── harness ─────────────────────────────────────────────────────────────

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
        Console.WriteLine($"   FAIL [{_case}] {what}");
    }
}
