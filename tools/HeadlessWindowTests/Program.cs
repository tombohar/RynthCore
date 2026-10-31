using System.Text.Json;
using System.Text.Json.Nodes;
using RynthCore.App;

namespace HeadlessWindowTests;

/// <summary>
/// The launcher's Headless window, its pure part (HeadlessMonitor.cs). Samples\status-outdoor.json and
/// status-indoor.json are live headless status files (rynth-headless 2026.10.31.1) with the names, the
/// profile lists and the chat replaced; every field the window reads keeps its real shape.
/// </summary>
internal static class Program
{
    private static int _checks, _failed;
    private static readonly DateTime SampleTs = new(2026, 10, 31, 22, 0, 0, DateTimeKind.Utc);

    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "rc-headless-window-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Section("parse: outdoor sample", ParseOutdoor);
            Section("parse: indoor sample", ParseIndoor);
            Section("parse: loading, game client, broken files", ParseOthers);
            Section("stale and gone rules, status dot", Rules);
            Section("roster keeps the last good status", Roster);
            Section("chat filter and new lines", Chat);
            Section("command box rules", CommandRules);
            Section("command file: name, JSON, atomic write", () => CommandFile(root));
            Section("opos to map coordinates", Coords);
            Section("formatting", Formatting);
            Section("scan a status folder", () => Scan(root));
            Section("find and tail a log", () => Logs(root));
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"   FAIL: unexpected {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }

