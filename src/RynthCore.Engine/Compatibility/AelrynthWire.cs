// ============================================================================
//  RynthCore.Engine - Compatibility/AelrynthWire.cs
//
//  Pure parsing for the Aelrynth-only features: no AC, no engine state, so the
//  offline tests (tools/AelrynthTests) compile this file as is.
//
//    ServerDetect  which server the client is on, from the launch command line
//                  (-h host[:port] -p port), the world name the server announced
//                  at login, and the Bank mod's custom player properties.
//    MasteryWire   the SkillMastery mod's /mastery-data reply: ONE chat line,
//                  "~ael1 " + a JSON object (Mods/src/Aeshnidae.SkillMastery,
//                  DataCommand.cs + CompanionFeed.cs), and ACE's "Unknown
//                  command: <name>" refusal.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace RynthCore.Engine.Compatibility;

/// <summary>Where the launch command line says the client connects.</summary>
internal enum ServerHostKind
{
    /// <summary>No -h on the command line (or it couldn't be read).</summary>
    Unknown,
    /// <summary>aelrynth.com (or a subdomain) / 144.217.84.148, any port.</summary>
    Aelrynth,
    /// <summary>Loopback or a private LAN address: a developer's own server.</summary>
    Local,
    /// <summary>Any other host: somebody else's server.</summary>
    Other,
}

/// <summary>One detection result. Immutable.</summary>
internal sealed class ServerVerdict
{
    public bool IsAelrynth { get; init; }
    public bool IsStaging { get; init; }
    /// <summary>Plain-English why, for /rc server and the log.</summary>
    public string Reason { get; init; } = "";

    public static readonly ServerVerdict Unknown = new() { Reason = "nothing known yet" };
}

internal static class ServerDetect
{
    public const string AelrynthDomain = "aelrynth.com";
    public const string AelrynthIp = "144.217.84.148";
    /// <summary>The live world (9020, 9021 the second socket ACE opens).</summary>
    public const int LivePortLow = 9020, LivePortHigh = 9021;
    /// <summary>The staging world on the same VPS (ops/staging.sh).</summary>
    public const int StagingPort = 9030;

    /// <summary>The Bank mod's custom PropertyInt64 ids (Aeshnidae.Bank ClientTotals): Radiance earned, Luminance banked, Luminance drawn this session.</summary>
    public const uint RadianceEarnedProperty = 9101, LuminanceBankedProperty = 9102, LuminanceDrawnProperty = 9103;

    /// <summary>
    /// The host and port from acclient's command line: "-h host:port" (the RynthCore
    /// launcher's ACE form) or "-h host -p port" (GDLE form, other launchers). Only
    /// -h and -p are read; the rest (account, password) is never kept.
    /// </summary>
    public static bool TryParseLaunchHost(string? commandLine, out string host, out int port)
    {
        host = "";
        port = 0;
        if (string.IsNullOrEmpty(commandLine))
            return false;
        List<string> args = SplitArgs(commandLine);
        for (int i = 0; i + 1 < args.Count; i++)
        {
            if (string.Equals(args[i], "-h", StringComparison.OrdinalIgnoreCase))
            {
                string v = args[i + 1].Trim();
                int colon = v.LastIndexOf(':');
                if (colon > 0 && v.IndexOf(':') == colon
                    && int.TryParse(v.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int p))
                {
                    host = v.Substring(0, colon);
                    if (port == 0) port = p;
                }
                else
                {
                    host = v;
                }
            }
            else if (string.Equals(args[i], "-p", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int p))
            {
                port = p;
            }
        }
        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        return host.Length > 0;
    }

