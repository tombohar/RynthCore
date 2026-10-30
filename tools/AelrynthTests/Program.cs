using System.Text.Json;
using RynthCore.Engine.Compatibility;

namespace AelrynthTests;

/// <summary>
/// Server detection and the /mastery-data parser, offline. Reply lines are built the way
/// the SkillMastery mod builds them (ServerFormat below mirrors DataCommand.Describe and
/// CompanionFeed.Send in Mods/src/Aeshnidae.SkillMastery), then fed through the engine's
/// parser; every value the Skills panel reads is checked.
/// </summary>
internal static class Program
{
    private static int _checks, _failed;
    private static string _case = "";

    private static int Main()
    {
        LaunchCommandLines();
        HostClassification();
        WorldNames();
        Decisions();
        TypicalReply();
        RefusalReply();
        HugeNumbersAndLongLine();
        PrefixPlacement();
        OtherDataLinesAndDamage();
        UnknownCommand();
        RaiseCommands();

        Console.WriteLine();
        Console.WriteLine($"{_checks - _failed}/{_checks} checks passed.");
        Console.WriteLine(_failed == 0 ? "ALL AELRYNTH TESTS PASSED." : $"{_failed} FAILED.");
        return _failed == 0 ? 0 : 1;
    }

    // ── Server detection ────────────────────────────────────────────────────

    private static void LaunchCommandLines()
    {
        Case("launch command lines");
        // The RynthCore launcher's ACE form: -h host:port (AcLaunchArgumentBuilder).
        Check(ServerDetect.TryParseLaunchHost(
                "\"C:\\Turbine\\Asheron's Call\\acclient.exe\" -a tom -v \"pa ss -h x\" -h aelrynth.com:9020 -rodat off",
                out string h, out int p) && h == "aelrynth.com" && p == 9020, $"ACE form: {h}:{p}");
        // GDLE form and other launchers: -h host -p port, account:password in -a.
        Check(ServerDetect.TryParseLaunchHost("acclient.exe -h 144.217.84.148 -p 9030 -a tom:secret -rodat on", out h, out p)
            && h == "144.217.84.148" && p == 9030, $"GDLE form: {h}:{p}");
        Check(ServerDetect.TryParseLaunchHost("acclient.exe -H Play.Aelrynth.COM. -P 9021", out h, out p)
            && h == "play.aelrynth.com" && p == 9021, $"case and trailing dot: {h}:{p}");
        Check(ServerDetect.TryParseLaunchHost("acclient.exe -h \"localhost:9000\"", out h, out p) && h == "localhost" && p == 9000,
            $"quoted host:port: {h}:{p}");
        Check(!ServerDetect.TryParseLaunchHost("acclient.exe -a tom -v pw", out h, out _) && h == "", "no -h");
        Check(!ServerDetect.TryParseLaunchHost("", out _, out _) && !ServerDetect.TryParseLaunchHost(null, out _, out _), "empty / null");
        Check(!ServerDetect.TryParseLaunchHost("acclient.exe -h", out _, out _), "-h with nothing after it");
    }

    private static void HostClassification()
    {
        Case("host classification");
        Check(ServerDetect.ClassifyHost("aelrynth.com") == ServerHostKind.Aelrynth, "aelrynth.com");
        Check(ServerDetect.ClassifyHost("play.aelrynth.com") == ServerHostKind.Aelrynth, "subdomain");
        Check(ServerDetect.ClassifyHost("144.217.84.148") == ServerHostKind.Aelrynth, "the VPS address");
        Check(ServerDetect.ClassifyHost("notaelrynth.com") == ServerHostKind.Other, "a look-alike domain is not Aelrynth");
        Check(ServerDetect.ClassifyHost("aelrynth.com.evil.net") == ServerHostKind.Other, "aelrynth.com as a prefix is not Aelrynth");
        Check(ServerDetect.ClassifyHost("144.217.84.149") == ServerHostKind.Other, "the next address over");
        Check(ServerDetect.ClassifyHost("localhost") == ServerHostKind.Local, "localhost");
        Check(ServerDetect.ClassifyHost("127.0.0.1") == ServerHostKind.Local, "loopback");
        Check(ServerDetect.ClassifyHost("192.168.1.20") == ServerHostKind.Local, "192.168/16");
        Check(ServerDetect.ClassifyHost("10.0.0.5") == ServerHostKind.Local, "10/8");
        Check(ServerDetect.ClassifyHost("172.20.1.1") == ServerHostKind.Local, "172.16/12");
        Check(ServerDetect.ClassifyHost("172.32.1.1") == ServerHostKind.Other, "172.32 is public");
        Check(ServerDetect.ClassifyHost("play.coldeve.ac") == ServerHostKind.Other, "someone else's server");
        Check(ServerDetect.ClassifyHost("") == ServerHostKind.Unknown && ServerDetect.ClassifyHost(null) == ServerHostKind.Unknown, "nothing");
    }

