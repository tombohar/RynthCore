using System.Globalization;

namespace RynthCore.StatusAgent;

/// <summary>
/// The pieces that make a RynthCore client look to DrakRemote the way a DrakBot client does
/// (2026-10-05 parity work): landblock ids in the form the app matches on, the capabilities block,
/// enchantments with names and icons, and the tap-to-click coordinate mapping. Pure functions, so the
/// agent tests (tools\StatusAgentTests) cover them without a game running.
/// </summary>
internal static class RemoteParity
{
    // ── landblocks ──────────────────────────────────────────────────────────

    /// <summary>
    /// A landblock as DrakRemote matches it: the full cell-style id "XXYY0000". The engine (and the baked
    /// floor-plan file names) write the short form "0000XXYY"; DrakBot writes the full one, and since
    /// DrakRemote f87130c the app reads a short map id as full (shifts it) but takes the client's id as
    /// written - so a RynthCore client never matched its own dungeon's maps. Writing both full fixes it
    /// for the phone already installed. Empty or unparseable stays as it was.
    /// </summary>
    public static string LandblockFull(string? landblock)
    {
        if (string.IsNullOrWhiteSpace(landblock)) return "";
        if (!uint.TryParse(landblock.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return landblock;
        if (v == 0) return "";
        uint full = v <= 0xFFFF ? v << 16 : v & 0xFFFF0000u;
        return full.ToString("X8", CultureInfo.InvariantCulture);
    }

    /// <summary>The short landblock (0xXXYY) from either form ("5A48", "00005A48", "5A480000", "5A48013F").</summary>
    public static bool TryLandblockShort(string? s, out uint lb)
    {
        lb = 0;
        if (string.IsNullOrWhiteSpace(s)
            || !uint.TryParse(s.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return false;
        lb = v > 0xFFFF ? v >> 16 : v;
        return lb != 0;
    }

    // ── capabilities ────────────────────────────────────────────────────────

    /// <summary>What the agent offers, from its config and what is on this box. act / nearby / pick /
    /// dungeon are DrakBot's (no RynthCore route for them yet), and a scraped window has no picture while
    /// minimized, so those stay off.</summary>
    public static AgentCapabilities Capabilities(AgentConfig cfg, bool iconDat, bool dungeonMaps, bool worldMap, bool characterSheets, bool navAtlas = false, bool raiseXp = false)
    {
        bool control = cfg.EnableRemoteControl;
        return new AgentCapabilities
        {
            Inventory = true,
            Settings = true,
            Movement = control,
            Chat = true,
            Icons = iconDat,
            CloseClient = control,
            Video = cfg.EnableScreenStream,
            VideoMinimized = false,
            VideoHd = cfg.EnableVideoStream,
            Click = control && cfg.EnableScreenStream,
            Runs = true,
            Maps = dungeonMaps || worldMap,
            Dungeon = false,
            Act = false,
            Nearby = false,
            Pick = false,
            Character = characterSheets,
            WorldMap = worldMap,
            // Searching needs the Atlas; going needs the command channel the travel rides on.
            Travel = navAtlas && control,
            // Spending XP rides the command channel and needs a client whose plugin can (engine API v79).
            RaiseXp = raiseXp && control && characterSheets,
        };
    }

    // ── raise (spend XP / skill credits from the Skills tab) ────────────────

    /// <summary>
    /// Checks a phone raise before it becomes a command file and rewrites the value in a fixed shape:
    /// {"kind":"attribute|vital|skill|train","id":N or "health|stamina|mana","count":1..100,"cost":N,"nonce":".."}.
    /// The pid must be a client this agent reads whose plugin reports raiseXp (<paramref name="raiseClients"/>:
    /// pid -> raiseXp). Returns the HTTP status to answer with (202 = write the command) and why not.
    /// The plugin and the engine check everything again (the engine against fresh numbers and the cost).
    /// </summary>
    public static int CheckRaise(int pid, string? value, IReadOnlyDictionary<int, bool> raiseClients, out string normalized, out string error)
    {
        normalized = "";
        error = "";
        if (!raiseClients.TryGetValue(pid, out bool can)) { error = "unknown client"; return 404; }
        if (!can) { error = "raising isn't available on that client (needs the RynthRemote plugin 0.4 and a RynthCore engine with plugin API 79)"; return 409; }
        if (string.IsNullOrWhiteSpace(value)) { error = "bad raise"; return 400; }
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(value);
            var r = doc.RootElement;
            if (r.ValueKind != System.Text.Json.JsonValueKind.Object) { error = "bad raise"; return 400; }
            string kind = r.TryGetProperty("kind", out var k) && k.ValueKind == System.Text.Json.JsonValueKind.String ? (k.GetString() ?? "").ToLowerInvariant() : "";
            if (kind is not ("attribute" or "vital" or "skill" or "train")) { error = "bad raise kind"; return 400; }

            string id;
            if (!r.TryGetProperty("id", out var idEl)) { error = "bad raise id"; return 400; }
            if (idEl.ValueKind == System.Text.Json.JsonValueKind.Number && idEl.TryGetInt32(out int n))
            {
                bool ok = kind switch { "attribute" => n is >= 1 and <= 6, "vital" => n is 1 or 3 or 5, _ => n is >= 1 and <= 54 };
                if (!ok) { error = "bad raise id"; return 400; }
                id = n.ToString(CultureInfo.InvariantCulture);
            }
            else if (kind == "vital" && idEl.ValueKind == System.Text.Json.JsonValueKind.String
                     && (idEl.GetString() ?? "").ToLowerInvariant() is "health" or "stamina" or "mana")
                id = "\"" + idEl.GetString()!.ToLowerInvariant() + "\"";
            else { error = "bad raise id"; return 400; }

            int count = 1;
            if (kind != "train" && r.TryGetProperty("count", out var c)
                && (c.ValueKind != System.Text.Json.JsonValueKind.Number || !c.TryGetInt32(out count) || count < 1 || count > 100))
            { error = "bad raise count"; return 400; }

            if (!r.TryGetProperty("cost", out var costEl) || costEl.ValueKind != System.Text.Json.JsonValueKind.Number
                || !costEl.TryGetInt64(out long cost) || cost <= 0 || cost > uint.MaxValue)
            { error = "a raise needs the confirmed cost"; return 400; }

            string nonce = "";
            if (r.TryGetProperty("nonce", out var nEl) && nEl.ValueKind == System.Text.Json.JsonValueKind.String)
                foreach (char ch in nEl.GetString() ?? "")
                    if (nonce.Length < 40 && (char.IsAsciiLetterOrDigit(ch) || ch == '-' || ch == '_')) nonce += ch;

            normalized = "{\"kind\":\"" + kind + "\",\"id\":" + id
                + ",\"count\":" + count.ToString(CultureInfo.InvariantCulture)
                + ",\"cost\":" + cost.ToString(CultureInfo.InvariantCulture)
                + ",\"nonce\":\"" + nonce + "\"}";
            return 202;
        }
        catch (System.Text.Json.JsonException) { error = "bad raise"; return 400; }
    }

    // ── travel ──────────────────────────────────────────────────────────────

    private const string NavTag = "[RynthNav] ";

    /// <summary>
    /// The phone's travel status: the plugin's record of what it sent, RynthNav's latest chat note (its
    /// "[RynthNav] ..." lines reach the status's recent chat), "arrived" when that note says so, and the
    /// straight-line distance left while outdoors. RynthNav's own progress line and ETA are not reachable
    /// from another plugin (its status export isn't a RynthPluginGet*Json), so the note stands in for them.
    /// </summary>
    public static TravelOut? Travel(TravelIn? t, IReadOnlyList<ChatLine>? recentChat, bool indoor, double wx, double wy)
    {
        if (t == null || string.IsNullOrEmpty(t.State)) return null;
        string note = "";
        if (recentChat != null)
            for (int i = recentChat.Count - 1; i >= 0; i--)
            {
                string line = recentChat[i].Text ?? "";
                if (line.StartsWith(NavTag, StringComparison.Ordinal)) { note = line[NavTag.Length..].Trim(); break; }
            }
        string state = t.State;
        if (state == "sent" && note.StartsWith("Arrived at ", StringComparison.Ordinal)
            && (t.Dest.Length == 0 || note.Contains(t.Dest, StringComparison.OrdinalIgnoreCase)))
            state = "arrived";
        double? left = null;
        if (!indoor && (wx != 0 || wy != 0) && t.Ns is double dns && t.Ew is double dew)
        {
            var (ns, ew) = NavAtlasService.FromWorld(wx, wy);
            left = Math.Round(NavAtlasService.Distance(ns, ew, dns, dew));
        }
        return new TravelOut { Dest = t.Dest, Ns = t.Ns, Ew = t.Ew, State = state, Detail = t.Detail, SentAt = t.SentAt, Note = note, Distance = left };
    }

    // ── enchantments ────────────────────────────────────────────────────────

    /// <summary>The plugin's bare enchantments with the spell table's name, family, tier, school,
    /// beneficial flag and icon; "Spell N" (beneficial, no icon) for one the table doesn't know.</summary>
    public static List<EnchantmentOut>? Enchantments(List<EnchantmentIn>? input, Func<uint, SpellTableService.SpellInfo?> lookup)
    {
        if (input == null) return null;
        var list = new List<EnchantmentOut>(input.Count);
        foreach (var e in input)
        {
            if (e.SpellId == 0) continue;
            var info = lookup(e.SpellId);
            string name = info is { Name.Length: > 0 } i ? i.Name : "Spell " + e.SpellId.ToString(CultureInfo.InvariantCulture);
            list.Add(new EnchantmentOut
            {
                SpellId = e.SpellId,
                Name = name,
                Family = info?.Category ?? 0,
                Tier = SpellTableService.TierFromName(name),
                SecondsRemaining = double.IsFinite(e.SecondsRemaining) ? e.SecondsRemaining : -1,
                Beneficial = info?.Beneficial ?? true,
                School = info?.School ?? 0,
                IconId = info?.Icon ?? 0,
            });
        }
        // Soonest first; the never-lapsing ones (-1) last, as DrakBot orders them for the countdown.
        list.Sort(static (a, b) =>
        {
            bool ap = a.SecondsRemaining < 0, bp = b.SecondsRemaining < 0;
            if (ap != bp) return ap ? 1 : -1;
            int c = a.SecondsRemaining.CompareTo(b.SecondsRemaining);
            return c != 0 ? c : a.SpellId.CompareTo(b.SpellId);
        });
        return list;
    }

    // ── tap-to-click ────────────────────────────────────────────────────────

    /// <summary>
    /// The client-area pixel a phone tap lands on: (u, v) in 0..1 of the frame the stream shows (the
    /// window's client area, origin top-left, same aspect - a downscaled stream keeps it), so x = u * (w-1),
    /// y = v * (h-1), clamped. The app has already taken the letterbox out (rynthTap.normalize).
    /// </summary>
    public static (int X, int Y) ClickPoint(double u, double v, int clientWidth, int clientHeight)
    {
        if (!double.IsFinite(u)) u = 0;
        if (!double.IsFinite(v)) v = 0;
        int x = (int)Math.Round(Math.Clamp(u, 0, 1) * Math.Max(0, clientWidth - 1));
        int y = (int)Math.Round(Math.Clamp(v, 0, 1) * Math.Max(0, clientHeight - 1));
        return (x, y);
    }

    /// <summary>A mouse message's lParam: y in the high word, x in the low.</summary>
    public static IntPtr MouseLParam(int x, int y) => (IntPtr)unchecked((int)(((uint)(y & 0xFFFF) << 16) | (uint)(x & 0xFFFF)));
}