    /// <summary>
    /// acclient's own splitting (ConvertCommandLineToArgList, 0x004098F0; the launcher's
    /// AcClientCommandLine), so -h is the host the client itself reads: spaces split outside quotes,
    /// a quote only toggles and is dropped, a backslash is ordinary. An open quote (the client refuses
    /// the whole line) still yields what was read.
    /// </summary>
    private static List<string> SplitArgs(string s)
    {
        var list = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool inQuotes = false, any = false;
        foreach (char c in s)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (c == ' ' && !inQuotes)
            {
                if (any) { list.Add(cur.ToString()); cur.Clear(); any = false; }
                continue;
            }
            cur.Append(c);
            any = true;
        }
        if (any) list.Add(cur.ToString());
        return list;
    }

    public static ServerHostKind ClassifyHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return ServerHostKind.Unknown;
        string h = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (h == AelrynthDomain || h.EndsWith("." + AelrynthDomain, StringComparison.Ordinal) || h == AelrynthIp)
            return ServerHostKind.Aelrynth;
        if (h == "localhost" || h == "::1" || h == "[::1]" || h.StartsWith("127.", StringComparison.Ordinal)
            || h.StartsWith("10.", StringComparison.Ordinal) || h.StartsWith("192.168.", StringComparison.Ordinal)
            || IsPrivate172(h))
            return ServerHostKind.Local;
        return ServerHostKind.Other;
    }

    private static bool IsPrivate172(string h)
    {
        if (!h.StartsWith("172.", StringComparison.Ordinal)) return false;
        int dot = h.IndexOf('.', 4);
        return dot > 4 && int.TryParse(h.AsSpan(4, dot - 4), NumberStyles.None, CultureInfo.InvariantCulture, out int b)
            && b >= 16 && b <= 31;
    }

    /// <summary>The server's own name for the world: Aelrynth, or Aeshnidae (its earlier name, still in the live config).</summary>
    public static bool IsAelrynthWorldName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string n = name.Trim();
        return n.StartsWith("Aelrynth", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("Aeshnidae", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Combines the signals. Unknown means not Aelrynth.
    ///   - Aelrynth's host (domain or IP, any port): yes. Port 9030 or a world name
    ///     with "Staging" in it is the staging world (same mods, IsStaging).
    ///   - Somebody else's public host: no, unless BOTH server-sent signals agree
    ///     (the announced world name and the Bank mod's properties) - Aelrynth on a
    ///     new address before this list knows it.
    ///   - A local/LAN host or no host at all: yes when the server announced an
    ///     Aelrynth world name or sends the Bank mod's properties (a dev copy).
    /// </summary>
    public static ServerVerdict Decide(ServerHostKind hostKind, string? host, int port, string? announcedWorld, bool bankProperties)
    {
        bool worldMatch = IsAelrynthWorldName(announcedWorld);
        bool stagingName = announcedWorld != null && announcedWorld.IndexOf("staging", StringComparison.OrdinalIgnoreCase) >= 0;
        string where = host is { Length: > 0 } ? (port > 0 ? $"{host}:{port}" : host) : "no host on the command line";
        string world = announcedWorld is { Length: > 0 } ? $"world \"{announcedWorld}\"" : "no world name yet";
        string bank = bankProperties ? "Bank properties seen" : "no Bank properties";

        switch (hostKind)
        {
            case ServerHostKind.Aelrynth:
            {
                bool staging = port == StagingPort || stagingName;
                return new ServerVerdict
                {
                    IsAelrynth = true,
                    IsStaging = staging,
                    Reason = $"Aelrynth{(staging ? " staging" : "")}: connected to {where} ({world}, {bank})",
                };
            }
            case ServerHostKind.Other:
                if (worldMatch && bankProperties)
                    return new ServerVerdict
                    {
                        IsAelrynth = true,
                        IsStaging = stagingName,
                        Reason = $"Aelrynth: {where} is not a known Aelrynth address, but the server says {world} and sends Bank properties",
                    };
                return new ServerVerdict { Reason = $"not Aelrynth: connected to {where} ({world}, {bank})" };
            default:
                if (worldMatch || bankProperties)
                    return new ServerVerdict
                    {
                        IsAelrynth = true,
                        IsStaging = stagingName,
                        Reason = $"Aelrynth (local copy): {where}, {world}, {bank}",
                    };
                return new ServerVerdict { Reason = $"not Aelrynth: {where}, {world}, {bank}" };
        }
    }
}

/// <summary>One trained skill's mastery, as /mastery-data sends it.</summary>
internal sealed class MasterySkill
{
    public int Id;                 // STypeSkill
    public string Key = "";        // Skill enum name, lower case: what /raise takes
    public string Name = "";
    public bool Specialized;
    public long Base, Current;     // the server's, mastery bonus included
    public int Ranks;              // mastery ranks bought (RanksPerSkillPoint of them make a point)
    public string Points = "";     // ranks as points, "3.7"
    public int Bonus;              // whole points on the skill now
    public long Next;              // Radiance for the next raise; 0 at the ceiling
    public int Afford;             // raises in a row the banked Radiance pays for (capped at 999)
    public bool Ceiling;           // at the server's mastery ceiling
    public bool Locked;            // the server wants the retail maximum first
}

/// <summary>One /mastery-data reply. Immutable once parsed.</summary>
internal sealed class MasteryData
{
    public bool Ok;
    public string Why = "";
    public bool Enabled;
    public long Radiance;          // banked Radiance on the account: what a raise spends
    public int PerPoint = 1;       // raises per skill point
    public int CeilingPoints;      // 0 = no ceiling
    public bool RequireCap;
    public Dictionary<int, MasterySkill> Skills = new();
}

internal static class MasteryWire
{
    /// <summary>The Companion wire prefix (CompanionFeed.Prefix in the server mods).</summary>
    public const string Prefix = "~ael1 ";
    public const string DataCommand = "mastery-data";

    /// <summary>
    /// The prefix within the line's first 32 characters (in case the client puts
    /// something in front), with no quote before it: a player who SAYS "~ael1 {...}"
    /// arrives as Name says, "~ael1 ..." and is not a server line.
    /// </summary>
    public static bool IsDataLine(string? line) => PrefixAt(line) >= 0;

    private static int PrefixAt(string? line)
    {
        if (line == null || line.Length < Prefix.Length) return -1;
        int at = line.IndexOf(Prefix, StringComparison.Ordinal);
        return at >= 0 && at <= 32 && line.LastIndexOf('"', at) < 0 ? at : -1;
    }

    /// <summary>The data line's "t" (what it is), or "" when it isn't one or can't be read.</summary>
    public static string TypeOf(string? line)
    {
        int at = PrefixAt(line);
        if (at < 0) return "";
        try
        {
            using JsonDocument doc = JsonDocument.Parse(Body(line!, at));
            return doc.RootElement.ValueKind == JsonValueKind.Object ? Str(doc.RootElement, "t") : "";
        }
        catch { return ""; }
    }

    private static string Body(string line, int at)
    {
        string body = line.Substring(at + Prefix.Length);
        int end = body.LastIndexOf('}');
        return end >= 0 ? body.Substring(0, end + 1) : body;
    }

    /// <summary>
    /// Parses a /mastery-data line ("t":"mastery"). False for any other line, another
    /// "t", or JSON that can't be read. Fields the server adds later are ignored;
    /// missing ones keep their defaults.
    /// </summary>
    public static bool TryParse(string? line, out MasteryData? data)
    {
        data = null;
        int at = PrefixAt(line);
        if (at < 0) return false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(Body(line!, at));
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Str(root, "t") != "mastery")
                return false;
            var d = new MasteryData
            {
                Ok = Bool(root, "ok"),
                Why = Str(root, "why"),
                Enabled = Bool(root, "enabled"),
                Radiance = Math.Max(0, Long(root, "radiance")),
                PerPoint = (int)Math.Clamp(Long(root, "perPoint", 1), 1, 1_000_000),
                CeilingPoints = (int)Math.Clamp(Long(root, "ceiling"), 0, int.MaxValue),
                RequireCap = Bool(root, "requireCap"),
            };
            if (root.TryGetProperty("skills", out JsonElement skills) && skills.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement s in skills.EnumerateArray())
                {
                    if (s.ValueKind != JsonValueKind.Object) continue;
                    int id = (int)Math.Clamp(Long(s, "id"), 0, int.MaxValue);
                    string key = Str(s, "k");
                    if (id <= 0 || !IsSkillKey(key)) continue;
                    d.Skills[id] = new MasterySkill
                    {
                        Id = id,
                        Key = key,
                        Name = Str(s, "name"),
                        Specialized = Str(s, "adv") == "spec",
                        Base = Long(s, "base"),
                        Current = Long(s, "cur"),
                        Ranks = (int)Math.Clamp(Long(s, "ranks"), 0, int.MaxValue),
                        Points = Str(s, "points"),
                        Bonus = (int)Math.Clamp(Long(s, "bonus"), 0, int.MaxValue),
                        Next = Math.Max(0, Long(s, "next")),
                        Afford = (int)Math.Clamp(Long(s, "afford"), 0, int.MaxValue),
                        Ceiling = Bool(s, "ceiling"),
                        Locked = Bool(s, "locked"),
                    };
                }
            }
            data = d;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// ACE's refusal of a command it doesn't have: "Unknown command: &lt;name&gt;"
    /// (GameActionTalk). True when <paramref name="line"/> is that for <paramref name="command"/>.
    /// </summary>
    public static bool IsUnknownCommand(string? line, string command)
    {
        const string marker = "Unknown command: ";
        if (line == null) return false;
        int at = line.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0 || at > 32 || (at > 0 && line.LastIndexOf('"', at - 1) >= 0)) return false;
        return string.Equals(line.Substring(at + marker.Length).Trim(), command, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A Skill enum name in lower case ("meleedefense"): what the mod's /raise matches.</summary>
    public static bool IsSkillKey(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 40) return false;
        foreach (char c in key)
            if (c < 'a' || c > 'z') return false;
        return true;
    }

    /// <summary>
    /// The server command text for a raise, as the client sends a typed "/raise ..."
    /// (ACE runs Talk text that starts with '@' as a command): "@raise meleedefense 1".
    /// Null when the key or count isn't one the mod's parser reads unambiguously.
    /// </summary>
    public static string? RaiseCommand(string? key, int count)
    {
        if (!IsSkillKey(key) || count < 1 || count > 999) return null;
        return "@raise " + key + " " + count.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The server command text that asks for the data line.</summary>
    public const string DataRequest = "@" + DataCommand;

    // ── JSON helpers: tolerant of type drift (a number sent as a string, and the reverse) ──

    private static string Str(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out JsonElement v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => "",
        };
    }

    private static long Long(JsonElement o, string name, long fallback = 0)
    {
        if (!o.TryGetProperty(name, out JsonElement v)) return fallback;
        if (v.ValueKind == JsonValueKind.Number)
        {
            if (v.TryGetInt64(out long l)) return l;
            if (v.TryGetDouble(out double d)) return d >= long.MaxValue ? long.MaxValue : d <= long.MinValue ? long.MinValue : (long)d;
            return fallback;
        }
        if (v.ValueKind == JsonValueKind.String
            && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long s))
            return s;
        return fallback;
    }

    private static bool Bool(JsonElement o, string name) =>
        o.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;
}
