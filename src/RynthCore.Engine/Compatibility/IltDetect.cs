// ============================================================================
//  RynthCore.Engine - Compatibility/IltDetect.cs
//
//  Pure detection for Infinite Leaftide (ILT), the ACECustom server: no AC, no
//  engine state, so the offline tests (tools/IltTests) compile this file as is.
//  The stateful side (command line, announced world name, engine.json lists,
//  the /rc ilt override) is IltInfo.cs.
//
//  Signals, either one is enough:
//    - the launch command line's -h host: game.infiniteleaftide.online, as the
//      public AC server list (acresources/serverslist Servers.xml) gives it, or
//      any other host under infiniteleaftide.online;
//    - the world name the server announced at login: "InfiniteLeaftide" (the
//      name UtilityBelt's Leaftide tools and SilentKelpie's ILT Hub check for),
//      or anything with "Leaftide" in it.
//  Both lists can be replaced in engine.json ("IltServerNames", "IltHosts").
//  A third signal is the RynthILT plugin's own verdict (its server list, or the
//  character's "Treat this world as ILT"): Combine adds it, ParsePluginStatus
//  reads RynthILTGetStatusJson (UI/Data/IltData.cs polls it).
//
//  Unlike Aelrynth detection nothing here unlocks a server-side action that
//  could hurt another server: a false positive only shows the ILT bar button,
//  and the Bounties window's "/bounty list" gets "Unknown command" (after which
//  RynthILT stops reading). So one signal is enough, and a dev copy on
//  localhost counts when it announces an ILT world name.
// ============================================================================

using System;
using System.Collections.Generic;

namespace RynthCore.Engine.Compatibility;

/// <summary>One ILT detection result. Immutable.</summary>
internal sealed class IltVerdict
{
    public bool IsIlt { get; init; }
    /// <summary>Plain-English why, for /rc ilt and the log.</summary>
    public string Reason { get; init; } = "";

    public static readonly IltVerdict Unknown = new() { Reason = "nothing known yet" };
}

internal static class IltDetect
{
    /// <summary>World-name patterns used when engine.json has no "IltServerNames" ('*' = any run of characters, case-insensitive).</summary>
    public static readonly string[] DefaultServerNames = { "InfiniteLeaftide", "*Leaftide*" };

    /// <summary>Host names used when engine.json has no "IltHosts": the domain and every host under it ('*' allowed too).</summary>
    public static readonly string[] DefaultHosts = { "infiniteleaftide.online" };