    private static void WorldNames()
    {
        Case("world names");
        Check(ServerDetect.IsAelrynthWorldName("Aelrynth"), "Aelrynth");
        Check(ServerDetect.IsAelrynthWorldName("Aeshnidae"), "Aeshnidae (the live config's name)");
        Check(ServerDetect.IsAelrynthWorldName("Aeshnidae Staging"), "Aeshnidae Staging (ops/staging.sh)");
        Check(!ServerDetect.IsAelrynthWorldName("ACEmulator"), "ACE's default name");
        Check(!ServerDetect.IsAelrynthWorldName("Coldeve") && !ServerDetect.IsAelrynthWorldName("") && !ServerDetect.IsAelrynthWorldName(null), "others");
    }

    private static void Decisions()
    {
        Case("decisions");
        ServerVerdict v = ServerDetect.Decide(ServerHostKind.Aelrynth, "aelrynth.com", 9020, null, false);
        Check(v.IsAelrynth && !v.IsStaging, "live by host alone: " + v.Reason);
        v = ServerDetect.Decide(ServerHostKind.Aelrynth, "144.217.84.148", 9030, "Aeshnidae Staging", true);
        Check(v.IsAelrynth && v.IsStaging, "staging by port: " + v.Reason);
        v = ServerDetect.Decide(ServerHostKind.Aelrynth, "aelrynth.com", 0, "Aeshnidae Staging", false);
        Check(v.IsAelrynth && v.IsStaging, "staging by world name");
        v = ServerDetect.Decide(ServerHostKind.Other, "play.coldeve.ac", 9000, "Coldeve", false);
        Check(!v.IsAelrynth, "someone else's server: " + v.Reason);
        v = ServerDetect.Decide(ServerHostKind.Other, "1.2.3.4", 9000, "Aelrynth", false);
        Check(!v.IsAelrynth, "another host calling itself Aelrynth is not enough");
        v = ServerDetect.Decide(ServerHostKind.Other, "1.2.3.4", 9000, null, true);
        Check(!v.IsAelrynth, "another host with property 9101 alone is not enough");
        v = ServerDetect.Decide(ServerHostKind.Other, "1.2.3.4", 9000, "Aelrynth", true);
        Check(v.IsAelrynth, "a new address with both server-sent signals: " + v.Reason);
        v = ServerDetect.Decide(ServerHostKind.Local, "localhost", 9000, "Aeshnidae", false);
        Check(v.IsAelrynth && !v.IsStaging, "a dev copy on localhost by world name");
        v = ServerDetect.Decide(ServerHostKind.Local, "localhost", 9000, null, true);
        Check(v.IsAelrynth, "a dev copy by the Bank properties");
        v = ServerDetect.Decide(ServerHostKind.Local, "localhost", 9000, "ACEmulator", false);
        Check(!v.IsAelrynth, "a plain local ACE");
        v = ServerDetect.Decide(ServerHostKind.Unknown, "", 0, null, false);
        Check(!v.IsAelrynth && v.Reason.Length > 0, "unknown means not Aelrynth: " + v.Reason);
        Check(!ServerVerdict.Unknown.IsAelrynth, "the starting verdict is not Aelrynth");
    }

    // ── /mastery-data ───────────────────────────────────────────────────────

