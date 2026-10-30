using System;
using System.Collections.Generic;
using System.Linq;

namespace RynthCore.App;

/// <summary>
/// One server RynthSuite ships a server plugin for: where to connect, and which plugin
/// (C:\Games\RynthSuite\&lt;PluginName&gt;\) RynthCore loads for it.
/// </summary>
/// <param name="Id">The plugin's manifest server id (lower-case, "a-z0-9-").</param>
/// <param name="Name">The server's name, used as the profile's Server Name.</param>
/// <param name="Host">The host the launcher connects to (the client's -h).</param>
/// <param name="Port">The login port.</param>
/// <param name="Emulator">The server software.</param>
/// <param name="Ruleset">"PvE" or "PvP", as the server lists itself.</param>
/// <param name="PluginName">The RynthSuite server plugin for this server.</param>
/// <param name="Note">One line about the server, shown under the picker.</param>
internal sealed record KnownServer(
    string Id,
    string Name,
    string Host,
    int Port,
    AcEmulatorKind Emulator,
    string Ruleset,
    string PluginName,
    string Note)
{
    /// <summary>"host:port", as the server list shows it.</summary>
    public string Address => $"{Host}:{Port}";

    /// <summary>The picker's line for this server.</summary>
    public override string ToString() => $"{Name}  -  {Address}  ({Ruleset})";
}

/// <summary>
/// The servers RynthSuite has a server plugin for, so an account can be set up on one by picking
/// it. Hosts, ports and emulators come from the public server list
/// (github.com/acresources/serverslist, Servers.xml, checked 2026-10-13); Aelrynth is not on
/// that list and uses aelrynth.com:9020. A PluginName is the server plugin's folder under
/// C:\Games\RynthSuite when one is installed; the picker works the same without it. A Host
/// should stay inside that plugin's manifest "server" hosts, or RynthCore's server gate won't
/// pick the plugin for it.
/// </summary>
internal static class KnownServerCatalog
{
    /// <summary>Every known server, in the order the picker lists them (by name).</summary>
    public static IReadOnlyList<KnownServer> All { get; } = new[]
    {
        new KnownServer("achard", "AChard", "a-chard.ddns.net", 9000, AcEmulatorKind.Ace, "PvP", "RynthAChard",
            "End-of-retail PvP server with custom PK content and kill rankings."),
        new KnownServer("aelrynth", "Aelrynth", "aelrynth.com", 9020, AcEmulatorKind.Ace, "PvE", "RynthAelrynth",
            "Aelrynth (aelrynth.com), an ACE server with its own mods."),
        new KnownServer("coldeve", "Coldeve", "play.coldeve.ac", 9000, AcEmulatorKind.Ace, "PvE", "RynthColdeve",
            "PvE server evolving toward an end-of-retail experience."),
        new KnownServer("conquest", "Conquest", "acconquest.ddns.net", 9000, AcEmulatorKind.Ace, "PvE", "RynthConquest",
            "Custom PvE server with live events, deep crafting and a PK system."),
        new KnownServer("derptide", "Derptide", "ac.derptide.net", 9000, AcEmulatorKind.Ace, "PvE", "RynthDerptide",
            "End-of-retail server with custom content, upgraded quest weapons and live events."),
        new KnownServer("dreamweave", "DreamWeave", "play.acdreamweave.com", 9000, AcEmulatorKind.Ace, "PvE", "RynthDreamWeave",
            "Lore-driven PvE server: new class abilities, new crafting, an up-to-date wiki."),
        new KnownServer("eversong", "Eversong", "eversong.abrdns.com", 9000, AcEmulatorKind.Ace, "PvE", "RynthEversong",
            "Five difficulty worlds, enlightenment, banking and lockboxes, invasions."),
        new KnownServer("frostace", "FrostACE", "172.111.230.127", 9000, AcEmulatorKind.Ace, "PvE", "RynthFrostACE",
            "New accounts include level 275 starter characters; retail restoration supported."),
        new KnownServer("frostcull", "Frostcull", "frostcull.ddns.net", 9000, AcEmulatorKind.Ace, "PvE", "RynthFrostcull",
            "No level or attribute cap, 10x XP and luminance, 25x drop rate."),
        new KnownServer("ilt", "Infinite Leaftide", "game.infiniteleaftide.online", 9000, AcEmulatorKind.Ace, "PvE", "RynthILT",
            "Infinite levels and enlightenment, custom content, constant development."),
        new KnownServer("leafdawn", "LeafDawn", "leafdawn.hopto.org", 9000, AcEmulatorKind.Ace, "PvE", "RynthLeafDawn",
            "End-of-retail PvE server with some custom content."),
        new KnownServer("leafdawning", "Leafdawning", "leafdawning.duckdns.org", 9000, AcEmulatorKind.Ace, "PvE", "RynthLeafdawning",
            "No-frills end-of-retail PvE server. Enforced 3 account limit."),
        new KnownServer("levistras", "Levistras", "levistras.acportalstorm.com", 9000, AcEmulatorKind.Ace, "PvE", "RynthLevistras",
            "Botting-free PvE server (automation is not allowed). 2 account limit."),
        new KnownServer("portalstorm", "PortalStorm", "ac.portalstorm.us.com", 9020, AcEmulatorKind.Ace, "PvE", "RynthPortalStorm",
            "15 tiers, 10 enlightenments, infinite luminance augmentations, custom skills."),
        new KnownServer("reefcull", "Reefcull", "reefcull.gdleac.com", 9000, AcEmulatorKind.Gdle, "PvE", "RynthReefcull",
            "GDLE PvE server that mimics end-of-game retail."),
        new KnownServer("sundering", "Sundering", "ace.sunderingac.com", 9000, AcEmulatorKind.Ace, "PvE", "RynthSundering",
            "End-of-retail feel with convenience changes. 3 active character limit."),
        new KnownServer("thistlecrown", "Thistlecrown", "thistlecrown.ddns.net", 9000, AcEmulatorKind.Ace, "PvE", "RynthThistlecrown",
            "Recreation of Thistledown. 3 account limit."),
    };

