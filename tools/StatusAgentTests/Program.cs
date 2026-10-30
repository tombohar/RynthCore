using System.Text;
using System.Text.Json;
using RynthCore.StatusAgent;

namespace StatusAgentTests;

/// <summary>
/// The StatusAgent's DrakRemote parity pieces, offline. Args (optional, read-only):
///   --portal PATH   also read the real spell table from this portal.dat
///   --cell PATH     also render the real world map from this cell.dat (prints size and timing)
///   --out FILE      with --cell: write the rendered JPEG there to look at
/// </summary>
internal static class Program
{
    private static int _checks, _failed;

    private static int Main(string[] args)
    {
        string? portal = Arg(args, "--portal"), cell = Arg(args, "--cell"), outFile = Arg(args, "--out");

        Landblocks();
        AppMatchesWithTheInstalledPhone();
        ClickMapping();
        Capabilities();
        SpellTable();
        Enchantments();
        PluginStatusAdditions();
        PayloadJsonNames();
        WorldMap();
        NavSearch();
        TravelStatus();
        RaiseCommand();
        StatusFileReads();
        MoveProtocol();
        LogRollover();
        ServeGuards();
        if (Arg(args, "--atlas") is { } atlas) RealAtlas(atlas);
        if (portal != null) RealSpellTable(portal);
        if (cell != null) RealWorldMap(cell, outFile);

        Console.WriteLine();
        Console.WriteLine(_failed == 0 ? $"PASS ({_checks} checks)" : $"FAIL ({_failed} of {_checks} checks failed)");
        return _failed == 0 ? 0 : 1;
    }

    // ── serve guards (remote access) ────────────────────────────────────────

    private static void ServeGuards()
    {
        Section("status server: loopback, token length, lockout, bounded bodies, log-safe text");
        Check(ServeGuard.IsLoopbackPrefix("http://127.0.0.1:8740/"), "127.0.0.1 is loopback");
        Check(ServeGuard.IsLoopbackPrefix("http://localhost:8740/"), "localhost is loopback");
        Check(ServeGuard.IsLoopbackPrefix("http://[::1]:8740/"), "[::1] is loopback");
        Check(!ServeGuard.IsLoopbackPrefix("http://+:8740/"), "+ is every address");
        Check(!ServeGuard.IsLoopbackPrefix("http://*:8740/"), "* is every address");
        Check(!ServeGuard.IsLoopbackPrefix("http://100.64.1.2:8740/"), "a Tailscale address is not");
        Check(!ServeGuard.IsLoopbackPrefix("http://127.0.0.1.evil.example:8740/"), "a name starting 127.0.0.1 is not");

        Check(ServeGuard.TokenProblem("http://127.0.0.1:8740/", "") != null, "no token: never serves");
        Check(ServeGuard.TokenProblem("http://127.0.0.1:8740/", "short") == null, "loopback: any token");
        Check(ServeGuard.TokenProblem("http://+:8740/", "short") != null, "remote bind: a short token is refused");
        Check(ServeGuard.TokenProblem("http://+:8740/", new string('k', 16)) == null, "remote bind: 16 characters is fine");

        var t = new AuthThrottle();
        var lan = System.Net.IPAddress.Parse("192.168.1.50");
        var t0 = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        bool started = false;
        for (int i = 0; i < AuthThrottle.MaxFailures - 1; i++) started |= t.Fail(lan, t0.AddSeconds(i));
        Check(!started && !t.IsBlocked(lan, t0.AddSeconds(10)), "under the limit: not blocked");
        Check(t.Fail(lan, t0.AddSeconds(10)), "the limit starts a lockout (reported once)");
        Check(t.IsBlocked(lan, t0.AddMinutes(4)), "still blocked inside the lockout");
        Check(!t.IsBlocked(lan, t0.AddMinutes(6)), "free again after it");
        var slow = System.Net.IPAddress.Parse("192.168.1.51");
        for (int i = 0; i < AuthThrottle.MaxFailures * 2; i++) t.Fail(slow, t0.AddSeconds(i * 61));
        Check(!t.IsBlocked(slow, t0.AddSeconds(AuthThrottle.MaxFailures * 2 * 61)), "failures spread past the window don't add up");
        var other = System.Net.IPAddress.Parse("192.168.1.52");
        for (int i = 0; i < AuthThrottle.MaxFailures - 1; i++) t.Fail(other, t0);
        t.Succeed(other);
        Check(!t.Fail(other, t0), "a good token clears the count");
        for (int i = 0; i < AuthThrottle.MaxFailures * 2; i++) t.Fail(System.Net.IPAddress.Loopback, t0);
        Check(!t.IsBlocked(System.Net.IPAddress.Loopback, t0), "loopback is never locked out");

        Check(ServeGuard.TryReadBounded(new MemoryStream(Encoding.UTF8.GetBytes("{\"pid\":1}")), 9, 64, null, out string small) && small == "{\"pid\":1}", "a small body reads");
        Check(!ServeGuard.TryReadBounded(new MemoryStream(new byte[100]), 100, 64, null, out _), "a declared length over the limit is refused");
        Check(!ServeGuard.TryReadBounded(new MemoryStream(new byte[100]), -1, 64, null, out _), "chunked (no length) over the limit is refused");
        Check(ServeGuard.TryReadBounded(new MemoryStream(new byte[64]), -1, 64, null, out _), "exactly the limit reads");

        Check(ServeGuard.LogSafe("a\r\nFAKE LOG LINE") == "a  FAKE LOG LINE", "CR/LF can't start a log line");
        Check(ServeGuard.LogSafe(new string('x', 500), 10) == "xxxxxxxxxx...", "capped");
        Check(ServeGuard.LogSafe(null) == "", "null is empty");
    }

    // ── log rollover ────────────────────────────────────────────────────────