    private static void TypicalReply()
    {
        Case("typical reply (ten raises per point)");
        string line = ServerFormat.Reply(enabled: true, radiance: 5_600_000, perPoint: 10, ceiling: 0, requireCap: true,
            ServerFormat.Skill(6, "MeleeDefense", "Melee Defense", spec: true, @base: 433, cur: 520, ranks: 37, perPoint: 10,
                next: 1_234_567, afford: 4, atCeiling: false, locked: false),
            ServerFormat.Skill(34, "WarMagic", "War Magic", spec: false, @base: 300, cur: 300, ranks: 0, perPoint: 10,
                next: 900_000, afford: 6, atCeiling: false, locked: true),
            ServerFormat.Skill(15, "MagicDefense", "Magic Defense", spec: false, @base: 410, cur: 410, ranks: 120, perPoint: 10,
                next: 2_000_000, afford: 2, atCeiling: false, locked: false));
        Check(line.StartsWith("~ael1 {\"t\":\"mastery\",\"ok\":true,", StringComparison.Ordinal), "wire shape: " + line[..Math.Min(60, line.Length)]);
        Check(MasteryWire.IsDataLine(line) && MasteryWire.TypeOf(line) == "mastery", "recognised");
        Check(MasteryWire.TryParse(line, out MasteryData? d) && d != null, "parsed");
        if (d == null) return;
        Check(d.Ok && d.Enabled && d.Radiance == 5_600_000 && d.PerPoint == 10 && d.CeilingPoints == 0 && d.RequireCap, "header fields");
        Check(d.Skills.Count == 3, $"three skills ({d.Skills.Count})");
        MasterySkill md = d.Skills[6];
        Check(md.Key == "meleedefense" && md.Name == "Melee Defense" && md.Specialized, "melee defense: key, name, specialized");
        Check(md.Base == 433 && md.Current == 520 && md.Ranks == 37 && md.Points == "3.7" && md.Bonus == 3, "melee defense: numbers");
        Check(md.Next == 1_234_567 && md.Afford == 4 && !md.Ceiling && !md.Locked, "melee defense: price");
        MasterySkill war = d.Skills[34];
        Check(!war.Specialized && war.Locked && war.Ranks == 0 && war.Points == "0.0" && war.Bonus == 0, "war magic: locked, none yet");
        Check(d.Skills[15].Points == "12.0" && d.Skills[15].Bonus == 12, "magic defense: whole points");
    }

    private static void RefusalReply()
    {
        Case("the mod's refusal and a disabled server");
        string refusal = ServerFormat.Send(new { t = "mastery", ok = false, why = "could not read your mastery" });
        Check(MasteryWire.TryParse(refusal, out MasteryData? d) && d != null && !d.Ok && d.Why == "could not read your mastery"
            && d.Skills.Count == 0, "ok=false keeps the reason");
        string off = ServerFormat.Reply(enabled: false, radiance: 0, perPoint: 1, ceiling: 500, requireCap: false,
            ServerFormat.Skill(6, "MeleeDefense", "Melee Defense", spec: false, @base: 200, cur: 200, ranks: 500, perPoint: 1,
                next: 0, afford: 0, atCeiling: true, locked: false));
        Check(MasteryWire.TryParse(off, out d) && d != null && d.Ok && !d.Enabled && d.CeilingPoints == 500 && d.PerPoint == 1,
            "enabled=false, ceiling 500, one raise per point");
        Check(d != null && d.Skills[6].Ceiling && d.Skills[6].Next == 0 && d.Skills[6].Points == "500", "at the ceiling: next 0");
    }

