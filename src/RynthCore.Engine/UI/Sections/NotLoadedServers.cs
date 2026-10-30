// ============================================================================
//  RynthCore.Engine - UI/Sections/NotLoadedServers.cs
//  Servers whose plugin is installed but not loaded: the connect host belongs to
//  another server, so PluginManifestGate left the plugin out. They still go in
//  the server list (Options > Diagnostics, GetServerStateJson), so a server can
//  be picked there; a server forced On gets its plugin loaded
//  (PluginManager.RequestServerPluginLoad).
//
//  Pure (no AC, no engine state, no I/O): tools/SectionsTests compiles it.
// ============================================================================

using System;
using System.Collections.Generic;

namespace RynthCore.Engine.UI.Sections;

/// <summary>An installed server plugin that is not loaded: its server and the plugin's name.</summary>
internal readonly record struct NotLoadedServer(string Id, string Name, string Plugin, string[]? WorldNames = null);

internal static class NotLoadedServers
{
    /// <summary>
    /// Adds each not-loaded server whose id isn't in <paramref name="list"/> yet (a loaded plugin's
    /// declaration or a built-in server wins). Not detected (the host is another server's); active
    /// only when this character forces it On.
    /// </summary>
    public static void Append(List<ServerState> list, IReadOnlyList<NotLoadedServer> notLoaded, Func<string, ServerForce> force)
    {
        foreach (NotLoadedServer s in notLoaded)
        {
            if (s.Id.Length == 0 || list.Exists(x => x.Id.Equals(s.Id, StringComparison.OrdinalIgnoreCase))) continue;
            ServerForce f = force(s.Id);
            string reason = f == ServerForce.On
                ? $"forced On: loading {s.Plugin}"
                : $"{s.Plugin} is installed but not loaded (this host is another server's); pick it to load it";
            list.Add(new ServerState(s.Id, s.Name, ServerForceText.Apply(f, false), false, f, false, reason));
        }
    }

    /// <summary>The not-loaded servers this character forces On: their plugins should load now.</summary>
    public static List<string> ForcedOn(IReadOnlyList<NotLoadedServer> notLoaded, Func<string, ServerForce> force)
        => ToLoad(notLoaded, force, world: null);

    /// <summary>
    /// The not-loaded servers whose plugins should load now: forced On by this character, or (not
    /// forced Off) named by the world name the server announced (<paramref name="world"/>; null or
    /// empty before login). The host left them out, but the world says this is their server.
    /// </summary>
    public static List<string> ToLoad(IReadOnlyList<NotLoadedServer> notLoaded, Func<string, ServerForce> force, string? world)
    {
        var ids = new List<string>();
        foreach (NotLoadedServer s in notLoaded)
        {
            if (s.Id.Length == 0) continue;
            ServerForce f = force(s.Id);
            if (f == ServerForce.On
                || (f != ServerForce.Off && !string.IsNullOrWhiteSpace(world) && s.WorldNames is { Length: > 0 }
                    && Compatibility.IltDetect.WorldNameMatches(world, s.WorldNames, out _)))
                ids.Add(s.Id);
        }
        return ids;
    }
}