        Console.WriteLine(_failed == 0 ? $"PASS ({_checks} checks)" : $"FAIL ({_failed} of {_checks} checks)");
        return _failed == 0 ? 0 : 1;
    }

    // ── samples ─────────────────────────────────────────────────────────────

    private static string SamplePath(string name) => Path.Combine(AppContext.BaseDirectory, "Samples", name);
    private static string Sample(string name) => File.ReadAllText(SamplePath(name));

    private static HeadlessCharacterStatus Parse(string json)
    {
        Check(HeadlessStatusParser.TryParse(json, DateTime.UnixEpoch, out HeadlessCharacterStatus s), "parses");
        return s;
    }

    /// <summary>A sample with some fields changed (bot fields with "bot." in front).</summary>
    private static string Edit(string name, params (string Path, JsonNode? Value)[] edits)
    {
        JsonNode root = JsonNode.Parse(Sample(name))!;
        foreach (var (path, value) in edits)
        {
            if (path.StartsWith("bot.", StringComparison.Ordinal))
                root["bot"]![path[4..]] = value?.DeepClone();
            else
                root[path] = value?.DeepClone();
        }
        return root.ToJsonString();
    }

    private static void ParseOutdoor()
    {
        HeadlessCharacterStatus s = Parse(Sample("status-outdoor.json"));
        Check(s.Id == 1006434801, "id from pid");
        Check(s.Character == "Character One" && s.Server == "ServerOne", "character and server");
        Check(s.Account == "account-one", "account kept (for the log file only)");
        Check(s.TimestampUtc == SampleTs && s.TimestampUtc.Kind == DateTimeKind.Utc, "ts is UTC");
        Check(s.InWorld && s.HostPid == 64348 && s.HostProcess == "rynth-headless", "inWorld, hostPid, hostProcess");
        Check(s.UptimeSec == 221973, "uptime");
        Check(!s.Indoor && s.Landblock == "0000A9B3" && s.Cell == "A9B3000E", "outdoors, landblock, cell");
        Check(Near(s.WorldX, 32486.4) && Near(s.WorldY, 34500.95) && Near(s.Z, 94) && Near(s.Heading, 244.6), "opos position");
        Check(s.HasBot && s.MacroRunning && s.BotAction == "Default", "bot: macro running, action");
        Check(s.Health == 100019 && s.MaxHealth == 100019, "health");
        Check(s.Stamina == 100039 && s.MaxStamina == 100039, "stamina");
        Check(s.Mana == 99888 && s.MaxMana == 100039, "mana");
        Check(!s.HasTarget && s.TargetLabel == "NO TARGET", "no target");
        Check(Math.Abs(s.BurdenPct - 4.1) < 1e-9 && s.VitaePct == 0 && s.DeathsSession == 0, "burden, vitae, deaths");
        Check(s.RecentChat.Count == 57, "all 57 chat lines read");
        Check(s.RecentChat[^1] == new HeadlessChatLine("[RynthAi] Macro started.", 5), "chat line text and type");
        Check(s.MetaName == "Meta One", "meta name");
    }

    private static void ParseIndoor()
    {
        HeadlessCharacterStatus s = Parse(Sample("status-indoor.json"));
        Check(s.Id == 1006063601 && s.Character == "Character Two", "id and character");
        Check(s.Indoor && s.Cell == "01F501BE", "indoors, from opos");
        Check(!s.MacroRunning, "macro off");
        Check(s.XpPerHour == 36815639 && s.XpSession == 2450183040, "XP per hour and XP this session (over 2^31)");
        Check(Math.Abs(s.BurdenPct - 182.2) < 1e-9, "burden over 100%");
        Check(s.Health == 165 && s.MaxStamina == 230 && s.MaxMana == 330, "vitals");

        HeadlessCharacterStatus t = Parse(Edit("status-indoor.json",
            ("bot.currentTargetId", 2147500000L), ("bot.targetLabel", "Monster"), ("bot.targetHealthPercent", 42.5),
            ("bot.sessionKills", 17), ("bot.killsPerHour", 120.4), ("deathsSession", 2), ("vitaePct", 10.0)));
        Check(t.HasTarget && t.TargetId == 2147500000L && t.TargetLabel == "Monster", "target id (above int) and label");
        Check(t.TargetHealthPercent == 42.5, "target health");
        Check(t.SessionKills == 17 && Math.Abs(t.KillsPerHour - 120.4) < 1e-9, "kills");
        Check(t.DeathsSession == 2 && t.VitaePct == 10, "deaths this session, vitae");
    }

    private static void ParseOthers()
    {
        // The exporter's OfflineJson (between sessions): bot null, no opos.
        string loading = """
            {"schema":"rynthcore.client-status/1","ts":"2026-10-31T22:00:00.000Z","host":"TEST-PC","pid":1000000101,
             "account":"account-three","character":"Character Three","server":"ServerOne","uptimeSec":12,"fps":0,
             "pluginTicksPerSec":0,"workingSetMB":40,"inWorld":false,"bot":null,"headless":true,"hostPid":1234,
             "hostProcess":"rynth-headless"}
            """;
        HeadlessCharacterStatus s = Parse(loading);
        Check(!s.InWorld && !s.HasBot && !s.HasVitals, "loading: not in world, no bot, no vitals");
        Check(s.RecentChat.Count == 0 && s.WorldX == null && s.Cell == "", "loading: no chat, no position");
        Check(HeadlessStatusRules.Activity(s, SampleTs) == HeadlessActivity.Loading, "loading: dot");

        string client = Edit("status-outdoor.json", ("headless", null), ("hostPid", null));
        Check(!HeadlessStatusParser.TryParse(client, DateTime.UtcNow, out _), "a game client's status (no headless) is not listed");
        Check(!HeadlessStatusParser.TryParse(Edit("status-outdoor.json", ("headless", false)), DateTime.UtcNow, out _), "headless false is not listed");

        string full = Sample("status-outdoor.json");
        Check(!HeadlessStatusParser.TryParse(full[..(full.Length / 2)], DateTime.UtcNow, out _), "half a file is refused");
        Check(!HeadlessStatusParser.TryParse("", DateTime.UtcNow, out _), "an empty file is refused");
        Check(!HeadlessStatusParser.TryParse("[1,2]", DateTime.UtcNow, out _), "not an object is refused");
        Check(HeadlessStatusParser.Parse(full[..(full.Length / 2)], DateTime.UtcNow, out _) == HeadlessParseResult.Broken, "half a file: broken");
        Check(HeadlessStatusParser.Parse("  ", DateTime.UtcNow, out _) == HeadlessParseResult.Broken, "blank: broken");
        Check(HeadlessStatusParser.Parse(client, DateTime.UtcNow, out _) == HeadlessParseResult.NotHeadless, "game client: not headless");
        Check(!HeadlessStatusParser.TryParse(Edit("status-outdoor.json", ("pid", 0)), DateTime.UtcNow, out _), "no id is refused");

        DateTime written = new(2026, 10, 31, 23, 0, 0, DateTimeKind.Utc);
        Check(HeadlessStatusParser.TryParse(Edit("status-outdoor.json", ("ts", "not a time")), written, out var noTs) &&
              noTs.TimestampUtc == written, "unreadable ts: the file's write time");
        Check(HeadlessStatusParser.TryParse(Edit("status-outdoor.json", ("bot.recentChat", new JsonArray("plain line"))), written, out var plain) &&
              plain.RecentChat.Count == 1 && plain.RecentChat[0].Text == "plain line", "a chat line as a bare string");
        Check(HeadlessStatusParser.TryParse(Edit("status-outdoor.json", ("bot.playerHealth", "oops")), written, out var bad) &&
              bad.Health == 0, "a wrong-typed field reads as 0");
    }

    // ── rules ───────────────────────────────────────────────────────────────

    private static void Rules()
    {
        HeadlessCharacterStatus botting = Parse(Sample("status-outdoor.json"));
        HeadlessCharacterStatus idle = Parse(Sample("status-indoor.json"));
        HeadlessCharacterStatus dead = Parse(Edit("status-outdoor.json", ("bot.playerHealth", 0)));

        Check(HeadlessStatusRules.Freshness(TimeSpan.FromSeconds(0)) == HeadlessFreshness.Fresh, "0 s fresh");
        Check(HeadlessStatusRules.Freshness(TimeSpan.FromSeconds(10)) == HeadlessFreshness.Fresh, "10 s still fresh");
        Check(HeadlessStatusRules.Freshness(TimeSpan.FromSeconds(10.5)) == HeadlessFreshness.Stale, "over 10 s stale");
        Check(HeadlessStatusRules.Freshness(TimeSpan.FromSeconds(30)) == HeadlessFreshness.Stale, "30 s still stale");
        Check(HeadlessStatusRules.Freshness(TimeSpan.FromSeconds(30.5)) == HeadlessFreshness.Gone, "over 30 s gone");

        Check(HeadlessStatusRules.Age(botting, SampleTs.AddSeconds(-5)) == TimeSpan.Zero, "a ts in the future is age 0");
        Check(HeadlessStatusRules.Age(botting, SampleTs.AddSeconds(4)) == TimeSpan.FromSeconds(4), "age from ts");

        DateTime now = SampleTs.AddSeconds(2);
        Check(HeadlessStatusRules.Activity(botting, now) == HeadlessActivity.Botting, "macro on: botting");
        Check(HeadlessStatusRules.Activity(idle, now) == HeadlessActivity.Idle, "macro off: idle");
        Check(HeadlessStatusRules.Activity(dead, now) == HeadlessActivity.Dead, "no health: dead");
        Check(HeadlessStatusRules.Activity(botting, SampleTs.AddSeconds(15)) == HeadlessActivity.Stale, "15 s old: stale, whatever it was doing");
        Check(HeadlessStatusRules.Activity(dead, SampleTs.AddSeconds(15)) == HeadlessActivity.Stale, "stale wins over dead");
        HeadlessCharacterStatus noBot = Parse(Edit("status-outdoor.json", ("bot", null)));
        Check(HeadlessStatusRules.Activity(noBot, now) == HeadlessActivity.Idle, "in world without a bot (no RynthAi): idle, not dead");
        Check(HeadlessStatusRules.ActivityText(HeadlessActivity.Stale) == "not answering", "stale text");
    }

    private static void Roster()
    {
        var roster = new HeadlessRoster();
        HeadlessCharacterStatus a = Parse(Sample("status-outdoor.json"));   // Character One
        HeadlessCharacterStatus b = Parse(Sample("status-indoor.json"));    // Character Two
        DateTime now = SampleTs.AddSeconds(1);

        List<HeadlessCharacterStatus> list = roster.Update(new[] { b, a }, Array.Empty<int>(), now);
        Check(list.Select(s => s.Id).SequenceEqual(new[] { a.Id, b.Id }), "sorted by character name");

        list = roster.Update(new[] { a }, new[] { b.Id }, now);
        Check(list.Count == 2 && roster.TryGet(b.Id, out var keptB) && ReferenceEquals(keptB, b), "a file mid-replace keeps its last status");

        HeadlessCharacterStatus older = Parse(Edit("status-outdoor.json", ("ts", "2026-10-31T21:59:58.000Z"), ("bot.macroRunning", false)));
        roster.Update(new[] { older, b }, Array.Empty<int>(), now);
        Check(roster.TryGet(a.Id, out var keptA) && keptA.MacroRunning, "an older read never replaces a newer status");

        list = roster.Update(new[] { a }, Array.Empty<int>(), now);
        Check(list.Count == 1 && list[0].Id == a.Id, "a removed file leaves the list");

        list = roster.Update(new[] { a }, Array.Empty<int>(), SampleTs.AddSeconds(20));
        Check(list.Count == 1, "stale (20 s) stays listed");
        list = roster.Update(new[] { a }, Array.Empty<int>(), SampleTs.AddSeconds(31));
        Check(list.Count == 0, "gone (31 s) leaves the list");
    }

    // ── chat ────────────────────────────────────────────────────────────────

    private static void Chat()
    {
        HeadlessCharacterStatus s = Parse(Sample("status-outdoor.json"));
        List<HeadlessChatLine> shown = HeadlessChat.Visible(s.RecentChat);
        Check(shown.Count == HeadlessChat.ShownLines, "30 lines shown");
        Check(shown.All(l => l.Type != 21 && l.Type != 22), "combat spam (21, 22) hidden");
        Check(shown[^1].Text == "[RynthAi] Macro started.", "newest last");
        Check(HeadlessChat.Visible(s.RecentChat, 1000).Count == 41, "57 lines, 16 of them combat: 41 left");
        Check(HeadlessChat.IsCombatSpam(21) && HeadlessChat.IsCombatSpam(22) && !HeadlessChat.IsCombatSpam(7) &&
              !HeadlessChat.IsCombatSpam(18) && !HeadlessChat.IsCombatSpam(0), "only 21 and 22 are spam");

        var L = (string t, int c) => new HeadlessChatLine(t, c);
        var prev = new List<HeadlessChatLine> { L("a", 1), L("b", 1), L("c", 1) };
        Check(HeadlessChat.NewLines(prev, prev).Count == 0, "same list: nothing new");
        var shifted = new List<HeadlessChatLine> { L("b", 1), L("c", 1), L("d", 5), L("e", 21) };
        Check(HeadlessChat.NewLines(prev, shifted).Select(l => l.Text).SequenceEqual(new[] { "d", "e" }), "ring shifted: the two new lines");
        Check(HeadlessChat.FirstReply(HeadlessChat.NewLines(prev, shifted)) == L("d", 5), "first reply skips nothing it shouldn't");
        var spamFirst = new List<HeadlessChatLine> { L("c", 1), L("hit", 21), L("ok", 5) };
        Check(HeadlessChat.FirstReply(HeadlessChat.NewLines(prev, spamFirst)) == L("ok", 5), "first reply skips combat spam");
        Check(HeadlessChat.NewLines(Array.Empty<HeadlessChatLine>(), prev).Count == 3, "no snapshot: all lines new");
        Check(HeadlessChat.NewLines(prev, new List<HeadlessChatLine> { L("x", 1) }).Count == 1, "no overlap: all lines new");
        // Same text, another type: a different line.
        Check(HeadlessChat.NewLines(prev, new List<HeadlessChatLine> { L("c", 2) }).Count == 1, "type is part of the line");
        // A repeated line ("Pack full" twice) at the boundary.
        var rep = new List<HeadlessChatLine> { L("p", 2), L("p", 2) };
        var rep2 = new List<HeadlessChatLine> { L("p", 2), L("p", 2), L("p", 2) };
        Check(HeadlessChat.NewLines(rep, rep2).Count == 1, "repeated lines: the longest overlap wins");
        Check(HeadlessChat.FirstReply(new[] { L("", 1) }) == null, "an empty line is no reply");
    }

    // ── commands ────────────────────────────────────────────────────────────

    private static void CommandRules()
    {
        Check(HeadlessCommandFile.ChatCommandProblem("/ra pause") == null, "/ra pause ok");
        Check(HeadlessCommandFile.ChatCommandProblem("  /rnav go home ") == null, "/rnav ok, trimmed");
        Check(HeadlessCommandFile.ChatCommandProblem("/mt usep x") == null, "/mt ok");
        Check(HeadlessCommandFile.ChatCommandProblem("/rn status") == null, "/rn ok");
        Check(HeadlessCommandFile.ChatCommandProblem("/RA start") == null, "case-insensitive");
        Check(HeadlessCommandFile.ChatCommandProblem("/ra") == null, "bare /ra ok");
        Check(HeadlessCommandFile.ChatCommandProblem("/rap") != null, "/rap is not /ra");
        Check(HeadlessCommandFile.ChatCommandProblem("/rnavx") != null, "/rnavx is not /rnav");
        Check(HeadlessCommandFile.ChatCommandProblem("hello there") != null, "plain chat refused");
        Check(HeadlessCommandFile.ChatCommandProblem("@tele x") != null, "server commands refused");
        Check(HeadlessCommandFile.ChatCommandProblem("/tell x, hi") != null, "tells refused");
        Check(HeadlessCommandFile.ChatCommandProblem("") != null && HeadlessCommandFile.ChatCommandProblem(null) != null, "empty refused");
        Check(HeadlessCommandFile.ChatCommandProblem("/ra a\n/ra b") != null, "two lines refused");
        Check(HeadlessCommandFile.ChatCommandProblem("/ra " + new string('x', 500)) != null, "too long refused");
    }

    private static void CommandFile(string root)
    {
        DateTime now = new(2026, 10, 31, 22, 1, 2, 345, DateTimeKind.Utc);
        var guid = Guid.Parse("0123456789abcdef0123456789abcdef");
        string name = HeadlessCommandFile.FileName(1006434801, now, guid);
        Check(name == $"RynthCore.1006434801.{now.Ticks:D19}-0123456789abcdef0123456789abcdef.cmd.json", "name: id, 19-digit ticks, guid");
        Check(string.CompareOrdinal(HeadlessCommandFile.FileName(5, now, guid), HeadlessCommandFile.FileName(5, now.AddTicks(1), Guid.Empty)) < 0,
            "names sort in submit order");

        using (JsonDocument doc = JsonDocument.Parse(HeadlessCommandFile.Json(1006434801, "sendChat", "/ra pause \"x\"", now)))
        {
            JsonElement r = doc.RootElement;
            Check(r.GetProperty("schema").GetString() == "rynthcore.command/1", "schema");
            Check(r.GetProperty("pid").GetInt32() == 1006434801, "pid = the character id");
            Check(r.GetProperty("action").GetString() == "sendChat", "action");
            Check(r.GetProperty("value").GetString() == "/ra pause \"x\"", "value, quotes escaped");
            Check(r.GetProperty("ts").GetDateTimeOffset() == new DateTimeOffset(now), "ts");
        }

        string dir = Path.Combine(root, "status", HeadlessCommandFile.CommandsFolderName);
        string path = HeadlessCommandFile.Write(dir, 1006434801, HeadlessCommandFile.MacroAction, "off", now);
        Check(File.Exists(path) && Path.GetDirectoryName(path) == dir, "written into the commands folder (created)");
        Check(Directory.GetFiles(dir).Length == 1, "no temp file left behind");
        // What the headless drain matches (StatusExporter.DrainCommands).
        Check(Directory.GetFiles(dir, "RynthCore.1006434801.*.cmd.json").Length == 1, "the character's drain pattern finds it");
        Check(Directory.GetFiles(dir, "RynthCore.1006063601.*.cmd.json").Length == 0, "another character's pattern doesn't");
        using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path)))
            Check(doc.RootElement.GetProperty("action").GetString() == "macro" && doc.RootElement.GetProperty("value").GetString() == "off",
                "file content");
        Check(!File.ReadAllBytes(path).AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }), "UTF-8 without BOM");

        // A temp file of a write in progress is never matched by the drain.
        File.WriteAllText(Path.Combine(dir, HeadlessCommandFile.FileName(1006434801, now, Guid.NewGuid()) + ".tmp"), "{");
        Check(Directory.GetFiles(dir, "RynthCore.1006434801.*.cmd.json").Length == 1, "a .tmp is not drained");

        string second = HeadlessCommandFile.Write(dir, 1006434801, "sendChat", "/ra start", now);
        Check(second != path && File.Exists(second), "same instant: a second file (guid)");
        Check(Throws(() => HeadlessCommandFile.Write(dir, 0, "x", "", now)), "id 0 refused");
        Check(Throws(() => HeadlessCommandFile.Write(dir, 5, " ", "", now)), "no action refused");
    }

    // ── coordinates, formatting ─────────────────────────────────────────────

    private static void Coords()
    {
        var (ns, ew) = HeadlessCoords.FromWorld((33.6 * 10 + 1019.5) * 24, (42.1 * 10 + 1019.5) * 24);
        Check(Math.Abs(ns - 42.1) < 1e-9 && Math.Abs(ew - 33.6) < 1e-9, "round trip 42.1N 33.6E");
        Check(HeadlessCoords.Text(42.1, 33.6) == "42.1N 33.6E", "text");
        Check(HeadlessCoords.Text(-12.34, -5.06) == "12.3S 5.1W", "south and west");
        var (zns, zew) = HeadlessCoords.FromWorld(1019.5 * 24, 1019.5 * 24);
        Check(Math.Abs(zns) < 1e-12 && Math.Abs(zew) < 1e-12, "the map's centre is 0, 0");
        Check(HeadlessCoords.Text(-0.04, 0.04) == "0.0S 0.0E", "near zero keeps its sign letter");
        // Landblock 0x0000: the far south-west corner.
        var (cns, cew) = HeadlessCoords.FromWorld(0, 0);
        Check(Math.Abs(cns + 101.95) < 1e-9 && Math.Abs(cew + 101.95) < 1e-9, "world 0,0 is 101.95S 101.95W");

        HeadlessCharacterStatus outdoor = Parse(Sample("status-outdoor.json"));
        Check(HeadlessCoords.Where(outdoor) == "Outdoors 41.8N 33.4E (landblock A9B3)", "outdoor sample: " + HeadlessCoords.Where(outdoor));
        HeadlessCharacterStatus indoor = Parse(Sample("status-indoor.json"));
        Check(HeadlessCoords.Where(indoor) == "Indoors (landblock 01F5, cell 01F501BE)", "indoor sample: no map coordinates");
        HeadlessCharacterStatus none = Parse(Edit("status-outdoor.json", ("opos", null), ("landblock", "")));
        Check(HeadlessCoords.Where(none) == "unknown", "no position: unknown");
    }

    private static void Formatting()
    {
        Check(HeadlessFormat.Number(950) == "950" && HeadlessFormat.Number(12_345) == "12.3k", "small numbers");
        Check(HeadlessFormat.Number(36_815_639) == "36.8M" && HeadlessFormat.Number(2_450_183_040) == "2.5B", "millions, billions");
        Check(HeadlessFormat.Duration(45) == "45s" && HeadlessFormat.Duration(725) == "12m 05s", "seconds, minutes");
        Check(HeadlessFormat.Duration(3 * 3600 + 12 * 60) == "3h 12m" && HeadlessFormat.Duration(221973) == "2d 13h", "hours, days");
        Check(HeadlessFormat.Duration(-3) == "0s", "negative = 0");
        Check(HeadlessFormat.Vital(165, 230) == "165 / 230" && HeadlessFormat.Vital(0, 0) == "-", "vital text");
        Check(HeadlessFormat.Fraction(5, 10) == 0.5 && HeadlessFormat.Fraction(20, 10) == 1 && HeadlessFormat.Fraction(1, 0) == 0, "bar fraction");
    }

    // ── files ───────────────────────────────────────────────────────────────

    private static void Scan(string root)
    {
        string dir = Path.Combine(root, "scan");
        Directory.CreateDirectory(dir);
        DateTime now = DateTime.UtcNow;
        string fresh = Edit("status-outdoor.json", ("ts", now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")));
        File.WriteAllText(Path.Combine(dir, "RynthCore.1006434801.status.json"), fresh);
        File.WriteAllText(Path.Combine(dir, "RynthCore.1006063601.status.json"), Sample("status-indoor.json")[..200]);   // mid-write
        File.WriteAllText(Path.Combine(dir, "RynthCore.17132.status.json"), Edit("status-outdoor.json", ("headless", null)));   // a game client
        string old = Path.Combine(dir, "RynthCore.10684.status.json");
        File.WriteAllText(old, fresh);
        File.SetLastWriteTimeUtc(old, now.AddHours(-3));   // left over from an old session: not even opened
        File.WriteAllText(Path.Combine(dir, "RynthCore.1006434801.inventory.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "RynthCore.x.status.json"), fresh);

        List<HeadlessCharacterStatus> found = HeadlessStatusScanner.Scan(dir, now, out List<int> unreadable);
        Check(found.Count == 1 && found[0].Id == 1006434801, "one headless character read");
        Check(unreadable.SequenceEqual(new[] { 1006063601 }), "the half-written headless file is reported unreadable");
        Check(HeadlessStatusScanner.Scan(Path.Combine(root, "missing"), now, out var none).Count == 0 && none.Count == 0, "missing folder: nothing");

        // Read while another handle holds it open for writing (as the exporter's replace may).
        string held = Path.Combine(dir, "RynthCore.1006434801.status.json");
        using (new FileStream(held, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            Check(HeadlessStatusScanner.ReadShared(held) != null, "read while held open for writing");
        Check(HeadlessStatusScanner.ReadShared(Path.Combine(dir, "nope.json")) == null, "missing file: null");

        Check(HeadlessStatusScanner.TryIdOf(@"C:\x\RynthCore.1006434801.status.json", out int id) && id == 1006434801, "id from the name");
        Check(!HeadlessStatusScanner.TryIdOf("RynthCore.-5.status.json", out _) && !HeadlessStatusScanner.TryIdOf("RynthCore.5.inventory.json", out _),
            "bad names refused");
    }

    private static void Logs(string root)
    {
        Check(HeadlessLogs.LogFileName("account-one") == "headless-account_one.log", "letters and digits kept, the rest _");
        Check(HeadlessLogs.LogFileName("Account2") == "headless-Account2.log", "case kept");

        string cfgDir = Path.Combine(root, "appdata", "headless");
        string suiteA = Path.Combine(root, "suiteA"), suiteDefault = Path.Combine(root, "default"), configured = Path.Combine(root, "configured");
        Directory.CreateDirectory(cfgDir);
        File.WriteAllText(Path.Combine(cfgDir, "launch_a.json"),
            $$"""{"host":"h","port":1,"suiteDir":{{JsonSerializer.Serialize(suiteA)}},"sessions":[{"launcherProfileId":"p1","account":"account-one"}]}""");
        File.WriteAllText(Path.Combine(cfgDir, "launch_b.json"),
            """{"host":"h","port":1,"sessions":[{"launcherProfileId":"p2","account":"account-two"}]}""");
        File.WriteAllText(Path.Combine(cfgDir, "launch_c.json"), "{ broken");

        List<string> dirsA = HeadlessLogs.CandidateSuiteDirs("ACCOUNT-ONE", cfgDir, configured, suiteDefault, legacySuiteDir: null);
        Check(dirsA.SequenceEqual(new[] { suiteA, configured, suiteDefault }), "config's suiteDir first, then configured, default");
        List<string> dirsB = HeadlessLogs.CandidateSuiteDirs("account-two", cfgDir, "", suiteDefault, legacySuiteDir: null);
        Check(dirsB.SequenceEqual(new[] { suiteDefault }), "config without suiteDir = the default (once)");
        List<string> dirsC = HeadlessLogs.CandidateSuiteDirs("account-three", Path.Combine(root, "nothing"), null, suiteDefault);
        Check(dirsC.SequenceEqual(new[] { suiteDefault, HeadlessLogs.LegacySuiteDir }), "no config: default, legacy");

        Directory.CreateDirectory(suiteDefault);
        Directory.CreateDirectory(suiteA);
        string logA = Path.Combine(suiteDefault, "headless-account_one.log");
        Check(HeadlessLogs.Find("account-one", dirsA) == null, "no log yet: null");
        File.WriteAllText(logA, "x\n");
        Check(HeadlessLogs.Find("account-one", dirsA) == logA, "found in a later folder");

        // Tail: last 50 lines, only the end read, the partial first line dropped.
        var sb = new System.Text.StringBuilder();
        for (int i = 1; i <= 5000; i++) sb.Append($"22:00:00.000 line {i}\r\n");
        sb.Append("22:00:01.000 unfinished");
        File.WriteAllText(logA, sb.ToString());
        List<string>? tail = HeadlessLogs.Tail(logA, 50, 4096);
        Check(tail != null && tail.Count == 50, "50 lines");
        Check(tail != null && tail[^1] == "22:00:01.000 unfinished" && tail[^2] == "22:00:00.000 line 5000", "newest last, no \\r, unfinished line kept");
        Check(tail != null && tail.All(l => l.StartsWith("22:00:0", StringComparison.Ordinal)), "no partial first line");
        using (var writer = new FileStream(logA, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            // The writer (rynth-headless's StreamWriter) shares read only; the tail must still open it.
            writer.Write("\nmore\n"u8);
            writer.Flush();
            List<string>? live = HeadlessLogs.Tail(logA, 2);
            Check(live != null && live.SequenceEqual(new[] { "22:00:01.000 unfinished", "more" }), "tailed while the writer has it open");
        }
        Check(HeadlessLogs.Tail(Path.Combine(root, "missing.log")) == null, "missing log: null");
        Check(HeadlessLogs.LastLines("a\nb\n", 10, startsMidFile: false).SequenceEqual(new[] { "a", "b" }), "short file whole");
        Check(HeadlessLogs.LastLines("", 10, false).Count == 0, "empty file: no lines");
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static void Section(string name, Action body)
    {
        int before = _failed;
        try { body(); }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"   FAIL: {name}: {ex.GetType().Name}: {ex.Message}");
        }
        Console.WriteLine($"{(_failed == before ? "ok  " : "FAIL")} {name}");
    }

    private static bool Near(double? a, double b) => a is double v && Math.Abs(v - b) < 1e-9;

    private static bool Throws(Action a)
    {
        try { a(); return false; }
        catch { return true; }
    }

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (ok) return;
        _failed++;
        Console.WriteLine($"   FAIL: {what}");
    }
}