    private static void HugeNumbersAndLongLine()
    {
        Case("huge prices and a long line");
        const long priceCeiling = (long.MaxValue / 4) / 1024;   // Mastery.PriceCeiling
        var skills = new List<object>();
        string[] names =
        {
            "Alchemy", "ArcaneLore", "ArmorTinkering", "AssessCreature", "AssessPerson", "Cooking", "CreatureEnchantment",
            "Deception", "DirtyFighting", "DualWield", "Fletching", "Healing", "HeavyWeapons", "ItemEnchantment",
            "ItemTinkering", "Jump", "Leadership", "LifeMagic", "LightWeapons", "Lockpick", "Loyalty", "MagicDefense",
            "MagicItemTinkering", "ManaConversion", "MeleeDefense", "MissileDefense", "MissileWeapons", "Recklessness",
            "Run", "Salvaging", "Shield", "SneakAttack", "Summoning", "TwoHandedCombat", "VoidMagic", "WarMagic",
            "WeaponTinkering", "FinesseWeapons",
        };
        for (int i = 0; i < names.Length; i++)
            skills.Add(ServerFormat.Skill(i + 1, names[i], names[i], spec: i % 3 == 0, @base: 500 + i, cur: 600 + i,
                ranks: 2400 + i, perPoint: 10, next: i == 0 ? priceCeiling : 1_000_000_000_000L + i, afford: i == 0 ? 0 : 999,
                atCeiling: false, locked: false));
        string line = ServerFormat.Reply(true, 70_000_000_000_000_000L, 10, 0, true, skills.ToArray());
        Check(line.Length > 6000, $"line is {line.Length} chars (the chat hook reads data lines past its 1024 cap)");
        Check(MasteryWire.TryParse(line, out MasteryData? d) && d != null && d.Skills.Count == names.Length, "all skills parsed");
        if (d == null) return;
        Check(d.Radiance == 70_000_000_000_000_000L, "7e16 Radiance (a test account)");
        Check(d.Skills[1].Next == priceCeiling && d.Skills[1].Afford == 0, "price at the ceiling: " + d.Skills[1].Next);
        Check(d.Skills[2].Afford == 999 && d.Skills[2].Points == "240.1", "afford capped at 999, points 240.1");
    }

    private static void PrefixPlacement()
    {
        Case("where the prefix may sit");
        string body = "{\"t\":\"mastery\",\"ok\":true,\"enabled\":true,\"radiance\":5,\"perPoint\":1,\"ceiling\":0,\"requireCap\":false,\"skills\":[]}";
        Check(MasteryWire.TryParse("~ael1 " + body, out _), "at the start");
        Check(MasteryWire.TryParse("~ael1 " + body + "\n", out _), "trailing newline");
        Check(MasteryWire.TryParse("[System] ~ael1 " + body, out _), "after a short tag");
        Check(!MasteryWire.IsDataLine("Bob says, \"~ael1 " + body + "\""), "a player saying it is not a server line");
        Check(!MasteryWire.TryParse("Somebody tells you, \"~ael1 " + body + "\"", out _), "nor a tell");
        Check(!MasteryWire.IsDataLine(new string(' ', 40) + "~ael1 " + body), "too far into the line");
        Check(!MasteryWire.IsDataLine("~ael2 " + body), "another protocol version");
        Check(!MasteryWire.IsDataLine("~ael1") && !MasteryWire.IsDataLine(null) && !MasteryWire.IsDataLine(""), "short / null / empty");
    }

    private static void OtherDataLinesAndDamage()
    {
        Case("other data lines and damage");
        string inspect = ServerFormat.Send(new { t = "inspect", guid = "0x80000201", ok = true });
        Check(MasteryWire.IsDataLine(inspect) && MasteryWire.TypeOf(inspect) == "inspect", "an /inspect-data line is a data line");
        Check(!MasteryWire.TryParse(inspect, out _), "but not a mastery reply");
        Check(!MasteryWire.TryParse("~ael1 {\"t\":\"mastery\",\"ok\":tr", out _), "cut off");
        Check(!MasteryWire.TryParse("~ael1 not json at all", out _), "not JSON");
        Check(!MasteryWire.TryParse("~ael1 [1,2,3]", out _), "an array, not an object");
        // Extra fields and type drift: ignored / tolerated.
        string drift = "~ael1 {\"t\":\"mastery\",\"ok\":true,\"enabled\":true,\"radiance\":\"42\",\"perPoint\":0,\"future\":{\"x\":1},"
            + "\"skills\":[{\"id\":6,\"k\":\"meleedefense\",\"ranks\":3,\"next\":10.0,\"newField\":true},"
            + "{\"id\":7,\"k\":\"Melee Defense\",\"ranks\":1},{\"id\":0,\"k\":\"run\"},\"junk\"]}";
        Check(MasteryWire.TryParse(drift, out MasteryData? d) && d != null, "parsed with extra fields");
        if (d == null) return;
        Check(d.Radiance == 42 && d.PerPoint == 1, "a number sent as a string; perPoint 0 read as 1");
        Check(d.Skills.Count == 1 && d.Skills[6].Ranks == 3 && d.Skills[6].Next == 10 && d.Skills[6].Points == "",
            "a bad key, id 0 and a non-object are skipped; missing points stay empty");
    }