    /// <summary>The known server with this id (case-insensitive), or null.</summary>
    public static KnownServer? FindById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        string wanted = id.Trim();
        return All.FirstOrDefault(s => string.Equals(s.Id, wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The known server a saved profile points at, by host: the same host, or one under the known
    /// host's parent domain ("infiniteleaftide.online" also finds ILT). An IP matches only itself.
    /// Null when the host is empty or not a known server.
    /// </summary>
    public static KnownServer? FindByHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return null;
        string h = host.Trim().TrimEnd('.');
        foreach (KnownServer s in All)
        {
            if (string.Equals(h, s.Host, StringComparison.OrdinalIgnoreCase))
                return s;
        }
        foreach (KnownServer s in All)
        {
            string domain = ParentDomain(s.Host);
            if (domain.Length == 0)
                continue;
            if (string.Equals(h, domain, StringComparison.OrdinalIgnoreCase) ||
                h.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))
                return s;
        }
        return null;
    }

    /// <summary>
    /// Fills a profile's connection fields from a known server: Name, Host, Port and Emulator.
    /// Id, Alias and RodatEnabled are the user's and are left alone.
    /// </summary>
    public static void ApplyTo(KnownServer server, LaunchServerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(profile);
        profile.Name = server.Name;
        profile.Host = server.Host;
        profile.Port = server.Port;
        profile.Emulator = server.Emulator;
    }

    /// <summary>
    /// The domain a host sits under, for matching a typed host to a known one:
    /// "game.infiniteleaftide.online" gives "infiniteleaftide.online". Hosts on shared dynamic-DNS
    /// domains (ddns.net, duckdns.org, ...) and IPs give "" so they match only themselves.
    /// </summary>
    private static string ParentDomain(string host)
    {
        if (System.Net.IPAddress.TryParse(host, out _))
            return "";
        string[] labels = host.Split('.');
        if (labels.Length < 3)
            return host;
        string parent = string.Join('.', labels.Skip(1));
        return SharedDomains.Contains(parent) ? "" : parent;
    }

    /// <summary>
    /// Dynamic-DNS domains several servers share; a host under one is not that server's domain.
    /// </summary>
    private static readonly HashSet<string> SharedDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "ddns.net", "duckdns.org", "hopto.org", "abrdns.com", "zapto.org", "redirectme.net",
        "dynamic-dns.net", "gdleac.com", "acportalstorm.com",
    };
}