    /// <summary>True when <paramref name="world"/> matches one of <paramref name="patterns"/>.</summary>
    public static bool WorldNameMatches(string? world, IEnumerable<string>? patterns, out string matched)
    {
        matched = "";
        if (string.IsNullOrWhiteSpace(world) || patterns == null) return false;
        string w = world.Trim();
        foreach (string p in patterns)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            if (!Matches(w, p.Trim())) continue;
            matched = p.Trim();
            return true;
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="host"/> is one of <paramref name="patterns"/>: the same name, a
    /// host under it ("game.infiniteleaftide.online" for "infiniteleaftide.online"), or a '*'
    /// pattern match. An IP address matches only itself. Case and a trailing dot are ignored.
    /// </summary>
    public static bool HostMatches(string? host, IEnumerable<string>? patterns, out string matched)
    {
        matched = "";
        if (string.IsNullOrWhiteSpace(host) || patterns == null) return false;
        string h = host.Trim().TrimEnd('.').ToLowerInvariant();
        foreach (string raw in patterns)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string p = raw.Trim().TrimEnd('.').ToLowerInvariant();
            bool hit = p.Contains('*')
                ? Matches(h, p)
                : h == p || (!IsIpv4(p) && h.EndsWith("." + p, StringComparison.Ordinal));
            if (!hit) continue;
            matched = raw.Trim();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Combines the signals: ILT when the launch host or the announced world name matches.
    /// Null lists mean the defaults; an empty list turns that signal off.
    /// </summary>
    public static IltVerdict Decide(string? host, string? announcedWorld,
                                    IReadOnlyList<string>? serverNames, IReadOnlyList<string>? hosts)
    {
        IReadOnlyList<string> names = serverNames ?? DefaultServerNames;
        IReadOnlyList<string> hostList = hosts ?? DefaultHosts;
        string where = host is { Length: > 0 } ? host : "no host on the command line";
        string world = announcedWorld is { Length: > 0 } ? $"world \"{announcedWorld}\"" : "no world name yet";

        bool byHost = HostMatches(host, hostList, out string hostHit);
        bool byWorld = WorldNameMatches(announcedWorld, names, out string worldHit);
        if (byHost && byWorld)
            return new IltVerdict { IsIlt = true, Reason = $"Infinite Leaftide: {where} matches \"{hostHit}\" and {world} matches \"{worldHit}\"" };
        if (byHost)
            return new IltVerdict { IsIlt = true, Reason = $"Infinite Leaftide: {where} matches \"{hostHit}\" ({world})" };
        if (byWorld)
            return new IltVerdict { IsIlt = true, Reason = $"Infinite Leaftide: {world} matches \"{worldHit}\" (host {where})" };
        return new IltVerdict { Reason = $"not Infinite Leaftide: {where}, {world}" };
    }

    /// <summary>
    /// The engine's verdict, or ILT when RynthILT says so (<paramref name="pluginIlt"/>, with its
    /// <paramref name="pluginReason"/>). The engine's own "is ILT" always stands; RynthILT can only
    /// add worlds (its list, or a character's "Treat this world as ILT").
    /// </summary>
    public static IltVerdict Combine(IltVerdict engine, bool pluginIlt, string? pluginReason)
    {
        if (engine.IsIlt || !pluginIlt) return engine;
        string why = string.IsNullOrWhiteSpace(pluginReason) ? "RynthILT says so" : pluginReason.Trim();
        return new IltVerdict { IsIlt = true, Reason = $"Infinite Leaftide: {why} ({engine.Reason})" };
    }

    /// <summary>
    /// RynthILTGetStatusJson: {"loaded":bool,"ilt":bool,"forced":bool,"available":bool,"world":"..."}.
    /// Missing fields read false / "". False when the text isn't a JSON object.
    /// </summary>
    public static bool ParsePluginStatus(string? json, out bool loaded, out bool ilt, out bool forced, out bool available, out string world)
    {
        loaded = ilt = forced = available = false;
        world = "";
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
            static bool B(System.Text.Json.JsonElement e, string name)
                => e.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.True;
            loaded = B(root, "loaded");
            ilt = B(root, "ilt");
            forced = B(root, "forced");
            available = B(root, "available");
            world = root.TryGetProperty("world", out var w) && w.ValueKind == System.Text.Json.JsonValueKind.String ? w.GetString() ?? "" : "";
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>Case-insensitive match where '*' stands for any run of characters (anywhere in the pattern).</summary>
    public static bool Matches(string text, string pattern)
    {
        if (pattern.Length == 0) return false;
        if (!pattern.Contains('*')) return text.Equals(pattern, StringComparison.OrdinalIgnoreCase);

        string[] parts = pattern.Split('*');
        int pos = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (part.Length == 0) continue;
            if (i == 0)
            {
                if (!text.StartsWith(part, StringComparison.OrdinalIgnoreCase)) return false;
                pos = part.Length;
                continue;
            }
            if (i == parts.Length - 1)
                return text.Length - pos >= part.Length && text.EndsWith(part, StringComparison.OrdinalIgnoreCase);
            int at = text.IndexOf(part, pos, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return false;
            pos = at + part.Length;
        }
        return true;
    }

    /// <summary>"InfiniteLeaftide, *Leaftide*" → trimmed, de-duplicated list (case-insensitive). Null for null.</summary>
    public static List<string>? ParseList(IEnumerable<string?>? items)
    {
        if (items == null) return null;
        var list = new List<string>();
        foreach (string? raw in items)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            foreach (string piece in raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string p = piece.Trim();
                if (p.Length == 0) continue;
                bool dup = false;
                foreach (string have in list)
                    if (have.Equals(p, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                if (!dup) list.Add(p);
            }
        }
        return list;
    }

    private static bool IsIpv4(string s)
    {
        int dots = 0;
        foreach (char c in s)
        {
            if (c == '.') dots++;
            else if (c < '0' || c > '9') return false;
        }
        return dots == 3;
    }
}