    private static void UnknownCommand()
    {
        Case("ACE's unknown-command refusal");
        Check(MasteryWire.IsUnknownCommand("Unknown command: mastery-data", "mastery-data"), "exact (GameActionTalk's text)");
        Check(MasteryWire.IsUnknownCommand("Unknown command: MASTERY-DATA ", "mastery-data"), "case and trailing space");
        Check(!MasteryWire.IsUnknownCommand("Unknown command: raise", "mastery-data"), "another command");
        Check(!MasteryWire.IsUnknownCommand("Bob says, \"Unknown command: mastery-data\"", "mastery-data"), "a player saying it");
        Check(!MasteryWire.IsUnknownCommand("You have 5 mastery points.", "mastery-data") && !MasteryWire.IsUnknownCommand(null, "mastery-data"),
            "ordinary lines");
        Check(MasteryWire.DataRequest == "@mastery-data", "the request is sent as @mastery-data");
    }

    private static void RaiseCommands()
    {
        Case("/raise command text");
        // The mod's /raise: "/raise <skill> [points]"; the last word is the count when it is a number,
        // the rest is matched against the Skill enum with spaces removed (Commands.TryParseSkill).
        Check(MasteryWire.RaiseCommand("meleedefense", 1) == "@raise meleedefense 1", "one raise");
        Check(MasteryWire.RaiseCommand("warmagic", 25) == "@raise warmagic 25", "several");
        Check(MasteryWire.RaiseCommand("Melee Defense", 1) == null, "a display name is refused (not the enum key)");
        Check(MasteryWire.RaiseCommand("meleedefense", 0) == null && MasteryWire.RaiseCommand("meleedefense", -3) == null, "count below 1");
        Check(MasteryWire.RaiseCommand("meleedefense", 1000) == null, "count above 999");
        Check(MasteryWire.RaiseCommand("", 1) == null && MasteryWire.RaiseCommand(null, 1) == null, "no key");
        Check(MasteryWire.RaiseCommand("melee;defense", 1) == null, "punctuation");
        // Every key the mod sends round-trips: skill.ToString().ToLowerInvariant() is letters only.
        foreach (string k in new[] { "alchemy", "twohandedcombat", "missileweapons", "voidmagic" })
            Check(MasteryWire.IsSkillKey(k), "key " + k);
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

/// <summary>
/// The SkillMastery mod's wire format, mirrored: CompanionFeed.Send serializes an object with
/// UnsafeRelaxedJsonEscaping and WhenWritingNull after the "~ael1 " prefix; DataCommand.Describe
/// builds the object (field names and order as there; Mastery.Points for "points",
/// Mastery.SkillPointsFor for "bonus").
/// </summary>
internal static class ServerFormat
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Send(object data) => "~ael1 " + JsonSerializer.Serialize(data, Json);

    public static string Reply(bool enabled, long radiance, int perPoint, int ceiling, bool requireCap, params object[] skills) =>
        Send(new
        {
            t = "mastery",
            ok = true,
            enabled,
            radiance,
            perPoint,
            ceiling,
            requireCap,
            skills,
        });

    public static object Skill(int id, string enumName, string name, bool spec, long @base, long cur, int ranks, int perPoint,
        long next, int afford, bool atCeiling, bool locked) =>
        new
        {
            id,
            k = enumName.ToLowerInvariant(),
            name,
            adv = spec ? "spec" : "trained",
            @base,
            cur,
            ranks,
            points = Points(ranks, perPoint),
            bonus = perPoint < 1 ? ranks : ranks / perPoint,
            next,
            afford,
            ceiling = atCeiling,
            locked,
        };

    /// <summary>Mastery.Points: "N0" with one rank per point, else "{ranks/per:N0}.{ranks%per}".</summary>
    private static string Points(int ranks, int perPoint)
    {
        int per = Math.Max(1, perPoint);
        return per == 1 ? ranks.ToString("N0") : $"{ranks / per:N0}.{ranks % per}";
    }
}