    private static void LogRollover()
    {
        Section("StatusAgent.log rolls over past its cap");
        string dir = Path.Combine(Path.GetTempPath(), "sa-logroll-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        try
        {
            string f = Path.Combine(dir, "StatusAgent.log");
            File.WriteAllText(f, new string('x', 2000));
            File.WriteAllText(f + ".old", "older");
            AgentLog.RollIfBig(f, maxBytes: 1000);
            Check(!File.Exists(f), "the first write checks at once (a big log from before starts over)");
            Check(File.Exists(f + ".old") && new FileInfo(f + ".old").Length == 2000, "to .old, replacing the older copy");
            File.WriteAllText(f, new string('y', 3000));
            for (int i = 0; i < 199; i++) AgentLog.RollIfBig(f, maxBytes: 1000);
            Check(File.Exists(f), "then only every 200th write checks the size");
            AgentLog.RollIfBig(f, maxBytes: 1000);
            Check(!File.Exists(f) && new FileInfo(f + ".old").Length == 3000, "the 200th rolls it over");
            File.WriteAllText(f, "small");
            for (int i = 0; i < 400; i++) AgentLog.RollIfBig(f, maxBytes: 1000);
            Check(File.Exists(f), "under the cap it stays");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ── landblocks ──────────────────────────────────────────────────────────

    private static void Landblocks()
    {
        Section("landblock ids");
        Check(RemoteParity.LandblockFull("00005A48") == "5A480000", "engine short form \"00005A48\" -> \"5A480000\"");
        Check(RemoteParity.LandblockFull("5A48") == "5A480000", "four digits -> full");
        Check(RemoteParity.LandblockFull("5A480000") == "5A480000", "full stays full");
        Check(RemoteParity.LandblockFull("5A48013F") == "5A480000", "a cell id drops its cell part");
        Check(RemoteParity.LandblockFull("") == "", "empty stays empty (outdoors at login)");
        Check(RemoteParity.LandblockFull("00000000") == "", "zero is no landblock");
        Check(RemoteParity.LandblockFull("zz") == "zz", "unparseable passes through");
        Check(RemoteParity.TryLandblockShort("5A480000", out uint a) && a == 0x5A48, "/map?lb=5A480000 -> 0x5A48");
        Check(RemoteParity.TryLandblockShort("00005A48", out uint b) && b == 0x5A48, "/map?lb=00005A48 -> 0x5A48");
        Check(RemoteParity.TryLandblockShort("5A48", out uint c) && c == 0x5A48, "/map?lb=5A48 -> 0x5A48");
        Check(!RemoteParity.TryLandblockShort("", out _) && !RemoteParity.TryLandblockShort("0", out _), "empty / zero refused");
    }

    /// <summary>DrakRemote v1.0.13 (cf0e206, on the phone now): AcMapEntry.LandblockId shifts a short id up,
    /// AcClientStatus.LandblockId takes the id as written. The agent's output must match under those rules.</summary>
    private static void AppMatchesWithTheInstalledPhone()
    {
        Section("matching under the installed app's rules");
        static uint MapRule(string s) => uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v) ? (v > 0xFFFF ? v : v << 16) : 0;
        static uint ClientRule(string s) => uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : 0;

        // Before: what the agent wrote (engine form for both) never matched.
        Check(MapRule("00005A48") != ClientRule("00005A48"), "negative control: the old output does not match (the bug)");
        string map = RemoteParity.LandblockFull("00005A48"), client = RemoteParity.LandblockFull("00005A48");
        Check(MapRule(map) == ClientRule(client) && ClientRule(client) == 0x5A480000, "new output: map and client both 0x5A480000");
        // The image URL the app builds from the matched map: lb.ToString("X") of the parsed id.
        string urlLb = MapRule(map).ToString("X");
        Check(RemoteParity.TryLandblockShort(urlLb, out uint file) && $"{file:X8}_1.bin" == "00005A48_1.bin", "the /map request finds the baked file 00005A48_1.bin");
    }

    // ── click mapping ───────────────────────────────────────────────────────

    private static void ClickMapping()
    {
        Section("tap-to-click mapping");
        Check(RemoteParity.ClickPoint(0, 0, 1280, 720) == (0, 0), "top-left -> 0,0");
        Check(RemoteParity.ClickPoint(1, 1, 1280, 720) == (1279, 719), "bottom-right -> last pixel");
        Check(RemoteParity.ClickPoint(0.5, 0.5, 1280, 720) == (640, 360), "centre (rounded half away)");
        Check(RemoteParity.ClickPoint(0.25, 0.75, 1920, 1080) == (480, 809), "a quarter across, three quarters down");
        Check(RemoteParity.ClickPoint(-0.2, 1.4, 800, 600) == (0, 599), "out of range clamps to the edge");
        Check(RemoteParity.ClickPoint(double.NaN, double.PositiveInfinity, 800, 600) == (0, 0), "NaN / infinity -> origin, never an exception");
        // The same tap on a stream scaled down to 720 wide lands on the same game pixel (same aspect).
        var native = RemoteParity.ClickPoint(812.0 / 1919, 300.0 / 1079, 1920, 1080);
        Check(native == (812, 300), "a pixel picked on the native frame maps back to itself");
        long lp = (long)RemoteParity.MouseLParam(640, 360);
        Check((lp & 0xFFFF) == 640 && ((lp >> 16) & 0xFFFF) == 360, "lParam: x low word, y high word");
        long big = (long)RemoteParity.MouseLParam(3839, 2159);
        Check((big & 0xFFFF) == 3839 && ((big >> 16) & 0xFFFF) == 2159, "lParam holds a 4K client area");
    }

    // ── capabilities ────────────────────────────────────────────────────────

    private static void Capabilities()
    {
        Section("capabilities");
        var all = new AgentConfig { EnableRemoteControl = true, EnableScreenStream = true, EnableVideoStream = true };
        var c = RemoteParity.Capabilities(all, iconDat: true, dungeonMaps: true, worldMap: true, characterSheets: true);
        Check(c.Movement && c.CloseClient && c.Click && c.Video && c.VideoHd && c.Icons && c.Maps && c.WorldMap && c.Character,
            "everything on when configured and present");
        Check(!c.Act && !c.Nearby && !c.Pick && !c.Dungeon && !c.VideoMinimized, "DrakBot-only routes stay off on RynthCore");

        var readOnly = new AgentConfig();
        var r = RemoteParity.Capabilities(readOnly, iconDat: false, dungeonMaps: false, worldMap: false, characterSheets: false);
        Check(!r.Movement && !r.CloseClient && !r.Click && !r.Video && !r.VideoHd, "a read-only agent offers no control or video");
        Check(!r.Icons && !r.Maps && !r.WorldMap && !r.Character, "nothing on disk -> no icons, maps or Skills tab");
        Check(r.Inventory && r.Settings && r.Runs, "the file-backed routes are always there");

        var streamOnly = new AgentConfig { EnableScreenStream = true };
        Check(!RemoteParity.Capabilities(streamOnly, false, false, false, false).Click, "click needs remote control as well as the stream");
        Check(RemoteParity.Capabilities(readOnly, false, false, worldMap: true, false).Maps, "a world map alone lights the Map tab");
    }

    // ── spell table ─────────────────────────────────────────────────────────

    private static byte[] SpellTableFile(params (uint Id, string Name, uint School, uint Icon, uint Category, uint Flags, uint Meta)[] spells)
    {
        var w = new Writer();
        w.U32(0x0E00000E);
        w.U16((ushort)spells.Length);
        w.U16(64);
        foreach (var s in spells)
        {
            w.U32(s.Id);
            w.Obfuscated(s.Name);
            w.Obfuscated("A description that is not read.");
            w.U32(s.School); w.U32(s.Icon); w.U32(s.Category); w.U32(s.Flags);
            w.U32(10);                 // base mana
            w.Zero(8);                 // range constant, range mod
            w.U32(150);                // power
            w.Zero(12);                // economy mod, formula version, component loss
            w.U32(s.Meta); w.U32(s.Id);
            if (s.Meta is 1 or 12) w.Zero(16);
            else if (s.Meta == 7) w.Zero(8);
            w.Zero(8 * 4 + 12 + 8 + 4 + 12);
        }
        return w.ToArray();
    }

    private static void SpellTable()
    {
        Section("spell table parse");
        byte[] raw = SpellTableFile(
            (2, "Strength Self I", 4, 0x06001234, 1, 0x4 | 0x8, 1),          // creature, beneficial, enchantment
            (1237, "Drain Health Other I", 2, 0x06002222, 83, 0x0, 1),       // life, harmful
            (157, "Summon Primary Portal I", 3, 0x06003333, 200, 0x4, 7),    // a portal summon (different tail)
            (4305, "Incantation of Strength Self", 4, 0x06004444, 1, 0x4 | 0x8, 1));
        var t = SpellTableService.Parse(raw, out int total);
        Check(total == 4 && t.Count == 4, "all four spells read, including after a portal summon's shorter tail");
        Check(t.TryGetValue(2, out var s) && s.Name == "Strength Self I" && s.Icon == 0x06001234 && s.Category == 1 && s.Beneficial, "name, icon, family, beneficial");
        Check(t.TryGetValue(1237, out var d) && !d.Beneficial && d.School == 2, "a harmful spell is not beneficial");
        Check(t.TryGetValue(4305, out var inc) && inc.Name == "Incantation of Strength Self", "the spell after the portal summon is still aligned");
        Check(SpellTableService.TierFromName("Strength Self VII") == 7 && SpellTableService.TierFromName("Incantation of Strength Self") == 8
              && SpellTableService.TierFromName("Strength Self VIII") == 8 && SpellTableService.TierFromName("Aura of Defender") == 0, "tier from the name");
        var cut = SpellTableService.Parse(raw[..(raw.Length - 40)], out _);
        Check(cut.Count == 3, "a truncated file keeps the spells before the cut and never throws");
    }

    private static void Enchantments()
    {
        Section("enchantments for the Buffs tab");
        var table = SpellTableService.Parse(SpellTableFile(
            (2, "Strength Self I", 4, 0x06001234, 1, 0x4, 1),
            (1237, "Drain Health Other I", 2, 0x06002222, 83, 0x0, 1)), out _);
        SpellTableService.SpellInfo? Lookup(uint id) => table.TryGetValue(id, out var i) ? i : null;

        var input = new List<EnchantmentIn>
        {
            new() { SpellId = 2, SecondsRemaining = 1800 },
            new() { SpellId = 1237, SecondsRemaining = 42 },
            new() { SpellId = 99999, SecondsRemaining = -1 },      // permanent, unknown to the table
            new() { SpellId = 0, SecondsRemaining = 10 },          // junk
        };
        var outList = RemoteParity.Enchantments(input, Lookup)!;
        Check(outList.Count == 3, "id 0 dropped");
        Check(outList[0].SpellId == 1237 && outList[1].SpellId == 2 && outList[2].SpellId == 99999, "soonest first, permanent last");
        Check(outList[1].Name == "Strength Self I" && outList[1].Family == 1 && outList[1].Tier == 1 && outList[1].IconId == 0x06001234 && outList[1].Beneficial, "named, family, tier, icon");
        Check(!outList[0].Beneficial, "a debuff reads as not beneficial (the app lists it under debuffs)");
        Check(outList[2].Name == "Spell 99999" && outList[2].IconId == 0 && outList[2].Beneficial && outList[2].SecondsRemaining == -1, "unknown spell: id as name, no icon, kept as a buff");
        Check(RemoteParity.Enchantments(null, Lookup) == null, "no enchantments from an older plugin -> null (the app shows nothing in force)");
        var before = RemoteParity.Enchantments(new() { new() { SpellId = 2, SecondsRemaining = 5 } }, _ => null)!;
        Check(before[0].Name == "Spell 2", "before the table loads the countdown still works");
    }

    // ── what the plugin writes, read back by the agent ──────────────────────

    private static void PluginStatusAdditions()
    {
        Section("plugin status additions read by the agent");
        // A status file as the RynthRemote plugin 0.3 writes it: the engine object + bot + the two additions.
        const string file = """
            {"schema":"rynthcore.client-status/1","pid":8808,"character":"Drakkon","inWorld":true,
             "landblock":"0000A9B4","indoor":false,"area":"A9B4","bot":null,
             "enchantments":[{"spellId":1237,"secondsRemaining":42},{"spellId":2,"secondsRemaining":1800},{"spellId":4305,"secondsRemaining":-1}],
             "opos":{"cell":"A9B40031","indoor":false,"wx":32549.12,"wy":34620.5,"z":42.03,"heading":271.5}}
            """;
        var m = JsonSerializer.Deserialize(file, AgentJsonContext.Default.StatusFileModel)!;
        Check(m.Enchantments is { Count: 3 } && m.Enchantments[0].SpellId == 1237 && m.Enchantments[2].SecondsRemaining == -1, "enchantments parsed");
        Check(m.Opos is { Wx: 32549.12, Wy: 34620.5, Heading: 271.5 } && m.Opos.Cell == "A9B40031", "outdoor position parsed");
        var old = JsonSerializer.Deserialize("""{"schema":"rynthcore.client-status/1","pid":1,"landblock":"","indoor":false}""", AgentJsonContext.Default.StatusFileModel)!;
        Check(old.Enchantments == null && old.Opos == null, "an older plugin's file still reads (fields absent)");
        // The world map pixel for that position: 32549.12/24 = 1356.2, 2040 - 34620.5/24 = 597.5.
        var (px, py) = WorldMapService.ToPixel(m.Opos!.Wx, m.Opos.Wy);
        Check(Math.Abs(px - 1356.21) < 0.01 && Math.Abs(py - 597.48) < 0.01, "world map pixel for the outdoor position");
    }

    private static void PayloadJsonNames()
    {
        Section("JSON names the phone reads");
        var payload = new AggregatePayload
        {
            Host = "PC", ClientCount = 1,
            Capabilities = RemoteParity.Capabilities(new AgentConfig { EnableRemoteControl = true, EnableScreenStream = true }, true, true, true, true),
            Clients =
            {
                new ClientStatus
                {
                    Pid = 8808, Landblock = RemoteParity.LandblockFull("00005A48"), Indoor = true, Heading = 90,
                    Enchantments = new() { new EnchantmentOut { SpellId = 2, Name = "Strength Self I", SecondsRemaining = 1800, IconId = 0x06001234 } },
                },
            },
        };
        string json = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(payload, AgentJsonContext.Default.AggregatePayload));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Check(root.TryGetProperty("capabilities", out var caps) && caps.GetProperty("click").GetBoolean() && caps.GetProperty("worldMap").GetBoolean()
              && caps.GetProperty("character").GetBoolean() && !caps.GetProperty("act").GetBoolean(), "capabilities: click, worldMap, character, act");
        var c = root.GetProperty("clients")[0];
        Check(c.GetProperty("landblock").GetString() == "5A480000", "client landblock in full form");
        var e = c.GetProperty("enchantments")[0];
        Check(e.GetProperty("spellId").GetUInt32() == 2 && e.GetProperty("name").GetString() == "Strength Self I"
              && e.GetProperty("secondsRemaining").GetDouble() == 1800 && e.GetProperty("iconId").GetUInt32() == 0x06001234
              && e.TryGetProperty("family", out _) && e.TryGetProperty("tier", out _) && e.TryGetProperty("beneficial", out _) && e.TryGetProperty("school", out _),
            "enchantment entry has DrakBot's field names");
        Check(c.GetProperty("heading").GetDouble() == 90, "heading");
    }

    // ── world map ───────────────────────────────────────────────────────────

    private static byte[] Landblock(ushort terrainWord, byte height, Func<int, byte>? heightAt = null)
    {
        var b = new byte[8 + 81 * 2 + 81];
        for (int i = 0; i < 81; i++)
        {
            BitConverter.GetBytes(terrainWord).CopyTo(b, 8 + i * 2);
            b[8 + 162 + i] = heightAt?.Invoke(i) ?? height;
        }
        return b;
    }

    private static void WorldMap()
    {
        Section("world map render");
        // Landblock 0x0000 (south-west corner) is snow; 0xFE00 (south-east) grassland with a road word;
        // 0x00FE (north-west) is sand; everything else open sea.
        ushort snow = 15 << 2, grass = 1 << 2, road = (1 << 2) | 1, sand = 10 << 2;
        var blocks = new Dictionary<int, byte[]>
        {
            [0x0000] = Landblock(snow, 10),
            [0xFE00] = Landblock(road, 10),
            [0x00FE] = Landblock(sand, 10),
            [0x8080] = Landblock(grass, 0, i => (byte)(i / 9 * 4)),   // rising to the east: lit from the north-west
        };
        byte[] rgb = WorldMapService.Render(lb => blocks.TryGetValue(lb, out var r) ? r : null, out int found);
        int size = WorldMapService.Size;
        Check(size == 2040 && rgb.Length == size * size * 3, "2040 x 2040 RGB");
        Check(found == 4, "four landblocks drawn");
        (byte R, byte G, byte B) Px(int x, int y) { int o = (y * size + x) * 3; return (rgb[o], rgb[o + 1], rgb[o + 2]); }
        Check(Px(0, size - 1) == (0xF0, 0xF4, 0xF7), "south-west corner is snow, at the bottom-left (north up)");
        Check(Px(0, 0) == (0xD8, 0xC4, 0x8A), "landblock 0x00FE (north) is at the top: sand");
        Check(Px(0xFE * 8, size - 1) == (0xA8, 0x93, 0x6C), "a road word draws as road");
        Check(Px(1000, 1000) == (0x1F, 0x4A, 0x78), "no landblock -> sea");
        var g = Px(0x80 * 8 + 3, size - 1 - (0x80 * 8 + 3));
        Check(g.G > 0x8B, "a slope rising east is lit brighter than flat grass");
        var (wx, wy) = (0x80 * 192.0 + 72, 0x80 * 192.0 + 72);   // that cell's corner, in world units
        var (mx, my) = WorldMapService.ToPixel(wx, wy);
        Check((int)mx == 0x80 * 8 + 3 && (int)Math.Ceiling(my) - 1 == size - 1 - (0x80 * 8 + 3), "ToPixel lands on the cell that was painted");
    }

    // ── travel ──────────────────────────────────────────────────────────────

    private const string AtlasJson = """
        {"version":1,"generated":"2026-10-01T09:59:37Z","locations":[
          {"name":"Holtburg","type":"Town","cell":"A9B40001","place":"outdoor","ns":42.10,"ew":33.60,"id":1},
          {"name":"Holtburg Dungeon","type":"Dungeon","cell":"A9B20001","place":"outdoor","ns":43.57,"ew":32.98,"id":2},
          {"name":"Holtburg Portal Shrine","type":"Portal","cell":"A9B40010","place":"outdoor","ns":42.20,"ew":33.50,"id":3,"dest":{"name":"Town Network"}},
          {"name":"Barkeeper Holtburg","type":"NPC","cell":"A9B4011A","place":"outdoor","ns":42.15,"ew":33.62,"id":4},
          {"name":"Secret Room of Holtburg","type":"Landmark","cell":"01E901AD","place":"dungeon","id":5},
          {"name":"Shoushi","type":"Town","cell":"DE510001","place":"outdoor","ns":-33.50,"ew":72.80,"id":6},
          {"name":"Lifestone","type":"Lifestone","cell":"A9B40020","place":"outdoor","ns":42.05,"ew":33.55,"id":7},
          {"name":"Closed Mine","type":"Dungeon","place":"outdoor","ns":40.0,"ew":30.0,"id":8,"closed":true}
        ]}
        """;

    private static void NavSearch()
    {
        Section("travel search (RynthNav's Atlas ranking)");
        var (places, generated) = NavAtlasService.Parse(AtlasJson);
        Check(places.Count == 8 && generated == "2026-10-01T09:59:37Z", "Atlas read");
        var hits = NavAtlasService.Search(places, "holtburg", null, null, null, 40);
        Check(hits.Select(h => h.Place.Name).SequenceEqual(new[] { "Holtburg", "Holtburg Dungeon", "Holtburg Portal Shrine", "Secret Room of Holtburg", "Barkeeper Holtburg" }),
            "exact name, then starts-with by type (town, dungeon, portal), then whole-word, then inside a name - as in game");
        Check(NavAtlasService.Search(places, "holtburg", "Dungeon", null, null, 40).Single().Place.Name == "Holtburg Dungeon", "type filter");
        Check(NavAtlasService.Search(places, "h", null, null, null, 40).Count == 0, "one letter is too little to search");
        Check(NavAtlasService.Search(places, "holt dung", null, null, null, 40).Single().Place.Name == "Holtburg Dungeon", "every word somewhere in the name");
        var near = NavAtlasService.Search(places, "holtburg", null, 42.10, 33.60, 40);
        Check(near[0].Distance == 0 && near[1].Distance is > 300 and < 400, "distance from the character, in RynthNav's units (240 a degree)");
        Check(near.Single(h => h.Place.Name == "Secret Room of Holtburg").Distance == null, "a place inside a dungeon has no distance");
        Check(NavAtlasService.Search(places, "holtburg", null, null, null, 2).Count == 2, "max");

        Check(places.Single(p => p.Name == "Lifestone").Area == "near Holtburg" && places.Single(p => p.Name == "Holtburg").Area == "", "area = nearest town (a town is its own)");
        using var doc = JsonDocument.Parse(NavAtlasService.SearchJson(NavAtlasService.Search(places, "holtburg", null, 42.10, 33.60, 40), "holtburg", places.Count, generated));
        var root = doc.RootElement;
        Check(root.GetProperty("schema").GetString() == "rynthcore.nav-search/1" && root.GetProperty("count").GetInt32() == 5, "search JSON");
        var r0 = root.GetProperty("results")[0];
        Check(r0.GetProperty("name").GetString() == "Holtburg" && r0.GetProperty("type").GetString() == "Town" && r0.GetProperty("coords").GetString() == "42.1N, 33.6E"
              && r0.GetProperty("ns").GetDouble() == 42.1 && r0.GetProperty("canTravel").GetBoolean() && r0.GetProperty("distance").GetDouble() == 0, "a result: name, type, coords, distance, can travel");
        var secret = root.GetProperty("results").EnumerateArray().Single(e => e.GetProperty("name").GetString() == "Secret Room of Holtburg");
        Check(!secret.GetProperty("canTravel").GetBoolean() && !secret.TryGetProperty("ns", out _) && secret.GetProperty("where").GetString() == "dungeon", "inside a dungeon: listed, can't travel (RynthNav won't guess)");
        var closed = NavAtlasService.SearchJson(NavAtlasService.Search(places, "closed", null, null, null, 5), "closed", 8, "");
        Check(closed.Contains("\"closed\":true") && closed.Contains("\"canTravel\":false"), "a closed place can't be travelled to");
        Check(NavAtlasService.Fmt(-33.5, 72.8) == "33.5S, 72.8E", "coordinates like RynthNav's");
        var (wns, wew) = NavAtlasService.FromWorld((33.6 * 10 + 1019.5) * 24, (42.1 * 10 + 1019.5) * 24);
        Check(Math.Abs(wns - 42.1) < 1e-9 && Math.Abs(wew - 33.6) < 1e-9, "world units -> coordinates");

        var cfg = new AgentConfig { EnableRemoteControl = true };
        Check(RemoteParity.Capabilities(cfg, false, false, false, false, navAtlas: true).Travel, "travel on with the Atlas and remote control");
        Check(!RemoteParity.Capabilities(new AgentConfig(), false, false, false, false, navAtlas: true).Travel, "no remote control -> no travel (the Go would be refused)");
        Check(!RemoteParity.Capabilities(cfg, false, false, false, false, navAtlas: false).Travel, "no Atlas -> no travel");
    }

    private static void TravelStatus()
    {
        Section("travel status");
        var sent = new TravelIn { Dest = "Holtburg", Ns = 42.1, Ew = 33.6, State = "sent", SentAt = DateTimeOffset.UtcNow };
        var chat = new List<ChatLine>
        {
            new() { Text = "[RynthNav] walking to Holtburg (2.1k yd) — /rnav stop to cancel" },
            new() { Text = "Drakkon says, \"hi\"" },
        };
        // Standing 1 degree south of Holtburg: 240 units left.
        var t = RemoteParity.Travel(sent, chat, indoor: false, wx: (33.6 * 10 + 1019.5) * 24, wy: (41.1 * 10 + 1019.5) * 24)!;
        Check(t.State == "sent" && t.Note == "walking to Holtburg (2.1k yd) — /rnav stop to cancel", "RynthNav's latest note, tag removed");
        Check(t.Distance == 240, "distance left from the character's position");
        chat.Add(new ChatLine { Text = "[RynthNav] Arrived at Holtburg." });
        Check(RemoteParity.Travel(sent, chat, false, 1, 1)!.State == "arrived", "RynthNav's arrival note -> arrived");
        chat.Add(new ChatLine { Text = "[RynthNav] Arrived at Shoushi." });
        Check(RemoteParity.Travel(sent, chat, false, 1, 1)!.State == "sent", "arriving somewhere else doesn't count");
        Check(RemoteParity.Travel(sent, chat, indoor: true, 1, 1)!.Distance == null, "no distance indoors");
        Check(RemoteParity.Travel(null, chat, false, 1, 1) == null && RemoteParity.Travel(new TravelIn(), chat, false, 1, 1) == null, "no travel yet -> null");
        var stopped = RemoteParity.Travel(new TravelIn { Dest = "Holtburg", State = "stopped" }, null, false, 0, 0)!;
        Check(stopped.State == "stopped" && stopped.Note == "" && stopped.Distance == null, "stopped, no chat, no position");
        // The plugin's status.json entry, read back.
        var m = JsonSerializer.Deserialize("""{"schema":"rynthcore.client-status/1","pid":1,"travel":{"dest":"Holtburg","ns":42.1,"ew":33.6,"state":"sent","detail":"","sentAt":"2026-10-05T09:00:00Z"}}""",
            AgentJsonContext.Default.StatusFileModel)!;
        Check(m.Travel is { Dest: "Holtburg", State: "sent", Ns: 42.1 } && m.Travel.SentAt == new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero), "plugin travel entry parsed");
    }

    private static void RealAtlas(string path)
    {
        Section("real Atlas (read-only) " + path);
        var (places, generated) = NavAtlasService.Parse(File.ReadAllText(path));
        Console.WriteLine($"    {places.Count:N0} places, generated {generated}");
        Check(places.Count > 5000, "the Atlas has thousands of places");
        var hits = NavAtlasService.Search(places, "holtburg", null, 42.1, 33.6, 10);
        Console.WriteLine("    " + string.Join(" | ", hits.Select(h => $"{h.Place.Name} ({h.Place.Type}{(h.Place.Area.Length > 0 ? ", " + h.Place.Area : "")}{(h.Distance is double d ? $", {d:0} yd" : "")})")));
        Check(hits.Count > 0 && hits[0].Place.Name == "Holtburg" && hits[0].Place.Type == "Town", "\"holtburg\" finds the town first");
    }

    private static void RealSpellTable(string portal)
    {
        Section("real spell table (read-only) " + portal);
        using var db = new RynthCore2.TerrainData.DatDatabase();
        Check(db.Open(portal), "portal.dat opens");
        byte[]? raw = db.GetFileData(0x0E00000E);
        Check(raw != null, "spell table present");
        if (raw == null) return;
        var t = SpellTableService.Parse(raw, out int total);
        Console.WriteLine($"    {t.Count:N0} of {total:N0} spells");
        Check(t.Count == total && total > 5000, "every spell parsed");
        Check(t.TryGetValue(2, out var s) && s.Name == "Strength Self I" && s.Beneficial && s.Icon != 0, "spell 2 is Strength Self I, beneficial, with an icon");
    }

    private static void RealWorldMap(string cell, string? outFile)
    {
        Section("real world map (read-only) " + cell);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var db = new RynthCore2.TerrainData.DatDatabase();
        Check(db.Open(cell), "cell.dat opens");
        Console.WriteLine($"    indexed in {sw.Elapsed.TotalSeconds:0.0}s");
        byte[] rgb = WorldMapService.Render(lb => db.GetFileData(((uint)lb << 16) | 0xFFFF), out int found);
        Console.WriteLine($"    {found:N0} landblocks in {sw.Elapsed.TotalSeconds:0.0}s");
        Check(found > 20000, "most of Dereth's landblocks found");
        if (outFile != null)
        {
            string dir = Path.Combine(Path.GetTempPath(), "rc-worldmap-test-" + Guid.NewGuid().ToString("N"));
            var svc = new WorldMapService(cell, dir);
            byte[]? jpg = null;
            for (int i = 0; i < 600 && (jpg = svc.GetJpeg()) == null; i++) Thread.Sleep(200);
            Check(jpg != null, "the service renders and caches a JPEG");
            if (jpg != null) { File.WriteAllBytes(outFile, jpg); Console.WriteLine($"    {jpg.Length / 1024:N0} KB -> {outFile}"); }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private sealed class Writer
    {
        private readonly MemoryStream _ms = new();
        public void U32(uint v) => _ms.Write(BitConverter.GetBytes(v));
        public void U16(ushort v) => _ms.Write(BitConverter.GetBytes(v));
        public void Zero(int n) => _ms.Write(new byte[n]);
        public void Obfuscated(string s)
        {
            byte[] b = Encoding.Latin1.GetBytes(s);
            U16((ushort)b.Length);
            foreach (byte x in b) _ms.WriteByte((byte)((x >> 4) | (x << 4)));
            while (_ms.Length % 4 != 0) _ms.WriteByte(0);
        }
        public byte[] ToArray() => _ms.ToArray();
    }

    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    // ── raise (Skills tab "+") ──────────────────────────────────────────────

    private static void RaiseCommand()
    {
        Section("raise command (POST /command action raise)");
        var control = new AgentConfig { EnableRemoteControl = true };
        Check(RemoteParity.Capabilities(control, false, false, false, characterSheets: true, raiseXp: true).RaiseXp, "raiseXp on: control + a sheet + a client that can");
        Check(!RemoteParity.Capabilities(new AgentConfig(), false, false, false, true, raiseXp: true).RaiseXp, "off without remote control");
        Check(!RemoteParity.Capabilities(control, false, false, false, true, raiseXp: false).RaiseXp, "off when no client can (old engine or plugin)");
        Check(!RemoteParity.Capabilities(control, false, false, false, characterSheets: false, raiseXp: true).RaiseXp, "off without character sheets (nothing to show costs from)");

        var clients = new Dictionary<int, bool> { [8808] = true, [9100] = false };
        int code = RemoteParity.CheckRaise(8808, """{"kind":"attribute","id":5,"count":1,"cost":1234567,"nonce":"k3f9","extra":"dropped"}""", clients, out string norm, out string err);
        Check(code == 202 && norm == """{"kind":"attribute","id":5,"count":1,"cost":1234567,"nonce":"k3f9"}""", "accepted and rewritten in the fixed shape (extra members dropped)");
        code = RemoteParity.CheckRaise(8808, """{"kind":"Vital","id":"Stamina","count":10,"cost":42}""", clients, out norm, out _);
        Check(code == 202 && norm == """{"kind":"vital","id":"stamina","count":10,"cost":42,"nonce":""}""", "vital by name, lower-cased");
        code = RemoteParity.CheckRaise(8808, """{"kind":"train","id":22,"cost":6,"count":77}""", clients, out norm, out _);
        Check(code == 202 && norm.Contains("\"count\":1"), "train: count forced to 1");
        Check(RemoteParity.CheckRaise(1234, """{"kind":"attribute","id":5,"cost":1}""", clients, out _, out err) == 404 && err == "unknown client", "a pid the agent doesn't read -> 404");
        Check(RemoteParity.CheckRaise(9100, """{"kind":"attribute","id":5,"cost":1}""", clients, out _, out err) == 409 && err.Contains("plugin API 79"), "a client without raising -> 409 with why");
        Check(RemoteParity.CheckRaise(8808, """{"kind":"attribute","id":5,"count":1}""", clients, out _, out err) == 400 && err.Contains("confirmed cost"), "no confirmed cost -> 400");
        Check(RemoteParity.CheckRaise(8808, """{"kind":"attribute","id":5,"cost":0}""", clients, out _, out _) == 400, "cost 0 -> 400");
        Check(RemoteParity.CheckRaise(8808, """{"kind":"attribute","id":5,"cost":4294967296}""", clients, out _, out _) == 400, "cost over 32 bits -> 400");
        Check(RemoteParity.CheckRaise(8808, """{"kind":"attribute","id":9,"cost":1}""", clients, out _, out _) == 400
              && RemoteParity.CheckRaise(8808, """{"kind":"vital","id":2,"cost":1}""", clients, out _, out _) == 400
              && RemoteParity.CheckRaise(8808, """{"kind":"skill","id":"34","cost":1}""", clients, out _, out _) == 400, "bad ids -> 400");
        Check(RemoteParity.CheckRaise(8808, """{"kind":"skill","id":34,"count":101,"cost":1}""", clients, out _, out _) == 400
              && RemoteParity.CheckRaise(8808, """{"kind":"skill","id":34,"count":0,"cost":1}""", clients, out _, out _) == 400, "count out of 1..100 -> 400");
        Check(RemoteParity.CheckRaise(8808, """{"kind":"luminance","id":1,"cost":1}""", clients, out _, out _) == 400
              && RemoteParity.CheckRaise(8808, "not json", clients, out _, out _) == 400
              && RemoteParity.CheckRaise(8808, "", clients, out _, out _) == 400, "unknown kind / junk / empty -> 400");
        code = RemoteParity.CheckRaise(8808, "{\"kind\":\"skill\",\"id\":34,\"cost\":5,\"nonce\":\"a\\\"},{\\\"x\"}", clients, out norm, out _);
        Check(code == 202 && norm.EndsWith("\"nonce\":\"ax\"}") && JsonDocument.Parse(norm).RootElement.GetProperty("nonce").GetString() == "ax", "a nonce can't break out of its string");

        // The plugin's flag reaches the client and the phone's JSON.
        var m = JsonSerializer.Deserialize("""{"schema":"rynthcore.client-status/1","pid":1,"raiseXp":true}""", AgentJsonContext.Default.StatusFileModel)!;
        Check(m.RaiseXp, "status.json raiseXp read");
        var old = JsonSerializer.Deserialize("""{"schema":"rynthcore.client-status/1","pid":1}""", AgentJsonContext.Default.StatusFileModel)!;
        Check(!old.RaiseXp, "an older plugin: no raiseXp -> false");
        string json = JsonSerializer.Serialize(new AggregatePayload
        {
            Capabilities = RemoteParity.Capabilities(control, false, false, false, true, raiseXp: true),
            Clients = { new ClientStatus { Pid = 1, RaiseXp = true } },
        }, AgentJsonContext.Default.AggregatePayload);
        using var doc = JsonDocument.Parse(json);
        Check(doc.RootElement.GetProperty("capabilities").GetProperty("raiseXp").GetBoolean()
              && doc.RootElement.GetProperty("clients")[0].GetProperty("raiseXp").GetBoolean(), "capabilities.raiseXp and clients[].raiseXp in the payload");
    }

    // The flicker's back-end half: a status file caught mid-write must not drop its client from a snapshot,
    // and a read must not stop the plugin's atomic replace.
    private static void StatusFileReads()
    {
        Section("status files: shared reads and the last good parse");
        string dir = Path.Combine(Path.GetTempPath(), "statusagent-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "RynthCore.25580.status.json");
            File.WriteAllText(path, """{"schema":"rynthcore.client-status/1","pid":25580,"character":"Lucy McJuicy","moveProto":2}""");
            static StatusFileModel? Parse(string t) => JsonSerializer.Deserialize(t, AgentJsonContext.Default.StatusFileModel);
            var t0 = new DateTime(2026, 10, 5, 11, 21, 21, DateTimeKind.Utc);
            var m = StatusFileCache.Read(path, Parse, t0);
            Check(m?.Character == "Lucy McJuicy" && m.MoveProto == 2, "a good file reads (and carries moveProto)");

            // What the agent used to see: the plugin's in-place fallback caught half-written.
            File.WriteAllText(path, """{"schema":"rynthcore.client-status/1","pid":25580,"charac""");
            var torn = StatusFileCache.Read(path, Parse, t0.AddMilliseconds(60));
            Check(torn != null && ReferenceEquals(torn, m), "a torn file answers the last good parse - the client stays in the snapshot");
            File.WriteAllText(path, "");
            Check(StatusFileCache.Read(path, Parse, t0.AddMilliseconds(120)) is { Character: "Lucy McJuicy" }, "an empty file too");
            Check(StatusFileCache.Read(path, Parse, t0.AddSeconds(3.5)) == null, "but not for longer than 3 s (a file that stays broken is a real problem)");
            Check(StatusFileCache.Read(Path.Combine(dir, "missing.json"), Parse, t0) == null, "a missing file with no history is just missing");

            // While the agent has the file open, the plugin's replace (File.Move over it) fails on Windows
            // whatever the read's sharing (MoveFileEx won't replace an open file). RynthRemote 0.3 then
            // overwrote the file in place, which is what tore it; 0.4 skips that one write instead. The two
            // halves the agent relies on: the replace really fails mid-read (so skipping is needed), and the
            // shared read doesn't block an old plugin's in-place rewrite (its torn result is covered above).
            File.WriteAllText(path, """{"pid":1,"character":"old"}""");
            using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, """{"pid":1,"character":"new"}""");
                bool moved;
                try { File.Move(tmp, path, overwrite: true); moved = true; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { moved = false; }
                Check(!moved, "a replace fails while the agent reads - why RynthRemote 0.4 skips that write instead of overwriting in place");
                try { File.Delete(tmp); } catch { }
                bool rewrote;
                try { File.WriteAllText(path, """{"pid":1,"character":"inplace"}"""); rewrote = true; } catch (IOException) { rewrote = false; }
                Check(rewrote, "an old plugin's in-place rewrite isn't blocked by the agent's read (no more 'status write failed')");
            }
            Check(StatusFileCache.ReadAllTextShared(path).Contains("inplace"), "and the next read sees it");

            StatusFileCache.Read(path, Parse, t0);
            StatusFileCache.ForgetAllBut(Array.Empty<string>());
            Check(StatusFileCache.CountForTests == 0, "files that are gone are forgotten");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static void MoveProtocol()
    {
        Section("hold-to-move protocol passes through");
        var payload = new AggregatePayload { Host = "PC", ClientCount = 2, Clients = { new ClientStatus { Pid = 1, MoveProto = 2 }, new ClientStatus { Pid = 2 } } };
        using var doc = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(payload, AgentJsonContext.Default.AggregatePayload));
        var cs = doc.RootElement.GetProperty("clients");
        Check(cs[0].GetProperty("moveProto").GetInt32() == 2, "a RynthRemote 0.4 client says moveProto 2");
        Check(!cs[1].TryGetProperty("moveProto", out var mp) || mp.GetInt32() == 0, "an older plugin's client: 0 or absent, the phone uses the old protocol");
        var old = JsonSerializer.Deserialize("""{"pid":1}""", AgentJsonContext.Default.StatusFileModel)!;
        Check(old.MoveProto == 0, "a status file without the field reads as 0");
    }

    private static void Section(string title) => Console.WriteLine("\n== " + title);

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (!ok) _failed++;
        Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what);
    }
}
